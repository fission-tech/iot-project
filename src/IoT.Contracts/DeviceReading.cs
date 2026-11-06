using System.Text.Json;

namespace IoT.Contracts;

// One poll of one device. DeviceSeq is assigned and persisted by data acquisition and never repeats,
// so it identifies a reading end to end. Metric names and meanings come from the device's configuration.
// MetricIds maps each metric name to its permanent registry ID, as configured when the reading was taken.
public sealed record DeviceReading(string DeviceId, ulong DeviceSeq, DateTimeOffset Timestamp,
    Dictionary<string, MetricValue> Metrics, Dictionary<string, string>? MetricIds = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DeviceId) || DeviceId.IndexOfAny(['/', '+', '#']) >= 0)
            throw new ArgumentException("Device ID must be nonempty and must not contain /, + or #.");
        if (DeviceSeq == 0 || DeviceSeq > long.MaxValue)
            throw new ArgumentException("Device sequence must be positive.");
        if (Metrics is null || Metrics.Count == 0)
            throw new ArgumentException("A reading needs at least one metric.");
        foreach (var (name, value) in Metrics)
        {
            if (string.IsNullOrWhiteSpace(name) || name == "bdSeq" || name == "DeviceSeq")
                throw new ArgumentException($"Invalid metric name '{name}'.");
            if (value is null || value.Quality is not (null or MetricQuality.Good or MetricQuality.Bad or MetricQuality.Stale))
                throw new ArgumentException($"Metric {name} has an unknown quality.");
            if (!value.IsGood)
            {
                // A bad or stale sample says "no trustworthy value", so it must not carry one.
                if (value.Number.HasValue || value.Boolean.HasValue)
                    throw new ArgumentException($"Metric {name} is {value.Quality} and must not carry a value.");
                continue;
            }
            if (value.Number.HasValue == value.Boolean.HasValue || (value.Number is { } number && !double.IsFinite(number)))
                throw new ArgumentException($"Metric {name} needs exactly one finite value.");
        }
    }

    // A metric list change (a name or a permanent ID) means the device must be born again with its new definition.
    public string MetricSignature() => string.Join(",", Metrics.Keys.Order(StringComparer.Ordinal)
        .Select(name => MetricIds?.GetValueOrDefault(name) is { } id ? $"{name}={id}" : name));
}

public static class EdgeTopics
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
