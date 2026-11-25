# .NET edge gateway on qbee

The initial PoC keeps .NET 8 and the existing Sparkplug B implementation. One qbee device represents one Linux edge computer (an Ubuntu VM for the PoC), with one independent Compose project. More computers use the same images and their own gateway identity and persistent volumes. Multiple Modbus devices are registered inside each gateway; they are not additional qbee devices.

The continuous services are `configuration` (registry API and `/register` page), `gateway` (polling, SQLite outbox and Sparkplug publishing), `mosquitto` (TLS broker), `historian`, `timescaledb` and `grafana`. `certs` initializes certificates once and exits. `modbus-sim` is optional for staging. The downstream API and demo launcher are not deployed. See the Grafana/history setup below for its additional qbee secrets.

## Prepare an edge computer

1. Create an Ubuntu x64 VM, install Docker Engine and Compose v2, and enable Docker at boot. Install/bootstrap the qbee agent and confirm the VM is online.
2. Create a **dedicated** qbee group, e.g. `dotnet-staging`, and move the VM into it. Keep Node-RED computers in their existing group. The deployment updates the target group's Docker Compose configuration; do not target a group managing another application.
3. In qbee File manager, create the folder `iot-dotnet` (no slashes in the folder-name dialog).
4. Set these qbee **Parameters** on the .NET group, with a unique gateway ID override on **each device**:

| Parameter | Example | Scope |
| --- | --- | --- |
| `DOTNET_GATEWAY_ID` | `dotnet-edge-01` | Device; unique across the fleet, including other groups |
| `DOTNET_SPARKPLUG_GROUP` | `factory-a` | Group/site |
| `DOTNET_MANAGEMENT_PASSWORD` | Your chosen password; see escaping notes below | Secret parameter; group or device |
| `DOTNET_API_PORT` | `5300` | Group; select an unused VM port |
| `DOTNET_MQTT_PORT` | `8883` | Group; select an unused VM port |

Use letters, digits, `_` and `-` for IDs. The application imposes no password length or character policy. Like the Node-RED deployment, qbee substitutes the password into Compose environment variables. Avoid `$`, or enter `$$` for a literal dollar sign; quotes and backslashes must also be escaped for the double-quoted YAML value. This raw templating does not automatically preserve arbitrary characters. Root/Docker administrators can see environment values through `docker inspect`. Even an empty password still requires the `admin` username when deployment authentication is enabled. The password is for the registry's `admin` user, not a qbee or GitHub password. Do not commit it. Gateway identity and Sparkplug group form MQTT topics: changing identity changes the published source, so keep them stable.

## GitHub configuration

The workflows use separate .NET names so existing Node-RED variables do not accidentally target its group.

- Repository variable: `DOTNET_QBEE_GROUP_ID` = the dedicated .NET group's ID. Leave it unset until the VM and parameters are ready; CI can still build/test/publish, and deployment is skipped.
- Repository secrets: `ILYAAN_QBEE_EMAIL`, `ILYAAN_QBEE_PASSWORD`, `ILYAAN_GHCR_USERNAME`, `ILYAAN_GHCR_TOKEN`. The workflow maps these existing credentials to the deployment script's `DOTNET_` environment variables; no duplicate secrets are needed. The qbee account must have access to the dedicated .NET group, and the GHCR token owner must be able to read the .NET packages.
- The GHCR token is a classic PAT with `read:packages`, belonging to a user who can read the private .NET packages. Authorize organization SSO if required. It is used by the edge computer to pull images. Publishing uses the workflow's `GITHUB_TOKEN` with `packages:write`.
- Organization policy must allow this repository to create packages. If a package with one of these names already exists, its administrator must grant this repository Write access under **Manage Actions access**. Adding a PAT alone does not change package permissions.

CI runs the existing executable checks, browser/deployment-script tests and isolated Docker integration checks for amd64 and arm64 (ARM64 through QEMU). On a push to `feature/dotnet-qbee` or `main`, it publishes five multi-platform packages: `iot-dotnet-gateway`, `iot-dotnet-configuration`, `iot-dotnet-broker`, `iot-dotnet-certs`, `iot-dotnet-simulator`. The tag is `sha-<full commit>`, never `latest`. Docker selects the computer's matching architecture. Real ARM hardware still needs its own acceptance check for physical network/equipment access.

After publishing, CI deploys to the configured .NET staging group with the simulator enabled. Each computer gets its own substituted parameters. The script refuses an empty group, commits a versioned Compose file and waits until gateway/configuration are healthy and the requested broker/simulator versions are running on every target. An offline computer causes a timeout rather than a false success. Large fleets should roll out first to a small staging/canary group, then to separate production groups after validation.

