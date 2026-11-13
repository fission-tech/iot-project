const test = require("node:test");
const assert = require("node:assert/strict");
const { run, store } = require("../helpers/flow-functions");

const SCHEDULER = "registry_scheduler";
const T0 = Date.parse("2026-11-13T08:00:00Z");

function metric(name, register, address, dataType = register === "coil" || register === "discrete" ? "bool" : "int16") {
    return { name, register, address, dataType, wordOrder: null, scale: 1, unit: "" };
}
function device(id, metrics, extra = {}) {
    return { id, deviceId: id, host: "127.0.0.1", port: 1502, unitId: 3, pollIntervalSeconds: 2, enabled: true, metrics, ...extra };
}
function configure(flow, devices, version = 1, now = T0) {
    return run(SCHEDULER, { _kind: "config", payload: { version, devices } }, { flow, now }).result;
}
function tick(flow, now) {
    const [reads, status] = run(SCHEDULER, { _kind: "tick" }, { flow, now }).result;
    return { reads: reads || [], status: status || [] };
}
function done(flow, task, now) {
    run(SCHEDULER, { _kind: "done", _iotTask: task }, { flow, now });
}
function framesFor(metrics) {
    const flow = store();
    configure(flow, [device("d1", metrics)]);
    return tick(flow, T0).reads.map(read => read._iotTask.frame);
}

test("frame planning: each Modbus area gets its own frame", () => {
    const frames = framesFor([metric("h", "holding", 0), metric("i", "input", 0), metric("c", "coil", 0), metric("d", "discrete", 0)]);
    assert.deepEqual(frames.map(f => f.register).sort(), ["coil", "discrete", "holding", "input"]);
});

test("frame planning: up to eight addresses between points share a frame", () => {
    const merged = framesFor([metric("a", "holding", 0), metric("b", "holding", 9)]);
    assert.equal(merged.length, 1);
    assert.deepEqual([merged[0].start, merged[0].count], [0, 10]);

    const split = framesFor([metric("a", "holding", 0), metric("b", "holding", 10)]);
    assert.equal(split.length, 2);
});

test("frame planning: 32-bit types take two registers", () => {
    const frames = framesFor([metric("a", "holding", 0), metric("f", "holding", 1, "float32")]);
    assert.deepEqual([frames[0].start, frames[0].count], [0, 3]);
});

test("frame planning: register frames are capped at 125 registers", () => {
    const points = Array.from({ length: 27 }, (_, i) => metric("m" + i, "holding", i * 5));
    const frames = framesFor(points);
    assert.equal(frames.length, 2);
    assert.ok(frames.every(f => f.count <= 125));
    assert.equal(frames.reduce((sum, f) => sum + f.metrics.length, 0), 27);
});

test("frame planning: bit frames are capped at 2000 bits", () => {
    const points = Array.from({ length: 402 }, (_, i) => metric("c" + i, "coil", i * 5));
    const frames = framesFor(points);
    assert.equal(frames.length, 2);
    assert.ok(frames.every(f => f.count <= 2000));
});

test("scheduling: only the last frame of a poll is marked last", () => {
    const flow = store();
    configure(flow, [device("d1", [metric("h", "holding", 0), metric("i", "input", 0)])]);
    const { reads } = tick(flow, T0);
    assert.deepEqual(reads.map(r => r._iotTask.lastFrame), [false, true]);
    assert.equal(flow.get("iotPollerSamples")[reads[0]._iotTask.cycleId].remaining, 2);
});

test("scheduling: disabled devices are not polled", () => {
    const flow = store();
    const [, status] = configure(flow, [device("on", [metric("h", "holding", 0)]), device("off", [metric("h", "holding", 0)], { enabled: false })]);
    assert.equal(status[0].payload.deviceCount, 1);
    assert.deepEqual(tick(flow, T0).reads.map(r => r._iotTask.device.id), ["on"]);
});

