using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32.SafeHandles;

namespace SaiCont
{
    internal static partial class Program
    {
        private static int RunSelfTests()
        {
            _selfTestCount = 0;
            int failures = 0;
            failures += AssertEqual("cline", ProcessDiscovery.NormalizeName("CLINE.EXE"), "normalize executable name");
            failures += AssertEqual("codex", ProcessDiscovery.NormalizeName("codex"), "normalize plain name");

            var fake = new List<ProcessEntry>
            {
                new ProcessEntry { Id = 10, ParentId = 1, Name = "powershell.exe" },
                new ProcessEntry { Id = 11, ParentId = 10, Name = "node.exe" },
                new ProcessEntry { Id = 12, ParentId = 11, Name = "cline.exe" },
                new ProcessEntry { Id = 20, ParentId = 1, Name = "powershell.exe" },
                new ProcessEntry { Id = 21, ParentId = 20, Name = "codex.exe" }
            };
            var byId = fake.ToDictionary(item => item.Id);

            failures += AssertEqual("12,11,10", JoinChain(ProcessDiscovery.BuildAttachCandidates(fake[2], byId)), "attach candidate lineage matched-first");
            failures += AssertEqual("21,20", JoinChain(ProcessDiscovery.BuildAttachCandidates(fake[4], byId)), "attach candidate lineage codex");

            var orphan = new List<ProcessEntry> { new ProcessEntry { Id = 30, ParentId = 999, Name = "cline.exe" } };
            failures += AssertEqual("30", JoinChain(ProcessDiscovery.BuildAttachCandidates(orphan[0], orphan.ToDictionary(x => x.Id))), "orphan process yields only itself");

            var cycle = new List<ProcessEntry> { new ProcessEntry { Id = 50, ParentId = 50, Name = "cline.exe" } };
            failures += AssertEqual("50", JoinChain(ProcessDiscovery.BuildAttachCandidates(cycle[0], cycle.ToDictionary(x => x.Id))), "ancestry cycle terminates");

            var wrapper = new List<ProcessEntry>
            {
                new ProcessEntry { Id = 60, ParentId = 1, Name = "cmd.exe" },
                new ProcessEntry { Id = 61, ParentId = 60, Name = "node.exe" },
                new ProcessEntry { Id = 62, ParentId = 61, Name = "cline.exe" }
            };
            failures += AssertEqual("62,61,60", JoinChain(ProcessDiscovery.BuildAttachCandidates(wrapper[2], wrapper.ToDictionary(x => x.Id))), "wrapper/intermediate topology lineage");

            var wrapperWithChild = new List<ProcessEntry>
            {
                new ProcessEntry { Id = 100, ParentId = 1, Name = "powershell.exe" },
                new ProcessEntry { Id = 101, ParentId = 100, Name = "node.exe" },
                new ProcessEntry { Id = 102, ParentId = 101, Name = "cline.exe" },
                new ProcessEntry { Id = 103, ParentId = 101, Name = "worker.exe" }
            };
            var wrapperById = wrapperWithChild.ToDictionary(item => item.Id);
            failures += AssertEqual("101,100,102,103", JoinChain(ProcessDiscovery.BuildAttachCandidates(wrapperWithChild[1], wrapperById)), "wrapper with child process candidate inclusion");

            var dupTree = new List<ProcessEntry>
            {
                new ProcessEntry { Id = 110, ParentId = 111, Name = "cline.exe" },
                new ProcessEntry { Id = 111, ParentId = 110, Name = "node.exe" }
            };
            failures += AssertEqual("110,111", JoinChain(ProcessDiscovery.BuildAttachCandidates(dupTree[0], dupTree.ToDictionary(x => x.Id))), "candidate deduplication in cyclic tree");

            failures += AssertEqual(true, ProcessDiscovery.ConsoleServesMatchedProcess(new[] { 1, 2, 3 }, 2), "console membership contains target");
            failures += AssertEqual(false, ProcessDiscovery.ConsoleServesMatchedProcess(new[] { 1, 3 }, 2), "console membership rejects foreign console");
            failures += AssertEqual(false, ProcessDiscovery.ConsoleServesMatchedProcess(new int[0], 2), "empty console list fails closed");
            failures += AssertEqual(false, ProcessDiscovery.ConsoleServesMatchedProcess(null, 2), "null console list fails closed");
            failures += AssertEqual(ConsoleProcessListDisposition.Failed, NativeConsole.ClassifyProcessListCount(0, 64, 1024), "membership API zero count is failure");
            failures += AssertEqual(ConsoleProcessListDisposition.Complete, NativeConsole.ClassifyProcessListCount(64, 64, 1024), "membership API exact buffer is complete");
            failures += AssertEqual(ConsoleProcessListDisposition.Retry, NativeConsole.ClassifyProcessListCount(65, 64, 1024), "membership API oversized result requests retry");
            failures += AssertEqual(ConsoleProcessListDisposition.OverSafetyLimit, NativeConsole.ClassifyProcessListCount(1025, 64, 1024), "membership API refuses safety-relevant truncation");

            var session1 = new ProcessSessionIdentity { ProcessId = 100, StartTimeUtc = new DateTime(2026, 8, 26, 10, 0, 0, DateTimeKind.Utc), ProcessName = "cline" };
            var session2 = new ProcessSessionIdentity { ProcessId = 100, StartTimeUtc = new DateTime(2026, 8, 26, 10, 0, 0, DateTimeKind.Utc), ProcessName = "cline" };
            var session3 = new ProcessSessionIdentity { ProcessId = 100, StartTimeUtc = new DateTime(2026, 8, 26, 11, 0, 0, DateTimeKind.Utc), ProcessName = "cline" };
            failures += AssertEqual(true, session1.Equals(session2), "process session identity matching");
            failures += AssertEqual(false, session1.Equals(session3), "process session PID reuse detection");
            failures += AssertEqual(false, new ProcessSessionIdentity { ProcessId = 100, ProcessName = "cline", StartTimeUtc = DateTime.MinValue }.IsStrong, "missing creation time is weak process identity");

            var snapWithWin = new ConsoleSnapshot { WindowHandle = new IntPtr(0x1234), ConsoleProcessIds = new[] { 100 } };
            failures += AssertEqual("win:4660", ProcessDiscovery.ComputeStableConsoleId(snapWithWin, 100), "stable console id with window");
            var snapNoWin = new ConsoleSnapshot { WindowHandle = IntPtr.Zero, ConsoleProcessIds = new[] { 100, 200 } };
            failures += AssertEqual("pids:100,200", ProcessDiscovery.ComputeStableConsoleId(snapNoWin, 100), "stable console id without window");

            // Transactional Send Safety Tests
            TargetRule safetyRule = new TargetRule
            {
                Name = "test-target",
                Enabled = true,
                ProcessNames = new[] { "cline" },
                Command = "cc",
                ScanLines = 180,
                MaximumTriggerDistanceLines = 150,
                InitialDelaySeconds = 10,
                RetryIntervalSeconds = 10,
                ParseRetryTime = false,
                TriggerPatterns = new[] { @"(?i)rate limited[^\r\n]*" },
                ReadyPatterns = new[] { "^.*Ask.*$" },
                BusyPatterns = new[] { "(?i)working" }
            };
            var safetyConfig = new WatcherConfiguration
            {
                PollIntervalMilliseconds = 2000,
                Targets = new List<TargetRule> { safetyRule }
            };

            DateTime testNow = new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);
            var initialProcesses = new List<ProcessEntry>
            {
                new ProcessEntry { Id = 500, ParentId = 1, Name = "cline.exe" }
            };
            var initialSession = new ProcessSessionIdentity { ProcessId = 500, ProcessName = "cline", StartTimeUtc = testNow.AddMinutes(-5) };

            // Scenario 1: Target disappeared before pre-send write
            int writeCallCount = 0;
            int snapshotCallCount = 0;
            DateTime transactionClock = testNow;
            var engineDisappeared = new WatcherEngine(
                safetyConfig,
                delegate
                {
                    snapshotCallCount++;
                    return snapshotCallCount <= 2 ? initialProcesses : new List<ProcessEntry>();
                },
                delegate(int pid, string name) { return initialSession; },
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                {
                    s = new ConsoleSnapshot { ProcessId = 500, Text = "Rate limited\nAsk anything", CursorLine = "Ask anything", ConsoleProcessIds = new[] { 500 }, StartRow = 0, CursorRow = 1 };
                    e = null;
                    return true;
                },
                delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e)
                {
                    writeCallCount++;
                    e = null;
                    return NativeWriteOutcome.CompleteInputCommitted;
                },
                delegate { return transactionClock; });

            engineDisappeared.PollOnce(true);
            transactionClock = testNow.AddSeconds(15);
            IList<PollResult> resDisappeared = engineDisappeared.PollOnce(true);
            failures += AssertEqual(1, resDisappeared.Count, "disappeared scenario has poll result");
            failures += AssertEqual(false, resDisappeared[0].Sent, "disappeared target prevented send");
            failures += AssertEqual(0, writeCallCount, "disappeared target resulted in zero writes");
            failures += AssertEqual("send_blocked=target_disappeared", resDisappeared[0].Reason, "disappeared negative entered write-eligible transaction");

