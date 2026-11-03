# Gateway logs and error reporting

The Gateway uses .NET ILogger and a small console formatter showing `[INFO]`, `[WARN]` and other log levels without categories or Event IDs. Dates and local times use `yyyy-MM-dd HH:mm:ss`, without a timezone offset. Stored reading and Broker History timestamps are unchanged.

```text
2026-11-03 15:30:00 [WARN] Device device-01: holding 200+1 [Pressure]: IllegalDataAddress (0x02): Device rejected the requested register range.
2026-11-03 15:30:16 [INFO] device-01: Pressure reading recovered. Quality = good.
```

## Polling

- Errors identify the device, affected metrics, register area, zero-based frame start/count and actual error.
- Standard Modbus codes have short explanations. Unknown codes keep their numeric value. Malformed protocol responses without a device code use `InvalidResponse`. Socket/connection failures retain the actual exception details.
- An unchanged error is logged once. A changed error or recurrence after clearing is logged again. No periodic reminders are added.
- Bad-to-Good metric recovery is reported once. Tracking is bounded by current failures/configured metrics.
- Existing frame isolation, offline thresholds, SQLite retry, source timestamps and replay remain unchanged.
- Non-finite decoded/scaled values produce null/Bad and an affected-metric diagnostic.

## MQTT and other output

Existing ILogger MQTT/state-machine reporting is retained. The connection-failure entry is concise:

```text
Connection failed (MqttCommunicationException): <actual reason>. Retrying in 30s.
```

Lifecycle/reconnect/replay uses INFO, expected failures WARN, unexpected failures ERROR, and routine readings/publishes/state transitions DEBUG. Use `--Logging:LogLevel:Default=Debug` for details. No logging database, global wrapper or file provider was added.

Configuration reporting keeps loaded-version, polling-start and device-disabled/removed INFO events. An outage logs `Configuration unavailable. Retrying on the next refresh.` once; actual recovery logs `Configuration service reachable again.` once. A cached configuration does not count as service recovery. No detailed HTTP/JSON/cache failure logs are added.

Configuration API error responses and storage retry logs are unchanged. Local Swagger is independent of this work. `--configuration-reporting-only` tests real HTTP failures/recovery, cached restart and continued Modbus polling.

Demo Control strips the timestamp/level prefix for Activity entries and avoids duplicate timestamps. Multiline buffering was removed.

## Before / after

| Before | Now |
|---|---|
| Poll error lacked affected metric names. | Device, range, metric names and actual code/reason. |
| Same polling error printed every cycle. | First occurrence, changed reason or recurrence. |
| No metric recovery message. | `device-01: Pressure reading recovered. Quality = good.` |
| Invalid numeric value could be Bad without a reason. | `InvalidValue`, affected metric and null/Bad value. |
| Category/Event ID in the built-in format. | `[INFO]`/`[WARN]`, date/time without offset. |
| Multiline MQTT operation failure. | `Connection failed (<type>): <reason>. Retrying in <delay>s.` |
| Demo buffered multiline blocks. | Parses complete single-line messages. |

## Checks

`dotnet run --project tests/IoT.Checks -- --error-reporting-only` tests real Modbus TCP frame errors, standard/unknown codes, malformed responses, suppression/recurrence, recovery and invalid values, plus actual console output and Demo Activity parsing. The full suite also covers storage, lifecycle, configuration, address validation and precision.

`node tests/IoT.Checks/demo-status-ui.cjs` and `node tests/IoT.Checks/metric-address-ui.cjs` test actual UI functions. The existing `--broker-history-live <mosquitto-path>` option covers private Mosquitto TLS outages, replay, ACK accounting and shutdown.
