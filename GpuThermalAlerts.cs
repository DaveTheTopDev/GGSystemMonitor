using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace GGSystemMonitor
{
    internal partial class Program
    {
        private const string PasteWarningText = "Check GPU Thermal Paste!";
        private static readonly Dictionary<LineItem, GpuThermalAlert> ThermalAlerts = new Dictionary<LineItem, GpuThermalAlert>();

        internal sealed class GpuThermalAlert
        {
            private readonly LineItem widget;
            private int samples;
            private long started;
            private double? core, hotspot;
            internal GpuThermalAlert(LineItem widget) { this.widget = widget; }
            internal bool Active => samples >= 12;

            internal void Observe(double? coreTemperature, double? hotspotTemperature, long now)
            {
                bool wasActive = Active;
                core = coreTemperature;
                hotspot = hotspotTemperature;
                if (!IsValidTemperature(core) || !IsValidTemperature(hotspot)) samples = 0;
                else if (hotspot.Value - core.Value > widget.GpuPasteGapTemp) samples = Math.Min(48, samples + 1);
                else samples = Math.Max(0, samples - 1);
                if (!wasActive && Active) started = now;
            }

            internal (string Text, int Elapsed, int Duration) Frame(long now, int duration)
            {
                // Advance independently of the widget's scroll timer, which
                // loops even when a priority winner holds the line.
                duration = Math.Max(500, duration);
                long age = Math.Max(0, now - started);
                string Reading(string label, double? value)
                {
                    if (!value.HasValue) return label + ": N/A";
                    double temperature = widget.UseFahrenheit ? value.Value * 1.8 + 32 : value.Value;
                    return $"{label}: {temperature:F1}{(widget.UseFahrenheit ? "°F" : "°C")}";
                }
                string text = (age / duration % 3) switch
                {
                    1 => Reading("GPU", core),
                    2 => Reading("HOTSPOT", hotspot),
                    _ => PasteWarningText
                };
                return (text, (int)(age % duration), duration);
            }
        }

        private static void PollThermalAlerts(AppSettings config, long now)
        {
            foreach (var list in new[] { config.TopLineItems, config.BottomLineItems })
            {
                if (list == null) continue;
                foreach (var item in list)
                {
                    if (!IsLineItemType(item, "gputemperature") || item.Hidden || !item.GpuPasteMonitoring) continue;
                    var device = ResolveHardware(item, false);
                    if (!ThermalAlerts.TryGetValue(item, out var alert))
                        ThermalAlerts[item] = alert = new GpuThermalAlert(item);
                    alert.Observe(ReadTemperature(device, item.SensorOverride), GetGpuHotSpot(device), now);
                }
            }
        }

        private static double? GetGpuHotSpot(IHardware device)
        {
            foreach (var sensor in Sensors(device))
            {
                if (sensor.SensorType != SensorType.Temperature || !IsValidTemperature(sensor.Value)) continue;
                string name = sensor.Name ?? "";
                if (name.IndexOf("hot spot", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("hotspot", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("junction", StringComparison.OrdinalIgnoreCase) >= 0)
                    return sensor.Value;
            }
            return null;
        }

        internal static (GpuThermalAlert Top, GpuThermalAlert Bottom) SelectThermalAlerts(
            LineItem top, LineItem bottom, IReadOnlyDictionary<LineItem, GpuThermalAlert> alerts)
        {
            GpuThermalAlert Find(LineItem item) =>
                item != null && alerts.TryGetValue(item, out var alert) && alert.Active ? alert : null;
            var upper = Find(top);
            var lower = Find(bottom);
            // Retain the old bottom-line emergency alert when its GPU widget
            // is off-screen; never duplicate an alert already visible on top.
            if (upper == null && lower == null)
                lower = alerts.Values.FirstOrDefault(alert => alert.Active);
            return (upper, lower);
        }
    }
}
