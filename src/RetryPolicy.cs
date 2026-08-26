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
        AmbiguousFailClosed
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

        public StateRecord Export(string ruleName, ProcessSessionIdentity session, DateTime nowUtc)
        {
            return new StateRecord
            {
                RuleName = ruleName,
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
            DateTime maximum = nowUtc.AddHours(24);
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
                    MatchCollection matches = rx.Matches(snapshot.Text);
                    if (matches.Count == 0)
                    {
                        continue;
                    }

                    Match candidate = matches[matches.Count - 1];
                    if (bestMatch == null || candidate.Index > bestMatch.Index)
                    {
                        bestMatch = candidate;
                        bestPatternIndex = index;
                    }
                }

                string cursorTail = ExtractCursorTail(snapshot.Text, 5);
                bool ready = MatchesAny(snapshot.CursorLine, rule.CompiledReadyPatterns);
                bool busy = MatchesAny(cursorTail, rule.CompiledBusyPatterns);

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

                bool parsedDue = false;
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
                        parsedDue = true;
                    }
                }

                string normalizedContext = NormalizeEventContext(bestMatch.Value);
                string token = rule.Name + ":" + bestPatternIndex + ":" + StableHash(normalizedContext).ToString("X8", CultureInfo.InvariantCulture);
                if (parsedDue)
                {
                    token += ":" + due.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                }

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
            if (String.IsNullOrEmpty(text))
            {
                return String.Empty;
            }

            string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            if (lines.Length == 0)
            {
                return String.Empty;
            }

            int endIdx = lines.Length - 1;
            int startIdx = Math.Max(0, endIdx - lineCount + 1);
            int count = endIdx - startIdx + 1;
            var tailLines = new string[count];
            Array.Copy(lines, startIdx, tailLines, 0, count);
            return String.Join("\n", tailLines);
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
            if (clock.Groups["month"].Success)
            {
                string monthStr = clock.Groups["month"].Value;
                DateTime monthDt;
                if (DateTime.TryParseExact(monthStr, new[] { "MMMM", "MMM" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out monthDt))
                {
                    monthNumber = monthDt.Month;
                }
            }
            int dayNumber = 0;
            if (clock.Groups["day"].Success)
            {
                Int32.TryParse(clock.Groups["day"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out dayNumber);
            }
            int yearNumber = nowLocal.Year;
            if (clock.Groups["year"].Success)
            {
                int y;
                if (Int32.TryParse(clock.Groups["year"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out y) && y >= 2024 && y <= 2040)
                {
                    yearNumber = y;
                }
            }

            if (monthNumber > 0 && dayNumber > 0)
            {
                try
                {
                    DateTime specificDate = new DateTime(yearNumber, monthNumber, dayNumber, parsed.Hour, parsed.Minute, parsed.Second, DateTimeKind.Local);
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
                catch
                {
                }
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
            const long MaximumDurationSeconds = 366L * 24L * 60L * 60L;
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
