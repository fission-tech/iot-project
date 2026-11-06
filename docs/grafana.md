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
| `device_catalog` | device | `node, device, uid` (its dashboard), `in_registry`, `first_seen, last_seen` |
| `metric_catalog` | metric | `node, device, metric` (permanent key), `name` (current), `registry_id`, `kind`, `register, data_type, unit, scale`, `in_registry` |
| `metric_names` | name a metric has had | `node, device, name, metric` (key) |

- **`metric` in `readings` is the metric's permanent key, not necessarily its current name.** Join `metric_catalog` for the name (see [Metric identity](#metric-identity-renames-keep-history)).

- `node` is `group/edge node`, e.g. `warehouse-01/gateway-01`, so several gateways or sites can share one database.
- **Reading times are measurement times** from the Sparkplug payload. Replayed samples keep their original time. **Lifecycle times are arrival times:** a DBIRTH containing an old saved sample still announces the device now. The online/offline timeline shows what the historian observed, not the exact outage start while it was disconnected. `event_order` resolves identical event timestamps.
- **No duplicate readings:** conflicting readings keep their original values, timestamps and quality; only `historical` can become true if the sample is later replayed. New reconnect births still create lifecycle events. Lifecycle events are recorded in observed order without session filtering.
- **Replayed device samples:** the stat counts distinct `(node, device, device_seq, time)` samples, not individual metric rows. Three metrics in one sample count once, including across retries. It follows the selected measurement-time range and device/metric filters; Demo Console's counter instead covers messages seen since its last reset/restart. Compare matching devices and periods. The live/replayed metric chart and readings-stored stat continue counting metric values.
- **Latest value:** shows metrics from each device's latest complete sample within the last 24 hours, then applies the metric filter. Removed metrics remain in history but disappear here after the next sample. Bad/null values do not fall back to older Good values. This relies on the current gateway sending a complete metric set per sample.
- Migration `003_remove_lifecycle_session.sql` removes the earlier experimental session column and its lookup index, preserving history.
- Migration `002_lifecycle_order.sql` applies automatically on historian startup and preserves existing data. Previously misdated lifecycle events cannot be accurately repaired because their arrival times were not recorded; the corrected timeline applies to newly received events. Restart the historian with the updated build and reload the provisioned Grafana dashboard after deployment.
- Booleans are stored as 1/0. A bad or stale metric is stored with `value` NULL and its quality. Text metrics and node data (NDATA) are not stored. `DeviceSeq` is a column, not a metric.

## Metric identity: renames keep history

A metric has a **permanent ID** and a **name**, like an OPC UA `NodeId` and its display name. History is stored against the ID, so renaming a metric only changes its label.

```text
Registry   metric { id: 8b75…, name: "OutletValve" }       a rename changes the name, never the id
Gateway    DBIRTH metric "OutletValve" + property metricId = 8b75…   (DDATA carries names only)
Historian  metricId → key "Valve" (permanent) → readings.metric = "Valve"; metric_catalog.name = "OutletValve"
Grafana    filters by key, shows the current name
```

- **Registry:** assigns every metric an `id` (a GUID) and keeps it. The console's device form sends it back, so renaming a metric in the form keeps its history. A metric saved without an `id` keeps the ID of the stored metric with the same name, or gets a new one. Metrics stored before IDs existed get theirs at startup.
- **Sparkplug:** metric names stay on the wire for every other subscriber. The ID is an extra `metricId` string property on each DBIRTH metric; consumers that do not know it ignore it. The gateway stores the IDs with each reading in its outbox, so a birth replayed after an outage names the IDs that applied when the reading was taken.
- **Historian:** stores readings under a **key** that never changes. `metric_catalog` holds the key (`metric`), the current `name` and the `registry_id`. `metric_names` maps every name a key has had, because DDATA carries only names and readings replayed after a rename still use the old name.
  - A birth metric is matched by `registry_id` (a different name is a rename). Otherwise, a metric already known by that name without an ID **adopts** it. Otherwise it is new and keyed by its ID.
  - The registry pass every 60 s also adopts IDs by name and applies renames, so a rename shows before the gateway's next birth.
  - Edge nodes that send no IDs are keyed by name, as before.
