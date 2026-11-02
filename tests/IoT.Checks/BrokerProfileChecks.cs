using System.Text.Json;
using IoT.Contracts;

internal static class BrokerProfileChecks
{
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception("Broker profile: " + reason); }
    private static void Reject(Action action, string reason)
    {
        try { action(); } catch (ArgumentException) { return; }
        throw new Exception("Broker profile accepted invalid settings: " + reason);
    }

    private const string Fingerprint = "3f7a9c1e5b2d4f608a1c3e5f7b9d0e2a4c6e8f0a1b3d5f7092a4c6e8f0b2d4f6";
    private static BrokerProfile Broker() => new("local", "127.0.0.1", 8883, true, "certs/broker.crt", null, 60, 10, 2, 30);

    public static void Run()
    {
        // Today's appsettings values are valid, with and without a pinned fingerprint.
        Broker().Validate();
        (Broker() with { CertificateSha256 = Fingerprint.ToUpperInvariant() }).Validate();
        (Broker() with { CertificateLocation = @"C:\certs\broker.crt" }).Validate();
        (Broker() with { CertificateLocation = "/etc/gateway/broker.crt" }).Validate();
        (Broker() with { CertificateLocation = "https://certs.example.com/broker.crt", CertificateSha256 = Fingerprint }).Validate();
        (Broker() with { UseTls = false, CertificateLocation = null }).Validate();

        Reject(() => (Broker() with { Id = "a/b" }).Validate(), "topic character in broker ID");
        Reject(() => (Broker() with { Host = "" }).Validate(), "empty host");
        Reject(() => (Broker() with { Host = "bad host" }).Validate(), "space in host");
        Reject(() => (Broker() with { Port = 0 }).Validate(), "port 0");
        Reject(() => (Broker() with { Port = 65536 }).Validate(), "port above 65535");
        Reject(() => (Broker() with { CertificateLocation = null }).Validate(), "TLS without certificate");
        Reject(() => (Broker() with { UseTls = false }).Validate(), "certificate without TLS");
        Reject(() => (Broker() with { CertificateLocation = "http://certs.example.com/broker.crt", CertificateSha256 = Fingerprint }).Validate(), "plain http certificate URL");
        Reject(() => (Broker() with { CertificateLocation = "https://certs.example.com/broker.crt" }).Validate(), "URL without fingerprint");
        Reject(() => (Broker() with { CertificateSha256 = "abc" }).Validate(), "short fingerprint");
        Reject(() => (Broker() with { CertificateSha256 = Fingerprint[..63] + "z" }).Validate(), "non-hex fingerprint");
        Reject(() => (Broker() with { KeepAliveSeconds = 0 }).Validate(), "keep-alive disabled");
        Reject(() => (Broker() with { ConnectTimeoutSeconds = 0 }).Validate(), "zero connect timeout");
        Reject(() => (Broker() with { RetryDelaySeconds = 0 }).Validate(), "zero retry delay");
        Reject(() => (Broker() with { MaxRetryDelaySeconds = 1 }).Validate(), "maximum retry below retry delay");

        // JSON at the API boundary: unknown fields are refused, and snapshots without broker settings still load.
        var json = JsonSerializer.Serialize(Broker(), EdgeTopics.Json);
        Check(JsonSerializer.Deserialize<BrokerProfile>(json, EdgeTopics.Json) == Broker(), "JSON round trip changed the profile");
        try
        {
            JsonSerializer.Deserialize<BrokerProfile>(json.TrimEnd('}') + ""","password":"x"}""", EdgeTopics.Json);
            throw new Exception("Broker profile: unknown field such as a password was accepted");
        }
        catch (JsonException) { }
        var old = JsonSerializer.Deserialize<GatewayConfiguration>("""{"version":3,"devices":[]}""", EdgeTopics.Json)!;
        old.Validate();
        Check(old.Broker is null, "snapshot without broker settings did not load");
        Check(Throws(() => new GatewayConfiguration(1, [], Broker() with { Port = 0 }).Validate()),
            "snapshot with invalid broker settings was accepted");

        Console.WriteLine("PASS: Broker profile: valid defaults, file/https certificates with SHA-256 pins, port/ID/timing limits, TLS consistency, JSON round trip, unknown-field refusal and snapshots without broker settings.");
        RunStore();
    }

    // The registry's Brokers table: seeded broker, edits with version bumps, invalid saves and deleting the last broker.
    private static void RunStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "broker-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "configuration.db");
            using (var store = new EdgeGateway.Configuration.DeviceStore(path))
            {
                Check(store.AllBrokers().Single() == Broker(), "migration did not seed today's broker as 'local'");
                Check(store.Snapshot() is { Version: 0, Broker: null }, "snapshot without broker ID changed");
                Check(store.Snapshot("local") is { Version: 0, Broker: { } seeded } && seeded == Broker(), "snapshot did not carry the broker");
                Check(store.Snapshot("missing") is null, "unknown broker was not reported");

                Check(!store.SaveBroker(Broker(), create: true), "duplicate broker created");
                var moved = Broker() with { Port = 18883, CertificateSha256 = Fingerprint.ToUpperInvariant() };
                Check(store.SaveBroker(moved, create: false), "broker update failed");
                Check(store.FindBroker("local") == moved with { CertificateSha256 = Fingerprint }, "fingerprint not stored in lower case");
                Check(store.Snapshot("local")!.Version == 1, "broker edit did not bump the version");
                Check(!store.SaveBroker(Broker() with { Id = "absent" }, create: false), "update of a missing broker succeeded");

                Reject(() => store.SaveBroker(Broker() with { Port = 0 }, create: false), "invalid port saved");
                Reject(() => store.SaveBroker(Broker() with { CertificateLocation = "http://x/broker.crt", CertificateSha256 = Fingerprint }, create: false), "http certificate saved");
                Check(store.FindBroker("local")!.Port == 18883 && store.Snapshot().Version == 1, "a refused save changed data or the version");

                try { store.DeleteBroker("local"); throw new Exception("Broker profile: the only broker was deleted"); }
                catch (EdgeGateway.Configuration.ConfigurationConflictException) { }
                Check(store.SaveBroker(Broker() with { Id = "cloud", Host = "broker.example.com" }, create: true), "second broker not created");
                Check(store.DeleteBroker("local") && store.AllBrokers().Single().Id == "cloud", "broker delete failed");
                Check(!store.DeleteBroker("local"), "deleting a missing broker reported success");
                Check(store.Snapshot().Version == 3, "create/delete did not bump the version");
            }
            using (var store = new EdgeGateway.Configuration.DeviceStore(path))
                Check(store.FindBroker("cloud")?.Host == "broker.example.com", "broker settings did not survive a restart");
            Console.WriteLine("PASS: Brokers table: migration seed, snapshot with/without broker, unknown broker, create/update/delete with version bumps, lower-case fingerprints, refused invalid saves and protected last broker.");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; } catch (ArgumentException) { return true; }
    }
}
