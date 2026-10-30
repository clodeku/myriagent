// hub-e2e.mjs、gateway.mjs、pairing.mjs、remote-control.mjs、full-flow.mjs、real-exe.mjs、releases.mjs 與 ui-probe/conv-probe.mjs 共用的序列化鎖（各 worktree 都用同一個檔）：
// 都以 file 憑證模式啟動 MyriagentHub、都看整個使用者共用的 %APPDATA%\Microsoft\Crypto\Keys 判斷孤兒金鑰檔（pairing.mjs、remote-control.mjs、full-flow.mjs 另有假裝置的裝置金鑰），同時跑會互相干擾。
// 以 fs.openSync(…, 'wx') 建立（已存在就失敗），內容是持有者的 PID、工具、工作資料夾與時間；
// 已存在而且不到 10 分鐘就每 0.5 秒再試、最多等 2 分鐘；超過 10 分鐘的當成上一次被強制結束留下的、刪掉再搶；結束時只刪自己的。
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

export const lockFile = path.join(os.tmpdir(), 'myriagent-tools', 'hub-e2e.lock');
export const LOCK_STALE_MS = 10 * 60 * 1000;
export const LOCK_WAIT_MS = 2 * 60 * 1000;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/** 取得鎖：{ ok, waitedMs, staleRemoved, holder }（ok 為 false 時 holder 是持有者的內容） */
export async function acquireLock(tool, work) {
  fs.mkdirSync(path.dirname(lockFile), { recursive: true });
  const started = Date.now();
  let staleRemoved = false;
  let holder = null;
  while (true) {
    try {
      const fd = fs.openSync(lockFile, 'wx');
      fs.writeSync(fd, JSON.stringify({ pid: process.pid, tool, work, at: new Date().toISOString() }));
      fs.closeSync(fd);
      return { ok: true, waitedMs: Date.now() - started, staleRemoved, holder: null };
    } catch (e) {
      if (e?.code !== 'EEXIST') throw e;
    }
    let ageMs = null;
    try {
      ageMs = Date.now() - fs.statSync(lockFile).mtimeMs;
      holder = fs.readFileSync(lockFile, 'utf8');
    } catch {
      continue; // 剛好被刪掉：再搶一次
    }
    if (ageMs >= LOCK_STALE_MS) {
      try {
        fs.unlinkSync(lockFile);
        staleRemoved = true;
      } catch { /* 別人先刪了 */ }
      continue;
    }
    if (Date.now() - started >= LOCK_WAIT_MS) return { ok: false, waitedMs: Date.now() - started, staleRemoved, holder };
    await sleep(500);
  }
}

/** 釋放鎖（只刪自己的）：{ ok, message } */
export function releaseLock() {
  try {
    let owner = null;
    try {
      owner = JSON.parse(fs.readFileSync(lockFile, 'utf8'));
    } catch { /* 內容不是 JSON：當成不是自己的 */ }
    if (owner?.pid === process.pid) {
      fs.unlinkSync(lockFile);
      return { ok: true, message: null };
    }
    return { ok: false, message: `鎖檔的持有者不是這個程序（${owner?.pid ?? '不明'}），不刪` };
  } catch (e) {
    return { ok: false, message: `刪不掉 ${lockFile}：${e?.message ?? e}` };
  }
}
