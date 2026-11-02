using System.Text.Json.Serialization;

namespace IoT.Contracts;

// Where the broker is, how to trust it and how to keep the connection alive. The gateway's own identity
// (client ID, Sparkplug IDs) stays in its local settings. The certificate is a file path on the gateway box
// or an https URL; never the certificate or a key itself.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BrokerProfile(
    string Id, string Host, int Port, bool UseTls, string? CertificateLocation, string? CertificateSha256,
    int KeepAliveSeconds, int ConnectTimeoutSeconds, int RetryDelaySeconds, int MaxRetryDelaySeconds)
{
    public void Validate()
    {
        DeviceRegistration.ValidateId(Id, "Broker ID");
        if (string.IsNullOrWhiteSpace(Host) || Host.Any(char.IsWhiteSpace)) throw new ArgumentException("Broker host is required and must not contain spaces.");
        if (Port is < 1 or > 65535) throw new ArgumentException("Broker port must be between 1 and 65535.");
        // Keep-alive also bounds how long a silently dead broker goes unnoticed while idle.
        if (KeepAliveSeconds is < 5 or > 3600) throw new ArgumentException("Keep-alive must be 5–3600 seconds.");
        if (ConnectTimeoutSeconds is < 1 or > 300) throw new ArgumentException("Connect timeout must be 1–300 seconds.");
        if (RetryDelaySeconds is < 1 or > 3600) throw new ArgumentException("Retry delay must be 1–3600 seconds.");
        if (MaxRetryDelaySeconds < RetryDelaySeconds || MaxRetryDelaySeconds > 3600)
            throw new ArgumentException("Maximum retry delay must be at least the retry delay and at most 3600 seconds.");
        if (!UseTls)
        {
            if (CertificateLocation is not null || CertificateSha256 is not null)
                throw new ArgumentException("Certificate settings need TLS. Enable TLS or remove the certificate.");
            return;
        }
        if (string.IsNullOrWhiteSpace(CertificateLocation)) throw new ArgumentException("TLS needs a certificate location (file path or https URL).");
        // A Windows or Unix path parses as a file URI; any other scheme must be https, so the trust anchor cannot be swapped in transit.
        var isUrl = Uri.TryCreate(CertificateLocation, UriKind.Absolute, out var uri) && !uri.IsFile;
        if (isUrl && uri!.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("A certificate URL must use https.");
        if (CertificateSha256 is not null && (CertificateSha256.Length != 64 || !CertificateSha256.All(Uri.IsHexDigit)))
            throw new ArgumentException("Certificate fingerprint must be a SHA-256 hash: 64 hexadecimal characters.");
        if (isUrl && CertificateSha256 is null) throw new ArgumentException("A certificate URL needs its SHA-256 fingerprint, so a replaced file is refused.");
    }
}