If qbee rejects an expired session with HTTP 401, the script signs in again and retries that request once. Persistent authentication/permission errors fail immediately. Temporary inventory/network errors are reported explicitly and retried within the rollout timeout; they are not shown as missing containers. Credentials and API response bodies are never included in these error messages.

Manual deployments use `.NET gateway deploy` with the matching commit and image tag; leave simulator off for real equipment. The workflow file must exist on the repository's default branch for GitHub's **Run workflow** UI to appear. Until then, feature-branch pushes run CI, and existing runs can be re-run. Only one deployment to a group runs at a time.

For rollback, select a previous commit and its existing immutable tag. This restores images and the matching Compose file, **not a database backup**. Future schema changes must remain compatible with the rollback target; take a stopped/consistent backup of the persistent volumes before schema-changing releases.

## Access and verify

Use qbee-connect or qbee-cli authenticated to an account with remote-access permission. Map a free laptop port to `localhost:5300` on the VM and open `http://127.0.0.1:<mapped-port>/register`. For example, from PowerShell in the CLI folder:

```powershell
.\qbee-cli.exe connect -d DEVICE_ID -t 15300:localhost:5300
```

Replace `DEVICE_ID` without angle brackets. Sign in as `admin` with the management password. Add a TCP interface using an address reachable **from the VM/container**, then register its devices, unit IDs and metrics. One interface can serve several unit IDs. Addresses are zero-based. Existing API validation and metric identities are retained. RTU records remain in the registry, but RTU polling is not implemented.

The page provides device/interface/broker configuration and basic device polling status. A worker snapshot older than 15 seconds shows status unavailable. Detailed MQTT/outbox diagnostics stay in the console/status file. Docker health checks use the worker heartbeat and registry readiness independently of the page; a broker or field-device outage does not make an otherwise functioning buffering gateway unhealthy.

For MQTTX, create another tunnel to VM `localhost:8883`. Download the **local broker certificate** from the registry page, select `mqtts://127.0.0.1:<mapped-port>`, enable SSL Secure, choose CA or Self signed certificates and select `broker.crt` in CA File. This uses the same self-signed broker certificate approach as Node-RED, with no separate CA. The certificate covers `mosquitto`, `localhost` and `127.0.0.1`. Subscribe to `spBv1.0/<group>/#`; payloads are Sparkplug B protobuf. This PoC broker uses anonymous MQTT inside the private Compose network and loopback tunnel; TLS supplies server verification, not client authentication. Access to the tunnel is controlled by qbee users/roles.

On the VM console:

```sh
sudo docker ps
sudo docker logs --tail 50 -f iot-dotnet-gateway-1
sudo docker exec iot-dotnet-gateway-1 cat /state/status.json
```

To test the outbox, keep the simulator/gateway running, stop `iot-dotnet-mosquitto-1`, and inspect `unsent` in `/state/status.json` using the console command above. Start that same broker again and verify the gateway reconnects and the count drains. Docker's `restart: unless-stopped` starts services after a VM reboot, but an explicitly stopped container stays stopped until started. Reopen laptop tunnels after a laptop/VM restart; no redeploy or device registration is needed.

## Persistent state and certificates

Named volumes keep the configuration database, gateway outbox, configuration cache, per-device sequences, Sparkplug birth/death sequence, broker data and certificates. Redeploying/recreating containers does not remove them. Never run `docker compose down -v` on a deployed computer unless intentionally resetting it.

The registry seeds the container broker (`mosquitto:8883`, `/certs/broker.crt`) only on the first deployment. A pending initialization marker allows retry after interrupted setup. Saved user broker edits are preserved on future starts. Native development keeps its existing defaults when no `SeedBroker` settings are provided.

Only the broker mounts its private key. Gateway/configuration mount the public `broker.crt`; the UI downloads only that configured public file. The certificate persists on restart and lasts one year. Plan renewal before expiry and update clients' trusted broker certificate when replacing it. Back up keys securely; do not copy private keys to MQTTX or GitHub.

## Local verification for contributors

```sh
dotnet build IoT.sln -c Release
dotnet tests/IoT.Checks/bin/Release/net8.0/IoT.Checks.dll
node --test tests/deployment/qbee.test.cjs
```

