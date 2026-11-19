# Deploying the Node-RED gateway with qbee.io

On an edge device, the gateway runs as a Docker Compose project that qbee.io installs and keeps running. [compose.qbee.yaml](compose.qbee.yaml) is that project. It differs from [../compose.yaml](../compose.yaml), which is for running on your own computer:
- **Nothing is built on the device.** It downloads images from GHCR: `iot-nodered`, `iot-nodered-broker` (Mosquitto with its configuration), `iot-nodered-certs` and, on staging, `iot-nodered-sim` (the simulated plant).
- **The broker certificate is kept in a Docker volume,** not in a folder on the host.
- **Logins are on.** One user name and password open the Node-RED editor, the `/register` page and the API.
- **The ports are published only on the device itself.** You reach them through qbee.

[qbee-deploy.js](qbee-deploy.js) deploys a version to a qbee group and waits until every device in it runs that version. The GitHub workflows run it, and you can also run it by hand.

## The pipeline

| Workflow | Runs on | Does |
| --- | --- | --- |
| [Node-RED CI](../../.github/workflows/nodered-ci.yml) | Pull requests and pushes to `feature/node-red` that touch `nodered/` | See the steps below |
| [Node-RED deploy](../../.github/workflows/nodered-deploy.yml) | Called by CI, or run by hand | Deploys one image tag to the staging group |

**What CI does:**
1. Runs the unit tests and checks that the flow converts for Docker and that the device Compose file is valid.
2. Starts the stack and runs the end-to-end tests.
3. On pushes to `feature/node-red` and on `nodered-v*` tags, builds the four images for amd64 and arm64, pushes them to GHCR and scans the Node-RED image for critical vulnerabilities.
4. On pushes to `feature/node-red` only, calls the deploy workflow for staging with the tag `sha-<7 characters of the commit>`.

**For now only staging is deployed.** The canary and production jobs are commented out in the deploy workflow. To turn them on:
1. Uncomment both jobs.
2. Create their GitHub environments, with required reviewers.
3. Choose what triggers them, for example a `nodered-v*` tag.
4. When the Node-RED variant is merged, change the CI branch from `feature/node-red` to `main`.

**Running deploy by hand.** This is how to roll back, by deploying an earlier tag. GitHub only shows **Run workflow** for workflows that are on the default branch, so until this is merged, start it from the command line:

```
gh workflow run nodered-deploy.yml --ref feature/node-red -f tag=sha-1a2b3c4
```

This may also need the workflow on the default branch. If GitHub refuses, push a commit that brings back the version you want instead.

**How a step works:**
1. It uploads the version's Compose file as `/iot-nodered/compose-<tag>.yaml`.
2. It commits the group's Docker Compose configuration with the new `IMAGE_TAG`: one Compose project per gateway on the device (see [Several gateways per device](#several-gateways-per-device-and-the-simulated-plant)).
3. It asks each device to check in now. This needs remote access on the device; without it, the device picks the change up on its next regular check-in.
4. It waits until the Node-RED container of every gateway on each device runs the new image and reports `healthy`.

If a device isn't ready in time, the step fails and later environments don't run. Devices keep their data across updates and rollbacks: devices, unsent readings, sequence numbers and history. These live in the `nodered-state` and `mosquitto-data` volumes.

Only one deployment runs at a time. A newer one waits rather than cancelling one in progress.

## One-time setup

### Devices

- Linux on amd64 or arm64.
- Docker Engine with the Compose plugin. Check with `docker compose version`.
- The qbee agent, registered with your qbee account.
- Remote access turned on, so the pipeline can trigger a check-in. This is optional, but makes deployments faster.

### Virtual staging devices (Multipass)

Instead of hardware, staging can use Multipass VMs on a Windows PC. Each is a headless Ubuntu Server with its own IP address, about 1 GB of memory and 10 GB of disk. Most of the disk goes on the 1.4 GB Node-RED image.

1. **Install Multipass** with the Hyper-V driver.
2. **Store the bootstrap key.** Copy it from qbee (**Devices → + Add device → Bootstrap a device**, the value after `--bootstrap-key`). Put it in `nodered/deploy/vm/bootstrap-key.txt`, which is gitignored. You can set the environment variable `QBEE_BOOTSTRAP_KEY` instead.
3. **Run the script** in PowerShell. Use an Administrator window if Multipass is installed inside its storage folder, which only Administrators can open.

   ```
   cd nodered\deploy\vm
   .\new-staging-devices.ps1             # qbee-staging-1
   .\new-staging-devices.ps1 -Count 3    # qbee-staging-1 to -3
   ```

   For each VM, [new-staging-devices.ps1](vm/new-staging-devices.ps1):
   - launches it, unless it already exists;
   - runs [setup-device.sh](vm/setup-device.sh) inside it, which installs Docker with Compose from Ubuntu's packages and then the qbee agent with the key.

   Running it again skips whatever is already done. Without a key, it installs only Docker.
