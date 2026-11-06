using System.Text.Json;
using EdgeGateway.Configuration;
using EdgeGateway.Gateway.Sparkplug;
using IoT.Contracts;
using Microsoft.Data.Sqlite;

// Permanent metric IDs: assigned by the registry, kept through renames, and announced by the gateway in DBIRTH.
internal static class MetricIdentityChecks
{
    private static void Check(bool value, string reason)
    { if (!value) throw new Exception("Metric identity: " + reason); }

    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "metric-identity-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "configuration.db");
        try
        {
            using (var store = new DeviceStore(path))
            {
                store.SaveInterface(new("tcp", ModbusInterface.Tcp,
                    JsonSerializer.SerializeToElement(new TcpInterfaceSettings("127.0.0.1", 502), EdgeTopics.Json)), true);
                DeviceRegistration Device(params MetricDefinition[] metrics) => new("tank", "tcp", true, 1, 1, null, "", metrics);
                store.Save(Device(new("Level", "input", 0, 1, "%"), new("Pump", "coil", 0, 1, "")), true);
                var saved = store.Find("tank")!.Metrics;
                Check(saved.All(metric => metric.Id is { Length: 32 }) && saved[0].Id != saved[1].Id, "Every new metric gets its own ID.");
                var (level, pump) = (saved[0].Id, saved[1].Id);

                // A client that does not know IDs (curl, an old form) keeps them by sending the same names.
                store.Save(Device(new("Level", "input", 0, 1, "mm"), new("Pump", "coil", 0, 1, "")), false);
                Check(store.Find("tank")!.Metrics.Select(metric => metric.Id).SequenceEqual([level, pump]), "Same names keep their IDs.");

                // A rename sends the ID with the new name. A new metric that reuses the old name gets a new ID.
                store.Save(Device(new("TankLevel", "input", 0, 1, "%", Id: level), new("Pump", "coil", 0, 1, ""),
                    new("Level", "input", 1, 1, "%")), false);
                var renamed = store.Find("tank")!.Metrics;
                Check(renamed[0] is { Name: "TankLevel" } && renamed[0].Id == level && renamed[1].Id == pump
                      && renamed[2].Id is { } fresh && fresh != level && fresh != pump,
                    "A rename keeps the ID; a reused name does not take the renamed metric's ID.");
                Check(store.Snapshot().Devices.Single().Metrics[0].Id == level, "GET /config carries the IDs to the gateway.");

                Reject(() => store.Save(Device(new("A", "input", 0, 1, "", Id: "same"), new("B", "input", 1, 1, "", Id: "same")), false),
                    "duplicate metric IDs");
                Reject(() => store.Save(Device(new MetricDefinition("A", "input", 0, 1, "", Id: "has space")), false), "an ID with whitespace");
            }

            // Devices saved before IDs existed get them when the registry starts, with a version bump for the gateway.
            var stripped = (string)Sql(path, "SELECT Data FROM Devices WHERE Id = 'tank'")!;
            var node = System.Text.Json.Nodes.JsonNode.Parse(stripped)!;
            foreach (var metric in node["metrics"]!.AsArray()) metric!.AsObject().Remove("id");
            Sql(path, $"UPDATE Devices SET Data = '{node.ToJsonString().Replace("'", "''")}' WHERE Id = 'tank'");
            var version = (long)Sql(path, "SELECT Value FROM Meta WHERE Key = 'Version'")!;
            using (var store = new DeviceStore(path))
            {
                Check(store.Find("tank")!.Metrics.All(metric => metric.Id is { Length: 32 })
                      && store.Snapshot().Version == version + 1, "Metrics without IDs get them once at startup.");
            }
            using (var store = new DeviceStore(path))
                Check(store.Snapshot().Version == version + 1, "A registry whose metrics all have IDs is not bumped again.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }

        var reading = new DeviceReading("tank", 1, DateTimeOffset.UtcNow,
            new() { ["Level"] = new(Number: 1), ["Pump"] = new(Quality: MetricQuality.Bad) },
            new() { ["Level"] = "id-level", ["Pump"] = "id-pump" });
        var birth = SparkplugPayloads.DeviceReading(1, reading, birth: true);
        Check(birth.Metrics.Single(metric => metric.Name == "Level") is var level2 && SparkplugDecoder.MetricIdOf(level2) == "id-level"
              && birth.Metrics.Single(metric => metric.Name == "Pump") is var pump2 && SparkplugDecoder.MetricIdOf(pump2) == "id-pump"
              && SparkplugDecoder.QualityOf(pump2) == MetricQuality.Bad && SparkplugDecoder.MetricIdOf(birth.Metrics.Single(metric => metric.Name == "DeviceSeq")) is null,
            "DBIRTH must carry each metric's ID next to its quality.");
        Check(SparkplugPayloads.DeviceReading(2, reading).Metrics.All(metric => SparkplugDecoder.MetricIdOf(metric) is null),
            "DDATA carries names only.");
        var restored = JsonSerializer.Deserialize<DeviceReading>(JsonSerializer.Serialize(reading, EdgeTopics.Json), EdgeTopics.Json)!;
        Check(restored.MetricIds!["Level"] == "id-level", "The outbox must keep the IDs a reading was taken with.");
        Check(reading.MetricSignature() != (reading with { MetricIds = new() { ["Level"] = "other", ["Pump"] = "id-pump" } }).MetricSignature()
              && (reading with { MetricIds = null }).MetricSignature() == "Level,Pump",
            "A changed ID must cause a rebirth; readings without IDs keep the name-only signature.");
        Console.WriteLine("PASS: Metric identity: registry IDs on create, kept by name and through renames, unique, backfilled once; DBIRTH metricId property, outbox persistence, rebirth on ID change.");
    }

    private static void Reject(Action action, string what)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new Exception($"Metric identity: {what} must be rejected.");
    }

    private static object? Sql(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
