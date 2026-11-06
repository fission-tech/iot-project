namespace IoT.Contracts;

public sealed record LifecycleEvent(
    string EventId, string MessageType, string GroupId, string GatewayId,
    string? DeviceId, string SourceTopic, DateTimeOffset ObservedAt,
    DateTimeOffset? RecordedAt, ulong? Seq, ulong? BdSeq,
    Dictionary<string, MetricValue> Metrics, bool Historical = false);

// Quality is null for a good value. A bad or stale metric carries no value.
public sealed record MetricValue(double? Number = null, bool? Boolean = null, string? Quality = null)
{
    public bool IsGood => Quality is null or MetricQuality.Good;
}

// Data quality as Sparkplug carries it: a "Quality" metric property with OPC-style codes.
public static class MetricQuality
{
    public const string Good = "good", Bad = "bad", Stale = "stale";
    public const string PropertyName = "Quality";

    public static int Code(string? quality) => quality switch { Bad => 0, Stale => 500, _ => 192 };

    public static string FromCode(int code) => code switch { 0 => Bad, 500 => Stale, _ => Good };
}

// A metric's permanent registry ID, sent as a string property on each DBIRTH metric. The name stays the
// Sparkplug metric name; consumers that do not know this property ignore it.
public static class MetricIdentity
{
    public const string PropertyName = "metricId";
}
