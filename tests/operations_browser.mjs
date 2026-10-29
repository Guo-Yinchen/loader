import { spawn } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, writeFileSync, rmSync, lstatSync } from 'node:fs';
import { join, resolve, relative } from 'node:path';

const root = resolve(import.meta.dirname, '..');
const output = join(root, '.local', 'maintenance-check');
const temporary = mkdtempSync(join(output, 'operations-browser-'));
const base = process.argv[2];
if (!/^http:\/\/127\.0\.0\.1:\d+$/.test(base)) throw new Error('An isolated loopback server is required.');
const chrome = spawn('C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', [
  '--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
  '--disable-extensions', '--remote-allow-origins=*', '--remote-debugging-port=0',
  `--user-data-dir=${temporary}`, 'about:blank'
], { stdio: 'ignore', windowsHide: true });
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
let socket;
try {
  const portFile = join(temporary, 'DevToolsActivePort');
  for (let n = 0; n < 100 && !existsSync(portFile); n++) await pause(100);
  if (!existsSync(portFile)) throw new Error('Chrome did not start.');
  const port = Number(readFileSync(portFile, 'utf8').split('\n')[0]);
  const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  socket = new WebSocket(pages.find(page => page.type === 'page').webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { socket.addEventListener('open', resolve, { once: true }); socket.addEventListener('error', reject, { once: true }); });
  let nextId = 0;
  const pending = new Map(), errors = [];
  socket.addEventListener('message', event => {
    const response = JSON.parse(event.data);
    if (response.method === 'Runtime.exceptionThrown') errors.push(response.params.exceptionDetails.text + ': ' + response.params.exceptionDetails.exception?.description);
    if (!response.id || !pending.has(response.id)) return;
    const { resolve, reject } = pending.get(response.id); pending.delete(response.id);
    if (response.error) reject(new Error(response.error.message)); else resolve(response.result);
  });
  const send = (method, params = {}) => new Promise((resolve, reject) => { const id = ++nextId; pending.set(id, { resolve, reject }); socket.send(JSON.stringify({ id, method, params })); });
  const evaluate = async expression => {
    const result = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text);
    return result.result.value;
  };
  const until = async expression => {
    for (let n = 0; n < 100; n++) { try { if (await evaluate(expression)) return; } catch {} await pause(100); }
    throw new Error('Timed out: ' + expression);
  };
  const check = async (name, expression) => { if (!await evaluate(expression)) throw new Error(name); process.stdout.write('PASS browser ' + name + '\n'); };
  const screenshot = async name => { const result = await send('Page.captureScreenshot', { format: 'png' }); writeFileSync(join(output, `operations-${name}.png`), Buffer.from(result.data, 'base64')); };
  await send('Page.enable'); await send('Runtime.enable');
  await send('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1080, deviceScaleFactor: 1, mobile: false });
  await send('Page.navigate', { url: base + '/admin/login' });
  await until('!!document.querySelector("[name=password]")');
  await evaluate(`document.querySelector('[name=username]').value='manager';document.querySelector('[name=password]').value='an excellent admin password';document.querySelector('form').requestSubmit()`);
  await until('!!document.getElementById("onlineUsers") && document.getElementById("onlineUsers").textContent!=="—"');
  await check('metrics and online table load', 'document.getElementById("receivedBytes").textContent!=="—" && document.getElementById("onlineRows").children.length>0');
  await screenshot('overview');
  await evaluate('location.hash="accounts"');
  await until('!document.getElementById("accounts").hidden');
  await check('pagination limits visible accounts', 'document.querySelectorAll(".account-row:not([hidden])").length===20');
  await evaluate('document.getElementById("accountNext").click()');
  await check('next page shows remaining accounts', 'document.querySelectorAll(".account-row:not([hidden])").length===6');
  await evaluate('document.getElementById("accountSearch").value="demo01";document.getElementById("accountSearch").dispatchEvent(new Event("input"))');
  await check('search resets pagination', 'document.querySelectorAll(".account-row:not([hidden])").length===1 && document.querySelector(".account-row:not([hidden])").dataset.username==="demo01"');
  await evaluate('document.getElementById("accountSearch").value="";document.getElementById("accountSearch").dispatchEvent(new Event("input"));document.getElementById("accountState").value="expired";document.getElementById("accountState").dispatchEvent(new Event("change"))');
  await check('expiry filter isolates expired account', 'document.querySelectorAll(".account-row:not([hidden])").length===1');
  await evaluate('document.getElementById("accountState").value="all";document.getElementById("accountState").dispatchEvent(new Event("change"))');
  await screenshot('accounts');
  await evaluate('document.querySelector(".account-row>summary").click()');
  await check('sensitive account actions initially collapsed', 'document.querySelectorAll(".account-row[open] .account-action[open]").length===0 && document.querySelectorAll(".account-row[open] .account-action").length>=4');
  await evaluate('location.hash="programs"'); await until('!document.getElementById("programs").hidden');
  await evaluate('document.querySelector(".program>summary").click()');
  await check('billing controls visible in program', '[...document.querySelectorAll(".program[open] form")].some(f=>f.action.endsWith("/billing")) && [...document.querySelectorAll(".program[open] form")].some(f=>f.action.endsWith("/compensate"))');
  await screenshot('programs');
  await evaluate('location.hash="activation"'); await until('!document.getElementById("activation").hidden');
  await check('card groups start collapsed', 'document.querySelectorAll(".key-scope[open]").length===0');
  await evaluate('document.querySelector(".key-scope>summary").click()');
  await check('program expands duration submenu', 'document.querySelectorAll(".key-scope[open] .key-duration").length===3');
  await evaluate('document.querySelector(".key-duration>summary").click()');
  await screenshot('activation');
  await send('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 1, mobile: true });
  for (const view of ['overview', 'accounts', 'programs', 'activation']) {
    await evaluate(`location.hash=${JSON.stringify(view)}`); await pause(100);
    await check('mobile fits viewport: ' + view, 'document.documentElement.scrollWidth<=window.innerWidth+1');
  }
  await screenshot('mobile');
  if (errors.length) throw new Error(errors.join('\n'));
  process.stdout.write('PASS browser no JavaScript runtime exceptions\n');
} finally {
  socket?.close(); chrome.kill();
  if (chrome.exitCode === null) await new Promise(resolve => chrome.once('exit', resolve));
  const path = resolve(temporary), within = relative(output, path);
  if (!within.startsWith('..') && !lstatSync(path).isSymbolicLink()) rmSync(path, { recursive: true, force: true, maxRetries: 10, retryDelay: 200 });
}
