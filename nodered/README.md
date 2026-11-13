# Node-RED device registry and Modbus poller

A Node-RED version of the IoT project's acquisition path. You manage devices, metrics and the MQTT broker in a web page. Node-RED polls the devices over Modbus TCP, stores each reading in a durable outbox, and publishes it as JSON to MQTT. Adding a device needs no new flow nodes.

## Run it with Docker (recommended)

With Docker Desktop installed, run this in the `nodered` folder:

```
docker compose up -d
```

Then open `http://127.0.0.1:1880/register`. Nothing else needs installing; see [DOCKER.md](DOCKER.md).

## Set up without Docker

1. In the `.node-red` directory, install the packages the flow uses: `npm install node-red-node-sqlite@2.0.1 modbus-serial@8.0.25 mqtt@5.16.1`. `sqlite3` comes with `node-red-node-sqlite`.
2. Back up `C:\Users\aarij\.node-red\flows.json`, then import `nodered/flows.json` in the editor (**Menu → Import**). If the tabs already exist, choose **Replace**; **Import copy** would create duplicates with new node IDs.
3. On another machine, update the paths. Double-click each tab and edit its **Environment variables**: `GATEWAY_DB`, `GATEWAY_STATE_DIR` and `MQTT_CA` on Modbus to MQTT; `REGISTRY_DB` and `SEED_BROKER_CERTIFICATE` on Device Registry. Also change the **Device Registry DB** config node to the same file as `REGISTRY_DB`.
4. Deploy. Function nodes load these modules through `functionExternalModules: true` in `settings.js`, which is the default.

`node-red-contrib-modbus` and the old `node-red-contrib-iot-registry-poller` are no longer used and can be uninstalled.

## What the flow does

- The **Device Registry** tab owns device definitions: a SQLite `Devices` table, a CRUD API at `/api/devices`, and an editor page at `http://127.0.0.1:1880/register`.
- The **Modbus to MQTT** tab reads the registry every 10 seconds, and immediately after any successful save, enable/disable or delete. It re-plans only when the device list actually changes.
- A scheduler checks each device's poll interval, plans frames by Modbus area and address, and sends each due device's frames to the reader.
- The **Read frame (one connection per endpoint)** node keeps one TCP connection per `host:port`. Devices that share an endpoint (several unit IDs behind a gateway or ModSim) share its connection, and their requests go one at a time; different endpoints poll in parallel. Each frame returns data or an error within the 3 s connect or 2 s request timeout, so an unreachable device never holds up the others.
- A refused or closed connection is reopened on the next poll. A Modbus exception or timeout from one unit keeps the connection open for its neighbours.
- **Decode, scale, and format** decodes each frame and combines all of a device's frames into one reading.
- **MQTT gateway (outbox, broker connection)** stores the reading in `nodered/state/gateway.db` with the device's next sequence number. It publishes readings oldest first to `iot/<device-id>/telemetry` and deletes each one only after the broker's PUBACK. While the broker is down, readings are buffered and outages recorded, and the backlog replays in order on reconnect. Broker settings come from the registry and apply live, with a trial and rollback. See [RELIABILITY.md](RELIABILITY.md).

Frame planning follows the .NET gateway's rules:
- Each Modbus area is grouped separately.
- Points with up to eight addresses between them share a frame.
- Reads are capped at 125 registers or 2000 bits.

Register types are `int16`, `uint16`, `int32`, `uint32` and `float32`; coils and discrete inputs use `bool`. Each metric's word order, scale and unit are applied.

## Device status

Each enabled device has a state, following the .NET `ModbusPoller` rules:

| State | Meaning | Published |
| --- | --- | --- |
| `connecting` | Not polled yet | Nothing |
| `polling` | The last poll was answered with no errors | Readings |
| `failing` | Errors, but still counted online: some frames failed, values were bad, or fewer than 3 polls in a row went unanswered | Readings with bad-quality metrics |
| `offline` | 3 polls in a row went unanswered (`FAILED_POLLS_BEFORE_OFFLINE`) | One `offline` message, then nothing until it answers again |

Rules:
- **What counts as an answer.** A poll counts as answered when any frame got a reply. That includes Modbus exception codes 1–6 and 8, which are sent by the device itself. Timeouts, refused or lost connections, and gateway exceptions 10 and 11 don't count. After a frame goes unanswered, the rest of that poll is skipped.
- **Coming online.** The first answered poll publishes `{"state":"online"}` to `iot/<id>/status`, then the reading. These status messages are the JSON counterparts of Sparkplug DBIRTH and DDEATH. They go through the outbox, so they stay in order with the readings and survive broker outages.
- **Never online.** A device that has never answered publishes nothing; its status still shows why.
- **Disabled or deleted.** An online device that is disabled or deleted publishes `{"state":"offline","reason":"not enabled in configuration"}` once. This also happens after a restart, if it was disabled meanwhile. A poll that finishes after the device was disabled is dropped, so it can't bring the device back.
- **Starting over.** Editing a device, redeploying or restarting starts its state over (`connecting`), like a new .NET poller. Whether it was last announced online or offline is kept in `gateway.db`.

`GET /api/gateway/status` lists every enabled device under `devices`, with the same fields as the .NET status: `state`, `endpoint`, `frames`, `lastSeq`, `failures`, `lastPollAt`, `lastSuccessAt`, `lastError`, `lastRaw` and `lastValues`. The device table on `/register` shows the same information under each device, refreshed every 3 s.

## Add devices

Use `http://127.0.0.1:1880/register`. For ModSim, enter:
- host `127.0.0.1` and the simulator's TCP port (usually 502);
- the ModSim window's Device ID as the Unit ID;
- 0-based addresses within each area: holding register 40001 is Holding address 0.

ModSim answers exception 2 for any address that no open window covers.

Devices in the .NET project's Configuration API are not imported; re-enter them here.

## Run and inspect

Start the project Mosquitto broker; the Configuration API is not needed. If Mosquitto is down, readings wait in the outbox until it returns. Stop the .NET gateway while Node-RED polls the same devices, otherwise each device is read twice.

In the editor's Debug sidebar:
- **Published telemetry** shows each reading once the broker has acknowledged it, with its `seq` and `historical` flag.
- **Gateway status** shows connection, broker switch, rejection and replay events.
- **Registry / poll status** shows configuration changes and errors.

The **MQTT broker** section of `/register` shows the gateway's live state, and so does `GET /api/gateway/status`.

For a step-by-step walkthrough with MQTTX and the test Modbus server, see [test/MANUAL-TESTING.md](test/MANUAL-TESTING.md). The automated tests are in [test/README.md](test/README.md).

## Current limits

- Requests on one endpoint go strictly one at a time. An Ethernet-native server that can answer several requests at once is not used to its full capacity.
- Readings are published as ordinary JSON, not Sparkplug B, so the .NET historian doesn't store them.
- Device and metric history, permanent metric IDs and interfaces are not part of this registry.
- The Demo Console's online/offline panel reflects the .NET gateway, not Node-RED.
