// FF6 媒體與檔案（多裝置、離線、配額；WP4.9 步 16，計畫塊 13）：OFFICE 一個對話，網頁送出一則，那一輪回一張 PNG、一段「看起來像 MP4」的影片
// 與一則超過 128 KB 的回覆（夾具在工作資料夾的 media\，不在任何資料根裡）。伺服器以 Development 的參數把 blob 配額調低（Hub:Media:QuotaBytes）。
// 斷言：/m/ 的圖片 200、內容等於原檔（SHA-256）、CSP 是 MediaCsp；影片 Range 206（Content-Range 與內容對得上）與超出檔尾的 416；/t/ 回 PNG 縮圖
// （電腦上傳的縮圖 blob，E8）；超過 128 KB 的項目走 blob（blob_refs 的 item）、網頁顯示得出來（結尾還在）；OFFICE 離線（暫停同步）後 /m/ 的圖片回縮圖
// （X-Myriagent-Fallback: thumb、no-store）、影片回 503、對話照樣讀得到（P47:1265）；範圍外的路徑（listFiles、fileFetch）被拒（policy）並寫進 OFFICE 的
// 遠端紀錄；上傳補到告警門檻 → blob_quota；再傳會超過拒收門檻 → 507；以更低的配額重啟同一個資料根 → blob_refuse、任何上傳 507，事件與對話照收
// （P47:1266、:1268）。上傳的 SHA-256 與 SEC-23 由 remote-control.mjs 第 11 段守。
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { appLayout, openApp, waitFor } from '../lib/harness.mjs';
import { countBy } from '../lib/flow-rules.mjs';
import { MEDIA_CSP, mp4Bytes, parseContentRange, pngImage, quotaPlan, statusOf } from '../lib/media-rules.mjs';
import {
  ffSnapshot, ffWaitResult, flowRecorder, opsStatus, pageGet, pageUpload, rawClose, rawCmd, rawConnect, rawMark, rawWait, trackHubFrames,
} from '../lib/flow-page.mjs';
import { tstatus, waitLive } from '../../ui-probe/hub-page.mjs';
import { control, deviceStatus, localCommand, newConversation, pageCleanStep, serverConversation, waitSnapshots } from './common.mjs';

/** 調低的 blob 配額（16 MiB）與磁碟下限（1 MiB：配額的判斷不受開發機的可用空間影響） */
const QUOTA = 16 * 1024 * 1024;
const MIN_FREE = 1024 * 1024;
const mediaArgs = (quota) => [`--Hub:Media:QuotaBytes=${quota}`, `--Hub:Media:MinFreeBytes=${MIN_FREE}`];

/** 影片夾具的大小 */
const VIDEO_BYTES = 1024 * 1024;

/** 長回覆的行數（每行約 90 位元組 UTF-8：合計約 220 KB，超過 128 KB；行裡沒有 16 個以上連續的英數字，DLP 不會擋） */
const LONG_LINES = 2400;

/** 伺服器以佔位顯示的項目（HubItemRules.TooLargeMessage、UnavailableMessage；逐字比對） */
const PLACEHOLDERS = ['內容太大，暫時無法在瀏覽器顯示', '這一筆內容暫時無法在瀏覽器顯示'];

const convSel = (id) => '.conv[data-conv="' + id + '"]';
const taSel = (id) => convSel(id) + ' textarea.cinput';

/** startEnv 的選項：預設的兩台；頁面記錄器；調低的 blob 配額 */
export const env = {
  initOpts: { lightMessages: true },
  initScripts: [{ fn: flowRecorder }],
  hubArgs: mediaArgs(QUOTA),
};

const sha256 = (buf) => crypto.createHash('sha256').update(buf).digest('hex');

/** 長回覆：LONG_LINES 行，最後一行是 tail */
function longReply(tail) {
  const lines = [];
  for (let i = 1; i <= LONG_LINES; i++) lines.push(`第 ${String(i).padStart(4, '0')} 行：媒體與檔案的長回覆，用來確認超過上限的欄位會換成 blob。`);
  lines.push(tail);
  return lines.join('\n');
}

