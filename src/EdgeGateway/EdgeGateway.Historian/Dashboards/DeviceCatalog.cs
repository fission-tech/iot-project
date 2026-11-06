using System.Security.Cryptography;
using System.Text;

namespace EdgeGateway.Historian.Dashboards;

// One metric of a device as the dashboards see it: what the birth said, plus the registry's description.
// Metric is the permanent key its readings are stored under; Name is its current name, which a rename changes.
public sealed record CatalogMetric(string Metric, string Name, string Kind, string? Register, string? DataType, string? Unit,
    double? Scale, bool InRegistry)
{
    // The registry's register area decides boolean vs numeric when known; otherwise the Sparkplug datatype does.
    public bool IsBoolean => Register is "coil" or "discrete" || (Register is null && Kind == SparkplugRows.Boolean);
}

public sealed record DeviceCatalog(string Node, string Device, bool InRegistry, CatalogMetric[] Metrics)
{
    public string Uid => DashboardUid(Node, Device);

    // Stable, at most 40 characters (Grafana's limit), and unique per gateway: two gateways may use the same device ID.
    public static string DashboardUid(string node, string device)
    {
        var slug = new string(device.ToLowerInvariant().Select(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' ? c : '-').ToArray());
        if (slug.Length > 26) slug = slug[..26];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(node + "/" + device)))[..6].ToLowerInvariant();
        return $"dev-{slug}-{hash}";
    }
}

// The registry's description of a metric, matched to the catalog by its permanent ID (or, for a metric stored
// before IDs existed, by device ID and metric name).
public sealed record RegistryMetric(string Device, string Metric, string Register, string DataType, string Unit, double Scale,
    string? Id = null);
