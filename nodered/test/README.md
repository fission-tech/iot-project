# Tests for the Node-RED flow

Two suites check `nodered/flows.json`:

- **Unit** (60 tests, about a second): runs the flow's Function node code straight from `flows.json`, with fake flow/context stores and a controllable clock, and checks the qbee deploy script against a fake qbee API and the staging simulator against a fake registry API. It needs no Node-RED, broker, Modbus device or qbee account.
- **End to end** (22 tests, about 2–3 minutes): drives a running Node-RED through its registry API, serves Modbus from built-in test servers, and reads the results from the MQTT broker. The reliability tests point the gateway at an in-process test broker (`aedes`) that they stop and start.

## Run

```
cd nodered/test
npm install
npm test             # unit only
npm run test:e2e     # end to end; needs Node-RED and Mosquitto running
npm run test:all     # both
```

Requirements: Node 22 or newer. For end to end, you also need:
- Node-RED with this flow deployed, at `http://127.0.0.1:1880`.
- Mosquitto with TLS at `mqtts://127.0.0.1:8883`.
- The broker CA at `broker/certs/broker.crt`, which is gitignored and generated locally.

Override any of these with `NODE_RED_URL`, `MQTT_URL`, `MQTT_CA` or `GATEWAY_DB`. `GATEWAY_DB` is the gateway's database, `nodered/state/gateway.db`, which the reliability tests read.

The end-to-end suite starts its own Modbus servers on ports 15020, 15021 and 15030, and a test MQTT broker on 18883. It uses 15029 and 18999 as ports with nothing listening. It creates devices and a broker whose IDs start with `e2e-`, and deletes them before and after the run, so it leaves your own devices alone.

The reliability tests also change shared state for a while:
- They briefly assign the gateway to the test broker.
- They set broker `local` to port 18999 for about 60 s, to see the trial and rollback.
- They redeploy the flows from storage once.

They restore `local` and the gateway's own broker setting afterwards. Stop the .NET gateway first if it polls the same devices.

## What it covers

### Unit: `unit/scheduler.test.js` (Schedule device polls)

| Area | Checked |
| --- | --- |
| Frame planning | Each Modbus area gets its own frames. Points with up to 8 addresses between them share a frame, 9 or more split it. 32-bit types take 2 registers. Frames are capped at 125 registers and 2000 bits. |
| Poll timing | A device is polled again only once its interval has passed and its previous poll has finished. Disabled devices are skipped. Only the last frame of a poll releases the device, and a stale `done` from another poll is ignored. |
| Stuck polls | A poll unfinished after 60 s reports `poll-stalled` and is restarted. |
| Configuration | The same version is ignored. A new version applies and polls at once. A malformed snapshot or a device with no metrics reports `configuration-error`. |
| Restart | The `initialize` hook clears polls that were in flight. |

### Unit: `unit/decoder.test.js` (Decode, scale, and format)

| Area | Checked |
| --- | --- |
| Data types | int16 (signed), uint16, int32/uint32/float32 in both word orders, coil and discrete as 1/0, scale with rounding, a metric's offset inside its frame |
| Combining frames | Telemetry is published once, after the last frame, on `iot/<device-id>/telemetry`, with the poll's start time |
| Errors | A failed frame marks only its own metrics bad, and the device is `failing`, with the error naming the frame and metrics. A non-finite value is a bad metric in an answered poll. A late reply for a cleared poll publishes nothing but still reports done. |
| Stray input | A message that isn't a frame result is ignored |

### Unit: `unit/device-state.test.js` (device state, like the .NET poller)

| Area | Checked |
| --- | --- |
| Start | A new device is `connecting`, with its endpoint and frame plan |
| Never online | No readings; `offline` after 3 unanswered polls, reported once |
| Failing, then offline | An online device publishes bad readings for 2 failed polls, then one offline event with the reason, then nothing |
| Recovery | The next answered poll publishes a `polling` reading and resets failures |
| What counts as an answer | Exception 2 (a device reply) keeps the device `failing` with 0 failures, forever. Exception 11 (gateway) counts as unanswered. |
| Threshold | `FAILED_POLLS_BEFORE_OFFLINE` changes it |
| Last values | The status keeps the raw words and values of the last answered poll |
| Configuration | A changed definition starts over; a removed device drops out of the status |
| Redeploy | All states start over, nothing is polled before the configuration arrives, and the frame plan is restored |

### Unit: `unit/registry.test.js` (Device Registry tab)

| Area | Checked |
| --- | --- |
| Validation | deviceId format, host (no whitespace or `/`), port 1–65535, unitId 1–247, interval ≥ 1 s, boolean `enabled`, at least one metric, unique metric names, valid area, data type that fits the area, address range including 32-bit width, word order, numeric scale, PUT ID must match the URL |
| SQL | GET, POST, PUT and DELETE build the right statement and parameters, and `enabled` defaults to true |
| Responses | 200/201/404/409/500 mapping, and a 400 passes through unchanged |
| Snapshot | Rows become devices. The version stays the same for the same content and changes when content changes. A corrupt row is skipped with a warning. |
| Reload | Only successful writes tell the poller to reload |

### Unit: `unit/qbee-deploy.test.js` (`deploy/qbee-deploy.js`)

