using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GGSystemMonitor
{
    internal partial class SettingsForm
    {
        private static System.Collections.Generic.HashSet<string> InstalledFontNames()
        {
            using (var collection = new System.Drawing.Text.InstalledFontCollection())
            {
                var names = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var family in collection.Families)
                {
                    try { names.Add(family.Name); } finally { family.Dispose(); }
                }
                return names;
            }
        }
        private NotifyIcon _trayIcon;
        private ComboBox _outputProfile;
        private Timer _diagnosticsTimer;
        private sealed class HardwareChoice
        {
            public string Id, Name;
            public override string ToString() => Name;
        }
        private void PopulateHardwareCombo(ComboBox devices, ComboBox sensors, Program.LineItem item)
        {
            devices.Items.Clear();
            bool cpu = ProgramType(item);
            devices.Items.Add(new HardwareChoice { Id = "", Name = cpu ? "Auto (default CPU)" : "Auto (prefers discrete GPU)" });
            try
            {
                var inventory = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "hardware.json")));
                var entries = (inventory["Devices"] as JArray ?? new JArray()).OfType<JObject>().Where(d => cpu ? (string)d["Type"] == "Cpu" : ((string)d["Type"] ?? "").StartsWith("Gpu")).ToArray();
                foreach (var d in entries) devices.Items.Add(new HardwareChoice { Id = (string)d["Id"], Name = (string)d["Name"] });
                devices.SelectedIndex = 0;
                for (int i = 1; i < devices.Items.Count; i++) if (((HardwareChoice)devices.Items[i]).Id == item.HardwareId) devices.SelectedIndex = i;
                if (!string.IsNullOrEmpty(item.HardwareId) && devices.SelectedIndex == 0)
                {
                    devices.Items.Add(new HardwareChoice { Id = item.HardwareId, Name = "Unavailable: " + item.HardwareId });
                    devices.SelectedIndex = devices.Items.Count - 1;
                }
                var resolved = entries.FirstOrDefault(d => (string)d["Id"] == item.HardwareId);
                if (string.IsNullOrEmpty(item.HardwareId))
                {
                    string autoName = (string)inventory[cpu ? "CpuName" : "GpuName"];
                    resolved = entries.FirstOrDefault(d => (string)d["Name"] == autoName);
                }
                sensors.Items.Clear(); sensors.Items.Add("(Auto)");
                foreach (var sensor in resolved?["Sensors"] as JArray ?? new JArray())
                    sensors.Items.Add(new HardwareChoice { Id = (string)sensor["Id"], Name = (string)sensor["Name"] });
                sensors.SelectedIndex = 0;
                for (int i = 1; i < sensors.Items.Count; i++)
                {
                    var sensor = (HardwareChoice)sensors.Items[i];
                    if (sensor.Id == item.SensorOverride || sensor.Name == item.SensorOverride) sensors.SelectedIndex = i;
                }
                if (!string.IsNullOrEmpty(item.SensorOverride) && sensors.SelectedIndex == 0)
                {
                    sensors.Items.Add(new HardwareChoice { Id = item.SensorOverride, Name = "Unavailable: " + item.SensorOverride });
                    sensors.SelectedIndex = sensors.Items.Count - 1;
                }
            }
            catch { devices.SelectedIndex = 0; }
        }
        private static bool ProgramType(Program.LineItem item) => NormType(item) == "cputemperature";
        private void InitializeTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Settings", null, (s, e) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
            menu.Items.Add("Start / Stop Monitoring", null, OnToggleClick);
            menu.Items.Add("Exit Settings", null, (s, e) => Close());
            _trayIcon = new NotifyIcon { Text = "GGSystemMonitor Settings", Icon = Icon, Visible = true, ContextMenuStrip = menu };
            _trayIcon.DoubleClick += (s, e) => { Show(); Activate(); };
            FormClosed += (s, e) => menu.Dispose();
        }
        private TabPage BuildDiagnosticsTab()
        {
            var tab = new TabPage("Diagnostics") { BackColor = BackColor };
            tab.Controls.Add(new Label { Text = "OLED target (experimental devices require owner testing):", AutoSize = true, Location = new Point(10, 12) });
            _outputProfile = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(10, 36), Width = 450 };
            _outputProfile.Items.AddRange(new object[] { "Keyboard (128 x 40)", "Arctis Pro Wireless beta (128 x 48)", "GameDAC beta (128 x 52)" });
            _outputProfile.SelectedIndex = 0;
            _outputProfile.SelectedIndexChanged += (s, e) => EvaluateDirty();
            tab.Controls.Add(_outputProfile);
            var report = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Location = new Point(10, 100), Size = new Size(450, 390) };
            string BuildReport()
            {
                var data = new JObject(new JProperty("AppVersion", Program.VERSION), new JProperty("OS", Environment.OSVersion.ToString()), new JProperty("Process64Bit", Environment.Is64BitProcess));
                foreach (string file in new[] { "hardware.json", "monitor_status.json" })
                    try { data[file] = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file))); } catch { data[file] = "Unavailable / monitor not started"; }
                data["Elevated"] = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                data["PawnIOServicePresent"] = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\PawnIO", "ImagePath", null) != null;
                data["HardwareLibrary"] = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly.GetName().Version.ToString();
                return data.ToString(Formatting.Indented);
            }
            var refresh = new Button { Text = "Refresh", Location = new Point(10, 65), Width = 100 };
            refresh.Click += (s, e) => report.Text = BuildReport();
            var export = new Button { Text = "Export Diagnostics", Location = new Point(120, 65), Width = 160 };
            export.Click += (s, e) =>
            {
                using (var dialog = new SaveFileDialog { Filter = "JSON report|*.json", FileName = "GGSystemMonitor-diagnostics.json" })
                    if (dialog.ShowDialog(this) == DialogResult.OK) try { File.WriteAllText(dialog.FileName, BuildReport()); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export failed"); }
            };
            var test = new Button { Text = "Test pattern", Location = new Point(290, 65), Width = 130 };
            test.Click += (s, e) =>
            {
                try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "test-pattern.request"), DateTime.UtcNow.ToString("o")); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Test request failed"); }
            };
            var driver = new LinkLabel { Text = "Prerequisites are bundled with setup; upstream support", Location = new Point(10, 500), AutoSize = true };
            driver.LinkClicked += (s, e) => Process.Start(new ProcessStartInfo { FileName = "https://github.com/namazso/PawnIO", UseShellExecute = true });
            tab.Controls.AddRange(new Control[] { report, refresh, export, test, driver });
            _diagnosticsTimer = new Timer { Interval = 2000 };
            _diagnosticsTimer.Tick += (s, e) => { if (tab.Visible) report.Text = BuildReport(); };
            _diagnosticsTimer.Start();
            tab.Enter += (s, e) => report.Text = BuildReport();
            return tab;
        }
    }
}
