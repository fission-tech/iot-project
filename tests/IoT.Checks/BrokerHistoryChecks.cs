using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using EdgeGateway.Gateway.Configuration;
using EdgeGateway.Gateway.Connection;
using EdgeGateway.Gateway.Sparkplug;
using EdgeGateway.Gateway.Storage;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MQTTnet;
using MQTTnet.Formatter;
using Org.Eclipse.Tahu.Protobuf;

internal static class BrokerHistoryChecks
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("Broker history: " + message); }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
        connection.Open();
        return connection;
    }

    private static object? Sql(string path, string query)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = query;
        return command.ExecuteScalar();
    }

    private static long Number(string path, string query) => (long)Sql(path, query)!;
    private static IoT.Contracts.DeviceReading Reading(ulong seq) =>
        SparkplugPayloads.FridgeReading("history-device", seq, DateTimeOffset.UtcNow, 4.5, 65, 17, 0);

    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "broker-history-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "outbox.db");
        var time = DateTimeOffset.Parse("2026-10-29T10:00:00Z");
        try
        {
            var legacyPath = Path.Combine(directory, "pre-ef.db");
            Sql(legacyPath, "CREATE TABLE Outbox (Id INTEGER PRIMARY KEY);");
            try { using var legacy = new Outbox(legacyPath); throw new Exception("Pre-EF DB should require reset"); }
            catch (InvalidOperationException error) when (error.Message.Contains("pre-EF schema")) { }
            Check(Number(legacyPath, "SELECT COUNT(*) FROM sqlite_master WHERE name='Outbox'") == 1, "legacy guard deleted data");

            // Apply the baseline migration, insert pending and acknowledged data, then let startup apply the rest.
            using (var database = new GatewayDbContextFactory().CreateDbContext([path]))
                database.GetService<IMigrator>().Migrate("InitializeGatewayRuntime");
            Sql(path, """
                INSERT INTO Outbox(Kind,DeviceId,CreatedAt) VALUES('DDEATH','old-device',0);
                INSERT INTO Outbox(Kind,DeviceId,CreatedAt,SentAt) VALUES('DDEATH','acknowledged-device',0,1);
                """);
            using (var box = new Outbox(path))
            {
                Check(box.CountUnsent() == 1 && box.PeekUnsent(10).Single().DeviceId == "old-device",
                    "schema upgrade lost pending data or kept acknowledged rows for republishing");
                Check(Number(path, "SELECT COUNT(*) FROM __EFMigrationsHistory") == 3, "migrations not recorded");
                Check(Number(path, "SELECT COUNT(*) FROM pragma_index_list('Outbox') WHERE name='IX_Outbox_BrokerHistoryId'") == 1, "history index missing");
                box.RecordBrokerConnected(time);
                Check(Number(path, "SELECT COUNT(*) FROM BrokerHistory") == 0, "normal startup created an outage");
                box.RecordBrokerDisconnected(time);
                box.RecordBrokerDisconnected(time.AddSeconds(1));
                box.AcceptReading(Reading(1));
                box.AcceptReading(Reading(2));
                box.AcceptDeviceOffline("history-device");
                Check(Number(path, "SELECT COUNT(*) FROM BrokerHistory") == 1, "retry duplicated outage");
                Check(Sql(path, "SELECT MessagesBuffered FROM BrokerHistory") is DBNull, "count updated before reconnect");
                box.RecordBrokerConnected(time.AddSeconds(120));
                box.RecordBrokerConnected(time.AddSeconds(121));
                Check(Number(path, "SELECT DowntimeSeconds FROM BrokerHistory") == 120, "wrong duration");
                Check(Number(path, "SELECT MessagesBuffered FROM BrokerHistory") == 3, "birth/data/death count incorrect");
                Check(((string)Sql(path, "SELECT DisconnectedAt FROM BrokerHistory")!).EndsWith('Z'), "time not UTC ISO8601");
                var rows = box.PeekUnsent(10);
                box.MarkSent(rows[0]); // Old pre-feature message is not assigned to this outage.
                box.MarkSent(rows[1]);
                box.MarkSent(rows[1]);
                Check(Number(path, "SELECT BufferedMessagesSent FROM BrokerHistory") == 1, "duplicate ACK counted");
                box.RecordBrokerDisconnected(time.AddMinutes(3));
                box.AcceptReading(Reading(3));
                box.RecordBrokerConnected(time.AddMinutes(4));
                box.MarkSent(rows[2]);
                box.MarkSent(rows[3]);
                Check(Number(path, "SELECT BufferedMessagesSent FROM BrokerHistory WHERE Id=1") == 3, "old replay counted in new outage");
                Check(Number(path, "SELECT BufferedMessagesSent FROM BrokerHistory WHERE Id=2") == 0, "new outage polluted");

                // Counter failure rolls back sent marker and published device state too.
                Sql(path, "CREATE TRIGGER fail_history BEFORE UPDATE OF BufferedMessagesSent ON BrokerHistory BEGIN SELECT RAISE(ABORT,'injected history failure'); END;");
                var pending = box.PeekUnsent(10).Single();
                try { box.MarkSent(pending); throw new Exception("Expected transaction failure"); }
                catch (SqliteException) { }
                Check(box.CountUnsent() == 1 && box.GetPublishedOnlineDevices().Length == 0, "ACK transaction partially committed");
                Sql(path, "DROP TRIGGER fail_history;");
                box.MarkSent(pending);
                box.AcceptReading(Reading(4)); // Live traffic is excluded.
                box.MarkSent(box.PeekUnsent(10).Single());
                Check(Number(path, "SELECT BufferedMessagesSent FROM BrokerHistory WHERE Id=2") == 1, "live message counted");
                // The ACK deleted the row; history keeps its totals and a late repeated ACK changes nothing.
                Check(Number(path, "SELECT COUNT(*) FROM Outbox") == 0, "acknowledged rows were not deleted");
                box.MarkSent(pending);
                Check(Number(path, "SELECT SUM(BufferedMessagesSent) FROM BrokerHistory") == 4, "deletion lost/duplicated totals");

                box.RecordBrokerDisconnected(time.AddMinutes(5));
                box.AcceptReading(Reading(5));
            } // Simulate a crash: no EndBrokerObservation call.
            using (var box = new Outbox(path))
            {
                Check(Number(path, "SELECT COUNT(*) FROM __EFMigrationsHistory") == 3, "repeat startup reran migration");
                box.RecordBrokerConnected(time.AddHours(1));
                Check(Sql(path, "SELECT ReconnectedAt FROM BrokerHistory WHERE Id=3") is DBNull, "restart fabricated recovery");
                box.AcceptReading(Reading(6));
                Check(Number(path, "SELECT COUNT(*) FROM Outbox WHERE BrokerHistoryId IS NULL") == 1, "restart reused old open outage");
                box.RecordBrokerDisconnected(time.AddHours(2));
                box.AcceptReading(Reading(7));
                box.RecordBrokerConnected(time.AddHours(2).AddSeconds(10));
                foreach (var row in box.PeekUnsent(10)) box.MarkSent(row);
                Check(Number(path, "SELECT BufferedMessagesSent FROM BrokerHistory WHERE Id=3") == 1, "restart lost old message ownership");
                Check(Number(path, "SELECT BufferedMessagesSent FROM BrokerHistory WHERE Id=4") == 1, "restart miscounted new outage");
                box.RecordBrokerDisconnected(time.AddHours(3));
                box.EndBrokerObservation();
                box.AcceptReading(Reading(8));
                Check(Number(path, "SELECT COUNT(*) FROM Outbox WHERE BrokerHistoryId=5") == 0, "end observation left active association");

                // Concurrent polling and connection transitions must assign each row at most once.
                Parallel.Invoke(
                    () => { for (var i = 0; i < 20; i++) { box.RecordBrokerDisconnected(time); box.RecordBrokerConnected(time.AddSeconds(1)); } },
                    () => { for (ulong i = 100; i < 200; i++) box.AcceptReading(Reading(i)); });
                Check(Number(path, "SELECT COUNT(*) FROM BrokerHistory h WHERE h.MessagesBuffered IS NOT NULL AND h.MessagesBuffered != (SELECT COUNT(*) FROM Outbox o WHERE o.BrokerHistoryId=h.Id) AND h.Id>5") == 0, "concurrent reconnect count inconsistent");
                Check((string)Sql(path, "PRAGMA integrity_check")! == "ok", "database integrity failed");
                Check(Sql(path, "PRAGMA foreign_key_check") is null, "foreign key violation");
            }
            using (var database = new GatewayDbContextFactory().CreateDbContext([path]))
            {
                Check(!database.Database.HasPendingModelChanges(), "model snapshot differs from actual model");
                // A developer can roll back the history migration without changing existing Outbox data.
                var pendingBefore = Number(path, "SELECT COUNT(*) FROM Outbox");
                database.GetService<IMigrator>().Migrate("InitializeGatewayRuntime");
                Check(Number(path, "SELECT COUNT(*) FROM __EFMigrationsHistory") == 1, "rollback failed");
                Check(Number(path, "SELECT COUNT(*) FROM Outbox") == pendingBefore, "rollback lost Outbox messages");
                database.Database.Migrate();
                Check(Number(path, "SELECT COUNT(*) FROM __EFMigrationsHistory") == 3, "reapply failed");
            }
            Console.WriteLine("PASS: Broker history migration, UTC/downtime, retry deduplication, birth/data/death counts, multiple outages, atomic ACK rollback, repeated ACKs, delete on ACK, interrupted restart and concurrent polling.");
            Console.WriteLine("PASS: EF migrations: fresh baseline, data-preserving history upgrade, acknowledged rows removed with SentAt, repeat startup, migration ledger/index, model snapshot, rollback/reapply and explicit pre-EF reset requirement.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    public static async Task RunLiveAsync(string mosquitto)
    {
        var directory = Path.Combine(Path.GetTempPath(), "broker-history-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "outbox.db");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var cert = Path.Combine(directory, "broker.crt");
        File.Copy(Path.GetFullPath("broker/certs/broker.crt"), cert);
        File.Copy(Path.GetFullPath("broker/certs/broker.key"), Path.Combine(directory, "broker.key"));
        var configuration = Path.Combine(directory, "mosquitto.conf");
        File.WriteAllText(configuration, $"listener {port} 127.0.0.1\nallow_anonymous true\npersistence false\ncertfile {cert.Replace('\\', '/')}\nkeyfile {Path.Combine(directory, "broker.key").Replace('\\', '/')}\n");
        Process? broker = null;
        Task<string>? brokerErrors = null;
        using var box = new Outbox(path);
        var settings = new GatewaySettings();
        settings.Mqtt.Port = port;
        settings.Mqtt.ClientId = "history-check-" + Guid.NewGuid().ToString("N");
        settings.Mqtt.StateDirectory = Path.Combine(directory, "sequence");
        settings.Mqtt.BrokerCertificatePath = cert;
        settings.Mqtt.ConnectTimeoutSeconds = 1;
        settings.Mqtt.KeepAliveSeconds = 5;
        settings.Mqtt.RetryDelaySeconds = settings.Mqtt.MaxRetryDelaySeconds = 1;
        settings.Gateway.DatabasePath = path;
        using var connection = new MqttConnection(settings.Mqtt, settings.Sparkplug);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var subscriber = new MqttClientFactory().CreateMqttClient();
        using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(cert));
        var options = new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", port)
            .WithProtocolVersion(MqttProtocolVersion.V311).WithClientId("history-observer")
            .WithTlsOptions(new MqttClientTlsOptionsBuilder().WithTrustChain(new X509Certificate2Collection(certificate)).Build()).Build();
        var killOnHistorical = 0;
        var historicalSeen = 0;
        subscriber.ApplicationMessageReceivedAsync += args =>
        {
            var payload = Payload.Parser.ParseFrom(args.ApplicationMessage.Payload.ToArray());
            if (args.ApplicationMessage.Topic.Contains("/DDATA/") && payload.Metrics.Any(metric => metric.IsHistorical))
            {
                Interlocked.Increment(ref historicalSeen);
                if (Interlocked.Exchange(ref killOnHistorical, 0) == 1) broker!.Kill();
            }
            return Task.CompletedTask;
        };

        async Task StartBroker()
        {
            broker?.Dispose();
            var start = new ProcessStartInfo(mosquitto) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("-c"); start.ArgumentList.Add(configuration);
            broker = Process.Start(start)!;
            brokerErrors = broker.StandardError.ReadToEndAsync();
            _ = broker.StandardOutput.ReadToEndAsync();
            await Wait(async () =>
            {
                if (broker.HasExited) throw new Exception(await brokerErrors);
                try { await subscriber.ConnectAsync(options); return true; } catch { return false; }
            });
            await subscriber.SubscribeAsync(new MqttClientFactory().CreateSubscribeOptionsBuilder().WithTopicFilter("spBv1.0/#").Build());
        }
        async Task StopBroker()
        {
            if (broker is { HasExited: false }) broker.Kill();
            if (broker is not null) await broker.WaitForExitAsync();
            await Wait(() => Task.FromResult(!subscriber.IsConnected));
        }
        var running = new GatewayRunner(connection, box, settings).RunAsync(stop.Token);
        try
        {
            await Wait(() => Task.FromResult(Number(path, "SELECT COUNT(*) FROM BrokerHistory") == 1));
            await Task.Delay(1200);
            Check(Number(path, "SELECT COUNT(*) FROM BrokerHistory") == 1, "live failed retries duplicated history");
            box.AcceptReading(Reading(1)); box.AcceptReading(Reading(2));
            await StartBroker();
            await Wait(() => Task.FromResult(box.CountUnsent() == 0));
            Check(Number(path, "SELECT MessagesBuffered FROM BrokerHistory WHERE Id=1") == 2 && Number(path, "SELECT BufferedMessagesSent FROM BrokerHistory WHERE Id=1") == 2, "live first recovery counts");
            await StopBroker();
            await Wait(() => Task.FromResult(Number(path, "SELECT COUNT(*) FROM BrokerHistory") == 2));
            for (ulong i = 3; i < 303; i++) box.AcceptReading(Reading(i));
            killOnHistorical = 1;
            await StartBroker();
            await Wait(() => Task.FromResult(Number(path, "SELECT COUNT(*) FROM BrokerHistory") == 3));
            await StopBroker();
            var partial = Number(path, "SELECT BufferedMessagesSent FROM BrokerHistory WHERE Id=2");
            Check(partial < 300, "test did not interrupt replay");
            box.AcceptDeviceOffline("history-device");
            box.AcceptReading(Reading(303));
            await StartBroker();
            await Wait(() => Task.FromResult(box.CountUnsent() == 0));
            Check(Number(path, "SELECT BufferedMessagesSent FROM BrokerHistory WHERE Id=2") == 300, "live old replay attributed wrongly");
            Check(Number(path, "SELECT MessagesBuffered FROM BrokerHistory WHERE Id=3") == 2 && Number(path, "SELECT BufferedMessagesSent FROM BrokerHistory WHERE Id=3") == 2, "live death/birth recovery counted wrongly");
            Check(historicalSeen > 0, "no actual historical Sparkplug payload received");
            box.AcceptReading(Reading(304));
            await Wait(() => Task.FromResult(box.CountUnsent() == 0));
            Check(Number(path, "SELECT SUM(BufferedMessagesSent) FROM BrokerHistory") == 304, "live traffic or generated births included");
            stop.Cancel();
            try { await running; } catch (OperationCanceledException) { }
            await connection.DisconnectAsync(CancellationToken.None);
            Check(Number(path, "SELECT COUNT(*) FROM BrokerHistory") == 3, "normal shutdown created outage");
            Console.WriteLine($"PASS: Live Mosquitto TLS/PUBACK: startup unavailable, repeated retries, reconnect, partial replay ({partial}/300 before next outage), original outage ownership, DDEATH/DBIRTH, live exclusion, historical payloads and graceful disconnect. Counts: 2/2, 300/300, 2/2.");

            // Fresh connected startup and a diagnostics-only insert failure must not break MQTT recovery.
            var normalPath = Path.Combine(directory, "normal.db");
            using var normalBox = new Outbox(normalPath);
            using var normalConnection = new MqttConnection(settings.Mqtt, settings.Sparkplug);
            using var normalStop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var normalRunning = new GatewayRunner(normalConnection, normalBox, settings).RunAsync(normalStop.Token);
            try
            {
                await Wait(() => Task.FromResult(normalConnection.State == ConnectionState.Online));
                Check(Number(normalPath, "SELECT COUNT(*) FROM BrokerHistory") == 0, "live normal startup created outage");
                Sql(normalPath, "CREATE TRIGGER fail_insert BEFORE INSERT ON BrokerHistory BEGIN SELECT RAISE(ABORT,'injected history insert failure'); END;");
                await StopBroker();
                await Wait(() => Task.FromResult(normalConnection.State == ConnectionState.RetryWaiting));
                normalBox.AcceptReading(Reading(1));
                await StartBroker();
                await Wait(() => Task.FromResult(normalConnection.State == ConnectionState.Online && normalBox.CountUnsent() == 0));
                Check(Number(normalPath, "SELECT COUNT(*) FROM BrokerHistory") == 0, "injected history failure was not exercised");
                Sql(normalPath, "DROP TRIGGER fail_insert;");
                Console.WriteLine("PASS: Live connected startup creates no outage; injected history-write failure is logged while MQTT reconnect and buffered publishing still work.");
            }
            finally
            {
                normalStop.Cancel();
                try { await normalRunning; } catch (OperationCanceledException) { }
                await normalConnection.DisconnectAsync(CancellationToken.None);
            }
        }
        finally
        {
            stop.Cancel();
            try { await running; } catch (OperationCanceledException) { }
            await StopBroker();
            broker?.Dispose();
            subscriber.Dispose(); connection.Dispose(); box.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task Wait(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Broker history test condition not reached");
            await Task.Delay(20);
        }
    }
}
