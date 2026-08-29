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
        public DateTime DueUtc;
    }

    internal sealed class RuleObservation
    {
        private string _triggerToken;
        private DateTime _dueUtc;
        private int _triggerRow;

        public bool Triggered;
        public bool Ready;
        public bool Busy;
        public TriggerEvent Event;
        public string EvaluationError;

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
        private DateTime _nextAttemptUtc;
        private DateTime _lastObservedUtc;
        private DateTime _lastWriteUtc;
        private int _attemptCount;

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

        public void RestoreFrom(StateRecord record, DateTime nowUtc)
        {
            if (record == null) return;
            _triggerToken = record.TriggerFingerprint;
            _lastObservedUtc = SanitizeHistoricalTime(record.LastObservedUtc, nowUtc);
            _lastWriteUtc = SanitizeHistoricalTime(record.LastWriteUtc, nowUtc);
            _nextAttemptUtc = SanitizeNextAttempt(record.NextAllowedAttemptUtc, nowUtc);
            _awaitingOutcome = record.AwaitingOutcome;
            _sawBusy = record.SawBusyAfterWrite;
            _suppressedToken = record.SuppressedFingerprint;
            _attemptCount = Math.Max(0, Math.Min(50, record.AttemptCount));
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
                RuleSemanticFingerprint = rule == null ? String.Empty : rule.SemanticFingerprint,
                ProcessId = session != null ? session.ProcessId : 0,
                ProcessStartUtc = session != null ? session.StartTimeUtc : DateTime.MinValue,
                TriggerFingerprint = _triggerToken,
                LastObservedUtc = _lastObservedUtc == DateTime.MinValue ? nowUtc : _lastObservedUtc,
                LastWriteUtc = _lastWriteUtc,
                NextAllowedAttemptUtc = _nextAttemptUtc,
                AwaitingOutcome = _awaitingOutcome,
                SawBusyAfterWrite = _sawBusy,
                SuppressedFingerprint = _suppressedToken,
                AttemptCount = _attemptCount,
                RecoveryState = _state.ToString()
            };
        }

        public RetryDecision Observe(RuleObservation observation, TargetRule rule, DateTime nowUtc)
        {
            _lastObservedUtc = nowUtc;

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
                    _state = RecoveryState.RecoveryConfirmed;
                    _attemptCount = 0;
                    _awaitingOutcome = false;
                    _sawBusy = false;
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
                    _attemptToken = null;
                    _awaitingOutcome = false;
                    _sawBusy = false;
                    _attemptCount = 0;
                    _nextAttemptUtc = observation.DueUtc;
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
                    _state = RecoveryState.RecoveryConfirmed;
                    _attemptCount = 0;
                    _awaitingOutcome = false;
                    _sawBusy = false;
                    return Decision(false, "recovery confirmed; trigger cleared");
                }

                if (!_awaitingOutcome)
                {
                    _state = RecoveryState.IdleNoEvent;
                    _attemptCount = 0;
                    _triggerToken = null;
                    _attemptToken = null;
                    _nextAttemptUtc = DateTime.MinValue;
                }

                _suppressedToken = null;
                return Decision(false, "no trigger");
            }

            if (_suppressedToken != null && String.Equals(_suppressedToken, observation.TriggerToken, StringComparison.Ordinal))
            {
                return Decision(false, "stale trigger suppressed after recovery");
            }

            if (!String.Equals(_triggerToken, observation.TriggerToken, StringComparison.Ordinal))
            {
                _triggerToken = observation.TriggerToken;
                _attemptToken = null;
                _awaitingOutcome = false;
                _sawBusy = false;
                _attemptCount = 0;
                _nextAttemptUtc = observation.DueUtc;
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
            _nextAttemptUtc = nowUtc.AddSeconds(backoffSeconds);
        }

        // CORE-004: mark the session as selected for a native write BEFORE the
        // WriteConsoleInputW call. The caller must durably persist the state
        // immediately after this call; if the persist fails the caller must
        // not perform the native write. On restart, an AttemptInFlightReserved
        // session is treated as a possible-successful-write and is NOT
        // immediately re-attempted.
        public void ReserveAttempt(string triggerToken, TargetRule rule, DateTime nowUtc)
        {
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
            _nextAttemptUtc = nowUtc.AddSeconds(backoffSeconds);
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
            _nextAttemptUtc = nowUtc.AddSeconds(backoffSeconds);
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

                if (bestMatch == null)
                {
                    return new RuleObservation
                    {
                        Triggered = false,
                        Ready = ready,
                        Busy = busy
                    };
                }

                int relativeRow = CountNewLines(snapshot.Text, bestMatch.Index);
                int triggerRow = snapshot.StartRow + relativeRow;
                int distance = snapshot.CursorRow - triggerRow;
                bool nearCursor = distance >= 0 && distance <= Math.Max(1, rule.MaximumTriggerDistanceLines);

                // Bounded event context: inspect up to 6 lines starting from the trigger line
                string eventContext = ExtractLinesAfterMatch(snapshot.Text, bestMatch.Index, 6);

                DateTime due = nowUtc.AddSeconds(rule.SafeInitialDelaySeconds);
                if (rule.ParseRetryTime)
                {
                    DateTime parsed;
                    if (RetryTimeParser.TryParseDue(eventContext, nowUtc.ToLocalTime(), out parsed))
                    {
                        due = parsed.ToUniversalTime().AddSeconds(3);
                        if (due < nowUtc)
                        {
                            due = nowUtc;
                        }
                    }
                }

                string normalizedContext = NormalizeEventContext(bestMatch.Value);
                string token = rule.Name + ":" + bestPatternIndex + ":" + StableHash(normalizedContext).ToString("X8", CultureInfo.InvariantCulture);
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
                    MatchStart = bestMatch.Index,
                    MatchLength = bestMatch.Length,
                    TriggerRow = triggerRow,
                    DistanceLines = distance,
                    TriggerText = bestMatch.Value,
                    TriggerFingerprint = token,
                    DueUtc = due
                };

                return new RuleObservation
                {
                    Triggered = nearCursor,
                    Ready = ready,
                    Busy = busy,
                    Event = triggerEvent
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
