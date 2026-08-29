using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace SaiCont
{
    // W2-002: the native-write transaction boundary must not be a boolean.
    // Only "no input committed" may enter ordinary retry/backoff; a partial
    // accepted write is ambiguous (a command prefix or Enter may already be in
    // the target buffer) and must fail closed until console evidence resolves
    // the outcome. A complete accepted write is committed and must never be
    // blindly re-dispatched by elapsed time.
    internal enum NativeWriteOutcome
    {
        NoInputCommitted = 0,
        CompleteInputCommitted = 1,
        AmbiguousOrPartialInput = 2
    }

    internal enum ConsoleMembershipStatus
    {
        VerifiedPresent,
        VerifiedAbsent,
        QueryFailed
    }

    internal enum ConsoleProcessListDisposition
    {
        Failed,
        Complete,
        Retry,
        OverSafetyLimit
    }

    internal sealed class ConsoleSnapshot
    {
        public int ProcessId;
        public IntPtr WindowHandle;
        public string Title;
        public string Text;
        public string CursorLine;
        public int CursorColumn;
        public int CursorRow;
        public int StartRow;
        public int BufferWidth;
        public IList<int> ConsoleProcessIds;
        public ConsoleMembershipStatus MembershipStatus;
        public string MembershipError;
    }

    internal static class NativeConsole
    {
        internal const int MaximumScanLines = 2000;

        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint ShareRead = 0x00000001;
        private const uint ShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const short KeyEvent = 0x0001;
        private const ushort VkReturn = 0x0D;
        private const uint MaxProcessListBuffer = 1024;
        private static readonly object ConsoleLock = new object();

        public static bool TryGetConsoleProcessList(out IList<int> processIds, out string error)
        {
            processIds = new List<int>();
            error = null;

            uint[] buffer = new uint[64];
            for (int attempt = 0; attempt < 4; attempt++)
            {
                uint count = GetConsoleProcessList(buffer, (uint)buffer.Length);
                ConsoleProcessListDisposition disposition = ClassifyProcessListCount(count, (uint)buffer.Length, MaxProcessListBuffer);
                if (disposition == ConsoleProcessListDisposition.Failed)
                {
                    int code = Marshal.GetLastWin32Error();
                    error = "GetConsoleProcessList failed: " + new Win32Exception(code).Message + " (" + code + ")";
                    return false;
                }

                if (disposition == ConsoleProcessListDisposition.OverSafetyLimit)
                {
                    error = "GetConsoleProcessList returned " + count + " processes, above the safety limit of " + MaxProcessListBuffer + ".";
                    return false;
                }

                if (disposition == ConsoleProcessListDisposition.Retry)
                {
                    buffer = new uint[count];
                    continue;
                }

                for (int index = 0; index < (int)count; index++)
                {
                    processIds.Add((int)buffer[index]);
                }
                return true;
            }

            error = "GetConsoleProcessList membership changed repeatedly during bounded retrieval.";
            return false;
        }

        internal static ConsoleProcessListDisposition ClassifyProcessListCount(uint count, uint capacity, uint safetyLimit)
        {
            if (count == 0)
            {
                return ConsoleProcessListDisposition.Failed;
            }
            if (count > safetyLimit)
            {
                return ConsoleProcessListDisposition.OverSafetyLimit;
            }
            if (count > capacity)
            {
                return ConsoleProcessListDisposition.Retry;
            }
            return ConsoleProcessListDisposition.Complete;
        }

        public static bool TryRead(int processId, int lineCount, out ConsoleSnapshot snapshot, out string error)
        {
            snapshot = null;
            error = null;

            lock (ConsoleLock)
            {
                FreeConsole();
                if (!AttachConsole((uint)processId))
                {
                    error = Win32Error("AttachConsole", processId);
                    TryRestoreHostConsole();
                    return false;
                }

                try
                {
                    using (SafeFileHandle output = OpenConsoleDevice("CONOUT$"))
                    {
                        if (output.IsInvalid)
                        {
                            error = Win32Error("CreateFile(CONOUT$)", processId);
                            return false;
                        }

                        ConsoleScreenBufferInfo info;
                        if (!GetConsoleScreenBufferInfo(output, out info))
                        {
                            error = Win32Error("GetConsoleScreenBufferInfo", processId);
                            return false;
                        }

                        int width = Math.Max(1, Math.Min(512, (int)info.Size.X));
                        int endRow = Math.Max(0, (int)info.CursorPosition.Y);
                        int cappedLines = Math.Min(Math.Max(1, lineCount), 2000);
                        int startRow = Math.Max(0, endRow - cappedLines + 1);
                        var lines = new List<string>(endRow - startRow + 1);
                        string cursorLine = String.Empty;
                        var rowBuffer = new StringBuilder(width);

                        for (int row = startRow; row <= endRow; row++)
                        {
                            rowBuffer.Length = 0;
                            uint charsRead;
                            if (!ReadConsoleOutputCharacterW(output, rowBuffer, (uint)width, new Coord(0, (short)row), out charsRead))
                            {
                                error = Win32Error("ReadConsoleOutputCharacter", processId);
                                return false;
                            }

                            string line = rowBuffer.ToString(0, (int)charsRead).TrimEnd('\0', ' ');
                            lines.Add(line);
                            if (row == endRow)
                            {
                                cursorLine = line;
                            }
                        }

                        var title = new StringBuilder(1024);
                        GetConsoleTitleW(title, title.Capacity);

                        IList<int> clients;
                        string membershipError;
                        bool queryOk = TryGetConsoleProcessList(out clients, out membershipError);
                        ConsoleMembershipStatus memStatus = ConsoleMembershipStatus.QueryFailed;
                        if (queryOk)
                        {
                            memStatus = clients.Contains(processId) ? ConsoleMembershipStatus.VerifiedPresent : ConsoleMembershipStatus.VerifiedAbsent;
                        }

                        snapshot = new ConsoleSnapshot
                        {
                            ProcessId = processId,
                            WindowHandle = GetConsoleWindow(),
                            Title = title.ToString(),
                            Text = String.Join(Environment.NewLine, lines.ToArray()),
                            CursorLine = cursorLine,
                            CursorColumn = info.CursorPosition.X,
                            CursorRow = info.CursorPosition.Y,
                            StartRow = startRow,
                            BufferWidth = width,
                            ConsoleProcessIds = clients,
                            MembershipStatus = memStatus,
                            MembershipError = membershipError
                        };
                        return true;
                    }
                }
                finally
                {
                    FreeConsole();
                    TryRestoreHostConsole();
                }
            }
        }

        public static bool TryWriteLine(int processId, string command, out string error)
        {
            error = null;
            if (String.IsNullOrEmpty(command))
            {
                error = "Refusing to send an empty command.";
                return false;
            }

            if (command.IndexOf('\r') >= 0 || command.IndexOf('\n') >= 0)
            {
                error = "A command must be one line.";
                return false;
            }

            lock (ConsoleLock)
            {
                FreeConsole();
                if (!AttachConsole((uint)processId))
                {
                    error = Win32Error("AttachConsole", processId);
                    TryRestoreHostConsole();
                    return false;
                }

                try
                {
                    using (SafeFileHandle input = OpenConsoleDevice("CONIN$"))
                    {
                        if (input.IsInvalid)
                        {
                            error = Win32Error("CreateFile(CONIN$)", processId);
                            return false;
                        }

                        var records = new List<InputRecord>();
                        foreach (char character in command)
                        {
                            records.Add(CreateKeyRecord(true, character, 0));
                            records.Add(CreateKeyRecord(false, character, 0));
                        }

                        records.Add(CreateKeyRecord(true, '\r', VkReturn));
                        records.Add(CreateKeyRecord(false, '\r', VkReturn));

                        uint written;
                        InputRecord[] recordArray = records.ToArray();
                        if (!WriteConsoleInputW(input, recordArray, (uint)recordArray.Length, out written))
                        {
                            error = Win32Error("WriteConsoleInput", processId);
                            return false;
                        }

                        if (!IsCompleteInputWrite((uint)recordArray.Length, written))
                        {
                            error = "WriteConsoleInput accepted " + written + " of " + recordArray.Length + " records.";
                            return false;
                        }

                        return true;
                    }
                }
                finally
                {
                    FreeConsole();
                    TryRestoreHostConsole();
                }
            }
        }

        public static NativeWriteOutcome TryWriteLineVerified(
            ResolvedConsoleSession session,
            ProcessSessionIdentity expectedMatchedSession,
            string command,
            out string error)
        {
            error = null;
            if (session == null)
            {
                error = "send_blocked=null_console_session";
                return NativeWriteOutcome.NoInputCommitted;
            }

            if (expectedMatchedSession == null)
            {
                error = "send_blocked=null_target_session";
                return NativeWriteOutcome.NoInputCommitted;
            }

            if (!expectedMatchedSession.IsStrong)
            {
                error = "send_blocked=target_identity_unavailable";
                return NativeWriteOutcome.NoInputCommitted;
            }
            if (session.MatchedTargetSession == null || !session.MatchedTargetSession.Equals(expectedMatchedSession))
            {
                error = "send_blocked=session_identity_mismatch";
                return NativeWriteOutcome.NoInputCommitted;
            }

            if (String.IsNullOrEmpty(command))
            {
                error = "send_blocked=empty_command";
                return NativeWriteOutcome.NoInputCommitted;
            }

            if (command.IndexOf('\r') >= 0 || command.IndexOf('\n') >= 0)
            {
                error = "send_blocked=multiline_command";
                return NativeWriteOutcome.NoInputCommitted;
            }
            if (command.Length > 512)
            {
                error = "send_blocked=command_too_long";
                return NativeWriteOutcome.NoInputCommitted;
            }

            lock (ConsoleLock)
            {
                FreeConsole();
                if (!AttachConsole((uint)session.ResolvedAttachProcessId))
                {
                    error = "send_blocked=" + Win32Error("AttachConsole", session.ResolvedAttachProcessId);
                    TryRestoreHostConsole();
                    return NativeWriteOutcome.NoInputCommitted;
                }

                try
                {
                    ProcessSessionIdentity currentTarget = ProcessDiscovery.ResolveSessionIdentity(
                        expectedMatchedSession.ProcessId,
                        expectedMatchedSession.ProcessName);
                    if (!currentTarget.IsStrong)
                    {
                        error = "send_blocked=target_identity_unavailable";
                        return NativeWriteOutcome.NoInputCommitted;
                    }
                    if (!currentTarget.Equals(expectedMatchedSession))
                    {
                        error = "send_blocked=process_session_changed";
                        return NativeWriteOutcome.NoInputCommitted;
                    }

                    IList<int> attachedPids;
                    string memError;
                    if (!TryGetConsoleProcessList(out attachedPids, out memError))
                    {
                        error = "send_blocked=membership_unavailable: " + memError;
                        return NativeWriteOutcome.NoInputCommitted;
                    }

                    if (!attachedPids.Contains(expectedMatchedSession.ProcessId))
                    {
                        error = "send_blocked=target_not_in_console (PID " + expectedMatchedSession.ProcessId + " missing from attached console)";
                        return NativeWriteOutcome.NoInputCommitted;
                    }

                    IntPtr currentWindow = GetConsoleWindow();
                    if (session.WindowHandle != IntPtr.Zero && currentWindow != IntPtr.Zero && currentWindow != session.WindowHandle)
                    {
                        error = "send_blocked=console_changed (window 0x" + currentWindow.ToInt64().ToString("X") + " != expected 0x" + session.WindowHandle.ToInt64().ToString("X") + ")";
                        return NativeWriteOutcome.NoInputCommitted;
                    }

                    if (session.WindowHandle == IntPtr.Zero)
                    {
                        var currentIdentitySnapshot = new ConsoleSnapshot
                        {
                            WindowHandle = currentWindow,
                            ConsoleProcessIds = attachedPids
                        };
                        string currentConsoleId = ProcessDiscovery.ComputeStableConsoleId(
                            currentIdentitySnapshot,
                            session.ResolvedAttachProcessId);
                        if (!String.Equals(currentConsoleId, session.StableConsoleId, StringComparison.Ordinal))
                        {
                            error = "send_blocked=console_changed";
                            return NativeWriteOutcome.NoInputCommitted;
                        }
                    }

                    using (SafeFileHandle input = OpenConsoleDevice("CONIN$"))
                    {
                        if (input.IsInvalid)
                        {
                            error = "send_blocked=" + Win32Error("CreateFile(CONIN$)", session.ResolvedAttachProcessId);
                            return NativeWriteOutcome.NoInputCommitted;
                        }

                        var records = new List<InputRecord>();
                        bool commandAlreadyTyped = session.Snapshot != null && ContainsTypedCommand(
                            String.IsNullOrWhiteSpace(session.Snapshot.CursorLine) ? LastNonEmptyLine(session.Snapshot.Text) : session.Snapshot.CursorLine,
                            command);
                        if (!commandAlreadyTyped)
                        {
                            foreach (char character in command)
                            {
                                ushort virtualKey = VirtualKeyForCharacter(character);
                                records.Add(CreateKeyRecord(true, character, virtualKey));
                                records.Add(CreateKeyRecord(false, character, virtualKey));
                            }
                        }

                        records.Add(CreateKeyRecord(true, '\r', VkReturn));
                        records.Add(CreateKeyRecord(false, '\r', VkReturn));

                        ProcessSessionIdentity finalTarget = ProcessDiscovery.ResolveSessionIdentity(
                            expectedMatchedSession.ProcessId,
                            expectedMatchedSession.ProcessName);
                        if (!finalTarget.IsStrong || !finalTarget.Equals(expectedMatchedSession))
                        {
                            error = "send_blocked=process_session_changed";
                            return NativeWriteOutcome.NoInputCommitted;
                        }

                        uint written;
                        InputRecord[] recordArray = records.ToArray();
                        if (!WriteConsoleInputW(input, recordArray, (uint)recordArray.Length, out written))
                        {
                            error = "send_blocked=" + Win32Error("WriteConsoleInput", session.ResolvedAttachProcessId);
                            return NativeWriteOutcome.NoInputCommitted;
                        }

                        if (!IsCompleteInputWrite((uint)recordArray.Length, written))
                        {
                            // W2-002: a partial accepted write is ambiguous. A
                            // command prefix or an Enter-down may already be in the
                            // target buffer. This MUST NOT enter ordinary retry.
                            error = "send_blocked=partial_write (accepted " + written + " of " + recordArray.Length + " records)";
                            return NativeWriteOutcome.AmbiguousOrPartialInput;
                        }

                        // W2-002: a complete accepted write is committed. Whether
                        // the target visibly shows "Working" within a short window
                        // is diagnostic, not a retry condition -- re-dispatching a
                        // committed command risks concatenation/duplication.
                        return NativeWriteOutcome.CompleteInputCommitted;
                    }
                }
                finally
                {
                    FreeConsole();
                    TryRestoreHostConsole();
                }
            }
        }

        public static void Detach()
        {
            lock (ConsoleLock)
            {
                FreeConsole();
            }
        }

        public delegate bool ConsoleCtrlHandler(int controlType);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler handler, bool add);

        private static ConsoleCtrlHandler _activeCtrlHandler;

        /// <summary>
        /// Registers a raw Win32 console control handler. Deliberately avoids
        /// System.Console.CancelKeyPress: its internal ControlCHooker finalizer
        /// calls Unhook after FreeConsole and aborts process exit (0xE0434352).
        /// </summary>
        public static bool TrySetCtrlHandler(ConsoleCtrlHandler handler)
        {
            lock (ConsoleLock)
            {
                ConsoleCtrlHandler existing = _activeCtrlHandler;
                _activeCtrlHandler = null;
                if (existing != null)
                {
                    try { SetConsoleCtrlHandler(existing, false); } catch { }
                }
                if (handler == null)
                {
                    return false;
                }
                try
                {
                    if (!SetConsoleCtrlHandler(handler, true))
                    {
                        return false;
                    }
                    _activeCtrlHandler = handler;
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static void UnsetCtrlHandler()
        {
            lock (ConsoleLock)
            {
                ConsoleCtrlHandler handler = _activeCtrlHandler;
                _activeCtrlHandler = null;
                if (handler != null)
                {
                    try { SetConsoleCtrlHandler(handler, false); } catch { }
                }
            }
        }

        internal static bool IsCompleteInputWrite(uint expectedRecords, uint writtenRecords)
        {
            return expectedRecords > 0 && writtenRecords == expectedRecords;
        }

        private static bool IsReadyPrompt(string line)
        {
            string value = (line ?? String.Empty).Trim();
            return value == ">" || value == "› Ask Codex to do anything" || value.EndsWith("Ask anything...", StringComparison.Ordinal);
        }

        private static ushort VirtualKeyForCharacter(char character)
        {
            if (character >= 'a' && character <= 'z')
            {
                return (ushort)(character - 'a' + 'A');
            }
            if (character >= 'A' && character <= 'Z')
            {
                return character;
            }
            if (character >= '0' && character <= '9')
            {
                return character;
            }
            return 0;
        }

        private static bool ContainsWorkingMarker(string text)
        {
            return !String.IsNullOrEmpty(text) && text.IndexOf("Working (", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ContainsTypedCommand(string line, string command)
        {
            string value = (line ?? String.Empty).Trim();
            value = value.TrimStart('>', '›', '?').Trim();
            return String.Equals(value, command, StringComparison.Ordinal);
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

        private static SafeFileHandle OpenConsoleDevice(string name)
        {
            return CreateFileW(name, GenericRead | GenericWrite, ShareRead | ShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        }

        private static InputRecord CreateKeyRecord(bool down, char character, ushort virtualKey)
        {
            var record = new InputRecord();
            record.EventType = KeyEvent;
            record.KeyEvent = new KeyEventRecord
            {
                KeyDown = down,
                RepeatCount = 1,
                VirtualKeyCode = virtualKey,
                VirtualScanCode = virtualKey == 0 ? (ushort)0 : (ushort)MapVirtualKey(virtualKey, 0),
                UnicodeChar = character,
                ControlKeyState = 0
            };
            return record;
        }

        private static string Win32Error(string operation, int processId)
        {
            int code = Marshal.GetLastWin32Error();
            return operation + " failed for PID " + processId + ": " + new Win32Exception(code).Message + " (" + code + ")";
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Coord
        {
            public short X;
            public short Y;

            public Coord(short x, short y)
            {
                X = x;
                Y = y;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SmallRect
        {
            public short Left;
            public short Top;
            public short Right;
            public short Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ConsoleScreenBufferInfo
        {
            public Coord Size;
            public Coord CursorPosition;
            public ushort Attributes;
            public SmallRect Window;
            public Coord MaximumWindowSize;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct KeyEventRecord
        {
            [MarshalAs(UnmanagedType.Bool)]
            public bool KeyDown;
            public ushort RepeatCount;
            public ushort VirtualKeyCode;
            public ushort VirtualScanCode;
            public char UnicodeChar;
            public uint ControlKeyState;
        }

        [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
        private struct InputRecord
        {
            [FieldOffset(0)]
            public short EventType;
            [FieldOffset(4)]
            public KeyEventRecord KeyEvent;
        }

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint code, uint mapType);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();

        // CORE-002: ATTACH_PARENT_PROCESS sentinel. Re-attach to the parent
        // process's console (typically the cmd that launched SAICONT, or the
        // scheduled-task host) after each target-console operation so the
        // TUI's rendering, keyboard input and Ctrl handler keep working.
        private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFFu;

        // CORE-002: try to re-attach to the host console after a target-console
        // operation. Best-effort: if the host has no console (detached scheduled
        // task) or AttachConsole fails, the process remains console-less until
        // the next AttachConsole call. Either state is strictly better than the
        // previous FreeConsole-only behavior, which left the TUI's host console
        // destroyed for every poll.
        private static void TryRestoreHostConsole()
        {
            try
            {
                AttachConsole(ATTACH_PARENT_PROCESS);
            }
            catch
            {
                // best-effort; never throw from a restore path
            }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetConsoleTitleW(StringBuilder title, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetConsoleProcessList([Out] uint[] processList, uint processCount);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleScreenBufferInfo(SafeFileHandle consoleOutput, out ConsoleScreenBufferInfo info);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ReadConsoleOutputCharacterW(SafeFileHandle consoleOutput, [Out] StringBuilder character, uint length, Coord readCoord, out uint charsRead);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool WriteConsoleInputW(SafeFileHandle consoleInput, [In] InputRecord[] buffer, uint length, out uint eventsWritten);
    }
}
