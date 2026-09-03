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
        public string FailureClass;
        public string FailureAction;
        public bool FailureRetryable;
        public string FailureSeverity;
        public string FailureMatchedBy;
        public string DecisionCode { get { return PollDecisionCode.From(this); } }
    }        internal delegate NativeWriteOutcome VerifiedConsoleWriter(
        ResolvedConsoleSession session,
        ProcessSessionIdentity expectedTarget,
        string command,
        out string error);

    // T-141: stable, bounded machine codes. Human Reason/Error text may carry
    // diagnostics, paths, or provider prose; none of that may leak into the
    // health contract as an accidental API.
    internal static class PollDecisionCode
    {
        public static string From(PollResult result)
        {
            if (result == null) return "no_result";
            string reason = result.Reason ?? String.Empty;
            if (result.Sent || reason == "send=command_written") return "command_written";
            if (reason.StartsWith("send_blocked=process_discovery_unavailable", StringComparison.Ordinal)) return "process_discovery_unavailable";
            if (reason.StartsWith("send_blocked=state_store_unavailable", StringComparison.Ordinal)) return "state_store_unavailable";
            if (reason.StartsWith("send_blocked=state_ledger_ambiguous", StringComparison.Ordinal)) return "state_ledger_ambiguous";
            if (reason.StartsWith("send_blocked=state_capacity_exhausted", StringComparison.Ordinal)) return "state_capacity_exhausted";
            if (reason.StartsWith("send_blocked=state_recovery_cooldown", StringComparison.Ordinal) || reason == "state_recovery_cooldown") return "state_recovery_cooldown";
            if (reason.StartsWith("send_blocked=rule_evaluation_failed", StringComparison.Ordinal)) return "rule_evaluation_failed";
            if (reason.StartsWith("send_blocked=authorization_revoked_mid_poll", StringComparison.Ordinal)) return "authorization_revoked";
            if (reason.StartsWith("send_blocked=target_identity_unavailable", StringComparison.Ordinal)) return "target_identity_unavailable";
            if (reason.StartsWith("send_blocked=process_session_changed", StringComparison.Ordinal)) return "process_session_changed";
            if (reason.StartsWith("send_blocked=target_disappeared", StringComparison.Ordinal)) return "target_disappeared";
            if (reason.StartsWith("send_blocked=re-resolution_failed", StringComparison.Ordinal)) return "reresolution_failed";
            if (reason.StartsWith("send_blocked=console_changed", StringComparison.Ordinal)) return "console_changed";
            if (reason.StartsWith("send_blocked=target_not_in_console", StringComparison.Ordinal)) return "target_not_in_console";
            if (reason.StartsWith("send_blocked=event_changed", StringComparison.Ordinal)) return "event_changed";
            if (reason.StartsWith("send_blocked=prompt_not_ready", StringComparison.Ordinal)) return "prompt_not_ready";
            if (reason.StartsWith("send_blocked=target_busy", StringComparison.Ordinal)) return "target_busy";
            if (reason.StartsWith("send_blocked=stop_requested", StringComparison.Ordinal)) return "stop_requested";
            if (reason.StartsWith("send_blocked=console_already_attempted_this_poll", StringComparison.Ordinal)) return "console_already_attempted";
            if (reason.StartsWith("send_blocked=console_unavailable_backoff", StringComparison.Ordinal)) return "console_unavailable_backoff";
            if (reason.StartsWith("send_blocked=transport_unavailable_backoff", StringComparison.Ordinal)) return "transport_unavailable_backoff";
            if (reason.StartsWith("send_blocked=ambiguous_partial_write", StringComparison.Ordinal)) return "ambiguous_partial_write";
            if (reason.StartsWith("send_blocked=input_write_failed", StringComparison.Ordinal)) return "input_write_failed";
            if (reason == "console unavailable") return "console_unavailable";
            if (reason == "poll failed") return "poll_failed";
            if (result.FailureAction == FailureRecoveryAction.STOP_AND_REPORT.ToString()) return "provider_stop";
            if (result.WouldSend) return "attempt_ready";
            if (result.Busy) return "target_busy";
            if (result.Triggered) return "trigger_wait";
            if (!String.IsNullOrEmpty(result.Error)) return "runtime_error";
            if (result.Read) return "observed";
            return "no_match";
        }
    }

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
        private readonly Dictionary<string, ITargetTransport> _targetTransports = new Dictionary<string, ITargetTransport>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _restoredOpenCodeSessions = new Dictionary<string, string>(StringComparer.Ordinal);
        // T-158: console-resolution failure backoff. When a matched process
        // cannot be bound to an attachable console (headless-host cline,
        // dead-parent lineage, GUI-only binary, etc.), re-probing on every
        // 2s poll is wasted work AND spams ERROR logs. Per-PID exponential
        // backoff bounds the retry cadence while still recovering the moment
        // the console becomes resolvable (e.g. after a rate-limit reset).
        private readonly Dictionary<int, DateTime> _resolutionUnavailableUntilUtc = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, int> _resolutionUnavailableAttempts = new Dictionary<int, int>();
        // T-158-http: transport-level backoff for HTTP targets whose server
        // is unreachable. Keyed by rule name; same exponential cadence.
        private readonly Dictionary<string, DateTime> _transportUnavailableUntilUtc = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _transportUnavailableAttempts = new Dictionary<string, int>(StringComparer.Ordinal);
        private bool _stateLoaded;
        private bool _stateStoreHealthy = true;
        private bool _stateLedgerAmbiguous;
        private DateTime _stateRecoveryNotBeforeUtc = DateTime.MinValue;
        // PERF-004: track durable-state mutations per RetrySessionState
        // (IsDurableDirty) to skip full rebuild when no safety-relevant
        // change happened, while keeping a bounded checkpoint cadence for
        // LastObservedUtc retention semantics.
        private DateTime _lastDurableCheckpointUtc = DateTime.MinValue;
        // T-139: health contract tracking. Health is written atomically to
        // run/SAICONT.health.json and is non-authoritative for safety/retry.
        private string _healthFilePath;
        private int _policyRevision;
        private DateTime _lastPollUtc = DateTime.MinValue;
        private DateTime _lastSuccessfulPollUtc = DateTime.MinValue;
        private DateTime _LastErrorUtc = DateTime.MinValue;
        private string _lastErrorCode;
        private DateTime _lastWriteUtc = DateTime.MinValue;
        private string _lastWriteTarget;
        private DateTime _lastHealthWriteUtc = DateTime.MinValue;
        private const int HealthHeartbeatIntervalSeconds = 45;
        private const int ProductVersionMajor = 1, ProductVersionMinor = 2;
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

        public IList<PollResult> PollOnce(bool allowInput, Func<bool> shouldStop = null, Func<bool> authorizationRevoked = null)
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
                        RememberRestoredOpenCodeSession(rec, savedRule);
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

                string transportKind = rule.Transport ?? "console";
                if (String.Equals(transportKind, "opencode-http", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(PollOpenCodeHttp(rule, allowInput, shouldStop, authorizationRevoked, nowUtc));
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
                    int resolutionKey = candidate.MatchedProcessId;
                    DateTime resolutionCooldownUntil;
                    if (_resolutionUnavailableUntilUtc.TryGetValue(resolutionKey, out resolutionCooldownUntil) && nowUtc < resolutionCooldownUntil)
                    {
                        // T-158: still inside the console-resolution backoff
                        // window. Do not re-probe; report the waiting state so
                        // the operator can see the cadence is intentional.
                        results.Add(new PollResult
                        {
                            Target = rule.Name,
                            ProcessId = resolutionKey,
                            ParentProcessId = candidate.ParentProcessId,
                            ProcessName = candidate.MatchedProcessName,
                            Read = false,
                            Error = null,
                            Reason = "send_blocked=console_unavailable_backoff",
                            NextAttemptUtc = resolutionCooldownUntil
                        });
                        continue;
                    }
                    if (!TryReadTarget(rule, candidate, shouldStop, out resolvedConsole, out readError, ref sharedRefreshedProcesses, ref sharedRefreshedIndex, ref sharedRefreshTaken))
                    {
                        // T-158: console could not be resolved (AttachConsole
                        // invalid-handle, "console does not contain matched
                        // process", membership unavailable, etc.). Record an
                        // exponential backoff for THIS pid so the next polls
                        // skip the expensive re-probe until it expires.
                        AdvanceResolutionBackoff(resolutionKey, rule, nowUtc);
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

                    // T-158: console resolved; clear any pending backoff so a
                    // future transient failure starts a fresh backoff ladder.
                    ClearResolutionBackoff(resolutionKey);

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
                        NextAttemptUtc = decision.NextAttemptUtc,
                        FailureClass = observation.Failure != null && observation.Failure.IsKnown ? observation.Failure.Class.ToString() : null,
                        FailureAction = observation.Failure != null && observation.Failure.IsKnown ? observation.Failure.RecommendedAction.ToString() : null,
                        FailureRetryable = observation.Failure != null && observation.Failure.Retryable,
                        FailureSeverity = observation.Failure != null && observation.Failure.IsKnown ? observation.Failure.Severity : null,
                        FailureMatchedBy = observation.Failure != null && observation.Failure.MatchedBy != null ? String.Join(",", observation.Failure.MatchedBy) : null
                    };

                    if (!String.IsNullOrEmpty(observation.EvaluationError))
                    {
                        result.Error = "rule_evaluation_failed=" + observation.EvaluationError;
                        result.Reason = "send_blocked=rule_evaluation_failed";
                    }

                    // T-135: pre-write authorization barrier. allowInput was captured
                    // before PollOnce began; if authorization was revoked mid-poll
                    // (e.g. WATCH -> DRY-RUN), the write must not proceed.
                    if (decision.Send && allowInput && (authorizationRevoked == null || !authorizationRevoked()))
                    {
                        if (authorizationRevoked != null && authorizationRevoked())
                        {
                            result.Reason = "send_blocked=authorization_revoked_mid_poll";
                            state.RecordAttempt(false, decision.TriggerToken, rule, nowUtc);
                            continue;
                        }
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

            // T-139: track poll outcome for health contract.
            bool hadError = false;
            string errorCode = null;
            if (results != null)
            {
                foreach (PollResult r in results)
                {
                    if (!String.IsNullOrEmpty(r.Error))
                    {
                        hadError = true;
                        errorCode = r.DecisionCode;
                        break;
                    }
                }
            }
            _lastPollUtc = nowUtc;
            if (hadError)
            {
                _LastErrorUtc = nowUtc;
                _lastErrorCode = errorCode;
            }
            else
            {
                _lastSuccessfulPollUtc = nowUtc;
            }
            WriteHealth(nowUtc, results, !hadError);

            return results;
        }

        // SRC-023: OpenCode HTTP transport path. Mirrors the console candidate
        // flow but reads through ITargetTransport.Poll instead of a Win32
        // screen buffer, and commits through ITargetTransport.Send instead of
        // WriteConsoleInputW. The transport builds a synthetic text surface
        // that RuleMatcher.Inspect consumes unchanged, so trigger/ready/busy
        // regexes, FailureIntelligence, and RetrySessionState all behave
        // identically to the console path.
        private PollResult PollOpenCodeHttp(
            TargetRule rule,
            bool allowInput,
            Func<bool> shouldStop,
            Func<bool> authorizationRevoked,
            DateTime nowUtc)
        {
            ITargetTransport transport;
            string transportKey = (rule.Endpoint ?? String.Empty).TrimEnd('/') + "|" + (rule.ProjectDirectory ?? String.Empty);
            // T-158-http: while inside the unreachable-server backoff window,
            // do not construct/poll the transport at all. A dead endpoint is
            // reported once per cooldown instead of every 2s poll.
            DateTime transportCooldownUntil;
            if (_transportUnavailableUntilUtc.TryGetValue(rule.Name, out transportCooldownUntil) && nowUtc < transportCooldownUntil)
            {
                return new PollResult
                {
                    Target = rule.Name,
                    Reason = "send_blocked=transport_unavailable_backoff",
                    NextAttemptUtc = transportCooldownUntil
                };
            }
            if (!_targetTransports.TryGetValue(transportKey, out transport))
            {
                string restoredSessionId;
                bool restoredSessionKnown = TryGetRestoredOpenCodeSession(rule, out restoredSessionId);
                transport = new OpenCodeHttpTransport(rule.Endpoint, rule.ProjectDirectory, restoredSessionKnown ? restoredSessionId : null, restoredSessionKnown && String.IsNullOrEmpty(restoredSessionId));
                _targetTransports[transportKey] = transport;
            }
            string pollError;
            TargetObservation observation = transport.Poll(rule, nowUtc, out pollError);

            if (!String.IsNullOrEmpty(pollError) || observation == null)
            {
                AdvanceTransportBackoff(rule, nowUtc);
                return new PollResult
                {
                    Target = rule.Name,
                    Error = String.IsNullOrEmpty(pollError) ? "http_poll_failed" : pollError,
                    Reason = "send_blocked=transport_unavailable"
                };
            }
            ClearTransportBackoff(rule.Name);
            if (!observation.CanSend)
            {
                return new PollResult
                {
                    Target = rule.Name,
                    Error = observation.BlockReason ?? "transport_blocked",
                    Reason = "send_blocked=" + (observation.BlockReason ?? "transport_blocked")
                };
            }

            // Build the synthetic console surface the rule engine expects.
            string text = observation.Text ?? String.Empty;
            int cursorRow = observation.CursorRow > 0 ? observation.CursorRow : CountLines(text);
            var snapshot = new ConsoleSnapshot
            {
                ProcessId = 0,
                WindowHandle = IntPtr.Zero,
                Title = observation.SessionIdentity,
                Text = text,
                CursorLine = LastLine(text),
                CursorColumn = 0,
                CursorRow = cursorRow,
                StartRow = observation.StartRow,
                BufferWidth = 120,
                ConsoleProcessIds = new int[0],
                MembershipStatus = ConsoleMembershipStatus.VerifiedPresent,
                MembershipError = null
            };

            RuleObservation ruleObservation = RuleMatcher.Inspect(rule, snapshot, nowUtc);
            if (!String.IsNullOrEmpty(ruleObservation.EvaluationError))
            {
                return new PollResult
                {
                    Target = rule.Name,
                    Error = "rule_evaluation_failed=" + ruleObservation.EvaluationError,
                    Reason = "send_blocked=rule_evaluation_failed"
                };
            }
            // OpenCode owns lifecycle truth through its API. The matcher still
            // supplies trigger/failure policy, but message history must not
            // turn an idle session into busy (or vice versa) merely because a
            // historical line contains words such as "working".
            ruleObservation.Ready = observation.Ready;
            ruleObservation.Busy = observation.Busy;

            // Stable pseudo-PID for state identity: deterministic hash of
            // endpoint+session so durable state survives restart.
            int pseudoPid = StableIdentity(rule.Endpoint, observation.SessionIdentity);
            DateTime sessionStart = StableSessionStart(rule.Endpoint, observation.SessionIdentity);
            var identity = new ProcessSessionIdentity
            {
                ProcessId = pseudoPid,
                ProcessName = "opencode-http",
                StartTimeUtc = sessionStart
            };
            string stateKey = rule.Name + ":" + pseudoPid + ":" + sessionStart.ToString("o", CultureInfo.InvariantCulture);

            RetrySessionState state;
            if (!_states.TryGetValue(stateKey, out state))
            {
                if (_states.Count >= MaximumSessionStates && !TryMakeStateCapacity())
                {
                    return new PollResult
                    {
                        Target = rule.Name,
                        Error = "state_capacity_exhausted",
                        Reason = "send_blocked=state_capacity_exhausted"
                    };
                }
                state = new RetrySessionState();
                _states[stateKey] = state;
            }
            state.SetTransportIdentity(observation);
            if (identity.IsStrong)
            {
                _stateIdentities[stateKey] = identity;
            }

            RetryDecision decision = state.Observe(ruleObservation, rule, nowUtc);
            var result = new PollResult
            {
                Target = rule.Name,
                ProcessId = pseudoPid,
                ProcessName = "opencode-http",
                AttachProcessId = pseudoPid,
                Title = observation.SessionIdentity,
                Read = true,
                Triggered = ruleObservation.Triggered,
                Ready = ruleObservation.Ready,
                Busy = ruleObservation.Busy,
                WouldSend = decision.Send,
                Reason = decision.Reason,
                TriggerToken = ruleObservation.TriggerToken,
                NextAttemptUtc = decision.NextAttemptUtc,
                FailureClass = ruleObservation.Failure != null && ruleObservation.Failure.IsKnown ? ruleObservation.Failure.Class.ToString() : null,
                FailureAction = ruleObservation.Failure != null && ruleObservation.Failure.IsKnown ? ruleObservation.Failure.RecommendedAction.ToString() : null,
                FailureRetryable = ruleObservation.Failure != null && ruleObservation.Failure.Retryable,
                FailureSeverity = ruleObservation.Failure != null && ruleObservation.Failure.IsKnown ? ruleObservation.Failure.Severity : null,
                FailureMatchedBy = ruleObservation.Failure != null && ruleObservation.Failure.MatchedBy != null ? String.Join(",", ruleObservation.Failure.MatchedBy) : null
            };

            SendOutcome completedOutcome;
            if (transport.TryTakeCompletedSend(observation.SessionIdentity, out completedOutcome))
            {
                state.CommitAttempt(MapOutcome(completedOutcome), rule, nowUtc);
                result.Sent = completedOutcome != null && completedOutcome.Disposition == SendDisposition.CompleteInputCommitted;
                result.Error = result.Sent ? null : "http_send_failed: " + (completedOutcome == null ? "unknown" : completedOutcome.Disposition.ToString());
                result.Reason = result.Sent ? "send=command_written" : "send_blocked=ambiguous_partial_write";
                return result;
            }

            // T-135 mirror: same pre-write authorization barrier as console.
            if (decision.Send && allowInput && (authorizationRevoked == null || !authorizationRevoked()))
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
                else if (shouldStop != null && shouldStop())
                {
                    result.Reason = "send_blocked=stop_requested";
                    state.RecordAttempt(false, decision.TriggerToken, rule, nowUtc);
                }
                else
                {
                    string validateError;
                    if (!transport.ValidatePreSend(observation, rule, out validateError))
                    {
                        result.Reason = "send_blocked=pre_send_validation: " + validateError;
                        state.RecordAttempt(false, decision.TriggerToken, rule, nowUtc);
                    }
                    else
                    {
                        RetrySessionState preReservationSnapshot = state.Clone();
                        state.ReserveAttempt(ruleObservation.TriggerToken, rule, nowUtc);
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
                                return result;
                            }
                        }
                        string sendError;
                        SendOutcome outcome = transport.Send(
                            observation,
                            rule,
                            rule.Command,
                            delegate
                            {
                                return (authorizationRevoked == null || !authorizationRevoked()) &&
                                    (shouldStop == null || !shouldStop());
                            },
                            out sendError);
                        result.Sent = outcome != null && outcome.Disposition == SendDisposition.CompleteInputCommitted;
                        result.Error = sendError;
                        if (outcome != null && outcome.Disposition == SendDisposition.Pending)
                        {
                            result.Reason = "send=pending";
                            return result;
                        }
                        if (result.Sent)
                        {
                            result.Reason = "send=command_written";
                        }
                        else
                        {
                            result.Reason = sendError ?? "send_blocked=send_failed";
                        }
                        NativeWriteOutcome mapped = MapOutcome(outcome);
                        state.CommitAttempt(mapped, rule, nowUtc);
                    }
                }
            }

            return result;
        }

        private static NativeWriteOutcome MapOutcome(SendOutcome outcome)
        {
            if (outcome == null) return NativeWriteOutcome.NoInputCommitted;
            switch (outcome.Disposition)
            {
                case SendDisposition.CompleteInputCommitted: return NativeWriteOutcome.CompleteInputCommitted;
                case SendDisposition.AmbiguousOrPartialInput: return NativeWriteOutcome.AmbiguousOrPartialInput;
                default: return NativeWriteOutcome.NoInputCommitted;
            }
        }

        private void RememberRestoredOpenCodeSession(StateRecord record, TargetRule rule)
        {
            if (record == null || rule == null || !String.Equals(rule.Transport, "opencode-http", StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(record.TransportKind, "opencode-http", StringComparison.OrdinalIgnoreCase) ||
                String.IsNullOrWhiteSpace(record.SessionIdentity)) return;
            string key = OpenCodeStateKey(rule.Name, rule.Endpoint, rule.ProjectDirectory);
            string existing;
            if (_restoredOpenCodeSessions.TryGetValue(key, out existing) && !String.Equals(existing, record.SessionIdentity, StringComparison.Ordinal))
            {
                _restoredOpenCodeSessions[key] = String.Empty;
            }
            else if (!_restoredOpenCodeSessions.ContainsKey(key))
            {
                _restoredOpenCodeSessions[key] = record.SessionIdentity;
            }
        }

        private bool TryGetRestoredOpenCodeSession(TargetRule rule, out string sessionId)
        {
            return _restoredOpenCodeSessions.TryGetValue(OpenCodeStateKey(rule.Name, rule.Endpoint, rule.ProjectDirectory), out sessionId);
        }

        private static string OpenCodeStateKey(string ruleName, string endpoint, string projectDirectory)
        {
            return (ruleName ?? String.Empty) + "|" + (endpoint ?? String.Empty).TrimEnd('/') + "|" + (projectDirectory ?? String.Empty);
        }

        private static int StableIdentity(string endpoint, string sessionId)
        {
            unchecked
            {
                int hash = 17;
                foreach (char c in (endpoint ?? String.Empty) + "|" + (sessionId ?? String.Empty))
                {
                    hash = hash * 31 + c;
                }
                return hash & 0x7FFFFFFF;
            }
        }

        private static DateTime StableSessionStart(string endpoint, string sessionId)
        {
            long offset = (long)StableIdentity(endpoint, sessionId) % TimeSpan.TicksPerDay;
            return new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(offset);
        }

        private static int CountLines(string text)
        {
            if (String.IsNullOrEmpty(text)) return 0;
            int n = 0;
            for (int i = 0; i < text.Length; i++) { if (text[i] == '\n') n++; }
            return n + 1;
        }

        private static string LastLine(string text)
        {
            if (String.IsNullOrEmpty(text)) return String.Empty;
            int idx = text.LastIndexOf('\n');
            return idx < 0 ? text : text.Substring(idx + 1);
        }

        internal static bool IsNonConsoleDiscoveryFailure(string error)
        {
            return !String.IsNullOrEmpty(error) &&
                error.StartsWith("AttachConsole failed", StringComparison.Ordinal) &&
                error.EndsWith("(6)", StringComparison.Ordinal);
        }

        // T-158: record an exponential backoff for a PID whose console
        // could not be resolved. Subsequent polls skip the expensive
        // re-probe until the cooldown expires. Backoff base uses the
        // rule's configured retry interval (min 30s), multiplied by
        // the configured backoff multiplier, capped at the rule's
        // maximum retry interval (or 300s, whichever is smaller).
        private void AdvanceResolutionBackoff(int pid, TargetRule rule, DateTime nowUtc)
        {
            int attempts;
            _resolutionUnavailableAttempts.TryGetValue(pid, out attempts);
            attempts++;
            _resolutionUnavailableAttempts[pid] = attempts;
            int baseInterval = Math.Max(30, rule.SafeRetryIntervalSeconds);
            double multiplier = Math.Pow(rule.SafeBackoffMultiplier, Math.Max(0, attempts - 1));
            int maxInterval = Math.Min(rule.SafeMaximumRetryIntervalSeconds, 300);
            int backoffSeconds = (int)Math.Min(maxInterval, Math.Max(baseInterval, baseInterval * multiplier));
            _resolutionUnavailableUntilUtc[pid] = nowUtc.AddSeconds(backoffSeconds);
        }

        private void ClearResolutionBackoff(int pid)
        {
            _resolutionUnavailableAttempts.Remove(pid);
            _resolutionUnavailableUntilUtc.Remove(pid);
        }

        // T-158-http: same exponential ladder as the console path, keyed by
        // rule name. A dead opencode endpoint stops being probed every poll.
        private void AdvanceTransportBackoff(TargetRule rule, DateTime nowUtc)
        {
            int attempts;
            _transportUnavailableAttempts.TryGetValue(rule.Name, out attempts);
            attempts++;
            _transportUnavailableAttempts[rule.Name] = attempts;
            int baseInterval = Math.Max(30, rule.SafeRetryIntervalSeconds);
            double multiplier = Math.Pow(rule.SafeBackoffMultiplier, Math.Max(0, attempts - 1));
            int maxInterval = Math.Min(rule.SafeMaximumRetryIntervalSeconds, 300);
            int backoffSeconds = (int)Math.Min(maxInterval, Math.Max(baseInterval, baseInterval * multiplier));
            _transportUnavailableUntilUtc[rule.Name] = nowUtc.AddSeconds(backoffSeconds);
        }

        private void ClearTransportBackoff(string ruleName)
        {
            _transportUnavailableAttempts.Remove(ruleName);
            _transportUnavailableUntilUtc.Remove(ruleName);
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
                    RememberRestoredOpenCodeSession(rec, savedRule);
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
            // T-158: drop resolution-backoff entries for PIDs that no longer
            // exist so the maps cannot grow unbounded across process churn.
            if (livePids != null)
            {
                var staleBackoff = new List<int>();
                foreach (int pid in _resolutionUnavailableUntilUtc.Keys)
                {
                    if (!livePids.Contains(pid))
                    {
                        staleBackoff.Add(pid);
                    }
                }
                for (int i = 0; i < staleBackoff.Count; i++)
                {
                    _resolutionUnavailableUntilUtc.Remove(staleBackoff[i]);
                    _resolutionUnavailableAttempts.Remove(staleBackoff[i]);
                }
            }

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

        /// <summary>T--139: set the path to write SAICONT.health.json to. null disables health.</summary>
        public string HealthFilePath
        {
            get { return _healthFilePath; }
            set { _healthFilePath = value; }
        }

        /// <summary>T-139: increment policy revision when configuration is swapped.</summary>
        public void BumpPolicyRevision()
        {
            _policyRevision++;
        }

        private void WriteHealth(DateTime nowUtc, IList<PollResult> results, bool pollSucceeded)
        {
            if (String.IsNullOrEmpty(_healthFilePath)) { return; }
            // T-140: bounded heartbeat -- do not rewrite every poll when
            // nothing changed. Rewrite on: meaningful transition (error,
            // success-after-error, write), or heartbeat interval elapsed.
            bool meaningfulTransition =
                (nowUtc - _lastHealthWriteUtc).TotalSeconds >= HealthHeartbeatIntervalSeconds ||
                (pollSucceeded && _lastSuccessfulPollUtc == DateTime.MinValue) ||
                (!pollSucceeded && _LastErrorUtc == DateTime.MinValue) ||
                (results != null && results.Count > 0);
            if (!meaningfulTransition && _lastHealthWriteUtc != DateTime.MinValue)
            {
                return;
            }
            int active = 0, triggered = 0, writeEligible = 0;
            DateTime nextDue = DateTime.MaxValue;
            if (results != null)
            {
                foreach (PollResult r in results)
                {
                    active++;
                    if (r.Triggered) triggered++;
                    if (r.WouldSend || r.Sent) writeEligible++;
                    if (r.Sent)
                    {
                        _lastWriteUtc = nowUtc;
                        _lastWriteTarget = r.Target;
                    }
                    if (r.NextAttemptUtc != DateTime.MinValue && r.NextAttemptUtc < nextDue)
                    {
                        nextDue = r.NextAttemptUtc;
                    }
                }
            }
            var report = new HealthReport
            {
                ProductVersion = ProductVersionMajor + "." + ProductVersionMinor,
                ProcessId = 0,
                ProcessStartUtc = null,
                InstanceShortId = null,
                Mode = null,
                PolicyRevision = _policyRevision,
                ConfigFingerprint = _configuration != null ? (_configuration.Targets != null ? _configuration.Targets.Count.ToString(CultureInfo.InvariantCulture) + "_targets" : "0_targets") : "null",
                LastPollUtc = _lastPollUtc == DateTime.MinValue ? (DateTime?)null : _lastPollUtc,
                LastSuccessfulPollUtc = _lastSuccessfulPollUtc == DateTime.MinValue ? (DateTime?)null : _lastSuccessfulPollUtc,
                LastErrorUtc = _LastErrorUtc == DateTime.MinValue ? (DateTime?)null : _LastErrorUtc,
                LastErrorCode = _lastErrorCode,
                StateStoreDisposition = _stateStoreHealthy ? (_stateLedgerAmbiguous ? "ambiguous" : "healthy") : "unhealthy",
                StateStoreHealthy = _stateStoreHealthy && !_stateLedgerAmbiguous,
                ActiveSessions = active,
                TriggeredSessions = triggered,
                WriteEligibleSessions = writeEligible,
                NextDueUtc = nextDue == DateTime.MaxValue ? (DateTime?)null : nextDue,
                LastWriteUtc = _lastWriteUtc == DateTime.MinValue ? (DateTime?)null : _lastWriteUtc,
                LastWriteTarget = _lastWriteTarget,
                HealthUpdatedUtc = nowUtc
            };
            string healthError;
            if (report.TryWrite(_healthFilePath, out healthError))
            {
                _lastHealthWriteUtc = nowUtc;
            }
            // health write failure is non-authoritative: do not change safety/retry truth
        }

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
            Dispose();
        }

        public void Dispose()
        {
            foreach (ITargetTransport transport in _targetTransports.Values)
            {
                try { transport.Dispose(); } catch { }
            }
            _targetTransports.Clear();
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
