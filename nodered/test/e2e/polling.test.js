const test = require("node:test");
const assert = require("node:assert/strict");
const { api, deviceHost, removeTestDevices, device, deviceStatus, telemetry, until, sleep } = require("../helpers/live");
const { createSim } = require("../helpers/modbus-sim");

// Endpoint A: two healthy units, one the "gateway" cannot reach (exception 11) and one that rejects the address (exception 2).
const simA = createSim({
    port: 15020,
    units: {
        3: { holding: { 0: 212, 1: 0x4148, 2: 0x0000, 10: 0xFFFF }, input: { 0: 65535 }, coils: { 0: 1 }, discrete: { 0: 0 } },
        4: { holding: { 0: 77 } },
        5: "illegal",
        9: "exception"
    }
});
// Endpoint B: a unit that accepts the connection but never answers.
const simB = createSim({ port: 15021, units: { 7: "silent" } });
const DEAD_PORT = 15029; // nothing listens here

const m = (name, register, address, dataType, extra = {}) => ({ name, register, address, dataType, wordOrder: null, scale: 1, unit: "", ...extra });
const unit3 = device("u3", 15020, 3, [
    m("Temperature", "holding", 0, "int16", { scale: 0.1, unit: "°C" }),
    m("Flow", "holding", 1, "float32", { wordOrder: "big", unit: "m3/h" }),
    m("Offset", "holding", 10, "int16"),
    m("Counter", "input", 0, "uint16"),
    m("Pump", "coil", 0, "bool"),
    m("Alarm", "discrete", 0, "bool")
]);
const polling = p => p.state === "polling";
const offlineAfterFailures = id => until(async () => {
    const status = await deviceStatus(id);
    return status?.state === "offline" && status;
}, { timeoutMs: 15000, what: id + " offline" });
let bus;

test.before(async () => {
    await removeTestDevices();
    await simA.start();
    await simB.start();
    bus = await telemetry();
});

test.after(async () => {
    await removeTestDevices();
    await bus?.close();
    await simA.stop();
    await simB.stop();
});

test("every area and data type decodes end to end", { timeout: 20000 }, async () => {
    const since = Date.now();
    assert.equal((await api("POST", "", unit3)).status, 201);
    const reading = await bus.waitFor("u3", polling, { since });
    const values = Object.fromEntries(Object.entries(reading.metrics).map(([k, v]) => [k, v.value]));
    assert.deepEqual(values, { Temperature: 21.2, Flow: 12.5, Offset: -1, Counter: 65535, Pump: 1, Alarm: 0 });
    assert.equal(reading.metrics.Temperature.unit, "°C");
    assert.equal(reading.frames.length, 4, "holding 0-10 is one frame; input, coil and discrete one each");
    assert.ok(reading.frames.every(f => f.quality === "Good"));
    await bus.waitFor("u3", p => p.state === "online", { since, kind: "status" });
});

test("the device status shows endpoint, frames, last reading and raw registers", { timeout: 15000 }, async () => {
    const status = await until(async () => {
        const current = await deviceStatus("u3");
        return current?.state === "polling" && current.lastSeq > 0 && current;
    }, { what: "u3 polling" });
    assert.equal(status.endpoint, deviceHost + ":15020 unit 3");
    assert.deepEqual(status.frames, ["holding 0+11", "input 0+1", "coil 0+1", "discrete 0+1"]);
    assert.equal(status.failures, 0);
    assert.equal(status.lastError, null);
    assert.ok(status.lastSuccessAt);
    assert.deepEqual(status.lastRaw.find(f => f.register === "input"), { register: "input", start: 0, values: [65535] });
    assert.equal(status.lastValues.Temperature.value, 21.2);
});

test("two units on one endpoint share one TCP connection", { timeout: 20000 }, async () => {
    const since = Date.now();
    await api("POST", "", device("u4", 15020, 4, [m("Level", "holding", 0, "int16")]));
    const reading = await bus.waitFor("u4", polling, { since });
    assert.equal(reading.metrics.Level.value, 77);
    await bus.waitFor("u3", polling, { since: Date.now() });
    assert.equal(simA.openConnections, 1);
});

test("a unit the gateway cannot reach (exception 11) publishes nothing and goes offline; neighbours and the connection are unaffected", { timeout: 25000 }, async () => {
    const connectionsBefore = simA.totalConnections;
    const since = Date.now();
    await api("POST", "", device("u9", 15020, 9, [m("Level", "holding", 0, "int16")]));
    const status = await offlineAfterFailures("u9");
    assert.match(status.lastError, /exception 11/i);
    assert.ok(status.failures >= 3);
    assert.equal(bus.forDevice("u9", since).length, 0, "a device never seen online publishes no readings");
    assert.equal(bus.forDevice("u9", since, "status").length, 0, "nor an offline message");
    await bus.waitFor("u3", polling, { since: Date.now() });
    await bus.waitFor("u4", polling, { since: Date.now() });
    assert.equal(simA.totalConnections, connectionsBefore, "no reconnect after a Modbus exception");
    assert.equal(simA.openConnections, 1);
});

