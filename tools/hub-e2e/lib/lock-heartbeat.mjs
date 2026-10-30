// 鎖續期（WP4.9 步 7、計畫 D10）：lock.mjs 以鎖檔的 mtime 判斷過期（超過 10 分鐘當成被強制結束留下的、別人會刪掉再搶），
// 全流程與負載一次要跑十幾分鐘以上，所以持有期間每 60 秒更新 mtime。只在鎖檔內容的 pid 等於自己時才更新
// （不是自己的鎖一律不動）；不改 lock.mjs 的取得與釋放邏輯。
import fs from 'node:fs';
import { lockFile as defaultLockFile } from '../lock.mjs';

export const HEARTBEAT_MS = 60 * 1000;

/** 續期一次：鎖檔的 pid 是自己就把 mtime 設成現在、回 true；不是自己的、讀不到或不是 JSON 回 false（不動） */
export function beat(file = defaultLockFile, pid = process.pid, now = new Date()) {
  let owner = null;
  try {
    owner = JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch {
    return false;
  }
  if (owner?.pid !== pid) return false;
  try {
    fs.utimesSync(file, now, now);
    return true;
  } catch {
    return false;
  }
}

/** 開始續期（每 intervalMs 一次；計時器 unref，不撐住程序）：{ beat(), stop(), beats() } */
export function startLockHeartbeat({ file = defaultLockFile, intervalMs = HEARTBEAT_MS, pid = process.pid } = {}) {
  let count = 0;
  const once = () => {
    const ok = beat(file, pid);
    if (ok) count++;
    return ok;
  };
  const timer = setInterval(once, intervalMs);
  timer.unref();
  return {
    beat: once,
    stop() {
      clearInterval(timer);
    },
    beats: () => count,
  };
}