For the real-browser registry check, publish the configuration service to `logs/dotnet-qbee/browser-app`, then run `npm ci`, `npx playwright install chromium` and `npm test` from `tests/deployment`. The test starts/stops its own temporary local API. Playwright is a test-only dependency; it is not shipped in any edge image.

Build the eight Docker targets (`gateway`, `configuration`, `broker`, `certs`, `simulator`, `historian`, `history-db`, `grafana`) from `deploy/dotnet/Dockerfile`, tagging each `iot-dotnet-<target>:local`, then run:

```sh
node --test tests/deployment/stack.test.cjs
```

For ARM64, build each target with `--platform linux/arm64` and tag `iot-dotnet-<target>:local-arm64`, then run the same test with `EDGE_TEST_PLATFORM=linux/arm64` and `EDGE_IMAGE_TAG=local-arm64` environment variables. The builder needs ARM64 emulation or a native ARM64 host. The SDK builds on the build host and publishes for the target architecture, including the matching native SQLite assets.

The integration test creates an isolated Compose project without published host ports and removes only its own containers/volumes when finished. It checks authenticated APIs, strict .NET/Node TLS verification, actual Modbus polling and Sparkplug topics, buffered outage recovery, cached startup, stale device status, persistent metric IDs/sequences and whole-stack recreation. The Node TLS probe runs in a disposable `node:22-alpine` test container; it is not deployed to qbee. The Historian database checks require `HISTORIAN_TEST_CONNECTION` pointing at a disposable TimescaleDB test database: they drop/recreate history tables. CI uses its own temporary database, never a deployed database.

## Grafana and history

The Grafana branch builds/tests/publishes the eight image targets and then deploys to the configured .NET staging group. Configure the qbee parameters below before pushing deployment changes. The rollout uses the same commit's immutable image tag, `iot-dotnet` project and existing volumes. The previous branch/worktree remains available as the gateway-only demo source.

Three continuous services are added: `historian` (Sparkplug history and generated dashboards), `timescaledb` (storage), and `grafana` (visualization). With the optional simulator there are seven running containers plus the successful one-shot `certs` container.

Add these qbee parameters before the first history rollout:

| Parameter | Value | Kind |
|---|---|---|
| `DOTNET_GRAFANA_PORT` | `3000`, or another free VM loopback port | Parameter |
| `DOTNET_HISTORY_DB_PASSWORD` | Non-empty database password | Secret |
| `DOTNET_GRAFANA_DB_PASSWORD` | Separate non-empty read-only database password | Secret |
| `DOTNET_GRAFANA_ADMIN_PASSWORD` | Separate Grafana admin password | Secret |

Reuse existing GitHub credentials and `DOTNET_QBEE_GROUP_ID`. The management password also authenticates the Historian's registry requests. The qbee YAML escaping requirements above still apply. The historian passes the database password as `PGPASSWORD`, so semicolons do not change connection-string settings. Grafana provisioning uses `$GRAFANA_DB_PASSWORD` to avoid double expansion of dollar signs.

Historian subscribes to the local broker for this Sparkplug group and requests fresh definitions using the gateway's existing NCMD Rebirth support. It stores measurement timestamps and acknowledges messages after database commit. Offline queuing is bounded by `max_queued_messages`; it is not unlimited storage. Readings acknowledged before its first subscription cannot be reconstructed. If a gateway is assigned an external broker, explicitly configure the historian for that broker too; it does not automatically follow registry broker assignments.

Named volumes `history-data`, `history-dashboards` and `grafana-data` preserve history, generated dashboards and Grafana settings. Historian owns the dashboard volume; Grafana mounts it read-only. Grafana's database account only has SELECT access. The database has no published host port. Grafana binds only to VM loopback: use a qbee tunnel such as `15300:localhost:5300,13000:localhost:3000`, then open `http://localhost:13000`. Sign in as `admin` with the Grafana password, separate from the registry password. Existing certificate behavior is unchanged.

Database users and the Grafana admin account are initialized on first start. Changing environment parameters later does not rotate stored account passwords automatically. Rotate accounts deliberately and update clients; never delete volumes to change passwords. Back up history with `pg_dump` to storage outside the VM. Keep PostgreSQL major version 17; test extension/version upgrades before rollout. Existing history retention/compression policies remain unchanged.

Rollout checks require the requested version and healthy gateway/configuration/historian/database/Grafana, plus a running broker/simulator. Historian readiness is a local heartbeat after MQTT subscription and database connectivity, with no new HTTP service. Gateway polling does not depend on historian/Grafana readiness. Measure VM RAM/disk at the expected load; the small PoC VM may require additional RAM.
