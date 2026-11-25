using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GGSystemMonitor
{
    internal partial class Program
    {
        private static readonly object DisplayLock = new object();
        private static readonly int CurrentProcessId = GetProcessId();
        private static string _sensorError = "";
        private static readonly Stopwatch SensorClock = Stopwatch.StartNew();
        private static long _lastHardwarePoll = -100000;
        private static readonly object LogLock = new object();
        private static void AppendLog(string text)
        {
            try
            {
                lock (LogLock)
                {
                    string path = Path.Combine(AppContext.BaseDirectory, "GGSystemMonitor.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.WriteAllText(path, "");
                    File.AppendAllText(path, text);
                }
            }
            catch { }
        }
        private static volatile bool _reloadSettings;
        private static DateTime _testPatternUntil;
        internal static string MonitorStopEventName => @"Local\GGSystemMonitor.Stop." + System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
        private static System.Threading.EventWaitHandle _stopSignal;
        private static System.Threading.Mutex _monitorMutex;
        private static bool AcquireMonitorInstance()
        {
            _monitorMutex = new System.Threading.Mutex(true, MonitorStopEventName + ".Mutex", out bool first);
            if (!first) { _monitorMutex.Dispose(); return false; }
            // Permit this user's non-elevated settings process to stop the elevated monitor.
            var security = new System.Security.AccessControl.EventWaitHandleSecurity();
            security.AddAccessRule(new System.Security.AccessControl.EventWaitHandleAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User,
                System.Security.AccessControl.EventWaitHandleRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            _stopSignal = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.ManualReset, MonitorStopEventName, out bool created, security);
            _stopSignal.Reset();
            return true;
        }
        private static bool MonitorStopRequested() => _stopSignal != null && _stopSignal.WaitOne(0);
        private static void ApplyPendingSettings()
        {
            if (!_reloadSettings) return;
            _reloadSettings = false;
            try
            {
                ApplyDisplaySettings(SettingsManager.LoadSettings());
                _weatherRefreshMs = 0;
            }
            catch { _reloadSettings = true; }
        }
        private static void ApplyDisplaySettings(AppSettings loaded)
        {
            string previousDevice = ScreenBindingDevice();
            settings = loaded;
            UpdateOledFont();
            RefreshWidgetFlags();
            if (previousDevice != ScreenBindingDevice()) InvalidateScreenBinding();
        }
        private static void CheckTestPattern()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "test-pattern.request");
            if (!File.Exists(path)) return;
            try { File.Delete(path); _testPatternUntil = DateTime.UtcNow.AddSeconds(3); } catch { }
        }
        private static void RunSettingsWindow()
        {
            string suffix = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
            using (var signal = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, @"Local\GGSystemMonitor.Settings.Activate." + suffix))
            using (var mutex = new System.Threading.Mutex(true, @"Local\GGSystemMonitor.Settings." + suffix, out bool first))
            {
                if (!first) { signal.Set(); return; }
                try
                {
                    System.Windows.Forms.Application.EnableVisualStyles();
                    System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
                    System.Threading.Tasks.Task.Run(() => CheckForUpdates());
                    using (var form = new SettingsForm())
                    using (var timer = new System.Windows.Forms.Timer { Interval = 250 })
                    {
                        timer.Tick += (s, e) => { if (signal.WaitOne(0)) { form.Show(); form.WindowState = System.Windows.Forms.FormWindowState.Normal; form.BringToFront(); form.Activate(); } };
                        timer.Start();
                        System.Windows.Forms.Application.Run(form);
                    }
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
        private static int GetProcessId() { using (var p = Process.GetCurrentProcess()) return p.Id; }
        internal static bool IsValidTemperature(double? value) => value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value) && value.Value > 0 && value.Value < 200;
        private static readonly Regex HardwareNameNoise = new Regex(@"\((?:R|TM)\)|[®™]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HardwareNameSpacing = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly Regex CpuBrandPrefix = new Regex(@"^\d+(?:st|nd|rd|th) Gen\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CpuBrandSuffix = new Regex(
            @"\s*(?:@[^@]*|\s+\d+-Core Processor\b.*|\s+(?:w/|with)\s+(?:AMD\s+)?Radeon\b.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CpuMarketingWords = new Regex(@"\s+(?:Processor\b|Dual Edition$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Gpu5060TiVramSuffix = new Regex(@"^NVIDIA GeForce RTX 5060 Ti\s+(?:8|16)\s*GB$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex GpuVramSpacing = new Regex(@"(?<=\d)\s+GB$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static string NormalizeHardwareName(string name) =>
            HardwareNameSpacing.Replace(HardwareNameNoise.Replace(name ?? "", ""), " ").Trim();

        internal static string NormalizeCpuName(string name)
        {
            string model = CpuBrandPrefix.Replace(NormalizeHardwareName(name), "");
            model = CpuBrandSuffix.Replace(model, "");
            return CpuMarketingWords.Replace(model, "").Trim();
        }

        private static string NormalizeGpuName(string name)
        {
            string model = NormalizeHardwareName(name);
            if (model.StartsWith("GeForce ", StringComparison.OrdinalIgnoreCase))
                model = "NVIDIA " + model;
            if (model.StartsWith("Radeon RX ", StringComparison.OrdinalIgnoreCase))
                model = "AMD " + model;
            // NVIDIA publishes one limit for both VRAM variants of the desktop 5060 Ti.
            return Gpu5060TiVramSuffix.IsMatch(model) ? "NVIDIA GeForce RTX 5060 Ti" : GpuVramSpacing.Replace(model, "GB");
        }
        private static IEnumerable<IHardware> WalkHardware(IEnumerable<IHardware> roots)
        {
            foreach (var hw in roots) { yield return hw; foreach (var child in WalkHardware(hw.SubHardware)) yield return child; }
        }
        private static IEnumerable<ISensor> Sensors(IHardware hw) => hw == null ? Enumerable.Empty<ISensor>() : WalkHardware(new[] { hw }).SelectMany(h => h.Sensors);
        private static IHardware ChooseGpu(IEnumerable<IHardware> roots) => WalkHardware(roots).Where(h => h.HardwareType.ToString().StartsWith("Gpu", StringComparison.Ordinal)).OrderBy(h => h.HardwareType.ToString() == "GpuNvidia" || h.Name.IndexOf("Radeon RX", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1).ThenBy(h => h.Identifier.ToString(), StringComparer.Ordinal).FirstOrDefault();
        private static IHardware ResolveHardware(LineItem item, bool cpu)
        {
            var all = WalkHardware(_computer.Hardware).Where(h => cpu ? h.HardwareType == HardwareType.Cpu : h.HardwareType.ToString().StartsWith("Gpu", StringComparison.Ordinal));
            return !string.IsNullOrEmpty(item?.HardwareId) ? all.FirstOrDefault(h => h.Identifier.ToString() == item.HardwareId) : cpu ? all.FirstOrDefault() : ChooseGpu(_computer.Hardware);
        }
        private static void PollHardware()
        {
            int interval = Math.Max(500, Math.Min(GetMinPollInterval("cputemperature"), GetMinPollInterval("gputemperature")));
            if (SensorClock.ElapsedMilliseconds - _lastHardwarePoll < interval) return;
            _lastHardwarePoll = SensorClock.ElapsedMilliseconds;
            _sensorError = "";
            foreach (var hw in WalkHardware(_computer.Hardware))
                try { hw.Update(); } catch (Exception ex) { _sensorError = ex.GetType().Name + ": " + ex.Message; }
        }
        private static double? ReadTemperature(IHardware hw, string selected)
        {
            var sensors = Sensors(hw).Where(s => s.SensorType == SensorType.Temperature);
            if (!string.IsNullOrEmpty(selected))
            {
                var sensor = sensors.FirstOrDefault(s => s.Identifier.ToString() == selected) ?? sensors.FirstOrDefault(s => s.Name == selected);
                return sensor != null && IsValidTemperature(sensor.Value) ? (double?)sensor.Value : null;
            }
            var valid = sensors.Where(s => IsValidTemperature(s.Value) && s.Name.IndexOf("distance", StringComparison.OrdinalIgnoreCase) < 0).ToArray();
            var preferred = valid.FirstOrDefault(s => s.Name.IndexOf("average", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? valid.FirstOrDefault(s => s.Name.IndexOf("package", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? valid.FirstOrDefault(s => s.Name.IndexOf("tdie", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? valid.FirstOrDefault(s => s.Name.IndexOf("core", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? valid.FirstOrDefault();
            return preferred?.Value;
        }
        private static double? ReadLoad(IHardware hw)
        {
            var values = Sensors(hw).Where(s => s.SensorType == SensorType.Load && s.Value.HasValue && !float.IsNaN(s.Value.Value) && !float.IsInfinity(s.Value.Value));
            return (values.FirstOrDefault(s => s.Name.IndexOf("core", StringComparison.OrdinalIgnoreCase) >= 0) ?? values.FirstOrDefault())?.Value;
        }
        private static string CpuStatus() => IsValidTemperature(cachedCpuTemp) ? "Available" : !new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator) ? "InsufficientPrivileges" : string.IsNullOrEmpty(_sensorError) ? "SensorUnavailable" : "ReadFailed";
        private static void WriteHardwareInventory()
        {
            var devices = new JArray(WalkHardware(_computer.Hardware).Where(h => h.HardwareType == HardwareType.Cpu || h.HardwareType.ToString().StartsWith("Gpu", StringComparison.Ordinal)).Select(h => new JObject(
                new JProperty("Id", h.Identifier.ToString()), new JProperty("Name", h.Name), new JProperty("Type", h.HardwareType.ToString()),
                new JProperty("Sensors", new JArray(Sensors(h).Where(s => s.SensorType == SensorType.Temperature).Select(s => new JObject(new JProperty("Id", s.Identifier.ToString()), new JProperty("Name", s.Name))))))));
            var data = new JObject(new JProperty("CpuName", cpuHardware?.Name ?? ""), new JProperty("GpuName", gpuHardware?.Name ?? ""), new JProperty("Devices", devices), new JProperty("CpuTempSensors", new JArray(Sensors(cpuHardware).Where(s => s.SensorType == SensorType.Temperature).Select(s => s.Name))), new JProperty("GpuTempSensors", new JArray(Sensors(gpuHardware).Where(s => s.SensorType == SensorType.Temperature).Select(s => s.Name))));
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "hardware.json"), data.ToString(Formatting.None));
        }
    }
}
