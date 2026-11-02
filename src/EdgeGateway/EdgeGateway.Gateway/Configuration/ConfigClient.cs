using System.Net.Http.Json;
using System.Text.Json;
using IoT.Contracts;

namespace EdgeGateway.Gateway.Configuration;

// Fetches the device list and this gateway's broker settings from the configuration service and keeps a local copy,
// so data acquisition keeps polling even when the configuration service is down. The broker is the one the registry
// assigns to gatewayId, else the one named by brokerId.
public sealed class ConfigClient(DaqSettings settings, string? brokerId = null, string? gatewayId = null) : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly string _cachePath = Path.Combine(settings.StateDirectory, "config-cache.json");
    private bool _brokerMissing;

    public async Task<GatewayConfiguration?> TryFetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            var url = settings.ConfigUrl.TrimEnd('/') + "/config";
            var query = string.Join("&", new[] { ("broker", brokerId), ("gateway", gatewayId) }
                .Where(item => item.Item2 is not null).Select(item => $"{item.Item1}={Uri.EscapeDataString(item.Item2!)}"));
            GatewayConfiguration? configuration;
            try
            {
                configuration = await _http.GetFromJsonAsync<GatewayConfiguration>(
                    query == "" ? url : url + "?" + query, EdgeTopics.Json, cancellationToken);
                _brokerMissing = false;
            }
            catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound && brokerId is not null)
            {
                // A removed or renamed broker must not stop device updates; the gateway keeps its working broker.
                if (!_brokerMissing) Console.WriteLine($"Broker {brokerId} is not in the registry. Devices still update; broker settings stay as they are.");
                _brokerMissing = true;
                configuration = await _http.GetFromJsonAsync<GatewayConfiguration>(url, EdgeTopics.Json, cancellationToken);
            }
            if (configuration is null) return null;
            configuration.Validate(); // Do not replace a usable cache with an invalid/old-format snapshot.
            Directory.CreateDirectory(settings.StateDirectory);
            await File.WriteAllTextAsync(_cachePath + ".tmp", JsonSerializer.Serialize(configuration, EdgeTopics.Json), cancellationToken);
            File.Move(_cachePath + ".tmp", _cachePath, overwrite: true);
            return configuration;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or ArgumentException
                                          && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public GatewayConfiguration? LoadCache()
    {
        try
        {
            var configuration = JsonSerializer.Deserialize<GatewayConfiguration>(File.ReadAllText(_cachePath), EdgeTopics.Json);
            configuration?.Validate();
            return configuration;
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException) { return null; }
    }

    public void Dispose() => _http.Dispose();
}
