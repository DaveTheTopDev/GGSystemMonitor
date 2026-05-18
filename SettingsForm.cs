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
                { "Time",           "Time"             },
                { "Date",           "Date"             },
                { "Weather",        "Weather"          },
                { "NowPlaying",     "Now Playing"      },
                { "Text",           "Text" }
            };

        private static readonly Dictionary<string, string> DefaultFormats =
            new Dictionary<string, string>
            {
                { "CpuTemperature", "CPU: {temp:F1}°C"           },
                { "CpuUsage",       "CPU USE: {pct:F0}%"          },
                { "GpuTemperature", "GPU: {temp:F1}°C"           },
                { "GpuUsage",       "GPU USE: {pct:F0}%"          },
                { "RamUsage",       "RAM: {used:0.#}/{total:0.#}GB" },
                { "Time",           "⏰{time:hh:mm:ss tt}"        },
                { "Date",           "📅 {date:MMM dd, yyyy}"     },
                { "Weather",        "{wicon} {temp:F0}°C - {condition}" },
                { "NowPlaying",     "{source} {artist} - {title} - {elapsed:mm:ss}/{duration:mm:ss}" }
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
            public bool    CapsLock, ShowUpdateNotif;
            public decimal Rotation;
            public string  GgPath;
            public string  OledFont;
            public string  TopItemsJson, BottomItemsJson;
        }

        // Temperature tab hardware-detection labels (updated by status timer)
        private Label _cpuHwStatusLabel;
        private Label _gpuHwStatusLabel;

        // Singleton modals
        private Form _placeholderInfoForm;
        private Form _itemPlaceholderForm;
        private Form _fontInfoForm;
        private Form _cpuAvInfoForm;
        private bool _uninstallDialogOpen;

        // Status header
        private Label  _statusLabel;
        private Button _toggleButton;

        // Footer
        private Button _btnSave;

        // Display tab
        private ListBox       _topList,      _bottomList;
        private ComboBox      _topAddCombo,  _bottomAddCombo;
        private ComboBox      _topSensorCombo,  _botSensorCombo;
        private Label         _topSensorLabel,  _botSensorLabel;
        private string[]      _cpuTempSensorNames = Array.Empty<string>();
        private string[]      _gpuTempSensorNames = Array.Empty<string>();
        private NumericUpDown _rotationNud;

        // Temperature / type-specific controls (created in BuildTypeExtPanels)
        private CheckBox      _enableCpuWarnCheck, _enableGpuWarnCheck;
        private CheckBox      _cpuAutoCheck, _gpuAutoCheck;
        private NumericUpDown _cpuWarnNud, _cpuCritNud, _gpuWarnNud, _gpuCritNud;
        private float         _cpuDefaultWarn, _cpuDefaultCrit, _gpuDefaultWarn, _gpuDefaultCrit;

        // Ext panels — type-specific settings embedded inside widget line editors
        private Panel    _cpuTempExtPanel, _gpuTempExtPanel, _weatherExtPanel;
        private Panel    _cpuAvBlockedRow, _cpuTempPollRow;
        private GroupBox _cpuTempHost,     _gpuTempHost,     _weatherHost;
        private GroupBox _cpuThreshGroup;
        private Label    _cpuAvBlockedLabel;
        private Button   _cpuAvBlockedInfoButton;
        private bool     _cpuTemperatureBlockedByAv;
        private bool     _suppressExtPopulate;

        private Action _displayTabReflow;
        private Label  _fontNotInstalledLabel;

        // Callbacks registered by each BuildLineEditor so ext-panel resizes can propagate
        private readonly List<Action> _lineEditorUpdateHeightCallbacks = new List<Action>();

        // General tab
        private CheckBox      _capsLockCheck, _gpuPasteCheck, _showUpdateCheck;
        private NumericUpDown _tempUpdateNud, _gpuTempUpdateNud, _gpuPasteGapNud;
        private TextBox       _ggPathBox;
        private TextBox       _weatherLocationBox;
        private Label         _weatherLocationHintLabel;
        private Label         _weatherLocationStatusLabel;
        private ListBox       _weatherLocationSuggestionsList;
        private System.Windows.Forms.Timer _weatherValidationTimer;
        private int           _weatherValidationVersion;
        private string        _queuedWeatherValidationText = "";
        private bool          _queuedWeatherSuggestionsAllowed;
        private ComboBox      _oledFontCombo;

        private static readonly System.Net.Http.HttpClient WeatherValidationClient =
            new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };

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

        private static string SerializeItems(ListBox lb) =>
            System.Text.Json.JsonSerializer.Serialize(lb.Items.Cast<LineItem>().ToList());

        private Baseline CaptureBaseline() => new Baseline
        {
            CapsLock        = _capsLockCheck.Checked,
            ShowUpdateNotif = _showUpdateCheck.Checked,
            Rotation        = _rotationNud.Value,
            GgPath          = _ggPathBox.Text,
            OledFont        = _oledFontCombo?.SelectedIndex == 0 ? "" : _oledFontCombo?.SelectedItem?.ToString() ?? "",
            TopItemsJson    = SerializeItems(_topList),
            BottomItemsJson = SerializeItems(_bottomList),
        };

        private bool HasChanges()
        {
            var cur = CaptureBaseline();
            if (cur.CapsLock        != _baseline.CapsLock)        return true;
            if (cur.ShowUpdateNotif != _baseline.ShowUpdateNotif) return true;
            if (cur.Rotation        != _baseline.Rotation)        return true;
            if (cur.GgPath          != _baseline.GgPath)          return true;
            if (cur.OledFont        != _baseline.OledFont)        return true;
            if (cur.TopItemsJson    != _baseline.TopItemsJson)    return true;
            if (cur.BottomItemsJson != _baseline.BottomItemsJson) return true;
            return false;
        }

        // ----------------------------------------------------------------
        //  Form layout
        // ----------------------------------------------------------------
        private void BuildForm()
        {
            Text            = "GGSystemMonitor Settings";
            ClientSize      = new Size(500, 748);
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

            // Build ext panels before any tab so fields exist when lambdas fire
            BuildTypeExtPanels();

            // ---- Tab control ----
            var tabs = new TabControl
            {
                Dock    = DockStyle.Fill,
                Padding = new Point(10, 4)
            };
            tabs.TabPages.Add(BuildDisplayTab());
            tabs.TabPages.Add(BuildGeneralTab());

            tabs.SelectedIndexChanged += (s, e) =>
            {
                if (tabs.SelectedIndex == 0) _displayTabReflow?.Invoke();
            };

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

            // Skinny custom scrollbar (8px wide, right side)
            const int ScrollW = 8;
            var scrollTrack = new Panel
            {
                Width = ScrollW, Dock = DockStyle.Right,
                BackColor = Color.FromArgb(235, 235, 238), Visible = false
            };
            var scrollThumb = new Panel
            {
                Left = 1, Width = ScrollW - 2, Height = 30,
                BackColor = Color.FromArgb(175, 175, 185), Cursor = Cursors.Hand
            };
            scrollTrack.Controls.Add(scrollThumb);

            // Viewport clips overflow; content panel scrolls within it
            var viewport = new Panel { Dock = DockStyle.Fill, BackColor = BackColor };
            var content  = new Panel { Location = Point.Empty, BackColor = BackColor };
            viewport.Controls.Add(content);

            int scrollOffset = 0;
            GroupBox topGroupRef = null, botGroupRef = null;
            Panel    rotRowRef   = null;

            void UpdateScrollbar()
            {
                if (rotRowRef == null || !viewport.IsHandleCreated) return;
                int contentH = rotRowRef.Bottom + 8;
                content.Height = contentH;
                content.Width  = viewport.ClientSize.Width;
                int viewH  = viewport.ClientSize.Height;
                bool need  = contentH > viewH;
                if (scrollTrack.Visible != need) scrollTrack.Visible = need;
                if (!need) { scrollOffset = 0; content.Top = 0; return; }
                int maxOff   = contentH - viewH;
                scrollOffset = Math.Max(0, Math.Min(scrollOffset, maxOff));
                content.Top  = -scrollOffset;
                int thumbH   = Math.Max(20, (int)((float)viewH / contentH * viewH));
                scrollThumb.Height = thumbH;
                scrollThumb.Top    = maxOff > 0 ? (int)((float)scrollOffset / maxOff * (viewH - thumbH)) : 0;
            }

            void Reflow()
            {
                if (botGroupRef == null || rotRowRef == null) return;
                botGroupRef.Top = topGroupRef.Bottom + 6;
                rotRowRef.Top   = botGroupRef.Bottom + 10;
                UpdateScrollbar();
            }

            var tabTitle = new Label
            {
                Text      = "OLED Display Settings",
                AutoSize  = true,
                Location  = new Point(8, 11),
                Font      = new Font("Segoe UI", 10.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(50, 50, 65)
            };

            var btnHelp = new Button
            {
                Text      = "Placeholder Help",
                Size      = new Size(104, 24),
                Location  = new Point(373, 8),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(230, 241, 255),
                ForeColor = Color.FromArgb(0, 90, 180),
                Font      = new Font("Segoe UI", 8f),
                Cursor    = Cursors.Hand
            };
            btnHelp.FlatAppearance.BorderColor = Color.FromArgb(0, 120, 215);
            btnHelp.Click += (s, e) => ShowPlaceholderHelp();

            var topGroup = BuildLineEditor("Top Line Items", out _topList, out _topAddCombo,
                out _topSensorCombo, out _topSensorLabel, new Point(8, 34), Reflow,
                () => _bottomList);
            topGroupRef = topGroup;

            var botGroup = BuildLineEditor("Bottom Line Items", out _bottomList, out _bottomAddCombo,
                out _botSensorCombo, out _botSensorLabel, new Point(8, topGroup.Bottom + 6), Reflow,
                () => _topList);
            botGroupRef = botGroup;

            var rotRow = new Panel
            {
                Location = new Point(8, botGroup.Bottom + 10),
                Size     = new Size(460, 28)
            };
            rotRowRef = rotRow;
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

            content.Controls.Add(tabTitle);
            content.Controls.Add(btnHelp);
            content.Controls.Add(topGroup);
            content.Controls.Add(botGroup);
            content.Controls.Add(rotRow);

            // Mouse wheel via message filter so it works regardless of which child has focus
            var wheelFilter = new ScrollWheelFilter(viewport, delta =>
            {
                if (!scrollTrack.Visible) return;
                scrollOffset -= delta / 120 * 20;
                UpdateScrollbar();
            });
            Application.AddMessageFilter(wheelFilter);
            viewport.Disposed += (s, e) => Application.RemoveMessageFilter(wheelFilter);

            viewport.HandleCreated     += (s, e) => Reflow();
            viewport.ClientSizeChanged += (s, e) => UpdateScrollbar();

            // Thumb drag
            bool thumbDrag = false; int thumbDragY = 0, scrollDragStart = 0;
            scrollThumb.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                thumbDrag = true; thumbDragY = scrollThumb.Top + e.Y; scrollDragStart = scrollOffset;
                scrollThumb.Capture = true;
            };
            scrollThumb.MouseMove += (s, e) =>
            {
                if (!thumbDrag) return;
                int dy    = (scrollThumb.Top + e.Y) - thumbDragY;
                int avail = scrollTrack.ClientSize.Height - scrollThumb.Height;
                if (avail <= 0) return;
                int maxOff = Math.Max(0, content.Height - viewport.ClientSize.Height);
                scrollOffset = scrollDragStart + (int)((float)dy / avail * maxOff);
                UpdateScrollbar();
            };
            scrollThumb.MouseUp += (s, e) => thumbDrag = false;
            // Click on track itself to jump
            scrollTrack.MouseDown += (s, e) =>
            {
                if (thumbDrag) return;
                int maxOff = Math.Max(0, content.Height - viewport.ClientSize.Height);
                scrollOffset = (int)((float)e.Y / scrollTrack.ClientSize.Height * maxOff);
                UpdateScrollbar();
            };

            tab.Controls.Add(scrollTrack);
            tab.Controls.Add(viewport);

            void QueueReflow()
            {
                void Run()
                {
                    if (IsDisposed || tab.IsDisposed) return;
                    foreach (var cb in _lineEditorUpdateHeightCallbacks) cb?.Invoke();
                    Reflow();
                    content.Refresh();
                }

                if (IsHandleCreated) BeginInvoke((Action)Run);
                else Run();
            }

            _displayTabReflow = QueueReflow;
            tab.Enter += (s, e) => QueueReflow();

            return tab;
        }

        private GroupBox BuildLineEditor(string title, out ListBox list, out ComboBox addCombo,
            out ComboBox sensorCombo, out Label sensorLabel, Point location, Action onResize = null,
            Func<ListBox> getOtherList = null)
        {
            const int CollapseH = 158;
            var group = new GroupBox { Text = title, Location = location, Size = new Size(476, CollapseH) };

            var localList = new ListBox
            {
                Location      = new Point(8, 18),
                Size          = new Size(348, 100),
                SelectionMode = SelectionMode.One,
                DrawMode      = DrawMode.OwnerDrawFixed,
                ItemHeight    = 16
            };
            localList.DrawItem += DrawLineItem;
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
            var txtLabel = new TextBox { Location = new Point(55, 173), Size = new Size(222, 22), Enabled = false };

            bool infoHover = false, infoDown = false;
            var btnInfo = new Button
            {
                Text      = "",
                Size      = new Size(20, 20),
                Location  = new Point(281, 173),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(245, 245, 248),
                ForeColor = Color.White,
                Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold | FontStyle.Italic),
                Cursor    = Cursors.Hand,
                Visible   = false,
                Enabled   = false,
                TabStop   = false
            };
            btnInfo.FlatAppearance.BorderSize          = 0;
            btnInfo.FlatAppearance.MouseOverBackColor  = Color.FromArgb(245, 245, 248);
            btnInfo.FlatAppearance.MouseDownBackColor  = Color.FromArgb(245, 245, 248);
            btnInfo.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode     = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.PixelOffsetMode   = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                Color fill = infoDown  ? Color.FromArgb(0,  80, 170)
                           : infoHover ? Color.FromArgb(0, 100, 190)
                                       : Color.FromArgb(0, 120, 215);
                var rc = new RectangleF(0.5f, 0.5f, btnInfo.Width - 1, btnInfo.Height - 1);
                using (var br = new System.Drawing.SolidBrush(fill))
                    g.FillEllipse(br, rc);
                using (var sf = new System.Drawing.StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                using (var fr = new Font("Segoe UI", 8.5f, FontStyle.Bold | FontStyle.Italic))
                using (var tb = new System.Drawing.SolidBrush(Color.White))
                    g.DrawString("i", fr, tb, new RectangleF(0, 0, btnInfo.Width, btnInfo.Height), sf);
            };
            btnInfo.MouseEnter += (s, e) => { infoHover = true;  btnInfo.Invalidate(); };
            btnInfo.MouseLeave += (s, e) => { infoHover = false; btnInfo.Invalidate(); };
            btnInfo.MouseDown  += (s, e) => { infoDown  = true;  btnInfo.Invalidate(); };
            btnInfo.MouseUp    += (s, e) => { infoDown  = false; btnInfo.Invalidate(); };
            btnInfo.Click += (s, e) => { var sel = localList.SelectedItem as LineItem; if (sel != null) ShowItemPlaceholderHelp(sel.Type); };

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

            // Sensor override row (shown only for temperature items)
            var localSensorLabel = new Label
            {
                Text      = "Sensor:",
                AutoSize  = true,
                Location  = new Point(8, 226),
                ForeColor = Color.Gray,
                Visible   = false
            };
            var localSensorCombo = new ComboBox
            {
                Location      = new Point(60, 222),
                Size          = new Size(390, 22),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Visible       = false
            };
            sensorCombo = localSensorCombo;
            sensorLabel = localSensorLabel;

            var chkNpSpotify  = new CheckBox { Text = "Spotify",      AutoSize = true, Location = new Point(8,   199), Visible = false, Checked = true };
            var chkNpYouTube  = new CheckBox { Text = "YouTube",      AutoSize = true, Location = new Point(90,  199), Visible = false, Checked = true };
            var chkNpOther    = new CheckBox { Text = "Media Player", AutoSize = true, Location = new Point(175, 199), Visible = false, Checked = true };
            var lblNotPlaying     = new Label  { Text = "Not Playing:", AutoSize = true, Location = new Point(8, 227),   ForeColor = Color.Gray, Visible = false };
            var txtNotPlaying     = new TextBox { Location = new Point(82, 223), Size = new Size(234, 22), Visible = false };
            var chkSkipNotPlaying = new CheckBox { Text = "Skip rotation when no media is playing", AutoSize = true, Location = new Point(8, 249), Visible = false };

            bool suppressLabel   = false;
            bool suppressRefresh = false;
            bool suppressSensor  = false;
            bool warnRowVisible  = false;

            void SetWarnRowVisible(bool visible)
            {
                warnRowVisible = visible;
                scrollWarnLabel.Visible = visible;
            }

            localList.SelectedIndexChanged += (s, e) =>
            {
                if (suppressRefresh) return;
                var item = localList.SelectedItem as LineItem;
                if (item == null)
                {
                    suppressLabel = true;
                    txtLabel.Text = "";
                    nudDur.Value  = 0;
                    suppressLabel = false;
                    SetWarnRowVisible(false);
                    lblSel.Visible = txtLabel.Visible = lblDur.Visible = nudDur.Visible = false;
                    chkFahrenheit.Visible = chkRamMb.Visible = false;
                    localSensorCombo.Visible = localSensorLabel.Visible = false;
                    btnInfo.Visible = btnInfo.Enabled = false;
                    lblNotPlaying.Visible = txtNotPlaying.Visible = chkSkipNotPlaying.Visible = false;
                    chkNpSpotify.Visible = chkNpYouTube.Visible = chkNpOther.Visible = false;
                    HideExtPanel(_cpuTempExtPanel, ref _cpuTempHost, group);
                    HideExtPanel(_gpuTempExtPanel, ref _gpuTempHost, group);
                    HideExtPanel(_weatherExtPanel, ref _weatherHost, group);
                    UpdateHeight();
                    return;
                }

                string norm = item.Type?.ToLower().Replace(" ", "").Replace("_", "") ?? "";
                bool isTemp        = norm == "cputemperature" || norm == "gputemperature";
                bool isCpuTemp     = norm == "cputemperature";
                bool isRam         = norm == "ramusage";
                bool isNowPlaying  = norm == "nowplaying";
                bool showFahrenheit = isTemp || norm == "weather";

                chkFahrenheit.Visible = showFahrenheit;
                chkFahrenheit.Enabled = showFahrenheit;
                chkRamMb.Visible      = isRam;
                chkRamMb.Enabled      = isRam;

                lblNotPlaying.Visible = txtNotPlaying.Visible = chkSkipNotPlaying.Visible = isNowPlaying;
                chkNpSpotify.Visible = chkNpYouTube.Visible = chkNpOther.Visible = isNowPlaying;

                // Populate sensor combo for the selected temperature type
                localSensorCombo.Visible = localSensorLabel.Visible = isTemp;
                if (isTemp)
                {
                    suppressSensor = true;
                    localSensorCombo.Items.Clear();
                    localSensorCombo.Items.Add("(Auto)");
                    string[] sensors = isCpuTemp ? _cpuTempSensorNames : _gpuTempSensorNames;
                    foreach (var sn in sensors) localSensorCombo.Items.Add(sn);
                    string current = item.SensorOverride ?? "";
                    int idx = string.IsNullOrEmpty(current) ? 0 : Array.IndexOf(sensors, current) + 1;
                    localSensorCombo.SelectedIndex = idx >= 1 && idx < localSensorCombo.Items.Count ? idx : 0;
                    suppressSensor = false;
                }

                suppressLabel = true;
                chkFahrenheit.Checked = item.UseFahrenheit;
                chkRamMb.Checked      = item.UseRamMb;
                nudDur.Value = item.DurationMs;
                if (isNowPlaying)
                {
                    txtNotPlaying.Text        = item.NotPlayingText ?? "Not Playing";
                    chkSkipNotPlaying.Checked = item.SkipIfNotPlaying;
                    chkNpSpotify.Checked      = item.NowPlayingSpotify;
                    chkNpYouTube.Checked      = item.NowPlayingYouTube;
                    chkNpOther.Checked        = item.NowPlayingOtherPlayer;
                }
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
                lblSel.Visible = txtLabel.Visible = lblDur.Visible = nudDur.Visible = true;
                txtLabel.Enabled = nudDur.Enabled = true;
                lblSel.ForeColor = lblDur.ForeColor = Color.Black;
                SetWarnRowVisible(EstimateRenderedLength(txtLabel.Text, item.Type) > 15);
                btnInfo.Visible = btnInfo.Enabled = (norm != "text");

                // Show/hide type-specific ext panels and populate with per-item values
                if (norm == "cputemperature") { ShowExtPanel(_cpuTempExtPanel, ref _cpuTempHost, group, getOtherList); PopulateCpuExtPanel(item); }
                else                          HideExtPanel(_cpuTempExtPanel, ref _cpuTempHost, group);
                if (norm == "gputemperature") { ShowExtPanel(_gpuTempExtPanel, ref _gpuTempHost, group, getOtherList); PopulateGpuExtPanel(item); }
                else                          HideExtPanel(_gpuTempExtPanel, ref _gpuTempHost, group);
                if (norm == "weather")        { ShowExtPanel(_weatherExtPanel, ref _weatherHost,  group, getOtherList); PopulateWeatherExtPanel(item); }
                else                          HideExtPanel(_weatherExtPanel, ref _weatherHost,  group);

                ApplyWarnShift(warnRowVisible);
            };

            localSensorCombo.SelectedIndexChanged += (s, e) =>
            {
                if (suppressSensor || suppressLabel) return;
                var item = localList.SelectedItem as LineItem;
                if (item == null) return;
                string selected = localSensorCombo.SelectedIndex <= 0
                    ? ""
                    : localSensorCombo.SelectedItem as string ?? "";
                item.SensorOverride = string.IsNullOrEmpty(selected) ? null : selected;
                EvaluateDirty();
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
                bool nowWarn = EstimateRenderedLength(val, item.Type) > 15;
                if (warnRowVisible != nowWarn) { SetWarnRowVisible(nowWarn); ApplyWarnShift(warnRowVisible); }
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
                if (item.Label != null)
                {
                    item.Label = chkFahrenheit.Checked
                        ? item.Label.Replace("°C", "°F")
                        : item.Label.Replace("°F", "°C");
                    txtLabel.Text = item.Label;
                }
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

            txtNotPlaying.TextChanged += (s, e) =>
            {
                if (suppressLabel) return;
                var item = localList.SelectedItem as LineItem;
                if (item == null) return;
                item.NotPlayingText = string.IsNullOrEmpty(txtNotPlaying.Text) ? "Not Playing" : txtNotPlaying.Text;
                EvaluateDirty();
            };

            chkSkipNotPlaying.CheckedChanged += (s, e) =>
            {
                if (suppressLabel) return;
                var item = localList.SelectedItem as LineItem;
                if (item == null) return;
                item.SkipIfNotPlaying = chkSkipNotPlaying.Checked;
                EvaluateDirty();
            };

            chkNpSpotify.CheckedChanged += (s, e) =>
            {
                if (suppressLabel) return;
                var item = localList.SelectedItem as LineItem;
                if (item == null) return;
                item.NowPlayingSpotify = chkNpSpotify.Checked;
                EvaluateDirty();
            };
            chkNpYouTube.CheckedChanged += (s, e) =>
            {
                if (suppressLabel) return;
                var item = localList.SelectedItem as LineItem;
                if (item == null) return;
                item.NowPlayingYouTube = chkNpYouTube.Checked;
                EvaluateDirty();
            };
            chkNpOther.CheckedChanged += (s, e) =>
            {
                if (suppressLabel) return;
                var item = localList.SelectedItem as LineItem;
                if (item == null) return;
                item.NowPlayingOtherPlayer = chkNpOther.Checked;
                EvaluateDirty();
            };

            void ApplyWarnShift(bool warnVisible)
            {
                const int warningTop = 153;
                const int labelTopWithoutWarning = 158;
                const int labelTopWithWarning = 181;

                int labelTop = warnVisible ? labelTopWithWarning : labelTopWithoutWarning;
                scrollWarnLabel.Top  = warningTop;
                scrollWarnLabel.Left = 55;

                lblSel.Top  = labelTop + 4;
                txtLabel.Top = labelTop;
                btnInfo.Top  = labelTop;
                lblDur.Top   = labelTop + 4;
                nudDur.Top   = labelTop;

                int optionTop = labelTop + 27;
                chkFahrenheit.Top = optionTop;
                chkRamMb.Top      = optionTop;
                chkNpSpotify.Top  = optionTop;
                chkNpYouTube.Top  = optionTop;
                chkNpOther.Top    = optionTop;

                int detailTop = optionTop + 28;
                localSensorLabel.Top = detailTop + 4;
                localSensorCombo.Top = detailTop;
                lblNotPlaying.Top    = detailTop + 4;
                txtNotPlaying.Top    = detailTop;

                chkSkipNotPlaying.Top = detailTop + 27;
                UpdateHeight();
            }

            void UpdateHeight()
            {
                // During tab switches WinForms can report every child as not visible
                // because the parent TabPage is hidden. Do not measure in that state.
                if (!group.Visible)
                {
                    onResize?.Invoke();
                    return;
                }

                // Find whichever ext panel (if any) is hosted by this group
                Panel activeExt = null;
                if (_cpuTempExtPanel != null && _cpuTempHost == group && _cpuTempExtPanel.Visible) activeExt = _cpuTempExtPanel;
                else if (_gpuTempExtPanel != null && _gpuTempHost == group && _gpuTempExtPanel.Visible) activeExt = _gpuTempExtPanel;
                else if (_weatherExtPanel != null && _weatherHost == group && _weatherExtPanel.Visible) activeExt = _weatherExtPanel;

                // Compute bottom of all non-ext controls
                int maxBottom = 0;
                foreach (Control c in group.Controls)
                    if (c.Visible && c != activeExt)
                        maxBottom = Math.Max(maxBottom, c.Bottom);

                // Position ext panel below all other content
                if (activeExt != null)
                {
                    activeExt.Location = new Point(8, maxBottom + 8);
                    activeExt.Width    = Math.Max(1, group.ClientSize.Width - 16);
                    maxBottom = activeExt.Bottom;
                }

                int newH = maxBottom + 10;
                if (group.Height == newH) { onResize?.Invoke(); return; }
                group.Height = newH;
                onResize?.Invoke();
            }

            group.Controls.AddRange(new Control[] { localList, btnUp, btnDown, btnRemove, localCombo, btnAdd, scrollWarnLabel, lblSel, txtLabel, btnInfo, lblDur, nudDur, chkFahrenheit, chkRamMb, localSensorLabel, localSensorCombo, chkNpSpotify, chkNpYouTube, chkNpOther, lblNotPlaying, txtNotPlaying, chkSkipNotPlaying });

            // Start collapsed — hide per-item controls until a widget is selected
            lblSel.Visible = txtLabel.Visible = lblDur.Visible = nudDur.Visible = false;

            // Register so ext-panel height changes (e.g. paste gap NUD) can propagate up
            _lineEditorUpdateHeightCallbacks.Add(() => ApplyWarnShift(warnRowVisible));

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
                case "time":           return "⏰{time:hh:mm:ss tt}";
                case "date":           return "📅 {date:MMM dd, yyyy}";
                case "weather":        return fahrenheit ? "{wicon} {temp:F0}°F - {condition}" : "{wicon} {temp:F0}°C - {condition}";
                case "nowplaying":     return "{source} {artist} - {title} - {elapsed:mm:ss}/{duration:mm:ss}";
                default:               return "{value}";
            }
        }

        private static int EstimateRenderedLength(string label, string itemType)
        {
            if (string.IsNullOrEmpty(label)) return 0;
            string est = label;
            est = Regex.Replace(est, @"\{temp:[^}]*\}", "100.0");
            est = Regex.Replace(est, @"\{pct:[^}]*\}",  "100");
            est = Regex.Replace(est, @"\{used:[^}]*\}", "16.0");
            est = Regex.Replace(est, @"\{total:[^}]*\}", "128");
            est = Regex.Replace(est, @"\{time:[^}]*\}", "11:59:59 PM");
            est = Regex.Replace(est, @"\{date:[^}]*\}", "May 17, 2026");
            est = Regex.Replace(est, @"\{feelslike:[^}]*\}", "100.0");
            est = Regex.Replace(est, @"\{humidity:[^}]*\}", "100");
            est = Regex.Replace(est, @"\{wind:[^}]*\}", "100");
            est = est.Replace("{condition}", "Partly cloudy");
            est = est.Replace("{humidity}", "100");
            est = est.Replace("{title}",  "Song Title");
            est = est.Replace("{artist}", "Artist");
            est = est.Replace("{album}",  "Album");
            est = est.Replace("{wicon}",  "☀");
            est = est.Replace("{source}", "♪");
            string norm = itemType?.ToLower().Replace(" ", "").Replace("_", "") ?? "";
            string valueRepl;
            switch (norm)
            {
                case "cputemperature":
                case "gputemperature": valueRepl = "100.0°C";      break;
                case "cpuusage":
                case "gpuusage":       valueRepl = "100%";          break;
                case "ramusage":       valueRepl = "16.0/128GB";    break;
                case "time":           valueRepl = "⏰11:59:59 PM";  break;
                case "date":           valueRepl = "📅 May 17, 2026"; break;
                case "weather":        valueRepl = "☀72°F Cloudy";  break;
                case "nowplaying":     valueRepl = "♪Artist-Title"; break;
                default:               valueRepl = "??";             break;
            }
            est = est.Replace("{value}", valueRepl);
            return est.Length;
        }

        // ----------------------------------------------------------------
        //  Type-specific ext panels (embedded inside widget line editors)
        // ----------------------------------------------------------------
        private void BuildTypeExtPanels()
        {
            // Read hardware cache
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
            _cpuDefaultWarn   = GetCpuAutoWarning(_detectedCpuName);
            _cpuDefaultCrit   = GetCpuAutoCritical(_detectedCpuName);
            _gpuDefaultWarn   = GetGpuAutoWarning(_detectedGpuName);
            _gpuDefaultCrit   = GetGpuAutoCritical(_detectedGpuName);

            // ── CPU Temperature ext panel ──────────────────────────────
            _cpuTempExtPanel = new Panel { BackColor = Color.FromArgb(237, 237, 242) };
            _cpuTempExtPanel.Controls.Add(new Panel { Height = 1, Dock = DockStyle.Top, BackColor = Color.FromArgb(200, 200, 210) });

            _cpuAvBlockedRow = new Panel
            {
                Location  = new Point(0, 8),
                Size      = new Size(460, 24),
                BackColor = _cpuTempExtPanel.BackColor,
                Visible   = false
            };
            _cpuAvBlockedLabel = new Label
            {
                Text      = "Application was blocked by AV",
                AutoSize  = true,
                Location  = new Point(0, 4),
                ForeColor = Color.FromArgb(205, 35, 35),
                Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };
            _cpuAvBlockedInfoButton = CreateBlueInfoButton(new Point(202, 2));
            _cpuAvBlockedInfoButton.BackColor = _cpuTempExtPanel.BackColor;
            _cpuAvBlockedInfoButton.FlatAppearance.MouseOverBackColor = _cpuTempExtPanel.BackColor;
            _cpuAvBlockedInfoButton.FlatAppearance.MouseDownBackColor = _cpuTempExtPanel.BackColor;
            _cpuAvBlockedInfoButton.Click += (s, e) => ShowCpuAvBlockedHelp();
            _cpuAvBlockedRow.Controls.Add(_cpuAvBlockedLabel);
            _cpuAvBlockedRow.Controls.Add(_cpuAvBlockedInfoButton);
            _cpuTempExtPanel.Controls.Add(_cpuAvBlockedRow);

            _cpuTempPollRow = new Panel { Location = new Point(0, 8), Size = new Size(460, 26) };
            _cpuTempPollRow.Controls.Add(new Label { Text = "Temperature poll interval:", AutoSize = true, Location = new Point(0, 4) });
            _tempUpdateNud = new NumericUpDown { Location = new Point(175, 0), Size = new Size(80, 22), Minimum = 500, Maximum = 30000, Increment = 500, Value = 2000 };
            _cpuTempPollRow.Controls.Add(_tempUpdateNud);
            _cpuTempPollRow.Controls.Add(new Label { Text = "ms", AutoSize = true, ForeColor = Color.Gray, Location = new Point(260, 4) });
            _cpuTempExtPanel.Controls.Add(_cpuTempPollRow);

            _enableCpuWarnCheck = new CheckBox { Text = "Show CPU temperature warning indicators on the display", AutoSize = true, Location = new Point(0, 40) };
            _cpuTempExtPanel.Controls.Add(_enableCpuWarnCheck);

            _cpuThreshGroup = BuildThresholdGroup("CPU Thresholds",
                new Point(0, 62),
                _detectedCpuName, _cpuAutoAvailable,
                () => _cpuDefaultWarn, () => _cpuDefaultCrit,
                GetCpuMaximum(_detectedCpuName),
                out _cpuAutoCheck, out _cpuWarnNud, out _cpuCritNud,
                out _cpuHwStatusLabel);
            _cpuThreshGroup.Width = 460;
            _cpuTempExtPanel.Controls.Add(_cpuThreshGroup);
            ApplyCpuAvBlockedVisual();

            _cpuAutoCheck.CheckedChanged += (s, e) =>
            {
                if (_suppressExtPopulate) return;
                var item = GetSelectedCpuItem();
                if (item == null) return;
                item.WarnTemp = _cpuAutoCheck.Checked ? "AUTO" : ((int)_cpuWarnNud.Value).ToString();
                item.CritTemp = _cpuAutoCheck.Checked ? "AUTO" : ((int)_cpuCritNud.Value).ToString();
                EvaluateDirty();
            };
            _cpuWarnNud.ValueChanged += (s, e) =>
            {
                if (_suppressExtPopulate || _cpuAutoCheck.Checked) return;
                var item = GetSelectedCpuItem();
                if (item != null) item.WarnTemp = ((int)_cpuWarnNud.Value).ToString();
                EvaluateDirty();
            };
            _cpuCritNud.ValueChanged += (s, e) =>
            {
                if (_suppressExtPopulate || _cpuAutoCheck.Checked) return;
                var item = GetSelectedCpuItem();
                if (item != null) item.CritTemp = ((int)_cpuCritNud.Value).ToString();
                EvaluateDirty();
            };

            _enableCpuWarnCheck.CheckedChanged += (s, e) =>
            {
                bool en = _enableCpuWarnCheck.Checked;
                _cpuAutoCheck.Enabled = en && _cpuAutoAvailable;
                _cpuWarnNud.Enabled   = en && !_cpuAutoCheck.Checked;
                _cpuCritNud.Enabled   = en && !_cpuAutoCheck.Checked;
                if (!_suppressExtPopulate) { var item = GetSelectedCpuItem(); if (item != null) item.EnableWarnIndicators = en; }
                EvaluateDirty();
            };

            // ── GPU Temperature ext panel ──────────────────────────────
            _gpuTempExtPanel = new Panel { BackColor = Color.FromArgb(237, 237, 242) };
            _gpuTempExtPanel.Controls.Add(new Panel { Height = 1, Dock = DockStyle.Top, BackColor = Color.FromArgb(200, 200, 210) });

            var gpuPollRow = new Panel { Location = new Point(0, 8), Size = new Size(460, 26) };
            gpuPollRow.Controls.Add(new Label { Text = "Temperature poll interval:", AutoSize = true, Location = new Point(0, 4) });
            _gpuTempUpdateNud = new NumericUpDown { Location = new Point(175, 0), Size = new Size(80, 22), Minimum = 500, Maximum = 30000, Increment = 500, Value = 2000 };
            gpuPollRow.Controls.Add(_gpuTempUpdateNud);
            gpuPollRow.Controls.Add(new Label { Text = "ms", AutoSize = true, ForeColor = Color.Gray, Location = new Point(260, 4) });
            _gpuTempExtPanel.Controls.Add(gpuPollRow);

            _enableGpuWarnCheck = new CheckBox { Text = "Show GPU temperature warning indicators on the display", AutoSize = true, Location = new Point(0, 40) };
            _gpuTempExtPanel.Controls.Add(_enableGpuWarnCheck);

            var gpuThreshGroup = BuildThresholdGroup("GPU Thresholds",
                new Point(0, 62),
                _detectedGpuName, _gpuAutoAvailable,
                () => _gpuDefaultWarn, () => _gpuDefaultCrit,
                GetGpuMaximum(_detectedGpuName),
                out _gpuAutoCheck, out _gpuWarnNud, out _gpuCritNud,
                out _gpuHwStatusLabel);
            gpuThreshGroup.Width = 460;

            // Thermal paste monitoring inside GPU group (independent of indicator checkbox)
            const int PasteCollapsedH = 156;  // 116 + 40
            const int PasteExpandedH  = 184;  // + gap-NUD row (24px) + 4px gap
            gpuThreshGroup.Size = new Size(gpuThreshGroup.Width, PasteCollapsedH);
            _gpuPasteCheck = new CheckBox { Text = "Enable GPU thermal paste monitoring", AutoSize = true, Location = new Point(8, 114) };
            var pasteHint  = new Label
            {
                Text      = "Scrolling warning when hotspot temperature gap exceeds configured threshold",
                AutoSize  = true,
                Location  = new Point(26, 134),
                ForeColor = Color.Gray,
                Font      = new Font("Segoe UI", 8f)
            };

            // Gap threshold row — hidden until paste monitoring is enabled
            var pasteGapRow = new Panel { Location = new Point(8, 152), Size = new Size(440, 24), Visible = false };
            pasteGapRow.Controls.Add(new Label { Text = "Max gap:", AutoSize = true, Location = new Point(0, 4) });
            _gpuPasteGapNud = new NumericUpDown { Location = new Point(62, 1), Size = new Size(65, 22), Minimum = 1, Maximum = 100, Value = 15 };
            pasteGapRow.Controls.Add(_gpuPasteGapNud);
            pasteGapRow.Controls.Add(new Label { Text = "°C", AutoSize = true, Location = new Point(132, 4) });

            gpuThreshGroup.Controls.Add(_gpuPasteCheck);
            gpuThreshGroup.Controls.Add(pasteHint);
            gpuThreshGroup.Controls.Add(pasteGapRow);

            _gpuTempExtPanel.Controls.Add(gpuThreshGroup);
            _gpuTempExtPanel.Size = new Size(460, gpuThreshGroup.Bottom + 6);

            _gpuAutoCheck.CheckedChanged += (s, e) =>
            {
                if (_suppressExtPopulate) return;
                var item = GetSelectedGpuItem();
                if (item == null) return;
                item.WarnTemp = _gpuAutoCheck.Checked ? "AUTO" : ((int)_gpuWarnNud.Value).ToString();
                item.CritTemp = _gpuAutoCheck.Checked ? "AUTO" : ((int)_gpuCritNud.Value).ToString();
                EvaluateDirty();
            };
            _gpuWarnNud.ValueChanged += (s, e) =>
            {
                if (_suppressExtPopulate || _gpuAutoCheck.Checked) return;
                var item = GetSelectedGpuItem();
                if (item != null) item.WarnTemp = ((int)_gpuWarnNud.Value).ToString();
                EvaluateDirty();
            };
            _gpuCritNud.ValueChanged += (s, e) =>
            {
                if (_suppressExtPopulate || _gpuAutoCheck.Checked) return;
                var item = GetSelectedGpuItem();
                if (item != null) item.CritTemp = ((int)_gpuCritNud.Value).ToString();
                EvaluateDirty();
            };

            _gpuPasteCheck.CheckedChanged += (s, e) =>
            {
                bool on = _gpuPasteCheck.Checked;
                pasteGapRow.Visible    = on;
                gpuThreshGroup.Height  = on ? PasteExpandedH : PasteCollapsedH;
                _gpuTempExtPanel.Height = gpuThreshGroup.Bottom + 6;
                foreach (var cb in _lineEditorUpdateHeightCallbacks) cb?.Invoke();
                if (!_suppressExtPopulate) { var item = GetSelectedGpuItem(); if (item != null) item.GpuPasteMonitoring = on; }
                EvaluateDirty();
            };

            _enableGpuWarnCheck.CheckedChanged += (s, e) =>
            {
                bool en = _enableGpuWarnCheck.Checked;
                _gpuAutoCheck.Enabled = en && _gpuAutoAvailable;
                _gpuWarnNud.Enabled   = en && !_gpuAutoCheck.Checked;
                _gpuCritNud.Enabled   = en && !_gpuAutoCheck.Checked;
                if (!_suppressExtPopulate) { var item = GetSelectedGpuItem(); if (item != null) item.EnableWarnIndicators = en; }
                EvaluateDirty();
            };

            // CPU poll NUD writes to the currently-selected CPU temp item
            _tempUpdateNud.ValueChanged += (s, e) =>
            {
                if (_suppressExtPopulate) return;
                var item = GetSelectedCpuItem();
                if (item != null) item.PollIntervalMs = (int)_tempUpdateNud.Value;
                EvaluateDirty();
            };
            // GPU poll NUD writes to the currently-selected GPU temp item
            _gpuTempUpdateNud.ValueChanged += (s, e) =>
            {
                if (_suppressExtPopulate) return;
                var item = GetSelectedGpuItem();
                if (item != null) item.PollIntervalMs = (int)_gpuTempUpdateNud.Value;
                EvaluateDirty();
            };
            // Paste gap NUD writes to the currently-selected GPU temp item
            _gpuPasteGapNud.ValueChanged += (s, e) =>
            {
                if (_suppressExtPopulate) return;
                var item = GetSelectedGpuItem();
                if (item != null) item.GpuPasteGapTemp = (int)_gpuPasteGapNud.Value;
                EvaluateDirty();
            };

            // ── Weather ext panel ──────────────────────────────────────
            _weatherExtPanel = new Panel { BackColor = Color.FromArgb(237, 237, 242) };
            _weatherExtPanel.Controls.Add(new Panel { Height = 1, Dock = DockStyle.Top, BackColor = Color.FromArgb(200, 200, 210) });
            _weatherExtPanel.Controls.Add(new Label { Text = "Location:", AutoSize = true, Location = new Point(0, 10) });
            _weatherLocationBox = new TextBox { Location = new Point(70, 6), Size = new Size(380, 22) };
            _weatherLocationBox.HandleCreated += (s, e) => SetPlaceholder(_weatherLocationBox, "City, postcode, or lat,lon");
            _weatherValidationTimer = new System.Windows.Forms.Timer { Interval = 650 };
            _weatherValidationTimer.Tick += (s, e) =>
            {
                _weatherValidationTimer.Stop();
                StartWeatherLocationValidation(_queuedWeatherValidationText, _weatherValidationVersion, _queuedWeatherSuggestionsAllowed);
            };
            // Wire TextChanged AFTER creating the textbox so it's never null
            _weatherLocationBox.TextChanged += (s, e) =>
            {
                if (_suppressExtPopulate) return;
                var item = GetSelectedWeatherItem();
                if (item != null) item.WeatherLocation = _weatherLocationBox.Text;
                QueueWeatherLocationValidation(_weatherLocationBox.Text, allowSuggestions: true);
                EvaluateDirty();
            };
            _weatherLocationBox.KeyDown += (s, e) =>
            {
                if (_weatherLocationSuggestionsList == null || !_weatherLocationSuggestionsList.Visible) return;
                if (e.KeyCode == Keys.Down && _weatherLocationSuggestionsList.Items.Count > 0)
                {
                    _weatherLocationSuggestionsList.Focus();
                    _weatherLocationSuggestionsList.SelectedIndex = 0;
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    HideWeatherLocationSuggestions();
                    e.Handled = true;
                }
            };
            _weatherLocationBox.Leave += (s, e) =>
            {
                BeginInvoke((Action)(() =>
                {
                    if (_weatherLocationSuggestionsList == null || _weatherLocationSuggestionsList.Focused) return;
                    HideWeatherLocationSuggestions();
                }));
            };
            _weatherLocationHintLabel = new Label
            {
                Text      = "Leave blank to auto detect from IP",
                AutoSize  = true,
                Location  = new Point(0, 32),
                ForeColor = Color.Gray,
                Font      = new Font("Segoe UI", 7.5f)
            };
            int statusLeft = _weatherLocationHintLabel.Left + TextRenderer.MeasureText(_weatherLocationHintLabel.Text, _weatherLocationHintLabel.Font).Width + 12;
            _weatherLocationStatusLabel = new Label
            {
                Text        = "",
                AutoSize    = false,
                AutoEllipsis = true,
                Size        = new Size(Math.Max(120, 452 - statusLeft), 17),
                Location    = new Point(statusLeft, 32),
                ForeColor   = Color.Gray,
                Font        = new Font("Segoe UI", 7.5f, FontStyle.Bold)
            };
            _weatherLocationSuggestionsList = new ListBox
            {
                Location       = new Point(70, 31),
                Size           = new Size(380, 72),
                IntegralHeight = false,
                Visible        = false,
                BorderStyle    = BorderStyle.FixedSingle
            };
            _weatherLocationSuggestionsList.MouseClick += (s, e) => CommitWeatherLocationSuggestion();
            _weatherLocationSuggestionsList.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Tab)
                {
                    CommitWeatherLocationSuggestion();
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    HideWeatherLocationSuggestions();
                    _weatherLocationBox.Focus();
                    e.Handled = true;
                }
            };
            _weatherLocationSuggestionsList.Leave += (s, e) =>
            {
                BeginInvoke((Action)(() =>
                {
                    if (_weatherLocationBox == null || _weatherLocationBox.Focused) return;
                    HideWeatherLocationSuggestions();
                }));
            };
            _weatherExtPanel.Controls.Add(_weatherLocationBox);
            _weatherExtPanel.Controls.Add(_weatherLocationHintLabel);
            _weatherExtPanel.Controls.Add(_weatherLocationStatusLabel);
            _weatherExtPanel.Controls.Add(_weatherLocationSuggestionsList);
            _weatherExtPanel.Size = new Size(460, 52);
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
        //  Per-item ext panel helpers
        // ----------------------------------------------------------------
        private static string NormType(LineItem item) =>
            item?.Type?.ToLower().Replace(" ", "").Replace("_", "") ?? "";

        private LineItem GetSelectedCpuItem()
        {
            if (_topList?.SelectedItem is LineItem ti && NormType(ti) == "cputemperature") return ti;
            if (_bottomList?.SelectedItem is LineItem bi && NormType(bi) == "cputemperature") return bi;
            return null;
        }
        private LineItem GetSelectedGpuItem()
        {
            if (_topList?.SelectedItem is LineItem ti && NormType(ti) == "gputemperature") return ti;
            if (_bottomList?.SelectedItem is LineItem bi && NormType(bi) == "gputemperature") return bi;
            return null;
        }
        private LineItem GetSelectedWeatherItem()
        {
            if (_topList?.SelectedItem is LineItem ti && NormType(ti) == "weather") return ti;
            if (_bottomList?.SelectedItem is LineItem bi && NormType(bi) == "weather") return bi;
            return null;
        }

        private void ApplyCpuAvBlockedVisual()
        {
            if (_cpuTempExtPanel == null ||
                _cpuAvBlockedRow == null ||
                _cpuTempPollRow == null ||
                _enableCpuWarnCheck == null ||
                _cpuThreshGroup == null)
            {
                return;
            }

            int y = 8;
            _cpuAvBlockedRow.Visible = _cpuTemperatureBlockedByAv;
            if (_cpuTemperatureBlockedByAv)
            {
                _cpuAvBlockedRow.Location = new Point(0, y);
                y += _cpuAvBlockedRow.Height + 4;
            }

            _cpuTempPollRow.Location = new Point(0, y);
            y += _cpuTempPollRow.Height + 6;
            _enableCpuWarnCheck.Location = new Point(0, y);
            y += _enableCpuWarnCheck.Height + 4;
            _cpuThreshGroup.Location = new Point(0, y);
            _cpuTempExtPanel.Size = new Size(460, _cpuThreshGroup.Bottom + 6);
        }

        private void PopulateCpuExtPanel(LineItem item)
        {
            if (item == null) return;
            _suppressExtPopulate = true;
            _tempUpdateNud.Value = Clamp(_tempUpdateNud, item.PollIntervalMs);
            _enableCpuWarnCheck.Checked = item.EnableWarnIndicators;
            ApplyThresholdGroup(item.WarnTemp, item.CritTemp,
                _cpuAutoCheck, _cpuWarnNud, _cpuCritNud, _cpuAutoAvailable, _cpuDefaultWarn, _cpuDefaultCrit);
            bool en = _enableCpuWarnCheck.Checked;
            _cpuAutoCheck.Enabled = en && _cpuAutoAvailable;
            _cpuWarnNud.Enabled   = en && !_cpuAutoCheck.Checked;
            _cpuCritNud.Enabled   = en && !_cpuAutoCheck.Checked;
            _suppressExtPopulate = false;
            ApplyCpuAvBlockedVisual();
        }

        private void PopulateGpuExtPanel(LineItem item)
        {
            if (item == null) return;
            _suppressExtPopulate = true;
            _gpuTempUpdateNud.Value = Clamp(_gpuTempUpdateNud, item.PollIntervalMs);
            _enableGpuWarnCheck.Checked = item.EnableWarnIndicators;
            ApplyThresholdGroup(item.WarnTemp, item.CritTemp,
                _gpuAutoCheck, _gpuWarnNud, _gpuCritNud, _gpuAutoAvailable, _gpuDefaultWarn, _gpuDefaultCrit);
            bool en = _enableGpuWarnCheck.Checked;
            _gpuAutoCheck.Enabled = en && _gpuAutoAvailable;
            _gpuWarnNud.Enabled   = en && !_gpuAutoCheck.Checked;
            _gpuCritNud.Enabled   = en && !_gpuAutoCheck.Checked;
            _gpuPasteCheck.Checked  = item.GpuPasteMonitoring;
            _gpuPasteGapNud.Value   = Clamp(_gpuPasteGapNud, item.GpuPasteGapTemp);
            _suppressExtPopulate = false;
        }

        private void PopulateWeatherExtPanel(LineItem item)
        {
            if (item == null) return;
            _suppressExtPopulate = true;
            _weatherLocationBox.Text = item.WeatherLocation ?? "";
            _suppressExtPopulate = false;
            HideWeatherLocationSuggestions();
            QueueWeatherLocationValidation(_weatherLocationBox.Text, allowSuggestions: false);
        }

        private sealed class WeatherLocationValidationResult
        {
            public bool IsValid { get; set; }
            public string Suggestion { get; set; }
            public List<string> Suggestions { get; set; } = new List<string>();
        }

        private void QueueWeatherLocationValidation(string location, bool allowSuggestions)
        {
            if (_weatherLocationStatusLabel == null) return;

            _queuedWeatherValidationText = location ?? "";
            _queuedWeatherSuggestionsAllowed = allowSuggestions;
            _weatherValidationVersion++;
            _weatherValidationTimer?.Stop();
            HideWeatherLocationSuggestions();

            if (string.IsNullOrWhiteSpace(_queuedWeatherValidationText))
            {
                SetWeatherLocationStatus(true, null);
                return;
            }

            _weatherLocationStatusLabel.Text = "Checking...";
            _weatherLocationStatusLabel.ForeColor = Color.Gray;
            _weatherValidationTimer?.Start();
        }

        private void StartWeatherLocationValidation(string location, int version, bool allowSuggestions)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                var suggestions = allowSuggestions
                    ? FetchWeatherLocationSuggestions(location, 5)
                    : new List<string>();

                if (allowSuggestions && suggestions.Count > 0 && !IsDisposed && IsHandleCreated)
                {
                    try
                    {
                        BeginInvoke((Action)(() =>
                        {
                            if (IsDisposed || version != _weatherValidationVersion) return;
                            if (!string.Equals(_weatherLocationBox?.Text ?? "", location ?? "", StringComparison.Ordinal)) return;
                            SetWeatherLocationSuggestions(location, suggestions);
                        }));
                    }
                    catch { }
                }

                var result = ValidateWeatherLocation(location, suggestions);
                if (IsDisposed || !IsHandleCreated) return;

                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (IsDisposed || version != _weatherValidationVersion) return;
                        if (!string.Equals(_weatherLocationBox?.Text ?? "", location ?? "", StringComparison.Ordinal)) return;
                        SetWeatherLocationStatus(result.IsValid, result.Suggestion);
                        if (allowSuggestions && suggestions.Count == 0)
                            SetWeatherLocationSuggestions(location, result.Suggestions);
                    }));
                }
                catch { }
            });
        }

        private void SetWeatherLocationSuggestions(string query, IEnumerable<string> suggestions)
        {
            if (_weatherLocationSuggestionsList == null) return;

            _weatherLocationSuggestionsList.BeginUpdate();
            _weatherLocationSuggestionsList.Items.Clear();
            string normalizedQuery = NormalizeLocationText(query);
            foreach (string suggestion in suggestions ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(suggestion)) continue;
                if (NormalizeLocationText(suggestion) == normalizedQuery) continue;
                if (!_weatherLocationSuggestionsList.Items.Contains(suggestion))
                    _weatherLocationSuggestionsList.Items.Add(suggestion);
            }
            _weatherLocationSuggestionsList.EndUpdate();

            _weatherLocationSuggestionsList.Visible = _weatherLocationSuggestionsList.Items.Count > 0;
            UpdateWeatherExtPanelLayout();
        }

        private void HideWeatherLocationSuggestions()
        {
            if (_weatherLocationSuggestionsList == null) return;
            if (!_weatherLocationSuggestionsList.Visible && _weatherLocationSuggestionsList.Items.Count == 0)
            {
                UpdateWeatherExtPanelLayout();
                return;
            }

            _weatherLocationSuggestionsList.Visible = false;
            _weatherLocationSuggestionsList.Items.Clear();
            UpdateWeatherExtPanelLayout();
        }

        private void CommitWeatherLocationSuggestion()
        {
            if (_weatherLocationSuggestionsList == null || _weatherLocationSuggestionsList.SelectedItem == null) return;

            string suggestion = _weatherLocationSuggestionsList.SelectedItem.ToString();
            HideWeatherLocationSuggestions();
            _weatherLocationBox.Text = suggestion;
            _weatherLocationBox.SelectionStart = _weatherLocationBox.Text.Length;
            _weatherLocationBox.Focus();
        }

        private void UpdateWeatherExtPanelLayout()
        {
            if (_weatherExtPanel == null) return;

            bool showSuggestions = _weatherLocationSuggestionsList != null &&
                                   _weatherLocationSuggestionsList.Visible &&
                                   _weatherLocationSuggestionsList.Items.Count > 0;
            if (_weatherLocationHintLabel != null) _weatherLocationHintLabel.Visible = !showSuggestions;
            if (_weatherLocationStatusLabel != null) _weatherLocationStatusLabel.Visible = !showSuggestions;

            int targetHeight = showSuggestions ? _weatherLocationSuggestionsList.Bottom + 4 : 52;
            if (_weatherExtPanel.Height == targetHeight) return;

            _weatherExtPanel.Height = targetHeight;
            foreach (var cb in _lineEditorUpdateHeightCallbacks) cb?.Invoke();
        }

        private void SetWeatherLocationStatus(bool isValid, string suggestion)
        {
            if (_weatherLocationStatusLabel == null) return;

            if (isValid)
            {
                _weatherLocationStatusLabel.Text = "Valid location";
                _weatherLocationStatusLabel.ForeColor = Color.FromArgb(20, 135, 45);
                return;
            }

            _weatherLocationStatusLabel.Text = string.IsNullOrWhiteSpace(suggestion)
                ? "Invalid location"
                : "Invalid location, did you mean " + suggestion + "?";
            _weatherLocationStatusLabel.ForeColor = Color.FromArgb(190, 35, 35);
        }

        private static WeatherLocationValidationResult ValidateWeatherLocation(string location, List<string> citySuggestions = null)
        {
            string query = (location ?? "").Trim();
            if (string.IsNullOrWhiteSpace(query))
                return new WeatherLocationValidationResult { IsValid = true };

            citySuggestions = citySuggestions ?? FetchWeatherLocationSuggestions(query, 5);
            try
            {
                string url = "https://wttr.in/" + Uri.EscapeDataString(query) + "?format=j1";
                string json = WeatherValidationClient.GetStringAsync(url).Result;
                var jobj = JObject.Parse(json);

                var area = jobj["nearest_area"]?[0];
                string areaName = area?["areaName"]?[0]?["value"]?.ToString() ?? "";
                string region = area?["region"]?[0]?["value"]?.ToString() ?? "";
                string country = area?["country"]?[0]?["value"]?.ToString() ?? "";
                string suggestion = FormatWeatherLocationSuggestion(areaName, region, country);

                var cond = jobj["current_condition"]?[0];
                bool hasWeather = cond != null && (
                    cond["temp_C"] != null ||
                    cond["weatherDesc"] != null ||
                    cond["humidity"] != null);

                if (hasWeather && (LooksLikeCoordinates(query) || LooksLikePostalCode(query)))
                    return new WeatherLocationValidationResult { IsValid = true, Suggestion = suggestion, Suggestions = citySuggestions };

                if (hasWeather && IsWeatherLocationMatch(query, areaName, region, country))
                    return new WeatherLocationValidationResult { IsValid = true, Suggestion = suggestion, Suggestions = citySuggestions };

                return new WeatherLocationValidationResult
                {
                    IsValid = false,
                    Suggestion = !string.IsNullOrWhiteSpace(suggestion)
                        ? suggestion
                        : citySuggestions.FirstOrDefault() ?? "",
                    Suggestions = citySuggestions
                };
            }
            catch
            {
                return new WeatherLocationValidationResult
                {
                    IsValid = false,
                    Suggestion = citySuggestions.FirstOrDefault() ?? "",
                    Suggestions = citySuggestions
                };
            }
        }

        private static string FetchWeatherLocationSuggestion(string query)
        {
            return FetchWeatherLocationSuggestions(query, 1).FirstOrDefault() ?? "";
        }

        private static List<string> FetchWeatherLocationSuggestions(string query, int count)
        {
            var suggestions = new List<string>();
            if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2) return suggestions;
            if (LooksLikeCoordinates(query) || query.Any(char.IsDigit)) return suggestions;

            try
            {
                string url = "https://geocoding-api.open-meteo.com/v1/search?name=" +
                             Uri.EscapeDataString(query) +
                             "&count=" + Math.Max(1, count).ToString() + "&language=en&format=json";
                string json = WeatherValidationClient.GetStringAsync(url).Result;
                var results = JObject.Parse(json)["results"] as JArray;
                if (results == null) return suggestions;

                foreach (var result in results)
                {
                    string suggestion = FormatWeatherLocationSuggestion(
                        result["name"]?.ToString() ?? "",
                        result["admin1"]?.ToString() ?? "",
                        result["country"]?.ToString() ?? "");
                    if (!string.IsNullOrWhiteSpace(suggestion) &&
                        !suggestions.Any(s => s.Equals(suggestion, StringComparison.OrdinalIgnoreCase)))
                        suggestions.Add(suggestion);
                }
            }
            catch
            {
            }
            return suggestions;
        }

        private static bool IsWeatherLocationMatch(string query, string areaName, string region, string country)
        {
            string q = NormalizeLocationText(query);
            string area = NormalizeLocationText(areaName);
            string display = NormalizeLocationText(FormatWeatherLocationSuggestion(areaName, region, country));

            if (string.IsNullOrEmpty(q) || string.IsNullOrEmpty(area)) return false;
            if (q == area || q == display || display.Contains(q)) return true;
            if (q.StartsWith(area + " ", StringComparison.Ordinal)) return true;
            if (q.Length >= 4 && area.StartsWith(q, StringComparison.Ordinal)) return true;

            string qCompact = q.Replace(" ", "");
            string areaCompact = area.Replace(" ", "");
            string displayCompact = display.Replace(" ", "");
            if (qCompact == areaCompact || qCompact == displayCompact || displayCompact.Contains(qCompact)) return true;
            if (qCompact.StartsWith(areaCompact, StringComparison.Ordinal)) return true;

            return false;
        }

        private static bool LooksLikeCoordinates(string value)
        {
            var m = Regex.Match((value ?? "").Trim(), @"^\s*(-?\d{1,3}(?:\.\d+)?)\s*,\s*(-?\d{1,3}(?:\.\d+)?)\s*$");
            if (!m.Success) return false;

            return double.TryParse(m.Groups[1].Value, out double lat) &&
                   double.TryParse(m.Groups[2].Value, out double lon) &&
                   lat >= -90 && lat <= 90 &&
                   lon >= -180 && lon <= 180;
        }

        private static bool LooksLikePostalCode(string value)
        {
            string trimmed = (value ?? "").Trim();
            if (trimmed.Length < 3 || trimmed.Length > 12) return false;
            if (!trimmed.Any(char.IsDigit)) return false;
            return Regex.IsMatch(trimmed, @"^[A-Za-z0-9][A-Za-z0-9\s-]*$");
        }

        private static string FormatWeatherLocationSuggestion(string name, string region, string country)
        {
            var parts = new List<string>();
            AddWeatherLocationPart(parts, name);
            AddWeatherLocationPart(parts, region);
            AddWeatherLocationPart(parts, country);
            return string.Join(", ", parts);
        }

        private static void AddWeatherLocationPart(List<string> parts, string part)
        {
            part = (part ?? "").Trim();
            if (part.Length == 0) return;
            if (parts.Any(p => p.Equals(part, StringComparison.OrdinalIgnoreCase))) return;
            parts.Add(part);
        }

        private static string NormalizeLocationText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";

            string normalized = value.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder(normalized.Length);
            foreach (char c in normalized)
            {
                var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
                if (category == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
                sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
            }
            return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        }

        // ----------------------------------------------------------------
        //  Ext panel host management
        // ----------------------------------------------------------------
        private void ShowExtPanel(Panel extPanel, ref GroupBox host, GroupBox target, Func<ListBox> getOtherList)
        {
            if (extPanel == null) return;
            if (host == target) { extPanel.Visible = true; return; }

            if (host != null)
            {
                // Steal from another group — detach first, then clear that list's selection.
                host = target;
                extPanel.Visible = false;
                extPanel.Parent  = null;
                getOtherList?.Invoke()?.ClearSelected();
            }
            else
            {
                host = target;
            }
            extPanel.Parent  = target;
            extPanel.Visible = true;
        }

        private static void HideExtPanel(Panel extPanel, ref GroupBox host, GroupBox source)
        {
            if (extPanel == null || host != source) return;
            extPanel.Visible = false;
            extPanel.Parent  = null;
            host = null;
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

            // Display font (type)
            var fontGroup = new GroupBox { Text = "Display Font", Location = new Point(10, 128), Size = new Size(460, 76) };
            fontGroup.Controls.Add(new Label { Text = "Font:", AutoSize = true, Location = new Point(8, 26) });
            _oledFontCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(52, 22), Size = new Size(180, 22) };
            _oledFontCombo.Items.Add("(Default)");
            foreach (string f in GetCuratedOledFonts())
                _oledFontCombo.Items.Add(f);
            _oledFontCombo.SelectedIndex = 0;
            var btnFontInfo = CreateBlueInfoButton(new Point(238, 23));
            btnFontInfo.Click += (s, e) => ShowFontSampleHelp();
            var fontHint = new Label
            {
                Text      = "Custom font renders as image - leave blank for built-in display font",
                AutoSize  = false,
                Size      = new Size(444, 16),
                Location  = new Point(8, 48),
                ForeColor = Color.Gray,
                Font      = new Font("Segoe UI", 7.5f)
            };
            _fontNotInstalledLabel = new Label
            {
                Text      = "",
                AutoSize  = true,
                Location  = new Point(52, 48),
                ForeColor = Color.FromArgb(200, 40, 40),
                Font      = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                Visible   = false
            };
            fontGroup.Controls.Add(_oledFontCombo);
            fontGroup.Controls.Add(btnFontInfo);
            fontGroup.Controls.Add(fontHint);
            fontGroup.Controls.Add(_fontNotInstalledLabel);

            var btnReset = new Button
            {
                Text      = "Reset All Settings to Defaults",
                Location  = new Point(10, 216),
                Size      = new Size(210, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(180, 40, 40),
                ForeColor = Color.White
            };
            btnReset.FlatAppearance.BorderSize = 0;
            btnReset.Click += OnResetDefaultsClick;

            tab.Controls.AddRange(new Control[] { _capsLockCheck, _showUpdateCheck, _ggPathBox, btnBrowse, fontGroup, btnReset });
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

        private void DrawLineItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;

            var list = sender as ListBox;
            var item = list?.Items[e.Index] as LineItem;
            string text = item?.ListDisplayName ?? list?.Items[e.Index]?.ToString() ?? "";
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            bool blockedCpuTemp = _cpuTemperatureBlockedByAv && NormType(item) == "cputemperature";

            Color back = selected ? SystemColors.Highlight : list?.BackColor ?? Color.White;
            Color fore = selected ? SystemColors.HighlightText : list?.ForeColor ?? Color.Black;
            if (blockedCpuTemp)
                fore = selected ? Color.FromArgb(220, 220, 225) : Color.FromArgb(135, 135, 145);

            using (var br = new SolidBrush(back))
                e.Graphics.FillRectangle(br, e.Bounds);

            var bounds = new Rectangle(e.Bounds.Left + 2, e.Bounds.Top, e.Bounds.Width - 4, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, text, e.Font, bounds, fore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            if ((e.State & DrawItemState.Focus) == DrawItemState.Focus)
                e.DrawFocusRectangle();
        }

        private Button CreateBlueInfoButton(Point location)
        {
            bool infoHover = false, infoDown = false;
            var btn = new Button
            {
                Text      = "",
                Size      = new Size(20, 20),
                Location  = location,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(245, 245, 248),
                ForeColor = Color.White,
                Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold | FontStyle.Italic),
                Cursor    = Cursors.Hand,
                TabStop   = false
            };
            btn.FlatAppearance.BorderSize          = 0;
            btn.FlatAppearance.MouseOverBackColor  = Color.FromArgb(245, 245, 248);
            btn.FlatAppearance.MouseDownBackColor  = Color.FromArgb(245, 245, 248);
            btn.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode   = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                Color fill = infoDown  ? Color.FromArgb(0,  80, 170)
                           : infoHover ? Color.FromArgb(0, 100, 190)
                                       : Color.FromArgb(0, 120, 215);
                var rc = new RectangleF(0.5f, 0.5f, btn.Width - 1, btn.Height - 1);
                using (var br = new SolidBrush(fill))
                    g.FillEllipse(br, rc);
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                using (var fr = new Font("Segoe UI", 8.5f, FontStyle.Bold | FontStyle.Italic))
                using (var tb = new SolidBrush(Color.White))
                    g.DrawString("i", fr, tb, new RectangleF(0, 0, btn.Width, btn.Height), sf);
            };
            btn.MouseEnter += (s, e) => { infoHover = true;  btn.Invalidate(); };
            btn.MouseLeave += (s, e) => { infoHover = false; btn.Invalidate(); };
            btn.MouseDown  += (s, e) => { infoDown  = true;  btn.Invalidate(); };
            btn.MouseUp    += (s, e) => { infoDown  = false; btn.Invalidate(); };
            return btn;
        }

        private static string[] GetCuratedOledFonts()
        {
            var candidates = new[]
            {
                "Consolas",
                "Courier New",
                "Lucida Console",
                "Cascadia Mono",
                "Cascadia Code",
                "Lucida Sans Typewriter",
                "OCR A Extended",
                "Terminal",
                "Fixedsys",
                "Arial",
                "Segoe UI",
                "Tahoma",
                "Verdana",
                "Trebuchet MS",
                "Calibri",
                "Candara",
                "Bahnschrift",
                "Georgia",
                "Times New Roman",
                "Franklin Gothic Medium"
            };
            var installed  = new System.Drawing.Text.InstalledFontCollection()
                .Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return candidates.Where(f => installed.Contains(f)).ToArray();
        }

        private void ShowCpuAvBlockedHelp()
        {
            if (_cpuAvInfoForm != null && !_cpuAvInfoForm.IsDisposed)
            {
                _cpuAvInfoForm.BringToFront();
                _cpuAvInfoForm.Activate();
                return;
            }

            const int W = 510;
            const int H = 390;
            const int pad = 16;

            var frm = new Form
            {
                Text            = "CPU Sensor Access",
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

            var header = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 72,
                BackColor = Color.FromArgb(28, 28, 38)
            };
            header.Controls.Add(new Label
            {
                Text      = "CPU Temperature Access Blocked",
                Location  = new Point(pad, 14),
                Size      = new Size(W - pad * 2, 24),
                ForeColor = Color.White,
                Font      = new Font("Segoe UI", 12f, FontStyle.Bold),
                AutoSize  = false
            });
            header.Controls.Add(new Label
            {
                Text      = "The app is running, but the CPU temperature sensor is returning 0.0 or N/A.",
                Location  = new Point(pad, 42),
                Size      = new Size(W - pad * 2, 18),
                ForeColor = Color.FromArgb(190, 205, 230),
                Font      = new Font("Segoe UI", 8.5f),
                AutoSize  = false
            });

            var footer = new Panel
            {
                Dock      = DockStyle.Bottom,
                Height    = 52,
                BackColor = Color.FromArgb(232, 234, 240)
            };
            var btnWindowsSecurity = new Button
            {
                Text      = "Open Windows Security",
                Size      = new Size(160, 30),
                Location  = new Point(W - 160 - pad - 88 - 8, 11),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                Font      = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            btnWindowsSecurity.FlatAppearance.BorderSize = 0;
            btnWindowsSecurity.Click += (s, e) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName        = "ms-settings:windowsdefender",
                        UseShellExecute = true
                    });
                }
                catch { }
            };
            var btnClose = new Button
            {
                Text      = "Close",
                Size      = new Size(88, 30),
                Location  = new Point(W - 88 - pad, 11),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(245, 245, 248)
            };
            btnClose.FlatAppearance.BorderColor = Color.FromArgb(185, 185, 198);
            btnClose.Click += (s, e) => frm.Close();
            footer.Controls.Add(new Panel { Location = new Point(0, 0), Size = new Size(W, 1), BackColor = Color.FromArgb(200, 202, 210) });
            footer.Controls.Add(btnWindowsSecurity);
            footer.Controls.Add(btnClose);

            var content = new Panel
            {
                Dock       = DockStyle.Fill,
                BackColor  = Color.FromArgb(245, 245, 248),
                AutoScroll = true
            };

            int y = 14;
            void AddCard(string title, string body, Color accent)
            {
                int cardW = W - pad * 2;
                var bodySize = TextRenderer.MeasureText(body, frm.Font, new Size(cardW - 34, 2000), TextFormatFlags.WordBreak);
                var card = new Panel
                {
                    Location  = new Point(pad, y),
                    Size      = new Size(cardW, bodySize.Height + 44),
                    BackColor = Color.White
                };
                card.Controls.Add(new Panel
                {
                    Location  = new Point(0, 0),
                    Size      = new Size(4, card.Height),
                    BackColor = accent
                });
                card.Controls.Add(new Label
                {
                    Text      = title,
                    Location  = new Point(14, 9),
                    Size      = new Size(cardW - 24, 18),
                    ForeColor = Color.FromArgb(40, 42, 55),
                    Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
                    AutoSize  = false
                });
                card.Controls.Add(new Label
                {
                    Text      = body,
                    Location  = new Point(14, 29),
                    Size      = new Size(cardW - 28, bodySize.Height + 6),
                    ForeColor = Color.FromArgb(65, 68, 80),
                    AutoSize  = false
                });
                content.Controls.Add(card);
                y += card.Height + 10;
            }

            AddCard(
                "Why this happens",
                "CPU temperature sensors require low-level hardware access. Some antivirus products can false-flag that access and block GGSystemMonitor.exe from reading CPU sensor data. Windows Defender is the most common source of this false positive; premium antivirus products such as Norton usually do not trigger it.",
                Color.FromArgb(205, 35, 35));
            AddCard(
                "How to fix it",
                "If Windows Defender is blocking the app, add an exclusion for GGSystemMonitor.exe in Windows Security. After the exclusion is added, restart the monitor so the sensor library can access CPU temperatures again.",
                Color.FromArgb(0, 120, 215));
            AddCard(
                "What still works",
                "If you do not add the exclusion, GGSystemMonitor will still run normally. Only CPU temperature widgets will show 0.0 or N/A; other widgets and settings are unaffected.",
                Color.FromArgb(0, 140, 70));
            content.AutoScrollMinSize = new Size(0, y + 8);

            frm.Controls.Add(content);
            frm.Controls.Add(footer);
            frm.Controls.Add(header);
            frm.FormClosed += (s, e) => _cpuAvInfoForm = null;

            _cpuAvInfoForm = frm;
            frm.Show(this);
            frm.BringToFront();
            frm.Activate();
        }

        private void ShowFontSampleHelp()
        {
            if (_fontInfoForm != null && !_fontInfoForm.IsDisposed)
            {
                _fontInfoForm.BringToFront();
                _fontInfoForm.Activate();
                return;
            }

            const int W = 520;
            const int H = 460;
            const int padX = 16;
            var sampleFonts = new List<Font>();

            var frm = new Form
            {
                Text            = "OLED Font Samples",
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

            var header = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 50,
                BackColor = Color.FromArgb(28, 28, 38)
            };
            header.Controls.Add(new Label
            {
                Text      = "OLED Font Samples",
                Location  = new Point(padX, 15),
                Size      = new Size(W - padX * 2, 22),
                ForeColor = Color.White,
                Font      = new Font("Segoe UI", 11f, FontStyle.Bold),
                AutoSize  = false
            });

            var footer = new Panel
            {
                Dock      = DockStyle.Bottom,
                Height    = 46,
                BackColor = Color.FromArgb(232, 234, 240)
            };
            var btnOk = new Button
            {
                Text      = "OK",
                Size      = new Size(80, 28),
                Location  = new Point(W - 80 - padX, 9),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                Font      = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            btnOk.FlatAppearance.BorderSize = 0;
            btnOk.Click += (s, e) => frm.Close();
            footer.Controls.Add(new Panel { Location = new Point(0, 0), Size = new Size(W, 1), BackColor = Color.FromArgb(200, 202, 210) });
            footer.Controls.Add(btnOk);

            var content = new Panel
            {
                Dock       = DockStyle.Fill,
                BackColor  = Color.FromArgb(245, 245, 248),
                AutoScroll = true
            };

            int y = 12;
            int cW = W - padX * 2 - 18;
            string note = "The default option uses the keyboard's native OLED font. Custom fonts are rendered as bitmap images. Some characters and symbols may visually change, use fallback glyphs, or appear simpler depending on the selected font.";
            var noteSize = TextRenderer.MeasureText(note, frm.Font, new Size(cW, 2000), TextFormatFlags.WordBreak);
            content.Controls.Add(new Label
            {
                Text      = note,
                Location  = new Point(padX, y),
                Size      = new Size(cW, noteSize.Height + 4),
                ForeColor = Color.FromArgb(70, 70, 85),
                AutoSize  = false
            });
            y += noteSize.Height + 14;

            void AddFontRow(string name, Font sampleFont, bool isDefault)
            {
                var row = new Panel
                {
                    Location  = new Point(padX, y),
                    Size      = new Size(cW, 44),
                    BackColor = Color.FromArgb(232, 234, 240)
                };
                row.Controls.Add(new Label
                {
                    Text      = name,
                    Location  = new Point(8, 5),
                    Size      = new Size(145, 16),
                    ForeColor = Color.FromArgb(45, 48, 60),
                    Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                    AutoSize  = false
                });
                row.Controls.Add(new Label
                {
                    Text      = isDefault ? "Native keyboard font" : "CPU: 72.4°C  12345  🡅 ⚠ ♪",
                    Location  = new Point(160, 5),
                    Size      = new Size(cW - 168, 30),
                    ForeColor = Color.FromArgb(20, 20, 25),
                    Font      = sampleFont,
                    AutoSize  = false
                });
                content.Controls.Add(row);
                y += row.Height + 6;
            }

            var defaultSampleFont = new Font("Segoe UI", 9f);
            sampleFonts.Add(defaultSampleFont);
            AddFontRow("(Default)", defaultSampleFont, true);
            foreach (string fontName in GetCuratedOledFonts())
            {
                try
                {
                    var sampleFont = new Font(fontName, 14, GraphicsUnit.Pixel);
                    sampleFonts.Add(sampleFont);
                    AddFontRow(fontName, sampleFont, false);
                }
                catch { }
            }

            content.AutoScrollMinSize = new Size(0, y + 8);

            frm.Controls.Add(content);
            frm.Controls.Add(footer);
            frm.Controls.Add(header);
            frm.FormClosed += (s, e) =>
            {
                foreach (var f in sampleFonts) f.Dispose();
                _fontInfoForm = null;
            };

            _fontInfoForm = frm;
            frm.Show(this);
            frm.BringToFront();
            frm.Activate();
        }

        private void ShowItemPlaceholderHelp(string type)
        {
            if (_itemPlaceholderForm != null && !_itemPlaceholderForm.IsDisposed)
            {
                _itemPlaceholderForm.BringToFront();
                _itemPlaceholderForm.Activate();
                return;
            }

            string norm = type?.ToLower().Replace(" ", "").Replace("_", "") ?? "";

            const int W    = 370;
            const int padX = 14;
            int y  = 10;
            int cW = W - padX * 2 - 2;

            var bodyFont = new Font("Segoe UI", 8.5f);
            var monoFont = new Font("Consolas", 8.5f);

            var content = new Panel { BackColor = Color.FromArgb(245, 245, 248), AutoScroll = true };

            void Row(string code, string desc)
            {
                var row = new Panel { Location = new Point(padX, y), Size = new Size(cW, 22), BackColor = Color.FromArgb(232, 234, 240) };
                row.Controls.Add(new Label { Text = code, Location = new Point(6,   3), Size = new Size(138, 16), ForeColor = Color.FromArgb(180, 75, 0),  Font = monoFont, AutoSize = false });
                row.Controls.Add(new Label { Text = desc, Location = new Point(148, 3), Size = new Size(cW - 154, 16), ForeColor = Color.FromArgb(55, 60, 75), Font = bodyFont, AutoSize = false });
                content.Controls.Add(row);
                y += 26;
            }

            void Note(string text)
            {
                var sz = TextRenderer.MeasureText(text, new Font("Segoe UI", 8f), new Size(cW, 1000), TextFormatFlags.WordBreak);
                content.Controls.Add(new Label { Text = text, Location = new Point(padX, y), Size = new Size(cW, sz.Height + 2), ForeColor = Color.FromArgb(100, 100, 115), Font = new Font("Segoe UI", 8f), AutoSize = false });
                y += sz.Height + 8;
            }

            string dialogTitle;
            switch (norm)
            {
                case "cputemperature":
                case "gputemperature":
                    dialogTitle = norm == "cputemperature" ? "CPU Temperature Placeholders" : "GPU Temperature Placeholders";
                    Row("{temp:FORMAT}", "Temperature reading");
                    y += 6;
                    Note("FORMAT codes: F0 = whole number (72),  F1 = one decimal (72.4),  F2 = two decimals");
                    break;
                case "cpuusage":
                case "gpuusage":
                    dialogTitle = norm == "cpuusage" ? "CPU Usage Placeholders" : "GPU Usage Placeholders";
                    Row("{pct:FORMAT}", "Usage percentage");
                    y += 6;
                    Note("FORMAT codes: F0 = whole number (87),  F1 = one decimal (87.3)");
                    break;
                case "ramusage":
                    dialogTitle = "RAM Usage Placeholders";
                    Row("{used:FORMAT}",  "RAM currently in use");
                    Row("{total:FORMAT}", "Total installed RAM");
                    y += 6;
                    Note("FORMAT codes: 0.# = compact (7.5 or 8),  F1 = always one decimal.  MB units apply when Display in MB is checked.");
                    break;
                case "time":
                    dialogTitle = "Time Placeholders";
                    Row("{time:FORMAT}", "Current time");
                    y += 6;
                    Note("FORMAT codes:  HH = 24-hour  hh = 12-hour  mm = minutes  ss = seconds  tt = AM/PM\nExamples: HH:mm:ss → 14:23:07    hh:mm:ss tt → 02:23:07 PM");
                    break;
                case "date":
                    dialogTitle = "Date Placeholders";
                    Row("{date:FORMAT}", "Current date");
                    y += 6;
                    Note("FORMAT codes:  dd = day  MM = month number  MMM = month name  ddd = day name  yyyy = year\nExample: MMM dd, yyyy → May 17, 2026");
                    break;
                case "weather":
                    dialogTitle = "Weather Placeholders";
                    Row("{wicon}",            "Animated icon  (☀ ☁ ☂ ❄ ⚡ ≡)");
                    Row("{temp:FORMAT}",      "Temperature (°C or °F per widget)");
                    Row("{feelslike:FORMAT}", "Feels-like temperature");
                    Row("{condition}",        "Weather description text");
                    Row("{humidity:FORMAT}",  "Humidity percentage");
                    Row("{wind:FORMAT}",      "Wind speed (km/h or mph per widget)");
                    Row("{city}",             "Detected city name");
                    y += 6;
                    Note("FORMAT codes: F0 = whole number (72),  F1 = one decimal (72.4)\nSet location in the widget settings. Refreshes every 15 minutes.");
                    break;
                case "nowplaying":
                    dialogTitle = "Now Playing Placeholders";
                    Row("{source}",          "Source icon: ♫ Spotify  ▶ YouTube  ♪/♫ generic  ⏸ paused");
                    Row("{title}",           "Track title");
                    Row("{artist}",          "Artist name");
                    Row("{album}",           "Album name");
                    Row("{elapsed:FORMAT}",  "Current position in track");
                    Row("{duration:FORMAT}", "Total track length");
                    y += 6;
                    Note("FORMAT codes: HH = hours  mm = minutes  ss = seconds  (use any separator)\nExamples: mm:ss → 03:45   HH:mm:ss → 00:03:45\nReads from Windows SMTC. Works with Spotify, browsers, and most media apps.");
                    break;
                default:
                    return;
            }

            int contentH = y + 10;
            int formH    = Math.Max(190, Math.Min(480, contentH + 50 + 46));
            content.Location          = new Point(0, 50);
            content.Size              = new Size(W, formH - 50 - 46);
            content.AutoScrollMinSize = new Size(0, contentH);

            var frm = new Form
            {
                Text            = dialogTitle,
                ClientSize      = new Size(W, formH),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition   = FormStartPosition.CenterParent,
                MaximizeBox     = false,
                MinimizeBox     = false,
                ShowInTaskbar   = false,
                BackColor       = Color.FromArgb(245, 245, 248),
                Font            = new Font("Segoe UI", 9f),
                Icon            = Icon.ExtractAssociatedIcon(Application.ExecutablePath)
            };

            var hdr = new Panel { Location = new Point(0, 0), Size = new Size(W, 50), BackColor = Color.FromArgb(24, 24, 28) };
            hdr.Controls.Add(new Label { Text = dialogTitle, Location = new Point(padX, 14), Size = new Size(W - padX * 2, 22), ForeColor = Color.White, Font = new Font("Segoe UI", 10f, FontStyle.Bold), AutoSize = false });
            frm.Controls.Add(hdr);
            frm.Controls.Add(content);

            var ftr = new Panel { Location = new Point(0, formH - 46), Size = new Size(W, 46), BackColor = Color.FromArgb(232, 234, 240) };
            ftr.Controls.Add(new Panel { Location = new Point(0, 0), Size = new Size(W, 1), BackColor = Color.FromArgb(200, 202, 210) });
            var btnOk = new Button { Text = "OK", Size = new Size(80, 28), Location = new Point(W - 80 - padX, 9), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 120, 215), ForeColor = Color.White, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
            btnOk.FlatAppearance.BorderSize = 0;
            btnOk.Click += (s, e) => frm.Close();
            ftr.Controls.Add(btnOk);
            frm.Controls.Add(ftr);

            frm.FormClosed += (s, e) => { _itemPlaceholderForm = null; };
            _itemPlaceholderForm = frm;
            frm.Show(this);
            frm.BringToFront();
            frm.Activate();
        }

        private void ShowPlaceholderHelp()
        {
            if (_placeholderInfoForm != null && !_placeholderInfoForm.IsDisposed)
            {
                _placeholderInfoForm.BringToFront();
                _placeholderInfoForm.Activate();
                return;
            }

            const int W    = 490;
            const int H    = 390;
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
                    string typeNorm = type?.ToLower().Replace(" ", "").Replace("_", "") ?? "";
                    if (typeNorm == "weather" || typeNorm == "nowplaying")
                        item.DurationMs = 5000;
                    list.Items.Add(item);
                    list.SelectedIndex = list.Items.Count - 1;
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

            // Load sensor lists from hardware.json
            try
            {
                string hwPath = Path.Combine(AppContext.BaseDirectory, "hardware.json");
                if (File.Exists(hwPath))
                {
                    var hw = JObject.Parse(File.ReadAllText(hwPath));
                    _cpuTempSensorNames = hw["CpuTempSensors"]?.ToObject<string[]>() ?? Array.Empty<string>();
                    _gpuTempSensorNames = hw["GpuTempSensors"]?.ToObject<string[]>() ?? Array.Empty<string>();
                }
            }
            catch { }

            // Display
            PopulateList(_topList,    s.TopLineItems    ?? new List<LineItem> { new LineItem { Type = "CpuTemperature" } });
            PopulateList(_bottomList, s.BottomLineItems ?? new List<LineItem> { new LineItem { Type = "GpuTemperature" } });
            _rotationNud.Value = Clamp(_rotationNud, s.RotationIntervalMs);

            // General
            _capsLockCheck.Checked   = s.ShowCapsLockIndicator;
            _showUpdateCheck.Checked = s.ShowUpdateNotifications;
            _ggPathBox.Text          = s.GGEngineCorePropsPath
                ?? @"C:/ProgramData/SteelSeries/SteelSeries Engine 3/coreProps.json";
            string savedFont = s.OledFont ?? "";
            if (string.IsNullOrEmpty(savedFont))
            {
                _oledFontCombo.SelectedIndex = 0;
                _fontNotInstalledLabel.Visible = false;
            }
            else
            {
                int idx = _oledFontCombo.Items.IndexOf(savedFont);
                if (idx >= 0)
                {
                    _oledFontCombo.SelectedIndex = idx;
                    _fontNotInstalledLabel.Visible = false;
                }
                else
                {
                    _oledFontCombo.SelectedIndex = 0;
                    _fontNotInstalledLabel.Text    = $"'{savedFont}' is not installed - install it to use this font";
                    _fontNotInstalledLabel.Visible = true;
                }
            }

            _loading = false;
            _baseline = CaptureBaseline();
            ResetDirty();
            if (IsHandleCreated)
                BeginInvoke(new Action(() => _displayTabReflow?.Invoke()));
            else
                _displayTabReflow?.Invoke();
        }

        private void WireChangeHandlers()
        {
            _rotationNud.ValueChanged         += (s, e) => EvaluateDirty();
            _capsLockCheck.CheckedChanged     += (s, e) => EvaluateDirty();
            _showUpdateCheck.CheckedChanged   += (s, e) => EvaluateDirty();
            _ggPathBox.TextChanged            += (s, e) => EvaluateDirty();
            _oledFontCombo.SelectedIndexChanged += (s, e) => EvaluateDirty();
            // Commit typed value immediately so HasChanges() reads the updated Value
            foreach (var nud in new[] { _rotationNud, _cpuWarnNud, _cpuCritNud, _gpuWarnNud, _gpuCritNud, _tempUpdateNud, _gpuTempUpdateNud, _gpuPasteGapNud })
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

        private static bool ParseIsAuto(string setting) =>
            string.IsNullOrEmpty(setting) || setting.Equals("AUTO", StringComparison.OrdinalIgnoreCase);

        private static float ParseTempValue(string setting, float fallback)
        {
            if (!string.IsNullOrEmpty(setting) && !setting.Equals("AUTO", StringComparison.OrdinalIgnoreCase))
            {
                if (float.TryParse(setting, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out float v)) return v;
            }
            return fallback;
        }

        private void ApplyThresholdGroup(
            string warnSetting, string critSetting,
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
                    new JProperty("Type",                   item.Type),
                    new JProperty("Label",                  item.Label),
                    new JProperty("DurationMs",             item.DurationMs),
                    new JProperty("UseFahrenheit",          item.UseFahrenheit),
                    new JProperty("UseRamMb",               item.UseRamMb),
                    new JProperty("NotPlayingText",         item.NotPlayingText),
                    new JProperty("SkipIfNotPlaying",       item.SkipIfNotPlaying),
                    new JProperty("NowPlayingSpotify",      item.NowPlayingSpotify),
                    new JProperty("NowPlayingYouTube",      item.NowPlayingYouTube),
                    new JProperty("NowPlayingOtherPlayer",  item.NowPlayingOtherPlayer),
                    new JProperty("PollIntervalMs",         item.PollIntervalMs),
                    new JProperty("EnableWarnIndicators",   item.EnableWarnIndicators),
                    new JProperty("WarnTemp",               item.WarnTemp),
                    new JProperty("CritTemp",               item.CritTemp),
                    new JProperty("SensorOverride",         item.SensorOverride),
                    new JProperty("GpuPasteMonitoring",     item.GpuPasteMonitoring),
                    new JProperty("GpuPasteGapTemp",        item.GpuPasteGapTemp),
                    new JProperty("WeatherLocation",        item.WeatherLocation)
                ))
            );

            jo["GGEngineCorePropsPath"]   = _ggPathBox.Text;
            jo["ShowCapsLockIndicator"]   = _capsLockCheck.Checked;
            jo["ShowUpdateNotifications"] = _showUpdateCheck.Checked;
            jo.Remove("DebugCpuTemperatureOverride");
            jo.Remove("_note_DebugCpuTemperatureOverride");
            jo["TopLineItems"]            = MapList(_topList);
            jo["BottomLineItems"]         = MapList(_bottomList);
            jo["RotationIntervalMs"]      = (int)_rotationNud.Value;
            string chosenFont = _oledFontCombo.SelectedIndex == 0 ? "" : _oledFontCombo.SelectedItem?.ToString() ?? "";
            if (!string.IsNullOrEmpty(chosenFont))
            {
                var installed = new System.Drawing.Text.InstalledFontCollection()
                    .Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!installed.Contains(chosenFont))
                {
                    _oledFontCombo.SelectedIndex = 0;
                    _fontNotInstalledLabel.Text    = $"'{chosenFont}' is not installed - install it to use this font";
                    _fontNotInstalledLabel.Visible = true;
                    chosenFont = "";
                }
            }
            jo["OledFont"]                = chosenFont;

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
            _rotationNud.Value           = Clamp(_rotationNud, d.RotationIntervalMs);
            _capsLockCheck.Checked       = d.ShowCapsLockIndicator;
            _showUpdateCheck.Checked     = d.ShowUpdateNotifications;
            _ggPathBox.Text              = d.GGEngineCorePropsPath;
            _oledFontCombo.SelectedIndex = 0;
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
            string[] cpuSensors = Array.Empty<string>(), gpuSensors = Array.Empty<string>();
            bool hardwareLoaded = false;
            try
            {
                string hwPath = Path.Combine(AppContext.BaseDirectory, "hardware.json");
                if (File.Exists(hwPath))
                {
                    var hw = JObject.Parse(File.ReadAllText(hwPath));
                    cpuName    = hw["CpuName"]?.ToString() ?? "";
                    gpuName    = hw["GpuName"]?.ToString() ?? "";
                    cpuSensors = hw["CpuTempSensors"]?.ToObject<string[]>() ?? Array.Empty<string>();
                    gpuSensors = hw["GpuTempSensors"]?.ToObject<string[]>() ?? Array.Empty<string>();
                    hardwareLoaded = true;
                }
            }
            catch { }

            RefreshCpuTemperatureHealthDisplay();

            if (!hardwareLoaded || (cpuName == _detectedCpuName && gpuName == _detectedGpuName)) return;

            _cpuTempSensorNames = cpuSensors;
            _gpuTempSensorNames = gpuSensors;

            _detectedCpuName  = cpuName;
            _detectedGpuName  = gpuName;
            _cpuAutoAvailable = IsCpuNameInDictionary(_detectedCpuName);
            _gpuAutoAvailable = IsGpuNameInDictionary(_detectedGpuName);

            ApplyHardwareStatusToGroup(_cpuHwStatusLabel, _detectedCpuName, _cpuAutoAvailable, GetCpuMaximum(_detectedCpuName));
            ApplyHardwareStatusToGroup(_gpuHwStatusLabel, _detectedGpuName, _gpuAutoAvailable, GetGpuMaximum(_detectedGpuName));

            _cpuDefaultWarn = GetCpuAutoWarning(_detectedCpuName);
            _cpuDefaultCrit = GetCpuAutoCritical(_detectedCpuName);
            _gpuDefaultWarn = GetGpuAutoWarning(_detectedGpuName);
            _gpuDefaultCrit = GetGpuAutoCritical(_detectedGpuName);
            // Re-populate ext panel if a temp widget is currently selected (auto limits may have changed)
            var cpuSel = GetSelectedCpuItem();
            if (cpuSel != null) PopulateCpuExtPanel(cpuSel);
            var gpuSel = GetSelectedGpuItem();
            if (gpuSel != null) PopulateGpuExtPanel(gpuSel);
            _baseline = CaptureBaseline();
            ResetDirty();
        }

        private void RefreshCpuTemperatureHealthDisplay()
        {
            bool blocked;
            if (TryReadCpuTemperatureBlockedByAv(out blocked))
            {
                ApplyCpuTemperatureBlockedState(blocked);
                return;
            }

            ApplyCpuTemperatureBlockedState(false);
        }

        private bool TryReadCpuTemperatureBlockedByAv(out bool blocked)
        {
            blocked = false;
            try
            {
                string statusPath = Path.Combine(AppContext.BaseDirectory, "monitor_status.json");
                if (!File.Exists(statusPath)) return false;

                var status = JObject.Parse(File.ReadAllText(statusPath));
                string updatedRaw = status["UpdatedUtc"]?.ToString();
                if (DateTime.TryParse(updatedRaw, null,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out DateTime updatedUtc) &&
                    DateTime.UtcNow - updatedUtc.ToUniversalTime() > TimeSpan.FromSeconds(30))
                {
                    return false;
                }

                JToken explicitBlocked = status["CpuTemperatureBlockedByAv"];
                if (explicitBlocked != null && explicitBlocked.Type == JTokenType.Boolean)
                {
                    blocked = explicitBlocked.Value<bool>();
                    return true;
                }

                JToken temp = status["CpuTemperature"];
                if (temp == null || temp.Type == JTokenType.Null)
                {
                    blocked = true;
                    return true;
                }

                if (double.TryParse(temp.ToString(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double value))
                {
                    blocked = value <= 0.0;
                    return true;
                }
            }
            catch { }

            return false;
        }

        private void ApplyCpuTemperatureBlockedState(bool blocked)
        {
            if (_cpuTemperatureBlockedByAv == blocked) return;

            _cpuTemperatureBlockedByAv = blocked;
            _topList?.Invalidate();
            _bottomList?.Invalidate();
            ApplyCpuAvBlockedVisual();

            if (GetSelectedCpuItem() != null)
            {
                foreach (var cb in _lineEditorUpdateHeightCallbacks) cb?.Invoke();
            }
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
                int currentPid;
                using (var current = Process.GetCurrentProcess())
                    currentPid = current.Id;
                var processes = Process.GetProcessesByName("GGSystemMonitor");
                foreach (var p in processes)
                {
                    try
                    {
                        if (p.Id != currentPid) p.Kill();
                    }
                    catch { }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
            else
            {
                try
                {
                    string exePath;
                    using (var current = Process.GetCurrentProcess())
                        exePath = current.MainModule.FileName;
                    Process.Start(new ProcessStartInfo
                    {
                        FileName        = exePath,
                        UseShellExecute = true
                    });
                }
                catch { }
            }
            System.Threading.Thread.Sleep(600);
            UpdateStatus();
        }

        private bool IsMonitorRunning()
        {
            int currentPid;
            using (var current = Process.GetCurrentProcess())
                currentPid = current.Id;
            var processes = Process.GetProcessesByName("GGSystemMonitor");
            try
            {
                foreach (var p in processes)
                {
                    try
                    {
                        if (p.Id != currentPid) return true;
                    }
                    catch { }
                }
                return false;
            }
            finally
            {
                foreach (var p in processes)
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }

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
                        int currentPid;
                        using (var current = Process.GetCurrentProcess())
                            currentPid = current.Id;
                        Process.Start(new ProcessStartInfo
                        {
                            FileName        = "powershell.exe",
                            Arguments       = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden" +
                                              $" -File \"{installPs1}\" -Uninstall -DoneFile \"{doneFile}\"" +
                                              $" -ExcludePid {currentPid}",
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
            _weatherValidationTimer?.Stop();
            _weatherValidationTimer?.Dispose();
            base.OnFormClosed(e);
        }

        private sealed class ScrollWheelFilter : IMessageFilter
        {
            private const int WM_MOUSEWHEEL = 0x020A;
            private readonly Control    _target;
            private readonly Action<int> _onDelta;
            public ScrollWheelFilter(Control target, Action<int> onDelta) { _target = target; _onDelta = onDelta; }
            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg != WM_MOUSEWHEEL) return false;
                var pt = _target.PointToClient(Cursor.Position);
                if (!_target.ClientRectangle.Contains(pt)) return false;
                int delta = unchecked((short)((long)m.WParam >> 16));
                _onDelta(delta);
                return true;
            }
        }
    }
}
