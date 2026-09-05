using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
        // W2L1-004: CancelInFlight() is called from the shutdown/reload thread
        // while the poll worker may be mutating the transport table.
        private readonly object _transportCancelLock = new object();
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
        // CORE-001: a corrupt/unsupported durable ledger arms one bounded
        // reconciliation instead of wedging the installation forever.
        private bool _stateReconciliationRequired;
        private int _stateReconciliationFailures;
        private const int MaximumStateReconciliationAttempts = 5;
        private DateTime _stateRecoveryNotBeforeUtc = DateTime.MinValue;
        // PERF-004: track durable-state mutations per RetrySessionState
        // (IsDurableDirty) to skip full rebuild when no safety-relevant
        // change happened, while keeping a bounded checkpoint cadence for
        // LastObservedUtc retention semantics.
        private DateTime _lastDurableCheckpointUtc = DateTime.MinValue;
        // W2L1-003: pruning removes records from memory and must be persisted
        // with the same discipline as any other safety-relevant mutation.
        private bool _pruneDurableDirty;
        // T-139: health contract tracking. Health is written atomically to
        // run/SAICONT.health.json and is non-authoritative for safety/retry.
        private string _healthFilePath;
        // CORE-005: authoritative lifecycle identity, injected by Program.
        private LifecycleHealthContext _lifecycleHealth;
        // CORE-007: semantic snapshot of the last published health state.
        private string _lastHealthFingerprint;
        private int _healthWriteCount;
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
                    // CORE-001: this classification -- writeable store, but an
                    // unreadable/foreign-schema ledger -- is the one that used
                    // to be terminal. TryRecoverStateStore() is only entered
                    // when the store is UNHEALTHY, and preflight reports a
                    // corrupt-but-writeable store as healthy, so nothing ever
                    // reconciled it. Arm the reconciliation instead; it runs
                    // after the safe initial delay above, not inside this load.
                    _stateReconciliationRequired = true;
                }
            }

            if (_stateStore != null && _stateReconciliationRequired)
            {
                TryReconcileStateLedger(nowUtc, ref stateDiagnostic);
            }

            var results = new List<PollResult>();
            // CORE-003: process discovery is a CONSOLE prerequisite, not a
            // global one. An opencode-http rule observes through HTTP/SSE and
            // never touches the Win32 snapshot, so a Toolhelp failure must fail
            // console targets closed WITHOUT taking down the independent HTTP
            // continuation path. Only when every enabled target depends on the
            // snapshot is the global early return still correct.
            if (!snapshotSucceeded)
            {
                results.Add(new PollResult
                {
                    Target = "runtime",
                    Error = "process_discovery_unavailable",
                    Reason = "send_blocked=process_discovery_unavailable"
                });
                if (!HasEnabledSnapshotIndependentTarget())
                {
                    return results;
                }
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

            // PERF-003: one console observation cache for the whole poll
            // generation, plus ONE shared refreshed snapshot instead of one per
            // rule. Console contents are a poll-generation fact; re-reading them
            // per rule scaled cost with rule count rather than console count.
            var observationCache = new PollConsoleObservationCache();
            int deepestScan = 0;
            if (_configuration.Targets != null)
            {
                foreach (TargetRule scanRule in _configuration.Targets)
                {
                    if (scanRule == null || !scanRule.Enabled || IsSnapshotIndependentTransport(scanRule)) continue;
                    if (scanRule.ScanLines > deepestScan) deepestScan = scanRule.ScanLines;
                }
            }
            IList<ProcessEntry> sharedRefreshedProcesses = null;
            ProcessSnapshotIndex sharedRefreshedIndex = null;
            bool sharedRefreshTaken = false;
            // PERF-004: one rule-independent failure assessment per console
            // snapshot instance, shared across every rule that observes it.
            var failureBySnapshot = new Dictionary<ConsoleSnapshot, FailureAssessment>();

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

                // CORE-003: a console rule genuinely needs the snapshot. When
                // it is missing, this rule alone fails closed while HTTP rules
                // above continue.
                if (!snapshotSucceeded)
                {
                    results.Add(new PollResult
                    {
                        Target = rule.Name,
                        Error = "process_discovery_unavailable",
                        Reason = "send_blocked=process_discovery_unavailable"
                    });
                    continue;
                }

                // PERF-006: use the pre-built index when available; fall back
                // to per-call construction only when the snapshot failed.
                IList<ConsoleCandidate> candidates = snapshotIndex != null
                    ? ProcessDiscovery.FindCandidates(snapshotIndex, targetNameSet, _sessionResolver)
                    : ProcessDiscovery.FindCandidates(processes, targetNameSet, _sessionResolver);

                // PERF-003: the shared refreshed snapshot is now poll-scoped
                // (declared above), so a resolution failure in rule A does not make
                // rule B take another Toolhelp snapshot in the same poll.

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
                    if (!TryReadTarget(rule, candidate, shouldStop, observationCache, deepestScan, out resolvedConsole, out readError, ref sharedRefreshedProcesses, ref sharedRefreshedIndex, ref sharedRefreshTaken))
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

                    // PERF-004: classify this immutable snapshot once, then share
                    // the result with every rule that observes it this poll.
                    FailureAssessment sharedFailure;
                    if (!failureBySnapshot.TryGetValue(resolvedConsole.Snapshot, out sharedFailure))
                    {
                        sharedFailure = FailureIntelligence.Classify(resolvedConsole.Snapshot.Text);
                        failureBySnapshot[resolvedConsole.Snapshot] = sharedFailure;
                    }
                    RuleObservation observation = RuleMatcher.Inspect(rule, resolvedConsole.Snapshot, nowUtc, sharedFailure);
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
                                 // PERF-003: null cache -- the transactional
                                 // pre-send reread must be FRESH, never served
                                 // from the poll observation cache.
                                 if (!TryReadTarget(rule, freshTarget, shouldStop, null, 0, out freshResolved, out freshError, ref preSendRefreshed, ref preSendRefreshedIndex, ref preSendRefreshTaken))

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
                // W2L1-003: a prune removed records from memory, so the persisted
                // ledger no longer matches. Treat it as a first-class mutation.
                if (_pruneDurableDirty)
                {
                    anyDirty = true;
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
                        _pruneDurableDirty = false;
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
                lock (_transportCancelLock)
                {
                    _targetTransports[transportKey] = transport;
                }
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
            // CORE-004: bind this observation to the owning rule's durable state
            // key so an asynchronous completion cannot be consumed by a sibling
            // rule that happens to share endpoint+project+session.
            observation.OwnerStateKey = stateKey;
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
            var sendOwner = new SendOwner
            {
                RuleName = rule.Name,
                StateKey = stateKey,
                SessionIdentity = observation.SessionIdentity
            };
            if (transport.TryTakeCompletedSend(sendOwner, out completedOutcome))
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

        // CORE-003: opencode-http observes through HTTP/SSE, so it does not
        // depend on the Win32 process snapshot. Console rules do.
        internal static bool IsSnapshotIndependentTransport(TargetRule rule)
        {
            return rule != null && String.Equals(rule.Transport ?? "console", "opencode-http", StringComparison.OrdinalIgnoreCase);
        }

        private bool HasEnabledSnapshotIndependentTarget()
        {
            if (_configuration == null || _configuration.Targets == null) return false;
            foreach (TargetRule rule in _configuration.Targets)
            {
                if (rule != null && rule.Enabled && IsSnapshotIndependentTransport(rule)) return true;
            }
            return false;
        }

        // CORE-001: the reachable exit from conservative recovery. The store is
        // WRITEABLE (preflight passed) but its ledger could not be read, so the
        // in-memory authority is all that exists. Order is the safety argument:
        // commit a fresh authoritative ledger atomically FIRST, clear the
        // recovery marker only after that commit, and clear _stateLedgerAmbiguous
        // only after both. Any failure leaves marker + ambiguity intact, so the
        // watcher stays fail-closed exactly as it was before the attempt.
        private bool TryReconcileStateLedger(DateTime nowUtc, ref string stateDiagnostic)
        {
            if (_stateStore == null || !_stateReconciliationRequired)
            {
                return true;
            }
            if (nowUtc < _stateRecoveryNotBeforeUtc)
            {
                stateDiagnostic = "state_ledger_ambiguous (reconciliation cooldown)";
                return false;
            }
            if (_stateReconciliationFailures >= MaximumStateReconciliationAttempts)
            {
                stateDiagnostic = "state_ledger_ambiguous (reconciliation attempts exhausted: " + (_stateStore.LastError ?? "unknown") + ")";
                return false;
            }

            string preflightError;
            if (!_stateStore.TryPreflight(out preflightError))
            {
                _stateStoreHealthy = false;
                _stateRecoveryNotBeforeUtc = nowUtc.AddSeconds(30);
                stateDiagnostic = "state_preflight_failed: " + preflightError;
                return false;
            }

            string reconcileError;
            if (!_stateStore.TryReconcile(ExportAllStates(nowUtc), nowUtc, out reconcileError))
            {
                _stateReconciliationFailures++;
                _stateRecoveryNotBeforeUtc = nowUtc.AddSeconds(30);
                stateDiagnostic = "state_reconciliation_failed: " + reconcileError;
                return false;
            }

            _stateReconciliationRequired = false;
            _stateReconciliationFailures = 0;
            _stateLedgerAmbiguous = false;
            _stateStoreHealthy = true;
            _stateRecoveryNotBeforeUtc = DateTime.MinValue;
            stateDiagnostic = null;
            return true;
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
                // CORE-001: arm the reconciliation instead of latching forever.
                _stateReconciliationRequired = true;
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
            PollConsoleObservationCache observationCache,
            int captureLines,
            out ResolvedConsoleSession session,
            out string error,
             ref IList<ProcessEntry> sharedRefreshedProcesses,
             ref ProcessSnapshotIndex sharedRefreshedIndex,
             ref bool sharedRefreshTaken)
        {
            session = null;
            error = null;
            ConsoleReadAttempt directRead = delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
            {
                return _consoleReader(pid, lineCount, out s, out e);
            };
            // PERF-003: ordinary rule evaluation reads through the poll cache.
            // The transactional pre-send reread deliberately passes null so it
            // always observes the console as it is at that instant.
            ConsoleReadAttempt readAttempt = observationCache == null
                ? directRead
                : observationCache.Wrap(directRead, captureLines);

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
                //
                // W2L1-003: OS-PID liveness is CONSOLE evidence only. An
                // opencode-http record's ProcessId is a synthetic hash of
                // endpoint+session, so "not in livePids" is true by construction
                // and an endpoint outage (which also stops LastObservedUtc from
                // advancing) would age anti-replay authority out of existence for
                // a session that never disappeared. HTTP records are retired only
                // by transport-specific evidence, never by this path.
                if (livePids != null && !IsHttpStateRecord(pair.Key, pair.Value))
                {
                    ProcessSessionIdentity identity;
                    if (_stateIdentities.TryGetValue(pair.Key, out identity) && identity != null && identity.IsStrong)
                    {
                        if (!livePids.Contains(identity.ProcessId))
                        {
                            bool isCritical = pair.Value.State == RecoveryState.AttemptInFlightReserved ||
                                pair.Value.State == RecoveryState.AmbiguousFailClosed ||
                                pair.Value.State == RecoveryState.CommandWrittenAwaitingOutcome ||
                                // W2L1-003: RecoveryConfirmed carries the
                                // SuppressedToken anti-replay authority that
                                // TryMakeStateCapacity already refuses to evict;
                                // giving it only the short grace here contradicted
                                // that and could authorize a duplicate write.
                                !String.IsNullOrEmpty(pair.Value.SuppressedToken);
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
            // W2L1-003: pruning is a real durable mutation. Without marking it,
            // memory could diverge from the persisted ledger until an unrelated
            // mutation or the 60s checkpoint happened to save.
            if (expiredKeys.Count > 0)
            {
                _pruneDurableDirty = true;
            }
        }

        // W2L1-003: identify records owned by the opencode-http transport. The
        // record's own transport kind is authoritative; the state-key prefix is a
        // fallback for records restored before the transport kind was persisted.
        private bool IsHttpStateRecord(string stateKey, RetrySessionState state)
        {
            if (state != null && String.Equals(state.TransportKind, "opencode-http", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (String.IsNullOrEmpty(stateKey)) return false;
            int separator = stateKey.IndexOf(':');
            string ruleName = separator > 0 ? stateKey.Substring(0, separator) : stateKey;
            TargetRule rule = LookupRule(ruleName);
            return IsSnapshotIndependentTransport(rule);
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

        /// <summary>
        /// CORE-005: bind this engine to the lifecycle's authoritative identity.
        /// Program calls this for --watch/--dry-run, and the GUI/TUI pass the same
        /// context into every replacement engine they build on reload, so health
        /// never silently stops being published or starts describing pid 0.
        /// </summary>
        public LifecycleHealthContext LifecycleHealth
        {
            get { return _lifecycleHealth; }
            set
            {
                _lifecycleHealth = value;
                if (value != null && !String.IsNullOrEmpty(value.HealthFilePath))
                {
                    _healthFilePath = value.HealthFilePath;
                }
            }
        }

        /// <summary>T-139: increment policy revision when configuration is swapped.</summary>
        public void BumpPolicyRevision()
        {
            _policyRevision++;
        }

        // CORE-005: a SEMANTIC configuration fingerprint. "<n>_targets" made two
        // materially different configurations indistinguishable to a consumer as
        // long as they had the same target count.
        private string ComputeConfigFingerprint()
        {
            if (_configuration == null) return "null";
            var builder = new StringBuilder();
            builder.Append(_configuration.PollIntervalMilliseconds).Append('|')
                .Append(_configuration.LogMaximumBytes).Append('|')
                .Append(_configuration.LogRetainedFiles).Append('|')
                .Append(_configuration.LogDuplicateWindowSeconds).Append('|');
            if (_configuration.Targets != null)
            {
                foreach (TargetRule rule in _configuration.Targets)
                {
                    if (rule == null) continue;
                    builder.Append(rule.SemanticFingerprint).Append('\u001f');
                }
            }
            using (var sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
                var text = new StringBuilder(16);
                for (int i = 0; i < 8; i++) text.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

        private void WriteHealth(DateTime nowUtc, IList<PollResult> results, bool pollSucceeded)
        {
            if (String.IsNullOrEmpty(_healthFilePath)) { return; }

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

            LifecycleHealthContext lifecycle = _lifecycleHealth;
            bool stateStoreApplicable = lifecycle == null ? _stateStore != null : lifecycle.StateStoreApplicable;
            // CORE-005: --dry-run builds no DurableStateStore, and the engine's
            // default _stateStoreHealthy=true then described an absent store as
            // healthy. Report it as not applicable instead.
            string storeDisposition = !stateStoreApplicable
                ? "not_applicable"
                : (_stateStoreHealthy ? (_stateLedgerAmbiguous ? "ambiguous" : "healthy") : "unhealthy");

            var report = new HealthReport
            {
                ProductVersion = ProductVersionMajor + "." + ProductVersionMinor,
                ProcessId = lifecycle != null ? lifecycle.ProcessId : 0,
                ProcessStartUtc = lifecycle != null && lifecycle.ProcessStartUtc != DateTime.MinValue ? (DateTime?)lifecycle.ProcessStartUtc : null,
                InstanceShortId = lifecycle != null ? lifecycle.InstanceShortId : null,
                Mode = lifecycle != null ? lifecycle.Mode : null,
                PolicyRevision = _policyRevision,
                ConfigFingerprint = ComputeConfigFingerprint(),
                LastPollUtc = _lastPollUtc == DateTime.MinValue ? (DateTime?)null : _lastPollUtc,
                LastSuccessfulPollUtc = _lastSuccessfulPollUtc == DateTime.MinValue ? (DateTime?)null : _lastSuccessfulPollUtc,
                LastErrorUtc = _LastErrorUtc == DateTime.MinValue ? (DateTime?)null : _LastErrorUtc,
                LastErrorCode = _lastErrorCode,
                StateStoreDisposition = storeDisposition,
                StateStoreHealthy = stateStoreApplicable && _stateStoreHealthy && !_stateLedgerAmbiguous,
                ActiveSessions = active,
                TriggeredSessions = triggered,
                WriteEligibleSessions = writeEligible,
                NextDueUtc = nextDue == DateTime.MaxValue ? (DateTime?)null : nextDue,
                LastWriteUtc = _lastWriteUtc == DateTime.MinValue ? (DateTime?)null : _lastWriteUtc,
                LastWriteTarget = _lastWriteTarget,
                HealthUpdatedUtc = nowUtc
            };

            // CORE-007: bounded heartbeat driven by the SEMANTIC snapshot, not by
            // "a result exists". results.Count > 0 is true on every ordinary poll,
            // so the 45-second bound was defeated and health was rewritten at poll
            // cadence (2s in the shipped configuration).
            string fingerprint = SemanticHealthFingerprint(report, pollSucceeded);
            bool firstPublication = _lastHealthWriteUtc == DateTime.MinValue || _lastHealthFingerprint == null;
            bool materialChange = !String.Equals(_lastHealthFingerprint, fingerprint, StringComparison.Ordinal);
            bool heartbeatDue = (nowUtc - _lastHealthWriteUtc).TotalSeconds >= HealthHeartbeatIntervalSeconds;
            if (!firstPublication && !materialChange && !heartbeatDue)
            {
                return;
            }

            string healthError;
            if (report.TryWrite(_healthFilePath, out healthError))
            {
                _lastHealthWriteUtc = nowUtc;
                _lastHealthFingerprint = fingerprint;
                _healthWriteCount++;
            }
            // health write failure is non-authoritative: do not change safety/retry truth
        }

        // CORE-007: every contract field EXCEPT health_updated_utc. A poll that
        // changes nothing produces the same fingerprint and therefore no write.
        // Timestamps that advance on every successful poll (last_poll_utc) are
        // deliberately excluded too -- they are liveness, not state, and the
        // bounded heartbeat is what publishes liveness.
        internal static string SemanticHealthFingerprint(HealthReport report)
        {
            return SemanticHealthFingerprint(report, true);
        }

        internal static string SemanticHealthFingerprint(HealthReport report, bool pollSucceeded)
        {
            if (report == null) return String.Empty;
            var builder = new StringBuilder(256);
            builder.Append(report.ProductVersion).Append('|')
                .Append(report.ProcessId).Append('|')
                .Append(report.ProcessStartUtc.HasValue ? report.ProcessStartUtc.Value.ToString("o", CultureInfo.InvariantCulture) : String.Empty).Append('|')
                .Append(report.InstanceShortId).Append('|')
                .Append(report.Mode).Append('|')
                .Append(report.PolicyRevision).Append('|')
                .Append(report.ConfigFingerprint).Append('|')
                .Append(pollSucceeded ? "ok" : "error").Append('|')
                .Append(report.LastErrorCode).Append('|')
                .Append(report.StateStoreDisposition).Append('|')
                .Append(report.StateStoreHealthy).Append('|')
                .Append(report.ActiveSessions).Append('|')
                .Append(report.TriggeredSessions).Append('|')
                .Append(report.WriteEligibleSessions).Append('|')
                .Append(report.NextDueUtc.HasValue ? report.NextDueUtc.Value.ToString("o", CultureInfo.InvariantCulture) : String.Empty).Append('|')
                .Append(report.LastWriteUtc.HasValue ? report.LastWriteUtc.Value.ToString("o", CultureInfo.InvariantCulture) : String.Empty).Append('|')
                .Append(report.LastWriteTarget);
            return builder.ToString();
        }

        /// <summary>CORE-007 test seam: how many health snapshots were written.</summary>
        internal int HealthWriteCount { get { return _healthWriteCount; } }

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
            ITargetTransport[] transports;
            lock (_transportCancelLock)
            {
                transports = new ITargetTransport[_targetTransports.Count];
                _targetTransports.Values.CopyTo(transports, 0);
                _targetTransports.Clear();
            }
            for (int i = 0; i < transports.Length; i++)
            {
                try { transports[i].Dispose(); } catch { }
            }
        }

        /// <summary>
        /// W2L1-004: unblock a poll worker that is inside synchronous transport
        /// work, WITHOUT disposing the engine. Reload and shutdown call this
        /// between invalidating authorization and proving the worker exited.
        /// </summary>
        public void CancelInFlight()
        {
            ITargetTransport[] transports;
            lock (_transportCancelLock)
            {
                transports = new ITargetTransport[_targetTransports.Count];
                _targetTransports.Values.CopyTo(transports, 0);
            }
            for (int i = 0; i < transports.Length; i++)
            {
                try { transports[i].CancelInFlight(); } catch { }
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
