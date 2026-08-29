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
        OnWatch,
        OnDryRun,
        Paused,
        Disabled
    }

    internal sealed class SaiContGuiForm : Form
    {
        private readonly string configPath;
        private readonly DurableStateStore stateStore;
        private readonly Func<bool> shouldStop;
        private WatcherConfiguration currentConfig;
        private WatcherEngine engine;
        private SystemOperationalState systemState = SystemOperationalState.Paused;
        private int pollCounter = 0;
        private DateTime lastPollTime = DateTime.MinValue;

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

        private const int MaximumGuiLogEntries = 2000;
        private RichTextBox logRichText;
        private int guiLogEntries;
        private StatusStrip statusStrip;
        private ToolStripStatusLabel statusLabel;
        private ToolStripStatusLabel sessionCountLabel;
        private ToolStripStatusLabel stateIndicatorLabel;
        private ToolStripStatusLabel pollStatsLabel;

        private Timer pollTimer;
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;

        private static readonly Color CBackground = Color.FromArgb(26, 24, 16);
        private static readonly Color CBackgroundSoft = Color.FromArgb(35, 32, 24);
        private static readonly Color CSurface = Color.FromArgb(51, 46, 34);
        private static readonly Color CSurfaceRaised = Color.FromArgb(61, 55, 42);
        private static readonly Color CSurfaceAlt = Color.FromArgb(69, 61, 48);

        private static readonly Color CBorderDark = Color.FromArgb(16, 14, 8);
        private static readonly Color CBevelLight = Color.FromArgb(117, 102, 61);
        private static readonly Color CBorderMuted = Color.FromArgb(90, 80, 64);

        private static readonly Color CTextPrimary = Color.FromArgb(212, 200, 154);
        private static readonly Color CTextSecondary = Color.FromArgb(156, 147, 113);
        private static readonly Color CTextMuted = Color.FromArgb(110, 103, 78);

        private static readonly Color CAccentTeal = Color.FromArgb(0, 128, 128);
        private static readonly Color CAccentTealDeep = Color.FromArgb(0, 76, 76);

        private static readonly Color CSuccess = Color.FromArgb(74, 122, 32);
        private static readonly Color CWarning = Color.FromArgb(122, 122, 32);
        private static readonly Color CDanger = Color.FromArgb(122, 32, 32);
        private static readonly Color CDangerText = Color.FromArgb(214, 100, 100);

        private static readonly Color CCompareBack = Color.FromArgb(20, 18, 12);
        private static readonly Color CLink = Color.FromArgb(240, 208, 96);

        private static readonly Font FontTitle = new Font("Verdana", 14f, FontStyle.Bold);
        private static readonly Font FontHeader = new Font("Verdana", 12f, FontStyle.Bold);
        private static readonly Font FontBody = new Font("Verdana", 12f, FontStyle.Regular);
        private static readonly Font FontSecondary = new Font("Verdana", 11f, FontStyle.Regular);
        private static readonly Font FontSmall = new Font("Verdana", 10f, FontStyle.Regular);

        private readonly List<PollResult> currentPollResults = new List<PollResult>();

        public SaiContGuiForm(WatcherConfiguration config, string configurationFilePath, string initialMode = null, DurableStateStore sharedStateStore = null, Func<bool> stopPredicate = null)
        {
            configPath = configurationFilePath;
            stateStore = sharedStateStore;
            shouldStop = stopPredicate;
            currentConfig = config;
            engine = new WatcherEngine(currentConfig, stateStore);

            if (String.Equals(initialMode, "--watch", StringComparison.OrdinalIgnoreCase)) systemState = SystemOperationalState.OnWatch;
            else if (String.Equals(initialMode, "--dry-run", StringComparison.OrdinalIgnoreCase)) systemState = SystemOperationalState.OnDryRun;
            else systemState = SystemOperationalState.Paused;

            InitializeGui();
            ReloadRulesList();
            UpdateStateUi();
            RunProbe();
        }

        private void InitializeGui()
        {
            this.Text = "SAICONT";
            this.Size = new Size(780, 520);
            this.MinimumSize = new Size(640, 480);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = CBackground;
            this.ForeColor = CTextPrimary;
            this.KeyPreview = true;
            this.KeyDown += OnFormKeyDown;
            this.FormClosing += OnFormClosing;

            InitHeader();
            InitControlBar();
            InitMainContent();
            InitStatusStrip();
            InitTray();

            this.Controls.Add(mainSplit);
            this.Controls.Add(stateControlPanel);
            this.Controls.Add(headerPanel);
            this.Controls.Add(statusStrip);

            pollTimer = new Timer { Interval = Math.Max(500, currentConfig.PollIntervalMilliseconds) };
            pollTimer.Tick += OnPollTimerTick;
            pollTimer.Start();

            AppendLog("INFO", "SAICONT started. Use state buttons to select mode.");
        }

        private void InitHeader()
        {
            headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 20,
                BackColor = CSurface
            };
            headerPanel.Paint += (s, e) => DrawBevel(e.Graphics, headerPanel.ClientRectangle, true);

            titleLabel = new Label
            {
                Text = "SAICONT v1.1.0",
                Font = FontSmall,
                ForeColor = CTextPrimary,
                AutoSize = true,
                Location = new Point(4, 2)
            };

            stateBanner = new Label
            {
                Text = "PAUSED",
                Font = FontSmall,
                ForeColor = Color.Black,
                BackColor = CWarning,
                AutoSize = true,
                Location = new Point(180, 2),
                Padding = new Padding(4, 1, 4, 1)
            };

            statsLabel = new Label
            {
                Text = "Polls: 0",
                Font = FontSmall,
                ForeColor = CTextSecondary,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleRight,
                Dock = DockStyle.Right,
                Width = 120
            };

            headerPanel.Controls.Add(titleLabel);
            headerPanel.Controls.Add(stateBanner);
            headerPanel.Controls.Add(statsLabel);
        }

        private void InitControlBar()
        {
            stateControlPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 26,
                BackColor = CBackgroundSoft,
                Padding = new Padding(4, 2, 4, 2)
            };

            btnStateWatch = CreateRaisedButton("ON: Watch", (s, e) => SetState(SystemOperationalState.OnWatch));
            btnStateDryRun = CreateRaisedButton("ON: DryRun", (s, e) => SetState(SystemOperationalState.OnDryRun));
            btnStatePause = CreateRaisedButton("PAUSE", (s, e) => SetState(SystemOperationalState.Paused));
            btnStateDisable = CreateRaisedButton("STOP", (s, e) => SetState(SystemOperationalState.Disabled));

            btnProbe = CreateRaisedButton("Probe", (s, e) => RunProbe());
            btnReload = CreateRaisedButton("Reload", (s, e) => ReloadConfig());
            btnClearLog = CreateRaisedButton("Clear Log", (s, e) => ClearLog());
            btnCopy = CreateRaisedButton("Copy", (s, e) => CopySelectedSessionInfo());

            var sep = new Panel { Dock = DockStyle.Left, Width = 8, BackColor = Color.Transparent };

            stateControlPanel.Controls.Add(btnCopy);
            stateControlPanel.Controls.Add(btnClearLog);
            stateControlPanel.Controls.Add(btnReload);
            stateControlPanel.Controls.Add(btnProbe);
            stateControlPanel.Controls.Add(sep);
            stateControlPanel.Controls.Add(btnStateDisable);
            stateControlPanel.Controls.Add(btnStatePause);
            stateControlPanel.Controls.Add(btnStateDryRun);
            stateControlPanel.Controls.Add(btnStateWatch);
        }

        private void InitMainContent()
        {
            mainSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 220,
                SplitterWidth = 4,
                BackColor = CBackground
            };

            mainTabs = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = FontSmall
            };
            mainTabs.BackColor = CBackground;

            tabSessions = new TabPage("Sessions");
            tabRules = new TabPage("Rules");
            tabInspector = new TabPage("Inspector");

            InitSessionsTab();
            InitRulesTab();
            InitInspectorTab();

            mainTabs.TabPages.Add(tabSessions);
            mainTabs.TabPages.Add(tabRules);
            mainTabs.TabPages.Add(tabInspector);
            mainSplit.Panel1.Controls.Add(mainTabs);

            var logPanel = new Panel { Dock = DockStyle.Fill, BackColor = CBackground };
            var logHeader = new Label
            {
                Dock = DockStyle.Top,
                Height = 18,
                Text = " LOG",
                Font = FontSmall,
                ForeColor = CTextSecondary,
                BackColor = CCompareBack,
                TextAlign = ContentAlignment.MiddleLeft
            };

            logRichText = new RichTextBox
            {
                Dock = DockStyle.Fill,
                MaxLength = MaximumGuiLogEntries * 512,
                ReadOnly = true,
                BackColor = CCompareBack,
                ForeColor = CTextPrimary,
                Font = FontSmall,
                BorderStyle = BorderStyle.None,
                HideSelection = false
            };
            logPanel.Controls.Add(logRichText);
            logPanel.Controls.Add(logHeader);
            mainSplit.Panel2.Controls.Add(logPanel);
        }

        private void InitSessionsTab()
        {
            sessionListView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                Font = FontSmall,
                BackColor = CCompareBack,
                ForeColor = CTextPrimary
            };
            sessionListView.Columns.Add("Target", 100);
            sessionListView.Columns.Add("Process", 70);
            sessionListView.Columns.Add("PID", 50);
            sessionListView.Columns.Add("Attach", 50);
            sessionListView.Columns.Add("Status", 70);
            sessionListView.Columns.Add("Ready", 60);
            sessionListView.Columns.Add("State", 90);
            sessionListView.Columns.Add("Next", 70);
            sessionListView.Columns.Add("Reason", 160);
            sessionListView.SelectedIndexChanged += (s, e) => UpdateInspectorFromSelected();
            sessionListView.DoubleClick += (s, e) => { mainTabs.SelectedTab = tabInspector; };
            tabSessions.Controls.Add(sessionListView);
        }

        private void InitRulesTab()
        {
            var rulesPanel = new Panel { Dock = DockStyle.Fill, BackColor = CBackground };
            rulesListView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                Font = FontSmall,
                BackColor = CCompareBack,
                ForeColor = CTextPrimary
            };
            rulesListView.Columns.Add("Name", 120);
            rulesListView.Columns.Add("Enabled", 60);
            rulesListView.Columns.Add("Processes", 120);
            rulesListView.Columns.Add("Command", 60);
            rulesListView.Columns.Add("Delay", 50);
            rulesListView.Columns.Add("Retry", 50);
            rulesListView.Columns.Add("Backoff", 60);
            rulesListView.Columns.Add("Triggers", 60);

            var rulesBar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                BackColor = CBackgroundSoft,
                Padding = new Padding(4, 2, 4, 2)
            };
            btnToggleRule = CreateRaisedButton("Toggle Rule", (s, e) => ToggleSelectedRule());
            rulesBar.Controls.Add(btnToggleRule);

            rulesPanel.Controls.Add(rulesListView);
            rulesPanel.Controls.Add(rulesBar);
            tabRules.Controls.Add(rulesPanel);
        }

        private void InitInspectorTab()
        {
            inspectorTextBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                Font = FontSmall,
                BackColor = CCompareBack,
                ForeColor = CTextPrimary,
                BorderStyle = BorderStyle.None
            };
            tabInspector.Controls.Add(inspectorTextBox);
        }

        private void InitStatusStrip()
        {
            statusStrip = new StatusStrip
            {
                BackColor = CBackgroundSoft,
                ForeColor = CTextSecondary,
                Font = FontSmall
            };
            statusLabel = new ToolStripStatusLabel("Ready.") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            stateIndicatorLabel = new ToolStripStatusLabel("PAUSED") { Width = 80 };
            sessionCountLabel = new ToolStripStatusLabel("Sessions: 0") { Width = 90 };
            pollStatsLabel = new ToolStripStatusLabel("Polls: 0") { Width = 80 };

            statusStrip.Items.Add(statusLabel);
            statusStrip.Items.Add(new ToolStripSeparator());
            statusStrip.Items.Add(stateIndicatorLabel);
            statusStrip.Items.Add(new ToolStripSeparator());
            statusStrip.Items.Add(sessionCountLabel);
            statusStrip.Items.Add(new ToolStripSeparator());
            statusStrip.Items.Add(pollStatsLabel);
        }

        private void InitTray()
        {
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Open", null, (s, e) => ShowDashboard());
            trayMenu.Items.Add("Probe", null, (s, e) => RunProbe());
            trayMenu.Items.Add("Watch", null, (s, e) => SetState(SystemOperationalState.OnWatch));
            trayMenu.Items.Add("DryRun", null, (s, e) => SetState(SystemOperationalState.OnDryRun));
            trayMenu.Items.Add("Pause", null, (s, e) => SetState(SystemOperationalState.Paused));
            trayMenu.Items.Add("Stop", null, (s, e) => SetState(SystemOperationalState.Disabled));
            trayMenu.Items.Add("-");
            trayMenu.Items.Add("Exit", null, (s, e) => { trayIcon.Visible = false; Application.Exit(); });

            trayIcon = new NotifyIcon
            {
                Text = "SAICONT",
                Icon = SystemIcons.Application,
                ContextMenuStrip = trayMenu,
                Visible = true
            };
            trayIcon.DoubleClick += (s, e) => ShowDashboard();
        }

        private Button CreateRaisedButton(string text, EventHandler onClick)
        {
            var btn = new Button
            {
                Text = text,
                Font = FontSmall,
                ForeColor = CTextPrimary,
                BackColor = CSurfaceRaised,
                Height = 20,
                FlatStyle = FlatStyle.Flat,
                Dock = DockStyle.Left,
                Cursor = Cursors.Hand,
                Padding = new Padding(6, 0, 6, 0),
                Margin = new Padding(0, 0, 4, 0)
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.Paint += (s, e) =>
            {
                var g = e.Graphics;
                var r = btn.ClientRectangle;
                DrawBevel(g, r, true);
                var ts = g.MeasureString(btn.Text, btn.Font);
                using (var b = new SolidBrush(btn.ForeColor))
                    g.DrawString(btn.Text, btn.Font, b, (r.Width - ts.Width) / 2, (r.Height - ts.Height) / 2);
            };
            btn.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    btn.Parent.Refresh();
                    using (var g = btn.CreateGraphics())
                    {
                        var r = btn.ClientRectangle;
                        DrawBevel(g, r, false);
                        var ts = g.MeasureString(btn.Text, btn.Font);
                        using (var b = new SolidBrush(btn.ForeColor))
                            g.DrawString(btn.Text, btn.Font, b, (r.Width - ts.Width) / 2 + 1, (r.Height - ts.Height) / 2 + 1);
                    }
                }
            };
            btn.MouseUp += (s, e) => btn.Invalidate();
            btn.Click += onClick;
            return btn;
        }

        private static void DrawBevel(Graphics g, Rectangle r, bool raised)
        {
            Color light = raised ? CBevelLight : CBorderDark;
            Color dark = raised ? CBorderDark : CBevelLight;
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
                    "Enable Watch Mode (automated input injection)?",
                    "SAICONT",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (dr != DialogResult.Yes)
                {
                    statusLabel.Text = "Watch mode canceled.";
                    return;
                }
            }

            systemState = newState;
            UpdateStateUi();

            switch (systemState)
            {
                case SystemOperationalState.OnWatch:
                    statusLabel.Text = "Watch mode active.";
                    AppendLog("WARN", "State: ON WATCH.");
                    RunPollCycle();
                    break;
                case SystemOperationalState.OnDryRun:
                    statusLabel.Text = "Dry-run active.";
                    AppendLog("INFO", "State: ON DRY-RUN.");
                    RunPollCycle();
                    break;
                case SystemOperationalState.Paused:
                    statusLabel.Text = "Paused.";
                    AppendLog("INFO", "State: PAUSED.");
                    break;
                case SystemOperationalState.Disabled:
                    statusLabel.Text = "Disabled.";
                    AppendLog("WARN", "State: DISABLED.");
                    break;
            }
        }

        private void UpdateStateUi()
        {
            Color bannerBg;
            Color stateColor;
            string bannerText;

            switch (systemState)
            {
                case SystemOperationalState.OnWatch:
                    bannerBg = CSuccess;
                    stateColor = CSuccess;
                    bannerText = "ON WATCH";
                    stateIndicatorLabel.Text = "WATCH";
                    break;
                case SystemOperationalState.OnDryRun:
                    bannerBg = CAccentTeal;
                    stateColor = CAccentTeal;
                    bannerText = "ON DRY-RUN";
                    stateIndicatorLabel.Text = "DRY-RUN";
                    break;
                case SystemOperationalState.Paused:
                    bannerBg = CWarning;
                    stateColor = CWarning;
                    bannerText = "PAUSED";
                    stateIndicatorLabel.Text = "PAUSED";
                    break;
                default:
                    bannerBg = CDanger;
                    stateColor = CDangerText;
                    bannerText = "STOPPED";
                    stateIndicatorLabel.Text = "STOPPED";
                    break;
            }

            stateBanner.Text = bannerText;
            stateBanner.BackColor = bannerBg;
            stateBanner.ForeColor = (systemState == SystemOperationalState.Paused) ? Color.Black : Color.White;
            stateIndicatorLabel.ForeColor = stateColor;
        }

        private void ReloadRulesList()
        {
            rulesListView.Items.Clear();
            foreach (TargetRule rule in currentConfig.Targets)
            {
                var item = new ListViewItem(rule.Name);
                item.SubItems.Add(rule.Enabled ? "ON" : "OFF");
                item.SubItems.Add(String.Join(",", rule.ProcessNames));
                item.SubItems.Add(rule.Command);
                item.SubItems.Add(rule.InitialDelaySeconds + "s");
                item.SubItems.Add(rule.RetryIntervalSeconds + "s");
                item.SubItems.Add(rule.BackoffMultiplier + "x");
                item.SubItems.Add(rule.TriggerPatterns.Length.ToString(CultureInfo.InvariantCulture));
                item.ForeColor = rule.Enabled ? CSuccess : CTextMuted;
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
                    AppendLog("INFO", "Rule '" + r.Name + "' set to: " + (r.Enabled ? "ON" : "OFF"));
                    statusLabel.Text = "Rule '" + r.Name + "' " + (r.Enabled ? "ON" : "OFF");
                }
            }
            else
            {
                statusLabel.Text = "Select a rule to toggle.";
            }
        }

        private void RunProbe()
        {
            statusLabel.Text = "Probing...";
            this.Cursor = Cursors.WaitCursor;
            try
            {
                IList<PollResult> results = engine.PollOnce(false);
                UpdateSessionsList(results);
                pollCounter++;
                lastPollTime = DateTime.UtcNow;
                UpdateHeaderStats();
                statusLabel.Text = "Probed: " + results.Count + " session(s).";
                AppendLog("INFO", "Probe: " + results.Count + " session(s).");
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
                ReloadRulesList();
                pollTimer.Interval = Math.Max(500, currentConfig.PollIntervalMilliseconds);
                statusLabel.Text = "Config reloaded (" + currentConfig.Targets.Count + " rules).";
                AppendLog("INFO", "Config reloaded.");
                RunProbe();
            }
            catch (Exception ex)
            {
                statusLabel.Text = "Reload error: " + ex.Message;
                AppendLog("ERROR", "Reload failed: " + ex.Message);
            }
        }

        private void ClearLog()
        {
            logRichText.Clear();
            guiLogEntries = 0;
            statusLabel.Text = "Log cleared.";
        }

        private void CopySelectedSessionInfo()
        {
            if (sessionListView.SelectedItems.Count > 0)
            {
                int idx = sessionListView.SelectedItems[0].Index;
                if (idx >= 0 && idx < currentPollResults.Count)
                {
                    PollResult s = currentPollResults[idx];
                    string txt = TerminalUi.FormatPollResult(s);
                    Clipboard.SetText(txt);
                    statusLabel.Text = "Copied PID " + s.ProcessId + ".";
                }
            }
            else
            {
                statusLabel.Text = "Select a session to copy.";
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
                if (shouldStop != null && shouldStop())
                {
                    systemState = SystemOperationalState.Disabled;
                    pollTimer.Stop();
                    Close();
                    return;
                }
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
                        AppendLog("SEND", "Injected to PID " + r.ProcessId + " (" + r.Target + ")");
                    }
                    else if (r.Triggered)
                    {
                        AppendLog("MATCH", "Trigger PID " + r.ProcessId + ": " + r.Reason);
                    }
                    else if (!String.IsNullOrEmpty(r.Error))
                    {
                        AppendLog("ERROR", "Error PID " + r.ProcessId + ": " + r.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "Poll cycle: " + ex.Message);
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
                item.SubItems.Add(r.Read ? "OK" : "FAIL");
                item.SubItems.Add(r.Busy ? "BUSY" : (r.Ready ? "READY" : "WAIT"));

                string opState;
                Color itemColor;
                if (!r.Read)
                {
                    opState = "UNREADABLE";
                    itemColor = CDangerText;
                }
                else if (r.Busy)
                {
                    opState = "BUSY";
                    itemColor = CWarning;
                }
                else if (r.Sent)
                {
                    opState = "INJECTED";
                    itemColor = CSuccess;
                }
                else if (r.Triggered)
                {
                    if (r.NextAttemptUtc > DateTime.UtcNow)
                    {
                        opState = "COOLDOWN";
                        itemColor = CAccentTeal;
                    }
                    else
                    {
                        opState = "READY";
                        itemColor = CSuccess;
                    }
                }
                else
                {
                    opState = "IDLE";
                    itemColor = CTextPrimary;
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
                    sb.AppendLine("SESSION INSPECTOR: PID " + s.ProcessId + " (" + (s.ProcessName ?? "-") + ")");
                    sb.AppendLine("Target: " + (s.Target ?? "-"));
                    sb.AppendLine("Attach PID: " + s.AttachProcessId);
                    sb.AppendLine("Window: " + (s.Title ?? "-"));
                    sb.AppendLine("Console: " + (s.Read ? "OK" : "FAIL"));
                    sb.AppendLine("Prompt: " + (s.Busy ? "BUSY" : (s.Ready ? "READY" : "UNKNOWN")));
                    sb.AppendLine("Trigger: " + (s.Triggered ? "YES" : "NO"));
                    sb.AppendLine("Sent: " + s.Sent);
                    sb.AppendLine("Next: " + (s.NextAttemptUtc == DateTime.MinValue ? "none" : s.NextAttemptUtc.ToString("o", CultureInfo.InvariantCulture)));
                    sb.AppendLine("Reason: " + (s.Reason ?? "-"));
                    if (!String.IsNullOrEmpty(s.Error))
                    {
                        sb.AppendLine("Error: " + s.Error);
                    }
                    inspectorTextBox.Text = sb.ToString();
                    return;
                }
            }
            inspectorTextBox.Text = "Select a session above.";
        }

        private void UpdateHeaderStats()
        {
            string timeStr = lastPollTime == DateTime.MinValue ? "--:--" : lastPollTime.ToString("HH:mm", CultureInfo.InvariantCulture);
            statsLabel.Text = "Polls: " + pollCounter + " | " + timeStr;
            pollStatsLabel.Text = "Polls: " + pollCounter;
        }

        private void AppendLog(string level, string message)
        {
            if (logRichText.IsDisposed) return;

            string timeStr = DateTime.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

            Color color = CTextMuted;
            if (level == "ERROR") color = CDangerText;
            else if (level == "WARN") color = CWarning;
            else if (level == "SEND") color = CSuccess;
            else if (level == "MATCH") color = CAccentTeal;

            logRichText.SelectionStart = logRichText.TextLength;
            logRichText.SelectionLength = 0;
            logRichText.SelectionColor = CTextMuted;
            logRichText.AppendText(timeStr + " ");

            logRichText.SelectionColor = color;
            logRichText.AppendText(String.Format(CultureInfo.InvariantCulture, "[{0,-5}] ", level));

            logRichText.SelectionColor = CTextPrimary;
            logRichText.AppendText(message + "\n");
            guiLogEntries++;
            if (guiLogEntries > MaximumGuiLogEntries)
            {
                int removeEntries = Math.Min(100, guiLogEntries - MaximumGuiLogEntries + 100);
                int removeEnd = 0;
                for (int index = 0; index < removeEntries; index++)
                {
                    int newline = logRichText.Text.IndexOf('\n', removeEnd);
                    if (newline < 0)
                    {
                        removeEnd = logRichText.TextLength;
                        break;
                    }
                    removeEnd = newline + 1;
                }
                if (removeEnd > 0)
                {
                    logRichText.Select(0, removeEnd);
                    logRichText.SelectedText = String.Empty;
                    guiLogEntries -= removeEntries;
                }
            }
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

        public static int RunDesktopGui(WatcherConfiguration config, string configurationFilePath, string initialMode = null, DurableStateStore sharedStateStore = null, Func<bool> stopPredicate = null)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
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
                        "Internal error. Details in run\\SAICONT.crash.log.",
                        "SAICONT",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                };
            }
            catch { }
            Application.Run(new SaiContGuiForm(config, configurationFilePath, initialMode, sharedStateStore, stopPredicate));
            return 0;
        }
    }
}
