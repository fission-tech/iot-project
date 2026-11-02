using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using IoT.Contracts;

namespace EdgeGateway.Gateway.Connection;

public sealed class BrokerCertificateException(string message) : Exception(message);

// Loads the certificate the broker must present. A file path is read on every connect; an https URL is downloaded
// once and cached under its fingerprint, so the gateway still connects when the URL is unreachable.
// A pinned SHA-256 fingerprint is compared with the certificate itself, so a replaced file is refused.
public static class BrokerCertificates
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<X509Certificate2> LoadAsync(BrokerProfile broker, string stateDirectory, CancellationToken cancellationToken)
    {
        var location = broker.CertificateLocation ?? throw new BrokerCertificateException($"Broker {broker.Id} uses TLS but names no certificate.");
        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.IsFile)
        {
            var path = Path.Combine(AppContext.BaseDirectory, location);
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(path, cancellationToken); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new BrokerCertificateException($"Certificate {location} could not be read: {error.Message}");
            }
            return Verified(Parse(bytes, location), broker.CertificateSha256, location);
        }

        var fingerprint = broker.CertificateSha256!.ToLowerInvariant();
        var cachePath = Path.Combine(stateDirectory, "certs", fingerprint + ".crt");
        if (File.Exists(cachePath))
        {
            try { return Verified(Parse(await File.ReadAllBytesAsync(cachePath, cancellationToken), cachePath), fingerprint, cachePath); }
            catch (BrokerCertificateException) { File.Delete(cachePath); } // A damaged copy is fetched again.
        }
        byte[] downloaded;
        try { downloaded = await Http.GetByteArrayAsync(uri, cancellationToken); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new BrokerCertificateException($"Certificate {location} could not be downloaded and is not cached: {error.Message}");
        }
        var certificate = Verified(Parse(downloaded, location), fingerprint, location);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        await File.WriteAllBytesAsync(cachePath + ".tmp", downloaded, cancellationToken);
        File.Move(cachePath + ".tmp", cachePath, overwrite: true);
        return certificate;
    }

    // Lower-case hex SHA-256 of the certificate (what `openssl x509 -fingerprint -sha256` prints, without colons).
    public static string Fingerprint(X509Certificate2 certificate) =>
        Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant();

    private static X509Certificate2 Parse(byte[] bytes, string source)
    {
        try
        {
            var text = Encoding.ASCII.GetString(bytes);
            return text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)
                ? X509Certificate2.CreateFromPem(text)
                : new X509Certificate2(bytes);
        }
        catch (CryptographicException error)
        {
            throw new BrokerCertificateException($"Certificate {source} is not a valid certificate: {error.Message}");
        }
    }

    private static X509Certificate2 Verified(X509Certificate2 certificate, string? expected, string source)
    {
        if (expected is null || Fingerprint(certificate) == expected.ToLowerInvariant()) return certificate;
        var actual = Fingerprint(certificate);
        certificate.Dispose();
        throw new BrokerCertificateException($"Certificate {source} has fingerprint {actual}, not the pinned {expected.ToLowerInvariant()}.");
    }
}
