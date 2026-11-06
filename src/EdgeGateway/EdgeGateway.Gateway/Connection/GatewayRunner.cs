using System.Diagnostics;
using EdgeGateway.Gateway.Configuration;
using EdgeGateway.Gateway.Sparkplug;
using EdgeGateway.Gateway.Storage;
using Google.Protobuf;
using MQTTnet.Protocol;
using MQTTnet.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdgeGateway.Gateway.Connection;

public sealed class GatewayRunner(MqttConnection connection, Outbox outbox, GatewaySettings settings, BrokerProfiles? brokers = null, ILogger<GatewayRunner>? logger = null)
{
    private readonly ILogger<GatewayRunner> _log = logger ?? NullLogger<GatewayRunner>.Instance;
    // Initializers run in order: the first one settles the shared instance the selector then uses.
    private readonly BrokerProfiles _brokers = brokers ??= new BrokerProfiles(settings.Mqtt);
    private readonly BrokerSelector _selector = new(brokers, TimeSpan.FromSeconds(settings.Mqtt.BrokerTrialSeconds));
    private readonly SemaphoreSlim _brokerChanged = new(0, 1);
    public BrokerProfiles Brokers => _brokers;
    // The broker the gateway last got online with, not a trial attempt that may still fail.
    private volatile BrokerConnection? _connected;
    private sealed record BrokerConnection(IoT.Contracts.BrokerProfile Broker, DateTimeOffset? CertificateExpires);
    public BrokerState BrokerState => _connected is { } current
        ? _selector.State(current.Broker, current.CertificateExpires)
        : _selector.State(null, null);
    private readonly PrimaryHostState _host = new();
    private int _rebirthRequested;
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private ulong UptimeSeconds => (ulong)Stopwatch.GetElapsedTime(_startedAt).TotalSeconds;
    private bool HostRequired => !string.IsNullOrWhiteSpace(settings.Sparkplug.HostId);
    public bool HostOnline => !HostRequired || _host.IsOnline;
    public string? LastPublished { get; private set; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        void RecordConnectivity(bool connected, DateTimeOffset observedAt)
        {
            if (cancellationToken.IsCancellationRequested) return;
            try
            {
                if (connected) outbox.RecordBrokerConnected(observedAt);
                else outbox.RecordBrokerDisconnected(observedAt);
            }
            catch (Microsoft.Data.Sqlite.SqliteException error)
            {
                // A diagnostics write must not turn a successful MQTT connection into a failed one.
                _log.LogWarning("Broker history could not be recorded: {Reason}", error.Message);
            }
        }
        connection.ConnectivityChanged += RecordConnectivity;
        _brokers.Changed += SignalBrokerChange;
        try { await RunConnectionLoopAsync(cancellationToken); }
        finally
        {
            _brokers.Changed -= SignalBrokerChange;
            connection.ConnectivityChanged -= RecordConnectivity;
            outbox.EndBrokerObservation();
        }
    }

