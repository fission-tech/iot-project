namespace EdgeGateway.Historian;

public sealed class HistorianSettings
{
    public HistorySettings Historian { get; init; } = new();
    public BrokerSettings Mqtt { get; init; } = new();
}

public sealed class HistorySettings
{
    // Npgsql connection string; on a server set it as the Historian__ConnectionString environment variable.
    public string ConnectionString { get; init; } = "";
    // Its own MQTT session: the broker queues messages for this client ID while the historian is down.
    public string ClientId { get; init; } = "historian";
    public string Topic { get; init; } = "spBv1.0/#";
    // Applied on every start, so changing them takes effect after a restart.
    public int CompressAfterDays { get; init; } = 7;
    public int RawRetentionDays { get; init; } = 90;
    public int RollupRetentionDays { get; init; } = 730;
    // Device registry (configuration service) describing each metric: register area, data type, unit.
    // Empty: dashboards only know boolean vs numeric, from the births.
    public string ConfigUrl { get; init; } = "";
    // Folder Grafana provisions dashboards from; the historian writes devices/<uid>.json there. Empty: no dashboards.
    public string DashboardDirectory { get; init; } = "";
    public int DashboardRefreshSeconds { get; init; } = 60;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
            throw new InvalidOperationException("Historian:ConnectionString is required (or the Historian__ConnectionString environment variable).");
        if (string.IsNullOrWhiteSpace(ClientId)) throw new InvalidOperationException("Historian:ClientId is required.");
        if (CompressAfterDays < 1 || RawRetentionDays <= CompressAfterDays || RollupRetentionDays < RawRetentionDays)
            throw new InvalidOperationException("Need 1 <= CompressAfterDays < RawRetentionDays <= RollupRetentionDays.");
        if (DashboardRefreshSeconds < 5) throw new InvalidOperationException("Historian:DashboardRefreshSeconds must be at least 5.");
    }
}

// The same Mqtt keys as the shared appsettings, so locally the historian uses the gateway's seed broker.
public sealed class BrokerSettings
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 8883;
    public bool UseTls { get; init; } = true;
    public string BrokerCertificatePath { get; init; } = "certs/broker.crt";
    // Name expected in the broker certificate when it differs from Host (a container reaching "mosquitto").
    public string? TlsServerName { get; init; }
    public int KeepAliveSeconds { get; init; } = 30;
    public int RetryDelaySeconds { get; init; } = 2;
    public int MaxRetryDelaySeconds { get; init; } = 30;
}
