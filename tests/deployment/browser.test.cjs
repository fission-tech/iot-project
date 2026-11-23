const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const net = require('node:net');
const { spawn } = require('node:child_process');
const { chromium } = require('playwright');
const root = path.resolve(__dirname, '../..');
test('registry page creates and edits records without losing metric identity', { timeout: 60000 }, async () => {
  const logs = path.join(root, 'logs/dotnet-qbee'); fs.mkdirSync(logs, { recursive: true });
  const directory = fs.mkdtempSync(path.join(logs, 'browser-'));
  const listener = net.createServer(); await new Promise(resolve => listener.listen(0, '127.0.0.1', resolve));
  const port = listener.address().port; await new Promise(resolve => listener.close(resolve));
  const baseURL = `http://127.0.0.1:${port}`, password = 'browser-check-password-12345678';
  const statusPath = path.join(directory, 'status.json');
  const app = spawn('dotnet', [path.join(logs, 'browser-app/EdgeGateway.Configuration.dll'), '--Configuration:Url=' + baseURL,
    '--Configuration:DatabasePath=' + path.join(directory, 'configuration.db'), '--GatewayStatus:Path=' + statusPath,
    '--Gateway:Id=ui-edge-01', '--Management:RequireAuthentication=true', '--Management:Password=' + password],
    { cwd: root, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  let output = ''; app.stdout.on('data', data => output += data); app.stderr.on('data', data => output += data);
  let browser;
  try {
    for (let attempt = 0; attempt < 100; attempt++) {
      if (app.exitCode !== null) throw new Error(output);
      if (await fetch(baseURL + '/health').then(r => r.ok).catch(() => false)) break;
      await new Promise(resolve => setTimeout(resolve, 100));
    }
    browser = await chromium.launch(process.env.BROWSER_CHANNEL ? { channel: process.env.BROWSER_CHANNEL } : {});
    const context = await browser.newContext({ httpCredentials: { username: 'admin', password }, viewport: { width: 1400, height: 1000 } });
    const page = await context.newPage(), errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.goto(baseURL + '/register'); await page.getByRole('heading', { name: 'Device registry' }).waitFor();
    await page.getByRole('button', { name: 'Add interface', exact: true }).click();
    await page.getByLabel('Interface ID', { exact: true }).fill('ui-tcp');
    await page.getByLabel('Host reachable from the gateway VM').fill('192.168.10.50');
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    await page.getByText('192.168.10.50:502', { exact: true }).waitFor();
    await page.getByRole('button', { name: 'Add device', exact: true }).click();
    await page.getByLabel('Device ID', { exact: true }).fill('ui-meter-01');
    await page.getByLabel('Description', { exact: true }).fill('<img src=x onerror=alert(1)>');
    await page.getByLabel('Name', { exact: true }).fill('Temperature');
    await page.getByLabel('Unit', { exact: true }).fill('°C');
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    const row = page.locator('#devices tr').filter({ hasText: 'ui-meter-01' }); await row.waitFor();
    const auth = 'Basic ' + Buffer.from('admin:' + password).toString('base64');
    const read = route => fetch(baseURL + route, { headers: { Authorization: auth } }).then(r => r.json());
    const before = await read('/devices/ui-meter-01'); assert.ok(before.metrics[0].id);
    assert.equal(await row.locator('img').count(), 0, 'description must be plain text');
    await row.getByRole('button', { name: 'Edit', exact: true }).click();
    await page.getByLabel('Name', { exact: true }).fill('Room temperature');
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    await page.locator('#editor').waitFor({ state: 'hidden' });
    assert.equal((await read('/devices/ui-meter-01')).metrics[0].id, before.metrics[0].id);
    await row.getByRole('button', { name: 'Disable', exact: true }).click();
    await row.getByRole('button', { name: 'Enable', exact: true }).waitFor();
    assert.equal((await read('/devices/ui-meter-01')).enabled, false);
    await row.getByRole('button', { name: 'Enable', exact: true }).click();
    await row.getByRole('button', { name: 'Disable', exact: true }).waitFor();
    await page.locator('#brokers tr').first().getByRole('button', { name: 'Edit', exact: true }).click();
    await page.getByLabel('Keep alive (seconds)', { exact: true }).fill('45');
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    await page.locator('#editor').waitFor({ state: 'hidden' }); assert.equal((await read('/brokers/local')).keepAliveSeconds, 45);
    fs.writeFileSync(statusPath, JSON.stringify({ updatedAt: new Date(), gatewayId: 'ui-edge-01', devices: [{ deviceId: 'ui-meter-01', state: 'polling' }] }));
    await row.getByText('polling', { exact: true }).waitFor();
    assert.equal(await page.getByRole('heading', { name: 'Gateway status' }).count(), 0);
    assert.deepEqual(await read('/device-status'), { gatewayId: 'ui-edge-01', devices: [{ deviceId: 'ui-meter-01', state: 'polling' }] });
    await page.screenshot({ path: path.join(logs, 'registry-desktop.png'), fullPage: true });
    await page.setViewportSize({ width: 390, height: 844 });
    await page.screenshot({ path: path.join(logs, 'registry-mobile.png'), fullPage: true });
    fs.writeFileSync(statusPath, JSON.stringify({ updatedAt: new Date(Date.now() - 20000), gatewayId: 'ui-edge-01' }));
    await row.getByText('Status unavailable', { exact: true }).waitFor();
    assert.deepEqual(errors, []);
    console.log('PASS: Real browser CRUD, metric rename/identity, enable/disable, broker edit, safe text and basic fresh/stale device status.');
  } finally {
    if (browser) await browser.close();
    if (app.exitCode === null) { const ended = new Promise(resolve => app.once('exit', resolve)); app.kill(); await ended; }
    fs.writeFileSync(path.join(directory, 'service.log'), output);
  }
});
