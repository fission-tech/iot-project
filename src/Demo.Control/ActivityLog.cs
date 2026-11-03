using System.Text.RegularExpressions;

namespace Demo.Control;

public sealed record ActivityEntry(long Id, DateTimeOffset At, string Source, string Kind, string Text);

// A single timeline of everything that changes: component actions, registry edits, configuration loads,
// device online/offline/failing, births and deaths, broker connection and replay. Readings themselves are
// not repeated here; they are on the wire feed.
public sealed partial class ActivityLog
{
    private readonly LinkedList<ActivityEntry> _entries = new();
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
        if (FormattedLine().Match(line) is { Success: true } formatted) line = formatted.Groups[1].Value;
        if (line.StartsWith("Gateway stopped unexpectedly.", StringComparison.Ordinal))
        {
            Add("gateway", "fail", line);
            return;
        }
        if (line.StartsWith("Device ", StringComparison.Ordinal))
        {
            Add("gateway", "fail", line);
            return;
        }
        if (Recovery().IsMatch(line))
        {
            Add("gateway", "online", line);
            return;
        }
        if (line.StartsWith("MQTT connected.", StringComparison.Ordinal) ||
            line.StartsWith("MQTT connection restored.", StringComparison.Ordinal) ||
            line.StartsWith("MQTT Connection Lost", StringComparison.Ordinal))
        {
            Add("gateway", "broker", line);
            return;
        }
        if (ConfigLoaded().Match(line) is { Success: true } config)
            Add("gateway", "config", $"Loaded configuration v{config.Groups[1].Value} ({config.Groups[2].Value} device(s)).");
        else if (line.StartsWith("Configuration unavailable.", StringComparison.Ordinal) ||
                 line.StartsWith("Configuration service reachable again", StringComparison.Ordinal))
            Add("gateway", "config", line);
        else if (PollingStarted().Match(line) is { Success: true } polling)
            Add("gateway", "device", $"{polling.Groups[1].Value}: polling {polling.Groups[2].Value} in frames {polling.Groups[3].Value}.");
        else if (DeviceOnline().Match(line) is { Success: true } online)
        {
            Add("gateway", "online", $"{online.Groups[1].Value} online.");
        }
        else if (line.Contains(" offline after ", StringComparison.Ordinal) ||
                 line.EndsWith(" offline (not enabled in configuration).", StringComparison.Ordinal))
            Add("gateway", "offline", line);
        else if (Lifecycle().Match(line) is { Success: true } lifecycle)
            Add("gateway", lifecycle.Groups[1].Value, $"{lifecycle.Groups[1].Value} {lifecycle.Groups[2].Value}");
        else if (line.StartsWith("Published NBIRTH", StringComparison.Ordinal))
            Add("gateway", "NBIRTH", "Gateway session started (NBIRTH).");
        else if (line.StartsWith("Replaying ", StringComparison.Ordinal) || line.StartsWith("Rebirth requested", StringComparison.Ordinal) ||
                 line.StartsWith("Connection failed", StringComparison.Ordinal) ||
                 line.Contains("received. Shutting down", StringComparison.Ordinal) || line.StartsWith("Primary host", StringComparison.Ordinal))
            Add("gateway", "broker", line);
    }

    [GeneratedRegex(@"^Configuration v(\d+): (\d+) device")]
    private static partial Regex ConfigLoaded();
    [GeneratedRegex(@"^Polling (\S+) at (\S+) every \S+ in (.+)\.$")]
    private static partial Regex PollingStarted();
    [GeneratedRegex(@"^(\S+) online\.$")]
    private static partial Regex DeviceOnline();
    [GeneratedRegex(@"^(DBIRTH|DDEATH) (\S+)")]
    private static partial Regex Lifecycle();
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} \[(?:TRACE|DEBUG|INFO|WARN|ERROR|CRITICAL)\] (.*)$")]
    private static partial Regex FormattedLine();
    [GeneratedRegex(@"^\S+: .+ reading recovered\. Quality = good\.$")]
    private static partial Regex Recovery();
}
