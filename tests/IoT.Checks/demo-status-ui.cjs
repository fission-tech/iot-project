// Exercise the actual page functions with a small DOM fixture; no demo services/data are changed.
const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const path = require('node:path');
const html = fs.readFileSync(path.join(__dirname, '../../src/Demo.Control/wwwroot/index.html'), 'utf8');
const source = html.match(/<script>([\s\S]*?)<\/script>/)[1]
  .replace(/\(async function loop\(\)[\s\S]*?\}\)\(\);/, '');
const elements = new Map();
const element = id => {
  if (!elements.has(id)) elements.set(id, { innerHTML: '', textContent: '', children: [], dataset: {},
    classList: { toggle() {}, add() {}, remove() {}, contains() { return false; } } });
  return elements.get(id);
};
let running = true, snapshot = null, registryAvailable = true;
let devices = [{ id: 'meter-01', enabled: true }, { id: 'disabled-01', enabled: false }];
const context = vm.createContext({ console, setTimeout, clearTimeout,
  document: { getElementById: element, addEventListener() {}, querySelectorAll() { return []; } },
  fetch: async url => ({ ok: url !== '/api/config/devices' || registryAvailable, status: 503,
    json: async () => url === '/api/state' ? { gateway: snapshot, components: [
      { name: 'gateway', running, actions: [], kind: 'process' },
      { name: 'configuration', running: true, actions: [], kind: 'process' },
      { name: 'mosquitto', running: false, actions: [], kind: 'container' }],
      configurationReachable: true, monitorConnected: false, monitors: { upstream: false } }
      : url === '/api/config/devices' ? devices : [] }) });
vm.runInContext(source, context);
vm.runInContext('renderView = () => {}; renderScenario = () => {}; renderFeed = async () => {};', context);
const refresh = () => vm.runInContext('refresh()', context);
(async () => {
  await refresh();
  assert(element('pipeline').innerHTML.includes('meter-01'), 'Enabled device must appear without status/MQTT');
  assert(!element('pipeline').innerHTML.includes('disabled-01'), 'Disabled registry device should not be added to data path');
  assert(element('g-kv').innerHTML.includes('running · live status temporarily unavailable'));
  assert(!element('controls').innerHTML.includes('Gateway not running'));
  snapshot = { upstream: 'RetryWaiting', unsent: 7, hostOnline: true, bdSeq: 4,
    devices: [{ deviceId: 'meter-01', endpoint: '127.0.0.1:502 unit 1', state: 'polling', lastSeq: 8 }] };
  await refresh();
  assert.equal(element('g-unsent').textContent, 7);
  assert(element('d-meter-01').textContent.includes('polling'));
  snapshot.devices[0] = { ...snapshot.devices[0], state: 'failing', failures: 0,
    lastError: 'holding 200+1: IllegalDataAddress', lastRaw: [], frames: ['holding 0+2', 'holding 200+1'] };
  await refresh();
  assert(element('d-gateway').textContent.includes('polling 1/1'), 'Partial frame failure hid active polling');
  const detail = vm.runInContext("pollStatus({id:'meter-01', enabled:true})", context);
  assert(detail.includes('frame read error: holding 200+1: IllegalDataAddress'));
  assert(!detail.includes('0 failed poll'), 'Partial quality error was described as a communication failure');
  snapshot.devices[0].failures = 2;
  assert(vm.runInContext("pollStatus({id:'meter-01', enabled:true})", context).includes('2 consecutive communication failure(s)'));
  snapshot = null; registryAvailable = false;
  await refresh();
  assert(element('pipeline').innerHTML.includes('meter-01'), 'Temporary registry failure lost last registry list');
  assert(!element('g-kv').innerHTML.includes('gateway not running'));
  running = false;
  await refresh();
  assert(element('controls').innerHTML.includes('Gateway not running'));
  registryAvailable = true; devices = [];
  await refresh();
  assert(element('pipeline').innerHTML.includes('No devices'), 'Removed devices remained after registry refresh');
  console.log('PASS: Actual Demo UI functions: registry fallback, disabled exclusion, broker-down polling/buffer, transient status/registry failures, stopped gateway and device removal.');
})().catch(error => { console.error(error); process.exitCode = 1; });