    private async Task RunConnectionLoopAsync(CancellationToken cancellationToken)
    {
        var mqtt = settings.Mqtt;
        if (mqtt.RetryDelaySeconds <= 0 || mqtt.MaxRetryDelaySeconds < mqtt.RetryDelaySeconds)
            throw new ArgumentException("Retry delays must be positive and the maximum must be at least the initial delay.");
        if (settings.Gateway.PublishBatchSize <= 0 || settings.Gateway.NodeDataIntervalSeconds <= 0)
            throw new ArgumentException("Publish batch size and node data interval must be positive.");
        var commandTopic = SparkplugTopics.NodeCommand(settings.Sparkplug);
        var stateTopic = HostRequired ? SparkplugTopics.PrimaryHostState(settings.Sparkplug.HostId) : null;
        connection.MessageReceived += (topic, payload) =>
        {
            if (topic == stateTopic) _host.Apply(payload);
            else if (topic == commandTopic) RequestRebirth(payload);
            return Task.CompletedTask;
        };
        var broker = _selector.Next(DateTimeOffset.UtcNow);
        var delaySeconds = broker.RetryDelaySeconds;
        var attempt = 0;
        string? lastFailure = null;
        var reportedAt = DateTimeOffset.MinValue;
        while (!cancellationToken.IsCancellationRequested)
        {
            broker = _selector.Next(DateTimeOffset.UtcNow);
            var switching = false;
            var operation = "connect";
            try
            {
                _log.LogDebug("Connecting {ClientId} to broker {Broker}...", mqtt.ClientId, BrokerProfiles.Describe(broker));
                await connection.ConnectAsync(broker, cancellationToken);
                _log.LogInformation("MQTT {Connection}. Broker: {Host}:{Port}", attempt == 0 ? "connected" : "connection restored", broker.Host, broker.Port);
                _host.Reset();
                // Subscribe before the birth so the host's STATE and rebirth commands are not missed.
                operation = "subscribe";
                await connection.SubscribeAsync(stateTopic is null ? [commandTopic] : [commandTopic, stateTopic],
                    cancellationToken);
                if (HostRequired && !_host.CameOnline.IsCompleted)
                {
                    _log.LogInformation("Waiting for primary host {HostId}. Buffering readings meanwhile.", settings.Sparkplug.HostId);
                    var disconnected = connection.WaitForDisconnectAsync(cancellationToken);
                    if (await Task.WhenAny(_host.CameOnline, disconnected) != _host.CameOnline)
                        throw new InvalidOperationException("Disconnected while waiting for the primary host.");
                }
                Interlocked.Exchange(ref _rebirthRequested, 0);
                operation = "publish NBIRTH";
                await PublishNodeBirthAsync(cancellationToken);
                if (!connection.MarkOnline())
                    throw new InvalidOperationException("Connection closed during the birth announcement.");
                // Proven settings: the fallback for later failures and the restart default.
                _selector.Connected(broker);
                _connected = new(broker, connection.CertificateExpires);
                attempt = 0;
                delaySeconds = broker.RetryDelaySeconds;
                lastFailure = null;
                operation = "publish/replay";
                switching = await RunSessionAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                var reason = $"{BrokerProfiles.Describe(broker)} {operation}: {error.GetType().Name}: {error.GetBaseException().Message}";
                if (reason != lastFailure || DateTimeOffset.UtcNow - reportedAt >= TimeSpan.FromSeconds(30))
                {
                    if (error is Microsoft.Data.Sqlite.SqliteException)
                        _log.LogError("Gateway state operation failed ({Operation}): {Reason}", operation, error.Message);
                    else if (error is MqttCommunicationException or IOException or InvalidOperationException or TimeoutException)
                        _log.LogWarning("Connection failed ({ErrorType}): {Reason} Retrying in {Delay}s.",
                            error.GetType().Name, error.GetBaseException().Message, delaySeconds);
                    else
                        _log.LogError(error, "Gateway operation failed unexpectedly ({Operation}).", operation);
                    lastFailure = reason;
                    reportedAt = DateTimeOffset.UtcNow;
                }
                _selector.Failed(broker, error.Message);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Ends with NDEATH and an intentional disconnect, so a switch is not recorded as a broker outage.
            await connection.PrepareRetryAsync(cancellationToken);
            if (switching) continue; // New settings are tried at once; readings wait in the outbox meanwhile.
            attempt++;
            _log.LogDebug("Retry {Attempt} in {Delay}s. {Buffered} message(s) buffered.", attempt, delaySeconds, outbox.CountUnsent());
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
            delaySeconds = (int)Math.Min((long)delaySeconds * 2, broker.MaxRetryDelaySeconds);
        }
    }

    private void RequestRebirth(byte[] payload)
    {
        try
        {
            if (!SparkplugPayloads.IsRebirthCommand(payload)) return;
            Interlocked.Exchange(ref _rebirthRequested, 1);
            _log.LogInformation("Rebirth requested by host.");
        }
        catch (InvalidProtocolBufferException)
        {
            _log.LogWarning("Ignored invalid NCMD. Payload is not valid Sparkplug Protobuf.");
        }
    }

    private void SignalBrokerChange()
    {
        try { _brokerChanged.Release(); }
        catch (SemaphoreFullException) { }
    }

    // Completes when the registry asks for broker settings other than the ones this session uses.
    private async Task WaitForBrokerSwitchAsync(CancellationToken cancellationToken)
    {
        while (!_selector.ShouldSwitch(connection.Broker)) await _brokerChanged.WaitAsync(cancellationToken);
    }

    // Returns true when the session ended to switch to new broker settings.
    private async Task<bool> RunSessionAsync(CancellationToken cancellationToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var disconnected = connection.WaitForDisconnectAsync(session.Token);
        var hostOffline = _host.WentOffline;
        var switching = WaitForBrokerSwitchAsync(session.Token);
        var publishing = DrainAsync(session.Token);
        try
        {
            var finished = await Task.WhenAny(disconnected, publishing, hostOffline, switching);
            if (finished == publishing) await publishing;
            else if (finished == hostOffline)
                _log.LogInformation("Primary host went offline. Ending session and buffering until it returns.");
            else if (finished == switching)
            {
                _log.LogInformation("Broker settings changed in the registry. Ending this session to switch.");
                return true;
            }
            else await disconnected;
            return false;
        }
        finally
        {
            session.Cancel();
            try { await publishing; }
            catch (OperationCanceledException) when (session.IsCancellationRequested) { }
            catch (Exception) when (session.IsCancellationRequested) { }
            try { await disconnected; }
            catch (OperationCanceledException) when (session.IsCancellationRequested) { }
        }
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        var sequence = new SparkplugSequence();
        var sessionStart = DateTimeOffset.UtcNow;
        await PublishDeviceBirthsAsync(sequence, cancellationToken);
        var nodeDataInterval = TimeSpan.FromSeconds(settings.Gateway.NodeDataIntervalSeconds);
        var nextNodeData = DateTime.UtcNow + nodeDataInterval;
        var announcedBacklog = false;
        var nodeTopic = SparkplugTopics.NodeData(settings.Sparkplug);

        // One loop preserves the publish order and the shared sequence across node and device messages.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _rebirthRequested, 0) == 1)
            {
                await PublishNodeBirthAsync(cancellationToken);
                sequence = new SparkplugSequence();
                await PublishDeviceBirthsAsync(sequence, cancellationToken);
            }
            if (DateTime.UtcNow >= nextNodeData)
            {
                var current = sequence.Next();
                await connection.PublishAsync(nodeTopic, SparkplugPayloads.NodeData(current, UptimeSeconds).ToByteArray(),
                    cancellationToken, MqttQualityOfServiceLevel.AtLeastOnce);
                nextNodeData = DateTime.UtcNow + nodeDataInterval;
            }

            var rows = outbox.PeekUnsent(settings.Gateway.PublishBatchSize);
            if (rows.Length == 0)
            {
                await outbox.WaitForRowsAsync(TimeSpan.FromSeconds(1), cancellationToken);
                continue;
            }
            if (!announcedBacklog && rows[0].CreatedAt < sessionStart)
            {
                _log.LogInformation("Replaying {Buffered} buffered message(s).", outbox.CountUnsent());
                announcedBacklog = true;
            }
            foreach (var row in rows)
            {
                if (Volatile.Read(ref _rebirthRequested) == 1) break;
                await PublishRowAsync(row, sequence.Next(), row.CreatedAt < sessionStart, cancellationToken);
                outbox.MarkSent(row);
            }
        }
    }

