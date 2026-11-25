using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using Json = System.Text.Json.JsonSerializer;
using static GGSystemMonitor.Program;

namespace GGSystemMonitor
{
    internal partial class Program
    {
        public sealed class DisplayLayout
        {
            [System.Text.Json.Serialization.JsonConverter(typeof(LineItemListConverter))]
            public List<LineItem> TopLineItems { get; set; } = new List<LineItem>();
            [System.Text.Json.Serialization.JsonConverter(typeof(LineItemListConverter))]
            public List<LineItem> BottomLineItems { get; set; } = new List<LineItem>();
            public int RotationIntervalMs { get; set; } = 3000;
            public bool RotateLinesTogether { get; set; } = true;

            internal static DisplayLayout From(AppSettings settings)
            {
                var defaults = new AppSettings();
                return new DisplayLayout
                {
                    TopLineItems = settings.TopLineItems ?? defaults.TopLineItems,
                    BottomLineItems = settings.BottomLineItems ?? defaults.BottomLineItems,
                    RotationIntervalMs = settings.RotationIntervalMs > 0 ? settings.RotationIntervalMs : defaults.RotationIntervalMs,
                    RotateLinesTogether = settings.RotateLinesTogether
                };
            }

            internal static DisplayLayout Factory() => From(new AppSettings());
            internal string Signature() => Json.Serialize(this);
            internal DisplayLayout Copy() => Json.Deserialize<DisplayLayout>(Signature());

            internal void Project(AppSettings settings)
            {
                settings.TopLineItems = TopLineItems ?? new List<LineItem>();
                settings.BottomLineItems = BottomLineItems ?? new List<LineItem>();
                settings.RotationIntervalMs = RotationIntervalMs > 0 ? RotationIntervalMs : 3000;
                settings.RotateLinesTogether = RotateLinesTogether;
            }

            internal void WriteProjection(JObject settings)
            {
                var fields = JObject.Parse(Signature());
                foreach (var field in fields.Properties()) settings[field.Name] = field.Value.DeepClone();
            }
        }

        public sealed class DisplayPreset
        {
            internal const string DefaultId = "default";
            internal const string DefaultName = "(default)";
            public string Id { get; set; }
            public string Name { get; set; }
            public DisplayLayout Layout { get; set; }
            public override string ToString() => Name;

            internal static void UpgradeLegacy(AppSettings settings, JObject document)
            {
                var layout = DisplayLayout.From(settings);
                var legacy = new DisplayPreset { Id = "legacy-custom-1", Name = "Custom 1", Layout = layout };
                settings.ActiveDisplayPresetId = legacy.Id;
                settings.DisplayPresets = new List<DisplayPreset> { legacy };
                document["ActiveDisplayPresetId"] = legacy.Id;
                // Copy the original arrays on upgrade so legacy string widgets and any
                // unrecognized fields survive the initial migration.
                var data = JObject.Parse(Json.Serialize(legacy));
                var savedLayout = (JObject)data["Layout"];
                foreach (var name in new[] { "TopLineItems", "BottomLineItems" })
                    if (document[name] is JArray original) savedLayout[name] = original.DeepClone();
                document["DisplayPresets"] = new JArray(data);
            }

            internal static DisplayLayout Resolve(AppSettings settings)
            {
                if (settings.ActiveDisplayPresetId == DefaultId) return DisplayLayout.Factory();
                return settings.DisplayPresets?.FirstOrDefault(p => p != null && p.Id == settings.ActiveDisplayPresetId)?.Layout
                    ?? DisplayLayout.From(settings);
            }
        }
    }

    internal partial class SettingsForm
    {
        private ComboBox _displayPresetCombo;
        private Button _presetGear;
        private ToolStripMenuItem _renamePresetItem, _deletePresetItem, _clonePresetItem;
        private List<DisplayPreset> _displayPresets = new List<DisplayPreset>();
        private string _activeDisplayPreset = DisplayPreset.DefaultId;
        private bool _loadingPreset;
        private static readonly string FactoryDisplaySignature = DisplayLayout.Factory().Signature();

