using System.Globalization;
using System.Net;
using System.Net.Sockets;
using EdgeGateway.Gateway.Configuration;
using EdgeGateway.Gateway.Storage;
using FluentModbus;
using IoT.Contracts;
using Microsoft.Data.Sqlite;

namespace EdgeGateway.Gateway.Acquisition;

// A snapshot of one device's polling, for operators and the demo console: what is read, what came back, what failed.
public sealed record PollerStatus(
    string DeviceId, string State, string Endpoint, string[] Frames, long LastSeq, int Failures,
    DateTimeOffset? LastPollAt, DateTimeOffset? LastSuccessAt, string? LastError, RawFrame[] LastRaw,
    Dictionary<string, MetricValue> LastValues);

// The raw words (or bits) of one frame as the device returned them, before decoding.
public sealed record RawFrame(string Register, int Start, int[] Values);

// Polls one Modbus TCP device on its configured interval and commits each reading straight to the outbox,
// so a reading survives a gateway crash once committed. After repeated device failures it reports
// the device offline once; the next good poll brings it back.
public sealed class ModbusPoller(DeviceDefinition device, DaqSettings settings, Outbox outbox)
{
    private readonly DeviceSequenceStore _sequence = new(device.Id, settings.StateDirectory);
    private readonly ModbusFrame[] _frames = ModbusFrames.Plan(device.Metrics);
    private int _failures;
    private bool? _online;
    private volatile PollerStatus? _status;

    // "connecting" until the first poll, then "polling", "failing" (errors, still counted online), or "offline".
    public PollerStatus Status => _status ??= Snapshot("connecting", 0, null, null, null, [], []);
    public string DeviceId => device.Id;

    private PollerStatus Snapshot(string state, long seq, DateTimeOffset? pollAt, DateTimeOffset? successAt, string? error,
        RawFrame[] raw, Dictionary<string, MetricValue> values) =>
        new(device.Id, state, $"{device.Host}:{device.Port} unit {device.UnitId}",
            _frames.Select(frame => $"{frame.Register} {frame.Start}+{frame.Count}").ToArray(),
            seq, _failures, pollAt, successAt, error, raw, values);

