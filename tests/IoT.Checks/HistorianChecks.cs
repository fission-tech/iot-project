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
        var death = SparkplugRows.From(Prefix + "DDEATH/gateway-01/fridge-01", SparkplugPayloads.DeviceDeath(9).ToByteArray(), Received);
        Check(death.Event is { Kind: "DDEATH" } && death.Readings.Length == 0, "DDEATH is an event only.");
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
        await store.MigrateAsync(settings, CancellationToken.None);

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
        Check(await Count("SELECT count(*) FROM device_events") == 2, "Expected one DBIRTH and one DDEATH event.");
        Check(await Count("SELECT count(*) FROM schema_version") == 1, "Migration recorded more than once.");
        Check(await Count("SELECT count(*) FROM timescaledb_information.jobs WHERE hypertable_name IN ('readings') OR proc_name = 'policy_refresh_continuous_aggregate'") >= 3,
            "Compression, retention and rollup policies are missing.");
    }
}
