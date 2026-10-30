// 全流程（full-flow.mjs）與真的 exe 的冒煙（real-exe.mjs，步 20）共用的環境（WP4.9 步 14，計畫 docs/plans/2026-10-20-wp4.9-e2e-perf-deploy.md）：
//   startEnv({ devices: ['OFFICE','HOME'], seeds, proxyDelayMs: 20, fakeCodex, report }) 依序：
//   1. 兩份建置（MyriagentHub、FakeDevice）不比原始碼舊；取得序列化鎖（lock.mjs）並開始續期（D10）
//   2. tripwire（D21）：墊片放在子程序 PATH 最前面，verifyShims 通過才啟動任何子程序；之後每 2 秒掃一次自己的程序樹（不擋事件迴圈）
//   3. 延遲代理（D9，每方向 proxyDelayMs）＋本機 MyriagentHub（lib/hub-process.mjs：暫存資料根、127.0.0.1、自己挑的高埠、Testing 環境、
//      Hub:PublicOrigin＝https://127.0.0.1:<代理埠>）；經代理讀 /readyz 過正式站守門（D18）
//   4. 每台假裝置：pair（經代理的網址＝PublicOrigin，aud 對得上；擁有者自動核准）→ run --control-stdin（種子對話、帳戶、政策、等 outbox 清空）
//   5. 瀏覽器（playwright-core＋本機 Chrome、無頭、ignoreHTTPSErrors）：ui-probe/hub-page.mjs 的 probeInit（CSP 違規、XSS 計數、傳輸層訊息記錄）
//      以 addInitScript 掛在每個頁面，擁有者以預設密碼在 https://127.0.0.1:<代理埠> 登入
//   env.close()：先寫結果檔，再有時限地關瀏覽器，依序收假裝置（quit、unpair；只剩 pending-key.json 的跑一次 status）、伺服器（admin stop --data）、
//   情境另外開的伺服器（startAltHub）、代理；檢查自己的金鑰檔（MyriagentHub 的 needle、假裝置的 keyName）、tripwire；最後放鎖。
//   Ctrl+C（SIGINT、SIGBREAK）時照 env.close() 收尾每個還開著的環境（每個最多 2 分鐘），寫出結果檔、以結束碼 130 結束；再按一次直接結束（WP4.9 合併前審查）。
// 所有子程序只以 PID 結束；所有網址都過 isAllowedServerUrl；密碼只在記憶體與給 pair 的環境變數裡。
// 延遲代理在這個 Node 程序裡：這裡不用任何 spawnSync（會讓代理停止轉送），子程序一律非同步。
import { chromium } from 'playwright-core';
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import readline from 'node:readline';
import { fileURLToPath } from 'node:url';
import { acquireLock, lockFile, LOCK_WAIT_MS, releaseLock } from '../lock.mjs';
import { newContext } from './quiet-browser.mjs';
import { childEnv } from './child-env.mjs';
import { startDelayProxy } from './delay-proxy.mjs';
import { isAllowedServerUrl, isProductionTarget, isUnderTemp } from './guard-rules.mjs';
import {
  cleanupAction, controlLine, deviceScript, normalizeDevices, OWNER_PASSWORD_ENV, OWNER_USER, pairArgs, parseOutputLine, restartScript, runArgs, staleOutputs, statusArgs, unpairArgs,
} from './harness-rules.mjs';
import { keysDir, localGet, startHub } from './hub-process.mjs';
import { startLockHeartbeat } from './lock-heartbeat.mjs';
import { createTripwire, scanOwnTreeAsync } from './tripwire.mjs';
import { probeInit, tstatus, waitLive } from '../../ui-probe/hub-page.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
export const REPO = path.resolve(here, '..', '..', '..');
export const DEFAULT_HUB_EXE = path.join(REPO, 'src', 'MyriagentHub', 'bin', 'Release', 'net10.0', 'win-x64', 'MyriagentHub.exe');
export const DEFAULT_FAKE_EXE = path.join(REPO, 'tools', 'fake-device', 'bin', 'Release', 'net10.0-windows', 'win-x64', 'FakeDevice.exe');
/** 伺服器的 OwnerAccount.DefaultPassword（HubAuthNames）：只在記憶體，給假裝置時經環境變數 */
export const DEFAULT_PASSWORD = 'changeme2026';
export const SESSION_COOKIE = '__Host-myriagenthub';
export const DEVICE_KEY_PREFIX = 'Myriagent.Device.';

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/** 每 stepMs 問一次 read，ms 毫秒內回真值就回它，否則回 null */
export async function waitFor(read, ms, stepMs = 200) {
  const started = Date.now();
  while (Date.now() - started < ms) {
    const v = await read();
    if (v) return v;
    await sleep(stepMs);
  }
  return null;
}

// ───── Ctrl+C（WP4.9 合併前審查）─────
// 假裝置以全管線、windowsHide 啟動（CREATE_NO_WINDOW）收不到主控台的 Ctrl+C，真的 exe（real-exe）也是：Node 預設直接結束的話 finally 不跑，
// 已配對裝置的 CNG 金鑰、還在跑的程序都留在這台電腦上。所以在這個模組註冊一次處理器，照 env.close() 收尾每個還開著的環境

/** 中斷時每個環境的收尾最多等多久（quit 90 秒＋unpair＋admin stop） */
export const INTERRUPT_CLOSE_MS = 120000;

const activeEnvs = new Set();
let interruptedBy = null;

/** 收到 Ctrl+C 了沒（之後 startEnv 一律丟例外，不再啟動新的環境） */
export function interrupted() {
  return interruptedBy !== null;
}

/**
 * 登記一個要在 Ctrl+C 時收尾的環境（startEnv 自己登記；測試以假的 { close(), beforeInterruptClose?() } 登記）。
 * env.beforeInterruptClose（可選；例如 real-exe 以 PID 結束真的 exe）在 env.close() 之前呼叫。回傳取消登記的函式
 */
export function trackEnv(env) {
  activeEnvs.add(env);
  return () => activeEnvs.delete(env);
}

/** 處理器：每個還登記著的環境先 beforeInterruptClose、再 env.close()（各有時限），之後以 130 結束（呼叫端在 close 裡已寫出結果檔） */
async function onInterrupt(signal) {
  if (interruptedBy !== null) return;
  interruptedBy = signal;
  console.log(`[中斷] 收到 ${signal}：照 env.close() 收尾 ${activeEnvs.size} 個環境（quit、unpair、admin stop --data、金鑰檢查、放鎖；每個最多 ${INTERRUPT_CLOSE_MS / 1000} 秒）；再按一次直接結束`);
  for (const env of [...activeEnvs]) {
    try {
      env.step?.('中斷（Ctrl+C）', false, `收到 ${signal}：中途收尾，這一輪不算通過`);
    } catch { /* 報告寫不了照樣收 */ }
    if (typeof env.beforeInterruptClose === 'function') await Promise.race([Promise.resolve().then(() => env.beforeInterruptClose()).catch(() => null), sleep(30000)]);
    await Promise.race([Promise.resolve().then(() => env.close()).catch(() => null), sleep(INTERRUPT_CLOSE_MS)]);
    activeEnvs.delete(env);
  }
  process.exit(130);
}

for (const sig of ['SIGINT', 'SIGBREAK']) process.once(sig, (s) => { void onInterrupt(s); });

// ───── 建置的新鮮度 ─────

function walk(dir, accept, out = []) {
  let entries = [];
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return out;
  }
  for (const e of entries) {
    const full = path.join(dir, e.name);
    if (e.isDirectory()) {
      if (e.name === 'bin' || e.name === 'obj' || e.name === 'node_modules' || e.name.startsWith('.')) continue;
      walk(full, accept, out);
    } else if (accept(e.name)) {
      out.push(full);
    }
  }
  return out;
}

