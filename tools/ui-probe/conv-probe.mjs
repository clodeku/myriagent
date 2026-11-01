// 伺服器對話的瀏覽器探針（WP4.2 步 15；計畫 docs/plans/2026-10-19-wp4.2-server-conversations.md，SEC-06、SEC-12 的瀏覽器一半）：
// 用 Playwright（本機 Chrome、無頭、暫存設定檔）把伺服器渲染的 Item 真的放進網頁的 DOM，確認沒有任何腳本執行、沒有 CSP 違規、主控台沒有錯誤。
// - 輸入：tests/MyriagentHub.Tests/Rendering/Golden/hub-items.json（HubItemCorpus 經伺服器渲染的 Item）與 hub-xss.json（ItemSanitizerXssCorpus 經
//   ItemSanitizer 淨化後的輸出）。兩份都由 MyriagentHub.Tests 的黃金檔測試寫出（MYRIAGENT_GOLDEN_RECORD=1 重錄），這裡只讀。
// - 桌面模式（https://myriagent.local：最小的 WebView2 模擬、桌面的 CSP）：兩份都當成 conversation 送進網頁——hub-items 的兩組各一個對話，
//   XSS 語料放進 Assistant 的 html、User 的 html 與 PlanApproval 的 detailHtml 各一個對話。每個對話展開所有 details、從頭捲到尾（lazy 的圖片才會載入），
//   斷言 CSP 違規 0 次、window.__xss 計數 0（alert／confirm／prompt／print 也算）、主控台沒有錯誤；另外掃 DOM：內容區（.md）只有淨化器白名單的元素與屬性，
//   整份文件沒有 on 屬性、沒有 javascript: 之類的網址，也沒有對外站的請求。
// - 瀏覽器模式（https://hub.probe.local/app/：伺服器的 CSP、真的 SignalR 程式庫與 transport-hub.js、fake-signalr.mjs 的假 hub）：
//   init.conversations 與 conversations 推送畫出分組清單、unread:true 的一列畫出未讀的點（F49，不改網頁）；接著（WP4.6 步 12）切到聊天畫面、
//   從側欄開對話，假 hub 回 open 時把同一份 XSS 語料放進聊天窗格（Assistant 的 html、User 的 html、PlanApproval 的 detailHtml 各一個對話），
//   和桌面一樣展開、捲動、掃 DOM；notify 要顯示成頁內提示（文字照字面）、沒有按複製就推來的 clipboard 要被略過（剪貼簿沒寫、沒有對話框，WP4.6 D42）、
//   按了複製之後的 clipboard 要寫進剪貼簿（被拒時開退路框）、resync 要對開著的那個對話重送 open。剪貼簿是頁內的假物件（記下每次寫入，不碰真的剪貼簿）。
//   --control 時瀏覽器另開一個沒有淨化的對話（最後才開），CSP、DOM 與主控台的檢查也要抓到。
// - 結果先寫 out-conv/conv-report.json 再關瀏覽器（關閉加時限）；關閉之後確認這個程序開的 chrome.exe 都結束了，結果補進同一份報告。
//   和 tools/hub-e2e 的工具共用序列化鎖（../hub-e2e/lock.mjs），同時只跑一支。開頭只清空 out-conv/，不碰 out/ 與 out-remote/。
//
// 用法：cd tools/ui-probe && npm install && node conv-probe.mjs
//   顯示瀏覽器視窗：node conv-probe.mjs --headed
//   陽性對照：node conv-probe.mjs --control（另送一個沒有淨化的對話；CSP、DOM 與主控台的檢查都要抓到它才算探針有效，抓到時結束碼 0）
import { chromium } from 'playwright';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { FakeSignalRHub } from './fake-signalr.mjs';
import * as RF from './remote-fixtures.mjs';
import { CLAUDE_MODES } from './fixtures.mjs';
import { acquireLock, releaseLock } from '../hub-e2e/lock.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const REPO = path.resolve(here, '..', '..');
const WEB = path.join(REPO, 'src', 'Myriagent', 'Web');
const WWWROOT = path.join(REPO, 'src', 'MyriagentHub', 'wwwroot');
const GOLDEN = path.join(REPO, 'tests', 'MyriagentHub.Tests', 'Rendering', 'Golden');
const OUT = path.join(here, 'out-conv');
const ORIGIN = RF.ORIGIN;
const DESKTOP_ORIGIN = RF.DESKTOP_ORIGIN;
const HUB_URL = 'wss://hub.probe.local/hubs/v1/browser';
// 伺服器的 CSP：SecurityHeaderRules.ContentSecurityPolicy("https://hub.probe.local")（src/MyriagentHub/Hosting/SecurityHeaders.cs）。
// 這一行要和伺服器的結果逐字相同（tests/MyriagentHub.Tests/Web/TransportHubFileTests 的 conv_probe的CSP與伺服器相同）
const HUB_CSP = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; media-src 'self' blob:; font-src 'self' data:; connect-src 'self' wss://hub.probe.local; manifest-src 'self'; base-uri 'none'; form-action 'self'; frame-src 'self'; frame-ancestors 'none'; object-src 'none'; worker-src 'none'";
// 桌面宿主回應 index.html 時帶的 CSP（同 probe.mjs、remote-probe.mjs）
const DESKTOP_CSP = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; media-src 'self' blob:; " +
  "font-src 'self' data:; connect-src 'self'; frame-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'; object-src 'none'";
// index.html 載入網頁主程式的那一行（AssetServerRules.Anchor）：傳輸腳本插在它前面
const ANCHOR = '<script type="module" src="app.js"></script>';
const MIME = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.svg': 'image/svg+xml', '.png': 'image/png', '.json': 'application/json; charset=utf-8', '.txt': 'text/plain; charset=utf-8' };
// /m/、/t/ 的替身：1×1 的 PNG（伺服器的媒體端點是 WP4.7；這裡只要圖片載得到、主控台沒有「Failed to load resource」）
const PNG_1X1 = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==', 'base64');
const HEADED = process.argv.includes('--headed');
const CONTROL = process.argv.includes('--control');
const NOW = Date.now();
const MIN = 60e3;
const HOUR = 3600e3;
const DAY = 86400e3;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/** 淨化器的標籤白名單（src/MyriagentHub/Rendering/ItemSanitizer.cs 的 Tags）加上 items.js 的 enhanceMarkdown 包的 div（.code、.tbl） */
const MD_TAGS = new Set(['p', 'br', 'hr', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'strong', 'em', 'del', 'code', 'pre', 'blockquote',
  'ul', 'ol', 'li', 'table', 'thead', 'tbody', 'tr', 'th', 'td', 'a', 'img', 'span', 'input', 'div']);
