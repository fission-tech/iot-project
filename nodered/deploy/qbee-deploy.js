// Deploys an image version of the Node-RED gateway to a qbee.io group, then waits until every device in it runs that
// version and Node-RED reports healthy. Used by .github/workflows/nodered-deploy.yml; also works by hand.
//
// Usage: node qbee-deploy.js --group <qbee group id> --tag <image tag> [--wait-minutes 20] [--dry-run]
// Environment: QBEE_EMAIL, QBEE_PASSWORD, GHCR_USERNAME, GHCR_TOKEN (read:packages), QBEE_API (default below)
const fs = require("fs");
const path = require("path");

const PROJECT = "iot-nodered";
const IMAGE = "ghcr.io/fission-tech/iot-nodered";
const FILE_DIR = "/iot-nodered/";
const COMPOSE_FILE = path.join(__dirname, "compose.qbee.yaml");

// The Compose file is uploaded once per version, so a rollback also restores the matching file.
function composeFileName(tag) {
    return "compose-" + tag + ".yaml";
}

// One commit replaces the group's whole Docker Compose configuration. Everything except the image tag points to
// qbee parameters, so each device can set its own GATEWAY_ID and ports and the password stays a qbee secret.
function buildCommit({ group, tag, registryUser, registryToken }) {
    return {
        message: "Deploy " + PROJECT + " " + tag,
        changes: [{
            node_id: group,
            formtype: "docker_compose",
            config: {
                enabled: true,
                version: "v1",
                items: [{
                    name: PROJECT,
                    file: FILE_DIR + composeFileName(tag),
                    parameters: [
                        { key: "IMAGE_TAG", value: tag },
                        { key: "GATEWAY_ID", value: "$(GATEWAY_ID)" },
                        { key: "NODE_RED_PASSWORD", value: "$(NODE_RED_PASSWORD)" },
                        { key: "NODE_RED_PORT", value: "$(NODE_RED_PORT)" },
                        { key: "MQTT_PORT", value: "$(MQTT_PORT)" }
                    ]
                }],
                registry_auths: [{ server: "ghcr.io", username: registryUser, password: registryToken }]
            }
        }]
    };
}

// Device IDs anywhere below a group node from GET /api/v2/grouptree/{id}.
function findDevices(node) {
    if (!node) return [];
    if (node.type === "device") return [node.node_id];
    return (node.nodes || []).flatMap(findDevices);
}

// A device is done when its Node-RED container runs the new image and its health check passes.
function deviceState(report, tag) {
    const containers = report?.docker_containers?.items || [];
    const nodered = containers.find(c => (c.image || "").startsWith(IMAGE + ":"));
    if (!nodered) return { done: false, detail: "no Node-RED container yet" };
    if (nodered.image !== IMAGE + ":" + tag) return { done: false, detail: "runs " + nodered.image };
    if (/\(healthy\)/.test(nodered.status || "")) return { done: true, detail: nodered.status };
    return { done: false, detail: "new version, " + (nodered.status || "no status") };
}

function client(base, token) {
    return async function call(method, url, body) {
        const headers = token ? { Authorization: "Bearer " + token } : {};
        const options = { method, headers };
        if (body instanceof FormData) options.body = body;
        else if (body !== undefined) {
            headers["Content-Type"] = "application/json";
            options.body = JSON.stringify(body);
        }
        const response = await fetch(base + url, options);
        const text = await response.text();
        if (!response.ok) throw new Error(method + " " + url + " failed: " + response.status + " " + text.slice(0, 300));
        return text ? JSON.parse(text) : null;
    };
}