const mtimeOf = (file) => {
  try {
    return fs.statSync(file).mtimeMs;
  } catch {
    return null;
  }
};

const isCs = (n) => n.endsWith('.cs');
const any = () => true;

/**
 * MyriagentHub 與 FakeDevice 的建置是否比原始碼舊（P46:1079；忘了重建會測到舊的程式）。比的是編譯出來的組件，不是 apphost：
 * 參考的組件只改了內部時 FakeDevice.dll 不重新編譯，exe 的時間不代表新舊。桌面的 Web 嵌在 MyriagentHub.dll 裡，wwwroot 是逐檔複製
 */
export function buildFreshness({ hubExe = DEFAULT_HUB_EXE, fakeExe = DEFAULT_FAKE_EXE } = {}) {
  const hubBin = path.dirname(hubExe);
  const fakeBin = path.dirname(fakeExe);
  const src = (...p) => path.join(REPO, ...p);
  const sources = (files) => files.map((f) => ({ path: path.relative(REPO, f), mtime: mtimeOf(f) }));
  const outputs = [
    { label: 'MyriagentHub.dll', mtime: mtimeOf(path.join(hubBin, 'MyriagentHub.dll')), sources: sources([...walk(src('src', 'MyriagentHub'), isCs), ...walk(src('src', 'Myriagent', 'Web'), any)]) },
    { label: 'MyriagentHub 的 Myriagent.Protocol.dll', mtime: mtimeOf(path.join(hubBin, 'Myriagent.Protocol.dll')), sources: sources(walk(src('src', 'Myriagent.Protocol'), isCs)) },
    { label: 'FakeDevice.dll', mtime: mtimeOf(path.join(fakeBin, 'FakeDevice.dll')), sources: sources(walk(src('tools', 'fake-device'), isCs)) },
    { label: 'FakeDevice 的 Myriagent.dll', mtime: mtimeOf(path.join(fakeBin, 'Myriagent.dll')), sources: sources(walk(src('src', 'Myriagent'), isCs)) },
    { label: 'FakeDevice 的 Myriagent.Protocol.dll', mtime: mtimeOf(path.join(fakeBin, 'Myriagent.Protocol.dll')), sources: sources(walk(src('src', 'Myriagent.Protocol'), isCs)) },
  ];
  // wwwroot：每個檔各自比（PreserveNewest 複製時保留原始檔的修改時間）
  for (const f of walk(src('src', 'MyriagentHub', 'wwwroot'), any)) {
    const rel = path.relative(src('src', 'MyriagentHub', 'wwwroot'), f);
    outputs.push({ label: `wwwroot\\${rel}`, mtime: mtimeOf(path.join(hubBin, 'wwwroot', rel)), sources: [{ path: path.relative(REPO, f), mtime: mtimeOf(f) }] });
  }
  if (!fs.existsSync(hubExe)) outputs.push({ label: 'MyriagentHub.exe', mtime: null, sources: [] });
  if (!fs.existsSync(fakeExe)) outputs.push({ label: 'FakeDevice.exe', mtime: null, sources: [] });
  const r = staleOutputs(outputs);
  return { ...r, checked: outputs.length, sources: outputs.reduce((n, o) => n + o.sources.length, 0) };
}

// ───── 金鑰存放區 ─────

export function listKeys() {
  try {
    return fs.readdirSync(keysDir);
  } catch {
    return [];
  }
}

/** 金鑰存放區裡內容含這些金鑰名稱（UTF-16LE）的檔：{名稱: [檔名…]} */
export function keyFilesContaining(names) {
  const needles = names.map((n) => [n, Buffer.from(n, 'utf16le')]);
  const hits = Object.fromEntries(names.map((n) => [n, []]));
  for (const k of listKeys()) {
    let buf;
    try { buf = fs.readFileSync(path.join(keysDir, k)); } catch { continue; }
    for (const [n, b] of needles) if (buf.includes(b)) hits[n].push(k);
  }
  return hits;
}

function readJson(file) {
  try {
    return JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch {
    return null;
  }
}

function readKeyName(data) {
  for (const file of ['device.json', 'pending-key.json']) {
    const j = readJson(path.join(data, 'sync', file));
    if (j && typeof j.keyName === 'string') return j.keyName;
  }
  return null;
}

function freePort() {
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.on('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const { port } = server.address();
      server.close(() => resolve(port));
    });
  });
}

// ───── 子程序（一律非同步；只以 PID 結束）─────

