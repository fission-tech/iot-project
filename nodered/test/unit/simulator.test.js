const test = require("node:test");
const assert = require("node:assert/strict");
const http = require("node:http");
const { plantAt, signalAt, encode, unitsFor } = require("../../deploy/simulator/simulator");
const { seed, plantDevices, gatewayId } = require("../../deploy/simulator/seed");
const { run, store } = require("../helpers/flow-functions");

const VALIDATE = "c22d6d9f13e4c097";
const minute = 60000;

test("the simulated pump runs 9 minutes in 10 and the tank fills and empties between 20 and 90 %", () => {
    assert.equal(plantAt(0).running, true);
    assert.equal(plantAt(9.5 * minute).running, false);
    assert.equal(plantAt(9.5 * minute).flow, 0);
    assert.ok(plantAt(4 * minute).flow > 10);
    assert.equal(plantAt(0).level, 20);
    assert.equal(plantAt(15 * minute).level, 90);
    assert.equal(plantAt(30 * minute).level, 20);
    for (let t = 0; t < 60 * minute; t += 7000) {
        const plant = plantAt(t);
        assert.ok(plant.temperature > 15 && plant.temperature < 30, "temperature fits int16 x10");
        assert.ok(plant.level >= 20 && plant.level <= 90);
    }
});

// Decodes words with the gateway's own decoder, as one poll of a one-metric device.
function decodeWithGateway(m, words) {
    const flow = store();
    const global = store();
    const device = { id: "d1", deviceId: "d1", host: "modbus-sim", port: 1502, unitId: 3, pollIntervalSeconds: 2, enabled: true, metrics: [m] };
    run("registry_scheduler", { _kind: "config", payload: { version: 1, devices: [device] } }, { flow, global, now: 0 });
    const [reads] = run("registry_scheduler", { _kind: "tick" }, { flow, global, now: 0 }).result;
    const [telemetry] = run("scale_and_format", { _iotTask: reads[0]._iotTask, payload: { data: words } }, { flow, global }).result;
    return telemetry.payload.metrics[m.name].value;
}

test("registered values encode so the gateway decodes them back, for every type and word order", () => {
    const m = (dataType, extra = {}) => ({ name: "v", register: "holding", address: 4, dataType, wordOrder: null, scale: 1, unit: "", ...extra });
    assert.equal(decodeWithGateway(m("int16", { scale: 0.1 }), [encode(m("int16", { scale: 0.1 }), 42.3)]), 42.3);
    assert.equal(decodeWithGateway(m("uint16"), [encode(m("uint16"), 77.4)]), 77);
    for (const wordOrder of ["big", "little"]) {
        for (const dataType of ["int32", "uint32"]) {
            const metric = m(dataType, { wordOrder, scale: 0.01 });
            assert.equal(decodeWithGateway(metric, encode(metric, 63.25)), 63.25, dataType + " " + wordOrder);
        }
        const metric = m("float32", { wordOrder });
        assert.ok(Math.abs(decodeWithGateway(metric, encode(metric, 12.5)) - 12.5) < 1e-6, "float32 " + wordOrder);
    }
    // A value that does not fit the type is clamped instead of wrapping around.
    assert.equal(decodeWithGateway(m("int16", { scale: 0.001 }), [encode(m("int16", { scale: 0.001 }), 90)]), 32.767);
    assert.equal(encode({ dataType: "bool" }, 80), 1);
    assert.equal(encode({ dataType: "bool" }, 10), 0);
});

