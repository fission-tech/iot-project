using System.Reflection;
using Npgsql;
using NpgsqlTypes;

namespace EdgeGateway.Historian;

// Writes Sparkplug rows to TimescaleDB. Reading inserts skip rows that already exist,
// so QoS 1 redelivery, outbox replay and the last reading repeated in a DBIRTH never duplicate data.
public sealed class HistoryStore(string connectionString) : IAsyncDisposable
{
    private readonly NpgsqlDataSource _source = NpgsqlDataSource.Create(connectionString);

    // Applies the numbered scripts in Schema/ that have not run yet, then the retention and compression settings.
    public async Task MigrateAsync(HistorySettings settings, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await Execute(connection, """
            CREATE TABLE IF NOT EXISTS schema_version (
                version int PRIMARY KEY, name text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now())
            """, cancellationToken);
        var applied = new HashSet<int>();
        await using (var command = new NpgsqlCommand("SELECT version FROM schema_version", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) applied.Add(reader.GetInt32(0));

        foreach (var (version, name, script) in Scripts().Where(script => !applied.Contains(script.Version)))
        {
            // Statements are idempotent, so a script interrupted halfway is simply run again.
            foreach (var statement in Statements(script)) await Execute(connection, statement, cancellationToken);
            await using var record = new NpgsqlCommand("INSERT INTO schema_version (version, name) VALUES ($1, $2)", connection)
            {
                Parameters = { new() { Value = version }, new() { Value = name } }
            };
            await record.ExecuteNonQueryAsync(cancellationToken);
        }

        // Settings can change between starts, so the policies are replaced rather than added once.
        string Days(int days) => $"INTERVAL '{days} days'";
        foreach (var statement in (string[])[
            "SELECT remove_compression_policy('readings', if_exists => TRUE)",
            $"SELECT add_compression_policy('readings', {Days(settings.CompressAfterDays)})",
            "SELECT remove_retention_policy('readings', if_exists => TRUE)",
            $"SELECT add_retention_policy('readings', {Days(settings.RawRetentionDays)})",
            "SELECT remove_retention_policy('readings_1m', if_exists => TRUE)",
            $"SELECT add_retention_policy('readings_1m', {Days(settings.RollupRetentionDays)})",
            // Grafana's read-only login, created by history/init when the database volume was set up.
            """
            DO $$ BEGIN
                IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'grafana_reader') THEN
                    GRANT SELECT ON readings, device_events, readings_1m TO grafana_reader;
                END IF;
            END $$
            """])
            await Execute(connection, statement, cancellationToken);
    }

    public async Task WriteAsync(SparkplugRows rows, CancellationToken cancellationToken)
    {
        if (rows.Readings.Length == 0 && rows.Event is null) return;
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (rows.Readings.Length > 0)
        {
            var readings = rows.Readings;
            await using var command = new NpgsqlCommand("""
                INSERT INTO readings (time, node, device, metric, value, quality, historical, device_seq)
                SELECT * FROM unnest($1::timestamptz[], $2::text[], $3::text[], $4::text[], $5::float8[],
                                     $6::text[], $7::boolean[], $8::bigint[])
                ON CONFLICT DO NOTHING
                """, connection, transaction)
            {
                Parameters =
                {
                    new() { Value = readings.Select(row => row.Time.UtcDateTime).ToArray() },
                    new() { Value = readings.Select(row => row.Node).ToArray() },
                    new() { Value = readings.Select(row => row.Device).ToArray() },
                    new() { Value = readings.Select(row => row.Metric).ToArray() },
                    new() { Value = readings.Select(row => row.Value).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Double },
                    new() { Value = readings.Select(row => row.Quality).ToArray() },
                    new() { Value = readings.Select(row => row.Historical).ToArray() },
                    new() { Value = readings.Select(row => row.DeviceSeq).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint }
                }
            };
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        if (rows.Event is { } lifecycle)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO device_events (time, node, device, kind) VALUES ($1, $2, $3, $4) ON CONFLICT DO NOTHING
                """, connection, transaction)
            {
                Parameters =
                {
                    new() { Value = lifecycle.Time.UtcDateTime }, new() { Value = lifecycle.Node },
                    new() { Value = lifecycle.Device }, new() { Value = lifecycle.Kind }
                }
            };
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task Execute(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IEnumerable<(int Version, string Name, string Script)> Scripts()
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql")).Order())
        {
            // EdgeGateway.Historian.Schema.001_initial.sql -> 001_initial
            var name = resource.Split('.')[^2];
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
            yield return (int.Parse(name.Split('_')[0]), name, reader.ReadToEnd());
        }
    }

    // Statements end with ';' at the end of a line; scripts keep DO blocks out so this split stays simple.
    public static IEnumerable<string> Statements(string script)
    {
        var current = new System.Text.StringBuilder();
        foreach (var line in script.Split('\n'))
        {
            var trimmed = line.TrimEnd();
            if (trimmed.TrimStart().StartsWith("--", StringComparison.Ordinal)) continue;
            current.AppendLine(trimmed);
            if (!trimmed.EndsWith(';')) continue;
            var statement = current.ToString().Trim().TrimEnd(';');
            if (statement.Length > 0) yield return statement;
            current.Clear();
        }
        if (current.ToString().Trim() is { Length: > 0 } rest) yield return rest;
    }

    public ValueTask DisposeAsync() => _source.DisposeAsync();
}
