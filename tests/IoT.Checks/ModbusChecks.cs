using System.Net;
using System.Net.Sockets;
using EdgeGateway.Gateway.Sparkplug;
using EdgeGateway.Gateway.Storage;
using EdgeGateway.Configuration;
using EdgeGateway.Gateway.Acquisition;
using EdgeGateway.Gateway.Configuration;
using FluentModbus;
using Google.Protobuf;
using IoT.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

internal static class ModbusChecks
{
    public static async Task RunAsync()
    {
        CheckRegistry();
        await InterfaceChecks.RunAsync();
        CheckMetricChangeRebirth();
        CheckFrames();
        CheckQuality();
        await CheckPollerAsync();
        await CheckStorageRecoveryAsync();
        await CheckConfigurationReconciliationAsync();
        Console.WriteLine("PASS: Device registry, DBIRTH on metric change, read frames, int16/uint16/int32/uint32/float32 decoding with word order, Good/Bad/Stale quality through Sparkplug, Modbus polling into the outbox with request-time stamps, Bad samples and offline detection.");
    }

    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }

    private static DeviceDefinition Fridge(string id, int port, bool enabled = true) => new(id, enabled,
        DeviceDefinition.ModbusTcp, "127.0.0.1", port, 1, 1, 17, "check",
        [new("Temperature", "holding", 0, 0.1, "Â°C"), new("Humidity", "holding", 1, 0.1, "%"), new("GpioValue", "coil", 0, 1, "door")], $"tcp-{port}");

    private static DeviceRegistration Registration(DeviceDefinition device) => new(device.Id, device.InterfaceId,
        device.Enabled, device.UnitId, device.PollIntervalSeconds, device.GpioPin, device.Description, device.Metrics);

    private static void CheckRegistry()
    {
        using var store = new DeviceStore(":memory:");
        store.SaveInterface(new("tcp-5021", ModbusInterface.Tcp,
            System.Text.Json.JsonSerializer.SerializeToElement(new TcpInterfaceSettings("127.0.0.1", 5021), EdgeTopics.Json)), create: true);
        Check(store.Save(Registration(Fridge("fridge-a", 5021)), create: true), "Create failed.");
        Check(!store.Save(Registration(Fridge("fridge-a", 5021)), create: true), "Duplicate device was created.");
        var version = store.Snapshot().Version;
        Check(store.Save(Registration(Fridge("fridge-a", 5021, enabled: false)), create: false), "Update failed.");
        var disabled = store.Snapshot();
        Check(disabled.Version > version && disabled.Devices.Length == 0, "Disabling did not bump the version or hide the device.");
        Check(store.Delete("fridge-a") && store.Snapshot().Version > disabled.Version, "Delete did not bump the version.");
        foreach (var bad in new[]
        {
            Fridge("bad id", 5021), Fridge("fridge-b", 0), Fridge("fridge-c", 5021) with { Metrics = [] },
            Fridge("fridge-d", 5021) with { Metrics = [new("Temperature", "register", 0, 1, "")] },
            Fridge("fridge-e", 5021) with { Metrics = [new("X", "holding", 0, 1, ""), new("X", "holding", 1, 1, "")] }
        })
        {
            try { store.Save(Registration(bad), create: true); throw new Exception($"Invalid device {bad.Id} was accepted."); }
            catch (ArgumentException) { }
        }
    }

    private static void CheckMetricChangeRebirth()
    {
        using var outbox = new Outbox(":memory:");
        var first = SparkplugPayloads.FridgeReading("fridge-a", 1, DateTimeOffset.UtcNow, 4.5, 65, 17, 0);
        outbox.AcceptReading(first);
        outbox.AcceptReading(first with { DeviceSeq = 2 });
        var changed = first with { DeviceSeq = 3, Metrics = new(first.Metrics) { ["Pressure"] = new(Number: 1.2) } };
        outbox.AcceptReading(changed);
        Check(outbox.PeekUnsent(10).Select(row => row.Kind).SequenceEqual(["DBIRTH", "DDATA", "DBIRTH"]),
            "A changed metric list did not trigger a new DBIRTH.");
    }

    private static void CheckFrames()
    {
        MetricDefinition Metric(string name, string register, ushort address, string? type = null) =>
            new(name, register, address, 1, "", type);
        var frames = ModbusFrames.Plan(
        [
            Metric("A", "holding", 0, "int16"), Metric("B", "holding", 1, "uint16"),
            Metric("C", "holding", 10, "float32"), Metric("D", "holding", 12, "uint32"),
            Metric("Far", "holding", 200, "int16"), Metric("Door", "coil", 0)
        ]);
        Check(frames.Length == 3
              && frames.Any(frame => frame is { Register: "holding", Start: 0, Count: 14 } && frame.Metrics.Length == 4)
              && frames.Any(frame => frame is { Register: "holding", Start: 200, Count: 1 })
              && frames.Any(frame => frame is { Register: "coil", Start: 0, Count: 1 }),
            "Read frames were not planned as expected: " + string.Join("; ", frames.Select(f => $"{f.Register} {f.Start}+{f.Count}")));
        var floatBits = (uint)BitConverter.SingleToInt32Bits(85.5f);
        ushort[] big = [(ushort)(floatBits >> 16), (ushort)floatBits];
        Check(ModbusFrames.Decode(big, "float32", "big") == 85.5, "float32 (big word order) decoded wrongly.");
        Check(ModbusFrames.Decode([big[1], big[0]], "float32", "little") == 85.5, "float32 (little word order) decoded wrongly.");
        Check(ModbusFrames.Decode([0xFFFF, 0xFFFE], "int32", "big") == -2, "int32 sign handled wrongly.");
        Check(ModbusFrames.Decode([0x0001, 0x1170], "uint32", "big") == 70_000, "uint32 decoded wrongly.");
        Check(ModbusFrames.Decode([0xFFE7], "int16", "big") == -25 && ModbusFrames.Decode([0xFFE7], "uint16", "big") == 65_511,
            "16-bit sign handled wrongly.");
        Check(ModbusFrames.DecodeBit([0b0000_0100], 2) == 1 && ModbusFrames.DecodeBit([0b0000_0100], 1) == 0, "Bit unpacking is wrong.");
        foreach (var bad in new[] { Metric("X", "coil", 0, "int16"), Metric("X", "holding", 0, "bool"), Metric("X", "holding", 65535, "uint32"),
                     new MetricDefinition("X", "holding", 0, 1, "", "float32", "middle") })
        {
            try { bad.Validate(); throw new Exception($"Invalid metric {bad} was accepted."); }
            catch (ArgumentException) { }
        }
    }

    private static void CheckQuality()
    {
        var reading = new DeviceReading("fridge-q", 7, DateTimeOffset.UtcNow, new()
        {
            ["Temperature"] = new(Number: 4.5), ["Humidity"] = new(Quality: MetricQuality.Bad),
            ["Power"] = new(Quality: MetricQuality.Stale)
        });
        reading.Validate();
        foreach (var bad in new MetricValue[] { new(Number: 1, Quality: MetricQuality.Bad), new(Quality: "odd"), new() })
        {
            try { (reading with { Metrics = new() { ["X"] = bad } }).Validate(); throw new Exception($"Invalid metric {bad} was accepted."); }
            catch (ArgumentException) { }
        }
        var payload = SparkplugPayloads.DeviceReading(1, reading);
        var humidity = payload.Metrics.Single(metric => metric.Name == "Humidity");
        Check(humidity.IsNull && SparkplugDecoder.QualityOf(humidity) == MetricQuality.Bad, "Bad quality was not encoded as a null metric with Quality 0.");
        Check(payload.Metrics.Single(metric => metric.Name == "Temperature").Properties is null, "A good metric should carry no quality property.");
        var decoder = new SparkplugDecoder();
        decoder.Decode("spBv1.0/g/NBIRTH/n", SparkplugPayloads.NodeBirth(1).ToByteArray());
        var decoded = decoder.Decode("spBv1.0/g/DBIRTH/n/fridge-q", payload.ToByteArray());
        Check(decoded.Metrics["Humidity"] is { Quality: MetricQuality.Bad, Number: null }
              && decoded.Metrics["Power"].Quality == MetricQuality.Stale && decoded.Metrics["Temperature"] is { Number: 4.5, IsGood: true },
            "Quality did not survive the Sparkplug round trip.");
    }

    private static async Task CheckPollerAsync()
    {
        var port = FreePort();
        var state = Path.Combine(Path.GetTempPath(), "daq-check-" + Guid.NewGuid().ToString("N"));
        using var server = new ModbusTcpServer();
        server.Start(new IPEndPoint(IPAddress.Loopback, port));
        SetSensorValues(server);
        using var outbox = new Outbox(":memory:");
        using var stop = new CancellationTokenSource();
        var settings = new DaqSettings { StateDirectory = state, FailedPollsBeforeOffline = 3 };
        var device = Fridge("fridge-a", port) with
        {
            Metrics =
            [
                new("Temperature", "holding", 0, 0.1, "Â°C", "int16"), new("Humidity", "holding", 1, 0.1, "%", "uint16"),
                new("CompressorPower", "holding", 10, 1, "W", "float32"), new("DoorOpenings", "holding", 12, 1, "", "uint32"),
                new("GpioValue", "coil", 0, 1, "door")
            ]
        };
        var startedAt = DateTimeOffset.UtcNow.AddMilliseconds(-1);
        var poller = new ModbusPoller(device, settings, outbox);
        var polling = poller.RunAsync(stop.Token);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            OutboxRow[] rows;
            while ((rows = outbox.PeekUnsent(10)).Length == 0 && DateTime.UtcNow < deadline) await Task.Delay(100);
            var reading = rows.FirstOrDefault()?.Reading;
            Check(rows.FirstOrDefault()?.Kind == "DBIRTH" && reading is { DeviceSeq: 1 }
                  && reading.Metrics["Temperature"].Number == -2.5 && reading.Metrics["Humidity"].Number == 65
                  && reading.Metrics["CompressorPower"].Number == 85.5 && reading.Metrics["DoorOpenings"].Number == 70_000
                  && reading.Metrics["GpioValue"].Number == 1 && reading.Metrics["GpioPin"].Number == 17,
                "Registers were not read, decoded and scaled correctly: " + string.Join(", ",
                    reading?.Metrics.Select(metric => $"{metric.Key}={metric.Value.Number}") ?? []));
            while (poller.Status.State != "polling" && DateTime.UtcNow < deadline) await Task.Delay(10);
            var goodStatus = poller.Status;
            Check(goodStatus is { State: "polling", Failures: 0, LastError: null, LastSeq: 1 }
                && goodStatus.LastPollAt == reading!.Timestamp && goodStatus.LastSuccessAt == reading.Timestamp
                && goodStatus.LastRaw.Any(frame => frame.Register == "holding" && frame.Values[0] == 65511)
                && goodStatus.LastValues["Temperature"].Number == -2.5,
                "Successful polling lost the demo's raw values, timestamps or diagnostic state.");
            server.Stop();
            while (!outbox.PeekUnsent(100).Any(row => row.Kind == "DDEATH") && DateTime.UtcNow < deadline) await Task.Delay(100);
            var after = outbox.PeekUnsent(100);
            Check(after.Any(row => row is { Kind: "DDEATH", DeviceId: "fridge-a" }), "Three failed polls did not record the device offline.");
            // The failed polls before the death are Bad samples, numbered in sequence, so the gap is explained.
            var badSamples = after.TakeWhile(row => row.Kind != "DDEATH").Where(row => row.Reading?.Metrics["Temperature"].Quality == MetricQuality.Bad).ToArray();
            Check(badSamples.Length == settings.FailedPollsBeforeOffline - 1 && badSamples.All(row => row.Reading!.Metrics["GpioPin"].Number == 17),
                $"Expected {settings.FailedPollsBeforeOffline - 1} Bad samples before DDEATH, got {badSamples.Length}.");
            Check(reading!.Timestamp >= startedAt && reading.Timestamp <= rows[0].CreatedAt.AddMilliseconds(1), "The reading was not stamped at request time.");
            while (poller.Status.State != "offline" && DateTime.UtcNow < deadline) await Task.Delay(10);
            var failedStatus = poller.Status;
            Check(failedStatus.State == "offline" && failedStatus.Failures >= 3 && failedStatus.LastError is not null
                && failedStatus.LastSuccessAt == goodStatus.LastSuccessAt && failedStatus.LastRaw == goodStatus.LastRaw
                && failedStatus.LastValues == goodStatus.LastValues,
                "Failure diagnostics did not retain the last good values or report the device offline.");
        }
        finally
        {
            stop.Cancel();
            await polling;
            Directory.Delete(state, recursive: true);
        }
    }

    private static async Task CheckStorageRecoveryAsync()
    {
        var state = Path.Combine(Path.GetTempPath(), "daq-storage-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        try
        {
            var port = FreePort();
            using var server = new ModbusTcpServer();
            server.Start(new IPEndPoint(IPAddress.Loopback, port));
            SetSensorValues(server);
            var database = Path.Combine(state, "outbox.db");
            using var outbox = new Outbox(database);
            using var probe = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
            probe.Open();
            using var stop = new CancellationTokenSource();
            var settings = new DaqSettings { StateDirectory = state, FailedPollsBeforeOffline = 3 };
            var poller = new ModbusPoller(Fridge("blocked", port), settings, outbox);
            var polling = poller.RunAsync(stop.Token);
            var other = new ModbusPoller(Fridge("other", port), settings, outbox).RunAsync(stop.Token);
            OutboxRow[] Rows(string id) => outbox.PeekUnsent(1000).Where(row => row.DeviceId == id).ToArray();
            ulong SavedSequence() => Rows("blocked").Max(row => row.Reading?.DeviceSeq ?? 0);
            ulong AllocatedSequence() => ulong.Parse(File.ReadAllText(Path.Combine(state, "sequences", "blocked.seq")));
            void Execute(string sql) { using var command = probe.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
            void Fail(string name, string condition) => Execute($"CREATE TRIGGER {name} BEFORE INSERT ON Outbox WHEN NEW.DeviceId = 'blocked' AND ({condition}) BEGIN SELECT RAISE(ABORT, 'test storage unavailable'); END;");
            async Task WaitFor(Func<bool> condition, string reason)
            {
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (!condition() && DateTime.UtcNow < deadline)
                {
                    Check(!polling.IsCompleted && !other.IsCompleted, "A poller stopped unexpectedly: " + polling.Exception);
                    await Task.Delay(50);
                }
                Check(condition(), reason);
            }
            try
            {
                await WaitFor(() => Rows("blocked").Length > 0 && Rows("other").Length > 0, "Pollers did not start.");
                Fail("FailReading", "1");
                await WaitFor(() => AllocatedSequence() > SavedSequence(), "Storage failure was not exercised.");
                var pending = AllocatedSequence();
                var otherCount = Rows("other").Length;
                await Task.Delay(3200); // More than the device failure threshold, with storage still unavailable.
                Check(!polling.IsCompleted && AllocatedSequence() == pending, "Storage retries faulted the poller or allocated extra sequences.");
                Check(Rows("other").Length > otherCount, "One device's storage retry blocked another poller.");
                Check(Rows("blocked").All(row => row.Kind != "DDEATH" && row.Reading!.Metrics["Temperature"].IsGood),
                    "A storage failure was counted as a device failure.");
                Check(poller.Status is { State: "polling", Failures: 0, LastError: null },
                    "Storage errors were incorrectly shown as Modbus failures in demo diagnostics.");
                var changedAt = DateTimeOffset.UtcNow;
                server.GetHoldingRegisters(1).SetBigEndian<short>(0, 120);
                Execute("DROP TRIGGER FailReading");
                await WaitFor(() => SavedSequence() > pending, "Polling did not recover after storage became available.");
                var recovered = Rows("blocked").Single(row => row.Reading?.DeviceSeq == pending).Reading!;
                Check(recovered.Metrics["Temperature"].Number == -2.5 && recovered.Timestamp < changedAt,
                    "Retry changed the pending sample's values or timestamp.");
                Check(Rows("blocked").Last().Reading!.Metrics["Temperature"].Number == 12,
                    "Fresh polling did not resume after saving the pending sample.");

                // The persistent device sequence can also fail independently of SQLite.
                using (var locked = new FileStream(Path.Combine(state, "sequences", "blocked.seq.tmp"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    await Task.Delay(1500);
                    var count = Rows("blocked").Length;
                    await Task.Delay(2200);
                    Check(!polling.IsCompleted && Rows("blocked").Length == count, "Sequence-file failure was not retried before the next poll.");
                    Check(Rows("blocked").All(row => row.Kind != "DDEATH" && row.Reading!.Metrics["Temperature"].IsGood),
                        "Sequence-file failure produced a Bad sample or DDEATH.");
                }
                var beforeResume = SavedSequence();
                await WaitFor(() => SavedSequence() > beforeResume, "Sequence-file recovery did not resume polling.");

                // Failed saves of a genuine Bad sample and offline event must also remain retryable.
                Fail("FailBad", "NEW.Kind = 'DDATA' AND json_extract(NEW.Reading, '$.metrics.Temperature.quality') = 'bad'");
                Fail("FailDeath", "NEW.Kind = 'DDEATH'");
                server.Stop();
                await WaitFor(() => AllocatedSequence() > SavedSequence(), "Bad-sample storage failure was not exercised.");
                await Task.Delay(1200);
                Check(!polling.IsCompleted, "Failed saving a Bad sample killed the poller.");
                Execute("DROP TRIGGER FailBad");
                await WaitFor(() => Rows("blocked").Count(row => row.Reading?.Metrics["Temperature"].Quality == MetricQuality.Bad) == 2,
                    "Expected two saved Bad samples before offline detection.");
                await Task.Delay(2200);
                Check(!polling.IsCompleted && Rows("blocked").All(row => row.Kind != "DDEATH"),
                    "Failed saving DDEATH killed the poller or recorded an uncommitted offline state.");
                Execute("DROP TRIGGER FailDeath");
                await WaitFor(() => Rows("blocked").Count(row => row.Kind == "DDEATH") == 1, "Offline event was not retried exactly once.");
                using var restarted = new ModbusTcpServer();
                restarted.Start(new IPEndPoint(IPAddress.Loopback, port));
                SetSensorValues(restarted);
                await WaitFor(() => Rows("blocked").Last() is { Kind: "DBIRTH", Reading: not null }, "Device recovery did not produce DBIRTH.");
                Check(Rows("blocked").Select(row => row.Reading).OfType<DeviceReading>().Select(reading => reading.DeviceSeq)
                    .SequenceEqual(Enumerable.Range(1, (int)SavedSequence()).Select(value => (ulong)value)),
                    "Storage retries duplicated or skipped a saved reading sequence.");

                Fail("FailShutdown", "1");
                await WaitFor(() => AllocatedSequence() > SavedSequence(), "Shutdown storage failure was not exercised.");
                stop.Cancel();
                await Task.WhenAll(polling, other).WaitAsync(TimeSpan.FromSeconds(3));
                Check(polling.IsCompletedSuccessfully && Rows("blocked").Count(row => row.Kind == "DDEATH") == 1,
                    "Cancellation during storage failure faulted the poller or added a false DDEATH.");
                using var integrity = probe.CreateCommand();
                integrity.CommandText = "PRAGMA integrity_check";
                Check((string?)integrity.ExecuteScalar() == "ok", "Outbox integrity check failed after retries.");
            }
            finally
            {
                stop.Cancel();
                await Task.WhenAll(polling, other).WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally { Directory.Delete(state, recursive: true); }
        Console.WriteLine("PASS: SQLite and sequence-file failure recovery, unchanged pending samples, independent pollers, Bad-sample/offline retries and cancellation during storage failure.");
    }

    private static async Task CheckConfigurationReconciliationAsync()
    {
        var state = Path.Combine(Path.GetTempPath(), "daq-config-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        await using var api = builder.Build();
        var configUrl = $"http://127.0.0.1:{FreePort()}";
        api.Urls.Add(configUrl);
        GatewayConfiguration? snapshot = null;
        api.MapGet("/config", () => Volatile.Read(ref snapshot) is { } value ? Results.Ok(value) : Results.StatusCode(503));
        await api.StartAsync();
        try
        {
            using var server = new ModbusTcpServer();
            var port = FreePort();
            server.Start(new IPEndPoint(IPAddress.Loopback, port));
            SetSensorValues(server);
            var database = Path.Combine(state, "outbox.db");
            using var outbox = new Outbox(database);
            using var probe = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
            probe.Open();
            var settings = new DaqSettings { ConfigUrl = configUrl, StateDirectory = state, ConfigRefreshSeconds = 1 };
            using var client = new ConfigClient(settings);
            var keep = Fridge("keep", port);
            OutboxRow[] Rows(string id) => outbox.PeekUnsent(1000).Where(row => row.DeviceId == id).ToArray();
            void Sql(string sql) { using var command = probe.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
            long Deaths(string id)
            {
                using var command = probe.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM Outbox WHERE DeviceId = $id AND Kind = 'DDEATH'";
                command.Parameters.AddWithValue("$id", id);
                return (long)command.ExecuteScalar()!;
            }
            DeviceReading Seed(string id, bool published)
            {
                var reading = SparkplugPayloads.FridgeReading(id, 1, DateTimeOffset.UtcNow.AddMinutes(-1), 4.5, 65, 17, 0);
                outbox.AcceptReading(reading);
                if (published) outbox.MarkSent(Rows(id).Single());
                return reading;
            }
            async Task WaitFor(Func<bool> condition, Task task, string reason)
            {
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (!condition() && DateTime.UtcNow < deadline)
                {
                    Check(!task.IsCompleted, "Acquisition stopped unexpectedly: " + task.Exception);
                    await Task.Delay(50);
                }
                Check(condition(), reason);
            }
            AcquisitionService? acquisition = null;
            async Task Run(Func<Task, Task> checks)
            {
                using var stop = new CancellationTokenSource();
                acquisition = new AcquisitionService(settings, client, outbox);
                var task = acquisition.RunAsync(stop.Token);
                try { await checks(task); }
                finally
                {
                    stop.Cancel();
                    try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (OperationCanceledException) { }
                }
            }

            var disabled = Seed("disabled", published: true) with { DeviceSeq = 2, Timestamp = DateTimeOffset.UtcNow.AddSeconds(-10) };
            outbox.AcceptReading(disabled);
            Seed("deleted", published: true);
            Seed("unpublished", published: false);
            Seed("already-offline", published: true);
            outbox.AcceptDeviceOffline("already-offline");
            // An unavailable API with no cache is not an empty enabled-device list.
            await Run(async task =>
            {
                await Task.Delay(1300);
                Check(Deaths("disabled") == 0 && Deaths("deleted") == 0, "Missing configuration incorrectly disabled saved devices.");
                Volatile.Write(ref snapshot, new(1, [keep]));
                await WaitFor(() => Deaths("disabled") == 1 && Deaths("deleted") == 1 && Rows("keep").Length > 0,
                    task, "Startup did not reconcile persisted devices while starting enabled ones.");
                Check(Rows("disabled").Select(row => row.Kind).SequenceEqual(["DDATA", "DDEATH"])
                    && Rows("disabled")[0].Reading is { } retained && retained.DeviceSeq == disabled.DeviceSeq
                    && retained.Timestamp == disabled.Timestamp && retained.Metrics.OrderBy(item => item.Key).SequenceEqual(disabled.Metrics.OrderBy(item => item.Key)),
                    "Reconciliation changed/dropped the saved reading or reordered its DDEATH.");
                Check(Rows("unpublished").Select(row => row.Kind).SequenceEqual(["DBIRTH", "DDEATH"])
                    && Deaths("already-offline") == 1, "Never-published births or pending deaths were mishandled.");
                Check(outbox.GetPublishedOnlineDevices().Any(reading => reading.DeviceId == "disabled"),
                    "Reconciliation overwrote broker-acknowledged state before replay.");
                foreach (var row in outbox.PeekUnsent(1000).Where(row => row.DeviceId != "keep")) outbox.MarkSent(row);
                Check(outbox.GetPublishedOnlineDevices().All(reading => reading.DeviceId == "keep"), "Replayed DDEATH did not clear published state.");

                Seed("storage", published: true);
                Sql("CREATE TRIGGER FailReconcile BEFORE INSERT ON Outbox WHEN NEW.DeviceId = 'storage' AND NEW.Kind = 'DDEATH' BEGIN SELECT RAISE(ABORT, 'test reconciliation storage failure'); END;");
                Volatile.Write(ref snapshot, new(2, [keep]));
                var otherCount = Rows("keep").Length;
                await Task.Delay(2300);
                Check(!task.IsCompleted && Deaths("storage") == 0 && outbox.GetIntakeOnlineDeviceIds().Contains("storage")
                    && Rows("keep").Length > otherCount, "Failed reconciliation committed partial state or stopped unaffected polling.");
                Sql("DROP TRIGGER FailReconcile");
                await WaitFor(() => Deaths("storage") == 1, task, "Failed configuration version was not retried after storage recovery.");
                Volatile.Write(ref snapshot, null);
                var beforeOutage = Rows("keep").Length;
                await WaitFor(() => Rows("keep").Length > beforeOutage, task, "API outage stopped existing polling.");
            });
            // Fresh acquisition process state, API down: the last valid cached configuration still applies.
            await Run(async task =>
            {
                var beforeCache = Rows("keep").Length;
                await WaitFor(() => Rows("keep").Length > beforeCache, task, "Cached startup did not resume enabled polling.");
                // Acknowledged deaths were deleted and must not be queued again; the unacknowledged one stays once.
                Check(Deaths("disabled") == 0 && Deaths("deleted") == 0 && Deaths("storage") == 1, "Restart duplicated offline transitions.");
                Volatile.Write(ref snapshot, new(3, []));
                await WaitFor(() => Deaths("keep") == 1, task, "A valid empty registry did not stop the active device.");
                var afterDisable = Rows("keep").Length;
                await Task.Delay(1300);
                Check(Rows("keep").Length == afterDisable && outbox.GetIntakeOnlineDeviceIds().Length == 0,
                    "A late poll resurrected the disabled device.");
                Volatile.Write(ref snapshot, new(4, [keep]));
                await WaitFor(() => Rows("keep").Last() is { Kind: "DBIRTH" }, task, "Re-enable did not queue a new birth after the pending death.");
            });
            Volatile.Write(ref snapshot, new(5, []));
            await Run(async task => await WaitFor(() => Deaths("keep") == 2, task, "Restart with empty config failed to reconcile the re-enabled device."));
            await Run(async task =>
            {
                await Task.Delay(1200);
                Check(!task.IsCompleted && Deaths("keep") == 2, "Repeated empty config/restart duplicated DDEATH.");
            });
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = 0;
            server.RequestValidator = (_, _, _, _) =>
            {
                entered.TrySetResult();
                Interlocked.Increment(ref requests);
                Thread.Sleep(1200); // Each request succeeds, but six frames exceed the old five-second stop wait.
                return ModbusExceptionCode.OK;
            };
            var slow = keep with { Id = "slow", Metrics = Enumerable.Range(0, 5)
                .Select(i => new MetricDefinition("M" + i, "holding", (ushort)(i * 20), 1, ""))
                .Append(new("Door", "coil", 0, 1, "")).ToArray() };
            Volatile.Write(ref snapshot, new(6, [slow]));
            await Run(async task =>
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Volatile.Write(ref snapshot, new(7, []));
                await Task.Delay(1300); // Apply is now waiting for the slow in-flight poll.
                var statuses = await Task.Run(() => acquisition!.Statuses()).WaitAsync(TimeSpan.FromSeconds(1));
                Check(statuses.Any(status => status.DeviceId == "slow") && acquisition!.ConfigState.Version == 6,
                    "In-flight configuration changes blocked diagnostics or reported the new version too early.");
                await WaitFor(() => Deaths("slow") == 1, task, "Disable did not wait for the complete in-flight sample.");
                Check(requests == 6 && Rows("slow").Select(row => row.Kind).SequenceEqual(["DBIRTH", "DDEATH"])
                    && !outbox.GetIntakeOnlineDeviceIds().Contains("slow"), "A slow poll was dropped or resurrected the disabled device.");
            });
            using var integrity = probe.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check";
            Check((string?)integrity.ExecuteScalar() == "ok", "Outbox integrity failed after reconciliation.");
        }
        finally { await api.StopAsync(); Directory.Delete(state, recursive: true); }
        Console.WriteLine("PASS: Persisted device reconciliation, disabled/deleted/empty configuration, ordered backlog, pending deaths, SQLite rollback/retry, unaffected polling, cached restart, re-enable and slow in-flight polls.");
    }

    // Sensor registers: -2.5 C, 65%, 85.5 W, 70000 door openings, door open.
    private static void SetSensorValues(ModbusTcpServer server)
    {
        server.AddUnit(1);
        var registers = server.GetHoldingRegisters(1);
        registers.SetBigEndian<short>(0, -25);
        registers.SetBigEndian<short>(1, 650);
        var power = BitConverter.SingleToInt32Bits(85.5f);
        registers.SetBigEndian<short>(10, (short)(power >> 16));
        registers.SetBigEndian<short>(11, (short)power);
        registers.SetBigEndian<short>(12, 1);
        registers.SetBigEndian<short>(13, 0x1170);
        server.GetCoils(1)[0] = 1;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