4. **Finish in qbee.** Approve each VM under **Pending Devices**, move it into the staging group, and set its `GATEWAY_ID`.

Other commands:
- **Remove a VM:** `multipass delete --purge qbee-staging-2`. Also remove it in qbee.
- **Open a shell in one:** `multipass shell qbee-staging-1`.

### qbee

1. **Group.** Create a group for staging, for example `nodered-staging`, and put at least one device in it. Canary and production groups come later. Each group's ID is in its URL: `https://www.app.qbee.io/#/groups/<id>`.
2. **Folder.** In the **File manager**, create the folder `/iot-nodered/`.
3. **Parameters** (**Configure → Parameters**). The deployment points the Compose file's template parameters at these, so they must exist:

   | Parameter | Where | Example |
   | --- | --- | --- |
   | `NODE_RED_PORT` | Each group, or a parent group | `1880` |
   | `MQTT_PORT` | Each group, or a parent group | `8883` |
   | `NODE_RED_PASSWORD` | A **secret** on each group | A strong password. The user name is `admin`. |
   | `GATEWAY_ID` | **Each device** | `site-a-gw-01`. Must be unique; the gateway's MQTT status topic is `iot/gateway/<id>/status`. |

   Two notes on the password:
   - **Avoid `$` in the password.** Docker Compose treats `$` as the start of a variable; if you need one, write it as `$$`.
   - **Root on the device can see it.** It's passed to the container as an environment variable, so `docker inspect` shows it.
4. **Pipeline account.** Create a qbee user for the pipeline, without two-factor sign-in. The API's two-factor challenge step isn't supported by the script. It needs permission to manage configuration and files and to read devices. To trigger check-ins, it also needs `remote-access:connect`.

### GitHub (repository settings)

1. **Environment.** Under **Environments**, create `staging`. When canary and production are turned on, create those too, with **Required reviewers**.
2. **Settings for each environment.** Give each environment:
   - **Variable `QBEE_GROUP_ID`:** that environment's qbee group ID.
   - **Variable `QBEE_INSTANCES` (optional):** gateways per device, from 1 to 10. Without it, 1.
   - **Secrets `QBEE_EMAIL` and `QBEE_PASSWORD`:** the pipeline account.
   - **Secrets `GHCR_USERNAME` and `GHCR_TOKEN`:** a GitHub user and a token with only `read:packages`. Devices use it to download the images. The workflow's own `GITHUB_TOKEN` can't be used, because it expires after the run.
3. **Package access.** The first CI run on `feature/node-red` creates the GHCR packages; `iot-nodered-sim` arrives with the first run after it was added. They're private by default. If the token's user can't read them, grant it access under each package's settings.

## Releasing

For now, every push to `feature/node-red` that passes CI goes to staging. Check it there, for example through remote access. Release tags, canary and production come once those jobs are turned on.

## Running a deployment by hand

```
QBEE_EMAIL=... QBEE_PASSWORD=... GHCR_USERNAME=... GHCR_TOKEN=... \
  node nodered/deploy/qbee-deploy.js --group <qbee group id> --tag v1.2.0
```

Options:
- `--instances N` runs N gateways on each device. The default is 1.
- `--simulator` runs the simulated plant next to each gateway. Staging always uses it; leave it off for real devices.
- `--dry-run` prints the commit, with the registry password hidden, and changes nothing.
- `--wait-minutes` sets how long to wait for devices. The default is 20.

## Remote access

The device needs no open ports and no VPN. qbee tunnels a local port on your computer to a port on the device.

```
qbee-cli connect -d <qbee-device-id> -t 1880:localhost:1880,8883:localhost:8883
```

Then:
- **Node-RED:** open `http://localhost:1880` and log in as `admin`.
- **MQTT:** connect MQTTX to `mqtts://localhost:8883`.
- **The device's own certificate,** to trust it in MQTTX: on the device, run `docker compose -p iot-nodered cp certs:/certs/broker.crt .`. Or use the qbee terminal.

The qbee-connect desktop app does the same thing with a graphical interface. Use a **custom** port.

