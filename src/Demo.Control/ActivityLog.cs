using System.Text.RegularExpressions;

namespace Demo.Control;

public sealed record ActivityEntry(long Id, DateTimeOffset At, string Source, string Kind, string Text);

// A single timeline of everything that changes: component actions, registry edits, configuration loads,
// device online/offline/failing, births and deaths, broker connection and replay. Readings themselves are
// not repeated here; they are on the wire feed.
public sealed partial class ActivityLog
{
    private readonly LinkedList<ActivityEntry> _entries = new();
    private readonly Dictionary<string, string> _lastFailure = [];
    private long _nextId;

    public void Add(string source, string kind, string text)
    {
        lock (_entries)
        {
            _entries.AddFirst(new ActivityEntry(++_nextId, DateTimeOffset.UtcNow, source, kind, text));
            while (_entries.Count > 400) _entries.RemoveLast();
        }
    }

    public ActivityEntry[] Recent(int count = 200)
    {
        lock (_entries) return _entries.Take(count).ToArray();
    }

    // Turns notable gateway console lines into activity; routine "Polled"/"DDATA" lines are skipped.
    public void FromGateway(string line)
    {
        if (ConfigLoaded().Match(line) is { Success: true } config)
            Add("gateway", "config", $"Loaded configuration v{config.Groups[1].Value} ({config.Groups[2].Value} device(s)).");
        else if (line.StartsWith("Configuration service unreachable", StringComparison.Ordinal) ||
                 line.StartsWith("Configuration service reachable again", StringComparison.Ordinal))
            Add("gateway", "config", line);
        else if (PollingStarted().Match(line) is { Success: true } polling)
            Add("gateway", "device", $"{polling.Groups[1].Value}: polling {polling.Groups[2].Value} in frames {polling.Groups[3].Value}.");
        else if (PollFailed().Match(line) is { Success: true } failed)
        {
            // Report the first failure and any change of reason, not every repeat.
            var (device, reason) = (failed.Groups[1].Value, failed.Groups[2].Value);
            bool report;
            lock (_lastFailure)
            {
                report = !_lastFailure.TryGetValue(device, out var previous) || previous != reason;
                _lastFailure[device] = reason;
            }
            if (report) Add("gateway", "fail", $"{device}: read error: {reason}");
        }
        else if (DeviceOnline().Match(line) is { Success: true } online)
        {
            lock (_lastFailure) _lastFailure.Remove(online.Groups[1].Value);
            Add("gateway", "online", $"{online.Groups[1].Value} online.");
        }
        else if (line.Contains(" offline after ", StringComparison.Ordinal) || line.StartsWith("Stopped polling", StringComparison.Ordinal))
            Add("gateway", "offline", line);
        else if (Lifecycle().Match(line) is { Success: true } lifecycle)
            Add("gateway", lifecycle.Groups[1].Value, $"{lifecycle.Groups[1].Value} {lifecycle.Groups[2].Value}");
        else if (line.StartsWith("Published NBIRTH", StringComparison.Ordinal))
            Add("gateway", "NBIRTH", "Gateway session started (NBIRTH).");
        else if (line.StartsWith("Replaying ", StringComparison.Ordinal) || line.StartsWith("Rebirth requested", StringComparison.Ordinal) ||
                 line.StartsWith("Connection failed", StringComparison.Ordinal) || line == "Connected." ||
                 line.Contains("received. Shutting down", StringComparison.Ordinal) || line.StartsWith("Primary host", StringComparison.Ordinal))
            Add("gateway", "broker", line);
    }

    [GeneratedRegex(@"^Configuration v(\d+): (\d+) device")]
    private static partial Regex ConfigLoaded();
    [GeneratedRegex(@"^Polling (\S+) at (\S+) every \S+ in (.+)\.$")]
    private static partial Regex PollingStarted();
    [GeneratedRegex(@"^Poll (\S+): (.+); \d+ consecutive communication failure\(s\)\.$")]
    private static partial Regex PollFailed();
    [GeneratedRegex(@"^(\S+) online\.$")]
    private static partial Regex DeviceOnline();
    [GeneratedRegex(@"^(DBIRTH|DDEATH) (\S+)")]
    private static partial Regex Lifecycle();
}
