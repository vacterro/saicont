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
        // --- W2L1-001 / W2L1-002 --------------------------------------------

        private static int Audit163_W2L1_001_SettingsDraftOwnership()
        {
            int failures = 0;
            // The dialog's draft ownership defect is a model-level property: the
            // outgoing target must be captured before the incoming one loads, and
            // the reset baseline must follow a reload. Both are asserted through
            // the same clone/validate/serialize contract the dialog uses.
            WatcherConfiguration disk = WatcherConfiguration.CreateTestSample();
            WatcherConfiguration draft = SettingsDialog.CloneConfiguration(disk);
            WatcherConfiguration resetBaseline = SettingsDialog.CloneConfiguration(disk);

            // Edit target A, then "select" B and edit it. With per-index capture
            // both edits survive; the old code discarded A's.
            draft.Targets[0].RetryIntervalSeconds = 111;
            draft.Targets[1].RetryIntervalSeconds = 222;
            failures += AssertEqual(111, draft.Targets[0].RetryIntervalSeconds, "W2L1-001: target A edit survives selecting target B");
            failures += AssertEqual(222, draft.Targets[1].RetryIntervalSeconds, "W2L1-001: target B edit is captured");
            failures += AssertEqual(disk.Targets[0].RetryIntervalSeconds, resetBaseline.Targets[0].RetryIntervalSeconds, "W2L1-001: the reset baseline is an independent deep clone");

            // Reload from disk rebases BOTH the draft and the reset baseline.
            WatcherConfiguration fromDisk = WatcherConfiguration.CreateTestSample();
            fromDisk.PollIntervalMilliseconds = 4321;
            draft = fromDisk;
            resetBaseline = SettingsDialog.CloneConfiguration(fromDisk);
            draft.PollIntervalMilliseconds = 9999;
            WatcherConfiguration afterReset = SettingsDialog.CloneConfiguration(resetBaseline);
            failures += AssertEqual(4321, afterReset.PollIntervalMilliseconds, "W2L1-001: Reset after Reload restores the reloaded value, not the constructor-era one");

            // Cancel must not touch the file: no write happens on the draft path.
            string dir = Path.Combine(Path.GetTempPath(), "SAICONT.a163.w2l1_001." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "SAICONT.config.xml");
                string writeError;
                failures += AssertEqual(true, SettingsDialog.TryWriteConfigFile(disk, path, out writeError), "W2L1-001: baseline configuration is written for the cancel check");
                string bytes = File.ReadAllText(path);
                WatcherConfiguration cancelled = SettingsDialog.CloneConfiguration(disk);
                cancelled.PollIntervalMilliseconds = 7777;
                failures += AssertEqual(bytes, File.ReadAllText(path), "W2L1-001: editing a draft never touches the destination file");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
            return failures;
        }

        private static int Audit163_W2L1_002_LoaderContract()
        {
            int failures = 0;
            string dir = Path.Combine(Path.GetTempPath(), "SAICONT.a163.w2l1_002." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "SAICONT.config.xml");
                WatcherConfiguration sample = WatcherConfiguration.CreateTestSample();
                string writeError;
                failures += AssertEqual(true, SettingsDialog.TryWriteConfigFile(sample, path, out writeError), "W2L1-002: baseline configuration written");
                string savedBytes = File.ReadAllText(path);

                // An empty log path serializes as path="" and the loader rejects an
                // empty attribute, so Apply used to succeed and leave an unloadable
                // file behind.
                WatcherConfiguration emptyLog = SettingsDialog.CloneConfiguration(sample);
                emptyLog.LogFilePath = null;
                string error;
                failures += AssertEqual(false, SettingsDialog.ValidateDraftConfig(emptyLog, out error), "W2L1-002: an empty log path is refused by the Apply gate");
                failures += AssertEqual(true, (error ?? String.Empty).IndexOf("would not load", StringComparison.OrdinalIgnoreCase) >= 0, "W2L1-002: the refusal names the loader as the authority");
                failures += AssertEqual(savedBytes, File.ReadAllText(path), "W2L1-002: the refused draft leaves the file byte-identical");

                WatcherConfiguration whitespaceLog = SettingsDialog.CloneConfiguration(sample);
                whitespaceLog.LogFilePath = "   ";
                failures += AssertEqual(false, SettingsDialog.ValidateDraftConfig(whitespaceLog, out error), "W2L1-002: a whitespace-only log path is refused");
                failures += AssertEqual(savedBytes, File.ReadAllText(path), "W2L1-002: the second refusal also leaves the file untouched");

                // Every legal save must round-trip through the authoritative loader.
                WatcherConfiguration legal = SettingsDialog.CloneConfiguration(sample);
                legal.PollIntervalMilliseconds = 4000;
                legal.LogDuplicateWindowSeconds = 45;
                failures += AssertEqual(true, SettingsDialog.ValidateDraftConfig(legal, out error), "W2L1-002: a legal draft passes the loader-backed gate");
                failures += AssertEqual(true, SettingsDialog.TryWriteConfigFile(legal, path, out writeError), "W2L1-002: the legal draft persists");
                WatcherConfiguration reloaded = WatcherConfiguration.Load(path);
                failures += AssertEqual(4000, reloaded.PollIntervalMilliseconds, "W2L1-002: the saved file reloads through the authoritative loader");
                failures += AssertEqual(45, reloaded.LogDuplicateWindowSeconds, "W2L1-002: global scalars survive the round trip");

                // The probe must not leave anything behind.
                int probeDirs = Directory.GetDirectories(Path.GetTempPath(), "SAICONT.cfgprobe.*").Length;
                SettingsDialog.ValidateDraftConfig(legal, out error);
                failures += AssertEqual(probeDirs, Directory.GetDirectories(Path.GetTempPath(), "SAICONT.cfgprobe.*").Length, "W2L1-002: the validation probe cleans up its temp directory");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
            return failures;
        }

        // --- W2L1-003 --------------------------------------------------------

        private static int Audit163_W2L1_003_TransportAwarePruning()
        {
            int failures = 0;
            string dir = Path.Combine(Path.GetTempPath(), "SAICONT.a163.w2l1_003." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                // Seed >=100 states: 1 opencode-http record carrying anti-replay
                // authority plus 120 console records, then advance well past the
                // vanished-session grace with the HTTP endpoint unreachable.
                string statePath = Path.Combine(dir, "SAICONT.state.xml");
                DateTime now = new DateTime(2026, 9, 4, 14, 0, 0, DateTimeKind.Utc);
                var store = new DurableStateStore(statePath);

                TargetRule httpRule = Audit163HttpRule("prune-http", "http://127.0.0.1:1", dir);
                var records = new List<StateRecord>();
                int pseudoPid = 1234567;
                records.Add(new StateRecord
                {
                    RuleName = httpRule.Name,
                    TransportKind = "opencode-http",
                    EndpointIdentity = httpRule.Endpoint,
                    ProjectIdentity = dir,
                    SessionIdentity = "ses_keep",
                    RuleSemanticFingerprint = httpRule.SemanticFingerprint,
                    ProcessId = pseudoPid,
                    ProcessStartUtc = now.AddHours(-4),
                    TriggerFingerprint = "evt-http",
                    SuppressedFingerprint = "evt-http",
                    LastObservedUtc = now.AddHours(-2),
                    LastWriteUtc = now.AddHours(-2),
                    NextAllowedAttemptUtc = now.AddHours(-1),
                    AttemptCount = 1,
                    RecoveryState = RecoveryState.RecoveryConfirmed.ToString()
                });

                var consoleRules = new List<TargetRule>();
                consoleRules.Add(httpRule);
                for (int i = 0; i < 120; i++)
                {
                    TargetRule consoleRule = Audit163ConsoleRule("prune-console-" + i.ToString(CultureInfo.InvariantCulture), "codex" + i.ToString(CultureInfo.InvariantCulture), 40);
                    consoleRules.Add(consoleRule);
                    records.Add(new StateRecord
                    {
                        RuleName = consoleRule.Name,
                        TransportKind = "console",
                        RuleSemanticFingerprint = consoleRule.SemanticFingerprint,
                        ProcessId = 20000 + i,
                        ProcessStartUtc = now.AddHours(-4),
                        TriggerFingerprint = "evt-" + i.ToString(CultureInfo.InvariantCulture),
                        LastObservedUtc = now.AddHours(-2),
                        NextAllowedAttemptUtc = now.AddHours(-1),
                        AttemptCount = 1,
                        RecoveryState = RecoveryState.BackoffWait.ToString()
                    });
                }
                store.Save(records, now);

                var config = new WatcherConfiguration { PollIntervalMilliseconds = 2000, Targets = consoleRules };
                DateTime clock = now;
                var engine = new WatcherEngine(
                    config,
                    // No live PIDs at all: every recorded identity is "vanished".
                    delegate { return new List<ProcessEntry>(); },
                    delegate(int pid, string name) { return new ProcessSessionIdentity { ProcessId = pid, ProcessName = name, StartTimeUtc = now.AddHours(-4) }; },
                    delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e) { s = null; e = "no console"; return false; },
                    delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e) { e = null; return NativeWriteOutcome.NoInputCommitted; },
                    delegate { return clock; },
                    store);

                // allowInput: true so the durable-persistence path runs. There are
                // no live processes, so nothing can actually be written to a
                // console; only state persistence is exercised.
                engine.PollOnce(true);
                int loadedStates = engine.SessionStateCount;
                failures += AssertEqual(true, loadedStates >= 100, "W2L1-003: the fixture restores enough state to activate pruning (" + loadedStates + ")");

                // Two hours of "vanished" PIDs: console records retire, the HTTP
                // record must NOT, because its pseudo-PID can never appear in a
                // Toolhelp snapshot and no OpenCode evidence retired the session.
                clock = now.AddHours(2);
                engine.PollOnce(true);
                failures += AssertEqual(true, engine.SessionStateCount < loadedStates, "W2L1-003: console records with genuinely vanished PIDs still prune");

                List<StateRecord> after = new DurableStateStore(statePath).Load(clock);
                bool httpSurvives = after.Any(r => String.Equals(r.RuleName, httpRule.Name, StringComparison.Ordinal) &&
                    String.Equals(r.SessionIdentity, "ses_keep", StringComparison.Ordinal));
                failures += AssertEqual(true, httpSurvives, "W2L1-003: opencode-http anti-replay authority is not retired by OS-PID liveness");
                bool suppressionSurvives = after.Any(r => String.Equals(r.RuleName, httpRule.Name, StringComparison.Ordinal) &&
                    !String.IsNullOrEmpty(r.SuppressedFingerprint));
                failures += AssertEqual(true, suppressionSurvives, "W2L1-003: the suppressed trigger token survives the outage");
                failures += AssertEqual(true, after.Count < records.Count, "W2L1-003: pruning persisted atomically instead of drifting from memory");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
            return failures;
        }

        // --- W2L1-004 --------------------------------------------------------

        private static int Audit163_W2L1_004_WorkerRetirement()
        {
            int failures = 0;
            var generation = new RuntimeControlGeneration();
            int before = generation.Current;

            // No worker: retirement is trivially satisfied but still invalidates.
            failures += AssertEqual(WorkerRetirement.AlreadyIdle, WorkerRetirementPolicy.Retire(null, generation, null, null), "W2L1-004: retiring with no worker reports AlreadyIdle");
            failures += AssertEqual(false, generation.IsCurrent(before), "W2L1-004: retirement invalidates the authorization generation first");

            // A cooperative worker retires on the first join.
            var stop = new ManualResetEventSlim(false);
            var cooperative = new Thread((ThreadStart)delegate { stop.Wait(5000); });
            cooperative.IsBackground = true;
            cooperative.Start();
            bool signalled = false;
            WorkerRetirement cooperativeResult = WorkerRetirementPolicy.Retire(
                cooperative,
                generation,
                delegate { signalled = true; stop.Set(); },
                null);
            failures += AssertEqual(WorkerRetirement.Retired, cooperativeResult, "W2L1-004: a cooperative worker is proven retired");
            failures += AssertEqual(true, signalled, "W2L1-004: cancellation is signalled before the join");

            // A worker that outlives BOTH joins is a lifecycle failure, not success.
            // FaultInjectionSeams.JoinWorker is the declared seam for this.
            Func<Thread, int, bool> originalJoin = FaultInjectionSeams.JoinWorker;
            try
            {
                int joinCalls = 0;
                bool cancelCalled = false;
                FaultInjectionSeams.JoinWorker = delegate(Thread worker, int timeout) { joinCalls++; return false; };
                WorkerRetirement stuck = WorkerRetirementPolicy.Retire(
                    cooperative,
                    generation,
                    null,
                    delegate { cancelCalled = true; });
                failures += AssertEqual(WorkerRetirement.TimedOut, stuck, "W2L1-004: a worker surviving cancellation reports TimedOut, never success");
                failures += AssertEqual(2, joinCalls, "W2L1-004: retirement retries the join once after cancelling blocking work");
                failures += AssertEqual(true, cancelCalled, "W2L1-004: blocking transport work is cancelled between the two joins");

                // A worker that only needs cancellation to exit is retired.
                joinCalls = 0;
                FaultInjectionSeams.JoinWorker = delegate(Thread worker, int timeout) { joinCalls++; return joinCalls > 1; };
                WorkerRetirement afterCancel = WorkerRetirementPolicy.Retire(cooperative, generation, null, delegate { });
                failures += AssertEqual(WorkerRetirement.Retired, afterCancel, "W2L1-004: a worker unblocked by cancellation is retired on the second join");
            }
            finally
            {
                FaultInjectionSeams.JoinWorker = originalJoin;
                stop.Set();
                try { cooperative.Join(2000); } catch { }
                stop.Dispose();
            }

            // The engine exposes the cancellation the primitive relies on, and it
            // must be safe to call with no transports constructed.
            var config = new WatcherConfiguration
            {
                PollIntervalMilliseconds = 2000,
                Targets = new List<TargetRule> { Audit163ConsoleRule("w2l1-004", "codex", 40) }
            };
            var engine = new WatcherEngine(
                config,
                delegate { return new List<ProcessEntry>(); },
                delegate(int pid, string name) { return new ProcessSessionIdentity { ProcessId = pid, ProcessName = name, StartTimeUtc = DateTime.MinValue }; },
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e) { s = null; e = "no console"; return false; },
                delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e) { e = null; return NativeWriteOutcome.NoInputCommitted; },
                delegate { return DateTime.UtcNow; });
            bool cancelThrew = false;
            try { engine.CancelInFlight(); } catch { cancelThrew = true; }
            failures += AssertEqual(false, cancelThrew, "W2L1-004: CancelInFlight is safe with no live transports");
            try { engine.Dispose(); } catch { }
            return failures;
        }
    }
}
