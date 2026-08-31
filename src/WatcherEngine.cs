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
    }        internal delegate NativeWriteOutcome VerifiedConsoleWriter(
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
        private readonly ConsoleMembershipCheck _membershipChecker;
        private bool _stateLoaded;
        private bool _stateStoreHealthy = true;
        private bool _stateLedgerAmbiguous;
        private DateTime _stateRecoveryNotBeforeUtc = DateTime.MinValue;
        // PERF-004: track durable-state mutations per RetrySessionState
        // (IsDurableDirty) to skip full rebuild when no safety-relevant
        // change happened, while keeping a bounded checkpoint cadence for
        // LastObservedUtc retention semantics.
        private DateTime _lastDurableCheckpointUtc = DateTime.MinValue;
        private Dictionary<string, TargetRule> _ruleByName;
        private bool _ruleByNameValid;
        private const int DurableCheckpointIntervalSeconds = 60;

        internal int SessionStateCount { get { return _states.Count; } }

        public WatcherEngine(WatcherConfiguration configuration, DurableStateStore stateStore = null)
            : this(
                configuration,
                ProcessDiscovery.Snapshot,
                ProcessDiscovery.ResolveSessionIdentity,
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e) { return NativeConsole.TryRead(pid, lineCount, out s, out e); },
                delegate(int pid, out IList<int> pids, out string err) { return NativeConsole.TryCheckMembership(pid, out pids, out err); },
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
            : this(configuration, snapshotProvider, sessionResolver, consoleReader, null, verifiedWriter, clock, stateStore)
        {
        }

        public WatcherEngine(
            WatcherConfiguration configuration,
            Func<IList<ProcessEntry>> snapshotProvider,
            Func<int, string, ProcessSessionIdentity> sessionResolver,
            ConsoleReadAttempt consoleReader,
            ConsoleMembershipCheck membershipChecker,
            VerifiedConsoleWriter verifiedWriter,
            Func<DateTime> clock,
            DurableStateStore stateStore = null)
        {
            _configuration = configuration;
            _snapshotProvider = snapshotProvider ?? ProcessDiscovery.Snapshot;
            _sessionResolver = sessionResolver ?? ProcessDiscovery.ResolveSessionIdentity;
            _consoleReader = consoleReader ?? (delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e) { return NativeConsole.TryRead(pid, lineCount, out s, out e); });
            _membershipChecker = membershipChecker;
            _verifiedWriter = verifiedWriter ?? NativeConsole.TryWriteLineVerified;
            _clock = clock ?? (delegate { return DateTime.UtcNow; });
            _stateStore = stateStore;
        }

        public IList<PollResult> PollOnce(bool allowInput, Func<bool> shouldStop = null)
        {
            DateTime nowUtc = _clock();
            string stateDiagnostic = null;

            // W2-003: take one snapshot early to identify vanished sessions
            // for lifecycle pruning, then reuse it for rule evaluation below
            // to avoid doubling the snapshot cost.
            ISet<int> livePids = null;
            IList<ProcessEntry> processes = null;
            bool snapshotSucceeded = false;
            try
            {
                processes = _snapshotProvider();
                snapshotSucceeded = true;
                livePids = new HashSet<int>();
                foreach (ProcessEntry entry in processes)
                {
                    livePids.Add(entry.Id);
                }
            }
            catch
            {
                // If snapshot fails, prune with null (idle-only pruning).
            }
            PruneInactiveStates(nowUtc, livePids);
            if (_stateStore != null && !_stateStoreHealthy)
            {
                // W2-006 / CORE-007: bounded, rate-limited recovery probing.
                // Stay fail-closed while the store is unhealthy, but transition
                // back to healthy automatically once writeability and durable
                // authority are reconciled again -- no manual restart required.
                TryRecoverStateStore(nowUtc, ref stateDiagnostic);
            }
            if (!_stateLoaded && _stateStore != null)
            {
                _stateLoaded = true;
                string preflightError;
                _stateStoreHealthy = _stateStore.TryPreflight(out preflightError);
                if (!_stateStoreHealthy)
                {
                    stateDiagnostic = "state_preflight_failed: " + preflightError;
                }
                List<StateRecord> saved = _stateStore.Load(nowUtc);
                InvalidateRuleCache();
                foreach (StateRecord rec in saved)
                {
                    TargetRule savedRule = rec == null ? null : LookupRule(rec.RuleName);
                    if (rec != null && savedRule != null && String.Equals(rec.RuleSemanticFingerprint, savedRule.SemanticFingerprint, StringComparison.Ordinal) && rec.ProcessId > 0 && rec.ProcessStartUtc != DateTime.MinValue && _states.Count < MaximumSessionStates)
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
                if (_stateStore.LastLoadDisposition == StateLoadDisposition.Unavailable)
                {
                    _stateStoreHealthy = false;
                }
                if (_stateStore.RequiresConservativeRecovery)
                {
                    _stateLedgerAmbiguous = true;
                    int delaySeconds = _configuration.Targets.Count == 0
                        ? 60
                        : _configuration.Targets.Max(t => t.SafeInitialDelaySeconds);
                    _stateRecoveryNotBeforeUtc = nowUtc.AddSeconds(Math.Max(60, delaySeconds));
                    stateDiagnostic = "state_" + _stateStore.LastLoadDisposition.ToString().ToLowerInvariant() + ": " + _stateStore.LastError;
                }
            }

            var results = new List<PollResult>();
            if (!snapshotSucceeded)
            {
                results.Add(new PollResult
                {
                    Target = "runtime",
                    Error = "process_discovery_unavailable",
                    Reason = "send_blocked=process_discovery_unavailable"
                });
                return results;
            }
            if (!String.IsNullOrEmpty(stateDiagnostic))
            {
                results.Add(new PollResult
                {
                    Target = "runtime",
                    Error = stateDiagnostic,
                    Reason = _stateStoreHealthy ? "state_recovery_cooldown" : "send_blocked=state_store_unavailable"
                });
            }

            var reservedConsoles = new HashSet<string>(StringComparer.Ordinal);

            // PERF-006: build one immutable index over the process snapshot
            // and reuse it for every enabled rule, avoiding redundant per-rule
            // dictionary reconstruction.
            ProcessSnapshotIndex snapshotIndex = processes != null ? new ProcessSnapshotIndex(processes) : null;

            foreach (TargetRule rule in _configuration.Targets)
            {
                if (shouldStop != null && shouldStop())
                {
                    results.Add(new PollResult { Target = "runtime", Error = "stop_requested", Reason = "send_blocked=stop_requested" });
                    break;
                }
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

                // PERF-006: use the pre-built index when available; fall back
                // to per-call construction only when the snapshot failed.
                IList<ConsoleCandidate> candidates = snapshotIndex != null
                    ? ProcessDiscovery.FindCandidates(snapshotIndex, targetNameSet, _sessionResolver)
                    : ProcessDiscovery.FindCandidates(processes, targetNameSet, _sessionResolver);

                // PERF-007: share one refreshed snapshot across all candidates
                // that fail the initial read, instead of each candidate taking
                // an independent snapshot.  Only one refresh is performed per
                // rule even when many candidates fail.
                 IList<ProcessEntry> sharedRefreshedProcesses = null;
                 ProcessSnapshotIndex sharedRefreshedIndex = null;
                 bool sharedRefreshTaken = false;

                foreach (ConsoleCandidate candidate in candidates)
                {
                    if (shouldStop != null && shouldStop())
                    {
                        results.Add(new PollResult
                        {
                            Target = "runtime",
                            Error = "stop_requested",
                            Reason = "send_blocked=stop_requested"
                        });
                        break;
                    }
                    ResolvedConsoleSession resolvedConsole;
                    string readError;
                    if (!TryReadTarget(rule, candidate, shouldStop, out resolvedConsole, out readError, ref sharedRefreshedProcesses, ref sharedRefreshedIndex, ref sharedRefreshTaken))
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

                    RuleObservation observation = RuleMatcher.Inspect(rule, resolvedConsole.Snapshot, nowUtc);
                    // CORE-005: an evaluation failure (regex timeout / rule
                    // evaluation error) must NEVER mutate semantic safety state.
                    // Short-circuit before any Observe/capacity transition so a
                    // transient failure cannot erase deadlines, attempts,
                    // suppression or an in-flight reservation.
                    if (!String.IsNullOrEmpty(observation.EvaluationError))
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
                            Error = "rule_evaluation_failed=" + observation.EvaluationError,
                            Reason = "send_blocked=rule_evaluation_failed"
                        });
                        continue;
                    }

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
                        if (_stateStore != null && (!_stateStoreHealthy || _stateLedgerAmbiguous))
                        {
                            result.Error = _stateLedgerAmbiguous ? "state_ledger_ambiguous" : "state_store_unavailable";
                            result.Reason = _stateLedgerAmbiguous ? "send_blocked=state_ledger_ambiguous" : "send_blocked=state_store_unavailable";
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
                            ProcessSnapshotIndex freshIndex = new ProcessSnapshotIndex(freshProcesses);
                            IList<ConsoleCandidate> freshCandidates = ProcessDiscovery.FindCandidates(freshIndex, targetNameSet, _sessionResolver);
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
                                 IList<ProcessEntry> preSendRefreshed = null;
                                 ProcessSnapshotIndex preSendRefreshedIndex = null;
                                 bool preSendRefreshTaken = false;
                                 if (!TryReadTarget(rule, freshTarget, shouldStop, out freshResolved, out freshError, ref preSendRefreshed, ref preSendRefreshedIndex, ref preSendRefreshTaken))
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
                                        // CORE-005: do not RecordAttempt (which mutates
                                        // attempts/deadline/state) on evaluation failure;
                                        // preserve all authorization/recovery fields.
                                        result.Error = "rule_evaluation_failed=" + safety.EvaluationError;
                                        result.Reason = "send_blocked=rule_evaluation_failed";
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
                                        if (reservedConsoles.Contains(resolvedConsole.StableConsoleId))
                                        {
                                            result.Reason = "send_blocked=console_already_attempted_this_poll";
                                            results.Add(result);
                                            continue;
                                        }
                                        reservedConsoles.Add(resolvedConsole.StableConsoleId);
                                        RetrySessionState preReservationSnapshot = state.Clone();
                                        state.ReserveAttempt(safety.TriggerToken, rule, nowUtc);
                                        if (_stateStore != null)
                                        {
                                            bool preChanged;
                                            string preError;
                                            List<StateRecord> preRecords = ExportAllStates(nowUtc);
                                            if (!_stateStore.TrySave(preRecords, nowUtc, out preChanged, out preError))
                                            {
                                                state.RestoreFromClone(preReservationSnapshot);
                                                _stateStoreHealthy = false;
                                                result.Error = "state_write_failed: " + preError;
                                                result.Reason = "send_blocked=state_store_unavailable";
                                                results.Add(result);
                                                continue;
                                            }
                                        }
                                        string writeError;
                                        NativeWriteOutcome writeOutcome = _verifiedWriter(freshResolved, freshTarget.MatchedSession, rule.Command, out writeError);
                                        result.Sent = writeOutcome == NativeWriteOutcome.CompleteInputCommitted;
                                        result.Error = writeError;
                                        if (writeOutcome == NativeWriteOutcome.CompleteInputCommitted)
                                        {
                                            result.Reason = "send=command_written";
                                        }
                                        else if (writeOutcome == NativeWriteOutcome.AmbiguousOrPartialInput)
                                        {
                                            result.Reason = "send_blocked=ambiguous_partial_write: " + writeError;
                                        }
                                        else
                                        {
                                            result.Reason = writeError ?? "send_blocked=input_write_failed";
                                        }
                                        // CORE-004: refine the reserved state to its terminal outcome
                                        // AFTER the writer returns. The post-poll export at line ~355
                                        // will durably persist the final state.
                                        state.CommitAttempt(writeOutcome, rule, nowUtc);
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
                // PERF-004: only export and persist when at least one session
                // reports a safety-relevant mutation since the last successful
                // save, or when the bounded checkpoint interval for
                // LastObservedUtc retention has elapsed.
                bool anyDirty = false;
                foreach (var pair in _states)
                {
                    if (pair.Value != null && pair.Value.IsDurableDirty)
                    {
                        anyDirty = true;
                        break;
                    }
                }
                bool checkpointDue = _lastDurableCheckpointUtc == DateTime.MinValue
                    || (nowUtc - _lastDurableCheckpointUtc).TotalSeconds >= DurableCheckpointIntervalSeconds;
                if (anyDirty || checkpointDue)
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
                    else
                    {
                        if (changed)
                        {
                            _lastDurableCheckpointUtc = nowUtc;
                        }
                        foreach (var pair in _states)
                        {
                            if (pair.Value != null) pair.Value.ClearDurableDirty();
                        }
                    }
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

        private bool TryRecoverStateStore(DateTime nowUtc, ref string stateDiagnostic)
        {
            if (_stateStore == null || _stateStoreHealthy)
            {
                return true;
            }

            if (nowUtc < _stateRecoveryNotBeforeUtc)
            {
                stateDiagnostic = "state_store_unavailable (recovery probe rate-limited)";
                return false;
            }

            string preflightError;
            if (!_stateStore.TryPreflight(out preflightError))
            {
                // Persistent failure: stay fail-closed, reprobe later.
                _stateRecoveryNotBeforeUtc = nowUtc.AddSeconds(30);
                stateDiagnostic = "state_preflight_failed: " + preflightError;
                return false;
            }

            // Reconcile durable authority: reload what survived on disk and
            // restore those records so cooldown/suppression authority is not
            // lost across the outage.
            List<StateRecord> saved = _stateStore.Load(nowUtc);
            if (_stateStore.LastLoadDisposition == StateLoadDisposition.Unavailable)
            {
                _stateRecoveryNotBeforeUtc = nowUtc.AddSeconds(30);
                stateDiagnostic = "state_unavailable: " + _stateStore.LastError;
                return false;
            }
            if (_stateStore.LastLoadDisposition == StateLoadDisposition.Corrupt || _stateStore.LastLoadDisposition == StateLoadDisposition.UnsupportedSchema)
            {
                _stateLedgerAmbiguous = true;
            }
            foreach (StateRecord rec in saved)
            {
                if (rec != null && !String.IsNullOrEmpty(rec.RuleName) && rec.ProcessId > 0 && rec.ProcessStartUtc != DateTime.MinValue && _states.Count < MaximumSessionStates)
                {
                    TargetRule savedRule = _configuration.Targets.FirstOrDefault(t => String.Equals(t.Name, rec.RuleName, StringComparison.Ordinal));
                    if (savedRule == null || !String.Equals(rec.RuleSemanticFingerprint, savedRule.SemanticFingerprint, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    RetrySessionState existing;
                    if (_states.TryGetValue(rec.CompositeKey, out existing))
                    {
                        continue;
                    }
                    var restored = new RetrySessionState();
                    restored.RestoreFrom(rec, nowUtc);
                    _states[rec.CompositeKey] = restored;
                    _stateIdentities[rec.CompositeKey] = new ProcessSessionIdentity
                    {
                        ProcessId = rec.ProcessId,
                        ProcessName = String.Empty,
                        StartTimeUtc = rec.ProcessStartUtc
                    };
                }
            }

            _stateStoreHealthy = true;
            _stateRecoveryNotBeforeUtc = DateTime.MinValue;
            return true;
        }

        private bool TryReadTarget(
            TargetRule rule,
            ConsoleCandidate candidate,
            Func<bool> shouldStop,
            out ResolvedConsoleSession session,
            out string error,
             ref IList<ProcessEntry> sharedRefreshedProcesses,
             ref ProcessSnapshotIndex sharedRefreshedIndex,
             ref bool sharedRefreshTaken)
        {
            session = null;
            error = null;
            ConsoleReadAttempt readAttempt = delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
            {
                return _consoleReader(pid, lineCount, out s, out e);
            };

            if (ProcessDiscovery.TryResolveConsoleSession(candidate, readAttempt, rule.ScanLines, _membershipChecker, out session, out error))
            {
                return true;
            }

            string firstError = error;
            if (shouldStop != null && shouldStop())
            {
                error = "send_blocked=stop_requested";
                return false;
            }

            // PERF-007: reuse one shared refreshed snapshot across all
            // candidates that fail the initial read, rather than each
            // candidate independently calling _snapshotProvider().
            if (!sharedRefreshTaken)
            {
                sharedRefreshedProcesses = _snapshotProvider();
                sharedRefreshedIndex = new ProcessSnapshotIndex(sharedRefreshedProcesses);
                sharedRefreshTaken = true;
            }
            ProcessSnapshotIndex freshIndex = sharedRefreshedIndex;
            IDictionary<int, ProcessEntry> freshById = freshIndex.ById;

            ProcessEntry freshMatched;
            if (!freshById.TryGetValue(candidate.MatchedProcessId, out freshMatched))
            {
                error = "process disappeared between snapshot and attach (PID " + candidate.MatchedProcessId + "): " + firstError;
                return false;
            }

            ProcessSessionIdentity freshSession;
            if (!freshIndex.SessionIdentities.TryGetValue(freshMatched.Id, out freshSession))
            {
                freshSession = _sessionResolver(freshMatched.Id, freshMatched.Name);
                freshIndex.SessionIdentities[freshMatched.Id] = freshSession;
            }
            if (!freshSession.Equals(candidate.MatchedSession))
            {
                error = "process session changed for PID " + candidate.MatchedProcessId;
                return false;
            }

            var freshCandidate = new ConsoleCandidate
            {
                MatchedSession = freshSession,
                ParentProcessId = freshMatched.ParentId,
                AttachProcessIds = ProcessDiscovery.BuildAttachCandidates(freshMatched, freshById, freshIndex.ChildrenByParentId)
            };

            if (ProcessDiscovery.TryResolveConsoleSession(freshCandidate, readAttempt, rule.ScanLines, _membershipChecker, out session, out error))
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

        // W2-003: session lifecycle management. Retire sessions whose
        // process has vanished from the snapshot after a conservative grace
        // period. IdleNoEvent records older than 24h are always retired.
        // Active/error records for vanished sessions get a shorter grace to
        // prevent permanent capacity exhaustion.
        private void PruneInactiveStates(DateTime nowUtc, ISet<int> livePids = null)
        {
            if (_states.Count < 100)
            {
                return;
            }

            var expiredKeys = new List<string>();
            TimeSpan maxAge = TimeSpan.FromHours(24);
            TimeSpan vanishedGrace = TimeSpan.FromMinutes(30);
            foreach (var pair in _states)
            {
                if (pair.Value == null) continue;

                // Always prune stale IdleNoEvent entries.
                if (pair.Value.State == RecoveryState.IdleNoEvent && nowUtc - pair.Value.LastObservedUtc > maxAge)
                {
                    expiredKeys.Add(pair.Key);
                    continue;
                }

                // W2-003: if the session's PID has vanished from the live
                // snapshot, retire it conservatively. Ambiguous/post-write
                // states keep a longer grace; idle/backoff states retire sooner
                // to free capacity for new sessions.
                if (livePids != null)
                {
                    ProcessSessionIdentity identity;
                    if (_stateIdentities.TryGetValue(pair.Key, out identity) && identity != null && identity.IsStrong)
                    {
                        if (!livePids.Contains(identity.ProcessId))
                        {
                            bool isCritical = pair.Value.State == RecoveryState.AttemptInFlightReserved ||
                                pair.Value.State == RecoveryState.AmbiguousFailClosed ||
                                pair.Value.State == RecoveryState.CommandWrittenAwaitingOutcome;
                            TimeSpan grace = isCritical ? vanishedGrace : TimeSpan.FromMinutes(15);
                            if (nowUtc - pair.Value.LastObservedUtc > grace)
                            {
                                expiredKeys.Add(pair.Key);
                            }
                        }
                    }
                }
            }

            for (int i = 0; i < expiredKeys.Count; i++)
            {
                _states.Remove(expiredKeys[i]);
                _stateIdentities.Remove(expiredKeys[i]);
            }
        }

        // W2-004: only evict states that are truly safe to discard.
        // RecoveryConfirmed states carry _suppressedToken for anti-replay;
        // states in AmbiguousFailClosed or AttemptInFlightReserved must not
        // be evicted because discarding them can authorize a duplicate write.
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
                if (pair.Value == null) continue;
                if (pair.Value.Active) continue;
                // W2-004: states with unresolved suppression or post-write
                // ambiguity are not safe to evict.
                if (!String.IsNullOrEmpty(pair.Value.SuppressedToken)) continue;
                if (pair.Value.State == RecoveryState.AmbiguousFailClosed) continue;
                if (pair.Value.State == RecoveryState.AttemptInFlightReserved) continue;
                if (pair.Value.LastObservedUtc < oldestSeen)
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
                TargetRule rule = LookupRule(ruleName);
                records.Add(pair.Value.Export(ruleName, rule, identity, nowUtc));
            }
            return records;
        }

        private TargetRule LookupRule(string ruleName)
        {
            if (String.IsNullOrEmpty(ruleName)) return null;
            if (_ruleByName == null || !_ruleByNameValid)
            {
                _ruleByName = new Dictionary<string, TargetRule>(StringComparer.Ordinal);
                if (_configuration != null && _configuration.Targets != null)
                {
                    foreach (TargetRule rule in _configuration.Targets)
                    {
                        if (rule == null || String.IsNullOrEmpty(rule.Name)) continue;
                        _ruleByName[rule.Name] = rule;
                    }
                }
                _ruleByNameValid = true;
            }
            TargetRule found;
            return _ruleByName.TryGetValue(ruleName, out found) ? found : null;
        }

        private void InvalidateRuleCache() { _ruleByNameValid = false; }

        public void Run(bool allowInput, Action<PollResult> onResult, Func<bool> shouldStop)
        {
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
