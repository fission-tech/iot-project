using System.Text.Json.Serialization;

namespace IoT.Contracts;

// Registry contract. Connection settings belong to the referenced interface, not to the device JSON.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeviceRegistration(
    string Id, string InterfaceId, bool Enabled, byte UnitId, int PollIntervalSeconds,
    int? GpioPin, string Description, MetricDefinition[] Metrics)
{
    public void Validate()
    {
        ValidateId(Id, "Device ID");
        ValidateId(InterfaceId, "Interface ID");
        if (PollIntervalSeconds is < 1 or > 3600) throw new ArgumentException("Poll interval must be 1–3600 seconds.");
        if (GpioPin is < 0) throw new ArgumentException("GPIO pin must not be negative.");
        if (Metrics is null || Metrics.Length == 0) throw new ArgumentException("At least one metric is required.");
        if (Metrics.Any(metric => metric is null)) throw new ArgumentException("Metrics must not contain null entries.");
        if (Metrics.Select(metric => metric.Name).Distinct(StringComparer.Ordinal).Count() != Metrics.Length)
            throw new ArgumentException("Metric names must be unique.");
        foreach (var metric in Metrics) metric.Validate();
    }

    public static void ValidateId(string id, string label)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Any(char.IsWhiteSpace) || id.IndexOfAny(['/', '+', '#']) >= 0)
            throw new ArgumentException($"{label} must be nonempty and must not contain /, +, # or whitespace.");
    }
}

// Resolved TCP polling snapshot: self-contained so the gateway cache never needs an extra interface request.
public sealed record DeviceDefinition(
    string Id, bool Enabled, string Protocol, string Host, int Port, byte UnitId,
    int PollIntervalSeconds, int? GpioPin, string Description, MetricDefinition[] Metrics, string InterfaceId)
{
    public const string ModbusTcp = "ModbusTcp";

    public void Validate()
    {
        new DeviceRegistration(Id, InterfaceId, Enabled, UnitId, PollIntervalSeconds, GpioPin, Description, Metrics).Validate();
        if (Protocol != ModbusTcp) throw new ArgumentException($"Protocol must be {ModbusTcp}; Modbus RTU comes later.");
        if (string.IsNullOrWhiteSpace(Host)) throw new ArgumentException("Host is required.");
        if (Port is < 1 or > 65535) throw new ArgumentException("Port must be between 1 and 65535.");
    }
}

// Where a value lives on the device and how to turn the raw register(s) into an engineering value:
// value = decoded(DataType, WordOrder) × Scale. 32-bit types span two consecutive registers.
public sealed record MetricDefinition(string Name, string Register, ushort Address, double Scale, string Unit,
    string? DataType = null, string? WordOrder = null)
{
    public static readonly string[] Registers = ["holding", "input", "coil", "discrete"];
    public static readonly string[] RegisterTypes = ["int16", "uint16", "int32", "uint32", "float32"];
    public static readonly string[] WordOrders = ["big", "little"];

    public bool IsBit => Register is "coil" or "discrete";
    // Defaults keep older definitions working: a register is a signed 16-bit value, a coil or input is a bit.
    public string Type => DataType ?? (IsBit ? "bool" : "int16");
    // "big": the first register holds the high word (Modbus convention). "little": word-swapped devices.
    public string Order => WordOrder ?? "big";
    public int Width => Type is "int32" or "uint32" or "float32" ? 2 : 1;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name is "bdSeq" or "DeviceSeq" or "GpioPin")
            throw new ArgumentException($"Invalid metric name '{Name}'.");
        if (!Registers.Contains(Register))
            throw new ArgumentException($"Metric {Name}: register must be one of {string.Join(", ", Registers)}.");
        if (IsBit ? Type != "bool" : !RegisterTypes.Contains(Type))
            throw new ArgumentException(IsBit
                ? $"Metric {Name}: a {Register} holds a single bit, so its data type must be bool."
                : $"Metric {Name}: data type must be one of {string.Join(", ", RegisterTypes)}.");
        if (!WordOrders.Contains(Order)) throw new ArgumentException($"Metric {Name}: word order must be big or little.");
        if (Address + Width - 1 > ushort.MaxValue) throw new ArgumentException($"Metric {Name}: address out of range.");
        if (!double.IsFinite(Scale) || Scale == 0) throw new ArgumentException($"Metric {Name}: scale must be a nonzero number.");
    }
}

// What data acquisition fetches: every enabled device, with a version that changes on every edit.
// Broker is present only when a gateway asks for its broker; older snapshots and caches have none.
public sealed record GatewayConfiguration(long Version, DeviceDefinition[] Devices, BrokerProfile? Broker = null)
{
    public void Validate()
    {
        if (Version < 0 || Devices is null) throw new ArgumentException("Invalid configuration snapshot.");
        if (Devices.Any(device => device is null) || Devices.Select(device => device.Id).Distinct(StringComparer.Ordinal).Count() != Devices.Length)
            throw new ArgumentException("Snapshot devices must be non-null and have unique IDs.");
        foreach (var device in Devices) device.Validate();
        Broker?.Validate();
    }
}
