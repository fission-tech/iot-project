# IoT Project

Requires .NET 8 SDK and Docker Desktop. MQTTX or MQTT Explorer to watch the broker.

```text
EdgeGateway.Configuration (device registry, REST :5300)
        │ device list (fetched every 10 s, cached locally)
        ▼
Modbus devices ◄── Modbus TCP polling ── EdgeGateway.Gateway [poll → SQLite outbox → Sparkplug B]
(ModSim, PLCs, any Modbus TCP server)               │ MQTT over TLS
                                                    ▼
                                   Mosquitto (:8883) ──► MQTTX / MQTT Explorer, demo console
```

- **Devices:** any Modbus TCP server, such as ModSim or a PLC. Nothing is built in: a device exists only once it is registered in the configuration service, and its values change on the device itself.
- **Configuration service:** stores shared connection settings in `Interfaces` (`Id`, `Type`, JSON `Settings`). `Devices` references `InterfaceId` and keeps unit ID, polling interval, enabled status and metrics in JSON `Data`. `GET/POST/PUT/DELETE /interfaces` and `/devices`; `GET /config` resolves complete TCP polling definitions for the gateway/cache. It holds configuration only; online/offline comes from the gateway. It starts empty.
- **Gateway (one process):** one poll loop per enabled device. Each reading is numbered (persisted per device) and saved to the SQLite outbox before publishing. Committed readings survive a gateway crash or a broker outage. The publishing loop drains the outbox in order as Sparkplug B (NBIRTH/DBIRTH/DDATA/DDEATH, Rebirth, historical replay) and marks a row sent only after the broker's acknowledgement.
  - After 3 consecutive cycles without a target response a device is reported offline (DDEATH); a target response brings it back (DBIRTH). A rejected frame alone does not take a responding device offline.
  - Devices added, changed or removed in the registry apply within about 10 seconds; a changed metric list triggers a new DBIRTH.
  - Startup also reconciles saved device state: disabled/deleted devices get a DDEATH after any queued readings replay. An initial saved DBIRTH can still appear before that replay finishes.
  - If the configuration service is down, it keeps polling from its cached device list.

## Reading registers

Each metric in the registry says where its value lives and how to decode it:

| Field | Meaning |
|---|---|
| `register` | `holding`, `input` (16-bit registers), `coil`, `discrete` (single bits) |
| `address` | first register (0-based) |
| `dataType` | `int16` (default), `uint16`, `int32`, `uint32`, `float32` for registers; `bool` for bits |
| `wordOrder` | 32-bit types span two registers: `big` (default, high word first) or `little` (word-swapped devices) |
| `scale`, `unit` | value = decoded × scale, e.g. `0.1` for a register holding tenths |

The gateway groups each device's metrics into **frames**: one read request per run of nearby registers of the same type (up to 125 registers or 2000 bits; gaps of up to 8 registers are read through), then decodes each metric from its slice.

Addresses are 0-based. Tools such as ModSim number holding registers from 40001, so register 40001 is address 0 and 40100 is address 99.

Example: first create a shared TCP interface, then register a ModSim device with a temperature in its first holding register:

```bash
curl -X POST http://127.0.0.1:5300/interfaces -H "Content-Type: application/json" -d '{"id":"modsim-local","type":"ModbusTcp","settings":{"host":"127.0.0.1","port":502}}'
curl -X POST http://127.0.0.1:5300/devices -H "Content-Type: application/json" -d '{"id":"modsim-01","interfaceId":"modsim-local","enabled":true,"unitId":1,"pollIntervalSeconds":3,"description":"ModSim","metrics":[{"name":"Temperature","register":"holding","address":0,"dataType":"int16","scale":0.1,"unit":"°C"}]}'
```

## Configuration

Root `appsettings.json` (ignored by Git) holds shared settings; the settings classes provide the other defaults:

```json
{
  "Sparkplug": { "GroupId": "warehouse-01", "EdgeNodeId": "gateway-01", "HostId": "" },
  "Mqtt": { "Host": "127.0.0.1", "Port": 8883, "ClientId": "gateway-01", "UseTls": true, "BrokerCertificatePath": "certs/broker.crt" }
}
```

The backend (`Application.Api`) is not part of this flow; MQTTX and the demo console are the consumers. A non-empty `HostId` would make the gateway wait for a Sparkplug Primary Host's STATE, so both launchers start the gateway with an empty one.

Multiple devices can reference the same interface with their own Unit IDs and metric maps. Updating an interface bumps the configuration version and reloads affected pollers. A referenced interface cannot be deleted; reassign or remove its devices first. Removing a device keeps its interface available for reuse. TCP connection/read/write timeouts remain fixed at 2000 ms.

Configuration schema is managed by its own EF Core migrations, applied at service startup. The baseline creates fresh DBs and adopts the current shared-interface schema while preserving records and configuration version. Its context, migration history and DB remain separate from Gateway. See [Configuration migrations](docs/configuration-migrations.md) for commands and details.

`ModbusRtu` interfaces accept JSON settings such as `{"portName":"COM3","baudRate":19200,"dataBits":8,"parity":"Even","stopBits":1}`. This is **configuration-only**: serial devices must have `enabled:false`; no serial port is opened and no RTU polling runs.

This schema starts with a fresh Configuration DB; old device-registration bodies are not supported. On the initial switch, stop the applications and remove only the old Configuration DB and gateway `config-cache.json`. For the demo these are `logs/demo/state/configuration.db` (including any `-wal`/`-shm` sidecars after stopping) and `logs/demo/state/daq/config-cache.json`. Keep `outbox.db`, sequence files and node state. The configuration service reports an explicit error if pointed at the old schema. Do not use **Reset demo** for this transition: it also removes the outbox and broker state.

