// Outbox, sequence numbers, broker history, broker switching and rollback, against a running Node-RED.
// The gateway is pointed at an in-process test broker that the tests stop and start; everything is restored afterwards.
const test = require("node:test");
const assert = require("node:assert/strict");
const net = require("node:net");
const path = require("node:path");
const sqlite3 = require("sqlite3");
const { api, deviceHost, request, gatewayStatus, reloadFlows, until, removeTestDevices, device, sleep } = require("../helpers/live");
const { createSim } = require("../helpers/modbus-sim");
const { createTestBroker } = require("../helpers/test-broker");

const gatewayDb = process.env.GATEWAY_DB || path.join(__dirname, "..", "..", "state", "gateway.db");
const BROKER_PORT = 18883;
const DEAD_PORT = 18999;
const broker = createTestBroker(BROKER_PORT);
const sim = createSim({ port: 15030, units: { 3: { holding: { 0: 5 } } } });
const testBroker = {
    id: "e2e-test", host: deviceHost, port: BROKER_PORT, useTls: false, certificateLocation: null, certificateSha256: null,
    keepAliveSeconds: 5, connectTimeoutSeconds: 2, retryDelaySeconds: 1, maxRetryDelaySeconds: 2
};
// A TCP relay in front of the test broker. Cutting it drops the gateway with no MQTT DISCONNECT, like a pulled
// cable or a killed process, while the broker itself keeps running and so publishes the gateway's Last Will.
const PROXY_PORT = 18884;
function createProxy(listenPort, targetPort) {
    const pairs = new Set();
    let server = null;
    const cut = () => {
        for (const pair of pairs) for (const socket of pair) socket.destroy();
        pairs.clear();
    };
    return {
        start: () => new Promise((resolve, reject) => {
            server = net.createServer(client => {
                const upstream = net.connect(targetPort, "127.0.0.1");
                const pair = [client, upstream];
                pairs.add(pair);
                client.pipe(upstream);
                upstream.pipe(client);
                for (const socket of pair) {
                    socket.on("error", () => {});
                    socket.on("close", () => { pairs.delete(pair); for (const other of pair) other.destroy(); });
                }
            });
            server.once("error", reject);
            server.listen(listenPort, process.env.E2E_LISTEN || "127.0.0.1", resolve);
        }),
        cut,
        stop: () => new Promise(resolve => { cut(); server ? server.close(() => resolve()) : resolve(); })
    };
}
const proxy = createProxy(PROXY_PORT, BROKER_PORT);
const proxyBroker = { ...testBroker, id: "e2e-proxy", port: PROXY_PORT };
let gatewayId;
let originalLocal;
const gatewayMessages = since => broker.published.filter(m => m.topic === "iot/gateway/" + gatewayId + "/status" && m.receivedAt >= since);

function query(sql, params = []) {
    return new Promise((resolve, reject) => {
        const db = new sqlite3.Database(gatewayDb, sqlite3.OPEN_READONLY, error => {
            if (error) return reject(error);
            db.all(sql, params, (queryError, rows) => {
                db.close();
                queryError ? reject(queryError) : resolve(rows);
            });
        });
    });
}
const r1 = () => broker.published.filter(m => m.topic === "iot/e2e-r1/telemetry").map(m => m.payload);
const latestHistory = async () => (await query("SELECT * FROM BrokerHistory ORDER BY Id DESC LIMIT 1"))[0];
const onBroker = id => async () => {
    const status = await gatewayStatus();
    return status.connection.state === "online" && status.broker.active?.id === id && status;
};
// Every reading number from the first to the last seen arrives at least once. Repeats are allowed (at-least-once).
function assertContiguous(seqs) {
    const unique = [...new Set(seqs)].sort((a, b) => a - b);
    for (let i = 1; i < unique.length; i++) assert.equal(unique[i], unique[i - 1] + 1, "gap after seq " + unique[i - 1]);
}

test.before(async () => {
    await removeTestDevices();
    await broker.start();
    await proxy.start();
    await sim.start();
    gatewayId = (await gatewayStatus()).gatewayId;
    assert.ok(gatewayId, "the gateway must be running");
    originalLocal = (await request("GET", "/brokers/local")).data;
    await request("DELETE", "/brokers/e2e-test");
    await request("DELETE", "/brokers/e2e-proxy");
});

test.after(async () => {
    if (originalLocal) await request("PUT", "/brokers/local", originalLocal);
    await request("DELETE", "/gateways/" + gatewayId);
    await until(onBroker("local"), { timeoutMs: 30000, what: "gateway back on local" }).catch(() => {});
    await request("DELETE", "/brokers/e2e-test");
    await request("DELETE", "/brokers/e2e-proxy");
    await removeTestDevices();
    await proxy.stop();
    await broker.stop();
    await sim.stop();
});

