using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using EdgeGateway.Gateway.Acquisition;
using EdgeGateway.Gateway.Configuration;
using EdgeGateway.Gateway.Sparkplug;
using EdgeGateway.Gateway.Storage;
using FluentModbus;
using Google.Protobuf;
using IoT.Contracts;

internal static class NumericDecodingChecks
{
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    private static ushort[] Words(uint raw, string order) => order == "big"
        ? [(ushort)(raw >> 16), (ushort)raw] : [(ushort)raw, (ushort)(raw >> 16)];

    public static async Task RunAsync()
    {
        foreach (var order in new[] { "big", "little" })
        {
            foreach (uint value in new uint[] { 0, 1, 16_777_215, 16_777_216, 16_777_217, 16_777_218,
                         2_147_483_647, 2_147_483_648, uint.MaxValue - 1, uint.MaxValue })
                Exact(value, "uint32", order, value);
            foreach (var value in new[] { int.MinValue, int.MinValue + 1, -16_777_217, -16_777_216, -1, 0, 1,
                         16_777_216, 16_777_217, int.MaxValue - 1, int.MaxValue })
                Exact(unchecked((uint)value), "int32", order, value);
            var random = new Random(42);
            for (var index = 0; index < 256; index++)
            {
                var raw = (uint)random.NextInt64(0, 1L << 32);
                Exact(raw, "uint32", order, raw);
                Exact(raw, "int32", order, unchecked((int)raw));
            }
            foreach (var value in new[] { 0f, -0f, 85.5f, -2.5f, .1f, float.Epsilon, float.MaxValue,
                         float.MinValue, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                var actual = ModbusFrames.Decode(Words(unchecked((uint)BitConverter.SingleToInt32Bits(value)), order), "float32", order);
                Check(float.IsNaN(value) ? double.IsNaN(actual) : BitConverter.DoubleToInt64Bits(actual) == BitConverter.DoubleToInt64Bits((double)value),
                    $"float32 {value} / {order} changed: {actual:R}");
            }
        }
        Check(ModbusFrames.Decode([0x8000], "int16", "big") == short.MinValue
            && ModbusFrames.Decode([0x7fff], "int16", "big") == short.MaxValue
            && ModbusFrames.Decode([0xffff], "uint16", "big") == ushort.MaxValue, "16-bit decoding changed.");
        await CheckPollingAsync();
        Console.WriteLine("PASS: Exact int32/uint32 boundaries and randomized values, both word orders, unchanged float32/16-bit decoding, actual Modbus polling, JSON/Outbox and Sparkplug precision.");
    }

    private static void Exact(uint raw, string type, string order, double expected)
    {
        var actual = ModbusFrames.Decode(Words(raw, order), type, order);
        Check(actual == expected, $"{type} {order}: expected {expected:R}, got {actual:R}.");
    }

    private static void SetWords(ModbusTcpServer server, string area, ushort address, ushort[] words)
    {
        var registers = area == "holding" ? server.GetHoldingRegisters(1) : server.GetInputRegisters(1);
        registers.SetBigEndian<ushort>(address, words[0]);
        registers.SetBigEndian<ushort>(address + 1, words[1]);
    }

    private static async Task CheckPollingAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var server = new ModbusTcpServer();
        server.AddUnit(1);
        var samples = new (string Name, string Area, ushort Address, string Type, string Order, uint Raw, double Expected)[]
        {
            ("UnsignedBig", "holding", 0, "uint32", "big", uint.MaxValue, uint.MaxValue),
            ("UnsignedLittle", "holding", 2, "uint32", "little", 16_777_217, 16_777_217),
            ("SignedBig", "input", 0, "int32", "big", int.MaxValue, int.MaxValue),
            ("SignedLittle", "input", 2, "int32", "little", unchecked((uint)-16_777_217), -16_777_217),
            ("FloatBig", "holding", 4, "float32", "big", (uint)BitConverter.SingleToInt32Bits(85.5f), 85.5),
            ("FloatLittle", "input", 4, "float32", "little", unchecked((uint)BitConverter.SingleToInt32Bits(-2.5f)), -2.5)
        };
        foreach (var sample in samples)
            SetWords(server, sample.Area, sample.Address, Words(sample.Raw, sample.Order));
        server.Start(new IPEndPoint(IPAddress.Loopback, port));
        var state = Path.Combine(Path.GetTempPath(), "numeric-decoding-" + Guid.NewGuid().ToString("N"));
        using var outbox = new Outbox(":memory:");
        using var stop = new CancellationTokenSource();
        var device = new DeviceDefinition("numeric", true, DeviceDefinition.ModbusTcp, "127.0.0.1", port, 1, 1, null, "",
            samples.Select(s => new MetricDefinition(s.Name, s.Area, s.Address, 1, "", s.Type, s.Order)).ToArray(), "tcp");
        device.Validate();
        var poller = new ModbusPoller(device, new DaqSettings { StateDirectory = state }, outbox);
        var polling = poller.RunAsync(stop.Token);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (outbox.CountUnsent() < 2 && DateTime.UtcNow < deadline)
            {
                Check(!polling.IsCompleted, "Numeric poller exited: " + polling.Exception);
                await Task.Delay(25);
            }
            var rows = outbox.PeekUnsent(100);
            Check(rows.Length >= 2 && rows[0].Kind == "DBIRTH" && rows[1].Kind == "DDATA", "Numeric reading lifecycle missing.");
            foreach (var row in rows)
            {
                var reading = JsonSerializer.Deserialize<DeviceReading>(JsonSerializer.Serialize(row.Reading, EdgeTopics.Json), EdgeTopics.Json)!;
                reading.Validate();
                var payload = SparkplugPayloads.DeviceReading(1, reading);
                var decoder = new SparkplugDecoder();
                decoder.Decode("spBv1.0/g/NBIRTH/n", SparkplugPayloads.NodeBirth(1).ToByteArray());
                var decoded = decoder.Decode("spBv1.0/g/DBIRTH/n/numeric", payload.ToByteArray());
                foreach (var sample in samples)
                    Check(reading.Metrics[sample.Name] is { IsGood: true } value && value.Number == sample.Expected
                        && payload.Metrics.Single(m => m.Name == sample.Name).DoubleValue == sample.Expected
                        && decoded.Metrics[sample.Name].Number == sample.Expected,
                        $"{sample.Name} lost precision in actual poll/JSON/Outbox/Sparkplug.");
            }
        }
        finally { stop.Cancel(); await polling; Directory.Delete(state, true); }
    }
}