    public Task RunAsync(CancellationToken cancellationToken) => Task.Factory.StartNew(() => Run(cancellationToken),
        cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Run(CancellationToken cancellationToken)
    {
        using var client = new ModbusTcpClient { ConnectTimeout = 2000, ReadTimeout = 2000, WriteTimeout = 2000 };
        var interval = TimeSpan.FromSeconds(device.PollIntervalSeconds);
        Console.WriteLine($"Polling {device.Id} at {device.Host}:{device.Port} every {device.PollIntervalSeconds}s in " +
            string.Join(", ", _frames.Select(frame => $"{frame.Register} {frame.Start}..{frame.Start + frame.Count - 1}")) + ".");
        while (!cancellationToken.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            // The source timestamp is when the device was asked, not when the reply was processed or published.
            var sampledAt = DateTimeOffset.UtcNow;
            PollResult result;
            try
            {
                if (!client.IsConnected)
                {
                    client.Connect(new IPEndPoint(Resolve(device.Host), device.Port), ModbusEndianness.BigEndian);
                    sampledAt = DateTimeOffset.UtcNow;
                }
                result = Read(client);
            }
            catch (Exception error)
            {
                try { client.Disconnect(); } catch (Exception) { }
                if (cancellationToken.IsCancellationRequested) break; // stopping mid-poll is not a device failure
                result = new(BadSample(), [], false, $"{error.GetType().Name}: {error.Message}");
            }
            if (cancellationToken.IsCancellationRequested && !result.TargetResponded) break;
            // Quality errors in a valid target response do not mean the device is unreachable.
            _failures = result.TargetResponded ? 0 : _failures + 1;
            if (result.Error is not null)
                Console.WriteLine($"Poll {device.Id}: {result.Error}; {_failures} consecutive communication failure(s).");
            var previous = Status;
            _status = Snapshot(result.TargetResponded && result.Error is null ? "polling" : _online == false ? "offline" : "failing", previous.LastSeq, sampledAt,
                previous.LastSuccessAt, result.Error, result.TargetResponded ? result.Raw : previous.LastRaw,
                result.TargetResponded ? result.Metrics : previous.LastValues);
            // Storage errors must not count as device failures. Finish saving this sample before polling again.
            if (result.TargetResponded)
            {
                var reading = SaveReading(result.Metrics, sampledAt, cancellationToken);
                if (reading is null) break;
                if (_online != true) Console.WriteLine($"{device.Id} online.");
                _online = true;
                var successAt = result.Raw.Length > 0 ? sampledAt : previous.LastSuccessAt;
                _status = Snapshot(result.Error is null ? "polling" : "failing", (long)reading.DeviceSeq,
                    sampledAt, successAt, result.Error, result.Raw, result.Metrics);
            }
            else if (_online == true && _failures < settings.FailedPollsBeforeOffline)
            {
                var reading = SaveReading(result.Metrics, sampledAt, cancellationToken);
                if (reading is null) break;
                _status = Status with { LastSeq = (long)reading.DeviceSeq };
            }
            else if (_failures >= settings.FailedPollsBeforeOffline && _online != false)
            {
                if (!SaveWithRetry(() => outbox.AcceptDeviceOffline(device.Id), "offline event", cancellationToken)) break;
                _online = false;
                _status = Status with { State = "offline" };
                Console.WriteLine($"{device.Id} offline after {_failures} failed polls.");
            }
            var wait = interval - (DateTime.UtcNow - started);
            if (wait > TimeSpan.Zero) cancellationToken.WaitHandle.WaitOne(wait);
        }
    }

    private DeviceReading? SaveReading(Dictionary<string, MetricValue> metrics, DateTimeOffset sampledAt, CancellationToken cancellationToken)
    {
        DeviceReading? reading = null;
        var saved = SaveWithRetry(() =>
        {
            // Allocate once: an outbox retry keeps the same sequence, values and source timestamp.
            reading ??= new(device.Id, _sequence.Next(), sampledAt, metrics);
            outbox.AcceptReading(reading);
        }, "reading", cancellationToken);
        if (saved)
            Console.WriteLine($"Polled {device.Id} #{reading!.DeviceSeq}: " + string.Join(", ", metrics.Select(metric =>
                $"{metric.Key}={metric.Value.Number?.ToString(CultureInfo.InvariantCulture) ?? metric.Value.Quality}")));
        return saved ? reading : null;
    }

    private bool SaveWithRetry(Action save, string pending, CancellationToken cancellationToken)
    {
        while (true)
        {
            try { save(); return true; }
            catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"Storage failed for {device.Id} {pending} ({error.GetType().Name}: {error.Message}). Retrying in 1s; polling paused.");
                if (!cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(1))) continue;
                Console.WriteLine($"Stopped with an uncommitted {pending} for {device.Id}; storage is unavailable.");
                return false;
            }
        }
    }

    // One request per frame, then each metric is decoded from its slice of the frame.
    private sealed record PollResult(Dictionary<string, MetricValue> Metrics, RawFrame[] Raw,
        bool TargetResponded, string? Error);

    private PollResult Read(ModbusTcpClient client)
    {
        var metrics = BadSample(); // Failed and unattempted frames must not publish stale values as Good.
        var raw = new List<RawFrame>();
        var responded = false;
        string? error = null;
        foreach (var frame in _frames)
        {
            try
            {
                ReadFrame(client, frame, metrics, raw);
                responded = true;
            }
            catch (ModbusException failure) when (IsTargetResponse(failure.ExceptionCode))
            {
                responded = true;
                error ??= $"{frame.Register} {frame.Start}+{frame.Count}: {failure.ExceptionCode}";
                // A complete Modbus exception response leaves the next independent request safe to try.
            }
            // FluentModbus reports a clean TCP close as InvalidOperationException.
            catch (Exception failure) when (failure is IOException or SocketException or TimeoutException or ModbusException or InvalidOperationException)
            {
                error = $"{frame.Register} {frame.Start}+{frame.Count}: {failure.GetType().Name}: {failure.Message}";
                try { client.Disconnect(); } catch (Exception) { }
                break; // Do not issue further requests on a failed/unsynchronised transport.
            }
        }
        return new(metrics, raw.ToArray(), responded, error);
    }

    private static bool IsTargetResponse(ModbusExceptionCode code) => code is
        ModbusExceptionCode.IllegalFunction or ModbusExceptionCode.IllegalDataAddress or
        ModbusExceptionCode.IllegalDataValue or ModbusExceptionCode.ServerDeviceFailure or
        ModbusExceptionCode.Acknowledge or ModbusExceptionCode.ServerDeviceBusy or ModbusExceptionCode.MemoryParityError;

    private void ReadFrame(ModbusTcpClient client, ModbusFrame frame,
        Dictionary<string, MetricValue> metrics, List<RawFrame> raw)
    {
        int unit = device.UnitId;
        if (frame.Register is "coil" or "discrete")
        {
            var bits = frame.Register == "coil"
                ? client.ReadCoils(unit, frame.Start, frame.Count).ToArray()
                : client.ReadDiscreteInputs(unit, frame.Start, frame.Count).ToArray();
            raw.Add(new(frame.Register, frame.Start,
                Enumerable.Range(0, frame.Count).Select(index => (int)ModbusFrames.DecodeBit(bits, index)).ToArray()));
            foreach (var metric in frame.Metrics)
                metrics[metric.Name] = new(Number: ModbusFrames.DecodeBit(bits, metric.Address - frame.Start) * metric.Scale);
            return;
        }
        var words = frame.Register == "holding"
            ? client.ReadHoldingRegisters<ushort>(unit, frame.Start, frame.Count).ToArray()
            : client.ReadInputRegisters<ushort>(unit, frame.Start, frame.Count).ToArray();
        raw.Add(new(frame.Register, frame.Start, words.Select(word => (int)word).ToArray()));
        foreach (var metric in frame.Metrics)
        {
            var value = ModbusFrames.Decode(words.AsSpan(metric.Address - frame.Start, metric.Width), metric.Type, metric.Order);
            // A float register can hold NaN or infinity: report that one value as Bad, keep the rest of the reading.
            metrics[metric.Name] = double.IsFinite(value)
                ? new(Number: Math.Round(value * metric.Scale, 6))
                : new(Quality: MetricQuality.Bad);
        }
    }

    // Same metric list as a good reading (so no rebirth is needed), with no values.
    private Dictionary<string, MetricValue> BadSample()
    {
        var metrics = device.Metrics.ToDictionary(metric => metric.Name, _ => new MetricValue(Quality: MetricQuality.Bad),
            StringComparer.Ordinal);
        if (device.GpioPin is { } pin) metrics["GpioPin"] = new(Number: pin);
        return metrics;
    }

    private static IPAddress Resolve(string host) =>
        IPAddress.TryParse(host, out var address) ? address
            : Dns.GetHostAddresses(host).First(item => item.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

    public bool SameDefinition(DeviceDefinition other) =>
        System.Text.Json.JsonSerializer.Serialize(device) == System.Text.Json.JsonSerializer.Serialize(other);
}
