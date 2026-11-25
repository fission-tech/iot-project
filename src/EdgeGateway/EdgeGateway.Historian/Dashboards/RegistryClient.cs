using System.Net.Http.Json;
using IoT.Contracts;

namespace EdgeGateway.Historian.Dashboards;

// Reads every registered device, enabled or not, from the configuration service. A disabled device is still in
// the registry, so its dashboard is not marked removed.
public sealed class RegistryClient : IDisposable
{
    private readonly HttpClient _http;
    public RegistryClient(string configUrl, string? username = null, string password = "")
    {
        _http = new() { BaseAddress = new Uri(configUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(10) };
        if (username is not null)
            _http.DefaultRequestHeaders.Authorization = new("Basic",
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(username + ":" + password)));
    }

    public async Task<(RegistryMetric[] Metrics, string[] Devices)> ReadAsync(CancellationToken cancellationToken)
    {
        var devices = await _http.GetFromJsonAsync<DeviceRegistration[]>("devices", EdgeTopics.Json, cancellationToken)
            ?? throw new InvalidOperationException("The device registry returned no device list.");
        return (Describe(devices), devices.Select(device => device.Id).ToArray());
    }

    public static RegistryMetric[] Describe(IEnumerable<DeviceRegistration> devices) =>
        devices.SelectMany(device => (device.Metrics ?? []).Select(metric =>
            new RegistryMetric(device.Id, metric.Name, metric.Register, metric.Type, metric.Unit ?? "", metric.Scale, metric.Id))).ToArray();

    public void Dispose() => _http.Dispose();
}