Gateway 2 and later use their own ports (see below), so tunnel those instead, for example `-t 1881:localhost:1881` for gateway 2.

## Several gateways per device, and the simulated plant

A VM with 1 GB of memory is enough for several gateways, so a few VMs can stand in for a bigger fleet.

**Several gateways.** `--instances N` (in the pipeline, the variable `QBEE_INSTANCES`) makes each device run the Compose file N times, as separate projects. Each has its own broker, data and ports:

| Gateway | Compose project | `GATEWAY_ID` | Node-RED port | MQTT port |
| --- | --- | --- | --- | --- |
| 1 | `iot-nodered` | the device's `GATEWAY_ID` | qbee `NODE_RED_PORT` | qbee `MQTT_PORT` |
| 2 | `iot-nodered-2` | `<GATEWAY_ID>-2` | 1881 | 8884 |
| n | `iot-nodered-n` | `<GATEWAY_ID>-n` | 1879 + n | 8882 + n |

- Gateway 1 is exactly what a device ran before, so turning this on changes nothing for it.
- Keep `NODE_RED_PORT` and `MQTT_PORT` at 1880 and 8883 so gateway 1 doesn't collide with the others.
- Lowering N removes the extra projects and their containers on the next deploy. Their Docker volumes stay on the device until removed by hand.
- Each Node-RED container uses about 100 to 150 MB of memory. On a 1 GB VM, stay at 4 or fewer.

**The simulated plant.** Staging VMs have no Modbus devices, so with `--simulator` each gateway also gets two containers from the `iot-nodered-sim` image:
- `modbus-sim` serves a pump (unit 1: temperature, flow, run time, running) and a tank (unit 2: level, high level) whose values change over the minutes. The pump stops for one minute in every ten. The code is in [simulator/simulator.js](simulator/simulator.js).
- `sim-seed` registers them with the gateway as `<gateway ID>-pump` and `<gateway ID>-tank`, then exits. Devices that already exist are left as they are, so your edits survive redeploys. The code is in [simulator/seed.js](simulator/seed.js).

Readings then appear on the gateway's broker as `iot/<gateway ID>-pump/telemetry` and `iot/<gateway ID>-tank/telemetry`. To watch them, open the device's terminal in qbee and run:

```
docker exec iot-nodered-mosquitto-1 sh -c 'mosquitto_sub -h localhost -p 8883 --cafile /mosquitto/certs/broker.crt -t "iot/#" -v'
```

For gateway 2, use `iot-nodered-2-mosquitto-1`. Without `--simulator`, both containers are defined but not started.

## The qbee API calls used

The full reference is at https://www.app.qbee.io/api/v2.

| Call | Used for |
| --- | --- |
| `POST /api/v2/login` | Returns a token, sent as `Authorization: Bearer <token>` |
| `POST /api/v2/file` | Uploads the Compose file (multipart: `path`, `file`, `replace`) |
| `POST /api/v2/commit` | Applies the changes in its `changes` list all at once. The older `POST /api/v2/change` is deprecated. |
| `GET /api/v2/grouptree/{id}` | Lists the group's devices |
| `GET /api/v2/device/{id}/run-agent` | Makes a device check in now |
| `GET /api/v2/inventoryreport/docker/{id}` | Shows the device's containers, with name, image and status |

The commit replaces the group's whole Docker Compose configuration, including the registry login. It sets `clean`, so qbee removes Compose projects it started before that the commit no longer lists. Each project has `skip_restart`, because the `certs` and `sim-seed` containers exit when they are done; Docker itself restarts any container that crashes. The exact request is built by `buildCommit` in [qbee-deploy.js](qbee-deploy.js).

## Trying the device file on your computer

Build the images with the tag `test-local`:

```
docker build -f docker/Dockerfile --target runtime -t ghcr.io/fission-tech/iot-nodered:test-local .
docker build -f docker/Dockerfile --target broker  -t ghcr.io/fission-tech/iot-nodered-broker:test-local .
docker build -f docker/Dockerfile --target certs   -t ghcr.io/fission-tech/iot-nodered-certs:test-local .
docker build -f docker/Dockerfile --target sim     -t ghcr.io/fission-tech/iot-nodered-sim:test-local .
```

Then do what qbee does:
1. Copy `compose.qbee.yaml` somewhere else.
2. Replace each parameter with a value.
3. Run `docker compose -p qbeetest up -d` in that folder. Don't use the project name `iot-nodered`: that is also the local stack's name, so it would share the local stack's data.

Use ports that are free, such as 1891 and 8894, if the local stack is running.
