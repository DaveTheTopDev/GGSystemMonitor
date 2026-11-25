using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using NativeOledPixelFont = GGSystemMonitor.NativeOledPixelFont;
using static GGSystemMonitor.Program;

internal static partial class Regression
{
    private static byte[] RenderBitmapForTest(string first, string second, Font top, Font bottom,
        string topSuffix, string bottomSuffix, bool topWeather = false, bool bottomWeather = false) =>
        (byte[])Call("RenderOledBitmap", first, second, top, bottom, topSuffix, bottomSuffix, topWeather, bottomWeather);

    private static void CheckWidgetEditor(Form form, Type formType, BindingFlags fields, ListBox list, string settingsPath, bool capture)
    {
        form.Show();
        Control ancestor = list.Parent;
        while (!(ancestor is TabControl)) ancestor = ancestor.Parent;
        var tabs = (TabControl)ancestor;
        tabs.SelectedIndex = 0;
        list.Items.Clear();
        var cpu = new LineItem { Type = "CpuTemperature", PriorityRank = 4, WarnTemp = "77" };
        var gpu = new LineItem { Type = "GpuTemperature", PriorityRank = 4, PriorityMode = (int)WidgetPriority.Threshold };
        list.Items.AddRange(new object[] { cpu, gpu });
        list.SelectedIndex = 0;
        Application.DoEvents();
        var editor = form.Controls.Find("TopWidgetEditor", true).Single();
        var priority = form.Controls.Find("TopWidgetPriority", true).Single();
        var unique = editor.Parent.Controls.Find("WidgetSpecificSettings", false).Single();
        var extra = editor.Parent.Controls.Find("WidgetExtraSettings", false).Single();
        var topSettings = editor.Parent;
        var bottomGroup = form.Controls.Find("BottomSelectedWidget", true).Single().Parent;
        Check(topSettings.BackColor != topSettings.Parent.BackColor && topSettings.Parent.BackColor != bottomGroup.BackColor,
            "selected settings and the two line lists have distinct backgrounds");
        bool Ordered() => unique.Visible && extra.Visible && editor.Bottom < unique.Top && unique.Bottom < extra.Top &&
            extra.Bottom < priority.Top;
        Check(Ordered(), "OLED display settings appear first");
        Check(topSettings.Parent.Font.Bold && topSettings.Parent.Font.Size > form.Font.Size && bottomGroup.Font.Bold,
            "top and bottom line titles are larger and bold");
        Check(topSettings.Parent.Text == "Top Line Widgets" && bottomGroup.Text == "Bottom Line Widgets" &&
            topSettings.Parent.Controls.OfType<Button>().Count(button => button.FlatStyle == FlatStyle.Flat &&
                button.ForeColor == Color.White && button.BackColor.A == 255) == 4, "widget lists have filled action buttons and the new headings");
        var fonts = (ComboBox)editor.Controls.Find("Widgetfont", true).Single();
        var sizes = (ComboBox)editor.Controls.Find("Widgetfontsize", true).Single();
        Check(fonts.Visible && (string)fonts.SelectedItem == "(default)" && !sizes.Enabled, "widget inherits native font and size is fixed");
        var generalFont = (ComboBox)formType.GetField("_oledFontCombo", fields).GetValue(form);
        int generalFontIndex = generalFont.SelectedIndex;
        generalFont.SelectedItem = "Consolas";
        Check((string)fonts.Items[1] == "Native font" && !generalFont.Items.Contains("Native font") && sizes.Enabled,
            "Native font is available immediately under the widget default, not in General");
        fonts.SelectedIndex = 1;
        Check(cpu.FontFamily == NativeWidgetFontId && !sizes.Enabled, "explicit native ignores the General font and uses a fixed size");
        fonts.SelectedIndex = 0;
        generalFont.SelectedIndex = generalFontIndex;
        fonts.SelectedItem = "Courier New";
        sizes.SelectedIndex = 2;
        Check(cpu.FontFamily == "Courier New" && cpu.FontSizeStep == 2, "editor changes only the selected widget font and size");
        var rules = (Button)priority.Controls.Find("PriorityToggle", false).Single();
        Check(rules.Width == priority.ClientSize.Width && rules.AccessibleName == "Priority and Visibility" &&
            rules.AccessibleDescription.StartsWith("Collapsed"),
            "priority toggle spans the section and exposes collapsed state");
        rules.PerformClick();
        var mode = (ComboBox)priority.Controls.Find("Prioritycondition", true).Single();
        var target = (NumericUpDown)priority.Controls.Find("PriorityTarget", true).Single();
        var changePriority = (Button)priority.Controls.Find("ChangePriority", true).Single();
        var visibility = (ComboBox)priority.Controls.Find("Visibilitycondition", true).Single();
        Check(mode.Items.Cast<string>().SequenceEqual(new[] { "Off", "At or above target temperature", "At or below target temperature" }) &&
            !target.Enabled && !changePriority.Enabled && visibility.Enabled && !rules.AccessibleDescription.Contains("Priority off"),
            "CPU takeover offers both comparisons and disables only takeover controls");
        mode.SelectedIndex = 1;
        Check(cpu.PriorityMode == (int)WidgetPriority.Threshold && !cpu.PriorityThreshold.HasValue && target.Value == 77 &&
            changePriority.Enabled, "numeric CPU target starts at its configured warning temperature");
        Check(rules.BackColor.G > rules.BackColor.B && priority.BackColor.G > priority.BackColor.B,
            "enabled rule tints its header and body green");
        mode.SelectedIndex = 2;
        target.Value = 72;
        Check(cpu.PriorityMode == (int)WidgetPriority.AtOrBelow && cpu.PriorityThreshold == 72, "below mode keeps an editable temperature target");
        target.Value = 77;
        Check(cpu.PriorityThreshold == null, "restoring the warning number follows the warning threshold again");
        var rulePanel = mode.Parent;
        var threshold = rulePanel.Controls.OfType<NumericUpDown>().Single(c => c.AccessibleName == "Visibility threshold");
        Check(!threshold.Visible, "always-visible widgets have no visibility number box");
        visibility.SelectedIndex = 1;
        Check(threshold.Visible && threshold.Top > visibility.Bottom, "conditional visibility shows a labeled minimum on its own row");
        threshold.Value = 85;
        Check(rules.AccessibleDescription.Contains("(tied)") && !rulePanel.Controls.OfType<CheckBox>().Any(),
            "ties are summarized without the old ranking dropdown or warning checkbox");
        using (var dialog = new GGSystemMonitor.PriorityOrderDialog(new GGSystemMonitor.PriorityOrder(new[] { cpu, gpu }), cpu, true))
        {
            dialog.Show(form);
            Application.DoEvents();
            var tree = (TreeView)dialog.Controls.Find("PriorityLevels", false).Single();
            Check(!dialog.ShowInTaskbar && tree.SelectedNode.Tag == cpu && tree.SelectedNode.Parent.Nodes.Count == 2,
                "priority editor opens on the selected item in its tie level");
            if (capture)
            {
                using (var image = new Bitmap(dialog.Width, dialog.Height))
                {
                    dialog.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                    image.Save(Path.Combine(AppContext.BaseDirectory, "settings-priority-levels.png"));
                }
            }
            typeof(Control).GetMethod("OnKeyDown", fields).Invoke(tree, new object[] { new KeyEventArgs(Keys.Up) });
            Check(tree.Nodes.Count == 2 && tree.SelectedNode.Tag == cpu && tree.SelectedNode.Parent == tree.Nodes[0],
                "Up separates a tied item into its own higher level");
            typeof(Control).GetMethod("OnKeyDown", fields).Invoke(tree, new object[] { new KeyEventArgs(Keys.Down) });
            Check(tree.Nodes.Count == 1 && tree.SelectedNode.Tag == cpu && tree.SelectedNode.Parent.Nodes.Count == 2,
                "Down first joins the next priority level instead of swapping");
            dialog.Close();
            Check(cpu.PriorityRank == gpu.PriorityRank, "closing the priority editor without Apply leaves stored ranks alone");
        }
        var together = (CheckBox)formType.GetField("_rotateTogether", fields).GetValue(form);
        together.Checked = true;
        tabs.SelectedIndex = 1; Application.DoEvents();
        tabs.SelectedIndex = 0; Application.DoEvents();
        Check(Ordered() && editor.Bottom <= editor.Parent.ClientSize.Height && mode.Visible && threshold.Visible,
            "expanded editor survives tab changes without clipping");
        var holdLabel = editor.Controls.OfType<Label>().Single(c => c.Text == "Duration (ms)");
        var holdInput = editor.Controls.OfType<NumericUpDown>().Single();
        Check(holdLabel.Right + 5 <= holdInput.Left, "OLED duration label has clearance from its input");
        Check(rules.TabStop && fonts.TabStop && mode.TabStop && threshold.TabStop, "new controls are keyboard reachable");
        if (capture)
        {
            void Capture(string name)
            {
                var group = editor.Parent;
                using (var image = new Bitmap(group.Width, group.Height))
                {
                    group.DrawToBitmap(image, group.ClientRectangle);
                    image.Save(Path.Combine(AppContext.BaseDirectory, name));
                }
            }
            Capture("settings-rules-100.png");
            form.Scale(new SizeF(1.25f, 1.25f));
            tabs.SelectedIndex = 1; Application.DoEvents();
            tabs.SelectedIndex = 0; Application.DoEvents();
            Application.DoEvents();
            Capture("settings-rules-125.png");
            Check(Ordered() && editor.Bottom <= editor.Parent.ClientSize.Height && rules.Width == priority.ClientSize.Width &&
                holdLabel.Right + 5 <= holdInput.Left,
                "expanded settings fit a simulated 125 percent scale");
        }
        var media = new LineItem { Type = "NowPlaying", Label = "{source} {artist} - {title} - {elapsed:mm:ss}/{duration:mm:ss}" };
        list.Items.Add(media);
        list.SelectedIndex = 2;
        Application.DoEvents();
        var warning = editor.Controls.OfType<Label>().Single(c => c.Text.Contains("auto-scroll"));
        var labelBox = editor.Controls.OfType<TextBox>().Single();
        Check(unique.Visible && !extra.Visible && editor.Bottom < unique.Top && unique.Bottom < priority.Top,
            "Now Playing has playback settings but no unrelated extra section");
        Check(visibility.Items.Cast<string>().SequenceEqual(new[] { "Always", "When media is playing" }) &&
            visibility.SelectedIndex == 1 && !unique.Controls.OfType<CheckBox>().Any(box => box.Text.StartsWith("Skip")),
            "media visibility defaults to When media is playing in the rule dropdown");
        visibility.SelectedIndex = 0;
        Check(!media.SkipIfNotPlaying && !threshold.Visible, "Always media rotation has no unrelated threshold input");
        Check(warning.Visible && warning.Top > labelBox.Bottom &&
            editor.Controls.Find("WidgetFontRows", false).Single().Top > warning.Bottom, "long-label warning has its own row above the font controls");
        rules.PerformClick();
        Check(!mode.Visible && rules.Height == priority.Height && editor.Bottom < priority.Top,
            "collapsing priority hides its contents and reclaims the vertical space");
        rules.PerformClick();
        var text = new LineItem { Type = "Text" };
        list.Items.Add(text);
        list.SelectedIndex = 3;
        Check(!unique.Visible && !extra.Visible && editor.Visible && !mode.Visible, "text widget omits irrelevant sections");
        var weather = new LineItem { Type = "Weather" };
        list.Items.Add(weather);
        list.SelectedIndex = 4;
        var weatherPanel = (Panel)formType.GetField("_weatherExtPanel", fields).GetValue(form);
        var suggestions = (ListBox)formType.GetField("_weatherLocationSuggestionsList", fields).GetValue(form);
        var weatherLayout = formType.GetMethod("UpdateWeatherExtPanelLayout", fields);
        Check(weatherPanel.Parent == unique && !extra.Visible, "weather location belongs to the widget-specific section");
        int closedHeight = unique.Height;
        suggestions.Items.Add("Example City");
        suggestions.Visible = true;
        weatherLayout.Invoke(form, null);
        Check(unique.Height > closedHeight && priority.Visible && editor.Bottom < unique.Top && unique.Bottom < priority.Top,
            "weather suggestions expand their own section without overlapping later sections");
        Check(!mode.Visible && !changePriority.Visible && visibility.Visible &&
            visibility.Items.Cast<string>().SequenceEqual(new[] { "Always", "At or above temperature", "At or below temperature" }),
            "Weather has both temperature comparisons but no takeover controls");
        visibility.SelectedIndex = 2;
        threshold.Value = 0;
        Check(weather.VisibilityAtOrBelow && weather.VisibilityThreshold == 0 && threshold.Visible,
            "Weather accepts a zero-degree visibility threshold");
        unique.Controls.OfType<CheckBox>().Single(box => box.Visible && box.Text.StartsWith("Display in")).Checked = true;
        Check(threshold.Value == 32, "Weather displays the same threshold when switching to Fahrenheit");
        threshold.Value = -4;
        Check(weather.VisibilityThreshold == -20, "Weather stores edited Fahrenheit as Celsius");
        if (capture) CaptureWeather();
        var ram = new LineItem { Type = "RamUsage", PriorityMode = (int)WidgetPriority.Threshold };
        list.Items.Add(ram);
        list.SelectedIndex = 5;
        var units = (ComboBox)priority.Controls.Find("RAMtargetunit", true).Single();
        Check(units.Visible && units.Bottom < target.Top && units.SelectedIndex == 0 && target.Value == 80,
            "RAM takeover has a percentage selector above its target");
        units.SelectedIndex = 1;
        Check(ram.RamPriorityUnit == (int)RamTargetUnit.Mb && target.Value == 8192 &&
            priority.Controls.Find("PriorityTarget", true).Single().Parent.Controls.OfType<Label>().Any(label =>
                label.Text == "MB" && label.Left > target.Right && label.Top >= target.Top),
            "RAM MB selection updates target range and its visible unit");
        target.Value = 12288;
        units.SelectedIndex = 2;
        Check(target.Value == 12 && ram.PriorityThreshold == 12, "RAM target converts MB to GB without changing its value in memory");
        list.Items.Remove(ram);
        list.Items.Remove(weather);
        list.Items.Remove(text);
        list.Items.Remove(media);
        list.SelectedIndex = 0;
        formType.GetMethod("SaveSettings", fields).Invoke(form, null);
        var saved = JObject.Parse(File.ReadAllText(settingsPath));
        var first = saved["TopLineItems"][0];
        Check((bool)saved["RotateLinesTogether"] && (string)first["FontFamily"] == "Courier New" &&
            (int)first["FontSizeStep"] == 2 && (int)first["PriorityMode"] == (int)WidgetPriority.AtOrBelow && (double)first["VisibilityThreshold"] == 85,
            "save and reload round trip sync, font, priority, and visibility");

        void CaptureWeather()
        {
            using (var image = new Bitmap(topSettings.Width, topSettings.Height))
            {
                topSettings.DrawToBitmap(image, topSettings.ClientRectangle);
                image.Save(Path.Combine(AppContext.BaseDirectory, "settings-weather-visibility.png"));
            }
        }
    }

