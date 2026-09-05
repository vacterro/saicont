using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace SaiCont
{
    /// <summary>
    /// CORE-005: the immutable lifecycle identity a health snapshot must carry.
    /// Program owns the authoritative values (they are the same ones written to
    /// run/SAICONT.instance.xml) and injects this context into every engine it
    /// creates, including GUI/TUI reload replacements. WriteHealth used to
    /// hardcode ProcessId=0 and null start/mode, so a consumer could not perform
    /// the documented PID/start-time anti-reuse cross-check, and interactive
    /// modes published no health at all.
    ///
    /// InstanceShortId is a SHORT NON-SECRET hash of the instance token: it
    /// correlates health with the instance record without ever exposing the raw
    /// token that authorizes a stop request.
    /// </summary>
    internal sealed class LifecycleHealthContext
    {
        public string HealthFilePath;
        public int ProcessId;
        public DateTime ProcessStartUtc;
        public string Mode;
        public string InstanceShortId;
        public bool StateStoreApplicable;

        public static string DeriveInstanceShortId(string instanceToken)
        {
            if (String.IsNullOrEmpty(instanceToken)) return null;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(new UTF8Encoding(false).GetBytes(instanceToken));
                var text = new StringBuilder(16);
                for (int i = 0; i < 8; i++) text.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }
    }

    /// <summary>
    /// T-139: local machine-readable health contract (schema v1).
    /// A tiny versioned snapshot another local orchestrator (TERMISAI) can
    /// read without scraping GUI text, console frames, or log prose.
    ///
    /// Privacy/safety: no console text, no prompt contents, no command
    /// contents, no access tokens/credentials, no full environment dump, no
    /// arbitrary stack traces. instance_token is never copied verbatim -- a
    /// short non-authoritative correlation id derived from a hash is used.
    /// </summary>
    internal sealed class HealthReport
    {
        public const int SchemaVersion = 1;

        public string ProductVersion;
        public int ProcessId;
        public DateTime? ProcessStartUtc;
        public string InstanceShortId;
        public string Mode;
        public int PolicyRevision;
        public string ConfigFingerprint;
        public DateTime? LastPollUtc;
        public DateTime? LastSuccessfulPollUtc;
        public DateTime? LastErrorUtc;
        public string LastErrorCode;
        public string StateStoreDisposition;
        public bool StateStoreHealthy;
        public int ActiveSessions;
        public int TriggeredSessions;
        public int WriteEligibleSessions;
        public DateTime? NextDueUtc;
        public DateTime? LastWriteUtc;
        public string LastWriteTarget;
        public DateTime HealthUpdatedUtc;

        /// <summary>
        /// Serialize to a compact single-line JSON object (stable field order
        /// for machine consumers) and atomically write to <paramref name="path"/>.
        /// Health write is non-authoritative: a write failure returns false but
        /// never changes safety/retry truth.
        /// </summary>
        public bool TryWrite(string path, out string error)
        {
            return TryWrite(path, null, out error);
        }

        // Test seam for torn-write fault injection. The override receives the
        // complete payload but still writes through AtomicFile, so a fault
        // before commit must leave the prior authoritative snapshot intact.
        internal bool TryWrite(string path, Action<Stream, byte[]> writeOverride, out string error)
        {
            string json = ToJson();
            return AtomicFile.TryWrite(path, delegate(Stream stream)
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(json);
                if (writeOverride == null)
                {
                    stream.Write(bytes, 0, bytes.Length);
                }
                else
                {
                    writeOverride(stream, bytes);
                }
            }, out error);
        }

        private string ToJson()
        {
            var sb = new StringBuilder(640);
            sb.Append('{');
            AppendField(sb, "schema_version", SchemaVersion.ToString(CultureInfo.InvariantCulture), true);
            AppendField(sb, "product_version", JsonEscape(ProductVersion), false);
            AppendField(sb, "process_id", ProcessId.ToString(CultureInfo.InvariantCulture), false);
            AppendField(sb, "process_start_utc", JsonDate(ProcessStartUtc), false);
            AppendField(sb, "instance_short_id", JsonEscape(InstanceShortId), false);
            AppendField(sb, "mode", JsonEscape(Mode), false);
            AppendField(sb, "policy_revision", PolicyRevision.ToString(CultureInfo.InvariantCulture), false);
            AppendField(sb, "config_fingerprint", JsonEscape(ConfigFingerprint), false);
            AppendField(sb, "last_poll_utc", JsonDate(LastPollUtc), false);
            AppendField(sb, "last_successful_poll_utc", JsonDate(LastSuccessfulPollUtc), false);
            AppendField(sb, "last_error_utc", JsonDate(LastErrorUtc), false);
            AppendField(sb, "last_error_code", JsonEscape(LastErrorCode), false);
            AppendField(sb, "state_store_disposition", JsonEscape(StateStoreDisposition), false);
            AppendField(sb, "state_store_healthy", StateStoreHealthy ? "true" : "false", false);
            AppendField(sb, "active_sessions", ActiveSessions.ToString(CultureInfo.InvariantCulture), false);
            AppendField(sb, "triggered_sessions", TriggeredSessions.ToString(CultureInfo.InvariantCulture), false);
            AppendField(sb, "write_eligible_sessions", WriteEligibleSessions.ToString(CultureInfo.InvariantCulture), false);
            AppendField(sb, "next_due_utc", JsonDate(NextDueUtc), false);
            AppendField(sb, "last_write_utc", JsonDate(LastWriteUtc), false);
            AppendField(sb, "last_write_target", JsonEscape(LastWriteTarget), false);
            AppendField(sb, "health_updated_utc", JsonDate(HealthUpdatedUtc), false);
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendField(StringBuilder sb, string name, string value, bool first)
        {
            if (!first) { sb.Append(','); }
            sb.Append('"').Append(name).Append("\":");
            // numeric/bool/null pass through unwrapped; strings/date are already wrapped
            sb.Append(value);
        }

        private static string JsonDate(DateTime? value)
        {
            if (!value.HasValue) { return "null"; }
            DateTime utc = value.Value.ToUniversalTime();
            return "\"" + utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + "\"";
        }

        private static string JsonEscape(string value)
        {
            if (value == null) { return "null"; }
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
