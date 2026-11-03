using Demo.Control;
using EdgeGateway.Gateway.Storage;

internal static class DemoStatusChecks
{
    public static void Run()
    {
        var activity = new ActivityLog();
        activity.FromGateway("Poll meter: holding 200+1: IllegalDataAddress; 0 consecutive communication failure(s).");
        activity.FromGateway("Poll meter: holding 200+1: IllegalDataAddress; 0 consecutive communication failure(s).");
        activity.FromGateway("Poll meter: IOException: connection lost; 1 consecutive communication failure(s).");
        if (activity.Recent().Length != 2 || !activity.Recent().All(entry => entry.Kind == "fail"))
            throw new Exception("Demo activity lost frame/transport errors or repeated an unchanged error.");
        var directory = Path.Combine(Path.GetTempPath(), "demo-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "status.json");
        void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
        try
        {
            Check(ComponentManager.ReadGatewaySnapshot(path, true) is null, "Missing status was accepted");
            Check(GatewayStatusFile.TryWrite(path, "{\"unsent\":1}"), "Initial status failed");
            // Reproduce the old Windows reader denying File.Move(..., overwrite:true).
            if (OperatingSystem.IsWindows())
            {
                using (var oldReader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Check(!GatewayStatusFile.TryWrite(path, "{\"unsent\":2}"), "Reader did not block replacement as expected");
            }
            Check(GatewayStatusFile.TryWrite(path, "{\"unsent\":3}"), "Reporting did not recover after lock released");
            Check(ComponentManager.ReadGatewaySnapshot(path, true)?.GetProperty("unsent").GetInt32() == 3, "Recovery status not readable");
            Check(ComponentManager.ReadGatewaySnapshot(path, false) is null, "Stopped gateway exposed old status");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-10));
            Check(ComponentManager.ReadGatewaySnapshot(path, true) is null, "Stale data reported as fresh");
            GatewayStatusFile.TryWrite(path, "broken json");
            Check(ComponentManager.ReadGatewaySnapshot(path, true) is null, "Malformed snapshot accepted");
            var successfulWrites = 0;
            Parallel.Invoke(
                () => { for (var i = 0; i < 300; i++) if (GatewayStatusFile.TryWrite(path, $"{{\"unsent\":{i}}}")) successfulWrites++; },
                () => { for (var i = 0; i < 300; i++) ComponentManager.ReadGatewaySnapshot(path, true); });
            Check(successfulWrites > 0, "No concurrent status updates succeeded");
            // Windows can still deny a single replace briefly (e.g. filesystem filters).
            // Reporting must survive that tick and publish the next snapshot.
            Check(GatewayStatusFile.TryWrite(path, "{\"unsent\":299}"), "Reporting did not resume after concurrent activity");
            Check(ComponentManager.ReadGatewaySnapshot(path, true)?.GetProperty("unsent").GetInt32() == 299, "Concurrent reporting stopped");
            Console.WriteLine("PASS: Demo diagnostics: Windows replacement lock recovery, concurrent read/write, missing/stale/malformed status and stopped process.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
