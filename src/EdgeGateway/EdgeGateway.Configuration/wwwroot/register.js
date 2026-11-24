'use strict';
const $ = id => document.getElementById(id);
let devices = [], interfaces = [], brokers = [], assignments = [], runtime = null, saveAction;
const node = (tag, text, className) => { const el = document.createElement(tag); if (text !== undefined) el.textContent = text; if (className) el.className = className; return el; };
const button = (text, action) => { const el = node('button', text); el.type = 'button'; el.onclick = action; return el; };
async function api(path, method = 'GET', data) {
  const response = await fetch(path, { method, headers: data === undefined ? {} : { 'Content-Type': 'application/json' }, body: data === undefined ? undefined : JSON.stringify(data), cache: 'no-store' });
  if (!response.ok) {
    const body = await response.json().catch(() => ({}));
    throw new Error(body.message || body.detail || `Request failed (${response.status}).`);
  }
  return response.status === 204 ? null : response.json();
}
function notice(message) { $('notice').textContent = message; $('notice').hidden = !message; }
function label(text, input) { const el = node('label', text); el.append(input); return el; }
function input(value = '', type = 'text', required = true) { const el = node('input'); el.type = type; if (type === 'checkbox') el.checked = Boolean(value); else el.value = value ?? ''; el.required = required && type !== 'checkbox'; return el; }
function number(value, min, max, step = '1') { const el = input(value, 'number'); el.min = min; el.max = max; el.step = step; return el; }
function select(values, value) { const el = node('select'); for (const item of values) { const option = node('option', typeof item === 'string' ? item : item.text); option.value = typeof item === 'string' ? item : item.value; el.append(option); } el.value = value; el.required = true; return el; }
function openEditor(title, build, save) {
  $('editor-title').textContent = title; $('fields').replaceChildren(); $('form-error').hidden = true;
  build($('fields')); saveAction = save; $('editor').showModal();
}
$('close-editor').onclick = $('cancel-editor').onclick = () => $('editor').close();
$('edit-form').onsubmit = async event => {
  event.preventDefault(); $('save').disabled = true; $('form-error').hidden = true;
  try { await saveAction(); $('editor').close(); await refresh(); }
  catch (error) { $('form-error').textContent = error.message; $('form-error').hidden = false; }
  finally { $('save').disabled = false; }
};
async function mutate(action) { try { await action(); await refresh(); } catch (error) { notice(error.message); } }
async function remove(path, name) { if (confirm(`Delete ${name}?`)) await mutate(() => api(path, 'DELETE')); }
function row(parent, cells) { const tr = node('tr'); for (const value of cells) { const td = node('td'); td.append(value instanceof Node ? value : node('span', String(value))); tr.append(td); } parent.append(tr); }
function actions(...items) { const el = node('div', undefined, 'actions'); el.append(...items); return el; }
function showDevices() {
  const body = $('devices'); body.replaceChildren(); $('empty-devices').hidden = devices.length > 0;
  for (const device of devices) {
    const connection = interfaces.find(i => i.id === device.interfaceId);
    const status = runtime?.devices.find(d => d.deviceId === device.id);
    const details = node('div'); details.append(node('strong', device.id), node('div', device.description || '', 'muted'));
    row(body, [details, connection ? `${connection.settings.host || connection.settings.portName}:${connection.settings.port || ''} (${connection.id})` : device.interfaceId,
      `${device.unitId} / ${device.pollIntervalSeconds}s`, device.metrics.length,
      !device.enabled ? 'Disabled' : status?.state || 'Status unavailable',
      actions(button('Edit', () => editDevice(device)), button(device.enabled ? 'Disable' : 'Enable', () => mutate(() => api('/devices/' + encodeURIComponent(device.id), 'PUT', { ...device, metrics: device.metrics.map(metricPayload), enabled: !device.enabled }))), button('Delete', () => remove('/devices/' + encodeURIComponent(device.id), device.id)))]);
  }
}
// GET also exposes computed metric properties. Send only writable contract fields back to the strict API.
function metricPayload(m) { return { name: m.name, register: m.register, address: m.address, scale: m.scale, unit: m.unit, dataType: m.dataType, wordOrder: m.wordOrder, id: m.id }; }
function editDevice(existing) {
  if (!interfaces.some(i => i.type === 'ModbusTcp')) { notice('Add a TCP interface before adding a device.'); return; }
  let id, connection, unit, interval, description, enabled, metrics = [];
  openEditor(existing ? `Edit ${existing.id}` : 'Add device', fields => {
    const grid = node('div', undefined, 'grid');
    id = input(existing?.id); id.readOnly = Boolean(existing);
    connection = select(interfaces.map(i => ({ value: i.id, text: `${i.id} (${i.type})` })), existing?.interfaceId || interfaces.find(i => i.type === 'ModbusTcp').id);
    unit = number(existing?.unitId ?? 1, 0, 255); interval = number(existing?.pollIntervalSeconds ?? 5, 1, 3600);
    description = input(existing?.description, 'text', false); enabled = input(existing?.enabled ?? true, 'checkbox');
    grid.append(label('Device ID', id), label('Interface', connection), label('Unit ID', unit), label('Poll interval (seconds)', interval), label('Description', description), label('Enabled', enabled)); fields.append(grid, node('h3', 'Metrics'), node('p', 'Addresses are zero-based. Renaming a metric here keeps its identity.'));
    const list = node('div', undefined, 'metrics');
    function addMetric(metric = {}) {
      const el = node('div', undefined, 'metric');
      const name = input(metric.name), register = select(['holding', 'input', 'coil', 'discrete'], metric.register || 'holding');
      const address = number(metric.address ?? 0, 0, 65535), scale = number(metric.scale ?? 1, -1e12, 1e12, 'any'), unitText = input(metric.unit, 'text', false);
      const type = select(['int16', 'uint16', 'int32', 'uint32', 'float32', 'bool'], metric.dataType || (['coil', 'discrete'].includes(metric.register) ? 'bool' : 'int16'));
      const order = select(['big', 'little'], metric.wordOrder || 'big');
      const item = { el, read: () => ({ name: name.value, register: register.value, address: +address.value, scale: +scale.value, unit: unitText.value, dataType: type.value, wordOrder: order.value, id: metric.id ?? null }) };
      register.onchange = () => { type.value = ['coil', 'discrete'].includes(register.value) ? 'bool' : 'int16'; };
      el.append(label('Name', name), label('Register', register), label('Address', address), label('Scale', scale), label('Unit', unitText), label('Data type', type), label('Word order', order), button('✕', () => { el.remove(); metrics = metrics.filter(m => m !== item); }));
      metrics.push(item); list.append(el);
    }
    (existing?.metrics || [{}]).forEach(addMetric); fields.append(list, button('Add metric', () => addMetric()));
  }, () => {
    if (!metrics.length) throw new Error('At least one metric is required.');
    return api(existing ? '/devices/' + encodeURIComponent(existing.id) : '/devices', existing ? 'PUT' : 'POST', { id: id.value, interfaceId: connection.value, unitId: +unit.value, pollIntervalSeconds: +interval.value, enabled: enabled.checked, description: description.value, gpioPin: existing?.gpioPin ?? null, metrics: metrics.map(m => m.read()) });
  });
}
function editInterface(existing) {
  if (existing && existing.type !== 'ModbusTcp') { notice('RTU configuration is retained; this gateway currently polls TCP interfaces.'); return; }
  let id, host, port;
  openEditor(existing ? `Edit ${existing.id}` : 'Add interface', fields => {
    id = input(existing?.id); id.readOnly = Boolean(existing); id.placeholder = 'modsim-local';
    const type = select(['ModbusTcp', 'ModbusRtu'], 'ModbusTcp');
    type.querySelector('[value="ModbusRtu"]').disabled = true;
    host = input(existing?.settings.host); port = number(existing?.settings.port ?? 502, 1, 65535);
    const identity = node('div', undefined, 'grid'), settings = node('div', undefined, 'grid');
    identity.append(label('Interface ID', id), label('Type', type));
    settings.append(label('Host', host), label('TCP port', port));
    const count = devices.filter(device => device.interfaceId === existing?.id).length;
    fields.append(identity, settings, node('p', 'Use a host address reachable from this gateway. Modbus RTU is not available yet.', 'muted'),
      node('p', `${count} registered device(s) use this interface. Changes apply on the next configuration refresh.`, 'muted'));
  }, () => api(existing ? '/interfaces/' + encodeURIComponent(existing.id) : '/interfaces', existing ? 'PUT' : 'POST', { id: id.value, type: 'ModbusTcp', settings: { host: host.value, port: +port.value } }));
}
function editBroker(existing) {
  let id, host, port, tls, certificate, fingerprint, keepAlive, timeout, retry, maxRetry;
  openEditor(existing ? `Edit ${existing.id}` : 'Add MQTT broker', fields => {
    id = input(existing?.id); id.readOnly = Boolean(existing); host = input(existing?.host); port = number(existing?.port ?? 8883, 1, 65535); tls = input(existing?.useTls ?? true, 'checkbox');
    certificate = input(existing?.certificateLocation ?? '/certs/broker.crt', 'text', false); fingerprint = input(existing?.certificateSha256, 'text', false);
    keepAlive = number(existing?.keepAliveSeconds ?? 60, 5, 3600); timeout = number(existing?.connectTimeoutSeconds ?? 10, 1, 300); retry = number(existing?.retryDelaySeconds ?? 2, 1, 3600); maxRetry = number(existing?.maxRetryDelaySeconds ?? 30, 1, 3600);
    const grid = node('div', undefined, 'grid'); grid.append(label('Broker ID', id), label('Host', host), label('Port', port), label('Use TLS', tls), label('Trust certificate path on gateway or HTTPS URL', certificate), label('SHA-256 fingerprint (required for URL)', fingerprint), label('Keep alive (seconds)', keepAlive), label('Connect timeout (seconds)', timeout), label('Retry delay (seconds)', retry), label('Maximum retry (seconds)', maxRetry)); fields.append(grid);
  }, () => api(existing ? '/brokers/' + encodeURIComponent(existing.id) : '/brokers', existing ? 'PUT' : 'POST', { id: id.value, host: host.value, port: +port.value, useTls: tls.checked, certificateLocation: tls.checked ? certificate.value || null : null, certificateSha256: tls.checked ? fingerprint.value || null : null, keepAliveSeconds: +keepAlive.value, connectTimeoutSeconds: +timeout.value, retryDelaySeconds: +retry.value, maxRetryDelaySeconds: +maxRetry.value }));
}
function showRegistry() {
  showDevices(); $('interfaces').replaceChildren(); $('brokers').replaceChildren(); $('assignment').replaceChildren();
  for (const item of interfaces) row($('interfaces'), [item.id, item.type === 'ModbusTcp' ? `${item.settings.host}:${item.settings.port}` : `${item.type} (not polled)`, actions(button('Edit', () => editInterface(item)), button('Delete', () => remove('/interfaces/' + encodeURIComponent(item.id), item.id)))]);
  for (const item of brokers) row($('brokers'), [item.id, `${item.host}:${item.port} ${item.useTls ? 'TLS' : ''}`, item.certificateLocation || '—', actions(button('Edit', () => editBroker(item)), button('Delete', () => remove('/brokers/' + encodeURIComponent(item.id), item.id)))]);
  const gatewayId = runtime?.gatewayId;
  if (gatewayId && brokers.length) {
    const current = assignments.find(a => a.gatewayId === gatewayId), choice = select([{ value: '', text: 'Default broker' }, ...brokers.map(b => b.id)], current?.brokerId || ''); choice.required = false;
    $('assignment').append(node('p', gatewayId), choice, button('Save assignment', () => mutate(() => choice.value ? api('/gateways/' + encodeURIComponent(gatewayId), 'PUT', { gatewayId, brokerId: choice.value }) : current ? api('/gateways/' + encodeURIComponent(gatewayId), 'DELETE') : Promise.resolve())));
  } else $('assignment').append(node('p', 'Gateway identity is not available yet.'));
}
let refreshing = false;
async function refresh() {
  if (refreshing) return; refreshing = true;
  try { [devices, interfaces, brokers, assignments, runtime] = await Promise.all(['/devices', '/interfaces', '/brokers', '/gateways', '/device-status'].map(path => api(path))); showRegistry(); notice(''); }
  catch (error) { runtime = null; showDevices(); notice(error.message); }
  finally { refreshing = false; }
}
$('add-device').onclick = () => editDevice(); $('add-interface').onclick = () => editInterface(); $('add-broker').onclick = () => editBroker(); $('refresh').onclick = refresh;
refresh();
setInterval(async () => {
  if (refreshing || document.hidden || $('editor').open) return;
  try { runtime = await api('/device-status'); showDevices(); }
  catch { runtime = null; showDevices(); }
}, 3000);
