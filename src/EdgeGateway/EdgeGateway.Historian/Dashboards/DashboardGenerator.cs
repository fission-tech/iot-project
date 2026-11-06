using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace EdgeGateway.Historian.Dashboards;

// Keeps one Grafana dashboard file per device in sync with the catalog. Runs at startup, after every device birth,
// and every RefreshSeconds (which also picks up registry edits that send no birth, such as a unit change).
// Grafana's file provider reads the folder; only changed files are rewritten, so Grafana adds no needless versions.
// Failures are logged and retried on the next pass; they never affect storing readings.
public sealed class DashboardGenerator(HistoryStore store, RegistryClient? registry, string directory,
    TimeSpan refresh, ILogger<DashboardGenerator> log)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly SemaphoreSlim _requested = new(0, 1);
    private bool _registryWarned;

    public string DevicesDirectory => Path.Combine(directory, "devices");

    // Called after a birth is stored: the device or its metric list may be new.
    public void Request()
    {
        try { _requested.Release(); }
        catch (SemaphoreFullException) { } // a pass is already pending
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await SyncAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                log.LogWarning("Device dashboards not updated: {Reason}. Retrying in {Seconds}s.",
                    error.GetBaseException().Message.TrimEnd('.'), refresh.TotalSeconds);
            }
            try { await _requested.WaitAsync(refresh, cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        if (registry is not null)
        {
            try
            {
                var (metrics, devices) = await registry.ReadAsync(cancellationToken);
                await store.ApplyRegistryAsync(metrics, devices, cancellationToken);
                if (_registryWarned) log.LogInformation("Device registry reachable again.");
                _registryWarned = false;
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException
                                          && !cancellationToken.IsCancellationRequested)
            {
                // Keep the last known descriptions; boolean vs numeric still comes from the births.
                if (!_registryWarned)
                    log.LogWarning("Device registry unavailable ({Reason}); dashboards use the last known metric descriptions.",
                        error.GetBaseException().Message.TrimEnd('.'));
                _registryWarned = true;
            }
        }

        var catalog = await store.ReadCatalogAsync(cancellationToken);
        Directory.CreateDirectory(DevicesDirectory);
        var written = 0;
        foreach (var device in catalog)
            if (WriteIfChanged(Path.Combine(DevicesDirectory, device.Uid + ".json"), Render(device))) written++;
        // A file whose device left the catalog (database reset) would show a dashboard with no data.
        var current = catalog.Select(device => device.Uid + ".json").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in Directory.EnumerateFiles(DevicesDirectory, "dev-*.json").Where(path => !current.Contains(Path.GetFileName(path))))
            File.Delete(stale);
        if (written > 0) log.LogInformation("Updated {Count} device dashboard(s) in {Directory}.", written, DevicesDirectory);
    }

    public static string Render(DeviceCatalog device) => DeviceDashboardBuilder.Build(device).ToJsonString(Json) + "\n";

    // Atomic: Grafana never reads a half-written file.
    public static bool WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content) return false;
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
        return true;
    }
}
