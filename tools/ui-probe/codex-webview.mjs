// 真實 WebView2 的 Codex 診斷入口與宿主回覆；獨立資料根，不找 CLI、不送模型請求。
// 用法：先建置 Release，再於 tools/ui-probe 執行 node codex-webview.mjs。
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import net from 'node:net';
import { fileURLToPath } from 'node:url';
import { spawn, spawnSync } from 'node:child_process';
import { chromium } from 'playwright';
import { childEnv } from '../hub-e2e/lib/child-env.mjs';
import { appEnv } from '../hub-e2e/lib/real-exe-rules.mjs';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'myriagent-codex-webview-'));
const bin = path.join(work, 'bin');
const probeDir = path.join(work, 'probe');
const claudeConfigDir = path.join(work, 'claude');
const codexHome = path.join(work, 'codex');
for (const dir of [probeDir, claudeConfigDir, codexHome]) fs.mkdirSync(dir);
fs.cpSync(path.join(repo, 'src/Myriagent/bin/Release/net10.0-windows/win-x64'), bin, { recursive: true });
const server = net.createServer();
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const port = server.address().port;
await new Promise(resolve => server.close(resolve));
const env = appEnv(childEnv(), { probeDir, claudeConfigDir, codexHome });
env.WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = '--remote-debugging-port=' + port;
const app = spawn(path.join(bin, 'Myriagent.exe'), ['--data', path.join(work, 'data')], { env, windowsHide: true, stdio: 'ignore' });
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const result = { work, checks: {}, errors: [] };
let browser;
try {
  const deadline = Date.now() + 180000;
  while (!fs.existsSync(path.join(probeDir, 'report.json'))) {
    if (Date.now() > deadline || app.exitCode !== null) throw new Error('探針沒有完成啟動');
    await delay(500);
  }
  const startup = JSON.parse(fs.readFileSync(path.join(probeDir, 'report.json'), 'utf8'));
  result.checks.startup = !startup.error && !(startup.consoleErrors || []).length;
  browser = await chromium.connectOverCDP('http://127.0.0.1:' + port);
  const page = browser.contexts().flatMap(c => c.pages()).find(p => p.url().startsWith('https://myriagent.local/index.html'));
  if (!page) throw new Error('找不到測試 app 的網頁');
  page.on('pageerror', e => result.errors.push(String(e)));
  await page.locator('[data-view="dashboard"]').click();
  await page.locator('.codex-doctor:visible').first().click();
  await page.getByRole('button', { name: '只檢查連線', exact: true }).click();
  await page.locator('.codex-doctor-result .codex-error').waitFor();
  result.checks.nativeReply = (await page.locator('.codex-doctor-result').innerText()).includes('請先安裝 Codex CLI');
  result.checks.retry = await page.getByRole('button', { name: '完整診斷', exact: true }).isEnabled();
  result.checks.embeddedModules = await page.evaluate(async () => {
    const controls = await import('./codexcontrols.js');
    return typeof controls.openCodexPanel === 'function';
  });
  await page.screenshot({ path: path.join(work, 'doctor-webview.png') });
  result.checks.noPageErrors = result.errors.length === 0;
} catch (error) {
  result.errors.push(String(error.stack || error));
} finally {
  // 只結束本探針建立的程序樹，保留結果供檢查。
  if (app.pid && app.exitCode === null) spawnSync('taskkill.exe', ['/PID', String(app.pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore' });
  if (browser) await Promise.race([browser.close().catch(() => {}), delay(3000)]);
  result.passed = result.errors.length === 0 && Object.values(result.checks).every(Boolean);
  fs.writeFileSync(path.join(work, 'result.json'), JSON.stringify(result, null, 2));
  console.log(JSON.stringify(result, null, 2));
  process.exit(result.passed ? 0 : 1);
}
