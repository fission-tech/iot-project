using System.Text.Json;

namespace IoT.Contracts;

// Diagnostics only: a stopped worker must never appear online because its last file still exists.
public static class GatewayRuntimeStatus
{
    public static JsonElement? ReadFresh(string path, TimeSpan maximumAge, DateTimeOffset? now = null)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("updatedAt", out var field) || !field.TryGetDateTimeOffset(out var updated))
                return null;
            var age = (now ?? DateTimeOffset.UtcNow) - updated;
            return age >= TimeSpan.FromSeconds(-5) && age <= maximumAge ? document.RootElement.Clone() : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                                      or InvalidOperationException or FormatException) { return null; }
    }
}