- **No history is rewritten:** a metric that had history before migration `005_metric_identity.sql` keeps its old name as its key. Verified live: a rename, then another rename during a 30 s broker outage, kept every sample on one key with no duplicates and no gaps.

Limits:
- History already split by a rename made before IDs existed stays split. It shows under "Removed metrics · history only".
- A name reused by a different metric maps readings replayed under that name to the newest metric using it.

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
| `Historian:DashboardDirectory` | empty, so no device dashboards. The demo console passes `grafana/generated`; the compose historian uses `/dashboards` |
| `Historian:ConfigUrl` | empty, so metrics are only described as boolean or numeric. The demo console passes the configuration service |
| `Historian:DashboardRefreshSeconds` | `60`: how often the registry is re-read and dashboards are checked |

The historian connects to the broker in these settings. It does not follow broker switches made in the registry; with more than one broker, run one historian per broker.

## Dashboards

### Fleet overview (all devices)

`grafana/dashboards/edge.json`, loaded by `grafana/provisioning/`. Filters: gateway, device, metric.

- **Devices:** every device seen, with status (Online, Offline, or Removed from the registry), last reading, metric count and bad readings in range. Click a device to open its own dashboard.
- **Device dashboards:** a list of the generated per-device dashboards.
- **Readings:** average per interval.
- **Latest value**, **Bad or stale readings**, **Replayed from outbox**, **Readings stored**.
- **Device online / offline:** from births and deaths.
- **Live vs replayed readings:** per interval. After a broker outage the gap fills with replayed (orange) bars.
- **Latest bad or stale readings:** a table.

The overview is a file in the repository and cannot be saved from the UI. To change it: **Make editable** → edit → **Export → Save to file**, then replace `grafana/dashboards/edge.json`.

### One dashboard per device (generated)

The historian writes one dashboard per device to `grafana/generated/devices/<uid>.json` (not in Git). Grafana shows them in the **Devices** folder within about 10 s. Nobody edits them: they are rewritten from the catalog.

```text
DBIRTH ─► historian ─► device_catalog / metric_catalog (TimescaleDB) ◄─ configuration service GET /devices (every 60 s)
                              │ render per device; write only if changed
                              ▼
                 grafana/generated/devices/dev-<device>-<hash>.json ─► Grafana folder "Devices"
```

- **When:** at historian start, after every DBIRTH (new device, new or removed metric), and every `DashboardRefreshSeconds`. The periodic pass catches registry edits that cause no rebirth, such as a unit change.
- **Metric description:** a birth says whether each metric is boolean or numeric. The registry adds register area, data type, unit and scale. If the registry is unreachable, the last known description is kept.
- **Layout:** a **Status** row (online/offline timeline, last reading, readings, bad/stale, replayed), then one row per register area with panels per metric, then **Data quality**. Empty rows are left out.

| Row | Metrics | Panels per metric |
|---|---|---|
| Input registers · measurements | `input` | Time series (unit, min/mean/max/last) + latest value |
| Holding registers · setpoints | `holding` | **Stepped** time series (setpoints change in steps) + latest value |
| Coils · outputs | `coil` | ON/OFF state timeline + current state + number of changes in range |
| Discrete inputs | `discrete` | Same as coils |
| Other metrics | no registry description | Time series or state timeline, from boolean vs numeric |
| Removed metrics · history only | dropped from the registry | Same panels, so the history stays visible |

- **Units and decimals:** common units map to Grafana units (`°C` → Celsius, `%`, `V`, `A`, `W`, `kW`, `kWh`, `Hz`, `bar`, `kPa`, `rpm`, ...); anything else is shown as a suffix. Integer types show the decimals their scale implies (int16 × 0.1 → 1 decimal); float32 shows 2.
- **Ranges:** raw readings up to a 1-day range, the 1-minute rollups beyond that, like the overview.
- **Removed devices:** a device deleted from the registry keeps its dashboard, titled "(removed)", and its history. A disabled device is still in the registry and is not marked. If a device disappears from the catalog (database reset), its file is deleted.
- **UID:** `dev-<device slug>-<6 hex of gateway/device>`, stable and unique per gateway. The overview links through `device_catalog.uid`.

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
