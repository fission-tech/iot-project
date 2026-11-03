using System.Diagnostics;
using System.Globalization;

namespace Demo.Control;

// One demo component run as a child process, with its console output kept for the page and a log file.
public sealed class ManagedProcess(string name, string workspace, string logDirectory)
{
    private readonly object _gate = new();
    private readonly LinkedList<string> _lines = new();
    private Process? _process;
    private StreamWriter? _log;

    public event Action<string>? Line;
    public bool Running
    {
        get { lock (_gate) return _process is { HasExited: false }; }
    }

    public void Start(IEnumerable<string> arguments)
    {
        lock (_gate)
        {
            if (_process is { HasExited: false }) return;
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = workspace,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            var sourceRoot = name.StartsWith("EdgeGateway.") ? Path.Combine(workspace, "src", "EdgeGateway") : Path.Combine(workspace, "src");
            start.ArgumentList.Add(Path.Combine(sourceRoot, name, "bin", "Debug", "net8.0", name + ".dll"));
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            Directory.CreateDirectory(logDirectory);
            _log?.Dispose();
            _log = new StreamWriter(Path.Combine(logDirectory, name + ".log"), append: true) { AutoFlush = true };
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) Append(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Append("ERR " + e.Data); };
            process.Exited += (_, _) => Append($"[demo] {name} exited.");
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            Append($"[demo] {name} started.");
        }
    }

    // Kill without warning, as a crash or power loss would.
    public void Crash()
    {
        Process? process;
        lock (_gate) process = _process;
        if (process is { HasExited: false })
        {
            Append($"[demo] {name} crashed (killed).");
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
    }

    // Ask the component to shut down cleanly; kill it if it does not exit in time.
    public async Task StopAsync(bool acceptsStopCommand)
    {
        Process? process;
        lock (_gate) process = _process;
        if (process is not { HasExited: false }) return;
        if (!acceptsStopCommand) { Crash(); return; }
        Send("stop");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { Crash(); }
    }

    public void Send(string command)
    {
        lock (_gate)
        {
            if (_process is not { HasExited: false }) return;
            try
            {
                _process.StandardInput.WriteLine(command);
                _process.StandardInput.Flush();
            }
            catch (IOException) { }
        }
    }

    public string[] Tail(int count)
    {
        lock (_lines) return _lines.Skip(Math.Max(0, _lines.Count - count)).ToArray();
    }

    private void Append(string line)
    {
        var alreadyDated = line.Length >= 19 && DateTime.TryParseExact(line.AsSpan(0, 19),
            "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        var stamped = alreadyDated ? line : $"{DateTime.Now:HH:mm:ss} {line}";
        lock (_lines)
        {
            _lines.AddLast(stamped);
            while (_lines.Count > 400) _lines.RemoveFirst();
        }
        try { _log?.WriteLine(stamped); } catch (ObjectDisposedException) { }
        Line?.Invoke(line);
    }
}
