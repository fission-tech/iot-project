const test = require("node:test");
const assert = require("node:assert/strict");
const http = require("node:http");
const { buildCommit, findDevices, deviceState, deploy, projectName } = require("../../deploy/qbee-deploy");

const IMAGE = "ghcr.io/fission-tech/iot-nodered";

test("the commit sets the image tag, versioned file and registry login, and leaves the rest to qbee parameters", () => {
    const commit = buildCommit({ group: "grp-1", tag: "sha-abc1234", registryUser: "bot", registryToken: "tok" });
    const [change] = commit.changes;
    assert.equal(change.node_id, "grp-1");
    assert.equal(change.formtype, "docker_compose");
    const [project] = change.config.items;
    assert.equal(project.name, "iot-nodered");
    assert.equal(project.file, "/iot-nodered/compose-sha-abc1234.yaml");
    const params = Object.fromEntries(project.parameters.map(p => [p.key, p.value]));
    assert.deepEqual(params, {
        IMAGE_TAG: "sha-abc1234", GATEWAY_ID: "$(GATEWAY_ID)", GATEWAY_INSTANCE: "1", NODE_RED_PASSWORD: "$(NODE_RED_PASSWORD)",
        MQTT_GATEWAY_PASSWORD: "$(MQTT_GATEWAY_PASSWORD)", MQTT_VIEWER_PASSWORD: "$(MQTT_VIEWER_PASSWORD)",
        REMOTE_BIND: "127.0.0.2", NODE_RED_PORT: "$(NODE_RED_PORT)", MQTT_PORT: "$(MQTT_PORT)", SIM_REPLICAS: "0"
    });
    assert.equal(project.skip_restart, true, "exited one-shot containers must not restart the project");
    assert.equal(change.config.clean, true, "projects no longer listed are removed");
    assert.deepEqual(change.config.registry_auths, [{ server: "ghcr.io", username: "bot", password: "tok" }]);
});

test("several gateways per device get their own project, number and ports; the simulator is switched on", () => {
    const { items } = buildCommit({ group: "g", tag: "t", instances: 3, simulator: true }).changes[0].config;
    const params = items.map(item => Object.fromEntries(item.parameters.map(p => [p.key, p.value])));
    assert.deepEqual(items.map(item => item.name), ["iot-nodered", "iot-nodered-2", "iot-nodered-3"]);
    assert.ok(items.every(item => item.file === "/iot-nodered/compose-t.yaml"));
    assert.deepEqual(params.map(p => p.GATEWAY_INSTANCE), ["1", "2", "3"]);
    assert.deepEqual(params.map(p => p.NODE_RED_PORT), ["$(NODE_RED_PORT)", "1881", "1882"]);
    assert.deepEqual(params.map(p => p.MQTT_PORT), ["$(MQTT_PORT)", "8884", "8885"]);
    assert.ok(params.every(p => p.GATEWAY_ID === "$(GATEWAY_ID)" && p.SIM_REPLICAS === "1"));
});

test("only --zerotier publishes the ports on the device's ZeroTier IP; otherwise a second loopback address", () => {
    const bind = options => buildCommit({ group: "g", tag: "t", instances: 2, ...options }).changes[0].config.items
        .map(item => item.parameters.find(p => p.key === "REMOTE_BIND").value);
    assert.deepEqual(bind({}), ["127.0.0.2", "127.0.0.2"]);
    assert.deepEqual(bind({ zerotier: true }), ["$(ZEROTIER_IP)", "$(ZEROTIER_IP)"]);
});

test("the Compose file publishes every port on 127.0.0.1 and REMOTE_BIND only, never on all addresses", () => {
    const compose = require("fs").readFileSync(require.resolve("../../deploy/compose.qbee.yaml"), "utf8");
    const published = [...compose.matchAll(/^\s+- "([^"]+:\d*\{?\{?\w*\}?\}?:\d+)"$/gm)].map(m => m[1]);
    assert.ok(published.length >= 4, "found the published ports");
    for (const entry of published) assert.match(entry, /^(127\.0\.0\.1|\{\{REMOTE_BIND\}\}):/, entry);
});

