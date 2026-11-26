const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const { buildCommit, findDevices, deviceState, deploy } = require('../../deploy/dotnet/qbee-deploy.cjs');
test('qbee template covers all parameters, isolates .NET project and does not delete other projects', () => {
  const commit = buildCommit({ group: 'dotnet-group', tag: 'sha-abc', registryUser: 'user', registryToken: 'secret' });
  const change = commit.changes.find(change => change.formtype === 'docker_compose'), item = change.config.items[0];
  assert.equal(change.node_id, 'dotnet-group'); assert.equal(item.name, 'iot-dotnet');
  assert.equal(item.file, '/iot-dotnet/compose-sha-abc.yaml'); assert.equal(item.skip_restart, true); assert.equal(change.config.clean, false);
  const template = fs.readFileSync(path.join(__dirname, '../../deploy/dotnet/compose.qbee.yaml'), 'utf8');
  assert.deepEqual([...new Set([...template.matchAll(/\{\{(\w+)\}\}/g)].map(m => m[1]))].sort(), item.parameters.map(p => p.key).sort());
  assert.equal(item.parameters.find(p => p.key === 'SIM_REPLICAS').value, '0');
  assert.equal(buildCommit({ simulator: true }).changes.find(change => change.formtype === 'docker_compose').config.items[0].parameters.at(-1).value, '1');
  assert.equal(item.parameters.find(p => p.key === 'DOTNET_MANAGEMENT_PASSWORD').value, '$(DOTNET_MANAGEMENT_PASSWORD)');
  assert.deepEqual(commit.changes.map(change => change.formtype), ['docker_compose']);
  assert.equal(item.pre_condition, undefined);
  assert.doesNotMatch(template, /PasswordFile|MANAGEMENT_PASSWORD_FILE|management-password/);
});
test('only --zerotier publishes the registry and the logged-in broker on the device ZeroTier IP', () => {
  const bind = options => buildCommit({ group: 'g', tag: 'sha-abc', ...options }).changes[0].config.items[0].parameters.find(p => p.key === 'DOTNET_REMOTE_BIND').value;
  assert.equal(bind({}), '127.0.0.2');
  assert.equal(bind({ zerotier: true }), '$(ZEROTIER_IP)');
  const template = fs.readFileSync(path.join(__dirname, '../../deploy/dotnet/compose.qbee.yaml'), 'utf8');
  const published = [...template.matchAll(/"([^"\s]+):\{\{(\w+)\}\}:(\d+)"/g)].map(([, host, port]) => `${host}:${port}`);
  assert.deepEqual(published.sort(), ['127.0.0.1:DOTNET_API_PORT', '127.0.0.1:DOTNET_MQTT_PORT',
    '{{DOTNET_REMOTE_BIND}}:DOTNET_API_PORT', '{{DOTNET_REMOTE_BIND}}:DOTNET_MQTT_PORT'].sort());
  // The broker leaves the device only with logins: anonymous clients are refused and the gateway signs in.
  const conf = fs.readFileSync(path.join(__dirname, '../../deploy/dotnet/mosquitto.conf'), 'utf8');
  assert.match(conf, /^allow_anonymous false$/m);
  assert.match(template, /MQTT_GATEWAY_PASSWORD: "\{\{DOTNET_MQTT_GATEWAY_PASSWORD\}\}"/);
  assert.match(template, /Mqtt__Password: "\{\{DOTNET_MQTT_GATEWAY_PASSWORD\}\}"/);
});
test('all .NET services must run the requested version; worker must be healthy', () => {
  const items = ['gateway', 'configuration', 'broker', 'simulator'].map(service => ({ image: `ghcr.io/fission-tech/iot-dotnet-${service}:sha-abc`, status: 'Up 1 minute (healthy)' }));
  assert.equal(deviceState({ docker_containers: { items } }, 'sha-abc', true).done, true);
  items[0].status = 'Up 1 minute (unhealthy)'; assert.equal(deviceState({ docker_containers: { items } }, 'sha-abc').done, false);
  items[0].status = 'Up 1 minute (healthy)'; items[1].image = items[1].image.replace('sha-abc', 'old');
  assert.equal(deviceState({ docker_containers: { items } }, 'sha-abc').done, false);
  assert.equal(deviceState(null, 'sha-abc').done, false);
  assert.deepEqual(findDevices({ type: 'group', nodes: [{ type: 'device', node_id: 'a' }, { type: 'group', nodes: [{ type: 'device', node_id: 'b' }] }] }), ['a', 'b']);
});
test('empty group fails before upload or commit', async () => {
  const calls = [], server = http.createServer((req, res) => { calls.push(req.url); res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify(req.url.endsWith('/login') ? { token: 'token' } : { type: 'group', nodes: [] })); });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  try {
    await assert.rejects(deploy({ api: `http://127.0.0.1:${server.address().port}`, group: 'empty', tag: 'sha-abc', email: 'email', password: 'password', registryUser: 'user', registryToken: 'token' }), /no devices/);
    assert.deepEqual(calls, ['/api/v2/login', '/api/v2/grouptree/empty']);
  } finally { await new Promise(resolve => server.close(resolve)); }
});
test('dry run masks registry token and never contacts qbee', async () => {
  const output = [];
  await deploy({ group: 'g', tag: 'sha-abc', registryToken: 'never-print-this', dryRun: true }, line => output.push(line));
  assert.ok(!output.join('').includes('never-print-this'));
});
test('rollout uploads and commits the version, then waits for every device in nested groups', async () => {
  const calls = [], reports = new Map(), output = [];
  let committed;
  const server = http.createServer(async (req, res) => {
    calls.push(req.url); let body = ''; for await (const data of req) body += data;
    let result = {};
    if (req.url.endsWith('/login')) result = { token: 'login-token' };
    else if (req.url.includes('/grouptree/')) result = { type: 'group', nodes: [{ type: 'device', node_id: 'edge-a' }, { type: 'group', nodes: [{ type: 'device', node_id: 'edge-b' }] }] };
    else if (req.url === '/api/v2/file') {
      assert.match(body, /compose-sha-abc.yaml/); assert.match(body, /iot-dotnet-gateway/);
    }
    else if (req.url === '/api/v2/commit') committed = JSON.parse(body);
    else if (req.url.includes('/inventoryreport/')) {
      const id = req.url.split('/').at(-1), count = (reports.get(id) || 0) + 1; reports.set(id, count);
      result = { docker_containers: { items: ['gateway', 'configuration', 'broker', 'simulator'].map(service => ({
        image: `ghcr.io/fission-tech/iot-dotnet-${service}:${count === 1 ? 'old' : 'sha-abc'}`, status: 'Up 1 minute (healthy)' })) } };
    }
    res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify(result));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  try {
    const result = await deploy({ api: `http://127.0.0.1:${server.address().port}`, group: 'dotnet-group', tag: 'sha-abc', simulator: true,
      email: 'email', password: 'password', registryUser: 'reader', registryToken: 'registry-secret', pollSeconds: 0.001 }, line => output.push(line));
    assert.deepEqual(result.devices, ['edge-a', 'edge-b']); assert.equal(committed.changes[0].node_id, 'dotnet-group');
    assert.equal(calls.filter(route => route === '/api/v2/file').length, 1);
    assert.ok(reports.get('edge-a') >= 2 && reports.get('edge-b') >= 2);
    assert.ok(calls.includes('/api/v2/device/edge-b/run-agent'));
    assert.ok(!output.join('').includes('registry-secret'));
  } finally { await new Promise(resolve => server.close(resolve)); }
});

for (const scenario of [
  { name: 'expired sessions renew and retry without uploading or committing again', codes: [401, 200, 401, 200], logins: 3 },
  { name: 'a second 401 fails promptly instead of renewing forever', codes: [401, 401], logins: 2, failure: 401 },
  { name: 'rejected credentials during renewal fail promptly without leaking the response', codes: [401], logins: 2, failure: 400, loginFailure: 400 },
  { name: 'permission failure stays explicit and does not trigger a login loop', codes: [403], logins: 1, failure: 403 },
  { name: 'temporary server errors are reported and recover on the next poll', codes: [503, 200], logins: 1, reportError: 503 },
  { name: 'a not-yet-available inventory report is reported and retried', codes: [404, 200], logins: 1, reportError: 404 },
  { name: 'rate-limited inventory requests remain visible and recover', codes: [429, 200], logins: 1, reportError: 429 },
  { name: 'malformed responses are reported without echoing their contents', codes: [200, 200], logins: 1, reportError: 'invalid JSON', invalidJSON: true }
]) {
  test(scenario.name, { timeout: 10000 }, async () => {
    let logins = 0, inventories = 0, uploads = 0, commits = 0;
    const output = [];
    const server = http.createServer(async (req, res) => {
      let body = ''; for await (const data of req) body += data;
      let result = {};
      if (req.url.endsWith('/login')) {
        assert.equal(JSON.parse(body).password, 'account-password');
        result = { token: 'session-token-' + ++logins };
        if (scenario.loginFailure && logins > 1) { res.statusCode = scenario.loginFailure; result = { error: 'account-password registry-secret session-token' }; }
      } else {
        assert.equal(req.headers.authorization, 'Bearer session-token-' + logins);
        if (req.url.includes('/grouptree/')) result = { type: 'group', nodes: [{ type: 'device', node_id: 'edge-a' }] };
        else if (req.url === '/api/v2/file') uploads++;
        else if (req.url === '/api/v2/commit') commits++;
        else if (req.url.includes('/inventoryreport/')) {
          res.statusCode = scenario.codes[Math.min(inventories++, scenario.codes.length - 1)];
          result = res.statusCode === 200 ? { docker_containers: { items: ['gateway', 'configuration', 'broker'].map(service => ({
            image: `ghcr.io/fission-tech/iot-dotnet-${service}:${inventories >= scenario.codes.length ? 'sha-abc' : 'old'}`,
            status: 'Up 1 minute (healthy)' })) } } : { error: 'Do not echo account-password registry-secret session-token-' + logins };
        }
      }
      if (scenario.invalidJSON && req.url.includes('/inventoryreport/') && inventories === 1) {
        res.end('not-json account-password registry-secret session-token'); return;
      }
      res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify(result));
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    try {
      const rollout = deploy({ api: `http://127.0.0.1:${server.address().port}`, group: 'dotnet-group', tag: 'sha-abc',
        email: 'email', password: 'account-password', registryUser: 'reader', registryToken: 'registry-secret',
        pollSeconds: 0.001, waitMinutes: 0.02 }, line => output.push(line));
      if (scenario.failure) {
        await assert.rejects(rollout, error => {
          assert.equal(error.status, scenario.failure);
          assert.match(error.message, new RegExp('failed \\(' + scenario.failure + '\\)'));
          assert.doesNotMatch(error.message, /account-password|registry-secret|session-token/);
          return true;
        });
      } else assert.deepEqual((await rollout).devices, ['edge-a']);
      assert.equal(logins, scenario.logins);
      assert.equal(inventories, scenario.codes.length);
      assert.equal(uploads, 1); assert.equal(commits, 1);
      assert.doesNotMatch(output.join('\n'), /account-password|registry-secret|session-token/);
      if (scenario.reportError) {
        assert.match(output.join('\n'), new RegExp('inventory unavailable:.*' + scenario.reportError));
        assert.doesNotMatch(output.join('\n'), /version not running/);
      }
    } finally { await new Promise(resolve => server.close(resolve)); }
  });
}
