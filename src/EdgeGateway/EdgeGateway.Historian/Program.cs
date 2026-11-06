// Historian: subscribes to the Sparkplug messages on the broker and stores every reading in TimescaleDB,
// where Grafana reads them. Settings: appsettings.json, then environment variables (Historian__ConnectionString),
// then the command line.
using System.Runtime.InteropServices;
using EdgeGateway.Historian;
using EdgeGateway.Historian.Dashboards;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using var stop = new CancellationTokenSource();
void RequestShutdown(PosixSignalContext context)
{
    context.Cancel = true;
    stop.Cancel();
}
using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, RequestShutdown);
using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, RequestShutdown);

var configuration = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true).AddEnvironmentVariables().AddCommandLine(args).Build();
using var loggers = LoggerFactory.Create(builder => builder
    .AddConfiguration(configuration.GetSection("Logging"))
    .AddSimpleConsole(options => { options.SingleLine = true; options.TimestampFormat = "yyyy-MM-dd HH:mm:ss "; }));
var log = loggers.CreateLogger("Historian");
try
{
    var settings = configuration.Get<HistorianSettings>() ?? new HistorianSettings();
    settings.Historian.Validate();
    await using var store = new HistoryStore(settings.Historian.ConnectionString);
    using var registry = settings.Historian.ConfigUrl is { Length: > 0 } url ? new RegistryClient(url) : null;
    var dashboards = settings.Historian.DashboardDirectory is { Length: > 0 } directory
        ? new DashboardGenerator(store, registry, Path.GetFullPath(directory), TimeSpan.FromSeconds(settings.Historian.DashboardRefreshSeconds),
            loggers.CreateLogger<DashboardGenerator>())
        : null;
    if (dashboards is null) log.LogInformation("Historian:DashboardDirectory is not set; device dashboards are not generated.");
    var service = new HistorianService(settings, store, loggers.CreateLogger<HistorianService>(), dashboards);
    // The demo console stops processes by writing "stop" to their input.
    _ = Task.Run(() =>
    {
        while (Console.ReadLine() is { } line)
            if (line.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase)) { stop.Cancel(); return; }
    });
    await service.RunAsync(stop.Token);
    log.LogInformation("Stopped.");
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { log.LogInformation("Stopped."); }
catch (Exception error)
{
    log.LogError(error, "Historian stopped unexpectedly.");
    Environment.ExitCode = 1;
}