test("every template parameter in the Compose file is set by the commit", () => {
    const compose = require("fs").readFileSync(require.resolve("../../deploy/compose.qbee.yaml"), "utf8");
    const used = new Set([...compose.matchAll(/\{\{(\w+)\}\}/g)].map(m => m[1]));
    const set = new Set(buildCommit({ group: "g", tag: "t" }).changes[0].config.items[0].parameters.map(p => p.key));
    assert.deepEqual([...used].sort(), [...set].sort());
});

test("devices are found anywhere below the group", () => {
    const tree = { type: "group", node_id: "g", nodes: [
        { type: "device", node_id: "d1" },
        { type: "group", node_id: "sub", nodes: [{ type: "device", node_id: "d2" }] },
        { type: "group", node_id: "empty" }
    ] };
    assert.deepEqual(findDevices(tree), ["d1", "d2"]);
});

test("a device is ready only when Node-RED runs the new tag and is healthy", () => {
    const report = (image, status) => ({ docker_containers: { items: [
        { image: "ghcr.io/fission-tech/iot-nodered-broker:t2", status: "Up 1 minute" },
        { image, status }
    ] } });
    assert.equal(deviceState({}, "t2").done, false);
    assert.match(deviceState(report(IMAGE + ":t1", "Up 2 hours (healthy)"), "t2").detail, /runs .*:t1/);
    assert.equal(deviceState(report(IMAGE + ":t2", "Up 5 seconds (health: starting)"), "t2").done, false);
    assert.equal(deviceState(report(IMAGE + ":t2", "Up 1 minute (healthy)"), "t2").done, true);
});

test("with several gateways, every one must run the new tag and be healthy", () => {
    const gateway = (instance, tag, status) =>
        ({ names: ["/" + projectName(instance) + "-nodered-1"], image: IMAGE + ":" + tag, status });
    const report = (...items) => ({ docker_containers: { items } });
    const healthy = "Up 1 minute (healthy)";
    const partial = deviceState(report(gateway(1, "t2", healthy), gateway(2, "t1", healthy)), "t2", 3);
    assert.equal(partial.done, false);
    assert.match(partial.detail, /gateway 1: Up 1 minute \(healthy\); gateway 2: runs .*:t1; gateway 3: no Node-RED container yet/);
    const all = report(gateway(1, "t2", healthy), gateway(2, "t2", healthy), gateway(3, "t2", healthy));
    assert.equal(deviceState(all, "t2", 3).done, true);
    // Gateway 10 is not mistaken for gateway 1, and a plain string name works too.
    const tenth = { names: "iot-nodered-10-nodered-1", image: IMAGE + ":t2", status: healthy };
    assert.equal(deviceState(report(tenth), "t2").done, false);
});

// A fake qbee API: records requests, and device d1 runs the new tag only after it has been asked twice.
function fakeQbee() {
    const requests = [];
    let polls = 0;
    const server = http.createServer((req, res) => {
        let body = "";
        req.on("data", chunk => body += chunk);
        req.on("end", () => {
            requests.push({ method: req.method, url: req.url, auth: req.headers.authorization, type: req.headers["content-type"], body });
            const reply = (status, data) => { res.writeHead(status, { "Content-Type": "application/json" }); res.end(JSON.stringify(data)); };
            if (req.url === "/api/v2/login") return JSON.parse(body).password === "pw" ? reply(200, { token: "T" }) : reply(401, { error: "bad" });
            if (req.headers.authorization !== "Bearer T") return reply(401, { error: "no token" });
            if (req.url === "/api/v2/file") return reply(200, {});
            if (req.url === "/api/v2/commit") return reply(200, { sha: "c0ffee" });
            if (req.url === "/api/v2/grouptree/grp-1") return reply(200, { type: "group", nodes: [{ type: "device", node_id: "d1" }] });
            if (req.url === "/api/v2/grouptree") return reply(200, { tree: { type: "group", node_id: "root", title: "All devices", nodes: [
                { type: "group", node_id: "grp-1", title: "nodered-staging", nodes: [{ type: "device", node_id: "d1", title: "vm" }] }] } });
            if (req.url.startsWith("/api/v2/grouptree/")) return reply(404, { error: { code: 404, message: "node not found" } });
            if (req.url === "/api/v2/device/d1/run-agent") return reply(403, { error: "no remote access" });
            if (req.url === "/api/v2/inventoryreport/docker/d1") {
                polls++;
                const image = IMAGE + ":" + (polls >= 2 ? "v2" : "v1");
                return reply(200, { docker_containers: { items: [{ image, status: "Up 1 minute (healthy)" }] } });
            }
            reply(404, { error: "unknown " + req.url });
        });
    });
    return new Promise(resolve => server.listen(0, "127.0.0.1", () =>
        resolve({ server, requests, api: "http://127.0.0.1:" + server.address().port })));
}

