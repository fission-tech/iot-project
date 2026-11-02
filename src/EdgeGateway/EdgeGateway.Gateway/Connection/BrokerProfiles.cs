using System.Text.Json;
using EdgeGateway.Gateway.Configuration;
using IoT.Contracts;

namespace EdgeGateway.Gateway.Connection;

// Which broker settings the gateway may use: the registry's current ones (Desired), the last ones that actually
// connected and published a birth (LastGood, kept on disk), and the appsettings values as the first-boot Seed.
public sealed class BrokerProfiles
{
    private readonly object _gate = new();
    private readonly string _lastGoodPath;
    private BrokerProfile? _desired, _lastGood;

    public BrokerProfiles(MqttSettings settings)
    {
        Seed = new(settings.BrokerId, settings.Host, settings.Port, settings.UseTls,
            settings.UseTls ? settings.BrokerCertificatePath : null, null,
            settings.KeepAliveSeconds, settings.ConnectTimeoutSeconds, settings.RetryDelaySeconds, settings.MaxRetryDelaySeconds);
        _lastGoodPath = Path.Combine(settings.StateDirectory, "broker-last-good.json");
        _lastGood = LoadLastGood();
    }

    public BrokerProfile Seed { get; }
    public BrokerProfile? Desired { get { lock (_gate) return _desired; } }
    public BrokerProfile? LastGood { get { lock (_gate) return _lastGood; } }
    // What to fall back on when the registry's settings are missing or fail.
    public BrokerProfile Fallback { get { lock (_gate) return _lastGood ?? Seed; } }

    // Raised when the registry's broker settings change.
    public event Action? Changed;

    // Settings from the registry (or its cached copy). Null means the registry names no broker for this gateway.
    public void Offer(BrokerProfile? profile)
    {
        if (profile is not null)
        {
            try { profile.Validate(); }
            catch (ArgumentException error)
            {
                Console.WriteLine($"Ignored invalid broker settings {profile.Id}: {error.Message}");
                return;
            }
        }
        lock (_gate)
        {
            if (_desired == profile) return;
            _desired = profile;
        }
        Console.WriteLine(profile is null
            ? "No broker settings in the registry for this gateway. Using the last working settings."
            : $"Broker settings from the registry: {Describe(profile)}.");
        Changed?.Invoke();
    }

    // Called after a connection with these settings published its birth. Survives restarts.
    public void MarkGood(BrokerProfile profile)
    {
        lock (_gate)
        {
            if (_lastGood == profile) return;
            _lastGood = profile;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_lastGoodPath)!);
                File.WriteAllText(_lastGoodPath + ".tmp", JsonSerializer.Serialize(profile, EdgeTopics.Json));
                File.Move(_lastGoodPath + ".tmp", _lastGoodPath, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Only a restart fallback is lost; the working connection is unaffected.
                Console.WriteLine($"Last working broker settings not saved: {error.Message}");
            }
        }
    }

    public static string Describe(BrokerProfile profile) =>
        $"{profile.Id} {profile.Host}:{profile.Port}{(profile.UseTls ? " TLS" : "")}";

    private BrokerProfile? LoadLastGood()
    {
        try
        {
            if (!File.Exists(_lastGoodPath)) return null;
            var profile = JsonSerializer.Deserialize<BrokerProfile>(File.ReadAllText(_lastGoodPath), EdgeTopics.Json);
            profile?.Validate();
            return profile;
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException)
        {
            Console.WriteLine($"Ignored unreadable last working broker settings: {error.Message}");
            return null;
        }
    }
}
