using System.Security.Cryptography;
using System.Text;

namespace EdgeGateway.Configuration;

public static class ManagementAccess
{
    public static bool Matches(string? authorization, string username, string password)
    {
        if (authorization is null || !authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..].Trim()));
            var colon = decoded.IndexOf(':');
            if (colon < 1) return false;
            var userOk = Equal(decoded[..colon], username);
            var passwordOk = Equal(decoded[(colon + 1)..], password);
            return userOk && passwordOk;
        }
        catch (FormatException) { return false; }
    }

    private static bool Equal(string left, string right) => CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(left)), SHA256.HashData(Encoding.UTF8.GetBytes(right)));
}
