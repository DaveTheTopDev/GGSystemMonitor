using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static GGSystemMonitor.Program;

namespace GGSystemMonitor
{
    internal class SettingsForm : Form
    {
        // ----------------------------------------------------------------
        //  Static data
        // ----------------------------------------------------------------
        private static readonly Dictionary<string, string> DisplayNames =
            new Dictionary<string, string>
            {
                { "CpuTemperature", "CPU Temperature" },
                { "CpuUsage",       "CPU Usage %"     },
                { "GpuTemperature", "GPU Temperature" },
                { "GpuUsage",       "GPU Usage %"     },
                { "RamUsage",       "RAM Usage (used / total GB)" },
                { "Text",           "Text" }
            };

        private static readonly Dictionary<string, string> DefaultFormats =
            new Dictionary<string, string>
            {
                { "CpuTemperature", "CPU: {temp:F1}°C" },
                { "CpuUsage",       "CPU USE: {pct:F0}%" },
                { "GpuTemperature", "GPU: {temp:F1}°C" },
                { "GpuUsage",       "GPU USE: {pct:F0}%" },
                { "RamUsage",       "RAM: {used:0.#}/{total:0.#}GB" }
            };

        // ----------------------------------------------------------------
        //  Fields
        // ----------------------------------------------------------------
        private readonly string _settingsPath =
            Path.Combine(AppContext.BaseDirectory, "settings.json");

        private System.Windows.Forms.Timer _statusTimer;
        private bool   _loading;
        private bool   _isDirty;
        private Baseline _baseline;

        private string _detectedCpuName = "";
        private string _detectedGpuName = "";
        private bool   _cpuAutoAvailable = false;
        private bool   _gpuAutoAvailable = false;

        private struct Baseline
        {
            public bool    EnableCpuWarn, EnableGpuWarn;
            public bool    CpuAuto, GpuAuto;
            public decimal CpuWarnVal, CpuCritVal, GpuWarnVal, GpuCritVal;
            public bool    CapsLock, GpuPaste, ShowUpdateNotif;
            public decimal TempUpdate, Rotation;
            public string  GgPath;
            public (string Type, string Label, int Dur, bool UseFahrenheit, bool UseRamMb)[] TopItems, BottomItems;
        }

        // Temperature tab hardware-detection labels (updated by status timer)
        private Label _cpuHwStatusLabel;
        private Label _gpuHwStatusLabel;

        // Singleton modals
        private Form _placeholderInfoForm;
        private bool _uninstallDialogOpen;

        // Status header
        private Label  _statusLabel;
        private Button _toggleButton;

        // Footer
        private Button _btnSave;

        // Display tab
        private ListBox       _topList,      _bottomList;
        private ComboBox      _topAddCombo,  _bottomAddCombo;
        private NumericUpDown _rotationNud;

        // Temperature tab
        private CheckBox      _enableCpuWarnCheck, _enableGpuWarnCheck;
        private CheckBox      _cpuAutoCheck, _gpuAutoCheck;
        private NumericUpDown _cpuWarnNud, _cpuCritNud, _gpuWarnNud, _gpuCritNud;
        private float         _cpuDefaultWarn, _cpuDefaultCrit, _gpuDefaultWarn, _gpuDefaultCrit;

        // General tab
        private CheckBox      _capsLockCheck, _gpuPasteCheck, _showUpdateCheck;
        private NumericUpDown _tempUpdateNud;
        private TextBox       _ggPathBox;

        // ----------------------------------------------------------------
        //  Constructor
        // ----------------------------------------------------------------
        public SettingsForm()
        {
            BuildForm();
            LoadAndPopulate();
            WireChangeHandlers();

            _statusTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _statusTimer.Tick += (s, e) => { UpdateStatus(); RefreshHardwareDetection(); };
            _statusTimer.Start();
            UpdateStatus();
            RefreshHardwareDetection();

            var updatePopupTimer = new System.Windows.Forms.Timer { Interval = 2500 };
            updatePopupTimer.Tick += (s, e) =>
            {
                updatePopupTimer.Stop();
                string v = Program.AvailableVersion;
                if (v != null) ShowUpdateDialog(v);
            };
            updatePopupTimer.Start();
        }

        // ----------------------------------------------------------------
        //  Dirty state
        // ----------------------------------------------------------------
        private void MarkDirty()
        {
            if (_loading || _btnSave == null) return;
            _isDirty = true;
            _btnSave.Enabled   = true;
            _btnSave.BackColor = Color.FromArgb(0, 120, 215);
            _btnSave.ForeColor = Color.White;
            _btnSave.FlatAppearance.BorderColor = Color.FromArgb(0, 100, 195);
        }

        private void ResetDirty()
        {
            _isDirty = false;
            _btnSave.Enabled   = false;
            _btnSave.BackColor = Color.FromArgb(200, 200, 205);
            _btnSave.ForeColor = Color.FromArgb(110, 110, 118);
            _btnSave.FlatAppearance.BorderColor = Color.FromArgb(175, 175, 185);
        }

        private void EvaluateDirty()
        {
            if (_loading || _btnSave == null) return;
            if (HasChanges()) MarkDirty();
            else ResetDirty();
        }

        private Baseline CaptureBaseline() => new Baseline
        {
            EnableCpuWarn = _enableCpuWarnCheck.Checked,
            EnableGpuWarn = _enableGpuWarnCheck.Checked,
            CpuAuto       = _cpuAutoCheck.Checked,
            GpuAuto       = _gpuAutoCheck.Checked,
            CpuWarnVal    = _cpuWarnNud.Value,
            CpuCritVal    = _cpuCritNud.Value,
            GpuWarnVal    = _gpuWarnNud.Value,
            GpuCritVal    = _gpuCritNud.Value,
            CapsLock        = _capsLockCheck.Checked,
            GpuPaste        = _gpuPasteCheck.Checked,
            ShowUpdateNotif = _showUpdateCheck.Checked,
            TempUpdate      = _tempUpdateNud.Value,
            Rotation      = _rotationNud.Value,
            GgPath        = _ggPathBox.Text,
            TopItems      = _topList.Items.Cast<LineItem>().Select(i => (i.Type, i.Label, i.DurationMs, i.UseFahrenheit, i.UseRamMb)).ToArray(),
            BottomItems   = _bottomList.Items.Cast<LineItem>().Select(i => (i.Type, i.Label, i.DurationMs, i.UseFahrenheit, i.UseRamMb)).ToArray(),
        };

        private bool HasChanges()
        {
            var cur = CaptureBaseline();
            if (cur.EnableCpuWarn != _baseline.EnableCpuWarn) return true;
            if (cur.EnableGpuWarn != _baseline.EnableGpuWarn) return true;
            if (cur.CpuAuto       != _baseline.CpuAuto)       return true;
            if (cur.GpuAuto       != _baseline.GpuAuto)       return true;
            if (!cur.CpuAuto && cur.CpuWarnVal != _baseline.CpuWarnVal) return true;
            if (!cur.CpuAuto && cur.CpuCritVal != _baseline.CpuCritVal) return true;
            if (!cur.GpuAuto && cur.GpuWarnVal != _baseline.GpuWarnVal) return true;
            if (!cur.GpuAuto && cur.GpuCritVal != _baseline.GpuCritVal) return true;
            if (cur.CapsLock        != _baseline.CapsLock)        return true;
            if (cur.GpuPaste        != _baseline.GpuPaste)        return true;
            if (cur.ShowUpdateNotif != _baseline.ShowUpdateNotif) return true;
            if (cur.TempUpdate      != _baseline.TempUpdate)      return true;
            if (cur.Rotation   != _baseline.Rotation)   return true;
            if (cur.GgPath     != _baseline.GgPath)     return true;
            if (!ListsEqual(cur.TopItems,    _baseline.TopItems))    return true;
            if (!ListsEqual(cur.BottomItems, _baseline.BottomItems)) return true;
            return false;
        }