        private Panel BuildPresetRow()
        {
            var row = NewWidgetSection("DisplayPresetBar");
            row.Visible = true;
            row.SetBounds(8, 40, 469, 41);
            var caption = new Label { Text = "Preset", AutoSize = true, Location = new Point(10, 13), ForeColor = WidgetSectionInk };
            _displayPresetCombo = new ComboBox
            {
                Name = "DisplayPresets", AccessibleName = "Display preset", DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(66, 9), Width = 245
            };
            var create = new Button
            {
                Name = "CreateDisplayPreset", Text = "Create…", Location = new Point(320, 8), Size = new Size(96, 25),
                FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 100, 185), ForeColor = Color.White
            };
            create.FlatAppearance.BorderColor = Color.FromArgb(0, 90, 165);
            _presetGear = new Button
            {
                Name = "DisplayPresetActions", AccessibleName = "Preset actions", Location = new Point(426, 8), Size = new Size(31, 25),
                FlatStyle = FlatStyle.Flat, ForeColor = Color.FromArgb(32, 75, 126), BackColor = Color.FromArgb(211, 229, 250),
                UseVisualStyleBackColor = false, Cursor = Cursors.Hand
            };
            _presetGear.FlatAppearance.BorderColor = Color.FromArgb(174, 201, 234);
            _presetGear.FlatAppearance.MouseOverBackColor = Color.FromArgb(195, 219, 248);
            _presetGear.FlatAppearance.MouseDownBackColor = Color.FromArgb(181, 209, 243);
            _presetGear.Paint += (s, e) =>
            {
                float x = _presetGear.Width / 2f, y = _presetGear.Height / 2f;
                float radius = Math.Max(5f, _presetGear.Height * .23f);
                var points = new PointF[32];
                for (int i = 0; i < points.Length; i++)
                {
                    double angle = i * Math.PI / 16;
                    float r = radius * (i % 4 == 1 || i % 4 == 2 ? 1.40f : 1f);
                    points[i] = new PointF(x + r * (float)Math.Cos(angle), y + r * (float)Math.Sin(angle));
                }
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var pen = new Pen(_presetGear.ForeColor, 1.25f))
                {
                    e.Graphics.DrawPolygon(pen, points);
                    float hole = radius * .38f;
                    e.Graphics.DrawEllipse(pen, x - hole, y - hole, hole * 2, hole * 2);
                }
                if (_presetGear.Focused)
                    ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(_presetGear.ClientRectangle, -3, -3));
            };
            var menu = new ContextMenuStrip { ShowImageMargin = false };
            _renamePresetItem = new ToolStripMenuItem("Rename…");
            _deletePresetItem = new ToolStripMenuItem("Delete…");
            _clonePresetItem = new ToolStripMenuItem("Clone");
            menu.Items.AddRange(new ToolStripItem[] { _renamePresetItem, _deletePresetItem, _clonePresetItem });
            _presetGear.ContextMenuStrip = menu;
            _presetGear.Disposed += (s, e) => menu.Dispose();
            _presetGear.Click += (s, e) => menu.Show(_presetGear, new Point(_presetGear.Width, _presetGear.Height), ToolStripDropDownDirection.BelowLeft);
            row.Controls.AddRange(new Control[] { caption, _displayPresetCombo, create, _presetGear });
            _displayPresetCombo.SelectionChangeCommitted += (s, e) =>
            {
                if (!_loadingPreset && _displayPresetCombo.SelectedItem is DisplayPreset selected)
                    SwitchDisplayPreset(selected.Id);
            };
            create.Click += (s, e) =>
            {
                string name = AskPresetName("Create display preset", NextCustomPresetName(), null);
                if (name != null) CreateDisplayPreset(name);
            };
            _renamePresetItem.Click += (s, e) =>
            {
                var current = FindPreset(_activeDisplayPreset);
                if (current == null) return;
                string name = AskPresetName("Rename display preset", current.Name, current.Id);
                if (name != null) RenameDisplayPreset(name);
            };
            _deletePresetItem.Click += (s, e) =>
            {
                var current = FindPreset(_activeDisplayPreset);
                if (current != null && MessageBox.Show(this, "Delete the preset “" + current.Name +
                    "”? The display will switch to (default).", "Delete display preset", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                    DeleteDisplayPreset();
            };
            _clonePresetItem.Click += (s, e) => CloneDisplayPreset();
            return row;
        }

        private DisplayLayout CaptureDisplayLayout() => new DisplayLayout
        {
            TopLineItems = _topList.Items.Cast<LineItem>().ToList(),
            BottomLineItems = _bottomList.Items.Cast<LineItem>().ToList(),
            RotationIntervalMs = (int)_rotationNud.Value,
            RotateLinesTogether = _rotateTogether.Checked
        };

        private DisplayPreset FindPreset(string id) => _displayPresets.FirstOrDefault(p => p.Id == id);
        private string NextCustomPresetName()
        {
            int i = 1;
            while (_displayPresets.Any(p => string.Equals(p.Name, "Custom " + i, StringComparison.OrdinalIgnoreCase))) i++;
            return "Custom " + i;
        }

        private void RefreshPresetList()
        {
            _loadingPreset = true;
            try
            {
                _displayPresetCombo.Items.Clear();
                _displayPresetCombo.Items.Add(new DisplayPreset { Id = DisplayPreset.DefaultId, Name = DisplayPreset.DefaultName });
                _displayPresetCombo.Items.AddRange(_displayPresets.ToArray());
                int index = _displayPresets.FindIndex(p => p.Id == _activeDisplayPreset);
                _displayPresetCombo.SelectedIndex = index + 1;
                _renamePresetItem.Enabled = _deletePresetItem.Enabled = index >= 0;
                _clonePresetItem.Enabled = true;
            }
            finally { _loadingPreset = false; }
        }

        private void UseDisplayPreset(string id, DisplayLayout restore = null)
        {
            _activeDisplayPreset = id;
            var layout = restore ?? (FindPreset(id)?.Layout ?? DisplayLayout.Factory()).Copy();
            bool wasLoading = _loading;
            _loading = true;
            try
            {
                PopulateList(_topList, layout.TopLineItems ?? new List<LineItem>());
                PopulateList(_bottomList, layout.BottomLineItems ?? new List<LineItem>());
                _rotationNud.Value = Clamp(_rotationNud, layout.RotationIntervalMs);
                _rotateTogether.Checked = layout.RotateLinesTogether;
                RefreshPresetList();
            }
            finally { _loading = wasLoading; }
            _displayTabReflow?.Invoke();
        }

        private void StashDisplayPreset(bool forkDefault)
        {
            var layout = CaptureDisplayLayout();
            var current = FindPreset(_activeDisplayPreset);
            if (current != null)
            {
                if (current.Layout.Signature() != layout.Signature()) current.Layout = layout.Copy();
            }
            else if (forkDefault) TryForkFactoryPreset(layout);
        }

        private void SelectEditedFactoryDraft()
        {
            if (_activeDisplayPreset == DisplayPreset.DefaultId && TryForkFactoryPreset(CaptureDisplayLayout()))
                RefreshPresetList(); // Keep the current widget, editor, and keyboard focus intact.
        }

        private bool TryForkFactoryPreset(DisplayLayout layout)
        {
            if (_activeDisplayPreset != DisplayPreset.DefaultId || layout.Signature() == FactoryDisplaySignature)
                return false;

            SelectNewPreset(NextCustomPresetName(), layout);
            return true;
        }

        private void SelectNewPreset(string name, DisplayLayout layout)
        {
            var preset = new DisplayPreset { Id = Guid.NewGuid().ToString("N"), Name = name, Layout = layout.Copy() };
            _displayPresets.Add(preset);
            _activeDisplayPreset = preset.Id;
        }

        private void SwitchDisplayPreset(string id)
        {
            if (id == _activeDisplayPreset || (id != DisplayPreset.DefaultId && FindPreset(id) == null)) return;
            ApplyPresetChange(() =>
            {
                StashDisplayPreset(forkDefault: true);
                UseDisplayPreset(id);
            });
        }

        private void WriteDisplaySettings(JObject document)
        {
            document["SettingsVersion"] = Math.Max(AppSettings.CurrentSettingsVersion, (int?)document["SettingsVersion"] ?? 0);
            document["ActiveDisplayPresetId"] = _activeDisplayPreset;
            document["DisplayPresets"] = JToken.Parse(Json.Serialize(_displayPresets));
            CaptureDisplayLayout().WriteProjection(document);
        }

        private void ApplyPresetChange(Action change)
        {
            string previousId = _activeDisplayPreset;
            string previousLibrary = Json.Serialize(_displayPresets);
            var previousLayout = CaptureDisplayLayout().Copy();
            int topSelection = _topList.SelectedIndex, bottomSelection = _bottomList.SelectedIndex;
            try
            {
                // Apply the Display tab only. Pending General edits still belong to Save.
                var document = File.Exists(_settingsPath) ? JObject.Parse(File.ReadAllText(_settingsPath)) : new JObject();
                change();
                WriteDisplaySettings(document);
                SettingsManager.WriteFile(_settingsPath, document.ToString(Newtonsoft.Json.Formatting.Indented));
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
                error is Newtonsoft.Json.JsonException)
            {
                _displayPresets = Json.Deserialize<List<DisplayPreset>>(previousLibrary);
                UseDisplayPreset(previousId, previousLayout);
                _topList.SelectedIndex = topSelection;
                _bottomList.SelectedIndex = bottomSelection;
                EvaluateDirty();
                MessageBox.Show(this, "The display preset could not be applied. Check that settings.json is writable and try again.\n\n" +
                    error.Message, "Display presets", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var current = CaptureBaseline();
            _baseline.ActiveDisplayPreset = current.ActiveDisplayPreset;
            _baseline.DisplayPresetLibrary = current.DisplayPresetLibrary;
            _baseline.Rotation = current.Rotation;
            _baseline.RotateTogether = current.RotateTogether;
            _baseline.TopItemsJson = current.TopItemsJson;
            _baseline.BottomItemsJson = current.BottomItemsJson;
            EvaluateDirty();
        }

        private bool IsAvailablePresetName(string name, string ownId) =>
            !string.IsNullOrWhiteSpace(name) && name.Length <= 40 &&
            !string.Equals(name, DisplayPreset.DefaultName, StringComparison.OrdinalIgnoreCase) &&
            !_displayPresets.Any(p => p.Id != ownId && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        private void CreateDisplayPreset(string name)
        {
            name = name?.Trim();
            if (!IsAvailablePresetName(name, null)) return;
            AddDisplayPreset(name);
        }

        private void AddDisplayPreset(string name)
        {
            ApplyPresetChange(() =>
            {
                StashDisplayPreset(forkDefault: false);
                SelectNewPreset(name, CaptureDisplayLayout());
                RefreshPresetList();
            });
        }

        private void CloneDisplayPreset()
        {
            string source = FindPreset(_activeDisplayPreset)?.Name ?? DisplayPreset.DefaultName;
            int number = 1;
            while (_displayPresets.Any(p => string.Equals(p.Name, source + " " + number, StringComparison.OrdinalIgnoreCase))) number++;
            AddDisplayPreset(source + " " + number);
        }

        private void DeleteDisplayPreset()
        {
            var current = FindPreset(_activeDisplayPreset);
            if (current == null) return;
            ApplyPresetChange(() =>
            {
                _displayPresets.Remove(current);
                UseDisplayPreset(DisplayPreset.DefaultId);
            });
        }

        private void RenameDisplayPreset(string name)
        {
            name = name?.Trim();
            var current = FindPreset(_activeDisplayPreset);
            if (current == null || !IsAvailablePresetName(name, current.Id)) return;
            current.Name = name;
            RefreshPresetList();
            EvaluateDirty();
        }

        private string AskPresetName(string title, string initial, string ownId)
        {
            using (var dialog = new Form
            {
                Text = title, ClientSize = new Size(375, 138), ShowInTaskbar = false,
                StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false, MaximizeBox = false, BackColor = BackColor, Font = Font
            })
            {
                var label = new Label { Text = "Preset name", AutoSize = true, Location = new Point(14, 13) };
                var input = new TextBox { Text = initial, AccessibleName = "Preset name", MaxLength = Math.Max(40, initial.Length), Location = new Point(14, 34), Width = 346 };
                var help = new Label { Text = "Choose a unique name.", AutoSize = true, Location = new Point(14, 63), ForeColor = WidgetMuted };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(182, 96), Width = 84 };
                var ok = new Button { Text = ownId == null ? "Create" : "Rename", DialogResult = DialogResult.OK, Location = new Point(276, 96), Width = 84 };
                void ValidateName()
                {
                    ok.Enabled = IsAvailablePresetName(input.Text.Trim(), ownId);
                    help.ForeColor = ok.Enabled ? WidgetMuted : Color.FromArgb(153, 45, 42);
                    help.Text = ok.Enabled ? "Choose a unique name." : "Enter a name that is not already in use.";
                }
                input.TextChanged += (s, e) => ValidateName();
                dialog.Controls.AddRange(new Control[] { label, input, help, cancel, ok });
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;
                dialog.Shown += (s, e) => { input.Focus(); input.SelectAll(); };
                ValidateName();
                return dialog.ShowDialog(this) == DialogResult.OK ? input.Text.Trim() : null;
            }
        }
    }
}
