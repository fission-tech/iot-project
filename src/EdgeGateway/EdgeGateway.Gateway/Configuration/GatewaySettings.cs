namespace EdgeGateway.Gateway.Configuration;

public sealed class GatewaySettings
{
    public MqttSettings Mqtt { get; set; } = new();
    public DaqSettings Daq { get; set; } = new();
    public SparkplugSettings Sparkplug { get; set; } = new();
    public GatewayOutboxSettings Gateway { get; set; } = new();
}

public sealed class MqttSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8883;
    public string ClientId { get; set; } = "gateway-01";
    public string StateDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "state", "gateway");
    public bool UseTls { get; set; } = true;
    public string BrokerCertificatePath { get; set; } = "certs/broker.crt";
    public int ConnectTimeoutSeconds { get; set; } = 10;
    public int KeepAliveSeconds { get; set; } = 60;
    public int RetryDelaySeconds { get; set; } = 2;
    public int MaxRetryDelaySeconds { get; set; } = 30;
}

public sealed class SparkplugSettings
{
    public string GroupId { get; set; } = "warehouse-01";
    public string EdgeNodeId { get; set; } = "gateway-01";
    // Primary Host Application to wait for; empty publishes without waiting.
    public string HostId { get; set; } = "";
}

public sealed class GatewayOutboxSettings
{
    public string DatabasePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "state", "gateway", "outbox.db");
    public int PublishBatchSize { get; set; } = 50;
    public int NodeDataIntervalSeconds { get; set; } = 30;
}
