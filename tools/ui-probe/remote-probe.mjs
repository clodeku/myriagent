// 伺服器模式的介面探針（WP3.5；計畫 docs/plans/2026-10-16-wp3.5-web-transport.md 步驟 6 起；步驟 7 連線、步驟 8 指令層、步驟 9～11 儀表板與桌面的同步畫面、
// 步驟 12 登入頁的配對核准頁與登入後導向）：
// 用 Playwright（本機 Chrome、無頭、暫存設定檔）在 https://hub.probe.local 以 context.route 供應「伺服器的 /app/」——
// src/Myriagent/Web/ 的檔案、照 AssetServerRules.InjectScripts 插入的真的 SignalR 程式庫與真的 transport-hub.js（src/MyriagentHub/wwwroot），
// 帶伺服器版的 CSP；hub 由 fake-signalr.mjs 在頁內假造（routeWebSocket），擁有者 HTTP 端點（/api/v1/…）是可控制的假回應。
// 桌面情境（已配對的 sync／devices）另用一份最小的 WebView2 模擬，在 https://myriagent.local 供應。
// 結果寫 out-remote/remote-report.json（先寫結果再關瀏覽器），截圖也放在 out-remote/；開頭只清空 out-remote/，不碰 probe.mjs 的 out/。
//
// 用法：cd tools/ui-probe && npm install && node remote-probe.mjs
//   只跑名稱開頭符合的檢查：node remote-probe.mjs --only hub（多個前綴以逗號分隔：--only revoke,desktop）
//   顯示瀏覽器視窗：node remote-probe.mjs --headed
import { chromium } from 'playwright';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { FakeSignalRHub } from './fake-signalr.mjs';
import * as RF from './remote-fixtures.mjs';
import { baseHeader } from './fixtures.mjs';
// 〔遠端〕WP4.8 步 9：瀏覽器版的帳戶與 CLI 操作（夾具與檢查都在那個檔，這裡只組 ctx、加一列 SCENARIOS）
import { runRemoteAccounts, REMOTE_ACCOUNT_CHECKS } from './remote-accounts-scenarios.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const REPO = path.resolve(here, '..', '..');
const WEB = path.join(REPO, 'src', 'Myriagent', 'Web');
const WWWROOT = path.join(REPO, 'src', 'MyriagentHub', 'wwwroot');
const OUT = path.join(here, 'out-remote');
const ORIGIN = RF.ORIGIN;
const DESKTOP_ORIGIN = RF.DESKTOP_ORIGIN;
const HUB_URL = 'wss://hub.probe.local/hubs/v1/browser';
// 伺服器的 CSP：SecurityHeaderRules.ContentSecurityPolicy("https://hub.probe.local")（src/MyriagentHub/Hosting/SecurityHeaders.cs）。
// 這一行要和伺服器的結果逐字相同（tests/MyriagentHub.Tests/Web/TransportHubFileTests 的 remote_probe的CSP與伺服器相同）
const HUB_CSP = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; media-src 'self' blob:; font-src 'self' data:; connect-src 'self' wss://hub.probe.local; manifest-src 'self'; base-uri 'none'; form-action 'self'; frame-src 'self'; frame-ancestors 'none'; object-src 'none'; worker-src 'none'";
// 桌面宿主回應 index.html 時帶的 CSP（同 probe.mjs）
const DESKTOP_CSP = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; media-src 'self' blob:; " +
  "font-src 'self' data:; connect-src 'self'; frame-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'; object-src 'none'";
// index.html 載入網頁主程式的那一行（AssetServerRules.Anchor）：傳輸腳本插在它前面
const ANCHOR = '<script type="module" src="app.js"></script>';
const MIME = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.svg': 'image/svg+xml', '.png': 'image/png', '.json': 'application/json; charset=utf-8', '.txt': 'text/plain; charset=utf-8' };
// --only 可以給多個前綴，以逗號分隔（例如 --only revoke,devices）
const ONLY = (() => { const i = process.argv.indexOf('--only'); return i > 0 ? String(process.argv[i + 1] || '').split(',').filter(Boolean) : null; })();
const onlyMatch = (name) => !ONLY || ONLY.some((p) => name.startsWith(p));
const HEADED = process.argv.includes('--headed');
const NOW = Date.now();
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

fs.rmSync(OUT, { recursive: true, force: true });
fs.mkdirSync(OUT, { recursive: true });

const report = {
  generatedAt: new Date().toISOString(), chrome: null, webDir: WEB, wwwroot: WWWROOT, outDir: OUT,
  checks: {}, details: {}, screenshots: [], consoleErrors: [], expectedConsole: [], notes: [],
};
function check(name, ok, detail) {
  if (!onlyMatch(name)) return;
  report.checks[name] = !!ok;
  if (detail !== undefined) report.details[name] = detail;
  console.log(`[${ok ? '通過' : '失敗'}] ${name}`);
}

async function until(fn, ms = 4000, step = 40) {
  const t0 = Date.now();
  while (Date.now() - t0 < ms) {
    const v = await fn();
    if (v) return v;
    await sleep(step);
  }
  return null;
}

async function shot(page, name) {
  await sleep(200);
  const file = name + '.png';
  await page.screenshot({ path: path.join(OUT, file) });
  report.screenshots.push(file);
}

// ───── 伺服器的 /app/（照 src/MyriagentHub/Web/AssetServerRules.cs）─────

/** wwwroot/lib 的 SignalR 程式庫（檔名動態讀，不寫死版本） */
function libraryNames() {
  return fs.readdirSync(path.join(WWWROOT, 'lib')).filter((n) => /^signalr-[0-9]+\.[0-9]+\.[0-9]+\.min\.js$/.test(n)).sort();
}

/** 照 AssetServerRules.InjectScripts：依序插在錨點前，縮排照錨點那一行；錨點不是正好一個就丟例外 */
function injectScripts(html, scripts) {
  const first = html.indexOf(ANCHOR);
  if (first < 0 || html.indexOf(ANCHOR, first + ANCHOR.length) >= 0) throw new Error('index.html 的 app.js 錨點應該正好一個');
  if (!scripts.length) return html;
  const lineStart = html.lastIndexOf('\n', Math.max(0, first - 1)) + 1;
  let indent = html.slice(lineStart, first);
  if (/[^ \t]/.test(indent)) indent = '';
  return html.slice(0, first) + scripts.map((s) => `<script src="${s}"></script>\n${indent}`).join('') + html.slice(first);
}

/** 伺服器插入的腳本：程式庫恰好一支而且有 transport-hub.js → [程式庫, transport-hub]（AssetServerRules.SelectInjected） */
function injectedScripts() {
  const libs = libraryNames();
  const hasTransport = fs.existsSync(path.join(WWWROOT, 'transport-hub.js'));
  if (libs.length !== 1 || !hasTransport) throw new Error(`wwwroot 的組合不合法：程式庫 ${libs.length} 支、transport-hub.js ${hasTransport ? '有' : '沒有'}`);
  return ['/lib/' + libs[0], '/transport-hub.js'];
}

// 「早到的訂閱者」：插在 transport-hub 之後、app.js 之前，模擬 WP3.4 的 settings.js 比 app.js 先訂閱
const EARLY_SUB = {
  record: `window.__earlySub = [];
window.myriagentHubTransport.onMessage(function (m) { window.__earlySub.push(JSON.parse(JSON.stringify(m))); });
`,
  throw: `window.__earlySub = [];
window.myriagentHubTransport.onMessage(function (m) { window.__earlySub.push(JSON.parse(JSON.stringify(m))); if (m.type === 'init') throw new Error('probe-early-sub 刻意丟出的例外：' + m.type); });
`,
};

const AUTH_PAGE = `<!doctype html>
<html lang="zh-Hant-TW"><head><meta charset="utf-8"><meta name="myriagenthub-page" content="auth"><title>Myriagent Hub</title><link rel="icon" href="data:,"></head>
<body><p id="probe-auth">登入頁（探針的替身）</p></body></html>`;

/** 擁有者 HTTP 端點的假回應：預設 auth/me 200；set(路徑, 回應或函式)、queue(路徑, [回應…])；回應 'network' 表示網路錯誤 */
class FakeApi {
  constructor() {
    this.requests = [];
    this.fixed = new Map();
    this.queues = new Map();
    this.fixed.set('/api/v1/auth/me', { status: 200, body: { name: 'admin', serverDomain: 'hub.probe.local', session: {} } });
  }

  set(p, r) { this.fixed.set(p, r); }

  queue(p, list) { this.queues.set(p, list.slice()); }

  count(p, method) { return this.requests.filter((r) => r.path === p && (!method || r.method === method)).length; }

