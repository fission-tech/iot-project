using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdgeGateway.Gateway.Connection;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    PublishingBirth,
    Online,
    RetryWaiting,
    Stopping,
    Stopped
}

public sealed class ConnectionStateMachine(ILogger? logger = null)
{
    private readonly ILogger _log = logger ?? NullLogger.Instance;
    private readonly object _sync = new();
    private ConnectionState _current = ConnectionState.Disconnected;

    public ConnectionState Current
    {
        get { lock (_sync) return _current; }
    }

    public bool TryMoveTo(ConnectionState next)
    {
        lock (_sync)
        {
            if (_current == next) return false;

            var allowed = (_current, next) switch
            {
                (ConnectionState.Disconnected or ConnectionState.RetryWaiting, ConnectionState.Connecting) => true,
                (ConnectionState.Connecting, ConnectionState.PublishingBirth) => true,
                (ConnectionState.PublishingBirth, ConnectionState.Online) => true,
                (ConnectionState.Connecting or ConnectionState.PublishingBirth or ConnectionState.Online,
                    ConnectionState.Disconnected) => true,
                (ConnectionState.Disconnected or ConnectionState.Connecting or ConnectionState.PublishingBirth or ConnectionState.Online,
                    ConnectionState.Stopping) => true,
                (ConnectionState.Disconnected, ConnectionState.RetryWaiting) => true,
                (ConnectionState.RetryWaiting, ConnectionState.Stopping) => true,
                (ConnectionState.Stopping, ConnectionState.Stopped) => true,
                _ => false
            };

            if (!allowed) return false;

            _log.LogDebug("State: {Previous} -> {Next}", _current, next);
            _current = next;
            return true;
        }
    }
}
