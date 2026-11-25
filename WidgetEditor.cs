using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using static GGSystemMonitor.Program;

namespace GGSystemMonitor
{
    internal partial class SettingsForm
    {
        private CheckBox _rotateTogether;
        private event Action WidgetFontDefaultChanged;
        private event Action WidgetThresholdContextChanged;
        private static readonly Color WidgetSectionFill = Color.FromArgb(250, 250, 253);
        private static readonly Color WidgetSectionInk = Color.FromArgb(43, 52, 72);
        private static readonly Color WidgetMuted = Color.FromArgb(70, 79, 99);
        private static readonly Color WidgetBorder = Color.FromArgb(211, 217, 229);

        private static Panel NewWidgetSection(string name)
        {
            var panel = new Panel { Name = name, Width = 460, BackColor = WidgetSectionFill, Visible = false };
            panel.Paint += (s, e) => ControlPaint.DrawBorder(e.Graphics, panel.ClientRectangle, WidgetBorder, ButtonBorderStyle.Solid);
            return panel;
        }

        private static Label WidgetSectionTitle(string text)
        {
            var label = new Label { Text = text, AutoSize = true, ForeColor = WidgetSectionInk, Location = new Point(12, 9) };
            var font = new Font("Segoe UI", 9f, FontStyle.Bold);
            label.Font = font;
            label.Disposed += (s, e) => font.Dispose();
            return label;
        }

