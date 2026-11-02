using IoT.Contracts;

namespace EdgeGateway.Gateway.Connection;

// What the status file reports about the broker connection.
public sealed record BrokerState(
    BrokerProfile? Active, string Source, BrokerProfile? Trying, DateTimeOffset? TryingSince,
    BrokerProfile? Rejected, string? RejectedReason, DateTimeOffset? CertificateExpires);

// Decides which broker settings each connection attempt uses. New registry settings get a trial window; if they
// never connect while the fallback (last working, else appsettings) does, they are rejected until they change again.
// If neither connects after the window, the broker itself may be down, so both are tried in turn and the first
// to connect wins: an outage at the moment of a change never rejects good settings.
public sealed class BrokerSelector(BrokerProfiles profiles, TimeSpan trialWindow)
{
    private readonly object _gate = new();
    private BrokerProfile? _candidate, _rejected;
    private DateTimeOffset _candidateSince;
    private string? _candidateError, _rejectedReason;
    private bool _alternate;

    public BrokerProfile Next(DateTimeOffset now)
    {
        var desired = profiles.Desired;
        var fallback = profiles.Fallback;
        lock (_gate)
        {
            ForgetStaleRejection(desired);
            if (desired is null || desired == fallback || desired == _rejected)
            {
                _candidate = null;
                return fallback;
            }
            if (_candidate != desired)
            {
                _candidate = desired;
                _candidateSince = now;
                _candidateError = null;
                _alternate = false;
                Console.WriteLine($"Trying broker settings {BrokerProfiles.Describe(desired)} for up to {trialWindow.TotalSeconds:0}s; " +
                    $"{BrokerProfiles.Describe(fallback)} stays the fallback.");
            }
            if (now - _candidateSince < trialWindow) return desired;
            _alternate = !_alternate;
            return _alternate ? fallback : desired;
        }
    }

    // A connection with these settings published its birth.
    public void Connected(BrokerProfile profile)
    {
        lock (_gate)
        {
            if (_candidate is { } candidate)
            {
                if (profile == candidate) Console.WriteLine($"New broker settings {BrokerProfiles.Describe(candidate)} work.");
                else
                {
                    _rejected = candidate;
                    _rejectedReason = _candidateError ?? $"no connection within {trialWindow.TotalSeconds:0}s";
                    Console.WriteLine($"Broker settings {BrokerProfiles.Describe(candidate)} rejected ({_rejectedReason}). " +
                        $"Restored {BrokerProfiles.Describe(profile)}.");
                }
                _candidate = null;
            }
        }
        profiles.MarkGood(profile);
    }

    public void Failed(BrokerProfile profile, string error)
    {
        lock (_gate) if (profile == _candidate) _candidateError = error;
    }

    // True when the registry asks for settings other than the active ones, and they were not already rejected.
    public bool ShouldSwitch(BrokerProfile? active)
    {
        var desired = profiles.Desired;
        lock (_gate)
        {
            ForgetStaleRejection(desired);
            return desired is not null && desired != active && desired != _rejected;
        }
    }

    // Once the registry moves on, a rejection is forgotten: saving those values again gets a fresh trial.
    private void ForgetStaleRejection(BrokerProfile? desired)
    {
        if (_rejected is not null && desired != _rejected) (_rejected, _rejectedReason) = (null, null);
    }

    public BrokerState State(BrokerProfile? active, DateTimeOffset? certificateExpires)
    {
        var source = active is null ? "none"
            : active == profiles.Desired ? "registry"
            : active == profiles.LastGood ? "last-good"
            : active == profiles.Seed ? "appsettings" : "previous";
        var desired = profiles.Desired;
        lock (_gate)
        {
            ForgetStaleRejection(desired);
            return new(active, source, _candidate, _candidate is null ? null : _candidateSince,
                _rejected, _rejectedReason, certificateExpires);
        }
    }
}