/** 在輸入框打字、按 Enter（真的鍵盤）；回這次送出的 Cmd 框 */
async function typeSend(app, conv, text) {
  await app.page.bringToFront();
  const from = app.frames.length;
  await app.page.fill(taSel(conv), text);
  await app.page.press(taSel(conv), 'Enter');
  return waitFor(() => app.frames.slice(from).find((f) => f.kind === 'cmd' && f.cmd === 'send' && f.conv === conv) ?? null, 5000, 50);
}

/** 原始連線 open 一個對話、拿完整的項目（只有第一頁；這個對話不到一頁）之後 close：項目陣列，沒收到是 null */
async function openItems(app, conv) {
  const mark = await rawMark(app);
  await rawCmd(app, { cmd: 'open', conversationId: conv });
  const snap = await rawWait(app, { type: 'conversation', conversationId: conv }, { from: mark, ms: 15000 });
  await rawCmd(app, { cmd: 'close', conversationId: conv });
  return snap ? snap.m.items ?? [] : null;
}

/** 項目裡的媒體（網頁版的 media[]：url 是 /m/{key}） */
const mediaOf = (items) => (items ?? []).flatMap((i) => (Array.isArray(i.media) ? i.media.map((m) => ({ item: i.id, ...m })) : []));

/** 項目看得到的文字（html 與 output） */
const textOf = (i) => `${i?.html ?? ''}\n${i?.output ?? ''}`;

/** 原始連線送一個指令、等它的 commandResult：{ commandId, state, code } */
async function rawResult(app, cmd) {
  const sent = await rawCmd(app, cmd);
  const r = sent.invocationId ? await rawWait(app, { type: 'commandResult', commandId: sent.commandId }, { ms: 20000 }) : null;
  return { commandId: sent.commandId, state: r?.m.state ?? null, code: r?.m.code ?? null };
}

/** ops/status 的 blobs 與告警（只取比對要用的） */
async function quotaState(app) {
  const s = await opsStatus(app);
  const b = s.json?.blobs ?? null;
  return { status: s.status, bytes: b?.bytes ?? null, quotaBytes: b?.quotaBytes ?? null, level: b?.level ?? null, count: b?.count ?? null,
    alerts: (s.json?.alerts ?? []).filter((a) => a === 'blob_quota' || a === 'blob_refuse') };
}

