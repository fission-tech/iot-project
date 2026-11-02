using System.Buffers;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using IoT.Contracts;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using Org.Eclipse.Tahu.Protobuf;

namespace Demo.Control;

public sealed record WireMessage(long Id, DateTimeOffset At, string Topic, string Type, string Summary,
    long? Seq, bool Historical, bool Retained);

// The console's Sparkplug consumer on Mosquitto, alongside MQTTX. It uses a persistent session with QoS 1,
// so the broker keeps its messages while it is disconnected, and feeds both the message feed and the view.
public sealed class WireMonitor(IConfiguration configuration, SparkplugView view, ComponentManager components,
    ILogger<WireMonitor> logger) : BackgroundService
{
    private readonly Dictionary<string, LinkedList<WireMessage>> _messages = new() { ["upstream"] = new() };
    private readonly Dictionary<string, bool> _connected = new() { ["upstream"] = false };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private long _nextId;
    private BrokerTarget? _gatewayBroker;

    private sealed record BrokerTarget(string Host, int Port, bool UseTls, string? CertificateLocation, string? CertificateSha256);

    public WireMessage[] Recent(string broker, int count = 120)
    {
        lock (_messages) return _messages[broker].Take(count).ToArray();
    }

    public bool Connected(string broker)
    {
        lock (_messages) return _connected[broker];
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => WatchAsync("upstream", "spBv1.0/#", stoppingToken);

    // Watch the broker the gateway publishes to, so the view stays right through a registry switch or rollback.
    // Only a broker the gateway is online with counts: trial attempts that fail are not followed.
    // Before the gateway has reported one, use this console's appsettings.
    private BrokerTarget CurrentTarget()
    {
        if (components.ReadGatewayStatus() is { } status
            && status.TryGetProperty("upstream", out var upstream) && upstream.GetString() == "Online"
            && status.TryGetProperty("broker", out var broker)
            && broker.ValueKind == System.Text.Json.JsonValueKind.Object && broker.TryGetProperty("active", out var active)
            && active.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            string? Text(string name) => active.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null;
            _gatewayBroker = new(Text("host") ?? "127.0.0.1", active.GetProperty("port").GetInt32(), active.GetProperty("useTls").GetBoolean(),
                Text("certificateLocation"), Text("certificateSha256"));
        }
        if (_gatewayBroker is not null) return _gatewayBroker;
        var mqtt = configuration.GetSection("Mqtt");
        return new(mqtt["Host"] ?? "127.0.0.1", mqtt.GetValue("Port", 8883), mqtt.GetValue("UseTls", true),
            mqtt["BrokerCertificatePath"] ?? "certs/broker.crt", null);
    }

    // A file path is resolved next to this console (it ships the same certs folder); a URL is downloaded and pinned.
    private static async Task<X509Certificate2?> LoadCertificateAsync(BrokerTarget target, CancellationToken cancellationToken)
    {
        if (!target.UseTls || target.CertificateLocation is null) return null;
        var text = Uri.TryCreate(target.CertificateLocation, UriKind.Absolute, out var uri) && !uri.IsFile
            ? Encoding.ASCII.GetString(await Http.GetByteArrayAsync(uri, cancellationToken))
            : await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, target.CertificateLocation), cancellationToken);
        var certificate = X509Certificate2.CreateFromPem(text);
        var fingerprint = Convert.ToHexString(certificate.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256));
        if (target.CertificateSha256 is { } pinned && !fingerprint.Equals(pinned, StringComparison.OrdinalIgnoreCase))
        {
            certificate.Dispose();
            throw new InvalidOperationException($"Certificate {target.CertificateLocation} does not match its pinned fingerprint.");
        }
        return certificate;
    }

    private async Task WatchAsync(string broker, string filter, CancellationToken stoppingToken)
    {
        using var client = new MqttClientFactory().CreateMqttClient();
        X509Certificate2? certificate = null;
        BrokerTarget? connectedTo = null;
        client.ApplicationMessageReceivedAsync += args =>
        {
            var message = args.ApplicationMessage;
            Add(broker, message.Topic, message.Payload.ToArray(), message.Retain);
            return Task.CompletedTask;
        };
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var target = CurrentTarget();
                if (client.IsConnected && target != connectedTo)
                {
                    logger.LogInformation("Gateway moved to broker {Host}:{Port}; following it.", target.Host, target.Port);
                    await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), stoppingToken);
                }
                if (!client.IsConnected)
                {
                    certificate?.Dispose();
                    certificate = await LoadCertificateAsync(target, stoppingToken);
                    var builder = new MqttClientOptionsBuilder()
                        .WithTcpServer(target.Host, target.Port)
                        .WithClientId("demo-console-monitor")
                        .WithProtocolVersion(MqttProtocolVersion.V311)
                        .WithCleanSession(false)
                        .WithKeepAlivePeriod(TimeSpan.FromSeconds(10));
                    if (certificate is not null)
                        builder.WithTlsOptions(new MqttClientTlsOptionsBuilder()
                            .WithTrustChain(new X509Certificate2Collection(certificate)).Build());
                    await client.ConnectAsync(builder.Build(), stoppingToken);
                    connectedTo = target;
                    await client.SubscribeAsync(new MqttClientFactory().CreateSubscribeOptionsBuilder()
                        .WithTopicFilter(filter, MqttQualityOfServiceLevel.AtLeastOnce).Build(), stoppingToken);
                }
                lock (_messages) _connected[broker] = true;
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                lock (_messages) _connected[broker] = false;
                logger.LogDebug("Monitor for {Broker} not connected ({ErrorType}).", broker, error.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        certificate?.Dispose();
    }

    private void Add(string broker, string topic, byte[] payload, bool retained)
    {
        string type, summary;
        long? seq = null;
        var historical = false;
        try
        {
            if (topic.StartsWith("spBv1.0/STATE/", StringComparison.Ordinal))
            {
                type = "STATE";
                summary = Encoding.UTF8.GetString(payload);
            }
            else
            {
                type = topic.Split('/') is { Length: >= 3 } parts ? parts[2] : "?";
                var decoded = Payload.Parser.ParseFrom(payload);
                view.Apply(topic, decoded);
                if (decoded.HasSeq) seq = (long)decoded.Seq;
                historical = decoded.Metrics.Any(metric => metric.IsHistorical);
                summary = string.Join(" · ", decoded.Metrics.Select(metric => $"{metric.Name}={Value(metric)}"));
                if (summary == "") summary = "(no metrics)";
            }
        }
        catch (Google.Protobuf.InvalidProtocolBufferException)
        {
            type = "?";
            summary = $"{payload.Length} bytes (not decodable)";
        }
        var item = new WireMessage(Interlocked.Increment(ref _nextId), DateTimeOffset.UtcNow, topic, type, summary,
            seq, historical, retained);
        lock (_messages)
        {
            var list = _messages[broker];
            list.AddFirst(item);
            while (list.Count > 200) list.RemoveLast();
        }
    }

    private static string Value(Payload.Types.Metric metric) =>
        metric.IsNull || SparkplugDecoder.QualityOf(metric) != MetricQuality.Good
            ? $"null ({SparkplugDecoder.QualityOf(metric) switch { MetricQuality.Good => MetricQuality.Bad, var quality => quality }})"
        : metric.HasDoubleValue ? metric.DoubleValue.ToString(CultureInfo.InvariantCulture)
        : metric.HasBooleanValue ? metric.BooleanValue.ToString().ToLowerInvariant()
        : metric.HasLongValue ? metric.LongValue.ToString(CultureInfo.InvariantCulture)
        : metric.HasIntValue ? metric.IntValue.ToString(CultureInfo.InvariantCulture)
        : "?";
}
