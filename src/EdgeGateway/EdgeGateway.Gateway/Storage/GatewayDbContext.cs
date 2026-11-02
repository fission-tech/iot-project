using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EdgeGateway.Gateway.Storage;

// Schema ownership only: runtime Outbox writes keep their existing SQL and transactions.
public sealed class GatewayDbContext(DbContextOptions<GatewayDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<StoredDevice>(entity =>
        {
            entity.ToTable("Devices");
            entity.HasKey(x => x.DeviceId);
            entity.Property(x => x.IntakeOnline).HasDefaultValue(0L);
            entity.Property(x => x.PublishedOnline).HasDefaultValue(0L);
        });
        model.Entity<StoredOutboxMessage>(entity =>
        {
            entity.ToTable("Outbox");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Kind).IsRequired();
            entity.Property(x => x.DeviceId).IsRequired();
            entity.HasIndex(x => new { x.DeviceId, x.DeviceSeq }).IsUnique();
            entity.HasOne<BrokerHistoryRecord>().WithMany().HasForeignKey(x => x.BrokerHistoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<BrokerHistoryRecord>(entity =>
        {
            entity.ToTable("BrokerHistory");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DisconnectedAt).IsRequired();
            entity.Property(x => x.BufferedMessagesSent).HasDefaultValue(0L);
        });
    }
}

public sealed class StoredDevice
{
    public string DeviceId { get; set; } = "";
    public long IntakeOnline { get; set; }
    public long PublishedOnline { get; set; }
    public string? PublishedReading { get; set; }
    public string? MetricSignature { get; set; }
}

public sealed class StoredOutboxMessage
{
    public long Id { get; set; }
    public string Kind { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public long? DeviceSeq { get; set; }
    public string? Reading { get; set; }
    public long CreatedAt { get; set; }
    public long? BrokerHistoryId { get; set; }
}

public sealed class BrokerHistoryRecord
{
    public long Id { get; set; }
    public string DisconnectedAt { get; set; } = "";
    public string? ReconnectedAt { get; set; }
    public long? DowntimeSeconds { get; set; }
    public long? MessagesBuffered { get; set; }
    public long BufferedMessagesSent { get; set; }
}

// Do not execute Gateway Program.cs, read credentials, or connect to MQTT when running dotnet ef.
public sealed class GatewayDbContextFactory : IDesignTimeDbContextFactory<GatewayDbContext>
{
    public GatewayDbContext CreateDbContext(string[] args)
    {
        var path = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "state", "gateway", "outbox.db");
        var connection = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, Pooling = false }.ToString();
        return new(new DbContextOptionsBuilder<GatewayDbContext>().UseSqlite(connection).Options);
    }
}
