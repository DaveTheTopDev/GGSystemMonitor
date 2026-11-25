using LibreHardwareMonitor.Hardware;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using static GGSystemMonitor.Program;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace GGSystemMonitor
{
    internal partial class Program
    {
        // Configuration
        private const string APP_NAME = "CUSTOM_SYSTEM_MONITOR";
        private const int DEFAULT_SCREEN_COLS = 15;
        private const int OLED_IMAGE_WIDTH = 128;
        private static int OLED_IMAGE_HEIGHT => settings?.OutputProfile == "arctis" ? 48 : settings?.OutputProfile == "gamedac" ? 52 : 40;
        private const string DEFAULT_CAPS_LOCK_INDICATOR = "🡅";
        private const string TEMPERATURE_WARNING_INDICATOR = "⚠";
        private const string TEMPERATURE_CRITICAL_INDICATOR = "🔥";
        private const string OLED_WIDE_TEXT_GLYPHS = "▶⏸♫♪⏰📅";
        private const int OLED_FONT_MEASURE_CACHE_LIMIT = 512;
        private const int SCREEN_COLS = DEFAULT_SCREEN_COLS;
        private const int OLED_UPDATE_INTERVAL_MS = 250;
        private const string EVENT_NAME = "SYS_MONITOR";
        internal const string VERSION = "3.3.0";
        private const string GITHUB_REPO = "DaveTheTopDev/GGSystemMonitor";
        private static HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private static readonly HttpClient GgHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        private static readonly string MonitorStatusFilePath =
            Path.Combine(AppContext.BaseDirectory, "monitor_status.json");
        private static int[] OledBlankImageData => new int[OLED_IMAGE_WIDTH * OLED_IMAGE_HEIGHT / 8];
        private static readonly object OledFontMeasureLock = new object();
        private static readonly Dictionary<(Font, string, bool, bool), float> OledFontAdvanceCache =
            new Dictionary<(Font, string, bool, bool), float>();
        private static Computer _computer;
        private static IHardware cpuHardware;
        private static IHardware gpuHardware;
        private static string baseUrl = null;
        private static bool _ggConnected = true;
        private static int _reconnectCooldownMs = 0;
        private static int updateValue;
        private static int screenUpdate;
        private static int _blinkPhase;
        private static bool capsLockToggled;
        internal static volatile string AvailableVersion = null;
        private  static int _updateNotifCycle = 0;
        // Per-location weather cache
        private class WeatherData
        {
            public double? TempC, FeelsLikeC, Humidity, WindKmh;
            public string Condition, City;
        }
        private static readonly Dictionary<string, WeatherData> _weatherByLocation
            = new Dictionary<string, WeatherData>(StringComparer.OrdinalIgnoreCase);
        private static int _weatherRefreshMs = 0;
        private static List<(string Code, string Province, string NameEn)> _ecSites = null;
        private static readonly Dictionary<string, string> _provinceMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Alberta", "AB" }, { "British Columbia", "BC" }, { "Manitoba", "MB" },
            { "New Brunswick", "NB" }, { "Newfoundland and Labrador", "NL" }, { "Newfoundland", "NL" },
            { "Northwest Territories", "NT" }, { "Nova Scotia", "NS" }, { "Nunavut", "NU" },
            { "Ontario", "ON" }, { "Prince Edward Island", "PE" }, { "Quebec", "QC" },
            { "Québec", "QC" }, { "Saskatchewan", "SK" }, { "Yukon", "YT" }
        };
        // Now Playing cache
        private static string  _nowPlayingTitle   = null;
        private static string  _nowPlayingArtist  = null;
        private static string  _nowPlayingAlbum   = null;
        private static string  _nowPlayingAppId   = null;
        private static bool      _nowPlayingIsPaused  = false;
        private static TimeSpan? _nowPlayingPosition  = null;
        private static TimeSpan? _nowPlayingDuration  = null;
        private static int       _nowPlayingPollMs    = 0;
        private static int       _nowPlayingPollInProgress = 0;
        private static int       _memoryMaintenanceMs = 60 * 1000;
        private static Font      _oledFont            = null;
        private static Font _nativeBitmapFont;
        private static readonly Dictionary<(string, int), Font> FrameFonts = new Dictionary<(string, int), Font>();
        private static bool _bitmapFrame;
        private static bool _bindingReady;
        private static long _nextBindAttempt;
        private static OledFrameBuffer _oledFrameBuffer;
        private static bool    _smtcLoggedError    = false;
        private static bool    _weatherLoggedError = false;
        private static DateTime _nowPlayingPositionTime = DateTime.MinValue;
        private static string  _nowPlayingSongKey   = null;
        private static volatile bool _hasWeatherWidget;
        private static volatile bool _hasNowPlayingWidget;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);
        [System.Runtime.InteropServices.DllImport("psapi.dll")]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);
        private static bool IsCapsLockOn() => (GetKeyState(0x14) & 1) != 0;

        private static volatile AppSettings settings;
        private static readonly Regex coreSensorRegex = new Regex(@"core #\d+", RegexOptions.Compiled);
        private static double? cachedCpuTemp;
        private static double? cachedCpuUsage;
        private static double? cachedRamUsed;
        private static double? cachedRamTotal;
        private static IHardware memoryHardware;
        [System.STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0].Equals("--settings", StringComparison.OrdinalIgnoreCase))
            {
                RunSettingsWindow();
                return;
            }

            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            {
                try
                {
                    string exePath;
                    using (var current = Process.GetCurrentProcess())
                        exePath = current.MainModule.FileName;
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exePath,
                        UseShellExecute = true,
                        Verb = "runas"
                    });
                    return; // Elevated instance launched - Exit this one
                }
                catch { /* User declined UAC - Continue without admin (CPU temp will read N/A) */ }
            }

            if (!AcquireMonitorInstance()) return;
            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                try { _computer?.Close(); } catch { }
            };
            Microsoft.Win32.SystemEvents.SessionEnding += (s, e) =>
            {
                try { _stopSignal?.Set(); } catch { }
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
                try { _computer.Open(); } catch (Exception ex) { _sensorError = ex.GetType().Name + ": " + ex.Message; }

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

                memoryHardware = _computer.Hardware.Where(h => h.HardwareType == HardwareType.Memory).OrderBy(h => h.Name.IndexOf("virtual", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0).FirstOrDefault();
                gpuHardware = ChooseGpu(_computer.Hardware);

                // Independent failures must not prevent the settings inventory from being published.
                PollHardware();
                try { WriteHardwareInventory(); } catch (Exception ex) { AppendLog("Hardware inventory: " + ex.Message + Environment.NewLine); }

                // --- Initialize Settings.json file ---
                settings = SettingsManager.LoadSettings();
                UpdateOledFont();
                RefreshWidgetFlags();

                FileSystemWatcher watcher = new FileSystemWatcher(AppContext.BaseDirectory, "settings.json");
                watcher.Changed += (sender, change) => _reloadSettings = true;
                watcher.Created += (sender, change) => _reloadSettings = true;
                watcher.Renamed += (sender, change) => _reloadSettings = true;
                watcher.EnableRaisingEvents = true;

                // Wait for GG Engine to be available (polls until coreProps.json is present and readable)
                while (baseUrl == null && !MonitorStopRequested())
                {
                    PollHardware();
                    cachedCpuTemp = ReadTemperature(cpuHardware, null);
                    WriteMonitorStatus();
                    GetGGAddress();
                    if (baseUrl == null) Thread.Sleep(2000);
                }

                // --- Register app/events with SteelSeries GameSense ---
                RegisterApp();
                RegisterEvent();
                BindEvent();
                if (_bindingReady)
                {
                    if (_bitmapFrame)
                    {
                        SendToOledImage("GGSystemMonitor", "Waiting...", _oledFont, _oledFont, "", "");
                    }
                    else SendToOled("GGSystemMonitor", "Waiting...");
                }
                StartCapsLockWatcher();

                // Check GitHub for a newer release only when OLED update notifications are enabled.
                if (settings.ShowUpdateNotifications)
                    Task.Run(() => CheckForUpdates());

                //Console.WriteLine("GGSystemMonitor started. Updating every 2 seconds. Press Ctrl+C to stop.");

                int _cpuPollMs = 0;
                int _gpuPollMs = 0;
                screenUpdate = OLED_UPDATE_INTERVAL_MS;
                var tickSw = System.Diagnostics.Stopwatch.StartNew();

                // Main loop
                while (!MonitorStopRequested())
                {
                    try
                    {
                        ApplyPendingSettings();
                        // Measure actual elapsed time so all timers run at real wall-clock speed
                        // regardless of Thread.Sleep precision (~15ms on Windows default timer resolution)
                        int tickMs = (int)tickSw.ElapsedMilliseconds;
                        tickSw.Restart();
                        if (tickMs < 1)   tickMs = 1;
                        if (tickMs > 200) tickMs = 10; // guard against system suspend/resume

                        _cpuPollMs -= tickMs;
                        if (_cpuPollMs <= 0)
                        {
                            int cpuInterval = GetMinPollInterval("cputemperature");
                            _cpuPollMs = cpuInterval;
                            PollHardware();
                            cpuHardware = ResolveHardware(null, true);
                            gpuHardware = ChooseGpu(_computer.Hardware);
                            cachedCpuTemp = ReadTemperature(cpuHardware, null);
                            try { cachedCpuUsage = GetCpuUsage(); } catch { cachedCpuUsage = null; }
                            try { (cachedRamUsed, cachedRamTotal) = GetRamUsage(); }
                            catch { cachedRamUsed = null; cachedRamTotal = null; }
                            WidgetReadings.Clear();
                            WriteMonitorStatus();
                        }

                        _gpuPollMs -= tickMs;
                        if (_gpuPollMs <= 0)
                        {
                            int gpuInterval = GetMinPollInterval("gputemperature");
                            _gpuPollMs = gpuInterval;
                            PollHardware();
                            WidgetReadings.Clear();

                            PollThermalAlerts(settings, RotationClock.ElapsedMilliseconds);
                        }

                        // Weather refresh (every 15 minutes; only when a weather widget is configured)
                        if (_hasWeatherWidget)
                        {
                            _weatherRefreshMs -= tickMs;
                            if (_weatherRefreshMs <= 0)
                            {
                                _weatherRefreshMs = 15 * 60 * 1000;
                                Task.Run(() => RefreshAllWeather());
                            }
                        }

                        // Now Playing poll (only when configured; WinRT media APIs add memory overhead)
                        if (_hasNowPlayingWidget)
                        {
                            _nowPlayingPollMs -= tickMs;
                            if (_nowPlayingPollMs <= 0)
                            {
                                _nowPlayingPollMs = 1000;
                                if (Interlocked.CompareExchange(ref _nowPlayingPollInProgress, 1, 0) == 0)
                                {
                                    Task.Run(() =>
                                    {
                                        try { PollNowPlaying(); }
                                        finally { Interlocked.Exchange(ref _nowPlayingPollInProgress, 0); }
                                    });
                                }
                            }
                        }
                        else
                        {
                            _nowPlayingPollMs = 1000;
                            ClearNowPlaying();
                        }

                        if (AvailableVersion != null && settings.ShowUpdateNotifications)
                            _updateNotifCycle = (_updateNotifCycle + tickMs) % 13000;
                        else
                            _updateNotifCycle = 0;

                        if (!_ggConnected)
                        {
                            _reconnectCooldownMs -= tickMs;
                            if (_reconnectCooldownMs <= 0)
                            {
                                GetGGAddress();
                                if (baseUrl != null)
                                {
                                    RegisterApp();
                                    RegisterEvent();
                                    BindEvent();
                                    if (baseUrl != null)
                                    {
                                        _ggConnected = true;
                                        AppendLog( DateTime.Now + " - GG Engine reconnected." + Environment.NewLine);
                                    }
                                    else
                                    {
                                        _reconnectCooldownMs = 2000;
                                    }
                                }
                                else
                                {
                                    _reconnectCooldownMs = 2000;
                                }
                            }
                        }

                        screenUpdate -= tickMs;
                        if (screenUpdate <= 0)
                        {
                            long now = RotationClock.ElapsedMilliseconds;
                            _frameMedia = _mediaActivity;
                            Rotation.Update(settings, now, LiveRules, _frameMedia?.Key);
                            _blinkPhase = (_blinkPhase + 1) & 3;
                            if (_ggConnected)
                                BuildAndSendDisplay(now);
                            screenUpdate = OLED_UPDATE_INTERVAL_MS;
                        }

                        _memoryMaintenanceMs -= tickMs;
                        if (_memoryMaintenanceMs <= 0)
                        {
                            _memoryMaintenanceMs = 60 * 1000;
                            RunMemoryMaintenance();
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log exceptions to file for troubleshooting
                        AppendLog( DateTime.Now + " - Error: " + ex + Environment.NewLine);
                    }
                    Thread.Sleep(10);
                }
            }
            catch (Exception ex)
            {
                AppendLog( DateTime.Now + " - Fatal: " + ex + Environment.NewLine);
            }
            finally
            {
                try { PostJsonText("/stop_game", "{\"game\":\"" + APP_NAME + "\"}"); } catch { }
                try { _computer?.Close(); } catch { }
                ReleaseFrameFonts();
                _monitorMutex?.Dispose();
                _stopSignal?.Dispose();
            }
        }
        public class AppSettings
        {
            public const int CurrentSettingsVersion = 3;
            public int SettingsVersion { get; set; } = 0;
            public string GGEngineCorePropsPath { get; set; } = @"C:/ProgramData/SteelSeries/SteelSeries Engine 3/coreProps.json";
            public bool ShowCapsLockIndicator { get; set; } = true;
            public bool ShowUpdateNotifications { get; set; } = true;
            [System.Text.Json.Serialization.JsonConverter(typeof(LineItemListConverter))]
            public List<LineItem> TopLineItems { get; set; } = new List<LineItem>
            {
                DefaultTemperatureWidget("CpuTemperature", "CPU: {temp:F1}°C"),
                new LineItem { Type = "CpuUsage", Label = "CPU USE: {pct:F0}%", VisibilityThreshold = 70 },
                new LineItem { Type = "RamUsage", Label = "RAM: {used:0.#}/{total:0.#}GB", VisibilityThreshold = 70 },
            };
            [System.Text.Json.Serialization.JsonConverter(typeof(LineItemListConverter))]
            public List<LineItem> BottomLineItems { get; set; } = new List<LineItem>
            {
                DefaultTemperatureWidget("GpuTemperature", "GPU: {temp:F1}°C"),
                new LineItem { Type = "GpuUsage", Label = "GPU USE: {pct:F0}%", VisibilityThreshold = 75 },
            };
            public int RotationIntervalMs { get; set; } = 3000;
            public bool RotateLinesTogether { get; set; } = true;
            public string ActiveDisplayPresetId { get; set; } = DisplayPreset.DefaultId;
            public List<DisplayPreset> DisplayPresets { get; set; } = new List<DisplayPreset>();
            public string OledFont        { get; set; } = "";
            public string OutputProfile { get; set; } = "keyboard";

            private static LineItem DefaultTemperatureWidget(string type, string label) => new LineItem
            {
                Type = type,
                Label = label,
                PriorityMode = (int)WidgetPriority.Threshold,
                // Follow this widget's hardware-derived warning value, not the
                // value displayed on the PC where the default was authored.
                WarnTemp = "AUTO",
                CritTemp = "AUTO",
                PriorityThreshold = null
            };
        }
        public static class SettingsManager
        {
            private static readonly string SettingsFilePath =
                Path.Combine(AppContext.BaseDirectory, "settings.json");

            public static AppSettings LoadSettings() => LoadFrom(SettingsFilePath);

            internal static AppSettings LoadFrom(string path)
            {
                if (!File.Exists(path)) WriteFile(path, GetDefaultSettingsJson());
                string fileJson = File.ReadAllText(path);
                AppSettings appSettings = JsonSerializer.Deserialize<AppSettings>(fileJson) ?? new AppSettings();
                if (appSettings.SettingsVersion < AppSettings.CurrentSettingsVersion)
                    Migrate(appSettings, fileJson, path);
                DisplayPreset.Resolve(appSettings).Project(appSettings);
                return appSettings;
            }

            internal static void WriteFile(string path, string json)
            {
                // Publish a complete JSON document so the background monitor's
                // settings watcher never sees a partially written preset.
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, json);
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            private static string GetDefaultSettingsJson()
            {
                var document = JObject.Parse(@"{
  ""DisplayPresets"": [],

  ""GGEngineCorePropsPath"": ""C:/ProgramData/SteelSeries/SteelSeries Engine 3/coreProps.json"",
  ""_note_GGEngineCorePropsPath"": ""Only change if GG Engine is installed in a non-default location."",

  ""ShowCapsLockIndicator"": true,
  ""_note_ShowCapsLockIndicator"": ""Show a 🡅 icon on the top-right of the display when Caps Lock is active."",

  ""ShowUpdateNotifications"": true,
  ""_note_ShowUpdateNotifications"": ""When a newer version is found, display a notification on the keyboard OLED (Update / Available) for 3 seconds every 10 seconds."",

  ""_note_TopLineItems"": ""Items for the top display line. For CPU/GPU temperature, a null PriorityThreshold follows the widget's warning temperature."",
  ""_note_BottomLineItems"": ""Items for the bottom display line. Same format as TopLineItems."",

  ""_note_RotationIntervalMs"": ""Fallback display duration when a widget's DurationMs is 0. Default: 3000 (3 seconds).""
}");
                document["SettingsVersion"] = AppSettings.CurrentSettingsVersion;
                document["ActiveDisplayPresetId"] = DisplayPreset.DefaultId;
                DisplayLayout.Factory().WriteProjection(document);
                return document.ToString(Formatting.Indented);
            }

            private static void Migrate(AppSettings settings, string originalJson, string path)
            {
                try
                {
                    var jo = JObject.Parse(originalJson);
                    DisplayPreset.UpgradeLegacy(settings, jo);
                    settings.SettingsVersion = AppSettings.CurrentSettingsVersion;
                    jo["SettingsVersion"] = settings.SettingsVersion;
                    WriteFile(path, jo.ToString(Formatting.Indented));
                }
                catch { }
            }
        }
        public class LineItem
        {
            public string Type { get; set; } = "CpuTemperature";
            public bool Hidden { get; set; }
            public string Label { get; set; } = null;
            public int DurationMs { get; set; } = 0;
            public bool UseFahrenheit { get; set; } = false;
            public bool UseRamMb { get; set; } = false;
            public string NotPlayingText { get; set; } = "Not Playing";
            public bool SkipIfNotPlaying { get; set; } = true;
            public bool NowPlayingSpotify     { get; set; } = true;
            public bool NowPlayingYouTube     { get; set; } = true;
            public bool NowPlayingOtherPlayer { get; set; } = true;
            // Per-widget type-specific settings
            public int PollIntervalMs { get; set; } = 2000;
            public bool EnableWarnIndicators { get; set; } = true;
            public string WarnTemp { get; set; } = "AUTO";
            public string CritTemp { get; set; } = "AUTO";
            public string SensorOverride { get; set; } = null;
            public string HardwareId { get; set; } = "";
            public bool GpuPasteMonitoring { get; set; } = true;
            public int GpuPasteGapTemp { get; set; } = 15;
            public string WeatherLocation { get; set; } = "";
            public string FontFamily { get; set; } = "";
            public int FontSizeStep { get; set; }
            public int PriorityMode { get; set; }
            public int PriorityRank { get; set; } = 1;
            public double? PriorityThreshold { get; set; }
            public int RamPriorityUnit { get; set; }
            public double? VisibilityThreshold { get; set; }
            public bool VisibilityAtOrBelow { get; set; }

            internal LineItem Copy() => (LineItem)MemberwiseClone();

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
                        case "weather":        baseName = UseFahrenheit ? "Weather (°F)" : "Weather (°C)"; break;
                        case "nowplaying":     baseName = "Now Playing"; break;
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
        // Returns the minimum PollIntervalMs across all active items of the given type (default 2000).
        private static int GetMinPollInterval(string typeNorm)
        {
            int min = int.MaxValue;
            foreach (var list in new[] { settings?.TopLineItems, settings?.BottomLineItems })
            {
                if (list == null) continue;
                foreach (var item in list)
                {
                    if (IsLineItemType(item, typeNorm) && !item.Hidden && item.PollIntervalMs > 0)
                        min = Math.Min(min, item.PollIntervalMs);
                }
            }
            return min == int.MaxValue ? 2000 : min;
        }

        // Returns the SensorOverride from the first item of the given type found in top then bottom list.
        private static string GetFirstSensorOverride(string typeNorm)
        {
            foreach (var list in new[] { settings?.TopLineItems, settings?.BottomLineItems })
            {
                if (list == null) continue;
                foreach (var item in list)
                {
                    if (IsLineItemType(item, typeNorm) && !item.Hidden && !string.IsNullOrEmpty(item.SensorOverride))
                        return item.SensorOverride;
                }
            }
            return null;
        }

        // Resolves a WarnTemp/CritTemp string value ("AUTO" or number) to a float threshold.
        private static float GetThresholdValue(string setting, float autoDefault)
        {
            if (string.IsNullOrEmpty(setting) || setting.Equals("AUTO", StringComparison.OrdinalIgnoreCase))
                return autoDefault;
            if (float.TryParse(setting, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out float v) && !float.IsNaN(v) && !float.IsInfinity(v))
                return v;
            return autoDefault;
        }

        private static void GetGGAddress()
        {
            if (!File.Exists(settings.GGEngineCorePropsPath))
            {
                AppendLog( DateTime.Now + $" - coreProps.json not found at {settings.GGEngineCorePropsPath}. Please verify SteelSeries GG installation path and update in the settings.json file!" + Environment.NewLine);
                return;
            }
            string coreText = File.ReadAllText(settings.GGEngineCorePropsPath);
            try
            {
                var j = JObject.Parse(coreText);
                var addr = j["address"]?.ToString();
                if (string.IsNullOrEmpty(addr))
                {
                    AppendLog( DateTime.Now + " - Could not find 'address' field in coreProps.json" + Environment.NewLine);
                    return;
                }
                // Validate host is loopback before trusting it, to reject tampered coreProps.json.
                var host = addr.Contains(':') ? addr.Substring(0, addr.LastIndexOf(':')) : addr;
                if (!IPAddress.TryParse(host, out var ip) || !IPAddress.IsLoopback(ip))
                {
                    AppendLog( DateTime.Now + " - coreProps.json address is not a loopback address; refusing to connect: " + addr + Environment.NewLine);
                    return;
                }
                baseUrl = "http://" + addr;
            }
            catch (Exception ex)
            {
                AppendLog( DateTime.Now + " - Failed to parse coreProps.json: " + ex.Message + Environment.NewLine);
                return;
            }
        }
        private static void RegisterApp()
        {
            var meta = new JObject(
                new JProperty("game", APP_NAME),
                new JProperty("game_display_name", "GG System Monitor"),
                new JProperty("developer", "DaveTheTopDev"),
                new JProperty("icon_color_id", 7)
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
            JObject datasEntry = _bitmapFrame
                ? new JObject(
                    new JProperty("has-text", false),
                    new JProperty("image-data", new JArray(OledBlankImageData)))
                : new JObject(
                    new JProperty("lines", new JArray(
                        new JObject(new JProperty("has-text", true), new JProperty("context-frame-key", "text_line_1")),
                        new JObject(new JProperty("has-text", true), new JProperty("context-frame-key", "text_line_2"))
                    )));

            var bind = new JObject(
                new JProperty("game", APP_NAME),
                new JProperty("event", EVENT_NAME),
                new JProperty("handlers", new JArray(
                    new JObject(
                        new JProperty("device-type", ScreenBindingDevice()),
                        new JProperty("zone", "one"),
                        new JProperty("mode", "screen"),
                        new JProperty("datas", new JArray(datasEntry))
                    )))
            );
            _bindingReady = PostJson("/bind_game_event", bind);
            _nextBindAttempt = RotationClock.ElapsedMilliseconds + 2000;
        }

        private static string ScreenBindingDevice() => settings?.OutputProfile == "arctis" ? "screened-128x48" :
            settings?.OutputProfile == "gamedac" ? "screened-128x52" : _bitmapFrame ? "screened-128x40" : "keyboard";
        private static string GetItemContent(LineItem lineItem, WidgetReading reading)
        {
            string type = lineItem?.Type?.ToLower().Replace(" ", "").Replace("_", "") ?? "";
            if (type == "text")
                return lineItem.Label ?? "Text";

            // Early exits for items with no data — bypass format string entirely
            if (type == "nowplaying" && _nowPlayingTitle == null && _nowPlayingArtist == null)
                return lineItem?.NotPlayingText ?? "Not Playing";
            if (type == "nowplaying" && !IsNowPlayingSourceAllowed(lineItem))
                return lineItem?.NotPlayingText ?? "Not Playing";

            if (type == "weather")
            {
                string wLoc = lineItem?.WeatherLocation ?? "";
                WeatherData wd;
                lock (_weatherByLocation) _weatherByLocation.TryGetValue(wLoc, out wd);
                if (wd == null) return "Loading...";
                if (lineItem?.Label != null) return ApplyLabelFormat(lineItem.Label, type, lineItem.UseFahrenheit, lineItem.UseRamMb, wd);
                string wIcon = GetWeatherIcon(wd.Condition, _blinkPhase);
                string wUnit = lineItem.UseFahrenheit ? "°F" : "°C";
                if (wd.TempC.HasValue)
                {
                    double wt = lineItem.UseFahrenheit ? wd.TempC.Value * 9.0 / 5.0 + 32.0 : wd.TempC.Value;
                    return $"{wIcon}{wt:F0}{wUnit} - {wd.Condition ?? ""}".TrimEnd();
                }
                return $"{wIcon}{wd.Condition ?? "No weather"}".TrimStart();
            }

            if (lineItem?.Label != null)
                return ApplyLabelFormat(lineItem.Label, type, lineItem.UseFahrenheit, lineItem.UseRamMb, null, reading.Value);

            switch (type)
            {
                case "cputemperature":
                    if (!reading.Value.HasValue) return "CPU: N/A";
                    if (lineItem.UseFahrenheit) { double f = reading.Value.Value * 9.0 / 5.0 + 32.0; return $"CPU: {f:F1}°F"; }
                    return $"CPU: {reading.Value.Value:F1}°C";
                case "cpuusage":
                    return reading.Value.HasValue ? $"CPU USE: {reading.Value.Value:F0}%" : "CPU: N/A";
                case "gputemperature":
                    if (!reading.Value.HasValue) return "GPU: N/A";
                    if (lineItem.UseFahrenheit) { double f = reading.Value.Value * 9.0 / 5.0 + 32.0; return $"GPU: {f:F1}°F"; }
                    return $"GPU: {reading.Value.Value:F1}°C";
                case "gpuusage":
                    return reading.Value.HasValue ? $"GPU USE: {reading.Value.Value:F0}%" : "GPU: N/A";
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
                case "time":
                    return "⏰" + DateTime.Now.ToString("hh:mm:ss tt");
                case "date":
                    return "📅 " + DateTime.Now.ToString("MMM dd, yyyy");
                case "nowplaying":
                    if (_nowPlayingTitle == null && _nowPlayingArtist == null) return "No media";
                    string srcIcon = GetNowPlayingSourceIcon();
                    string np = _nowPlayingArtist ?? "";
                    if (!string.IsNullOrEmpty(_nowPlayingTitle))
                        np = (np.Length > 0 ? np + " - " : "") + _nowPlayingTitle;
                    return np.Length > 0 ? srcIcon + " " + np : "No media";
                default:
                    return "N/A";
            }
        }

        private static string ApplyLabelFormat(string label, string type, bool useFahrenheit, bool useRamMb, WeatherData wd = null, double? reading = null)
        {
            string result = label;

            result = Regex.Replace(result, @"\{temp:([^}]+)\}", m =>
            {
                double? raw = type == "weather" ? wd?.TempC : reading;
                if (!raw.HasValue) return "N/A";
                double val = useFahrenheit ? raw.Value * 9.0 / 5.0 + 32.0 : raw.Value;
                try { return val.ToString(m.Groups[1].Value); } catch { return val.ToString("F1"); }
            });

            result = Regex.Replace(result, @"\{pct:([^}]+)\}", m =>
            {
                double? raw = reading;
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

            // Time and date
            result = Regex.Replace(result, @"\{time:([^}]+)\}", m =>
            {
                try { return DateTime.Now.ToString(m.Groups[1].Value); }
                catch { return DateTime.Now.ToString("HH:mm"); }
            });
            result = Regex.Replace(result, @"\{date:([^}]+)\}", m =>
            {
                try { return DateTime.Now.ToString(m.Groups[1].Value); }
                catch { return DateTime.Now.ToString("MMM d"); }
            });

            // Weather
            result = result.Replace("{condition}", wd?.Condition ?? "N/A");
            result = Regex.Replace(result, @"\{feelslike:([^}]+)\}", m =>
            {
                if (!wd?.FeelsLikeC.HasValue ?? true) return "N/A";
                double val = useFahrenheit ? wd.FeelsLikeC.Value * 9.0 / 5.0 + 32.0 : wd.FeelsLikeC.Value;
                try { return val.ToString(m.Groups[1].Value); } catch { return val.ToString("F1"); }
            });
            result = Regex.Replace(result, @"\{humidity:([^}]+)\}", m =>
            {
                if (!wd?.Humidity.HasValue ?? true) return "N/A";
                try { return wd.Humidity.Value.ToString(m.Groups[1].Value); } catch { return wd.Humidity.Value.ToString("F0"); }
            });
            result = result.Replace("{humidity}", wd?.Humidity.HasValue == true ? $"{wd.Humidity.Value:F0}" : "N/A");
            result = Regex.Replace(result, @"\{wind:([^}]+)\}", m =>
            {
                if (!wd?.WindKmh.HasValue ?? true) return "N/A";
                double val = useFahrenheit ? wd.WindKmh.Value * 0.621371 : wd.WindKmh.Value;
                try { return val.ToString(m.Groups[1].Value); } catch { return val.ToString("F0"); }
            });

            // Weather — city name
            result = result.Replace("{city}", wd?.City ?? "");

            // Now Playing
            result = result.Replace("{title}",  _nowPlayingTitle  ?? "");
            result = result.Replace("{artist}", _nowPlayingArtist ?? "");
            result = result.Replace("{album}",  _nowPlayingAlbum  ?? "");
            result = Regex.Replace(result, @"\{elapsed:([^}]+)\}", m =>
            {
                TimeSpan? pos = _nowPlayingPosition;
                if (pos.HasValue && !_nowPlayingIsPaused && _nowPlayingPositionTime != DateTime.MinValue)
                    pos = pos.Value + (DateTime.UtcNow - _nowPlayingPositionTime);
                return FormatMediaTime(pos, m.Groups[1].Value);
            });
            result = Regex.Replace(result, @"\{duration:([^}]+)\}", m => FormatMediaTime(_nowPlayingDuration, m.Groups[1].Value));

            // Icons (evaluated last so they use current _blinkPhase)
            result = result.Replace("{wicon}",  GetWeatherIcon(wd?.Condition, _blinkPhase));
            result = result.Replace("{source}", GetNowPlayingSourceIcon());

            if (result.Contains("{value}"))
            {
                string legacy;
                switch (type)
                {
                    case "cputemperature":
                        if (!reading.HasValue) { legacy = "N/A"; break; }
                        if (useFahrenheit) { double f = reading.Value * 9.0 / 5.0 + 32.0; legacy = $"{f:F1}°F"; break; }
                        legacy = $"{reading.Value:F1}°C"; break;
                    case "cpuusage":  legacy = reading.HasValue ? $"{reading.Value:F0}%" : "N/A"; break;
                    case "gputemperature":
                        if (!reading.HasValue) { legacy = "N/A"; break; }
                        if (useFahrenheit) { double f = reading.Value * 9.0 / 5.0 + 32.0; legacy = $"{f:F1}°F"; break; }
                        legacy = $"{reading.Value:F1}°C"; break;
                    case "gpuusage":  legacy = reading.HasValue ? $"{reading.Value:F0}%" : "N/A"; break;
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
        private static List<string> GetTextElements(string text)
        {
            var elements = new List<string>();
            if (string.IsNullOrEmpty(text)) return elements;

            var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
            while (enumerator.MoveNext())
                elements.Add(enumerator.GetTextElement());
            return elements;
        }

        private static int GetNativeOledTextElementCols(string element)
        {
            if (string.IsNullOrEmpty(element)) return 0;
            return OLED_WIDE_TEXT_GLYPHS.IndexOf(element, StringComparison.Ordinal) >= 0 ? 2 : 1;
        }

        private static int GetNativeOledTextElementUnits(string element)
        {
            return string.IsNullOrEmpty(element) ? 0 : element.Length;
        }

        private static int GetNativeOledTextCols(IEnumerable<string> elements)
        {
            int cols = 0;
            foreach (string element in elements)
                cols += GetNativeOledTextElementCols(element);
            return cols;
        }

        private static int GetNativeOledTextUnits(IEnumerable<string> elements)
        {
            int units = 0;
            foreach (string element in elements)
                units += GetNativeOledTextElementUnits(element);
            return units;
        }

        private static (int Cols, int Units) MeasureNativeOledTextElement(string element, bool bitmap) =>
            bitmap ? (NativeBitmapGlyphCols(element, NativeOledPixelFont.FindGlyph(element)), 0)
                   : (GetNativeOledTextElementCols(element), GetNativeOledTextElementUnits(element));

        private static string TakeNativeOledTextWindow(List<string> elements, int start, int availableCols, bool bitmap)
        {
            if (elements == null || elements.Count == 0 || availableCols <= 0) return "";

            var sb = new StringBuilder();
            int cols = 0;
            int units = 0;
            for (int i = Math.Max(0, start); i < elements.Count; i++)
            {
                string element = elements[i];
                var (nextCols, nextUnits) = MeasureNativeOledTextElement(element, bitmap);
                if (cols + nextCols > availableCols) break;
                if (units + nextUnits > availableCols) break;
                sb.Append(element);
                cols += nextCols;
                units += nextUnits;
            }
            return sb.ToString();
        }

        private static int GetNativeOledTextLastWindowStart(List<string> elements, int availableCols, bool bitmap)
        {
            if (elements == null || elements.Count == 0 || availableCols <= 0) return 0;

            int cols = 0;
            int units = 0;
            int start = elements.Count;
            for (int i = elements.Count - 1; i >= 0; i--)
            {
                var (nextCols, nextUnits) = MeasureNativeOledTextElement(elements[i], bitmap);
                if (cols + nextCols > availableCols) break;
                if (units + nextUnits > availableCols) break;
                cols += nextCols;
                units += nextUnits;
                start = i;
            }
            return Math.Max(0, start);
        }

        private static string ApplyCharacterScroll(string content, int availableCols, int elapsed, int duration)
        {
            if (string.IsNullOrEmpty(content) || availableCols <= 0) return "";
            var elements = GetTextElements(content);
            if (elements.Count <= availableCols || duration <= 0)
                return string.Concat(elements.Take(availableCols));

            int scrollDistance = elements.Count - availableCols;
            double t = Math.Max(0.0, Math.Min(1.0, elapsed / (double)duration));

            int offset;
            if (t < 0.2)
                offset = 0;
            else if (t >= 0.8)
                offset = scrollDistance;
            else
                offset = (int)Math.Round(scrollDistance * (t - 0.2) / 0.6);

            return string.Concat(elements.Skip(Math.Min(offset, scrollDistance)).Take(availableCols));
        }

        private static string ApplyNativeOledTextScroll(string content, int availableCols, int elapsed, int duration) =>
            ApplyNativeTextScroll(content, availableCols, elapsed, duration, false);

        private static string ApplyNativeTextScroll(string content, int availableCols, int elapsed, int duration, bool bitmap)
        {
            if (string.IsNullOrEmpty(content) || availableCols <= 0) return "";

            var elements = GetTextElements(content);
            int scrollDistance = GetNativeOledTextLastWindowStart(elements, availableCols, bitmap);
            if (scrollDistance == 0) return content;
            if (duration <= 0)
                return TakeNativeOledTextWindow(elements, 0, availableCols, bitmap);

            double t = Math.Max(0.0, Math.Min(1.0, elapsed / (double)duration));

            int offset;
            if (t < 0.2)
                offset = 0;
            else if (t >= 0.8)
                offset = scrollDistance;
            else
                offset = (int)Math.Round(scrollDistance * (t - 0.2) / 0.6);

            return TakeNativeOledTextWindow(elements, Math.Min(offset, scrollDistance), availableCols, bitmap);
        }

        private static string PadRightForOledText(string text, int targetCols)
        {
            text = text ?? "";

            var elements = GetTextElements(text);
            int cols = GetNativeOledTextCols(elements);
            int units = GetNativeOledTextUnits(elements);
            if (cols >= targetCols || units >= targetCols) return text;

            int pad = Math.Min(targetCols - cols, targetCols - units);
            return pad > 0 ? text + new string(' ', pad) : text;
        }

        private static string AppendIndicator(string line, string indicator, int reservedCols)
        {
            return PadRightForOledText(line, Math.Max(0, SCREEN_COLS - reservedCols)) + indicator;
        }

        private static string AppendCapsLockIndicator(string line)
        {
            return AppendIndicator(line, DEFAULT_CAPS_LOCK_INDICATOR, 1);
        }

        private static void BuildAndSendDisplay(long now)
        {
            lock (DisplayLock)
            {
                var a = Rotation.Top;
                var b = Rotation.Bottom;
                CheckTestPattern();
                bool test = DateTime.UtcNow < _testPatternUntil;
                bool update = AvailableVersion != null && settings.ShowUpdateNotifications && _updateNotifCycle >= 10000;
                bool special = test || update;
                var thermal = special ? default : SelectThermalAlerts(a.Item, b.Item, ThermalAlerts);
                if (!_bindingReady)
                {
                    if (now < _nextBindAttempt) return;
                    BindEvent();
                    if (!_bindingReady) return;
                }
                Font topFont = _bitmapFrame ? (special ? _oledFont : GetWidgetFont(a.Item)) : null;
                Font bottomFont = _bitmapFrame ? (special ? _oledFont : GetWidgetFont(b.Item)) : null;
                bool capsLock = settings.ShowCapsLockIndicator && capsLockToggled;
                string topSuffix, bottomSuffix = "", line1, line2;
                if (special)
                {
                    topSuffix = test || capsLock ? DEFAULT_CAPS_LOCK_INDICATOR : "";
                    line1 = test ? "12345678901234" : "Update";
                    line2 = test ? "OLED TEST 12345" : "Available!";
                }
                else
                {
                    line1 = BuildWidgetLine(a, now, topFont, capsLock, out topSuffix, thermal.Top);
                    line2 = BuildWidgetLine(b, now, bottomFont, false, out bottomSuffix, thermal.Bottom);
                }
                if (_bitmapFrame) SendToOledImage(line1, line2, topFont, bottomFont, topSuffix, bottomSuffix,
                    !special && IsWeatherWidget(a.Item), !special && IsWeatherWidget(b.Item));
                else SendToOled(AddNativeSuffix(line1, topSuffix), AddNativeSuffix(line2, bottomSuffix));
            }
        }

        private static string AddNativeSuffix(string line, string suffix) => suffix.Length == 0 ? line :
            PadRightForOledText(line, SCREEN_COLS - GetIndicatorLogicalCols(suffix)) + suffix;

        private static void SendToOledImage(string line1, string line2, Font top, Font bottom, string topSuffix, string bottomSuffix,
            bool topWeather = false, bool bottomWeather = false)
        {
            PostJsonText("/game_event", BuildImageEventJson(RenderOledBitmap(line1, line2, top, bottom, topSuffix, bottomSuffix,
                topWeather, bottomWeather)));
        }

        private static byte[] RenderOledBitmap(string line1, string line2, Font top, Font bottom, string topSuffix, string bottomSuffix,
            bool topWeather, bool bottomWeather)
        {
            int height = OLED_IMAGE_HEIGHT;
            if (_oledFrameBuffer == null || _oledFrameBuffer.Height != height)
            {
                _oledFrameBuffer?.Dispose();
                _oledFrameBuffer = new OledFrameBuffer(height);
            }
            return _oledFrameBuffer.Draw(line1, line2, top, bottom, topSuffix, bottomSuffix, topWeather, bottomWeather);
        }

        private sealed class OledFrameBuffer : IDisposable
        {
            private readonly Bitmap bitmap;
            private readonly byte[] pixels;
            private (string Top, string Bottom, Font TopFont, Font BottomFont, string TopSuffix, string BottomSuffix,
                bool TopWeather, bool BottomWeather) last;
            private bool hasFrame;
            private string imageDataJson;

            internal OledFrameBuffer(int height)
            {
                Height = height;
                bitmap = new Bitmap(OLED_IMAGE_WIDTH, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                bitmap.SetResolution(96, 96);
                pixels = new byte[OLED_IMAGE_WIDTH * height * 4];
                Packed = new byte[OLED_IMAGE_WIDTH * height / 8];
            }

            internal int Height { get; }
            internal byte[] Packed { get; }
            internal string ImageDataJson => imageDataJson ?? (imageDataJson = EncodeImageData(Packed));

            internal byte[] Draw(string line1, string line2, Font top, Font bottom, string topSuffix, string bottomSuffix,
                bool topWeather, bool bottomWeather)
            {
                var key = (line1, line2, top, bottom, topSuffix, bottomSuffix, topWeather, bottomWeather);
                if (hasFrame && key.Equals(last)) return Packed;

                var nativeTop = top == null ? LayoutNativeBitmapLine(line1, topSuffix) : null;
                var nativeBottom = bottom == null ? LayoutNativeBitmapLine(line2, bottomSuffix) : null;
                Array.Clear(Packed, 0, Packed.Length);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    PrepareOledGraphics(graphics);
                    DrawOledImageLine(Packed, graphics, top, line1, topSuffix, 0, Height / 2, nativeTop, topWeather);
                    DrawOledImageLine(Packed, graphics, bottom, line2, bottomSuffix, Height / 2, Height / 2, nativeBottom, bottomWeather);
                }

                // Reuse one packed frame and one pixel buffer, even as the content scrolls.
                // The monitor serializes this borrowed buffer before drawing the next frame.
                var rect = new Rectangle(0, 0, OLED_IMAGE_WIDTH, Height);
                var data = bitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                    for (int y = 0; y < Height; y++)
                    {
                        int rowOffset = y * data.Stride;
                        for (int x = 0; x < OLED_IMAGE_WIDTH; x++)
                        {
                            int i = rowOffset + x * 4;
                            int blue = pixels[i], green = pixels[i + 1], red = pixels[i + 2];
                            int max = Math.Max(blue, Math.Max(green, red));
                            int min = Math.Min(blue, Math.Min(green, red));
                            if (max + min > 25)
                                Packed[(y * OLED_IMAGE_WIDTH + x) / 8] |= (byte)(1 << (7 - (x % 8)));
                        }
                    }
                }
                finally { bitmap.UnlockBits(data); }
                BlitNativeBitmapLine(Packed, nativeTop, 0, Height / 2, topSuffix);
                BlitNativeBitmapLine(Packed, nativeBottom, Height / 2, Height / 2, bottomSuffix);
                BlitBitmapIndicators(Packed, 0, Height / 2, topSuffix);
                BlitBitmapIndicators(Packed, Height / 2, Height / 2, bottomSuffix);
                last = key;
                hasFrame = true;
                imageDataJson = null;
                return Packed;
            }

            public void Dispose() => bitmap.Dispose();
        }

        private static void PrepareOledGraphics(Graphics g)
        {
            g.Clear(Color.Black);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        }

        private static System.Drawing.StringFormat CreateOledStringFormat()
        {
            var format = (System.Drawing.StringFormat)System.Drawing.StringFormat.GenericTypographic.Clone();
            format.FormatFlags |= System.Drawing.StringFormatFlags.MeasureTrailingSpaces;
            return format;
        }

        private static float GetTextAdvanceWidth(Font font, string text) =>
            MeasureOledText(font, text, inlineIcons: false, weather: false);

        private static float MeasureOledText(Font font, string text, bool inlineIcons, bool weather)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            var key = (font, text, inlineIcons, weather);
            lock (OledFontMeasureLock)
            {
                if (OledFontAdvanceCache.TryGetValue(key, out float cached))
                    return cached;
            }

            float width;
            using (var bmp = new Bitmap(1, 1, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            using (var format = CreateOledStringFormat())
            {
                PrepareOledGraphics(g);
                width = inlineIcons ? LayoutCustomBitmapText(g, font, format, text, weather) :
                    g.MeasureString(text, font, PointF.Empty, format).Width;
            }

            CacheOledFontAdvance(key, width);
            return width;
        }

        private static void CacheOledFontAdvance((Font, string, bool, bool) key, float width)
        {
            lock (OledFontMeasureLock)
            {
                if (OledFontAdvanceCache.Count >= OLED_FONT_MEASURE_CACHE_LIMIT)
                    OledFontAdvanceCache.Clear();
                OledFontAdvanceCache[key] = width;
            }
        }

        private static int GetIndicatorLogicalCols(string suffix)
        {
            if (string.IsNullOrEmpty(suffix)) return 0;
            return suffix == TEMPERATURE_CRITICAL_INDICATOR + DEFAULT_CAPS_LOCK_INDICATOR ||
                suffix == TEMPERATURE_WARNING_INDICATOR + DEFAULT_CAPS_LOCK_INDICATOR ? 3 : 1;
        }

        private static string ApplyBitmapScroll(string text, Font font, int width, int elapsed, int duration, bool weather)
        {
            if (string.IsNullOrEmpty(text) || width <= 0) return "";
            // Measure whole windows, not nominal characters: these fonts can be proportional.
            bool Fits(string window) => MeasureOledText(font, window, inlineIcons: true, weather: weather) <= width - 1;
            if (Fits(text)) return text;
            var elements = GetTextElements(text);
            int last = elements.Count - 1;
            string tail = elements[last];
            while (last > 0)
            {
                string candidate = elements[last - 1] + tail;
                if (!Fits(candidate)) break;
                tail = candidate; last--;
            }
            double phase = duration <= 0 ? 0 : elapsed / (double)duration;
            int offset = (int)Math.Round(last * Math.Max(0, Math.Min(1, (phase - 0.2) / 0.6)));
            if (offset == last) return tail;
            string window = "";
            for (int i = offset; i < elements.Count; i++)
            {
                string candidate = window + elements[i];
                if (window.Length > 0 && !Fits(candidate)) break;
                window = candidate;
            }
            return window;
        }

        private static void DrawOledImageLine(byte[] packed, Graphics g, Font font, string line, string suffix, int y,
            int rowHeight, List<NativePixelGlyph> nativeGlyphs, bool weather)
        {
            using (var format = CreateOledStringFormat())
            {
                var state = g.Save();
                try
                {
                    int textWidth = BitmapTextWidth(suffix);
                    g.SetClip(new RectangleF(0, y, textWidth, rowHeight));
                    if (nativeGlyphs != null)
                        DrawNativeFallbackGlyphs(g, nativeGlyphs, Brushes.White, y, rowHeight, format);
                    else if (!string.IsNullOrEmpty(line))
                        LayoutCustomBitmapText(g, font, format, line, weather, packed, Brushes.White, y, rowHeight, textWidth);
                }
                finally { g.Restore(state); }
            }
        }

        private static string BuildWidgetLine(LineSelection selection, long now, Font font, bool caps, out string suffix, GpuThermalAlert thermal)
        {
            var item = selection.Item;
            var reading = GetWidgetReading(item);
            suffix = caps ? DEFAULT_CAPS_LOCK_INDICATOR : "";
            if (thermal != null)
            {
                var frame = thermal.Frame(now, selection.Duration > 0 ? selection.Duration : settings.RotationIntervalMs);
                return ScrollWidgetText(frame.Text, font, suffix, frame.Elapsed, frame.Duration);
            }
            string budget = suffix;
            if (IsTemperatureWidget(item) && item.EnableWarnIndicators && reading.Value.HasValue)
            {
                if (reading.Value.Value >= reading.Warning) budget = TEMPERATURE_WARNING_INDICATOR + suffix;
                if (reading.Value.Value >= reading.Critical)
                    suffix = ((_blinkPhase & 1) == 0 ? TEMPERATURE_CRITICAL_INDICATOR : "") + suffix;
                else if (reading.Value.Value >= reading.Warning)
                    suffix = (_blinkPhase < 2 ? TEMPERATURE_WARNING_INDICATOR : "") + suffix;
            }
            if (item == null) return "";
            return ScrollWidgetText(GetItemContent(item, reading), font, budget, selection.Elapsed(now), selection.Duration,
                IsWeatherWidget(item));
        }

        private static string ScrollWidgetText(string text, Font font, string suffix, int elapsed, int duration, bool weather = false)
        {
            if (font != null)
                return ApplyBitmapScroll(text, font, BitmapTextWidth(suffix), elapsed, duration, weather);
            return _bitmapFrame
                ? ApplyNativeTextScroll(text, NativeBitmapTextCols(suffix), elapsed, duration, true)
                : ApplyNativeOledTextScroll(text, SCREEN_COLS - GetIndicatorLogicalCols(suffix), elapsed, duration);
        }

        private static void SendToOled(string line1, string line2)
        {
            PostJsonText("/game_event", BuildTextEventJson(line1, line2));
        }

        private static int NextUpdateValue()
        {
            updateValue = (updateValue + 1) % 3;
            return updateValue;
        }

        private static string JsonString(string value) =>
            JsonSerializer.Serialize(value ?? "");

        private static string BuildTextEventJson(string line1, string line2)
        {
            int value = NextUpdateValue();
            return "{\"game\":\"" + APP_NAME +
                   "\",\"event\":\"" + EVENT_NAME +
                   "\",\"data\":{\"value\":" + value.ToString() +
                   ",\"frame\":{\"text_line_1\":" + JsonString(line1) +
                   ",\"text_line_2\":" + JsonString(line2) +
                   "}}}";
        }

        private static string BuildImageEventJson(byte[] bytes)
        {
            int value = NextUpdateValue();
            string image = ReferenceEquals(bytes, _oledFrameBuffer?.Packed)
                ? _oledFrameBuffer.ImageDataJson : EncodeImageData(bytes);
            return "{\"game\":\"" + APP_NAME + "\",\"event\":\"" + EVENT_NAME +
                "\",\"data\":{\"value\":" + value.ToString() + ",\"frame\":{\"image-data-128x" + OLED_IMAGE_HEIGHT +
                "\":[" + image + "]}}}";
        }

        private static string EncodeImageData(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 3);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append((int)bytes[i]);
            }
            return sb.ToString();
        }

        private static bool PostJson(string path, JObject obj)
        {
            return PostJsonText(path, obj.ToString(Formatting.None));
        }

        private static bool PostJsonText(string path, string json)
        {
            if (baseUrl == null) return false;
            try
            {
                var url = baseUrl + path;
                using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
                using (var resp = GgHttp.PostAsync(url, content).Result)
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        string body = "";
                        try { body = resp.Content.ReadAsStringAsync().Result; } catch { }
                        AppendLog(
                            $"{DateTime.Now} - GameSense {(int)resp.StatusCode} for {path}: {body}{Environment.NewLine}");
                    }
                    return resp.IsSuccessStatusCode;
                }
            }
            catch (Exception ex)
            {
                if (_ggConnected)
                {
                    _ggConnected = false;
                    _reconnectCooldownMs = 2000;
                    var msg = ex is AggregateException ae && ae.InnerException != null ? ae.InnerException.Message : ex.Message;
                    AppendLog( DateTime.Now + " - GG Engine connection lost: " + msg + Environment.NewLine);
                }
                baseUrl = null;
                return false;
            }
        }

        private static void RunMemoryMaintenance()
        {
            try
            {
                lock (OledFontMeasureLock)
                {
                    if (OledFontAdvanceCache.Count > OLED_FONT_MEASURE_CACHE_LIMIT / 2)
                        OledFontAdvanceCache.Clear();
                }


            }
            catch { }
        }

        // Windows fallback for symbols outside the pixel atlas and warning icons.
        private static Font NativeBitmapFont() =>
            _nativeBitmapFont ?? (_nativeBitmapFont = new Font("Consolas", 14, FontStyle.Bold, GraphicsUnit.Pixel));

        private static Font GetWidgetFont(LineItem item)
        {
            if (string.Equals(item?.FontFamily, NativeWidgetFontId, StringComparison.Ordinal)) return null;
            string family = string.IsNullOrWhiteSpace(item?.FontFamily) ? settings?.OledFont : item.FontFamily;
            int size = item?.FontSizeStep ?? 0;
            return GetFrameFont(family, size) ?? GetFrameFont(settings?.OledFont, size);
        }

        private static Font GetFrameFont(string family, int size)
        {
            if (string.IsNullOrWhiteSpace(family)) return null;
            var key = (family.Trim(), Math.Max(0, Math.Min(2, size)));
            if (FrameFonts.TryGetValue(key, out var existing)) return existing;
            Font font = null;
            try
            {
                font = new Font(key.Item1, 14 - key.Item2, GraphicsUnit.Pixel);
                if (!font.Name.Equals(key.Item1, StringComparison.OrdinalIgnoreCase)) { font.Dispose(); font = null; }
            }
            catch (ArgumentException) { }
            FrameFonts[key] = font;
            return font;
        }

        private static void ReleaseFrameFonts()
        {
            _oledFrameBuffer?.Dispose();
            _oledFrameBuffer = null;
            lock (OledFontMeasureLock) OledFontAdvanceCache.Clear();
            foreach (var font in FrameFonts.Values) font?.Dispose();
            FrameFonts.Clear();
            _nativeBitmapFont?.Dispose();
            _nativeBitmapFont = _oledFont = null;
        }

        private static void UpdateOledFont()
        {
            bool oldMode = _bitmapFrame;
            ReleaseFrameFonts();
            _oledFont = GetFrameFont(settings?.OledFont, 0);
            bool HasCustomFont(List<LineItem> line) => line != null && line.Any(item => item != null && !item.Hidden && GetWidgetFont(item) != null);
            // Keep a stable screen mode even when both currently visible rows
            // use the default font, or a custom-font widget is skipped by a rule.
            _bitmapFrame = HasCustomFont(settings?.TopLineItems) || HasCustomFont(settings?.BottomLineItems);
            if (oldMode != _bitmapFrame) InvalidateScreenBinding();
            WidgetReadings.Clear();
            ThermalAlerts.Clear();
        }

        private static void InvalidateScreenBinding()
        {
            _bindingReady = false;
            _nextBindAttempt = 0;
        }

        private static bool HasWeatherWidget()
        {
            var top = settings?.TopLineItems;
            var bot = settings?.BottomLineItems;
            bool Check(List<LineItem> list) => list != null && list.Any(
                i => IsLineItemType(i, "weather") && !i.Hidden);
            return Check(top) || Check(bot);
        }

        private static bool HasNowPlayingWidget()
        {
            var top = settings?.TopLineItems;
            var bot = settings?.BottomLineItems;
            bool Check(List<LineItem> list) => list != null && list.Any(
                i => IsLineItemType(i, "nowplaying") && !i.Hidden);
            return Check(top) || Check(bot);
        }

        private static void RefreshWidgetFlags()
        {
            _hasWeatherWidget = HasWeatherWidget();
            _hasNowPlayingWidget = HasNowPlayingWidget();
            if (!_hasNowPlayingWidget) ClearNowPlaying();
        }

        internal static bool IsLineItemType(LineItem item, string normalizedType)
        {
            string type = item?.Type;
            if (string.IsNullOrEmpty(type)) return false;

            int j = 0;
            for (int i = 0; i < type.Length; i++)
            {
                char c = type[i];
                if (c == ' ' || c == '_') continue;
                if (j >= normalizedType.Length) return false;
                if (char.ToLowerInvariant(c) != normalizedType[j]) return false;
                j++;
            }
            return j == normalizedType.Length;
        }

        private static void ClearNowPlaying()
        {
            _nowPlayingTitle = _nowPlayingArtist = _nowPlayingAlbum = _nowPlayingAppId = null;
            _nowPlayingIsPaused = false;
            _mediaActivity = null;
            _nowPlayingPosition = _nowPlayingDuration = null;
            _nowPlayingPositionTime = DateTime.MinValue;
            _nowPlayingSongKey = null;
        }

        private static void RefreshAllWeather()
        {
            // Collect unique locations across all weather widgets
            var locs = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var list in new[] { settings?.TopLineItems, settings?.BottomLineItems })
            {
                if (list == null) continue;
                foreach (var item in list)
                    if (IsLineItemType(item, "weather") && !item.Hidden)
                        locs.Add(item.WeatherLocation ?? "");
            }
            lock (_weatherByLocation) foreach (var oldKey in _weatherByLocation.Keys.Where(k => !locs.Contains(k)).ToArray()) _weatherByLocation.Remove(oldKey);
            foreach (var loc in locs)
            {
                string locCopy = loc;
                Task.Run(() => RefreshWeatherForLocation(locCopy));
            }
        }

        private static void RefreshWeatherForLocation(string loc)
        {
            try
            {
                string url = string.IsNullOrWhiteSpace(loc)
                    ? "https://wttr.in/?format=j1"
                    : "https://wttr.in/" + Uri.EscapeDataString(loc) + "?format=j1";

                string json = http.GetStringAsync(url).Result;
                var jobj = JObject.Parse(json);

                // Use wttr.in for geocoding: country, province, and city name
                var area       = jobj["nearest_area"]?[0];
                string country = area?["country"]?[0]?["value"]?.ToString() ?? "";
                string region  = area?["region"]?[0]?["value"]?.ToString()  ?? "";

                var wd = new WeatherData();
                wd.City = area?["areaName"]?[0]?["value"]?.ToString();

                // Always parse wttr.in current conditions as a working baseline
                var cond = jobj["current_condition"]?[0];
                if (cond != null)
                {
                    if (double.TryParse(cond["temp_C"]?.ToString(),       out double tc)) wd.TempC      = tc;
                    if (double.TryParse(cond["FeelsLikeC"]?.ToString(),   out double fl)) wd.FeelsLikeC = fl;
                    if (double.TryParse(cond["humidity"]?.ToString(),      out double h))  wd.Humidity   = h;
                    if (double.TryParse(cond["windspeedKmph"]?.ToString(), out double w))  wd.WindKmh    = w;
                    wd.Condition = cond["weatherDesc"]?[0]?["value"]?.ToString();
                }

                lock (_weatherByLocation) { _weatherByLocation[loc] = wd; }

                // If in Canada, try to override with Environment Canada station data
                if (country == "Canada" && !string.IsNullOrEmpty(wd.City))
                {
                    _provinceMap.TryGetValue(region, out string provinceCode);
                    try { RefreshWeatherFromEc(loc, wd, wd.City, provinceCode); }
                    catch (Exception ecEx)
                    {
                        AppendLog( DateTime.Now + " - EC weather fetch failed (using wttr.in fallback): " + ecEx.Message + Environment.NewLine);
                    }
                }

                _weatherLoggedError = false;
            }
            catch (Exception ex)
            {
                if (!_weatherLoggedError)
                {
                    _weatherLoggedError = true;
                    AppendLog( DateTime.Now + " - Weather fetch failed: " + ex.Message + Environment.NewLine);
                }
            }
        }

        private static void RefreshWeatherFromEc(string loc, WeatherData wd, string city, string province)
        {
            EnsureEcSiteList();
            if (_ecSites == null || _ecSites.Count == 0) return;

            // Filter to province first, fall back to all stations if no match
            var candidates = !string.IsNullOrEmpty(province)
                ? _ecSites.Where(s => s.Province.Equals(province, StringComparison.OrdinalIgnoreCase)).ToList()
                : (IEnumerable<(string Code, string Province, string NameEn)>)_ecSites;

            var best = candidates
                .Select(s => (Site: s, Score: ScoreEcNameMatch(s.NameEn, city)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Select(x => x.Site)
                .FirstOrDefault();

            if (best == default) return;

            string xml = FetchEcCityXml(best.Province, best.Code);
            if (xml == null) return;

            var doc = System.Xml.Linq.XDocument.Parse(xml);
            var cur = doc.Root?.Element("currentConditions");
            if (cur == null) return;

            if (double.TryParse(cur.Element("temperature")?.Value,            out double temp)) wd.TempC    = temp;
            if (double.TryParse(cur.Element("relativeHumidity")?.Value,       out double hum))  wd.Humidity = hum;
            if (double.TryParse(cur.Element("wind")?.Element("speed")?.Value, out double wind)) wd.WindKmh  = wind;

            string ecCondition = cur.Element("condition")?.Value;
            if (!string.IsNullOrWhiteSpace(ecCondition)) wd.Condition = ecCondition;

            string wcStr = cur.Element("windChill")?.Value;
            string hxStr = cur.Element("humidex")?.Value;
            if (!string.IsNullOrWhiteSpace(wcStr) && double.TryParse(wcStr, out double wc))
                wd.FeelsLikeC = wc;
            else if (!string.IsNullOrWhiteSpace(hxStr) && double.TryParse(hxStr, out double hx))
                wd.FeelsLikeC = hx;
            else
                wd.FeelsLikeC = wd.TempC;

            lock (_weatherByLocation) { _weatherByLocation[loc] = wd; }
        }

        // Fetches the EC city XML by listing the hourly directory and finding the matching filename.
        // Tries current UTC hour and up to 2 prior hours in case the current hour has no data yet.
        private static string FetchEcCityXml(string province, string siteCode)
        {
            for (int h = 0; h <= 2; h++)
            {
                string hourStr = DateTime.UtcNow.AddHours(-h).ToString("HH");
                string dirUrl  = "https://dd.weather.gc.ca/today/citypage_weather/" + province + "/" + hourStr + "/";
                try
                {
                    string listing = http.GetStringAsync(dirUrl).Result;
                    var m = Regex.Match(listing, @"[\dT.Z]+_MSC_CitypageWeather_" + Regex.Escape(siteCode) + @"_en\.xml");
                    if (m.Success)
                        return http.GetStringAsync(dirUrl + m.Value).Result;
                }
                catch { }
            }
            return null;
        }

        // Scores how well an EC station name matches the target city name.
        private static int ScoreEcNameMatch(string stationName, string city)
        {
            if (string.IsNullOrEmpty(stationName) || string.IsNullOrEmpty(city)) return 0;
            if (stationName.Equals(city, StringComparison.OrdinalIgnoreCase))                      return 100;
            if (stationName.StartsWith(city + " ", StringComparison.OrdinalIgnoreCase))            return 60;
            if (stationName.StartsWith(city, StringComparison.OrdinalIgnoreCase))                  return 40;
            if (stationName.IndexOf(city, StringComparison.OrdinalIgnoreCase) >= 0)                return 10;
            return 0;
        }

        private static void EnsureEcSiteList()
        {
            if (_ecSites != null) return;
            try
            {
                string xml = http.GetStringAsync("https://dd.weather.gc.ca/today/citypage_weather/siteList.xml").Result;
                var doc = System.Xml.Linq.XDocument.Parse(xml);
                _ecSites = doc.Root.Elements("site").Select(s => (
                    Code:     s.Attribute("code")?.Value ?? "",
                    Province: s.Element("provinceCode")?.Value ?? "",
                    NameEn:   s.Element("nameEn")?.Value ?? ""
                )).Where(s => !string.IsNullOrEmpty(s.Code)).ToList();
            }
            catch
            {
                _ecSites = new List<(string, string, string)>();
            }
        }

        private static void ReleaseComObject(object obj)
        {
            if (obj == null) return;
            try
            {
                if (System.Runtime.InteropServices.Marshal.IsComObject(obj))
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(obj);
            }
            catch { }
        }

        private static void CloseAndReleaseWinRTAsyncInfo(object obj)
        {
            if (obj == null) return;
            try { ((IWinRTAsyncInfo)obj).Close(); } catch { }
            ReleaseComObject(obj);
        }

        private static int GetPlaybackStatus(dynamic session)
        {
            object playbackInfo = null;
            try
            {
                playbackInfo = session.GetPlaybackInfo();
                return Convert.ToInt32(((dynamic)playbackInfo).PlaybackStatus);
            }
            catch
            {
                return 0;
            }
            finally
            {
                ReleaseComObject(playbackInfo);
            }
        }

        private static void DisposeProcesses(Process[] processes)
        {
            if (processes == null) return;
            foreach (var process in processes)
            {
                try { process?.Dispose(); } catch { }
            }
        }

        private static bool IsProcessRunning(string processName)
        {
            Process[] processes = null;
            try
            {
                processes = Process.GetProcessesByName(processName);
                return processes.Length > 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                DisposeProcesses(processes);
            }
        }

        // Must be loaded before any WinRT async op is created so .NET projects IAsyncOperation<T> onto the RCW.
        private static readonly System.Reflection.Assembly _srrw = TryLoadSrrw();
        private static System.Reflection.Assembly TryLoadSrrw()
        {
            try { return System.Reflection.Assembly.Load("System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"); }
            catch { return null; }
        }

        // IAsyncInfo is non-generic, so we can [ComImport] it and cast directly.
        // IAsyncOperation<T>.GetResults() IS generic and is invisible to the dynamic binder —
        // we use System.WindowsRuntimeSystemExtensions.AsTask<T>() from System.Runtime.WindowsRuntime as a fallback.
        [System.Runtime.InteropServices.ComImport]
        [System.Runtime.InteropServices.Guid("00000036-0000-0000-C000-000000000046")]
        [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIInspectable)]
        private interface IWinRTAsyncInfo
        {
            uint Id        { get; }
            int  Status    { get; } // 0=Started 1=Completed 2=Canceled 3=Error
            int  ErrorCode { get; }
            void Cancel();
            void Close();
        }

        private static dynamic WinRTGetResults(dynamic asyncOp, Type resultType = null, int timeoutMs = 3000)
        {
            object op = asyncOp;
            try
            {

            // Phase 1: wait for completion via the non-generic IAsyncInfo COM interface.
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            bool completed = false;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    int status = ((IWinRTAsyncInfo)op).Status;
                    if (status == 1) { completed = true; break; }
                    if (status >= 2) return null; // Error or Canceled
                }
                catch (InvalidCastException)
                {
                    // IAsyncInfo not available — fall back to dynamic GetResults spin
                    try { return asyncOp.GetResults(); }
                    catch (System.Runtime.InteropServices.COMException ce)
                        when (unchecked((uint)ce.HResult) == 0x8000000Eu) { }
                }
                Thread.Sleep(10);
            }

            if (!completed) return null;

            // Phase 2: call GetResults() via interface reflection (works if _srrw was loaded early enough).
            foreach (var iface in op.GetType().GetInterfaces())
            {
                var m = iface.GetMethod("GetResults");
                if (m != null)
                {
                    try { return m.Invoke(op, null); }
                    catch { return null; }
                }
            }

            // Phase 3: use WindowsRuntimeSystemExtensions.AsTask<T>() from System.Runtime.WindowsRuntime.
            // This works even when GetInterfaces() returns empty because AsTask wraps the RCW via WinRT projection.
            if (_srrw != null && resultType != null)
            {
                try
                {
                    var extType = _srrw.GetType("System.WindowsRuntimeSystemExtensions");
                    if (extType != null)
                    {
                        foreach (var m in extType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                        {
                            if (!m.IsGenericMethodDefinition || m.Name != "AsTask" || m.GetParameters().Length != 1) continue;
                            var pt = m.GetParameters()[0].ParameterType;
                            if (!pt.IsGenericType || pt.GetGenericTypeDefinition().Name != "IAsyncOperation`1") continue;
                            var task = (System.Threading.Tasks.Task)m.MakeGenericMethod(resultType).Invoke(null, new[] { op });
                            if (task.Wait(timeoutMs))
                                return ((dynamic)task).Result;
                            return null;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppendLog(
                        $"{DateTime.Now} - SMTC AsTask failed: {ex.Message}{Environment.NewLine}");
                }
            }

            AppendLog(
                $"{DateTime.Now} - SMTC: GetResults not found. srrw={_srrw != null} resultType={resultType?.Name} " +
                $"Interfaces: {string.Join(", ", op.GetType().GetInterfaces().Select(i => i.Name))}{Environment.NewLine}");
            return null;
            }
            finally
            {
                CloseAndReleaseWinRTAsyncInfo(op);
            }
        }

        private static void PollNowPlaying()
        {
            object managerObj = null;
            object currentSessionObj = null;
            object sessionsObj = null;
            object chosenSessionObj = null;
            object propsObj = null;
            object timelineObj = null;
            var sessionObjects = new List<object>();
            try
            {
                var smtcType = Type.GetType(
                    "Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager" +
                    ", Windows, ContentType=WindowsRuntime");
                if (smtcType == null)
                {
                    PollNowPlayingSpotifyFallback(); return;
                }

                dynamic requestOp = smtcType.GetMethod("RequestAsync").Invoke(null, null);
                dynamic manager = WinRTGetResults(requestOp, smtcType);
                managerObj = manager;
                if (manager == null)
                {
                    _nowPlayingTitle = _nowPlayingArtist = _nowPlayingAlbum = _nowPlayingAppId = null;
                    _nowPlayingIsPaused = false;
                    _mediaActivity = null;
                    _nowPlayingPosition = _nowPlayingDuration = null;
                    _nowPlayingPositionTime = DateTime.MinValue;
                    _nowPlayingSongKey = null;
                    return;
                }

                // Step 1: Get current session (media key target) and its status.
                dynamic currentSession = null;
                string  currentAppId   = null;
                int     currentStatus  = 0;
                try
                {
                    currentSession = manager.GetCurrentSession();
                    if (currentSession != null)
                    {
                        currentSessionObj = currentSession;
                        try { currentAppId  = currentSession.SourceAppUserModelId?.ToString(); } catch { }
                        currentStatus = GetPlaybackStatus(currentSession);
                    }
                }
                catch { }

                // Step 2: Scan all sessions to find any Playing or Paused session.
                dynamic playingSession = null, pausedSession = null;
                string  playingAppId   = null, pausedAppId   = null;
                try
                {
                    dynamic sessions = manager.GetSessions();
                    sessionsObj = sessions;
                    // Iterate via Size/GetAt instead of foreach. Iterating a WinRT IVectorView
                    // via `foreach (dynamic ... in ...)` creates an IIterator<T> RCW that the
                    // dynamic-dispatched foreach does not reliably dispose, leaking one COM
                    // reference per poll.
                    int sessionCount = 0;
                    try { sessionCount = Convert.ToInt32(sessions.Size); } catch { }
                    for (int i = 0; i < sessionCount; i++)
                    {
                        dynamic s = null;
                        try { s = sessions.GetAt(i); } catch { continue; }
                        if (s == null) continue;
                        sessionObjects.Add((object)s);
                        string sId = null;
                        try { sId = s.SourceAppUserModelId?.ToString(); } catch { }
                        try
                        {
                            int st = GetPlaybackStatus(s);
                            if      (st == 4 && playingSession == null) { playingSession = s; playingAppId = sId; }
                            else if (st == 5 && pausedSession  == null) { pausedSession  = s; pausedAppId  = sId; }
                        }
                        catch { }
                    }
                }
                catch { }

                // Step 3: Choose session.
                // If the current session (media key target) is Playing → use it.
                // Else if any session is Playing → use it (e.g. YouTube playing while Spotify is "current" but paused).
                // Else fall back to the current session or any paused session.
                dynamic chosenSession;
                string  chosenAppId;
                bool    isPaused;

                if (currentSession != null && currentStatus == 4)
                {
                    chosenSession = currentSession;
                    chosenAppId   = currentAppId;
                    isPaused      = false;
                }
                else if (playingSession != null)
                {
                    chosenSession = playingSession;
                    chosenAppId   = playingAppId;
                    isPaused      = false;
                }
                else if (currentSession != null)
                {
                    chosenSession = currentSession;
                    chosenAppId   = currentAppId;
                    isPaused      = currentStatus == 5;
                }
                else
                {
                    chosenSession = pausedSession;
                    chosenAppId   = pausedAppId;
                    isPaused      = pausedSession != null;
                }
                chosenSessionObj = chosenSession;

                if (chosenSession == null)
                {
                    _nowPlayingTitle = _nowPlayingArtist = _nowPlayingAlbum = _nowPlayingAppId = null;
                    _nowPlayingIsPaused = false;
                    _mediaActivity = null;
                    _nowPlayingPosition = _nowPlayingDuration = null;
                    _nowPlayingPositionTime = DateTime.MinValue;
                    _nowPlayingSongKey = null;
                    PollNowPlayingSpotifyFallback();
                    return;
                }

                var propsType = Type.GetType(
                    "Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties" +
                    ", Windows, ContentType=WindowsRuntime");
                dynamic propsOp = chosenSession.TryGetMediaPropertiesAsync();
                dynamic props = WinRTGetResults(propsOp, propsType);
                propsObj = props;
                string title  = props?.Title?.ToString()      ?? "";
                string artist = props?.Artist?.ToString()     ?? "";
                string album  = props?.AlbumTitle?.ToString() ?? "";

                if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(artist))
                {
                    PollNowPlayingSpotifyFallback();
                    return;
                }

                _nowPlayingTitle    = string.IsNullOrEmpty(title)  ? null : title;
                _nowPlayingArtist   = string.IsNullOrEmpty(artist) ? null : artist;
                _nowPlayingAlbum    = string.IsNullOrEmpty(album)  ? null : album;
                _nowPlayingIsPaused = isPaused;
                // Only a session explicitly reported as playing can activate Playing-only.
                bool isPlaying = currentStatus == 4 || playingSession != null;

                // Reset position base whenever the song changes so the new song's first poll
                // always seeds the extrapolation, regardless of the previous song's position.
                string songKey = $"{_nowPlayingTitle}|{_nowPlayingArtist}";
                if (songKey != _nowPlayingSongKey)
                {
                    _nowPlayingSongKey      = songKey;
                    _nowPlayingPosition     = null;
                    _nowPlayingPositionTime = DateTime.MinValue;
                }

                // If AppId is missing, check for a running Spotify process to correctly identify source.
                if (string.IsNullOrEmpty(chosenAppId) && IsProcessRunning("Spotify"))
                    chosenAppId = "Spotify";
                _nowPlayingAppId = chosenAppId;

                try
                {
                    dynamic tl = chosenSession.GetTimelineProperties();
                    timelineObj = tl;
                    long posTicks = GetWinRtTimeSpanTicks(tl.Position);
                    long endTicks = GetWinRtTimeSpanTicks(tl.EndTime);
                    DateTime nowUtc = DateTime.UtcNow;
                    DateTime sampleUtc = GetTimelineLastUpdatedUtc(tl, nowUtc);
                    if (posTicks >= 0)
                    {
                        TimeSpan newPos = TimeSpan.FromTicks(posTicks);
                        TimeSpan reportedNow = ExtrapolateMediaPosition(newPos, sampleUtc, nowUtc, _nowPlayingIsPaused);
                        TimeSpan? currentNow = _nowPlayingPosition;
                        if (currentNow.HasValue)
                            currentNow = ExtrapolateMediaPosition(currentNow.Value, _nowPlayingPositionTime, nowUtc, _nowPlayingIsPaused);

                        // Browser media sessions, especially YouTube, can report Position values
                        // from an older LastUpdatedTime. Store the sample time with the position so
                        // display formatting can compensate instead of showing a stale elapsed value.
                        bool looksLikeStuckZero = !_nowPlayingIsPaused &&
                            newPos < TimeSpan.FromSeconds(2) &&
                            currentNow.HasValue &&
                            currentNow.Value > TimeSpan.FromSeconds(10) &&
                            sampleUtc >= nowUtc.AddSeconds(-3);

                        bool acceptTimeline =
                            !looksLikeStuckZero &&
                            (!currentNow.HasValue ||
                             _nowPlayingIsPaused ||
                             reportedNow >= currentNow.Value - TimeSpan.FromSeconds(2) ||
                             Math.Abs((reportedNow - currentNow.Value).TotalSeconds) > 10);

                        if (acceptTimeline)
                        {
                            _nowPlayingPosition     = newPos;
                            _nowPlayingPositionTime = sampleUtc;
                        }
                    }
                    else
                    {
                        _nowPlayingPosition     = null;
                        _nowPlayingPositionTime = DateTime.MinValue;
                    }
                    _nowPlayingDuration = endTicks > 0 ? TimeSpan.FromTicks(endTicks) : (TimeSpan?)null;
                }
                catch { _nowPlayingPosition = _nowPlayingDuration = null; _nowPlayingPositionTime = DateTime.MinValue; }

                _mediaActivity = new MediaActivity(chosenAppId, chosenAppId + ":" + songKey, isPlaying);
                _smtcLoggedError    = false;
            }
            catch (Exception ex)
            {
                if (!_smtcLoggedError)
                {
                    _smtcLoggedError = true;
                    AppendLog( DateTime.Now + " - SMTC poll failed: " + ex.Message + Environment.NewLine);
                }
                PollNowPlayingSpotifyFallback();
            }
            finally
            {
                ReleaseComObject(timelineObj);
                ReleaseComObject(propsObj);
                ReleaseComObject(chosenSessionObj);
                ReleaseComObject(currentSessionObj);
                foreach (var sessionObj in sessionObjects)
                    ReleaseComObject(sessionObj);
                ReleaseComObject(sessionsObj);
                ReleaseComObject(managerObj);
            }
        }

        // Secondary fallback: reads Spotify's window title which contains "Artist - Title - Spotify"
        private static void PollNowPlayingSpotifyFallback()
        {
            Process[] spotifyProcesses = null;
            try
            {
                spotifyProcesses = Process.GetProcessesByName("Spotify");
                foreach (var proc in spotifyProcesses)
                {
                    string wt = proc.MainWindowTitle;
                    if (string.IsNullOrEmpty(wt) || wt == "Spotify" || wt == "Spotify Premium" || wt == "Spotify Free")
                        continue;

                    // Strip trailing " - Spotify" suffix
                    const string suffix = " - Spotify";
                    if (wt.EndsWith(suffix, StringComparison.Ordinal))
                        wt = wt.Substring(0, wt.Length - suffix.Length);

                    int dash = wt.IndexOf(" - ", StringComparison.Ordinal);
                    if (dash > 0)
                    {
                        ClearNowPlaying();
                        _nowPlayingArtist = wt.Substring(0, dash);
                        _nowPlayingTitle  = wt.Substring(dash + 3);
                        _nowPlayingAlbum  = null;
                        _nowPlayingAppId  = "spotify";
                        _nowPlayingSongKey = wt;
                        _mediaActivity = new MediaActivity(_nowPlayingAppId, wt, false);
                        return;
                    }
                }
            }
            catch { }
            finally
            {
                DisposeProcesses(spotifyProcesses);
            }
            ClearNowPlaying();
        }

        private static bool IsNowPlayingSourceAllowed(LineItem item) => IsNowPlayingSourceAllowed(item, _nowPlayingAppId);

        private static bool IsNowPlayingSourceAllowed(LineItem item, string source)
        {
            if (item == null) return true;
            string appId = (source ?? "").ToLowerInvariant();
            if (appId.Contains("spotify")) return item.NowPlayingSpotify;
            if (appId.Contains("chrome") || appId.Contains("msedge") || appId.Contains("edge") ||
                appId.Contains("firefox") || appId.Contains("brave")) return item.NowPlayingYouTube;
            return item.NowPlayingOtherPlayer;
        }

        private static string GetWeatherIcon(string condition, int blinkPhase)
        {
            if (string.IsNullOrEmpty(condition)) return "";
            string c = condition.ToLower();

            if (c.Contains("thunder") || c.Contains("storm"))
                return (blinkPhase & 1) == 0 ? "⚡" : "☁";

            if (c.Contains("snow") || c.Contains("blizzard") || c.Contains("sleet") || c.Contains("ice pellet"))
                return blinkPhase < 2 ? "❄" : "*";

            if (c.Contains("rain") || c.Contains("drizzle") || c.Contains("shower"))
                return blinkPhase < 2 ? "☂" : "·";

            if (c.Contains("fog") || c.Contains("mist") || c.Contains("haze"))
                return "≡";

            if (c.Contains("partly") || c.Contains("partial"))
                return blinkPhase < 2 ? "☀" : "☁";

            if (c.Contains("cloud") || c.Contains("overcast"))
                return "☁";

            if (c.Contains("sun") || c.Contains("clear") || c.Contains("bright") || c.Contains("fair"))
                return "☀";

            return "";
        }

        private static string FormatMediaTime(TimeSpan? ts, string format)
        {
            if (!ts.HasValue) return "?";
            try { return (new DateTime(2000, 1, 1) + ts.Value.Duration()).ToString(format); }
            catch { return "?"; }
        }

        private static string GetNowPlayingSourceIcon()
        {
            bool hasMedia = _nowPlayingTitle != null || _nowPlayingArtist != null;
            if (_nowPlayingIsPaused && hasMedia) return "⏸";
            string lower = (_nowPlayingAppId ?? "").ToLower();
            if (lower.Contains("spotify")) return "♫";
            if (lower.Contains("chrome") || lower.Contains("msedge") || lower.Contains("edge") ||
                lower.Contains("firefox") || lower.Contains("brave")) return "▶";
            if (hasMedia) return _blinkPhase < 2 ? "♪" : "♫";
            return "";
        }

        // WinRT TimeSpan via dynamic may be System.TimeSpan (.Ticks) or a raw Duration struct (.Duration).
        private static long GetWinRtTimeSpanTicks(dynamic ts)
        {
            try { return Convert.ToInt64(ts.Ticks); }    catch { }
            try { return Convert.ToInt64(ts.Duration); } catch { }
            return -1;
        }

        private static DateTime GetWinRtDateTimeUtc(dynamic value, DateTime fallbackUtc)
        {
            try
            {
                object obj = value;
                if (obj is DateTimeOffset dto)
                    return ClampMediaSampleTime(dto.UtcDateTime, fallbackUtc);
                if (obj is DateTime dt)
                    return ClampMediaSampleTime(dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime(), fallbackUtc);
            }
            catch { }

            try
            {
                DateTimeOffset dto = (DateTimeOffset)value;
                return ClampMediaSampleTime(dto.UtcDateTime, fallbackUtc);
            }
            catch { }

            try
            {
                DateTime dt = Convert.ToDateTime(value);
                return ClampMediaSampleTime(dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime(), fallbackUtc);
            }
            catch { }

            return fallbackUtc;
        }

        private static DateTime GetTimelineLastUpdatedUtc(dynamic timeline, DateTime fallbackUtc)
        {
            try { return GetWinRtDateTimeUtc(timeline.LastUpdatedTime, fallbackUtc); }
            catch { return fallbackUtc; }
        }

        private static DateTime ClampMediaSampleTime(DateTime sampleUtc, DateTime fallbackUtc)
        {
            if (sampleUtc == DateTime.MinValue || sampleUtc == DateTime.MaxValue)
                return fallbackUtc;
            if (sampleUtc > fallbackUtc.AddSeconds(5))
                return fallbackUtc;
            if (sampleUtc < fallbackUtc.AddHours(-6))
                return fallbackUtc;
            return DateTime.SpecifyKind(sampleUtc, DateTimeKind.Utc);
        }

        private static TimeSpan ExtrapolateMediaPosition(TimeSpan position, DateTime sampleUtc, DateTime nowUtc, bool paused)
        {
            if (paused || sampleUtc == DateTime.MinValue || sampleUtc >= nowUtc)
                return position;

            return position + (nowUtc - sampleUtc);
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

                    using (var doc = JsonDocument.Parse(json))
                    {
                        string tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
                        string latestStr = tagName.TrimStart('v', 'V');

                        if (!Version.TryParse(latestStr, out Version latest) ||
                            !Version.TryParse(VERSION, out Version current) ||
                            latest <= current)
                            return;

                        AvailableVersion = latestStr;
                    }
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
                    screenUpdate = 0;
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

            double sum = 0;
            int count = 0;
            foreach (var sensor in cpuHardware.Sensors)
            {
                if (sensor.SensorType != SensorType.Load || !sensor.Value.HasValue) continue;
                sum += sensor.Value.Value;
                count++;
            }
            return count > 0 ? sum / count : (double?)null;
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
            // Hardware was refreshed by the shared polling pass.
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

        private static bool IsCpuTemperatureBlockedSignal(double? temperature)
        {
            return !IsValidTemperature(temperature);
        }

        private static void WriteMonitorStatus()
        {
            try
            {
                var status = new JObject(
                    new JProperty("SchemaVersion", 2),
                    new JProperty("ProcessId", CurrentProcessId),
                    new JProperty("Elevated", new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)),
                    new JProperty("CpuStatus", CpuStatus()),
                    new JProperty("SensorError", _sensorError),
                    new JProperty("UpdatedUtc", DateTime.UtcNow.ToString("o")),
                    new JProperty("CpuTemperature",
                        cachedCpuTemp.HasValue ? (JToken)new JValue(cachedCpuTemp.Value) : JValue.CreateNull()),
                    new JProperty("CpuTemperatureBlockedByAv", false),
                    new JProperty("CpuTemperatureUnavailable", IsCpuTemperatureBlockedSignal(cachedCpuTemp))
                );

                string tmpPath = MonitorStatusFilePath + ".tmp";
                File.WriteAllText(tmpPath, status.ToString(Formatting.None));
                if (File.Exists(MonitorStatusFilePath))
                    File.Replace(tmpPath, MonitorStatusFilePath, null);
                else
                    File.Move(tmpPath, MonitorStatusFilePath);
            }
            catch { }
        }

        private static double? GetCpuTemperature(string sensorOverride = null)
        {
            if (cpuHardware == null)
                return null;

            cpuHardware.Update();

            if (!string.IsNullOrEmpty(sensorOverride))
            {
                var named = cpuHardware.Sensors.FirstOrDefault(s =>
                    s.SensorType == SensorType.Temperature && s.Name == sensorOverride && s.Value.HasValue);
                if (named != null) return (double)named.Value.Value;
            }

            // Try to read Average sensor first
            var average = cpuHardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name != null && s.Name.ToLower().Contains("average"));
            if (average != null && average.Value.HasValue)
                return (double) average.Value.Value;

            // Try to read Package sensor next
            var package = cpuHardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name != null && s.Name.ToLower().Contains("package"));
            if (package != null && package.Value.HasValue)
                return (double) package.Value.Value;

            // Fallback: average all "Core" temp sensors
            double coreTempSum = 0;
            int coreTempCount = 0;
            foreach (var sensor in cpuHardware.Sensors)
            {
                if (sensor.SensorType != SensorType.Temperature ||
                    sensor.Name == null ||
                    !sensor.Value.HasValue ||
                    !coreSensorRegex.IsMatch(sensor.Name.ToLower()))
                    continue;

                coreTempSum += sensor.Value.Value;
                coreTempCount++;
            }

            if (coreTempCount > 0)
                return coreTempSum / coreTempCount;

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
        private static double? GetGpuTemperature(string sensorOverride = null)
        {
            if (gpuHardware == null)
                return null;

            gpuHardware.Update();

            if (!string.IsNullOrEmpty(sensorOverride))
            {
                var named = gpuHardware.Sensors.FirstOrDefault(s =>
                    s.SensorType == SensorType.Temperature && s.Name == sensorOverride && s.Value.HasValue);
                if (named != null) return (double)named.Value.Value;
            }

            // Look for core sensor
            var gpuCore = gpuHardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name != null && s.Name.ToLower().Contains("core"));
            if (gpuCore != null && gpuCore.Value.HasValue)
                return (double) gpuCore.Value.Value;

            var firstTemp = gpuHardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value.HasValue);
            if (firstTemp != null)
                return (double) firstTemp.Value.Value;

            return null;
        }
        private static readonly Dictionary<string, float> CpuMaxTemps = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            // Sources for the new entries: Intel ARK and AMD's individual
            // processor specifications (Maximum Operating Temperature / Tjmax).
            // Intel Core Ultra Series 3 (Intel ARK)
            { "Intel Core Ultra 5 338H", 100.0f },
            { "Intel Core Ultra 5 335", 100.0f },
            { "Intel Core Ultra 5 332", 100.0f },
            { "Intel Core Ultra 5 325", 100.0f },
            { "Intel Core Ultra 5 322", 100.0f },
            { "Intel Core Ultra 7 366H", 100.0f },
            { "Intel Core Ultra 7 365", 100.0f },
            { "Intel Core Ultra 7 356H", 100.0f },
            { "Intel Core Ultra 7 355", 100.0f },
            { "Intel Core Ultra 9 386H", 100.0f },
            { "Intel Core Ultra X7 368H", 100.0f },
            { "Intel Core Ultra X7 358H", 100.0f },
            { "Intel Core Ultra X9 388H", 100.0f },
            { "Intel Core Ultra X9 378H", 100.0f },
            // Intel Core Ultra Series 2 (Intel ARK). H/U, HX, V and desktop limits differ.
            { "Intel Core Ultra 5 250KF Plus", 105.0f },
            { "Intel Core Ultra 5 250K Plus", 105.0f },
            { "Intel Core Ultra 5 245T", 105.0f },
            { "Intel Core Ultra 5 245KF", 105.0f },
            { "Intel Core Ultra 5 245K", 105.0f },
            { "Intel Core Ultra 5 245HX", 105.0f },
            { "Intel Core Ultra 5 245", 105.0f },
            { "Intel Core Ultra 5 238V", 100.0f },
            { "Intel Core Ultra 5 236V", 100.0f },
            { "Intel Core Ultra 5 235U", 110.0f },
            { "Intel Core Ultra 5 235T", 105.0f },
            { "Intel Core Ultra 5 235HX", 105.0f },
            { "Intel Core Ultra 5 235H", 110.0f },
            { "Intel Core Ultra 5 235", 105.0f },
            { "Intel Core Ultra 5 230F", 105.0f },
            { "Intel Core Ultra 5 228V", 100.0f },
            { "Intel Core Ultra 5 226V", 100.0f },
            { "Intel Core Ultra 5 225U", 110.0f },
            { "Intel Core Ultra 5 225T", 105.0f },
            { "Intel Core Ultra 5 225H", 110.0f },
            { "Intel Core Ultra 5 225F", 105.0f },
            { "Intel Core Ultra 5 225", 105.0f },
            { "Intel Core Ultra 7 270K Plus", 105.0f },
            { "Intel Core Ultra 7 270HX Plus", 105.0f },
            { "Intel Core Ultra 7 268V", 100.0f },
            { "Intel Core Ultra 7 266V", 100.0f },
            { "Intel Core Ultra 7 265U", 110.0f },
            { "Intel Core Ultra 7 265T", 105.0f },
            { "Intel Core Ultra 7 265KF", 105.0f },
            { "Intel Core Ultra 7 265K", 105.0f },
            { "Intel Core Ultra 7 265HX", 105.0f },
            { "Intel Core Ultra 7 265H", 110.0f },
            { "Intel Core Ultra 7 265F", 105.0f },
            { "Intel Core Ultra 7 265", 105.0f },
            { "Intel Core Ultra 7 258V", 100.0f },
            { "Intel Core Ultra 7 256V", 100.0f },
            { "Intel Core Ultra 7 255U", 110.0f },
            { "Intel Core Ultra 7 255HX", 105.0f },
            { "Intel Core Ultra 7 255H", 110.0f },
            { "Intel Core Ultra 9 290HX Plus", 105.0f },
            { "Intel Core Ultra 9 288V", 100.0f },
            { "Intel Core Ultra 9 285T", 105.0f },
            { "Intel Core Ultra 9 285K", 105.0f },
            { "Intel Core Ultra 9 285HX", 105.0f },
            { "Intel Core Ultra 9 285H", 110.0f },
            { "Intel Core Ultra 9 285", 105.0f },
            { "Intel Core Ultra 9 275HX", 105.0f },
            // Intel Core Ultra Series 1 (Meteor Lake U/H; Intel datasheet 792044).
            { "Intel Core Ultra 9 185H", 110.0f },
            { "Intel Core Ultra 7 165H", 110.0f },
            { "Intel Core Ultra 7 155H", 110.0f },
            { "Intel Core Ultra 7 165U", 110.0f },
            { "Intel Core Ultra 7 155U", 110.0f },
            { "Intel Core Ultra 5 135H", 110.0f },
            { "Intel Core Ultra 5 125H", 110.0f },
            { "Intel Core Ultra 5 135U", 110.0f },
            { "Intel Core Ultra 5 125U", 110.0f },
            // AMD Ryzen 9000 desktop; Ryzen 9000 HX is listed separately.
            { "AMD Ryzen 9 9950X3D2", 95.0f },
            { "AMD Ryzen 9 9950X3D", 95.0f },
            { "AMD Ryzen 9 9950X", 95.0f },
            { "AMD Ryzen 9 9900X3D", 95.0f },
            { "AMD Ryzen 9 9900X", 95.0f },
            { "AMD Ryzen 7 9850X3D", 95.0f },
            { "AMD Ryzen 7 9800X3D", 95.0f },
            { "AMD Ryzen 7 9700X", 95.0f },
            { "AMD Ryzen 7 9700F", 95.0f },
            { "AMD Ryzen 5 9600X", 95.0f },
            { "AMD Ryzen 5 9600", 95.0f },
            { "AMD Ryzen 5 9500F", 95.0f },
            // AMD Ryzen 9000 HX
            { "AMD Ryzen 9 9955HX3D", 100.0f },
            { "AMD Ryzen 9 9955HX", 100.0f },
            { "AMD Ryzen 9 9850HX", 100.0f },
            // AMD Ryzen 8000 desktop
            { "AMD Ryzen 7 8700G", 95.0f },
            { "AMD Ryzen 7 8700F", 95.0f },
            { "AMD Ryzen 5 8600G", 95.0f },
            { "AMD Ryzen 5 8500G", 95.0f },
            { "AMD Ryzen 5 8500GE", 95.0f },
            { "AMD Ryzen 5 8400F", 95.0f },
            { "AMD Ryzen 3 8300G", 95.0f },
            // AMD Ryzen AI 400, 300 and Ryzen AI Max
            { "AMD Ryzen AI 9 HX 475", 100.0f },
            { "AMD Ryzen AI 9 HX 470", 100.0f },
            { "AMD Ryzen AI 9 465", 100.0f },
            { "AMD Ryzen AI 7 450", 100.0f },
            { "AMD Ryzen AI 7 445", 100.0f },
            { "AMD Ryzen AI 5 435", 100.0f },
            { "AMD Ryzen AI 5 430", 100.0f },
            { "AMD Ryzen AI 9 HX 375", 100.0f },
            { "AMD Ryzen AI 9 HX 370", 100.0f },
            { "AMD Ryzen AI 9 365", 100.0f },
            { "AMD Ryzen AI 7 350", 100.0f },
            { "AMD Ryzen AI 5 340", 100.0f },
            { "AMD Ryzen AI 5 330", 100.0f },
            { "AMD Ryzen AI Max+ 395", 100.0f },
            { "AMD Ryzen AI Max+ 392", 100.0f },
            { "AMD Ryzen AI Max 390", 100.0f },
            { "AMD Ryzen AI Max 385", 100.0f },
            // AMD Threadripper 9000 and PRO 9000 WX
            { "AMD Ryzen Threadripper 9980X", 95.0f },
            { "AMD Ryzen Threadripper 9970X", 95.0f },
            { "AMD Ryzen Threadripper 9960X", 95.0f },
            { "AMD Ryzen Threadripper PRO 9995WX", 95.0f },
            { "AMD Ryzen Threadripper PRO 9985WX", 95.0f },
            { "AMD Ryzen Threadripper PRO 9975WX", 95.0f },
            { "AMD Ryzen Threadripper PRO 9965WX", 95.0f },
            { "AMD Ryzen Threadripper PRO 9955WX", 95.0f },
            { "AMD Ryzen Threadripper PRO 9945WX", 95.0f },
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
            { "AMD Ryzen 5 7600X3D",     89.0f },
            { "AMD Ryzen 5 7500X3D",     89.0f },
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
            { "AMD Ryzen 5 7500F",       95.0f },
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

        private static readonly Dictionary<string, float> GpuMaxTemps = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            // NVIDIA RTX 50 desktop reference specifications. Do not use these for laptop models.
            // https://www.nvidia.com/en-us/geforce/graphics-cards/compare/
            { "NVIDIA GeForce RTX 5090",          90.0f },
            // https://www.nvidia.cn/geforce/graphics-cards/50-series/rtx-5090-d-v2/
            { "NVIDIA GeForce RTX 5090 D v2",     90.0f },
            { "NVIDIA GeForce RTX 5080",          88.0f },
            { "NVIDIA GeForce RTX 5070 Ti",       88.0f },
            { "NVIDIA GeForce RTX 5070",          85.0f },
            { "NVIDIA GeForce RTX 5060 Ti",       87.0f },
            { "NVIDIA GeForce RTX 5060",          89.0f },
            { "NVIDIA GeForce RTX 5050",          87.0f },
            // NVIDIA RTX 40 Series
            { "NVIDIA GeForce RTX 4090",          90.0f },
            // https://www.nvidia.cn/geforce/graphics-cards/40-series/rtx-4090-d/
            { "NVIDIA GeForce RTX 4090 D",        90.0f },
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
            { "NVIDIA GeForce RTX 3050 6GB",      93.0f },
            { "NVIDIA GeForce RTX 3050 8GB",      93.0f },
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
            TryGetCpuMaximum(name, out _);

        internal static bool IsGpuNameInDictionary(string name) =>
            TryGetGpuMaximum(name, out _);

        private static bool TryGetCpuMaximum(string name, out float maximum)
        {
            maximum = 0;
            return !string.IsNullOrWhiteSpace(name) &&
                (CpuMaxTemps.TryGetValue(name, out maximum) || CpuMaxTemps.TryGetValue(NormalizeCpuName(name), out maximum));
        }

        private static bool TryGetGpuMaximum(string name, out float maximum)
        {
            maximum = 0;
            return !string.IsNullOrWhiteSpace(name) &&
                (GpuMaxTemps.TryGetValue(name, out maximum) || GpuMaxTemps.TryGetValue(NormalizeGpuName(name), out maximum));
        }

        internal static float GetCpuMaximum(string name) =>
            TryGetCpuMaximum(name, out var maximum) ? maximum :
                (string.IsNullOrWhiteSpace(name) || name.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0 ? 100f : 95f);

        internal static float GetGpuMaximum(string name) =>
            TryGetGpuMaximum(name, out var maximum) ? maximum :
                (name?.IndexOf("AMD Radeon", StringComparison.OrdinalIgnoreCase) >= 0 ? 110f : 92f);

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