        private static bool ListsEqual(
            (string Type, string Label, int Dur, bool UseFahrenheit, bool UseRamMb)[] a,
            (string Type, string Label, int Dur, bool UseFahrenheit, bool UseRamMb)[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        // ----------------------------------------------------------------
        //  Form layout
        // ----------------------------------------------------------------
        private void BuildForm()
        {
            Text            = "GGSystemMonitor Settings";
            ClientSize      = new Size(500, 652);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            StartPosition   = FormStartPosition.CenterScreen;
            Font            = new Font("Segoe UI", 9f);
            BackColor       = Color.FromArgb(245, 245, 248);
            Icon            = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            // ---- Dark status header ----
            var header = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 50,
                BackColor = Color.FromArgb(28, 28, 38)
            };

            var headerIcon = new PictureBox
            {
                Image    = Icon.ExtractAssociatedIcon(Application.ExecutablePath).ToBitmap(),
                Size     = new Size(32, 32),
                Location = new Point(10, 9),
                SizeMode = PictureBoxSizeMode.Zoom
            };
            header.Controls.Add(headerIcon);

            _statusLabel = new Label
            {
                Font      = new Font("Segoe UI", 9.5f),
                ForeColor = Color.White,
                AutoSize  = true,
                Location  = new Point(52, 16)
            };
            _toggleButton = new Button
            {
                Size      = new Size(155, 30),
                Location  = new Point(500 - 155 - 10, 10),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                BackColor = Color.FromArgb(55, 55, 75)
            };
            _toggleButton.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 120);
            _toggleButton.Click += OnToggleClick;
            header.Controls.Add(_statusLabel);
            header.Controls.Add(_toggleButton);

            // ---- Tab control ----
            var tabs = new TabControl
            {
                Dock    = DockStyle.Fill,
                Padding = new Point(10, 4)
            };
            tabs.TabPages.Add(BuildDisplayTab());
            tabs.TabPages.Add(BuildTemperatureTab());
            tabs.TabPages.Add(BuildGeneralTab());

            // ---- Footer ----
            var footer = new Panel
            {
                Dock      = DockStyle.Bottom,
                Height    = 46,
                BackColor = Color.FromArgb(235, 235, 240)
            };
            var btnUninstall = new Button
            {
                Text      = "Uninstall",
                Size      = new Size(88, 28),
                Location  = new Point(8, 9),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(180, 40, 40),
                ForeColor = Color.White
            };
            btnUninstall.FlatAppearance.BorderSize = 0;
            btnUninstall.Click += OnUninstallClick;

            var verLabel = new Label
            {
                Text      = "v" + Program.VERSION,
                AutoSize  = true,
                ForeColor = Color.Gray,
                Location  = new Point(104, 14)
            };

            _btnSave = new Button
            {
                Text      = "Save",
                Size      = new Size(88, 28),
                Location  = new Point(500 - 88 - 6, 9),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(200, 200, 205),
                ForeColor = Color.FromArgb(110, 110, 118),
                Enabled   = false
            };
            _btnSave.FlatAppearance.BorderSize  = 0;
            _btnSave.FlatAppearance.BorderColor = Color.FromArgb(175, 175, 185);
            _btnSave.Click += OnSaveClick;

            footer.Controls.Add(btnUninstall);
            footer.Controls.Add(verLabel);
            footer.Controls.Add(_btnSave);

            var mid = new Panel { Dock = DockStyle.Fill };
            mid.Controls.Add(tabs);

            Controls.Add(mid);
            Controls.Add(footer);
            Controls.Add(header);
        }

        // ----------------------------------------------------------------
        //  Display tab
        // ----------------------------------------------------------------
        private TabPage BuildDisplayTab()
        {
            var tab = new TabPage("Display") { BackColor = BackColor };

            var btnHelp = new Button
            {
                Text      = "Placeholder Help",
                Size      = new Size(104, 24),
                Location  = new Point(380, 8),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(230, 241, 255),
                ForeColor = Color.FromArgb(0, 90, 180),
                Font      = new Font("Segoe UI", 8f),
                Cursor    = Cursors.Hand
            };
            btnHelp.FlatAppearance.BorderColor = Color.FromArgb(0, 120, 215);
            btnHelp.Click += (s, e) => ShowPlaceholderHelp();

            var topGroup = BuildLineEditor("Top Line Items", out _topList, out _topAddCombo,
                new Point(8, 34));
            var botGroup = BuildLineEditor("Bottom Line Items", out _bottomList, out _bottomAddCombo,
                new Point(8, topGroup.Bottom + 6));

            var rotRow = new Panel
            {
                Location = new Point(8, botGroup.Bottom + 10),
                Size     = new Size(460, 28)
            };
            rotRow.Controls.Add(new Label { Text = "Rotation interval:", AutoSize = true, Location = new Point(0, 5) });
            _rotationNud = new NumericUpDown
            {
                Location  = new Point(125, 1),
                Size      = new Size(80, 22),
                Minimum   = 500,
                Maximum   = 30000,
                Increment = 500,
                Value     = 3000
            };
            rotRow.Controls.Add(_rotationNud);
            rotRow.Controls.Add(new Label
            {
                Text      = "ms  (fallback when item Duration is 0)",
                AutoSize  = true,
                Location  = new Point(210, 5),
                ForeColor = Color.Gray
            });

            tab.Controls.Add(topGroup);
            tab.Controls.Add(botGroup);
            tab.Controls.Add(rotRow);
            tab.Controls.Add(btnHelp);
            return tab;
        }

