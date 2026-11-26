const test = require('node:test');
const assert = require('node:assert/strict');
const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '../..');
const project = 'dotnet-check-' + Date.now();
const directory = path.join(root, 'logs', 'dotnet-qbee');
fs.mkdirSync(directory, { recursive: true });
const compose = path.join(directory, project + '.yaml');
let password = process.env.EDGE_TEST_PASSWORD ?? 'ci-env-password-123';
const platform = process.env.EDGE_TEST_PLATFORM || 'linux/amd64';
const params = { IMAGE_TAG: process.env.EDGE_IMAGE_TAG || 'local', DOTNET_GATEWAY_ID: 'ci-edge-01', DOTNET_SPARKPLUG_GROUP: 'ci-plant', DOTNET_MANAGEMENT_PASSWORD: password, DOTNET_API_PORT: '5300', DOTNET_MQTT_PORT: '8883',
  DOTNET_MQTT_GATEWAY_PASSWORD: 'ci-gateway-mqtt', DOTNET_MQTT_VIEWER_PASSWORD: 'ci-viewer-mqtt', DOTNET_REMOTE_BIND: '127.0.0.2', SIM_REPLICAS: '1' };
const viewer = ['-u', 'viewer', '-P', params.DOTNET_MQTT_VIEWER_PASSWORD];
let template = fs.readFileSync(path.join(root, 'deploy/dotnet/compose.qbee.yaml'), 'utf8');
// Tests communicate inside Docker. No existing host port or application is touched.
template = template.replace(/^\s*ports:.*\r?\n/gm, '\n').replaceAll('ghcr.io/fission-tech/', '');
template = template.replace(/^(\s*)image: (.+)$/gm, `$&\n$1platform: ${platform}`);
function writeCompose() {
  fs.writeFileSync(compose, template.replace(/\{\{(\w+)\}\}/g, (_, key) => {
    if (!(key in params)) throw new Error('Unknown parameter ' + key);
    return params[key];
  }));
}
writeCompose();
function docker(...args) { return execFileSync('docker', args, { cwd: root, encoding: 'utf8', timeout: 210000, maxBuffer: 8 * 1024 * 1024, stdio: ['pipe', 'pipe', 'pipe'] }).trim(); }
function dc(...args) { return docker('compose', '-p', project, '-f', compose, ...args); }
function curl(route, { method = 'GET', data, auth = true, extra = [], json = true } = {}) {
  const args = ['exec', '-T', 'configuration', 'curl', '--silent', '--show-error', '--fail-with-body', '--max-time', '8'];
  if (auth) args.push('-u', 'admin:' + password);
  args.push(...extra, '-X', method);
  if (data !== undefined) args.push('-H', 'Content-Type: application/json', '--data', JSON.stringify(data));
  const output = dc(...args, 'http://127.0.0.1:5300' + route);
  return json && output ? JSON.parse(output) : output;
}
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
async function wait(reason, predicate, timeout = 65000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) { try { const value = predicate(); if (value) return value; } catch {} await delay(1000); }
  throw new Error('Timed out: ' + reason);
}
// Operator diagnostics stay in the worker console/file, not the registration API.
const status = () => JSON.parse(dc('exec', '-T', 'gateway', 'cat', '/state/status.json'));
const sample = () => status().devices?.find(d => d.deviceId === 'ci-edge-01-pump');
test('containerized edge stack: TLS, API, polling, outage buffering and persistent restart', { timeout: platform === 'linux/arm64' ? 600000 : 300000 }, async t => {
  try {
    dc('up', '-d', '--wait', '--wait-timeout', '180');
    await wait('live Modbus data', () => sample()?.lastValues?.Temperature?.number > 0);
    console.log('PASS: Fresh Docker volumes, non-root services, authenticated registry and all Modbus areas.');
    assert.throws(() => curl('/devices', { auth: false }), /failed|401|Command/);
    assert.throws(() => curl('/interfaces', { method: 'POST', data: {}, extra: ['-H', 'Origin: https://untrusted.example'] }));
    assert.match(curl('/register', { json: false }), /Device registry/);
    assert.match(curl('/register.js', { json: false }), /metricPayload/);
    const certificate = curl('/broker-certificate', { json: false });
    assert.match(certificate, /BEGIN CERTIFICATE/); assert.ok(!certificate.includes('PRIVATE KEY'));
    const tls = ['-h', 'localhost', '-p', '8883', '--cafile', '/mosquitto/public/broker.crt'];
    assert.match(dc('exec', '-T', 'mosquitto', 'mosquitto_sub', ...tls, ...viewer, '-t', 'spBv1.0/ci-plant/DDATA/#', '-C', '1', '-W', '20', '-F', '%t'), /DDATA\/ci-edge-01/);
    // Logins: anonymous clients are refused, and the viewer may read but not publish.
    assert.throws(() => dc('exec', '-T', 'mosquitto', 'mosquitto_sub', ...tls, '-t', 'spBv1.0/#', '-C', '1', '-W', '5'));
    const probe = 'spBv1.0/ci-plant/NCMD/viewer-probe';
    dc('exec', '-d', 'mosquitto', 'sh', '-c', `mosquitto_sub ${tls.join(' ')} -u gateway -P ${params.DOTNET_MQTT_GATEWAY_PASSWORD} -t ${probe} -C 1 -W 6 > /tmp/probe 2>&1`);
    await delay(1000);
    dc('exec', '-T', 'mosquitto', 'mosquitto_pub', ...tls, ...viewer, '-t', probe, '-m', 'should-be-dropped');
    await delay(6500);
    assert.doesNotMatch(dc('exec', '-T', 'mosquitto', 'cat', '/tmp/probe'), /should-be-dropped/, 'the viewer must not be able to publish');
    console.log('PASS: Broker logins: anonymous refused, viewer reads Sparkplug data but cannot publish.');
    // Check the Node TLS verification path used by Electron MQTT clients as well as the .NET publisher.
    assert.equal(docker('run', '--rm', '--network', project + '_default', '--mount',
      `type=volume,source=${project}_broker-public,target=/certs,readonly`, 'node:22-alpine', 'node', '-e',
      "const s=require('tls').connect({host:'mosquitto',port:8883,ca:require('fs').readFileSync('/certs/broker.crt'),rejectUnauthorized:true},()=>{console.log(s.authorized?'verified':'refused');s.end();});s.on('error',e=>{console.error(e.message);process.exitCode=1;});s.setTimeout(5000,()=>s.destroy(new Error('timeout')));"), 'verified');
    assert.throws(() => docker('run', '--rm', '--network', project + '_default', 'node:22-alpine', 'node', '-e',
      "const s=require('tls').connect({host:'mosquitto',port:8883,rejectUnauthorized:true},()=>s.end());s.on('error',e=>{console.error(e.message);process.exitCode=1;});s.setTimeout(5000,()=>s.destroy(new Error('timeout')));"), /self-signed certificate/i);
    assert.throws(() => dc('exec', '-T', 'mosquitto', 'mosquitto_sub', '-h', 'localhost', '-p', '8883', '-t', '#', '-C', '1', '-W', '3'));
    console.log('PASS: Real Sparkplug topic over verified TLS; untrusted broker refused.');
    const device = curl('/devices/ci-edge-01-pump'), metricId = device.metrics[0].id;
    device.description = 'Persist across deploy';
    curl('/devices/' + device.id, { method: 'PUT', data: device });
    const broker = curl('/brokers/local'); broker.keepAliveSeconds = 45;
    curl('/brokers/local', { method: 'PUT', data: broker });
    dc('restart', 'configuration');
    await wait('registry restarted', () => curl('/devices/' + device.id).description === device.description);
    assert.equal(curl('/brokers/local').keepAliveSeconds, 45);
    assert.equal(curl('/devices/' + device.id).metrics[0].id, metricId);
    const seq = (await wait('polling before gateway restart', sample)).lastSeq;
    dc('restart', 'gateway');
    await wait('sequence survives restart', () => sample()?.lastSeq > seq);
    assert.equal(curl('/brokers/local').keepAliveSeconds, 45);
    console.log('PASS: Registry, metric identity, edited broker and polling sequence survive service restarts.');
    dc('stop', 'mosquitto');
    await wait('outbox buffers during broker outage', () => status().unsent >= 3);
    const buffered = status().unsent;
    dc('restart', 'gateway');
    await wait('buffer survives gateway restart', () => status().unsent >= buffered);
    dc('start', 'mosquitto');
    await wait('broker reconnect and drain', () => status().upstream === 'Online' && status().unsent === 0);
    console.log('PASS: MQTT outage buffers to disk, survives restart and drains on reconnect.');
    const beforeCache = (await wait('polling after broker recovery', sample)).lastSeq;
    dc('stop', 'configuration'); dc('restart', 'gateway');
    await wait('cached polling without registry', () => {
      const value = JSON.parse(dc('exec', '-T', 'gateway', 'cat', '/state/status.json'));
      return !value.config.reachable && value.devices.find(d => d.deviceId === device.id)?.lastSeq > beforeCache;
    });
    dc('start', 'configuration');
    await wait('registry ready and cached polling continued', () => curl('/health', { json: false }) === 'ok' && sample()?.lastSeq > beforeCache);
    dc('stop', 'gateway'); await delay(16500);
    assert.deepEqual(curl('/device-status').devices, [], 'stopped worker must not report live devices');
    dc('start', 'gateway');
    await wait('worker returns', () => curl('/device-status').devices.length && sample()?.lastSeq > beforeCache);
    dc('down'); dc('up', '-d', '--wait', '--wait-timeout', '180');
    await wait('whole stack restart', () => sample()?.lastSeq > beforeCache);
    assert.equal(curl('/devices/' + device.id).description, device.description);
    assert.equal(curl('/devices/' + device.id).metrics[0].id, metricId);
    assert.equal(curl('/brokers/local').keepAliveSeconds, 45);
    assert.equal(curl('/broker-certificate', { json: false }), certificate, 'restart changed trusted certificate');
    console.log('PASS: Stale status detection and whole-stack recreation preserve data and certificates.');
    for (const chosenPassword of ['x', '']) {
      const previous = (await wait('polling before password rotation', sample)).lastSeq;
      password = chosenPassword; params.DOTNET_MANAGEMENT_PASSWORD = password; writeCompose();
      dc('up', '-d', '--wait', '--wait-timeout', '180');
      await wait('password change keeps authenticated polling and publishing', () =>
        curl('/devices/' + device.id).metrics[0].id === metricId && sample()?.lastSeq > previous && status().upstream === 'Online');
      assert.throws(() => curl('/devices', { auth: false }), /failed|401|Command/);
    }
    console.log('PASS: One-character and empty password rotations keep authentication, polling, MQTT and saved device identity.');
  } catch (error) {
    console.error(dc('logs', '--no-color', '--tail', '60')); throw error;
  } finally {
    dc('down', '-v', '--remove-orphans'); fs.unlinkSync(compose);
  }
});
