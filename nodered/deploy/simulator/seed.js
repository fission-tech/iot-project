// Registers the simulated plant (simulator.js) with this device's gateway. Runs once as a container next to Node-RED;
// a device that already exists is left as it is, so restarts and redeploys change nothing someone edited.
// Environment: NODE_RED_URL (default http://nodered:1880), NODE_RED_USER (default admin), NODE_RED_PASSWORD,
// GATEWAY_ID, GATEWAY_INSTANCE, SIM_HOST (default modbus-sim), SIM_PORT (default 1502)

// The gateway's own ID, by the same rule as docker/settings.js: extra gateways on a device get their number appended.
function gatewayId(env) {
    const instance = Number(env.GATEWAY_INSTANCE || 1);
    return (env.GATEWAY_ID || "nodered-gateway-01") + (instance > 1 ? "-" + instance : "");
}

const metric = (name, register, address, dataType, extra = {}) =>
    ({ name, register, address, dataType, wordOrder: null, scale: 1, unit: "", ...extra });

// Device IDs start with the gateway ID, so readings from several simulated gateways stay apart in the cloud.
function plantDevices({ gateway, host, port }) {
    const device = (name, unitId, description, metrics) =>
        ({ deviceId: gateway + "-" + name, host, port, unitId, pollIntervalSeconds: 5, enabled: true, description, metrics });
    return [
        device("pump", 1, "Simulated pump", [
            metric("Temperature", "holding", 0, "int16", { scale: 0.1, unit: "°C" }),
            metric("Flow", "holding", 1, "float32", { wordOrder: "big", unit: "m3/h" }),
            metric("RunMinutes", "input", 0, "uint16", { unit: "min" }),
            metric("Running", "coil", 0, "bool")
        ]),
        device("tank", 2, "Simulated tank", [
            metric("Level", "holding", 0, "int16", { scale: 0.1, unit: "%" }),
            metric("HighLevel", "discrete", 0, "bool")
        ])
    ];
}

async function seed(env = process.env, log = console.log, { attempts = 30, delayMs = 2000 } = {}) {
    const url = (env.NODE_RED_URL || "http://nodered:1880") + "/api/devices";
    const headers = { "Content-Type": "application/json" };
    if (env.NODE_RED_PASSWORD) {
        headers.Authorization = "Basic " + Buffer.from((env.NODE_RED_USER || "admin") + ":" + env.NODE_RED_PASSWORD).toString("base64");
    }
    const devices = plantDevices({ gateway: gatewayId(env), host: env.SIM_HOST || "modbus-sim", port: Number(env.SIM_PORT || 1502) });

    for (const device of devices) {
        for (let attempt = 1; ; attempt++) {
            // Node-RED can still be starting even after its health check passes once; retry until the API answers.
            const response = await fetch(url, { method: "POST", headers, body: JSON.stringify(device) }).catch(error => error);
            const status = response instanceof Error ? null : response.status;
            if (status === 201) { log("Registered " + device.deviceId); break; }
            if (status === 409) { log(device.deviceId + " already registered; left as it is"); break; }
            const detail = status ? status + " " + (await response.text()).slice(0, 200) : response.cause?.message || response.message;
            // A rejected device or a wrong password does not get better by retrying.
            if (status === 400 || status === 401 || attempt >= attempts) throw new Error("Could not register " + device.deviceId + ": " + detail);
            await new Promise(resolve => setTimeout(resolve, delayMs));
        }
    }
}

if (require.main === module) {
    seed().catch(error => {
        console.error(error.message);
        process.exit(1);
    });
}

module.exports = { seed, plantDevices, gatewayId };
