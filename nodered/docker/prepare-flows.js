// Writes the flow for the container: the repository's flows.json with machine paths replaced by container paths.
// Usage: node prepare-flows.js <source flows.json> <target flows.json>
const fs = require("fs");
const [source, target] = process.argv.slice(2);
const flows = JSON.parse(fs.readFileSync(source, "utf8"));

const containerEnv = {
    tab_modbus_mqtt: {
        GATEWAY_DB: "/data/state/gateway.db",
        GATEWAY_STATE_DIR: "/data/state",
        MQTT_CA: "/certs/broker.crt"
    },
    f937eaa01d565cc0: {
        REGISTRY_DB: "/data/state/registry.db",
        SEED_BROKER_CERTIFICATE: "/certs/broker.crt"
    }
};
for (const [tabId, values] of Object.entries(containerEnv)) {
    const tab = flows.find(node => node.id === tabId && node.type === "tab");
    if (!tab) throw new Error("Tab " + tabId + " not found in " + source);
    for (const [name, value] of Object.entries(values)) {
        const entry = tab.env?.find(item => item.name === name);
        if (!entry) throw new Error("Environment variable " + name + " not found on tab " + tab.label);
        entry.value = value;
    }
}
// Set per device when the container starts. A tab variable of type "env" named after itself reads the
// process environment; the Dockerfile gives each one a default.
const fromEnvironment = {
    tab_modbus_mqtt: ["GATEWAY_ID", "BROKER_ID", "MQTT_HOST", "MQTT_PORT", "MQTT_USERNAME", "MQTT_PASSWORD"],
    f937eaa01d565cc0: ["SEED_BROKER_HOST"]
};
for (const [tabId, names] of Object.entries(fromEnvironment)) {
    const tab = flows.find(node => node.id === tabId && node.type === "tab");
    for (const name of names) {
        const entry = tab.env?.find(item => item.name === name);
        if (!entry) throw new Error("Environment variable " + name + " not found on tab " + tab.label);
        entry.type = "env";
        entry.value = name;
    }
}

const registryDb = flows.find(node => node.type === "sqlitedb");
if (!registryDb) throw new Error("Device Registry DB config node not found");
registryDb.db = "/data/state/registry.db";

// Any Windows path left over would point nowhere inside the container.
const leftover = JSON.stringify(flows).match(/[A-Za-z]:\\\\[^"]*/);
if (leftover) throw new Error("A Windows path is still in the flow: " + leftover[0]);
fs.writeFileSync(target, JSON.stringify(flows, null, 2) + "\n");
console.log("Prepared " + target + " for the container.");