export async function run(env) {
  const office = env.devices.get('OFFICE');
  const outcome = {};
  const nonce = crypto.randomBytes(4).toString('hex');
  const tail = `長回覆的最後一行 ${nonce}`;

  // 預期的錯誤：故意的 416、到配額的 507；OFFICE 離線期間的媒體（影片 503）；重啟伺服器的停機期間（連線失敗、重連）
  const windows = { offline: null, downtime: null };
  const inWindow = (w, at) => !!w && typeof at === 'number' && at >= w[0] && at <= (w[1] ?? Number.POSITIVE_INFINITY);
  env.expected.push((m) => {
    const status = statusOf(m);
    const p = m.path ?? '';
    if (p.startsWith('/m/') && status === 416) return 'FF6：故意要超出檔尾的 Range（416）';
    if (p.startsWith('/api/v1/uploads/') && status === 507) return 'FF6：blob 用量會到拒收門檻的上傳（507）';
    if ((p.startsWith('/m/') || p.startsWith('/t/')) && inWindow(windows.offline, m.at)) return 'FF6：OFFICE 離線期間的媒體（影片 503）';
    if (inWindow(windows.downtime, m.at)) return 'FF6：以較低的配額重啟伺服器的停機期間（連線失敗、重連）';
    return null;
  });

  // 1. 夾具（工作資料夾的 media\，不在任何資料根裡）與 OFFICE 的對話；下一輪：圖片、影片、長回覆
  const mediaDir = path.join(env.work, 'media');
  fs.mkdirSync(mediaDir, { recursive: true });
  const png = pngImage(800, 600, 20261022);
  const mp4 = mp4Bytes(VIDEO_BYTES, 20261023);
  const pngPath = path.join(mediaDir, 'ff6-photo.png');
  const mp4Path = path.join(mediaDir, 'ff6-clip.mp4');
  fs.writeFileSync(pngPath, png);
  fs.writeFileSync(mp4Path, mp4);
  const conv = await newConversation(office, 'ff6', 'FF6 媒體與檔案');
  await control(office, 'nextRemoteTurn', { turn: { steps: [{ media: pngPath }, { media: mp4Path }, { text: longReply(tail) }], outcome: 'completed' } });
  const P = await openApp(env, 'P', { layout: appLayout([{ tabs: [conv] }]), beforeGoto: trackHubFrames });
  const ready = P.live && (await waitSnapshots(P, [conv]));
  const raw = await rawConnect(P);
  const before = { remote: ((await control(office, 'remoteAudit', { limit: 1 })).rows ?? [])[0]?.seq ?? 0, quota: await quotaState(P) };
  env.step('FF6：頁面開著 OFFICE 的對話；伺服器的 blob 配額調低', !!ready && raw.ok && before.quota.quotaBytes === QUOTA,
    `P ${P.live ? '連上' : '沒連上'}、快照${ready ? '收到' : '沒收到'}、原始連線${raw.ok ? '連上' : `沒連上：${raw.error}`}；夾具 PNG ${png.length} 位元組、影片 ${mp4.length} 位元組；`
      + `ops/status 的 blobs：配額 ${before.quota.quotaBytes ?? '—'}（Hub:Media:QuotaBytes=${QUOTA}）、用量 ${before.quota.bytes ?? '—'}、等級 ${before.quota.level ?? '—'}`,
    { conv, quota: before.quota, png: { bytes: png.length, sha256: sha256(png) }, mp4: { bytes: mp4.length, sha256: sha256(mp4) } });
  if (!ready || !raw.ok) throw new Error('頁面沒有開著 OFFICE 的對話');

  // 2. 網頁送出一則（真的鍵盤）→ 那一輪跑完：圖片、影片、長回覆的項目都到了
  const send = await typeSend(P, conv, 'FF6 給我圖片、影片與一段很長的回覆');
  const sendRes = send ? await ffWaitResult(P, send.commandId, 20000) : null;
  const items = await waitFor(async () => {
    const list = await openItems(P, conv);
    const media = mediaOf(list);
    const long = (list ?? []).find((i) => textOf(i).includes(tail));
    return media.some((m) => m.kind === 'image') && media.some((m) => m.kind === 'video') && long ? list : null;
  }, 60000, 1000);
  const media = mediaOf(items);
  const image = media.find((m) => m.kind === 'image') ?? null;
  const video = media.find((m) => m.kind === 'video') ?? null;
  const longItem = (items ?? []).find((i) => textOf(i).includes(tail)) ?? null;
  env.step('FF6：網頁送出一則，那一輪的圖片、影片與長回覆都到了', sendRes?.state === 'done' && !!image && !!video && !!longItem,
    `send ${send ? sendRes?.state ?? '沒有結果' : '沒送'}；項目 ${items?.length ?? 0} 則：圖片 ${image?.url ?? '—'}、影片 ${video?.url ?? '—'}、長回覆 ${longItem ? longItem.id : '沒到'}`,
    { media, longItem: longItem && { id: longItem.id, kind: longItem.kind, htmlBytes: Buffer.byteLength(longItem.html ?? '') } });
  if (!image || !video || !longItem) throw new Error('媒體或長回覆沒有到');

  // 3. /m/ 圖片 200（內容等於原檔、MediaCsp）；影片 Range 206 與 416；/t/ 縮圖（等電腦上傳的縮圖 blob，E8）
  const imageGet = await pageGet(P, image.url);
  const range = await pageGet(P, video.url, { Range: 'bytes=0-1023' });
  const rangeCr = parseContentRange(range.headers['content-range']);
  const beyond = await pageGet(P, video.url, { Range: `bytes=${VIDEO_BYTES + 100}-${VIDEO_BYTES + 200}` });
  const beyondCr = parseContentRange(beyond.headers['content-range']);
  const thumbKey = image.url.slice('/m/'.length);
  // 電腦上傳的縮圖（media_refs.thumb_blob；只有圖片有，影片產不出縮圖）
  const thumbed = await waitFor(async () => {
    const c = await serverConversation(env, conv);
    return (c?.media ?? []).reduce((n, x) => n + x.thumbs, 0) >= 1 ? c : null;
  }, 30000, 500);
  const thumb = await pageGet(P, `/t/${thumbKey}?s=320`);
  const imageOk = imageGet.status === 200 && imageGet.sha256 === sha256(png) && imageGet.headers['content-type'] === 'image/png' && imageGet.headers['content-security-policy'] === MEDIA_CSP;
  const rangeOk = range.status === 206 && range.size === 1024 && range.sha256 === sha256(mp4.subarray(0, 1024))
    && rangeCr?.start === 0 && rangeCr.end === 1023 && rangeCr.total === VIDEO_BYTES;
  const beyondOk = beyond.status === 416 && beyondCr?.unsatisfied === true && beyondCr.total === VIDEO_BYTES;
  const thumbOk = thumb.status === 200 && thumb.head?.startsWith('89504e47') && thumb.headers['content-type'] === 'image/png' && !!thumbed;
  outcome.media = { image: imageGet.status, imageSame: imageGet.sha256 === sha256(png), csp: imageGet.headers['content-security-policy'] === MEDIA_CSP,
    range: range.status, rangeSame: range.sha256 === sha256(mp4.subarray(0, 1024)), beyond: beyond.status, thumb: thumb.status, thumbPng: !!thumb.head?.startsWith('89504e47'), thumbBlob: !!thumbed };
  env.step('FF6：/m/ 圖片 200、影片 Range 206 與 416、/t/ 縮圖', imageOk && rangeOk && beyondOk && thumbOk,
    `圖片 ${imageGet.status}、${imageGet.headers['content-type'] ?? '—'}、${imageGet.size} 位元組、SHA-256 ${imageGet.sha256 === sha256(png) ? '等於原檔' : '不同'}、CSP ${imageGet.headers['content-security-policy'] === MEDIA_CSP ? '是 MediaCsp' : imageGet.headers['content-security-policy'] ?? '—'}；`
      + `影片 bytes=0-1023：${range.status}、Content-Range「${range.headers['content-range'] ?? '—'}」、內容${range.sha256 === sha256(mp4.subarray(0, 1024)) ? '等於原檔的前 1024 位元組' : '不同'}；`
      + `超出檔尾：${beyond.status}、Content-Range「${beyond.headers['content-range'] ?? '—'}」；縮圖 blob ${thumbed ? '電腦傳上來了' : '30 秒內沒有'}；`
      + `/t/?s=320：${thumb.status}、${thumb.headers['content-type'] ?? '—'}、${thumb.size} 位元組、開頭 ${thumb.head?.slice(0, 8) ?? '—'}`,
    { image: { url: image.url, status: imageGet.status, headers: imageGet.headers, size: imageGet.size }, range: { status: range.status, contentRange: range.headers['content-range'] ?? null },
      beyond: { status: beyond.status, contentRange: beyond.headers['content-range'] ?? null }, thumb: { status: thumb.status, size: thumb.size, type: thumb.headers['content-type'] ?? null } });

  // 4. 超過 128 KB 的項目走 blob（blob_refs 的 item）；網頁顯示得出來（不是佔位、結尾還在）
  const srv = await serverConversation(env, conv);
  const placeholder = PLACEHOLDERS.some((t) => textOf(longItem).includes(t));
  const shownTail = await waitFor(() => P.page.evaluate(([sel, t]) => [...document.querySelectorAll(sel)].some((el) => (el.textContent || '').includes(t)), [convSel(conv) + ' .it', tail]).catch(() => null), 15000, 200);
  const blobOk = (srv?.itemBlobs?.count ?? 0) >= 1 && (srv?.itemBlobs?.bytes ?? 0) > 128 * 1024;
  outcome.blob = { itemBlob: blobOk, placeholder, tailShown: !!shownTail };
  env.step('FF6：超過 128 KB 的項目走 blob，網頁顯示得出來', blobOk && !placeholder && !!shownTail,
    `伺服器的項目欄位 blob ${srv?.itemBlobs?.count ?? '—'} 個、${srv?.itemBlobs?.bytes ?? '—'} 位元組；網頁的項目${placeholder ? '是佔位' : '不是佔位'}（html ${Buffer.byteLength(longItem.html ?? '')} 位元組）、畫面${shownTail ? '有' : '沒有'}最後一行`,
    { server: srv });

  // 5. OFFICE 離線（暫停同步＝斷線）：/m/ 圖片回縮圖（X-Myriagent-Fallback: thumb、no-store）、影片 503、對話照樣讀得到
  windows.offline = [Date.now(), null];
  let fallback = null;
  let videoOffline = null;
  let readable = null;
  let pause = null;
  let resumed = null;
  try {
    pause = await localCommand(office, 'syncPause', { paused: true });
    fallback = await waitFor(async () => {
      const r = await pageGet(P, image.url);
      return r.headers['x-myriagent-fallback'] === 'thumb' ? r : null;
    }, 30000, 500);
    videoOffline = await pageGet(P, video.url, { Range: 'bytes=0-1023' });
    const offlineItems = await openItems(P, conv);
    readable = { count: offlineItems?.length ?? null, tail: (offlineItems ?? []).some((i) => textOf(i).includes(tail)), media: mediaOf(offlineItems).length };
  } finally {
    // 一定要恢復：暫停中的電腦收不了尾（quit 等 outbox 清空會逾時）
    await localCommand(office, 'syncPause', { paused: false }).catch(() => null);
    resumed = await waitFor(async () => ((await deviceStatus(office, 10000))?.state === 'Live' ? true : null), 60000, 500);
    windows.offline[1] = Date.now() + 2000;
  }
  const fallbackOk = !!fallback && fallback.status === 200 && fallback.headers['cache-control'] === 'no-store' && fallback.sha256 === thumb.sha256 && !!fallback.head?.startsWith('89504e47');
  const readableOk = readable?.count === items.length && readable.tail && readable.media === media.length;
  outcome.offline = { paused: pause?.paused ?? null, fallback: !!fallback, fallbackIsThumb: !!fallback && fallback.sha256 === thumb.sha256, noStore: fallback?.headers['cache-control'] === 'no-store',
    video: videoOffline?.status ?? null, videoFallback: videoOffline?.headers['x-myriagent-fallback'] ?? null, readable: readableOk, resumed: !!resumed };
  env.step('FF6：OFFICE 離線後 /m/ 圖片回縮圖（X-Myriagent-Fallback: thumb）、影片 503、對話照樣讀得到（P47:1265）',
    pause?.paused === true && fallbackOk && videoOffline?.status === 503 && !videoOffline.headers['x-myriagent-fallback'] && readableOk && !!resumed,
    `暫停同步：${pause ? `paused ${pause.paused}、狀態 ${pause.state}` : '—'}；圖片 ${fallback ? `${fallback.status}、X-Myriagent-Fallback ${fallback.headers['x-myriagent-fallback']}、Cache-Control ${fallback.headers['cache-control'] ?? '—'}、${fallback.size} 位元組（${fallback.sha256 === thumb.sha256 ? '等於 /t/ 的縮圖' : '不是縮圖'}）` : '30 秒內沒有退回縮圖'}；`
      + `影片 ${videoOffline?.status ?? '—'}${videoOffline?.headers['x-myriagent-fallback'] ? `（退回 ${videoOffline.headers['x-myriagent-fallback']}）` : ''}；open 拿到 ${readable?.count ?? '—'}／${items.length} 則（長回覆的結尾${readable?.tail ? '在' : '不在'}、媒體 ${readable?.media ?? '—'} 個）；`
      + `恢復同步後 OFFICE ${resumed ? '回到 Live' : '60 秒內沒有回到 Live'}`,
    { pause, fallback: fallback && { status: fallback.status, headers: fallback.headers, size: fallback.size }, video: videoOffline && { status: videoOffline.status, headers: videoOffline.headers }, readable });

  // 6. 範圍外的路徑：listFiles、fileFetch 被拒（policy）並寫進 OFFICE 的遠端紀錄（UNC 不測：檢查存在就會對外送 SMB 雜湊）
  const workspace = path.join(office.data, 'conversations', conv, 'workspace');
  const winIni = path.join(process.env.SystemRoot || path.win32.resolve('C:/Windows'), 'win.ini');
  const probes = [
    { label: 'listFiles 系統資料夾', cmd: { cmd: 'listFiles', conversationId: conv, requestId: 'ff6-l1', path: path.dirname(winIni) } },
    { label: 'fileFetch win.ini', cmd: { cmd: 'fileFetch', conversationId: conv, requestId: 'ff6-f1', path: winIni } },
    { label: 'fileFetch 這台的 device.json（資料根）', cmd: { cmd: 'fileFetch', conversationId: conv, requestId: 'ff6-f2', path: path.join(office.data, 'sync', 'device.json') } },
    { label: 'fileFetch 工作資料夾上一層（..）的 items.json', cmd: { cmd: 'fileFetch', conversationId: conv, requestId: 'ff6-f3', path: workspace + path.sep + '..' + path.sep + 'items.json' } },
  ];
  const rejected = [];
  for (const p of probes) rejected.push({ label: p.label, ...(await rawResult(P, p.cmd)) });
  const audit = await waitFor(async () => {
    const r = await control(office, 'remoteAudit', { limit: 50 });
    const rows = (r.rows ?? []).filter((x) => x.seq > before.remote && (x.cmd === 'listFiles' || x.cmd === 'fileFetch'));
    return rows.length >= probes.length ? rows : null;
  }, 15000, 300);
  const auditOk = !!audit && audit.length === probes.length && audit.every((r) => r.state === 'rejected' && r.code === 'policy');
  outcome.files = { results: rejected.map((r) => `${r.state}/${r.code}`), audit: audit ? audit.map((r) => `${r.cmd}:${r.state}/${r.code}`).sort() : null };
  env.step('FF6：範圍外的路徑被拒（policy）並寫進 OFFICE 的遠端紀錄', rejected.every((r) => r.state === 'rejected' && r.code === 'policy') && auditOk,
    rejected.map((r) => `${r.label}：${r.state ?? '沒有結果'}${r.code ? `／${r.code}` : ''}`).join('；')
      + `；OFFICE 的遠端紀錄 ${audit?.length ?? 0} 列（${audit ? JSON.stringify(countBy(audit.map((r) => ({ k: `${r.cmd} ${r.state}/${r.code}` })), 'k')) : '—'}）`,
    { rejected, audit: audit?.map((r) => ({ cmd: r.cmd, state: r.state, code: r.code, step: r.step, tier: r.tier })) ?? null });

  // 7. blob 配額：補到告警門檻（blob_quota）→ 再傳會超過拒收門檻（507）→ 以更低的配額重啟（blob_refuse、任何上傳 507）→ 事件與對話照收
  const q0 = await quotaState(P);
  const plan = quotaPlan({ used: q0.bytes, quota: q0.quotaBytes });
  const fill = await pageUpload(P, `ff6-fill-${nonce}`, plan.fill, 1);
  const q1 = await quotaState(P);
  const over = await pageUpload(P, `ff6-over-${nonce}`, plan.refused, 2);
  const q2 = await quotaState(P);
  const warnOk = fill.status === 201 && q1.level === 'warn' && q1.alerts.includes('blob_quota') && over.status === 507 && over.json?.error === 'insufficient_storage'
    && q2.bytes === q1.bytes && q2.level === 'warn';
  outcome.quotaWarn = { fill: fill.status, level: q1.level, alerts: q1.alerts, over: over.status, error: over.json?.error ?? null, unchanged: q2.bytes === q1.bytes };
  env.step('FF6：上傳補到告警門檻 → blob_quota；再傳會超過拒收門檻 → 507', warnOk,
    `用量 ${q0.bytes}／配額 ${q0.quotaBytes}（${q0.level}）；補 ${plan.fill} 位元組：${fill.status}、用量 ${q1.bytes}（${q1.level}、告警 ${q1.alerts.join('、') || '沒有'}）；`
      + `再傳 ${plan.refused} 位元組（會到 ${Math.round(((q1.bytes + plan.refused) * 100) / q1.quotaBytes)}%）：${over.status} ${over.json?.error ?? ''}、用量${q2.bytes === q1.bytes ? '沒變' : `變成 ${q2.bytes}`}`,
    { plan, q0, q1, q2, fill: { status: fill.status, json: fill.json }, over: { status: over.status, json: over.json } });

  // 以更低的配額（現在的用量是它的 98%）重啟同一個資料根：用量從 blobs 表載入
  await rawClose(P);
  const snapBefore = (await ffSnapshot(P, conv))?.t ?? 0;
  const lowered = Math.floor((q2.bytes * 100) / 98);
  windows.downtime = [Date.now(), null];
  const stop = await env.hub.stop();
  const offline = await waitFor(async () => ((await tstatus(P))?.state !== 'online' ? true : null), 20000, 200);
  await env.hub.restart({ extraArgs: mediaArgs(lowered) });
  const live = await waitLive(P, 60000);
  const devicesLive = await waitFor(async () => {
    for (const d of env.devices.values()) if ((await deviceStatus(d, 10000))?.state !== 'Live') return null;
    return true;
  }, 90000, 1000);
  windows.downtime[1] = Date.now() + 2000;
  const raw2 = await rawConnect(P);
  const q3 = await quotaState(P);
  const after = await pageUpload(P, `ff6-after-${nonce}`, 4096, 3);
  outcome.restart = { stopForced: stop.forced, pageOffline: !!offline, pageLive: !!live, devicesLive: !!devicesLive };
  outcome.quotaRefuse = { quotaLowered: q3.quotaBytes === lowered, level: q3.level, alerts: q3.alerts, upload: after.status, error: after.json?.error ?? null };
  env.step('FF6：以更低的配額重啟 → blob_refuse、任何上傳 507（P47:1266、:1268）',
    !stop.forced && !!live && !!devicesLive && raw2.ok && q3.quotaBytes === lowered && q3.bytes === q2.bytes && q3.level === 'refuse' && q3.alerts.includes('blob_refuse')
      && after.status === 507 && after.json?.error === 'insufficient_storage',
    `admin stop ${stop.forced ? '逾時（強制結束）' : `結束碼 ${stop.adminCode}`}、頁面${offline ? '看到斷線' : '沒看到斷線'}；以 Hub:Media:QuotaBytes=${lowered} 重啟：頁面${live ? '重新連上' : '60 秒內沒有連上'}、兩台假裝置${devicesLive ? '都回到 Live' : '沒有都回到 Live'}；`
      + `ops/status 的 blobs：配額 ${q3.quotaBytes}、用量 ${q3.bytes}（${q3.bytes === q2.bytes ? '照 blobs 表載入' : '和重啟前不同'}）、等級 ${q3.level}、告警 ${q3.alerts.join('、') || '沒有'}；`
      + `再傳 4096 位元組：${after.status} ${after.json?.error ?? ''}`,
    { stop, q3, after: { status: after.status, json: after.json } });

  // 事件與對話照收：網頁再送一則，那一輪的回覆到了、伺服器的項目增加
  await control(office, 'nextRemoteTurn', { turn: { steps: [{ text: `配額滿了也照收 ${nonce}` }], outcome: 'completed' } });
  const srvBefore = await serverConversation(env, conv);
  // 重新連上之後網頁照版面再 open 一次（新的 conversation 快照）才打字
  const snapBack = await waitFor(async () => (((await ffSnapshot(P, conv))?.t ?? 0) > snapBefore ? true : null), 20000, 200);
  const send2 = snapBack ? await typeSend(P, conv, 'FF6 配額滿了還能聊天嗎') : null;
  const send2Res = send2 ? await ffWaitResult(P, send2.commandId, 20000) : null;
  const grown = await waitFor(async () => {
    const c = await serverConversation(env, conv);
    return c && srvBefore && c.items >= srvBefore.items + 2 ? c : null;
  }, 30000, 500);
  const replied = raw2.ok ? await waitFor(async () => ((await openItems(P, conv)) ?? []).some((i) => textOf(i).includes(`配額滿了也照收 ${nonce}`)) || null, 30000, 1000) : null;
  outcome.stillChats = { send: send2Res?.state ?? null, serverGrew: !!grown, replied: !!replied };
  env.step('FF6：配額到拒收門檻時事件與對話照收', send2Res?.state === 'done' && !!grown && !!replied,
    `送出 ${send2 ? send2Res?.state ?? '沒有結果' : '沒送'}；伺服器的項目 ${srvBefore?.items ?? '—'} → ${grown?.items ?? '沒有增加'}；回覆${replied ? '到了' : '沒到'}`, { send: send2, result: send2Res });
  await rawClose(P);

  // 8. 頁面乾淨（預期的 416、507、離線與停機期間的錯誤除外）
  outcome.page = await pageCleanStep(env, [P], 'FF6：頁面乾淨（主控台、回應、CSP、對話框、XSS 計數）');
  return outcome;
}