        private GroupBox BuildLineEditor(string title, out ListBox list, out ComboBox addCombo, Point location)
        {
            var group = new GroupBox { Text = title, Location = location, Size = new Size(476, 222) };

            var localList = new ListBox
            {
                Location      = new Point(8, 18),
                Size          = new Size(348, 100),
                SelectionMode = SelectionMode.One
            };
            list = localList;

            var btnUp     = SmallBtn("▲",      364, 18);
            var btnDown   = SmallBtn("▼",      364, 46);
            var btnRemove = SmallBtn("Remove", 364, 80);

            btnUp.Click     += (s, e) => { MoveItem(localList, -1); EvaluateDirty(); };
            btnDown.Click   += (s, e) => { MoveItem(localList, +1); EvaluateDirty(); };
            btnRemove.Click += (s, e) => { RemoveItem(localList);   EvaluateDirty(); };

            var localCombo = new ComboBox
            {
                Location      = new Point(8, 126),
                Size          = new Size(240, 22),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            localCombo.Items.AddRange(DisplayNames.Values.ToArray());
            localCombo.SelectedIndex = 0;
            addCombo = localCombo;

            var btnAdd = new Button { Text = "Add", Location = new Point(255, 124), Size = new Size(55, 24) };
            btnAdd.Click += (s, e) => { AddItem(localList, localCombo); EvaluateDirty(); };

            // ---- Per-item editing rows ----
            var scrollWarnLabel = new Label
            {
                Text      = "Text is too long (max 15 chars) - Will auto-scroll",
                AutoSize  = true,
                Location  = new Point(55, 153),
                ForeColor = Color.FromArgb(210, 100, 0),
                Visible   = false
            };
            var lblSel   = new Label { Text = "Label:", AutoSize = true, Location = new Point(8, 177), ForeColor = Color.Gray };
            var txtLabel = new TextBox { Location = new Point(55, 173), Size = new Size(250, 22), Enabled = false };
            var lblDur   = new Label { Text = "Duration:", AutoSize = true, Location = new Point(313, 177), ForeColor = Color.Gray };
            var nudDur   = new NumericUpDown
            {
                Location  = new Point(378, 173),
                Size      = new Size(80, 22),
                Minimum   = 0,
                Maximum   = 30000,
                Increment = 500,
                Value     = 0,
                Enabled   = false
            };

            // Extra option checkboxes (shown only when relevant type is selected)
            var chkFahrenheit = new CheckBox
            {
                Text     = "Display in °F",
                AutoSize = true,
                Location = new Point(8, 199),
                Enabled  = false,
                Visible  = false
            };
            var chkRamMb = new CheckBox
            {
                Text     = "Display in MB",
                AutoSize = true,
                Location = new Point(8, 199),
                Enabled  = false,
                Visible  = false
            };

            bool suppressLabel   = false;
            bool suppressRefresh = false;

            localList.SelectedIndexChanged += (s, e) =>
            {
                if (suppressRefresh) return;
                var item = localList.SelectedItem as LineItem;
                if (item == null)
                {
                    txtLabel.Enabled = nudDur.Enabled = false;
                    lblSel.ForeColor = lblDur.ForeColor = Color.Gray;
                    suppressLabel = true;
                    txtLabel.Text = "";
                    nudDur.Value  = 0;
                    suppressLabel = false;
                    scrollWarnLabel.Visible = false;
                    chkFahrenheit.Visible = chkRamMb.Visible = false;
                    return;
                }

                string norm = item.Type?.ToLower().Replace(" ", "").Replace("_", "") ?? "";
                bool isTemp = norm == "cputemperature" || norm == "gputemperature";
                bool isRam  = norm == "ramusage";

                chkFahrenheit.Visible = isTemp;
                chkFahrenheit.Enabled = isTemp;
                chkRamMb.Visible      = isRam;
                chkRamMb.Enabled      = isRam;

                suppressLabel = true;
                chkFahrenheit.Checked = item.UseFahrenheit;
                chkRamMb.Checked      = item.UseRamMb;
                nudDur.Value = item.DurationMs;
                if (item.Type == "Text")
                {
                    SetPlaceholder(txtLabel, "Text");
                    txtLabel.Text = item.Label ?? "";
                }
                else
                {
                    SetPlaceholder(txtLabel, GetFormatHint(item.Type, item.UseFahrenheit, item.UseRamMb));
                    txtLabel.Text = item.Label ?? "";
                }
                suppressLabel = false;
                txtLabel.Enabled = nudDur.Enabled = true;
                lblSel.ForeColor = lblDur.ForeColor = Color.Black;
                scrollWarnLabel.Visible = EstimateRenderedLength(txtLabel.Text, item.Type) > 15;
            };

            txtLabel.TextChanged += (s, e) =>
            {
                if (suppressLabel) return;
                var item = localList.SelectedItem as LineItem;
                if (item == null) return;
                string val = txtLabel.Text;
                item.Label = val.Length > 0 ? val : null;
                int selIdx = localList.SelectedIndex;
                if (selIdx >= 0)
                {
                    suppressRefresh = true;
                    localList.BeginUpdate();
                    localList.Items.RemoveAt(selIdx);
                    localList.Items.Insert(selIdx, item);
                    localList.SelectedIndex = selIdx;
                    localList.EndUpdate();
                    suppressRefresh = false;
                }
                scrollWarnLabel.Visible = EstimateRenderedLength(val, item.Type) > 15;
                EvaluateDirty();
            };

            nudDur.ValueChanged += (s, e) =>
            {
                if (suppressLabel) return;
                var item = localList.SelectedItem as LineItem;
                if (item != null) item.DurationMs = (int)nudDur.Value;
                EvaluateDirty();
            };
            WireNudCommit(nudDur);

            chkFahrenheit.CheckedChanged += (s, e) =>
            {
                if (suppressLabel) return;
                var item = localList.SelectedItem as LineItem;
                if (item == null) return;
                item.UseFahrenheit = chkFahrenheit.Checked;
                SetPlaceholder(txtLabel, GetFormatHint(item.Type, item.UseFahrenheit, item.UseRamMb));
                int selIdx = localList.SelectedIndex;
                if (selIdx >= 0)
                {
                    suppressRefresh = true;
                    localList.BeginUpdate();
                    localList.Items.RemoveAt(selIdx);
                    localList.Items.Insert(selIdx, item);
                    localList.SelectedIndex = selIdx;
                    localList.EndUpdate();
                    suppressRefresh = false;
                }
                EvaluateDirty();
            };

            chkRamMb.CheckedChanged += (s, e) =>
            {
                if (suppressLabel) return;
                var item = localList.SelectedItem as LineItem;
                if (item == null) return;
                item.UseRamMb = chkRamMb.Checked;
                SetPlaceholder(txtLabel, GetFormatHint(item.Type, item.UseFahrenheit, item.UseRamMb));
                int selIdx = localList.SelectedIndex;
                if (selIdx >= 0)
                {
                    suppressRefresh = true;
                    localList.BeginUpdate();
                    localList.Items.RemoveAt(selIdx);
                    localList.Items.Insert(selIdx, item);
                    localList.SelectedIndex = selIdx;
                    localList.EndUpdate();
                    suppressRefresh = false;
                }
                EvaluateDirty();
            };

            group.Controls.AddRange(new Control[] { localList, btnUp, btnDown, btnRemove, localCombo, btnAdd, scrollWarnLabel, lblSel, txtLabel, lblDur, nudDur, chkFahrenheit, chkRamMb });
            return group;
        }

        private static string GetFormatHint(string type, bool fahrenheit, bool ramMb)
        {
            switch (type?.ToLower().Replace(" ", "").Replace("_", "") ?? "")
            {
                case "cputemperature": return fahrenheit ? "CPU: {temp:F1}°F" : "CPU: {temp:F1}°C";
                case "gputemperature": return fahrenheit ? "GPU: {temp:F1}°F" : "GPU: {temp:F1}°C";
                case "cpuusage":       return "CPU USE: {pct:F0}%";
                case "gpuusage":       return "GPU USE: {pct:F0}%";
                case "ramusage":       return ramMb ? "RAM: {used:0.#}/{total:0.#}MB" : "RAM: {used:0.#}/{total:0.#}GB";
                default:               return "{value}";
            }
        }

        private static int EstimateRenderedLength(string label, string itemType)
        {
            if (string.IsNullOrEmpty(label)) return 0;
            string est = label;
            est = Regex.Replace(est, @"\{temp:[^}]*\}", "100.0");  // worst-case 5 chars
            est = Regex.Replace(est, @"\{pct:[^}]*\}",  "100");    // 3 chars
            est = Regex.Replace(est, @"\{used:[^}]*\}", "16.0");   // 4 chars
            est = Regex.Replace(est, @"\{total:[^}]*\}", "128");   // 3 chars
            string norm = itemType?.ToLower().Replace(" ", "").Replace("_", "") ?? "";
            string valueRepl;
            switch (norm)
            {
                case "cputemperature":
                case "gputemperature": valueRepl = "100.0°C"; break;  // 7 chars
                case "cpuusage":
                case "gpuusage":       valueRepl = "100%";    break;  // 4 chars
                case "ramusage":       valueRepl = "16.0/128GB"; break; // 10 chars
                default:               valueRepl = "??";      break;
            }
            est = est.Replace("{value}", valueRepl);
            return est.Length;
        }

        // ----------------------------------------------------------------
        //  Temperature tab
        // ----------------------------------------------------------------
        private TabPage BuildTemperatureTab()
        {
            var tab = new TabPage("Temperature") { BackColor = BackColor };

            // Read hardware cache written by the monitor process
            try
            {
                string hwPath = Path.Combine(AppContext.BaseDirectory, "hardware.json");
                if (File.Exists(hwPath))
                {
                    var hw = JObject.Parse(File.ReadAllText(hwPath));
                    _detectedCpuName = hw["CpuName"]?.ToString() ?? "";
                    _detectedGpuName = hw["GpuName"]?.ToString() ?? "";
                }
            }
            catch { }
            _cpuAutoAvailable = IsCpuNameInDictionary(_detectedCpuName);
            _gpuAutoAvailable = IsGpuNameInDictionary(_detectedGpuName);

            _cpuDefaultWarn = GetCpuAutoWarning(_detectedCpuName);
            _cpuDefaultCrit = GetCpuAutoCritical(_detectedCpuName);
            _gpuDefaultWarn = GetGpuAutoWarning(_detectedGpuName);
            _gpuDefaultCrit = GetGpuAutoCritical(_detectedGpuName);

            var tempPollRow = new Panel { Location = new Point(8, 8), Size = new Size(460, 28) };
            tempPollRow.Controls.Add(new Label { Text = "Temperature poll interval:", AutoSize = true, Location = new Point(0, 5) });
            _tempUpdateNud = new NumericUpDown { Location = new Point(175, 1), Size = new Size(80, 22), Minimum = 500, Maximum = 30000, Increment = 500, Value = 2000 };
            tempPollRow.Controls.Add(_tempUpdateNud);
            tempPollRow.Controls.Add(new Label { Text = "ms", AutoSize = true, ForeColor = Color.Gray, Location = new Point(260, 5) });
            tab.Controls.Add(tempPollRow);

            _enableCpuWarnCheck = new CheckBox { Text = "Show CPU temperature warning indicators on the display", AutoSize = true, Location = new Point(10, 44) };
            _enableGpuWarnCheck = new CheckBox { Text = "Show GPU temperature warning indicators on the display", AutoSize = true, Location = new Point(10, 69) };

            var cpuGroup = BuildThresholdGroup("CPU Thresholds",
                new Point(10, 99),
                _detectedCpuName, _cpuAutoAvailable,
                () => _cpuDefaultWarn, () => _cpuDefaultCrit,
                GetCpuMaximum(_detectedCpuName),
                out _cpuAutoCheck, out _cpuWarnNud, out _cpuCritNud,
                out _cpuHwStatusLabel);

            var gpuGroup = BuildThresholdGroup("GPU Thresholds",
                new Point(10, cpuGroup.Bottom + 8),
                _detectedGpuName, _gpuAutoAvailable,
                () => _gpuDefaultWarn, () => _gpuDefaultCrit,
                GetGpuMaximum(_detectedGpuName),
                out _gpuAutoCheck, out _gpuWarnNud, out _gpuCritNud,
                out _gpuHwStatusLabel);

            // Expand GPU group and place thermal paste option inside it
            gpuGroup.Size = new Size(gpuGroup.Width, gpuGroup.Height + 40);
            _gpuPasteCheck = new CheckBox
            {
                Text     = "Enable GPU thermal paste monitoring",
                AutoSize = true,
                Location = new Point(8, 114)
            };
            var pasteHint = new Label
            {
                Text      = "Scrolling warning when hotspot temperature gap exceeds 15°C",
                AutoSize  = true,
                Location  = new Point(26, 134),
                ForeColor = Color.Gray,
                Font      = new Font("Segoe UI", 8f)
            };
            gpuGroup.Controls.Add(_gpuPasteCheck);
            gpuGroup.Controls.Add(pasteHint);

            tab.Controls.AddRange(new Control[] { _enableCpuWarnCheck, _enableGpuWarnCheck, cpuGroup, gpuGroup });
            return tab;
        }

        private GroupBox BuildThresholdGroup(string title, Point location,
            string hardwareName, bool autoAvailable,
            Func<float> getDefaultWarn, Func<float> getDefaultCrit,
            float tjmax,
            out CheckBox autoCheck, out NumericUpDown warnNud,
            out NumericUpDown critNud,
            out Label statusLabel)
        {
            var group = new GroupBox { Text = title, Location = location, Size = new Size(476, 116) };

            var localWarnNud = new NumericUpDown { Location = new Point(80, 22), Size = new Size(65, 22), Minimum = 20, Maximum = 150, Value = 85, Enabled = !autoAvailable };
            var localCritNud = new NumericUpDown { Location = new Point(80, 56), Size = new Size(65, 22), Minimum = 20, Maximum = 150, Value = 100, Enabled = !autoAvailable };
            warnNud = localWarnNud;
            critNud = localCritNud;

            // Single Auto checkbox controlling both thresholds
            var localAuto = new CheckBox
            {
                Text      = "Auto Detect",
                AutoSize  = true,
                Location  = new Point(240, 38),
                Checked   = autoAvailable,
                Enabled   = autoAvailable
            };
            autoCheck = localAuto;

            bool clampGuard = false;
            void CheckClamp()
            {
                if (clampGuard || _loading) return;
                if (localCritNud.Value <= localWarnNud.Value)
                {
                    clampGuard = true;
                    localCritNud.Value = Math.Min(localWarnNud.Value + 1, localCritNud.Maximum);
                    clampGuard = false;
                    MessageBox.Show(
                        $"Critical temperature must be greater than the warning temperature.\n" +
                        $"Critical has been adjusted to {localCritNud.Value} °C.",
                        "Temperature Threshold",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                EvaluateDirty();
            }

            localAuto.CheckedChanged += (s, e) =>
            {
                bool isAuto = localAuto.Checked;
                localWarnNud.Enabled = !isAuto;
                localCritNud.Enabled = !isAuto;
                if (isAuto)
                {
                    // Read current defaults at toggle time so stale closure values don't apply
                    localWarnNud.Value = Clamp(localWarnNud, (decimal)getDefaultWarn());
                    localCritNud.Value = Clamp(localCritNud, (decimal)getDefaultCrit());
                }
                EvaluateDirty();
            };

            localWarnNud.ValueChanged += (s, e) => CheckClamp();
            localCritNud.ValueChanged += (s, e) => CheckClamp();

            // Hardware name status label
            string statusText;
            Color  statusColor;
            if (string.IsNullOrEmpty(hardwareName))
            {
                statusText  = "Run the monitor once to enable auto-detection";
                statusColor = Color.Gray;
            }
            else if (autoAvailable)
            {
                statusText  = $"{hardwareName} (TJMax {(int)tjmax}°C) - Auto temperature limits available";
                statusColor = Color.FromArgb(0, 140, 60);
            }
            else
            {
                statusText  = hardwareName + " - No auto temperature limits in database";
                statusColor = Color.FromArgb(200, 100, 0);
            }
            statusLabel = new Label
            {
                Text      = statusText,
                AutoSize  = false,
                Size      = new Size(456, 30),
                Location  = new Point(8, 82),
                ForeColor = statusColor,
                Font      = new Font("Segoe UI", 8f)
            };

            group.Controls.Add(new Label { Text = "Warning:",  AutoSize = true, Location = new Point(8, 26) });
            group.Controls.Add(new Label { Text = "°C", AutoSize = true, Location = new Point(150, 26) });
            group.Controls.Add(new Label { Text = "Critical:", AutoSize = true, Location = new Point(8, 60) });
            group.Controls.Add(new Label { Text = "°C", AutoSize = true, Location = new Point(150, 60) });
            group.Controls.AddRange(new Control[] { localAuto, localWarnNud, localCritNud, statusLabel });
            return group;
        }

        // ----------------------------------------------------------------
        //  General tab
        // ----------------------------------------------------------------
        private TabPage BuildGeneralTab()
        {
            var tab = new TabPage("General") { BackColor = BackColor };

            _capsLockCheck   = new CheckBox { Text = "Show Caps Lock indicator (🡅) on the top-right of the display", AutoSize = true, Location = new Point(10, 10) };
            _showUpdateCheck = new CheckBox { Text = "Show update notification on the keyboard OLED display", AutoSize = true, Location = new Point(10, 36) };

            tab.Controls.Add(new Label { Text = "GG Engine coreProps.json path:", AutoSize = true, Location = new Point(10, 72) });
            _ggPathBox = new TextBox { Location = new Point(10, 90), Size = new Size(374, 22) };
            var btnBrowse = new Button { Text = "Browse...", Location = new Point(390, 88), Size = new Size(78, 24) };
            btnBrowse.Click += OnBrowseClick;

            var btnReset = new Button
            {
                Text      = "Reset All Settings to Defaults",
                Location  = new Point(10, 128),
                Size      = new Size(210, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(180, 40, 40),
                ForeColor = Color.White
            };
            btnReset.FlatAppearance.BorderSize = 0;
            btnReset.Click += OnResetDefaultsClick;

            tab.Controls.AddRange(new Control[] { _capsLockCheck, _showUpdateCheck, _ggPathBox, btnBrowse, btnReset });
            return tab;
        }

        // ----------------------------------------------------------------
        //  Helpers
        // ----------------------------------------------------------------
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);

        private static void SetPlaceholder(TextBox tb, string text) =>
            SendMessage(tb.Handle, 0x1501 /* EM_SETCUEBANNER */, IntPtr.Zero, text);

        private static Button SmallBtn(string text, int x, int y) =>
            new Button { Text = text, Location = new Point(x, y), Size = new Size(104, 26) };

        private void ShowPlaceholderHelp()
        {
            if (_placeholderInfoForm != null && !_placeholderInfoForm.IsDisposed)
            {
                _placeholderInfoForm.BringToFront();
                _placeholderInfoForm.Activate();
                return;
            }

            const int W    = 490;
            const int H    = 560;
            const int padX = 16;

            var frm = new Form
            {
                Text            = "Display Information",
                ClientSize      = new Size(W, H),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition   = FormStartPosition.CenterParent,
                MaximizeBox     = false,
                MinimizeBox     = false,
                ShowInTaskbar   = false,
                BackColor       = Color.FromArgb(245, 245, 248),
                Font            = new Font("Segoe UI", 9f),
                Icon            = Icon.ExtractAssociatedIcon(Application.ExecutablePath)
            };

            // Dark header
            var header = new Panel { Location = new Point(0, 0), Size = new Size(W, 60), BackColor = Color.FromArgb(24, 24, 28) };
            header.Controls.Add(new Label
            {
                Text      = "Display Information",
                Location  = new Point(padX, 10),
                Size      = new Size(W - padX * 2, 24),
                ForeColor = Color.White,
                Font      = new Font("Segoe UI", 13f, FontStyle.Bold),
                AutoSize  = false
            });
            header.Controls.Add(new Label
            {
                Text      = "Placeholder reference and formatting guide",
                Location  = new Point(padX, 38),
                Size      = new Size(W - padX * 2, 16),
                ForeColor = Color.FromArgb(160, 160, 175),
                Font      = new Font("Segoe UI", 8f),
                AutoSize  = false
            });
            frm.Controls.Add(header);

            // Scrollable content panel
            var scroll = new Panel
            {
                Location   = new Point(0, 60),
                Size       = new Size(W, H - 60 - 52),
                BackColor  = Color.FromArgb(245, 245, 248),
                AutoScroll = true
            };
            frm.Controls.Add(scroll);

            int y        = 12;
            int cW       = W - padX * 2 - 18;
            var bodyFont = new Font("Segoe UI", 8.5f);
            var monoFont = new Font("Consolas", 8.5f);
            var boldFont = new Font("Segoe UI", 8.5f, FontStyle.Bold);

            void Section(string text)
            {
                scroll.Controls.Add(new Label
                {
                    Text      = text,
                    Location  = new Point(padX, y),
                    Size      = new Size(cW, 18),
                    ForeColor = Color.FromArgb(0, 100, 200),
                    Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
                    AutoSize  = false
                });
                y += 20;
                scroll.Controls.Add(new Panel
                {
                    Location  = new Point(padX, y),
                    Size      = new Size(cW, 1),
                    BackColor = Color.FromArgb(0, 100, 200)
                });
                y += 7;
            }

            void Para(string text)
            {
                var sz = TextRenderer.MeasureText(text, bodyFont, new Size(cW, 2000), TextFormatFlags.WordBreak);
                scroll.Controls.Add(new Label
                {
                    Text      = text,
                    Location  = new Point(padX, y),
                    Size      = new Size(cW, sz.Height + 2),
                    ForeColor = Color.FromArgb(80, 80, 95),
                    Font      = bodyFont,
                    AutoSize  = false
                });
                y += sz.Height + 6;
            }

            void CodeRow(string code, string desc)
            {
                var row = new Panel { Location = new Point(padX, y), Size = new Size(cW, 22), BackColor = Color.FromArgb(232, 234, 240) };
                row.Controls.Add(new Label { Text = code, Location = new Point(6, 3),   Size = new Size(120, 16), ForeColor = Color.FromArgb(180, 75, 0),  Font = monoFont, AutoSize = false });
                row.Controls.Add(new Label { Text = desc, Location = new Point(130, 3), Size = new Size(cW - 136, 16), ForeColor = Color.FromArgb(55, 60, 75), Font = bodyFont, AutoSize = false });
                scroll.Controls.Add(row);
                y += 26;
            }

            void ExampleRow(string code, string result, string note)
            {
                var row = new Panel { Location = new Point(padX, y), Size = new Size(cW, 22), BackColor = Color.FromArgb(238, 248, 238) };
                row.Controls.Add(new Label { Text = code,     Location = new Point(6, 3),   Size = new Size(210, 16), ForeColor = Color.FromArgb(100, 0, 130), Font = monoFont, AutoSize = false });
                row.Controls.Add(new Label { Text = "→", Location = new Point(220, 3), Size = new Size(16, 16),  ForeColor = Color.FromArgb(0, 100, 200), Font = boldFont, AutoSize = false });
                if (note != null)
                {
                    row.Controls.Add(new Label { Text = result, Location = new Point(240, 3), Size = new Size(90, 16),        ForeColor = Color.FromArgb(0, 120, 50),    Font = boldFont,                                               AutoSize = false });
                    row.Controls.Add(new Label { Text = note,   Location = new Point(336, 3), Size = new Size(cW - 342, 16),  ForeColor = Color.FromArgb(120, 120, 135), Font = new Font("Segoe UI", 7.5f, FontStyle.Italic), AutoSize = false });
                }
                else
                {
                    row.Controls.Add(new Label { Text = result, Location = new Point(240, 3), Size = new Size(cW - 246, 16), ForeColor = Color.FromArgb(0, 120, 50), Font = boldFont, AutoSize = false });
                }
                scroll.Controls.Add(row);
                y += 26;
            }

            void FormatRow(string code, string desc, string sample)
            {
                var row = new Panel { Location = new Point(padX, y), Size = new Size(cW, 22), BackColor = Color.FromArgb(232, 234, 240) };
                row.Controls.Add(new Label { Text = code,   Location = new Point(6, 3),        Size = new Size(46, 16),       ForeColor = Color.FromArgb(180, 75, 0),  Font = monoFont, AutoSize = false });
                row.Controls.Add(new Label { Text = desc,   Location = new Point(80, 3),        Size = new Size(cW - 194, 16), ForeColor = Color.FromArgb(55, 60, 75),  Font = bodyFont, AutoSize = false });
                row.Controls.Add(new Label { Text = sample, Location = new Point(cW - 108, 3),  Size = new Size(102, 16),      ForeColor = Color.FromArgb(0, 130, 60),  Font = monoFont, AutoSize = false });
                scroll.Controls.Add(row);
                y += 26;
            }

            void Gap(int px) { y += px; }

            // Content
            Para("Use placeholders in the Label field to insert live sensor values. Clearing the label reverts the item to its built-in default format.");
            Gap(4);

            Section("TEMPERATURE & USAGE");
            CodeRow("{temp:FORMAT}", "Temperature reading  (CPU / GPU Temperature)");
            CodeRow("{pct:FORMAT}",  "Usage percentage  (CPU / GPU Usage)");
            Gap(8);

            Section("RAM USAGE");
            CodeRow("{used:FORMAT}",  "Amount of RAM currently in use");
            CodeRow("{total:FORMAT}", "Total installed RAM capacity");
            Gap(8);

            Section("FORMAT CODES");
            Para("Labels use C# standard numeric format strings. Replace FORMAT with one of the following specifiers. These are the most common ones used for sensor values:");

            FormatRow("F0",  "Whole number",                      "75");
            FormatRow("F1",  "One decimal place",                 "75.3");
            FormatRow("F2",  "Two decimal places",                "75.34");
            FormatRow("0.#", "Up to one decimal, no trailing zero", "7.5  or  8");
            Gap(8);

            Section("EXAMPLES");
            ExampleRow("CPU: {temp:F1}°C",         "CPU: 72.4°C",  null);
            ExampleRow("GPU USE: {pct:F0}%",            "GPU USE: 87%",      null);
            ExampleRow("RAM: {used:0.#}/{total:0.#}GB", "RAM: 7.5/16GB",     null);
            ExampleRow("Temp: {temp:F0}°F",        "Temp: 162°F",  "requires °F mode");
            Gap(8);

            Section("DISPLAY WIDTH");
            Para("The OLED display is 15 characters wide. Labels longer than 15 characters will auto-scroll during display.");
            Gap(4);

            scroll.AutoScrollMinSize = new Size(0, y + 12);

            // Bottom bar
            var bottomBar = new Panel { Location = new Point(0, H - 52), Size = new Size(W, 52), BackColor = Color.FromArgb(232, 234, 240) };
            bottomBar.Controls.Add(new Panel { Location = new Point(0, 0), Size = new Size(W, 1), BackColor = Color.FromArgb(200, 202, 210) });
            var btnOk = new Button
            {
                Text      = "OK",
                Size      = new Size(90, 30),
                Location  = new Point(W - 90 - padX, 11),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                Font      = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            btnOk.FlatAppearance.BorderSize = 0;
            btnOk.Click += (s, e) => frm.Close();
            bottomBar.Controls.Add(btnOk);
            frm.Controls.Add(bottomBar);

            frm.FormClosed += (s, e) => { _placeholderInfoForm = null; };
            _placeholderInfoForm = frm;
            frm.Show(this);
            frm.BringToFront();
            frm.Activate();
        }

        private void AddItem(ListBox list, ComboBox combo)
        {
            if (combo.SelectedItem is string display)
            {
                var rev = DisplayNames.ToDictionary(kv => kv.Value, kv => kv.Key);
                if (rev.TryGetValue(display, out string type))
                {
                    var item = new LineItem { Type = type };
                    item.Label = type == "Text" ? "Text" : GetFormatHint(type, false, false);
                    list.Items.Add(item);
                }
            }
        }

        private void RemoveItem(ListBox list)
        {
            if (list.SelectedIndex >= 0) list.Items.RemoveAt(list.SelectedIndex);
        }

        private void MoveItem(ListBox list, int dir)
        {
            int i = list.SelectedIndex;
            if (i < 0) return;
            int j = i + dir;
            if (j < 0 || j >= list.Items.Count) return;
            var item = list.Items[i];
            list.Items.RemoveAt(i);
            list.Items.Insert(j, item);
            list.SelectedIndex = j;
        }

        // ----------------------------------------------------------------
        //  Load settings into controls
        // ----------------------------------------------------------------
        private void LoadAndPopulate()
        {
            _loading = true;

            AppSettings s = null;
            if (File.Exists(_settingsPath))
            {
                try { s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath)); }
                catch { }
            }
            s = s ?? new AppSettings();

            // Display
            PopulateList(_topList,    s.TopLineItems    ?? new List<LineItem> { new LineItem { Type = "CpuTemperature" } });
            PopulateList(_bottomList, s.BottomLineItems ?? new List<LineItem> { new LineItem { Type = "GpuTemperature" } });
            _rotationNud.Value = Clamp(_rotationNud, s.RotationIntervalMs);

            // Temperature
            _enableCpuWarnCheck.Checked = s.EnableCPUTemperatureWarningIndicators;
            _enableGpuWarnCheck.Checked = s.EnableGPUTemperatureWarningIndicators;
            ApplyThresholdGroup(s.WarningCPUTemperature, s.CriticalCPUTemperature,
                _cpuAutoCheck, _cpuWarnNud, _cpuCritNud, _cpuAutoAvailable, _cpuDefaultWarn, _cpuDefaultCrit);
            ApplyThresholdGroup(s.WarningGPUTemperature, s.CriticalGPUTemperature,
                _gpuAutoCheck, _gpuWarnNud, _gpuCritNud, _gpuAutoAvailable, _gpuDefaultWarn, _gpuDefaultCrit);

            // General
            _capsLockCheck.Checked   = s.ShowCapsLockIndicator;
            _gpuPasteCheck.Checked   = s.EnableGPUThermalPasteMonitoring;
            _showUpdateCheck.Checked = s.ShowUpdateNotifications;
            _tempUpdateNud.Value     = Clamp(_tempUpdateNud, s.TemperatureUpdateIntervalMs);
            _ggPathBox.Text        = s.GGEngineCorePropsPath
                ?? @"C:/ProgramData/SteelSeries/SteelSeries Engine 3/coreProps.json";

            _loading = false;
            _baseline = CaptureBaseline();
            ResetDirty();
        }

        private void WireChangeHandlers()
        {
            _rotationNud.ValueChanged          += (s, e) => EvaluateDirty();
            _enableCpuWarnCheck.CheckedChanged += (s, e) => EvaluateDirty();
            _enableGpuWarnCheck.CheckedChanged += (s, e) => EvaluateDirty();
            _cpuAutoCheck.CheckedChanged       += (s, e) => EvaluateDirty();
            _cpuWarnNud.ValueChanged           += (s, e) => EvaluateDirty();
            _cpuCritNud.ValueChanged           += (s, e) => EvaluateDirty();
            _gpuAutoCheck.CheckedChanged       += (s, e) => EvaluateDirty();
            _gpuWarnNud.ValueChanged           += (s, e) => EvaluateDirty();
            _gpuCritNud.ValueChanged           += (s, e) => EvaluateDirty();
            _capsLockCheck.CheckedChanged      += (s, e) => EvaluateDirty();
            _gpuPasteCheck.CheckedChanged      += (s, e) => EvaluateDirty();
            _showUpdateCheck.CheckedChanged    += (s, e) => EvaluateDirty();
            _tempUpdateNud.ValueChanged        += (s, e) => EvaluateDirty();
            _ggPathBox.TextChanged             += (s, e) => EvaluateDirty();
            // Commit typed value immediately so HasChanges() reads the updated Value
            foreach (var nud in new[] { _rotationNud, _cpuWarnNud, _cpuCritNud, _gpuWarnNud, _gpuCritNud, _tempUpdateNud })
                WireNudCommit(nud);
        }

        private void WireNudCommit(NumericUpDown nud)
        {
            Control innerTb = null;
            foreach (Control c in nud.Controls)
                if (c is TextBox) { innerTb = c; break; }
            if (innerTb == null) return;

            bool syncing = false;
            innerTb.TextChanged += (s, e) =>
            {
                if (syncing) return;
                if (decimal.TryParse(innerTb.Text, out decimal parsed))
                {
                    decimal clamped = Math.Max(nud.Minimum, Math.Min(nud.Maximum, parsed));
                    if (nud.Value != clamped)
                    {
                        syncing = true;
                        nud.Value = clamped;
                        syncing = false;
                    }
                    EvaluateDirty();
                }
            };
        }

        private void PopulateList(ListBox list, List<LineItem> items)
        {
            list.Items.Clear();
            foreach (var item in items)
            {
                if (item.Label == null && !string.Equals(item.Type, "Text", StringComparison.OrdinalIgnoreCase))
                    item.Label = GetFormatHint(item.Type, item.UseFahrenheit, item.UseRamMb);
                list.Items.Add(item);
            }
        }

        private static bool ParseIsAuto(object setting)
        {
            if (setting is string str)
                return str.Equals("auto", StringComparison.OrdinalIgnoreCase);
            if (setting is JsonElement elem)
            {
                if (elem.ValueKind == JsonValueKind.String)
                    return (elem.GetString() ?? "auto").Equals("auto", StringComparison.OrdinalIgnoreCase);
                return false;
            }
            return true;
        }

        private static float ParseTempValue(object setting, float fallback)
        {
            if (setting is string str)
            {
                if (float.TryParse(str, out float sv)) return sv;
            }
            else if (setting is JsonElement elem)
            {
                if (elem.ValueKind == JsonValueKind.Number) return elem.GetSingle();
                if (elem.ValueKind == JsonValueKind.String && float.TryParse(elem.GetString(), out float sv)) return sv;
            }
            return fallback;
        }

        private void ApplyThresholdGroup(
            object warnSetting, object critSetting,
            CheckBox autoCheck, NumericUpDown warnNud, NumericUpDown critNud,
            bool autoAvailable, float fallbackWarn, float fallbackCrit)
        {
            bool isAutoWarn = ParseIsAuto(warnSetting);
            bool isAutoCrit = ParseIsAuto(critSetting);
            float valueWarn = ParseTempValue(warnSetting, fallbackWarn);
            float valueCrit = ParseTempValue(critSetting, fallbackCrit);

            // Both must be auto for the shared checkbox to be checked
            bool isAuto = isAutoWarn && isAutoCrit;

            autoCheck.Enabled = autoAvailable;
            if (!autoAvailable)
            {
                autoCheck.Checked = false;
                warnNud.Enabled   = true;
                critNud.Enabled   = true;
                warnNud.Value     = Clamp(warnNud, (decimal)valueWarn);
                critNud.Value     = Clamp(critNud, (decimal)valueCrit);
            }
            else
            {
                autoCheck.Checked = isAuto;
                warnNud.Enabled   = !isAuto;
                critNud.Enabled   = !isAuto;
                if (isAuto)
                {
                    warnNud.Value = Clamp(warnNud, (decimal)fallbackWarn);
                    critNud.Value = Clamp(critNud, (decimal)fallbackCrit);
                }
                else
                {
                    warnNud.Value = Clamp(warnNud, (decimal)valueWarn);
                    critNud.Value = Clamp(critNud, (decimal)valueCrit);
                }
            }
        }

        private static decimal Clamp(NumericUpDown nud, decimal v) =>
            Math.Max(nud.Minimum, Math.Min(nud.Maximum, v));

        // ----------------------------------------------------------------
        //  Save
        // ----------------------------------------------------------------
        private void OnSaveClick(object sender, EventArgs e)
        {
            if (!_isDirty) return;
            SaveSettings();
        }

        private void SaveSettings()
        {
            int topSel = _topList.SelectedIndex;
            int botSel = _bottomList.SelectedIndex;

            // Load existing JSON with Newtonsoft to preserve any _note_ comment fields
            JObject jo;
            try { jo = File.Exists(_settingsPath) ? JObject.Parse(File.ReadAllText(_settingsPath)) : new JObject(); }
            catch  { jo = new JObject(); }

            JArray MapList(ListBox lb) => new JArray(
                lb.Items.Cast<LineItem>().Select(item => new JObject(
                    new JProperty("Type",         item.Type),
                    new JProperty("Label",        item.Label),
                    new JProperty("DurationMs",   item.DurationMs),
                    new JProperty("UseFahrenheit", item.UseFahrenheit),
                    new JProperty("UseRamMb",      item.UseRamMb)
                ))
            );

            jo["GGEngineCorePropsPath"]                = _ggPathBox.Text;
            jo["TemperatureUpdateIntervalMs"]           = (int)_tempUpdateNud.Value;
            jo["EnableCPUTemperatureWarningIndicators"] = _enableCpuWarnCheck.Checked;
            jo["EnableGPUTemperatureWarningIndicators"] = _enableGpuWarnCheck.Checked;
            jo["WarningCPUTemperature"]                 = _cpuAutoCheck.Checked ? (JToken)"AUTO" : (float)_cpuWarnNud.Value;
            jo["CriticalCPUTemperature"]                = _cpuAutoCheck.Checked ? (JToken)"AUTO" : (float)_cpuCritNud.Value;
            jo["WarningGPUTemperature"]                 = _gpuAutoCheck.Checked ? (JToken)"AUTO" : (float)_gpuWarnNud.Value;
            jo["CriticalGPUTemperature"]                = _gpuAutoCheck.Checked ? (JToken)"AUTO" : (float)_gpuCritNud.Value;
            jo["EnableGPUThermalPasteMonitoring"]       = _gpuPasteCheck.Checked;
            jo["ShowCapsLockIndicator"]                 = _capsLockCheck.Checked;
            jo["ShowUpdateNotifications"]               = _showUpdateCheck.Checked;
            jo["TopLineItems"]                          = MapList(_topList);
            jo["BottomLineItems"]                       = MapList(_bottomList);
            jo["RotationIntervalMs"]                    = (int)_rotationNud.Value;

            File.WriteAllText(_settingsPath, jo.ToString(Formatting.Indented));

            LoadAndPopulate();

            if (topSel >= 0 && topSel < _topList.Items.Count) _topList.SelectedIndex = topSel;
            if (botSel >= 0 && botSel < _bottomList.Items.Count) _bottomList.SelectedIndex = botSel;
        }

        private void OnResetDefaultsClick(object sender, EventArgs e)
        {
            var confirm = MessageBox.Show(
                "This will reset all settings to their default values.\n\nAre you sure?",
                "Reset Settings",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (confirm != DialogResult.Yes) return;

            var d = new AppSettings();
            _loading = true;
            var defaultTopItems = new List<LineItem>
            {
                new LineItem { Type = "CpuTemperature", Label = GetFormatHint("CpuTemperature", false, false) },
                new LineItem { Type = "CpuUsage",       Label = GetFormatHint("CpuUsage",       false, false) },
                new LineItem { Type = "RamUsage",       Label = GetFormatHint("RamUsage",       false, false) },
            };
            var defaultBottomItems = new List<LineItem>
            {
                new LineItem { Type = "GpuTemperature", Label = GetFormatHint("GpuTemperature", false, false) },
            };
            PopulateList(_topList,    defaultTopItems);
            PopulateList(_bottomList, defaultBottomItems);
            _rotationNud.Value          = Clamp(_rotationNud,   d.RotationIntervalMs);
            _enableCpuWarnCheck.Checked = d.EnableCPUTemperatureWarningIndicators;
            _enableGpuWarnCheck.Checked = d.EnableGPUTemperatureWarningIndicators;
            ApplyThresholdGroup(d.WarningCPUTemperature, d.CriticalCPUTemperature,
                _cpuAutoCheck, _cpuWarnNud, _cpuCritNud, _cpuAutoAvailable, GetCpuAutoWarning(_detectedCpuName), GetCpuAutoCritical(_detectedCpuName));
            ApplyThresholdGroup(d.WarningGPUTemperature, d.CriticalGPUTemperature,
                _gpuAutoCheck, _gpuWarnNud, _gpuCritNud, _gpuAutoAvailable, GetGpuAutoWarning(_detectedGpuName), GetGpuAutoCritical(_detectedGpuName));
            _capsLockCheck.Checked   = d.ShowCapsLockIndicator;
            _gpuPasteCheck.Checked   = d.EnableGPUThermalPasteMonitoring;
            _showUpdateCheck.Checked = d.ShowUpdateNotifications;
            _tempUpdateNud.Value     = Clamp(_tempUpdateNud, d.TemperatureUpdateIntervalMs);
            _ggPathBox.Text          = d.GGEngineCorePropsPath;
            _loading = false;
            SaveSettings();
        }

        private void OnBrowseClick(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog
            {
                Title    = "Locate coreProps.json",
                Filter   = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                FileName = "coreProps.json"
            })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    _ggPathBox.Text = dlg.FileName;
            }
        }

        // ----------------------------------------------------------------
        //  Hardware detection (polls until hardware.json appears)
        // ----------------------------------------------------------------
        private void RefreshHardwareDetection()
        {
            string cpuName = "", gpuName = "";
            try
            {
                string hwPath = Path.Combine(AppContext.BaseDirectory, "hardware.json");
                if (!File.Exists(hwPath)) return;
                var hw = JObject.Parse(File.ReadAllText(hwPath));
                cpuName = hw["CpuName"]?.ToString() ?? "";
                gpuName = hw["GpuName"]?.ToString() ?? "";
            }
            catch { return; }

            if (cpuName == _detectedCpuName && gpuName == _detectedGpuName) return;

            _detectedCpuName  = cpuName;
            _detectedGpuName  = gpuName;
            _cpuAutoAvailable = IsCpuNameInDictionary(_detectedCpuName);
            _gpuAutoAvailable = IsGpuNameInDictionary(_detectedGpuName);

            ApplyHardwareStatusToGroup(_cpuHwStatusLabel, _detectedCpuName, _cpuAutoAvailable, GetCpuMaximum(_detectedCpuName));
            ApplyHardwareStatusToGroup(_gpuHwStatusLabel, _detectedGpuName, _gpuAutoAvailable, GetGpuMaximum(_detectedGpuName));

            AppSettings s = null;
            try
            {
                if (File.Exists(_settingsPath))
                    s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath));
            }
            catch { }
            s = s ?? new AppSettings();