    private static void CheckPresetStorage()
    {
        string path = Path.Combine(Path.GetTempPath(), "ggsm-preset-regression-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var factory = SettingsManager.LoadFrom(path);
            Check(factory.SettingsVersion == 3 && factory.ActiveDisplayPresetId == DisplayPreset.DefaultId &&
                factory.DisplayPresets.Count == 0 && factory.RotateLinesTogether, "fresh settings open on factory preset with synchronized rotation on");
            CheckFactoryProfile(factory);
            var written = JObject.Parse(File.ReadAllText(path));
            Check((double?)written["TopLineItems"][1]["VisibilityThreshold"] == 70 &&
                (double?)written["BottomLineItems"][1]["VisibilityThreshold"] == 75 &&
                written["TopLineItems"][0]["PriorityThreshold"].Type == JTokenType.Null &&
                written["BottomLineItems"][0]["PriorityThreshold"].Type == JTokenType.Null,
                "fresh settings on disk match the built-in layout and contain no fixed takeover temperatures");
            var old = JObject.Parse(@"{
                ""SettingsVersion"":2, ""RotationIntervalMs"":7100, ""RotateLinesTogether"":true,
                ""OledFont"":""Consolas"", ""ShowCapsLockIndicator"":false, ""OutputProfile"":""gamedac"",
                ""TopLineItems"":[{""Type"":""CpuTemperature"",""Label"":""legacy text"",""PriorityMode"":4,""PriorityThreshold"":68,
                                  ""FontFamily"":""Courier New"",""HardwareId"":""/cpu/3"",""FutureField"":""keep""}],
                ""BottomLineItems"":[""Date""], ""_note_fixture"":""preserved""
            }");
            File.WriteAllText(path, old.ToString());
            var migrated = SettingsManager.LoadFrom(path);
            var custom = migrated.DisplayPresets.Single();
            Check(custom.Name == "Custom 1" && custom.Id == migrated.ActiveDisplayPresetId && migrated.RotateLinesTogether &&
                migrated.RotationIntervalMs == 7100 && migrated.TopLineItems[0].PriorityThreshold == 68 &&
                migrated.TopLineItems[0].FontFamily == "Courier New" && migrated.TopLineItems[0].HardwareId == "/cpu/3" &&
                migrated.TopLineItems[0].Label == "legacy text" && migrated.BottomLineItems[0].Type == "Date",
                "upgrade selects Custom 1 with both lines, rotation, and existing widget options");
            var document = JObject.Parse(File.ReadAllText(path));
            var migratedLayout = document["DisplayPresets"][0]["Layout"];
            Check(migrated.OledFont == "Consolas" && migrated.OutputProfile == "gamedac" && !migrated.ShowCapsLockIndicator &&
                migratedLayout["OledFont"] == null && migratedLayout["OutputProfile"] == null &&
                (string)document["_note_fixture"] == "preserved" && (string)migratedLayout["TopLineItems"][0]["FutureField"] == "keep",
                "migration keeps General global and preserves notes and legacy widget data");
            Check(SettingsManager.LoadFrom(path).DisplayPresets.Single().Id == custom.Id, "migration is idempotent");

            document["TopLineItems"] = new JArray("Time");
            File.WriteAllText(path, document.ToString());
            Check(SettingsManager.LoadFrom(path).TopLineItems[0].Type == "CpuTemperature",
                "monitor projects the active preset instead of a stale top-level display");
            document["ActiveDisplayPresetId"] = DisplayPreset.DefaultId;
            File.WriteAllText(path, document.ToString());
            var selectedFactory = SettingsManager.LoadFrom(path);
            Check(selectedFactory.RotationIntervalMs == 3000 && selectedFactory.TopLineItems[0].Label == "CPU: {temp:F1}°C" &&
                selectedFactory.DisplayPresets.Single().Layout.TopLineItems[0].Label == "legacy text",
                "factory selection uses the new layout without changing the user's saved presets");
        }
        finally { File.Delete(path); }
    }

