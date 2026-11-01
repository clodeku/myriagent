// 伺服器模式與桌面模式的探針頁面（WP4.6 計畫 docs/plans/2026-10-19-wp4.6-web-live.md 步 3）：把 remote-probe.mjs 的 serveHub、probeInit、
// openHub、openDesktop 重寫成可以匯入的模組，給 live-probe.mjs（與 WP4.9 的全流程探針）重用；remote-probe.mjs 本身不改。
// - 伺服器的 /app/：https://hub.probe.local 以 context.route 供應 src/Myriagent/Web/ 的檔案，照 AssetServerRules.InjectScripts 插入真的
//   SignalR 程式庫與真的 transport-hub.js，帶伺服器版的 CSP（HUB_CSP 要和 SecurityHeaderRules.ContentSecurityPolicy 逐字相同，
//   tests/MyriagentHub.Tests/Web/LiveProbeCspTests 把關）；hub 是 fake-signalr.mjs 在頁內假造的，擁有者 HTTP 端點（/api/v1/…）是 FakeApi 的可控回應，
//   上傳端點 PUT /api/v1/uploads/{id} 預設回 404，api.uploads() 換成照 E4 回（201／200／409，可固定回 413、507 等，步 11）；
//   /m/、/t/ 回 1×1 的 PNG、/f/ 回一小段文字（媒體端點是 WP4.7 的）。
// - 桌面：https://myriagent.local 的最小 WebView2 模擬（同 remote-probe.mjs 的 MOCK），帶桌面的 CSP；/media、/thumb 與副本的 /m/、/t/
//   （ReplicaMediaUrls，K-M2）回 1×1 的 PNG。
// - 每個頁面最早執行的 probeInit（CDP 注入，不受 CSP 影響）：CSP 違規收集、可控的 navigator.onLine／document.hidden／document.hasFocus、
//   Date.now 的推進（只有 Date.now；計時器照真實時間）、window.open 記錄、假的 Notification 與 navigator.clipboard（權限可控、呼叫都記下）、
//   localStorage／sessionStorage 可以改成丟例外、XSS 計數（alert／confirm／prompt／print 被呼叫就加一）、傳輸層訊息的記錄訂閱者（window.__probeMsgs）。
// 主控台的 error、warning 與 pageerror 都記在 app.console；情境以 app.expect(pattern, why) 登記預期的錯誤。
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { FakeSignalRHub } from './fake-signalr.mjs';
import * as RF from './remote-fixtures.mjs';
import { fakeMic } from './fake-mic.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
export const REPO = path.resolve(here, '..', '..');
export const WEB = path.join(REPO, 'src', 'Myriagent', 'Web');
export const WWWROOT = path.join(REPO, 'src', 'MyriagentHub', 'wwwroot');
export const ORIGIN = RF.ORIGIN;
export const DESKTOP_ORIGIN = RF.DESKTOP_ORIGIN;
export const HUB_URL = 'wss://hub.probe.local/hubs/v1/browser';
// 伺服器的 CSP：SecurityHeaderRules.ContentSecurityPolicy("https://hub.probe.local")（src/MyriagentHub/Hosting/SecurityHeaders.cs）。
// 這一行要和伺服器的結果逐字相同（tests/MyriagentHub.Tests/Web/LiveProbeCspTests）
export const HUB_CSP = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; media-src 'self' blob:; font-src 'self' data:; connect-src 'self' wss://hub.probe.local; manifest-src 'self'; base-uri 'none'; form-action 'self'; frame-src 'self'; frame-ancestors 'none'; object-src 'none'; worker-src 'none'";
// 桌面宿主回應 index.html 時帶的 CSP（同 probe.mjs、remote-probe.mjs）
export const DESKTOP_CSP = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; media-src 'self' blob:; " +
  "font-src 'self' data:; connect-src 'self'; frame-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'; object-src 'none'";