async function deploy(options, log = console.log) {
    const { api, group, tag, email, password, registryUser, registryToken, waitMinutes, pollSeconds, dryRun } = options;
    const commit = buildCommit({ group, tag, registryUser, registryToken });
    if (dryRun) {
        log(JSON.stringify({ ...commit, changes: commit.changes.map(redact) }, null, 2));
        return { devices: [] };
    }

    const { token } = await client(api)("POST", "/api/v2/login", { email, password });
    if (!token) throw new Error("qbee login returned no token");
    const call = client(api, token);

    const form = new FormData();
    form.append("path", FILE_DIR);
    form.append("replace", "true");
    form.append("file", new Blob([fs.readFileSync(COMPOSE_FILE)]), composeFileName(tag));
    await call("POST", "/api/v2/file", form);
    log("Uploaded " + FILE_DIR + composeFileName(tag));

    const committed = await call("POST", "/api/v2/commit", commit);
    log("Committed " + (committed?.sha || "(no sha)") + ": " + commit.message + " to group " + group);

    const devices = findDevices(await call("GET", "/api/v2/grouptree/" + encodeURIComponent(group)));
    if (devices.length === 0) {
        log("The group has no devices; nothing to wait for.");
        return { devices };
    }
    // Ask each device to check in now instead of at its next interval. Needs remote access; not fatal without it.
    for (const id of devices) {
        await call("GET", "/api/v2/device/" + encodeURIComponent(id) + "/run-agent")
            .catch(error => log("Could not trigger " + id + ": " + error.message));
    }

    const deadline = Date.now() + waitMinutes * 60000;
    const states = new Map();
    for (;;) {
        for (const id of devices) {
            const report = await call("GET", "/api/v2/inventoryreport/docker/" + encodeURIComponent(id))
                .catch(error => ({ error: error.message }));
            const state = report.error ? { done: false, detail: report.error } : deviceState(report, tag);
            if (states.get(id)?.detail !== state.detail) log(id + ": " + state.detail);
            states.set(id, state);
        }
        if ([...states.values()].every(s => s.done)) {
            log("All " + devices.length + " device(s) run " + tag + " and are healthy.");
            return { devices };
        }
        if (Date.now() >= deadline) {
            const waiting = devices.filter(id => !states.get(id).done);
            throw new Error("Timed out after " + waitMinutes + " min; not ready: " +
                waiting.map(id => id + " (" + states.get(id).detail + ")").join(", "));
        }
        await new Promise(resolve => setTimeout(resolve, pollSeconds * 1000));
    }
}

function redact(change) {
    const auths = change.config.registry_auths.map(a => ({ ...a, password: a.password ? "***" : "" }));
    return { ...change, config: { ...change.config, registry_auths: auths } };
}

function parseArgs(argv) {
    const args = {};
    for (let i = 0; i < argv.length; i++) {
        const name = argv[i].replace(/^--/, "");
        if (name === "dry-run") args.dryRun = true;
        else args[name] = argv[++i];
    }
    return args;
}

if (require.main === module) {
    const args = parseArgs(process.argv.slice(2));
    const env = process.env;
    const options = {
        api: env.QBEE_API || "https://www.app.qbee.io",
        group: args.group,
        tag: args.tag,
        email: env.QBEE_EMAIL,
        password: env.QBEE_PASSWORD,
        registryUser: env.GHCR_USERNAME,
        registryToken: env.GHCR_TOKEN,
        waitMinutes: Number(args["wait-minutes"] || 20),
        pollSeconds: Number(args["poll-seconds"] || 30),
        dryRun: Boolean(args.dryRun)
    };
    const missing = ["group", "tag"].filter(k => !options[k]);
    if (!options.dryRun) {
        for (const [key, name] of [["email", "QBEE_EMAIL"], ["password", "QBEE_PASSWORD"],
            ["registryUser", "GHCR_USERNAME"], ["registryToken", "GHCR_TOKEN"]]) {
            if (!options[key]) missing.push(name);
        }
    }
    if (missing.length) {
        console.error("Missing: " + missing.join(", ") + "\nUsage: node qbee-deploy.js --group <id> --tag <image tag> [--wait-minutes 20] [--dry-run]");
        process.exit(2);
    }
    if (!/^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$/.test(options.tag)) {
        console.error("Not a valid image tag: " + options.tag);
        process.exit(2);
    }
    deploy(options).catch(error => {
        console.error(error.message);
        process.exit(1);
    });
}

module.exports = { buildCommit, findDevices, deviceState, deploy, composeFileName };
