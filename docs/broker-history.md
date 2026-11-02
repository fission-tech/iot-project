# MQTT broker connection history

The Gateway records its observed connection outages to our single Mosquitto broker in its existing runtime SQLite database. It does not store broker configuration or prove that the Mosquitto process itself stopped; a network failure also causes a connection outage.

## Storage

Open the running Gateway's `Gateway:DatabasePath` in DB Browser for SQLite. With `Start-Demo.ps1`, this is normally `logs/demo/state/outbox.db`. Configuration uses a separate database and is unchanged by this feature.

Schema is now managed by **EF Core 8 migrations**. `GatewayDbContext` maps the existing runtime tables; the Outbox's runtime queries, polling and ACK transactions still use the existing SQL. `InitializeGatewayRuntime` establishes the existing Outbox/Devices schema; `AddBrokerHistory` adds the history table, nullable Outbox foreign key and its index. Migration source/designer files and the model snapshot are in `Storage/Migrations`. Applied versions are recorded in `__EFMigrationsHistory`.

At Gateway startup, `Database.Migrate()` applies missing migrations, including on a fresh DB. Do not use `EnsureCreated()`. The one-time switch from the previous manually-created schema requires a **fresh Gateway DB**, as agreed for this PoC. Startup reports an explicit pre-EF-schema error rather than deleting data automatically. Stop the Gateway and close DB Browser before resetting that file; keep Configuration DB, config cache and sequence files. Future EF-managed databases upgrade through migrations and retain data unless a migration explicitly removes it. Do not use Reset demo for this switch, because it also deletes device configuration.

From the repository root, the local EF CLI is pinned to 8.0.31:

```powershell
dotnet tool restore
dotnet ef migrations list --project src/EdgeGateway/EdgeGateway.Gateway --context GatewayDbContext
# Explicit DB path is passed to the design-time factory after --:
dotnet ef database update --project src/EdgeGateway/EdgeGateway.Gateway --context GatewayDbContext -- "C:\path\to\outbox.db"
# After a future model change:
dotnet ef migrations add DescribeTheChange --project src/EdgeGateway/EdgeGateway.Gateway --context GatewayDbContext --output-dir Storage/Migrations
```

Review each generated migration before applying it. Development rollback of `AddBrokerHistory` removes history and its link column; rollback is not a backup. The design-time factory does not run the Gateway, connect to MQTT or read cloud credentials.

`BrokerHistory` contains:

| Column | Meaning |
|---|---|
| `Id` | Primary key for one observed outage. |
| `DisconnectedAt` | Full UTC date/time in ISO 8601, when loss was observed, or when the first connection attempt failed. |
| `ReconnectedAt` | Full UTC date/time when MQTT connected successfully, before births or replay. NULL means **reconnection not recorded**, not necessarily that the broker is still down. |
| `DowntimeSeconds` | Whole elapsed seconds between the recorded times; NULL without a recorded reconnect. Negative clock adjustments are clamped to zero. |
| `MessagesBuffered` | Number of Outbox messages inserted while this outage was active. Captured once at reconnect; NULL if observation ended without recording reconnect. |
| `BufferedMessagesSent` | Messages from that same set whose ACK handling committed successfully. Starts at zero and can keep increasing after later reconnects or Gateway restarts. |

Outbox has one new nullable foreign key, `BrokerHistoryId`, plus an index on it. Many Outbox messages reference one history row. Messages inserted outside an observed outage, including existing pre-upgrade records, have NULL. No `Interrupted` column is used.

## How it works

1. An unexpected MQTT disconnect or failed initial connection attempt creates one history row. Further failures in the same outage reuse it.
2. Existing polling continues. Outbox inserts attach the active history ID under the same lock used for storage and connection transitions. Timestamps are not used to guess message ownership.
3. Successful MQTT connection closes that active interval and counts its Outbox messages before replay starts. Later live inserts have no history ID.
4. After a QoS 1 publish completes, `MarkSent` deletes the row (reading its history ID from the delete itself) and increments that history counter in the **same transaction** as published device state. Repeated handling of an already-deleted row does nothing.
5. If another outage interrupts replay, remaining rows keep their original history ID. Acknowledging them later updates the original record.