    private static void CheckFactoryProfile(AppSettings factory)
    {
        var top = factory.TopLineItems;
        var bottom = factory.BottomLineItems;
        Check(top.Select(item => item.Type).SequenceEqual(new[] { "CpuTemperature", "CpuUsage", "RamUsage" }) &&
            bottom.Select(item => item.Type).SequenceEqual(new[] { "GpuTemperature", "GpuUsage" }) &&
            top.Concat(bottom).Select(item => item.Label).SequenceEqual(new[]
            {
                "CPU: {temp:F1}°C", "CPU USE: {pct:F0}%", "RAM: {used:0.#}/{total:0.#}GB",
                "GPU: {temp:F1}°C", "GPU USE: {pct:F0}%"
            }) && factory.RotateLinesTogether && factory.RotationIntervalMs == 3000,
            "factory reproduces Custom 2 widget order, labels and timing");
        Check(top[1].VisibilityThreshold == 70 && top[2].VisibilityThreshold == 70 && bottom[1].VisibilityThreshold == 75 &&
            new[] { top[1], top[2], bottom[1] }.All(item => item.PriorityMode == 0 &&
                !EvaluateWidgetRule(item, new WidgetReading(item.VisibilityThreshold - .1), false, false).Visible &&
                EvaluateWidgetRule(item, new WidgetReading(item.VisibilityThreshold), false, false).Visible),
            "factory usage widgets keep the 70/70/75 percent visibility thresholds");
        foreach (var (item, firstModel, firstWarning, secondModel, secondWarning) in new[]
        {
            (top[0], "Intel Core Ultra 9 285H", 95, "AMD Ryzen 9 9950X3D2", 80),
            (bottom[0], "NVIDIA GeForce RTX 5050", 74, "NVIDIA GeForce RTX 5090", 77)
        })
        {
            Check(item.WarnTemp == "AUTO" && item.CritTemp == "AUTO" && item.PriorityThreshold == null &&
                item.PriorityMode == (int)WidgetPriority.Threshold && item.PriorityRank == 1 &&
                item.VisibilityThreshold == null && item.HardwareId == "" && item.SensorOverride == null,
                "factory temperature widget carries no machine-specific sensor or threshold: " + item.Type);
            foreach (var (model, expectedWarning) in new[] { (firstModel, firstWarning), (secondModel, secondWarning) })
            {
                var (warning, critical) = GetWidgetTemperatureThresholds(item, model);
                var below = EvaluateWidgetRule(item, new WidgetReading(warning - .1, warning, critical), false, false);
                var atWarning = EvaluateWidgetRule(item, new WidgetReading(warning, warning, critical), false, false);
                Check(warning == expectedWarning && below.Visible && below.Priority == 0 && atWarning.Priority == 1,
                    "factory takes priority exactly at the detected device's warning limit: " + model);
            }
        }
    }

    private static void CheckPresetEditor(Form form, Type formType, BindingFlags fields, ListBox top, string path, bool capture)
    {
        var presets = (ComboBox)form.Controls.Find("DisplayPresets", true).Single();
        var gear = (Button)form.Controls.Find("DisplayPresetActions", true).Single();
        var saveButton = (Button)formType.GetField("_btnSave", fields).GetValue(form);
        Check(gear.BackColor != gear.Parent.BackColor && !gear.UseVisualStyleBackColor, "preset gear has a filled background");
        var rename = gear.ContextMenuStrip.Items[0];
        var delete = gear.ContextMenuStrip.Items[1];
        var clone = gear.ContextMenuStrip.Items[2];
        var append = formType.GetMethod("CreateDisplayPreset", fields);
        var changeName = formType.GetMethod("RenameDisplayPreset", fields);
        var changeSelection = formType.GetMethod("SwitchDisplayPreset", fields);
        var save = formType.GetMethod("SaveSettings", fields);
        var duplicate = formType.GetMethod("CloneDisplayPreset", fields);
        var remove = formType.GetMethod("DeleteDisplayPreset", fields);
        var ggPath = (TextBox)formType.GetField("_ggPathBox", fields).GetValue(form);
        var rotation = (NumericUpDown)formType.GetField("_rotationNud", fields).GetValue(form);
        var pair = (CheckBox)formType.GetField("_rotateTogether", fields).GetValue(form);
        Check(pair.Parent == rotation.Parent && pair.Top > rotation.Bottom &&
            pair.Parent.Controls.Find("RotationInfo", false).Single().AccessibleDescription.Contains("longer"),
            "rotation toggle and info icon are below the rotation interval");
        var savedSelection = (DisplayPreset)presets.SelectedItem;
        string savedPath = SettingsManager.LoadFrom(path).GGEngineCorePropsPath;
        ggPath.Text += ".test";
        string expectedPath = ggPath.Text;
        append.Invoke(form, new object[] { "Evening" });
        var evening = (DisplayPreset)presets.SelectedItem;
        Check(SettingsManager.LoadFrom(path).ActiveDisplayPresetId == evening.Id && saveButton.Enabled,
            "creating a preset activates immediately and leaves General edits pending");
        top.Items.Clear();
        top.Items.Add(new LineItem { Type = "Text", Label = "evening sample" });
        rotation.Value = 9400;
        pair.Checked = false;
        changeName.Invoke(form, new object[] { "(default)" });
        Check(evening.Name == "Evening", "factory preset name cannot be reused");
        changeName.Invoke(form, new object[] { "Night desk" });
        changeSelection.Invoke(form, new object[] { savedSelection.Id });
        Check(((LineItem)top.Items[0]).Type == "CpuTemperature" && rotation.Value != 9400 && pair.Checked && ggPath.Text == expectedPath,
            "switching presets keeps unsaved General edits in the form");
        Check(SettingsManager.LoadFrom(path).ActiveDisplayPresetId == savedSelection.Id &&
            SettingsManager.LoadFrom(path).GGEngineCorePropsPath == savedPath,
            "preset switch writes the active layout but not unsaved General settings");
        using (var notification = new System.Threading.ManualResetEventSlim())
        using (var watcher = new FileSystemWatcher(Path.GetDirectoryName(path), Path.GetFileName(path)))
        {
            watcher.Changed += (s, e) => notification.Set();
            watcher.Created += (s, e) => notification.Set();
            watcher.Renamed += (s, e) => notification.Set();
            watcher.EnableRaisingEvents = true;
            changeSelection.Invoke(form, new object[] { evening.Id });
            Check(notification.Wait(5000), "automatic preset activation notifies the monitor's file watcher");
        }
        Check(((LineItem)top.Items[0]).Label == "evening sample" && rotation.Value == 9400 &&
            ((DisplayPreset)presets.SelectedItem).Name == "Night desk", "renamed preset keeps its edits when switching away and back");
        var applied = SettingsManager.LoadFrom(path);
        Check(applied.ActiveDisplayPresetId == evening.Id && applied.TopLineItems[0].Label == "evening sample" &&
            applied.RotationIntervalMs == 9400 && !applied.RotateLinesTogether && applied.GGEngineCorePropsPath == savedPath,
            "monitor loads the newly selected preset without clicking Save");
        using (var reopened = (Form)Activator.CreateInstance(formType, true))
        {
            var active = (ComboBox)reopened.Controls.Find("DisplayPresets", true).Single();
            Check(((DisplayPreset)active.SelectedItem).Id == evening.Id,
                "reopened Settings selects the active preset");
        }
        save.Invoke(form, null);
        var reloaded = SettingsManager.LoadFrom(path);
        Check(reloaded.ActiveDisplayPresetId == evening.Id && reloaded.TopLineItems[0].Label == "evening sample" &&
            reloaded.GGEngineCorePropsPath == expectedPath, "Save persists the pending General edits");
        Check(rename.Enabled && delete.Enabled && clone.Enabled, "the gear menu exposes Rename, Delete, and Clone for custom presets");
        duplicate.Invoke(form, null);
        var firstClone = (DisplayPreset)presets.SelectedItem;
        ((LineItem)top.Items[0]).Label = "cloned draft";
        changeSelection.Invoke(form, new object[] { evening.Id });
        duplicate.Invoke(form, null);
        var secondClone = (DisplayPreset)presets.SelectedItem;
        Check(firstClone.Name == "Night desk 1" && secondClone.Name == "Night desk 2" &&
            ((LineItem)top.Items[0]).Label == "evening sample", "clones use incrementing names and isolated copies");
        remove.Invoke(form, null);
        Check(!presets.Items.Cast<DisplayPreset>().Any(preset => preset.Id == secondClone.Id) &&
            SettingsManager.LoadFrom(path).DisplayPresets.All(preset => preset.Id != secondClone.Id),
            "deleting a preset removes it immediately");
        changeSelection.Invoke(form, new object[] { DisplayPreset.DefaultId });
        Check((string)presets.SelectedItem.ToString() == "(default)" && !rename.Enabled && !delete.Enabled && clone.Enabled &&
            rotation.Value == 3000 && pair.Checked && ((LineItem)top.Items[0]).Label == "CPU: {temp:F1}°C",
            "built-in preset cannot be renamed or deleted but can be cloned");
        int customCount = presets.Items.Count;
        ggPath.Text += ".general-only";
        Check(((DisplayPreset)presets.SelectedItem).Id == DisplayPreset.DefaultId && presets.Items.Count == customCount,
            "editing General alone does not fork the factory layout");
        ggPath.Text = expectedPath;
        save.Invoke(form, null);
        Check(presets.Items.Count == customCount, "saving an unchanged default does not add a preset");
        Check(SettingsManager.LoadFrom(path).DisplayPresets.All(preset => preset.Id != secondClone.Id), "saved deletion survives reload");
        CheckFactoryTemperatureEditor(form, formType, fields, top);
        top.SelectedIndex = 0;
        var first = (LineItem)top.SelectedItem;
        var editor = top.Parent.Controls.Find("TopWidgetEditor", true).Single();
        var label = editor.Controls.OfType<TextBox>().Single(box => box.AccessibleName == "OLED widget label");
        label.Text = "CPU temp: {temp:F1}°C";
        var fork = (DisplayPreset)presets.SelectedItem;
        Check(presets.Items.Count == customCount + 1 && fork.Id != DisplayPreset.DefaultId && fork.Name.StartsWith("Custom ") &&
            ReferenceEquals(top.SelectedItem, first) && label.Text == first.Label && saveButton.Enabled,
            "first widget edit immediately selects a new custom preset without interrupting editing");
        rotation.Value = 8400;
        Check(((DisplayPreset)presets.SelectedItem).Id == fork.Id && presets.Items.Count == customCount + 1 &&
            SettingsManager.LoadFrom(path).ActiveDisplayPresetId == DisplayPreset.DefaultId,
            "further edits stay in the same preset draft until saved");
        save.Invoke(form, null);
        var savedFork = SettingsManager.LoadFrom(path);
        Check(presets.Items.Count == customCount + 1 && savedFork.ActiveDisplayPresetId == fork.Id &&
            savedFork.RotationIntervalMs == 8400 && savedFork.TopLineItems[0].Label == first.Label &&
            DisplayLayout.Factory().TopLineItems[0].Label == "CPU: {temp:F1}°C", "Save applies the fork without changing the factory preset");
        if (capture)
        {
            var bar = form.Controls.Find("DisplayPresetBar", true).Single();
            using (var bitmap = new Bitmap(bar.Width, bar.Height))
            {
                bar.DrawToBitmap(bitmap, bar.ClientRectangle);
                bitmap.Save(Path.Combine(AppContext.BaseDirectory, "settings-preset-bar.png"));
            }
        }
    }

