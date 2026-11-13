const test = require("node:test");
const assert = require("node:assert/strict");
const { run, store } = require("../helpers/flow-functions");

const SCHEDULER = "registry_scheduler";
const DECODER = "scale_and_format";
const T0 = Date.parse("2026-11-13T08:00:00Z");

function metric(name, register, address, dataType, extra = {}) {
    return { name, register, address, dataType, wordOrder: null, scale: 1, unit: "", ...extra };
}
// Schedules one poll of a device and returns its frame tasks plus the shared flow store.
function poll(metrics) {
    const flow = store();
    const global = store();
    const device = { id: "d1", deviceId: "d1", description: "Test", host: "127.0.0.1", port: 1502, unitId: 3, pollIntervalSeconds: 2, enabled: true, metrics };
    run(SCHEDULER, { _kind: "config", payload: { version: 1, devices: [device] } }, { flow, global, now: T0 });
    const [reads] = run(SCHEDULER, { _kind: "tick" }, { flow, global, now: T0 }).result;
    return { flow, global, tasks: reads.map(r => r._iotTask) };
}
function answer(flow, task, msg, global = store()) {
    return run(DECODER, { _iotTask: task, ...msg }, { flow, global }).result;
}
// Decodes a single-frame device whose frame returns these words.
function decodeOne(m, words) {
    const { flow, tasks } = poll([m]);
    const [telemetry] = answer(flow, tasks[0], { payload: { data: words } });
    return telemetry.payload.metrics[m.name];
}

test("int16 is signed and scaled", () => {
    assert.equal(decodeOne(metric("t", "holding", 0, "int16", { scale: 0.1 }), [212]).value, 21.2);
    assert.equal(decodeOne(metric("t", "holding", 0, "int16"), [65535]).value, -1);
});

test("uint16 is unsigned", () => {
    assert.equal(decodeOne(metric("c", "input", 0, "uint16"), [65535]).value, 65535);
});

test("int32 and uint32 respect word order", () => {
    assert.equal(decodeOne(metric("a", "holding", 0, "int32", { wordOrder: "big" }), [0xFFFF, 0xFFFE]).value, -2);
    assert.equal(decodeOne(metric("a", "holding", 0, "int32", { wordOrder: "little" }), [0xFFFE, 0xFFFF]).value, -2);
    assert.equal(decodeOne(metric("u", "holding", 0, "uint32", { wordOrder: "big" }), [0x0001, 0x0000]).value, 65536);
});

test("float32 respects word order", () => {
    assert.equal(decodeOne(metric("f", "holding", 0, "float32", { wordOrder: "big" }), [0x4148, 0x0000]).value, 12.5);
    assert.equal(decodeOne(metric("f", "holding", 0, "float32", { wordOrder: "little" }), [0x0000, 0x4148]).value, 12.5);
});

test("coils and discrete inputs decode to 1 or 0", () => {
    assert.equal(decodeOne(metric("p", "coil", 0, "bool"), [true, false]).value, 1);
    assert.equal(decodeOne(metric("a", "discrete", 0, "bool"), [false]).value, 0);
});

test("a metric is read at its offset inside the frame", () => {
    const { flow, tasks } = poll([metric("a", "holding", 10, "int16"), metric("b", "holding", 12, "uint16")]);
    assert.equal(tasks[0].frame.start, 10);
    const [telemetry] = answer(flow, tasks[0], { payload: { data: [1, 999, 7] } });
    assert.equal(telemetry.payload.metrics.a.value, 1);
    assert.equal(telemetry.payload.metrics.b.value, 7);
});

test("telemetry is published once, after the last frame, with the poll's timestamp", () => {
    const { flow, tasks } = poll([metric("h", "holding", 0, "int16"), metric("i", "input", 0, "uint16")]);
    const [first, firstDone] = answer(flow, tasks[0], { payload: { data: [5] } });
    assert.equal(first, null);
    assert.equal(firstDone._kind, "done");
    const [telemetry] = answer(flow, tasks[1], { payload: { data: [6] } });
    assert.equal(telemetry.topic, "iot/d1/telemetry");
    assert.equal(telemetry.payload.timestamp, new Date(T0).toISOString());
    assert.equal(telemetry.payload.unitId, 3);
    assert.equal(telemetry.payload.state, "polling");
    assert.equal(telemetry.payload.frames.length, 2);
});

test("a failed frame only marks its own metrics bad, and the device is failing", () => {
    const { flow, global, tasks } = poll([metric("h", "holding", 0, "int16"), metric("i", "input", 0, "uint16")]);
    answer(flow, tasks[0], { payload: { data: [5] } }, global);
    const [telemetry] = answer(flow, tasks[1], { _iotError: "127.0.0.1:1502 unit 3: Timed out" }, global);
    const { metrics, frames, state } = telemetry.payload;
    assert.equal(metrics.h.quality, "Good");
    assert.equal(metrics.i.quality, "Bad");
    assert.match(metrics.i.error, /Timed out/);
    assert.deepEqual(frames.map(f => f.quality), ["Good", "Bad"]);
    assert.equal(state, "failing");
    assert.match(global.get("iotDeviceStatus").d1.lastError, /^input 0\+1 \[i\]: .*Timed out/);
});

test("a non-finite value is a bad metric in an answered poll", () => {
    const { flow, global, tasks } = poll([metric("f", "holding", 0, "float32", { wordOrder: "big" })]);
    const [telemetry] = answer(flow, tasks[0], { payload: { data: [0x7FC0, 0x0000] } }, global);
    assert.equal(telemetry.payload.metrics.f.quality, "Bad");
    assert.equal(telemetry.payload.state, "failing");
    assert.match(global.get("iotDeviceStatus").d1.lastError, /InvalidValue/);
});

test("a late frame from a cleared poll publishes nothing but still reports done", () => {
    const { tasks } = poll([metric("h", "holding", 0, "int16")]);
    const [telemetry, doneMsg] = run(DECODER, { _iotTask: tasks[0], payload: { data: [1] } }, { flow: store() }).result;
    assert.equal(telemetry, null);
    assert.equal(doneMsg._iotTask.cycleId, tasks[0].cycleId);
});

test("a message without a frame task is ignored", () => {
    assert.deepEqual(run(DECODER, { payload: [212] }).result, [null, null]);
});
