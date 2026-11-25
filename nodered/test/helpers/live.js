// Talks to a running Node-RED (registry API) and the project broker (telemetry).
const fs = require("fs");
const os = require("os");
const path = require("path");
const mqtt = require("mqtt");

const nodeRedUrl = process.env.NODE_RED_URL || "http://127.0.0.1:1880";
const mqttUrl = process.env.MQTT_URL || "mqtts://127.0.0.1:8883";
const mqttCa = process.env.MQTT_CA || path.join(__dirname, "..", "..", "..", "broker", "certs", "broker.crt");
// The broker's read-only login (viewer); empty for a broker without logins.
const mqttLogin = process.env.MQTT_USERNAME ? { username: process.env.MQTT_USERNAME, password: process.env.MQTT_PASSWORD } : {};

// Where Node-RED reaches the test servers this suite starts. "auto" is this container's own address in Docker,
// because `docker compose run` does not register the service name.
function ownAddress() {
    const address = Object.values(os.networkInterfaces()).flat().find(item => item.family === "IPv4" && !item.internal);
    if (!address) throw new Error("E2E_HOST=auto: no network address found");
    return address.address;
}
const deviceHost = process.env.E2E_HOST === "auto" ? ownAddress() : process.env.E2E_HOST || "127.0.0.1";

// Every device a test creates starts with this prefix, so leftovers can be cleaned safely.
const PREFIX = "e2e-";

async function api(method, route = "", body) {
    const response = await fetch(nodeRedUrl + "/api/devices" + route, {
        method,
        headers: { "Content-Type": "application/json" },
        body: body === undefined ? undefined : JSON.stringify(body)
    });
    const text = await response.text();
    let data = null;
    try { data = JSON.parse(text); } catch { data = text; }
    return { status: response.status, data };
}

// Any registry endpoint under /api (brokers, gateways, gateway status).
async function request(method, route, body) {
    const response = await fetch(nodeRedUrl + "/api" + route, {
        method,
        headers: { "Content-Type": "application/json" },
        body: body === undefined ? undefined : JSON.stringify(body)
    });
    const text = await response.text();
    let data = null;
    try { data = text ? JSON.parse(text) : null; } catch { data = text; }
    return { status: response.status, data };
}

const gatewayStatus = async () => (await request("GET", "/gateway/status")).data;
// One device's polling status from the gateway status, or undefined.
const deviceStatus = async id => (await gatewayStatus()).devices?.find(device => device.deviceId === PREFIX + id);

// Restarts every flow from storage, like a redeploy: function nodes stop and start again.
async function reloadFlows() {
    const response = await fetch(nodeRedUrl + "/flows", {
        method: "POST",
        headers: { "Content-Type": "application/json", "Node-RED-Deployment-Type": "reload" },
        body: "{}"
    });
    if (!response.ok) throw new Error("Reload failed: " + response.status);
}

// Polls until check() returns something truthy, and returns it.
async function until(check, { timeoutMs = 15000, what = "condition" } = {}) {
    const deadline = Date.now() + timeoutMs;
    let last;
    while (Date.now() < deadline) {
        last = await check();
        if (last) return last;
        await new Promise(resolve => setTimeout(resolve, 200));
    }
    throw new Error("Timed out after " + timeoutMs + " ms waiting for " + what);
}

async function removeTestDevices() {
    const { data } = await api("GET");
    for (const device of Array.isArray(data) ? data : []) {
        if (device.deviceId.startsWith(PREFIX)) await api("DELETE", "/" + encodeURIComponent(device.deviceId));
    }
}

function device(id, port, unitId, metrics, extra = {}) {
    return { deviceId: PREFIX + id, host: deviceHost, port, unitId, pollIntervalSeconds: 1, enabled: true, description: "e2e test", metrics, ...extra };
}

// Collects every telemetry message so tests can wait for one that matches.
async function telemetry() {
    const ca = fs.readFileSync(mqttCa);
    const client = await mqtt.connectAsync(mqttUrl, { ca, clientId: "nodered-e2e-" + process.pid, reconnectPeriod: 0, ...mqttLogin });
    const messages = [];
    client.on("message", (topic, raw) => {
        try { messages.push({ topic, payload: JSON.parse(raw.toString()), receivedAt: Date.now() }); } catch { }
    });
    try {
        // "+" must fill a whole topic level, so filter test devices by deviceId below instead.
        await client.subscribeAsync(["iot/+/telemetry", "iot/+/status"]);
    } catch (error) {
        await client.endAsync(true);
        throw error;
    }

    // kind: "telemetry" (readings) or "status" (online/offline messages).
    const forDevice = (id, since, kind = "telemetry") => messages.filter(m =>
        m.payload.deviceId === PREFIX + id && m.receivedAt >= since && m.topic.endsWith("/" + kind));
    async function waitFor(id, predicate, { since = 0, timeoutMs = 10000, kind = "telemetry" } = {}) {
        const deadline = Date.now() + timeoutMs;
        while (Date.now() < deadline) {
            const match = forDevice(id, since, kind).find(m => predicate(m.payload));
            if (match) return match.payload;
            await new Promise(resolve => setTimeout(resolve, 100));
        }
        const last = forDevice(id, since, kind).at(-1);
        throw new Error(`No matching ${kind} for ${PREFIX}${id} within ${timeoutMs} ms; last: ${last ? JSON.stringify(last.payload) : "none"}`);
    }
    return { messages, forDevice, waitFor, close: () => client.endAsync() };
}

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

module.exports = { nodeRedUrl, deviceHost, PREFIX, api, request, gatewayStatus, deviceStatus, reloadFlows, until, removeTestDevices, device, telemetry, sleep };
