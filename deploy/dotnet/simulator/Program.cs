using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FluentModbus;
using IoT.Contracts;

using var stop = new CancellationTokenSource();
using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var server = new ModbusTcpServer();
server.AddUnit(1); server.AddUnit(2);
server.Start(new IPEndPoint(IPAddress.Any, 1502));
using var http = new HttpClient { BaseAddress = new Uri(Environment.GetEnvironmentVariable("CONFIGURATION_URL") ?? "http://configuration:5300"), Timeout = TimeSpan.FromSeconds(10) };
http.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:" + Environment.GetEnvironmentVariable("MANAGEMENT_PASSWORD"))));
var gatewayId = Environment.GetEnvironmentVariable("GATEWAY_ID") ?? "dotnet-staging-01";
DeviceRegistration.ValidateId(gatewayId, "Gateway ID");
var connection = new ModbusInterface("staging-simulator", "ModbusTcp", JsonSerializer.SerializeToElement(new { host = "modbus-sim", port = 1502 }));
var samples = new[] {
    new DeviceRegistration(gatewayId + "-pump", connection.Id, true, 1, 2, null, "Staging pump",
        [new("Temperature", "holding", 0, 0.1, "°C"), new("Pressure", "input", 0, 0.1, "bar"), new("Running", "coil", 0, 1, "", "bool")]),
    new DeviceRegistration(gatewayId + "-tank", connection.Id, true, 2, 2, null, "Staging tank",
        [new("Level", "holding", 0, 0.1, "%"), new("Alarm", "discrete", 0, 1, "", "bool")])
};
while (!stop.IsCancellationRequested)
{
    try
    {
        foreach (var device in samples)
        {
            using var exists = await http.GetAsync("/devices/" + Uri.EscapeDataString(device.Id), stop.Token);
            if (exists.StatusCode != HttpStatusCode.NotFound) { exists.EnsureSuccessStatusCode(); continue; }
            using var existingInterface = await http.GetAsync("/interfaces/" + connection.Id, stop.Token);
            using var response = existingInterface.StatusCode == HttpStatusCode.NotFound
                ? await http.PostAsJsonAsync("/devices/with-interface", new DeviceWithInterface(device, connection), stop.Token)
                : await http.PostAsJsonAsync("/devices", device, stop.Token);
            response.EnsureSuccessStatusCode();
        }
        break;
    }
    catch (HttpRequestException error) { Console.WriteLine("Simulator registration retry: " + error.Message); await Task.Delay(2000, stop.Token); }
}
Console.WriteLine("Modbus simulator listening on 1502 (units 1 and 2).");
try
{
    var tick = 0;
    while (!stop.IsCancellationRequested)
    {
        lock (server.Lock)
        {
            server.GetHoldingRegisters(1).SetBigEndian<short>(0, (short)(220 + tick % 30));
            server.GetInputRegisters(1).SetBigEndian<short>(0, (short)(40 + tick % 10));
            server.GetCoils(1)[0] = 1;
            server.GetHoldingRegisters(2).SetBigEndian<short>(0, (short)(600 + tick % 100));
            server.GetDiscreteInputs(2)[0] = (byte)(tick % 20 == 0 ? 1 : 0);
        }
        tick++;
        await Task.Delay(1000, stop.Token);
    }
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