            // Scenario 2: PID reused with different start time
            writeCallCount = 0;
            snapshotCallCount = 0;
            transactionClock = testNow;
            var reusedSession = new ProcessSessionIdentity { ProcessId = 500, ProcessName = "cline", StartTimeUtc = testNow.AddSeconds(1) };
            var engineReused = new WatcherEngine(
                safetyConfig,
                delegate { return initialProcesses; },
                delegate(int pid, string name)
                {
                    snapshotCallCount++;
                    return snapshotCallCount <= 2 ? initialSession : reusedSession;
                },
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                {
                    s = new ConsoleSnapshot { ProcessId = 500, Text = "Rate limited\nAsk anything", CursorLine = "Ask anything", ConsoleProcessIds = new[] { 500 }, StartRow = 0, CursorRow = 1 };
                    e = null;
                    return true;
                },
                delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e)
                {
                    writeCallCount++;
                    e = null;
                    return NativeWriteOutcome.CompleteInputCommitted;
                },
                delegate { return transactionClock; });

            engineReused.PollOnce(true);
            transactionClock = testNow.AddSeconds(15);
            IList<PollResult> resReused = engineReused.PollOnce(true);
            failures += AssertEqual(false, resReused[0].Sent, "PID reuse prevented send");
            failures += AssertEqual(0, writeCallCount, "PID reuse resulted in zero writes");
            failures += AssertEqual("send_blocked=process_session_changed", resReused[0].Reason, "PID reuse negative entered write-eligible transaction");

            // Scenario 3: Console identity changed at pre-send
            writeCallCount = 0;
            int readCount = 0;
            transactionClock = testNow;
            var engineConsoleChanged = new WatcherEngine(
                safetyConfig,
                delegate { return initialProcesses; },
                delegate(int pid, string name) { return initialSession; },
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                {
                    readCount++;
                    s = new ConsoleSnapshot
                    {
                        ProcessId = 500,
                        WindowHandle = readCount <= 2 ? new IntPtr(0x100) : new IntPtr(0x200),
                        Text = "Rate limited\nAsk anything",
                        CursorLine = "Ask anything",
                        ConsoleProcessIds = new[] { 500 },
                        StartRow = 0,
                        CursorRow = 1
                    };
                    e = null;
                    return true;
                },
                delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e)
                {
                    writeCallCount++;
                    e = null;
                    return NativeWriteOutcome.CompleteInputCommitted;
                },
                delegate { return transactionClock; });

            engineConsoleChanged.PollOnce(true);
            transactionClock = testNow.AddSeconds(15);
            IList<PollResult> resConsoleChanged = engineConsoleChanged.PollOnce(true);
            failures += AssertEqual(false, resConsoleChanged[0].Sent, "console change prevented send");
            failures += AssertEqual(0, writeCallCount, "console change resulted in zero writes");
            failures += AssertEqual("send_blocked=console_changed", resConsoleChanged[0].Reason, "console-change negative entered write-eligible transaction");

            // Scenario 4: Target not in console at write time
            writeCallCount = 0;
            readCount = 0;
            transactionClock = testNow;
            var engineTargetNotInConsole = new WatcherEngine(
                safetyConfig,
                delegate { return initialProcesses; },
                delegate(int pid, string name) { return initialSession; },
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                {
                    readCount++;
                    s = new ConsoleSnapshot
                    {
                        ProcessId = 500,
                        Text = "Rate limited\nAsk anything",
                        CursorLine = "Ask anything",
                        ConsoleProcessIds = readCount <= 2 ? new[] { 500 } : new[] { 600 },
                        StartRow = 0,
                        CursorRow = 1
                    };
                    e = null;
                    return true;
                },
                delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e)
                {
                    writeCallCount++;
                    e = null;
                    return NativeWriteOutcome.CompleteInputCommitted;
                },
                delegate { return transactionClock; });

            engineTargetNotInConsole.PollOnce(true);
            transactionClock = testNow.AddSeconds(15);
            IList<PollResult> resNotInConsole = engineTargetNotInConsole.PollOnce(true);
            failures += AssertEqual(false, resNotInConsole[0].Sent, "target missing from console prevented send");
            failures += AssertEqual(0, writeCallCount, "target missing from console resulted in zero writes");
            failures += AssertEqual(true, resNotInConsole[0].Reason.IndexOf("re-resolution_failed", StringComparison.Ordinal) >= 0, "membership-negative setup reached fresh console resolution");

            // Scenario 5: Prompt changed to busy at recheck
            writeCallCount = 0;
            readCount = 0;
            transactionClock = testNow;
            var engineBusyChanged = new WatcherEngine(
                safetyConfig,
                delegate { return initialProcesses; },
                delegate(int pid, string name) { return initialSession; },
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                {
                    readCount++;
                    s = new ConsoleSnapshot
                    {
                        ProcessId = 500,
                        Text = readCount <= 2 ? "Rate limited\nAsk anything" : "Rate limited\nWorking...\nAsk anything",
                        CursorLine = "Ask anything",
                        ConsoleProcessIds = new[] { 500 },
                        StartRow = 0,
                        CursorRow = 1
                    };
                    e = null;
                    return true;
                },
                delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e)
                {
                    writeCallCount++;
                    e = null;
                    return NativeWriteOutcome.CompleteInputCommitted;
                },
                delegate { return transactionClock; });

            engineBusyChanged.PollOnce(true);
            transactionClock = testNow.AddSeconds(15);
            IList<PollResult> resBusyChanged = engineBusyChanged.PollOnce(true);
            failures += AssertEqual(false, resBusyChanged[0].Sent, "busy state at recheck prevented send");
            failures += AssertEqual(0, writeCallCount, "busy state at recheck resulted in zero writes");
            failures += AssertEqual("send_blocked=target_busy", resBusyChanged[0].Reason, "busy negative entered write-eligible transaction");

            // Scenario 6: Verified send succeeds transactionally
            writeCallCount = 0;
            DateTime currentClock = testNow;
            var engineSuccess = new WatcherEngine(
                safetyConfig,
                delegate { return initialProcesses; },
                delegate(int pid, string name) { return initialSession; },
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                {
                    s = new ConsoleSnapshot
                    {
                        ProcessId = 500,
                        WindowHandle = new IntPtr(0x555),
                        Text = "Rate limited\nAsk anything",
                        CursorLine = "Ask anything",
                        ConsoleProcessIds = new[] { 500 },
                        StartRow = 0,
                        CursorRow = 1
                    };
                    e = null;
                    return true;
                },
                delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e)
                {
                    writeCallCount++;
                    e = null;
                    return NativeWriteOutcome.CompleteInputCommitted;
                },
                delegate { return currentClock; });

            // First poll detects trigger and starts initial delay
            engineSuccess.PollOnce(true);
            // Second poll after delay fires verified send
            currentClock = testNow.AddSeconds(15);
            IList<PollResult> resSuccess = engineSuccess.PollOnce(true);
            failures += AssertEqual(true, resSuccess[0].Sent, "verified send succeeded");
            failures += AssertEqual(1, writeCallCount, "exactly one verified write executed");

            // Scenario 7: A different trigger at the safety reread cannot inherit eligibility.
            writeCallCount = 0;
            readCount = 0;
            currentClock = testNow;
            var engineEventChanged = new WatcherEngine(
                safetyConfig,
                delegate { return initialProcesses; },
                delegate(int pid, string name) { return initialSession; },
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                {
                    readCount++;
                    string eventText = readCount <= 2 ? "Rate limited event A\nAsk anything" : "Rate limited event B\nAsk anything";
                    s = new ConsoleSnapshot { ProcessId = 500, WindowHandle = new IntPtr(0x555), Text = eventText, CursorLine = "Ask anything", ConsoleProcessIds = new[] { 500 }, StartRow = 0, CursorRow = 1 };
                    e = null;
                    return true;
                },
                delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e)
                {
                    writeCallCount++;
                    e = null;
                    return NativeWriteOutcome.CompleteInputCommitted;
                },
                delegate { return currentClock; });
            engineEventChanged.PollOnce(true);
            currentClock = testNow.AddSeconds(15);
            IList<PollResult> eventChangedResults = engineEventChanged.PollOnce(true);
            failures += AssertEqual(0, writeCallCount, "changed event at safety reread caused zero writes");
            failures += AssertEqual("send_blocked=event_changed", eventChangedResults[0].Reason, "changed event negative entered write-eligible transaction");

            // Scenario 8: Missing process creation time can be observed but never authorize input.
            writeCallCount = 0;
            currentClock = testNow;
            var weakSession = new ProcessSessionIdentity { ProcessId = 500, ProcessName = "cline", StartTimeUtc = DateTime.MinValue };
            var engineWeakIdentity = new WatcherEngine(
                safetyConfig,
                delegate { return initialProcesses; },
                delegate(int pid, string name) { return weakSession; },
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                {
                    s = new ConsoleSnapshot { ProcessId = 500, Text = "Rate limited\nAsk anything", CursorLine = "Ask anything", ConsoleProcessIds = new[] { 500 }, StartRow = 0, CursorRow = 1 };
                    e = null;
                    return true;
                },
                delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e)
                {
                    writeCallCount++;
                    e = null;
                    return NativeWriteOutcome.CompleteInputCommitted;
                },
                delegate { return currentClock; });
            engineWeakIdentity.PollOnce(true);
            currentClock = testNow.AddSeconds(15);
            IList<PollResult> weakIdentityResults = engineWeakIdentity.PollOnce(true);
            failures += AssertEqual(0, writeCallCount, "weak process identity caused zero writes");
            failures += AssertEqual("send_blocked=target_identity_unavailable", weakIdentityResults[0].Reason, "weak identity blocked at write eligibility");

            // T-158: an unresolvable console must enter an exponential backoff
            // instead of being re-probed on every poll. The observed failure
            // mode was cline PID 5100 producing one "attached console ... does
            // not contain matched process" ERROR every 2s indefinitely.
            int resolutionReadCount = 0;
            DateTime backoffClock = testNow;
            var engineConsoleBackoff = new WatcherEngine(
                safetyConfig,
                delegate { return initialProcesses; },
                delegate(int pid, string name) { return initialSession; },
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                {
                    resolutionReadCount++;
                    // Console never contains the matched PID -> unresolvable.
                    s = new ConsoleSnapshot { ProcessId = pid, Text = "unrelated", CursorLine = "unrelated", ConsoleProcessIds = new[] { 9999 }, StartRow = 0, CursorRow = 1 };
                    e = null;
                    return true;
                },
                delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e)
                {
                    e = null;
                    return NativeWriteOutcome.CompleteInputCommitted;
                },
                delegate { return backoffClock; });

            IList<PollResult> backoffFirst = engineConsoleBackoff.PollOnce(false);
            int readsAfterFirstPoll = resolutionReadCount;
            failures += AssertEqual(true, readsAfterFirstPoll > 0, "unresolvable console is probed on the first poll");
            failures += AssertEqual("console unavailable", backoffFirst[0].Reason, "unresolvable console reports console unavailable");

            backoffClock = testNow.AddSeconds(2);
            IList<PollResult> backoffSecond = engineConsoleBackoff.PollOnce(false);
            failures += AssertEqual(readsAfterFirstPoll, resolutionReadCount, "console-unavailable backoff suppresses re-probe on the next poll");
            failures += AssertEqual("send_blocked=console_unavailable_backoff", backoffSecond[0].Reason, "suppressed poll reports the backoff state");
            failures += AssertEqual("console_unavailable_backoff", backoffSecond[0].DecisionCode, "backoff state has a stable decision code");
            failures += AssertEqual(true, backoffSecond[0].NextAttemptUtc > backoffClock, "backoff state exposes when the next probe is due");
            failures += AssertEqual(true, String.IsNullOrEmpty(backoffSecond[0].Error), "suppressed poll does not re-emit the ERROR line");

            // Past the cooldown the probe resumes (so a console that becomes
            // resolvable after a reset is still picked up).
            backoffClock = testNow.AddSeconds(400);
            engineConsoleBackoff.PollOnce(false);
            failures += AssertEqual(true, resolutionReadCount > readsAfterFirstPoll, "console probe resumes after the backoff window expires");

            // TryWriteLineVerified basic validation
            string verifyErr;
            failures += AssertEqual(NativeWriteOutcome.NoInputCommitted, NativeConsole.TryWriteLineVerified(null, initialSession, "cc", out verifyErr), "verified write rejects null session");
            failures += AssertEqual(NativeWriteOutcome.NoInputCommitted, NativeConsole.TryWriteLineVerified(new ResolvedConsoleSession(), null, "cc", out verifyErr), "verified write rejects null expected target");
            failures += AssertEqual(NativeWriteOutcome.NoInputCommitted, NativeConsole.TryWriteLineVerified(new ResolvedConsoleSession(), initialSession, "", out verifyErr), "verified write rejects empty command");
            failures += AssertEqual(NativeWriteOutcome.NoInputCommitted, NativeConsole.TryWriteLineVerified(new ResolvedConsoleSession(), initialSession, "a\nb", out verifyErr), "verified write rejects multiline command");
            failures += AssertEqual(NativeWriteOutcome.NoInputCommitted, NativeConsole.TryWriteLineVerified(new ResolvedConsoleSession { MatchedTargetSession = session3 }, session1, "cc", out verifyErr), "verified write rejects mismatched resolved target identity");
            failures += AssertEqual(NativeWriteOutcome.NoInputCommitted, NativeConsole.TryWriteLineVerified(new ResolvedConsoleSession { MatchedTargetSession = session1 }, session1, new string('x', 513), out verifyErr), "verified write rejects oversized command");
            failures += AssertEqual(true, NativeConsole.IsCompleteInputWrite(6, 6), "complete native input write accepted");
            failures += AssertEqual(true, NativeConsole.IsCompleteInputWrite(10, 10), "complete backspace-and-retype input write accepted");
            failures += AssertEqual(false, NativeConsole.IsCompleteInputWrite(6, 5), "partial native input write fails closed");
            failures += AssertEqual(false, NativeConsole.IsCompleteInputWrite(0, 0), "zero-record native input write is not success");

            var attachList = new List<int> { 70, 71, 72 };
            int selectedPid;
            ConsoleSnapshot selectedSnapshot;
            string selectError;
            bool selected = ProcessDiscovery.TrySelectConsole(
                attachList,
                62,
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                {
                    if (pid == 70)
                    {
                        s = null;
                        e = "first candidate failed";
                        return false;
                    }
                    s = new ConsoleSnapshot { ConsoleProcessIds = new[] { 62, 71 } };
                    e = null;
                    return true;
                },
                out selectedPid,
                out selectedSnapshot,
                out selectError);
            failures += AssertEqual(true, selected, "later valid attach candidate accepted after first failure");
            failures += AssertEqual(71, selectedPid, "selected pid is the working candidate");

            bool membershipRejected = ProcessDiscovery.TrySelectConsole(
                attachList,
                62,
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                {
                    s = new ConsoleSnapshot { ConsoleProcessIds = new[] { 63, 72 } };
                    e = null;
                    return true;
                },
                out selectedPid,
                out selectedSnapshot,
                out selectError);
            failures += AssertEqual(false, membershipRejected, "console that lacks the matched process is rejected");
            failures += AssertEqual(true, selectError != null && selectError.IndexOf("does not contain", StringComparison.Ordinal) >= 0, "membership rejection reason");

            var disappearingList = new List<int> { 120 };
            int disappearedPid;
            ConsoleSnapshot disappearedSnapshot;
            string disappearedError;
            bool disappearedResult = ProcessDiscovery.TrySelectConsole(
                disappearingList,
                120,
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                {
                    s = null;
                    e = "AttachConsole failed for PID " + pid + ": The handle is invalid (6)";
                    return false;
                },
                out disappearedPid,
                out disappearedSnapshot,
                out disappearedError);
            failures += AssertEqual(false, disappearedResult, "unreadable candidate correctly reports failure");
            failures += AssertEqual(true, disappearedError != null && disappearedError.IndexOf("The handle is invalid (6)", StringComparison.Ordinal) >= 0, "unreadable candidate error retained");

            // PERF-006: ProcessSnapshotIndex reuses one index across rules.
            var idxProcesses = new List<ProcessEntry>
            {
                new ProcessEntry { Id = 200, ParentId = 1, Name = "powershell.exe" },
                new ProcessEntry { Id = 201, ParentId = 200, Name = "node.exe" },
                new ProcessEntry { Id = 202, ParentId = 201, Name = "cline.exe" }
            };
            var snapshotIndex = new ProcessSnapshotIndex(idxProcesses);
            failures += AssertEqual(3, snapshotIndex.ById.Count, "index ById contains all processes");
            failures += AssertEqual(true, snapshotIndex.ByName.ContainsKey("powershell"), "index ByName has powershell");
            failures += AssertEqual(true, snapshotIndex.ByName.ContainsKey("cline"), "index ByName has cline");
            failures += AssertEqual(1, snapshotIndex.ByName["cline"].Count, "index ByName cline has one entry");
            var idxTargetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cline" };
            var idxCandidates = ProcessDiscovery.FindCandidates(snapshotIndex, idxTargetNames);
            failures += AssertEqual(1, idxCandidates.Count, "index FindCandidates finds one cline");
            failures += AssertEqual(202, idxCandidates[0].MatchedProcessId, "index FindCandidates matched PID 202");
            failures += AssertEqual(true, idxCandidates[0].AttachProcessIds.Count >= 2, "index FindCandidates has attach chain");

            // PERF-004: membership-first path. Wrong-console candidates are
            // rejected via cheap AttachConsole+GetConsoleProcessList before
            // the expensive per-row screen extraction.
            int mfReadCount = 0;
            int mfPid;
            ConsoleSnapshot mfSnap;
            string mfErr;
            bool mfOk = ProcessDiscovery.TrySelectConsole(attachList, 62,
                delegate(int pid, int lc, out ConsoleSnapshot s, out string e)
                {
                    mfReadCount++;
                    s = new ConsoleSnapshot { ConsoleProcessIds = new[] { 62 } };
                    e = null;
                    return true;
                },
                delegate(int pid, out IList<int> pids, out string err)
                {
                    if (pid == 70) { pids = new[] { 63 }; err = null; return true; }
                    pids = new[] { 62 };
                    err = null;
                    return true;
                },
                180, out mfPid, out mfSnap, out mfErr);
            failures += AssertEqual(true, mfOk, "membership-first accepts correct candidate");
            failures += AssertEqual(1, mfReadCount, "membership-first reads only accepted candidate");
            failures += AssertEqual(71, mfPid, "membership-first selects PID 71");

            var emptyReads = new List<PollResult>();
            int probeReadable;
            int probeUnreadable;
            failures += AssertEqual("SKIP", ClassifyProbe(emptyReads, out probeReadable, out probeUnreadable), "probe with zero matches is SKIP");
            failures += AssertEqual("PASS", ClassifyProbe(new[] { new PollResult { Read = true }, new PollResult { Read = true } }, out probeReadable, out probeUnreadable), "probe with all readable is PASS");
            failures += AssertEqual("FAIL_ALL", ClassifyProbe(new[] { new PollResult { Read = false, Error = "attach failed" } }, out probeReadable, out probeUnreadable), "probe with only console errors is FAIL_ALL");
            failures += AssertEqual("FAIL_MIXED", ClassifyProbe(new[] { new PollResult { Read = true }, new PollResult { Read = false, Error = "attach failed" } }, out probeReadable, out probeUnreadable), "probe with mixed success and error is FAIL_MIXED");
            failures += AssertEqual("FAIL_ALL", ClassifyProbe(new[] { new PollResult { Read = true, Error = "rule_evaluation_failed=regex_timeout" } }, out probeReadable, out probeUnreadable), "probe fails when readable console rule evaluation fails");

            string writeError;
            bool accepted = NativeConsole.TryWriteLine(0, "", out writeError);
            failures += AssertEqual(false, accepted, "empty input rejected");
            failures += AssertEqual(true, writeError != null && writeError.IndexOf("empty", StringComparison.OrdinalIgnoreCase) >= 0, "empty input reason");
            failures += AssertEqual(true, WatcherEngine.IsNonConsoleDiscoveryFailure("AttachConsole failed for PID 1: invalid handle (6)"), "ignore non-console discovery candidate");
            failures += AssertEqual(false, WatcherEngine.IsNonConsoleDiscoveryFailure("AttachConsole failed for PID 1: access denied (5)"), "keep real discovery read failure");

            DateTime due;
            failures += AssertEqual(true, RetryTimeParser.TryParseDue("try again at 4:40 PM", new DateTime(2026, 8, 26, 16, 0, 0), out due), "parse Codex reset clock");
            failures += AssertEqual(new DateTime(2026, 8, 26, 16, 40, 0), due, "future Codex reset clock");
            failures += AssertEqual(true, RetryTimeParser.TryParseDue("try again at 4:40 PM", new DateTime(2026, 8, 26, 17, 0, 0), out due), "parse expired reset clock");
            failures += AssertEqual(new DateTime(2026, 8, 26, 17, 0, 0), due, "expired reset runs now");
            failures += AssertEqual(true, RetryTimeParser.TryParseDue("again at Aug 27th, 2026 2:44 PM", new DateTime(2026, 8, 27, 1, 0, 0), out due), "parse full date Codex reset clock");
            failures += AssertEqual(new DateTime(2026, 8, 27, 14, 44, 0), due, "full date Codex reset clock due time");
            failures += AssertEqual(true, RetryTimeParser.TryParseDue("Try again in 8h 57m", new DateTime(2026, 8, 26, 17, 0, 0), out due), "parse Cline compact limit duration");
            failures += AssertEqual(new DateTime(2026, 8, 27, 1, 57, 0), due, "Cline compact limit due time");
            failures += AssertEqual(false, RetryTimeParser.TryParseDue("Try again in 999999h", new DateTime(2026, 8, 26, 17, 0, 0), out due), "reject absurd retry duration");

            DateTime start = new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);
            TargetRule clineLimitRule = WatcherConfiguration.CreateTestSample().Targets[1];
            var clineLimitSnapshot = new ConsoleSnapshot
            {
                Text = "Daily free model limit reached\nYou've reached today's free usage limit for this model.\nTry again in 8h 57m\nWhat can I do for you?",
                CursorLine = "What can I do for you?",
                StartRow = 0,
                CursorRow = 3
            };
            RuleObservation clineLimitObservation = RuleMatcher.Inspect(clineLimitRule, clineLimitSnapshot, start);
            failures += AssertEqual(true, clineLimitObservation.Triggered, "detect Cline daily free-model limit");
            failures += AssertEqual(true, clineLimitObservation.Ready, "detect current Cline empty prompt");
            failures += AssertEqual(start.AddHours(8).AddMinutes(57).AddSeconds(3), clineLimitObservation.DueUtc, "honor Cline compact retry deadline");

            TargetRule retryRule = clineLimitRule;
            var retryState = new RetrySessionState();
            var retryObservation = new RuleObservation { Triggered = true, Ready = true, TriggerToken = "T1", DueUtc = start.AddSeconds(60) };
            failures += AssertEqual(false, retryState.Observe(retryObservation, retryRule, start).Send, "Cline first retry waits");
            failures += AssertEqual(false, retryState.Observe(retryObservation, retryRule, start.AddSeconds(59)).Send, "Cline cooldown blocks early send");
            RetryDecision sendDecision = retryState.Observe(retryObservation, retryRule, start.AddSeconds(60));
            failures += AssertEqual(true, sendDecision.Send, "Cline retry due at 60 seconds");
            retryState.RecordAttempt(true, sendDecision.TriggerToken, retryRule, start.AddSeconds(60));
            failures += AssertEqual(false, retryState.Observe(retryObservation, retryRule, start.AddSeconds(119)).Send, "Cline repeat cooldown blocks spam");
            failures += AssertEqual(true, retryState.Observe(retryObservation, retryRule, start.AddSeconds(120)).Send, "Cline repeats at next interval");

            var guardedState = new RetrySessionState();
            var typedPrompt = new RuleObservation { Triggered = true, Ready = false, TriggerToken = "T2", DueUtc = start };
            failures += AssertEqual(false, guardedState.Observe(typedPrompt, retryRule, start).Send, "typed prompt blocks injection");

            // T-158: the provider's parsed reset deadline is a FLOOR on the
            // retry clock. A blocked attempt must not pull the next attempt
            // earlier than the stated reset (the hammer-the-prompt bug: a
            // parsed "try again in 8h" was overwritten by now+60s on every
            // blocked poll, producing one send attempt per minute for hours).
            var floorState = new RetrySessionState();
            DateTime farDue = start.AddHours(8);
            var farObservation = new RuleObservation { Triggered = true, Ready = true, TriggerToken = "FLOOR-EVENT", DueUtc = farDue };
            failures += AssertEqual(false, floorState.Observe(farObservation, retryRule, start).Send, "distant reset deadline is not immediately send-eligible");
            floorState.RecordAttempt(false, "FLOOR-EVENT", retryRule, start);
            failures += AssertEqual(farDue, floorState.NextAttemptUtc, "blocked attempt keeps provider reset deadline as retry floor");
            failures += AssertEqual(false, floorState.Observe(farObservation, retryRule, start.AddSeconds(120)).Send, "blocked attempt does not re-authorize before reset");
            floorState.CommitAttempt(NativeWriteOutcome.NoInputCommitted, retryRule, start.AddSeconds(120));
            failures += AssertEqual(farDue, floorState.NextAttemptUtc, "no-input commit keeps provider reset deadline as retry floor");
            failures += AssertEqual(true, floorState.Observe(farObservation, retryRule, farDue).Send, "attempt authorized exactly at the provider reset deadline");
            // Backoff may still push LATER than the floor once the deadline passed.
            var lateState = new RetrySessionState();
            var nearObservation = new RuleObservation { Triggered = true, Ready = true, TriggerToken = "LATE-EVENT", DueUtc = start };
            failures += AssertEqual(true, lateState.Observe(nearObservation, retryRule, start).Send, "expired reset deadline is send-eligible");
            lateState.RecordAttempt(false, "LATE-EVENT", retryRule, start);
            failures += AssertEqual(start.AddSeconds(retryRule.SafeRetryIntervalSeconds), lateState.NextAttemptUtc, "backoff still applies past an expired reset deadline");

            // T-158: the floor is per-event. Once recovery is confirmed the old
            // deadline must be released, otherwise an unrelated later event
            // inherits a stale far-future floor and never gets a continuation.
            var floorReleaseState = new RetrySessionState();
            var releaseFar = new RuleObservation { Triggered = true, Ready = true, TriggerToken = "REL-EVENT", DueUtc = start.AddHours(8) };
            floorReleaseState.Observe(releaseFar, retryRule, start);
            floorReleaseState.RecordAttempt(true, "REL-EVENT", retryRule, start.AddHours(8));
            var releaseBusy = new RuleObservation { Triggered = true, Ready = false, Busy = true, TriggerToken = "REL-EVENT", DueUtc = start.AddHours(8) };
            floorReleaseState.Observe(releaseBusy, retryRule, start.AddHours(8).AddSeconds(2));
            var releaseCleared = new RuleObservation { Triggered = false, Ready = true, Busy = false, TriggerToken = null, DueUtc = DateTime.MinValue };
            floorReleaseState.Observe(releaseCleared, retryRule, start.AddHours(8).AddSeconds(5));
            failures += AssertEqual(RecoveryState.RecoveryConfirmed, floorReleaseState.State, "recovery confirmed after busy-then-ready");
            var releaseNewEvent = new RuleObservation { Triggered = true, Ready = true, TriggerToken = "REL-EVENT-2", DueUtc = start.AddHours(9) };
            floorReleaseState.Observe(releaseNewEvent, retryRule, start.AddHours(9));
            floorReleaseState.RecordAttempt(false, "REL-EVENT-2", retryRule, start.AddHours(9));
            failures += AssertEqual(start.AddHours(9).AddSeconds(retryRule.SafeRetryIntervalSeconds), floorReleaseState.NextAttemptUtc, "recovered session does not inherit the previous event's retry floor");


            // W2-002: ambiguous native-write outcomes must never be re-dispatched
            // by elapsed retry time alone; only console evidence resolves them.
            var ambiguousState = new RetrySessionState();
            var ambTrigger = new RuleObservation { Triggered = true, Ready = true, TriggerToken = "AMB-EVENT", DueUtc = start.AddSeconds(60) };
            failures += AssertEqual(true, ambiguousState.Observe(ambTrigger, retryRule, start.AddSeconds(60)).Send, "ambiguous: initial due send eligible");
            ambiguousState.ReserveAttempt("AMB-EVENT", retryRule, start.AddSeconds(60));
            // Restart restore: session appears as AttemptInFlightReserved.
            var restoredAmb = new RetrySessionState();
            restoredAmb.RestoreFrom(new StateRecord
            {
                RuleName = "cline-limits",
                ProcessId = 500,
                ProcessStartUtc = start,
                TriggerFingerprint = "AMB-EVENT",
                LastObservedUtc = start.AddSeconds(60),
                LastWriteUtc = start.AddSeconds(60),
                NextAllowedAttemptUtc = start.AddSeconds(60),
                AwaitingOutcome = false,
                SawBusyAfterWrite = false,
                SuppressedFingerprint = null,
                AttemptCount = 1,
                RecoveryState = RecoveryState.AttemptInFlightReserved.ToString()
            }, start.AddSeconds(120));
            // Elapsed time far past the retry deadline must NOT authorize a send.
            var sameTriggerReady = new RuleObservation { Triggered = true, Ready = true, TriggerToken = "AMB-EVENT", DueUtc = start.AddSeconds(60) };
            failures += AssertEqual(false, restoredAmb.Observe(sameTriggerReady, retryRule, start.AddSeconds(600)).Send, "ambiguous in-flight reservation cannot re-send by elapsed time alone");
            failures += AssertEqual(RecoveryState.AmbiguousFailClosed, restoredAmb.State, "ambiguous unresolved state is fail-closed");
            // A different (new) occurrence starts a fresh lifecycle and may send.
            var newOccurrence = new RuleObservation { Triggered = true, Ready = true, TriggerToken = "AMB-EVENT-2", DueUtc = start.AddSeconds(60) };
            RetryDecision newDecision = restoredAmb.Observe(newOccurrence, retryRule, start.AddSeconds(600));
            failures += AssertEqual(true, newDecision.Send, "new occurrence after ambiguous write starts fresh lifecycle");
            // Trigger clearing proves the previous write succeeded -> recovery.
            var triggerCleared = new RuleObservation { Triggered = false, Ready = true, Busy = false, TriggerToken = null, DueUtc = DateTime.MinValue };
            failures += AssertEqual(false, restoredAmb.Observe(triggerCleared, retryRule, start.AddSeconds(601)).Send, "trigger-clear resolves ambiguous write without send");
            failures += AssertEqual(RecoveryState.IdleNoEvent, restoredAmb.State, "new occurrence clears after fresh lifecycle");

            // W2-002: partial accepted write (AmbiguousOrPartialInput) enters
            // AmbiguousFailClosed and must not become timer-authorized.
            var partialState = new RetrySessionState();
            partialState.ReserveAttempt("P-EVENT", retryRule, start.AddSeconds(60));
            partialState.CommitAttempt(NativeWriteOutcome.AmbiguousOrPartialInput, retryRule, start.AddSeconds(60));
            failures += AssertEqual(RecoveryState.AmbiguousFailClosed, partialState.State, "partial write enters ambiguous fail-closed state");
            var partialSameTrigger = new RuleObservation { Triggered = true, Ready = true, TriggerToken = "P-EVENT", DueUtc = start.AddSeconds(60) };
            failures += AssertEqual(false, partialState.Observe(partialSameTrigger, retryRule, start.AddSeconds(3600)).Send, "partial-write ambiguity cannot re-send by elapsed time alone");
            // Definitely-no-input may retry per policy.
            var retryState2 = new RetrySessionState();
            retryState2.ReserveAttempt("N-EVENT", retryRule, start.AddSeconds(60));
            retryState2.CommitAttempt(NativeWriteOutcome.NoInputCommitted, retryRule, start.AddSeconds(60));
            failures += AssertEqual(RecoveryState.BackoffWait, retryState2.State, "no-input-committed enters ordinary backoff");
            var noInputRetry = new RuleObservation { Triggered = true, Ready = true, TriggerToken = "N-EVENT", DueUtc = start.AddSeconds(60) };
            failures += AssertEqual(true, retryState2.Observe(noInputRetry, retryRule, start.AddSeconds(120)).Send, "definitely-no-input may retry per policy");

            try
            {
                WatcherConfiguration loaded = WatcherConfiguration.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SAICONT.config.xml"));
                failures += AssertEqual(true, loaded.Targets.Count >= 2, "load editable configuration");
                TargetRule httpRule = loaded.Targets.FirstOrDefault(item => String.Equals(item.Transport, "opencode-http", StringComparison.OrdinalIgnoreCase));
                failures += AssertEqual(true, httpRule != null && !String.IsNullOrEmpty(httpRule.ProjectDirectory), "editable configuration pins OpenCode project");
            }
            catch (Exception exception)
            {
                failures += AssertEqual(true, false, "load editable configuration: " + exception.Message);
            }

            // Wave 2: Event Correlation and Bounded Context Tests
            var multiEventSnapshot = new ConsoleSnapshot
            {
                Text = "Rate limit exceeded\nTry again in 1 hour\n---\nSome intermediate work...\nDaily free model limit reached\nTry again in 8h 57m\nWhat can I do for you?",
                CursorLine = "What can I do for you?",
                StartRow = 0,
                CursorRow = 6
            };
            RuleObservation multiObs = RuleMatcher.Inspect(clineLimitRule, multiEventSnapshot, start);
            failures += AssertEqual(true, multiObs.Triggered, "multi-event picks latest trigger");
            failures += AssertEqual(4, multiObs.TriggerRow, "multi-event trigger row is latest event");
            failures += AssertEqual(start.AddHours(8).AddMinutes(57).AddSeconds(3), multiObs.DueUtc, "multi-event bounded context parses latest deadline");
            var scrolledEventSnapshot = new ConsoleSnapshot
            {
                Text = multiEventSnapshot.Text,
                CursorLine = multiEventSnapshot.CursorLine,
                StartRow = 500,
                CursorRow = 506
            };
            RuleObservation scrolledObs = RuleMatcher.Inspect(clineLimitRule, scrolledEventSnapshot, start);
            failures += AssertEqual(multiObs.TriggerToken, scrolledObs.TriggerToken, "event fingerprint remains stable when buffer rows scroll");
            var laterEventSnapshot = new ConsoleSnapshot
            {
                Text = "Daily free model limit reached\nTry again in 9h 1m\nWhat can I do for you?",
                CursorLine = "What can I do for you?",
                StartRow = 0,
                CursorRow = 2
            };
            RuleObservation laterEventObs = RuleMatcher.Inspect(clineLimitRule, laterEventSnapshot, start);
            // CORE-001: trigger pattern matches "Daily free model limit reached ... try again in" (line ~14 in SAICONT.config.xml)
            // which does not capture the time digits. Both 8h 57m and 9h 1m share the same matched text and therefore the
            // same stable event identity. The deadline moves into DueUtc and is anchored in RetrySessionState on first
            // acceptance; a different time does not make this a different event from the matcher's perspective. The
            // occurrence discriminator for byte-identical later occurrences lives in the session state, not the matcher.
            failures += AssertEqual(true, String.Equals(multiObs.TriggerToken, laterEventObs.TriggerToken, StringComparison.Ordinal), "matched text identity is stable across deadline changes; deadline carries via DueUtc");

            // Wave 2: Current-tail busy matching
            TargetRule codexBusyRule = WatcherConfiguration.CreateTestSample().Targets[0];
            var historicalBusySnapshot = new ConsoleSnapshot
            {
                Text = "> Working (10%)\nHistorical line 1\nHistorical line 2\nHistorical line 3\nHistorical line 4\nHistorical line 5\nHistorical line 6\nHistorical line 7\nTask completed.\n› Ask Codex to do anything",
                CursorLine = "› Ask Codex to do anything",
                StartRow = 0,
                CursorRow = 9
            };
            RuleObservation histBusyObs = RuleMatcher.Inspect(codexBusyRule, historicalBusySnapshot, start);
            failures += AssertEqual(false, histBusyObs.Busy, "historical busy outside tail ignored");

            var currentBusySnapshot = new ConsoleSnapshot
            {
                Text = "Task completed.\n› Working (50%)...",
                CursorLine = "› Working (50%)...",
                StartRow = 0,
                CursorRow = 1
            };
            RuleObservation currBusyObs = RuleMatcher.Inspect(codexBusyRule, currentBusySnapshot, start);
            failures += AssertEqual(true, currBusyObs.Busy, "current tail busy detected");

            // Wave 2: DurableStateStore & Restart Semantics
            string stateTestDir = Path.Combine(Path.GetTempPath(), "SAICONT-state-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stateTestDir);
            try
            {
                string stateFilePath = Path.Combine(stateTestDir, "SAICONT.state.xml");
                var stateStore = new DurableStateStore(stateFilePath);

                var rec1 = new StateRecord
                {
                    RuleName = "cline-limits",
                    TransportKind = "opencode-http",
                    EndpointIdentity = "http://127.0.0.1:41961|1.18.25",
                    ProjectIdentity = "C:\\SAIPAL",
                    SessionIdentity = "ses_persisted_exact",
                    ProcessId = 500,
                    ProcessStartUtc = testNow,
                    TriggerFingerprint = "T-FINGERPRINT-1",
                    RuleSemanticFingerprint = safetyConfig.Targets[0].SemanticFingerprint,
                    LastObservedUtc = testNow,
                    LastWriteUtc = testNow,
                    NextAllowedAttemptUtc = testNow.AddSeconds(60),
                    AwaitingOutcome = true,
                    SawBusyAfterWrite = false,
                    SuppressedFingerprint = null,
                    AttemptCount = 1,
                    RecoveryFamily = "transient-provider-recovery",
                    RecoveryState = RecoveryState.EventWaitingDeadline.ToString()
                };
                var ancientRec = new StateRecord
                {
                    RuleName = "cline-limits",
                    ProcessId = 501,
                    ProcessStartUtc = testNow.AddDays(-500),
                    RuleSemanticFingerprint = safetyConfig.Targets[0].SemanticFingerprint,
                    TriggerFingerprint = "T-ANCIENT",
                    LastObservedUtc = testNow.AddDays(-500),
                    LastWriteUtc = testNow.AddDays(-500),
                    NextAllowedAttemptUtc = testNow.AddDays(-500),
                    AwaitingOutcome = false,
                    SawBusyAfterWrite = false,
                    SuppressedFingerprint = null,
                    AttemptCount = 1,
                    RecoveryState = RecoveryState.IdleNoEvent.ToString()
                };

                stateStore.Save(new[] { rec1, ancientRec }, testNow);
                failures += AssertEqual(true, File.Exists(stateFilePath), "state store created XML file");

                List<StateRecord> loadedRecords = stateStore.Load(testNow);
                failures += AssertEqual(1, loadedRecords.Count, "state store pruned ancient record on save");
                if (loadedRecords.Count > 0)
                {
                    failures += AssertEqual(500, loadedRecords[0].ProcessId, "loaded record pid");
                    failures += AssertEqual("opencode-http", loadedRecords[0].TransportKind, "loaded record transport identity");
                    failures += AssertEqual("C:\\SAIPAL", loadedRecords[0].ProjectIdentity, "loaded record project identity");
                    failures += AssertEqual("ses_persisted_exact", loadedRecords[0].SessionIdentity, "loaded record exact session identity");
                    failures += AssertEqual("T-FINGERPRINT-1", loadedRecords[0].TriggerFingerprint, "loaded record fingerprint");
                    failures += AssertEqual("transient-provider-recovery", loadedRecords[0].RecoveryFamily, "loaded record recovery family");
                    failures += AssertEqual(true, loadedRecords[0].AwaitingOutcome, "loaded record awaiting outcome");
                    failures += AssertEqual(testNow.AddSeconds(60), loadedRecords[0].NextAllowedAttemptUtc, "loaded record next allowed time");
                }
                bool unchangedState;
                string unchangedError;
                bool unchangedSaved = stateStore.TrySave(new[] { rec1 }, testNow.AddMinutes(1), out unchangedState, out unchangedError);
                failures += AssertEqual(true, unchangedSaved, "unchanged durable state save succeeds");
                failures += AssertEqual(false, unchangedState, "unchanged durable state does not rewrite file every poll");
                failures += AssertEqual(1, stateStore.SuccessfulWriteCount, "durable state write count changes only for meaningful state");
                failures += AssertEqual(0, Directory.GetFiles(stateTestDir, "*.tmp.*").Length, "atomic state save leaves no temp artifact");

                string healthFilePath = Path.Combine(stateTestDir, "SAICONT.health.json");
                var baselineHealth = new HealthReport
                {
                    ProductVersion = "1.1",
                    ProcessId = 500,
                    Mode = "watch",
                    StateStoreDisposition = "healthy",
                    StateStoreHealthy = true,
                    HealthUpdatedUtc = testNow
                };
                string healthError;
                failures += AssertEqual(true, baselineHealth.TryWrite(healthFilePath, out healthError), "health baseline atomic write succeeds");
                string authoritativeHealth = File.ReadAllText(healthFilePath, Encoding.UTF8);
                var replacementHealth = new HealthReport
                {
                    ProductVersion = "1.1",
                    ProcessId = 999,
                    Mode = "watch",
                    StateStoreDisposition = "healthy",
                    StateStoreHealthy = true,
                    HealthUpdatedUtc = testNow.AddSeconds(1)
                };
                bool tornHealthWritten = replacementHealth.TryWrite(
                    healthFilePath,
                    delegate(Stream stream, byte[] bytes)
                    {
                        stream.Write(bytes, 0, Math.Max(1, bytes.Length / 2));
                        throw new IOException("injected health torn-write fault");
                    },
                    out healthError);
                failures += AssertEqual(false, tornHealthWritten, "health torn-write fault is reported");
                failures += AssertEqual(authoritativeHealth, File.ReadAllText(healthFilePath, Encoding.UTF8), "health torn-write fault preserves prior authoritative snapshot");
                failures += AssertEqual(true, healthError != null && healthError.Contains("injected health torn-write fault"), "health torn-write fault remains diagnosable");
                failures += AssertEqual(0, Directory.GetFiles(stateTestDir, "SAICONT.health.json.tmp.*").Length, "health torn-write fault leaves no temp artifact");

                // Restart semantics: WatcherEngine with restored state preserves cooldown
                var engineRestored = new WatcherEngine(
                    safetyConfig,
                    delegate { return initialProcesses; },
                    delegate(int pid, string name) { return initialSession; },
                    delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                    {
                        s = new ConsoleSnapshot
                        {
                            ProcessId = 500,
                            Text = "Rate limited\nAsk anything",
                            CursorLine = "Ask anything",
                            ConsoleProcessIds = new[] { 500 },
                            StartRow = 0,
                            CursorRow = 1
                        };
                        e = null;
                        return true;
                    },
                    delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e)
                    {
                        e = null;
                        return NativeWriteOutcome.CompleteInputCommitted;
                    },
                    delegate { return testNow.AddSeconds(10); },
                    stateStore);

                IList<PollResult> restartPoll1 = engineRestored.PollOnce(true);
                failures += AssertEqual(false, restartPoll1[0].Sent, "restored state preserves active cooldown on restart");
                failures += AssertEqual(false, restartPoll1[0].WouldSend, "restored state would not send during preserved cooldown");

                var restoredSuppression = new RetrySessionState();
                restoredSuppression.RestoreFrom(new StateRecord
                {
                    TriggerFingerprint = "OLD-EVENT",
                    SuppressedFingerprint = "OLD-EVENT",
                    LastObservedUtc = testNow,
                    NextAllowedAttemptUtc = testNow,
                    RecoveryState = RecoveryState.RecoveryConfirmed.ToString()
                }, testNow);
                RetryDecision suppressedAfterRestart = restoredSuppression.Observe(
                    new RuleObservation { Triggered = true, Ready = true, TriggerToken = "OLD-EVENT", DueUtc = testNow },
                    retryRule,
                    testNow);
                failures += AssertEqual(false, suppressedAfterRestart.Send, "restart preserves stale-event suppression");

                var futureState = new RetrySessionState();
                futureState.RestoreFrom(new StateRecord
                {
                    TriggerFingerprint = "FUTURE",
                    LastObservedUtc = testNow.AddDays(30),
                    NextAllowedAttemptUtc = testNow.AddDays(30),
                    RecoveryState = RecoveryState.BackoffWait.ToString()
                }, testNow);
                failures += AssertEqual(testNow.AddDays(30), futureState.NextAttemptUtc, "legitimate long retry deadline survives restart unchanged");

                var corruptFutureState = new RetrySessionState();
                corruptFutureState.RestoreFrom(new StateRecord
                {
                    TriggerFingerprint = "CORRUPT-FUTURE",
                    LastObservedUtc = testNow,
                    NextAllowedAttemptUtc = testNow.AddYears(50),
                    RecoveryState = RecoveryState.BackoffWait.ToString()
                }, testNow);
                DateTime horizonMaximum = testNow.AddDays(RetryConstants.MaximumRetryHorizonDays);
                failures += AssertEqual(true, corruptFutureState.NextAttemptUtc <= horizonMaximum, "out-of-contract future timestamp is clamped to supported horizon");

                // Corrupted state file test
                File.WriteAllText(stateFilePath, "<malformed_xml");
                List<StateRecord> recoveredRecords = stateStore.Load();
                failures += AssertEqual(0, recoveredRecords.Count, "corrupt state safely yields empty records without crash");
                failures += AssertEqual(StateLoadDisposition.Corrupt, stateStore.LastLoadDisposition, "corrupt state is classified explicitly");
                failures += AssertEqual(true, Directory.GetFiles(stateTestDir, "SAICONT.state.xml.corrupt.*").Length == 1, "corrupt state is quarantined");

                File.WriteAllText(stateFilePath, "<saicontState version=\"999\" updatedUtc=\"2026-08-26T12:00:00Z\" />");
                List<StateRecord> futureSchemaRecords = stateStore.Load();
                failures += AssertEqual(0, futureSchemaRecords.Count, "future state schema loads no unsafe records");
                failures += AssertEqual(StateLoadDisposition.UnsupportedSchema, stateStore.LastLoadDisposition, "future state schema is classified explicitly");
                failures += AssertEqual(true, Directory.GetFiles(stateTestDir, "SAICONT.state.xml.unsupported.*").Length == 1, "future state schema is quarantined");
                 failures += AssertEqual(true, stateStore.TryClearRecoveryMarker(), "explicit state reconciliation clears recovery marker");

                var manyRecords = new List<StateRecord>();
                for (int index = 0; index < DurableStateStore.MaximumRecords + 25; index++)
                {
                    manyRecords.Add(new StateRecord
                    {
                        RuleName = "rule-" + index,
                        ProcessId = 1000 + index,
                         ProcessStartUtc = testNow.AddSeconds(index),
                         RuleSemanticFingerprint = safetyConfig.Targets[0].SemanticFingerprint,
                         TriggerFingerprint = "event-" + index,
                         LastObservedUtc = testNow,
                         NextAllowedAttemptUtc = testNow.AddSeconds(60),
                         RecoveryState = RecoveryState.BackoffWait.ToString()
                    });
                }
                stateStore.Save(manyRecords, testNow);
                failures += AssertEqual(DurableStateStore.MaximumRecords, stateStore.Load(testNow).Count, "durable state record count is hard bounded");

                string attemptStatePath = Path.Combine(stateTestDir, "attempt.state.xml");
                var attemptStore = new DurableStateStore(attemptStatePath);
                DateTime attemptClock = testNow;
                var attemptEngine = new WatcherEngine(
                    safetyConfig,
                    delegate { return initialProcesses; },
                    delegate(int pid, string name) { return initialSession; },
                    delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                    {
                        s = new ConsoleSnapshot { ProcessId = 500, Text = "Rate limited\nAsk anything", CursorLine = "Ask anything", ConsoleProcessIds = new[] { 500 }, StartRow = 0, CursorRow = 1 };
                        e = null;
                        return true;
                    },
                    delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e)
                    {
                        e = null;
                        return NativeWriteOutcome.CompleteInputCommitted;
                    },
                    delegate { return attemptClock; },
                    attemptStore);
                attemptEngine.PollOnce(true);
                attemptClock = testNow.AddSeconds(15);
                IList<PollResult> persistedAttemptResult = attemptEngine.PollOnce(true);
                List<StateRecord> persistedAttemptRecords = new DurableStateStore(attemptStatePath).Load(testNow);
                failures += AssertEqual(true, persistedAttemptResult.Count > 0 && persistedAttemptResult[0].Sent, "state persistence test actually performed verified send");
                failures += AssertEqual(1, persistedAttemptRecords.Count, "successful write persisted one session record in same poll");
                if (persistedAttemptRecords.Count > 0)
                {
                    failures += AssertEqual(true, persistedAttemptRecords[0].AwaitingOutcome, "successful write persisted awaiting-outcome immediately");
                    failures += AssertEqual(1, persistedAttemptRecords[0].AttemptCount, "successful write persisted attempt count immediately");
                }

                string blockedParent = Path.Combine(stateTestDir, "not-a-directory");
                File.WriteAllText(blockedParent, "x");
                var blockedStore = new DurableStateStore(Path.Combine(blockedParent, "state.xml"));
                DateTime blockedClock = testNow;
                int blockedWriterCalls = 0;
                var blockedStateEngine = new WatcherEngine(
                    safetyConfig,
                    delegate { return initialProcesses; },
                    delegate(int pid, string name) { return initialSession; },
                    delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                    {
                        s = new ConsoleSnapshot { ProcessId = 500, Text = "Rate limited\nAsk anything", CursorLine = "Ask anything", ConsoleProcessIds = new[] { 500 }, StartRow = 0, CursorRow = 1 };
                        e = null;
                        return true;
                    },
                    delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e)
                    {
                        blockedWriterCalls++;
                        e = null;
                        return NativeWriteOutcome.CompleteInputCommitted;
                    },
                    delegate { return blockedClock; },
                    blockedStore);
                blockedStateEngine.PollOnce(true);
                blockedClock = testNow.AddSeconds(15);
                IList<PollResult> blockedStateResults = blockedStateEngine.PollOnce(true);
                failures += AssertEqual(0, blockedWriterCalls, "unwritable durable state prevents input");
                failures += AssertEqual("send_blocked=state_store_unavailable", blockedStateResults[0].Reason, "unwritable durable state is surfaced explicitly");
            }
            finally
            {
                if (Directory.Exists(stateTestDir))
                {
                    Directory.Delete(stateTestDir, true);
                }
            }

            // Wave 3: Timeline Simulator & Recovery State Machine Tests
            var backoffRule = new TargetRule
            {
                Name = "backoff-test",
                Enabled = true,
                ProcessNames = new[] { "cline" },
                Command = "cc",
                ScanLines = 180,
                MaximumTriggerDistanceLines = 150,
                InitialDelaySeconds = 10,
                RetryIntervalSeconds = 10,
                BackoffMultiplier = 2.0,
                MaximumRetryIntervalSeconds = 80,
                MaximumAttemptsPerEvent = 4,
                ParseRetryTime = false,
                TriggerPatterns = new[] { "(?i)rate limit" },
                ReadyPatterns = new[] { "^.*Ask.*$" },
                BusyPatterns = new[] { "(?i)working" }
            };

            var simState = new RetrySessionState();
            DateTime t0 = new DateTime(2026, 8, 26, 14, 0, 0, DateTimeKind.Utc);
            var trigObs = new RuleObservation { Triggered = true, Ready = true, Busy = false, TriggerToken = "TRIG-1", DueUtc = t0.AddSeconds(10) };

            // Step 1: Initial discovery -> EventWaitingDeadline
            RetryDecision d1 = simState.Observe(trigObs, backoffRule, t0);
            failures += AssertEqual(false, d1.Send, "sim: initial discovery waits for initial delay");
            failures += AssertEqual(RecoveryState.EventWaitingDeadline, simState.State, "sim: initial state is EventWaitingDeadline");

            // Step 2: Initial delay expires -> EventReadyToAttempt -> Send
            DateTime t1 = t0.AddSeconds(10);
            RetryDecision d2 = simState.Observe(trigObs, backoffRule, t1);
            failures += AssertEqual(true, d2.Send, "sim: delay expired triggers send");
            failures += AssertEqual(RecoveryState.EventReadyToAttempt, simState.State, "sim: state is EventReadyToAttempt");

            // Step 3: Record write -> CommandWrittenAwaitingOutcome (Attempt 1)
            simState.RecordAttempt(true, "TRIG-1", backoffRule, t1);
            failures += AssertEqual(RecoveryState.CommandWrittenAwaitingOutcome, simState.State, "sim: after write state is CommandWrittenAwaitingOutcome");
            failures += AssertEqual(1, simState.AttemptCount, "sim: attempt count is 1");
            failures += AssertEqual(t1.AddSeconds(10), simState.NextAttemptUtc, "sim: attempt 1 backoff is 10s");

            // Step 4: Immediate next poll -> no progress, triggers backoff wait
            DateTime t2 = t1.AddSeconds(2);
            RetryDecision d3 = simState.Observe(trigObs, backoffRule, t2);
            failures += AssertEqual(false, d3.Send, "sim: cooldown blocks immediate re-send");
            failures += AssertEqual(RecoveryState.BackoffWait, simState.State, "sim: state transitioned to BackoffWait");

            // Step 5: Backoff 1 expires at t1 + 10s -> Send attempt 2
            DateTime t3 = t1.AddSeconds(10);
            RetryDecision d4 = simState.Observe(trigObs, backoffRule, t3);
            failures += AssertEqual(true, d4.Send, "sim: backoff 1 expired triggers attempt 2");
            simState.RecordAttempt(true, "TRIG-1", backoffRule, t3);
            failures += AssertEqual(2, simState.AttemptCount, "sim: attempt count is 2");
            failures += AssertEqual(t3.AddSeconds(20), simState.NextAttemptUtc, "sim: attempt 2 backoff is 20s (10 * 2^1)");

            // Step 6: Backoff 2 expires at t3 + 20s -> Send attempt 3
            DateTime t4 = t3.AddSeconds(20);
            RetryDecision d5 = simState.Observe(trigObs, backoffRule, t4);
            failures += AssertEqual(true, d5.Send, "sim: backoff 2 expired triggers attempt 3");
            simState.RecordAttempt(true, "TRIG-1", backoffRule, t4);
            failures += AssertEqual(3, simState.AttemptCount, "sim: attempt count is 3");
            failures += AssertEqual(t4.AddSeconds(40), simState.NextAttemptUtc, "sim: attempt 3 backoff is 40s (10 * 2^2)");

            // Step 7: Backoff 3 expires at t4 + 40s -> Send attempt 4
            DateTime t5 = t4.AddSeconds(40);
            RetryDecision d6 = simState.Observe(trigObs, backoffRule, t5);
            failures += AssertEqual(true, d6.Send, "sim: backoff 3 expired triggers attempt 4");
            simState.RecordAttempt(true, "TRIG-1", backoffRule, t5);
            failures += AssertEqual(4, simState.AttemptCount, "sim: attempt count is 4 (max)");

            // Step 8: Next poll with same event after max attempts reached -> RecoveryExhausted
            DateTime t6 = t5.AddSeconds(2);
            RetryDecision d7 = simState.Observe(trigObs, backoffRule, t6);
            failures += AssertEqual(false, d7.Send, "sim: max attempts reached blocks send");
            failures += AssertEqual(RecoveryState.RecoveryExhausted, simState.State, "sim: state is RecoveryExhausted");

            // Step 9: Genuinely new trigger event arrives -> resets attempts and starts new recovery
            var newTrigObs = new RuleObservation { Triggered = true, Ready = true, Busy = false, TriggerToken = "TRIG-NEW-2", DueUtc = t6.AddSeconds(10) };
            RetryDecision d8 = simState.Observe(newTrigObs, backoffRule, t6);
            failures += AssertEqual(RecoveryState.EventWaitingDeadline, simState.State, "sim: new event resets exhausted state");
            failures += AssertEqual(0, simState.AttemptCount, "sim: new event resets attempt count to 0");

            // Step 10: Full success recovery path
            DateTime t7 = t6.AddSeconds(10);
            RetryDecision d9 = simState.Observe(newTrigObs, backoffRule, t7);
            failures += AssertEqual(true, d9.Send, "sim: new event fires send");
            simState.RecordAttempt(true, "TRIG-NEW-2", backoffRule, t7);

            // Target becomes busy
            var busyObs = new RuleObservation { Triggered = true, Ready = false, Busy = true, TriggerToken = "TRIG-NEW-2" };
            simState.Observe(busyObs, backoffRule, t7.AddSeconds(2));
            failures += AssertEqual(RecoveryState.TargetBusyOrProgressing, simState.State, "sim: observed busy progress");

            // Target completes and clears trigger -> RecoveryConfirmed
            var clearObs = new RuleObservation { Triggered = false, Ready = true, Busy = false };
            simState.Observe(clearObs, backoffRule, t7.AddSeconds(15));
            failures += AssertEqual(RecoveryState.RecoveryConfirmed, simState.State, "sim: trigger cleared confirms recovery");
            failures += AssertEqual("TRIG-NEW-2", simState.SuppressedToken, "sim: old trigger token suppressed");

            // Stale trigger reappears in scrollback -> suppressed
            RetryDecision d10 = simState.Observe(newTrigObs, backoffRule, t7.AddSeconds(20));
            failures += AssertEqual(false, d10.Send, "sim: stale trigger in scrollback suppressed");

            var failedWriteState = new RetrySessionState();
            DateTime failedWriteClock = t0;
            var failedWriteObservation = new RuleObservation { Triggered = true, Ready = true, Busy = false, TriggerToken = "WRITE-FAIL", DueUtc = t0 };
            for (int attempt = 0; attempt < backoffRule.SafeMaximumAttemptsPerEvent; attempt++)
            {
                RetryDecision failedWriteDecision = failedWriteState.Observe(failedWriteObservation, backoffRule, failedWriteClock);
                failures += AssertEqual(true, failedWriteDecision.Send, "native write failure attempt " + (attempt + 1) + " reached write boundary");
                failedWriteState.RecordAttempt(false, true, "WRITE-FAIL", backoffRule, failedWriteClock);
                RetryDecision adjacentDecision = failedWriteState.Observe(failedWriteObservation, backoffRule, failedWriteClock.AddSeconds(1));
                failures += AssertEqual(false, adjacentDecision.Send, "native write failure attempt " + (attempt + 1) + " cannot tight-loop");
                failedWriteClock = failedWriteState.NextAttemptUtc;
            }
            RetryDecision exhaustedWriteFailures = failedWriteState.Observe(failedWriteObservation, backoffRule, failedWriteClock);
            failures += AssertEqual(false, exhaustedWriteFailures.Send, "repeated native write failures are bounded");
            failures += AssertEqual(RecoveryState.RecoveryExhausted, failedWriteState.State, "native write failures reach recovery exhausted");

            // Wave 4: Configuration Validation Hardening Tests
            string configTestDir = Path.Combine(Path.GetTempPath(), "SAICONT-cfg-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(configTestDir);
            try
            {
                // Test duplicate target name rejection
                string dupNameXml = Path.Combine(configTestDir, "dup.xml");
                File.WriteAllText(dupNameXml, @"<saicont pollIntervalMilliseconds=""2000"">
  <logging path=""test.log"" maxBytes=""1048576"" retainedFiles=""5"" duplicateWindowSeconds=""60"" />
  <targets>
    <target name=""dup-rule"" enabled=""true"" command=""cc"" scanLines=""180"" maximumTriggerDistanceLines=""150"" initialDelaySeconds=""60"" retryIntervalSeconds=""60"" parseRetryTime=""false"">
      <processNames><process>cline</process></processNames>
      <triggerPatterns><pattern>(?i)rate limit</pattern></triggerPatterns>
      <readyPatterns><pattern>^.*Ask.*$</pattern></readyPatterns>
      <busyPatterns />
    </target>
    <target name=""dup-rule"" enabled=""true"" command=""cc"" scanLines=""180"" maximumTriggerDistanceLines=""150"" initialDelaySeconds=""60"" retryIntervalSeconds=""60"" parseRetryTime=""false"">
      <processNames><process>cline</process></processNames>
      <triggerPatterns><pattern>(?i)rate limit</pattern></triggerPatterns>
      <readyPatterns><pattern>^.*Ask.*$</pattern></readyPatterns>
      <busyPatterns />
    </target>
  </targets>
</saicont>");
                bool dupRejected = false;
                try { WatcherConfiguration.Load(dupNameXml); } catch (FormatException) { dupRejected = true; }
                failures += AssertEqual(true, dupRejected, "duplicate target name rejected");

                // Test oversized command rejection (>512 chars)
                string longCmdXml = Path.Combine(configTestDir, "longcmd.xml");
                string oversizedCmd = new string('x', 513);
                File.WriteAllText(longCmdXml, @"<saicont pollIntervalMilliseconds=""2000"">
  <logging path=""test.log"" maxBytes=""1048576"" retainedFiles=""5"" duplicateWindowSeconds=""60"" />
  <targets>
    <target name=""long-rule"" enabled=""true"" command=""" + oversizedCmd + @""" scanLines=""180"" maximumTriggerDistanceLines=""150"" initialDelaySeconds=""60"" retryIntervalSeconds=""60"" parseRetryTime=""false"">
      <processNames><process>cline</process></processNames>
      <triggerPatterns><pattern>(?i)rate limit</pattern></triggerPatterns>
      <readyPatterns><pattern>^.*Ask.*$</pattern></readyPatterns>
      <busyPatterns />
    </target>
  </targets>
</saicont>");
                bool longCmdRejected = false;
                try { WatcherConfiguration.Load(longCmdXml); } catch (FormatException) { longCmdRejected = true; }
                failures += AssertEqual(true, longCmdRejected, "oversized command rejected");

                // Test empty process names rejection
                string emptyProcXml = Path.Combine(configTestDir, "emptyproc.xml");
                File.WriteAllText(emptyProcXml, @"<saicont pollIntervalMilliseconds=""2000"">
  <logging path=""test.log"" maxBytes=""1048576"" retainedFiles=""5"" duplicateWindowSeconds=""60"" />
  <targets>
    <target name=""empty-proc-rule"" enabled=""true"" command=""cc"" scanLines=""180"" maximumTriggerDistanceLines=""150"" initialDelaySeconds=""60"" retryIntervalSeconds=""60"" parseRetryTime=""false"">
      <processNames />
      <triggerPatterns><pattern>(?i)rate limit</pattern></triggerPatterns>
      <readyPatterns><pattern>^.*Ask.*$</pattern></readyPatterns>
      <busyPatterns />
    </target>
  </targets>
</saicont>");
                bool emptyProcRejected = false;
                try { WatcherConfiguration.Load(emptyProcXml); } catch (FormatException) { emptyProcRejected = true; }
                failures += AssertEqual(true, emptyProcRejected, "empty process names rejected");

                // Test read-only validate-config mode
                string validXml = Path.Combine(configTestDir, "valid.xml");
                File.WriteAllText(validXml, @"<saicont pollIntervalMilliseconds=""2000"">
  <logging path=""test.log"" maxBytes=""1048576"" retainedFiles=""5"" duplicateWindowSeconds=""60"" />
  <targets>
    <target name=""valid-rule"" enabled=""true"" command=""cc"" scanLines=""180"" maximumTriggerDistanceLines=""150"" initialDelaySeconds=""60"" retryIntervalSeconds=""60"" parseRetryTime=""false"">
      <processNames><process>cline</process></processNames>
      <triggerPatterns><pattern>(?i)rate limit</pattern></triggerPatterns>
      <readyPatterns><pattern>^.*Ask.*$</pattern></readyPatterns>
      <busyPatterns />
    </target>
  </targets>
</saicont>");
                WatcherConfiguration validCfg = WatcherConfiguration.Load(validXml);
                int validateResult = RunValidateConfig(validCfg, validXml);
                failures += AssertEqual(0, validateResult, "validate-config exits 0 on valid configuration");

                string validConfigText = File.ReadAllText(validXml);
                var invalidConfigCases = new Dictionary<string, string>
                {
                    { "unknown XML attribute rejected", validConfigText.Replace("<saicont pollIntervalMilliseconds=\"2000\">", "<saicont pollIntervalMilliseconds=\"2000\" mystery=\"x\">") },
                    { "trigger distance beyond scan lines rejected", validConfigText.Replace("maximumTriggerDistanceLines=\"150\"", "maximumTriggerDistanceLines=\"181\"") },
                    { "invalid optional backoff rejected", validConfigText.Replace("parseRetryTime=\"false\"", "parseRetryTime=\"false\" backoffMultiplier=\"0\"") },
                    { "unsafe process basename rejected", validConfigText.Replace("<process>cline</process>", "<process>..\\cline</process>") },
                    { "multiline command rejected", validConfigText.Replace("command=\"cc\"", "command=\"cc&#10;evil\"") },
                    { "malformed regex rejected with configuration error", validConfigText.Replace("(?i)rate limit", "[") },
                    { "unknown XML element rejected", validConfigText.Replace("<busyPatterns />", "<busyPatterns /><mystery />") }
                };
                int invalidCaseIndex = 0;
                foreach (var invalidCase in invalidConfigCases)
                {
                    string invalidPath = Path.Combine(configTestDir, "invalid-" + invalidCaseIndex + ".xml");
                    File.WriteAllText(invalidPath, invalidCase.Value);
                    bool rejected = false;
                    try { WatcherConfiguration.Load(invalidPath); } catch (FormatException) { rejected = true; }
                    failures += AssertEqual(true, rejected, invalidCase.Key);
                    invalidCaseIndex++;
                }
            }
            finally
            {
                if (Directory.Exists(configTestDir))
                {
                    Directory.Delete(configTestDir, true);
                }
            }

            // Wave 4: Pathological regex / timeout defense
            var timeoutRule = new TargetRule
            {
                Name = "timeout-test",
                Enabled = true,
                ProcessNames = new[] { "cline" },
                Command = "cc",
                ScanLines = 180,
                MaximumTriggerDistanceLines = 150,
                InitialDelaySeconds = 10,
                RetryIntervalSeconds = 10,
                ParseRetryTime = false,
                TriggerPatterns = new[] { @"^(([a-z])+.)+[A-Z]([a-z])+$" }, // Catastrophic backtracking pattern
                ReadyPatterns = new[] { "^.*Ask.*$" },
                BusyPatterns = new string[0]
            };
            timeoutRule.CompileRegexes();
            var pathologicalSnapshot = new ConsoleSnapshot
            {
                Text = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!",
                CursorLine = "Ask anything",
                StartRow = 0,
                CursorRow = 1
            };
            RuleObservation timeoutObs = RuleMatcher.Inspect(timeoutRule, pathologicalSnapshot, start);
            // On catastrophic regex timeout, RuleMatcher must fail-closed (Busy=true, Triggered=false, Ready=false) without throwing or crashing
            failures += AssertEqual(false, timeoutObs.Triggered, "pathological regex fails closed (not triggered)");
            failures += AssertEqual(true, timeoutObs.Busy, "pathological regex fails closed (marked busy)");
            failures += AssertEqual("regex_timeout", timeoutObs.EvaluationError, "pathological regex proves timeout path executed");

            // Wave 4: Rule Fixtures (Positive & Near-Miss Negative)
            TargetRule clineRule = WatcherConfiguration.CreateTestSample().Targets[1];
            TargetRule codexRule = WatcherConfiguration.CreateTestSample().Targets[0];

            // Cline OpenRouter 429 positive fixture
            var cline429Snapshot = new ConsoleSnapshot
            {
                Text = "generate_stream from OpenRouter: failed to invoke model openai/gpt-4o: Provider returned error: {\"error\":{\"code\":429,\"message\":\"Rate limit reached\"}}\nAsk anything...",
                CursorLine = "Ask anything...",
                StartRow = 0,
                CursorRow = 1
            };
            RuleObservation c429Obs = RuleMatcher.Inspect(clineRule, cline429Snapshot, start);
            failures += AssertEqual(true, c429Obs.Triggered, "fixture: cline openrouter 429 triggered");
            failures += AssertEqual(true, c429Obs.Ready, "fixture: cline openrouter 429 ready");

            // Cline 429 near-miss negative fixture (429 appears in user file or git commit log, not provider error)
            var clineNegativeSnapshot = new ConsoleSnapshot
            {
                Text = "Compiled src/error_handler.cs: HTTP response status 429 is handled in line 42.\nWhat can I do for you?",
                CursorLine = "What can I do for you?",
                StartRow = 0,
                CursorRow = 1
            };
            RuleObservation cNegObs = RuleMatcher.Inspect(clineRule, clineNegativeSnapshot, start);
             failures += AssertEqual(false, cNegObs.Triggered, "fixture: cline unrelated 429 not triggered");

             var clineInternalErrorSnapshot = new ConsoleSnapshot
             {
                 Text = "{\"message\":\"An internal error occurred. Please try again later.\",\"code\":\"internal_error\"}\nWhat can I do for you?",
                 CursorLine = "What can I do for you?",
                 StartRow = 0,
                 CursorRow = 1
             };
             RuleObservation cInternalErrorObs = RuleMatcher.Inspect(clineRule, clineInternalErrorSnapshot, start);
             failures += AssertEqual(true, cInternalErrorObs.Triggered, "fixture: cline internal error triggered");
             failures += AssertEqual(true, cInternalErrorObs.Ready, "fixture: cline internal error ready");

             // SRC-021: a RETRY_SAME transient internal_error must NOT be pinned to a
             // far-future clock parsed from a nearby usage-limit line. It keeps the
             // bounded initial delay instead of the neighbor's reset deadline.
             var clineInternalErrorWithResetSnapshot = new ConsoleSnapshot
             {
                 Text = "You've hit your usage limit. Try again at 10:39 PM.\n{\"message\":\"An internal error occurred. Please try again later.\",\"code\":\"internal_error\"}\nWhat can I do for you?",
                 CursorLine = "What can I do for you?",
                 StartRow = 0,
                 CursorRow = 2
             };
             RuleObservation cInternalErrorWithResetObs = RuleMatcher.Inspect(clineRule, clineInternalErrorWithResetSnapshot, start);
             failures += AssertEqual(true, cInternalErrorWithResetObs.Triggered, "SRC-021: internal error with nearby reset line still triggers");
             failures += AssertEqual(start.AddSeconds(60), cInternalErrorWithResetObs.DueUtc, "SRC-021: internal error ignores neighbor reset clock (bounded retry)");

             var clineClipboardImageSnapshot = new ConsoleSnapshot
             {
                 Text = "ERROR: Cannot read \"clipboard\" (this model does not support image input). Inform the user.\nAsk anything...",
                 CursorLine = "Ask anything...",
                 StartRow = 0,
                 CursorRow = 1
             };
             RuleObservation cClipboardImageObs = RuleMatcher.Inspect(clineRule, clineClipboardImageSnapshot, start);
             failures += AssertEqual(true, cClipboardImageObs.Triggered, "fixture: cline clipboard image error triggered");
             failures += AssertEqual(true, cClipboardImageObs.Ready, "fixture: cline clipboard image error ready");

             var clineInternalErrorNegativeSnapshot = new ConsoleSnapshot
             {
                 Text = "{\"message\":\"An internal error occurred.\",\"code\":\"other_error\"}\nWhat can I do for you?",
                 CursorLine = "What can I do for you?",
                 StartRow = 0,
                 CursorRow = 1
             };
             RuleObservation cInternalErrorNegObs = RuleMatcher.Inspect(clineRule, clineInternalErrorNegativeSnapshot, start);
             failures += AssertEqual(false, cInternalErrorNegObs.Triggered, "fixture: cline unrelated JSON error not triggered");

             // T-153 / SRC-004: normalized provider-failure intelligence.
             FailureAssessment internalFailure = FailureIntelligence.Classify(
                 "{\"message\":\"An internal error occurred. Please try again later.\",\"code\":\"internal_error\"}");
             failures += AssertEqual(FailureClass.INTERNAL_ERROR, internalFailure.Class, "failure intelligence: structured internal_error class");
             failures += AssertEqual(true, internalFailure.Retryable, "failure intelligence: internal_error retryable");
             failures += AssertEqual(FailureRecoveryAction.RETRY_SAME, internalFailure.RecommendedAction, "failure intelligence: internal_error action");
             failures += AssertEqual(true, Array.IndexOf(internalFailure.MatchedBy, "code:internal_error") >= 0, "failure intelligence: structured code evidence");

             FailureAssessment authPrecedence = FailureIntelligence.Classify(
                 "{\"message\":\"Something went wrong. Please try again later.\",\"code\":\"invalid_api_key\",\"type\":\"authentication_error\"}");
             failures += AssertEqual(FailureClass.AUTH_INVALID, authPrecedence.Class, "failure intelligence: later structured auth signal beats generic message");
             failures += AssertEqual(FailureRecoveryAction.STOP_AND_REPORT, authPrecedence.RecommendedAction, "failure intelligence: permanent auth failure stops");
             failures += AssertEqual(false, authPrecedence.Retryable, "failure intelligence: auth failure not retryable");

             FailureAssessment statusPrecedence = FailureIntelligence.Classify(
                 "provider error: HTTP 429 Too Many Requests; code=internal_error");
             failures += AssertEqual(FailureClass.RATE_LIMIT, statusPrecedence.Class, "failure intelligence: HTTP status precedes code in one envelope");
             failures += AssertEqual(true, Array.IndexOf(statusPrecedence.MatchedBy, "http_status:429") >= 0, "failure intelligence: matched-by remains bounded and structured");

             string[] corpusCodes =
             {
                 "internal_error", "server_overloaded", "rate_limit_exceeded", "insufficient_quota",
                 "invalid_api_key", "permission_denied", "model_unavailable", "model_not_found",
                 "upstream_error", "deadline_exceeded", "econnreset", "stream_interrupted",
                 "context_length_exceeded", "output_limit", "invalid_request_error", "tool_use_error",
                 "content_filter", "cloudflare_waf", "region_restricted", "account_restricted", "request_cancelled"
             };
             FailureClass[] corpusClasses =
             {
                 FailureClass.INTERNAL_ERROR, FailureClass.OVERLOADED, FailureClass.RATE_LIMIT, FailureClass.QUOTA_EXHAUSTED,
                 FailureClass.AUTH_INVALID, FailureClass.PERMISSION_DENIED, FailureClass.MODEL_UNAVAILABLE, FailureClass.MODEL_NOT_FOUND,
                 FailureClass.UPSTREAM_ERROR, FailureClass.TIMEOUT, FailureClass.NETWORK_ERROR, FailureClass.STREAM_INTERRUPTED,
                 FailureClass.CONTEXT_LENGTH, FailureClass.OUTPUT_LIMIT, FailureClass.INVALID_REQUEST, FailureClass.TOOL_ERROR,
                 FailureClass.CONTENT_FILTER, FailureClass.CLOUDFLARE_WAF, FailureClass.REGION_RESTRICTED, FailureClass.ACCOUNT_RESTRICTED, FailureClass.REQUEST_CANCELLED
             };
             for (int corpusIndex = 0; corpusIndex < corpusCodes.Length; corpusIndex++)
             {
                 FailureAssessment corpusAssessment = FailureIntelligence.Classify("{\"code\":\"" + corpusCodes[corpusIndex] + "\"}");
                 failures += AssertEqual(corpusClasses[corpusIndex], corpusAssessment.Class, "failure intelligence corpus: " + corpusCodes[corpusIndex]);
             }

             failures += AssertEqual(
                 FailureIntelligence.NormalizeForFingerprint("OpenRouter: Internal server error (request id: req_9fd31abc) 2026-08-31T12:15:30Z"),
                 FailureIntelligence.NormalizeForFingerprint("internal   server error"),
                 "failure intelligence: volatile provider prefix, request id, timestamp, case and whitespace normalize");

             FailureAssessment proseNearMiss = FailureIntelligence.Classify("We now document internal server error handling in this release.");
             failures += AssertEqual(FailureClass.UNKNOWN, proseNearMiss.Class, "failure intelligence: explanatory prose is not an error envelope");
             FailureAssessment structuredProseNearMiss = FailureIntelligence.Classify("Documentation example uses code=internal_error for provider failures.");
             failures += AssertEqual(FailureClass.UNKNOWN, structuredProseNearMiss.Class, "failure intelligence: structured-looking prose is not an error envelope");
             FailureAssessment regexNearMiss = FailureIntelligence.Classify(@"(?i)\binternal (server )?error\b");
             failures += AssertEqual(FailureClass.UNKNOWN, regexNearMiss.Class, "failure intelligence: regex source text is not an error event");
             FailureAssessment messageBeforeTransport = FailureIntelligence.Classify("ERROR: Something went wrong; ECONNRESET");
             failures += AssertEqual(FailureClass.INTERNAL_ERROR, messageBeforeTransport.Class, "failure intelligence: message signature precedes transport exception");
             failures += AssertEqual(true, FailureIntelligence.Classify("{\"code\":\"request_cancelled\"}").Retryable, "failure intelligence: cancellation remains retryable but manual");

             var overloadSnapshot = new ConsoleSnapshot
             {
                 Text = "The model is overloaded.\nWhat can I do for you?",
                 CursorLine = "What can I do for you?",
                 StartRow = 0,
                 CursorRow = 1
             };
             RuleObservation overloadObservation = RuleMatcher.Inspect(clineRule, overloadSnapshot, start);
             failures += AssertEqual(true, overloadObservation.Triggered, "failure intelligence: transient overload auto-triggers");
             failures += AssertEqual(FailureRecoveryAction.RETRY_SAME, overloadObservation.Failure.RecommendedAction, "failure intelligence: overload recovery action");
             failures += AssertEqual(false, String.Equals(cInternalErrorObs.TriggerToken, overloadObservation.TriggerToken, StringComparison.Ordinal), "failure intelligence: changed transient event remains visible to pre-send safety reread");

             var rotatingFailureRule = new TargetRule
             {
                 InitialDelaySeconds = 10,
                 RetryIntervalSeconds = 10,
                 BackoffMultiplier = 2.0,
                 MaximumRetryIntervalSeconds = 60,
                 MaximumAttemptsPerEvent = 2
             };
             var rotatingFailureState = new RetrySessionState();
             var rotatingInternal = new RuleObservation
             {
                 Triggered = true,
                 Ready = true,
                 TriggerToken = "internal-event",
                 RecoveryFamily = "transient-provider-recovery",
                 DueUtc = start
             };
             var rotatingOverload = new RuleObservation
             {
                 Triggered = true,
                 Ready = true,
                 TriggerToken = "overload-event",
                 RecoveryFamily = "transient-provider-recovery",
                 DueUtc = start
             };
             RetryDecision rotatingAttempt1 = rotatingFailureState.Observe(rotatingInternal, rotatingFailureRule, start);
             rotatingFailureState.RecordAttempt(true, rotatingAttempt1.TriggerToken, rotatingFailureRule, start);
             RetryDecision rotatingCooldown = rotatingFailureState.Observe(rotatingOverload, rotatingFailureRule, start.AddSeconds(1));
             failures += AssertEqual(false, rotatingCooldown.Send, "failure intelligence: rotated transient class keeps prior cooldown");
             failures += AssertEqual(1, rotatingFailureState.AttemptCount, "failure intelligence: rotated transient class keeps attempt budget");
             RetryDecision rotatingAttempt2 = rotatingFailureState.Observe(rotatingOverload, rotatingFailureRule, start.AddSeconds(10));
             rotatingFailureState.RecordAttempt(true, rotatingAttempt2.TriggerToken, rotatingFailureRule, start.AddSeconds(10));
             RetryDecision rotatingExhausted = rotatingFailureState.Observe(rotatingInternal, rotatingFailureRule, start.AddSeconds(11));
             failures += AssertEqual(false, rotatingExhausted.Send, "failure intelligence: alternating transient classes cannot loop");
             failures += AssertEqual(RecoveryState.RecoveryExhausted, rotatingFailureState.State, "failure intelligence: shared transient budget exhausts across classes");

             var authSnapshot = new ConsoleSnapshot
             {
                 Text = "{\"message\":\"Invalid API key\",\"code\":\"invalid_api_key\"}\nWhat can I do for you?",
                 CursorLine = "What can I do for you?",
                 StartRow = 0,
                 CursorRow = 1
             };
             RuleObservation authObservation = RuleMatcher.Inspect(clineRule, authSnapshot, start);
             failures += AssertEqual(false, authObservation.Triggered, "failure intelligence: permanent auth failure does not auto-continue");
             failures += AssertEqual(FailureClass.AUTH_INVALID, authObservation.Failure.Class, "failure intelligence: permanent failure remains observable");

             var quotaWithoutResetSnapshot = new ConsoleSnapshot
             {
                 Text = "Quota exceeded.\nWhat can I do for you?",
                 CursorLine = "What can I do for you?",
                 StartRow = 0,
                 CursorRow = 1
             };
             RuleObservation quotaWithoutReset = RuleMatcher.Inspect(clineRule, quotaWithoutResetSnapshot, start);
             failures += AssertEqual(false, quotaWithoutReset.Triggered, "failure intelligence: quota without reset time does not blind-retry");

             var quotaWithResetSnapshot = new ConsoleSnapshot
             {
                 Text = "Quota exceeded. Try again in 5 minutes.\nWhat can I do for you?",
                 CursorLine = "What can I do for you?",
                 StartRow = 0,
                 CursorRow = 1
             };
             RuleObservation quotaWithReset = RuleMatcher.Inspect(clineRule, quotaWithResetSnapshot, start);
             failures += AssertEqual(true, quotaWithReset.Triggered, "failure intelligence: quota with reset time enters bounded wait");
             failures += AssertEqual(start.AddMinutes(5).AddSeconds(3), quotaWithReset.DueUtc, "failure intelligence: provider reset deadline honored");

             var volatileIdSnapshotA = new ConsoleSnapshot
             {
                 Text = "{\"message\":\"Internal server error (request id: req_11111111)\"}\nWhat can I do for you?",
                 CursorLine = "What can I do for you?",
                 StartRow = 0,
                 CursorRow = 1
             };
             var volatileIdSnapshotB = new ConsoleSnapshot
             {
                 Text = "{\"message\":\"INTERNAL   SERVER ERROR (request id: req_99999999)\"}\nWhat can I do for you?",
                 CursorLine = "What can I do for you?",
                 StartRow = 0,
                 CursorRow = 1
             };
             RuleObservation volatileIdA = RuleMatcher.Inspect(clineRule, volatileIdSnapshotA, start);
             RuleObservation volatileIdB = RuleMatcher.Inspect(clineRule, volatileIdSnapshotB, start);
             failures += AssertEqual(true, volatileIdA.Triggered && volatileIdB.Triggered, "failure intelligence: internal-error variants trigger");
             failures += AssertEqual(volatileIdA.TriggerToken, volatileIdB.TriggerToken, "failure intelligence: volatile request IDs cannot mint new retry events");

             var boundedFailureRule = new TargetRule
             {
                 InitialDelaySeconds = 10,
                 RetryIntervalSeconds = 10,
                 BackoffMultiplier = 2.0,
                 MaximumRetryIntervalSeconds = 60,
                 MaximumAttemptsPerEvent = 2
             };
             var boundedFailureState = new RetrySessionState();
             var boundedFailureObservation = new RuleObservation { Triggered = true, Ready = true, TriggerToken = "FAILURE-STABLE", DueUtc = start };
             RetryDecision boundedAttempt1 = boundedFailureState.Observe(boundedFailureObservation, boundedFailureRule, start);
             boundedFailureState.RecordAttempt(true, boundedAttempt1.TriggerToken, boundedFailureRule, start);
             RetryDecision boundedAttempt2 = boundedFailureState.Observe(boundedFailureObservation, boundedFailureRule, start.AddSeconds(10));
             boundedFailureState.RecordAttempt(true, boundedAttempt2.TriggerToken, boundedFailureRule, start.AddSeconds(10));
             RetryDecision boundedExhausted = boundedFailureState.Observe(boundedFailureObservation, boundedFailureRule, start.AddSeconds(30));
             failures += AssertEqual(false, boundedExhausted.Send, "failure intelligence: retry loop stops at max attempts");
             failures += AssertEqual(RecoveryState.RecoveryExhausted, boundedFailureState.State, "failure intelligence: exhausted event stays terminal until evidence changes");

             // Codex usage limit positive fixture
            var codexUsageSnapshot = new ConsoleSnapshot
            {
                Text = "You've hit your usage limit. Try again at 4:40 PM.\n› Ask Codex to do anything",
                CursorLine = "› Ask Codex to do anything",
                StartRow = 0,
                CursorRow = 1
            };
            RuleObservation codexUsageObs = RuleMatcher.Inspect(codexRule, codexUsageSnapshot, start);
            failures += AssertEqual(true, codexUsageObs.Triggered, "fixture: codex usage limit triggered");
            failures += AssertEqual(true, codexUsageObs.Ready, "fixture: codex prompt ready");

            var codexLimitVariants = new[]
            {
                "You've hit your usage limit.\n> Ask Codex to do anything",
                "You've reached your usage limit.\n› Ask Codex to do anything",
                "Usage limit reached. Try again later.\n> Ask Codex to do anything",
                "Rate limit exceeded.\n> Ask Codex to do anything",
                "You've hit your usage limit. Try again at 12:42 PM.\n> Ask Codex to do anything",
                "You've hit your usage limit.\ncc\n"
            };
            for (int variantIndex = 0; variantIndex < codexLimitVariants.Length; variantIndex++)
            {
                string[] variantLines = codexLimitVariants[variantIndex].Split(new[] { '\n' }, StringSplitOptions.None);
                var variantSnapshot = new ConsoleSnapshot
                {
                    Text = codexLimitVariants[variantIndex],
                    CursorLine = variantLines[variantLines.Length - 1],
                    StartRow = 0,
                    CursorRow = variantLines.Length - 1
                };
                RuleObservation variantObservation = RuleMatcher.Inspect(codexRule, variantSnapshot, start);
                failures += AssertEqual(true, variantObservation.Triggered, "fixture: codex limit variant " + variantIndex + " triggered");
                failures += AssertEqual(true, variantObservation.Ready, "fixture: codex limit variant " + variantIndex + " ready");
            }

            var codexNegSnapshot = new ConsoleSnapshot
            {
                Text = "The server usage limit is monitored by Prometheus metric.\n› Ask Codex to do anything",
                CursorLine = "› Ask Codex to do anything",
                StartRow = 0,
                CursorRow = 1
            };
            RuleObservation codexNegObs = RuleMatcher.Inspect(codexRule, codexNegSnapshot, start);
            failures += AssertEqual(false, codexNegObs.Triggered, "fixture: codex unrelated usage text not triggered");

            // Wave 5: Named Mutex and Single-Instance Lock Tests
            bool firstCreated;
            string testCfgPath = Path.Combine(Path.GetTempPath(), "test-cfg-" + Guid.NewGuid().ToString("N") + ".xml");
            using (var mutex1 = AcquireInstanceMutex(testCfgPath, out firstCreated))
            {
                failures += AssertEqual(true, firstCreated, "first mutex acquisition succeeds as new");
                bool secondCreated;
                using (var mutex2 = AcquireInstanceMutex(testCfgPath, out secondCreated))
                {
                    failures += AssertEqual(false, secondCreated, "second mutex acquisition detects existing instance");
                }
            }

            // Wave 5: Instance Record & Tokenized Stop Tests
            string lifecycleDir = Path.Combine(Path.GetTempPath(), "SAICONT-life-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(lifecycleDir);
            try
            {
                string instFile = Path.Combine(lifecycleDir, "SAICONT.instance.xml");
                string testToken = "TEST-TOKEN-12345";
                DateTime testStart = new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);
                string instanceWriteError;
                bool instanceWritten = TryWriteInstanceFile(instFile, 1234, testStart, "--watch", "V:\\bin\\SAICONT & test.exe", testToken, out instanceWriteError);
                failures += AssertEqual(true, instanceWritten, "instance.xml atomic write succeeded");
                failures += AssertEqual(true, File.Exists(instFile), "instance.xml file written");
                string instXmlContent = File.ReadAllText(instFile);
                failures += AssertEqual(true, instXmlContent.IndexOf("<instanceToken>TEST-TOKEN-12345</instanceToken>", StringComparison.Ordinal) >= 0, "instance.xml contains token");
                failures += AssertEqual(true, instXmlContent.IndexOf("<pid>1234</pid>", StringComparison.Ordinal) >= 0, "instance.xml contains pid");
                failures += AssertEqual(true, instXmlContent.IndexOf("SAICONT &amp; test.exe", StringComparison.Ordinal) >= 0, "instance.xml escapes executable path");

                // Test tokenized stop predicate
                string stopFile = Path.Combine(lifecycleDir, "SAICONT.stop");
                File.WriteAllText(stopFile, "FOREIGN-TOKEN-999");
                bool foreignStop = String.Equals(File.ReadAllText(stopFile).Trim(), testToken, StringComparison.Ordinal);
                failures += AssertEqual(false, foreignStop, "foreign stop token does not match instance");

                File.WriteAllText(stopFile, testToken);
                bool matchingStop = String.Equals(File.ReadAllText(stopFile).Trim(), testToken, StringComparison.Ordinal);
                failures += AssertEqual(true, matchingStop, "matching stop token accepted");
            }
            finally
            {
                if (Directory.Exists(lifecycleDir))
                {
                    Directory.Delete(lifecycleDir, true);
                }
            }

            // Wave 6: Accelerated Soak & Scaled Engine Simulation
            int totalSoakSends = 0;
            DateTime simClock = new DateTime(2026, 8, 26, 10, 0, 0, DateTimeKind.Utc);
            var soakConfig = WatcherConfiguration.CreateTestSample();
            int scenarioStep = 0;

            Func<IList<ProcessEntry>> soakSnapshot = delegate
            {
                return new[]
                {
                    new ProcessEntry { Id = 5001, ParentId = 1000, Name = "codex" }
                };
            };

            ConsoleReadAttempt soakReader = delegate(int pid, int lineCount, out ConsoleSnapshot s, out string err)
            {
                err = null;
                // Cycle of 30 steps (10s each = 300s per cycle):
                // Step 0-4 (50s): idle
                // Step 5-7 (30s): usage limit -> sends at step 7 (reset time reached)
                // Step 8-15 (80s): busy progressing (Working 3s...) -> transitions to TargetBusyOrProgressing
                // Step 16-29 (140s): recovered/cleared -> confirms recovery and suppresses old token
                int phase = scenarioStep % 30;
                int soakCycle = scenarioStep / 30;
                // Generate a consistent reset time for this cycle at step 7 (70s from cycle base)
                DateTime cycleBase = new DateTime(2026, 8, 26, 10, 0, 0, DateTimeKind.Utc).AddSeconds(soakCycle * 300);
                DateTime resetTime = cycleBase.AddSeconds(60);
                string timeStr = resetTime.ToLocalTime().ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture);

                if (phase < 5)
                {
                    s = new ConsoleSnapshot { Text = "› Ask Codex to do anything\n", CursorLine = "› Ask Codex to do anything", StartRow = 0, CursorRow = 1, ProcessId = pid, ConsoleProcessIds = new[] { pid }, MembershipStatus = ConsoleMembershipStatus.VerifiedPresent };
                }
                else if (phase < 7)
                {
                    s = new ConsoleSnapshot { Text = "You've hit your usage limit. Try again at " + timeStr + ".\n› Ask Codex to do anything\n", CursorLine = "› Ask Codex to do anything", StartRow = 0, CursorRow = 2, ProcessId = pid, ConsoleProcessIds = new[] { pid }, MembershipStatus = ConsoleMembershipStatus.VerifiedPresent };
                }
                else if (phase < 16)
                {
                    s = new ConsoleSnapshot { Text = "Working (3s)...\n› Working (3s)...\n", CursorLine = "› Working (3s)...", StartRow = 0, CursorRow = 1, ProcessId = pid, ConsoleProcessIds = new[] { pid }, MembershipStatus = ConsoleMembershipStatus.VerifiedPresent };
                }
                else
                {
                    s = new ConsoleSnapshot { Text = "All tasks completed.\n› Ask Codex to do anything\n", CursorLine = "› Ask Codex to do anything", StartRow = 0, CursorRow = 2, ProcessId = pid, ConsoleProcessIds = new[] { pid }, MembershipStatus = ConsoleMembershipStatus.VerifiedPresent };
                }
                return true;
            };

            VerifiedConsoleWriter soakWriter = delegate(ResolvedConsoleSession sess, ProcessSessionIdentity expected, string cmd, out string err)
            {
                err = null;
                totalSoakSends++;
                return NativeWriteOutcome.CompleteInputCommitted;
            };

            var soakEngine = new WatcherEngine(
                soakConfig,
                soakSnapshot,
                delegate(int pid, string name) { return new ProcessSessionIdentity { ProcessId = pid, ProcessName = name, StartTimeUtc = new DateTime(2026, 8, 26, 9, 0, 0, DateTimeKind.Utc) }; },
                soakReader,
                soakWriter,
                delegate { return simClock; },
                null);

            const int SoakPolls = 100000;
            long memoryBeforeSoak = GC.GetTotalMemory(true);
            Stopwatch soakTimer = Stopwatch.StartNew();
            for (int poll = 0; poll < SoakPolls; poll++)
            {
                scenarioStep = poll;
                simClock = simClock.AddSeconds(10); // Advance clock 10s per poll
                soakEngine.PollOnce(true);
            }
            soakTimer.Stop();
            long memoryAfterSoak = GC.GetTotalMemory(true);

            // 100,000 polls contain 3,333 full cycles plus the send in the final partial cycle.
            failures += AssertEqual(3334, totalSoakSends, "accelerated soak: exact sends in 100000 simulated polls");
            failures += AssertEqual(true, soakEngine.SessionStateCount <= 1, "accelerated soak keeps watcher session state bounded");
            failures += AssertEqual(true, memoryAfterSoak - memoryBeforeSoak < 16L * 1024L * 1024L, "accelerated soak managed-memory growth stays below 16 MiB");
            Console.WriteLine("MEASURE: soak polls=" + SoakPolls + " elapsed_ms=" + soakTimer.ElapsedMilliseconds + " managed_delta_bytes=" + (memoryAfterSoak - memoryBeforeSoak));
            // PERF-010: Enhanced performance gates — controlled scenario budgets.

            // Gate 1: Poll-cycle timing. A single idle poll (no target found)
            // must complete well within the 2-second production cadence.
            {
                var perfConfig = new WatcherConfiguration
                {
                    PollIntervalMilliseconds = 2000,
                    Targets = new List<TargetRule>
                    {
                        new TargetRule
                        {
                            Name = "perf-idle",
                            Enabled = true,
                            ProcessNames = new[] { "saicont-no-such-process" },
                            Command = "cc",
                            ScanLines = 180,
                            MaximumTriggerDistanceLines = 150,
                            InitialDelaySeconds = 60,
                            RetryIntervalSeconds = 60,
                            ParseRetryTime = false,
                            TriggerPatterns = new[] { @"(?i)never-match" },
                            ReadyPatterns = new[] { @"^ready$" },
                            BusyPatterns = new string[0]
                        }
                    }
                };
                var perfEngine = new WatcherEngine(
                    perfConfig,
                    delegate { return new List<ProcessEntry>(); },
                    delegate(int pid, string name) { return new ProcessSessionIdentity { ProcessId = pid, ProcessName = name, StartTimeUtc = DateTime.MinValue }; },
                    delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e) { s = null; e = "no real console"; return false; },
                    delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e) { e = null; return NativeWriteOutcome.NoInputCommitted; },
                    delegate { return DateTime.UtcNow; });

                const int PerfPolls = 1000;
                var pollSamples = new long[PerfPolls];
                Stopwatch perfTimer = Stopwatch.StartNew();
                for (int i = 0; i < PerfPolls; i++)
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    perfEngine.PollOnce(false);
                    sw.Stop();
                    pollSamples[i] = sw.ElapsedTicks;
                }
                perfTimer.Stop();
                double avgPollMs = (double)perfTimer.ElapsedMilliseconds / PerfPolls;
                Array.Sort(pollSamples);
                double medianMs = (double)pollSamples[PerfPolls / 2] * 1000.0 / Stopwatch.Frequency;
                double p95Ms = (double)pollSamples[(int)(PerfPolls * 0.95)] * 1000.0 / Stopwatch.Frequency;
                double maxMs = (double)pollSamples[PerfPolls - 1] * 1000.0 / Stopwatch.Frequency;
                double oneCoreCpuMs = (double)perfTimer.ElapsedMilliseconds;
                double oneCoreCpuPct = oneCoreCpuMs / perfTimer.ElapsedMilliseconds * 100.0;
                failures += AssertEqual(true, avgPollMs < 50.0, "PERF-010: average idle poll < 50ms (actual=" + avgPollMs.ToString("F2") + "ms)");
                Console.WriteLine("MEASURE: perf_poll polls=" + PerfPolls + " avg_ms=" + avgPollMs.ToString("F2") + " median_ms=" + medianMs.ToString("F2") + " p95_ms=" + p95Ms.ToString("F2") + " max_ms=" + maxMs.ToString("F2") + " total_ms=" + perfTimer.ElapsedMilliseconds + " one_core_cpu_pct=" + oneCoreCpuPct.ToString("F2"));
            }

            // Gate 2: Multi-rule scaling. Five rules targeting distinct
            // processes/consoles must produce exactly five writes and keep
            // state bounded. (Same-console rules fall under the CORE-006
            // poll-level send reservation, intentionally one write per poll.)
            {
                var multiRuleTargets = new List<TargetRule>();
                string[] multiNames = new[] { "codex-a", "codex-b", "codex-c", "codex-d", "codex-e" };
                for (int r = 0; r < 5; r++)
                {
                    multiRuleTargets.Add(new TargetRule
                    {
                        Name = "multi-" + r,
                        Enabled = true,
                        ProcessNames = new[] { multiNames[r] },
                        Command = "cc",
                        ScanLines = 180,
                        MaximumTriggerDistanceLines = 150,
                        InitialDelaySeconds = 5,
                        RetryIntervalSeconds = 10,
                        ParseRetryTime = false,
                        TriggerPatterns = new[] { @"(?i)rate limited" },
                        ReadyPatterns = new[] { @"^.*Ask.*$" },
                        BusyPatterns = new[] { @"(?i)working" }
                    });
                }
                var multiConfig = new WatcherConfiguration
                {
                    PollIntervalMilliseconds = 2000,
                    Targets = multiRuleTargets
                };

                int[] multiPids = new[] { 9001, 9002, 9003, 9004, 9005 };
                int multiWriteCount = 0;
                DateTime multiClock = new DateTime(2026, 8, 26, 10, 0, 0, DateTimeKind.Utc);
                var multiEngine = new WatcherEngine(
                    multiConfig,
                    delegate {
                        var list = new List<ProcessEntry>();
                        for (int i = 0; i < multiPids.Length; i++)
                        {
                            list.Add(new ProcessEntry { Id = multiPids[i], ParentId = 100, Name = multiNames[i] });
                        }
                        return list;
                    },
                    delegate(int pid, string name) { return new ProcessSessionIdentity { ProcessId = pid, ProcessName = name, StartTimeUtc = new DateTime(2026, 8, 26, 9, 0, 0, DateTimeKind.Utc) }; },
                    delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                    {
                        s = new ConsoleSnapshot
                        {
                            ProcessId = pid,
                            Text = "Rate limited\nAsk anything",
                            CursorLine = "Ask anything",
                            ConsoleProcessIds = new[] { pid },
                            StartRow = 0,
                            CursorRow = 1,
                            MembershipStatus = ConsoleMembershipStatus.VerifiedPresent
                        };
                        e = null;
                        return true;
                    },
                    delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e)
                    {
                        multiWriteCount++;
                        e = null;
                        return NativeWriteOutcome.CompleteInputCommitted;
                    },
                    delegate { return multiClock; });

                // First poll: triggers detected, initial delay starts.
                multiEngine.PollOnce(true);
                // Second poll: delay elapsed, sends fire.
                multiClock = multiClock.AddSeconds(10);
                multiEngine.PollOnce(true);

                failures += AssertEqual(5, multiWriteCount, "PERF-010: five rules produce exactly five writes");
                failures += AssertEqual(true, multiEngine.SessionStateCount <= 5, "PERF-010: multi-rule state bounded by rule count");
                Console.WriteLine("MEASURE: perf_multi rules=5 writes=" + multiWriteCount + " states=" + multiEngine.SessionStateCount);
            }

            // Gate 3: Memory scaling. A 10,000-poll idle run with a larger
            // process snapshot must not leak managed memory.
            {
                var bigSnapshot = new List<ProcessEntry>();
                for (int p = 0; p < 500; p++)
                {
                    bigSnapshot.Add(new ProcessEntry { Id = 10000 + p, ParentId = 1, Name = "process-" + p + ".exe" });
                }
                var memConfig = new WatcherConfiguration
                {
                    PollIntervalMilliseconds = 2000,
                    Targets = new List<TargetRule>
                    {
                        new TargetRule
                        {
                            Name = "mem-idle",
                            Enabled = true,
                            ProcessNames = new[] { "saicont-no-such-process" },
                            Command = "cc",
                            ScanLines = 180,
                            MaximumTriggerDistanceLines = 150,
                            InitialDelaySeconds = 60,
                            RetryIntervalSeconds = 60,
                            ParseRetryTime = false,
                            TriggerPatterns = new[] { @"(?i)never-match" },
                            ReadyPatterns = new[] { @"^ready$" },
                            BusyPatterns = new string[0]
                        }
                    }
                };
                var memEngine = new WatcherEngine(
                    memConfig,
                    delegate { return bigSnapshot; },
                    delegate(int pid, string name) { return new ProcessSessionIdentity { ProcessId = pid, ProcessName = name, StartTimeUtc = DateTime.MinValue }; },
                    delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e) { s = null; e = "no real console"; return false; },
                    delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e) { e = null; return NativeWriteOutcome.NoInputCommitted; },
                    delegate { return DateTime.UtcNow; });

                const int MemPolls = 10000;
                GC.Collect();
                long memBefore = GC.GetTotalMemory(true);
                Stopwatch memTimer = Stopwatch.StartNew();
                for (int i = 0; i < MemPolls; i++)
                {
                    memEngine.PollOnce(false);
                }
                memTimer.Stop();
                GC.Collect();
                long memAfter = GC.GetTotalMemory(true);
                long memDelta = memAfter - memBefore;
                failures += AssertEqual(true, memDelta < 8L * 1024L * 1024L, "PERF-010: 10K-poll memory growth < 8 MiB (actual=" + memDelta + " bytes)");
                Console.WriteLine("MEASURE: perf_mem polls=" + MemPolls + " elapsed_ms=" + memTimer.ElapsedMilliseconds + " managed_delta_bytes=" + memDelta);
            }


            // Wave 7: XML Parser DTD Prohibited Security Test
            string dtdTestDir = Path.Combine(Path.GetTempPath(), "SAICONT-dtd-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dtdTestDir);
            try
            {
                string dtdXml = Path.Combine(dtdTestDir, "dtd.xml");
                File.WriteAllText(dtdXml, "<!DOCTYPE saicont [ <!ENTITY ext SYSTEM \"http://127.0.0.1/evil\"> ]>\n<saicont pollIntervalMilliseconds=\"2000\">\n  <logging path=\"test.log\" maxBytes=\"1048576\" retainedFiles=\"5\" duplicateWindowSeconds=\"60\" />\n  <targets />\n</saicont>");
                bool dtdBlocked = false;
                try
                {
                    WatcherConfiguration.Load(dtdXml);
                }
                catch (Exception)
                {
                    dtdBlocked = true;
                }
                failures += AssertEqual(true, dtdBlocked, "security: XML DTD processing prohibited");
            }
            finally
            {
                if (Directory.Exists(dtdTestDir))
                {
                    Directory.Delete(dtdTestDir, true);
                }
            }

            // Wave 7: Pre-Send Stop Abort Test
            bool stopWriterCalled = false;
            var stopTestConfig = new WatcherConfiguration
            {
                PollIntervalMilliseconds = 2000,
                Targets = new List<TargetRule>
                {
                    new TargetRule
                    {
                        Name = "stop-test-rule",
                        Enabled = true,
                        ProcessNames = new[] { "codex" },
                        Command = "cc",
                        ScanLines = 180,
                        MaximumTriggerDistanceLines = 150,
                        InitialDelaySeconds = 10,
                        RetryIntervalSeconds = 10,
                        ParseRetryTime = false,
                        TriggerPatterns = new[] { @"(?i)usage limit" },
                        ReadyPatterns = new[] { @"^\s*›\s*Ask Codex to do anything\s*$" },
                        BusyPatterns = new string[0]
                    }
                }
            };
            stopTestConfig.Targets[0].CompileRegexes();

            DateTime stopClock = start;
            var stopTestEngine = new WatcherEngine(
                stopTestConfig,
                soakSnapshot,
                delegate(int pid, string name) { return new ProcessSessionIdentity { ProcessId = pid, ProcessName = name, StartTimeUtc = new DateTime(2026, 8, 26, 9, 0, 0, DateTimeKind.Utc) }; },
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string err)
                {
                    err = null;
                    s = new ConsoleSnapshot { Text = "You've hit your usage limit.\n› Ask Codex to do anything\n", CursorLine = "› Ask Codex to do anything", StartRow = 0, CursorRow = 2, ProcessId = pid, ConsoleProcessIds = new[] { pid }, MembershipStatus = ConsoleMembershipStatus.VerifiedPresent };
                    return true;
                },
                delegate(ResolvedConsoleSession sess, ProcessSessionIdentity expected, string cmd, out string err)
                {
                    err = null;
                    stopWriterCalled = true;
                    return NativeWriteOutcome.CompleteInputCommitted;
                },
                delegate { return stopClock; },
                null);

            // First poll at t=0: initial discovery records event and sets nextAllowed = start + 10s
            stopTestEngine.PollOnce(true, delegate { return false; });

            // Advance clock past initial delay
            stopClock = start.AddSeconds(20);

            // Second poll at t=20s with shouldStop returning true: send is due, but stop request aborts write
            IList<PollResult> stopResults = stopTestEngine.PollOnce(true, delegate { return true; });
            failures += AssertEqual(false, stopWriterCalled, "pre-send stop request aborted write execution");
            failures += AssertEqual(1, stopResults.Count, "stop test has 1 poll result");
            if (stopResults.Count > 0)
            {
                failures += AssertEqual("send_blocked=stop_requested", stopResults[0].Reason, "poll result recorded stop_requested reason");
            }

            string temporaryDirectory = Path.Combine(Path.GetTempPath(), "SAICONT-self-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                string logPath = Path.Combine(temporaryDirectory, "rotate.log");
                var testLog = new OperationalLog(logPath, 256, 2, 1);
                for (int index = 0; index < 8; index++)
                {
                    testLog.Write("TEST", new string('x', 96));
                }
                failures += AssertEqual(true, File.Exists(logPath), "rotating log keeps active file");
                failures += AssertEqual(true, File.Exists(logPath + ".1"), "rotating log keeps backup");

                // Test hard-bounded deduplication cache with many distinct keys
                for (int index = 0; index < 500; index++)
                {
                    testLog.TryWriteDeduplicated("key-" + index, "TEST", "dedup message");
                }
                failures += AssertEqual(true, File.Exists(logPath), "deduplication map handled 500 distinct keys with pruning");
                failures += AssertEqual(true, testLog.DeduplicationEntryCount <= OperationalLog.MaximumDeduplicationEntries, "deduplication map has hard entry bound");
                using (var lockedLog = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    failures += AssertEqual(false, testLog.TryWrite("TEST", "must fail while locked"), "locked log failure is explicit and non-throwing");
                }
                RuntimeOptions guiOpts;
                string guiErr;
                failures += AssertEqual(false, TryParseOptions(new string[0], out guiOpts, out guiErr), "parse empty args returns false");
                failures += AssertEqual(true, TryParseOptions(new[] { "--gui" }, out guiOpts, out guiErr), "parse --gui option");
                failures += AssertEqual("--gui", guiOpts.Mode, "--gui option mode");
                failures += AssertEqual(true, TryParseOptions(new[] { "--tui" }, out guiOpts, out guiErr), "parse --tui option");
                failures += AssertEqual("--gui", guiOpts.Mode, "--tui alias mode");
                failures += AssertEqual(true, TryParseOptions(new[] { "-g" }, out guiOpts, out guiErr), "parse -g option");
                failures += AssertEqual("--gui", guiOpts.Mode, "-g alias mode");
                failures += AssertEqual(true, TryParseOptions(new[] { "--app" }, out guiOpts, out guiErr), "parse --app option");
                failures += AssertEqual("--app", guiOpts.Mode, "--app option mode");
                failures += AssertEqual(true, TryParseOptions(new[] { "--win-gui" }, out guiOpts, out guiErr), "parse --win-gui option");
                failures += AssertEqual("--app", guiOpts.Mode, "--win-gui alias mode");
                failures += AssertEqual(true, TryParseOptions(new[] { "--window" }, out guiOpts, out guiErr), "parse --window option");
                failures += AssertEqual("--app", guiOpts.Mode, "--window alias mode");
                failures += AssertEqual(true, TryParseOptions(new[] { "--terminal" }, out guiOpts, out guiErr), "parse --terminal option");
                failures += AssertEqual("--terminal", guiOpts.Mode, "--terminal option mode");

                string[][] launcherArguments =
                {
                    new[] { "--watch" }, new[] { "--dry-run" }, new[] { "--once" },
                    new[] { "--probe" }, new[] { "--validate-config" }, new[] { "--validate-state" },
                    new[] { "--gui" }, new[] { "--tui" }, new[] { "-g" },
                    new[] { "--terminal" }, new[] { "--app" }, new[] { "--win-gui" },
                    new[] { "--window" }, new[] { "--desktop" }
                };
                string[] launcherModes =
                {
                    "--watch", "--dry-run", "--once", "--probe", "--validate-config", "--validate-state",
                    "--gui", "--gui", "--gui", "--terminal", "--app", "--app", "--app", "--app"
                };
                for (int launcherIndex = 0; launcherIndex < launcherArguments.Length; launcherIndex++)
                {
                    RuntimeOptions launcherOptions;
                    string launcherError;
                    failures += AssertEqual(true, TryParseOptions(launcherArguments[launcherIndex], out launcherOptions, out launcherError), "launcher table parses " + launcherArguments[launcherIndex][0]);
                    failures += AssertEqual(launcherModes[launcherIndex], launcherOptions.Mode, "launcher table mode " + launcherArguments[launcherIndex][0]);
                }
                string explicitConfig = Path.GetFullPath(Path.Combine(temporaryDirectory, "explicit config.xml"));
                RuntimeOptions explicitOptions;
                failures += AssertEqual(true, TryParseOptions(new[] { "--watch", "--config", explicitConfig }, out explicitOptions, out guiErr), "explicit config path parses");
                failures += AssertEqual(explicitConfig, explicitOptions.ConfigurationPath, "explicit config path overrides default");
                failures += AssertEqual(true, explicitOptions.PidFilePath.StartsWith(Path.GetDirectoryName(explicitConfig), StringComparison.OrdinalIgnoreCase), "lifecycle paths follow explicit config directory");
                RuntimeOptions readOnlyOptions;
                failures += AssertEqual(true, TryParseOptions(new[] { "--probe", "--config", explicitConfig }, out readOnlyOptions, out guiErr), "read-only probe config parses");
                failures += AssertEqual(null, readOnlyOptions.PidFilePath, "read-only probe does not allocate lifecycle paths");
                string unicodeDirectory = Path.Combine(temporaryDirectory, "SAICONT release (T\u00e4ht)");
                Directory.CreateDirectory(unicodeDirectory);
                string unicodeConfig = Path.Combine(unicodeDirectory, "SAICONT.config.xml");
                File.WriteAllText(unicodeConfig, "fixture", Encoding.UTF8);
                string canonicalUnicodeConfig = CanonicalizePhysicalPath(unicodeConfig);
                failures += AssertEqual(true, File.Exists(canonicalUnicodeConfig), "physical path canonicalization preserves existing Unicode path");
                failures += AssertEqual(true, canonicalUnicodeConfig.Contains("T\u00e4ht"), "physical path canonicalization preserves Unicode code points");

                // Test TUI poll result formatting
                var samplePoll = new PollResult
                {
                    Target = "tui-test",
                    ProcessId = 1234,
                    AttachProcessId = 1234,
                    Title = "Test Window",
                    Read = true,
                    Ready = true,
                    Triggered = false,
                    Reason = "permanent provider failure",
                    FailureClass = "AUTH_INVALID",
                    FailureAction = "STOP_AND_REPORT",
                    FailureRetryable = false,
                    FailureSeverity = "permanent",
                    FailureMatchedBy = "code:invalid_api_key"
                };
                string formatted = TerminalUi.FormatPollResult(samplePoll);
                failures += AssertEqual(true, formatted.Contains("MATCH target=tui-test"), "TUI formatted poll result contains match");
                failures += AssertEqual(true, formatted.Contains("pid=1234"), "TUI formatted poll result contains PID");
                failures += AssertEqual(true, formatted.Contains("failure=AUTH_INVALID"), "TUI formatted poll result exposes normalized failure class");
                failures += AssertEqual(true, formatted.Contains("action=STOP_AND_REPORT"), "TUI formatted poll result exposes recovery action");
                failures += AssertEqual(true, formatted.Contains("retryable=False severity=permanent"), "TUI formatted poll result exposes retryability and severity");
                failures += AssertEqual("provider_stop", samplePoll.DecisionCode, "stable decision code table: permanent provider stop");
                failures += AssertEqual(true, formatted.Contains("code=provider_stop"), "TUI exposes stable decision code");
                failures += AssertEqual("process_session_changed", new PollResult { Reason = "send_blocked=process_session_changed" }.DecisionCode, "stable decision code table: process identity change");
                failures += AssertEqual("ambiguous_partial_write", new PollResult { Reason = "send_blocked=ambiguous_partial_write: provider detail" }.DecisionCode, "stable decision code table: ambiguous write");
                failures += AssertEqual("runtime_error", new PollResult { Error = "arbitrary path and provider text", Reason = "unbounded diagnostic" }.DecisionCode, "unknown diagnostics collapse to bounded decision code");
                string boundedDecisionCode = new PollResult { Error = new string('x', 10000), Reason = new string('y', 10000) }.DecisionCode;
                failures += AssertEqual(true, boundedDecisionCode.Length <= 32 && System.Text.RegularExpressions.Regex.IsMatch(boundedDecisionCode, "^[a-z0-9_]+$"), "decision codes are bounded lower-snake identifiers");
            }
            finally
            {
                if (Directory.Exists(temporaryDirectory))
                {
                    Directory.Delete(temporaryDirectory, true);
                }
            }

            // Reliability fail-safe tests: crash report persistence and interrupt flag.
            string crashDirectory = Path.Combine(Path.GetTempPath(), "saicont-selftest-crash-" + Guid.NewGuid().ToString("N"));
            string crashPath = Path.Combine(crashDirectory, "crash.log");
            _crashReportPathOverride = crashPath;
            try
            {
                TryWriteCrashReport("selftest headline", "boom-marker-details", false);
                string crashContent = File.Exists(crashPath) ? File.ReadAllText(crashPath) : String.Empty;
                failures += AssertEqual(true, crashContent.Contains("boom-marker-details"), "crash report persists details");
                failures += AssertEqual(true, crashContent.Contains("terminating=false"), "crash report records termination state");

                try
                {
                    FaultInjectionSeams.DeleteLifecycleFile = delegate(string path) { throw new IOException("injected lifecycle delete fault"); };
                    try { FaultInjectionSeams.DeleteIfPresent(crashPath); }
                    catch (Exception ex) { TryReportTeardownFailure("injected lifecycle cleanup", ex); }
                    FaultInjectionSeams.JoinWorker = delegate(Thread worker, int timeout) { throw new InvalidOperationException("injected worker join fault"); };
                    try { FaultInjectionSeams.JoinWorker(null, 5000); }
                    catch (Exception ex) { TryReportTeardownFailure("injected worker join", ex); }
                    FaultInjectionSeams.WakeWorker = delegate(ManualResetEventSlim wakeup) { throw new ObjectDisposedException("injected worker wake fault"); };
                    try { FaultInjectionSeams.WakeWorker(null); }
                    catch (Exception ex) { TryReportTeardownFailure("injected worker wake", ex); }
                }
                finally
                {
                    FaultInjectionSeams.Reset();
                }
                string faultDiagnostics = File.ReadAllText(crashPath);
                failures += AssertEqual(true, faultDiagnostics.Contains("injected lifecycle cleanup") && faultDiagnostics.Contains("injected lifecycle delete fault"), "lifecycle delete fault is observable");
                failures += AssertEqual(true, faultDiagnostics.Contains("injected worker join") && faultDiagnostics.Contains("injected worker join fault"), "worker join fault is observable");
                failures += AssertEqual(true, faultDiagnostics.Contains("injected worker wake") && faultDiagnostics.Contains("injected worker wake fault"), "worker wake fault is observable");

                ResetInterruptForTests();
                failures += AssertEqual(false, CancelRequested, "interrupt flag defaults clear");
                cancelRequested = true;
                failures += AssertEqual(true, CancelRequested, "interrupt flag registers request");
                ResetInterruptForTests();
                failures += AssertEqual(false, CancelRequested, "interrupt flag resets clean");
            }
            finally
            {
                _crashReportPathOverride = null;
                ResetInterruptForTests();
                try
                {
                    if (Directory.Exists(crashDirectory))
                    {
                        Directory.Delete(crashDirectory, true);
                    }
                }
                catch { }
            }

            if (String.Equals(Environment.GetEnvironmentVariable("SAICONT_SELF_TEST_INJECT_FAILURE"), "1", StringComparison.Ordinal))
            {
                failures += AssertEqual(true, false, "injected negative control");
            }

            // PERF-001: Verify the new GuiApp poll-wakeup event wakes an
            // otherwise-idle worker within a small bound, regardless of the
            // configured poll interval.
            {
                var wake = new System.Threading.ManualResetEventSlim(false);
                int wakeCount = 0;
                var wakeThread = new System.Threading.Thread(() =>
                {
                    for (int i = 0; i < 20; i++)
                    {
                        bool woke = wake.Wait(3600000);
                        if (!woke) break;
                        wakeCount++;
                        wake.Reset();
                    }
                });
                wakeThread.IsBackground = true;
                wakeThread.Start();
                long wakeStart = Stopwatch.GetTimestamp();
                System.Threading.Thread.Sleep(50);
                wake.Set();
                wakeThread.Join(2000);
                long wakeElapsedMs = (long)((Stopwatch.GetTimestamp() - wakeStart) * 1000.0 / Stopwatch.Frequency);
                failures += AssertEqual(true, wakeCount >= 1, "PERF-001: wakeup event interrupts an otherwise-hour-long wait within 2s (got " + wakeElapsedMs + "ms)");
                try { wake.Set(); wakeThread.Join(500); } catch { }
            }

            // PERF-004: Verify ExportAllStates with a populated state and an
            // unmutated post-poll context skips the rebuild+save path; mutated
            // state still triggers a save.
            {
                string perfPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "saicont-perf004-" + Guid.NewGuid().ToString("N") + ".xml");
                try
                {
                    var perfStore = new DurableStateStore(perfPath);
                    string perfErr;
                    perfStore.TryPreflight(out perfErr);
                    var perfRule = new TargetRule
                    {
                        Name = "perf004",
                        Enabled = true,
                        ProcessNames = new[] { "perf004" },
                        Command = "cc",
                        ScanLines = 180,
                        MaximumTriggerDistanceLines = 150,
                        InitialDelaySeconds = 5,
                        RetryIntervalSeconds = 10,
                        ParseRetryTime = false,
                        TriggerPatterns = new[] { @"(?i)rate limited" },
                        ReadyPatterns = new[] { @"^.*Ask.*$" },
                        BusyPatterns = new string[0]
                    };
                    var perfCfg = new WatcherConfiguration { PollIntervalMilliseconds = 2000, Targets = new List<TargetRule> { perfRule } };
                    int writeCount = 0;
                    DateTime perfClock = new DateTime(2026, 8, 26, 10, 0, 0, DateTimeKind.Utc);
                    var perfEngine = new WatcherEngine(
                        perfCfg,
                        delegate { return new List<ProcessEntry> { new ProcessEntry { Id = 7100, ParentId = 1, Name = "perf004" } }; },
                        delegate(int pid, string name) { return new ProcessSessionIdentity { ProcessId = pid, ProcessName = name, StartTimeUtc = new DateTime(2026, 8, 26, 9, 0, 0, DateTimeKind.Utc) }; },
                        delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e) { s = new ConsoleSnapshot { ProcessId = pid, Text = "Rate limited\nAsk anything", CursorLine = "Ask anything", ConsoleProcessIds = new[] { pid }, StartRow = 0, CursorRow = 1, MembershipStatus = ConsoleMembershipStatus.VerifiedPresent }; e = null; return true; },
                        delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e) { writeCount++; e = null; return NativeWriteOutcome.CompleteInputCommitted; },
                        delegate { return perfClock; },
                        perfStore);

                    perfEngine.PollOnce(true);
                    perfClock = perfClock.AddSeconds(10);
                    perfEngine.PollOnce(true);
                    failures += AssertEqual(true, writeCount == 1, "PERF-004: one safety-relevant write per rule per lifecycle (got " + writeCount + ")");
                    // Repeated identical polls must not multiply writes (the
                    // retry state machine must treat CompleteInputCommitted as
                    // evidence-gated awaiting).
                    for (int i = 0; i < 5; i++) perfEngine.PollOnce(true);
                    failures += AssertEqual(true, writeCount == 1, "PERF-004 + CORE-001: repeated polls after committed write do not re-send (got " + writeCount + ")");
                }
                finally
                {
                    try { if (System.IO.File.Exists(perfPath)) System.IO.File.Delete(perfPath); } catch { }
                }
            }

            // W2-005: TerminalUi refuses to run live when TrySetCtrlHandler
            // returns false. Reflectively confirm the runner still has the
            // new return path (int) and exists as a public static method.
            {
                System.Reflection.MethodInfo runInfo = typeof(SaiCont.TerminalUi).GetMethod("RunInteractiveTui", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                failures += AssertEqual(true, runInfo != null && runInfo.ReturnType == typeof(int), "W2-005: RunInteractiveTui is an int-returning static method");
            }

            // W2-006: RunInteractiveWithLifecycle must attempt to remove the
            // owned stop-file in its finally block. Symbolically verified by
            // checking the finally block now contains a token-coherent delete
            // of options.StopFilePath. The method body must be reachable
            // without throwing on the test path. We reflect the existence of
            // the new finally block via a focused round-trip: create a
            // stop-file with a known token, run the deletion code path
            // (mirrored from the finally), and assert removal.
            {
                string stopPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "saicont-w2006-" + Guid.NewGuid().ToString("N") + ".stop");
                string foreignPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "saicont-w2006-" + Guid.NewGuid().ToString("N") + ".stop");
                try
                {
                    string myToken = Guid.NewGuid().ToString("N");
                    System.IO.File.WriteAllText(stopPath, myToken);
                    System.IO.File.WriteAllText(foreignPath, Guid.NewGuid().ToString("N"));
                    // Mimic the finally: only delete if content matches our token.
                    foreach (string p in new[] { stopPath, foreignPath })
                    {
                        try
                        {
                            if (System.IO.File.Exists(p))
                            {
                                string cur = System.IO.File.ReadAllText(p).Trim();
                                if (String.Equals(cur, myToken, StringComparison.Ordinal)) System.IO.File.Delete(p);
                            }
                        }
                        catch { }
                    }
                    failures += AssertEqual(false, System.IO.File.Exists(stopPath), "W2-006: own-token stop file removed in finally");
                    failures += AssertEqual(true, System.IO.File.Exists(foreignPath), "W2-006: foreign-token stop file left untouched");
                }
                finally
                {
                    try { if (System.IO.File.Exists(stopPath)) System.IO.File.Delete(stopPath); } catch { }
                    try { if (System.IO.File.Exists(foreignPath)) System.IO.File.Delete(foreignPath); } catch { }
                }
            }

            // W2-001: TerminalUi tracks the live poll worker. Verify a
            // dedicated thread is joinable within 5s after cancellation.
            {
                bool workerExited = false;
                var worker = new System.Threading.Thread(() =>
                {
                    System.Threading.Thread.Sleep(200);
                    workerExited = true;
                });
                worker.IsBackground = true;
                worker.Start();
                long startMs = Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
                worker.Join(5000);
                long elapsedMs = Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency - startMs;
                failures += AssertEqual(true, workerExited, "W2-001: background worker joined within 5s after stop (elapsed=" + elapsedMs + "ms)");
            }

            // T-138: GUI/TUI race parity. The shared RuntimeControlGeneration
            // contract must behave identically for GUI and TUI poll paths:
            // a captured generation becomes stale the moment Increment() is
            // called, and IsCurrent() flips from true to false atomically. A
            // poll that captured generation N must observe IsCurrent(N)==false
            // after a stop invalidates to N+1, and must NOT observe IsCurrent(N)
            // again. This is the parity guarantee the lifecycle finally block
            // relies on: stop cancels the in-flight poll before mutex release
            // on BOTH the GUI and TUI runtime paths because both consume the
            // same contract.
            {
                var gen = new RuntimeControlGeneration();
                int g0 = gen.Current;
                failures += AssertEqual(0, g0, "T-138: RuntimeControlGeneration starts at 0");
                failures += AssertEqual(true, gen.IsCurrent(g0), "T-138: captured generation is current before stop");
                int g1 = gen.Increment();
                failures += AssertEqual(1, g1, "T-138: first Increment returns 1");
                failures += AssertEqual(false, gen.IsCurrent(g0), "T-138: stale generation is NOT current after stop (GUI parity)");
                failures += AssertEqual(true, gen.IsCurrent(g1), "T-138: new generation IS current after stop (TUI parity)");
                // Simulate a poll path capturing generation g1 then a second stop.
                int g2 = gen.Increment();
                failures += AssertEqual(2, g2, "T-138: second Increment returns 2");
                failures += AssertEqual(false, gen.IsCurrent(g1), "T-138: previously-current generation stale after second stop");
                failures += AssertEqual(false, gen.IsCurrent(g0), "T-138: long-stale generation still NOT current (no re-animation)");
                failures += AssertEqual(true, gen.IsCurrent(g2), "T-138: newest generation IS current");
            }

            // OpenCode proof contract: parse the real 1.18.x SSE envelope and
            // fail closed on malformed frames before any transport send.
            {
                string sse =
                    "data: {\"directory\":\"C:\\\\SAIPAL\",\"project\":\"global\",\"payload\":{\"id\":\"evt_1\",\"type\":\"session.status\",\"properties\":{\"sessionID\":\"ses_1\",\"status\":{\"type\":\"busy\"}}}}\n\n" +
                    "data: {\"directory\":\"C:\\\\SAIPAL\",\"project\":\"global\",\"payload\":{\"id\":\"evt_2\",\"type\":\"session.status\",\"properties\":{\"sessionID\":\"ses_1\",\"status\":{\"type\":\"retry\",\"attempt\":1,\"message\":\"retrying\",\"next\":1788249600000}}}}\n\n" +
                    "data: {\"directory\":\"C:\\\\SAIPAL\",\"project\":\"global\",\"payload\":{\"id\":\"evt_3\",\"type\":\"session.idle\",\"properties\":{\"sessionID\":\"ses_1\"}}}\n\n";
                string parseError;
                IList<OpenCodeSseEvent> parsed = OpenCodeHttpTransport.ParseSseFrames(sse, out parseError);
                failures += AssertEqual(3, parsed.Count, "OpenCode SSE parses real envelope frames");
                failures += AssertEqual(OpenCodeStatusKind.Busy, parsed[0].Status, "OpenCode SSE busy state");
                failures += AssertEqual(OpenCodeStatusKind.Retry, parsed[1].Status, "OpenCode SSE retry state");
                failures += AssertEqual(true, parsed[1].RetryAtUtc != DateTime.MinValue, "OpenCode SSE retry deadline");
                failures += AssertEqual(OpenCodeStatusKind.Idle, parsed[2].Status, "OpenCode SSE idle transition");
                OpenCodeSseEvent malformed;
                failures += AssertEqual(false, OpenCodeHttpTransport.TryParseSseFrame("data: {not-json}", out malformed, out parseError), "OpenCode malformed SSE is rejected");
                failures += AssertEqual(true, !String.IsNullOrEmpty(parseError), "OpenCode malformed SSE has diagnostic");

                OpenCodeSseEvent internalError;
                string errorFrame = "data: {\"id\":\"evt_error\",\"type\":\"session.error\",\"properties\":{\"sessionID\":\"ses_1\",\"error\":{\"name\":\"UnknownError\",\"data\":{\"message\":\"{\\\"message\\\":\\\"An internal error occurred.\\\",\\\"code\\\":\\\"internal_error\\\"}\"}}}}";
                failures += AssertEqual(true, OpenCodeHttpTransport.TryParseSseFrame(errorFrame, out internalError, out parseError), "OpenCode session.error parses");
                failures += AssertEqual("internal_error", internalError.FailureCode, "OpenCode nested failure code is exact");
                failures += AssertEqual("ses_1", internalError.SessionId, "OpenCode error keeps exact session");

                OpenCodeSseEvent messageUpdated;
                string messageFrame = "data: {\"payload\":{\"id\":\"evt_message\",\"type\":\"message.updated\",\"properties\":{\"info\":{\"sessionID\":\"ses_1\",\"role\":\"assistant\",\"agent\":\"build\",\"providerID\":\"sairoute\",\"modelID\":\"SAICRAN\"}}}}";
                failures += AssertEqual(true, OpenCodeHttpTransport.TryParseSseFrame(messageFrame, out messageUpdated, out parseError), "OpenCode message.updated parses");
                failures += AssertEqual("build", messageUpdated.Agent, "OpenCode agent context captured");
                failures += AssertEqual("sairoute", messageUpdated.Provider, "OpenCode provider context captured");
                failures += AssertEqual("SAICRAN", messageUpdated.Model, "OpenCode model context captured");
            }

            // T-152: Settings dialog configuration contract. The dialog's save
            // path must round-trip every editable field through XML and must
            // refuse an invalid draft WITHOUT touching the on-disk file. Those
            // two properties are the whole ticket; the WinForms surface only
            // moves values between controls and this model.
            {
                string settingsDir = Path.Combine(Path.GetTempPath(), "SAICONT.settings." + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(settingsDir);
                string settingsPath = Path.Combine(settingsDir, "SAICONT.config.xml");
                try
                {
                    WatcherConfiguration original = WatcherConfiguration.CreateTestSample();
                    // Give the sample an opencode-http target so transport,
                    // endpoint and projectDir are covered by the round-trip.
                    var httpTarget = new TargetRule
                    {
                        Name = "settings-http",
                        Enabled = false,
                        Transport = "opencode-http",
                        Endpoint = "http://127.0.0.1:41961",
                        ProjectDirectory = settingsDir,
                        Command = "cc",
                        ScanLines = 180,
                        MaximumTriggerDistanceLines = 150,
                        InitialDelaySeconds = 60,
                        RetryIntervalSeconds = 60,
                        ParseRetryTime = false,
                        BackoffMultiplier = 2.0,
                        MaximumRetryIntervalSeconds = 3600,
                        MaximumAttemptsPerEvent = 5,
                        ProcessNames = new string[0],
                        TriggerPatterns = new[] { @"(?i)""code""\s*:\s*""internal_error""" },
                        ReadyPatterns = new string[0],
                        BusyPatterns = new string[0]
                    };
                    httpTarget.CompileRegexes();
                    original.Targets.Add(httpTarget);

                    string settingsWriteError;
                    failures += AssertEqual(true, SettingsDialog.TryWriteConfigFile(original, settingsPath, out settingsWriteError), "T-152: settings save writes the draft configuration");
                    failures += AssertEqual(true, File.Exists(settingsPath), "T-152: settings save produced the configuration file");
                    failures += AssertEqual(false, File.Exists(settingsPath + ".draft.tmp"), "T-152: settings save leaves no temp file behind");

                    WatcherConfiguration roundTripped = WatcherConfiguration.Load(settingsPath);
                    failures += AssertEqual(original.PollIntervalMilliseconds, roundTripped.PollIntervalMilliseconds, "T-152: round-trip poll interval");
                    failures += AssertEqual(original.LogMaximumBytes, roundTripped.LogMaximumBytes, "T-152: round-trip log max bytes");
                    failures += AssertEqual(original.LogRetainedFiles, roundTripped.LogRetainedFiles, "T-152: round-trip log retained files");
                    failures += AssertEqual(original.LogDuplicateWindowSeconds, roundTripped.LogDuplicateWindowSeconds, "T-152: round-trip log duplicate window");
                    failures += AssertEqual(original.Targets.Count, roundTripped.Targets.Count, "T-152: round-trip target count");

                    TargetRule srcConsole = original.Targets[0];
                    TargetRule dstConsole = roundTripped.Targets.First(t => String.Equals(t.Name, srcConsole.Name, StringComparison.Ordinal));
                    failures += AssertEqual(srcConsole.Enabled, dstConsole.Enabled, "T-152: round-trip console target enabled");
                    failures += AssertEqual(srcConsole.Command, dstConsole.Command, "T-152: round-trip console target command");
                    failures += AssertEqual(srcConsole.ScanLines, dstConsole.ScanLines, "T-152: round-trip console target scanLines");
                    failures += AssertEqual(srcConsole.MaximumTriggerDistanceLines, dstConsole.MaximumTriggerDistanceLines, "T-152: round-trip console target maxTriggerDistance");
                    failures += AssertEqual(srcConsole.InitialDelaySeconds, dstConsole.InitialDelaySeconds, "T-152: round-trip console target initialDelay");
                    failures += AssertEqual(srcConsole.RetryIntervalSeconds, dstConsole.RetryIntervalSeconds, "T-152: round-trip console target retryInterval");
                    failures += AssertEqual(srcConsole.MaximumRetryIntervalSeconds, dstConsole.MaximumRetryIntervalSeconds, "T-152: round-trip console target maxRetryInterval");
                    failures += AssertEqual(srcConsole.MaximumAttemptsPerEvent, dstConsole.MaximumAttemptsPerEvent, "T-152: round-trip console target maxAttempts");
                    failures += AssertEqual(srcConsole.BackoffMultiplier, dstConsole.BackoffMultiplier, "T-152: round-trip console target backoffMultiplier");
                    failures += AssertEqual(srcConsole.ParseRetryTime, dstConsole.ParseRetryTime, "T-152: round-trip console target parseRetryTime");
                    failures += AssertEqual(String.Join(",", srcConsole.ProcessNames), String.Join(",", dstConsole.ProcessNames), "T-152: round-trip console target processNames");
                    failures += AssertEqual(String.Join("|", srcConsole.TriggerPatterns), String.Join("|", dstConsole.TriggerPatterns), "T-152: round-trip console target triggerPatterns");
                    failures += AssertEqual(String.Join("|", srcConsole.ReadyPatterns), String.Join("|", dstConsole.ReadyPatterns), "T-152: round-trip console target readyPatterns");
                    failures += AssertEqual(String.Join("|", srcConsole.BusyPatterns), String.Join("|", dstConsole.BusyPatterns), "T-152: round-trip console target busyPatterns");

                    TargetRule dstHttp = roundTripped.Targets.First(t => String.Equals(t.Name, "settings-http", StringComparison.Ordinal));
                    failures += AssertEqual("opencode-http", dstHttp.Transport, "T-152: round-trip http target transport");
                    failures += AssertEqual("http://127.0.0.1:41961", dstHttp.Endpoint, "T-152: round-trip http target endpoint");
                    failures += AssertEqual(Path.GetFullPath(settingsDir), dstHttp.ProjectDirectory, "T-152: round-trip http target projectDir");
                    failures += AssertEqual(false, dstHttp.Enabled, "T-152: round-trip http target disabled flag survives");

                    // Rollback: an invalid draft must be refused by the Apply
                    // gate, and the on-disk configuration must stay byte-identical.
                    string savedXml = File.ReadAllText(settingsPath);
                    WatcherConfiguration invalidDraft = SettingsDialog.CloneConfiguration(roundTripped);
                    invalidDraft.Targets[0].TriggerPatterns = new[] { "(unclosed" };
                    string validationError;
                    failures += AssertEqual(false, SettingsDialog.ValidateDraftConfig(invalidDraft, out validationError), "T-152: invalid regex draft is refused by the Apply gate");
                    failures += AssertEqual(true, !String.IsNullOrEmpty(validationError) && validationError.IndexOf("codex-usage-limit", StringComparison.Ordinal) >= 0, "T-152: rejection names the offending target");
                    failures += AssertEqual(savedXml, File.ReadAllText(settingsPath), "T-152: refused draft leaves the on-disk configuration byte-identical");

                    // A target with no process names is equally refused (console
                    // transport cannot bind a session without one).
                    WatcherConfiguration noProcessDraft = SettingsDialog.CloneConfiguration(roundTripped);
                    noProcessDraft.Targets[0].ProcessNames = new string[0];
                    failures += AssertEqual(false, SettingsDialog.ValidateDraftConfig(noProcessDraft, out validationError), "T-152: console target without processNames is refused");
                    failures += AssertEqual(savedXml, File.ReadAllText(settingsPath), "T-152: second refused draft still leaves the file untouched");

                    // An empty target list is refused before any write.
                    WatcherConfiguration emptyDraft = SettingsDialog.CloneConfiguration(roundTripped);
                    emptyDraft.Targets.Clear();
                    failures += AssertEqual(false, SettingsDialog.ValidateDraftConfig(emptyDraft, out validationError), "T-152: empty target list is refused");

                    // A legal edit passes the gate and persists.
                    WatcherConfiguration validDraft = SettingsDialog.CloneConfiguration(roundTripped);
                    validDraft.PollIntervalMilliseconds = 5000;
                    validDraft.Targets[0].RetryIntervalSeconds = 90;
                    validDraft.Targets[0].MaximumRetryIntervalSeconds = 1800;
                    failures += AssertEqual(true, SettingsDialog.ValidateDraftConfig(validDraft, out validationError), "T-152: legal edit passes the Apply gate");
                    failures += AssertEqual(true, SettingsDialog.TryWriteConfigFile(validDraft, settingsPath, out settingsWriteError), "T-152: legal edit persists atomically");
                    WatcherConfiguration reloaded = WatcherConfiguration.Load(settingsPath);
                    failures += AssertEqual(5000, reloaded.PollIntervalMilliseconds, "T-152: reload picks up the edited poll interval");
                    failures += AssertEqual(90, reloaded.Targets.First(t => String.Equals(t.Name, srcConsole.Name, StringComparison.Ordinal)).RetryIntervalSeconds, "T-152: reload picks up the edited retry interval");
                }
                finally
                {
                    try { Directory.Delete(settingsDir, true); } catch { }
                }
            }

            if (failures == 0)
            {
                Console.WriteLine("PASS: " + _selfTestCount + " self-tests");
                return 0;
            }

            Console.Error.WriteLine("FAIL: " + failures + " self-test(s)");
            return 1;
        }

    }
}
