using IoT.Contracts;
using Org.Eclipse.Tahu.Protobuf;

namespace Demo.Control;

public sealed record GatewayView(string GroupId, string GatewayId, bool Online, ulong? BdSeq);
public sealed record DeviceStateView(string DeviceId, bool Online, double? Temperature, double? Humidity, int? GpioPin,
    bool DoorOpen, bool HighTemperature, DateTimeOffset? RecordedAt, string Quality, string[] BadMetrics);
public sealed record DeviceSummaryView(string DeviceId, long Readings, long FirstSeq, long LastSeq, long Missing,
    long Duplicates, long Historical);
public sealed record ChartPoint(DateTimeOffset RecordedAt, double Temperature, bool Historical);

// What a Sparkplug consumer on Mosquitto sees, rebuilt from the messages themselves (no backend).
// Readings are tracked by each device's DeviceSeq, so gaps and duplicates are visible.
public sealed class SparkplugView
{
    private const double AlarmTemperature = 8;
    private readonly object _gate = new();
    private readonly Dictionary<string, Device> _devices = new(StringComparer.Ordinal);
    private GatewayView? _gateway;

    public void Clear()
    {
        lock (_gate) { _devices.Clear(); _gateway = null; }
    }

    public void Apply(string topic, Payload payload)
    {
        var parts = topic.Split('/');
        if (parts.Length is not (4 or 5) || parts[0] != "spBv1.0") return;
        var (group, type, node) = (parts[1], parts[2], parts[3]);
        lock (_gate)
        {
            switch (type)
            {
                case "NBIRTH":
                    _gateway = new(group, node, true, BdSeq(payload));
                    return;
                case "NDEATH":
                    // A death from an older session (a late Will) must not mark the current one offline.
                    if (_gateway is { } current && current.BdSeq != BdSeq(payload)) return;
                    _gateway = (_gateway ?? new(group, node, false, BdSeq(payload))) with { Online = false };
                    foreach (var device in _devices.Values) device.Online = false;
                    return;
            }
            if (parts.Length != 5) return;
            var state = _devices.TryGetValue(parts[4], out var found) ? found : _devices[parts[4]] = new();
            if (type == "DDEATH") { state.Online = false; return; }
            if (type is not ("DBIRTH" or "DDATA")) return;
            state.Online = true;
            var metrics = payload.Metrics.Where(metric => metric.HasName).ToDictionary(metric => metric.Name, Number);
            var recordedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)payload.Timestamp);
            var historical = payload.Metrics.Any(metric => metric.IsHistorical);
            if (metrics.GetValueOrDefault("DeviceSeq") is { } number)
            {
                var seq = (long)number;
                if (type == "DDATA" && historical) state.HistoricalSeqs.Add(seq);
                // A rebirth re-sends the last reading in its DBIRTH; only a repeated DDATA is a duplicate.
                if (type == "DDATA" && !state.DataSeqs.Add(seq)) { state.Duplicates++; return; }
                state.Seqs.Add(seq);
            }
            if (state.RecordedAt is null || recordedAt >= state.RecordedAt)
            {
                state.RecordedAt = recordedAt;
                state.Metrics = metrics;
                state.BadMetrics = payload.Metrics.Where(metric => metric.HasName && metric.Name != "DeviceSeq" &&
                    (metric.IsNull || SparkplugDecoder.QualityOf(metric) != MetricQuality.Good)).Select(metric => metric.Name).ToArray();
            }
            if (metrics.GetValueOrDefault("Temperature") is { } temperature)
            {
                state.Points.AddLast(new ChartPoint(recordedAt, temperature, historical));
                while (state.Points.Count > 200) state.Points.RemoveFirst();
            }
        }
    }

    public GatewayView[] Gateways()
    {
        lock (_gate) return _gateway is null ? [] : [_gateway];
    }

    public DeviceStateView[] Devices()
    {
        lock (_gate)
            return _devices.OrderBy(item => item.Key).Where(item => item.Value.RecordedAt is not null).Select(item =>
            {
                var metrics = item.Value.Metrics;
                var temperature = metrics.GetValueOrDefault("Temperature");
                return new DeviceStateView(item.Key, item.Value.Online, temperature, metrics.GetValueOrDefault("Humidity"),
                    metrics.GetValueOrDefault("GpioPin") is { } pin ? (int)pin : null, metrics.GetValueOrDefault("GpioValue") == 1,
                    temperature > AlarmTemperature, item.Value.RecordedAt,
                    // Offline means every value is stale (Sparkplug); otherwise the worst quality in the latest reading.
                    !item.Value.Online ? MetricQuality.Stale : item.Value.BadMetrics.Length > 0 ? MetricQuality.Bad : MetricQuality.Good,
                    item.Value.BadMetrics);
            }).ToArray();
    }

    public DeviceSummaryView[] Summaries()
    {
        lock (_gate)
            return _devices.OrderBy(item => item.Key).Where(item => item.Value.Seqs.Count > 0).Select(item =>
            {
                var seqs = item.Value.Seqs;
                var (first, last) = (seqs.Min(), seqs.Max());
                return new DeviceSummaryView(item.Key, seqs.Count, first, last, last - first + 1 - seqs.Count,
                    item.Value.Duplicates, item.Value.HistoricalSeqs.Count);
            }).ToArray();
    }

    public ChartPoint[] History(string deviceId)
    {
        lock (_gate) return _devices.TryGetValue(deviceId, out var device) ? device.Points.ToArray() : [];
    }

    private static ulong? BdSeq(Payload payload) =>
        payload.Metrics.FirstOrDefault(metric => metric.Name == "bdSeq") is { HasLongValue: true } metric ? metric.LongValue : null;

    private static double? Number(Payload.Types.Metric metric) =>
        metric.HasDoubleValue ? metric.DoubleValue
        : metric.HasLongValue ? metric.LongValue
        : metric.HasIntValue ? metric.IntValue
        : metric.HasBooleanValue ? (metric.BooleanValue ? 1 : 0)
        : null;

    private sealed class Device
    {
        public HashSet<long> Seqs { get; } = [];
        public HashSet<long> DataSeqs { get; } = [];
        public long Duplicates { get; set; }
        public HashSet<long> HistoricalSeqs { get; } = [];
        public bool Online { get; set; }
        public DateTimeOffset? RecordedAt { get; set; }
        public Dictionary<string, double?> Metrics { get; set; } = [];
        public string[] BadMetrics { get; set; } = [];
        public LinkedList<ChartPoint> Points { get; } = new();
    }
}
