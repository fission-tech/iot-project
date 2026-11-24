using EdgeGateway.Configuration;
using IoT.Contracts;

// Configuration layer: the device registry. It says which devices exist and how to read them;
// whether a device is online is known only to data acquisition, which reports it as Sparkplug births/deaths.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Configuration.Sources.Clear();
builder.Configuration.SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables().AddCommandLine(args);
builder.WebHost.UseUrls(builder.Configuration["Configuration:Url"] ?? "http://127.0.0.1:5300");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy =
    EdgeTopics.Json.PropertyNamingPolicy);
var databasePath = builder.Configuration["Configuration:DatabasePath"]
    ?? Path.Combine(AppContext.BaseDirectory, "state", "configuration.db");
var username = builder.Configuration["Management:Username"] ?? "admin";
var password = builder.Configuration["Management:Password"] ?? "";
var authenticationRequired = builder.Configuration.GetValue<bool>("Management:RequireAuthentication");
if (username.Length == 0 || username.Contains(':'))
    throw new InvalidOperationException("Management requires a username without ':'.");
if (authenticationRequired && !System.Text.RegularExpressions.Regex.IsMatch(builder.Configuration["Gateway:Id"] ?? "", "^[A-Za-z0-9_-]+$"))
    throw new InvalidOperationException("Set Gateway:Id to this edge computer's unique ID (letters, digits, underscores or hyphens).");
BrokerProfile? seed = null;
if (builder.Configuration["SeedBroker:Host"] is { } brokerHost)
    seed = new BrokerProfile("local", brokerHost, builder.Configuration.GetValue("SeedBroker:Port", 8883),
        true, builder.Configuration["SeedBroker:CertificateLocation"] ?? "/certs/broker.crt", null, 60, 10, 2, 30);
builder.Services.AddSingleton(_ => DeploymentBootstrap.Open(databasePath, seed));

var app = builder.Build();
var store = app.Services.GetRequiredService<DeviceStore>();

app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    if (context.Request.Path != "/health" && (authenticationRequired || password.Length > 0) &&
        !ManagementAccess.Matches(context.Request.Headers.Authorization, username, password))
    {
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"Gateway management\", charset=\"UTF-8\"";
        context.Response.StatusCode = 401;
        return;
    }
    // Browser mutations must come from this management origin. CLI and gateway clients have no Origin header.
    if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) &&
        context.Request.Headers.Origin is { Count: > 0 } origin &&
        !string.Equals(origin.ToString(), $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = 403;
        return;
    }
    await next();
});
app.UseStaticFiles();
app.MapGet("/", () => Results.Redirect("/register"));
app.MapGet("/register", () => Results.File(Path.Combine(app.Environment.WebRootPath, "register.html"), "text/html"));
app.MapGet("/health", () => "ok");
app.MapGet("/device-status", () =>
{
    var gatewayId = builder.Configuration["Gateway:Id"];
    var statusPath = builder.Configuration["GatewayStatus:Path"] ?? Path.Combine(AppContext.BaseDirectory, "state", "gateway", "status.json");
    var status = GatewayRuntimeStatus.ReadFresh(statusPath, TimeSpan.FromSeconds(15));
    if (status is { } value && gatewayId is not null &&
        (!value.TryGetProperty("gatewayId", out var id) || id.GetString() != gatewayId)) status = null;
    var devices = status is { } fresh && fresh.TryGetProperty("devices", out var rows)
        && rows.ValueKind == System.Text.Json.JsonValueKind.Array
        ? rows.EnumerateArray().Select(row => new
        {
            deviceId = row.GetProperty("deviceId").GetString(), state = row.GetProperty("state").GetString()
        }).ToArray() : [];
    return Results.Ok(new { gatewayId, devices });
});
app.MapGet("/broker-certificate", () =>
{
    var path = builder.Configuration["Management:PublicCertificatePath"];
    return path is not null && File.Exists(path)
        ? Results.File(path, "application/x-pem-file", "broker.crt") : Results.NotFound();
});
app.MapGet("/config", (string? broker, string? gateway) => store.Snapshot(broker, gateway) is { } snapshot
    ? Results.Ok(snapshot)
    : Results.NotFound(new { message = $"Broker {broker} does not exist." }));
