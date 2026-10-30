// FF9 持久性與回滾（WP4.9 步 18，計畫塊 15）：OFFICE、HOME 兩台（HOME 4 個種子對話），網頁 P 開著 HOME 的 Y 與 OFFICE 的一個對話、Y 的輸入區有沒送出的字。
//   (a) 強制結束：下行暫停（伺服器照收照提交、確認送不回去）時 HOME 串流 1,000 段（每段一個完成的項目）、積壓到 1,000 筆，瀏覽器送一個非 durable 的 rename 給 OFFICE，
//       再暫停上行 → 以 PID 強制結束伺服器 → 3 秒內同一個埠重啟 → 放開代理。斷言：兩台與瀏覽器 30 秒內恢復；sqlite_ro 讀到每台的 deviceSeq 正好 1..N（0 遺失 0 重複，
//       N＝假裝置 outbox 的最大 deviceSeq）；那個 rename 變 unknown、不重送（OFFICE 沒有執行）；瀏覽器那一半（S8）：原始連線讀到的項目 id 與 Ord 等於 HOME 的。
//   (b) 回滾鏈（計畫 D12：OLD 1724718、PREV 5538705、DEPLOYED_PREV --prev-ref；沒設的記「未執行」、整體不通過）：每個舊組建一輪——上行暫停時 HOME 新開對話 X
//       （它的 conv.created 送出、沒確認，切換那一刻有在途批次：LIVE-1、K1 的前提）→ admin stop → 舊版開同一個資料根、代理 setUpstream → 回滾期間 HOME 把 X 改成
//       metadata、在 Y 新增帶標記的項目 → admin stop → 新版開同一個資料根。斷言：收斂（X 在伺服器上是 metadata、項目清空；改回 full 整份重送；Y 的項目 id 與 Ord 等於
//       HOME 的）；舊伺服器記下的被拒列只有 {} 或切換那一刻之前寫的（K1 凍結的）；瀏覽器：切換時斷線、30 秒內重連，有沒送出的字的 P 顯示換版橫幅、不自動重新載入，
//       沒打字的 Q 自動重新載入（WP4.6 的換版規則）；升級回來之後新開的分頁與 P 的清單等於伺服器的對話；P 的字還在；舊伺服器沒有留下金鑰檔。
//   (c) PRIV-R3-1/2：HOME 暫停同步、在 C 寫帶標記的項目 → 以 PID 結束 HOME → 以 powershell 獨占 C 的 meta.json → 重啟 HOME（中樞沒有 C）→ 解除暫停：
//       C 的待送列換成 {}、伺服器沒有標記 → 依 PID 結束 powershell → 重啟 HOME：C 回到中樞、照摘要重送，id 與 Ord 收斂、伺服器有標記。
//   (d) 還原：admin backup-now → admin stop → admin restore --from <備份> → 還原後（伺服器還沒起來）每個對話都是 recovering → 啟動：
//       90 秒內都解除（擁有者的新 conv.created），對話與項目數等於還原前；還原撤銷所有瀏覽器工作階段（RestoreService）：P 回到登入頁，重新登入之後回到 /app/。
//       所以「P 的字還在」在 (d) 之前查。
//   (e) PRIV-R3-3：第三台 OFFICE2 照抄 OFFICE 的種子配對（同 id → not_owner 封鎖）→ 在 OFFICE2 把一個被封鎖的對話改成 localOnly、之後寫一段標記 →
//       伺服器的 db\*（含 -wal）沒有標記（UTF-8 與 UTF-16LE；正向對照是 OFFICE 寫的另一段標記）→ 以 --app-version 換版重開 OFFICE2：not_owner 封鎖照舊、標記照樣不在。
// 開發時可以用環境變數 MYRIAGENT_FF9_PHASES（例如 a,c）只跑幾段；正式的驗收一律全部跑（沒跑的段落記「未執行」、整體不通過）。
import { spawn } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { addDevice, appLayout, killTree, login, openApp, postCmd, restartDevice, startAltHub, waitFor } from '../lib/harness.mjs';
import { rollbackBuilds } from '../lib/harness-rules.mjs';
import { adminCommandAsync, keyFilesNaming, localGet } from '../lib/hub-process.mjs';
import { ffList, flowRecorder, opsStatus, rawClose, rawConnect, rawReadAll } from '../lib/flow-page.mjs';
import { childEnv } from '../lib/child-env.mjs';
import { control, deviceStatus, localCommand, newConversation, pageCleanStep, sleep, sqliteRo, sqliteRoAt } from './common.mjs';

/** 計畫的五段 */
const ALL_PHASES = ['a', 'b', 'c', 'd', 'e'];

/** 換版橫幅（WP4.6；web-protocol 第一節 init 的 build） */
const BUILD_NOTICE = '伺服器已換版本，重新載入以取得對應的網頁';

/** P 輸入區裡沒送出的字（換版時不自動重新載入的條件；升級回來之後要還在） */
const UNSENT = 'FF9 換版前打的字，還沒送出';

/** startEnv 的選項：OFFICE 預設 3 個、HOME 4 個種子對話；頁面記錄器（輕量的訊息記錄） */
export const env = {
  devices: ['OFFICE', 'HOME'],
  seeds: { HOME: { count: 4, seed: 20261902 } },
  initOpts: { lightMessages: true },
  initScripts: [{ fn: flowRecorder }],
};

const rand = () => crypto.randomBytes(5).toString('hex');

/** outbox 還沒完成的列數（Pending、Retry、Sent） */
const undone = (s) => ['Pending', 'Retry', 'Sent'].reduce((n, k) => n + (s?.outbox?.[k] ?? 0), 0);

/** 等這台 Live 而且 outbox 清空：回 status，ms 內沒有回 null */
function waitLiveDrained(dev, ms) {
  return waitFor(async () => {
    const s = await deviceStatus(dev, 10000);
    return s && s.state === 'Live' && s.outbox && undone(s) === 0 ? s : null;
  }, ms, 300);
}

/** 等這些裝置都是 Live：回 { 名稱: status }，ms 內沒有回 null */
function waitAllLive(devs, ms) {
  return waitFor(async () => {
    const out = {};
    for (const d of devs) {
      const s = await deviceStatus(d, 5000);
      if (s?.state !== 'Live') return null;
      out[d.name] = s;
    }
    return out;
  }, ms, 250);
}

/** 頁面收到幾則 init（記錄器的計數；重新載入之後從 0 算） */
const initCount = (app) => app.page.evaluate(() => window.__ff?.counts?.init ?? 0).catch(() => null);

/** 頁面頂部的通知（.live-notices 的 data-notice） */
const notices = (app) => app.page.evaluate(() => [...document.querySelectorAll('.live-notices > .live-notice')].map((n) => ({ kind: n.dataset.notice ?? null, text: (n.textContent ?? '').trim() }))).catch(() => []);

/** 某個對話輸入區的字 */
const composerText = (app, id) => app.page.evaluate((cid) => {
  const ta = document.querySelector(`.conv[data-conv="${cid}"] textarea.cinput`);
  return ta ? ta.value : null;
}, id).catch(() => null);

