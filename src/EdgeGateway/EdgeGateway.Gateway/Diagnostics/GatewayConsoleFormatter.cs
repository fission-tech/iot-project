using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace EdgeGateway.Gateway.Diagnostics;

public sealed class GatewayConsoleFormatter() : ConsoleFormatter("gateway")
{
    public override void Write<TState>(in LogEntry<TState> entry, IExternalScopeProvider? scopes, TextWriter writer)
    {
        var message = entry.Formatter(entry.State, entry.Exception);
        if (string.IsNullOrEmpty(message) && entry.Exception is null) return;
        var level = entry.LogLevel switch
        {
            LogLevel.Information => "INFO", LogLevel.Warning => "WARN",
            _ => entry.LogLevel.ToString().ToUpperInvariant()
        };
        writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message.ReplaceLineEndings(" ")}");
        if (entry.Exception is not null) writer.WriteLine(entry.Exception);
    }
}
