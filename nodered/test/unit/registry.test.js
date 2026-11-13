const test = require("node:test");
const assert = require("node:assert/strict");
const { run } = require("../helpers/flow-functions");

const VALIDATE = "c22d6d9f13e4c097";
const RESPOND = "b223f6a3945bf349";
const SNAPSHOT = "registry_snapshot";
const CHANGED = "registry_changed";

function validDevice(extra = {}) {
    return {
        deviceId: "dev-1", host: "127.0.0.1", port: 502, unitId: 3, pollIntervalSeconds: 2,
        metrics: [{ name: "Temperature", register: "holding", address: 0, dataType: "int16", wordOrder: null, scale: 0.1, unit: "°C" }],
        ...extra
    };
}
function validate(method, payload, params = {}) {
    const [ok, rejected] = run(VALIDATE, { req: { method, params }, payload }).result;
    return { ok, rejected };
}
function rejects(device, method = "POST", params = {}) {
    const { ok, rejected } = validate(method, device, params);
    assert.equal(ok, null, "expected a rejection");
    assert.equal(rejected.statusCode, 400);
    return rejected.payload.error;
}

test("GET and DELETE build their queries", () => {
    assert.match(validate("GET").ok.topic, /^SELECT Id, Data FROM Devices/);
    const del = validate("DELETE", {}, { id: "dev-1" }).ok;
    assert.match(del.topic, /^DELETE FROM Devices/);
    assert.deepEqual(del.payload, ["dev-1"]);
});

test("POST stores an enabled-by-default device", () => {
    const { ok } = validate("POST", validDevice());
    assert.match(ok.topic, /^INSERT INTO Devices/);
    assert.equal(ok.payload[0], "dev-1");
    const stored = JSON.parse(ok.payload[1]);
    assert.equal(stored.host, "127.0.0.1");
    assert.equal(stored.enabled, true);
    // Whitespace anywhere in the host is rejected, including leading/trailing spaces.
    rejects(validDevice({ host: " 127.0.0.1 " }));
});

test("PUT updates by URL id and requires the body id to match", () => {
    const { ok } = validate("PUT", validDevice({ enabled: false }), { id: "dev-1" });
    assert.match(ok.topic, /^UPDATE Devices/);
    assert.equal(ok.payload[1], "dev-1");
    assert.equal(JSON.parse(ok.payload[0]).enabled, false);
    assert.match(rejects(validDevice(), "PUT", { id: "other" }), /must match/);
});

test("device fields are validated", () => {
    for (const bad of [
        { deviceId: "has space" }, { deviceId: "" }, { host: "" }, { host: "a/b" },
        { port: 0 }, { port: 65536 }, { port: 502.5 }, { unitId: 0 }, { unitId: 248 },
        { pollIntervalSeconds: 0.5 }, { enabled: "yes" }, { metrics: [] }
    ]) rejects(validDevice(bad));
    rejects(null);
});

test("metric fields are validated", () => {
    const m = validDevice().metrics[0];
    for (const bad of [
        [m, m], // duplicate names
        [{ ...m, register: "memory" }],
        [{ ...m, register: "coil", dataType: "int16" }],
        [{ ...m, register: "holding", dataType: "bool" }],
        [{ ...m, dataType: "float32", wordOrder: "big", address: 65535 }],
        [{ ...m, address: -1 }],
        [{ ...m, dataType: "int32", wordOrder: "middle" }],
        [{ ...m, scale: "abc" }]
    ]) rejects(validDevice({ metrics: bad }));
    assert.ok(validate("POST", validDevice({ metrics: [{ ...m, register: "coil", dataType: "bool" }] })).ok);
});

test("responses map to the right status codes", () => {
    const respond = (method, extra) => run(RESPOND, { req: { method, params: { id: "dev-1" } }, ...extra }).result;
    const list = respond("GET", { payload: [{ Id: "dev-1", Data: JSON.stringify({ host: "h" }) }] });
    assert.equal(list.statusCode, 200);
    assert.deepEqual(list.payload, [{ host: "h", deviceId: "dev-1" }]);
    assert.equal(respond("POST", { payload: [], device: { deviceId: "dev-1" } }).statusCode, 201);
    assert.equal(respond("PUT", { payload: [] }).statusCode, 404);
    assert.equal(respond("PUT", { payload: [{ Id: "dev-1" }], device: {} }).statusCode, 200);
    assert.equal(respond("DELETE", { payload: [] }).statusCode, 404);
    assert.equal(respond("DELETE", { payload: [{ Id: "dev-1" }] }).payload.deleted, true);
    assert.equal(respond("POST", { error: { message: "UNIQUE constraint failed: Devices.Id" } }).statusCode, 409);
    assert.equal(respond("POST", { error: { message: "disk I/O error" } }).statusCode, 500);
    const rejected = respond("POST", { statusCode: 400, payload: { error: "bad" } });
    assert.deepEqual([rejected.statusCode, rejected.payload.error], [400, "bad"]);
});

test("snapshot: rows become devices with a stable content version", () => {
    const rows = [{ Id: "dev-1", Data: JSON.stringify({ deviceId: "dev-1", enabled: true, metrics: [] }) }];
    const first = run(SNAPSHOT, { payload: rows }).result;
    assert.equal(first._kind, "config");
    assert.equal(first.payload.devices[0].id, "dev-1");
    assert.equal(run(SNAPSHOT, { payload: rows }).result.payload.version, first.payload.version);
    const changed = [{ Id: "dev-1", Data: JSON.stringify({ deviceId: "dev-1", enabled: false, metrics: [] }) }];
    assert.notEqual(run(SNAPSHOT, { payload: changed }).result.payload.version, first.payload.version);
});

test("snapshot: a corrupt row is skipped with a warning", () => {
    const { result, node } = run(SNAPSHOT, { payload: [{ Id: "bad", Data: "{" }, { Id: "ok", Data: "{}" }] });
    assert.deepEqual(result.payload.devices.map(d => d.id), ["ok"]);
    assert.match(node.warnings[0], /bad/);
});

test("only successful writes trigger a poller reload", () => {
    const changed = (method, statusCode) => run(CHANGED, { req: { method }, statusCode }).result;
    assert.deepEqual(changed("POST", 201), { _kind: "reload" });
    assert.deepEqual(changed("DELETE", 200), { _kind: "reload" });
    assert.equal(changed("GET", 200), null);
    assert.equal(changed("POST", 400), null);
    assert.equal(changed("PUT", 404), null);
});
