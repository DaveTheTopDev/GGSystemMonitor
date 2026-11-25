using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;
using Moq;
using Newtonsoft.Json.Linq;

internal static partial class Regression
{
    static Type P = typeof(Computer).Assembly == null ? null : Assembly.Load("GGSystemMonitor").GetType("GGSystemMonitor.Program");
    static BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    static int count;
    static object Call(string name, params object[] args) => P.GetMethod(name, Flags).Invoke(null, args);
    static void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); count++; }
    static JObject CaptureRequest(Action send)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        P.GetField("baseUrl", Flags).SetValue(null, "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port);
        var receive = Task.Run(() =>
        {
            using (var client = listener.AcceptTcpClient())
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true))
            {
                string line; int length = 0;
                while (!string.IsNullOrEmpty(line = reader.ReadLine()))
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Substring(15).Trim());
                var chars = new char[length]; int read = 0;
                while (read < length) read += reader.Read(chars, read, length - read);
                var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                stream.Write(response, 0, response.Length); stream.Flush();
                return JObject.Parse(new string(chars));
            }
        });
        try { send(); if (!receive.Wait(5000)) throw new Exception("Mock GG request timed out"); return receive.Result; }
        finally { listener.Stop(); P.GetField("baseUrl", Flags).SetValue(null, null); }
    }
    static IHardware Hardware(string name, string id, HardwareType type, params ISensor[] sensors)
    {
        var h = new Mock<IHardware>(); h.SetupGet(x => x.Name).Returns(name);
        h.SetupGet(x => x.Identifier).Returns(new Identifier(id)); h.SetupGet(x => x.HardwareType).Returns(type);
        h.SetupGet(x => x.Sensors).Returns(sensors); h.SetupGet(x => x.SubHardware).Returns(new IHardware[0]); return h.Object;
    }
    static ISensor Sensor(string name, string id, float? value)
    {
        var s = new Mock<ISensor>(); s.SetupGet(x => x.Name).Returns(name); s.SetupGet(x => x.Identifier).Returns(new Identifier(id));
        s.SetupGet(x => x.SensorType).Returns(SensorType.Temperature); s.SetupGet(x => x.Value).Returns(value); return s.Object;
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            foreach (double? value in new double?[] { null, 0, -1, double.NaN, double.PositiveInfinity, 250 })
                Check(!(bool)Call("IsValidTemperature", value), "reject invalid temperature " + value);
            Check((bool)Call("IsValidTemperature", (double?)42.5), "accept valid temperature");
            Check((string)Call("NormalizeCpuName", "13th Gen Intel Core i9-13900K") == "Intel Core i9-13900K", "CPU model normalization");
            CheckNewTemperatureLimits();
            var cpu = Hardware("CPU", "cpu", HardwareType.Cpu, Sensor("CPU Average", "bad", 0), Sensor("CPU Package", "package", 52), Sensor("Core #1", "core", 44));
            Check((double?)Call("ReadTemperature", cpu, null) == 52, "Auto skips zero sensor");
            Check((double?)Call("ReadTemperature", cpu, "/core") == 44, "sensor identifier selection");
            Check((double?)Call("ReadTemperature", cpu, "Core #1") == 44, "legacy sensor name selection");
            Check(Call("ReadTemperature", cpu, "missing") == null, "missing explicit sensor is not substituted");
            Check(Call("ReadTemperature", cpu, "bad") == null, "invalid explicit sensor is not substituted");
            Check(Call("ReadTemperature", Hardware("CPU", "cpu", HardwareType.Cpu, Sensor("Distance to TjMax", "distance", 50)), null) == null, "Auto excludes relative distance temperature");
            var igpu = Hardware("AMD Radeon Graphics", "integrated", HardwareType.GpuAmd);
            var dgpu = Hardware("NVIDIA RTX 5080", "discrete", HardwareType.GpuNvidia);
            Check(ReferenceEquals(Call("ChooseGpu", new object[] { new[] { igpu, dgpu } }), dgpu), "discrete GPU preferred");
            P.GetField("_oledFont", Flags).SetValue(null, null);
            foreach (string text in new[] { "plain very long widget text", "\U0001F4C5 Nov 13, 2026", "\u23f0" + "12:34:56 PM", "\u25b6 Artist - title goes here", "\u2600 24 C sunny and clear" })
                foreach (int elapsed in new[] { 0, 1500, 3000, 5000 })
                {
                    string window = (string)Call("ApplyNativeOledTextScroll", text, 14, elapsed, 5000);
                    string line = (string)Call("AppendCapsLockIndicator", window);
                    Check(line.EndsWith("\U0001F845") && window.Length <= 14, "native caps budget " + elapsed + " " + text.Substring(0, 2));
                }
            var settingsType = P.GetNestedType("AppSettings", BindingFlags.Public);
            var settings = Activator.CreateInstance(settingsType); P.GetField("settings", Flags).SetValue(null, settings);
            CheckDisplayRules();
            CheckHiddenWidgetRotation();
            CheckRamPriorityAndGpuAlerts();
            CheckIndependentPriorityPeer();
            CheckWeatherVisibility();
            CheckPresetStorage();
            foreach (var profile in new[] { new { Name = "keyboard", Height = 40 }, new { Name = "arctis", Height = 48 }, new { Name = "gamedac", Height = 52 } })
            {
                settingsType.GetProperty("OutputProfile").SetValue(settings, profile.Name);
                string payload = (string)Call("BuildImageEventJson", new object[] { new byte[128 * profile.Height / 8] });
                var frame = JObject.Parse(payload)["data"]["frame"];
                Check(((JArray)frame["image-data-128x" + profile.Height]).Count == 128 * profile.Height / 8, "image dimensions " + profile.Name);
                var binding = CaptureRequest(() => Call("BindEvent"));
                var handler = binding["handlers"][0];
                Check((string)handler["mode"] == "screen" && (string)handler["zone"] == "one", "binding is screen-only " + profile.Name);
                Check((string)handler["device-type"] == (profile.Name == "keyboard" ? "keyboard" : "screened-128x" + profile.Height), "binding selects output profile " + profile.Name);
                P.GetField("_bitmapFrame", Flags).SetValue(null, true);
                var imageBinding = CaptureRequest(() => Call("BindEvent"))["handlers"][0];
                Check((string)imageBinding["device-type"] == "screened-128x" + profile.Height &&
                    (bool)imageBinding["datas"][0]["has-text"] == false &&
                    ((JArray)imageBinding["datas"][0]["image-data"]).Count == 128 * profile.Height / 8,
                    "mixed fonts bind a correctly sized bitmap on " + profile.Name);
                P.GetField("_bitmapFrame", Flags).SetValue(null, false);
            }
            CheckStableMixedFontBinding();
            CheckNativePixelFont();
            CheckNativeIndicators();
            CheckInlineWidgetIcons();
            var formType = P.Assembly.GetType("GGSystemMonitor.SettingsForm");
            using (var form = (Form)Activator.CreateInstance(formType, true))
            {
                var fields = BindingFlags.Instance | BindingFlags.NonPublic;
                var top = (ListBox)formType.GetField("_topList", fields).GetValue(form);
                top.SelectedIndex = 0; Application.DoEvents();
                Check(top.Items.Count > 0, "settings loads widgets");
                Check(form.Controls.Count > 0, "settings UI constructs");
                formType.GetMethod("ShowFontSampleHelp", fields).Invoke(form, null);
                var info = (Form)formType.GetField("_fontInfoForm", fields).GetValue(form);
                Check(!info.ShowInTaskbar && info.Owner == form, "font dialog owned without taskbar entry");
                info.Close();
                formType.GetMethod("ShowCpuAvBlockedHelp", fields).Invoke(form, null);
                var cpuInfo = (Form)formType.GetField("_cpuAvInfoForm", fields).GetValue(form);
                formType.GetMethod("ShowCpuAvBlockedHelp", fields).Invoke(form, null);
                Check(ReferenceEquals(cpuInfo, formType.GetField("_cpuAvInfoForm", fields).GetValue(form)), "CPU dialog singleton");
                Check(!cpuInfo.ShowInTaskbar && cpuInfo.Owner == form, "CPU dialog owned");
                cpuInfo.Close();
                var sensorCombo = (ComboBox)formType.GetField("_topSensorCombo", fields).GetValue(form);
                Check(sensorCombo.Items.Count >= 1, "sensor combo initializes without changing widget selection");
                string settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
                var original = File.Exists(settingsPath) ? JObject.Parse(File.ReadAllText(settingsPath)) : new JObject();
                original["_note_regression"] = "keep this";
                File.WriteAllText(settingsPath, original.ToString());
                object widget = top.Items[0];
                widget.GetType().GetProperty("HardwareId").SetValue(widget, "/retained-device");
                widget.GetType().GetProperty("SensorOverride").SetValue(widget, "/retained-sensor");
                formType.GetMethod("SaveSettings", fields).Invoke(form, null);
                var saved = JObject.Parse(File.ReadAllText(settingsPath));
                Check((string)saved["_note_regression"] == "keep this", "save preserves note fields");
                Check((string)saved["TopLineItems"][0]["HardwareId"] == "/retained-device", "save preserves explicit device identifier");
                Check((string)saved["TopLineItems"][0]["SensorOverride"] == "/retained-sensor", "save preserves explicit sensor identifier");
                CheckWidgetEditor(form, formType, fields, top, settingsPath, args.Contains("--ui-captures"));
                var profiles = (ComboBox)formType.GetField("_outputProfile", fields).GetValue(form);
                profiles.SelectedIndex = 1;
                formType.GetMethod("SaveSettings", fields).Invoke(form, null);
                Check((string)JObject.Parse(File.ReadAllText(settingsPath))["OutputProfile"] == "arctis", "save preserves experimental output profile");
                CheckPresetEditor(form, formType, fields, top, settingsPath, args.Contains("--ui-captures"));
                CheckWidgetListMenus(form, formType, fields, top, settingsPath, args.Contains("--ui-captures"));
                CheckWidgetWheelRouting(form, formType);
                // Restore a deterministic fixture for subsequent runs.
                original.Remove("_note_regression");
                File.WriteAllText(settingsPath, original.ToString());
            }
            using (var font = new System.Drawing.Font("Courier New", 14, System.Drawing.GraphicsUnit.Pixel))
            {
                P.GetField("_oledFont", Flags).SetValue(null, font);
                string scrolled = (string)Call("ApplyCharacterScroll", "\U0001F4C5 Unicode text scrolling test", 14, 2500, 5000);
                Check(!scrolled.Any(char.IsSurrogate) || char.IsSurrogatePair(scrolled, 0), "custom scroll never splits calendar surrogate");
                if (args.Contains("--stress"))
                {
                    using (var proc = System.Diagnostics.Process.GetCurrentProcess())
                    {
                        for (int i = 0; i < 100; i++) RenderBitmapForTest("♫ Song " + i, "☀ Weather", font, font, "", "");
                        proc.Refresh(); long before = proc.PrivateMemorySize64;
                        for (int i = 0; i < 2000; i++) RenderBitmapForTest("♫ Song " + (i % 20), "☀ Weather", font, font, "", "");
                        proc.Refresh(); long growth = proc.PrivateMemorySize64 - before;
                        Console.WriteLine("STRESS private bytes growth = " + growth);
                        Check(growth < 64 * 1024 * 1024, "2000-frame image allocation budget");
                    }
                }
                P.GetField("_oledFont", Flags).SetValue(null, null);
            }
            if (args.Contains("--sensors"))
                {
                    var computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true };
                    try {
                    computer.Open();
                    foreach (var hw in computer.Hardware)
                    {
                        hw.Update(); Console.WriteLine("HARDWARE " + hw.Name);
                        foreach (var sensor in hw.Sensors.Where(s => s.SensorType == SensorType.Temperature))
                            Console.WriteLine("SENSOR " + sensor.Name + " = " + sensor.Value);
                    }
                    } finally { computer.Close(); }
                }
            Console.WriteLine("PASS TOTAL " + count); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