test("broker settings are validated like the .NET registry", async () => {
    const tls = { ...testBroker, id: "e2e-bad", useTls: true };
    for (const [bad, why] of [
        [{ ...tls, certificateLocation: null }, "TLS without a certificate"],
        [{ ...tls, certificateLocation: "http://example.com/ca.crt", certificateSha256: "a".repeat(64) }, "plain http certificate URL"],
        [{ ...tls, certificateLocation: "https://example.com/ca.crt" }, "certificate URL without a fingerprint"],
        [{ ...tls, certificateLocation: "C:\\ca.crt", certificateSha256: "xyz" }, "malformed fingerprint"],
        [{ ...testBroker, id: "e2e-bad", certificateLocation: "C:\\ca.crt" }, "certificate without TLS"],
        [{ ...testBroker, id: "e2e/bad" }, "slash in the ID"],
        [{ ...testBroker, id: "e2e-bad", port: 0 }, "port 0"],
        [{ ...testBroker, id: "e2e-bad", keepAliveSeconds: 4 }, "keep-alive under 5 s"],
        [{ ...testBroker, id: "e2e-bad", maxRetryDelaySeconds: 0 }, "maximum retry below the retry delay"],
        [{ ...testBroker, id: "e2e-bad", password: "secret" }, "unknown field"]
    ]) assert.equal((await request("POST", "/brokers", bad)).status, 400, why);
    assert.equal((await request("GET", "/brokers/e2e-missing")).status, 404);
    assert.equal((await request("DELETE", "/brokers/e2e-missing")).status, 404);
    assert.equal((await request("PUT", "/gateways/" + gatewayId, { gatewayId, brokerId: "e2e-missing" })).status, 400);
});

test("the gateway switches live to a broker assigned in the registry", { timeout: 30000 }, async () => {
    assert.equal((await request("POST", "/brokers", testBroker)).status, 201);
    assert.equal((await request("POST", "/brokers", testBroker)).status, 409, "duplicate ID");
    assert.equal((await api("POST", "", device("r1", 15030, 3, [{ name: "Level", register: "holding", address: 0, dataType: "int16", wordOrder: null, scale: 1, unit: "" }]))).status, 201);
    assert.equal((await request("PUT", "/gateways/" + gatewayId, { gatewayId, brokerId: "e2e-test" })).status, 200);
    const status = await until(onBroker("e2e-test"), { what: "gateway online on e2e-test" });
    assert.equal(status.broker.source, "registry");
    const announced = gatewayMessages(0).find(m => m.payload.state === "online");
    assert.ok(announced?.retain, "the gateway announces itself online, retained");
    assert.equal(status.statusTopic, "iot/gateway/" + gatewayId + "/status");
    await until(() => r1().length >= 3, { what: "readings on the test broker" });
    assert.equal((await request("DELETE", "/brokers/e2e-test")).status, 409, "an assigned broker cannot be deleted");
});

test("readings carry consecutive per-device sequence numbers", { timeout: 15000 }, async () => {
    await until(() => r1().length >= 5, { what: "five readings" });
    const live = r1();
    assertContiguous(live.map(p => p.seq));
    assert.ok(live.every(p => p.historical === false));
});

test("a broker outage buffers readings, records the outage and replays them in order", { timeout: 60000 }, async () => {
    const before = r1().length;
    await broker.stop();
    await until(async () => (await gatewayStatus()).connection.state !== "online", { what: "gateway to notice the outage" });
    await until(async () => (await gatewayStatus()).buffered >= 4, { what: "readings to buffer" });

    const outage = await latestHistory();
    assert.equal(outage.ReconnectedAt, null, "outage still open");
    const [{ n: linked }] = await query("SELECT COUNT(*) AS n FROM Outbox WHERE BrokerHistoryId = ?", [outage.Id]);
    assert.ok(linked >= 4, "buffered rows are linked to the outage");
    await sleep(3000); // retries during the same outage must not add history rows
    assert.equal((await latestHistory()).Id, outage.Id);

    await broker.start();
    await until(onBroker("e2e-test"), { timeoutMs: 15000, what: "reconnect" });
    await until(async () => (await gatewayStatus()).buffered === 0, { timeoutMs: 20000, what: "outbox to drain" });

    const after = r1().slice(before);
    const replayed = after.filter(p => p.historical);
    assert.ok(replayed.length >= 4, "buffered readings replayed as historical");
    const firstLive = after.findIndex(p => !p.historical);
    assert.ok(firstLive === -1 || after.slice(firstLive).every(p => !p.historical), "replay comes before live readings");
    assertContiguous(r1().map(p => p.seq));

    const closed = (await query("SELECT * FROM BrokerHistory WHERE Id = ?", [outage.Id]))[0];
    assert.ok(closed.ReconnectedAt, "reconnect recorded");
    assert.ok(closed.DowntimeSeconds >= 3, "downtime recorded");
    assert.ok(closed.MessagesBuffered >= linked);
    assert.equal(closed.BufferedMessagesSent, closed.MessagesBuffered, "every buffered message acknowledged and counted");
});