        // These controls belong to one line and edit the selected item, not a
        // shared panel that moves between the top and bottom editors.
        private (Panel Priority, Panel FontRows, Action Refresh) BuildWidgetEditor(ListBox widgets, Action reflow, bool isTop,
            Action<Control> revealSection, Func<string> temperatureDevice)
        {
            var accent = Color.FromArgb(0, 81, 155);
            var panel = NewWidgetSection(isTop ? "TopWidgetPriority" : "BottomWidgetPriority");
            var fontRows = new Panel { Name = "WidgetFontRows", Width = 436, Height = 48 };
            var installedFonts = InstalledFontNames();
            var editorFonts = GetCuratedOledFonts().Where(installedFonts.Contains).ToArray();
            var font = Choice("Widget font", 49, 0, 200);
            var size = Choice("Widget font size", 279, 0, 155);
            var fontHint = Caption("Inherits General → OLED font. Native text has a fixed size.", 0, 28);
            size.Items.AddRange(new object[] { "Regular", "Small (−1 px)", "Extra small (−2 px)" });
            fontRows.Controls.AddRange(new Control[] { Caption("Font", 0, 4), font, Caption("Size", 252, 4), size, fontHint });

            var rulesButton = new Button
            {
                Name = "PriorityToggle", AccessibleName = "Priority and Visibility",
                FlatStyle = FlatStyle.Flat, ForeColor = accent, BackColor = Color.FromArgb(233, 241, 252),
                Dock = DockStyle.Top, Height = 51, Cursor = Cursors.Hand, TabIndex = 0
            };
            rulesButton.FlatAppearance.BorderColor = Color.FromArgb(177, 198, 226);
            rulesButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(221, 235, 252);
            rulesButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(211, 228, 249);
            var details = new Panel { Location = new Point(12, 51), Width = 436, Visible = false, TabIndex = 1 };
            var whenLabel = Caption("Take over", 0, 0);
            var mode = Choice("Priority condition", 112, 0, 322);
            var ramUnitsLabel = Caption("Usage unit", 0, 0);
            var ramUnits = Choice("RAM target unit", 112, 0, 180);
            ramUnits.Items.AddRange(new object[] { "Percentage", "MB", "GB" });
            var limitLabel = Caption("Target", 0, 0);
            var limit = Number("Takeover target", 0, 0);
            limit.Name = "PriorityTarget";
            var limitUnit = Caption("", 0, 0);
            var rankLabel = Caption("Priority", 0, 0);
            var rank = Caption("", 0, 0);
            var changePriority = new Button { Name = "ChangePriority", Text = "Change priority…", FlatStyle = FlatStyle.Flat, ForeColor = accent };
            changePriority.FlatAppearance.BorderColor = WidgetBorder;
            changePriority.FlatAppearance.MouseOverBackColor = Color.FromArgb(233, 241, 252);
            var lineDivider = new Panel { Height = 1, BackColor = WidgetBorder };
            var visibilityLabel = Caption("Show in rotation", 0, 0);
            var visibility = Choice("Visibility condition", 112, 0, 168);
            var minimum = Number("Visibility threshold", 300, 0);
            var minimumLabel = Caption("Minimum", 0, 0);
            var minimumUnit = Caption("", 0, 0);
            details.Controls.AddRange(new Control[] { whenLabel, mode, ramUnitsLabel, ramUnits, limitLabel, limit, limitUnit, rankLabel,
                rank, changePriority, lineDivider, visibilityLabel, visibility, minimumLabel, minimum, minimumUnit });
            panel.Controls.AddRange(new Control[] { details, rulesButton });
            bool expanded = false, loading = false;
            string summary = "";
            bool takeoverEnabled = false;
            bool rulesEnabled = false;
            var buttonFont = new Font("Segoe UI", 9f, FontStyle.Bold);
            rulesButton.Disposed += (s, e) => buttonFont.Dispose();
            rulesButton.Paint += (s, e) =>
            {
                int inset = Math.Max(10, rulesButton.Height / 4);
                const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
                int middle = rulesButton.Height / 2;
                int margin = inset + 20;
                int textWidth = Math.Max(0, rulesButton.Width - 2 * margin);
                Color headerInk = rulesEnabled ? Color.FromArgb(35, 84, 63) : accent;
                TextRenderer.DrawText(e.Graphics, "Priority and Visibility", buttonFont,
                    new Rectangle(margin, 4, textWidth, middle), headerInk, flags);
                TextRenderer.DrawText(e.Graphics, summary, rulesButton.Font,
                    new Rectangle(margin, middle, textWidth, middle - 3), WidgetMuted, flags);
                int x = rulesButton.Width - inset - 5;
                int y = middle;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var dot = new Rectangle(inset + 1, y - 5, 10, 10);
                if (rulesEnabled)
                {
                    using (var fill = new SolidBrush(Color.FromArgb(31, 123, 86))) e.Graphics.FillEllipse(fill, dot);
                }
                else
                {
                    using (var outline = new Pen(Color.FromArgb(100, 113, 132), 1.5f)) e.Graphics.DrawEllipse(outline, dot);
                }
                using (var pen = new Pen(headerInk, 1.5f))
                {
                    int tilt = expanded ? -3 : 3;
                    e.Graphics.DrawLines(pen, new[] { new Point(x - 4, y - tilt), new Point(x, y), new Point(x + 4, y - tilt) });
                }
                if (rulesButton.Focused)
                    ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(rulesButton.ClientRectangle, -3, -3), accent, rulesButton.BackColor);
            };
            rulesButton.GotFocus += (s, e) => rulesButton.Invalidate();
            rulesButton.LostFocus += (s, e) => rulesButton.Invalidate();
            LineItem Item() => widgets.SelectedItem as LineItem;
            Label Caption(string text, int x, int y) => new Label { Text = text, ForeColor = WidgetMuted, Location = new Point(x, y), AutoSize = true };
            ComboBox Choice(string name, int x, int y, int width) =>
                new ComboBox { Name = name.Replace(" ", ""), AccessibleName = name, DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(x, y), Width = width };
            NumericUpDown Number(string name, int x, int y) =>
                new NumericUpDown { AccessibleName = name, Location = new Point(x, y), Width = 68, DecimalPlaces = 1, Increment = 1 };

            void LoadNumber(NumericUpDown input, double value, bool temperature, bool fahrenheit, bool weather = false)
            {
                input.Minimum = weather ? (fahrenheit ? -148 : -100) : temperature ? -40 : 0;
                input.Maximum = weather ? (fahrenheit ? 212 : 100) : temperature ? (fahrenheit ? 392 : 200) : 100;
                double shown = temperature && fahrenheit ? value * 1.8 + 32 : value;
                if (double.IsNaN(shown)) shown = 0;
                input.Value = (decimal)Math.Max((double)input.Minimum, Math.Min((double)input.Maximum, shown));
            }

