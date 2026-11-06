using System.Text.Json;
using IoT.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using EdgeGateway.Configuration.Storage;

namespace EdgeGateway.Configuration;

// One owner/connection for the registry. Every successful write and its version bump commit together.
public sealed class DeviceStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();

    public DeviceStore(string databasePath)
    {
        if (databasePath != ":memory:")
        {
            databasePath = Path.GetFullPath(databasePath);
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        }
        _connection = new(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false, ForeignKeys = true }.ToString());
        try
        {
            _connection.Open();
            using (var command = Command("PRAGMA table_info(Devices)"))
            using (var reader = command.ExecuteReader())
            {
                var columns = new List<string>();
                while (reader.Read()) columns.Add(reader.GetString(1));
                if (columns.Count > 0 && !columns.Contains("InterfaceId"))
                    throw new InvalidOperationException("Old Configuration DB schema. Stop the configuration service and remove only its configuration.db (and old gateway config-cache.json), then restart. Do not remove the gateway outbox or node state.");
            }
            using var database = new ConfigurationDbContext(new DbContextOptionsBuilder<ConfigurationDbContext>()
                .UseSqlite(_connection).Options);
            database.Database.Migrate();
            Execute("PRAGMA journal_mode = WAL;");
            AssignMissingMetricIds();
        }
        catch { _connection.Dispose(); throw; }
    }

    public DeviceRegistration[] All()
    {
        lock (_gate)
        {
            using var command = Command("SELECT Id, InterfaceId, Data FROM Devices ORDER BY Id");
            using var reader = command.ExecuteReader();
            var devices = new List<DeviceRegistration>();
            while (reader.Read()) devices.Add(ReadDevice(reader));
            return devices.ToArray();
        }
    }

    public DeviceRegistration? Find(string id)
    {
        lock (_gate)
        {
            using var command = Command("SELECT Id, InterfaceId, Data FROM Devices WHERE Id = $id", null, ("$id", id));
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadDevice(reader) : null;
        }
    }

    public ModbusInterface[] AllInterfaces()
    {
        lock (_gate)
        {
            using var command = Command("SELECT Id, Type, Settings FROM Interfaces ORDER BY Id");
            using var reader = command.ExecuteReader();
            var interfaces = new List<ModbusInterface>();
            while (reader.Read()) interfaces.Add(ReadInterface(reader));
            return interfaces.ToArray();
        }
    }

    public ModbusInterface? FindInterface(string id)
    {
        lock (_gate) return FindInterface(id, null);
    }

    // Resolve in one read transaction: cacheable polling definitions cannot mix old/new interface settings.
    public GatewayConfiguration Snapshot() => Snapshot(null)!;

    // With a gateway or broker ID the snapshot also carries a broker: the gateway's registry assignment if it has
    // one, else the broker it names itself. Null when that named broker does not exist.
    public GatewayConfiguration? Snapshot(string? brokerId, string? gatewayId = null)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction(deferred: true);
            var version = (long)Scalar("SELECT Value FROM Meta WHERE Key = 'Version'", transaction)!;
            if (gatewayId is not null && Scalar("SELECT BrokerId FROM GatewayBrokers WHERE GatewayId = $id", transaction,
                    ("$id", gatewayId)) is string assigned)
                brokerId = assigned;
            BrokerProfile? broker = null;
            if (brokerId is not null && (broker = FindBroker(brokerId, transaction)) is null) return null;
            using var command = Command("""
                SELECT d.Id, d.InterfaceId, d.Data, i.Type, i.Settings
                FROM Devices d JOIN Interfaces i ON i.Id = d.InterfaceId ORDER BY d.Id
                """, transaction);
            var devices = new List<DeviceDefinition>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var device = ReadDevice(reader);
                    if (!device.Enabled) continue;
                    var connection = new ModbusInterface(device.InterfaceId, reader.GetString(3),
                        JsonSerializer.Deserialize<JsonElement>(reader.GetString(4)));
                    ValidateConnection(device, connection);
                    var tcp = connection.TcpSettings();
                    devices.Add(new(device.Id, device.Enabled, connection.Type, tcp.Host, tcp.Port, device.UnitId,
                        device.PollIntervalSeconds, device.GpioPin, device.Description, device.Metrics, device.InterfaceId));
                }
            }
            transaction.Commit();
            return new(version, devices.ToArray(), broker);
        }
    }

    public bool Save(DeviceRegistration device, bool create, ModbusInterface? newInterface = null)
    {
        device.Validate();
        if (newInterface is not null)
        {
            if (!create) throw new ArgumentException("A new interface can only be created with a new device.");
            newInterface.Validate();
            if (device.InterfaceId != newInterface.Id) throw new ArgumentException("Device interfaceId must match the new interface ID.");
        }
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            var stored = Scalar("SELECT Data FROM Devices WHERE Id = $id", transaction, ("$id", device.Id)) as string;
            if ((stored is not null) == create) return false;
            device = device with { Metrics = WithIds(device.Metrics, stored is null ? [] : ReadData(stored).Metrics) };
            var connection = newInterface ?? FindInterface(device.InterfaceId, transaction)
                ?? throw new ArgumentException($"Interface {device.InterfaceId} does not exist.");
            ValidateConnection(device, connection);
            if (newInterface is not null)
            {
                if (FindInterface(newInterface.Id, transaction) is not null)
                    throw new ConfigurationConflictException($"Interface {newInterface.Id} already exists. Select the existing interface instead.");
                Execute("INSERT INTO Interfaces (Id, Type, Settings) VALUES ($id, $type, $settings)", transaction,
                    ("$id", newInterface.Id), ("$type", newInterface.Type), ("$settings", newInterface.Settings.GetRawText()));
            }
            var data = new DeviceData(device.Enabled, device.UnitId, device.PollIntervalSeconds, device.GpioPin, device.Description, device.Metrics);
            Execute("""
                INSERT INTO Devices (Id, InterfaceId, Data) VALUES ($id, $interface, $data)
                ON CONFLICT(Id) DO UPDATE SET InterfaceId = excluded.InterfaceId, Data = excluded.Data
                """, transaction, ("$id", device.Id), ("$interface", device.InterfaceId), ("$data", JsonSerializer.Serialize(data, EdgeTopics.Json)));
            BumpVersion(transaction);
            transaction.Commit();
            return true;
        }
    }

    public bool SaveInterface(ModbusInterface connection, bool create)
    {
        connection.Validate();
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            var exists = FindInterface(connection.Id, transaction) is not null;
            if (exists == create) return false;
            using (var command = Command("SELECT Id, InterfaceId, Data FROM Devices WHERE InterfaceId = $id", transaction, ("$id", connection.Id)))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    try { ValidateConnection(ReadDevice(reader), connection); }
                    catch (ArgumentException error) { throw new ConfigurationConflictException($"Interface update rejected: {error.Message}"); }
                }
            }
            Execute("""
                INSERT INTO Interfaces (Id, Type, Settings) VALUES ($id, $type, $settings)
                ON CONFLICT(Id) DO UPDATE SET Type = excluded.Type, Settings = excluded.Settings
                """, transaction, ("$id", connection.Id), ("$type", connection.Type), ("$settings", connection.Settings.GetRawText()));
            BumpVersion(transaction);
            transaction.Commit();
            return true;
        }
    }

    public bool Delete(string id) => DeleteRecord("Devices", id);
    public bool DeleteInterface(string id) => DeleteRecord("Interfaces", id);
    public bool DeleteBroker(string id) => DeleteRecord("Brokers", id);

    public GatewayAssignment[] AllAssignments()
    {
        lock (_gate)
        {
            using var command = Command("SELECT GatewayId, BrokerId FROM GatewayBrokers ORDER BY GatewayId");
            using var reader = command.ExecuteReader();
            var assignments = new List<GatewayAssignment>();
            while (reader.Read()) assignments.Add(new(reader.GetString(0), reader.GetString(1)));
            return assignments.ToArray();
        }
    }

    // Points one gateway at a registry broker; the gateway switches on its next refresh (with trial and rollback).
    public void Assign(GatewayAssignment assignment)
    {
        assignment.Validate();
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            if (FindBroker(assignment.BrokerId, transaction) is null)
                throw new ArgumentException($"Broker {assignment.BrokerId} does not exist.");
            Execute("""
                INSERT INTO GatewayBrokers (GatewayId, BrokerId) VALUES ($gateway, $broker)
                ON CONFLICT(GatewayId) DO UPDATE SET BrokerId = excluded.BrokerId
                """, transaction, ("$gateway", assignment.GatewayId), ("$broker", assignment.BrokerId));
            BumpVersion(transaction);
            transaction.Commit();
        }
    }

    // The gateway goes back to the broker named by its own Mqtt:BrokerId.
    public bool Unassign(string gatewayId)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = Command("DELETE FROM GatewayBrokers WHERE GatewayId = $id", transaction, ("$id", gatewayId));
            if (command.ExecuteNonQuery() == 0) return false;
            BumpVersion(transaction);
            transaction.Commit();
            return true;
        }
    }

    public BrokerProfile[] AllBrokers()
    {
        lock (_gate)
        {
            using var command = Command($"SELECT {BrokerColumns} FROM Brokers ORDER BY Id");
            using var reader = command.ExecuteReader();
            var brokers = new List<BrokerProfile>();
            while (reader.Read()) brokers.Add(ReadBroker(reader));
            return brokers.ToArray();
        }
    }

    public BrokerProfile? FindBroker(string id)
    {
        lock (_gate) return FindBroker(id, null);
    }

    public bool SaveBroker(BrokerProfile broker, bool create)
    {
        broker.Validate();
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            var exists = FindBroker(broker.Id, transaction) is not null;
            if (exists == create) return false;
            Execute($"""
                INSERT INTO Brokers ({BrokerColumns}) VALUES ($id, $host, $port, $tls, $location, $sha, $keepAlive, $connect, $retry, $maxRetry)
                ON CONFLICT(Id) DO UPDATE SET Host = excluded.Host, Port = excluded.Port, UseTls = excluded.UseTls,
                    CertificateLocation = excluded.CertificateLocation, CertificateSha256 = excluded.CertificateSha256,
                    KeepAliveSeconds = excluded.KeepAliveSeconds, ConnectTimeoutSeconds = excluded.ConnectTimeoutSeconds,
                    RetryDelaySeconds = excluded.RetryDelaySeconds, MaxRetryDelaySeconds = excluded.MaxRetryDelaySeconds
                """, transaction, ("$id", broker.Id), ("$host", broker.Host), ("$port", broker.Port), ("$tls", broker.UseTls),
                ("$location", broker.CertificateLocation), ("$sha", broker.CertificateSha256?.ToLowerInvariant()),
                ("$keepAlive", broker.KeepAliveSeconds), ("$connect", broker.ConnectTimeoutSeconds),
                ("$retry", broker.RetryDelaySeconds), ("$maxRetry", broker.MaxRetryDelaySeconds));
            BumpVersion(transaction);
            transaction.Commit();
            return true;
        }
    }

    private bool DeleteRecord(string table, string id)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            if (table == "Interfaces" && Scalar("SELECT 1 FROM Devices WHERE InterfaceId = $id LIMIT 1", transaction, ("$id", id)) is not null)
                throw new ConfigurationConflictException($"Interface {id} is used by registered devices. Reassign or remove those devices first.");
            if (table == "Brokers" && Scalar("SELECT group_concat(GatewayId, ', ') FROM GatewayBrokers WHERE BrokerId = $id", transaction,
                    ("$id", id)) is string users)
                throw new ConfigurationConflictException($"Broker {id} is assigned to gateway(s) {users}. Assign them another broker first.");
            // Gateways without an assignment name their broker locally, so the registry cannot see those. Never leave none.
            if (table == "Brokers" && FindBroker(id, transaction) is not null && (long)Scalar("SELECT COUNT(*) FROM Brokers", transaction)! == 1)
                throw new ConfigurationConflictException($"Broker {id} is the only broker. Add another before removing it.");
            using var command = Command($"DELETE FROM {table} WHERE Id = $id", transaction, ("$id", id));
            if (command.ExecuteNonQuery() == 0) return false;
            BumpVersion(transaction);
            transaction.Commit();
            return true;
        }
    }

    private static void ValidateConnection(DeviceRegistration device, ModbusInterface connection)
    {
        connection.Validate();
        if (connection.Type != ModbusInterface.Rtu) return;
        if (device.UnitId is < 1 or > 247) throw new ArgumentException($"Device {device.Id}: Modbus RTU Unit ID must be 1–247.");
        if (device.Enabled) throw new ArgumentException($"Device {device.Id}: Modbus RTU polling is not implemented yet. Save serial devices with enabled=false.");
    }

    private const string BrokerColumns = "Id, Host, Port, UseTls, CertificateLocation, CertificateSha256, " +
        "KeepAliveSeconds, ConnectTimeoutSeconds, RetryDelaySeconds, MaxRetryDelaySeconds";

    private BrokerProfile? FindBroker(string id, SqliteTransaction? transaction)
    {
        using var command = Command($"SELECT {BrokerColumns} FROM Brokers WHERE Id = $id", transaction, ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadBroker(reader) : null;
    }

    private static BrokerProfile ReadBroker(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetBoolean(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9));

    private ModbusInterface? FindInterface(string id, SqliteTransaction? transaction)
    {
        using var command = Command("SELECT Id, Type, Settings FROM Interfaces WHERE Id = $id", transaction, ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadInterface(reader) : null;
    }

    private sealed record DeviceData(bool Enabled, byte UnitId, int PollIntervalSeconds, int? GpioPin, string Description, MetricDefinition[] Metrics);

    private static DeviceData ReadData(string json) => JsonSerializer.Deserialize<DeviceData>(json, EdgeTopics.Json)!;

    // Every stored metric has a permanent ID. A metric sent with its ID keeps it, which is how a rename keeps its
    // history; one sent without an ID takes the stored ID of the same name, else a new one.
    private static MetricDefinition[] WithIds(MetricDefinition[] metrics, MetricDefinition[] stored)
    {
        var claimed = metrics.Where(metric => metric.Id is not null).Select(metric => metric.Id!).ToHashSet(StringComparer.Ordinal);
        return metrics.Select(metric => metric.Id is not null ? metric : metric with
        {
            Id = stored.FirstOrDefault(old => old.Name == metric.Name && old.Id is not null && claimed.Add(old.Id))?.Id
                ?? Guid.NewGuid().ToString("N")
        }).ToArray();
    }

    // Devices saved before metric IDs existed get them once, with one version bump so the gateway picks them up.
    private void AssignMissingMetricIds()
    {
        using var transaction = _connection.BeginTransaction();
        var updates = new List<(string Id, DeviceData Data)>();
        using (var command = Command("SELECT Id, Data FROM Devices", transaction))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var data = ReadData(reader.GetString(1));
                if (data.Metrics.All(metric => metric.Id is not null)) continue;
                updates.Add((reader.GetString(0), data with { Metrics = WithIds(data.Metrics, []) }));
            }
        }
        if (updates.Count == 0) return;
        foreach (var (id, data) in updates)
            Execute("UPDATE Devices SET Data = $data WHERE Id = $id", transaction,
                ("$id", id), ("$data", JsonSerializer.Serialize(data, EdgeTopics.Json)));
        BumpVersion(transaction);
        transaction.Commit();
    }

    private static DeviceRegistration ReadDevice(SqliteDataReader reader)
    {
        var data = ReadData(reader.GetString(2));
        return new(reader.GetString(0), reader.GetString(1), data.Enabled, data.UnitId, data.PollIntervalSeconds, data.GpioPin, data.Description, data.Metrics);
    }

    private static ModbusInterface ReadInterface(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), JsonSerializer.Deserialize<JsonElement>(reader.GetString(2)));

    private void BumpVersion(SqliteTransaction transaction) => Execute("UPDATE Meta SET Value = Value + 1 WHERE Key = 'Version'", transaction);

    private void Execute(string sql, SqliteTransaction? transaction = null, params (string Name, object? Value)[] values)
    {
        using var command = Command(sql, transaction, values);
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql, SqliteTransaction? transaction = null, params (string Name, object? Value)[] values)
    {
        using var command = Command(sql, transaction, values);
        return command.ExecuteScalar();
    }

    private SqliteCommand Command(string sql, SqliteTransaction? transaction = null, params (string Name, object? Value)[] values)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public void Dispose() => _connection.Dispose();
}

public sealed class ConfigurationConflictException(string message) : Exception(message);
