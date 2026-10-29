using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace SaiCont
{
    // T-164 (SRC-006): regressions for the re-audit's NEW findings -- W2-001
    // TUI/console pre-write authorization boundaries, W2-007 probe completion
    // generation, and PERF-006 bounded quarantine/redirect retention.
    internal static partial class Program
    {
        private static int RunAudit164SelfTests()
        {
            int failures = 0;
            failures += Audit164_W2001_ConsoleAuthorizationBoundary();
            failures += Audit164_W2001_HttpPreReservationRevocation();
            failures += Audit164_Perf006_QuarantineRetention();
            failures += PublicationRegressionTests.Run();
            return failures;
        }

        private static WatcherEngine Audit164Engine(
            WatcherConfiguration configuration,
            Func<bool> writerGate,
            List<string> writeLog,
            Func<DateTime> clock)
        {
            return new WatcherEngine(
                configuration,
                delegate { return new List<ProcessEntry> { new ProcessEntry { Id = 7311, ParentId = 1, Name = "a164proc" } }; },
                delegate(int pid, string name) { return new ProcessSessionIdentity { ProcessId = pid, ProcessName = name, StartTimeUtc = new DateTime(2026, 9, 5, 11, 0, 0, DateTimeKind.Utc) }; },
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                {
                    s = Audit164Snapshot(pid, 6, "Ask anything");
                    s.Text = "rate limited" + Environment.NewLine + s.Text;
                    e = null;
                    return true;
                },
                delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e)
                {
                    e = null;
                    if (writerGate != null && !writerGate()) return NativeWriteOutcome.NoInputCommitted;
                    if (writeLog != null) writeLog.Add("written");
                    return NativeWriteOutcome.CompleteInputCommitted;
                },
                delegate { return clock(); });
        }

        private static int Audit164_W2001_ConsoleAuthorizationBoundary()
        {
            int failures = 0;
            string dir = Path.Combine(Path.GetTempPath(), "saicont-a164-w2001-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            List<string> writes = null;
            bool revocation = false;
            try
            {
                TargetRule rule = Audit163ConsoleRule("a164-w2001", "a164proc", 40);
                rule.ProcessNames = new[] { "a164proc" };
                rule.InitialDelaySeconds = 0;
                var config = new WatcherConfiguration
                {
                    PollIntervalMilliseconds = 2000,
                    Targets = new List<TargetRule> { rule }
                };
                var stateStore = new DurableStateStore(Path.Combine(dir, "SAICONT.state.xml"));
                writes = new List<string>();
                DateTime clockValue = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
                WatcherEngine engine = Audit164Engine(config, null, writes, delegate { return clockValue; });
                try
                {
                    // First poll arms the bounded event (SafeInitialDelay floor);
                    // the second poll, past the delay, reaches the send path.
                    engine.PollOnce(true, null, null);

                    // The late revocation flips BETWEEN the early authorization
                    // check and the final pre-write boundary, exactly the window
                    // the TUI WATCH -> DRY-RUN transition used to expose. The
                    // first invocation (early gate) passes; every later one is
                    // revoked, so the final pre-write boundary must see revoked.
                    clockValue = new DateTime(2026, 9, 5, 12, 0, 30, 0, DateTimeKind.Utc);
                    int snapshotCalls = 0;
                    IList<PollResult> results = engine.PollOnce(
                        true,
                        null,
                        delegate
                        {
                            // Call 1: the early gate; call 2: the mid-poll barrier;
                            // call 3: the FINAL pre-write boundary. Flipping here
                            // proves the last check exists after re-resolution.
                            snapshotCalls++;
                            revocation = snapshotCalls >= 3;
                            return revocation;
                        });
                    bool blockedRow = results.Any(r =>
                        String.Equals(r.Reason, "send_blocked=authorization_revoked", StringComparison.Ordinal));
                    failures += AssertEqual(true, blockedRow, "W2-001: the console FINAL pre-write boundary reports authorization_revoked");
                    failures += AssertEqual(0, writes.Count, "W2-001: no native write commits after late revocation");
                }
                finally { try { engine.Dispose(); } catch { } }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
            return failures;
        }

        private static int Audit164_W2001_HttpPreReservationRevocation()
        {
            int failures = 0;
            var fake = new Audit163FakeOpenCode();
            TargetRule rule;
            using (var scope = new Audit163Disposable(Audit163Transport(fake, false, out rule)))
            {
                OpenCodeHttpTransport transport = scope.Transport;
                DateTime now = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
                transport.SetStreamStateForTests(true, true);
                transport.SetSessionStateForTests(fake.SessionId, OpenCodeStatusKind.Idle, null, null);
                string error;
                TargetObservation sendable = transport.Poll(rule, now, out error);
                sendable.OwnerStateKey = rule.Name + ":1:2026";

                // Candidate validation passes, then the authorization generation
                // is revoked BEFORE the durable reservation / POST path. The send
                // must be refused with a stable decision, not committed.
                failures += AssertEqual(true, transport.ValidatePreSend(sendable, rule, out error), "W2-001: candidate validation still passes pre-revocation");
                fake.Reset();
                bool revoked = true;
                try
                {
                    string sendError;
                    // The engine-level refusal is the send path with a revoked
                    // commit callback: the transport must block the POST.
                    SendOutcome outcome = transport.Send(
                        sendable,
                        rule,
                        rule.Command,
                        delegate { return !revoked; },
                        out sendError);
                    failures += AssertEqual(true, outcome == null || outcome.Disposition != SendDisposition.CompleteInputCommitted, "W2-001: a revoked HTTP authorization never commits the POST");
                    failures += AssertEqual(0, fake.Post, "W2-001: no POST request is issued after revocation");
                }
                catch (Exception ex)
                {
                    failures += AssertEqual("no-exception", ex.GetType().Name, "W2-001: revocation is a stable decision, not a throw");
                }
            }
            return failures;
        }

        private static int Audit164_Perf006_QuarantineRetention()
        {
            int failures = 0;
            string dir = Path.Combine(Path.GetTempPath(), "saicont-a164-perf006-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string statePath = Path.Combine(dir, "SAICONT.state.xml");
            try
            {
                // Write MAXIMUMQUARANTINES + 3 corrupt generations. Load()
                // quarantines each corrupt file; the subsequent reconcile
                // establishes the fresh authoritative ledger, exactly the
                // production corruption/recovery cycle.
                for (int i = 0; i < DurableStateStore.MaximumQuarantines + 3; i++)
                {
                    File.WriteAllText(statePath, "<root><corrupt");
                    var store = new DurableStateStore(statePath);
                    store.Load();
                    string reconcileError;
                    store.TryReconcile(new List<StateRecord>(), new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc).AddMinutes(i), out reconcileError);
                }
                int quarantines = Directory.GetFiles(dir, "SAICONT.state.xml.corrupt.*").Length;
                failures += AssertEqual(DurableStateStore.MaximumQuarantines, quarantines,
                    "PERF-006: quarantine count settles at the bounded retention window");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
            return failures;
        }

        private static ConsoleSnapshot Audit164Snapshot(int pid, int lineCount, string tailLine)
        {
            var text = new StringBuilder();
            for (int i = 0; i < lineCount - 1; i++)
            {
                text.Append("line-").Append(i.ToString(CultureInfo.InvariantCulture)).Append(Environment.NewLine);
            }
            text.Append(tailLine);
            return new ConsoleSnapshot
            {
                ProcessId = pid,
                WindowHandle = IntPtr.Zero,
                Title = "console-" + pid,
                Text = text.ToString(),
                CursorLine = tailLine,
                CursorColumn = 0,
                CursorRow = lineCount - 1,
                StartRow = 0,
                BufferWidth = 120,
                ConsoleProcessIds = new[] { pid },
                MembershipStatus = ConsoleMembershipStatus.VerifiedPresent,
                MembershipError = null
            };
        }
    }
}
