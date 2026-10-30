// FF1～FF6 共用的小工具（WP4.9 步 15、16）：假裝置的狀態與串流、頁面乾淨的檢查、等快照、S7 的判定與摘要、伺服器 sync.db 的只讀查詢。
import { spawn } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { unexpected, waitFor } from '../lib/harness.mjs';
import { judgeS7, latencySamples, worstSamples } from '../lib/flow-rules.mjs';
import { ffSnapshot, rafCount } from '../lib/flow-page.mjs';
import { xssState } from '../../ui-probe/hub-page.mjs';

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const SQLITE_RO = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'lib', 'sqlite_ro.py');

/**
 * 伺服器 sync.db 的只讀查詢（lib/sqlite_ro.py：file:…?mode=ro，伺服器執行中也讀得到）：args 例如 ['conversation', id]。
 * 一律非同步（延遲代理在同一個 Node 程序裡，不能 spawnSync）；回解析後的 JSON，失敗丟例外（訊息帶 stderr）
 */
export function sqliteRo(env, args, ms = 30000) {
  return sqliteRoAt(path.join(env.hub.dataDir, 'db', 'sync.db'), args, ms);
}

/** 同 sqliteRo，但指定資料庫（WP4.9 步 18：假裝置自己的 sync.db 的 publish-blocks） */
export function sqliteRoAt(db, args, ms = 30000) {
  return new Promise((resolve, reject) => {
    const p = spawn('python', ['-X', 'utf8', '-I', SQLITE_RO, db, ...args], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    let out = '';
    let err = '';
    p.stdout.setEncoding('utf8');
    p.stderr.setEncoding('utf8');
    p.stdout.on('data', (d) => { out += d; });
    p.stderr.on('data', (d) => { err += d; });
    const timer = setTimeout(() => { try { p.kill(); } catch { /* 已結束 */ } }, ms);
    p.on('error', (e) => { clearTimeout(timer); reject(new Error(`sqlite_ro 起不來：${e?.message ?? e}`)); });
    p.on('close', (code) => {
      clearTimeout(timer);
      if (code !== 0) { reject(new Error(`sqlite_ro ${args.join(' ')} 結束碼 ${code}：${err.trim()}`)); return; }
      try { resolve(JSON.parse(out)); } catch { reject(new Error(`sqlite_ro 的輸出不是 JSON：${out.slice(0, 200)}`)); }
    });
  });
}

/** 伺服器上的一個對話（sqlite_ro 的 conversation）：查不了時回 null（等待迴圈照常重試） */
export function serverConversation(env, id) {
  return sqliteRo(env, ['conversation', id]).catch(() => null);
}

/** 本機（假裝置）的一個桌面操作（控制通道的 localCommand）：回 { cmd, toasts, conversation?, paused?, state? }，失敗丟例外 */
export function localCommand(dev, cmd, args = {}, ms = 60000) {
  return control(dev, 'localCommand', { cmd, args }, ms);
}

/** 這台的複本（控制通道的 replicas，只要一個對話）：那一列（沒有是 null） */
export async function replicaOf(dev, id) {
  const r = await dev.run.control('replicas', { conversation: id }, 30000);
  return r.ok ? (r.result.replicas ?? [])[0] ?? null : null;
}

/** 假裝置的 status（失敗回 null） */
export async function deviceStatus(dev, ms = 30000) {
  const r = await dev.run.control('status', {}, ms);
  return r.ok ? r.result : null;
}

/** 控制通道的一個指令：失敗丟例外（訊息帶 op 與假裝置的錯誤） */
export async function control(dev, op, params = {}, ms = 60000) {
  const r = await dev.run.control(op, params, ms);
  if (!r.ok) throw new Error(`${dev.name} 的 ${op} 失敗：${r.error ?? '不明'}`);
  return r.result;
}

/** 以一段很短的固定回覆開一個新對話（stream wait:true、沒有標記）：回對話 id */
export async function newConversation(dev, alias, message) {
  const r = await control(dev, 'stream', { conversation: alias, account: dev.cfg.alias, message, segments: 1, delayMs: 0, wait: true }, 90000);
  if (r.outcome !== 'Completed' || typeof r.conversationId !== 'string') throw new Error(`${dev.name} 開對話 ${alias} 沒有完成：${JSON.stringify(r)}`);
  return r.conversationId;
}

/** 這些別名最後一個背景串流都結束了（status 的 streams 同一個別名取最後一筆）：回 { 別名: 那一筆 }，ms 內沒結束回 null */
export async function waitStreamsDone(dev, keys, ms) {
  return waitFor(async () => {
    const s = await deviceStatus(dev, 15000);
    if (!s) return null;
    const last = {};
    for (const run of s.streams ?? []) last[run.conversation] = run;
    return keys.every((k) => last[k]?.done === true) ? Object.fromEntries(keys.map((k) => [k, last[k]])) : null;
  }, ms, 500);
}

/** 頁面都收到這些對話的 conversation 快照（版面還原後網頁自己 open）：回 true，ms 內沒有回 false */
export async function waitSnapshots(app, ids, ms = 20000) {
  return !!(await waitFor(async () => {
    for (const id of ids) if (!(await ffSnapshot(app, id))) return null;
    return true;
  }, ms, 200));
}

/** 這些頁面的 requestAnimationFrame 都在跑（畫面的更新沒有停住；HER:140）：{ ok, counts: { 標籤: 次數 } } */
export async function rafAlive(apps) {
  const counts = {};
  for (const a of apps) counts[a.label] = await rafCount(a, 400);
  return { ok: Object.values(counts).every((n) => typeof n === 'number' && n > 0), counts };
}

/**
 * 一條路的 S7（judgeS7）：source 'dom'（畫面顯示）不能用時（rAF 停住）改用 'ws' 並在 note 說明；另外附上另一個來源的摘要供對照。
 * 回 { ok, source, summary, reason, other: { source, summary }, missing }
 */
export function s7Of(records, { path, conversations, window = null, domOk }) {
  const source = domOk ? 'dom' : 'ws';
  const main = latencySamples(records, { path, source, conversations, window });
  const verdict = judgeS7(main);
  const otherSource = source === 'dom' ? 'ws' : 'dom';
  const other = latencySamples(records, { path, source: otherSource, conversations, window });
  // 最慢的 10 個（標記的時間＝產生的時間；拿來對照回補的時間線）
  const worst = worstSamples(records, { path, source, conversations, window }, 10);
  return {
    ok: verdict.ok, source, summary: verdict.summary, reason: verdict.reason, missing: main.missing, worst,
    other: { source: otherSource, summary: judgeS7(other).summary, missing: other.missing },
  };
}

/** S7 摘要的一句話 */
export function s7Text(label, s) {
  const x = s.summary;
  return `${label}（${s.source === 'dom' ? '畫面顯示' : '傳輸層收到'}）${x.count} 個樣本：p50 ${x.p50 ?? '—'} ms、p95 ${x.p95 ?? '—'} ms、p99 ${x.p99 ?? '—'} ms、最大 ${x.max ?? '—'} ms`
    + `${s.ok ? '' : `（${s.reason}）`}；對照 ${s.other.source === 'dom' ? '畫面' : '傳輸層'} p95 ${s.other.summary.p95 ?? '—'} ms`;
}

/** 頁面乾淨：主控台、≥400 的回應、CSP、對話框、XSS 計數（同 smoke 的第 5 點）；寫一步、回結果的計數 */
export async function pageCleanStep(env, apps, name) {
  const u = unexpected(env);
  let xss = 0;
  for (const a of apps) {
    const s = await xssState(a).catch(() => ({ xss: null }));
    xss = s.xss === null || xss === null ? null : xss + s.xss;
  }
  const ok = u.console.length === 0 && u.http.length === 0 && u.csp.length === 0 && u.dialogs.length === 0 && xss === 0;
  env.step(name, ok,
    `主控台錯誤 ${u.consoleTotal} 筆（非預期 ${u.console.length}）、≥400 的回應 ${u.httpTotal} 筆（非預期 ${u.http.length}）、CSP 違規 ${u.csp.length} 筆、對話框 ${u.dialogs.length} 個、XSS 計數 ${xss}`,
    { console: u.console, http: u.http, csp: u.csp, dialogs: u.dialogs });
  return { console: u.console.length, http: u.http.length, csp: u.csp.length, dialogs: u.dialogs.length, xss };
}
