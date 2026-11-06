using EdgeGateway.Gateway.Sparkplug;
using EdgeGateway.Historian;
using EdgeGateway.Historian.Dashboards;
using Google.Protobuf;
using IoT.Contracts;
using Npgsql;
using Org.Eclipse.Tahu.Protobuf;

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
        CheckDeviceDashboards();
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
                "DROP TABLE IF EXISTS device_events", "DROP TABLE IF EXISTS device_catalog", "DROP TABLE IF EXISTS metric_catalog",
                "DROP TABLE IF EXISTS metric_names",
                "DROP TABLE IF EXISTS schema_version"])
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
        Check(await Count("SELECT count(*) FROM schema_version") == 5, "Migrations recorded more than once.");
        Check(await Count("SELECT count(*) FROM timescaledb_information.jobs WHERE hypertable_name IN ('readings') OR proc_name = 'policy_refresh_continuous_aggregate'") >= 3,
            "Compression, retention and rollup policies are missing.");
        await CheckLifecycleAsync(store, source, connectionString);
        await CheckReplayCountsAsync(store, source);
        await CheckLatestValuesAsync(store, source);
        await CheckCatalogAsync(store, source);
        await CheckMetricIdentityAsync(store, source);
    }

    private static async Task CheckLatestValuesAsync(HistoryStore store, NpgsqlDataSource source)
    {
        const string node = "latest-check/gateway";
        var now = DateTimeOffset.UtcNow;
        using var dashboard = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRoot(), "grafana", "dashboards", "edge.json")));
        var panel = dashboard.RootElement.GetProperty("panels").EnumerateArray().Single(panel => panel.GetProperty("id").GetInt32() == 2);
        var panelSql = panel.GetProperty("targets")[0].GetProperty("rawSql").GetString()!;
        async Task Send(string gateway, string device, int secondsAgo, Dictionary<string, MetricValue> metrics, bool historical = false)
        {
            var reading = new DeviceReading(device, (ulong)secondsAgo, now.AddSeconds(-secondsAgo), metrics);
            await store.WriteAsync(SparkplugRows.From($"spBv1.0/latest-check/DDATA/{gateway}/{device}",
                SparkplugPayloads.DeviceReading(1, reading, historical).ToByteArray(), now), CancellationToken.None);
        }
        async Task<List<(DateTime Time, string Metric, double? Value)>> Latest(string metrics = "'Temperature','Humidity','Pressure'", string devices = "'first'", string nodes = "'latest-check/gateway'")
        {
            await using var command = source.CreateCommand(panelSql.Replace("$node", nodes).Replace("$device", devices).Replace("$metric", metrics));
            await using var reader = await command.ExecuteReaderAsync();
            var result = new List<(DateTime, string, double?)>();
            while (await reader.ReadAsync()) result.Add((reader.GetDateTime(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetDouble(2)));
            return result;
        }
        await Send("gateway", "first", 120, new() { ["Temperature"] = new(Number: 4.5), ["Humidity"] = new(Number: 65) });
        await Send("gateway", "first", 60, new() { ["Temperature"] = new(Number: 5) });
        var latest = await Latest();
        Check(latest.Count == 1 && latest[0].Value == 5, "Removed Humidity must not survive in Latest value.");
        Check((await Latest("'Humidity'")).Count == 0, "Selecting a removed metric must not resurrect old values.");
        await Send("gateway", "first", 30, new() { ["Temperature"] = new(Number: 6), ["Pressure"] = new(Number: 120) });
        Check((await Latest()).Count == 2, "A newly added metric must appear in the latest full sample.");
        await Send("gateway", "first", 15, new() { ["Temperature"] = new(Quality: MetricQuality.Bad), ["Pressure"] = new(Number: 121) });
        latest = await Latest();
        Check(latest.Count == 2 && latest.Single(row => row.Metric.EndsWith("Temperature [Bad]")).Value is null,
            "Latest Bad/null must not fall back to an older Good temperature.");
        await Send("gateway", "first", 90, new() { ["Humidity"] = new(Number: 66) }, historical: true);
        Check((await Latest("'Humidity'")).Count == 0, "Late historical replay must not restore a removed metric.");
        await Send("gateway", "second", 45, new() { ["Humidity"] = new(Number: 55) });
        latest = await Latest(devices: "'first','second'");
        Check(latest.Count == 3 && latest.Select(row => row.Time).SequenceEqual(latest.Select(row => row.Time).Order()),
            "Each device must use its own latest sample and output must remain chronological.");
        await Send("other", "first", 5, new() { ["Temperature"] = new(Number: 99) });
        Check((await Latest()).Single(row => row.Metric.EndsWith("Pressure")).Value == 121
            && (await Latest(nodes: "'latest-check/other'")).Single().Value == 99,
            "Gateway filtering must isolate identical device IDs.");
        await Send("gateway", "expired", 172800, new() { ["Temperature"] = new(Number: 88) });
        Check((await Latest(devices: "'expired'")).Count == 0 && (await Latest(devices: "'unknown'")).Count == 0,
            "Keep the 24-hour lookup and return no fabricated value for missing devices.");
        await using var history = source.CreateCommand($"SELECT count(*) FROM readings WHERE node='{node}' AND device='first' AND metric='Humidity'");
        Check(Convert.ToInt64(await history.ExecuteScalarAsync()) == 2, "Removed metrics must remain in historical storage.");
        await Send("gateway", "first", 8, new() { ["Temperature"] = new(Number: 4.5), ["Pressure"] = new(Quality: MetricQuality.Stale) });
        latest = await Latest();
        Check(latest.Single(row => row.Metric.EndsWith("Pressure [Stale]")).Value is null
            && latest.Single(row => row.Metric.EndsWith("Temperature")).Value == 4.5,
            "Stale must have its own label, and quality recovery must restore the normal numeric field.");
        foreach (var quality in new[] { "Bad", "Stale" })
        {
            var fieldName = $"first · Pressure [{quality}]";
            var matches = panel.GetProperty("fieldConfig").GetProperty("overrides").EnumerateArray()
                .Where(item => System.Text.RegularExpressions.Regex.IsMatch(fieldName, "^" + item.GetProperty("matcher").GetProperty("options").GetString()! + "$")).ToArray();
            Check(matches.Length == 1 && matches[0].GetProperty("properties")[0].GetProperty("value").GetString() == quality + " value",
                "The null-value override must match the actual series quality.");
        }
        Console.WriteLine("PASS: Latest-value query: removed/added metrics, filters, Bad/null, late replay, independent devices, gateway isolation, 24-hour lookup, history retention and chronological output.");
    }

    private static async Task CheckReplayCountsAsync(HistoryStore store, NpgsqlDataSource source)
    {
        var demo = new Demo.Control.SparkplugView();
        const string node = "warehouse-01/replay-check";
        var time = Received.AddDays(-1);
        using var dashboard = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRoot(), "grafana", "dashboards", "edge.json")));
        var panelSql = dashboard.RootElement.GetProperty("panels").EnumerateArray().Single(panel => panel.GetProperty("id").GetInt32() == 4)
            .GetProperty("targets")[0].GetProperty("rawSql").GetString()!;
        async Task<long> PanelCount(string metric = "'Temperature','Humidity','Switch'", string device = "'first','second'", int fromSeconds = -1)
        {
            var sql = panelSql.Replace("$__timeFilter(time)", $"time BETWEEN '{time.AddSeconds(fromSeconds):O}'::timestamptz AND '{time.AddMinutes(1):O}'::timestamptz")
                .Replace("$node", $"'{node}'").Replace("$device", device).Replace("$metric", metric);
            await using var command = source.CreateCommand(sql);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        async Task Send(string kind, string device, ulong seq, bool historical, double temperature = 4.5)
        {
            var reading = new DeviceReading(device, seq, time.AddSeconds((long)seq), new()
            {
                ["Temperature"] = new(Number: temperature), ["Humidity"] = new(Number: 65), ["Switch"] = new(Boolean: true)
            });
            var payload = SparkplugPayloads.DeviceReading(1, reading, historical);
            var topic = $"spBv1.0/warehouse-01/{kind}/replay-check/{device}";
            demo.Apply(topic, payload);
            await store.WriteAsync(SparkplugRows.From(topic, payload.ToByteArray(), Received), CancellationToken.None);
        }
        await Send("DBIRTH", "first", 1, false);
        Check(await PanelCount() == 0, "An unflagged birth is not a replayed sample.");
        await Send("DDATA", "first", 1, true);
        await Send("DDATA", "first", 1, true);
        Check(await PanelCount() == 1 && demo.Summaries().Sum(device => device.Historical) == 1,
            "A three-metric replay and its retry must count once in Grafana and Demo Console.");
        await Send("DDATA", "first", 2, false);
        await Send("DDATA", "first", 2, true, temperature: 999);
        await Send("DDATA", "first", 2, false);
        Check(await PanelCount() == 2 && demo.Summaries().Sum(device => device.Historical) == 2,
            "Live-first historical replay must count once, and later live redelivery must not clear the flag.");
        await Send("DDATA", "first", 3, true);
        await Send("DDATA", "second", 3, true);
        Check(await PanelCount() == 4 && demo.Summaries().Sum(device => device.Historical) == 4,
            "Different devices with the same DeviceSeq must count independently.");
        Check(await PanelCount("'Temperature'") == 4 && await PanelCount(device: "'second'") == 1,
            "Metric and device filters must count matching samples once.");
        Check(await PanelCount(fromSeconds: 4) == 0, "Replay must still respect original measurement-time filtering.");
        await using var values = source.CreateCommand($"SELECT count(*) FROM readings WHERE node='{node}' AND (NOT historical OR (metric='Temperature' AND value<>4.5) OR quality<>'good')");
        Check(Convert.ToInt64(await values.ExecuteScalarAsync()) == 0, "Historical flag merge must not overwrite first-stored values or quality.");
        Console.WriteLine("PASS: Grafana/Demo replay sample counts match across three metrics, retry duplicates, birth/live-first replay, distinct devices, filters and original-value preservation.");
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

    // Generated per-device dashboards: rows by register area, panel type by output, units, stable UIDs.
    private static void CheckDeviceDashboards()
    {
        var birth = new Org.Eclipse.Tahu.Protobuf.Payload { Timestamp = 1, Seq = 1 };
        birth.Metrics.Add(new Org.Eclipse.Tahu.Protobuf.Payload.Types.Metric { Name = "Pump", Datatype = (uint)Org.Eclipse.Tahu.Protobuf.DataType.Boolean, BooleanValue = true });
        birth.Metrics.Add(new Org.Eclipse.Tahu.Protobuf.Payload.Types.Metric { Name = "Level", Datatype = (uint)Org.Eclipse.Tahu.Protobuf.DataType.Double, DoubleValue = 2 });
        birth.Metrics.Add(new Org.Eclipse.Tahu.Protobuf.Payload.Types.Metric { Name = "Valve", Datatype = (uint)Org.Eclipse.Tahu.Protobuf.DataType.Double, IsNull = true });
        birth.Metrics.Add(new Org.Eclipse.Tahu.Protobuf.Payload.Types.Metric { Name = "DeviceSeq", Datatype = (uint)Org.Eclipse.Tahu.Protobuf.DataType.Int64, LongValue = 5 });
        var births = SparkplugRows.From(Prefix + "DBIRTH/gateway-01/tank", birth.ToByteArray(), Received).Births!;
        Check(births.Length == 3 && births.Single(metric => metric.Metric == "Pump").Kind == SparkplugRows.Boolean
            && births.Single(metric => metric.Metric == "Level").Kind == SparkplugRows.Numeric
            && births.Single(metric => metric.Metric == "Valve").Kind is null,
            "A birth must report boolean/numeric per metric, unknown for null values, and skip DeviceSeq.");
        Check(SparkplugRows.From(Prefix + "DDATA/gateway-01/tank", birth.ToByteArray(), Received).Births is null, "Only births describe metrics.");

        var device = new DeviceCatalog("warehouse-01/gateway-01", "Tank #1", true,
        [
            new("key-temperature", "Temperature", SparkplugRows.Numeric, "input", "int16", "°C", 0.1, true),
            new("Setpoint", "Setpoint", SparkplugRows.Numeric, "holding", "float32", "bar", 1, true),
            // A null birth value left it numeric; the registry's register area makes it boolean.
            new("Pump", "Pump", SparkplugRows.Numeric, "coil", "bool", "", 1, true),
            new("Raw", "Raw", SparkplugRows.Boolean, null, null, null, null, true),
            new("O'Brien", "O'Brien", SparkplugRows.Numeric, "input", "uint16", "widgets", 1, false)
        ]);
        var json = System.Text.Json.Nodes.JsonNode.Parse(DashboardGenerator.Render(device))!;
        var panels = json["panels"]!.AsArray().Select(panel => panel!).ToArray();
        string[] rows = panels.Where(panel => (string)panel["type"]! == "row").Select(panel => (string)panel["title"]!).ToArray();
        Check(rows.SequenceEqual(["Status", "Input registers · measurements (1)", "Holding registers · setpoints (1)", "Coils · outputs (1)",
            "Other metrics (1)", "Removed metrics · history only (1)", "Data quality"]),
            "Rows must follow register areas in order, omit empty areas and keep removed metrics apart: " + string.Join(" | ", rows));
        System.Text.Json.Nodes.JsonNode Titled(string title, string type) =>
            panels.Single(panel => (string)panel["title"]! == title && (string)panel["type"]! == type);
        var temperature = Titled("Temperature", "timeseries")["fieldConfig"]!["defaults"]!;
        Check((string)temperature["unit"]! == "celsius" && (int)temperature["decimals"]! == 1,
            "A scaled int16 in °C must chart in Celsius with one decimal.");
        Check(((string)Titled("Temperature", "timeseries")["targets"]![0]!["rawSql"]!).Contains("metric = 'key-temperature'")
              && (string)temperature["displayName"]! == "Temperature",
            "Panels must query by the permanent key and show the current name, so a rename keeps their history.");
        var setpoint = Titled("Setpoint", "timeseries")["fieldConfig"]!["defaults"]!;
        Check((string)setpoint["custom"]!["lineInterpolation"]! == "stepAfter" && (string)setpoint["unit"]! == "pressurebar"
            && (int)setpoint["decimals"]! == 2, "Holding registers must draw stepped lines; float32 shows 2 decimals.");
        Check(panels.Count(panel => (string)panel["title"]! == "Pump" && (string)panel["type"]! == "state-timeline") == 1
              && panels.Count(panel => (string)panel["title"]! == "Raw" && (string)panel["type"]! == "state-timeline") == 1,
            "Coils and boolean metrics without registry data must use state timelines.");
        var removed = Titled("O'Brien", "timeseries");
        Check(((string)removed["targets"]![0]!["rawSql"]!).Contains("metric = 'O''Brien'")
              && (string)removed["fieldConfig"]!["defaults"]!["unit"]! == "suffix: widgets",
            "Metric names must be escaped in SQL; unknown units are shown as a suffix.");
        Check(panels.Where(panel => (string)panel["type"]! != "row").All(panel => (int)panel["gridPos"]!["x"]! + (int)panel["gridPos"]!["w"]! <= 24),
            "Panels must fit the 24-column grid.");
        Check(panels.Select(panel => (int)panel["id"]!).Distinct().Count() == panels.Length, "Panel IDs must be unique.");

        var uid = (string)json["uid"]!;
        Check(uid == DeviceCatalog.DashboardUid(device.Node, device.Device) && uid.Length <= 40 && uid.StartsWith("dev-tank--1-")
              && uid != DeviceCatalog.DashboardUid("warehouse-02/gateway-01", device.Device),
            "Dashboard UIDs must be stable, at most 40 characters, and differ per gateway.");
        Check(DeviceCatalog.DashboardUid("n", new string('x', 200)).Length <= 40, "Long device IDs must be shortened.");
        Check((string)json["title"]! == "Tank #1 · warehouse-01/gateway-01"
              && (string)System.Text.Json.Nodes.JsonNode.Parse(DashboardGenerator.Render(device with { InRegistry = false }))!["title"]!
                 == "Tank #1 · warehouse-01/gateway-01 (removed)", "A removed device keeps its dashboard, marked removed.");
        Check(DashboardGenerator.Render(device) == DashboardGenerator.Render(device), "Rendering must be deterministic.");

        var directory = Path.Combine(Path.GetTempPath(), "dashboards-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, uid + ".json");
            Check(DashboardGenerator.WriteIfChanged(path, "a") && !DashboardGenerator.WriteIfChanged(path, "a")
                  && DashboardGenerator.WriteIfChanged(path, "b") && File.ReadAllText(path) == "b" && !File.Exists(path + ".tmp"),
                "Dashboards must be written atomically and only when they change.");
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("PASS: Device dashboards: birth kinds, register-area rows, panel type per output, stepped setpoints, units/decimals, SQL escaping, removed metrics and devices, stable UIDs, change-only atomic writes.");
    }

    // Catalog from births and the registry, and the generator writing a file per device.
    private static async Task CheckCatalogAsync(HistoryStore store, NpgsqlDataSource source)
    {
        Payload Birth(bool nullPump)
        {
            var payload = new Payload { Timestamp = 1, Seq = 1 };
            payload.Metrics.Add(nullPump
                ? new Payload.Types.Metric { Name = "Pump", Datatype = (uint)DataType.Double, IsNull = true }
                : new Payload.Types.Metric { Name = "Pump", Datatype = (uint)DataType.Boolean, BooleanValue = true });
            payload.Metrics.Add(new Payload.Types.Metric { Name = "Level", Datatype = (uint)DataType.Double, DoubleValue = 3 });
            return payload;
        }
        var topic = "spBv1.0/catalog/DBIRTH/gateway-01/tank";
        await store.WriteAsync(SparkplugRows.From(topic, Birth(nullPump: false).ToByteArray(), Received), CancellationToken.None);
        // A later birth with a bad (null) value must not forget that Pump is boolean.
        await store.WriteAsync(SparkplugRows.From(topic, Birth(nullPump: true).ToByteArray(), Received.AddMinutes(1)), CancellationToken.None);
        DeviceCatalog Tank(DeviceCatalog[] catalog) => catalog.Single(device => device.Node == "catalog/gateway-01");
        var tank = Tank(await store.ReadCatalogAsync(CancellationToken.None));
        Check(tank is { Device: "tank", InRegistry: true } && tank.Metrics.Length == 2
              && tank.Metrics.Single(metric => metric.Metric == "Pump").Kind == SparkplugRows.Boolean
              && tank.Metrics.Single(metric => metric.Metric == "Level") is { Kind: SparkplugRows.Numeric, Register: null },
            "Births must create catalog rows and keep known kinds through null values.");

        await store.ApplyRegistryAsync([new RegistryMetric("tank", "Level", "input", "float32", "%", 1)], ["tank"], CancellationToken.None);
        tank = Tank(await store.ReadCatalogAsync(CancellationToken.None));
        Check(tank.Metrics.Single(metric => metric.Metric == "Level") is { Register: "input", DataType: "float32", Unit: "%", InRegistry: true }
              && tank.Metrics.Single(metric => metric.Metric == "Pump").InRegistry == false && tank.InRegistry,
            "The registry must describe known metrics and mark metrics it no longer has.");
        await store.ApplyRegistryAsync([], [], CancellationToken.None);
        tank = Tank(await store.ReadCatalogAsync(CancellationToken.None));
        Check(!tank.InRegistry && tank.Metrics.Single(metric => metric.Metric == "Level").Register == "input",
            "A device removed from the registry is kept, marked removed, with its last description.");
        await using (var command = source.CreateCommand("SELECT uid FROM device_catalog WHERE node = 'catalog/gateway-01'"))
            Check((string)(await command.ExecuteScalarAsync())! == tank.Uid, "The overview links through the stored UID.");

        var directory = Path.Combine(Path.GetTempPath(), "dashboards-" + Guid.NewGuid().ToString("N"));
        try
        {
            var generator = new DashboardGenerator(store, null, directory, TimeSpan.FromMinutes(1),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<DashboardGenerator>.Instance);
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(generator.DevicesDirectory).FullName, "dev-gone-000000.json"), "{}");
            await generator.SyncAsync(CancellationToken.None);
            var file = Path.Combine(generator.DevicesDirectory, tank.Uid + ".json");
            Check(File.Exists(file) && File.ReadAllText(file).Contains("tank · catalog/gateway-01 (removed)"),
                "The generator must write one dashboard per catalog device.");
            Check(!File.Exists(Path.Combine(generator.DevicesDirectory, "dev-gone-000000.json")), "Dashboards of devices no longer in the catalog are deleted.");
            var written = File.GetLastWriteTimeUtc(file);
            await generator.SyncAsync(CancellationToken.None);
            Check(File.GetLastWriteTimeUtc(file) == written, "An unchanged dashboard must not be rewritten.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        Console.WriteLine("PASS: Metric catalog: births, kind kept through null values, registry descriptions, removed metrics and devices, stored UID, generator files and stale cleanup.");
    }

    // Renames keep one history: readings are stored under a permanent key, and names (current and old) map to it.
    private static async Task CheckMetricIdentityAsync(HistoryStore store, NpgsqlDataSource source)
    {
        const string node = "identity/gateway-01";
        var start = Received.AddHours(-1);
        ulong seq = 0;
        async Task Send(string kind, string device, int minute, Dictionary<string, MetricValue> metrics,
            Dictionary<string, string>? ids = null, bool historical = false)
        {
            var reading = new DeviceReading(device, ++seq, start.AddMinutes(minute), metrics, ids);
            var payload = SparkplugPayloads.DeviceReading(seq, reading, historical, birth: kind == "DBIRTH");
            await store.WriteAsync(SparkplugRows.From($"spBv1.0/identity/{kind}/gateway-01/{device}", payload.ToByteArray(),
                start.AddMinutes(minute)), CancellationToken.None);
        }
        async Task<List<(string, string)>> Rows(string sql)
        {
            await using var command = source.CreateCommand(sql);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<(string, string)>();
            while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1)));
            return rows;
        }
        Task<List<(string, string)>> Catalog(string device) => Rows(
            $"SELECT metric || '=' || name, registry_id FROM metric_catalog WHERE node = '{node}' AND device = '{device}' ORDER BY metric COLLATE \"C\"");
        Task<List<(string, string)>> Stored(string device) => Rows(
            $"SELECT metric, count(*)::text FROM readings WHERE node = '{node}' AND device = '{device}' GROUP BY 1 ORDER BY metric COLLATE \"C\"");

        // History from before IDs existed: the name is the key.
        await Send("DDATA", "tank", 0, new() { ["Level"] = new(Number: 1) });
        // The first birth with IDs adopts the existing metric (its key stays "Level") and keys the new one by its ID.
        await Send("DBIRTH", "tank", 1, new() { ["Level"] = new(Number: 2), ["Pump"] = new(Number: 1) },
            new() { ["Level"] = "id-level", ["Pump"] = "id-pump" });
        Check((await Catalog("tank")).SequenceEqual([("Level=Level", "id-level"), ("id-pump=Pump", "id-pump")]),
            "A birth must adopt an existing metric by name and key a new one by its registry ID.");
        // Rename: the same ID under a new name keeps the key.
        await Send("DBIRTH", "tank", 2, new() { ["TankLevel"] = new(Number: 3), ["Pump"] = new(Number: 1) },
            new() { ["TankLevel"] = "id-level", ["Pump"] = "id-pump" });
        await Send("DDATA", "tank", 3, new() { ["TankLevel"] = new(Number: 4), ["Pump"] = new(Number: 0) });
        // A reading taken before the rename and replayed after it still carries the old name.
        await Send("DDATA", "tank", 1, new() { ["Level"] = new(Number: 2) }, historical: true);
        Check((await Catalog("tank")).SequenceEqual([("Level=TankLevel", "id-level"), ("id-pump=Pump", "id-pump")]),
            "A rename must update the name of the same catalog row, with no new or removed metric.");
        Check((await Stored("tank")).SequenceEqual([("Level", "4"), ("id-pump", "3")]),
            "Readings before and after the rename (and replays under the old name) must share one key, without duplicates.");

        // A rename in the registry shows before the gateway's rebirth, and data sent under the new name finds the key.
        await store.ApplyRegistryAsync([new RegistryMetric("tank", "Fill", "input", "float32", "%", 1, "id-level"),
            new RegistryMetric("tank", "Pump", "coil", "bool", "", 1, "id-pump"),
            new RegistryMetric("legacy", "Temp", "holding", "int16", "°C", 0.1, "id-temp")], ["tank", "legacy"], CancellationToken.None);
        await Send("DDATA", "tank", 4, new() { ["Fill"] = new(Number: 5) });
        Check((await Catalog("tank"))[0] == ("Level=Fill", "id-level") && (await Stored("tank"))[0] == ("Level", "5"),
            "A registry rename must rename the catalog row and route the new name to the same key.");

        // The registry gives an existing name-keyed metric its ID, so a rename right after keeps its history.
        await Send("DDATA", "legacy", 0, new() { ["Temp"] = new(Number: 20) });
        await store.ApplyRegistryAsync([new RegistryMetric("legacy", "Temp", "holding", "int16", "°C", 0.1, "id-temp")], ["legacy"],
            CancellationToken.None);
        await Send("DBIRTH", "legacy", 1, new() { ["Temperature"] = new(Number: 21) }, new() { ["Temperature"] = "id-temp" });
        Check((await Catalog("legacy")).SequenceEqual([("Temp=Temperature", "id-temp")]) && (await Stored("legacy")).SequenceEqual([("Temp", "2")]),
            "The registry must adopt existing metrics by name, so a later rename keeps their history.");

        // Edge nodes that send no IDs are keyed by name, as before.
        await Send("DBIRTH", "other", 0, new() { ["X"] = new(Number: 1) });
        Check((await Catalog("other")).SequenceEqual([("X=X", "")]), "A birth without IDs keys metrics by name.");
        var tank = (await store.ReadCatalogAsync(CancellationToken.None)).Single(device => device.Node == node && device.Device == "tank");
        Check(tank.Metrics.Single(metric => metric.Metric == "Level").Name == "Fill", "Dashboards get the key and the current name.");
        Console.WriteLine("PASS: Metric identity in history: adoption by birth and registry, renames keep one key, old names from replays, registry renames, edge nodes without IDs.");
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "IoT.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new Exception("Repository root not found.");
    }
}