            double ReadNumber(NumericUpDown input, LineItem item) =>
                SupportsTemperatureVisibility(item) && item.UseFahrenheit ? Math.Round(((double)input.Value - 32) / 1.8, 6) : (double)input.Value;

            PriorityOrder CurrentOrder() => new PriorityOrder(widgets.Items.Cast<LineItem>());
            double DefaultTarget(LineItem item) => IsTemperatureWidget(item)
                ? GetWidgetTemperatureThresholds(item, temperatureDevice?.Invoke()).Warning :
                DefaultRamPriorityTarget(IsLineItemType(item, "ramusage") ? item.RamPriorityUnit : 0);
            string TargetUnit(LineItem item) => IsTemperatureWidget(item) ? (item.UseFahrenheit ? "°F" : "°C") :
                !IsLineItemType(item, "ramusage") ? "%" :
                item.RamPriorityUnit == (int)RamTargetUnit.Mb ? "MB" : item.RamPriorityUnit == (int)RamTargetUnit.Gb ? "GB" : "%";
            string FormatTarget(double value, LineItem item) => IsTemperatureWidget(item) || TargetUnit(item) == "%"
                ? FormatNumber(value, item) : value.ToString("0.#") + " " + TargetUnit(item);
            void LoadTarget(LineItem item)
            {
                double target = DefaultTarget(item);
                limit.AccessibleDescription = IsTemperatureWidget(item)
                    ? "The initial target is the widget's warning temperature: " + FormatTarget(target, item)
                    : "The initial target is " + FormatTarget(target, item) + ".";
                string unit = TargetUnit(item);
                if (unit == "MB" || unit == "GB")
                {
                    limit.Minimum = 0;
                    limit.Maximum = unit == "MB" ? 1048576000 : 1024000;
                    var requested = item.PriorityThreshold ?? target;
                    limit.Value = double.IsNaN(requested) ? 0 :
                        (decimal)Math.Max(0, Math.Min((double)limit.Maximum, requested));
                }
                else LoadNumber(limit, item.PriorityThreshold ?? target, IsTemperatureWidget(item), item.UseFahrenheit);
            }

            void RefreshSummary(LineItem item)
            {
                var priorityMode = (WidgetPriority)item.PriorityMode;
                bool weather = IsWeatherWidget(item);
                takeoverEnabled = !weather && priorityMode != WidgetPriority.Off;
                string visibilityText = item.VisibilityThreshold.HasValue && (SupportsNumericRules(item) || weather)
                    ? (weather && item.VisibilityAtOrBelow ? "Show at or below " : "Show at or above ") +
                        FormatNumber(item.VisibilityThreshold.Value, item) : "";
                if (IsLineItemType(item, "nowplaying") && item.SkipIfNotPlaying)
                    visibilityText = "Rotate when playing";
                rulesEnabled = takeoverEnabled || visibilityText.Length > 0;
                panel.BackColor = rulesEnabled ? Color.FromArgb(239, 248, 242) : WidgetSectionFill;
                rulesButton.BackColor = rulesEnabled ? Color.FromArgb(224, 243, 231) : Color.FromArgb(233, 241, 252);
                rulesButton.FlatAppearance.BorderColor = rulesEnabled ? Color.FromArgb(162, 202, 177) : Color.FromArgb(177, 198, 226);
                rulesButton.FlatAppearance.MouseOverBackColor = rulesEnabled ? Color.FromArgb(213, 237, 222) : Color.FromArgb(221, 235, 252);
                rulesButton.FlatAppearance.MouseDownBackColor = rulesEnabled ? Color.FromArgb(204, 231, 214) : Color.FromArgb(211, 228, 249);
                if (takeoverEnabled)
                {
                    var order = CurrentOrder();
                    int level = order.Level(item);
                    bool tied = level > 0 && order.Levels[level - 1].Count > 1;
                    rank.Text = "Level " + level + (tied ? " (tied)" : level == 1 ? " (highest)" : "");
                    string trigger = priorityMode == WidgetPriority.MediaPlaying ? "While playing"
                        : priorityMode == WidgetPriority.MediaPresent ? "While media is present"
                        : (priorityMode == WidgetPriority.AtOrBelow ? "At or below " : "At or above ") +
                            FormatTarget(item.PriorityThreshold ?? DefaultTarget(item), item);
                    summary = "Level " + level + (tied ? " (tied)" : "") + " · " + trigger;
                    if (visibilityText.Length > 0) summary += " · " + visibilityText;
                }
                else
                {
                    rank.Text = "Not assigned";
                    summary = visibilityText.Length > 0 ? visibilityText : "No rules configured";
                }
                rulesButton.AccessibleDescription = (expanded ? "Expanded. " : "Collapsed. ") +
                    (weather ? "" : takeoverEnabled ? "Takeover enabled. " : "Takeover disabled. ") + summary;
                rulesButton.Invalidate();
            }