test("a redeploy during an outage keeps buffered readings and never reuses a sequence number", { timeout: 90000 }, async () => {
    await broker.stop();
    await until(async () => (await gatewayStatus()).buffered >= 3, { what: "readings to buffer" });
    const oldOutage = await latestHistory();
    const [{ LastSeq: lastBefore }] = await query("SELECT LastSeq FROM Devices WHERE DeviceId = 'e2e-r1'");

    const reloadedAt = new Date().toISOString();
    await reloadFlows();
    await until(async () => {
        const status = await gatewayStatus();
        return status.updatedAt > reloadedAt && status.connection.state !== "online";
    }, { what: "restarted gateway" });
    await until(async () => (await query("SELECT LastSeq FROM Devices WHERE DeviceId = 'e2e-r1'"))[0].LastSeq > lastBefore + 2,
        { what: "new readings after the restart" });
    const newOutage = await until(async () => {
        const row = await latestHistory();
        return row.Id > oldOutage.Id && row;
    }, { what: "a new outage row for the new run" });
    assert.equal(newOutage.ReconnectedAt, null);
    assert.equal((await query("SELECT ReconnectedAt FROM BrokerHistory WHERE Id = ?", [oldOutage.Id]))[0].ReconnectedAt, null,
        "the previous run's unfinished outage is not reopened");

    await broker.start();
    await until(onBroker("e2e-test"), { timeoutMs: 20000, what: "reconnect after restart" });
    await until(async () => (await gatewayStatus()).buffered === 0, { timeoutMs: 20000, what: "outbox to drain" });
    assertContiguous(r1().map(p => p.seq));
    const [old] = await query("SELECT * FROM BrokerHistory WHERE Id = ?", [oldOutage.Id]);
    const [{ n: oldPending }] = await query("SELECT COUNT(*) AS n FROM Outbox WHERE BrokerHistoryId = ?", [oldOutage.Id]);
    assert.equal(oldPending, 0);
    assert.ok(old.BufferedMessagesSent >= 3, "the old outage's messages still count when sent later");
});

test("the broker announces a vanished gateway offline through its Last Will; a clean switch announces it first", { timeout: 40000 }, async () => {
    assert.equal((await request("POST", "/brokers", proxyBroker)).status, 201);
    const switched = Date.now();
    assert.equal((await request("PUT", "/gateways/" + gatewayId, { gatewayId, brokerId: "e2e-proxy" })).status, 200);
    await until(onBroker("e2e-proxy"), { what: "gateway online through the proxy" });
    const leaving = gatewayMessages(switched).find(m => m.payload.state === "offline");
    assert.equal(leaving?.payload.reason, "switching to new broker settings", "a clean disconnect announces offline itself");
    assert.ok(leaving.retain);
    await until(() => gatewayMessages(switched).some(m => m.payload.state === "online" && m.payload.broker.startsWith("e2e-proxy")),
        { what: "online announced through the proxy" });

    const cut = Date.now();
    proxy.cut();
    const will = await until(() => gatewayMessages(cut).find(m => m.payload.state === "offline"), { timeoutMs: 10000, what: "the Last Will" });
    assert.equal(will.payload.reason, "connection lost");
    assert.ok(will.retain, "the Will is retained, so later subscribers see the gateway offline");
    await until(() => gatewayMessages(cut).some(m => m.payload.state === "online"), { timeoutMs: 15000, what: "online again after reconnecting" });
});

test("removing the assignment returns the gateway to its own broker", { timeout: 30000 }, async () => {
    assert.equal((await request("DELETE", "/gateways/" + gatewayId)).status, 204);
    const status = await until(onBroker("local"), { what: "gateway back on local" });
    assert.equal(status.broker.source, "registry");
});

test("settings that never connect are rejected after the trial and the last working ones restored", { timeout: 150000 }, async () => {
    assert.equal((await request("PUT", "/brokers/local", { ...originalLocal, port: DEAD_PORT })).status, 200);
    await until(async () => (await gatewayStatus()).broker.trying?.port === DEAD_PORT, { what: "trial to start" });
    const rejected = await until(async () => {
        const status = await gatewayStatus();
        return status.broker.rejected?.port === DEAD_PORT && status.connection.state === "online" && status;
    }, { timeoutMs: 120000, what: "rejection and rollback" });
    assert.equal(rejected.broker.active.port, originalLocal.port);
    assert.equal(rejected.broker.source, "last-good");
    assert.ok(rejected.broker.rejectedReason);

    assert.equal((await request("PUT", "/brokers/local", originalLocal)).status, 200);
    const restored = await until(async () => {
        const status = await gatewayStatus();
        return !status.broker.rejected && status.broker.source === "registry" && status;
    }, { what: "rejection to clear" });
    assert.equal(restored.connection.state, "online");
});
