using System.Buffers;
using System.Security.Cryptography.X509Certificates;
using EdgeGateway.Gateway.Configuration;
using EdgeGateway.Gateway.Sparkplug;
using Google.Protobuf;
using IoT.Contracts;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Exceptions;
using MQTTnet.Protocol;

namespace EdgeGateway.Gateway.Connection;

public sealed class MqttConnection : IDisposable
{
    private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();
    private readonly ConnectionStateMachine _state = new();
    private TaskCompletionSource _disconnected = NewDisconnectSignal();
    private readonly MqttSettings _settings;
    private readonly BirthDeathSequenceStore _sequenceStore;
    private readonly string _deathTopic;
    public ulong BirthDeathSequence { get; private set; }
    public bool IsConnected => _client.IsConnected;
    public ConnectionState State => _state.Current;
    // The broker settings of the latest connection attempt, and the expiry of the certificate it trusted.
    public BrokerProfile? Broker { get; private set; }
    public DateTimeOffset? CertificateExpires { get; private set; }
    public event Func<string, byte[], Task>? MessageReceived;
    public event Action<bool, DateTimeOffset>? ConnectivityChanged;
    private volatile bool _intentionalDisconnect;
    private X509Certificate2? _trustedCertificate;

    // Identity (client ID, bdSeq, death topic) is fixed; the broker can change between connections.
    public MqttConnection(MqttSettings settings, SparkplugSettings sparkplug)
    {
        _settings = settings;
        _sequenceStore = new BirthDeathSequenceStore(settings.ClientId, settings.StateDirectory);
        _deathTopic = SparkplugTopics.NodeDeath(sparkplug);
        _client.ConnectedAsync += _ =>
        {
            ConnectivityChanged?.Invoke(true, DateTimeOffset.UtcNow);
            return Task.CompletedTask;
        };
        _client.DisconnectedAsync += args =>
        {
            if (args.ClientWasConnected && !_intentionalDisconnect)
                ConnectivityChanged?.Invoke(false, DateTimeOffset.UtcNow);
            if (_state.TryMoveTo(ConnectionState.Disconnected))
            {
                Console.WriteLine("Broker disconnected.");
            }
            _disconnected.TrySetResult();
            return Task.CompletedTask;
        };
        _client.ApplicationMessageReceivedAsync += args =>
            MessageReceived?.Invoke(args.ApplicationMessage.Topic, args.ApplicationMessage.Payload.ToArray())
            ?? Task.CompletedTask;
    }

    public async Task ConnectAsync(BrokerProfile broker, CancellationToken cancellationToken)
    {
        if (!_state.TryMoveTo(ConnectionState.Connecting))
            throw new InvalidOperationException("Gateway cannot connect in its current state.");

        try
        {
            _intentionalDisconnect = false;
            _disconnected = NewDisconnectSignal();
            Broker = broker;
            BirthDeathSequence = _sequenceStore.ReserveNext();
            try
            {
                var options = await BuildOptionsAsync(broker, cancellationToken);
                await _client.ConnectAsync(options, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Includes first-start connection and certificate failures; local bdSeq/storage errors are outside this catch.
                ConnectivityChanged?.Invoke(false, DateTimeOffset.UtcNow);
                throw;
            }
            Console.WriteLine($"Session bdSeq: {BirthDeathSequence}");
            if (!_state.TryMoveTo(ConnectionState.PublishingBirth))
                throw new InvalidOperationException("Connection closed before the birth announcement.");
        }
        catch
        {
            _state.TryMoveTo(ConnectionState.Disconnected);
            throw;
        }
    }

    private async Task<MqttClientOptions> BuildOptionsAsync(BrokerProfile broker, CancellationToken cancellationToken)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(broker.Host, broker.Port)
            .WithClientId(_settings.ClientId)
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithCleanSession()
            .WithTimeout(TimeSpan.FromSeconds(broker.ConnectTimeoutSeconds))
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(broker.KeepAliveSeconds))
            .WithWillTopic(_deathTopic)
            .WithWillPayload(SparkplugPayloads.NodeDeath(BirthDeathSequence).ToByteArray())
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithWillRetain(false);
        _trustedCertificate?.Dispose();
        _trustedCertificate = null;
        CertificateExpires = null;
        if (broker.UseTls)
        {
            // Trust exactly the configured broker certificate while keeping TLS verification enabled.
            _trustedCertificate = await BrokerCertificates.LoadAsync(broker, _settings.StateDirectory, cancellationToken);
            CertificateExpires = new DateTimeOffset(_trustedCertificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
            builder.WithTlsOptions(new MqttClientTlsOptionsBuilder()
                .WithTrustChain(new X509Certificate2Collection(_trustedCertificate))
                .Build());
        }
        return builder.Build();
    }

    private static TaskCompletionSource NewDisconnectSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitForDisconnectAsync(CancellationToken cancellationToken) =>
        _disconnected.Task.WaitAsync(cancellationToken);

    public async Task PrepareRetryAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_client.IsConnected)
                await PublishDeathAndDisconnectAsync(cancellationToken);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            // The connection dropped meanwhile; the broker publishes the Will instead.
            Console.WriteLine($"NDEATH not sent ({error.GetType().Name}).");
        }
        _state.TryMoveTo(ConnectionState.Disconnected);
        if (!_state.TryMoveTo(ConnectionState.RetryWaiting))
            throw new InvalidOperationException("Gateway cannot retry in its current state.");
    }


    public async Task SubscribeAsync(IEnumerable<string> topics, CancellationToken cancellationToken)
    {
        var builder = new MqttClientFactory().CreateSubscribeOptionsBuilder();
        foreach (var topic in topics) builder.WithTopicFilter(topic, MqttQualityOfServiceLevel.AtLeastOnce);
        await _client.SubscribeAsync(builder.Build(), cancellationToken);
    }

    public bool MarkOnline() => _state.TryMoveTo(ConnectionState.Online);

    public async Task PublishAsync(string topic, byte[] payload, CancellationToken cancellationToken,
        MqttQualityOfServiceLevel qualityOfService = MqttQualityOfServiceLevel.AtMostOnce)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(qualityOfService)
            .WithRetainFlag(false)
            .Build();

        await _client.PublishAsync(message, cancellationToken);
    }
    private async Task PublishDeathAndDisconnectAsync(CancellationToken cancellationToken)
    {
        _intentionalDisconnect = true;
        await PublishAsync(_deathTopic, SparkplugPayloads.NodeDeath(BirthDeathSequence).ToByteArray(),
            cancellationToken, MqttQualityOfServiceLevel.AtLeastOnce);
        Console.WriteLine($"Published NDEATH: {_deathTopic} (bdSeq {BirthDeathSequence})");
        var handled = _disconnected.Task;
        await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), cancellationToken);
        // The client reports the disconnect asynchronously. Wait for that handler, so it cannot run during the next
        // connection (an immediate reconnect to switched broker settings) and abort it.
        try { await handled.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
        catch (TimeoutException) { Console.WriteLine("Disconnect was not confirmed within 5s."); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        _state.TryMoveTo(ConnectionState.Stopping);
        try
        {
            if (_client.IsConnected)
                await PublishDeathAndDisconnectAsync(cancellationToken);
        }
        catch (Exception error) when (error is MqttCommunicationException or IOException)
        {
            // A lost transport cannot send NDEATH; disposal closes it and the broker handles the Will.
            Console.WriteLine("Connection closed during shutdown.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("Disconnect timed out.");
        }
        finally
        {
            _state.TryMoveTo(ConnectionState.Stopped);
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _trustedCertificate?.Dispose();
    }
}
