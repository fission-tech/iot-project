# Modbus frame failure handling

The gateway still polls each enabled device, saves each sample to SQLite, and publishes Sparkplug B through MQTT. Frame planning and configured limits are unchanged.

Each cycle begins with all configured metrics set to null/Bad. Successful frames replace their own metrics with decoded/scaled values. A valid target exception (for example, IllegalDataAddress) leaves that frame Bad and allows the next independent frame to run. Metric names stay unchanged, so a quality change does not require a new DBIRTH.

A timeout, broken connection (including a clean TCP close) or invalid transport response closes the connection and stops further requests in that cycle. Values already collected are saved; failed and unattempted frames remain Bad. The next cycle reconnects. Three consecutive cycles without a target response retain the existing DDEATH behaviour. A response from an intermediary saying the target is unavailable (exception 0A/0B) does not prove the target is reachable.

The sample keeps the existing cycle-start timestamp and one DeviceSeq. SQLite retries keep the same sample, sequence and timestamp. Broker buffering, replay, configuration refresh and shutdown handling are unchanged. Demo Console shows the affected metric names as Bad and distinguishes frame errors from communication failures.

## Example and manual demo

Configure holding Temperature/Humidity at 0/1, Pressure at 200, and float32 Voltage at 400/401. These form three independent requests.

1. With all ranges available in ModSim, check that MQTTX receives all four values.
2. Make only the Pressure range unavailable. Keep the other ranges accessible.
3. Check multiple cycles: Temperature/Humidity/Voltage still update; Pressure is null with `Quality=0`. There should be no DDEATH merely because Pressure is unavailable, even after three cycles.
4. Restore Pressure. It becomes Good on a later poll without restarting the gateway or changing the metric list.
5. Stop the actual Modbus server. After three cycles without a target response, DDEATH should appear. Restart it and check DBIRTH/data recovery.

If one request spans several metrics and the server rejects that request, all metrics in that frame are Bad. There is no automatic per-address fallback or frame splitting in this fix. A silent timeout also prevents trying later frames in that cycle, to avoid further requests on an unsynchronised connection.

## Regression checks

`dotnet run --project tests/IoT.Checks -- --frame-isolation-only` runs real local Modbus TCP servers with rejected first/middle/last frames, mixed memory areas, 16/32-bit decoding, valid target exceptions, clean TCP close/timeout/reconnect, storage failure/retry, Sparkplug quality, and target-unavailable responses. The normal checks run these alongside the existing storage, lifecycle, configuration, broker-history and migration tests.

`node tests/IoT.Checks/demo-status-ui.cjs` checks the actual page functions, including partial-frame error display and active polling count.
