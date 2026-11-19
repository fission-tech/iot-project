// Simulated plant for staging devices, which have no real Modbus devices. Answers as every device registered with
// the gateway at this simulator's address (host modbus-sim, port 1502): add one on the /register page and within
// REGISTRY_REFRESH_SECONDS it answers with values that change over time. Units 1 and 2 are always the pump and tank
// that seed.js registers; their registers follow a small plant model instead.
// Usage: node simulator.js [port]   (default 1502)
// Environment: NODE_RED_URL (default http://nodered:1880), NODE_RED_USER, NODE_RED_PASSWORD,
// SIM_HOSTS (host names devices use for this simulator, comma separated; default modbus-sim)
const { createSim } = require("../../test/helpers/modbus-sim");
const { authHeaders } = require("./seed");

const REGISTRY_REFRESH_SECONDS = 15;

function float32Words(value) {
    const buffer = Buffer.alloc(4);
    buffer.writeFloatBE(value);
    return [buffer.readUInt16BE(0), buffer.readUInt16BE(2)];
}

// Values a given time after the start; exported so the tests can check them without a server.
function plantAt(elapsedMs) {
    const minutes = elapsedMs / 60000;
    // The pump stops for the last minute of every 10, which shows up as zero flow and a falling temperature.
    const running = minutes % 10 < 9;
    const temperature = 22 + (running ? 3 : 0) + 1.5 * Math.sin(minutes * 2 * Math.PI / 5);
    const flow = running ? 12 + 1.5 * Math.sin(minutes * 2 * Math.PI / 3) : 0;
    // The tank fills over 15 minutes and empties over 15, between 20 and 90 %.
    const phase = (minutes % 30) / 15;
    const level = 20 + 70 * (phase <= 1 ? phase : 2 - phase);
    return { running, temperature, flow, level, runMinutes: Math.floor(minutes * 0.9) };
}

// FNV-1a, so each metric gets its own fixed period and phase.
function hash(text) {
    let h = 0x811C9DC5;
    for (const char of text) h = Math.imul(h ^ char.charCodeAt(0), 0x01000193) >>> 0;
    return h;
}

// A value between 0 and 100 that rises and falls over 2 to 10 minutes, different for each key.
function signalAt(key, elapsedMs) {
    const h = hash(key);
    const periodMinutes = 2 + (h % 801) / 100;
    const phase = ((h >>> 12) % 360) * Math.PI / 180;
    return 50 + 40 * Math.sin(elapsedMs / 60000 * 2 * Math.PI / periodMinutes + phase);
}

const INTEGER_RANGES = { int16: [-32768, 32767], uint16: [0, 65535], int32: [-2147483648, 2147483647], uint32: [0, 4294967295] };

// The register words (or the bit) that the gateway decodes back to value, by the metric's type, scale and word order.
function encode(metric, value) {
    if (metric.dataType === "bool") return value > 30 ? 1 : 0;
    const raw = value / (Number(metric.scale) || 1);
    const buffer = Buffer.alloc(4);
    if (metric.dataType === "float32") {
        buffer.writeFloatBE(raw);
    } else {
        const [min, max] = INTEGER_RANGES[metric.dataType] || INTEGER_RANGES.int16;
        const whole = Math.min(max, Math.max(min, Math.round(raw)));
        if (metric.dataType === "int16" || metric.dataType === "uint16" || !INTEGER_RANGES[metric.dataType]) return whole & 0xFFFF;
        buffer.writeUInt32BE(whole >>> 0);
    }
    const words = [buffer.readUInt16BE(0), buffer.readUInt16BE(2)];
    return metric.wordOrder === "little" ? words.reverse() : words;
}

const TABLES = { holding: "holding", input: "input", coil: "coils", discrete: "discrete" };

// Register tables per unit ID for the devices that point at this simulator, at a given time after the start.
function unitsFor(devices, elapsedMs, { hosts, port }) {
    const units = {};
    for (const device of devices) {
        if (device.enabled === false || !hosts.includes(String(device.host).toLowerCase()) || Number(device.port) !== port) continue;
        const unit = units[device.unitId] ||= { holding: {}, input: {}, coils: {}, discrete: {} };
        for (const metric of device.metrics || []) {
            const table = unit[TABLES[metric.register]];
            if (!table) continue;
            const words = encode(metric, signalAt(device.deviceId + "/" + metric.name, elapsedMs));
            [].concat(words).forEach((word, i) => { table[Number(metric.address) + i] = word; });
        }
    }
    const plant = plantAt(elapsedMs);
    const pump = units[1] ||= { holding: {}, input: {}, coils: {}, discrete: {} };
    const tank = units[2] ||= { holding: {}, input: {}, coils: {}, discrete: {} };
    pump.holding[0] = Math.round(plant.temperature * 10) & 0xFFFF;
    [pump.holding[1], pump.holding[2]] = float32Words(plant.flow);
    pump.input[0] = plant.runMinutes & 0xFFFF;
    pump.coils[0] = plant.running ? 1 : 0;
    tank.holding[0] = Math.round(plant.level * 10);
    tank.discrete[0] = plant.level > 85 ? 1 : 0;
    return units;
}

// The gateway's registered devices, or null when its API does not answer.
async function fetchDevices(env) {
    const url = (env.NODE_RED_URL || "http://nodered:1880") + "/api/devices";
    const response = await fetch(url, { headers: authHeaders(env) });
    if (!response.ok) throw new Error(url + " answered " + response.status);
    return response.json();
}

if (require.main === module) {
    const port = Number(process.argv[2] || 1502);
    const hosts = (process.env.SIM_HOSTS || "modbus-sim").split(",").map(host => host.trim().toLowerCase()).filter(Boolean);
    const started = Date.now();
    const units = {};
    let devices = [];
    let registryError = null;
    let answering = "";

    // The same object stays with the server; its contents are replaced every second.
    function update() {
        const next = unitsFor(devices, Date.now() - started, { hosts, port });
        for (const key of Object.keys(units)) delete units[key];
        Object.assign(units, next);
        const now = Object.keys(next).join(", ");
        if (now !== answering) console.log("Answering as unit(s) " + now);
        answering = now;
    }

    async function refresh() {
        try {
            devices = await fetchDevices(process.env);
            registryError = null;
        } catch (error) {
            // Keeps the last known devices; logs only when the problem changes, since Node-RED may still be starting.
            const message = error.cause?.message || error.message;
            if (registryError !== message) console.log("Registry not readable, keeping the last devices: " + message);
            registryError = message;
        }
    }

    update();
    setInterval(update, 1000);
    refresh();
    setInterval(refresh, REGISTRY_REFRESH_SECONDS * 1000);
    const sim = createSim({ port, units, record: false });
    sim.start().then(() => {
        console.log("Simulated plant on " + (process.env.E2E_LISTEN || "127.0.0.1") + ":" + port + " for devices at " + hosts.join(", "));
    }, error => {
        console.error("Could not listen on port " + port + ": " + error.message);
        process.exit(1);
    });
}

module.exports = { plantAt, signalAt, encode, unitsFor };
