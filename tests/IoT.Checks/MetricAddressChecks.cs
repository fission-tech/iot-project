using System.Text.Json;
using System.Text.Json.Nodes;
using EdgeGateway.Configuration;
using EdgeGateway.Gateway.Configuration;
using IoT.Contracts;

internal static class MetricAddressChecks
{
    public static void Run()
    {
        const string valid = """{"name":"Temperature","register":"holding","address":0,"scale":0.1,"unit":"C","dataType":"int16"}""";
        void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
        void Reject(string json)
        {
            try { JsonSerializer.Deserialize<MetricDefinition>(json, EdgeTopics.Json); }
            catch (JsonException) { return; }
            throw new Exception("Invalid metric JSON was accepted: " + json);
        }
        var missing = JsonNode.Parse(valid)!.AsObject();
        missing.Remove("address");
        Reject(missing.ToJsonString());
        missing["adress"] = 10;
        Reject(missing.ToJsonString());
        foreach (var value in new[] { "null", "-1", "65536", "1.5", "true", "\"invalid\"" })
            Reject(valid.Replace("\"address\":0", "\"address\":" + value));
        Reject(valid.Replace("\"unit\":\"C\"", "\"unit\":\"C\",\"adress\":10"));
        Reject(valid.Replace("\"unit\":\"C\"", "\"unit\":\"C\",\"dataTyp\":\"float32\""));

        foreach (var area in MetricDefinition.Registers)
        foreach (ushort address in new ushort[] { 0, 10, 65535 })
        {
            var original = new MetricDefinition("X", area, address, 1, "", area is "coil" or "discrete" ? "bool" : "uint16");
            // Includes the existing computed isBit/type/order/width properties found in stored JSON.
            var decoded = JsonSerializer.Deserialize<MetricDefinition>(JsonSerializer.Serialize(original, EdgeTopics.Json), EdgeTopics.Json)!;
            decoded.Validate();
            Check(decoded == original, "Valid area/address or stored metric JSON did not round-trip.");
        }
        foreach (var type in MetricDefinition.RegisterTypes)
        {
            var metric = new MetricDefinition("X", "input", 65534, 1, "", type, "little");
            var decoded = JsonSerializer.Deserialize<MetricDefinition>(JsonSerializer.Serialize(metric, EdgeTopics.Json), EdgeTopics.Json)!;
            decoded.Validate();
            Check(decoded == metric, "Supported data type or word order stopped working.");
        }
        var defaults = JsonSerializer.Deserialize<MetricDefinition>(valid.Replace(",\"dataType\":\"int16\"", ""), EdgeTopics.Json)!;
        Check(defaults.Address == 0 && defaults.Type == "int16" && defaults.Order == "big", "Intentional type/order defaults changed.");

        using var store = new DeviceStore(":memory:");
        store.SaveInterface(new("tcp", ModbusInterface.Tcp,
            JsonSerializer.SerializeToElement(new TcpInterfaceSettings("127.0.0.1", 502), EdgeTopics.Json)), true);
        var device = new DeviceRegistration("device", "tcp", true, 1, 1, null, "",
            [new("Temperature", "holding", 0, .1, "C"), new("Pressure", "input", 10, 1, "kPa")]);
        store.Save(device, true);
        Check(store.Find("device")!.Metrics.Select(m => m.Address).SequenceEqual(new ushort[] { 0, 10 }), "Stored addresses changed.");
        var snapshot = store.Snapshot();
        var json = JsonSerializer.Serialize(snapshot, EdgeTopics.Json);
        var roundtrip = JsonSerializer.Deserialize<GatewayConfiguration>(json, EdgeTopics.Json)!;
        roundtrip.Validate();
        Check(roundtrip.Devices.Single().Metrics[0].Address == 0, "GET /config shape lost explicit address zero.");

        var directory = Path.Combine(Path.GetTempPath(), "metric-address-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config-cache.json");
            using var client = new ConfigClient(new DaqSettings { StateDirectory = directory });
            File.WriteAllText(path, json);
            Check(client.LoadCache()?.Devices.Single().Metrics[0].Address == 0, "Valid existing cache was rejected.");
            var invalid = JsonNode.Parse(json)!;
            var metric = invalid["devices"]![0]!["metrics"]![0]!.AsObject();
            metric.Remove("address");
            File.WriteAllText(path, invalid.ToJsonString());
            Check(client.LoadCache() is null, "Missing address in cached configuration was silently defaulted.");
            metric["address"] = 0;
            metric["adress"] = 10;
            File.WriteAllText(path, invalid.ToJsonString());
            Check(client.LoadCache() is null, "Unknown metric field in cache was silently ignored.");
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("PASS: Required metric address, typo/unknown-field rejection, explicit zero/nonzero/boundaries, stored JSON/config round-trip and cache validation.");
    }
}