            _cpuDefaultWarn = GetCpuAutoWarning(_detectedCpuName);
            _cpuDefaultCrit = GetCpuAutoCritical(_detectedCpuName);
            _gpuDefaultWarn = GetGpuAutoWarning(_detectedGpuName);
            _gpuDefaultCrit = GetGpuAutoCritical(_detectedGpuName);
            _loading = true;
            ApplyThresholdGroup(s.WarningCPUTemperature, s.CriticalCPUTemperature,
                _cpuAutoCheck, _cpuWarnNud, _cpuCritNud, _cpuAutoAvailable, _cpuDefaultWarn, _cpuDefaultCrit);
            ApplyThresholdGroup(s.WarningGPUTemperature, s.CriticalGPUTemperature,
                _gpuAutoCheck, _gpuWarnNud, _gpuCritNud, _gpuAutoAvailable, _gpuDefaultWarn, _gpuDefaultCrit);
            _loading = false;
            _baseline = CaptureBaseline();
            ResetDirty();
        }

        private static void ApplyHardwareStatusToGroup(Label statusLbl, string name, bool autoAvail, float tjmax = 0f)
        {
            if (statusLbl == null) return;
            if (string.IsNullOrEmpty(name))
            {
                statusLbl.Text      = "Run the monitor once to enable auto-detection";
                statusLbl.ForeColor = Color.Gray;
            }
            else if (autoAvail)
            {
                statusLbl.Text      = $"{name} (TJMax {(int)tjmax}°C) - Auto temperature limits available";
                statusLbl.ForeColor = Color.FromArgb(0, 140, 60);
            }
            else
            {
                statusLbl.Text      = name + " - No auto temperature limits in database";
                statusLbl.ForeColor = Color.FromArgb(200, 100, 0);
            }
        }

