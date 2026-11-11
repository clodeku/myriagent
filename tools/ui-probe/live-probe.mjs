// 網頁即時檢視的瀏覽器探針（WP4.6 計畫 docs/plans/2026-10-19-wp4.6-web-live.md 步 3 起，之後各步在 SCENARIOS 加情境）：
// 用 Playwright（本機 Chrome、無頭、暫存設定檔）開 hub-page.mjs 的伺服器模式頁（真的 SignalR 程式庫＋真的 transport-hub.js＋假 hub、伺服器版 CSP）
// 與桌面模式頁（最小的 WebView2 模擬），資料取自 live-fixtures.mjs。
// - 每個檢查印「[通過]／[失敗] 名稱 詳情」；最後另加兩項：所有頁面 CSP 0 違規、主控台沒有預期以外的錯誤（SEC-06）。
// - 結果先寫 out/live/live-report.json 再關瀏覽器（關閉加時限）；截圖也在 out/live/。開頭只清空 out/live/（probe.mjs 開頭會清掉整個 out/）。
// - 結束碼：全部通過 0、有失敗 1、探針本身出錯 2。
//
// 用法：cd tools/ui-probe && npm install && node live-probe.mjs
//   只跑名稱開頭符合的檢查：node live-probe.mjs --only fakeHub（多個前綴以逗號分隔：--only harness,th）
//   顯示瀏覽器視窗：node live-probe.mjs --headed
import { chromium } from 'playwright';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import * as HP from './hub-page.mjs';
import * as LF from './live-fixtures.mjs';
import * as RF from './remote-fixtures.mjs';
import { runCollaboratorView, runOwnerCollaborators } from './live-collab.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const OUT = path.join(here, 'out', 'live');
const ONLY = (() => { const i = process.argv.indexOf('--only'); return i > 0 ? String(process.argv[i + 1] || '').split(',').filter(Boolean) : null; })();
const onlyMatch = (name) => !ONLY || ONLY.some((p) => name.startsWith(p));
const HEADED = process.argv.includes('--headed');
const NOW = Date.now();
/** 版號前面的日期時間（init.app.commitAt）：今年的 10/23 14:05（版號旁省略年份），探針哪一年跑都一樣 */
const COMMIT_AT = new Date(new Date().getFullYear(), 9, 23, 14, 5).getTime();
const COMMIT_FULL = `${new Date().getFullYear()}/10/23 14:05`;
const { sleep, until } = HP;

fs.rmSync(OUT, { recursive: true, force: true });
fs.mkdirSync(OUT, { recursive: true });

const report = {
  generatedAt: new Date().toISOString(), chrome: null, webDir: HP.WEB, wwwroot: HP.WWWROOT, outDir: OUT,
  checks: {}, details: {}, screenshots: [], consoleErrors: [], expectedConsole: [], notes: [],
};

/** 詳情只印一行（太長截斷）；完整的在報告檔 */
function brief(detail) {
  if (detail === undefined) return '';
  let text;
  try {
    text = JSON.stringify(detail);
  } catch {
    text = String(detail);
  }
  return text.length > 300 ? text.slice(0, 300) + '…' : text;
}

function check(name, ok, detail) {
  if (!onlyMatch(name)) return;
  report.checks[name] = !!ok;
  if (detail !== undefined) report.details[name] = detail;
  console.log(`[${ok ? '通過' : '失敗'}] ${name} ${brief(detail)}`);
}

async function shot(app, name) {
  await sleep(200);
  const file = name + '.png';
  await app.page.screenshot({ path: path.join(OUT, file) });
  report.screenshots.push(file);
}

let browser;
let H;

// ───── 步 3：探針本身 ─────

async function runHarnessSmoke() {
  const app = await H.openHub('smoke-hub', { init: (ready) => LF.liveBrowserInit(NOW, { mode: ready && ready.mode }) });
  const live = await HP.waitLive(app);
  const msgs = await HP.probeMsgs(app);
  const init = msgs.find((m) => m.type === 'init');
  const scripts = await app.page.evaluate(() => [...document.scripts].map((s) => s.getAttribute('src')));
  await shot(app, 'smoke-hub-1440');
  const errors = HP.unexpectedErrors(app);
  check('harnessSmoke', !!live && !!init && init.conversations.length === LF.liveList(NOW).length && init.remote.devices.length === LF.liveDevices(NOW).length
    && scripts.length === 3 && scripts[1] === '/transport-hub.js' && scripts[2] === 'app.js' && app.csp.length === 0 && errors.length === 0,
  { live: !!live, init: !!init, conversations: init && init.conversations.length, scripts, csp: app.csp, errors });
  await HP.closePage(app);
}

async function runHarnessDesktopSmoke() {
  const app = await H.openDesktop('smoke-desktop', { init: LF.liveDesktopInit(NOW) });
  const ready = await app.page.evaluate(() => document.getElementById('app').classList.contains('ready'));
  const rows = await until(() => app.page.evaluate(() => document.querySelectorAll('.sb-list .crow').length || 0), 4000);
  await shot(app, 'smoke-desktop-1440');
  const errors = HP.unexpectedErrors(app);
  check('harnessDesktopSmoke', ready && app.host.posted[0] && app.host.posted[0].cmd === 'ready' && rows === LF.desktopList(NOW).length && app.csp.length === 0 && errors.length === 0,
    { ready, first: app.host.posted[0] || null, rows, csp: app.csp, errors });
  await HP.closePage(app);
}

async function runFakeHubOnCmd() {
  const app = await H.openHub('fake-hub-oncmd', { init: (ready) => LF.liveBrowserInit(NOW, { mode: ready && ready.mode }) });
  const { hub } = app;
  await HP.waitLive(app);
  const seenArgs = [];
  hub.onCmd('open', (cmd, arg) => {
    seenArgs.push({ cmd, commandId: arg.commandId });
    return { push: [LF.conversationMessage(NOW, cmd.conversationId, '整理用量報表')] };
  });
  const posted = await HP.tpost(app, { cmd: 'open', conversationId: LF.CONV.idle });
  const got = await until(async () => (await HP.msgsOf(app, 'conversation')).find((m) => m.conversationId === LF.CONV.idle) || null, 4000);
  const openFrame = hub.cmds('open').at(-1);
  const result = openFrame ? hub.frames.find((f) => f.dir === 'out' && f.obj.type === 3 && f.obj.result && f.obj.result.commandId === openFrame.commandId) : null;
  // 處理函式回 error：Cmd 回 accepted:false、不推訊息；取消登記之後回到預設（accepted、不推）
  hub.onCmd('close', () => ({ error: 'invalid', push: [] }));
  await HP.tpost(app, { cmd: 'close', conversationId: LF.CONV.idle });
  const closeResult = await until(() => {
    const c = hub.cmds('close').at(-1);
    return c ? hub.frames.find((f) => f.dir === 'out' && f.obj.type === 3 && f.obj.result && f.obj.result.commandId === c.commandId) || null : null;
  }, 3000);
  hub.onCmd('open', null);
  const before = (await HP.msgsOf(app, 'conversation')).length;
  await HP.tpost(app, { cmd: 'open', conversationId: LF.CONV.running });
  await sleep(500);
  const after = (await HP.msgsOf(app, 'conversation')).length;
  check('fakeHubOnCmd', posted.ok && !!got && got.items.length === LF.liveItems(NOW).length && seenArgs.length === 1 && seenArgs[0].cmd.conversationId === LF.CONV.idle
    && !!result && result.obj.result.accepted === true && !!closeResult && closeResult.obj.result.accepted === false && closeResult.obj.result.error === 'invalid'
    && hub.cmds('open').length === 2 && after === before,
  { posted, pushed: !!got, handlerCalls: seenArgs.length, openResult: result && result.obj.result, closeResult: closeResult && closeResult.obj.result, afterUnregister: after - before });
  await HP.closePage(app);
}

async function runHarnessControls() {
  const app = await H.openHub('harness-controls', { init: (ready) => LF.liveBrowserInit(NOW, { mode: ready && ready.mode }) });
  const { page, api } = app;
  await HP.waitLive(app);
  // 可見度、焦點、時鐘
  await HP.setHidden(app, true);
  await HP.setFocused(app, false);
  const t0 = await page.evaluate(() => Date.now());
  await HP.advanceClock(app, 150000);
  const env = await page.evaluate(() => ({ hidden: document.hidden, vis: document.visibilityState, focus: document.hasFocus(), now: Date.now() }));
  await HP.setHidden(app, false);
  await HP.setFocused(app, true);
  // window.open、剪貼簿（成功、被拒）、通知（要求授權、顯示、點擊）、sessionStorage 丟例外
  const probe = await page.evaluate(async () => {
    const out = {};
    window.open('/m/abc', '_blank', 'noopener,noreferrer');
    try { await navigator.clipboard.writeText('第一次'); out.copyOk = true; } catch (e) { out.copyOk = 'threw:' + e.name; }
    window.__probeClipboard = 'deny';
    try { await navigator.clipboard.writeText('第二次'); out.copyDenied = false; } catch (e) { out.copyDenied = e.name; }
    out.permBefore = Notification.permission;
    out.answer = await Notification.requestPermission();
    const n = new Notification('OFFICE-PC：標題', { body: '本文', tag: 't1' });
    let clicked = 0;
    n.onclick = () => { clicked++; };
    out.clickReturned = window.__clickNotification(0);
    out.clicked = clicked;
    try { sessionStorage.setItem('k', 'v'); out.sessionOk = sessionStorage.getItem('k') === 'v'; } catch (e) { out.sessionOk = 'threw:' + e.name; }
    window.__probeSessionThrows = true;
    try { sessionStorage.getItem('k'); out.sessionThrows = false; } catch (e) { out.sessionThrows = e.name; }
    window.__probeSessionThrows = false;
    return out;
  });
  const opens = await HP.opened(app);
  const notes = await HP.notifications(app);
  const copies = await HP.clipboardWrites(app);
  // 上傳端點：PUT /api/v1/uploads/{id} 先回 404（步 11 才實作）
  app.expect((m) => /Failed to load resource/.test(m.text) && /status of 404/.test(m.text) && /\/api\/v1\/uploads\//.test(m.url), '上傳端點的替身刻意回 404');
  const upload = await page.evaluate(async () => {
    const r = await fetch('/api/v1/uploads/01J9QUPLOAD0000000000000000', { method: 'PUT', headers: { 'X-Myriagent-Csrf': '1' }, body: new Blob(['0123456789']) });
    return r.status;
  });
  const uploadReq = api.requests.find((r) => r.path.startsWith('/api/v1/uploads/'));
  check('harnessControls', env.hidden === true && env.vis === 'hidden' && env.focus === false && env.now - t0 >= 150000 && env.now - t0 < 160000
    && opens.length === 1 && opens[0][0] === '/m/abc' && opens[0][2] === 'noopener,noreferrer'
    && probe.copyOk === true && probe.copyDenied === 'NotAllowedError' && copies.length === 2 && copies[0].ok && !copies[1].ok
    && probe.permBefore === 'default' && probe.answer === 'granted' && notes.requests === 1 && notes.shown.length === 1 && notes.shown[0].body === '本文' && probe.clicked === 1
    && probe.sessionOk === true && probe.sessionThrows === 'SecurityError'
    && upload === 404 && !!uploadReq && uploadReq.method === 'PUT' && uploadReq.bodyBytes === 10 && uploadReq.headers['x-myriagent-csrf'] === '1',
  { env: Object.assign({}, env, { advancedMs: env.now - t0 }), opens, probe, notes, copies, upload, uploadReq: uploadReq && { method: uploadReq.method, bytes: uploadReq.bodyBytes } });
  await HP.closePage(app);
}

// ───── 步 4：傳輸層（commandId 回傳、合成 commandResult、openConversationIds、媒體連結開新分頁）─────

const ULID_RE = /^[0-7][0-9A-HJKMNP-TV-Z]{25}$/;
const TEXT_OFFLINE = '目前沒有連線，這個變更沒有送出';
const TEXT_BUSY = '伺服器忙碌，這個變更沒有送出';
const TEXT_TOO_LARGE = '這個變更太大，沒有送出';
const countOf = (list, x) => list.filter((y) => y === x).length;
/** 傳輸層交給網頁的提示（toast 訊息） */
const toastTexts = async (app) => (await HP.msgsOf(app, 'toast')).map((m) => m.text);
/** 頁面收到的 commandResult（某個 commandId） */
const resultsFor = async (app, id) => (await HP.msgsOf(app, 'commandResult')).filter((m) => m.commandId === id);
/** 假 hub 對某個 commandId 回的 Cmd 結果（type 3） */
const cmdResultFrames = (hub, id) => hub.frames.filter((f) => f.dir === 'out' && f.obj.type === 3 && f.obj.result && f.obj.result.commandId === id);
/** 頁面送來、帶這個 commandId 的 Cmd 框 */
const framesWith = (hub, id) => hub.invocations('Cmd').filter((f) => f.obj.arguments[0].commandId === id);
/** 合成的 commandResult 逐鍵符合 D4 */
const isSynth = (m, id, code) => !!m && JSON.stringify(m) === JSON.stringify({ type: 'commandResult', commandId: id, state: 'rejected', code, message: null, tier: null, origin: 'transport' });
const openLive = async (name, opt = {}) => {
  const app = await H.openHub(name, Object.assign({ init: (ready) => LF.liveBrowserInit(NOW, { mode: ready && ready.mode }) }, opt));
  await HP.waitLive(app);
  return app;
};

async function runThPostReturns() {
  const app = await openLive('th-post');
  const { hub, api } = app;
  // 送出的指令：回傳值等於框裡的 commandId（ULID）；ready、在瀏覽器裡處理的回 null
  const open = await HP.tpost(app, { cmd: 'open', conversationId: LF.CONV.idle });
  const rename = await HP.tpost(app, { cmd: 'rename', conversationId: LF.CONV.idle, title: '新標題' });
  const openFrame = await until(() => framesWith(hub, open.value)[0] || null, 3000);
  const renameFrame = await until(() => framesWith(hub, rename.value)[0] || null, 3000);
  const ready = await HP.tpost(app, { cmd: 'ready', mode: 'main' });
  const layout = await HP.tpost(app, { cmd: 'layout', layout: { panes: [{ id: 'p1', tabs: [], active: null }] } });
  const copy = await HP.tpost(app, { cmd: 'copyText', text: 'x' });
  const notObj = await HP.tpost(app, 'open');
  await sleep(400);
  const synth = (await HP.msgsOf(app, 'commandResult')).length;
  check('thPostReturnsCommandId', open.ok && ULID_RE.test(open.value || '') && !!openFrame && openFrame.obj.arguments[0].commandId === open.value
    && ULID_RE.test(rename.value || '') && !!renameFrame && rename.value !== open.value && ready.value === null && layout.value === null && copy.value === null
    && notObj.value === null && synth === 0,
  { open: open.value, rename: rename.value, frames: [!!openFrame, !!renameFrame], ready: ready.value, layout: layout.value, copyText: copy.value, notObject: notObj.value, commandResults: synth });

  // 沒有連線（auth/me 503、斷線）：回 null、沒有框；使用者操作的提示一次，網頁自動送的靜默
  app.expect((m) => /Failed to load resource/.test(m.text) && /status of 503/.test(m.text) && /\/api\/v1\/auth\/me/.test(m.url), '斷線期間 auth/me 503（刻意）');
  api.set('/api/v1/auth/me', { status: 503, body: { error: 'unavailable' } });
  hub.drop();
  await until(async () => { const s = await HP.tstatus(app); return s && s.state !== 'online' ? s : null; }, 4000, 30);
  const c0 = hub.invocations('Cmd').length;
  const t0 = countOf(await toastTexts(app), TEXT_OFFLINE);
  const user = await HP.tpost(app, { cmd: 'rename', conversationId: LF.CONV.idle, title: '離線時改名' });
  const auto = await HP.tpost(app, { cmd: 'open', conversationId: LF.CONV.idle });
  await sleep(300);
  const offlineToasts = countOf(await toastTexts(app), TEXT_OFFLINE) - t0;
  check('thPostOfflineReturnsNull', user.ok && user.value === null && auto.value === null && hub.invocations('Cmd').length === c0 && offlineToasts === 1,
    { user: user.value, auto: auto.value, frames: hub.invocations('Cmd').length - c0, offlineToasts });
  api.set('/api/v1/auth/me', { status: 200, body: { name: 'admin', serverDomain: 'hub.probe.local', session: {} } });
  await app.page.evaluate(() => window.myriagentHubTransport.retryNow());
  await HP.waitLive(app, 6000);
  await HP.closePage(app);
}

async function runThTooLarge() {
  const app = await openLive('th-too-large');
  const { hub } = app;
  const c0 = hub.invocations('Cmd').length;
  const w0 = HP.warnsOf(app).length;
  const t0 = countOf(await toastTexts(app), TEXT_TOO_LARGE);
  const big = await HP.tpost(app, { cmd: 'rename', conversationId: LF.CONV.idle, title: 'x'.repeat(70000) });
  await sleep(400);
  const one = { frames: hub.invocations('Cmd').length - c0, warns: HP.warnsOf(app).length - w0, toasts: countOf(await toastTexts(app), TEXT_TOO_LARGE) - t0 };
  // 正向對照：上限內的照送、回 commandId
  const ok = await HP.tpost(app, { cmd: 'rename', conversationId: LF.CONV.idle, title: 'x'.repeat(4000) });
  const okFrame = await until(() => framesWith(hub, ok.value)[0] || null, 3000);
  check('thTooLargeReturnsNull', big.ok && big.value === null && one.frames === 0 && one.warns === 1 && one.toasts === 1 && (await HP.msgsOf(app, 'commandResult')).length === 0
    && ULID_RE.test(ok.value || '') && !!okFrame,
  Object.assign({ value: big.value, positiveControl: ok.value }, one));
  await HP.closePage(app);
}

async function runThSynthRejected() {
  const app = await openLive('th-synth-rejected');
  const { hub } = app;
  hub.rejectNext('rename', 'invalid');
  const r = await HP.tpost(app, { cmd: 'rename', conversationId: LF.CONV.idle, title: '被拒' });
  await until(async () => (await resultsFor(app, r.value)).length > 0, 3000);
  await sleep(600);
  const got = await resultsFor(app, r.value);
  // 正向對照：被接受的不合成
  const accepted = await HP.tpost(app, { cmd: 'rename', conversationId: LF.CONV.idle, title: '接受' });
  await until(() => cmdResultFrames(hub, accepted.value)[0] || null, 3000);
  await sleep(500);
  const acceptedResults = await resultsFor(app, accepted.value);
  const toasts = await toastTexts(app);
  check('thSynthRejectedOnInvalid', got.length === 1 && isSynth(got[0], r.value, 'invalid') && acceptedResults.length === 0 && toasts.length === 0,
    { results: got, acceptedResults, toasts });
  await HP.closePage(app);
}

async function runThSynthRateLimit() {
  const app = await openLive('th-synth-rate');
  const { hub } = app;
  // 第一次＋重送三次（1、2、4 秒）都被限流：放棄，合成一則 rate_limited，提示「伺服器忙碌」一次
  hub.rejectNext('rename', 'rate_limited', 4);
  const r = await HP.tpost(app, { cmd: 'rename', conversationId: LF.CONV.idle, title: '一直被限流' });
  await until(async () => (await resultsFor(app, r.value)).length > 0, 11000, 100);
  await sleep(600);
  const got = await resultsFor(app, r.value);
  const busy = countOf(await toastTexts(app), TEXT_BUSY);
  check('thSynthRateLimitGiveUp', got.length === 1 && isSynth(got[0], r.value, 'rate_limited') && framesWith(hub, r.value).length === 4 && busy === 1,
    { results: got, frames: framesWith(hub, r.value).length, busyToasts: busy });
  await HP.closePage(app);
}

async function runThRateLimitedReconnect() {
  const app = await openLive('th-rate-reconnect');
  const { hub } = app;
  hub.rejectNext('rename', 'rate_limited', 1);
  const r = await HP.tpost(app, { cmd: 'rename', conversationId: LF.CONV.idle, title: '限流後斷線' });
  await until(() => cmdResultFrames(hub, r.value).find((f) => f.obj.result.error === 'rate_limited') || null, 3000, 20);
  await sleep(150); // 頁面收到限流的結果（排了 1 秒後重送）之後才斷
  hub.drop();
  await until(() => hub.connections.length >= 2, 6000);
  const live = await HP.waitLive(app, 6000);
  await sleep(1800); // 超過原本 1 秒的重送時間
  const got = await resultsFor(app, r.value);
  const second = hub.connections[1];
  const resent = framesWith(hub, r.value).filter((f) => second && f.conn === second.id);
  check('thRateLimitedThenReconnect_合成not_sent', !!live && got.length === 1 && isSynth(got[0], r.value, 'not_sent') && framesWith(hub, r.value).length === 1 && resent.length === 0,
    { results: got, frames: framesWith(hub, r.value).map((f) => f.conn), connections: hub.connections.length });
  await HP.closePage(app);
}

async function runThDropPending() {
  const app = await openLive('th-drop-pending');
  const { hub, api } = app;
  app.expect((m) => /Failed to load resource/.test(m.text) && /status of 503/.test(m.text) && /\/api\/v1\/auth\/me/.test(m.url), '斷線期間 auth/me 503（刻意）');
  // 三筆等待重送（使用者操作的 rename、合併鍵的 setting、網頁自動送的 open）時斷線：每一筆各一則 not_sent，「目前沒有連線」只提示一次
  hub.rejectNext('rename', 'rate_limited', 1);
  hub.rejectNext('setting', 'rate_limited', 1);
  hub.rejectNext('open', 'rate_limited', 1);
  const ids = [];
  for (const m of [{ cmd: 'rename', conversationId: LF.CONV.idle, title: '甲' }, { cmd: 'setting', key: 'theme', value: 'dark' }, { cmd: 'open', conversationId: LF.CONV.running }]) {
    ids.push((await HP.tpost(app, m)).value);
  }
  await until(() => ids.every((id) => cmdResultFrames(hub, id).length > 0), 3000, 20);
  await sleep(150);
  const t0 = countOf(await toastTexts(app), TEXT_OFFLINE);
  api.set('/api/v1/auth/me', { status: 503, body: { error: 'unavailable' } });
  hub.drop();
  await until(async () => { const s = await HP.tstatus(app); return s && s.state !== 'online' ? s : null; }, 4000, 30);
  await sleep(1500);
  const per = [];
  for (const id of ids) per.push(await resultsFor(app, id));
  const offline = countOf(await toastTexts(app), TEXT_OFFLINE) - t0;
  check('thDropPendingSynthNotSent', ids.every((id) => ULID_RE.test(id || '')) && per.every((list, i) => list.length === 1 && isSynth(list[0], ids[i], 'not_sent'))
    && ids.every((id) => framesWith(hub, id).length === 1) && offline === 1,
  { ids, results: per, offlineToasts: offline });
  api.set('/api/v1/auth/me', { status: 200, body: { name: 'admin', serverDomain: 'hub.probe.local', session: {} } });
  await app.page.evaluate(() => window.myriagentHubTransport.retryNow());
  await HP.waitLive(app, 6000);
  await HP.closePage(app);
}

async function runThClientState() {
  const app = await openLive('th-clientstate');
  const { hub } = app;
  const states = () => hub.invocations('ClientState').map((f) => Object.assign({ at: f.at, conn: f.conn }, f.obj.arguments[0]));
  // visible 之後斷線重連：新連線的第一則 ClientState 就帶那份清單（草稿被濾掉）
  await HP.tpost(app, { cmd: 'visible', conversationIds: [LF.CONV.idle, LF.DRAFT, LF.CONV.running] });
  await sleep(400);
  hub.drop();
  await until(() => hub.connections.length >= 2, 6000);
  await HP.waitLive(app, 6000);
  const second = hub.connections[1];
  const firstOnNew = states().find((s) => second && s.conn === second.id);
  check('thClientStateCarriesVisible', !!firstOnNew && JSON.stringify(firstOnNew.openConversationIds) === JSON.stringify([LF.CONV.idle, LF.CONV.running]) && firstOnNew.visible === true,
    { firstOnNewConnection: firstOnNew || null });

  // visible 換了：200 ms 合併後重送 ClientState；內容沒變不送
  const sameIds = (s, ids) => JSON.stringify(s.openConversationIds) === JSON.stringify(ids);
  const n0 = states().length;
  const tA = Date.now();
  await HP.tpost(app, { cmd: 'visible', conversationIds: [LF.CONV.view] });
  const a = await until(() => states().slice(n0).find((s) => sameIds(s, [LF.CONV.view])) || null, 2000, 10);
  const n1 = states().length;
  const tB = Date.now();
  await HP.tpost(app, { cmd: 'visible', conversationIds: [LF.CONV.codex] });
  const b = await until(() => states().slice(n1).find((s) => sameIds(s, [LF.CONV.codex])) || null, 2000, 10);
  const n2 = states().length;
  await HP.tpost(app, { cmd: 'visible', conversationIds: [LF.CONV.codex] });
  await sleep(600);
  const repeat = states().slice(n2);
  const delayA = a ? a.at - tA : null;
  const delayB = b ? b.at - tB : null;
  check('thVisibleUpdatesClientState', !!a && !!b && delayA >= 150 && delayA <= 600 && delayB >= 150 && delayB <= 600 && states().slice(n1).length === 1 && repeat.length === 0,
    { delayA, delayB, afterB: states().slice(n1).length, repeat: repeat.length });
  await HP.closePage(app);
}

async function runThMediaLink() {
  const app = await openLive('th-media-link');
  const { hub } = app;
  const n0 = app.navigations.length;
  const c0 = hub.invocations('Cmd').length;
  const r = await HP.tpost(app, { cmd: 'openLink', href: '/m/k3a?s=160' });
  const r2 = await HP.tpost(app, { cmd: 'openLink', href: '/f/k9' });
  await sleep(400);
  const opens = await HP.opened(app);
  const pathNow = await app.page.evaluate(() => location.pathname).catch((e) => 'error:' + e.message);
  check('thMediaLinkNewTab', r.ok && r2.ok && r.value === null && r2.value === null
    && JSON.stringify(opens) === JSON.stringify([[HP.ORIGIN + '/m/k3a?s=160', '_blank', 'noopener,noreferrer'], [HP.ORIGIN + '/f/k9', '_blank', 'noopener,noreferrer']])
    && pathNow === '/app/' && app.navigations.length === n0 && hub.invocations('Cmd').length === c0,
  { opens, pathNow, navigations: app.navigations.slice(n0).map((x) => x.url), frames: hub.invocations('Cmd').length - c0 });
  await HP.closePage(app);
}

// ───── 步 5：瀏覽器開對話（D9～D12：版面還原、重新 init、草稿、所在電腦、狀態列）─────

const TITLE_OF = Object.fromEntries(LF.liveList(NOW).map((c) => [c.id, c.title]));
/**
 * 假 hub 照伺服器回 open：o.closed（Set）裡的回 conversationClosed（o.closedDelayMs 毫秒後才推）；草稿 id（d 開頭）回伺服器草稿
 * （o.draft(id) 回 draftMessage 的選項）；其他回 conversation（o.header(id) 回要改的標頭欄位）
 */
function serveOpen(hub, o = {}) {
  hub.onCmd('open', (cmd) => {
    const id = cmd.conversationId;
    if (o.closed && o.closed.has(id)) return { push: [{ type: 'conversationClosed', conversationId: id }], delayMs: o.closedDelayMs || 0 };
    if (id.startsWith('d')) return { push: [LF.draftMessage(NOW, id, Object.assign({ paneId: null }, (o.draft && o.draft(id)) || {}))] };
    const items = (o.items && o.items(id)) || undefined;
    return { push: [LF.conversationMessage(NOW, id, TITLE_OF[id] || (o.title && o.title(id)) || '清單外還開著的對話', { header: (o.header && o.header(id)) || {}, items })] };
  });
}

/** 版面：panes 是 [[分頁 id…], 作用中] 的陣列（窗格 id 依序 p1、p2…） */
const layoutOf = (panes, o = {}) => Object.assign({
  panes: panes.map(([tabs, active], i) => ({ id: 'p' + (i + 1), tabs, active })), focusedPane: 'p1', sidebarCollapsed: false, view: 'chat',
}, o);

/** 開瀏覽器頁：第一則 init 帶這個版面（null＝這個瀏覽器沒有存下的版面；o.initOpts 併進 liveBrowserInit 的選項）；假 hub 照 serveOpen 回 open */
async function openWithLayout(name, layout, o = {}) {
  const app = await H.openHub(name, Object.assign({
    init: (ready) => LF.liveBrowserInit(NOW, Object.assign({ mode: ready && ready.mode, layout }, o.initOpts || {})),
    hub: (hub) => serveOpen(hub, o.serve || {}),
  }, o.open || {}));
  await HP.waitLive(app);
  return app;
}

/** 工作區：每個窗格的分頁 id 與作用中的分頁；目前的檢視 */
const wsState = (app) => app.page.evaluate(() => {
  const panes = [...document.querySelectorAll('#workspace .pane')].map((p) => {
    const act = p.querySelector('.tab.active');
    return { id: p.dataset.pane, tabs: [...p.querySelectorAll('.tab')].map((t) => t.dataset.key), active: act ? act.dataset.key : null };
  });
  return { panes, chat: !document.getElementById('workspace').hidden, dashboard: !document.getElementById('dashboard').hidden };
});

/** 某個對話的畫面：載入了沒、顯示中、項目數、輸入框的字、狀態列（文字與轉圈）、裝置行、標記（探針放的，看 DOM 有沒有重建） */
const convState = (app, id) => app.page.evaluate((cid) => {
  const root = document.querySelector('.conv[data-conv="' + cid + '"]');
  if (!root) return null;
  const shown = (el) => !!el && !el.hidden && getComputedStyle(el).display !== 'none';
  const run = root.querySelector('.runline');
  const dev = root.querySelector('.ch-dev');
  const ta = root.querySelector('textarea.cinput');
  return {
    loaded: !root.classList.contains('loading'), shown: shown(root), mark: root.dataset.probeMark || null,
    items: root.querySelectorAll('.items > *').length, text: ta ? ta.value : null,
    run: shown(run) ? run.querySelector('.run-text').textContent : null, spin: shown(run) && shown(run.querySelector('.run-spin')),
    device: shown(dev) ? { text: dev.querySelector('.ch-dev-text').textContent, online: dev.dataset.online } : null,
  };
}, id);

const waitLoaded = (app, ids, ms = 4000) => until(async () => {
  const out = {};
  for (const id of ids) {
    const s = await convState(app, id);
    if (!s || !s.loaded) return null;
    out[id] = s;
  }
  return out;
}, ms);

/** 在某個對話的輸入框打字（不必是顯示中的分頁） */
const typeInto = (app, id, text) => app.page.evaluate(([cid, t]) => {
  const ta = document.querySelector('.conv[data-conv="' + cid + '"] textarea.cinput');
  ta.value = t;
  ta.dispatchEvent(new Event('input', { bubbles: true }));
}, [id, text]);

/** 在對話的 DOM 放標記（之後還在＝沒有重建） */
const markConv = (app, ids) => app.page.evaluate((list) => { for (const id of list) { const el = document.querySelector('.conv[data-conv="' + id + '"]'); if (el) el.dataset.probeMark = '1'; } }, ids);

const openIds = (hub, from = 0) => hub.cmds('open').slice(from).map((c) => c.cmd.conversationId);
const countIn = (list, x) => list.filter((y) => y === x).length;
const clientStates = (hub) => hub.invocations('ClientState').map((f) => f.obj.arguments[0]);

/** 水平捲動：文件與工作區都不超出視窗寬 */
const hScroll = (app) => app.page.evaluate(() => {
  const w = window.innerWidth;
  const wide = [...document.querySelectorAll('#app *')].filter((el) => { const r = el.getBoundingClientRect(); return r.width > 0 && r.right > w + 1 && getComputedStyle(el).position !== 'fixed'; })
    .slice(0, 5).map((el) => el.tagName.toLowerCase() + '.' + String(el.className).split(' ').join('.'));
  return { docScroll: document.documentElement.scrollWidth, bodyScroll: document.body.scrollWidth, innerWidth: w, wide };
});

async function runRestoreLayout() {
  const layout = layoutOf([[[LF.CONV.running, LF.CONV.idle], LF.CONV.idle], [[LF.CONV.codex], LF.CONV.codex]]);
  const app = await openWithLayout('restore-layout', layout);
  const { hub } = app;
  const loaded = await waitLoaded(app, [LF.CONV.running, LF.CONV.idle, LF.CONV.codex]);
  const ws = await wsState(app);
  const opens = openIds(hub);
  check('hubRestoreLayout', !!loaded && ws.chat && !ws.dashboard && ws.panes.length === 2
    && JSON.stringify(ws.panes[0].tabs) === JSON.stringify([LF.CONV.running, LF.CONV.idle]) && ws.panes[0].active === LF.CONV.idle
    && JSON.stringify(ws.panes[1].tabs) === JSON.stringify([LF.CONV.codex]) && ws.panes[1].active === LF.CONV.codex
    && opens.length === 3 && [LF.CONV.running, LF.CONV.idle, LF.CONV.codex].every((id) => countIn(opens, id) === 1)
    && loaded[LF.CONV.idle].shown && loaded[LF.CONV.codex].shown && !loaded[LF.CONV.running].shown
    && loaded[LF.CONV.idle].items === LF.liveItems(NOW).length && hub.cmds('newConversation').length === 0,
  { panes: ws.panes, chat: ws.chat, opens, shown: loaded && Object.fromEntries(Object.entries(loaded).map(([k, v]) => [k.slice(0, 8), v.shown])), newConversation: hub.cmds('newConversation').length });
  // ClientState 的 openConversationIds：各窗格作用中、看得到的分頁
  const cs = await until(() => clientStates(hub).find((s) => JSON.stringify(s.openConversationIds) === JSON.stringify([LF.CONV.idle, LF.CONV.codex])) || null, 3000);
  check('hubClientStateOpenIds', !!cs && cs.visible === true, { clientStates: clientStates(hub).map((s) => s.openConversationIds) });
  await shot(app, 'hub-chat-1440');
  const hs = await hScroll(app);
  check('hubChat1440NoHScroll', hs.docScroll <= hs.innerWidth && hs.bodyScroll <= hs.innerWidth && hs.wide.length === 0, hs);
  await HP.closePage(app);
}

/**
 * Grok 與 Antigravity（設計 2026-10-22-grok-antigravity-sync 的 P5、成功條件第 3 項）：瀏覽器版的側欄有兩種的帳戶卡、矩陣有兩列；
 * 兩個窗格各開一個對話——Grok 的工具卡、待辦與等核准的卡片照常顯示，執行中送出鈕是「加入佇列」（不支援插入）；Antigravity 沒有任何卡片、
 * 權限是「自動接受編輯」。截圖 hub-providers-chat-1440、hub-providers-dashboard-1440
 */
async function runProviderCards() {
  const titles = Object.fromEntries(LF.providerList(NOW).map((c) => [c.id, c.title]));
  const { grok, agy } = LF.PROVIDER_CONV;
  const app = await openWithLayout('provider-cards', layoutOf([[[grok], grok], [[agy], agy]]), {
    initOpts: { accounts: [...LF.liveAccounts(NOW), ...LF.providerAccounts(NOW)], conversations: [...LF.liveList(NOW), ...LF.providerList(NOW)].sort((a, b) => b.updatedAt - a.updatedAt) },
    serve: { title: (id) => titles[id], header: (id) => LF.providerHeader(id) || {}, items: (id) => LF.providerItems(NOW, id) },
  });
  const { page } = app;
  const loaded = await waitLoaded(app, [grok, agy]);
  const cards = await page.evaluate(() => Object.fromEntries(['la_g1', 'la_a1'].map((k) => {
    const el = document.querySelector('#sidebar .macct[data-key="' + k + '"]');
    return [k, el ? { text: el.textContent, imgs: el.querySelectorAll('img').length, shown: el.getBoundingClientRect().height > 0 } : null];
  })));
  const conv = await page.evaluate(([g, a]) => {
    const one = (id) => {
      const root = document.querySelector('.conv[data-conv="' + id + '"]');
      if (!root) return null;
      const kinds = [...root.querySelectorAll('.items .it[data-kind]')].map((x) => x.dataset.kind);
      return { kinds, text: root.textContent };
    };
    return { grok: one(g), agy: one(a) };
  }, [grok, agy]);
  await page.fill('.conv[data-conv="' + grok + '"] textarea.cinput', 'Grok 執行中再送一則');
  const sendTitle = await page.evaluate((id) => document.querySelector('.conv[data-conv="' + id + '"] .cbtn.send')?.title ?? null, grok);
  const grokOk = !!conv.grok && ['User', 'Thinking', 'Plan', 'Tool', 'Approval'].every((k) => conv.grok.kinds.includes(k)) && conv.grok.text.includes('TODO\u3000src');
  const agyOk = !!conv.agy && !conv.agy.kinds.some((k) => ['Approval', 'Question', 'PlanApproval'].includes(k)) && conv.agy.kinds.includes('Tool') && conv.agy.text.includes('自動接受編輯');
  check('hubProviderAccountCards', !!loaded && !!cards.la_g1 && cards.la_g1.shown && cards.la_g1.text.includes('Grok 個人 ' + LF.XSS) && cards.la_g1.imgs === 0
    && !!cards.la_a1 && cards.la_a1.shown && cards.la_a1.text.includes('Antigravity'), { cards });
  check('hubProviderConversations', grokOk && agyOk && sendTitle === '加入佇列（這一輪完成後送出）',
    { grok: conv.grok && conv.grok.kinds, agy: conv.agy && conv.agy.kinds, sendTitle });
  await shot(app, 'hub-providers-chat-1440');
  await page.click('#viewSwitch [data-view="dashboard"]');
  const rows = await until(() => page.evaluate(() => {
    const grid = document.querySelector('#dashboard .acct-grid');
    if (!grid) return null;
    const text = grid.textContent || '';
    return text.includes('Grok 個人') && text.includes('Antigravity') ? { text: text.slice(0, 400) } : null;
  }), 4000);
  check('hubProviderMatrixRows', !!rows, rows || {});
  await shot(app, 'hub-providers-dashboard-1440');
  const hs = await hScroll(app);
  check('hubProvidersNoHScroll', hs.docScroll <= hs.innerWidth && hs.bodyScroll <= hs.innerWidth && hs.wide.length === 0, hs);
  await HP.closePage(app);
}

/**
 * 載入看門狗（2026-11-07 使用者回報：分頁一直「載入對話中…」）：第一次 open 沒有回應，看得到的分頁 6 秒後自動重送 open、
 * 回應到了就載好，並以 clientError 回報當時的狀態（找出是哪一條路掉的）
 */
async function runLoadRetry() {
  const id = LF.CONV.idle;
  let opens = 0;
  const app = await H.openHub('load-retry', {
    init: (ready) => LF.liveBrowserInit(NOW, { mode: ready && ready.mode, layout: layoutOf([[[id], id]]) }),
    hub: (hub) => hub.onCmd('open', (cmd) => {
      opens++;
      if (opens === 1) return { push: [] }; // 第一次不回
      return { push: [LF.conversationMessage(NOW, cmd.conversationId, TITLE_OF[cmd.conversationId] || '對話')] };
    }),
  });
  await HP.waitLive(app);
  const stuck = await convState(app, id);
  const loaded = await waitLoaded(app, [id], 10000);
  const reports = app.hub.cmds('clientError').map((c) => c.cmd);
  const report = reports.find((r) => String(r.message).includes('一直沒有載入完成'));
  check('hubLoadRetry', !!stuck && !stuck.loaded && !!loaded && openIds(app.hub).filter((x) => x === id).length === 2 && !!report && /"sent":true/.test(report.detail || ''),
    { stuckFirst: stuck && stuck.loaded, loaded: !!loaded, opens: openIds(app.hub), report });
  await HP.closePage(app);
}

/**
 * 標頭清單對不上（2026-11-07 使用者回報 Claude 常偵測不到模型與推理強度）：對話沒有上一份清單時收到省略清單的標頭，
 * 網頁重送 open 拿整份快照，模型鈕回來，並回報當時的狀態
 */
async function runHeaderListsRecover() {
  const id = LF.CONV.idle;
  let opens = 0;
  const app = await H.openHub('header-lists', {
    init: (ready) => LF.liveBrowserInit(NOW, { mode: ready && ready.mode, layout: layoutOf([[[id], id]]) }),
    hub: (hub) => hub.onCmd('open', (cmd) => {
      opens++;
      const msg = LF.conversationMessage(NOW, cmd.conversationId, TITLE_OF[cmd.conversationId] || '對話');
      if (opens === 1) { delete msg.header.models; delete msg.header.commands; } // 第一份快照少了清單（模擬兩邊對不上）
      return { push: [msg] };
    }),
  });
  await HP.waitLive(app);
  await waitLoaded(app, [id]);
  const modelHidden = () => app.page.evaluate((cid) => { const b = document.querySelector('.conv[data-conv="' + cid + '"] .cbar .pick'); return !b || b.hidden; }, id);
  const before = await modelHidden();
  const slim = LF.conversationMessage(NOW, id, TITLE_OF[id] || '對話').header;
  delete slim.models; delete slim.commands;
  app.hub.push({ type: 'header', conversationId: id, header: slim });
  const reopened = await until(() => (openIds(app.hub).filter((x) => x === id).length >= 2 ? true : null), 3000);
  const shown = await until(async () => ((await modelHidden()) ? null : true), 3000);
  const report = app.hub.cmds('clientError').map((c) => c.cmd).find((r) => String(r.message).includes('沒有上一份清單'));
  // 第一份快照就少了清單時網頁當場就重送（before 可能已經是 false）：看得到重送、模型鈕回來、有回報
  check('hubHeaderListsRecover', !!reopened && !!shown && !!report, { before, reopened: !!reopened, shown: !!shown, opens: openIds(app.hub), report });
  await HP.closePage(app);
}

/**
 * 執行中的標頭更新很頻繁，同一個影格常排到好幾份省略清單的標頭（2026-11-08 使用者回報：Claude 對話的模型鈕不見、推理強度「不支援」）：
 * 連續推幾份省略清單的標頭，模型鈕要一直在、推理強度不能變成「不支援」，也不必重送 open
 */
async function runHeaderSlimBatch() {
  const efforts = ['low', 'medium', 'high', 'xhigh', 'max'];
  const SLIM_BATCH_HEADER = { effort: 'high', models: [{ value: 'default', label: '預設', description: '帳戶預設模型', efforts }, { value: 'opus', label: 'Opus', description: '', efforts }] };
  const id = LF.CONV.idle;
  const app = await H.openHub('header-slim-batch', {
    init: (ready) => LF.liveBrowserInit(NOW, { mode: ready && ready.mode, layout: layoutOf([[[id], id]]) }),
    hub: (hub) => hub.onCmd('open', (cmd) => ({ push: [LF.conversationMessage(NOW, cmd.conversationId, TITLE_OF[cmd.conversationId] || '對話', { header: SLIM_BATCH_HEADER })] })),
  });
  await HP.waitLive(app);
  await waitLoaded(app, [id]);
  const state = () => app.page.evaluate((cid) => {
    const bar = document.querySelector('.conv[data-conv="' + cid + '"] .cbar');
    const picks = bar ? [...bar.querySelectorAll('.pick')] : [];
    return { model: picks[0] ? !picks[0].hidden : false, labels: picks.map((p) => (p.hidden ? '' : p.textContent.trim())) };
  }, id);
  const before = await state();
  const opensBefore = openIds(app.hub).length;
  for (let i = 0; i < 6; i++) {
    const slim = LF.conversationMessage(NOW, id, (TITLE_OF[id] || '對話') + i, { header: SLIM_BATCH_HEADER }).header;
    delete slim.models; delete slim.commands;
    app.hub.push({ type: 'header', conversationId: id, header: slim });
  }
  await sleep(800);
  const after = await state();
  check('hubHeaderSlimBatchKeepsLists', before.model && after.model && !after.labels.some((t) => t.includes('不支援')) && openIds(app.hub).length === opensBefore,
    { before, after, opens: openIds(app.hub) });
  await HP.closePage(app);
}

async function runRestoreUnlisted() {
  // 清單外（被 1000 筆擠出）的對話：分頁留著、送 open；伺服器說不在了（conversationClosed）才關
  const layout = layoutOf([[[LF.CONV.idle, LF.CONV.unlisted], LF.CONV.idle]]);
  const app = await openWithLayout('restore-unlisted', layout, { serve: { closed: new Set([LF.CONV.unlisted]), closedDelayMs: 1500 } });
  const { hub } = app;
  const before = await wsState(app);
  const closed = await until(async () => { const s = await wsState(app); return s.panes[0].tabs.includes(LF.CONV.unlisted) ? null : s; }, 5000);
  const opens = openIds(hub);
  const closes = hub.cmds('close').length;
  check('hubRestoreKeepsUnlistedConversation', JSON.stringify(before.panes[0].tabs) === JSON.stringify([LF.CONV.idle, LF.CONV.unlisted])
    && countIn(opens, LF.CONV.unlisted) === 1 && !!closed && JSON.stringify(closed.panes[0].tabs) === JSON.stringify([LF.CONV.idle]) && closes === 0,
  { before: before.panes, after: closed && closed.panes, opens, closes });
  await HP.closePage(app);
}

async function runNoAutoDraft() {
  // 存下的版面是空的（聊天檢視、沒有分頁）：不自動開草稿
  const app = await openWithLayout('no-auto-draft', layoutOf([[[], null]]));
  await sleep(1000);
  const ws = await wsState(app);
  check('hubNoAutoDraft', app.hub.cmds('newConversation').length === 0 && ws.chat && ws.panes.length === 1 && ws.panes[0].tabs.length === 0 && openIds(app.hub).length === 0,
    { newConversation: app.hub.cmds('newConversation').length, ws });
  await HP.closePage(app);
}

async function runOpenFromSidebar() {
  const app = await openWithLayout('open-from-sidebar', null);
  const { page, hub } = app;
  const first = await wsState(app);
  await page.click('#viewSwitch [data-view="chat"]');
  const rowOf = (title) => page.locator('.sb-list .crow', { has: page.locator('.crow-title', { hasText: title }) });
  await rowOf(TITLE_OF[LF.CONV.idle]).click();
  const loaded = await waitLoaded(app, [LF.CONV.idle]);
  const ws = await wsState(app);
  check('hubOpenFromSidebar', first.dashboard && !first.chat && !!loaded && ws.chat && ws.panes[0].active === LF.CONV.idle && JSON.stringify(openIds(hub)) === JSON.stringify([LF.CONV.idle])
    && loaded[LF.CONV.idle].shown && loaded[LF.CONV.idle].items === LF.liveItems(NOW).length,
  { first: { dashboard: first.dashboard, chat: first.chat }, ws, opens: openIds(hub), conv: loaded && loaded[LF.CONV.idle] });

  // 清單的電腦徽章與標頭的裝置行：名稱照字面（XSS 不執行）、在線小點
  const badges = await page.evaluate(() => [...document.querySelectorAll('.sb-list .crow')].map((r) => {
    const b = r.querySelector('.crow-dev');
    return { title: r.querySelector('.crow-title').textContent, shown: !!b && !b.hidden, name: b ? b.querySelector('.crow-dev-name').textContent : null, online: b ? b.dataset.online : null };
  }));
  await rowOf('筆電上的對話').click();
  const off = await waitLoaded(app, [LF.CONV.offline]);
  const xss = await HP.xssState(app);
  const injected = await page.evaluate(() => document.querySelectorAll('img[src="x"]').length);
  const offRow = badges.find((b) => b.title.startsWith('筆電上的對話'));
  const idleRow = badges.find((b) => b.title === TITLE_OF[LF.CONV.idle]);
  const dev = off && off[LF.CONV.offline].device;
  check('hubDeviceBadgeXss', badges.length === LF.liveList(NOW).length && badges.every((b) => b.shown) && !!offRow && offRow.name === '筆電 ' + LF.XSS && offRow.online === 'false'
    && !!idleRow && idleRow.name === 'OFFICE-PC' && idleRow.online === 'true' && !!dev && dev.text.includes('筆電 ' + LF.XSS) && dev.online === 'false'
    && xss.xss === 0 && xss.calls.length === 0 && injected === 0,
  { badges, device: dev, xss, injected });
  // 電腦離線的對話：狀態列「電腦離線」、不轉圈
  const s = off && off[LF.CONV.offline];
  check('hubStatusOfflineNoSpinner', !!s && s.run === '電腦離線' && s.spin === false, { conv: s });

  // 側欄搜尋（2026-10-23 起瀏覽器版也搜內容）：標題立刻比對；打字停下後送 searchConversations，伺服器的結果補上只有內容符合的對話與片段
  const sidebarSearch = () => page.evaluate(() => ({
    placeholder: document.querySelector('.sb-search-input').placeholder,
    ids: [...document.querySelectorAll('.sb-list .crow')].map((r) => r.querySelector('.crow-title').textContent),
    snippets: [...document.querySelectorAll('.sb-list .crow')].map((r) => { const el = r.querySelector('.crow-snip'); return el && !el.hidden ? el.textContent : null; }),
    marks: [...document.querySelectorAll('.sb-list .crow-snip mark')].map((m) => m.textContent),
    searching: !document.querySelector('.sb-searching').hidden,
    status: document.querySelector('.sb-searching').hidden ? null : document.querySelector('.sb-searching').textContent,
    empty: document.querySelector('.sb-empty').hidden ? null : document.querySelector('.sb-empty').textContent,
  }));
  const sent0 = hub.cmds('searchConversations').length;
  // 伺服器回的片段（XSS 字串照字面顯示）：清單裡標題沒有「報表 匯出」的 idle 對話
  hub.onCmd('searchConversations', (cmd) => ({
    push: [{ type: 'conversationSearch', requestId: cmd.requestId, query: cmd.query, hits: cmd.query.includes('報表') ? [{ conversationId: LF.CONV.idle, snippet: '…把報表匯出成 CSV ' + LF.XSS }] : [] }],
    delayMs: 300,
  }));
  await page.fill('.sb-search-input', '報表 匯出');
  const pending = await until(async () => { const st = await sidebarSearch(); return st.searching && st.status === '搜尋內容中…' ? st : null; }, 2000);
  const hit = await until(async () => { const st = await sidebarSearch(); return !st.searching && st.ids.length === 1 ? st : null; }, 4000);
  const sentHit = hub.cmds('searchConversations').slice(sent0);
  const hubXss = await HP.xssState(app);
  await page.fill('.sb-search-input', '不存在的字');
  const noHit = await until(async () => { const st = await sidebarSearch(); return !st.searching && st.empty ? st : null; }, 4000);
  check('hubSearchContent', !!pending && pending.placeholder === '搜尋標題與內容' && pending.ids.length === 0
    && !!hit && hit.ids[0] === TITLE_OF[LF.CONV.idle] && hit.snippets[0] === '…把報表匯出成 CSV ' + LF.XSS && hit.marks.join('|') === '報表|匯出'
    && sentHit.length === 1 && sentHit[0].cmd.query === '報表 匯出' && typeof sentHit[0].cmd.requestId === 'string'
    && hubXss.xss === 0 && hubXss.calls.length === 0
    && !!noHit && noHit.ids.length === 0 && noHit.empty === '找不到標題或內容含「不存在的字」的對話',
  { pending, hit, sent: sentHit.map((c) => c.cmd), xss: hubXss, noHit });
  hub.onCmd('searchConversations', null);
  await HP.closePage(app);
}

async function runLoadingText() {
  // 伺服器的狀態文字（還在整理、等電腦重新上傳）照樣顯示、不轉圈
  const status = { [LF.CONV.idle]: '伺服器正在整理這個對話', [LF.CONV.view]: '等待電腦重新上傳' };
  const app = await openWithLayout('loading-text', layoutOf([[[LF.CONV.idle], LF.CONV.idle], [[LF.CONV.view], LF.CONV.view]]),
    { serve: { header: (id) => (status[id] ? { status: status[id] } : null) } });
  const loaded = await waitLoaded(app, [LF.CONV.idle, LF.CONV.view]);
  const a = loaded && loaded[LF.CONV.idle];
  const b = loaded && loaded[LF.CONV.view];
  check('hubLoadingText', !!a && a.run === status[LF.CONV.idle] && a.spin === false && !!b && b.run === status[LF.CONV.view] && b.spin === false, { idle: a, view: b });
  await HP.closePage(app);
}

async function runReinit() {
  const layout = layoutOf([[[LF.DRAFT, LF.CONV.idle], LF.CONV.idle], [[LF.CONV.codex], LF.CONV.codex]]);
  const app = await openWithLayout('reinit', layout);
  const { hub } = app;
  const ids = [LF.DRAFT, LF.CONV.idle, LF.CONV.codex];
  const first = await waitLoaded(app, ids);
  await markConv(app, ids);
  await typeInto(app, LF.CONV.idle, '還沒送出的字');
  await typeInto(app, LF.DRAFT, '草稿裡的字');
  // 傳輸層先記一份空的 visible（不經 app.js）：之後 app.js 重送同樣內容的 visible，ClientState 才會變
  const csBefore = clientStates(hub).length;
  await HP.tpost(app, { cmd: 'visible', conversationIds: [] });
  await until(() => clientStates(hub).slice(csBefore).find((s) => s.openConversationIds.length === 0) || null, 2000);
  await sleep(300);
  const n0 = hub.cmds('open').length;
  const cs0 = clientStates(hub).length;
  hub.push(LF.liveBrowserInit(NOW, { mode: 'main' }));
  const reopened = await until(() => (ids.every((id) => countIn(openIds(hub, n0), id) === 1) ? openIds(hub, n0) : null), 4000);
  await sleep(600);
  const after = {};
  for (const id of ids) after[id] = await convState(app, id);
  const ws = await wsState(app);
  const again = openIds(hub, n0);
  check('hubReinitReopens', !!first && !!reopened && again.length === 3 && ids.every((id) => after[id] && after[id].mark === '1' && after[id].loaded)
    && after[LF.CONV.idle].text === '還沒送出的字' && hub.cmds('newConversation').length === 0,
  { again, marks: Object.fromEntries(ids.map((id) => [id.slice(0, 8), after[id] && after[id].mark])), text: after[LF.CONV.idle] && after[LF.CONV.idle].text });
  const resent = clientStates(hub).slice(cs0).map((s) => s.openConversationIds);
  check('hubVisibleResentAfterInit', resent.some((x) => JSON.stringify(x) === JSON.stringify([LF.CONV.idle, LF.CONV.codex])), { clientStatesAfterInit: resent });
  check('hubDraftKeptAfterInit', JSON.stringify(ws.panes[0].tabs) === JSON.stringify([LF.DRAFT, LF.CONV.idle]) && countIn(again, LF.DRAFT) === 1
    && !!after[LF.DRAFT] && after[LF.DRAFT].text === '草稿裡的字',
  { panes: ws.panes, draftOpens: countIn(openIds(hub), LF.DRAFT), draft: after[LF.DRAFT] });
  await HP.closePage(app);
}

async function runDraftRecreate() {
  // 伺服器重開（草稿只在伺服器記憶體）：重新 init 後草稿的 open 回 conversationClosed（沒有 replacedBy）→ 同一位置以同帳戶、同電腦開新草稿、字還原
  const layout = layoutOf([[[LF.CONV.idle, LF.DRAFT], LF.DRAFT]]);
  const app = await openWithLayout('draft-recreate', layout);
  const { hub } = app;
  const NEW = LF.draftId(2);
  await waitLoaded(app, [LF.CONV.idle, LF.DRAFT]);
  await typeInto(app, LF.DRAFT, '伺服器重開前打的字');
  serveOpen(hub, { closed: new Set([LF.DRAFT]) });
  hub.onCmd('newConversation', (cmd) => ({ push: [LF.draftMessage(NOW, NEW, { paneId: cmd.paneId, deviceId: cmd.deviceId || null })] }));
  hub.push(LF.liveBrowserInit(NOW, { mode: 'main' }));
  const got = await until(async () => { const s = await convState(app, NEW); return s && s.loaded ? s : null; }, 5000);
  await sleep(500);
  const ws = await wsState(app);
  const nc = hub.cmds('newConversation').map((c) => c.cmd);
  check('hubDraftRecreatedAfterServerRestart', !!got && nc.length === 1 && nc[0].paneId === 'p1' && nc[0].accountId === 'la_c1' && nc[0].deviceId === LF.DEVICE.control
    && JSON.stringify(ws.panes[0].tabs) === JSON.stringify([LF.CONV.idle, NEW]) && ws.panes[0].active === NEW && got.text === '伺服器重開前打的字'
    && hub.cmds('close').length === 0,
  { newConversation: nc, panes: ws.panes, draft: got, closes: hub.cmds('close').length });
  await HP.closePage(app);
}

/** 網頁版也一樣（2026-11-08 使用者要求）：兩個窗格並排時點了一個就暫時加寬（已經滿高），點側欄就恢復 */
async function runPaneZoom() {
  const app = await openWithLayout('pane-zoom', layoutOf([[[LF.CONV.idle], LF.CONV.idle], [[LF.CONV.codex], LF.CONV.codex]]));
  const { page } = app;
  await waitLoaded(app, [LF.CONV.idle, LF.CONV.codex]);
  const rects = () => page.evaluate(() => Array.from(document.querySelectorAll('#workspace .pane')).map((p) => ({ zoomed: p.classList.contains('zoomed'), w: Math.round(p.getBoundingClientRect().width) })));
  const btn = '#workspace .pane:first-child .conv:not([hidden]) [data-field="paneZoom"]';
  const before = await rects();
  await page.click('#workspace .pane:first-child .scroller', { position: { x: 80, y: 260 } }); // 點對話內容不放大
  await sleep(200);
  const clicked = await rects();
  await page.click(btn);
  await sleep(200);
  const zoomed = await rects();
  await page.click('#sidebar', { position: { x: 20, y: 200 } }); // 點外面不恢復
  await sleep(200);
  const outside = await rects();
  await page.click(btn);
  await sleep(200);
  const after = await rects();
  check('hubPaneZoom', !clicked[0].zoomed && zoomed[0].zoomed && zoomed[0].w > before[0].w * 1.8 && outside[0].zoomed && !after[0].zoomed && after[0].w === before[0].w, { before, clicked, zoomed, outside, after });
  await HP.closePage(app);
}

// 輸入框草稿的同步（瀏覽器版，draftsync.js）：打字停下送 composerDraft 給伺服器；伺服器推來的（其他電腦）沒改過就換上；
// 重連後的整份沒列出（在別處送出了）就清掉
async function runComposerDraft() {
  const app = await openWithLayout('composer-draft', layoutOf([[[LF.CONV.idle], LF.CONV.idle]]));
  const { page, hub } = app;
  await waitLoaded(app, [LF.CONV.idle]);
  const ta = '.conv[data-conv="' + LF.CONV.idle + '"] .cinput';
  await page.click(ta);
  await page.keyboard.type('在手機上打到一半');
  await sleep(1100);
  const sent = hub.cmds('composerDraft').map((c) => c.cmd);
  await page.evaluate(() => document.activeElement && document.activeElement.blur());
  hub.push({ type: 'composerDrafts', full: false, drafts: [{ conversationId: LF.CONV.idle, text: '在桌機上接著打', rev: 10, at: Date.now(), from: '辦公室桌機' }] });
  const applied = await until(() => page.$eval(ta, (t) => (t.value === '在桌機上接著打' ? t.value : null)), 3000);
  await sleep(900);
  const echoed = hub.cmds('composerDraft').length;
  hub.push({ type: 'composerDrafts', full: true, drafts: [] });
  const cleared = await until(() => page.$eval(ta, (t) => (t.value === '' ? 'empty' : null)), 3000);
  check('hubComposerDraft', sent.length === 1 && sent[0].conversationId === LF.CONV.idle && sent[0].text === '在手機上打到一半'
    && applied === '在桌機上接著打' && echoed === 1 && cleared === 'empty', { sent, applied, echoed, cleared });
  await HP.closePage(app);
}

// 臨時對話（瀏覽器版，quickchat.js）：對話鈕送 quickChat、伺服器回帶面板記號的臨時草稿；第一則送出後草稿換成正式對話（replacedBy），
// 面板送 open 嵌回同一個；朗讀由瀏覽器自己念（不送 speakItem）；關掉面板送 delete（已建立的）
// 草稿同步的回歸（2026-11-11 使用者回報：已經送出的字一直出現在瀏覽器版的輸入欄）：別處的草稿換上之後在這裡送出，
// 清掉要馬上送；之後這個對話重新載入（conversation）、或伺服器的整份還帶著那句，都不能再填回輸入框，而且再送一次清掉
async function runComposerDraftSent() {
  const app = await openWithLayout('composer-draft-sent', layoutOf([[[LF.CONV.idle], LF.CONV.idle]]));
  const { page, hub } = app;
  await waitLoaded(app, [LF.CONV.idle]);
  const ta = '.conv[data-conv="' + LF.CONV.idle + '"] .cinput';
  const T = '你要擔任的是一位非常資深的交易員';
  hub.push({ type: 'composerDrafts', full: false, drafts: [{ conversationId: LF.CONV.idle, text: T, rev: 10, at: Date.now(), from: '辦公室桌機' }] });
  const applied = await until(() => page.$eval(ta, (t, v) => (t.value === v ? true : null), T), 3000);
  await page.click(ta);
  await page.keyboard.press('Enter');
  await sleep(250);
  const clears = () => hub.cmds('composerDraft').map((c) => c.cmd).filter((c) => c.text === '');
  const clearedFast = clears().length;
  const sentOk = hub.cmds('send').some((c) => c.cmd.text === T);
  await page.evaluate(() => document.activeElement && document.activeElement.blur());
  hub.push(LF.conversationMessage(NOW, LF.CONV.idle, '整理用量報表', { header: { running: false } }));
  await sleep(600);
  const afterReload = await page.$eval(ta, (t) => t.value);
  hub.push({ type: 'composerDrafts', full: true, drafts: [{ conversationId: LF.CONV.idle, text: T, rev: 10, at: Date.now(), from: '辦公室桌機' }] });
  await sleep(400);
  const afterFull = await page.$eval(ta, (t) => t.value);
  check('hubComposerDraftSentStaysCleared', applied === true && sentOk && clearedFast >= 1 && afterReload === '' && afterFull === '' && clears().length >= 2,
    { applied, sentOk, clearedFast, afterReload, afterFull, clears: clears().length });
  await HP.closePage(app);
}

async function runQuickChat() {
  const app = await openWithLayout('quick-chat', layoutOf([[[LF.CONV.idle], LF.CONV.idle]]));
  const { page, hub } = app;
  await waitLoaded(app, [LF.CONV.idle]);
  const QD = LF.draftId(7);
  hub.onCmd('quickChat', (cmd) => ({ push: [LF.draftMessage(NOW, QD, { paneId: cmd.paneId, header: { ephemeral: true, title: '臨時對話' } })] }));
  await page.click('#btnQuickChat');
  const mounted = await until(() => page.evaluate((id) => { const c = document.querySelector('.qc .conv[data-conv="' + id + '"]'); return c && c.classList.contains('quick') ? true : null; }, QD), 4000);
  const qc = hub.cmds('quickChat').map((c) => c.cmd);
  hub.push({ type: 'conversationClosed', conversationId: QD, replacedBy: LF.CONV.unlisted });
  const swapped = await until(() => page.evaluate((id) => !!document.querySelector('.qc .conv[data-conv="' + id + '"]') || null, LF.CONV.unlisted), 4000);
  const opened = hub.cmds('open').map((c) => c.cmd.conversationId).includes(LF.CONV.unlisted);
  const inPane = await page.evaluate((id) => !!document.querySelector('#workspace .conv[data-conv="' + id + '"]'), LF.CONV.unlisted);
  await page.click('.qc [data-field="quickClose"]');
  await sleep(300);
  const del = hub.cmds('delete').map((c) => c.cmd);
  const hidden = await page.evaluate(() => document.querySelector('.qc').hidden);
  check('hubQuickChat', !!mounted && qc.length === 1 && qc[0].paneId === 'quick:dock' && !!swapped && opened && !inPane && hidden
    && del.length === 1 && del[0].conversationId === LF.CONV.unlisted && hub.cmds('quickChatEnd').length === 0,
  { mounted, qc, swapped, opened, inPane, del, hidden });
  hub.onCmd('quickChat', null);
  await HP.closePage(app);
}

async function runClosedClosesPane() {
  const app = await openWithLayout('closed-closes-pane', layoutOf([[[LF.CONV.idle], LF.CONV.idle], [[LF.CONV.codex], LF.CONV.codex]]));
  const { hub } = app;
  await waitLoaded(app, [LF.CONV.idle, LF.CONV.codex]);
  const before = await wsState(app);
  hub.push({ type: 'conversationClosed', conversationId: LF.CONV.codex });
  const after = await until(async () => { const s = await wsState(app); return s.panes.length === 1 ? s : null; }, 3000);
  check('hubConversationClosedClosesPane', before.panes.length === 2 && !!after && JSON.stringify(after.panes[0].tabs) === JSON.stringify([LF.CONV.idle]) && hub.cmds('close').length === 0
    && (await convState(app, LF.CONV.codex)) === null,
  { before: before.panes, after: after && after.panes, closes: hub.cmds('close').length });
  await HP.closePage(app);
}

/** 瀏覽器版的排程（最少的形狀：頂部列顯示「排程」鈕；排程有 1 個失敗，徽章也顯示） */
const PHONE_VIEWS = {
  schedules: { machine: null, entries: [], devices: [], failing: 1 },
};

async function runChat390() {
  // 頂部列帶齊瀏覽器版現在有的檢視鈕（排程 sched1）：2026-10-27 iPhone 上頂部列超出畫面，Safari 把整頁縮小、右邊與下面空一塊
  const layout = layoutOf([[[LF.CONV.offline], LF.CONV.offline]]);
  const app = await openWithLayout('chat-390', layout, { open: { vp: { w: 390, h: 844 },
    init: (ready) => Object.assign(LF.liveBrowserInit(NOW, { mode: ready && ready.mode, layout }), PHONE_VIEWS) } });
  await waitLoaded(app, [LF.CONV.offline]);
  // 側欄開著（窄螢幕是蓋在工作區上的抽屜）與收起來各一張
  await shot(app, 'hub-chat-390-sidebar');
  const withSidebar = await hScroll(app);
  await app.page.click('#btnSidebar');
  await sleep(200);
  await shot(app, 'hub-chat-390');
  const hs = await hScroll(app);
  // 頂部列：三顆檢視鈕都在（只剩圖示、各有提示），再窄到 360px 也放得下
  const views = await app.page.evaluate(() => [...document.querySelectorAll('#viewSwitch [data-view]')].filter((b) => !b.hidden)
    .map((b) => ({ view: b.dataset.view, w: Math.round(b.getBoundingClientRect().width), title: b.title, icon: (b.querySelector('.ic')?.getBoundingClientRect().width || 0) >= 12 })));
  await app.page.setViewportSize({ width: 360, height: 780 });
  await sleep(200);
  // 360px：整頁不變寬、頂部列的東西都在畫面內（對話裡「最近的指令」的跑馬燈文字本來就超出它自己的框，不算）
  const hs360 = await hScroll(app);
  const top360 = await app.page.evaluate(() => [...document.querySelectorAll('.topbar *')]
    .filter((el) => { const r = el.getBoundingClientRect(); return r.width > 0 && r.right > window.innerWidth + 1; }).length);
  const ok = (x) => x.docScroll <= x.innerWidth && x.bodyScroll <= x.innerWidth && x.wide.length === 0;
  check('hubChat390NoHScroll', ok(withSidebar) && ok(hs) && hs360.docScroll <= 360 && hs360.bodyScroll <= 360 && top360 === 0
    && views.length === 3 && views.every((v) => v.w >= 28 && v.w <= 40 && v.icon && !!v.title),
    { withSidebar, collapsed: hs, at360: { ...hs360, topbarOut: top360 }, views });
  // 整頁不縮放（2026-10-27 使用者要求：手機上點兩下整個畫面被放大）：viewport 不准縮放、整頁 touch-action 是 manipulation（關掉雙擊縮放）、
  // iOS 的捏合 gesture 事件被擋下（Chromium 沒有這個事件，以合成的事件驗監聽者有 preventDefault）
  const zoom = await app.page.evaluate(() => {
    const meta = document.querySelector('meta[name="viewport"]').content;
    const ev = new Event('gesturestart', { bubbles: true, cancelable: true });
    document.body.dispatchEvent(ev);
    return { meta, touchAction: getComputedStyle(document.documentElement).touchAction, gesturePrevented: ev.defaultPrevented };
  });
  check('hubPhoneNoPageZoom', /user-scalable=no/.test(zoom.meta) && /maximum-scale=1(,|$)/.test(zoom.meta) && /viewport-fit=cover/.test(zoom.meta)
    && zoom.touchAction === 'manipulation' && zoom.gesturePrevented, zoom);
  await HP.closePage(app);
}

async function runMobileComposer() {
  // 手機（390×664、觸控）：整頁剛好是看得到的高度、輸入框在畫面內而且打得了字，字級至少 16px（iOS 點進去不自動放大）。
  // iOS 的 100vh 比看得到的區域高（輸入區被瀏覽器工具列蓋住）這件事 Chromium 模擬不出來，由 live-rules 的 CSS 掃描守（vh 一律配 dvh）
  const id = LF.CONV.idle;
  const app = await openWithLayout('mobile-composer', layoutOf([[[id], id]]), { open: { vp: { w: 390, h: 664 }, hasTouch: true } });
  await waitLoaded(app, [id]);
  const { page } = app;
  await page.click('#btnSidebar'); // 窄螢幕的側欄是蓋在工作區上的抽屜，先收起來（同 runChat390）
  await sleep(200);
  const layout = await page.evaluate((sel) => {
    const app = document.querySelector('.app');
    const ta = document.querySelector(sel);
    const r = ta ? ta.getBoundingClientRect() : null;
    return {
      innerHeight: window.innerHeight, appHeight: app ? Math.round(app.getBoundingClientRect().height) : null,
      coarse: matchMedia('(hover: none) and (pointer: coarse)').matches,
      ta: r ? { top: Math.round(r.top), bottom: Math.round(r.bottom), fontSize: parseFloat(getComputedStyle(ta).fontSize) } : null,
    };
  }, taSel(id));
  await shot(app, 'hub-mobile-composer');
  await page.tap(taSel(id));
  await page.keyboard.type('手機輸入測試');
  const typed = await page.evaluate((sel) => { const ta = document.querySelector(sel); return { value: ta.value, focused: document.activeElement === ta }; }, taSel(id));
  check('hubMobileComposer', layout.appHeight === layout.innerHeight && layout.coarse && !!layout.ta && layout.ta.top >= 0 && layout.ta.bottom <= layout.innerHeight
    && layout.ta.fontSize >= 16 && typed.value === '手機輸入測試' && typed.focused, { layout, typed });
  // 觸控操作的 Enter 只換行、不送出（sendrules.enterSends，2026-10-29 使用者要求）：設定開著 Enter 送出也一樣，送出鈕照樣送得出去
  await page.keyboard.press('Enter');
  await page.keyboard.type('第二行');
  await sleep(300);
  const sendSel = '.conv[data-conv="' + id + '"] .cbtn.send';
  const composerOf = () => page.evaluate(([t, b]) => {
    const ta = document.querySelector(t), send = document.querySelector(b);
    return { value: ta.value, sendTitle: send ? send.title : null };
  }, [taSel(id), sendSel]);
  const afterEnter = await composerOf();
  await page.tap(sendSel);
  await sleep(500);
  const afterSend = await composerOf();
  check('hubMobileEnterNewline', afterEnter.value === '手機輸入測試' + String.fromCharCode(10) + '第二行' && afterEnter.sendTitle === '送出' && afterSend.value === '',
    { afterEnter, afterSend });
  await HP.closePage(app);
}

/** 燈箱用的大圖（1600×1200 的 SVG：手機寬度符合視窗約 21%，捏合放大才看得出差別） */
const BIG_IMAGE = '<svg xmlns="http://www.w3.org/2000/svg" width="1600" height="1200" viewBox="0 0 1600 1200">'
  + '<defs><pattern id="g" width="100" height="100" patternUnits="userSpaceOnUse"><rect width="50" height="50" fill="#fff"/><rect x="50" y="50" width="50" height="50" fill="#fff"/></pattern></defs>'
  + '<rect width="1600" height="1200" fill="#2f6fd0"/><rect width="1600" height="1200" fill="url(#g)" opacity=".22"/>'
  + '<circle cx="800" cy="600" r="260" fill="none" stroke="#ffd34d" stroke-width="18"/><text x="800" y="632" font-size="96" text-anchor="middle" fill="#fff">1600×1200</text></svg>';

/** 燈箱的圖片在舞台上的位置與大小、倍率（寬÷原始寬）、標示的百分比、整頁的縮放；從縮圖放大的動畫還在播時回 null（位置還不是最後的） */
const lbView = (app) => app.page.evaluate(() => {
  const lb = document.querySelector('.lb:not(.leaving)');
  const img = lb && lb.querySelector('.lb-img');
  const stage = lb && lb.querySelector('.lb-stage');
  if (!img || !stage || !img.naturalWidth || img.style.visibility === 'hidden' || img.getAnimations().some((a) => a.playState === 'running')) return null;
  const r = img.getBoundingClientRect(), s = stage.getBoundingClientRect();
  return {
    left: r.left - s.left, top: r.top - s.top, width: r.width, height: r.height, scale: r.width / img.naturalWidth, nw: img.naturalWidth,
    label: lb.querySelector('.lb-zoom').textContent, stage: { x: s.left, y: s.top, w: s.width, h: s.height },
    count: lb.querySelector('.lb-count').textContent, nav: !lb.querySelector('.lb-nav.prev').hidden,
    pageScale: window.visualViewport ? window.visualViewport.scale : 1,
  };
});

/** 燈箱的大圖換成 SVG 的媒體回應（want 是要換的路徑） */
const bigImageAt = (want) => (p) => (p === want ? { status: 200, headers: { 'Content-Type': 'image/svg+xml', 'Cache-Control': 'no-store' }, body: BIG_IMAGE } : null);

/**
 * 觸控螢幕上的燈箱（手機、平板、觸控筆電上的桌面 app 都是同一份 viewer.js，不看螢幕寬度）：點 thumb 打開，兩指捏合以兩指中點為中心放大、
 * 單指拖曳平移、雙點切換符合視窗／100%（點的位置留在手指底下，圖片邊緣照樣不離開視窗）、捏得比符合視窗小放開時彈回、
 * 有上一張／下一張時手指連點照常換圖不當成雙點、以上都不關燈箱也不整頁放大，點圖片外的背景才關。
 * 觸控以 CDP 的 Input.dispatchTouchEvent 送（Playwright 的 touchscreen 只有 tap）。回傳 { ok, result }
 */
async function lightboxTouch(app, thumb, shotName) {
  const { page } = app;
  await page.tap(thumb);
  const fit = await until(() => lbView(app), 4000);
  const cdp = await page.context().newCDPSession(page);
  const touch = (type, pts) => cdp.send('Input.dispatchTouchEvent', { type, touchPoints: pts.map((p) => ({ x: p.x, y: p.y, id: p.id })) });
  // 兩指：先放一指再放第二指，距離從 from 拉到 to（中點 c 不動），最後一起放開
  const pinch = async (c, from, to, steps = 10) => {
    const at = (d) => [{ x: c.x - d / 2, y: c.y, id: 0 }, { x: c.x + d / 2, y: c.y, id: 1 }];
    await touch('touchStart', at(from).slice(0, 1));
    await touch('touchStart', at(from));
    for (let i = 1; i <= steps; i++) { await touch('touchMove', at(from + (to - from) * i / steps)); await sleep(16); }
    await touch('touchEnd', []);
    await sleep(150);
  };
  const drag = async (p, dx, dy, steps = 8) => {
    await touch('touchStart', [{ x: p.x, y: p.y, id: 0 }]);
    for (let i = 1; i <= steps; i++) { await touch('touchMove', [{ x: p.x + dx * i / steps, y: p.y + dy * i / steps, id: 0 }]); await sleep(16); }
    await touch('touchEnd', []);
    await sleep(150);
  };
  const doubleTap = async (p) => {
    for (let i = 0; i < 2; i++) { await touch('touchStart', [{ x: p.x, y: p.y, id: 0 }]); await sleep(30); await touch('touchEnd', []); await sleep(90); }
    await sleep(450); // 瀏覽器自己的 dblclick 若也觸發、又切換一次，這段時間內就會看到
  };
  const result = { fit };
  if (fit) {
    const st = fit.stage;
    // 1. 捏合：中點在圖片左邊 30%、上下置中的一點（不在舞台正中，看得出錨點；放大後不碰到邊界），兩指距離 60→230
    const c = { x: st.x + fit.left + fit.width * 0.3, y: st.y + fit.top + fit.height * 0.5 };
    const ix = (c.x - st.x - fit.left) / fit.scale, iy = (c.y - st.y - fit.top) / fit.scale;
    await pinch(c, 60, 230);
    const zoomed = await lbView(app);
    result.pinch = zoomed && { scale: zoomed.scale, label: zoomed.label, anchorDx: st.x + zoomed.left + ix * zoomed.scale - c.x, anchorDy: st.y + zoomed.top + iy * zoomed.scale - c.y, pageScale: zoomed.pageScale };
    await shot(app, shotName);
    // 2. 單指往左上拖 100、60：圖片跟著走（放大後比舞台大、不碰到邊界）
    await drag({ x: st.x + st.w / 2, y: st.y + st.h / 2 }, -100, -60);
    const panned = await lbView(app);
    result.pan = panned && zoomed && { dx: panned.left - zoomed.left, dy: panned.top - zoomed.top, scale: panned.scale };
    // 3. 雙點：放大中→符合視窗；再雙點→100%，點的位置留在手指底下（圖片邊緣不離開視窗時被推回，和燈箱的 constrain 同一個算法）
    await doubleTap({ x: st.x + st.w / 2, y: st.y + st.h / 2 });
    const back = await lbView(app);
    const p = { x: st.x + fit.left + fit.width * 0.7, y: st.y + fit.top + fit.height * 0.6 };
    await doubleTap(p);
    const full = await lbView(app);
    const want = (pt, fitPos, len, stageLen) => (len <= stageLen ? (stageLen - len) / 2 : Math.min(0, Math.max(stageLen - len, pt - (pt - fitPos) / fit.scale)));
    result.doubleTap = {
      back: back && { scale: back.scale, label: back.label },
      full: full && { scale: full.scale, label: full.label, dx: full.left - want(p.x - st.x, fit.left, full.width, st.w), dy: full.top - want(p.y - st.y, fit.top, full.height, st.h) },
    };
    // 4. 捏得比符合視窗小（距離 300→20），放開時彈回符合視窗（手機上第一指落在「上一張」鈕上）
    await pinch({ x: st.x + st.w / 2, y: st.y + st.h / 2 }, 300, 20);
    const snapped = await lbView(app);
    result.snap = snapped && { scale: snapped.scale, label: snapped.label };
    // 5. 有上一張／下一張時：手指連點（300 毫秒內）照常換圖再換回來，不當成雙點放大
    if (fit.nav) {
      const navAt = (sel) => page.evaluate((q) => { const r = document.querySelector(q).getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; }, sel);
      const prevAt = await navAt('.lb .lb-nav.prev'), nextAt = await navAt('.lb .lb-nav.next');
      await page.touchscreen.tap(prevAt.x, prevAt.y);
      const countPrev = await page.textContent('.lb .lb-count');
      await sleep(100);
      await page.touchscreen.tap(nextAt.x, nextAt.y);
      const navBack = await until(async () => { const v = await lbView(app); return v && v.count === fit.count && Math.abs(v.scale - fit.scale) < 1e-3 ? v : null; }, 3000);
      result.nav = { countPrev, back: navBack && { count: navBack.count, label: navBack.label } };
    }
    // 6. 以上都沒有關掉燈箱；點圖片外的背景才關（圖片上方有空就點上方，否則點左上角）
    result.openBeforeTap = await lightboxOpen(app);
    const bg = fit.top >= 40 ? { x: st.x + st.w / 2, y: st.y + fit.top / 2 } : { x: st.x + Math.max(4, fit.left / 2), y: st.y + 30 };
    await page.touchscreen.tap(bg.x, bg.y);
    result.closedByTap = !!(await until(async () => ((await lightboxOpen(app)) ? null : true), 2000));
  }
  await cdp.detach().catch(() => {});
  const r = result;
  const fitScale = fit && fit.scale;
  const ok = !!fit && fitScale < 0.7
    && !!r.pinch && r.pinch.scale > fitScale * 3 && Math.abs(r.pinch.anchorDx) < 4 && Math.abs(r.pinch.anchorDy) < 4 && r.pinch.pageScale === 1
    && !!r.pan && Math.abs(r.pan.dx + 100) < 3 && Math.abs(r.pan.dy + 60) < 3 && Math.abs(r.pan.scale - r.pinch.scale) < 1e-3
    && !!r.doubleTap.back && Math.abs(r.doubleTap.back.scale - fitScale) < 1e-3
    && !!r.doubleTap.full && r.doubleTap.full.label === '100%' && Math.abs(r.doubleTap.full.dx) < 4 && Math.abs(r.doubleTap.full.dy) < 4
    && !!r.snap && Math.abs(r.snap.scale - fitScale) < 1e-3
    && (!fit.nav || (r.nav.countPrev !== fit.count && !!r.nav.back))
    && r.openBeforeTap && r.closedByTap;
  return { ok, result };
}

async function runMobileLightbox() {
  // 手機（390×844、觸控）：符合視窗約 21%，這個對話有兩張圖（有上一張／下一張鈕，捏小那一次第一指落在「上一張」鈕上）
  const id = LF.CONV.idle;
  const app = await openWithLayout('mobile-lightbox', layoutOf([[[id], id]]), { open: { vp: { w: 390, h: 844 }, hasTouch: true, media: bigImageAt('/m/k3b') } });
  await waitLoaded(app, [id]);
  await app.page.click('#btnSidebar'); // 窄螢幕的側欄是蓋在工作區上的抽屜，先收起來（同 runMobileComposer）
  await sleep(300);
  const t = await lightboxTouch(app, itemSel(id, 'i2') + ' .media .mimg', 'hub-mobile-lightbox-pinch');
  check('hubMobileLightboxTouch', t.ok && t.result.fit.nav && !!t.result.nav, t.result);
  await HP.closePage(app);
}

async function runTabletLightbox() {
  // 平板（1024×768、觸控，側欄照常顯示）：不是手機寬度也一樣能捏合
  const id = LF.CONV.idle;
  const app = await openWithLayout('tablet-lightbox', layoutOf([[[id], id]]), { open: { vp: { w: 1024, h: 768 }, hasTouch: true, media: bigImageAt('/m/k3b') } });
  await waitLoaded(app, [id]);
  const t = await lightboxTouch(app, itemSel(id, 'i2') + ' .media .mimg', 'hub-tablet-lightbox-pinch');
  check('hubTabletLightboxTouch', t.ok, t.result);
  await HP.closePage(app);
}

async function runDesktopLightboxTouch() {
  // 桌面 app 在觸控筆電上（WebView2 模擬、1440×900、觸控）：本機對話 c1 的圖片（/media），只有一張、沒有上一張／下一張
  const app = await openDesktopLive('desktop-lightbox-touch', { init: { layout: desktopLayout(['c1']) }, vp: { w: 1440, h: 900 }, hasTouch: true, media: bigImageAt('/media') });
  const thumb = convSel('c1') + ':not(.loading) .it[data-id="l2"] .mimg';
  await until(() => app.page.evaluate((s) => !!document.querySelector(s), thumb), 4000);
  const t = await lightboxTouch(app, thumb, 'desktop-lightbox-touch-pinch');
  check('desktopLightboxTouch', t.ok, t.result);
  await HP.closePage(app);
}

// ───── 燈箱與影片播放器：右上角的關閉鈕、觸控拖到旁邊關閉、右鍵與長按的媒體選單（2026-11-01 使用者要求；viewer.js、mediamenu.js、longpress.js）─────

/** 檢視器（sel：'.lb'／'.vp'）右上角的關閉鈕：位置、大小、正中心點得到的是不是它 */
const closeBtnState = (app, sel) => app.page.evaluate((q) => {
  const b = document.querySelector(q + ':not(.leaving) .lb-close');
  if (!b) return null;
  const r = b.getBoundingClientRect();
  const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
  return { left: Math.round(r.left), right: Math.round(r.right), top: Math.round(r.top), bottom: Math.round(r.bottom), w: Math.round(r.width), vw: innerWidth,
    hit: !!hit && b.contains(hit), label: b.getAttribute('aria-label') };
}, sel);
/** 關閉鈕在視窗的右上角（右緣離視窗右緣 24px 內、在工具列的高度內）、至少 34px、點得到 */
const closeBtnOk = (c) => !!c && c.hit && c.right <= c.vw && c.right >= c.vw - 24 && c.top >= 0 && c.bottom <= 64 && c.w >= 34;

/** 媒體選單的項目（沒開時 null） */
const mediaMenuState = (app) => app.page.evaluate(() => {
  const m = document.querySelector('.menu.media-menu');
  return m ? [...m.querySelectorAll('.menu-item')].map((n) => ({ label: n.querySelector('.mi-label').textContent, disabled: n.disabled })) : null;
});
const menuLabels = (m) => (m || []).map((x) => x.label);

/** CDP 的觸控（Playwright 的 touchscreen 只有 tap）：drag 每一步隔 stepMs、拖到一半時呼叫 mid()；hold 按著 ms 毫秒、放開前呼叫 during() */
async function touchKit(page) {
  const cdp = await page.context().newCDPSession(page);
  const touch = (type, pts) => cdp.send('Input.dispatchTouchEvent', { type, touchPoints: pts.map((p) => ({ x: p.x, y: p.y, id: 0 })) });
  return {
    async drag(p, dx, dy, steps = 8, stepMs = 16, mid = null) {
      await touch('touchStart', [p]);
      let seen = null;
      for (let i = 1; i <= steps; i++) {
        await touch('touchMove', [{ x: p.x + dx * i / steps, y: p.y + dy * i / steps }]);
        await sleep(stepMs);
        if (mid && i === Math.ceil(steps / 2)) seen = await mid();
      }
      await touch('touchEnd', []);
      return seen;
    },
    async hold(p, ms = 700, during = null) {
      await touch('touchStart', [p]);
      await sleep(ms);
      const seen = during ? await during() : null;
      await touch('touchEnd', []);
      return seen;
    },
    detach: () => cdp.detach().catch(() => {}),
  };
}

/**
 * 觸控螢幕上的燈箱：右上角的關閉鈕；拖一小段放開放回原位；長按圖片開媒體選單（放開手指不關燈箱、圖片沒動），
 * 選「複製圖片」以 PNG 寫進剪貼簿（SVG 在 canvas 上轉）；往旁邊拖，拖到一半圖片跟著手指走、背景與工具列變淡，放開就關閉。
 * save 是選單裡另存的字（瀏覽器版「下載」、桌面「另存新檔」）
 */
async function lightboxSwipeMenu(app, thumb, shotName, save) {
  const { page } = app;
  await page.tap(thumb);
  const fit = await until(() => lbView(app), 4000);
  const k = await touchKit(page);
  const r = { fit };
  if (fit) {
    const st = fit.stage;
    const c = { x: st.x + fit.left + fit.width / 2, y: st.y + fit.top + fit.height / 2 };
    r.close = await closeBtnState(app, '.lb');
    await shot(app, shotName + '-close');
    // 1. 往右慢慢拖 40px 放開：沒拖夠遠，放回原位、燈箱還開著
    await k.drag(c, 40, 0, 6, 45);
    await sleep(350);
    const back = await until(() => lbView(app), 2000);
    r.short = { open: await lightboxOpen(app), dx: back && back.left - fit.left, dy: back && back.top - fit.top,
      bg: await page.evaluate(() => document.querySelector('.lb').style.backgroundColor) };
    // 2. 長按圖片：開媒體選單；放開手指後燈箱還開著、圖片沒動；選「複製圖片」
    const w0 = (await HP.clipboardWrites(app)).length;
    r.holdMenu = await k.hold(c, 700, () => mediaMenuState(app));
    await sleep(300);
    r.menu = await mediaMenuState(app);
    r.openAfterHold = await lightboxOpen(app);
    const still = await lbView(app);
    r.holdMoved = still ? Math.abs(still.left - fit.left) + Math.abs(still.top - fit.top) : null;
    await shot(app, shotName + '-menu');
    if (r.menu) await page.locator('.media-menu .menu-item', { hasText: '複製圖片' }).tap();
    r.clip = await until(async () => (await HP.clipboardWrites(app)).slice(w0).find((x) => x.items) || null, 3000);
    r.toast = await until(async () => (await HP.domToasts(app)).find((t) => t === '已複製圖片') || null, 2000);
    r.menuClosed = !(await mediaMenuState(app));
    r.openAfterCopy = await lightboxOpen(app);
    // 3. 往右拖 180px：拖到一半圖片跟著手指走、背景與工具列變淡；放開就關閉
    r.mid = await k.drag(c, 180, 0, 8, 16, () => page.evaluate(() => {
      const lb = document.querySelector('.lb');
      return { left: lb.querySelector('.lb-img').getBoundingClientRect().left, bg: lb.style.backgroundColor, bar: Number(getComputedStyle(lb.querySelector('.lb-bar')).opacity) };
    }));
    if (r.mid) r.mid.dx = r.mid.left - (st.x + fit.left);
    r.closedBySwipe = !!(await until(async () => ((await lightboxOpen(app)) ? null : true), 2000));
  }
  await k.detach();
  const clip = r.clip && r.clip.items && r.clip.items[0];
  const ok = !!fit && closeBtnOk(r.close)
    && r.short.open && Math.abs(r.short.dx) < 1 && Math.abs(r.short.dy) < 1 && r.short.bg === ''
    && !!r.holdMenu && JSON.stringify(menuLabels(r.menu).slice(0, 2)) === JSON.stringify(['複製圖片', save]) && r.openAfterHold && r.holdMoved < 1
    && !!clip && r.clip.ok && clip.types[0] === 'image/png' && clip.blobTypes[0] === 'image/png' && clip.sizes[0] > 100 && !!r.toast && r.menuClosed && r.openAfterCopy
    && !!r.mid && r.mid.dx > 60 && r.mid.bar < 0.95 && /rgba/.test(r.mid.bg) && r.closedBySwipe;
  return { ok, result: r };
}

async function runMobileLightboxSwipeMenu() {
  // 手機（390×844、觸控）：瀏覽器版對話的大圖（/m/k3b，SVG）
  const id = LF.CONV.idle;
  const app = await openWithLayout('mobile-lightbox-swipe', layoutOf([[[id], id]]), { open: { vp: { w: 390, h: 844 }, hasTouch: true, media: bigImageAt('/m/k3b') } });
  await waitLoaded(app, [id]);
  await app.page.click('#btnSidebar');
  await sleep(300);
  const t = await lightboxSwipeMenu(app, itemSel(id, 'i2') + ' .media .mimg', 'hub-mobile-lightbox-swipe', '下載');
  check('hubMobileLightboxSwipeMenu', t.ok, t.result);
  await HP.closePage(app);
}

async function runDesktopLightboxSwipeMenu() {
  // 桌面 app 在觸控筆電上（1440×900、觸控）：本機對話 c1 的圖片（/media）；另存是「另存新檔」
  const app = await openDesktopLive('desktop-lightbox-swipe', { init: { layout: desktopLayout(['c1']) }, vp: { w: 1440, h: 900 }, hasTouch: true, media: bigImageAt('/media') });
  const thumb = convSel('c1') + ':not(.loading) .it[data-id="l2"] .mimg';
  await until(() => app.page.evaluate((s) => !!document.querySelector(s), thumb), 4000);
  const t = await lightboxSwipeMenu(app, thumb, 'desktop-lightbox-swipe', '另存新檔');
  check('desktopLightboxSwipeMenu', t.ok, t.result);
  await HP.closePage(app);
}

/** 對話裡有圖片與影片的那一則（瀏覽器版的網址） */
const MEDIA_ITEMS = (now) => [
  { id: 'v1', kind: 'User', status: 'Done', time: now - 3 * LF.MIN, html: '<p>錄一段畫面給我看</p>', attachments: [], seq: 1 },
  { id: 'v2', kind: 'Assistant', status: 'Done', time: now - 2 * LF.MIN, html: '<p>錄好了：</p>', seq: 2,
    media: [{ url: '/m/d_A-img1', kind: 'image', name: '畫面.png' }, { url: '/m/d_A-vid1', kind: 'video', name: '操作錄影.mp4' }] },
];

/** 對話裡的影片（iframe）在畫面上的位置 */
const frameRect = (app, sel) => app.page.evaluate((q) => {
  const f = document.querySelector(q);
  if (!f) return null;
  const r = f.getBoundingClientRect();
  return { x: r.left, y: r.top, w: r.width, h: r.height };
}, sel);

async function runMobileMediaTouch() {
  // 手機（390×844、觸控）：對話裡的圖片與影片長按開媒體選單（不打開燈箱、不播放）；播放器右上角有關閉鈕、拖一小段放回原位、拖到旁邊關閉
  const id = LF.CONV.idle;
  const app = await openWithLayout('mobile-media-touch', layoutOf([[[id], id]]), {
    open: {
      vp: { w: 390, h: 844 }, hasTouch: true,
      hub: (hub) => hub.onCmd('open', (cmd) => ({ push: [LF.conversationMessage(NOW, cmd.conversationId, TITLE_OF[cmd.conversationId] || '對話', { items: MEDIA_ITEMS(NOW) })] })),
    },
  });
  // 探針的 /m/ 一律回 PNG：影片放不出來（只要 iframe 與影片元素在就驗得了手勢）
  app.expect((m) => /\/m\/d_A-vid1/.test(m.text || '') || /\/m\/d_A-vid1/.test(m.url || ''), '探針的影片網址回 PNG，影片放不出來');
  const { page } = app;
  await waitLoaded(app, [id]);
  await page.click('#btnSidebar');
  await sleep(300);
  const k = await touchKit(page);
  const r = {};
  // 1. 長按對話裡的圖片：媒體選單（複製圖片、下載），不打開燈箱
  const img = await frameRect(app, itemSel(id, 'v2') + ' .media .mimg');
  await page.evaluate((q) => document.querySelector(q).scrollIntoView({ block: 'center' }), itemSel(id, 'v2') + ' .media');
  await sleep(200);
  const imgAt = await frameRect(app, itemSel(id, 'v2') + ' .media .mimg');
  if (imgAt) {
    await k.hold({ x: imgAt.x + imgAt.w / 2, y: imgAt.y + imgAt.h / 2 }, 700);
    await sleep(400);
    r.imageMenu = await mediaMenuState(app);
    r.lightboxAfterImageHold = await lightboxOpen(app);
    await shot(app, 'hub-mobile-media-image-menu');
    await page.keyboard.press('Escape');
    await sleep(150);
  }
  // 2. 長按對話裡的影片（iframe 裡）：媒體選單（複製影片停用、說明要下載；下載）
  const vf = await frameRect(app, itemSel(id, 'v2') + ' .mvid iframe.vframe');
  if (vf) {
    await k.hold({ x: vf.x + vf.w / 2, y: vf.y + vf.h * 0.35 }, 700);
    await sleep(400);
    r.videoMenu = await mediaMenuState(app);
    r.videoMenuDesc = await page.evaluate(() => { const d = document.querySelector('.media-menu .menu-item .mi-desc'); return d ? d.textContent : null; });
    await shot(app, 'hub-mobile-media-video-menu');
    await page.keyboard.press('Escape');
    await sleep(150);
  }
  // 3. 放大播放：右上角的關閉鈕
  await page.tap(itemSel(id, 'v2') + ' .mvid-cap button[aria-label="放大播放"]');
  await page.waitForSelector('.vp:not(.leaving) iframe.vframe');
  await sleep(300);
  r.close = await closeBtnState(app, '.vp');
  await shot(app, 'hub-mobile-video-player');
  // 4. 在影片上（控制列以外）往右慢慢拖 40px：放回原位、播放器還開著；長按影片開媒體選單
  const pv = await frameRect(app, '.vp iframe.vframe');
  if (pv) {
    const c = { x: pv.x + pv.w / 2, y: pv.y + pv.h * 0.35 };
    await k.drag(c, 40, 0, 6, 45);
    await sleep(350);
    r.short = await page.evaluate(() => { const vp = document.querySelector('.vp:not(.leaving)'); return vp ? { open: true, transform: vp.querySelector('iframe.vframe').style.transform, bg: vp.style.backgroundColor } : { open: false }; });
    await k.hold(c, 700);
    await sleep(300);
    r.playerMenu = await mediaMenuState(app);
    await page.keyboard.press('Escape');
    await sleep(150);
    // 5. 往右拖 200px：拖到一半影片跟著手指走，放開就關閉
    r.mid = await k.drag(c, 200, 0, 8, 16, () => frameRect(app, '.vp iframe.vframe'));
    if (r.mid) r.mid.dx = r.mid.x - pv.x;
    r.closedBySwipe = !!(await until(() => page.evaluate(() => (document.querySelector('.vp') ? null : true)), 2000));
  }
  await k.detach();
  check('hubMobileMediaLongPress', JSON.stringify(menuLabels(r.imageMenu)) === JSON.stringify(['複製圖片', '下載']) && r.lightboxAfterImageHold === false
    && JSON.stringify(menuLabels(r.videoMenu)) === JSON.stringify(['複製影片', '下載']) && r.videoMenu[0].disabled && /下載/.test(r.videoMenuDesc || ''),
  { imageMenu: r.imageMenu, lightbox: r.lightboxAfterImageHold, videoMenu: r.videoMenu, desc: r.videoMenuDesc, img });
  check('hubMobileVideoSwipe', closeBtnOk(r.close) && !!r.short && r.short.open && !r.short.transform && !r.short.bg
    && JSON.stringify(menuLabels(r.playerMenu)) === JSON.stringify(['複製影片', '下載'])
    && !!r.mid && r.mid.dx > 60 && r.closedBySwipe,
  { close: r.close, short: r.short, playerMenu: r.playerMenu, mid: r.mid, closed: r.closedBySwipe });
  await HP.closePage(app);
}

async function runDesktopMediaMenu() {
  // 桌面（滑鼠）：對話裡的圖片與影片按右鍵開媒體選單（不打開燈箱），選項帶這台的路徑與對話 id；燈箱與播放器右上角的關閉鈕，視窗窄到 420px 也在
  const lv = { id: 'lv', kind: 'Assistant', status: 'Done', time: NOW - LF.MIN, html: '<p>錄好了</p>',
    media: [{ url: '/media?p=vid', kind: 'video', name: '操作錄影.mp4', path: 'C:\\repo\\demo\\操作錄影.mp4' }] };
  const app = await openDesktopLive('desktop-media-menu', { init: { layout: desktopLayout(['c1']) }, vp: { w: 1440, h: 900 },
    host: { items: (cid) => (cid === 'c1' ? LF.desktopItems(NOW, 'l').concat([lv]) : undefined) } });
  const { page } = app;
  const thumb = convSel('c1') + ':not(.loading) .it[data-id="l2"] .mimg';
  const vsel = convSel('c1') + ' .it[data-id="lv"] .mvid iframe.vframe';
  await until(() => page.evaluate((s) => !!document.querySelector(s), vsel), 4000);
  const r = {};
  // 1. 圖片按右鍵：複製圖片、另存新檔、用預設程式開啟、在檔案總管顯示；不打開燈箱；另存新檔送 saveFile（路徑＋對話 id）
  await page.click(thumb, { button: 'right' });
  r.imageMenu = await until(() => mediaMenuState(app), 2000);
  r.lightbox = await lightboxOpen(app);
  await shot(app, 'desktop-media-image-menu');
  const s0 = postedOf(app, 'saveFile').length;
  if (r.imageMenu) await page.locator('.media-menu .menu-item', { hasText: '另存新檔' }).click();
  r.save = await until(() => postedOf(app, 'saveFile').slice(s0)[0] || null, 2000);
  // 2. 影片（iframe 裡）按右鍵：複製影片送 copyMedia
  await page.evaluate((q) => document.querySelector(q).scrollIntoView({ block: 'center' }), vsel);
  await sleep(150);
  const vf = await frameRect(app, vsel);
  if (vf) await page.mouse.click(vf.x + vf.w / 2, vf.y + vf.h * 0.35, { button: 'right' });
  r.videoMenu = await until(() => mediaMenuState(app), 2000);
  const c0 = postedOf(app, 'copyMedia').length;
  if (r.videoMenu) await page.locator('.media-menu .menu-item', { hasText: '複製影片' }).click();
  r.copy = await until(() => postedOf(app, 'copyMedia').slice(c0)[0] || null, 2000);
  // 3. 燈箱右上角的關閉鈕：1440 寬、窄到 420 寬（工具列收成只有圖示）都在，點了就關
  await page.click(thumb);
  await until(() => lbView(app), 4000);
  r.lbWide = await closeBtnState(app, '.lb');
  await page.setViewportSize({ width: 420, height: 800 });
  await sleep(250);
  r.lbNarrow = await closeBtnState(app, '.lb');
  // 窄的畫面文字按鈕只留圖示：文字藏起來、看得到的按鈕圖示都在（icon() 的圖示也是 span，2026-11-01 曾經連圖示一起藏掉）
  r.narrowLabels = await page.evaluate(() => [...document.querySelectorAll('.lb .lb-btn > span:not(.ic)')].some((s) => getComputedStyle(s).display !== 'none'));
  r.narrowIcons = await page.evaluate(() => [...document.querySelectorAll('.lb .lb-btn')].filter((b) => getComputedStyle(b).display !== 'none')
    .map((b) => Math.round(b.querySelector('.ic').getBoundingClientRect().width)));
  await shot(app, 'desktop-lightbox-close-420');
  if (r.lbNarrow) await page.click('.lb .lb-close');
  r.lbClosed = !!(await until(() => page.evaluate(() => (document.querySelector('.lb') ? null : true)), 2000));
  // 4. 播放器（420 寬）的關閉鈕
  await page.evaluate((q) => document.querySelector(q).scrollIntoView({ block: 'center' }), vsel);
  await page.click(convSel('c1') + ' .it[data-id="lv"] .mvid-cap button[aria-label="放大播放"]');
  await page.waitForSelector('.vp:not(.leaving)');
  await sleep(200);
  r.vpNarrow = await closeBtnState(app, '.vp');
  await shot(app, 'desktop-video-close-420');
  await page.keyboard.press('Escape');
  check('desktopMediaContextMenu', JSON.stringify(menuLabels(r.imageMenu)) === JSON.stringify(['複製圖片', '另存新檔', '用預設程式開啟', '在檔案總管顯示']) && r.lightbox === false
    && !!r.save && r.save.path === 'C:\\repo\\demo\\shot.png' && r.save.conversationId === 'c1'
    && JSON.stringify(menuLabels(r.videoMenu)) === JSON.stringify(['複製影片', '另存新檔', '用預設程式開啟', '在檔案總管顯示']) && !r.videoMenu[0].disabled
    && !!r.copy && r.copy.path === 'C:\\repo\\demo\\操作錄影.mp4' && r.copy.conversationId === 'c1',
  { imageMenu: r.imageMenu, lightbox: r.lightbox, save: r.save, videoMenu: r.videoMenu, copy: r.copy });
  check('desktopViewerCloseTopRight', closeBtnOk(r.lbWide) && closeBtnOk(r.lbNarrow) && !r.narrowLabels && r.narrowIcons.length >= 3 && r.narrowIcons.every((w) => w === 14)
    && r.lbClosed && closeBtnOk(r.vpNarrow),
    { lbWide: r.lbWide, lbNarrow: r.lbNarrow, narrowLabels: r.narrowLabels, narrowIcons: r.narrowIcons, closed: r.lbClosed, vpNarrow: r.vpNarrow });
  await HP.closePage(app);
}

// ───── 手機版的側欄抽屜（2026-10-23：帳戶區預設收起、左右滑收起與展開、點對話那一側或清單裡的對話就收起；drawer.js、drawerrules.js）─────

/** 抽屜與帳戶區的狀態：app 的 class、側欄的位置與 visibility、遮罩、帳戶區的展開鈕、清單的位置、存在這個瀏覽器的版面 */
const sideDrawerState = (app) => app.page.evaluate(() => {
  const appEl = document.getElementById('app');
  const sb = document.getElementById('sidebar');
  const scrim = document.querySelector('.sb-scrim');
  const fold = document.querySelector('.sb-fold');
  const accts = document.getElementById('sbAccts');
  const r = sb.getBoundingClientRect();
  const hist = document.querySelector('.sb-sec-hist').getBoundingClientRect();
  const sc = scrim ? getComputedStyle(scrim) : null;
  let saved = null;
  try { saved = JSON.parse(localStorage.getItem('myriagent.app.layout.v1') || 'null'); } catch { saved = null; }
  return {
    collapsed: appEl.classList.contains('sidebar-collapsed'), dragging: appEl.classList.contains('drawer-drag'),
    sbHidden: sb.hidden, sbVis: getComputedStyle(sb).visibility, sbLeft: Math.round(r.left), sbRight: Math.round(r.right), sbWidth: Math.round(r.width),
    scrim: sc ? { hidden: scrim.hidden, display: sc.display, vis: sc.visibility, opacity: Math.round(Number(sc.opacity) * 100) / 100, pe: sc.pointerEvents } : null,
    fold: fold ? { shown: !fold.hidden && getComputedStyle(fold).display !== 'none', expanded: fold.getAttribute('aria-expanded'), sum: fold.querySelector('.sb-fold-sum').textContent,
      run: !!fold.querySelector('.sb-fold-sum .macct-run'), wait: !!fold.querySelector('.sb-fold-sum .macct-wait'), h: Math.round(fold.getBoundingClientRect().height) } : null,
    title: (() => { const t = document.querySelector('.sb-sec-accts > .sb-head > .sb-title'); return !!t && !t.hidden; })(),
    acctsHidden: accts.hidden, cards: [...accts.querySelectorAll('.macct')].filter((c) => c.getBoundingClientRect().height > 0).length,
    // 「新增帳戶」（只有桌面；瀏覽器版是 null）：跟著帳戶清單收起
    add: (() => { const a = document.querySelector('.sb-add'); return a ? a.getBoundingClientRect().height > 0 : null; })(),
    sbPos: getComputedStyle(sb).position,
    histTop: Math.round(hist.top - r.top), histH: Math.round(hist.height),
    saved: saved ? saved.sidebarCollapsed : null,
    // 品牌圖示外圈的「執行中」：markAnyRunning 切的 class；has 是以前寫在 fx.css 的選擇器當下比對的結果（只記錄對照）
    brand: { cls: appEl.classList.contains('acct-running'), has: appEl.matches('.app:has(.sidebar .macct-run:not([hidden]))') },
  };
});

/**
 * 以 CDP 送真的觸控（hasTouch 的頁面）：from→to 分 steps 步、每步 stepMs；holdMs 是放開前停住多久。
 * end=false 不放開，回傳放開的函式（觸控的狀態跟著 CDP 工作階段，放開要用同一個）
 */
async function swipe(page, from, to, o = {}) {
  const steps = o.steps || 8;
  const cdp = await page.context().newCDPSession(page);
  const pt = (x, y) => [{ x: Math.round(x), y: Math.round(y), id: 1 }];
  const release = async () => {
    try { await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] }); } finally { await cdp.detach().catch(() => {}); }
  };
  try {
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: pt(from.x, from.y) });
    for (let i = 1; i <= steps; i++) {
      await cdp.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: pt(from.x + (to.x - from.x) * i / steps, from.y + (to.y - from.y) * i / steps) });
      await sleep(o.stepMs || 16);
    }
    if (o.holdMs) await sleep(o.holdMs);
  } catch (e) {
    await cdp.detach().catch(() => {});
    throw e;
  }
  if (o.end === false) return release;
  await release();
  return null;
}

/**
 * 開關觸控模擬（hasTouch 的頁面主要指標是觸控＝pointer: coarse，等於手機或平板；關掉等於接上滑鼠的桌面瀏覽器）。
 * 下一影格網頁收到 (pointer: coarse) 的 change。同一頁一律用同一個 CDP 工作階段（不斷開，跟著頁面關）：
 * 關掉的工作階段斷開後照樣是關的，另一個工作階段再開也打不開（實測 Chrome 156）
 */
const touchCdp = new WeakMap();
async function setTouch(page, on) {
  let cdp = touchCdp.get(page);
  if (!cdp) {
    cdp = await page.context().newCDPSession(page);
    touchCdp.set(page, cdp);
  }
  await cdp.send('Emulation.setTouchEmulationEnabled', on ? { enabled: true, maxTouchPoints: 1 } : { enabled: false });
  await sleep(150);
}

/** 點左上角的鈕之後，逐影格記側欄的左緣：要有介於收起與全開之間的影格（真的滑進來，不是一下出現） */
const buttonSlide = (app) => app.page.evaluate(() => new Promise((resolve) => {
  const sb = document.getElementById('sidebar');
  const xs = [];
  document.getElementById('btnSidebar').click();
  let n = 0;
  const step = () => { xs.push(Math.round(sb.getBoundingClientRect().left)); if (++n < 30) requestAnimationFrame(step); else resolve(xs); };
  requestAnimationFrame(step);
}));

async function runPhoneDrawer() {
  const id = LF.CONV.idle;
  const app = await openWithLayout('phone-drawer', layoutOf([[[id], id]]), { open: { vp: { w: 390, h: 844 }, hasTouch: true } });
  await waitLoaded(app, [id]);
  const { page, hub } = app;
  const settle = () => sleep(450);
  const accounts = LF.liveAccounts(NOW).length;

  // 帳戶區預設收起：只有展開鈕（合計：帳戶數、執行中、等你回應），對話清單緊接在下面；點一下展開、再點收起
  const first = await sideDrawerState(app);
  await shot(app, 'hub-phone-drawer');
  await page.tap('.sb-fold');
  await sleep(150);
  const expanded = await sideDrawerState(app);
  await shot(app, 'hub-phone-drawer-accounts');
  await page.tap('.sb-fold');
  await sleep(150);
  const refolded = await sideDrawerState(app);
  check('hubPhoneAcctsFolded', !first.collapsed && first.sbVis === 'visible' && first.sbLeft === 0 && !!first.fold && first.fold.shown && first.fold.expanded === 'false'
    && first.fold.sum.startsWith(String(accounts)) && first.fold.run && first.fold.wait && first.fold.h >= 34 && !first.title && first.acctsHidden && first.cards === 0 && first.histTop <= 42
    && expanded.fold.expanded === 'true' && !expanded.acctsHidden && expanded.cards === accounts && expanded.histTop > 100
    && refolded.fold.expanded === 'false' && refolded.acctsHidden && refolded.histTop === first.histTop,
  { first, expanded, refolded, accounts });

  // 品牌圖示外圈的「執行中」（.app.acct-running）：每個狀態都要跟著有沒有對話在執行（收起、展開、加寬、窄回來都一樣）。
  // 以前的 .app:has(.sidebar .macct-run:not([hidden])) 在加寬之後全部停時還亮著（手機版的合計只在手機版重畫，留著舊的膠囊），
  // has 只記錄對照：wideStopped 的 has 是 true（2026-10-24 修正）。加寬時關掉觸控模擬（接上滑鼠的桌面瀏覽器）：
  // 觸控的寬螢幕是平板，帳戶區照樣可以收起、合計照樣重畫，就不是「離開可以收起的版面」了
  const pushRunning = async (on) => {
    const accts = LF.liveAccounts(NOW).map((a) => (on ? a : Object.assign({}, a, { sessions: Object.assign({}, a.sessions, { running: 0 }), otherSessions: null })));
    hub.push({ type: 'accounts', accounts: accts });
    await sleep(250);
  };
  const brand = { first: Object.assign({ want: true }, first.brand), expanded: Object.assign({ want: true }, expanded.brand), refolded: Object.assign({ want: true }, refolded.brand) };
  const brandAt = async (want) => Object.assign({ want }, (await sideDrawerState(app)).brand);
  await pushRunning(false);
  brand.foldedStopped = await brandAt(false);
  await page.tap('.sb-fold');
  await sleep(150);
  brand.expandedStopped = await brandAt(false);
  await page.tap('.sb-fold');
  await sleep(150);
  await pushRunning(true);
  brand.foldedRunning = await brandAt(true);
  await setTouch(page, false);
  await page.setViewportSize({ width: 1100, height: 844 });
  await sleep(300);
  brand.wideRunning = await brandAt(true);
  await pushRunning(false);
  brand.wideStopped = await brandAt(false);
  await page.setViewportSize({ width: 390, height: 844 });
  await setTouch(page, true);
  await sleep(300);
  brand.narrowStopped = await brandAt(false);
  await pushRunning(true);
  brand.narrowRunning = await brandAt(true);
  const brandList = Object.values(brand);
  check('hubPhoneBrandRunMatches', brandList.every((b) => b && b.cls === b.want) && brandList.some((b) => b.cls) && brandList.some((b) => !b.cls), brand);

  // 在清單上往左甩：收起（滑出畫面、遮罩淡出、記進版面），起點那一列不會被當成點擊而開啟
  const opens0 = hub.cmds('open').length;
  await swipe(page, { x: 250, y: 520 }, { x: 30, y: 530 });
  await settle();
  const closed = await sideDrawerState(app);
  check('hubPhoneSwipeLeftCloses', closed.collapsed && !closed.dragging && closed.sbVis === 'hidden' && closed.sbRight <= 0 && !closed.sbHidden
    && closed.scrim.vis === 'hidden' && closed.scrim.pe === 'none' && closed.saved === true && hub.cmds('open').length === opens0,
  { closed, opens: hub.cmds('open').length - opens0 });
  await shot(app, 'hub-phone-drawer-closed');

  // 收著時：上下滑（捲動）、從輸入框開始的左右滑、往左滑都不展開側欄（往左滑叫出的是右邊的電腦狀態，下面收起它）
  await swipe(page, { x: 200, y: 520 }, { x: 215, y: 300 });
  await settle();
  const vertical = await sideDrawerState(app);
  const ta = await page.locator(taSel(id)).boundingBox();
  await swipe(page, { x: ta.x + 20, y: ta.y + 15 }, { x: ta.x + 250, y: ta.y + 18 });
  await settle();
  const fromInput = await sideDrawerState(app);
  await swipe(page, { x: 300, y: 450 }, { x: 80, y: 455 });
  await settle();
  const leftward = await sideDrawerState(app);
  // 往左滑叫出的是電腦狀態（手機版疊在對話的右邊）；在它上面往右滑收起
  const monOpened = await panelsState(app);
  const monAt = await monTitleAt(app);
  if (monAt) await swipe(page, monAt, { x: Math.min(386, monAt.x + 170), y: monAt.y + 3 });
  await settle();
  const monClosed = await panelsState(app);
  check('hubPhoneSwipeLeftMonitor', !!monOpened.mon && monOpened.mon.right === 390 && monOpened.mon.pos === 'absolute' && monOpened.collapsed && monOpened.classes.length === 0
    && !!monAt && !monClosed.mon && monClosed.collapsed && monClosed.classes.length === 0 && monClosed.docScroll <= monClosed.innerWidth,
  { monOpened, monClosed });
  check('hubPhoneSwipeIgnored', vertical.collapsed && fromInput.collapsed && leftward.collapsed && !vertical.dragging && !fromInput.dragging && !leftward.dragging,
    { vertical: vertical.collapsed, fromInput: fromInput.collapsed, leftward: leftward.collapsed });

  // 往右拉到三成就停住再放開：拖曳中跟著手指（側欄露出一部分、遮罩半透明），放開後縮回去
  const release = await swipe(page, { x: 60, y: 400 }, { x: 160, y: 402 }, { steps: 10, end: false });
  const mid = await sideDrawerState(app);
  await sleep(200);
  await release();
  await settle();
  const back = await sideDrawerState(app);
  check('hubPhoneDragFollowsFinger', mid.dragging && mid.sbVis === 'visible' && mid.sbLeft > -mid.sbWidth + 40 && mid.sbLeft < -mid.sbWidth / 2
    && mid.scrim.opacity > 0.1 && mid.scrim.opacity < 0.6 && mid.collapsed
    && back.collapsed && !back.dragging && back.sbVis === 'hidden' && back.sbRight <= 0,
  { mid, back });

  // 往右甩：展開（記進版面）
  await swipe(page, { x: 60, y: 400 }, { x: 260, y: 405 });
  await settle();
  const opened = await sideDrawerState(app);
  check('hubPhoneSwipeRightOpens', !opened.collapsed && !opened.dragging && opened.sbVis === 'visible' && opened.sbLeft === 0 && opened.scrim.vis === 'visible'
    && opened.scrim.opacity === 1 && opened.saved === false,
  { opened });

  // 點對話那一側（遮罩）：收起，點擊不落到對話上
  await page.touchscreen.tap(370, 420);
  await settle();
  const tapped = await sideDrawerState(app);
  check('hubPhoneTapChatCloses', tapped.collapsed && tapped.sbVis === 'hidden' && tapped.saved === true, { tapped });

  // 左上角的鈕：一樣滑進來（逐影格有中間的位置）；在清單點了別的對話：收起並切到那個對話
  const xs = await buttonSlide(app);
  await settle();
  const viaButton = await sideDrawerState(app);
  const target = LF.CONV.offline;
  await page.locator('.sb-list .crow', { has: page.locator('.crow-title', { hasText: TITLE_OF[target] }) }).tap();
  const loaded = await waitLoaded(app, [target]);
  await settle();
  const picked = await sideDrawerState(app);
  const ws = await wsState(app);
  check('hubPhonePickCloses', !viaButton.collapsed && xs.some((x) => x < -10 && x > -viaButton.sbWidth + 10) && xs[xs.length - 1] === 0
    && !!loaded && picked.collapsed && picked.sbVis === 'hidden' && ws.panes[0].active === target,
  { xs, viaButton: viaButton.collapsed, picked, active: ws.panes[0].active });
  const hs = await hScroll(app);
  check('hubPhoneDrawerNoHScroll', hs.docScroll <= hs.innerWidth && hs.bodyScroll <= hs.innerWidth && hs.wide.length === 0, hs);

  // 搜尋框在抽屜的左下角、篩選下拉的上面（篩選是最後一列）：點了打得了字、清單跟著篩；清掉之後恢復
  await page.click('#btnSidebar');
  await settle();
  const geo = await page.evaluate(() => {
    const r = (sel) => document.querySelector(sel).getBoundingClientRect();
    const s = r('#sidebar'), q = r('#sidebar .sb-search-input'), f = r('#sidebar .sb-filters'), sc = r('#sidebar .sb-scroll');
    return { sbBottom: Math.round(s.bottom), innerHeight: window.innerHeight, gap: Math.round(s.bottom - f.bottom), left: Math.round(q.left - s.left),
      searchAbove: q.bottom <= f.top + 0.5, listAbove: sc.bottom <= q.top + 0.5 };
  });
  await page.tap('#sidebar .sb-search-input');
  await page.keyboard.type('筆電上的');
  await sleep(200);
  const rows = await page.$$eval('#sidebar .sb-list .crow .crow-title', (xs) => xs.map((x) => x.textContent));
  await shot(app, 'hub-phone-drawer-search');
  await page.fill('#sidebar .sb-search-input', '');
  await sleep(150);
  const allRows = await page.$$eval('#sidebar .sb-list .crow', (xs) => xs.length);
  check('hubPhoneSearchBottomLeft', geo.sbBottom === geo.innerHeight && geo.gap >= 0 && geo.gap <= 12 && geo.left >= 0 && geo.left <= 14 && geo.searchAbove && geo.listAbove
    && rows.length === 1 && rows[0].startsWith('筆電上的對話') && allRows === LF.liveList(NOW).length,
  { geo, rows, allRows });
  await page.click('#btnSidebar');
  await settle();

  // 加寬到桌面的寬度（關掉觸控模擬＝接上滑鼠的桌面瀏覽器；觸控的寬螢幕是平板，見 hubTabletAcctsFolded）：
  // 帳戶區照常（一般的標題、卡片都在、沒有展開鈕）、沒有遮罩；再窄回來又從收起開始
  await setTouch(page, false);
  await page.setViewportSize({ width: 1100, height: 844 });
  await page.click('#btnSidebar');
  await sleep(300);
  const wide = await sideDrawerState(app);
  await page.setViewportSize({ width: 390, height: 844 });
  await sleep(300);
  const narrowAgain = await sideDrawerState(app);
  check('hubPhoneWideUnchanged', !wide.collapsed && !wide.sbHidden && wide.title && !wide.fold.shown && !wide.acctsHidden && wide.cards === accounts && wide.scrim.display === 'none'
    && narrowAgain.fold.shown && narrowAgain.fold.expanded === 'false' && narrowAgain.acctsHidden,
  { wide, narrowAgain });
  await HP.closePage(app);
}

/** 兩側面板與對話區：側欄、電腦狀態（藏起來是 null）的位置與定位方式、對話區、app 上叫出中的 class、水平捲動 */
const panelsState = (app) => app.page.evaluate(() => {
  const box = (el) => {
    if (!el || el.hidden) return null;
    const b = el.getBoundingClientRect();
    return { left: Math.round(b.left), right: Math.round(b.right), width: Math.round(b.width), pos: getComputedStyle(el).position };
  };
  const appEl = document.getElementById('app');
  return {
    sb: box(document.getElementById('sidebar')), mon: box(document.getElementById('monitor')), ws: box(document.getElementById('workspace')),
    collapsed: appEl.classList.contains('sidebar-collapsed'), classes: [...appEl.classList].filter((c) => /^(peek-|drawer-)/.test(c)),
    docScroll: document.documentElement.scrollWidth, bodyScroll: document.body.scrollWidth, innerWidth: window.innerWidth,
  };
});

/** 電腦狀態標題的中心（在面板上滑的起點；面板沒顯示時 null） */
const monTitleAt = (app) => app.page.evaluate(() => {
  const m = document.getElementById('monitor');
  const t = m && !m.hidden ? m.querySelector('.mon-title') : null;
  if (!t) return null;
  const b = t.getBoundingClientRect();
  return { x: Math.round(b.left + b.width / 2), y: Math.round(b.top + b.height / 2) };
});

/**
 * 寬螢幕的觸控（側欄與電腦狀態是並排的欄位；平板、觸控筆電上的桌面 app）：在側欄上往左甩收起；對話區往右拉三成停住，
 * 側欄疊在對話上跟著手指（對話區不變寬窄），放開縮回；往右甩叫出側欄，落定回到欄位、對話區讓出位置；電腦狀態開著時在它上面往右滑收起，
 * 對話區往左拉過一半停住（疊在對話上跟著手指）再放開叫出、落定回到欄位，再在它上面往右滑收起；每一步都沒有水平捲動。回傳 { ok, r }
 */
async function touchPanels(app) {
  const { page } = app;
  const { width: vw, height: vh } = page.viewportSize();
  const settle = () => sleep(450);
  const y = Math.round(vh * 0.55);
  const x0 = Math.round(vw * 0.3);
  const r = {};
  r.start = await panelsState(app);
  const sbW = r.start.sb ? r.start.sb.width : 0;
  // 側欄：往左甩收起
  if (r.start.sb) await swipe(page, { x: r.start.sb.right - 40, y }, { x: 10, y: y + 5 });
  await settle();
  r.sbClosed = await panelsState(app);
  // 對話區往右拉三成停住：疊在對話上跟著手指；放開縮回
  let release = await swipe(page, { x: x0, y }, { x: x0 + Math.round(sbW * 0.3) + 10, y: y + 2 }, { steps: 10, end: false });
  r.sbMid = await panelsState(app);
  await sleep(200);
  await release();
  await settle();
  r.sbBack = await panelsState(app);
  // 往右甩：叫出側欄，落定回到欄位
  await swipe(page, { x: x0, y }, { x: x0 + 220, y: y + 4 });
  await settle();
  r.sbOpened = await panelsState(app);
  // 電腦狀態：開著就先在它上面往右滑收起
  r.monStart = r.sbOpened.mon;
  let t = await monTitleAt(app);
  if (t) {
    await swipe(page, t, { x: Math.min(vw - 4, t.x + 170), y: t.y + 3 });
    await settle();
  }
  r.monClosed = await panelsState(app);
  // 對話區往左拉過一半停住：疊在對話上跟著手指；放開叫出、落定回到欄位
  const x1 = Math.round(vw * 0.8);
  release = await swipe(page, { x: x1, y }, { x: x1 - 160, y: y + 2 }, { steps: 10, end: false });
  r.monMid = await panelsState(app);
  await sleep(200);
  await release();
  await settle();
  r.monOpened = await panelsState(app);
  await shot(app, app.name + '-monitor-opened');
  // 在電腦狀態上往右滑：收起
  t = await monTitleAt(app);
  if (t) {
    await swipe(page, t, { x: Math.min(vw - 4, t.x + 170), y: t.y + 3 });
    await settle();
  }
  r.monClosed2 = await panelsState(app);
  const noScroll = (s) => s.docScroll <= s.innerWidth && s.bodyScroll <= s.innerWidth;
  const column = (b) => !!b && b.pos !== 'absolute';
  const near = (a, b) => Math.abs(a - b) <= 1;
  const states = [r.start, r.sbClosed, r.sbMid, r.sbBack, r.sbOpened, r.monClosed, r.monMid, r.monOpened, r.monClosed2];
  const ok = column(r.start.sb) && r.start.sb.left === 0 && sbW > 200
    && !r.sbClosed.sb && r.sbClosed.collapsed && r.sbClosed.ws.left === 0 && r.sbClosed.classes.length === 0
    && !!r.sbMid.sb && r.sbMid.sb.pos === 'absolute' && r.sbMid.classes.includes('peek-left') && r.sbMid.classes.includes('drawer-drag')
    && r.sbMid.sb.left > -sbW + 30 && r.sbMid.sb.left < -sbW / 2 && r.sbMid.ws.left === 0
    && !r.sbBack.sb && r.sbBack.collapsed && r.sbBack.classes.length === 0
    && column(r.sbOpened.sb) && r.sbOpened.sb.left === 0 && !r.sbOpened.collapsed && near(r.sbOpened.ws.left, r.sbOpened.sb.right) && r.sbOpened.classes.length === 0
    && !r.monClosed.mon && near(r.monClosed.ws.right, vw)
    && !!r.monMid.mon && r.monMid.mon.pos === 'absolute' && r.monMid.classes.includes('peek-right') && near(r.monMid.ws.right, vw)
    && r.monMid.mon.left > vw - r.monMid.mon.width && r.monMid.mon.left < vw - r.monMid.mon.width / 2
    && column(r.monOpened.mon) && near(r.monOpened.mon.right, vw) && near(r.monOpened.ws.right, r.monOpened.mon.left) && r.monOpened.classes.length === 0
    && !r.monClosed2.mon && near(r.monClosed2.ws.right, vw) && r.monClosed2.classes.length === 0
    && states.every(noScroll);
  return { ok, r };
}

/**
 * 平板的帳戶區（2026-10-24 使用者要求）：側欄是並排的欄位（不是抽屜、沒有遮罩），帳戶區和手機版一樣預設收起（展開鈕與合計、沒有卡片，
 * 桌面的「新增帳戶」也收起）；點一下展開、再點收起。回傳 { ok, first, expanded, refolded }
 */
async function tabletAccounts(app, accounts) {
  const { page } = app;
  const first = await sideDrawerState(app);
  await shot(app, app.name + '-accounts-folded');
  if (!first.fold || !first.fold.shown) return { ok: false, first }; // 沒有展開鈕：不點（點不到會等 30 秒逾時、整個探針中斷）
  await page.tap('.sb-fold');
  await sleep(150);
  const expanded = await sideDrawerState(app);
  await page.tap('.sb-fold');
  await sleep(150);
  const refolded = await sideDrawerState(app);
  const cardsOk = (n) => (accounts == null ? n > 0 : n === accounts);
  const ok = !first.collapsed && first.sbPos !== 'absolute' && first.sbLeft === 0 && first.scrim.display === 'none'
    && !!first.fold && first.fold.shown && first.fold.expanded === 'false' && first.fold.h >= 34 && !first.title && first.acctsHidden && first.cards === 0
    && first.add !== true && first.histTop <= 42
    && expanded.fold.expanded === 'true' && !expanded.acctsHidden && cardsOk(expanded.cards) && expanded.add !== false && expanded.histTop > 100
    && refolded.fold.expanded === 'false' && refolded.acctsHidden && refolded.add !== true && refolded.histTop === first.histTop;
  return { ok, first, expanded, refolded };
}

async function runTabletPanels() {
  // 平板（瀏覽器版 1024×768、觸控）：側欄與電腦狀態是並排的欄位，一樣能左右滑叫出與收起（電腦狀態在 1200 寬以下預設收起）
  const id = LF.CONV.idle;
  const app = await openWithLayout('tablet-panels', layoutOf([[[id], id]]), { open: { vp: { w: 1024, h: 768 }, hasTouch: true } });
  await waitLoaded(app, [id]);
  const accounts = LF.liveAccounts(NOW).length;
  const a = await tabletAccounts(app, accounts);
  // 接上滑鼠（主要指標變成精細的，例如 Windows 平板接上鍵盤）：帳戶區照常顯示；拆下又從收起開始（(pointer: coarse) 的 change）
  await setTouch(app.page, false);
  const mouse = await sideDrawerState(app);
  await setTouch(app.page, true);
  const touchAgain = await sideDrawerState(app);
  check('hubTabletAcctsFolded', a.ok && accounts > 0
    && mouse.title && !mouse.fold.shown && !mouse.acctsHidden && mouse.cards === accounts
    && touchAgain.fold.shown && touchAgain.fold.expanded === 'false' && touchAgain.acctsHidden,
  { first: a.first, expanded: a.expanded, refolded: a.refolded, mouse, touchAgain, accounts });
  const t = await touchPanels(app);
  check('hubTabletSwipePanels', t.ok && !t.r.monStart, t.r);
  await HP.closePage(app);
}

async function runDesktopTouchPanels() {
  // 觸控筆電上的桌面 app（WebView2 模擬 1440×900、觸控）：同上；電腦狀態在 1440 寬預設顯示，先收起再叫出
  const app = await openDesktopLive('desktop-touch-panels', { init: { layout: desktopLayout(['c1']) }, vp: { w: 1440, h: 900 }, hasTouch: true });
  await until(() => app.page.evaluate((s) => !!document.querySelector(s), convSel('c1') + ':not(.loading) .it'), 4000);
  // 模擬的觸控是主要指標（pointer: coarse），等於平板上的桌面 app：帳戶區與「新增帳戶」預設收起
  const a = await tabletAccounts(app, null);
  check('desktopTabletAcctsFolded', a.ok && a.first.add === false && a.expanded.add === true, a);
  const t = await touchPanels(app);
  check('desktopTouchSwipePanels', t.ok && !!t.r.monStart, t.r);
  await HP.closePage(app);
}

// ───── 步 6：遠端操作（D5、D14～D18、D43：送出與追蹤、閘門、卡片、選電腦、草稿換電腦、換成正式對話、刪除）─────

const UNKNOWN_TEXT = '結果不明，請看對話內容確認';
const SEND_OFFLINE_TEXT = '目前沒有連線，連上之後才能送出';
/** 伺服器的 commandResult（沒有 origin） */
const serverResult = (commandId, state, code = null) => ({ type: 'commandResult', commandId, state, code, message: null, tier: null });
const taSel = (id) => '.conv[data-conv="' + id + '"] textarea.cinput';
const cardSel = (convId, itemId) => '.conv[data-conv="' + convId + '"] .it[data-id="' + itemId + '"]';

/** 筆電（d_B）也在線的 init 選項：帳戶 la_c1 有兩台候選（OFFICE-PC、筆電） */
function twoOnline() {
  const devices = LF.liveDevices(NOW).map((d) => (d.id === LF.DEVICE.offline ? Object.assign({}, d, { online: true }) : d));
  const accounts = LF.liveAccounts(NOW).map((a) => Object.assign({}, a, {
    devices: a.devices.map((c) => (c.deviceId === LF.DEVICE.offline ? Object.assign({}, c, { online: true }) : c)),
  }));
  return { devices, accounts };
}

/** 輸入區：字、送出鈕停用、閘門說明、結果不明的說明、佇列的「還有 N 則」 */
const composerState = (app, id) => app.page.evaluate((cid) => {
  const root = document.querySelector('.conv[data-conv="' + cid + '"]');
  if (!root) return null;
  const shown = (el) => !!el && !el.hidden && getComputedStyle(el).display !== 'none';
  const gate = root.querySelector('[data-field="sendGate"]');
  const settle = root.querySelector('[data-field="sendSettle"]');
  const more = root.querySelector('[data-field="queueMore"]');
  const send = root.querySelector('.cbtn.send');
  return {
    text: root.querySelector('textarea.cinput').value, sendDisabled: !!send && send.disabled,
    gate: shown(gate) ? gate.querySelector('.cgate-text').textContent : null,
    settle: shown(settle) ? settle.querySelector('.csettle-text').textContent : null,
    more: shown(more) ? more.textContent : null, moreButtons: more ? more.querySelectorAll('button').length : null,
  };
}, id);

/** 在對話的輸入框填字後按 Enter（要是顯示中的分頁） */
async function typeAndEnter(app, id, text) {
  await app.page.fill(taSel(id), text);
  await app.page.press(taSel(id), 'Enter');
}

/** 一張卡片：等你回應、送出中、按鈕全停用／全可按、閘門說明、結果不明的說明 */
const cardState = (app, convId, itemId) => app.page.evaluate((sel) => {
  const n = document.querySelector(sel);
  if (!n) return null;
  const btns = [...n.querySelectorAll('button')];
  const gate = n.querySelector(':scope > [data-field="cardGate"]');
  const settle = n.querySelector(':scope > [data-field="cardSettle"]');
  return {
    waiting: n.classList.contains('waiting'), sending: n.classList.contains('sending'),
    disabled: btns.length > 0 && btns.every((b) => b.disabled), enabled: btns.length > 0 && btns.every((b) => !b.disabled),
    gate: gate ? gate.textContent : null, settle: settle ? settle.textContent : null,
  };
}, cardSel(convId, itemId));

/** 點卡片的第 i 個選項（1 起算） */
const clickOption = (app, convId, itemId, i) => app.page.click(cardSel(convId, itemId) + ' .opt[data-idx="' + i + '"]');

/** 結果送到頁面之前假 hub 回的 commandResult（頁面收到的） */
const resultOf = async (app, id) => (await HP.msgsOf(app, 'commandResult')).find((m) => m.commandId === id) || null;

async function runSendDone() {
  const app = await openWithLayout('send-done', layoutOf([[[LF.CONV.idle], LF.CONV.idle]]));
  const { hub } = app;
  await waitLoaded(app, [LF.CONV.idle]);
  hub.onCmd('send', (cmd, arg) => ({ push: [serverResult(arg.commandId, 'done')] }));
  await typeAndEnter(app, LF.CONV.idle, '幫我跑一次測試');
  const frame = await until(() => hub.cmds('send')[0] || null, 3000);
  const res = frame ? await until(() => resultOf(app, frame.commandId), 3000) : null;
  // 往返讀數（composer.showRtt）：結果回來時輸入框下方燈號的後面亮一下「⇅ N ms」（cmdtrack 量到的送出到結果），約 2.4 秒後收起
  const rtt = await until(() => app.page.evaluate((cid) => {
    const el = document.querySelector('.conv[data-conv="' + cid + '"] .ci-rtt');
    if (!el || el.hidden || !el.isConnected) return null;
    return { text: el.querySelector('.ci-rtt-n').textContent, fx: el.classList.contains('fx-rtt'), anims: el.getAnimations().map((a) => a.animationName), title: el.title };
  }, LF.CONV.idle), 2000, 15);
  const rttGone = await until(() => app.page.evaluate((cid) => document.querySelector('.conv[data-conv="' + cid + '"] .ci-rtt').hidden || null, LF.CONV.idle), 4000, 50);
  check('hubRttReadout', !!rtt && /^\d+ ms$/.test(rtt.text) && rtt.fx && rtt.anims.includes('fx-rtt') && rtt.title.includes(rtt.text) && !!rttGone, { rtt, rttGone });
  await sleep(300);
  const c = await composerState(app, LF.CONV.idle);
  check('hubSendTrackedDone', !!frame && frame.cmd.text === '幫我跑一次測試' && ULID_RE.test(frame.commandId || '') && !!res && res.state === 'done'
    && c.text === '' && c.settle === null && c.gate === null,
  { frame: frame && frame.cmd, result: res, composer: c });
  await HP.closePage(app);
}

async function runSendRejected() {
  const app = await openWithLayout('send-rejected', layoutOf([[[LF.CONV.idle], LF.CONV.idle]]));
  const { hub } = app;
  await waitLoaded(app, [LF.CONV.idle]);
  const SERVER_TOAST = 'OFFICE-PC 沒有收下這則訊息（探針）';
  // 伺服器的拒絕：commandResult 與提示先到，sendRejected（伺服器另送）晚 800 ms——字要等 sendRejected 才還原，網頁自己不還原
  hub.onCmd('send', (cmd, arg) => {
    setTimeout(() => hub.push({ type: 'sendRejected', conversationId: cmd.conversationId, text: cmd.text }), 800);
    return { push: [serverResult(arg.commandId, 'rejected', 'offline'), { type: 'toast', text: SERVER_TOAST, level: 'error' }] };
  });
  const t0 = (await HP.domToasts(app)).length;
  await typeAndEnter(app, LF.CONV.idle, '被拒絕的訊息');
  const frame = await until(() => hub.cmds('send')[0] || null, 3000);
  const res = frame ? await until(() => resultOf(app, frame.commandId), 3000) : null;
  await sleep(250);
  const mid = await composerState(app, LF.CONV.idle);
  const after = await until(async () => { const c = await composerState(app, LF.CONV.idle); return c.text ? c : null; }, 3000);
  await sleep(300);
  const toasts = (await HP.domToasts(app)).slice(t0);
  check('hubSendRejectedRestores', !!res && mid.text === '' && !!after && after.text === '被拒絕的訊息' && JSON.stringify(toasts) === JSON.stringify([SERVER_TOAST]),
    { result: res, beforeSendRejected: mid.text, after: after && after.text, toasts });
  await HP.closePage(app);
}

/** 對話欄裡標成沒有送出的那幾則的說明（Web/sendrules.js 的 failureOf） */
const failTexts = (app, conv) => app.page.evaluate((id) => Array.from(document.querySelectorAll('.conv[data-conv="' + id + '"] .sending-list .it-user.failed .sending-fail-text')).map((n) => n.textContent), conv);

async function runSendRateLimited() {
  const app = await openWithLayout('send-rate-limited', layoutOf([[[LF.CONV.idle], LF.CONV.idle]]));
  const { hub } = app;
  await waitLoaded(app, [LF.CONV.idle]);
  // 第一次＋重送三次都被限流：傳輸層放棄、合成 rate_limited、提示「伺服器忙碌」一次；伺服器沒處理這則（不會送 sendRejected），網頁把字還原一次
  hub.rejectNext('send', 'rate_limited', 4);
  await typeAndEnter(app, LF.CONV.idle, '被限流的訊息');
  const frame = await until(() => hub.cmds('send')[0] || null, 3000);
  const synth = frame ? await until(() => resultOf(app, frame.commandId), 11000, 100) : null;
  await sleep(300);
  const a = await composerState(app, LF.CONV.idle);
  const busy = countOf(await toastTexts(app), TEXT_BUSY);
  const rejected = (await HP.msgsOf(app, 'sendRejected')).length;
  const failA = await failTexts(app, LF.CONV.idle);
  // 放棄之前輸入框已經有新的字：不覆寫
  hub.rejectNext('send', 'rate_limited', 4);
  await typeAndEnter(app, LF.CONV.idle, '第二則也被限流');
  const f2 = await until(() => hub.cmds('send').find((c) => c.cmd.text === '第二則也被限流') || null, 3000);
  await app.page.fill(taSel(LF.CONV.idle), '之後打的字');
  const s2 = f2 ? await until(() => resultOf(app, f2.commandId), 11000, 100) : null;
  await sleep(300);
  const b = await composerState(app, LF.CONV.idle);
  // 對話欄裡那則留著、標成「沒有送出：送得太頻繁」（第二則也是）
  const failB = await failTexts(app, LF.CONV.idle);
  check('hubSendRateLimitedGiveUpRestores', !!synth && synth.origin === 'transport' && synth.code === 'rate_limited' && a.text === '被限流的訊息' && busy === 1 && rejected === 0
    && !!s2 && b.text === '之後打的字' && failA.join('|') === '沒有送出：送得太頻繁' && failB.join('|') === '沒有送出：送得太頻繁|沒有送出：送得太頻繁',
  { synth, text: a.text, busyToasts: busy, sendRejected: rejected, second: { synth: s2 && s2.code, text: b.text }, failA, failB });
  await HP.closePage(app);
}

async function runSendNotSent() {
  const app = await openWithLayout('send-not-sent', layoutOf([[[LF.CONV.idle], LF.CONV.idle]]));
  const { hub } = app;
  await waitLoaded(app, [LF.CONV.idle]);
  // 限流一次（排了 1 秒後重送）之後斷線：傳輸層合成 not_sent，伺服器沒處理這則，網頁把字還原一次
  hub.rejectNext('send', 'rate_limited', 1);
  await typeAndEnter(app, LF.CONV.idle, '斷線前送出的訊息');
  const frame = await until(() => hub.cmds('send')[0] || null, 3000);
  await until(() => (frame ? cmdResultFrames(hub, frame.commandId).find((f) => f.obj.result.error === 'rate_limited') || null : null), 3000, 20);
  await sleep(150);
  hub.drop();
  await until(() => hub.connections.length >= 2, 6000);
  await HP.waitLive(app, 6000);
  await sleep(1800);
  const res = frame ? (await HP.msgsOf(app, 'commandResult')).filter((m) => m.commandId === frame.commandId) : [];
  const c = await composerState(app, LF.CONV.idle);
  // 對話欄裡那則標成「沒有送出：沒有連上伺服器」（重新連線後整份重載對話也留著）
  const fails = await failTexts(app, LF.CONV.idle);
  check('hubSendNotSentRestores', res.length === 1 && res[0].origin === 'transport' && res[0].code === 'not_sent' && c.text === '斷線前送出的訊息' && hub.cmds('send').length === 1
    && fails.join('|') === '沒有送出：沒有連上伺服器',
    { results: res, text: c.text, sends: hub.cmds('send').length, fails });
  await HP.closePage(app);
}

async function runSendOffline() {
  const app = await openWithLayout('send-offline', layoutOf([[[LF.CONV.idle], LF.CONV.idle]]));
  const { hub, api } = app;
  await waitLoaded(app, [LF.CONV.idle]);
  app.expect((m) => /Failed to load resource/.test(m.text) && /status of 503/.test(m.text) && /\/api\/v1\/auth\/me/.test(m.url), '斷線期間 auth/me 503（刻意）');
  api.set('/api/v1/auth/me', { status: 503, body: { error: 'unavailable' } });
  hub.drop();
  await until(async () => { const s = await HP.tstatus(app); return s && s.state !== 'online' ? s : null; }, 4000, 30);
  await sleep(200);
  const c0 = hub.invocations('Cmd').length;
  await typeAndEnter(app, LF.CONV.idle, '離線時打的字');
  await app.page.evaluate((sel) => document.querySelector(sel).click(), '.conv[data-conv="' + LF.CONV.idle + '"] .cbtn.send');
  await sleep(400);
  const c = await composerState(app, LF.CONV.idle);
  check('hubSendOfflineKeepsText', c.text === '離線時打的字' && c.sendDisabled && c.gate === SEND_OFFLINE_TEXT && hub.invocations('Cmd').length === c0,
    { composer: c, frames: hub.invocations('Cmd').length - c0 });
  api.set('/api/v1/auth/me', { status: 200, body: { name: 'admin', serverDomain: 'hub.probe.local', session: {} } });
  await app.page.evaluate(() => window.myriagentHubTransport.retryNow());
  await HP.waitLive(app, 6000);
  await HP.closePage(app);
}

async function runSendGates() {
  const app = await openWithLayout('send-gates', layoutOf([[[LF.CONV.offline], LF.CONV.offline], [[LF.CONV.view], LF.CONV.view]]));
  const { hub } = app;
  await waitLoaded(app, [LF.CONV.offline, LF.CONV.view]);
  await typeInto(app, LF.CONV.offline, '想送出');
  await typeInto(app, LF.CONV.view, '想送出');
  const c0 = hub.cmds('send').length;
  await app.page.press(taSel(LF.CONV.offline), 'Enter');
  await app.page.press(taSel(LF.CONV.view), 'Enter');
  await sleep(300);
  const off = await composerState(app, LF.CONV.offline);
  const view = await composerState(app, LF.CONV.view);
  const offCard = await cardState(app, LF.CONV.offline, 'i6');
  const viewCard = await cardState(app, LF.CONV.view, 'i6');
  await shot(app, 'hub-send-gates-1440');
  check('hubSendDisabledDeviceOffline', off.gate === '電腦離線：筆電 ' + LF.XSS + ' 重新上線後才能送出' && off.sendDisabled && off.text === '想送出'
    && !!offCard && offCard.disabled && offCard.gate === '電腦離線：筆電 ' + LF.XSS + ' 重新上線後才能回應' && (await HP.xssState(app)).xss === 0,
  { composer: off, card: offCard });
  check('hubSendDisabledPolicyView', view.gate === '在 HOME-PC 的設定中允許遠端操作' && view.sendDisabled && view.text === '想送出' && hub.cmds('send').length === c0
    && !!viewCard && viewCard.disabled && viewCard.gate === '在 HOME-PC 的設定中允許遠端操作',
  { composer: view, card: viewCard, sends: hub.cmds('send').length - c0 });
  await HP.closePage(app);
}

async function runFullAccess() {
  // OFFICE-PC 沒有允許遠端完全自動：完全自動的對話不能送出；權限選單的「完全自動」停用並說明
  const devices = LF.liveDevices(NOW).map((d) => (d.id === LF.DEVICE.control ? Object.assign({}, d, { allowRemoteFullAccess: false }) : d));
  const header = (id) => (id === LF.CONV.idle ? { permission: 'FullAccess' } : id === LF.CONV.codex ? { provider: 'Codex' } : null);
  const app = await openWithLayout('full-access', layoutOf([[[LF.CONV.idle], LF.CONV.idle], [[LF.CONV.codex], LF.CONV.codex]]), { initOpts: { devices }, serve: { header } });
  const { page } = app;
  await waitLoaded(app, [LF.CONV.idle, LF.CONV.codex]);
  await typeInto(app, LF.CONV.idle, '完全自動下送出');
  const c = await composerState(app, LF.CONV.idle);
  await page.click('.conv[data-conv="' + LF.CONV.codex + '"] .perm');
  const menu = await until(() => page.evaluate(() => {
    const m = document.querySelector('.menu');
    return m ? [...m.querySelectorAll('.menu-item')].map((b) => ({ label: b.querySelector('.mi-label').textContent, disabled: b.disabled, title: b.title })) : null;
  }), 3000);
  await page.keyboard.press('Escape');
  const full = menu && menu.find((x) => x.label === '完全自動');
  check('hubFullAccessBlocked', c.gate === 'OFFICE-PC 沒有允許遠端完全自動，先把權限改成其他模式' && c.sendDisabled
    && !!full && full.disabled === true && full.title === 'OFFICE-PC 沒有允許遠端完全自動' && menu.filter((x) => x.disabled).length === 1,
  { composer: c, menu });
  await HP.closePage(app);
}

async function runHubAccountWhileRunning() {
  // 執行中換帳戶（使用者 2026-10-23；2026-10-24 起四種代理之間都能互換）——選單列四種的帳戶、同一種在前，選了再選時機
  // （現在停下並改用，或目前步驟完成後換），選了「目前步驟完成後換」送帶 when turnEnd 的 setAccount（那台的中樞等安全點再停下交接）；
  // Grok 的對話也開選單（Grok 在前）、不再提示不能換。2026-10-25 起只列對話所在那台（OFFICE-PC）已登入的帳戶：「沒有電腦登入」不列
  const id = LF.CONV.running;
  const { grok } = LF.PROVIDER_CONV;
  const titles = Object.fromEntries(LF.providerList(NOW).map((c) => [c.id, c.title]));
  const app = await openWithLayout('account-while-running', layoutOf([[[id], id], [[grok], grok]]), {
    initOpts: {
      accounts: [...LF.liveAccounts(NOW), ...LF.providerAccounts(NOW)], conversations: [...LF.liveList(NOW), ...LF.providerList(NOW)].sort((a, b) => b.updatedAt - a.updatedAt),
      // 同真的伺服器（2026-10-22 起送四種代理）：執行中的選單照 init 的 providers 排其他種類
      providers: [{ key: 'Claude', name: 'Claude Code' }, { key: 'Codex', name: 'Codex' }, { key: 'Antigravity', name: 'Antigravity' }, { key: 'Grok', name: 'Grok Build' }],
    },
    serve: { title: (cid) => titles[cid], header: (cid) => (cid === id ? { running: true } : LF.providerHeader(cid) || {}), items: (cid) => (cid === grok ? LF.providerItems(NOW, cid) : undefined) },
  });
  const { page, hub } = app;
  await waitLoaded(app, [id, grok]);
  hub.onCmd('setAccount', (cmd, arg) => ({ push: [serverResult(arg.commandId, 'done')] }));
  const chipTitle = await page.evaluate((sel) => document.querySelector(sel + ' .ch-acct').title, convSel(id));
  await page.click(convSel(id) + ' .ch-acct');
  await page.waitForSelector('.menu');
  const menu = await page.evaluate(() => ({
    heads: [...document.querySelectorAll('.menu .menu-head')].map((e) => e.textContent),
    items: [...document.querySelectorAll('.menu .menu-item')].map((b) => ({ label: b.querySelector('.mi-label').textContent, checked: b.getAttribute('aria-checked'), disabled: b.disabled })),
  }));
  await clickMenu(app, 'Codex Pro');
  await page.waitForSelector('.dlg', { timeout: 3000 }).catch(() => {});
  const dlg = await page.evaluate(() => {
    const d = document.querySelector('.dlg');
    return d ? { title: d.querySelector('.dlg-title').textContent, text: d.querySelector('.dlg-body').textContent, buttons: [...d.querySelectorAll('.dlg-foot button')].map((b) => b.textContent) } : null;
  });
  const before = hub.cmds('setAccount').length;
  await page.locator('.dlg .dlg-foot button', { hasText: '目前步驟完成後換' }).first().click().catch(() => {});
  const sent = await until(() => hub.cmds('setAccount')[before] || null, 3000);
  // Grok：也開選單（Grok 在前），不提示不能換
  const t0 = (await HP.domToasts(app)).length;
  await page.click(convSel(grok) + ' .ch-acct');
  await page.waitForSelector('.menu', { timeout: 2000 }).catch(() => {});
  const grokMenu = await page.evaluate(() => [...document.querySelectorAll('.menu .menu-head')].map((e) => e.textContent));
  await page.keyboard.press('Escape');
  const toasts = (await HP.domToasts(app)).slice(t0);
  check('hubAccountSwitchWhileRunning', chipTitle.endsWith('執行中也可以換帳戶（立即或等目前步驟完成後換）\n只能換成在 OFFICE-PC 登入的帳戶')
    && JSON.stringify(menu.heads) === JSON.stringify(['Claude（立即換或等目前步驟完成後換）', 'Codex（會以前文節錄開新對話）', 'Antigravity（會以前文節錄開新對話）', 'Grok（會以前文節錄開新對話）'])
    && JSON.stringify(menu.items.map((x) => x.label)) === JSON.stringify(['工作用 Max', 'Codex Pro', 'Antigravity', 'Grok 個人 ' + LF.XSS])
    && menu.items[0].checked === 'true'
    && !!dlg && dlg.title === '改用「Codex Pro」繼續？' && dlg.text.includes('正在執行的指令會被中斷') && dlg.text.includes('不能直接接續')
    && JSON.stringify(dlg.buttons) === JSON.stringify(['取消', '現在停下並改用', '目前步驟完成後換'])
    && !!sent && sent.cmd.conversationId === id && sent.cmd.accountId === 'la_x1' && sent.cmd.when === 'turnEnd' && ULID_RE.test(sent.commandId || '')
    && grokMenu[0] === 'Grok（立即換或等目前步驟完成後換）' && grokMenu.length === 4 && toasts.length === 0,
  { chipTitle, menu, dlg, sent: sent && { cmd: sent.cmd, commandId: sent.commandId }, grokMenu, toasts });
  await HP.closePage(app);
}

async function runCardDigests() {
  const app = await openWithLayout('card-digests', layoutOf([[[LF.CONV.running], LF.CONV.running]]));
  const { page, hub } = app;
  await waitLoaded(app, [LF.CONV.running]);
  const before = await cardState(app, LF.CONV.running, 'i6');
  await clickOption(app, LF.CONV.running, 'i6', 1);
  const ap = await until(() => hub.cmds('approve')[0] || null, 3000);
  await clickOption(app, LF.CONV.running, 'i5', 1);
  const pl = await until(() => hub.cmds('approvePlan')[0] || null, 3000);
  const locked = await cardState(app, LF.CONV.running, 'i6');
  check('hubApproveCarriesDigest', !!before && before.enabled && !!ap && ap.cmd.itemId === 'i6' && ap.cmd.choice === 'allow' && ap.cmd.requestDigest === LF.DIGEST.approval
    && !!pl && pl.cmd.itemId === 'i5' && pl.cmd.choice === 'acceptEdits' && pl.cmd.requestDigest === LF.DIGEST.plan && locked.sending && locked.disabled,
  { approve: ap && ap.cmd, approvePlan: pl && pl.cmd, locked });
  // 問卷：選第一個選項再送出
  await clickOption(app, LF.CONV.running, 'i4', 1);
  await page.click(cardSel(LF.CONV.running, 'i4') + ' .card-actions .btn.acc');
  const an = await until(() => hub.cmds('answer')[0] || null, 3000);
  check('hubAnswerCarriesDigest', !!an && an.cmd.itemId === 'i4' && an.cmd.requestDigest === LF.DIGEST.question && JSON.stringify(an.cmd.answers) === JSON.stringify({ q1: ['3 分鐘（建議）'] }),
    { answer: an && an.cmd });
  await HP.closePage(app);
}

async function runSkipNoDigest() {
  const app = await openWithLayout('skip-no-digest', layoutOf([[[LF.CONV.running], LF.CONV.running]]));
  const { hub } = app;
  await waitLoaded(app, [LF.CONV.running]);
  await app.page.click(cardSel(LF.CONV.running, 'i4') + ' .card-actions .btn.ghost');
  const sk = await until(() => hub.cmds('skipQuestion')[0] || null, 3000);
  await sleep(200);
  const st = await cardState(app, LF.CONV.running, 'i4');
  check('hubSkipNoDigest', !!sk && sk.cmd.itemId === 'i4' && !('requestDigest' in sk.cmd) && ULID_RE.test(sk.commandId || '') && st.sending,
    { skip: sk && sk.cmd, card: st });
  await HP.closePage(app);
}

async function runApproveUnknown() {
  const app = await openWithLayout('approve-unknown', layoutOf([[[LF.CONV.running], LF.CONV.running]]));
  const { hub } = app;
  await waitLoaded(app, [LF.CONV.running]);
  await clickOption(app, LF.CONV.running, 'i6', 1);
  await until(() => hub.cmds('approve')[0] || null, 3000);
  await sleep(300);
  const locked = await cardState(app, LF.CONV.running, 'i6');
  // 新的 init（伺服器 resync）：還在等的結果當成不明 → 解鎖並說明，不自動重送；之後重新 open 畫出來的卡片照樣有說明
  const n0 = hub.cmds('open').length;
  hub.push(LF.liveBrowserInit(NOW, { mode: 'main' }));
  await until(() => (hub.cmds('open').length > n0 ? true : null), 3000);
  await sleep(1200);
  const after = await cardState(app, LF.CONV.running, 'i6');
  check('hubApproveUnknownUnlocks', locked.sending && !!after && !after.sending && after.enabled && after.settle === UNKNOWN_TEXT && hub.cmds('approve').length === 1,
    { locked, after, approves: hub.cmds('approve').length });
  await HP.closePage(app);
}

async function runCardRejected() {
  const app = await openWithLayout('card-rejected', layoutOf([[[LF.CONV.running], LF.CONV.running]]));
  const { hub } = app;
  await waitLoaded(app, [LF.CONV.running]);
  const SERVER_TOAST = '這張卡片已經處理過或內容變了（探針）';
  hub.onCmd('approve', (cmd, arg) => ({ push: [serverResult(arg.commandId, 'rejected', 'stale'), { type: 'toast', text: SERVER_TOAST, level: 'error' }] }));
  const t0 = (await HP.domToasts(app)).length;
  await clickOption(app, LF.CONV.running, 'i6', 1);
  const ap = await until(() => hub.cmds('approve')[0] || null, 3000);
  const res = ap ? await until(() => resultOf(app, ap.commandId), 3000) : null;
  await sleep(300);
  const after = await cardState(app, LF.CONV.running, 'i6');
  const toasts = (await HP.domToasts(app)).slice(t0);
  check('hubCardRejectedUnlocks', !!res && !!after && !after.sending && after.enabled && after.settle === null && JSON.stringify(toasts) === JSON.stringify([SERVER_TOAST]),
    { result: res, after, toasts });
  await HP.closePage(app);
}

async function runSweepHidden() {
  const app = await openWithLayout('sweep-hidden', layoutOf([[[LF.CONV.running], LF.CONV.running]]));
  const { hub } = app;
  await waitLoaded(app, [LF.CONV.running]);
  await clickOption(app, LF.CONV.running, 'i6', 1);
  await until(() => hub.cmds('approve')[0] || null, 3000);
  await sleep(200);
  const locked = await cardState(app, LF.CONV.running, 'i6');
  // 分頁隱藏、時鐘推進 151 秒：每秒的計時器在 hidden 判斷之前 sweep，背景分頁的卡片也解鎖
  await HP.setHidden(app, true);
  await HP.advanceClock(app, 151000);
  const unlocked = await until(async () => { const s = await cardState(app, LF.CONV.running, 'i6'); return s && !s.sending ? s : null; }, 4000);
  const hidden = await app.page.evaluate(() => document.hidden);
  await HP.setHidden(app, false);
  check('hubSweepWhileHidden', locked.sending && !!unlocked && unlocked.settle === UNKNOWN_TEXT && hidden === true && hub.cmds('approve').length === 1,
    { locked, unlocked, hidden });
  await HP.closePage(app);
}

async function runQueueMore() {
  const app = await openWithLayout('queue-more', layoutOf([[[LF.CONV.idle], LF.CONV.idle]]));
  const { hub } = app;
  await waitLoaded(app, [LF.CONV.idle]);
  const c = await composerState(app, LF.CONV.idle);
  await app.page.click('.conv[data-conv="' + LF.CONV.idle + '"] [data-field="queueMore"]');
  await sleep(300);
  check('hubQueueMoreNoCancel', c.more === '還有 3 則在佇列中' && c.moreButtons === 0 && hub.cmds('cancelQueued').length === 0, { composer: c, cancels: hub.cmds('cancelQueued').length });
  await HP.closePage(app);
}

const pickDialog = (app) => app.page.evaluate(() => {
  const b = [...document.querySelectorAll('.dlg-pick-dev [data-field="pickDevice"]')];
  return b.length ? b.map((x) => ({ id: x.dataset.deviceId, name: x.querySelector('.pick-dev-name').textContent })) : null;
});

async function runPickSingle() {
  const app = await openWithLayout('pick-single', layoutOf([[[], null]]));
  const { hub } = app;
  await app.page.keyboard.press('Control+n');
  const nc = await until(() => hub.cmds('newConversation')[0] || null, 3000);
  await sleep(300);
  const dlg = await pickDialog(app);
  check('hubPickDeviceSingleOnline', !!nc && nc.cmd.deviceId === LF.DEVICE.control && nc.cmd.accountId === 'la_c1' && nc.cmd.paneId === 'p1' && dlg === null,
    { newConversation: nc && nc.cmd, dialog: dlg });
  await HP.closePage(app);
}

async function runPickAsk() {
  const app = await openWithLayout('pick-ask', layoutOf([[[], null]]), { initOpts: twoOnline() });
  const { page, hub } = app;
  await page.keyboard.press('Control+n');
  const names = await until(() => pickDialog(app), 3000);
  const before = hub.cmds('newConversation').length;
  await shot(app, 'hub-pick-device-1440');
  await page.click('.dlg-pick-dev [data-device-id="' + LF.DEVICE.offline + '"]');
  const nc = await until(() => hub.cmds('newConversation')[0] || null, 3000);
  await sleep(400);
  // 上次用的那台記下來了：再開一次直接用它、不再問
  await page.keyboard.press('Control+n');
  const nc2 = await until(() => hub.cmds('newConversation')[1] || null, 3000);
  await sleep(300);
  const again = await pickDialog(app);
  const xss = await HP.xssState(app);
  check('hubPickDeviceAsk', JSON.stringify(names) === JSON.stringify([{ id: LF.DEVICE.control, name: 'OFFICE-PC' }, { id: LF.DEVICE.offline, name: '筆電 ' + LF.XSS }])
    && before === 0 && !!nc && nc.cmd.deviceId === LF.DEVICE.offline && nc.cmd.accountId === 'la_c1' && !!nc2 && nc2.cmd.deviceId === LF.DEVICE.offline && again === null
    && xss.xss === 0 && xss.calls.length === 0,
  { names, first: nc && nc.cmd, second: nc2 && nc2.cmd, askedAgain: again, xss });
  await HP.closePage(app);
}

async function runPickNone() {
  const app = await openWithLayout('pick-none', layoutOf([[[], null]]));
  const { hub } = app;
  const t0 = (await HP.domToasts(app)).length;
  // 側欄帳戶卡的「＋」：這個帳戶沒有任何電腦登入
  await app.page.evaluate(() => document.querySelector('.macct[data-key="la_c9"] .macct-new').click());
  await sleep(600);
  const toasts = (await HP.domToasts(app)).slice(t0);
  check('hubPickDeviceNone', toasts.includes('沒有一台電腦登入了這個帳戶') && hub.cmds('newConversation').length === 0 && (await pickDialog(app)) === null,
    { toasts, newConversation: hub.cmds('newConversation').length });
  await HP.closePage(app);
}

/** 草稿的電腦晶片 */
const draftChip = (app, id) => app.page.evaluate((cid) => {
  const c = document.querySelector('.conv[data-conv="' + cid + '"] [data-field="draftDevice"]');
  return c ? { shown: !c.hidden, text: c.querySelector('.chip-label').textContent, online: c.dataset.online } : null;
}, id);

async function runDraftChip() {
  const app = await openWithLayout('draft-chip', layoutOf([[[LF.DRAFT], LF.DRAFT]]), { serve: { draft: () => ({ deviceId: LF.DEVICE.view }) } });
  await waitLoaded(app, [LF.DRAFT]);
  const chip = await draftChip(app, LF.DRAFT);
  const conv = await convState(app, LF.DRAFT);
  await shot(app, 'hub-draft-chip-1440');
  check('hubDraftChipFromHeader', !!chip && chip.shown && chip.text === '電腦：HOME-PC' && chip.online === 'true' && conv.device === null, { chip, deviceLine: conv && conv.device });
  await HP.closePage(app);
}

async function runDraftSwitch() {
  const NEW = LF.draftId(3);
  const oldHeader = { model: 'sonnet', permission: 'Plan', customFolder: true, workingDirectory: 'D:/work/demo', title: '草稿的標題' };
  const app = await openWithLayout('draft-switch', layoutOf([[[LF.CONV.idle, LF.DRAFT], LF.DRAFT]]), { initOpts: twoOnline(), serve: { draft: () => ({ header: oldHeader }) } });
  const { page, hub } = app;
  await waitLoaded(app, [LF.CONV.idle, LF.DRAFT]);
  hub.onCmd('newConversation', (cmd) => ({ push: [LF.draftMessage(NOW, NEW, { paneId: cmd.paneId, deviceId: cmd.deviceId || null })] }));
  await typeInto(app, LF.DRAFT, '要搬到新草稿的字');
  const chip0 = await draftChip(app, LF.DRAFT);
  await page.click('.conv[data-conv="' + LF.DRAFT + '"] [data-field="draftDevice"]');
  await page.locator('.menu .menu-item', { hasText: '筆電' }).click();
  const got = await until(async () => { const s = await convState(app, NEW); return s && s.loaded ? s : null; }, 4000);
  await sleep(600);
  const all = hub.cmds().map((c) => c.cmd);
  const at = (pred) => all.findIndex(pred);
  const nc = all.find((c) => c.cmd === 'newConversation');
  const carried = [
    at((c) => c.cmd === 'setOption' && c.conversationId === NEW && c.key === 'model' && c.value === 'sonnet'),
    at((c) => c.cmd === 'setOption' && c.conversationId === NEW && c.key === 'permission' && c.value === 'Plan'),
    at((c) => c.cmd === 'setFolder' && c.conversationId === NEW && c.path === 'D:/work/demo'),
    at((c) => c.cmd === 'rename' && c.conversationId === NEW && c.title === '草稿的標題'),
  ];
  const closeAt = at((c) => c.cmd === 'close' && c.conversationId === LF.DRAFT);
  const ws = await wsState(app);
  const chip1 = await draftChip(app, NEW);
  check('hubDraftSwitchDeviceKeepsOptions', chip0.text === '電腦：OFFICE-PC' && !!nc && nc.deviceId === LF.DEVICE.offline && nc.accountId === 'la_c1' && nc.paneId === 'p1'
    && carried.every((i) => i >= 0) && all.filter((c) => c.cmd === 'setOption').length === 2 && closeAt > Math.max(...carried)
    && !!got && got.text === '要搬到新草稿的字' && JSON.stringify(ws.panes[0].tabs) === JSON.stringify([LF.CONV.idle, NEW]) && ws.panes[0].active === NEW
    && !!chip1 && chip1.text === '電腦：筆電 ' + LF.XSS,
  { newConversation: nc, carried, closeAt, cmds: all.filter((c) => c.cmd !== 'open').map((c) => c.cmd + (c.key ? ':' + c.key : '')), panes: ws.panes, text: got && got.text, chips: [chip0, chip1] });
  await HP.closePage(app);
}

async function runDraftReplaced() {
  const app = await openWithLayout('draft-replaced', layoutOf([[[LF.CONV.idle, LF.DRAFT], LF.DRAFT]]));
  const { hub } = app;
  await waitLoaded(app, [LF.CONV.idle, LF.DRAFT]);
  const n0 = hub.cmds('open').length;
  // 草稿第一則送出後換成正式對話（伺服器送 conversationClosed{replacedBy}）
  hub.push({ type: 'conversationClosed', conversationId: LF.DRAFT, replacedBy: LF.CONV.unlisted });
  const loaded = await waitLoaded(app, [LF.CONV.unlisted]);
  const ws = await wsState(app);
  check('hubDraftReplacedBy', !!loaded && JSON.stringify(ws.panes[0].tabs) === JSON.stringify([LF.CONV.idle, LF.CONV.unlisted]) && ws.panes[0].active === LF.CONV.unlisted
    && JSON.stringify(openIds(hub, n0)) === JSON.stringify([LF.CONV.unlisted]) && loaded[LF.CONV.unlisted].shown && (await convState(app, LF.DRAFT)) === null && hub.cmds('close').length === 0,
  { panes: ws.panes, opens: openIds(hub, n0), closes: hub.cmds('close').length });
  await HP.closePage(app);
}

async function runDraftReplacedOpen() {
  const app = await openWithLayout('draft-replaced-open', layoutOf([[[LF.DRAFT], LF.DRAFT], [[LF.CONV.idle], LF.CONV.idle]]));
  const { hub } = app;
  await waitLoaded(app, [LF.DRAFT, LF.CONV.idle]);
  const n0 = hub.cmds('open').length;
  // 新的 id 已經開在別的分頁：只關草稿、聚焦那個分頁
  hub.push({ type: 'conversationClosed', conversationId: LF.DRAFT, replacedBy: LF.CONV.idle });
  const after = await until(async () => { const s = await wsState(app); return s.panes.length === 1 ? s : null; }, 3000);
  const focused = await app.page.evaluate(() => { const t = document.querySelector('.tab.focused'); return t ? t.dataset.key : null; });
  const tabs = after ? after.panes.flatMap((p) => p.tabs) : [];
  check('hubDraftReplacedByAlreadyOpen', !!after && JSON.stringify(tabs) === JSON.stringify([LF.CONV.idle]) && focused === LF.CONV.idle && openIds(hub, n0).length === 0 && hub.cmds('close').length === 0,
    { panes: after && after.panes, focused, opens: openIds(hub, n0) });
  await HP.closePage(app);
}

async function runDeleteTrash() {
  const app = await openWithLayout('delete-trash', layoutOf([[[LF.CONV.idle], LF.CONV.idle]]));
  const { page, hub } = app;
  await waitLoaded(app, [LF.CONV.idle]);
  hub.onCmd('delete', (cmd, arg) => ({ push: [serverResult(arg.commandId, 'rejected', 'offline')], delayMs: 800 }));
  await page.click('.conv[data-conv="' + LF.CONV.idle + '"] header.ch button[aria-label="更多動作"]');
  await page.locator('.menu .menu-item', { hasText: '刪除對話' }).click();
  const msg = await until(() => page.evaluate(() => { const d = document.querySelector('.dlg-confirm .confirm-msg'); return d ? d.textContent : null; }), 3000);
  await shot(app, 'hub-delete-confirm-1440');
  await page.click('.dlg-confirm .btn.danger-solid');
  const del = await until(() => hub.cmds('delete')[0] || null, 3000);
  const deleting = await page.evaluate((cid) => document.querySelector('.conv[data-conv="' + cid + '"]').classList.contains('deleting'), LF.CONV.idle);
  const restored = await until(() => page.evaluate((cid) => (document.querySelector('.conv[data-conv="' + cid + '"]').classList.contains('deleting') ? null : true), LF.CONV.idle), 3000);
  check('hubDeleteConfirmTrash', !!msg && msg.includes('會移到 OFFICE-PC 的回收區，30 天內可在那台電腦的設定還原') && !msg.includes('無法復原') && !!del && del.cmd.conversationId === LF.CONV.idle
    && deleting && !!restored,
  { message: msg, delete: del && del.cmd, deleting, restoredAfterRejected: !!restored });
  await HP.closePage(app);
}

// ───── 步 8：通知類訊息與 build 橫幅（D6、D7、D13、D19～D21、D42）─────

const DEFAULT_PASSWORD_TEXT = '密碼還是預設值，知道它的人都能遠端操作你開了遠端控制的電腦';
const BUILD_TEXT = '伺服器已換版本，重新載入以取得對應的網頁';
/** clipboard 的原文（Markdown，帶 XSS：寫進剪貼簿與文字框都要照字面） */
const CLIP_TEXT = '## 修正摘要\n\n' + LF.XSS + '\n\n```js\nlogin();\n```\n';
const itemSel = (convId, itemId) => '.conv[data-conv="' + convId + '"] .it[data-id="' + itemId + '"]';

/** 頂部列下方的提示：每一列的種類、說明、連結、按鈕 */
const noticeState = (app) => app.page.evaluate(() => [...document.querySelectorAll('.live-notices > .live-notice')].map((n) => ({
  kind: n.dataset.notice, tone: n.dataset.tone, role: n.getAttribute('role'),
  text: n.querySelector('.live-notice-text').textContent,
  links: [...n.querySelectorAll('a')].map((a) => ({ href: a.getAttribute('href'), text: a.textContent })),
  buttons: [...n.querySelectorAll('button')].map((b) => b.textContent),
  afterStatus: !!n.parentElement.previousElementSibling && n.parentElement.previousElementSibling.classList.contains('hub-status'),
}))).catch(() => null);
const noticeOf = async (app, kind) => ((await noticeState(app)) || []).find((n) => n.kind === kind) || null;

/** 頂部列品牌名旁的版號（#appVer，versionrules.js）：看不看得到以 getComputedStyle 與大小判斷（不信 hidden 屬性）；前綴另外判斷（窄螢幕省略） */
const versionBadge = (app) => app.page.evaluate(() => {
  const b = document.getElementById('appVer');
  if (!b) return null;
  const r = b.getBoundingClientRect();
  const top = document.querySelector('.topbar').getBoundingClientRect();
  const pre = b.querySelector('.app-ver-pre');
  const time = b.querySelector('.app-ver-time');
  return {
    shown: getComputedStyle(b).display !== 'none' && r.width > 0 && r.right <= top.right && r.top >= top.top && r.bottom <= top.bottom,
    preShown: !!pre && getComputedStyle(pre).display !== 'none' && pre.getBoundingClientRect().width > 0,
    timeShown: !!time && getComputedStyle(time).display !== 'none' && time.getBoundingClientRect().width > 0,
    time: time ? time.textContent : null,
    numShown: (() => { const n = b.querySelector('.app-ver-num'); return !!n && getComputedStyle(n).display !== 'none' && n.getBoundingClientRect().width > 0; })(),
    // 日期在時刻上方（手機寬度疊成兩行）或左邊（同一行）
    stacked: (() => {
      const d = b.querySelector('.app-ver-date'), c = b.querySelector('.app-ver-clock');
      return !!d && !!c && c.getBoundingClientRect().top >= d.getBoundingClientRect().bottom - 1;
    })(),
    pre: pre ? pre.textContent : null, text: b.querySelector('.app-ver-num')?.textContent ?? null, tone: b.dataset.tone ?? null,
    tag: b.tagName.toLowerCase(), href: b.getAttribute('href'), target: b.getAttribute('target'), rel: b.getAttribute('rel'), title: b.title, label: b.getAttribute('aria-label'),
  };
});

/** 退路框：開著時回文字框的值、唯讀、全選了沒、框裡有沒有其他元素（不該有 HTML） */
const fallbackState = (app) => app.page.evaluate(() => {
  const d = document.querySelector('.dlg-copy-fallback');
  if (!d) return null;
  const ta = d.querySelector('textarea');
  return {
    value: ta ? ta.value : null, readOnly: !!ta && ta.readOnly, selected: !!ta && ta.selectionStart === 0 && ta.selectionEnd === ta.value.length,
    focused: document.activeElement === ta, imgs: d.querySelectorAll('img').length, taChildren: ta ? ta.children.length : null,
    title: d.querySelector('.dlg-title').textContent, buttons: [...d.querySelectorAll('.dlg-foot button')].map((b) => b.textContent),
  };
}).catch(() => null);
const anyDialog = (app) => app.page.evaluate(() => document.querySelectorAll('.dlg-back').length);

/** Assistant 項目的複製鈕：文字與狀態 */
const copyBtn = (app, convId, itemId) => app.page.evaluate((sel) => {
  const b = document.querySelector(sel + ' .it-acts .code-copy');
  return b ? { text: b.lastChild.textContent, busy: b.classList.contains('busy'), done: b.classList.contains('done') } : null;
}, itemSel(convId, itemId));

async function runControlBanner() {
  const app = await openWithLayout('control-banner', layoutOf([[[LF.CONV.idle], LF.CONV.idle]]));
  const { page, hub } = app;
  await waitLoaded(app, [LF.CONV.idle]);
  const before = await noticeState(app);
  hub.push(LF.controlMessage({ defaultPassword: true, tier1Until: NOW + 3600e3, tier2Until: NOW + 600e3 }));
  const shown = await until(() => noticeOf(app, 'control'), 3000);
  await shot(app, 'hub-default-password-1440');
  // 同一則再送一次：不重複
  hub.push(LF.controlMessage({ defaultPassword: true }));
  await sleep(300);
  const rows = await noticeState(app);
  check('hubControlDefaultPasswordBanner', Array.isArray(before) && before.length === 0 && !!shown && shown.text === DEFAULT_PASSWORD_TEXT && shown.tone === 'warn' && shown.role === 'alert'
    && shown.afterStatus && JSON.stringify(shown.links) === JSON.stringify([{ href: '/', text: '改密碼' }]) && !/\d{1,2}:\d{2}/.test(shown.text)
    && rows.filter((r) => r.kind === 'control').length === 1,
  { before, shown, rows: rows && rows.length });
  // 預設密碼改掉了（defaultPassword 不是 true）：拿掉；「改密碼」連到登入頁 /
  hub.push(LF.controlMessage({ defaultPassword: true }));
  hub.push(LF.controlMessage());
  const gone = await until(async () => ((await noticeOf(app, 'control')) ? null : true), 3000);
  hub.push(LF.controlMessage({ defaultPassword: true }));
  await until(() => noticeOf(app, 'control'), 3000);
  const navBefore = app.navigations.length;
  await page.click('.live-notice[data-notice="control"] a[data-field="changePassword"]');
  const nav = await until(() => app.navigations.slice(navBefore).find((n) => n.url === HP.ORIGIN + '/') || null, 4000);
  check('hubControlFalseHidesBanner', !!gone && !!nav, { hidden: !!gone, navigatedTo: nav && nav.url });
  await HP.closePage(app);

  // 窄螢幕：警告與「改密碼」換行，不超出畫面
  const narrow = await openWithLayout('control-banner-390', layoutOf([[[LF.CONV.idle], LF.CONV.idle]]), { open: { vp: { w: 390, h: 844 } } });
  await waitLoaded(narrow, [LF.CONV.idle]);
  narrow.hub.push(LF.controlMessage({ defaultPassword: true }));
  await until(() => noticeOf(narrow, 'control'), 3000);
  await shot(narrow, 'hub-default-password-390');
  const hs = await hScroll(narrow);
  check('hubNotice390NoHScroll', hs.docScroll <= hs.innerWidth && hs.bodyScroll <= hs.innerWidth && hs.wide.length === 0, hs);
  await HP.closePage(narrow);
}

/** 第七批：瀏覽器版分頁圖示的燈——有對話等你回應時琥珀色加數字、只有執行中時綠點、都沒有時換回原本的圖示（樣子變了才重畫） */
async function runTabBadge() {
  const a = await openWithLayout('tab-badge', layoutOf([[[LF.CONV.idle], LF.CONV.idle]]));
  await waitLoaded(a, [LF.CONV.idle]);
  const icon = () => a.page.evaluate(() => {
    const l = document.querySelector('link[rel="icon"]');
    return l ? { href: (l.getAttribute('href') || '').slice(0, 22), badge: l.dataset.badge || '', links: document.querySelectorAll('link[rel="icon"]').length } : null;
  });
  const list = LF.liveList(NOW);
  const waits = list.filter((c) => c.waiting).length;
  const wait0 = await until(async () => { const i = await icon(); return i && i.badge.startsWith('wait:') ? i : null; }, 3000);
  a.hub.push({ type: 'conversations', items: list.map((c) => Object.assign({}, c, { waiting: false })) });
  const run = await until(async () => { const i = await icon(); return i && i.badge === 'run:' ? i : null; }, 3000);
  a.hub.push({ type: 'conversations', items: list.map((c) => Object.assign({}, c, { waiting: false, running: false })) });
  const none = await until(async () => { const i = await icon(); return i && i.href === 'favicon.ico' && !i.badge ? i : null; }, 3000);
  check('hubTabBadge', waits > 0 && !!wait0 && wait0.badge === 'wait:' + Math.min(waits, 9) + (waits > 9 ? '+' : '') && wait0.href.startsWith('data:image/png')
    && !!run && run.href.startsWith('data:image/png') && !!none && none.links === 1, { waits, wait0, run, none });
  await HP.closePage(a);
}

async function runNotify() {
  // 已授權、分頁在背景：系統通知（標題照字面）；點了回到這個分頁、聚焦那個對話
  const title = '修正登入流程 ' + LF.XSS;
  const a = await openWithLayout('notify-system', layoutOf([[[LF.CONV.idle], LF.CONV.idle]]), { open: { initOpts: { notification: 'granted' } } });
  await waitLoaded(a, [LF.CONV.idle]);
  await HP.setHidden(a, true);
  a.hub.push(LF.notifyMessage(LF.CONV.running, 'approval', title));
  const shown = await until(async () => { const n = await HP.notifications(a); return n.shown.length ? n : null; }, 3000);
  const toastsA = await HP.domToasts(a);
  await HP.setHidden(a, false);
  const clicked = await HP.clickNotification(a, 0);
  const focused = await until(async () => { const s = await wsState(a); return s.panes.some((p) => p.active === LF.CONV.running) ? s : null; }, 3000);
  const afterClick = await HP.notifications(a);
  const xssA = await HP.xssState(a);
  const n0 = shown && shown.shown[0];
  check('hubNotifyHiddenSystem', !!n0 && shown.shown.length === 1 && n0.title === 'OFFICE-PC：' + title && n0.body === '等你核准' && n0.tag === 'myriagent-' + LF.CONV.running
    && toastsA.length === 0 && clicked && !!focused && afterClick.shown[0].closed === true && xssA.xss === 0 && shown.requests === 0,
  { notification: n0, toasts: toastsA, clicked, closedAfterClick: afterClick.shown[0].closed, panes: focused && focused.panes, xss: xssA.xss });
  await HP.closePage(a);

  // 分頁在前景（已授權）：頁內提示、沒有系統通知
  const b = await openWithLayout('notify-toast', layoutOf([[[LF.CONV.idle], LF.CONV.idle]]), { open: { initOpts: { notification: 'granted' } } });
  await waitLoaded(b, [LF.CONV.idle]);
  b.hub.push(LF.notifyMessage(LF.CONV.running, 'completed', '整理用量報表'));
  const toastB = await until(async () => (await HP.domToasts(b)).find((t) => t.includes('整理用量報表')) || null, 3000);
  const notesB = await HP.notifications(b);
  check('hubNotifyVisibleToast', toastB === 'OFFICE-PC：整理用量報表・已完成' && notesB.shown.length === 0 && !(await noticeOf(b, 'notify')),
    { toast: toastB, notifications: notesB.shown.length });
  await HP.closePage(b);

  // 設定關閉：什麼都不顯示（背景、已授權也一樣），也不出現「開啟瀏覽器通知」
  const c = await openWithLayout('notify-off', layoutOf([[[LF.CONV.idle], LF.CONV.idle]]),
    { initOpts: { settings: { notifications: false } }, open: { initOpts: { notification: 'default' } } });
  await waitLoaded(c, [LF.CONV.idle]);
  c.hub.push(LF.notifyMessage(LF.CONV.running, 'question', '前景'));
  await HP.setHidden(c, true);
  await HP.setNotification(c, 'granted');
  c.hub.push(LF.notifyMessage(LF.CONV.running, 'question', '背景'));
  await sleep(800);
  await HP.setHidden(c, false);
  const notesC = await HP.notifications(c);
  const toastsC = await HP.domToasts(c);
  check('hubNotifyOffNone', notesC.shown.length === 0 && toastsC.length === 0 && !(await noticeOf(c, 'notify')) && (await HP.msgsOf(c, 'notify')).length === 2,
    { notifications: notesC.shown.length, toasts: toastsC, received: (await HP.msgsOf(c, 'notify')).length });
  await HP.closePage(c);

  // 權限還沒決定：第一次收到 notify 才出現「開啟瀏覽器通知」，按下才要求授權；授權之後背景的 notify 用系統通知
  const d = await openWithLayout('notify-permission', layoutOf([[[LF.CONV.idle], LF.CONV.idle]]));
  await waitLoaded(d, [LF.CONV.idle]);
  const beforeAny = await noticeOf(d, 'notify');
  d.hub.push(LF.notifyMessage(LF.CONV.running, 'failed', '跑測試'));
  const offer = await until(() => noticeOf(d, 'notify'), 3000);
  const req0 = (await HP.notifications(d)).requests;
  const toastD = (await HP.domToasts(d)).find((t) => t.includes('跑測試')) || null;
  await shot(d, 'hub-notify-permission-1440');
  await d.page.click('.live-notice[data-notice="notify"] button[data-field="enableNotifications"]');
  const after = await until(async () => { const n = await HP.notifications(d); return n.requests === 1 ? n : null; }, 3000);
  const offerGone = !(await noticeOf(d, 'notify'));
  await HP.setHidden(d, true);
  d.hub.push(LF.notifyMessage(LF.CONV.running, 'completed', '跑測試'));
  const sys = await until(async () => { const n = await HP.notifications(d); return n.shown.length ? n : null; }, 3000);
  await HP.setHidden(d, false);
  check('hubNotifyPermissionButton', beforeAny === null && !!offer && JSON.stringify(offer.buttons.slice(0, 1)) === JSON.stringify(['開啟瀏覽器通知']) && req0 === 0
    && toastD === 'OFFICE-PC：跑測試・發生錯誤，沒有完成' && !!after && after.permission === 'granted' && offerGone && !!sys && sys.shown[0].body === '已完成',
  { offer, requestsBeforeClick: req0, toast: toastD, after: after && { requests: after.requests, permission: after.permission }, offerGone, system: sys && sys.shown });
  await HP.closePage(d);
}

async function runSpeak() {
  // 瀏覽器版的語音通知（speak，voice.js）：瀏覽器不准發聲（not-allowed）時記著、出現「播放」提示，點了才補念；
  // 收到就開始閃那個對話（attnflash.js：念不出來、語音關掉也閃），念出聲時標頭另外閃三下，點了那個對話才停；
  // 同一筆事件不重念、新的照念、句子用臺灣華語的聲音、預設的 1.5 倍速與升 2 個半音（pitch 1.122）；這個瀏覽器的開關關掉就不念；文字只交給語音合成、不進 DOM。
  // 擋下發聲以假語音合成的 deny 模擬：登入頁點過、打過字，頁面已經有使用者啟用，不能靠「還沒點過」
  const a = await openWithLayout('speak', layoutOf([[[LF.CONV.idle], LF.CONV.idle]]));
  await waitLoaded(a, [LF.CONV.idle]);
  const said = async () => { const s = await HP.speech(a); return { spoken: s.spoken.filter((x) => !x.denied), denied: s.spoken.filter((x) => x.denied).length }; };
  const flashOn = () => a.page.evaluate((id) => (!!document.querySelector('.crow.attn-flash[data-key="c:' + id + '"]')
    || !!document.querySelector('.tab.attn-flash[data-key="' + id + '"]')) && !!document.querySelector('.conv[data-conv="' + id + '"] > .ch.attn-flash'), LF.CONV.idle);
  const first = '「整理報表」已完成：月報已經匯出成 Excel ' + LF.XSS;
  await a.page.evaluate(() => { window.__speech.deny = true; });
  a.hub.push({ type: 'speak', conversationId: LF.CONV.idle, id: 101, text: first });
  const offer = await until(() => noticeOf(a, 'voice'), 3000);
  const blocked = await said();
  const beforeClick = blocked.denied === 1 ? blocked.spoken.length : -1;
  const flashedBlocked = await flashOn(); // 念不出來也先閃
  await a.page.evaluate(() => { window.__speech.deny = false; });
  await shot(a, 'hub-speak-blocked-1440');
  await a.page.click('.live-notice[data-notice="voice"] button[data-field="playVoice"]');
  const replayed = await until(async () => { const s = await said(); return s.spoken.length ? s : null; }, 3000);
  const burst = await until(() => a.page.evaluate((id) => !!document.querySelector('.conv[data-conv="' + id + '"] > .ch.ch-flash'), LF.CONV.idle), 2000);
  const flashed = await flashOn(); // 按「播放」不是點那個對話：照閃
  const offerGone = !(await noticeOf(a, 'voice'));

  a.hub.push({ type: 'speak', conversationId: LF.CONV.idle, id: 101, text: first });
  a.hub.push({ type: 'speak', conversationId: LF.CONV.idle, id: 102, text: '「整理報表」有問題想問你：要用哪個格式？' });
  const second = await until(async () => { const s = await said(); return s.spoken.length >= 2 ? s : null; }, 3000);
  await sleep(400);
  const afterDup = await said();

  await a.page.click('.conv[data-conv="' + LF.CONV.idle + '"] .ch-top', { position: { x: 4, y: 4 } }); // 點那個對話（標題列左上角的留白）
  const stopped = !(await flashOn());

  await a.page.evaluate(() => localStorage.setItem('myriagent.voice', 'off'));
  a.hub.push({ type: 'speak', conversationId: LF.CONV.idle, id: 103, text: '關掉之後不念' });
  const flashedOff = await until(() => flashOn(), 2000); // 語音關掉也閃
  await sleep(400);
  const afterOff = await said();
  await a.page.evaluate(() => localStorage.removeItem('myriagent.voice'));
  const xss = await HP.xssState(a);
  const s0 = replayed && replayed.spoken[0];
  check('hubSpeak', !!offer && JSON.stringify(offer.buttons.slice(0, 1)) === JSON.stringify(['播放']) && beforeClick === 0
    && !!s0 && s0.text === first && s0.lang === 'zh-TW' && s0.voice === 'Probe 臺灣華語' && s0.rate === 1.5 && s0.pitch === 1.122
    && flashedBlocked && !!burst && flashed && stopped && !!flashedOff
    && offerGone && !!second && afterDup.spoken.length === 2 && afterDup.spoken[1].text === '「整理報表」有問題想問你：要用哪個格式？'
    && afterOff.spoken.length === 2 && xss.xss === 0,
  { offer, beforeClick, first: s0, flashedBlocked, burst: !!burst, flashed, stopped, flashedOff: !!flashedOff, offerGone,
    spoken: afterDup.spoken.map((x) => x.text), afterOff: afterOff.spoken.length, xss: xss.xss });
  await HP.closePage(a);
}

async function runVoiceMessage() {
  // 瀏覽器版的語音訊息（voicemsg.js）：麥克風鈕開始錄音（紅色、計時、取消鈕），再按一下停止：錄音以 E4 上傳（檔名「語音訊息_….weba」），
  // 再送 voiceMessage {conversationId, uploads:[那一筆]} 給對話所在的電腦（那台辨識、交給代理）；結果回來前按鈕是送出中（停用），done 之後恢復，
  // 麥克風關掉。沒有麥克風權限時說明、不送。不碰這台真的麥克風（fake-mic.mjs）
  const app = await openUploadConv('voice-message');
  const { hub, page } = app;
  const sel = convSel(LF.CONV.idle);
  const mic = sel + ' .cbtn.mic';
  const micState = () => page.evaluate((s) => {
    const b = document.querySelector(s);
    const w = b.closest('.mic-wrap');
    return { shown: getComputedStyle(b).display !== 'none', disabled: b.disabled, listening: b.classList.contains('listening'), sending: b.classList.contains('sending'),
      time: w.querySelector('.mic-time').hidden ? null : w.querySelector('.mic-time').textContent, cancel: !w.querySelector('.mic-cancel').hidden,
      requests: window.__mic.requests, stopped: window.__mic.tracksStopped };
  }, mic);
  hub.onCmd('voiceMessage', (cmd, arg) => ({ push: [serverResult(arg.commandId, 'done')], delayMs: 600 }));
  const s0 = await micState();
  await page.click(mic);
  const recording = await until(async () => { const st = await micState(); return st.listening && st.time ? st : null; }, 2000);
  await shot(app, 'hub-voice-recording-1440');
  await sleep(800);
  await page.click(mic);
  const sendingState = await until(async () => { const st = await micState(); return st.sending ? st : null; }, 2000);
  const sent = await until(() => (hub.cmds('voiceMessage').length ? hub.cmds('voiceMessage').at(-1).cmd : null), 4000);
  const settled = await until(async () => { const st = await micState(); return !st.sending && !st.disabled ? st : null; }, 4000);
  const up = app.uploadLog.uploads.at(-1) || null;
  const entry = sent && Array.isArray(sent.uploads) ? sent.uploads[0] : null;

  // 沒有麥克風權限：說明、不送
  await page.evaluate(() => { window.__mic.deny = 'NotAllowedError'; });
  const before = hub.cmds('voiceMessage').length;
  await page.click(mic);
  await sleep(300);
  const deniedToasts = await page.$$eval('.toast', (n) => n.map((t) => t.textContent));
  const xss = await HP.xssState(app);
  check('hubVoiceMessage', s0.shown && !s0.disabled && !!recording && recording.cancel && !!sendingState && sendingState.disabled
    && !!sent && sent.conversationId === LF.CONV.idle && Array.isArray(sent.uploads) && sent.uploads.length === 1
    && !!entry && /^語音訊息_\d{8}_\d{6}\.weba$/.test(entry.name) && !!up && up.id === entry.uploadId && up.method === 'PUT' && entry.size === up.size
    && !!settled && settled.stopped >= 1 && !settled.listening
    && hub.cmds('voiceMessage').length === before && deniedToasts.some((t) => /麥克風的權限/.test(t)) && xss.xss === 0,
  { s0, recording, sendingState, sent, upload: up, settled, deniedToasts, xss: xss.xss });
  await HP.closePage(app);
}

async function runVoiceDictation() {
  // 手機與平板的瀏覽器版（390×844、觸控）：麥克風鈕改用系統的語音辨識（fake-mic.mjs 的假辨識器，不連語音服務）——按一下開始聽
  // （zh-TW、連續、即時結果），說的話即時打進輸入框的游標處（Android 越說越長的確定結果不重複），再按一下停止、字留著、不送出也不錄音；
  // 送出照一般的 send；聽寫中自己打字就停、之後的結果不蓋掉打的字；送出時停、之後的結果不寫回。系統不讓瀏覽器用（service-not-allowed）時
  // 說明並改回錄音（再按一下開始錄音、Esc 取消）。整段沒有 voiceMessage、沒有上傳
  const id = LF.CONV.idle;
  const app = await openWithLayout('voice-dictation', layoutOf([[[id], id]]), { open: { vp: { w: 390, h: 844 }, hasTouch: true } });
  await waitLoaded(app, [id]);
  const { hub, page } = app;
  await page.click('#btnSidebar'); // 窄螢幕的側欄是蓋在工作區上的抽屜，先收起來（同 runMobileComposer）
  await sleep(200);
  hub.onCmd('send', (cmd, arg) => ({ push: [serverResult(arg.commandId, 'done')] }));
  const mic = convSel(id) + ' .cbtn.mic';
  const st = () => page.evaluate(([m, t]) => {
    const b = document.querySelector(m);
    const w = b.closest('.mic-wrap');
    const sr = window.__sr.instances;
    const r = sr[sr.length - 1] || null;
    return { shown: getComputedStyle(b).display !== 'none', disabled: b.disabled, listening: b.classList.contains('listening'), title: b.title,
      time: !w.querySelector('.mic-time').hidden, cancel: !w.querySelector('.mic-cancel').hidden, value: document.querySelector(t).value,
      recognizers: sr.length, last: r && { lang: r.lang, continuous: r.continuous, interim: r.interimResults, started: r.started, stopped: r.stopped, aborted: r.aborted },
      micRequests: window.__mic.requests, recorders: window.__mic.recorders.length };
  }, [mic, taSel(id)]);
  const emit = (list) => page.evaluate((l) => window.__sr.emit(l), list);

  const s0 = await st();
  await page.tap(taSel(id));
  await page.keyboard.type('請幫我');
  await page.tap(mic);
  const listening = await until(async () => { const x = await st(); return x.listening ? x : null; }, 2000);
  // 聽寫的預覽列（輸入框上方）：只列還沒確定的字；全部確定後寫「正在聽…」；停下就收起來
  const pv = () => page.evaluate((t) => { const p = document.querySelector(t).parentElement.querySelector('.dict-preview'); return p ? { hidden: p.hidden, idle: p.classList.contains('idle'), text: p.querySelector('.dict-text').textContent } : null; }, taSel(id));
  const pv0 = await pv();
  await emit([['整理', false]]);
  const interim = (await st()).value;
  const pv1 = await pv();
  await emit([['整理報表', true], ['然後', false]]);
  const mixed = (await st()).value;
  const pv2 = await pv();
  await emit([['整理報表', true], ['整理報表然後寄出', true]]); // Android：越說越長的確定結果
  const android = (await st()).value;
  const pv3 = await pv();
  await shot(app, 'hub-voice-dictation-390');
  await page.tap(mic);
  const stopped = await until(async () => { const x = await st(); return !x.listening ? x : null; }, 2000);
  const pv4 = await pv();
  check('hubVoiceDictationPreview', !!pv0 && !pv0.hidden && pv0.idle && pv0.text === '正在聽…' && !!pv1 && !pv1.idle && pv1.text === '整理'
    && !!pv2 && pv2.text === '然後' && !!pv3 && pv3.idle && !!pv4 && pv4.hidden, { pv0, pv1, pv2, pv3, pv4 });
  await page.tap(convSel(id) + ' .cbtn.send');
  const frame = await until(() => hub.cmds('send')[0] || null, 3000);
  await sleep(300);
  const afterSend = await st();

  // 聽寫中自己打字：停下、之後才到的結果不蓋掉打的字
  await page.tap(mic);
  await until(async () => { const x = await st(); return x.listening && x.recognizers === 2 ? x : null; }, 2000);
  await emit([['先說一段', false]]);
  await page.tap(taSel(id));
  await page.keyboard.type('X');
  const typedStop = await until(async () => { const x = await st(); return !x.listening ? x : null; }, 2000);
  await emit([['不該出現', true]]);
  const typedLate = await st();
  await page.evaluate((t) => { const ta = document.querySelector(t); ta.value = ''; ta.dispatchEvent(new Event('input', { bubbles: true })); }, taSel(id));

  // 送出時停下：之後才到的結果不寫回
  await page.tap(mic);
  await until(async () => { const x = await st(); return x.listening && x.recognizers === 3 ? x : null; }, 2000);
  await emit([['送出前', true]]);
  await page.tap(convSel(id) + ' .cbtn.send');
  const frame2 = await until(() => hub.cmds('send')[1] || null, 3000);
  await emit([['送出前', true], ['送出後', true]]);
  await sleep(300);
  const sentStop = await st();

  // 系統不讓瀏覽器用：說明、這一頁改回錄音
  await page.tap(mic);
  await until(async () => { const x = await st(); return x.listening && x.recognizers === 4 ? x : null; }, 2000);
  await page.evaluate(() => window.__sr.fail('service-not-allowed'));
  const fellBack = await until(async () => { const x = await st(); return !x.listening && /^語音訊息：/.test(x.title) ? x : null; }, 2000);
  const toasts = await page.$$eval('.toast', (n) => n.map((t) => t.textContent));
  await page.tap(mic);
  const recording = await until(async () => { const x = await st(); return x.listening && x.time ? x : null; }, 2000);
  await page.keyboard.press('Escape');
  const cancelled = await until(async () => { const x = await st(); return !x.listening ? x : null; }, 2000);
  await sleep(200);
  const xss = await HP.xssState(app);
  check('hubVoiceDictation', s0.shown && !s0.disabled && /^語音輸入：/.test(s0.title) && !!listening && !listening.time && !listening.cancel
    && !!listening.last && listening.last.lang === 'zh-TW' && listening.last.continuous && listening.last.interim && listening.last.started === 1
    && interim === '請幫我整理' && mixed === '請幫我整理報表然後' && android === '請幫我整理報表然後寄出'
    && !!stopped && stopped.value === '請幫我整理報表然後寄出' && stopped.last.stopped === 1
    && !!frame && frame.cmd.text === '請幫我整理報表然後寄出' && afterSend.value === ''
    && !!typedStop && typedStop.last.aborted === 1 && typedLate.value === '先說一段X'
    && !!frame2 && frame2.cmd.text === '送出前' && sentStop.last.aborted === 1 && !/送出後/.test(sentStop.value)
    && !!fellBack && toasts.some((t) => /改成錄音/.test(t)) && !!recording && recording.micRequests === 1 && recording.recognizers === 4
    && !!cancelled && cancelled.recorders === 1 && hub.cmds('voiceMessage').length === 0 && s0.micRequests === 0 && xss.xss === 0,
  { s0, listening, interim, mixed, android, stopped, sent: frame && frame.cmd, afterSend, typedStop, typedLate, sent2: frame2 && frame2.cmd, sentStop,
    fellBack, toasts, recording, cancelled, voiceMessages: hub.cmds('voiceMessage').length, xss: xss.xss });
  await HP.closePage(app);
}

async function runVoiceDictationRestart() {
  // 手機的聽寫按了講話沒反應（2026-10-24 使用者回報：時好時壞、換個對話有時又好了）。以假辨識器的 exclusive（同時只能一個在聽：
  // 前一個還沒 end 就開始的新辨識器聽不到）、stopDelay（按停之後要等最後一段）、silent（卡住）與假語音合成的 durationMs 重現：
  // - hubVoiceDictationRetap：按停之後馬上再按，先等前一個結束（最後一段照樣寫進去、預覽列不再出現）才開始，新的聽得到；
  //   前一個一直不結束（按停之後 5 秒才 end）時約 0.45 秒中止它再開始
  // - hubVoiceDictationSpeech：正在念語音通知時按，先停下語音通知、稍等再開始；聽寫中到的通知先記著，停下之後兩句都補念
  // - hubVoiceDictationSilent：辨識器卡住（什麼事件都沒有）時說明並回到閒置；自己結束而沒聽到字時說明；Android 說完一段就結束（有字）不說
  const id = LF.CONV.idle;
  const app = await openWithLayout('voice-dictation-restart', layoutOf([[[id], id]]), { open: { vp: { w: 390, h: 844 }, hasTouch: true } });
  await waitLoaded(app, [id]);
  const { hub, page } = app;
  await page.click('#btnSidebar'); // 窄螢幕的側欄是蓋在工作區上的抽屜，先收起來
  await sleep(200);
  const mic = convSel(id) + ' .cbtn.mic';
  const ta = taSel(id);
  const st = () => page.evaluate(([m, t]) => {
    const b = document.querySelector(m);
    const p = document.querySelector(t).parentElement.querySelector('.dict-preview');
    return { listening: b.classList.contains('listening'), value: document.querySelector(t).value, preview: !!p && !p.hidden, dropped: window.__sr.dropped,
      rs: window.__sr.instances.map((r) => ({ started: r.started, stopped: r.stopped, aborted: r.aborted, live: r.live, deaf: r.deaf, began: r.began, createdAt: r.createdAt })) };
  }, [mic, ta]);
  const began = (n, ms = 3000) => until(async () => { const x = await st(); return x.rs.length === n && x.rs[n - 1].began && x.listening ? x : null; }, ms);
  const idle = (ms = 3000) => until(async () => { const x = await st(); return !x.listening ? x : null; }, ms);
  const emit = (list, i) => page.evaluate(([l, k]) => window.__sr.emit(l, k), [list, i]);
  const toasts = () => page.$$eval('.toast', (n) => n.map((t) => t.textContent));
  const clear = () => page.evaluate((t) => { const el = document.querySelector(t); el.value = ''; el.dispatchEvent(new Event('input', { bubbles: true })); }, ta);
  const said = () => HP.speech(app);
  await page.evaluate(() => { window.__sr.exclusive = true; window.__sr.stopDelay = 300; });

  // 按停之後馬上再按：前一個 300 毫秒後才結束
  await page.tap(mic);
  const one = await began(1);
  await emit([['第一段', false]]);
  await page.tap(mic); // 停：等最後一段
  await emit([['第一段話', true]], 0); // 停下之後才到的最後一段
  const late = await st();
  await page.tap(mic); // 馬上再按
  const two = await began(2);
  await emit([['第二段', false]], 1);
  const twoValue = (await st()).value;
  await page.tap(mic);
  const twoStopped = await until(async () => { const x = await st(); return !x.listening && x.rs.every((r) => !r.live) ? x : null; }, 3000);
  // 前一個一直不結束：中止它再開始
  await clear();
  await page.evaluate(() => { window.__sr.stopDelay = 5000; });
  await page.tap(mic);
  await began(3);
  await page.tap(mic);
  const t3 = Date.now();
  await page.tap(mic);
  const four = await began(4);
  const fourMs = Date.now() - t3;
  await emit([['第三段', false]], 3);
  const fourValue = (await st()).value;
  await page.evaluate(() => { window.__sr.stopDelay = 5; });
  await page.tap(mic);
  await idle();
  const toastsRetap = await toasts();
  check('hubVoiceDictationRetap', !!one && late.value === '第一段話' && !late.preview && !late.listening
    && !!two && !two.rs[1].deaf && !two.rs[0].live && two.rs[0].aborted === 0 && twoValue === '第一段話第二段' && !!twoStopped && twoStopped.dropped === 0
    && !!four && four.rs[2].aborted === 1 && !four.rs[3].deaf && fourValue === '第三段' && fourMs < 1500
    && !toastsRetap.some((t) => /沒有聽到|中斷|沒有回應/.test(t)),
  { late, two: two && two.rs, twoValue, dropped: twoStopped && twoStopped.dropped, four: four && four.rs, fourMs, fourValue, toasts: toastsRetap });

  // 正在念語音通知時按：停下、稍等再開始；聽寫中到的先記著，停下之後補念
  await clear();
  await page.evaluate(() => { window.__speech.durationMs = 3000; });
  hub.push({ type: 'speak', conversationId: id, id: 201, text: '「整理報表」已完成' });
  const talking = await until(() => page.evaluate(() => (window.speechSynthesis.speaking ? window.__speech.spoken.length : 0)), 3000);
  const sp0 = await said();
  const tapAt = Date.now();
  await page.tap(mic);
  const five = await began(5);
  const sp1 = await said();
  hub.push({ type: 'speak', conversationId: id, id: 202, text: '「寄出報表」有問題想問你' });
  await sleep(500);
  const sp2 = await said();
  const quietWhile = await page.evaluate(() => window.speechSynthesis.speaking);
  await page.tap(mic);
  const sp3 = await until(async () => { const s = await said(); return s.spoken.length >= sp2.spoken.length + 2 ? s : null; }, 3000);
  const replay = sp3 ? sp3.spoken.slice(sp2.spoken.length).map((x) => x.text) : null;
  await page.evaluate(() => { window.__speech.durationMs = 30; window.speechSynthesis.cancel(); });
  check('hubVoiceDictationSpeech', talking === 1 && !!five && sp1.cancels > sp0.cancels && five.rs[4].createdAt - tapAt >= 250
    && sp2.spoken.length === sp1.spoken.length && !quietWhile
    && !!replay && replay[0] === '「整理報表」已完成' && replay[1] === '「寄出報表」有問題想問你',
  { talking, cancels: [sp0.cancels, sp1.cancels], startDelayMs: five && five.rs[4].createdAt - tapAt, spokenWhile: sp2.spoken.length - sp1.spoken.length, quietWhile, replay });

  // 辨識器卡住：8 秒後說明、回到閒置；自己結束而沒聽到字時說明；說完一段就結束（有字）不說
  await page.evaluate(() => { window.__sr.exclusive = false; window.__sr.silent = true; });
  await page.tap(mic);
  await sleep(300);
  const stuck = await st();
  const unstuck = await idle(12000);
  const toastsStuck = await toasts();
  await page.evaluate(() => { window.__sr.silent = false; });
  await page.tap(mic);
  await began(7, 4000);
  await page.evaluate(() => window.__sr.fail('no-speech'));
  await idle();
  const toastsQuiet = await toasts();
  // 手機寬度的通知在畫面上方，不蓋住它要你按的麥克風（以前在右下角，正好蓋在麥克風與送出鈕上）
  const toastOverMic = await page.evaluate((m) => {
    const b = document.querySelector(m).getBoundingClientRect();
    const list = [...document.querySelectorAll('.toast')].map((t) => t.getBoundingClientRect());
    return { toasts: list.length, over: list.filter((r) => !(r.bottom <= b.top || r.top >= b.bottom || r.right <= b.left || r.left >= b.right)).length };
  }, mic);
  await page.tap(mic);
  await began(8);
  await emit([['說完一段', true]]);
  await page.evaluate(() => window.__sr.end());
  const ended = await idle();
  await sleep(200);
  const toastsEnded = await toasts();
  const xss = await HP.xssState(app);
  check('hubVoiceDictationSilent', stuck.listening && !!unstuck && unstuck.rs[5].aborted === 1 && toastsStuck.some((t) => /語音辨識沒有回應/.test(t))
    && toastsQuiet.some((t) => /沒有聽到聲音/.test(t)) && !!ended && ended.value === '說完一段'
    && toastsEnded.filter((t) => /沒有聽到聲音/.test(t)).length === toastsQuiet.filter((t) => /沒有聽到聲音/.test(t)).length && xss.xss === 0
    && toastOverMic.toasts > 0 && toastOverMic.over === 0,
  { stuck: stuck.listening, unstuck: unstuck && unstuck.rs[5], toastsStuck, toastsQuiet, toastOverMic, ended: ended && ended.value, toastsEnded, xss: xss.xss });
  await HP.closePage(app);
}

async function runVoiceDictationIos() {
  // iPhone／iPad 的聽寫（2026-10-27 使用者回報：第一次正常、第二次「正在聽」卻打不出字；打進去的有些被截斷、有些重複）：
  // - hubVoiceDictationIosText：iOS 的連續辨識只有一個越說越長的結果、停頓之後清掉重來、重送同一段、最後給從頭到尾的確定結果、
  //   中途給空的結果——輸入框的字不被截斷、不重複
  // - hubVoiceDictationIosDeaf：之後的辨識器照樣發 start、卻收不到聲音（假辨識器的 mute；WebKit bug 321436）：約 9 秒後停下、說明是 iOS 的問題，
  //   這一頁的麥克風改成錄音（再按一下開始錄音、Esc 取消），不送 voiceMessage
  const id = LF.CONV.idle;
  const app = await openWithLayout('voice-dictation-ios', layoutOf([[[id], id]]), { open: { vp: { w: 390, h: 844 }, hasTouch: true } });
  await waitLoaded(app, [id]);
  const { hub, page } = app;
  await page.click('#btnSidebar'); // 窄螢幕的側欄是蓋在工作區上的抽屜，先收起來
  await sleep(200);
  const mic = convSel(id) + ' .cbtn.mic';
  const ta = taSel(id);
  const st = () => page.evaluate(([m, t]) => {
    const b = document.querySelector(m);
    const w = b.closest('.mic-wrap');
    return { listening: b.classList.contains('listening'), title: b.title, time: !w.querySelector('.mic-time').hidden, value: document.querySelector(t).value,
      recognizers: window.__sr.instances.length, dropped: window.__sr.dropped, recorders: window.__mic.recorders.length, micRequests: window.__mic.requests,
      began: window.__sr.instances.map((r) => r.began), aborted: window.__sr.instances.map((r) => r.aborted) };
  }, [mic, ta]);
  const began = (n, ms = 3000) => until(async () => { const x = await st(); return x.recognizers === n && x.began[n - 1] && x.listening ? x : null; }, ms);
  const idle = (ms = 3000) => until(async () => { const x = await st(); return !x.listening ? x : null; }, ms);
  const emit = async (list) => { await page.evaluate((l) => window.__sr.emit(l), list); return (await st()).value; };
  await page.evaluate(() => { window.__sr.stopDelay = 300; }); // 按停之後要等最後一段（同手機）

  await page.tap(mic);
  const one = await began(1);
  const values = [];
  values.push(await emit([['今天天氣', false]]));
  values.push(await emit([['今天天氣很好', false]]));
  values.push(await emit([['我們', false]])); // 停頓之後清掉重來
  values.push(await emit([['我們去公園', false]]));
  values.push(await emit([['我們去公園', true], ['我們去公園', true]])); // 重送同一段
  values.push(await emit([['', false]])); // 空的結果
  await page.tap(mic); // 停：最後給從頭到尾的確定結果
  values.push(await emit([['今天天氣很好我們去公園散步', true]]));
  const stopped = await idle();
  check('hubVoiceDictationIosText', !!one && !!stopped
    && JSON.stringify(values) === JSON.stringify(['今天天氣', '今天天氣很好', '今天天氣很好我們', '今天天氣很好我們去公園', '今天天氣很好我們去公園',
      '今天天氣很好我們去公園', '今天天氣很好我們去公園散步']) && stopped.value === '今天天氣很好我們去公園散步',
  { values, stopped: stopped && stopped.value });

  // 之後的辨識器收不到聲音
  await page.evaluate(() => { window.__sr.mute = true; });
  const tapAt = Date.now();
  await page.tap(mic);
  const two = await began(2);
  const mutedValue = await emit([['不會出現', false]]);
  const gaveUp = await idle(14000);
  const gaveUpMs = Date.now() - tapAt;
  const toasts = await page.$$eval('.toast', (n) => n.map((t) => t.textContent));
  await page.evaluate(() => { window.__sr.mute = false; });
  await page.tap(mic);
  const recording = await until(async () => { const x = await st(); return x.listening && x.time ? x : null; }, 3000);
  await page.keyboard.press('Escape');
  const cancelled = await idle();
  await sleep(200);
  const xss = await HP.xssState(app);
  check('hubVoiceDictationIosDeaf', !!two && mutedValue === '今天天氣很好我們去公園散步' && !!gaveUp && gaveUpMs >= 8000 && gaveUp.aborted[1] === 1
    && gaveUp.value === '今天天氣很好我們去公園散步' && /^語音訊息：/.test(gaveUp.title)
    && toasts.some((t) => /iOS 的已知問題/.test(t) && /改成錄音/.test(t)) && !toasts.some((t) => /沒有聽到聲音|語音辨識沒有回應/.test(t))
    && !!recording && recording.recognizers === 2 && recording.micRequests === 1
    && !!cancelled && cancelled.recorders === 1 && hub.cmds('voiceMessage').length === 0 && xss.xss === 0,
  { two: !!two, mutedValue, gaveUp, gaveUpMs, toasts, recording, cancelled, voiceMessages: hub.cmds('voiceMessage').length, xss: xss.xss });
  await HP.closePage(app);
}

const IPHONE_UA = 'Mozilla/5.0 (iPhone; CPU iPhone OS 27_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/27.2 Mobile/15E148 Safari/604.1';

/**
 * 聽寫時的音量格（第十一批，2026-11-08）：iPhone 的聽寫本來就先開著麥克風、每 100 ms 讀一次音量（voicemsg.js 的 gumHold），
 * 畫在錄音的那組 5 格上——麥克風有聲音時出現、有亮格；停下聽寫就藏起來
 */
async function runVoiceDictationMeter() {
  const id = LF.CONV.idle;
  const app = await openWithLayout('voice-dictation-meter', layoutOf([[[id], id]]), { open: { vp: { w: 390, h: 844 }, hasTouch: true, userAgent: IPHONE_UA } });
  await waitLoaded(app, [id]);
  const { page } = app;
  await page.click('#btnSidebar');
  await sleep(200);
  const mic = convSel(id) + ' .cbtn.mic';
  const meterOf = () => page.evaluate((c) => {
    const m = document.querySelector(c + ' .mic-meter');
    return m ? { shown: !m.hidden && m.getBoundingClientRect().width > 0, dict: m.classList.contains('dict'), lit: m.querySelectorAll('i.lit').length } : null;
  }, convSel(id));
  await page.evaluate(() => { window.__mic.tone = 0.6; });
  await page.tap(mic);
  const on = await until(async () => { const x = await meterOf(); return x && x.shown && x.lit > 0 ? x : null; }, 4000);
  await page.tap(mic);
  const off = await until(async () => { const x = await meterOf(); return x && !x.shown ? x : null; }, 4000);
  check('hubVoiceDictationMeter', !!on && on.dict && !!off && !off.dict && off.lit === 0, { on, off });
  await HP.closePage(app);
}

async function runVoiceDictationIosRepeat() {
  // iPhone 的聽寫關掉再開（2026-10-28 使用者回報：iOS 27 主畫面 app，第一次正常、關掉再開就再也打不出字）。假辨識器的 freshDeaf 模擬
  // iOS 27.2 的回報（開 app 之後只有第一個新建的辨識器收得到字），以 iPhone 的使用者代理開頁面：
  // - hubVoiceDictationIosRepeat：一次聽一句（continuous=false）、一句說完或沒說話都以同一個辨識器接著聽、字接在後面；
  //   停下、再開、打字停下都用同一個辨識器、只用 stop() 不用 abort()，第二次、第三次照樣打得出字；語音通知出聲之後才換新的
  // - hubVoiceDictationIosRecover：共用的辨識器收不到聲音（mute）時約 9 秒換一個新的接著聽，打得出字、不改成錄音
  const id = LF.CONV.idle;
  const app = await openWithLayout('voice-dictation-ios-repeat', layoutOf([[[id], id]]), { open: { vp: { w: 390, h: 844 }, hasTouch: true, userAgent: IPHONE_UA } });
  await waitLoaded(app, [id]);
  const { hub, page } = app;
  await page.click('#btnSidebar'); // 窄螢幕的側欄是蓋在工作區上的抽屜，先收起來
  await sleep(200);
  const mic = convSel(id) + ' .cbtn.mic';
  const ta = taSel(id);
  const st = () => page.evaluate(([m, t]) => {
    const b = document.querySelector(m);
    return { listening: b.classList.contains('listening'), title: b.title, value: document.querySelector(t).value, dropped: window.__sr.dropped,
      recorders: window.__mic.recorders.length,
      rs: window.__sr.instances.map((r) => ({ continuous: r.continuous, started: r.started, stopped: r.stopped, aborted: r.aborted, live: r.live, mute: r.mute, began: r.began })) };
  }, [mic, ta]);
  const listeningWith = (pred, ms = 3000) => until(async () => { const x = await st(); return x.listening && pred(x) ? x : null; }, ms);
  const idle = (ms = 3000) => until(async () => { const x = await st(); return !x.listening && x.rs.every((r) => !r.live) ? x : null; }, ms);
  const emit = (list) => page.evaluate((l) => window.__sr.emit(l), list);
  const toasts = () => page.$$eval('.toast', (n) => n.map((t) => t.textContent));
  await page.evaluate(() => { window.__sr.freshDeaf = true; window.__sr.stopDelay = 300; });

  // 第一次：兩句，中間一句沒說話
  await page.tap(mic);
  const one = await listeningWith((x) => x.rs.length === 1 && x.rs[0].began);
  await emit([['今天天氣很好', false]]);
  await emit([['今天天氣很好', true]]);
  await page.evaluate(() => window.__sr.end()); // 一句說完：iOS 一次一句，自己結束
  const restarted = await listeningWith((x) => x.rs[0].started === 2 && x.rs[0].live, 2000);
  await emit([['我們去公園', true]]);
  const twoUtterances = (await st()).value;
  await page.evaluate(() => window.__sr.fail('no-speech')); // 這一句沒說話
  const afterSilence = await listeningWith((x) => x.rs[0].started === 3 && x.rs[0].live, 2000);
  await page.tap(mic); // 停：最後一段 300 毫秒內到
  await emit([['散步', true]]);
  const first = await idle();
  // 第二次：同一個辨識器（新建的會收不到聲音）
  await page.tap(mic);
  const again = await listeningWith((x) => x.rs[0].started === 4 && x.rs[0].live);
  await emit([['再說一次', true]]);
  const secondValue = (await st()).value;
  await page.tap(mic);
  const second = await idle();
  // 第三次：自己打字就停（stop，不 abort）
  await page.tap(mic);
  await listeningWith((x) => x.rs[0].started === 5 && x.rs[0].live);
  await emit([['第三次', true]]);
  await page.tap(ta);
  await page.keyboard.type('X');
  const typed = await idle();
  const toastsRepeat = await toasts();
  // 語音通知出聲之後：換一個新的辨識器（出聲之前建好的之後常收不到聲音）
  await page.evaluate(() => { window.__sr.freshDeaf = false; });
  hub.push({ type: 'speak', conversationId: id, id: 301, text: '「整理報表」已完成' });
  await until(() => page.evaluate(() => window.__speech.spoken.length > 0 && !window.speechSynthesis.speaking), 3000);
  await page.tap(mic);
  const afterSpeech = await listeningWith((x) => x.rs.length === 2 && x.rs[1].began);
  await page.tap(mic);
  await idle();
  check('hubVoiceDictationIosRepeat', !!one && one.rs[0].continuous === false && !!restarted && twoUtterances === '今天天氣很好我們去公園' && !!afterSilence
    && !!first && first.value === '今天天氣很好我們去公園散步' && !!again && again.rs.length === 1
    && secondValue === '今天天氣很好我們去公園散步再說一次' && !!second && second.rs[0].stopped === 2
    && !!typed && typed.value.includes('X') && typed.value.replace('X', '') === '今天天氣很好我們去公園散步再說一次第三次' && typed.rs.length === 1 && typed.rs[0].aborted === 0 && typed.dropped === 0
    && !!afterSpeech && afterSpeech.rs[1].continuous === false
    && !toastsRepeat.some((t) => /沒有聽到|收不到|中斷|沒有回應|錄音/.test(t)) && hub.cmds('voiceMessage').length === 0,
  { one: one && one.rs, restarted: !!restarted, twoUtterances, afterSilence: !!afterSilence, first: first && first.value, again: again && again.rs, secondValue,
    second: second && second.rs, typed: typed && { value: typed.value, rs: typed.rs, dropped: typed.dropped }, afterSpeech: afterSpeech && afterSpeech.rs, toasts: toastsRepeat });

  // 共用的辨識器收不到聲音：約 9 秒後換一個新的接著聽
  await page.evaluate((t) => { const el = document.querySelector(t); el.value = ''; el.dispatchEvent(new Event('input', { bubbles: true })); }, ta);
  await page.evaluate(() => { window.__sr.mute = true; });
  const tapAt = Date.now();
  await page.tap(mic);
  const deaf = await listeningWith((x) => x.rs.length === 2 && x.rs[1].began && x.rs[1].mute);
  await page.evaluate(() => { window.__sr.mute = false; }); // 新建的收得到
  const swapped = await listeningWith((x) => x.rs.length === 3 && x.rs[2].began, 14000);
  const swappedMs = Date.now() - tapAt;
  await emit([['換了一個', true]]);
  const recovered = await st();
  await page.tap(mic);
  const done = await idle();
  const toastsRecover = await toasts();
  const xss = await HP.xssState(app);
  check('hubVoiceDictationIosRecover', !!deaf && !!swapped && swappedMs >= 8000 && swapped.rs[1].aborted === 1 && swapped.listening
    && recovered.value === '換了一個' && !!done && /^語音輸入：/.test(done.title) && done.recorders === 0
    && !toastsRecover.some((t) => /收不到|改成錄音/.test(t)) && xss.xss === 0,
  { deaf: !!deaf, swapped: swapped && swapped.rs, swappedMs, recovered: recovered.value, done: done && { title: done.title, recorders: done.recorders }, toasts: toastsRecover, xss: xss.xss });
  await HP.closePage(app);
}

async function runVoiceDictationDiag() {
  // 語音輸入的診斷記錄（2026-10-28：iPhone 關掉再開打不出字，要實機的事件順序）。以 iPhone 的使用者代理：
  // - hubVoiceDictationDiag：聽寫時預覽列的「記錄」打開對話框，列出環境、按下、新建與沿用的辨識器、事件、停止；長按麥克風鈕也打開
  //   （長按之後的那一下不開始聽寫）；「放進輸入框」把記錄接在輸入框後面
  // - hubVoiceDictationDiagMode：選「先開麥克風（開著）」之後，按麥克風先 getUserMedia、聽寫照樣打字、結束時關掉那個麥克風；選回預設不再要麥克風
  const id = LF.CONV.idle;
  const app = await openWithLayout('voice-dictation-diag', layoutOf([[[id], id]]), { open: { vp: { w: 390, h: 844 }, hasTouch: true, userAgent: IPHONE_UA } });
  await waitLoaded(app, [id]);
  const { page } = app;
  await page.click('#btnSidebar'); // 窄螢幕的側欄是蓋在工作區上的抽屜，先收起來
  await sleep(200);
  const mic = convSel(id) + ' .cbtn.mic';
  const ta = taSel(id);
  const st = () => page.evaluate(([m, t]) => {
    const b = document.querySelector(m);
    const dlg = document.querySelector('.dlg .voice-diag');
    return { listening: b.classList.contains('listening'), value: document.querySelector(t).value, dialog: dlg ? dlg.textContent : null,
      modes: [...document.querySelectorAll('.voice-diag-modes .btn')].map((x) => x.dataset.mode + ':' + x.getAttribute('aria-pressed')),
      micRequests: window.__mic.requests, tracksStopped: window.__mic.tracksStopped, recognizers: window.__sr.instances.length,
      rs: window.__sr.instances.map((r) => ({ started: r.started, live: r.live, began: r.began })) };
  }, [mic, ta]);
  const idle = (ms = 3000) => until(async () => { const x = await st(); return !x.listening && x.rs.every((r) => !r.live) ? x : null; }, ms);
  const closeDlg = async () => { await page.tap('.dlg .dlg-x'); await until(async () => ((await st()).dialog === null ? true : null), 2000); };

  await page.tap(mic);
  await until(async () => { const x = await st(); return x.listening && x.rs[0] && x.rs[0].began ? x : null; }, 3000);
  await page.evaluate(() => window.__sr.emit([['今天天氣很好', true]]));
  await page.tap(convSel(id) + ' .dict-log');
  const opened = await until(async () => { const x = await st(); return x.dialog ? x : null; }, 2000);
  await closeDlg();
  const stillListening = (await st()).listening;
  await page.tap(mic);
  await idle();
  // 長按麥克風鈕（700ms）：打開記錄，放開後那一下不開始聽寫
  await page.evaluate((m) => {
    const b = document.querySelector(m);
    b.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, pointerType: 'touch' }));
  }, mic);
  await sleep(900);
  await page.evaluate((m) => {
    const b = document.querySelector(m);
    b.dispatchEvent(new PointerEvent('pointerup', { bubbles: true, pointerType: 'touch' }));
    b.click();
  }, mic);
  const pressed = await until(async () => { const x = await st(); return x.dialog ? x : null; }, 2000);
  await shot(app, 'hub-voice-diag-390');
  await page.tap('.voice-diag-modes .btn[data-mode="gumHold"]');
  const picked = await st();
  await closeDlg();

  // 先開麥克風（開著）：假麥克風給 0.1 振幅的正弦波（約 -23 dBFS），記錄裡每秒一筆音量
  await page.evaluate(() => { window.__mic.tone = 0.1; });
  const req0 = (await st()).micRequests;
  await page.tap(mic);
  const held = await until(async () => { const x = await st(); return x.listening && x.micRequests === req0 + 1 && x.rs[0].live ? x : null; }, 3000);
  await sleep(1200);
  await page.evaluate(() => window.__sr.emit([['先開麥克風', true]]));
  await page.tap(mic);
  const heldDone = await idle();
  // 選回預設
  await page.tap(mic);
  await until(async () => { const x = await st(); return x.listening && x.rs[0].live ? x : null; }, 3000);
  await page.tap(convSel(id) + ' .dict-log');
  await until(async () => ((await st()).dialog ? true : null), 2000);
  await page.tap('.voice-diag-modes .btn[data-mode="shared"]');
  await page.tap('.dlg .btn.primary'); // 放進輸入框（同時停下聽寫）
  const put = await idle();
  const req1 = put.micRequests;
  await page.evaluate((t) => { const el = document.querySelector(t); el.value = ''; el.dispatchEvent(new Event('input', { bubbles: true })); }, ta);
  await page.tap(mic);
  await until(async () => { const x = await st(); return x.listening && x.rs[0].live ? x : null; }, 3000);
  await page.tap(mic);
  const back = await idle();
  // 先念一聲再新建：念「好」、念完新建辨識器 #2、打得出字
  await page.tap(mic);
  await until(async () => { const x = await st(); return x.listening && x.rs[0].live ? x : null; }, 3000);
  await page.tap(convSel(id) + ' .dict-log');
  await until(async () => ((await st()).dialog ? true : null), 2000);
  await page.tap('.voice-diag-modes .btn[data-mode="ttsFresh"]');
  await closeDlg();
  await page.tap(mic);
  await idle();
  const spoken0 = (await HP.speech(app)).spoken.length;
  await page.tap(mic);
  const tts = await until(async () => { const x = await st(); return x.listening && x.rs.length === 2 && x.rs[1].began ? x : null; }, 4000);
  const spokenTts = (await HP.speech(app)).spoken.slice(spoken0).map((x) => x.text);
  await page.evaluate(() => window.__sr.emit([['念完再聽', true]]));
  const ttsValue = (await st()).value;
  await page.tap(mic);
  await idle();
  const xss = await HP.xssState(app);
  const log = opened ? opened.dialog : '';
  check('hubVoiceDictationDiag', !!opened && /環境：Mozilla\/5\.0 \(iPhone/.test(log) && /一句一句聽=true/.test(log) && /按下：開始聽寫/.test(log)
    && /做法=gumHold/.test(log) && /getUserMedia 拿到麥克風/.test(log)
    && /新建辨識器 #1/.test(log) && /#1 start\(\) 呼叫完成/.test(log) && /#1 start \d+ms/.test(log) && /#1 result 1 段（確定）「今天天氣很好」/.test(log)
    && stillListening && !!pressed && /停止聽寫（按鈕）#1/.test(pressed.dialog) && /#1 end/.test(pressed.dialog) && !pressed.listening
    && pressed.rs[0].started === 1 && /^今天天氣很好先開麥克風\n\n語音輸入診斷記錄：\n/.test(put.value) && /改用做法 shared/.test(put.value)
    && put.recognizers === 1 && xss.xss === 0,
  { log: log && log.split('\n').slice(0, 14), pressed: pressed && { listening: pressed.listening, started: pressed.rs[0].started, tail: pressed.dialog.split('\n').slice(-4) },
    put: put && put.value.slice(0, 120), recognizers: put && put.recognizers, xss: xss.xss });
  check('hubVoiceDictationDiagMode', JSON.stringify(picked.modes) === JSON.stringify(['gumHold:true', 'shared:false', 'fresh:false', 'ttsFresh:false', 'sessionFlip:false', 'gumPrime:false'])
    && !!held && !!heldDone && heldDone.tracksStopped > held.tracksStopped && heldDone.value === '今天天氣很好先開麥克風'
    && /getUserMedia 拿到麥克風/.test(put.value) && /關掉先開的麥克風/.test(put.value) && /先開的麥克風音量 -?\d+ dBFS/.test(put.value)
    && !!back && back.micRequests === req1 && back.rs[0].started === 4
    && !!tts && JSON.stringify(spokenTts) === JSON.stringify(['好']) && ttsValue.endsWith('念完再聽'),
  { modes: picked.modes, tts: tts && tts.rs, spokenTts, ttsValue, held: held && { micRequests: held.micRequests, tracksStopped: held.tracksStopped }, heldDone: heldDone && { value: heldDone.value, tracksStopped: heldDone.tracksStopped },
    back: back && { micRequests: back.micRequests, req1, started: back.rs[0].started } });
  await HP.closePage(app);
}

async function runResync() {
  const app = await openWithLayout('resync', layoutOf([[[LF.CONV.idle], LF.CONV.idle], [[LF.CONV.codex], LF.CONV.codex]]));
  const { hub } = app;
  await waitLoaded(app, [LF.CONV.idle, LF.CONV.codex]);
  await markConv(app, [LF.CONV.idle]);
  await typeInto(app, LF.CONV.idle, '還沒送出');
  const n0 = hub.cmds('open').length;
  hub.push({ type: 'resync', conversationId: LF.CONV.idle });
  const again = await until(() => (openIds(hub, n0).length ? openIds(hub, n0) : null), 3000);
  await sleep(600);
  const s = await convState(app, LF.CONV.idle);
  check('hubResyncReopens', !!again && JSON.stringify(openIds(hub, n0)) === JSON.stringify([LF.CONV.idle]) && !!s && s.loaded && s.items === LF.liveItems(NOW).length
    && s.text === '還沒送出' && hub.cmds('newConversation').length === 0,
  { opens: openIds(hub, n0), conv: s });
  // null（伺服器另送 init）與沒開著的對話：什麼都不做
  const n1 = hub.cmds('open').length;
  hub.push({ type: 'resync', conversationId: null });
  hub.push({ type: 'resync', conversationId: LF.CONV.view });
  hub.push({ type: 'resync' });
  await sleep(900);
  const ws = await wsState(app);
  check('hubResyncNullNoop', openIds(hub, n1).length === 0 && ws.panes.length === 2 && ws.panes.flatMap((p) => p.tabs).length === 2,
    { opens: openIds(hub, n1), panes: ws.panes });
  await HP.closePage(app);
}

/** 開 CONV.idle 的頁面，假 hub 對 copyItem 回 clipboard（o.reply 是 false 時不回；o.delayMs 毫秒後才推） */
async function openCopy(name, o = {}) {
  const app = await openWithLayout(name, layoutOf([[[LF.CONV.idle], LF.CONV.idle]]), { open: o.open || {} });
  await waitLoaded(app, [LF.CONV.idle]);
  app.hub.onCmd('copyItem', (cmd) => (o.reply === false ? null
    : { push: [{ type: 'clipboard', conversationId: cmd.conversationId, itemId: cmd.itemId, text: CLIP_TEXT }], delayMs: o.delayMs || 0 }));
  return app;
}

/** 按 Assistant 項目的「複製」（複製鈕在滑鼠移到項目上才出現） */
async function clickCopy(app, itemId = 'i2') {
  await app.page.hover(itemSel(LF.CONV.idle, itemId));
  await app.page.click(itemSel(LF.CONV.idle, itemId) + ' .it-acts .code-copy');
}

async function runClipboard() {
  // 按複製 → 複製中… → 伺服器回 clipboard → 寫入剪貼簿之後才是「已複製」
  const a = await openCopy('clipboard-write', { delayMs: 900 });
  await clickCopy(a);
  const busy = await until(async () => { const b = await copyBtn(a, LF.CONV.idle, 'i2'); return b && b.busy ? b : null; }, 2000);
  const writesBusy = (await HP.clipboardWrites(a)).length;
  // 複製中再按一次：不重送
  await clickCopy(a);
  const written = await until(async () => { const w = await HP.clipboardWrites(a); return w.length ? w : null; }, 4000);
  const done = await until(async () => { const b = await copyBtn(a, LF.CONV.idle, 'i2'); return b && b.done ? b : null; }, 2000);
  const sent = a.hub.cmds('copyItem').map((c) => c.cmd);
  const doneAt = Date.now();
  const idle = await until(async () => { const b = await copyBtn(a, LF.CONV.idle, 'i2'); return b && !b.done && !b.busy ? b : null; }, 3000);
  check('hubClipboardWrite', !!busy && busy.text === '複製中…' && writesBusy === 0 && !!written && written.length === 1 && written[0].ok && written[0].text === CLIP_TEXT
    && !!done && done.text === '已複製' && sent.length === 1 && sent[0].conversationId === LF.CONV.idle && sent[0].itemId === 'i2' && (await anyDialog(a)) === 0
    && !!idle && idle.text === '複製' && Date.now() - doneAt > 700,
  { busy, writesWhileBusy: writesBusy, written, done, sent, idle });
  await HP.closePage(a);

  // 剪貼簿被拒：退路框（value 照字面、唯讀、全選），框裡沒有 HTML；按鈕回到「複製」
  const b = await openCopy('clipboard-denied', { open: { initOpts: { clipboard: 'deny' } } });
  await clickCopy(b);
  const fb = await until(() => fallbackState(b), 4000);
  await sleep(300);
  const fb2 = await fallbackState(b);
  const btnB = await copyBtn(b, LF.CONV.idle, 'i2');
  const xssB = await HP.xssState(b);
  const injected = await b.page.evaluate(() => document.querySelectorAll('img[src="x"]').length);
  await shot(b, 'hub-copy-fallback-1440');
  const writesB = await HP.clipboardWrites(b);
  check('hubClipboardDeniedFallback', !!fb && fb2.value === CLIP_TEXT && fb2.readOnly && fb2.selected && fb2.imgs === 0 && fb2.taChildren === 0 && fb2.title === '複製文字'
    && JSON.stringify(fb2.buttons) === JSON.stringify(['關閉']) && writesB.length === 1 && !writesB[0].ok && !!btnB && btnB.text === '複製' && !btnB.done
    && xssB.xss === 0 && injected === 0,
  { fallback: fb2 && Object.assign({}, fb2, { value: fb2.value === CLIP_TEXT ? '(相同)' : fb2.value }), button: btnB, writes: writesB.length, xss: xssB.xss, injected });
  await b.page.click('.dlg-copy-fallback .dlg-foot button');
  await until(async () => ((await anyDialog(b)) === 0 ? true : null), 2000);
  await HP.closePage(b);

  // 沒有按複製就推來的 clipboard：不寫剪貼簿、不開框
  const c = await openCopy('clipboard-unsolicited');
  c.hub.push({ type: 'clipboard', conversationId: LF.CONV.idle, itemId: 'i2', text: CLIP_TEXT });
  c.hub.push({ type: 'clipboard', conversationId: LF.CONV.idle, itemId: 'i9', text: 'x' });
  await sleep(800);
  // 按了 i2 的複製，伺服器回的是別的項目：也略過
  c.hub.onCmd('copyItem', (cmd) => ({ push: [{ type: 'clipboard', conversationId: cmd.conversationId, itemId: 'i1', text: '別的項目' }] }));
  await clickCopy(c);
  await sleep(800);
  check('hubClipboardUnsolicitedIgnored', (await HP.clipboardWrites(c)).length === 0 && (await anyDialog(c)) === 0 && (await HP.msgsOf(c, 'clipboard')).length === 3,
    { writes: (await HP.clipboardWrites(c)).length, dialogs: await anyDialog(c), received: (await HP.msgsOf(c, 'clipboard')).length });
  await HP.closePage(c);

  // 按複製 31 秒後才回：略過，按鈕回到「複製」
  const d = await openCopy('clipboard-expired', { reply: false });
  await clickCopy(d);
  const busyD = await until(async () => { const x = await copyBtn(d, LF.CONV.idle, 'i2'); return x && x.busy ? x : null; }, 2000);
  await HP.advanceClock(d, 31000);
  d.hub.push({ type: 'clipboard', conversationId: LF.CONV.idle, itemId: 'i2', text: CLIP_TEXT });
  await sleep(800);
  const btnD = await copyBtn(d, LF.CONV.idle, 'i2');
  check('hubClipboardExpiredIgnored', !!busyD && (await HP.clipboardWrites(d)).length === 0 && (await anyDialog(d)) === 0 && !!btnD && btnD.text === '複製' && !btnD.busy,
    { busy: !!busyD, writes: (await HP.clipboardWrites(d)).length, dialogs: await anyDialog(d), button: btnD });
  await HP.closePage(d);
}

async function runCodeCopyFallback() {
  // 程式碼區塊的複製：剪貼簿被拒 → copyText 不送伺服器，傳輸層交給網頁 copyFallback → 退路框；按鈕不標「已複製」
  const code = 'const html = "' + LF.XSS + '";\nlogin(html);';
  const esc = code.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
  const items = LF.liveItems(NOW).concat([{ id: 'i9', kind: 'Assistant', status: 'Done', time: NOW, seq: 99, html: '<p>改好了：</p><pre><code class="language-js">' + esc + '</code></pre>' }]);
  const app = await openWithLayout('code-copy-fallback', layoutOf([[[LF.CONV.idle], LF.CONV.idle]]), { open: { initOpts: { clipboard: 'deny' } } });
  const { page, hub } = app;
  await waitLoaded(app, [LF.CONV.idle]);
  hub.push(LF.conversationMessage(NOW, LF.CONV.idle, TITLE_OF[LF.CONV.idle], { items }));
  const sel = itemSel(LF.CONV.idle, 'i9') + ' .code .code-copy';
  await until(() => page.evaluate((s) => !!document.querySelector(s), sel), 3000);
  await page.click(sel);
  const fb = await until(() => fallbackState(app), 3000);
  await sleep(300);
  const label = await page.evaluate((s) => { const b = document.querySelector(s); return { text: b.lastChild.textContent, done: b.classList.contains('done') }; }, sel);
  const fbMsgs = await HP.msgsOf(app, 'copyFallback');
  check('hubCodeCopyFallback', !!fb && fb.value === code && fb.readOnly && fb.imgs === 0 && label.text === '複製' && !label.done && hub.cmds('copyText').length === 0
    && fbMsgs.length === 1 && fbMsgs[0].text === code && (await HP.xssState(app)).xss === 0,
  { fallback: fb && { readOnly: fb.readOnly, same: fb.value === code, imgs: fb.imgs }, label, copyTextSent: hub.cmds('copyText').length, copyFallback: fbMsgs.length });
  await HP.closePage(app);
}

/**
 * 換版的頁面：ready 的 init 帶 app.cur.build（undefined＝沒有這個欄位），每次重新載入之後照目前的值；o.layout 是存下的版面、
 * o.open 併進 openHub 的選項、o.serve 是 serveOpen 的選項、o.onReady(cur) 在每則 ready（含重新載入之後的）回 init 之前呼叫。
 * app.reinit(build) 推一則新的 init（像伺服器換版後重連）
 */
async function openBuild(name, build, o = {}) {
  const cur = { build };
  const initOf = (mode) => LF.liveBrowserInit(NOW, { mode, layout: o.layout || null, build: cur.build });
  const init = (ready) => {
    if (o.onReady) o.onReady(cur);
    return initOf(ready && ready.mode);
  };
  const app = await H.openHub(name, Object.assign({ init, hub: (hub) => serveOpen(hub, o.serve || {}) }, o.open || {}));
  await HP.waitLive(app);
  app.cur = cur;
  app.reinit = (b) => {
    cur.build = b;
    return app.hub.push(initOf('main'));
  };
  return app;
}
const reloads = (app) => app.navigations.length - 1;

async function runBuild() {
  // 沒有未送出的字：換版就重新載入一次（重新載入之後的 init 已是新版，不再載入）
  const a = await openBuild('build-auto', 'aaaa111');
  a.reinit('bbbb222');
  const navA = await until(() => (reloads(a) >= 1 ? true : null), 5000);
  await HP.waitLive(a);
  await sleep(1500);
  const bannerA = await noticeOf(a, 'build');
  check('hubBuildChangedAutoReload', !!navA && reloads(a) === 1 && !bannerA, { reloads: reloads(a), banner: bannerA });
  await HP.closePage(a);

  // 剛打了字（還在用）：先顯示橫幅（字留著）；按「重新載入」馬上載入，字照樣還原
  const layout = layoutOf([[[LF.CONV.idle], LF.CONV.idle]]);
  const b = await openBuild('build-banner', 'aaaa111', { layout });
  await waitLoaded(b, [LF.CONV.idle]);
  await typeInto(b, LF.CONV.idle, '還沒送出的字');
  b.reinit('bbbb222');
  const bannerB = await until(() => noticeOf(b, 'build'), 3000);
  await sleep(800);
  const textB = ((await convState(b, LF.CONV.idle).catch(() => null)) || {}).text;
  await shot(b, 'hub-build-banner-1440');
  const reloadsB = reloads(b);
  if (bannerB && reloadsB === 0) await b.page.click('.live-notice[data-notice="build"] button[data-field="reloadApp"]');
  const navB = bannerB && reloadsB === 0 ? await until(() => (reloads(b) >= 1 ? true : null), 5000) : null;
  let textAfterB = null;
  if (navB) {
    await HP.waitLive(b);
    const loadedB = await waitLoaded(b, [LF.CONV.idle]);
    textAfterB = loadedB ? loadedB[LF.CONV.idle].text : null;
  }
  check('hubBuildChangedBanner', !!bannerB && bannerB.text === BUILD_TEXT && JSON.stringify(bannerB.buttons) === JSON.stringify(['重新載入']) && reloadsB === 0
    && textB === '還沒送出的字' && !!navB && textAfterB === '還沒送出的字',
  { banner: bannerB, reloadsBeforeClick: reloadsB, text: textB, reloadedOnClick: !!navB, textAfterReload: textAfterB });
  await HP.closePage(b);

  // 同一個 build：沒有橫幅、不載入
  const c = await openBuild('build-same', 'aaaa111');
  c.reinit('aaaa111');
  await sleep(1500);
  check('hubBuildSameNoBanner', reloads(c) === 0 && !(await noticeOf(c, 'build')) && (await HP.msgsOf(c, 'init')).length === 2,
    { reloads: reloads(c), inits: (await HP.msgsOf(c, 'init')).length });
  await HP.closePage(c);

  // 回滾到沒有 build 欄位的舊伺服器：也算換版
  const d = await openBuild('build-rollback', 'aaaa111');
  d.reinit(undefined);
  const navD = await until(() => (reloads(d) >= 1 ? true : null), 5000);
  await HP.waitLive(d);
  await sleep(1200);
  check('hubBuildMissingAfterRollback', !!navD && reloads(d) === 1 && !(await noticeOf(d, 'build')), { reloads: reloads(d) });
  await HP.closePage(d);

  // 60 秒內第二次換版：不再自動載入，顯示橫幅；sessionStorage 不能用時一律橫幅（不會無限重新載入）
  const e = await openBuild('build-once-per-minute', 'aaaa111');
  e.reinit('bbbb222');
  await until(() => (reloads(e) >= 1 ? true : null), 5000);
  await HP.waitLive(e);
  const stamp = await e.page.evaluate(() => { try { return sessionStorage.getItem('myriagent.app.reloadAt.v1'); } catch { return 'threw'; } });
  e.reinit('cccc333');
  const bannerE = await until(() => noticeOf(e, 'build'), 3000);
  await sleep(1200);
  const f = await openBuild('build-storage-throws', 'aaaa111', { open: { initOpts: { sessionStorageThrows: true } } });
  f.reinit('bbbb222');
  const bannerF = await until(() => noticeOf(f, 'build'), 3000);
  f.reinit('cccc333');
  await sleep(1500);
  check('hubBuildReloadOncePerMinute', reloads(e) === 1 && !!stamp && Number.isFinite(Number(stamp)) && !!bannerE && bannerE.text === BUILD_TEXT
    && reloads(f) === 0 && !!bannerF && ((await noticeState(f)) || []).filter((n) => n.kind === 'build').length === 1,
  { reloads: reloads(e), stamp, banner: !!bannerE, storageThrows: { reloads: reloads(f), banner: !!bannerF } });
  await HP.closePage(e);
  await HP.closePage(f);
}

/** 輸入框放字但不送 input 事件（不算使用者的操作：換版時不必等停手） */
const setTextQuiet = (app, id, text) => app.page.evaluate(([cid, t]) => {
  document.querySelector('.conv[data-conv="' + cid + '"] textarea.cinput').value = t;
}, [id, text]);
/** 重新載入前存下的草稿（sessionStorage；讀過就刪，沒有是 null） */
const reloadDraftsStored = (app) => app.page.evaluate(() => { try { return sessionStorage.getItem('myriagent.app.reloadDrafts.v1'); } catch { return 'threw'; } });

async function runBuildDeferred() {
  const layout = layoutOf([[[LF.CONV.idle], LF.CONV.idle]]);
  // 剛操作過：先顯示橫幅、不載入；停手 10 秒（RELOAD_IDLE_MS）後自動重新載入，輸入框的字還原、橫幅沒了、存下的字讀過就刪
  const a = await openBuild('build-deferred', 'aaaa111', { layout });
  await waitLoaded(a, [LF.CONV.idle]);
  await typeInto(a, LF.CONV.idle, '停手之後才載入');
  a.reinit('bbbb222');
  const bannerA = await until(() => noticeOf(a, 'build'), 3000);
  await sleep(2500);
  const waitedA = reloads(a);
  const navA = await until(() => (reloads(a) >= 1 ? true : null), 15000);
  if (navA) await HP.waitLive(a);
  const loadedA = navA ? await waitLoaded(a, [LF.CONV.idle]) : null;
  await sleep(300);
  const textA = loadedA ? loadedA[LF.CONV.idle].text : null;
  const storedA = await reloadDraftsStored(a);
  check('hubBuildDeferredReloadKeepsDraft', !!bannerA && waitedA === 0 && !!navA && reloads(a) === 1 && textA === '停手之後才載入'
    && !(await noticeOf(a, 'build')) && storedA === null,
  { banner: !!bannerA, reloadsWhileActive: waitedA, reloaded: !!navA, reloads: reloads(a), text: textA, stored: storedA });
  await HP.closePage(a);

  // 焦點在有字的輸入框（正在打字）：一直等；分頁藏起來（切到別的分頁）馬上重新載入，字還原
  const b = await openBuild('build-hidden', 'aaaa111', { layout });
  await waitLoaded(b, [LF.CONV.idle]);
  await b.page.click(convSel(LF.CONV.idle) + ' textarea.cinput');
  await b.page.keyboard.type('切走就載入');
  b.reinit('bbbb222');
  const bannerB = await until(() => noticeOf(b, 'build'), 3000);
  await sleep(2500);
  const waitedB = reloads(b);
  await b.page.evaluate(() => {
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'hidden' });
    document.dispatchEvent(new Event('visibilitychange'));
  });
  const navB = await until(() => (reloads(b) >= 1 ? true : null), 3000);
  if (navB) await HP.waitLive(b);
  const loadedB = navB ? await waitLoaded(b, [LF.CONV.idle]) : null;
  const textB = loadedB ? loadedB[LF.CONV.idle].text : null;
  check('hubBuildHiddenReloadsNow', !!bannerB && waitedB === 0 && !!navB && reloads(b) === 1 && textB === '切走就載入',
    { banner: !!bannerB, reloadsWhileTyping: waitedB, reloaded: !!navB, text: textB });
  await HP.closePage(b);

  // 60 秒內第二次換版：先顯示橫幅，滿 60 秒（上次載入的時間改成 56 秒前）就自動重新載入
  const c = await openBuild('build-after-minute', 'aaaa111');
  c.reinit('bbbb222');
  await until(() => (reloads(c) >= 1 ? true : null), 5000);
  await HP.waitLive(c);
  await c.page.evaluate(() => sessionStorage.setItem('myriagent.app.reloadAt.v1', String(Date.now() - 56000)));
  c.reinit('cccc333');
  const bannerC = await until(() => noticeOf(c, 'build'), 3000);
  const waitedC = reloads(c);
  const navC = await until(() => (reloads(c) >= 2 ? true : null), 9000);
  if (navC) await HP.waitLive(c);
  await sleep(600);
  check('hubBuildDeferredAfterMinute', !!bannerC && waitedC === 1 && !!navC && reloads(c) === 2 && !(await noticeOf(c, 'build')),
    { banner: !!bannerC, reloadsBeforeMinute: waitedC, reloads: reloads(c) });
  await HP.closePage(c);

  // 伺服器草稿裡沒送出的字：新版的伺服器沒有這個草稿（重新載入之後的 open 回 conversationClosed）
  // → 以重新載入前記下的帳戶、電腦在同一個位置開新草稿，字還原（舊頁面的 open 照常回草稿，只有新頁面的被關）
  const NEW = LF.draftId(3);
  const closed = new Set();
  const dl = layoutOf([[[LF.CONV.idle, LF.DRAFT], LF.DRAFT]]);
  const d = await openBuild('build-reload-draft', 'aaaa111', { layout: dl, serve: { closed }, onReady: (cur) => { if (cur.build === 'bbbb222') closed.add(LF.DRAFT); } });
  d.hub.onCmd('newConversation', (cmd) => ({ push: [LF.draftMessage(NOW, NEW, { paneId: cmd.paneId, deviceId: cmd.deviceId || null })] }));
  await waitLoaded(d, [LF.CONV.idle, LF.DRAFT]);
  await setTextQuiet(d, LF.DRAFT, '換版前在新對話打的字');
  const nc0 = d.hub.cmds('newConversation').length;
  d.reinit('bbbb222');
  const navD = await until(() => (reloads(d) >= 1 ? true : null), 5000);
  if (navD) await HP.waitLive(d);
  const gotD = navD ? await until(async () => { const s = await convState(d, NEW); return s && s.loaded ? s : null; }, 6000) : null;
  await sleep(300);
  const wsD = await wsState(d);
  const ncD = d.hub.cmds('newConversation').slice(nc0).map((x) => x.cmd);
  check('hubBuildReloadRecreatesDraft', !!navD && reloads(d) === 1 && !!gotD && gotD.text === '換版前在新對話打的字'
    && ncD.length === 1 && ncD[0].accountId === 'la_c1' && ncD[0].deviceId === LF.DEVICE.control && ncD[0].paneId === 'p1'
    && JSON.stringify(wsD.panes[0].tabs) === JSON.stringify([LF.CONV.idle, NEW]),
  { reloaded: !!navD, reloads: reloads(d), draft: gotD, newConversation: ncD, panes: wsD.panes });
  await HP.closePage(d);
}

// 每一組：會產生的檢查（--only 用）、執行
// ───── 步 10：桌面的副本唯讀、對話的同步政策、設定頁的同步區、遠端操作紀錄與回收區（D22、D25～D31、D44）─────

const REPLICA_TEXT = '這是 筆電 ' + LF.XSS + ' 的對話（唯讀），到網頁版或 筆電 ' + LF.XSS + ' 上回應';
const webUrlOf = (id) => 'https://hub.probe.local/app/?mode=popout&conversation=' + encodeURIComponent(id);
const LOCAL_ONLY_TEXT = '會從伺服器刪除這個對話，網頁與其他電腦都看不到，之後不能再同步';
const postedOf = (app, cmd) => app.host.posted.filter((m) => m.cmd === cmd);
const desktopLayout = (tabs, o = {}) => Object.assign({ panes: [{ id: 'p1', tabs, active: tabs[0] || null }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' }, o);
const okScroll = (x) => x.docScroll <= x.innerWidth && x.bodyScroll <= x.innerWidth && x.wide.length === 0;
const convSel = (id) => '.conv[data-conv="' + id + '"]';

/**
 * 桌面的假宿主：open 回副本（清單上有 deviceId 的；o.replica(id) 回 replicaMessage 的選項）或本機對話（o.sync(id) 回 conversationSync，null＝不帶）；
 * syncSetConversationPolicy 照宿主改標頭再送（改成 localOnly＝伺服器刪除、只剩 localOnly）；syncSetPolicy 改 local 再送 sync；
 * 紀錄、匯出、回收區照 o.audit(m)、o.exportResult(n)、o.trash 回；還原送 toast 再重送 trash
 */
function desktopHost(o = {}) {
  const list = o.conversations || LF.desktopList(NOW);
  const st = { sync: o.status || null, policies: new Map(), trash: (o.trash || []).slice(), exports: 0 };
  const syncOf = (id) => (st.policies.has(id) ? st.policies.get(id) : o.sync ? o.sync(id) : undefined);
  const onPost = (m, host) => {
    const c = typeof m.conversationId === 'string' ? list.find((x) => x.id === m.conversationId) : null;
    switch (m.cmd) {
      case 'open':
        if (!c) return;
        if (c.deviceId) host.send(LF.replicaMessage(NOW, c.id, c.title, c.deviceId, Object.assign({ running: c.running, waiting: c.waiting, items: LF.desktopItems(NOW, 'r') }, (o.replica && o.replica(c.id)) || {})));
        else host.send(LF.localMessage(NOW, c.id, c.title, Object.assign({ conversationSync: syncOf(c.id) }, o.items && o.items(c.id) ? { items: o.items(c.id) } : {})));
        break;
      case 'syncSetConversationPolicy': {
        if (!c || c.deviceId) return;
        const next = m.policy === 'localOnly' ? LF.conversationSync('localOnly', { serverDeleted: true }) : LF.conversationSync(m.policy);
        st.policies.set(c.id, next);
        host.send({ type: 'header', conversationId: c.id, header: LF.localMessage(NOW, c.id, c.title, { conversationSync: next }).header });
        break;
      }
      case 'syncSetPolicy': {
        if (!st.sync) return;
        const local = Object.assign({}, st.sync.local);
        for (const k of ['remoteControlLevel', 'allowRemoteFullAccess', 'notifyRemoteActions', 'allowRemoteExec']) if (m[k] !== undefined) local[k] = m[k];
        st.sync = Object.assign({}, st.sync, { local });
        host.send({ type: 'sync', sync: st.sync });
        break;
      }
      case 'syncLoadRemoteAudit': {
        const r = o.audit ? o.audit(m) : null;
        if (r) host.send(Object.assign({ type: 'remoteAudit' }, r));
        break;
      }
      case 'syncExportRemoteAudit': {
        st.exports++;
        const r = o.exportResult ? o.exportResult(st.exports) : null;
        if (r) host.send(Object.assign({ type: 'remoteAuditExport' }, r));
        break;
      }
      case 'syncListTrash': host.send({ type: 'trash', entries: st.trash }); break;
      case 'syncRestoreTrash':
        // o.restoreDelayMs：宿主晚一點才回（畫面先顯示「還原中…」）
        setTimeout(() => {
          st.trash = st.trash.filter((e) => e.conversationId !== m.conversationId);
          host.send({ type: 'toast', text: '已還原到這台', level: 'success' }).catch(() => {});
          host.send({ type: 'trash', entries: st.trash }).catch(() => {});
        }, o.restoreDelayMs || 0);
        break;
      default: break;
    }
  };
  return { st, onPost };
}

/** 開桌面頁：o.init 併進 liveDesktopInit 的選項（sync、conversations、layout）；o.host 是 desktopHost 的選項 */
async function openDesktopLive(name, o = {}) {
  const dh = desktopHost(Object.assign({ status: (o.init && o.init.sync) || LF.syncWith(NOW, { remoteSupported: true }) }, o.host || {}));
  const init = Object.assign(LF.liveDesktopInit(NOW, o.init || {}), o.initPatch || {});
  const app = await H.openDesktop(name, { init, onPost: dh.onPost, vp: o.vp, hasTouch: o.hasTouch, media: o.media });
  app.dh = dh;
  return app;
}

/** 標題列「更多」選單的內容（標題列與項目；項目帶打勾與停用） */
const menuState = (app) => app.page.evaluate(() => [...document.querySelectorAll('.menu > .menu-head, .menu > .menu-item')].map((n) => (n.classList.contains('menu-head')
  ? { head: n.textContent }
  : { label: n.querySelector('.mi-label').textContent, checked: n.getAttribute('aria-checked'), disabled: n.disabled })));
const openMore = async (app, id) => {
  await app.page.click(convSel(id) + ' header.ch button[aria-label="更多動作"]');
  await app.page.waitForSelector('.menu');
  return menuState(app);
};
const closeMenu = async (app) => { await app.page.keyboard.press('Escape'); await sleep(120); };
const clickMenu = (app, label) => app.page.locator('.menu .menu-item', { hasText: label }).first().click();

/** 副本的畫面：唯讀說明、輸入框與送出鈕在不在、閘門、晶片與按鈕停用、帳戶已移除的提示 */
const replicaState = (app, id) => app.page.evaluate((cid) => {
  const root = document.querySelector('.conv[data-conv="' + cid + '"]');
  if (!root) return null;
  const vis = (el) => !!el && el.isConnected && getComputedStyle(el).display !== 'none';
  const ro = root.querySelector('[data-field="replicaReadOnly"]');
  const web = root.querySelector('[data-field="openInWeb"]');
  const chip = root.querySelector('.ch-acct');
  const card = root.querySelector('.it[data-id="r4"]');
  return {
    readOnly: vis(ro), text: ro ? ro.querySelector('.creadonly-text').textContent : null, web: vis(web),
    textarea: !!root.querySelector('textarea.cinput'), sendBtn: !!root.querySelector('.cbtn.send'), gate: vis(root.querySelector('[data-field="sendGate"]')),
    folderDisabled: root.querySelector('.ch-folder').disabled, drawerDisabled: root.querySelector('header.ch button[aria-label="檔案瀏覽器"]').disabled,
    acctDisabled: chip.disabled, acctLabel: chip.querySelector('.chip-label').textContent, acctMissing: chip.classList.contains('missing'),
    warn: vis(root.querySelector('.ch-warn')), infoWarn: !!root.querySelector('.ci-warn'),
    device: vis(root.querySelector('.ch-dev')) ? root.querySelector('.ch-dev-text').textContent : null,
    cardGate: card ? (card.querySelector('.card-gate') || {}).textContent || null : null,
    serverDeleted: vis(root.querySelector('[data-field="serverDeleted"]')),
  };
}, id);

async function runDesktopReplica() {
  const id = LF.CONV.running;
  // 宿主就算誤送 accountMissing:true，副本也不顯示「帳戶已移除」（副本不是這台的帳戶）
  const app = await openDesktopLive('desktop-replica', {
    init: { layout: desktopLayout([id, 'c1']) },
    host: { replica: () => ({ header: { accountMissing: true } }), sync: () => LF.conversationSync('full') },
  });
  const { page } = app;
  await until(() => page.evaluate((s) => !!document.querySelector(s + ':not(.loading) [data-field="replicaReadOnly"]'), convSel(id)), 4000);
  await sleep(200);
  const r = await replicaState(app, id);
  // 改名：雙擊標題不出現改名框
  await page.dblclick(convSel(id) + ' .ch-title');
  await sleep(150);
  const renameBox = await page.$(convSel(id) + ' .ch-title input');
  await shot(app, 'desktop-replica-1440');
  const hs1440 = await hScroll(app);
  check('desktopReplicaReadOnly', !!r && r.readOnly && r.text === REPLICA_TEXT && !r.textarea && !r.sendBtn && !r.gate && r.folderDisabled && r.drawerDisabled && r.acctDisabled
    && !renameBox && !!r.cardGate && (await HP.xssState(app)).xss === 0,
  { state: r, renameBox: !!renameBox });
  check('desktopReplicaNoAccountMissing', !!r && !r.warn && !r.infoWarn && !r.acctMissing && r.acctLabel === 'Claude', r);
  // 「更多」選單：只有在新視窗開啟（沒有改名、瀏覽檔案、刪除、同步到伺服器）；側欄按 Delete 也不出確認框、不送 delete
  const menu = await openMore(app, id);
  await closeMenu(app);
  const labels = menu.map((x) => x.label || x.head);
  await page.evaluate((title) => { const row = [...document.querySelectorAll('.sb-list .crow')].find((n) => n.querySelector('.crow-title').textContent === title); if (row) row.focus(); },
    LF.desktopList(NOW).find((c) => c.id === id).title);
  await page.keyboard.press('Delete');
  await sleep(300);
  const dialog = await page.$('.dlg-confirm');
  check('desktopReplicaNoDelete', labels.includes('在新視窗開啟') && !labels.some((x) => /刪除|重新命名|瀏覽工作資料夾|同步到伺服器/.test(x)) && !dialog && postedOf(app, 'delete').length === 0,
    { labels, dialog: !!dialog, deletes: postedOf(app, 'delete').length });
  // 在網頁版開啟：openLink 伺服器的 /app/ 彈出視窗（id 編碼），不帶 conversationId（副本只收看的指令）
  await page.click(convSel(id) + ' [data-field="openInWeb"]');
  const link = await until(() => postedOf(app, 'openLink').at(-1) || null, 2000);
  check('desktopReplicaOpenInWeb', !!r && r.web && !!link && link.href === webUrlOf(id) && !('conversationId' in link), { link, web: r && r.web });
  // 開路徑帶 conversationId：檔案連結（開啟、在檔案總管顯示）、燈箱的「用預設程式開啟」；本機對話也帶自己的 id
  await page.click(convSel(id) + ' .it[data-id="r3"] .frow .flink');
  await page.click(convSel(id) + ' .it[data-id="r3"] .frow button:not(.flink)');
  await page.click(convSel(id) + ' .it[data-id="r2"] .mimg');
  await page.waitForSelector('.lb');
  await page.locator('.lb .lb-btn', { hasText: '用預設程式開啟' }).click();
  await page.keyboard.press('Escape');
  await sleep(150);
  await page.click('.tab[data-key="c1"]');
  await until(() => page.evaluate((s) => !!document.querySelector(s + ':not(.loading) .it[data-id="l3"]'), convSel('c1')), 4000);
  await page.click(convSel('c1') + ' .it[data-id="l3"] .frow .flink');
  await sleep(150);
  const paths = app.host.posted.filter((m) => m.cmd === 'openPath' || m.cmd === 'revealPath');
  const want = [
    { cmd: 'openPath', path: 'C:\\repo\\demo\\a.cs', conversationId: id },
    { cmd: 'revealPath', path: 'C:\\repo\\demo\\a.cs', conversationId: id },
    { cmd: 'openPath', path: 'C:\\repo\\demo\\shot.png', conversationId: id },
    { cmd: 'openPath', path: 'C:\\repo\\demo\\a.cs', conversationId: 'c1' },
  ];
  check('desktopReplicaOpenPathCarriesConversation', JSON.stringify(paths) === JSON.stringify(want), { paths });
  // 本機對話的輸入區照舊（有輸入框、沒有唯讀說明）
  const local = await replicaState(app, 'c1');
  // 桌面主視窗最窄 960（MainForm 的 LogicalMinimumSize）：1440 與 960 都不水平捲動
  await page.click('.tab[data-key="' + id + '"]');
  await page.setViewportSize({ width: 960, height: 700 });
  await sleep(250);
  await shot(app, 'desktop-replica-960');
  const hs960 = await hScroll(app);
  check('desktopReplicaNoHScroll', okScroll(hs1440) && okScroll(hs960) && !!local && local.textarea && !local.readOnly, { hs1440, hs960, local });
  await HP.closePage(app);
}

async function runDesktopPolicy() {
  // c1 有接收者（完整同步），c2 沒有 conversationSync（例如還沒發布）
  const app = await openDesktopLive('desktop-policy', {
    init: { layout: desktopLayout(['c1', 'c2']) },
    host: { sync: (cid) => (cid === 'c2' ? null : LF.conversationSync('full')) },
  });
  const { page } = app;
  await until(() => page.evaluate((s) => !!document.querySelector(s + ':not(.loading) .it'), convSel('c1')), 4000);
  const menu1 = await openMore(app, 'c1');
  await shot(app, 'desktop-policy-menu-1440');
  // 改成「只同步標題與狀態」：直接送；再按同一個（已是目前的）不送
  await clickMenu(app, '只同步標題與狀態');
  const sent1 = await until(() => postedOf(app, 'syncSetConversationPolicy')[0] || null, 2000);
  await sleep(250);
  const menu2 = await openMore(app, 'c1');
  await clickMenu(app, '只同步標題與狀態');
  await sleep(250);
  const sameNotSent = postedOf(app, 'syncSetConversationPolicy').length === 1;
  // 權杖暫停（policy paused）：標題列說明、沒有打勾的
  await app.host.send({ type: 'header', conversationId: 'c1', header: LF.localMessage(NOW, 'c1', '這台的對話', { conversationSync: LF.conversationSync('paused') }).header });
  await sleep(250);
  const menuPaused = await openMore(app, 'c1');
  await closeMenu(app);
  await page.click('.tab[data-key="c2"]');
  await until(() => page.evaluate((s) => !!document.querySelector(s + ':not(.loading) .it'), convSel('c2')), 4000);
  const menuNoSync = await openMore(app, 'c2');
  await closeMenu(app);
  const heads = (m) => m.filter((x) => x.head).map((x) => x.head);
  const choices = (m) => m.filter((x) => x.label && ['完整同步', '只同步標題與狀態', '只留在這台'].includes(x.label)).map((x) => x.label + ':' + x.checked);
  check('desktopPolicyMenuChoices', heads(menu1).includes('同步到伺服器') && JSON.stringify(choices(menu1)) === JSON.stringify(['完整同步:true', '只同步標題與狀態:false', '只留在這台:false'])
    && menu1.some((x) => x.label === '刪除對話…') && !!sent1 && sent1.conversationId === 'c1' && sent1.policy === 'metadata'
    && JSON.stringify(choices(menu2)) === JSON.stringify(['完整同步:false', '只同步標題與狀態:true', '只留在這台:false']) && sameNotSent
    && heads(menuPaused).includes('同步到伺服器（偵測到權杖，已暫停）') && choices(menuPaused).every((x) => x.endsWith(':false')) && choices(menuPaused).length === 3
    && !heads(menuNoSync).some((x) => x.startsWith('同步到伺服器')) && choices(menuNoSync).length === 0,
  { menu1, sent1, menu2, sameNotSent, menuPaused: heads(menuPaused).concat(choices(menuPaused)), menuNoSync: menuNoSync.map((x) => x.label || x.head) });
  // 「只留在這台」：先確認；取消不送，確認送一筆；之後標頭的「已從伺服器刪除」、選單只剩「只留在這台」
  await page.click('.tab[data-key="c1"]');
  await sleep(150);
  const before = postedOf(app, 'syncSetConversationPolicy').length;
  // 確認框沒出現時記成失敗（不讓探針整支出錯）
  const confirmBox = () => page.waitForSelector('.dlg-confirm .btn.danger-solid', { timeout: 3000 }).catch(() => null);
  await openMore(app, 'c1');
  await clickMenu(app, '只留在這台');
  const dlg1 = await confirmBox();
  const msg = dlg1 ? await page.$eval('.dlg-confirm', (n) => n.textContent) : null;
  if (dlg1) {
    await shot(app, 'desktop-local-only-confirm-1440');
    await page.click('.dlg-confirm .btn:not(.danger-solid)');
  }
  await sleep(250);
  const afterCancel = postedOf(app, 'syncSetConversationPolicy').length - before;
  await openMore(app, 'c1');
  await clickMenu(app, '只留在這台');
  const dlg2 = await confirmBox();
  if (dlg2) await page.click('.dlg-confirm .btn.danger-solid');
  const sent = await until(() => postedOf(app, 'syncSetConversationPolicy')[before] || null, 2000);
  const badge = await until(async () => ((await replicaState(app, 'c1')) || {}).serverDeleted || null, 2000);
  const badgeText = await page.$eval(convSel('c1') + ' [data-field="serverDeleted"]', (n) => n.textContent).catch(() => null);
  const menuDeleted = await openMore(app, 'c1');
  await closeMenu(app);
  await shot(app, 'desktop-server-deleted-1440');
  check('desktopLocalOnlyConfirm', !!dlg1 && !!dlg2 && !!msg && msg.includes(LOCAL_ONLY_TEXT) && afterCancel === 0 && !!sent && sent.policy === 'localOnly' && sent.conversationId === 'c1'
    && postedOf(app, 'syncSetConversationPolicy').length === before + 1 && !!badge && badgeText === '已從伺服器刪除'
    && JSON.stringify(choices(menuDeleted)) === JSON.stringify(['只留在這台:true']),
  { msg, afterCancel, sent, badge: !!badge, badgeText, menuDeleted: choices(menuDeleted) });
  await HP.closePage(app);
}

/** 打開設定的「多電腦同步」分頁 */
async function openSyncTab(app) {
  await app.page.click('#btnSettings');
  await app.page.waitForSelector('.dlg-settings');
  await app.page.click('.dlg-settings .set-tab[data-tab="sync"]');
  await app.page.waitForSelector('.dlg-settings .sync[data-phase]');
  await sleep(200);
}

const SYNC_SEL = '.dlg-settings .sync ';
const syncState = (app) => app.page.evaluate((base) => {
  const q = (s) => document.querySelector(base + s);
  const vis = (el) => !!el && el.isConnected && !el.closest('[hidden]') && getComputedStyle(el).display !== 'none';
  const bf = q('[data-field="backfill"]');
  const notify = q('[data-field="notifyRemoteActions"]');
  const level = q('[data-field="remoteControlLevel"] .seg-btn.on');
  const un = q('[data-field="remoteUnsupported"]');
  return {
    backfill: vis(bf) ? bf.textContent : null, notify: vis(notify) ? notify.checked : null, level: level ? level.dataset.value : null,
    unsupported: vis(un) ? un.textContent : null, group: (q('[data-group="policy"]') || {}).textContent || null,
    logs: vis(q('[data-group="remoteLog"]')), trashBtn: vis(q('[data-act="trash"]')), auditBtn: vis(q('[data-act="auditLog"]')),
  };
}, SYNC_SEL);

async function runDesktopSyncSettings() {
  const status = LF.syncWith(NOW, { backfill: LF.BACKFILL.busy, remoteSupported: false, notifyRemoteActions: true, remoteControlLevel: 'Off' });
  const app = await openDesktopLive('desktop-sync-settings', { init: { sync: status } });
  const { page, host } = app;
  await openSyncTab(app);
  const s0 = await syncState(app);
  await shot(app, 'desktop-sync-settings-1440');
  // 回補：12 個約 50 MB → 1 個不到 1 MB → 沒有在回補（列藏起來）
  const sendSync = (patch) => { app.dh.st.sync = Object.assign({}, app.dh.st.sync, patch); return host.send({ type: 'sync', sync: app.dh.st.sync }); };
  await sendSync({ backfill: LF.BACKFILL.small });
  const s1 = await until(async () => { const s = await syncState(app); return s.backfill !== s0.backfill ? s : null; }, 2000);
  const done = Object.assign({}, app.dh.st.sync);
  delete done.backfill;
  app.dh.st.sync = done;
  await host.send({ type: 'sync', sync: done });
  const s2 = await until(async () => { const s = await syncState(app); return s.backfill === null ? s : null; }, 2000);
  check('desktopBackfillRow', s0.backfill === '還有 12 個對話（約 50 MB）' && !!s1 && s1.backfill === '還有 1 個對話（不到 1 MB）' && !!s2, { s0: s0.backfill, s1: s1 && s1.backfill, s2: !!s2 });
  // remoteSupported 不是 true：說明這一版還不會執行；true 時藏起來
  await sendSync({ remoteSupported: true });
  const s3 = await until(async () => { const s = await syncState(app); return s.unsupported === null ? s : null; }, 2000);
  check('desktopRemoteSupportedNote', s0.unsupported === '這一版還不會執行遠端指令' && !!s3, { before: s0.unsupported, after: s3 && s3.unsupported });
  // 「可以操作」先確認：取消不送、回到「不接受」；確認送一筆；「只能查看」直接送（不確認）
  const levels = () => postedOf(app, 'syncSetPolicy').filter((m) => 'remoteControlLevel' in m);
  // 確認框沒出現時記成失敗（不讓探針整支出錯）
  const confirmBox = () => page.waitForSelector('.dlg-confirm .btn.danger-solid', { timeout: 3000 }).catch(() => null);
  await page.click(SYNC_SEL + '[data-field="remoteControlLevel"] .seg-btn[data-value="Control"]');
  const dlg1 = await confirmBox();
  const msg = dlg1 ? await page.$eval('.dlg-confirm', (n) => n.textContent) : null;
  if (dlg1) {
    await shot(app, 'desktop-control-confirm-1440');
    await page.click('.dlg-confirm .btn:not(.danger-solid)');
  }
  const back = await until(async () => ((await syncState(app)).level === 'Off' ? 'Off' : null), 2000);
  await sleep(200);
  const afterCancel = levels().length;
  await page.click(SYNC_SEL + '[data-field="remoteControlLevel"] .seg-btn[data-value="Control"]');
  const dlg2 = await confirmBox();
  if (dlg2) await page.click('.dlg-confirm .btn.danger-solid');
  const on = await until(async () => ((await syncState(app)).level === 'Control' ? 'Control' : null), 2000);
  // 確認框的關閉動畫結束（元素拿掉）之後才換「只能查看」：之後不該再出現確認框
  await until(async () => !(await page.$('.dlg-confirm')), 2000);
  await page.click(SYNC_SEL + '[data-field="remoteControlLevel"] .seg-btn[data-value="View"]');
  const view = await until(async () => ((await syncState(app)).level === 'View' ? 'View' : null), 2000);
  await sleep(300);
  const viewDialog = await page.$('.dlg-confirm');
  const sent = levels().map((m) => m.remoteControlLevel);
  check('desktopControlConfirm', !!dlg1 && !!dlg2 && !!msg && msg.includes('知道擁有者密碼、或拿到瀏覽器登入狀態的人，都能在這台送訊息、核准與回答') && back === 'Off' && afterCancel === 0
    && on === 'Control' && view === 'View' && !viewDialog && JSON.stringify(sent) === JSON.stringify(['Control', 'View']),
  { msg, back, afterCancel, on, view, viewDialog: !!viewDialog, sent });
  // 通知開關：照 local.notifyRemoteActions；切換只送 notifyRemoteActions；宿主沒有這個鍵時不顯示
  const n0 = (await syncState(app)).notify;
  await page.click(SYNC_SEL + '[data-field="notifyRemoteActions"]');
  const nSent = await until(() => postedOf(app, 'syncSetPolicy').find((m) => 'notifyRemoteActions' in m) || null, 2000);
  const n1 = await until(async () => ((await syncState(app)).notify === false ? 'off' : null), 2000);
  const noKey = Object.assign({}, app.dh.st.sync, { local: Object.assign({}, app.dh.st.sync.local) });
  delete noKey.local.notifyRemoteActions;
  await host.send({ type: 'sync', sync: noKey });
  const n2 = await until(async () => ((await syncState(app)).notify === null ? 'hidden' : null), 2000);
  check('desktopNotifySwitch', n0 === true && !!nSent && nSent.notifyRemoteActions === false && Object.keys(nSent).sort().join() === 'cmd,notifyRemoteActions' && !!n1 && !!n2,
    { n0, sent: nSent, off: !!n1, hiddenWithoutKey: !!n2 });
  // 遠端執行指令的開關（sync-protocol 14.11）：宿主沒有這個鍵時不顯示；層級不是「可以操作」時停用；打開前先確認（取消不送），關掉直接送
  const execState = () => page.evaluate((base) => {
    const input = document.querySelector(base + '[data-field="allowRemoteExec"]');
    const vis = !!input && !input.closest('[hidden]');
    return { visible: vis, checked: !!input && input.checked, disabled: !!input && input.disabled };
  }, SYNC_SEL);
  const execSent = () => postedOf(app, 'syncSetPolicy').filter((m) => 'allowRemoteExec' in m);
  const e0 = await execState();
  await sendSync({ local: Object.assign({}, app.dh.st.sync.local, { allowRemoteExec: false }) });
  const e1 = await until(async () => { const e = await execState(); return e.visible ? e : null; }, 2000);
  await sendSync({ local: Object.assign({}, app.dh.st.sync.local, { remoteControlLevel: 'Control' }) });
  const e2 = await until(async () => { const e = await execState(); return e.visible && !e.disabled ? e : null; }, 2000);
  await page.click(SYNC_SEL + '[data-field="allowRemoteExec"]');
  const execDlg1 = await confirmBox();
  const execMsg = execDlg1 ? await page.$eval('.dlg-confirm', (n) => n.textContent) : null;
  if (execDlg1) {
    await shot(app, 'desktop-exec-confirm-1440');
    await page.click('.dlg-confirm .btn:not(.danger-solid)');
  }
  const e3 = await until(async () => { const e = await execState(); return !e.checked ? e : null; }, 2000);
  await until(async () => !(await page.$('.dlg-confirm')), 2000);
  const afterExecCancel = execSent().length;
  await page.click(SYNC_SEL + '[data-field="allowRemoteExec"]');
  const execDlg2 = await confirmBox();
  if (execDlg2) await page.click('.dlg-confirm .btn.danger-solid');
  const e4 = await until(async () => { const e = await execState(); return e.checked ? e : null; }, 2000);
  await until(async () => !(await page.$('.dlg-confirm')), 2000);
  await shot(app, 'desktop-exec-switch-1440');
  await page.click(SYNC_SEL + '[data-field="allowRemoteExec"]');
  const e5 = await until(async () => { const e = await execState(); return !e.checked ? e : null; }, 2000);
  await sleep(200);
  const offDialog = await page.$('.dlg-confirm');
  const sentExec = execSent().map((m) => m.allowRemoteExec);
  check('desktopExecSwitch', !e0.visible && !!e1 && e1.disabled && !!e2 && !!execDlg1 && !!execMsg && execMsg.includes('myriagent-remote') && execMsg.includes('UAC')
    && !!e3 && afterExecCancel === 0 && !!execDlg2 && !!e4 && !!e5 && !offDialog && JSON.stringify(sentExec) === JSON.stringify([true, false]),
  { e0, e1, e2, e3: !!e3, e4: !!e4, e5: !!e5, afterExecCancel, offDialog: !!offDialog, sentExec, execMsg });
  // 文字（D30、WP4.4 的閘門 5e）
  const s4 = await syncState(app);
  check('desktopPolicyTexts', !!s4.group && s4.group.includes('只能在這台改，馬上生效。') && s4.group.includes('遠端操作時在這台通知') && s4.logs && s4.trashBtn && s4.auditBtn,
    { group: s4.group, logs: s4.logs });
  await page.setViewportSize({ width: 960, height: 700 });
  await sleep(250);
  await shot(app, 'desktop-sync-settings-960');
  const hs = await hScroll(app);
  check('desktopSyncSettingsNoHScroll', okScroll(hs), hs);
  await HP.closePage(app);
}

/** 紀錄對話框：每列的流水號、狀態、來源與其他細節；「載入更早的」在不在 */
const auditState = (app) => app.page.evaluate(() => {
  const dlg = document.querySelector('.dlg-slog');
  if (!dlg) return null;
  const vis = (el) => !!el && el.isConnected && !el.hidden && getComputedStyle(el).display !== 'none';
  return {
    rows: [...dlg.querySelectorAll('[data-field="auditList"] .slog-row')].map((r) => ({
      seq: r.dataset.seq, cmd: r.querySelector('.slog-cmd').textContent, state: r.querySelector('[data-field="auditState"]').textContent,
      issuer: r.querySelector('[data-field="auditIssuer"]').textContent, meta: r.querySelector('.slog-meta').textContent, tag: !!r.querySelector('.tag.warn'),
    })),
    more: vis(dlg.querySelector('[data-act="auditMore"]')), status: vis(dlg.querySelector('[data-field="auditStatus"]')) ? dlg.querySelector('[data-field="auditStatus"]').textContent : null,
    exportText: vis(dlg.querySelector('[data-field="auditExport"]')) ? dlg.querySelector('[data-field="auditExportText"]').textContent : null,
    exportTone: dlg.querySelector('[data-field="auditExport"]').dataset.tone || null, reveal: vis(dlg.querySelector('[data-act="auditReveal"]')),
  };
});

async function runDesktopAudit() {
  const pages = LF.auditPages(NOW);
  const EXPORT_PATH = 'C:\\Users\\Lin\\AppData\\Local\\Myriagent\\remote-audit\\d_A-20261021-153000.jsonl';
  const app = await openDesktopLive('desktop-audit', {
    host: {
      // 第一頁（沒有 before）；第二頁要以第一頁最舊一筆的 (at, seq) 才給（帶錯就回空的一頁，畫面看得出來）
      audit: (m) => (m.before === undefined && m.beforeSeq === undefined ? { entries: pages.first, hasMore: true }
        : m.before === pages.oldestAt && m.beforeSeq === pages.oldestSeq ? { entries: pages.second, hasMore: false } : { entries: [], hasMore: false }),
      exportResult: (n) => (n === 1 ? { path: EXPORT_PATH, count: 5, error: null } : { path: null, count: 0, error: '匯出逾時，請稍後再試' }),
    },
  });
  const { page } = app;
  await openSyncTab(app);
  await page.click(SYNC_SEL + '[data-act="auditLog"]');
  const a1 = await until(async () => { const s = await auditState(app); return s && s.rows.length === 3 ? s : null; }, 3000);
  await shot(app, 'desktop-audit-1440');
  // 第七批：讀進來的那一頁滑進來（fx-row-in）——記下「載入更早的」新加的列
  await page.evaluate(() => {
    window.__slogIn = [];
    const list = document.querySelector('.dlg-slog [data-field="auditList"]');
    new MutationObserver((recs) => { for (const r of recs) for (const n of r.addedNodes) if (n.classList) window.__slogIn.push(n.classList.contains('fx-row-in')); }).observe(list, { childList: true });
  });
  await page.click('.dlg-slog [data-act="auditMore"]');
  const a2 = await until(async () => { const s = await auditState(app); return s && s.rows.length === 5 ? s : null; }, 3000);
  const reqs = postedOf(app, 'syncLoadRemoteAudit');
  const seqs = a2 ? a2.rows.map((r) => r.seq).join() : null;
  const r8 = a1 && a1.rows.find((r) => r.seq === '8');
  const r7 = a1 && a1.rows.find((r) => r.seq === '7');
  check('desktopAuditDialogPaging', !!a1 && a1.more && !!a2 && !a2.more && seqs === '9,8,7,6,5' && reqs.length === 2
    && JSON.stringify(reqs[0]) === JSON.stringify({ cmd: 'syncLoadRemoteAudit', limit: 50 })
    && JSON.stringify(reqs[1]) === JSON.stringify({ cmd: 'syncLoadRemoteAudit', limit: 50, before: pages.oldestAt, beforeSeq: pages.oldestSeq })
    && !!r8 && r8.state === '執行中或不明' && r8.issuer === '來源 Edge・' + LF.XSS && r8.tag && r8.meta.includes('收到 2 次')
    && !!r7 && r7.issuer === '來源 來源名稱無法顯示' && r7.state === '已拒絕' && r7.meta.includes('原因 政策不允許') && r7.meta.includes('層級 2') && r7.meta.includes('政策 只能查看')
    && a2.rows[3].state === '結果不明' && a2.rows[4].meta.includes('完全自動') && (await HP.xssState(app)).xss === 0,
  { requests: reqs, seqs, first: a1 && a1.rows, more: [a1 && a1.more, a2 && a2.more] });
  // 第七批：每列依結果一顆燈（已執行綠、已拒絕琥珀、結果不明紅、執行中藍）
  const auditLeds = await page.evaluate(() => [...document.querySelectorAll('.dlg-slog [data-field="auditList"] .slog-row')].map((r) => ({
    state: r.querySelector('[data-field="auditState"]').textContent, led: r.dataset.led, color: getComputedStyle(r.querySelector('.slog-head'), '::before').backgroundColor })));
  const slogIn = await page.evaluate(() => window.__slogIn);
  const LED_OF = { 已執行: 'done', 已拒絕: 'rejected', 結果不明: 'unknown', 執行中或不明: 'executing' };
  check('desktopAuditLeds', auditLeds.length === 5 && auditLeds.every((x) => x.led === LED_OF[x.state] && x.color !== 'rgba(0, 0, 0, 0)')
    && new Set(auditLeds.map((x) => x.color)).size === new Set(auditLeds.map((x) => x.led)).size && slogIn.length === 2 && slogIn.every(Boolean), { auditLeds, slogIn });
  // 匯出：成功顯示路徑與「在檔案總管顯示」（revealPath，不帶 conversationId）；失敗顯示原因、不給按鈕
  await page.click('.dlg-slog [data-act="auditExport"]');
  const e1 = await until(async () => { const s = await auditState(app); return s && s.exportTone === 'ok' ? s : null; }, 3000);
  await page.click('.dlg-slog [data-act="auditReveal"]');
  const reveal = await until(() => postedOf(app, 'revealPath').at(-1) || null, 2000);
  await shot(app, 'desktop-audit-export-1440');
  await page.click('.dlg-slog [data-act="auditExport"]');
  const e2 = await until(async () => { const s = await auditState(app); return s && s.exportTone === 'err' ? s : null; }, 3000);
  check('desktopAuditExport', !!e1 && e1.exportText === '已匯出 5 筆：' + EXPORT_PATH && e1.reveal && !!reveal && JSON.stringify(reveal) === JSON.stringify({ cmd: 'revealPath', path: EXPORT_PATH })
    && !!e2 && e2.exportText === '匯出逾時，請稍後再試' && !e2.reveal && postedOf(app, 'syncExportRemoteAudit').length === 2,
  { ok: e1 && e1.exportText, reveal, err: e2 && e2.exportText, errReveal: e2 && e2.reveal });
  await page.setViewportSize({ width: 960, height: 700 });
  await sleep(250);
  await shot(app, 'desktop-audit-960');
  const hs = await hScroll(app);
  check('desktopAuditNoHScroll', okScroll(hs), hs);
  await HP.closePage(app);
}

/** 回收區對話框的每一列 */
const trashState = (app) => app.page.evaluate(() => {
  const dlg = document.querySelector('.dlg-slog');
  if (!dlg) return null;
  return {
    rows: [...dlg.querySelectorAll('[data-field="trashList"] .slog-row')].map((r) => {
      const b = r.querySelector('[data-act="trashRestore"]');
      const days = r.querySelector('[data-field="trashDays"]');
      return { id: r.dataset.conversationId, title: r.querySelector('[data-field="trashTitle"]').textContent, issuer: r.querySelector('[data-field="trashIssuer"]').textContent,
        days: days ? days.textContent : null, btn: b.textContent, disabled: b.disabled };
    }),
    status: (dlg.querySelector('[data-field="trashStatus"]') || {}).textContent || null,
  };
});

async function runDesktopTrash() {
  const entries = LF.trashEntries(NOW);
  const app = await openDesktopLive('desktop-trash', { host: { trash: entries, restoreDelayMs: 600 } });
  const { page } = app;
  await openSyncTab(app);
  await page.click(SYNC_SEL + '[data-act="trash"]');
  const t1 = await until(async () => { const s = await trashState(app); return s && s.rows.length === 3 ? s : null; }, 3000);
  await shot(app, 'desktop-trash-1440');
  await page.click('.dlg-slog [data-act="trashRestore"][data-conversation-id="' + entries[0].conversationId + '"]');
  const pending = await trashState(app);
  const t2 = await until(async () => { const s = await trashState(app); return s && s.rows.length === 2 ? s : null; }, 3000);
  const restore = postedOf(app, 'syncRestoreTrash');
  const toasts = await HP.domToasts(app);
  check('desktopTrashRestore', !!t1 && t1.rows[0].title === '遠端刪掉的對話 ' + LF.XSS && t1.rows[0].issuer === '發起者 Chrome・Windows' && t1.rows[0].days === '還剩 28 天'
    && t1.rows[1].title === '（沒有標題）' && t1.rows[1].issuer === '發起者 來源名稱無法顯示' && t1.rows[2].days === '還剩 1 天' && t1.rows.every((r) => r.btn === '還原' && !r.disabled)
    && !!pending && pending.rows[0].disabled && pending.rows[0].btn === '還原中…'
    && restore.length === 1 && JSON.stringify(restore[0]) === JSON.stringify({ cmd: 'syncRestoreTrash', conversationId: entries[0].conversationId })
    && !!t2 && t2.rows.map((r) => r.id).join() === [entries[1].conversationId, entries[2].conversationId].join() && toasts.includes('已還原到這台') && (await HP.xssState(app)).xss === 0,
  { first: t1 && t1.rows, pending: pending && pending.rows[0], restore, after: t2 && t2.rows.map((r) => r.id), toasts });
  await HP.closePage(app);
  // 沒有配對（remote 是 null）：回收區照樣打得開（解除配對之後回收區還在）；空的回收區有說明
  const app2 = await openDesktopLive('desktop-trash-unpaired', { initPatch: { remote: null }, host: { trash: [] } });
  await openSyncTab(app2);
  const phase = await app2.page.$eval('.dlg-settings .sync', (n) => n.dataset.phase).catch(() => null);
  await app2.page.click(SYNC_SEL + '[data-act="trash"]');
  const t3 = await until(async () => { const s = await trashState(app2); return s && s.status === '回收區是空的' ? s : null; }, 3000);
  check('desktopTrashUnpaired', phase === 'unpaired' && !!t3 && t3.rows.length === 0 && postedOf(app2, 'syncListTrash').length === 1, { phase, status: t3 && t3.status });
  await HP.closePage(app2);
}

/** 側欄：每一列的標題；電腦篩選下拉顯示時按鈕上的名稱（不顯示是 null） */
const sidebarState = (app) => app.page.evaluate(() => {
  const b = document.querySelector('.sb-sec-hist [data-field="historyDevice"]');
  return {
    rows: [...document.querySelectorAll('.sb-list .crow')].map((r) => r.querySelector('.crow-title').textContent),
    device: b && getComputedStyle(b).display !== 'none' ? b.querySelector('.sb-pick-label').textContent : null,
  };
});

/** 打開電腦篩選下拉：回選單各項的名稱、對話數、有沒有打勾 */
async function openDeviceFilter(app) {
  await app.page.click('.sb-sec-hist [data-field="historyDevice"]');
  await app.page.waitForSelector('.menu', { timeout: 2000 }).catch(() => {});
  return app.page.$$eval('.menu .menu-item', (xs) => xs.map((x) => ({ label: x.querySelector('.mi-label').textContent, n: x.querySelector('.mi-hint') && x.querySelector('.mi-hint').textContent, checked: x.classList.contains('checked') })));
}

/** 在電腦篩選下拉裡選名稱是 label 的那一項（名稱比對整段，含 XSS 字樣） */
async function chooseDevice(app, label) {
  await openDeviceFilter(app);
  await app.page.evaluate((t) => { const x = [...document.querySelectorAll('.menu .menu-item')].find((b) => b.querySelector('.mi-label').textContent === t); if (x) x.click(); }, label);
}

async function runDesktopHistoryScope() {
  const all = LF.desktopList(NOW);
  const localTitles = all.filter((c) => !c.deviceId).map((c) => c.title);
  const homeTitles = all.filter((c) => c.deviceId === LF.DEVICE.view).map((c) => c.title);
  const laptop = '筆電 ' + LF.XSS;
  const lastLayout = (app) => { const l = postedOf(app, 'layout').at(-1); return l ? l.layout : null; };
  const app = await openDesktopLive('desktop-history-scope', { init: { layout: desktopLayout(['c1']) } });
  const s0 = await until(async () => { const s = await sidebarState(app); return s.rows.length === all.length ? s : null; }, 3000);
  // 選單：所有電腦、這台電腦、其他電腦照名冊的順序（筆電、HOME-PC），各帶對話數
  const menu0 = await openDeviceFilter(app);
  await shot(app, 'desktop-history-device-menu-1440');
  await app.page.keyboard.press('Escape');
  await chooseDevice(app, '這台電腦');
  const s1 = await until(async () => { const s = await sidebarState(app); return s.rows.length === localTitles.length ? s : null; }, 2000);
  const layout1 = await until(() => { const l = lastLayout(app); return l && l.historyScope === 'local' ? l : null; }, 2000);
  await shot(app, 'desktop-history-local-1440');
  await chooseDevice(app, 'HOME-PC');
  const s1b = await until(async () => { const s = await sidebarState(app); return s.rows.length === homeTitles.length && s.device === 'HOME-PC' ? s : null; }, 2000);
  const layout1b = await until(() => { const l = lastLayout(app); return l && l.historyDevice === LF.DEVICE.view ? l : null; }, 2000);
  await chooseDevice(app, '所有電腦');
  const s2 = await until(async () => { const s = await sidebarState(app); return s.rows.length === all.length ? s : null; }, 2000);
  const layout2 = await until(() => { const l = lastLayout(app); return l && l.historyScope === 'all' && l.historyDevice === undefined ? l : null; }, 2000);
  const xss1 = await HP.xssState(app);
  await HP.closePage(app);
  // 存下的版面是 local 或某一台：一開始就照它篩；沒有副本時不顯示電腦下拉（也不篩）
  const app2 = await openDesktopLive('desktop-history-restore', { init: { layout: desktopLayout(['c1'], { historyScope: 'local' }) } });
  const s3 = await until(async () => { const s = await sidebarState(app2); return s.rows.length === localTitles.length ? s : null; }, 3000);
  await HP.closePage(app2);
  const app2b = await openDesktopLive('desktop-history-restore-device', { init: { layout: desktopLayout(['c1'], { historyScope: 'all', historyDevice: LF.DEVICE.view }) } });
  const s3b = await until(async () => { const s = await sidebarState(app2b); return s.rows.length === homeTitles.length ? s : null; }, 3000);
  await HP.closePage(app2b);
  const app3 = await openDesktopLive('desktop-history-noreplica', { init: { conversations: all.filter((c) => !c.deviceId), layout: desktopLayout(['c1'], { historyScope: 'local' }) } });
  const s4 = await until(async () => { const s = await sidebarState(app3); return s.rows.length === localTitles.length ? s : null; }, 3000);
  await HP.closePage(app3);
  check('desktopHistoryScope', !!s0 && s0.device === '所有電腦'
    && JSON.stringify(menu0.map((x) => [x.label, x.n, x.checked])) === JSON.stringify([['所有電腦', '4', true], ['這台電腦', '2', false], [laptop, '1', false], ['HOME-PC', '1', false]])
    && !!s1 && s1.device === '這台電腦' && JSON.stringify(s1.rows) === JSON.stringify(localTitles) && !!layout1 && layout1.historyDevice === undefined
    && !!s1b && JSON.stringify(s1b.rows) === JSON.stringify(homeTitles) && !!layout1b && layout1b.historyScope === 'all'
    && !!s2 && s2.device === '所有電腦' && !!layout2 && xss1.xss === 0
    && !!s3 && s3.device === '這台電腦' && JSON.stringify(s3.rows) === JSON.stringify(localTitles)
    && !!s3b && s3b.device === 'HOME-PC' && JSON.stringify(s3b.rows) === JSON.stringify(homeTitles) && !!s4 && s4.device === null,
  { s0, menu0, s1, s1b, s2: s2 && s2.rows.length, layout: [layout1, layout1b, layout2], xss: xss1, restored: s3, restoredDevice: s3b, noReplica: s4 });

  // 工作區篩選（2026-10-23）：本機一個自選資料夾（demo）、一個預設工作區時出現，和電腦下拉並用；副本看不出那台的資料夾，只算在「所有工作區」。
  // 下拉都在側欄裡、彼此不重疊（三個時第三個自成一排）；預設工作區＋HOME-PC 清單空了要說明，電腦選單的數字照工作區篩選算。
  // 「全部」一次清掉電腦與工作區的篩選，版面裡的電腦篩選也回到 all（不留 historyDevice）
  const folderList = all.map((c) => (c.id === 'c2' ? Object.assign({}, c, { customFolder: false, workingDirectory: 'C:\\Users\\Lin\\AppData\\Local\\Myriagent\\conversations\\c2\\workspace' }) : c));
  const app4 = await openDesktopLive('desktop-history-folder', { init: { conversations: folderList, layout: desktopLayout(['c1']) } });
  await until(async () => { const s = await sidebarState(app4); return s.rows.length === all.length ? s : null; }, 3000);
  const picks = () => app4.page.evaluate(() => {
    const sb = document.querySelector('.sb-sec-hist').getBoundingClientRect();
    const shown = [...document.querySelectorAll('.sb-filters .sb-pick')].filter((b) => getComputedStyle(b).display !== 'none').map((b) => {
      const r = b.getBoundingClientRect();
      return { field: b.dataset.field, label: b.querySelector('.sb-pick-label').textContent, left: r.left, right: r.right, top: r.top, bottom: r.bottom, inside: r.left >= sb.left - 0.5 && r.right <= sb.right + 0.5 };
    });
    const overlap = shown.some((a, i) => shown.some((b, j) => j > i && a.left < b.right - 0.5 && b.left < a.right - 0.5 && a.top < b.bottom - 0.5 && b.top < a.bottom - 0.5));
    const empty = document.querySelector('.sb-empty');
    return { shown, overlap, empty: empty.hidden ? null : empty.querySelector('span').textContent };
  });
  const k0 = await picks();
  await app4.page.click('.sb-sec-hist [data-field="historyFolder"]');
  await app4.page.waitForSelector('.menu', { timeout: 2000 }).catch(() => {});
  const fmenu = await app4.page.$$eval('.menu .menu-item', (xs) => xs.map((x) => [x.querySelector('.mi-label').textContent, x.querySelector('.mi-hint') && x.querySelector('.mi-hint').textContent]));
  await shot(app4, 'desktop-history-folder-menu-1440');
  await app4.page.evaluate(() => { const x = [...document.querySelectorAll('.menu .menu-item')].find((b) => b.querySelector('.mi-label').textContent === '預設工作區'); if (x) x.click(); });
  const f1 = await until(async () => { const s = await sidebarState(app4); return s.rows.length === 1 ? s : null; }, 2000);
  const dmenu = await openDeviceFilter(app4);
  await app4.page.keyboard.press('Escape');
  await chooseDevice(app4, 'HOME-PC');
  const k1 = await until(async () => { const k = await picks(); return k.empty ? k : null; }, 2000);
  await app4.page.click('.sb-sec-hist .sb-all');
  const k2 = await until(async () => { const s = await sidebarState(app4); return s.rows.length === all.length ? await picks() : null; }, 2000);
  const layout4 = await until(() => { const l = lastLayout(app4); return l && l.historyScope === 'all' && l.historyDevice === undefined ? l : null; }, 2000);
  const allOn = await app4.page.$eval('.sb-sec-hist .sb-all', (x) => x.classList.contains('on'));
  const xss4 = await HP.xssState(app4);
  await HP.closePage(app4);
  const rowsOf = (k) => [...new Set(k.shown.map((x) => Math.round(x.top)))].length;
  check('desktopHistoryFolder', k0.shown.some((x) => x.field === 'historyFolder' && x.label === '所有工作區') && k0.shown.some((x) => x.field === 'historyDevice')
    && k0.shown.every((x) => x.inside) && !k0.overlap && rowsOf(k0) === (k0.shown.length > 2 ? 2 : 1)
    && JSON.stringify(fmenu) === JSON.stringify([['所有工作區', '4'], ['demo', '1'], ['預設工作區', '1']])
    && !!f1 && JSON.stringify(f1.rows) === JSON.stringify(['這台只在本機的對話'])
    && JSON.stringify(dmenu.map((x) => [x.label, x.n])) === JSON.stringify([['所有電腦', '1'], ['這台電腦', '1'], [laptop, '0'], ['HOME-PC', '0']])
    && !!k1 && k1.empty === '「HOME-PC」在預設工作區沒有對話' && k1.shown.every((x) => x.inside) && !k1.overlap && xss4.xss === 0
    && !!k2 && k2.shown.every((x) => x.label.startsWith('所有')) && !!layout4 && allOn,
  { k0, fmenu, f1, dmenu, k1, k2: k2 && k2.shown.map((x) => x.label), layout4, allOn, xss: xss4 });
}

// ───── 步 11：瀏覽器檔案（D32～D34：上傳與附件晶片、資料夾挑選、檔案預覽、政策 Off 的影片；K-M2 的副本媒體）─────

const SHA_RE = /^[0-9a-f]{64}$/;
const UPLOADING_TEXT = '附件上傳中，完成後才能送出';
const DRAFT_ATTACH_TEXT = '送出第一則訊息後才能附加檔案';
const AWAIT_ATTACH_TEXT = '附件還在處理中，請稍候';
const FILE_REF_ERROR = '這個檔案不能從網頁讀取';
const POLICY_OFF_TEXT = '這台電腦的遠端政策是關閉，只能看縮圖';
const PNG_BYTES = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==', 'base64');
const fileOf = (name, content, mimeType = 'text/plain') => ({ name, mimeType, buffer: Buffer.isBuffer(content) ? content : Buffer.from(content, 'utf8') });
const uploadReqs = (api) => api.requests.filter((r) => r.path.startsWith('/api/v1/uploads/'));
const uploadKey = (id) => 'myriagent.app.uploaded.v1:' + id;
const storedOf = (app, id) => app.page.evaluate((k) => { try { return sessionStorage.getItem(k); } catch { return 'threw'; } }, uploadKey(id));

/** 輸入區的上傳晶片（狀態、名稱、說明、× 看不看得到）、迴紋針、送出鈕、閘門說明、輸入框的字 */
const uploadState = (app, id) => app.page.evaluate((cid) => {
  const root = document.querySelector('.conv[data-conv="' + cid + '"]');
  if (!root) return null;
  const shown = (el) => !!el && !el.hidden && getComputedStyle(el).display !== 'none';
  const box = root.querySelector('[data-field="uploads"]');
  const attach = root.querySelector('[data-field="attach"]');
  const send = root.querySelector('.cbtn.send');
  const gate = root.querySelector('[data-field="sendGate"]');
  return {
    shown: shown(box),
    chips: box ? [...box.querySelectorAll('.up-chip')].map((c) => ({ state: c.dataset.state, name: c.querySelector('.att-name').textContent, sub: c.querySelector('.up-sub').textContent, x: shown(c.querySelector('.chip-x')) })) : [],
    attach: attach ? { disabled: attach.disabled, title: attach.title } : null,
    sendDisabled: !!send && send.disabled, sendHidden: !shown(send), sendResume: !!send && send.classList.contains('resume'),
    gate: shown(gate) ? gate.querySelector('.cgate-text').textContent : null,
    text: root.querySelector('textarea.cinput').value,
  };
}, id);
/** 等到晶片符合 pred（uploadState 的結果） */
const untilChips = (app, id, pred, ms = 5000) => until(async () => { const s = await uploadState(app, id); return s && pred(s) ? s : null; }, ms);
const allState = (st, n) => (s) => s.chips.length === n && s.chips.every((c) => c.state === st);

/** 按迴紋針、在檔案選擇裡選這些檔案（files：Playwright 的 {name, mimeType, buffer} 或路徑） */
async function chooseFiles(app, id, files) {
  const [fc] = await Promise.all([app.page.waitForEvent('filechooser', { timeout: 4000 }), app.page.click(convSel(id) + ' [data-field="attach"]')]);
  await fc.setFiles(files);
}

/** 模擬拖放或貼上檔案（kind：'drop'｜'paste'）：頁內建 DataTransfer，照瀏覽器的順序送事件 */
const dropFiles = (app, id, kind, files) => app.page.evaluate(([sel, k, list]) => {
  const dt = new DataTransfer();
  for (const f of list) dt.items.add(new File([f.text], f.name, { type: f.type }));
  if (k === 'paste') {
    document.querySelector(sel + ' textarea.cinput').dispatchEvent(new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true }));
    return;
  }
  const root = document.querySelector(sel);
  for (const type of ['dragenter', 'dragover', 'drop']) root.dispatchEvent(new DragEvent(type, { dataTransfer: dt, bubbles: true, cancelable: true }));
}, [convSel(id), kind, files]);

/** 假 hub 回 addUploaded（預設 done） */
const serveAddUploaded = (hub, st = 'done', code = null) => hub.onCmd('addUploaded', (cmd, arg) => ({ push: [serverResult(arg.commandId, st, code)] }));

/** 開一個 OFFICE-PC（Control）的閒置對話、上傳端點照 E4 回；o 同 openWithLayout，o.uploads 是 api.uploads 的選項 */
async function openUploadConv(name, o = {}) {
  const app = await openWithLayout(name, layoutOf([[[LF.CONV.idle], LF.CONV.idle]]), o);
  await waitLoaded(app, [LF.CONV.idle]);
  app.uploadLog = app.api.uploads(o.uploads || {});
  serveAddUploaded(app.hub);
  return app;
}

async function runUploadBasic() {
  const id = LF.CONV.idle;
  let release;
  const hold = new Promise((r) => { release = r; });
  const app = await openUploadConv('upload-basic', { uploads: { hold } });
  const { hub, api } = app;
  hub.onCmd('send', (cmd, arg) => ({ push: [serverResult(arg.commandId, 'done')] }));
  await chooseFiles(app, id, [fileOf('報表.txt', '第一份檔案的內容'), fileOf('截圖.png', PNG_BYTES, 'image/png')]);
  // 伺服器還沒回：兩個晶片都在上傳中、送出停用並說明
  const during = await untilChips(app, id, allState('uploading', 2), 3000);
  await shot(app, 'hub-upload-progress-1440');
  // 第七批：上傳中的那一行是「已傳／總大小・速度」（還沒有進度時照舊寫百分比），進度條只伸縮（transform，不改寬度）
  const upFx = await app.page.evaluate((cid) => [...document.querySelectorAll('.conv[data-conv="' + cid + '"] [data-field="uploads"] .up-chip')].map((c) => {
    const f = c.querySelector('.up-fill');
    return { sub: c.querySelector('.up-sub').textContent, width: f.style.width, tf: f.style.transform, cw: getComputedStyle(f).width === getComputedStyle(f.parentElement).width };
  }), id);
  check('hubUploadReadout', upFx.length === 2 && upFx.every((u) => (/^[\d.]+(?: [KM]?B)?／[\d.]+ [KM]?B(?:・[\d.]+ [KM]?B\/s)?$/.test(u.sub) || /^上傳中 \d+%$/.test(u.sub))
    && u.width === '' && /^scaleX\([\d.]+\)$/.test(u.tf) && u.cw), upFx);
  const addBefore = hub.cmds('addUploaded').length;
  release();
  const after = await untilChips(app, id, allState('attached', 2));
  const reqs = uploadReqs(api);
  const add = hub.cmds('addUploaded');
  const ups = add.length ? add[0].cmd.uploads : [];
  const log = app.uploadLog.uploads;
  const sameAsServer = ups.length === 2 && ups.every((u) => {
    const l = log.find((x) => x.id === u.uploadId);
    return !!l && l.status === 201 && u.sha256 === l.sha256 && SHA_RE.test(u.sha256) && u.size === l.size && ULID_RE.test(u.uploadId);
  });
  check('hubUploadProgressAndAddUploaded', !!during && during.sendDisabled && during.gate === UPLOADING_TEXT && addBefore === 0
    && reqs.length === 2 && reqs.every((r) => r.method === 'PUT' && r.headers['x-myriagent-csrf'] === '1')
    && add.length === 1 && add[0].cmd.conversationId === id && sameAsServer
    && JSON.stringify(ups.map((u) => u.name).sort()) === JSON.stringify(['報表.txt', '截圖.png'].sort())
    && !!after && after.gate === null && JSON.stringify(after.chips.map((c) => c.name).sort()) === JSON.stringify(['報表.txt', '截圖.png'].sort()),
  { during: during && { gate: during.gate, sendDisabled: during.sendDisabled, chips: during.chips }, reqs: reqs.map((r) => ({ method: r.method, csrf: r.headers['x-myriagent-csrf'], bytes: r.bodyBytes })),
    addUploaded: add.map((a) => a.cmd), log, after: after && after.chips });
  // 只有附件、沒有字：送出鈕可按，而且是一般的送出（夾具的佇列有一則：沒有附件時這顆是「從佇列第一則繼續送出」）；
  // 按了送 text 是空的，done 之後晶片與 sessionStorage 都清掉
  const stored = await storedOf(app, id);
  await app.page.click(convSel(id) + ' .cbtn.send');
  const send = await until(() => hub.cmds('send')[0] || null, 3000);
  const cleared = await untilChips(app, id, (s) => s.chips.length === 0, 3000);
  const storedAfter = await storedOf(app, id);
  check('hubUploadChipsCountForSend', !!after && !after.sendDisabled && !after.sendResume && after.text === '' && !!stored && JSON.parse(stored).length === 2
    && !!send && send.cmd.text === '' && !!cleared && !cleared.shown && cleared.sendResume && storedAfter === null,
  { before: after && { sendDisabled: after.sendDisabled, sendResume: after.sendResume, text: after.text }, stored: stored && JSON.parse(stored).length, send: send && send.cmd,
    cleared, storedAfter });
  await HP.closePage(app);
}

async function runUploadErrors() {
  const id = LF.CONV.idle;
  const app = await openUploadConv('upload-errors');
  const { hub, api } = app;
  app.expect((m) => /Failed to load resource/.test(m.text) && /status of (409|413|507)/.test(m.text) && /\/api\/v1\/uploads\//.test(m.url), '上傳端點刻意回 409／413／507');
  const errorChip = async (status, how, name) => {
    api.uploads({ status });
    const n0 = uploadReqs(api).length;
    if (how === 'choose') await chooseFiles(app, id, [fileOf(name, '內容 ' + status)]);
    else await dropFiles(app, id, how, [{ name, text: '內容 ' + status, type: 'text/plain' }]);
    const s = await untilChips(app, id, (x) => x.chips.some((c) => c.name === name && c.state === 'error'), 4000);
    const chip = s ? s.chips.find((c) => c.name === name) : null;
    return { chip, puts: uploadReqs(api).length - n0 };
  };
  const dismiss = async (name) => {
    await app.page.evaluate(([sel, n]) => {
      const c = [...document.querySelectorAll(sel + ' [data-field="uploads"] .up-chip')].find((x) => x.querySelector('.att-name').textContent === n);
      if (c) c.querySelector('.chip-x').click();
    }, [convSel(id), name]);
    return untilChips(app, id, (x) => !x.chips.some((c) => c.name === name), 2000);
  };
  // 409（拖放進來的）、413（貼上的）、507（迴紋針）：晶片說明逐字、× 看得到、沒有送 addUploaded；按 × 拿掉
  const e409 = await errorChip(409, 'drop', '拖放的.txt');
  const gone409 = await dismiss('拖放的.txt');
  const e413 = await errorChip(413, 'paste', '貼上的.txt');
  await dismiss('貼上的.txt');
  const e507 = await errorChip(507, 'choose', '空間不足.txt');
  await dismiss('空間不足.txt');
  const adds0 = hub.cmds('addUploaded').length;
  check('hubUpload409', !!e409.chip && e409.chip.sub === '這個檔案的上傳內容和先前不同，請重新附加' && e409.chip.x && e409.puts === 1 && !!gone409 && adds0 === 0, { e409, gone: !!gone409, adds: adds0 });
  check('hubUpload413', !!e413.chip && e413.chip.sub === '檔案超過 512 MB，不能從網頁上傳' && e413.chip.x && e413.puts === 1, e413);
  // 同一批一個 507、一個成功：失敗的留著說明，成功的照樣送 addUploaded（只有它）
  api.uploads({ statusFor: (uid, n) => (n === 0 ? 507 : null) });
  await chooseFiles(app, id, [fileOf('甲.txt', '甲的內容'), fileOf('乙.txt', '乙的內容')]);
  const mixed = await untilChips(app, id, (x) => x.chips.length === 2 && x.chips.some((c) => c.state === 'error') && x.chips.some((c) => c.state === 'attached'), 5000);
  const madd = hub.cmds('addUploaded').slice(adds0);
  const okChip = mixed ? mixed.chips.find((c) => c.state === 'attached') : null;
  check('hubUpload507', !!e507.chip && e507.chip.sub === '伺服器的空間不足或待處理的上傳太多，請稍後再試' && e507.chip.x && e507.puts === 1
    && !!mixed && madd.length === 1 && madd[0].cmd.uploads.length === 1 && !!okChip && madd[0].cmd.uploads[0].name === okChip.name
    && mixed.chips.find((c) => c.state === 'error').sub === '伺服器的空間不足或待處理的上傳太多，請稍後再試',
  { e507, mixed: mixed && mixed.chips, addUploaded: madd.map((a) => a.cmd.uploads) });
  // 超過 512 MB：本機就擋（沒有發出 PUT、沒有 addUploaded），晶片說明原因
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'myriagent-upload-'));
  const big = path.join(dir, '太大的影片.mp4');
  try {
    const fd = fs.openSync(big, 'w');
    fs.ftruncateSync(fd, 512 * 1024 * 1024 + 1);
    fs.closeSync(fd);
    api.uploads();
    const n0 = uploadReqs(api).length;
    const a0 = hub.cmds('addUploaded').length;
    await chooseFiles(app, id, [big]);
    const s = await untilChips(app, id, (x) => x.chips.some((c) => c.name === '太大的影片.mp4'), 4000);
    await sleep(500);
    const chip = s ? s.chips.find((c) => c.name === '太大的影片.mp4') : null;
    check('hubUploadTooLargeLocal', !!chip && chip.state === 'error' && chip.sub === '檔案超過 512 MB，不能從網頁上傳' && uploadReqs(api).length === n0 && hub.cmds('addUploaded').length === a0,
      { chip, puts: uploadReqs(api).length - n0, adds: hub.cmds('addUploaded').length - a0 });
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
  // 上傳成功但 addUploaded 被拒（電腦不收）：晶片成為失敗的「沒有加入輸入區，請重新附加」，不存進 sessionStorage
  api.uploads();
  serveAddUploaded(hub, 'rejected', 'policy');
  const ar0 = hub.cmds('addUploaded').length;
  await chooseFiles(app, id, [fileOf('電腦不收.txt', '被拒的附件')]);
  const rej = await untilChips(app, id, (x) => x.chips.some((c) => c.name === '電腦不收.txt' && c.state === 'error'), 4000);
  const rejChip = rej ? rej.chips.find((c) => c.name === '電腦不收.txt') : null;
  const rejStored = await storedOf(app, id);
  check('hubUploadAddRejected', !!rejChip && rejChip.sub === '沒有加入輸入區，請重新附加' && rejChip.x && hub.cmds('addUploaded').length === ar0 + 1
    && !(rejStored && rejStored.includes('電腦不收.txt')),
  { chip: rejChip, adds: hub.cmds('addUploaded').length - ar0, stored: rejStored });
  serveAddUploaded(hub);
  await HP.closePage(app);
}

async function runUploadKept() {
  const id = LF.CONV.idle;
  const app = await openUploadConv('upload-kept');
  const { hub } = app;
  await chooseFiles(app, id, [fileOf('還在下載.txt', '電腦還在下載這個檔')]);
  await untilChips(app, id, allState('attached', 1));
  // 電腦還在下載附件：send 被拒（conflict）、伺服器另送提示與 sendRejected → 晶片與字都留著，網頁沒有自己的說明
  hub.onCmd('send', (cmd, arg) => {
    setTimeout(() => hub.push({ type: 'sendRejected', conversationId: cmd.conversationId, text: cmd.text }), 300);
    return { push: [serverResult(arg.commandId, 'rejected', 'conflict'), { type: 'toast', text: AWAIT_ATTACH_TEXT, level: 'error' }] };
  });
  const t0 = (await HP.domToasts(app)).length;
  await typeAndEnter(app, id, '附上剛才的檔案');
  const back = await until(async () => { const c = await composerState(app, id); return c && c.text === '附上剛才的檔案' ? c : null; }, 3000);
  await sleep(400);
  const s = await uploadState(app, id);
  const c = await composerState(app, id);
  const toasts = (await HP.domToasts(app)).slice(t0);
  const stored = await storedOf(app, id);
  check('hubUploadChipsKeptOnSendRejected', !!back && !!s && s.chips.length === 1 && s.chips[0].state === 'attached' && s.text === '附上剛才的檔案'
    && JSON.stringify(toasts) === JSON.stringify([AWAIT_ATTACH_TEXT]) && c.settle === null && c.gate === null && !!stored && JSON.parse(stored).length === 1,
  { chips: s && s.chips, text: s && s.text, toasts, settle: c.settle, gate: c.gate, stored });
  await HP.closePage(app);
}

async function runUploadRestore() {
  const id = LF.CONV.idle;
  const app = await openUploadConv('upload-restore');
  await chooseFiles(app, id, [fileOf('重新載入.txt', '重新載入後還在')]);
  const attached = await untilChips(app, id, allState('attached', 1));
  await app.page.reload({ waitUntil: 'load' });
  await HP.waitLive(app);
  await waitLoaded(app, [id]);
  const restored = await untilChips(app, id, (s) => s.chips.length === 1, 3000);
  // 伺服器關閉了這個對話（conversationClosed）：分頁關掉、存著的晶片也刪
  const storedBeforeClose = await storedOf(app, id);
  app.hub.push({ type: 'conversationClosed', conversationId: id });
  const tabGone = await until(() => app.page.evaluate((sel) => (document.querySelector(sel) ? null : true), convSel(id)), 3000);
  const storedAfterClose = await storedOf(app, id);
  check('hubUploadChipsClearedOnClose', !!storedBeforeClose && JSON.parse(storedBeforeClose).length === 1 && !!tabGone && storedAfterClose === null,
    { storedBeforeClose, tabGone: !!tabGone, storedAfterClose });
  await HP.closePage(app);
  // sessionStorage 丟例外：上傳照常、存不下；重新載入後沒有晶片，頁面照常（沒有主控台錯誤）
  const app2 = await openUploadConv('upload-restore-nostorage', { open: { initOpts: { sessionStorageThrows: true } } });
  await chooseFiles(app2, id, [fileOf('存不下.txt', '存不下的晶片')]);
  const attached2 = await untilChips(app2, id, allState('attached', 1));
  await app2.page.reload({ waitUntil: 'load' });
  await HP.waitLive(app2);
  const loaded2 = await waitLoaded(app2, [id]);
  await sleep(300);
  const after2 = await uploadState(app2, id);
  const errors2 = HP.unexpectedErrors(app2);
  check('hubUploadChipsRestoredAfterReload', !!attached && !!restored && restored.chips[0].state === 'attached' && restored.chips[0].name === '重新載入.txt' && !restored.sendDisabled
    && !!attached2 && !!loaded2 && !!after2 && after2.chips.length === 0 && !after2.shown && errors2.length === 0,
  { restored: restored && restored.chips, noStorage: { attached: !!attached2, loaded: !!loaded2, chips: after2 && after2.chips, errors: errors2 } });
  await HP.closePage(app2);
}

async function runUploadBlocksReload() {
  // 上傳中（D13 的「沒有上傳中的檔」）：伺服器換版也不自動重新載入，只顯示橫幅；上傳照常完成、晶片還在
  const id = LF.CONV.idle;
  const layout = layoutOf([[[id], id]]);
  let release;
  const hold = new Promise((r) => { release = r; });
  const app = await openUploadConv('upload-blocks-reload', { initOpts: { build: 'aaaa111' }, uploads: { hold } });
  await chooseFiles(app, id, [fileOf('換版時.txt', '伺服器換版時還在上傳')]);
  const during = await untilChips(app, id, allState('uploading', 1), 3000);
  app.hub.push(LF.liveBrowserInit(NOW, { mode: 'main', layout, build: 'bbbb222' }));
  const banner = await until(() => noticeOf(app, 'build'), 3000);
  await sleep(800);
  const reloadsDuring = reloads(app);
  release();
  const after = await untilChips(app, id, allState('attached', 1));
  await sleep(300);
  check('hubUploadBlocksBuildReload', !!during && !!banner && banner.text === BUILD_TEXT && reloadsDuring === 0 && reloads(app) === 0 && !!after,
    { during: during && during.chips, banner, reloadsDuring, reloads: reloads(app), after: after && after.chips });
  await HP.closePage(app);
}

async function runDraftAttach() {
  const app = await openWithLayout('draft-attach', layoutOf([[[LF.DRAFT], LF.DRAFT]]));
  const { api } = app;
  await waitLoaded(app, [LF.DRAFT]);
  api.uploads();
  const s = await uploadState(app, LF.DRAFT);
  const t0 = (await HP.domToasts(app)).length;
  // 拖放、貼上、程式選的檔案都不上傳，提示原因
  await dropFiles(app, LF.DRAFT, 'drop', [{ name: '草稿拖放.txt', text: 'x', type: 'text/plain' }]);
  await dropFiles(app, LF.DRAFT, 'paste', [{ name: '草稿貼上.txt', text: 'x', type: 'text/plain' }]);
  await app.page.setInputFiles(convSel(LF.DRAFT) + ' [data-field="attachInput"]', [fileOf('草稿選的.txt', 'x')]);
  await sleep(500);
  const after = await uploadState(app, LF.DRAFT);
  const toasts = (await HP.domToasts(app)).slice(t0);
  // 資料夾選單有「瀏覽資料夾…」（伺服器轉給選的那台，runDraftDirsPicker）也有「輸入資料夾路徑…」
  await app.page.click(convSel(LF.DRAFT) + ' .ch-folder');
  await app.page.waitForSelector('.menu');
  const menu = await menuState(app);
  await closeMenu(app);
  check('hubDraftAttachDisabled', !!s && s.attach.disabled && s.attach.title === DRAFT_ATTACH_TEXT && uploadReqs(api).length === 0 && after.chips.length === 0
    && toasts.length === 3 && toasts.every((t) => t === DRAFT_ATTACH_TEXT) && menu.some((x) => x.label === '瀏覽資料夾…') && menu.some((x) => x.label === '輸入資料夾路徑…'),
  { attach: s && s.attach, puts: uploadReqs(api).length, chips: after.chips, toasts, menu: menu.map((x) => x.label || x.head) });
  await HP.closePage(app);
}

const PICK_ROOT = 'C:\\Users\\lin\\AppData\\Local\\Myriagent\\workspaces\\demo';
const dirEntry = (name, p) => ({ name, path: p, kind: 'dir', size: null, modified: NOW - LF.HOUR, hidden: false });
/** 抽屜：挑選模式、標題、項目名稱、麵包屑、底列、「選這個資料夾」、上一層 */
const drawerState = (app, id) => app.page.evaluate((cid) => {
  const root = document.querySelector('.conv[data-conv="' + cid + '"]');
  const d = root && root.querySelector('.drawer');
  if (!d) return null;
  const shown = (el) => !!el && !el.hidden && getComputedStyle(el).display !== 'none';
  const pick = d.querySelector('[data-field="pickFolder"]');
  return {
    open: d.classList.contains('open'), picking: d.classList.contains('picking'), title: d.querySelector('.dr-title').textContent,
    names: [...d.querySelectorAll('.dr-body .fe .fe-name')].map((n) => n.textContent), crumbs: [...d.querySelectorAll('.dr-crumbs .cr')].map((n) => n.textContent),
    foot: d.querySelector('.dr-foot').textContent, bar: shown(d.querySelector('[data-field="pickBar"]')), pickPath: d.querySelector('[data-field="pickPath"]').textContent,
    pickDisabled: !!pick && pick.disabled, state: (d.querySelector('.dr-state') || {}).textContent || null,
  };
}, id);
const clickEntry = (app, id, name) => app.page.evaluate(([sel, n]) => {
  const fe = [...document.querySelectorAll(sel + ' .drawer .dr-body .fe')].find((x) => x.querySelector('.fe-name').textContent === n);
  if (fe) fe.click();
  return !!fe;
}, [convSel(id), name]);

/** 假 hub 回 listFiles：dirsOnly 的最上層是兩個最近資料夾；往下列子資料夾（truncated）；一般瀏覽是工作資料夾裡的一個檔案 */
function serveListFiles(hub) {
  hub.onCmd('listFiles', (cmd) => {
    const base = { type: 'files', requestId: cmd.requestId, conversationId: cmd.conversationId, root: PICK_ROOT, error: null };
    if (!cmd.dirsOnly) {
      return { push: [Object.assign(base, { path: PICK_ROOT, parent: null, truncated: false,
        entries: [{ name: 'notes.md', path: PICK_ROOT + '\\notes.md', kind: 'doc', size: 120, modified: NOW - LF.HOUR, hidden: false }] })] };
    }
    if (cmd.path === null) {
      return { push: [Object.assign(base, { path: null, parent: null, truncated: false, entries: [dirEntry('C:\\repo\\alpha', 'C:\\repo\\alpha'), dirEntry('D:\\work\\beta', 'D:\\work\\beta')] })] };
    }
    return { push: [Object.assign(base, { path: cmd.path, parent: null, truncated: true, entries: [dirEntry('src', cmd.path + '\\src'), dirEntry('docs', cmd.path + '\\docs')] })] };
  });
}

async function runDirsPicker() {
  const app = await openWithLayout('dirs-picker', layoutOf([[[LF.CONV.idle], LF.CONV.idle], [[LF.CONV.view], LF.CONV.view]]));
  const { hub } = app;
  await waitLoaded(app, [LF.CONV.idle, LF.CONV.view]);
  serveListFiles(hub);
  hub.onCmd('setFolder', (cmd, arg) => ({ push: [serverResult(arg.commandId, 'done')] }));
  const id = LF.CONV.idle;
  await app.page.click(convSel(id) + ' .ch-folder');
  await app.page.waitForSelector('.menu');
  const menu = await menuState(app);
  await clickMenu(app, '瀏覽資料夾…');
  const top = await until(async () => { const d = await drawerState(app, id); return d && d.open && d.names.length === 3 ? d : null; }, 3000);
  const l1 = hub.cmds('listFiles').at(-1);
  await clickEntry(app, id, 'C:\\repo\\alpha');
  const sub = await until(async () => { const d = await drawerState(app, id); return d && d.pickPath === 'C:\\repo\\alpha' && d.names.length === 2 ? d : null; }, 3000);
  const l2 = hub.cmds('listFiles').at(-1);
  await shot(app, 'hub-dirs-picker-1440');
  const set0 = hub.cmds('setFolder').length;
  await app.page.click(convSel(id) + ' [data-field="pickFolder"]');
  const set = await until(() => hub.cmds('setFolder')[set0] || null, 3000);
  await sleep(250);
  const closed = await drawerState(app, id);
  const lists = hub.cmds('listFiles').length;
  check('hubDirsOnlyPicker', menu.some((x) => x.label === '瀏覽資料夾…' && !x.disabled) && !!top && top.picking && top.title === '選擇工作資料夾' && top.pickDisabled
    && JSON.stringify(top.names) === JSON.stringify(['目前的工作資料夾', 'C:\\repo\\alpha', 'D:\\work\\beta']) && top.crumbs[0] === '最近的資料夾'
    && !!l1 && l1.cmd.dirsOnly === true && l1.cmd.path === null && l1.cmd.conversationId === id
    && !!sub && !sub.pickDisabled && /（只列出前 2 筆）/.test(sub.foot) && sub.foot.startsWith('2 個資料夾') && JSON.stringify(sub.names) === JSON.stringify(['src', 'docs'])
    && !!l2 && l2.cmd.dirsOnly === true && l2.cmd.path === 'C:\\repo\\alpha'
    && !!set && set.cmd.path === 'C:\\repo\\alpha' && set.cmd.conversationId === id && !!closed && !closed.open && !closed.picking && lists === 2,
  { menu: menu.map((x) => x.label), top, l1: l1 && l1.cmd, sub, l2: l2 && l2.cmd, set: set && set.cmd, closed, lists });
  // HOME-PC 是 View：「瀏覽資料夾…」停用並說明
  await app.page.click(convSel(LF.CONV.view) + ' .ch-folder');
  await app.page.waitForSelector('.menu');
  const vmenu = await app.page.evaluate(() => [...document.querySelectorAll('.menu > .menu-item')].map((n) => ({ label: n.querySelector('.mi-label').textContent, desc: (n.querySelector('.mi-desc') || {}).textContent || null, disabled: n.disabled })));
  await closeMenu(app);
  const item = vmenu.find((x) => x.label === '瀏覽資料夾…');
  check('hubDirsOnlyNeedsControl', !!item && item.disabled && item.desc === '在 HOME-PC 的設定中允許遠端操作' && hub.cmds('listFiles').length === lists, { item, lists: hub.cmds('listFiles').length - lists });
  await HP.closePage(app);
}

/** 新對話（伺服器草稿）的「瀏覽資料夾…」（2026-10-27）：同樣送 listFiles dirsOnly（伺服器轉給選的那台），root 是 null（沒有「目前的工作資料夾」）；那台太舊（invalid）時說明 */
async function runDraftDirsPicker() {
  const id = LF.DRAFT;
  const app = await openWithLayout('draft-dirs-picker', layoutOf([[[id], id]]));
  const { hub } = app;
  await waitLoaded(app, [id]);
  let reject = false;
  hub.onCmd('listFiles', (cmd, arg) => {
    if (reject) return { push: [serverResult(arg.commandId, 'rejected', 'invalid')] };
    const base = { type: 'files', requestId: cmd.requestId, conversationId: cmd.conversationId, root: null, error: null };
    const entries = cmd.path === null ? [dirEntry('C:\\repo\\alpha', 'C:\\repo\\alpha'), dirEntry('D:\\work\\beta', 'D:\\work\\beta')] : [dirEntry('src', cmd.path + '\\src')];
    return { push: [Object.assign(base, { path: cmd.path, parent: null, truncated: false, entries }), serverResult(arg.commandId, 'done')] };
  });
  hub.onCmd('setFolder', (cmd, arg) => ({ push: [serverResult(arg.commandId, 'done')] }));
  await app.page.click(convSel(id) + ' .ch-folder');
  await app.page.waitForSelector('.menu');
  const menu = await menuState(app);
  await clickMenu(app, '瀏覽資料夾…');
  const top = await until(async () => { const d = await drawerState(app, id); return d && d.open && d.names.length === 2 ? d : null; }, 3000);
  const l1 = hub.cmds('listFiles').at(-1);
  await clickEntry(app, id, 'C:\\repo\\alpha');
  const sub = await until(async () => { const d = await drawerState(app, id); return d && d.pickPath === 'C:\\repo\\alpha' && d.names.length === 1 ? d : null; }, 3000);
  await shot(app, 'hub-draft-dirs-picker-1440');
  const set0 = hub.cmds('setFolder').length;
  await app.page.click(convSel(id) + ' [data-field="pickFolder"]');
  const set = await until(() => hub.cmds('setFolder')[set0] || null, 3000);
  check('hubDraftDirsPicker', menu.some((x) => x.label === '瀏覽資料夾…' && !x.disabled) && menu.some((x) => x.label === '輸入資料夾路徑…')
    && !!top && top.picking && JSON.stringify(top.names) === JSON.stringify(['C:\\repo\\alpha', 'D:\\work\\beta'])
    && !!l1 && l1.cmd.dirsOnly === true && l1.cmd.path === null && l1.cmd.conversationId === id
    && !!sub && !sub.pickDisabled && !!set && set.cmd.path === 'C:\\repo\\alpha' && set.cmd.conversationId === id,
  { menu: menu.map((x) => x.label), top, l1: l1 && l1.cmd, sub, set: set && set.cmd });
  // 那台的版本還不認得（rejected invalid）：抽屜說明要先更新那台或手輸路徑，不是「讀不到這個資料夾」
  reject = true;
  await app.page.click(convSel(id) + ' .ch-folder');
  await app.page.waitForSelector('.menu');
  await clickMenu(app, '瀏覽資料夾…');
  const old = await until(async () => { const d = await drawerState(app, id); return d && d.open && d.state && d.state !== '讀取資料夾中…' ? d : null; }, 3000);
  check('hubDraftDirsPickerOldDevice', !!old && old.state.includes('版本太舊') && old.state.includes('輸入資料夾路徑'), { old });
  await HP.closePage(app);
}

async function runFileFetch() {
  const id = LF.CONV.idle;
  const items = LF.liveItems(NOW).concat([{ id: 'i7', kind: 'Approval', status: 'Done', time: NOW - LF.MIN, toolKind: 'edit', toolName: '編輯檔案', title: '更新報表',
    filePath: 'C:\\repo\\demo\\report.txt', detail: null, canAllowForSession: false, decision: 'allow', seq: 7 },
  { id: 'i8', kind: 'Approval', status: 'Done', time: NOW - LF.MIN, toolKind: 'edit', toolName: '編輯檔案', title: '更新季報',
    filePath: 'C:\\repo\\demo\\季報.xlsx', detail: null, canAllowForSession: false, decision: 'allow', seq: 8 }]);
  const app = await openWithLayout('file-fetch', layoutOf([[[id], id]]), {
    open: { hub: (hub) => hub.onCmd('open', (cmd) => ({ push: [LF.conversationMessage(NOW, cmd.conversationId, TITLE_OF[cmd.conversationId] || '對話', { items })] })) },
  });
  const { hub, page } = app;
  await waitLoaded(app, [id]);
  serveListFiles(hub);
  const refs = [
    { url: '/f/d_A-ref1', name: 'report.txt', size: 18233, expiresAt: NOW + 15 * LF.MIN, error: null },
    { url: null, error: '伺服器給的任意文字 ' + LF.XSS },
    { url: 'https://evil.example/f/d_A-ref3', name: 'evil.txt', size: 3, expiresAt: NOW + LF.MIN, error: null },
    { url: '/f/d_A-ref4', name: '季報.xlsx', size: 52011, expiresAt: NOW + 15 * LF.MIN, error: null },
  ];
  hub.onCmd('fileFetch', (cmd, arg) => {
    const ref = refs.shift() || { url: null, error: '沒有了' };
    return { push: [serverResult(arg.commandId, 'done'), Object.assign({ type: 'fileRef', requestId: cmd.requestId, conversationId: cmd.conversationId }, ref)] };
  });
  const dialogState = () => page.evaluate(() => {
    const d = document.querySelector('.dlg-file-ref');
    if (!d) return null;
    const a = d.querySelector('[data-field="fileRefOpen"]');
    const err = d.querySelector('[data-field="fileRefError"]');
    return { link: a ? { href: a.getAttribute('href'), rel: a.getAttribute('rel'), target: a.getAttribute('target'), text: a.textContent } : null,
      error: err ? err.textContent : null, name: (d.querySelector('.file-ref-name') || {}).textContent || null, wait: !!d.querySelector('[data-field="fileRefWait"]') };
  });
  // 1. 項目的檔案連結：fileFetch 帶 ULID 的 requestId、路徑；fileRef 之後出現「開啟」，點了開新分頁（相對網址、noopener noreferrer）
  //    （連結不是 fileFetch 的形狀時記成失敗，不讓探針等到逾時出錯）
  const fetchLink = itemSel(id, 'i7') + ' [data-field="fileFetch"]';
  const hasFetchLink = await page.waitForSelector(fetchLink, { timeout: 3000 }).then(() => true, () => false);
  if (hasFetchLink) await page.click(fetchLink);
  const d1 = hasFetchLink ? await until(async () => { const d = await dialogState(); return d && d.link ? d : null; }, 3000) : null;
  const f1 = hub.cmds('fileFetch')[0];
  await shot(app, 'hub-file-ref-1440');
  const reveal = await page.evaluate((sel) => !!document.querySelector(sel + ' .frow button:not(.flink)'), itemSel(id, 'i7'));
  const o0 = (await HP.opened(app)).length;
  if (d1) await page.click('.dlg-file-ref [data-field="fileRefOpen"]');
  await sleep(300);
  const opens = (await HP.opened(app)).slice(o0);
  const after1 = await dialogState();
  const pathNow = await page.evaluate(() => location.pathname);
  check('hubFileFetchDialog', !!d1 && d1.link.href === '/f/d_A-ref1' && d1.link.rel === 'noopener noreferrer' && d1.link.target === '_blank' && d1.link.text === '開啟' && d1.name === 'report.txt'
    && !!f1 && f1.cmd.path === 'C:\\repo\\demo\\report.txt' && f1.cmd.conversationId === id && ULID_RE.test(f1.cmd.requestId) && !reveal
    && JSON.stringify(opens) === JSON.stringify([[HP.ORIGIN + '/f/d_A-ref1', '_blank', 'noopener,noreferrer']]) && after1 === null && pathNow === '/app/',
  { dialog: d1, fetch: f1 && f1.cmd, reveal, opens, after: after1, pathNow });
  // 2. 抽屜裡的檔案：同樣送 fileFetch（路徑是那一筆）；fileRef 有 error → 固定文字、沒有連結
  await page.click(convSel(id) + ' header.ch button[aria-label="檔案瀏覽器"]');
  await until(async () => (await page.evaluate((sel) => document.querySelectorAll(sel + ' .drawer .dr-body .fe').length, convSel(id))) === 1, 3000);
  await clickEntry(app, id, 'notes.md');
  const d2 = await until(async () => { const d = await dialogState(); return d && d.error ? d : null; }, 3000);
  const f2 = hub.cmds('fileFetch')[1];
  await page.click('.dlg-file-ref .dlg-foot button', { timeout: 3000 }).catch(() => {});
  await until(async () => ((await dialogState()) ? null : true), 2000);
  check('hubFileRefErrorFixedText', !!d2 && d2.error === FILE_REF_ERROR && !d2.link && !!f2 && f2.cmd.path === PICK_ROOT + '\\notes.md' && (await HP.xssState(app)).xss === 0,
    { dialog: d2, fetch: f2 && f2.cmd });
  // 3. 網址不是相對網址：當成錯誤
  if (hasFetchLink) await page.click(fetchLink);
  const d3 = hasFetchLink ? await until(async () => { const d = await dialogState(); return d && (d.error || d.link) ? d : null; }, 3000) : null;
  check('hubFileRefAbsoluteRejected', !!d3 && d3.error === FILE_REF_ERROR && !d3.link, d3);
  await page.keyboard.press('Escape');
  await until(async () => ((await dialogState()) ? null : true), 2000);
  // 4. Word、Excel、PowerPoint、PDF（2026-11-05）：瀏覽器版在電腦上拿到連結就直接下載（<a download> 同站的 /f/），框關掉、不給「開啟」
  await page.evaluate(() => {
    window.__downloads = [];
    const orig = HTMLAnchorElement.prototype.click;
    HTMLAnchorElement.prototype.click = function () {
      if (this.hasAttribute('download')) { window.__downloads.push({ href: this.getAttribute('href'), download: this.getAttribute('download') }); return; }
      return orig.call(this);
    };
  });
  const xlsxLink = itemSel(id, 'i8') + ' [data-field="fileFetch"]';
  const hasXlsx = await page.waitForSelector(xlsxLink, { timeout: 3000 }).then(() => true, () => false);
  if (hasXlsx) await page.click(xlsxLink);
  const downloads = hasXlsx ? await until(async () => { const d = await page.evaluate(() => window.__downloads); return d && d.length ? d : null; }, 3000) : null;
  const after4 = await until(async () => ((await dialogState()) ? null : true), 2000);
  const f4 = hub.cmds('fileFetch')[3];
  check('hubOfficeFileDownloads', !!downloads && JSON.stringify(downloads) === JSON.stringify([{ href: '/f/d_A-ref4', download: '季報.xlsx' }]) && after4 === true
    && !!f4 && f4.cmd.path === 'C:\\repo\\demo\\季報.xlsx',
    { downloads, dialogClosed: after4, fetch: f4 && f4.cmd });
  await HP.closePage(app);
}

async function runVideoPolicyOff() {
  const id = LF.CONV.idle;
  // OFFICE-PC 的遠端政策是 Off：/m/ 的影片回 403 policy（設計 Q1-A），影片放不出來 → 說明只能看縮圖
  const devices = LF.liveDevices(NOW).map((d) => (d.id === LF.DEVICE.control ? Object.assign({}, d, { remoteControlLevel: 'Off' }) : d));
  const items = [
    { id: 'v1', kind: 'User', status: 'Done', time: NOW - 3 * LF.MIN, html: '<p>錄一段畫面給我看</p>', attachments: [], seq: 1 },
    { id: 'v2', kind: 'Assistant', status: 'Done', time: NOW - 2 * LF.MIN, html: '<p>錄好了：</p>', media: [{ url: '/m/d_A-vid1', kind: 'video', name: '操作錄影.mp4' }], seq: 2 },
  ];
  const app = await openWithLayout('video-policy-off', layoutOf([[[id], id]]), {
    initOpts: { devices },
    open: {
      hub: (hub) => hub.onCmd('open', (cmd) => ({ push: [LF.conversationMessage(NOW, cmd.conversationId, TITLE_OF[cmd.conversationId] || '對話', { items })] })),
      media: (p) => (p.startsWith('/m/d_A-vid') ? { status: 403, headers: { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' }, body: '{"error":"policy"}' } : null),
    },
  });
  app.expect((m) => /Failed to load resource/.test(m.text) && /status of 403/.test(m.text) && /\/m\/d_A-vid1/.test(m.url), '政策 Off 的影片刻意回 403');
  const { page } = app;
  await waitLoaded(app, [id]);
  const note = await until(() => page.evaluate((sel) => { const n = document.querySelector(sel + ' [data-field="mediaPolicyNote"]'); return n ? n.textContent : null; }, itemSel(id, 'v2')), 5000);
  // 放大播放：播放器的錯誤說明也是這一句
  await page.click(itemSel(id, 'v2') + ' .mvid-cap button[aria-label="放大播放"]');
  const vp = await until(() => page.evaluate(() => { const e = document.querySelector('.vp [data-field="videoError"]'); return e && !e.hidden ? e.textContent : null; }), 5000);
  await shot(app, 'hub-video-policy-off-1440');
  await page.keyboard.press('Escape');
  check('hubVideoPolicyOff', note === POLICY_OFF_TEXT && vp === POLICY_OFF_TEXT, { note, player: vp });
  await HP.closePage(app);
}

async function runDesktopReplicaMedia() {
  // K-M2：副本的圖片附件與媒體是 ReplicaMediaUrls 的 https://myriagent.local/m/{key}、/t/{key}?s=320（桌面的 CSP 下載得到、不違規）
  const id = LF.CONV.running;
  const key = 'd_B-0f3c9a52b7e94d0c8a61f2d4e5b6c7d9';
  const items = [
    { id: 'm1', kind: 'User', status: 'Done', time: NOW - 2 * LF.MIN, html: '<p>看這張</p>', seq: 1,
      attachments: [{ id: 'x1', name: 'screen.png', kind: 'Image', size: '1 KB', url: 'https://myriagent.local/m/' + key, thumbUrl: 'https://myriagent.local/t/' + key + '?s=320', path: null }] },
    { id: 'm2', kind: 'Assistant', status: 'Done', time: NOW - LF.MIN, html: '<p>收到</p>', seq: 2, media: [{ url: 'https://myriagent.local/m/' + key, kind: 'image', name: 'screen.png' }] },
  ];
  const app = await openDesktopLive('desktop-replica-media', { init: { layout: desktopLayout([id]) }, host: { replica: () => ({ items }) } });
  const { page } = app;
  const imgs = await until(() => page.evaluate((sel) => {
    const list = [...document.querySelectorAll(sel + ' .items img')];
    return list.length === 2 && list.every((i) => i.complete && i.naturalWidth > 0) ? list.map((i) => i.getAttribute('src')) : null;
  }, convSel(id)), 5000);
  check('desktopReplicaMedia', !!imgs && imgs.includes('https://myriagent.local/t/' + key + '?s=320') && imgs.includes('https://myriagent.local/m/' + key) && app.csp.length === 0,
    { imgs, csp: app.csp });
  await HP.closePage(app);
}

// ───── 步 12：長工作門檻（S6、D39）與兩種寬度的截圖 ─────

const BIG = LF.convId(31);
const BIG_TITLE = '大對話（80 項、約 1 MB）';
const BIG_ITEMS = LF.bigItems(NOW);
const BIG_BYTES = BIG_ITEMS.reduce((n, it) => n + Buffer.byteLength(it.html, 'utf8'), 0);
/** 長工作量測的回合數（桌面與瀏覽器交替，各取中位數比，降低機器忙時的雜訊） */
const LONG_TASK_ROUNDS = 3;

/** 從現在起記長工作（PerformanceObserver longtask）；之前的不算 */
const startLongTasks = (app) => app.page.evaluate(() => {
  if (window.__ltObs) window.__ltObs.disconnect();
  window.__lt = [];
  window.__ltObs = new PerformanceObserver((l) => { for (const e of l.getEntries()) window.__lt.push(Math.round(e.duration)); });
  window.__ltObs.observe({ type: 'longtask' });
});
/** 記到的長工作（>50 ms 的次數）、整頁的元素數、大對話畫出的項目數、串流項目畫完了沒 */
const readLongTasks = (app) => app.page.evaluate((cid) => {
  const root = document.querySelector('.conv[data-conv="' + cid + '"]');
  const s = root && root.querySelector('.it[data-id="stream1"]');
  return {
    durations: window.__lt.slice(), over50: window.__lt.filter((d) => d > 50).length, domNodes: document.getElementsByTagName('*').length,
    items: root ? root.querySelectorAll('.items > .it').length : -1, streamDone: !!s && s.textContent.includes('串流完成') && s.querySelectorAll('.code').length === 1,
  };
}, BIG);

/** 第 i 次（1～200）串流更新的同一則回覆：同 probe.mjs 的 streamingNoLongTasks（越來越長，61 次起有程式碼區塊、121 次起有表格），最後一次是 Done */
function streamItem(i) {
  const para = '<p>串流測試：把縮圖產生搬到背景執行緒，<code>ThumbnailService</code> 改成佇列，並加上 LRU 快取。</p>';
  const html = para.repeat(Math.ceil(i / 10)) + (i > 60 ? '<pre><code class="language-csharp">var cache = new LruCache&lt;string, Bitmap&gt;(64 * 1024 * 1024);\n</code></pre>' : '')
    + (i > 120 ? '<table><thead><tr><th>項目</th><th>前</th><th>後</th></tr></thead><tbody><tr><td>首次載入</td><td>2.1 秒</td><td>0.3 秒</td></tr></tbody></table>' : '')
    + (i === 200 ? '<p>串流完成</p>' : '');
  return { id: 'stream1', kind: 'Assistant', status: i < 200 ? 'Running' : 'Done', time: NOW, seq: BIG_ITEMS.length + 1, html };
}

/** 一個頁面的量測：送大對話（open 已經送出、還沒回）→ 畫完 80 項；再送 200 次串流更新 → 畫完。send.load()、send.stream(i) 由模式決定 */
async function measureLongTasks(app, send) {
  await startLongTasks(app);
  const t0 = Date.now();
  await send.load();
  const loaded = await until(async () => { const r = await readLongTasks(app); return r.items === BIG_ITEMS.length ? r : null; }, 20000, 50);
  const loadMs = Date.now() - t0;
  await sleep(600);
  const load = Object.assign(await readLongTasks(app), { ms: loadMs, ok: !!loaded });
  await startLongTasks(app);
  const t1 = Date.now();
  await send.stream();
  const done = await until(async () => { const r = await readLongTasks(app); return r.streamDone ? r : null; }, 20000, 50);
  const streamMs = Date.now() - t1;
  await sleep(400);
  const stream = Object.assign(await readLongTasks(app), { ms: streamMs, ok: !!done });
  delete load.streamDone;
  delete stream.items;
  return { load, stream };
}

/**
 * 宿主訊息在頁面自己的工作裡送達、在那個工作裡才解析 JSON（setTimeout 0 ＋ JSON.parse，像 WebView2 以 JSON 送來的 message 事件；
 * 瀏覽器版的 SignalR 也在收到的那個工作裡解析）：host.send 的 page.evaluate 是 DevTools 協定驅動的工作，Long Tasks API 看不到它
 * （同一份大對話以 Chrome 追蹤量，主執行緒一樣是「訊息 ~60 ms＋樣式與版面 ~78 ms」兩個工作，觀察者卻只回報一個），直接用它當基線會讓桌面少算
 */
const sendInPageTask = (app, list) => app.page.evaluate((texts) => { for (const s of texts) setTimeout(() => window.__host(JSON.parse(s)), 0); }, list.map((m) => JSON.stringify(m)));

/** 桌面基線：同一份大對話與串流，經最小的 WebView2 模擬（已配對的桌面；open 送出之後才由探針送 conversation） */
async function longTasksDesktop(round) {
  const init = LF.liveDesktopInit(NOW, {
    layout: desktopLayout([BIG]),
    conversations: [{ id: BIG, accountId: 'a1', provider: 'Claude', title: BIG_TITLE, group: '今天', updatedAt: NOW - LF.MIN, workingDirectory: 'C:\\repo\\demo', customFolder: true,
      running: false, waiting: false, unread: false }],
  });
  const app = await H.openDesktop('long-desktop-' + round, { init });
  await until(() => (postedOf(app, 'open').some((m) => m.conversationId === BIG) ? true : null), 5000);
  const r = await measureLongTasks(app, {
    load: () => sendInPageTask(app, [LF.localMessage(NOW, BIG, BIG_TITLE, { items: BIG_ITEMS, conversationSync: null })]),
    stream: () => sendInPageTask(app, Array.from({ length: 200 }, (_, k) => ({ type: 'item', conversationId: BIG, item: streamItem(k + 1) }))),
  });
  if (round === 1) await shot(app, 'long-tasks-desktop-1440');
  await HP.closePage(app);
  return r;
}

/** 瀏覽器版：真的 transport-hub.js 收假 hub 推的同一份大對話與 200 則 item（探針的訊息記錄只記種類，不複製大訊息） */
async function longTasksHub(round) {
  const app = await H.openHub('long-hub-' + round, {
    init: (ready) => LF.liveBrowserInit(NOW, { mode: ready && ready.mode, layout: layoutOf([[[BIG], BIG]]), conversations: [LF.summary(NOW, BIG, BIG_TITLE)] }),
    initOpts: { lightMessages: true },
  });
  await HP.waitLive(app);
  await until(() => (app.hub.cmds('open').some((c) => c.cmd.conversationId === BIG) ? true : null), 5000);
  const r = await measureLongTasks(app, {
    load: async () => { app.hub.push(LF.conversationMessage(NOW, BIG, BIG_TITLE, { items: BIG_ITEMS })); },
    stream: async () => { for (let i = 1; i <= 200; i++) app.hub.push({ type: 'item', conversationId: BIG, item: streamItem(i) }); },
  });
  if (round === 1) await shot(app, 'long-tasks-hub-1440');
  await HP.closePage(app);
  return r;
}

const median = (list) => {
  const s = list.slice().sort((a, b) => a - b);
  return s.length ? s[Math.floor((s.length - 1) / 2)] : null;
};

async function runLongTasks() {
  const desktop = [];
  const hub = [];
  for (let round = 1; round <= LONG_TASK_ROUNDS; round++) {
    desktop.push(await longTasksDesktop(round));
    hub.push(await longTasksHub(round));
  }
  const sum = (list, phase) => ({
    over50: list.map((r) => r[phase].over50), median: median(list.map((r) => r[phase].over50)), ms: list.map((r) => r[phase].ms),
    durations: list.map((r) => r[phase].durations), ok: list.every((r) => r[phase].ok),
  });
  const s = { desktop: { load: sum(desktop, 'load'), stream: sum(desktop, 'stream') }, hub: { load: sum(hub, 'load'), stream: sum(hub, 'stream') } };
  check('hubLongTasks', s.desktop.load.ok && s.desktop.stream.ok && s.hub.load.ok && s.hub.stream.ok
    && s.hub.load.median <= s.desktop.load.median && s.hub.stream.median <= s.desktop.stream.median,
  { items: BIG_ITEMS.length, htmlBytes: BIG_BYTES, rounds: LONG_TASK_ROUNDS, desktop: s.desktop, hub: s.hub,
    domNodes: { desktop: desktop.map((r) => r.load.domNodes), hub: hub.map((r) => r.load.domNodes), hubAfterStream: hub.map((r) => r.stream.domNodes) } });
}

/** 分批載入的一批（liverules.LOAD_CHUNK） */
const LOAD_CHUNK = 20;
const BIG_IDS = BIG_ITEMS.map((it) => it.id);
const OLDER_ITEMS = [1, 2, 3].map((k) => ({ id: 'old0' + k, kind: k % 2 ? 'User' : 'Assistant', status: 'Done', time: NOW - (200 - k) * LF.MIN, html: '<p>較早的第 ' + k + ' 則</p>',
  attachments: k % 2 ? [] : undefined }));
/** 大對話畫面上的項目 id（依 DOM 順序） */
const bigOrder = (app) => app.page.evaluate((cid) => Array.from(document.querySelectorAll('.conv[data-conv="' + cid + '"] .items > .it')).map((n) => n.dataset.id), BIG);

/**
 * 在頁面裡、app.js 之後註冊的傳輸層訂閱者（同一則訊息 app.js 先處理完才輪到它）：收到大對話的 conversation 當下的項目 id、
 * big001 的 item 送到時已畫幾項；o.clickOlder 時收到 conversation 就同步按「載入較早的訊息」（分批還沒補完），記按下後畫了幾項
 */
const watchChunks = (app, o = {}) => app.page.evaluate(([cid, clickOlder]) => {
  const q = () => Array.from(document.querySelectorAll('.conv[data-conv="' + cid + '"] .items > .it')).map((n) => n.dataset.id);
  window.__chunk = { first: null, atItem: null, afterClick: null };
  window.myriagentHubTransport.onMessage((m) => {
    if (!m || m.conversationId !== cid) return;
    if (m.type === 'conversation' && !window.__chunk.first) {
      window.__chunk.first = q();
      if (clickOlder) {
        const b = document.querySelector('.conv[data-conv="' + cid + '"] .older-btn');
        if (b) b.click();
        window.__chunk.afterClick = q().length;
      }
    }
    if (m.type === 'item' && m.item && m.item.id === 'big001' && window.__chunk.atItem === null) window.__chunk.atItem = q().length;
  });
}, [BIG, !!o.clickOlder]);

/** 開一頁瀏覽器版、只開著大對話（open 已送出、還沒回）；onCmd 由呼叫端設 */
async function openBigHub(name) {
  const app = await H.openHub(name, {
    init: (ready) => LF.liveBrowserInit(NOW, { mode: ready && ready.mode, layout: layoutOf([[[BIG], BIG]]), conversations: [LF.summary(NOW, BIG, BIG_TITLE)] }),
    initOpts: { lightMessages: true },
  });
  await HP.waitLive(app);
  await until(() => (app.hub.cmds('open').some((c) => c.cmd.conversationId === BIG) ? true : null), 5000);
  return app;
}

/**
 * 瀏覽器版分批載入（D39）：收到 conversation 的那個工作只畫最後一批（≤ LOAD_CHUNK），之後往前補完、順序和訊息一樣、貼底；
 * 分批中送到的 item（big001）照常套用（先補完再改）；分批中按「載入較早的訊息」先補完，beforeId 仍是第一項，回來的較早項目接在最前面
 */
async function runLoadChunked() {
  const MARK = '分批中更新';
  // 一、順序與分批中的 item：兩則訊息緊接著推，item 在分批還沒補完時送到（三次內有一次即可；機器忙時可能剛好補完）
  let a = null;
  for (let attempt = 1; attempt <= 3 && !(a && a.atItem !== null && a.atItem < BIG_IDS.length); attempt++) {
    const app = await openBigHub('chunk-order-' + attempt);
    await watchChunks(app);
    app.hub.push(LF.conversationMessage(NOW, BIG, BIG_TITLE, { items: BIG_ITEMS }));
    app.hub.push({ type: 'item', conversationId: BIG, item: Object.assign({}, BIG_ITEMS[0], { html: BIG_ITEMS[0].html + '<p>' + MARK + '</p>' }) });
    const done = await until(() => app.page.evaluate(([cid, mark]) => {
      const root = document.querySelector('.conv[data-conv="' + cid + '"]');
      const n = root && root.querySelectorAll('.items > .it[data-id="big001"]');
      return n && n.length === 1 && n[0].textContent.includes(mark) && root.querySelectorAll('.items > .it').length === 80 ? true : null;
    }, [BIG, MARK]), 10000, 50);
    await sleep(300);
    const order = await bigOrder(app);
    const info = await app.page.evaluate((cid) => {
      const sc = document.querySelector('.conv[data-conv="' + cid + '"] .scroller');
      return { chunk: window.__chunk, big001: document.querySelectorAll('.conv[data-conv="' + cid + '"] .it[data-id="big001"]').length,
        atBottom: !!sc && sc.scrollHeight - sc.scrollTop - sc.clientHeight <= 4 };
    }, BIG);
    a = { attempt, done: !!done, first: info.chunk.first, atItem: info.chunk.atItem, big001: info.big001, atBottom: info.atBottom, orderOk: JSON.stringify(order) === JSON.stringify(BIG_IDS) };
    if (attempt === 1) await shot(app, 'chunked-load-1440');
    await HP.closePage(app);
  }
  // 二、分批中按「載入較早的訊息」（hasMore）：先補完、beforeId 是第一項、較早的接在最前面
  const app = await openBigHub('chunk-older');
  app.hub.onCmd('loadOlder', (cmd) => ({ push: [{ type: 'olderItems', conversationId: cmd.conversationId, items: OLDER_ITEMS, hasMore: false }] }));
  await watchChunks(app, { clickOlder: true });
  app.hub.push(LF.conversationMessage(NOW, BIG, BIG_TITLE, { items: BIG_ITEMS, hasMore: true }));
  const older = await until(() => app.hub.cmds('loadOlder')[0] || null, 5000);
  const want = OLDER_ITEMS.map((it) => it.id).concat(BIG_IDS);
  const final = await until(async () => { const o = await bigOrder(app); return o.length === want.length ? o : null; }, 5000, 50);
  const b = await app.page.evaluate((cid) => ({ chunk: window.__chunk, olderHidden: document.querySelector('.conv[data-conv="' + cid + '"] .older').hidden }), BIG);
  await HP.closePage(app);
  const firstOk = (f) => Array.isArray(f) && f.length > 0 && f.length <= LOAD_CHUNK && JSON.stringify(f) === JSON.stringify(BIG_IDS.slice(-f.length));
  const olderOk = !!older && older.cmd.beforeId === BIG_IDS[0] && b.chunk.afterClick === BIG_IDS.length && firstOk(b.chunk.first)
    && !!final && JSON.stringify(final) === JSON.stringify(want) && b.olderHidden === true;
  check('hubLoadChunkedOrder', !!a && a.done && firstOk(a.first) && a.orderOk && a.big001 === 1 && a.atItem !== null && a.atItem < BIG_IDS.length && a.atBottom && olderOk,
    { order: a && { attempt: a.attempt, updated: a.done, firstChunk: a.first && a.first.length, firstIds: a.first && [a.first[0], a.first.at(-1)], orderOk: a.orderOk, big001: a.big001, itemArrivedAt: a.atItem, atBottom: a.atBottom },
      older: { beforeId: older && older.cmd.beforeId, firstChunk: b.chunk.first && b.chunk.first.length, afterClick: b.chunk.afterClick, final: final && [final[0], final[3], final.at(-1), final.length], olderHidden: b.olderHidden } });
}

/**
 * 兩種寬度截圖的水平捲動：文件與 body 的 scrollWidth 不超過 clientWidth；body 底下（含對話框、燈箱、提示）沒有超出視窗右緣的元素。
 * position:fixed 的元素本身不算；在可以橫向捲動的容器（overflow-x auto／scroll，例如分頁列）裡、而且容器本身在視窗內的不算（捲得到）
 */
const screenScroll = (app) => app.page.evaluate(() => {
  const w = document.documentElement.clientWidth;
  const scrolls = (el) => { const x = getComputedStyle(el).overflowX; return x === 'auto' || x === 'scroll'; };
  const wide = [];
  for (const el of document.body.querySelectorAll('*')) {
    const r = el.getBoundingClientRect();
    if (r.width <= 0 || r.height <= 0 || r.right <= w + 1 || getComputedStyle(el).position === 'fixed') continue;
    let p = el.parentElement;
    let reachable = false;
    while (p && p !== document.body) {
      if (scrolls(p) && p.getBoundingClientRect().right <= w + 1) { reachable = true; break; }
      p = p.parentElement;
    }
    if (!reachable) wide.push(el.tagName.toLowerCase() + '.' + String(el.className).split(' ').filter(Boolean).join('.'));
    if (wide.length >= 5) break;
  }
  return { docScroll: document.documentElement.scrollWidth, docClient: w, bodyScroll: document.body.scrollWidth, bodyClient: document.body.clientWidth, wide };
});
const screenOk = (x) => x.docScroll <= x.docClient && x.bodyScroll <= x.bodyClient && x.wide.length === 0;

/** 側欄某一列的未讀點 */
const unreadDot = (app, id) => app.page.evaluate((cid) => {
  const row = document.querySelector('.sb-list .crow[data-key="c:' + cid + '"]');
  const dot = row && row.querySelector('.crow-state .dot-unread');
  if (!dot) return false;
  const r = dot.getBoundingClientRect();
  return r.width > 0 && r.right <= window.innerWidth;
}, id);
const lightboxOpen = (app) => app.page.evaluate(() => !!document.querySelector('.lb'));

/** 窄螢幕的側欄是蓋在工作區上的抽屜：要看聊天時收起來、看清單時打開 */
async function setSidebar(app, open) {
  // 手機版收起時側欄不拿掉（hidden），往左滑出畫面（app.css）：看 #app 的 sidebar-collapsed，等滑完（0.3 秒）
  const shown = await app.page.evaluate(() => { const s = document.getElementById('sidebar'); return !!s && !s.hidden && getComputedStyle(s).display !== 'none' && !document.getElementById('app').classList.contains('sidebar-collapsed'); });
  if (shown !== open) await app.page.click('#btnSidebar');
  await sleep(350);
}

/**
 * 一種寬度的所有畫面：頂部列的版號、R4（執行中、電腦離線、伺服器的狀態文字＋政策 View 的閘門）、queueMore、未讀、草稿的電腦晶片、預設密碼警告、燈箱、
 * 剪貼簿退路框、build 橫幅（版號換成警告色調）、選電腦；每個畫面先確認真的出現了（shown）才截圖，再量水平捲動
 */
async function screensAt(vp, results) {
  const narrow = vp.w < 720;
  const tag = String(vp.w);
  const tabs = [LF.CONV.running, LF.CONV.offline, LF.CONV.view, LF.CONV.idle, LF.DRAFT];
  const app = await openWithLayout('screens-' + tag, layoutOf([[tabs, LF.CONV.running]], { sidebarCollapsed: narrow }), {
    initOpts: { build: 'aaaa111', commitAt: COMMIT_AT },
    serve: {
      header: (id) => (id === LF.CONV.running ? { running: true } : id === LF.CONV.view ? { status: '等待電腦重新上傳' } : null),
      draft: () => ({ deviceId: LF.DEVICE.view }),
    },
    open: { vp, initOpts: { clipboard: 'deny' } },
  });
  const { page, hub } = app;
  hub.onCmd('copyItem', (cmd) => ({ push: [{ type: 'clipboard', conversationId: cmd.conversationId, itemId: cmd.itemId, text: CLIP_TEXT }] }));
  await waitLoaded(app, tabs);
  if (narrow) await setSidebar(app, false);
  const snap = async (name, shown, detail) => {
    await sleep(200);
    await shot(app, 'hub-screen-' + name + '-' + tag);
    const sc = await screenScroll(app);
    results.push({ name, width: vp.w, shown: !!shown, scroll: screenOk(sc), detail: shown ? undefined : detail, sc: screenOk(sc) ? undefined : sc });
  };
  const tab = async (id) => {
    await page.click('.tab[data-key="' + id + '"]');
    return until(async () => { const s = await convState(app, id); return s && s.shown ? s : null; }, 3000);
  };

  // 品牌名旁的版號：這個網頁的伺服器 build，前面是它的日期時間；是「版本與更新」頁的連結、開在新分頁。
  // 手機寬度省略「網頁版」前綴與 build（提示與讀屏照樣有），日期與時刻疊成兩行；頂部列不得水平捲動（snap 量）
  const ver = await versionBadge(app);
  await snap('version-badge', !!ver && ver.shown && ver.pre === '網頁版' && ver.preShown === !narrow && ver.text === 'aaaa111' && ver.tone === 'plain'
    && ver.time === '10/23 14:05' && ver.timeShown && ver.numShown === !narrow && ver.stacked === narrow
    && ver.tag === 'a' && ver.href === '/#releases' && ver.target === '_blank' && ver.rel === 'noopener'
    && ver.title.startsWith(`Myriagent Hub 0.2.0（aaaa111）\n更新於 ${COMMIT_FULL}\n這個網頁與伺服器是同一個版本`) && ver.label === `網頁版 build aaaa111（${COMMIT_FULL}）`, ver);

  // R4：所在電腦、在線狀態與伺服器的狀態文字；電腦離線、政策不允許時送出停用並說明
  let s = await tab(LF.CONV.running);
  await snap('r4-running', !!s && s.run === '執行指令：npm test' && s.spin && !!s.device && s.device.online === 'true', s);
  s = await tab(LF.CONV.offline);
  let c = await composerState(app, LF.CONV.offline);
  await snap('r4-offline', !!s && s.run === '電腦離線' && !s.spin && !!c && c.sendDisabled && !!c.gate && c.gate.includes('重新上線後才能送出'), { s, c });
  s = await tab(LF.CONV.view);
  c = await composerState(app, LF.CONV.view);
  await snap('r4-status-policy-view', !!s && s.run === '等待電腦重新上傳' && !s.spin && !!c && c.gate === '在 HOME-PC 的設定中允許遠端操作', { s, c });
  s = await tab(LF.CONV.idle);
  c = await composerState(app, LF.CONV.idle);
  await snap('queue-more', !!c && c.more === '還有 3 則在佇列中' && c.moreButtons === 0, c);
  // 未讀：窄螢幕打開側欄抽屜
  if (narrow) await setSidebar(app, true);
  await snap('unread', await unreadDot(app, LF.CONV.idle));
  if (narrow) await setSidebar(app, false);
  await tab(LF.DRAFT);
  const chip = await draftChip(app, LF.DRAFT);
  await snap('draft-device-chip', !!chip && chip.shown && chip.text === '電腦：HOME-PC', chip);

  // 預設密碼的常駐警告（之後拿掉，免得蓋到其他畫面）
  await tab(LF.CONV.idle);
  hub.push(LF.controlMessage({ defaultPassword: true }));
  const banner = await until(() => noticeOf(app, 'control'), 3000);
  await snap('default-password', !!banner && banner.text === DEFAULT_PASSWORD_TEXT, banner);
  hub.push(LF.controlMessage());
  await until(async () => ((await noticeOf(app, 'control')) ? null : true), 3000);

  // 燈箱（Assistant 項目的圖片）
  await page.click(itemSel(LF.CONV.idle, 'i2') + ' .media .mimg');
  await snap('lightbox', await until(() => lightboxOpen(app), 3000));
  await page.keyboard.press('Escape');
  await until(async () => ((await lightboxOpen(app)) ? null : true), 3000);

  // 剪貼簿被拒時的退路框
  await clickCopy(app);
  const fb = await until(() => fallbackState(app), 4000);
  await snap('copy-fallback', !!fb && fb.value === CLIP_TEXT && fb.readOnly, fb && { readOnly: fb.readOnly, same: fb.value === CLIP_TEXT });
  if (fb) await page.click('.dlg-copy-fallback .dlg-foot button');
  await until(async () => ((await anyDialog(app)) === 0 ? true : null), 2000);

  // 步 11：上傳中的晶片（送出停用並說明）、失敗的晶片（原因逐字）、「瀏覽資料夾…」的挑選器
  await tab(LF.CONV.idle);
  let releaseUp;
  const holdUp = new Promise((r) => { releaseUp = r; });
  app.api.uploads({ hold: holdUp });
  serveAddUploaded(hub);
  await chooseFiles(app, LF.CONV.idle, [fileOf('畫面截圖.png', PNG_BYTES, 'image/png'), fileOf('需求說明.txt', '需求')]);
  const upl = await untilChips(app, LF.CONV.idle, allState('uploading', 2), 3000);
  await snap('upload-progress', !!upl && upl.gate === UPLOADING_TEXT && upl.sendDisabled, upl);
  releaseUp();
  await untilChips(app, LF.CONV.idle, allState('attached', 2));
  app.expect((m) => /Failed to load resource/.test(m.text) && /status of 507/.test(m.text) && /\/api\/v1\/uploads\//.test(m.url), '截圖：上傳端點刻意回 507');
  app.api.uploads({ status: 507 });
  await chooseFiles(app, LF.CONV.idle, [fileOf('空間不足時的檔案.txt', '507')]);
  const uerr = await untilChips(app, LF.CONV.idle, (s) => s.chips.some((c) => c.state === 'error'), 4000);
  await snap('upload-error', !!uerr && uerr.chips.find((c) => c.state === 'error').sub === '伺服器的空間不足或待處理的上傳太多，請稍後再試', uerr);
  serveListFiles(hub);
  await page.click(convSel(LF.CONV.idle) + ' .ch-folder');
  await page.waitForSelector('.menu');
  await clickMenu(app, '瀏覽資料夾…');
  await until(async () => { const d = await drawerState(app, LF.CONV.idle); return d && d.open && d.names.length === 3 ? d : null; }, 3000);
  await clickEntry(app, LF.CONV.idle, 'C:\\repo\\alpha');
  const dp = await until(async () => { const d = await drawerState(app, LF.CONV.idle); return d && d.pickPath === 'C:\\repo\\alpha' && d.names.length === 2 ? d : null; }, 3000);
  await snap('dirs-picker', !!dp && dp.picking && dp.bar && !dp.pickDisabled, dp);
  await page.click(convSel(LF.CONV.idle) + ' [data-field="pickCancel"]');
  await until(async () => { const d = await drawerState(app, LF.CONV.idle); return d && !d.open ? d : null; }, 2000);

  // build 橫幅（有沒送出的字，不自動重新載入）
  await typeInto(app, LF.CONV.idle, '還沒送出的字');
  hub.push(LF.liveBrowserInit(NOW, { mode: 'main', build: 'bbbb222' }));
  const build = await until(() => noticeOf(app, 'build'), 3000);
  await sleep(600);
  // 版號仍是這個網頁的 build 與時間、換成警告色調，提示寫出伺服器新的 build（新的 init 沒有時間：那一行不帶時間）
  const verChanged = await versionBadge(app);
  await snap('build-banner', !!build && build.text === BUILD_TEXT && reloads(app) === 0
    && !!verChanged && verChanged.shown && verChanged.text === 'aaaa111' && verChanged.time === '10/23 14:05' && verChanged.tone === 'warn'
    && verChanged.title.includes('伺服器已換成 build bbbb222，') && verChanged.label === `網頁版 build aaaa111（${COMMIT_FULL}），伺服器已換版`,
  { build, reloads: reloads(app), ver: verChanged });
  await HP.closePage(app);

  // 選電腦（這個帳戶有兩台在線的候選）
  const pick = await openWithLayout('screens-pick-' + tag, layoutOf([[[], null]], { sidebarCollapsed: narrow }), { initOpts: twoOnline(), open: { vp } });
  if (narrow) await setSidebar(pick, false);
  await pick.page.keyboard.press('Control+n');
  const names = await until(() => pickDialog(pick), 3000);
  await sleep(200);
  await shot(pick, 'hub-screen-pick-device-' + tag);
  const sc = await screenScroll(pick);
  results.push({ name: 'pick-device', width: vp.w, shown: !!names && names.length === 2, scroll: screenOk(sc), sc: screenOk(sc) ? undefined : sc });
  await HP.closePage(pick);
}

/** 各畫面在 1440 與 390 的名稱（上傳進度與錯誤、資料夾挑選器是步 11 的） */
const SCREEN_NAMES = ['version-badge', 'r4-running','r4-offline', 'r4-status-policy-view', 'queue-more', 'unread', 'draft-device-chip', 'default-password', 'lightbox', 'copy-fallback',
  'upload-progress', 'upload-error', 'dirs-picker', 'build-banner', 'pick-device'];

async function runHubScreens() {
  const results = [];
  for (const vp of [{ w: 1440, h: 900 }, { w: 390, h: 844 }]) await screensAt(vp, results);
  const missing = [1440, 390].flatMap((w) => SCREEN_NAMES.filter((n) => !results.some((r) => r.name === n && r.width === w)).map((n) => n + '@' + w));
  const bad = results.filter((r) => !r.shown || !r.scroll);
  check('hubScreens', missing.length === 0 && bad.length === 0 && results.length === SCREEN_NAMES.length * 2,
    { screens: results.length, missing, bad, names: results.map((r) => r.name + '@' + r.width) });
}

// ───── 合併前審查（WP4.6）的回歸：每一項拿掉對應的修正都會失敗 ─────

const LIST_UNKNOWN_TEXT = '沒有收到那台電腦的回覆，請按重新整理再試一次';
const FILE_REF_UNKNOWN_TEXT = '連線中斷，結果不明，請關閉後再試一次';
const SEARCH_DRAFT_TEXT = '送出第一則訊息後才能搜尋檔案';
const SEARCH_REJECTED_TEXT = '現在不能搜尋這台電腦上的檔案';
const SEARCH_UNKNOWN_TEXT = '沒有收到那台電腦的回覆，請再輸入一次';
/** 頁面在換頁中（自動重新載入）時 evaluate 會丟例外：當成讀不到 */
const quiet = (p) => p.catch(() => null);

/** 卡片的說明欄（核准卡「告訴代理改怎麼做」、計畫卡「要調整的地方」）：填字並送 input 事件；讀回目前的字 */
const setCardText = (app, convId, itemId, text) => app.page.evaluate(([sel, t]) => {
  const el = document.querySelector(sel + ' input.ap-tell');
  if (!el) return false;
  el.value = t;
  el.dispatchEvent(new Event('input', { bubbles: true }));
  return true;
}, [cardSel(convId, itemId), text]);
const cardText = (app, convId, itemId) => app.page.evaluate((sel) => { const el = document.querySelector(sel + ' input.ap-tell'); return el ? el.value : null; }, cardSel(convId, itemId));

async function runReviewCardGateUnlisted() {
  // 清單外的對話（keepOnRestore 保留的舊分頁）：第一次載入建卡片時還沒有標頭、也查不到摘要；標頭到了要照它的電腦重套卡片閘門（WP46-XSS-RO-1）
  const OFF = LF.convId(7);
  const devOf = { [LF.CONV.unlisted]: LF.DEVICE.view, [OFF]: LF.DEVICE.offline };
  const app = await openWithLayout('review-card-gate-unlisted', layoutOf([[[LF.CONV.unlisted], LF.CONV.unlisted], [[OFF], OFF]]),
    { serve: { header: (id) => (devOf[id] ? { deviceId: devOf[id] } : null) } });
  const loaded = await waitLoaded(app, [LF.CONV.unlisted, OFF]);
  await sleep(300);
  const view = await cardState(app, LF.CONV.unlisted, 'i6');
  const plan = await cardState(app, LF.CONV.unlisted, 'i5');
  const off = await cardState(app, OFF, 'i6');
  check('hubCardGateUnlistedOnLoad', !!loaded && !!view && view.disabled && view.gate === '在 HOME-PC 的設定中允許遠端操作' && !!plan && plan.disabled
    && !!off && off.disabled && off.gate === '電腦離線：筆電 ' + LF.XSS + ' 重新上線後才能回應' && (await HP.xssState(app)).xss === 0,
  { view, plan, off });
  await HP.closePage(app);
}

/**
 * 手機（390×844、觸控）執行中往上滑看歷史（2026-10-25 使用者回報：對話正在執行時往上滑、載入歷史資料時畫面還是閃爍卡頓）。
 * 以 overflow-anchor: none 模擬 iOS Safari 26 以前沒有捲動錨定：一邊串流（最後一則一直長、標頭的狀態一直換、隔一陣子多一張工具卡）、
 * 一邊用手指往上滑到頂、自動載入較早的訊息；中間有單張圖片（.mgrid.n1，延遲載入）與回覆裡的 Markdown 圖片。
 * 每次滑完、慣性停下之後不碰螢幕看 800ms：看著的那一則（畫面上緣往下 200px 的項目）不能自己移動
 */
/** 以 overflow-anchor: none 模擬沒有捲動錨定的瀏覽器（iOS Safari 26 以前）：網頁載入前就生效（對話的 ScrollAnchor 第一次排版時就看得到） */
const NO_SCROLL_ANCHOR = () => {
  document.addEventListener('DOMContentLoaded', () => {
    const st = document.createElement('style');
    st.textContent = '.scroller { overflow-anchor: none !important; }';
    document.head.append(st);
  });
};

async function runMobileRunningScroll() {
  const id = LF.CONV.idle;
  const img = (k) => '/m/img' + k;
  const items = Array.from({ length: 40 }, (_, k) => {
    const it = { id: 'mr' + String(k).padStart(2, '0'), kind: k % 2 ? 'Assistant' : 'User', status: 'Done', time: NOW - (200 - k) * LF.MIN,
      html: '<p>' + ('第 ' + k + ' 則，執行中往上滑看歷史。').repeat(k % 2 ? 6 : 1) + '</p>', attachments: [] };
    if (k % 2 && k % 6 === 1) it.media = [{ url: img(k), kind: 'image', name: '截圖' + k + '.png' }];
    if (k % 2 && k % 6 === 3) it.html += '<p><img src="' + img(k) + '" alt="圖' + k + '"></p><p>圖片下面的說明。</p>';
    return it;
  });
  const older = Array.from({ length: 12 }, (_, k) => ({ id: 'mo' + String(k).padStart(2, '0'), kind: k % 2 ? 'Assistant' : 'User', status: 'Done',
    time: NOW - (300 - k) * LF.MIN, html: '<p>' + ('較早的第 ' + k + ' 則。').repeat(k % 2 ? 6 : 1) + (k % 2 && k % 6 === 1 ? '</p><p><img src="' + img(100 + k) + '" alt="舊圖"></p>' : '</p>'), attachments: [] }));
  const app = await openWithLayout('mobile-running-scroll', layoutOf([[[id], id]]), {
    open: { vp: { w: 390, h: 844 }, hasTouch: true, initScript: NO_SCROLL_ANCHOR, media: (p) => (p.startsWith('/m/img') ? { status: 200, headers: { 'Content-Type': 'image/svg+xml', 'Cache-Control': 'no-store' }, body: BIG_IMAGE } : null),
      hub: (hub) => hub.onCmd('open', (cmd) => ({ push: [LF.conversationMessage(NOW, cmd.conversationId, TITLE_OF[cmd.conversationId] || '對話', { items, hasMore: true, header: { running: true } })] })) },
  });
  const { hub, page } = app;
  await waitLoaded(app, [id]);
  hub.onCmd('loadOlder', (cmd) => {
    setTimeout(() => hub.push({ type: 'olderItems', conversationId: id, beforeId: cmd.beforeId, items: older, hasMore: false }), 120);
    return { push: [] };
  });
  await page.click('#btnSidebar');
  await sleep(300);
  const sel = convSel(id);

  // 串流：最後一則一直長、標頭狀態一直換、每 600ms 多一張工具卡
  let streaming = true;
  let n = 0;
  const stream = (async () => {
    let text = '';
    while (streaming) {
      n++;
      text += '串流的新內容第 ' + n + ' 段。';
      hub.push({ type: 'item', conversationId: id, item: { id: 'live', kind: 'Assistant', status: 'Running', time: NOW, html: '<p>' + text + '</p>' } });
      if (n % 3 === 0) hub.push({ type: 'header', conversationId: id, header: LF.serverHeader(NOW, id, TITLE_OF[id], { running: true, status: n % 2 ? '思考中…' : '執行指令：cd /c/repo/myriagent && dotnet test Myriagent.slnx -c Release' }) });
      if (n % 8 === 0) hub.push({ type: 'item', conversationId: id, item: { id: 'tool' + n, kind: 'Tool', status: 'Done', time: NOW, toolKind: 'execute', toolName: 'Bash', title: 'ls -la', output: 'ok', html: '' } });
      await sleep(80);
    }
  })();

  // 逐影格記錄畫面上緣往下 200px 的那一則在捲動內容裡的位置（畫面位置＋scrollTop）：手指捲動不會改變它，只有上方的排版變了才會；
  // 程式自己補的 scrollTop（插入較早的項目時停在原位）另外記，扣掉之後還有的位移＝看得到的跳動
  await page.evaluate((s) => {
    const sc = document.querySelector(s + ' .scroller');
    const desc = Object.getOwnPropertyDescriptor(Element.prototype, 'scrollTop');
    const w = window.__jump = { list: [], frames: 0, stop: false, comp: 0 };
    Object.defineProperty(sc, 'scrollTop', {
      configurable: true,
      get() { return desc.get.call(this); },
      set(v) { const before = desc.get.call(this); desc.set.call(this, v); w.comp += desc.get.call(this) - before; },
    });
    let ref = null, prev = 0, compAt = 0;
    const content = () => ref.getBoundingClientRect().top + desc.get.call(sc);
    const pick = () => {
      const r = sc.getBoundingClientRect();
      let el = document.elementFromPoint(r.left + r.width / 2, r.top + 200);
      while (el && !(el.classList && el.classList.contains('it'))) el = el.parentElement;
      ref = el;
      if (ref) { prev = content(); compAt = w.comp; }
    };
    // 在 ResizeObserver 的階段量（這個觀察者比對話的 ScrollAnchor 晚建立，同一格排在它後面）：量到的是這一格補完、要畫出來的樣子。
    // 上方有東西變高變矮，捲動內容的高度一定跟著變，所以觀察 thread 就不會漏
    const measure = () => {
      if (w.stop) return;
      w.frames++;
      if (ref && ref.isConnected) {
        const c = content();
        const shift = (c - prev) - (w.comp - compAt);
        if (Math.abs(shift) > 1.5) w.list.push({ id: ref.dataset.id, shift: Math.round(shift), scrollTop: Math.round(desc.get.call(sc)) });
      }
      pick();
    };
    new ResizeObserver(measure).observe(sc.querySelector('.thread'));
  }, sel);

  // 手指慢慢往下拖（內容往上捲）：每拖 90px 停住 220ms，拖到下緣附近停住再放開（不會有慣性），再從上面重新按下
  const box = await page.evaluate((s) => { const r = document.querySelector(s + ' .scroller').getBoundingClientRect(); return { x: r.left + r.width / 2, top: r.top, bottom: r.bottom }; }, sel);
  const cdp = await page.context().newCDPSession(page);
  const pt = (y) => [{ x: Math.round(box.x), y: Math.round(y), id: 1 }];
  let gestures = 0;
  for (; gestures < 40; gestures++) {
    let y = box.top + 60;
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: pt(y) });
    while (y + 90 < box.bottom - 40) {
      for (let k = 0; k < 6; k++) { y += 15; await cdp.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: pt(y) }); await sleep(16); }
      await sleep(220);
    }
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
    await sleep(250);
    const done = await page.evaluate((s) => { const sc = document.querySelector(s + ' .scroller'); return sc.scrollTop < 5 && !!document.querySelector(s + ' .it[data-id="mo00"]'); }, sel);
    if (done) break;
  }
  await cdp.detach().catch(() => {});
  streaming = false;
  await stream;
  const jumps = await page.evaluate(() => { window.__jump.stop = true; return { list: window.__jump.list, frames: window.__jump.frames }; });
  const olderIn = await page.evaluate((s) => !!document.querySelector(s + ' .it[data-id="mo00"]'), sel);
  check('hubMobileRunningSteady', gestures > 3 && jumps.frames > 100 && jumps.list.length === 0 && olderIn,
    { gestures, frames: jumps.frames, jumps: jumps.list.length, first: jumps.list.slice(0, 12), olderIn });
  await HP.closePage(app);
}

/**
 * 手機（390×844、觸控）往上滑看歷史（2026-10-25 使用者回報：iPhone 上往上滑一直閃、不順暢；畫面上下都被截掉）：
 * 一、觸控裝置的項目照實際高度排版（不用 content-visibility：沒有捲動錨定時，畫面外的項目換成真的高度會讓看著的內容跳動）；
 * 二、還在最底附近（離底不到 80px，捲動事件還不會停止貼底）時手指往下拖：照樣停止自動貼底，串流再長也不被拉回最底；
 * 三、手指按著時到的較早一頁先不插（畫面不跳），放開、停下來才插，看著的那一則停在原位；
 * 四、安全區域（上 47、下 34，iPhone 的狀態列與手勢列）：整個 app 在安全區域裡，最上面那排與最下面那行都不被蓋到
 */
async function runMobileScrollSteady() {
  const id = LF.CONV.idle;
  const items = Array.from({ length: 30 }, (_, k) => ({ id: 'ms' + String(k).padStart(2, '0'), kind: k % 2 ? 'Assistant' : 'User', status: 'Done',
    time: NOW - (60 - k) * LF.MIN, html: '<p>' + ('第 ' + k + ' 則，往上滑看歷史。').repeat(k % 2 ? 24 : 2) + '</p>', attachments: [] }));
  const app = await openWithLayout('mobile-scroll', layoutOf([[[id], id]]), {
    open: { vp: { w: 390, h: 844 }, hasTouch: true,
      // 載入時有沒有出現過先不排版的項目（it-lazy）
      initScript: () => {
        window.__sawLazy = 0;
        new MutationObserver((rs) => { for (const r of rs) for (const n of r.addedNodes) if (n.nodeType === 1 && n.classList.contains('it-lazy')) window.__sawLazy++; })
          .observe(document, { childList: true, subtree: true });
      },
      hub: (hub) => hub.onCmd('open', (cmd) => ({ push: [LF.conversationMessage(NOW, cmd.conversationId, TITLE_OF[cmd.conversationId] || '對話', { items, hasMore: true })] })) },
  });
  const { hub, page } = app;
  await waitLoaded(app, [id]);
  hub.onCmd('loadOlder', () => ({ push: [] })); // 接受、不回：探針自己決定什麼時候送
  await page.click('#btnSidebar'); // 窄螢幕的側欄是蓋在工作區上的抽屜，先收起來（同 runMobileComposer）
  await sleep(300);
  const sel = convSel(id);
  const order = () => page.evaluate((s) => [...document.querySelectorAll(s + ' .items > .it')].map((n) => n.dataset.id), sel);
  const scroll = () => page.evaluate((s) => {
    const sc = document.querySelector(s + ' .scroller');
    return { top: Math.round(sc.scrollTop), dist: Math.round(sc.scrollHeight - sc.scrollTop - sc.clientHeight), jump: !document.querySelector(s + ' .jump').hidden };
  }, sel);
  const topOf = (itemId) => page.evaluate(([s, iid]) => { const n = document.querySelector(s + ' .it[data-id="' + iid + '"]'); return n ? Math.round(n.getBoundingClientRect().top) : null; }, [sel, itemId]);

  // 一
  // 載入時先不排版的項目（it-lazy）畫好之後都換回實際高度：等它們換完再看
  await until(() => page.evaluate((s) => !document.querySelector(s + ' .it-lazy')), 3000);
  const cv = await page.evaluate((s) => [...document.querySelectorAll(s + ' .items > .it')].map((n) => getComputedStyle(n).contentVisibility), sel);
  const lazy = await page.evaluate((s) => document.querySelectorAll(s + ' .it-lazy').length, sel);
  check('hubMobileItemsFullHeight', cv.length === items.length && cv.every((v) => v === 'visible') && lazy === 0, { count: cv.length, values: [...new Set(cv)], lazy });
  const sawLazy = await page.evaluate(() => window.__sawLazy);
  check('hubMobileLazyLoad', sawLazy > 0, { sawLazy });

  // 二：貼底中，從畫面中間往下拖 40px、不放開（Chrome 的觸控起滑距離之後只捲動二十幾 px，離底還不到 80px）
  await until(async () => ((await scroll()).dist <= 4 ? true : null), 2000);
  const box = await page.evaluate((s) => { const r = document.querySelector(s + ' .scroller').getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; }, sel);
  const release = await swipe(page, { x: box.x, y: box.y }, { x: box.x, y: box.y + 40 }, { end: false, holdMs: 200 });
  const held = await scroll();
  hub.push({ type: 'item', conversationId: id, item: { id: 'ms99', kind: 'Assistant', status: 'Running', time: NOW, html: '<p>' + '串流中的新內容。'.repeat(60) + '</p>' } });
  await until(async () => ((await order()).includes('ms99') ? true : null), 2000);
  await sleep(300);
  const afterStream = await scroll();
  check('hubTouchUnsticks', held.dist > 4 && held.dist <= 80 && held.jump && afterStream.top === held.top && afterStream.dist > held.dist,
    { held, afterStream });

  // 三：手指還按著，較早的一頁到了：先不插；放開、停下來才插，看著的那一則停在原位
  await page.evaluate((s) => document.querySelector(s + ' .older-btn').click(), sel);
  const req = await until(() => hub.cmds('loadOlder')[0] || null, 3000);
  const anchor = (await order()).at(-3);
  const anchorTop = await topOf(anchor);
  hub.push({ type: 'olderItems', conversationId: id, beforeId: req ? req.cmd.beforeId : undefined, items: OLDER_ITEMS, hasMore: false });
  await sleep(400);
  const whileHeld = await order();
  const anchorWhileHeld = await topOf(anchor);
  await release();
  const inserted = await until(async () => { const o = await order(); return o[0] === OLDER_ITEMS[0].id ? o : null; }, 3000);
  const anchorAfter = await topOf(anchor);
  check('hubTouchOlderDeferred', !!req && !whileHeld.includes(OLDER_ITEMS[0].id) && anchorWhileHeld === anchorTop && !!inserted
    && anchorAfter !== null && Math.abs(anchorAfter - anchorTop) <= 1,
  { beforeId: req && req.cmd.beforeId, whileHeld: whileHeld.slice(0, 2), inserted: inserted && inserted.slice(0, 4), anchor, anchorTop, anchorWhileHeld, anchorAfter });

  // 四：安全區域（CDP 模擬 iPhone 的狀態列與手勢列）
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Emulation.setSafeAreaInsetsOverride', { insets: { top: 47, bottom: 34, left: 0, right: 0 } });
  await sleep(300);
  const safe = await page.evaluate((s) => {
    const r = (el) => { const b = el.getBoundingClientRect(); return { top: Math.round(b.top), bottom: Math.round(b.bottom) }; };
    const app = document.querySelector('.app');
    const ci = document.querySelector(s + ' .cinfo');
    const comp = document.querySelector(s + ' .composer') || ci;
    return { vh: innerHeight, app: r(app), cinfo: ci ? r(ci) : null, composer: comp ? r(comp) : null, first: r(app.firstElementChild) };
  }, sel);
  await shot(app, 'mobile-safe-area-390');
  // iPhone、iPad（app.js 標的 data-os="ios"）：上緣再讓 32px 避開狀態列底下的模糊漸層，下緣只留安全區域的四成（34 × .4 = 13.6）
  const ios = await page.evaluate((s) => {
    document.documentElement.dataset.os = 'ios';
    const r = (el) => { const b = el.getBoundingClientRect(); return { top: Math.round(b.top), bottom: Math.round(b.bottom) }; };
    const app = document.querySelector('.app');
    const ci = document.querySelector(s + ' .cinfo');
    const out = { vh: innerHeight, app: r(app), cinfo: ci ? r(ci) : null, first: r(app.firstElementChild) };
    delete document.documentElement.dataset.os;
    return out;
  }, sel);
  await cdp.send('Emulation.setSafeAreaInsetsOverride', { insets: { top: 0, bottom: 0, left: 0, right: 0 } }).catch(() => {});
  await cdp.detach().catch(() => {});
  check('hubMobileSafeArea', safe.app.top === 47 && safe.app.bottom === safe.vh - 34 && safe.first.top >= 47 && !!safe.cinfo && safe.cinfo.bottom <= safe.vh - 34,
    safe);
  check('hubMobileSafeAreaIos', ios.app.top === 47 + 32 && ios.first.top >= 79 && Math.abs(ios.app.bottom - (ios.vh - 13.6)) <= 1 && !!ios.cinfo && ios.cinfo.bottom <= ios.vh - 13,
    ios);
  await HP.closePage(app);
}

async function runReviewOlderStale() {
  // loadOlder 之後先到整份 conversation（重連後重新 open、伺服器主動重送）：晚到的舊頁不插進新視窗；beforeId 不是這一次請求的也不收（LIVE-2）
  const id = LF.CONV.idle;
  const app = await openWithLayout('review-older-stale', layoutOf([[[id], id]]), {
    open: { hub: (hub) => hub.onCmd('open', (cmd) => ({ push: [LF.conversationMessage(NOW, cmd.conversationId, TITLE_OF[cmd.conversationId] || '對話', { hasMore: true })] })) },
  });
  const { hub, page } = app;
  await waitLoaded(app, [id]);
  hub.onCmd('loadOlder', () => ({ push: [] })); // 接受、不回：探針自己決定送哪一頁
  const order = () => page.evaluate((sel) => [...document.querySelectorAll(sel + ' .items > .it')].map((n) => n.dataset.id), convSel(id));
  const olderShown = () => page.evaluate((sel) => !document.querySelector(sel + ' .older').hidden, convSel(id));
  const older = (beforeId) => Object.assign({ type: 'olderItems', conversationId: id }, beforeId === undefined ? {} : { beforeId }, { items: OLDER_ITEMS, hasMore: false });
  const NEW = LF.liveItems(NOW, 'n');
  const newIds = NEW.map((it) => it.id);
  // 1. 第一次 loadOlder（beforeId i1）還沒回，整份 conversation（n1…n6）先到；之後才到的舊頁（沒帶 beforeId 的舊版伺服器也一樣）不採用
  await page.click(convSel(id) + ' .older-btn');
  const l1 = await until(() => hub.cmds('loadOlder')[0] || null, 3000);
  hub.push(LF.conversationMessage(NOW, id, TITLE_OF[id], { items: NEW, hasMore: true }));
  await until(async () => (JSON.stringify(await order()) === JSON.stringify(newIds) ? true : null), 3000);
  hub.push(older(undefined));
  await sleep(500);
  const afterStale = await order();
  const shownAfterStale = await olderShown();
  // 2. 第二次 loadOlder（beforeId n1）：先到的是上一次請求的頁（beforeId i1）→ 不收；這一次的才接在最前面
  //    （舊頁被誤收時 hasMore 變成 false、按鈕藏起來：記成失敗，不等點擊逾時）
  if (shownAfterStale) await page.click(convSel(id) + ' .older-btn');
  const l2 = shownAfterStale ? await until(() => hub.cmds('loadOlder')[1] || null, 3000) : null;
  hub.push(older('i1'));
  await sleep(500);
  const afterWrong = await order();
  hub.push(older(l2 ? l2.cmd.beforeId : 'n1'));
  const want = OLDER_ITEMS.map((it) => it.id).concat(newIds);
  const final = await until(async () => { const o = await order(); return JSON.stringify(o) === JSON.stringify(want) ? o : null; }, 3000);
  const shownFinal = await olderShown();
  check('hubOlderStaleIgnored', !!l1 && l1.cmd.beforeId === 'i1' && JSON.stringify(afterStale) === JSON.stringify(newIds) && shownAfterStale === true
    && !!l2 && l2.cmd.beforeId === 'n1' && JSON.stringify(afterWrong) === JSON.stringify(newIds) && !!final && shownFinal === false,
  { first: l1 && l1.cmd.beforeId, afterStale, shownAfterStale, second: l2 && l2.cmd.beforeId, afterWrong, final: final || (await order()), shownFinal });
  await HP.closePage(app);
}

async function runClearHidesEarlier() {
  // /clear（Notice 的 noticeKind clear，liverules.clearBoundary）：最後一則清除提示之前的項目藏起來、捲到頂也不往前載入，上方的按鈕是
  // 「顯示清除前的對話」；按了照舊全部顯示（不送 loadOlder，按鈕回到「載入較早的訊息」）；之後又清除一次（item 更新）再藏
  const id = LF.CONV.idle;
  const clearItem = (cid, seq) => ({ id: cid, kind: 'Notice', status: 'Done', time: NOW - LF.MIN, noticeKind: 'clear', seq,
    html: '<p>已清除對話：代理看不到這之前的內容，接下來的訊息從新的工作階段開始。</p>' });
  const items = LF.liveItems(NOW).slice(0, 3).concat([clearItem('c1', 4),
    { id: 'a1', kind: 'User', status: 'Done', time: NOW - LF.MIN, html: '<p>清除之後的第一則</p>', attachments: [], seq: 5 },
    { id: 'a2', kind: 'Assistant', status: 'Done', time: NOW - LF.MIN, html: '<p>好的，從頭開始。</p>', seq: 6 }]);
  const app = await openWithLayout('clear-hides-earlier', layoutOf([[[id], id]]), {
    open: { hub: (hub) => hub.onCmd('open', (cmd) => ({ push: [LF.conversationMessage(NOW, cmd.conversationId, TITLE_OF[cmd.conversationId] || '對話', { items, hasMore: true })] })) },
  });
  const { hub, page } = app;
  await waitLoaded(app, [id]);
  hub.onCmd('loadOlder', () => ({ push: [] }));
  const state = () => page.evaluate((sel) => {
    const nodes = [...document.querySelectorAll(sel + ' .items > .it')];
    const row = document.querySelector(sel + ' .older');
    return {
      all: nodes.map((n) => n.dataset.id),
      shown: nodes.filter((n) => getComputedStyle(n).display !== 'none').map((n) => n.dataset.id),
      older: row && getComputedStyle(row).display !== 'none' ? row.textContent.trim() : null,
      clearStyled: !!document.querySelector(sel + ' .it[data-id="c1"].cleared .note-ic'),
    };
  }, convSel(id));
  const s1 = await until(async () => { const s = await state(); return s.all.length === items.length ? s : null; }, 3000);
  await shot(app, 'clear-hidden');
  // 捲到最上面（使用者往上捲過，IntersectionObserver 看得到上方的按鈕）：藏著的時候不自動往前載入
  await page.hover(convSel(id) + ' .scroller');
  await page.mouse.wheel(0, -3000);
  await page.evaluate((sel) => { document.querySelector(sel + ' .scroller').scrollTop = 0; }, convSel(id));
  await sleep(500);
  const autoLoads = hub.cmds('loadOlder').length;
  await page.click(convSel(id) + ' .older-btn');
  const s2 = await until(async () => { const s = await state(); return s.shown.length === items.length ? s : null; }, 3000);
  await sleep(300);
  const revealLoads = hub.cmds('loadOlder').length;
  hub.push({ type: 'item', conversationId: id, item: clearItem('c2', 7) });
  const s3 = await until(async () => { const s = await state(); return JSON.stringify(s.shown) === '["c2"]' ? s : null; }, 3000);
  check('hubClearHidesEarlier', !!s1 && JSON.stringify(s1.shown) === '["c1","a1","a2"]' && s1.older === '顯示清除前的對話' && s1.clearStyled && autoLoads === 0
    && !!s2 && s2.older === '載入較早的訊息' && revealLoads === 0 && !!s3 && s3.older === '顯示清除前的對話' && (await HP.xssState(app)).xss === 0,
  { s1, autoLoads, s2, revealLoads, s3: s3 || (await state()) });
  await HP.closePage(app);
}

async function runItemPatchStream() {
  // 〔遠端〕即時更新的增量（itemPatch，patch1；設計 2026-10-23 3.5）：同一段串流以「帶 rv 的整筆＋itemPatch」與「每次整筆」各送一份，
  // 畫面上的兩則逐字相同、等於最後一版；對不上的增量（baseRv 不同）丟掉、畫面不變、對那個對話重送一次 open，10 秒內再錯不再重送
  const id = LF.CONV.idle;
  const app = await openWithLayout('item-patch-stream', layoutOf([[[id], id]]));
  const { hub, page } = app;
  await waitLoaded(app, [id]);
  const base = { kind: 'Assistant', status: 'Running', time: NOW, seq: 900 };
  const words = ['串流', '的第一段，', '含 emoji 😀🎉 與代理對，', '<b>不是標籤</b>', '\n下一行', '；改寫前段'];
  const versions = [];
  let text = '';
  for (let k = 0; k < 24; k++) {
    text += words[k % words.length] + k;
    // 每 7 版把開頭改掉一次（共同前綴變短，增量從前面開始）；最後一版收尾成兩段
    const head = k % 7 === 6 ? '<p>（改寫）' : '<p>';
    versions.push(head + text.replace(/[<>&]/g, (c) => ({ '<': '&lt;', '>': '&gt;', '&': '&amp;' }[c])) + '</p>' + (k === 23 ? '<p>結束</p>' : ''));
  }
  const prefix = (a, b) => { let i = 0; while (i < a.length && i < b.length && a.charCodeAt(i) === b.charCodeAt(i)) i++; return i; };
  hub.push({ type: 'item', conversationId: id, item: Object.assign({ id: 'pz', html: versions[0] }, base), rv: 100 });
  hub.push({ type: 'item', conversationId: id, item: Object.assign({ id: 'fz', html: versions[0] }, base, { seq: 901 }) });
  for (let k = 1; k < versions.length; k++) {
    let at = prefix(versions[k - 1], versions[k]);
    if (at > 0 && at < versions[k].length && versions[k].charCodeAt(at - 1) >= 0xd800 && versions[k].charCodeAt(at - 1) <= 0xdbff) at--;
    hub.push({ type: 'itemPatch', conversationId: id, id: 'pz', baseRv: 100 + k - 1, rv: 100 + k, set: Object.assign({ id: 'pz' }, base),
      ops: [{ f: 'html', at, s: versions[k].slice(at), n: versions[k].length }] });
    hub.push({ type: 'item', conversationId: id, item: Object.assign({ id: 'fz', html: versions[k] }, base, { seq: 901 }) });
    if (k % 5 === 0) await sleep(60); // 跨過 50 ms 的批次：增量的基底有時是畫面上的、有時是排著的
  }
  const md = (iid) => page.evaluate(([sel, i]) => { const el = document.querySelector(sel + ' .it[data-id="' + i + '"] .md'); return el ? el.innerHTML : null; }, [convSel(id), iid]);
  const expect = await page.evaluate((html) => { const d = document.createElement('div'); d.innerHTML = html; return d.innerHTML; }, versions.at(-1));
  const done = await until(async () => { const a = await md('pz'); const b = await md('fz'); return a !== null && a === b && a.includes('結束') ? { a, b } : null; }, 4000);
  const opens0 = hub.cmds('open').length;
  // 對不上：baseRv 不是網頁記著的版本 → 丟掉、重送 open；第二則在 10 秒內，不再重送
  const bad = (rv) => ({ type: 'itemPatch', conversationId: id, id: 'pz', baseRv: 5, rv, set: Object.assign({ id: 'pz' }, base), ops: [{ f: 'html', at: 0, s: '<p>錯的</p>', n: 9 }] });
  hub.push(bad(200));
  const reopened = await until(() => (hub.cmds('open').length > opens0 ? true : null), 3000);
  hub.push(bad(201));
  await sleep(400);
  const opens1 = hub.cmds('open').length;
  check('hubItemPatchStream', !!done && done.a === expect && !!reopened && opens1 === opens0 + 1 && (await HP.xssState(app)).xss === 0,
    { same: !!done && done.a === done.b, equalsLast: !!done && done.a === expect, length: done ? done.a.length : null, opens: [opens0, opens1], pz: done ? null : await md('pz'), fz: done ? null : await md('fz') });
  await HP.closePage(app);
}

async function runLastPromptSeek() {
  // 對話上方常駐的「最近的指令」（liverules.latestPrompt）：瀏覽器版的標頭沒有 lastPrompt，已載入的那一頁沒有使用者訊息（長的一輪）時
  // 顯示「在較早的訊息裡」，點了往前載入、一頁沒有就再載一頁，找到就停並顯示那則；之後送來的使用者訊息立刻換上
  const id = LF.CONV.idle;
  const tool = (iid, seq) => ({ id: iid, kind: 'Tool', status: 'Done', time: NOW - 5 * LF.MIN, toolKind: 'bash', toolName: '執行指令', title: 'npm test #' + seq, seq });
  const user = (iid, seq, html) => ({ id: iid, kind: 'User', status: 'Done', time: NOW - 20 * LF.MIN, html, attachments: [{ id: 'f' + seq, name: 'a.png', kind: 'Image', size: '1 KB' }], seq });
  const items = [tool('t5', 50), tool('t6', 51), { id: 'a6', kind: 'Assistant', status: 'Done', time: NOW - 4 * LF.MIN, html: '<p>測試都過了。</p>', seq: 52 }];
  const app = await openWithLayout('last-prompt-seek', layoutOf([[[id], id]]), {
    open: { hub: (hub) => hub.onCmd('open', (cmd) => ({ push: [LF.conversationMessage(NOW, cmd.conversationId, TITLE_OF[cmd.conversationId] || '對話', { items, hasMore: true })] })) },
  });
  const { hub, page } = app;
  await waitLoaded(app, [id]);
  hub.onCmd('loadOlder', () => ({ push: [] })); // 接受、不回：探針自己決定送哪一頁
  const bar = () => page.evaluate((sel) => {
    const el = document.querySelector(sel + ' .lastprompt');
    return el && { hidden: el.hidden, older: el.classList.contains('lp-older'), height: Math.round(el.getBoundingClientRect().height), spin: !el.querySelector('.lp-spin').hidden, text: el.querySelector('.lp-body').textContent, time: el.querySelector('.lp-time').textContent,
      files: el.querySelector('.lp-files').hidden ? null : el.querySelector('.lp-files').textContent, jump: !el.querySelector('.ibtn').hidden, hist: !el.querySelector('.lp-hist').hidden };
  }, convSel(id));
  const b1 = await until(async () => { const b = await bar(); return b && !b.hidden ? b : null; }, 3000);
  await shot(app, 'last-prompt-older');
  await page.click(convSel(id) + ' .lp-body');
  const l1 = await until(() => hub.cmds('loadOlder')[0] || null, 3000);
  const b2 = await bar();
  hub.push({ type: 'olderItems', conversationId: id, beforeId: 't5', items: [tool('t3', 30), tool('t4', 31)], hasMore: true });
  const l2 = await until(() => hub.cmds('loadOlder')[1] || null, 3000);
  hub.push({ type: 'olderItems', conversationId: id, beforeId: 't3', items: [user('u1', 10, '更早的'), user('u2', 11, '把 &lt;登入&gt; 修好<br>再跑測試'), tool('t2', 12)], hasMore: true });
  const b3 = await until(async () => { const b = await bar(); return b && !b.older ? b : null; }, 3000);
  await sleep(400);
  const loads = hub.cmds('loadOlder').length;
  await shot(app, 'last-prompt-found');
  // 這一頁之後才送來的使用者訊息（瀏覽器版插在最後）
  hub.push({ type: 'item', conversationId: id, item: { id: 'u9', kind: 'User', status: 'Done', time: NOW, html: '再補一個測試', attachments: [], seq: 60 } });
  const b4 = await until(async () => { const b = await bar(); return b && b.text === '最近的指令再補一個測試' ? b : null; }, 3000);
  check('hubLastPromptSeekOlder', !!b1 && b1.older && b1.text === '最近的指令在較早的訊息裡，點這裡載入' && !b1.jump && !b1.spin && !!l1 && l1.cmd.beforeId === 't5'
    && !!b2 && b2.text === '最近的指令載入中…' && b2.spin && !!l2 && l2.cmd.beforeId === 't3'
    && !!b3 && b3.text === '最近的指令把 <登入> 修好\n再跑測試' && b3.files === '1' && b3.jump && /^\d\d:\d\d$|^昨天 \d\d:\d\d$/.test(b3.time) && loads === 2
    && !b3.spin && !!b4 && b4.files === null && b1.height === b4.height && (await HP.xssState(app)).xss === 0, // 說明文字和一行的指令一樣高
  { b1, l1: l1 && l1.cmd.beforeId, b2, l2: l2 && l2.cmd.beforeId, b3, loads, b4: b4 || (await bar()) });
  // 行尾的「歷史指令」：瀏覽器版不問（promptHistory 是 desktop_only），從已載入的項目找（最新的在前、多行換成一行）；點一筆換進輸入框
  await page.click(convSel(id) + ' .lp-hist');
  const menu = await until(() => page.evaluate(() => {
    const m = document.querySelector('.menu.menu-prompt-history');
    return m ? Array.from(m.querySelectorAll('.menu-item .mi-label')).map((x) => x.textContent) : null;
  }), 3000);
  await shot(app, 'last-prompt-history');
  await page.click('.menu-prompt-history .menu-item:nth-of-type(2)');
  const value = await until(() => page.evaluate((s) => document.querySelector(s + ' .cinput').value || null, convSel(id)), 2000);
  check('hubLastPromptHistoryFromLoaded', !!b1 && !b1.hist && !!b4 && b4.hist && JSON.stringify(menu) === JSON.stringify(['再補一個測試', '把 <登入> 修好 再跑測試', '更早的'])
    && value === '把 <登入> 修好\n再跑測試' && hub.cmds('promptHistory').length === 0, { menu, value, sent: hub.cmds('promptHistory').length });
  await HP.closePage(app);
}

async function runReviewUnsolicitedAfterClose() {
  // 伺服器主動重送的整份 conversation（F1 流量控制、通知溢出的重讀；沒有 reload、沒有 paneId）：剛關掉的分頁不重開，開著的照樣重建（LIVE-3）
  const app = await openWithLayout('review-unsolicited-after-close', layoutOf([[[LF.CONV.idle, LF.CONV.codex], LF.CONV.idle]]));
  const { hub, page } = app;
  await waitLoaded(app, [LF.CONV.idle, LF.CONV.codex]);
  await markConv(app, [LF.CONV.idle]);
  await page.hover('.tab[data-key="' + LF.CONV.codex + '"]');
  await page.click('.tab[data-key="' + LF.CONV.codex + '"] .tab-x');
  const closed = await until(async () => { const s = await wsState(app); return s.panes[0].tabs.includes(LF.CONV.codex) ? null : s; }, 3000);
  const closes = hub.cmds('close').map((c) => c.cmd.conversationId);
  hub.push(LF.conversationMessage(NOW, LF.CONV.codex, TITLE_OF[LF.CONV.codex]));
  hub.push(LF.conversationMessage(NOW, LF.CONV.idle, TITLE_OF[LF.CONV.idle], { items: LF.liveItems(NOW, 'n') }));
  const reloaded = await until(() => page.evaluate((sel) => (document.querySelector(sel + ' .it[data-id="n1"]') ? true : null), convSel(LF.CONV.idle)), 3000);
  await sleep(500);
  const ws = await wsState(app);
  const idle = await convState(app, LF.CONV.idle);
  const codex = await convState(app, LF.CONV.codex);
  check('hubUnsolicitedConversationAfterClose', !!closed && JSON.stringify(closes) === JSON.stringify([LF.CONV.codex]) && !!reloaded && !!idle && idle.mark === '1'
    && JSON.stringify(ws.panes.flatMap((p) => p.tabs)) === JSON.stringify([LF.CONV.idle]) && ws.panes[0].active === LF.CONV.idle && codex === null,
  { closed: closed && closed.panes, closes, reloaded: !!reloaded, idleMark: idle && idle.mark, panes: ws.panes, codex: !!codex });
  await HP.closePage(app);
}

async function runReviewCardTexts() {
  // 核准卡與計畫卡打到一半的字：重新 init 後重送 open、卡片整個重建，字照樣還原（LIVE-5）
  const id = LF.CONV.running;
  const app = await openWithLayout('review-card-texts', layoutOf([[[id], id]]));
  const { hub, page } = app;
  await waitLoaded(app, [id]);
  const TELL = '先不要跑整個測試，只跑登入的';
  const NOTE = '計畫裡加一步：補文件';
  const typed = (await setCardText(app, id, 'i6', TELL)) && (await setCardText(app, id, 'i5', NOTE));
  const sels = [cardSel(id, 'i6'), cardSel(id, 'i5')];
  await page.evaluate((list) => { for (const s of list) { const n = document.querySelector(s); if (n) n.dataset.probeMark = '1'; } }, sels);
  const n0 = hub.cmds('open').length;
  hub.push(LF.liveBrowserInit(NOW, { mode: 'main' }));
  const reopened = await until(() => (hub.cmds('open').length > n0 ? true : null), 3000);
  const rebuilt = await until(() => page.evaluate((list) => (list.every((s) => { const n = document.querySelector(s); return !!n && !n.dataset.probeMark; }) ? true : null), sels), 3000);
  await sleep(300);
  const tell = await cardText(app, id, 'i6');
  const note = await cardText(app, id, 'i5');
  check('hubCardTextsKeptOnReload', typed && !!reopened && !!rebuilt && tell === TELL && note === NOTE, { typed, reopened: !!reopened, rebuilt: !!rebuilt, tell, note });
  await HP.closePage(app);
}

async function runReviewCardInputBlocksReload() {
  // 伺服器換版：卡片說明欄打到一半的字、問卷選了的選項都算未送出的輸入 → 只顯示橫幅、不自動重新載入（LIVE-5）
  const id = LF.CONV.running;
  const layout = layoutOf([[[id], id]]);
  const TEXT = '改成只跑登入的測試';
  const a = await openBuild('review-card-text-build', 'aaaa111', { layout });
  await waitLoaded(a, [id]);
  const typed = await setCardText(a, id, 'i6', TEXT);
  a.reinit('bbbb222');
  const bannerA = await until(() => quiet(noticeOf(a, 'build')), 3000);
  await sleep(800);
  const reloadsA = reloads(a);
  const textA = await quiet(cardText(a, id, 'i6'));
  await HP.closePage(a);
  const b = await openBuild('review-card-option-build', 'aaaa111', { layout });
  await waitLoaded(b, [id]);
  await clickOption(b, id, 'i4', 1);
  await sleep(200);
  const picked = await quiet(b.page.evaluate((sel) => document.querySelectorAll(sel + ' .opt[aria-pressed="true"], .opt.sel, .opt.on').length, cardSel(id, 'i4')));
  b.reinit('bbbb222');
  const bannerB = await until(() => quiet(noticeOf(b, 'build')), 3000);
  await sleep(800);
  const reloadsB = reloads(b);
  await HP.closePage(b);
  check('hubCardInputBlocksBuildReload', typed && !!bannerA && bannerA.text === BUILD_TEXT && reloadsA === 0 && textA === TEXT
    && !!bannerB && bannerB.text === BUILD_TEXT && reloadsB === 0,
  { approvalText: { typed, banner: !!bannerA, reloads: reloadsA, text: textA }, questionOption: { picked, banner: !!bannerB, reloads: reloadsB } });
}

async function runReviewListFilesUnknown() {
  // listFiles 送出後重新 init（重連、resync）：files 只送回發出的那條連線，等不到了 → 抽屜不再轉圈、說明可以重新整理（LIVE-6、P1）
  const id = LF.CONV.idle;
  const app = await openWithLayout('review-list-unknown', layoutOf([[[id], id]]));
  const { hub, page } = app;
  await waitLoaded(app, [id]);
  hub.onCmd('listFiles', () => ({ push: [] })); // 接受、不回 files
  await page.click(convSel(id) + ' header.ch button[aria-label="檔案瀏覽器"]');
  const l1 = await until(() => hub.cmds('listFiles')[0] || null, 3000);
  const loading = await until(async () => { const d = await drawerState(app, id); return d && d.open && d.state === '讀取資料夾中…' ? d : null; }, 3000);
  hub.push(LF.liveBrowserInit(NOW, { mode: 'main' }));
  const failed = await until(async () => { const d = await drawerState(app, id); return d && d.state && d.state.includes(LIST_UNKNOWN_TEXT) ? d : null; }, 4000);
  check('hubListFilesUnknownOnReinit', !!l1 && !!loading && !!failed && failed.open, { list: l1 && l1.cmd, loading: loading && loading.state, after: failed || (await drawerState(app, id)) });
  await HP.closePage(app);
}

/** 檔案框（fileFetch）：開啟連結、錯誤說明、還在等的那一行 */
const fileRefState = (app) => app.page.evaluate(() => {
  const d = document.querySelector('.dlg-file-ref');
  if (!d) return null;
  const err = d.querySelector('[data-field="fileRefError"]');
  return { link: !!d.querySelector('[data-field="fileRefOpen"]'), error: err ? err.textContent : null, wait: !!d.querySelector('[data-field="fileRefWait"]') };
});

async function runReviewFileFetchUnknown() {
  // fileFetch 送出後重新 init：fileRef 等不到了 → 框裡不再停在「讀取中」，改成說明（LIVE-6、P1）
  const id = LF.CONV.idle;
  const items = LF.liveItems(NOW).concat([{ id: 'i7', kind: 'Approval', status: 'Done', time: NOW - LF.MIN, toolKind: 'edit', toolName: '編輯檔案', title: '更新報表',
    filePath: 'C:\\repo\\demo\\report.txt', detail: null, canAllowForSession: false, decision: 'allow', seq: 7 }]);
  const app = await openWithLayout('review-fetch-unknown', layoutOf([[[id], id]]), {
    open: { hub: (hub) => hub.onCmd('open', (cmd) => ({ push: [LF.conversationMessage(NOW, cmd.conversationId, TITLE_OF[cmd.conversationId] || '對話', { items })] })) },
  });
  const { hub, page } = app;
  await waitLoaded(app, [id]);
  hub.onCmd('fileFetch', () => ({ push: [] })); // 接受、不回 fileRef
  await page.click(itemSel(id, 'i7') + ' [data-field="fileFetch"]');
  const f1 = await until(() => hub.cmds('fileFetch')[0] || null, 3000);
  const waiting = await until(async () => { const d = await fileRefState(app); return d && d.wait ? d : null; }, 3000);
  hub.push(LF.liveBrowserInit(NOW, { mode: 'main' }));
  const after = await until(async () => { const d = await fileRefState(app); return d && d.error ? d : null; }, 4000);
  check('hubFileFetchUnknownOnReinit', !!f1 && !!waiting && !!after && after.error === FILE_REF_UNKNOWN_TEXT && !after.wait && !after.link,
    { fetch: f1 && f1.cmd, waiting, after: after || (await fileRefState(app)) });
  await page.keyboard.press('Escape');
  await HP.closePage(app);
}

async function runReviewReplicaTabRunning() {
  // 桌面上其他電腦的副本：分頁的「執行中」照擁有者那邊的（header.replica.running；這台的 header.running 一律 false），和側欄一致（LIVE-7、P5）
  const id = LF.CONV.running;
  const conversations = LF.desktopList(NOW).map((c) => (c.id === id ? Object.assign({}, c, { waiting: false }) : c));
  const app = await openDesktopLive('review-replica-tab-running', {
    init: { conversations, layout: desktopLayout([id]) },
    host: { conversations, replica: () => ({ waiting: false, items: LF.desktopItems(NOW, 'r').slice(0, 3) }) },
  });
  const { page } = app;
  const loaded = await until(() => page.evaluate((s) => !!document.querySelector(s + ':not(.loading) [data-field="replicaReadOnly"]'), convSel(id)), 4000);
  await sleep(300);
  const opened = app.host.posted.filter((m) => m.cmd === 'open').map((m) => m.conversationId);
  const tab = await page.evaluate((cid) => {
    const t = document.querySelector('.tab[data-key="' + cid + '"]');
    return t ? { spin: !!t.querySelector('.tab-state .tab-spin'), wait: !!t.querySelector('.tab-state .badge-wait'), title: t.title } : null;
  }, id);
  check('desktopReplicaTabRunning', !!loaded && opened.includes(id) && !!tab && tab.spin && !tab.wait && tab.title.includes('\n執行中'), { loaded: !!loaded, tab });
  // 對話畫面的執行中動畫（.conv.running：輸入框的跑馬燈等）與狀態列也照擁有者那邊的：以前只有分頁轉圈，對話框看起來像閒置
  const conv = await page.evaluate((s) => {
    const root = document.querySelector(s);
    const run = root && root.querySelector('.runline');
    return root ? { running: root.classList.contains('running'), runHidden: !run || run.hidden, idle: !!run && run.classList.contains('idle'), text: run ? run.querySelector('.run-text').textContent : null } : null;
  }, convSel(id));
  check('desktopReplicaConvRunning', !!conv && conv.running && !conv.runHidden && !conv.idle && conv.text === '執行中…', { conv });
  await HP.closePage(app);
}

async function runPeerReplicaFolder() {
  // 桌面遠端操作：可以操作的副本（sync.peer）的資料夾晶片與選單照擁有者發布的 customFolder／workingDirectory（2026-10-23 之前副本一律是
  // 「預設工作區」：從這台換了那台的資料夾看起來沒換成功、「恢復預設工作區」一直停用）；執行中看 header.replica.running（這台的 header.running 一律 false）
  const id = LF.CONV.running;
  const folder = 'D:\\work\\proj';
  const conversations = LF.desktopList(NOW).map((c) => (c.id === id ? Object.assign({}, c, { running: false, waiting: false, workingDirectory: folder, customFolder: true }) : c));
  const sync = Object.assign(LF.syncWith(NOW, { remoteSupported: true }), { peer: true });
  const remote = LF.liveDesktopInit(NOW, { sync }).remote;
  remote.devices = remote.devices.map((d) => (d.id === LF.DEVICE.offline ? Object.assign({}, d, { online: true }) : d)); // 擁有者在線、政策 Control
  const app = await openDesktopLive('peer-replica-folder', {
    init: { conversations, layout: desktopLayout([id]), sync },
    initPatch: { remote },
    host: { conversations, replica: () => ({ waiting: false, items: LF.desktopItems(NOW, 'r').slice(0, 1), header: { customFolder: true, workingDirectory: folder } }) },
  });
  const { page } = app;
  await waitLoaded(app, [id]);
  const chip = () => page.evaluate((s) => {
    const b = document.querySelector(s + ' .ch-folder');
    return b ? { label: b.querySelector('.chip-label').textContent, title: b.title, disabled: b.disabled, custom: b.classList.contains('custom') } : null;
  }, convSel(id));
  const folderMenu = async () => {
    await page.click(convSel(id) + ' .ch-folder');
    await page.waitForSelector('.menu');
    const items = await page.evaluate(() => [...document.querySelectorAll('.menu .menu-item')].map((b) => ({ label: b.querySelector('.mi-label').textContent, disabled: b.disabled })));
    const path = await page.evaluate(() => { const v = document.querySelector('.menu .menu-path-value'); return v ? v.textContent : null; });
    return { items, path };
  };
  const item = (m, label) => m.items.find((x) => x.label === label) || null;

  const idle = await chip();
  const idleMenu = await folderMenu();
  const n0 = app.host.posted.length;
  await clickMenu(app, '恢復預設工作區');
  const reset = await until(() => app.host.posted.slice(n0).find((m) => m.cmd === 'resetFolder') || null, 2000);
  check('desktopPeerReplicaFolderShown', !!idle && idle.label === 'proj' && idle.custom && !idle.disabled && idle.title === '工作資料夾：' + folder
    && idleMenu.path === folder && !!item(idleMenu, '恢復預設工作區') && !item(idleMenu, '恢復預設工作區').disabled
    && !!item(idleMenu, '輸入資料夾路徑…') && !item(idleMenu, '輸入資料夾路徑…').disabled
    && !item(idleMenu, '開啟附件資料夾') // 這台的檔案總管開不到那台的附件（openFolder 不轉送，送了只會被當成唯讀擋下）
    && !!reset && reset.conversationId === id && /^[0-9A-HJKMNP-TV-Z]{26}$/.test(reset.commandId || ''),
  { idle, idleMenu, reset });

  // 擁有者那邊開始執行：晶片說明與選單照 replica.running 停用（那台執行中會拒絕）
  const busyHeader = LF.replicaHeader(id, conversations.find((c) => c.id === id).title, LF.DEVICE.offline, { running: true, header: { customFolder: true, workingDirectory: folder } });
  await app.host.send({ type: 'header', conversationId: id, header: busyHeader });
  const busy = await until(async () => { const c = await chip(); return c && c.title.endsWith('（執行中不能更換）') ? c : null; }, 2000);
  const busyMenu = await folderMenu();
  await closeMenu(app);
  check('desktopPeerReplicaFolderOwnerRunning', !!busy && busyHeader.running === false
    && ['恢復預設工作區', '輸入資料夾路徑…', '瀏覽資料夾…'].every((l) => !!item(busyMenu, l) && item(busyMenu, l).disabled),
  { busy, busyMenu });
  await HP.closePage(app);
}

async function runPeerDraftFolder() {
  // 桌面遠端操作（2026-10-27 使用者要求：選好電腦就能選工作區，不必先送一次）：草稿選了其他電腦（header.deviceId）時資料夾晶片照常可按，
  // 選單同瀏覽器版的草稿——手輸那台的路徑、挑那台最近用過的（宿主從那台的副本取 recentFolders）、恢復預設工作區；這台的資料夾對話框、
  // 檔案總管、附件資料夾、瀏覽資料夾（那台還沒有這個對話）都不列。選了送 setFolder（草稿不帶 commandId：宿主記在草稿上，第一次送出才帶給那台）；
  // 建立中（peerCreating）停用
  const host0 = LF.desktopList(NOW).find((c) => !c.deviceId);
  const sync = Object.assign(LF.syncWith(NOW, { remoteSupported: true }), { peer: true });
  const remote = LF.liveDesktopInit(NOW, { sync }).remote;
  remote.devices = remote.devices.map((d) => (d.id === LF.DEVICE.offline ? Object.assign({}, d, { online: true }) : d)); // 那台在線、政策 Control
  const devName = remote.devices.find((d) => d.id === LF.DEVICE.offline).name;
  const app = await openDesktopLive('peer-draft-folder', { init: { layout: desktopLayout([host0.id]), sync }, initPatch: { remote } });
  const { page } = app;
  await waitLoaded(app, [host0.id]);
  const id = 'dr1';
  const recent = ['D:\\work\\proj', 'C:\\repo\\site'];
  const draftHeader = (o) => ({ isDraft: true, title: '', deviceId: LF.DEVICE.offline, workingDirectory: null, customFolder: false, recentFolders: recent, peerCreating: false, ...o });
  const msg = LF.localMessage(NOW, id, '', { conversationSync: null, items: [], header: draftHeader({}) });
  await app.host.send(Object.assign(msg, { paneId: 'p1' }));
  await page.waitForSelector(convSel(id) + ' .ch-folder', { timeout: 4000 });
  const chip = () => page.evaluate((s) => {
    const b = document.querySelector(s + ' .ch-folder');
    return b ? { label: b.querySelector('.chip-label').textContent, title: b.title, disabled: b.disabled, custom: b.classList.contains('custom') } : null;
  }, convSel(id));
  const folderMenu = async () => {
    await page.click(convSel(id) + ' .ch-folder');
    await page.waitForSelector('.menu');
    return page.evaluate(() => ({
      items: [...document.querySelectorAll('.menu .menu-item')].map((b) => ({ label: b.querySelector('.mi-label').textContent, disabled: b.disabled })),
      heads: [...document.querySelectorAll('.menu .menu-head')].map((n) => n.textContent),
      pathLabel: (document.querySelector('.menu .menu-path-label') || {}).textContent || null,
      path: (document.querySelector('.menu .menu-path-value') || {}).textContent || null,
    }));
  };
  const item = (m, label) => m.items.find((x) => x.label === label) || null;

  const idle = await chip();
  const menu = await folderMenu();
  await shot(app, 'desktop-peer-draft-folder-menu');
  const n0 = app.host.posted.length;
  await clickMenu(app, 'proj');
  const picked = await until(() => app.host.posted.slice(n0).find((m) => m.cmd === 'setFolder') || null, 2000);
  check('desktopPeerDraftFolderMenu', !!idle && !idle.disabled && idle.label === '預設工作區' && idle.title === '預設工作區（在 ' + devName + ' 上，這個對話自己的資料夾）'
    && menu.pathLabel === '預設工作區（在 ' + devName + ' 上，這個對話自己的資料夾）' && menu.path === null
    && !!item(menu, '輸入資料夾路徑…') && !item(menu, '輸入資料夾路徑…').disabled
    && menu.heads.includes('最近使用（' + devName + '）') && !!item(menu, 'proj') && !!item(menu, 'site')
    && !!item(menu, '恢復預設工作區') && item(menu, '恢復預設工作區').disabled
    && ['瀏覽資料夾…', '瀏覽檔案', '在檔案總管開啟', '在 app 內瀏覽檔案', '更換資料夾…', '開啟附件資料夾'].every((l) => !item(menu, l))
    && !!picked && picked.conversationId === id && picked.path === 'D:\\work\\proj' && picked.commandId === undefined,
  { idle, menu, picked });

  // 宿主記下來（header 的 customFolder／workingDirectory）：晶片換成資料夾名稱，選單可以恢復預設；手輸路徑的對話框說明送出時那台才檢查
  await app.host.send({ type: 'header', conversationId: id, header: Object.assign(msg.header, draftHeader({ customFolder: true, workingDirectory: 'D:\\work\\proj' })) });
  const custom = await until(async () => { const c = await chip(); return c && c.custom ? c : null; }, 2000);
  const customMenu = await folderMenu();
  await clickMenu(app, '輸入資料夾路徑…');
  await page.waitForSelector('[data-field="folderPath"]');
  const dlg = await page.evaluate(() => {
    const input = document.querySelector('[data-field="folderPath"]');
    const d = input.closest('.dlg, .dialog, [role="dialog"]') || document.body;
    return { value: input.value, detail: (d.querySelector('.confirm-detail') || {}).textContent || null, buttons: [...d.querySelectorAll('button')].map((b) => b.textContent) };
  });
  await shot(app, 'desktop-peer-draft-folder-prompt');
  await page.fill('[data-field="folderPath"]', 'E:\\data\\new');
  const n1 = app.host.posted.length;
  await page.keyboard.press('Enter');
  const typed = await until(() => app.host.posted.slice(n1).find((m) => m.cmd === 'setFolder') || null, 2000);
  check('desktopPeerDraftFolderChosen', !!custom && custom.label === 'proj' && custom.title === '工作資料夾（在 ' + devName + ' 上）：D:\\work\\proj'
    && customMenu.path === 'D:\\work\\proj' && customMenu.pathLabel === devName + ' 上的工作資料夾' && !item(customMenu, '恢復預設工作區').disabled
    && dlg.value === 'D:\\work\\proj' && /送出第一則訊息時那台電腦會檢查/.test(dlg.detail || '') && dlg.buttons.includes('選定')
    && !!typed && typed.path === 'E:\\data\\new' && typed.commandId === undefined,
  { custom, customMenu, dlg, typed });

  // 送出後建立中：晶片停用
  await app.host.send({ type: 'header', conversationId: id, header: Object.assign(msg.header, draftHeader({ customFolder: true, workingDirectory: 'D:\\work\\proj', peerCreating: true })) });
  const creating = await until(async () => { const c = await chip(); return c && c.disabled ? c : null; }, 2000);
  check('desktopPeerDraftFolderCreating', !!creating, { creating, now: await chip() });
  await HP.closePage(app);
}

async function runPeerReplicaModel() {
  // 桌面遠端操作：從這台改那台對話的模型——選單列擁有者發布的清單（header.models，2026-10-23 起；以前是這台內建的預設選單，Codex 只有「預設」），
  // 選了送帶 commandId 的 setOption、標籤先樂觀換掉；那台拒絕（或結果不明）時那台沒改、不會有新的標頭，標籤回到原本的模型
  const id = LF.CONV.running;
  const models = [
    { value: 'default', label: '預設', description: '帳戶預設模型', efforts: ['low', 'high'] },
    { value: 'gpt-5', label: 'GPT-5', description: '旗艦', efforts: ['low', 'high'] },
    { value: 'gpt-5-mini', label: 'GPT-5 mini', description: null, efforts: ['low'] },
  ];
  const header = { provider: 'Codex', model: 'gpt-5', effort: 'high', models };
  const conversations = LF.desktopList(NOW).map((c) => (c.id === id ? Object.assign({}, c, { running: false, waiting: false }) : c));
  const sync = Object.assign(LF.syncWith(NOW, { remoteSupported: true }), { peer: true });
  const remote = LF.liveDesktopInit(NOW, { sync }).remote;
  remote.devices = remote.devices.map((d) => (d.id === LF.DEVICE.offline ? Object.assign({}, d, { online: true }) : d)); // 擁有者在線、政策 Control
  const app = await openDesktopLive('peer-replica-model', {
    init: { conversations, layout: desktopLayout([id]), sync },
    initPatch: { remote },
    host: { conversations, replica: () => ({ waiting: false, items: LF.desktopItems(NOW, 'r').slice(0, 1), header }) },
  });
  const { page } = app;
  await waitLoaded(app, [id]);
  // 模型鈕：工具列第一個沒有「推理」前綴的 .pick（第二個是推理強度）
  const modelSel = convSel(id) + ' .cbar .pick:not(:has(.pick-pre))';
  const pick = () => page.evaluate((s) => {
    const b = document.querySelector(s);
    return b ? { label: b.querySelector('.pick-label').textContent, title: b.title, visible: getComputedStyle(b).display !== 'none' } : null;
  }, modelSel);

  const before = await pick();
  await page.click(modelSel);
  await page.waitForSelector('.menu');
  const labels = await page.evaluate(() => [...document.querySelectorAll('.menu .menu-item .mi-label')].map((e) => e.textContent));
  const n0 = app.host.posted.length;
  await clickMenu(app, 'GPT-5 mini');
  const sent = await until(() => app.host.posted.slice(n0).find((m) => m.cmd === 'setOption') || null, 2000);
  const optimistic = await until(async () => { const p = await pick(); return p && p.label === 'GPT-5 mini' ? p : null; }, 2000);
  check('desktopPeerReplicaModelMenu', !!before && before.visible && before.label === 'GPT-5'
    && JSON.stringify(labels) === JSON.stringify(['預設', 'GPT-5', 'GPT-5 mini'])
    && !!sent && sent.conversationId === id && sent.key === 'model' && sent.value === 'gpt-5-mini' && /^[0-9A-HJKMNP-TV-Z]{26}$/.test(sent.commandId || '')
    && !!optimistic,
  { before, labels, sent, optimistic });

  // 那台拒絕：標籤回到原本的模型
  await app.host.send(serverResult(sent && sent.commandId, 'rejected', 'policy'));
  const reverted = await until(async () => { const p = await pick(); return p && p.label === 'GPT-5' ? p : null; }, 2000);
  check('desktopPeerReplicaModelRevert', !!reverted, { reverted, now: await pick() });
  await HP.closePage(app);
}

/** 執行中換模型用的清單（標籤彼此不是子字串：clickMenu 以子字串找項目） */
const RUNNING_MODELS = [
  { value: 'default', label: '預設', description: '帳戶預設模型', efforts: ['low', 'high'] },
  { value: 'opus', label: 'Opus 5.5', description: '最強', efforts: ['low', 'high', 'max'] },
  { value: 'haiku', label: 'Haiku 5.5', description: '最快', efforts: [] },
];

/** 模型鈕（工具列第一個沒有「推理」前綴的 .pick）、推理強度鈕、停止鈕的狀態 */
const runningPicks = (app, id) => app.page.evaluate((sel) => {
  const root = document.querySelector(sel);
  if (!root) return null;
  const vis = (el) => !!el && !el.hidden && getComputedStyle(el).display !== 'none';
  const model = root.querySelector('.cbar .pick:not(:has(.pick-pre))');
  const effort = root.querySelector('.cbar .pick:has(.pick-pre)');
  return {
    model: model ? { label: model.querySelector('.pick-label').textContent, visible: vis(model), disabled: model.disabled } : null,
    effort: effort ? { label: effort.querySelector('.pick-label').textContent, visible: vis(effort) } : null,
    stop: vis(root.querySelector('.cbtn.stop')),
  };
}, convSel(id));

/** 點模型鈕、選 label 那一項：回選單的標籤 */
async function pickModelWhileRunning(app, id, label) {
  await app.page.click(convSel(id) + ' .cbar .pick:not(:has(.pick-pre))');
  await app.page.waitForSelector('.menu');
  const labels = await app.page.evaluate(() => [...document.querySelectorAll('.menu .menu-item .mi-label')].map((e) => e.textContent));
  await clickMenu(app, label);
  return labels;
}

async function runHubModelWhileRunning() {
  // 瀏覽器版：代理執行中也能換模型——那台的中樞立即套用到這一輪（AgentHub.SetOption → IAgentSession.SetModelAsync，Claude、Grok 從下一次模型呼叫起；
  // Codex、Antigravity 那台回提示「下一則訊息才生效」）。執行中模型鈕照常可按，選了送帶 commandId 的 setOption、標籤先樂觀換掉；
  // 那台換好、發布新的標頭之後照新的標頭（換成不支援推理強度的模型時強度鈕收起來）
  const id = LF.CONV.running;
  const header = (cid) => (cid === id ? { running: true, model: 'opus', effort: 'high', models: RUNNING_MODELS } : null);
  const app = await openWithLayout('model-while-running', layoutOf([[[id], id]]), { serve: { header } });
  const { hub } = app;
  await waitLoaded(app, [id]);
  hub.onCmd('setOption', (cmd, arg) => ({ push: [serverResult(arg.commandId, 'done')] }));
  const before = await runningPicks(app, id);
  const labels = await pickModelWhileRunning(app, id, 'Haiku 5.5');
  const sent = await until(() => hub.cmds('setOption')[0] || null, 3000);
  const optimistic = await until(async () => { const p = await runningPicks(app, id); return p && p.model && p.model.label === 'Haiku 5.5' ? p : null; }, 2000);
  const res = sent ? await until(() => resultOf(app, sent.commandId), 3000) : null;
  // 那台換好：新的標頭（推理強度換成新模型的預設；haiku 不支援推理強度）
  hub.push({ type: 'header', conversationId: id, header: LF.serverHeader(NOW, id, TITLE_OF[id], { running: true, model: 'haiku', effort: 'default', models: RUNNING_MODELS }) });
  await sleep(300);
  const after = await runningPicks(app, id);
  check('hubModelSwitchWhileRunning', !!before && before.stop && !!before.model && before.model.visible && !before.model.disabled && before.model.label === 'Opus 5.5'
    && JSON.stringify(labels) === JSON.stringify(['預設', 'Opus 5.5', 'Haiku 5.5'])
    && !!sent && sent.cmd.conversationId === id && sent.cmd.key === 'model' && sent.cmd.value === 'haiku' && ULID_RE.test(sent.commandId || '')
    && !!optimistic && optimistic.stop && !!res && res.state === 'done'
    && !!after && after.stop && after.model.label === 'Haiku 5.5' && !!after.effort && !after.effort.visible,
  { before, labels, sent: sent && { cmd: sent.cmd, commandId: sent.commandId }, optimistic, result: res, after });
  await HP.closePage(app);
}

async function runPeerReplicaModelWhileRunning() {
  // 桌面遠端操作：擁有者那台正在執行（header.replica.running）時，這台照樣能換那個對話的模型與推理強度（那台立即套用到這一輪）——
  // 和換工作資料夾不同（那台執行中會拒絕，ownerRunning 停用）。模型與強度各送一次帶 commandId 的 setOption
  const id = LF.CONV.running;
  const header = { provider: 'Claude', model: 'opus', effort: 'high', models: RUNNING_MODELS };
  const conversations = LF.desktopList(NOW).map((c) => (c.id === id ? Object.assign({}, c, { running: true, waiting: false }) : c));
  const sync = Object.assign(LF.syncWith(NOW, { remoteSupported: true }), { peer: true });
  const remote = LF.liveDesktopInit(NOW, { sync }).remote;
  remote.devices = remote.devices.map((d) => (d.id === LF.DEVICE.offline ? Object.assign({}, d, { online: true }) : d)); // 擁有者在線、政策 Control
  const app = await openDesktopLive('peer-replica-model-running', {
    init: { conversations, layout: desktopLayout([id]), sync },
    initPatch: { remote },
    host: { conversations, replica: () => ({ running: true, waiting: false, items: LF.desktopItems(NOW, 'r').slice(0, 1), header }) },
  });
  await waitLoaded(app, [id]);
  const before = await runningPicks(app, id);
  const n0 = app.host.posted.length;
  const labels = await pickModelWhileRunning(app, id, '預設');
  const sentModel = await until(() => app.host.posted.slice(n0).find((m) => m.cmd === 'setOption' && m.key === 'model') || null, 2000);
  const optimistic = await until(async () => { const p = await runningPicks(app, id); return p && p.model && p.model.label === '預設' ? p : null; }, 2000);
  await app.host.send(serverResult(sentModel && sentModel.commandId, 'done'));
  // 推理強度（預設模型有 low、high）
  await app.page.click(convSel(id) + ' .cbar .pick:has(.pick-pre)');
  await app.page.waitForSelector('.menu');
  const n1 = app.host.posted.length;
  await clickMenu(app, '低');
  const sentEffort = await until(() => app.host.posted.slice(n1).find((m) => m.cmd === 'setOption' && m.key === 'effort') || null, 2000);
  const replica = await replicaState(app, id);
  check('desktopPeerReplicaModelWhileRunning', !!before && !!before.model && before.model.visible && !before.model.disabled && before.model.label === 'Opus 5.5'
    && JSON.stringify(labels) === JSON.stringify(['預設', 'Opus 5.5', 'Haiku 5.5'])
    && !!sentModel && sentModel.conversationId === id && sentModel.value === 'default' && /^[0-9A-HJKMNP-TV-Z]{26}$/.test(sentModel.commandId || '') && !!optimistic
    && !!sentEffort && sentEffort.conversationId === id && sentEffort.value === 'low' && /^[0-9A-HJKMNP-TV-Z]{26}$/.test(sentEffort.commandId || '')
    && !!replica && !replica.readOnly,
  { before, labels, sentModel, optimistic, sentEffort, replica });
  await HP.closePage(app);
}

async function runPeerReplicaAccount() {
  // 桌面遠端操作（2026-10-25 使用者要求）：其他電腦的對話也能換帳戶，但只能換成那台已登入的帳戶。帳戶晶片照 replica.accountId（那台用的邏輯帳戶）
  // 顯示名稱；選單只列那台 ready 的（這台登入、那台沒登入的 Codex Pro 與沒連結的不列；只在那台登入的 remoteOnly 帳戶要列），選了送帶 commandId、
  // 帶邏輯帳戶的 setAccount；那台執行中（replica.running）時先選時機
  const id = LF.CONV.running;
  const title = LF.desktopList(NOW).find((c) => c.id === id).title;
  const conversations = LF.desktopList(NOW).map((c) => (c.id === id ? Object.assign({}, c, { running: false, waiting: false }) : c));
  const sync = Object.assign(LF.syncWith(NOW, { remoteSupported: true }), { peer: true });
  const remote = LF.liveDesktopInit(NOW, { sync }).remote;
  remote.devices = remote.devices.map((d) => (d.id === LF.DEVICE.offline ? Object.assign({}, d, { online: true }) : d)); // 擁有者在線、政策 Control
  const accounts = [...RF.desktopAccounts(NOW), ...RF.remoteOnlyAccounts(NOW)];
  const app = await openDesktopLive('peer-replica-account', {
    init: { conversations, layout: desktopLayout([id]), sync },
    initPatch: { remote, accounts },
    host: { conversations, replica: () => ({ waiting: false, accountId: 'la_c1', items: LF.desktopItems(NOW, 'r').slice(0, 1) }) },
  });
  const { page } = app;
  await waitLoaded(app, [id]);
  const chipSel = convSel(id) + ' .ch-acct';
  const chip = () => page.evaluate((s) => {
    const b = document.querySelector(s);
    return b ? { label: b.querySelector('.chip-label').textContent, title: b.title, disabled: b.disabled } : null;
  }, chipSel);
  const accountMenu = async () => {
    await page.click(chipSel);
    await page.waitForSelector('.menu');
    return menuState(app);
  };
  const labelsOf = (m) => m.filter((x) => x.label).map((x) => x.label);

  const idle = await chip();
  const idleMenu = await accountMenu();
  const n0 = app.host.posted.length;
  await clickMenu(app, '只在筆電（沒有用量）');
  await page.waitForSelector('.dlg-confirm');
  await page.click('.dlg-confirm .btn.primary'); // 換到另一種代理：先確認會以前文節錄開新對話
  const sent = await until(() => app.host.posted.slice(n0).find((m) => m.cmd === 'setAccount') || null, 2000);
  check('desktopPeerReplicaAccountMenu', !!idle && !idle.disabled && idle.label === '工作用 Max' && idle.title.endsWith('點一下更換帳戶\n只能換成在 筆電 ' + LF.XSS + ' 登入的帳戶')
    && JSON.stringify(labelsOf(idleMenu)) === JSON.stringify(['工作用 Max', '只在筆電（有用量）', '只在筆電（沒有用量）'])
    && idleMenu.find((x) => x.label === '工作用 Max').checked === 'true'
    && !!sent && sent.conversationId === id && sent.accountId === 'la_ro2' && !('when' in sent) && /^[0-9A-HJKMNP-TV-Z]{26}$/.test(sent.commandId || ''),
  { idle, idleMenu, sent });

  // 那台開始執行：選了同一種代理的另一個帳戶，先選時機（等這一輪結束再換＝帶 when:turnEnd）
  const busyHeader = LF.replicaHeader(id, title, LF.DEVICE.offline, { running: true, accountId: 'la_c1' });
  await app.host.send({ type: 'header', conversationId: id, header: busyHeader });
  await until(async () => { const c = await chip(); return c && c.title.includes('執行中也可以換帳戶') ? c : null; }, 2000);
  const busyMenu = await accountMenu();
  const n1 = app.host.posted.length;
  await clickMenu(app, '只在筆電（有用量）');
  await page.waitForSelector('.dlg-choice');
  await page.click('.dlg-choice .btn[data-value="turnEnd"]');
  const sentBusy = await until(() => app.host.posted.slice(n1).find((m) => m.cmd === 'setAccount') || null, 2000);
  check('desktopPeerReplicaAccountWhileRunning', labelsOf(busyMenu).join('|') === '工作用 Max|只在筆電（有用量）|只在筆電（沒有用量）'
    && busyMenu.some((x) => x.head && x.head.startsWith('Claude（立即換或等目前步驟完成後換'))
    && !!sentBusy && sentBusy.conversationId === id && sentBusy.accountId === 'la_ro1' && sentBusy.when === 'turnEnd'
    && /^[0-9A-HJKMNP-TV-Z]{26}$/.test(sentBusy.commandId || ''),
  { busyMenu, sentBusy });
  await HP.closePage(app);
}

/** 頁面裡的 XMLHttpRequest.abort 計數（上傳的 PUT 有沒有被中止） */
const ABORT_PATCH = () => {
  window.__xhrAborts = 0;
  const orig = XMLHttpRequest.prototype.abort;
  XMLHttpRequest.prototype.abort = function abort(...args) {
    window.__xhrAborts++;
    return orig.apply(this, args);
  };
};
const xhrAborts = (app) => app.page.evaluate(() => window.__xhrAborts || 0);
const chipX = (app, id) => app.page.evaluate((sel) => {
  const x = document.querySelector(sel + ' [data-field="uploads"] .up-chip .chip-x');
  if (!x) return null;
  const title = x.title;
  x.click();
  return title;
}, convSel(id));

async function runReviewUploadCancel() {
  // 上傳中可以取消：晶片的 ×、關掉分頁、伺服器關閉對話都中止 PUT（不在背景傳完、佔著伺服器的預留），也不送 addUploaded（PERF-2）
  const id = LF.CONV.idle;
  const never = new Promise(() => {}); // 上傳端點永遠不回：只有中止才會結束
  const app = await openUploadConv('review-upload-cancel', { uploads: { hold: never } });
  const { hub, page } = app;
  await page.evaluate(ABORT_PATCH);
  // 1. 上傳中的晶片有 ×（標題「取消上傳」）：按了中止、晶片拿掉、送出不再等它
  await chooseFiles(app, id, [fileOf('取消的.txt', '按 × 取消上傳')]);
  const during = await untilChips(app, id, allState('uploading', 1), 3000);
  const xTitle = await chipX(app, id);
  const gone = await untilChips(app, id, (s) => s.chips.length === 0, 2000);
  const abortX = await xhrAborts(app);
  // 2. 上傳中關掉分頁
  await chooseFiles(app, id, [fileOf('關分頁.txt', '關掉分頁時還在上傳')]);
  const during2 = await untilChips(app, id, allState('uploading', 1), 3000);
  await page.hover('.tab[data-key="' + id + '"]');
  await page.click('.tab[data-key="' + id + '"] .tab-x');
  const tabGone = await until(() => page.evaluate((sel) => (document.querySelector(sel) ? null : true), convSel(id)), 3000);
  await sleep(300);
  const abortTab = await xhrAborts(app);
  const adds = hub.cmds('addUploaded').length;
  await HP.closePage(app);
  // 3. 彈出視窗：伺服器關閉對話（conversationClosed；彈出視窗不關分頁，只換成說明）
  const pop = await openUploadConv('review-upload-cancel-popout', { uploads: { hold: never }, open: { url: HP.ORIGIN + '/app/?mode=popout&conversation=' + id } });
  await pop.page.evaluate(ABORT_PATCH);
  await chooseFiles(pop, id, [fileOf('伺服器關閉.txt', '伺服器關閉對話時還在上傳')]);
  const during3 = await untilChips(pop, id, allState('uploading', 1), 3000);
  pop.hub.push({ type: 'conversationClosed', conversationId: id });
  const closedNote = await until(() => pop.page.evaluate((sel) => { const n = document.querySelector(sel + ' .conv-closed'); return n ? n.textContent : null; }, convSel(id)), 3000);
  await sleep(300);
  const abortClosed = await xhrAborts(pop);
  const adds3 = pop.hub.cmds('addUploaded').length;
  await HP.closePage(pop);
  check('hubUploadCancel', !!during && during.chips[0].x && xTitle === '取消上傳' && !!gone && gone.gate === null && abortX === 1
    && !!during2 && !!tabGone && abortTab === 2 && adds === 0 && !!during3 && !!closedNote && abortClosed === 1 && adds3 === 0,
  { chip: { during: during && during.chips, xTitle, gone: !!gone, gate: gone && gone.gate, aborts: abortX }, closeTab: { during: !!during2, tabGone: !!tabGone, aborts: abortTab, adds },
    serverClosed: { during: !!during3, note: closedNote, aborts: abortClosed, adds: adds3 } });
}

/** 「@」檔案提示：說明（不能搜尋時）、空白列的字、底列 */
const suggestState = (app, id) => app.page.evaluate((sel) => {
  const s = document.querySelector(sel + ' .suggest');
  if (!s || s.hidden) return null;
  const err = s.querySelector('[data-field="searchError"]');
  const empty = s.querySelector('.sg-empty');
  return { error: err ? err.textContent : null, empty: empty ? empty.textContent : null, foot: (s.querySelector('.sg-foot') || {}).textContent || null };
}, convSel(id));
const untilSuggestError = (app, id, ms = 3000) => until(async () => { const s = await suggestState(app, id); return s && s.error ? s : null; }, ms);

async function runReviewFileSearch() {
  // 1. 伺服器草稿：不送 fileSearch（一定被拒），說明送出第一則之後才能搜尋；Enter 照常送出（P2）
  const a = await openWithLayout('review-file-search-draft', layoutOf([[[LF.DRAFT], LF.DRAFT]]));
  await waitLoaded(a, [LF.DRAFT]);
  await a.page.fill(taSel(LF.DRAFT), '@abc');
  const sd = await untilSuggestError(a, LF.DRAFT, 2000);
  await sleep(400);
  const searchesA = a.hub.cmds('fileSearch').length;
  await a.page.press(taSel(LF.DRAFT), 'Enter');
  const sendA = await until(() => a.hub.cmds('send')[0] || null, 2000);
  check('hubFileSearchDraft', !!sd && sd.error === SEARCH_DRAFT_TEXT && sd.foot === 'Enter 送出訊息　Esc 關閉' && searchesA === 0 && !!sendA && sendA.cmd.text === '@abc',
    { suggest: sd || (await suggestState(a, LF.DRAFT)), searches: searchesA, send: sendA && sendA.cmd });
  await HP.closePage(a);
  // 2. 被拒（政策、電腦離線…；假 hub 回 invalid）：說明、Enter 照常送出；3. 結果不明（150 秒過期）：說明請再輸入一次
  const id = LF.CONV.idle;
  const b = await openWithLayout('review-file-search-rejected', layoutOf([[[id], id]]));
  await waitLoaded(b, [id]);
  b.hub.onCmd('fileSearch', () => ({ error: 'invalid', push: [] }));
  b.hub.onCmd('send', (cmd, arg) => ({ push: [serverResult(arg.commandId, 'done')] }));
  await b.page.fill(taSel(id), '@abc');
  const sr = await untilSuggestError(b, id);
  const searchesB = b.hub.cmds('fileSearch').length;
  await b.page.press(taSel(id), 'Enter');
  const sendB = await until(() => b.hub.cmds('send')[0] || null, 2000);
  await until(async () => { const c = await composerState(b, id); return c && c.text === '' ? true : null; }, 2000);
  b.hub.onCmd('fileSearch', () => ({ push: [] })); // 接受、不回
  await b.page.fill(taSel(id), '@xyz');
  const loading = await until(async () => { const s = await suggestState(b, id); return s && s.empty === '搜尋檔案中…' ? s : null; }, 2000);
  // 等 fileSearch 真的送出（搜尋有 120ms 的防抖）、登記追蹤之後才把時鐘往後推
  await until(() => (b.hub.cmds('fileSearch').length > searchesB ? true : null), 2000);
  await sleep(200);
  await HP.advanceClock(b, 151000);
  const su = await untilSuggestError(b, id, 4000);
  check('hubFileSearchRejected', !!sr && sr.error === SEARCH_REJECTED_TEXT && searchesB === 1 && !!sendB && sendB.cmd.text === '@abc'
    && !!loading && !!su && su.error === SEARCH_UNKNOWN_TEXT,
  { rejected: sr || null, searches: searchesB, send: sendB && sendB.cmd, loading: !!loading, unknown: su || (await suggestState(b, id)) });
  await HP.closePage(b);
}

async function runReviewPickerCrumbs() {
  // 資料夾挑選器的路徑列：最近資料夾與工作資料夾以外的上層（C:\、C:\repo）不給點（電腦一定拒絕），範圍內的照樣可以點（P4）
  const id = LF.CONV.idle;
  const app = await openWithLayout('review-picker-crumbs', layoutOf([[[id], id]]));
  const { hub, page } = app;
  await waitLoaded(app, [id]);
  serveListFiles(hub);
  await page.click(convSel(id) + ' .ch-folder');
  await page.waitForSelector('.menu');
  await clickMenu(app, '瀏覽資料夾…');
  await until(async () => { const d = await drawerState(app, id); return d && d.open && d.names.length === 3 ? d : null; }, 3000);
  await clickEntry(app, id, 'C:\\repo\\alpha');
  await until(async () => { const d = await drawerState(app, id); return d && d.pickPath === 'C:\\repo\\alpha' && d.names.length === 2 ? d : null; }, 3000);
  await clickEntry(app, id, 'src');
  const deep = await until(async () => { const d = await drawerState(app, id); return d && d.pickPath === 'C:\\repo\\alpha\\src' ? d : null; }, 3000);
  const crumbs = await page.evaluate((sel) => [...document.querySelectorAll(sel + ' .drawer .dr-crumbs .cr')]
    .map((b) => ({ label: b.textContent, disabled: b.disabled, out: b.classList.contains('out'), cur: b.classList.contains('cur') })), convSel(id));
  const clickCrumb = (labels) => page.evaluate(([sel, ls]) => { for (const b of document.querySelectorAll(sel + ' .drawer .dr-crumbs .cr')) if (ls.includes(b.textContent)) b.click(); }, [convSel(id), labels]);
  const n0 = hub.cmds('listFiles').length;
  // 範圍外的上層：點了不送 listFiles（停用的按鈕不觸發 click）
  await clickCrumb(['C:', 'repo']);
  await sleep(300);
  const outLists = hub.cmds('listFiles').slice(n0).map((c) => c.cmd.path);
  // 範圍內的上層（C:\repo\alpha）照樣可以點
  await clickCrumb(['alpha']);
  const inList = await until(() => hub.cmds('listFiles')[n0 + outLists.length] || null, 2000);
  const want = [
    { label: '最近的資料夾', disabled: false, out: false, cur: false },
    { label: 'C:', disabled: true, out: true, cur: false },
    { label: 'repo', disabled: true, out: true, cur: false },
    { label: 'alpha', disabled: false, out: false, cur: false },
    { label: 'src', disabled: false, out: false, cur: true },
  ];
  check('hubDirsPickerCrumbsScope', !!deep && JSON.stringify(crumbs) === JSON.stringify(want) && outLists.length === 0
    && !!inList && inList.cmd.path === 'C:\\repo\\alpha' && inList.cmd.dirsOnly === true,
  { crumbs, outLists, inList: inList && inList.cmd });
  await HP.closePage(app);
}

/** live-collab.mjs 的情境用的共用函式（H 在 main 裡才建好：取用時才讀） */
const collabCtx = () => ({ H, HP, NOW, check, shot, sleep, until, convSel, openMore, closeMenu, clickMenu, serveOpen });

const SCENARIOS = [
  { checks: ['collabChrome', 'collabAuthorLabels', 'collabMenusLocked', 'collabNoNewConversation', 'collabSettingsTabs', 'collabMyAccount', 'collabNoErrors'], run: () => runCollaboratorView(collabCtx()) },
  { checks: ['ownerCollabChipAndAuthor', 'ownerShareDialog', 'ownerCollabChipUpdates', 'ownerCreateCollaborator', 'ownerCollabNoErrors'], run: () => runOwnerCollaborators(collabCtx()) },
  { checks: ['harnessSmoke'], run: runHarnessSmoke },
  { checks: ['harnessDesktopSmoke'], run: runHarnessDesktopSmoke },
  { checks: ['fakeHubOnCmd'], run: runFakeHubOnCmd },
  { checks: ['harnessControls'], run: runHarnessControls },
  { checks: ['thPostReturnsCommandId', 'thPostOfflineReturnsNull'], run: runThPostReturns },
  { checks: ['thTooLargeReturnsNull'], run: runThTooLarge },
  { checks: ['thSynthRejectedOnInvalid'], run: runThSynthRejected },
  { checks: ['thSynthRateLimitGiveUp'], run: runThSynthRateLimit },
  { checks: ['thRateLimitedThenReconnect_合成not_sent'], run: runThRateLimitedReconnect },
  { checks: ['thDropPendingSynthNotSent'], run: runThDropPending },
  { checks: ['thClientStateCarriesVisible', 'thVisibleUpdatesClientState'], run: runThClientState },
  { checks: ['thMediaLinkNewTab'], run: runThMediaLink },
  { checks: ['hubRestoreLayout', 'hubClientStateOpenIds', 'hubChat1440NoHScroll'], run: runRestoreLayout },
  { checks: ['hubLoadRetry'], run: runLoadRetry },
  { checks: ['hubHeaderListsRecover'], run: runHeaderListsRecover },
  { checks: ['hubHeaderSlimBatchKeepsLists'], run: runHeaderSlimBatch },
  { checks: ['hubRestoreKeepsUnlistedConversation'], run: runRestoreUnlisted },
  { checks: ['hubProviderAccountCards', 'hubProviderConversations', 'hubProviderMatrixRows', 'hubProvidersNoHScroll'], run: runProviderCards },
  { checks: ['hubNoAutoDraft'], run: runNoAutoDraft },
  { checks: ['hubOpenFromSidebar', 'hubDeviceBadgeXss', 'hubStatusOfflineNoSpinner', 'hubSearchContent'], run: runOpenFromSidebar },
  { checks: ['hubLoadingText'], run: runLoadingText },
  { checks: ['hubReinitReopens', 'hubVisibleResentAfterInit', 'hubDraftKeptAfterInit'], run: runReinit },
  { checks: ['hubDraftRecreatedAfterServerRestart'], run: runDraftRecreate },
  { checks: ['hubConversationClosedClosesPane'], run: runClosedClosesPane },
  { checks: ['hubChat390NoHScroll', 'hubPhoneNoPageZoom'], run: runChat390 },
  { checks: ['hubMobileComposer', 'hubMobileEnterNewline'], run: runMobileComposer },
  { checks: ['hubMobileLightboxTouch'], run: runMobileLightbox },
  { checks: ['hubTabletLightboxTouch'], run: runTabletLightbox },
  { checks: ['hubTabletAcctsFolded', 'hubTabletSwipePanels'], run: runTabletPanels },
  { checks: ['desktopTabletAcctsFolded', 'desktopTouchSwipePanels'], run: runDesktopTouchPanels },
  { checks: ['desktopLightboxTouch'], run: runDesktopLightboxTouch },
  { checks: ['hubMobileLightboxSwipeMenu'], run: runMobileLightboxSwipeMenu },
  { checks: ['desktopLightboxSwipeMenu'], run: runDesktopLightboxSwipeMenu },
  { checks: ['hubMobileMediaLongPress', 'hubMobileVideoSwipe'], run: runMobileMediaTouch },
  { checks: ['desktopMediaContextMenu', 'desktopViewerCloseTopRight'], run: runDesktopMediaMenu },
  { checks: ['hubPhoneAcctsFolded', 'hubPhoneSwipeLeftCloses', 'hubPhoneSwipeLeftMonitor', 'hubPhoneSwipeIgnored', 'hubPhoneDragFollowsFinger', 'hubPhoneSwipeRightOpens', 'hubPhoneTapChatCloses', 'hubPhonePickCloses', 'hubPhoneDrawerNoHScroll', 'hubPhoneSearchBottomLeft', 'hubPhoneWideUnchanged', 'hubPhoneBrandRunMatches'], run: runPhoneDrawer },
  { checks: ['hubSendTrackedDone', 'hubRttReadout'], run: runSendDone },
  { checks: ['hubSendRejectedRestores'], run: runSendRejected },
  { checks: ['hubSendRateLimitedGiveUpRestores'], run: runSendRateLimited },
  { checks: ['hubSendNotSentRestores'], run: runSendNotSent },
  { checks: ['hubSendOfflineKeepsText'], run: runSendOffline },
  { checks: ['hubSendDisabledDeviceOffline', 'hubSendDisabledPolicyView'], run: runSendGates },
  { checks: ['hubFullAccessBlocked'], run: runFullAccess },
  { checks: ['hubAccountSwitchWhileRunning'], run: runHubAccountWhileRunning },
  { checks: ['hubModelSwitchWhileRunning'], run: runHubModelWhileRunning },
  { checks: ['hubApproveCarriesDigest', 'hubAnswerCarriesDigest'], run: runCardDigests },
  { checks: ['hubSkipNoDigest'], run: runSkipNoDigest },
  { checks: ['hubApproveUnknownUnlocks'], run: runApproveUnknown },
  { checks: ['hubCardRejectedUnlocks'], run: runCardRejected },
  { checks: ['hubSweepWhileHidden'], run: runSweepHidden },
  { checks: ['hubQueueMoreNoCancel'], run: runQueueMore },
  { checks: ['hubPickDeviceSingleOnline'], run: runPickSingle },
  { checks: ['hubPickDeviceAsk'], run: runPickAsk },
  { checks: ['hubPickDeviceNone'], run: runPickNone },
  { checks: ['hubDraftChipFromHeader'], run: runDraftChip },
  { checks: ['hubDraftSwitchDeviceKeepsOptions'], run: runDraftSwitch },
  { checks: ['hubDraftReplacedBy'], run: runDraftReplaced },
  { checks: ['hubDraftReplacedByAlreadyOpen'], run: runDraftReplacedOpen },
  { checks: ['hubDeleteConfirmTrash'], run: runDeleteTrash },
  { checks: ['hubControlDefaultPasswordBanner', 'hubControlFalseHidesBanner', 'hubNotice390NoHScroll'], run: runControlBanner },
  { checks: ['hubNotifyHiddenSystem', 'hubNotifyVisibleToast', 'hubNotifyOffNone', 'hubNotifyPermissionButton'], run: runNotify },
  { checks: ['hubTabBadge'], run: runTabBadge },
  { checks: ['hubSpeak'], run: runSpeak },
  { checks: ['hubVoiceMessage'], run: runVoiceMessage },
  { checks: ['hubVoiceDictation', 'hubVoiceDictationPreview'], run: runVoiceDictation },
  { checks: ['hubVoiceDictationRetap', 'hubVoiceDictationSpeech', 'hubVoiceDictationSilent'], run: runVoiceDictationRestart },
  { checks: ['hubVoiceDictationIosText', 'hubVoiceDictationIosDeaf'], run: runVoiceDictationIos },
  { checks: ['hubVoiceDictationIosRepeat', 'hubVoiceDictationIosRecover'], run: runVoiceDictationIosRepeat },
  { checks: ['hubVoiceDictationMeter'], run: runVoiceDictationMeter },
  { checks: ['hubPaneZoom'], run: runPaneZoom },
  { checks: ['hubComposerDraft'], run: runComposerDraft },
  { checks: ['hubQuickChat'], run: runQuickChat },
  { checks: ['hubComposerDraftSentStaysCleared'], run: runComposerDraftSent },
  { checks: ['hubVoiceDictationDiag', 'hubVoiceDictationDiagMode'], run: runVoiceDictationDiag },
  { checks: ['hubResyncReopens', 'hubResyncNullNoop'], run: runResync },
  { checks: ['hubClipboardWrite', 'hubClipboardDeniedFallback', 'hubClipboardUnsolicitedIgnored', 'hubClipboardExpiredIgnored'], run: runClipboard },
  { checks: ['hubCodeCopyFallback'], run: runCodeCopyFallback },
  { checks: ['hubBuildChangedAutoReload', 'hubBuildChangedBanner', 'hubBuildSameNoBanner', 'hubBuildMissingAfterRollback', 'hubBuildReloadOncePerMinute'], run: runBuild },
  { checks: ['hubBuildDeferredReloadKeepsDraft', 'hubBuildHiddenReloadsNow', 'hubBuildDeferredAfterMinute', 'hubBuildReloadRecreatesDraft'], run: runBuildDeferred },
  { checks: ['desktopReplicaReadOnly', 'desktopReplicaNoAccountMissing', 'desktopReplicaNoDelete', 'desktopReplicaOpenInWeb', 'desktopReplicaOpenPathCarriesConversation', 'desktopReplicaNoHScroll'], run: runDesktopReplica },
  { checks: ['desktopPolicyMenuChoices', 'desktopLocalOnlyConfirm'], run: runDesktopPolicy },
  { checks: ['desktopBackfillRow', 'desktopRemoteSupportedNote', 'desktopControlConfirm', 'desktopNotifySwitch', 'desktopPolicyTexts', 'desktopSyncSettingsNoHScroll'], run: runDesktopSyncSettings },
  { checks: ['desktopAuditDialogPaging', 'desktopAuditLeds', 'desktopAuditExport', 'desktopAuditNoHScroll'], run: runDesktopAudit },
  { checks: ['desktopTrashRestore', 'desktopTrashUnpaired'], run: runDesktopTrash },
  { checks: ['desktopHistoryScope'], run: runDesktopHistoryScope },
  { checks: ['hubUploadProgressAndAddUploaded', 'hubUploadReadout', 'hubUploadChipsCountForSend'], run: runUploadBasic },
  { checks: ['hubUpload409', 'hubUpload413', 'hubUpload507', 'hubUploadTooLargeLocal', 'hubUploadAddRejected'], run: runUploadErrors },
  { checks: ['hubUploadChipsKeptOnSendRejected'], run: runUploadKept },
  { checks: ['hubUploadChipsRestoredAfterReload', 'hubUploadChipsClearedOnClose'], run: runUploadRestore },
  { checks: ['hubUploadBlocksBuildReload'], run: runUploadBlocksReload },
  { checks: ['hubDraftAttachDisabled'], run: runDraftAttach },
  { checks: ['hubDirsOnlyPicker', 'hubDirsOnlyNeedsControl'], run: runDirsPicker },
  { checks: ['hubDraftDirsPicker', 'hubDraftDirsPickerOldDevice'], run: runDraftDirsPicker },
  { checks: ['hubFileFetchDialog', 'hubFileRefErrorFixedText', 'hubFileRefAbsoluteRejected', 'hubOfficeFileDownloads'], run: runFileFetch },
  { checks: ['hubVideoPolicyOff'], run: runVideoPolicyOff },
  { checks: ['desktopReplicaMedia'], run: runDesktopReplicaMedia },
  { checks: ['hubLongTasks'], run: runLongTasks },
  { checks: ['hubLoadChunkedOrder'], run: runLoadChunked },
  { checks: ['hubClearHidesEarlier'], run: runClearHidesEarlier },
  { checks: ['hubLastPromptSeekOlder'], run: runLastPromptSeek },
  { checks: ['hubItemPatchStream'], run: runItemPatchStream },
  { checks: ['hubScreens'], run: runHubScreens },
  // 合併前審查的回歸
  { checks: ['hubCardGateUnlistedOnLoad'], run: runReviewCardGateUnlisted },
  { checks: ['hubOlderStaleIgnored'], run: runReviewOlderStale },
  { checks: ['hubMobileItemsFullHeight', 'hubMobileLazyLoad', 'hubTouchUnsticks', 'hubTouchOlderDeferred', 'hubMobileSafeArea'], run: runMobileScrollSteady },
  { checks: ['hubMobileRunningSteady'], run: runMobileRunningScroll },
  { checks: ['hubUnsolicitedConversationAfterClose'], run: runReviewUnsolicitedAfterClose },
  { checks: ['hubCardTextsKeptOnReload'], run: runReviewCardTexts },
  { checks: ['hubCardInputBlocksBuildReload'], run: runReviewCardInputBlocksReload },
  { checks: ['hubListFilesUnknownOnReinit'], run: runReviewListFilesUnknown },
  { checks: ['hubFileFetchUnknownOnReinit'], run: runReviewFileFetchUnknown },
  { checks: ['desktopReplicaTabRunning', 'desktopReplicaConvRunning'], run: runReviewReplicaTabRunning },
  { checks: ['desktopPeerReplicaFolderShown', 'desktopPeerReplicaFolderOwnerRunning'], run: runPeerReplicaFolder },
  { checks: ['desktopPeerDraftFolderMenu', 'desktopPeerDraftFolderChosen', 'desktopPeerDraftFolderCreating'], run: runPeerDraftFolder },
  { checks: ['desktopPeerReplicaModelMenu', 'desktopPeerReplicaModelRevert'], run: runPeerReplicaModel },
  { checks: ['desktopPeerReplicaModelWhileRunning'], run: runPeerReplicaModelWhileRunning },
  { checks: ['desktopPeerReplicaAccountMenu', 'desktopPeerReplicaAccountWhileRunning'], run: runPeerReplicaAccount },
  { checks: ['hubUploadCancel'], run: runReviewUploadCancel },
  { checks: ['hubFileSearchDraft', 'hubFileSearchRejected'], run: runReviewFileSearch },
  { checks: ['hubDirsPickerCrumbsScope'], run: runReviewPickerCrumbs },
];

let exitCode = 1;
try {
  browser = await chromium.launch({ channel: 'chrome', headless: !HEADED });
  report.chrome = browser.version();
  H = HP.createPages(browser, { now: NOW });
  for (const s of SCENARIOS) {
    if (!s.checks.some(onlyMatch)) continue;
    await s.run();
  }
  const all = [];
  for (const app of H.pages) {
    for (const m of app.console) {
      const expected = app.expected.find((x) => (typeof x.pattern === 'function' ? x.pattern(m) : x.pattern.test(m.text)));
      if (expected) report.expectedConsole.push({ page: app.name, ...m, why: expected.why });
      else if (m.type === 'error' || m.type === 'pageerror') all.push({ page: app.name, ...m });
      else report.notes.push({ page: app.name, ...m });
    }
  }
  report.consoleErrors = all;
  // 所有頁面（伺服器模式與桌面）：CSP 0 違規、主控台只有各情境登記的預期錯誤
  const violations = H.pages.flatMap((a) => a.csp.map((v) => Object.assign({ page: a.name }, v)));
  report.checks.liveCspNoViolations = H.pages.length > 0 && violations.length === 0;
  report.details.liveCspNoViolations = { pages: H.pages.map((a) => a.name), violations };
  console.log(`[${report.checks.liveCspNoViolations ? '通過' : '失敗'}] liveCspNoViolations ${brief({ pages: H.pages.length, violations: violations.length })}`);
  report.checks.liveNoConsoleErrors = all.length === 0;
  report.details.liveNoConsoleErrors = { unexpected: all, expected: report.expectedConsole };
  console.log(`[${report.checks.liveNoConsoleErrors ? '通過' : '失敗'}] liveNoConsoleErrors ${brief({ unexpected: all.length, expected: report.expectedConsole.length })}`);
  const bools = Object.values(report.checks);
  report.allPassed = bools.length > 2 && bools.every(Boolean);
  exitCode = report.allPassed ? 0 : 1;
} catch (e) {
  report.fatal = String((e && e.stack) || e);
  exitCode = 2;
} finally {
  // 先寫結果，再關瀏覽器（關閉加時限）
  fs.writeFileSync(path.join(OUT, 'live-report.json'), JSON.stringify(report, null, 2), 'utf8');
  const failed = Object.entries(report.checks).filter(([, v]) => !v).map(([k]) => k);
  console.log(JSON.stringify({ outDir: OUT, allPassed: report.allPassed, checks: Object.keys(report.checks).length, failed, consoleErrors: report.consoleErrors.length, fatal: report.fatal || null }, null, 2));
  if (H) await Promise.race([H.closeAll(), sleep(20000)]);
  if (browser) await Promise.race([browser.close().catch(() => {}), sleep(15000)]);
  process.exit(exitCode);
}