    private async Task PublishRowAsync(OutboxRow row, ulong sequence, bool historical, CancellationToken cancellationToken)
    {
        var (topic, payload) = row.Kind switch
        {
            "DBIRTH" => (SparkplugTopics.DeviceBirth(settings.Sparkplug, row.DeviceId),
                SparkplugPayloads.DeviceReading(sequence, row.Reading!, birth: true)),
            "DDATA" => (SparkplugTopics.DeviceData(settings.Sparkplug, row.DeviceId),
                SparkplugPayloads.DeviceReading(sequence, row.Reading!, historical)),
            "DDEATH" => (SparkplugTopics.DeviceDeath(settings.Sparkplug, row.DeviceId),
                SparkplugPayloads.DeviceDeath(sequence)),
            _ => throw new InvalidOperationException($"Unknown outbox message {row.Kind}.")
        };
        await connection.PublishAsync(topic, payload.ToByteArray(), cancellationToken, MqttQualityOfServiceLevel.AtLeastOnce);
        var detail = row.Reading is { } reading
            ? $" #{reading.DeviceSeq}: " + string.Join(", ", reading.Metrics.Select(metric =>
                $"{metric.Key}={metric.Value.Number?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? metric.Value.Boolean?.ToString() ?? metric.Value.Quality}"))
            : "";
        LastPublished = $"{row.Kind} {row.DeviceId}{(row.Reading is { } sent ? $" #{sent.DeviceSeq}" : "")}";
        if (row.Kind == "DDATA")
            _log.LogDebug("{Kind} {DeviceId}{Detail} (seq {Sequence}{Historical})", row.Kind, row.DeviceId, detail,
                sequence, historical ? ", historical" : "");
        else
            _log.LogInformation("{Kind} {DeviceId} (seq {Sequence})", row.Kind, row.DeviceId, sequence);
    }

    private async Task PublishNodeBirthAsync(CancellationToken cancellationToken)
    {
        var topic = SparkplugTopics.NodeBirth(settings.Sparkplug);
        await connection.PublishAsync(topic,
            SparkplugPayloads.NodeBirth(connection.BirthDeathSequence, UptimeSeconds).ToByteArray(),
            cancellationToken, MqttQualityOfServiceLevel.AtLeastOnce);
        _log.LogInformation("Published NBIRTH: {Topic} (bdSeq {BirthDeathSequence})", topic, connection.BirthDeathSequence);
    }

    // Births describe the last broker-acknowledged state, before replaying the remaining backlog.
    private async Task PublishDeviceBirthsAsync(SparkplugSequence sequence, CancellationToken cancellationToken)
    {
        foreach (var reading in outbox.GetPublishedOnlineDevices())
        {
            var current = sequence.Next();
            var topic = SparkplugTopics.DeviceBirth(settings.Sparkplug, reading.DeviceId);
            await connection.PublishAsync(topic, SparkplugPayloads.DeviceReading(current, reading, birth: true).ToByteArray(),
                cancellationToken, MqttQualityOfServiceLevel.AtLeastOnce);
            _log.LogInformation("Published DBIRTH: {Topic} (seq {Sequence})", topic, current);
        }
    }
}
