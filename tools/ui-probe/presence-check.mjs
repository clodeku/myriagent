// 在真的 app（WebView2）裡驗證「沒有人看得到畫面時不畫網頁」（ScreenPresence，2026-11-12）。
// 要在電腦鎖定或螢幕關閉時跑（代理跑測試時通常就是；看得到畫面時兩回合都照常畫，結果寫 seen、不算失敗）。
// 做法照 CLAUDE.md「要在真的 app 裡操作網頁驗證」：把建置輸出複製到暫存資料夾，以探針模式（不找 CLI、不送訊息）啟動兩次——
// 一次照探針的預設不接 ScreenPresence（對照組），一次以 MYRIAGENT_AGENT_PROBE_PRESENCE=1 接上；各自以 CDP 讀 document.hidden、
// 等網頁載入完成（#app 有內容），量 10 秒這個 app 的 WebView2 各程序的 CPU，再看記錄檔有沒有那一行。結束以 PID 收掉整棵樹。
// CDP 直接走 WebSocket、不用 Playwright：Playwright 會對它接上的頁面開焦點模擬（Emulation.setFocusEmulationEnabled），
// 頁面的 document.hidden 就一律是 false，看不出宿主有沒有把它藏起來。
// 用法：node presence-check.mjs [建置輸出的資料夾]（預設 src/Myriagent/bin/Release/net10.0-windows/win-x64）
import { spawn, execFileSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import net from 'node:net';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const bin = path.resolve(process.argv[2] || path.join(here, '..', '..', 'src', 'Myriagent', 'bin', 'Release', 'net10.0-windows', 'win-x64'));
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function freePort() {
  return new Promise((resolve, reject) => {
    const s = net.createServer();
    s.once('error', reject);
    s.listen(0, '127.0.0.1', () => { const p = s.address().port; s.close(() => resolve(p)); });
  });
}

/** 這個 PID 底下整棵樹各程序的 CPU 時間（毫秒）：PowerShell 讀一次（種類看命令列的 --type） */
function treeCpu(pid) {
  const ps = `$all = Get-CimInstance Win32_Process; function D($id) { foreach ($p in $all) { if ([int]$p.ParentProcessId -eq $id) { $p; D ([int]$p.ProcessId) } } };
    $list = @($all | Where-Object { [int]$_.ProcessId -eq ${pid} }) + @(D ${pid});
    $list | ForEach-Object { $k = if ($_.CommandLine -match '--type=renderer') { 'renderer' } elseif ($_.CommandLine -match '--type=gpu-process') { 'gpu' } elseif ($_.Name -eq 'Myriagent.exe') { 'app' } else { 'other' };
      $t = (Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue).TotalProcessorTime.TotalMilliseconds; "$($_.ProcessId) $k $t" }`;
  const out = execFileSync('powershell', ['-NoProfile', '-Command', ps], { encoding: 'utf8' });
  const map = new Map();
  for (const line of out.split(/\r?\n/)) {
    const [id, kind, ms] = line.trim().split(' ');
    if (id && kind && ms) map.set(id, { kind, ms: Number(ms) });
  }
  return map;
}

/** 接上 app 那一頁的 CDP（找不到回 null）：回 { evaluate(表示式), close() } */
async function connectPage(port) {
  let list;
  try { list = await (await fetch('http://127.0.0.1:' + port + '/json/list', { signal: AbortSignal.timeout(2000) })).json(); } catch { return null; }
  const target = list.find((t) => t.type === 'page' && String(t.url).startsWith('https://myriagent.local/'));
  if (!target) return null;
  const ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = () => reject(new Error('CDP 連不上')); });
  let seq = 0;
  const waiting = new Map();
  ws.onmessage = (e) => { const m = JSON.parse(e.data); const w = waiting.get(m.id); if (w) { waiting.delete(m.id); w(m); } };
  const evaluate = (expression) => new Promise((resolve, reject) => {
    const id = ++seq;
    waiting.set(id, (m) => (m.error || (m.result && m.result.exceptionDetails) ? reject(new Error(JSON.stringify(m.error || m.result.exceptionDetails).slice(0, 300))) : resolve(m.result.result.value)));
    ws.send(JSON.stringify({ id, method: 'Runtime.evaluate', params: { expression, awaitPromise: true, returnByValue: true } }));
  });
  return { evaluate, close: () => ws.close() };
}

