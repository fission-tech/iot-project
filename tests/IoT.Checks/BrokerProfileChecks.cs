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
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; } catch (ArgumentException) { return true; }
    }
}