app.MapGet("/devices", () => store.All());
app.MapGet("/devices/{id}", (string id) => store.Find(id) is { } device ? Results.Ok(device) : Results.NotFound());
app.MapPost("/devices", (DeviceRegistration device) => Write(() => store.Save(device, create: true)
    ? Results.Created($"/devices/{device.Id}", device)
    : Results.Conflict(new { message = $"Device {device.Id} already exists." })));
app.MapPost("/devices/with-interface", (DeviceWithInterface request) => Write(() =>
    request.Device is null || request.NewInterface is null
        ? Results.BadRequest(new { message = "device and newInterface are required." })
        : store.Save(request.Device, create: true, request.NewInterface)
            ? Results.Created($"/devices/{request.Device.Id}", request.Device)
            : Results.Conflict(new { message = $"Device {request.Device.Id} already exists." })));
app.MapPut("/devices/{id}", (string id, DeviceRegistration device) => Write(() => device.Id != id
    ? Results.BadRequest(new { message = "The device ID in the body must match the URL." })
    : store.Save(device, create: false) ? Results.Ok(device) : Results.NotFound()));
app.MapDelete("/devices/{id}", (string id) => store.Delete(id) ? Results.NoContent() : Results.NotFound());

app.MapGet("/interfaces", () => store.AllInterfaces());
app.MapGet("/interfaces/{id}", (string id) => store.FindInterface(id) is { } connection ? Results.Ok(connection) : Results.NotFound());
app.MapPost("/interfaces", (ModbusInterface connection) => Write(() => store.SaveInterface(connection, create: true)
    ? Results.Created($"/interfaces/{connection.Id}", connection)
    : Results.Conflict(new { message = $"Interface {connection.Id} already exists." })));
app.MapPut("/interfaces/{id}", (string id, ModbusInterface connection) => Write(() => connection.Id != id
    ? Results.BadRequest(new { message = "The interface ID in the body must match the URL." })
    : store.SaveInterface(connection, create: false) ? Results.Ok(connection) : Results.NotFound()));
app.MapDelete("/interfaces/{id}", (string id) => Write(() => store.DeleteInterface(id) ? Results.NoContent() : Results.NotFound()));

// Broker connection settings. Gateways pick one with Mqtt:BrokerId and read it through GET /config?broker=.
app.MapGet("/brokers", () => store.AllBrokers());
app.MapGet("/brokers/{id}", (string id) => store.FindBroker(id) is { } broker ? Results.Ok(broker) : Results.NotFound());
app.MapPost("/brokers", (BrokerProfile broker) => Write(() => store.SaveBroker(broker, create: true)
    ? Results.Created($"/brokers/{broker.Id}", broker)
    : Results.Conflict(new { message = $"Broker {broker.Id} already exists." })));
app.MapPut("/brokers/{id}", (string id, BrokerProfile broker) => Write(() => broker.Id != id
    ? Results.BadRequest(new { message = "The broker ID in the body must match the URL." })
    : store.SaveBroker(broker, create: false) ? Results.Ok(broker) : Results.NotFound()));
app.MapDelete("/brokers/{id}", (string id) => Write(() => store.DeleteBroker(id) ? Results.NoContent() : Results.NotFound()));

// Which broker each gateway uses (gateway ID = its MQTT client ID). Unassigned gateways use their own Mqtt:BrokerId.
app.MapGet("/gateways", () => store.AllAssignments());
app.MapPut("/gateways/{id}", (string id, GatewayAssignment assignment) => Write(() =>
{
    if (assignment.GatewayId != id) return Results.BadRequest(new { message = "The gateway ID in the body must match the URL." });
    store.Assign(assignment);
    return Results.Ok(assignment);
}));
app.MapDelete("/gateways/{id}", (string id) => store.Unassign(id) ? Results.NoContent() : Results.NotFound());

Console.WriteLine($"Configuration service: {builder.Configuration["Configuration:Url"] ?? "http://127.0.0.1:5300"} ({store.All().Length} devices).");
app.Run();

static IResult Write(Func<IResult> action)
{
    try { return action(); }
    catch (ConfigurationConflictException error) { return Results.Conflict(new { message = error.Message }); }
    catch (ArgumentException error) { return Results.BadRequest(new { message = error.Message }); }
}