Both counters include buffered **DBIRTH, DDATA and DDEATH** messages. They count messages, not individual metrics. Automatically generated session NBIRTH/DBIRTH and live traffic outside the outage are excluded. This is why the counter is called `BufferedMessagesSent`: not every buffered lifecycle message carries Sparkplug's `is_historical` flag. Sparkplug payloads and historical flags are unchanged.

For example, outage 1 buffered 20 messages and sent 8 before a second outage. If outage 2 adds 5 more, final totals after recovery are `20/20` for outage 1 and `5/5` for outage 2, not `20/8` and `17/17`.

## Shutdown, restart and limits

- Intentional disconnects, including normal Gateway shutdown and primary-host-controlled disconnects, do not create broker outages. Local storage/bdSeq failures are not treated as failed MQTT connection attempts.
- Ending the Gateway run clears its active history association. Startup never reopens an unfinished row from a previous run. A new failed connection creates a new row; a successful startup does not invent recovery time for the old one. Old linked messages still update their original counters when sent.
- An unfinished row retains NULL reconnect/duration/buffered total. Without an extra status field, the database alone cannot distinguish an ongoing outage from an incomplete observation in a previous run. Use current Gateway state for current connectivity.
- UTC times use the host's clock and reflect detection, not the exact instant a remote broker stopped. Downtime can be affected by host clock corrections; message ownership does not depend on the clock.
- An ACK confirms broker acceptance, not subscriber processing. A crash after broker acceptance but before the SQLite commit can cause replay/duplicate delivery. The counter is one committed ACK transition per Outbox row, not proof of exactly-once delivery.
- Connection-history write failures are logged and do not deliberately fail an otherwise successful MQTT connection. History can be incomplete if storage is unavailable. An ACK transaction failure keeps the Outbox row pending rather than partially committing its counter/state.
- Acknowledged Outbox rows are deleted immediately; history rows and their totals are never deleted with them. Upgrading removes rows that older versions had already marked sent, so they are not published again.

## ACK deletion

`MarkSent` deletes the acknowledged row with `DELETE ... RETURNING BrokerHistoryId`, then increments that history counter and updates published device state **in one transaction**. If any part fails, the row stays pending. History is never cascade-deleted, so outage totals outlive the rows they counted.

## Inspect and demo

```sql
SELECT Id, DisconnectedAt, ReconnectedAt, DowntimeSeconds,
       MessagesBuffered, BufferedMessagesSent
FROM BrokerHistory ORDER BY Id DESC;

SELECT Id, Kind, DeviceId, BrokerHistoryId
FROM Outbox ORDER BY Id DESC LIMIT 50;
```

1. Start the normal demo, enable a reachable Modbus device and confirm readings in MQTTX. A normal first connection creates no outage row.
2. Stop only Mosquitto. After the Gateway detects loss, refresh DB Browser: one row has NULL reconnect/count. Readings continue buffering with its history ID; retry attempts do not create more history rows.
3. Start Mosquitto. Refresh: reconnect time/duration and buffered total appear; the sent count increases as ACKs commit. MQTTX shows births and replay as before.
4. For a sufficiently large backlog, interrupt the broker during replay, then reconnect again. Old pending rows increment the old outage's count, new outage rows increment the new one.
5. Stop the Gateway normally: no extra outage row. To check incomplete observation, stop it while Mosquitto is unavailable, then restart it. The previous unfinished row stays NULL; a new failed connection period gets a new row.

## Automated checks

```powershell
dotnet build IoT.sln
dotnet run --project tests/IoT.Checks --no-build
# Optional real Mosquitto TLS test; needs the local demo certificate/key:
dotnet run --project tests/IoT.Checks --no-build -- --broker-history-live "C:\Program Files\mosquitto\mosquitto.exe"
```

The regular checks cover EF baseline-to-history upgrade with pending data, repeat startup, migration ledger, model snapshot, rollback/reapply, rejection of the old pre-EF schema, duplicate attempts, exact counts, lifecycle messages, repeated ACKs, transaction rollback, retention/deletion, interrupted restart and concurrent inserts. The optional live check uses a temporary EF-created DB and a separate loopback TLS port, starts/stops only its own Mosquitto process, and tests actual PUBACK/replay across multiple outages. Neither test uses the user's demo DB.
