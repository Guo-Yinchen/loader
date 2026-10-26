import { spawn } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const root = resolve(import.meta.dirname, '..');
const temporary = mkdtempSync(join(root, '.local', 'icon-browser-'));
const chromePath = 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const chrome = spawn(chromePath, [
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
  await new Promise((resolve, reject) => {
    socket.addEventListener('open', resolve, { once: true });
    socket.addEventListener('error', reject, { once: true });
  });
  let nextId = 0;
  const pending = new Map();
  socket.addEventListener('message', event => {
    const response = JSON.parse(event.data);
    if (!response.id || !pending.has(response.id)) return;
    const { resolve, reject } = pending.get(response.id);
    pending.delete(response.id);
    if (response.error) reject(new Error(response.error.message));
    else resolve(response.result);
  });
  const send = (method, params = {}) => new Promise((resolve, reject) => {
    const id = ++nextId;
    pending.set(id, { resolve, reject });
    socket.send(JSON.stringify({ id, method, params }));
  });
  await send('Page.enable');
  await send('Runtime.enable');
  await send('Page.navigate', { url: pathToFileURL(join(root, 'tests', 'icon_crop_browser.html')).href });
  let result;
  for (let n = 0; n < 100; n++) {
    await pause(100);
    try {
      const response = await send('Runtime.evaluate', {
        expression: 'document.body?.dataset.result || ""', returnByValue: true
      });
      result = response.result.value;
      if (result) break;
    } catch { /* Navigation briefly replaces the execution context. */ }
  }
  if (result !== 'PASS') throw new Error(result || 'Crop conversion timed out.');
  process.stdout.write('PASS: browser crops JPG preview and uploads a valid PNG\n');
} finally {
  socket?.close();
  chrome.kill();
  if (chrome.exitCode === null) await new Promise(resolve => chrome.once('exit', resolve));
  if (resolve(temporary).startsWith(join(root, '.local')))
    try { rmSync(temporary, { recursive: true, force: true, maxRetries: 10, retryDelay: 200 }); }
    catch (error) { if (error.code !== 'EPERM') throw error; }
}
