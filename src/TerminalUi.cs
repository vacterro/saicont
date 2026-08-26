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
            if (Console.IsOutputRedirected || Console.IsInputRedirected)
            {
                Console.WriteLine("Non-interactive console environment detected; running probe instead.");
                return PrintPollResults(new WatcherEngine(configuration).PollOnce(false));
            }

            ConsoleColor origFg = Console.ForegroundColor;
            ConsoleColor origBg = Console.BackgroundColor;
            bool origCursorVisible = true;
            try { origCursorVisible = Console.CursorVisible; } catch { }

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
            string statusMessage = "Ready. Press [P] to Probe, [D] for Dry-Run, [W] for Watch, [1-4] for Tabs, [Q] to Quit.";
            bool confirmWatch = false;
            var logs = new List<TuiLogEntry>();
            var latestSessions = new List<PollResult>();
            var engine = new WatcherEngine(configuration);
            int pollCounter = 0;
            DateTime lastPollTime = DateTime.MinValue;

            try
            {
                Console.BackgroundColor = ConsoleColor.Black;
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.Clear();
                try { Console.CursorVisible = false; } catch { }

                latestSessions = new List<PollResult>(engine.PollOnce(false));
                pollCounter++;
                lastPollTime = DateTime.UtcNow;
                AddLog(logs, "INFO", "system", 0, "SAICONT TUI started with " + configuration.Targets.Count + " target rules.");
                foreach (PollResult r in latestSessions)
                {
                    AddLogFromPoll(logs, r);
                }

                bool running = true;
                while (running)
                {
                    DateTime now = DateTime.UtcNow;

                    if (mode == TuiMode.Watch || mode == TuiMode.DryRun)
                    {
                        int interval = Math.Max(500, configuration.PollIntervalMilliseconds);
                        if ((now - lastPollTime).TotalMilliseconds >= interval)
                        {
                            bool allowInput = (mode == TuiMode.Watch);
                            IList<PollResult> results = engine.PollOnce(allowInput);
                            latestSessions = new List<PollResult>(results);
                            pollCounter++;
                            lastPollTime = now;
                            foreach (PollResult r in results)
                            {
                                AddLogFromPoll(logs, r);
                            }
                        }
                    }

                    RenderTui(configuration, configPath, mode, activeTab, latestSessions, logs, statusMessage, confirmWatch, pollCounter, lastPollTime);

                    int sleepRemaining = 100;
                    while (sleepRemaining > 0)
                    {
                        if (Console.KeyAvailable)
                        {
                            ConsoleKeyInfo key = Console.ReadKey(true);
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

                                    case ConsoleKey.D1:
                                    case ConsoleKey.F1:
                                        activeTab = TuiTab.Sessions;
                                        statusMessage = "View: Live Sessions Dashboard";
                                        break;

                                    case ConsoleKey.D2:
                                    case ConsoleKey.F2:
                                        activeTab = TuiTab.LogStream;
                                        statusMessage = "View: Real-Time Event & Activity Log";
                                        break;

                                    case ConsoleKey.D3:
                                    case ConsoleKey.F3:
                                        activeTab = TuiTab.Rules;
                                        statusMessage = "View: Target Rules & Configuration";
                                        break;

                                    case ConsoleKey.D4:
                                    case ConsoleKey.F4:
                                        activeTab = TuiTab.Help;
                                        statusMessage = "View: Quick Reference & Safety Architecture";
                                        break;

                                    case ConsoleKey.P:
                                        statusMessage = "Executing single PROBE pass...";
                                        latestSessions = new List<PollResult>(engine.PollOnce(false));
                                        pollCounter++;
                                        lastPollTime = DateTime.UtcNow;
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
                                        AddLog(logs, "INFO", "operator", 0, "Stopped.");
                                        break;

                                    case ConsoleKey.C:
                                        logs.Clear();
                                        statusMessage = "Log stream buffer cleared.";
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
                try { Console.CursorVisible = origCursorVisible; } catch { }
                Console.ForegroundColor = origFg;
                Console.BackgroundColor = origBg;
                Console.Clear();
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
            int pollCounter,
            DateTime lastPollTime)
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
            switch (activeTab)
            {
                case TuiTab.Sessions:
                    RenderSessionsTab(width, contentHeight, sessions);
                    break;
                case TuiTab.LogStream:
                    RenderLogStreamTab(width, contentHeight, logs);
                    break;
                case TuiTab.Rules:
                    RenderRulesTab(width, contentHeight, configuration, configPath);
                    break;
                case TuiTab.Help:
                    RenderHelpTab(width, contentHeight);
                    break;
            }

            WriteWin95Footer(width, mode, statusMessage, confirmWatch);
        }

        private static void WriteWin95Header(int width, TuiMode mode, int pollCounter, DateTime lastPollTime)
        {
            Console.BackgroundColor = ConsoleColor.DarkYellow;
            Console.ForegroundColor = ConsoleColor.Black;

            string title = " SAICONT v1.0.0 [TERMINAL CONTINUITY] ";
            string modeStr = " [" + mode.ToString().ToUpperInvariant() + "] ";
            string pollsStr = "Polls: " + pollCounter + " | " + (lastPollTime == DateTime.MinValue ? "--:--:--" : lastPollTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)) + " UTC ";

            int availableSpace = width - title.Length - modeStr.Length - pollsStr.Length;
            if (availableSpace < 0) availableSpace = 0;

            Console.Write(title);
            Console.BackgroundColor = mode == TuiMode.Watch ? ConsoleColor.DarkRed : (mode == TuiMode.DryRun ? ConsoleColor.DarkGreen : ConsoleColor.DarkGray);
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write(modeStr);

            Console.BackgroundColor = ConsoleColor.DarkYellow;
            Console.ForegroundColor = ConsoleColor.Black;
            Console.Write(new string(' ', availableSpace));
            Console.Write(pollsStr);
            Console.WriteLine();
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
            int pad = Math.Max(0, width - used);
            Console.Write(new string(' ', pad));
            Console.WriteLine();
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

        private static void RenderSessionsTab(int width, int maxLines, IList<PollResult> sessions)
        {
            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine(" " + BoxLine("DISCOVERED TERMINAL SESSIONS & PROMPT STATUS", width - 2));

            Console.BackgroundColor = ConsoleColor.DarkGray;
            Console.ForegroundColor = ConsoleColor.White;
            string hdr = String.Format(
                CultureInfo.InvariantCulture,
                " {0,-14} {1,-8} {2,-7} {3,-18} {4,-7} {5,-10} {6}",
                "RULE", "PROCESS", "PID", "TITLE", "STATUS", "PROMPT", "DECISION / REASON");
            if (hdr.Length < width) hdr = hdr.PadRight(width);
            else if (hdr.Length > width) hdr = hdr.Substring(0, width);
            Console.WriteLine(hdr);

            int linesPrinted = 0;
            Console.BackgroundColor = ConsoleColor.Black;

            if (sessions == null || sessions.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("   (No matching active console sessions discovered. Press [P] to Probe)");
                linesPrinted++;
            }
            else
            {
                foreach (PollResult s in sessions)
                {
                    if (linesPrinted >= maxLines - 2) break;

                    Console.ForegroundColor = s.Read ? ConsoleColor.Gray : ConsoleColor.DarkRed;
                    string title = String.IsNullOrEmpty(s.Title) ? "-" : (s.Title.Length > 16 ? s.Title.Substring(0, 16) + ".." : s.Title);
                    string status = s.Read ? "READ" : "FAIL";
                    string prompt = s.Busy ? "BUSY" : (s.Ready ? "READY" : (s.Triggered ? "TRIGGER" : "IDLE"));
                    string reason = s.Reason ?? "-";
                    if (reason.Length > width - 70) reason = reason.Substring(0, Math.Max(4, width - 73)) + "...";

                    ConsoleColor promptColor = s.Busy ? ConsoleColor.DarkRed : (s.Ready ? ConsoleColor.DarkGreen : (s.Triggered ? ConsoleColor.DarkYellow : ConsoleColor.Gray));

                    string line = String.Format(
                        CultureInfo.InvariantCulture,
                        " {0,-14} {1,-8} {2,-7} {3,-18} ",
                        Truncate(s.Target, 14),
                        Truncate(s.ProcessName, 8),
                        s.ProcessId,
                        title);

                    Console.Write(line);
                    Console.ForegroundColor = s.Read ? ConsoleColor.Green : ConsoleColor.Red;
                    Console.Write(String.Format(CultureInfo.InvariantCulture, "{0,-7} ", status));
                    Console.ForegroundColor = promptColor;
                    Console.Write(String.Format(CultureInfo.InvariantCulture, "{0,-10} ", prompt));
                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.WriteLine(reason);
                    linesPrinted++;
                }
            }

            while (linesPrinted < maxLines)
            {
                Console.WriteLine(new string(' ', width));
                linesPrinted++;
            }
        }

        private static void RenderLogStreamTab(int width, int maxLines, IList<TuiLogEntry> logs)
        {
            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine(" " + BoxLine("REAL-TIME EVENT STREAM & OPERATIONAL AUDIT", width - 2));

            int linesPrinted = 0;
            int startIndex = Math.Max(0, logs.Count - (maxLines - 1));

            for (int i = startIndex; i < logs.Count && linesPrinted < maxLines - 1; i++)
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
                int maxMsgWidth = Math.Max(10, width - 20);
                if (msg.Length > maxMsgWidth) msg = msg.Substring(0, maxMsgWidth - 3) + "...";
                Console.WriteLine(msg);
                linesPrinted++;
            }

            while (linesPrinted < maxLines)
            {
                Console.WriteLine(new string(' ', width));
                linesPrinted++;
            }
        }

        private static void RenderRulesTab(int width, int maxLines, WatcherConfiguration config, string configPath)
        {
            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine(" " + BoxLine("ACTIVE WATCHER CONFIGURATION (" + Path.GetFileName(configPath) + ")", width - 2));

            int linesPrinted = 0;
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("  Poll Interval: " + config.PollIntervalMilliseconds + "ms | Log File: " + config.LogFilePath);
            linesPrinted++;

            Console.BackgroundColor = ConsoleColor.DarkGray;
            Console.ForegroundColor = ConsoleColor.White;
            string hdr = String.Format(
                CultureInfo.InvariantCulture,
                " {0,-18} {1,-10} {2,-16} {3,-8} {4,-12} {5}",
                "RULE NAME", "ENABLED", "PROCESSES", "CMD", "RETRY/BACKOFF", "TRIGGERS");
            if (hdr.Length < width) hdr = hdr.PadRight(width);
            Console.WriteLine(hdr);
            linesPrinted++;

            Console.BackgroundColor = ConsoleColor.Black;
            foreach (TargetRule rule in config.Targets)
            {
                if (linesPrinted >= maxLines) break;
                Console.ForegroundColor = rule.Enabled ? ConsoleColor.Green : ConsoleColor.DarkGray;
                string procs = String.Join(",", rule.ProcessNames);
                string retryInfo = rule.InitialDelaySeconds + "s/" + rule.BackoffMultiplier + "x (max " + rule.MaximumRetryIntervalSeconds + "s)";
                string line = String.Format(
                    CultureInfo.InvariantCulture,
                    " {0,-18} {1,-10} {2,-16} {3,-8} {4,-12} {5} triggers",
                    Truncate(rule.Name, 18),
                    rule.Enabled ? "TRUE" : "FALSE",
                    Truncate(procs, 16),
                    Truncate(rule.Command, 8),
                    Truncate(retryInfo, 12),
                    rule.TriggerPatterns.Length);
                Console.WriteLine(line);
                linesPrinted++;
            }

            while (linesPrinted < maxLines)
            {
                Console.WriteLine(new string(' ', width));
                linesPrinted++;
            }
        }

        private static void RenderHelpTab(int width, int maxLines)
        {
            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine(" " + BoxLine("SAICONT TERMINAL GUI QUICK REFERENCE & SAFETY GUARANTEES", width - 2));

            string[] helpLines = new[]
            {
                " HOTKEYS:",
                "   [P]         Single Probe      - Scan process tree and read target console buffers.",
                "   [D]         Toggle Dry-Run    - Continuously inspect consoles without injecting input.",
                "   [W]         Toggle Watch      - Enable live automated continuation (guarded with confirmation).",
                "   [S]         Stop / Pause      - Stop active continuous monitoring and return to Idle.",
                "   [1] - [4]   Switch Tabs       - [1] Sessions  [2] Logs  [3] Rules  [4] Help",
                "   [C]         Clear Logs        - Flush the in-memory TUI log view buffer.",
                "   [Q] / [Esc] Quit              - Safely exit the dashboard (restores console state).",
                "",
                " SAFETY ARCHITECTURE:",
                "   * Zero Focus Steal: Uses Win32 AttachConsole + WriteConsoleInputW; never steals window focus.",
                "   * Transactional Send: Re-verifies console window and process start time right before sending.",
                "   * Exponential Backoff: Hard caps on attempts (5) and retry intervals (up to 3600s).",
                "   * Durable State: Retry countdowns and suppressed events persist across runs in SAICONT.state.xml."
            };

            int linesPrinted = 0;
            Console.ForegroundColor = ConsoleColor.Gray;
            foreach (string hl in helpLines)
            {
                if (linesPrinted >= maxLines) break;
                Console.WriteLine(hl.PadRight(width));
                linesPrinted++;
            }

            while (linesPrinted < maxLines)
            {
                Console.WriteLine(new string(' ', width));
                linesPrinted++;
            }
        }

        private static void WriteWin95Footer(int width, TuiMode mode, string statusMessage, bool confirmWatch)
        {
            Console.BackgroundColor = confirmWatch ? ConsoleColor.DarkRed : ConsoleColor.DarkCyan;
            Console.ForegroundColor = ConsoleColor.White;
            string status = " " + (statusMessage ?? String.Empty);
            if (status.Length < width) status = status.PadRight(width);
            else if (status.Length > width) status = status.Substring(0, width);
            Console.WriteLine(status);

            Console.BackgroundColor = ConsoleColor.DarkGray;
            Console.ForegroundColor = ConsoleColor.Black;
            string keyBar = " [P] Probe  [D] Dry-Run  [W] Watch  [S] Stop  [1-4] Tabs  [C] Clear  [Q] Quit ";
            if (keyBar.Length < width) keyBar = keyBar.PadRight(width);
            else if (keyBar.Length > width) keyBar = keyBar.Substring(0, width);
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
            if (String.IsNullOrEmpty(title)) return new string('=', length);
            int dashes = length - title.Length - 4;
            if (dashes < 2) return "== " + title + " ==";
            return "==[ " + title + " ]" + new string('=', dashes);
        }

        private static string Truncate(string val, int max)
        {
            if (String.IsNullOrEmpty(val)) return "-";
            return val.Length <= max ? val : val.Substring(0, max - 2) + "..";
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

