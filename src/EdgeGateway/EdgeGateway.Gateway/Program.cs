// Edge gateway: polls the Modbus devices listed by the configuration service, buffers every reading in
// a SQLite outbox and publishes it to Mosquitto as Sparkplug B.
using EdgeGateway.Gateway.Configuration;
using EdgeGateway.Gateway.Connection;
using EdgeGateway.Gateway.Acquisition;
using EdgeGateway.Gateway.Storage;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using EdgeGateway.Gateway.Diagnostics;

using var stop = new CancellationTokenSource();
ILoggerFactory? loggers = null;
ILogger log = NullLogger.Instance;
// Ctrl+C (SIGINT) and SIGTERM (docker stop, Kubernetes, systemd) both shut down cleanly: stop polling,
// let the current publish finish, send NDEATH and disconnect. If that hangs, exit after 15 seconds anyway;
// nothing is lost because the outbox is on disk and the broker publishes the NDEATH Will.
void RequestShutdown(PosixSignalContext context)
{
    context.Cancel = true;
    if (stop.IsCancellationRequested) return;
    log.LogInformation("{Signal} received. Shutting down.", context.Signal);
    stop.Cancel();
    _ = Task.Delay(TimeSpan.FromSeconds(15)).ContinueWith(_ => Environment.Exit(1), TaskScheduler.Default);
}
using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, RequestShutdown);
using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, RequestShutdown);
try
{
    var configuration = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json").AddCommandLine(args).Build();
    loggers = LoggerFactory.Create(builder => builder
        .AddConfiguration(configuration.GetSection("Logging"))
        .AddConsole(options => options.FormatterName = "gateway")
        .AddConsoleFormatter<GatewayConsoleFormatter, ConsoleFormatterOptions>());
    log = loggers.CreateLogger("Gateway");
    var settings = configuration.Get<GatewaySettings>()
        ?? throw new InvalidOperationException("Could not read appsettings.json.");
    using var outbox = new Outbox(settings.Gateway.DatabasePath);
    using var connection = new MqttConnection(settings.Mqtt, settings.Sparkplug, loggers.CreateLogger<MqttConnection>());
    // Broker settings come from the registry; appsettings remains the first-boot seed.
    var brokers = new BrokerProfiles(settings.Mqtt);
    using var config = new ConfigClient(settings.Daq, settings.Mqtt.BrokerId);
    var acquisition = new AcquisitionService(settings.Daq, config, outbox, brokers, loggers);
    var runner = new GatewayRunner(connection, outbox, settings, brokers, loggers.CreateLogger<GatewayRunner>());
    log.LogInformation("Outbox: {DatabasePath} ({Unsent} unsent). Type stop to exit.", settings.Gateway.DatabasePath, outbox.CountUnsent());
    var input = Task.Run(() =>
    {
        while (!stop.IsCancellationRequested)
        {
            var command = Console.ReadLine()?.Trim().ToLowerInvariant();
            // Without console input (for example under the launcher) run until stopped.
            if (command is null) stop.Token.WaitHandle.WaitOne();
            if (command is null or "stop") break;
            if (command != "") log.LogInformation("Commands: stop.");
        }
    });
    // Polling commits to the outbox and keeps going while the broker is down; publishing drains it.
    var receiving = acquisition.RunAsync(stop.Token);
    var running = runner.RunAsync(stop.Token);
    var reporting = WriteStatusAsync(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settings.Gateway.DatabasePath))!,
        "status.json"), stop.Token);
    try
    {
        var completed = await Task.WhenAny(input, running, receiving);
        stop.Cancel();
        await completed;
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested)
    {
        log.LogInformation("Stopping...");
    }
    finally
    {
        stop.Cancel();
        try
        {
            try { await Task.WhenAll(running, receiving, reporting); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        finally
        {
            using var disconnectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await connection.DisconnectAsync(disconnectTimeout.Token);
        }
    }
    log.LogInformation("Disconnected.");

    // A status snapshot for local tools such as the demo console; not part of the data path.
    async Task WriteStatusAsync(string path, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var json = JsonSerializer.Serialize(new
                {
                    updatedAt = DateTimeOffset.UtcNow, upstream = connection.State.ToString(),
                    hostOnline = runner.HostOnline,
                    unsent = outbox.CountUnsent(), bdSeq = connection.BirthDeathSequence,
                    lastPublished = runner.LastPublished,
                    // Which broker settings are in use, where they came from, any trial or rejection, certificate expiry.
                    broker = runner.BrokerState,
                    config = new { version = acquisition.ConfigState.Version, reachable = acquisition.ConfigState.Reachable },
                    devices = acquisition.Statuses()
                }, IoT.Contracts.EdgeTopics.Json);
                GatewayStatusFile.TryWrite(path, json);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            try { await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken); }
            catch (OperationCanceledException) { }
        }
    }
}
catch (Exception error)
{
    if (loggers is null) Console.Error.WriteLine($"[ERROR] Gateway startup failed: {error.Message}");
    else log.LogError(error, "Gateway stopped unexpectedly.");
    Environment.ExitCode = 1;
}
finally { loggers?.Dispose(); }