/** 淨化器留下的屬性（ItemSanitizer 的說明）加上 enhanceMarkdown 設的 loading、decoding 與燈箱的 data-lb-* */
const MD_ATTRS = new Set(['href', 'title', 'src', 'alt', 'class', 'start', 'colspan', 'rowspan', 'style', 'type', 'disabled', 'checked', 'rel', 'target',
  'loading', 'decoding', 'data-lb-url', 'data-lb-name', 'data-lb-path']);

fs.rmSync(OUT, { recursive: true, force: true });
fs.mkdirSync(OUT, { recursive: true });

const report = {
  generatedAt: new Date().toISOString(), chrome: null, webDir: WEB, wwwroot: WWWROOT, outDir: OUT, inputs: {},
  checks: {}, details: {}, screenshots: [], consoleErrors: [], notes: [], blockedRequests: [], chromeProcesses: null, lock: null,
};
function check(name, ok, detail) {
  report.checks[name] = !!ok;
  if (detail !== undefined) report.details[name] = detail;
  console.log(`[${ok ? '通過' : '失敗'}] ${name}`);
}

async function until(fn, ms = 6000, step = 50) {
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

// ───── 輸入：MyriagentHub.Tests 的黃金檔 ─────

function readGolden(name) {
  const file = path.join(GOLDEN, name);
  const bytes = fs.readFileSync(file);
  return { file, sha256: crypto.createHash('sha256').update(bytes).digest('hex'), json: JSON.parse(bytes.toString('utf8')) };
}

const hubItems = readGolden('hub-items.json');
const hubXss = readGolden('hub-xss.json');
report.inputs = {
  hubItems: { file: hubItems.file, sha256: hubItems.sha256, production: hubItems.json.production.length, withMediaUrls: hubItems.json.withMediaUrls.length },
  hubXss: { file: hubXss.file, sha256: hubXss.sha256, cases: hubXss.json.cases.length },
};

const T0 = NOW - 2 * HOUR;
const pad = (i) => String(i).padStart(3, '0');

/** XSS 語料的三種放法：Assistant 的 html（patchHtml）、User 的 html（setHtml）、PlanApproval 的 detailHtml（setHtml） */
function xssItems(kind) {
  return hubXss.json.cases.map((c, i) => {
    const base = { id: `xss-${kind}-${pad(i)}`, status: 'Done', time: T0 + i * 1000, seq: i + 1 };
    switch (kind) {
      case 'assistant': return Object.assign(base, { kind: 'Assistant', html: c.html });
      case 'user': return Object.assign(base, { kind: 'User', html: c.html, attachments: [] });
      case 'plan': return Object.assign(base, {
        kind: 'PlanApproval', html: '', toolKind: 'plan', toolName: '計畫', title: c.name, detail: null, output: null, decision: 'manual',
        canAllowForSession: false, filePath: null, detailHtml: c.html,
      });
      default: throw new Error('不認得的放法：' + kind);
    }
  });
}

/** 桌面模式送的對話：hub-items 的兩組、XSS 語料的三種放法 */
const DESKTOP_CONVS = [
  { id: 'c-hub-production', title: '伺服器渲染（WP4.2 的網址）', items: hubItems.json.production.map((e) => e.item) },
  { id: 'c-hub-media', title: '伺服器渲染（WP4.7 形狀的媒體網址）', items: hubItems.json.withMediaUrls.map((e) => e.item) },
  { id: 'c-xss-assistant', title: 'XSS 語料：Assistant 的 html', items: xssItems('assistant') },
  { id: 'c-xss-user', title: 'XSS 語料：User 的 html', items: xssItems('user') },
  { id: 'c-xss-plan', title: 'XSS 語料：PlanApproval 的 detailHtml', items: xssItems('plan') },
];

/** 陽性對照（--control）：沒有經過淨化器的 HTML，探針的 CSP、DOM 與主控台檢查都要抓到 */
const CONTROL_HTML = [
  '<p><img src="x" onerror="alert(1)"></p>',
  '<p><a href="javascript:alert(1)">點我</a></p>',
  '<iframe src="https://evil.example/"></iframe>',
  '<p><svg><circle r="1"></circle></svg></p>',
];
/** 對照開著時應該失敗的檢查（桌面與瀏覽器各四項） */
const CONTROL_EXPECT_FAIL = ['desktopDomOnlyAllowed', 'desktopMediaSameOriginOnly', 'desktopCspNoViolations', 'desktopNoConsoleErrors',
  'hubDomOnlyAllowed', 'hubMediaSameOriginOnly', 'hubCspNoViolations', 'hubNoConsoleErrors'];
const controlItems = () => CONTROL_HTML.map((html, i) => ({ id: `control-${pad(i)}`, kind: 'Assistant', status: 'Done', time: T0 + i * 1000, seq: i + 1, html }));
if (CONTROL) DESKTOP_CONVS.push({ id: 'c-control', title: '陽性對照：沒有淨化的 HTML', items: controlItems() });

/** 伺服器的標頭形狀（src/MyriagentHub/Realtime/ConversationViewRules.cs 的 Header，含 P5 的裝置欄位）；accountId 預設是桌面夾具的 a1 */
function serverHeader(id, title, accountId = 'a1') {
  return {
    id, accountId, provider: 'Claude', title, model: 'default', effort: 'default', permission: 'Ask', permissionModes: CLAUDE_MODES,
    models: [{ value: 'default', label: '預設', description: '帳戶預設模型', efforts: [] }], commands: [], workingDirectory: 'C:\\repo\\demo',
    customFolder: false, recentFolders: [], isDraft: false, running: false, status: null, startedAt: null, lastTurn: '輸入 1,234・輸出 567 tokens',
    accountMissing: false, restart: null, deviceId: 'd_A', deviceName: 'OFFICE-PC', deviceOnline: true,
  };
}

/** 伺服器的輸入區形狀（ConversationViewRules.Composer）：附件一律空的、佇列帶 queueMore（P11） */
const SERVER_COMPOSER = { attachments: [], busy: false, busyText: null, queue: [{ id: 'q1', text: '排隊中的訊息' }], queueMore: 2 };

function conversationMessage(c, paneId) {
  return { type: 'conversation', conversationId: c.id, paneId, header: serverHeader(c.id, c.title), items: c.items, hasMore: false, composer: SERVER_COMPOSER, draft: null };
}

// ───── 每個頁面最早執行的探針腳本（CDP 注入，不受 CSP 影響）─────

/**
 * CSP 違規收集器（也送回 Node 端）、window.__xss 計數器（alert／confirm／prompt／print 被呼叫就加一；語料的 window.__xss=1 也會讓它不是 0）、
 * window.open 記錄器
 */
function probeInit() {
  window.__csp = [];
  document.addEventListener('securitypolicyviolation', (e) => {
    const v = { directive: e.violatedDirective, blocked: e.blockedURI, source: e.sourceFile, line: e.lineNumber, sample: e.sample || '', href: location.href };
    window.__csp.push(v);
    if (typeof window.__probeCsp === 'function') window.__probeCsp(v);
  });
  window.__xss = 0;
  window.__xssCalls = [];
  for (const name of ['alert', 'confirm', 'prompt', 'print']) {
    window[name] = function (...args) {
      window.__xss = (typeof window.__xss === 'number' ? window.__xss : 1) + 1;
      window.__xssCalls.push({ name, args: args.map((a) => String(a)).slice(0, 2) });
      return null;
    };
  }
  window.__opened = [];
  window.open = function (...args) { window.__opened.push(args.map((a) => (a === undefined ? null : String(a)))); return null; };
}

/**
 * 瀏覽器模式的假剪貼簿（同 hub-page.mjs 的 probeInit；不碰真的剪貼簿）：window.__probeClipboard 是 'allow' 時寫入成功、'deny' 時照瀏覽器在沒有
 * 使用者手勢時的樣子丟 NotAllowedError；每次寫入都記在 window.__clipboard
 */
function clipboardInit() {
  window.__probeClipboard = 'allow';
  window.__clipboard = [];
  const fake = {
    writeText(text) {
      const ok = window.__probeClipboard !== 'deny';
      window.__clipboard.push({ text: String(text), ok });
      return ok ? Promise.resolve() : Promise.reject(new DOMException('探針：剪貼簿被拒絕', 'NotAllowedError'));
    },
    readText() { return Promise.reject(new DOMException('探針：不讀剪貼簿', 'NotAllowedError')); },
  };
  Object.defineProperty(Navigator.prototype, 'clipboard', { configurable: true, get() { return fake; } });
}

// ───── 桌面情境：最小的 WebView2 模擬（照 remote-probe.mjs 的 MOCK 形狀）─────

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

function mediaResponse(route) {
  return route.fulfill({ status: 200, headers: { 'Content-Type': 'image/png', 'Cache-Control': 'no-store' }, body: PNG_1X1 });
}

async function serveDesktop(route) {
  const url = new URL(route.request().url());
  if (url.pathname.startsWith('/m/') || url.pathname.startsWith('/t/')) return mediaResponse(route);
  const name = url.pathname === '/' ? 'index.html' : decodeURIComponent(url.pathname.slice(1));
  if (/[\\/]/.test(name)) return route.fulfill({ status: 404, headers: { 'Cache-Control': 'no-store' }, body: '' });
  const file = path.join(WEB, name);
  if (!fs.existsSync(file) || !fs.statSync(file).isFile()) return route.fulfill({ status: 404, headers: { 'Cache-Control': 'no-store' }, body: '' });
  const headers = { 'Content-Type': MIME[path.extname(name)] || 'application/octet-stream', 'Cache-Control': 'no-store' };
  if (name === 'index.html') headers['Content-Security-Policy'] = DESKTOP_CSP;
  return route.fulfill({ status: 200, headers, body: fs.readFileSync(file) });
}

// ───── 伺服器的 /app/（照 src/MyriagentHub/Web/AssetServerRules.cs，同 remote-probe.mjs 的作法）─────

function libraryNames() {
  return fs.readdirSync(path.join(WWWROOT, 'lib')).filter((n) => /^signalr-[0-9]+\.[0-9]+\.[0-9]+\.min\.js$/.test(n)).sort();
}

/** 照 AssetServerRules.InjectScripts：依序插在錨點前，縮排照錨點那一行；錨點不是正好一個就丟例外 */
function injectScripts(html, scripts) {
  const first = html.indexOf(ANCHOR);
  if (first < 0 || html.indexOf(ANCHOR, first + ANCHOR.length) >= 0) throw new Error('index.html 的 app.js 錨點應該正好一個');
  const lineStart = html.lastIndexOf('\n', Math.max(0, first - 1)) + 1;
  let indent = html.slice(lineStart, first);
  if (/[^ \t]/.test(indent)) indent = '';
  return html.slice(0, first) + scripts.map((s) => `<script src="${s}"></script>\n${indent}`).join('') + html.slice(first);
}

function injectedScripts() {
  const libs = libraryNames();
  if (libs.length !== 1 || !fs.existsSync(path.join(WWWROOT, 'transport-hub.js'))) throw new Error(`wwwroot 的組合不合法：程式庫 ${libs.length} 支`);
  return ['/lib/' + libs[0], '/transport-hub.js'];
}

function fileResponse(route, file, extraHeaders) {
  if (!fs.existsSync(file) || !fs.statSync(file).isFile()) return route.fulfill({ status: 404, headers: { 'Cache-Control': 'no-store' }, body: '' });
  const headers = Object.assign({ 'Content-Type': MIME[path.extname(file)] || 'application/octet-stream', 'Cache-Control': 'no-cache', 'Content-Security-Policy': HUB_CSP }, extraHeaders || {});
  return route.fulfill({ status: 200, headers, body: fs.readFileSync(file) });
}

async function serveHub(route, apiSeen) {
  const url = new URL(route.request().url());
  const p = url.pathname;
  if (p.startsWith('/api/')) {
    apiSeen.push(route.request().method() + ' ' + p);
    const body = p === '/api/v1/auth/me' ? { name: 'admin', serverDomain: 'hub.probe.local', session: {} } : { error: 'not_found' };
    return route.fulfill({ status: p === '/api/v1/auth/me' ? 200 : 404, headers: { 'Content-Type': MIME['.json'], 'Cache-Control': 'no-store', 'Content-Security-Policy': HUB_CSP }, body: JSON.stringify(body) });
  }
  if (p.startsWith('/m/') || p.startsWith('/t/')) return mediaResponse(route);
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

// ───── 頁面 ─────

let browser;
const pages = [];

/**
 * 新的 context：先掛「其他網址一律擋下」（記進 blockedRequests），再掛這個情境的來源（Playwright 後掛的先比對）；
 * 主控台的 error、warning 與 pageerror 都記下；extraInit 是另一支最早執行的探針腳本（瀏覽器模式的假剪貼簿）
 */
async function newApp(name, kind, originHandler, origin, extraInit) {
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1, colorScheme: 'dark', locale: 'zh-TW', timezoneId: 'Asia/Taipei' });
  await context.route('**/*', (route) => {
    report.blockedRequests.push({ page: name, url: route.request().url() });
    return route.abort('blockedbyclient');
  });
  await context.route(origin + '/**', originHandler);
  const csp = [];
  await context.exposeBinding('__probeCsp', (source, v) => { csp.push(v); });
  await context.addInitScript(probeInit);
  if (extraInit) await context.addInitScript(extraInit);
  const page = await context.newPage();
  const app = { name, kind, context, page, csp, console: [] };
  page.on('console', (m) => {
    if (m.type() === 'error' || m.type() === 'warning') app.console.push({ type: m.type(), text: m.text(), url: (m.location() && m.location().url) || '' });
  });
  page.on('pageerror', (e) => app.console.push({ type: 'pageerror', text: String((e && e.message) || e), url: '' }));
  pages.push(app);
  return app;
}