/** 伺服器的這台：deviceSeq 1..max 沒有間隙、沒有重複、max 等於假裝置 outbox 的最大值 */
async function seqCheck(env, dev, status) {
  const r = await sqliteRo(env, ['device', dev.deviceId]);
  const max = status?.outboxSeq?.max ?? null;
  const ok = r.gaps.length === 0 && r.duplicates === 0 && max !== null && r.maxDeviceSeq === max;
  return { ok, events: r.events, rejected: r.rejected.length, serverMax: r.maxDeviceSeq, deviceMax: max, gaps: r.gaps.slice(0, 10), gapCount: r.gaps.length, duplicates: r.duplicates };
}

/** 新開一個分頁、開一條原始連線，讀完一個對話的項目（open＋loadOlder 到底），關掉：{ ok, ids: [id…], seq: {id: seq} } */
async function readViaBrowser(env, label, id) {
  const R = await openApp(env, label);
  try {
    if (!R.live) return { ok: false, error: '分頁沒連上', ids: [], seq: {} };
    const raw = await rawConnect(R);
    if (!raw.ok) return { ok: false, error: raw.error, ids: [], seq: {} };
    const all = await rawReadAll(R, id, { maxPages: 60 });
    await rawClose(R);
    return { ok: all.ok, error: all.error ?? null, ids: all.items.map((i) => i.id), seq: Object.fromEntries(all.items.map((i) => [i.id, i.seq])), pages: all.pages };
  } finally {
    await Promise.race([R.page.close().catch(() => {}), sleep(5000)]);
  }
}

/** 假裝置的項目（ords）與瀏覽器讀到的比：id 集合相同、每個 Ord 相同、沒有重複 */
function sameItems(ords, read) {
  const dev = new Map(ords.items.map((i) => [i.id, i.ord ?? i.publishedOrd]));
  const dup = read.ids.length - new Set(read.ids).size;
  const missing = [...dev.keys()].filter((id) => !(id in read.seq));
  const extra = read.ids.filter((id) => !dev.has(id));
  const wrongOrd = [...dev.entries()].filter(([id, ord]) => id in read.seq && read.seq[id] !== ord).length;
  return { ok: read.ok && dup === 0 && missing.length === 0 && extra.length === 0 && wrongOrd === 0, count: dev.size, read: read.ids.length, dup, missing: missing.length, extra: extra.length, wrongOrd };
}

/** 資料根 db\ 底下每個檔（含 -wal、-shm）裡有沒有這些字（UTF-8 與 UTF-16LE）：{ 字: [檔名…] } */
function scanDb(dataDir, needles) {
  const dir = path.join(dataDir, 'db');
  const out = Object.fromEntries(needles.map((n) => [n, []]));
  let files = [];
  try { files = fs.readdirSync(dir); } catch { return { hits: out, files: 0 }; }
  for (const f of files) {
    let buf;
    try { buf = fs.readFileSync(path.join(dir, f)); } catch { continue; }
    for (const n of needles) if (buf.includes(Buffer.from(n, 'utf8')) || buf.includes(Buffer.from(n, 'utf16le'))) out[n].push(f);
  }
  return { hits: out, files: files.length };
}

/** 資料根底下這個對話的 meta.json（中樞的對話資料夾以 id 命名） */
function findMeta(root, id) {
  const stack = [root];
  while (stack.length > 0) {
    const dir = stack.pop();
    let entries = [];
    try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { continue; }
    for (const e of entries) {
      if (!e.isDirectory()) continue;
      const full = path.join(dir, e.name);
      if (e.name === id && fs.existsSync(path.join(full, 'meta.json'))) return path.join(full, 'meta.json');
      if (!/^[0-9a-f]{32}$/u.test(e.name)) stack.push(full);
    }
  }
  return null;
}

/**
 * 以 Windows PowerShell 獨占一個檔（FileShare.None）：計畫 18 (c) 的作法。不用 Start-Sleep（從 Claude Code 啟動時繼承 PS7 的 PSModulePath，
 * 5.1 找不到 Microsoft.PowerShell.Utility），等待用 [Threading.Thread]::Sleep；印出 LOCKED 才回。回 { pid, proc }；結束一律依 PID
 */