    private static void CheckFactoryTemperatureEditor(Form form, Type formType, BindingFlags fields, ListBox top)
    {
        var bottom = (ListBox)form.Controls.Find("BottomWidgetList", true).Single();
        top.SelectedIndex = 0;
        bottom.SelectedIndex = 0;
        var cpuTarget = (NumericUpDown)top.Parent.Controls.Find("PriorityTarget", true).Single();
        var gpuTarget = (NumericUpDown)bottom.Parent.Controls.Find("PriorityTarget", true).Single();
        var refresh = formType.GetMethod("RefreshHardwareDetection", fields);
        string inventoryPath = Path.Combine(AppContext.BaseDirectory, "hardware.json");
        byte[] original = File.Exists(inventoryPath) ? File.ReadAllBytes(inventoryPath) : null;
        try
        {
            void ReportHardware(string cpu, string gpu)
            {
                File.WriteAllText(inventoryPath, new JObject { ["CpuName"] = cpu, ["GpuName"] = gpu }.ToString());
                refresh.Invoke(form, null);
            }
            ReportHardware("AMD Ryzen 9 9950X3D2", "NVIDIA GeForce RTX 5090");
            Check(cpuTarget.Value == 80 && gpuTarget.Value == 77, "factory priority target fields follow the reported CPU and GPU");
            ReportHardware("Intel Core Ultra 9 285H", "NVIDIA GeForce RTX 5050");
            Check(cpuTarget.Value == 95 && gpuTarget.Value == 74 &&
                ((LineItem)top.SelectedItem).PriorityThreshold == null && ((LineItem)bottom.SelectedItem).PriorityThreshold == null &&
                !(bool)formType.GetField("_isDirty", fields).GetValue(form),
                "late hardware detection refreshes both targets without creating a preset or storing a fixed value");
        }
        finally
        {
            if (original == null) File.Delete(inventoryPath);
            else File.WriteAllBytes(inventoryPath, original);
        }
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendWidgetMenuMessage(IntPtr handle, int message, IntPtr source, IntPtr position);

    private static void CheckWidgetListMenus(Form form, Type formType, BindingFlags fields, ListBox top, string path, bool capture)
    {
        var bottom = (ListBox)form.Controls.Find("BottomWidgetList", true).Single();
        var addTop = (Button)form.Controls.Find("AddTopWidget", true).Single();
        var addBottom = (Button)form.Controls.Find("AddBottomWidget", true).Single();
        foreach (var (list, plus) in new[] { (top, addTop), (bottom, addBottom) })
        {
            Check(plus.Left > list.Right && plus.AccessibleName.StartsWith("Add widget") && plus.ContextMenuStrip.Items.Count == 10 &&
                !list.Parent.Controls.OfType<ComboBox>().Any(), "plus menu replaces the old picker beside " + list.AccessibleName);
            Check(list.Parent.Controls.OfType<Button>().All(button =>
                    button.Left > list.Right && button.Bottom <= list.Bottom),
                "line actions fit alongside " + list.AccessibleName);
        }

        var first = new LineItem { Type = "Text", Label = "First" };
        var copied = new LineItem { Type = "RamUsage", Label = "Memory", DurationMs = 5800, FontFamily = "Courier New",
            PriorityMode = (int)WidgetPriority.Threshold, PriorityRank = 8, VisibilityThreshold = 25 };
        var third = new LineItem { Type = "Text", Label = "Third" };
        top.Items.Clear();
        top.Items.AddRange(new[] { first, copied, third });
        bottom.Items.Clear();
        bottom.Items.AddRange(new[] { new LineItem { Type = "Time" }, new LineItem { Type = "Date" } });
        var topMenu = top.ContextMenuStrip;
        var bottomMenu = bottom.ContextMenuStrip;
        Action<ListBox, int?> open = (list, index) =>
        {
            topMenu.Close();
            bottomMenu.Close();
            var row = index >= 0 ? list.GetItemRectangle(index.Value) : Rectangle.Empty;
            var point = list.PointToScreen(index >= 0 ? new Point(12, row.Top + row.Height / 2) :
                new Point(12, list.ClientSize.Height - 4));
            var position = index.HasValue ? new IntPtr((point.X & 0xffff) | (point.Y << 16)) : new IntPtr(-1);
            SendWidgetMenuMessage(list.Handle, 0x007B, list.Handle, position);
            Application.DoEvents();
        };
        void Click(ContextMenuStrip menu, string name)
        {
            menu.Items[name].PerformClick();
            menu.Close();
            Application.DoEvents();
        }

        open(top, 0);
        Check(topMenu.Visible && top.SelectedItem == first && !topMenu.Items["Paste"].Enabled &&
            !topMenu.Items["MoveUp"].Enabled && !topMenu.Items["MoveTop"].Enabled,
            "right-click targets the clicked row, with empty Paste and upper movement disabled");
        open(top, 1);
        Click(topMenu, "MoveTop");
        Check(top.Items[0] == copied && top.SelectedItem == copied, "Move to top reorders and selects the widget");
        open(top, null);
        Click(topMenu, "MoveDown");
        Check(top.Items[1] == copied, "keyboard menu uses the selection and moves it down");
        open(top, 1);
        Click(topMenu, "MoveUp");
        Check(top.Items[0] == copied, "Move up sends the row one position higher");
        open(top, 0);
        Click(topMenu, "MoveBottom");
        Check(top.Items[2] == copied && top.Items[1] == third, "Move to bottom preserves the other widgets' order");
        open(top, 2);
        Check(!topMenu.Items["MoveDown"].Enabled && !topMenu.Items["MoveBottom"].Enabled, "bottom row cannot move farther down");
        Click(topMenu, "Copy");
        copied.Label = "Edited after copying";
        open(bottom, 1);
        Check(bottomMenu.Visible && bottomMenu.Items["Paste"].Enabled, "copied widget enables Paste in the other list");
        if (capture)
        {
            using (var bitmap = new Bitmap(bottomMenu.Width, bottomMenu.Height))
            {
                bottomMenu.DrawToBitmap(bitmap, bottomMenu.ClientRectangle);
                bitmap.Save(Path.Combine(AppContext.BaseDirectory, "settings-widget-menu.png"));
            }
        }
        Click(bottomMenu, "Paste");
        var paste = (LineItem)bottom.Items[1];
        Check(paste != copied && paste.Label == "Memory" && paste.PriorityRank == 8 && paste.DurationMs == 5800 &&
            paste.FontFamily == "Courier New" && paste.VisibilityThreshold == 25 &&
            ((LineItem)bottom.Items[2]).Type == "Date" && bottom.SelectedItem == paste,
            "Paste inserts an independent snapshot before the pointed row");
        open(bottom, -1);
        Click(bottomMenu, "Paste");
        var tail = (LineItem)bottom.Items[3];
        Check(tail != paste && tail.Label == paste.Label && bottom.SelectedItem == tail, "Paste on blank space appends a new copy");
        open(bottom, 1);
        Click(bottomMenu, "Hide");
        open(bottom, 1);
        Check(paste.Hidden && !tail.Hidden && bottomMenu.Items["Hide"].Text == "Unhide", "hidden state is per widget and toggles the menu to Unhide");
        Click(bottomMenu, "Hide");
        Check(!paste.Hidden && paste.PriorityRank == 8, "Unhide retains the widget settings");
        open(bottom, 1);
        Click(bottomMenu, "Hide");
        open(top, 1);
        Click(topMenu, "Remove");
        Check(!top.Items.Contains(third) && top.Items.Count == 2, "Remove deletes the clicked row");
        addTop.PerformClick();
        Check(addTop.ContextMenuStrip.Visible, "plus opens the widget picker");
        Click(addTop.ContextMenuStrip, "AddWeather");
        Check(top.SelectedIndex == 2 && top.SelectedItem is LineItem weather && weather.Type == "Weather" && weather.DurationMs == 5000,
            "picker adds and selects a widget with its usual defaults");
        formType.GetMethod("SaveSettings", fields).Invoke(form, null);
        var saved = SettingsManager.LoadFrom(path);
        Check(saved.BottomLineItems[1].Hidden && !saved.BottomLineItems[3].Hidden && saved.TopLineItems[2].Type == "Weather",
            "hidden state, copies, and added widgets survive save and reload");
        top.ClearSelected();
        bottom.ClearSelected();
        Application.DoEvents();
        foreach (var button in new[] { addTop, addBottom })
        {
            using (var glyph = new Bitmap(button.ClientSize.Width, button.ClientSize.Height))
            {
                button.DrawToBitmap(glyph, button.ClientRectangle);
                var visibleRows = Enumerable.Range(0, glyph.Height)
                    .Where(y => Enumerable.Range(glyph.Width / 2 - 1, 2)
                        .All(x => glyph.GetPixel(x, y).ToArgb() == button.ForeColor.ToArgb())).ToArray();
                Check(visibleRows.Length >= 10 &&
                    Math.Abs((visibleRows.First() + visibleRows.Last() + 1) / 2f - glyph.Height / 2f) <= .5f,
                    "larger add glyph is vertically centered: " + button.Name);
            }
        }
        if (capture)
        {
            var display = top.Parent.Parent;
            using (var bitmap = new Bitmap(display.Width, bottom.Parent.Bottom + 6))
            {
                display.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(AppContext.BaseDirectory, "settings-widget-lists.png"));
            }
        }
    }

