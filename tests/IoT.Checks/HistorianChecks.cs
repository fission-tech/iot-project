using EdgeGateway.Gateway.Sparkplug;
using EdgeGateway.Historian;
using Google.Protobuf;
using IoT.Contracts;
using Npgsql;

// Historian: how Sparkplug messages become history rows, and (when a test database is given) that storing is
// idempotent. Set HISTORIAN_TEST_CONNECTION to a TimescaleDB connection string to include the database part.
internal static class HistorianChecks
{
    private const string Prefix = "spBv1.0/warehouse-01/";
    private static readonly DateTimeOffset Received = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private static void Check(bool condition, string reason) { if (!condition) throw new Exception("Historian: " + reason); }

    public static async Task RunAsync()
    {
        CheckMapping();
        CheckStatements();
        var database = Environment.GetEnvironmentVariable("HISTORIAN_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(database))
        {
            Console.WriteLine("PASS: Historian mapping: payload timestamps, historical flag, DeviceSeq, quality of null metrics, signed integers, lifecycle events, ignored topics. (Database part skipped: HISTORIAN_TEST_CONNECTION not set.)");
            return;
        }
        await CheckDatabaseAsync(database);
        Console.WriteLine("PASS: Historian mapping and TimescaleDB storage: schema migration is repeatable, duplicates and redelivery are skipped, late readings keep their time.");
    }

    private static byte[] Reading(DateTimeOffset at, ulong deviceSeq, bool historical = false, string? quality = null) =>
        SparkplugPayloads.DeviceReading(7, new DeviceReading("fridge-01", deviceSeq, at, new()
        {
            ["Temperature"] = quality is null ? new(Number: 4.5) : new(Quality: quality),
            ["DoorOpen"] = new(Boolean: true)
        }), historical).ToByteArray();

    private static void CheckMapping()
    {
        using var dashboard = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRoot(), "grafana", "dashboards", "edge.json")));
        var timeline = dashboard.RootElement.GetProperty("panels").EnumerateArray().Single(panel => panel.GetProperty("id").GetInt32() == 6);
        Check(timeline.GetProperty("fieldConfig").GetProperty("defaults").GetProperty("custom").GetProperty("spanNulls").GetBoolean(),
            "Lifecycle state must continue across nulls introduced by other devices' event times.");
        var measured = Received.AddHours(-3);
        var rows = SparkplugRows.From(Prefix + "DDATA/gateway-01/fridge-01", Reading(measured, 42, historical: true), Received);
        Check(rows.Event is null, "DDATA is not a lifecycle event.");
        Check(rows.Readings.Length == 2, "Expected one row per metric, without DeviceSeq.");
        var temperature = rows.Readings.Single(row => row.Metric == "Temperature");
        Check(temperature.Time == measured, "A replayed reading must keep its measurement time, not the arrival time.");
        Check(temperature is { Node: "warehouse-01/gateway-01", Device: "fridge-01", Value: 4.5, Quality: MetricQuality.Good,
            Historical: true, DeviceSeq: 42 }, "Reading row mismatch.");
        Check(rows.Readings.Single(row => row.Metric == "DoorOpen").Value == 1, "Booleans are stored as 1/0.");

        var bad = SparkplugRows.From(Prefix + "DDATA/gateway-01/fridge-01", Reading(measured, 43, quality: MetricQuality.Stale), Received)
            .Readings.Single(row => row.Metric == "Temperature");
        Check(bad is { Value: null, Quality: MetricQuality.Stale }, "A stale metric is stored without a value and with its quality.");

        var signed = new Org.Eclipse.Tahu.Protobuf.Payload { Timestamp = 1, Seq = 1 };
        signed.Metrics.Add(new Org.Eclipse.Tahu.Protobuf.Payload.Types.Metric { Name = "Offset", Datatype = (uint)Org.Eclipse.Tahu.Protobuf.DataType.Int32, IntValue = unchecked((uint)-5) });
        Check(SparkplugRows.From(Prefix + "DDATA/gateway-01/fridge-01", signed.ToByteArray(), Received).Readings.Single().Value == -5,
            "Signed integers must be decoded from two's complement.");

        var birth = SparkplugRows.From(Prefix + "DBIRTH/gateway-01/fridge-01", Reading(measured, 41), Received);
        Check(birth.Event is { Kind: "DBIRTH", Device: "fridge-01" } && birth.Readings.Length == 2, "DBIRTH stores its event and its reading.");
        Check(birth.Event!.Time == Received && birth.Readings.All(row => row.Time == measured),
            "DBIRTH lifecycle time must be arrival time, independently of its saved measurement time.");
        var nextBirth = SparkplugRows.From(Prefix + "DBIRTH/gateway-01/fridge-01", Reading(measured, 41), Received.AddMinutes(1));
        Check(nextBirth.Event!.Time > birth.Event.Time && nextBirth.Readings.SequenceEqual(birth.Readings),
            "Reconnecting with the same saved sample must create a new birth without changing the reading.");
        var nodeBirth = SparkplugRows.From(Prefix + "NBIRTH/gateway-01", SparkplugPayloads.NodeBirth(237).ToByteArray(), Received);
        Check(nodeBirth.Event is { Kind: "NBIRTH" } && nodeBirth.Event.Time == Received, "NBIRTH uses arrival time.");
        var death = SparkplugRows.From(Prefix + "DDEATH/gateway-01/fridge-01", SparkplugPayloads.DeviceDeath(9).ToByteArray(), Received);
        Check(death.Event is { Kind: "DDEATH" } && death.Event.Time == Received && death.Readings.Length == 0, "DDEATH is an arrival-time event only.");
        var nodeDeath = SparkplugRows.From(Prefix + "NDEATH/gateway-01", SparkplugPayloads.NodeDeath(3).ToByteArray(), Received);
        Check(nodeDeath.Event is { Kind: "NDEATH", Device: "" } && nodeDeath.Event.Time == Received, "NDEATH uses the arrival time.");
        Check(SparkplugRows.From("spBv1.0/STATE/iot-host", "{\"online\":true}"u8.ToArray(), Received) == SparkplugRows.None,
            "STATE messages are ignored.");
        Check(SparkplugRows.From(Prefix + "NDATA/gateway-01", SparkplugPayloads.NodeData(1, 5).ToByteArray(), Received) == SparkplugRows.None,
            "Node data is not stored.");
    }

    private static void CheckStatements()
    {
        var statements = HistoryStore.Statements("-- comment\nCREATE TABLE a (\n  x int -- inline\n);\n\nSELECT 1;\nSELECT 2").ToArray();
        Check(statements.Length == 3 && statements[0].StartsWith("CREATE TABLE a") && statements[2] == "SELECT 2",
            "Schema scripts must split into single statements.");
    }

    private static async Task CheckDatabaseAsync(string connectionString)
    {
        var settings = new HistorySettings { ConnectionString = connectionString };
        await using var store = new HistoryStore(connectionString);
        // Hypertables must be dropped one per statement.
        await using (var reset = NpgsqlDataSource.Create(connectionString))
            foreach (var sql in (string[])["DROP MATERIALIZED VIEW IF EXISTS readings_1m", "DROP TABLE IF EXISTS readings",
                "DROP TABLE IF EXISTS device_events", "DROP TABLE IF EXISTS schema_version"])
            {
                await using var command = reset.CreateCommand(sql);
                await command.ExecuteNonQueryAsync();
            }
        await store.MigrateAsync(settings, CancellationToken.None);
        // Upgrade the already-applied experimental column without deleting existing history.
        await using (var previous = NpgsqlDataSource.Create(connectionString))
            foreach (var sql in (string[])[
                "INSERT INTO device_events (time,node,device,kind) VALUES ('2026-11-04', 'migration-check', 'device-01', 'DBIRTH')",
                "ALTER TABLE device_events ADD COLUMN bd_seq bigint",
                "DELETE FROM schema_version WHERE version = 3"])
            {
                await using var command = previous.CreateCommand(sql);
                await command.ExecuteNonQueryAsync();
            }
        await store.MigrateAsync(settings, CancellationToken.None);
        await store.MigrateAsync(settings, CancellationToken.None);
        await using (var previous = NpgsqlDataSource.Create(connectionString))
        {
            await using var command = previous.CreateCommand("SELECT count(*) FROM device_events WHERE node = 'migration-check' AND event_order IS NOT NULL");
            Check(Convert.ToInt64(await command.ExecuteScalarAsync()) == 1, "Migration must preserve existing events and event_order.");
            await using var columns = previous.CreateCommand("SELECT count(*) FROM information_schema.columns WHERE table_schema='public' AND table_name='device_events' AND column_name='bd_seq'");
            Check(Convert.ToInt64(await columns.ExecuteScalarAsync()) == 0, "bd_seq must be removed from fresh and upgraded databases.");
        }

        var late = Received.AddDays(-2);
        var message = Reading(late, 100, historical: true);
        var rows = SparkplugRows.From(Prefix + "DDATA/gateway-01/fridge-01", message, Received);
        await store.WriteAsync(rows, CancellationToken.None);
        await store.WriteAsync(rows, CancellationToken.None); // QoS 1 redelivery
        await store.WriteAsync(SparkplugRows.From(Prefix + "DBIRTH/gateway-01/fridge-01", message, Received), CancellationToken.None);
        var death = SparkplugRows.From(Prefix + "DDEATH/gateway-01/fridge-01", SparkplugPayloads.DeviceDeath(9).ToByteArray(), Received);
        await store.WriteAsync(death, CancellationToken.None);
        await store.WriteAsync(death, CancellationToken.None);

        await using var source = NpgsqlDataSource.Create(connectionString);
        async Task<long> Count(string sql)
        {
            await using var command = source.CreateCommand(sql);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        Check(await Count("SELECT count(*) FROM readings") == 2, "Redelivered and birth-repeated readings were stored twice.");
        Check(await Count($"SELECT count(*) FROM readings WHERE time = '{late.UtcDateTime:O}' AND historical") == 2,
            "A late reading must be stored at its measurement time.");
        Check(await Count("SELECT count(*) FROM device_events WHERE node = 'warehouse-01/gateway-01'") == 2, "Expected one DBIRTH and one DDEATH event.");
        Check(await Count("SELECT count(*) FROM schema_version") == 3, "Migrations recorded more than once.");
        Check(await Count("SELECT count(*) FROM timescaledb_information.jobs WHERE hypertable_name IN ('readings') OR proc_name = 'policy_refresh_continuous_aggregate'") >= 3,
            "Compression, retention and rollup policies are missing.");
        await CheckLifecycleAsync(store, source, connectionString);
    }

    private static async Task CheckLifecycleAsync(HistoryStore store, NpgsqlDataSource source, string connectionString)
    {
        const string node = "warehouse-01/lifecycle-check";
        const string device = "device-01";
        var time = Received.AddHours(1);
        async Task Send(string kind, string deviceId = "", int seconds = 0) =>
            await store.WriteAsync(new([], new(time.AddSeconds(seconds), node, deviceId, kind)), CancellationToken.None);
        async Task<long> Count(string condition)
        {
            await using var command = source.CreateCommand($"SELECT count(*) FROM device_events WHERE node = '{node}' AND {condition}");
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        await Send("NBIRTH");
        await Send("DBIRTH", device);
        await Send("NDEATH", seconds: 1);
        await Send("NBIRTH", seconds: 2);
        await Send("DBIRTH", device, seconds: 2);
        Check(await Count("kind = 'DBIRTH'") == 2, "Reconnect must record another device birth.");

        // Same timestamp: use the last observed event instead of arbitrary UNION ordering.
        await Send("NDEATH", seconds: 12);
        await Send("DBIRTH", device, seconds: 12);
        using var dashboard = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(
            FindRoot(), "grafana", "dashboards", "edge.json")));
        var query = dashboard.RootElement.GetProperty("panels").EnumerateArray().Single(panel => panel.GetProperty("id").GetInt32() == 6)
            .GetProperty("targets")[0].GetProperty("rawSql").GetString()!
            .Replace("$node", $"'{node}'").Replace("$device", $"'{device}'").Replace("$__timeTo()", $"'{time.AddSeconds(12):O}'::timestamptz");
        await using (var command = source.CreateCommand(query))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            var latest = -1;
            while (await reader.ReadAsync()) latest = reader.GetInt32(2);
            Check(latest == 1, "Grafana must choose the last arrival for tied lifecycle timestamps.");
        }
        await Send("NDEATH", seconds: 13);
        Check(await Count("kind = 'NDEATH'") == 3, "Deaths must be stored in observed order.");

        await using var restarted = new HistoryStore(connectionString);
        await restarted.WriteAsync(new([], new(time.AddSeconds(14), node, device, "DBIRTH")), CancellationToken.None);
        Check(await Count("kind = 'DBIRTH'") == 4, "Restarting the writer must preserve and append lifecycle history.");
        await store.WriteAsync(new([], new(time, "warehouse-01/no-birth", "", "NDEATH")), CancellationToken.None);
        await using (var command = source.CreateCommand("SELECT count(*) FROM device_events WHERE node='warehouse-01/no-birth' AND kind='NDEATH'"))
            Check(Convert.ToInt64(await command.ExecuteScalarAsync()) == 1, "A death without a cached birth must still be recorded.");
        // Exercise the real panel SQL with two staggered series, including a trailing alignment gap.
        var timelineSql = dashboard.RootElement.GetProperty("panels").EnumerateArray().Single(panel => panel.GetProperty("id").GetInt32() == 6)
            .GetProperty("targets")[0].GetProperty("rawSql").GetString()!;
        foreach (var (lastEvent, first, second) in new[] { ("DBIRTH", 1, 1), ("DDEATH", 0, 1), ("NDEATH", 0, 0) })
        {
            var fixture = """
                WITH device_events(time,node,device,kind,event_order) AS (VALUES
                  ('2026-11-04 12:00:00+00'::timestamptz,'test/gateway','first','DBIRTH',1),
                  ('2026-11-04 12:00:00.012+00'::timestamptz,'test/gateway','second','DBIRTH',2),
                  ('2026-11-04 12:01:00+00'::timestamptz,'test/gateway',DEVICE,KIND,3)
                ), devices AS
                """.Replace("DEVICE", lastEvent == "NDEATH" ? "''" : "'first'").Replace("KIND", $"'{lastEvent}'");
            var sql = timelineSql.Replace("WITH devices AS", fixture)
                .Replace("$node", "'test/gateway'").Replace("$device", "'first','second'")
                .Replace("$__timeTo()", "'2026-11-04 12:02:00+00'::timestamptz");
            await using var command = source.CreateCommand(sql);
            await using var reader = await command.ExecuteReaderAsync();
            var endStates = new Dictionary<string, int>();
            while (await reader.ReadAsync())
                if (reader.GetDateTime(0) == new DateTime(2026, 11, 4, 12, 2, 0, DateTimeKind.Utc))
                    endStates.Add(reader.GetString(1), reader.GetInt32(2));
            Check(endStates.Count == 2 && endStates["first"] == first && endStates["second"] == second,
                $"Timeline must carry both devices to the range end and preserve {lastEvent} state.");
        }
        Console.WriteLine("PASS: Lifecycle arrival order, same-sample reconnect, writer restart, Grafana timestamp ties and multi-device timeline boundaries.");
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "IoT.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new Exception("Repository root not found.");
    }
}