function lockExclusive(file) {
  return new Promise((resolve, reject) => {
    const script = `$f=[IO.File]::Open('${file.replace(/'/gu, "''")}',[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None); [Console]::Out.WriteLine('LOCKED'); [Console]::Out.Flush(); [Threading.Thread]::Sleep(600000)`;
    const e = childEnv();
    delete e.PSModulePath;
    const proc = spawn('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], { windowsHide: true, env: e, stdio: ['ignore', 'pipe', 'pipe'] });
    let out = '';
    let err = '';
    const timer = setTimeout(() => { killTree(proc.pid); reject(new Error(`powershell 20 秒內沒有獨占 ${file}：${err.trim()}`)); }, 20000);
    proc.stdout.setEncoding('utf8');
    proc.stderr.setEncoding('utf8');
    proc.stderr.on('data', (d) => { err += d; });
    proc.stdout.on('data', (d) => {
      out += d;
      if (out.includes('LOCKED')) { clearTimeout(timer); resolve({ pid: proc.pid, proc }); }
    });
    proc.on('error', (x) => { clearTimeout(timer); reject(x); });
    proc.on('close', (code) => { clearTimeout(timer); if (!out.includes('LOCKED')) reject(new Error(`powershell 結束（${code}）：${err.trim()}`)); });
  });
}

/** 某台的遠端操作紀錄裡某種指令的筆數 */
async function auditCount(dev, cmd) {
  const r = await control(dev, 'remoteAudit', { limit: 500 }, 30000);
  return (r.rows ?? []).filter((x) => x.cmd === cmd).length;
}

export async function run(env) {
  const office = env.devices.get('OFFICE');
  const home = env.devices.get('HOME');
  const outcome = {};
  const phases = (process.env.MYRIAGENT_FF9_PHASES ?? ALL_PHASES.join(',')).split(',').map((s) => s.trim()).filter(Boolean);
  // 停機與舊版的時間窗：這段時間連不上的請求與 WebSocket、舊版沒有的端點（404）是預期的
  const windows = [];
  const inWindow = (at, kind) => windows.some((w) => w.kind === kind && at >= w.from && (w.to === null || at <= w.to + 3000));
  env.expected.push((m) => {
    const at = m.at ?? 0;
    const text = m.text ?? '';
    if (inWindow(at, 'down') && (/ERR_CONNECTION_REFUSED|ERR_CONNECTION_RESET|ERR_EMPTY_RESPONSE|ERR_CONNECTION_CLOSED|ERR_CONNECTION_ABORTED/u.test(text) || /WebSocket connection to .* failed/u.test(text))) return 'FF9：伺服器停機期間連不上（重連前的嘗試）';
    if (inWindow(at, 'old') && (m.status === 404 || /status of 404/u.test(text))) return 'FF9：舊版伺服器沒有這個端點';
    if (inWindow(at, 'restore') && (m.status === 401 || /status of 401/u.test(text))) return 'FF9：還原撤銷了所有瀏覽器工作階段，重新登入之前是 401';
    return null;
  });

  // 0. 準備：P 開著 HOME 的 Y 與 OFFICE 的一個對話；Y 的輸入區有沒送出的字
  const P0 = await openApp(env, 'P0');
  const list0 = P0.live ? await waitFor(async () => {
    const l = await ffList(P0);
    return l && l.filter((c) => c.deviceId === home.deviceId).length >= home.cfg.seed.count && l.filter((c) => c.deviceId === office.deviceId).length >= office.cfg.seed.count ? l : null;
  }, 30000, 300) : null;
  await Promise.race([P0.page.close().catch(() => {}), sleep(5000)]);
  if (!list0) throw new Error('清單沒有兩台的種子對話');
  const homeSeeds = list0.filter((c) => c.deviceId === home.deviceId).map((c) => c.id).sort();
  const officeSeeds = list0.filter((c) => c.deviceId === office.deviceId).map((c) => c.id).sort();
  const Y = homeSeeds[0];
  const C = homeSeeds[1];
  const P = await openApp(env, 'P', { layout: appLayout([{ tabs: [Y, officeSeeds[0]], active: Y }]) });
  const typed = P.live ? await waitFor(async () => {
    if ((await composerText(P, Y)) === null) return null;
    await P.page.fill(`.conv[data-conv="${Y}"] textarea.cinput`, UNSENT);
    return (await composerText(P, Y)) === UNSENT;
  }, 20000, 300) : null;
  env.step('FF9：P 開著 HOME 的 Y 與 OFFICE 的對話，Y 的輸入區有沒送出的字', !!typed,
    `P ${P.live ? '連上' : '沒連上'}；HOME ${homeSeeds.length} 個、OFFICE ${officeSeeds.length} 個種子對話；Y 的輸入區${typed ? '填好了' : '找不到或填不進去'}`, { Y, C, homeSeeds, officeSeeds });
  if (!typed) throw new Error('P 沒有準備好');
  const ctx = { office, home, P, Y, C, homeSeeds, officeSeeds, windows };

  // P 的字還在（換版、伺服器與裝置的重啟都沒有弄丟、P 沒有被重新載入）：在 (d) 之前查——還原會撤銷所有瀏覽器工作階段（RestoreService 第 4 點），P 一定回到登入頁
  let unsentChecked = false;
  const checkUnsent = async () => {
    unsentChecked = true;
    const text = await composerText(P, Y);
    outcome.unsentKept = text === UNSENT;
    env.step('FF9：P 的輸入區的字還在（換版與重啟都沒有弄丟、沒有被重新載入；還原之前）', text === UNSENT,
      `Y 的輸入區：${text === null ? '找不到' : text === UNSENT ? '還是原本的字' : `變成「${text.slice(0, 40)}」`}；P 重新載入 ${P.navigations - 1} 次`, { navigations: P.navigations });
  };

  for (const p of ALL_PHASES) {
    if (p === 'd' && !unsentChecked) await checkUnsent();
    if (!phases.includes(p)) {
      outcome[p] = 'skipped';
      env.step(`FF9 (${p})：未執行`, false, `MYRIAGENT_FF9_PHASES=${phases.join(',')} 沒有這一段（開發用；正式的驗收要五段都跑）`);
      continue;
    }
    const fn = { a: phaseA, b: phaseB, c: phaseC, d: phaseD, e: phaseE }[p];
    try {
      outcome[p] = await fn(env, ctx);
    } catch (e) {
      outcome[p] = { error: String(e?.message ?? e) };
      env.step(`FF9 (${p})：例外`, false, String(e?.message ?? e), { stack: String(e?.stack ?? e) });
      // 環境壞了（伺服器沒在跑）就不往下跑
      if (env.hub.exitCode !== null) break;
    }
  }

  // 最後：頁面乾淨（停機與舊版時間窗裡連不上、舊版沒有的端點、還原撤銷工作階段之後的 401 除外）
  if (!unsentChecked) await checkUnsent();
  outcome.page = await pageCleanStep(env, [P], 'FF9：頁面乾淨（停機、舊版、還原的時間窗裡連不上、404 與 401 以外的主控台錯誤、回應、CSP、對話框、XSS 計數）');
  return outcome;
}

// ───── (a) 強制結束 ─────

async function phaseA(env, ctx) {
  const { office, home, P, officeSeeds, windows } = ctx;
  const out = {};
  const a1 = await newConversation(home, 'ff9-a', 'FF9 強制結束');
  const renames0 = await auditCount(office, 'rename');
  const title0 = (await sqliteRo(env, ['conversation', officeSeeds[0]])).title;
  const inits0 = await initCount(P);
  // 新對話的項目要等 conv.created 確認之後才發布（publish_meta 的 createdAcked）：先清空，否則下行一暫停項目就停在發布者那裡
  await waitLiveDrained(home, 60000);

  // 1. 下行暫停：伺服器照收、照提交，確認送不回去 → 1,000 段的串流（每段一個完成的項目）積壓在 HOME 的 outbox
  env.proxy.hold('down');
  let backlog = null;
  let renameId = null;
  let held = null;
  try {
    await control(home, 'stream', { conversation: 'ff9-a', message: 'FF9 1000 段', segments: 1000, delayMs: 0, completeEachSegment: true, seconds: 120 }, 30000);
    // 項目由發布者分批寫進 outbox：等積壓到 1,000 筆（最多 20 秒；下行暫停超過 SignalR 的 30 秒逾時裝置會自己斷線）
    let last = null;
    backlog = await waitFor(async () => {
      const s = await deviceStatus(home, 5000);
      const done = (s?.streams ?? []).filter((x) => x.conversation === 'ff9-a').at(-1)?.done === true;
      last = s ? { undone: undone(s), outbox: s.outbox, streamDone: done } : last;
      return s && undone(s) >= 1000 ? last : null;
    }, 20000, 200) ?? last;
    // 非 durable 的指令：伺服器送出（到代理為止），確認送不回來
    renameId = await postCmd(P, { cmd: 'rename', conversationId: officeSeeds[0], title: 'FF9 強制結束前的改名' });
    await sleep(800);
    env.proxy.hold('up');
    await sleep(200);
    held = env.proxy.stats();
  } catch (e) {
    env.proxy.release('both');
    throw e;
  }
  env.step('FF9 (a)：下行暫停時 HOME 積壓 1,000 筆、瀏覽器送一個非 durable 的 rename，再暫停上行', (backlog?.undone ?? 0) >= 1000 && !!renameId,
    `HOME 的 outbox 還沒完成 ${backlog?.undone ?? '—'} 筆（${JSON.stringify(backlog?.outbox ?? {})}）；rename ${renameId ? '送出' : '沒送出'}；代理佇列上行 ${held?.queuedUp ?? '—'} 塊、下行 ${held?.queuedDown ?? '—'} 塊`,
    { backlog, renameId, held: held && { queuedUp: held.queuedUp, queuedDown: held.queuedDown } });
  out.backlog = (backlog?.undone ?? 0) >= 1000;

  // 2. 以 PID 強制結束 → 3 秒內同一個埠重啟 → 放開代理
  const win = { kind: 'down', from: Date.now(), to: null };
  windows.push(win);
  const k0 = Date.now();
  await env.hub.killHard();
  env.proxy.release('both');
  const spawnMs = Date.now() - k0;
  await env.hub.restart();
  const readyAt = Date.now();
  env.step('FF9 (a)：強制結束之後 3 秒內以同一個埠重啟', spawnMs <= 3000,
    `強制結束到重新啟動 ${spawnMs} ms（上限 3,000）、到 /healthz 200 ${readyAt - k0} ms；埠 ${env.hub.port}`, { spawnMs, readyMs: readyAt - k0 });
  out.restartWithin3s = spawnMs <= 3000;

  // 3. 30 秒內恢復：兩台 Live、瀏覽器收到新的 init
  const back = await waitFor(async () => {
    const live = await waitAllLive([office, home], 1000);
    const n = await initCount(P);
    return live && n !== null && n > inits0 ? { live, inits: n } : null;
  }, 30000, 300);
  win.to = Date.now();
  out.recovered = !!back;
  env.step('FF9 (a)：兩台與瀏覽器 30 秒內恢復', !!back,
    back ? `重啟後 ${win.to - readyAt} ms：OFFICE、HOME 都 Live、P 收到新的 init（第 ${back.inits} 則）` : '30 秒內沒有都恢復', { ms: back ? win.to - readyAt : null });

  // 4. 0 遺失 0 重複：清空之後伺服器的 deviceSeq 正好 1..N（N＝假裝置 outbox 的最大值）；HOME 的項目在伺服器上不重複
  const drained = { HOME: await waitLiveDrained(home, 120000), OFFICE: await waitLiveDrained(office, 60000) };
  const drainMs = Date.now() - readyAt;
  const seq = { HOME: await seqCheck(env, home, drained.HOME), OFFICE: await seqCheck(env, office, drained.OFFICE) };
  const ords = await control(home, 'ords', { conversation: a1 }, 60000);
  const server = await sqliteRo(env, ['conversation', a1]);
  const itemsOk = server.items === ords.count && ords.withoutPublishedOrd === 0;
  out.seq = { HOME: seq.HOME.ok, OFFICE: seq.OFFICE.ok, items: itemsOk };
  env.step('FF9 (a)：每台的 deviceSeq 正好 1..N（0 遺失 0 重複）、項目在伺服器上不重複', !!drained.HOME && !!drained.OFFICE && seq.HOME.ok && seq.OFFICE.ok && itemsOk,
    `清空 ${drained.HOME && drained.OFFICE ? `（重啟後 ${drainMs} ms）` : '沒有'}；HOME：伺服器 events ${seq.HOME.events}＋被拒 ${seq.HOME.rejected}、最大 ${seq.HOME.serverMax}／裝置 ${seq.HOME.deviceMax}、間隙 ${seq.HOME.gapCount}、重複 ${seq.HOME.duplicates}；`
      + `OFFICE：最大 ${seq.OFFICE.serverMax}／裝置 ${seq.OFFICE.deviceMax}、間隙 ${seq.OFFICE.gapCount}、重複 ${seq.OFFICE.duplicates}；對話 a1 伺服器 ${server.items} 個項目／HOME ${ords.count} 個（沒有 Ord 的 ${ords.withoutPublishedOrd}）`,
    { seq, drainMs, server: { items: server.items, itemCount: server.itemCount, maxOrd: server.maxOrd }, ords: { count: ords.count, withoutPublishedOrd: ords.withoutPublishedOrd } });

  // 5. 非 durable 的 rename 變 unknown、不重送（OFFICE 沒有執行、標題沒變）
  const cmd = renameId ? await waitFor(async () => {
    const r = await sqliteRo(env, ['command', renameId]);
    return r.rows.length > 0 && r.rows.every((x) => x.state !== 'pending' && x.state !== 'delivered') ? r : null;
  }, 15000, 300) : null;
  await sleep(3000);
  const renames1 = await auditCount(office, 'rename');
  const title1 = (await sqliteRo(env, ['conversation', officeSeeds[0]])).title;
  const unknownOk = !!cmd && cmd.rows.length === 1 && cmd.rows[0].state === 'unknown' && !cmd.rows[0].durable && renames1 === renames0 && title1 === title0;
  out.unknown = unknownOk;
  env.step('FF9 (a)：非 durable 的指令變 unknown、不重送（營運:576）', unknownOk,
    `rename：伺服器的列 ${cmd ? cmd.rows.map((x) => `${x.cmd} ${x.state}${x.durable ? '（durable）' : ''}`).join('、') : '15 秒內沒有結束'}；OFFICE 的遠端紀錄 rename ${renames0} → ${renames1} 筆；標題${title1 === title0 ? '沒變' : `變成「${title1}」`}`,
    { command: cmd, renames: [renames0, renames1] });

  // 6. 瀏覽器那一半（S8）：新開的分頁讀到的 a1 項目 id 與 Ord 等於 HOME 的（0 遺失 0 重複）
  const read = await readViaBrowser(env, 'R-a', a1);
  const same = sameItems(ords, read);
  out.browser = same.ok;
  env.step('FF9 (a)：瀏覽器那一半（S8）：讀到的項目 id 與 Ord 等於 HOME 的', same.ok,
    `原始連線 open＋loadOlder ${read.pages ?? '—'} 頁、${same.read} 個（HOME ${same.count} 個）：重複 ${same.dup}、少 ${same.missing}、多 ${same.extra}、Ord 不同 ${same.wrongOrd}${read.error ? `；${read.error}` : ''}`, same);
  return out;
}

// ───── (b) 回滾鏈 ─────

async function phaseB(env, ctx) {
  const builds = rollbackBuilds(process.env, env.prevRef ?? null);
  const out = {};
  for (const b of builds) {
    if (b.missing) {
      out[b.key] = 'not-run';
      env.step(`FF9 (b) ${b.key}：未執行`, false, `${b.label}（${b.commit ?? '—'}）：${b.missing}（計畫 D12：三個舊組建都要跑，否則整體不通過）`, { build: b });
      continue;
    }
    const exe = path.resolve(b.exe);
    if (!fs.existsSync(exe)) {
      out[b.key] = 'not-run';
      env.step(`FF9 (b) ${b.key}：未執行`, false, `${b.variable} 指的檔不存在：${exe}`, { build: b });
      continue;
    }
    out[b.key] = await rollbackRound(env, ctx, { ...b, exe });
  }
  return out;
}

/**
 * 一段回滾（rollbackRoundBody）；任何一步丟例外時先收掉還在跑的舊版、代理換回新版、新版沒在跑就開回來，再往上丟
 * （WP4.9 合併前審查：否則舊版的程序與它匯入的金鑰檔留在這台電腦上，後面的收尾也沒有伺服器可以 unpair；env.close() 另外也會收 startAltHub 開的）
 */
async function rollbackRound(env, ctx, build) {
  const guard = { old: null };
  try {
    return await rollbackRoundBody(env, ctx, build, guard);
  } catch (e) {
    if (guard.old && guard.old.exitCode === null) await guard.old.close().catch(() => null);
    env.proxy.setUpstream(env.hub.port);
    if (env.hub.exitCode !== null) await env.hub.restart().catch(() => {});
    throw e;
  }
}

async function rollbackRoundBody(env, ctx, build, guard) {
  const { office, home, P, Y, windows } = ctx;
  const tag = `FF9 (b) ${build.key}`;
  const out = {};
  const dll = path.join(path.dirname(build.exe), 'MyriagentHub.dll');
  const dllSha = fs.existsSync(dll) ? crypto.createHash('sha256').update(fs.readFileSync(dll)).digest('hex') : null;
  // 沒打字的分頁 Q：換版時自動重新載入（WP4.6 的規則；每輪新開一個，sessionStorage 的 60 秒限制各自算）
  const Q = await openApp(env, `Q-${build.key}`, { layout: appLayout([{ tabs: [Y] }]) });
  const navQ0 = Q.navigations;
  const navP0 = P.navigations;
  const initsP0 = await initCount(P);

  // 1. 切換前：上行暫停 → HOME 新開對話 X（conv.created 送出、伺服器收不到、沒確認）→ admin stop（切換那一刻有在途批次）
  env.proxy.hold('up');
  let X = null;
  let inflight = null;
  let atSwitch = null;
  try {
    X = await newConversation(home, `ff9-b-${build.key}`, `FF9 回滾到 ${build.key} 之前開的對話`);
    inflight = await waitFor(async () => {
      const s = await deviceStatus(home, 5000);
      return s && (s.outbox?.Sent ?? 0) > 0 ? s : null;
    }, 10000, 200);
    atSwitch = (await deviceStatus(home, 5000))?.outboxSeq?.max ?? null;
  } catch (e) {
    env.proxy.release('up');
    throw e;
  }
  const rejBefore = new Set((await sqliteRo(env, ['rejected-rows', home.deviceId])).rows.map((r) => r.deviceSeq));
  const down1 = { kind: 'down', from: Date.now(), to: null };
  windows.push(down1);
  const stopNew = await env.hub.stop();
  env.proxy.release('up');
  env.step(`${tag}：切換前 HOME 新開對話 X、它的 conv.created 在途（上行暫停）→ admin stop`, !!X && !!inflight && !stopNew.forced && stopNew.adminCode === 0,
    `X ${X ? '開好' : '沒開'}；HOME 的 outbox 在途 ${inflight?.outbox?.Sent ?? 0} 筆、最大 deviceSeq ${atSwitch ?? '—'}；admin stop 結束碼 ${stopNew.adminCode}${stopNew.forced ? '（逾時，已強制結束）' : ''}`,
    { X, atSwitch, outbox: inflight?.outbox ?? null, stop: stopNew });

  // 2. 舊版開同一個資料根，代理換上游（網址、PublicOrigin 都不變）
  const old = await startAltHub(env, build.exe);
  guard.old = old;
  env.proxy.setUpstream(old.port);
  const oldReadyAt = Date.now();
  down1.to = oldReadyAt;
  const oldWin = { kind: 'old', from: oldReadyAt, to: null };
  windows.push(oldWin);
  const health = await localGet(old.port, '/healthz').then((r) => JSON.parse(r.body)).catch(() => null);
  const liveOld = await waitAllLive([office, home], 60000);
  const features = liveOld ? Object.fromEntries(Object.entries(liveOld).map(([n, s]) => [n, s.features ?? []])) : null;
  // 瀏覽器：P（有沒送出的字）重連、顯示換版橫幅、不重新載入；Q（沒打字）自動重新載入
  const browserOld = await waitFor(async () => {
    const n = await initCount(P);
    const ns = await notices(P);
    const banner = ns.find((x) => x.kind === 'build' || x.text.includes(BUILD_NOTICE)) ?? null;
    const qReloaded = Q.navigations > navQ0;
    return n !== null && n > initsP0 && banner && qReloaded ? { inits: n, banner, qReloaded } : null;
  }, 30000, 300);
  const pNav = P.navigations - navP0;
  env.step(`${tag}：舊版（${build.commit}）開同一個資料根，兩台重連；P 重連、顯示換版橫幅、不重新載入；Q 自動重新載入`,
    !!health && !!liveOld && !!browserOld && pNav === 0,
    `舊版 /healthz build ${health?.build ?? '—'}（MyriagentHub.dll ${dllSha ? dllSha.slice(0, 12) : '—'}…）、啟動 ${oldReadyAt - down1.from} ms；`
      + `${liveOld ? `兩台重連（協商功能 OFFICE ${features.OFFICE.join('、') || '無'}、HOME ${features.HOME.join('、') || '無'}）` : '60 秒內沒有都重連'}；`
      + `P ${browserOld ? `收到舊版的 init、橫幅「${browserOld.banner.text}」` : '30 秒內沒有重連或沒有橫幅'}、重新載入 ${pNav} 次；Q ${Q.navigations > navQ0 ? '自動重新載入了' : '沒有重新載入'}`,
    { build: { key: build.key, commit: build.commit, exe: build.exe, dllSha256: dllSha, healthz: health }, features, banner: browserOld?.banner ?? null, pNav, qNav: Q.navigations - navQ0 });
  out.switchedToOld = !!health && !!liveOld;
  out.browserOld = !!browserOld && pNav === 0;

  // 3. 回滾期間：HOME 把 X 改成 metadata、在 Y 新增帶標記的項目；等清空
  const marker = `ff9-b-${build.key}-${rand()}`;
  const toMeta = await localCommand(home, 'syncSetConversationPolicy', { conversationId: X, policy: 'metadata' });
  const streamed = await control(home, 'stream', { conversation: Y, message: `回滾期間 ${marker}`, text: `回滾期間的回覆 ${marker}`, segments: 2, delayMs: 0, wait: true }, 90000);
  const drainedOld = await waitLiveDrained(home, 90000);
  const rejOld = (await sqliteRo(env, ['rejected-rows', home.deviceId])).rows.filter((r) => !rejBefore.has(r.deviceSeq));
  const k1 = rejOld.filter((r) => !r.empty);
  const rejOk = atSwitch !== null && k1.every((r) => r.deviceSeq <= atSwitch);
  env.step(`${tag}：回滾期間 X 改成 metadata、Y 新增項目；舊伺服器記下的被拒列只有 {} 或切換之前寫的（K1）`, !!drainedOld && streamed.outcome === 'Completed' && rejOk,
    `X 改 metadata（${toMeta.conversation?.syncPolicy ?? '—'}）、Y 串流 ${streamed.outcome}；HOME ${drainedOld ? '清空' : '90 秒內沒有清空'}；`
      + `舊伺服器新記下的被拒列 ${rejOld.length} 筆（{} ${rejOld.length - k1.length} 筆、凍結的 ${k1.length} 筆，deviceSeq ${k1.map((r) => r.deviceSeq).join('、') || '—'}；切換那一刻最大 ${atSwitch ?? '—'}）`,
    { rejected: rejOld.map((r) => ({ deviceSeq: r.deviceSeq, kind: r.kind, reason: r.reason, empty: r.empty })), atSwitch });
  out.rejectedOnlyEmptyOrK1 = rejOk;

  // 4. 停舊版（不留金鑰檔）→ 新版開同一個資料根
  const down2 = { kind: 'down', from: Date.now(), to: null };
  windows.push(down2);
  const inits1 = await initCount(P);
  const stopOld = await old.close();
  oldWin.to = Date.now();
  const oldKeys = keyFilesNaming(old.needle);
  env.proxy.setUpstream(env.hub.port);
  await env.hub.restart();
  const newReadyAt = Date.now();
  down2.to = newReadyAt;
  env.step(`${tag}：停舊版（admin stop、不留金鑰檔）→ 新版開同一個資料根`, !stopOld.forced && stopOld.adminCode === 0 && oldKeys.length === 0 && stopOld.keysLeft.length === 0,
    `舊版 admin stop 結束碼 ${stopOld.adminCode}${stopOld.forced ? '（逾時，已強制結束）' : ''}、舊版的金鑰檔還在 ${oldKeys.length} 個；新版 ${newReadyAt - down2.from} ms 後起來`, { stopOld, oldKeys });
  out.oldStoppedClean = !stopOld.forced && stopOld.adminCode === 0 && oldKeys.length === 0;

  // 5. 收斂：兩台回到 conv1、清空；X 在伺服器上是 metadata、項目清空；Y 的項目 id 與 Ord 等於 HOME 的；P 重連
  const liveNew = await waitAllLive([office, home], 60000);
  const drainedNew = await waitLiveDrained(home, 90000);
  await waitLiveDrained(office, 60000);
  const backP = await waitFor(async () => {
    const n = await initCount(P);
    return n !== null && n > inits1 ? n : null;
  }, 30000, 300);
  const xServer = await waitFor(async () => {
    const c = await sqliteRo(env, ['conversation', X]);
    return c.exists && c.policy === 'metadata' && c.items === 0 ? c : null;
  }, 30000, 500);
  const yOrds = await control(home, 'ords', { conversation: Y }, 60000);
  const yRead = await readViaBrowser(env, `R-${build.key}`, Y);
  const ySame = sameItems(yOrds, yRead);
  const markerOnServer = scanDb(env.hub.dataDir, [marker]).hits[marker].length > 0;
  env.step(`${tag}：升級回來之後收斂：X 是 metadata、項目清空（先套轉換再補 created）；Y 的項目 id 與 Ord 等於 HOME 的、標記在伺服器上；P 重連`,
    !!liveNew && !!drainedNew && !!xServer && ySame.ok && markerOnServer && !!backP,
    `${liveNew ? '兩台 Live' : '60 秒內沒有都 Live'}（HOME 協商 ${(liveNew?.HOME?.features ?? []).join('、') || '—'}）、HOME ${drainedNew ? '清空' : '沒清空'}；`
      + `X：${xServer ? `伺服器 ${xServer.policy}、${xServer.items} 個項目` : '30 秒內沒有變成 metadata 且項目清空'}；`
      + `Y：瀏覽器讀到 ${ySame.read} 個（HOME ${ySame.count} 個）重複 ${ySame.dup}、少 ${ySame.missing}、多 ${ySame.extra}、Ord 不同 ${ySame.wrongOrd}；標記${markerOnServer ? '在' : '不在'}伺服器上；P ${backP ? '收到新版的 init' : '30 秒內沒有重連'}`,
    { x: xServer, y: ySame });
  out.converged = !!liveNew && !!drainedNew && !!xServer && ySame.ok && markerOnServer;
  out.browserBack = !!backP;

  // 6. X 改回 full：整份重送（摘要在轉換時清掉了，LIVE-1）
  await localCommand(home, 'syncSetConversationPolicy', { conversationId: X, policy: 'full' });
  const xOrds = await control(home, 'ords', { conversation: X }, 60000);
  const xFull = await waitFor(async () => {
    const c = await sqliteRo(env, ['conversation', X]);
    return c.exists && c.policy === 'full' && c.items === xOrds.count && xOrds.count > 0 ? c : null;
  }, 60000, 500);
  env.step(`${tag}：X 改回 full 之後整份重送（LIVE-1：摘要跟著轉換清掉）`, !!xFull,
    xFull ? `伺服器 ${xFull.policy}、${xFull.items} 個項目（HOME ${xOrds.count} 個）` : `60 秒內伺服器沒有變成 full 且 ${xOrds.count} 個項目`, { x: xFull });
  out.republished = !!xFull;

  // 7. 升級回來之後的清單等於伺服器的對話（新開的分頁 R 與 P）
  const serverList = await sqliteRo(env, ['conversations']);
  const want = serverList.conversations.map((c) => `${c.conversationId}|${c.title}`).sort();
  const R = await openApp(env, `L-${build.key}`);
  const listOf = async (app) => ((await ffList(app)) ?? []).map((c) => `${c.id}|${c.title}`).sort();
  const listR = R.live ? await waitFor(async () => {
    const l = await listOf(R);
    return JSON.stringify(l) === JSON.stringify(want) ? l : null;
  }, 20000, 300) : null;
  const listP = await waitFor(async () => {
    const l = await listOf(P);
    return JSON.stringify(l) === JSON.stringify(want) ? l : null;
  }, 20000, 300);
  const lastR = listR ?? await listOf(R);
  await Promise.race([R.page.close().catch(() => {}), sleep(5000)]);
  await Promise.race([Q.page.close().catch(() => {}), sleep(5000)]);
  env.step(`${tag}：升級回來之後清單等於伺服器的對話（新開的分頁與 P）`, !!listR && !!listP,
    `伺服器 ${want.length} 個對話；新開的分頁 ${listR ? '相同' : `不同（${lastR.length} 個）`}、P ${listP ? '相同' : '不同'}`, { want: want.length, R: lastR.length });
  out.listMatches = !!listR && !!listP;
  return out;
}

// ───── (c) PRIV-R3-1/2：meta.json 被占用，中樞這次沒有那個對話 ─────

async function phaseC(env, ctx) {
  const { home, C } = ctx;
  const out = {};
  const marker = `ff9-c-${rand()}`;
  const rej0 = new Set((await sqliteRo(env, ['rejected-rows', home.deviceId])).rows.map((r) => r.deviceSeq));
  // 1. 暫停同步，在 C 寫帶標記的項目（待送列留在 outbox）
  await localCommand(home, 'syncPause', { paused: true });
  let locker = null;
  let paused = true;
  try {
    const streamed = await control(home, 'stream', { conversation: C, message: `PRIV-R3 ${marker}`, text: `占用期間的回覆 ${marker}`, segments: 2, delayMs: 0, wait: true }, 90000);
    const pending = undone(await deviceStatus(home));
    // 2. 以 PID 結束 HOME（待送列留著）→ 獨占 C 的 meta.json → 重啟 HOME：中樞這次沒有 C
    const metaFile = findMeta(home.data, C);
    if (!metaFile) throw new Error(`找不到 C（${C}）的 meta.json`);
    // 暫停中的那台不會 Live：--timeout 給短一點（劇本開始前等 Live 用它；之後的 quit 等清空時已經清空）
    const r1 = await restartDevice(env, 'HOME', { hard: true, drain: false, timeoutSeconds: 10, beforeStart: async () => { locker = await lockExclusive(metaFile); } });
    const missing = await home.run.control('ords', { conversation: C }, 30000);
    env.step('FF9 (c)：暫停時 C 寫了帶標記的項目 → 結束 HOME、獨占 C 的 meta.json → 重啟：中樞沒有 C', streamed.outcome === 'Completed' && pending > 0 && r1.ready && !missing.ok,
      `串流 ${streamed.outcome}、待送 ${pending} 筆；powershell（PID ${locker?.pid ?? '—'}）獨占 ${path.basename(path.dirname(metaFile))}\\meta.json；HOME 重啟${r1.ready ? '' : '失敗'}（${r1.ms} ms）；`
        + `ords(C) ${missing.ok ? '找得到（中樞有 C）' : `找不到（${missing.error}）`}`, { pending, restart: { ready: r1.ready, ms: r1.ms, stopped: r1.stopped?.exit ?? null } });
    out.hubLacksC = !missing.ok;
    // 3. 解除暫停：閘門把 C 的待送列換成 {}、清掉摘要；伺服器沒有標記
    await localCommand(home, 'syncPause', { paused: false });
    paused = false;
    const drained = await waitLiveDrained(home, 90000);
    const rejC = (await sqliteRo(env, ['rejected-rows', home.deviceId])).rows.filter((r) => !rej0.has(r.deviceSeq) && r.conversationId === C);
    const scan = scanDb(env.hub.dataDir, [marker]);
    const gateOk = !!drained && rejC.length > 0 && rejC.every((r) => r.empty) && scan.hits[marker].length === 0;
    env.step('FF9 (c)：閘門把 C 的待送列換成 {}、伺服器上沒有標記（PRIV-R3-1/2）', gateOk,
      `HOME ${drained ? '清空' : '90 秒內沒有清空'}；C 新記下的被拒列 ${rejC.length} 筆（{} ${rejC.filter((r) => r.empty).length} 筆）；db\\ ${scan.files} 個檔${scan.hits[marker].length ? `有標記：${scan.hits[marker].join('、')}` : '都沒有標記'}`,
      { rejected: rejC.map((r) => ({ kind: r.kind, reason: r.reason, empty: r.empty })) });
    out.gate = gateOk;
  } finally {
    if (paused) await localCommand(home, 'syncPause', { paused: false }).catch(() => null);
    if (locker) {
      await killTree(locker.pid);
    }
  }
  // 4. 放開（依 PID 結束 powershell）→ 重啟 HOME：C 回到中樞，照摘要重送
  const r2 = await restartDevice(env, 'HOME', { drain: true });
  const drained2 = await waitLiveDrained(home, 90000);
  const ords = await control(home, 'ords', { conversation: C }, 60000);
  const read = await readViaBrowser(env, 'R-c', C);
  const same = sameItems(ords, read);
  const back = await waitFor(async () => (scanDb(env.hub.dataDir, [marker]).hits[marker].length > 0 ? true : null), 30000, 500);
  env.step('FF9 (c)：放開之後重啟：C 回到中樞、照摘要重送，id 與 Ord 收斂、標記在伺服器上', r2.ready && !!drained2 && same.ok && !!back,
    `HOME 重啟${r2.ready ? '' : '失敗'}、${drained2 ? '清空' : '沒清空'}；瀏覽器讀到 C ${same.read} 個（HOME ${same.count} 個）重複 ${same.dup}、少 ${same.missing}、多 ${same.extra}、Ord 不同 ${same.wrongOrd}；標記${back ? '在' : '不在'}伺服器上`, same);
  out.converged = r2.ready && !!drained2 && same.ok && !!back;
  return out;
}

// ───── (d) 備份還原 ─────

function findKey(obj, key) {
  if (!obj || typeof obj !== 'object') return null;
  for (const [k, v] of Object.entries(obj)) {
    if (k.toLowerCase() === key) return v;
    const inner = findKey(v, key);
    if (inner !== null) return inner;
  }
  return null;
}

async function phaseD(env, ctx) {
  const { office, home, P, windows } = ctx;
  const out = {};
  await waitLiveDrained(home, 60000);
  await waitLiveDrained(office, 60000);
  const before = await sqliteRo(env, ['conversations']);
  const backup = await adminCommandAsync({ exe: env.hub.exe, dataDir: env.hub.dataDir, args: ['backup-now', '--wait', '60'], timeoutMs: 90000, env: env.internal.childEnv });
  let parsed = null;
  try { parsed = JSON.parse(backup.stdout); } catch { /* 不是 JSON */ }
  const folder = findKey(parsed, 'folder');
  const down = { kind: 'down', from: Date.now(), to: null };
  // 還原撤銷所有瀏覽器工作階段：P 之後的 401 到重新登入、回到 /app/ 為止是預期的
  const revokedWin = { kind: 'restore', from: Date.now(), to: null };
  windows.push(down, revokedWin);
  const stop = await env.hub.stop();
  const restore = folder ? await adminCommandAsync({ exe: env.hub.exe, dataDir: env.hub.dataDir, args: ['restore', '--from', folder], timeoutMs: 120000, env: env.internal.childEnv }) : null;
  let restoreJson = null;
  try { restoreJson = restore ? JSON.parse(restore.stdout) : null; } catch { /* 不是 JSON */ }
  const revoked = findKey(restoreJson, 'revokedsessions');
  const leases0 = await sqliteRo(env, ['leases']);
  const allRecovering = leases0.total > 0 && (leases0.counts.recovering ?? 0) === leases0.total;
  env.step('FF9 (d)：admin backup-now → admin stop → admin restore：還原後每個對話都是 recovering', backup.code === 0 && !!folder && !stop.forced && restore?.code === 0 && allRecovering,
    `backup-now 結束碼 ${backup.code}（${folder ? path.basename(folder) : '沒有資料夾'}）；admin stop ${stop.adminCode}${stop.forced ? '（強制）' : ''}；restore 結束碼 ${restore?.code ?? '—'}；`
      + `還沒刪除的對話 ${leases0.total} 個、recovering ${leases0.counts.recovering ?? 0} 個`, { folder, restore: restore ? { code: restore.code, stderr: restore.stderr.slice(-500) } : null, leases: leases0.counts });
  out.restored = backup.code === 0 && !!folder && restore?.code === 0 && allRecovering;
  const t0 = Date.now();
  await env.hub.restart();
  down.to = Date.now();
  // 擁有者重新上傳（新的 conv.created 解除 recovering）
  const cleared = await waitFor(async () => {
    const l = await sqliteRo(env, ['leases']);
    return (l.counts.recovering ?? 0) === 0 ? l : null;
  }, 90000, 500);
  const after = await sqliteRo(env, ['conversations']);
  const key = (c) => `${c.conversationId}|${c.policy}|${c.itemCount}`;
  const same = JSON.stringify(before.conversations.map(key)) === JSON.stringify(after.conversations.map(key));

  // 還原撤銷了 P 的工作階段：P 自己回到登入頁（/api/v1/auth/me 401）→ 同一個瀏覽器內容重新登入 → P 回到 /app/、收到 init
  const navs0 = P.navigations;
  const loggedOut = await waitFor(async () => {
    const st = await P.page.evaluate(async () => (await fetch('/api/v1/auth/me', { credentials: 'same-origin', cache: 'no-store' })).status).catch(() => null);
    return st === 401 ? st : null;
  }, 30000, 500);
  const relogin = await login(env, env.context, 'L-d');
  await P.page.goto(`${env.base}/app/`, { waitUntil: 'load' }).catch(() => null);
  const back = await waitFor(async () => ((await initCount(P)) > 0 ? true : null), 30000, 300);
  revokedWin.to = Date.now();
  env.step('FF9 (d)：還原撤銷所有瀏覽器工作階段：P 回到登入頁、重新登入之後回到 /app/', Number(revoked) >= 1 && !!loggedOut && relogin.ok && !!back,
    `restore 撤銷 ${revoked ?? '—'} 個工作階段；P 的 /api/v1/auth/me ${loggedOut ? '401' : '30 秒內不是 401'}（P 自己導覽 ${P.navigations - navs0} 次）；重新登入${relogin.ok ? '成功' : '失敗'}；P 回到 /app/ ${back ? '收到 init' : '30 秒內沒有 init'}`,
    { revoked });
  out.sessionsRevoked = Number(revoked) >= 1 && !!loggedOut && relogin.ok && !!back;
  const ops = await opsStatus(P).catch(() => null);
  env.step('FF9 (d)：重新上傳之後解除 recovering，對話與項目數等於還原前', !!cleared && same,
    `${cleared ? `啟動後 ${Date.now() - t0} ms 內都解除` : '90 秒內沒有都解除'}；對話 ${after.count} 個（還原前 ${before.count} 個）、政策與項目數${same ? '都相同' : '不同'}；ops/status 的 recovering ${ops?.json?.conversations?.recovering ?? '—'}`,
    { before: before.conversations.map(key), after: after.conversations.map(key) });
  out.recovered = !!cleared && same;
  return out;
}

// ───── (e) PRIV-R3-3：封鎖中的對話降級 ─────

async function phaseE(env, ctx) {
  const { office } = ctx;
  const out = {};
  // 1. OFFICE2 照抄 OFFICE 的種子：同 id 的對話在伺服器上是 OFFICE 的 → not_owner 封鎖
  const twin = await addDevice(env, { name: 'OFFICE2', sameSeedAs: 'OFFICE' });
  if (!twin.ok) throw new Error('OFFICE2 沒有準備好');
  const twinDb = path.join(twin.data, 'sync', 'sync.db');
  const blocked = await waitFor(async () => {
    const r = await sqliteRoAt(twinDb, ['publish-blocks']).catch(() => null);
    const notOwner = (r?.blocks ?? []).filter((b) => /not_owner/u.test(b.blocked ?? ''));
    return notOwner.length >= office.cfg.seed.count ? notOwner : null;
  }, 60000, 500);
  env.step('FF9 (e)：OFFICE2 照抄 OFFICE 的種子配對：同 id 的對話 not_owner 封鎖', !!blocked,
    blocked ? `封鎖 ${blocked.length} 個（${[...new Set(blocked.map((b) => b.blocked))].join('、')}）` : `60 秒內沒有 ${office.cfg.seed.count} 個 not_owner 的封鎖`, { blocks: blocked });
  if (!blocked) return { blocked: false };
  out.blocked = true;
  const B = blocked[0].conversationId;
  // 2. OFFICE2 把 B 改成 localOnly，之後寫一段標記；OFFICE 另寫一段（正向對照：同一個掃描找得到）
  const marker = `ff9-e-${rand()}`;
  const control2 = `ff9-e-ctl-${rand()}`;
  const toLocal = await localCommand(twin, 'syncSetConversationPolicy', { conversationId: B, policy: 'localOnly' });
  const streamed = await control(twin, 'stream', { conversation: B, message: `降級之後 ${marker}`, text: `降級之後的回覆 ${marker}`, segments: 2, delayMs: 0, wait: true }, 90000);
  await control(office, 'stream', { conversation: 'ff9-e-ctl', account: office.cfg.alias, message: `正向對照 ${control2}`, text: `正向對照的回覆 ${control2}`, segments: 1, delayMs: 0, wait: true }, 90000);
  await waitLiveDrained(twin, 60000);
  await waitLiveDrained(office, 60000);
  const seen = await waitFor(async () => (scanDb(env.hub.dataDir, [control2]).hits[control2].length > 0 ? true : null), 30000, 500);
  const scan = scanDb(env.hub.dataDir, [marker, control2]);
  const owner = (await sqliteRo(env, ['conversation', B])).owner;
  const noLeak = scan.hits[marker].length === 0 && !!seen && owner === office.deviceId;
  env.step('FF9 (e)：封鎖中的 B 改成 localOnly 之後寫的標記不在伺服器的 db\\*（含 -wal；PRIV-R3-3）', streamed.outcome === 'Completed' && noLeak,
    `B 改 localOnly（${toLocal.conversation?.syncPolicy ?? '—'}）、串流 ${streamed.outcome}；db\\ ${scan.files} 個檔：標記${scan.hits[marker].length ? `在 ${scan.hits[marker].join('、')}` : '都沒有'}、正向對照${seen ? `在 ${scan.hits[control2].join('、')}` : '找不到'}；`
      + `伺服器上 B 的擁有者${owner === office.deviceId ? '照舊是 OFFICE' : `是 ${owner}`}`, { files: scan.files, controlHits: scan.hits[control2] });
  out.noLeak = noLeak;
  // 3. 以 --app-version 換版重開 OFFICE2：not_owner 封鎖不受換版影響（程式錯誤的封鎖才在換版時解除）
  const r = await restartDevice(env, 'OFFICE2', { appVersion: '9.9.9-ff9' });
  await waitLiveDrained(twin, 60000);
  await sleep(2000);
  const blocks2 = await sqliteRoAt(twinDb, ['publish-blocks']);
  const still = blocks2.blocks.filter((b) => /not_owner/u.test(b.blocked ?? ''));
  const scan2 = scanDb(env.hub.dataDir, [marker]);
  const kept = r.ready && still.some((b) => b.conversationId === B) && still.length >= blocked.length && scan2.hits[marker].length === 0;
  env.step('FF9 (e)：以 --app-version 換版重開 OFFICE2：not_owner 封鎖照舊、標記照樣不在伺服器上', kept,
    `重開${r.ready ? '' : '失敗'}（${r.ms} ms）；not_owner 封鎖 ${still.length} 個（換版前 ${blocked.length} 個）、B ${still.some((b) => b.conversationId === B) ? '還在封鎖' : '解除了'}；標記${scan2.hits[marker].length ? '出現了' : '照樣不在'}`,
    { blocks: still });
  out.blockKept = kept;
  return out;
}