            void LayoutRules(bool numeric, bool media, bool ram, bool weather)
            {
                int U(int value) => (int)Math.Round(value * rulesButton.Height / 51.0);
                int right = details.Width;
                int y = U(12);
                void PlaceCaption(Label control, int x, int top) => control.Location = new Point(x, top + U(4));

                if (!weather)
                {
                    PlaceCaption(whenLabel, 0, y);
                    mode.SetBounds(U(112), y, Math.Max(U(180), right - U(114)), mode.Height);
                    y = mode.Bottom + U(8);

                    if (ram)
                    {
                        PlaceCaption(ramUnitsLabel, 0, y);
                        ramUnits.SetBounds(U(112), y, U(174), ramUnits.Height);
                        y = ramUnits.Bottom + U(8);
                    }
                    if (numeric)
                    {
                        PlaceCaption(limitLabel, 0, y);
                        limit.SetBounds(U(112), y, U(ram ? 104 : 72), limit.Height);
                        PlaceCaption(limitUnit, limit.Right + U(7), y);
                        y = limit.Bottom + U(8);
                    }

                    changePriority.SetBounds(right - U(128), y, U(126), U(28));
                    PlaceCaption(rankLabel, 0, y);
                    PlaceCaption(rank, U(112), y);
                    y = changePriority.Bottom + U(10);
                }

                if (numeric || media || weather)
                {
                    if (!weather)
                    {
                        lineDivider.SetBounds(0, y - U(3), right - 2, 1);
                        y += U(8);
                    }
                    PlaceCaption(visibilityLabel, 0, y);
                    visibility.SetBounds(U(112), y, Math.Max(U(168), right - U(114)), visibility.Height);
                    y = visibility.Bottom + U(10);
                    if ((numeric || weather) && visibility.SelectedIndex > 0)
                    {
                        PlaceCaption(minimumLabel, 0, y);
                        minimum.SetBounds(U(112), y, U(72), minimum.Height);
                        PlaceCaption(minimumUnit, U(191), y);
                        y = minimum.Bottom + U(10);
                    }
                }

                details.Top = rulesButton.Height;
                details.Height = y;
                panel.Height = rulesButton.Height + (expanded ? details.Height + 1 : 0);
            }

