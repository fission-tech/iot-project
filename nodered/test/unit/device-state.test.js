// Device state like the .NET ModbusPoller: connecting → polling / failing / offline, and what each poll publishes.
const test = require("node:test");
const assert = require("node:assert/strict");
const { run, store } = require("../helpers/flow-functions");

const SCHEDULER = "registry_scheduler";
const DECODER = "scale_and_format";
const T0 = Date.parse("2026-11-13T08:00:00Z");
const GOOD = { data: [7] };
const REFUSED = { error: "127.0.0.1:1502 unit 3: connect ECONNREFUSED", responded: false };
const GATEWAY_TARGET_FAILED = { error: "127.0.0.1:1502 unit 3: Modbus exception 11", responded: false };
const ILLEGAL_ADDRESS = { error: "127.0.0.1:1502 unit 3: Modbus exception 2: Illegal data address", responded: true };

function deviceDefinition(extra = {}) {
    return { id: "d1", deviceId: "d1", description: "", host: "127.0.0.1", port: 1502, unitId: 3, pollIntervalSeconds: 2, enabled: true,
        metrics: [{ name: "h", register: "holding", address: 0, dataType: "int16", wordOrder: null, scale: 1, unit: "" }], ...extra };
}
// Runs whole polls through the real scheduler and decoder, sharing their flow and global stores.
function harness({ env = {}, device = deviceDefinition() } = {}) {
    const flow = store();
    const global = store();
    let now = T0;
    let version = 1;
    const configure = devices => run(SCHEDULER, { _kind: "config", payload: { version: version++, devices } }, { flow, global, now });
    configure([device]);
    return {
        flow, global,
        status: () => global.get("iotDeviceStatus").d1,
        configure,
        poll(answer) {
            const [reads] = run(SCHEDULER, { _kind: "tick" }, { flow, global, now }).result;
            assert.ok(reads, "device was due");
            let published = null;
            for (const read of reads) {
                const msg = answer.data ? { payload: { data: answer.data } } : { _iotError: answer.error, _iotResponded: answer.responded };
                const [output, done] = run(DECODER, { _iotTask: read._iotTask, ...msg }, { flow, global, env, now }).result;
                if (output) published = output;
                run(SCHEDULER, done, { flow, global, now });
            }
            now += 2000;
            return published;
        }
    };
}

test("a new device starts as connecting, with its endpoint and frame plan", () => {
    const { status } = harness();
    assert.equal(status().state, "connecting");
    assert.equal(status().endpoint, "127.0.0.1:1502 unit 3");
    assert.deepEqual(status().frames, ["holding 0+1"]);
});

test("a device that never answered publishes no readings and goes offline after 3 failed polls", () => {
    const device = harness();
    assert.equal(device.poll(REFUSED), null);
    assert.equal(device.status().state, "failing");
    assert.equal(device.poll(REFUSED), null);
    const third = device.poll(REFUSED);
    assert.equal(third._kind, "device-offline", "the gateway decides there is nothing to announce");
    assert.equal(device.status().state, "offline");
    assert.equal(device.status().failures, 3);
    assert.equal(device.poll(REFUSED), null, "offline is reported once");
});

test("an online device publishes bad readings while failing, then goes offline once", () => {
    const device = harness();
    assert.equal(device.poll(GOOD).payload.state, "polling");
    for (const failures of [1, 2]) {
        const reading = device.poll(REFUSED);
        assert.equal(reading.payload.state, "failing");
        assert.equal(reading.payload.metrics.h.quality, "Bad");
        assert.equal(device.status().failures, failures);
    }
    const offline = device.poll(REFUSED);
    assert.equal(offline._kind, "device-offline");
    assert.match(offline.payload.reason, /offline after 3 failed polls: holding 0\+1 \[h\]: .*ECONNREFUSED/);
    assert.equal(device.poll(REFUSED), null);
    assert.equal(device.status().state, "offline");
});

test("the next answered poll brings an offline device back and resets its failures", () => {
    const device = harness();
    device.poll(GOOD);
    for (let i = 0; i < 3; i++) device.poll(REFUSED);
    const back = device.poll(GOOD);
    assert.equal(back.payload.state, "polling");
    assert.equal(device.status().state, "polling");
    assert.equal(device.status().failures, 0);
    assert.equal(device.status().online, true);
});

test("a Modbus exception reply proves the device answered: failing, never offline", () => {
    const device = harness();
    for (let i = 0; i < 5; i++) {
        const reading = device.poll(ILLEGAL_ADDRESS);
        assert.equal(reading.payload.state, "failing");
        assert.equal(device.status().failures, 0);
    }
});

test("a gateway's exception 11 does not count as an answer", () => {
    const device = harness();
    device.poll(GOOD);
    device.poll(GATEWAY_TARGET_FAILED);
    device.poll(GATEWAY_TARGET_FAILED);
    assert.equal(device.poll(GATEWAY_TARGET_FAILED)._kind, "device-offline");
});

test("FAILED_POLLS_BEFORE_OFFLINE sets the threshold", () => {
    const device = harness({ env: { FAILED_POLLS_BEFORE_OFFLINE: "1" } });
    device.poll(GOOD);
    assert.equal(device.poll(REFUSED)._kind, "device-offline");
});

test("the status keeps the raw words and values of the last answered poll", () => {
    const device = harness();
    device.poll(GOOD);
    const answeredAt = device.status().lastSuccessAt;
    device.poll(REFUSED);
    const status = device.status();
    assert.deepEqual(status.lastRaw, [{ register: "holding", start: 0, values: [7] }]);
    assert.equal(status.lastValues.h.value, 7);
    assert.equal(status.lastSuccessAt, answeredAt);
    assert.notEqual(status.lastPollAt, answeredAt);
});

test("a changed definition starts the device over; a removed one drops out", () => {
    const device = harness();
    device.poll(GOOD);
    for (let i = 0; i < 3; i++) device.poll(REFUSED);
    device.configure([deviceDefinition({ unitId: 4 })]);
    assert.equal(device.status().state, "connecting");
    assert.equal(device.status().failures, 0);
    assert.equal(device.status().endpoint, "127.0.0.1:1502 unit 4");
    device.configure([]);
    assert.deepEqual(device.global.get("iotDeviceStatus"), {});
});

test("a redeploy starts every device over and re-applies the configuration", () => {
    const device = harness();
    device.poll(GOOD);
    run(SCHEDULER, null, { flow: device.flow, global: device.global, part: "initialize" });
    assert.deepEqual(device.global.get("iotDeviceStatus"), {});
    assert.equal(device.flow.get("iotRegistryScheduler").version, null);
    // A poll tick before the configuration arrives must not run on the stale device list.
    const [reads] = run(SCHEDULER, { _kind: "tick" }, { flow: device.flow, global: device.global, now: T0 + 60000 }).result;
    assert.equal(reads, null);
    device.configure([deviceDefinition()]);
    assert.deepEqual(device.status().frames, ["holding 0+1"]);
});
