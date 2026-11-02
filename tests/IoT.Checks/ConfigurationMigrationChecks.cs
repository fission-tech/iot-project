using EdgeGateway.Configuration;
using EdgeGateway.Configuration.Storage;
using IoT.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

internal static class ConfigurationMigrationChecks
{
    private static void Check(bool value, string reason)
    { if (!value) throw new Exception("Configuration migrations: " + reason); }

    private static object? Sql(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "configuration-migrations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var fresh = Path.Combine(directory, "fresh.db");
            using (var store = new DeviceStore(fresh))
            {
                Check(store.Snapshot() is { Version: 0, Devices.Length: 0 }, "fresh snapshot incorrect");
                Check((long)Sql(fresh, "SELECT COUNT(*) FROM __EFMigrationsHistory")! == 3, "baseline not recorded");
                Check((long)Sql(fresh, "SELECT COUNT(*) FROM pragma_index_list('Devices') WHERE name='IX_Devices_InterfaceId'")! == 1, "FK index missing");
                try { Sql(fresh, "INSERT INTO Interfaces VALUES('invalid','ModbusTcp','not-json');"); throw new Exception("Missing JSON constraint"); }
                catch (SqliteException) { }
                try { Sql(fresh, "INSERT INTO Devices VALUES('orphan','absent','{}');"); throw new Exception("Missing FK"); }
                catch (SqliteException) { }
            }
            using (var database = new ConfigurationDbContextFactory().CreateDbContext([fresh]))
            {
                Check(!database.Database.HasPendingModelChanges(), "snapshot/model mismatch");
                database.GetService<IMigrator>().Migrate("0");
                Check(Sql(fresh, "SELECT name FROM sqlite_master WHERE name='Devices'") is null, "baseline rollback failed");
                database.Database.Migrate();
            }
            using (var store = new DeviceStore(fresh))
                Check(store.Snapshot().Version == 0, "reapply did not initialize version");

            var existing = Path.Combine(directory, "existing.db");
            // Exact supported pre-EF shape, including JSON and a nonzero version.
            Sql(existing, """
                CREATE TABLE Interfaces(Id TEXT NOT NULL PRIMARY KEY, Type TEXT NOT NULL, Settings TEXT NOT NULL CHECK(json_valid(Settings)));
                CREATE TABLE Devices(Id TEXT NOT NULL PRIMARY KEY, InterfaceId TEXT NOT NULL REFERENCES Interfaces(Id) ON DELETE RESTRICT, Data TEXT NOT NULL CHECK(json_valid(Data)));
                CREATE INDEX IX_Devices_InterfaceId ON Devices(InterfaceId);
                CREATE TABLE Meta(Key TEXT PRIMARY KEY, Value INTEGER NOT NULL);
                INSERT INTO Meta VALUES('Version', 17);
                INSERT INTO Interfaces VALUES('tcp-01','ModbusTcp','{"host":"127.0.0.1","port":502}');
                INSERT INTO Devices VALUES('meter-01','tcp-01','{"enabled":true,"unitId":1,"pollIntervalSeconds":3,"gpioPin":null,"description":"Panel meter","metrics":[{"name":"Voltage","register":"holding","address":0,"scale":0.1,"unit":"V","dataType":"uint16","wordOrder":null}]}');
                """);
            var json = (string)Sql(existing, "SELECT Data FROM Devices WHERE Id='meter-01'")!;
            using (var store = new DeviceStore(existing))
            {
                Check(store.Snapshot() is { Version: 17, Devices.Length: 1 }, "adoption reset config/version");
                Check(store.Find("meter-01") is { InterfaceId: "tcp-01", UnitId: 1 }, "existing registration changed");
                Check((string)Sql(existing, "SELECT Data FROM Devices WHERE Id='meter-01'")! == json, "adoption rewrote JSON");
                Check((long)Sql(existing, "SELECT COUNT(*) FROM __EFMigrationsHistory")! == 3, "adoption not recorded");
                Check(store.Save(store.Find("meter-01")! with { Enabled = false }, false), "existing device update failed");
                Check(store.Snapshot().Version == 18, "version bump changed");
                try { store.DeleteInterface("tcp-01"); throw new Exception("Referenced interface removed"); }
                catch (ConfigurationConflictException) { }
            }
            using (var store = new DeviceStore(existing))
                Check(store.Snapshot() is { Version: 18, Devices.Length: 0 }, "repeat startup reset data/version");
            Check((string)Sql(existing, "PRAGMA integrity_check")! == "ok", "integrity failed");
            Check(Sql(existing, "PRAGMA foreign_key_check") is null, "FK integrity failed");

            var failing = Path.Combine(directory, "failure.db");
            Sql(failing, """
                CREATE TABLE Meta(Key TEXT PRIMARY KEY, Value INTEGER NOT NULL);
                CREATE TRIGGER fail_seed BEFORE INSERT ON Meta BEGIN SELECT RAISE(ABORT,'injected baseline failure'); END;
                """);
            try { using var store = new DeviceStore(failing); throw new Exception("Expected baseline failure"); }
            catch (SqliteException) { }
            Check(Sql(failing, "SELECT name FROM sqlite_master WHERE name='Interfaces'") is null, "failed baseline partially committed");
            Check((long)Sql(failing, "SELECT COUNT(*) FROM __EFMigrationsHistory")! == 0, "failed migration marked applied");
            Sql(failing, "DROP TRIGGER fail_seed;");
            using (var store = new DeviceStore(failing)) Check(store.Snapshot().Version == 0, "retry after migration failure failed");

            var old = Path.Combine(directory, "unsupported.db");
            Sql(old, "CREATE TABLE Devices(Id TEXT PRIMARY KEY, Data TEXT NOT NULL);");
            try { using var store = new DeviceStore(old); throw new Exception("Unsupported schema was adopted"); }
            catch (InvalidOperationException error) when (error.Message.Contains("Old Configuration DB schema")) { }
            Check(Sql(old, "SELECT name FROM sqlite_master WHERE name='__EFMigrationsHistory'") is null, "unsupported schema altered");
            Console.WriteLine("PASS: Configuration EF baseline: fresh DB, existing JSON/version preservation, repeat startup, schema/FK/index, model snapshot, rollback/reapply, transactional failure/retry and unsupported-old-schema guard.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
