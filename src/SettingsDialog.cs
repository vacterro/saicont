using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace SaiCont
{
    internal sealed class SettingsDialog : Form
    {
        private readonly string configPath;
        private WatcherConfiguration draftConfig;
        // W2L1-001: the baseline Reset returns to. It is NOT readonly on
        // purpose: after a successful Reload from disk the reset baseline has to
        // become that newly loaded configuration, otherwise Reset resurrects
        // constructor-era values the user never asked for.
        private WatcherConfiguration resetBaseline;
        // W2L1-001: the target whose editor controls are currently loaded.
        // Selection changes must capture THIS index before loading another, and
        // programmatic list rebuilds must not run that capture.
        private int loadedTargetIndex = -1;
        private bool suppressTargetCapture;

        private TabControl tabs;
        private TabPage tabGlobal;
        private TabPage tabTarget;
        private TabPage tabPatterns;

        private NumericUpDown numPollInterval;
        private TextBox txtLogPath;
        private NumericUpDown numLogMaxBytes;
        private NumericUpDown numLogRetained;
        private NumericUpDown numLogDuplicateWindow;
        private Label lblGlobalError;

        private ListBox lstTargets;
        private Label lblTargetName;
        private CheckBox chkEnabled;
        private ComboBox cmbTransport;
        private TextBox txtEndpoint;
        private TextBox txtProjectDir;
        private TextBox txtCommand;
        private NumericUpDown numScanLines;
        private NumericUpDown numMaxTriggerDistance;
        private NumericUpDown numInitialDelay;
        private NumericUpDown numRetryInterval;
        private NumericUpDown numMaxRetryInterval;
        private NumericUpDown numMaxAttempts;
        private NumericUpDown numBackoffNumerator;
        private NumericUpDown numBackoffDenominator;
        private CheckBox chkParseRetryTime;
        private Label lblTargetError;

        private TextBox txtTriggerPatterns;
        private TextBox txtReadyPatterns;
        private TextBox txtBusyPatterns;
        private Label lblPatternsError;
        private Label lblTriggerCount;
        private Label lblReadyCount;
        private Label lblBusyCount;

        private Button btnApply;
        private Button btnCancel;
        private Button btnReload;
        private Button btnReset;

        private static readonly Color CBackground = Color.FromArgb(26, 24, 16);
        private static readonly Color CBackgroundSoft = Color.FromArgb(35, 32, 24);
        private static readonly Color CSurface = Color.FromArgb(51, 46, 34);
        private static readonly Color CSurfaceRaised = Color.FromArgb(61, 55, 42);
        private static readonly Color CBorderDark = Color.FromArgb(16, 14, 8);
        private static readonly Color CBevelLight = Color.FromArgb(117, 102, 61);
        private static readonly Color CTextPrimary = Color.FromArgb(212, 200, 154);
        private static readonly Color CTextSecondary = Color.FromArgb(156, 147, 113);
        private static readonly Color CTextMuted = Color.FromArgb(110, 103, 78);
        private static readonly Color CDangerText = Color.FromArgb(214, 100, 100);
        private static readonly Color CCompareBack = Color.FromArgb(20, 18, 12);

        private static readonly Font FontBody = new Font("Verdana", 12f, FontStyle.Regular);
        private static readonly Font FontSmall = new Font("Verdana", 10f, FontStyle.Regular);
        private static readonly Font FontBold = new Font("Verdana", 12f, FontStyle.Bold);

        public WatcherConfiguration AppliedConfig { get; private set; }
        public bool DidApply { get; private set; }

        public SettingsDialog(WatcherConfiguration current, string configurationFilePath)
        {
            configPath = configurationFilePath;
            resetBaseline = CloneConfig(current);
            draftConfig = CloneConfig(current);
            InitializeComponents();
            PopulateFromDraft();
            BindTargetList();
            if (lstTargets.Items.Count > 0)
            {
                lstTargets.SelectedIndex = 0;
            }
        }

        private void InitializeComponents()
        {
            Text = "SAICONT Settings";
            ClientSize = new Size(640, 480);
            MinimumSize = new Size(640, 480);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = CBackground;
            ForeColor = CTextPrimary;
            Font = FontBody;
            KeyPreview = true;
            KeyDown += OnDialogKey;

            tabs = new TabControl
            {
                Dock = DockStyle.Fill,
                DrawMode = TabDrawMode.OwnerDrawFixed,
                Padding = new Point(8, 2),
                ItemSize = new Size(80, 18),
                BackColor = CBackground,
                Font = FontSmall
            };
            tabs.DrawItem += DrawTabItem;

            tabGlobal = new TabPage("Global");
            tabTarget = new TabPage("Target");
            tabPatterns = new TabPage("Patterns");
            tabGlobal.BackColor = CBackground;
            tabTarget.BackColor = CBackground;
            tabPatterns.BackColor = CBackground;

            BuildGlobalTab();
            BuildTargetTab();
            BuildPatternsTab();

            tabs.TabPages.Add(tabGlobal);
            tabs.TabPages.Add(tabTarget);
            tabs.TabPages.Add(tabPatterns);

            var buttonBar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 32,
                BackColor = CBackgroundSoft,
                Padding = new Padding(4, 4, 4, 4)
            };
            btnApply = CreateRaisedButton("Apply", (s, e) => OnApply());
            btnCancel = CreateRaisedButton("Cancel", (s, e) => { DialogResult = DialogResult.Cancel; Close(); });
            btnReload = CreateRaisedButton("Reload From Disk", (s, e) => OnReloadFromDisk());
            btnReset = CreateRaisedButton("Reset Draft", (s, e) => OnResetDraft());
            buttonBar.Controls.Add(btnReset);
            buttonBar.Controls.Add(btnReload);
            buttonBar.Controls.Add(btnCancel);
            buttonBar.Controls.Add(btnApply);

            Controls.Add(tabs);
            Controls.Add(buttonBar);
        }

        private void BuildGlobalTab()
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 6,
                BackColor = CBackground,
                Padding = new Padding(8)
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            numPollInterval = NewNumeric(WatcherRuleLimits.PollIntervalMillisecondsMin, WatcherRuleLimits.PollIntervalMillisecondsMax, 250);
            txtLogPath = NewTextBox();
            numLogMaxBytes = NewNumeric(WatcherRuleLimits.LogMaximumBytesMin, WatcherRuleLimits.LogMaximumBytesMax / 1024, 1024);
            numLogRetained = NewNumeric(WatcherRuleLimits.LogRetainedFilesMin, WatcherRuleLimits.LogRetainedFilesMax, 1);
            numLogDuplicateWindow = NewNumeric(WatcherRuleLimits.LogDuplicateWindowSecondsMin, WatcherRuleLimits.LogDuplicateWindowSecondsMax, 1);

            numLogMaxBytes.Maximum = WatcherRuleLimits.LogMaximumBytesMax / 1024;

            AddRow(panel, 0, "Poll interval (ms)", numPollInterval);
            AddRow(panel, 1, "Log file path", txtLogPath);
            AddRow(panel, 2, "Log max bytes (KiB)", numLogMaxBytes);
            AddRow(panel, 3, "Log retained files", numLogRetained);
            AddRow(panel, 4, "Log duplicate window (s)", numLogDuplicateWindow);
            lblGlobalError = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = CDangerText,
                Font = FontSmall,
                Text = String.Empty
            };
            panel.Controls.Add(lblGlobalError, 0, 5);
            panel.SetColumnSpan(lblGlobalError, 2);

            tabGlobal.Controls.Add(panel);
        }

        private void BuildTargetTab()
        {
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 140,
                SplitterWidth = 4,
                BackColor = CBackground
            };

            lstTargets = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = CCompareBack,
                ForeColor = CTextPrimary,
                Font = FontSmall,
                BorderStyle = BorderStyle.None,
                IntegralHeight = false
            };
            lstTargets.SelectedIndexChanged += (s, e) => OnTargetSelectionChanged();
            split.Panel1.Controls.Add(lstTargets);

            var form = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 14,
                BackColor = CBackground,
                Padding = new Padding(8),
                AutoScroll = true
            };
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            lblTargetName = new Label { Text = "-", Font = FontBold, ForeColor = CTextPrimary, AutoSize = true };
            chkEnabled = new CheckBox { Text = "Enabled", ForeColor = CTextPrimary, Font = FontBody, BackColor = CBackground, FlatStyle = FlatStyle.Flat };
            cmbTransport = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, BackColor = CCompareBack, ForeColor = CTextPrimary, Font = FontBody };
            cmbTransport.Items.AddRange(new object[] { "console", "opencode-http" });
            txtEndpoint = NewTextBox();
            txtProjectDir = NewTextBox();
            txtCommand = NewTextBox();
            numScanLines = NewNumeric(WatcherRuleLimits.ScanLinesMin, NativeConsole.MaximumScanLines, 10);
            numMaxTriggerDistance = NewNumeric(WatcherRuleLimits.MaximumTriggerDistanceLinesMin, WatcherRuleLimits.MaximumTriggerDistanceLinesMax, 1);
            numInitialDelay = NewNumeric(WatcherRuleLimits.InitialDelaySecondsMin, WatcherRuleLimits.InitialDelaySecondsMax, 1);
            numRetryInterval = NewNumeric(WatcherRuleLimits.RetryIntervalSecondsMin, WatcherRuleLimits.RetryIntervalSecondsMax, 1);
            numMaxRetryInterval = NewNumeric(WatcherRuleLimits.MaxRetryIntervalSecondsMin, WatcherRuleLimits.MaxRetryIntervalSecondsMax, 1);
            numMaxAttempts = NewNumeric(WatcherRuleLimits.MaxAttemptsMin, WatcherRuleLimits.MaxAttemptsMax, 1);
            numBackoffNumerator = NewNumeric((int)(WatcherRuleLimits.BackoffMultiplierMin * 10), (int)(WatcherRuleLimits.BackoffMultiplierMax * 10), 1);
            numBackoffDenominator = NewNumeric(1, 10, 1);
            numBackoffDenominator.Value = 10;
            chkParseRetryTime = new CheckBox { Text = "Parse retry time from message", ForeColor = CTextPrimary, Font = FontBody, BackColor = CBackground, FlatStyle = FlatStyle.Flat };

            int row = 0;
            form.Controls.Add(lblTargetName, 0, row); form.SetColumnSpan(lblTargetName, 2); row++;
            form.Controls.Add(chkEnabled, 0, row); form.SetColumnSpan(chkEnabled, 2); row++;
            AddRow(form, row++, "Transport", cmbTransport);
            AddRow(form, row++, "Endpoint (http)", txtEndpoint);
            AddRow(form, row++, "Project directory", txtProjectDir);
            AddRow(form, row++, "Command", txtCommand);
            AddRow(form, row++, "Scan lines", numScanLines);
            AddRow(form, row++, "Max trigger distance (lines)", numMaxTriggerDistance);
            AddRow(form, row++, "Initial delay (s)", numInitialDelay);
            AddRow(form, row++, "Retry interval (s)", numRetryInterval);
            AddRow(form, row++, "Max retry interval (s)", numMaxRetryInterval);
            AddRow(form, row++, "Max attempts per event", numMaxAttempts);
            var backoffRow = new TableLayoutPanel { ColumnCount = 3, Dock = DockStyle.Fill, BackColor = CBackground };
            backoffRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            backoffRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
            backoffRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
            backoffRow.Controls.Add(new Label { Text = "Backoff multiplier (x10 / 10)", ForeColor = CTextSecondary, Font = FontSmall, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            backoffRow.Controls.Add(numBackoffNumerator, 1, 0);
            backoffRow.Controls.Add(numBackoffDenominator, 2, 0);
            form.Controls.Add(backoffRow, 0, row); form.SetColumnSpan(backoffRow, 2); row++;
            form.Controls.Add(chkParseRetryTime, 0, row); form.SetColumnSpan(chkParseRetryTime, 2); row++;
            lblTargetError = new Label { Dock = DockStyle.Fill, ForeColor = CDangerText, Font = FontSmall, Text = String.Empty };
            form.Controls.Add(lblTargetError, 0, row); form.SetColumnSpan(lblTargetError, 2);

            split.Panel2.Controls.Add(form);
            tabTarget.Controls.Add(split);
        }

        private void BuildPatternsTab()
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 4,
                BackColor = CBackground,
                Padding = new Padding(8)
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            txtTriggerPatterns = NewMultiline();
            txtReadyPatterns = NewMultiline();
            txtBusyPatterns = NewMultiline();
            lblTriggerCount = new Label { ForeColor = CTextMuted, Font = FontSmall, Text = "0/32" };
            lblReadyCount = new Label { ForeColor = CTextMuted, Font = FontSmall, Text = "0/16" };
            lblBusyCount = new Label { ForeColor = CTextMuted, Font = FontSmall, Text = "0/16" };

            AddRow(panel, 0, "Trigger patterns", WrapStack(txtTriggerPatterns, lblTriggerCount));
            AddRow(panel, 1, "Ready patterns", WrapStack(txtReadyPatterns, lblReadyCount));
            AddRow(panel, 2, "Busy patterns", WrapStack(txtBusyPatterns, lblBusyCount));
            lblPatternsError = new Label { Dock = DockStyle.Fill, ForeColor = CDangerText, Font = FontSmall, Text = String.Empty };
            panel.Controls.Add(lblPatternsError, 0, 3);
            panel.SetColumnSpan(lblPatternsError, 2);

            tabPatterns.Controls.Add(panel);
        }

        private static Panel WrapStack(Control big, Control small)
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = CBackground, Padding = new Padding(0) };
            p.Controls.Add(big);
            p.Controls.Add(small);
            small.Dock = DockStyle.Bottom;
            big.Dock = DockStyle.Fill;
            return p;
        }

        private static void AddRow(TableLayoutPanel panel, int row, string label, Control editor)
        {
            var l = new Label
            {
                Text = label,
                ForeColor = CTextSecondary,
                Font = FontBody,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };
            panel.Controls.Add(l, 0, row);
            panel.Controls.Add(editor, 1, row);
        }

        private static NumericUpDown NewNumeric(int min, int max, int step)
        {
            return new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                Increment = step,
                BackColor = CCompareBack,
                ForeColor = CTextPrimary,
                Font = FontBody,
                BorderStyle = BorderStyle.FixedSingle,
                Dock = DockStyle.Fill
            };
        }

        private static TextBox NewTextBox()
        {
            return new TextBox
            {
                BackColor = CCompareBack,
                ForeColor = CTextPrimary,
                Font = FontBody,
                BorderStyle = BorderStyle.FixedSingle,
                Dock = DockStyle.Fill
            };
        }

        private static TextBox NewMultiline()
        {
            return new TextBox
            {
                BackColor = CCompareBack,
                ForeColor = CTextPrimary,
                Font = new Font("Verdana", 11f, FontStyle.Regular),
                BorderStyle = BorderStyle.FixedSingle,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                AcceptsReturn = true,
                WordWrap = false,
                Dock = DockStyle.Fill,
                Height = 80
            };
        }

        private Button CreateRaisedButton(string text, EventHandler onClick)
        {
            var btn = new Button
            {
                Text = text,
                Font = FontSmall,
                ForeColor = CTextPrimary,
                BackColor = CSurfaceRaised,
                Height = 22,
                FlatStyle = FlatStyle.Flat,
                Dock = DockStyle.Right,
                Width = 120,
                Cursor = Cursors.Hand,
                Margin = new Padding(4, 0, 0, 0)
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

        private void DrawTabItem(object sender, DrawItemEventArgs e)
        {
            bool isSelected = e.Index == tabs.SelectedIndex;
            Rectangle bounds = e.Bounds;
            using (var fill = new SolidBrush(isSelected ? CSurface : CSurfaceRaised))
                e.Graphics.FillRectangle(fill, bounds);
            DrawBevel(e.Graphics, bounds, !isSelected);
            string text = tabs.TabPages[e.Index].Text;
            using (var brush = new SolidBrush(isSelected ? CTextPrimary : CTextSecondary))
            {
                SizeF textSize = e.Graphics.MeasureString(text, FontSmall);
                e.Graphics.DrawString(text, FontSmall, brush, bounds.Left + (bounds.Width - textSize.Width) / 2f, bounds.Top + (bounds.Height - textSize.Height) / 2f);
            }
        }

        private void PopulateFromDraft()
        {
            numPollInterval.Value = Clamp(draftConfig.PollIntervalMilliseconds, (int)numPollInterval.Minimum, (int)numPollInterval.Maximum);
            txtLogPath.Text = draftConfig.LogFilePath ?? String.Empty;
            numLogMaxBytes.Value = Clamp((int)(draftConfig.LogMaximumBytes / 1024), (int)numLogMaxBytes.Minimum, (int)numLogMaxBytes.Maximum);
            numLogRetained.Value = Clamp(draftConfig.LogRetainedFiles, (int)numLogRetained.Minimum, (int)numLogRetained.Maximum);
            numLogDuplicateWindow.Value = Clamp(draftConfig.LogDuplicateWindowSeconds, (int)numLogDuplicateWindow.Minimum, (int)numLogDuplicateWindow.Maximum);
        }

        private void BindTargetList()
        {
            // W2L1-001: a programmatic rebuild raises SelectedIndexChanged while
            // the list contents are inconsistent with loadedTargetIndex. Suppress
            // the capture across the rebuild, and forget the loaded index so the
            // next selection loads rather than captures.
            suppressTargetCapture = true;
            try
            {
                lstTargets.BeginUpdate();
                lstTargets.Items.Clear();
                for (int i = 0; i < draftConfig.Targets.Count; i++)
                {
                    lstTargets.Items.Add(draftConfig.Targets[i].Name);
                }
                lstTargets.EndUpdate();
            }
            finally
            {
                suppressTargetCapture = false;
            }
            loadedTargetIndex = -1;
        }

        // W2L1-001: the draft synchronization boundary. Capture the OUTGOING
        // target's editors into its draft record, then load the incoming one.
        // Without this, editing target A and selecting B silently discarded A's
        // edits, and Apply only ever captured whichever target was selected last.
        private void OnTargetSelectionChanged()
        {
            if (suppressTargetCapture) return;
            int incoming = lstTargets.SelectedIndex;
            if (loadedTargetIndex >= 0 && loadedTargetIndex != incoming)
            {
                CaptureTargetEditors(loadedTargetIndex);
            }
            LoadSelectedTarget();
        }

        private void LoadSelectedTarget()
        {
            int idx = lstTargets.SelectedIndex;
            if (idx < 0 || idx >= draftConfig.Targets.Count)
            {
                lblTargetName.Text = "-";
                loadedTargetIndex = -1;
                return;
            }
            TargetRule t = draftConfig.Targets[idx];
            lblTargetName.Text = t.Name;
            chkEnabled.Checked = t.Enabled;
            cmbTransport.SelectedItem = t.Transport ?? "console";
            txtEndpoint.Text = t.Endpoint ?? String.Empty;
            txtProjectDir.Text = t.ProjectDirectory ?? String.Empty;
            txtCommand.Text = t.Command ?? String.Empty;
            numScanLines.Value = Clamp(t.ScanLines, (int)numScanLines.Minimum, (int)numScanLines.Maximum);
            numMaxTriggerDistance.Value = Clamp(t.MaximumTriggerDistanceLines, (int)numMaxTriggerDistance.Minimum, (int)numMaxTriggerDistance.Maximum);
            numInitialDelay.Value = Clamp(t.InitialDelaySeconds, (int)numInitialDelay.Minimum, (int)numInitialDelay.Maximum);
            numRetryInterval.Value = Clamp(t.RetryIntervalSeconds, (int)numRetryInterval.Minimum, (int)numRetryInterval.Maximum);
            numMaxRetryInterval.Value = Clamp(t.MaximumRetryIntervalSeconds, (int)numMaxRetryInterval.Minimum, (int)numMaxRetryInterval.Maximum);
            numMaxAttempts.Value = Clamp(t.MaximumAttemptsPerEvent, (int)numMaxAttempts.Minimum, (int)numMaxAttempts.Maximum);
            numBackoffNumerator.Value = Clamp((int)Math.Round(t.BackoffMultiplier * 10), (int)numBackoffNumerator.Minimum, (int)numBackoffNumerator.Maximum);
            numBackoffDenominator.Value = 10;
            chkParseRetryTime.Checked = t.ParseRetryTime;
            txtTriggerPatterns.Text = string.Join(Environment.NewLine, t.TriggerPatterns ?? new string[0]);
            txtReadyPatterns.Text = string.Join(Environment.NewLine, t.ReadyPatterns ?? new string[0]);
            txtBusyPatterns.Text = string.Join(Environment.NewLine, t.BusyPatterns ?? new string[0]);
            UpdatePatternCounts();
            lblTargetError.Text = String.Empty;
            lblPatternsError.Text = String.Empty;
            loadedTargetIndex = idx;
        }

        private void UpdatePatternCounts()
        {
            int trig = CountNonEmptyLines(txtTriggerPatterns.Text);
            int ready = CountNonEmptyLines(txtReadyPatterns.Text);
            int busy = CountNonEmptyLines(txtBusyPatterns.Text);
            lblTriggerCount.Text = trig + "/" + WatcherRuleLimits.TriggerPatternsMax;
            lblReadyCount.Text = ready + "/" + WatcherRuleLimits.ReadyPatternsMax;
            lblBusyCount.Text = busy + "/" + WatcherRuleLimits.BusyPatternsMax;
            lblTriggerCount.ForeColor = trig > WatcherRuleLimits.TriggerPatternsMax ? CDangerText : CTextMuted;
            lblReadyCount.ForeColor = ready > WatcherRuleLimits.ReadyPatternsMax ? CDangerText : CTextMuted;
            lblBusyCount.ForeColor = busy > WatcherRuleLimits.BusyPatternsMax ? CDangerText : CTextMuted;
        }

        private static int CountNonEmptyLines(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int n = 0;
            foreach (var line in s.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (!string.IsNullOrWhiteSpace(line)) n++;
            }
            return n;
        }

        private bool ValidateDraft(out string error)
        {
            return ValidateDraftConfig(draftConfig, out error);
        }

        // T-152: the Apply gate, callable without a Form. OnApply must never
        // write a configuration that this rejects -- that IS the rollback:
        // the on-disk file is left byte-identical when validation fails.
        //
        // W2L1-002: target validation alone was NOT the loader's contract. An
        // empty Log path serialized as path="" and WatcherConfiguration.ReadString
        // rejects an empty attribute, so Apply reported success and left a file
        // the next start could not load. The authoritative check is therefore the
        // loader itself, run against the exact bytes Apply is about to commit.
        internal static bool ValidateDraftConfig(WatcherConfiguration config, out string error)
        {
            error = null;
            if (config == null || config.Targets == null || config.Targets.Count == 0)
            {
                error = "At least one target is required.";
                return false;
            }
            for (int i = 0; i < config.Targets.Count; i++)
            {
                TargetRule t = config.Targets[i];
                try
                {
                    WatcherConfiguration.ValidateTarget(t);
                }
                catch (FormatException ex)
                {
                    error = "Target '" + t.Name + "': " + ex.Message;
                    return false;
                }
            }
            return ValidateThroughLoader(config, out error);
        }

        /// <summary>
        /// W2L1-002: serialize the draft and parse it back through
        /// WatcherConfiguration.Load -- the same routine the watcher, GUI and TUI
        /// use at startup. Anything the loader refuses is refused here, before any
        /// byte of the destination file is touched. The probe writes to a private
        /// temp directory so relative-path semantics still resolve the way the
        /// real file's directory would, and it is deleted either way.
        /// </summary>
        internal static bool ValidateThroughLoader(WatcherConfiguration config, out string error)
        {
            error = null;
            string probeDirectory = null;
            try
            {
                string xml = SerializeConfig(config);
                probeDirectory = Path.Combine(Path.GetTempPath(), "SAICONT.cfgprobe." + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(probeDirectory);
                string probePath = Path.Combine(probeDirectory, "SAICONT.config.xml");
                File.WriteAllText(probePath, xml, new UTF8Encoding(false));
                WatcherConfiguration.Load(probePath);
                return true;
            }
            catch (Exception ex)
            {
                error = "Configuration would not load: " + ex.Message;
                return false;
            }
            finally
            {
                if (probeDirectory != null)
                {
                    try { Directory.Delete(probeDirectory, true); } catch { }
                }
            }
        }

        private void OnApply()
        {
            CaptureDraftToModel();
            string error;
            if (!ValidateDraft(out error))
            {
                lblGlobalError.Text = error;
                MessageBox.Show(this, error, "Settings invalid", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string saveError;
            if (!TryWriteConfig(draftConfig, out saveError))
            {
                lblGlobalError.Text = saveError;
                MessageBox.Show(this, "Save failed: " + saveError, "Settings save error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            AppliedConfig = draftConfig;
            DidApply = true;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void OnReloadFromDisk()
        {
            try
            {
                WatcherConfiguration fromDisk = WatcherConfiguration.Load(configPath);
                draftConfig = fromDisk;
                // W2L1-001: rebase BOTH the working draft and the reset baseline
                // on the newly loaded file. Leaving the baseline behind made
                // Reset restore the configuration that existed when the dialog
                // opened, undoing a reload the user explicitly asked for.
                resetBaseline = CloneConfig(fromDisk);
                PopulateFromDraft();
                BindTargetList();
                if (lstTargets.Items.Count > 0) lstTargets.SelectedIndex = 0;
                lblGlobalError.Text = "Reloaded from disk.";
            }
            catch (Exception ex)
            {
                lblGlobalError.Text = "Reload failed: " + ex.Message;
            }
        }

        private void OnResetDraft()
        {
            draftConfig = CloneConfig(resetBaseline);
            PopulateFromDraft();
            BindTargetList();
            if (lstTargets.Items.Count > 0) lstTargets.SelectedIndex = 0;
            lblGlobalError.Text = String.Empty;
            lblTargetError.Text = String.Empty;
            lblPatternsError.Text = String.Empty;
        }

        private void OnDialogKey(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.Enter) { OnApply(); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
        }

        private void CaptureDraftToModel()
        {
            draftConfig.PollIntervalMilliseconds = (int)numPollInterval.Value;
            draftConfig.LogFilePath = string.IsNullOrWhiteSpace(txtLogPath.Text) ? null : txtLogPath.Text.Trim();
            draftConfig.LogMaximumBytes = (long)numLogMaxBytes.Value * 1024L;
            draftConfig.LogRetainedFiles = (int)numLogRetained.Value;
            draftConfig.LogDuplicateWindowSeconds = (int)numLogDuplicateWindow.Value;

            CaptureTargetEditors(loadedTargetIndex >= 0 ? loadedTargetIndex : lstTargets.SelectedIndex);
        }

        // W2L1-001: copy the target editor controls into ONE draft record. The
        // index is explicit so the selection handler can flush the outgoing
        // target rather than whichever one happens to be selected now.
        private void CaptureTargetEditors(int idx)
        {
            if (idx >= 0 && idx < draftConfig.Targets.Count)
            {
                TargetRule t = draftConfig.Targets[idx];
                t.Enabled = chkEnabled.Checked;
                t.Transport = (cmbTransport.SelectedItem as string) ?? "console";
                t.Endpoint = string.IsNullOrWhiteSpace(txtEndpoint.Text) ? null : txtEndpoint.Text.Trim();
                t.ProjectDirectory = string.IsNullOrWhiteSpace(txtProjectDir.Text) ? null : txtProjectDir.Text.Trim();
                t.Command = (txtCommand.Text ?? String.Empty).Trim();
                t.ScanLines = (int)numScanLines.Value;
                t.MaximumTriggerDistanceLines = (int)numMaxTriggerDistance.Value;
                t.InitialDelaySeconds = (int)numInitialDelay.Value;
                t.RetryIntervalSeconds = (int)numRetryInterval.Value;
                t.MaximumRetryIntervalSeconds = (int)numMaxRetryInterval.Value;
                t.MaximumAttemptsPerEvent = (int)numMaxAttempts.Value;
                double denom = (double)numBackoffDenominator.Value;
                if (denom < 1) denom = 1;
                t.BackoffMultiplier = Math.Max(WatcherRuleLimits.BackoffMultiplierMin, Math.Min(WatcherRuleLimits.BackoffMultiplierMax, (double)numBackoffNumerator.Value / denom));
                t.ParseRetryTime = chkParseRetryTime.Checked;
                t.TriggerPatterns = SplitLines(txtTriggerPatterns.Text);
                t.ReadyPatterns = SplitLines(txtReadyPatterns.Text);
                t.BusyPatterns = SplitLines(txtBusyPatterns.Text);
                try { t.CompileRegexes(); } catch (FormatException ex) { lblPatternsError.Text = ex.Message; }
            }
        }

        private static string[] SplitLines(string s)
        {
            if (string.IsNullOrEmpty(s)) return new string[0];
            var list = new List<string>();
            foreach (var line in s.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0) list.Add(trimmed);
            }
            return list.ToArray();
        }

        private bool TryWriteConfig(WatcherConfiguration config, out string error)
        {
            return TryWriteConfigFile(config, configPath, out error);
        }

        // T-152: atomic save, callable without a Form. Writes a sibling temp
        // file then File.Replace, so a failed write can never leave a partial
        // configuration on disk.
        internal static bool TryWriteConfigFile(WatcherConfiguration config, string targetPath, out string error)
        {
            error = null;
            try
            {
                string xml = SerializeConfig(config);
                string tmp = targetPath + ".draft.tmp";
                File.WriteAllText(tmp, xml, new UTF8Encoding(false));
                if (File.Exists(targetPath))
                {
                    File.Replace(tmp, targetPath, null);
                }
                else
                {
                    File.Move(tmp, targetPath);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // T-152: StringWriter reports UTF-16 as its Encoding, and XmlWriter
        // copies that into the XML declaration. The bytes we then write are
        // UTF-8 without BOM, so the declaration lied and WatcherConfiguration.Load
        // failed with "There is no Unicode byte order mark". Reporting UTF-8
        // keeps the declaration and the bytes in agreement.
        private sealed class Utf8StringWriter : StringWriter
        {
            public Utf8StringWriter(StringBuilder builder, IFormatProvider formatProvider) : base(builder, formatProvider) { }
            public override Encoding Encoding { get { return new UTF8Encoding(false); } }
        }

        private static string SerializeConfig(WatcherConfiguration config)
        {
            var sb = new StringBuilder();
            using (var sw = new Utf8StringWriter(sb, CultureInfo.InvariantCulture))
            using (var writer = System.Xml.XmlWriter.Create(sw, new System.Xml.XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) }))
            {
                writer.WriteStartDocument();
                writer.WriteStartElement("saicont");
                writer.WriteAttributeString("pollIntervalMilliseconds", config.PollIntervalMilliseconds.ToString(CultureInfo.InvariantCulture));
                writer.WriteStartElement("logging");
                writer.WriteAttributeString("path", config.LogFilePath ?? String.Empty);
                writer.WriteAttributeString("maxBytes", config.LogMaximumBytes.ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("retainedFiles", config.LogRetainedFiles.ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("duplicateWindowSeconds", config.LogDuplicateWindowSeconds.ToString(CultureInfo.InvariantCulture));
                writer.WriteEndElement();
                writer.WriteStartElement("targets");
                foreach (TargetRule t in config.Targets)
                {
                    writer.WriteStartElement("target");
                    writer.WriteAttributeString("name", t.Name);
                    writer.WriteAttributeString("enabled", t.Enabled ? "true" : "false");
                    writer.WriteAttributeString("transport", t.Transport ?? "console");
                    if (!string.IsNullOrEmpty(t.Endpoint)) writer.WriteAttributeString("endpoint", t.Endpoint);
                    if (!string.IsNullOrEmpty(t.ProjectDirectory)) writer.WriteAttributeString("projectDir", t.ProjectDirectory);
                    writer.WriteAttributeString("command", t.Command);
                    writer.WriteAttributeString("scanLines", t.ScanLines.ToString(CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("maximumTriggerDistanceLines", t.MaximumTriggerDistanceLines.ToString(CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("initialDelaySeconds", t.InitialDelaySeconds.ToString(CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("retryIntervalSeconds", t.RetryIntervalSeconds.ToString(CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("parseRetryTime", t.ParseRetryTime ? "true" : "false");
                    writer.WriteAttributeString("backoffMultiplier", t.BackoffMultiplier.ToString("R", CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("maxRetryIntervalSeconds", t.MaximumRetryIntervalSeconds.ToString(CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("maxAttempts", t.MaximumAttemptsPerEvent.ToString(CultureInfo.InvariantCulture));
                    if ((t.Transport ?? "console") != "opencode-http" && t.ProcessNames != null && t.ProcessNames.Length > 0)
                    {
                        writer.WriteStartElement("processNames");
                        foreach (string p in t.ProcessNames) { writer.WriteStartElement("process"); writer.WriteString(p); writer.WriteEndElement(); }
                        writer.WriteEndElement();
                    }
                    WritePatternGroup(writer, "triggerPatterns", t.TriggerPatterns);
                    WritePatternGroup(writer, "readyPatterns", t.ReadyPatterns);
                    WritePatternGroup(writer, "busyPatterns", t.BusyPatterns);
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndDocument();
            }
            return sb.ToString();
        }

        // T-152: the loader requires the pattern CONTAINER to exist for a
        // console target even when it holds no patterns (canonical XML writes
        // <busyPatterns />). Omitting an empty container produced a file that
        // Apply wrote successfully and WatcherConfiguration.Load then refused
        // with "Missing <busyPatterns> inside <target>". Always emit the
        // container; emptiness is expressed by having no <pattern> children.
        private static void WritePatternGroup(System.Xml.XmlWriter writer, string name, string[] patterns)
        {
            writer.WriteStartElement(name);
            if (patterns != null)
            {
                foreach (string p in patterns) { writer.WriteStartElement("pattern"); writer.WriteString(p); writer.WriteEndElement(); }
            }
            writer.WriteEndElement();
        }

        private static WatcherConfiguration CloneConfig(WatcherConfiguration src)
        {
            return CloneConfiguration(src);
        }

        internal static WatcherConfiguration CloneConfiguration(WatcherConfiguration src)
        {
            var copy = new WatcherConfiguration
            {
                PollIntervalMilliseconds = src.PollIntervalMilliseconds,
                LogFilePath = src.LogFilePath,
                LogMaximumBytes = src.LogMaximumBytes,
                LogRetainedFiles = src.LogRetainedFiles,
                LogDuplicateWindowSeconds = src.LogDuplicateWindowSeconds,
                Targets = new List<TargetRule>(src.Targets.Count)
            };
            foreach (TargetRule t in src.Targets) copy.Targets.Add(CloneTarget(t));
            return copy;
        }

        private static TargetRule CloneTarget(TargetRule src)
        {
            var c = new TargetRule
            {
                Name = src.Name,
                Enabled = src.Enabled,
                Transport = src.Transport,
                Endpoint = src.Endpoint,
                ProjectDirectory = src.ProjectDirectory,
                Command = src.Command,
                ScanLines = src.ScanLines,
                MaximumTriggerDistanceLines = src.MaximumTriggerDistanceLines,
                InitialDelaySeconds = src.InitialDelaySeconds,
                RetryIntervalSeconds = src.RetryIntervalSeconds,
                ParseRetryTime = src.ParseRetryTime,
                BackoffMultiplier = src.BackoffMultiplier,
                MaximumRetryIntervalSeconds = src.MaximumRetryIntervalSeconds,
                MaximumAttemptsPerEvent = src.MaximumAttemptsPerEvent,
                ProcessNames = src.ProcessNames == null ? new string[0] : (string[])src.ProcessNames.Clone(),
                TriggerPatterns = src.TriggerPatterns == null ? new string[0] : (string[])src.TriggerPatterns.Clone(),
                ReadyPatterns = src.ReadyPatterns == null ? new string[0] : (string[])src.ReadyPatterns.Clone(),
                BusyPatterns = src.BusyPatterns == null ? new string[0] : (string[])src.BusyPatterns.Clone()
            };
            try { c.CompileRegexes(); } catch { }
            return c;
        }

        private static int Clamp(int v, int lo, int hi) { if (v < lo) return lo; if (v > hi) return hi; return v; }
    }
}