const errorsOf = (app) => app.console.filter((m) => m.type === 'error' || m.type === 'pageerror');

const xssState = (page) => page.evaluate(() => ({ xss: window.__xss, calls: window.__xssCalls.slice(0, 10), opened: window.__opened.length, csp: window.__csp.length }));

/**
 * 掃 DOM（頁內執行）：conversation 的內容區（.md，不含 enhanceMarkdown 加的 .code-head 與行號欄 .code-gutter）只能有淨化器白名單的元素與屬性、
 * href 只能是 https:／http:／mailto:／#／本站 /m/、/f/，src 只能是本站 /m/、/t/；整份文件不能有 on 屬性，任何網址屬性不能是 javascript:／vbscript:／data:text/html
 */
function scanDom(allow) {
  const tags = new Set(allow.tags);
  const attrs = new Set(allow.attrs);
  const mmTags = new Set(['svg', 'rect', 'circle', 'ellipse', 'polygon', 'path', 'line', 'text', 'tspan']);
  const mmAttrs = new Set(['class', 'viewbox', 'width', 'height', 'x', 'y', 'rx', 'ry', 'cx', 'cy', 'r', 'points', 'd', 'x1', 'y1', 'x2', 'y2', 'text-anchor', 'dominant-baseline', 'dx', 'dy']);
  const findings = [];
  const note = (where, el, why) => { if (findings.length < 50) findings.push({ where, why, tag: el.tagName.toLowerCase(), html: el.outerHTML.slice(0, 200) }); };
  const URL_ATTRS = ['href', 'src', 'action', 'formaction', 'xlink:href', 'srcset', 'poster', 'data', 'background', 'ping', 'srcdoc'];
  const clean = (v) => String(v || '').replace(/[\u0000-\u0020\u007f-\u009f]/g, '').toLowerCase();
  let mdContainers = 0;
  let mdElements = 0;
  for (const md of document.querySelectorAll('section.conv .md')) {
    mdContainers++;
    for (const el of md.querySelectorAll('*')) {
      const head = el.closest('.code-head, .code-gutter');
      if (head && md.contains(head)) continue;
      const diag = el.closest('.code-diagram');
      if (diag && md.contains(diag) && diag.parentElement && diag.parentElement.classList.contains('code-mermaid')) {
        mdElements++;
        if (el === diag) {
          for (const a of el.attributes) {
            if (a.name !== 'class' && a.name !== 'hidden') note('md', el, 'code-diagram 白名單以外的屬性 ' + a.name);
          }
          continue;
        }
        const mtag = el.tagName.toLowerCase();
        if (!mmTags.has(mtag)) note('md', el, 'Mermaid 圖表白名單以外的元素 ' + mtag);
        for (const a of el.attributes) {
          if (!mmAttrs.has(a.name.toLowerCase())) note('md', el, 'Mermaid 圖表白名單以外的屬性 ' + a.name);
        }
        continue;
      }
      mdElements++;
      const tag = el.tagName.toLowerCase();
      if (!tags.has(tag)) note('md', el, '白名單以外的元素');
      if (tag === 'div' && !el.classList.contains('code') && !el.classList.contains('tbl')) note('md', el, '不是 enhanceMarkdown 加的 div');
      for (const a of el.attributes) {
        if (!attrs.has(a.name)) note('md', el, '白名單以外的屬性 ' + a.name);
      }
      const href = el.getAttribute('href');
      if (href !== null && !/^(https:|http:|mailto:|#|\/m\/|\/f\/)/i.test(clean(href))) note('md', el, 'href 不合格：' + href.slice(0, 80));
      const src = el.getAttribute('src');
      if (src !== null && !/^\/(m|t)\//.test(clean(src))) note('md', el, 'src 不合格：' + src.slice(0, 80));
    }
  }
  let all = 0;
  for (const el of document.querySelectorAll('*')) {
    all++;
    for (const a of el.attributes) {
      if (/^on/i.test(a.name)) note('document', el, 'on 屬性 ' + a.name);
      if (URL_ATTRS.includes(a.name.toLowerCase()) && /^(javascript:|vbscript:|data:text\/html)/.test(clean(a.value))) note('document', el, '可執行的網址 ' + a.name);
    }
  }
  const scripts = Array.from(document.scripts).map((s) => s.getAttribute('src'));
  return { findings, mdContainers, mdElements, elements: all, scripts };
}

const SCAN_ALLOW = { tags: Array.from(MD_TAGS), attrs: Array.from(MD_ATTRS) };

/** 這個對話的根節點裡畫出的項目數 */
const renderedCount = (page, id) => page.evaluate((cid) => {
  const root = document.querySelector(`section.conv[data-conv="${cid}"]`);
  return root ? root.querySelectorAll('.items > .it').length : -1;
}, id);

/** 展開這個對話所有的 details（Thinking、工具的內容在展開時才填），再從頭捲到尾（lazy 的圖片載入） */
async function exercise(page, id) {
  await page.evaluate((cid) => {
    const root = document.querySelector(`section.conv[data-conv="${cid}"]`);
    if (root) for (const d of root.querySelectorAll('.items details')) d.open = true;
  }, id);
  await sleep(150);
  const steps = await page.evaluate((cid) => {
    const sc = document.querySelector(`section.conv[data-conv="${cid}"] .scroller`);
    if (!sc) return 0;
    sc.scrollTop = 0;
    return Math.ceil(sc.scrollHeight / Math.max(1, sc.clientHeight)) + 1;
  }, id);
  for (let i = 0; i < steps; i++) {
    await page.evaluate((cid) => {
      const sc = document.querySelector(`section.conv[data-conv="${cid}"] .scroller`);
      if (sc) sc.scrollTop += sc.clientHeight;
    }, id);
    await sleep(40);
  }
  await sleep(250);
  return steps;
}

// ───── 桌面模式：伺服器渲染的 Item 與淨化後的 XSS 語料進 DOM ─────

async function runDesktop() {
  const app = await newApp('desktop', 'desktop', (route) => serveDesktop(route), DESKTOP_ORIGIN);
  const { page } = app;
  const posted = [];
  const init = RF.desktopInit(NOW);
  init.layout = { panes: [{ id: 'p1', tabs: [], active: null }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' };
  init.conversations = DESKTOP_CONVS.map((c, i) => ({
    id: c.id, accountId: 'a1', provider: 'Claude', title: c.title, group: '今天', updatedAt: NOW - i * MIN, workingDirectory: 'C:\\repo\\demo', customFolder: false,
    running: false, waiting: false, unread: false, deviceId: 'd_A', deviceName: 'OFFICE-PC', deviceOnline: true,
  }));
  const send = (msg) => page.evaluate((m) => window.__host(m), msg);
  await page.addInitScript(MOCK);
  await page.exposeFunction('__hostRecv', (msg) => {
    posted.push(msg);
    if (msg.cmd === 'ready') send(init).catch(() => {});
  });
  await page.goto(DESKTOP_ORIGIN + '/index.html');
  const ready = await page.waitForFunction(() => document.getElementById('app').classList.contains('ready'), null, { timeout: 15000 }).then(() => true).catch(() => false);

  const perConv = [];
  for (const c of DESKTOP_CONVS) {
    await send(conversationMessage(c, 'p1'));
    const n = await until(async () => { const k = await renderedCount(page, c.id); return k === c.items.length ? k : 0; }, 10000);
    const steps = await exercise(page, c.id);
    perConv.push({ id: c.id, sent: c.items.length, rendered: await renderedCount(page, c.id), scrollSteps: steps, ok: n === c.items.length });
    await shot(page, 'desktop-' + c.id);
  }
  check('desktopItemsRendered', ready && perConv.every((x) => x.ok), { ready, perConv });

  const xs = await xssState(page);
  check('desktopXssNotExecuted', xs.xss === 0 && xs.calls.length === 0, xs);
  const scan = await page.evaluate(scanDom, SCAN_ALLOW);
  check('desktopDomOnlyAllowed', scan.findings.length === 0 && scan.mdContainers > 0, scan);
  const images = await page.evaluate(() => Array.from(document.querySelectorAll('section.conv img')).map((i) => ({ src: i.getAttribute('src'), complete: i.complete, w: i.naturalWidth })));
  check('desktopMediaSameOriginOnly', images.length > 0 && images.every((i) => /^\/(m|t)\//.test(i.src || '')), { images: images.length, sample: images.slice(0, 10) });
  // 複製整則（copyItem）、核准之類的指令不在這個探針的範圍：只確認網頁沒有自己送出 openLink（沒有點任何東西）
  check('desktopNoUnexpectedCommands', !posted.some((m) => m.cmd === 'openLink'), { commands: Array.from(new Set(posted.map((m) => m.cmd))) });
  app.after = { csp: app.csp.slice(), errors: errorsOf(app) };
  check('desktopCspNoViolations', app.csp.length === 0, { violations: app.csp, csp: DESKTOP_CSP });
  check('desktopNoConsoleErrors', errorsOf(app).length === 0, { errors: errorsOf(app) });
}

// ───── 瀏覽器模式：對話清單、未讀的點、resync／notify／clipboard ─────

/** 伺服器的清單列（ConversationViewRules.Summary，含 P5 的裝置欄位）；group 由伺服器依連線的時區算好 */
function summary(id, title, group, updatedAt, o = {}) {
  return Object.assign({
    id, accountId: 'la_c1', provider: 'Claude', title, group, updatedAt, workingDirectory: 'C:\\repo\\demo', customFolder: false,
    running: false, waiting: false, unread: false, deviceId: 'd_A', deviceName: 'OFFICE-PC', deviceOnline: true,
  }, o);
}

/** 臺灣時間的「YYYY 年 M 月」（AgentHubRules.GetGroupLabel 對不同年份的寫法） */
function monthLabel(ms) {
  const d = new Date(ms + 8 * HOUR);
  return `${d.getUTCFullYear()} 年 ${d.getUTCMonth() + 1} 月`;
}

const XSS_TITLE = '標題 ' + RF.XSS;
const OLD = NOW - 400 * DAY;
const LIST1 = [
  summary('c1', '執行中的對話', '今天', NOW - 2 * MIN, { running: true }),
  summary('c2', '有新回覆的對話', '今天', NOW - 5 * MIN, { unread: true }),
  summary('c3', '等你核准的對話', '今天', NOW - 9 * MIN, { waiting: true, unread: true }),
  summary('c4', XSS_TITLE, '昨天', NOW - DAY - HOUR, { accountId: 'la_x1', provider: 'Codex' }),
  summary('c5', '上週的對話', '過去 7 天', NOW - 4 * DAY),
  summary('c6', '去年的對話（電腦離線）', monthLabel(OLD), OLD, { deviceId: 'd_B', deviceName: '筆電', deviceOnline: false }),
];
const LIST2 = [
  summary('c7', '剛完成的新對話', '今天', NOW - MIN, { unread: true }),
  summary('c1', '執行中的對話', '今天', NOW - 2 * MIN, { unread: true }),
  summary('c2', '有新回覆的對話', '今天', NOW - 5 * MIN),
  summary('c3', '等你核准的對話', '今天', NOW - 9 * MIN, { waiting: true, unread: true }),
  summary('c4', XSS_TITLE, '昨天', NOW - DAY - HOUR, { accountId: 'la_x1', provider: 'Codex' }),
  summary('c5', '上週的對話', '過去 7 天', NOW - 4 * DAY, { unread: true }),
];

/** 清單照伺服器的順序分組：[{ g }｜{ id, title }]，同 sidebar.js 的 renderHistory */
function expectedEntries(list) {
  const out = [];
  let group = null;
  for (const c of list) {
    if (c.group !== group) { group = c.group; out.push({ g: group }); }
    out.push({ id: c.id, title: c.title });
  }
  return out;
}

/** 側欄清單的 DOM：分組標題、每一列的標題（textContent）、狀態（未讀的點、等待、執行中） */
function sidebarState(page) {
  return page.evaluate(() => {
    const list = document.querySelector('.sb-list');
    const sidebar = document.getElementById('sidebar');
    if (!list) return null;
    const entries = [];
    for (const el of list.children) {
      if (el.classList.contains('sb-group')) entries.push({ g: el.textContent });
      else if (el.classList.contains('crow')) {
        entries.push({
          id: el._id || null, title: el.querySelector('.crow-title').textContent,
          dot: !!el.querySelector('.crow-state .dot-unread'), wait: !!el.querySelector('.crow-state .badge-wait'), spin: !!el.querySelector('.crow-state .spin'),
          imgs: el.querySelectorAll('img').length,
        });
      }
    }
    return { entries, sidebarHidden: !!(sidebar && sidebar.hidden), ready: document.getElementById('app').classList.contains('ready') };
  });
}

const sameEntries = (got, list) => {
  const want = expectedEntries(list);
  return !!got && got.length === want.length && want.every((w, i) => (w.g !== undefined ? got[i].g === w.g : got[i].id === w.id && got[i].title === w.title));
};

const rowOf = (st, id) => (st ? st.entries.find((e) => e.id === id) : null) || {};

// ───── 瀏覽器模式的聊天窗格（WP4.6 步 12）─────

/** 從側欄開進聊天窗格的對話：清單裡的 c1、c2、c3 各放一種 XSS 語料的放法（標題照清單） */
const BROWSER_CONVS = [
  { id: 'c1', items: xssItems('assistant') },
  { id: 'c2', items: xssItems('user') },
  { id: 'c3', items: xssItems('plan') },
];
/** --control 時最後另開的 c5：沒有淨化的 HTML */
const BROWSER_CONTROL = { id: 'c5', items: controlItems() };
const TITLE_OF = Object.fromEntries(LIST2.map((c) => [c.id, c.title]));

/** 伺服器回 open 的 conversation（paneId、draft 是 null；帳戶是瀏覽器夾具的 la_c1） */
function hubConversation(c) {
  return { type: 'conversation', conversationId: c.id, paneId: null, header: serverHeader(c.id, TITLE_OF[c.id], 'la_c1'), items: c.items, hasMore: false, composer: SERVER_COMPOSER, draft: null };
}

/** clipboard 的原文（帶 XSS：寫進剪貼簿與退路框都要照字面） */
const CLIP_TEXT = '## 複製的文字\n\n' + RF.XSS + '\n';
/** notify 與網頁的頁內提示（liverules 的 notifyText：「電腦：標題・本文」，沒有電腦名稱時只有標題） */
const NOTIFIES = [
  [{ type: 'notify', conversationId: 'c2', kind: 'completed', title: '有新回覆的對話', deviceName: 'OFFICE-PC' }, 'OFFICE-PC：有新回覆的對話・已完成'],
  [{ type: 'notify', conversationId: 'c3', kind: 'approval', title: XSS_TITLE, deviceName: RF.XSS }, RF.XSS + '：' + XSS_TITLE + '・等你核准'],
  [{ type: 'notify', conversationId: 'c5', kind: 'question', title: '上週的對話', deviceName: null }, '上週的對話・有問題等你回答'],
  [{ type: 'notify', conversationId: 'c4', kind: 'failed', title: XSS_TITLE, deviceName: '筆電' }, '筆電：' + XSS_TITLE + '・發生錯誤，沒有完成'],
];

const itemSel = (convId, itemId) => `section.conv[data-conv="${convId}"] .it[data-id="${itemId}"]`;
const toastTexts = (page) => page.evaluate(() => [...document.querySelectorAll('.toasts .toast-text')].map((e) => e.textContent));
const dialogCount = (page) => page.evaluate(() => document.querySelectorAll('.dlg-back').length);
const clipboardWrites = (page) => page.evaluate(() => window.__clipboard.slice());
const opensOf = (hub, id) => hub.cmds('open').filter((c) => c.cmd.conversationId === id).length;
/** Assistant 項目的複製鈕的字（「複製」「複製中…」「已複製」） */
const copyLabel = (page, convId, itemId) => page.evaluate((sel) => {
  const b = document.querySelector(sel + ' .it-acts .code-copy');
  return b ? b.lastChild.textContent : null;
}, itemSel(convId, itemId));

/** 按 Assistant 項目的「複製」（複製鈕在滑鼠移到項目上才出現） */
async function clickCopy(page, convId, itemId) {
  await page.hover(itemSel(convId, itemId));
  await page.click(itemSel(convId, itemId) + ' .it-acts .code-copy');
}

/** 從側欄開一個對話（假 hub 回 open 時放語料），等全部項目畫出來、展開與捲動之後截圖 */
async function openFromSidebar(page, c) {
  await page.click(`.sb-list .crow[data-key="c:${c.id}"]`);
  const n = await until(async () => { const k = await renderedCount(page, c.id); return k === c.items.length ? k : 0; }, 15000);
  const steps = await exercise(page, c.id);
  const out = { id: c.id, sent: c.items.length, rendered: await renderedCount(page, c.id), scrollSteps: steps, ok: n === c.items.length };
  await shot(page, 'browser-chat-' + c.id);
  return out;
}

async function runBrowser() {
  const apiSeen = [];
  const log = [];
  const app = await newApp('browser', 'hub', (route) => serveHub(route, apiSeen), ORIGIN, clipboardInit);
  const { page, context } = app;
  const hub = new FakeSignalRHub({ url: HUB_URL, init: (ready) => Object.assign(RF.browserInit(NOW, { mode: ready && ready.mode }), { conversations: LIST1 }), log });
  await hub.attach(context);
  app.hub = hub;
  await page.goto(ORIGIN + '/app/', { waitUntil: 'load' });
  const ready = await page.waitForFunction(() => document.getElementById('app').classList.contains('ready'), null, { timeout: 15000 }).then(() => true).catch(() => false);

  const st1 = await until(async () => { const s = await sidebarState(page); return s && sameEntries(s.entries, LIST1) ? s : null; }) || await sidebarState(page);
  check('hubListGroupedFromInit', ready && !!st1 && sameEntries(st1.entries, LIST1) && st1.entries.every((e) => !e.imgs),
    { ready, want: expectedEntries(LIST1), got: st1, note: '沒有存下的版面時瀏覽器版先顯示儀表板（側欄在聊天畫面才看得到）；檢查的是側欄清單的 DOM' });
  check('hubUnreadDotFromInit', !!st1 && rowOf(st1, 'c2').dot && !rowOf(st1, 'c1').dot && rowOf(st1, 'c1').spin && rowOf(st1, 'c3').wait && !rowOf(st1, 'c3').dot
    && !rowOf(st1, 'c4').dot && !rowOf(st1, 'c5').dot,
  { rows: st1 && st1.entries.filter((e) => e.id) });

  hub.push({ type: 'conversations', items: LIST2 });
  const st2 = await until(async () => { const s = await sidebarState(page); return s && sameEntries(s.entries, LIST2) ? s : null; }) || await sidebarState(page);
  check('hubListReplacedByPush', !!st2 && sameEntries(st2.entries, LIST2), { want: expectedEntries(LIST2), got: st2 });
  check('hubUnreadDotFromPush', !!st2 && rowOf(st2, 'c7').dot && rowOf(st2, 'c1').dot && !rowOf(st2, 'c2').dot && rowOf(st2, 'c5').dot && rowOf(st2, 'c3').wait && !rowOf(st2, 'c3').dot,
    { rows: st2 && st2.entries.filter((e) => e.id) });
  await shot(page, 'browser-dashboard');

  // 聊天窗格（SEC-12 的瀏覽器一半）：切到聊天畫面、從側欄開 c1、c2、c3，假 hub 回 open 時放進 XSS 語料（和桌面同一份）
  const convs = new Map(BROWSER_CONVS.map((c) => [c.id, c]));
  if (CONTROL) convs.set(BROWSER_CONTROL.id, BROWSER_CONTROL);
  hub.onCmd('open', (cmd) => { const c = convs.get(cmd.conversationId); return c ? { push: [hubConversation(c)] } : null; });
  hub.onCmd('copyItem', (cmd) => ({ push: [{ type: 'clipboard', conversationId: cmd.conversationId, itemId: cmd.itemId, text: CLIP_TEXT }] }));
  await page.click('#viewSwitch [data-view="chat"]');
  const perConv = [];
  for (const c of BROWSER_CONVS) perConv.push(await openFromSidebar(page, c));
  check('hubChatItemsRendered', perConv.every((x) => x.ok) && BROWSER_CONVS.every((c) => opensOf(hub, c.id) === 1), { perConv, opens: BROWSER_CONVS.map((c) => opensOf(hub, c.id)) });

  // notify、clipboard、resync（WP4.6 D19、D21、D42）：這一段沒有新的主控台錯誤、清單不變
  const before = errorsOf(app).length;
  // notify：分頁在前景 → 頁內提示，標題與電腦名稱照字面
  for (const [m] of NOTIFIES) hub.push(m);
  const want = NOTIFIES.map(([, t]) => t);
  const toasts = await until(async () => { const t = await toastTexts(page); return want.every((w) => t.includes(w)) ? t : null; }, 4000) || await toastTexts(page);
  check('hubNotifyShown', want.every((w) => toasts.includes(w)), { want, toasts });
  await shot(page, 'browser-notify');

  // 沒有按複製就推來的 clipboard：不寫剪貼簿、不開框
  hub.push({ type: 'clipboard', conversationId: 'c1', itemId: 'xss-assistant-000', text: CLIP_TEXT });
  hub.push({ type: 'clipboard', conversationId: 'c1', itemId: 'it-1', text: '複製的文字 ' + RF.XSS });
  await sleep(800);
  const unsolicited = { writes: await clipboardWrites(page), dialogs: await dialogCount(page) };
  check('hubClipboardUnsolicitedIgnored', unsolicited.writes.length === 0 && unsolicited.dialogs === 0, unsolicited);

  // 按了複製之後的 clipboard：寫進剪貼簿、按鈕標「已複製」；剪貼簿被拒時開退路框（文字框的值照字面）
  await page.click('.tab[data-key="c1"]');
  await clickCopy(page, 'c1', 'xss-assistant-000');
  const written = await until(async () => { const w = await clipboardWrites(page); return w.length ? w : null; }, 4000);
  const copied = await until(async () => ((await copyLabel(page, 'c1', 'xss-assistant-000')) === '已複製' ? true : null), 2000);
  const dialogsAfterWrite = await dialogCount(page);
  await page.evaluate(() => { window.__probeClipboard = 'deny'; });
  await clickCopy(page, 'c1', 'xss-assistant-001');
  const fallback = await until(() => page.evaluate(() => {
    const ta = document.querySelector('.dlg-copy-fallback textarea');
    return ta ? { value: ta.value, readOnly: ta.readOnly, imgs: document.querySelectorAll('.dlg-copy-fallback img').length } : null;
  }), 4000);
  await shot(page, 'browser-copy-fallback');
  const writes = await clipboardWrites(page);
  if (fallback) await page.click('.dlg-copy-fallback .dlg-foot button');
  const closed = await until(async () => ((await dialogCount(page)) === 0 ? true : null), 2000);
  const asked = hub.cmds('copyItem').map((c) => c.cmd.itemId);
  check('hubClipboardAfterCopy', !!written && written[0].ok && written[0].text === CLIP_TEXT && !!copied && dialogsAfterWrite === 0
    && !!fallback && fallback.value === CLIP_TEXT && fallback.readOnly && fallback.imgs === 0 && writes.length === 2 && !writes[1].ok && !!closed
    && JSON.stringify(asked) === JSON.stringify(['xss-assistant-000', 'xss-assistant-001']),
  { writes: writes.map((w) => ({ ok: w.ok, same: w.text === CLIP_TEXT })), copied: !!copied, dialogsAfterWrite, fallback: fallback && { readOnly: fallback.readOnly, same: fallback.value === CLIP_TEXT, imgs: fallback.imgs }, asked });

  // resync：開著的對話重送一次 open（伺服器回的 conversation 整份重建：先在項目上放標記，重建之後的新節點沒有它），沒開著的什麼都不做
  const o1 = opensOf(hub, 'c1');
  const marked = await page.evaluate((sel) => { const n = document.querySelector(sel); if (n) n.dataset.probeMark = '1'; return !!n; }, itemSel('c1', 'xss-assistant-000'));
  hub.push({ type: 'resync', conversationId: 'c1' });
  hub.push({ type: 'resync', conversationId: 'c-not-in-list' });
  const reopened = await until(() => (opensOf(hub, 'c1') > o1 ? true : null), 4000);
  const rebuilt = await until(() => page.evaluate(([sel, n]) => {
    const node = document.querySelector(sel);
    const root = document.querySelector('section.conv[data-conv="c1"]');
    return !!node && !node.dataset.probeMark && root.querySelectorAll('.items > .it').length === n ? true : null;
  }, [itemSel('c1', 'xss-assistant-000'), BROWSER_CONVS[0].items.length]), 8000);
  await sleep(600);
  check('hubResyncReopens', marked && !!reopened && !!rebuilt && opensOf(hub, 'c1') === o1 + 1 && opensOf(hub, 'c-not-in-list') === 0,
    { marked, opensBefore: o1, opensAfter: opensOf(hub, 'c1'), notInList: opensOf(hub, 'c-not-in-list'), rebuilt: !!rebuilt });

  const st3 = await sidebarState(page);
  const after = errorsOf(app);
  check('hubResyncNotifyClipboardNoError', after.length === before && !!st3 && st3.ready && sameEntries(st3.entries, LIST2),
    { errors: after.slice(before), stillReady: st3 && st3.ready });

  // 陽性對照（--control）：最後才開沒有淨化的對話，免得它的錯誤算進上面那一段
  if (CONTROL) {
    const r = await openFromSidebar(page, BROWSER_CONTROL);
    check('hubControlRendered', r.ok, r);
  }

  const xs = await xssState(page);
  check('hubXssNotExecuted', xs.xss === 0 && xs.calls.length === 0, xs);
  const scan = await page.evaluate(scanDom, SCAN_ALLOW);
  check('hubDomOnlyAllowed', scan.findings.length === 0 && scan.mdContainers > 0, scan);
  const images = await page.evaluate(() => Array.from(document.querySelectorAll('section.conv img')).map((i) => ({ src: i.getAttribute('src'), complete: i.complete, w: i.naturalWidth })));
  check('hubMediaSameOriginOnly', images.length > 0 && images.every((i) => /^\/(m|t)\//.test(i.src || '')), { images: images.length, sample: images.slice(0, 10) });
  check('hubCspNoViolations', app.csp.length === 0, { violations: app.csp, csp: HUB_CSP });
  check('hubNoConsoleErrors', errorsOf(app).length === 0, { errors: errorsOf(app), api: apiSeen, hubLog: log });
  hub.stop();
}

// ───── chrome.exe 是否殘留：這個 node 程序的子孫（記下 PID 與建立時間，關閉後逐一確認）─────

/** 所有程序的 PID、父 PID、名稱與建立時間（Windows PowerShell 5.1 的 Get-CimInstance；子程序的環境拿掉 PSModulePath，免得載到 PS7 的模組） */
function listProcesses() {
  const env = {};
  for (const [k, v] of Object.entries(process.env)) if (k.toLowerCase() !== 'psmodulepath') env[k] = v;
  const ps = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
  const script = "$ProgressPreference = 'SilentlyContinue'; Get-CimInstance Win32_Process | ForEach-Object { $c = ''; if ($_.CreationDate) { $c = $_.CreationDate.ToUniversalTime().ToString('o') }; '{0}|{1}|{2}|{3}' -f $_.ProcessId, $_.ParentProcessId, $_.Name, $c }";
  const out = execFileSync(ps, ['-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', Buffer.from(script, 'utf16le').toString('base64')],
    { env, encoding: 'utf8', timeout: 60000, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  return out.split(/\r?\n/).filter(Boolean).map((line) => {
    const [pid, ppid, name, created] = line.split('|');
    return { pid: Number(pid), ppid: Number(ppid), name, created };
  });
}

/** 這個程序（process.pid）的子孫裡的 chrome.exe */
function myChromes(all) {
  const children = new Map();
  for (const p of all) {
    if (!children.has(p.ppid)) children.set(p.ppid, []);
    children.get(p.ppid).push(p);
  }
  const out = [];
  const seen = new Set([process.pid]);
  const queue = [process.pid];
  while (queue.length) {
    const pid = queue.shift();
    for (const c of children.get(pid) || []) {
      if (seen.has(c.pid)) continue;
      seen.add(c.pid);
      queue.push(c.pid);
      if (c.name.toLowerCase() === 'chrome.exe') out.push(c);
    }
  }
  return out;
}

// ───── 主流程 ─────

let exitCode = 1;
let chromesBefore = null;
const lock = await acquireLock('conv-probe', OUT);
report.lock = { ok: lock.ok, waitedMs: lock.waitedMs, staleRemoved: lock.staleRemoved, holder: lock.holder };
if (!lock.ok) {
  report.fatal = '等不到 tools/hub-e2e 的序列化鎖（別的工具正在跑）：' + lock.holder;
  fs.writeFileSync(path.join(OUT, 'conv-report.json'), JSON.stringify(report, null, 2), 'utf8');
  console.log(report.fatal);
  process.exit(3);
}
try {
  browser = await chromium.launch({ channel: 'chrome', headless: !HEADED });
  report.chrome = browser.version();
  await runDesktop();
  await runBrowser();
  for (const app of pages) {
    for (const m of app.console) {
      if (m.type === 'error' || m.type === 'pageerror') report.consoleErrors.push({ page: app.name, ...m });
      else report.notes.push({ page: app.name, ...m });
    }
  }
  check('noBlockedRequests', report.blockedRequests.length === 0, report.blockedRequests.slice(0, 20));
  check('noConsoleErrors', report.consoleErrors.length === 0, report.consoleErrors);
  try {
    chromesBefore = myChromes(listProcesses());
  } catch (e) {
    report.notes.push({ page: '-', type: 'note', text: '列不出程序：' + String((e && e.message) || e) });
  }
} catch (e) {
  report.fatal = String((e && e.stack) || e);
  exitCode = 2;
} finally {
  // 先寫結果，再關瀏覽器（關閉加時限）
  const bools = Object.values(report.checks);
  report.allPassed = !report.fatal && bools.length > 0 && bools.every(Boolean);
  fs.writeFileSync(path.join(OUT, 'conv-report.json'), JSON.stringify(report, null, 2), 'utf8');
  for (const app of pages) await Promise.race([app.context.close().catch(() => {}), sleep(5000)]);
  if (browser) await Promise.race([browser.close().catch(() => {}), sleep(15000)]);

  // 關閉之後：關閉前記下的 chrome.exe（同一個 PID、同一個建立時間）10 秒內都要結束；還在的由這邊依 PID 結束並記成失敗
  let leftovers = [];
  let killed = [];
  if (chromesBefore) {
    const t0 = Date.now();
    while (Date.now() - t0 < 10000) {
      const now = listProcesses();
      leftovers = chromesBefore.filter((b) => now.some((p) => p.pid === b.pid && p.created === b.created));
      if (!leftovers.length) break;
      await sleep(500);
    }
    for (const p of leftovers) {
      try {
        process.kill(p.pid);
        killed.push(p.pid);
      } catch { /* 已經結束 */ }
    }
  }
  report.chromeProcesses = { beforeClose: chromesBefore ? chromesBefore.length : null, leftovers, killed };
  if (!report.fatal) {
    check('noLeftoverChrome', !!chromesBefore && chromesBefore.length > 0 && leftovers.length === 0, report.chromeProcesses);
    const all = Object.values(report.checks);
    report.allPassed = all.length > 0 && all.every(Boolean);
    exitCode = report.allPassed ? 0 : 1;
    if (CONTROL) {
      // 陽性對照：預期失敗的檢查都失敗、其他檢查照常通過，才表示探針抓得到
      const failedNames = Object.entries(report.checks).filter(([, v]) => !v).map(([k]) => k);
      const missed = CONTROL_EXPECT_FAIL.filter((k) => report.checks[k] !== false);
      const unexpected = failedNames.filter((k) => !CONTROL_EXPECT_FAIL.includes(k) && k !== 'noConsoleErrors');
      report.control = { expectFail: CONTROL_EXPECT_FAIL, failed: failedNames, missed, unexpected, sensitive: missed.length === 0 && unexpected.length === 0 };
      exitCode = report.control.sensitive ? 0 : 1;
    }
  }
  fs.writeFileSync(path.join(OUT, 'conv-report.json'), JSON.stringify(report, null, 2), 'utf8');
  const failed = Object.entries(report.checks).filter(([, v]) => !v).map(([k]) => k);
  console.log(JSON.stringify({ outDir: OUT, allPassed: report.allPassed, checks: Object.keys(report.checks).length, failed, consoleErrors: report.consoleErrors.length, chromeProcesses: report.chromeProcesses, control: report.control || null, fatal: report.fatal || null }, null, 2));
  const released = releaseLock();
  if (!released.ok) console.log(released.message);
  process.exit(exitCode);
}