| Area | Checked |
| --- | --- |
| Commit | Sets the image tag, the versioned Compose file and the GHCR login. Points the other parameters to qbee parameters. Sets every template parameter used in `compose.qbee.yaml`. Sets `clean` and `skip_restart`. |
| Several gateways | One project per gateway (`iot-nodered`, `iot-nodered-2`, ...), each with its number, its own ports and the simulator switch |
| Devices | Found at any depth below the group |
| Ready | Only when the Node-RED container runs the new tag and reports `healthy`. With several gateways, every one must, found by its Compose container name; gateway 10 is not taken for gateway 1. |
| Full run against a fake qbee API | Login, then upload, commit, device list and check-in, in that order. A failed check-in trigger is only logged. Waits until the device runs the new tag. |
| Failures | A device that never gets ready fails with its state. An unknown group or a wrong password stops before anything changes, and an unknown group lists the IDs that exist. A dry run calls nothing and hides the registry password. |

### Unit: `unit/simulator.test.js` (`deploy/simulator`)

| Area | Checked |
| --- | --- |
| Plant | The pump stops for the last minute in ten, with zero flow. The tank goes from 20 to 90 % and back in 30 minutes. Values stay in range for an hour. |
| Devices | The simulated pump and tank pass the registry's own validation |
| Gateway ID | Gateway n > 1 gets `-n` appended, like `settings.js` |
| Seeding | Retries while Node-RED is starting, logs in, leaves existing devices alone, and stops at once on a wrong password |

### End to end: `e2e/registry-api.test.js`

The `/register` page is served. Create, read, update and delete through `/api/devices` all work. Error statuses are 400 (invalid), 409 (duplicate), 400 (ID mismatch) and 404 (unknown on PUT and DELETE). `°C` comes back unchanged.

### End to end: `e2e/polling.test.js`

| Scenario | Checked |
| --- | --- |
| All areas and types | holding int16 with scale, float32, negative int16, input uint16, coil, discrete. Values arrive correct over MQTT. Holding 0–10 is read as one frame. |
| Shared endpoint | Two unit IDs on one host:port use exactly one TCP connection |
| Device status | Online announced on `iot/<id>/status`. The status API shows the endpoint, frame plan, `lastSeq`, failures, last error, last success, raw registers and values. |
| Exception 11 | Publishes nothing, not even offline. `offline` in the status after 3 polls. Neighbours stay `polling`, and no reconnect happens. |
| Exception 2 | Counted as an answer: `failing` with bad values and 0 failures, never offline |
| Silent unit | Times out, and the rest of the poll is skipped (no input request is ever sent). `offline` after 3 polls, and another endpoint keeps its 1 s cadence. |
| Nothing listening | `ECONNREFUSED` within seconds; offline after 3 polls, nothing published |
| Live edit | Changing a scale through the API takes effect on the next poll, with no redeploy |
| Disable and delete | A disabled online device publishes one offline message ("not enabled in configuration"), stops, and leaves the status. A deleted never-online one just stops. |
| Endpoint loss | Bad `failing` readings, then one offline message, then silence. When the server returns: online, then `polling` readings on a single connection. |

### End to end: `e2e/reliability.test.js`

| Scenario | Checked |
| --- | --- |
| Broker validation | 400 for TLS without a certificate, an http or unpinned certificate URL, a bad fingerprint, certificate fields without TLS, `/` in the ID, port 0, keep-alive under 5 s, maximum retry below the retry delay, and unknown fields. 404 for a missing broker. 400 when assigning an unknown broker. |
| Live switch | Assigning the gateway to a new broker in the registry moves it there within seconds (`source: registry`). Duplicate IDs get 409, and deleting an assigned broker gets 409. |
| Sequence numbers | Each device's `seq` goes up by one per reading, with `historical: false` while connected |
| Outage and replay | Stopping the broker buffers readings. One `BrokerHistory` row opens, and retries don't add more. Buffered rows link to it. After restart, the backlog replays as `historical: true` before live readings, with no gaps in `seq`. The row then records reconnect time and downtime, and `BufferedMessagesSent` = `MessagesBuffered`. |
| Redeploy during an outage | Buffered readings survive and `seq` continues without reuse. The new run opens its own outage row and the old one stays unfinished, but its messages still count when sent later. |
| Back to its own broker | Removing the assignment returns the gateway to `BROKER_ID` |
| Gateway online/offline | Online is announced retained on `iot/gateway/<id>/status`. A clean broker switch announces offline ("switching to new broker settings") before leaving. Cutting the connection through a TCP proxy, with no MQTT DISCONNECT, makes the broker publish the retained Last Will ("connection lost"), and the gateway announces online again after reconnecting. |
| Trial and rollback | A port with nothing listening is tried for the trial window, then rejected with the error, and the gateway runs on the last working settings (`source: last-good`). Saving the original settings again clears the rejection. |

## Not covered

- **A device disabled while Node-RED is stopped.** Its offline message on the next start is implemented, but not tested end to end.

- **Real ModSim or hardware.** The test servers follow the Modbus TCP framing but are not a certified implementation.
- **Delay on a shared endpoint.** A silent unit on the *same* host:port as healthy units delays them by up to the 2 s timeout per frame. This is expected (see the design doc) and isn't asserted.
- **A host that silently drops packets** (the 3 s connect timeout). Testing it needs a non-routable address, which behaves differently from one network to another.
- **The registry page in a browser.** Only the API behind it is tested; the editor's own JavaScript isn't.
- **SQLite failures** such as a locked or missing database file, beyond the 500 mapping.
- **Certificates over https with a pinned fingerprint.** Downloading and caching aren't tested, because that needs an https server with a trusted certificate.
- **Storage failures in `gateway.db`,** such as a locked or full disk, and the in-memory retry that follows.
- **A crash between a PUBACK and its delete,** which is the duplicate-delivery case. Repeats are allowed by the contiguity checks but never forced.
- **The poller's online/offline state machine** (the .NET gateway's offline after N failed polls). The flow doesn't have one yet.