/** 以 PID 結束整個程序樹（taskkill /PID <pid> /T /F；不以映像名稱），不擋事件迴圈 */
function killTree(pid) {
  return new Promise((resolve) => {
    if (!Number.isInteger(pid) || pid <= 0) { resolve(null); return; }
    const p = spawn('taskkill', ['/PID', String(pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore' });
    p.on('error', () => resolve(null));
    p.on('close', (code) => resolve(code));
  });
}

/** 跑一次 FakeDevice 指令並等它結束（逾時以 PID 結束整棵樹）：{ code, json（最後一行 JSON）, stdout, stderr } */
function runOnce(exe, argv, { env, timeoutMs, logBase }) {
  return new Promise((resolve) => {
    const proc = spawn(exe, argv, { cwd: path.dirname(exe), env, stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true });
    let stdout = '';
    let stderr = '';
    proc.stdout.setEncoding('utf8');
    proc.stderr.setEncoding('utf8');
    proc.stdout.on('data', (d) => { stdout += d; });
    proc.stderr.on('data', (d) => { stderr += d; });
    let timedOut = false;
    const timer = setTimeout(() => { timedOut = true; killTree(proc.pid); }, timeoutMs);
    const done = (code) => {
      clearTimeout(timer);
      if (logBase) {
        fs.writeFileSync(`${logBase}.stdout.log`, stdout);
        fs.writeFileSync(`${logBase}.stderr.log`, stderr);
      }
      const lines = stdout.split(/\r?\n/u).map(parseOutputLine).filter(Boolean);
      resolve({ code, timedOut, pid: proc.pid, json: lines.at(-1) ?? null, stdout, stderr });
    };
    proc.on('error', (e) => { stderr += `\n啟動失敗：${e?.message ?? e}`; done(null); });
    proc.on('close', (code) => done(code));
  });
}

/** 結束碼 3（這個資料根的實例鎖還沒放開：剛結束的程序）時等 1 秒重試，最多 15 秒 */
async function runOnceRetry(exe, argv, opts) {
  const started = Date.now();
  let attempts = 0;
  let r;
  while (true) {
    attempts++;
    r = await runOnce(exe, argv, { ...opts, logBase: opts.logBase ? `${opts.logBase}-${attempts}` : null });
    if (r.code !== 3 || Date.now() - started >= 15000) break;
    await sleep(1000);
  }
  return { ...r, attempts };
}

/** 劇本每一步的結果（假裝置印在標準錯誤：「第 N 步 op：{…}」） */
export function scriptSteps(stderr) {
  return String(stderr ?? '').split(/\r?\n/u).map((l) => /^第 (\d+) 步 (\w+)：(.*)$/u.exec(l.trim())).filter(Boolean)
    .map((m) => ({ step: Number(m[1]), op: m[2], result: parseOutputLine(m[3]) }));
}

/** 背景跑 run --control-stdin：{ proc, pid, ready（Promise）, control(op, params, ms), quit(ms), … } */
function startControlled(exe, argv, { env, logBase }) {
  const proc = spawn(exe, argv, { cwd: path.dirname(exe), env, stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
  const dev = { proc, pid: proc.pid, exit: undefined, readyLine: null, final: null, stderr: '', seq: 0, pending: new Map(), replies: 0 };
  const outLog = fs.openSync(`${logBase}.stdout.log`, 'a');
  const errLog = fs.openSync(`${logBase}.stderr.log`, 'a');
  let readyResolve;
  dev.ready = new Promise((r) => { readyResolve = r; });
  dev.exited = new Promise((resolve) => {
    proc.on('close', (code) => {
      dev.exit = code;
      for (const [, p] of dev.pending) p.resolve({ ok: false, error: `假裝置已結束（結束碼 ${code}）` });
      dev.pending.clear();
      readyResolve(null);
      try { fs.closeSync(outLog); } catch { /* 已關 */ }
      try { fs.closeSync(errLog); } catch { /* 已關 */ }
      resolve(code);
    });
  });
  proc.on('error', (e) => { dev.stderr += `\n啟動失敗：${e?.message ?? e}`; });
  proc.stdin.on('error', () => { /* 程序已結束：寫不進去由 control 的逾時回報 */ });
  readline.createInterface({ input: proc.stdout, crlfDelay: Infinity }).on('line', (line) => {
    try { fs.writeSync(outLog, line + '\n'); } catch { /* 已關 */ }
    const j = parseOutputLine(line);
    if (!j) return;
    if (j.event === 'ready') {
      dev.readyLine = j;
      readyResolve(j);
    } else if (j.command === 'run') {
      dev.final = j;
    } else if (j.id !== undefined && dev.pending.has(j.id)) {
      const p = dev.pending.get(j.id);
      dev.pending.delete(j.id);
      dev.replies++;
      p.resolve(j);
    }
  });
  proc.stderr.setEncoding('utf8');
  proc.stderr.on('data', (d) => {
    dev.stderr += d;
    try { fs.writeSync(errLog, d); } catch { /* 已關 */ }
  });
  /** 送一個控制指令、等它的回覆：{ ok, result | error, ms }（逾時回 ok:false） */
  dev.control = (op, params = {}, timeoutMs = 60000) => {
    if (dev.exit !== undefined) return Promise.resolve({ ok: false, error: `假裝置已結束（結束碼 ${dev.exit}）` });
    const id = ++dev.seq;
    return new Promise((resolve) => {
      const timer = setTimeout(() => {
        dev.pending.delete(id);
        resolve({ ok: false, error: `${timeoutMs} ms 內沒有回覆（${op}）` });
      }, timeoutMs);
      dev.pending.set(id, { resolve: (v) => { clearTimeout(timer); resolve(v); } });
      proc.stdin.write(controlLine(id, op, params), 'utf8');
    });
  };
  /** quit：送 quit、關掉標準輸入，等它照 run 的流程收尾（等 outbox 清空）；逾時以 PID 結束整棵樹 */
  dev.quit = async (timeoutMs = 90000) => {
    if (dev.exit === undefined) {
      const reply = await dev.control('quit', {}, 15000);
      try { proc.stdin.end(); } catch { /* 已關 */ }
      const done = await Promise.race([dev.exited.then(() => true), sleep(timeoutMs).then(() => false)]);
      if (!done) {
        await killTree(dev.pid);
        await Promise.race([dev.exited, sleep(10000)]);
        return { reply, exit: dev.exit ?? null, forced: true, final: dev.final };
      }
      return { reply, exit: dev.exit, forced: false, final: dev.final };
    }
    return { reply: null, exit: dev.exit, forced: false, final: dev.final, already: true };
  };
  return dev;
}

// ───── 瀏覽器 ─────

/**
 * 預期的主控台錯誤與 ≥400 的回應（回原因；不是預期的回 null）：登入頁 auth.js 以匿名 401 判斷要顯示登入畫面。
 * 登入頁的標籤是 L，另一個瀏覽器內容的登入頁是 L-<名稱>（addBrowserContext）
 */
function baseExpected(m) {
  if ((m.page === 'L' || String(m.page ?? '').startsWith('L-')) && (m.status === 401 || /status of 401/u.test(m.text ?? '')) && /\/api\/v1\/(auth\/me|sessions)$/u.test(m.path ?? m.url ?? '')) return '登入前開登入頁：auth.js 查目前的登入（匿名 401）';
  return null;
}

/**
 * 啟動整個環境（見檔頭）。opts：devices、seeds、fakeCodex（{名稱: 情境}）、proxyDelayMs（預設 20）、work（要在 TEMP 底下）、
 * hubExe、fakeExe、headed、hubArgs（多給伺服器的參數）、initOpts（probeInit 的選項）、initScripts（情境自己的頁面腳本 [{ fn, arg }]，
 * 接在 probeInit 後面掛在每個瀏覽器內容；步 15 的 flowRecorder）、tool（鎖的持有者名稱）、
 * report（{ step(name, ok, summary, data), save() }）。失敗時丟例外（呼叫端照樣呼叫 env.close() 收尾：startEnv 失敗時回傳的 env 掛在 error.env）
 */
export async function startEnv(opts = {}) {
  const report = opts.report ?? { step: (n, ok, s) => console.log(`[${ok === null ? '觀察' : ok ? '通過' : '失敗'}] ${n}：${s}`), save() {} };
  const step = (name, ok, summary, data) => report.step(name, ok, summary, data);
  if (interrupted()) throw new Error(`已中斷（${interruptedBy}）：不再啟動新的環境`);
  const work = path.resolve(opts.work ?? '');
  if (!opts.work || !isUnderTemp(work)) throw new Error(`工作資料夾要在暫存資料夾底下：${opts.work}`);
  const hubExe = path.resolve(opts.hubExe ?? DEFAULT_HUB_EXE);
  const fakeExe = path.resolve(opts.fakeExe ?? DEFAULT_FAKE_EXE);
  const devices = normalizeDevices(opts.devices, { seeds: opts.seeds ?? {}, fakeCodex: opts.fakeCodex ?? {} });
  const password = DEFAULT_PASSWORD;
  fs.mkdirSync(work, { recursive: true });

  const env = {
    work, hubExe, fakeExe, password, base: null, hub: null, proxy: null, devices: new Map(), browser: null, context: null, tripwire: null,
    consoleLog: [], httpErrors: [], cspLog: [], dialogs: [], pages: [], expected: [], step, report,
    keysBefore: new Set(), scanner: { scans: 0, failures: 0, hits: [], maxTree: 0, lastError: null, running: false, loop: null },
    closed: false, altHubs: [],
  };
  const internal = { lockHeld: false, heartbeat: null, childEnv: null, restarts: 0 };
  // 同時呼叫（Ctrl+C 的處理器與主流程的 finally）拿到同一個收尾
  env.close = () => {
    env.closing ??= closeEnv(env, internal).finally(() => activeEnvs.delete(env));
    return env.closing;
  };
  trackEnv(env);
  // 情境中途加裝置、重啟裝置、開另一個組建的伺服器（WP4.9 步 18 的 FF9）要用同一份子程序環境（tripwire 的 PATH）
  env.internal = internal;
  env.startOpts = opts;

  try {
    // 1. 建置的新鮮度、序列化鎖
    const fresh = buildFreshness({ hubExe, fakeExe });
    step('MyriagentHub 與 FakeDevice 的建置不比原始碼舊', fresh.ok, fresh.ok
      ? `${fresh.checked} 個輸出、${fresh.sources} 個原始檔：都不比輸出新`
      : `先重建（dotnet build Myriagent.slnx 與 tools/fake-device，-c Release -nodeReuse:false -p:UseSharedCompilation=false）：`
        + fresh.stale.map((s) => `${s.label}${s.missing ? ' 不存在' : ` 比 ${s.newerCount} 個原始檔舊（${s.newer.slice(0, 3).join('、')}${s.newerCount > 3 ? '…' : ''}）`}`).join('；'),
    { stale: fresh.stale, checked: fresh.checked, sources: fresh.sources });
    if (!fresh.ok) throw new Error('建置比原始碼舊');

    const lock = await acquireLock(opts.tool ?? 'full-flow', work);
    if (!lock.ok) throw new Error(`等不到序列化鎖（${lockFile}，${(LOCK_WAIT_MS / 1000).toFixed(0)} 秒）：持有者 ${lock.holder ?? '不明'}`);
    internal.lockHeld = true;
    internal.heartbeat = startLockHeartbeat();
    step('取得序列化鎖（每 60 秒續期）', true, `${lockFile}（等了 ${lock.waitedMs} ms${lock.staleRemoved ? '，清掉了超過 10 分鐘的舊鎖' : ''}）`, { waitedMs: lock.waitedMs, staleRemoved: lock.staleRemoved });

    // 2. tripwire：墊片在子程序 PATH 最前面；verifyShims 通過才啟動任何子程序（verifyShims 用 spawnSync，這時代理還沒起來）
    const tw = createTripwire(path.join(work, 'tripwire'));
    env.tripwire = tw;
    internal.childEnv = tw.childEnv(childEnv());
    tw.verifyShims(internal.childEnv);
    step('tripwire：墊片在子程序 PATH 最前面', true, `claude、codex、agy、grok 第一個找到的都是 ${tw.dir} 的墊片（where $PATH:）`, { dir: tw.dir });
    env.keysBefore = new Set(listKeys());

    // 3. 延遲代理＋伺服器（埠被搶走時換埠重試，最多 3 次；代理的網址不變）
    let hubPort = await freePort();
    const proxy = await startDelayProxy({ upstreamPort: hubPort, delayMs: opts.proxyDelayMs ?? 20 });
    env.proxy = proxy;
    env.base = `https://127.0.0.1:${proxy.port}`;
    if (!isAllowedServerUrl(env.base)) throw new Error(`代理的網址沒過守門：${env.base}`);
    const dataDir = path.join(work, 'hub-data');
    let lastError = null;
    for (let attempt = 1; attempt <= 3 && !env.hub; attempt++) {
      try {
        if (attempt > 1) {
          hubPort = await freePort();
          proxy.setUpstream(hubPort);
        }
        env.hub = await startHub({ exe: hubExe, dataDir, port: hubPort, publicOrigin: env.base, extraArgs: opts.hubArgs ?? [], env: internal.childEnv });
      } catch (e) {
        lastError = e;
      }
    }
    // 啟動期間按了 Ctrl+C：收尾可能已經走過伺服器那一步，這裡自己停掉（stop 對已經停的只刪金鑰檔）
    if (env.closing) {
      if (env.hub) await env.hub.close().catch(() => null);
      throw new Error('已中斷：不再往下啟動');
    }
    if (!env.hub) throw new Error(`MyriagentHub 起不來（試了 3 次）：${lastError?.message ?? lastError}`);
    const health = await localGet(hubPort, '/healthz').then((r) => readJsonText(r.body)).catch(() => null);
    step('啟動 MyriagentHub（Testing、暫存資料根、127.0.0.1）', true,
      `直連 https://127.0.0.1:${hubPort}、build ${health?.build ?? '—'}；PublicOrigin＝${env.base}（延遲代理，每方向 ${opts.proxyDelayMs ?? 20} ms）；這個實例匯入的金鑰檔 ${env.hub.instanceKeys().length} 個`,
      { pid: env.hub.pid, hubPort, proxyPort: proxy.port, dataDir, build: health?.build ?? null, needle: env.hub.needle, instanceKeys: env.hub.instanceKeys() });

    const viaProxy = await localGet(proxy.port, '/readyz', 5000);
    const ready = readJsonText(viaProxy.body);
    const production = isProductionTarget(ready, viaProxy.cert);
    const guardOk = viaProxy.status === 200 && ready?.environment === 'console' && !production;
    step('正式站守門（經代理讀 /readyz 與憑證）', guardOk, `/readyz ${viaProxy.status}、environment ${ready?.environment ?? '—'}、憑證 ${viaProxy.cert?.subject?.CN ?? '—'}（簽發者 ${viaProxy.cert?.issuer?.CN ?? '—'}）；${production ? '看起來是正式站' : '不是正式站'}`,
      { status: viaProxy.status, environment: ready?.environment ?? null, tls: ready?.tls ?? null, subject: viaProxy.cert?.subject ?? null, issuer: viaProxy.cert?.issuer ?? null });
    if (!guardOk) throw new Error('正式站守門沒過');
    startScanner(env);

    // 4. 假裝置：依序 pair（經代理、自動核准）→ run --control-stdin
    for (const cfg of devices) {
      if (env.closing) throw new Error('已中斷：不再往下啟動');
      const dev = await startDevice(env, internal, cfg, opts);
      env.devices.set(cfg.name, dev);
      if (!dev.ok) throw new Error(`假裝置 ${cfg.name} 沒有準備好`);
    }

    // 5. 瀏覽器：本機 Chrome、無頭；probeInit（與情境自己的 initScripts）掛在每個頁面；擁有者登入
    if (env.closing) throw new Error('已中斷：不再往下啟動');
    env.browser = await chromium.launch({ channel: 'chrome', headless: !opts.headed, args: ['--ignore-certificate-errors'] });
    env.pageInit = { initOpts: opts.initOpts ?? {}, initScripts: opts.initScripts ?? [] };
    env.context = await newBrowserContext(env);
    env.login = await login(env, env.context, 'L');
    if (!env.login.ok) throw new Error('擁有者登入失敗');
    return env;
  } catch (e) {
    step('準備環境', false, String(e?.message ?? e), { stack: String(e?.stack ?? e) });
    e.env = env;
    throw e;
  }
}

function readJsonText(text) {
  try {
    return text ? JSON.parse(text) : null;
  } catch {
    return null;
  }
}

/** 一台假裝置：pair → run --control-stdin，等控制通道 ready、讀一次 status */
async function startDevice(env, internal, cfg, opts) {
  const data = path.join(env.work, cfg.name);
  const dev = { name: cfg.name, cfg, data, deviceId: null, keyName: null, run: null, ok: false, pair: null, status: null, steps: [] };
  const pairEnv = { ...internal.childEnv, [OWNER_PASSWORD_ENV]: env.password };
  const pair = await runOnceRetry(env.fakeExe, pairArgs({ dataDir: data, server: env.base, device: cfg }), { env: pairEnv, timeoutMs: 180000, logBase: path.join(env.work, `${cfg.name}-pair`) });
  const paired = pair.json?.command === 'pair' ? pair.json : null;
  dev.deviceId = paired?.deviceId ?? null;
  dev.keyName = readKeyName(data);
  const hits = dev.keyName ? keyFilesContaining([dev.keyName])[dev.keyName] : [];
  dev.pair = { code: pair.code, attempts: pair.attempts, state: paired?.state ?? null, drained: paired?.drained ?? null, server: paired?.server ?? null };
  const pairOk = pair.code === 0 && paired?.state === 'Live' && paired?.drained === true && !!dev.deviceId && !!dev.keyName && dev.keyName.startsWith(DEVICE_KEY_PREFIX) && hits.length === 1;
  env.step(`假裝置 ${cfg.name}：配對（經代理、擁有者自動核准）`, pairOk,
    `結束碼 ${pair.code}${pair.attempts > 1 ? `（試了 ${pair.attempts} 次）` : ''}、state ${paired?.state ?? '—'}、outbox ${paired?.drained === true ? '清空' : '沒清空'}、伺服器 ${paired?.server ?? '—'}；`
      + `裝置金鑰 ${dev.keyName ?? '—'} 在金鑰存放區 ${hits.length} 個檔（正向對照，收尾時同樣方法確認不在）`,
    { deviceId: dev.deviceId, keyName: dev.keyName, pair: dev.pair, stderr: pairOk ? undefined : pair.stderr.slice(-1500) });
  if (!pairOk) return dev;

  const scriptFile = path.join(env.work, `script-${cfg.name}.json`);
  fs.writeFileSync(scriptFile, JSON.stringify(deviceScript(cfg), null, 2));
  const argv = runArgs({ dataDir: data, scriptFile, device: cfg, timeoutSeconds: 120 });
  const started = Date.now();
  let run = null;
  let attempts = 0;
  while (true) {
    attempts++;
    run = startControlled(env.fakeExe, argv, { env: internal.childEnv, logBase: path.join(env.work, `${cfg.name}-run-${attempts}`) });
    const readyLine = await Promise.race([run.ready, sleep(opts.readyTimeoutMs ?? 300000).then(() => null)]);
    if (readyLine || run.exit !== 3 || Date.now() - started > 15000) break;
    await sleep(1000);
  }
  dev.run = run;
  dev.steps = scriptSteps(run.stderr);
  const policy = dev.steps.find((s) => s.op === 'policy')?.result ?? null;
  // 種子可以是好幾段（步 15 的 FF1）：每段一個 seedConversations，合計對得上才算
  const seedSteps = dev.steps.filter((s) => s.op === 'seedConversations').map((s) => s.result);
  const seed = seedSteps.length === 0 ? null : {
    conversations: seedSteps.reduce((n, r) => n + (r?.conversations ?? 0), 0),
    items: seedSteps.reduce((n, r) => n + (r?.items ?? 0), 0),
    sha256: seedSteps.map((r) => String(r?.sha256 ?? '').slice(0, 12)).join('+'),
    parts: seedSteps.length,
  };
  const status = run.readyLine ? await run.control('status', {}, 30000) : null;
  dev.status = status?.ok ? status.result : null;
  const policyOk = policy?.level === cfg.policy.level && policy?.fullAccess === cfg.policy.fullAccess;
  const seedOk = cfg.seed.count === 0 ? seed === null : seed?.conversations === cfg.seed.count && seed.parts === cfg.seedParts.length;
  dev.seed = seed;
  dev.ok = !!run.readyLine && run.readyLine.paired === true && policyOk && seedOk && !!dev.status;
  env.step(`假裝置 ${cfg.name}：劇本與控制通道`, dev.ok,
    `${run.readyLine ? `控制通道 ready（PID ${run.pid}、state ${run.readyLine.state}、${attempts > 1 ? `試了 ${attempts} 次、` : ''}${Date.now() - started} ms）` : `沒有 ready（結束碼 ${run.exit ?? '還在跑'}）`}；`
      + `種子 ${seed ? `${seed.conversations} 個對話${seed.parts > 1 ? `（${seed.parts} 段）` : ''}（${seed.items} 則、指紋 ${seed.sha256}…）` : cfg.seed.count === 0 ? '不種' : '—'}；`
      + `政策 ${policy ? `${policy.level}、fullAccess ${policy.fullAccess}` : '—'}${cfg.fakeCodex ? `；假 Codex ${cfg.fakeCodex}` : ''}；`
      + `${cfg.drain ? '' : '不等 outbox 清空（回補留給情境）；'}協商功能 ${(dev.status?.features ?? []).join('、') || '—'}`,
    { pid: run.pid, ready: run.readyLine, steps: dev.steps, features: dev.status?.features ?? null, backfill: dev.status?.backfill ?? null, stderr: dev.ok ? undefined : run.stderr.slice(-2000) });
  return dev;
}

/**
 * 情境中途加一台假裝置（WP4.9 步 18 的 PRIV-R3-3）：設定同 startEnv 的 devices 一項（可以 sameSeedAs 一台已經在跑的），種子不能撞到別台
 * （sameSeedAs 的那台除外）；照 startEnv 的第 4 點 pair → run --control-stdin。收尾照樣由 env.close() 收（quit、unpair、金鑰）。回那台（dev.ok 是準備好了沒）
 */
export async function addDevice(env, raw) {
  const existing = [...env.devices.values()].map((d) => d.cfg);
  const [cfg] = normalizeDevices([raw], { existing });
  const dev = await startDevice(env, env.internal, cfg, env.startOpts ?? {});
  env.devices.set(cfg.name, dev);
  return dev;
}

/**
 * 同一個資料根重新啟動一台假裝置（WP4.9 步 18 的 FF9）：hard 時以 PID 結束整棵樹（模擬程式被結束，待送列留著），否則照 quit（等 outbox 清空，最多 90 秒）；
 * beforeStart() 在舊程序結束之後、新程序啟動之前呼叫（例如獨占某個 meta.json）；appVersion 有給就換 --app-version（之後的重啟與 unpair 沿用）；
 * 劇本只設政策（restartScript，drain 時等清空）。timeoutSeconds 是 run 的 --timeout：劇本開始前等 Live、quit 之後等清空都用它（暫停同步中的那台永遠不會 Live，
 * 給短一點才不會白等）。回 { stopped, ready, status, attempts, ms }
 */
export async function restartDevice(env, name, { hard = false, drain = true, appVersion = undefined, beforeStart = null, timeoutSeconds = 120 } = {}) {
  const dev = env.devices.get(name);
  if (!dev?.run) throw new Error(`沒有在跑的假裝置 ${name}`);
  let stopped;
  if (hard || dev.run.exit !== undefined) {
    if (dev.run.exit === undefined) await killTree(dev.run.pid);
    await Promise.race([dev.run.exited, sleep(15000)]);
    stopped = { exit: dev.run.exit ?? null, forced: hard, final: dev.run.final };
  } else {
    stopped = await dev.run.quit(90000);
  }
  if (beforeStart) await beforeStart();
  if (appVersion !== undefined) dev.cfg = { ...dev.cfg, appVersion };
  const n = ++env.internal.restarts;
  const scriptFile = path.join(env.work, `script-${name}-restart-${n}.json`);
  fs.writeFileSync(scriptFile, JSON.stringify(restartScript(dev.cfg, { drain }), null, 2));
  const argv = runArgs({ dataDir: dev.data, scriptFile, device: dev.cfg, timeoutSeconds });
  const started = Date.now();
  let run = null;
  let attempts = 0;
  while (true) {
    attempts++;
    run = startControlled(env.fakeExe, argv, { env: env.internal.childEnv, logBase: path.join(env.work, `${name}-restart-${n}-${attempts}`) });
    const readyLine = await Promise.race([run.ready, sleep(300000).then(() => null)]);
    if (readyLine || run.exit !== 3 || Date.now() - started > 15000) break;
    await sleep(1000);
  }
  dev.run = run;
  const status = run.readyLine ? await run.control('status', {}, 30000) : null;
  dev.status = status?.ok ? status.result : dev.status;
  return { stopped, ready: !!run.readyLine, status: status?.ok ? status.result : null, attempts, ms: Date.now() - started };
}

/**
 * 以另一個組建的 MyriagentHub 開同一個資料根（WP4.9 步 18 的回滾鏈，計畫 D12）：新的高埠、同一個 PublicOrigin（代理的網址）、同一份子程序環境；
 * 前一個伺服器要已經停了。呼叫端自己 proxy.setUpstream(hub.port) 與收（hub.close()，再以 keyFilesNaming(hub.needle) 確認沒有孤兒金鑰）；
 * 情境中途丟例外沒收到的，env.close() 在停 env.hub 之後照樣收（記在 env.altHubs，WP4.9 合併前審查）
 */
export async function startAltHub(env, exe, { extraArgs = [] } = {}) {
  if (env.closing) throw new Error('環境已在收尾：不再啟動另一個伺服器');
  const port = await freePort();
  const hub = await startHub({ exe, dataDir: env.hub.dataDir, port, publicOrigin: env.base, extraArgs, env: env.internal.childEnv });
  (env.altHubs ??= []).push(hub);
  return hub;
}

export { killTree, login };

/** tripwire 的程序掃描：每次掃完等 2 秒再掃（不重疊、不擋事件迴圈）；根是這個 Node 程序（自己啟動的子程序都在它底下） */
function startScanner(env) {
  const s = env.scanner;
  s.running = true;
  s.loop = (async () => {
    while (s.running) {
      try {
        const r = await scanOwnTreeAsync([process.pid]);
        s.scans++;
        if (r.scanned > s.maxTree) s.maxTree = r.scanned;
        for (const h of r.hits) {
          if (!s.hits.some((x) => x.pid === h.pid)) s.hits.push({ pid: h.pid, name: h.name, commandLine: String(h.commandLine ?? '').slice(0, 300), at: new Date().toISOString() });
        }
      } catch (e) {
        s.failures++;
        s.lastError = String(e?.message ?? e);
      }
      const until = Date.now() + 2000;
      while (s.running && Date.now() < until) await sleep(200);
    }
  })();
}

function labelOf(env, page) {
  return env.pages.find((p) => p.page === page)?.label ?? '?';
}

/** 監看一個頁面：主控台錯誤、pageerror、≥400 的回應、對話框（任何對話框都是 XSS 或非預期 confirm 的證據：記下並關掉） */
function watch(env, page, label) {
  const app = { label, page, navigations: 0 };
  env.pages.push(app);
  page.on('framenavigated', (f) => { if (f === page.mainFrame()) app.navigations++; });
  page.on('console', (m) => {
    if (m.type() === 'error' || m.type() === 'warning') env.consoleLog.push({ page: label, at: Date.now(), type: m.type(), text: m.text(), url: m.location()?.url ?? '' });
  });
  page.on('pageerror', (e) => env.consoleLog.push({ page: label, at: Date.now(), type: 'pageerror', text: String(e?.message ?? e), url: '' }));
  page.on('response', (r) => {
    if (r.status() < 400) return;
    let p = '';
    try { p = new URL(r.url()).pathname; } catch { p = r.url(); }
    env.httpErrors.push({ page: label, at: Date.now(), method: r.request().method(), status: r.status(), path: p });
  });
  page.on('dialog', (d) => {
    env.dialogs.push({ page: label, type: d.type(), message: d.message() });
    d.dismiss().catch(() => {});
  });
  return app;
}

/** 一個瀏覽器內容：CSP 收集器、probeInit 與情境的 initScripts（依序；每個是 { fn, arg }）掛在它的每個頁面 */
async function newBrowserContext(env) {
  const context = await newContext(env.browser, { ignoreHTTPSErrors: true, locale: 'zh-TW', timezoneId: 'Asia/Taipei', viewport: { width: 1440, height: 900 } });
  await context.exposeBinding('__probeCsp', (source, v) => { env.cspLog.push({ page: labelOf(env, source.page), ...v }); });
  await context.addInitScript(probeInit, env.pageInit.initOpts);
  for (const s of env.pageInit.initScripts) await context.addInitScript(s.fn, s.arg ?? null);
  return context;
}

/**
 * 另一個瀏覽器內容（WP4.9 步 15）：自己的視窗、自己登入一次（另一個工作階段）。同一個內容裡只有前景的分頁跑 requestAnimationFrame（HER:140），
 * 網頁以它合併畫面的更新；要同時量兩個頁面的「顯示」時間時，第二個頁面開在這裡。回傳 { context, login }
 */
export async function addBrowserContext(env, label) {
  const context = await newBrowserContext(env);
  env.extraContexts = [...(env.extraContexts ?? []), context];
  const r = await login(env, context, `L-${label}`);
  return { context, login: r };
}

/** 擁有者在 https://127.0.0.1:<代理埠>/ 以預設密碼登入（登入頁的 __hubProbe.login）；確認 cookie 與 /api/v1/auth/me */
async function login(env, context = env.context, label = 'L') {
  const page = await context.newPage();
  watch(env, page, label);
  await page.bringToFront();
  await page.goto(`${env.base}/`, { waitUntil: 'load' });
  const ready = await page.waitForFunction(() => window.__hubProbe && window.__hubProbe.ready === true, null, { timeout: 20000 }).then(() => true, () => false);
  const r = ready ? await page.evaluate(async ([u, p]) => {
    try {
      const x = await window.__hubProbe.login(u, p);
      return x ?? { ok: true };
    } catch (e) {
      return { error: true, message: e.message, status: e.status ?? null, code: e.code ?? null };
    }
  }, [OWNER_USER, env.password]) : { error: true, message: '登入頁沒有就緒' };
  const cookies = await context.cookies(env.base);
  const cookie = cookies.find((c) => c.name === SESSION_COOKIE) ?? null;
  const me = await page.evaluate(async () => {
    const res = await fetch('/api/v1/auth/me', { credentials: 'same-origin', cache: 'no-store' });
    let name = null;
    try { name = (await res.json()).name ?? null; } catch { /* 不是 JSON */ }
    return { status: res.status, name };
  }).catch(() => ({ status: null, name: null }));
  const ok = !r.error && !!cookie && cookie.secure && cookie.httpOnly && me.status === 200 && me.name === OWNER_USER;
  env.step(label === 'L' ? '擁有者登入（127.0.0.1 來源）' : `擁有者登入（另一個瀏覽器內容 ${label}）`, ok,
    `${env.base}/ 的登入頁${ready ? '就緒' : '沒有就緒'}、login ${r.error ? `失敗：${r.message}` : '成功'}；cookie ${cookie ? `${cookie.name}（網域 ${cookie.domain}、Secure ${cookie.secure}、HttpOnly ${cookie.httpOnly}、SameSite ${cookie.sameSite}）` : '沒有'}；`
      + `/api/v1/auth/me ${me.status}（${me.name ?? '—'}）`,
    { cookie: cookie ? { name: cookie.name, domain: cookie.domain, secure: cookie.secure, httpOnly: cookie.httpOnly, sameSite: cookie.sameSite, path: cookie.path } : null, me, login: r.error ? r : { ok: true } });
  await Promise.race([page.close().catch(() => {}), sleep(5000)]);
  return { ok, cookie: cookie?.name ?? null, me };
}

/** 版面（存在瀏覽器的 localStorage `myriagent.app.layout.v1`，同 remote-control.mjs）：每個窗格 { tabs: [對話 id…], active }，最多 12 個窗格（同 Web/workspace.js 的 MAX_PANES） */
export function appLayout(panes) {
  const list = panes.slice(0, 12).map((p, i) => ({ id: `p${i + 1}`, tabs: p.tabs, active: p.active ?? p.tabs[0] ?? null }));
  return { panes: list, focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' };
}

export const LAYOUT_KEY = 'myriagent.app.layout.v1';

/**
 * 開一個 /app/ 分頁並等它連上（傳輸層 online、這條連線收到 init、#app.ready、狀態列隱藏）：{ label, page, live }。
 * layout（appLayout 的結果）：載入前寫進這個分頁的 localStorage（網頁照它還原分頁並 open、visible；每個分頁只寫第一次）；
 * context：開在哪個瀏覽器內容（預設 env.context；另一個見 addBrowserContext）；beforeGoto(app)：導覽之前呼叫（例如掛 page.on('websocket')）
 */
export async function openApp(env, label, { url = '/app/', waitMs = 20000, layout = undefined, context = env.context, beforeGoto = null } = {}) {
  const page = await context.newPage();
  const app = watch(env, page, label);
  if (beforeGoto) await beforeGoto(app);
  if (layout !== undefined) {
    await page.addInitScript(([key, text]) => {
      try {
        if (location.pathname === '/app/' && !sessionStorage.getItem('ff.layout')) {
          localStorage.setItem(key, text);
          sessionStorage.setItem('ff.layout', '1');
        }
      } catch { /* 存不了就照瀏覽器存下的 */ }
    }, [LAYOUT_KEY, JSON.stringify(layout)]);
  }
  await page.bringToFront();
  await page.goto(`${env.base}${url}`, { waitUntil: 'load' });
  app.live = !!(await waitLive(app, waitMs));
  app.transport = await tstatus(app);
  return app;
}

/** 操作分頁之前先帶到前景（背景分頁的 requestAnimationFrame 停住，HER:140） */
export async function front(app) {
  await app.page.bringToFront();
}

/** 不是預期的主控台錯誤與 ≥400 的回應（scenario 以 env.expected.push((m) => 原因或 null) 加自己的預期） */
export function unexpected(env) {
  const why = (m) => baseExpected(m) ?? env.expected.map((f) => f(m)).find(Boolean) ?? null;
  const consoleErrors = env.consoleLog.filter((m) => m.type === 'error' || m.type === 'pageerror');
  return {
    console: consoleErrors.filter((m) => !why({ ...m, path: urlPath(m.url) })),
    consoleTotal: consoleErrors.length,
    http: env.httpErrors.filter((e) => !why(e)),
    httpTotal: env.httpErrors.length,
    csp: env.cspLog.filter((v) => !env.expected.map((f) => f({ ...v, csp: true })).find(Boolean)),
    dialogs: env.dialogs.slice(),
  };
}

function urlPath(u) {
  try {
    return new URL(u).pathname;
  } catch {
    return u ?? '';
  }
}

// ───── 收尾 ─────

async function closeEnv(env, internal) {
  env.closed = true;
  const step = env.step;
  const out = { browserClosed: null, devices: [], hub: null, keys: null, tripwire: null };
  // 1. 先寫結果檔（之後的關閉都可能卡住）
  try { env.report.save(); } catch { /* 寫不出來由呼叫端的最後一次 save 回報 */ }
  // 2. 有時限地關瀏覽器
  if (env.browser) {
    out.browserClosed = await Promise.race([env.browser.close().then(() => true, () => false), sleep(10000).then(() => false)]);
    step('關閉瀏覽器', out.browserClosed, out.browserClosed ? 'browser.close() 返回' : 'browser.close() 10 秒內沒有返回（結果已先寫好）');
  }
  // 3. 依序收假裝置：quit（等 outbox 清空）→ unpair（只刪本機的配對資料）；只剩 pending-key.json（配對到一半被逾時以 PID 結束，
  //    finally 的 PairingRecovery.Abandon 沒跑到）時跑一次 status，讓 SyncCoordinator.LoadAsync 的 PairingRecovery.Cleanup 刪掉那把金鑰（不連線；同 measure_sync）
  for (const dev of env.devices.values()) {
    const q = dev.run ? await dev.run.quit(90000) : null;
    const deviceFile = path.join(dev.data, 'sync', 'device.json');
    const pendingFile = path.join(dev.data, 'sync', 'pending-key.json');
    dev.keyName ??= readKeyName(dev.data);
    const action = cleanupAction({ hasDevice: fs.existsSync(deviceFile), hasPending: fs.existsSync(pendingFile) });
    const runEnv = internal.childEnv ?? childEnv();
    const un = action === 'unpair'
      ? await runOnceRetry(env.fakeExe, unpairArgs({ dataDir: dev.data, device: dev.cfg }), { env: runEnv, timeoutMs: 60000, logBase: path.join(env.work, `${dev.name}-unpair`) })
      : null;
    const pendingRun = action === 'status'
      ? await runOnceRetry(env.fakeExe, statusArgs({ dataDir: dev.data, device: dev.cfg }), { env: runEnv, timeoutMs: 60000, logBase: path.join(env.work, `${dev.name}-pending-status`) })
      : null;
    const pendingCleanup = pendingRun ? { code: pendingRun.code, attempts: pendingRun.attempts, left: fs.existsSync(pendingFile), keyLeft: dev.keyName ? keyFilesContaining([dev.keyName])[dev.keyName].length : null } : null;
    const left = fs.existsSync(deviceFile) || fs.existsSync(pendingFile);
    const final = q?.final ?? null;
    const ok = (!q || (q.exit === 0 && !q.forced)) && (!un || un.code === 0) && (!pendingCleanup || (pendingCleanup.code === 0 && !pendingCleanup.left && !pendingCleanup.keyLeft)) && !left;
    out.devices.push({ name: dev.name, quit: q ? { exit: q.exit, forced: q.forced, ended: final?.control?.ended ?? null, drained: final?.drained ?? null } : null, unpair: un ? { code: un.code, attempts: un.attempts } : null, pendingCleanup, left });
    step(`收尾：假裝置 ${dev.name}（quit、unpair）`, ok,
      `${q ? `quit 之後結束碼 ${q.exit}${q.forced ? '（逾時，已以 PID 結束整棵樹）' : ''}、控制通道 ${final?.control?.ended ?? '—'}（${final?.control?.commands ?? '—'} 個指令、${final?.control?.errors ?? '—'} 個錯誤）、outbox ${final?.drained === true ? '清空' : final?.drained === false ? '沒清空' : '—'}` : '沒有在跑'}；`
        + `${un ? `unpair 結束碼 ${un.code}${un.attempts > 1 ? `（試了 ${un.attempts} 次）` : ''}` : pendingCleanup ? `只剩 pending-key.json：status 結束碼 ${pendingCleanup.code}（配對清理之後 pending-key.json ${pendingCleanup.left ? '還在' : '不在'}、金鑰檔 ${pendingCleanup.keyLeft ?? '—'} 個）` : '沒有配對資料'}${left ? '、配對資料還在' : ''}`,
      out.devices.at(-1));
  }
  // 4. 伺服器（admin stop --data；30 秒內沒結束才強制結束、刪掉這個實例的金鑰檔）
  if (env.hub) {
    const r = await env.hub.close();
    out.hub = r;
    step('收尾：停止 MyriagentHub（admin stop --data）', !r.forced && r.adminCode === 0 && env.hub.exitCode === 0 && r.keysLeft.length === 0,
      `admin stop 結束碼 ${r.adminCode}、伺服器結束碼 ${env.hub.exitCode}${r.forced ? '（30 秒內沒有結束，已強制結束）' : ''}；這個實例的金鑰檔還在 ${r.keysLeft.length} 個`, r);
  }
  // 4b. 情境另外開的伺服器（startAltHub，FF9 的回滾鏈）：情境中途丟例外時沒收到的照樣收（已經停的只刪來不及刪的金鑰檔、刪複製的資料夾）
  out.altHubs = [];
  for (const h of env.altHubs ?? []) {
    const wasRunning = h.exitCode === null;
    const r = await h.close().catch((e) => ({ forced: null, adminCode: null, keysLeft: [], error: String(e?.message ?? e) }));
    out.altHubs.push({ needle: h.needle, wasRunning, forced: r.forced, adminCode: r.adminCode, exitCode: h.exitCode, keysLeft: r.keysLeft.length, error: r.error ?? null });
    if (wasRunning) {
      step(`收尾：情境沒收到的另一個 MyriagentHub（${h.needle}）`, false,
        `情境中途結束、這個伺服器還在跑：admin stop 結束碼 ${r.adminCode}${r.forced ? '（逾時，已強制結束）' : ''}、伺服器結束碼 ${h.exitCode}；這次已收掉，情境本身要修`, out.altHubs.at(-1));
    }
  }
  // 5. 代理
  if (env.proxy) {
    const stats = env.proxy.stats();
    await env.proxy.close();
    out.proxy = stats;
    step('收尾：關閉延遲代理', null, `連線 ${stats.connections} 條、上行 ${stats.chunksUp} 塊／${stats.bytesUp} 位元組、下行 ${stats.chunksDown} 塊／${stats.bytesDown} 位元組；`
      + `實際延遲上行平均 ${stats.lag.up.meanMs ?? '—'} ms（最大 ${stats.lag.up.maxMs}）、下行平均 ${stats.lag.down.meanMs ?? '—'} ms（最大 ${stats.lag.down.maxMs}）`, stats);
  }
  // 6. 金鑰：這次的裝置金鑰都不在、伺服器的 needle 也找不到；其他新出現的只記成觀察
  await sleep(500);
  const names = [...env.devices.values()].map((d) => d.keyName).filter(Boolean);
  const hits = keyFilesContaining(names);
  const deviceLeft = names.filter((n) => hits[n].length > 0);
  const hubLeft = env.hub ? keyFilesNamingNeedle(env.hub.needle) : [];
  // 另外開的伺服器（startAltHub）各自的 needle 也要找不到（WP4.9 合併前審查：只比 env.hub 的話看不到它們）
  const altHubLeft = (env.altHubs ?? []).flatMap((h) => keyFilesNamingNeedle(h.needle));
  out.keys = { deviceKeys: names, deviceLeft, hubNeedle: env.hub?.needle ?? null, hubLeft, altHubNeedles: (env.altHubs ?? []).map((h) => h.needle), altHubLeft };
  if (env.hub || names.length > 0) {
    step('主機副作用（孤兒金鑰檔）', deviceLeft.length === 0 && hubLeft.length === 0 && altHubLeft.length === 0,
      `這次的裝置金鑰 ${names.length} 把，解除配對後還在 ${deviceLeft.length} 把；MyriagentHub 複本（${env.hub?.needle ?? '—'}）的金鑰檔還在 ${hubLeft.length} 個`
        + `${(env.altHubs ?? []).length > 0 ? `、另外開的 ${env.altHubs.length} 個 MyriagentHub 的金鑰檔還在 ${altHubLeft.length} 個` : ''}（${keysDir}）`, out.keys);
    const known = new Set([...env.keysBefore]);
    const prefix = Buffer.from(DEVICE_KEY_PREFIX, 'utf16le');
    const others = listKeys().filter((k) => !known.has(k)).filter((k) => {
      try { return fs.readFileSync(path.join(keysDir, k)).includes(prefix); } catch { return false; }
    });
    if (others.length > 0) step('其他程序的裝置金鑰檔', null, `執行期間出現、現在還在、含 ${DEVICE_KEY_PREFIX} 但不是這次的金鑰檔 ${others.length} 個：只記錄、不算失敗`, { others });
  }
  // 7. tripwire：墊片沒被呼叫、掃描期間自己的程序樹裡沒有 CLI、至少掃過一次
  const s = env.scanner;
  const started = !!s.loop;
  s.running = false;
  if (s.loop) await Promise.race([s.loop, sleep(70000)]);
  if (env.tripwire) {
    const tripped = env.tripwire.tripped();
    out.tripwire = { tripped, scans: s.scans, failures: s.failures, hits: s.hits, maxTree: s.maxTree, lastError: s.lastError, started };
    // 掃描在守門通過之後才開始：更早失敗時只看墊片
    step('tripwire 沒有觸發', tripped.length === 0 && s.hits.length === 0 && (!started || s.scans > 0),
      `墊片被呼叫 ${tripped.length} 次${tripped.length ? `（${tripped.join('、')}）` : ''}；程序樹掃了 ${s.scans} 次（失敗 ${s.failures} 次、樹裡最多 ${s.maxTree} 個程序），CLI ${s.hits.length} 個`, out.tripwire);
  }
  // 8. 記錄不含擁有者密碼（UTF-8 與 UTF-16LE）
  out.passwordScan = scanForPassword(env);
  step('結果與記錄不含擁有者密碼', out.passwordScan.found.length === 0 && out.passwordScan.selfTest,
    `搜尋 ${out.passwordScan.scanned} 個檔（工作資料夾的 .log、.json 與資料根的記錄檔；UTF-8 與 UTF-16LE）：${out.passwordScan.found.length === 0 ? '都沒有' : `出現在 ${out.passwordScan.found.join('、')}`}（正向對照${out.passwordScan.selfTest ? '找得到' : '找不到'}）`,
    out.passwordScan);
  // 9. 放鎖
  if (internal.heartbeat) internal.heartbeat.stop();
  if (internal.lockHeld) {
    const released = releaseLock();
    if (!released.ok) step('釋放序列化鎖', false, released.message);
    out.lockBeats = internal.heartbeat?.beats() ?? 0;
  }
  env.closeResult = out;
  try { env.report.save(); } catch { /* 呼叫端的最後一次 save */ }
  return out;
}

function keyFilesNamingNeedle(needle) {
  const wanted = Buffer.from(needle, 'utf16le');
  return listKeys().filter((k) => {
    try { return fs.readFileSync(path.join(keysDir, k)).includes(wanted); } catch { return false; }
  });
}

/** 工作資料夾裡的記錄、結果與劇本（.log、.json、.txt），與各資料根的 logs\、diag\：都不含擁有者密碼 */
function scanForPassword(env) {
  const needles = [Buffer.from(env.password, 'utf8'), Buffer.from(env.password, 'utf16le')];
  const files = walk(env.work, (n) => /\.(log|json|txt|jsonl)$/iu.test(n));
  const found = [];
  for (const f of files) {
    let buf;
    try { buf = fs.readFileSync(f); } catch { continue; }
    if (needles.some((n) => buf.includes(n))) found.push(path.relative(env.work, f));
  }
  const probe = Buffer.concat([Buffer.from('x'), Buffer.from(env.password, 'utf16le')]);
  const selfTest = needles.some((n) => probe.includes(n));
  return { scanned: files.length, found, selfTest };
}

/** 結果檔：每一步先寫（browser.close() 可能不返回）；scenario 的報告與 env 的報告共用 */
export function createReport(outFile, root, current) {
  const save = () => {
    fs.mkdirSync(path.dirname(outFile), { recursive: true });
    fs.writeFileSync(outFile, JSON.stringify(root, null, 2));
  };
  return {
    save,
    step(name, ok, summary, data) {
      const target = current();
      target.steps.push({ name, ok, at: new Date().toISOString(), summary, ...(data === undefined ? {} : { data }) });
      save();
      console.log(`[${ok === null ? '觀察' : ok ? '通過' : '失敗'}] ${name}：${summary}`);
    },
  };
}

/** 頁面直接呼叫 myriagentHubTransport.post（不經 app.js）：回 commandId（沒有送出是 null） */
export function postCmd(app, msg) {
  return app.page.evaluate((m) => {
    try {
      const v = window.myriagentHubTransport.post(m);
      return typeof v === 'string' ? v : null;
    } catch {
      return null;
    }
  }, msg).catch(() => null);
}

/** 頁面收到的傳輸層訊息（probeInit 的記錄訂閱者；依順序） */
export function pageMsgs(app) {
  return app.page.evaluate(() => (window.__probeMsgs || []).map((x) => x.msg)).catch(() => []);
}

/** 等某個 commandId 的 commandResult（ms 內沒有回 null） */
export function waitCommandResult(app, commandId, ms = 30000) {
  return waitFor(async () => (await pageMsgs(app)).find((m) => m && m.type === 'commandResult' && m.commandId === commandId) ?? null, ms, 100);
}

/** 頁面最新的對話清單（init 或 conversations 的 list） */
export async function latestList(app) {
  const msgs = await pageMsgs(app);
  for (let i = msgs.length - 1; i >= 0; i--) {
    const m = msgs[i];
    if (m?.type === 'init' && Array.isArray(m.conversations)) return m.conversations;
    if (m?.type === 'conversations' && Array.isArray(m.items)) return m.items;
  }
  return null;
}
