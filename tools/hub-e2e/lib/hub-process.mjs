// 本機 MyriagentHub 程序的管理（WP4.9 步 7；照 tests/Myriagent.Tests/TestSupport/MyriagentHubExe.cs 與 MyriagentHubProcess.cs）：
// - 啟動前把 MyriagentHub.exe 所在的資料夾整個複製到 %TEMP%\myriagent-tools\hub-<pid>-<n>（殘留的子程序不鎖住 bin），以暫存資料根、
//   127.0.0.1、自己挑的高埠、Testing 環境、file 憑證模式執行；/healthz 200 之後讀 /readyz 過正式站守門（guard-rules）。
// - 金鑰檔（file 模式匯入 PFX 時寫進使用者金鑰存放區）以「複製後的路徑」認：檔裡記著建立者的 exe 路徑，
//   UTF-16LE 的 hub-<pid>-<n>\MyriagentHub.exe 就是 needle；只記啟動期間新增、含 needle 的。正常停止由伺服器自己刪，
//   強制結束後由這裡刪；判斷不了的只報告。
// - admin 指令一律帶 --data（沒有就丟例外：admin 沒帶 --data 時預設是 C:\ProgramData\MyriagentHub，正式資料根）；強制結束只以 PID。
import { spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import https from 'node:https';
import os from 'node:os';
import path from 'node:path';
import { childEnv } from './child-env.mjs';
import { isAllowedServerUrl, isProductionTarget, isUnderTemp } from './guard-rules.mjs';

export const keysDir = path.join(process.env.APPDATA ?? path.join(os.homedir(), 'AppData', 'Roaming'), 'Microsoft', 'Crypto', 'Keys');

let copies = 0;

/** 複製目的地的資料夾名稱（每次啟動一個；金鑰檔以它認） */
export function hubCopyName(pid = process.pid, n = copies) {
  return `hub-${pid}-${n}`;
}

/** 金鑰檔記的建立者路徑裡、認得出這份複製的字串（Windows 的路徑分隔字元） */
export function keyNeedle(copyName) {
  if (!copyName) throw new Error('要有複製的資料夾名稱');
  return path.win32.join(copyName, 'MyriagentHub.exe');
}

/**
 * 使用者金鑰存放區裡、內容含 needle（UTF-16LE 位元組、任何對齊、分大小寫；同 WithHubSupport 的 UserKeyStore.FilesNaming）的金鑰檔。
 * 金鑰檔裡的路徑不一定落在偶數位移，所以不能整檔當 UTF-16 解碼再比
 */
export function keyFilesNaming(needle, dir = keysDir) {
  const wanted = Buffer.from(needle, 'utf16le');
  const out = [];
  let names = [];
  try {
    names = fs.readdirSync(dir);
  } catch {
    return out;
  }
  for (const name of names) {
    try {
      if (fs.readFileSync(path.join(dir, name)).includes(wanted)) out.push(name);
    } catch { /* 讀不到：別人的 */ }
  }
  return out;
}

function keySnapshot(dir = keysDir) {
  try {
    return new Set(fs.readdirSync(dir));
  } catch {
    return new Set();
  }
}

/** admin 指令（一律帶 --data；沒有 dataDir 就丟例外）：{ code, stdout, stderr } */
export function adminCommand({ exe, dataDir, args = [], timeoutMs = 60000 } = {}) {
  if (!dataDir) throw new Error('admin 指令一定要帶 --data（不帶時是 C:\\ProgramData\\MyriagentHub＝正式資料根）');
  if (!exe) throw new Error('要有 MyriagentHub.exe');
  const r = spawnSync(exe, ['admin', ...args, '--data', dataDir], { encoding: 'utf8', windowsHide: true, timeout: timeoutMs, env: childEnv() });
  return { code: r.status, stdout: r.stdout ?? '', stderr: r.stderr ?? '' };
}

/** admin stop --wait（一律帶 --data） */
export function adminStop({ exe, dataDir, waitSeconds = 20 } = {}) {
  if (!dataDir) throw new Error('admin stop 一定要帶 --data');
  return adminCommand({ exe, dataDir, args: ['stop', '--wait', String(waitSeconds)], timeoutMs: (waitSeconds + 15) * 1000 });
}

/**
 * 同 adminCommand，但不擋事件迴圈（WP4.9 步 14：延遲代理在同一個 Node 程序裡，spawnSync 等 admin stop 的十幾秒代理完全不轉送）；
 * 逾時以 PID 結束這個 admin 程序，code 是 null
 */
export function adminCommandAsync({ exe, dataDir, args = [], timeoutMs = 60000, env = null } = {}) {
  if (!dataDir) throw new Error('admin 指令一定要帶 --data（不帶時是 C:\\ProgramData\\MyriagentHub＝正式資料根）');
  if (!exe) throw new Error('要有 MyriagentHub.exe');
  return new Promise((resolve) => {
    const proc = spawn(exe, ['admin', ...args, '--data', dataDir], { windowsHide: true, env: env ?? childEnv(), stdio: ['ignore', 'pipe', 'pipe'] });
    let stdout = '';
    let stderr = '';
    proc.stdout.setEncoding('utf8');
    proc.stderr.setEncoding('utf8');
    proc.stdout.on('data', (d) => { stdout += d; });
    proc.stderr.on('data', (d) => { stderr += d; });
    const timer = setTimeout(() => {
      try { proc.kill(); } catch { /* 已經結束 */ }
    }, timeoutMs);
    proc.on('error', (e) => {
      clearTimeout(timer);
      resolve({ code: null, stdout, stderr: stderr + String(e?.message ?? e) });
    });
    proc.on('close', (code) => {
      clearTimeout(timer);
      resolve({ code, stdout, stderr });
    });
  });
}

/** admin stop --wait（一律帶 --data；不擋事件迴圈） */
export function adminStopAsync({ exe, dataDir, waitSeconds = 20, env = null } = {}) {
  if (!dataDir) throw new Error('admin stop 一定要帶 --data');
  return adminCommandAsync({ exe, dataDir, args: ['stop', '--wait', String(waitSeconds)], timeoutMs: (waitSeconds + 15) * 1000, env });
}

/** 以 PID 強制結束整個程序樹（taskkill /PID <pid> /T /F；不以映像名稱） */
export function killHard(pid) {
  if (!Number.isInteger(pid) || pid <= 0) throw new Error('要有 PID');
  const r = spawnSync('taskkill', ['/PID', String(pid), '/T', '/F'], { encoding: 'utf8', windowsHide: true });
  return { code: r.status, stdout: r.stdout ?? '' };
}

/** 對本機伺服器的 GET（不驗憑證；只連 127.0.0.1）：{ status, body, cert } */
export function localGet(port, pathname, timeoutMs = 2000) {
  return new Promise((resolve, reject) => {
    const req = https.request({ host: '127.0.0.1', port, path: pathname, method: 'GET', rejectUnauthorized: false, timeout: timeoutMs, agent: false }, (res) => {
      // 憑證要在回應開始時取：end 之後 socket 可能已經拆開，getPeerCertificate 回空物件，守門就少了連線看到的憑證
      const cert = res.socket?.getPeerCertificate?.() ?? null;
      let body = '';
      res.setEncoding('utf8');
      res.on('data', (c) => { body += c; });
      res.on('end', () => resolve({ status: res.statusCode, body, cert }));
    });
    req.on('timeout', () => req.destroy(new Error('逾時')));
    req.on('error', reject);
    req.end();
  });
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/** MyriagentHubProcess.Arguments 的副本（Testing 環境、127.0.0.1、file 憑證、關掉限流與備份；PublicOrigin 預設是 https://127.0.0.1:<埠>） */
export function hubArguments({ dataDir, port, publicOrigin, extraArgs = [] }) {
  const values = {
    'Hub:ServerDomain': 'localhost',
    'Hub:HttpsPort': String(port),
    'Hub:BindAddress': '127.0.0.1',
    'Hub:BelowNormalPriority': 'false',
    'Hub:SelfTest:Enabled': 'false',
    'Hub:Acme:Enabled': 'false',
    'Hub:Backup:Enabled': 'false',
    'Hub:FileLog:Enabled': 'false',
    'Hub:AdminInbox:PollSeconds': '1',
    'Hub:RateLimits:Enabled': 'false',
    'Hub:Tls:CertStore': 'file',
    'Hub:MemoryFuse:SampleSeconds': '3600',
    'Hub:PublicOrigin': publicOrigin ?? `https://127.0.0.1:${port}`,
  };
  return ['--data', dataDir, '--environment', 'Testing', ...Object.entries(values).map(([k, v]) => `--${k}=${v}`), ...extraArgs];
}

/**
 * 啟動本機 MyriagentHub：{ pid, port, base, dataDir, exe, needle, instanceKeys, stop(), killHard(), restart(), readyz(), close() }。
 * dataDir 一定要在暫存資料夾底下（工具不碰別的資料根）；publicOrigin 要過 isAllowedServerUrl。
 * env 是子程序的環境（預設 childEnv()；harness 給 tripwire 的 PATH）；停止用不擋事件迴圈的 admin stop
 */
export async function startHub({ exe, dataDir, port, publicOrigin, extraArgs = [], timeoutMs = 60000, env = null } = {}) {
  if (!exe || !fs.existsSync(exe)) throw new Error(`找不到 MyriagentHub.exe：${exe}`);
  if (!dataDir || !isUnderTemp(dataDir)) throw new Error(`資料根要在暫存資料夾底下：${dataDir}`);
  if (!Number.isInteger(port) || port <= 1024) throw new Error('要有自己挑的高埠');
  if (publicOrigin && !isAllowedServerUrl(publicOrigin)) throw new Error(`PublicOrigin 只收本機迴路：${publicOrigin}`);
  copies++;
  const copyName = hubCopyName();
  const target = path.join(os.tmpdir(), 'myriagent-tools', copyName);
  fs.rmSync(target, { recursive: true, force: true });
  fs.cpSync(path.dirname(exe), target, { recursive: true });
  const copiedExe = path.join(target, 'MyriagentHub.exe');
  const needle = keyNeedle(copyName);
  fs.mkdirSync(dataDir, { recursive: true });
  const logFile = path.join(dataDir, '..', `${copyName}.console.log`);

  // 多給的參數：restart 可以換（WP4.9 步 16 的 FF6 以較低的 blob 配額重啟同一個資料根）
  const state = { proc: null, exit: null, instanceKeys: new Set(), forced: false, extraArgs: [...extraArgs] };

  async function launch() {
    const before = keySnapshot();
    const fd = fs.openSync(logFile, 'a');
    const proc = spawn(copiedExe, hubArguments({ dataDir, port, publicOrigin, extraArgs: state.extraArgs }), { cwd: target, stdio: ['ignore', fd, fd], windowsHide: true, env: env ?? childEnv() });
    fs.closeSync(fd);
    state.proc = proc;
    state.exit = null;
    proc.on('exit', (code) => { state.exit = code ?? -1; });
    const started = Date.now();
    let ok = false;
    try {
      while (Date.now() - started < timeoutMs && state.exit === null) {
        try {
          const r = await localGet(port, '/healthz');
          if (r.status === 200) { ok = true; break; }
        } catch { /* 還沒起來 */ }
        await sleep(100);
      }
    } finally {
      // TLS 憑證在 /healthz 200 之前就載入了：沒就緒被強制結束時也要記下，之後才刪得掉
      const mine = new Set(keyFilesNaming(needle));
      for (const name of keySnapshot()) if (!before.has(name) && mine.has(name)) state.instanceKeys.add(name);
    }
    if (!ok) {
      await hardStop();
      throw new Error(`MyriagentHub 沒有起來（結束碼 ${state.exit}），看 ${logFile}`);
    }
    const ready = await readyz();
    if (isProductionTarget(ready.json, ready.cert)) {
      await hardStop();
      throw new Error('啟動的伺服器看起來是正式服務（正式站守門）：已強制結束');
    }
  }

  async function readyz() {
    const r = await localGet(port, '/readyz');
    let json = null;
    try { json = JSON.parse(r.body); } catch { /* 不是 JSON */ }
    return { status: r.status, json, cert: r.cert };
  }

  function deleteInstanceKeys() {
    const left = [];
    for (const name of state.instanceKeys) {
      const file = path.join(keysDir, name);
      try {
        if (fs.existsSync(file)) fs.unlinkSync(file);
      } catch {
        left.push(name);
      }
    }
    state.instanceKeys.clear();
    return left;
  }

  async function waitExit(ms) {
    const started = Date.now();
    while (state.exit === null && Date.now() - started < ms) await sleep(100);
    return state.exit !== null;
  }

  async function hardStop() {
    if (state.proc && state.exit === null) {
      state.forced = true;
      killHard(state.proc.pid);
      await waitExit(10000);
    }
    return deleteInstanceKeys();
  }

  /** 正常停止（admin stop --data --wait 20）；30 秒內沒結束才強制結束。回傳 { forced, adminCode, keysLeft } */
  async function stop() {
    if (!state.proc || state.exit !== null) return { forced: false, adminCode: null, keysLeft: deleteInstanceKeys() };
    const admin = await adminStopAsync({ exe: copiedExe, dataDir, env });
    if (await waitExit(30000)) {
      // 正常結束：伺服器自己刪了金鑰檔；還在的只報告（不代刪）
      const keysLeft = [...state.instanceKeys].filter((n) => fs.existsSync(path.join(keysDir, n)));
      state.instanceKeys.clear();
      return { forced: false, adminCode: admin.code, keysLeft };
    }
    return { forced: true, adminCode: admin.code, keysLeft: await hardStop() };
  }

  await launch();
  return {
    get pid() { return state.proc?.pid ?? null; },
    get exitCode() { return state.exit; },
    get forced() { return state.forced; },
    port,
    base: `https://127.0.0.1:${port}`,
    dataDir,
    exe: copiedExe,
    needle,
    logFile,
    instanceKeys: () => [...state.instanceKeys],
    readyz,
    stop,
    /** 強制結束（S8／FF9 用）；結束後刪掉這個實例來不及刪的金鑰檔 */
    killHard: hardStop,
    /** 這次啟動多給的參數（restart 換過就是新的） */
    get extraArgs() { return [...state.extraArgs]; },
    /**
     * 同一個資料根、同一個埠再啟動一次（前一個要已經結束）。extraArgs 有給就換掉多給的參數（之後的 restart 沿用新的；
     * 和 hubArguments 的固定參數同名時以後面的為準，同 .NET 的命令列設定）
     */
    async restart({ extraArgs: next } = {}) {
      if (state.exit === null && state.proc) throw new Error('MyriagentHub 還在執行');
      if (next !== undefined) {
        if (!Array.isArray(next) || next.some((a) => typeof a !== 'string')) throw new Error('extraArgs 要是字串陣列');
        state.extraArgs = [...next];
      }
      await launch();
    },
    /** 停止並刪掉複製的資料夾 */
    async close() {
      const result = await stop();
      try { fs.rmSync(target, { recursive: true, force: true }); } catch { /* 還被占用：留給下次 */ }
      return result;
    },
  };
}
