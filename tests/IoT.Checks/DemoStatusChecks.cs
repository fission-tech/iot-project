using Demo.Control;
using EdgeGateway.Gateway.Storage;

internal static class DemoStatusChecks
{
    public static void Run()
    {
        var process = new ManagedProcess("unused", ".", ".");
        var append = typeof(ManagedProcess).GetMethod("Append", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        const string dated = "2026-11-02 15:30:00 [INFO] device-01: Pressure reading recovered. Quality = good.";
        append.Invoke(process, [dated]);
        append.Invoke(process, ["Undated message"]);
        if (process.Tail(2)[0] != dated || !process.Tail(2)[1].EndsWith(" Undated message", StringComparison.Ordinal))
            throw new Exception("Demo duplicated Gateway timestamps or stopped stamping undated output.");
        var activity = new ActivityLog();
        activity.FromGateway("2026-11-03 14:00:00 [WARN] Device meter: holding 200+1 [Pressure]: IllegalDataAddress (0x02).");
        activity.FromGateway("2026-11-03 14:00:01 [INFO] meter: Pressure reading recovered. Quality = good.");
        activity.FromGateway("2026-11-03 14:00:02 [WARN] Device meter: holding 200+1 [Pressure]: IllegalDataAddress (0x02).");
        if (activity.Recent().Length != 3 || activity.Recent().Count(entry => entry.Kind == "fail") != 2
            || activity.Recent().Count(entry => entry.Kind == "online") != 1)
            throw new Exception("Demo activity lost current error/recovery messages or suppressed a recurring error.");
        activity.FromGateway("2026-11-03 14:00:03 [INFO] meter offline (not enabled in configuration).");
        activity.FromGateway("2026-11-03 14:00:04 [ERROR] Gateway stopped unexpectedly.");
        if (activity.Recent().Count(entry => entry.Kind == "offline") != 1
            || activity.Recent().Count(entry => entry.Kind == "fail") != 3)
            throw new Exception("Demo activity lost disabled-device or fatal Gateway messages.");
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
