const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const { buildCommit, findDevices, deviceState, deploy } = require('../../deploy/dotnet/qbee-deploy.cjs');
test('qbee template covers all parameters, isolates .NET project and does not delete other projects', () => {
  const commit = buildCommit({ group: 'dotnet-group', tag: 'sha-abc', registryUser: 'user', registryToken: 'secret' });
  const change = commit.changes[0], item = change.config.items[0];
  assert.equal(change.node_id, 'dotnet-group'); assert.equal(item.name, 'iot-dotnet');
  assert.equal(item.file, '/iot-dotnet/compose-sha-abc.yaml'); assert.equal(item.skip_restart, true); assert.equal(change.config.clean, false);
  const template = fs.readFileSync(path.join(__dirname, '../../deploy/dotnet/compose.qbee.yaml'), 'utf8');
  assert.deepEqual([...new Set([...template.matchAll(/\{\{(\w+)\}\}/g)].map(m => m[1]))].sort(), item.parameters.map(p => p.key).sort());
  assert.equal(item.parameters.find(p => p.key === 'SIM_REPLICAS').value, '0');
  assert.equal(buildCommit({ simulator: true }).changes[0].config.items[0].parameters.at(-1).value, '1');
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
    else if (req.url === '/api/v2/file') { assert.match(body, /compose-sha-abc.yaml/); assert.match(body, /iot-dotnet-gateway/); }
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
    assert.ok(reports.get('edge-a') >= 2 && reports.get('edge-b') >= 2);
    assert.ok(calls.includes('/api/v2/device/edge-b/run-agent'));
    assert.ok(!output.join('').includes('registry-secret'));
  } finally { await new Promise(resolve => server.close(resolve)); }
});
