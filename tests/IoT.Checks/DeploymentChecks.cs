using System.Text;
using System.Text.Json;
using EdgeGateway.Configuration;
using IoT.Contracts;

internal static class DeploymentChecks
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("Deployment: " + message); }

    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "edge-deployment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "registry.db");
            var seed = new BrokerProfile("local", "mosquitto", 8883, true, "/certs/broker.crt", null, 60, 10, 2, 30);
            long version;
            using (var store = DeploymentBootstrap.Open(path, seed))
            {
                Check(store.FindBroker("local") == seed, "fresh deployment kept the native loopback broker");
                Check(!File.Exists(path + ".bootstrap-pending"), "successful bootstrap marker not cleared");
                Check(store.SaveBroker(seed with { Host = "plant-broker" }, false), "operator edit failed");
                version = store.Snapshot().Version;
            }
            using (var store = DeploymentBootstrap.Open(path, seed))
                Check(store.FindBroker("local")!.Host == "plant-broker" && store.Snapshot().Version == version,
                    "restart overwrote saved broker or bumped version");
            var interrupted = Path.Combine(directory, "interrupted.db");
            using (var old = new DeviceStore(interrupted)) { }
            File.WriteAllText(interrupted + ".bootstrap-pending", "pending");
            using (var store = DeploymentBootstrap.Open(interrupted, seed))
                Check(store.FindBroker("local") == seed, "interrupted initialization was not completed");
            var native = Path.Combine(directory, "native.db");
            using (var store = DeploymentBootstrap.Open(native, null))
                Check(store.FindBroker("local")!.Host == "127.0.0.1", "native defaults changed");
            using (var store = DeploymentBootstrap.Open(native, seed))
                Check(store.FindBroker("local")!.Host == "127.0.0.1", "existing native DB unexpectedly overwritten");

            const string username = "operator", password = "correct:pässword";
            string Header(string value) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
            Check(ManagementAccess.Matches(Header(username + ":" + password), username, password), "valid UTF-8 credentials refused");
            foreach (var chosenPassword in new[] { "", "x", " ", "quotes'\"\\:$${SHELL}\r\n\t雪🙂 ", "\uFEFF\0", new string('x', 4096) })
                Check(ManagementAccess.Matches(Header(username + ":" + chosenPassword), username, chosenPassword),
                    "chosen password length or characters changed");
            using (var client = new EdgeGateway.Gateway.Configuration.ConfigClient(new()
                { Username = username, Password = "", StateDirectory = directory })) { }
            foreach (var header in new[] { null, "Basic ???", Header("operator:wrong"), Header("wrong:" + password), "Bearer token", Header("no-colon") })
                Check(!ManagementAccess.Matches(header, username, password), "invalid credentials accepted");
            var statusPath = Path.Combine(directory, "status.json");
            var now = DateTimeOffset.UtcNow;
            void Status(DateTimeOffset updated) => File.WriteAllText(statusPath, JsonSerializer.Serialize(new { updatedAt = updated, gatewayId = "edge-01" }));
            Check(GatewayRuntimeStatus.ReadFresh(statusPath, TimeSpan.FromSeconds(15), now) is null, "missing status accepted");
            Status(now); Check(GatewayRuntimeStatus.ReadFresh(statusPath, TimeSpan.FromSeconds(15), now) is not null, "fresh status refused");
            Status(now.AddSeconds(-16)); Check(GatewayRuntimeStatus.ReadFresh(statusPath, TimeSpan.FromSeconds(15), now) is null, "stale status accepted");
            Status(now.AddMinutes(1)); Check(GatewayRuntimeStatus.ReadFresh(statusPath, TimeSpan.FromSeconds(15), now) is null, "future status accepted");
            foreach (var malformed in new[] { "{", "[]", "{\"updatedAt\":false}", "{}" })
            {
                File.WriteAllText(statusPath, malformed);
                Check(GatewayRuntimeStatus.ReadFresh(statusPath, TimeSpan.FromSeconds(15), now) is null, "malformed status accepted");
            }
            Console.WriteLine("PASS: Deployment broker seed/retry, restart preserves registry, native defaults unchanged, authentication and stale/malformed gateway status.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
