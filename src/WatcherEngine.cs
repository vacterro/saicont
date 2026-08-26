using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace SaiCont
{
    internal sealed class PollResult
    {
        public string Target;
        public int ProcessId;
        public int ParentProcessId;
        public string ProcessName;
        public int AttachProcessId;
        public string AttachChain;
        public string ConsolePids;
        public IntPtr ConsoleWindow;
        public string Title;
        public bool Read;
        public bool Triggered;
        public bool Ready;
        public bool Busy;
        public bool WouldSend;
        public bool Sent;
        public string Reason;
        public string Error;
        public string TriggerToken;
        public DateTime NextAttemptUtc;
    }

    internal delegate bool VerifiedConsoleWriter(
        ResolvedConsoleSession session,
        ProcessSessionIdentity expectedTarget,
        string command,
        out string error);

    internal sealed class WatcherEngine
    {
        private const int MaximumSessionStates = 128;
        private readonly WatcherConfiguration _configuration;
        private readonly Dictionary<string, RetrySessionState> _states = new Dictionary<string, RetrySessionState>(StringComparer.Ordinal);
        private readonly Dictionary<string, ProcessSessionIdentity> _stateIdentities = new Dictionary<string, ProcessSessionIdentity>(StringComparer.Ordinal);
        private readonly Func<IList<ProcessEntry>> _snapshotProvider;
        private readonly Func<int, string, ProcessSessionIdentity> _sessionResolver;
        private readonly ConsoleReadAttempt _consoleReader;
        private readonly VerifiedConsoleWriter _verifiedWriter;
        private readonly Func<DateTime> _clock;
        private readonly DurableStateStore _stateStore;
        private bool _stateLoaded;
        private bool _stateStoreHealthy = true;
        private DateTime _stateRecoveryNotBeforeUtc = DateTime.MinValue;

        internal int SessionStateCount { get { return _states.Count; } }

        public WatcherEngine(WatcherConfiguration configuration, DurableStateStore stateStore = null)
            : this(
                configuration,
                ProcessDiscovery.Snapshot,
                ProcessDiscovery.ResolveSessionIdentity,
                delegate(int pid, out ConsoleSnapshot s, out string e) { return NativeConsole.TryRead(pid, 180, out s, out e); },
                NativeConsole.TryWriteLineVerified,
                delegate { return DateTime.UtcNow; },
                stateStore)
        {
        }

        public WatcherEngine(
            WatcherConfiguration configuration,
            Func<IList<ProcessEntry>> snapshotProvider,
            Func<int, string, ProcessSessionIdentity> sessionResolver,
            ConsoleReadAttempt consoleReader,
            VerifiedConsoleWriter verifiedWriter,
            Func<DateTime> clock,
            DurableStateStore stateStore = null)
        {
            _configuration = configuration;
            _snapshotProvider = snapshotProvider ?? ProcessDiscovery.Snapshot;
            _sessionResolver = sessionResolver ?? ProcessDiscovery.ResolveSessionIdentity;
            _consoleReader = consoleReader ?? (delegate(int pid, out ConsoleSnapshot s, out string e) { return NativeConsole.TryRead(pid, 180, out s, out e); });
            _verifiedWriter = verifiedWriter ?? NativeConsole.TryWriteLineVerified;
            _clock = clock ?? (delegate { return DateTime.UtcNow; });
            _stateStore = stateStore;
        }

        public IList<PollResult> PollOnce(bool allowInput, Func<bool> shouldStop = null)
        {
            DateTime nowUtc = _clock();
            string stateDiagnostic = null;
            PruneInactiveStates(nowUtc);
            if (!_stateLoaded && _stateStore != null)
            {
                _stateLoaded = true;
                string preflightError;
                _stateStoreHealthy = _stateStore.TryPreflight(out preflightError);
                if (!_stateStoreHealthy)
                {
                    stateDiagnostic = "state_preflight_failed: " + preflightError;
                }
                List<StateRecord> saved = _stateStore.Load();
                foreach (StateRecord rec in saved)
                {
                    if (rec != null && !String.IsNullOrEmpty(rec.RuleName) && rec.ProcessId > 0 && rec.ProcessStartUtc != DateTime.MinValue && _states.Count < MaximumSessionStates)
                    {
                        var s = new RetrySessionState();
                        s.RestoreFrom(rec, nowUtc);
                        _states[rec.CompositeKey] = s;
                        _stateIdentities[rec.CompositeKey] = new ProcessSessionIdentity
                        {
                            ProcessId = rec.ProcessId,
                            ProcessName = String.Empty,
                            StartTimeUtc = rec.ProcessStartUtc
                        };
                    }
                }
                if (_stateStore.RequiresConservativeRecovery)
                {
                    int delaySeconds = _configuration.Targets.Count == 0
                        ? 60
                        : _configuration.Targets.Max(t => t.SafeInitialDelaySeconds);
                    _stateRecoveryNotBeforeUtc = nowUtc.AddSeconds(Math.Max(60, delaySeconds));
                    stateDiagnostic = "state_" + _stateStore.LastLoadDisposition.ToString().ToLowerInvariant() + ": " + _stateStore.LastError;
                }
            }

            IList<ProcessEntry> processes = _snapshotProvider();
            var results = new List<PollResult>();
            if (!String.IsNullOrEmpty(stateDiagnostic))
            {
                results.Add(new PollResult
                {
                    Target = "runtime",
                    Error = stateDiagnostic,
                    Reason = _stateStoreHealthy ? "state_recovery_cooldown" : "send_blocked=state_store_unavailable"
                });
            }

            foreach (TargetRule rule in _configuration.Targets)
            {
                if (!rule.Enabled)
                {
                    continue;
                }

                ISet<string> targetNameSet = rule.ProcessNameSet;
                if (targetNameSet == null || targetNameSet.Count == 0)
                {
                    rule.CompileRegexes();
                    targetNameSet = rule.ProcessNameSet;
                }

                IList<ConsoleCandidate> candidates = ProcessDiscovery.FindCandidates(processes, targetNameSet, _sessionResolver);
                var usedConsoles = new HashSet<string>(StringComparer.Ordinal);

                foreach (ConsoleCandidate candidate in candidates)
                {
                    ResolvedConsoleSession resolvedConsole;
                    string readError;
                    if (!TryReadTarget(rule, candidate, out resolvedConsole, out readError))
                    {
                        if (IsNonConsoleDiscoveryFailure(readError))
                        {
                            continue;
                        }

                        results.Add(new PollResult
                        {
                            Target = rule.Name,
                            ProcessId = candidate.MatchedProcessId,
                            ParentProcessId = candidate.ParentProcessId,
                            ProcessName = candidate.MatchedProcessName,
                            AttachProcessId = candidate.PrimaryAttachProcessId,
                            AttachChain = FormatChain(candidate.AttachProcessIds),
                            Error = readError,
                            Reason = "console unavailable"
                        });
                        continue;
                    }

                    if (!usedConsoles.Add(resolvedConsole.StableConsoleId))
                    {
                        continue;
                    }

                    RuleObservation observation = RuleMatcher.Inspect(rule, resolvedConsole.Snapshot, nowUtc);
                    string stateKey = rule.Name + ":" + candidate.MatchedSession.ProcessId + ":" + (candidate.MatchedSession.StartTimeUtc == DateTime.MinValue ? "0" : candidate.MatchedSession.StartTimeUtc.ToString("o", CultureInfo.InvariantCulture));
                    RetrySessionState state;
                    if (!_states.TryGetValue(stateKey, out state))
                    {
                        if (_states.Count >= MaximumSessionStates && !TryMakeStateCapacity())
                        {
                            results.Add(new PollResult
                            {
                                Target = rule.Name,
                                ProcessId = candidate.MatchedProcessId,
                                ParentProcessId = candidate.ParentProcessId,
                                ProcessName = candidate.MatchedProcessName,
                                AttachProcessId = resolvedConsole.ResolvedAttachProcessId,
                                AttachChain = FormatChain(candidate.AttachProcessIds),
                                ConsolePids = FormatChain(resolvedConsole.ConsoleProcessIds),
                                ConsoleWindow = resolvedConsole.WindowHandle,
                                Title = resolvedConsole.Snapshot != null ? resolvedConsole.Snapshot.Title : String.Empty,
                                Read = true,
                                Error = "state_capacity_exhausted",
                                Reason = "send_blocked=state_capacity_exhausted"
                            });
                            continue;
                        }
                        state = new RetrySessionState();
                        _states[stateKey] = state;
                    }
                    if (candidate.MatchedSession != null && candidate.MatchedSession.IsStrong)
                    {
                        _stateIdentities[stateKey] = candidate.MatchedSession;
                    }

                    RetryDecision decision = state.Observe(observation, rule, nowUtc);
                    var result = new PollResult
                    {
                        Target = rule.Name,
                        ProcessId = candidate.MatchedProcessId,
                        ParentProcessId = candidate.ParentProcessId,
                        ProcessName = candidate.MatchedProcessName,
                        AttachProcessId = resolvedConsole.ResolvedAttachProcessId,
                        AttachChain = FormatChain(candidate.AttachProcessIds),
                        ConsolePids = FormatChain(resolvedConsole.ConsoleProcessIds),
                        ConsoleWindow = resolvedConsole.WindowHandle,
                        Title = resolvedConsole.Snapshot != null ? resolvedConsole.Snapshot.Title : String.Empty,
                        Read = true,
                        Triggered = observation.Triggered,
                        Ready = observation.Ready,
                        Busy = observation.Busy,
                        WouldSend = decision.Send,
                        Reason = decision.Reason,
                        TriggerToken = observation.TriggerToken,
                        NextAttemptUtc = decision.NextAttemptUtc
                    };

                    if (!String.IsNullOrEmpty(observation.EvaluationError))
                    {
                        result.Error = "rule_evaluation_failed=" + observation.EvaluationError;
                        result.Reason = "send_blocked=rule_evaluation_failed";
                    }

                    if (decision.Send && allowInput)
                    {
                        if (_stateStore != null && !_stateStoreHealthy)
                        {
                            result.Error = "state_store_unavailable";
                            result.Reason = "send_blocked=state_store_unavailable";
                            state.RecordAttempt(false, decision.TriggerToken, rule, nowUtc);
                        }
                        else if (_stateRecoveryNotBeforeUtc != DateTime.MinValue && nowUtc < _stateRecoveryNotBeforeUtc)
                        {
                            result.Reason = "send_blocked=state_recovery_cooldown";
                            state.RecordAttempt(false, decision.TriggerToken, rule, nowUtc);
                        }
                        else if (candidate.MatchedSession == null || !candidate.MatchedSession.IsStrong)
                        {
                            result.Reason = "send_blocked=target_identity_unavailable";
                            state.RecordAttempt(false, decision.TriggerToken, rule, nowUtc);
                        }
                        else
                        {
                            // Transactional pre-send re-resolution
                            IList<ProcessEntry> freshProcesses = _snapshotProvider();
                            IList<ConsoleCandidate> freshCandidates = ProcessDiscovery.FindCandidates(freshProcesses, targetNameSet, _sessionResolver);
                            ConsoleCandidate freshTarget = null;
                            foreach (ConsoleCandidate fc in freshCandidates)
                            {
                                if (fc.MatchedSession != null && fc.MatchedSession.IsStrong && fc.MatchedSession.Equals(candidate.MatchedSession))
                                {
                                    freshTarget = fc;
                                    break;
                                }
                            }

                            if (freshTarget == null)
                            {
                                bool pidExists = freshProcesses.Any(p => p.Id == candidate.MatchedProcessId);
                                result.Reason = pidExists ? "send_blocked=process_session_changed" : "send_blocked=target_disappeared";
                                state.RecordAttempt(false, decision.TriggerToken, rule, nowUtc);
                            }
                            else
                            {
                                ResolvedConsoleSession freshResolved;
                                string freshError;
                                if (!TryReadTarget(rule, freshTarget, out freshResolved, out freshError))
                                {
                                    result.Error = freshError;
                                    result.Reason = "send_blocked=re-resolution_failed: " + freshError;
                                    state.RecordAttempt(false, decision.TriggerToken, rule, nowUtc);
                                }
                                else if (!String.Equals(freshResolved.StableConsoleId, resolvedConsole.StableConsoleId, StringComparison.Ordinal))
                                {
                                    result.Reason = "send_blocked=console_changed";
                                    state.RecordAttempt(false, decision.TriggerToken, rule, nowUtc);
                                }
                                else if (!ProcessDiscovery.ConsoleServesMatchedProcess(freshResolved.ConsoleProcessIds, freshTarget.MatchedProcessId))
                                {
                                    result.Reason = "send_blocked=target_not_in_console";
                                    state.RecordAttempt(false, decision.TriggerToken, rule, nowUtc);
                                }
                                else
                                {
                                    RuleObservation safety = RuleMatcher.Inspect(rule, freshResolved.Snapshot, nowUtc);
                                    if (!String.IsNullOrEmpty(safety.EvaluationError))
                                    {
                                        result.Error = "rule_evaluation_failed=" + safety.EvaluationError;
                                        result.Reason = "send_blocked=rule_evaluation_failed";
                                        state.RecordAttempt(false, decision.TriggerToken, rule, nowUtc);
                                    }
                                    else if (!safety.Triggered || !String.Equals(safety.TriggerToken, decision.TriggerToken, StringComparison.Ordinal))
                                    {
                                        result.Reason = "send_blocked=event_changed";
                                        state.RecordAttempt(false, decision.TriggerToken, rule, nowUtc);
                                    }
                                    else if (!safety.Ready)
                                    {
                                        result.Reason = "send_blocked=prompt_not_ready";
                                        state.RecordAttempt(false, decision.TriggerToken, rule, nowUtc);
                                    }
                                    else if (safety.Busy)
                                    {
                                        result.Reason = "send_blocked=target_busy";
                                        state.RecordAttempt(false, decision.TriggerToken, rule, nowUtc);
                                    }
                                    else if (shouldStop != null && shouldStop())
                                    {
                                        result.Reason = "send_blocked=stop_requested";
                                        state.RecordAttempt(false, safety.TriggerToken, rule, nowUtc);
                                    }
                                    else
                                    {
                                        string writeError;
                                        result.Sent = _verifiedWriter(freshResolved, freshTarget.MatchedSession, rule.Command, out writeError);
                                        result.Error = writeError;
                                        result.Reason = result.Sent ? "send=command_written" : "send_blocked=" + (writeError ?? "input_write_failed");
                                        state.RecordAttempt(result.Sent, true, safety.TriggerToken, rule, nowUtc);
                                    }
                                }
                            }
                        }
                    }

                    results.Add(result);
                }
            }

            if (allowInput && _stateStore != null && _stateStoreHealthy)
            {
                List<StateRecord> exportedRecords = ExportAllStates(nowUtc);
                bool changed;
                string saveError;
                if (!_stateStore.TrySave(exportedRecords, nowUtc, out changed, out saveError))
                {
                    _stateStoreHealthy = false;
                    results.Add(new PollResult
                    {
                        Target = "runtime",
                        Error = "state_write_failed: " + saveError,
                        Reason = "send_blocked=state_store_unavailable"
                    });
                }
            }

            return results;
        }

        internal static bool IsNonConsoleDiscoveryFailure(string error)
        {
            return !String.IsNullOrEmpty(error) &&
                error.StartsWith("AttachConsole failed", StringComparison.Ordinal) &&
                error.EndsWith("(6)", StringComparison.Ordinal);
        }

        private bool TryReadTarget(
            TargetRule rule,
            ConsoleCandidate candidate,
            out ResolvedConsoleSession session,
            out string error)
        {
            session = null;
            error = null;
            ConsoleReadAttempt readAttempt = delegate(int pid, out ConsoleSnapshot s, out string e)
            {
                return _consoleReader(pid, out s, out e);
            };

            if (ProcessDiscovery.TryResolveConsoleSession(candidate, readAttempt, out session, out error))
            {
                return true;
            }

            string firstError = error;

            IList<ProcessEntry> fresh = _snapshotProvider();
            var freshById = new Dictionary<int, ProcessEntry>();
            foreach (ProcessEntry entry in fresh)
            {
                freshById[entry.Id] = entry;
            }

            ProcessEntry freshMatched;
            if (!freshById.TryGetValue(candidate.MatchedProcessId, out freshMatched))
            {
                error = "process disappeared between snapshot and attach (PID " + candidate.MatchedProcessId + "): " + firstError;
                return false;
            }

            ProcessSessionIdentity freshSession = _sessionResolver(freshMatched.Id, freshMatched.Name);
            if (!freshSession.Equals(candidate.MatchedSession))
            {
                error = "process session changed for PID " + candidate.MatchedProcessId;
                return false;
            }

            IList<int> freshIds = ProcessDiscovery.BuildAttachCandidates(freshMatched, freshById);
            var freshCandidate = new ConsoleCandidate
            {
                MatchedSession = freshSession,
                ParentProcessId = freshMatched.ParentId,
                AttachProcessIds = freshIds
            };

            if (ProcessDiscovery.TryResolveConsoleSession(freshCandidate, readAttempt, out session, out error))
            {
                return true;
            }

            error = error ?? firstError;
            return false;
        }

        private static string FormatChain(IList<int> values)
        {
            if (values == null || values.Count == 0)
            {
                return "-";
            }

            var parts = new string[values.Count];
            for (int index = 0; index < values.Count; index++)
            {
                parts[index] = values[index].ToString(CultureInfo.InvariantCulture);
            }
            return String.Join(",", parts);
        }

        private void PruneInactiveStates(DateTime nowUtc)
        {
            if (_states.Count < 100)
            {
                return;
            }

            var expiredKeys = new List<string>();
            TimeSpan maxAge = TimeSpan.FromHours(24);
            foreach (var pair in _states)
            {
                if (pair.Value != null && pair.Value.State == RecoveryState.IdleNoEvent && nowUtc - pair.Value.LastObservedUtc > maxAge)
                {
                    expiredKeys.Add(pair.Key);
                }
            }

            for (int i = 0; i < expiredKeys.Count; i++)
            {
                _states.Remove(expiredKeys[i]);
                _stateIdentities.Remove(expiredKeys[i]);
            }
        }

        private bool TryMakeStateCapacity()
        {
            if (_states.Count < MaximumSessionStates)
            {
                return true;
            }

            string oldestKey = null;
            DateTime oldestSeen = DateTime.MaxValue;
            foreach (var pair in _states)
            {
                if (pair.Value != null && !pair.Value.Active && pair.Value.LastObservedUtc < oldestSeen)
                {
                    oldestKey = pair.Key;
                    oldestSeen = pair.Value.LastObservedUtc;
                }
            }
            if (oldestKey == null)
            {
                return false;
            }
            _states.Remove(oldestKey);
            _stateIdentities.Remove(oldestKey);
            return true;
        }

        private List<StateRecord> ExportAllStates(DateTime nowUtc)
        {
            var records = new List<StateRecord>();
            foreach (var pair in _states)
            {
                ProcessSessionIdentity identity;
                if (pair.Value == null || !_stateIdentities.TryGetValue(pair.Key, out identity) || identity == null || !identity.IsStrong)
                {
                    continue;
                }

                int separator = pair.Key.IndexOf(':');
                string ruleName = separator > 0 ? pair.Key.Substring(0, separator) : String.Empty;
                records.Add(pair.Value.Export(ruleName, identity, nowUtc));
            }
            return records;
        }

        public void Run(bool allowInput, Action<PollResult> onResult, Func<bool> shouldStop)
        {
            NativeConsole.Detach();
            while (shouldStop == null || !shouldStop())
            {
                IList<PollResult> results;
                try
                {
                    results = PollOnce(allowInput, shouldStop);
                }
                catch (Exception exception)
                {
                    results = new[]
                    {
                        new PollResult
                        {
                            Target = "runtime",
                            Error = exception.Message,
                            Reason = "poll failed"
                        }
                    };
                }

                if (onResult != null)
                {
                    foreach (PollResult result in results)
                    {
                        onResult(result);
                    }
                }

                WaitForNextPoll(Math.Max(250, _configuration.PollIntervalMilliseconds), shouldStop);
            }
        }

        private static void WaitForNextPoll(int milliseconds, Func<bool> shouldStop)
        {
            int remaining = milliseconds;
            while (remaining > 0 && (shouldStop == null || !shouldStop()))
            {
                int delay = Math.Min(100, remaining);
                Thread.Sleep(delay);
                remaining -= delay;
            }
        }
    }
}