// index.html 載入網頁主程式的那一行（AssetServerRules.Anchor）：傳輸腳本插在它前面
const ANCHOR = '<script type="module" src="app.js"></script>';
const MIME = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.svg': 'image/svg+xml', '.png': 'image/png', '.json': 'application/json; charset=utf-8', '.txt': 'text/plain; charset=utf-8' };
/** /m/、/t/ 的替身：1×1 的 PNG */
const PNG_1X1 = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==', 'base64');
/** /f/ 的替身 */
const FILE_BODY = Buffer.from('探針的檔案內容\n', 'utf8');
/** 上傳端點（E4）的路徑前綴 */
const UPLOAD_PREFIX = '/api/v1/uploads/';
/** E4 的錯誤代碼（api.uploads 固定回某個狀態時的本文） */
const UPLOAD_ERRORS = { 400: 'invalid', 409: 'conflict', 413: 'too_large', 503: 'unavailable', 507: 'pending_full' };

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/** 每 step 毫秒問一次 fn，ms 毫秒內回真值就回它，否則回 null */
export async function until(fn, ms = 4000, step = 40) {
  const t0 = Date.now();
  while (Date.now() - t0 < ms) {
    const v = await fn();
    if (v) return v;
    await sleep(step);
  }
  return null;
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

const AUTH_PAGE = `<!doctype html>
<html lang="zh-Hant-TW"><head><meta charset="utf-8"><meta name="myriagenthub-page" content="auth"><title>Myriagent Hub</title><link rel="icon" href="data:,"></head>
<body><p id="probe-auth">登入頁（探針的替身）</p></body></html>`;

/**
 * 擁有者 HTTP 端點的假回應：預設 auth/me 200、上傳端點（PUT /api/v1/uploads/{id}）404；set(路徑, 回應或函式)、queue(路徑, [回應…])；
 * 路徑結尾是 * 時以前綴比對；回應 'network' 表示網路錯誤。每個請求記在 requests（含方法、標頭與本文的位元組數）
 */
export class FakeApi {
  constructor() {
    this.requests = [];
    this.fixed = new Map();
    this.queues = new Map();
    this.fixed.set('/api/v1/auth/me', { status: 200, body: { name: 'admin', serverDomain: 'hub.probe.local', session: {} } });
    this.fixed.set('/api/v1/uploads/*', { status: 404, body: { error: 'not_found' } });
  }

  set(p, r) { this.fixed.set(p, r); }

  queue(p, list) { this.queues.set(p, list.slice()); }

  /**
   * 上傳端點照 E4 回（步 11）：本文的 SHA-256（小寫十六進位）；同一個 id 第一次 201、之後同內容 200、不同內容 409，回 {uploadId, sha256, size}。
   * o.status：一律回這個狀態（413、507、409…，本文 {error}）；o.statusFor(id, n)：第 n 個（0 起算）上傳回什麼狀態（null＝照常）；
   * o.delayMs：回應前等多久（看上傳中的晶片）；o.hold：回應前先等這個 Promise。回 { uploads: [{id, status, sha256, size}] } 的記錄
   */
  uploads(o = {}) {
    const seen = new Map();
    const log = { uploads: [] };
    let n = 0;
    this.set(UPLOAD_PREFIX + '*', async (rec) => {
      const i = n++;
      const id = decodeURIComponent(rec.path.slice(UPLOAD_PREFIX.length));
      if (o.delayMs) await sleep(o.delayMs);
      if (o.hold) await o.hold;
      const forced = typeof o.statusFor === 'function' ? o.statusFor(id, i) : o.status;
      const body = rec.buffer || Buffer.alloc(0);
      const sha256 = crypto.createHash('sha256').update(body).digest('hex');
      let status;
      let reply;
      if (forced) {
        status = forced;
        reply = { error: UPLOAD_ERRORS[forced] || 'error' };
      } else {
        const prev = seen.get(id);
        status = prev === undefined ? 201 : prev === sha256 ? 200 : 409;
        if (status !== 409) seen.set(id, sha256);
        reply = status === 409 ? { error: 'conflict' } : { uploadId: id, sha256, size: body.length };
      }
      log.uploads.push({ id, status, sha256, size: body.length, csrf: rec.headers['x-myriagent-csrf'] || null, method: rec.method });
      return { status, body: reply };
    });
    return log;
  }

  count(p, method) { return this.requests.filter((r) => (p.endsWith('*') ? r.path.startsWith(p.slice(0, -1)) : r.path === p) && (!method || r.method === method)).length; }

  async handle(route, url) {
    const req = route.request();
    let bodyBytes = 0;
    try {
      const buf = req.postDataBuffer();
      bodyBytes = buf ? buf.length : 0;
    } catch {
      bodyBytes = -1;
    }
    const rec = { at: Date.now(), method: req.method(), path: url.pathname, query: url.search, headers: req.headers(), bodyBytes };
    if (bodyBytes > 0 && bodyBytes <= 64 * 1024) rec.body = req.postData();
    // 上傳的本文留位元組（api.uploads() 算 SHA-256）
    if (url.pathname.startsWith(UPLOAD_PREFIX)) {
      try {
        rec.buffer = req.postDataBuffer() || Buffer.alloc(0);
      } catch {
        rec.buffer = null;
      }
    }
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

function mediaResponse(route) {
  return route.fulfill({ status: 200, headers: { 'Content-Type': 'image/png', 'Cache-Control': 'no-store' }, body: PNG_1X1 });
}

/** 伺服器的路由：/app/、/app/<名稱>（名稱含斜線一律 404）、/lib/*、/transport-hub.js、/、/api/v1/*、/m/、/t/、/f/ */
async function serveHub(route, o, api, seen) {
  const url = new URL(route.request().url());
  const p = url.pathname;
  seen.push(p);
  if (p.startsWith('/api/')) return api.handle(route, url);
  if (p.startsWith('/hubs/')) return route.fulfill({ status: 404, headers: { 'Cache-Control': 'no-store' }, body: '' }); // negotiate：skipNegotiation 時不該有
  if (p.startsWith('/m/') || p.startsWith('/t/')) {
    // o.media(路徑)：回 route.fulfill 的選項就照它回（例如政策 Off 的影片 403），null 照常回 PNG
    const r = typeof o.media === 'function' ? o.media(p) : null;
    return r ? route.fulfill(r) : mediaResponse(route);
  }
  if (p.startsWith('/f/')) {
    // o.media(路徑) 也管 /f/（例如文件預覽的 PDF）；null 照常回文字
    const r = typeof o.media === 'function' ? o.media(p) : null;
    return r ? route.fulfill(r) : route.fulfill({ status: 200, headers: { 'Content-Type': MIME['.txt'], 'Cache-Control': 'no-store' }, body: FILE_BODY });
  }
  if (p === '/') return route.fulfill({ status: 200, headers: { 'Content-Type': MIME['.html'], 'Cache-Control': 'no-store', 'Content-Security-Policy': HUB_CSP }, body: AUTH_PAGE });
  if (p === '/transport-hub.js') return fileResponse(route, path.join(WWWROOT, 'transport-hub.js'));
  if (p.startsWith('/lib/')) {
    const name = p.slice('/lib/'.length);
    if (!name || /[\\/:]/.test(name)) return route.fulfill({ status: 404, headers: { 'Cache-Control': 'no-store' }, body: '' });
    return fileResponse(route, path.join(WWWROOT, 'lib', name), { 'Cache-Control': 'public, max-age=31536000, immutable' });
  }
  if (p.startsWith('/app/')) {
    let name = decodeURIComponent(p.slice('/app/'.length));
    if (name === '') name = 'index.html';
    if (name.startsWith('.') || /[\\/:]/.test(name)) return route.fulfill({ status: 404, headers: { 'Cache-Control': 'no-store' }, body: '' });
    if (name === 'index.html') {
      const html = injectScripts(fs.readFileSync(path.join(WEB, 'index.html'), 'utf8'), injectedScripts());
      return route.fulfill({ status: 200, headers: { 'Content-Type': MIME['.html'], 'Cache-Control': 'no-store', 'Content-Security-Policy': HUB_CSP }, body: html });
    }
    return fileResponse(route, path.join(WEB, name));
  }
  return route.fulfill({ status: 404, headers: { 'Cache-Control': 'no-store' }, body: '' });
}

// ───── 每個頁面最早執行的探針腳本（CDP 注入，不受 CSP 影響）─────

/**
 * opts：clipboard（'allow'｜'deny'，預設 allow）、notification（'default'｜'granted'｜'denied'，預設 default）、
 * notificationAnswer（requestPermission 的回答，預設 granted）、localStorageThrows、sessionStorageThrows（存取就丟例外）、
 * lightMessages（訊息記錄只留 type、conversationId、commandId）。
 * 執行中可以改：window.__probeOnline、__probeHidden、__probeFocused、__probeClockOffset（Date.now 加多少毫秒）、__probeClipboard、
 * __probeNotifyPerm、__probeNotifyAnswer、__probeLocalThrows、__probeSessionThrows（改了要自己 dispatch 對應的事件，見下方的 set* 函式）
 */
export function probeInit(opts) {
  const o = opts || {};
  window.__csp = [];
  document.addEventListener('securitypolicyviolation', (e) => {
    const v = { directive: e.violatedDirective, blocked: e.blockedURI, source: e.sourceFile, line: e.lineNumber, sample: e.sample || '', href: location.href };
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

  // 時鐘：只推 Date.now（ULID、逾時與過期的判斷都用它）；計時器與 new Date() 照真實時間，SignalR 的逾時不受影響
  window.__probeClockOffset = 0;
  const realNow = Date.now.bind(Date);
  Date.now = function () { return realNow() + (Number(window.__probeClockOffset) || 0); };

  window.__opened = [];
  window.open = function (...args) { window.__opened.push(args.map((a) => (a === undefined ? null : String(a)))); return null; };

  // XSS 計數：語料的 window.__xss=1 也會讓它不是 0
  window.__xss = 0;
  window.__xssCalls = [];
  for (const name of ['alert', 'confirm', 'prompt', 'print']) {
    window[name] = function (...args) {
      window.__xss = (typeof window.__xss === 'number' ? window.__xss : 1) + 1;
      window.__xssCalls.push({ name, args: args.map((a) => String(a)).slice(0, 2) });
      return null;
    };
  }

  // 剪貼簿：allow 時寫入成功並記下；deny 時照瀏覽器在沒有使用者手勢時的樣子丟 NotAllowedError（也記下）
  window.__probeClipboard = o.clipboard === 'deny' ? 'deny' : 'allow';
  window.__clipboard = [];
  const fakeClipboard = {
    writeText(text) {
      const ok = window.__probeClipboard !== 'deny';
      window.__clipboard.push({ at: Date.now(), text: String(text), ok });
      return ok ? Promise.resolve() : Promise.reject(new DOMException('探針：剪貼簿被拒絕', 'NotAllowedError'));
    },
    readText() { return Promise.reject(new DOMException('探針：不讀剪貼簿', 'NotAllowedError')); },
    // 圖片（mediamenu.js 的複製圖片）：記下每個項目的型別與內容大小（ClipboardItem 的值可以是 Promise，等它解開再記）
    write(items) {
      const ok = window.__probeClipboard !== 'deny';
      const rec = { at: Date.now(), items: null, ok };
      window.__clipboard.push(rec);
      if (!ok) return Promise.reject(new DOMException('探針：剪貼簿被拒絕', 'NotAllowedError'));
      return Promise.all(Array.from(items || []).map(async (it) => {
        const types = Array.from(it.types || []);
        const blobs = await Promise.all(types.map((t) => it.getType(t)));
        return { types, blobTypes: blobs.map((b) => b.type), sizes: blobs.map((b) => b.size) };
      })).then((list) => { rec.items = list; });
    },
  };
  Object.defineProperty(Navigator.prototype, 'clipboard', { configurable: true, get() { return fakeClipboard; } });

  // 語音合成（瀏覽器版的語音通知，voice.js）：每一句都記下（文字、語言、聲音、語速、音調），10 毫秒後開始、__speech.durationMs（預設 30）毫秒後念完；
  // 念的期間 speaking 是 true，cancel() 讓還沒念完的回 interrupted。__speech.deny 為 true 時回 not-allowed（模擬瀏覽器擋下沒點過畫面的發聲）
  window.__speech = { spoken: [], cancels: 0, deny: false, durationMs: 30 };
  const talking = new Set();
  const fakeVoices = [
    { name: 'Probe English', lang: 'en-US', localService: true, default: true, voiceURI: 'probe-en' },
    { name: 'Probe 臺灣華語', lang: 'zh-TW', localService: true, default: false, voiceURI: 'probe-tw' },
  ];
  const fakeSynth = {
    get speaking() { return talking.size > 0; },
    pending: false,
    paused: false,
    getVoices() { return fakeVoices.slice(); },
    speak(u) {
      window.__speech.spoken.push({ at: Date.now(), text: String(u.text), lang: u.lang, voice: u.voice ? u.voice.name : null, rate: u.rate, pitch: u.pitch, denied: !!window.__speech.deny });
      const deny = !!window.__speech.deny;
      const t = { u, timer: 0 };
      talking.add(t);
      t.timer = setTimeout(() => {
        if (deny) { talking.delete(t); if (u.onerror) u.onerror({ error: 'not-allowed' }); return; }
        if (u.onstart) u.onstart({});
        t.timer = setTimeout(() => { talking.delete(t); if (u.onend) u.onend({}); }, window.__speech.durationMs);
      }, 10);
    },
    cancel() {
      window.__speech.cancels++;
      for (const t of [...talking]) {
        clearTimeout(t.timer);
        talking.delete(t);
        setTimeout(() => { if (t.u.onerror) t.u.onerror({ error: 'interrupted' }); }, 0);
      }
    },
    pause() {},
    resume() {},
    addEventListener() {},
    removeEventListener() {},
  };
  Object.defineProperty(window, 'speechSynthesis', { configurable: true, get() { return fakeSynth; } });
  window.SpeechSynthesisUtterance = function (text) {
    this.text = String(text);
    this.lang = '';
    this.voice = null;
    this.rate = 1;
    this.pitch = 1;
    this.volume = 1;
    this.onstart = null;
    this.onend = null;
    this.onerror = null;
  };

  // 系統通知：權限與 requestPermission 的回答可控；每一則都記下，click(i) 模擬使用者點了它
  window.__probeNotifyPerm = o.notification === 'granted' || o.notification === 'denied' ? o.notification : 'default';
  window.__probeNotifyAnswer = o.notificationAnswer === 'denied' || o.notificationAnswer === 'default' ? o.notificationAnswer : 'granted';
  window.__notifications = [];
  window.__notifyRequests = 0;
  const shown = [];
  function FakeNotification(title, options) {
    if (!(this instanceof FakeNotification)) throw new TypeError("Failed to construct 'Notification': Please use the 'new' operator");
    if (window.__probeNotifyPerm !== 'granted') throw new TypeError('探針：沒有通知權限');
    const opt = options || {};
    this.title = String(title);
    this.body = opt.body === undefined ? '' : String(opt.body);
    this.tag = opt.tag === undefined ? '' : String(opt.tag);
    this.onclick = null;
    this.onclose = null;
    this.closed = false;
    window.__notifications.push({ at: Date.now(), title: this.title, body: this.body, tag: this.tag, closed: false });
    shown.push(this);
  }
  FakeNotification.prototype.close = function () {
    this.closed = true;
    const i = shown.indexOf(this);
    if (i >= 0 && window.__notifications[i]) window.__notifications[i].closed = true;
  };
  FakeNotification.prototype.addEventListener = function (type, fn) { if (type === 'click') this.onclick = fn; };
  Object.defineProperty(FakeNotification, 'permission', { configurable: true, get() { return window.__probeNotifyPerm; } });
  FakeNotification.requestPermission = function (cb) {
    window.__notifyRequests++;
    window.__probeNotifyPerm = window.__probeNotifyAnswer;
    if (typeof cb === 'function') cb(window.__probeNotifyPerm);
    return Promise.resolve(window.__probeNotifyPerm);
  };
  window.__clickNotification = (i) => {
    const n = shown[i];
    if (!n || typeof n.onclick !== 'function') return false;
    n.onclick({ type: 'click', target: n, preventDefault() {} });
    return true;
  };
  Object.defineProperty(window, 'Notification', { configurable: true, writable: true, value: FakeNotification });

  // 儲存空間：可以改成存取就丟例外（私密瀏覽、被擋）
  window.__probeLocalThrows = !!o.localStorageThrows;
  window.__probeSessionThrows = !!o.sessionStorageThrows;
  const storageOf = (name, flag) => {
    let real = null;
    try {
      real = window[name];
    } catch {
      real = null;
    }
    Object.defineProperty(window, name, {
      configurable: true,
      get() {
        if (window[flag] || !real) throw new DOMException('探針：' + name + ' 被擋', 'SecurityError');
        return real;
      },
    });
  };
  storageOf('localStorage', '__probeLocalThrows');
  storageOf('sessionStorage', '__probeSessionThrows');

  // 傳輸層掛上全域時先註冊一個記錄訂閱者：頁面收到的每則訊息與順序。o.lightMessages 時只記 type、conversationId、commandId，
  // 不複製整則（量長工作時不把探針自己複製大訊息的時間算進頁面的工作）
  window.__probeMsgs = [];
  const light = o.lightMessages === true;
  const record = (m) => (light ? { type: m && m.type, conversationId: m && m.conversationId, commandId: m && m.commandId } : JSON.parse(JSON.stringify(m)));
  let transportHub;
  Object.defineProperty(window, 'myriagentHubTransport', {
    configurable: true,
    enumerable: true,
    get() { return transportHub; },
    set(v) {
      transportHub = v;
      try {
        if (v && typeof v.onMessage === 'function') v.onMessage((m) => window.__probeMsgs.push({ at: Date.now(), msg: record(m) }));
      } catch (e) {
        window.__probeRecorderError = String(e);
      }
    },
  });
}

// ───── 桌面情境：最小的 WebView2 模擬（同 remote-probe.mjs 的 MOCK）─────

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

async function serveDesktop(route, o = {}) {
  const url = new URL(route.request().url());
  // 本機的 /media、/thumb 與副本的媒體（ReplicaMediaUrls：https://myriagent.local/m/{key}、/t/{key}?s=320，WP4.7 的 ReplicaMediaRoute）；
  // o.media(路徑) 同 serveHub：回 route.fulfill 的選項就照它回，null 照常回 PNG
  if (url.pathname === '/media' || url.pathname === '/thumb' || url.pathname.startsWith('/m/') || url.pathname.startsWith('/t/')) {
    const r = typeof o.media === 'function' ? o.media(url.pathname, url) : null;
    return r ? route.fulfill(r) : mediaResponse(route);
  }
  const name = url.pathname === '/' ? 'index.html' : decodeURIComponent(url.pathname.slice(1));
  if (/[\\/]/.test(name)) return route.fulfill({ status: 404, headers: { 'Cache-Control': 'no-store' }, body: '' });
  const file = path.join(WEB, name);
  if (!fs.existsSync(file) || !fs.statSync(file).isFile()) return route.fulfill({ status: 404, headers: { 'Cache-Control': 'no-store' }, body: '' });
  const headers = { 'Content-Type': MIME[path.extname(name)] || 'application/octet-stream', 'Cache-Control': 'no-store' };
  if (name === 'index.html') headers['Content-Security-Policy'] = DESKTOP_CSP;
  return route.fulfill({ status: 200, headers, body: fs.readFileSync(file) });
}

// ───── 頁面 ─────

function watchPage(pages, app) {
  const { page } = app;
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
 * 開頁面的工廠：browser 是 chromium.launch 的結果；o.now 是夾具的時間基準（預設現在）。回傳 {pages, openHub, openDesktop, closeAll}。
 * openHub(name, o)：o.vp（預設 1440×900）、url（預設 /app/）、init(ready, conn)（預設 remote-fixtures 的 browserInit）、initOpts（probeInit 的 opts）、
 *   hub(hub)、api(api)（載入前調整）、waitReady（預設 true：等 #app.ready）、hasTouch、userAgent（模擬 iPhone 等）、media(路徑)（/m/、/t/ 的替代回應，見 serveHub）。
 * openDesktop(name, o)：o.init（init 物件或 (ready) => init；預設 remote-fixtures 的 desktopInit）、onPost(msg, host)（其他指令）、initOpts、vp、hasTouch、
 *   media(路徑)（/media、/thumb、/m/、/t/ 的替代回應，見 serveDesktop）。
 */
export function createPages(browser, o = {}) {
  const now = o.now || Date.now();
  const pages = [];

  async function openHub(name, opt = {}) {
    const vp = opt.vp || { w: 1440, h: 900 };
    const context = await browser.newContext({ viewport: { width: vp.w, height: vp.h }, deviceScaleFactor: 1, colorScheme: 'dark', locale: 'zh-TW', timezoneId: 'Asia/Taipei', hasTouch: !!opt.hasTouch, ...(opt.userAgent ? { userAgent: opt.userAgent } : {}) });
    const log = [];
    const hub = new FakeSignalRHub({ url: HUB_URL, init: opt.init || ((ready) => RF.browserInit(now, { mode: ready && ready.mode })), log });
    const api = new FakeApi();
    if (opt.hub) opt.hub(hub);
    if (opt.api) opt.api(api);
    await hub.attach(context);
    const seen = [];
    const csp = [];
    await context.route(ORIGIN + '/**', (route) => serveHub(route, opt, api, seen));
    await context.exposeBinding('__probeCsp', (source, v) => { csp.push(v); });
    await context.addInitScript(probeInit, opt.initOpts || {});
    await context.addInitScript(fakeMic); // 輸入區的語音訊息：假的麥克風與錄音器
    // 情境自己要在網頁載入前先跑的（例如以 overflow-anchor: none 模擬沒有捲動錨定的 iOS Safari）
    if (opt.initScript) await context.addInitScript(opt.initScript);
    const page = await context.newPage();
    const app = watchPage(pages, { name, kind: 'hub', context, page, hub, api, seen, csp, log, console: [], expected: [], navigations: [] });
    await page.goto(opt.url || ORIGIN + '/app/', { waitUntil: 'load' });
    if (opt.waitReady !== false) {
      await page.waitForFunction(() => {
        const el = document.getElementById('app');
        return !!el && (el.classList.contains('ready') || !!document.querySelector('.no-host'));
      }, null, { timeout: 15000 }).catch(() => {});
    }
    return app;
  }

  async function openDesktop(name, opt = {}) {
    const vp = opt.vp || { w: 1440, h: 900 };
    const context = await browser.newContext({ viewport: { width: vp.w, height: vp.h }, deviceScaleFactor: 1, colorScheme: 'dark', locale: 'zh-TW', timezoneId: 'Asia/Taipei', hasTouch: !!opt.hasTouch, ...(opt.userAgent ? { userAgent: opt.userAgent } : {}) });
    const page = await context.newPage();
    const host = { posted: [], page, send: (msg) => page.evaluate((m) => window.__host(m), JSON.parse(JSON.stringify(msg))) };
    const initOf = (ready) => {
      const v = opt.init === undefined ? RF.desktopInit(now) : opt.init;
      return typeof v === 'function' ? v(ready) : v;
    };
    const csp = [];
    await page.exposeBinding('__probeCsp', (source, v) => { csp.push(v); });
    await page.addInitScript(MOCK);
    await page.addInitScript(probeInit, opt.initOpts || {});
    await page.addInitScript(fakeMic);
    await page.exposeFunction('__hostRecv', (msg) => {
      host.posted.push(msg);
      if (msg.cmd === 'ready') {
        const init = initOf(msg);
        if (init) host.send(init).catch(() => {});
      } else if (opt.onPost) {
        opt.onPost(msg, host);
      }
    });
    await page.route(DESKTOP_ORIGIN + '/**', (route) => serveDesktop(route, opt));
    const app = watchPage(pages, { name, kind: 'desktop', context, page, host, csp, console: [], expected: [], navigations: [] });
    await page.goto(DESKTOP_ORIGIN + '/index.html');
    await page.waitForFunction(() => document.getElementById('app').classList.contains('ready'), null, { timeout: 10000 }).catch(() => {});
    return app;
  }

  async function closeAll() {
    for (const app of pages) await closePage(app);
  }

  return { pages, openHub, openDesktop, closeAll };
}

/** 關掉頁面（假 hub 的 ping 計時器一起停）；關閉加時限 */
export async function closePage(app) {
  if (app.hub) app.hub.stop();
  await Promise.race([app.context.close().catch(() => {}), sleep(5000)]);
}

/** 這個頁面的主控台錯誤（error、pageerror；warning 另計）扣掉預期的 */
export function unexpectedErrors(app) {
  return app.console.filter((m) => (m.type === 'error' || m.type === 'pageerror') && !app.expected.some((x) => (typeof x.pattern === 'function' ? x.pattern(m) : x.pattern.test(m.text))));
}

/** 這個頁面的 console.warn（transport-hub 的都以「[transport-hub]」開頭） */
export const warnsOf = (app, prefix = '[transport-hub]') => app.console.filter((m) => m.type === 'warning' && m.text.startsWith(prefix));

// ───── 讀頁面的狀態 ─────

/** 頁面收到的訊息（伺服器模式：探針的記錄訂閱者，依順序） */
export const probeMsgs = (app) => app.page.evaluate(() => window.__probeMsgs.map((x) => x.msg));
/** 某種訊息 */
export const msgsOf = async (app, type) => (await probeMsgs(app)).filter((m) => m.type === type);
/** 傳輸層的狀態（唯讀副本）；不是伺服器模式是 null */
export const tstatus = (app) => app.page.evaluate(() => (window.myriagentHubTransport ? Object.assign({}, window.myriagentHubTransport.status()) : null)).catch(() => null);
/** 頁面直接呼叫 myriagentHubTransport.post（不經 app.js）：{ok, value（post 的回傳）, error}；頁面正在導覽而叫不到時 ok 是 false */
export const tpost = (app, msg) => app.page.evaluate((m) => {
  try {
    const v = window.myriagentHubTransport.post(m);
    return { ok: true, value: v === undefined ? '(undefined)' : v };
  } catch (e) {
    return { ok: false, error: String(e) };
  }
}, msg).catch((e) => ({ ok: false, error: String((e && e.message) || e) }));
/** window.open 的記錄（頁面正在導覽而讀不到時是 null） */
export const opened = (app) => app.page.evaluate(() => window.__opened.slice()).catch(() => null);
/** 系統通知的記錄與要求授權的次數 */
export const notifications = (app) => app.page.evaluate(() => ({ shown: window.__notifications.slice(), requests: window.__notifyRequests, permission: window.__probeNotifyPerm }));
/** 剪貼簿的寫入（含被拒的） */
export const clipboardWrites = (app) => app.page.evaluate(() => window.__clipboard.slice());

/** 假的語音合成器記下的每一句（voice.js 念的） */
export const speech = (app) => app.page.evaluate(() => ({ spoken: window.__speech.spoken.slice(), cancels: window.__speech.cancels }));
/** XSS 計數 */
export const xssState = (app) => app.page.evaluate(() => ({ xss: window.__xss, calls: window.__xssCalls.slice(0, 10) }));
/** 頁面上的提示文字（app.js 的 toast） */
export const domToasts = (app) => app.page.evaluate(() => [...document.querySelectorAll('.toasts .toast-text')].map((e) => e.textContent)).catch(() => []);

/** 等到「連著而且這條連線收到過 init」、#app.ready、狀態列隱藏 */
export async function waitLive(app, ms = 8000) {
  return until(async () => {
    const st = await tstatus(app);
    if (!st || st.state !== 'online' || st.hubInit !== true) return null;
    return app.page.evaluate(() => {
      const el = document.getElementById('app');
      const bar = document.querySelector('.hub-status');
      return !!el && el.classList.contains('ready') && (!bar || bar.hidden) ? { ready: true } : null;
    }).catch(() => null);
  }, ms, 50);
}

// ───── 改頁面的狀態（改值之後照瀏覽器的順序送事件）─────

/** 分頁隱藏／回到前景（visibilitychange） */
export const setHidden = (app, hidden) => app.page.evaluate((x) => { window.__probeHidden = x; document.dispatchEvent(new Event('visibilitychange')); }, !!hidden);
/** 視窗取得／失去焦點（focus／blur） */
export const setFocused = (app, focused) => app.page.evaluate((x) => { window.__probeFocused = x; window.dispatchEvent(new Event(x ? 'focus' : 'blur')); }, !!focused);
/** 網路連上／中斷（online／offline 事件；routeWebSocket 是頁內模擬，連線本身要以假 hub 的 drop() 斷） */
export const setOnline = (app, online) => app.page.evaluate((x) => { window.__probeOnline = x; window.dispatchEvent(new Event(x ? 'online' : 'offline')); }, !!online);
/** Date.now 往後推 ms 毫秒（累加；計時器照真實時間） */
export const advanceClock = (app, ms) => app.page.evaluate((x) => { window.__probeClockOffset = (Number(window.__probeClockOffset) || 0) + x; return window.__probeClockOffset; }, Number(ms) || 0);
/** 剪貼簿寫入成功（allow）或被拒（deny） */
export const setClipboard = (app, mode) => app.page.evaluate((x) => { window.__probeClipboard = x; }, mode === 'deny' ? 'deny' : 'allow');
/** 通知的權限（default／granted／denied）與 requestPermission 的回答 */
export const setNotification = (app, permission, answer) => app.page.evaluate(([p, a]) => {
  window.__probeNotifyPerm = p;
  if (a) window.__probeNotifyAnswer = a;
}, [permission, answer || null]);
/** 模擬使用者點了第 i 則系統通知；沒有 onclick 回 false */
export const clickNotification = (app, i) => app.page.evaluate((x) => window.__clickNotification(x), i);
/** localStorage／sessionStorage 改成存取就丟例外（true）或恢復（false） */
export const setStorageThrows = (app, which, throws) => app.page.evaluate(([w, t]) => {
  if (w === 'session') window.__probeSessionThrows = t;
  else window.__probeLocalThrows = t;
}, [which, !!throws]);
