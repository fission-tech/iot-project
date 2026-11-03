// Run the actual form functions: blank addresses must never become valid address zero.
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const html = fs.readFileSync(path.join(__dirname, '../../src/Demo.Control/wwwroot/index.html'), 'utf8');
const source = html.match(/<script>([\s\S]*?)<\/script>/)[1]
  .replace(/\(async function loop\(\)[\s\S]*?\}\)\(\);/, '');
const metric = { name: 'Temperature', register: 'holding', address: '', dataType: 'int16', wordOrder: 'big', scale: '0.1', unit: 'C' };
const values = { id: 'device', interfaceId: 'tcp', unitId: '1', pollIntervalSeconds: '1', gpioPin: '', description: '' };
const form = { elements: Object.fromEntries(Object.entries(values).map(([name, value]) => [name, { value }])) };
form.elements.enabled = { checked: true };
form.querySelectorAll = () => [{ querySelector: selector => ({ value: metric[selector.match(/data-m="(.*?)"/)[1]] }) }];
const error = { textContent: '' };
let requests = [];
const context = vm.createContext({ console, setTimeout, clearTimeout,
  document: { getElementById: id => id === 'device-form' ? form : error, addEventListener() {}, querySelectorAll() { return []; } },
  fetch: async (_, options) => {
    requests.push(JSON.parse(options.body));
    return { ok: true, json: async () => ({}) };
  } });
vm.runInContext(source, context);
vm.runInContext('toast = () => {}; closeDeviceForm = () => {};', context);
(async () => {
  for (const blank of ['', '   ']) {
    metric.address = blank;
    await vm.runInContext('saveDeviceForm()', context);
    assert.equal(requests.length, 0, 'A blank address was sent to the API');
    assert(error.textContent.includes('Temperature: address is required.'));
  }
  for (const address of ['0', '10', '65535']) {
    metric.address = address;
    await vm.runInContext('saveDeviceForm()', context);
    assert.equal(requests.at(-1).metrics[0].address, Number(address));
  }
  assert.equal(requests.length, 3);
  console.log('PASS: Actual device form blocks blank/whitespace addresses before sending and preserves explicit zero/nonzero/boundary addresses.');
})().catch(error => { console.error(error); process.exitCode = 1; });
