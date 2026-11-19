const test = require("node:test");
const assert = require("node:assert/strict");
const http = require("node:http");
const { plantAt } = require("../../deploy/simulator/simulator");
const { seed, plantDevices, gatewayId } = require("../../deploy/simulator/seed");
const { run } = require("../helpers/flow-functions");

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
