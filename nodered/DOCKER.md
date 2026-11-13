# Running the Node-RED variant in Docker

One command starts everything: Node-RED with this flow, the MQTT broker and a TLS certificate. Nobody needs Node.js, npm, Node-RED or OpenSSL installed; only Docker Desktop (or Docker Engine with Compose v2).

## Start and stop

Run these in the `nodered` folder:

```
docker compose up -d                  # build on first run, then start
docker compose ps                     # what is running
docker compose logs -f nodered        # Node-RED's log
docker compose down                   # stop; devices, outbox and history are kept
docker compose down -v                # stop and delete all data (devices, outbox, history, broker queue)
docker compose up -d --build          # after pulling changes to flows.json or the docker files
```

| What | Where |
| --- | --- |
| Device and broker page | http://127.0.0.1:1880/register |
| Node-RED editor | http://127.0.0.1:1880 |
| Gateway and device status | http://127.0.0.1:1880/api/gateway/status |
| MQTT broker (TLS) | `mqtts://127.0.0.1:8883`. Trust `nodered/state/docker/certs/broker.crt` in MQTTX. |

If these ports are taken, set others before starting, for example `NODE_RED_PORT=1890` and `MQTT_PORT=8893` in a `.env` file next to `compose.yaml`. The .NET demo's broker also uses 8883, and a Node-RED running on this machine uses 1880.

## What runs

| Service | Image | Does |
| --- | --- | --- |
| `certs` | `alpine:3.20` + OpenSSL | Creates the broker's self-signed certificate on the first start, then exits. Later starts keep it. |
| `mosquitto` | `eclipse-mosquitto:2.1.2-alpine` | The MQTT broker, with the project's `broker/mosquitto-docker.conf` (TLS on 8883, persistent queue) |
| `nodered` | `nodered/node-red:5.0.9-debian` plus this flow | The registry, poller and gateway. The npm modules the flow loads (`mqtt`, `sqlite3`, `modbus-serial`, `node-red-node-sqlite`) are installed in the image, and nothing is installed at runtime. |
| `modbus-sim` (optional) | Test tools image | The test Modbus server: `docker compose --profile sim up -d` |
| `tests` (optional) | Test tools image | The unit and end-to-end tests against this stack: `docker compose --profile test run --rm tests` |

Data:
- **`nodered-state` volume:** `registry.db`, `gateway.db` (outbox, sequence numbers, broker history) and the certificate cache.
- **`mosquitto-data` volume:** the broker's persistent queue.
- **`nodered/state/docker/certs` on this computer:** the broker certificate and key. It's gitignored, and it's on the host so MQTTX can trust the certificate.

## Devices from inside Docker

Node-RED runs in a container, so `127.0.0.1` in a device means the container itself, not your computer.

| Device runs on | Host to enter on `/register` |
| --- | --- |
| This computer (ModSim, `node test/sim.js`) | `host.docker.internal` |
| The `modbus-sim` service | `modbus-sim`, port `1502` |
| Another machine on the network | Its IP address or name, as usual |

The MQTT broker is `mosquitto:8883` inside Docker. The registry's `local` broker is seeded with that, so nothing needs changing.

## The flow in the image

The image is built from the repository's `flows.json`. `docker/prepare-flows.js` replaces only the machine-specific tab settings:

| Setting | In the container |
| --- | --- |
| `GATEWAY_DB`, `GATEWAY_STATE_DIR` | `/data/state/gateway.db`, `/data/state` |
| `REGISTRY_DB` and the **Device Registry DB** node | `/data/state/registry.db` |
| `MQTT_HOST`, `SEED_BROKER_HOST` | `mosquitto` |
| `MQTT_CA`, `SEED_BROKER_CERTIFICATE` | `/certs/broker.crt` |

The build fails if any Windows path is left in the flow.

The image is the source of truth for the flow. A change deployed from the editor inside Docker lasts until the container is recreated, for example by `up --build`. To keep such a change, export the flow (**Menu → Export**), merge it into `nodered/flows.json`, and rebuild. Changes made in the repository take effect with `docker compose up -d --build`.

## Tests in Docker

```
docker compose --profile test run --rm tests
```

This runs every unit and end-to-end test against the running stack, in about 3 minutes:
- **Test devices and the test MQTT broker** run inside the `tests` container. Node-RED reaches them at that container's address (`E2E_HOST=auto`).
- **The reliability tests** read `gateway.db` from the shared volume.
- **What changes temporarily:** the tests change `local` broker settings and redeploy the flow once, as in a local run. Everything is restored afterwards.

## Security

This is a proof-of-concept setup:
- The Node-RED editor and registry API have no login.
- The broker allows anonymous clients.
- Both are published on `127.0.0.1` only, so other machines can't reach them.

Before exposing either, add Node-RED `adminAuth` and `httpNodeAuth` in `docker/settings.js`, and broker authentication in its configuration.
