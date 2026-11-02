using System.Diagnostics;
using EdgeGateway.Gateway.Configuration;
using EdgeGateway.Gateway.Sparkplug;
using EdgeGateway.Gateway.Storage;
using Google.Protobuf;
using MQTTnet.Protocol;

namespace EdgeGateway.Gateway.Connection;

public sealed class GatewayRunner(MqttConnection connection, Outbox outbox, GatewaySettings settings, BrokerProfiles? brokers = null)
{
    // Initializers run in order: the first one settles the shared instance the selector then uses.
    private readonly BrokerProfiles _brokers = brokers ??= new BrokerProfiles(settings.Mqtt);
    private readonly BrokerSelector _selector = new(brokers, TimeSpan.FromSeconds(settings.Mqtt.BrokerTrialSeconds));
    private readonly SemaphoreSlim _brokerChanged = new(0, 1);
    public BrokerProfiles Brokers => _brokers;
    public BrokerState BrokerState => _selector.State(connection.Broker, connection.CertificateExpires);
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
                Console.Error.WriteLine($"Broker history could not be recorded: {error.Message}");
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
        while (!cancellationToken.IsCancellationRequested)
        {
            broker = _selector.Next(DateTimeOffset.UtcNow);
            var switching = false;
            try
            {
                Console.WriteLine($"Connecting {mqtt.ClientId} to broker {BrokerProfiles.Describe(broker)}...");
                await connection.ConnectAsync(broker, cancellationToken);
                Console.WriteLine("Connected.");
                _host.Reset();
                // Subscribe before the birth so the host's STATE and rebirth commands are not missed.
                await connection.SubscribeAsync(stateTopic is null ? [commandTopic] : [commandTopic, stateTopic],
                    cancellationToken);
                if (HostRequired && !_host.CameOnline.IsCompleted)
                {
                    Console.WriteLine($"Waiting for primary host {settings.Sparkplug.HostId}. Buffering readings meanwhile.");
                    var disconnected = connection.WaitForDisconnectAsync(cancellationToken);
                    if (await Task.WhenAny(_host.CameOnline, disconnected) != _host.CameOnline)
                        throw new InvalidOperationException("Disconnected while waiting for the primary host.");
                }
                Interlocked.Exchange(ref _rebirthRequested, 0);
                await PublishNodeBirthAsync(cancellationToken);
                if (!connection.MarkOnline())
                    throw new InvalidOperationException("Connection closed during the birth announcement.");
                // Proven settings: the fallback for later failures and the restart default.
                _selector.Connected(broker);
                attempt = 0;
                delaySeconds = broker.RetryDelaySeconds;
                switching = await RunSessionAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                Console.WriteLine($"Connection failed: {error.Message}");
                _selector.Failed(broker, error.Message);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Ends with NDEATH and an intentional disconnect, so a switch is not recorded as a broker outage.
            await connection.PrepareRetryAsync(cancellationToken);
            if (switching) continue; // New settings are tried at once; readings wait in the outbox meanwhile.
            attempt++;
            Console.WriteLine($"Retry {attempt} in {delaySeconds}s. {outbox.CountUnsent()} message(s) buffered.");
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
            Console.WriteLine("Rebirth requested by host.");
        }
        catch (InvalidProtocolBufferException)
        {
            Console.WriteLine("Ignored invalid NCMD.");
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
                Console.WriteLine("Primary host went offline. Ending session and buffering until it returns.");
            else if (finished == switching)
            {
                Console.WriteLine("Broker settings changed in the registry. Ending this session to switch.");
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
                Console.WriteLine($"Replaying {outbox.CountUnsent()} buffered message(s).");
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
                SparkplugPayloads.DeviceReading(sequence, row.Reading!)),
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
        Console.WriteLine($"{row.Kind} {row.DeviceId}{detail} (seq {sequence}{(historical ? ", historical" : "")})");
    }

    private async Task PublishNodeBirthAsync(CancellationToken cancellationToken)
    {
        var topic = SparkplugTopics.NodeBirth(settings.Sparkplug);
        await connection.PublishAsync(topic,
            SparkplugPayloads.NodeBirth(connection.BirthDeathSequence, UptimeSeconds).ToByteArray(),
            cancellationToken, MqttQualityOfServiceLevel.AtLeastOnce);
        Console.WriteLine($"Published NBIRTH: {topic} (bdSeq {connection.BirthDeathSequence})");
    }

    // Births describe the last broker-acknowledged state, before replaying the remaining backlog.
    private async Task PublishDeviceBirthsAsync(SparkplugSequence sequence, CancellationToken cancellationToken)
    {
        foreach (var reading in outbox.GetPublishedOnlineDevices())
        {
            var current = sequence.Next();
            var topic = SparkplugTopics.DeviceBirth(settings.Sparkplug, reading.DeviceId);
            await connection.PublishAsync(topic, SparkplugPayloads.DeviceReading(current, reading).ToByteArray(),
                cancellationToken, MqttQualityOfServiceLevel.AtLeastOnce);
            Console.WriteLine($"Published DBIRTH: {topic} (seq {current})");
        }
    }
}
