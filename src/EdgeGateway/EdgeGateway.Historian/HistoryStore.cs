using System.Reflection;
using Npgsql;
using NpgsqlTypes;

namespace EdgeGateway.Historian;

// Stores each metric sample once. A replay can mark it historical without replacing its original measurement.
// Readings are stored under each metric's permanent key, not its name, so renaming a metric keeps its history.
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
                    GRANT SELECT ON readings, device_events, readings_1m, device_catalog, metric_catalog, metric_names TO grafana_reader;
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
        var db = new Session(connection, transaction, cancellationToken);
        // Births first, so the readings they carry are stored under the keys they announce.
        var keys = rows is { Event: { Kind: "DBIRTH" } birth, Births: { Length: > 0 } births }
            ? await UpsertCatalogAsync(db, birth, births)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        if (rows.Readings.Length > 0)
        {
            var readings = rows.Readings;
            await ResolveKeysAsync(db, readings[0].Node, readings[0].Device, readings.Select(row => row.Metric), keys);
            await using var command = new NpgsqlCommand("""
                INSERT INTO readings (time, node, device, metric, value, quality, historical, device_seq)
                SELECT * FROM unnest($1::timestamptz[], $2::text[], $3::text[], $4::text[], $5::float8[],
                                     $6::text[], $7::boolean[], $8::bigint[])
                ON CONFLICT (node, device, metric, time) DO UPDATE
                SET historical = true
                WHERE EXCLUDED.historical AND NOT readings.historical
                """, connection, transaction)
            {
                Parameters =
                {
                    new() { Value = readings.Select(row => row.Time.UtcDateTime).ToArray() },
                    new() { Value = readings.Select(row => row.Node).ToArray() },
                    new() { Value = readings.Select(row => row.Device).ToArray() },
                    new() { Value = readings.Select(row => keys[row.Metric]).ToArray() },
                    new() { Value = readings.Select(row => row.Value).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Double },
                    new() { Value = readings.Select(row => row.Quality).ToArray() },
                    new() { Value = readings.Select(row => row.Historical).ToArray() },
                    new() { Value = readings.Select(row => row.DeviceSeq).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint }
                }
            };
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        if (rows.Event is { } lifecycle)
            await db.Execute("INSERT INTO device_events (time, node, device, kind) VALUES ($1, $2, $3, $4) ON CONFLICT DO NOTHING",
                lifecycle.Time.UtcDateTime, lifecycle.Node, lifecycle.Device, lifecycle.Kind);
        await transaction.CommitAsync(cancellationToken);
    }

    // A birth lists every metric the device publishes, each resolved to its permanent key:
    // 1. the metric with this registry ID (a different name there is a rename, so the name is updated);
    // 2. else a metric known by this name that has no registry ID yet (stored before IDs existed), which adopts it;
    // 3. else a new metric, keyed by its registry ID, or by its name when the edge node sends no IDs.
    // A null value keeps the known kind.
    private static async Task<Dictionary<string, string>> UpsertCatalogAsync(Session db, DeviceEventRow birth, BirthMetric[] births)
    {
        var (node, device, time) = (birth.Node, birth.Device, birth.Time.UtcDateTime);
        await db.Execute("""
            INSERT INTO device_catalog (node, device, uid, first_seen, last_seen) VALUES ($1, $2, $3, $4, $4)
            ON CONFLICT (node, device) DO UPDATE SET last_seen = EXCLUDED.last_seen, uid = EXCLUDED.uid
            """, node, device, Dashboards.DeviceCatalog.DashboardUid(node, device), time);
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var metric in births)
        {
            var key = metric.RegistryId is { } id
                ? await db.Scalar("SELECT metric FROM metric_catalog WHERE node = $1 AND device = $2 AND registry_id = $3",
                      node, device, id) as string
                  ?? await db.Scalar("""
                      UPDATE metric_catalog c SET registry_id = $4
                      FROM metric_names n
                      WHERE n.node = $1 AND n.device = $2 AND n.name = $3
                        AND c.node = n.node AND c.device = n.device AND c.metric = n.metric AND c.registry_id IS NULL
                      RETURNING c.metric
                      """, node, device, metric.Metric, id) as string
                : await db.Scalar("SELECT metric FROM metric_names WHERE node = $1 AND device = $2 AND name = $3",
                      node, device, metric.Metric) as string;
            key ??= metric.RegistryId ?? metric.Metric;
            await db.Execute("""
                INSERT INTO metric_catalog (node, device, metric, name, registry_id, kind, first_seen, last_seen)
                VALUES ($1, $2, $3, $4, $5::text, coalesce($6::text, 'numeric'), $7, $7)
                ON CONFLICT (node, device, metric) DO UPDATE
                SET name = EXCLUDED.name, last_seen = EXCLUDED.last_seen, kind = coalesce($6::text, metric_catalog.kind)
                """, node, device, key, metric.Metric, metric.RegistryId, metric.Kind, time);
            keys[metric.Metric] = key;
        }
        await RememberNamesAsync(db, node, device, keys);
        return keys;
    }

    // DDATA carries names only. Each name maps to the key it last belonged to, including names from before a rename,
    // which the gateway still uses for readings it replays. A name never announced in a birth becomes its own key.
    private static async Task ResolveKeysAsync(Session db, string node, string device, IEnumerable<string> names,
        Dictionary<string, string> keys)
    {
        var missing = names.Where(name => !keys.ContainsKey(name)).Distinct(StringComparer.Ordinal).ToArray();
        if (missing.Length == 0) return;
        foreach (var (name, key) in await db.Pairs(
                     "SELECT name, metric FROM metric_names WHERE node = $1 AND device = $2 AND name = ANY($3::text[])", node, device, missing))
            keys[name] = key;
        var unknown = missing.Where(name => !keys.ContainsKey(name)).ToDictionary(name => name, name => name, StringComparer.Ordinal);
        if (unknown.Count == 0) return;
        await db.Execute("""
            INSERT INTO metric_catalog (node, device, metric, name, kind, first_seen, last_seen)
            SELECT $1, $2, name, name, 'numeric', now(), now() FROM unnest($3::text[]) AS name
            ON CONFLICT DO NOTHING
            """, node, device, unknown.Keys.ToArray());
        await RememberNamesAsync(db, node, device, unknown);
        foreach (var (name, key) in unknown) keys[name] = key;
    }

    private static Task RememberNamesAsync(Session db, string node, string device, Dictionary<string, string> keys) => db.Execute("""
        INSERT INTO metric_names (node, device, name, metric) SELECT $1, $2, name, metric FROM unnest($3::text[], $4::text[]) AS m(name, metric)
        ON CONFLICT (node, device, name) DO UPDATE SET metric = EXCLUDED.metric WHERE metric_names.metric <> EXCLUDED.metric
        """, node, device, keys.Keys.ToArray(), keys.Values.ToArray());

    // Copies the registry's description onto the catalog, matched by permanent ID. Metrics stored before IDs existed
    // adopt the ID of the registry metric with their name first. A registry rename updates the name (no rebirth
    // needed). A device or metric the registry no longer has is kept (its history stays viewable) and marked removed.
    public async Task ApplyRegistryAsync(Dashboards.RegistryMetric[] registry, string[] devices, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var db = new Session(connection, transaction, cancellationToken);
        var deviceIds = registry.Select(metric => metric.Device).ToArray();
        var names = registry.Select(metric => metric.Metric).ToArray();
        var ids = registry.Select(metric => metric.Id).ToArray();
        await db.Execute("""
            UPDATE metric_catalog c SET registry_id = r.id
            FROM unnest($1::text[], $2::text[], $3::text[]) AS r(device, name, id), metric_names n
            WHERE r.id IS NOT NULL AND c.registry_id IS NULL AND c.device = r.device
              AND n.node = c.node AND n.device = c.device AND n.name = r.name AND n.metric = c.metric
              AND NOT EXISTS (SELECT FROM metric_catalog o WHERE o.node = c.node AND o.device = c.device AND o.registry_id = r.id)
            """, deviceIds, names, ids);
        // A catalog metric matches a registry metric by ID; one the registry gives no ID matches by name.
        const string Matches = "c.device = r.device AND (c.registry_id = r.id OR (r.id IS NULL AND c.registry_id IS NULL AND c.name = r.name))";
        await db.Execute($"""
            UPDATE metric_catalog c SET name = r.name, register = r.register, data_type = r.data_type, unit = r.unit, scale = r.scale,
                in_registry = true
            FROM unnest($1::text[], $2::text[], $3::text[], $4::text[], $5::text[], $6::text[], $7::float8[])
                AS r(device, name, id, register, data_type, unit, scale)
            WHERE {Matches}
              AND (c.name, c.register, c.data_type, c.unit, c.scale, c.in_registry)
                  IS DISTINCT FROM (r.name, r.register, r.data_type, r.unit, r.scale, true)
            """, deviceIds, names, ids, registry.Select(metric => metric.Register).ToArray(),
            registry.Select(metric => metric.DataType).ToArray(), registry.Select(metric => metric.Unit).ToArray(),
            registry.Select(metric => metric.Scale).ToArray());
        await db.Execute($"""
            UPDATE metric_catalog c SET in_registry = false
            WHERE c.in_registry AND NOT EXISTS (
                SELECT FROM unnest($1::text[], $2::text[], $3::text[]) AS r(device, name, id) WHERE {Matches})
            """, deviceIds, names, ids);
        // Current names find their keys, so data sent under a name from a registry rename lands on the same history.
        await db.Execute("""
            INSERT INTO metric_names (node, device, name, metric) SELECT node, device, name, metric FROM metric_catalog WHERE in_registry
            ON CONFLICT (node, device, name) DO UPDATE SET metric = EXCLUDED.metric WHERE metric_names.metric <> EXCLUDED.metric
            """);
        await db.Execute("UPDATE device_catalog SET in_registry = (device = ANY($1::text[])) WHERE in_registry <> (device = ANY($1::text[]))",
            // Cast: a lone string[] would otherwise bind as the whole params array, one parameter per device.
            (object)devices);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<Dashboards.DeviceCatalog[]> ReadCatalogAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT d.node, d.device, d.in_registry, m.metric, m.name, m.kind, m.register, m.data_type, m.unit, m.scale, m.in_registry
            FROM device_catalog d JOIN metric_catalog m USING (node, device)
            ORDER BY d.node, d.device, m.name, m.metric
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<(string Node, string Device, bool InRegistry, Dashboards.CatalogMetric Metric)>();
        string? Text(int column) => reader.IsDBNull(column) ? null : reader.GetString(column);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), new(reader.GetString(3), reader.GetString(4),
                reader.GetString(5), Text(6), Text(7), Text(8), reader.IsDBNull(9) ? null : reader.GetDouble(9), reader.GetBoolean(10))));
        return rows.GroupBy(row => (row.Node, row.Device, row.InRegistry))
            .Select(group => new Dashboards.DeviceCatalog(group.Key.Node, group.Key.Device, group.Key.InRegistry,
                group.Select(row => row.Metric).ToArray()))
            .ToArray();
    }

    // Commands within one transaction, with positional parameters ($1, $2, ...). Null binds as SQL NULL, so a
    // nullable parameter needs a cast in the SQL. Pass a single array as (object)array (see ApplyRegistryAsync).
    private sealed class Session(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        public async Task Execute(string sql, params object?[] values)
        {
            await using var command = Command(sql, values);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task<object?> Scalar(string sql, params object?[] values)
        {
            await using var command = Command(sql, values);
            return await command.ExecuteScalarAsync(cancellationToken);
        }

        public async Task<List<(string, string)>> Pairs(string sql, params object?[] values)
        {
            await using var command = Command(sql, values);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var pairs = new List<(string, string)>();
            while (await reader.ReadAsync(cancellationToken)) pairs.Add((reader.GetString(0), reader.GetString(1)));
            return pairs;
        }

        private NpgsqlCommand Command(string sql, object?[] values)
        {
            var command = new NpgsqlCommand(sql, connection, transaction);
            foreach (var value in values) command.Parameters.Add(new() { Value = value ?? DBNull.Value });
            return command;
        }
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
