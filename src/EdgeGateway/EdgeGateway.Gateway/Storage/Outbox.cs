using System.Text.Json;
using IoT.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EdgeGateway.Gateway.Storage;

public sealed record OutboxRow(long Id, string Kind, string DeviceId, DeviceReading? Reading, DateTimeOffset CreatedAt);

// Polled readings are committed before publishing and deleted only after the broker's PUBACK.
public sealed partial class Outbox : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _added = new(0, 1);

    public Outbox(string databasePath)
    {
        if (databasePath != ":memory:")
        {
            databasePath = Path.GetFullPath(databasePath);
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        }
        _connection = new(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false, ForeignKeys = true }.ToString());
        _connection.Open();
        try
        {
            if (Scalar("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory'") is null
                && Scalar("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name IN ('Outbox', 'Devices', 'BrokerHistory')") is not null)
                throw new InvalidOperationException($"Gateway DB uses the pre-EF schema: {databasePath}. Stop the Gateway and use a fresh Gateway DB. Configuration DB is separate and must be kept.");
            using var database = new GatewayDbContext(new DbContextOptionsBuilder<GatewayDbContext>()
                .UseSqlite(_connection).Options);
            database.Database.Migrate();
            Execute("PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL;");
        }
        catch
        {
            _connection.Dispose();
            _added.Dispose();
            throw;
        }
    }

    // Returns false if this device's reading number is already stored.
    public bool AcceptReading(DeviceReading reading)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            if (Scalar("SELECT 1 FROM Outbox WHERE DeviceId = $device AND DeviceSeq = $seq", transaction,
                    ("$device", reading.DeviceId), ("$seq", (long)reading.DeviceSeq)) is not null)
                return false;
            var online = Scalar("SELECT IntakeOnline FROM Devices WHERE DeviceId = $device", transaction,
                ("$device", reading.DeviceId)) is 1L;
            // A changed metric list (from a configuration change) needs a new DBIRTH describing it.
            var signature = reading.MetricSignature();
            var sameMetrics = Scalar("SELECT MetricSignature FROM Devices WHERE DeviceId = $device", transaction,
                ("$device", reading.DeviceId)) as string == signature;
            Insert(online && sameMetrics ? "DDATA" : "DBIRTH", reading.DeviceId, reading, transaction);
            Execute("""
                INSERT INTO Devices (DeviceId, IntakeOnline, MetricSignature) VALUES ($device, 1, $signature)
                ON CONFLICT(DeviceId) DO UPDATE SET IntakeOnline = 1, MetricSignature = $signature
                """, transaction, ("$device", reading.DeviceId), ("$signature", signature));
            transaction.Commit();
        }
        Signal();
        return true;
    }

    // Includes devices saved by a previous process, even when no poller exists in this one yet.
    public string[] GetIntakeOnlineDeviceIds()
    {
        lock (_gate)
        {
            using var command = Command("SELECT DeviceId FROM Devices WHERE IntakeOnline = 1 ORDER BY DeviceId");
            using var reader = command.ExecuteReader();
            var ids = new List<string>();
            while (reader.Read()) ids.Add(reader.GetString(0));
            return ids.ToArray();
        }
    }

    // Returns false when the device was already offline.
    public bool AcceptDeviceOffline(string deviceId)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            if (Scalar("SELECT IntakeOnline FROM Devices WHERE DeviceId = $device", transaction,
                    ("$device", deviceId)) is not 1L)
                return false;
            Insert("DDEATH", deviceId, null, transaction);
            Execute("UPDATE Devices SET IntakeOnline = 0 WHERE DeviceId = $device", transaction, ("$device", deviceId));
            transaction.Commit();
        }
        Signal();
        return true;
    }

    public OutboxRow[] PeekUnsent(int limit)
    {
        lock (_gate)
        {
            using var command = Command("""
                SELECT Id, Kind, DeviceId, Reading, CreatedAt FROM Outbox
                ORDER BY Id LIMIT $limit
                """, null, ("$limit", limit));
            using var reader = command.ExecuteReader();
            var rows = new List<OutboxRow>();
            while (reader.Read())
                rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : JsonSerializer.Deserialize<DeviceReading>(reader.GetString(3), EdgeTopics.Json),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4))));
            return rows.ToArray();
        }
    }

    // Deletes the acknowledged row and records the last broker-acknowledged device state for births in the
    // next MQTT session, in one transaction.
    public void MarkSent(OutboxRow row)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            // The history link is read by the delete itself; a repeated ACK finds no row and changes nothing.
            long? historyId;
            using (var delete = Command("DELETE FROM Outbox WHERE Id = $id RETURNING BrokerHistoryId", transaction,
                       ("$id", row.Id)))
            using (var reader = delete.ExecuteReader())
            {
                if (!reader.Read()) return;
                historyId = reader.IsDBNull(0) ? null : reader.GetInt64(0);
            }
            if (historyId is { } id)
                Execute("UPDATE BrokerHistory SET BufferedMessagesSent = BufferedMessagesSent + 1 WHERE Id = $history",
                    transaction, ("$history", id));
            if (row.Kind == "DDEATH")
                Execute("UPDATE Devices SET PublishedOnline = 0 WHERE DeviceId = $device", transaction, ("$device", row.DeviceId));
            else
                Execute("UPDATE Devices SET PublishedOnline = 1, PublishedReading = $reading WHERE DeviceId = $device",
                    transaction, ("$device", row.DeviceId), ("$reading", JsonSerializer.Serialize(row.Reading, EdgeTopics.Json)));
            transaction.Commit();
        }
    }

    public DeviceReading[] GetPublishedOnlineDevices()
    {
        lock (_gate)
        {
            using var command = Command("SELECT PublishedReading FROM Devices WHERE PublishedOnline = 1 ORDER BY DeviceId");
            using var reader = command.ExecuteReader();
            var readings = new List<DeviceReading>();
            while (reader.Read())
                readings.Add(JsonSerializer.Deserialize<DeviceReading>(reader.GetString(0), EdgeTopics.Json)!);
            return readings.ToArray();
        }
    }

    public long CountUnsent()
    {
        lock (_gate) return (long)Scalar("SELECT COUNT(*) FROM Outbox")!;
    }

    public async Task WaitForRowsAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        await _added.WaitAsync(timeout, cancellationToken);

    private void Signal()
    {
        try { _added.Release(); }
        catch (SemaphoreFullException) { }
    }

    private void Insert(string kind, string deviceId, DeviceReading? reading, SqliteTransaction transaction) =>
        Execute("""
            INSERT INTO Outbox (Kind, DeviceId, DeviceSeq, Reading, CreatedAt, BrokerHistoryId)
            VALUES ($kind, $device, $seq, $reading, $now, $history)
            """, transaction, ("$kind", kind), ("$device", deviceId),
            ("$seq", reading is null ? null : (long)reading.DeviceSeq),
            ("$reading", reading is null ? null : JsonSerializer.Serialize(reading, EdgeTopics.Json)),
            ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("$history", _activeBrokerHistoryId));

    private int Execute(string sql, SqliteTransaction? transaction = null, params (string Name, object? Value)[] values)
    {
        using var command = Command(sql, transaction, values);
        return command.ExecuteNonQuery();
    }

    private object? Scalar(string sql, SqliteTransaction? transaction = null, params (string Name, object? Value)[] values)
    {
        using var command = Command(sql, transaction, values);
        return command.ExecuteScalar();
    }

    private SqliteCommand Command(string sql, SqliteTransaction? transaction = null,
        params (string Name, object? Value)[] values)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public void Dispose()
    {
        _connection.Dispose();
        _added.Dispose();
    }
}
