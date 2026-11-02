using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using EdgeGateway.Gateway.Configuration;
using EdgeGateway.Gateway.Connection;
using IoT.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

// Gateway side of registry-managed broker settings: which settings are used, what survives a restart,
// how certificates are pinned and cached, and that a missing broker never blocks device updates.
internal static class GatewayBrokerChecks
{
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception("Gateway broker: " + reason); }

    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gateway-broker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            CheckProfiles(directory);
            CheckSelector(directory);
            await CheckCertificatesAsync(directory);
            await CheckMissingBrokerAsync(directory);
            Console.WriteLine("PASS: Gateway broker settings: appsettings seed, registry offer/validation, trial window, rollback with rejection, alternation while the broker is down, fresh trial after a change, last-good persistence and corrupt-file fallback, pinned file certificates, cached URL certificates, mismatch refusal, and device updates when the broker is missing.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static MqttSettings Settings(string directory) => new()
    {
        BrokerId = "local", Host = "10.0.0.5", Port = 1883, UseTls = false, StateDirectory = Path.Combine(directory, "state"),
        KeepAliveSeconds = 20, ConnectTimeoutSeconds = 5, RetryDelaySeconds = 1, MaxRetryDelaySeconds = 8
    };

    private static void CheckProfiles(string directory)
    {
        var settings = Settings(directory);
        var profiles = new BrokerProfiles(settings);
        Check(profiles.Seed == new BrokerProfile("local", "10.0.0.5", 1883, false, null, null, 20, 5, 1, 8), "seed does not mirror appsettings");
        Check(profiles.Fallback == profiles.Seed && profiles.Desired is null && profiles.LastGood is null, "first boot should fall back to the seed");

        var changes = 0;
        profiles.Changed += () => changes++;
        var registry = profiles.Seed with { Host = "broker.example.com" };
        profiles.Offer(registry);
        profiles.Offer(registry);
        Check(profiles.Desired == registry && changes == 1, "registry settings not offered exactly once");
        profiles.Offer(registry with { Port = 0 });
        Check(profiles.Desired == registry && changes == 1, "invalid registry settings replaced valid ones");
        profiles.Offer(null);
        Check(profiles.Desired is null && changes == 2, "a registry without this broker did not clear the desired settings");

        profiles.MarkGood(registry);
        var restarted = new BrokerProfiles(settings);
        Check(restarted.LastGood == registry && restarted.Fallback == registry, "last working settings did not survive a restart");

        File.WriteAllText(Path.Combine(settings.StateDirectory, "broker-last-good.json"), "{ not json");
        Check(new BrokerProfiles(settings).Fallback == profiles.Seed, "a corrupt last-good file did not fall back to the seed");
    }

    private static void CheckSelector(string directory)
    {
        var profiles = new BrokerProfiles(Settings(Path.Combine(directory, "selector")));
        var selector = new BrokerSelector(profiles, TimeSpan.FromSeconds(60));
        var start = DateTimeOffset.Parse("2026-11-02T10:00:00Z");
        var seed = profiles.Seed;

        // No registry settings yet: the seed, and once it connects it becomes the last working settings.
        Check(selector.Next(start) == seed, "first boot did not use the seed");
        selector.Connected(seed);
        Check(selector.State(seed, null).Source == "last-good" || profiles.LastGood != seed, "seed not recorded as working");
        Check(profiles.LastGood == seed, "seed not recorded as working");
        profiles.Offer(seed);
        Check(!selector.ShouldSwitch(seed) && selector.Next(start) == seed, "identical registry settings caused a switch");

        // Good new settings: tried at once and adopted.
        var moved = seed with { Port = 1884 };
        profiles.Offer(moved);
        Check(selector.ShouldSwitch(seed), "changed registry settings did not request a switch");
        Check(selector.Next(start) == moved && selector.State(seed, null).Trying == moved, "new settings not on trial");
        selector.Connected(moved);
        Check(profiles.LastGood == moved && selector.State(moved, null) is { Source: "registry", Trying: null }, "working new settings not adopted");

        // Wrong settings: only they are tried for the window, then the fallback restores service and rejects them.
        var wrong = moved with { Port = 9 };
        profiles.Offer(wrong);
        Check(selector.Next(start) == wrong, "wrong settings not tried first");
        selector.Failed(wrong, "Connection refused");
        Check(selector.Next(start.AddSeconds(59)) == wrong, "fallback used before the trial window ended");
        Check(selector.Next(start.AddSeconds(61)) == moved, "fallback not tried after the trial window");
        selector.Connected(moved);
        var state = selector.State(moved, null);
        Check(state.Rejected == wrong && state.RejectedReason == "Connection refused" && state.Source == "last-good", "failed settings not rejected with their error");
        Check(!selector.ShouldSwitch(moved) && selector.Next(start.AddSeconds(62)) == moved, "rejected settings were tried again");
        Check(profiles.LastGood == moved, "rejected settings replaced the last working ones");

        // A changed broker while it is down: both are tried in turn and whichever connects first wins, without rejecting.
        var fixedPort = moved with { Port = 1885 };
        profiles.Offer(fixedPort);
        Check(selector.State(moved, null).Rejected is null, "old rejection kept after the registry moved on");
        var later = start.AddMinutes(10);
        Check(selector.Next(later) == fixedPort, "new settings not tried first");
        var turns = new[] { selector.Next(later.AddSeconds(61)), selector.Next(later.AddSeconds(62)), selector.Next(later.AddSeconds(63)) };
        Check(turns.SequenceEqual([moved, fixedPort, moved]), "settings were not tried in turn after the window");
        selector.Connected(fixedPort);
        Check(profiles.LastGood == fixedPort && selector.State(fixedPort, null).Rejected is null, "settings that connected late were not adopted");

        // Saving previously rejected values again later earns a fresh trial.
        profiles.Offer(wrong);
        Check(selector.ShouldSwitch(fixedPort) && selector.Next(later.AddMinutes(5)) == wrong, "re-saved settings were not tried again");

        // The registry dropping this broker keeps the working connection.
        profiles.Offer(null);
        Check(!selector.ShouldSwitch(fixedPort) && selector.Next(later.AddMinutes(6)) == fixedPort, "a removed registry broker disturbed the connection");
    }

    private static async Task CheckCertificatesAsync(string directory)
    {
        using var key = RSA.Create(2048);
        using var issued = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var pem = issued.ExportCertificatePem();
        var path = Path.Combine(directory, "broker.crt");
        File.WriteAllText(path, pem);
        var fingerprint = BrokerCertificates.Fingerprint(issued);
        var state = Path.Combine(directory, "state");
        var broker = new BrokerProfile("local", "127.0.0.1", 8883, true, path, null, 60, 10, 2, 30);

        using (var loaded = await BrokerCertificates.LoadAsync(broker, state, CancellationToken.None))
            Check(BrokerCertificates.Fingerprint(loaded) == fingerprint, "file certificate not loaded");
        using (var pinned = await BrokerCertificates.LoadAsync(broker with { CertificateSha256 = fingerprint.ToUpperInvariant() }, state, CancellationToken.None))
            Check(pinned.Subject == "CN=localhost", "pinned file certificate refused");
        await Refused(broker with { CertificateSha256 = new string('0', 64) }, state, "pinned", "a certificate with another fingerprint was trusted");
        await Refused(broker with { CertificateLocation = Path.Combine(directory, "missing.crt") }, state, "could not be read", "a missing certificate file was not reported");

        // An unreachable URL works from the cached copy, and is refused without one.
        var url = broker with { CertificateLocation = $"https://127.0.0.1:{FreePort()}/broker.crt", CertificateSha256 = fingerprint };
        await Refused(url, state, "not cached", "an unreachable URL without a cached copy was accepted");
        Directory.CreateDirectory(Path.Combine(state, "certs"));
        File.WriteAllText(Path.Combine(state, "certs", fingerprint + ".crt"), pem);
        using (var cached = await BrokerCertificates.LoadAsync(url, state, CancellationToken.None))
            Check(BrokerCertificates.Fingerprint(cached) == fingerprint, "cached URL certificate not used");
    }

    private static async Task Refused(BrokerProfile broker, string state, string reason, string failure)
    {
        try { (await BrokerCertificates.LoadAsync(broker, state, CancellationToken.None)).Dispose(); }
        catch (BrokerCertificateException error) when (error.Message.Contains(reason)) { return; }
        throw new Exception("Gateway broker: " + failure);
    }

    private static async Task CheckMissingBrokerAsync(string directory)
    {
        var port = FreePort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        await using var app = builder.Build();
        var snapshot = new GatewayConfiguration(7, []);
        app.MapGet("/config", (string? broker) => broker is null ? Results.Json(snapshot, EdgeTopics.Json) : Results.NotFound());
        await app.StartAsync();

        using var client = new ConfigClient(new DaqSettings { ConfigUrl = $"http://127.0.0.1:{port}", StateDirectory = Path.Combine(directory, "daq") }, "removed");
        var fetched = await client.TryFetchAsync(CancellationToken.None);
        Check(fetched is { Version: 7, Broker: null }, "a missing broker stopped the device configuration from loading");
        await app.StopAsync();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