test("scheduling: a device is polled again only after its interval and its previous poll finished", () => {
    const flow = store();
    configure(flow, [device("d1", [metric("h", "holding", 0)])]);
    const first = tick(flow, T0).reads;
    assert.equal(first.length, 1);
    assert.equal(tick(flow, T0 + 5000).reads.length, 0, "poll still in flight");
    done(flow, first[0]._iotTask, T0 + 5000);
    assert.equal(tick(flow, T0 + 5000).reads.length, 1, "due and released");

    const flow2 = store();
    configure(flow2, [device("d1", [metric("h", "holding", 0)])]);
    done(flow2, tick(flow2, T0).reads[0]._iotTask, T0 + 100);
    assert.equal(tick(flow2, T0 + 1000).reads.length, 0, "interval is 2 s");
    assert.equal(tick(flow2, T0 + 2000).reads.length, 1);
});

test("scheduling: a done signal for another poll or an earlier frame does not release the device", () => {
    const flow = store();
    configure(flow, [device("d1", [metric("h", "holding", 0), metric("i", "input", 0)])]);
    const [firstFrame, lastFrame] = tick(flow, T0).reads.map(r => r._iotTask);
    done(flow, firstFrame, T0 + 10);
    done(flow, { ...lastFrame, cycleId: "some-older-poll" }, T0 + 10);
    assert.equal(tick(flow, T0 + 3000).reads.length, 0);
    done(flow, lastFrame, T0 + 3000);
    assert.equal(tick(flow, T0 + 3000).reads.length, 2);
});

test("scheduling: a poll that never finishes is reported and restarted after 60 s", () => {
    const flow = store();
    configure(flow, [device("d1", [metric("h", "holding", 0)])]);
    tick(flow, T0);
    assert.equal(tick(flow, T0 + 59000).reads.length, 0);
    const { reads, status } = tick(flow, T0 + 61000);
    assert.equal(reads.length, 1);
    assert.equal(status[0].payload.state, "poll-stalled");
});

test("configuration: the same version is ignored, a new version applies and polls at once", () => {
    const flow = store();
    configure(flow, [device("d1", [metric("h", "holding", 0)])], 1);
    done(flow, tick(flow, T0).reads[0]._iotTask, T0 + 10);
    const [, unchanged] = configure(flow, [device("d1", [metric("h", "holding", 0)])], 1);
    assert.equal(unchanged, null);
    assert.equal(tick(flow, T0 + 100).reads.length, 0, "same version keeps the schedule");
    const [, applied] = configure(flow, [device("d1", [metric("h", "holding", 0)])], 2);
    assert.equal(applied[0].payload.state, "configuration-applied");
    assert.equal(tick(flow, T0 + 100).reads.length, 1, "new version polls immediately");
});

test("configuration: an invalid snapshot is reported", () => {
    const flow = store();
    const [, status] = run(SCHEDULER, { _kind: "config", payload: { devices: "nope" } }, { flow }).result;
    assert.equal(status[0].payload.state, "configuration-error");
});

test("configuration: a device without metrics is reported, not polled", () => {
    const flow = store();
    configure(flow, [device("empty", [])]);
    const { reads, status } = tick(flow, T0);
    assert.equal(reads.length, 0);
    assert.equal(status[0].payload.state, "configuration-error");
});

test("restart: initialize clears polls in flight and waits for the configuration again", () => {
    const flow = store();
    configure(flow, [device("d1", [metric("h", "holding", 0)])]);
    tick(flow, T0);
    run(SCHEDULER, null, { flow, part: "initialize" });
    assert.deepEqual(flow.get("iotRegistryScheduler").cycles, {});
    assert.deepEqual(flow.get("iotPollerSamples"), {});
    assert.equal(tick(flow, T0 + 3000).reads.length, 0, "nothing polled before the configuration arrives");
    configure(flow, [device("d1", [metric("h", "holding", 0)])]);
    assert.equal(tick(flow, T0 + 3000).reads.length, 1);
});