    private static void CheckWidgetWheelRouting(Form form, Type formType)
    {
        var list = (ListBox)form.Controls.Find("TopWidgetList", true).Single();
        var bottom = (ListBox)form.Controls.Find("BottomWidgetList", true).Single();
        var viewport = form.Controls.Find("DisplayViewport", true).Single();
        var pageControl = form.Controls.Find("AddTopWidget", true).Single();
        Control ancestor = list.Parent;
        while (!(ancestor is TabControl)) ancestor = ancestor.Parent;
        var tab = (TabControl)ancestor;
        var originalTop = list.Items.Cast<LineItem>().ToArray();
        var originalBottom = bottom.Items.Cast<LineItem>().ToArray();
        bool originalTopMost = form.TopMost;
        int outerDelta = 0;
        var filter = (IMessageFilter)Activator.CreateInstance(formType.GetNestedType("ScrollWheelFilter", BindingFlags.NonPublic),
            new object[] { viewport, new Action<int>(d => outerDelta += d) });
        bool Wheel(Control from, Control hovered, int delta)
        {
            var p = hovered.PointToScreen(new Point(hovered.Width / 2, hovered.Height / 2));
            var args = Message.Create(from.Handle, 0x020A, new IntPtr(delta << 16),
                new IntPtr((p.X & 0xffff) | (p.Y << 16)));
            return filter.PreFilterMessage(ref args);
        }
        try
        {
            form.TopMost = true;
            form.BringToFront();
            form.Activate();
            list.Items.Clear();
            bottom.Items.Clear();
            var many = Enumerable.Range(0, 24).Select(i => new LineItem { Type = "Text", Label = "Row " + i }).ToArray();
            list.Items.AddRange(many);
            bottom.Items.AddRange(many.Select(item => item.Copy()).ToArray());
            list.TopIndex = bottom.TopIndex = 0;
            pageControl.Focus();
            Application.DoEvents();
            int selection = list.SelectedIndex;
            bool hadFocus = pageControl.Focused;
            bool topRouted = Wheel(pageControl, list, -120);
            bool bottomRouted = Wheel(pageControl, bottom, -120);
            Check(topRouted && bottomRouted && outerDelta == 0 &&
                (SystemInformation.MouseWheelScrollLines == 0 || (list.TopIndex > 0 && bottom.TopIndex > 0)) &&
                list.SelectedIndex == selection && pageControl.Focused == hadFocus,
                $"wheel scrolls each hovered widget list without moving focus, selection, or the page " +
                $"(routed {topRouted}/{bottomRouted}, top indices {list.TopIndex}/{bottom.TopIndex}, outer {outerDelta})");
            Check(Wheel(pageControl, pageControl, -120) && outerDelta == -120,
                "wheel outside the list still scrolls the Display page");
            Check(!Wheel(list, list, -120) && outerDelta == -120,
                "wheel already delivered to the list remains native");
            list.Items.Clear();
            list.Items.Add(many[0]);
            Check(Wheel(pageControl, list, -120) && outerDelta == -120,
                "a short widget list does not pass wheel input to the page");
            tab.SelectedIndex = 1;
            Check(!Wheel(pageControl, pageControl, -120) && outerDelta == -120,
                "hidden Display tab does not intercept the wheel");
        }
        finally
        {
            form.TopMost = originalTopMost;
            list.Items.Clear();
            bottom.Items.Clear();
            list.Items.AddRange(originalTop);
            bottom.Items.AddRange(originalBottom);
            tab.SelectedIndex = 0;
        }
    }

    private static void CheckNewTemperatureLimits()
    {
        foreach (var (name, limit) in new[] {
            ("Intel(R) Core(TM) Ultra 7 Processor 270K Plus", 105), ("Intel Core Ultra 5 250KF Plus", 105),
            ("Intel® Core™ Ultra 9 285H", 110), ("Intel Core Ultra 9 285HX", 105),
            ("Intel Core Ultra 9 288V", 100), ("Intel Core Ultra X9 388H", 100),
            ("Intel Core Ultra 7 155H", 110), ("13th Gen Intel Core i9-13900K @ 3.00GHz", 100),
            ("AMD Ryzen 9 9950X3D2 Dual Edition 16-Core Processor", 95),
            ("AMD Ryzen 7 9850X3D 8-Core Processor", 95), ("AMD Ryzen 9 9955HX3D", 100),
            ("AMD Ryzen AI 9 HX 475 w/ Radeon 890M", 100), ("AMD Ryzen AI Max+ 395 with Radeon 8060S", 100),
            ("AMD Ryzen Threadripper PRO 9995WX 96-Core Processor", 95),
            ("AMD Ryzen 5 7600X3D 6-Core Processor", 89) })
        {
            Check(IsCpuNameInDictionary(name) && GetCpuMaximum(name) == limit &&
                GetCpuAutoCritical(name) < limit && GetCpuAutoWarning(name) < GetCpuAutoCritical(name),
                "model-specific CPU maximum and alert margins: " + name);
        }
        foreach (var (name, limit) in new[] {
            ("GeForce RTX 5050", 87), ("NVIDIA GeForce RTX 5060", 89), ("NVIDIA GeForce RTX 5060 Ti 16 GB", 87),
            ("NVIDIA® GeForce RTX 5060 Ti 8GB", 87), ("NVIDIA GeForce RTX 5070", 85),
            ("NVIDIA GeForce RTX 5090 D V2", 90), ("NVIDIA GeForce RTX 4090 D", 90), ("NVIDIA GeForce RTX 3050 6 GB", 93) })
            Check(IsGpuNameInDictionary(name) && GetGpuMaximum(name) == limit &&
                GetGpuAutoCritical(name) == limit - 4 && GetGpuAutoWarning(name) == limit - 13,
                "desktop GPU maximum and alert margins: " + name);
        Check(!IsGpuNameInDictionary("NVIDIA GeForce RTX 5050 Laptop GPU") &&
            !IsGpuNameInDictionary("AMD Radeon RX 9070 XT") &&
            !IsCpuNameInDictionary("Intel Core Ultra X9 999H"), "uncatalogued hardware does not inherit a different model's limit");
        var explicitLimits = GetWidgetTemperatureThresholds(
            new LineItem { Type = "GpuTemperature", WarnTemp = "65", CritTemp = "76" }, "NVIDIA GeForce RTX 5050");
        Check(explicitLimits.Warning == 65 && explicitLimits.Critical == 76, "automatic profiles do not override custom temperature limits");
    }

    private static void CheckHiddenWidgetRotation()
    {
        var hiddenMedia = new LineItem { Type = "NowPlaying", Hidden = true, PriorityMode = (int)WidgetPriority.MediaPlaying,
            PriorityRank = 10, DurationMs = 12000 };
        var hiddenGpu = new LineItem { Type = "GpuTemperature", Hidden = true, PriorityMode = (int)WidgetPriority.Threshold,
            PriorityRank = 10 };
        var top = new LineItem { Type = "Date", DurationMs = 3000 };
        var bottom = new LineItem { Type = "Time", DurationMs = 3000 };
        var fallback = new LineItem { Type = "Text", DurationMs = 3000 };
        var config = new AppSettings { TopLineItems = new List<LineItem> { hiddenMedia, top, fallback },
            BottomLineItems = new List<LineItem> { hiddenGpu, bottom } };
        int hiddenCalls = 0;
        WidgetRule Rule(LineItem item)
        {
            if (item.Hidden) { hiddenCalls++; return new WidgetRule(true, 10); }
            return EvaluateWidgetRule(item, default, true, true);
        }
        foreach (bool paired in new[] { true, false })
        {
            config.RotateLinesTogether = paired;
            var clock = new DisplayRotation();
            clock.Update(config, 0, Rule);
            Check(clock.Top.Item == top && clock.Bottom.Item == bottom && hiddenCalls == 0,
                "hidden widgets never enter priority or rotation (paired=" + paired + ")");
            clock.Update(config, 3000, Rule);
            Check(clock.Top.Item == fallback && clock.Bottom.Item == bottom,
                "only enabled peer stays visible when the other widget is hidden (paired=" + paired + ")");
        }
        Check(!EvaluateWidgetRule(hiddenMedia, default, true, true).Visible &&
            EvaluateWidgetRule(hiddenMedia, default, true, true).Priority == 0,
            "hidden Now Playing cannot claim priority even with active media");
        hiddenMedia.Hidden = false;
        var restored = new DisplayRotation();
        restored.Update(config, 0, Rule);
        Check(restored.Top.Item == hiddenMedia, "unhiding restores the previously configured takeover");
    }

