using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Demo.Control;
using EdgeGateway.Gateway.Acquisition;
using EdgeGateway.Gateway.Configuration;
using EdgeGateway.Gateway.Storage;
using FluentModbus;
using IoT.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using EdgeGateway.Gateway.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

internal static class ErrorReportingChecks
{
    private sealed record Entry(LogLevel Level, string Message, Exception? Exception,
        Dictionary<string, object?> Properties);

    private sealed class Capture : ILoggerProvider
    {
        public ConcurrentQueue<Entry> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturedLogger(this);
        public void Dispose() { }
        private sealed class CapturedLogger(Capture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
                => owner.Entries.Enqueue(new(level, format(state, error), error,
                    state is IEnumerable<KeyValuePair<string, object?>> values ? values.ToDictionary(item => item.Key, item => item.Value) : []));
        }
        public int Count(string text) => Entries.Count(entry => entry.Message.Contains(text, StringComparison.Ordinal));
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Wait(Func<bool> condition, Task running, string message, int seconds = 12)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (running.IsCompleted) { await running; throw new Exception("Worker stopped: " + message); }
            Check(DateTime.UtcNow < until, message);
            await Task.Delay(30);
        }
    }
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public static async Task RunAsync()
    {
        FormatAndActivity();
        var directory = Path.Combine(Path.GetTempPath(), "reporting-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await InvalidResponseAsync(directory);
            await ModbusAsync(directory);
            await RunConfigurationAsync();
            Console.WriteLine("PASS: Concise reporting: actual Modbus frame errors, all standard/unknown codes, transport failures, invalid values, suppression/recovery, console output and Demo compatibility.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    public static async Task RunConfigurationAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "config-reporting-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var capture = new Capture();
        using var logs = LoggerFactory.Create(builder => builder.AddProvider(capture));
        using var server = new ModbusTcpServer();
        server.AddUnit(1);
        server.GetHoldingRegisters(1).SetBigEndian<short>(0, 45);
        var port = FreePort();
        server.Start(new IPEndPoint(IPAddress.Loopback, port));
        var device = new DeviceDefinition("config-meter", true, DeviceDefinition.ModbusTcp, "127.0.0.1", port, 1, 1,
            null, "", [new("Temperature", "holding", 0, .1, "C")], "tcp");
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        await using var api = builder.Build();
        var url = $"http://127.0.0.1:{FreePort()}";
        api.Urls.Add(url);
        var available = false;
        var enabled = true;
        api.MapGet("/config", () => Volatile.Read(ref available)
            ? Results.Json(new GatewayConfiguration(enabled ? 7 : 8, [device with { Enabled = enabled }]), EdgeTopics.Json)
            : Results.StatusCode(503));
        await api.StartAsync();
        var settings = new DaqSettings { ConfigUrl = url, ConfigRefreshSeconds = 1, StateDirectory = directory };
        const string warning = "Configuration unavailable. Retrying on the next refresh.";
        const string recovery = "Configuration service reachable again.";
        try
        {
            using (var config = new ConfigClient(settings))
            using (var box = new Outbox(":memory:"))
            using (var stop = new CancellationTokenSource())
            {
                var service = new AcquisitionService(settings, config, box, loggers: logs);
                var running = service.RunAsync(stop.Token);
                try
                {
                    await Wait(() => capture.Count(warning) == 1, running, "Initial unavailable warning missing.");
                    await Task.Delay(2200);
                    Check(capture.Count(warning) == 1 && box.CountUnsent() == 0, "Warning repeated or polling started without configuration.");
                    Volatile.Write(ref available, true);
                    await Wait(() => box.CountUnsent() >= 2, running, "Recovery did not load configuration/start polling.");
                    Check(capture.Count(recovery) == 1 && capture.Count("Configuration v7: 1 device(s).") == 1
                        && capture.Count("Polling config-meter at") == 1, "Selected load/polling/recovery logs missing or duplicated.");
                    Volatile.Write(ref available, false);
                    await Wait(() => !service.ConfigState.Reachable && capture.Count(warning) == 2, running, "Second outage missing.");
                    var count = box.CountUnsent();
                    await Wait(() => box.CountUnsent() >= count + 2, running, "Configuration outage stopped current polling.");
                    Check(capture.Count(warning) == 2 && service.ConfigState.Version == 7, "Outage warning repeated or current configuration changed.");
                }
                finally { stop.Cancel(); try { await running; } catch (OperationCanceledException) { } }
            }
            using (var config = new ConfigClient(settings))
            using (var box = new Outbox(":memory:"))
            using (var stop = new CancellationTokenSource())
            {
                var service = new AcquisitionService(settings, config, box, loggers: logs);
                var running = service.RunAsync(stop.Token);
                try
                {
                    await Wait(() => box.CountUnsent() >= 2, running, "Cached restart did not resume polling.");
                    Check(!service.ConfigState.Reachable && capture.Count(warning) == 3 && capture.Count(recovery) == 1,
                        "Cache was mistaken for service recovery or warning repeated.");
                    Volatile.Write(ref available, true);
                    await Wait(() => service.ConfigState.Reachable, running, "Cached startup did not report actual service recovery.");
                    Check(capture.Count(recovery) == 2, "Actual recovery not logged once.");
                    Volatile.Write(ref enabled, false);
                    await Wait(() => box.PeekUnsent(100).Any(row => row.Kind == "DDEATH"), running, "Disable stopped emitting DDEATH.");
                    Check(capture.Count("config-meter offline (not enabled in configuration).") == 1,
                        "Selected device-disabled log missing.");
                    Check(capture.Entries.Where(entry => entry.Level == LogLevel.Warning).All(entry => entry.Message == warning),
                        "Detailed configuration warnings returned.");
                }
                finally { stop.Cancel(); try { await running; } catch (OperationCanceledException) { } }
            }
            var activity = new ActivityLog();
            activity.FromGateway("2026-11-03 14:00:00 [WARN] " + warning);
            activity.FromGateway("2026-11-03 14:00:01 [INFO] " + recovery);
            Check(activity.Recent().Count(entry => entry.Kind == "config") == 2, "Demo lost generic configuration logs.");
            Console.WriteLine("PASS: Generic configuration warning once per outage, recovery once, selected INFO events, polling retained through outage, cached restart without false recovery, disable/DDEATH and Demo Activity.");
        }
        finally { await api.StopAsync(); Directory.Delete(directory, true); }
    }

    public static void FormatAndActivity()
    {
        string Render(string message, LogLevel level = LogLevel.Warning, Exception? error = null)
        {
            using var writer = new StringWriter();
            new GatewayConsoleFormatter().Write(new LogEntry<string>(level, "GatewayRunner", new EventId(99),
                message, error, (text, _) => text), null, writer);
            return writer.ToString();
        }
        var recovery = "device-01: Pressure reading recovered. Quality = good.";
        var output = Render(recovery, LogLevel.Information);
        Check(output.Contains("[INFO] " + recovery) && !output.Contains("+05:00") && !output.Contains('\u2014')
            && !output.Contains("GatewayRunner") && !output.Contains("[99]"), "Recovery format changed.");
        foreach (var level in new[] { LogLevel.Trace, LogLevel.Debug, LogLevel.Warning, LogLevel.Error, LogLevel.Critical })
            Check(Render("test", level).Contains(level == LogLevel.Warning ? "[WARN] test" : $"[{level.ToString().ToUpperInvariant()}] test"),
                "Log level label changed: " + level);
        Check(Render("") == "", "Empty log was printed.");
        Check(Render("MQTT Connection Lost\nBroker: localhost\r\nError: test").Trim().Split('\n').Length == 1,
            "Routine messages were split across lines.");
        var warning = "Device device-01: holding 200+1 [Pressure]: IllegalDataAddress (0x02): Device rejected the requested register range.";
        var activity = new ActivityLog();
        void Feed(string block) { using var lines = new StringReader(block); while (lines.ReadLine() is { } line) activity.FromGateway(line); }
        Feed(Render(warning));
        Feed(output);
        Feed(Render(warning));
        Check(activity.Recent().Length == 3 && activity.Recent().Count(entry => entry.Kind == "fail") == 2,
            "Error/recovery/repeated error lost from Activity.");
        Check(activity.Recent().All(entry => entry.Text.Contains("Pressure")), "Activity lost metric context.");
        Feed(Render("Configuration unavailable. Retrying on the next refresh."));
        Feed(Render("MQTT connected. Broker: 127.0.0.1:8883", LogLevel.Information));
        Check(activity.Recent().Any(entry => entry.Kind == "config") && activity.Recent().Any(entry => entry.Kind == "broker"),
            "Formatted config/MQTT records lost.");
        var fatal = Render("Gateway stopped unexpectedly.", LogLevel.Error, new Exception("test unexpected failure"));
        Check(fatal.Contains("[ERROR]") && fatal.Contains("test unexpected failure"), "Unexpected failure lost its exception.");
    }

    private static async Task ModbusAsync(string directory)
    {
        using var capture = new Capture();
        using var logs = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(capture));
        using var server = new ModbusTcpServer();
        server.AddUnit(1);
        server.GetHoldingRegisters(1).SetBigEndian<short>(0, 45);
        server.GetHoldingRegisters(1).SetBigEndian<ushort>(200, 120);
        server.GetHoldingRegisters(1).SetBigEndian<float>(400, 230);
        var code = (int)ModbusExceptionCode.IllegalDataAddress;
        server.RequestValidator = (_, _, address, _) => address >= 200 ? (ModbusExceptionCode)Volatile.Read(ref code) : ModbusExceptionCode.OK;
        var port = FreePort();
        server.Start(new IPEndPoint(IPAddress.Loopback, port));
        var device = new DeviceDefinition("device-01", true, DeviceDefinition.ModbusTcp, "127.0.0.1", port, 1, 1,
            null, "Reporting test", [new("Temperature", "holding", 0, .1, "C", "int16"),
                new("Pressure", "holding", 200, 1, "kPa", "uint16"), new("Voltage", "holding", 400, 1, "V", "float32")], "tcp");
        using var box = new Outbox(":memory:");
        using var stop = new CancellationTokenSource();
        var poller = new ModbusPoller(device, new DaqSettings { StateDirectory = directory }, box, logs.CreateLogger<ModbusPoller>());
        var polling = poller.RunAsync(stop.Token);
        DeviceReading? Latest() => box.PeekUnsent(1000).LastOrDefault(row => row.Reading is not null)?.Reading;
        async Task Next(Action change)
        {
            var seq = Latest()!.DeviceSeq;
            change();
            await Wait(() => Latest()!.DeviceSeq > seq + 1, polling, "Next reporting sample missing.");
        }
        try
        {
            await Wait(() => Latest()?.DeviceSeq >= 3, polling, "Initial reporting sample missing.");
            Check(poller.Status.LastError!.Contains("Pressure") && poller.Status.LastError.Contains("Voltage")
                && Latest()!.Metrics["Temperature"].Number == 4.5 && poller.Status.Failures == 0,
                "Multiple errors lost or partial values/lifecycle changed.");
            Check(capture.Count("Device device-01:") == 2, "Unchanged errors logged on every poll.");
            Check(capture.Entries.Where(entry => entry.Message.Contains("Device device-01:")).All(entry =>
                entry.Properties.ContainsKey("DeviceId") && entry.Properties.ContainsKey("Reason")),
                "ILogger structured context missing.");
            await Task.Delay(2200);
            Check(capture.Count("Device device-01:") == 2, "Unchanged errors were repeated.");
            foreach (var value in new[] { 1, 3, 4, 5, 6, 8, 10, 11, 127 })
            {
                await Next(() => Volatile.Write(ref code, value));
                Check(poller.Status.LastError!.Contains($"0x{value:X2}"), "Actual Modbus code not reported: " + value);
                Check(Latest()!.Metrics["Temperature"].Number == 4.5, "Failure discarded successful earlier frame.");
                Check(capture.Entries.Any(entry => entry.Message.Contains($"0x{value:X2}")), "Changed error code was suppressed.");
            }
            Check(poller.Status.LastError!.Contains("unrecognised") && !poller.Status.LastError.Contains("IllegalDataAddress"),
                "Unknown Modbus exception was falsely classified.");
            await Next(() => Volatile.Write(ref code, 0));
            Check(capture.Count("device-01: Pressure reading recovered. Quality = good.") == 1
                && Latest()!.Metrics.Values.All(metric => metric.IsGood), "Metric recovery missing or quality changed.");
            var before = capture.Count("Device device-01:");
            await Next(() => Volatile.Write(ref code, 2));
            Check(capture.Count("Device device-01:") == before + 2, "Same error after recovery was suppressed.");
            await Next(() => Volatile.Write(ref code, 0));
            foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                await Next(() => server.GetHoldingRegisters(1).SetBigEndian<float>(400, invalid));
                Check(poller.Status is { State: "failing", Failures: 0 } && poller.Status.LastError!.Contains("Voltage")
                    && poller.Status.LastError.Contains("InvalidValue") && Latest()!.Metrics["Pressure"].IsGood
                    && Latest()!.Metrics["Voltage"] is { Number: null, Quality: MetricQuality.Bad }, "Invalid decoded value not reported accurately.");
            }
            await Next(() => server.GetHoldingRegisters(1).SetBigEndian<float>(400, 230));
            Check(poller.Status.LastError is null && poller.Status.State == "polling", "Recovered issues remain in status.");
            Check(!box.PeekUnsent(1000).Any(row => row.Kind == "DDEATH"), "Metric failures caused false offline.");
        }
        finally { stop.Cancel(); await polling; }

        var scaled = device with { Id = "scaled", Metrics = device.Metrics.Select(metric =>
            metric.Name == "Pressure" ? metric with { Scale = double.MaxValue } : metric).ToArray() };
        using var scaledStop = new CancellationTokenSource();
        using var scaledBox = new Outbox(":memory:");
        var scaledPoller = new ModbusPoller(scaled, new DaqSettings { StateDirectory = directory }, scaledBox, logs.CreateLogger<ModbusPoller>());
        var scaledTask = scaledPoller.RunAsync(scaledStop.Token);
        try
        {
            await Wait(() => scaledBox.PeekUnsent(10).Length >= 2, scaledTask, "Scaled overflow sample missing.");
            var sample = scaledBox.PeekUnsent(10).Last().Reading!;
            Check(sample.Metrics["Pressure"] is { Number: null, Quality: MetricQuality.Bad }
                && sample.Metrics["Temperature"].Number == 4.5 && scaledPoller.Status.Failures == 0
                && scaledPoller.Status.LastError!.Contains("value is not finite"), "Scaled overflow lost isolation or diagnostics.");
        }
        finally { scaledStop.Cancel(); await scaledTask; }

        var refused = device with { Id = "refused", Port = FreePort() };
        using var disconnected = new CancellationTokenSource();
        using var offlineBox = new Outbox(":memory:");
        var offlinePoller = new ModbusPoller(refused, new DaqSettings { StateDirectory = directory }, offlineBox, logs.CreateLogger<ModbusPoller>());
        var offlineTask = offlinePoller.RunAsync(disconnected.Token);
        try
        {
            await Wait(() => offlinePoller.Status.State == "offline", offlineTask, "Refused connection not exercised.");
            Check(capture.Entries.Any(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("refused")
                    && (entry.Message.Contains("ConnectionRefused") || entry.Message.Contains("Connection failed (")))
                && offlinePoller.Status.Failures >= 3 && offlineBox.PeekUnsent(100).Length == 0,
                "Connection failure reason or lifecycle changed: " + offlinePoller.Status.LastError);
        }
        finally { disconnected.Cancel(); await offlineTask; }
        Check(capture.Entries.All(entry => !entry.Message.Contains('\u2014')), "Em dash in a gateway log.");
    }

    private static async Task InvalidResponseAsync(string directory)
    {
        using var capture = new Capture();
        using var logs = LoggerFactory.Create(builder => builder.AddProvider(capture));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(stop.Token);
            using var stream = peer.GetStream();
            var request = new byte[12];
            await stream.ReadExactlyAsync(request, stop.Token);
            // Valid-length response with a deliberately wrong MBAP protocol identifier.
            await stream.WriteAsync(new byte[] { request[0], request[1], 0, 1, 0, 5, 1, 3, 2, 0, 45 }, stop.Token);
        });
        var device = new DeviceDefinition("invalid-response", true, DeviceDefinition.ModbusTcp, "127.0.0.1",
            ((IPEndPoint)listener.LocalEndpoint).Port, 1, 1, null, "Malformed protocol test",
            [new("Temperature", "holding", 0, .1, "C", "int16")], "tcp");
        using var box = new Outbox(":memory:");
        var poller = new ModbusPoller(device, new DaqSettings { StateDirectory = directory }, box, logs.CreateLogger<ModbusPoller>());
        var polling = poller.RunAsync(stop.Token);
        try
        {
            await Wait(() => poller.Status.LastError is not null, polling, "Invalid MBAP response not reported.");
            Check(poller.Status.LastError!.Contains("InvalidResponse") && !poller.Status.LastError.Contains("0xFFFFFFFF") && capture.Count("InvalidResponse") == 1,
                "Protocol failure was reported as an invented device exception code: " + poller.Status.LastError);
        }
        finally { stop.Cancel(); await Task.WhenAll(polling, server); }
    }

}