## Demo console (recommended)

```powershell
./scripts/New-LocalBrokerCertificate.ps1   # first setup only
./scripts/Start-Demo.ps1
```

This opens `http://127.0.0.1:5200` (loopback only, because it starts and kills processes). **Reset demo** starts fresh: an empty broker, an empty device registry and an empty outbox, then the configuration service and the gateway. Registered devices are deleted too. No backend: the console reads Mosquitto itself with a persistent QoS 1 Sparkplug subscription, like MQTTX.

- **Data path:** one box per registered device with its live polling state, plus Start, Stop and Crash for the configuration service and gateway, and Start, Stop, Restart and Crash for the broker.
- **Devices being polled:** each device's state (polling, failing, offline), endpoint, last reading and last error.
- **Device registry tab:** add, edit, enable, disable or remove devices with a form covering every field, and see what the gateway is doing with each one: state, read frames, raw register values and the last error.
- **Interfaces tab:** create/edit shared TCP or configuration-only RTU settings, see the number of referencing devices, and remove unused interfaces. Add Device can select an existing interface or create one inline. A new interface and device commit together; validation/conflict errors leave neither new record saved. Device editing selects an existing interface.

For an API client, `POST /devices/with-interface` accepts `{ "device": { ... }, "newInterface": { ... } }`, using the same device and interface fields as the separate endpoints. `device.interfaceId` must match `newInterface.id`. Both IDs must be new; the configuration version increments once.
- **Activity tab:** every change in one timeline: registry edits, configuration loads, devices online, failing or offline, births and deaths, broker connection and replay, component actions.
- **Resilience test:** crashes Mosquitto, the gateway and the configuration service, then checks every reading the gateway polled arrived on Mosquitto exactly once.
- **Device cards, chart and missing/duplicate counts:** rebuilt from the Sparkplug messages on Mosquitto, using each reading's DeviceSeq.
- **On the wire:** decoded Sparkplug traffic on Mosquitto, plus the logs of each process.

## Edge only, up to Mosquitto

```powershell
./scripts/Start-IoT.ps1
```

This starts the configuration service and runs the gateway in the terminal. Register devices with the API (see above) or the demo console.

In MQTTX, connect to `127.0.0.1:8883` with TLS, trust `broker/certs/broker.crt`, use a unique client ID, and subscribe to `spBv1.0/warehouse-01/#` before starting the gateway. To decode payloads, import `schemas/sparkplug_b.proto` and select `org.eclipse.tahu.protobuf.Payload`.

## Reliability behaviour

- **Broker connection history:** the Gateway records observed outages, reconnect times, downtime and buffered/acknowledged message counts in `BrokerHistory` inside its runtime DB. Gateway schema is managed by EF Core migrations, applied at startup. The initial switch from pre-EF storage requires a fresh Gateway DB; Configuration DB is kept. See [broker-history.md](docs/broker-history.md) for migrations, schema, NULL meaning, limits and manual demo steps.

- **Automatic reconnect:** the gateway retries Mosquitto with backoff (2 s doubling to 30 s), then sends a new NBIRTH and a DBIRTH per online device.
- **Data restored after disconnects:** every polled reading is committed to the SQLite outbox first; after a reconnect the backlog is replayed in order, marked `is_historical`, and each row is marked sent only after the broker's acknowledgement.
- **Source timestamps:** a reading carries the time the poll request was sent, and keeps it through buffering and replay.
- **Storage failures:** the affected poller pauses and retries the same sample or offline event every second. Storage errors do not count as device failures. One pending sample stays in memory; stopping before it can be saved logs a warning, and that sample will be lost.
- **Data quality:** each value is Good, Bad or Stale, using the Sparkplug `Quality` metric property (192, 0, 500); a good value carries no property. A Modbus exception such as IllegalDataAddress marks only that frame's metrics Bad (null); independent frames still return their values. A transport error retains values already read in that cycle, marks failed/unattempted frames Bad, and reconnects on the next cycle. Three consecutive cycles without a target response produce DDEATH, after which consumers treat values as Stale. A float register holding NaN or infinity is reported Bad on its own. See [frame failure handling](docs/modbus-frame-isolation.md).
- **Graceful shutdown:** SIGTERM (docker stop, Kubernetes, systemd) and Ctrl+C stop polling, let the current publish finish, send NDEATH and disconnect, with a 15-second limit. Committed readings remain in the outbox if the limit is hit, and the broker publishes the NDEATH Will.
- **Not bounded yet:** the outbox has no size limit; a long outage grows it until the disk fills.

## Checks and limits

Demo diagnostics are written to `status.json` once per second. Temporary Windows file-sharing errors are retried on the next update. The console distinguishes a stopped Gateway from a running Gateway whose live status is unavailable, and keeps enabled registry devices visible in the Data Path while waiting for polling status. This diagnostic file does not carry or buffer readings.

```powershell
dotnet run --project tests/IoT.Checks
node tests/IoT.Checks/demo-status-ui.cjs
```

- **Polling gaps:** while the gateway is down, nothing is read. A poller also pauses while its pending sample cannot be saved; readings are durable only after the SQLite commit.
- **Modbus security:** Modbus has no authentication or encryption; keep devices on an isolated network. The configuration API and Mosquitto have no authentication yet.
- **Not yet supported:** Modbus RTU (serial), byte-swapped (as opposed to word-swapped) registers, 64-bit values, strings, and Sparkplug metric aliases.
- **Sparkplug deviations:** data uses QoS 1, and the demo console subscribes with a persistent session.
- **Console view resets with the console:** after a console restart it counts from the readings it receives next, so earlier readings are not in its totals.
- **Backend:** `Application.Api` stays in the repository but is outside this flow; its checks still expect the fridge metric names.
