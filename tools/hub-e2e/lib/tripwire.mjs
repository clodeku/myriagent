// tripwire（WP4.9 步 7、計畫 D21）：端到端與負載工具絕不能叫到真的 claude／codex／agy／grok（會用掉訂閱額度、碰到使用者的帳戶）。
// 兩道：
// 1. 墊片：在暫存資料夾放 claude.cmd、codex.cmd、agy.cmd、grok.cmd（被呼叫就寫標記檔、結束碼 9），子程序的 PATH 最前面是它；
//    啟動任何子程序之前先以那份 PATH 跑 where，第一筆不是墊片就丟例外（不往下跑）。
//    注意 CreateProcess 不帶副檔名時只補 .exe：直接以名稱啟動、PATH 後面又有真的 claude.exe 時墊片攔不到，所以還有第二道。
// 2. 程序掃描：只看 harness 自己啟動的程序樹（Win32_Process 的父 PID 鏈，從自己的子程序往下找；子程序的建立時間要晚於父程序，
//    擋掉 PID 重用），裡面出現這幾個 CLI（執行檔名稱或 npm 版的命令列）就算觸發。別的 session 的 claude.exe 不在樹裡、不算。
// 墊片只寫 ASCII（批次檔含中文在隱藏主控台會被讀錯位）；呼叫 powershell.exe 時剝掉 PSModulePath（從 PS7 繼承的會讓 5.1 找不到內建指令）。
import { spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { isUnderTemp } from './guard-rules.mjs';

/** 墊片的名稱（也是不准出現在自己程序樹裡的 CLI） */
export const SHIM_NAMES = ['claude', 'codex', 'agy', 'grok'];

/** 標記檔（墊片被呼叫時附加一行 CLI 名稱） */
export const MARKER_NAME = 'tripped.flag';

/** 墊片結束碼 */
export const SHIM_EXIT_CODE = 9;

// 執行檔名稱（不分大小寫）與 npm 安裝版的命令列（node.exe 跑 cli.js）
const FORBIDDEN_IMAGE = /^(?:claude|codex|agy|grok)(?:\.exe)?$/iu;
const FORBIDDEN_COMMAND = /@anthropic-ai[\\/]claude-code|@openai[\\/]codex|[\\/](?:claude|codex|agy|grok)\.exe\b/iu;

/** 墊片的內容（ASCII；%~n0 是墊片自己的名稱、%~dp0 是它的資料夾） */
export function shimScript() {
  return ['@echo off', `(echo %~n0)>>"%~dp0${MARKER_NAME}"`, `exit /b ${SHIM_EXIT_CODE}`, ''].join('\r\n');
}

/** env 裡 PATH 的鍵（Windows 不分大小寫；Object.entries(process.env) 通常是 Path） */
export function pathKey(env) {
  return Object.keys(env ?? {}).find((k) => k.toUpperCase() === 'PATH') ?? 'PATH';
}

/**
 * 從自己的子程序往下找整棵樹（純函式）：processes 是 { pid, ppid, name, commandLine, createdAt }（createdAt 是毫秒，可省略）。
 * 子程序的建立時間早於父程序的不算（父 PID 被重用）。回傳樹裡的程序（不含根）
 */
export function descendants(processes, rootPids) {
  const byParent = new Map();
  const byPid = new Map();
  for (const p of processes ?? []) {
    byPid.set(p.pid, p);
    if (!byParent.has(p.ppid)) byParent.set(p.ppid, []);
    byParent.get(p.ppid).push(p);
  }
  const out = [];
  const seen = new Set();
  const queue = [...new Set(rootPids ?? [])].map((pid) => ({ pid, createdAt: byPid.get(pid)?.createdAt }));
  for (const r of queue) seen.add(r.pid);
  while (queue.length > 0) {
    const parent = queue.shift();
    for (const child of byParent.get(parent.pid) ?? []) {
      if (seen.has(child.pid)) continue;
      if (Number.isFinite(parent.createdAt) && Number.isFinite(child.createdAt) && child.createdAt < parent.createdAt) continue;
      seen.add(child.pid);
      out.push(child);
      queue.push(child);
    }
  }
  return out;
}

/** 這個程序是不是不准出現的 CLI（純函式） */
export function isForbiddenProcess(p) {
  return FORBIDDEN_IMAGE.test(String(p?.name ?? '')) || FORBIDDEN_COMMAND.test(String(p?.commandLine ?? ''));
}

// 5.1 導向的輸出用主控台碼頁（繁中是 cp950）：命令列含中文時 JSON 會壞，先改成 UTF-8
const LIST_SCRIPT = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; Get-CimInstance Win32_Process | ForEach-Object { [pscustomobject]@{ pid=[int]$_.ProcessId; ppid=[int]$_.ParentProcessId; name=$_.Name; commandLine=$_.CommandLine; createdAt=$(if ($_.CreationDate) { [int64](([datetimeoffset]$_.CreationDate).ToUnixTimeMilliseconds()) } else { $null }) } } | ConvertTo-Json -Compress -Depth 2";
const LIST_ARGS = ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', LIST_SCRIPT];

function listEnv() {
  const env = { ...process.env };
  for (const k of Object.keys(env)) if (k.toUpperCase() === 'PSMODULEPATH') delete env[k];
  return env;
}

function parseList(stdout) {
  const parsed = JSON.parse(stdout);
  return Array.isArray(parsed) ? parsed : [parsed];
}

/** 全機的程序清單（Win32_Process；經 powershell.exe 5.1、剝掉 PSModulePath）。會擋住事件迴圈幾秒：同一個程序裡有延遲代理或瀏覽器時用 listProcessesAsync */
export function listProcesses() {
  const r = spawnSync('powershell.exe', LIST_ARGS, { encoding: 'utf8', windowsHide: true, env: listEnv(), maxBuffer: 64 * 1024 * 1024, timeout: 60000 });
  if (r.status !== 0) throw new Error(`列不出程序清單（結束碼 ${r.status}）：${(r.stderr ?? '').trim().slice(0, 500)}`);
  return parseList(r.stdout);
}

/**
 * 同 listProcesses，但不擋事件迴圈（WP4.9 步 14：harness 每 2 秒掃一次，延遲代理與瀏覽器在同一個 Node 程序裡，
 * spawnSync 的幾秒會讓代理停止轉送、量到的延遲失真）。60 秒沒有結束就以 PID 結束這個 powershell 並丟例外
 */
export function listProcessesAsync() {
  return new Promise((resolve, reject) => {
    const proc = spawn('powershell.exe', LIST_ARGS, { windowsHide: true, env: listEnv(), stdio: ['ignore', 'pipe', 'pipe'] });
    const out = [];
    const err = [];
    proc.stdout.on('data', (d) => out.push(d));
    proc.stderr.on('data', (d) => err.push(d));
    const timer = setTimeout(() => {
      try { proc.kill(); } catch { /* 已經結束 */ }
    }, 60000);
    proc.on('error', (e) => {
      clearTimeout(timer);
      reject(e);
    });
    proc.on('close', (code) => {
      clearTimeout(timer);
      if (code !== 0) {
        reject(new Error(`列不出程序清單（結束碼 ${code}）：${Buffer.concat(err).toString('utf8').trim().slice(0, 500)}`));
        return;
      }
      try {
        resolve(parseList(Buffer.concat(out).toString('utf8')));
      } catch (e) {
        reject(e);
      }
    });
  });
}

/** 自己啟動的程序樹裡、不准出現的 CLI（rootPids 是自己的子程序；空清單回空） */
export function scanOwnTree(rootPids, processes = null) {
  const roots = (rootPids ?? []).filter((pid) => Number.isInteger(pid) && pid > 0);
  if (roots.length === 0) return [];
  return descendants(processes ?? listProcesses(), roots).filter(isForbiddenProcess);
}

/** 同 scanOwnTree，以 listProcessesAsync 列程序（不擋事件迴圈）；回 { hits, scanned }（scanned＝樹裡的程序數） */
export async function scanOwnTreeAsync(rootPids) {
  const roots = (rootPids ?? []).filter((pid) => Number.isInteger(pid) && pid > 0);
  if (roots.length === 0) return { hits: [], scanned: 0 };
  const tree = descendants(await listProcessesAsync(), roots);
  return { hits: tree.filter(isForbiddenProcess), scanned: tree.length };
}

/**
 * 在 dir（要在暫存資料夾底下）放墊片：{ dir, marker, childPath(base), childEnv(env), verifyShims(env), tripped(), scanOwnTree(rootPids), assertNotTripped(rootPids) }
 */
export function createTripwire(dir) {
  if (!isUnderTemp(dir)) throw new Error(`墊片要放在暫存資料夾底下：${dir}`);
  const full = path.resolve(dir);
  fs.mkdirSync(full, { recursive: true });
  for (const name of SHIM_NAMES) fs.writeFileSync(path.join(full, `${name}.cmd`), shimScript(), { encoding: 'ascii' });
  const marker = path.join(full, MARKER_NAME);
  fs.rmSync(marker, { force: true });

  const tw = {
    dir: full,
    marker,
    shims: Object.fromEntries(SHIM_NAMES.map((n) => [n, path.join(full, `${n}.cmd`)])),
    /** 墊片資料夾放最前面的 PATH 值 */
    childPath(base = process.env.PATH ?? '') {
      return [full, ...String(base).split(';').filter((p) => p && path.resolve(p).toLowerCase() !== full.toLowerCase())].join(';');
    },
    /** 把 env 的 PATH 換成墊片在最前面的（保留原本鍵的大小寫，不會多一個 PATH 鍵） */
    childEnv(env) {
      const key = pathKey(env);
      return { ...env, [key]: tw.childPath(env?.[key] ?? '') };
    },
    /**
     * 以 env 的 PATH 跑 where：每個名稱的第一筆都要是墊片，否則丟例外（只看 PATH：where 的 $PATH: 前置字元不找目前資料夾，
     * 子程序的目前資料夾和這裡不同）
     */
    verifyShims(env) {
      const problems = [];
      for (const name of SHIM_NAMES) {
        const r = spawnSync('where.exe', [`$PATH:${name}`], { encoding: 'utf8', windowsHide: true, env });
        const first = (r.stdout ?? '').split(/\r?\n/u).find((l) => l.trim().length > 0)?.trim() ?? '';
        if (first.toLowerCase() !== tw.shims[name].toLowerCase()) problems.push(`${name} → ${first || '（找不到）'}`);
      }
      if (problems.length > 0) throw new Error(`子程序的 PATH 第一個找到的不是墊片：${problems.join('；')}`);
      return true;
    },
    /** 墊片被呼叫過的名稱（依呼叫順序） */
    tripped() {
      try {
        return fs.readFileSync(marker, 'ascii').split(/\r?\n/u).map((l) => l.trim()).filter(Boolean);
      } catch {
        return [];
      }
    },
    scanOwnTree,
    /** 墊片被叫過、或自己的程序樹裡有真的 CLI 就丟例外 */
    assertNotTripped(rootPids = []) {
      const names = tw.tripped();
      if (names.length > 0) throw new Error(`tripwire：墊片被呼叫了（${names.join('、')}）`);
      const found = scanOwnTree(rootPids);
      if (found.length > 0) throw new Error(`tripwire：自己的程序樹裡有 CLI：${found.map((p) => `${p.name}(${p.pid})`).join('、')}`);
    },
  };
  return tw;
}
