using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SaiCont
{
    internal sealed class TriggerEvent
    {
        public string RuleName;
        public int PatternIndex;
        public int MatchStart;
        public int MatchLength;
        public int TriggerRow;
        public int DistanceLines;
        public string TriggerText;
        public string TriggerFingerprint;
        public string RecoveryFamily;
        public DateTime DueUtc;
    }

    internal sealed class RuleObservation
    {
        private string _triggerToken;
        private DateTime _dueUtc;
        private int _triggerRow;
        private string _recoveryFamily;

        public bool Triggered;
        public bool Ready;
        public bool Busy;
        public TriggerEvent Event;
        public string EvaluationError;
        public FailureAssessment Failure;

        public string RecoveryFamily
        {
            get { return Event != null ? Event.RecoveryFamily : _recoveryFamily; }
            set { _recoveryFamily = value; }
        }

        public string TriggerToken
        {
            get { return Event != null ? Event.TriggerFingerprint : _triggerToken; }
            set { _triggerToken = value; }
        }

        public int TriggerRow
        {
            get { return Event != null ? Event.TriggerRow : _triggerRow; }
            set { _triggerRow = value; }
        }

        public DateTime DueUtc
        {
            get { return Event != null ? Event.DueUtc : _dueUtc; }
            set { _dueUtc = value; }
        }

        public int TriggerMatchStart
        {
            get { return Event != null ? Event.MatchStart : -1; }
        }
    }

    internal sealed class RetryDecision
    {
        public bool Send;
        public string Reason;
        public string TriggerToken;
        public DateTime NextAttemptUtc;
    }

    internal enum RecoveryState
    {
        IdleNoEvent,
        EventWaitingDeadline,
        EventReadyToAttempt,
        CommandWrittenAwaitingOutcome,
        TargetBusyOrProgressing,
        RecoveryConfirmed,
        EventStillPresentReady,
        BackoffWait,
        RecoveryExhausted,
        SessionDisappeared,
        TargetUnreadable,
        AmbiguousFailClosed,
        // CORE-004: in-flight reservation. The session has been selected for
        // a native write and a conservative "attempt reserved" state was
        // durably persisted BEFORE the WriteConsoleInputW call. On restart,
        // the session is treated as a possible-successful-write (refuse
        // immediate re-send) until the snapshot confirms the prior write
        // either succeeded or failed.
        AttemptInFlightReserved
    }

    internal sealed class RetrySessionState
    {
        private RecoveryState _state = RecoveryState.IdleNoEvent;
        private bool _awaitingOutcome;
        private bool _sawBusy;
        private string _triggerToken;
        private string _attemptToken;
        private string _suppressedToken;
        private int _suppressedMatchStart = -1;
        private int _occurrence;
        private int _triggerMatchStart = -1;
        private DateTime _nextAttemptUtc;
        // T-158: the provider's own reset deadline for the CURRENT event, as
        // parsed from the console text ("try again at 4:40 PM" / "try again in
        // 8h 57m"). Backoff after a blocked attempt must never pull the next
        // attempt EARLIER than this: doing so is exactly the "hammer the
        // prompt every 60s for 8 hours" behaviour. Anchored when an event is
        // accepted, never re-derived per poll (see CORE-001).
        private DateTime _eventDueUtc;
        private DateTime _lastObservedUtc;
        private DateTime _lastWriteUtc;
        private int _attemptCount;
        private string _recoveryFamily;
        private string _transportKind;
        private string _endpointIdentity;
        private string _projectIdentity;
        private string _sessionIdentity;

        public RecoveryState State { get { return _state; } }
        public bool Active { get { return _state != RecoveryState.IdleNoEvent && _state != RecoveryState.RecoveryConfirmed; } }
        public bool AwaitingOutcome { get { return _awaitingOutcome; } }
        public bool SawBusy { get { return _sawBusy; } }
        public string TriggerToken { get { return _triggerToken; } }
        public string AttemptToken { get { return _attemptToken; } }
        public string SuppressedToken { get { return _suppressedToken; } }
        public DateTime NextAttemptUtc { get { return _nextAttemptUtc; } }
        public DateTime LastObservedUtc { get { return _lastObservedUtc; } }
        public DateTime LastWriteUtc { get { return _lastWriteUtc; } }
        public int AttemptCount { get { return _attemptCount; } }
        public string SessionIdentity { get { return _sessionIdentity; } }
        // W2L1-003: which transport owns this record. State retirement policy is
        // transport-specific: an OS PID that vanished is real evidence for a
        // console session and MEANINGLESS for an opencode-http session, whose
        // identity carries a synthetic pseudo-PID that can never appear in a
        // Toolhelp snapshot.
        public string TransportKind { get { return _transportKind; } }

        public void SetTransportIdentity(TargetObservation observation)
        {
            if (observation == null) return;
            _transportKind = observation.TransportKind;
            _endpointIdentity = observation.EndpointIdentity;
            _projectIdentity = observation.ProjectIdentity;
            _sessionIdentity = observation.SessionIdentity;
        }

        // PERF-004: durable-state mutation flag. The engine polls this to
        // decide whether a full export+TrySave is required this round. Set on
        // every transition that affects safety-relevant state (Observe,
        // RecordAttempt, ReserveAttempt, CommitAttempt, RestoreFromClone).
        // Cleared by the engine after a successful save.
        private bool _durableDirty;
        public bool IsDurableDirty { get { return _durableDirty; } }
        public void ClearDurableDirty() { _durableDirty = false; }

        public void RestoreFrom(StateRecord record, DateTime nowUtc)
        {
            if (record == null) return;
            _triggerToken = record.TriggerFingerprint;
            _triggerMatchStart = record.TriggerMatchStart;
            _lastObservedUtc = SanitizeHistoricalTime(record.LastObservedUtc, nowUtc);
            _lastWriteUtc = SanitizeHistoricalTime(record.LastWriteUtc, nowUtc);
            _nextAttemptUtc = SanitizeNextAttempt(record.NextAllowedAttemptUtc, nowUtc);
            // T-158: schema v1 carries no separate event-due column, but the
            // persisted nextAllowed is already floored at the event deadline
            // (ApplyEventDueFloor guarantees nextAttempt >= eventDue), so it
            // is a safe conservative floor to restore. Worst case the floor is
            // later than the provider's reset, never earlier.
            _eventDueUtc = _nextAttemptUtc;
            _awaitingOutcome = record.AwaitingOutcome;
            _sawBusy = record.SawBusyAfterWrite;
            _suppressedToken = record.SuppressedFingerprint;
            _suppressedMatchStart = record.SuppressedTriggerRow;
            _occurrence = Math.Max(0, record.Occurrence);
            _triggerMatchStart = record.TriggerMatchStart;
            _attemptCount = Math.Max(0, Math.Min(50, record.AttemptCount));
            _recoveryFamily = record.RecoveryFamily;
            _transportKind = record.TransportKind;
            _endpointIdentity = record.EndpointIdentity;
            _projectIdentity = record.ProjectIdentity;
            _sessionIdentity = record.SessionIdentity;
            _attemptToken = record.TriggerFingerprint;
            if (!String.IsNullOrEmpty(record.RecoveryState))
            {
                try
                {
                    _state = (RecoveryState)Enum.Parse(typeof(RecoveryState), record.RecoveryState, true);
                }
                catch
                {
                    _state = String.IsNullOrEmpty(_triggerToken) ? RecoveryState.IdleNoEvent : RecoveryState.BackoffWait;
                }
            }
            else
            {
                _state = String.IsNullOrEmpty(_triggerToken) ? RecoveryState.IdleNoEvent : RecoveryState.BackoffWait;
            }
        }

        public StateRecord Export(string ruleName, TargetRule rule, ProcessSessionIdentity session, DateTime nowUtc)
        {
            return new StateRecord
            {
                RuleName = ruleName,
                TransportKind = _transportKind,
                EndpointIdentity = _endpointIdentity,
                ProjectIdentity = _projectIdentity,
                SessionIdentity = _sessionIdentity,
                RuleSemanticFingerprint = rule == null ? String.Empty : rule.SemanticFingerprint,
                ProcessId = session != null ? session.ProcessId : 0,
                ProcessStartUtc = session != null ? session.StartTimeUtc : DateTime.MinValue,
                TriggerFingerprint = _triggerToken,
                TriggerMatchStart = _triggerMatchStart,
                LastObservedUtc = _lastObservedUtc == DateTime.MinValue ? nowUtc : _lastObservedUtc,
                LastWriteUtc = _lastWriteUtc,
                NextAllowedAttemptUtc = _nextAttemptUtc,
                AwaitingOutcome = _awaitingOutcome,
                SawBusyAfterWrite = _sawBusy,
                SuppressedFingerprint = _suppressedToken,
                SuppressedTriggerRow = _suppressedMatchStart,
                Occurrence = _occurrence,
                AttemptCount = _attemptCount,
                RecoveryFamily = _recoveryFamily,
                RecoveryState = _state.ToString()
            };
        }

        public RetryDecision Observe(RuleObservation observation, TargetRule rule, DateTime nowUtc)
        {
            _lastObservedUtc = nowUtc;
            _durableDirty = true;

            // W2-002: possible-successful-write (AttemptInFlightReserved restored
            // from a crash window) and partial-write ambiguity (AmbiguousFailClosed)
            // must NEVER be authorized by elapsed retry time. They are exclusive
            // locked states: they may only leave through console evidence proving
            // the previous attempt's outcome, and this branch must run before the
            // generic trigger-token-change / send logic.
            if (_state == RecoveryState.AttemptInFlightReserved || _state == RecoveryState.AmbiguousFailClosed)
            {
                if (!observation.Triggered)
                {
                    // Previous write consumed the trigger: recovery observed.
                    _suppressedToken = _attemptToken;
                    _suppressedMatchStart = _triggerMatchStart;
                    _state = RecoveryState.RecoveryConfirmed;
                    _attemptCount = 0;
                    _recoveryFamily = observation.RecoveryFamily;
                    _awaitingOutcome = false;
                    _sawBusy = false;
                    _eventDueUtc = DateTime.MinValue;
                    return Decision(false, "ambiguous write resolved: trigger cleared");
                }

                if (observation.Busy || !observation.Ready)
                {
                    _sawBusy = true;
                    _state = RecoveryState.TargetBusyOrProgressing;
                    return Decision(false, "ambiguous write unresolved: target busy/progressing");
                }

                if (!String.Equals(observation.TriggerToken, _triggerToken, StringComparison.Ordinal))
                {
                    // A NEW occurrence appeared (different trigger identity). The
                    // old ambiguous write is superseded; the new event starts a
                    // fresh retry lifecycle.
                    _triggerToken = observation.TriggerToken;
                    _triggerMatchStart = observation.TriggerMatchStart;
                    _attemptToken = null;
                    _awaitingOutcome = false;
                    _sawBusy = false;
                    _attemptCount = 0;
                    _recoveryFamily = observation.RecoveryFamily;
                    _nextAttemptUtc = observation.DueUtc;
                    _eventDueUtc = observation.DueUtc;
                    _state = nowUtc < _nextAttemptUtc ? RecoveryState.EventWaitingDeadline : RecoveryState.EventReadyToAttempt;
                    if (nowUtc < _nextAttemptUtc)
                    {
                        return Decision(false, "new occurrence supersedes ambiguous write (waiting)");
                    }
                }
                else
                {
                    _state = RecoveryState.AmbiguousFailClosed;
                    return Decision(false, "ambiguous write unresolved (same trigger)");
                }
            }

            if (!observation.Triggered)
            {
                if ((_state == RecoveryState.CommandWrittenAwaitingOutcome || _state == RecoveryState.TargetBusyOrProgressing) && observation.Ready)
                {
                    _suppressedToken = _attemptToken;
                    _suppressedMatchStart = _triggerMatchStart;
                    _state = RecoveryState.RecoveryConfirmed;
                    _attemptCount = 0;
                    _awaitingOutcome = false;
                    _sawBusy = false;
                    _eventDueUtc = DateTime.MinValue;
                    return Decision(false, "recovery confirmed; trigger cleared");
                }

                if (!_awaitingOutcome)
                {
                    _state = RecoveryState.IdleNoEvent;
                    _attemptCount = 0;
                    _triggerToken = null;
                    _attemptToken = null;
                    _nextAttemptUtc = DateTime.MinValue;
                    _eventDueUtc = DateTime.MinValue;
                }

                _suppressedToken = null;
                return Decision(false, "no trigger");
            }

            if (_suppressedToken != null && String.Equals(_suppressedToken, observation.TriggerToken, StringComparison.Ordinal))
            {
                if (_suppressedMatchStart < 0 || observation.TriggerMatchStart < 0 || observation.TriggerMatchStart == _suppressedMatchStart)
                {
                    return Decision(false, "stale trigger suppressed after recovery");
                }
                _occurrence++;
                _suppressedToken = null;
                _suppressedMatchStart = -1;
                _triggerMatchStart = observation.TriggerMatchStart;
                _attemptToken = null;
                _attemptCount = 0;
                _recoveryFamily = observation.RecoveryFamily;
                _awaitingOutcome = false;
                _nextAttemptUtc = observation.DueUtc;
                _eventDueUtc = observation.DueUtc;
                _state = nowUtc < _nextAttemptUtc ? RecoveryState.EventWaitingDeadline : RecoveryState.EventReadyToAttempt;
            }

            if (!String.Equals(_triggerToken, observation.TriggerToken, StringComparison.Ordinal))
            {
                bool carryRecoveryBudget = _attemptCount > 0 &&
                    !String.IsNullOrEmpty(_recoveryFamily) &&
                    String.Equals(_recoveryFamily, observation.RecoveryFamily, StringComparison.Ordinal);
                _triggerToken = observation.TriggerToken;
                _attemptToken = null;
                _awaitingOutcome = false;
                _sawBusy = false;
                _eventDueUtc = observation.DueUtc;
                if (!carryRecoveryBudget)
                {
                    _attemptCount = 0;
                    _nextAttemptUtc = observation.DueUtc;
                }
                _recoveryFamily = observation.RecoveryFamily;
                _state = nowUtc < _nextAttemptUtc ? RecoveryState.EventWaitingDeadline : RecoveryState.EventReadyToAttempt;
            }

            if (_state == RecoveryState.CommandWrittenAwaitingOutcome)
            {
                if (observation.Busy || !observation.Ready)
                {
                    _sawBusy = true;
                    _state = RecoveryState.TargetBusyOrProgressing;
                    return Decision(false, "target busy or progressing");
                }

                if (observation.Ready)
                {
                    if (_sawBusy && String.Equals(_attemptToken, observation.TriggerToken, StringComparison.Ordinal))
                    {
                        _suppressedToken = observation.TriggerToken;
                        _state = RecoveryState.RecoveryConfirmed;
                        _attemptCount = 0;
                        _awaitingOutcome = false;
                        _sawBusy = false;
                        _eventDueUtc = DateTime.MinValue;
                        return Decision(false, "recovery observed; old trigger suppressed");
                    }

                    _state = RecoveryState.BackoffWait;
                    if (_attemptCount >= rule.SafeMaximumAttemptsPerEvent)
                    {
                        _state = RecoveryState.RecoveryExhausted;
                        return Decision(false, "recovery exhausted (" + _attemptCount + " attempts)");
                    }
                }
            }

            if (_state == RecoveryState.TargetBusyOrProgressing)
            {
                if (observation.Busy || !observation.Ready)
                {
                    return Decision(false, "target busy or progressing");
                }

                if (observation.Ready && String.Equals(_attemptToken, observation.TriggerToken, StringComparison.Ordinal))
                {
                    _suppressedToken = observation.TriggerToken;
                    _state = RecoveryState.RecoveryConfirmed;
                    _attemptCount = 0;
                    _awaitingOutcome = false;
                    _sawBusy = false;
                    _eventDueUtc = DateTime.MinValue;
                    return Decision(false, "recovery observed; old trigger suppressed");
                }
            }

            if (_state == RecoveryState.RecoveryExhausted)
            {
                return Decision(false, "recovery exhausted (" + _attemptCount + " attempts)");
            }

            if (!_awaitingOutcome && _attemptCount >= rule.SafeMaximumAttemptsPerEvent)
            {
                _state = RecoveryState.RecoveryExhausted;
                return Decision(false, "recovery exhausted (" + _attemptCount + " attempts)");
            }

            if (observation.Busy)
            {
                return Decision(false, "target busy");
            }

            if (!observation.Ready)
            {
                return Decision(false, "input prompt is not empty and ready");
            }

            if (nowUtc < _nextAttemptUtc)
            {
                if (_state != RecoveryState.EventWaitingDeadline)
                {
                    _state = RecoveryState.BackoffWait;
                }
                return Decision(false, "cooldown");
            }

            _state = RecoveryState.EventReadyToAttempt;
            return new RetryDecision
            {
                Send = true,
                Reason = "trigger active and prompt ready",
                TriggerToken = observation.TriggerToken,
                NextAttemptUtc = _nextAttemptUtc
            };
        }

        public void RecordAttempt(bool inputWritten, string triggerToken, TargetRule rule, DateTime nowUtc)
        {
            RecordAttempt(inputWritten, inputWritten, triggerToken, rule, nowUtc);
        }

        public void RecordAttempt(bool inputWritten, bool nativeWriteAttempted, string triggerToken, TargetRule rule, DateTime nowUtc)
        {
            _durableDirty = true;
            _attemptToken = triggerToken;
            _sawBusy = false;
            if (nativeWriteAttempted)
            {
                _attemptCount++;
            }
            if (inputWritten)
            {
                _lastWriteUtc = nowUtc;
                _awaitingOutcome = true;
                _state = RecoveryState.CommandWrittenAwaitingOutcome;
            }
            else
            {
                _awaitingOutcome = false;
                _state = RecoveryState.BackoffWait;
            }

            int baseInterval = rule.SafeRetryIntervalSeconds;
            double multiplier = Math.Pow(rule.SafeBackoffMultiplier, Math.Max(0, _attemptCount - 1));
            int backoffSeconds = (int)Math.Min(rule.SafeMaximumRetryIntervalSeconds, Math.Max(baseInterval, baseInterval * multiplier));
            _nextAttemptUtc = ApplyEventDueFloor(nowUtc.AddSeconds(backoffSeconds));
        }

        // CORE-004: mark the session as selected for a native write BEFORE the
        // WriteConsoleInputW call. The caller must durably persist the state
        // immediately after this call; if the persist fails the caller must
        // not perform the native write. On restart, an AttemptInFlightReserved
        // session is treated as a possible-successful-write and is NOT
        // immediately re-attempted.
        public void ReserveAttempt(string triggerToken, TargetRule rule, DateTime nowUtc)
        {
            _durableDirty = true;
            if (String.IsNullOrEmpty(_triggerToken))
            {
                _triggerToken = triggerToken;
            }
            _attemptToken = triggerToken;
            _sawBusy = false;
            _attemptCount++;
            _awaitingOutcome = false;
            _state = RecoveryState.AttemptInFlightReserved;
            int baseInterval = rule.SafeRetryIntervalSeconds;
            double multiplier = Math.Pow(rule.SafeBackoffMultiplier, Math.Max(0, _attemptCount - 1));
            int backoffSeconds = (int)Math.Min(rule.SafeMaximumRetryIntervalSeconds, Math.Max(baseInterval, baseInterval * multiplier));
            _nextAttemptUtc = ApplyEventDueFloor(nowUtc.AddSeconds(backoffSeconds));
            _lastWriteUtc = nowUtc;
        }

        // CORE-004: refine a reserved session to its terminal outcome AFTER
        // the WriteConsoleInputW call returns. On restart a session still
        // sitting in AttemptInFlightReserved must remain in that state until
        // the next Observe cycle, which is what makes the crash window safe.
        // W2-002: a partial accepted write (AmbiguousOrPartialInput) enters a
        // durable fail-closed state and may only leave through console evidence.
        public void CommitAttempt(NativeWriteOutcome outcome, TargetRule rule, DateTime nowUtc)
        {
            _durableDirty = true;
            _sawBusy = false;
            if (outcome == NativeWriteOutcome.CompleteInputCommitted)
            {
                _lastWriteUtc = nowUtc;
                _awaitingOutcome = true;
                _state = RecoveryState.CommandWrittenAwaitingOutcome;
            }
            else if (outcome == NativeWriteOutcome.AmbiguousOrPartialInput)
            {
                // Do NOT advance the retry clock or permit a retry: the buffer
                // state of the previous write is unknown.
                _awaitingOutcome = true;
                _state = RecoveryState.AmbiguousFailClosed;
                return;
            }
            else
            {
                _awaitingOutcome = false;
                _state = RecoveryState.BackoffWait;
            }
            int baseInterval = rule.SafeRetryIntervalSeconds;
            double multiplier = Math.Pow(rule.SafeBackoffMultiplier, Math.Max(0, _attemptCount - 1));
            int backoffSeconds = (int)Math.Min(rule.SafeMaximumRetryIntervalSeconds, Math.Max(baseInterval, baseInterval * multiplier));
            _nextAttemptUtc = ApplyEventDueFloor(nowUtc.AddSeconds(backoffSeconds));
        }

        // T-158: never schedule an attempt EARLIER than the provider's own
        // stated reset deadline for the current event. Blind exponential
        // backoff from "now" is what produced the hammer-the-prompt loop:
        // a parsed "try again at 14:19" was overwritten by now+60s on every
        // blocked attempt. The floor keeps the event's absolute deadline
        // authoritative while backoff can still push the attempt LATER.
        private DateTime ApplyEventDueFloor(DateTime candidateUtc)
        {
            if (_eventDueUtc == DateTime.MinValue)
            {
                return candidateUtc;
            }
            return candidateUtc < _eventDueUtc ? _eventDueUtc : candidateUtc;
        }

        private static DateTime SanitizeHistoricalTime(DateTime value, DateTime nowUtc)
        {
            if (value == DateTime.MinValue)
            {
                return value;
            }
            if (value > nowUtc.AddMinutes(5))
            {
                return nowUtc;
            }
            if (value < nowUtc.AddDays(-30))
            {
                return DateTime.MinValue;
            }
            return value;
        }

        public RetrySessionState Clone()
        {
            var copy = new RetrySessionState();
            copy._state = _state;
            copy._awaitingOutcome = _awaitingOutcome;
            copy._sawBusy = _sawBusy;
            copy._triggerToken = _triggerToken;
            copy._attemptToken = _attemptToken;
            copy._suppressedToken = _suppressedToken;
            copy._suppressedMatchStart = _suppressedMatchStart;
            copy._occurrence = _occurrence;
            copy._triggerMatchStart = _triggerMatchStart;
            copy._nextAttemptUtc = _nextAttemptUtc;
            copy._eventDueUtc = _eventDueUtc;
            copy._lastObservedUtc = _lastObservedUtc;
            copy._lastWriteUtc = _lastWriteUtc;
            copy._attemptCount = _attemptCount;
            copy._recoveryFamily = _recoveryFamily;
            copy._transportKind = _transportKind;
            copy._endpointIdentity = _endpointIdentity;
            copy._projectIdentity = _projectIdentity;
            copy._sessionIdentity = _sessionIdentity;
            return copy;
        }

        public void RestoreFromClone(RetrySessionState source)
        {
            if (source == null) return;
            _durableDirty = true;
            _state = source._state;
            _awaitingOutcome = source._awaitingOutcome;
            _sawBusy = source._sawBusy;
            _triggerToken = source._triggerToken;
            _attemptToken = source._attemptToken;
            _suppressedToken = source._suppressedToken;
            _suppressedMatchStart = source._suppressedMatchStart;
            _occurrence = source._occurrence;
            _triggerMatchStart = source._triggerMatchStart;
            _nextAttemptUtc = source._nextAttemptUtc;
            _eventDueUtc = source._eventDueUtc;
            _lastObservedUtc = source._lastObservedUtc;
            _lastWriteUtc = source._lastWriteUtc;
            _attemptCount = source._attemptCount;
            _recoveryFamily = source._recoveryFamily;
            _transportKind = source._transportKind;
            _endpointIdentity = source._endpointIdentity;
            _projectIdentity = source._projectIdentity;
            _sessionIdentity = source._sessionIdentity;
        }

        private static DateTime SanitizeNextAttempt(DateTime value, DateTime nowUtc)
        {
            if (value == DateTime.MinValue)
            {
                return value;
            }
            // W2-003: one legitimate retry horizon, shared with parser validation
            // and durable retention. A future deadline inside that horizon keeps
            // its exact absolute value across restart; only demonstrably corrupt
            // out-of-contract timestamps are clamped.
            DateTime maximum = nowUtc.AddDays(RetryConstants.MaximumRetryHorizonDays);
            return value > maximum ? maximum : value;
        }

        private RetryDecision Decision(bool send, string reason)
        {
            return new RetryDecision
            {
                Send = send,
                Reason = reason,
                TriggerToken = _triggerToken,
                NextAttemptUtc = _nextAttemptUtc
            };
        }
    }

    internal static class RuleMatcher
    {
        public static RuleObservation Inspect(TargetRule rule, ConsoleSnapshot snapshot, DateTime nowUtc)
        {
            return Inspect(rule, snapshot, nowUtc, null);
        }

        /// <summary>
        /// PERF-004: <paramref name="precomputedFailure"/> lets the caller supply
        /// the rule-INDEPENDENT failure assessment for this exact immutable
        /// snapshot. Failure classification does not depend on any rule field, so
        /// when several rules inspect one console snapshot in the same poll the
        /// classifier runs once instead of once per rule. Passing null keeps the
        /// original behaviour.
        /// </summary>
        public static RuleObservation Inspect(TargetRule rule, ConsoleSnapshot snapshot, DateTime nowUtc, FailureAssessment precomputedFailure)
        {
            if (rule == null || snapshot == null || String.IsNullOrEmpty(snapshot.Text))
            {
                return new RuleObservation
                {
                    Triggered = false,
                    Ready = false,
                    Busy = false
                };
            }

            if (rule.CompiledTriggerPatterns == null || rule.CompiledReadyPatterns == null || rule.CompiledBusyPatterns == null)
            {
                rule.CompileRegexes();
            }

            Match bestMatch = null;
            int bestPatternIndex = -1;

            try
            {
                for (int index = 0; index < rule.CompiledTriggerPatterns.Length; index++)
                {
                    Regex rx = rule.CompiledTriggerPatterns[index];
                    if (rx == null) continue;
                    Match candidate = rx.Match(snapshot.Text);
                    if (!candidate.Success)
                    {
                        continue;
                    }
                    while (candidate.Success)
                    {
                        Match next = candidate.NextMatch();
                        if (!next.Success)
                        {
                            break;
                        }
                        candidate = next;
                    }
                    if (bestMatch == null || candidate.Index > bestMatch.Index)
                    {
                        bestMatch = candidate;
                        bestPatternIndex = index;
                    }
                }

                string inputLine = String.IsNullOrWhiteSpace(snapshot.CursorLine) ? LastNonEmptyLine(snapshot.Text) : snapshot.CursorLine;
                bool ready = MatchesAny(inputLine, rule.CompiledReadyPatterns) || ContainsTypedCommand(inputLine, rule.Command);
                bool busy = rule.CompiledBusyPatterns != null && rule.CompiledBusyPatterns.Length > 0 && MatchesAny(ExtractCursorTail(snapshot.Text, 5), rule.CompiledBusyPatterns);

                FailureAssessment failure = precomputedFailure ?? FailureIntelligence.Classify(snapshot.Text);
                bool failureSelected = false;
                int matchStart = bestMatch == null ? -1 : bestMatch.Index;
                int matchLength = bestMatch == null ? 0 : bestMatch.Length;
                string matchValue = bestMatch == null ? String.Empty : bestMatch.Value;
                string recoveryFamily = null;

                if (failure.IsKnown)
                {
                    bool overlapsConfigured = bestMatch != null && SameLine(snapshot.Text, failure.MatchStart, bestMatch.Index);

                    if (bestMatch == null && failure.IsAutomaticRecovery)
                    {
                        failureSelected = true;
                    }
                    if (bestMatch != null && failure.IsAutomaticRecovery &&
                        failure.RecommendedAction != FailureRecoveryAction.WAIT_FOR_RESET && overlapsConfigured)
                    {
                        // Keep the operator regex's exact token for the pre-send
                        // safety reread, but share the retry budget family.
                        recoveryFamily = "transient-provider-recovery";
                    }
                    if (bestMatch != null && failure.MatchStart > bestMatch.Index && !overlapsConfigured)
                    {
                        // A newer recognized permanent failure blocks an older
                        // transient trigger. An explicit trigger overlapping the
                        // same envelope still wins: configuration is operator policy.
                        if (failure.IsAutomaticRecovery)
                        {
                            failureSelected = true;
                        }
                        else
                        {
                            bestMatch = null;
                            matchStart = -1;
                        }
                    }
                }

                if (failureSelected)
                {
                    bestPatternIndex = -((int)failure.Class + 1);
                    matchStart = failure.MatchStart;
                    matchLength = failure.MatchLength;
                    matchValue = failure.FingerprintMaterial;
                    if (failure.RecommendedAction != FailureRecoveryAction.WAIT_FOR_RESET)
                    {
                        recoveryFamily = "transient-provider-recovery";
                    }
                }

                if (bestMatch == null && !failureSelected)
                {
                    return new RuleObservation
                    {
                        Triggered = false,
                        Ready = ready,
                        Busy = busy,
                        Failure = failure
                    };
                }

                int relativeRow = CountNewLines(snapshot.Text, matchStart);
                int triggerRow = snapshot.StartRow + relativeRow;
                int distance = snapshot.CursorRow - triggerRow;
                bool nearCursor = distance >= 0 && distance <= Math.Max(1, rule.MaximumTriggerDistanceLines);

                // Bounded event context: inspect up to 6 lines starting from the trigger line
                string eventContext = ExtractLinesAfterMatch(snapshot.Text, matchStart, 6);

                DateTime due = nowUtc.AddSeconds(rule.SafeInitialDelaySeconds);
                bool parsedRetryTime = false;
                // SRC-021: parseRetryTime is a quota/reset affordance. A
                // RETRY_SAME transient failure (internal_error, overload,
                // timeout, network, stream) must keep the bounded retry backoff
                // instead of being pinned to a far-future clock parsed from a
                // nearby usage-limit line. Quota/rate resets and unrecognized
                // triggers keep honoring a visible retry deadline.
                if (rule.ParseRetryTime && failure.RecommendedAction != FailureRecoveryAction.RETRY_SAME)
                {
                    DateTime parsed;
                    if (RetryTimeParser.TryParseDue(eventContext, nowUtc.ToLocalTime(), out parsed))
                    {
                        parsedRetryTime = true;
                        due = parsed.ToUniversalTime().AddSeconds(3);
                        if (due < nowUtc)
                        {
                            due = nowUtc;
                        }
                    }
                }

                if (failureSelected && failure.RecommendedAction == FailureRecoveryAction.WAIT_FOR_RESET && !parsedRetryTime)
                {
                    // Quota exhaustion without a provider reset time cannot be
                    // repaired by blind retries. Keep it classified and visible,
                    // but do not start a continuation lifecycle.
                    return new RuleObservation
                    {
                        Triggered = false,
                        Ready = ready,
                        Busy = busy,
                        Failure = failure
                    };
                }

                string normalizedContext = failureSelected
                    ? failure.FingerprintMaterial
                    : NormalizeEventContext(matchValue);
                string tokenPrefix = failureSelected ? "failure:" + failure.Class : bestPatternIndex.ToString(CultureInfo.InvariantCulture);
                string token = rule.Name + ":" + tokenPrefix + ":" + StableHash(normalizedContext).ToString("X8", CultureInfo.InvariantCulture);
                // CORE-001: event identity is STABLE; the parsed due is carried separately
                // via TriggerEvent.DueUtc and is anchored in RetrySessionState on first
                // acceptance, not re-derived from every poll's nowLocal. A relative-duration
                // event observed across polls (e.g. "Try again in 8h 57m" still scrolling)
                // must keep one fingerprint so the same event is not treated as a new
                // occurrence every poll. The deadline advancing with nowLocal was the
                // bug. Byte-identical later occurrences after recovery are still
                // distinguished by _suppressedToken at Observe (line ~178).

                var triggerEvent = new TriggerEvent
                {
                    RuleName = rule.Name,
                    PatternIndex = bestPatternIndex,
                    MatchStart = matchStart,
                    MatchLength = matchLength,
                    TriggerRow = triggerRow,
                    DistanceLines = distance,
                    TriggerText = matchValue,
                    TriggerFingerprint = token,
                    RecoveryFamily = recoveryFamily,
                    DueUtc = due
                };

                return new RuleObservation
                {
                    Triggered = nearCursor,
                    Ready = ready,
                    Busy = busy,
                    Event = triggerEvent,
                    Failure = failure
                };
            }
            catch (RegexMatchTimeoutException)
            {
                // Pathological pattern timed out -> fail closed
                return new RuleObservation
                {
                    Triggered = false,
                    Ready = false,
                    Busy = true,
                    EvaluationError = "regex_timeout"
                };
            }
            catch (Exception)
            {
                // Regex evaluation error -> fail closed
                return new RuleObservation
                {
                    Triggered = false,
                    Ready = false,
                    Busy = true,
                    EvaluationError = "rule_evaluation_failed"
                };
            }
        }

        private static bool ContainsTypedCommand(string line, string command)
        {
            string value = (line ?? String.Empty).Trim();
            string expected = (command ?? String.Empty).Trim();
            if (expected.Length == 0)
            {
                return false;
            }
            value = value.TrimStart('>', '›', '?').Trim();
            return String.Equals(value, expected, StringComparison.Ordinal);
        }

        private static string LastNonEmptyLine(string text)
        {
            string[] lines = (text ?? String.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            for (int index = lines.Length - 1; index >= 0; index--)
            {
                if (!String.IsNullOrWhiteSpace(lines[index]))
                {
                    return lines[index];
                }
            }
            return String.Empty;
        }

        private static string NormalizeEventContext(string text)
        {
            if (String.IsNullOrWhiteSpace(text))
            {
                return String.Empty;
            }

            string[] lines = text.Replace("\r", String.Empty).Split('\n');
            for (int index = 0; index < lines.Length; index++)
            {
                lines[index] = Regex.Replace(lines[index].Trim(), @"\s+", " ");
            }
            return String.Join("\n", lines).Trim();
        }

        private static string ExtractCursorTail(string text, int lineCount)
        {
            if (String.IsNullOrEmpty(text) || lineCount <= 0)
            {
                return String.Empty;
            }
            int newlineCount = 0;
            int start = 0;
            for (int index = text.Length - 1; index >= 0; index--)
            {
                if (text[index] == '\n')
                {
                    newlineCount++;
                    if (newlineCount == lineCount)
                    {
                        start = index + 1;
                        break;
                    }
                }
            }
            return text.Substring(start);
        }

        private static string ExtractLinesAfterMatch(string text, int matchIndex, int maxLines)
        {
            if (String.IsNullOrEmpty(text) || matchIndex < 0 || matchIndex >= text.Length)
            {
                return String.Empty;
            }

            int lineStart = text.LastIndexOf('\n', matchIndex);
            lineStart = lineStart < 0 ? 0 : lineStart + 1;

            int linesFound = 0;
            int pos = lineStart;
            while (pos < text.Length && linesFound < maxLines)
            {
                if (text[pos] == '\n')
                {
                    linesFound++;
                }
                pos++;
            }

            return text.Substring(lineStart, pos - lineStart);
        }

        private static bool MatchesAny(string text, Regex[] regexes)
        {
            if (regexes == null || regexes.Length == 0)
            {
                return false;
            }

            string input = text ?? String.Empty;
            foreach (Regex rx in regexes)
            {
                if (rx != null && rx.IsMatch(input))
                {
                    return true;
                }
            }
            return false;
        }

        private static int CountNewLines(string text, int end)
        {
            int count = 0;
            for (int index = 0; index < end && index < text.Length; index++)
            {
                if (text[index] == '\n')
                {
                    count++;
                }
            }
            return count;
        }

        private static bool SameLine(string text, int first, int second)
        {
            if (String.IsNullOrEmpty(text) || first < 0 || second < 0)
            {
                return false;
            }
            int start = Math.Min(first, second);
            int end = Math.Max(first, second);
            return text.IndexOf('\n', start, end - start) < 0;
        }

        private static uint StableHash(string text)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char character in text)
                {
                    hash ^= character;
                    hash *= 16777619;
                }
                return hash;
            }
        }
    }

    internal static class RetryConstants
    {
        internal const int MaximumRetryHorizonDays = 366;
    }

    internal static class RetryTimeParser
    {
        private static readonly Regex AtClock = new Regex(
            @"(?i)(?:try\s+)?again\s+at\s+(?:(?<month>[A-Za-z]+)\s+(?<day>\d{1,2})(?:st|nd|rd|th)?,?\s*(?<year>\d{4})?,?\s*)?(?<clock>\d{1,2}:\d{2}(?:\s*[AP]M)?)",
            RegexOptions.CultureInvariant,
            TargetRule.DefaultRegexTimeout);

        private static readonly Regex InDuration = new Regex(
            @"(?i)try again in\s+(?<count>\d+)\s*(?<unit>seconds?|minutes?|hours?)",
            RegexOptions.CultureInvariant,
            TargetRule.DefaultRegexTimeout);

        private static readonly Regex InCompactDuration = new Regex(
            @"(?i)try again in\s+(?:(?<hours>\d+)\s*h(?:ours?)?)?(?:\s*(?<minutes>\d+)\s*m(?:in(?:ute)?s?)?)?(?:\s*(?<seconds>\d+)\s*s(?:ec(?:ond)?s?)?)?",
            RegexOptions.CultureInvariant,
            TargetRule.DefaultRegexTimeout);

        public static bool TryParseDue(string text, DateTime nowLocal, out DateTime dueLocal)
        {
            dueLocal = DateTime.MinValue;
            Match compactDuration = InCompactDuration.Match(text ?? String.Empty);
            if (compactDuration.Success &&
                (compactDuration.Groups["hours"].Success || compactDuration.Groups["minutes"].Success || compactDuration.Groups["seconds"].Success))
            {
                int hours;
                int minutes;
                int seconds;
                if (!TryParseDurationPart(compactDuration.Groups["hours"].Value, out hours) ||
                    !TryParseDurationPart(compactDuration.Groups["minutes"].Value, out minutes) ||
                    !TryParseDurationPart(compactDuration.Groups["seconds"].Value, out seconds))
                {
                    return false;
                }

                long totalSeconds = (hours * 3600L) + (minutes * 60L) + seconds;
                return TryAddDuration(nowLocal, totalSeconds, out dueLocal);
            }

            Match duration = InDuration.Match(text ?? String.Empty);
            if (duration.Success)
            {
                int count;
                if (!Int32.TryParse(duration.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out count))
                {
                    return false;
                }

                string unit = duration.Groups["unit"].Value.ToLowerInvariant();
                long totalSeconds;
                if (unit.StartsWith("hour", StringComparison.Ordinal))
                {
                    totalSeconds = count * 3600L;
                }
                else if (unit.StartsWith("minute", StringComparison.Ordinal))
                {
                    totalSeconds = count * 60L;
                }
                else
                {
                    totalSeconds = count;
                }
                return TryAddDuration(nowLocal, totalSeconds, out dueLocal);
            }

            Match clock = AtClock.Match(text ?? String.Empty);
            if (!clock.Success)
            {
                return false;
            }

            DateTime parsed;
            string value = clock.Groups["clock"].Value.Replace(" ", String.Empty);
            string[] formats = { "h:mmtt", "hh:mmtt", "H:mm", "HH:mm" };
            if (!DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
            {
                return false;
            }

            int monthNumber = 0;
            bool monthRequested = clock.Groups["month"].Success;
            if (monthRequested)
            {
                string monthStr = clock.Groups["month"].Value;
                DateTime monthDt;
                if (DateTime.TryParseExact(monthStr, new[] { "MMMM", "MMM" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out monthDt))
                {
                    monthNumber = monthDt.Month;
                }
                else
                {
                    return false;
                }
            }
            int dayNumber = 0;
            bool dayRequested = clock.Groups["day"].Success;
            if (dayRequested)
            {
                if (!Int32.TryParse(clock.Groups["day"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out dayNumber) || dayNumber < 1 || dayNumber > 31)
                {
                    return false;
                }
            }
            int yearNumber = nowLocal.Year;
            bool yearRequested = clock.Groups["year"].Success;
            if (yearRequested)
            {
                int y;
                if (Int32.TryParse(clock.Groups["year"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out y) && y >= 2024 && y <= 2040)
                {
                    yearNumber = y;
                }
                else
                {
                    return false;
                }
            }
            if (monthRequested ^ dayRequested)
            {
                return false;
            }
            if (monthRequested && dayRequested)
            {
                DateTime specificDate;
                try
                {
                    specificDate = new DateTime(yearNumber, monthNumber, dayNumber, parsed.Hour, parsed.Minute, parsed.Second, DateTimeKind.Local);
                }
                catch
                {
                    return false;
                }
                if (yearRequested)
                {
                    if (specificDate < nowLocal.AddMinutes(-1))
                    {
                        dueLocal = nowLocal;
                    }
                    else
                    {
                        dueLocal = specificDate;
                    }
                    return true;
                }
                DateTime nextYear = specificDate.AddYears(1);
                if (specificDate >= nowLocal.AddMinutes(-1))
                {
                    dueLocal = specificDate;
                }
                else if (nextYear >= nowLocal.AddMinutes(-1))
                {
                    dueLocal = nextYear;
                }
                else
                {
                    dueLocal = nextYear;
                }
                return true;
            }

            DateTime candidate = nowLocal.Date.Add(parsed.TimeOfDay);
            if (candidate >= nowLocal.AddMinutes(-1))
            {
                dueLocal = candidate;
                return true;
            }

            TimeSpan elapsed = nowLocal - candidate;
            dueLocal = elapsed <= TimeSpan.FromHours(12) ? nowLocal : candidate.AddDays(1);
            return true;
        }

        private static bool TryParseDurationPart(string value, out int parsed)
        {
            if (String.IsNullOrEmpty(value))
            {
                parsed = 0;
                return true;
            }
            return Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed);
        }

        private static bool TryAddDuration(DateTime nowLocal, long totalSeconds, out DateTime dueLocal)
        {
            dueLocal = DateTime.MinValue;
            const long MaximumDurationSeconds = RetryConstants.MaximumRetryHorizonDays * 24L * 60L * 60L;
            if (totalSeconds <= 0 || totalSeconds > MaximumDurationSeconds)
            {
                return false;
            }

            try
            {
                dueLocal = nowLocal.AddSeconds(totalSeconds);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }
    }
}
