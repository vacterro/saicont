using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace SaiCont
{
    internal enum SystemOperationalState
    {
        OnWatch,      // ON - Live Watch (Continuous monitoring + Automated input injection)
        OnDryRun,     // ON - Dry-Run (Continuous monitoring + Zero input injection)
        Paused,       // PAUSED - Idle (Timer paused, manual probe only)
        Disabled      // DISABLED - Stopped (All monitoring disabled)
    }

    internal sealed class SaiContGuiForm : Form
    {
        private readonly string configPath;
        private WatcherConfiguration currentConfig;
        private WatcherEngine engine;
        private SystemOperationalState systemState = SystemOperationalState.Paused;
        private int pollCounter = 0;
        private DateTime lastPollTime = DateTime.MinValue;

        // UI Controls
        private Panel headerPanel;
        private Label titleLabel;
        private Label stateBanner;
        private Label statsLabel;

        private Panel stateControlPanel;
        private Button btnStateWatch;
        private Button btnStateDryRun;
        private Button btnStatePause;
        private Button btnStateDisable;
        private Button btnProbe;
        private Button btnReload;
        private Button btnClearLog;
        private Button btnCopy;

        private SplitContainer mainSplit;
        private TabControl mainTabs;
        private TabPage tabSessions;
        private TabPage tabRules;
        private TabPage tabInspector;
        private ListView sessionListView;
        private ListView rulesListView;
        private TextBox inspectorTextBox;
        private Button btnToggleRule;

        private RichTextBox logRichText;
        private StatusStrip statusStrip;
        private ToolStripStatusLabel statusLabel;
        private ToolStripStatusLabel sessionCountLabel;
        private ToolStripStatusLabel stateIndicatorLabel;
        private ToolStripStatusLabel pollStatsLabel;

        private Timer pollTimer;
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;

        // Colors (Win95 Dark Golden Theme)
        private static readonly Color BgDark = Color.FromArgb(20, 17, 14);
        private static readonly Color BgPanel = Color.FromArgb(32, 27, 22);
        private static readonly Color BgControl = Color.FromArgb(44, 38, 30);
        private static readonly Color BorderLight = Color.FromArgb(68, 58, 48);
        private static readonly Color BorderDark = Color.FromArgb(12, 10, 8);
        private static readonly Color TextGold = Color.FromArgb(212, 175, 55);
        private static readonly Color TextGoldBright = Color.FromArgb(245, 215, 110);
        private static readonly Color TextMuted = Color.FromArgb(168, 159, 145);
        private static readonly Color StateGreen = Color.FromArgb(46, 204, 113);
        private static readonly Color StateCyan = Color.FromArgb(52, 152, 219);
        private static readonly Color StateYellow = Color.FromArgb(241, 196, 15);
        private static readonly Color StateRed = Color.FromArgb(231, 76, 60);
        private static readonly Color StateGray = Color.FromArgb(127, 140, 141);

        private readonly List<PollResult> currentPollResults = new List<PollResult>();

        public SaiContGuiForm(WatcherConfiguration config, string configurationFilePath, string initialMode = null)
        {
            configPath = configurationFilePath;
            currentConfig = config;
            engine = new WatcherEngine(currentConfig);

            if (String.Equals(initialMode, "--watch", StringComparison.OrdinalIgnoreCase)) systemState = SystemOperationalState.OnWatch;
            else if (String.Equals(initialMode, "--dry-run", StringComparison.OrdinalIgnoreCase)) systemState = SystemOperationalState.OnDryRun;
            else systemState = SystemOperationalState.Paused;

            InitializeGui();
            ApplyGoldenTheme();
            ReloadRulesList();
            UpdateStateUi();
            RunProbe();
        }

        private void InitializeGui()
        {
            this.Text = "SAICONT - Terminal Continuity Manager & Console Watcher";
            this.Size = new Size(1060, 720);
            this.MinimumSize = new Size(820, 520);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = BgDark;
            this.ForeColor = TextMuted;
            this.KeyPreview = true;
            this.KeyDown += OnFormKeyDown;
            this.FormClosing += OnFormClosing;

            // 1. Header Panel
            headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 50,
                BackColor = Color.FromArgb(38, 32, 25),
                Padding = new Padding(10, 8, 10, 8)
            };
            headerPanel.Paint += (s, e) => DrawWin95Bevel(e.Graphics, headerPanel.ClientRectangle, true);

            titleLabel = new Label
            {
                Text = "SAICONT v1.0.0 [TERMINAL CONTINUITY]",
                Font = new Font("Verdana", 11f, FontStyle.Bold),
                ForeColor = TextGoldBright,
                AutoSize = true,
                Location = new Point(12, 14)
            };

            stateBanner = new Label
            {
                Text = "  STATE: [ ⏸ PAUSED / IDLE ]  ",
                Font = new Font("Verdana", 9.5f, FontStyle.Bold),
                ForeColor = Color.Black,
                BackColor = StateYellow,
                AutoSize = true,
                Location = new Point(360, 12),
                Padding = new Padding(6, 4, 6, 4)
            };

            statsLabel = new Label
            {
                Text = "Polls: 0 | Ready",
                Font = new Font("Lucida Console", 9.5f, FontStyle.Regular),
                ForeColor = TextGold,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleRight,
                Dock = DockStyle.Right,
                Width = 320
            };

            headerPanel.Controls.Add(titleLabel);
            headerPanel.Controls.Add(stateBanner);
            headerPanel.Controls.Add(statsLabel);

            // 2. State & Action Control Bar
            stateControlPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 42,
                BackColor = BgPanel,
                Padding = new Padding(8, 6, 8, 6)
            };

            btnStateWatch = CreateStateButton("▶ ON: Watch Mode", StateGreen, (s, e) => SetState(SystemOperationalState.OnWatch));
            btnStateDryRun = CreateStateButton("👁 ON: Dry-Run", StateCyan, (s, e) => SetState(SystemOperationalState.OnDryRun));
            btnStatePause = CreateStateButton("⏸ PAUSE Monitoring", StateYellow, (s, e) => SetState(SystemOperationalState.Paused));
            btnStateDisable = CreateStateButton("⏹ STOP / Disable", StateRed, (s, e) => SetState(SystemOperationalState.Disabled));

            var sep = new Panel { Dock = DockStyle.Left, Width = 12, BackColor = Color.Transparent };

            btnProbe = CreateActionButton("🔍 Probe (F5)", (s, e) => RunProbe());
            btnReload = CreateActionButton("⚙ Reload Config", (s, e) => ReloadConfig());
            btnClearLog = CreateActionButton("🧹 Clear Log", (s, e) => ClearLog());
            btnCopy = CreateActionButton("📋 Copy Info", (s, e) => CopySelectedSessionInfo());

            stateControlPanel.Controls.Add(btnCopy);
            stateControlPanel.Controls.Add(btnClearLog);
            stateControlPanel.Controls.Add(btnReload);
            stateControlPanel.Controls.Add(btnProbe);
            stateControlPanel.Controls.Add(sep);
            stateControlPanel.Controls.Add(btnStateDisable);
            stateControlPanel.Controls.Add(btnStatePause);
            stateControlPanel.Controls.Add(btnStateDryRun);
            stateControlPanel.Controls.Add(btnStateWatch);

            // 3. Main Split Container
            mainSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 320,
                SplitterWidth = 6,
                BackColor = BgDark
            };

            // 4. Tab Control (Top Half)
            mainTabs = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Verdana", 9f, FontStyle.Bold)
            };

            tabSessions = new TabPage("Live Monitored Sessions");
            tabRules = new TabPage("Target Rules Configuration");
            tabInspector = new TabPage("Deep Diagnostic Inspector");

            sessionListView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                Font = new Font("Lucida Console", 9f, FontStyle.Regular),
                BackColor = BgDark,
                ForeColor = TextGoldBright
            };
            sessionListView.Columns.Add("Rule Target", 120);
            sessionListView.Columns.Add("Process", 80);
            sessionListView.Columns.Add("PID", 65);
            sessionListView.Columns.Add("Attach PID", 80);
            sessionListView.Columns.Add("Window Title", 130);
            sessionListView.Columns.Add("Read Status", 85);
            sessionListView.Columns.Add("Prompt Readiness", 110);
            sessionListView.Columns.Add("Operational State", 140);
            sessionListView.Columns.Add("Next UTC", 100);
            sessionListView.Columns.Add("Decision / Reason", 220);
            sessionListView.SelectedIndexChanged += (s, e) => UpdateInspectorFromSelected();
            sessionListView.DoubleClick += (s, e) => { mainTabs.SelectedTab = tabInspector; };
            tabSessions.Controls.Add(sessionListView);

            // Rules Panel
            var rulesPanel = new Panel { Dock = DockStyle.Fill, BackColor = BgDark };
            rulesListView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                Font = new Font("Lucida Console", 9f, FontStyle.Regular),
                BackColor = BgDark,
                ForeColor = TextGoldBright
            };
            rulesListView.Columns.Add("Rule Name", 140);
            rulesListView.Columns.Add("Status", 90);
            rulesListView.Columns.Add("Processes", 120);
            rulesListView.Columns.Add("Command", 80);
            rulesListView.Columns.Add("Delay", 65);
            rulesListView.Columns.Add("Retry Interval", 95);
            rulesListView.Columns.Add("Backoff", 90);
            rulesListView.Columns.Add("Triggers", 75);

            var rulesBar = new Panel { Dock = DockStyle.Bottom, Height = 34, BackColor = BgPanel, Padding = new Padding(6, 4, 6, 4) };
            btnToggleRule = CreateActionButton("Toggle Rule Enabled/Disabled", (s, e) => ToggleSelectedRule());
            rulesBar.Controls.Add(btnToggleRule);

            rulesPanel.Controls.Add(rulesListView);
            rulesPanel.Controls.Add(rulesBar);
            tabRules.Controls.Add(rulesPanel);

            inspectorTextBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                Font = new Font("Lucida Console", 9.5f, FontStyle.Regular),
                BackColor = BgDark,
                ForeColor = TextGoldBright
            };
            tabInspector.Controls.Add(inspectorTextBox);

            mainTabs.TabPages.Add(tabSessions);
            mainTabs.TabPages.Add(tabRules);
            mainTabs.TabPages.Add(tabInspector);
            mainSplit.Panel1.Controls.Add(mainTabs);

            // 5. Log Stream (Bottom Half)
            var logPanel = new Panel { Dock = DockStyle.Fill, BackColor = BgDark };
            var logHeader = new Label
            {
                Dock = DockStyle.Top,
                Height = 22,
                Text = "  REAL-TIME OPERATIONAL LOG STREAM",
                Font = new Font("Verdana", 8.5f, FontStyle.Bold),
                ForeColor = TextGoldBright,
                BackColor = Color.FromArgb(28, 23, 18),
                TextAlign = ContentAlignment.MiddleLeft
            };

            logRichText = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.FromArgb(14, 12, 10),
                ForeColor = Color.FromArgb(200, 190, 175),
                Font = new Font("Lucida Console", 9f, FontStyle.Regular),
                BorderStyle = BorderStyle.None,
                HideSelection = false
            };
            logPanel.Controls.Add(logRichText);
            logPanel.Controls.Add(logHeader);
            mainSplit.Panel2.Controls.Add(logPanel);

            // 6. Status Strip
            statusStrip = new StatusStrip
            {
                BackColor = Color.FromArgb(30, 25, 20),
                ForeColor = TextGold
            };
            statusLabel = new ToolStripStatusLabel("Ready.") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            stateIndicatorLabel = new ToolStripStatusLabel("STATE: PAUSED") { Width = 140, Font = new Font("Verdana", 8.5f, FontStyle.Bold) };
            sessionCountLabel = new ToolStripStatusLabel("Sessions: 0") { Width = 110 };
            pollStatsLabel = new ToolStripStatusLabel("Polls: 0") { Width = 110 };

            statusStrip.Items.Add(statusLabel);
            statusStrip.Items.Add(new ToolStripSeparator());
            statusStrip.Items.Add(stateIndicatorLabel);
            statusStrip.Items.Add(new ToolStripSeparator());
            statusStrip.Items.Add(sessionCountLabel);
            statusStrip.Items.Add(new ToolStripSeparator());
            statusStrip.Items.Add(pollStatsLabel);

            // 7. System Tray Icon
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Open Dashboard", null, (s, e) => ShowDashboard());
            trayMenu.Items.Add("Probe Now", null, (s, e) => RunProbe());
            trayMenu.Items.Add("Enable Watch Mode", null, (s, e) => SetState(SystemOperationalState.OnWatch));
            trayMenu.Items.Add("Enable Dry-Run", null, (s, e) => SetState(SystemOperationalState.OnDryRun));
            trayMenu.Items.Add("Pause Monitoring", null, (s, e) => SetState(SystemOperationalState.Paused));
            trayMenu.Items.Add("Disable / Stop", null, (s, e) => SetState(SystemOperationalState.Disabled));
            trayMenu.Items.Add("-");
            trayMenu.Items.Add("Exit", null, (s, e) => { trayIcon.Visible = false; Application.Exit(); });

            trayIcon = new NotifyIcon
            {
                Text = "SAICONT - Terminal Continuity Manager",
                Icon = SystemIcons.Application,
                ContextMenuStrip = trayMenu,
                Visible = true
            };
            trayIcon.DoubleClick += (s, e) => ShowDashboard();

            // 8. Add Controls
            this.Controls.Add(mainSplit);
            this.Controls.Add(stateControlPanel);
            this.Controls.Add(headerPanel);
            this.Controls.Add(statusStrip);

            // 9. Poll Timer
            pollTimer = new Timer
            {
                Interval = Math.Max(500, currentConfig.PollIntervalMilliseconds)
            };
            pollTimer.Tick += OnPollTimerTick;
            pollTimer.Start();

            AppendLog("INFO", "SAICONT Desktop Manager started. Configure mode using the state buttons above.");
        }

        private Button CreateStateButton(string text, Color accentColor, EventHandler onClick)
        {
            var btn = new Button
            {
                Text = text,
                AutoSize = true,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                BackColor = BgControl,
                ForeColor = accentColor,
                Font = new Font("Verdana", 8.5f, FontStyle.Bold),
                Margin = new Padding(2, 0, 6, 0),
                Dock = DockStyle.Left,
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderColor = BorderLight;
            btn.FlatAppearance.BorderSize = 1;
            btn.Click += onClick;
            return btn;
        }

        private Button CreateActionButton(string text, EventHandler onClick)
        {
            var btn = new Button
            {
                Text = text,
                AutoSize = true,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                BackColor = BgControl,
                ForeColor = TextGoldBright,
                Font = new Font("Verdana", 8f, FontStyle.Regular),
                Margin = new Padding(2, 0, 4, 0),
                Dock = DockStyle.Left,
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderColor = BorderLight;
            btn.FlatAppearance.BorderSize = 1;
            btn.Click += onClick;
            return btn;
        }

        private void ApplyGoldenTheme()
        {
            this.tabSessions.BackColor = BgDark;
            this.tabRules.BackColor = BgDark;
            this.tabInspector.BackColor = BgDark;
        }

        private void DrawWin95Bevel(Graphics g, Rectangle r, bool raised)
        {
            Color light = raised ? BorderLight : BorderDark;
            Color dark = raised ? BorderDark : BorderLight;
            using (var pLight = new Pen(light))
            using (var pDark = new Pen(dark))
            {
                g.DrawLine(pLight, r.Left, r.Top, r.Right - 1, r.Top);
                g.DrawLine(pLight, r.Left, r.Top, r.Left, r.Bottom - 1);
                g.DrawLine(pDark, r.Right - 1, r.Top, r.Right - 1, r.Bottom - 1);
                g.DrawLine(pDark, r.Left, r.Bottom - 1, r.Right - 1, r.Bottom - 1);
            }
        }

        public void SetState(SystemOperationalState newState)
        {
            if (newState == SystemOperationalState.OnWatch && systemState != SystemOperationalState.OnWatch)
            {
                DialogResult dr = MessageBox.Show(
                    "Activate Automated Continuation (Watch Mode)?\n\nSAICONT will actively monitor Cline/Codex and automatically inject continuation commands ('cc' or Enter) when recovery conditions are met.",
                    "Confirm Watch Mode Activation",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (dr != DialogResult.Yes)
                {
                    statusLabel.Text = "Watch mode activation canceled by user.";
                    return;
                }
            }

            systemState = newState;
            UpdateStateUi();

            switch (systemState)
            {
                case SystemOperationalState.OnWatch:
                    statusLabel.Text = "WATCH MODE ACTIVE: Automated continuation is LIVE and monitoring.";
                    AppendLog("WARN", "System State changed to [ON: WATCH MODE] - Live injection active.");
                    RunPollCycle();
                    break;
                case SystemOperationalState.OnDryRun:
                    statusLabel.Text = "DRY-RUN ACTIVE: Continuous discovery monitoring (Zero injection).";
                    AppendLog("INFO", "System State changed to [ON: DRY-RUN] - Safe continuous discovery.");
                    RunPollCycle();
                    break;
                case SystemOperationalState.Paused:
                    statusLabel.Text = "MONITORING PAUSED: In Idle state. Press Probe or start Watch/Dry-Run.";
                    AppendLog("INFO", "System State changed to [PAUSED / IDLE].");
                    break;
                case SystemOperationalState.Disabled:
                    statusLabel.Text = "MONITORING DISABLED: All continuous polling stopped.";
                    AppendLog("WARN", "System State changed to [DISABLED / STOPPED].");
                    break;
            }
        }

        private void UpdateStateUi()
        {
            switch (systemState)
            {
                case SystemOperationalState.OnWatch:
                    stateBanner.Text = "  STATE: [ ● ON / WATCHING (LIVE INJECTION) ]  ";
                    stateBanner.BackColor = StateGreen;
                    stateBanner.ForeColor = Color.Black;
                    stateIndicatorLabel.Text = "STATE: WATCH (ON)";
                    stateIndicatorLabel.ForeColor = StateGreen;
                    btnStateWatch.BackColor = Color.FromArgb(20, 60, 30);
                    btnStateDryRun.BackColor = BgControl;
                    btnStatePause.BackColor = BgControl;
                    btnStateDisable.BackColor = BgControl;
                    break;
                case SystemOperationalState.OnDryRun:
                    stateBanner.Text = "  STATE: [ 👁 ON / DRY-RUN (DISCOVERY ONLY) ]  ";
                    stateBanner.BackColor = StateCyan;
                    stateBanner.ForeColor = Color.Black;
                    stateIndicatorLabel.Text = "STATE: DRY-RUN (ON)";
                    stateIndicatorLabel.ForeColor = StateCyan;
                    btnStateWatch.BackColor = BgControl;
                    btnStateDryRun.BackColor = Color.FromArgb(20, 50, 70);
                    btnStatePause.BackColor = BgControl;
                    btnStateDisable.BackColor = BgControl;
                    break;
                case SystemOperationalState.Paused:
                    stateBanner.Text = "  STATE: [ ⏸ PAUSED / IDLE (MANUAL PROBE ONLY) ]  ";
                    stateBanner.BackColor = StateYellow;
                    stateBanner.ForeColor = Color.Black;
                    stateIndicatorLabel.Text = "STATE: PAUSED";
                    stateIndicatorLabel.ForeColor = StateYellow;
                    btnStateWatch.BackColor = BgControl;
                    btnStateDryRun.BackColor = BgControl;
                    btnStatePause.BackColor = Color.FromArgb(60, 50, 20);
                    btnStateDisable.BackColor = BgControl;
                    break;
                case SystemOperationalState.Disabled:
                    stateBanner.Text = "  STATE: [ ⏹ STOPPED / DISABLED ]  ";
                    stateBanner.BackColor = StateRed;
                    stateBanner.ForeColor = Color.White;
                    stateIndicatorLabel.Text = "STATE: DISABLED";
                    stateIndicatorLabel.ForeColor = StateRed;
                    btnStateWatch.BackColor = BgControl;
                    btnStateDryRun.BackColor = BgControl;
                    btnStatePause.BackColor = BgControl;
                    btnStateDisable.BackColor = Color.FromArgb(70, 20, 20);
                    break;
            }
        }

        private void ReloadRulesList()
        {
            rulesListView.Items.Clear();
            foreach (TargetRule rule in currentConfig.Targets)
            {
                var item = new ListViewItem(rule.Name);
                item.SubItems.Add(rule.Enabled ? "ENABLED" : "DISABLED");
                item.SubItems.Add(String.Join(",", rule.ProcessNames));
                item.SubItems.Add(rule.Command);
                item.SubItems.Add(rule.InitialDelaySeconds + "s");
                item.SubItems.Add(rule.RetryIntervalSeconds + "s");
                item.SubItems.Add(rule.BackoffMultiplier + "x (max " + rule.MaximumRetryIntervalSeconds + "s)");
                item.SubItems.Add(rule.TriggerPatterns.Length.ToString(CultureInfo.InvariantCulture));
                item.ForeColor = rule.Enabled ? StateGreen : StateGray;
                rulesListView.Items.Add(item);
            }
        }

        private void ToggleSelectedRule()
        {
            if (rulesListView.SelectedItems.Count > 0)
            {
                int idx = rulesListView.SelectedItems[0].Index;
                if (idx >= 0 && idx < currentConfig.Targets.Count)
                {
                    TargetRule r = currentConfig.Targets[idx];
                    r.Enabled = !r.Enabled;
                    ReloadRulesList();
                    AppendLog("INFO", "Target rule '" + r.Name + "' state changed to: " + (r.Enabled ? "ENABLED" : "DISABLED"));
                    statusLabel.Text = "Rule '" + r.Name + "' is now " + (r.Enabled ? "ENABLED" : "DISABLED");
                }
            }
            else
            {
                statusLabel.Text = "Select a target rule to toggle.";
            }
        }

        private void RunProbe()
        {
            statusLabel.Text = "Probing target console sessions...";
            this.Cursor = Cursors.WaitCursor;
            try
            {
                IList<PollResult> results = engine.PollOnce(false);
                UpdateSessionsList(results);
                pollCounter++;
                lastPollTime = DateTime.UtcNow;
                UpdateHeaderStats();
                statusLabel.Text = "Probe complete: " + results.Count + " sessions inspected.";
                AppendLog("INFO", "Probe pass executed: " + results.Count + " session(s) found.");
            }
            catch (Exception ex)
            {
                statusLabel.Text = "Probe error: " + ex.Message;
                AppendLog("ERROR", "Probe failed: " + ex.Message);
            }
            finally
            {
                this.Cursor = Cursors.Default;
            }
        }

        private void ReloadConfig()
        {
            try
            {
                currentConfig = WatcherConfiguration.Load(configPath);
                engine = new WatcherEngine(currentConfig);
                ReloadRulesList();
                pollTimer.Interval = Math.Max(500, currentConfig.PollIntervalMilliseconds);
                statusLabel.Text = "Configuration reloaded (" + currentConfig.Targets.Count + " rules).";
                AppendLog("INFO", "Configuration reloaded from " + Path.GetFileName(configPath));
                RunProbe();
            }
            catch (Exception ex)
            {
                statusLabel.Text = "Reload error: " + ex.Message;
                AppendLog("ERROR", "Failed to reload configuration: " + ex.Message);
            }
        }

        private void ClearLog()
        {
            logRichText.Clear();
            statusLabel.Text = "Log stream cleared.";
        }

        private void CopySelectedSessionInfo()
        {
            if (sessionListView.SelectedItems.Count > 0)
            {
                ListViewItem item = sessionListView.SelectedItems[0];
                int idx = item.Index;
                if (idx >= 0 && idx < currentPollResults.Count)
                {
                    PollResult s = currentPollResults[idx];
                    string txt = TerminalUi.FormatPollResult(s);
                    Clipboard.SetText(txt);
                    statusLabel.Text = "Copied session details for PID " + s.ProcessId;
                }
            }
            else
            {
                statusLabel.Text = "Select a session from the list to copy.";
            }
        }

        private void OnPollTimerTick(object sender, EventArgs e)
        {
            if (systemState == SystemOperationalState.OnWatch || systemState == SystemOperationalState.OnDryRun)
            {
                RunPollCycle();
            }
        }

        private void RunPollCycle()
        {
            try
            {
                bool allowInput = (systemState == SystemOperationalState.OnWatch);
                IList<PollResult> results = engine.PollOnce(allowInput);
                UpdateSessionsList(results);
                pollCounter++;
                lastPollTime = DateTime.UtcNow;
                UpdateHeaderStats();

                foreach (PollResult r in results)
                {
                    if (r.Sent)
                    {
                        AppendLog("SEND", "Injected continuation command to PID " + r.ProcessId + " (" + r.Target + ")");
                    }
                    else if (r.Triggered)
                    {
                        AppendLog("MATCH", "Trigger active on PID " + r.ProcessId + ": " + r.Reason);
                    }
                    else if (!String.IsNullOrEmpty(r.Error))
                    {
                        AppendLog("ERROR", "Error on PID " + r.ProcessId + ": " + r.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "Poll cycle exception: " + ex.Message);
            }
        }

        private void UpdateSessionsList(IList<PollResult> results)
        {
            currentPollResults.Clear();
            currentPollResults.AddRange(results);

            sessionListView.BeginUpdate();
            sessionListView.Items.Clear();

            foreach (PollResult r in results)
            {
                var item = new ListViewItem(r.Target ?? "-");
                item.SubItems.Add(r.ProcessName ?? "-");
                item.SubItems.Add(r.ProcessId.ToString(CultureInfo.InvariantCulture));
                item.SubItems.Add(r.AttachProcessId.ToString(CultureInfo.InvariantCulture));
                item.SubItems.Add(r.Title ?? "-");
                item.SubItems.Add(r.Read ? "READ_OK" : "READ_FAIL");

                string prompt = r.Busy ? "BUSY" : (r.Ready ? "READY" : "WAITING");
                item.SubItems.Add(prompt);

                string opState;
                Color itemColor;
                if (!r.Read)
                {
                    opState = "UNREADABLE";
                    itemColor = StateRed;
                }
                else if (r.Busy)
                {
                    opState = "BUSY_GENERATING";
                    itemColor = StateYellow;
                }
                else if (r.Sent)
                {
                    opState = "INJECTED_SENT";
                    itemColor = StateGreen;
                }
                else if (r.Triggered)
                {
                    if (r.NextAttemptUtc > DateTime.UtcNow)
                    {
                        opState = "COOLDOWN_WAIT";
                        itemColor = StateCyan;
                    }
                    else
                    {
                        opState = "TRIGGER_READY";
                        itemColor = StateGreen;
                    }
                }
                else
                {
                    opState = "MONITORING_IDLE";
                    itemColor = TextGold;
                }

                item.SubItems.Add(opState);

                string next = r.NextAttemptUtc == DateTime.MinValue ? "-" : r.NextAttemptUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                item.SubItems.Add(next);
                item.SubItems.Add(r.Reason ?? "-");

                item.ForeColor = itemColor;
                sessionListView.Items.Add(item);
            }

            sessionListView.EndUpdate();
            sessionCountLabel.Text = "Sessions: " + results.Count;

            UpdateInspectorFromSelected();
        }

        private void UpdateInspectorFromSelected()
        {
            if (sessionListView.SelectedItems.Count > 0)
            {
                int idx = sessionListView.SelectedItems[0].Index;
                if (idx >= 0 && idx < currentPollResults.Count)
                {
                    PollResult s = currentPollResults[idx];
                    var sb = new StringBuilder();
                    sb.AppendLine("===============================================================================");
                    sb.AppendLine(" SESSION DEEP DIAGNOSTIC INSPECTION: PID " + s.ProcessId + " (" + (s.ProcessName ?? "unknown") + ")");
                    sb.AppendLine("===============================================================================");
                    sb.AppendLine(" Target Rule:        " + (s.Target ?? "-"));
                    sb.AppendLine(" Process ID:         " + s.ProcessId);
                    sb.AppendLine(" Attach Process ID:  " + s.AttachProcessId);
                    sb.AppendLine(" Window Title:       \"" + (s.Title ?? "-") + "\"");
                    sb.AppendLine(" Console Buffer:     " + (s.Read ? "SUCCESS (READABLE)" : "FAILED (UNREADABLE)"));
                    sb.AppendLine(" Prompt State:       " + (s.Busy ? "BUSY (Generating or User Input Present)" : (s.Ready ? "READY (Empty prompt, ready for input)" : "UNKNOWN")));
                    sb.AppendLine(" Trigger State:      " + (s.Triggered ? "TRIGGERED (Active failure pattern matched)" : "NO_TRIGGER"));
                    sb.AppendLine(" Transaction Status: WouldSend=" + s.WouldSend + " | Sent=" + s.Sent);
                    sb.AppendLine(" Next Attempt UTC:   " + (s.NextAttemptUtc == DateTime.MinValue ? "none" : s.NextAttemptUtc.ToString("o", CultureInfo.InvariantCulture)));
                    sb.AppendLine(" Decision / Reason:  " + (s.Reason ?? "-"));
                    if (!String.IsNullOrEmpty(s.Error))
                    {
                        sb.AppendLine(" Error Detail:       " + s.Error);
                    }
                    sb.AppendLine("===============================================================================");
                    inspectorTextBox.Text = sb.ToString();
                    return;
                }
            }

            inspectorTextBox.Text = "Select a terminal session from the list above to view deep diagnostic inspection.";
        }

        private void UpdateHeaderStats()
        {
            string timeStr = lastPollTime == DateTime.MinValue ? "--:--:--" : lastPollTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            statsLabel.Text = "Polls: " + pollCounter + " | " + timeStr + " UTC";
            pollStatsLabel.Text = "Polls: " + pollCounter;
        }

        private void AppendLog(string level, string message)
        {
            if (logRichText.IsDisposed) return;

            string timeStr = DateTime.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

            Color color = TextMuted;
            if (level == "ERROR") color = StateRed;
            else if (level == "WARN") color = StateYellow;
            else if (level == "SEND") color = StateGreen;
            else if (level == "MATCH") color = StateCyan;

            logRichText.SelectionStart = logRichText.TextLength;
            logRichText.SelectionLength = 0;
            logRichText.SelectionColor = Color.FromArgb(120, 110, 100);
            logRichText.AppendText(timeStr + " ");

            logRichText.SelectionColor = color;
            logRichText.AppendText(String.Format(CultureInfo.InvariantCulture, "[{0,-5}] ", level));

            logRichText.SelectionColor = Color.FromArgb(220, 210, 195);
            logRichText.AppendText(message + "\n");
            logRichText.ScrollToCaret();
        }

        private void ShowDashboard()
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.BringToFront();
        }

        private void OnFormKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5) { RunProbe(); e.Handled = true; }
            else if (e.KeyCode == Keys.F6) { SetState(SystemOperationalState.OnDryRun); e.Handled = true; }
            else if (e.KeyCode == Keys.F7) { SetState(SystemOperationalState.OnWatch); e.Handled = true; }
            else if (e.KeyCode == Keys.F8) { SetState(SystemOperationalState.Paused); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.R) { ReloadConfig(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.L) { ClearLog(); e.Handled = true; }
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
            }
        }

        public static int RunDesktopGui(WatcherConfiguration config, string configurationFilePath, string initialMode = null)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // WinForms fail-safe: a UI-thread exception must leave an auditable trace
            // instead of silently killing the desktop adapter window.
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs eventArgs)
                {
                    Program.TryWriteCrashReport(
                        "GUI thread exception: " + eventArgs.Exception.Message,
                        eventArgs.Exception.ToString(),
                        false);
                    MessageBox.Show(
                        "Recovered from an internal error; details were appended to run\\SAICONT.crash.log.",
                        "SAICONT",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                };
            }
            catch { }
            Application.Run(new SaiContGuiForm(config, configurationFilePath, initialMode));
            return 0;
        }
    }
}