test("the simulator answers as each registered device at its address, and keeps the pump and tank", () => {
    const metric = (name, register, address, dataType) => ({ name, register, address, dataType, wordOrder: null, scale: 1, unit: "" });
    const devices = [
        { deviceId: "fan", host: "Modbus-Sim", port: 1502, unitId: 5, enabled: true,
            metrics: [metric("Speed", "holding", 10, "uint16"), metric("Power", "input", 0, "float32"), metric("On", "coil", 3, "bool")] },
        { deviceId: "off", host: "modbus-sim", port: 1502, unitId: 6, enabled: false, metrics: [metric("x", "holding", 0, "int16")] },
        { deviceId: "plc", host: "192.168.1.50", port: 1502, unitId: 7, enabled: true, metrics: [metric("x", "holding", 0, "int16")] },
        { deviceId: "port", host: "modbus-sim", port: 502, unitId: 8, enabled: true, metrics: [metric("x", "holding", 0, "int16")] }
    ];
    const units = unitsFor(devices, 3 * minute, { hosts: ["modbus-sim"], port: 1502 });
    assert.deepEqual(Object.keys(units), ["1", "2", "5"]);
    assert.equal(units[5].holding[10], Math.round(signalAt("fan/Speed", 3 * minute)));
    assert.ok(units[5].input[0] !== undefined && units[5].input[1] !== undefined, "float32 fills two registers");
    assert.ok(units[5].coils[3] === 0 || units[5].coils[3] === 1);
    assert.equal(units[1].holding[0], Math.round(plantAt(3 * minute).temperature * 10));
    for (let t = 0; t < 30 * minute; t += 11000) {
        const value = signalAt("fan/Speed", t);
        assert.ok(value >= 10 && value <= 90);
    }
    assert.notEqual(signalAt("fan/Speed", minute), signalAt("fan/Power", minute), "each metric moves on its own");
});

test("the simulated devices pass the registry's own validation", () => {
    for (const device of plantDevices({ gateway: "staging-1-2", host: "modbus-sim", port: 1502 })) {
        const [ok, rejected] = run(VALIDATE, { req: { method: "POST", params: {} }, payload: device }).result;
        assert.equal(rejected, null, device.deviceId + ": " + JSON.stringify(rejected?.payload));
        assert.ok(ok);
    }
});

test("extra gateways get their number appended to the gateway ID, like settings.js", () => {
    assert.equal(gatewayId({ GATEWAY_ID: "vm-1" }), "vm-1");
    assert.equal(gatewayId({ GATEWAY_ID: "vm-1", GATEWAY_INSTANCE: "1" }), "vm-1");
    assert.equal(gatewayId({ GATEWAY_ID: "vm-1", GATEWAY_INSTANCE: "3" }), "vm-1-3");
});

// A fake registry API: answers 503 for the first request (Node-RED still starting), knows "vm-1-tank" already.
function fakeNodeRed() {
    const requests = [];
    const server = http.createServer((req, res) => {
        let body = "";
        req.on("data", chunk => body += chunk);
        req.on("end", () => {
            requests.push({ auth: req.headers.authorization, device: JSON.parse(body) });
            const status = requests.length === 1 ? 503 : JSON.parse(body).deviceId === "vm-1-tank" ? 409 : 201;
            res.writeHead(status).end("{}");
        });
    });
    return new Promise(resolve => server.listen(0, "127.0.0.1", () =>
        resolve({ server, requests, url: "http://127.0.0.1:" + server.address().port })));
}

test("seeding retries until Node-RED answers, logs in, and leaves existing devices alone", async t => {
    const fake = await fakeNodeRed();
    t.after(() => fake.server.close());
    const log = [];
    await seed({ NODE_RED_URL: fake.url, NODE_RED_PASSWORD: "pw", GATEWAY_ID: "vm-1", SIM_HOST: "sim", SIM_PORT: "1502" },
        line => log.push(line), { delayMs: 10 });
    assert.deepEqual(fake.requests.map(r => r.device.deviceId), ["vm-1-pump", "vm-1-pump", "vm-1-tank"]);
    assert.equal(fake.requests[0].auth, "Basic " + Buffer.from("admin:pw").toString("base64"));
    assert.equal(fake.requests[1].device.host, "sim");
    assert.deepEqual(log, ["Registered vm-1-pump", "vm-1-tank already registered; left as it is"]);
});

test("seeding stops at once on a wrong password", async t => {
    const server = http.createServer((req, res) => res.writeHead(401).end("Login required"));
    await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
    t.after(() => server.close());
    await assert.rejects(seed({ NODE_RED_URL: "http://127.0.0.1:" + server.address().port }, () => {}, { delayMs: 10 }),
        /Could not register nodered-gateway-01-pump: 401 Login required/);
});
