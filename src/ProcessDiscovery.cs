using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace SaiCont
{
    internal sealed class ProcessDiscoveryException : InvalidOperationException
    {
        public int Win32Error { get; private set; }

        public ProcessDiscoveryException(string message, int win32Error)
            : base(message)
        {
            Win32Error = win32Error;
        }
    }

    internal sealed class ProcessEntry
    {
        public int Id;
        public int ParentId;
        public string Name;
    }

    internal sealed class ProcessSessionIdentity : IEquatable<ProcessSessionIdentity>
    {
        public int ProcessId;
        public DateTime StartTimeUtc;
        public string ProcessName;

        public bool IsStrong
        {
            get
            {
                return ProcessId > 0 &&
                    StartTimeUtc != DateTime.MinValue;
            }
        }

        public bool Equals(ProcessSessionIdentity other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return ProcessId == other.ProcessId &&
                   StartTimeUtc.Equals(other.StartTimeUtc) &&
                   String.Equals(ProcessName, other.ProcessName, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as ProcessSessionIdentity);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = ProcessId;
                hashCode = (hashCode * 397) ^ StartTimeUtc.GetHashCode();
                hashCode = (hashCode * 397) ^ (ProcessName != null ? StringComparer.OrdinalIgnoreCase.GetHashCode(ProcessName) : 0);
                return hashCode;
            }
        }

        public override string ToString()
        {
            return ProcessName + ":" + ProcessId + ":" + (StartTimeUtc == DateTime.MinValue ? "0" : StartTimeUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    internal sealed class ResolvedConsoleSession
    {
        public ProcessSessionIdentity MatchedTargetSession;
        public int ResolvedAttachProcessId;
        public IList<int> ConsoleProcessIds;
        public IntPtr WindowHandle;
        public string StableConsoleId;
        public ConsoleSnapshot Snapshot;
        public DateTime ResolvedUtc;
        public string ResolutionError;
    }

    internal sealed class ConsoleCandidate
    {
        public ProcessSessionIdentity MatchedSession;
        public int MatchedProcessId { get { return MatchedSession != null ? MatchedSession.ProcessId : 0; } }
        public string MatchedProcessName { get { return MatchedSession != null ? MatchedSession.ProcessName : String.Empty; } }
        public int ParentProcessId;
        public IList<int> AttachProcessIds;

        public int PrimaryAttachProcessId
        {
            get
            {
                return AttachProcessIds != null && AttachProcessIds.Count > 0 ? AttachProcessIds[0] : MatchedProcessId;
            }
        }
    }

    internal delegate bool ConsoleReadAttempt(int processId, int lineCount, out ConsoleSnapshot snapshot, out string error);

    /// <summary>
    /// PERF-003: poll-local console observation cache.
    ///
    /// One NativeConsole.TryRead is not a memory lookup: it serializes through
    /// the console lock, detaches/attaches a console, opens CONOUT$, reads the
    /// screen in 64-row Win32 blocks (three blocks at the shipped scanLines=180),
    /// queries the title and the console process list, then restores the host
    /// console. Console contents are ONE poll-generation fact, but the engine
    /// evaluated each rule independently, so N rules covering one console paid
    /// that cost N times -- scaling with rule count instead of unique consoles.
    ///
    /// This cache captures each unique console once, at the deepest scan any
    /// sharing rule needs, and derives each rule's shallower tail from that
    /// immutable snapshot. Identity is strong: a later attach PID reuses a
    /// snapshot only when the live console process list of that snapshot contains
    /// it. The cache lives for exactly one PollOnce, so PID reuse or console
    /// migration between polls cannot merge distinct sessions.
    ///
    /// It is deliberately NOT used for the transactional pre-send reread: that
    /// path must observe the console as it is immediately before the write.
    /// </summary>
    internal sealed class PollConsoleObservationCache
    {
        private readonly Dictionary<int, ConsoleSnapshot> _byAttachPid = new Dictionary<int, ConsoleSnapshot>();
        private readonly Dictionary<int, string> _failedAttachPids = new Dictionary<int, string>();
        private readonly List<ConsoleSnapshot> _captured = new List<ConsoleSnapshot>();
        // PERF-003/PERF-004: identical (console, depth) requests must return the
        // SAME snapshot instance, so the rule-independent failure assessment can
        // be memoized per snapshot across rules that share both.
        private readonly Dictionary<string, ConsoleSnapshot> _derivedTails = new Dictionary<string, ConsoleSnapshot>(StringComparer.Ordinal);
        private int _captureCount;

        /// <summary>Test seam: how many real console reads this poll performed.</summary>
        internal int CaptureCount { get { return _captureCount; } }

        internal ConsoleReadAttempt Wrap(ConsoleReadAttempt inner, int captureLines)
        {
            return delegate(int processId, int lineCount, out ConsoleSnapshot snapshot, out string error)
            {
                ConsoleSnapshot cached;
                if (_byAttachPid.TryGetValue(processId, out cached))
                {
                    snapshot = Tail(processId, cached, lineCount);
                    error = null;
                    return true;
                }

                // Strong identity: a console already captured this poll whose live
                // process list contains this PID IS this PID's console.
                for (int i = 0; i < _captured.Count; i++)
                {
                    ConsoleSnapshot candidate = _captured[i];
                    if (candidate.MembershipStatus == ConsoleMembershipStatus.QueryFailed) continue;
                    if (!ProcessDiscovery.ConsoleServesMatchedProcess(candidate.ConsoleProcessIds, processId)) continue;
                    _byAttachPid[processId] = candidate;
                    snapshot = Tail(processId, candidate, lineCount);
                    error = null;
                    return true;
                }

                string cachedError;
                if (_failedAttachPids.TryGetValue(processId, out cachedError))
                {
                    snapshot = null;
                    error = cachedError;
                    return false;
                }

                int depth = Math.Max(lineCount, captureLines);
                ConsoleSnapshot fresh;
                string readError;
                _captureCount++;
                if (!inner(processId, depth, out fresh, out readError) || fresh == null)
                {
                    // A PID that cannot be attached this poll will not become
                    // attachable microseconds later in the same poll; the next
                    // poll re-probes it with a fresh cache.
                    _failedAttachPids[processId] = readError;
                    snapshot = null;
                    error = readError;
                    return false;
                }

                _byAttachPid[processId] = fresh;
                _captured.Add(fresh);
                snapshot = Tail(processId, fresh, lineCount);
                error = null;
                return true;
            };
        }

        private ConsoleSnapshot Tail(int processId, ConsoleSnapshot source, int lineCount)
        {
            string key = processId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + lineCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ConsoleSnapshot existing;
            if (_derivedTails.TryGetValue(key, out existing))
            {
                return existing;
            }
            ConsoleSnapshot derived = DeriveTail(source, lineCount);
            _derivedTails[key] = derived;
            return derived;
        }

        /// <summary>
        /// PERF-003: a rule asking for fewer lines than the capture depth sees the
        /// same tail an independent read would have produced: the last
        /// <paramref name="lineCount"/> rows ending at the cursor row, with
        /// StartRow adjusted accordingly. Everything else is identical because it
        /// describes the same console at the same instant.
        /// </summary>
        internal static ConsoleSnapshot DeriveTail(ConsoleSnapshot source, int lineCount)
        {
            if (source == null) return null;
            int capped = Math.Min(Math.Max(1, lineCount), 2000);
            string[] lines = (source.Text ?? String.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            if (lines.Length <= capped)
            {
                return source;
            }

            int skip = lines.Length - capped;
            var tail = new string[capped];
            Array.Copy(lines, skip, tail, 0, capped);
            return new ConsoleSnapshot
            {
                ProcessId = source.ProcessId,
                WindowHandle = source.WindowHandle,
                Title = source.Title,
                Text = String.Join(Environment.NewLine, tail),
                CursorLine = source.CursorLine,
                CursorColumn = source.CursorColumn,
                CursorRow = source.CursorRow,
                StartRow = source.StartRow + skip,
                BufferWidth = source.BufferWidth,
                ConsoleProcessIds = source.ConsoleProcessIds,
                MembershipStatus = source.MembershipStatus,
                MembershipError = source.MembershipError
            };
        }
    }

    // PERF-004: cheap membership-only check delegate. Performs AttachConsole +
    // GetConsoleProcessList without reading screen content.
    internal delegate bool ConsoleMembershipCheck(int processId, out IList<int> processIds, out string error);

    // PERF-006: immutable index over one process snapshot, built once per
    // poll and reused for every rule. Contains ById and normalized ByName
    // buckets. BuildAttachCandidates already handles child discovery via
    // ById traversal, so ChildrenByParentId is omitted.
    internal sealed class ProcessSnapshotIndex
    {
        public readonly Dictionary<int, ProcessEntry> ById;
        public readonly Dictionary<int, List<ProcessEntry>> ChildrenByParentId;
        public readonly Dictionary<string, List<ProcessEntry>> ByName;
        public readonly Dictionary<int, ProcessSessionIdentity> SessionIdentities;

        public ProcessSnapshotIndex(IList<ProcessEntry> processes)
        {
            ById = new Dictionary<int, ProcessEntry>();
            ChildrenByParentId = new Dictionary<int, List<ProcessEntry>>();
            ByName = new Dictionary<string, List<ProcessEntry>>(StringComparer.OrdinalIgnoreCase);
            SessionIdentities = new Dictionary<int, ProcessSessionIdentity>();

            foreach (ProcessEntry process in processes)
            {
                ById[process.Id] = process;
                List<ProcessEntry> children;
                if (!ChildrenByParentId.TryGetValue(process.ParentId, out children))
                {
                    children = new List<ProcessEntry>();
                    ChildrenByParentId[process.ParentId] = children;
                }
                children.Add(process);

                string normalizedName = ProcessDiscovery.NormalizeName(process.Name);
                List<ProcessEntry> nameGroup;
                if (!ByName.TryGetValue(normalizedName, out nameGroup))
                {
                    nameGroup = new List<ProcessEntry>();
                    ByName[normalizedName] = nameGroup;
                }
                nameGroup.Add(process);
            }
        }
    }

    internal static class ProcessDiscovery
    {
        private const uint SnapshotProcesses = 0x00000002;
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);
        private const int MaximumCandidateCount = 12;

        public static ProcessSessionIdentity ResolveSessionIdentity(int processId, string processName)
        {
            DateTime startTime = DateTime.MinValue;
            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    startTime = process.StartTime.ToUniversalTime();
                }
            }
            catch
            {
            }

            return new ProcessSessionIdentity
            {
                ProcessId = processId,
                ProcessName = NormalizeName(processName),
                StartTimeUtc = startTime
            };
        }

        public static IList<ProcessEntry> Snapshot()
        {
            var entries = new List<ProcessEntry>();
            IntPtr snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
            if (snapshot == InvalidHandleValue)
            {
                int win32Error = Marshal.GetLastWin32Error();
                throw new ProcessDiscoveryException("Process snapshot failed (Win32 " + win32Error + ").", win32Error);
            }

            try
            {
                var native = new ProcessEntry32();
                native.Size = (uint)Marshal.SizeOf(typeof(ProcessEntry32));
                if (!Process32First(snapshot, ref native))
                {
                    int win32Error = Marshal.GetLastWin32Error();
                    if (win32Error == 18)
                    {
                        return entries;
                    }
                    throw new ProcessDiscoveryException("Process enumeration start failed (Win32 " + win32Error + ").", win32Error);
                }

                do
                {
                    entries.Add(new ProcessEntry
                    {
                        Id = (int)native.ProcessId,
                        ParentId = (int)native.ParentProcessId,
                        Name = NormalizeName(native.ExecutableFile)
                    });
                }
                while (Process32Next(snapshot, ref native));

                int terminalError = Marshal.GetLastWin32Error();
                if (terminalError != 18)
                {
                    throw new ProcessDiscoveryException("Process enumeration failed (Win32 " + terminalError + ").", terminalError);
                }
            }
            finally
            {
                CloseHandle(snapshot);
            }

            return entries;
        }

        public static IList<ConsoleCandidate> FindCandidates(
            IList<ProcessEntry> processes,
            ISet<string> targetNames,
            Func<int, string, ProcessSessionIdentity> sessionResolver = null)
        {
            if (sessionResolver == null)
            {
                sessionResolver = ResolveSessionIdentity;
            }

            var byId = new Dictionary<int, ProcessEntry>();
            foreach (ProcessEntry process in processes)
            {
                byId[process.Id] = process;
            }

            var candidates = new List<ConsoleCandidate>();
            foreach (ProcessEntry matched in processes)
            {
                if (!targetNames.Contains(NormalizeName(matched.Name)))
                {
                    continue;
                }

                ProcessSessionIdentity session = sessionResolver(matched.Id, matched.Name);
                candidates.Add(new ConsoleCandidate
                {
                    MatchedSession = session,
                    ParentProcessId = matched.ParentId,
                    AttachProcessIds = BuildAttachCandidates(matched, byId)
                });
            }

            return candidates;
        }

        // PERF-006: overload that accepts a pre-built index, eliminating
        // redundant per-rule dictionary rebuild. The index is created once
        // per snapshot in PollOnce and reused for every enabled rule.
        public static IList<ConsoleCandidate> FindCandidates(
            ProcessSnapshotIndex index,
            ISet<string> targetNames,
            Func<int, string, ProcessSessionIdentity> sessionResolver = null)
        {
            if (sessionResolver == null)
            {
                sessionResolver = ResolveSessionIdentity;
            }

            var candidates = new List<ConsoleCandidate>();
            foreach (string targetName in targetNames)
            {
                List<ProcessEntry> matched;
                if (!index.ByName.TryGetValue(targetName, out matched))
                {
                    continue;
                }

                foreach (ProcessEntry process in matched)
                {
                    ProcessSessionIdentity session;
                    if (!index.SessionIdentities.TryGetValue(process.Id, out session))
                    {
                        session = sessionResolver(process.Id, process.Name);
                        index.SessionIdentities[process.Id] = session;
                    }
                    candidates.Add(new ConsoleCandidate
                    {
                        MatchedSession = session,
                        ParentProcessId = process.ParentId,
                        AttachProcessIds = BuildAttachCandidates(process, index.ById, index.ChildrenByParentId)
                    });
                }
            }

            return candidates;
        }

        public static IList<int> BuildAttachCandidates(ProcessEntry matched, IDictionary<int, ProcessEntry> byId)
        {
            return BuildAttachCandidates(matched, byId, null);
        }

        internal static IList<int> BuildAttachCandidates(ProcessEntry matched, IDictionary<int, ProcessEntry> byId, IDictionary<int, List<ProcessEntry>> childrenByParentId)
        {
            var ids = new List<int>();
            var seen = new HashSet<int>();
            if (matched == null)
            {
                return ids;
            }

            if (seen.Add(matched.Id))
            {
                ids.Add(matched.Id);
            }

            ProcessEntry current = matched;
            var visitedAncestors = new HashSet<int> { matched.Id };
            while (current != null && ids.Count < MaximumCandidateCount)
            {
                ProcessEntry parent;
                if (byId == null || !byId.TryGetValue(current.ParentId, out parent) || parent == null || !visitedAncestors.Add(parent.Id))
                {
                    break;
                }

                if (seen.Add(parent.Id))
                {
                    ids.Add(parent.Id);
                }
                current = parent;
            }

            if (ids.Count < MaximumCandidateCount)
            {
                List<ProcessEntry> children = null;
                if (childrenByParentId != null)
                {
                    childrenByParentId.TryGetValue(matched.Id, out children);
                }
                else if (byId != null)
                {
                    children = byId.Values.Where(entry => entry.ParentId == matched.Id).ToList();
                }
                foreach (ProcessEntry entry in children ?? new List<ProcessEntry>())
                {
                    if (ids.Count >= MaximumCandidateCount)
                    {
                        break;
                    }
                    if (seen.Add(entry.Id))
                    {
                        ids.Add(entry.Id);
                    }
                }
            }

            return ids;
        }

        internal static bool ConsoleServesMatchedProcess(IList<int> consoleProcessIds, int matchedProcessId)
        {
            if (consoleProcessIds == null || consoleProcessIds.Count == 0)
            {
                return false;
            }

            foreach (int processId in consoleProcessIds)
            {
                if (processId == matchedProcessId)
                {
                    return true;
                }
            }
            return false;
        }

        internal static string ComputeStableConsoleId(ConsoleSnapshot snapshot, int resolvedAttach)
        {
            return ComputeStableConsoleId(snapshot, resolvedAttach, 0);
        }

        internal static string ComputeStableConsoleId(ConsoleSnapshot snapshot, int resolvedAttach, int matchedProcessId)
        {
            if (snapshot == null)
            {
                return resolvedAttach.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (snapshot.WindowHandle != IntPtr.Zero)
            {
                return "win:" + snapshot.WindowHandle.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (matchedProcessId > 0 && ConsoleServesMatchedProcess(snapshot.ConsoleProcessIds, matchedProcessId))
            {
                return "session:" + matchedProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (snapshot.ConsoleProcessIds != null && snapshot.ConsoleProcessIds.Count > 0)
            {
                var ids = new List<int>(snapshot.ConsoleProcessIds);
                ids.Sort();
                return "pids:" + String.Join(",", ids.Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray());
            }

            return "attach:" + resolvedAttach.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static bool TryResolveConsoleSession(
            ConsoleCandidate candidate,
            ConsoleReadAttempt read,
            out ResolvedConsoleSession session,
            out string error)
        {
            return TryResolveConsoleSession(candidate, read, 180, null, out session, out error);
        }

        internal static bool TryResolveConsoleSession(
            ConsoleCandidate candidate,
            ConsoleReadAttempt read,
            int lineCount,
            out ResolvedConsoleSession session,
            out string error)
        {
            return TryResolveConsoleSession(candidate, read, lineCount, null, out session, out error);
        }

        // PERF-004: overload that accepts an optional membership checker for
        // cheap pre-read rejection of wrong-console candidates.
        internal static bool TryResolveConsoleSession(
            ConsoleCandidate candidate,
            ConsoleReadAttempt read,
            int lineCount,
            ConsoleMembershipCheck membershipChecker,
            out ResolvedConsoleSession session,
            out string error)
        {
            session = null;
            error = null;
            if (candidate == null || candidate.MatchedSession == null)
            {
                error = "candidate or matched session is null";
                return false;
            }

            int selectedPid;
            ConsoleSnapshot snapshot;
            string selectError;
            if (!TrySelectConsole(candidate.AttachProcessIds, candidate.MatchedProcessId, read, membershipChecker, lineCount, out selectedPid, out snapshot, out selectError))
            {
                error = selectError;
                return false;
            }

            session = new ResolvedConsoleSession
            {
                MatchedTargetSession = candidate.MatchedSession,
                ResolvedAttachProcessId = selectedPid,
                ConsoleProcessIds = snapshot.ConsoleProcessIds,
                WindowHandle = snapshot.WindowHandle,
                StableConsoleId = ComputeStableConsoleId(snapshot, selectedPid, candidate.MatchedProcessId),
                Snapshot = snapshot,
                ResolvedUtc = DateTime.UtcNow,
                ResolutionError = null
            };
            return true;
        }

        internal static bool TrySelectConsole(
            IList<int> attachPids,
            int matchedProcessId,
            ConsoleReadAttempt read,
            out int selectedPid,
            out ConsoleSnapshot snapshot,
            out string lastError)
        {
            return TrySelectConsole(attachPids, matchedProcessId, read, 180, out selectedPid, out snapshot, out lastError);
        }

        internal static bool TrySelectConsole(
            IList<int> attachPids,
            int matchedProcessId,
            ConsoleReadAttempt read,
            int lineCount,
            out int selectedPid,
            out ConsoleSnapshot snapshot,
            out string lastError)
        {
            return TrySelectConsole(attachPids, matchedProcessId, read, null, lineCount, out selectedPid, out snapshot, out lastError);
        }

        // PERF-004: membership-first overload. When a membershipChecker is
        // provided, wrong-console candidates are rejected via a cheap
        // AttachConsole + GetConsoleProcessList check before the expensive
        // per-row screen extraction. The accepted candidate still pays the
        // full read cost.
        internal static bool TrySelectConsole(
            IList<int> attachPids,
            int matchedProcessId,
            ConsoleReadAttempt read,
            ConsoleMembershipCheck membershipChecker,
            int lineCount,
            out int selectedPid,
            out ConsoleSnapshot snapshot,
            out string lastError)
        {
            selectedPid = 0;
            snapshot = null;
            lastError = null;
            if (attachPids == null)
            {
                return false;
            }

            foreach (int pid in attachPids)
            {
                // PERF-004: when a membership checker is available, verify
                // console membership BEFORE the expensive screen-content read.
                // Wrong-console candidates pay zero screen-buffer I/O.
                if (membershipChecker != null)
                {
                    IList<int> membershipPids;
                    string membershipError;
                    if (!membershipChecker(pid, out membershipPids, out membershipError))
                    {
                        lastError = membershipError;
                        continue;
                    }

                    if (!ConsoleServesMatchedProcess(membershipPids, matchedProcessId))
                    {
                        lastError = "attached console (PID " + pid + ") does not contain matched process " + matchedProcessId;
                        continue;
                    }
                }

                ConsoleSnapshot attemptSnapshot;
                string attemptError;
                if (!read(pid, lineCount, out attemptSnapshot, out attemptError))
                {
                    lastError = attemptError;
                    continue;
                }

                if (ConsoleServesMatchedProcess(attemptSnapshot.ConsoleProcessIds, matchedProcessId))
                {
                    selectedPid = pid;
                    snapshot = attemptSnapshot;
                    lastError = null;
                    return true;
                }

                lastError = "attached console (PID " + pid + ") does not contain matched process " + matchedProcessId;
            }

            return false;
        }

        internal static string NormalizeName(string name)
        {
            if (String.IsNullOrEmpty(name))
            {
                return String.Empty;
            }

            string value = name.Trim().ToLowerInvariant();
            if (value.EndsWith(".exe", StringComparison.Ordinal))
            {
                value = value.Substring(0, value.Length - 4);
            }
            return value;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public IntPtr DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int PriorityClassBase;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExecutableFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
