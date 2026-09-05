using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace SaiCont
{
    internal static partial class Program
    {
        // A deterministic in-process OpenCode responder with request counters.
        // No socket, no live endpoint: PERF-001's budget and CORE-002's
        // reconciliation are both properties of the REST/SSE contract, not of
        // real networking.
        private sealed class Audit163FakeOpenCode
        {
            public int Health;
            public int SessionList;
            public int SessionSingle;
            public int Status;
            public int Messages;
            public int Post;

            public string SessionId = "ses_1";
            public string Directory = @"C:\project";
            public string Agent = "build";
            public string Provider = "sairoute";
            public string Model = "SAIFREN";
            public string StatusType = "idle";
            public string Version = "1.18.5";
            public bool Healthy = true;

            public int Total { get { return Health + SessionList + SessionSingle + Status + Messages + Post; } }

            public void Reset()
            {
                Health = SessionList = SessionSingle = Status = Messages = Post = 0;
            }

            private string SessionJson()
            {
                return "{\"id\":\"" + SessionId + "\",\"directory\":\"" + Directory.Replace("\\", "\\\\") +
                    "\",\"agent\":\"" + Agent + "\",\"model\":{\"providerID\":\"" + Provider + "\",\"id\":\"" + Model + "\"}}";
            }

            public bool Invoke(string method, string path, string body, out int status, out string responseBody, out string error)
            {
                status = 200;
                responseBody = String.Empty;
                error = null;
                if (String.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    Post++;
                    responseBody = "{\"info\":{\"role\":\"assistant\",\"sessionID\":\"" + SessionId +
                        "\",\"providerID\":\"" + Provider + "\",\"modelID\":\"" + Model + "\",\"id\":\"msg_1\"}}";
                    return true;
                }
                if (path.StartsWith("/global/health", StringComparison.Ordinal))
                {
                    Health++;
                    responseBody = "{\"healthy\":" + (Healthy ? "true" : "false") + ",\"version\":\"" + Version + "\"}";
                    return true;
                }
                if (path.StartsWith("/session/status", StringComparison.Ordinal))
                {
                    Status++;
                    responseBody = "{\"" + SessionId + "\":{\"type\":\"" + StatusType + "\"}}";
                    return true;
                }
                if (path.IndexOf("/message", StringComparison.Ordinal) >= 0)
                {
                    Messages++;
                    responseBody = "[]";
                    return true;
                }
                if (String.Equals(path, "/session", StringComparison.Ordinal))
                {
                    SessionList++;
                    responseBody = "[" + SessionJson() + "]";
                    return true;
                }
                if (path.StartsWith("/session/", StringComparison.Ordinal))
                {
                    SessionSingle++;
                    responseBody = SessionJson();
                    return true;
                }
                status = 404;
                error = "unexpected_path=" + path;
                return true;
            }
        }

        private static OpenCodeHttpTransport Audit163Transport(Audit163FakeOpenCode fake, bool restoredAmbiguous, out TargetRule rule)
        {
            rule = Audit163HttpRule("a163-http", "http://127.0.0.1:41961", fake.Directory);
            OpenCodeHttpTransport transport = OpenCodeHttpTransport.CreateForTests(rule.Endpoint, fake.Directory, fake.SessionId, restoredAmbiguous);
            transport.HttpInvokeForTests = fake.Invoke;
            return transport;
        }

        // --- CORE-002 --------------------------------------------------------

        private static int Audit163_Core002_TransportReconciliation()
        {
            int failures = 0;
            var fake = new Audit163FakeOpenCode();
            TargetRule rule;
            // restoredSessionAmbiguous = true is the same latch a malformed frame
            // sets, and it used to be terminal for the process lifetime.
            using (var scope = new Audit163Disposable(Audit163Transport(fake, true, out rule)))
            {
                OpenCodeHttpTransport transport = scope.Transport;
                DateTime now = new DateTime(2026, 9, 4, 11, 0, 0, DateTimeKind.Utc);
                transport.SetSessionStateForTests(fake.SessionId, OpenCodeStatusKind.Idle, null, null);

                string error;
                TargetObservation blocked = transport.Poll(rule, now, out error);
                failures += AssertEqual(true, transport.IsAmbiguous, "CORE-002: restored ambiguity starts the transport in the ambiguous state");
                failures += AssertEqual("malformed_sse_event_requires_reconciliation", blocked.BlockReason, "CORE-002: ambiguous transport refuses to advertise a send-safe observation");

                // A reconnect ALONE must not clear it.
                transport.SetStreamStateForTests(true, true);
                fake.StatusType = "busy";
                transport.SetSessionStateForTests(fake.SessionId, OpenCodeStatusKind.Busy, null, null);
                TargetObservation stillBlocked = transport.Poll(rule, now.AddSeconds(2), out error);
                failures += AssertEqual(true, transport.IsAmbiguous, "CORE-002: a new stream without authoritative agreement does not clear ambiguity");
                failures += AssertEqual(true, (stillBlocked.BlockReason ?? String.Empty).Length > 0, "CORE-002: transport still blocks while unreconciled");

                // Authoritative agreement: newer live stream + REST idle + SSE idle.
                fake.StatusType = "idle";
                transport.SetSessionStateForTests(fake.SessionId, OpenCodeStatusKind.Idle, null, null);
                TargetObservation reconciled = transport.Poll(rule, now.AddSeconds(4), out error);
                failures += AssertEqual(false, transport.IsAmbiguous, "CORE-002: ambiguity clears after authoritative REST/SSE agreement on a newer stream");
                failures += AssertEqual(true, reconciled.CanSend, "CORE-002: the same transport becomes send-eligible again without a restart");
                failures += AssertEqual(fake.SessionId, reconciled.SessionIdentity, "CORE-002: reconciled observation names the bound session");

                // A malformed frame re-poisons it, and a busy session cannot clear it.
                transport.MarkAmbiguousForTests("malformed_sse_frame: injected");
                fake.StatusType = "busy";
                transport.SetSessionStateForTests(fake.SessionId, OpenCodeStatusKind.Busy, null, null);
                TargetObservation rePoisoned = transport.Poll(rule, now.AddSeconds(6), out error);
                failures += AssertEqual(true, transport.IsAmbiguous, "CORE-002: malformed evidence re-enters the ambiguous state");
                failures += AssertEqual(false, rePoisoned.CanSend, "CORE-002: a busy session cannot reconcile ambiguity");

                // A frame from a foreign project directory is ambiguity, not data.
                transport.SetStreamStateForTests(true, true);
                fake.StatusType = "idle";
                transport.SetSessionStateForTests(fake.SessionId, OpenCodeStatusKind.Idle, null, null);
                transport.Poll(rule, now.AddSeconds(8), out error);
                failures += AssertEqual(false, transport.IsAmbiguous, "CORE-002: reconciliation is repeatable, not one-shot");
                transport.ProcessFrameForTests("data: {\"directory\":\"C:\\\\elsewhere\",\"payload\":{\"id\":\"evt_x\",\"type\":\"session.idle\",\"properties\":{\"sessionID\":\"" + fake.SessionId + "\"}}}");
                failures += AssertEqual(true, transport.IsAmbiguous, "CORE-002: a frame from a foreign project directory marks the transport ambiguous");
                // CORE-002: ambiguity must ASK for a fresh stream. A malformed
                // frame does not disconnect the socket by itself, so without this
                // request the transport would wait forever for the newer
                // generation reconciliation requires.
                failures += AssertEqual(true, transport.AmbiguityDisconnectRequestedForTests, "CORE-002: ambiguity requests a fresh SSE stream so reconciliation is reachable");
            }
            return failures;
        }

        private sealed class Audit163Disposable : IDisposable
        {
            public readonly OpenCodeHttpTransport Transport;
            public Audit163Disposable(OpenCodeHttpTransport transport) { Transport = transport; }
            public void Dispose() { try { Transport.Dispose(); } catch { } }
        }

        // --- CORE-003 --------------------------------------------------------

        private static int Audit163_Core003_SnapshotScope()
        {
            int failures = 0;
            TargetRule consoleRule = Audit163ConsoleRule("core003-console", "codex", 40);
            TargetRule httpRule = Audit163HttpRule("core003-http", "http://127.0.0.1:1", Path.GetTempPath());

            // Console + HTTP: a Toolhelp failure must fail the console rule closed
            // and STILL poll the independent HTTP rule.
            var mixed = new WatcherConfiguration
            {
                PollIntervalMilliseconds = 2000,
                Targets = new List<TargetRule> { consoleRule, httpRule }
            };
            var mixedEngine = new WatcherEngine(
                mixed,
                delegate { throw new InvalidOperationException("injected snapshot failure"); },
                delegate(int pid, string name) { return new ProcessSessionIdentity { ProcessId = pid, ProcessName = name, StartTimeUtc = DateTime.MinValue }; },
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e) { s = null; e = "unreachable"; return false; },
                delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e) { e = null; return NativeWriteOutcome.NoInputCommitted; },
                delegate { return DateTime.UtcNow; });
            IList<PollResult> mixedResults = mixedEngine.PollOnce(false);
            try { mixedEngine.Dispose(); } catch { }

            bool runtimeRow = mixedResults.Any(r => String.Equals(r.Target, "runtime", StringComparison.Ordinal) &&
                String.Equals(r.Error, "process_discovery_unavailable", StringComparison.Ordinal));
            bool consoleRow = mixedResults.Any(r => String.Equals(r.Target, consoleRule.Name, StringComparison.Ordinal) &&
                String.Equals(r.Reason, "send_blocked=process_discovery_unavailable", StringComparison.Ordinal));
            bool httpRow = mixedResults.Any(r => String.Equals(r.Target, httpRule.Name, StringComparison.Ordinal));
            failures += AssertEqual(true, runtimeRow, "CORE-003: the snapshot failure is still reported as a runtime diagnostic");
            failures += AssertEqual(true, consoleRow, "CORE-003: the console rule fails closed with process_discovery_unavailable");
            failures += AssertEqual(true, httpRow, "CORE-003: the independent opencode-http rule is still polled");

            // Console-only: the global early return remains correct.
            var consoleOnly = new WatcherConfiguration
            {
                PollIntervalMilliseconds = 2000,
                Targets = new List<TargetRule> { Audit163ConsoleRule("core003-only", "codex", 40) }
            };
            var consoleEngine = new WatcherEngine(
                consoleOnly,
                delegate { throw new InvalidOperationException("injected snapshot failure"); },
                delegate(int pid, string name) { return new ProcessSessionIdentity { ProcessId = pid, ProcessName = name, StartTimeUtc = DateTime.MinValue }; },
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e) { s = null; e = "unreachable"; return false; },
                delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e) { e = null; return NativeWriteOutcome.NoInputCommitted; },
                delegate { return DateTime.UtcNow; });
            IList<PollResult> consoleResults = consoleEngine.PollOnce(false);
            try { consoleEngine.Dispose(); } catch { }
            failures += AssertEqual(1, consoleResults.Count, "CORE-003: a console-only configuration keeps the single global fail-closed result");
            failures += AssertEqual("process_discovery_unavailable", consoleResults[0].Error, "CORE-003: console-only failure names process discovery");
            return failures;
        }

        // --- CORE-004 --------------------------------------------------------

        private static int Audit163_Core004_SendOwnership()
        {
            int failures = 0;
            var fake = new Audit163FakeOpenCode();
            TargetRule ruleA;
            using (var scope = new Audit163Disposable(Audit163Transport(fake, false, out ruleA)))
            {
                OpenCodeHttpTransport transport = scope.Transport;
                TargetRule ruleB = Audit163HttpRule("a163-http-b", ruleA.Endpoint, fake.Directory);
                DateTime now = new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);
                transport.SetStreamStateForTests(true, true);
                transport.SetSessionStateForTests(fake.SessionId, OpenCodeStatusKind.Idle, null, null);

                string error;
                TargetObservation observation = transport.Poll(ruleA, now, out error);
                failures += AssertEqual(true, observation.CanSend, "CORE-004: fixture reaches a send-eligible observation");

                // Rule A owns the operation; the engine stamps its state key.
                observation.OwnerStateKey = ruleA.Name + ":100:2026";
                SendOutcome pending = transport.Send(observation, ruleA, ruleA.Command, delegate { return true; }, out error);
                failures += AssertEqual(SendDisposition.Pending, pending.Disposition, "CORE-004: the send starts asynchronously");

                DateTime deadline = DateTime.UtcNow.AddSeconds(10);
                while (transport.HasPendingSend && DateTime.UtcNow < deadline) { Thread.Sleep(20); }
                failures += AssertEqual(false, transport.HasPendingSend, "CORE-004: the asynchronous send completed");

                // Rule B shares endpoint+project+session but does NOT own this
                // operation: same session identity, different rule/state key.
                var ownerB = new SendOwner { RuleName = ruleB.Name, StateKey = ruleB.Name + ":100:2026", SessionIdentity = fake.SessionId };
                SendOutcome stolen;
                failures += AssertEqual(false, transport.TryTakeCompletedSend(ownerB, out stolen), "CORE-004: a sibling rule cannot consume another rule's completion");
                failures += AssertEqual(true, stolen == null, "CORE-004: the refused consumer receives no outcome");

                // Same rule name but a different durable state key is also foreign.
                var ownerWrongKey = new SendOwner { RuleName = ruleA.Name, StateKey = ruleA.Name + ":999:2026", SessionIdentity = fake.SessionId };
                failures += AssertEqual(false, transport.TryTakeCompletedSend(ownerWrongKey, out stolen), "CORE-004: a different state key cannot consume the completion");

                var ownerA = new SendOwner { RuleName = ruleA.Name, StateKey = ruleA.Name + ":100:2026", SessionIdentity = fake.SessionId };
                SendOutcome mine;
                failures += AssertEqual(true, transport.TryTakeCompletedSend(ownerA, out mine), "CORE-004: the owning rule consumes its own completion");
                failures += AssertEqual(SendDisposition.CompleteInputCommitted, mine.Disposition, "CORE-004: the consumed outcome is the committed write");
                failures += AssertEqual(false, transport.TryTakeCompletedSend(ownerA, out mine), "CORE-004: a completion is consumable exactly once");
            }
            return failures;
        }

        // --- CORE-005 / CORE-007 --------------------------------------------

        private static int Audit163_Core005_LifecycleHealth()
        {
            int failures = 0;
            string dir = Path.Combine(Path.GetTempPath(), "SAICONT.a163.core005." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string healthPath = Path.Combine(dir, "SAICONT.health.json");
                DateTime start = new DateTime(2026, 9, 4, 9, 30, 0, DateTimeKind.Utc);
                LifecycleHealthContext context = BuildLifecycleHealthContext(healthPath, 4242, start, "--watch", "0123456789abcdef", true);
                failures += AssertEqual(16, (context.InstanceShortId ?? String.Empty).Length, "CORE-005: instance_short_id is a short 16-hex correlation id");
                failures += AssertEqual(false, String.Equals(context.InstanceShortId, "0123456789abcdef", StringComparison.Ordinal), "CORE-005: instance_short_id is derived, never the raw token");

                TargetRule rule = Audit163ConsoleRule("core005", "codex", 40);
                var config = new WatcherConfiguration { PollIntervalMilliseconds = 2000, Targets = new List<TargetRule> { rule } };
                DateTime clock = start.AddMinutes(1);
                var engine = new WatcherEngine(
                    config,
                    delegate { return new List<ProcessEntry>(); },
                    delegate(int pid, string name) { return new ProcessSessionIdentity { ProcessId = pid, ProcessName = name, StartTimeUtc = DateTime.MinValue }; },
                    delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e) { s = null; e = "no console"; return false; },
                    delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e) { e = null; return NativeWriteOutcome.NoInputCommitted; },
                    delegate { return clock; });
                engine.LifecycleHealth = context;
                engine.PollOnce(false);
                failures += AssertEqual(true, File.Exists(healthPath), "CORE-005: a lifecycle-bound engine publishes health");
                string json = File.ReadAllText(healthPath, Encoding.UTF8);
                failures += AssertEqual(true, json.IndexOf("\"process_id\":4242", StringComparison.Ordinal) >= 0, "CORE-005: health carries the real lifecycle PID");
                failures += AssertEqual(true, json.IndexOf("\"mode\":\"--watch\"", StringComparison.Ordinal) >= 0, "CORE-005: health carries the lifecycle mode");
                failures += AssertEqual(true, json.IndexOf("\"process_start_utc\":\"2026-09-04T09:30:00Z\"", StringComparison.Ordinal) >= 0, "CORE-005: health carries the real process start time");
                failures += AssertEqual(true, json.IndexOf("\"instance_short_id\":\"" + context.InstanceShortId + "\"", StringComparison.Ordinal) >= 0, "CORE-005: health carries the derived instance correlation id");
                failures += AssertEqual(false, json.IndexOf("0123456789abcdef", StringComparison.Ordinal) >= 0, "CORE-005: the raw instance token never reaches health");
                failures += AssertEqual(false, json.IndexOf("\"config_fingerprint\":\"1_targets\"", StringComparison.Ordinal) >= 0, "CORE-005: the config fingerprint is semantic, not a target count");

                // A semantic configuration change with the SAME target count must
                // change the fingerprint.
                string firstFingerprint = Audit163ExtractJsonString(json, "config_fingerprint");
                TargetRule changed = Audit163ConsoleRule("core005", "codex", 40);
                changed.RetryIntervalSeconds = 77;
                var config2 = new WatcherConfiguration { PollIntervalMilliseconds = 2000, Targets = new List<TargetRule> { changed } };
                string healthPath2 = Path.Combine(dir, "SAICONT.health2.json");
                var engine2 = new WatcherEngine(
                    config2,
                    delegate { return new List<ProcessEntry>(); },
                    delegate(int pid, string name) { return new ProcessSessionIdentity { ProcessId = pid, ProcessName = name, StartTimeUtc = DateTime.MinValue }; },
                    delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e) { s = null; e = "no console"; return false; },
                    delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e) { e = null; return NativeWriteOutcome.NoInputCommitted; },
                    delegate { return clock; });
                engine2.LifecycleHealth = BuildLifecycleHealthContext(healthPath2, 4242, start, "--watch", "0123456789abcdef", true);
                engine2.PollOnce(false);
                string fingerprint2 = Audit163ExtractJsonString(File.ReadAllText(healthPath2, Encoding.UTF8), "config_fingerprint");
                failures += AssertEqual(false, String.Equals(firstFingerprint, fingerprint2, StringComparison.Ordinal), "CORE-005: a semantic config change changes the fingerprint at an unchanged target count");

                // --dry-run has no durable store: report it as not applicable
                // instead of describing an absent store as healthy.
                string healthPath3 = Path.Combine(dir, "SAICONT.health3.json");
                var engine3 = new WatcherEngine(
                    config,
                    delegate { return new List<ProcessEntry>(); },
                    delegate(int pid, string name) { return new ProcessSessionIdentity { ProcessId = pid, ProcessName = name, StartTimeUtc = DateTime.MinValue }; },
                    delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e) { s = null; e = "no console"; return false; },
                    delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e) { e = null; return NativeWriteOutcome.NoInputCommitted; },
                    delegate { return clock; });
                engine3.LifecycleHealth = BuildLifecycleHealthContext(healthPath3, 4242, start, "--dry-run", "0123456789abcdef", false);
                engine3.PollOnce(false);
                string dryJson = File.ReadAllText(healthPath3, Encoding.UTF8);
                failures += AssertEqual("not_applicable", Audit163ExtractJsonString(dryJson, "state_store_disposition"), "CORE-005: dry-run reports the state store as not applicable");
                failures += AssertEqual(true, dryJson.IndexOf("\"state_store_healthy\":false", StringComparison.Ordinal) >= 0, "CORE-005: an absent store is never reported healthy");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
            return failures;
        }

        private static string Audit163ExtractJsonString(string json, string field)
        {
            string marker = "\"" + field + "\":\"";
            int start = json.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return null;
            start += marker.Length;
            int end = json.IndexOf('"', start);
            return end > start ? json.Substring(start, end - start) : null;
        }

        private static int Audit163_Core007_BoundedHeartbeat()
        {
            int failures = 0;
            string dir = Path.Combine(Path.GetTempPath(), "SAICONT.a163.core007." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string healthPath = Path.Combine(dir, "SAICONT.health.json");
                DateTime clock = new DateTime(2026, 9, 4, 13, 0, 0, DateTimeKind.Utc);
                TargetRule rule = Audit163ConsoleRule("core007", "codex", 40);
                var config = new WatcherConfiguration { PollIntervalMilliseconds = 2000, Targets = new List<TargetRule> { rule } };
                var engine = new WatcherEngine(
                    config,
                    delegate { return new List<ProcessEntry> { new ProcessEntry { Id = 7001, ParentId = 1, Name = "codex" } }; },
                    delegate(int pid, string name) { return new ProcessSessionIdentity { ProcessId = pid, ProcessName = name, StartTimeUtc = new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc) }; },
                    delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e) { s = Audit163Snapshot(pid, 8, "Ask anything"); e = null; return true; },
                    delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e) { e = null; return NativeWriteOutcome.NoInputCommitted; },
                    delegate { return clock; });
                engine.LifecycleHealth = BuildLifecycleHealthContext(healthPath, 7001, clock, "--watch", "tok", false);

                engine.PollOnce(false);
                failures += AssertEqual(1, engine.HealthWriteCount, "CORE-007: first publication writes exactly one snapshot");

                // 20 unchanged polls at the shipped 2-second cadence: the old
                // predicate (results.Count > 0) rewrote health every single one.
                for (int i = 1; i <= 20; i++)
                {
                    clock = clock.AddSeconds(2);
                    engine.PollOnce(false);
                }
                failures += AssertEqual(1, engine.HealthWriteCount, "CORE-007: 20 unchanged polls over 40s produce no additional health write");

                // Crossing the 45-second deadline refreshes exactly once.
                clock = clock.AddSeconds(6);
                engine.PollOnce(false);
                failures += AssertEqual(2, engine.HealthWriteCount, "CORE-007: the first poll past the heartbeat deadline refreshes exactly once");
                clock = clock.AddSeconds(2);
                engine.PollOnce(false);
                failures += AssertEqual(2, engine.HealthWriteCount, "CORE-007: the poll after the refresh is bounded again");

                // A material transition publishes immediately.
                var report = new HealthReport
                {
                    ProductVersion = "1.2",
                    ProcessId = 7001,
                    StateStoreDisposition = "healthy",
                    StateStoreHealthy = true,
                    ActiveSessions = 1,
                    HealthUpdatedUtc = clock
                };
                string before = WatcherEngine.SemanticHealthFingerprint(report);
                report.HealthUpdatedUtc = clock.AddMinutes(5);
                failures += AssertEqual(before, WatcherEngine.SemanticHealthFingerprint(report), "CORE-007: health_updated_utc alone does not change the semantic fingerprint");
                report.LastErrorCode = "console_unavailable";
                failures += AssertEqual(false, String.Equals(before, WatcherEngine.SemanticHealthFingerprint(report), StringComparison.Ordinal), "CORE-007: a contract field change does change the fingerprint");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
            return failures;
        }
    }
}
