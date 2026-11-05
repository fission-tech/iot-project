# Reading history and Grafana

The gateway publishes readings to Mosquitto and deletes them from its outbox once the broker acknowledges them, so the edge keeps no history. The **historian** subscribes to the broker, stores every reading in **TimescaleDB**, and **Grafana** charts it.

```text
Gateway ─► Mosquitto ─► EdgeGateway.Historian ─► TimescaleDB ─► Grafana
                         (Sparkplug subscriber)   (history DB)   (dashboards)
```

The gateway is not involved: it only talks MQTT, and SQLite stays at the edge for the outbox and gateway state. TimescaleDB is Postgres with a time-series extension. Grafana reads it with its built-in PostgreSQL data source.

## Running it locally

1. `copy .env.example .env` and replace the passwords. `.env` is ignored by Git; Docker Compose and the demo console both read it.
2. `./scripts/Start-Demo.ps1` starts Mosquitto, TimescaleDB and Grafana (`docker compose up`), and **Start all** starts the historian with the other processes.
3. Open Grafana at `http://127.0.0.1:3000` (the **Grafana** button in the console) and sign in with `GRAFANA_ADMIN_USER` / `GRAFANA_ADMIN_PASSWORD`. The **Edge history** dashboard is the home page.

| Service | Address | Notes |
|---|---|---|
| TimescaleDB | `127.0.0.1:5433` | 5433, so it does not clash with a locally installed Postgres. User `HISTORY_DB_USER`, database `HISTORY_DB` |
| Grafana | `127.0.0.1:3000` | Anonymous access and sign-up are off |

Grafana connects as `grafana_reader`, a login that can only read the history tables. It is created by `history/init/10-grafana-reader.sh` when the TimescaleDB volume is first initialized. **Reset demo** (`docker compose down -v`) deletes the history volume too.

## What the historian stores

| Table | One row per | Columns |
|---|---|---|
| `readings` (hypertable, 1-day chunks) | metric per reading | `time, node, device, metric, value, quality, historical, device_seq`; key `(node, device, metric, time)` |
| `device_events` (hypertable) | observed birth or death | `time, node, device, kind, event_order` (NBIRTH, NDEATH, DBIRTH, DDEATH; `device` is empty for node events) |
| `readings_1m` (continuous aggregate) | metric per minute | `bucket, node, device, metric, avg, min, max, samples, bad_samples` |

- `node` is `group/edge node`, e.g. `warehouse-01/gateway-01`, so several gateways or sites can share one database.
- **Reading times are measurement times** from the Sparkplug payload. Replayed samples keep their original time. **Lifecycle times are arrival times:** a DBIRTH containing an old saved sample still announces the device now. The online/offline timeline shows what the historian observed, not the exact outage start while it was disconnected. `event_order` resolves identical event timestamps.
- **No duplicate readings:** conflicting readings keep their original values, timestamps and quality; only `historical` can become true if the sample is later replayed. New reconnect births still create lifecycle events. Lifecycle events are recorded in observed order without session filtering.
- **Replayed device samples:** the stat counts distinct `(node, device, device_seq, time)` samples, not individual metric rows. Three metrics in one sample count once, including across retries. It follows the selected measurement-time range and device/metric filters; Demo Console's counter instead covers messages seen since its last reset/restart. Compare matching devices and periods. The live/replayed metric chart and readings-stored stat continue counting metric values.
- **Latest value:** shows metrics from each device's latest complete sample within the last 24 hours, then applies the metric filter. Removed metrics remain in history but disappear here after the next sample. Bad/null values do not fall back to older Good values. This relies on the current gateway sending a complete metric set per sample.
- Migration `003_remove_lifecycle_session.sql` removes the earlier experimental session column and its lookup index, preserving history.
- Migration `002_lifecycle_order.sql` applies automatically on historian startup and preserves existing data. Previously misdated lifecycle events cannot be accurately repaired because their arrival times were not recorded; the corrected timeline applies to newly received events. Restart the historian with the updated build and reload the provisioned Grafana dashboard after deployment.
- Booleans are stored as 1/0. A bad or stale metric is stored with `value` NULL and its quality. Text metrics and node data (NDATA) are not stored. `DeviceSeq` is a column, not a metric.

## Nothing is lost