test("a unit that rejects the address answered: failing with bad values, never offline", { timeout: 20000 }, async () => {
    const since = Date.now();
    await api("POST", "", device("u5", 15020, 5, [m("Level", "holding", 0, "int16")]));
    const reading = await bus.waitFor("u5", p => p.state === "failing", { since });
    assert.equal(reading.metrics.Level.quality, "Bad");
    assert.match(reading.metrics.Level.error, /exception 2|illegal/i);
    // Past the 3 polls that would make an unanswered device offline. Waits for the readings rather than a fixed
    // time, so a slow start (such as just after Node-RED boots) doesn't fail it.
    await until(() => bus.forDevice("u5", since).length >= 4, { what: "4 readings while failing" });
    const status = await deviceStatus("u5");
    assert.equal(status.state, "failing");
    assert.equal(status.failures, 0, "an exception reply is an answer");
    await api("DELETE", "/e2e-u5");
});

test("a silent unit times out, skips the rest of its poll, and does not slow another endpoint", { timeout: 40000 }, async () => {
    const before = simB.requests.length;
    await api("POST", "", device("u7", 15021, 7, [m("Level", "holding", 0, "int16"), m("Count", "input", 0, "uint16")]));
    const status = await offlineAfterFailures("u7");
    assert.match(status.lastError, /timed out/i);
    assert.match(status.lastError, /not attempted after an earlier frame of this poll failed/);
    const sent = simB.requests.slice(before);
    assert.ok(sent.length >= 3);
    assert.ok(sent.every(request => request.fc === 3), "the input frame is never sent after the holding frame times out");
    // 10 readings expected at u3's 1 s interval. At least 7 tolerates a 2-3 s stall of a busy CI runner, while a u3
    // held up behind u7's 2 s timeouts would manage only about 3-5.
    const window = Date.now();
    await sleep(10000);
    const u3 = bus.forDevice("u3", window);
    assert.ok(u3.length >= 7, `u3 published ${u3.length} times in 10 s at a 1 s interval`);
    assert.ok(u3.every(msg => polling(msg.payload)));
});

test("an endpoint with nothing listening fails fast and goes offline", { timeout: 20000 }, async () => {
    const since = Date.now();
    await api("POST", "", device("dead", DEAD_PORT, 1, [m("Level", "holding", 0, "int16")]));
    const status = await offlineAfterFailures("dead");
    assert.match(status.lastError, /ECONNREFUSED/);
    assert.ok(Date.parse(status.lastPollAt) - since < 6000, "three 1 s polls, each failing at once");
    assert.equal(bus.forDevice("dead", since).length, 0);
});

test("an edited device applies without a redeploy", { timeout: 20000 }, async () => {
    const edited = { ...unit3, metrics: unit3.metrics.map(x => x.name === "Temperature" ? { ...x, scale: 1 } : x) };
    const since = Date.now();
    assert.equal((await api("PUT", "/" + unit3.deviceId, edited)).status, 200);
    await bus.waitFor("u3", p => p.metrics.Temperature?.value === 212, { since });
});

test("a disabled online device goes offline once and stops; a deleted never-online one just stops", { timeout: 20000 }, async () => {
    const u4 = (await api("GET")).data.find(d => d.deviceId === "e2e-u4");
    const changed = Date.now();
    await api("PUT", "/e2e-u4", { ...u4, enabled: false });
    await api("DELETE", "/e2e-u9");
    const offline = await bus.waitFor("u4", p => p.state === "offline", { since: changed, kind: "status" });
    assert.equal(offline.reason, "not enabled in configuration");
    await sleep(3000); // let polls already in flight finish
    const since = Date.now();
    await sleep(4000);
    assert.equal(bus.forDevice("u4", since).length, 0, "disabled device still published");
    assert.equal(bus.forDevice("u9", since).length, 0, "deleted device still published");
    assert.equal(bus.forDevice("u4", changed, "status").length, 1, "offline is announced once");
    assert.equal((await deviceStatus("u4")), undefined, "disabled devices leave the status list");
});

test("losing an endpoint: bad readings while failing, then offline once; online again when it returns", { timeout: 40000 }, async () => {
    await simA.stop();
    const down = Date.now();
    const failing = await bus.waitFor("u3", p => p.state === "failing", { since: down });
    assert.equal(failing.metrics.Temperature.quality, "Bad");
    const offline = await bus.waitFor("u3", p => p.state === "offline", { since: down, kind: "status", timeoutMs: 15000 });
    assert.match(offline.reason, /offline after 3 failed polls/);
    await sleep(3000);
    const quiet = Date.now();
    await sleep(3000);
    assert.equal(bus.forDevice("u3", quiet).length, 0, "no readings while offline");

    await simA.start();
    const up = Date.now();
    await bus.waitFor("u3", p => p.state === "online", { since: up, kind: "status", timeoutMs: 15000 });
    await bus.waitFor("u3", polling, { since: up });
    await sleep(2000);
    assert.equal(simA.openConnections, 1, "one connection after recovery");
    assert.equal((await deviceStatus("u3")).state, "polling");
});