            void Refresh()
            {
                var item = Item();
                if (item == null) { panel.Visible = false; reflow(); return; }
                loading = true;
                try
                {
                    bool temp = IsTemperatureWidget(item), numeric = SupportsNumericRules(item), media = IsLineItemType(item, "nowplaying");
                    bool ram = IsLineItemType(item, "ramusage");
                    bool weather = IsWeatherWidget(item);
                    bool supportsRules = numeric || media || weather;
                    font.BeginUpdate();
                    font.Items.Clear();
                    font.Items.AddRange(new object[] { "(default)", "Native font" });
                    font.Items.AddRange(editorFonts);
                    var saved = item.FontFamily ?? "";
                    bool nativeFont = saved == NativeWidgetFontId;
                    int known = Array.FindIndex(editorFonts, name => name.Equals(saved, StringComparison.OrdinalIgnoreCase));
                    int index = nativeFont ? 1 : known < 0 ? 0 : known + 2;
                    bool missingFont = !nativeFont && saved.Length != 0 && !installedFonts.Contains(saved);
                    if (index == 0 && saved.Length != 0)
                    { index = font.Items.Count; font.Items.Add(missingFont ? saved + " (not installed)" : saved); }
                    font.SelectedIndex = index;
                    font.EndUpdate();
                    size.SelectedIndex = Math.Max(0, Math.Min(2, item.FontSizeStep));
                    bool inheritedBitmap = _oledFontCombo?.SelectedIndex > 0;
                    size.Enabled = !nativeFont && ((index >= 2 && !missingFont) || inheritedBitmap);
                    fontHint.Text = nativeFont ? "Ignores General. Uses the native font or its pixel match." :
                        missingFont ? "Font not installed. Uses the General setting instead." :
                        index >= 2 ? "Applies only while this widget is displayed." :
                        inheritedBitmap ? "Inherits General → OLED font. Size is set per widget." :
                        "Inherits General → OLED font. Native text has a fixed size.";

                    panel.Visible = supportsRules;
                    details.Visible = expanded;
                    RefreshSummary(item);

                    if (supportsRules)
                    {
                        whenLabel.Visible = mode.Visible = rankLabel.Visible = rank.Visible = changePriority.Visible = !weather;
                        mode.Items.Clear();
                        mode.Items.Add("Off");
                        if (media) mode.Items.AddRange(new object[] { "While media is present", "Only while playing" });
                        else if (temp) mode.Items.AddRange(new object[] { "At or above target temperature", "At or below target temperature" });
                        else mode.Items.AddRange(new object[] { "At or above target usage", "At or below target usage" });
                        mode.SelectedIndex = media
                            ? item.PriorityMode == (int)WidgetPriority.MediaPresent ? 1 : item.PriorityMode == (int)WidgetPriority.MediaPlaying ? 2 : 0
                            : item.PriorityMode == (int)WidgetPriority.Threshold ? 1 : item.PriorityMode == (int)WidgetPriority.AtOrBelow ? 2 : 0;
                        changePriority.Enabled = rank.Enabled = rankLabel.Enabled = mode.SelectedIndex != 0;

                        ramUnits.Visible = ramUnitsLabel.Visible = ram;
                        ramUnits.Enabled = ramUnitsLabel.Enabled = mode.SelectedIndex != 0;
                        ramUnits.SelectedIndex = ram ? Math.Max(0, Math.Min(2, item.RamPriorityUnit)) : 0;
                        limitLabel.Visible = limit.Visible = limitUnit.Visible = numeric;
                        limitLabel.Text = temp ? "Temperature" : "Target usage";
                        limit.Enabled = limitLabel.Enabled = limitUnit.Enabled = mode.SelectedIndex != 0;
                        if (numeric) LoadTarget(item);

                        visibilityLabel.Visible = visibility.Visible = numeric || media || weather;
                        lineDivider.Visible = !weather;
                        visibility.Items.Clear();
                        if (weather) visibility.Items.AddRange(new object[] { "Always", "At or above temperature", "At or below temperature" });
                        else visibility.Items.AddRange(new object[] { "Always", media ? "When media is playing" : "At or above threshold" });
                        visibility.SelectedIndex = (media ? item.SkipIfNotPlaying : item.VisibilityThreshold.HasValue)
                            ? weather && item.VisibilityAtOrBelow ? 2 : 1 : 0;
                        minimumLabel.Visible = minimum.Visible = minimumUnit.Visible = (numeric || weather) && visibility.SelectedIndex > 0;
                        minimumLabel.Text = weather ? "Temperature" : "Minimum";
                        LoadNumber(minimum, item.VisibilityThreshold ?? (weather ? 20 : 80), temp || weather, item.UseFahrenheit, weather);
                        limitUnit.Text = TargetUnit(item);
                        minimumUnit.Text = temp || weather ? (item.UseFahrenheit ? "°F" : "°C") : "%";
                        LayoutRules(numeric, media, ram, weather);
                    }
                }
                finally { loading = false; }
                reflow();
            }