| Outage | What happens |
|---|---|
| Broker down | The gateway buffers in its outbox and replays with `historical = true` |
| Historian down | It has a persistent MQTT session (client ID `historian`, clean session off, QoS 1), so Mosquitto queues its messages (`max_queued_messages 100000`) |
| TimescaleDB down | The historian acknowledges a message only after it is committed. If a write fails it disconnects without acknowledging, and the broker delivers the message again on reconnect |
| TimescaleDB not yet started | The historian retries the schema setup with backoff instead of exiting |

Verified live with one device polled every 3 s: each outage (the broker for about 60 s, the historian for 45 s, TimescaleDB for 30 s) left every DeviceSeq exactly once, and no gap longer than the poll interval.

## Retention, compression, rollups

Historian settings, applied at every start (policies are replaced, so a changed value takes effect after a restart):

| Setting | Default | Effect |
|---|---|---|
| `Historian:CompressAfterDays` | 7 | Chunks older than this are compressed (per `node, device, metric`), roughly 10–20× smaller |
| `Historian:RawRetentionDays` | 90 | Raw readings older than this are dropped |
| `Historian:RollupRetentionDays` | 730 | 1-minute rollups older than this are dropped |

The rollup refreshes every minute over the last 7 days, which also takes in late readings replayed from the outbox. The dashboard reads raw readings up to a 1-day range and the rollups beyond that, so long ranges stay fast.

The schema is created by numbered scripts in `src/EdgeGateway/EdgeGateway.Historian/Schema/` (`001_initial.sql`, ...), applied once and recorded in `schema_version`. Add a new numbered script for any change; never edit an applied one.

## Historian settings

From `appsettings.json`, then environment variables (`Historian__ConnectionString`), then the command line.

| Setting | Default |
|---|---|
| `Historian:ConnectionString` | required. The demo console builds it from `.env` |
| `Historian:ClientId` | `historian` (its MQTT session; must be unique per historian) |
| `Historian:Topic` | `spBv1.0/#` |
| `Mqtt:Host`, `Port`, `UseTls`, `BrokerCertificatePath` | the shared broker seed in `appsettings.json` |
| `Mqtt:TlsServerName` | the name expected in the broker certificate, when it differs from `Mqtt:Host` |

The historian connects to the broker in these settings. It does not follow broker switches made in the registry; with more than one broker, run one historian per broker.

## Dashboard

`grafana/dashboards/edge.json`, loaded by `grafana/provisioning/`. Filters: gateway, device, metric.

- **Readings:** average per interval.
- **Latest value**, **Bad or stale readings**, **Replayed from outbox**, **Readings stored**.
- **Device online / offline:** from births and deaths.
- **Live vs replayed readings:** per interval. After a broker outage the gap fills with replayed (orange) bars.
- **Latest bad or stale readings:** a table.

Dashboards are files in the repository and cannot be saved from the UI. To change one: **Make editable** → edit → **Export → Save to file**, then replace `grafana/dashboards/edge.json`.

## Deploying on a server

```text
Edge box (per site)                     Server / VM
┌──────────────────────────┐            ┌──────────────────────────────────────────┐
│ Modbus devices           │            │ Mosquitto (TLS)                          │
│ EdgeGateway + SQLite     │──MQTT/TLS─►│ Historian ─► TimescaleDB ─► Grafana      │
│ (outbox, offline-safe)   │            │ Configuration service                    │
└──────────────────────────┘            └──────────────────────────────────────────┘
```

- `docker compose --profile server up -d` also runs the historian as a container (`src/EdgeGateway/EdgeGateway.Historian/Dockerfile`), configured entirely by environment variables.
- **TimescaleDB:** self-host the container with a volume. Managed Postgres services usually ship only the Apache edition of TimescaleDB, without compression or continuous aggregates. Back it up with `pg_dump` (e.g. `docker compose exec timescaledb pg_dump -U historian -Fc history > history.dump`; restore with `pg_restore` after `SELECT timescaledb_pre_restore()`).
- **Grafana:** put it behind HTTPS (a reverse proxy or Grafana's own TLS) and change the admin password. The ports in `compose.yaml` are bound to `127.0.0.1`; publish them only through the proxy.
- **Broker certificate:** the local certificate is issued for `localhost`, so the compose historian sets `Mqtt__TlsServerName=localhost`. A real broker certificate names its own host, so drop that setting.
