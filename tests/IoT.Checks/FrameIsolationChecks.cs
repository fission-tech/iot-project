using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;
using EdgeGateway.Gateway.Acquisition;
using EdgeGateway.Gateway.Configuration;
using EdgeGateway.Gateway.Sparkplug;
using EdgeGateway.Gateway.Storage;
using FluentModbus;
using Google.Protobuf;
using IoT.Contracts;
using Microsoft.Data.Sqlite;

internal static class FrameIsolationChecks
{
    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new Exception(reason);
    }

    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "frame-isolation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await CheckCleanCloseAsync(directory);
            await CheckIsolationAsync(directory);
            await CheckAllInvalidAtStartupAsync(directory);
            await CheckUnavailableTargetAsync(directory, ModbusExceptionCode.GatewayPathUnavailable);
            await CheckUnavailableTargetAsync(directory, ModbusExceptionCode.GatewayTargetDeviceFailedToRespond);
            Console.WriteLine("PASS: Real Modbus frame isolation, mixed areas/types, all-invalid target responses, transport timeout, recovery, Sparkplug quality and partial-sample storage retry.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task CheckCleanCloseAsync(string directory)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var closeAt = -1;
        var connections = 0;
        // A real TCP FIN (zero-byte read), unlike the reset/timeout covered by the other server tests.
        var serving = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var peer = await listener.AcceptTcpClientAsync(stop.Token);
                    Interlocked.Increment(ref connections);
                    using var stream = peer.GetStream();
                    try
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            var header = new byte[7];
                            await stream.ReadExactlyAsync(header, stop.Token);
                            var request = new byte[5];
                            await stream.ReadExactlyAsync(request, stop.Token);
                            var address = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(1));
                            var count = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(3));
                            if (address == Volatile.Read(ref closeAt)) break;
                            var reply = new byte[9 + count * 2];
                            header.CopyTo(reply, 0);
                            BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(4), (ushort)(3 + count * 2));
                            reply[7] = 3; reply[8] = (byte)(count * 2);
                            for (var index = 0; index < count; index++)
                                BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(9 + index * 2), (ushort)((address + index) switch
                                { 0 => 45, 1 => 650, 200 => 120, 400 => 0x4366, _ => 0 }));
                            await stream.WriteAsync(reply, stop.Token);
                        }
                    }
                    catch (IOException) { } // The poller also closes connections during recovery/shutdown.
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        using var outbox = new Outbox(":memory:");
        var device = Device(port, "clean-close") with { Metrics = Device(port, "clean-close").Metrics.Take(4).ToArray() };
        var poller = new ModbusPoller(device, new DaqSettings { StateDirectory = directory }, outbox);
        var polling = poller.RunAsync(stop.Token);
        OutboxRow[] Rows() => outbox.PeekUnsent(100);
        DeviceReading Latest() => Rows().Last(row => row.Reading is not null).Reading!;
        try
        {
            await WaitAsync(() => Rows().Length > 0, polling, "Initial clean-close test reading missing.");
            var baseline = Latest().DeviceSeq;
            Volatile.Write(ref closeAt, 200);
            await WaitAsync(() => Latest().DeviceSeq >= baseline + 4 || Rows().Any(row => row.Kind == "DDEATH"),
                polling, "Clean-close cycles did not continue.");
            Check(Rows().Where(row => row.Reading?.DeviceSeq > baseline || row.Kind == "DDEATH")
                .All(row => row.Kind != "DDEATH" && row.Reading!.Metrics["Temperature"].Number == 4.5
                && row.Reading.Metrics["Humidity"].Number == 65
                && row.Reading.Metrics["Pressure"] is { Number: null, Quality: MetricQuality.Bad }
                && row.Reading.Metrics["Voltage"] is { Number: null, Quality: MetricQuality.Bad })
                && connections >= 4 && poller.Status.Failures == 0,
                "Clean TCP close discarded earlier valid frames, invented later values, or caused false DDEATH.");
            var seq = Latest().DeviceSeq;
            Volatile.Write(ref closeAt, 400);
            await WaitAsync(() => Latest().DeviceSeq > seq + 1, polling, "Last-frame clean close did not continue.");
            Check(Latest().Metrics["Pressure"].Number == 120 && Latest().Metrics["Voltage"].Quality == MetricQuality.Bad,
                "Last-frame clean close lost an earlier frame.");
            seq = Latest().DeviceSeq;
            Volatile.Write(ref closeAt, -1);
            await WaitAsync(() => Latest().DeviceSeq > seq + 1, polling, "Clean-close recovery did not resume.");
            Check(Latest().Metrics.Values.All(value => value.IsGood) && Latest().Metrics["Voltage"].Number == 230
                && Rows().Count(row => row.Kind == "DBIRTH") == 1, "Clean-close recovery lost values or caused unnecessary rebirth.");
            Volatile.Write(ref closeAt, 0);
            await WaitAsync(() => Rows().Any(row => row.Kind == "DDEATH"), polling, "Clean close before any target response hid an unavailable device.");
            Check(Rows().Count(row => row.Kind == "DDEATH") == 1, "Clean close duplicated device death.");
            Console.WriteLine("PASS: Actual TCP FIN: middle/last-frame partial values preserved, fresh connections, Good recovery without rebirth, first-frame closure still causes DDEATH.");
        }
        finally { stop.Cancel(); await Task.WhenAll(polling, serving); }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static DeviceDefinition Device(int port, string id) => new(id, true, DeviceDefinition.ModbusTcp,
        "127.0.0.1", port, 1, 1, null, "Frame isolation check",
        [new("Temperature", "holding", 0, .1, "C", "int16"), new("Humidity", "holding", 1, .1, "%", "uint16"),
         new("Pressure", "holding", 200, 1, "kPa", "uint16"), new("Voltage", "holding", 400, 1, "V", "float32"),
         new("Counter", "input", 0, 1, "", "uint32", "little"), new("Door", "coil", 0, 1, "", "bool"),
         new("Alarm", "discrete", 0, 1, "", "bool")], "test-interface");

    private static async Task WaitAsync(Func<bool> condition, Task polling, string reason, int seconds = 12)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Check(!polling.IsCompleted, "Polling exited unexpectedly: " + polling.Exception);
            await Task.Delay(25);
        }
        Check(condition(), reason);
    }

    private static async Task CheckIsolationAsync(string directory)
    {
        var port = FreePort();
        using var server = new ModbusTcpServer();
        server.AddUnit(1);
        server.GetHoldingRegisters(1).SetBigEndian<short>(0, 45);
        server.GetHoldingRegisters(1).SetBigEndian<ushort>(1, 650);
        server.GetHoldingRegisters(1).SetBigEndian<ushort>(200, 120);
        server.GetHoldingRegisters(1).SetBigEndian<float>(400, 230);
        server.GetInputRegisters(1).SetBigEndian<ushort>(0, 0x1170);
        server.GetInputRegisters(1).SetBigEndian<ushort>(1, 1);
        server.GetCoils(1)[0] = 1;
        server.GetDiscreteInputs(1)[0] = 1;
        // -1: healthy; -2: every frame rejected; -3: timeout on the middle frame.
        var failedStart = 200;
        var exception = ModbusExceptionCode.IllegalDataAddress;
        var laterRequests = 0;
        server.RequestValidator = (_, _, address, _) =>
        {
            if (address == 400) Interlocked.Increment(ref laterRequests);
            var failed = Volatile.Read(ref failedStart);
            if (failed == -3 && address == 200) Thread.Sleep(2600);
            return failed == -2 || failed == address ? exception : ModbusExceptionCode.OK;
        };
        server.Start(new IPEndPoint(IPAddress.Loopback, port));
        var database = Path.Combine(directory, "partial.db");
        using var outbox = new Outbox(database);
        using var stop = new CancellationTokenSource();
        var poller = new ModbusPoller(Device(port, "partial"), new DaqSettings { StateDirectory = directory }, outbox);
        var polling = poller.RunAsync(stop.Token);
        OutboxRow[] Rows() => outbox.PeekUnsent(1000);
        DeviceReading Latest() => Rows().Last(row => row.Reading is not null).Reading!;
        async Task<DeviceReading> NextAsync(Action change)
        {
            var seq = Latest().DeviceSeq;
            change();
            // Skip a possibly in-flight cycle, then inspect a full cycle with the new server behaviour.
            await WaitAsync(() => Latest().DeviceSeq > seq + 1, polling, "No new frame-isolation sample.");
            return Latest();
        }
        void Quality(DeviceReading reading, params string[] bad)
        {
            Check(reading.Metrics.Count == 7, "Partial reading lost metric definitions.");
            foreach (var (name, value) in reading.Metrics)
                Check(bad.Contains(name) ? value is { Number: null, Quality: MetricQuality.Bad } : value.IsGood,
                    $"Unexpected quality for {name} in sample {reading.DeviceSeq}: {value}");
        }
        try
        {
            await WaitAsync(() => Rows().Count(row => row.Reading is not null) >= 4, polling, "Partial cycles did not continue.");
            var partial = Latest();
            Quality(partial, "Pressure");
            Check(partial.Metrics["Temperature"].Number == 4.5 && partial.Metrics["Humidity"].Number == 65
                && partial.Metrics["Voltage"].Number == 230 && partial.Metrics["Counter"].Number == 70_000
                && partial.Metrics["Door"].Number == 1 && partial.Metrics["Alarm"].Number == 1,
                "Successful frames before/after rejection did not decode correctly.");
            Check(Rows().Count(row => row.Kind == "DBIRTH") == 1 && Rows().All(row => row.Kind != "DDEATH")
                && poller.Status is { State: "failing", Failures: 0 }, "A rejected frame caused a false device death or rebirth.");
            Console.WriteLine("PASS: Middle-frame IllegalDataAddress: other five frames remain readable for four cycles; no DDEATH.");

            var decoded = new SparkplugDecoder();
            decoded.Decode("spBv1.0/g/NBIRTH/n", SparkplugPayloads.NodeBirth(1).ToByteArray());
            var wire = decoded.Decode("spBv1.0/g/DBIRTH/n/partial", SparkplugPayloads.DeviceReading(1, partial).ToByteArray());
            Check(wire.Metrics["Pressure"] is { Number: null, Quality: MetricQuality.Bad }
                && wire.Metrics["Voltage"].Number == 230 && wire.RecordedAt?.ToUnixTimeMilliseconds() == partial.Timestamp.ToUnixTimeMilliseconds(),
                "Partial quality/value/source timestamp was lost in the Sparkplug payload.");

            Quality(await NextAsync(() => Volatile.Write(ref failedStart, 0)), "Temperature", "Humidity", "Counter", "Door", "Alarm");
            Quality(await NextAsync(() => Volatile.Write(ref failedStart, 400)), "Voltage");
            foreach (var code in new[] { ModbusExceptionCode.IllegalFunction, ModbusExceptionCode.IllegalDataValue,
                         ModbusExceptionCode.ServerDeviceFailure, ModbusExceptionCode.Acknowledge,
                         ModbusExceptionCode.ServerDeviceBusy, ModbusExceptionCode.MemoryParityError })
            {
                var sample = await NextAsync(() => { exception = code; Volatile.Write(ref failedStart, 200); });
                Quality(sample, "Pressure");
                Check(poller.Status.Failures == 0, $"{code} was incorrectly counted as no target response.");
            }
            var allBad = await NextAsync(() => Volatile.Write(ref failedStart, -2));
            Quality(allBad, "Temperature", "Humidity", "Pressure", "Voltage", "Counter", "Door", "Alarm");
            Check(Rows().All(row => row.Kind != "DDEATH"), "Valid target exceptions were treated as an unreachable device.");
            Quality(await NextAsync(() => Volatile.Write(ref failedStart, -1)));
            Check(Rows().Count(row => row.Kind == "DBIRTH") == 1, "Quality-only recovery unnecessarily changed the metric schema.");
            Quality(await NextAsync(() => server.GetHoldingRegisters(1).SetBigEndian<float>(400, float.NaN)), "Voltage");
            Quality(await NextAsync(() => server.GetHoldingRegisters(1).SetBigEndian<float>(400, 230)));
            Console.WriteLine("PASS: First/last frame, mixed areas, 32-bit decode, valid exception codes, all-Bad response and automatic quality recovery.");

            var before = Latest().DeviceSeq;
            var timeout = await NextAsync(() => Volatile.Write(ref failedStart, -3));
            Quality(timeout, "Pressure", "Voltage", "Counter", "Door", "Alarm");
            var later = Volatile.Read(ref laterRequests);
            await WaitAsync(() => Latest().DeviceSeq > timeout.DeviceSeq, polling, "Timeout sample not saved.");
            Check(Volatile.Read(ref laterRequests) == later, "The poller continued using a timed-out transport.");
            Check(Latest().DeviceSeq > before && Rows().All(row => row.Kind != "DDEATH"),
                "A mid-cycle transport error discarded earlier valid values or falsely marked the responsive device offline.");
            Quality(await NextAsync(() => Volatile.Write(ref failedStart, -1)));
            Console.WriteLine("PASS: Middle-frame timeout retains earlier values, skips later requests, and recovers on a fresh connection.");

            // A partial reading must obey the existing durable-save retry behaviour too.
            using var probe = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
            probe.Open();
            void Execute(string sql) { using var command = probe.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
            Quality(await NextAsync(() => Volatile.Write(ref failedStart, 200)), "Pressure");
            Execute("CREATE TRIGGER BlockSample BEFORE INSERT ON Outbox BEGIN SELECT RAISE(ABORT, 'test unavailable'); END;");
            var seqFile = Path.Combine(directory, "sequences", "partial.seq");
            ulong Allocated() => ulong.Parse(File.ReadAllText(seqFile));
            await WaitAsync(() => Allocated() > Latest().DeviceSeq, polling, "Partial storage failure was not exercised.");
            var pending = Allocated();
            await Task.Delay(3100);
            Check(Allocated() == pending && poller.Status.Failures == 0, "Storage retry changed sequence or counted as communication failure.");
            var changedAt = DateTimeOffset.UtcNow;
            server.GetHoldingRegisters(1).SetBigEndian<short>(0, 120);
            Execute("DROP TRIGGER BlockSample");
            await WaitAsync(() => Latest().DeviceSeq > pending, polling, "Partial storage retry did not recover.");
            var saved = Rows().Single(row => row.Reading?.DeviceSeq == pending).Reading!;
            Quality(saved, "Pressure");
            Check(saved.Metrics["Temperature"].Number == 4.5 && saved.Timestamp < changedAt
                && Latest().Metrics["Temperature"].Number == 12, "Partial storage retry replaced values/source time.");
            Console.WriteLine("PASS: Partial sample survives storage retry unchanged; polling then resumes with fresh values.");

            server.Stop();
            await WaitAsync(() => Rows().Any(row => row.Kind == "DDEATH"), polling, "Actual disconnection did not produce DDEATH.");
            Check(Rows().Count(row => row.Kind == "DDEATH") == 1 && poller.Status.Failures >= 3,
                "Actual disconnection lifecycle threshold changed.");
            server.Start(new IPEndPoint(IPAddress.Loopback, port));
            Volatile.Write(ref failedStart, -1);
            await WaitAsync(() => Rows().Count(row => row.Kind == "DBIRTH") == 2, polling, "Device reconnection did not restore DBIRTH.");
            Quality(Latest());
            Console.WriteLine("PASS: Actual device outage still sends one DDEATH after three failures; reconnection sends DBIRTH.");
        }
        finally { stop.Cancel(); await polling; }
    }

    private static async Task CheckAllInvalidAtStartupAsync(string directory)
    {
        using var server = new ModbusTcpServer();
        var port = FreePort();
        server.AddUnit(1);
        server.RequestValidator = (_, _, _, _) => ModbusExceptionCode.IllegalDataAddress;
        server.Start(new IPEndPoint(IPAddress.Loopback, port));
        using var outbox = new Outbox(":memory:");
        using var stop = new CancellationTokenSource();
        var poller = new ModbusPoller(Device(port, "all-invalid"), new DaqSettings { StateDirectory = directory }, outbox);
        var polling = poller.RunAsync(stop.Token);
        try
        {
            await WaitAsync(() => outbox.CountUnsent() >= 4, polling, "All-invalid device did not remain readable.");
            var rows = outbox.PeekUnsent(100);
            Check(rows[0].Kind == "DBIRTH" && rows.Skip(1).All(row => row.Kind == "DDATA")
                && rows.All(row => row.Reading!.Metrics.Values.All(value => value.Quality == MetricQuality.Bad))
                && poller.Status is { Failures: 0, LastSuccessAt: null },
                "A reachable device with invalid configuration was falsely offline or had invented good data.");
            Console.WriteLine("PASS: Cold startup with all frames rejected remains Online/Bad, with no invented good values or false DDEATH.");
        }
        finally { stop.Cancel(); await polling; }
    }

    private static async Task CheckUnavailableTargetAsync(string directory, ModbusExceptionCode code)
    {
        using var server = new ModbusTcpServer();
        var port = FreePort();
        var reject = false;
        server.AddUnit(1);
        server.RequestValidator = (_, _, _, _) => Volatile.Read(ref reject) ? code : ModbusExceptionCode.OK;
        server.Start(new IPEndPoint(IPAddress.Loopback, port));
        using var outbox = new Outbox(":memory:");
        using var stop = new CancellationTokenSource();
        var poller = new ModbusPoller(Device(port, code.ToString()), new DaqSettings { StateDirectory = directory }, outbox);
        var polling = poller.RunAsync(stop.Token);
        try
        {
            await WaitAsync(() => outbox.CountUnsent() > 0, polling, "Initial poll did not succeed.");
            Volatile.Write(ref reject, true);
            await WaitAsync(() => outbox.PeekUnsent(100).Any(row => row.Kind == "DDEATH"), polling, $"{code} hid an unavailable target.");
            Check(poller.Status.Failures >= 3, "Intermediary exception bypassed the failure threshold.");
            Console.WriteLine($"PASS: {code} is not mistaken for a reachable target; three unavailable cycles cause DDEATH.");
        }
        finally { stop.Cancel(); await polling; }
    }
}