            void Change(Action<LineItem> edit)
            {
                if (loading || Item() == null) return;
                edit(Item()); EvaluateDirty(); Refresh();
            }
            font.SelectedIndexChanged += (s, e) => Change(item =>
            {
                if (font.SelectedIndex == 0) item.FontFamily = "";
                else if (font.SelectedIndex == 1) item.FontFamily = NativeWidgetFontId;
                else if (font.SelectedIndex >= 2 && font.SelectedIndex < editorFonts.Length + 2)
                    item.FontFamily = editorFonts[font.SelectedIndex - 2];
            });
            size.SelectedIndexChanged += (s, e) => Change(item => item.FontSizeStep = size.SelectedIndex);
            rulesButton.Click += (s, e) =>
            {
                expanded = !expanded;
                Refresh();
                if (expanded) revealSection?.Invoke(panel);
            };
            mode.SelectedIndexChanged += (s, e) => Change(item => item.PriorityMode = mode.SelectedIndex <= 0 ? 0 :
                IsLineItemType(item, "nowplaying") ? (int)(mode.SelectedIndex == 1 ? WidgetPriority.MediaPresent : WidgetPriority.MediaPlaying) :
                (int)(mode.SelectedIndex == 1 ? WidgetPriority.Threshold : WidgetPriority.AtOrBelow));
            changePriority.Click += (s, e) =>
            {
                var item = Item();
                if (item == null || item.PriorityMode == 0) return;
                var order = CurrentOrder();
                using (var dialog = new PriorityOrderDialog(order, item, isTop))
                    if (dialog.ShowDialog(this) == DialogResult.OK) { order.Apply(); EvaluateDirty(); Refresh(); }
            };

            limit.ValueChanged += (s, e) =>
            {
                var item = Item();
                if (item == null || loading) return;
                double value = ReadNumber(limit, item);
                item.PriorityThreshold = Math.Abs(value - DefaultTarget(item)) < 0.00001 ? (double?)null : value;
                RefreshSummary(item);
                EvaluateDirty();
            };
            ramUnits.SelectedIndexChanged += (s, e) => Change(item =>
            {
                if (!IsLineItemType(item, "ramusage") || ramUnits.SelectedIndex < 0) return;
                var oldUnit = (RamTargetUnit)item.RamPriorityUnit;
                var newUnit = (RamTargetUnit)ramUnits.SelectedIndex;
                if (oldUnit == newUnit) return;
                if (oldUnit == RamTargetUnit.Mb && newUnit == RamTargetUnit.Gb) item.PriorityThreshold /= 1024;
                else if (oldUnit == RamTargetUnit.Gb && newUnit == RamTargetUnit.Mb) item.PriorityThreshold *= 1024;
                else item.PriorityThreshold = null;
                item.RamPriorityUnit = (int)newUnit;
            });
            minimum.ValueChanged += (s, e) =>
            {
                if (loading || Item() == null || visibility.SelectedIndex <= 0) return;
                var item = Item();
                item.VisibilityThreshold = ReadNumber(minimum, item);
                RefreshSummary(item);
                EvaluateDirty();
            };
            visibility.SelectedIndexChanged += (s, e) => Change(item =>
            {
                if (IsLineItemType(item, "nowplaying")) item.SkipIfNotPlaying = visibility.SelectedIndex == 1;
                else
                {
                    item.VisibilityThreshold = visibility.SelectedIndex > 0 ? ReadNumber(minimum, item) : (double?)null;
                    item.VisibilityAtOrBelow = IsWeatherWidget(item) && visibility.SelectedIndex == 2;
                }
            });
            WidgetThresholdContextChanged += () =>
            {
                if (!loading && IsTemperatureWidget(Item()))
                {
                    loading = true;
                    try { LoadTarget(Item()); }
                    finally { loading = false; }
                    RefreshSummary(Item());
                }
            };
            WidgetFontDefaultChanged += Refresh;
            return (panel, fontRows, Refresh);
        }

        private static string FormatNumber(double value, LineItem item) => SupportsTemperatureVisibility(item)
            ? item.UseFahrenheit ? (value * 1.8 + 32).ToString("0.#") + " °F" : value.ToString("0.#") + " °C"
            : value.ToString("0.#") + "%";

    }
}