    private static void CheckDisplayRules()
    {
        var a = new LineItem { Type = "NowPlaying", DurationMs = 10000 };
        var b = new LineItem { Type = "NowPlaying", DurationMs = 10000 };
        var cpu = new LineItem { Type = "CpuTemperature", DurationMs = 6000 };
        var gpu = new LineItem { Type = "GpuTemperature", DurationMs = 6000 };
        var config = new AppSettings
        {
            RotateLinesTogether = false,
            TopLineItems = new List<LineItem> { a, cpu },
            BottomLineItems = new List<LineItem> { b, gpu }
        };
        var clock = new DisplayRotation();
        bool media = true, otherMedia = true;
        int cpuRank = 0, upperMediaRank = 0;
        bool showCpu = true, showGpu = true;
        WidgetRule Rule(LineItem i) => i == a ? new WidgetRule(media, upperMediaRank, !media)
            : i == b ? new WidgetRule(otherMedia, skipPair: !otherMedia)
            : i == cpu ? new WidgetRule(showCpu, cpuRank) : new WidgetRule(showGpu);
        void At(long ms, LineItem first, LineItem second, string description)
        {
            clock.Update(config, ms, Rule, "song");
            Check(clock.Top.Item == first && clock.Bottom.Item == second, description);
        }
        At(0, a, b, "equal schedules start on the first widget");
        At(8000, a, b, "scroll completion does not advance either line");
        string tail = (string)Call("ApplyNativeOledTextScroll", "long performer and timing information 01:01", 15, clock.Top.Elapsed(8000), clock.Top.Duration);
        Check(tail.EndsWith("01:01") && clock.Bottom.Elapsed(8000) == 8000, "long Now Playing finishes by 80 percent on common clock");
        At(9999, a, b, "both media rows visible to the boundary");
        At(10000, cpu, gpu, "10-second media advances together");
        At(16000, a, b, "6-second temperature advances together");
        At(65100, a, b, "large wall-clock gap does not accumulate timer drift");
        Check(clock.Top.Started == 64000 && clock.Bottom.Started == 64000, "late frame preserves both deadlines");
        media = false;
        At(66000, cpu, b, "independent skip affects only its own row");
        media = true;
        At(66500, cpu, gpu, "matching schedules realign after skip");

        config.RotateLinesTogether = true;
        b.DurationMs = 12000;
        clock = new DisplayRotation();
        At(0, a, b, "paired mode starts both rows together");
        At(10000, a, b, "paired mode uses the longer duration");
        At(12000, cpu, gpu, "paired transition interrupts both old scroll windows");
        otherMedia = false;
        At(18000, cpu, gpu, "unavailable paired media skips entire row");
        showCpu = false;
        At(18100, null, gpu, "numeric visibility skips only one side of a pair");
        showGpu = false;
        At(18200, null, null, "all hidden clears both lines");
        showGpu = showCpu = otherMedia = true;
        config.BottomLineItems.Remove(gpu);
        clock = new DisplayRotation();
        At(0, a, b, "unequal lists still pair first row");
        At(12000, cpu, b, "a sole bottom widget stays visible across unmatched paired rows");

        config.BottomLineItems.Add(gpu);
        clock = new DisplayRotation();
        upperMediaRank = 6;
        At(0, a, b, "media priority can take over its line");
        At(8000, a, b, "sole priority reaches the tail before its deadline");
        Check(clock.Top.Elapsed(8000) == 8000, "priority scroll uses widget duration");
        At(10000, a, b, "sole priority stays on its line when scrolling restarts");
        Check(clock.Top.Elapsed(10000) == 0, "sole priority loops at its deadline");
        At(13000, a, gpu, "paired peer rotates under sole priority");
        Check(clock.Top.Elapsed(13000) == 3000, "sole priority continues scrolling after the peer changes");
        clock.Update(config, 13100, Rule, "new song");
        Check(clock.Top.Started == 13100, "new priority song restarts text without restarting pair");
        cpuRank = 7;
        At(14000, cpu, gpu, "higher rank preempts lower rank");
        upperMediaRank = 7;
        At(14100, cpu, gpu, "joining tied contender preserves unchanged winner");
        At(20000, a, b, "equal priority rotates only tied widgets");
        upperMediaRank = cpuRank = 0;
        At(24500, a, b, "priority release rejoins current paired view");
        config.RotateLinesTogether = false;
        upperMediaRank = 5;
        At(25000, a, b, "independent lines support priority");
        At(37200, a, gpu, "independent peer still rotates");
        upperMediaRank = 0;
        At(37300, cpu, gpu, "independent line resumes normal timeline");

        var rule = new LineItem { Type = "RamUsage", VisibilityThreshold = 80, PriorityMode = (int)WidgetPriority.Threshold, PriorityRank = 6, UseRamMb = true };
        Check(!EvaluateWidgetRule(rule, new WidgetReading(79.9), false, false).Visible, "RAM percentage below threshold is hidden even in MB mode");
        Check(EvaluateWidgetRule(rule, new WidgetReading(80), false, false).Priority == 6, "exact RAM threshold qualifies");
        Check(!EvaluateWidgetRule(rule, new WidgetReading(null), false, false).Visible, "missing RAM is hidden");
        rule.VisibilityThreshold = 91;
        Check(EvaluateWidgetRule(rule, new WidgetReading(90), false, false).Priority == 0, "hidden widget cannot win priority");
        cpu.PriorityMode = (int)WidgetPriority.Threshold;
        Check(EvaluateWidgetRule(cpu, new WidgetReading(77, 77, 90), false, false).Priority == 1 &&
            EvaluateWidgetRule(cpu, new WidgetReading(77, 82, 93), false, false).Priority == 0, "same temperature uses selected device's warning value");
        Check(EvaluateWidgetRule(cpu, new WidgetReading(0, 0, 90), false, false).Priority == 0, "invalid temperature cannot trigger");
        cpu.PriorityMode = (int)WidgetPriority.AtOrBelow;
        cpu.PriorityThreshold = 75;
        Check(EvaluateWidgetRule(cpu, new WidgetReading(75), false, false).Priority == 1 &&
            EvaluateWidgetRule(cpu, new WidgetReading(75.1), false, false).Priority == 0 &&
            EvaluateWidgetRule(cpu, new WidgetReading(null), false, false).Priority == 0, "below target includes the boundary but not missing readings");
        var ordered = Enumerable.Range(1, 12).Select(i => new LineItem { Type = "RamUsage", PriorityMode = 1, PriorityRank = i }).ToList();
        var order = new GGSystemMonitor.PriorityOrder(ordered);
        Check(order.Level(ordered[11]) == 1 && order.Level(ordered[10]) == 2 &&
            order.Move(ordered[10], -1) && order.Levels[0].Count == 2, "one move up joins the higher level");
        Check(order.Move(ordered[10], -1) && order.Level(ordered[10]) == 1 && order.Level(ordered[11]) == 2,
            "a second move up splits off above the previously tied level");
        order.Apply();
        Check(EvaluateWidgetRule(ordered[10], new WidgetReading(99), false, false).Priority >
            EvaluateWidgetRule(ordered[11], new WidgetReading(99), false, false).Priority, "more than ten priority levels retain their ordering at runtime");
        a.PriorityMode = (int)WidgetPriority.MediaPlaying;
        Check(EvaluateWidgetRule(a, default, true, false).Priority == 0, "paused media does not claim Playing-only priority");
        Check(EvaluateWidgetRule(a, default, true, true).Priority == 1, "playing media activates priority");
        a.PriorityMode = (int)WidgetPriority.MediaPresent;
        var paused = EvaluateWidgetRule(a, default, true, false);
        Check(!paused.Visible && paused.Priority == 1 && paused.SkipPair, "paused media skips normal rotation but honors explicit Media-present priority");
        a.SkipIfNotPlaying = false;
        Check(EvaluateWidgetRule(a, default, true, false).Visible &&
            EvaluateWidgetRule(a, default, false, false).Visible, "Always includes paused and missing media in normal rotation");
        a.SkipIfNotPlaying = true;
        Check(EvaluateWidgetRule(a, default, false, true).SkipPair, "filtered or missing media is skipped");

        var old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"TopLineItems\":[\"CpuTemperature\"]}");
        Check(old.RotateLinesTogether && old.TopLineItems[0].FontFamily == "" && old.TopLineItems[0].FontSizeStep == 0 &&
             old.TopLineItems[0].PriorityMode == 0 && old.TopLineItems[0].VisibilityThreshold == null, "unset rotation and font settings inherit the new defaults");
        Check(!System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"RotateLinesTogether\":false}").RotateLinesTogether,
            "an explicit existing independent-rotation choice is preserved");
        using (var courier = new Font("Courier New", 14, GraphicsUnit.Pixel))
        using (var small = new Font("Courier New", 13, GraphicsUnit.Pixel))
        using (var tiny = new Font("Consolas", 12, GraphicsUnit.Pixel))
        {
            var content = "WWWWWWWWWWWWWWWWWW trailing glyphZ";
            int textWidth = (int)Call("BitmapTextWidth", "\U0001F845");
            string head = (string)Call("ApplyBitmapScroll", content, courier, textWidth, 0, 5000, false);
            string end = (string)Call("ApplyBitmapScroll", content, courier, textWidth, 4000, 5000, false);
            string smallHead = (string)Call("ApplyBitmapScroll", content, small, textWidth, 0, 5000, false);
            Check(content.StartsWith(head) && end.EndsWith("glyphZ") && (float)Call("GetTextAdvanceWidth", courier, end) <= textWidth,
                "Courier scroll fits complete last glyph by 80 percent");
            Check(smallHead.Length > head.Length && (float)Call("GetTextAdvanceWidth", tiny, content) !=
                (float)Call("GetTextAdvanceWidth", courier, content), "font size and family have distinct metric caches");
            var bitmap = RenderBitmapForTest(head, smallHead, courier, tiny, "\U0001F845", "");
            bool Lit(int x, int y) => (bitmap[(y * 128 + x) / 8] & (1 << (7 - x % 8))) != 0;
            Check(Lit(122, 3) && Lit(127, 8) && !Lit(127, 3), "native Caps Lock arrow is pinned to the OLED border in mixed-font mode");
            Check(Enumerable.Range(128 * 20 / 8, 128 * 20 / 8).Any(i => bitmap[i] != 0), "second line is rendered in the same mixed-font frame");
        }
    }

    private static void CheckIndependentPriorityPeer()
    {
        var media = new LineItem { Type = "NowPlaying", DurationMs = 5000, PriorityMode = (int)WidgetPriority.MediaPlaying };
        var otherTop = new LineItem { Type = "RamUsage", DurationMs = 5000 };
        var gpu = new LineItem { Type = "GpuTemperature", DurationMs = 2600 };
        var config = new AppSettings
        {
            RotateLinesTogether = true,
            TopLineItems = new List<LineItem> { media, otherTop },
            BottomLineItems = new List<LineItem> { gpu }
        };
        bool playing = true;
        WidgetRule Rule(LineItem item) => EvaluateWidgetRule(item, default, playing, playing);
        var rotation = new DisplayRotation();
        rotation.Update(config, 0, Rule, "track");
        Check(rotation.Top.Item == media && rotation.Bottom.Item == gpu, "priority media starts with the single GPU widget visible");
        rotation.Update(config, 5500, Rule, "track");
        Check(rotation.Top.Item == media && rotation.Bottom.Item == gpu, "single GPU survives a longer media takeover and an unmatched pair");
        playing = false;
        rotation.Update(config, 11200, Rule);
        Check(rotation.Top.Item == otherTop && rotation.Bottom.Item == gpu, "GPU stays visible when media takeover ends and its pair is skipped");
        var time = new LineItem { Type = "Time", DurationMs = 2600 };
        config.BottomLineItems.Add(time);
        playing = true;
        rotation = new DisplayRotation();
        rotation.Update(config, 0, Rule, "track");
        rotation.Update(config, 2800, Rule, "track");
        Check(rotation.Top.Item == media && rotation.Bottom.Item == time,
            "the peer follows its own rotation duration while media has priority");

        media = new LineItem { Type = "NowPlaying", DurationMs = 5000, PriorityMode = (int)WidgetPriority.MediaPlaying };
        config = new AppSettings
        {
            RotateLinesTogether = true,
            TopLineItems = new List<LineItem> { gpu },
            BottomLineItems = new List<LineItem> { media, otherTop }
        };
        rotation = new DisplayRotation();
        rotation.Update(config, 0, Rule, "track");
        rotation.Update(config, 5500, Rule, "track");
        Check(rotation.Top.Item == gpu && rotation.Bottom.Item == media, "priority on the bottom likewise cannot blank a single top widget");
    }

    private static void CheckStableMixedFontBinding()
    {
        var custom = new LineItem { Type = "Text", Label = "TOP SAMPLE", FontFamily = "Consolas", DurationMs = 3000 };
        var native = new LineItem { Type = "Text", Label = "TOP SAMPLE", DurationMs = 3000 };
        var gpu = new LineItem { Type = "GpuTemperature", Label = "GPU: 45 C" };
        var config = new AppSettings
        {
            TopLineItems = new List<LineItem> { custom, native },
            BottomLineItems = new List<LineItem> { gpu }
        };
        Call("ApplyDisplaySettings", config);
        var rotation = (DisplayRotation)P.GetField("Rotation", Flags).GetValue(null);
        var preferred = (Font)Call("GetWidgetFont", custom);
        var fallback = (Font)Call("NativeBitmapFont");
        Check(preferred.Name == "Consolas" && Call("GetWidgetFont", gpu) == null && !preferred.Equals(fallback),
            "a default row uses a fixed fallback distinct even from a custom selection of the same family");
        var bind = CaptureRequest(() => Call("BindEvent"));
        Check((string)bind["handlers"][0]["device-type"] == "screened-128x40", "mixed preset binds one bitmap screen");
        rotation.Update(config, 0, item => new WidgetRule(true));
        var first = CaptureRequest(() => Call("BuildAndSendDisplay", 0L));
        rotation.Update(config, 3000, item => new WidgetRule(true));
        var second = CaptureRequest(() => Call("BuildAndSendDisplay", 3000L));
        var firstFrame = first["data"]["frame"]["image-data-128x40"].Values<int>().ToArray();
        var secondFrame = second["data"]["frame"]["image-data-128x40"].Values<int>().ToArray();
        Check(firstFrame.Skip(320).SequenceEqual(secondFrame.Skip(320)) &&
            !firstFrame.Take(320).SequenceEqual(secondFrame.Take(320)), "switching the top font does not alter the bottom half of the frame");
        var third = CaptureRequest(() => Call("BuildAndSendDisplay", 3100L));
        Check(third["data"]["frame"]["image-data-128x40"].Values<int>().SequenceEqual(secondFrame),
            "an unchanged bitmap frame remains identical and still sends an event");
        Call("ApplyDisplaySettings", config);
        var sameMode = CaptureRequest(() => Call("BuildAndSendDisplay", 3200L));
        Check(sameMode["data"]["frame"]["image-data-128x40"] != null, "saving with the same screen mode does not rebind");

        var nativeConfig = new AppSettings { TopLineItems = new List<LineItem> { native }, BottomLineItems = new List<LineItem> { gpu } };
        Call("ApplyDisplaySettings", nativeConfig);
        Check(!(bool)P.GetField("_bindingReady", Flags).GetValue(null), "changing actual output mode invalidates the binding");
        var nativeBind = CaptureRequest(() => Call("BindEvent"));
        Check((string)nativeBind["handlers"][0]["device-type"] == "keyboard", "all-native presets keep the real keyboard text mode");
        rotation.Update(nativeConfig, 0, item => new WidgetRule(true));
        var nativeFrame = CaptureRequest(() => Call("BuildAndSendDisplay", 0L));
        Check((string)nativeFrame["data"]["frame"]["text_line_1"] == "TOP SAMPLE" &&
            (string)nativeFrame["data"]["frame"]["text_line_2"] == "GPU: 45 C", "native frame retains its original text path and content");
        nativeConfig.OledFont = "Courier New";
        native.FontFamily = gpu.FontFamily = NativeWidgetFontId;
        Call("ApplyDisplaySettings", nativeConfig);
        Check(Call("GetWidgetFont", gpu) == null && !(bool)P.GetField("_bitmapFrame", Flags).GetValue(null),
            "explicit native widgets can retain true native text despite a custom General font");
        native.FontFamily = "";
        Call("ApplyDisplaySettings", nativeConfig);
        Check(((Font)Call("GetWidgetFont", native)).Name == "Courier New" && Call("GetWidgetFont", gpu) == null &&
            (bool)P.GetField("_bitmapFrame", Flags).GetValue(null), "inherited font and explicit native remain independent");
    }

    private static void CheckRamPriorityAndGpuAlerts()
    {
        var ram = new LineItem { Type = "RamUsage", PriorityMode = (int)WidgetPriority.Threshold,
            VisibilityThreshold = 40, PriorityThreshold = 8192, RamPriorityUnit = (int)RamTargetUnit.Mb };
        var reading = new WidgetReading(50, ramUsedGb: 8);
        Check(EvaluateWidgetRule(ram, reading, false, false).Priority == 1, "RAM MB takeover uses absolute usage and honors percent visibility");
        ram.RamPriorityUnit = (int)RamTargetUnit.Gb;
        ram.PriorityThreshold = 8;
        Check(EvaluateWidgetRule(ram, reading, false, false).Priority == 1 &&
            EvaluateWidgetRule(ram, new WidgetReading(50, ramUsedGb: 7.9), false, false).Priority == 0,
            "RAM GB takes over at the exact configured boundary");
        Check(!EvaluateWidgetRule(ram, new WidgetReading(39, ramUsedGb: 9), false, false).Visible,
            "RAM absolute takeover can still be excluded by its percent visibility rule");
        Check(EvaluateWidgetRule(ram, new WidgetReading(70), false, false).Priority == 0,
            "RAM absolute threshold is not activated by a missing memory reading");

        var gpu = new LineItem { Type = "GpuTemperature", DurationMs = 3000, GpuPasteGapTemp = 15 };
        var peer = new LineItem { Type = "Text", Label = "Other line" };
        var config = new AppSettings { TopLineItems = new List<LineItem> { gpu }, BottomLineItems = new List<LineItem> { peer } };
        var clock = new DisplayRotation();
        WidgetRule Rule(LineItem item) => new WidgetRule(true, item == gpu ? 3 : 0);
        clock.Update(config, 0, Rule);
        var alert = new GpuThermalAlert(gpu);
        for (int i = 0; i < 11; i++) alert.Observe(65, 88, 0);
        Check(!alert.Active, "brief hotspot gap does not immediately trip the warning");
        alert.Observe(65, 88, 0);
        var alerts = new Dictionary<LineItem, GpuThermalAlert> { [gpu] = alert };
        var selected = SelectThermalAlerts(clock.Top.Item, clock.Bottom.Item, alerts);
        Check(selected.Top == alert && selected.Bottom == null, "GPU priority keeps the hotspot sequence on its own line");
        Check(alert.Frame(0, 3000).Text.Contains("Thermal Paste") && alert.Frame(3000, 3000).Text.StartsWith("GPU:") &&
            alert.Frame(6000, 3000).Text.StartsWith("HOTSPOT:") && alert.Frame(9000, 3000).Text.Contains("Thermal Paste"),
            "GPU warning rotates through paste warning, core, and hotspot without resetting at the scroll boundary");
        clock.Update(config, 6300, Rule);
        Check(clock.Top.Item == gpu && clock.Bottom.Item == peer && clock.Top.Elapsed(6300) == 300,
            "non-media priority also loops without dropping its priority");
        var topFrame = (string)Call("BuildWidgetLine", clock.Top, 6300L, null, true, null, selected.Top);
        var peerFrame = (string)Call("BuildWidgetLine", clock.Bottom, 6300L, null, false, null, selected.Bottom);
        Check(topFrame.StartsWith("HOTSPOT:") && peerFrame == "Other line", "rendered hotspot leaves the peer's content untouched");
        alert.Observe(65, null, 6500);
        Check(!alert.Active && SelectThermalAlerts(gpu, peer, alerts).Top == null, "missing hotspot does not leave a stale alert on screen");
    }

    private static void CheckWeatherVisibility()
    {
        var weather = new LineItem { Type = "Weather", WeatherLocation = "Test weather", VisibilityThreshold = 0 };
        Check(EvaluateWidgetRule(weather, new WidgetReading(0), false, false).Visible &&
            !EvaluateWidgetRule(weather, new WidgetReading(-1), false, false).Visible,
            "Weather at-or-above includes zero but excludes colder readings");
        weather.VisibilityAtOrBelow = true;
        Check(EvaluateWidgetRule(weather, new WidgetReading(0), false, false).Visible &&
            EvaluateWidgetRule(weather, new WidgetReading(-1), false, false).Visible &&
            !EvaluateWidgetRule(weather, new WidgetReading(1), false, false).Visible &&
            !EvaluateWidgetRule(weather, new WidgetReading(double.NaN), false, false).Visible,
            "Weather at-or-below includes negative temperatures and rejects invalid data");
        var dictionary = (System.Collections.IDictionary)P.GetField("_weatherByLocation", Flags).GetValue(null);
        var dataType = P.GetNestedType("WeatherData", BindingFlags.NonPublic);
        var data = Activator.CreateInstance(dataType, true);
        dataType.GetField("TempC").SetValue(data, -5d);
        dictionary[weather.WeatherLocation] = data;
        try
        {
            Check(((WidgetRule)Call("GetLiveRule", weather)).Visible, "Weather rule uses the reading for its configured location");
            weather.WeatherLocation = "Unavailable test weather";
            Check(!((WidgetRule)Call("GetLiveRule", weather)).Visible, "missing weather cannot satisfy a threshold");
            weather.VisibilityThreshold = null;
            Check(((WidgetRule)Call("GetLiveRule", weather)).Visible, "Always weather mode still shows its loading text");
        }
        finally { dictionary.Remove("Test weather"); }
    }

    private static void CheckNativePixelFont()
    {
        const int width = 128, height = 40;
        const int stride = width / 8, halfFrame = height * stride / 2;
        const string top = "GPU: 57.8°C", bottom = "Aa09 Ii1l % @";
        var dateGlyph = NativeOledPixelFont.FindGlyph("\U0001f4c5");
        Check(NativeOledPixelFont.FindGlyph("°") != null &&
            NativeOledPixelFont.FindGlyph("e\u0301") == NativeOledPixelFont.FindGlyph("é"),
            "embedded native atlas includes Celsius and both forms of accented letters");
        Check(dateGlyph != null && NativeOledPixelFont.GetWidth(dateGlyph) == 16 &&
            NativeOledPixelFont.GetWidth(NativeOledPixelFont.FindGlyph("♫")) == 8,
            "native widget symbols use their actual GG glyph widths");
        foreach (var (origin, right) in new[] { (2, width), (114, 126) })
        {
            var packed = new byte[width * height / 8];
            NativeOledPixelFont.Blit(packed, stride, origin, 2, dateGlyph, right);
            bool pixelsMatch = true;
            for (int y = 0; y < 16; y++)
            for (int x = 0; x < width; x++)
            {
                int gx = x - origin;
                bool expected = gx >= 0 && gx < 16 && x < right &&
                    (dateGlyph[y * 2 + gx / 8] & (128 >> (gx % 8))) != 0;
                pixelsMatch &= ((packed[(2 + y) * stride + x / 8] & (128 >> (x % 8))) != 0) == expected;
            }
            Check(pixelsMatch, "native double-width glyph blits intact or clips at the right edge: " + right);
        }
        int cols = (int)Call("NativeBitmapTextCols", "");
        string prefix = new string('X', 14);
        string wideContent = prefix + "⏰A";
        Check((string)Call("ApplyNativeTextScroll", wideContent, cols, 0, 5000, true) == prefix &&
            (string)Call("ApplyNativeTextScroll", wideContent, cols, 4500, 5000, true) == wideContent.Substring(2),
            "native bitmap scroll fits complete double-width glyphs and reaches the end");
        string narrowContent = "♫" + prefix;
        Check((string)Call("ApplyNativeTextScroll", narrowContent, cols, 0, 5000, true) == narrowContent,
            "native bitmap scroll shows all 15 cells when a music symbol is narrow");

        // Visible dots sampled from the user's native OLED photograph, packed at
        // our keyboard origin with the measured 19-pixel line pitch.
        const string topHash = "ACD1F6F9AFC210733B603C1C8468C3CDEA4DFDF73EDB46148A3239FDE9E0EEA3";
        const string bottomHash = "80462B35508C2E1168397E83B0D04E4338782D3BC0338FBBF1AEE5EFD2C4B835";
        using (var sha = SHA256.Create())
        using (var other = new Font("Courier New", 14, GraphicsUnit.Pixel))
        {
            var array = RenderBitmapForTest(top, bottom, null, other, "", "");
            Check(BitConverter.ToString(sha.ComputeHash(array, 0, halfFrame)).Replace("-", "") == topHash,
                "mixed-font native top row matches the photographed GPU temperature sample");
            array = RenderBitmapForTest(top, bottom, other, null, "", "");
            Check(BitConverter.ToString(sha.ComputeHash(array, halfFrame, halfFrame)).Replace("-", "") == bottomHash,
                "mixed-font native bottom row matches the photographed glyph sample and row spacing");
        }
    }

    private static void CheckNativeIndicators()
    {
        const string caps = "\U0001F845", warn = "\u26A0", critical = "\U0001F525";
        Check((int)Call("NativeBitmapTextCols", caps) == 14 && (int)Call("NativeBitmapTextCols", warn) == 14 &&
            (int)Call("NativeBitmapTextCols", critical + caps) == 12 && (int)Call("NativeBitmapTextCols", warn + caps) == 12,
            "native bitmap text leaves room for the full icons without dropping an extra character");
        using (var courier = new Font("Courier New", 14, GraphicsUnit.Pixel))
        using (var arial = new Font("Arial", 17, GraphicsUnit.Pixel))
        {
            foreach (var (top, bottom) in new[] { (caps, warn), (warn + caps, critical), (critical + caps, warn) })
            {
                var native = RenderBitmapForTest("", "", null, null, top, bottom).ToArray();
                var custom = RenderBitmapForTest(new string('W', 40), new string('M', 40), courier, arial, top, bottom);
                bool Matches(int firstRow, int cellTop, string suffix)
                {
                    int cols = (int)Call("BitmapTextWidth", suffix);
                    byte[] temp = suffix.StartsWith(warn, StringComparison.Ordinal) ? NativeOledPixelFont.FindGlyph(warn)
                        : suffix.StartsWith(critical, StringComparison.Ordinal) ? NativeOledPixelFont.FindGlyph(critical) : null;
                    byte[] arrow = suffix.EndsWith(caps, StringComparison.Ordinal) ? NativeOledPixelFont.FindGlyph(caps) : null;
                    int tempOrigin = (suffix.StartsWith(warn, StringComparison.Ordinal) ? 114 : 115) -
                        (arrow == null ? 0 : 16);
                    bool Expected(byte[] rows, int x, int y) => rows != null && y >= 0 && y < 16 && x >= 0 && x < 16 &&
                        (rows[y * 2 + x / 8] & (128 >> x % 8)) != 0;
                    for (int y = firstRow; y < firstRow + 20; y++)
                    for (int x = cols; x < 128; x++)
                    {
                        int i = y * 16 + x / 8, mask = 128 >> x % 8;
                        bool expected = Expected(arrow, x - 115, y - cellTop) ||
                            Expected(temp, x - tempOrigin, y - cellTop);
                        if ((native[i] & mask) != (custom[i] & mask) || ((custom[i] & mask) != 0) != expected)
                            throw new Exception($"Indicator {suffix} at ({x}, {y}): expected on={expected}, native on={(native[i] & mask) != 0}, custom on={(custom[i] & mask) != 0}");
                    }
                    return true;
                }
                Check(Matches(0, 2, top) && Matches(20, 21, bottom),
                    "packed GG indicator glyphs are complete, clear of text, and identical across fonts: " + top + "/" + bottom);
            }
        }
        string nativeSuffix = (string)Call("AddNativeSuffix", "CPU", warn + caps);
        Check(nativeSuffix == "CPU" + new string(' ', 9) + warn + caps,
            "all-native text mode keeps the original indicator characters and spacing");
    }

    private static void CheckInlineWidgetIcons()
    {
        using (var courier = new Font("Courier New", 14, GraphicsUnit.Pixel))
        using (var arial = new Font("Arial", 17, GraphicsUnit.Pixel))
        {
            foreach (var icon in new[] { "♫", "♪", "▶", "⏸", "☀", "☁", "☂", "❄", "⚡", "≡", "*", "·",
                "⏰", "📅", "⚠", "🔥", "🡅" })
            {
                bool weather = icon == "*" || icon == "·";
                var glyph = NativeOledPixelFont.FindGlyph(icon);
                Check(glyph != null, "inline symbol has its original GG glyph: " + icon);
                var expected = new byte[640];
                NativeOledPixelFont.Blit(expected, 16, 0, 2, glyph, 128);
                NativeOledPixelFont.Blit(expected, 16, 0, 21, glyph, 128);
                var bitmap = RenderBitmapForTest(icon, icon, courier, arial, "", "", weather, weather);
                Check(bitmap.SequenceEqual(expected), "same Unifont pixels on both custom-font rows: " + icon);
                Check((float)Call("MeasureOledText", courier, icon, true, weather) == NativeOledPixelFont.GetWidth(glyph) &&
                    (float)Call("MeasureOledText", arial, icon, true, weather) == NativeOledPixelFont.GetWidth(glyph),
                    "scrolling uses the same native width as drawing: " + icon);
            }
            var punctuation = "*· AV";
            var plainWidth = (float)Call("GetTextAdvanceWidth", arial, punctuation);
            Check((float)Call("MeasureOledText", arial, punctuation, true, false) == plainWidth &&
                (float)Call("MeasureOledText", arial, punctuation, true, true) != plainWidth,
                "weather's fallback symbols stay fixed without overriding ordinary punctuation on other widgets");

            var plain = RenderBitmapForTest("AV Test", "", courier, arial, "", "").ToArray();
            var withClock = RenderBitmapForTest("⏰AV Test", "", courier, arial, "", "");
            Check(Enumerable.Range(0, 20).All(y =>
                withClock.Skip(y * 16 + 2).Take(14).SequenceEqual(plain.Skip(y * 16).Take(14))),
                "label text following an icon keeps the selected font and kerning");
            const string calendar = "📅\uFE0F";
            Check(RenderBitmapForTest(calendar, "", courier, null, "", "").ToArray()
                .SequenceEqual(RenderBitmapForTest("📅", "", courier, null, "", "")),
                "variation selectors do not substitute a different font for a widget icon");
            var longLine = "WWWWWWWWWWWWWWW " + calendar;
            const string caps = "\U0001F845";
            int budget = (int)Call("BitmapTextWidth", caps);
            string end = (string)Call("ApplyBitmapScroll", longLine, courier, budget, 4000, 5000, false);
            Check(end.EndsWith(calendar, StringComparison.Ordinal) &&
                (float)Call("MeasureOledText", courier, end, true, false) < budget,
                "a wide calendar icon stays intact at the end of a scroll beside Caps Lock");
            foreach (var (condition, blink, symbol) in new[] {
                ("Snow", 0, "❄"), ("Snow", 2, "*"), ("Rain", 0, "☂"), ("Rain", 2, "·"),
                ("Thunderstorm", 0, "⚡"), ("Thunderstorm", 1, "☁"), ("Fog", 0, "≡"),
                ("Partly cloudy", 0, "☀"), ("Partly cloudy", 2, "☁") })
            {
                Check((string)Call("GetWeatherIcon", condition, blink) == symbol,
                    "weather keeps the existing icon and animation phase: " + condition + " " + blink);
            }
        }
    }
}
