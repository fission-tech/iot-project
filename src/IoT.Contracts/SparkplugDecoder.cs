using Org.Eclipse.Tahu.Protobuf;

namespace IoT.Contracts;

public sealed class SparkplugDecoder
{
    private readonly Dictionary<string, ulong> _nodes = [];
    private readonly Dictionary<string, ulong> _sequences = [];
    private readonly HashSet<string> _devices = [];
    private readonly object _gate = new();

    public void Reset()
    {
        lock (_gate) { _nodes.Clear(); _sequences.Clear(); _devices.Clear(); }
    }

    public LifecycleEvent Decode(string topic, byte[] bytes) => Decode(topic, bytes, out _);

    // A metric without a "Quality" property is good.
    public static string QualityOf(Payload.Types.Metric metric)
    {
        var index = metric.Properties?.Keys.IndexOf(MetricQuality.PropertyName) ?? -1;
        return index >= 0 && index < metric.Properties!.Values.Count
            ? MetricQuality.FromCode((int)metric.Properties.Values[index].IntValue)
            : MetricQuality.Good;
    }

    // The permanent registry ID announced in a birth, or null for metrics without one (e.g. other edge nodes).
    public static string? MetricIdOf(Payload.Types.Metric metric)
    {
        var index = metric.Properties?.Keys.IndexOf(MetricIdentity.PropertyName) ?? -1;
        return index >= 0 && index < metric.Properties!.Values.Count && metric.Properties.Values[index].HasStringValue
            ? metric.Properties.Values[index].StringValue
            : null;
    }

    // Out-of-sync messages (missing birth, sequence gap) are still returned so the caller can store them,
    // with needsRebirth set so the host asks the edge node to resynchronize.
    public LifecycleEvent Decode(string topic, byte[] bytes, out bool needsRebirth)
    {
        lock (_gate)
        {
            needsRebirth = false;
            var parts = topic.Split('/');
            if (parts.Length is not (4 or 5) || parts[0] != "spBv1.0" || parts.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Invalid Sparkplug topic.");
            var type = parts[2];
            if (type is not ("NBIRTH" or "NDATA" or "NDEATH" or "DBIRTH" or "DDATA" or "DDEATH") ||
                parts.Length != (type.StartsWith('D') ? 5 : 4))
                throw new ArgumentException("Unsupported lifecycle topic.");
            var payload = Payload.Parser.ParseFrom(bytes);
            if (type != "NDEATH" && (!payload.HasSeq || payload.Seq > 255 || !payload.HasTimestamp))
                throw new ArgumentException("Missing timestamp or valid sequence.");
            if (type == "NDEATH" && payload.HasSeq)
                throw new ArgumentException("NDEATH must not contain seq.");
            if (payload.HasTimestamp && payload.Timestamp > 253402300799999UL)
                throw new ArgumentException("Invalid timestamp.");
            var node = parts[1] + "/" + parts[3];
            var device = parts.Length == 5 ? node + "/" + parts[4] : null;
            var metrics = new Dictionary<string, MetricValue>(StringComparer.Ordinal);
            ulong? bdSeq = null;
            var historical = false;
            foreach (var metric in payload.Metrics)
            {
                if (!metric.HasName) throw new ArgumentException("Named metrics are required by this decoder.");
                historical |= metric.IsHistorical;
                if (metric.Name == "bdSeq")
                {
                    if (!metric.HasLongValue || metric.LongValue > 255) throw new ArgumentException("Invalid bdSeq.");
                    bdSeq = metric.LongValue;
                    continue;
                }
                var quality = QualityOf(metric);
                if (metric.IsNull || quality != MetricQuality.Good)
                    metrics.Add(metric.Name, new(Quality: quality == MetricQuality.Good ? MetricQuality.Bad : quality));
                else if (metric.HasDoubleValue && double.IsFinite(metric.DoubleValue))
                    metrics.Add(metric.Name, new(Number: metric.DoubleValue));
                else if (metric.HasIntValue) metrics.Add(metric.Name, new(Number: metric.IntValue));
                else if (metric.HasLongValue) metrics.Add(metric.Name, new(Number: metric.LongValue));
                else if (metric.HasBooleanValue) metrics.Add(metric.Name, new(Boolean: metric.BooleanValue));
                else throw new ArgumentException("Unsupported metric value.");
            }
            if ((type is "NBIRTH" or "NDEATH") && bdSeq is null) throw new ArgumentException("Missing bdSeq.");
            if (type == "NDEATH" && _nodes.TryGetValue(node, out var currentSession) && currentSession != bdSeq)
                throw new ArgumentException("Ignored NDEATH from an older session.");
            if (type == "NBIRTH" && payload.Seq != 0) throw new ArgumentException("NBIRTH must use seq 0.");

            if ((type is "NDATA" or "DBIRTH" or "DDATA" or "DDEATH") && !_nodes.ContainsKey(node))
                needsRebirth = true;
            else if ((type is "DDATA" or "DDEATH") && !_devices.Contains(device!))
                needsRebirth = true;
            if (type is not ("NBIRTH" or "NDEATH") && _sequences.TryGetValue(node, out var previous) &&
                payload.Seq != (previous + 1) % 256)
            {
                _nodes.Remove(node);
                needsRebirth = true;
            }
            if (type == "NBIRTH")
            {
                _nodes[node] = bdSeq!.Value;
                _devices.RemoveWhere(key => key.StartsWith(node + "/", StringComparison.Ordinal));
            }
            if (type == "DBIRTH") _devices.Add(device!);
            if (type == "DDEATH") _devices.Remove(device!);
            if (type == "NDEATH")
            {
                _nodes.Remove(node);
                _sequences.Remove(node);
                _devices.RemoveWhere(key => key.StartsWith(node + "/", StringComparison.Ordinal));
            }
            else _sequences[node] = payload.Seq;

            // Deterministic IDs let the backend drop redelivered and replayed messages.
            var session = bdSeq ?? (_nodes.TryGetValue(node, out var known) ? known : null);
            var eventId = type == "DDATA" && metrics.GetValueOrDefault("DeviceSeq")?.Number is { } deviceSeq
                ? $"{device}/reading/{deviceSeq}"
                : type == "NDEATH"
                    ? $"{node}/NDEATH/{bdSeq}"
                    : $"{device ?? node}/{type}/{session?.ToString() ?? "-"}/{payload.Seq}/{payload.Timestamp}";
            var recordedAt = payload.HasTimestamp
                ? DateTimeOffset.FromUnixTimeMilliseconds(checked((long)payload.Timestamp)) : (DateTimeOffset?)null;
            return new(eventId, type, parts[1], parts[3],
                parts.Length == 5 ? parts[4] : null, topic, DateTimeOffset.UtcNow,
                recordedAt,
                payload.HasSeq ? payload.Seq : null, bdSeq, metrics, historical);
        }
    }
}
