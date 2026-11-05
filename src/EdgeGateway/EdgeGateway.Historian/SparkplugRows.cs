using IoT.Contracts;
using Org.Eclipse.Tahu.Protobuf;

namespace EdgeGateway.Historian;

// One stored value. Node is "group/edge node", so several sites can share one history database.
public sealed record ReadingRow(DateTimeOffset Time, string Node, string Device, string Metric, double? Value,
    string Quality, bool Historical, long? DeviceSeq);

// A birth or death. Device is empty for node events (NBIRTH, NDEATH).
public sealed record DeviceEventRow(DateTimeOffset Time, string Node, string Device, string Kind);

public sealed record SparkplugRows(ReadingRow[] Readings, DeviceEventRow? Event)
{
    public static readonly SparkplugRows None = new([], null);

    // Turns one Sparkplug message into rows. Times are the measurement times from the payload, so readings
    // the gateway replays from its outbox land when they were taken, not when they arrived.
    public static SparkplugRows From(string topic, byte[] bytes, DateTimeOffset receivedAt)
    {
        var parts = topic.Split('/');
        if (parts.Length is not (4 or 5) || parts[0] != "spBv1.0" || parts[1] == "STATE") return None;
        var (type, node) = (parts[2], parts[1] + "/" + parts[3]);
        var device = parts.Length == 5 ? parts[4] : "";
        if (type is not ("NBIRTH" or "NDEATH" or "DBIRTH" or "DDATA" or "DDEATH")) return None;
        if (type.StartsWith('D') != (parts.Length == 5)) return None;

        var payload = Payload.Parser.ParseFrom(bytes);
        // NDEATH is the broker's Will, prepared before connecting, so it has no meaningful timestamp of its own.
        var sent = payload.HasTimestamp && type != "NDEATH" ? FromMillis(payload.Timestamp) : receivedAt;
        // Births can contain an old saved sample. Its measurement time is not the time the device came online.
        DeviceEventRow? lifecycle = type == "DDATA" ? null : new(receivedAt, node, device, type);
        if (type is not ("DBIRTH" or "DDATA")) return new([], lifecycle);

        long? deviceSeq = payload.Metrics.FirstOrDefault(metric => metric.Name == "DeviceSeq") is { HasLongValue: true } seq
            ? (long)seq.LongValue : null;
        var readings = payload.Metrics
            .Where(metric => metric.HasName && metric.Name != "DeviceSeq")
            .Select(metric => new ReadingRow(metric.HasTimestamp ? FromMillis(metric.Timestamp) : sent, node, device,
                metric.Name, Number(metric), Quality(metric), metric.IsHistorical, deviceSeq))
            // Text and other non-numeric metrics have nothing to chart.
            .Where(row => row.Value is not null || row.Quality != MetricQuality.Good)
            .ToArray();
        return new(readings, lifecycle);
    }

    private static DateTimeOffset FromMillis(ulong millis) => DateTimeOffset.FromUnixTimeMilliseconds((long)millis);

    // A null metric without a Quality property still has no trustworthy value.
    private static string Quality(Payload.Types.Metric metric)
    {
        var quality = SparkplugDecoder.QualityOf(metric);
        return metric.IsNull && quality == MetricQuality.Good ? MetricQuality.Bad : quality;
    }

    private static double? Number(Payload.Types.Metric metric) =>
        metric.IsNull ? null
        : metric.HasDoubleValue ? metric.DoubleValue
        : metric.HasFloatValue ? metric.FloatValue
        // Sparkplug carries signed integers as two's complement in unsigned fields.
        : metric.HasLongValue ? (metric.Datatype == (uint)DataType.Int64 ? (long)metric.LongValue : metric.LongValue)
        : metric.HasIntValue ? (metric.Datatype is (uint)DataType.Int8 or (uint)DataType.Int16 or (uint)DataType.Int32
            ? (int)metric.IntValue : metric.IntValue)
        : metric.HasBooleanValue ? (metric.BooleanValue ? 1 : 0)
        : null;
}
