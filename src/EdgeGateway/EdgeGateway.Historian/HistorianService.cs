using System.Buffers;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace EdgeGateway.Historian;

// Sparkplug subscriber that stores every reading. It keeps a persistent session with QoS 1 and acknowledges
// a message only after it is in the database: if the write fails it disconnects without acknowledging, and the
// broker delivers the message again on reconnect. Nothing is lost while the historian or the database is down.
public sealed class HistorianService(HistorianSettings settings, HistoryStore store, ILogger<HistorianService> log)
{

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await MigrateWithRetryAsync(cancellationToken);
        using var client = new MqttClientFactory().CreateMqttClient();
        using var certificate = LoadCertificate();
        var writeFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ApplicationMessageReceivedAsync += async args =>
        {
            args.AutoAcknowledge = false;
            var message = args.ApplicationMessage;
            SparkplugRows rows;
            try { rows = SparkplugRows.From(message.Topic, message.Payload.ToArray(), DateTimeOffset.UtcNow); }
            catch (Exception error) when (error is InvalidProtocolBufferException or ArgumentException)
            {
                // A malformed message will never succeed; acknowledge it so it does not block the session.
                log.LogWarning("Skipped unreadable message on {Topic}: {Reason}", message.Topic, error.Message);
                await args.AcknowledgeAsync(cancellationToken);
                return;
            }
            try
            {
                await store.WriteAsync(rows, cancellationToken);
                await args.AcknowledgeAsync(cancellationToken);
                if (rows.Event is { } lifecycle)
                    log.LogInformation("{Kind} {Node} {Device}", lifecycle.Kind, lifecycle.Node, lifecycle.Device);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                log.LogError("Could not store message on {Topic}: {Reason}. Reconnecting so the broker delivers it again.",
                    message.Topic, error.GetBaseException().Message.TrimEnd('.'));
                writeFailed.TrySetResult();
            }
        };

        var delay = TimeSpan.FromSeconds(settings.Mqtt.RetryDelaySeconds);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                writeFailed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task OnDisconnected(MqttClientDisconnectedEventArgs _) { disconnected.TrySetResult(); return Task.CompletedTask; }
                client.DisconnectedAsync += OnDisconnected;
                try
                {
                    await client.ConnectAsync(Options(certificate), cancellationToken);
                    await client.SubscribeAsync(new MqttClientFactory().CreateSubscribeOptionsBuilder()
                        .WithTopicFilter(settings.Historian.Topic, MqttQualityOfServiceLevel.AtLeastOnce).Build(), cancellationToken);
                    log.LogInformation("Connected to {Host}:{Port}, storing {Topic}.", settings.Mqtt.Host, settings.Mqtt.Port, settings.Historian.Topic);
                    delay = TimeSpan.FromSeconds(settings.Mqtt.RetryDelaySeconds);
                    var ended = await Task.WhenAny(disconnected.Task, writeFailed.Task, Task.Delay(Timeout.Infinite, cancellationToken));
                    if (ended == writeFailed.Task)
                        await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None);
                    else if (ended == disconnected.Task)
                        log.LogWarning("Disconnected from {Host}:{Port}.", settings.Mqtt.Host, settings.Mqtt.Port);
                }
                finally { client.DisconnectedAsync -= OnDisconnected; }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                log.LogWarning("Broker {Host}:{Port} unavailable: {Reason}", settings.Mqtt.Host, settings.Mqtt.Port,
                    error.GetBaseException().Message);
            }
            log.LogInformation("Retrying in {Delay}s.", delay.TotalSeconds);
            try { await Task.Delay(delay, cancellationToken); }
            catch (OperationCanceledException) { break; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, settings.Mqtt.MaxRetryDelaySeconds));
        }
        if (client.IsConnected)
            await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None);
    }

    // The database may still be starting (compose, server reboot): keep trying instead of exiting.
    private async Task MigrateWithRetryAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(settings.Mqtt.RetryDelaySeconds);
        while (true)
        {
            try
            {
                await store.MigrateAsync(settings.Historian, cancellationToken);
                log.LogInformation("History database ready (compress after {Compress} d, keep raw {Raw} d, rollups {Rollup} d).",
                    settings.Historian.CompressAfterDays, settings.Historian.RawRetentionDays, settings.Historian.RollupRetentionDays);
                return;
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is Npgsql.NpgsqlException or System.Net.Sockets.SocketException or TimeoutException)
            {
                log.LogWarning("History database unavailable: {Reason}. Retrying in {Delay}s.", error.GetBaseException().Message, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, settings.Mqtt.MaxRetryDelaySeconds));
            }
        }
    }

    private MqttClientOptions Options(X509Certificate2? certificate)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(settings.Mqtt.Host, settings.Mqtt.Port)
            .WithClientId(settings.Historian.ClientId)
            .WithProtocolVersion(MqttProtocolVersion.V311)
            // Persistent session: the broker queues QoS 1 messages for this client while it is offline.
            .WithCleanSession(false)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(settings.Mqtt.KeepAliveSeconds));
        if (certificate is not null)
        {
            var tls = new MqttClientTlsOptionsBuilder().WithTrustChain(new X509Certificate2Collection(certificate));
            if (settings.Mqtt.TlsServerName is { Length: > 0 } serverName) tls.WithTargetHost(serverName);
            builder.WithTlsOptions(tls.Build());
        }
        return builder.Build();
    }

    // A relative path is resolved next to the executable, as for the gateway.
    private X509Certificate2? LoadCertificate() => settings.Mqtt.UseTls
        ? X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, settings.Mqtt.BrokerCertificatePath)))
        : null;
}
