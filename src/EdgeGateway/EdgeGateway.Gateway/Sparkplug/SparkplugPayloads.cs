using IoT.Contracts;
using Org.Eclipse.Tahu.Protobuf;

namespace EdgeGateway.Gateway.Sparkplug;

public static class SparkplugPayloads
{
    public const string RebirthMetric = "Node Control/Rebirth";

    public static Payload NodeBirth(ulong birthDeathSequence, ulong uptimeSeconds = 0)
    {
        var timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var payload = new Payload { Timestamp = timestamp, Seq = 0 };

        payload.Metrics.Add(new Payload.Types.Metric
        {
            Name = "bdSeq",
            Datatype = (uint)DataType.Int64,
            LongValue = birthDeathSequence,
            Timestamp = timestamp
        });

        payload.Metrics.Add(new Payload.Types.Metric
        {
            Name = "UptimeSeconds",
            Datatype = (uint)DataType.Int64,
            LongValue = uptimeSeconds,
            Timestamp = timestamp
        });

        payload.Metrics.Add(new Payload.Types.Metric
        {
            Name = RebirthMetric,
            Datatype = (uint)DataType.Boolean,
            BooleanValue = false,
            Timestamp = timestamp
        });
        return payload;
    }
    public static Payload NodeData(ulong sequence, ulong uptimeSeconds)
    {
        var timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var payload = new Payload { Timestamp = timestamp, Seq = sequence };
        payload.Metrics.Add(new Payload.Types.Metric
        {
            Name = "UptimeSeconds",
            Timestamp = timestamp,
            LongValue = uptimeSeconds
        });
        return payload;
    }
    public static Payload NodeDeath(ulong birthDeathSequence)
    {
        // The Will is prepared before connecting, so its timestamp is not the death time.
        var payload = new Payload();
        payload.Metrics.Add(new Payload.Types.Metric
        {
            Name = "bdSeq",
            Datatype = (uint)DataType.Int64,
            LongValue = birthDeathSequence
        });
        return payload;
    }
    // Fridge-shaped reading, kept for checks that build payloads directly.
    public static Payload DeviceReading(ulong sequence, double temperature, double humidity, int gpioPin, int gpioValue) =>
        DeviceReading(sequence, FridgeReading("", 0, DateTimeOffset.UtcNow, temperature, humidity, gpioPin, gpioValue),
            includeDeviceSeq: false);

    public static DeviceReading FridgeReading(string deviceId, ulong deviceSeq, DateTimeOffset timestamp,
        double temperature, double humidity, int gpioPin, int gpioValue) => new(deviceId, deviceSeq, timestamp, new()
    {
        ["Temperature"] = new(Number: temperature), ["Humidity"] = new(Number: humidity),
        ["GpioPin"] = new(Number: gpioPin), ["GpioValue"] = new(Number: gpioValue)
    });

    // Metrics come from the device's configuration. Timestamps are the measurement time,
    // so replayed readings keep their original time. A birth also names each metric's permanent registry ID.
    public static Payload DeviceReading(ulong sequence, DeviceReading reading, bool historical = false,
        bool includeDeviceSeq = true, bool birth = false)
    {
        var timestamp = (ulong)reading.Timestamp.ToUnixTimeMilliseconds();
        var payload = new Payload { Timestamp = timestamp, Seq = sequence };
        void Add(Payload.Types.Metric metric)
        {
            metric.Timestamp = timestamp;
            if (historical) metric.IsHistorical = true;
            if (birth && reading.MetricIds?.GetValueOrDefault(metric.Name) is { } id)
            {
                metric.Properties ??= new();
                metric.Properties.Keys.Add(MetricIdentity.PropertyName);
                metric.Properties.Values.Add(new Payload.Types.PropertyValue { Type = (uint)DataType.String, StringValue = id });
            }
            payload.Metrics.Add(metric);
        }
        foreach (var (name, value) in reading.Metrics.OrderBy(metric => metric.Key, StringComparer.Ordinal))
            Add(!value.IsGood
                // No trustworthy value: a null metric with its quality (0 = Bad, 500 = Stale) as a Sparkplug property.
                ? new() { Name = name, Datatype = (uint)DataType.Double, IsNull = true, Properties = Quality(value.Quality) }
                : value.Boolean is { } flag
                    ? new() { Name = name, Datatype = (uint)DataType.Boolean, BooleanValue = flag }
                    : new() { Name = name, Datatype = (uint)DataType.Double, DoubleValue = value.Number!.Value });
        if (includeDeviceSeq)
            Add(new() { Name = "DeviceSeq", Datatype = (uint)DataType.Int64, LongValue = reading.DeviceSeq });
        return payload;
    }

    private static Payload.Types.PropertySet Quality(string? quality)
    {
        var properties = new Payload.Types.PropertySet();
        properties.Keys.Add(MetricQuality.PropertyName);
        properties.Values.Add(new Payload.Types.PropertyValue { Type = (uint)DataType.Int32, IntValue = (uint)MetricQuality.Code(quality) });
        return properties;
    }

    public static Payload DeviceDeath(ulong sequence) => new()
    {
        Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Seq = sequence
    };

    public static bool IsRebirthCommand(byte[] bytes)
    {
        var payload = Payload.Parser.ParseFrom(bytes);
        return payload.Metrics.Any(metric => metric.Name == RebirthMetric && metric.HasBooleanValue && metric.BooleanValue);
    }
}