const options = api => ({ api, group: "grp-1", tag: "v2", email: "ci@example.com", password: "pw",
    registryUser: "bot", registryToken: "tok", waitMinutes: 1, pollSeconds: 0.05 });

test("deploy logs in, uploads the file, commits, and waits until the device runs the new tag", async t => {
    const qbee = await fakeQbee();
    t.after(() => qbee.server.close());
    const log = [];
    const result = await deploy(options(qbee.api), line => log.push(line));
    assert.deepEqual(result.devices, ["d1"]);
    const urls = qbee.requests.map(r => r.method + " " + r.url);
    assert.deepEqual(urls.slice(0, 5), ["POST /api/v2/login", "GET /api/v2/grouptree/grp-1", "POST /api/v2/file",
        "POST /api/v2/commit", "GET /api/v2/device/d1/run-agent"]);
    const upload = qbee.requests[2];
    assert.match(upload.type, /^multipart\/form-data/);
    assert.match(upload.body, /filename="compose-v2.yaml"/);
    assert.match(upload.body, /image: ghcr\.io\/fission-tech\/iot-nodered:\{\{IMAGE_TAG\}\}/);
    assert.equal(JSON.parse(qbee.requests[3].body).changes[0].config.items[0].parameters[0].value, "v2");
    assert.ok(log.some(line => /Could not trigger d1/.test(line)), "a failed check-in trigger is only logged");
    assert.ok(log.some(line => /runs .*:v1/.test(line)));
    assert.match(log.at(-1), /All 1 device\(s\) run v2/);
});

test("deploy fails with the device's state when it never becomes ready", async t => {
    const qbee = await fakeQbee();
    t.after(() => qbee.server.close());
    await assert.rejects(deploy({ ...options(qbee.api), tag: "v3", waitMinutes: 0.002 }, () => {}),
        /Timed out .*d1 \(runs .*:v2\)/);
});

test("an unknown group stops before anything is changed and lists the group IDs", async t => {
    const qbee = await fakeQbee();
    t.after(() => qbee.server.close());
    await assert.rejects(deploy({ ...options(qbee.api), group: "nodered-staging" }, () => {}),
        /group "nodered-staging" not found .*404.*Group IDs: All devices = root, nodered-staging = grp-1$/);
    assert.deepEqual(qbee.requests.map(r => r.url), ["/api/v2/login", "/api/v2/grouptree/nodered-staging", "/api/v2/grouptree"]);
});

test("a wrong password stops before anything is changed", async t => {
    const qbee = await fakeQbee();
    t.after(() => qbee.server.close());
    await assert.rejects(deploy({ ...options(qbee.api), password: "wrong" }, () => {}), /login failed: 401/);
    assert.equal(qbee.requests.length, 1);
});

test("a dry run prints the commit with the registry password hidden and calls nothing", async () => {
    const log = [];
    await deploy({ ...options("http://127.0.0.1:9"), dryRun: true }, line => log.push(line));
    assert.match(log[0], /"password": "\*\*\*"/);
    assert.doesNotMatch(log[0], /tok/);
});
