// Deploys an image version of the Node-RED gateway to a qbee.io group, then waits until every device in it runs that
// version and Node-RED reports healthy. Used by .github/workflows/nodered-deploy.yml; also works by hand.
//
// Usage: node qbee-deploy.js --group <qbee group id> --tag <image tag> [--instances 1] [--simulator] [--wait-minutes 20]
//        [--dry-run]
//   --instances N  runs N independent gateways on each device (projects iot-nodered, iot-nodered-2, ...)
//   --simulator    also runs a simulated pump and tank next to each gateway, for devices without real ones
// Environment: QBEE_EMAIL, QBEE_PASSWORD, GHCR_USERNAME, GHCR_TOKEN (read:packages), QBEE_API (default below)
const fs = require("fs");
const path = require("path");

const PROJECT = "iot-nodered";
const IMAGE = "ghcr.io/fission-tech/iot-nodered";
const FILE_DIR = "/iot-nodered/";
const COMPOSE_FILE = path.join(__dirname, "compose.qbee.yaml");
const MAX_INSTANCES = 10;
// Host ports of gateway 2, 3, ...: gateway 1 uses the qbee parameters, the others count up from these.
const NODE_RED_PORT = 1880;
const MQTT_PORT = 8883;

// The Compose file is uploaded once per version, so a rollback also restores the matching file.
function composeFileName(tag) {
    return "compose-" + tag + ".yaml";
}

// Compose project of gateway n on a device. The first keeps the name it had before there were several.
function projectName(instance) {
    return instance === 1 ? PROJECT : PROJECT + "-" + instance;
}

// Template parameters of gateway n. GATEWAY_ID and the password point to qbee parameters, so each device sets its own
// ID and the password stays a qbee secret. Gateway 1 also takes its ports from qbee; the others get fixed ports
// above them, because a qbee parameter cannot be added to.
function instanceParameters(tag, instance, simulator) {
    const port = (parameter, base) => instance === 1 ? "$(" + parameter + ")" : String(base + instance - 1);
    return [
        { key: "IMAGE_TAG", value: tag },
        { key: "GATEWAY_ID", value: "$(GATEWAY_ID)" },
        { key: "GATEWAY_INSTANCE", value: String(instance) },
        { key: "NODE_RED_PASSWORD", value: "$(NODE_RED_PASSWORD)" },
        { key: "NODE_RED_PORT", value: port("NODE_RED_PORT", NODE_RED_PORT) },
        { key: "MQTT_PORT", value: port("MQTT_PORT", MQTT_PORT) },
        { key: "SIM_REPLICAS", value: simulator ? "1" : "0" }
    ];
}

