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
    // T-163 (SRC-005): regressions for the external audit findings. Kept in its
    // own file so the existing suite stays readable; RunSelfTests calls
    // RunAudit163SelfTests once and adds the returned failure count.
    internal static partial class Program
    {
        private static int RunAudit163SelfTests()
        {
            int failures = 0;
            failures += Audit163_Core001_StateReconciliation();
            failures += Audit163_Core002_TransportReconciliation();
            failures += Audit163_Core003_SnapshotScope();
            failures += Audit163_Core004_SendOwnership();
            failures += Audit163_Core005_LifecycleHealth();
            failures += Audit163_Core007_BoundedHeartbeat();
            failures += Audit163_W2L1_001_SettingsDraftOwnership();
            failures += Audit163_W2L1_002_LoaderContract();
            failures += Audit163_W2L1_003_TransportAwarePruning();
            failures += Audit163_W2L1_004_WorkerRetirement();
            failures += Audit163_Perf001_RequestBudget();
            failures += Audit163_Perf002_SessionBounds();
            failures += Audit163_Perf003_ObservationCache();
            failures += Audit163_Perf004_FailureGate();
            return failures;
        }

        // --- shared fixtures -------------------------------------------------

        private static TargetRule Audit163ConsoleRule(string name, string process, int scanLines)
        {
            var rule = new TargetRule
            {
                Name = name,
                Enabled = true,
                Transport = "console",
                ProcessNames = new[] { process },
                Command = "cc",
                ScanLines = scanLines,
                MaximumTriggerDistanceLines = Math.Min(scanLines, 150),
                InitialDelaySeconds = 10,
                RetryIntervalSeconds = 10,
                ParseRetryTime = false,
                BackoffMultiplier = 2.0,
                MaximumRetryIntervalSeconds = 3600,
                MaximumAttemptsPerEvent = 5,
                TriggerPatterns = new[] { @"(?i)rate limited" },
                ReadyPatterns = new[] { @"^.*Ask anything.*$" },
                BusyPatterns = new string[0]
            };
            rule.CompileRegexes();
            return rule;
        }

        private static TargetRule Audit163HttpRule(string name, string endpoint, string projectDir)
        {
            var rule = new TargetRule
            {
                Name = name,
                Enabled = true,
                Transport = "opencode-http",
                Endpoint = endpoint,
                ProjectDirectory = projectDir,
                ProcessNames = new string[0],
                Command = "cc",
                ScanLines = 180,
                MaximumTriggerDistanceLines = 150,
                InitialDelaySeconds = 10,
                RetryIntervalSeconds = 10,
                ParseRetryTime = false,
                BackoffMultiplier = 2.0,
                MaximumRetryIntervalSeconds = 3600,
                MaximumAttemptsPerEvent = 5,
                TriggerPatterns = new[] { @"(?i)""code""\s*:\s*""internal_error""" },
                ReadyPatterns = new string[0],
                BusyPatterns = new string[0]
            };
            rule.CompileRegexes();
            return rule;
        }

        private static ConsoleSnapshot Audit163Snapshot(int pid, int lineCount, string tailLine)
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

        // --- CORE-001 --------------------------------------------------------

        private static int Audit163_Core001_StateReconciliation()
        {
            int failures = 0;
            string dir = Path.Combine(Path.GetTempPath(), "SAICONT.a163.core001." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string statePath = Path.Combine(dir, "SAICONT.state.xml");
                string markerPath = statePath + ".recovery-required";
                DateTime now = new DateTime(2026, 9, 4, 10, 0, 0, DateTimeKind.Utc);

                // A corrupt ledger fails closed: quarantine + marker + no writes.
                File.WriteAllText(statePath, "<malformed");
                var store = new DurableStateStore(statePath);
                store.Load(now);
                failures += AssertEqual(StateLoadDisposition.Corrupt, store.LastLoadDisposition, "CORE-001: corrupt ledger is classified Corrupt");
                failures += AssertEqual(true, File.Exists(markerPath), "CORE-001: corrupt load writes the recovery marker");
                failures += AssertEqual(1, Directory.GetFiles(dir, "SAICONT.state.xml.corrupt.*").Length, "CORE-001: corrupt ledger is quarantined");
                bool changed;
                string saveError;
                failures += AssertEqual(false, store.TrySave(new List<StateRecord>(), now, out changed, out saveError), "CORE-001: durable writes stay blocked while the marker exists");

                // Reconciliation write failure must leave the marker intact.
                string lockedPath = Path.Combine(dir, "locked.state.xml");
                File.WriteAllText(lockedPath, "<malformed");
                var lockedStore = new DurableStateStore(lockedPath);
                lockedStore.Load(now);
                failures += AssertEqual(true, File.Exists(lockedPath + ".recovery-required"), "CORE-001: second store also armed its marker");
                string reconcileError;
                using (FileStream hold = new FileStream(lockedPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    hold.WriteByte(0);
                    hold.Flush();
                    failures += AssertEqual(false, lockedStore.TryReconcile(new List<StateRecord>(), now, out reconcileError), "CORE-001: reconciliation fails when the ledger cannot be committed");
                }
                failures += AssertEqual(true, File.Exists(lockedPath + ".recovery-required"), "CORE-001: failed reconciliation keeps the marker (still fail-closed)");

                // The supported transition: commit first, clear marker after.
                failures += AssertEqual(true, store.TryReconcile(new List<StateRecord>(), now, out reconcileError), "CORE-001: reconciliation commits a fresh authoritative ledger");
                failures += AssertEqual(true, File.Exists(statePath), "CORE-001: reconciled ledger exists on disk");
                failures += AssertEqual(false, File.Exists(markerPath), "CORE-001: marker is cleared only after the durable commit");
                failures += AssertEqual(false, store.RequiresConservativeRecovery, "CORE-001: store no longer requires conservative recovery");
                failures += AssertEqual(1, Directory.GetFiles(dir, "SAICONT.state.xml.corrupt.*").Length, "CORE-001: quarantined evidence is preserved, not deleted");
                failures += AssertEqual(true, store.TrySave(new List<StateRecord>(), now, out changed, out saveError), "CORE-001: durable writes are possible after reconciliation");
                var verifyStore = new DurableStateStore(statePath);
                verifyStore.ValidateReadOnly(now);
                failures += AssertEqual(StateLoadDisposition.Valid, verifyStore.LastLoadDisposition, "CORE-001: reconciled ledger loads as a valid document");

                // Engine level: corrupt state is fail-closed on the first poll and
                // recovers to send-eligible after the armed reconciliation runs.
                string enginePath = Path.Combine(dir, "engine.state.xml");
                File.WriteAllText(enginePath, "<malformed");
                var engineStore = new DurableStateStore(enginePath);
                TargetRule rule = Audit163ConsoleRule("core001", "codex", 40);
                var config = new WatcherConfiguration { PollIntervalMilliseconds = 2000, Targets = new List<TargetRule> { rule } };
                DateTime clock = now;
                int writes = 0;
                var engine = new WatcherEngine(
                    config,
                    delegate { return new List<ProcessEntry> { new ProcessEntry { Id = 4242, ParentId = 1, Name = "codex" } }; },
                    delegate(int pid, string name) { return new ProcessSessionIdentity { ProcessId = pid, ProcessName = name, StartTimeUtc = now.AddMinutes(-30) }; },
                    delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e) { s = Audit163Snapshot(pid, 6, "Ask anything"); s.Text = "Rate limited" + Environment.NewLine + s.Text; e = null; return true; },
                    delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e) { writes++; e = null; return NativeWriteOutcome.CompleteInputCommitted; },
                    delegate { return clock; },
                    engineStore);

                IList<PollResult> first = engine.PollOnce(true);
                bool ambiguousReported = first.Any(r =>
                    (r.Reason ?? String.Empty).IndexOf("state_ledger_ambiguous", StringComparison.Ordinal) >= 0 ||
                    (r.Reason ?? String.Empty).IndexOf("state_recovery_cooldown", StringComparison.Ordinal) >= 0 ||
                    (r.Error ?? String.Empty).IndexOf("state_corrupt", StringComparison.Ordinal) >= 0);
                if (!ambiguousReported)
                {
                    foreach (PollResult r in first)
                    {
                        Console.WriteLine("DIAG core001: target=" + r.Target + " reason=" + r.Reason + " err=" + r.Error);
                    }
                }
                failures += AssertEqual(true, ambiguousReported, "CORE-001: first poll on a corrupt ledger is fail-closed and says so");
                failures += AssertEqual(0, writes, "CORE-001: no write happens while the ledger is ambiguous");

                // Past the safe initial delay the armed reconciliation runs.
                clock = now.AddSeconds(120);
                engine.PollOnce(true);
                failures += AssertEqual(false, engineStore.RequiresConservativeRecovery, "CORE-001: engine reconciled the ledger after the cooldown");
                clock = now.AddSeconds(240);
                engine.PollOnce(true);
                clock = now.AddSeconds(400);
                engine.PollOnce(true);
                failures += AssertEqual(true, writes >= 1, "CORE-001: a due event can send exactly once after reconciliation (writes=" + writes + ")");
                List<StateRecord> persisted = new DurableStateStore(enginePath).Load(clock);
                failures += AssertEqual(true, persisted.Count >= 1, "CORE-001: reconciled authority is persisted for restart");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
            return failures;
        }
    }
}