async function run(presence) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'myriagent-presence-'));
  const app = path.join(root, 'app'), data = path.join(root, 'data'), out = path.join(root, 'probe'), empty = path.join(root, 'empty');
  fs.cpSync(bin, app, { recursive: true });
  for (const d of [data, out, empty]) fs.mkdirSync(d, { recursive: true });
  const port = await freePort();
  // 剝掉這個 session 的 CLAUDE*、CODEX*、GROK* 等變數；CLI 的設定資料夾指到空的暫存資料夾
  const env = Object.fromEntries(Object.entries(process.env).filter(([k]) => !/^(CLAUDE|CODEX|GROK|ANTHROPIC|OPENAI|XAI|ANTIGRAVITY|MYRIAGENT_)/i.test(k)));
  Object.assign(env, {
    WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: '--remote-debugging-port=' + port,
    MYRIAGENT_AGENT_PROBE: out, MYRIAGENT_AGENT_PROBE_NO_CLI: '1', MYRIAGENT_AGENT_PROBE_SEND: '0', MYRIAGENT_AGENT_PROBE_KEEP: '1',
    CLAUDE_CONFIG_DIR: empty, CODEX_HOME: empty, GROK_HOME: empty,
  });
  if (presence) env.MYRIAGENT_AGENT_PROBE_PRESENCE = '1';
  const child = spawn(path.join(app, 'Myriagent.exe'), ['--data', data], { env, stdio: 'ignore', windowsHide: false });
  const result = { presence, pid: child.pid };
  let page = null;
  try {
    // 等 CDP 起來、找到 app 的那一頁
    for (let i = 0; i < 120 && !page; i++) {
      await sleep(500);
      page = await connectPage(port);
    }
    if (!page) throw new Error('60 秒內找不到 app 的網頁（CDP 埠 ' + port + '）');
    // 網頁載入完成：收到 init 之後側欄有內容（看不到時照樣要載入，app 換版的健康閘門等的就是它）
    let loaded = false;
    for (let i = 0; i < 80 && !loaded; i++) {
      loaded = await page.evaluate("!!document.querySelector('.sidebar') && !!document.querySelector('.topbar')").catch(() => false);
      if (!loaded) await sleep(500);
    }
    result.loaded = loaded;
    await sleep(3000);
    result.hidden = await page.evaluate('document.hidden');
    result.visibility = await page.evaluate('document.visibilityState');
    result.runningAnimations = await page.evaluate("document.getAnimations().filter((a) => a.playState === 'running').length");
    // rAF 有沒有在跑（看不到時瀏覽器不叫 rAF）：以計時器等 1 秒、數這段時間的 rAF 次數
    result.rafPerSec = await page.evaluate(`new Promise((resolve) => {
      let n = 0, live = true;
      const tick = () => { if (!live) return; n++; requestAnimationFrame(tick); };
      requestAnimationFrame(tick);
      setTimeout(() => { live = false; resolve(n); }, 1000);
    })`);
    const c0 = treeCpu(child.pid);
    const t0 = Date.now();
    await sleep(10000);
    const c1 = treeCpu(child.pid);
    const secs = (Date.now() - t0) / 1000;
    const sum = { app: 0, renderer: 0, gpu: 0, other: 0 };
    for (const [id, after] of c1) if (c0.has(id)) sum[after.kind] += after.ms - c0.get(id).ms;
    result.cpuMsPerSec = Object.fromEntries(Object.entries(sum).map(([k, v]) => [k, Math.round(v / secs)]));
    const logDir = path.join(data, 'logs');
    const logs = fs.existsSync(logDir) ? fs.readdirSync(logDir).filter((f) => f.endsWith('.log')).map((f) => fs.readFileSync(path.join(logDir, f), 'utf8')).join('\n') : '';
    result.logLine = (logs.split(/\r?\n/).find((l) => l.includes('畫面沒有人看得到')) || '').slice(0, 160) || null;
  } finally {
    if (page) page.close();
    try { execFileSync('taskkill', ['/PID', String(child.pid), '/T', '/F'], { stdio: 'ignore' }); } catch { /* 已經結束 */ }
    await sleep(1500);
    try { fs.rmSync(root, { recursive: true, force: true }); } catch { /* WebView2 的檔還鎖著：留在暫存資料夾 */ }
  }
  return result;
}

const off = await run(false);
const on = await run(true);
// 看不到畫面（鎖定或螢幕關閉）時：接上的那一回合 document.hidden、沒有 rAF、有記錄；對照組照常畫。看得到畫面時兩回合一樣（不算失敗）
const unseen = on.hidden === true;
const ok = on.loaded && off.loaded && off.hidden === false && off.rafPerSec > 10
  && (unseen ? on.rafPerSec < 10 && !!on.logLine : on.rafPerSec > 10 && !on.logLine);
console.log(JSON.stringify({ screen: unseen ? 'unseen' : 'seen', ok, withoutPresence: off, withPresence: on }, null, 1));
process.exit(ok ? 0 : 1);
