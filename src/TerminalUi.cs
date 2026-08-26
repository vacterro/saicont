using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace SaiCont
{
    internal enum TuiMode
    {
        Idle,
        Probe,
        DryRun,
        Watch
    }

    internal enum TuiTab
    {
        Sessions = 0,
        LogStream = 1,
        Rules = 2,
        Help = 3
    }

    internal sealed class TuiLogEntry
    {
        public DateTime TimestampUtc;
        public string Level;
        public string Target;
        public int ProcessId;
        public string Message;
    }

    internal static class TerminalUi
    {
        private static readonly string[][] WordmarkLetters =
        {
            new[] { "#####", "  #  ", "  #  ", "  #  ", "  #  " },
            new[] { "#####", "#    ", "#### ", "#    ", "#####" },
            new[] { "#### ", "#   #", "#### ", "#  # ", "#   #" },
            new[] { "#   #", "## ##", "# # #", "#   #", "#   #" },
            new[] { "#####", "  #  ", "  #  ", "  #  ", "#####" },
            new[] { " ####", "#    ", " ### ", "    #", "#### " },
            new[] { " ### ", "#   #", "#####", "#   #", "#   #" },
            new[] { "#####", "  #  ", "  #  ", "  #  ", "#####" }
        };

        public static void PrintLandingPage()
        {
            bool interactive = !Console.IsOutputRedirected;
            ConsoleColor originalForeground = Console.ForegroundColor;
            ConsoleColor originalBackground = Console.BackgroundColor;

            try
            {
                if (interactive)
                {
                    Console.BackgroundColor = ConsoleColor.Black;
                    Console.Clear();
                }

                SetColor(interactive, ConsoleColor.DarkYellow);
                WriteCentered("+---------+");
                WriteCentered("/#########/|");
                WriteCentered("+---------+ |");
                WriteCentered("|   ###   | |");
                WriteCentered("|   ###   | +");
                WriteCentered("|   ###   |/");
                WriteCentered("+---------+");
                Console.WriteLine();
                foreach (string line in BuildWordmark())
                {
                    WriteCentered(line);
                }

                SetColor(interactive, ConsoleColor.Gray);
                WriteCentered("SAICONT / TERMINAL CONTINUITY");
                Console.WriteLine();
                WriteRule();

                SetColor(interactive, ConsoleColor.DarkCyan);
                Console.WriteLine("  START");
                SetColor(interactive, ConsoleColor.Gray);
                Console.WriteLine("  --gui        launch interactive Win95 Dark Golden TUI dashboard");
                Console.WriteLine("  --terminal   open the SAICONT TERMINAL monitor and dispatcher adapter");
                Console.WriteLine("  --probe      inspect Cline/Codex; never send input");
                Console.WriteLine("  --dry-run    watch continuously; never send input");
                Console.WriteLine("  --watch      run guarded continuation");
                Console.WriteLine("  --self-test  run deterministic checks");
                Console.WriteLine();

                SetColor(interactive, ConsoleColor.DarkCyan);
                Console.WriteLine("  SAIPEN BASICS");
                SetColor(interactive, ConsoleColor.Gray);
                Console.WriteLine("  cc  continue    gg <goal>  new goal");
                Console.WriteLine("  ss  stop        sss        status");
                Console.WriteLine();

                SetColor(interactive, ConsoleColor.DarkCyan);
                Console.WriteLine("  CLINE BASICS");
                SetColor(interactive, ConsoleColor.Gray);
                Console.WriteLine("  Enter  submit       Esc     abort/close menu");
                Console.WriteLine("  Ctrl+C clear/exit   Ctrl+L  clear conversation");
                WriteRule();
                SetColor(interactive, ConsoleColor.DarkGreen);
                Console.WriteLine("  Safe first run: SAICONT.exe --gui or SAICONT.exe --probe");
            }
            finally
            {
                if (interactive)
                {
                    Console.ForegroundColor = originalForeground;
                    Console.BackgroundColor = originalBackground;
                }
            }
        }

        public static int RunInteractiveTui(WatcherConfiguration configuration, string configPath, string initialModeName = null)
        {
            ConsoleColor origFg = ConsoleColor.Gray;
            ConsoleColor origBg = ConsoleColor.Black;
            try
            {
                origFg = Console.ForegroundColor;
                origBg = Console.BackgroundColor;
            }
            catch { }

            bool origCursorVisible = true;
            try
            {
                if (!Console.IsOutputRedirected)
                {
                    Console.Title = "SAICONT TERMINAL";
                }
            }
            catch { }
            try { origCursorVisible = Console.CursorVisible; } catch { }

            bool interruptRequested = false;
                        NativeConsole.ConsoleCtrlHandler interruptHandler = delegate(int controlType)
            {
                interruptRequested = true;
                return true;
            };
            TuiMode mode = TuiMode.Idle;
            if (String.Equals(initialModeName, "--watch", StringComparison.OrdinalIgnoreCase))
            {
                mode = TuiMode.Watch;
            }
            else if (String.Equals(initialModeName, "--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                mode = TuiMode.DryRun;
            }
            else if (String.Equals(initialModeName, "--probe", StringComparison.OrdinalIgnoreCase))
            {
                mode = TuiMode.Probe;
            }

            TuiTab activeTab = TuiTab.Sessions;
            string statusMessage = "Ready. [P] Probe  [D] Dry-Run  [W] Watch  [1-4] Tabs  [Enter] Inspect  [R] Reload  [Q] Quit";
            bool confirmWatch = false;
            bool inspectorOpen = false;
            int selectedSessionIndex = 0;
            int selectedRuleIndex = 0;
            int logScrollOffset = 0;

            var logs = new List<TuiLogEntry>();
            var latestSessions = new List<PollResult>();
            var engine = new WatcherEngine(configuration);
            int pollCounter = 0;
            DateTime lastPollTime = DateTime.MinValue;

            try
            {
                try
                {
                    Console.BackgroundColor = ConsoleColor.Black;
                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.Clear();
                }
                catch { }
                try { Console.CursorVisible = false; } catch { }

                try
                {
                    latestSessions = new List<PollResult>(engine.PollOnce(false));
                    pollCounter++;
                    lastPollTime = DateTime.UtcNow;
                    AddLog(logs, "INFO", "system", 0, "SAICONT TUI started with " + configuration.Targets.Count + " target rules.");
                    foreach (PollResult r in latestSessions)
                    {
                        AddLogFromPoll(logs, r);
                    }
                }
                catch (Exception startupException)
                {
                    AddLog(logs, "ERROR", "system", 0, "Initial probe failed: " + startupException.Message);
                }

                bool running = true;
            NativeConsole.TrySetCtrlHandler(interruptHandler);
                while (running)
                {
                    if (interruptRequested)
                    {
                        AddLog(logs, "INFO", "system", 0, "Console interrupt received; closing terminal adapter.");
                        break;
                    }

                    DateTime now = DateTime.UtcNow;

                    if (mode == TuiMode.Watch || mode == TuiMode.DryRun)
                    {
                        int interval = Math.Max(500, configuration.PollIntervalMilliseconds);
                        if ((now - lastPollTime).TotalMilliseconds >= interval)
                        {
                            bool allowInput = (mode == TuiMode.Watch);
                            IList<PollResult> results;
                            try
                            {
                                results = engine.PollOnce(allowInput);
                            }
                            catch (Exception pollException)
                            {
                                AddLog(logs, "ERROR", "system", 0, "Poll failed: " + pollException.Message);
                                results = new List<PollResult>();
                            }
                            latestSessions = new List<PollResult>(results);
                            if (results.Count > 0)
                            {
                                foreach (PollResult r in results)
                                {
                                    AddLogFromPoll(logs, r);
                                }
                            }
                        }
                    }

                    RenderTui(
                        configuration,
                        configPath,
                        mode,
                        activeTab,
                        latestSessions,
                        logs,
                        statusMessage,
                        confirmWatch,
                        inspectorOpen,
                        selectedSessionIndex,
                        selectedRuleIndex,
                        logScrollOffset,
                        pollCounter,
                        lastPollTime);

                    int sleepRemaining = 100;
                    while (sleepRemaining > 0)
                    {
                        bool hasKey = false;
                        try { hasKey = Console.KeyAvailable; } catch { }

                        if (hasKey)
                        {
                            ConsoleKeyInfo key;
                            try { key = Console.ReadKey(true); } catch { break; }

                            if (inspectorOpen)
                            {
                                if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.Enter || key.Key == ConsoleKey.Q || key.Key == ConsoleKey.Spacebar)
                                {
                                    inspectorOpen = false;
                                    statusMessage = "Closed session inspector.";
                                }
                                else if (key.Key == ConsoleKey.UpArrow && selectedSessionIndex > 0)
                                {
                                    selectedSessionIndex--;
                                }
                                else if (key.Key == ConsoleKey.DownArrow && selectedSessionIndex < latestSessions.Count - 1)
                                {
                                    selectedSessionIndex++;
                                }
                                break;
                            }

                            if (confirmWatch)
                            {
                                if (key.Key == ConsoleKey.Y)
                                {
                                    confirmWatch = false;
                                    mode = TuiMode.Watch;
                                    statusMessage = "WATCHING ACTIVE: Guarded automated continuation is LIVE.";
                                    AddLog(logs, "WARN", "operator", 0, "Switched to live WATCH mode.");
                                }
                                else
                                {
                                    confirmWatch = false;
                                    statusMessage = "Live watch activation canceled.";
                                }
                            }
                            else
                            {
                                switch (key.Key)
                                {
                                    case ConsoleKey.Q:
                                    case ConsoleKey.Escape:
                                        running = false;
                                        break;

                                    case ConsoleKey.Tab:
                                        activeTab = (TuiTab)(((int)activeTab + 1) % 4);
                                        statusMessage = "Switched to Tab " + ((int)activeTab + 1) + ": " + activeTab;
                                        break;

                                    case ConsoleKey.LeftArrow:
                                        if (activeTab > 0) activeTab--;
                                        else activeTab = TuiTab.Help;
                                        statusMessage = "Switched to Tab: " + activeTab;
                                        break;

                                    case ConsoleKey.RightArrow:
                                        if (activeTab < TuiTab.Help) activeTab++;
                                        else activeTab = TuiTab.Sessions;
                                        statusMessage = "Switched to Tab: " + activeTab;
                                        break;

                                    case ConsoleKey.D1:
                                    case ConsoleKey.F1:
                                        activeTab = TuiTab.Sessions;
                                        statusMessage = "View: Live Sessions Dashboard [Arrow keys to select, Enter to inspect]";
                                        break;

                                    case ConsoleKey.D2:
                                    case ConsoleKey.F2:
                                        activeTab = TuiTab.LogStream;
                                        statusMessage = "View: Real-Time Event & Activity Log [PageUp/Down to scroll]";
                                        break;

                                    case ConsoleKey.D3:
                                    case ConsoleKey.F3:
                                        activeTab = TuiTab.Rules;
                                        statusMessage = "View: Target Rules & Configuration [R to reload config]";
                                        break;

                                    case ConsoleKey.D4:
                                    case ConsoleKey.F4:
                                        activeTab = TuiTab.Help;
                                        statusMessage = "View: Quick Reference & Safety Architecture";
                                        break;

                                    case ConsoleKey.UpArrow:
                                        if (activeTab == TuiTab.Sessions && selectedSessionIndex > 0)
                                        {
                                            selectedSessionIndex--;
                                        }
                                        else if (activeTab == TuiTab.Rules && selectedRuleIndex > 0)
                                        {
                                            selectedRuleIndex--;
                                        }
                                        else if (activeTab == TuiTab.LogStream)
                                        {
                                            logScrollOffset++;
                                        }
                                        break;

                                    case ConsoleKey.DownArrow:
                                        if (activeTab == TuiTab.Sessions && selectedSessionIndex < latestSessions.Count - 1)
                                        {
                                            selectedSessionIndex++;
                                        }
                                        else if (activeTab == TuiTab.Rules && selectedRuleIndex < configuration.Targets.Count - 1)
                                        {
                                            selectedRuleIndex++;
                                        }
                                        else if (activeTab == TuiTab.LogStream && logScrollOffset > 0)
                                        {
                                            logScrollOffset--;
                                        }
                                        break;

                                    case ConsoleKey.PageUp:
                                        if (activeTab == TuiTab.LogStream) logScrollOffset += 10;
                                        break;

                                    case ConsoleKey.PageDown:
                                        if (activeTab == TuiTab.LogStream) logScrollOffset = Math.Max(0, logScrollOffset - 10);
                                        break;

                                    case ConsoleKey.Home:
                                        if (activeTab == TuiTab.LogStream) logScrollOffset = Math.Max(0, logs.Count - 5);
                                        else if (activeTab == TuiTab.Sessions) selectedSessionIndex = 0;
                                        break;

                                    case ConsoleKey.End:
                                        if (activeTab == TuiTab.LogStream) logScrollOffset = 0;
                                        else if (activeTab == TuiTab.Sessions && latestSessions.Count > 0) selectedSessionIndex = latestSessions.Count - 1;
                                        break;

                                    case ConsoleKey.Enter:
                                    case ConsoleKey.Spacebar:
                                        if (activeTab == TuiTab.Sessions && latestSessions.Count > 0 && selectedSessionIndex < latestSessions.Count)
                                        {
                                            inspectorOpen = true;
                                            statusMessage = "Session Inspector: Press [Esc] or [Enter] to close.";
                                        }
                                        break;

                                    case ConsoleKey.P:
                                        statusMessage = "Executing single PROBE pass...";
                                        latestSessions = new List<PollResult>(engine.PollOnce(false));
                                        pollCounter++;
                                        lastPollTime = DateTime.UtcNow;
                                        if (selectedSessionIndex >= latestSessions.Count) selectedSessionIndex = Math.Max(0, latestSessions.Count - 1);
                                        statusMessage = "Probe complete: " + latestSessions.Count + " sessions evaluated.";
                                        foreach (PollResult r in latestSessions)
                                        {
                                            AddLogFromPoll(logs, r);
                                        }
                                        break;

                                    case ConsoleKey.D:
                                        if (mode == TuiMode.DryRun)
                                        {
                                            mode = TuiMode.Idle;
                                            statusMessage = "Dry-run stopped. In IDLE mode.";
                                            AddLog(logs, "INFO", "operator", 0, "Dry-run stopped.");
                                        }
                                        else
                                        {
                                            mode = TuiMode.DryRun;
                                            statusMessage = "DRY-RUN ACTIVE: Continuous poll without input injection.";
                                            AddLog(logs, "INFO", "operator", 0, "Started continuous dry-run.");
                                        }
                                        break;

                                    case ConsoleKey.W:
                                        if (mode == TuiMode.Watch)
                                        {
                                            mode = TuiMode.Idle;
                                            statusMessage = "Watch mode paused. In IDLE mode.";
                                            AddLog(logs, "INFO", "operator", 0, "Watch mode stopped.");
                                        }
                                        else
                                        {
                                            confirmWatch = true;
                                            statusMessage = "CAUTION: Enable automated continuation? Press [Y] to confirm, [N] to cancel.";
                                        }
                                        break;

                                    case ConsoleKey.S:
                                        mode = TuiMode.Idle;
                                        confirmWatch = false;
                                        statusMessage = "Stopped. In IDLE mode.";
                                        AddLog(logs, "INFO", "operator", 0, "Monitoring stopped.");
                                        break;

                                    case ConsoleKey.R:
                                        try
                                        {
                                            configuration = WatcherConfiguration.Load(configPath);
                                            engine = new WatcherEngine(configuration);
                                            statusMessage = "Config reloaded successfully (" + configuration.Targets.Count + " targets).";
                                            AddLog(logs, "INFO", "config", 0, "Configuration reloaded from " + Path.GetFileName(configPath));
                                        }
                                        catch (Exception ex)
                                        {
                                            statusMessage = "Config reload failed: " + ex.Message;
                                            AddLog(logs, "ERROR", "config", 0, "Reload error: " + ex.Message);
                                        }
                                        break;

                                    case ConsoleKey.T:
                                        if (latestSessions.Count > 0 && selectedSessionIndex < latestSessions.Count)
                                        {
                                            PollResult sel = latestSessions[selectedSessionIndex];
                                            statusMessage = "Test evaluation for PID " + sel.ProcessId + ": Ready=" + sel.Ready + ", Trigger=" + sel.Triggered + ", Busy=" + sel.Busy;
                                            AddLog(logs, "INFO", "eval-test", sel.ProcessId, "Manual evaluation: " + (sel.Reason ?? "none"));
                                        }
                                        else
                                        {
                                            statusMessage = "No session selected to test.";
                                        }
                                        break;

                                    case ConsoleKey.C:
                                        logs.Clear();
                                        logScrollOffset = 0;
                                        statusMessage = "Log stream buffer cleared.";
                                        break;

                                    case ConsoleKey.H:
                                        activeTab = TuiTab.Help;
                                        statusMessage = "View: Quick Reference & Safety Architecture";
                                        break;
                                }
                            }
                            break;
                        }

                        Thread.Sleep(20);
                        sleepRemaining -= 20;
                    }
                }
            }
            finally
            {
                NativeConsole.UnsetCtrlHandler();
                try { Console.CursorVisible = origCursorVisible; } catch { }
                try { Console.ForegroundColor = origFg; } catch { }
                try { Console.BackgroundColor = origBg; } catch { }
                try { Console.Clear(); } catch { }
            }

            return 0;
        }

        private static void RenderTui(
            WatcherConfiguration configuration,
            string configPath,
            TuiMode mode,
            TuiTab activeTab,
            IList<PollResult> sessions,
            IList<TuiLogEntry> logs,
            string statusMessage,
            bool confirmWatch,
            bool inspectorOpen,
            int selectedSessionIndex,
            int selectedRuleIndex,
            int logScrollOffset,
            int pollCounter,
            DateTime lastPollTime)
        {
            try
            {
                int width = 80;
                int height = 25;
                try
                {
                    width = Math.Max(70, Console.WindowWidth);
                    height = Math.Max(20, Console.WindowHeight);
                }
                catch
                {
                }

                try { Console.SetCursorPosition(0, 0); } catch { }

                WriteWin95Header(width, mode, pollCounter, lastPollTime);
                WriteTabBar(width, activeTab);

                int contentHeight = Math.Max(8, height - 8);

                if (inspectorOpen && sessions != null && sessions.Count > 0 && selectedSessionIndex < sessions.Count)
                {
                    RenderInspectorModal(width, contentHeight, sessions[selectedSessionIndex]);
                }
                else
                {
                    switch (activeTab)
                    {
                        case TuiTab.Sessions:
                            RenderSessionsTab(width, contentHeight, sessions, selectedSessionIndex);
                            break;
                        case TuiTab.LogStream:
                            RenderLogStreamTab(width, contentHeight, logs, logScrollOffset);
                            break;
                        case TuiTab.Rules:
                            RenderRulesTab(width, contentHeight, configuration, configPath, selectedRuleIndex);
                            break;
                        case TuiTab.Help:
                            RenderHelpTab(width, contentHeight);
                            break;
                    }
                }

                WriteWin95Footer(width, mode, statusMessage, confirmWatch, inspectorOpen);
            }
            catch (Exception ex)
            {
                AddLog(logs, "ERROR", "render", 0, ex.Message);
            }
        }

        private static void WriteWin95Header(int width, TuiMode mode, int pollCounter, DateTime lastPollTime)
        {
            Console.BackgroundColor = ConsoleColor.DarkYellow;
            Console.ForegroundColor = ConsoleColor.Black;

            string title = " SAICONT v1.0.0 [TERMINAL CONTINUITY DASHBOARD] ";
            string modeStr;
            ConsoleColor modeBg;
            switch (mode)
            {
                case TuiMode.Watch:
                    modeStr = " [● ON: WATCHING (LIVE INJECTION)] ";
                    modeBg = ConsoleColor.DarkRed;
                    break;
                case TuiMode.DryRun:
                    modeStr = " [👁 ON: DRY-RUN (DISCOVERY ONLY)] ";
                    modeBg = ConsoleColor.DarkGreen;
                    break;
                default:
                    modeStr = " [⏸ PAUSED / IDLE] ";
                    modeBg = ConsoleColor.DarkGray;
                    break;
            }

            string pollsStr = "Polls: " + pollCounter + " | " + (lastPollTime == DateTime.MinValue ? "--:--:--" : lastPollTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)) + " UTC ";

            int availableSpace = Math.Max(0, (width - 1) - title.Length - modeStr.Length - pollsStr.Length);

            Console.Write(title);
            Console.BackgroundColor = modeBg;
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write(modeStr);

            Console.BackgroundColor = ConsoleColor.DarkYellow;
            Console.ForegroundColor = ConsoleColor.Black;
            Console.Write(new string(' ', availableSpace));
            Console.WriteLine(SafeClip(pollsStr, width - 1));
        }

        private static void WriteTabBar(int width, TuiTab activeTab)
        {
            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(" ");

            WriteTabItem("1: Sessions", activeTab == TuiTab.Sessions);
            Console.Write(" ");
            WriteTabItem("2: Log Stream", activeTab == TuiTab.LogStream);
            Console.Write(" ");
            WriteTabItem("3: Target Rules", activeTab == TuiTab.Rules);
            Console.Write(" ");
            WriteTabItem("4: Help & Ref", activeTab == TuiTab.Help);

            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            int used = 2 + 13 + 1 + 15 + 1 + 17 + 1 + 15;
            int pad = Math.Max(0, (width - 1) - used);
            Console.WriteLine(new string(' ', pad));
        }

        private static void WriteTabItem(string label, bool isActive)
        {
            if (isActive)
            {
                Console.BackgroundColor = ConsoleColor.DarkCyan;
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write(" [ " + label + " ] ");
            }
            else
            {
                Console.BackgroundColor = ConsoleColor.DarkGray;
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.Write("   " + label + "   ");
            }
        }

        private static void RenderSessionsTab(int width, int maxLines, IList<PollResult> sessions, int selectedIndex)
        {
            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine(" " + BoxLine("DISCOVERED TERMINAL SESSIONS (UP/DOWN TO SELECT, ENTER TO INSPECT)", width - 3));

            Console.BackgroundColor = ConsoleColor.DarkGray;
            Console.ForegroundColor = ConsoleColor.White;
            string hdr = String.Format(
                CultureInfo.InvariantCulture,
                " {0,-2} {1,-14} {2,-8} {3,-7} {4,-18} {5,-7} {6,-10} {7}",
                "  ", "RULE", "PROCESS", "PID", "TITLE", "STATUS", "PROMPT", "DECISION / REASON");
            if (hdr.Length > width - 1) hdr = hdr.Substring(0, width - 1);
            else if (hdr.Length < width - 1) hdr = hdr.PadRight(width - 1);
            Console.WriteLine(hdr);

            int linesPrinted = 0;

            if (sessions == null || sessions.Count == 0)
            {
                Console.BackgroundColor = ConsoleColor.Black;
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("   (No matching active console sessions discovered. Press [P] to Probe)");
                linesPrinted++;
            }
            else
            {
                for (int i = 0; i < sessions.Count; i++)
                {
                    if (linesPrinted >= maxLines - 2) break;

                    PollResult s = sessions[i];
                    bool isSelected = (i == selectedIndex);

                    Console.BackgroundColor = isSelected ? ConsoleColor.DarkCyan : ConsoleColor.Black;

                    string cursor = isSelected ? "> " : "  ";
                    string title = String.IsNullOrEmpty(s.Title) ? "-" : Truncate(s.Title, 16);
                    string status = s.Read ? "READ" : "FAIL";
                    string prompt = s.Busy ? "BUSY" : (s.Ready ? "READY" : (s.Triggered ? "TRIGGER" : "IDLE"));
                    string reason = s.Reason ?? "-";

                    ConsoleColor promptColor = isSelected ? ConsoleColor.White : (s.Busy ? ConsoleColor.DarkRed : (s.Ready ? ConsoleColor.DarkGreen : (s.Triggered ? ConsoleColor.DarkYellow : ConsoleColor.Gray)));

                    string line = String.Format(
                        CultureInfo.InvariantCulture,
                        " {0}{1,-14} {2,-8} {3,-7} {4,-18} ",
                        cursor,
                        Truncate(s.Target, 14),
                        Truncate(s.ProcessName, 8),
                        s.ProcessId,
                        title);

                    Console.ForegroundColor = isSelected ? ConsoleColor.White : ConsoleColor.Gray;
                    Console.Write(line);
                    Console.ForegroundColor = isSelected ? ConsoleColor.White : (s.Read ? ConsoleColor.Green : ConsoleColor.Red);
                    Console.Write(String.Format(CultureInfo.InvariantCulture, "{0,-7} ", status));
                    Console.ForegroundColor = promptColor;
                    Console.Write(String.Format(CultureInfo.InvariantCulture, "{0,-10} ", prompt));
                    Console.ForegroundColor = isSelected ? ConsoleColor.White : ConsoleColor.Gray;
                    int padLen = Math.Max(0, (width - 1) - 72);
                    Console.WriteLine(Truncate(reason, padLen).PadRight(padLen));
                    linesPrinted++;
                }
            }

            Console.BackgroundColor = ConsoleColor.Black;
            while (linesPrinted < maxLines)
            {
                Console.WriteLine(new string(' ', Math.Max(0, width - 1)));
                linesPrinted++;
            }
        }

        private static void RenderInspectorModal(int width, int maxLines, PollResult session)
        {
            Console.BackgroundColor = ConsoleColor.DarkYellow;
            Console.ForegroundColor = ConsoleColor.Black;
            string titleBar = " ===[ SESSION DETAIL INSPECTOR: PID " + session.ProcessId + " (" + (session.ProcessName ?? "unknown") + ") ]===";
            if (titleBar.Length > width - 1) titleBar = titleBar.Substring(0, width - 1);
            else titleBar = titleBar.PadRight(width - 1);
            Console.WriteLine(titleBar);

            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.Gray;

            string nextStr = session.NextAttemptUtc == DateTime.MinValue ? "none" : session.NextAttemptUtc.ToString("o", CultureInfo.InvariantCulture);

            string[] detailLines = new[]
            {
                "  Target Rule:       " + (session.Target ?? "-"),
                "  Process ID:        " + session.ProcessId + " (Name: " + (session.ProcessName ?? "-") + ")",
                "  Attach Process ID: " + session.AttachProcessId + " | Title: \"" + (session.Title ?? "-") + "\"",
                "  Console Read:      " + (session.Read ? "SUCCESS (READ)" : "FAILED (Unreadable)"),
                "  Prompt Status:     " + (session.Busy ? "BUSY (Generating or typed input)" : (session.Ready ? "READY (Empty prompt, ready to submit)" : "UNKNOWN")),
                "  Trigger State:     " + (session.Triggered ? "TRIGGERED (Active failure pattern matched)" : "NO_TRIGGER"),
                "  Transaction Send:  WouldSend=" + session.WouldSend + " | Sent=" + session.Sent,
                "  Retry Schedule:    NextAttempt=" + nextStr,
                "  Decision / Reason: " + (session.Reason ?? "-"),
                "  Error Detail:      " + (session.Error ?? "none"),
                "",
                "  [Esc] or [Enter] to return to Dashboard | [Up/Down] to inspect next session"
            };

            int linesPrinted = 0;
            foreach (string dl in detailLines)
            {
                if (linesPrinted >= maxLines) break;
                if (dl.Contains("SUCCESS") || dl.Contains("READY")) Console.ForegroundColor = ConsoleColor.Green;
                else if (dl.Contains("FAILED") || dl.Contains("BUSY")) Console.ForegroundColor = ConsoleColor.Red;
                else if (dl.Contains("TRIGGERED")) Console.ForegroundColor = ConsoleColor.Yellow;
                else if (dl.Contains("return to Dashboard")) Console.ForegroundColor = ConsoleColor.DarkCyan;
                else Console.ForegroundColor = ConsoleColor.Gray;

                Console.WriteLine(Truncate(dl, width - 1).PadRight(Math.Max(0, width - 1)));
                linesPrinted++;
            }

            Console.BackgroundColor = ConsoleColor.Black;
            while (linesPrinted < maxLines)
            {
                Console.WriteLine(new string(' ', Math.Max(0, width - 1)));
                linesPrinted++;
            }
        }

        private static void RenderLogStreamTab(int width, int maxLines, IList<TuiLogEntry> logs, int scrollOffset)
        {
            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            string header = " " + BoxLine("REAL-TIME EVENT STREAM (PAGEUP/DOWN TO SCROLL, C TO CLEAR)", width - 3);
            Console.WriteLine(header);

            int linesPrinted = 0;
            int visibleCount = maxLines - 1;
            int totalLogs = logs.Count;

            int endIdx = totalLogs - scrollOffset;
            if (endIdx > totalLogs) endIdx = totalLogs;
            int startIdx = Math.Max(0, endIdx - visibleCount);

            if (logs.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("   (No operational log entries recorded yet)");
                linesPrinted++;
            }
            else
            {
                for (int i = startIdx; i < endIdx && linesPrinted < visibleCount; i++)
                {
                    TuiLogEntry entry = logs[i];
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write(" " + entry.TimestampUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " ");

                    if (entry.Level == "ERROR") Console.ForegroundColor = ConsoleColor.Red;
                    else if (entry.Level == "WARN") Console.ForegroundColor = ConsoleColor.Yellow;
                    else Console.ForegroundColor = ConsoleColor.DarkCyan;
                    Console.Write(String.Format(CultureInfo.InvariantCulture, "[{0,-5}] ", entry.Level));

                    Console.ForegroundColor = ConsoleColor.Gray;
                    string msg = entry.Message ?? String.Empty;
                    int maxMsgWidth = Math.Max(10, (width - 1) - 20);
                    Console.WriteLine(Truncate(msg, maxMsgWidth).PadRight(maxMsgWidth));
                    linesPrinted++;
                }
            }

            while (linesPrinted < maxLines)
            {
                Console.WriteLine(new string(' ', Math.Max(0, width - 1)));
                linesPrinted++;
            }
        }

        private static void RenderRulesTab(int width, int maxLines, WatcherConfiguration config, string configPath, int selectedRuleIndex)
        {
            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine(" " + BoxLine("ACTIVE WATCHER CONFIGURATION (" + Path.GetFileName(configPath) + ") [R TO RELOAD]", width - 3));

            int linesPrinted = 0;
            Console.ForegroundColor = ConsoleColor.Gray;
            string pollInfo = "  Poll Interval: " + config.PollIntervalMilliseconds + "ms | Log File: " + config.LogFilePath;
            Console.WriteLine(Truncate(pollInfo, width - 1));
            linesPrinted++;

            Console.BackgroundColor = ConsoleColor.DarkGray;
            Console.ForegroundColor = ConsoleColor.White;
            string hdr = String.Format(
                CultureInfo.InvariantCulture,
                " {0,-2} {1,-18} {2,-8} {3,-16} {4,-8} {5,-12} {6}",
                "  ", "RULE NAME", "ENABLED", "PROCESSES", "CMD", "RETRY/BACKOFF", "TRIGGERS");
            if (hdr.Length > width - 1) hdr = hdr.Substring(0, width - 1);
            else if (hdr.Length < width - 1) hdr = hdr.PadRight(width - 1);
            Console.WriteLine(hdr);
            linesPrinted++;

            for (int i = 0; i < config.Targets.Count; i++)
            {
                if (linesPrinted >= maxLines) break;
                TargetRule rule = config.Targets[i];
                bool isSelected = (i == selectedRuleIndex);

                Console.BackgroundColor = isSelected ? ConsoleColor.DarkCyan : ConsoleColor.Black;
                Console.ForegroundColor = isSelected ? ConsoleColor.White : (rule.Enabled ? ConsoleColor.Green : ConsoleColor.DarkGray);

                string cursor = isSelected ? "> " : "  ";
                string procs = String.Join(",", rule.ProcessNames);
                string retryInfo = rule.InitialDelaySeconds + "s/" + rule.BackoffMultiplier + "x (max " + rule.MaximumRetryIntervalSeconds + "s)";
                string line = String.Format(
                    CultureInfo.InvariantCulture,
                    " {0}{1,-18} {2,-8} {3,-16} {4,-8} {5,-12} {6} triggers",
                    cursor,
                    Truncate(rule.Name, 18),
                    rule.Enabled ? "TRUE" : "FALSE",
                    Truncate(procs, 16),
                    Truncate(rule.Command, 8),
                    Truncate(retryInfo, 12),
                    rule.TriggerPatterns.Length);
                Console.WriteLine(Truncate(line, width - 1).PadRight(Math.Max(0, width - 1)));
                linesPrinted++;
            }

            Console.BackgroundColor = ConsoleColor.Black;
            while (linesPrinted < maxLines)
            {
                Console.WriteLine(new string(' ', Math.Max(0, width - 1)));
                linesPrinted++;
            }
        }

        private static void RenderHelpTab(int width, int maxLines)
        {
            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine(" " + BoxLine("SAICONT TERMINAL GUI QUICK REFERENCE & SAFETY GUARANTEES", width - 3));

            string[] helpLines = new[]
            {
                " NAVIGATION & CONTROLS:",
                "   [1] - [4] / [Tab]  Switch Tabs       - [1] Sessions  [2] Logs  [3] Rules  [4] Help",
                "   [Up] / [Down]      Navigate List     - Select terminal session or scroll log stream.",
                "   [Enter] / [Space]  Session Inspector - Open deep diagnostic modal for selected session.",
                "   [P]                Single Probe      - Run live discovery pass on console process tree.",
                "   [D]                Toggle Dry-Run    - Continuously inspect consoles without injecting input.",
                "   [W]                Toggle Watch      - Enable live automated continuation (guarded with [Y/N]).",
                "   [S]                Stop / Pause      - Stop active continuous monitoring and return to Idle.",
                "   [R]                Reload Config     - Hot-reload SAICONT.config.xml without restarting.",
                "   [T]                Test Evaluation   - Inspect trigger/ready/busy logic on selected session.",
                "   [C]                Clear Logs        - Flush the in-memory TUI log view buffer.",
                "   [Q] / [Esc]        Quit              - Safely exit the dashboard (restores console state).",
                "",
                " SAFETY ARCHITECTURE:",
                "   * Zero Focus Steal: Uses Win32 AttachConsole + WriteConsoleInputW; never activates windows.",
                "   * Transactional Send: Re-verifies console window and process start time right before sending.",
                "   * Exponential Backoff: Hard caps on attempts (5) and retry intervals (up to 3600s).",
                "   * Durable State: Retry countdowns and suppressed events persist across runs in SAICONT.state.xml."
            };

            int linesPrinted = 0;
            Console.ForegroundColor = ConsoleColor.Gray;
            foreach (string hl in helpLines)
            {
                if (linesPrinted >= maxLines) break;
                Console.WriteLine(Truncate(hl, width - 1).PadRight(Math.Max(0, width - 1)));
                linesPrinted++;
            }

            while (linesPrinted < maxLines)
            {
                Console.WriteLine(new string(' ', Math.Max(0, width - 1)));
                linesPrinted++;
            }
        }

        private static void WriteWin95Footer(int width, TuiMode mode, string statusMessage, bool confirmWatch, bool inspectorOpen)
        {
            Console.BackgroundColor = confirmWatch ? ConsoleColor.DarkRed : ConsoleColor.DarkCyan;
            Console.ForegroundColor = ConsoleColor.White;
            string status = " " + (statusMessage ?? String.Empty);
            if (status.Length >= width) status = status.Substring(0, width - 1);
            else status = status.PadRight(width - 1);
            Console.WriteLine(status);

            Console.BackgroundColor = ConsoleColor.DarkGray;
            Console.ForegroundColor = ConsoleColor.Black;

            string keyBar = inspectorOpen
                ? " [Esc/Enter] Close Inspector  [Up/Down] Select Session  [Q] Quit "
                : " [P] Probe  [D] Dry-Run  [W] Watch  [Enter] Inspect  [R] Reload  [1-4] Tabs  [Q] Quit ";

            if (keyBar.Length >= width) keyBar = keyBar.Substring(0, width - 1);
            else keyBar = keyBar.PadRight(width - 1);
            Console.Write(keyBar);
        }

        private static void AddLog(IList<TuiLogEntry> logs, string level, string target, int pid, string message)
        {
            if (logs == null) return;
            if (logs.Count > 500) logs.RemoveAt(0);
            logs.Add(new TuiLogEntry
            {
                TimestampUtc = DateTime.UtcNow,
                Level = level,
                Target = target,
                ProcessId = pid,
                Message = message
            });
        }

        private static void AddLogFromPoll(IList<TuiLogEntry> logs, PollResult result)
        {
            if (result == null) return;
            string level = !String.IsNullOrEmpty(result.Error) ? "ERROR" : (result.Triggered ? "WARN" : "INFO");
            string msg = FormatPollResult(result);
            AddLog(logs, level, result.Target ?? "target", result.ProcessId, msg);
        }

        private static string BoxLine(string title, int length)
        {
            if (String.IsNullOrEmpty(title)) return new string('=', Math.Max(0, length));
            int dashes = length - title.Length - 4;
            if (dashes < 2) return "== " + title + " ==";
            return "==[ " + title + " ]" + new string('=', dashes);
        }

        private static string Truncate(string val, int max)
        {
            if (String.IsNullOrEmpty(val)) return "-";
            if (max <= 0) return String.Empty;
            if (val.Length <= max) return val;
            if (max <= 2) return val.Substring(0, max);
            return val.Substring(0, max - 2) + "..";
        }

        private static string SafeClip(string val, int max)
        {
            if (String.IsNullOrEmpty(val)) return String.Empty;
            if (max <= 0) return String.Empty;
            return val.Length <= max ? val : val.Substring(0, max);
        }

        private static void WriteCentered(string value)
        {
            int width = 64;
            try
            {
                width = Math.Max(1, Console.WindowWidth);
            }
            catch (IOException)
            {
            }

            int padding = Math.Max(0, (width - value.Length) / 2);
            Console.WriteLine(new string(' ', padding) + value);
        }

        private static string[] BuildWordmark()
        {
            var lines = new string[5];
            for (int row = 0; row < lines.Length; row++)
            {
                var parts = new string[WordmarkLetters.Length];
                for (int letter = 0; letter < WordmarkLetters.Length; letter++)
                {
                    parts[letter] = WordmarkLetters[letter][row];
                }
                lines[row] = String.Join(" ", parts);
            }
            return lines;
        }

        private static void WriteRule()
        {
            Console.WriteLine("  +--------------------------------------------------------+");
        }

        private static void SetColor(bool interactive, ConsoleColor color)
        {
            if (interactive)
            {
                Console.ForegroundColor = color;
            }
        }

        private static int PrintPollResults(IList<PollResult> results)
        {
            foreach (PollResult result in results)
            {
                Console.WriteLine(FormatPollResult(result));
            }
            return 0;
        }

        internal static string FormatPollResult(PollResult result)
        {
            if (result == null) return String.Empty;
            if (!String.IsNullOrEmpty(result.Error))
            {
                return String.Format(
                    "ERROR target={0} pid={1} error={2} reason={3}",
                    result.Target,
                    result.ProcessId,
                    Quote(result.Error),
                    Quote(result.Reason));
            }

            string next = result.NextAttemptUtc == DateTime.MinValue
                ? "-"
                : result.NextAttemptUtc.ToString("o", CultureInfo.InvariantCulture);

            return String.Format(
                "MATCH target={0} pid={1} attach={2} title={3} trigger={4} ready={5} busy={6} would_send={7} sent={8} next={9} reason={10}",
                result.Target,
                result.ProcessId,
                result.AttachProcessId,
                Quote(result.Title),
                result.Triggered,
                result.Ready,
                result.Busy,
                result.WouldSend,
                result.Sent,
                next,
                Quote(result.Reason));
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? String.Empty).Replace("\"", "\\\"") + "\"";
        }
    }
}


