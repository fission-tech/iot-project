// One Compose project per edge computer. Run on a dedicated .NET group, not the Node-RED group.
const fs = require('node:fs');
const path = require('node:path');
const PROJECT = 'iot-dotnet';
const DIRECTORY = '/iot-dotnet/';
const IMAGE_PREFIX = 'ghcr.io/fission-tech/iot-dotnet-';
const parameters = ['DOTNET_GATEWAY_ID', 'DOTNET_SPARKPLUG_GROUP', 'DOTNET_MANAGEMENT_PASSWORD', 'DOTNET_API_PORT', 'DOTNET_MQTT_PORT'];
function buildCommit({ group, tag, registryUser, registryToken, simulator = false }) {
  return {
    message: `Deploy .NET gateway ${tag}`,
    changes: [{ node_id: group, formtype: 'docker_compose', config: {
      enabled: true, version: 'v1', clean: false,
      items: [{ name: PROJECT, file: `${DIRECTORY}compose-${tag}.yaml`, skip_restart: true,
        parameters: [{ key: 'IMAGE_TAG', value: tag }, ...parameters.map(key => ({ key, value: `$(${key})` })), { key: 'SIM_REPLICAS', value: simulator ? '1' : '0' }] }],
      registry_auths: [{ server: 'ghcr.io', username: registryUser, password: registryToken }]
    } }]
  };
}
function findDevices(node) {
  if (!node) return [];
  return node.type === 'device' ? [node.node_id] : (node.nodes || []).flatMap(findDevices);
}
function deviceState(report, tag, simulator = false) {
  const containers = report?.docker_containers?.items || [];
  const services = ['gateway', 'configuration', 'broker', ...(simulator ? ['simulator'] : [])];
  const states = services.map(service => {
    const item = containers.find(c => c.image === `${IMAGE_PREFIX}${service}:${tag}`);
    const healthy = service === 'gateway' || service === 'configuration';
    const done = Boolean(item && (healthy ? /\(healthy\)/.test(item.status || '') : /^Up\b/.test(item.status || '')));
    return { done, detail: `${service}: ${item?.status || 'version not running'}` };
  });
  return { done: states.every(s => s.done), detail: states.map(s => s.detail).join('; ') };
}
function client(base, token) {
  return async (method, route, body) => {
    const headers = token ? { Authorization: `Bearer ${token}` } : {};
    const options = { method, headers, signal: AbortSignal.timeout(30000) };
    if (body instanceof FormData) options.body = body;
    else if (body !== undefined) { headers['Content-Type'] = 'application/json'; options.body = JSON.stringify(body); }
    const response = await fetch(base + route, options).catch(() => {
      throw new Error(`${method} ${route} failed (network or timeout).`);
    });
    if (!response.ok) throw Object.assign(new Error(`${method} ${route} failed (${response.status}). Check qbee credentials, group permissions and parameters.`), { status: response.status });
    const text = await response.text();
    try { return text ? JSON.parse(text) : null; }
    catch { throw new Error(`${method} ${route} returned invalid JSON.`); }
  };
}
async function authenticatedClient(base, email, password, log) {
  let call;
  async function login() {
    const { token } = await client(base)('POST', '/api/v2/login', { email, password });
    if (!token) throw new Error('qbee login returned no token.');
    call = client(base, token);
  }
  await login();
  return async (method, route, body) => {
    try { return await call(method, route, body); }
    catch (error) {
      if (error.status !== 401) throw error;
      log('qbee session expired or rejected; signing in again and retrying the request once.');
      await login();
      return call(method, route, body);
    }
  };
}
async function deploy(options, log = console.log) {
  const { group, tag, registryUser, registryToken, simulator = false, dryRun = false, waitMinutes = 20, pollSeconds = 30 } = options;
  if (!group || !/^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$/.test(tag || '')) throw new Error('A group ID and valid image tag are required.');
  if (!(waitMinutes > 0 && waitMinutes <= 120 && pollSeconds > 0)) throw new Error('Invalid polling or wait duration.');
  const commit = buildCommit(options);
  if (dryRun) {
    const safe = structuredClone(commit);
    safe.changes[0].config.registry_auths[0].password = '***';
    log(JSON.stringify(safe, null, 2)); return { devices: [] };
  }
  for (const key of ['email', 'password', 'registryUser', 'registryToken']) if (!options[key]) throw new Error(`Missing ${key}.`);
  const base = options.api || 'https://www.app.qbee.io';
  const call = await authenticatedClient(base, options.email, options.password, log);
  const tree = await call('GET', '/api/v2/grouptree/' + encodeURIComponent(group));
  if (tree.type !== 'group') throw new Error('Target must be a dedicated .NET group.');
  const devices = [...new Set(findDevices(tree))];
  if (!devices.length) throw new Error('The .NET group contains no devices; nothing was changed.');
  const form = new FormData();
  form.append('path', DIRECTORY); form.append('replace', 'true');
  form.append('file', new Blob([fs.readFileSync(path.join(__dirname, 'compose.qbee.yaml'))]), `compose-${tag}.yaml`);
  await call('POST', '/api/v2/file', form);
  await call('POST', '/api/v2/commit', commit);
  log(`Committed ${tag} to .NET group ${group} (${devices.length} edge computer(s)).`);
  for (const id of devices) await call('GET', `/api/v2/device/${encodeURIComponent(id)}/run-agent`).catch(() => log(`Waiting for the next agent check-in on ${id}.`));
  const deadline = Date.now() + waitMinutes * 60000, last = new Map();
  for (;;) {
    const states = [];
    for (const id of devices) {
      let state;
      try { state = deviceState(await call('GET', '/api/v2/inventoryreport/docker/' + encodeURIComponent(id)), tag, simulator); }
      catch (error) {
        if (error.status >= 400 && error.status < 500 && ![404, 429].includes(error.status)) throw error;
        state = { done: false, detail: `inventory unavailable: ${error.message}` };
      }
      states.push(state);
      if (last.get(id) !== state.detail) { log(`${id}: ${state.detail}`); last.set(id, state.detail); }
    }
    if (states.every(s => s.done)) { log('Every target runs the requested version and reports healthy.'); return { devices }; }
    if (Date.now() >= deadline) throw new Error('Timed out waiting for the group. Check qbee logs and parameters; the requested configuration remains applied.');
    await new Promise(resolve => setTimeout(resolve, pollSeconds * 1000));
  }
}
if (require.main === module) {
  const args = process.argv.slice(2), value = name => args[args.indexOf(name) + 1];
  const options = { group: args.includes('--group') ? value('--group') : undefined, tag: args.includes('--tag') ? value('--tag') : undefined,
    simulator: args.includes('--simulator'), dryRun: args.includes('--dry-run'),
    email: process.env.DOTNET_QBEE_EMAIL, password: process.env.DOTNET_QBEE_PASSWORD,
    registryUser: process.env.DOTNET_GHCR_USERNAME, registryToken: process.env.DOTNET_GHCR_TOKEN };
  deploy(options).catch(error => { console.error(error.message); process.exitCode = 1; });
}
module.exports = { buildCommit, findDevices, deviceState, deploy };
