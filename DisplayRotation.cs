using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace GGSystemMonitor
{
    internal partial class Program
    {
        internal static bool IsTemperatureWidget(LineItem item) =>
            IsLineItemType(item, "cputemperature") || IsLineItemType(item, "gputemperature");

        internal static bool SupportsNumericRules(LineItem item) => IsTemperatureWidget(item) ||
            IsLineItemType(item, "cpuusage") || IsLineItemType(item, "gpuusage") || IsLineItemType(item, "ramusage");
        internal static bool IsWeatherWidget(LineItem item) => IsLineItemType(item, "weather");
        internal static bool SupportsTemperatureVisibility(LineItem item) => IsTemperatureWidget(item) || IsWeatherWidget(item);

        // Existing numeric values are persisted in settings.json.
        internal enum WidgetPriority { Off, Threshold, MediaPresent, MediaPlaying, AtOrBelow }
        internal enum RamTargetUnit { Percent, Mb, Gb }
        internal static double DefaultRamPriorityTarget(int unit) =>
            (RamTargetUnit)unit == RamTargetUnit.Mb ? 8192 : (RamTargetUnit)unit == RamTargetUnit.Gb ? 8 : 80;

        internal readonly struct WidgetReading
        {
            public readonly double? Value;
            public readonly double? RamUsedGb;
            public readonly double Warning, Critical;
            public WidgetReading(double? value, double warning = 0, double critical = 0, double? ramUsedGb = null)
            { Value = value; Warning = warning; Critical = critical; RamUsedGb = ramUsedGb; }
        }

        internal readonly struct WidgetRule
        {
            public readonly bool Visible, SkipPair;
            public readonly int Priority;
            public WidgetRule(bool visible, int priority = 0, bool skipPair = false)
            { Visible = visible; Priority = priority; SkipPair = skipPair; }
        }

        internal static WidgetRule EvaluateWidgetRule(LineItem item, WidgetReading reading, bool present, bool playing)
        {
            if (item == null || item.Hidden) return new WidgetRule(false);
            bool media = IsLineItemType(item, "nowplaying");
            bool numeric = SupportsNumericRules(item);
            bool temperature = IsTemperatureWidget(item);
            bool weather = IsWeatherWidget(item);
            bool Meets(double? value, double? threshold, bool below = false, bool absoluteRam = false) => value.HasValue && threshold.HasValue &&
                (temperature ? IsValidTemperature(value) : value >= 0 && !double.IsInfinity(value.Value) && (absoluteRam || value <= 100)) &&
                !double.IsNaN(threshold.Value) && !double.IsInfinity(threshold.Value) &&
                (below ? value.Value <= threshold.Value : value.Value >= threshold.Value);
            if (item.VisibilityThreshold.HasValue)
            {
                if (numeric && !Meets(reading.Value, item.VisibilityThreshold)) return new WidgetRule(false);
                if (weather)
                {
                    double threshold = item.VisibilityThreshold.Value;
                    double? value = reading.Value;
                    if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value) ||
                        double.IsNaN(threshold) || double.IsInfinity(threshold) ||
                        (item.VisibilityAtOrBelow ? value.Value > threshold : value.Value < threshold))
                        return new WidgetRule(false);
                }
            }
            bool ram = IsLineItemType(item, "ramusage");
            var unit = ram ? (RamTargetUnit)item.RamPriorityUnit : RamTargetUnit.Percent;
            bool absoluteRam = unit == RamTargetUnit.Mb || unit == RamTargetUnit.Gb;
            double? targetReading = unit == RamTargetUnit.Mb ? reading.RamUsedGb * 1024 :
                unit == RamTargetUnit.Gb ? reading.RamUsedGb : reading.Value;
            double? target = item.PriorityThreshold ?? (temperature ? reading.Warning : DefaultRamPriorityTarget((int)unit));
            bool active = (WidgetPriority)item.PriorityMode switch
            {
                WidgetPriority.MediaPresent => media && present,
                WidgetPriority.MediaPlaying => media && present && playing,
                WidgetPriority.Threshold => numeric && Meets(targetReading, target, absoluteRam: absoluteRam),
                WidgetPriority.AtOrBelow => numeric && Meets(targetReading, target, below: true, absoluteRam: absoluteRam),
                _ => false
            };
            // Media visibility gates normal rotation; an explicit "media present"
            // takeover can still keep a paused track pinned to this line.
            bool skipMedia = media && item.SkipIfNotPlaying && !(present && playing);
            return new WidgetRule(!skipMedia, active ? Math.Max(1, item.PriorityRank) : 0, skipPair: skipMedia);
        }

        internal static (double Warning, double Critical) GetWidgetTemperatureThresholds(LineItem item, string deviceName)
        {
            bool cpu = IsLineItemType(item, "cputemperature");
            double critical = GetThresholdValue(item.CritTemp, cpu ? GetCpuAutoCritical(deviceName) : GetGpuAutoCritical(deviceName));
            double warning = GetThresholdValue(item.WarnTemp, cpu ? GetCpuAutoWarning(deviceName) : GetGpuAutoWarning(deviceName));
            return (warning >= critical ? critical - 1 : warning, critical);
        }

        internal readonly struct LineSelection
        {
            public readonly LineItem Item;
            public readonly long Started;
            public readonly int Duration;
            public LineSelection(LineItem item, long started, int duration)
            { Item = item; Started = started; Duration = duration; }
            public int Elapsed(long now) => (int)Math.Max(0, Math.Min(Duration, now - Started));
        }

        // No rendering, sensor calls, or wall-clock reads in the scheduler.
        internal sealed class DisplayRotation
        {
            private readonly struct Slot
            {
                public readonly int Row, Duration;
                public readonly LineItem Top, Bottom;
                public Slot(int row, int duration, LineItem top, LineItem bottom = null)
                { Row = row; Duration = duration; Top = top; Bottom = bottom; }
            }

            private sealed class Track
            {
                public readonly List<Slot> Slots = new List<Slot>();
                private int row = -1, duration;
                private long start;
                private LineItem top, bottom;

                public void Reset() { row = -1; top = bottom = null; duration = 0; }
                public bool Matches(Track other)
                {
                    if (Slots.Count == 0 || Slots.Count != other.Slots.Count) return false;
                    for (int i = 0; i < Slots.Count; i++)
                        if (Slots[i].Duration != other.Slots[i].Duration) return false;
                    return true;
                }

                private int FindCurrent()
                {
                    for (int i = 0; i < Slots.Count; i++)
                        if (Slots[i].Row == row) return i;
                    return -1;
                }

                public void Advance(long now)
                {
                    if (Slots.Count == 0) { Reset(); return; }
                    int index = FindCurrent();
                    if (index < 0)
                    {
                        index = 0;
                        while (index < Slots.Count && Slots[index].Row < row) index++;
                        if (index == Slots.Count) index = 0;
                        start = now;
                    }
                    else if (!(top != null && ReferenceEquals(top, Slots[index].Top)) &&
                             !(bottom != null && ReferenceEquals(bottom, Slots[index].Bottom)))
                    {
                        start = now;
                    }
                    if (now - start >= Slots[index].Duration)
                    {
                        long period = 0;
                        foreach (var slot in Slots) period += slot.Duration;
                        start += ((now - start) / period) * period;
                        while (now - start >= Slots[index].Duration)
                        {
                            start += Slots[index].Duration;
                            index = (index + 1) % Slots.Count;
                        }
                    }
                    Select(index, start);
                }

                private void Select(int index, long now)
                {
                    var slot = Slots[index];
                    row = slot.Row; duration = slot.Duration;
                    top = slot.Top; bottom = slot.Bottom; start = now;
                }

                public void AlignTo(Track other) => Select(other.FindCurrent(), other.start);
                public bool InPhaseWith(Track other) => start == other.start && FindCurrent() == other.FindCurrent();
                public LineSelection Top => new LineSelection(top, start, duration);
                public LineSelection Bottom => new LineSelection(bottom, start, duration);
            }

            private readonly Track top = new Track(), bottom = new Track(), pairs = new Track();
            private readonly Track topPriority = new Track(), bottomPriority = new Track();
            private AppSettings previousSettings;
            private bool paired, topWasOverridden, bottomWasOverridden;
            private string previousMediaKey;
            public LineSelection Top { get; private set; }
            public LineSelection Bottom { get; private set; }

            public void Update(AppSettings config, long now, Func<LineItem, WidgetRule> rules, string mediaKey = null)
            {
                if (!ReferenceEquals(previousSettings, config) || paired != config.RotateLinesTogether)
                {
                    top.Reset(); bottom.Reset(); pairs.Reset(); topPriority.Reset(); bottomPriority.Reset();
                    topWasOverridden = bottomWasOverridden = false;
                    previousMediaKey = mediaKey;
                    previousSettings = config; paired = config.RotateLinesTogether;
                }
                top.Slots.Clear(); bottom.Slots.Clear(); pairs.Slots.Clear();
                topPriority.Slots.Clear(); bottomPriority.Slots.Clear();
                var a = config.TopLineItems;
                var b = config.BottomLineItems;
                int highestA = 0, highestB = 0, enabledA = 0, enabledB = 0;
                int Duration(LineItem item) => item.DurationMs > 0 ? item.DurationMs :
                    config.RotationIntervalMs > 0 ? config.RotationIntervalMs : 3000;
                for (int i = 0; i < Math.Max(a?.Count ?? 0, b?.Count ?? 0); i++)
                {
                    var x = a != null && i < a.Count ? a[i] : null;
                    var y = b != null && i < b.Count ? b[i] : null;
                    bool useX = x != null && !x.Hidden, useY = y != null && !y.Hidden;
                    if (useX) enabledA++;
                    if (useY) enabledB++;
                    var ax = useX ? rules(x) : default;
                    var by = useY ? rules(y) : default;
                    int dx = ax.Visible ? Duration(x) : 0, dy = by.Visible ? Duration(y) : 0;
                    if (dx > 0) top.Slots.Add(new Slot(i, dx, x));
                    if (dy > 0) bottom.Slots.Add(new Slot(i, dy, y));
                    if (paired && !ax.SkipPair && !by.SkipPair && (dx > 0 || dy > 0))
                        pairs.Slots.Add(new Slot(i, Math.Max(dx, dy), dx > 0 ? x : null, dy > 0 ? y : null));
                    AddPriority(topPriority, ax.Priority, ref highestA, i, ax.Priority > 0 ? Duration(x) : 0, x);
                    AddPriority(bottomPriority, by.Priority, ref highestB, i, by.Priority > 0 ? Duration(y) : 0, y);
                }
                // Track each line even in synchronized mode. A takeover only
                // changes the selection for its own line; the peer can use
                // its normal schedule without inheriting the paired row's gaps.
                top.Advance(now); bottom.Advance(now);
                if (paired) pairs.Advance(now);
                else if (top.Matches(bottom) && !top.InPhaseWith(bottom))
                {
                    // Rejoin the line that was not overridden rather than interrupting it.
                    if (topWasOverridden && !bottomWasOverridden) top.AlignTo(bottom);
                    else bottom.AlignTo(top);
                }
                if (mediaKey != previousMediaKey)
                {
                    RestartSoleMedia(topPriority);
                    RestartSoleMedia(bottomPriority);
                }
                previousMediaKey = mediaKey;
                topPriority.Advance(now); bottomPriority.Advance(now);
                topWasOverridden = topPriority.Top.Item != null;
                bottomWasOverridden = bottomPriority.Top.Item != null;
                Top = topWasOverridden ? topPriority.Top :
                    paired && !bottomWasOverridden && enabledA != 1 ? pairs.Top : top.Top;
                Bottom = bottomWasOverridden ? bottomPriority.Top :
                    paired && !topWasOverridden && enabledB != 1 ? pairs.Bottom : bottom.Top;
            }

            private static void RestartSoleMedia(Track track)
            {
                if (track.Slots.Count == 1 && IsLineItemType(track.Slots[0].Top, "nowplaying"))
                    track.Reset();
            }

            private static void AddPriority(Track track, int rank, ref int highest, int row, int duration, LineItem item)
            {
                if (duration <= 0 || rank <= 0 || rank < highest) return;
                if (rank > highest) { highest = rank; track.Slots.Clear(); }
                track.Slots.Add(new Slot(row, duration, item));
            }

        }

        private static readonly DisplayRotation Rotation = new DisplayRotation();
        private static readonly Stopwatch RotationClock = Stopwatch.StartNew();
        private static readonly Func<LineItem, WidgetRule> LiveRules = GetLiveRule;
        private static readonly Dictionary<LineItem, WidgetReading> WidgetReadings = new Dictionary<LineItem, WidgetReading>();
        private sealed class MediaActivity
        {
            public readonly string Source, Key;
            public readonly bool Playing;
            public MediaActivity(string source, string key, bool playing)
            { Source = source; Key = key; Playing = playing; }
        }
        private static volatile MediaActivity _mediaActivity;
        private static MediaActivity _frameMedia;

        private static WidgetReading GetWidgetReading(LineItem item)
        {
            if (!SupportsNumericRules(item)) return default;
            if (WidgetReadings.TryGetValue(item, out var value)) return value;
            if (_computer == null) return default;
            if (IsTemperatureWidget(item))
            {
                bool cpu = IsLineItemType(item, "cputemperature");
                var device = ResolveHardware(item, cpu);
                var thresholds = GetWidgetTemperatureThresholds(item, device?.Name);
                value = new WidgetReading(ReadTemperature(device, item.SensorOverride), thresholds.Warning, thresholds.Critical);
            }
            else if (IsLineItemType(item, "gpuusage")) value = new WidgetReading(ReadLoad(ResolveHardware(item, false)));
            else if (IsLineItemType(item, "cpuusage")) value = new WidgetReading(cachedCpuUsage);
            else value = new WidgetReading(cachedRamTotal > 0 ? cachedRamUsed * 100 / cachedRamTotal : null, ramUsedGb: cachedRamUsed);
            WidgetReadings[item] = value;
            return value;
        }

        private static WidgetRule GetLiveRule(LineItem item)
        {
            if (item == null || item.Hidden) return default;
            if (IsLineItemType(item, "nowplaying"))
                return EvaluateWidgetRule(item, default, _frameMedia != null &&
                    IsNowPlayingSourceAllowed(item, _frameMedia.Source), _frameMedia?.Playing == true);
            if (IsWeatherWidget(item))
            {
                double? temperature = null;
                if (item.VisibilityThreshold.HasValue)
                {
                    lock (_weatherByLocation)
                        if (_weatherByLocation.TryGetValue(item.WeatherLocation ?? "", out var weather))
                            temperature = weather?.TempC;
                }
                return EvaluateWidgetRule(item, new WidgetReading(temperature), false, false);
            }
            bool needReading = item.VisibilityThreshold.HasValue ||
                item.PriorityMode == (int)WidgetPriority.Threshold || item.PriorityMode == (int)WidgetPriority.AtOrBelow;
            return EvaluateWidgetRule(item, needReading ? GetWidgetReading(item) : default, false, false);
        }
    }
}
