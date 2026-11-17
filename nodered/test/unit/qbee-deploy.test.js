const test = require("node:test");
const assert = require("node:assert/strict");
const http = require("node:http");
const { buildCommit, findDevices, deviceState, deploy } = require("../../deploy/qbee-deploy");

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
        IMAGE_TAG: "sha-abc1234", GATEWAY_ID: "$(GATEWAY_ID)", NODE_RED_PASSWORD: "$(NODE_RED_PASSWORD)",
        NODE_RED_PORT: "$(NODE_RED_PORT)", MQTT_PORT: "$(MQTT_PORT)"
    });
    assert.deepEqual(change.config.registry_auths, [{ server: "ghcr.io", username: "bot", password: "tok" }]);
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
