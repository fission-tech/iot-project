using EdgeGateway.Configuration;
using IoT.Contracts;

// Configuration layer: the device registry. It says which devices exist and how to read them;
// whether a device is online is known only to data acquisition, which reports it as Sparkplug births/deaths.
var builder = WebApplication.CreateBuilder(args);
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
builder.Services.AddSingleton(_ => new DeviceStore(databasePath));

var app = builder.Build();
var store = app.Services.GetRequiredService<DeviceStore>();

app.MapGet("/", () => "Edge gateway configuration service.");
app.MapGet("/health", () => "ok");
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

