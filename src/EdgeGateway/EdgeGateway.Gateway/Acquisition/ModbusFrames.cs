using IoT.Contracts;

namespace EdgeGateway.Gateway.Acquisition;

// One Modbus read request covering several metrics of the same register type.
public sealed record ModbusFrame(string Register, int Start, int Count, MetricDefinition[] Metrics);

// Plans the fewest read requests for a device's metrics and decodes register words into typed values.
public static class ModbusFrames
{
    // Modbus limits a single read to 125 registers or 2000 bits. Small gaps are read through rather than split.
    private const int MaxRegisters = 125, MaxBits = 2000, MaxGap = 8;

    public static ModbusFrame[] Plan(IEnumerable<MetricDefinition> metrics) => metrics
        .GroupBy(metric => metric.Register)
        .SelectMany(group =>
        {
            var limit = group.First().IsBit ? MaxBits : MaxRegisters;
            var frames = new List<ModbusFrame>();
            var current = new List<MetricDefinition>();
            int start = 0, end = 0;
            foreach (var metric in group.OrderBy(metric => metric.Address))
            {
                var metricEnd = metric.Address + metric.Width - 1;
                if (current.Count > 0 && (metric.Address > end + 1 + MaxGap || metricEnd - start + 1 > limit))
                {
                    frames.Add(new(group.Key, start, end - start + 1, current.ToArray()));
                    current.Clear();
                }
                if (current.Count == 0) start = metric.Address;
                current.Add(metric);
                end = Math.Max(current.Count == 1 ? metricEnd : end, metricEnd);
            }
            if (current.Count > 0) frames.Add(new(group.Key, start, end - start + 1, current.ToArray()));
            return frames;
        })
        .ToArray();

    // words[0] is the first register of the metric, as received (each word already in host order).
    public static double Decode(ReadOnlySpan<ushort> words, string dataType, string wordOrder)
    {
        if (dataType is "int16") return (short)words[0];
        if (dataType is "uint16") return words[0];
        var (high, low) = wordOrder == "little" ? (words[1], words[0]) : (words[0], words[1]);
        var raw = (uint)high << 16 | low;
        return dataType switch
        {
            // Keep a common double result: a float result would round large integer branches first.
            "uint32" => (double)raw,
            "int32" => (double)(int)raw,
            "float32" => (double)BitConverter.Int32BitsToSingle((int)raw),
            _ => throw new ArgumentException($"Unsupported data type {dataType}.")
        };
    }

    // Coils and discrete inputs arrive packed eight per byte, lowest address in the lowest bit.
    public static double DecodeBit(ReadOnlySpan<byte> packed, int index) => (packed[index / 8] >> (index % 8)) & 1;
}
