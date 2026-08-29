using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SaiCont
{
    internal sealed class StateRecord
    {
        public string RuleName;
        public int ProcessId;
        public DateTime ProcessStartUtc;
        public string TriggerFingerprint;
        public string RuleSemanticFingerprint;
        public DateTime LastObservedUtc;
        public DateTime LastWriteUtc;
        public DateTime NextAllowedAttemptUtc;
        public bool AwaitingOutcome;
        public bool SawBusyAfterWrite;
        public string SuppressedFingerprint;
        public int AttemptCount;
        public string RecoveryState;

        public string CompositeKey
        {
            get
            {
                return RuleName + ":" + ProcessId + ":" +
                    (ProcessStartUtc == DateTime.MinValue
                        ? "0"
                        : ProcessStartUtc.ToString("o", CultureInfo.InvariantCulture));
            }
        }
    }

    internal enum StateLoadDisposition
    {
        Missing,
        Valid,
        Corrupt,
        UnsupportedSchema,
        Unavailable
    }

    internal sealed class DurableStateStore
    {
        internal const string SchemaVersion = "1";
        internal const int MaximumRecords = 128;
        private static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(RetryConstants.MaximumRetryHorizonDays + 31);
        private readonly string _filePath;
        private readonly object _ioLock = new object();
        private string _lastFingerprint;

        public DurableStateStore(string filePath)
        {
            _filePath = String.IsNullOrWhiteSpace(filePath) ? filePath : Path.GetFullPath(filePath);
            LastLoadDisposition = StateLoadDisposition.Missing;
        }

        public string FilePath { get { return _filePath; } }
        public StateLoadDisposition LastLoadDisposition { get; private set; }
        public string LastError { get; private set; }
        public int SuccessfulWriteCount { get; private set; }
        public bool RequiresConservativeRecovery
        {
            get
            {
                    return LastLoadDisposition == StateLoadDisposition.Corrupt ||
                    LastLoadDisposition == StateLoadDisposition.UnsupportedSchema ||
                    LastLoadDisposition == StateLoadDisposition.Unavailable;
            }
        }

        public bool TryPreflight(out string error)
        {
            error = null;
            if (String.IsNullOrWhiteSpace(_filePath))
            {
                error = "state path is empty";
                return false;
            }

            string directory = Path.GetDirectoryName(_filePath);
            string probePath = _filePath + ".preflight." + Guid.NewGuid().ToString("N");
            try
            {
                if (!String.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                using (var stream = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.WriteByte(0);
                    stream.Flush(true);
                }
                File.Delete(probePath);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
            finally
            {
                TryDelete(probePath);
            }
        }

        public List<StateRecord> Load()
        {
            return Load(DateTime.UtcNow);
        }

        public List<StateRecord> Load(DateTime nowUtc)
        {
            return LoadCore(nowUtc, true);
        }

        public List<StateRecord> ValidateReadOnly(DateTime nowUtc)
        {
            return LoadCore(nowUtc, false);
        }

        private List<StateRecord> LoadCore(DateTime nowUtc, bool quarantineInvalid)
        {
            lock (_ioLock)
            {
                LastError = null;
                CleanupStaleTemps();
                var records = new List<StateRecord>();
                if (String.IsNullOrEmpty(_filePath))
                {
                    LastLoadDisposition = StateLoadDisposition.Missing;
                    _lastFingerprint = ComputeFingerprint(records);
                    return records;
                }

                try
                {
                    using (var probe = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                    }
                }
                catch (FileNotFoundException)
                {
                    LastLoadDisposition = StateLoadDisposition.Missing;
                    _lastFingerprint = ComputeFingerprint(records);
                    return records;
                }
                catch (DirectoryNotFoundException)
                {
                    LastLoadDisposition = StateLoadDisposition.Missing;
                    _lastFingerprint = ComputeFingerprint(records);
                    return records;
                }
                catch (Exception exception)
                {
                    LastLoadDisposition = StateLoadDisposition.Unavailable;
                    LastError = exception.GetType().Name + ": " + exception.Message;
                    _lastFingerprint = ComputeFingerprint(records);
                    return records;
                }

                try
                {
                    var settings = new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null
                    };
                    XDocument document;
                    using (XmlReader reader = XmlReader.Create(_filePath, settings))
                    {
                        document = XDocument.Load(reader, LoadOptions.None);
                    }

                    XElement root = document.Root;
                    if (root == null || root.Name.LocalName != "saicontState")
                    {
                        throw new FormatException("state root must be <saicontState>");
                    }

                    string version = (string)root.Attribute("version");
                    if (!String.Equals(version, SchemaVersion, StringComparison.Ordinal))
                    {
                        LastLoadDisposition = StateLoadDisposition.UnsupportedSchema;
                        LastError = "unsupported state schema version '" + (version ?? "") + "'";
                        if (quarantineInvalid)
                        {
                            Quarantine("unsupported");
                        }
                        _lastFingerprint = ComputeFingerprint(records);
                        return records;
                    }

                    foreach (XElement element in root.Elements("record"))
                    {
                        records.Add(ParseRecord(element));
                    }

                    records = NormalizeRecords(records, nowUtc);
                    LastLoadDisposition = StateLoadDisposition.Valid;
                    _lastFingerprint = ComputeFingerprint(records);
                    return records;
                }
                catch (UnauthorizedAccessException exception)
                {
                    LastLoadDisposition = StateLoadDisposition.Unavailable;
                    LastError = exception.GetType().Name + ": " + exception.Message;
                    _lastFingerprint = ComputeFingerprint(records);
                    return new List<StateRecord>();
                }
                catch (IOException exception)
                {
                    LastLoadDisposition = StateLoadDisposition.Unavailable;
                    LastError = exception.GetType().Name + ": " + exception.Message;
                    _lastFingerprint = ComputeFingerprint(records);
                    return new List<StateRecord>();
                }
                catch (Exception exception)
                {
                    LastLoadDisposition = StateLoadDisposition.Corrupt;
                    LastError = exception.GetType().Name + ": " + exception.Message;
                    if (quarantineInvalid)
                    {
                        Quarantine("corrupt");
                    }
                    _lastFingerprint = ComputeFingerprint(records);
                    return new List<StateRecord>();
                }
            }
        }

        public bool TrySave(IEnumerable<StateRecord> records, DateTime nowUtc, out bool changed, out string error)
        {
            changed = false;
            error = null;
            if (String.IsNullOrEmpty(_filePath) || records == null)
            {
                error = "state path or records are unavailable";
                return false;
            }

            lock (_ioLock)
            {
                List<StateRecord> normalized = NormalizeRecords(records, nowUtc);
                string fingerprint = ComputeFingerprint(normalized);
                if (File.Exists(_filePath) && String.Equals(_lastFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return true;
                }

                var root = new XElement(
                    "saicontState",
                    new XAttribute("version", SchemaVersion),
                    new XAttribute("updatedUtc", FormatUtc(nowUtc)));
                foreach (StateRecord record in normalized)
                {
                    root.Add(new XElement(
                        "record",
                        new XAttribute("rule", record.RuleName ?? String.Empty),
                        new XAttribute("pid", record.ProcessId),
                        new XAttribute("startUtc", FormatUtc(record.ProcessStartUtc)),
                        new XAttribute("fingerprint", record.TriggerFingerprint ?? String.Empty),
                        new XAttribute("ruleFingerprint", record.RuleSemanticFingerprint ?? String.Empty),
                        new XAttribute("lastObserved", FormatUtc(record.LastObservedUtc)),
                        new XAttribute("lastWrite", FormatUtc(record.LastWriteUtc)),
                        new XAttribute("nextAllowed", FormatUtc(record.NextAllowedAttemptUtc)),
                        new XAttribute("awaitingOutcome", record.AwaitingOutcome),
                        new XAttribute("sawBusy", record.SawBusyAfterWrite),
                        new XAttribute("suppressed", record.SuppressedFingerprint ?? String.Empty),
                        new XAttribute("attempts", record.AttemptCount),
                        new XAttribute("state", SerializeRecoveryState(record))));
                }

                var document = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root);
                AtomicFileCommit commit;
                string writeError;
                bool written = AtomicFile.TryWrite(
                    _filePath,
                    delegate(Stream stream)
                    {
                        var settings = new XmlWriterSettings
                        {
                            Encoding = new UTF8Encoding(false),
                            Indent = true,
                            CloseOutput = false
                        };
                        using (XmlWriter writer = XmlWriter.Create(stream, settings))
                        {
                            document.Save(writer);
                        }
                    },
                    out commit,
                    out writeError);
                if (!written)
                {
                    LastError = writeError;
                    return false;
                }

                changed = true;
                SuccessfulWriteCount++;
                if (commit == AtomicFileCommit.CommittedWithCleanupWarning)
                {
                    LastError = writeError;
                    error = writeError;
                }
                else
                {
                    LastError = null;
                }
                _lastFingerprint = fingerprint;
                return true;
            }
        }

        public void Save(IEnumerable<StateRecord> records, DateTime nowUtc)
        {
            bool changed;
            string error;
            if (!TrySave(records, nowUtc, out changed, out error))
            {
                throw new IOException("State save failed: " + error);
            }
        }

        internal static string ComputeFingerprint(IEnumerable<StateRecord> records)
        {
            var builder = new StringBuilder();
            foreach (StateRecord record in (records ?? Enumerable.Empty<StateRecord>()).OrderBy(r => r.CompositeKey, StringComparer.Ordinal))
            {
                builder.Append(record.CompositeKey).Append('|')
                    .Append(record.TriggerFingerprint).Append('|')
                    .Append(FormatUtc(RoundDownToHour(record.LastObservedUtc))).Append('|')
                    .Append(FormatUtc(record.LastWriteUtc)).Append('|')
                    .Append(FormatUtc(record.NextAllowedAttemptUtc)).Append('|')
                    .Append(record.AwaitingOutcome ? '1' : '0').Append('|')
                    .Append(record.SawBusyAfterWrite ? '1' : '0').Append('|')
                    .Append(record.SuppressedFingerprint).Append('|')
                    .Append(record.AttemptCount).Append('|')
                    .Append(record.RecoveryState).Append('\n');
            }
            return builder.ToString();
        }

        private static StateRecord ParseRecord(XElement element)
        {
            string lastWriteRaw = RequiredAttributeAllowEmpty(element, "lastWrite");
            string nextAllowedRaw = RequiredAttributeAllowEmpty(element, "nextAllowed");
            string awaitingRaw = RequiredAttribute(element, "awaitingOutcome");
            string sawBusyRaw = RequiredAttribute(element, "sawBusy");
            string attemptsRaw = RequiredAttribute(element, "attempts");
            string stateRaw = RequiredAttribute(element, "state");

            var record = new StateRecord
            {
                RuleName = RequiredAttribute(element, "rule"),
                ProcessId = ParseInt(RequiredAttribute(element, "pid")),
                ProcessStartUtc = ParseUtc(RequiredAttribute(element, "startUtc")),
                TriggerFingerprint = OptionalAttribute(element, "fingerprint"),
                RuleSemanticFingerprint = OptionalAttribute(element, "ruleFingerprint"),
                LastObservedUtc = ParseUtc(RequiredAttribute(element, "lastObserved")),
                LastWriteUtc = ParseUtcRequired(lastWriteRaw, "lastWrite"),
                NextAllowedAttemptUtc = ParseUtcRequired(nextAllowedRaw, "nextAllowed"),
                AwaitingOutcome = ParseBoolStrict(awaitingRaw, "awaitingOutcome"),
                SawBusyAfterWrite = ParseBoolStrict(sawBusyRaw, "sawBusy"),
                SuppressedFingerprint = OptionalAttribute(element, "suppressed"),
                AttemptCount = ParseIntStrict(attemptsRaw, "attempts", 0, 50),
                RecoveryState = ValidateRecoveryState(stateRaw)
            };

            if (record.ProcessId <= 0 || record.ProcessStartUtc == DateTime.MinValue)
            {
                throw new FormatException("state record has weak process identity");
            }
            if (record.LastObservedUtc == DateTime.MinValue)
            {
                throw new FormatException("state record has invalid lastObserved timestamp");
            }
            return record;
        }

        private static string OptionalAttribute(XElement element, string name)
        {
            XAttribute attr = element.Attribute(name);
            return attr == null ? String.Empty : attr.Value ?? String.Empty;
        }

        private static DateTime ParseUtcRequired(string value, string fieldName)
        {
            if (String.IsNullOrEmpty(value))
            {
                return DateTime.MinValue;
            }
            DateTime parsed;
            if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed))
            {
                throw new FormatException("state record has invalid timestamp '" + fieldName + "'");
            }
            return parsed.ToUniversalTime();
        }

        private static bool ParseBoolStrict(string value, string fieldName)
        {
            if (String.IsNullOrEmpty(value))
            {
                throw new FormatException("state record missing required boolean '" + fieldName + "'");
            }
            bool parsed;
            if (!Boolean.TryParse(value, out parsed))
            {
                throw new FormatException("state record has invalid boolean '" + fieldName + "'");
            }
            return parsed;
        }

        private static int ParseIntStrict(string value, string fieldName, int min, int max)
        {
            if (String.IsNullOrEmpty(value))
            {
                throw new FormatException("state record missing required integer '" + fieldName + "'");
            }
            int parsed;
            if (!Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                throw new FormatException("state record has invalid integer '" + fieldName + "'");
            }
            if (parsed < min || parsed > max)
            {
                throw new FormatException("state record '" + fieldName + "' out of range [" + min + "," + max + "]");
            }
            return parsed;
        }

        private static string SerializeRecoveryState(StateRecord record)
        {
            // W2-001: Save must emit exactly the representations Load accepts.
            // An empty/unknown state name is never serialized; it degrades to
            // the safe idle disposition instead of producing a corrupt record.
            string value = record == null ? null : record.RecoveryState;
            if (!String.IsNullOrEmpty(value))
            {
                try
                {
                    Enum.Parse(typeof(RecoveryState), value, true);
                    return value;
                }
                catch
                {
                }
            }
            return RecoveryState.IdleNoEvent.ToString();
        }

        private static string ValidateRecoveryState(string value)
        {
            if (String.IsNullOrEmpty(value))
            {
                throw new FormatException("state record missing required 'state'");
            }
            switch (value)
            {
                case "IdleNoEvent":
                case "EventWaitingDeadline":
                case "BackoffWait":
                case "EventReadyToAttempt":
                case "RecoveryExhausted":
                case "RecoveryConfirmed":
                case "AttemptInFlightReserved":
                case "CommandWrittenAwaitingOutcome":
                case "TargetBusyOrProgressing":
                case "EventStillPresentReady":
                case "SessionDisappeared":
                case "TargetUnreadable":
                case "AmbiguousFailClosed":
                    return value;
                default:
                    throw new FormatException("state record has unknown RecoveryState '" + value + "'");
            }
        }

        private static List<StateRecord> NormalizeRecords(IEnumerable<StateRecord> records, DateTime nowUtc)
        {
            var byKey = new Dictionary<string, StateRecord>(StringComparer.Ordinal);
            foreach (StateRecord record in records ?? Enumerable.Empty<StateRecord>())
            {
                if (record == null || String.IsNullOrEmpty(record.RuleName) || record.ProcessId <= 0 || record.ProcessStartUtc == DateTime.MinValue)
                {
                    continue;
                }
                if (record.LastObservedUtc != DateTime.MinValue && nowUtc - record.LastObservedUtc > DefaultRetention)
                {
                    continue;
                }

                StateRecord existing;
                if (!byKey.TryGetValue(record.CompositeKey, out existing) || existing.LastObservedUtc < record.LastObservedUtc)
                {
                    byKey[record.CompositeKey] = record;
                }
            }

            return byKey.Values
                .OrderByDescending(r => r.LastObservedUtc)
                .Take(MaximumRecords)
                .OrderBy(r => r.CompositeKey, StringComparer.Ordinal)
                .ToList();
        }

        private void CleanupStaleTemps()
        {
            try
            {
                string directory = Path.GetDirectoryName(_filePath);
                string name = Path.GetFileName(_filePath);
                if (String.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                {
                    return;
                }
                foreach (string file in Directory.GetFiles(directory, name + ".tmp.*"))
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromHours(1))
                    {
                        TryDelete(file);
                    }
                }
            }
            catch
            {
            }
        }

        private void Quarantine(string reason)
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    string destination = _filePath + "." + reason + "." + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
                    File.Move(_filePath, destination);
                }
            }
            catch
            {
            }
        }

        private static string RequiredAttribute(XElement element, string name)
        {
            string value = RequiredAttributeAllowEmpty(element, name);
            if (String.IsNullOrWhiteSpace(value))
            {
                throw new FormatException("state record is missing '" + name + "'");
            }
            return value;
        }

        private static string RequiredAttributeAllowEmpty(XElement element, string name)
        {
            XAttribute attribute = element.Attribute(name);
            if (attribute == null)
            {
                throw new FormatException("state record is missing '" + name + "'");
            }
            return attribute.Value ?? String.Empty;
        }

        private static int ParseInt(string value, int fallback = Int32.MinValue)
        {
            int parsed;
            if (Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }
            if (fallback != Int32.MinValue)
            {
                return fallback;
            }
            throw new FormatException("invalid integer '" + value + "'");
        }

        private static bool ParseBool(string value)
        {
            bool parsed;
            return Boolean.TryParse(value, out parsed) && parsed;
        }

        private static DateTime ParseUtc(string value)
        {
            if (String.IsNullOrEmpty(value)) return DateTime.MinValue;
            DateTime parsed;
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed))
            {
                return parsed.ToUniversalTime();
            }
            return DateTime.MinValue;
        }

        private static DateTime RoundDownToHour(DateTime value)
        {
            if (value == DateTime.MinValue)
            {
                return value;
            }
            DateTime utc = value.ToUniversalTime();
            return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
        }

        private static string FormatUtc(DateTime utc)
        {
            return utc == DateTime.MinValue ? String.Empty : utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }
}