// One commit replaces the group's whole Docker Compose configuration: one project per gateway, all from the same file.
function buildCommit({ group, tag, registryUser, registryToken, instances = 1, simulator = false }) {
    const items = Array.from({ length: instances }, (_, i) => ({
        name: projectName(i + 1),
        file: FILE_DIR + composeFileName(tag),
        parameters: instanceParameters(tag, i + 1, simulator),
        // The certs and sim-seed containers exit when done; that is no reason for qbee to restart the project.
        // Containers that crash are restarted by Docker (restart: unless-stopped).
        skip_restart: true
    }));
    return {
        message: "Deploy " + PROJECT + " " + tag,
        changes: [{
            node_id: group,
            formtype: "docker_compose",
            config: {
                enabled: true,
                version: "v1",
                items,
                // Removes projects qbee started before that this commit no longer lists, such as gateways after
                // --instances goes down.
                clean: true,
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

// "Title = node_id" for every group in a tree from GET /api/v2/grouptree, for error messages.
function listGroups(node) {
    if (!node || node.type === "device") return [];
    const own = node.node_id ? [(node.title ? node.title + " = " : "") + node.node_id] : [];
    return own.concat((node.nodes || []).flatMap(listGroups));
}

// Container name without Docker's leading slash; the inventory may give one name or a list.
function containerName(container) {
    const name = Array.isArray(container.names) ? container.names[0] : container.names;
    return String(name || "").replace(/^\//, "");
}

// The Node-RED container of one gateway, found by its Compose name (<project>-nodered-1). Gateway 1 also matches by
// image alone when the inventory has no names.
function gatewayContainer(containers, instance) {
    const prefix = projectName(instance) + "-nodered-";
    return containers.find(c => containerName(c).startsWith(prefix)) ||
        (instance === 1 ? containers.find(c => !containerName(c) && (c.image || "").startsWith(IMAGE + ":")) : undefined);
}

// A device is done when the Node-RED container of every gateway on it runs the new image and its health check passes.
function deviceState(report, tag, instances = 1) {
    const containers = report?.docker_containers?.items || [];
    const states = Array.from({ length: instances }, (_, i) => gatewayState(gatewayContainer(containers, i + 1), tag));
    const detail = instances === 1 ? states[0].detail : states.map((s, i) => "gateway " + (i + 1) + ": " + s.detail).join("; ");
    return { done: states.every(s => s.done), detail };
}

function gatewayState(nodered, tag) {
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
        const response = await fetch(base + url, options).catch(error => {
            throw new Error(method + " " + url + " failed: " + (error.cause?.message || error.message));
        });
        const text = await response.text();
        if (!response.ok) throw new Error(method + " " + url + " failed: " + response.status + " " + text.slice(0, 300));
        return text ? JSON.parse(text) : null;
    };
}

async function deploy(options, log = console.log) {
    const { api, group, tag, email, password, registryUser, registryToken, waitMinutes, pollSeconds, dryRun } = options;
    const instances = options.instances || 1;
    const commit = buildCommit({ group, tag, registryUser, registryToken, instances, simulator: options.simulator });
    if (dryRun) {
        log(JSON.stringify({ ...commit, changes: commit.changes.map(redact) }, null, 2));
        return { devices: [] };
    }

    const { token } = await client(api)("POST", "/api/v2/login", { email, password });
    if (!token) throw new Error("qbee login returned no token");
    const call = client(api, token);

    // Check the group first, so a wrong ID changes nothing and the error lists the IDs that would work.
    const groupNode = await call("GET", "/api/v2/grouptree/" + encodeURIComponent(group)).catch(async error => {
        const tree = await call("GET", "/api/v2/grouptree").catch(() => null);
        const groups = listGroups(tree?.tree || tree?.root || tree);
        throw new Error("qbee group " + JSON.stringify(group) + " not found (" + error.message + "). " +
            (groups.length ? "Group IDs: " + groups.join(", ") : "Could not list the groups."));
    });
    const devices = findDevices(groupNode);

    const form = new FormData();
    form.append("path", FILE_DIR);
    form.append("replace", "true");
    form.append("file", new Blob([fs.readFileSync(COMPOSE_FILE)]), composeFileName(tag));
    await call("POST", "/api/v2/file", form);
    log("Uploaded " + FILE_DIR + composeFileName(tag));

    const committed = await call("POST", "/api/v2/commit", commit);
    log("Committed " + (committed?.sha || "(no sha)") + ": " + commit.message + " to group " + group);

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
            const state = report.error ? { done: false, detail: report.error } : deviceState(report, tag, instances);
            if (states.get(id)?.detail !== state.detail) log(id + ": " + state.detail);
            states.set(id, state);
        }
        if ([...states.values()].every(s => s.done)) {
            log("All " + devices.length + " device(s) run " + tag + (instances > 1 ? " on " + instances + " gateways each" : "") +
                " and are healthy.");
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
        else if (name === "simulator") args.simulator = true;
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
        instances: Number(args.instances || 1),
        simulator: Boolean(args.simulator),
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
        console.error("Missing: " + missing.join(", ") + "\nUsage: node qbee-deploy.js --group <id> --tag <image tag> " +
            "[--instances 1] [--simulator] [--wait-minutes 20] [--dry-run]");
        process.exit(2);
    }
    if (!/^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$/.test(options.tag)) {
        console.error("Not a valid image tag: " + options.tag);
        process.exit(2);
    }
    if (!Number.isInteger(options.instances) || options.instances < 1 || options.instances > MAX_INSTANCES) {
        console.error("--instances must be a whole number from 1 to " + MAX_INSTANCES);
        process.exit(2);
    }
    deploy(options).catch(error => {
        console.error(error.message);
        // In GitHub Actions, also an annotation: it shows on the run's summary, which needs no sign-in to read.
        if (process.env.GITHUB_ACTIONS) console.log("::error title=qbee deploy failed::" + error.message.replace(/\r?\n/g, " "));
        process.exit(1);
    });
}

module.exports = { buildCommit, findDevices, listGroups, deviceState, deploy, composeFileName, projectName };
