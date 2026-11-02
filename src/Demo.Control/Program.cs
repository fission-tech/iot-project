using Demo.Control;

// Demo console: controls and observes every component from one page. Listens on loopback only,
// because it can start and kill processes on this machine.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args, ContentRootPath = AppContext.BaseDirectory
});
builder.Configuration.Sources.Clear();
builder.Configuration.SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddCommandLine(args);
builder.WebHost.UseUrls(builder.Configuration["Control:Url"] ?? "http://127.0.0.1:5200");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddSingleton<ActivityLog>();
builder.Services.AddSingleton<ComponentManager>();
builder.Services.AddSingleton<ResilienceScenario>();
builder.Services.AddSingleton<SparkplugView>();
builder.Services.AddSingleton<WireMonitor>();
builder.Services.AddHostedService(services => services.GetRequiredService<WireMonitor>());
builder.Services.AddHttpClient("configuration", client => client.Timeout = TimeSpan.FromSeconds(2));

var app = builder.Build();
var components = app.Services.GetRequiredService<ComponentManager>();
app.Lifetime.ApplicationStopping.Register(components.Dispose);
_ = Task.Run(async () =>
{
    while (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
    {
        try { await components.RefreshContainersAsync(); } catch (Exception) { }
        await Task.Delay(TimeSpan.FromSeconds(2));
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/state", async (ResilienceScenario scenario, WireMonitor monitor) => new
{
    components = components.Describe(),
    gateway = components.ReadGatewayStatus(),
    monitorConnected = monitor.Connected("upstream"),
    configurationReachable = await components.ConfigurationReachableAsync(),
    monitors = new { upstream = monitor.Connected("upstream") },
    scenario = scenario.Snapshot()
});
app.MapGet("/api/wire/{broker}", (string broker, WireMonitor monitor) =>
    broker is "upstream" ? Results.Ok(monitor.Recent(broker)) : Results.NotFound());

// Pass-through to the configuration service (device registry), so devices can be managed from the page.
app.MapGet("/api/config/devices", (IHttpClientFactory factory) =>
    Forward(factory, HttpMethod.Get, "/devices"));
app.MapPost("/api/config/devices", async (HttpContext context, IHttpClientFactory factory) =>
    await Forward(factory, HttpMethod.Post, "/devices", await new StreamReader(context.Request.Body).ReadToEndAsync(), "Device added to the registry"));
app.MapPost("/api/config/devices/with-interface", async (HttpContext context, IHttpClientFactory factory) =>
    await Forward(factory, HttpMethod.Post, "/devices/with-interface", await new StreamReader(context.Request.Body).ReadToEndAsync(), "Device and interface added to the registry"));
app.MapPost("/api/config/devices/{id}/enabled/{enabled:bool}", async (string id, bool enabled, IHttpClientFactory factory) =>
{
    try
    {
        var client = factory.CreateClient("configuration");
        var device = await client.GetFromJsonAsync<IoT.Contracts.DeviceRegistration>($"{components.ConfigurationUrl}/devices/{Uri.EscapeDataString(id)}",
            IoT.Contracts.EdgeTopics.Json);
        if (device is null) return Results.NotFound();
        var response = await client.PutAsJsonAsync($"{components.ConfigurationUrl}/devices/{id}", device with { Enabled = enabled },
            IoT.Contracts.EdgeTopics.Json);
        return response.IsSuccessStatusCode
            ? Recorded(components.Activity, $"{id} {(enabled ? "enabled" : "disabled")} in the device registry.")
            : Results.Content(await response.Content.ReadAsStringAsync(), "application/json", statusCode: (int)response.StatusCode);
    }
    catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
    {
        return Results.BadRequest(new { message = "Configuration service is not reachable." });
    }
});
app.MapPut("/api/config/devices/{id}", async (string id, HttpContext context, IHttpClientFactory factory) =>
    await Forward(factory, HttpMethod.Put, $"/devices/{Uri.EscapeDataString(id)}",
        await new StreamReader(context.Request.Body).ReadToEndAsync(), $"{id}: registry entry updated"));
app.MapDelete("/api/config/devices/{id}", (string id, IHttpClientFactory factory) =>
    Forward(factory, HttpMethod.Delete, $"/devices/{Uri.EscapeDataString(id)}", null, $"{id}: removed from the registry"));

app.MapGet("/api/config/interfaces", (IHttpClientFactory factory) => Forward(factory, HttpMethod.Get, "/interfaces"));
app.MapPost("/api/config/interfaces", async (HttpContext context, IHttpClientFactory factory) =>
    await Forward(factory, HttpMethod.Post, "/interfaces", await new StreamReader(context.Request.Body).ReadToEndAsync(), "Interface added"));
app.MapPut("/api/config/interfaces/{id}", async (string id, HttpContext context, IHttpClientFactory factory) =>
    await Forward(factory, HttpMethod.Put, $"/interfaces/{Uri.EscapeDataString(id)}",
        await new StreamReader(context.Request.Body).ReadToEndAsync(), $"{id}: shared interface updated"));
app.MapDelete("/api/config/interfaces/{id}", (string id, IHttpClientFactory factory) =>
    Forward(factory, HttpMethod.Delete, $"/interfaces/{Uri.EscapeDataString(id)}", null, $"{id}: interface removed"));

async Task<IResult> Forward(IHttpClientFactory factory, HttpMethod method, string path, string? body = null, string? activity = null)
{
    try
    {
        using var request = new HttpRequestMessage(method, components.ConfigurationUrl + path);
        if (body is not null) request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var response = await factory.CreateClient("configuration").SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (activity is not null) components.Activity.Add("registry", response.IsSuccessStatusCode ? "registry" : "fail",
            response.IsSuccessStatusCode ? $"{activity}." : $"{activity} rejected: {text}");
        // A 204 must not carry a body, so pass it through as an empty success.
        return response.StatusCode == System.Net.HttpStatusCode.NoContent
            ? Results.Ok(new { message = "Done." })
            : Results.Content(text == "" ? "{}" : text, "application/json", statusCode: (int)response.StatusCode);
    }
    catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
    {
        return Results.BadRequest(new { message = "Configuration service is not reachable." });
    }
}
// Broker connection settings in the registry; the gateway follows the one named by its Mqtt:BrokerId.
app.MapGet("/api/config/brokers", (IHttpClientFactory factory) => Forward(factory, HttpMethod.Get, "/brokers"));
app.MapPost("/api/config/brokers", async (HttpContext context, IHttpClientFactory factory) =>
    await Forward(factory, HttpMethod.Post, "/brokers", await new StreamReader(context.Request.Body).ReadToEndAsync(), "Broker added"));
app.MapPut("/api/config/brokers/{id}", async (string id, HttpContext context, IHttpClientFactory factory) =>
    await Forward(factory, HttpMethod.Put, $"/brokers/{Uri.EscapeDataString(id)}",
        await new StreamReader(context.Request.Body).ReadToEndAsync(), $"{id}: broker settings updated"));
app.MapDelete("/api/config/brokers/{id}", (string id, IHttpClientFactory factory) =>
    Forward(factory, HttpMethod.Delete, $"/brokers/{Uri.EscapeDataString(id)}", null, $"{id}: broker removed"));
app.MapGet("/api/logs/{name}", (string name) => components.Tail(name, 150));
app.MapGet("/api/activity", () => components.Activity.Recent());

// What a Sparkplug consumer on Mosquitto sees, rebuilt by the console's own subscription (no backend).
app.MapGet("/api/view/gateways", (SparkplugView view) => view.Gateways());
app.MapGet("/api/view/devices", (SparkplugView view) => view.Devices());
app.MapGet("/api/view/summary", (SparkplugView view) => view.Summaries());
app.MapGet("/api/view/history/{id}", (string id, SparkplugView view) => view.History(id));

app.MapPost("/api/components/{name}/{action}", (string name, string action) =>
    Run(() => components.ActAsync(name, action)));
app.MapPost("/api/demo/start-all", () => Run(async () => { await components.StartAllAsync(); return "Everything started."; }));
app.MapPost("/api/demo/stop-all", () => Run(async () => { await components.StopAllAsync(); return "Everything stopped."; }));
app.MapPost("/api/demo/reset", (SparkplugView view) => Run(async () =>
{
    view.Clear();
    await components.ResetAsync();
    return "Fresh broker, simulators and state; everything started.";
}));
app.MapPost("/api/demo/resilience", (ResilienceScenario scenario) =>
    scenario.TryStart() ? Results.Accepted() : Results.Conflict(new { message = "Scenario already running." }));

Console.WriteLine($"Demo console: {builder.Configuration["Control:Url"] ?? "http://127.0.0.1:5200"}");
app.Run();

static async Task<IResult> Run(Func<Task<string>> action)
{
    try { return Results.Ok(new { message = await action() }); }
    catch (Exception error) when (error is ArgumentException or InvalidOperationException or TimeoutException)
    {
        return Results.BadRequest(new { message = error.Message });
    }
}

static IResult Recorded(ActivityLog activity, string message)
{
    activity.Add("registry", "registry", message);
    return Results.Ok(new { message });
}
