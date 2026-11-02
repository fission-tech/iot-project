using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EdgeGateway.Configuration.Storage;

// Owns Configuration schema only. DeviceStore keeps its existing transactional SQL.
public sealed class ConfigurationDbContext(DbContextOptions<ConfigurationDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<StoredInterface>(entity =>
        {
            entity.ToTable("Interfaces", table => table.HasCheckConstraint("CK_Interfaces_Settings_Json", "json_valid(Settings)"));
            entity.HasKey(x => x.Id);
        });
        model.Entity<StoredConfigurationDevice>(entity =>
        {
            entity.ToTable("Devices", table => table.HasCheckConstraint("CK_Devices_Data_Json", "json_valid(Data)"));
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.InterfaceId);
            entity.HasOne<StoredInterface>().WithMany().HasForeignKey(x => x.InterfaceId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ConfigurationMetadata>(entity =>
        {
            entity.ToTable("Meta");
            entity.HasKey(x => x.Key);
            entity.HasData(new ConfigurationMetadata { Key = "Version", Value = 0 });
        });
        model.Entity<StoredBroker>(entity =>
        {
            entity.ToTable("Brokers", table => table.HasCheckConstraint("CK_Brokers_Port", "Port BETWEEN 1 AND 65535"));
            entity.HasKey(x => x.Id);
            // The broker every gateway used from appsettings before this table existed.
            entity.HasData(new StoredBroker
            {
                Id = "local", Host = "127.0.0.1", Port = 8883, UseTls = true, CertificateLocation = "certs/broker.crt",
                KeepAliveSeconds = 60, ConnectTimeoutSeconds = 10, RetryDelaySeconds = 2, MaxRetryDelaySeconds = 30
            });
        });
        model.Entity<StoredGatewayBroker>(entity =>
        {
            entity.ToTable("GatewayBrokers");
            entity.HasKey(x => x.GatewayId);
            entity.HasIndex(x => x.BrokerId);
            // A broker that gateways are assigned to cannot be deleted.
            entity.HasOne<StoredBroker>().WithMany().HasForeignKey(x => x.BrokerId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

public sealed class StoredGatewayBroker
{
    public string GatewayId { get; set; } = "";
    public string BrokerId { get; set; } = "";
}

public sealed class StoredBroker
{
    public string Id { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public bool UseTls { get; set; }
    public string? CertificateLocation { get; set; }
    public string? CertificateSha256 { get; set; }
    public int KeepAliveSeconds { get; set; }
    public int ConnectTimeoutSeconds { get; set; }
    public int RetryDelaySeconds { get; set; }
    public int MaxRetryDelaySeconds { get; set; }
}

public sealed class StoredInterface
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Settings { get; set; } = "";
}

public sealed class StoredConfigurationDevice
{
    public string Id { get; set; } = "";
    public string InterfaceId { get; set; } = "";
    public string Data { get; set; } = "";
}

public sealed class ConfigurationMetadata
{
    public string Key { get; set; } = "";
    public long Value { get; set; }
}

// Tooling uses an explicit DB path after -- and never starts the Configuration API.
public sealed class ConfigurationDbContextFactory : IDesignTimeDbContextFactory<ConfigurationDbContext>
{
    public ConfigurationDbContext CreateDbContext(string[] args)
    {
        var path = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "state", "configuration.db");
        var connection = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, Pooling = false }.ToString();
        return new(new DbContextOptionsBuilder<ConfigurationDbContext>().UseSqlite(connection).Options);
    }
}