        // ----------------------------------------------------------------
        //  Monitor toggle
        // ----------------------------------------------------------------
        private void UpdateStatus()
        {
            bool running = IsMonitorRunning();
            _statusLabel.Text      = running ? "●  Monitoring is running" : "○  Monitoring is stopped";
            _statusLabel.ForeColor = running ? Color.LightGreen : Color.FromArgb(220, 100, 100);
            _toggleButton.Text     = running ? "Stop Monitoring" : "Start Monitoring";
        }

        private void OnToggleClick(object sender, EventArgs e)
        {
            if (IsMonitorRunning())
            {
                foreach (var p in Process.GetProcessesByName("GGSystemMonitor")
                    .Where(p => p.Id != Process.GetCurrentProcess().Id))
                {
                    try { p.Kill(); } catch { }
                }
            }
            else
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName        = Process.GetCurrentProcess().MainModule.FileName,
                        UseShellExecute = true
                    });
                }
                catch { }
            }
            System.Threading.Thread.Sleep(600);
            UpdateStatus();
        }

        private bool IsMonitorRunning() =>
            Process.GetProcessesByName("GGSystemMonitor")
                   .Any(p => p.Id != Process.GetCurrentProcess().Id);

        // ----------------------------------------------------------------
        //  Uninstall
        // ----------------------------------------------------------------
        private void OnUninstallClick(object sender, EventArgs e)
        {
            if (_uninstallDialogOpen) return;
            string installPs1 = Path.Combine(AppContext.BaseDirectory, "Install.ps1");
            if (!File.Exists(installPs1))
            {
                MessageBox.Show(
                    "Install.ps1 was not found in the installation folder.\n" +
                    "You can uninstall manually via Add/Remove Programs.",
                    "Uninstall Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            bool uninstallComplete = false;

            using (var dlg = new Form())
            {
                dlg.Text            = "Uninstall GGSystemMonitor";
                dlg.ClientSize      = new Size(420, 210);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition   = FormStartPosition.CenterParent;
                dlg.MaximizeBox     = false;
                dlg.MinimizeBox     = false;
                dlg.ShowInTaskbar   = false;
                dlg.BackColor       = Color.FromArgb(245, 245, 248);
                dlg.Font            = new Font("Segoe UI", 9f);
                dlg.Icon            = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

                var iconLbl = new Label
                {
                    Text      = "!",
                    Font      = new Font("Segoe UI", 22f, FontStyle.Bold),
                    ForeColor = Color.FromArgb(180, 40, 40),
                    AutoSize  = true,
                    Location  = new Point(16, 14)
                };

                var msgLbl = new Label
                {
                    Text      = "Are you sure you want to uninstall GGSystemMonitor?\n\n" +
                                "This will stop the application, remove the scheduled\n" +
                                "task, shortcuts, and delete the installation folder.",
                    AutoSize  = false,
                    Size      = new Size(344, 78),
                    Location  = new Point(60, 10),
                    ForeColor = Color.FromArgb(30, 30, 40)
                };

                var progressBar = new ProgressBar
                {
                    Location = new Point(16, 102),
                    Size     = new Size(388, 16),
                    Minimum  = 0,
                    Maximum  = 100,
                    Value    = 0,
                    Style    = ProgressBarStyle.Continuous,
                    Visible  = false
                };

                var statusLbl = new Label
                {
                    Text      = "Removing GGSystemMonitor...",
                    AutoSize  = true,
                    Location  = new Point(16, 123),
                    ForeColor = Color.FromArgb(90, 90, 100),
                    Visible   = false
                };

                var footer = new Panel
                {
                    Dock      = DockStyle.Bottom,
                    Height    = 50,
                    BackColor = Color.FromArgb(228, 228, 236)
                };

                var btnConfirm = new Button
                {
                    Text      = "Uninstall",
                    Size      = new Size(100, 30),
                    Location  = new Point(420 - 100 - 8, 10),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(180, 40, 40),
                    ForeColor = Color.White,
                    Font      = new Font("Segoe UI", 9f, FontStyle.Bold)
                };
                btnConfirm.FlatAppearance.BorderSize = 0;

                var btnCancel = new Button
                {
                    Text      = "Cancel",
                    Size      = new Size(88, 30),
                    Location  = new Point(420 - 100 - 8 - 88 - 6, 10),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(245, 245, 248)
                };
                btnCancel.FlatAppearance.BorderColor = Color.FromArgb(185, 185, 198);

                footer.Controls.Add(btnConfirm);
                footer.Controls.Add(btnCancel);
                dlg.Controls.Add(iconLbl);
                dlg.Controls.Add(msgLbl);
                dlg.Controls.Add(progressBar);
                dlg.Controls.Add(statusLbl);
                dlg.Controls.Add(footer);

                System.Windows.Forms.Timer pollTimer = null;
                string doneFile = Path.Combine(Path.GetTempPath(), "GGSystemMonitor_uninstall.tmp");

                btnCancel.Click += (s2, e2) => { pollTimer?.Stop(); dlg.Close(); };

                btnConfirm.Click += (s2, e2) =>
                {
                    btnConfirm.Enabled   = false;
                    btnCancel.Enabled    = false;
                    btnConfirm.BackColor = Color.FromArgb(160, 160, 165);
                    btnCancel.BackColor  = Color.FromArgb(210, 210, 215);
                    progressBar.Visible  = true;
                    statusLbl.Visible    = true;
                    dlg.Refresh();

                    if (File.Exists(doneFile)) { try { File.Delete(doneFile); } catch { } }

                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName        = "powershell.exe",
                            Arguments       = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden" +
                                              $" -File \"{installPs1}\" -Uninstall -DoneFile \"{doneFile}\"" +
                                              $" -ExcludePid {System.Diagnostics.Process.GetCurrentProcess().Id}",
                            UseShellExecute = true,
                            Verb            = "runas"
                        });
                    }
                    catch
                    {
                        btnConfirm.Enabled   = true;
                        btnCancel.Enabled    = true;
                        btnConfirm.BackColor = Color.FromArgb(180, 40, 40);
                        btnCancel.BackColor  = Color.FromArgb(245, 245, 248);
                        progressBar.Visible  = false;
                        statusLbl.Visible    = false;
                        return;
                    }

                    int elapsed = 0;
                    pollTimer = new System.Windows.Forms.Timer { Interval = 300 };
                    pollTimer.Tick += (s3, e3) =>
                    {
                        elapsed += 300;
                        try
                        {
                            if (!File.Exists(doneFile)) return;
                            string content = File.ReadAllText(doneFile).Trim();

                            if (int.TryParse(content, out int pct))
                            {
                                progressBar.Value = Math.Max(progressBar.Value, Math.Min(100, pct));
                                return;
                            }
                            if (content == "DONE")
                            {
                                pollTimer.Stop();
                                progressBar.Value = 100;
                                dlg.Refresh();
                                try { File.Delete(doneFile); } catch { }
                                var closeTimer = new System.Windows.Forms.Timer { Interval = 400 };
                                closeTimer.Tick += (s4, e4) =>
                                {
                                    closeTimer.Stop(); closeTimer.Dispose();
                                    uninstallComplete = true;
                                    dlg.Close();
                                };
                                closeTimer.Start();
                                return;
                            }
                            if (content.StartsWith("ERROR:"))
                            {
                                pollTimer.Stop();
                                try { File.Delete(doneFile); } catch { }
                                MessageBox.Show(content.Substring(6), "Uninstall Failed",
                                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                                dlg.Close();
                                return;
                            }
                        }
                        catch { }

                        if (elapsed > 90000)
                        {
                            pollTimer.Stop();
                            MessageBox.Show(
                                "Uninstall timed out. Please try again or use Add/Remove Programs.",
                                "Uninstall Timeout", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            dlg.Close();
                        }
                    };
                    pollTimer.Start();
                };

                dlg.FormClosed += (s2, e2) => { pollTimer?.Stop(); pollTimer?.Dispose(); _uninstallDialogOpen = false; };
                _uninstallDialogOpen = true;
                dlg.ShowDialog(this);
            }

            if (uninstallComplete)
            {
                MessageBox.Show(
                    "GGSystemMonitor has been successfully uninstalled.",
                    "Uninstall Complete",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                this.Close();
            }
        }

        private void ShowUpdateDialog(string newVersion)
        {
            using (var dlg = new Form())
            {
                dlg.Text            = "Update Available";
                dlg.ClientSize      = new Size(390, 168);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition   = FormStartPosition.CenterParent;
                dlg.MaximizeBox     = false;
                dlg.MinimizeBox     = false;
                dlg.BackColor       = Color.FromArgb(245, 245, 248);
                dlg.Font            = new Font("Segoe UI", 9f);
                dlg.Icon            = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

                var icon = new Label
                {
                    Text      = "!",
                    Font      = new Font("Segoe UI", 22f, FontStyle.Bold),
                    ForeColor = Color.FromArgb(0, 120, 215),
                    AutoSize  = true,
                    Location  = new Point(18, 18)
                };

                var msg = new Label
                {
                    Text      = "A new version of GGSystemMonitor is available.\n\n" +
                                "Installed:  v" + Program.VERSION + "\n" +
                                "Available:  v" + newVersion,
                    AutoSize  = false,
                    Size      = new Size(300, 80),
                    Location  = new Point(62, 16),
                    ForeColor = Color.FromArgb(30, 30, 40)
                };

                var footer = new Panel
                {
                    Dock      = DockStyle.Bottom,
                    Height    = 50,
                    BackColor = Color.FromArgb(228, 228, 236)
                };

                var btnOpen = new Button
                {
                    Text      = "Open GitHub",
                    Size      = new Size(108, 30),
                    Location  = new Point(390 - 108 - 8, 10),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(0, 120, 215),
                    ForeColor = Color.White,
                    Font      = new Font("Segoe UI", 9f, FontStyle.Bold)
                };
                btnOpen.FlatAppearance.BorderSize = 0;
                btnOpen.Click += (s, e) =>
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName        = "https://github.com/DaveTheTopDev/GGSystemMonitor",
                        UseShellExecute = true
                    });
                    dlg.Close();
                };

                var btnClose = new Button
                {
                    Text      = "Close",
                    Size      = new Size(88, 30),
                    Location  = new Point(390 - 108 - 8 - 88 - 6, 10),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(245, 245, 248)
                };
                btnClose.FlatAppearance.BorderColor = Color.FromArgb(185, 185, 198);
                btnClose.Click += (s, e) => dlg.Close();

                footer.Controls.Add(btnOpen);
                footer.Controls.Add(btnClose);
                dlg.Controls.Add(icon);
                dlg.Controls.Add(msg);
                dlg.Controls.Add(footer);
                dlg.ShowDialog(this);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _statusTimer?.Stop();
            _statusTimer?.Dispose();
            base.OnFormClosed(e);
        }
    }
}
