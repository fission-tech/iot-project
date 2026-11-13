const test = require("node:test");
const assert = require("node:assert/strict");
const { nodeRedUrl, PREFIX, api, removeTestDevices, device } = require("../helpers/live");

// Disabled devices, so these API checks never trigger Modbus polling.
const sample = device("api", 15099, 1, [{ name: "Level", register: "holding", address: 0, dataType: "int16", wordOrder: null, scale: 1, unit: "%" }], { enabled: false });

test.before(removeTestDevices);
test.after(removeTestDevices);

test("the registry page is served", async () => {
    const response = await fetch(nodeRedUrl + "/register");
    assert.equal(response.status, 200);
    assert.match(await response.text(), /Device Registry/);
});

test("create, read, update and delete a device", async () => {
    assert.equal((await api("POST", "", sample)).status, 201);
    const listed = (await api("GET")).data.find(d => d.deviceId === sample.deviceId);
    assert.equal(listed.unitId, 1);

    const updated = await api("PUT", "/" + sample.deviceId, { ...sample, unitId: 5 });
    assert.equal(updated.status, 200);
    assert.equal((await api("GET")).data.find(d => d.deviceId === sample.deviceId).unitId, 5);

    assert.equal((await api("DELETE", "/" + sample.deviceId)).status, 200);
    assert.equal((await api("GET")).data.some(d => d.deviceId === sample.deviceId), false);
});

test("errors come back with the right status", async () => {
    assert.equal((await api("POST", "", { ...sample, port: 0 })).status, 400);
    assert.equal((await api("POST", "", sample)).status, 201);
    assert.equal((await api("POST", "", sample)).status, 409, "duplicate id");
    assert.equal((await api("PUT", "/" + PREFIX + "other", sample)).status, 400, "id must match the URL");
    assert.equal((await api("PUT", "/" + PREFIX + "missing", { ...sample, deviceId: PREFIX + "missing" })).status, 404);
    assert.equal((await api("DELETE", "/" + PREFIX + "missing")).status, 404);
});

test("unicode units survive a round trip", async () => {
    await api("PUT", "/" + sample.deviceId, { ...sample, metrics: [{ ...sample.metrics[0], unit: "°C" }] });
    assert.equal((await api("GET")).data.find(d => d.deviceId === sample.deviceId).metrics[0].unit, "°C");
});