  async handle(route, url) {
    const req = route.request();
    const rec = { at: Date.now(), method: req.method(), path: url.pathname, query: url.search, headers: req.headers(), body: req.postData() };
    this.requests.push(rec);
    let r = null;
    const q = this.queues.get(url.pathname);
    if (q && q.length) r = q.shift();
    else if (this.fixed.has(url.pathname)) r = this.fixed.get(url.pathname);
    else {
      for (const [k, v] of this.fixed) if (k.endsWith('*') && url.pathname.startsWith(k.slice(0, -1))) r = v;
    }
    if (typeof r === 'function') r = await r(rec);
    if (!r) r = { status: 404, body: { error: 'not_found' } };
    if (r === 'network') return route.abort('failed');
    rec.status = r.status;
    return route.fulfill({ status: r.status, headers: Object.assign({ 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store', 'Content-Security-Policy': HUB_CSP }, r.headers || {}), body: JSON.stringify(r.body === undefined ? {} : r.body) });
  }
}

function fileResponse(route, file, extraHeaders) {
  if (!fs.existsSync(file) || !fs.statSync(file).isFile()) return route.fulfill({ status: 404, headers: { 'Cache-Control': 'no-store' }, body: '' });
  const headers = Object.assign({ 'Content-Type': MIME[path.extname(file)] || 'application/octet-stream', 'Cache-Control': 'no-cache', 'Content-Security-Policy': HUB_CSP }, extraHeaders || {});
  return route.fulfill({ status: 200, headers, body: fs.readFileSync(file) });
}

/** 登入頁真的檔案（o.realAuth 時供應；其他情境的 / 是替身）；auth.js 會 import 版本頁的 releases.js、releaserules.js（U2） */
const AUTH_FILES = new Set(['/auth.js', '/auth.css', '/authrules.js', '/releases.js', '/releaserules.js']);

/** 伺服器的路由：/app/、/app/<名稱>（名稱含斜線一律 404）、/lib/*、/transport-hub.js、/、/api/v1/*；o.realAuth 時 / 與 auth.* 是 wwwroot 的真檔案 */
async function serveHub(route, o, api, seen) {
  const url = new URL(route.request().url());
  const p = url.pathname;
  seen.push(p);
  if (p.startsWith('/api/')) return api.handle(route, url);
  if (o.realAuth) {
    if (p === '/') return fileResponse(route, path.join(WWWROOT, 'auth.html'), { 'Cache-Control': 'no-store' });
    if (AUTH_FILES.has(p)) return fileResponse(route, path.join(WWWROOT, p.slice(1)));
    if (p === '/healthz') return route.fulfill({ status: 200, headers: { 'Content-Type': MIME['.json'], 'Cache-Control': 'no-store' }, body: JSON.stringify({ status: 'ok', build: 'probe00' }) });
  }
  if (p.startsWith('/hubs/')) return route.fulfill({ status: 404, headers: { 'Cache-Control': 'no-store' }, body: '' }); // negotiate：skipNegotiation 時不該有
  if (p === '/') return route.fulfill({ status: 200, headers: { 'Content-Type': MIME['.html'], 'Cache-Control': 'no-store', 'Content-Security-Policy': HUB_CSP }, body: AUTH_PAGE });
  if (p === '/transport-hub.js') return fileResponse(route, path.join(WWWROOT, 'transport-hub.js'));
  if (p === '/probe-early-sub.js' && o.earlySub) return route.fulfill({ status: 200, headers: { 'Content-Type': MIME['.js'], 'Cache-Control': 'no-store' }, body: EARLY_SUB[o.earlySub] });
  if (p.startsWith('/lib/')) {
    const name = p.slice('/lib/'.length);
    if (!name || /[\\/:]/.test(name) || (o.blockLibrary && name.startsWith('signalr-'))) return route.fulfill({ status: 404, headers: { 'Cache-Control': 'no-store' }, body: '' });
    return fileResponse(route, path.join(WWWROOT, 'lib', name), { 'Cache-Control': 'public, max-age=31536000, immutable' });
  }
  if (p.startsWith('/app/')) {
    let name = decodeURIComponent(p.slice('/app/'.length));
    if (name === '') name = 'index.html';
    if (name.startsWith('.') || /[\\/:]/.test(name)) return route.fulfill({ status: 404, headers: { 'Cache-Control': 'no-store' }, body: '' });
    if (name === 'app.js' && o.blockApp) return route.fulfill({ status: 404, headers: { 'Cache-Control': 'no-store' }, body: '' });
    if (name === 'index.html') {
      const scripts = o.inject === false ? [] : injectedScripts();
      if (o.earlySub && scripts.length) scripts.push('/probe-early-sub.js');
      const html = injectScripts(fs.readFileSync(path.join(WEB, 'index.html'), 'utf8'), scripts);
      return route.fulfill({ status: 200, headers: { 'Content-Type': MIME['.html'], 'Cache-Control': 'no-store', 'Content-Security-Policy': HUB_CSP }, body: html });
    }
    return fileResponse(route, path.join(WEB, name));
  }
  return route.fulfill({ status: 404, headers: { 'Cache-Control': 'no-store' }, body: '' });
}

/**
 * 每個頁面最早執行的探針腳本（CDP 注入，不受 CSP 影響）：CSP 違規收集器；可控制的 navigator.onLine、document.hidden／visibilityState、
 * document.hasFocus（改值＋dispatchEvent 模擬，不用 context.setOffline：routeWebSocket 是頁內模擬，影響不到它）；window.open 記錄器；
 * transport-hub 掛上全域時先註冊一個記錄訂閱者（window.__probeMsgs，記下頁面收到的每則訊息與順序）
 */
function probeInit(opts) {
  window.__csp = [];
  document.addEventListener('securitypolicyviolation', (e) => {
    const v = { directive: e.violatedDirective, blocked: e.blockedURI, source: e.sourceFile, line: e.lineNumber, href: location.href };
    window.__csp.push(v);
    // 導覽之後（例如被導到 /）也不會遺失：送回 Node 端累積
    if (typeof window.__probeCsp === 'function') window.__probeCsp(v);
  });
  window.__probeOnline = true;
  window.__probeHidden = false;
  window.__probeFocused = true;
  Object.defineProperty(Navigator.prototype, 'onLine', { configurable: true, get() { return window.__probeOnline !== false; } });
  Object.defineProperty(Document.prototype, 'hidden', { configurable: true, get() { return window.__probeHidden === true; } });
  Object.defineProperty(Document.prototype, 'visibilityState', { configurable: true, get() { return window.__probeHidden === true ? 'hidden' : 'visible'; } });
  Document.prototype.hasFocus = function () { return window.__probeFocused !== false; };
  window.__opened = [];
  window.open = function (...args) { window.__opened.push(args.map((a) => (a === undefined ? null : String(a)))); return null; };
  window.__probeMsgs = [];
  let transportHub;
  Object.defineProperty(window, 'myriagentHubTransport', {
    configurable: true,
    enumerable: true,
    get() { return transportHub; },
    set(v) {
      transportHub = v;
      try {
        if (v && typeof v.onMessage === 'function') v.onMessage((m) => window.__probeMsgs.push({ at: Date.now(), msg: JSON.parse(JSON.stringify(m)) }));
      } catch (e) {
        window.__probeRecorderError = String(e);
      }
    },
  });
  if (opts && opts.localStorageThrows) {
    Object.defineProperty(window, 'localStorage', { configurable: true, get() { throw new DOMException('探針：localStorage 被擋', 'SecurityError'); } });
  }
}

let browser;
const pages = [];

function watchPage(app) {
  const { page, name } = app;
  page.on('console', (m) => {
    if (m.type() === 'error' || m.type() === 'warning') app.console.push({ type: m.type(), text: m.text(), url: (m.location() && m.location().url) || '' });
  });
  page.on('pageerror', (e) => app.console.push({ type: 'pageerror', text: String((e && e.message) || e), url: '' }));
  page.on('framenavigated', (f) => { if (f === page.mainFrame()) app.navigations.push({ at: Date.now(), url: f.url() }); });
  app.expect = (pattern, why) => app.expected.push({ pattern, why });
  pages.push(app);
  return app;
}

/**
 * 開一個伺服器模式的頁面。o：vp（預設 1440×900）、url（預設 /app/）、realAuth（/ 用真的登入頁）、inject（false＝不插腳本）、blockLibrary（程式庫 404）、
 * earlySub（'record'｜'throw'）、initOpts（probeInit 的參數）、hub(hub)、api(api)（載入前調整）、waitReady（預設 true：等 #app.ready）、hasTouch（觸控螢幕）、
 * reducedMotion（'reduce'＝系統要求減少動態：主題直接切換、不跑 620ms 的擦拭過場——過場期間點不到下一個鈕）
 */
async function openHub(name, o = {}) {
  const vp = o.vp || { w: 1440, h: 900 };
  const context = await browser.newContext({ viewport: { width: vp.w, height: vp.h }, deviceScaleFactor: 1, colorScheme: 'dark', locale: 'zh-TW', timezoneId: 'Asia/Taipei', hasTouch: !!o.hasTouch,
    ...(o.reducedMotion ? { reducedMotion: o.reducedMotion } : {}) });
  const log = [];
  const hub = new FakeSignalRHub({ url: HUB_URL, init: (ready) => RF.browserInit(NOW, { mode: ready && ready.mode }), log });
  const api = new FakeApi();
  if (o.hub) o.hub(hub);
  if (o.api) o.api(api);
  await hub.attach(context);
  const seen = [];
  const csp = [];
  await context.route(ORIGIN + '/**', (route) => serveHub(route, o, api, seen));
  await context.exposeBinding('__probeCsp', (source, v) => { csp.push(v); });
  await context.addInitScript(probeInit, o.initOpts || {});
  const page = await context.newPage();
  const app = watchPage({ name, kind: 'hub', context, page, hub, api, seen, csp, log, console: [], expected: [], navigations: [] });
  await page.goto(o.url || ORIGIN + '/app/', { waitUntil: 'load' });
  if (o.waitReady !== false) {
    await page.waitForFunction(() => {
      const el = document.getElementById('app');
      return !!el && (el.classList.contains('ready') || !!document.querySelector('.no-host'));
    }, null, { timeout: 15000 }).catch(() => {});
  }
  return app;
}

// ───── 桌面情境：最小的 WebView2 模擬（照 probe.mjs 的 MOCK 形狀抄一份）─────

const MOCK = `(() => {
  const listeners = [];
  window.__posted = [];
  const clone = (m) => JSON.parse(JSON.stringify(m));
  window.chrome = window.chrome || {};
  window.chrome.webview = {
    postMessage(m) { const c = clone(m); window.__posted.push(c); if (window.__hostRecv) window.__hostRecv(c); },
    postMessageWithAdditionalObjects(m, objs) {
      const c = Object.assign(clone(m), { __files: Array.from(objs || []).map((f) => ({ name: f.name, size: f.size, type: f.type })) });
      window.__posted.push(c); if (window.__hostRecv) window.__hostRecv(c);
    },
    addEventListener(type, fn) { if (type === 'message') listeners.push(fn); },
    removeEventListener(type, fn) { const i = listeners.indexOf(fn); if (i >= 0) listeners.splice(i, 1); },
  };
  window.__host = (msg) => { for (const fn of listeners.slice()) fn({ data: msg }); };
})();`;

async function serveDesktop(route) {
  const url = new URL(route.request().url());
  const name = url.pathname === '/' ? 'index.html' : decodeURIComponent(url.pathname.slice(1));
  if (/[\\/]/.test(name)) return route.fulfill({ status: 404, body: 'not found' });
  const file = path.join(WEB, name);
  if (!fs.existsSync(file)) return route.fulfill({ status: 404, body: 'not found' });
  const headers = { 'Content-Type': MIME[path.extname(name)] || 'application/octet-stream', 'Cache-Control': 'no-store' };
  if (name === 'index.html') headers['Content-Security-Policy'] = DESKTOP_CSP;
  return route.fulfill({ status: 200, headers, body: fs.readFileSync(file) });
}

/** 桌面頁面：ready → o.init（預設未配對的桌面 init）；host.send(msg) 推宿主訊息、host.posted 是網頁送出的指令；o.onPost(msg, host) 可以回應其他指令 */
async function openDesktop(name, o = {}) {
  const vp = o.vp || { w: 1440, h: 900 };
  const context = await browser.newContext({ viewport: { width: vp.w, height: vp.h }, deviceScaleFactor: 1, colorScheme: 'dark', locale: 'zh-TW', timezoneId: 'Asia/Taipei' });
  const page = await context.newPage();
  const host = { posted: [], page, send: (msg) => page.evaluate((m) => window.__host(m), JSON.parse(JSON.stringify(msg))) };
  const initMsg = o.init || RF.desktopInit(NOW);
  const csp = [];
  await page.exposeBinding('__probeCsp', (source, v) => { csp.push(v); });
  await page.addInitScript(MOCK);
  await page.addInitScript(probeInit, {});
  await page.exposeFunction('__hostRecv', (msg) => {
    host.posted.push(msg);
    if (msg.cmd === 'ready') host.send(initMsg).catch(() => {});
    else if (o.onPost) o.onPost(msg, host);
  });
  await page.route(DESKTOP_ORIGIN + '/**', (route) => serveDesktop(route));
  const app = watchPage({ name, kind: 'desktop', context, page, host, csp, console: [], expected: [], navigations: [] });
  await page.goto(DESKTOP_ORIGIN + '/index.html');
  await page.waitForFunction(() => document.getElementById('app').classList.contains('ready'), null, { timeout: 10000 }).catch(() => {});
  return app;
}

async function closePage(app) {
  if (app.hub) app.hub.stop();
  await app.context.close().catch(() => {});
}

/** 這個頁面的主控台錯誤（error、pageerror；warning 另計）扣掉預期的 */
function unexpectedErrors(app) {
  return app.console.filter((m) => (m.type === 'error' || m.type === 'pageerror') && !app.expected.some((x) => (typeof x.pattern === 'function' ? x.pattern(m) : x.pattern.test(m.text))));
}

// ───── 步驟 6：探針本身與 ok:false 時的行為 ─────

async function runHarness() {
  const app = await openHub('harness');
  const { page, hub, api } = app;
  const scripts = await page.evaluate(() => [...document.scripts].map((s) => ({ src: s.getAttribute('src'), type: s.getAttribute('type') })));
  const lib = libraryNames()[0];
  check('harnessInjectionOrder', scripts.length === 3 && scripts[0].src === '/lib/' + lib && !scripts[0].type && scripts[1].src === '/transport-hub.js' && !scripts[1].type
    && scripts[2].src === 'app.js' && scripts[2].type === 'module', { scripts, library: lib });

  const loaded = await page.evaluate(() => ({
    version: window.myriagentHubTransport && window.myriagentHubTransport.version,
    ok: window.myriagentHubTransport && window.myriagentHubTransport.ok,
    frozen: Object.isFrozen(window.myriagentHubTransport),
    builder: typeof (self.signalR && self.signalR.HubConnectionBuilder),
    recorder: window.__probeRecorderError || null,
  }));
  check('harnessRulesLoaded', loaded.version === 1 && loaded.ok === true && loaded.frozen && loaded.builder === 'function' && !loaded.recorder, loaded);

  // ok 是 false（程式庫沒載入）：transport.kind 是 none，顯示 no-host、不連線、不問 auth/me
  const none = await openHub('harness-none', { blockLibrary: true });
  none.expect((m) => /Failed to load resource/.test(m.text) && /\/lib\/signalr-/.test(m.url), '刻意擋掉程式庫（404）');
  await sleep(800);
  const noneState = await none.page.evaluate(() => {
    const el = document.querySelector('.no-host');
    return { noHost: el ? el.textContent : null, ok: window.myriagentHubTransport && window.myriagentHubTransport.ok, signalR: typeof self.signalR };
  });
  check('harnessNoneShowsNoHost', (noneState.noHost || '').includes('要在 Clode Myriagent 裡開啟') && noneState.ok === false && none.hub.connections.length === 0
    && none.api.count('/api/v1/auth/me') === 0,
    Object.assign({ connections: none.hub.connections.length, authMe: none.api.count('/api/v1/auth/me'), requests: none.seen }, noneState));
  await shot(none.page, 'harness-none-1440');
  await closePage(none);

  // 假 hub 的自我測試：頁內以原生 WebSocket 連過去，握手回 {}、10 秒內收到 ping
  const self = await page.evaluate((url) => new Promise((resolve) => {
    const rs = String.fromCharCode(0x1e);
    const ws = new WebSocket(url);
    const out = { handshake: null, pingAfterMs: null, error: null };
    let t0 = 0;
    const timer = setTimeout(() => { out.error = '12 秒內沒有收到 ping'; ws.close(); resolve(out); }, 12000);
    ws.onopen = () => ws.send(JSON.stringify({ protocol: 'json', version: 1 }) + rs);
    ws.onerror = () => { out.error = 'WebSocket 錯誤'; };
    ws.onmessage = (e) => {
      for (const part of String(e.data).split(rs)) {
        if (!part) continue;
        const m = JSON.parse(part);
        if (out.handshake === null) { out.handshake = m; t0 = Date.now(); continue; }
        if (m.type === 6 && out.pingAfterMs === null) {
          out.pingAfterMs = Date.now() - t0;
          clearTimeout(timer);
          ws.close();
          resolve(out);
        }
      }
    };
  }), HUB_URL);
  check('harnessFakeHubHandshake', !!self.handshake && JSON.stringify(self.handshake) === '{}' && self.pingAfterMs !== null && self.pingAfterMs <= 10500 && !self.error,
    Object.assign({ hubConnections: hub.connections.length }, self));

  const violations = app.csp.slice();
  check('harnessCspNoViolations', violations.length === 0, { violations, csp: HUB_CSP });
  await closePage(app);
}

async function runDesktop() {
  const app = await openDesktop('desktop');
  const { page, host } = app;
  const ready = await page.evaluate(() => document.getElementById('app').classList.contains('ready'));
  const before = unexpectedErrors(app).length;
  // 沒有 remote（未配對）：不出現矩陣與各電腦 CLI（步驟 9）
  await showDashboard(page);
  const sections = await remoteSections(page);
  check('desktopUnchangedWithoutRemote', ready && before === 0 && host.posted[0] && host.posted[0].cmd === 'ready' && sections.dashboard && !sections.matrix && !sections.devicesCli,
    { ready, consoleErrors: unexpectedErrors(app), firstPost: host.posted[0] || null, sections });

  // 未配對啟動之後：開始配對 → 配對完成 → devices → 離線 → 各電腦 CLI；再一則已配對的 init 整份取代
  for (const m of RF.desktopSyncSequence(NOW)) {
    await host.send(m);
    await sleep(80);
  }
  await host.send(RF.desktopInit(NOW, { remote: RF.desktopRemote(NOW), devicesCli: RF.browserDevicesCli(NOW) }));
  await sleep(300);
  await host.send({ type: 'sync', sync: null });
  await sleep(150);
  const stillReady = await page.evaluate(() => document.getElementById('app').classList.contains('ready'));
  const errors = unexpectedErrors(app);
  check('desktopSyncMessagesNoError', stillReady && errors.length === 0, { stillReady, consoleErrors: errors, sent: RF.desktopSyncSequence(NOW).map((m) => m.type) });
  await shot(page, 'desktop-synced-1440');
  await closePage(app);
}

// ───── 步驟 7：連線核心與瀏覽器版外殼 ─────

const ULID_RE = /^[0-7][0-9A-HJKMNP-TV-Z]{25}$/;

/** 頁面收到的訊息（探針的記錄訂閱者，依順序） */
const probeMsgs = (page) => page.evaluate(() => window.__probeMsgs.map((x) => x.msg));
/** 傳輸層的狀態（唯讀副本） */
const tstatus = (page) => page.evaluate(() => (window.myriagentHubTransport ? Object.assign({}, window.myriagentHubTransport.status()) : null)).catch(() => null);

/** 畫面上與連線有關的狀態 */
function domState(page) {
  return page.evaluate(() => {
    const vis = (el) => {
      if (!el || el.hidden) return false;
      const cs = getComputedStyle(el);
      const r = el.getBoundingClientRect();
      return cs.display !== 'none' && cs.visibility !== 'hidden' && r.width > 0 && r.height > 0;
    };
    const $ = (sel) => document.querySelector(sel);
    const bar = $('.hub-status');
    const settings = $('#btnSettings');
    const dash = $('#dashboard');
    return {
      path: location.pathname,
      ready: !!$('#app') && $('#app').classList.contains('ready'),
      bar: bar && !bar.hidden ? bar.querySelector('.hub-status-text').textContent : null,
      barKind: bar && !bar.hidden ? bar.dataset.kind : null,
      retryVisible: !!bar && !bar.hidden && vis(bar.querySelector('.hub-status-retry')),
      dashInert: !!dash && dash.inert === true,
      // WP4.6 D12：第一則 init 之前工作區、側欄 inert，檢視切換停用
      wsInert: $('#workspace') ? $('#workspace').inert === true : null,
      sbInert: $('#sidebar') ? $('#sidebar').inert === true : null,
      switchDisabled: [...document.querySelectorAll('#viewSwitch [data-view]')].every((b) => b.disabled === true),
      dashboard: vis(dash),
      sidebar: vis($('#sidebar')),
      workspace: vis($('#workspace')),
      viewSwitch: vis($('#viewSwitch')),
      btnSidebar: vis($('#btnSidebar')),
      cliBadge: vis($('#cliBadge')),
      settingsDisabled: !!settings && settings.disabled === true,
      accountLink: vis($('.hub-account')),
      accounts: document.querySelectorAll('#dashboard article.acard').length,
      noHost: !!$('.no-host'),
      hubNote: $('.hub-note') && vis($('.hub-note')) ? $('.hub-note').textContent : null,
      authPage: !!$('meta[name="myriagenthub-page"][content="auth"]'),
    };
  }).catch((e) => ({ error: String(e) }));
}

/** 等到「連著而且這條連線收到過 init」、#app.ready、狀態列隱藏 */
async function waitLive(app, ms = 8000) {
  return until(async () => {
    const st = await tstatus(app.page);
    if (!st || st.state !== 'online' || st.hubInit !== true) return null;
    const d = await domState(app.page);
    return d.ready && d.bar === null ? d : null;
  }, ms, 50);
}

/** 第 n 條連線（1 起算）握手之後頁面送來的第一則呼叫 */
function firstInvocation(hub, connId) {
  const f = hub.frames.find((x) => x.dir === 'in' && x.conn === connId && x.obj.type === 1);
  return f ? f.obj : null;
}

function cmdShape(inv) {
  const arg = inv && inv.arguments && inv.arguments[0];
  const keys = arg ? Object.keys(arg) : [];
  return {
    target: inv && inv.target, keys, cmd: arg && arg.cmd, commandId: arg && arg.commandId,
    ok: !!inv && inv.target === 'Cmd' && keys.every((k) => k === 'commandId' || k === 'cmd') && ULID_RE.test((arg && arg.commandId) || ''),
  };
}

async function runHubStartOnReady() {
  // app.js 擋掉：沒有任何人送 ready → 傳輸層不連線、不問 auth/me；手動送第一則 ready 才開始
  const app = await openHub('hub-start', { blockApp: true, waitReady: false });
  app.expect((m) => /Failed to load resource/.test(m.text) && /\/app\/app\.js/.test(m.url), '刻意擋掉 app.js（404），沒有人送 ready');
  await sleep(1500);
  const beforeReady = { authMe: app.api.count('/api/v1/auth/me'), connections: app.hub.connections.length, status: await tstatus(app.page) };
  const postedAt = Date.now();
  await app.page.evaluate(() => window.myriagentHubTransport.post({ cmd: 'ready', mode: 'main' }));
  const conn = await until(() => app.hub.connections.find((c) => c.handshakeAt !== null && firstInvocation(app.hub, c.id)), 5000);
  const first = conn ? cmdShape(firstInvocation(app.hub, conn.id)) : null;
  const authAt = (app.api.requests.find((r) => r.path === '/api/v1/auth/me') || {}).at || null;
  check('hubStartsOnFirstReady', beforeReady.authMe === 0 && beforeReady.connections === 0 && beforeReady.status && beforeReady.status.state === null
    && !!conn && conn.openedAt >= postedAt && authAt !== null && authAt >= postedAt && authAt <= conn.openedAt
    && first && first.ok && first.cmd && first.cmd.cmd === 'ready' && first.cmd.mode === 'main',
    { beforeReady, connOpenedAfterPostMs: conn ? conn.openedAt - postedAt : null, authMeAfterPostMs: authAt ? authAt - postedAt : null, first });
  await closePage(app);
}

async function runHubAnonymous() {
  const app = await openHub('hub-anon', { waitReady: false, api: (a) => a.set('/api/v1/auth/me', { status: 401, body: { error: 'unauthorized' } }) });
  app.expect((m) => /Failed to load resource/.test(m.text) && /status of 401/.test(m.text) && /\/api\/v1\/auth\/me/.test(m.url), '匿名：傳輸層連線前問 auth/me 得到 401');
  const at = await until(async () => {
    const d = await domState(app.page);
    return d.path === '/' && d.authPage ? d : null;
  }, 4000, 50);
  const auth = app.api.requests.find((r) => r.path === '/api/v1/auth/me');
  const nav = app.navigations.find((n) => new URL(n.url).pathname === '/');
  const ms = auth && nav ? nav.at - auth.at : null;
  check('hubAnonymousRedirects', !!at && ms !== null && ms <= 2000 && app.hub.connections.length === 0 && app.api.count('/api/v1/auth/me') === 1,
    { at, authMeToRedirectMs: ms, connections: app.hub.connections.length, authMe: app.api.count('/api/v1/auth/me'), navigations: app.navigations });
  await closePage(app);
}

async function runHubBasics() {
  const app = await openHub('hub-basics', { waitReady: false, hub: (h) => h.holdInit(2000) });
  const { page, hub, api } = app;
  // online 之後、init 之前（假 hub 晚 2 秒才回 init）
  await until(() => page.evaluate(() => window.__probeMsgs.some((x) => x.msg.type === 'connection' && x.msg.state === 'online')), 6000, 30);
  const before = await domState(page);
  const live = await waitLive(app, 8000);
  await sleep(500);
  const after = await domState(page);
  const msgs = await probeMsgs(page);
  const iOnline = msgs.findIndex((m) => m.type === 'connection' && m.state === 'online');
  const iInit = msgs.findIndex((m) => m.type === 'init');
  check('hubSkipNegotiation', !app.seen.some((p) => p.includes('negotiate') || p.startsWith('/hubs/')) && hub.connections.length === 1, { requests: app.seen, connections: hub.connections.length });
  check('hubOnlineThenInit', iOnline >= 0 && iInit > iOnline && before.bar === '載入中…' && before.dashInert === true && !!live && after.bar === null && after.dashInert === false,
    { order: msgs.map((m) => m.type + (m.state ? ':' + m.state : '')), before: { bar: before.bar, dashInert: before.dashInert }, after: { bar: after.bar, dashInert: after.dashInert } });
  // WP4.6 D12：init 之前工作區與側欄 inert、檢視切換停用（儀表板也 inert）；init 之後依版面（這裡沒有存下的版面：儀表板，D10），不自動開草稿
  check('hubNoChromeBeforeInit', !before.sidebar && !before.workspace && before.viewSwitch && !before.cliBadge && before.settingsDisabled && before.dashInert
    && before.wsInert && before.sbInert && before.switchDisabled
    && !after.sidebar && !after.workspace && after.viewSwitch && after.dashboard && !after.cliBadge && !after.settingsDisabled && after.accountLink
    && !after.wsInert && !after.sbInert && !after.switchDisabled && hub.cmds('newConversation').length === 0,
    { before, after, newConversation: hub.cmds('newConversation').length });
  const csInv = hub.invocations('ClientState');
  const cs = csInv[0] && csInv[0].obj.arguments[0];
  const readyFrame = hub.frames.find((f) => f.dir === 'in' && f.obj.type === 1 && f.obj.target === 'Cmd' && f.obj.arguments[0].cmd.cmd === 'ready');
  check('hubClientStateShape', csInv.length === 1 && !!cs && JSON.stringify(Object.keys(cs)) === JSON.stringify(['visible', 'focused', 'openConversationIds', 'tzOffsetMinutes', 'features'])
    && JSON.stringify(cs.features) === JSON.stringify(['patch1'])
    && cs.visible === true && cs.focused === true && Array.isArray(cs.openConversationIds) && cs.openConversationIds.length === 0 && cs.tzOffsetMinutes === -480
    && !!readyFrame && csInv[0].at >= readyFrame.at && hub.frames.indexOf(csInv[0]) > hub.frames.indexOf(readyFrame),
    { count: csInv.length, clientState: cs, first: cmdShape(firstInvocation(hub, 1)) });

  // 同一條連線上第二則 init（伺服器的 resync）：整份取代
  const accounts = RF.browserAccounts(NOW);
  hub.push(RF.browserInit(NOW, { accounts: accounts.slice(0, 2) }));
  const resync = await until(async () => {
    const d = await domState(page);
    return d.accounts === 2 ? d : null;
  }, 3000);
  check('hubResyncInit', !!resync && hub.connections.length === 1 && after.accounts === accounts.length && resync.bar === null,
    { before: after.accounts, after: resync && resync.accounts, connections: hub.connections.length });

  // Ctrl+N（WP4.6）：瀏覽器版也開新草稿——先選電腦（D16），newConversation 一律帶 deviceId，檢視切到聊天。
  // RF.browserInit 沒有政策是 Control 的電腦（d_A 是 Off、d_B 是 View）：這裡自己組一份 init（同一條連線的 resync），
  // 把 d_A 改成 Control（帳戶 la_c1 在 d_A 已登入、在線）；不改 remote-fixtures.mjs
  const ctl = RF.browserInit(NOW, { accounts: accounts.slice(0, 2) });
  ctl.remote.devices = ctl.remote.devices.map((d) => (d.id === 'd_A' ? Object.assign({}, d, { remoteControlLevel: 'Control' }) : d));
  const inits = async () => (await probeMsgs(page)).filter((m) => m.type === 'init').length;
  const nInit = await inits();
  hub.push(ctl);
  await until(async () => ((await inits()) > nInit ? true : null), 3000);
  await sleep(200);
  const framesBefore = hub.invocations().length;
  await page.keyboard.press('Control+n');
  await sleep(500);
  const ctrlN = await domState(page);
  const newConv = hub.cmds('newConversation');
  const nc = newConv[0] && newConv[0].cmd;
  check('hubCtrlNNewDraft', newConv.length === 1 && !!nc && typeof nc.paneId === 'string' && nc.paneId !== '' && nc.deviceId === 'd_A' && nc.accountId === 'la_c1'
    && !ctrlN.dashboard && ctrlN.workspace,
    { framesBefore, newConversation: newConv.map((x) => x.cmd), dashboard: ctrlN.dashboard, workspace: ctrlN.workspace,
      newFrames: hub.invocations().slice(framesBefore).map((f) => f.obj.target + ':' + JSON.stringify(f.obj.arguments[0]).slice(0, 80)) });
  // 回到儀表板（下面以儀表板的帳戶卡數判斷 init 有沒有整份取代）
  await page.click('#viewSwitch [data-view="dashboard"]');
  await until(async () => ((await domState(page)).dashboard ? true : null), 2000);

  // 斷線 → reconnecting → online → ready 重送 → 新的 init（帳戶變 3 個）整份取代
  hub.onReady((ready) => RF.browserInit(NOW, { mode: ready.mode, accounts: accounts.slice(0, 3) }));
  const msgCount = (await probeMsgs(page)).length;
  hub.drop();
  const reinit = await until(async () => {
    const d = await domState(page);
    return d.accounts === 3 && d.bar === null ? d : null;
  }, 6000);
  const later = (await probeMsgs(page)).slice(msgCount).map((m) => m.type + (m.state ? ':' + m.state : ''));
  const second = hub.connections[1];
  const secondFirst = second ? cmdShape(firstInvocation(hub, second.id)) : null;
  check('hubReconnectReinit', !!reinit && JSON.stringify(later.slice(0, 3)) === JSON.stringify(['connection:reconnecting', 'connection:online', 'init'])
    && !!secondFirst && secondFirst.ok && secondFirst.cmd.cmd === 'ready' && secondFirst.cmd.mode === 'main' && hub.connections.length === 2,
    { after: later, secondFirst, connections: hub.connections.length, accounts: reinit && reinit.accounts });

  // 工作階段失效：斷線之後 auth/me 401 → 導到登入頁
  api.set('/api/v1/auth/me', { status: 401, body: { error: 'unauthorized' } });
  app.expect((m) => /Failed to load resource/.test(m.text) && /status of 401/.test(m.text) && /\/api\/v1\/auth\/me/.test(m.url), '工作階段失效：重連前問 auth/me 得到 401');
  const dropAt = Date.now();
  hub.drop();
  const expired = await until(async () => {
    const d = await domState(page);
    return d.path === '/' && d.authPage ? d : null;
  }, 5000, 50);
  check('hubSessionExpiredRedirect', !!expired && hub.connections.length === 2, { ms: expired ? Date.now() - dropAt : null, path: expired && expired.path, connections: hub.connections.length });
  await closePage(app);
}

async function runHubEarly() {
  const app = await openHub('hub-early', { earlySub: 'record' });
  const live = await waitLive(app);
  const got = await app.page.evaluate(() => window.__earlySub.map((m) => m.type + (m.state ? ':' + m.state : '')));
  const scripts = await app.page.evaluate(() => [...document.scripts].map((x) => x.getAttribute('src')));
  check('hubEarlySubscriber', !!live && got.includes('connection:online') && got.includes('init') && got.indexOf('connection:online') < got.indexOf('init')
    && scripts.indexOf('/probe-early-sub.js') === scripts.indexOf('/transport-hub.js') + 1 && scripts.at(-1) === 'app.js',
    { earlySubscriber: got, scripts, live: !!live });
  await closePage(app);
}

async function runHubThrows() {
  const app = await openHub('hub-throws', { earlySub: 'throw' });
  app.expect((m) => m.type === 'pageerror' && /probe-early-sub 刻意丟出的例外：init/.test(m.text), '先註冊的訂閱者刻意丟例外（transport-hub 以 reportError 報出）');
  const live = await waitLive(app);
  await sleep(300);
  const thrown = app.console.filter((m) => /probe-early-sub 刻意丟出的例外/.test(m.text));
  const got = await app.page.evaluate(() => window.__earlySub.map((m) => m.type));
  check('hubSubscriberThrows', !!live && live.accounts === RF.browserAccounts(NOW).length && thrown.length === 1 && thrown[0].type === 'pageerror' && got.includes('init'),
    { live: !!live, accounts: live && live.accounts, thrown, earlySubscriber: got });
  await closePage(app);
}

/** 讓連線斷掉、重試一直失敗，等到退避進入 5 秒那一階（attempt ≥ 4）；回傳當下的狀態 */
async function failUntilLongBackoff(app, failure) {
  app.api.set('/api/v1/auth/me', failure);
  app.hub.drop();
  return until(async () => {
    const st = await tstatus(app.page);
    return st && st.attempt >= 4 && st.state !== 'online' && st.retryAt && st.retryAt - Date.now() > 2500 ? st : null;
  }, 10000, 50);
}

async function runHubServerDown() {
  const app = await openHub('hub-down');
  app.expect((m) => /Failed to load resource/.test(m.text) && /status of 503/.test(m.text) && /\/api\/v1\/auth\/me/.test(m.url), '伺服器停機：auth/me 503（刻意）');
  await waitLive(app);
  const st = await failUntilLongBackoff(app, { status: 503, body: { error: 'unavailable' } });
  const d = await domState(app.page);
  const n0 = app.api.count('/api/v1/auth/me');
  const clickAt = Date.now();
  await app.page.click('.hub-status-retry');
  const hit = await until(() => app.api.count('/api/v1/auth/me') > n0, 1000, 20);
  const ms = hit ? Date.now() - clickAt : null;
  check('hubServerDownBackoff', !!st && /^重新連線中…（\d+ 秒後重試）$/.test(d.bar || '') && d.retryVisible && d.dashInert && !!hit && ms < 1000 && st.retryAt - clickAt > 2000,
    { status: st, bar: d.bar, retryVisible: d.retryVisible, dashInert: d.dashInert, authMeBefore: n0, retryNowMs: ms, waitLeftMs: st ? st.retryAt - clickAt : null });
  await shot(app.page, 'hub-server-down-1440');
  await closePage(app);
}

async function runHubOffline() {
  const app = await openHub('hub-offline');
  app.expect((m) => /Failed to load resource/.test(m.text) && /\/api\/v1\/auth\/me/.test(m.url), '網路中斷：auth/me 送不出去（刻意）');
  await waitLive(app);
  await app.page.evaluate(() => { window.__probeOnline = false; window.dispatchEvent(new Event('offline')); });
  const st = await failUntilLongBackoff(app, 'network');
  const d = await domState(app.page);
  app.api.set('/api/v1/auth/me', { status: 200, body: { name: 'admin' } });
  const t0 = Date.now();
  await app.page.evaluate(() => { window.__probeOnline = true; window.dispatchEvent(new Event('online')); });
  const live = await waitLive(app, 3000);
  const ms = live ? Date.now() - t0 : null;
  check('hubOfflineEvents', !!st && st.state === 'offline' && st.reason === 'offline' && d.bar === '離線：網路中斷' && !!live && ms < 1500,
    { status: st, bar: d.bar, reconnectMs: ms, connections: app.hub.connections.length });
  await closePage(app);
}

async function runHubVisibility() {
  const app = await openHub('hub-visibility');
  app.expect((m) => /Failed to load resource/.test(m.text) && /status of 503/.test(m.text) && /\/api\/v1\/auth\/me/.test(m.url), '伺服器停機：auth/me 503（刻意）');
  await waitLive(app);
  const st = await failUntilLongBackoff(app, { status: 503, body: { error: 'unavailable' } });
  app.api.set('/api/v1/auth/me', { status: 200, body: { name: 'admin' } });
  await app.page.evaluate(() => { window.__probeHidden = true; document.dispatchEvent(new Event('visibilitychange')); });
  await sleep(200);
  const n0 = app.api.count('/api/v1/auth/me');
  const t0 = Date.now();
  await app.page.evaluate(() => { window.__probeHidden = false; document.dispatchEvent(new Event('visibilitychange')); });
  const live = await waitLive(app, 3000);
  const ms = live ? Date.now() - t0 : null;
  check('hubVisibilityRetry', !!st && !!live && ms < 1500 && st.retryAt - t0 > 1500 && app.api.count('/api/v1/auth/me') === n0 + 1,
    { status: st, reconnectMs: ms, waitLeftMs: st ? st.retryAt - t0 : null, hiddenTriggered: n0 !== app.api.count('/api/v1/auth/me') - 1 });
  await closePage(app);
}

async function runHubWatchdog() {
  // 假 hub 收到 ready 不回 init（25 秒）：online 之後約 20 秒傳輸層自己關掉重連，新連線拿到 init
  const app = await openHub('hub-watchdog', { waitReady: false, hub: (h) => h.holdInit(25000) });
  const first = await until(() => (app.hub.connections[0] && app.hub.connections[0].handshakeAt ? app.hub.connections[0] : null), 6000, 30);
  const closed = first ? await until(() => (first.closedAt ? first : null), 26000, 100) : null;
  const ms = closed ? closed.closedAt - closed.handshakeAt : null;
  const live = await waitLive(app, 6000);
  check('hubInitWatchdog', ms !== null && ms >= 18000 && ms <= 22000 && !!live && app.hub.connections.length === 2,
    { closedAfterMs: ms, connections: app.hub.connections.length, live: !!live });
  await closePage(app);
}

async function runHubPopout() {
  const app = await openHub('hub-popout', { url: ORIGIN + '/app/?mode=popout&conversation=c1' });
  const live = await waitLive(app);
  const ready = app.hub.cmds('ready')[0];
  const d = await domState(app.page);
  // WP4.6 步 5：彈出視窗開那個對話（送 open c1），不再顯示 hub-note
  const opens = await until(() => (app.hub.cmds('open').length ? app.hub.cmds('open') : null), 3000);
  check('hubPopoutConversation', !!live && !!ready && ready.cmd.mode === 'popout' && ready.cmd.conversationId === 'c1' && d.hubNote === null
    && !d.dashboard && app.hub.cmds('newConversation').length === 0 && !!opens && opens.length === 1 && opens[0].cmd.conversationId === 'c1',
    { ready: ready && ready.cmd, opens: opens && opens.map((x) => x.cmd), dom: d });
  // 彈出視窗不存版面（D9）：直接 post layout，localStorage 沒有鍵、也不送伺服器
  const posted = await tpost(app.page, { cmd: 'layout', layout: { panes: [{ id: 'p1', tabs: ['c1'], active: 'c1' }] } });
  await sleep(200);
  const stored = await readStored(app.page);
  check('hubPopoutLayoutNotStored', posted.ok && stored === null && app.hub.cmds('layout').length === 0, { posted, stored, frames: app.hub.cmds('layout').length });
  await shot(app.page, 'hub-popout-1440');
  await closePage(app);
}

// ───── 步驟 8：指令層（commandId、限流重送、斷線丟棄、上限自檢、本地動作、連線上限）─────

/** 頁面收到的 toast 文字（探針的記錄訂閱者：伺服器送的與傳輸層的本地提示都在這裡） */
async function toastTexts(page) {
  return (await probeMsgs(page)).filter((m) => m.type === 'toast').map((m) => m.text);
}
const countOf = (list, x) => list.filter((y) => y === x).length;
/** 畫面上的提示文字（app.js 的 toast） */
const domToasts = (page) => page.evaluate(() => [...document.querySelectorAll('.toasts .toast-text')].map((e) => e.textContent)).catch(() => []);
/** 這個頁面的 console.warn（transport-hub 的都以「[transport-hub]」開頭） */
const warnsOf = (app) => app.console.filter((m) => m.type === 'warning' && m.text.startsWith('[transport-hub]'));
/** 頁面直接呼叫 myriagentHubTransport.post（不經 app.js）；回傳有沒有丟例外 */
const tpost = (page, msg) => page.evaluate((m) => {
  try {
    window.myriagentHubTransport.post(m);
    return { ok: true };
  } catch (e) {
    return { ok: false, error: String(e) };
  }
}, msg);
/** ULID 前 10 字的時間（毫秒） */
function ulidTime(id) {
  const alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
  let t = 0;
  for (const ch of String(id).slice(0, 10)) t = t * 32 + alphabet.indexOf(ch);
  return t;
}
async function openSettingsDialog(page) {
  await page.click('#btnSettings');
  await page.waitForSelector('.dlg-settings .seg-btn[data-value="light"]', { timeout: 3000 });
}
const clickTheme = (page, v) => page.click(`.dlg-settings .seg-btn[data-value="${v}"]`);
/** t0 之後頁面送來的 setting（theme）指令 */
const themeCmds = (hub, t0) => hub.cmds('setting').filter((a) => a.at >= t0 && a.cmd.key === 'theme');
const LAYOUT_KEY = 'myriagent.app.layout.v1';
const readStored = (page) => page.evaluate((k) => {
  try {
    return window.localStorage.getItem(k);
  } catch (e) {
    return 'throws:' + e.name;
  }
}, LAYOUT_KEY);

async function runHubCommands() {
  // 減少動態：主題的擦拭過場（theme.js，620ms）期間整頁被過場蓋住，第二下要等它結束才點得到，
  // 會落在第一下被擋後 1 秒的重送附近（實測差 20～50ms），負載高時順序對調；這裡驗的是傳輸層的合併與重送，不是過場
  const app = await openHub('hub-cmd', { reducedMotion: 'reduce' });
  const { page, hub } = app;
  await waitLive(app);
  await openSettingsDialog(page);

  // rate_limited 兩次（淺色、深色都被擋）：同一個合併鍵只留最新的，1 秒後只重送深色、只重送一次，commandId 沿用
  hub.rejectNext('setting', 'rate_limited', 2);
  const t1 = Date.now();
  await clickTheme(page, 'light');
  await clickTheme(page, 'dark');
  await sleep(2600);
  const r1 = themeCmds(hub, t1);
  const darkFirst = r1.find((a) => a.cmd.value === 'dark');
  const resent = darkFirst ? r1.filter((a) => a !== darkFirst && a.cmd.value === 'dark') : [];
  const resendMs = resent[0] && darkFirst ? resent[0].at - darkFirst.at : null;
  check('hubRateLimitedRetry', r1.length === 3 && r1[0].cmd.value === 'light' && !!darkFirst && resent.length === 1 && r1.filter((a) => a.cmd.value === 'light').length === 1
    && resent[0].commandId === darkFirst.commandId && resendMs >= 800 && resendMs <= 1700 && countOf(await toastTexts(page), '伺服器忙碌，這個變更沒有送出') === 0,
    { frames: r1.map((a) => ({ value: a.cmd.value, commandId: a.commandId, afterMs: a.at - t1 })), resendMs });

  // 連續 4 次 rate_limited：重送 3 次（1、2、4 秒）之後放棄，提示一則「伺服器忙碌」
  hub.rejectNext('setting', 'rate_limited', 4);
  const t2 = Date.now();
  await clickTheme(page, 'light');
  await until(async () => countOf(await toastTexts(page), '伺服器忙碌，這個變更沒有送出') > 0, 11000, 100);
  await sleep(1500);
  const r2 = themeCmds(hub, t2);
  const gaps = r2.slice(1).map((a, i) => a.at - r2[i].at);
  const busy = countOf(await toastTexts(page), '伺服器忙碌，這個變更沒有送出');
  const shown = countOf(await domToasts(page), '伺服器忙碌，這個變更沒有送出');
  check('hubRateLimitedGiveUp', r2.length === 4 && r2.every((a) => a.cmd.value === 'light' && a.commandId === r2[0].commandId)
    && gaps.length === 3 && gaps[0] >= 800 && gaps[0] <= 1700 && gaps[1] >= 1600 && gaps[1] <= 2700 && gaps[2] >= 3200 && gaps[2] <= 4800 && busy === 1 && shown === 1,
    { frames: r2.map((a) => ({ value: a.cmd.value, commandId: a.commandId, afterMs: a.at - t2 })), gaps, busyToasts: busy, shown });

  // 每一個 Cmd 框：頂層只有 commandId、cmd，commandId 是 ULID、時間部分不晚於送出時間（重送沿用原本的，最多早 8 秒）
  const all = hub.invocations('Cmd');
  const bad = all.map((f) => cmdShape(f.obj)).map((s, i) => Object.assign({ at: all[i].at }, s)).filter((s) => !s.ok || ulidTime(s.commandId) > s.at + 1000 || ulidTime(s.commandId) < s.at - 8000);
  const firstSends = all.filter((f, i) => all.findIndex((g) => g.obj.arguments[0].commandId === f.obj.arguments[0].commandId) === i);
  check('hubCmdUlid', all.length >= 8 && bad.length === 0 && firstSends.length === all.length - 4,
    { frames: all.length, distinct: firstSends.length, bad: bad.slice(0, 5), names: all.map((f) => f.obj.arguments[0].cmd.cmd) });
  await closePage(app);
}

async function runHubOfflineCmd() {
  const app = await openHub('hub-offline-cmd');
  const { page, hub, api } = app;
  app.expect((m) => /Failed to load resource/.test(m.text) && /status of 503/.test(m.text) && /\/api\/v1\/auth\/me/.test(m.url), '斷線期間 auth/me 503（刻意）');
  await waitLive(app);
  // 正向對照：連著的時候 focus 會讓 workspace.js 送 visible
  const v0 = hub.cmds('visible').length;
  await page.evaluate(() => window.dispatchEvent(new Event('focus')));
  const visibleSent = await until(() => hub.cmds('visible').length > v0, 2000);
  await openSettingsDialog(page);

  // 新連線握手完成、還沒收到 init（假 hub 不回）：連著但不可送
  hub.holdInit(Infinity);
  hub.drop();
  const half = await until(async () => {
    const st = await tstatus(page);
    return st && st.state === 'online' && st.hubInit === false && hub.connections.length === 2 && hub.current() ? st : null;
  }, 5000, 30);
  const cmd0 = hub.invocations('Cmd').length;
  const toasts0 = (await toastTexts(page)).length;
  await page.evaluate(async () => {
    window.dispatchEvent(new Event('focus'));
    window.__probeHidden = true;
    document.dispatchEvent(new Event('visibilitychange'));
    await new Promise((r) => setTimeout(r, 50));
    window.__probeHidden = false;
    document.dispatchEvent(new Event('visibilitychange'));
  });
  await sleep(600);
  const autoFrames = hub.invocations('Cmd').slice(cmd0).map((f) => f.obj.arguments[0].cmd.cmd);
  const autoToasts = (await toastTexts(page)).slice(toasts0);
  check('hubOfflineSilentAuto', !!visibleSent && !!half && autoFrames.length === 0 && autoToasts.length === 0 && !!hub.current(),
    { positiveControl: !!visibleSent, connectedWithoutInit: !!half, frames: autoFrames, toasts: autoToasts });

  // 使用者在已開的設定對話框改主題：沒有框、提示一則；之後真的斷線（auth/me 503），10 秒內再改一次不再提示
  const tA = Date.now();
  await clickTheme(page, 'light');
  await sleep(300);
  const afterFirst = countOf(await toastTexts(page), '目前沒有連線，這個變更沒有送出');
  api.set('/api/v1/auth/me', { status: 503, body: { error: 'unavailable' } });
  hub.drop();
  const down = await until(async () => {
    const st = await tstatus(page);
    return st && st.state !== 'online' ? st : null;
  }, 3000, 30);
  await clickTheme(page, 'dark');
  await sleep(300);
  const offline = countOf(await toastTexts(page), '目前沒有連線，這個變更沒有送出');
  const shown = countOf(await domToasts(page), '目前沒有連線，這個變更沒有送出');
  const sent = themeCmds(hub, tA);
  check('hubOfflineDropUserCmd', afterFirst === 1 && !!down && offline === 1 && shown === 1 && sent.length === 0 && Date.now() - tA < 10000,
    { afterFirstClick: afterFirst, disconnected: down && down.state, toasts: offline, shown, frames: sent.length, elapsedMs: Date.now() - tA });

  api.set('/api/v1/auth/me', { status: 200, body: { name: 'admin' } });
  hub.holdInit(0);
  await page.evaluate(() => window.myriagentHubTransport.retryNow());
  await waitLive(app, 5000);
  await closePage(app);
}

async function runHubFrameLimit() {
  const app = await openHub('hub-frame');
  const { page, hub } = app;
  await waitLive(app);
  const conns = hub.connections.length;
  const TOO_LARGE = '這個變更太大，沒有送出';
  // 超長字串：16,001 字（單一字串的上限是 16,000，WP4.5 D31）
  const c0 = hub.invocations('Cmd').length;
  const w0 = warnsOf(app).length;
  const posted = await tpost(page, { cmd: 'setting', key: 'theme', value: 'x'.repeat(16001) });
  await sleep(400);
  const big = { frames: hub.invocations('Cmd').length - c0, warns: warnsOf(app).length - w0, toasts: countOf(await toastTexts(page), TOO_LARGE) };
  // 每個字串都在 16,000 字以內、UTF-8 超過 128 KB（10 × 4700 個「中」＝141000 位元組；UTF-16 只有 47000 單位）
  const c1 = hub.invocations('Cmd').length;
  const w1 = warnsOf(app).length;
  await tpost(page, { cmd: 'setting', key: 'theme', value: Array(10).fill('中'.repeat(4700)) });
  await sleep(400);
  const utf8 = { frames: hub.invocations('Cmd').length - c1, warns: warnsOf(app).length - w1, toasts: countOf(await toastTexts(page), TOO_LARGE) };
  // 正向對照：10 × 4000 個「中」（約 120 KB，超過舊的 64 KB）照送
  const c2 = hub.invocations('Cmd').length;
  await tpost(page, { cmd: 'visible', conversationIds: Array(10).fill('中'.repeat(4000)) });
  const okFrame = await until(() => hub.invocations('Cmd').slice(c2)[0] || null, 3000);
  const st = await tstatus(page);
  check('hubFrameLimit', posted.ok && big.frames === 0 && big.warns === 1 && big.toasts === 1 && utf8.frames === 0 && utf8.warns === 1 && utf8.toasts === 1
    && !!okFrame && okFrame.bytes > 120000 && okFrame.bytes <= 131072 && hub.connections.length === conns && !!hub.current() && st.state === 'online' && st.hubInit === true,
    { string16001: big, utf8Over128k: utf8, positiveControlBytes: okFrame ? okFrame.bytes : null, connections: hub.connections.length, status: st,
      shown: countOf(await domToasts(page), TOO_LARGE), warns: warnsOf(app).map((m) => m.text) });
  await closePage(app);
}

async function runHubShapeLimit() {
  const app = await openHub('hub-shape');
  const { page, hub } = app;
  await waitLive(app);
  const TOO_LARGE = '這個變更太大，沒有送出';
  // 呼叫參數 {commandId, cmd} 是深度 1、cmd 是 2；再包 k 層物件 → 深度 2 + k
  const nest = (k) => { let v = 1; for (let i = 0; i < k; i++) v = { n: v }; return v; };
  const cases = [
    { name: '深度 8（上限）', msg: { cmd: 'visible', conversationIds: [], deep: nest(6) }, sent: true },
    { name: '深度 9', msg: { cmd: 'visible', conversationIds: [], deep: nest(7) }, sent: false },
    // 節點：參數、commandId、cmd、cmd.cmd、陣列＝5，加上 N 個元素
    { name: '節點 4096（上限）', msg: { cmd: 'visible', conversationIds: Array(4091).fill('a') }, sent: true },
    { name: '節點 4097', msg: { cmd: 'visible', conversationIds: Array(4092).fill('a') }, sent: false },
    // 字串值的上限是伺服器第一段的 16,000（WP4.5 D31）；屬性名仍是 4096
    { name: '字串 16000 字（上限）', msg: { cmd: 'visible', conversationIds: ['a'.repeat(16000)] }, sent: true },
    { name: '字串 16001 字', msg: { cmd: 'visible', conversationIds: ['a'.repeat(16001)] }, sent: false },
    { name: '屬性名 4096 字（上限）', msg: { cmd: 'visible', conversationIds: [], ['k'.repeat(4096)]: 1 }, sent: true },
    { name: '屬性名 4097 字', msg: { cmd: 'visible', conversationIds: [], ['k'.repeat(4097)]: 1 }, sent: false },
  ];
  const results = [];
  for (const c of cases) {
    const c0 = hub.invocations('Cmd').length;
    const w0 = warnsOf(app).length;
    await tpost(page, c.msg);
    await sleep(c.sent ? 0 : 300);
    const got = c.sent ? await until(() => hub.invocations('Cmd').length > c0, 2000) : hub.invocations('Cmd').length > c0;
    results.push({ name: c.name, expectSent: c.sent, sent: !!got, warns: warnsOf(app).length - w0 });
  }
  const autoToasts = countOf(await toastTexts(page), TOO_LARGE);
  // 使用者操作的指令（setting）深度 9：沒有框、警告一筆、提示一則
  const c0 = hub.invocations('Cmd').length;
  const w0 = warnsOf(app).length;
  await tpost(page, { cmd: 'setting', key: 'fontSize', value: nest(7) });
  await sleep(400);
  const user = { frames: hub.invocations('Cmd').length - c0, warns: warnsOf(app).length - w0, toasts: countOf(await toastTexts(page), TOO_LARGE) };
  check('hubShapeLimit', results.every((r) => r.sent === r.expectSent && r.warns === (r.expectSent ? 0 : 1)) && autoToasts === 0
    && user.frames === 0 && user.warns === 1 && user.toasts === 1 && !!hub.current(),
    { auto: results, autoToasts, user });
  await closePage(app);
}

async function runHubTooMany() {
  const app = await openHub('hub-toomany');
  const { page, hub, api } = app;
  await waitLive(app);
  hub.closeWithError('Connection closed with an error. HubException: too_many_connections');
  const st = await until(async () => {
    const s = await tstatus(page);
    return s && s.reason === 'tooManyConnections' ? s : null;
  }, 5000, 30);
  await sleep(100);
  const d = await domState(page);
  await shot(page, 'hub-too-many-1440');
  const n0 = api.count('/api/v1/auth/me');
  const t0 = Date.now();
  await page.evaluate(() => window.myriagentHubTransport.retryNow());
  const live = await waitLive(app, 3000);
  const ms = live ? Date.now() - t0 : null;
  const waitMs = st ? st.retryAt - st.lastCloseAt : null;
  check('hubTooManyConnections', !!st && st.state !== 'online' && waitMs >= 30000 && waitMs <= 36000 && /開了太多分頁/.test(d.bar || '') && d.barKind === 'tooMany' && d.retryVisible
    && !!live && ms < 2000 && api.count('/api/v1/auth/me') === n0 + 1 && hub.connections.length === 2,
    { status: st, waitMs, bar: d.bar, barKind: d.barKind, retryVisible: d.retryVisible, reconnectMs: ms, connections: hub.connections.length });
  await closePage(app);
}

async function runHubClientState() {
  const app = await openHub('hub-clientstate');
  const { page, hub } = app;
  await waitLive(app);
  const states = () => hub.invocations('ClientState').map((f) => Object.assign({ at: f.at }, f.obj.arguments[0]));
  const setVis = (hidden) => page.evaluate((x) => { window.__probeHidden = x; document.dispatchEvent(new Event('visibilitychange')); }, hidden);
  const n0 = states().length;
  // hidden、visible 各一次（各等 200 ms 合併）
  const tHide = Date.now();
  await setVis(true);
  await sleep(500);
  await setVis(false);
  await sleep(500);
  const step1 = states().slice(n0);
  // 200 ms 內 hidden → visible → hidden：只送最後狀態（hidden）
  const n1 = states().length;
  await page.evaluate(async () => {
    const flip = (x) => { window.__probeHidden = x; document.dispatchEvent(new Event('visibilitychange')); };
    flip(true);
    await new Promise((r) => setTimeout(r, 40));
    flip(false);
    await new Promise((r) => setTimeout(r, 40));
    flip(true);
  });
  await sleep(600);
  const step2 = states().slice(n1);
  // 內容沒變：再觸發 visibilitychange、focus、blur 都不送
  const n2 = states().length;
  await page.evaluate(() => {
    document.dispatchEvent(new Event('visibilitychange'));
    window.dispatchEvent(new Event('focus'));
    window.dispatchEvent(new Event('blur'));
  });
  await sleep(500);
  const step3 = states().slice(n2);
  // 失去焦點（blur）：focused 變 false
  await setVis(false);
  await sleep(400);
  const n3 = states().length;
  await page.evaluate(() => { window.__probeFocused = false; window.dispatchEvent(new Event('blur')); });
  await sleep(500);
  const step4 = states().slice(n3);
  check('hubClientStateOnVisibility', step1.length === 2 && step1[0].visible === false && step1[1].visible === true && step1[0].at - tHide >= 190
    && step2.length === 1 && step2[0].visible === false && step3.length === 0 && step4.length === 1 && step4[0].focused === false && step4[0].visible === true,
    { hiddenThenVisible: step1.map((s) => ({ visible: s.visible, focused: s.focused })), firstDelayMs: step1[0] ? step1[0].at - tHide : null,
      burst: step2.map((s) => s.visible), unchanged: step3.length, blur: step4.map((s) => ({ visible: s.visible, focused: s.focused })) });

  // rate_limited：2 秒後重送一次（同樣內容）；重送也被限流就不再送
  hub.rejectClientState('rate_limited');
  const n4 = states().length;
  const tF = Date.now();
  await page.evaluate(() => { window.__probeFocused = true; window.dispatchEvent(new Event('focus')); });
  await sleep(3000);
  const once = states().slice(n4);
  hub.rejectClientState('rate_limited');
  hub.rejectClientState('rate_limited');
  const n5 = states().length;
  const tB = Date.now();
  await page.evaluate(() => { window.__probeFocused = false; window.dispatchEvent(new Event('blur')); });
  await sleep(4500);
  const twice = states().slice(n5);
  check('hubClientStateRateLimited', once.length === 2 && once[0].focused === true && once[1].focused === true && once[1].at - once[0].at >= 1800 && once[1].at - once[0].at <= 2700
    && twice.length === 2 && twice.every((s) => s.focused === false) && once[0].at - tF >= 190,
    { firstRejected: once.map((s) => ({ focused: s.focused, afterMs: s.at - tF })), bothRejected: twice.map((s) => ({ focused: s.focused, afterMs: s.at - tB })) });
  await closePage(app);
}

async function runHubLayout() {
  const app = await openHub('hub-layout', { earlySub: 'record' });
  const { page, hub } = app;
  await waitLive(app);
  const firstInit = await page.evaluate(() => window.__earlySub.filter((m) => m.type === 'init').map((m) => m.layout));
  const layout = { panes: [{ id: 'p1', tabs: ['c1', 'c2'], active: 'c2' }, { id: 'p2', tabs: [], active: null }], focusedPane: 'p2', sidebarCollapsed: true, view: 'chat' };
  const posted = await tpost(page, { cmd: 'layout', layout });
  await sleep(300);
  const stored = await readStored(page);
  const layoutFrames = hub.cmds('layout').length;
  // 斷線重連：新的 init（layout 是 null）在分派前換成存下的
  const before = await page.evaluate(() => window.__earlySub.length);
  hub.drop();
  await until(async () => (await page.evaluate((n) => window.__earlySub.slice(n).some((m) => m.type === 'init'), before)) || null, 6000);
  const injected = await page.evaluate((n) => (window.__earlySub.slice(n).find((m) => m.type === 'init') || {}).layout, before);
  const storedAtReconnect = await readStored(page);
  // 存的內容壞掉：不注入（layout 維持 null）
  await page.evaluate((k) => window.localStorage.setItem(k, '{"panes":'), LAYOUT_KEY);
  const before2 = await page.evaluate(() => window.__earlySub.length);
  hub.drop();
  await until(async () => (await page.evaluate((n) => window.__earlySub.slice(n).some((m) => m.type === 'init'), before2)) || null, 6000);
  const corrupt = await page.evaluate((n) => (window.__earlySub.slice(n).find((m) => m.type === 'init') || { layout: 'missing' }).layout, before2);
  const live = await waitLive(app, 4000);
  check('hubLayoutLocal', posted.ok && firstInit.length === 1 && firstInit[0] === null && layoutFrames === 0
    && stored === JSON.stringify(layout) && JSON.stringify(injected) === storedAtReconnect && JSON.stringify(injected) === JSON.stringify(layout) && corrupt === null && !!live,
    { firstInitLayout: firstInit, stored, layoutFrames, injected, storedAtReconnect, corrupt, live: !!live });
  await closePage(app);
}

async function runHubStorageThrows() {
  const app = await openHub('hub-storage-throws', { initOpts: { localStorageThrows: true } });
  const { page, hub } = app;
  const live = await waitLive(app);
  const stored = await readStored(page);
  const posted = await tpost(page, { cmd: 'layout', layout: { panes: [{ id: 'p1', tabs: [], active: null }] } });
  const before = hub.connections.length;
  hub.drop();
  const again = await until(async () => {
    const msgs = await probeMsgs(page);
    return hub.connections.length > before && msgs.filter((m) => m.type === 'init').length >= 2 ? msgs.filter((m) => m.type === 'init').at(-1) : null;
  }, 6000);
  const live2 = await waitLive(app, 4000);
  check('hubLocalStorageThrows', !!live && live.accounts === RF.browserAccounts(NOW).length && /^throws:/.test(stored || '') && posted.ok && hub.cmds('layout').length === 0
    && !!again && again.layout === null && !!live2 && unexpectedErrors(app).length === 0,
    { accounts: live && live.accounts, storage: stored, posted, reinitLayout: again ? again.layout : 'missing', errors: unexpectedErrors(app) });
  await closePage(app);
}

async function runHubLocalActions() {
  const app = await openHub('hub-local');
  const { page, hub } = app;
  await waitLive(app);
  const opened = () => page.evaluate(() => window.__opened.slice());
  // 外站：開新分頁（noopener,noreferrer）；javascript:、file: 拒絕並警告；都不送伺服器
  const w0 = warnsOf(app).length;
  await tpost(page, { cmd: 'openLink', href: 'https://example.com/docs?q=1#a' });
  await tpost(page, { cmd: 'openLink', href: 'javascript:alert(1)' });
  await tpost(page, { cmd: 'openLink', href: 'file:///C:/Windows/win.ini' });
  await sleep(300);
  const afterExternal = await opened();
  const linkWarns = warnsOf(app).length - w0;
  // 剪貼簿（WP4.6 D6）：傳輸層交給網頁 copyFallback（網頁開可以選取的文字框），不再提示、不送伺服器
  const m0 = (await probeMsgs(page)).length;
  await tpost(page, { cmd: 'copyText', text: '要複製的字' });
  await tpost(page, { cmd: 'copyText', text: '再一次' });
  await sleep(300);
  const fallbacks = (await probeMsgs(page)).slice(m0).filter((m) => m.type === 'copyFallback').map((m) => m.text);
  const copyToasts = (await toastTexts(page)).filter((t) => /複製/.test(t)).length;
  // 網頁的退路框（WP4.6 步 8）：同時只開一個、內容是最後一則；關掉再做下一項
  const box = await page.evaluate(() => [...document.querySelectorAll('.dlg-copy-fallback textarea')].map((t) => t.value));
  check('hubCopyTextFallback', JSON.stringify(fallbacks) === JSON.stringify(['要複製的字', '再一次']) && copyToasts === 0 && hub.cmds('copyText').length === 0
    && JSON.stringify(box) === JSON.stringify(['再一次']),
    { fallbacks, copyToasts, frames: hub.cmds('copyText').length, box });
  await page.click('.dlg-copy-fallback .dlg-foot button').catch(() => {});
  await until(() => page.evaluate(() => (document.querySelectorAll('.dlg-back').length === 0 ? true : null)), 2000, 50);
  // 彈出視窗：同站的 /app/?mode=popout&conversation=…（id 編碼）、noopener；沒有 id 的拒絕
  const w1 = warnsOf(app).length;
  await tpost(page, { cmd: 'popout', conversationId: 'c 1' });
  await tpost(page, { cmd: 'popout' });
  await sleep(300);
  const afterPopout = (await opened()).slice(afterExternal.length);
  check('hubPopoutLocal', afterPopout.length === 1 && JSON.stringify(afterPopout[0]) === JSON.stringify(['/app/?mode=popout&conversation=c%201', '_blank', 'noopener'])
    && warnsOf(app).length - w1 === 1 && hub.cmds('popout').length === 0, { opened: afterPopout, warns: warnsOf(app).length - w1, frames: hub.cmds('popout').length });
  // 同站：點「帳號與安全」經 openLink 導覽到 /（不開新分頁）
  const n0 = app.navigations.length;
  await page.click('.hub-account');
  const nav = await until(async () => {
    const d = await domState(page);
    return d.path === '/' && d.authPage ? d : null;
  }, 4000, 50);
  const totalOpened = await opened().catch(() => null);
  check('hubOpenLinkLocal', afterExternal.length === 1 && JSON.stringify(afterExternal[0]) === JSON.stringify(['https://example.com/docs?q=1#a', '_blank', 'noopener,noreferrer'])
    && linkWarns === 2 && hub.cmds('openLink').length === 0 && !!nav && app.navigations.slice(n0).some((n) => n.url === ORIGIN + '/'),
    { external: afterExternal, linkWarns, frames: hub.cmds('openLink').length, navigated: app.navigations.slice(n0).map((n) => n.url), openedAfterNav: totalOpened });
  await closePage(app);
}

// ───── 合併前審查：accountUpdate 的合併、ready 被限流、setting 被拒時改回、WebSocket 開啟握手卡住 ─────

/** 只比對欄位（不看順序） */
const sameFields = (a, b) => !!a && !!b && sameJson(Object.keys(a).sort().map((k) => [k, a[k]]), Object.keys(b).sort().map((k) => [k, b[k]]));

async function runHubAccountUpdateMerge() {
  const app = await openHub('hub-merge');
  const { page, hub } = app;
  await waitLive(app);
  // 情況一：改名被限流（1 秒後重送），300 ms 後改色（accountUpdate 是部分更新，只帶顏色）：改色那一筆帶上名稱，改名不再單獨重送
  hub.rejectNext('accountUpdate', 'rate_limited', 1);
  const t1 = Date.now();
  await tpost(page, { cmd: 'accountUpdate', accountId: 'la_c1', label: '新名' });
  await sleep(300);
  await tpost(page, { cmd: 'accountUpdate', accountId: 'la_c1', color: '#7A7CF0' });
  await sleep(2000);
  const one = hub.cmds('accountUpdate').filter((a) => a.at >= t1).map((a) => a.cmd);
  // 情況二：改名的結果還沒回來就改色（同一個工作裡連送兩筆）；改名的 rate_limited 晚到、不重送，但名稱已經在改色那一筆裡
  hub.rejectNext('accountUpdate', 'rate_limited', 1);
  const t2 = Date.now();
  await page.evaluate(() => {
    window.myriagentHubTransport.post({ cmd: 'accountUpdate', accountId: 'la_x1', label: '另一個新名' });
    window.myriagentHubTransport.post({ cmd: 'accountUpdate', accountId: 'la_x1', color: '#56B6C2' });
  });
  await sleep(2000);
  const two = hub.cmds('accountUpdate').filter((a) => a.at >= t2).map((a) => a.cmd);
  const toasts = await toastTexts(page);
  check('hubAccountUpdateMerge', one.length === 2 && sameFields(one[0], { cmd: 'accountUpdate', accountId: 'la_c1', label: '新名' })
    && sameFields(one[1], { cmd: 'accountUpdate', accountId: 'la_c1', label: '新名', color: '#7A7CF0' })
    && two.length === 2 && sameFields(two[0], { cmd: 'accountUpdate', accountId: 'la_x1', label: '另一個新名' })
    && sameFields(two[1], { cmd: 'accountUpdate', accountId: 'la_x1', label: '另一個新名', color: '#56B6C2' }) && toasts.length === 0,
    { renameThenColor: one, inFlight: two, toasts });
  await closePage(app);
}

// ───── 各帳戶的 token 計數（tok1，sync-protocol 14.9）：瀏覽器版是各電腦的合計（scope 'all'），只列有電腦回報過的帳戶 ─────
/** 每張帳戶卡：token 列看不看得到、in 與 out 的字（鎖定後）、滑過的說明、狀態那一行的文字；數字有沒有蓋到各電腦小點、有沒有被右緣裁切 */
function hubTokenRows() {
  const shown = (el) => { if (!el) return false; const cs = getComputedStyle(el); const r = el.getBoundingClientRect(); return cs.display !== 'none' && cs.visibility !== 'hidden' && r.width > 0 && r.height > 0; };
  return Object.fromEntries(Array.from(document.querySelectorAll('#sidebar .macct')).map((card) => {
    const row = card.querySelector('.macct-tok');
    const sub = card.querySelector('.macct-sub-text');
    const nums = row ? Array.from(row.querySelectorAll('.mtok-n')) : [];
    const textOf = (n) => (n ? Array.from(n.querySelectorAll('.mtok-c')).map((c) => c.textContent).join('') : null);
    let fits = null;
    if (shown(row)) {
      const box = row.getBoundingClientRect();
      const right = Math.max(...Array.from(row.children).filter(shown).map((el) => el.getBoundingClientRect().right));
      const devs = card.querySelector('.macct-devs');
      fits = right <= box.right + 0.5 && (!shown(devs) || right <= devs.getBoundingClientRect().left + 0.5);
    }
    return [card.dataset.key, {
      shown: shown(row), fits,
      in: textOf(nums[0]), out: textOf(nums[1]),
      scrambling: row ? row.querySelectorAll('.mtok-c.scr').length : 0,
      title: row ? row.title : null,
      subText: shown(sub) ? sub.textContent : null,
    }];
  }));
}

async function runHubTokens() {
  const tokens = (today) => ({
    day: '2026-10-23', scope: 'all',
    accounts: {
      la_c1: { today: { input: today, cached: 90_000_000, output: 2_958_077 }, week: { input: 400_000_000, output: 9_000_000 }, month: { input: 900_000_000, output: 20_000_000 } },
      la_x2: { today: { input: 2_500_000, cached: 0, output: 34_449 }, week: { input: 2_500_000, output: 34_449 }, month: { input: 2_500_000, output: 34_449 } },
    },
  });
  const app = await openHub('hub-tokens', { hub: (h) => { h.initFor = (ready) => Object.assign(RF.browserInit(NOW, { mode: ready && ready.mode }), { tokens: tokens(128_000_000) }); } });
  const { page, hub } = app;
  await waitLive(app);
  // 瀏覽器版一開始是儀表板：切到對話檢視才有側欄
  await page.click('#viewSwitch [data-view="chat"]');
  await page.waitForFunction(() => !document.getElementById('sidebar').hidden, null, { timeout: 3000 });
  const r0 = await until(async () => { const r = await page.evaluate(hubTokenRows); return r.la_c1 && r.la_c1.shown && r.la_c1.scrambling === 0 && r.la_c1.in === '128.0M' ? r : null; }, 4000)
    || await page.evaluate(hubTokenRows);
  // la_c1、la_x2 有電腦回報過（各電腦的合計），in 與 out 分開（精簡格式）；la_c4 已登入但沒有電腦回報過（例如用它的電腦還是舊版）：照舊顯示狀態那一行，不顯示 0；
  // la_x1 沒有電腦登入。同一行還有各電腦的小點：數字不蓋到小點、不被裁切
  check('hubTokensMerged', r0.la_c1.shown && r0.la_c1.in === '128.0M' && r0.la_c1.out === '2.958M' && /所有電腦上 Clode Myriagent 的對話合計/.test(r0.la_c1.title)
    && /in（輸入）：128,000,000（其中快取讀取 90,000,000）/.test(r0.la_c1.title)
    && r0.la_x2.shown && r0.la_x2.in === '2.500M' && r0.la_x2.out === '34.44K' && r0.la_c1.fits && r0.la_x2.fits
    && !r0.la_c4.shown && !!r0.la_c4.subText && !r0.la_x1.shown, r0);
  await page.locator('#sidebar .sb-sec-accts').screenshot({ path: path.join(OUT, 'hub-tokens-sidebar.png') });
  report.screenshots.push('hub-tokens-sidebar.png');

  // 伺服器送新的 tokens（某台電腦又回報了）：數字解碼成新的合計
  hub.push({ type: 'tokens', tokens: tokens(128_146_873) });
  const r1 = await until(async () => { const r = await page.evaluate(hubTokenRows); return r.la_c1.scrambling === 0 && r.la_c1.in === '128.1M' && r.la_c1.out === '2.958M' ? r : null; }, 4000);
  check('hubTokensPush', !!r1, r1 || await page.evaluate(hubTokenRows));
  await closePage(app);
}

async function runHubReadyRateLimited() {
  // 新連線送 ready 時權杖桶是空的：1 秒後在同一條連線以同一個 commandId 重送，不必等 20 秒的 init 看門狗換連線
  const app = await openHub('hub-ready-limited', { waitReady: false, hub: (h) => h.rejectNext('ready', 'rate_limited', 1) });
  const live = await waitLive(app, 6000);
  const readies = app.hub.cmds('ready');
  const st = await tstatus(app.page);
  const gap = readies.length >= 2 ? readies[1].at - readies[0].at : null;
  check('hubReadyRateLimitedRetry', !!live && readies.length === 2 && readies[0].commandId === readies[1].commandId && readies[0].conn === readies[1].conn
    && gap >= 800 && gap <= 1700 && app.hub.connections.length === 1 && !!st && st.generation === 1,
    { live: !!live, readies: readies.map((r) => ({ commandId: r.commandId, conn: r.conn, at: r.at - (readies[0] ? readies[0].at : 0) })), gapMs: gap, connections: app.hub.connections.length, status: st });
  await closePage(app);
}

async function runHubSettingRevert() {
  const app = await openHub('hub-setting-revert', { reducedMotion: 'reduce' }); // 同 runHubCommands：不跑主題的擦拭過場
  const { page, hub } = app;
  await waitLive(app);
  await openSettingsDialog(page);
  const themeNow = () => page.evaluate(() => ({
    html: document.documentElement.dataset.theme,
    seg: [...document.querySelectorAll('.dlg-settings .seg-btn.on')].map((b) => b.dataset.value),
  }));
  const before = await themeNow();
  // 伺服器拒絕（unavailable）：畫面先變淺色，回應之後改回伺服器的深色（主題與設定頁的分段鈕都是）
  hub.rejectNext('setting', 'unavailable');
  const t0 = Date.now();
  await clickTheme(page, 'light');
  const reverted = await until(async () => {
    const st = await themeNow();
    return st.html === 'dark' && sameJson(st.seg, ['dark']) && themeCmds(hub, t0).length === 1 ? st : null;
  }, 3000);
  const settingsMsgs = (await probeMsgs(page)).filter((m) => m.type === 'settings').map((m) => m.settings);
  // 正向對照：被接受的停在淺色
  const t1 = Date.now();
  await clickTheme(page, 'light');
  await sleep(600);
  const kept = await themeNow();
  check('hubSettingRevert', before.html === 'dark' && sameJson(before.seg, ['dark']) && !!reverted && sameJson(settingsMsgs, [{ theme: 'dark' }])
    && kept.html === 'light' && sameJson(kept.seg, ['light']) && themeCmds(hub, t1).length === 1,
    { before, reverted, settingsMsgs, accepted: kept, frames: themeCmds(hub, t0).map((a) => a.cmd.value) });
  await closePage(app);
}

async function runHubStartTimeout() {
  // WebSocket 的開啟握手卡住（假 hub 收下、一直不完成開啟）：30 秒時傳輸層關掉還在 CONNECTING 的 socket、離開這一次嘗試，照退避再連
  const app = await openHub('hub-start-timeout');
  const { page, hub, api } = app;
  await waitLive(app);
  hub.hangNext(1);
  const n0 = api.count('/api/v1/auth/me');
  hub.drop();
  const hung = await until(() => hub.connections.find((c) => c.hung) || null, 5000, 30);
  await sleep(1000);
  const during = await tstatus(page);
  const bar = (await domState(page)).bar;
  const closed = hung ? await until(() => (hung.closedAt ? hung : null), 34000, 100) : null;
  const closedMs = closed ? closed.closedAt - closed.openedAt : null;
  const afterClose = await tstatus(page);
  const live = await waitLive(app, 5000);
  const liveMs = live && closed ? Date.now() - closed.closedAt : null;
  check('hubStartTimeout', !!hung && !!during && during.state === 'reconnecting' && during.attempt === 1 && /^重新連線中/.test(bar || '')
    && closedMs !== null && closedMs >= 29500 && closedMs <= 32000 && !!afterClose && afterClose.attempt === 2 && afterClose.state !== 'online'
    && !!live && hub.connections.length === 3 && api.count('/api/v1/auth/me') === n0 + 2,
    { hungClosedAfterMs: closedMs, during, bar, afterClose, liveAfterCloseMs: liveMs, connections: hub.connections.length, authMe: api.count('/api/v1/auth/me') - n0 });
  await closePage(app);
}

// ───── 步驟 9：儀表板（帳戶 × 電腦、各電腦 CLI、瀏覽器版帳戶卡與選單、桌面裝置碼入口、手機版面）─────

/** 桌面：切到儀表板（init 的 layout 沒有分頁時 workspace 會開新對話、切回對話檢視） */
async function showDashboard(page) {
  await page.click('#viewSwitch [data-view="dashboard"]');
  await page.waitForFunction(() => !document.getElementById('dashboard').hidden, null, { timeout: 3000 });
}

/** 儀表板上看得到的同步區塊 */
function remoteSections(page) {
  return page.evaluate(() => {
    const vis = (el) => !!el && !el.hidden && getComputedStyle(el).display !== 'none' && el.getBoundingClientRect().height > 0;
    return {
      dashboard: vis(document.getElementById('dashboard')),
      matrix: vis(document.querySelector('#dashboard .dash-sec-mx')),
      devicesCli: vis(document.querySelector('#dashboard .dash-sec-dcli')),
    };
  });
}

/** 矩陣的內容：欄（電腦）與每一列的格子 */
function readMatrix(page) {
  return page.evaluate(() => {
    const txt = (el, sel) => { const x = el.querySelector(sel); return x ? x.textContent : null; };
    const cols = [...document.querySelectorAll('#dashboard .mx thead th.mx-dev')].map((th) => ({ id: th.dataset.device, name: txt(th, '.mx-dev-name'), self: !!th.querySelector('.mx-self'), state: txt(th, '.mx-dev-state') }));
    const rows = [...document.querySelectorAll('#dashboard .mx tbody tr')].map((tr) => ({
      id: tr.dataset.account, name: txt(tr, '.mx-acct-name'),
      cells: [...tr.querySelectorAll('td.mx-cell')].map((td) => ({
        device: td.dataset.device, kind: td.dataset.kind, tone: td.dataset.tone || null, text: txt(td, '.mx-text'), hint: txt(td, '.mx-hint'),
        poller: !!td.querySelector('.mx-poller'), dim: td.classList.contains('dim'), seen: txt(td, '.mx-seen'), opacity: getComputedStyle(td.querySelector('.mx-text')).opacity,
      })),
    }));
    return { cols, rows };
  });
}

/** 各電腦 CLI 的內容 */
function readDeviceCli(page) {
  return page.evaluate(() => [...document.querySelectorAll('#dashboard .dcli')].map((card) => ({
    device: card.dataset.device, name: card.querySelector('.dcli-name').textContent, self: !!card.querySelector('.dcli-head .tag'),
    state: card.querySelector('.dcli-state') ? card.querySelector('.dcli-state').textContent : null,
    rows: [...card.querySelectorAll('.dcli-row')].map((r) => ({ provider: r.dataset.provider, kind: r.dataset.kind, tone: r.dataset.tone, text: r.querySelector('.dcli-text').textContent, notes: [...r.querySelectorAll('.dcli-note')].map((n) => n.textContent) })),
  })));
}

/** 帳戶卡的順序 */
const cardOrder = (page) => page.evaluate(() => [...document.querySelectorAll('#dashboard article.acard')].map((c) => c.dataset.key));

/** 開某張帳戶卡的「帳戶動作」選單，回傳選單項目（文字、停用） */
async function openCardMenu(page, key) {
  await page.click(`#dashboard article.acard[data-key="${key}"] .acard-more`);
  await page.waitForSelector('.menu', { timeout: 2000 });
  return page.evaluate(() => [...document.querySelectorAll('.menu .menu-item')].map((b) => ({ label: b.querySelector('.mi-label').textContent, disabled: b.disabled })));
}
/** 按選單裡文字正好是 label 的項目（真的滑鼠點擊） */
const clickMenuItem = (page, label) => page.locator('.menu .menu-item').filter({ has: page.locator('.mi-label', { hasText: new RegExp('^' + label.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '$') }) }).first().click();
const closeMenus = (page) => page.keyboard.press('Escape');

/** 矩陣每一格的預期（照步驟 2 的優先順序表；remote-fixtures 的 browserAccounts × A 在線、B 離線） */
const EXPECT_CELLS = {
  la_c1: [['ready', '已登入', 'ok'], ['ready', '已登入', 'warn']],
  la_c2: [['identity', '身分不符', 'bad'], ['ready', '已登入', 'warn']],
  la_x1: [['notLoggedIn', '尚未登入', 'muted'], ['absent', '尚未加入', 'none']],
  la_c3: [['error', '讀不到憑證 ' + RF.XSS, 'bad'], ['checking', '檢查中', 'info']],
  la_c4: [['apiKey', 'API Key 登入', 'warn'], ['ready', '登入已到期', 'bad']],
  la_x2: [['notInstalled', '未安裝 CLI', 'muted'], ['ready', '已登入', 'ok']],
};
const EXPECT_HINTS = [
  ['la_c1', 1, 'lin.dev@example.com・登入即將到期'],
  ['la_c2', 0, '那台登入的是 ' + RF.XSS + '@example.com'],
  ['la_c2', 1, 'x@example.com・權杖待更新'],
  ['la_x1', 0, '到 OFFICE-PC 登入，或用裝置碼從網頁登入'],
];
// monitor：電腦狀態面板的訂閱（web-protocol 第六節第 9 點，伺服器處理；每則 init 之後依面板的狀態送一次）
const ALLOWED_HUB_CMDS = new Set(['ready', 'setting', 'accountUpdate', 'accountReorder', 'deviceRename', 'visible', 'monitor']);

async function runHubDashboard() {
  const app = await openHub('hub-dash');
  const { page, hub } = app;
  await waitLive(app);
  const sections = await remoteSections(page);
  const mx = await readMatrix(page);

  // 矩陣：欄跳過已撤銷、照順序；每一格的種類、文字、色調；查用量；離線調暗
  check('matrixColumnsSkipRevoked', sections.matrix && JSON.stringify(mx.cols.map((c) => c.id)) === JSON.stringify(['d_A', 'd_B'])
    && mx.cols[0].name === 'OFFICE-PC' && mx.cols[1].name === '筆電 ' + RF.XSS && mx.cols[0].state === '在線' && /^離線・最後上線 /.test(mx.cols[1].state || '') && !mx.cols.some((c) => c.self),
    { cols: mx.cols });
  const kinds = Object.fromEntries(mx.rows.map((r) => [r.id, r.cells.map((c) => [c.kind, c.text, c.tone])]));
  const hints = EXPECT_HINTS.map(([id, i, hint]) => { const r = mx.rows.find((x) => x.id === id); return { id, i, expected: hint, got: r && r.cells[i] ? r.cells[i].hint : null }; });
  check('matrixCellStates', JSON.stringify(kinds) === JSON.stringify(EXPECT_CELLS) && JSON.stringify(mx.rows.map((r) => r.id)) === JSON.stringify(Object.keys(EXPECT_CELLS))
    && hints.every((x) => x.got === x.expected) && mx.rows.every((r) => r.cells.every((c, i) => c.device === ['d_A', 'd_B'][i])),
    { got: kinds, hints });
  const pollers = mx.rows.flatMap((r) => r.cells.map((c, i) => (c.poller ? r.id + '/' + ['d_A', 'd_B'][i] : null))).filter(Boolean);
  check('matrixPollerMark', JSON.stringify(pollers) === JSON.stringify(['la_c1/d_A', 'la_x2/d_B']), { pollers });
  const dimB = mx.rows.every((r) => r.cells[1].dim && /^離線・最後已知 /.test(r.cells[1].seen || '') && Number(r.cells[1].opacity) < 1);
  const brightA = mx.rows.every((r) => !r.cells[0].dim && r.cells[0].seen === null && Number(r.cells[0].opacity) === 1);
  check('matrixOfflineDim', dimB && brightA, { b: mx.rows.map((r) => ({ id: r.id, dim: r.cells[1].dim, seen: r.cells[1].seen, opacity: r.cells[1].opacity })), aDim: mx.rows.map((r) => r.cells[0].dim) });
  // 十字準線（remote.js 的 bindMatrixCrosshair）：滑過一格，那一行（同一台電腦的表頭與各格）加上 .mx-hc、那一列有底色；離開就清掉
  await page.hover('#dashboard .mx tbody tr:first-child td.mx-cell[data-device="d_B"]');
  const hc = await until(() => page.evaluate(() => {
    const lit = [...document.querySelectorAll('#dashboard .mx .mx-hc')];
    if (!lit.length) return null;
    const row = document.querySelector('#dashboard .mx tbody tr:first-child th');
    return { devices: [...new Set(lit.map((x) => x.dataset.device))], count: lit.length, header: lit.some((x) => x.tagName === 'TH'), rowBg: getComputedStyle(row).backgroundColor };
  }), 1500, 15);
  await page.mouse.move(2, 2);
  const hcOff = await until(() => page.evaluate(() => document.querySelectorAll('#dashboard .mx .mx-hc').length === 0 || null), 1500, 15);
  check('matrixCrosshair', !!hc && JSON.stringify(hc.devices) === '["d_B"]' && hc.count === mx.rows.length + 1 && hc.header && hc.rowBg !== 'rgba(0, 0, 0, 0)' && !!hcOff, { hc, hcOff });

  // 短暫斷線（伺服器換版時每台都會斷）：看到 OFFICE-PC 從線上變離線，欄先顯示「重新連線中…」（remoterules.trackDeviceLinks）；
  // 本來就離線很久的筆電照舊；連回來是在線
  const devs = RF.browserDevices(NOW);
  hub.push({ type: 'devices', devices: [Object.assign({}, devs[0], { online: false }), devs[1], devs[2]] });
  const flip = await until(async () => { const m = await readMatrix(page); return m.cols[0] && m.cols[0].state === '重新連線中…' ? m : null; });
  if (flip) {
    await page.evaluate(() => { const s = document.querySelector('#dashboard .dash-sec-mx'); if (s) s.scrollIntoView({ block: 'start' }); });
    await shot(page, 'hub-dashboard-reconnecting');
    await page.evaluate(() => { const d = document.getElementById('dashboard'); if (d) d.scrollTop = 0; });
  }
  hub.push({ type: 'devices', devices: devs });
  const back = await until(async () => { const m = await readMatrix(page); return m.cols[0] && m.cols[0].state === '在線' ? m : null; });
  check('matrixDeviceReconnecting', !!flip && /^離線・最後上線 /.test(flip.cols[1].state || '') && !!back, { flip: flip && flip.cols, back: back && back.cols });

  // 各電腦 CLI：每台每種一格
  const dcli = await readDeviceCli(page);
  const dcliSig = dcli.map((d) => [d.device, d.rows.map((r) => [r.provider, r.kind, r.text, r.notes])]);
  check('devicesCliTable', sections.devicesCli && JSON.stringify(dcliSig) === JSON.stringify([
    ['d_A', [['Claude', 'ok', '2.1.0', []], ['Codex', 'update', '0.50.0 → 0.51.0（有新版）', []]]],
    ['d_B', [['Claude', 'update', '2.0.9 → 2.1.0（有新版）', ['1 個對話仍在用舊版執行', '2 個對話待換版']], ['Codex', 'notInstalled', '未安裝', []]]],
  ]) && dcli[1].name === '筆電 ' + RF.XSS && dcli[0].state === '在線' && /^離線/.test(dcli[1].state || ''), { dcli });

  // 瀏覽器版沒有桌面專屬的操作；帳戶選單只有改名、改色、上移、下移（步驟 10 起另有「從目錄刪除…」，WP4.8 起另有「在所有電腦登出…」）。
  // WP4.8 對指定電腦的按鈕都在 .ra-acts 裡（remote-accounts-scenarios.mjs 另外檢查），這裡不算
  const ui = await page.evaluate(() => {
    const scope = [...document.querySelectorAll('#dashboard .acct-grid, #dashboard .dash-sec-mx, #dashboard .dash-sec-dcli')];
    // 隱藏的不算（帳戶卡的健康度按鈕 .acard-health 在瀏覽器版一律藏著）
    const buttons = scope.flatMap((el) => [...el.querySelectorAll('button')]).filter((b) => !b.closest('.ra-acts') && getComputedStyle(b).display !== 'none');
    return {
      add: document.querySelectorAll('#dashboard .acard-add').length,
      ccard: document.querySelectorAll('#dashboard .ccard').length,
      policy: !!document.querySelector('#dashboard .cli-policy'), autoSw: !!document.querySelector('#dashboard .inline-switch'), check: !!document.querySelector('#dashboard [data-cli-check]'),
      buttons: buttons.map((b) => b.getAttribute('aria-label') || b.textContent.trim()),
      heads: [...document.querySelectorAll('#dashboard .dash-h')].map((x) => x.textContent),
    };
  });
  const menuFirst = await openCardMenu(page, 'la_c1');
  await closeMenus(page);
  const menuLast = await openCardMenu(page, 'la_x2');
  await closeMenus(page);
  check('hubHidesDesktopFeatures', ui.add === 0 && ui.ccard === 0 && !ui.policy && !ui.autoSw && !ui.check && ui.buttons.length === 6 && ui.buttons.every((b) => b === '帳戶動作')
    && JSON.stringify(ui.heads) === JSON.stringify(['帳戶', '帳戶 × 電腦', '電腦', '各電腦 CLI'])
    && JSON.stringify(menuFirst.map((m) => m.label)) === JSON.stringify(['重新命名…', '變更顏色…', '上移', '下移', '在所有電腦登出…', '從目錄刪除…']) && menuFirst[2].disabled && !menuFirst[3].disabled
    && !menuLast[2].disabled && menuLast[3].disabled,
    { ui, menuFirst, menuLast });

  // 上移：送 accountReorder（新順序、全部是邏輯帳戶 id），不先在畫面上排；伺服器推回 accounts 之後才換
  const order0 = await cardOrder(page);
  const r0 = hub.cmds('accountReorder').length;
  await openCardMenu(page, 'la_c2');
  await clickMenuItem(page, '上移');
  const sent = await until(() => hub.cmds('accountReorder')[r0] || null, 2000);
  await sleep(300);
  const orderAfterClick = await cardOrder(page);
  const ids = sent ? sent.cmd.ids : null;
  const accounts = RF.browserAccounts(NOW);
  if (ids) hub.push({ type: 'accounts', accounts: ids.map((id) => accounts.find((a) => a.id === id)) });
  const pushed = await until(async () => { const o = await cardOrder(page); return o[0] === 'la_c2' ? o : null; }, 2000);
  check('hubReorderSends', !!ids && JSON.stringify(ids) === JSON.stringify(['la_c2', 'la_c1', 'la_x1', 'la_c3', 'la_c4', 'la_x2']) && ids.every((x) => /^la_/.test(x))
    && JSON.stringify(Object.keys(sent.cmd).sort()) === JSON.stringify(['cmd', 'ids']) && JSON.stringify(orderAfterClick) === JSON.stringify(order0) && !!pushed && pushed[1] === 'la_c1',
    { ids, orderBefore: order0, orderAfterClick, orderAfterPush: pushed });

  // 改名、改色（openEditAccount 只送 label／color）、設定的主題：之後所有 Cmd 都在瀏覽器接受的清單裡
  await openCardMenu(page, 'la_c1');
  await clickMenuItem(page, '重新命名…');
  await page.waitForSelector('.dlg input.input', { timeout: 2000 });
  await page.fill('.dlg input.input', '改過的名字');
  await page.click('.dlg .dlg-foot .btn.primary');
  await sleep(300);
  await openCardMenu(page, 'la_x1');
  await clickMenuItem(page, '變更顏色…');
  await page.waitForSelector('.dlg .swatch', { timeout: 2000 });
  await page.click('.dlg .swatch[data-c="#7A7CF0"]');
  await page.click('.dlg .dlg-foot .btn.primary');
  await sleep(300);
  await openSettingsDialog(page);
  await clickTheme(page, 'light');
  await sleep(300);
  await page.keyboard.press('Escape');
  await sleep(200);
  const updates = hub.cmds('accountUpdate').map((a) => a.cmd);
  const names = hub.cmds().map((a) => a.cmd.cmd);
  check('hubNoDesktopOnlyFrames', names.every((n) => ALLOWED_HUB_CMDS.has(n)) && names.includes('accountReorder') && names.includes('setting')
    && updates.length === 2 && JSON.stringify(updates[0]) === JSON.stringify({ cmd: 'accountUpdate', accountId: 'la_c1', label: '改過的名字' })
    && JSON.stringify(updates[1]) === JSON.stringify({ cmd: 'accountUpdate', accountId: 'la_x1', color: '#7A7CF0' }),
    { names: [...new Set(names)], updates });

  // 不可信的字串照字面顯示
  const xss = await page.evaluate(() => ({
    flag: typeof window.__xss, imgs: document.querySelectorAll('#dashboard img[src="x"], body img[src="x"]').length,
    text: document.getElementById('dashboard').textContent,
  }));
  const literal = ['XSS ' + RF.XSS, '筆電 ' + RF.XSS, '讀不到憑證 ' + RF.XSS, RF.XSS + '@example.com'].filter((s) => !xss.text.includes(s));
  const pill = await page.evaluate(() => { const c = document.querySelector('#dashboard article.acard[data-key="la_c2"]'); return c ? { pill: c.querySelector('.pill').textContent, sub: c.querySelector('.acard-sub').textContent } : null; });
  check('xssLiteral', xss.flag === 'undefined' && xss.imgs === 0 && literal.length === 0 && !!pill && pill.pill === RF.XSS && pill.sub.includes(RF.XSS),
    { flag: xss.flag, imgs: xss.imgs, missing: literal, card: pill });
  await shot(page, 'hub-dashboard-1440');
  await closePage(app);
}

async function runHubPhone() {
  const app = await openHub('hub-phone', { vp: { w: 390, h: 844 } });
  const { page } = app;
  await waitLive(app);
  await sleep(300);
  const m = await page.evaluate(() => {
    const dash = document.getElementById('dashboard');
    const wrap = document.querySelector('#dashboard .mx-wrap');
    const wide = [...document.querySelectorAll('body *')].filter((el) => {
      if (el.closest('.mx-wrap')) return false;
      const r = el.getBoundingClientRect();
      return r.width > 0 && (r.right > window.innerWidth + 0.5 || r.left < -0.5);
    }).slice(0, 8).map((el) => el.className || el.tagName);
    return {
      docScroll: document.documentElement.scrollWidth, innerWidth: window.innerWidth,
      dashScroll: dash.scrollWidth, dashClient: dash.clientWidth,
      wrapScroll: wrap ? wrap.scrollWidth : null, wrapClient: wrap ? wrap.clientWidth : null,
      cols: getComputedStyle(document.querySelector('#dashboard .acct-grid')).gridTemplateColumns.split(' ').length,
      brandName: getComputedStyle(document.querySelector('.brand-name')).display,
      wide,
    };
  });
  await shot(page, 'hub-dashboard-390');
  // 捲到矩陣再拍一張
  await page.evaluate(() => document.querySelector('#dashboard .dash-sec-mx').scrollIntoView());
  await shot(page, 'hub-matrix-390');
  await page.evaluate(() => document.querySelector('#dashboard .dash-sec-dev').scrollIntoView());
  await shot(page, 'hub-devices-390');
  check('phone390NoHScroll', m.docScroll <= m.innerWidth && m.dashScroll <= m.dashClient && m.cols === 1 && m.brandName === 'none' && m.wide.length === 0 && m.wrapScroll !== null, m);
  await closePage(app);
}

async function runDesktopDashboard() {
  const accounts = RF.desktopAccounts(NOW).concat([RF.deviceCodeAccount()]);
  const app = await openDesktop('desktop-dash', { init: RF.desktopInit(NOW, { remote: RF.desktopRemote(NOW), devicesCli: RF.browserDevicesCli(NOW), accounts }) });
  const { page, host } = app;
  await showDashboard(page);
  const sections = await remoteSections(page);
  const mx = await readMatrix(page);
  const dcli = await readDeviceCli(page);
  check('desktopMatrixSelf', sections.matrix && sections.devicesCli && JSON.stringify(mx.rows.map((r) => r.id)) === JSON.stringify(['a1', 'a2'])
    && JSON.stringify(mx.cols.map((c) => [c.id, c.self])) === JSON.stringify([['d_A', true], ['d_B', false]])
    && mx.rows[0].cells[0].kind === 'ready' && mx.rows[1].cells[1].kind === 'absent' && dcli.length === 2 && dcli[0].self && !dcli[1].self,
    { sections, cols: mx.cols, rows: mx.rows.map((r) => [r.id, r.cells.map((c) => c.kind)]), dcli: dcli.map((d) => [d.device, d.self]) });

  // 電腦區（桌面）：可以改名、沒有撤銷，寫明撤銷要到網頁版（網址取 sync.server）；這台 verified:false 時警告；不顯示伺服器身分指紋
  const ddevs = await readDevices(page);
  const note = await page.evaluate(() => {
    const n = document.querySelector('#dashboard .dev-note');
    const a = n && n.querySelector('a');
    return n && !n.hidden ? { text: n.textContent, href: a ? a.getAttribute('href') : null } : null;
  });
  const dashText = await page.evaluate(() => document.getElementById('dashboard').textContent);
  const pr = host.posted.length;
  await page.click('#dashboard .dev[data-device="d_B"] .dev-rename');
  await waitDialog(page, 'dlg-dev-rename');
  await page.fill('.dlg-dev-rename input.input', '筆電');
  await page.click('.dlg-dev-rename .btn.primary');
  await sleep(200);
  const renamedDesk = host.posted.slice(pr).filter((m) => m.cmd === 'deviceRename');
  check('desktopDevicesNoRevoke', JSON.stringify(ddevs.map((d) => d.id)) === JSON.stringify(['d_A', 'd_B', 'd_R']) && ddevs.every((d) => !d.revoke) && ddevs[0].rename && ddevs[1].rename && !ddevs[2].rename
    && !!note && note.text.includes(RF.ORIGIN) && note.href === RF.ORIGIN + '/app/' && !dashText.includes('HUBF-INGE-RPRI-NT16')
    && renamedDesk.length === 1 && JSON.stringify(renamedDesk[0]) === JSON.stringify({ cmd: 'deviceRename', deviceId: 'd_B', name: '筆電' }),
    { devices: ddevs.map((d) => ({ id: d.id, rename: d.rename, revoke: d.revoke })), note, hubFingerprintShown: dashText.includes('HUBF-INGE-RPRI-NT16'), renamed: renamedDesk });
  check('desktopVerifiedWarning', ddevs[0].id === 'd_A' && ddevs[0].self && /名冊/.test(ddevs[0].warn || '') && !ddevs[1].warn && !ddevs[2].warn && !ddevs[1].self,
    { warn: ddevs.map((d) => [d.id, d.self, d.warn]) });

  // 裝置碼入口：deviceCodeLogin 而且不在登入中才有；按下送 accountLogin {method:'deviceCode'}
  const LABEL = '用裝置碼登入（可用手機核准）';
  const menuA9 = await openCardMenu(page, 'a9');
  const p0 = host.posted.length;
  if (menuA9.some((m) => m.label === LABEL)) await clickMenuItem(page, LABEL);
  else await closeMenus(page);
  await sleep(200);
  const sent = host.posted.slice(p0).filter((m) => m.cmd === 'accountLogin');
  const menuA1 = (await openCardMenu(page, 'a1')).map((m) => m.label);
  await closeMenus(page);
  const busy = accounts.map((a) => (a.id === 'a9' ? Object.assign({}, a, { loginBusy: true }) : a));
  await host.send({ type: 'accounts', accounts: busy });
  await sleep(150);
  const menuBusy = (await openCardMenu(page, 'a9')).map((m) => m.label);
  await closeMenus(page);
  const off = accounts.map((a) => (a.id === 'a9' ? Object.assign({}, a, { deviceCodeLogin: false }) : a));
  await host.send({ type: 'accounts', accounts: off });
  await sleep(150);
  const menuOff = (await openCardMenu(page, 'a9')).map((m) => m.label);
  await closeMenus(page);
  check('desktopDeviceCodeMenu', menuA9.some((m) => m.label === LABEL) && sent.length === 1 && JSON.stringify(sent[0]) === JSON.stringify({ cmd: 'accountLogin', accountId: 'a9', method: 'deviceCode' })
    && !menuA1.includes(LABEL) && !menuBusy.includes(LABEL) && !menuOff.includes(LABEL),
    { a9: menuA9.map((m) => m.label), sent, a1: menuA1.includes(LABEL), busy: menuBusy.includes(LABEL), off: menuOff.includes(LABEL) });

  // 宿主送 accountLogin {deviceCode}：帳戶卡顯示大字驗證碼與「複製」；done 之後消失
  await host.send({ type: 'accounts', accounts: busy });
  await app.context.grantPermissions(['clipboard-read', 'clipboard-write'], { origin: DESKTOP_ORIGIN }).catch(() => {});
  const dc = { userCode: 'WXYZ-1234', verificationUrl: 'https://auth.openai.com/codex/device', expiresAt: NOW + 15 * 60e3 };
  await host.send({ type: 'accountLogin', accountId: 'a9', message: '請用手機開啟網址並輸入驗證碼', done: false, deviceCode: dc });
  await sleep(200);
  const readCode = () => page.evaluate(() => {
    const c = document.querySelector('#dashboard article.acard[data-key="a9"]');
    const b = c && c.querySelector('.login-code');
    return b ? { code: b.querySelector('.login-code-val').textContent, mono: getComputedStyle(b.querySelector('.login-code-val')).fontFamily, size: getComputedStyle(b.querySelector('.login-code-val')).fontSize,
      copy: !!b.querySelector('.login-code-copy'), url: b.querySelector('.login-code-url') ? b.querySelector('.login-code-url').textContent : null, exp: b.querySelector('.login-code-exp') ? b.querySelector('.login-code-exp').textContent : null } : null;
  });
  const shown = await readCode();
  await shot(page, 'desktop-device-code-1440');
  const p1 = host.posted.length;
  await page.click('#dashboard article.acard[data-key="a9"] .login-code-copy');
  const copied = await until(async () => {
    const toasts = await domToasts(page);
    const viaHost = host.posted.slice(p1).find((m) => m.cmd === 'copyText');
    return toasts.includes('已複製驗證碼') ? 'clipboard' : viaHost && viaHost.text === 'WXYZ-1234' ? 'host' : null;
  }, 2000);
  await host.send({ type: 'accountLogin', accountId: 'a9', message: '登入完成', done: true, success: true });
  await sleep(200);
  const after = await readCode();
  check('desktopDeviceCodeShown', !!shown && shown.code === 'WXYZ-1234' && /Cascadia|Consolas|monospace/i.test(shown.mono) && parseFloat(shown.size) >= 20 && shown.copy
    && shown.url === dc.verificationUrl && /內有效$/.test(shown.exp || '') && !!copied && after === null,
    { shown, copied, afterDone: after });
  await closePage(app);
}

// ───── 步驟 10：電腦頁、改名、撤銷與從目錄刪除（近期驗證）─────

/** 電腦區每一台的內容 */
function readDevices(page) {
  return page.evaluate(() => [...document.querySelectorAll('#dashboard .dev[data-device]')].map((c) => {
    const t = (sel) => { const x = c.querySelector(sel); return x ? x.textContent : null; };
    const dts = [...c.querySelectorAll('.dev-rows dt')];
    return {
      id: c.dataset.device, name: t('.dev-name'), self: !!c.querySelector('.dev-head .tag'), state: t('.dev-state'), signed: t('.dev-signed'), warn: t('.dev-warn'),
      rows: Object.fromEntries(dts.map((dt) => [dt.textContent, dt.nextElementSibling ? dt.nextElementSibling.textContent : null])), policy: t('.dev-policy'),
      rename: !!c.querySelector('.dev-rename'), revoke: !!c.querySelector('.dev-revoke'), revoked: c.classList.contains('is-revoked'),
    };
  }));
}

/** 等某個對話框出現（className），回傳有沒有 */
/**
 * 只認沒在淡出的對話框：關掉的框加上 .dlg-back.leaving 之後 120 毫秒才移除（ui.js 的 openDialog），
 * 這段期間緊接著開的同類框（例如撤銷 503 之後的「從目錄刪除」確認框）排在它後面，只看 .dlg.{cls} 會拿到正在淡出的舊框
 */
const OPEN_DLG = '.dlg-back:not(.leaving) .dlg';
const waitDialog = (page, cls) => page.waitForSelector(`${OPEN_DLG}.${cls}`, { timeout: 3000 }).then(() => true).catch(() => false);
const dialogOpen = (page, cls) => page.evaluate(([sel, c]) => !!document.querySelector(`${sel}.${c}`), [OPEN_DLG, cls]);
/** 擁有者請求的摘要（方法、路徑、CSRF 與 Content-Type 標頭、本文） */
const ownerReqs = (api, from) => api.requests.slice(from).filter((r) => r.method === 'POST').map((r) => ({
  path: r.path, csrf: r.headers['x-myriagent-csrf'] || null, type: r.headers['content-type'] || null, body: r.body, status: r.status,
}));

/** 撤銷某台：按「撤銷…」、選原因、按確認框的「撤銷」 */
async function startRevoke(page, deviceId, reason) {
  await page.click(`#dashboard .dev[data-device="${deviceId}"] .dev-revoke`);
  await waitDialog(page, 'dlg-revoke');
  if (reason) await page.check(`${OPEN_DLG}.dlg-revoke input[name="revoke-reason"][value="${reason}"]`);
  if (reason === 'compromised' && !report.screenshots.includes('hub-revoke-1440.png')) await shot(page, 'hub-revoke-1440');
  await page.click(`${OPEN_DLG}.dlg-revoke .btn.danger-solid`);
}

async function submitPassword(page, password) {
  await page.fill(`${OPEN_DLG}.dlg-stepup input[type="password"]`, password);
  await page.click(`${OPEN_DLG}.dlg-stepup .dlg-foot .btn.primary`);
}

const STEP_UP = { status: 403, body: { error: 'step_up_required', stepUp: true } };
const OK = { status: 200, body: { ok: true } };

async function runHubDevices() {
  const app = await openHub('hub-devices');
  const { page, hub, api } = app;
  app.expect((m) => /Failed to load resource/.test(m.text) && /status of (401|403|409|429|503)/.test(m.text) && /\/api\/v1\/(devices|accounts|auth\/stepup)/.test(m.url), '擁有者端點的錯誤回應（刻意）');
  await waitLive(app);

  // 電腦頁的欄位：撤銷的排最後、政策只顯示、配對時名稱不同才另標；沒有伺服器身分指紋
  const devs = await readDevices(page);
  const byId = Object.fromEntries(devs.map((d) => [d.id, d]));
  const page0 = await page.evaluate(() => {
    const sec = document.querySelector('#dashboard .dash-sec-dev');
    const add = sec && sec.querySelector('.dev-add a');
    return { visible: !!sec && !sec.hidden, add: add ? add.getAttribute('href') : null, text: sec ? sec.textContent : '', heads: [...document.querySelectorAll('#dashboard .dash-h')].map((x) => x.textContent) };
  });
  const A = byId.d_A, B = byId.d_B, Rv = byId.d_R;
  check('devicesPageFields', page0.visible && JSON.stringify(devs.map((d) => d.id)) === JSON.stringify(['d_A', 'd_B', 'd_R'])
    && A.name === 'OFFICE-PC' && A.state === '在線' && JSON.stringify(A.rows) === JSON.stringify({ 電腦名稱: 'OFFICE-PC', 版本: '0.3.0', 裝置指紋: 'ABCD-EFGH', 金鑰位置: 'TPM' }) && A.policy === '其他電腦的遠端操作：不接受'
    && B.name === '筆電 ' + RF.XSS && /^離線・最後上線 /.test(B.state || '') && B.rows['金鑰位置'] === '軟體' && B.policy === '其他電腦的遠端操作：只能查看'
    && Rv.revoked && /^已撤銷・退役・/.test(Rv.state || '') && page0.add === '/#pair' && !/HUBF|hubFingerprint/.test(page0.text) && !devs.some((d) => d.self || d.warn)
    && JSON.stringify(page0.heads) === JSON.stringify(['帳戶', '帳戶 × 電腦', '電腦', '各電腦 CLI']),
    { devices: devs, addLink: page0.add, heads: page0.heads });
  check('signedNameShown', B.signed === '配對時名稱：LAPTOP' && A.signed === null && Rv.signed === null, { A: A.signed, B: B.signed, R: Rv.signed });
  check('revokedNoRename', !Rv.rename && !Rv.revoke && A.rename && A.revoke && B.rename && B.revoke, { A: [A.rename, A.revoke], B: [B.rename, B.revoke], R: [Rv.rename, Rv.revoke] });
  await page.evaluate(() => document.querySelector('#dashboard .dash-sec-dev').scrollIntoView());
  await shot(page, 'hub-devices-1440');

  // 改名：清理後 1～60 字才送；送 deviceRename {deviceId, name}
  const r0 = hub.cmds('deviceRename').length;
  await page.click('#dashboard .dev[data-device="d_A"] .dev-rename');
  await waitDialog(page, 'dlg-dev-rename');
  const maxlength = await page.getAttribute('.dlg-dev-rename input.input', 'maxlength');
  await page.fill('.dlg-dev-rename input.input', '   ');
  const blank = await page.evaluate(() => ({ disabled: document.querySelector('.dlg-dev-rename .btn.primary').disabled, err: !document.querySelector('.dlg-dev-rename .dev-name-err').hidden }));
  await page.press('.dlg-dev-rename input.input', 'Enter');
  await sleep(200);
  const sentBlank = hub.cmds('deviceRename').length - r0;
  await page.fill('.dlg-dev-rename input.input', '  辦公室  ');
  await page.click('.dlg-dev-rename .btn.primary');
  const renamed = await until(() => hub.cmds('deviceRename')[r0] || null, 2000);
  check('deviceRenameSends', maxlength === '60' && blank.disabled && blank.err && sentBlank === 0 && !!renamed
    && JSON.stringify(renamed.cmd) === JSON.stringify({ cmd: 'deviceRename', deviceId: 'd_A', name: '辦公室' }) && hub.cmds('deviceRename').length === r0 + 1,
    { maxlength, blank, sentBlank, sent: renamed && renamed.cmd });

  // 撤銷：403 step_up_required → 密碼框（焦點在密碼欄）→ stepup → 原請求重送一次；三個請求都帶 CSRF 與 JSON；密碼送出就清空；畫面不自己改
  const n0 = api.requests.length;
  api.queue('/api/v1/devices/d_B/revoke', [STEP_UP, OK]);
  api.set('/api/v1/auth/stepup', async () => { await sleep(600); return OK; });
  const devsBefore = JSON.stringify(await readDevices(page));
  await startRevoke(page, 'd_B', 'compromised');
  const pwOpen = await waitDialog(page, 'dlg-stepup');
  await sleep(150);
  const focus = await page.evaluate(() => { const el = document.activeElement; return el ? { type: el.getAttribute('type'), autocomplete: el.getAttribute('autocomplete') } : null; });
  await shot(page, 'hub-stepup-1440');
  await submitPassword(page, 'correct horse');
  await sleep(200);
  const midway = await page.evaluate(() => { const i = document.querySelector('.dlg-stepup input[type="password"]'); return { value: i ? i.value : null, open: !!i }; });
  await until(() => api.count('/api/v1/devices/d_B/revoke') >= 2, 4000);
  await sleep(300);
  const reqs = ownerReqs(api, n0);
  const closed = !(await dialogOpen(page, 'dlg-stepup'));
  const devsAfter = JSON.stringify(await readDevices(page));
  const okToast = (await domToasts(page)).some((t) => /^已撤銷「/.test(t));
  check('revokeStepUpFlow', pwOpen && !!focus && focus.type === 'password' && focus.autocomplete === 'current-password' && midway.open && midway.value === ''
    && JSON.stringify(reqs.map((r) => [r.path, r.status])) === JSON.stringify([['/api/v1/devices/d_B/revoke', 403], ['/api/v1/auth/stepup', 200], ['/api/v1/devices/d_B/revoke', 200]])
    && reqs.every((r) => r.csrf === '1' && /^application\/json/.test(r.type || '')) && reqs[0].body === '{"reason":"compromised"}' && reqs[2].body === reqs[0].body
    && reqs[1].body === '{"password":"correct horse"}' && closed && devsAfter === devsBefore && okToast,
    { passwordDialog: pwOpen, focus, whileWaiting: midway, requests: reqs, closed, unchangedBeforePush: devsAfter === devsBefore, okToast });

  // 密碼錯誤：框內顯示、可以再試
  const n1 = api.requests.length;
  api.queue('/api/v1/devices/d_A/revoke', [STEP_UP, OK]);
  api.queue('/api/v1/auth/stepup', [{ status: 403, body: { error: 'password_failed' } }, OK]);
  await startRevoke(page, 'd_A');
  await waitDialog(page, 'dlg-stepup');
  await submitPassword(page, 'wrong');
  const wrong = await until(() => page.evaluate(() => { const e = document.querySelector('.dlg-stepup .stepup-err'); return e && !e.hidden ? e.textContent : null; }), 3000);
  const stillOpen = await dialogOpen(page, 'dlg-stepup');
  await submitPassword(page, 'right');
  await until(() => api.count('/api/v1/devices/d_A/revoke') >= 2, 3000);
  await sleep(300);
  const reqs1 = ownerReqs(api, n1);
  check('revokeWrongPassword', wrong === '密碼錯誤' && stillOpen && !(await dialogOpen(page, 'dlg-stepup'))
    && JSON.stringify(reqs1.map((r) => [r.path, r.status])) === JSON.stringify([['/api/v1/devices/d_A/revoke', 403], ['/api/v1/auth/stepup', 403], ['/api/v1/auth/stepup', 200], ['/api/v1/devices/d_A/revoke', 200]])
    && JSON.parse(reqs1[1].body).reason === undefined && reqs1[0].body === '{"reason":"retired"}',
    { error: wrong, stillOpen, requests: reqs1.map((r) => [r.path, r.status, r.body]) });

  // 太頻繁：429＋Retry-After 30 → 框內「嘗試太頻繁，30 秒後再試」；取消之後不再送任何請求
  const n2 = api.requests.length;
  api.queue('/api/v1/devices/d_A/revoke', [STEP_UP]);
  api.queue('/api/v1/auth/stepup', [{ status: 429, headers: { 'Retry-After': '30' }, body: { error: 'rate_limited' } }]);
  await startRevoke(page, 'd_A');
  await waitDialog(page, 'dlg-stepup');
  await submitPassword(page, 'pw');
  const limited = await until(() => page.evaluate(() => { const e = document.querySelector('.dlg-stepup .stepup-err'); return e && !e.hidden ? e.textContent : null; }), 3000);
  check('revokeStepUpRateLimited', limited === '嘗試太頻繁，30 秒後再試' && (await dialogOpen(page, 'dlg-stepup')), { error: limited, requests: ownerReqs(api, n2).map((r) => [r.path, r.status]) });
  await page.click('.dlg-stepup .dlg-foot .btn:not(.primary)');
  await sleep(400);
  const afterCancelPw = ownerReqs(api, n2).length;

  // 取消：確認框取消不送請求；密碼框按 Esc 不送 stepup、不重送
  const n3 = api.requests.length;
  await page.click('#dashboard .dev[data-device="d_A"] .dev-revoke');
  await waitDialog(page, 'dlg-revoke');
  await page.click('.dlg-revoke .dlg-foot .btn:not(.danger-solid)');
  await sleep(300);
  const afterConfirmCancel = api.requests.length - n3;
  api.queue('/api/v1/devices/d_A/revoke', [STEP_UP]);
  await startRevoke(page, 'd_A');
  await waitDialog(page, 'dlg-stepup');
  await page.keyboard.press('Escape');
  await sleep(400);
  const escReqs = ownerReqs(api, n3);
  check('revokeCancelNoRequest', afterCancelPw === 2 && afterConfirmCancel === 0 && JSON.stringify(escReqs.map((r) => [r.path, r.status])) === JSON.stringify([['/api/v1/devices/d_A/revoke', 403]])
    && !(await dialogOpen(page, 'dlg-stepup')),
    { afterPasswordCancel: afterCancelPw, afterConfirmCancel, afterEsc: escReqs.map((r) => [r.path, r.status]) });

  // 409、503 的提示
  api.queue('/api/v1/devices/d_A/revoke', [{ status: 409, body: { error: 'already_revoked' } }]);
  await startRevoke(page, 'd_A');
  const t409 = await until(async () => (await domToasts(page)).find((t) => t === '這台電腦已經撤銷了') || null, 3000);
  check('revoke409Message', !!t409, { toasts: await domToasts(page) });
  api.queue('/api/v1/devices/d_A/revoke', [{ status: 503, body: { error: 'unavailable' } }]);
  await startRevoke(page, 'd_A');
  const t503 = await until(async () => (await domToasts(page)).find((t) => /可能已經撤銷/.test(t)) || null, 3000);
  check('revoke503Message', !!t503, { toast: t503 });

  // 帳戶選單正好六項（WP4.8 加「在所有電腦登出…」，檢查名稱沿用）；從目錄刪除：確認文字、路徑、{} 本文；畫面不自己改
  const menu = await openCardMenu(page, 'la_x1');
  const n4 = api.requests.length;
  api.queue('/api/v1/accounts/la_x1/delete', [STEP_UP, OK]);
  api.set('/api/v1/auth/stepup', OK);
  await clickMenuItem(page, '從目錄刪除…');
  await waitDialog(page, 'dlg-confirm');
  const confirmText = await page.evaluate((sel) => document.querySelector(sel + '.dlg-confirm').textContent, OPEN_DLG);
  await page.click(`${OPEN_DLG}.dlg-confirm .btn.danger-solid`);
  await waitDialog(page, 'dlg-stepup');
  await submitPassword(page, 'pw');
  await until(() => api.count('/api/v1/accounts/la_x1/delete') >= 2, 3000);
  await sleep(300);
  const reqs4 = ownerReqs(api, n4);
  const cards = await cardOrder(page);
  check('hubAccountMenuFive', JSON.stringify(menu.map((m) => m.label)) === JSON.stringify(['重新命名…', '變更顏色…', '上移', '下移', '在所有電腦登出…', '從目錄刪除…']), { menu });
  check('deleteAccountFlow', confirmText.includes('電腦之後再回報同一個登入身分時，這個帳戶會再出現') && confirmText.includes('Codex Pro')
    && JSON.stringify(reqs4.map((r) => [r.path, r.status])) === JSON.stringify([['/api/v1/accounts/la_x1/delete', 403], ['/api/v1/auth/stepup', 200], ['/api/v1/accounts/la_x1/delete', 200]])
    && reqs4[0].body === '{}' && reqs4[2].body === '{}' && reqs4.every((r) => r.csrf === '1' && /^application\/json/.test(r.type || '')) && cards.includes('la_x1'),
    { confirm: confirmText, requests: reqs4.map((r) => [r.path, r.status, r.body]), cardsStillHasIt: cards.includes('la_x1') });

  // 不送任何以 la_… 當 deviceId、或桌面專屬的指令
  const names = hub.cmds().map((a) => a.cmd.cmd);
  // 登入失效：任何擁有者請求 401 → 到 /
  api.queue('/api/v1/devices/d_A/revoke', [{ status: 401, body: { error: 'unauthenticated' } }]);
  await startRevoke(page, 'd_A');
  const redirected = await until(async () => { const d = await domState(page); return d.path === '/' && d.authPage ? d : null; }, 4000, 50);
  check('ownerPost401Redirect', !!redirected && names.every((n) => ALLOWED_HUB_CMDS.has(n)), { path: redirected && redirected.path, names: [...new Set(names)] });
  await closePage(app);
}

// ───── 步驟 11：側欄裝置徽章、標題列同步小點、remoteOnly 卡片（開關仍關：remoteOnly 由夾具直接放進 init，和宿主的開關無關）─────

/** 頂部列的同步小點 */
function readSyncDot(page) {
  return page.evaluate(() => {
    const b = document.getElementById('syncDot');
    if (!b) return { exists: false, visible: false };
    const visible = !b.hidden && getComputedStyle(b).display !== 'none' && b.getBoundingClientRect().width > 0;
    const next = b.nextElementSibling;
    return {
      exists: true, visible, state: b.dataset.state || null, tone: b.dataset.tone || null, text: b.querySelector('.sync-dot-text').textContent,
      label: b.getAttribute('aria-label'), title: b.title, beforeCliBadge: !!next && next.id === 'cliBadge',
    };
  });
}

/** 側欄的帳戶精簡卡（照畫面順序） */
function readMiniCards(page) {
  return page.evaluate(() => [...document.querySelectorAll('#sidebar .macct')].map((c) => {
    const devs = c.querySelector('.macct-devs');
    return {
      id: c.dataset.key, remoteOnly: c.classList.contains('remote-only'), draggable: c.draggable, newBtn: !!c.querySelector('.macct-new'),
      sub: c.querySelector('.macct-sub-text').textContent, filtered: c.classList.contains('filtered'),
      dots: [...c.querySelectorAll('.macct-devs .ddot')].map((d) => [d.dataset.device, d.dataset.tone]),
      devsVisible: !!devs && !devs.hidden && devs.getBoundingClientRect().width > 0,
    };
  }));
}

/** 儀表板的一張帳戶大卡：圓環、用量來源、說明、看得到的按鈕 */
function readBigCard(page, key) {
  return page.evaluate((k) => {
    const c = document.querySelector(`#dashboard article.acard[data-key="${k}"]`);
    if (!c) return null;
    const vis = (el) => !!el && !el.hidden && getComputedStyle(el).display !== 'none' && el.getBoundingClientRect().width > 0;
    return {
      remoteOnly: c.classList.contains('remote-only'), rings: c.querySelectorAll('.rings .ring-col').length, ringsVisible: vis(c.querySelector('.rings')),
      src: [...c.querySelectorAll('.note-src')].map((n) => n.textContent), empty: c.querySelector('.note-empty') ? c.querySelector('.note-empty').textContent : null,
      buttons: [...c.querySelectorAll('.acard-foot button')].filter(vis).map((b) => b.textContent.trim()), border: getComputedStyle(c).borderTopStyle,
    };
  }, key);
}

const menuLabels = (page) => page.evaluate(() => [...document.querySelectorAll('.menu .menu-item')].map((b) => b.querySelector('.mi-label').textContent));
const LOGIN_HERE = '在這台登入';
const RO_SENT = {
  la_ro1: { cmd: 'accountAdd', provider: 'Claude', label: '只在筆電（有用量）', color: '#C678DD', logicalAccountId: 'la_ro1' },
  la_ro2: { cmd: 'accountAdd', provider: 'Codex', label: '只在筆電（沒有用量）', color: '#56B6C2', logicalAccountId: 'la_ro2' },
};
const sameJson = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const addsOf = (host, from = 0) => host.posted.slice(from).filter((m) => m.cmd === 'accountAdd');
/** 任何以 la_…（邏輯帳戶 id）當 accountId 的指令、或 ids 含 la_… 的 accountReorder */
const laLeaks = (posted) => posted.filter((m) => (typeof m.accountId === 'string' && m.accountId.startsWith('la_'))
  || (m.cmd === 'accountReorder' && Array.isArray(m.ids) && m.ids.some((x) => String(x).startsWith('la_'))));

/** 桌面 init：本機帳戶與 remoteOnly 交錯（a1、la_ro1、a2、a3、la_ro2），開著一個用 a1 的對話 */
function remoteOnlyInit() {
  const [a1, a2, a3] = RF.desktopAccounts(NOW);
  const [ro1, ro2] = RF.remoteOnlyAccounts(NOW);
  return Object.assign(RF.desktopInit(NOW, { accounts: [a1, ro1, a2, a3, ro2] }), {
    conversations: [{ id: 'c1', accountId: 'a1', provider: 'Claude', title: '同步探針的對話', updatedAt: NOW - 60e3, workingDirectory: 'C:\\repo\\probe', customFolder: true, running: false, waiting: false, unread: false }],
    layout: { panes: [{ id: 'p1', tabs: ['c1'], active: 'c1' }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' },
  });
}

/** 宿主收到 open c1 就回對話（換帳戶選單要有 header） */
function answerOpen(msg, host) {
  if (msg.cmd !== 'open' || msg.conversationId !== 'c1') return;
  const header = Object.assign(baseHeader('c1', 'a1', '同步探針的對話'), { workingDirectory: 'C:\\repo\\probe' });
  host.send({ type: 'conversation', conversationId: 'c1', paneId: null, header, items: [], hasMore: false, composer: { attachments: [], busy: false, busyText: null, queue: [] } }).catch(() => {});
}

async function showChat(page) {
  await page.click('#viewSwitch [data-view="chat"]');
  await page.waitForFunction(() => !document.getElementById('sidebar').hidden, null, { timeout: 3000 });
}

async function runDesktopRemoteOnly() {
  const app = await openDesktop('desktop-remote-only', { init: remoteOnlyInit(), onPost: answerOpen });
  const { page, host } = app;

  // ── 同步小點：init.remote 是 null → 不顯示；之後才收到 sync 才出現；六種狀態、confirm、時間差；sync 是 null → 隱藏；點了開設定
  const dot0 = await readSyncDot(page);
  const cases = [
    [RF.syncPairing(NOW), 'pairing', 'info', '同步：配對中'],
    [RF.syncOnline(NOW, { state: 'connecting' }), 'connecting', 'info', '同步：連線中'],
    [RF.syncOnline(NOW), 'online', 'ok', '同步：已連線'],
    [RF.syncOnline(NOW, { state: 'offline', pending: 12, detail: '伺服器連不上，30 秒後重試' }), 'offline', 'warn', '同步：離線（12 筆待送）'],
    [RF.syncOnline(NOW, { state: 'paused' }), 'paused', 'muted', '同步：已暫停'],
    [RF.syncOnline(NOW, { state: 'stopped', detail: '這台已被撤銷' }), 'stopped', 'bad', '同步：已停止（這台已被撤銷）'],
    [RF.syncOnline(NOW, { confirm: { hubFingerprint: 'HUBF-INGE-RPRI-NT16' } }), 'online', 'bad', '同步：要在這台確認伺服器身分'],
  ];
  const states = [];
  for (const [sync, st, tn, text] of cases) {
    await host.send({ type: 'sync', sync });
    await sleep(60);
    const d = await readSyncDot(page);
    states.push({ want: [st, tn, text], got: [d.state, d.tone, d.text, d.label], visible: d.visible, ok: d.visible && d.state === st && d.tone === tn && d.text === text && d.label === text });
  }
  await host.send({ type: 'sync', sync: RF.syncOnline(NOW, { clockSkewMs: -185000 }) });
  await sleep(60);
  const skew = await readSyncDot(page);
  await host.send({ type: 'sync', sync: null });
  await sleep(60);
  const gone = await readSyncDot(page);
  await host.send({ type: 'sync', sync: RF.syncOnline(NOW) });
  await sleep(60);
  const back = await readSyncDot(page);
  await page.click('#syncDot');
  const settingsOpened = await waitDialog(page, 'dlg-settings');
  await page.keyboard.press('Escape');
  await sleep(300);
  check('syncDotStates', dot0.exists && !dot0.visible && states.every((x) => x.ok) && skew.visible && skew.tone === 'warn' && /Windows 時間/.test(skew.title)
    && !gone.visible && back.visible && back.state === 'online' && back.beforeCliBadge && settingsOpened,
    { initial: dot0, states, skew: { tone: skew.tone, title: skew.title }, afterNull: gone.visible, back, settingsOpened });

  // ── 側欄的裝置小點（devices 是陣列的帳戶才有；absent 的不畫）；內容變了才重畫
  await host.send({ type: 'devices', devices: RF.desktopDevices(NOW) });
  await sleep(150);
  const minis = await readMiniCards(page);
  const mini = Object.fromEntries(minis.map((m) => [m.id, m]));
  const [a1, a2, a3] = RF.desktopAccounts(NOW);
  const [ro1, ro2] = RF.remoteOnlyAccounts(NOW);
  const a1err = Object.assign({}, a1, { devices: [a1.devices[0], Object.assign({}, a1.devices[1], { status: 'error', statusText: '讀不到憑證' })] });
  await host.send({ type: 'accounts', accounts: [a1err, ro1, a2, a3, ro2] });
  await sleep(150);
  const minis2 = Object.fromEntries((await readMiniCards(page)).map((m) => [m.id, m]));
  await host.send({ type: 'accounts', accounts: [a1, ro1, a2, a3, ro2] });
  await sleep(150);
  await shot(page, 'desktop-sidebar-remote-1440');
  check('sidebarDeviceDots', sameJson(mini.a1.dots, [['d_A', 'ok'], ['d_B', 'off']]) && mini.a1.devsVisible && sameJson(mini.a2.dots, [['d_A', 'ok']])
    && mini.a3.dots.length === 0 && !mini.a3.devsVisible && sameJson(mini.la_ro1.dots, [['d_B', 'off']]) && sameJson(minis2.a1.dots, [['d_A', 'ok'], ['d_B', 'bad']]),
    { dots: minis.map((m) => [m.id, m.dots, m.devsVisible]), afterError: minis2.a1.dots });

  // ── remoteOnly：精簡卡沒有＋、不能拖曳、副標題「這台未登入」；點擊、右鍵、Enter 都只開「在這台登入」
  const roCard = (id) => `#sidebar .macct[data-key="${id}"]`;
  await page.click(roCard('la_ro1'));
  await page.waitForSelector('.menu', { timeout: 2000 }).catch(() => {});
  const byClick = await menuLabels(page);
  const p0 = host.posted.length;
  if (sameJson(byClick, [LOGIN_HERE])) await clickMenuItem(page, LOGIN_HERE);
  else await closeMenus(page);
  await sleep(150);
  const sentMini = addsOf(host, p0);
  await page.click(roCard('la_ro2'), { button: 'right' });
  await page.waitForSelector('.menu', { timeout: 2000 }).catch(() => {});
  const byRight = await menuLabels(page);
  await closeMenus(page);
  await page.focus(roCard('la_ro2'));
  await page.keyboard.press('Enter');
  await page.waitForSelector('.menu', { timeout: 2000 }).catch(() => {});
  const byEnter = await menuLabels(page);
  await closeMenus(page);
  await sleep(100);
  const filteredAny = (await readMiniCards(page)).some((m) => m.filtered);
  // 換帳戶選單、帳戶篩選下拉都不含 remoteOnly
  await page.waitForSelector('.conv[data-conv="c1"] .ch-acct', { state: 'visible', timeout: 3000 }).catch(() => {});
  await page.click('.conv[data-conv="c1"] .ch-acct');
  await page.waitForSelector('.menu', { timeout: 2000 }).catch(() => {});
  const switchMenu = await menuLabels(page);
  await closeMenus(page);
  // 側欄的帳戶篩選下拉：選單不列 remoteOnly
  const chipBtn = await page.$('#sidebar .sb-filters [data-field="historyAccount"]');
  const chipsHidden = !chipBtn || (await chipBtn.evaluate((b) => getComputedStyle(b).display === 'none'));
  if (!chipsHidden) {
    await chipBtn.click();
    await page.waitForSelector('.menu', { timeout: 2000 }).catch(() => {});
  }
  const chips = { hidden: chipsHidden, labels: chipsHidden ? [] : await menuLabels(page) };
  if (!chipsHidden) await closeMenus(page);

  // ── 儀表板：摘要不算 remoteOnly、大卡（有共享用量畫圓環並標「其他電腦回報」，沒有的寫「這台未登入」）、按鈕與選單只有「在這台登入」
  await showDashboard(page);
  await sleep(150);
  const summary = await page.evaluate(() => {
    const first = document.querySelector('#dashboard .dash-summary .sum-item');
    return first ? { n: first.querySelector('.sum-n').textContent, remote: first.querySelector('.sum-remote') ? first.querySelector('.sum-remote').textContent : null, text: first.textContent } : null;
  });
  const big1 = await readBigCard(page, 'la_ro1');
  const big2 = await readBigCard(page, 'la_ro2');
  const bigA1 = await readBigCard(page, 'a1');
  const bigMenu = (await openCardMenu(page, 'la_ro1')).map((m) => m.label);
  await closeMenus(page);
  const p1 = host.posted.length;
  await page.click('#dashboard article.acard[data-key="la_ro2"] .acard-here');
  await sleep(100);
  await page.click('#dashboard article.acard[data-key="la_ro2"] .acard-here');
  await sleep(150);
  const sentBig = addsOf(host, p1);
  await shot(page, 'desktop-remote-only-1440');
  // 新增帳戶對話框的預設名稱（count(p)）不算 remoteOnly：Claude 本機有 2 個 → 「Claude 帳戶 3」，Codex 本機 1 個 → 「Codex 帳戶 2」
  await page.click('#dashboard .acard-add');
  await waitDialog(page, 'dlg-add');
  const phClaude = await page.getAttribute('.dlg-add input.input', 'placeholder');
  await page.click('.dlg-add .prov[data-p="Codex"]');
  const phCodex = await page.getAttribute('.dlg-add input.input', 'placeholder');
  await page.keyboard.press('Escape');
  await sleep(300);

  // ── 拖曳兩張本機卡：ids 只含本機帳戶、remoteOnly 留在原位；落在 remoteOnly 卡上、拖 remoteOnly 卡都不送
  await showChat(page);
  const a2h = await page.$eval('#sidebar .macct[data-key="a2"]', (n) => n.getBoundingClientRect().height);
  const r0 = host.posted.length;
  await page.dragAndDrop('#sidebar .macct[data-key="a1"]', '#sidebar .macct[data-key="a2"]', { targetPosition: { x: 30, y: a2h - 6 } });
  const reorder = await until(() => host.posted.slice(r0).find((m) => m.cmd === 'accountReorder') || null, 2000);
  const orderAfter = (await readMiniCards(page)).map((m) => m.id);
  const r1 = host.posted.length;
  await page.dragAndDrop('#sidebar .macct[data-key="a3"]', '#sidebar .macct[data-key="la_ro2"]', { targetPosition: { x: 30, y: 6 } });
  await sleep(150);
  await page.dragAndDrop('#sidebar .macct[data-key="la_ro1"]', '#sidebar .macct[data-key="a1"]', { targetPosition: { x: 30, y: 6 } });
  await sleep(150);
  await closeMenus(page);
  const extraReorders = host.posted.slice(r1).filter((m) => m.cmd === 'accountReorder');

  const leaks1 = laLeaks(host.posted);
  const page1 = { mini, byClick, byRight, byEnter, sentMini, filteredAny, switchMenu, chips, summary, big1, big2, bigA1, bigMenu, sentBig, phClaude, phCodex, reorder, orderAfter, extraReorders, leaks1 };

  // ── 只在這台用過、從這台移除之後留在目錄裡的帳戶（devices 只有 absent）：不說「在其他電腦登入過」；移除已連結的帳戶時確認框先講
  const solo = Object.assign({}, ro2, {
    id: 'la_ro3', logicalId: 'la_ro3', label: '只在這台用過', color: '#E5C07B',
    devices: ro2.devices.map((d) => Object.assign({}, d, { status: 'absent', statusText: '尚未加入', email: null, usagePoller: false })),
  });
  await host.send({ type: 'accounts', accounts: [a1, ro1, a2, a3, ro2, solo] });
  await sleep(150);
  const titleOf = (id) => page.$eval(`#sidebar .macct[data-key="${id}"]`, (n) => n.title).catch(() => null);
  const soloTitle = await titleOf('la_ro3');
  const ro2Title = await titleOf('la_ro2');
  await showDashboard(page);
  await sleep(150);
  const soloBig = await readBigCard(page, 'la_ro3');
  const ro2Big = await readBigCard(page, 'la_ro2');
  const summary2 = await page.evaluate(() => { const x = document.querySelector('#dashboard .dash-summary .sum-remote'); return x ? x.textContent : null; });
  const removeDialog = async (key) => {
    const labels = (await openCardMenu(page, key)).map((m) => m.label);
    if (!labels.includes('移除帳戶…')) { await closeMenus(page); return { labels, text: null }; }
    await clickMenuItem(page, '移除帳戶…');
    await waitDialog(page, 'dlg-confirm');
    const text = await page.evaluate((sel) => { const d = document.querySelector(sel + '.dlg-confirm .confirm-detail'); return d ? d.textContent : ''; }, OPEN_DLG);
    await page.click(`${OPEN_DLG}.dlg-confirm .dlg-foot .btn:not(.danger-solid)`);
    await sleep(300);
    return { labels, text };
  };
  const p2 = host.posted.length;
  const removeLinked = await removeDialog('a1');
  const removeUnlinked = await removeDialog('a3');
  const removeSent = host.posted.slice(p2).filter((m) => m.cmd === 'accountRemove');
  await shot(page, 'desktop-remote-only-solo-1440');
  check('remoteOnlyNoOtherDevice', /目前沒有電腦登入/.test(soloTitle || '') && !/其他電腦登入過/.test(soloTitle || '') && /只在其他電腦登入過/.test(ro2Title || '')
    && !!soloBig && /還在帳戶目錄裡，目前沒有電腦登入/.test(soloBig.empty || '') && /網頁版/.test(soloBig.empty || '') && !/其他電腦登入過/.test(soloBig.empty || '')
    && !!ro2Big && /其他電腦登入過/.test(ro2Big.empty || '') && sameJson(soloBig.buttons, [LOGIN_HERE])
    && summary2 === '（另有 3 個在帳戶目錄、這台未登入）'
    && /仍留在帳戶目錄/.test(removeLinked.text || '') && /從目錄刪除/.test(removeLinked.text || '') && removeUnlinked.text === '' && removeSent.length === 0,
    { soloTitle, ro2Title, soloNote: soloBig && soloBig.empty, ro2Note: ro2Big && ro2Big.empty, summary: summary2, removeLinked, removeUnlinked, removeSent });

  // ── 第二頁（模組狀態另一份）：矩陣「這台」格的「在這台登入」、大卡選單的「在這台登入」；同一個帳戶再按一次只送一次
  const app2 = await openDesktop('desktop-remote-only-2', { init: Object.assign(remoteOnlyInit(), { remote: RF.desktopRemote(NOW) }), onPost: answerOpen });
  const pg2 = app2.page;
  const host2 = app2.host;
  await showDashboard(pg2);
  await sleep(150);
  const mxBtns = await pg2.evaluate(() => [...document.querySelectorAll('#dashboard .mx tbody tr')].map((tr) => [tr.dataset.account,
    [...tr.querySelectorAll('td.mx-cell')].map((td) => (td.querySelector('.mx-login') ? td.dataset.device : null)).filter(Boolean)]));
  const q0 = host2.posted.length;
  await pg2.click('#dashboard .mx tr[data-account="la_ro1"] td[data-device="d_A"] .mx-login');
  await sleep(150);
  const sentMx = addsOf(host2, q0);
  const q1 = host2.posted.length;
  const menu2 = (await openCardMenu(pg2, 'la_ro2')).map((m) => m.label);
  if (sameJson(menu2, [LOGIN_HERE])) await clickMenuItem(pg2, LOGIN_HERE);
  else await closeMenus(pg2);
  await sleep(150);
  const sentMenu = addsOf(host2, q1);
  const q2 = host2.posted.length;
  await showChat(pg2);
  await pg2.click('#sidebar .macct[data-key="la_ro1"]', { button: 'right' });
  await pg2.waitForSelector('.menu', { timeout: 2000 }).catch(() => {});
  await clickMenuItem(pg2, LOGIN_HERE).catch(() => {});
  await sleep(150);
  const sentAgain = addsOf(host2, q2);
  const leaks2 = laLeaks(host2.posted);
  await closePage(app2);

  const p = page1;
  const rowIds = minis.map((m) => m.id);
  check('remoteOnlyCards',
    sameJson(rowIds, ['a1', 'la_ro1', 'a2', 'a3', 'la_ro2'])
    && [p.mini.la_ro1, p.mini.la_ro2].every((m) => m.remoteOnly && !m.draggable && !m.newBtn && m.sub === '這台未登入')
    && [p.mini.a1, p.mini.a2, p.mini.a3].every((m) => !m.remoteOnly && m.draggable && m.newBtn)
    && sameJson(p.byClick, [LOGIN_HERE]) && sameJson(p.byRight, [LOGIN_HERE]) && sameJson(p.byEnter, [LOGIN_HERE]) && !p.filteredAny
    && p.sentMini.length === 1 && sameJson(p.sentMini[0], RO_SENT.la_ro1)
    && sameJson(p.switchMenu, ['工作用 Max', '個人 Pro（沒連結）', 'Codex Pro'])
    && !p.chips.hidden && sameJson(p.chips.labels, ['所有帳戶', '工作用 Max', 'Codex Pro', '個人 Pro（沒連結）'])
    && !!p.summary && p.summary.n === '3' && p.summary.remote === '（另有 2 個在帳戶目錄、這台未登入）'
    && p.big1 && p.big1.remoteOnly && p.big1.rings === 2 && p.big1.ringsVisible && sameJson(p.big1.src, ['其他電腦回報']) && !p.big1.empty && sameJson(p.big1.buttons, [LOGIN_HERE])
    && p.big2 && p.big2.remoteOnly && p.big2.rings === 0 && !p.big2.ringsVisible && /這台未登入/.test(p.big2.empty || '') && sameJson(p.big2.buttons, [LOGIN_HERE])
    && p.bigA1 && !p.bigA1.remoteOnly && !p.bigA1.buttons.includes(LOGIN_HERE) && p.bigA1.buttons.includes('新對話')
    && sameJson(p.bigMenu, [LOGIN_HERE]) && p.sentBig.length === 1 && sameJson(p.sentBig[0], RO_SENT.la_ro2)
    && p.phClaude === 'Claude 帳戶 3' && p.phCodex === 'Codex 帳戶 2'
    && !!p.reorder && sameJson(p.reorder.ids, ['a2', 'a1', 'a3']) && sameJson(p.orderAfter, ['a2', 'la_ro1', 'a1', 'a3', 'la_ro2']) && p.extraReorders.length === 0
    && sameJson(mxBtns.filter(([, d]) => d.length), [['la_ro1', ['d_A']], ['la_ro2', ['d_A']]])
    && sentMx.length === 1 && sameJson(sentMx[0], RO_SENT.la_ro1) && sameJson(menu2, [LOGIN_HERE]) && sentMenu.length === 1 && sameJson(sentMenu[0], RO_SENT.la_ro2)
    && sentAgain.length === 0 && p.leaks1.length === 0 && leaks2.length === 0,
    {
      order: rowIds, mini: [p.mini.la_ro1, p.mini.la_ro2, p.mini.a1], menus: { click: p.byClick, right: p.byRight, enter: p.byEnter, big: p.bigMenu, page2: menu2 },
      filteredAny: p.filteredAny, switchMenu: p.switchMenu, chips: p.chips, summary: p.summary, big: { la_ro1: p.big1, la_ro2: p.big2, a1: p.bigA1 },
      sent: { mini: p.sentMini, big: p.sentBig, matrix: sentMx, menu: sentMenu, again: sentAgain }, placeholders: [p.phClaude, p.phCodex],
      reorder: p.reorder, orderAfter: p.orderAfter, extraReorders: p.extraReorders, matrixButtons: mxBtns, leaks: p.leaks1.concat(leaks2),
    });
  await closePage(app);
}

// ───── 步驟 12：登入頁（wwwroot 的 auth.*）：登入後到網頁版、配對核准頁（/#pair）─────
// 用真的 auth.html／auth.js／authrules.js／auth.css（openHub 的 realAuth），擁有者端點是 FakeApi 的假回應；真伺服器的配對流程在 hub-e2e 的 pairing.mjs。

/** GET /api/v1/pair/pending 的回應（PendingPairing，camelCase）；裝置名帶 XSS，畫面要照字面顯示 */
const PENDING = (o = {}) => Object.assign({
  deviceName: '筆電 ' + RF.XSS, machineName: 'LAPTOP-7', windowsUser: 'lin', keyStorage: 'tpm', appVersion: '0.3.0', ip: '203.0.113.7',
  createdAt: NOW - 2 * 60e3, expiresAt: NOW + 8 * 60e3, fingerprint: 'ABCD-EFGH-JKMN-PQRS',
}, o);

/** 登入頁目前的狀態（看得到的才算） */
function authState(page) {
  return page.evaluate(() => {
    const el = (id) => document.getElementById(id);
    const t = (id) => (el(id) ? el(id).textContent : null);
    const vis = (id) => !!el(id) && !el(id).hidden && el(id).getBoundingClientRect().height > 0;
    return {
      path: location.pathname, hash: location.hash, view: window.__hubProbe ? window.__hubProbe.view : null, ready: !!(window.__hubProbe && window.__hubProbe.ready),
      loginHint: vis('login-pair-hint'), code: el('pair-code') ? el('pair-code').value : null,
      detail: vis('pair-detail'), fp: t('pair-fp'), device: t('pair-device'), key: t('pair-key'), ip: t('pair-ip'), created: t('pair-created'),
      result: vis('pair-result') ? t('pair-result') : null, resultTone: el('pair-result') ? el('pair-result').dataset.tone || null : null,
      done: vis('pair-done'), dialog: !!document.querySelector('#dlg-stepup[open]'), xss: window.__xss === 1,
      approveDisabled: el('btn-pair-approve') ? el('btn-pair-approve').getAttribute('aria-disabled') : null,
    };
  });
}

/** 擁有者端點刻意回的錯誤（401／403／404）在主控台的「Failed to load resource」 */
function expectApiErrors(app, re, why) {
  app.expect((m) => /Failed to load resource/.test(m.text) && re.test(m.url), why);
}

async function openAuth(name, url, api, vp, hasTouch) {
  const app = await openHub(name, { realAuth: true, url, waitReady: false, api, vp, hasTouch });
  await app.page.waitForFunction(() => window.__hubProbe && window.__hubProbe.ready, null, { timeout: 8000 }).catch(() => {});
  return app;
}

/** 已登入的假端點：me、sessions 200；o.pending、o.approve、o.deny 是各端點的回應佇列 */
const loggedInApi = (o = {}) => (api) => {
  api.set('/api/v1/sessions', { status: 200, body: { sessions: [] } });
  api.set('/api/v1/auth/stepup', { status: 200, body: { ok: true } });
  if (o.pending) api.queue('/api/v1/pair/pending', o.pending);
  if (o.approve) api.queue('/api/v1/pair/approve', o.approve);
  if (o.deny) api.queue('/api/v1/pair/deny', o.deny);
};

/** 還沒登入的假端點：登入之前 me、sessions 401，POST auth/login 之後 200 */
const loggedOutApi = (o = {}) => (api) => {
  let loggedIn = false;
  api.set('/api/v1/auth/me', () => (loggedIn ? { status: 200, body: { name: 'admin', serverDomain: 'hub.probe.local', session: {} } } : { status: 401, body: { error: 'unauthenticated' } }));
  api.set('/api/v1/sessions', () => (loggedIn ? { status: 200, body: { sessions: [] } } : { status: 401, body: { error: 'unauthenticated' } }));
  api.set('/api/v1/auth/login', () => { loggedIn = true; return { status: 200, body: { ok: true } }; });
  if (o.pending) api.queue('/api/v1/pair/pending', o.pending);
};

const postsOf = (api, from) => api.requests.slice(from).filter((r) => r.method === 'POST')
  .map((r) => ({ path: r.path, body: r.body, csrf: r.headers['x-myriagent-csrf'] || null, type: r.headers['content-type'] || null }));

/** 按鈕中心的座標（以真的滑鼠或觸控點下去，不經 Playwright 的可操作檢查：模擬雙擊的第二下） */
async function centerOf(page, sel) {
  const b = await page.locator(sel).boundingBox();
  return b ? { x: b.x + b.width / 2, y: b.y + b.height / 2 } : null;
}

async function runAuthPair() {
  const app = await openAuth('auth-pair', ORIGIN + '/#pair=mtlz%20xfch', loggedInApi({
    pending: [{ status: 200, body: PENDING() }, { status: 404, body: { error: 'not_found' } }, { status: 200, body: PENDING({ deviceName: '桌機', keyStorage: 'software' }) }],
    approve: [OK],
    deny: [OK],
  }));
  expectApiErrors(app, /\/api\/v1\/pair\/pending/, '刻意的 404 not_found');
  const { page, api } = app;

  // 網址帶碼：直接查，欄位照字面顯示、大字指紋
  const shown = await until(async () => { const s = await authState(page); return s.detail ? s : null; }, 4000);
  const lookups = api.requests.filter((r) => r.path === '/api/v1/pair/pending').map((r) => r.query);
  check('authPairLookup', !!shown && shown.view === 'pair' && shown.code === 'MTLZ-XFCH' && shown.fp === 'ABCD-EFGH-JKMN-PQRS' && shown.device === '筆電 ' + RF.XSS
    && shown.key === 'TPM（硬體）' && shown.ip === '203.0.113.7' && /\d/.test(shown.created || '') && !shown.xss && sameJson(lookups, ['?code=MTLZXFCH']),
    { shown, lookups });
  await shot(page, 'auth-pair-1440');

  // 核准：細節出現 1 秒後移動滑鼠才解除停用；按下去一律先跳密碼框（近期驗證還有效也一樣：假端點的 stepup 一律 200）→ stepup → 核准 →
  // 「已核准」與「開啟網頁版」；輸入密碼之前不送任何請求
  await sleep(1100);
  await page.hover('#btn-pair-approve');
  const armed = (await authState(page)).approveDisabled;
  const r0 = api.requests.length;
  await page.click('#btn-pair-approve');
  await page.waitForSelector('#dlg-stepup[open]', { timeout: 3000 }).catch(() => {});
  const dialogShown = (await authState(page)).dialog;
  const beforePassword = postsOf(api, r0).length;
  await page.fill('#stepup-password', 'pw-probe');
  await page.click('#btn-stepup');
  const done = await until(async () => { const s = await authState(page); return s.done ? s : null; }, 4000);
  const approveReqs = postsOf(api, r0);
  const openApp = await page.getAttribute('#pair-done a', 'href');
  check('authPairApproveStepUp', armed === 'false' && dialogShown && beforePassword === 0 && !!done && done.result === '已核准。那台電腦會自動完成配對' && done.resultTone === 'ok' && !done.detail
    && openApp === '/app/' && sameJson(approveReqs.map((r) => r.path), ['/api/v1/auth/stepup', '/api/v1/pair/approve'])
    && approveReqs[1].body === JSON.stringify({ userCode: 'MTLZXFCH' }) && approveReqs[0].body === JSON.stringify({ password: 'pw-probe' })
    && approveReqs.every((r) => r.csrf === '1' && /application\/json/.test(r.type || '')),
    { armed, dialogShown, beforePassword, done, approveReqs, openApp });

  // 手動輸入：找不到（404）→ 錯誤說明；另一個碼 → 拒絕（不用近期驗證）；清完是空的不送
  await page.fill('#pair-code', 'abcd efgh');
  await page.click('#btn-pair-lookup');
  const notFound = await until(async () => { const s = await authState(page); return s.resultTone === 'error' ? s : null; }, 3000);
  await page.fill('#pair-code', ' zzzz-yyyy ');
  await page.click('#btn-pair-lookup');
  const second = await until(async () => { const s = await authState(page); return s.detail ? s : null; }, 3000);
  const d0 = api.requests.length;
  await page.click('#btn-pair-deny');
  const denied = await until(async () => { const s = await authState(page); return s.result && s.result.startsWith('已拒絕') ? s : null; }, 3000);
  const denyReqs = postsOf(api, d0);
  const q0 = api.requests.length;
  await page.fill('#pair-code', '--');
  await page.click('#btn-pair-lookup');
  await sleep(150);
  const empty = await authState(page);
  const emptyReqs = api.requests.length - q0;
  const queries = api.requests.filter((r) => r.path === '/api/v1/pair/pending').map((r) => r.query);
  check('authPairManualAndDeny', !!notFound && /找不到這個配對碼/.test(notFound.result || '') && !notFound.detail
    && !!second && second.code === 'ZZZZ-YYYY' && second.device === '桌機' && second.key === '軟體（Windows 使用者設定檔）'
    && !!denied && !denied.detail && !denied.done && denyReqs.length === 1 && denyReqs[0].path === '/api/v1/pair/deny' && denyReqs[0].body === JSON.stringify({ userCode: 'ZZZZYYYY' }) && denyReqs[0].csrf === '1'
    && /請輸入配對碼/.test(empty.result || '') && empty.resultTone === 'error' && emptyReqs === 0
    && sameJson(queries, ['?code=MTLZXFCH', '?code=ABCDEFGH', '?code=ZZZZYYYY']),
    { notFound: notFound && notFound.result, second, denied: denied && denied.result, denyReqs, empty: empty.result, emptyReqs, queries });

  // 網址片段改變：空片段 → 帳戶頁（有「開啟網頁版」）；按「核准電腦的配對」→ 空的核准頁
  await page.evaluate(() => { location.hash = ''; });
  const account = await until(async () => { const s = await authState(page); return s.view === 'account' ? s : null; }, 3000);
  const appLink = await page.getAttribute('#link-app', 'href');
  const appLinkText = await page.textContent('#link-app');
  await page.click('#link-pair');
  const pairEmpty = await until(async () => { const s = await authState(page); return s.view === 'pair' ? s : null; }, 3000);
  check('authPairHashChange', !!account && appLink === '/app/' && appLinkText === '開啟網頁版' && !!pairEmpty && pairEmpty.hash === '#pair' && pairEmpty.code === '' && !pairEmpty.detail && !pairEmpty.result,
    { account: account && account.view, appLink, pairEmpty });
  await closePage(app);

  // 點擊劫持（DoubleClickjacking）的防線：細節一出現就點、視窗剛拿回焦點就點，都不跳密碼框、不送任何請求；1 秒後真的移動滑鼠才按得下去，
  // 按下去也只是跳密碼框，取消（Esc）不送核准
  const g = await openAuth('auth-pair-guard', ORIGIN + '/#pair=MTLZ-XFCH', loggedInApi({ pending: [{ status: 200, body: PENDING() }], approve: [OK] }));
  const shownG = await until(async () => { const s = await authState(g.page); return s.detail ? s : null; }, 4000, 10);
  const shownAt = Date.now();
  const g0 = g.api.requests.length;
  const at = shownG ? await centerOf(g.page, '#btn-pair-approve') : null;
  if (at) await g.page.mouse.click(at.x, at.y);
  const quickMs = Date.now() - shownAt;
  await sleep(250);
  const quick = await authState(g.page);
  const quickPosts = postsOf(g.api, g0).length;
  await sleep(1100);
  await g.page.mouse.move(5, 5);
  await g.page.evaluate(() => {
    window.__probeFocused = false;
    window.dispatchEvent(new Event('blur'));
    window.__probeFocused = true;
    window.dispatchEvent(new Event('focus'));
  });
  if (at) await g.page.mouse.click(at.x, at.y);
  await sleep(250);
  const refocused = await authState(g.page);
  const refocusPosts = postsOf(g.api, g0).length;
  await sleep(1100);
  await g.page.mouse.move(5, 5);
  if (at) await g.page.mouse.move(at.x, at.y, { steps: 3 });
  const armedG = (await authState(g.page)).approveDisabled;
  if (at) await g.page.mouse.click(at.x, at.y);
  await g.page.waitForSelector('#dlg-stepup[open]', { timeout: 3000 }).catch(() => {});
  const opened = (await authState(g.page)).dialog;
  await g.page.keyboard.press('Escape');
  await sleep(300);
  const cancelled = await authState(g.page);
  const cancelPosts = postsOf(g.api, g0);
  check('authPairApproveGuard', !!shownG && !!at && quickMs < 1000 && !quick.dialog && quick.approveDisabled === 'true' && quickPosts === 0
    && !refocused.dialog && refocused.approveDisabled === 'true' && refocusPosts === 0 && armedG === 'false' && opened
    && !cancelled.dialog && cancelled.detail && !cancelled.result && cancelPosts.length === 0,
    { quickClickMs: quickMs, quick: { dialog: quick.dialog, disabled: quick.approveDisabled, posts: quickPosts },
      refocused: { dialog: refocused.dialog, disabled: refocused.approveDisabled, posts: refocusPosts }, armed: armedG, opened,
      cancelled: { dialog: cancelled.dialog, detail: cancelled.detail, result: cancelled.result }, posts: cancelPosts });
  await closePage(g);

  // 手機（390px、觸控）：核准頁不水平捲動；觸控的點按沒有 pointermove，1 秒內點不理、1 秒後同一下點按就跳密碼框
  const phone = await openAuth('auth-pair-390', ORIGIN + '/#pair=MTLZ-XFCH', loggedInApi({ pending: [{ status: 200, body: PENDING() }] }), { w: 390, h: 844 }, true);
  await until(async () => { const s = await authState(phone.page); return s.detail ? s : null; }, 4000);
  const tapAt = await centerOf(phone.page, '#btn-pair-approve');
  if (tapAt) await phone.page.touchscreen.tap(tapAt.x, tapAt.y);
  await sleep(250);
  const tapQuick = (await authState(phone.page)).dialog;
  await sleep(1100);
  if (tapAt) await phone.page.touchscreen.tap(tapAt.x, tapAt.y);
  await phone.page.waitForSelector('#dlg-stepup[open]', { timeout: 3000 }).catch(() => {});
  const tapLater = (await authState(phone.page)).dialog;
  await phone.page.keyboard.press('Escape');
  await sleep(300);
  check('authPairTouchApprove', !!tapAt && !tapQuick && tapLater && !(await authState(phone.page)).dialog
    && postsOf(phone.api, 0).length === 0, { tapAt, quickTapOpened: tapQuick, laterTapOpened: tapLater, posts: postsOf(phone.api, 0) });
  const overflow = await phone.page.evaluate(() => {
    const w = document.documentElement.clientWidth;
    const wide = [...document.querySelectorAll('body *')].filter((e) => { const r = e.getBoundingClientRect(); return r.width > 0 && r.right > w + 1; })
      .map((e) => (e.id ? '#' + e.id : e.tagName.toLowerCase() + '.' + e.className));
    return { scrollWidth: document.documentElement.scrollWidth, clientWidth: w, wide };
  });
  await shot(phone.page, 'auth-pair-390');
  check('authPair390NoHScroll', overflow.scrollWidth <= overflow.clientWidth && overflow.wide.length === 0, overflow);
  await closePage(phone);
}

async function runAuthCollaborator() {
  // 多人協作：協作人登入（就算網址是配對片段）直接到 /app/、不查工作階段清單；已登入的協作人開 / 也導到 /app/
  const collabApi = (loggedInAtStart) => (api) => {
    let loggedIn = loggedInAtStart;
    api.set('/api/v1/auth/me', () => (loggedIn ? { status: 200, body: { name: 'ming', role: 'collaborator', displayName: '小明', serverDomain: 'hub.probe.local', session: {} } }
      : { status: 401, body: { error: 'unauthenticated' } }));
    api.set('/api/v1/sessions', { status: 403, body: { error: 'forbidden' } });
    api.set('/api/v1/auth/login', () => { loggedIn = true; return { status: 200, body: { ok: true, role: 'collaborator' } }; });
  };
  const app = await openAuth('auth-collab-login', ORIGIN + '/#pair=MTLZ-XFCH', collabApi(false));
  expectApiErrors(app, /\/api\/v1\/(auth\/me|sessions)/, '登入之前的 401');
  await app.page.fill('#login-user', 'ming');
  await app.page.fill('#login-password', 'pw-collab');
  const from = app.api.requests.length;
  await app.page.click('#btn-login');
  await app.page.waitForURL(ORIGIN + '/app/', { timeout: 5000 }).catch(() => {});
  const url = app.page.url();
  // 協作人登入之後不重查帳戶頁（不查工作階段清單），直接到網頁版
  const sessions = app.api.requests.slice(from).filter((r) => r.path === '/api/v1/sessions').length;
  check('authCollaboratorLoginToApp', url === ORIGIN + '/app/' && sessions === 0, { url, sessions });
  await closePage(app);

  const back = await openHub('auth-collab-back', { realAuth: true, url: ORIGIN + '/', waitReady: false, api: collabApi(true) });
  expectApiErrors(back, /\/api\/v1\/sessions/, '協作人的工作階段清單 403（和 me 一起查，看到是協作人就導走）');
  await back.page.waitForURL(ORIGIN + '/app/', { timeout: 5000 }).catch(() => {});
  const backUrl = back.page.url();
  check('authCollaboratorRedirectsToApp', backUrl === ORIGIN + '/app/', { url: backUrl });
  await closePage(back);
}

async function runAuthLogin() {
  // 一般登入（網址沒有配對片段）：登入成功之後到 /app/
  const app = await openAuth('auth-login', ORIGIN + '/', loggedOutApi());
  expectApiErrors(app, /\/api\/v1\/(auth\/me|sessions)/, '登入之前的 401');
  const before = await authState(app.page);
  await app.page.fill('#login-user', 'admin');
  await app.page.fill('#login-password', 'pw-probe');
  await app.page.click('#btn-login');
  await app.page.waitForURL(ORIGIN + '/app/', { timeout: 5000 }).catch(() => {});
  const url = app.page.url();
  const logins = app.api.requests.filter((r) => r.path === '/api/v1/auth/login').map((r) => r.body);
  check('authLoginToApp', before.view === 'login' && !before.loginHint && url === ORIGIN + '/app/' && sameJson(logins, [JSON.stringify({ userName: 'admin', password: 'pw-probe' })]),
    { before: { view: before.view, loginHint: before.loginHint }, url, logins });
  await closePage(app);

  // 從配對網址來：登入頁多一行提示；登入後留在核准頁（不去 /app/）並查詢配對碼
  const pair = await openAuth('auth-login-pair', ORIGIN + '/#pair=MTLZ-XFCH', loggedOutApi({ pending: [{ status: 200, body: PENDING() }] }));
  expectApiErrors(pair, /\/api\/v1\/(auth\/me|sessions)/, '登入之前的 401');
  const before2 = await authState(pair.page);
  await shot(pair.page, 'auth-login-pair-1440');
  await pair.page.fill('#login-user', 'admin');
  await pair.page.fill('#login-password', 'pw-probe');
  await pair.page.click('#btn-login');
  const after2 = await until(async () => { const s = await authState(pair.page); return s.detail ? s : null; }, 4000);
  await sleep(300);
  const navs = pair.navigations.map((n) => n.url);
  check('authLoginStaysOnPair', before2.view === 'login' && before2.loginHint && !!after2 && after2.view === 'pair' && after2.path === '/' && after2.hash === '#pair=MTLZ-XFCH'
    && after2.fp === 'ABCD-EFGH-JKMN-PQRS' && !navs.some((u) => u.includes('/app/')),
    { before: { view: before2.view, loginHint: before2.loginHint }, after: after2, navigations: navs });
  await closePage(pair);
}

// 每一組：會產生的檢查（--only 用）、執行
const SCENARIOS = [
  { checks: ['harnessInjectionOrder', 'harnessRulesLoaded', 'harnessNoneShowsNoHost', 'harnessFakeHubHandshake', 'harnessCspNoViolations'], run: runHarness },
  { checks: ['desktopUnchangedWithoutRemote', 'desktopSyncMessagesNoError'], run: runDesktop },
  { checks: ['hubStartsOnFirstReady'], run: runHubStartOnReady },
  { checks: ['hubAnonymousRedirects'], run: runHubAnonymous },
  { checks: ['hubSkipNegotiation', 'hubOnlineThenInit', 'hubNoChromeBeforeInit', 'hubClientStateShape', 'hubResyncInit', 'hubCtrlNNewDraft', 'hubReconnectReinit', 'hubSessionExpiredRedirect'], run: runHubBasics },
  { checks: ['hubEarlySubscriber'], run: runHubEarly },
  { checks: ['hubSubscriberThrows'], run: runHubThrows },
  { checks: ['hubServerDownBackoff'], run: runHubServerDown },
  { checks: ['hubOfflineEvents'], run: runHubOffline },
  { checks: ['hubVisibilityRetry'], run: runHubVisibility },
  { checks: ['hubInitWatchdog'], run: runHubWatchdog },
  { checks: ['hubPopoutConversation', 'hubPopoutLayoutNotStored'], run: runHubPopout },
  { checks: ['hubRateLimitedRetry', 'hubRateLimitedGiveUp', 'hubCmdUlid'], run: runHubCommands },
  { checks: ['hubOfflineSilentAuto', 'hubOfflineDropUserCmd'], run: runHubOfflineCmd },
  { checks: ['hubAccountUpdateMerge'], run: runHubAccountUpdateMerge },
  { checks: ['hubTokensMerged', 'hubTokensPush'], run: runHubTokens },
  { checks: ['hubReadyRateLimitedRetry'], run: runHubReadyRateLimited },
  { checks: ['hubSettingRevert'], run: runHubSettingRevert },
  { checks: ['hubStartTimeout'], run: runHubStartTimeout },
  { checks: ['hubFrameLimit'], run: runHubFrameLimit },
  { checks: ['hubShapeLimit'], run: runHubShapeLimit },
  { checks: ['hubTooManyConnections'], run: runHubTooMany },
  { checks: ['hubClientStateOnVisibility', 'hubClientStateRateLimited'], run: runHubClientState },
  { checks: ['hubLayoutLocal'], run: runHubLayout },
  { checks: ['hubLocalStorageThrows'], run: runHubStorageThrows },
  { checks: ['hubOpenLinkLocal', 'hubCopyTextFallback', 'hubPopoutLocal'], run: runHubLocalActions },
  { checks: ['matrixColumnsSkipRevoked', 'matrixCellStates', 'matrixPollerMark', 'matrixOfflineDim', 'matrixCrosshair', 'devicesCliTable', 'hubHidesDesktopFeatures', 'hubReorderSends', 'hubNoDesktopOnlyFrames', 'xssLiteral'], run: runHubDashboard },
  { checks: ['phone390NoHScroll'], run: runHubPhone },
  { checks: ['devicesPageFields', 'signedNameShown', 'revokedNoRename', 'deviceRenameSends', 'revokeStepUpFlow', 'revokeWrongPassword', 'revokeStepUpRateLimited',
    'revokeCancelNoRequest', 'revoke409Message', 'revoke503Message', 'hubAccountMenuFive', 'deleteAccountFlow', 'ownerPost401Redirect'], run: runHubDevices },
  { checks: ['desktopMatrixSelf', 'desktopDevicesNoRevoke', 'desktopVerifiedWarning', 'desktopDeviceCodeMenu', 'desktopDeviceCodeShown'], run: runDesktopDashboard },
  { checks: ['syncDotStates', 'sidebarDeviceDots', 'remoteOnlyCards', 'remoteOnlyNoOtherDevice'], run: runDesktopRemoteOnly },
  { checks: ['authPairLookup', 'authPairApproveStepUp', 'authPairManualAndDeny', 'authPairHashChange', 'authPairApproveGuard', 'authPairTouchApprove', 'authPair390NoHScroll'], run: runAuthPair },
  { checks: ['authLoginToApp', 'authLoginStaysOnPair'], run: runAuthLogin },
  { checks: ['authCollaboratorLoginToApp', 'authCollaboratorRedirectsToApp'], run: runAuthCollaborator },
  {
    checks: REMOTE_ACCOUNT_CHECKS,
    run: () => runRemoteAccounts({ openHub, openDesktop, showDashboard, waitLive, check, closePage, until, sleep, shot, openCardMenu, clickMenuItem, closeMenus, readMatrix, RF, NOW }),
  },
];

let exitCode = 1;
try {
  browser = await chromium.launch({ channel: 'chrome', headless: !HEADED });
  report.chrome = browser.version();
  for (const s of SCENARIOS) {
    if (!s.checks.some(onlyMatch)) continue;
    await s.run();
  }
  const all = [];
  for (const app of pages) {
    for (const m of app.console) {
      const expected = app.expected.find((x) => (typeof x.pattern === 'function' ? x.pattern(m) : x.pattern.test(m.text)));
      if (expected) report.expectedConsole.push({ page: app.name, ...m, why: expected.why });
      else if (m.type === 'error' || m.type === 'pageerror') all.push({ page: app.name, ...m });
      else report.notes.push({ page: app.name, ...m });
    }
  }
  report.consoleErrors = all;
  // 伺服器模式的頁面：CSP 0 違規（每個情境都在伺服器版的 CSP 下）、主控台只有各情境登記的預期錯誤
  const hubPages = pages.filter((a) => a.kind === 'hub');
  if (!ONLY || onlyMatch('hubCspNoViolations') || ONLY.some((p) => p.startsWith('hub'))) {
    const violations = hubPages.flatMap((a) => a.csp.map((v) => Object.assign({ page: a.name }, v)));
    check('hubCspNoViolations', hubPages.length > 0 && violations.length === 0, { pages: hubPages.map((a) => a.name), violations });
    const hubErrors = all.filter((m) => hubPages.some((a) => a.name === m.page));
    check('hubNoConsoleErrors', hubErrors.length === 0, { unexpected: hubErrors, expected: report.expectedConsole.filter((m) => hubPages.some((a) => a.name === m.page)) });
  }
  if (!ONLY) check('noUnexpectedConsoleErrors', all.length === 0, all);
  const bools = Object.values(report.checks);
  report.allPassed = bools.length > 0 && bools.every(Boolean);
  exitCode = report.allPassed ? 0 : 1;
} catch (e) {
  report.fatal = String((e && e.stack) || e);
  exitCode = 2;
} finally {
  // 先寫結果，再關瀏覽器（關閉加時限）
  fs.writeFileSync(path.join(OUT, 'remote-report.json'), JSON.stringify(report, null, 2), 'utf8');
  const failed = Object.entries(report.checks).filter(([, v]) => !v).map(([k]) => k);
  console.log(JSON.stringify({ outDir: OUT, allPassed: report.allPassed, checks: Object.keys(report.checks).length, failed, consoleErrors: report.consoleErrors.length, fatal: report.fatal || null }, null, 2));
  if (browser) await Promise.race([browser.close().catch(() => {}), sleep(15000)]);
  process.exit(exitCode);
}
