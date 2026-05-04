using LibreHardwareMonitor.Hardware;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using static GGSystemMonitor.Program;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace GGSystemMonitor
{
    internal class Program
    {
        // Configuration
        private const string APP_NAME = "CUSTOM_SYSTEM_MONITOR";
        private const int SCREEN_COLS = 15;
        private const int OLED_UPDATE_INTERVAL_MS = 250;
        private const string EVENT_NAME = "SYS_MONITOR";
        internal const string VERSION = "3.0.0";
        private const string GITHUB_REPO = "DaveTheTopDev/GGSystemMonitor";
        private static HttpClient http = new HttpClient();
        private static Computer _computer;
        private static IHardware cpuHardware;
        private static IHardware gpuHardware;
        private static string baseUrl = null;
        private static int updateValue;
        private static int screenUpdate;
        private static int _blinkPhase;
        internal static bool gpuPasteWarning;
        private static int gpuPasteFPCounter;
        private static int textScrollPos;
        private static int _topLineDuration;
        private static int _bottomLineDuration;
        private static bool capsLockToggled;
        internal static volatile string AvailableVersion = null;
        private  static int _updateNotifCycle = 0;
        private const string PasteWarningText = "                 Check GPU Thermal Paste!";

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);
        private static bool IsCapsLockOn() => (GetKeyState(0x14) & 1) != 0;

        private static volatile AppSettings settings;
        private static readonly Regex coreSensorRegex = new Regex(@"core #\d+", RegexOptions.Compiled);
        private static double? cachedHotspot;
        private static double? cachedCpuTemp;
        private static double? cachedGpuTemp;
        private static double? cachedCpuUsage;
        private static double? cachedGpuUsage;
        private static double? cachedRamUsed;
        private static double? cachedRamTotal;
        private static IHardware memoryHardware;
        private static int topLineIndex;
        private static int bottomLineIndex;
        private static int topLineTimer;
        private static int bottomLineTimer;
        private static float cpuWarningTemperature;
        private static float cpuCriticalTemperature;
        private static float gpuWarningTemperature;
        private static float gpuCriticalTemperature;

        [System.STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0].Equals("--settings", StringComparison.OrdinalIgnoreCase))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Task.Run(() => CheckForUpdates());
                Application.Run(new SettingsForm());
                return;
            }

            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = Process.GetCurrentProcess().MainModule.FileName,
                        UseShellExecute = true,
                        Verb = "runas"
                    });
                    return; // Elevated instance launched - Exit this one
                }
                catch { /* User declined UAC - Continue without admin (CPU temp will read N/A) */ }
            }

            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                try { SendToOled("GGSystemMonitor", "Waiting..."); } catch { }
                try { _computer?.Close(); } catch { }
            };
            Microsoft.Win32.SystemEvents.SessionEnding += (s, e) =>
            {
                try { SendToOled("GGSystemMonitor", "Waiting..."); } catch { }
            };

            try
            {
                // --- Initialize LibreHardwareMonitor Computer ---
                _computer = new Computer()
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMemoryEnabled = true
                };
                _computer.Open();

                // Give LHM a short moment to initialize internal polling
                Thread.Sleep(500);

                // Init the hardware for the cpu, gpu, and memory
                foreach (var hw in _computer.Hardware)
                {
                    // HardwareType.ToString() contains "Cpu" for CPU entries
                    if (hw.HardwareType.ToString().ToLower().Contains("cpu"))
                    {
                        cpuHardware = hw;
                        break;
                    }
                }
                foreach (var hw in _computer.Hardware)
                {
                    if (hw.HardwareType.ToString().ToLower().Contains("gpu"))
                    {
                        gpuHardware = hw;
                        break;
                    }
                }
                foreach (var hw in _computer.Hardware)
                {
                    if (hw.HardwareType == HardwareType.Memory)
                    {
                        memoryHardware = hw;
                        break;
                    }
                }

                // Cache detected hardware names for the settings UI
                try
                {
                    var hwInfo = new JObject(
                        new JProperty("CpuName", cpuHardware?.Name ?? ""),
                        new JProperty("GpuName", gpuHardware?.Name ?? "")
                    );
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "hardware.json"), hwInfo.ToString(Formatting.None));
                }
                catch { }

                // --- Initialize Settings.json file ---
                settings = SettingsManager.LoadSettings();

                FileSystemWatcher watcher = new FileSystemWatcher(AppContext.BaseDirectory, "settings.json");
                watcher.Changed += (s, e) =>
                {
                    try
                    {
                        Thread.Sleep(100); // brief delay for file lock
                        settings = SettingsManager.LoadSettings();
                    }
                    catch { }
                };
                watcher.EnableRaisingEvents = true;

                // Wait for GG Engine to be available (polls until coreProps.json is present and readable)
                while (baseUrl == null)
                {
                    GetGGAddress();
                    if (baseUrl == null) Thread.Sleep(2000);
                }

                // --- Register app/events with SteelSeries GameSense ---
                RegisterApp();
                RegisterEvent();
                BindEvent();
                SendToOled("GGSystemMonitor", "Waiting...");
                StartCapsLockWatcher();

                // Check GitHub for a newer release in the background (non-blocking)
                Task.Run(() => CheckForUpdates());

                //Console.WriteLine("GGSystemMonitor started. Updating every 2 seconds. Press Ctrl+C to stop.");

                int temperatureUpdate = 0;
                screenUpdate = OLED_UPDATE_INTERVAL_MS;
                var tickSw = System.Diagnostics.Stopwatch.StartNew();

                // Main loop
                while (true)
                {
                    try
                    {
                        // Measure actual elapsed time so all timers run at real wall-clock speed
                        // regardless of Thread.Sleep precision (~15ms on Windows default timer resolution)
                        int tickMs = (int)tickSw.ElapsedMilliseconds;
                        tickSw.Restart();
                        if (tickMs < 1)   tickMs = 1;
                        if (tickMs > 200) tickMs = 10; // guard against system suspend/resume

                        temperatureUpdate -= tickMs;
                        if (temperatureUpdate <= 0)
                        {
                            temperatureUpdate = settings.TemperatureUpdateIntervalMs;
                            cachedCpuTemp = GetCpuTemperature();
                            cachedGpuTemp = GetGpuTemperature();
                            cachedCpuUsage = GetCpuUsage();
                            cachedGpuUsage = GetGpuUsage();
                            (cachedRamUsed, cachedRamTotal) = GetRamUsage();

                            // GPU thermal paste monitoring (real hardware)
                            bool pasteWarnFromHW = false;
                            if (settings.EnableGPUThermalPasteMonitoring)
                            {
                                cachedHotspot = GetGpuHotSpot();
                                if (cachedHotspot.HasValue && cachedGpuTemp.HasValue && cachedHotspot - cachedGpuTemp > 15)
                                {
                                    if (gpuPasteFPCounter < 48) gpuPasteFPCounter++;
                                }
                                else if (gpuPasteFPCounter > 0)
                                {
                                    gpuPasteFPCounter--;
                                }
                                pasteWarnFromHW = gpuPasteFPCounter >= 12;
                            }
                            else
                            {
                                gpuPasteFPCounter = 0;
                            }

                            gpuPasteWarning = pasteWarnFromHW;
                        }

                        topLineTimer -= tickMs;
                        if (topLineTimer <= 0)
                        {
                            var topItems = settings.TopLineItems;
                            if (topItems != null && topItems.Count > 1)
                                topLineIndex = (topLineIndex + 1) % topItems.Count;
                            var cur = topItems != null && topItems.Count > 0 ? topItems[topLineIndex % topItems.Count] : null;
                            int topDur = (cur?.DurationMs > 0) ? cur.DurationMs : (settings.RotationIntervalMs > 0 ? settings.RotationIntervalMs : 3000);
                            topLineTimer = topDur;
                            _topLineDuration = topDur;
                        }
                        bottomLineTimer -= tickMs;
                        if (bottomLineTimer <= 0)
                        {
                            var bottomItems = settings.BottomLineItems;
                            if (bottomItems != null && bottomItems.Count > 1)
                                bottomLineIndex = (bottomLineIndex + 1) % bottomItems.Count;
                            var cur = bottomItems != null && bottomItems.Count > 0 ? bottomItems[bottomLineIndex % bottomItems.Count] : null;
                            int botDur = (cur?.DurationMs > 0) ? cur.DurationMs : (settings.RotationIntervalMs > 0 ? settings.RotationIntervalMs : 3000);
                            bottomLineTimer = botDur;
                            _bottomLineDuration = botDur;
                        }

                        if (AvailableVersion != null && settings.ShowUpdateNotifications)
                            _updateNotifCycle = (_updateNotifCycle + tickMs) % 13000;
                        else
                            _updateNotifCycle = 0;

                        screenUpdate -= tickMs;
                        if (screenUpdate <= 0)
                        {
                            _blinkPhase = (_blinkPhase + 1) & 3;
                            // Advance paste-warning scroll only in the main loop so caps-lock
                            // key presses (which also send a frame) don't speed it up
                            if (gpuPasteWarning)
                            {
                                textScrollPos++;
                                if (textScrollPos >= PasteWarningText.Length + 32)
                                    textScrollPos = 0;
                            }
                            else
                            {
                                textScrollPos = 0;
                            }
                            BuildAndSendDisplay();
                            screenUpdate = OLED_UPDATE_INTERVAL_MS;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log exceptions to file for troubleshooting
                        File.AppendAllText("GGSystemMonitor.log", DateTime.Now + " - Error: " + ex + Environment.NewLine);
                    }
                    Thread.Sleep(10);
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText("GGSystemMonitor.log", DateTime.Now + " - Fatal: " + ex + Environment.NewLine);
            }
            finally
            {
                try { _computer?.Close(); } catch { }
            }
        }
        public class AppSettings
        {
            public const int CurrentSettingsVersion = 1;
            public int SettingsVersion { get; set; } = 0;
            public string GGEngineCorePropsPath { get; set; } = @"C:/ProgramData/SteelSeries/SteelSeries Engine 3/coreProps.json";
            public int TemperatureUpdateIntervalMs { get; set; } = 2000;
            public bool EnableCPUTemperatureWarningIndicators { get; set; } = true;
            public bool EnableGPUTemperatureWarningIndicators { get; set; } = true;
            public object WarningCPUTemperature { get; set; } = "AUTO";
            public object CriticalCPUTemperature { get; set; } = "AUTO";
            public object WarningGPUTemperature { get; set; } = "AUTO";
            public object CriticalGPUTemperature { get; set; } = "AUTO";
            public bool EnableGPUThermalPasteMonitoring { get; set; } = true;
            public bool ShowCapsLockIndicator { get; set; } = true;
            public bool ShowUpdateNotifications { get; set; } = true;
            [System.Text.Json.Serialization.JsonConverter(typeof(LineItemListConverter))]
            public List<LineItem> TopLineItems { get; set; } = new List<LineItem>
            {
                new LineItem { Type = "CpuTemperature" },
                new LineItem { Type = "CpuUsage" },
                new LineItem { Type = "RamUsage" },
            };
            [System.Text.Json.Serialization.JsonConverter(typeof(LineItemListConverter))]
            public List<LineItem> BottomLineItems { get; set; } = new List<LineItem> { new LineItem { Type = "GpuTemperature" } };
            public int RotationIntervalMs { get; set; } = 3000;
        }
        public static class SettingsManager
        {
            private static readonly string SettingsFilePath =
                Path.Combine(AppContext.BaseDirectory, "settings.json");

            public static AppSettings LoadSettings()
            {
                if (!File.Exists(SettingsFilePath))
                {
                    File.WriteAllText(SettingsFilePath, GetDefaultSettingsJson());
                }
                string fileJson = File.ReadAllText(SettingsFilePath);
                AppSettings appSettings = JsonSerializer.Deserialize<AppSettings>(fileJson) ?? new AppSettings();
                if (appSettings.SettingsVersion < AppSettings.CurrentSettingsVersion)
                    appSettings = Migrate(appSettings, fileJson);
                cpuCriticalTemperature = GetSettingParse(appSettings.CriticalCPUTemperature, GetCpuAutoCritical(cpuHardware?.Name));
                cpuWarningTemperature  = GetSettingParse(appSettings.WarningCPUTemperature,  GetCpuAutoWarning(cpuHardware?.Name));
                if (cpuWarningTemperature >= cpuCriticalTemperature)
                {
                    cpuWarningTemperature = cpuCriticalTemperature - 1;
                }
                gpuCriticalTemperature = GetSettingParse(appSettings.CriticalGPUTemperature, GetGpuAutoCritical(gpuHardware?.Name));
                gpuWarningTemperature  = GetSettingParse(appSettings.WarningGPUTemperature,  GetGpuAutoWarning(gpuHardware?.Name));
                if (gpuWarningTemperature >= gpuCriticalTemperature)
                {
                    gpuWarningTemperature = gpuCriticalTemperature - 1;
                }
                return appSettings;
            }
            private static string GetDefaultSettingsJson()
            {
                return @"{
  ""SettingsVersion"": 1,

  ""GGEngineCorePropsPath"": ""C:/ProgramData/SteelSeries/SteelSeries Engine 3/coreProps.json"",
  ""_note_GGEngineCorePropsPath"": ""Only change if GG Engine is installed in a non-default location."",

  ""TemperatureUpdateIntervalMs"": 2000,
  ""_note_TemperatureUpdateIntervalMs"": ""How often in milliseconds to poll hardware sensors. Default: 2000 (2 seconds)."",

  ""EnableCPUTemperatureWarningIndicators"": true,
  ""_note_EnableCPUTemperatureWarningIndicators"": ""Show blinking warning icons on the display when CPU temperature nears critical levels."",

  ""EnableGPUTemperatureWarningIndicators"": true,
  ""_note_EnableGPUTemperatureWarningIndicators"": ""Show blinking warning icons on the display when GPU temperature nears critical levels."",

  ""WarningCPUTemperature"": ""AUTO"",
  ""_note_WarningCPUTemperature"": ""CPU temp (°C) where the warning indicator starts blinking. AUTO = CriticalCPUTemperature minus 15."",

  ""CriticalCPUTemperature"": ""AUTO"",
  ""_note_CriticalCPUTemperature"": ""CPU temp (°C) considered critical. AUTO = manufacturer rated maximum for your CPU model."",

  ""WarningGPUTemperature"": ""AUTO"",
  ""_note_WarningGPUTemperature"": ""GPU temp (°C) where the warning indicator starts blinking. AUTO = CriticalGPUTemperature minus 15."",

  ""CriticalGPUTemperature"": ""AUTO"",
  ""_note_CriticalGPUTemperature"": ""GPU temp (°C) considered critical. AUTO = manufacturer rated maximum for your GPU model."",

  ""EnableGPUThermalPasteMonitoring"": true,
  ""_note_EnableGPUThermalPasteMonitoring"": ""When the gap between GPU core and hotspot temperature exceeds 15°C, a scrolling warning replaces the bottom display line."",

  ""ShowCapsLockIndicator"": true,
  ""_note_ShowCapsLockIndicator"": ""Show a 🡅 icon on the top-right of the display when Caps Lock is active."",

  ""ShowUpdateNotifications"": true,
  ""_note_ShowUpdateNotifications"": ""When a newer version is found, display a notification on the keyboard OLED (Update / Available) for 3 seconds every 10 seconds."",

  ""TopLineItems"": [ { ""Type"": ""CpuTemperature"", ""Label"": null, ""DurationMs"": 0 }, { ""Type"": ""CpuUsage"", ""Label"": null, ""DurationMs"": 0 }, { ""Type"": ""RamUsage"", ""Label"": null, ""DurationMs"": 0 } ],
  ""_note_TopLineItems"": ""Items for the top display line. Each item: Type (CpuTemperature/CpuUsage/GpuTemperature/GpuUsage/RamUsage/Text), Label (custom text with {value} as sensor placeholder, null = default format), DurationMs (ms to show this item, 0 = use RotationIntervalMs)."",

  ""BottomLineItems"": [ { ""Type"": ""GpuTemperature"", ""Label"": null, ""DurationMs"": 0 } ],
  ""_note_BottomLineItems"": ""Items for the bottom display line. Same format as TopLineItems."",

  ""RotationIntervalMs"": 3000,
  ""_note_RotationIntervalMs"": ""Fallback display duration per item when DurationMs is 0. Only applies when a line has more than one item. Default: 3000 (3 seconds).""
}";
            }

            private static AppSettings Migrate(AppSettings settings, string originalJson)
            {
                try
                {
                    var jo = JObject.Parse(originalJson);
                    int ver = jo["SettingsVersion"]?.Value<int>() ?? 0;
                    if (ver < 1)
                    {
                        jo["SettingsVersion"] = 1;
                        ver = 1;
                    }
                    // Future: if (ver < 2) { jo["NewField"] = defaultValue; jo["SettingsVersion"] = ver = 2; }
                    settings.SettingsVersion = ver;
                    File.WriteAllText(SettingsFilePath, jo.ToString(Formatting.Indented));
                }
                catch { }
                return settings;
            }
        }
        public class LineItem
        {
            public string Type { get; set; } = "CpuTemperature";
            public string Label { get; set; } = null;
            public int DurationMs { get; set; } = 0;
            public bool UseFahrenheit { get; set; } = false;
            public bool UseRamMb { get; set; } = false;

            [System.Text.Json.Serialization.JsonIgnore]
            public string ListDisplayName
            {
                get
                {
                    if (Type?.ToLower() == "text") return $"Text: \"{Label ?? "Text"}\"";
                    string baseName;
                    switch (Type?.ToLower().Replace(" ", "").Replace("_", "") ?? "")
                    {
                        case "cputemperature": baseName = UseFahrenheit ? "CPU Temperature (°F)" : "CPU Temperature (°C)"; break;
                        case "cpuusage":       baseName = "CPU Usage %"; break;
                        case "gputemperature": baseName = UseFahrenheit ? "GPU Temperature (°F)" : "GPU Temperature (°C)"; break;
                        case "gpuusage":       baseName = "GPU Usage %"; break;
                        case "ramusage":       baseName = UseRamMb ? "RAM Usage (used / total MB)" : "RAM Usage (used / total GB)"; break;
                        default:               baseName = Type ?? "Unknown"; break;
                    }
                    return Label != null ? $"{baseName}: \"{Label}\"" : baseName;
                }
            }
            public override string ToString() => ListDisplayName;
        }

        public class LineItemListConverter : System.Text.Json.Serialization.JsonConverter<List<LineItem>>
        {
            public override List<LineItem> Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
            {
                var result = new List<LineItem>();
                if (reader.TokenType != System.Text.Json.JsonTokenType.StartArray) return result;
                while (reader.Read() && reader.TokenType != System.Text.Json.JsonTokenType.EndArray)
                {
                    if (reader.TokenType == System.Text.Json.JsonTokenType.String)
                        result.Add(new LineItem { Type = reader.GetString() });
                    else if (reader.TokenType == System.Text.Json.JsonTokenType.StartObject)
                    {
                        var item = System.Text.Json.JsonSerializer.Deserialize<LineItem>(ref reader, options);
                        if (item != null) result.Add(item);
                    }
                }
                return result;
            }
            public override void Write(System.Text.Json.Utf8JsonWriter writer, List<LineItem> value, System.Text.Json.JsonSerializerOptions options)
            {
                System.Text.Json.JsonSerializer.Serialize(writer, value, options);
            }
        }

        private static float GetSettingParse(object setting, float defaultValue)
        {
            if (setting is string s && s.ToLower().Equals("auto"))
            {
                return defaultValue;
            }
            if (setting is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Number)
                    return element.GetSingle();
                if (element.ValueKind == JsonValueKind.String &&
                    float.TryParse(element.GetString(), out float val))
                    return val;
            }
            if (setting is IConvertible convertible)
            {
                try
                {
                    return Convert.ToSingle(convertible);
                }
                catch { }
            }
            return defaultValue;
        }
        private static void GetGGAddress()
        {
            if (!File.Exists(settings.GGEngineCorePropsPath))
            {
                File.AppendAllText("GGSystemMonitor.log", DateTime.Now + $" - coreProps.json not found at {settings.GGEngineCorePropsPath}. Please verify SteelSeries GG installation path and update in the settings.json file!" + Environment.NewLine);
                return;
            }
            string coreText = File.ReadAllText(settings.GGEngineCorePropsPath);
            try
            {
                var j = JObject.Parse(coreText);
                var addr = j["address"]?.ToString();
                if (string.IsNullOrEmpty(addr))
                {
                    File.AppendAllText("GGSystemMonitor.log", DateTime.Now + " - Could not find 'address' field in coreProps.json" + Environment.NewLine);
                    return;
                }
                baseUrl = "http://" + addr;
            }
            catch (Exception ex)
            {
                File.AppendAllText("GGSystemMonitor.log", DateTime.Now + " - Failed to parse coreProps.json: " + ex.Message + Environment.NewLine);
                return;
            }
        }
        private static void RegisterApp()
        {
            var meta = new JObject(
                new JProperty("game", APP_NAME),
                new JProperty("game_display_name", "Custom System Monitor"),
                new JProperty("developer", "DaveTheTopDev")
            );
            PostJson("/game_metadata", meta);
        }
        private static void RegisterEvent()
        {
            var evt = new JObject(
                new JProperty("game", APP_NAME),
                new JProperty("event", EVENT_NAME),
                new JProperty("min_value", 0),
                new JProperty("max_value", 100),
                new JProperty("icon_id", 0),
                new JProperty("value_optional", true)
            );
            PostJson("/register_game_event", evt);
        }
        private static void BindEvent()
        {
            // Build the bind payload using JObject so we can use hyphenated property names (device-type, has-text)
            var bind = new JObject(
                new JProperty("game", APP_NAME),
                new JProperty("event", EVENT_NAME),
                new JProperty("handlers", new JArray(
                    new JObject(
                        new JProperty("device-type", "keyboard"),
                        new JProperty("zone", "one"),
                        new JProperty("mode", "screen"),
                        new JProperty("datas", new JArray(
                            new JObject(
                                new JProperty("lines", new JArray(
                                    new JObject(new JProperty("has-text", true), new JProperty("context-frame-key", "text_line_1")),
                                    new JObject(new JProperty("has-text", true), new JProperty("context-frame-key", "text_line_2"))
                                ))
                            ))
                        )
                    ))
                )
            );
            PostJson("/bind_game_event", bind);
        }
        private static string GetItemContent(LineItem lineItem)
        {
            string type = lineItem?.Type?.ToLower().Replace(" ", "").Replace("_", "") ?? "";
            if (type == "text")
                return lineItem.Label ?? "Text";

            if (lineItem?.Label != null)
                return ApplyLabelFormat(lineItem.Label, type, lineItem.UseFahrenheit, lineItem.UseRamMb);

            switch (type)
            {
                case "cputemperature":
                    if (!cachedCpuTemp.HasValue) return "CPU: N/A";
                    if (lineItem.UseFahrenheit) { double f = cachedCpuTemp.Value * 9.0 / 5.0 + 32.0; return $"CPU: {f:F1}°F"; }
                    return $"CPU: {cachedCpuTemp.Value:F1}°C";
                case "cpuusage":
                    return cachedCpuUsage.HasValue ? $"CPU USE: {cachedCpuUsage.Value:F0}%" : "CPU: N/A";
                case "gputemperature":
                    if (!cachedGpuTemp.HasValue) return "GPU: N/A";
                    if (lineItem.UseFahrenheit) { double f = cachedGpuTemp.Value * 9.0 / 5.0 + 32.0; return $"GPU: {f:F1}°F"; }
                    return $"GPU: {cachedGpuTemp.Value:F1}°C";
                case "gpuusage":
                    return cachedGpuUsage.HasValue ? $"GPU USE: {cachedGpuUsage.Value:F0}%" : "GPU: N/A";
                case "ramusage":
                    if (cachedRamUsed.HasValue && cachedRamTotal.HasValue)
                    {
                        if (lineItem.UseRamMb)
                        {
                            double usedMb = cachedRamUsed.Value * 1024.0;
                            double totalMb = Math.Round(cachedRamTotal.Value) * 1024.0;
                            return $"RAM: {usedMb:0.#}/{totalMb:0.#}MB";
                        }
                        double used = cachedRamUsed.Value;
                        double total = Math.Round(cachedRamTotal.Value);
                        return $"RAM: {used:0.#}/{total:0.#}GB";
                    }
                    return "RAM: N/A";
                default:
                    return "N/A";
            }
        }

        private static string ApplyLabelFormat(string label, string type, bool useFahrenheit, bool useRamMb)
        {
            string result = label;

            result = Regex.Replace(result, @"\{temp:([^}]+)\}", m =>
            {
                double? raw = type == "cputemperature" ? cachedCpuTemp : cachedGpuTemp;
                if (!raw.HasValue) return "N/A";
                double val = useFahrenheit ? raw.Value * 9.0 / 5.0 + 32.0 : raw.Value;
                try { return val.ToString(m.Groups[1].Value); } catch { return val.ToString("F1"); }
            });

            result = Regex.Replace(result, @"\{pct:([^}]+)\}", m =>
            {
                double? raw = type == "cpuusage" ? cachedCpuUsage : cachedGpuUsage;
                if (!raw.HasValue) return "N/A";
                try { return raw.Value.ToString(m.Groups[1].Value); } catch { return raw.Value.ToString("F0"); }
            });

            result = Regex.Replace(result, @"\{used:([^}]+)\}", m =>
            {
                if (!cachedRamUsed.HasValue) return "N/A";
                double val = useRamMb ? cachedRamUsed.Value * 1024.0 : cachedRamUsed.Value;
                try { return val.ToString(m.Groups[1].Value); } catch { return val.ToString("0.#"); }
            });

            result = Regex.Replace(result, @"\{total:([^}]+)\}", m =>
            {
                if (!cachedRamTotal.HasValue) return "N/A";
                double val = useRamMb ? Math.Round(cachedRamTotal.Value) * 1024.0 : Math.Round(cachedRamTotal.Value);
                try { return val.ToString(m.Groups[1].Value); } catch { return val.ToString("0.#"); }
            });

            if (result.Contains("{value}"))
            {
                string legacy;
                switch (type)
                {
                    case "cputemperature":
                        if (!cachedCpuTemp.HasValue) { legacy = "N/A"; break; }
                        if (useFahrenheit) { double f = cachedCpuTemp.Value * 9.0 / 5.0 + 32.0; legacy = $"{f:F1}°F"; break; }
                        legacy = $"{cachedCpuTemp.Value:F1}°C"; break;
                    case "cpuusage":  legacy = cachedCpuUsage.HasValue ? $"{cachedCpuUsage.Value:F0}%" : "N/A"; break;
                    case "gputemperature":
                        if (!cachedGpuTemp.HasValue) { legacy = "N/A"; break; }
                        if (useFahrenheit) { double f = cachedGpuTemp.Value * 9.0 / 5.0 + 32.0; legacy = $"{f:F1}°F"; break; }
                        legacy = $"{cachedGpuTemp.Value:F1}°C"; break;
                    case "gpuusage":  legacy = cachedGpuUsage.HasValue ? $"{cachedGpuUsage.Value:F0}%" : "N/A"; break;
                    case "ramusage":
                        if (!cachedRamUsed.HasValue || !cachedRamTotal.HasValue) { legacy = "N/A"; break; }
                        if (useRamMb)
                        {
                            double uMb = cachedRamUsed.Value * 1024.0;
                            double tMb = Math.Round(cachedRamTotal.Value) * 1024.0;
                            legacy = $"{uMb:0.#}/{tMb:0.#}MB"; break;
                        }
                        legacy = $"{cachedRamUsed.Value:0.#}/{Math.Round(cachedRamTotal.Value):0.#}GB"; break;
                    default: legacy = "N/A"; break;
                }
                result = result.Replace("{value}", legacy);
            }

            return result;
        }
        // Returns how many display columns are reserved for indicators on a given line.
        // 🡅 alone = 1 display col; ⚠🡅 or 🔥🡅 combined = 3 display cols; ⚠ or 🔥 alone = 1.
        // The combined case reserves 3 (not 1+1=2) because the preceding temp indicator causes
        // an extra display column to be consumed before 🡅 on this OLED firmware.
        private static int GetReservedCols(bool isTopLine, string normalizedType, bool capsLockActive)
        {
            bool tempActive =
                (normalizedType == "cputemperature" && settings.EnableCPUTemperatureWarningIndicators &&
                 cachedCpuTemp.HasValue && cachedCpuTemp.Value >= cpuWarningTemperature) ||
                (normalizedType == "gputemperature" && settings.EnableGPUTemperatureWarningIndicators &&
                 cachedGpuTemp.HasValue && cachedGpuTemp.Value >= gpuWarningTemperature);
            if (isTopLine && capsLockActive)
                return tempActive ? 3 : 1;  // ⚠/🔥+🡅 = 3 cols; 🡅 alone = 1 col
            return tempActive ? 1 : 0;
        }

        // Returns a window of `availableCols` chars from `content` based on elapsed time.
        // The full scroll distance is mapped into the configured item duration.
        private static string ApplyScroll(string content, int availableCols, int elapsed, int duration)
        {
            if (content.Length <= availableCols || duration <= 0)
                return content;

            int scrollDistance = content.Length - availableCols;
            int scrollDuration = Math.Max(1, duration - OLED_UPDATE_INTERVAL_MS);
            double progress = Math.Max(0, Math.Min(1, elapsed / (double)scrollDuration));
            int offset = Math.Min((int)Math.Round(scrollDistance * progress), scrollDistance);
            return content.Substring(offset, availableCols);
        }

        private static string ApplyTemperatureIndicators(string line, double temp, float warning, float critical, bool indicatorsEnabled, bool capsLock)
        {
            if (indicatorsEnabled && temp >= warning)
            {
                if (temp >= critical)
                {
                    // blink fire at 2Hz: phase 0 and 2 are "on" (250ms on, 250ms off)
                    if ((_blinkPhase & 1) == 0)
                        return capsLock ? line.PadRight(12) + "🔥🡅" : line.PadRight(14) + "🔥";
                    return capsLock ? line.PadRight(14) + "🡅" : line;
                }
                // blink warning at 1Hz: phases 0-1 are "on" (500ms on, 500ms off)
                if (_blinkPhase < 2)
                    return capsLock ? line.PadRight(12) + "⚠🡅" : line.PadRight(14) + "⚠";
                return capsLock ? line.PadRight(14) + "🡅" : line;
            }
            return capsLock ? line.PadRight(14) + "🡅" : line;
        }
        private static void BuildAndSendDisplay()
        {
            if (AvailableVersion != null && settings.ShowUpdateNotifications && _updateNotifCycle >= 10000)
            {
                bool capsLock = settings.ShowCapsLockIndicator && capsLockToggled;
                string updateTop = capsLock ? "Update".PadRight(14) + "🡅" : "Update";
                SendToOled(updateTop, "Available!");
            }
            else
            {
                SendToOled(BuildTopLine(), BuildBottomLine());
            }
        }

        private static string BuildTopLine()
        {
            bool capsLock = settings.ShowCapsLockIndicator && capsLockToggled;
            var items = settings.TopLineItems;
            var lineItem = (items != null && items.Count > 0) ? items[topLineIndex % items.Count] : new LineItem { Type = "CpuTemperature" };
            string type = lineItem?.Type?.ToLower().Replace(" ", "").Replace("_", "") ?? "";
            string content = GetItemContent(lineItem);

            int reservedCols = GetReservedCols(isTopLine: true, type, capsLock);
            int availableCols = SCREEN_COLS - reservedCols;
            int elapsed = _topLineDuration > 0 ? _topLineDuration - topLineTimer : 0;
            content = ApplyScroll(content, availableCols, elapsed, _topLineDuration);

            switch (type)
            {
                case "cputemperature":
                    if (cachedCpuTemp.HasValue)
                        return ApplyTemperatureIndicators(content, cachedCpuTemp.Value, cpuWarningTemperature, cpuCriticalTemperature, settings.EnableCPUTemperatureWarningIndicators, capsLock);
                    break;
                case "gputemperature":
                    if (cachedGpuTemp.HasValue)
                        return ApplyTemperatureIndicators(content, cachedGpuTemp.Value, gpuWarningTemperature, gpuCriticalTemperature, settings.EnableGPUTemperatureWarningIndicators, capsLock);
                    break;
            }
            return capsLock ? content.PadRight(14) + "🡅" : content;
        }
        private static string BuildBottomLine()
        {
            if (gpuPasteWarning)
            {
                // textScrollPos is advanced by the main loop only — not here — so that
                // caps-lock key presses (which also call BuildAndSendDisplay) don't speed
                // up the scroll or the GPU/hotspot display timing.
                if (textScrollPos > PasteWarningText.Length)
                {
                    if (textScrollPos < PasteWarningText.Length + 16)
                        return cachedGpuTemp.HasValue   ? $"GPU: {cachedGpuTemp.Value:F1}°C"     : "GPU: N/A";
                    return cachedHotspot.HasValue ? $"HOTSPOT: {cachedHotspot.Value:F1}°C" : "HOT SPOT: N/A";
                }
                int p = Math.Max(0, Math.Min(textScrollPos, PasteWarningText.Length));
                return PasteWarningText.Substring(p) + PasteWarningText.Substring(0, p);
            }

            var items = settings.BottomLineItems;
            var lineItem = (items != null && items.Count > 0) ? items[bottomLineIndex % items.Count] : new LineItem { Type = "GpuTemperature" };
            string type = lineItem?.Type?.ToLower().Replace(" ", "").Replace("_", "") ?? "";
            string content = GetItemContent(lineItem);

            int reservedCols = GetReservedCols(isTopLine: false, type, false);
            int availableCols = SCREEN_COLS - reservedCols;
            int elapsed = _bottomLineDuration > 0 ? _bottomLineDuration - bottomLineTimer : 0;
            content = ApplyScroll(content, availableCols, elapsed, _bottomLineDuration);

            switch (type)
            {
                case "cputemperature":
                    if (cachedCpuTemp.HasValue)
                        return ApplyTemperatureIndicators(content, cachedCpuTemp.Value, cpuWarningTemperature, cpuCriticalTemperature, settings.EnableCPUTemperatureWarningIndicators, false);
                    break;
                case "gputemperature":
                    if (cachedGpuTemp.HasValue)
                        return ApplyTemperatureIndicators(content, cachedGpuTemp.Value, gpuWarningTemperature, gpuCriticalTemperature, settings.EnableGPUTemperatureWarningIndicators, false);
                    break;
            }
            return content;
        }
        private static void SendToOled(string line1, string line2)
        {
            if (updateValue++ == 2)
                updateValue = 0;

            var payload = new JObject(
                new JProperty("game", APP_NAME),
                new JProperty("event", EVENT_NAME),
                new JProperty("data", new JObject(
                    new JProperty("value", updateValue),
                    new JProperty("frame", new JObject(
                        new JProperty("text_line_1", line1),
                        new JProperty("text_line_2", line2)
                    ))
                ))
            );
            PostJson("/game_event", payload);
        }
        private static void PostJson(string path, JObject obj)
        {
            try
            {
                var url = baseUrl + path;
                var content = new StringContent(obj.ToString(Formatting.None), Encoding.UTF8, "application/json");
                var resp = http.PostAsync(url, content).Result;
                // optionally inspect resp.StatusCode or resp.Content
            }
            catch (Exception ex)
            {
                File.AppendAllText("GGSystemMonitor.log", DateTime.Now + " - PostJson error: " + ex + Environment.NewLine);
                GetGGAddress();
            }
        }
        private static void CheckForUpdates()
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "GGSystemMonitor/" + VERSION);
                    string json = client.GetStringAsync(
                        "https://api.github.com/repos/" + GITHUB_REPO + "/releases/latest").Result;

                    var doc = JsonDocument.Parse(json);
                    string tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
                    string latestStr = tagName.TrimStart('v', 'V');

                    if (!Version.TryParse(latestStr, out Version latest) ||
                        !Version.TryParse(VERSION, out Version current) ||
                        latest <= current)
                        return;

                    AvailableVersion = latestStr;
                }
            }
            catch { /* Network unavailable or API changed - Update check skipped */ }
        }
        private static void StartCapsLockWatcher()
        {
            var thread = new Thread(() =>
            {
                bool last = IsCapsLockOn();
                capsLockToggled = last;
                while (true)
                {
                    Thread.Sleep(10);
                    bool current = IsCapsLockOn();
                    if (current == last) continue;
                    last = current;
                    capsLockToggled = current;
                    try { BuildAndSendDisplay(); } catch { }
                }
            }) { IsBackground = true };
            thread.Start();
        }

        private static double? GetCpuUsage()
        {
            if (cpuHardware == null) return null;
            // cpuHardware.Update() was already called in GetCpuTemperature
            var total = cpuHardware.Sensors.FirstOrDefault(s =>
                s.SensorType == SensorType.Load && s.Name != null && s.Name.ToLower().Contains("total"));
            if (total?.Value.HasValue == true)
                return (double)total.Value.Value;
            var coreLoads = cpuHardware.Sensors
                .Where(s => s.SensorType == SensorType.Load && s.Value.HasValue)
                .Select(s => (double)s.Value.Value).ToList();
            return coreLoads.Count > 0 ? coreLoads.Average() : (double?)null;
        }
        private static double? GetGpuUsage()
        {
            if (gpuHardware == null) return null;
            // gpuHardware.Update() was already called in GetGpuTemperature
            var coreLoad = gpuHardware.Sensors.FirstOrDefault(s =>
                s.SensorType == SensorType.Load && s.Name != null && s.Name.ToLower().Contains("core"));
            if (coreLoad?.Value.HasValue == true)
                return (double)coreLoad.Value.Value;
            var firstLoad = gpuHardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Value.HasValue);
            return firstLoad != null ? (double?)firstLoad.Value.Value : null;
        }
        private static (double? used, double? total) GetRamUsage()
        {
            if (memoryHardware == null) return (null, null);
            memoryHardware.Update();
            var usedSensor = memoryHardware.Sensors.FirstOrDefault(s =>
                s.SensorType == SensorType.Data && s.Name != null &&
                s.Name.ToLower().Contains("used") && !s.Name.ToLower().Contains("virtual"));
            var availableSensor = memoryHardware.Sensors.FirstOrDefault(s =>
                s.SensorType == SensorType.Data && s.Name != null &&
                s.Name.ToLower().Contains("available") && !s.Name.ToLower().Contains("virtual"));
            if (usedSensor?.Value.HasValue == true && availableSensor?.Value.HasValue == true)
                return ((double)usedSensor.Value.Value, (double)usedSensor.Value.Value + availableSensor.Value.Value);
            if (usedSensor?.Value.HasValue == true)
                return ((double)usedSensor.Value.Value, null);
            return (null, null);
        }
        private static double? GetCpuTemperature()
        {
            if (cpuHardware == null)
                return null;

            cpuHardware.Update();

            // Try to read Average sensor first
            var average = cpuHardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name != null && s.Name.ToLower().Contains("average"));
            if (average != null && average.Value.HasValue)
                return (double) average.Value.Value;

            // Try to read Package sensor next
            var package = cpuHardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name != null && s.Name.ToLower().Contains("package"));
            if (package != null && package.Value.HasValue)
                return (double) package.Value.Value;

            // Fallback: average all "Core" temp sensors
            var coreTemps = cpuHardware.Sensors
                .Where(s => s.SensorType == SensorType.Temperature 
                    && s.Name != null 
                    && coreSensorRegex.IsMatch(s.Name.ToLower()))
                .Select(s => s.Value)
                .Where(v => v.HasValue)
                .Select(v => (double) v.Value)
                .ToList();

            if (coreTemps.Count > 0)
                return coreTemps.Average();

            // AMD Ryzen uses "Core (Tctl/Tdie)" as its primary die temperature sensor
            var tdie = cpuHardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature
                && s.Name != null
                && (s.Name.ToLower().Contains("tctl/tdie") || s.Name.ToLower().Contains("tdie")));
            if (tdie != null && tdie.Value.HasValue)
                return (double)tdie.Value.Value;

            // Last resort: first Temperature sensor with a value
            var anyTemp = cpuHardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value.HasValue);
            if (anyTemp != null)
                return (double) anyTemp.Value.Value;

            return null;
        }
        private static double? GetGpuTemperature()
        {
            if (gpuHardware == null)
                return null;

            gpuHardware.Update();

            // Look for core sensor
            var gpuCore = gpuHardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name != null && s.Name.ToLower().Contains("core"));
            if (gpuCore != null && gpuCore.Value.HasValue)
                return (double) gpuCore.Value.Value;

            var firstTemp = gpuHardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value.HasValue);
            if (firstTemp != null)
                return (double) firstTemp.Value.Value;

            return null;
        }
        private static double? GetGpuHotSpot()
        {
            if (gpuHardware == null) return null;

            // Match "hot spot", "hotspot", or "junction" temperature sensors
            bool IsHotspotSensor(ISensor s) =>
                s.SensorType == SensorType.Temperature && s.Name != null &&
                (s.Name.ToLower().Contains("hot spot") ||
                 s.Name.ToLower().Contains("hotspot") ||
                 s.Name.ToLower().Contains("junction"));

            var sensor = gpuHardware.Sensors.FirstOrDefault(IsHotspotSensor);
            if (sensor?.Value.HasValue == true)
                return (double)sensor.Value.Value;

            // Some drivers expose hotspot/junction under subhardware
            foreach (var sub in gpuHardware.SubHardware)
            {
                sub.Update();
                var subSensor = sub.Sensors.FirstOrDefault(IsHotspotSensor);
                if (subSensor?.Value.HasValue == true)
                    return (double)subSensor.Value.Value;
            }
            return null;
        }
        private static readonly Dictionary<string, float> CpuMaxTemps = new Dictionary<string, float>
        {
            // Intel 14th Gen (TjMax 100°C)
            { "Intel Core i9-14900KS",  100.0f },
            { "Intel Core i9-14900K",   100.0f },
            { "Intel Core i9-14900KF",  100.0f },
            { "Intel Core i9-14900F",   100.0f },
            { "Intel Core i9-14900",    100.0f },
            { "Intel Core i7-14700K",   100.0f },
            { "Intel Core i7-14700KF",  100.0f },
            { "Intel Core i7-14700F",   100.0f },
            { "Intel Core i7-14700",    100.0f },
            { "Intel Core i5-14600K",   100.0f },
            { "Intel Core i5-14600KF",  100.0f },
            { "Intel Core i5-14600",    100.0f },
            { "Intel Core i5-14500",    100.0f },
            { "Intel Core i5-14400",    100.0f },
            { "Intel Core i5-14400F",   100.0f },
            { "Intel Core i3-14100",    100.0f },
            { "Intel Core i3-14100F",   100.0f },
            // Intel 13th Gen (TjMax 100°C)
            { "Intel Core i9-13900KS",  100.0f },
            { "Intel Core i9-13900K",   100.0f },
            { "Intel Core i9-13900KF",  100.0f },
            { "Intel Core i9-13900F",   100.0f },
            { "Intel Core i9-13900",    100.0f },
            { "Intel Core i7-13700K",   100.0f },
            { "Intel Core i7-13700KF",  100.0f },
            { "Intel Core i7-13700F",   100.0f },
            { "Intel Core i7-13700",    100.0f },
            { "Intel Core i5-13600K",   100.0f },
            { "Intel Core i5-13600KF",  100.0f },
            { "Intel Core i5-13600",    100.0f },
            { "Intel Core i5-13500",    100.0f },
            { "Intel Core i5-13400",    100.0f },
            { "Intel Core i5-13400F",   100.0f },
            { "Intel Core i3-13100",    100.0f },
            { "Intel Core i3-13100F",   100.0f },
            // Intel 12th Gen (TjMax 100°C)
            { "Intel Core i9-12900KS",  100.0f },
            { "Intel Core i9-12900K",   100.0f },
            { "Intel Core i9-12900KF",  100.0f },
            { "Intel Core i9-12900F",   100.0f },
            { "Intel Core i9-12900",    100.0f },
            { "Intel Core i7-12700K",   100.0f },
            { "Intel Core i7-12700KF",  100.0f },
            { "Intel Core i7-12700F",   100.0f },
            { "Intel Core i7-12700",    100.0f },
            { "Intel Core i5-12600K",   100.0f },
            { "Intel Core i5-12600KF",  100.0f },
            { "Intel Core i5-12600",    100.0f },
            { "Intel Core i5-12500",    100.0f },
            { "Intel Core i5-12400",    100.0f },
            { "Intel Core i5-12400F",   100.0f },
            { "Intel Core i3-12100",    100.0f },
            { "Intel Core i3-12100F",   100.0f },
            // Intel 11th Gen (TjMax 100°C)
            { "Intel Core i9-11900K",   100.0f },
            { "Intel Core i9-11900KF",  100.0f },
            { "Intel Core i9-11900F",   100.0f },
            { "Intel Core i9-11900",    100.0f },
            { "Intel Core i7-11700K",   100.0f },
            { "Intel Core i7-11700KF",  100.0f },
            { "Intel Core i7-11700F",   100.0f },
            { "Intel Core i7-11700",    100.0f },
            { "Intel Core i5-11600K",   100.0f },
            { "Intel Core i5-11600KF",  100.0f },
            { "Intel Core i5-11600",    100.0f },
            { "Intel Core i5-11400",    100.0f },
            { "Intel Core i5-11400F",   100.0f },
            { "Intel Core i3-11100",    100.0f },
            // Intel 10th Gen (TjMax 100°C)
            { "Intel Core i9-10900K",   100.0f },
            { "Intel Core i9-10900KF",  100.0f },
            { "Intel Core i9-10900F",   100.0f },
            { "Intel Core i9-10900",    100.0f },
            { "Intel Core i7-10700K",   100.0f },
            { "Intel Core i7-10700KF",  100.0f },
            { "Intel Core i7-10700F",   100.0f },
            { "Intel Core i7-10700",    100.0f },
            { "Intel Core i5-10600K",   100.0f },
            { "Intel Core i5-10600KF",  100.0f },
            { "Intel Core i5-10600",    100.0f },
            { "Intel Core i5-10500",    100.0f },
            { "Intel Core i5-10400",    100.0f },
            { "Intel Core i5-10400F",   100.0f },
            { "Intel Core i3-10100",    100.0f },
            { "Intel Core i3-10100F",   100.0f },
            // Intel 9th Gen (TjMax 100°C)
            { "Intel Core i9-9900KS",   100.0f },
            { "Intel Core i9-9900K",    100.0f },
            { "Intel Core i9-9900KF",   100.0f },
            { "Intel Core i9-9900",     100.0f },
            { "Intel Core i7-9700K",    100.0f },
            { "Intel Core i7-9700KF",   100.0f },
            { "Intel Core i7-9700",     100.0f },
            { "Intel Core i5-9600K",    100.0f },
            { "Intel Core i5-9600KF",   100.0f },
            { "Intel Core i5-9600",     100.0f },
            { "Intel Core i5-9500",     100.0f },
            { "Intel Core i5-9400",     100.0f },
            { "Intel Core i5-9400F",    100.0f },
            { "Intel Core i3-9350K",    100.0f },
            { "Intel Core i3-9100",     100.0f },
            { "Intel Core i3-9100F",    100.0f },
            // Intel 8th Gen (TjMax 100°C)
            { "Intel Core i7-8700K",    100.0f },
            { "Intel Core i7-8700",     100.0f },
            { "Intel Core i7-8086K",    100.0f },
            { "Intel Core i5-8600K",    100.0f },
            { "Intel Core i5-8600",     100.0f },
            { "Intel Core i5-8500",     100.0f },
            { "Intel Core i5-8400",     100.0f },
            { "Intel Core i3-8350K",    100.0f },
            { "Intel Core i3-8100",     100.0f },
            // Intel 7th Gen (TjMax 100°C)
            { "Intel Core i7-7700K",    100.0f },
            { "Intel Core i7-7700",     100.0f },
            { "Intel Core i5-7600K",    100.0f },
            { "Intel Core i5-7600",     100.0f },
            { "Intel Core i5-7500",     100.0f },
            { "Intel Core i5-7400",     100.0f },
            { "Intel Core i3-7350K",    100.0f },
            { "Intel Core i3-7100",     100.0f },
            // Intel 6th Gen (TjMax 100°C)
            { "Intel Core i7-6700K",    100.0f },
            { "Intel Core i7-6700",     100.0f },
            { "Intel Core i5-6600K",    100.0f },
            { "Intel Core i5-6600",     100.0f },
            { "Intel Core i5-6500",     100.0f },
            { "Intel Core i5-6400",     100.0f },
            { "Intel Core i3-6320",     100.0f },
            { "Intel Core i3-6100",     100.0f },
            // AMD Ryzen 7000 X3D (TjMax 89°C)
            { "AMD Ryzen 9 7950X3D",     89.0f },
            { "AMD Ryzen 9 7900X3D",     89.0f },
            { "AMD Ryzen 7 7800X3D",     89.0f },
            // AMD Ryzen 7000 (TjMax 95°C)
            { "AMD Ryzen 9 7950X",       95.0f },
            { "AMD Ryzen 9 7900X",       95.0f },
            { "AMD Ryzen 9 7900",        95.0f },
            { "AMD Ryzen 7 7700X",       95.0f },
            { "AMD Ryzen 7 7700",        95.0f },
            { "AMD Ryzen 5 7600X",       95.0f },
            { "AMD Ryzen 5 7600",        95.0f },
            // AMD Ryzen 5000
            { "AMD Ryzen 9 5950X",       90.0f },
            { "AMD Ryzen 9 5900X",       90.0f },
            { "AMD Ryzen 9 5900",        90.0f },
            { "AMD Ryzen 7 5800X3D",     90.0f },
            { "AMD Ryzen 7 5800X",       90.0f },
            { "AMD Ryzen 7 5800",        90.0f },
            { "AMD Ryzen 7 5700X",       95.0f },
            { "AMD Ryzen 7 5700G",       95.0f },
            { "AMD Ryzen 5 5600X",       95.0f },
            { "AMD Ryzen 5 5600G",       95.0f },
            { "AMD Ryzen 5 5600",        95.0f },
            { "AMD Ryzen 5 5500",        95.0f },
            { "AMD Ryzen 5 5300G",       95.0f },
            { "AMD Ryzen 3 5100",        95.0f },
            // AMD Ryzen 3000
            { "AMD Ryzen 9 3950X",       95.0f },
            { "AMD Ryzen 9 3900XT",      95.0f },
            { "AMD Ryzen 9 3900X",       95.0f },
            { "AMD Ryzen 9 3900",        95.0f },
            { "AMD Ryzen 7 3800XT",      95.0f },
            { "AMD Ryzen 7 3800X",       95.0f },
            { "AMD Ryzen 7 3700X",       95.0f },
            { "AMD Ryzen 5 3600XT",      95.0f },
            { "AMD Ryzen 5 3600X",       95.0f },
            { "AMD Ryzen 5 3600",        95.0f },
            { "AMD Ryzen 5 3400G",       95.0f },
            { "AMD Ryzen 5 3400GE",      95.0f },
            { "AMD Ryzen 3 3300X",       95.0f },
            { "AMD Ryzen 3 3200G",       95.0f },
            { "AMD Ryzen 3 3200GE",      95.0f },
            { "AMD Ryzen 3 3100",        95.0f },
            // AMD Ryzen 2000
            { "AMD Ryzen 7 2700X",       85.0f },
            { "AMD Ryzen 7 2700",        85.0f },
            { "AMD Ryzen 5 2600X",       95.0f },
            { "AMD Ryzen 5 2600",        95.0f },
            { "AMD Ryzen 5 2400G",       95.0f },
            { "AMD Ryzen 5 2400GE",      95.0f },
            { "AMD Ryzen 3 2300X",       95.0f },
            { "AMD Ryzen 3 2200G",       95.0f },
            { "AMD Ryzen 3 2200GE",      95.0f },
            // AMD Ryzen 1000
            { "AMD Ryzen 7 1800X",       95.0f },
            { "AMD Ryzen 7 1700X",       95.0f },
            { "AMD Ryzen 7 1700",        95.0f },
            { "AMD Ryzen 5 1600X",       95.0f },
            { "AMD Ryzen 5 1600 AF",     95.0f },
            { "AMD Ryzen 5 1600",        95.0f },
            { "AMD Ryzen 5 1500X",       95.0f },
            { "AMD Ryzen 5 1400",        95.0f },
            { "AMD Ryzen 3 1300X",       95.0f },
            { "AMD Ryzen 3 1200",        95.0f },
        };

        private static readonly Dictionary<string, float> GpuMaxTemps = new Dictionary<string, float>
        {
            // NVIDIA RTX 50 Series
            { "NVIDIA GeForce RTX 5090",          90.0f },
            { "NVIDIA GeForce RTX 5080",          88.0f },
            { "NVIDIA GeForce RTX 5070 Ti",       88.0f },
            { "NVIDIA GeForce RTX 5070",          85.0f },
            { "NVIDIA GeForce RTX 5060 Ti",       90.0f },
            { "NVIDIA GeForce RTX 5060",          90.0f },
            // NVIDIA RTX 40 Series
            { "NVIDIA GeForce RTX 4090",          90.0f },
            { "NVIDIA GeForce RTX 4080 Super",    90.0f },
            { "NVIDIA GeForce RTX 4080",          90.0f },
            { "NVIDIA GeForce RTX 4070 Ti Super", 90.0f },
            { "NVIDIA GeForce RTX 4070 Ti",       90.0f },
            { "NVIDIA GeForce RTX 4070 Super",    90.0f },
            { "NVIDIA GeForce RTX 4070",          90.0f },
            { "NVIDIA GeForce RTX 4060 Ti",       90.0f },
            { "NVIDIA GeForce RTX 4060",          90.0f },
            // NVIDIA RTX 30 Series
            { "NVIDIA GeForce RTX 3090 Ti",       92.0f },
            { "NVIDIA GeForce RTX 3090",          92.0f },
            { "NVIDIA GeForce RTX 3080 Ti",       93.0f },
            { "NVIDIA GeForce RTX 3080 12GB",     93.0f },
            { "NVIDIA GeForce RTX 3080",          93.0f },
            { "NVIDIA GeForce RTX 3070 Ti",       93.0f },
            { "NVIDIA GeForce RTX 3070",          93.0f },
            { "NVIDIA GeForce RTX 3060 Ti",       93.0f },
            { "NVIDIA GeForce RTX 3060",          93.0f },
            { "NVIDIA GeForce RTX 3050",          93.0f },
            // NVIDIA RTX 20 Series
            { "NVIDIA GeForce RTX 2080 Ti",       89.0f },
            { "NVIDIA GeForce RTX 2080 Super",    89.0f },
            { "NVIDIA GeForce RTX 2080",          89.0f },
            { "NVIDIA GeForce RTX 2070 Super",    89.0f },
            { "NVIDIA GeForce RTX 2070",          89.0f },
            { "NVIDIA GeForce RTX 2060 Super",    89.0f },
            { "NVIDIA GeForce RTX 2060",          89.0f },
            // NVIDIA GTX 16 Series
            { "NVIDIA GeForce GTX 1660 Ti",       93.0f },
            { "NVIDIA GeForce GTX 1660 Super",    93.0f },
            { "NVIDIA GeForce GTX 1660",          93.0f },
            { "NVIDIA GeForce GTX 1650 Super",    90.0f },
            { "NVIDIA GeForce GTX 1650",          90.0f },
            // NVIDIA GTX 10 Series
            { "NVIDIA GeForce GTX 1080 Ti",       91.0f },
            { "NVIDIA GeForce GTX 1080",          94.0f },
            { "NVIDIA GeForce GTX 1070 Ti",       94.0f },
            { "NVIDIA GeForce GTX 1070",          94.0f },
            { "NVIDIA GeForce GTX 1060",          94.0f },
            { "NVIDIA GeForce GTX 1050 Ti",       97.0f },
            { "NVIDIA GeForce GTX 1050",          97.0f },
            // AMD Radeon RX 7000 Series
            { "AMD Radeon RX 7900 XTX",          110.0f },
            { "AMD Radeon RX 7900 XT",           110.0f },
            { "AMD Radeon RX 7900 GRE",          110.0f },
            { "AMD Radeon RX 7800 XT",           110.0f },
            { "AMD Radeon RX 7700 XT",           110.0f },
            { "AMD Radeon RX 7700",              110.0f },
            { "AMD Radeon RX 7600 XT",           110.0f },
            { "AMD Radeon RX 7600",              110.0f },
            // AMD Radeon RX 6000 Series
            { "AMD Radeon RX 6950 XT",           110.0f },
            { "AMD Radeon RX 6900 XT",           110.0f },
            { "AMD Radeon RX 6800 XT",           110.0f },
            { "AMD Radeon RX 6800",              110.0f },
            { "AMD Radeon RX 6750 XT",           110.0f },
            { "AMD Radeon RX 6700 XT",           110.0f },
            { "AMD Radeon RX 6700",              110.0f },
            { "AMD Radeon RX 6650 XT",           110.0f },
            { "AMD Radeon RX 6600 XT",           110.0f },
            { "AMD Radeon RX 6600",              110.0f },
            { "AMD Radeon RX 6500 XT",           110.0f },
            { "AMD Radeon RX 6500",              110.0f },
            { "AMD Radeon RX 6400",              110.0f },
            // AMD Radeon RX 5000 Series
            { "AMD Radeon RX 5700 XT",           110.0f },
            { "AMD Radeon RX 5700",              110.0f },
            { "AMD Radeon RX 5600 XT",           110.0f },
            { "AMD Radeon RX 5600",              110.0f },
            { "AMD Radeon RX 5500 XT",           110.0f },
            { "AMD Radeon RX 5500",              110.0f },
            { "AMD Radeon RX 5300 XT",           110.0f },
            { "AMD Radeon RX 5300",              110.0f },
            { "AMD Radeon RX 5200",              110.0f },
            // AMD Radeon RX Vega Series
            { "AMD Radeon RX Vega 64",           105.0f },
            { "AMD Radeon RX Vega 56",           105.0f },
        };

        internal static bool IsCpuNameInDictionary(string name) =>
            !string.IsNullOrEmpty(name) && CpuMaxTemps.ContainsKey(name);

        internal static bool IsGpuNameInDictionary(string name) =>
            !string.IsNullOrEmpty(name) && GpuMaxTemps.ContainsKey(name);

        internal static float GetCpuMaximum(string CpuName)
        {
            if (string.IsNullOrEmpty(CpuName)) return 100.0f;
            if (CpuMaxTemps.TryGetValue(CpuName, out float v)) return v;
            if (CpuName.Contains("Intel")) return 100.0f;
            return 95.0f;
        }

        internal static float GetGpuMaximum(string GpuName)
        {
            if (string.IsNullOrEmpty(GpuName)) return 92.0f;
            if (GpuMaxTemps.TryGetValue(GpuName, out float v)) return v;
            if (GpuName.Contains("AMD Radeon")) return 110.0f;
            return 92.0f;
        }

        // Auto critical = TJMax rounded down to the nearest 5°C below TJMax-3
        // e.g. TJMax 93 → 90, TJMax 100 → 95, TJMax 110 → 105
        internal static float GetCpuAutoCritical(string name) =>
            (float)(Math.Floor((GetCpuMaximum(name) - 3f) / 5f) * 5f);

        internal static float GetCpuAutoWarning(string name) =>
            GetCpuAutoCritical(name) - 10f;

        internal static float GetGpuAutoCritical(string name) =>
            GetGpuMaximum(name) - 4f;

        internal static float GetGpuAutoWarning(string name) =>
            GetGpuMaximum(name) - 13f;
    }
}
