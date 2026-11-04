// Clode Myriagent 介面探針：用 Playwright（本機 Chrome）模擬 WebView2 宿主，
// 以 page.route 在 https://myriagent.local/ 提供 src/Myriagent/Web/ 的檔案（index.html 帶與 app 相同的 CSP），
// 假宿主依網頁送出的指令回應（ready→init、open→conversation、listFiles→files…），截圖並檢查協定行為，結果寫到 out/report.json。
//
// 用法：cd tools/ui-probe && npm install && node probe.mjs
//   只截一種尺寸：node probe.mjs --only 1440
//   只跑〔熱更新〕的檢查（CLI 換版的標頭、Notice、CLI 卡、設定）：node probe.mjs --hot
//   只跑電腦狀態面板（monitor-probe.mjs）：node probe.mjs --monitor
//   只跑側欄 token 計數的檢查（位置、解碼、減少動態、淺色對比；另含執行中的最後一張卡不讓帳戶清單多出捲軸）：node probe.mjs --tokens
//   只跑儀表板帳戶卡的欄數（寬度夠時一排最多三張）與 HUD 動態的檢查：node probe.mjs --dash
//   只跑對話標頭 git 晶片的檢查（徽章、選單、窄窗格、對比）：node probe.mjs --git
//   只跑輸入框的上下文用量提示（圓環、嚴重度、說明、窄窗格、淺色對比）：node probe.mjs --context
//   只跑介面持續耗用的量測（主執行緒忙碌比例、動非合成屬性的無限循環動畫）：node probe.mjs --perf
//   只跑側欄對話搜尋（標題與內容、片段、重搜、對比）的檢查：node probe.mjs --search
//   只跑登入到期（側欄帳戶卡的標示與點擊、卡片提示、儀表板的一行與「重新登入」、對比）的檢查：node probe.mjs --login
//   只跑對話框的視覺效果（靜態裝飾、一次性動畫、減少動態與關掉動態效果時不播）的檢查（fx-probe.mjs）：node probe.mjs --fx
//   只跑輸入區上方的執行列（按鈕、快捷鍵、輸出面板、目標選單、窄窗格、淺色對比）的檢查：node probe.mjs --launch
//   只跑送出中（宿主回傳使用者訊息之前先畫在對話欄、換掉、被拒絕、插入、排隊）的檢查（sending-probe.mjs）：node probe.mjs --sending
//   不必下載瀏覽器（channel: 'chrome'）；PATH 上有 ffmpeg 時會產生 3 秒測試影片，否則略過影片檢查。
import { chromium } from 'playwright';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import * as F from './fixtures.mjs';
import { runSyncSettings } from './sync-settings.mjs';
import { runMonitor } from './monitor-probe.mjs';
import { runPerf } from './perf-probe.mjs';
import { runSearch, fakeSearch } from './search-probe.mjs';
import { runHealth } from './health-probe.mjs';
import { runUsageSegments } from './usageseg-probe.mjs';
import { runMcp } from './mcp-probe.mjs';
import { runSchedules } from './schedules-probe.mjs';
import { runClaudeOpt } from './claudeopt-probe.mjs';
import { runCodex } from './codex-probe.mjs';
import { runFork } from './fork-probe.mjs';
import { runFx } from './fx-probe.mjs';
import { runLaunch } from './launch-probe.mjs';
import { runSending } from './sending-probe.mjs';
import { runSettings } from './settings-probe.mjs';
import { fakeMic } from './fake-mic.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
// MYRIAGENT_PROBE_WEB：改用別的網頁資料夾（例如 git archive 取出的舊版，做前後比較；--perf 的量測用）
const WEB = process.env.MYRIAGENT_PROBE_WEB || path.resolve(here, '..', '..', 'src', 'Myriagent', 'Web');
const OUT = path.join(here, 'out');
const ORIGIN = F.ORIGIN;
// 與宿主回應 index.html 時帶的 CSP 相同
const CSP = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; media-src 'self' blob:; " +
  "font-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'; object-src 'none'";
const MIME = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.svg': 'image/svg+xml', '.png': 'image/png', '.json': 'application/json' };
const ONLY = (() => { const i = process.argv.indexOf('--only'); return i > 0 ? process.argv[i + 1] : null; })();
const HOT_ONLY = process.argv.includes('--hot');
const SYNC_ONLY = process.argv.includes('--sync'); // 〔同步〕只跑設定頁「多電腦同步」的檢查（sync-settings.mjs）
const MONITOR_ONLY = process.argv.includes('--monitor'); // 只跑電腦狀態面板的檢查（monitor-probe.mjs）
const TOKENS_ONLY = process.argv.includes('--tokens');
const DASH_ONLY = process.argv.includes('--dash'); // 只跑儀表板帳戶卡的欄數與 HUD 動態的檢查（runDashGrid）
const GIT_ONLY = process.argv.includes('--git'); // 只跑對話標頭 git 晶片的檢查（runGit）
const PERF_ONLY = process.argv.includes('--perf'); // 只跑介面持續耗用的量測與檢查（perf-probe.mjs）
const SEARCH_ONLY = process.argv.includes('--search'); // 只跑側欄對話搜尋的檢查（search-probe.mjs）
const FLASH_ONLY = process.argv.includes('--flash'); // 只跑側欄點對話時標題列閃爍的檢查（runHeadFlash）
const HEALTH_ONLY = process.argv.includes('--health'); // 只跑儀表板健康度的檢查（health-probe.mjs）
const USAGESEG_ONLY = process.argv.includes('--usageseg'); // 只跑儀表板「每週額度每 5% 的 token」的檢查（usageseg-probe.mjs）
const MCP_ONLY = process.argv.includes('--mcp'); // 只跑儀表板 MCP 的檢查（mcp-probe.mjs）
const SCHED_ONLY = process.argv.includes('--schedules'); // 只跑排程頁的檢查（schedules-probe.mjs）
const CODEX_ONLY = process.argv.includes('--codex');
const CLAUDEOPT_ONLY = process.argv.includes('--claudeopt'); // 只跑 Claude 對話的選項與操作（claudeopt-probe.mjs）
const REPLICA_RESTORE_ONLY = process.argv.includes('--replica-restore'); // 只跑還原版面時等其他電腦的對話清單（runReplicaRestore）
const FORK_ONLY = process.argv.includes('--fork'); // 只跑延伸成新對話（fork-probe.mjs）
const LOGIN_ONLY = process.argv.includes('--login'); // 只跑登入到期的檢查（runLoginExpiry）
const CONTEXT_ONLY = process.argv.includes('--context'); // 只跑輸入框的上下文用量提示（runContext）
const BG_ONLY = process.argv.includes('--bg'); // 只跑等背景工作回報的樣貌（runBackgroundWait）
const FX_ONLY = process.argv.includes('--fx'); // 只跑對話框的視覺效果的檢查（fx-probe.mjs）
const CONTRAST_ONLY = process.argv.includes('--contrast'); // 只跑資訊文字的對比（runVisualChecks 的 textContrastAA 等）
const LAUNCH_ONLY = process.argv.includes('--launch'); // 只跑輸入區上方的執行列（launch-probe.mjs）
const SENDING_ONLY = process.argv.includes('--sending'); // 只跑送出中（sending-probe.mjs）
const ACCOUNT_ONLY = process.argv.includes('--account'); // 只跑換帳戶的選擇框與待換標記（runAccountSwitch：執行中；runAccountSwitchBackground：背景工作還在跑）
const ACCT_NAME_ONLY = process.argv.includes('--acct-name'); // 只跑側欄帳戶名稱的跑馬燈（runAccountNameMarquee）
const AGY_ONLY = process.argv.includes('--agy'); // 只跑自訂代理選單、Artifact／Mermaid 預覽、匯入本機對話的檢查（runAgyAll8）
const FIXES_ONLY = process.argv.includes('--fixes'); // 只跑對話框與選單修補項目的檢查（runConversationFixes）
const SETTINGS_ONLY = process.argv.includes('--settings'); // 只跑設定對話框的版面、代理子分頁與搜尋（settings-probe.mjs）
const COMPOSER_ONLY = process.argv.includes('--composer'); // 只跑輸入區修補項目的檢查（runComposerFixes：串流中點擊、輸入框高度…）
const SHELL_ONLY = process.argv.includes('--shell'); // 只跑殼層互動的檢查（runShell：雙擊、鍵盤焦點、選單、側欄的點擊與改名）

fs.rmSync(OUT, { recursive: true, force: true });
fs.mkdirSync(OUT, { recursive: true });
const NOW = Date.now();
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ───── 測試影片（ffmpeg testsrc2，3 秒） ─────
function makeVideo() {
  const file = path.join(OUT, 'test-3s.mp4');
  const frame = path.join(OUT, 'test-frame.png');
  const r = spawnSync('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-y', '-f', 'lavfi', '-i', 'testsrc2=duration=3:size=640x360:rate=25',
    '-f', 'lavfi', '-i', 'sine=frequency=520:duration=3', '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-shortest', '-movflags', '+faststart', file], { encoding: 'utf8' });
  if (r.error || r.status !== 0 || !fs.existsSync(file)) return null;
  spawnSync('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-y', '-ss', '1', '-i', file, '-frames:v', '1', '-vf', 'scale=320:-2', frame]);
  return { file, data: fs.readFileSync(file), frame: fs.existsSync(frame) ? fs.readFileSync(frame) : null };
}
const VIDEO = makeVideo();
const HAS_VIDEO = !!VIDEO;

// ───── 圖片（依檔名程式產生，快取） ─────
const imgCache = new Map();
function imageFor(p, size) {
  const name = String(p).split('\\').pop();
  const w = size ? Math.max(64, size * 2) : 1280;
  const hgt = Math.round(w * 0.625);
  const key = name + '|' + w;
  if (!imgCache.has(key)) imgCache.set(key, F.renderImage(name, w, hgt));
  return imgCache.get(key);
}

function serveBytes(route, data, type) {
  const range = route.request().headers()['range'];
  if (range) {
    const m = /bytes=(\d+)-(\d*)/.exec(range);
    const start = m ? Number(m[1]) : 0;
    const end = m && m[2] ? Math.min(Number(m[2]), data.length - 1) : data.length - 1;
    return route.fulfill({ status: 206, headers: { 'Content-Type': type, 'Accept-Ranges': 'bytes', 'Content-Range': `bytes ${start}-${end}/${data.length}`, 'Content-Length': String(end - start + 1) }, body: data.subarray(start, end + 1) });
  }
  return route.fulfill({ status: 200, headers: { 'Content-Type': type, 'Accept-Ranges': 'bytes', 'Content-Length': String(data.length) }, body: data });
}

async function serve(route, o = {}) {
  const url = new URL(route.request().url());
  if (url.pathname === '/media' || url.pathname === '/thumb') {
    const p = url.searchParams.get('path') || '';
    if (/\.mp4$/i.test(p)) {
      if (url.pathname === '/thumb') return route.fulfill({ status: 200, contentType: 'image/png', body: (VIDEO && VIDEO.frame) || imageFor('影片封面.png', 160) });
      if (!VIDEO) return route.fulfill({ status: 404, body: '' });
      return serveBytes(route, VIDEO.data, 'video/mp4');
    }
    const size = url.pathname === '/thumb' ? Number(url.searchParams.get('size') || 160) : 0;
    return route.fulfill({ status: 200, contentType: 'image/png', body: imageFor(p, size) });
  }
  if (url.pathname === '/open') return route.fulfill({ status: 204, body: '' });
  const name = url.pathname === '/' ? 'index.html' : decodeURIComponent(url.pathname.slice(1));
  if (/[\\/]/.test(name)) return route.fulfill({ status: 404, body: 'not found' }); // 宿主不支援子資料夾
  const file = path.join(WEB, name);
  if (!fs.existsSync(file)) return route.fulfill({ status: 404, body: 'not found' });
  const headers = { 'Content-Type': MIME[path.extname(name)] || 'application/octet-stream', 'Cache-Control': 'no-store' };
  let body = fs.readFileSync(file);
  if (name === 'index.html') {
    headers['Content-Security-Policy'] = CSP;
    // 與宿主相同（UI/WebAssetRules.ApplyInitialTheme）：送出時把 <html data-theme> 換成目前主題
    if (o.settings && o.settings.theme === 'light') body = Buffer.from(body.toString('utf8').replace('<html lang="zh-Hant-TW" data-theme="dark">', '<html lang="zh-Hant-TW" data-theme="light">'), 'utf8');
  }
  return route.fulfill({ status: 200, headers, body });
}

// ───── 模擬 chrome.webview ─────
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

const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

// ───── 假宿主 ─────
class FakeHost {
  constructor(opts, log) {
    this.opts = opts;
    this.log = log;
    this.accounts = F.makeAccounts(NOW);
    this.conversations = F.makeConversations(NOW);
    this.cli = F.makeCli(NOW);
    this.tokens = opts.tokens === undefined ? F.makeTokens() : opts.tokens;
    this.settings = Object.assign({}, F.SETTINGS, { conversationDefaults: F.makeConversationDefaults() }, opts.settings || {});
    this.data = F.makeConversationData(NOW, HAS_VIDEO);
    this.files = F.makeFiles(NOW, HAS_VIDEO);
    this.received = [];
    this.seq = 0;
    this.chain = Promise.resolve();
    this.delayOpen = 0; // 模擬宿主讀檔較慢：收到 open 後延遲多久才回 conversation
    this.holdOpen = new Set();   // 這些對話的 open 先不回（模擬宿主讀檔很慢），release(id) 才送
    this.held = new Map();
    this.fileSearchDelay = 40;
    this.searchDelay = 60;         // 側欄的內容搜尋（searchConversations）多久之後回
    this.cliCheckFail = new Set(); // 「立即檢查」時模擬查最新版失敗（離線）的 CLI
    this.ignore = new Set();       // 這些指令只記錄、不回應（模擬宿主還沒處理到）
  }

  release(id) {
    const fn = this.held.get(id);
    this.held.delete(id);
    if (fn) fn();
    return this.chain;
  }

  attach(page) { this.page = page; }

  send(msg) {
    const copy = JSON.parse(JSON.stringify(msg));
    this.chain = this.chain.then(() => this.page.evaluate((m) => window.__host(m), copy))
      .catch((e) => { this.log.errors.push('[宿主→網頁] ' + msg.type + '：' + e.message); });
    return this.chain;
  }

  conv(id) {
    if (!this.data[id]) {
      const s = this.conversations.find((x) => x.id === id);
      if (s) {
        this.data[id] = F.genericConversation(NOW, s);
        if (!this.accounts.some((a) => a.id === s.accountId)) this.data[id].header.accountMissing = true;
      }
    }
    return this.data[id] || null;
  }

  sendConversation(id, paneId) {
    const c = this.conv(id);
    if (!c) return;
    this.send({ type: 'conversation', conversationId: id, paneId: paneId || null, header: c.header, items: c.items, hasMore: c.hasMore, composer: c.composer });
  }

  pushHeader(id) { const c = this.conv(id); if (c) this.send({ type: 'header', conversationId: id, header: c.header }); }
  pushItem(id, item) { this.send({ type: 'item', conversationId: id, item }); }
  pushComposer(id) { const c = this.conv(id); if (c) this.send({ type: 'composer', conversationId: id, composer: c.composer }); }
  pushAccounts() { this.send({ type: 'accounts', accounts: this.accounts }); }
  pushTokens() { this.send({ type: 'tokens', tokens: this.tokens }); }
  pushConversations() { this.send({ type: 'conversations', items: this.conversations }); }

  recv(msg) {
    this.received.push(msg);
    if (this.ignore.has(msg.cmd)) return;
    try { this.handle(msg); } catch (e) { this.log.errors.push('[假宿主] ' + msg.cmd + '：' + e.stack); }
  }

  handle(m) {
    const id = m.conversationId;
    const c = id ? this.conv(id) : null;
    const item = (iid) => (c ? c.items.find((x) => x.id === iid) : null);
    switch (m.cmd) {
      case 'ready': {
        // restoredDrafts：app 更新後還原的草稿對話 [{ id, accountId, text }]。同 AgentHub.OnReady：init 列出 id，
        // 緊接著送它們的 conversation（paneId 為 null、帶輸入框的 draft），網頁的 newConversation 一定比它們晚處理
        const restored = this.opts.mode === 'popout' ? [] : this.opts.restoredDrafts || [];
        const init = () => {
          this.send({
            type: 'init', mode: this.opts.mode || 'main', popoutConversationId: this.opts.popoutId || null, app: { version: '0.1.0', commitAt: F.COMMIT_AT },
            settings: this.settings, accounts: this.accounts, conversations: this.conversations, cli: this.cli, providers: F.PROVIDERS,
            layout: this.opts.mode === 'popout' ? null : (this.opts.layout === undefined ? null : this.opts.layout),
            restoredDrafts: restored.map((d) => d.id),
            // 〔同步〕其他電腦的對話清單還沒載入（AgentHub.ReplicasPending）：opts.replicasPending 有給才帶
            ...(this.opts.replicasPending !== undefined ? { replicasPending: this.opts.replicasPending } : {}),
            ...(this.tokens ? { tokens: this.tokens } : {}),
            ...(this.opts.remote !== undefined ? { remote: this.opts.remote, devicesCli: this.opts.devicesCli ?? null } : {}),
            // 輸入區的語音訊息：這台有辨識程序（opts.voiceInput 是 false 時模擬沒有）
            voiceInput: this.opts.voiceInput !== false,
            // 排程頁：opts.schedules 有給才帶（沒給＝宿主沒掛排程讀取器，不顯示排程鈕）
            ...(this.opts.schedules !== undefined ? { schedules: this.opts.schedules } : {}),
          });
          for (const d of restored) {
            this.data[d.id] = {
              header: F.baseHeader(d.id, d.accountId, '', { isDraft: true, customFolder: false, workingDirectory: 'C:\\Users\\Lin\\AppData\\Local\\Myriagent\\conversations\\' + d.id + '\\workspace' }),
              items: [], hasMore: false, composer: { attachments: [], busy: false, busyText: null, queue: [] },
            };
            const c = this.data[d.id];
            this.send({ type: 'conversation', conversationId: d.id, paneId: null, header: c.header, items: c.items, hasMore: false, composer: c.composer, draft: d.text });
          }
        };
        // initDelay：模擬啟動時 UI 執行緒忙、init 晚到
        if (this.opts.initDelay) setTimeout(init, this.opts.initDelay); else init();
        break;
      }
      case 'open':
        if (this.holdOpen.has(id)) this.held.set(id, () => this.sendConversation(id));
        else if (this.delayOpen) setTimeout(() => this.sendConversation(id), this.delayOpen);
        else this.sendConversation(id);
        break;
      case 'newConversation': {
        const acc = this.accounts.find((a) => a.id === m.accountId) || this.accounts[0];
        const did = 'd' + (++this.seq);
        this.data[did] = {
          header: F.baseHeader(did, acc.id, '', { isDraft: true, customFolder: false, workingDirectory: 'C:\\Users\\Lin\\AppData\\Local\\Myriagent\\conversations\\' + did + '\\workspace' }),
          items: [], hasMore: false, composer: { attachments: [], busy: false, busyText: null, queue: [] },
        };
        this.sendConversation(did, m.paneId);
        break;
      }
      case 'loadOlder':
        if (c) {
          const older = c.older || [];
          c.items = older.concat(c.items);
          c.older = [];
          c.hasMore = false;
          setTimeout(() => this.send({ type: 'olderItems', conversationId: id, items: older, hasMore: false }), 120);
        }
        break;
      case 'send': {
        if (!c) break;
        if (c.header.running) {
          c.composer.queue.push({ id: 'qm' + (++this.seq), text: m.text }); // 不和測試資料的 q1、q2 撞號（宿主用 GUID，網頁依 id 保留每一列）
          this.pushComposer(id);
          break;
        }
        if (!m.text && c.composer.queue.length) {
          // 佇列暫停後的續送：從第一則接著送
          const first = c.composer.queue.shift();
          m = Object.assign({}, m, { text: first.text });
          this.pushComposer(id);
        }
        const n = ++this.seq;
        const user = { id: 'u' + n, kind: 'User', status: 'Done', time: Date.now(), html: '<p>' + esc(m.text) + '</p>', attachments: c.composer.attachments.slice() };
        c.items.push(user);
        c.composer.attachments = [];
        const wasDraft = c.header.isDraft;
        c.header = Object.assign({}, c.header, { running: true, status: '思考中…', startedAt: Date.now(), isDraft: false, title: c.header.title || m.text.slice(0, 24) });
        this.pushHeader(id); this.pushItem(id, user); this.pushComposer(id);
        if (wasDraft) {
          this.conversations.unshift({ id, accountId: c.header.accountId, provider: c.header.provider, title: c.header.title, group: '今天', updatedAt: Date.now(), workingDirectory: c.header.workingDirectory, running: true, waiting: false, unread: false });
          this.pushConversations();
        }
        const reply = { id: 'r' + n, kind: 'Assistant', status: 'Running', time: Date.now(), html: '<p>好的</p>' };
        setTimeout(() => { c.items.push(reply); this.pushItem(id, reply); }, 120);
        setTimeout(() => { reply.html = '<p>好的，我來處理「' + esc(m.text) + '」。</p>'; this.pushItem(id, reply); }, 260);
        setTimeout(() => {
          reply.status = 'Done';
          c.header = Object.assign({}, c.header, { running: false, status: null, startedAt: null, lastTurn: '輸入 1,024・輸出 128 tokens' });
          this.pushItem(id, reply); this.pushHeader(id);
          const s = this.conversations.find((x) => x.id === id);
          if (s) { s.running = false; this.pushConversations(); }
        }, 420);
        break;
      }
      case 'cancelQueued': if (c) { c.composer.queue = c.composer.queue.filter((q) => q.id !== m.id); this.pushComposer(id); } break;
      case 'stop':
        if (c) {
          c.header = Object.assign({}, c.header, { running: false, status: null, startedAt: null });
          const n = { id: 'n' + (++this.seq), kind: 'Notice', status: 'Done', time: Date.now(), html: '<p>已停止這一輪</p>' };
          c.items.push(n);
          this.pushHeader(id); this.pushItem(id, n);
        }
        break;
      case 'answer': { const it = item(m.itemId); if (it) { Object.assign(it, { status: 'Done', decision: 'answered', answers: m.answers }); this.pushItem(id, it); } break; }
      case 'skipQuestion': { const it = item(m.itemId); if (it) { Object.assign(it, { status: 'Declined', decision: 'skip' }); this.pushItem(id, it); } break; }
      // 同 AgentHub：拒絕／繼續規劃時附的說明存成 output（「你的說明：…」）
      case 'approve': {
        const it = item(m.itemId);
        if (it) {
          Object.assign(it, { status: m.choice === 'deny' ? 'Declined' : 'Done', decision: m.choice });
          if (m.choice === 'deny' && m.message) it.output = '你的說明：' + m.message;
          this.pushItem(id, it);
        }
        break;
      }
      case 'approvePlan': {
        const it = item(m.itemId);
        if (it) {
          Object.assign(it, { status: m.choice === 'keepPlanning' ? 'Declined' : 'Done', decision: m.choice });
          if (m.choice === 'keepPlanning' && m.message) it.output = '你的說明：' + m.message;
          this.pushItem(id, it);
        }
        break;
      }
      case 'rename':
        if (c) {
          c.header = Object.assign({}, c.header, { title: m.title });
          const s = this.conversations.find((x) => x.id === id);
          if (s) s.title = m.title;
          this.pushHeader(id); this.pushConversations();
        }
        break;
      case 'delete':
        this.conversations = this.conversations.filter((x) => x.id !== id);
        delete this.data[id];
        this.send({ type: 'conversationClosed', conversationId: id });
        this.pushConversations();
        break;
      case 'setOption':
        if (c) {
          const nh = Object.assign({}, c.header, { [m.key]: m.value || 'default' });
          if (m.key === 'model') {
            // 同 AgentHub.SetOption：新模型不支援目前的推理強度就改回預設
            const ms = nh.models || [];
            const cm = ms.find((x) => x.value === nh.model) || ms.find((x) => x.value === 'default') || ms[0];
            if (nh.effort !== 'default' && !(cm && (cm.efforts || []).includes(nh.effort))) nh.effort = 'default';
          }
          c.header = nh;
          this.pushHeader(id);
        }
        break;
      case 'setAccount':
        if (c) {
          // 執行中（同 AgentHub.SetAccount）：等這一輪結束才換的只記在標頭的 pendingAccount，選回目前的帳戶就取消
          if (c.header.running && !c.header.isDraft && (m.when === 'turnEnd' || m.accountId === c.header.accountId)) {
            const target = m.accountId === c.header.accountId ? null : this.accounts.find((a) => a.id === m.accountId);
            c.header = Object.assign({}, c.header, { pendingAccount: target ? { accountId: target.id, label: target.label, when: 'turnEnd' } : null });
            this.pushHeader(id);
            break;
          }
          // 這一輪結束了、背景工作還在跑（同 AgentHub.SetAccount，2026-10-27）：沒有明確的 now 就等背景工作結束才換，標頭的 pendingAccount 帶 background
          if (!c.header.running && !c.header.isDraft && c.header.background && (m.when !== 'now' || m.accountId === c.header.accountId)) {
            const target = m.accountId === c.header.accountId ? null : this.accounts.find((a) => a.id === m.accountId);
            c.header = Object.assign({}, c.header, { pendingAccount: target ? { accountId: target.id, label: target.label, when: 'turnEnd', background: true } : null });
            this.pushHeader(id);
            break;
          }
          const acc = this.accounts.find((a) => a.id === m.accountId);
          if (acc) {
            const nh = F.baseHeader(id, acc.id, c.header.title, { isDraft: c.header.isDraft, workingDirectory: c.header.workingDirectory, customFolder: c.header.customFolder });
            c.header = nh;
            const s = this.conversations.find((x) => x.id === id);
            if (s) { s.accountId = acc.id; s.provider = acc.provider; this.pushConversations(); }
            this.sendConversation(id);
          }
        }
        break;
      case 'chooseFolder': if (c) { c.header = Object.assign({}, c.header, { workingDirectory: 'D:\\work\\design-assets', customFolder: true }); this.pushHeader(id); } break;
      case 'setFolder': if (c) { c.header = Object.assign({}, c.header, { workingDirectory: m.path, customFolder: true }); this.pushHeader(id); } break;
      case 'resetFolder': if (c) { c.header = Object.assign({}, c.header, { workingDirectory: 'C:\\Users\\Lin\\AppData\\Local\\Myriagent\\conversations\\' + id + '\\workspace', customFolder: false }); this.pushHeader(id); } break;
      case 'listFiles': {
        const p = m.path || F.ROOT;
        const entries = this.files[p];
        setTimeout(() => this.send({
          type: 'files', requestId: m.requestId, conversationId: id, path: p, parent: F.parentOf(p), root: F.ROOT,
          entries: entries || [], truncated: false, error: entries ? null : '找不到這個資料夾，或沒有讀取權限',
        }), 60);
        break;
      }
      case 'fileSearch':
        setTimeout(() => this.send({ type: 'fileSearch', requestId: m.requestId, conversationId: id, query: m.query, results: F.searchFiles(this.files, m.query) }), this.fileSearchDelay);
        break;
      case 'searchConversations':
        setTimeout(() => this.send({ type: 'conversationSearch', requestId: m.requestId, query: m.query, hits: fakeSearch(this.conversations, m.query) }), this.searchDelay);
        break;
      case 'addFiles':
        if (c) {
          for (const f of m.__files || []) c.composer.attachments.push({ id: 'x' + (++this.seq), name: f.name, kind: 'Document', size: Math.max(1, Math.round(f.size / 1024)) + ' KB', path: 'C:\\Users\\Lin\\Downloads\\' + f.name, thumbUrl: null, url: null, videoInfo: null });
          this.pushComposer(id);
        }
        break;
      case 'pasteImage':
        if (c) {
          const p = 'C:\\Users\\Lin\\AppData\\Local\\Myriagent\\conversations\\' + id + '\\attachments\\' + (m.name || '貼上的圖片.png');
          c.composer.attachments.push({ id: 'x' + (++this.seq), name: m.name || '貼上的圖片.png', kind: 'Image', size: '64 KB', path: p, thumbUrl: F.thumb(p), url: F.media(p), videoInfo: null });
          this.pushComposer(id);
        }
        break;
      case 'addPathAttachment':
        if (c) {
          const name = m.path.split('\\').pop();
          const isImg = /\.(png|jpe?g|gif|webp)$/i.test(name), isVid = /\.mp4$/i.test(name);
          c.composer.attachments.push({ id: 'x' + (++this.seq), name, kind: isImg ? 'Image' : isVid ? 'Video' : 'Document', size: '120 KB', path: m.path,
            thumbUrl: isImg || isVid ? F.thumb(m.path) : null, url: isImg || (isVid && HAS_VIDEO) ? F.media(m.path) : null, videoInfo: isVid ? '0:03・6 張畫面' : null });
          this.pushComposer(id);
        }
        break;
      case 'removeAttachment': if (c) { c.composer.attachments = c.composer.attachments.filter((a) => a.id !== m.id); this.pushComposer(id); } break;
      case 'accountAdd': {
        const aid = 'a' + (10 + (++this.seq));
        this.accounts.push({ id: aid, provider: m.provider, label: m.label, color: m.color, isSystemDefault: false, configDirectory: 'C:\\Users\\Lin\\AppData\\Local\\Myriagent\\accounts\\' + aid,
          email: null, plan: null, status: 'notLoggedIn', statusText: '尚未登入', safeMode: false, loginBusy: true, usage: null, sessions: { open: 0, running: 0, waiting: 0 } });
        this.pushAccounts();
        this.send({ type: 'accountLogin', accountId: aid, message: m.provider === 'Codex' ? '已開啟瀏覽器，請完成 ChatGPT 登入…' : '已開啟登入視窗，請完成 Claude 登入…', done: false, success: null });
        break;
      }
      case 'accountLogin': {
        const a = this.accounts.find((x) => x.id === m.accountId);
        if (a) {
          a.loginBusy = true; this.pushAccounts();
          this.send({ type: 'accountLogin', accountId: a.id, message: '已開啟登入視窗，請完成登入…', done: false, success: null });
        }
        break;
      }
      case 'accountLoginCancel': {
        const a = this.accounts.find((x) => x.id === m.accountId);
        if (a && a.loginBusy) {
          a.loginBusy = false; this.pushAccounts();
          this.send({ type: 'accountLogin', accountId: a.id, message: '已取消登入', done: true, success: false });
        }
        break;
      }
      case 'accountLogout': { const a = this.accounts.find((x) => x.id === m.accountId); if (a) { Object.assign(a, { status: 'notLoggedIn', statusText: '尚未登入', usage: null }); this.pushAccounts(); } break; }
      case 'accountUpdate': {
        const a = this.accounts.find((x) => x.id === m.accountId);
        if (a) { for (const k of ['label', 'color', 'safeMode']) if (m[k] !== undefined) a[k] = m[k]; this.pushAccounts(); }
        break;
      }
      case 'accountRemove': this.accounts = this.accounts.filter((x) => x.id !== m.accountId); this.pushAccounts(); break;
      case 'accountReorder': { const map = new Map(this.accounts.map((a) => [a.id, a])); this.accounts = m.ids.map((x) => map.get(x)).filter(Boolean); this.pushAccounts(); break; }
      case 'accountRefresh': for (const a of this.accounts) if (a.usage && (!m.accountId || a.id === m.accountId)) a.usage.fetchedAt = Date.now(); this.pushAccounts(); break;
      case 'cliCheck': {
        // 和宿主一樣依序檢查每個 CLI、各自檢查完就送一次 cli：checkedAt 每次都更新（網頁等每個 checkedAt 都更新才算完成），
        // lastCheckedAt 只在查到最新版時更新；cliCheckFail 裡的 CLI 模擬查最新版失敗
        const checked = (p) => {
          const v = this.cli[p], t = Date.now();
          v.checkedAt = t;
          if (this.cliCheckFail.has(p)) v.latestError = '無法查詢最新版本：連線逾時';
          else Object.assign(v, { lastCheckedAt: t, latestError: null });
          this.send({ type: 'cli', cli: this.cli });
        };
        setTimeout(() => checked('Claude'), 300);
        setTimeout(() => checked('Antigravity'), 1200);
        setTimeout(() => checked('Grok'), 1800);
        setTimeout(() => checked('Codex'), 2500);
        break;
      }
      case 'cliUpdate':
      case 'cliInstall': {
        const v = this.cli[m.provider];
        if (v) {
          Object.assign(v, { busy: true, progress: '下載中 45%' });
          this.send({ type: 'cli', cli: this.cli });
          // 〔熱更新〕安裝不一致時的「重新安裝」也走 cliInstall：裝好就不再不一致
          setTimeout(() => { const t = Date.now(); Object.assign(v, { busy: false, progress: null, installed: true, version: v.latest, updateAvailable: false, lastCheckedAt: t, checkedAt: t, installBroken: null }); this.send({ type: 'cli', cli: this.cli }); }, 700);
        }
        break;
      }
      // 〔熱更新〕同 AgentHub 的重開協調（簡化）：conversationId＝這個對話立刻以新版重開並接續；provider＝排空中就是「現在中斷」，
      // 否則所有待重開的對話立刻重開
      case 'cliApplyNow': {
        if (m.conversationId) {
          const cc = this.conv(m.conversationId);
          if (cc && cc.header.restart && cc.header.restart.canApplyNow) {
            cc.header = Object.assign({}, cc.header, { running: true, restart: F.restartHeader('restarting'), status: '正在以新版重新啟動…' });
            this.pushHeader(m.conversationId);
            setTimeout(() => {
              cc.header = Object.assign({}, cc.header, { restart: F.restartHeader('continued', { detail: '已用新版 2.1.297 接續（立即套用：中斷時正在執行 dotnet test，結果未知）' }), status: '已用新版自動接續…' });
              const n = { id: 'hn' + (++this.seq), seq: 1000 + this.seq, kind: 'Notice', noticeKind: 'cliUpdate', status: 'Done', time: Date.now(), html: '<p>已用 Claude Code 2.1.297 接續（立即套用：中斷時正在執行 <code>dotnet test</code>，結果未知）</p>' };
              cc.items.push(n);
              this.pushHeader(m.conversationId); this.pushItem(m.conversationId, n);
            }, 400);
          }
        } else if (this.cli[m.provider]) {
          const v = this.cli[m.provider];
          if (v.update && v.update.phase === 'draining') v.update = Object.assign({}, v.update, { phase: 'waitingProcesses', held: (v.update.held || 0) + (v.update.waiting || 0), waiting: 0, overdue: false });
          else v.restartWaiting = (v.restartWaiting || []).map((w) => Object.assign({}, w, { state: 'restarting' }));
          this.send({ type: 'cli', cli: this.cli });
        }
        break;
      }
      // 〔熱更新〕「繼續」：送出接續訊息（不顯示成使用者訊息），對話加一則 cliUpdate 的 Notice
      case 'cliContinue': {
        const cc = this.conv(m.conversationId);
        if (cc && cc.header.restart && cc.header.restart.canContinue) {
          cc.header = Object.assign({}, cc.header, { restart: null, running: true, status: '思考中…', startedAt: Date.now() });
          const n = { id: 'hn' + (++this.seq), seq: 1000 + this.seq, kind: 'Notice', noticeKind: 'cliUpdate', status: 'Done', time: Date.now(), html: '<p>已請代理從中斷處繼續（CLI 更新後的接續訊息，不是你送的）</p>' };
          cc.items.push(n);
          this.pushHeader(m.conversationId); this.pushItem(m.conversationId, n);
        }
        break;
      }
      // 〔熱更新〕排空中的「取消更新」：不更新了，已停下的對話以目前的版本重開
      case 'cliCancelUpdate': {
        const v = this.cli[m.provider];
        if (v && v.update && v.update.phase === 'draining') {
          Object.assign(v, { update: null, busy: false, progress: null, restartWaiting: (v.restartWaiting || []).map((w) => Object.assign({}, w, { state: 'restarting' })) });
          this.send({ type: 'cli', cli: this.cli });
        }
        break;
      }
      case 'setting': this.settings[m.key] = m.value; this.send({ type: 'settings', settings: this.settings }); break;
      // 「最近的指令」的歷史指令：這個對話的使用者訊息（最新的在前），再加一則只有宿主有的較早指令（不在已載入的那一頁）
      case 'promptHistory': {
        const cc = this.conv(m.conversationId);
        const plain = (html) => String(html || '').replace(/<br\s*\/?>/g, '\n').replace(/<[^>]+>/g, '');
        const prompts = cc ? cc.items.filter((x) => x.kind === 'User').reverse().map((x) => ({ id: x.id, seq: x.seq || 0, time: x.time, text: plain(x.html), attachments: (x.attachments || []).length })) : [];
        prompts.push({ id: 'old-p1', seq: 1, time: NOW - 3 * 86400000, text: '較早一頁裡的指令（宿主才有）\n第二行', attachments: 0 });
        setTimeout(() => this.send({ type: 'promptHistory', conversationId: m.conversationId, requestId: m.requestId, prompts }), 30);
        break;
      }
      case 'setAgent': if (c) { c.header = Object.assign({}, c.header, { agent: m.agent || null }); this.pushHeader(id); } break;
      case 'setOutputStyle': if (c) { c.header = Object.assign({}, c.header, { outputStyle: m.style || null }); this.pushHeader(id); } break;
      case 'forkConversation': {
        // 同 AgentHub.Fork.cs：複製項目（新的 id、執行中的算已中斷）、最後加 noticeKind fork 的提示，回 conversation（帶網頁給的 paneId）＋focusConversation；
        // opts.forkFail：模擬複製失敗（只送 toast）
        if (!c || c.header.isDraft) { this.send({ type: 'toast', text: '還沒送出過訊息的對話沒有內容可以延伸', isError: false }); break; }
        if (this.opts.forkFail) { this.send({ type: 'toast', text: '延伸失敗：磁碟已滿', isError: true }); break; }
        const fid = 'f' + (++this.seq);
        const title = (c.header.title || '對話') + '（延伸）';
        const items = c.items.map((x) => Object.assign({}, x, { id: fid + '-' + x.id }, x.status === 'Running' || x.status === 'Waiting' ? { status: 'Cancelled' } : {}));
        items.push({ id: fid + '-n', kind: 'Notice', status: 'Done', time: Date.now(), noticeKind: 'fork',
          html: '<p>從「' + esc(c.header.title || '') + '」延伸出來的新對話：上面是複製過來的內容，接下來由新的代理接手，和原對話各自進行。新的代理帶著原對話的完整紀錄（含工具結果）。</p>' });
        this.data[fid] = {
          header: Object.assign({}, c.header, { id: fid, title, running: false, status: null, startedAt: null, isDraft: false, lastTurn: null }),
          items, hasMore: false, composer: { attachments: [], busy: false, busyText: null, queue: [] },
        };
        const src = this.conversations.find((x) => x.id === id) || {};
        this.conversations.unshift(Object.assign({}, src, { id: fid, title, updatedAt: Date.now(), running: false, waiting: false, unread: false, forkedFrom: id, forkedFromTitle: c.header.title || '' }));
        this.pushConversations();
        this.sendConversation(fid, m.paneId);
        this.send({ type: 'focusConversation', conversationId: fid });
        break;
      }
      case 'listExternalConversations':
        setTimeout(() => this.send({
          type: 'externalConversations', requestId: m.requestId,
          items: [
            { sourceKey: 'sk-agy-1', provider: 'Antigravity', sessionId: 'ext-agy-1', accountId: 'a3', accountLabel: 'Antigravity', title: '重構狀態機架構', workingDirectory: 'C:\\repo\\alpha', updatedAt: NOW - 180000, messageCount: 12 },
            { sourceKey: 'sk-claude-1', provider: 'Claude', sessionId: 'ext-claude-1', accountId: 'a1', accountLabel: '工作訂閱', title: '修正登入憑證檢查', workingDirectory: 'C:\\repo\\beta', updatedAt: NOW - 600000, messageCount: 8 },
          ],
        }), 30);
        break;
      case 'importExternalConversation': {
        const nid = 'imp1';
        this.conversations.unshift({ id: nid, accountId: 'a3', provider: 'Antigravity', title: '重構狀態機架構', group: '今天', updatedAt: Date.now(), workingDirectory: 'C:\\repo\\alpha', running: false, waiting: false, unread: false });
        this.data[nid] = {
          header: F.baseHeader(nid, 'a3', '重構狀態機架構', { isDraft: false, sessionStarted: true, customFolder: true, workingDirectory: 'C:\\repo\\alpha' }),
          items: [{ id: 'imp-u1', kind: 'User', status: 'Done', time: NOW - 180000, html: '<p>請幫我重構狀態機架構</p>', attachments: [] }],
          hasMore: false, composer: { attachments: [], busy: false, busyText: null, queue: [] },
        };
        this.pushConversations();
        this.sendConversation(nid, m.paneId || null);
        this.send({ type: 'focusConversation', conversationId: nid });
        break;
      }
      case 'setConversationDefault': {
        const p = this.settings.conversationDefaults && this.settings.conversationDefaults[m.provider];
        if (!p) break;
        if (m.key === 'model') p.model = m.value || null;
        else if (m.key === 'permission') p.permission = m.value;
        else if (m.key === 'effort') { const row = p.models.find((x) => x.value === m.model); if (row) row.effort = m.value === 'default' ? null : m.value; }
        else if (m.key === 'autoCompact') { // 同宿主：夾進範圍（解析在網頁已做過，這裡只收數字或空字串）
          if (!p.autoCompactRange) break;
          const n = m.value ? Number(m.value) : null;
          p.autoCompact = n == null ? null : Math.min(p.autoCompactRange.max, Math.max(p.autoCompactRange.min, n));
        } else if (m.key === 'autoCompactPercent') {
          if (!p.autoCompactPercentRange) break;
          const n = m.value ? Number(m.value) : null;
          p.autoCompactPercent = n == null ? null : Math.min(p.autoCompactPercentRange.max, Math.max(p.autoCompactPercentRange.min, n));
        }
        this.send({ type: 'settings', settings: this.settings });
        break;
      }
      default: if (this.onCmd) this.onCmd(m, this); break; // openLink／openPath／revealPath／copyText／copyItem／popout／openFolder／attach／accountOpenFolder／layout／close／visible：只記錄（〔同步〕onCmd 由 hostSetup 掛上）
    }
  }

  last(cmd, pred = () => true) {
    for (let i = this.received.length - 1; i >= 0; i--) if (this.received[i].cmd === cmd && pred(this.received[i])) return this.received[i];
    return null;
  }
}

// ───── 共用 ─────
const report = {
  generatedAt: new Date().toISOString(), chrome: null, hasVideo: HAS_VIDEO, webDir: WEB, outDir: OUT,
  checks: {}, details: {}, screenshots: [], consoleErrors: [], xssPhaseErrors: [], notes: [],
};
const check = (name, ok, detail) => { report.checks[name] = !!ok; if (detail !== undefined) report.details[name] = detail; };

async function until(fn, ms = 4000, step = 40) {
  const t0 = Date.now();
  while (Date.now() - t0 < ms) {
    const v = await fn();
    if (v) return v;
    await sleep(step);
  }
  return null;
}

async function settle(page, ms = 250) {
  await page.evaluate(() => document.fonts && document.fonts.ready).catch(() => {});
  await page.evaluate(() => Promise.all(Array.from(document.images)
    .filter((i) => !i.complete && i.getBoundingClientRect().width > 0)
    .map((i) => new Promise((r) => { i.addEventListener('load', r, { once: true }); i.addEventListener('error', r, { once: true }); setTimeout(r, 2500); })))).catch(() => {});
  await sleep(ms);
}

async function shot(page, name) {
  await settle(page);
  const file = name + '.png';
  await page.screenshot({ path: path.join(OUT, file) });
  report.screenshots.push(file);
}

let browser;
async function openApp(name, o) {
  const log = { errors: [], xss: [], phase: 'normal' };
  const context = await browser.newContext({ viewport: { width: o.vp.w, height: o.vp.h }, deviceScaleFactor: 1, colorScheme: o.colorScheme || 'dark', locale: 'zh-TW',
    ...(o.reducedMotion ? { reducedMotion: o.reducedMotion } : {}) });
  const page = await context.newPage();
  const host = new FakeHost(o, log);
  if (o.hostSetup) o.hostSetup(host); // 網頁載入前調整假宿主的資料（init 就帶著）
  host.attach(page);
  page.on('console', (m) => { if (m.type() === 'error') (log.phase === 'xss' ? log.xss : log.errors).push(`[${name}] ${m.text()}`); });
  page.on('pageerror', (e) => log.errors.push(`[${name}] pageerror ${e.message}`));
  await page.addInitScript(MOCK);
  await page.addInitScript(fakeMic); // 輸入區的語音訊息：假的麥克風與錄音器（不碰這台真的麥克風）
  if (o.initScript) await page.addInitScript(o.initScript);
  await page.exposeFunction('__hostRecv', (msg) => host.recv(msg));
  await page.route(ORIGIN + '/**', (route) => serve(route, o));
  await page.goto(o.url || `${ORIGIN}/index.html`);
  await page.waitForFunction(() => document.getElementById('app').classList.contains('ready'), null, { timeout: 10000 });
  return { name, context, page, host, log };
}

async function closeApp(app) {
  report.consoleErrors.push(...app.log.errors);
  report.xssPhaseErrors.push(...app.log.xss);
  await app.context.close().catch(() => {});
}

const convSel = (id) => `.conv[data-conv="${id}"]`;
async function waitConv(page, id) {
  await page.waitForSelector(`${convSel(id)}:not(.loading) .items`, { state: 'attached', timeout: 8000 });
}
async function scrollItem(page, conv, itemId, block = 'start') {
  await page.evaluate(([c, i, b]) => {
    const el = document.querySelector(`.conv[data-conv="${c}"] .it[data-id="${i}"]`);
    if (el) el.scrollIntoView({ block: b });
  }, [conv, itemId, block]);
  await sleep(150);
}
const visible = (page, sel) => page.evaluate((s) => {
  const el = document.querySelector(s);
  if (!el) return false;
  const cs = getComputedStyle(el);
  const r = el.getBoundingClientRect();
  return cs.display !== 'none' && cs.visibility !== 'hidden' && r.width > 0 && r.height > 0;
}, sel);

/** 訊息後方的時間（一直看得到）與對話上方常駐的「最近的指令」（c1：已載入的最後一則使用者訊息是 i1） */
async function lastPromptChecks(page, tag, host) {
  const times = await page.evaluate(() => {
    const t = (sel) => {
      const el = document.querySelector(sel);
      if (!el) return null;
      const r = el.getBoundingClientRect();
      return { text: el.textContent, title: el.title, visible: r.height > 0 && getComputedStyle(el).opacity === '1' && getComputedStyle(el).display !== 'none' };
    };
    return { user: t('.conv[data-conv="c1"] .it[data-id="i1"] .it-time'), assistant: t('.conv[data-conv="c1"] .it-assistant .it-time'), remote: t('.conv[data-conv="c1"] .it[data-id="o5"] .u-meta .it-time') };
  });
  const timeRe = /^(\d\d:\d\d|昨天 \d\d:\d\d|\d{1,2}\/\d\d \d\d:\d\d|\d{4}\/\d{1,2}\/\d\d \d\d:\d\d)$/;
  const fullRe = /^\d{4}\/\d\d\/\d\d \d\d:\d\d:\d\d$/;
  check('messageTimes', [times.user, times.assistant, times.remote].every((x) => !!x && timeRe.test(x.text) && fullRe.test(x.title) && x.visible), times);
  const bar = () => page.evaluate(() => {
    const root = document.querySelector('.conv[data-conv="c1"]');
    const el = root.querySelector('.lastprompt');
    const sc = root.querySelector('.scroller').getBoundingClientRect();
    const r = el.getBoundingClientRect();
    const lt = el.querySelector('.lp-text');
    return { hidden: el.hidden, open: el.classList.contains('open'), oneLine: getComputedStyle(lt).whiteSpace === 'nowrap', text: el.querySelector('.lp-body').textContent, time: el.querySelector('.lp-time').textContent,
      above: r.bottom <= sc.top + 1, height: Math.round(r.height), itemTime: root.querySelector('.it[data-id="i1"] .it-time').textContent };
  });
  const b1 = await bar();
  check('lastPromptBar', !b1.hidden && b1.text.startsWith('最近的指令帳戶沒有登入時') && b1.time === b1.itemTime && b1.above && b1.height < 70, b1);
  // 點一下展開全文、再點收合；箭頭捲到那則（停止貼底、出現「最新」）
  await page.click(`${convSel('c1')} .lp-body`);
  const b2 = await bar();
  await shot(page, `chat-lastprompt-open-${tag}`);
  await page.click(`${convSel('c1')} .lp-body`);
  const b3 = await bar();
  await page.click(`${convSel('c1')} .lastprompt .ibtn`);
  await sleep(250);
  const jumped = await page.evaluate(() => {
    const root = document.querySelector('.conv[data-conv="c1"]');
    const sc = root.querySelector('.scroller').getBoundingClientRect();
    const r = root.querySelector('.it[data-id="i1"]').getBoundingClientRect();
    return { offset: Math.round(r.top - sc.top), jumpBtn: !root.querySelector('.jump').hidden, flash: root.querySelector('.it[data-id="i1"]').classList.contains('lp-flash') };
  });
  await shot(page, `chat-lastprompt-jump-${tag}`);
  check('lastPromptToggleAndJump', b1.oneLine && b2.open && !b2.oneLine && !b3.open && b3.oneLine && b3.height === b1.height && jumped.offset >= 0 && jumped.offset < 40 && jumped.jumpBtn && jumped.flash,
    { b2, b3, jumped });
  // 只顯示一行（2026-10-25 使用者要求）：放不下時跑馬燈（同標題的關鍵影格），展開全文時不捲
  const mq = () => page.evaluate(() => {
    const el = document.querySelector('.conv[data-conv="c1"] .lastprompt');
    const box = el.querySelector('.lp-text');
    const run = el.querySelector('.lp-run');
    return { over: run.offsetWidth - box.clientWidth, marquee: box.classList.contains('marquee'), anim: getComputedStyle(run).animationName,
      lines: Math.round(box.getBoundingClientRect().height / parseFloat(getComputedStyle(box).lineHeight)), label: el.querySelector('.lp-label').getBoundingClientRect().width > 0 };
  });
  const m1 = await mq();
  await page.click(`${convSel('c1')} .lp-body`);
  const m2 = await mq();
  await page.click(`${convSel('c1')} .lp-body`);
  check('lastPromptOneLineMarquee', m1.lines === 1 && m1.over > 1 && m1.marquee && m1.anim === 'ch-marquee' && m1.label && !m2.marquee && m2.anim === 'none' && (await mq()).marquee, { m1, m2 });
  // 行尾的「歷史指令」：問宿主、回來才開清單（最新的在前、只有宿主有的較早指令也在），點一筆換進輸入框（Ctrl+Z 還原原本打的字）
  const ta = `${convSel('c1')} .cinput`;
  await page.fill(ta, '原本打到一半的字');
  await page.click(`${convSel('c1')} .lp-hist`);
  const req = await until(() => host.last('promptHistory', (m) => m.conversationId === 'c1' && typeof m.requestId === 'string'), 2000);
  const menu = await until(() => page.evaluate(() => {
    const m = document.querySelector('.menu.menu-prompt-history');
    return m ? { head: (m.querySelector('.menu-head') || {}).textContent || '', items: Array.from(m.querySelectorAll('.menu-item')).map((i) => [i.querySelector('.mi-label').textContent, (i.querySelector('.mi-desc') || {}).textContent || '']) } : null;
  }), 2000);
  await settle(page, 300);
  await shot(page, `chat-lastprompt-history-${tag}`);
  const latest = b1.text.replace(/^最近的指令/, '');
  await page.click('.menu-prompt-history .menu-item:last-of-type');
  await settle(page, 150);
  const used = await page.evaluate((s) => ({ value: document.querySelector(s).value, focused: document.activeElement === document.querySelector(s) }), ta);
  await page.keyboard.press('Control+z');
  await settle(page, 100);
  const undone = await page.evaluate((s) => document.querySelector(s).value, ta);
  await page.fill(ta, '');
  check('lastPromptHistory', !!req && !!menu && /^歷史指令（最近 \d+ 筆/.test(menu.head) && menu.items.length >= 2 && menu.items[0][0] === latest
    && menu.items[menu.items.length - 1][0] === '較早一頁裡的指令（宿主才有） 第二行' && /^\d{1,2}\/\d\d \d\d:\d\d$|^\d{4}\//.test(menu.items[menu.items.length - 1][1])
    && used.value === '較早一頁裡的指令（宿主才有）\n第二行' && used.focused && undone === '原本打到一半的字', { req, menu, used, undone });
  // 回到最底（後面的步驟照舊）
  await page.click(`${convSel('c1')} .jump`);
  await settle(page, 200);
}

const MAIN_LAYOUT = { panes: [{ id: 'p1', tabs: ['c1', 'gone1', 'c2'], active: 'c1' }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' };

// ───── 主視窗流程 ─────
async function runMain(vp, full) {
  const tag = String(vp.w);
  const app = await openApp('main-' + tag, { vp, layout: MAIN_LAYOUT });
  const { page, host } = app;
  await waitConv(page, 'c1');
  await settle(page, 400);

  if (full) {
    const first = host.received[0];
    check('readySent', first && first.cmd === 'ready' && first.mode === 'main', first);
    const opens = host.received.filter((m) => m.cmd === 'open').map((m) => m.conversationId);
    // 桌面還原版面只 open 作用中的分頁（c1）；背景分頁（c2）切過去才 open，已不存在的（gone1）一律不開
    check('restoreOpensValidTabs', opens.includes('c1') && !opens.includes('c2') && !opens.includes('gone1'), opens);
    await page.click('.tab[data-key="c2"]');
    const deferred = await until(() => host.received.some((m) => m.cmd === 'open' && m.conversationId === 'c2'));
    await waitConv(page, 'c2');
    check('restoreDefersBackgroundTabs', !!deferred && host.received.filter((m) => m.cmd === 'open' && m.conversationId === 'c2').length === 1,
      host.received.filter((m) => m.cmd === 'open').map((m) => m.conversationId));
    await page.click('.tab[data-key="c1"]');
    await settle(page, 200);
  }

  // 單窗格：最底（執行中、待辦面板、等待作答的卡片）
  await shot(page, `chat-single-bottom-${tag}`);
  // 卡片區
  await scrollItem(page, 'c1', 'i18');
  await shot(page, `chat-single-cards-${tag}`);
  // 工具列（展開差異）
  await page.click(`${convSel('c1')} .it[data-id="i6"] .t-row`);
  await page.click(`${convSel('c1')} .it[data-id="i7"] .t-row`);
  await scrollItem(page, 'c1', 'i3');
  await shot(page, `chat-single-tools-${tag}`);
  // Markdown 與媒體
  await scrollItem(page, 'c1', 'i12');
  await shot(page, `chat-single-markdown-${tag}`);
  await scrollItem(page, 'c1', 'i15', 'center');
  await shot(page, `chat-single-media-${tag}`);

  // 捲到頂：自動載入較早的訊息
  await page.evaluate((c) => { const sc = document.querySelector(`.conv[data-conv="${c}"] .scroller`); sc.scrollTop = 0; }, 'c1');
  const older = await until(() => host.last('loadOlder', (m) => m.conversationId === 'c1'), 3000);
  await until(() => page.$(`${convSel('c1')} .it[data-id="o1"]`), 2000);
  await page.evaluate((c) => { document.querySelector(`.conv[data-conv="${c}"] .scroller`).scrollTop = 0; }, 'c1');
  await shot(page, `chat-single-top-${tag}`);
  if (full) {
    const kept = await page.evaluate(() => !!document.querySelector('.conv[data-conv="c1"] .it[data-id="o1"]') && !!document.querySelector('.conv[data-conv="c1"] .it[data-id="i1"]'));
    check('loadOlderOnScrollTop', !!older && older.beforeId === 'i1' && kept, older);
    // 從 claude.ai 或 Claude App 送來的訊息：氣泡下一直看得到來源（和時間同一行）；在 app 送出的沒有
    const src = await page.evaluate(() => {
      const label = (id) => document.querySelector(`.conv[data-conv="c1"] .it[data-id="${id}"] .u-src`);
      const remote = label('o5');
      const box = remote && remote.getBoundingClientRect();
      return { remote: remote && remote.textContent, visible: !!box && box.height > 0 && getComputedStyle(remote).opacity !== '0', local: !!label('o1') };
    });
    check('remoteUserSource', src.remote === '從 claude.ai／Claude App 送出' && src.visible && !src.local, src);
    await lastPromptChecks(page, tag, host);
  }

  const ta = `${convSel('c1')} .cinput`;

  // @ 檔案提示
  await page.click(ta);
  await page.keyboard.type('請檢查 @app');
  const fsMsg = await until(() => host.last('fileSearch', (m) => m.query === 'app'), 3000);
  await until(() => page.$(`${convSel('c1')} .suggest:not([hidden]) .sg-row`), 2000);
  await shot(page, `mention-menu-${tag}`);
  await page.keyboard.press('Enter');
  const taVal = await page.inputValue(ta);
  if (full) check('atSendsFileSearch', !!fsMsg && fsMsg.conversationId === 'c1' && /@src\\Myriagent\\Web\\app\.(js|css) $/.test(taVal), { fileSearch: fsMsg, textarea: taVal });
  await page.fill(ta, '');

  // @ AI 帳戶（帳戶委派，docs/account-delegation.md）：header.accountDelegation 為 true 時，其他已登入的本機帳戶列在檔案前面，選取插入以 id 指向的提及
  host.conv('c1').header.accountDelegation = true;
  host.pushHeader('c1');
  // 第九批：Codex Pro 有 2 個對話在跑（選單右邊寫執行中的對話數；用量只有每週一條）
  const a3 = host.accounts.find((x) => x.id === 'a3');
  const a3Sessions = a3.sessions;
  a3.sessions = Object.assign({}, a3Sessions, { running: 2 });
  host.pushAccounts();
  await host.chain;
  await page.keyboard.type('@codex');
  await until(() => page.$(`${convSel('c1')} .suggest:not([hidden]) .sg-row`), 2000);
  await until(() => host.last('fileSearch', (m) => m.query === 'codex'), 3000);
  await sleep(120);
  await shot(page, `mention-account-${tag}`);
  const acctRows = await page.$$eval(`${convSel('c1')} .suggest .sg-row`, (n) => n.map((x) => x.textContent));
  // 第九批：帳戶色方塊、右邊的用量刻度（每週 18%＝1 格亮）與執行中的對話數
  const acctMeta = await page.evaluate((s) => {
    const row = document.querySelector(s + ' .suggest .sg-row');
    const dot = row.querySelector('.sg-acc-dot');
    const meta = row.querySelector('.sg-acc-meta');
    return { radius: getComputedStyle(dot).borderTopLeftRadius, meters: meta ? meta.querySelectorAll('.ci-meter').length : 0,
      lit: meta ? meta.querySelectorAll('.ci-meter > i.lit').length : 0, run: meta && meta.querySelector('.sg-acc-run') ? meta.querySelector('.sg-acc-run').textContent : null,
      title: meta ? meta.title : null, right: meta ? Math.round(row.getBoundingClientRect().right - meta.getBoundingClientRect().right) : null };
  }, convSel('c1'));
  await page.keyboard.press('Enter');
  const acctVal = await page.inputValue(ta);
  if (full) check('atListsAccountsBeforeFiles', /Codex Pro/.test(acctRows[0] || '') && !acctRows.some((r) => /Codex 團隊|工作用 Max/.test(r)) && acctVal === '@[Codex Pro](account:a3) ',
    { rows: acctRows.slice(0, 4), textarea: acctVal });
  if (full) check('atAccountMeta', acctMeta.radius === '2px' && acctMeta.meters === 1 && acctMeta.lit === 1 && acctMeta.run === '2' && acctMeta.title === '每週 18%\n2 個對話執行中'
    && acctMeta.right >= 0 && acctMeta.right <= 12, acctMeta);
  await page.fill(ta, '');
  host.conv('c1').header.accountDelegation = false;
  host.pushHeader('c1');
  a3.sessions = a3Sessions;
  host.pushAccounts();
  await host.chain;

  // 宿主的標頭更新在清單沒變時不帶指令與模型清單（HeaderListRules）：網頁沿用上一份，下面的「/」選單照樣列出指令
  let slimSent = false;
  if (full) {
    const slim = Object.assign({}, host.conv('c1').header);
    delete slim.commands;
    delete slim.models;
    await host.send({ type: 'header', conversationId: 'c1', header: slim });
    await sleep(200);
    slimSent = true;
  }

  // / 指令提示
  await page.keyboard.type('/');
  await until(() => page.$(`${convSel('c1')} .suggest[data-type="cmd"]:not([hidden]) .sg-row`), 2000);
  await shot(page, `slash-menu-${tag}`);
  await page.keyboard.type('re');
  const cmdRows = await page.$$eval(`${convSel('c1')} .suggest .sg-cmd`, (n) => n.map((x) => x.textContent));
  if (full) check('slashCommandsFilter', cmdRows.length > 0 && cmdRows[0] === '/review', cmdRows);
  if (full) check('headerSlimKeepsCommands', slimSent && cmdRows.includes('/review'), { slimSent, cmdRows });
  await page.keyboard.press('Escape');
  await page.fill(ta, '');

  // Shift+Tab 切權限模式
  await page.focus(ta);
  await page.keyboard.press('Shift+Tab');
  const perm1 = await until(() => host.last('setOption', (m) => m.key === 'permission'), 2000);
  await sleep(150);
  if (full) {
    const tone = await page.getAttribute(`${convSel('c1')} .perm`, 'data-tone');
    check('shiftTabCyclesPermission', !!perm1 && perm1.value === 'AcceptEdits' && perm1.conversationId === 'c1' && tone === 'edit', { setOption: perm1, tone });
  }

  // 權限模式選單＋完全自動確認
  await page.click(`${convSel('c1')} .perm`);
  await page.waitForSelector('.menu.menu-perm');
  await shot(page, `permission-menu-${tag}`);
  await page.click('.menu.menu-perm .menu-item:has(.mi-label:text-is("完全自動"))');
  await page.waitForSelector('.dlg-confirm');
  await shot(page, `fullaccess-confirm-${tag}`);
  await page.click('.dlg-confirm .btn.danger-solid');
  const permFull = await until(() => host.last('setOption', (m) => m.key === 'permission' && m.value === 'FullAccess'), 2000);
  if (full) check('fullAccessNeedsConfirm', !!permFull, permFull);
  await sleep(200);
  await page.click(`${convSel('c1')} .perm`);
  await page.click('.menu.menu-perm .menu-item:has(.mi-label:text-is("每步詢問"))');
  await sleep(150);

  // 輸入紀錄（historyrules.js）每個對話一份：先放別的對話的紀錄與舊版共用的一份，c1 按 ↑ 不能翻到它們
  if (full) {
    await page.evaluate(() => {
      localStorage.setItem('myriagent.app.inputHistory.v1', JSON.stringify(['舊版共用的紀錄']));
      localStorage.setItem('myriagent.app.inputHistory.v2', JSON.stringify([{ id: 'c2', list: ['別的對話送出的訊息'] }]));
    });
  }

  // Enter 送出（執行中＝排隊）
  await page.fill(ta, '請把測試結果整理成表格');
  await page.focus(ta);
  await page.keyboard.press('Enter');
  const sent = await until(() => host.last('send', (m) => m.conversationId === 'c1'), 2000);
  const queued = await until(() => page.evaluate(() => document.querySelectorAll('.conv[data-conv="c1"] .queue-row').length === 3), 2000);
  if (full) {
    const cleared = (await page.inputValue(ta)) === '';
    check('enterSends', !!sent && sent.text === '請把測試結果整理成表格' && cleared && !!queued, { send: sent, queuedRows3: !!queued });
  }

  // ↑↓ 帶入這個對話最近送出的訊息（historyrules.js）：空的輸入框直接翻；有草稿時第一下 ↑ 先把游標移到最前面，再按才翻，↓ 到底還原草稿
  if (full) {
    const sendsBefore = host.received.filter((m) => m.cmd === 'send').length;
    const keys = async (k) => { await page.keyboard.press(k); return page.inputValue(ta); };
    await page.focus(ta);
    const up1 = await keys('ArrowUp');
    const caret1 = await page.$eval(ta, (t) => [t.selectionStart, t.value.length]);
    const upOnly = await keys('ArrowUp'); // c1 只送過這一則：別的對話與舊版共用的紀錄不會翻出來（照預設把游標移到最前面）
    await page.keyboard.press('End'); // 游標放回最後（帶入時的位置），↓ 才會還原
    const down1 = await keys('ArrowDown');
    await page.keyboard.type('草稿');
    const up2 = await keys('ArrowUp');
    const up3 = await keys('ArrowUp');
    const down2 = await keys('ArrowDown');
    const sendsAfter = host.received.filter((m) => m.cmd === 'send').length;
    const stored = await page.evaluate(() => ({ legacy: localStorage.getItem('myriagent.app.inputHistory.v1'), v2: JSON.parse(localStorage.getItem('myriagent.app.inputHistory.v2') || 'null') }));
    await page.fill(ta, '');
    check('arrowRecallsHistory', up1 === '請把測試結果整理成表格' && caret1[0] === caret1[1] && upOnly === up1 && down1 === '' && up2 === '草稿' && up3 === '請把測試結果整理成表格'
      && down2 === '草稿' && sendsAfter === sendsBefore
      && stored.legacy === null && JSON.stringify(stored.v2) === JSON.stringify([{ id: 'c1', list: ['請把測試結果整理成表格'] }, { id: 'c2', list: ['別的對話送出的訊息'] }]),
    { up1, caret1, upOnly, down1, up2, up3, down2, sendsBefore, sendsAfter, stored });
  }

  // 網址交給宿主（openLink）；本機檔案連結開「下載或開啟檔案」框，「用預設程式開啟」送 openPath（另存新檔見 filelinks-probe.mjs）
  await scrollItem(page, 'c1', 'i12');
  await page.click(`${convSel('c1')} .it[data-id="i12"] a[href^="https://learn"]`);
  const fileHref = await page.getAttribute(`${convSel('c1')} .it[data-id="i12"] a[href*="/open?path="]`, 'href');
  await page.click(`${convSel('c1')} .it[data-id="i12"] a[href*="/open?path="]`);
  const fileDlg = await page.waitForSelector('.dlg-local-file', { timeout: 2000 }).then(() => true, () => false);
  if (fileDlg) await page.click('.dlg-local-file [data-field="localFileOpen"]');
  const openedPath = await until(() => host.last('openPath', (m) => m.conversationId === 'c1'), 2000);
  await page.waitForSelector('.dlg-local-file', { state: 'detached', timeout: 2000 }).catch(() => {});
  if (full) {
    const links = host.received.filter((m) => m.cmd === 'openLink').map((m) => m.href);
    const expectedPath = fileHref ? new URL(fileHref).searchParams.get('path') : null;
    check('linksRouted', links.some((x) => x.startsWith('https://learn.microsoft.com/')) && !links.some((x) => x.includes('/open?path='))
      && fileDlg && !!openedPath && openedPath.path === expectedPath && page.url().startsWith(ORIGIN + '/index.html'), { links, fileDlg, openPath: openedPath });
  }

  // 燈箱：對話內所有圖片，左右切換、放大
  await scrollItem(page, 'c1', 'i15', 'center');
  await page.click(`${convSel('c1')} .it[data-id="i15"] .mimg >> nth=0`);
  await page.waitForSelector('.lb .lb-img');
  await until(() => page.evaluate(() => { const i = document.querySelector('.lb .lb-img'); return i && i.complete && i.naturalWidth > 0; }), 3000);
  const c0 = await page.textContent('.lb .lb-count');
  await page.keyboard.press('ArrowRight');
  await sleep(250);
  const c1t = await page.textContent('.lb .lb-count');
  await page.keyboard.press('ArrowLeft');
  await sleep(250);
  const c2t = await page.textContent('.lb .lb-count');
  const box = await page.locator('.lb .lb-stage').boundingBox();
  await page.mouse.move(box.x + box.width * 0.45, box.y + box.height * 0.45);
  for (let i = 0; i < 4; i++) { await page.mouse.wheel(0, -120); await sleep(60); }
  await sleep(250);
  const zoom = await page.textContent('.lb .lb-zoom');
  await shot(page, `lightbox-zoomed-${tag}`);
  if (full) check('lightboxArrowKeys', c0 !== c1t && c0 === c2t && /第 \d+／\d+ 張/.test(c0) && parseInt(zoom, 10) > 100, { before: c0, afterRight: c1t, afterLeft: c2t, zoom });
  await page.keyboard.press('Escape');
  await sleep(200);

  // 影片播放器
  await scrollItem(page, 'c1', 'i1', 'start');
  await page.evaluate(() => { const sc = document.querySelector('.conv[data-conv="c1"] .scroller'); sc.scrollTop = Math.max(0, sc.scrollTop - 40); });
  if (HAS_VIDEO) {
    await page.click(`${convSel('c1')} .it[data-id="i1"] .att-vid`);
    // 播放器的影片在 iframe 裡（framedvideo.js）
    await page.waitForSelector('.vp iframe.vframe');
    const ready = await until(() => page.evaluate(() => { const f = document.querySelector('.vp iframe.vframe'); const v = f && f.contentDocument && f.contentDocument.querySelector('video'); return v && v.readyState >= 1 ? { readyState: v.readyState, duration: v.duration } : null; }), 8000);
    await page.evaluate(() => { const f = document.querySelector('.vp iframe.vframe'); const v = f && f.contentDocument && f.contentDocument.querySelector('video'); if (v) { v.pause(); try { v.currentTime = 1.2; } catch { /* 忽略 */ } } });
    await sleep(500);
    await shot(page, `video-player-${tag}`);
    if (full) check('videoLoadsMetadata', !!ready && ready.duration > 2.5, ready);
    await page.keyboard.press('Escape');
    await sleep(200);
  } else {
    report.notes.push('PATH 上沒有 ffmpeg：略過影片截圖與檢查');
  }

  // 問卷：數字鍵＋點選＋密碼欄
  await scrollItem(page, 'c1', 'i19', 'center');
  const q = `${convSel('c1')} .it[data-id="i19"]`;
  await page.click(`${q} .card-head`);
  await page.keyboard.press('2');
  await page.click(`${q} .q-block[data-qi="1"] .opt >> nth=0`);
  await page.click(`${q} .q-block[data-qi="1"] .opt >> nth=1`);
  await page.fill(`${q} .q-block[data-qi="2"] .q-other`, 'sk-test-123');
  await page.click(`${q} .card-actions .btn.acc`);
  const ans = await until(() => host.last('answer', (m) => m.itemId === 'i19'), 2000);
  if (full) {
    const a = ans && ans.answers;
    check('questionAnswer', !!a && a.q1 && a.q1[0] === '5 分鐘' && a.q2 && a.q2.join('|') === '逾時回報|關閉視窗後重新檢查' && a.q3 && a.q3[0] === 'sk-test-123', ans);
  }

  // 核准：否＋告訴代理改怎麼做
  const ap = `${convSel('c1')} .it[data-id="i20"]`;
  if (full) {
    const labels = await page.$$eval(`${ap} .opt .opt-label`, (n) => n.map((x) => x.textContent));
    check('approvalAllowSessionLabel', labels.join('|') === '是|本對話同樣的指令都允許|否', labels);
  }
  await page.fill(`${ap} .ap-tell`, '改成只跑 AccountRulesTests');
  await page.click(`${ap} .opt.deny`);
  const appr = await until(() => host.last('approve', (m) => m.itemId === 'i20'), 2000);
  if (full) check('approveDenyWithMessage', !!appr && appr.choice === 'deny' && appr.message === '改成只跑 AccountRulesTests', appr);

  // 計畫確認：手動核准每次編輯
  await page.click(`${convSel('c1')} .it[data-id="i18"] .opt >> nth=1`);
  const plan = await until(() => host.last('approvePlan', (m) => m.itemId === 'i18'), 2000);
  if (full) check('approvePlanManual', !!plan && plan.choice === 'manual', plan);
  await sleep(300);
  await shot(page, `chat-single-answered-${tag}`);

  // XSS：html 欄位被塞了腳本也不會執行（CSP 擋 inline handler；script 不會因 innerHTML 執行）
  if (full) {
    app.log.phase = 'xss';
    await host.send({ type: 'item', conversationId: 'c1', item: { id: 'xss1', kind: 'Assistant', status: 'Done', time: Date.now(), html: '<p>XSS 測試</p><img src="x" onerror="window.__xss=1"><script>window.__xss=2</script>' } });
    await sleep(900);
    const xss = await page.evaluate(() => (window.__xss === undefined ? null : window.__xss));
    app.log.phase = 'normal';
    check('xssBlocked', xss === null, { windowXss: xss });
    check('cspReportedInlineHandler', app.log.xss.some((e) => /Content Security Policy/i.test(e)), app.log.xss);
    await host.send({ type: 'removeItem', conversationId: 'c1', id: 'xss1' });
    await sleep(100);
  }

  // 檔案瀏覽器抽屜
  await page.click(`${convSel('c1')} .ch .ibtn[aria-label="檔案瀏覽器"]`);
  await until(() => host.last('listFiles', (m) => m.conversationId === 'c1'), 2000);
  await page.waitForSelector(`${convSel('c1')} .drawer.open .fe`);
  await page.click(`${convSel('c1')} .drawer .fe.k-dir:has(.fe-name:text-is("assets"))`);
  await until(() => page.evaluate(() => document.querySelectorAll('.conv[data-conv="c1"] .drawer .fe.k-image').length >= 12), 3000);
  await shot(page, `drawer-grid-${tag}`);
  if (full) {
    const lf = host.received.filter((m) => m.cmd === 'listFiles');
    const crumbs = await page.$$eval(`${convSel('c1')} .dr-crumbs .cr`, (n) => n.map((x) => x.textContent));
    check('drawerListsAndNavigates', lf.length >= 2 && lf[0].path === null && lf[1].path === F.ROOT + '\\assets' && crumbs.join('/') === '工作資料夾/assets', { listFiles: lf, crumbs });
  }
  await page.click(`${convSel('c1')} .drawer .fe.k-image >> nth=2`, { button: 'right' });
  await page.waitForSelector('.menu');
  await shot(page, `drawer-menu-${tag}`);
  await page.keyboard.press('Escape');
  await page.click(`${convSel('c1')} .drawer .fe.k-image >> nth=0`);
  await page.waitForSelector('.lb .lb-img');
  const dc = await page.textContent('.lb .lb-count');
  if (full) check('drawerImageOpensFolderLightbox', dc === '第 1／12 張', dc);
  await page.keyboard.press('Escape');
  await sleep(150);
  await page.click(`${convSel('c1')} .drawer .ibtn[aria-label="改用清單檢視"]`);
  await shot(page, `drawer-list-${tag}`);
  await page.click(`${convSel('c1')} .drawer .ibtn[aria-label="關閉檔案瀏覽器"]`);

  // 側欄點對話＝開分頁（送 open），× 關分頁（送 close）
  await page.click('.crow[data-key="c:c5"]');
  const openC5 = await until(() => host.last('open', (m) => m.conversationId === 'c5'), 2000);
  await waitConv(page, 'c5');
  await page.click('.tab[data-key="c5"] .tab-x');
  const closeC5 = await until(() => host.last('close', (m) => m.conversationId === 'c5'), 2000);
  const tabGone = await until(() => page.evaluate(() => !document.querySelector('.tab[data-key="c5"]')), 1000);

  // 分割：2 窗格（左 c1 執行中、右 c3 執行中）
  await page.click('.pane[data-pane="p1"] .strip-actions .ibtn[title^="分割"]');
  const nc = await until(() => host.last('newConversation', (m) => m.paneId === 'p2'), 2000);
  await page.waitForSelector('.pane[data-pane="p2"] .conv:not(.loading)');
  await page.click('.crow[data-key="c:c3"]');
  await waitConv(page, 'c3');
  await sleep(500);
  await shot(page, `chat-2pane-${tag}`);
  const layout2 = await until(() => { const l = host.last('layout'); return l && l.layout.panes.length === 2 && l.layout.panes[1].tabs.includes('c3') ? l : null; }, 2000);
  if (full) {
    check('openCloseLayout', !!openC5 && !!closeC5 && !!tabGone && !!nc && !!layout2 && layout2.layout.focusedPane === 'p2',
      { openC5: !!openC5, closeC5: !!closeC5, tabGone: !!tabGone, newConversation: nc, layout: layout2 && layout2.layout });
  }

  // 4 窗格（2×2）
  await page.click('.pane[data-pane="p2"] .strip-actions .ibtn[title^="分割"]');
  await page.waitForSelector('.pane[data-pane="p3"] .conv:not(.loading)');
  await page.click('.crow[data-key="c:c4"]');
  await waitConv(page, 'c4');
  await page.click('.pane[data-pane="p3"] .strip-actions .ibtn[title^="分割"]');
  await page.waitForSelector('.pane[data-pane="p4"] .conv:not(.loading)');
  await page.click('.crow[data-key="c:c2"]'); // c2 已在 p1：切過去而不是重開
  await page.click('.pane[data-pane="p4"]');
  await page.click('.crow[data-key="c:c7"]');
  await waitConv(page, 'c7');
  await sleep(500);
  await shot(page, `chat-4pane-${tag}`);
  const layout4 = await until(() => { const l = host.last('layout'); return l && l.layout.panes.length === 4 ? l : null; }, 2000);
  // 4 個還能再分割（最多 12 個，見 runVisualChecks 的 paneGrid）
  const splitEnabled = await page.evaluate(() => Array.from(document.querySelectorAll('.strip-actions .ibtn[title^="分割"]')).filter((b) => !b.disabled).length === 4);
  if (full) {
    const reopened = host.received.filter((m) => m.cmd === 'open' && m.conversationId === 'c2').length;
    check('fourPanesLayout', !!layout4 && splitEnabled && reopened === 1, { layout: layout4 && layout4.layout, splitEnabled, c2OpenCount: reopened });
  }

  // 分頁拖曳到另一個窗格
  if (full) {
    await page.dragAndDrop('.tab[data-key="c2"]', '.pane[data-pane="p3"] .tabs');
    const moved = await until(() => { const l = host.last('layout'); return l && l.layout.panes.find((p) => p.id === 'p3' && p.tabs.includes('c2')) && !l.layout.panes.find((p) => p.id === 'p1' && p.tabs.includes('c2')) ? l : null; }, 2500);
    check('tabDragToPane', !!moved, moved && moved.layout);
  }

  // showView（系統匣「帳戶儀表板」）
  await host.send({ type: 'showView', view: 'dashboard' });
  await sleep(1300); // 圓環長出的動畫
  const dashShown = await visible(page, '#dashboard');
  const wsHidden = !(await visible(page, '#workspace'));
  await shot(page, `dashboard-${tag}`);
  const layoutDash = await until(() => { const l = host.last('layout'); return l && l.layout.view === 'dashboard' ? l : null; }, 2000);
  if (full) {
    const rings = await page.$$eval('.acard .ring-col', (n) => n.length);
    const ringsSev = await page.$$eval('.acard .ring-col', (n) => n.map((x) => x.dataset.sev));
    check('showViewDashboard', dashShown && wsHidden && !!layoutDash, { dashShown, wsHidden, layoutView: layoutDash && layoutDash.layout.view });
    const visDash = host.last('visible');
    check('visibleEmptyOnDashboard', !!visDash && visDash.conversationIds.length === 0, visDash);
    check('dashboardRings', rings === 11 && ringsSev.includes('high') && ringsSev.includes('warn'), { rings, ringsSev });

    // CLI 卡：找到執行檔但第一次查版本還沒完成（checking）時顯示「檢查中…」、不給安裝按鈕；查最新版失敗（latestError）時不顯示「已是最新版」
    const savedCli = JSON.parse(JSON.stringify(host.cli));
    Object.assign(host.cli.Claude, { installed: false, checking: true, version: null, latest: null, updateAvailable: false, lastCheckedAt: null });
    Object.assign(host.cli.Codex, { installed: true, checking: false, version: '0.162.0', latest: '0.162.0', updateAvailable: false, latestError: '無法查詢最新版本：連線逾時' });
    await host.send({ type: 'cli', cli: host.cli });
    await sleep(200);
    const cliCards = await page.evaluate(() => {
      const shown = (el) => !!el && getComputedStyle(el).display !== 'none' && el.getBoundingClientRect().height > 0;
      return Object.fromEntries(Array.from(document.querySelectorAll('#dashboard .cli-grid .ccard')).map((c) => [c.dataset.key, {
        ver: c.querySelector('.ccard-ver').textContent,
        pill: shown(c.querySelector('.pill')) ? c.querySelector('.pill').textContent : null,
        button: shown(c.querySelector('.ccard-foot')) ? c.querySelector('.ccard-foot .btn').textContent : null,
        msg: shown(c.querySelector('.ccard-msg')) ? c.querySelector('.ccard-msg').textContent : null,
      }]));
    });
    await page.locator('#dashboard .cli-grid').screenshot({ path: path.join(OUT, `dashboard-cli-states-${tag}.png`) });
    report.screenshots.push(`dashboard-cli-states-${tag}.png`);
    check('cliCardCheckingAndLatestError',
      cliCards.Claude.ver === '檢查中…' && cliCards.Claude.pill === null && cliCards.Claude.button === null
        && cliCards.Codex.pill === null && cliCards.Codex.msg === '無法查詢最新版本：連線逾時' && cliCards.Codex.button === null,
      cliCards);
    host.cli = savedCli;
    await host.send({ type: 'cli', cli: host.cli });
    await page.evaluate(() => { document.getElementById('dashboard').scrollTop = 0; });   // 元素截圖會把儀表板捲到 CLI 區，捲回頂端
    await sleep(150);
  }

  // 走勢線 hover
  const spark = await page.locator('.acard .spark >> nth=0').boundingBox();
  if (spark) {
    await page.mouse.move(spark.x + spark.width * 0.7, spark.y + spark.height / 2);
    await sleep(150);
    await shot(page, `dashboard-spark-hover-${tag}`);
    if (full) { const tip = await page.textContent('.tip').catch(() => null); check('sparklineTooltip', !!tip && /\d+%/.test(tip), tip); }
    await page.mouse.move(5, 5);
  }

  // 新增帳戶對話框
  await page.click('.acard-add');
  await page.waitForSelector('.dlg-add');
  await shot(page, `add-account-${tag}`);
  // Antigravity 一台電腦只能有一個帳戶：已經有 a5，選項停用並說明
  const agyChoice = await page.evaluate(() => { const b = document.querySelector('.dlg-add .prov[data-p="Antigravity"]'); return b && { disabled: b.disabled, text: b.textContent }; });
  if (full) check('addAccountAntigravitySingle', !!agyChoice && agyChoice.disabled && agyChoice.text.includes('已加入'), agyChoice);
  await page.click('.dlg-add .prov[data-p="Codex"]');
  await page.fill('.dlg-add input.input', '測試 Codex');
  const swatchColor = await page.getAttribute('.dlg-add .swatch >> nth=6', 'data-c');
  await page.click('.dlg-add .swatch >> nth=6');
  await page.click('.dlg-add .btn.primary');
  const added = await until(() => host.last('accountAdd'), 2000);
  if (full) check('accountAdd', !!added && added.provider === 'Codex' && added.label === '測試 Codex' && added.color === swatchColor, added);
  await sleep(400);
  await shot(page, `dashboard-login-${tag}`);

  // 登入中可以取消（瀏覽器分頁被關掉、想換帳號時不必等 10 分鐘逾時）
  const cancelBtn = page.locator('.acard:has(.acard-name:text-is("測試 Codex")) .acard-foot button:has-text("取消登入")');
  const cancelEnabled = (await cancelBtn.count()) === 1 && (await cancelBtn.isEnabled());
  if (cancelEnabled) {
    await cancelBtn.scrollIntoViewIfNeeded();
    await shot(page, `dashboard-login-busy-${tag}`);
    await cancelBtn.click();
  }
  const canceled = await until(() => host.last('accountLoginCancel'), 2000);
  const relogin = await until(() => page.evaluate(() => {
    const card = [...document.querySelectorAll('.acard')].find((c) => c.querySelector('.acard-name')?.textContent === '測試 Codex');
    const btn = card && card.querySelector('.acard-foot button');
    return btn && !btn.disabled && btn.textContent.includes('登入') && !btn.textContent.includes('取消') ? btn.textContent : null;
  }), 2000);
  await sleep(200);
  await shot(page, `dashboard-login-canceled-${tag}`);
  if (full) check('accountLoginCancel', cancelEnabled && !!canceled && !!added && !!relogin, { cancelEnabled, canceled, relogin });

  // 移除帳戶確認（隔離帳戶可勾選刪除設定資料夾；系統預設帳戶不行）
  await page.click('.acard[data-key="a1"] .acard-more');
  await page.click('.menu .menu-item:has(.mi-label:text-is("移除帳戶…"))');
  await page.waitForSelector('.dlg-confirm');
  const defLocked = await page.isDisabled('.dlg-confirm .check-row input');
  await page.click('.dlg-confirm .btn:not(.danger-solid)');
  await sleep(150);
  await page.click('.acard[data-key="a2"] .acard-more');
  await page.click('.menu .menu-item:has(.mi-label:text-is("移除帳戶…"))');
  await page.waitForSelector('.dlg-confirm');
  await page.check('.dlg-confirm .check-row input');
  await shot(page, `remove-account-${tag}`);
  await page.click('.dlg-confirm .btn.danger-solid');
  const removed = await until(() => host.last('accountRemove'), 2000);
  if (full) check('accountRemoveConfirm', defLocked && !!removed && removed.accountId === 'a2' && removed.deleteData === true, { defaultLocked: defLocked, removed });
  await sleep(300);

  // 設定對話框：左側直排的「一般／對話／模型設定／CLI／多電腦同步／版本與更新」六個分頁。一開在「一般」、焦點在選取的分頁，一次只顯示一個面板；
  // 方向鍵（頭尾循環）、Home／End 切換並移動焦點；高度固定（切分頁不跳動）、分頁列不隨內容捲動、換分頁從頂端看起；
  // 只有一個區塊的分頁不重複顯示區塊標題（heads 只數區塊層級的標題）；關掉再開回到上次看的分頁；指定區塊時切到它所在的分頁並捲到看得見
  await page.click('#btnSettings');
  await page.waitForSelector('.dlg-settings');
  await sleep(250);
  const readTabs = () => page.evaluate(() => {
    const d = document.querySelector('.dlg-settings');
    const tabs = Array.from(d.querySelectorAll('.set-tab'));
    const sel = tabs.find((t) => t.getAttribute('aria-selected') === 'true');
    const panels = Array.from(d.querySelectorAll('.set-panel')).filter((p) => getComputedStyle(p).display !== 'none');
    const bar = d.querySelector('.set-tabs');
    const one = panels.length === 1 ? panels[0] : null;
    return {
      labels: tabs.map((t) => t.textContent), sel: sel ? sel.dataset.tab : null, focus: !!sel && document.activeElement === sel,
      tabbable: tabs.filter((t) => t.tabIndex === 0).map((t) => t.dataset.tab), shown: panels.map((p) => p.dataset.tab),
      secs: one ? Array.from(one.querySelectorAll('.set-sec')).map((s) => s.dataset.sec) : null, heads: one ? one.querySelectorAll(':scope > .set-sec > .set-h').length : null,
      linked: !!sel && !!one && sel.getAttribute('aria-controls') === one.id && one.getAttribute('aria-labelledby') === sel.id,
      height: Math.round(d.getBoundingClientRect().height), barTop: Math.round(bar.getBoundingClientRect().top), barOverflow: bar.scrollWidth > bar.clientWidth + 1,
    };
  });
  const tabIs = (t, id, secs, heads) => t.sel === id && same(t.tabbable, [id]) && same(t.shown, [id]) && same(t.secs, secs) && t.heads === heads && t.linked && !t.barOverflow;
  const scrollTop = () => page.$eval('.dlg-settings .set-panels', (s) => s.scrollTop);
  const tb0 = await readTabs();
  await shot(page, `settings-${tag}`);
  await page.keyboard.press('ArrowRight');
  const tb1 = await readTabs();
  await page.keyboard.press('End');
  const tb2 = await readTabs();
  await page.keyboard.press('ArrowRight');
  const tb3 = await readTabs();
  await page.keyboard.press('ArrowLeft');
  const tb4 = await readTabs();
  await page.click('.dlg-settings .set-tab[data-tab="chat"]');
  await page.$eval('.dlg-settings .set-panels', (s) => { s.scrollTop = s.scrollHeight; });
  const tb5 = await readTabs();
  const scrolled = await scrollTop();
  await page.click('.dlg-settings .set-tab[data-tab="defaults"]');
  const tb6 = await readTabs();
  const scrollAfterSwitch = await scrollTop();
  await shot(page, `settings-tab-defaults-${tag}`);
  await page.keyboard.press('Escape');
  await sleep(200);
  await page.click('#btnSettings');
  await page.waitForSelector('.dlg-settings');
  await sleep(250);
  const tb7 = await readTabs();
  await page.keyboard.press('Escape');
  await sleep(200);
  // 指定區塊（不是分頁的第一塊）：程式開啟，和 CLI 徽章走同一條路
  await page.evaluate(() => import('/settings.js').then((m) => m.openSettings({ section: 'system' })));
  await page.waitForSelector('.dlg-settings');
  await sleep(250);
  const tb8 = await readTabs();
  const sysVisible = await page.evaluate(() => {
    const s = document.querySelector('.dlg-settings .set-panels').getBoundingClientRect();
    const r = document.querySelector('.dlg-settings .set-sec[data-sec="system"]').getBoundingClientRect();
    return r.height > 0 && r.top >= s.top - 1 && r.top < s.bottom - 40;
  });
  if (full) check('settingsTabs', same(tb0.labels, ['一般', '對話', '模型設定', 'CLI', '多電腦同步', '版本與更新']) && tabIs(tb0, 'general', ['look', 'system', 'notify'], 3) && tb0.focus
    && tabIs(tb1, 'chat', ['chat', 'quota', 'remote', 'instr'], 4) && tb1.focus && tabIs(tb2, 'app', ['app'], 0) && tb2.focus
    && tabIs(tb3, 'general', ['look', 'system', 'notify'], 3) && tb3.focus && tabIs(tb4, 'app', ['app'], 0) && tb4.focus
    && tabIs(tb5, 'chat', ['chat', 'quota', 'remote', 'instr'], 4) && scrolled > 0 && tabIs(tb6, 'defaults', ['defaults'], 0) && scrollAfterSwitch === 0
    && [tb1, tb2, tb3, tb4, tb5, tb6].every((t) => t.height === tb0.height && t.barTop === tb0.barTop)
    && tabIs(tb7, 'defaults', ['defaults'], 0) && tb7.focus && tabIs(tb8, 'general', ['look', 'system', 'notify'], 3) && sysVisible,
    { open: tb0, right: tb1, end: tb2, wrapRight: tb3, wrapLeft: tb4, defaults: tb5, scrolled, chat: tb6, scrollAfterSwitch, reopened: tb7, section: tb8, sysVisible });
  await page.click('.dlg-settings .set-tab[data-tab="chat"]');
  await page.click('.dlg-settings .set-row:has(.set-label:text-is("Enter 送出")) .switch');
  const st1 = await until(() => host.last('setting', (m) => m.key === 'enterToSend'), 2000);
  // Remote Control 開關（預設開）：關掉送出 claudeRemoteControl=false，宿主重送設定後開關維持關閉
  const remoteRow = '.dlg-settings .set-row:has(.set-label:text-is("Claude 對話開啟 Remote Control"))';
  const rc0 = await page.$eval(remoteRow + ' input', (i) => i.checked);
  await page.click(remoteRow + ' .switch');
  const rc1 = await until(() => host.last('setting', (m) => m.key === 'claudeRemoteControl'), 2000);
  await sleep(150);
  const rc2 = await page.$eval(remoteRow + ' input', (i) => i.checked);
  await page.click(remoteRow + ' .switch');
  const rc3 = await until(() => host.last('setting', (m) => m.key === 'claudeRemoteControl' && m.value === true), 2000);
  await shot(page, `settings-tab-chat-${tag}`);
  if (full) check('settingsRemoteControl', rc0 === true && !!rc1 && rc1.value === false && rc2 === false && !!rc3, { initial: rc0, off: rc1, afterOff: rc2, on: rc3 });
  // 額度用完時自動換帳戶繼續（對話分頁的「額度用完時」，預設開）：關掉送出 autoSwitchAccount=false，宿主重送設定後開關維持關閉
  const switchRow = '.dlg-settings .set-sec[data-sec="quota"] .set-row[data-setting="autoSwitchAccount"]';
  const as0 = await page.$eval(switchRow, (r) => ({ checked: r.querySelector('input').checked, label: r.querySelector('.set-label').textContent, desc: r.querySelector('.set-desc').textContent }));
  await page.click(switchRow + ' .switch');
  const as1 = await until(() => host.last('setting', (m) => m.key === 'autoSwitchAccount'), 2000);
  await sleep(150);
  const as2 = await page.$eval(switchRow + ' input', (i) => i.checked);
  await page.click(switchRow + ' .switch');
  const as3 = await until(() => host.last('setting', (m) => m.key === 'autoSwitchAccount' && m.value === true), 2000);
  if (full) check('settingsAutoSwitchAccount', as0.checked === true && as0.label === '自動換帳戶繼續' && /最快重置/.test(as0.desc)
    && !!as1 && as1.value === false && as2 === false && !!as3, { initial: as0, off: as1, afterOff: as2, on: as3 });
  // 同種代理都沒額度時跨代理備援（預設關）：打開送出 autoSwitchCrossProvider=true，再關回 false
  const crossRow = '.dlg-settings .set-sec[data-sec="quota"] .set-row[data-setting="autoSwitchCrossProvider"]';
  const cp0 = await page.$eval(crossRow, (r) => ({ checked: r.querySelector('input').checked, label: r.querySelector('.set-label').textContent, desc: r.querySelector('.set-desc').textContent }));
  await page.click(crossRow + ' .switch');
  const cp1 = await until(() => host.last('setting', (m) => m.key === 'autoSwitchCrossProvider' && m.value === true), 2000);
  await sleep(120);
  const cp2 = await page.$eval(crossRow + ' input', (i) => i.checked);
  await page.click(crossRow + ' .switch');
  const cp3 = await until(() => host.last('setting', (m) => m.key === 'autoSwitchCrossProvider' && m.value === false), 2000);
  if (full) check('settingsAutoSwitchCrossProvider', cp0.checked === false && cp0.label === '同種代理都沒額度時跨代理備援' && /跨代理/.test(cp0.label)
    && !!cp1 && cp2 === true && !!cp3, { initial: cp0, on: cp1, afterOn: cp2, off: cp3 });
  await page.click('.dlg-settings .set-tab[data-tab="general"]');
  await page.click('.dlg-settings .seg-btn[data-value="light"]');
  const st2 = await until(() => host.last('setting', (m) => m.key === 'theme' && m.value === 'light'), 2000);
  await sleep(200);
  const themeAttr = await page.getAttribute('html', 'data-theme');
  await shot(page, `settings-light-${tag}`);
  if (full) check('settingsSend', !!st1 && st1.value === false && !!st2 && themeAttr === 'light', { enterToSend: st1, theme: st2, htmlTheme: themeAttr });
  // 開機時自動啟動（一般 › 系統的第一列，預設關）：打開送出 autoStart=true，宿主重送設定後開關維持開著；
  // 宿主說這次執行不能設定（null：不是安裝版）時開關停用並說明原因；「關閉視窗時縮到系統匣」關掉時說明改成會開啟視窗
  const bootRow = '.dlg-settings .set-sec[data-sec="system"] .set-row[data-setting="autoStart"]';
  const bootOf = () => page.$eval(bootRow, (r) => ({
    checked: r.querySelector('input').checked, disabled: r.querySelector('input').disabled, label: r.querySelector('.set-label').textContent,
    desc: r.querySelector('.set-desc').textContent, first: r === r.parentElement.querySelector('.set-row'),
  }));
  const bt0 = await bootOf();
  await page.click(bootRow + ' .switch');
  const bt1 = await until(() => host.last('setting', (m) => m.key === 'autoStart'), 2000);
  await sleep(150);
  const bt2 = await bootOf();
  await shot(page, `settings-autostart-${tag}`);
  host.settings.closeToTray = false;
  await host.send({ type: 'settings', settings: host.settings });
  await sleep(150);
  const bt3 = await bootOf();
  host.settings.autoStart = null;
  host.settings.closeToTray = true;
  await host.send({ type: 'settings', settings: host.settings });
  await sleep(150);
  const bt4 = await bootOf();
  host.settings.autoStart = false;
  await host.send({ type: 'settings', settings: host.settings });
  await sleep(150);
  const bt5 = await bootOf();
  if (full) check('settingsAutoStart', bt0.label === '開機時自動啟動' && bt0.first && bt0.checked === false && !bt0.disabled && /系統匣圖示/.test(bt0.desc)
    && !!bt1 && bt1.value === true && bt2.checked === true && /開啟視窗/.test(bt3.desc) && bt3.checked === true
    && bt4.disabled && bt4.checked === false && /安裝到這台電腦/.test(bt4.desc) && !bt5.disabled && bt5.checked === false && /系統匣圖示/.test(bt5.desc),
    { initial: bt0, on: bt1, afterOn: bt2, noTray: bt3, unavailable: bt4, back: bt5 });
  // 語音通知底下的語速與音調（預設 1.5 倍、升 2 個半音）：放開滑桿送出 voiceSpeed／voicePitch，拖動中只改數字；
  // 宿主重送設定後滑桿停在新值；語音通知關掉時兩個滑桿停用，打開又能調
  const voiceRow = '.dlg-settings .set-sec[data-sec="notify"] .set-row[data-setting="voiceNotifications"]';
  const tuneOf = () => page.$eval(voiceRow, (r) => {
    const one = (k) => { const i = r.querySelector('input[data-tune="' + k + '"]'); return i && { value: Number(i.value), disabled: i.disabled, text: i.parentElement.querySelector('.range-val').textContent }; };
    const t = r.querySelector('.voice-tune').getBoundingClientRect();
    const sw = r.querySelector('.switch').getBoundingClientRect();
    return { speed: one('speed'), pitch: one('pitch'), off: r.querySelector('.voice-tune').classList.contains('off'), below: t.top >= sw.bottom - 1, rows: r.parentElement.querySelectorAll('.set-row').length };
  });
  const slide = (k, v, commit) => page.$eval(`${voiceRow} input[data-tune="${k}"]`, (i, a) => {
    i.value = String(a.v);
    i.dispatchEvent(new Event('input', { bubbles: true }));
    if (a.commit) i.dispatchEvent(new Event('change', { bubbles: true }));
  }, { v, commit });
  const vt0 = await tuneOf();
  await slide('speed', 2.2, false);
  const vtDrag = await tuneOf();
  const sentWhileDragging = !!host.last('setting', (m) => m.key === 'voiceSpeed');
  await slide('speed', 1.8, true);
  const vs1 = await until(() => host.last('setting', (m) => m.key === 'voiceSpeed'), 2000);
  await slide('pitch', -3, true);
  const vp1 = await until(() => host.last('setting', (m) => m.key === 'voicePitch'), 2000);
  await page.evaluate(() => document.activeElement && document.activeElement.blur());
  await sleep(150);
  const vt1 = await tuneOf();
  await page.$eval(voiceRow, (r) => r.scrollIntoView({ block: 'center' }));
  await sleep(100);
  await shot(page, `settings-voice-tune-${tag}`);
  await page.click(voiceRow + ' .switch');
  await until(() => host.last('setting', (m) => m.key === 'voiceNotifications' && m.value === false), 2000);
  await sleep(150);
  const vt2 = await tuneOf();
  await page.click(voiceRow + ' .switch');
  await until(() => host.last('setting', (m) => m.key === 'voiceNotifications' && m.value === true), 2000);
  await sleep(150);
  const vt3 = await tuneOf();
  if (full) check('settingsVoiceTune', !!vt0.speed && vt0.speed.value === 1.5 && vt0.speed.text === '1.5×' && vt0.pitch.value === 2 && vt0.pitch.text === '+2 半音'
    && !vt0.speed.disabled && !vt0.off && vt0.below
    && vtDrag.speed.text === '2.2×' && !sentWhileDragging && !!vs1 && vs1.value === 1.8 && !!vp1 && vp1.value === -3
    && vt1.speed.value === 1.8 && vt1.speed.text === '1.8×' && vt1.pitch.value === -3 && vt1.pitch.text === '-3 半音'
    && vt2.speed.disabled && vt2.pitch.disabled && vt2.off && !vt3.speed.disabled && !vt3.off && vt3.rows === vt0.rows,
    { initial: vt0, dragging: vtDrag, sentWhileDragging, speed: vs1, pitch: vp1, after: vt1, off: vt2, on: vt3 });
  await page.click('.dlg-settings .seg-btn[data-value="dark"]');
  await page.click('.dlg-settings .set-tab[data-tab="chat"]');
  await page.click('.dlg-settings .set-row:has(.set-label:text-is("Enter 送出")) .switch');
  await page.keyboard.press('Escape');
  await sleep(150);

  // CLI 徽章 → 設定的 CLI 分頁（上次看的是「對話」也一樣）：已安裝的 CLI、更新、共用與權杖三塊
  await page.click('#cliBadge');
  await page.waitForSelector('.dlg-settings .set-sec[data-sec="cli"] .ccard');
  await sleep(300);
  const tbBadge = await readTabs();
  if (full) check('settingsCliBadgeTab', tabIs(tbBadge, 'cli', ['cli', 'cliUpdate', 'cliShare'], 3) && tbBadge.focus, tbBadge);
  await shot(page, `settings-cli-${tag}`);
  // 閒置的 Claude 帳戶自動換發權杖（CLI 分頁，預設開）：關掉送出 claudeIdleTokenRefresh=false，宿主重送設定後開關維持關閉
  const tokenRow = '.dlg-settings .set-sec[data-sec="cliShare"] .set-row[data-setting="claudeIdleTokenRefresh"]';
  const tk0 = await page.$eval(tokenRow, (r) => ({ checked: r.querySelector('input').checked, label: r.querySelector('.set-label').textContent, desc: r.querySelector('.set-desc').textContent }));
  await page.click(tokenRow + ' .switch');
  const tk1 = await until(() => host.last('setting', (m) => m.key === 'claudeIdleTokenRefresh'), 2000);
  await sleep(150);
  const tk2 = await page.$eval(tokenRow + ' input', (i) => i.checked);
  await page.click(tokenRow + ' .switch');
  const tk3 = await until(() => host.last('setting', (m) => m.key === 'claudeIdleTokenRefresh' && m.value === true), 2000);
  if (full) check('settingsIdleTokenRefresh', tk0.checked === true && tk0.label === '閒置的 Claude 帳戶自動換發權杖' && /每小時一次/.test(tk0.desc)
    && !!tk1 && tk1.value === false && tk2 === false && !!tk3, { initial: tk0, off: tk1, afterOff: tk2, on: tk3 });
  // 跨四家 CLI 共用技能與全域指示（CLI 分頁，預設關）：打開送出 shareCrossCliCustomizations=true，再關回 false
  const shareRow = '.dlg-settings .set-sec[data-sec="cliShare"] .set-row[data-setting="shareCrossCliCustomizations"]';
  const sc0 = await page.$eval(shareRow, (r) => ({ checked: r.querySelector('input').checked, label: r.querySelector('.set-label').textContent, desc: r.querySelector('.set-desc').textContent }));
  await page.click(shareRow + ' .switch');
  const sc1 = await until(() => host.last('setting', (m) => m.key === 'shareCrossCliCustomizations' && m.value === true), 2000);
  await sleep(120);
  const sc2 = await page.$eval(shareRow + ' input', (i) => i.checked);
  await page.click(shareRow + ' .switch');
  const sc3 = await until(() => host.last('setting', (m) => m.key === 'shareCrossCliCustomizations' && m.value === false), 2000);
  if (full) check('settingsShareCrossCliCustomizations', sc0.checked === false && sc0.label === '跨四家 CLI 共用技能與全域指示' && /GEMINI\.md/.test(sc0.desc)
    && !!sc1 && sc2 === true && !!sc3, { initial: sc0, on: sc1, afterOn: sc2, off: sc3 });
  await page.keyboard.press('Escape');

  // app 自我更新：橫幅（已備妥 → 立即重啟；排空中 → 取消這次）、設定頁的 Clode Myriagent 區塊
  await host.send({ type: 'appUpdate', state: F.appUpdate('staged') });
  await page.waitForSelector('#appBanner:not([hidden])');
  const stagedBanner = await page.textContent('#appBanner');
  const stagedContrast = await page.evaluate(contrastOf, '#appBanner .app-banner-text');
  const stagedLayout = await page.evaluate(bannerLayoutOf);
  const verStaged = await page.evaluate(versionBadgeOf);
  await shot(page, `app-banner-staged-${tag}`);
  await page.click('#appBanner button:has-text("立即重啟")');
  const restartNow = await until(() => host.last('appUpdateRestartNow'), 2000);
  await host.send({ type: 'appUpdate', state: F.appUpdate('draining') });
  await sleep(150);
  const drainBanner = await page.textContent('#appBanner');
  const drainContrast = await page.evaluate(contrastOf, '#appBanner .app-banner-text');
  const drainLayout = await page.evaluate(bannerLayoutOf);
  await shot(page, `app-banner-draining-${tag}`);
  // 排空中等的對話數變動：只換文字，「取消這次」不重建（鍵盤焦點不掉、點擊不落空）
  await page.evaluate(() => { const b = document.querySelector('#appBanner button'); b.dataset.probeKeep = '1'; b.focus(); });
  await host.send({ type: 'appUpdate', state: F.appUpdate('draining', { waiting: 1 }) });
  await sleep(150);
  const bannerKeep = await page.evaluate(() => {
    const b = document.querySelector('#appBanner button');
    return { kept: !!b && b.dataset.probeKeep === '1', focused: !!b && document.activeElement === b, text: document.querySelector('#appBanner .app-banner-text').textContent };
  });
  await page.click('#appBanner button:has-text("取消這次")');
  const cancel = await until(() => host.last('appUpdateCancel'), 2000);
  await host.send({ type: 'appUpdate', state: F.appUpdate('idle') });
  await sleep(150);
  const idleHidden = !(await visible(page, '#appBanner'));
  // 品牌名旁的版號：init.app 的版本（探針的 init 沒有 build＝開發版）；已備妥較新版時換色調、回到 idle 換回來；
  // 點了開設定的「版本與更新」分頁（Clode Myriagent 區塊）
  const verIdle = await page.evaluate(versionBadgeOf);
  await page.click('#appVer');
  await page.waitForSelector('.dlg-settings .set-sec[data-sec="app"]');
  await sleep(150);
  const verOpens = await page.evaluate(() => {
    const s = document.querySelector('.dlg-settings .set-sec[data-sec="app"]');
    const r = s.getBoundingClientRect();
    return { tab: document.querySelector('.dlg-settings .set-tab[aria-selected="true"]')?.dataset.tab ?? null, visible: r.height > 0 && r.top < innerHeight && r.bottom > 0 };
  });
  await page.keyboard.press('Escape');
  await sleep(100);
  // 版號前面是這一版的日期時間（init.app.commitAt；今年的省略年份），提示與讀屏帶完整日期時間
  if (full) check('versionBadgeDesktop',
    !!verStaged && verStaged.shown && verStaged.pre === '' && verStaged.text === '0.1.0' && verStaged.tone === 'update'
      && verStaged.time === '10/23 14:05' && verStaged.timeShown && verStaged.timeBeforeNum
      && verStaged.title.startsWith(`Clode Myriagent 0.1.0（開發版）\n更新於 ${F.COMMIT_FULL}\n新版 0.2.1530 已下載`) && verStaged.label === `版本 0.1.0（${F.COMMIT_FULL}），有新版 0.2.1530`
      && !!verIdle && verIdle.shown && verIdle.tone === 'plain' && verIdle.title.includes('\n已是最新版\n') && verIdle.label === `版本 0.1.0（${F.COMMIT_FULL}）`
      && verOpens.tab === 'app' && verOpens.visible,
    { staged: verStaged, idle: verIdle, opens: verOpens });
  await page.click('#btnSettings');
  // Clode Myriagent 區塊在「版本與更新」分頁（再開設定回到上次看的分頁，上一步是 CLI）
  await page.click('.dlg-settings .set-tab[data-tab="app"]');
  await page.waitForSelector('.dlg-settings .set-sec[data-sec="app"]');
  const appText = await page.textContent('.dlg-settings .set-sec[data-sec="app"]');
  await page.click('.dlg-settings .set-sec[data-sec="app"] .switch');
  const autoApp = await until(() => host.last('setting', (m) => m.key === 'autoUpdateApp'), 2000);
  await page.click('.dlg-settings .set-sec[data-sec="app"] button:has-text("立即檢查")');
  const checkNow = await until(() => host.last('appUpdateCheck'), 2000);
  await shot(page, `settings-app-${tag}`);
  // 下載進度一直更新：設定頁只換狀態文字（已下載的 MB），「立即檢查」不重建（焦點不掉、點擊不落空）
  await page.evaluate(() => { const b = document.querySelector('.dlg-settings .app-actions button'); b.dataset.probeKeep = '1'; b.focus(); });
  await host.send({ type: 'appUpdate', state: F.appUpdate('downloading') });
  await sleep(100);
  await host.send({ type: 'appUpdate', state: F.appUpdate('downloading', { downloaded: 60_000_000 }) });
  await sleep(150);
  const settingsKeep = await page.evaluate(() => {
    const b = document.querySelector('.dlg-settings .app-actions button');
    return { kept: !!b && b.dataset.probeKeep === '1' && b.textContent === '立即檢查', focused: !!b && document.activeElement === b, status: document.querySelector('.dlg-settings .app-status').textContent };
  });
  await host.send({ type: 'appUpdate', state: F.appUpdate('idle') });
  await sleep(100);
  // 已備妥但現在不能重啟（例如開著 Remote Control 的對話在用）：設定頁的狀態列和橫幅一樣說明原因，不再承諾「閒置時自動重啟套用」
  const remoteBlocker = '有開著 Remote Control 的對話正在使用或等你回應，空下來後再重啟';   // AppUpdateRestartRules.DescribeBlocker(RemoteInUse)
  await host.send({ type: 'appUpdate', state: F.appUpdate('staged', { blocker: remoteBlocker }) });
  await sleep(150);
  const blockedStatus = await page.textContent('.dlg-settings .app-status');
  const blockedBanner = await page.textContent('#appBanner');
  await shot(page, `settings-app-blocker-${tag}`);
  await host.send({ type: 'appUpdate', state: F.appUpdate('staged') });
  await sleep(150);
  const freeStatus = await page.textContent('.dlg-settings .app-status');
  if (full) check('appUpdateBlockerShown',
    blockedStatus.includes(`新版 0.2.1530 已下載；${remoteBlocker}`) && !blockedStatus.includes('閒置時自動重啟') && blockedBanner.includes(remoteBlocker)
      && !blockedBanner.includes('閒置時自動重啟') && freeStatus.includes('新版 0.2.1530 已下載，閒置時自動重啟套用'),
    { blockedStatus, blockedBanner, freeStatus });
  // 強制更新（2026-10-28 使用者要求）：設定頁在已備妥時、排空中還沒強制時有「強制更新」（提示說明不等背景工作、10 分鐘後強制停下），
  // 按了送 appUpdateForce；強制了就拿掉。橫幅在設定頁關掉之後檢查（forceBanner）
  const forceCount = () => host.received.filter((m) => m.cmd === 'appUpdateForce').length;
  const bgBlocker = '有對話的背景工作還在跑，全部結束後再重啟';   // AppUpdateRestartRules.DescribeBlocker(BackgroundWork)
  await host.send({ type: 'appUpdate', state: F.appUpdate('staged', { blocker: bgBlocker }) });
  await sleep(150);
  const setButtons = (sel) => page.$$eval(sel, (bs) => bs.map((b) => ({ text: b.textContent, title: b.title })));
  const forceSetStaged = await setButtons('.dlg-settings .app-actions button');
  const setForce0 = forceCount();
  await page.click('.dlg-settings .app-actions button:has-text("強制更新")');
  const setForceSent = await until(() => forceCount() === setForce0 + 1, 2000);
  await host.send({ type: 'appUpdate', state: F.appUpdate('draining') });
  await sleep(150);
  const forceSetDraining = await setButtons('.dlg-settings .app-actions button');
  await host.send({ type: 'appUpdate', state: F.appUpdate('draining', { forced: true, forceAt: new Date(2026, 9, 28, 1, 23).getTime() }) });
  await sleep(150);
  const forceSetForced = await setButtons('.dlg-settings .app-actions button');
  const forcedStatus = await page.textContent('.dlg-settings .app-status');
  await host.send({ type: 'appUpdate', state: F.appUpdate('draining', { handoverTo: '0.2.9' }) });
  await sleep(150);
  const forceSetHandover = await setButtons('.dlg-settings .app-actions button');
  const hasForce = (list) => list.some((b) => b.text === '強制更新');
  if (full) check('appUpdateForceSettings',
    hasForce(forceSetStaged) && /不等背景工作.*10 分鐘/.test(forceSetStaged.find((b) => b.text === '強制更新').title) && !!setForceSent
      && hasForce(forceSetDraining) && !hasForce(forceSetForced) && !hasForce(forceSetHandover)
      && forcedStatus.includes('正在強制更新：等 2 個對話到安全點（01:23 起還沒停下的強制停下）'),
    { forceSetStaged, setForceSent, forceSetDraining, forceSetForced, forceSetHandover, forcedStatus });
  await host.send({ type: 'appUpdate', state: F.appUpdate('idle') });
  await sleep(100);
  await page.click('.dlg-settings .set-sec[data-sec="app"] .switch');
  await page.keyboard.press('Escape');
  // 強制更新的橫幅：已備妥時「立即重啟」旁邊有「強制更新」，按了送 appUpdateForce；排空中還沒強制的「強制更新」＋「取消這次」，
  // 強制了只剩「取消這次」、說明幾點起強制停下
  await host.send({ type: 'appUpdate', state: F.appUpdate('staged', { blocker: bgBlocker }) });
  await page.waitForSelector('#appBanner:not([hidden])');
  await sleep(150);
  const bannerButtons = () => page.$$eval('#appBanner button', (bs) => bs.map((b) => b.textContent));
  const forceBannerStaged = { text: await page.textContent('#appBanner'), buttons: await bannerButtons(), layout: await page.evaluate(bannerLayoutOf) };
  await shot(page, `app-banner-force-${tag}`);
  const bannerForce0 = forceCount();
  await page.click('#appBanner button:has-text("強制更新")');
  const bannerForceSent = await until(() => forceCount() === bannerForce0 + 1, 2000);
  await host.send({ type: 'appUpdate', state: F.appUpdate('draining') });
  await sleep(150);
  const forceBannerDraining = { buttons: await bannerButtons(), layout: await page.evaluate(bannerLayoutOf) };
  await host.send({ type: 'appUpdate', state: F.appUpdate('draining', { forced: true, forceAt: new Date(2026, 9, 28, 1, 23).getTime() }) });
  await sleep(150);
  const forceBannerForced = { text: await page.textContent('#appBanner'), buttons: await bannerButtons(), layout: await page.evaluate(bannerLayoutOf) };
  await shot(page, `app-banner-forced-${tag}`);
  check('appUpdateForceBanner_' + tag,
    forceBannerStaged.text.includes(bgBlocker) && forceBannerStaged.buttons.join('|') === '立即重啟|強制更新' && !!bannerForceSent
      && forceBannerDraining.buttons.join('|') === '強制更新|取消這次'
      && forceBannerForced.buttons.join('|') === '取消這次' && forceBannerForced.text.includes('01:23 起還沒停下的強制停下')
      && [forceBannerStaged.layout, forceBannerDraining.layout, forceBannerForced.layout].every(bannerLayoutOk),
    { forceBannerStaged, bannerForceSent, forceBannerDraining, forceBannerForced });
  await host.send({ type: 'appUpdate', state: F.appUpdate('disabled') });
  await sleep(150);
  const installBanner = await page.textContent('#appBanner');
  const installLayout = await page.evaluate(bannerLayoutOf);
  await page.click('#appBanner button:has-text("安裝到這台電腦")');
  const install = await until(() => host.last('appInstallHere'), 2000);
  await page.click('#appBanner .app-banner-close');
  await sleep(100);
  const dismissed = !(await visible(page, '#appBanner'));
  if (full) check('appUpdateBanner',
    stagedBanner.includes('新版 0.2.1530 已下載') && !!restartNow && drainBanner.includes('等 2 個對話') && !!cancel && idleHidden
      && appText.includes(`${F.COMMIT_FULL} · 0.2.1523（ab12cd3）`) && appText.includes('已更新到 0.2.1523') && !appText.includes('null') && !!autoApp && autoApp.value === false && !!checkNow
      && appText.includes('已是最新版（上次檢查') && !appText.includes('已是最新版：已是最新版')   // idle 的 detail（檢查結果）不和階段的說法重複
      && installBanner.includes('安裝到這台電腦') && !!install && dismissed,
    { stagedBanner, drainBanner, idleHidden, appText, autoApp, install: !!install, dismissed });
  if (full) check('appBannerContrast_dark', [stagedContrast, drainContrast].every((r) => r !== null && r >= 4.5), { staged: stagedContrast, draining: drainContrast });
  if (full) check('appUpdateInPlace', bannerKeep.kept && bannerKeep.focused && bannerKeep.text.includes('等 1 個對話')
      && settingsKeep.kept && settingsKeep.focused && settingsKeep.status.includes('已下載 57 MB') && !settingsKeep.status.includes('%'),
    { bannerKeep, settingsKeep });
  check('appBannerLayout_dark_' + tag, [stagedLayout, drainLayout, installLayout].every(bannerLayoutOk), { staged: stagedLayout, draining: drainLayout, install: installLayout });

  // 草稿快照：宿主要求時回覆每個開著的對話輸入框裡的字。
  // draftTexts() 讀所有開著的對話（含藏在後面的分頁、儀表板檢視時整個工作區都藏著），所以直接設 .value、不經 page.fill 的可見檢查
  await page.evaluate(([sel, t]) => { document.querySelector(sel).value = t; }, [convSel('c1') + ' .cinput', '還沒送出的草稿']);
  await host.send({ type: 'draftSnapshotRequest', requestId: 'r-probe' });
  const snap = await until(() => host.last('draftSnapshot', (m) => m.requestId === 'r-probe'), 2000);
  await page.evaluate((sel) => { document.querySelector(sel).value = ''; }, convSel('c1') + ' .cinput');
  if (full) check('draftSnapshot', !!snap && snap.drafts.some((d) => d.conversationId === 'c1' && d.text === '還沒送出的草稿'), { snap });

  // 回到對話
  await host.send({ type: 'showView', view: 'chat' });
  await sleep(300);
  if (full) {
    const back = (await visible(page, '#workspace')) && !(await visible(page, '#dashboard'));
    check('showViewChat', back, back);
  }
  await closeApp(app);
}

// ───── 彈出視窗 ─────
async function runPopout(vp, full) {
  const tag = String(vp.w);
  const app = await openApp('popout-' + tag, { vp: { w: Math.round(vp.w * 0.62), h: vp.h }, mode: 'popout', popoutId: 'c3', url: `${ORIGIN}/index.html?mode=popout&conversation=c3` });
  const { page, host } = app;
  await waitConv(page, 'c3');
  await sleep(800);
  await shot(page, `popout-${tag}`);
  if (full) {
    const first = host.received[0];
    const opened = host.received.some((m) => m.cmd === 'open' && m.conversationId === 'c3');
    const layouts = host.received.filter((m) => m.cmd === 'layout').length;
    const noChrome = !(await visible(page, '.view-switch')) && !(await visible(page, '#sidebar')) && !(await visible(page, '.strip'));
    check('popoutMode', first && first.cmd === 'ready' && first.mode === 'popout' && first.conversationId === 'c3' && opened && layouts === 0 && noChrome,
      { ready: first, opened, layoutMessages: layouts, noChrome });
  }
  await closeApp(app);
}

// ───── 沒有儲存的版面：在 p1 開新對話 ─────
async function runNoLayout(vp) {
  const app = await openApp('nolayout', { vp, layout: null });
  const { page, host } = app;
  const nc = await until(() => host.last('newConversation'), 3000);
  await page.waitForSelector('.pane[data-pane="p1"] .conv:not(.loading)', { timeout: 5000 }).catch(() => {});
  const draftTab = await page.evaluate(() => { const t = document.querySelector('.pane[data-pane="p1"] .tab.active'); return t ? t.dataset.key : null; });
  const opens = host.received.filter((m) => m.cmd === 'open').length;
  check('initWithoutLayoutStartsDraft', !!nc && nc.paneId === 'p1' && nc.accountId === null && /^d\d+$/.test(draftTab || '') && opens === 0, { newConversation: nc, draftTab, opens });
  await closeApp(app);
}

// ───── app 更新後還原的草稿對話：照存下的版面放回原位、作用中的分頁不變、不另開空白的新對話 ─────
const RESTORED_DRAFTS = [{ id: 'dr1', accountId: 'a1', text: '還原的草稿一' }, { id: 'dr2', accountId: 'a3', text: '還原的草稿二' }];
/** 各窗格的分頁順序與作用中的分頁，加上聚焦的窗格 */
const paneTabs = (page) => page.evaluate(() => ({
  panes: Object.fromEntries(Array.from(document.querySelectorAll('.pane')).map((p) => {
    const active = p.querySelector('.tab.active');
    return [p.dataset.pane, { tabs: Array.from(p.querySelectorAll('.tab')).map((t) => t.dataset.key), active: active ? active.dataset.key : null }];
  })),
  focused: (document.querySelector('.pane.focused') || document.querySelector('.pane') || { dataset: {} }).dataset.pane || null,
}));

async function runDraftRestore(vp) {
  const result = {};
  let ok = true;
  const cases = [
    {
      // 版面只有草稿分頁（草稿不在 init 的對話清單裡）
      name: 'only',
      layout: { panes: [{ id: 'p1', tabs: ['dr1', 'dr2'], active: 'dr2' }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' },
      wait: ['dr1', 'dr2'],
      expect: { panes: { p1: { tabs: ['dr1', 'dr2'], active: 'dr2' } }, focused: 'p1' },
      opens: [],
    },
    {
      // 草稿夾在正式對話之間；另一個窗格只有草稿、而且是作用中的
      name: 'mixed',
      layout: { panes: [{ id: 'p1', tabs: ['c1', 'dr1', 'c2'], active: 'c2' }, { id: 'p2', tabs: ['dr2'], active: 'dr2' }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' },
      wait: ['c2', 'dr1', 'dr2'],
      expect: { panes: { p1: { tabs: ['c1', 'dr1', 'c2'], active: 'c2' }, p2: { tabs: ['dr2'], active: 'dr2' } }, focused: 'p1' },
      opens: ['c2'], // 背景的 c1 切過去才 open
    },
    {
      // 存下的版面裡沒有的草稿（例如原本顯示在彈出視窗）：放在聚焦窗格的最後，不搶作用中的分頁
      name: 'outside',
      layout: { panes: [{ id: 'p1', tabs: ['c1'], active: 'c1' }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' },
      wait: ['c1', 'dr1', 'dr2'],
      expect: { panes: { p1: { tabs: ['c1', 'dr1', 'dr2'], active: 'c1' } }, focused: 'p1' },
      opens: ['c1'],
    },
  ];
  for (const k of cases) {
    const app = await openApp('draft-restore-' + k.name, { vp, layout: k.layout, restoredDrafts: RESTORED_DRAFTS });
    const { page, host } = app;
    for (const id of k.wait) await page.waitForSelector(convSel(id) + ':not(.loading)', { state: 'attached', timeout: 5000 }).catch(() => {});
    await sleep(400);   // 網頁若自己開了新對話，等它的回覆到達
    const layout = await paneTabs(page);
    const texts = await page.evaluate((ids) => Object.fromEntries(ids.map((id) => {
      const ta = document.querySelector('.conv[data-conv="' + id + '"] .cinput');
      return [id, ta ? ta.value : null];
    })), RESTORED_DRAFTS.map((d) => d.id));
    const newConversation = host.received.filter((m) => m.cmd === 'newConversation').length;
    const opens = host.received.filter((m) => m.cmd === 'open').map((m) => m.conversationId).sort();
    result[k.name] = { layout, texts, newConversation, opens };
    if (!(same(layout, k.expect) && RESTORED_DRAFTS.every((d) => texts[d.id] === d.text) && newConversation === 0 && same(opens, k.opens))) ok = false;
    await shot(page, 'draft-restore-' + k.name + '-' + vp.w);
    await closeApp(app);
  }
  check('restoredDraftsKeepLayout', ok, result);
}

// ───── 〔同步〕還原版面時其他電腦的對話還沒進清單（2026-10-29：重開機後其他電腦的對話分頁全部不見） ─────
// init 帶 replicasPending:true、清單裡沒有其他電腦的對話：分頁照原位留著（不送 open、存版面時照樣存），清單到了接上
// （作用中的送 open、背景的切過去才送），宿主說載完（replicasPending:false）仍不在的關掉、不送 close
async function runReplicaRestore(vp) {
  const R1 = '0f3c9a52b7e94d0c8a61f2d4e5b6c7d8';   // 其他電腦的對話：p1 作用中
  const R2 = '1a2b3c4d5e6f40718293a4b5c6d7e8f9';   // 其他電腦的對話：已經不在了（清單載完仍沒有）
  const R3 = '2b3c4d5e6f7a48190a1b2c3d4e5f6a7b';   // 其他電腦的對話：p2 作用中
  const R4 = '3c4d5e6f7a8b49201b2c3d4e5f6a7b8c';   // 只有它一個分頁的窗格，最後不在 → 窗格跟著收掉
  const layout = {
    panes: [{ id: 'p1', tabs: ['c1', R1], active: R1 }, { id: 'p2', tabs: [R2, R3], active: R3 }, { id: 'p3', tabs: [R4], active: R4 }],
    focusedPane: 'p1', sidebarCollapsed: false, view: 'chat',
  };
  const summary = (id, title) => ({ id, accountId: 'a1', provider: 'Claude', title, updatedAt: NOW - 60000, workingDirectory: 'D:/other', customFolder: true,
    running: false, waiting: false, unread: false, deviceId: 'd_01J9Q0000000000000000000OW' });
  const app = await openApp('replica-restore', {
    vp, layout, replicasPending: true,
    hostSetup: (host) => {
      for (const [id, title] of [[R1, '其他電腦的對話一'], [R3, '其他電腦的對話三']]) {
        host.data[id] = { header: F.baseHeader(id, 'a1', title), items: [], hasMore: false, composer: { attachments: [], busy: false, busyText: null, queue: [] } };
      }
    },
  });
  const { page, host } = app;
  const result = {};
  await sleep(500);
  const opensOf = () => host.received.filter((m) => m.cmd === 'open').map((m) => m.conversationId);
  result.waiting = await paneTabs(page);
  result.waitingOpens = opensOf();
  const savedWhileWaiting = host.last('layout');
  result.savedWhileWaiting = savedWhileWaiting && savedWhileWaiting.layout.panes.map((p) => ({ id: p.id, tabs: p.tabs, active: p.active }));
  result.newWhileWaiting = host.received.filter((m) => m.cmd === 'newConversation').length;
  const okWaiting = same(result.waiting, { panes: { p1: { tabs: ['c1', R1], active: R1 }, p2: { tabs: [R2, R3], active: R3 }, p3: { tabs: [R4], active: R4 } }, focused: 'p1' })
    && !result.waitingOpens.some((id) => [R1, R2, R3, R4].includes(id))
    && result.newWhileWaiting === 0
    && (!savedWhileWaiting || same(result.savedWhileWaiting, layout.panes));

  // 引擎交來第一份整份清單：R1、R3 到了，R2、R4 沒有
  host.conversations = host.conversations.concat([summary(R1, '其他電腦的對話一'), summary(R3, '其他電腦的對話三')]);
  host.send({ type: 'conversations', items: host.conversations, replicasPending: false });
  await page.waitForSelector(convSel(R1) + ':not(.loading)', { state: 'attached', timeout: 5000 }).catch(() => {});
  await page.waitForSelector(convSel(R3) + ':not(.loading)', { state: 'attached', timeout: 5000 }).catch(() => {});
  await sleep(500);
  result.settled = await paneTabs(page);
  result.settledOpens = opensOf();
  result.closes = host.received.filter((m) => m.cmd === 'close').map((m) => m.conversationId);
  const saved = host.last('layout');
  result.saved = saved && saved.layout.panes.map((p) => ({ id: p.id, tabs: p.tabs, active: p.active }));
  result.titles = await page.evaluate((ids) => ids.map((id) => {
    const t = document.querySelector('.tab[data-key="' + id + '"] .tab-title');
    return t ? t.textContent : null;
  }), [R1, R3]);
  const okSettled = same(result.settled, { panes: { p1: { tabs: ['c1', R1], active: R1 }, p2: { tabs: [R3], active: R3 } }, focused: 'p1' })
    && result.settledOpens.includes(R1) && result.settledOpens.includes(R3)
    && !result.closes.some((id) => [R2, R4].includes(id))
    && same(result.saved, [{ id: 'p1', tabs: ['c1', R1], active: R1 }, { id: 'p2', tabs: [R3], active: R3 }])
    && same(result.titles, ['其他電腦的對話一', '其他電腦的對話三'])
    && host.received.filter((m) => m.cmd === 'newConversation').length === 0;
  await shot(page, 'replica-restore-' + vp.w);
  await closeApp(app);

  // 舊宿主（沒有 replicasPending）：照以前照清單丟掉，只剩 c1
  const old = await openApp('replica-restore-old', { vp, layout });
  await sleep(500);
  result.oldHost = await paneTabs(old.page);
  const okOld = same(result.oldHost, { panes: { p1: { tabs: ['c1'], active: 'c1' } }, focused: 'p1' });
  await closeApp(old);
  check('replicaRestoreWaitsForList', okWaiting && okSettled && okOld, result);
}

// ───── 淺色主題 ─────
async function runLight(vp) {
  const tag = String(vp.w);
  const app = await openApp('light-' + tag, { vp, layout: { panes: [{ id: 'p1', tabs: ['c1'], active: 'c1' }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' }, settings: { theme: 'light' }, colorScheme: 'light' });
  const { page, host } = app;
  await waitConv(page, 'c1');
  await settle(page, 400);
  // 視窗標題（桌面的視窗標題跟著它）：app 名稱在最前面、後面才接目前對話的標題；切到儀表板是「Clode Myriagent － 儀表板」
  const chatTitle = await page.title();
  await shot(page, `chat-single-bottom-light-${tag}`);
  await page.click(`${convSel('c1')} .it[data-id="i6"] .t-row`);
  await scrollItem(page, 'c1', 'i3');
  await shot(page, `chat-single-tools-light-${tag}`);
  await page.click(`${convSel('c1')} .ch .ibtn[aria-label="檔案瀏覽器"]`);
  await page.waitForSelector(`${convSel('c1')} .drawer.open .fe`);
  await page.click(`${convSel('c1')} .drawer .fe.k-dir:has(.fe-name:text-is("assets"))`);
  await until(() => page.evaluate(() => document.querySelectorAll('.conv[data-conv="c1"] .drawer .fe.k-image').length >= 12), 3000);
  await shot(page, `drawer-grid-light-${tag}`);
  // app 更新的橫幅與設定頁的 Clode Myriagent 區塊（淺色；其他寬度在 runHotUpdate 的淺色那輪補）
  await appUpdateLightShots(page, host, vp.w);
  await page.click('#viewSwitch [data-view="dashboard"]');
  await sleep(1300);
  const dashTitle = await page.title();
  check(`windowTitleAppFirst_${tag}`, chatTitle === 'Clode Myriagent － 修正登入流程卡住並補上單元測試' && dashTitle === 'Clode Myriagent － 儀表板', { chatTitle, dashTitle });
  await shot(page, `dashboard-light-${tag}`);
  await closeApp(app);
}

// ───── 其他互動：改名、選項、資料夾、退回、貼上、拖放、換帳戶、快捷鍵、宿主訊息 ─────
async function runExtras(vp) {
  const tag = String(vp.w);
  const app = await openApp('extras-' + tag, { vp, layout: { panes: [{ id: 'p1', tabs: ['c2'], active: 'c2' }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' } });
  const { page, host } = app;
  await waitConv(page, 'c2');
  const c2 = convSel('c2');
  const ta = `${c2} .cinput`;

  // 窗格內拖曳分頁重新排序、中鍵關分頁
  await page.click('.crow[data-key="c:c5"]');
  await waitConv(page, 'c5');
  await page.click('.crow[data-key="c:c6"]');
  await waitConv(page, 'c6');
  await page.dragAndDrop('.tab[data-key="c6"]', '.tab[data-key="c2"]', { targetPosition: { x: 6, y: 12 } });
  const reo = await until(() => { const l = host.last('layout'); return l && l.layout.panes[0].tabs.join(',') === 'c6,c2,c5' ? l : null; }, 2500);
  check('tabReorderInPane', !!reo, reo ? reo.layout.panes[0] : host.last('layout'));
  await page.click('.tab[data-key="c6"]', { button: 'middle' });
  const mc6 = await until(() => host.last('close', (m) => m.conversationId === 'c6'), 2000);
  await page.click('.tab[data-key="c5"]', { button: 'middle' });
  const mc5 = await until(() => host.last('close', (m) => m.conversationId === 'c5'), 2000);
  check('middleClickClosesTab', !!mc6 && !!mc5, { c6: !!mc6, c5: !!mc5 });
  await page.click('.tab[data-key="c2"]');

  // 畫面上顯示的對話（宿主據此判斷未讀與通知）：切分頁時送 visible、內容沒變不重送；視窗取得焦點時一律重送
  const visBack = await until(() => { const v = host.last('visible'); return v && v.conversationIds.join(',') === 'c2' ? v : null; }, 2000);
  const visSeq = host.received.filter((m) => m.cmd === 'visible').map((m) => m.conversationIds.join(','));
  const visCount = visSeq.length;
  await page.evaluate(() => window.dispatchEvent(new Event('focus')));
  const visFocus = await until(() => host.received.filter((m) => m.cmd === 'visible').length > visCount ? host.last('visible') : null, 2000);
  check('visibleReportsShownTabs', !!visBack && visSeq.indexOf('c2') >= 0 && visSeq.indexOf('c2') < visSeq.indexOf('c5') && visSeq.indexOf('c5') < visSeq.indexOf('c6')
    && !!visFocus && visFocus.conversationIds.join(',') === 'c2',
    { sequence: visSeq, onFocus: visFocus && visFocus.conversationIds });
  // 視窗最小化（宿主把 WebView2 設成看不見，document.hidden）：桌面送空的 visible（宿主照背景分頁節流、這段時間完成的標未讀），還原時重送
  const setHidden = (hidden) => page.evaluate((v) => {
    Object.defineProperty(document, 'hidden', { configurable: true, get: () => v });
    document.dispatchEvent(new Event('visibilitychange'));
  }, hidden);
  const hiddenFrom = host.received.length;
  await setHidden(true);
  const visMin = await until(() => host.received.slice(hiddenFrom).find((m) => m.cmd === 'visible') || null, 2000);
  const restoreFrom = host.received.length;
  await setHidden(false);
  const visRestore = await until(() => host.received.slice(restoreFrom).find((m) => m.cmd === 'visible') || null, 2000);
  await page.evaluate(() => { delete document.hidden; });
  check('visibleEmptyWhileMinimized', !!visMin && visMin.conversationIds.length === 0 && !!visRestore && visRestore.conversationIds.join(',') === 'c2',
    { minimized: visMin && visMin.conversationIds, restored: visRestore && visRestore.conversationIds });

  // 側欄帳戶卡拖曳排序（拖到 Codex Pro 下方）
  const a3Height = await page.$eval('.macct[data-key="a3"]', (n) => n.getBoundingClientRect().height);
  await page.dragAndDrop('.macct[data-key="a1"]', '.macct[data-key="a3"]', { targetPosition: { x: 30, y: a3Height - 8 } });
  const ro = await until(() => host.last('accountReorder'), 2000);
  check('accountReorderDrag', !!ro && ro.ids.join(',') === 'a2,a3,a1,a4,a5,a6', ro);

  // 標題雙擊改名
  await page.dblclick(`${c2} .ch-title`);
  await page.fill(`${c2} .ch-rename`, 'Codex 用量欄位（改名）');
  await page.keyboard.press('Enter');
  const ren = await until(() => host.last('rename', (m) => m.conversationId === 'c2'), 2000);
  await sleep(200);
  const tabTitle = await page.textContent('.tab[data-key="c2"] .tab-title');
  check('renameFromHeader', !!ren && ren.title === 'Codex 用量欄位（改名）' && tabTitle === 'Codex 用量欄位（改名）', { rename: ren, tabTitle });

  // 標題右鍵：重新命名、自動重新命名（autoTitle）；產生期間（header.titling）標題後面轉圈、選單項目停用
  await page.click(`${c2} .ch-title-text`, { button: 'right' });
  await page.waitForSelector('.menu');
  await shot(page, `title-menu-${tag}`);
  const titleMenu = await page.$$eval('.menu .menu-item', (ns) => ns.map((n) => ({ label: n.querySelector('.mi-label').textContent, disabled: n.disabled })));
  await page.click('.menu .menu-item:has(.mi-label:text-is("自動重新命名"))');
  const autoT = await until(() => host.last('autoTitle', (m) => m.conversationId === 'c2'), 2000);
  const c2Data = host.conv('c2');
  c2Data.header = Object.assign({}, c2Data.header, { titling: true });
  host.pushHeader('c2');
  await sleep(200);
  const spinning = await page.$eval(`${c2} .ch-title-spin`, (n) => !n.hidden && n.getBoundingClientRect().width > 0);
  await page.click(`${c2} .ch-title-text`, { button: 'right' });
  await page.waitForSelector('.menu');
  const busyItem = await page.$eval('.menu .menu-item:has(.mi-label:text-is("正在產生標題…"))', (n) => n.disabled).catch(() => null);
  await page.keyboard.press('Escape');
  c2Data.header = Object.assign({}, c2Data.header, { titling: false, title: 'Codex 用量欄位解析' });
  host.pushHeader('c2');
  await sleep(200);
  const after = await page.$eval(`${c2} .ch-title`, (n) => ({ text: n.querySelector('.ch-title-text').textContent, spin: !n.querySelector('.ch-title-spin').hidden }));
  check('titleContextMenuAutoTitle', titleMenu.map((x) => x.label).join('/') === '重新命名/自動重新命名' && !!autoT && spinning && busyItem === true
    && after.text === 'Codex 用量欄位解析' && !after.spin, { titleMenu, autoTitle: autoT, spinning, busyItem, after });

  // 標題後面的自動重新命名圖示：緊跟在標題後面（同一行、不超過 8px），點了直接送 autoTitle（不開選單）；
  // 產生期間藏起來、改成轉圈，結果回來後又出現。看不看得到以 getComputedStyle 與大小判斷
  const autoBtn = () => page.$eval(c2, (root) => {
    const b = root.querySelector('.ch-top > .ch-title-auto');
    if (!b) return null;
    const r = b.getBoundingClientRect();
    const t = root.querySelector('.ch-top > .ch-title').getBoundingClientRect();
    return {
      shown: getComputedStyle(b).display !== 'none' && r.width > 0, gap: Math.round(r.left - t.right), sameRow: Math.abs((r.top + r.bottom) / 2 - (t.top + t.bottom) / 2) <= 2,
      label: b.getAttribute('aria-label'), spin: !root.querySelector('.ch-title-spin').hidden,
    };
  });
  const btnIdle = await autoBtn();
  const autoCount = () => host.received.filter((m) => m.cmd === 'autoTitle' && m.conversationId === 'c2').length;
  const autoBefore = autoCount();
  if (btnIdle && btnIdle.shown) await page.click(`${c2} .ch-top > .ch-title-auto`);
  const autoClicked = await until(() => (autoCount() > autoBefore ? true : null), 2000);
  const menuAfterClick = !!(await page.$('.menu'));
  c2Data.header = Object.assign({}, c2Data.header, { titling: true });
  host.pushHeader('c2');
  await sleep(200);
  const btnBusy = await autoBtn();
  c2Data.header = Object.assign({}, c2Data.header, { titling: false, title: 'Codex 用量欄位解析' });
  host.pushHeader('c2');
  await sleep(200);
  const btnBack = await autoBtn();
  check('titleAutoTitleButton', !!btnIdle && btnIdle.shown && btnIdle.gap >= 0 && btnIdle.gap <= 8 && btnIdle.sameRow && btnIdle.label.startsWith('自動重新命名')
    && !!autoClicked && autoCount() === autoBefore + 1 && !menuAfterClick && !!btnBusy && !btnBusy.shown && btnBusy.spin && !!btnBack && btnBack.shown && !btnBack.spin,
  { btnIdle, clicked: !!autoClicked, menuAfterClick, btnBusy, btnBack });

  // 標題列兩行：第一行標題與按鈕，第二行帳戶與工作資料夾晶片；標題放不下時是跑馬燈（只動 transform、滑鼠移上去暫停），
  // 放得下時不捲；系統要求減少動態時不捲、照舊以「…」截斷
  const headLayout = () => page.$eval(c2, (root) => {
    const rect = (el) => el.getBoundingClientRect();
    const title = rect(root.querySelector('.ch-top .ch-title')), more = rect(root.querySelector('.ch-top .ibtn[aria-label="更多動作"]'));
    const acct = rect(root.querySelector('.ch-sub .ch-acct')), folder = rect(root.querySelector('.ch-sub .ch-folder'));
    const box = root.querySelector('.ch-title-text');
    const run = box.querySelector('.ch-title-run');
    const anim = run.getAnimations().find((a) => a.animationName === 'ch-marquee');
    return {
      twoRows: acct.top >= title.bottom - 1 && folder.top >= title.bottom - 1 && Math.abs(acct.top - folder.top) < 1 && Math.abs((more.top + more.bottom) - (title.top + title.bottom)) < 4,
      height: Math.round(rect(root.querySelector('.ch')).height), text: box.textContent,
      marquee: box.classList.contains('marquee'), anim: anim ? anim.playState : null, dist: parseFloat(box.style.getPropertyValue('--mq-dist')) || 0,
      overflow: run.offsetWidth - box.clientWidth, textOverflow: getComputedStyle(box).textOverflow,
    };
  });
  await page.mouse.move(5, 5); // 上面右鍵點過標題：滑鼠移開，跑馬燈才不是暫停的
  const shortHead = await headLayout();
  const longTitle = '幫我分析最近三個月的銷售數據與各通路的廣告成效數據，研究哪些通路的轉換率最高、哪些素材的點擊率下滑、退貨率和客單價有沒有關係，並整理成下週業務會議要用的簡報大綱與建議的預算分配';
  c2Data.header = Object.assign({}, c2Data.header, { title: longTitle });
  host.pushHeader('c2');
  await sleep(300);
  const longHead = await headLayout();
  await page.locator(`${c2} .ch`).screenshot({ path: path.join(OUT, `header-marquee-${tag}.png`) });
  report.screenshots.push(`header-marquee-${tag}.png`);
  await page.hover(`${c2} .ch-title-text`);
  await sleep(100);
  const hovered = await headLayout();
  await page.mouse.move(5, 5);
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await sleep(100);
  const calmHead = await headLayout();
  await page.emulateMedia({ reducedMotion: 'no-preference' });
  c2Data.header = Object.assign({}, c2Data.header, { title: 'Codex 用量欄位解析' });
  host.pushHeader('c2');
  await sleep(300);
  const backHead = await headLayout();
  check('headerTwoRowsMarquee', shortHead.twoRows && !shortHead.marquee && !shortHead.anim
    && longHead.twoRows && longHead.text === longTitle && longHead.marquee && longHead.anim === 'running' && longHead.dist < 0 && Math.abs(longHead.dist + longHead.overflow) <= 1
    && hovered.anim === 'paused' && !calmHead.anim && calmHead.textOverflow === 'ellipsis' && !backHead.marquee && !backHead.anim && backHead.text === 'Codex 用量欄位解析',
  { shortHead, longHead, hovered, calmHead, backHead });

  // 模型與推理強度
  await page.click(`${c2} .pick[title^="模型"]`);
  await page.click('.menu .menu-item:has(.mi-label:text-is("GPT-6 Astra"))');
  const mdl = await until(() => host.last('setOption', (m) => m.key === 'model'), 2000);
  await sleep(150);
  await page.click(`${c2} .pick[title^="推理強度"]`);
  await shot(page, `effort-menu-${tag}`);
  await page.click('.menu .menu-item:has(.mi-label:text-is("極限"))');
  const eff = await until(() => host.last('setOption', (m) => m.key === 'effort'), 2000);
  check('modelEffortOptions', !!mdl && mdl.value === 'gpt-6-astra' && !!eff && eff.value === 'ultra', { model: mdl, effort: eff });

  // 工作資料夾選單：最近使用
  await page.click(`${c2} .ch-folder`);
  await page.waitForSelector('.menu.menu-folder');
  await shot(page, `folder-menu-${tag}`);
  await page.click('.menu.menu-folder .menu-item:has(.mi-label:text-is("SampleShop"))');
  const sf = await until(() => host.last('setFolder'), 2000);
  check('folderMenuSetFolder', !!sf && sf.path === 'C:\\repo\\SampleShop' && sf.conversationId === 'c2', sf);

  // sendRejected：輸入框是空的才還原
  await page.fill(ta, '');
  await host.send({ type: 'sendRejected', conversationId: 'c2', text: '被退回的訊息' });
  await sleep(150);
  const restored = await page.inputValue(ta);
  await page.fill(ta, '正在打的字');
  await host.send({ type: 'sendRejected', conversationId: 'c2', text: '不該蓋掉' });
  await sleep(150);
  const kept = await page.inputValue(ta);
  check('sendRejectedRestores', restored === '被退回的訊息' && kept === '正在打的字', { restored, kept });
  await page.fill(ta, '');

  // 佇列暫停（上一輪停止或失敗、沒有在執行）：送出鈕可按，空白送出＝從佇列第一則續送
  const pausedQueue = [{ id: 'qp1', text: '接著補上 README 的安裝步驟' }, { id: 'qp2', text: '最後跑一次 dotnet test' }];
  host.data.c2.composer = { attachments: [], busy: false, busyText: null, queue: pausedQueue.slice() };
  await host.send({ type: 'composer', conversationId: 'c2', composer: host.data.c2.composer });
  await sleep(150);
  const qHead = await page.textContent(`${c2} .queue-head`);
  const sendEnabled = await page.isEnabled(`${c2} .cbtn.send`);
  await shot(page, `queue-paused-${tag}`);
  await page.click(`${c2} .cbtn.send`);
  const resumed = await until(() => host.last('send', (m) => m.conversationId === 'c2'), 2000);
  await until(() => page.evaluate(() => !!Array.from(document.querySelectorAll('.conv[data-conv="c2"] .it-user .u-text')).find((x) => x.textContent.includes('接著補上 README'))), 2000);
  check('queueResumeWhenPaused', /佇列已暫停，按送出繼續/.test(qHead) && sendEnabled && !!resumed && resumed.text === '',
    { queueHead: qHead, sendEnabled, send: resumed });
  await sleep(600); // 等假宿主模擬的這一輪結束
  host.data.c2.composer = { attachments: [], busy: false, busyText: null, queue: [] };
  await host.send({ type: 'composer', conversationId: 'c2', composer: host.data.c2.composer });
  await sleep(100);

  // 貼上圖片＝pasteImage；拖放檔案＝addFiles（postMessageWithAdditionalObjects）
  await page.evaluate(() => {
    const t = document.querySelector('.conv[data-conv="c2"] .cinput');
    const dt = new DataTransfer();
    dt.items.add(new File([new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13])], 'paste.png', { type: 'image/png' }));
    t.dispatchEvent(new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true }));
  });
  const pasted = await until(() => host.last('pasteImage'), 2000);
  check('pasteImage', !!pasted && pasted.conversationId === 'c2' && /^data:image\/png;base64,/.test(pasted.dataUrl) && pasted.name === 'paste.png', pasted && { name: pasted.name, prefix: pasted.dataUrl.slice(0, 30) });
  await page.evaluate(() => {
    const root = document.querySelector('.conv[data-conv="c2"]');
    const dt = new DataTransfer();
    dt.items.add(new File(['hello'], '會議筆記.txt', { type: 'text/plain' }));
    root.dispatchEvent(new DragEvent('dragenter', { dataTransfer: dt, bubbles: true, cancelable: true }));
    root.dispatchEvent(new DragEvent('dragover', { dataTransfer: dt, bubbles: true, cancelable: true }));
    window.__dropDt = dt;
  });
  await sleep(150);
  await shot(page, `drop-overlay-${tag}`);
  await page.evaluate(() => { document.querySelector('.conv[data-conv="c2"] .scroller').dispatchEvent(new DragEvent('drop', { dataTransfer: window.__dropDt, bubbles: true, cancelable: true })); });
  const dropped = await until(() => host.last('addFiles'), 2000);
  const overlayGone = !(await visible(page, `${c2} .drop`));
  check('dropFilesAddFiles', !!dropped && dropped.conversationId === 'c2' && dropped.__files && dropped.__files[0].name === '會議筆記.txt' && overlayGone, { addFiles: dropped, overlayGone });
  await sleep(200);
  await shot(page, `composer-attachments-${tag}`);

  // 跨代理換帳戶：先確認，再送 setAccount
  await page.click(`${c2} .ch-acct`);
  await page.waitForSelector('.menu');
  await shot(page, `account-menu-${tag}`);
  await page.click('.menu .menu-item:has(.mi-label:text-is("工作用 Max"))');
  await page.waitForSelector('.dlg-confirm');
  await page.click('.dlg-confirm .btn.primary');
  const sa = await until(() => host.last('setAccount', (m) => m.conversationId === 'c2'), 2000);
  await sleep(300);
  const chip = await page.textContent(`${c2} .ch-acct .chip-label`);
  check('setAccountCrossProvider', !!sa && sa.accountId === 'a1' && chip === '工作用 Max', { setAccount: sa, chip });

  // 鍵盤快捷鍵
  await page.focus(ta);
  const before = host.received.length;
  await page.keyboard.press('Control+n');
  const nc = await until(() => host.received.slice(before).find((m) => m.cmd === 'newConversation'), 2000);
  await page.waitForSelector('.pane[data-pane="p1"] .tab:nth-child(2)');
  await sleep(250);
  await shot(page, `chat-draft-${tag}`);
  await page.keyboard.press('Control+Tab');
  await sleep(150);
  const activeAfterTab = await page.getAttribute('.pane[data-pane="p1"] .tab.active', 'data-key');
  await page.keyboard.press('Control+Tab');
  await sleep(100);
  const draftId = await page.getAttribute('.pane[data-pane="p1"] .tab.active', 'data-key');
  await page.keyboard.press('Control+w');
  const closed = await until(() => host.last('close', (m) => m.conversationId === draftId), 2000);
  await page.keyboard.press('Control+Backslash');
  const split = await until(() => host.received.slice(before).find((m) => m.cmd === 'newConversation' && m.paneId === 'p2'), 2000);
  await page.waitForSelector('.pane[data-pane="p2"] .conv:not(.loading)');
  await page.keyboard.press('Control+b');
  const coll = await until(() => { const l = host.last('layout'); return l && l.layout.sidebarCollapsed === true ? l : null; }, 2000);
  const sbHidden = !(await visible(page, '#sidebar'));
  await shot(page, `sidebar-collapsed-${tag}`);
  await page.keyboard.press('Control+b');
  await sleep(100);
  const sbBack = await visible(page, '#sidebar');
  check('keyboardShortcuts', !!nc && nc.accountId === 'a1' && activeAfterTab === 'c2' && /^d\d+$/.test(draftId || '') && !!closed && !!split && !!coll && sbHidden && sbBack,
    { ctrlN: nc, ctrlTab: activeAfterTab, ctrlW: closed, ctrlBackslash: split, ctrlB: !!coll, sbHidden, sbBack });

  // focusConversation：沒開就送 open 並切過去
  await host.send({ type: 'focusConversation', conversationId: 'c4' });
  const fo = await until(() => host.last('open', (m) => m.conversationId === 'c4'), 2000);
  await waitConv(page, 'c4');
  const c4Active = await page.evaluate(() => !!document.querySelector('.tab.active[data-key="c4"]'));
  check('focusConversationOpens', !!fo && c4Active, { open: fo, c4Active });

  // 語音訊息（桌面這台的對話）：按麥克風開始錄音（紅色、計時、取消鈕），再按一下送 voiceMessage {conversationId, dataUrl}（WebM／Opus 32 kbps 的 base64），
  // 麥克風關掉；Esc 取消不送；太短（誤按）不送並提示；不碰這台真的麥克風（fake-mic.mjs）
  const micSel = `${convSel('c4')} .cbtn.mic`;
  const micState = () => page.evaluate((s) => {
    const b = document.querySelector(s);
    const w = b.closest('.mic-wrap');
    const r = window.__mic.recorders.at(-1);
    return { shown: getComputedStyle(b).display !== 'none', disabled: b.disabled, listening: b.classList.contains('listening'), title: b.title,
      time: w.querySelector('.mic-time').hidden ? null : w.querySelector('.mic-time').textContent, cancel: !w.querySelector('.mic-cancel').hidden,
      requests: window.__mic.requests, stopped: window.__mic.tracksStopped, rec: r ? { mime: r.mimeType, bps: r.opts && r.opts.audioBitsPerSecond, state: r.state } : null };
  }, micSel);
  const voiceCount = () => host.received.filter((m) => m.cmd === 'voiceMessage').length;
  const mic0 = await micState();
  await page.click(micSel);
  const micRec = await until(async () => { const st = await micState(); return st.listening && st.time ? st : null; }, 2000);
  await shot(page, 'desktop-voice-recording');
  await sleep(800);
  await page.click(micSel);
  const vm = await until(() => host.last('voiceMessage'), 3000);
  const micAfter = await micState();
  await page.click(micSel);
  await until(async () => (await micState()).listening, 2000);
  await sleep(700);
  await page.keyboard.press('Escape');
  await sleep(300);
  const micCancelled = await micState();
  const afterCancel = voiceCount();
  const toastsBefore = await page.$$eval('.toast', (n) => n.length);
  await page.click(micSel);
  await until(async () => (await micState()).listening, 2000);
  await page.click(micSel);
  await sleep(300);
  const tooShortToast = await page.$$eval('.toast', (n) => n.map((t) => t.textContent));
  check('desktopVoiceMessage', mic0.shown && !mic0.disabled && !!micRec && micRec.cancel && micRec.time === '0:00' && /錄音中/.test(micRec.title)
    && !!vm && vm.conversationId === 'c4' && /^data:audio\/webm;base64,/.test(vm.dataUrl) && !micAfter.listening && micAfter.stopped >= 1
    && !!micAfter.rec && micAfter.rec.mime === 'audio/webm;codecs=opus' && micAfter.rec.bps === 32000
    && !micCancelled.listening && afterCancel === 1 && voiceCount() === 1 && tooShortToast.length > toastsBefore && tooShortToast.some((t) => /錄音太短/.test(t)),
  { mic0, micRec, voiceMessage: vm && { conversationId: vm.conversationId, dataUrl: (vm.dataUrl || '').slice(0, 40) }, micAfter, micCancelled, afterCancel, total: voiceCount(), tooShortToast });

  // flash（要你回來看：完成、失敗、提問、核准、計畫；attnflash.js）：開著的對話分頁、對話標頭與側欄那一列一直閃，沒開著的只有側欄；
  // 不切換分頁、過了 2.4 秒還在閃；speaking（語音開始念它）讓標頭另外閃三下；點了那個對話才停、其他的照閃，並告訴其他視窗（BroadcastChannel）；
  // 其他視窗說點過了也停
  await page.evaluate(() => {
    window.__attnHeard = [];
    window.__attnPeer = new BroadcastChannel('myriagent.attn-flash'); // 扮演另一個視窗
    window.__attnPeer.onmessage = (e) => window.__attnHeard.push(e.data);
  });
  await host.send({ type: 'flash', conversationId: 'c4' });
  await host.send({ type: 'flash', conversationId: 'c5' });
  await sleep(150);
  const flashState = () => page.evaluate(() => {
    const anim = (sel) => { const el = document.querySelector(sel); return el ? getComputedStyle(el, '::before').animationName : null; };
    return {
      tab: !!document.querySelector('.tab.attn-flash[data-key="c4"]'),
      head: !!document.querySelector('.conv[data-conv="c4"] > .ch.attn-flash'),
      row: !!document.querySelector('.crow.attn-flash[data-key="c:c4"]'),
      closedRow: !!document.querySelector('.crow.attn-flash[data-key="c:c5"]'),
      closedTab: !!document.querySelector('.tab[data-key="c5"]'),
      anims: [anim('.tab.attn-flash[data-key="c4"] .tab-title'), anim('.crow.attn-flash[data-key="c:c4"]'), anim('.conv[data-conv="c4"] > .ch.attn-flash')],
      c4StillActive: !!document.querySelector('.tab.active[data-key="c4"]'), // 不切換分頁（畫面有兩個窗格，各有自己的作用中分頁）
      count: document.querySelectorAll('.attn-flash').length,
    };
  });
  const flashing = await flashState();
  await shot(page, `attn-flash-${tag}`);
  await sleep(2400);
  const flashLater = await flashState();
  await host.send({ type: 'flash', conversationId: 'c4', speaking: true });
  await sleep(100);
  const burst = await page.evaluate(() => !!document.querySelector('.conv[data-conv="c4"] > .ch.ch-flash.attn-flash'));
  await sleep(1500);
  await page.click(`${convSel('c4')} .ch-top`, { position: { x: 4, y: 4 } }); // 點那個對話（標題列左上角的留白）
  await sleep(100);
  const afterClick = await flashState();
  const heard = await page.evaluate(() => window.__attnHeard.slice());
  await page.evaluate(() => window.__attnPeer.postMessage({ stop: 'c5' }));
  const peerStopped = await until(() => page.evaluate(() => document.querySelectorAll('.attn-flash').length === 0), 1000);
  await page.evaluate(() => window.__attnPeer.close());
  check('attnFlash', flashing.tab && flashing.head && flashing.row && flashing.closedRow && !flashing.closedTab && flashing.c4StillActive
    && flashing.anims.every((a) => a === 'attn-flash') && flashLater.count === flashing.count && flashLater.tab && flashLater.head && flashLater.closedRow
    && burst && !afterClick.tab && !afterClick.head && !afterClick.row && afterClick.closedRow && afterClick.count === 1
    && JSON.stringify(heard) === JSON.stringify([{ stop: 'c4' }]) && !!peerStopped,
  { flashing, flashLater, burst, afterClick, heard, peerStopped: !!peerStopped });

  // 刪除（自訂確認框）→ 宿主回 conversationClosed → 分頁關閉
  await page.click(`${convSel('c4')} .ch .ibtn[aria-label="更多動作"]`);
  await page.click('.menu .menu-item:has(.mi-label:text-is("刪除對話…"))');
  await page.waitForSelector('.dlg-confirm');
  await shot(page, `delete-confirm-${tag}`);
  await page.click('.dlg-confirm .btn.danger-solid');
  const del = await until(() => host.last('delete', (m) => m.conversationId === 'c4'), 2000);
  const gone = await until(() => page.evaluate(() => !document.querySelector('.tab[data-key="c4"]') && !document.querySelector('.crow[data-key="c:c4"]')), 2000);
  check('deleteClosesTab', !!del && !!gone, { delete: del, tabAndRowGone: !!gone });

  // 宿主送 toast、登入完成
  await host.send({ type: 'toast', text: '已複製到剪貼簿', level: 'success' });
  await host.send({ type: 'accountLogin', accountId: 'a4', message: 'Codex 團隊已登入', done: true, success: true });
  await sleep(300);
  const toasts = await page.$$eval('.toast .toast-text', (n) => n.map((x) => x.textContent));
  check('hostToastAndLoginDone', toasts.includes('已複製到剪貼簿') && toasts.includes('Codex 團隊已登入'), toasts);
  await shot(page, `toasts-${tag}`);

  // 帳戶已被移除的對話
  await page.click('.crow[data-key="c:c13"]');
  await waitConv(page, 'c13');
  await sleep(200);
  const warn = await visible(page, `${convSel('c13')} .ch-warn`);
  check('accountMissingWarning', warn, warn);
  await shot(page, `chat-account-missing-${tag}`);

  // 串流壓力：200 次更新同一則回覆（比宿主每 50ms 合併一次密得多），期間不應出現 long task（>50ms）
  await page.evaluate(() => {
    window.__lt = [];
    new PerformanceObserver((l) => { for (const e of l.getEntries()) window.__lt.push(Math.round(e.duration)); }).observe({ type: 'longtask' });
  });
  const t0 = Date.now();
  const para = '<p>串流測試：把縮圖產生搬到背景執行緒，<code>ThumbnailService</code> 改成佇列，並加上 LRU 快取。</p>';
  for (let i = 1; i <= 200; i++) {
    const html = para.repeat(Math.ceil(i / 10)) + (i > 60 ? '<pre><code class="language-csharp">var cache = new LruCache&lt;string, Bitmap&gt;(64 * 1024 * 1024);\n</code></pre>' : '') +
      (i > 120 ? '<table><thead><tr><th>項目</th><th>前</th><th>後</th></tr></thead><tbody><tr><td>首次載入</td><td>2.1 秒</td><td>0.3 秒</td></tr></tbody></table>' : '');
    host.send({ type: 'item', conversationId: 'c13', item: { id: 'stream1', kind: 'Assistant', status: i < 200 ? 'Running' : 'Done', time: Date.now(), html } });
  }
  await host.chain;
  await sleep(400);
  const elapsed = Date.now() - t0;
  const perf = await page.evaluate(() => ({
    longTasks: window.__lt,
    codeBlocks: document.querySelectorAll('.conv[data-conv="c13"] .it[data-id="stream1"] .code').length,
    domNodes: document.getElementsByTagName('*').length,
  }));
  check('streamingNoLongTasks', perf.longTasks.filter((d) => d > 50).length === 0 && perf.codeBlocks === 1, Object.assign({ updates: 200, elapsedMs: elapsed }, perf));
  await closeApp(app);
}

// ───── 視覺與版面：文字對比、焦點框、分頁溢出、篩選籤、2×2 輸入區、提示選單、抽屜拉寬、最小寬度儀表板 ─────
const onePane = (id, view = 'chat') => ({ panes: [{ id: 'p1', tabs: [id], active: id }], focusedPane: 'p1', sidebarCollapsed: false, view });
const FOUR_PANES = { panes: [{ id: 'p1', tabs: ['c1'], active: 'c1' }, { id: 'p2', tabs: ['c3'], active: 'c3' }, { id: 'p3', tabs: ['c4'], active: 'c4' }, { id: 'p4', tabs: ['c7'], active: 'c7' }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' };

/** 在頁面內算 WCAG 對比：前景色（乘上祖先的 opacity）對往上疊好的背景色；找不到元素回 null */
function contrastOf(sel) {
  const parse = (s) => {
    let m = /^rgba?\(([^)]+)\)$/.exec(s);
    if (m) { const p = m[1].split(/[ ,/]+/).filter(Boolean).map(Number); return [p[0], p[1], p[2], p.length > 3 ? p[3] : 1]; }
    m = /^color\(srgb ([^)]+)\)$/.exec(s);
    if (m) { const p = m[1].split(/[ /]+/).filter(Boolean).map(Number); return [p[0] * 255, p[1] * 255, p[2] * 255, p.length > 3 ? p[3] : 1]; }
    // 其他寫法（oklch(…)、相對色語法算出來的、color-mix）：畫到 1×1 的 canvas 讀回 sRGB（2026-10-25：側欄 token 數依帳戶色算出 oklch）
    if (s && s !== 'transparent') {
      const cv = contrastOf.cv || (contrastOf.cv = document.createElement('canvas').getContext('2d', { willReadFrequently: true }));
      cv.clearRect(0, 0, 1, 1);
      cv.fillStyle = 'rgba(0, 0, 0, 0)';
      cv.fillStyle = s;
      cv.fillRect(0, 0, 1, 1);
      const d = cv.getImageData(0, 0, 1, 1).data;
      return [d[0], d[1], d[2], d[3] / 255];
    }
    return [0, 0, 0, 0];
  };
  const over = (t, b) => [0, 1, 2].map((i) => t[i] * t[3] + b[i] * (1 - t[3])).concat(1);
  const lin = (c) => { c /= 255; return c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
  const lum = (c) => 0.2126 * lin(c[0]) + 0.7152 * lin(c[1]) + 0.0722 * lin(c[2]);
  const el = Array.from(document.querySelectorAll(sel)).find((e) => e.getBoundingClientRect().width > 0);
  if (!el) return null;
  const chain = [];
  for (let n = el; n; n = n.parentElement) chain.push(n);
  let bg = [255, 255, 255, 1], op = 1;
  for (let i = chain.length - 1; i >= 0; i--) {
    const cs = getComputedStyle(chain[i]);
    const c = parse(cs.backgroundColor);
    if (c[3] > 0) bg = over(c, bg);
    op *= Number(cs.opacity);
  }
  const f = parse(getComputedStyle(el).color);
  const fg = over([f[0], f[1], f[2], f[3] * op], bg);
  const l1 = lum(fg), l2 = lum(bg);
  return Math.round((Math.max(l1, l2) + 0.05) / (Math.min(l1, l2) + 0.05) * 100) / 100;
}

// ───── 側欄的 token 計數（tokenfx.js、tokenrules.js）─────
/**
 * 每張帳戶卡：token 計數看不看得到、在不在狀態那一行、in 與 out 的字（含亂碼）與標籤、亂碼格數、增量；狀態文字、背景的方案名稱、卡片高度；
 * 放不放得下（fit：數字沒有超出狀態那一行、沒有蓋到過時標示與裝置小點、in 與 out 不重疊）
 */
function tokenRows() {
  const shown = (el) => { if (!el) return false; const cs = getComputedStyle(el); const r = el.getBoundingClientRect(); return cs.display !== 'none' && cs.visibility !== 'hidden' && r.width > 0 && r.height > 0; };
  return Array.from(document.querySelectorAll('#sidebar .macct')).map((card) => {
    const row = card.querySelector('.macct-tok');
    const nums = row ? Array.from(row.querySelectorAll('.mtok-n')) : [];
    const textOf = (n) => (n ? Array.from(n.querySelectorAll('.mtok-c')).map((c) => c.textContent).join('') : null);
    let fit = null;
    if (shown(row)) {
      const sub = card.querySelector('.macct-sub').getBoundingClientRect();
      const r = nums.map((n) => n.getBoundingClientRect());
      const after = Array.from(card.querySelectorAll('.macct-stale, .macct-devs')).filter(shown).map((el) => el.getBoundingClientRect().left);
      const right = Math.max(...r.map((x) => x.right));
      const box = row.getBoundingClientRect();
      // 沒被裁切：最後一個看得到的子元素在這一行之內（裁切只是最後的保險，正常的寬度不該用到）
      const kids = Array.from(row.children).filter(shown).map((el) => el.getBoundingClientRect());
      fit = { right: Math.round(right), subRight: Math.round(sub.right), nextLeft: after.length ? Math.round(Math.min(...after)) : null,
        width: Math.round(box.width), content: Math.round(Math.max(...kids.map((k) => k.right)) - box.left),
        ok: r.length === 2 && r[0].right <= r[1].left && right <= sub.right + 0.5 && after.every((left) => right <= left + 0.5),
        clipped: Math.max(...kids.map((k) => k.right)) > box.right + 0.5 };
    }
    return {
      key: card.dataset.key, shown: shown(row), inSubRow: !!row && row.parentElement.classList.contains('macct-sub'),
      in: textOf(nums[0]), out: textOf(nums[1]), fit, rowText: row ? row.textContent : '',
      // 看得到的標籤（in／out，放不下時收起）與斜線
      tags: row ? Array.from(row.querySelectorAll('.mtok-tag')).filter(shown).map((t) => t.textContent) : [], sep: shown(row && row.querySelector('.mtok-sep')),
      scrambling: row ? row.querySelectorAll('.mtok-c.scr').length : 0,
      delta: !!card.querySelector('.mtok-d'),
      title: row ? row.title : null,
      subText: shown(card.querySelector('.macct-sub-text')) ? card.querySelector('.macct-sub-text').textContent : null,
      mark: !!card.querySelector('.macct-mark, .macct-mark-text'),
      height: Math.round(card.getBoundingClientRect().height),
      canvas: !!card.querySelector('canvas'),
    };
  });
}

// ───── 側欄帳戶名稱的跑馬燈（sidebar.js 的 fitNames）：放不下時來回捲動（只動 transform、滑過卡片照常捲），
// 名稱改短或放得下時不捲；減少動態、設定關掉動態效果時不捲、照舊以「…」截斷 ─────
async function runAccountNameMarquee(vp) {
  const longLabel = '公司專案用的 Claude Max 訂閱（行銷與客服部門共用）';
  const names = (page) => page.evaluate(() => Object.fromEntries(Array.from(document.querySelectorAll('#sidebar .macct')).map((c) => {
    const box = c.querySelector('.macct-name');
    const run = box.querySelector('.macct-name-run');
    const anim = run && run.getAnimations().find((x) => x.animationName === 'ch-marquee');
    const m = run ? new DOMMatrixReadOnly(getComputedStyle(run).transform) : null;
    return [c.dataset.key, { text: box.textContent, marquee: box.classList.contains('marquee'), anim: anim ? anim.playState : null,
      dist: parseFloat(box.style.getPropertyValue('--mq-dist')) || 0, overflow: run ? run.offsetWidth - box.clientWidth : null,
      textOverflow: getComputedStyle(box).textOverflow, shiftX: m ? Math.round(m.m41) : null, boxWidth: box.clientWidth }];
  })));
  const setup = (h) => { h.accounts.find((a) => a.id === 'a2').label = longLabel; };
  const app = await openApp('acct-marquee', { vp, layout: MAIN_LAYOUT, hostSetup: setup });
  const { page, host } = app;
  await settle(page, 400);
  const n0 = await names(page);
  // 兩端各停 14%（--mq-dur 至少 5 秒，停 0.7 秒以上）：等 2.5 秒一定已經在捲
  await sleep(2500);
  const n1 = await names(page);
  await page.locator('#sidebar .sb-sec-accts').screenshot({ path: path.join(OUT, 'acct-marquee-dark.png') });
  report.screenshots.push('acct-marquee-dark.png');
  // 滑過卡片：＋出現、名稱的框變窄，照常捲，距離跟著重算
  await page.hover('#sidebar .macct[data-key="a2"] .macct-sub');
  await sleep(200);
  const hovered = (await names(page)).a2;
  await page.mouse.move(5, 5);
  // 名稱改短：不捲、照常顯示
  host.accounts.find((a) => a.id === 'a2').label = '個人 Pro';
  host.pushAccounts();
  await settle(page, 200);
  const back = (await names(page)).a2;
  await closeApp(app);
  // 每張卡：捲不捲只看放不放得下（放得下的照舊是「…」的樣式、不捲），至少有一張放得下
  const cards = Object.values(n0);
  check('accountNameMarquee', n0.a2.text === longLabel && n0.a2.marquee && n0.a2.anim === 'running' && n0.a2.dist < 0 && Math.abs(n0.a2.dist + n0.a2.overflow) <= 1
    && n0.a2.textOverflow === 'clip' && n1.a2.shiftX < 0
    && cards.every((c) => c.marquee === c.overflow > 1 && (c.anim === 'running') === c.marquee && c.textOverflow === (c.marquee ? 'clip' : 'ellipsis'))
    && cards.some((c) => !c.marquee)
    && hovered.marquee && hovered.anim === 'running' && hovered.boxWidth < n0.a2.boxWidth && Math.abs(hovered.dist + hovered.overflow) <= 1
    && !back.marquee && !back.anim && back.text === '個人 Pro' && back.shiftX === 0,
  { cards: n0, moved: n1.a2, hovered, back });
  // 減少動態、關掉動態效果：有 .marquee 也不捲，照舊以「…」截斷
  const calm = {};
  for (const [name, o] of [['reduced', { reducedMotion: 'reduce' }], ['fxoff', { settings: { visualEffects: false } }]]) {
    const a = await openApp('acct-marquee-' + name, Object.assign({ vp, layout: MAIN_LAYOUT, hostSetup: setup }, o));
    await settle(a.page, 400);
    calm[name] = (await names(a.page)).a2;
    await closeApp(a);
  }
  check('accountNameMarqueeCalm', Object.values(calm).every((c) => c.text === longLabel && !c.anim && c.textOverflow === 'ellipsis' && c.shiftX === 0), calm);
}

// ───── 登入到期（loginrules.js）：側欄帳戶卡的標示與點擊、卡片提示、儀表板的一行與「重新登入」─────
async function runLoginExpiry(vp) {
  const DAYMS = 86_400_000;
  const HOURMS = 3_600_000;
  // a1 還有 19 天（只寫在提示）、a2 剩 2 天多（側欄標示＋儀表板琥珀色）；其他帳戶沒有到期資料
  const app = await openApp('login-expiry', { vp, layout: MAIN_LAYOUT, hostSetup: (h) => {
    h.accounts.find((a) => a.id === 'a1').loginExpiresAt = NOW + 19 * DAYMS + 5 * HOURMS;
    Object.assign(h.accounts.find((a) => a.id === 'a2'), { loginExpiresAt: NOW + 2 * DAYMS + 5 * HOURMS, sessions: { open: 1, running: 0, waiting: 0 } });
  } });
  const { page, host } = app;
  const a2 = host.accounts.find((a) => a.id === 'a2');
  await settle(page, 600);
  const cards = () => page.evaluate(() => Object.fromEntries(Array.from(document.querySelectorAll('#sidebar .macct')).map((c) => {
    const b = c.querySelector('.macct-login');
    const shown = !!b && getComputedStyle(b).display !== 'none' && b.getBoundingClientRect().width > 0;
    // 在名稱那一行、不蓋到名稱（名稱省略）與圓環
    const br = shown ? b.getBoundingClientRect() : null;
    const nr = c.querySelector('.macct-name').getBoundingClientRect();
    const mr = c.querySelector('.macct-meters').getBoundingClientRect();
    const place = br ? { inTop: b.parentElement.classList.contains('macct-top'), clearOfName: nr.right <= br.left + 0.5,
      clearOfMeters: mr.width === 0 || br.right <= mr.left + 0.5, nameEllipsis: c.querySelector('.macct-name').scrollWidth > nr.width } : null;
    const left = b && b.querySelector('.mlogin-left');
    return [c.dataset.key, { badge: shown ? b.textContent : null, expired: !!b && b.classList.contains('expired'), tip: b ? b.title : '', place,
      leftShown: !!left && getComputedStyle(left).display !== 'none', nameWidth: Math.round(c.querySelector('.macct-name').getBoundingClientRect().width),
      cardTitle: c.title, filtered: c.classList.contains('filtered'), height: Math.round(c.getBoundingClientRect().height) }];
  })));
  const c0 = await cards();
  const others = Object.keys(c0).filter((k) => k !== 'a1' && k !== 'a2');
  check('loginExpirySidebar', c0.a2.badge === '2d' && c0.a2.leftShown && !c0.a2.expired && !!c0.a2.place && c0.a2.place.inTop && c0.a2.place.clearOfName && c0.a2.place.clearOfMeters
    && /^登入將在 2 天後到期（\d+\/\d\d \d\d:\d\d），重新登入後會重新起算\n點一下重新登入$/.test(c0.a2.tip)
    && c0.a1.badge === null && /\n登入到期：\d+\/\d\d \d\d:\d\d（19 天後）\n點一下只顯示這個帳戶的對話$/.test(c0.a1.cardTitle)
    && /\n登入將在 2 天後到期/.test(c0.a2.cardTitle) && c0.a2.height === c0.a1.height
    && others.length > 0 && others.every((k) => c0[k].badge === null && !/登入到期|登入將在/.test(c0[k].cardTitle)), c0);
  // 標示在名稱那一行：狀態那一行的 token 數照舊放得下（同一行還有過時的時鐘）
  const row = (await page.evaluate(tokenRows)).find((r) => r.key === 'a2');
  check('loginExpiryTokensFit', row.shown && !!row.fit && row.fit.ok && !row.fit.clipped && row.fit.nextLeft !== null, row.fit);
  const soonContrast = await page.evaluate(contrastOf, '#sidebar .macct[data-key="a2"] .macct-login');
  await page.locator('#sidebar .sb-sec-accts').screenshot({ path: path.join(OUT, 'login-expiry-sidebar-dark.png') });
  report.screenshots.push('login-expiry-sidebar-dark.png');

  // 點標示：送 accountLogin（不是裝置碼），不切換帳戶篩選；登入中標示收起來
  const before = host.received.length;
  await page.click('#sidebar .macct[data-key="a2"] .macct-login');
  const sent = await until(() => host.received.slice(before).find((m) => m.cmd === 'accountLogin'), 2000);
  await settle(page, 300);
  const c1 = await cards();
  check('loginExpiryClickRelogin', !!sent && sent.accountId === 'a2' && !sent.method && !c1.a2.filtered && c1.a2.badge === null
    && host.received.slice(before).filter((m) => m.cmd === 'accountLogin').length === 1, { sent, a2: c1.a2 });

  // 已到期（CLI 還沒發現、狀態仍是已登入）：紅色「到期」
  Object.assign(a2, { loginBusy: false, loginExpiresAt: NOW - 60_000 });
  host.pushAccounts();
  await settle(page, 300);
  const c2 = await cards();
  const expiredContrast = await page.evaluate(contrastOf, '#sidebar .macct[data-key="a2"] .macct-login');
  check('loginExpiryExpired', c2.a2.badge === '到期' && c2.a2.expired && /^登入已到期（\d+\/\d\d \d\d:\d\d），請重新登入\n點一下重新登入$/.test(c2.a2.tip), c2.a2);
  await page.locator('#sidebar .macct[data-key="a2"]').screenshot({ path: path.join(OUT, 'login-expiry-expired-dark.png') });
  report.screenshots.push('login-expiry-expired-dark.png');
  // 登入失效後宿主不送到期（ShownLoginExpiresAt）：狀態那一行照舊是「尚未登入」、沒有標示
  Object.assign(a2, { status: 'notLoggedIn', statusText: '尚未登入', loginExpiresAt: null });
  host.pushAccounts();
  await settle(page, 300);
  const c3 = await cards();
  check('loginExpiryGoneWhenLoggedOut', c3.a2.badge === null && !/登入到期|登入將在|登入已到期/.test(c3.a2.cardTitle), c3.a2);
  // 淺色主題：a1 剩 1 天（琥珀色）、a2 已到期（紅色）。兩張卡的名稱那一行都已經有執行中（a1 另有等待你）：只留圖示，名稱不被擠到只剩一個字
  const light = await openApp('login-expiry-light', { vp, layout: MAIN_LAYOUT, settings: { theme: 'light' }, colorScheme: 'light', hostSetup: (h) => {
    h.accounts.find((a) => a.id === 'a1').loginExpiresAt = NOW + DAYMS + HOURMS;
    h.accounts.find((a) => a.id === 'a2').loginExpiresAt = NOW - 60_000;
  } });
  await settle(light.page, 600);
  const lightSoon = await light.page.evaluate(contrastOf, '#sidebar .macct[data-key="a1"] .macct-login');
  const lightExpired = await light.page.evaluate(contrastOf, '#sidebar .macct[data-key="a2"] .macct-login');
  const lc = await light.page.evaluate(() => Object.fromEntries(['a1', 'a2'].map((k) => {
    const c = document.querySelector('#sidebar .macct[data-key="' + k + '"]');
    const b = c.querySelector('.macct-login');
    return [k, { shown: !b.hidden, compact: b.classList.contains('compact'), leftShown: getComputedStyle(b.querySelector('.mlogin-left')).display !== 'none',
      tip: b.title, nameWidth: Math.round(c.querySelector('.macct-name').getBoundingClientRect().width) }];
  })));
  // 名稱至少留得下「工作用」三個字的寬度（12px 字約 36px）
  check('loginExpiryCompact', lc.a1.shown && lc.a1.compact && !lc.a1.leftShown && /^登入將在 1 天後到期/.test(lc.a1.tip) && lc.a1.nameWidth >= 36
    && lc.a2.shown && lc.a2.compact && !lc.a2.leftShown && /^登入已到期/.test(lc.a2.tip) && lc.a2.nameWidth >= 36, lc);
  await light.page.locator('#sidebar .sb-sec-accts').screenshot({ path: path.join(OUT, 'login-expiry-sidebar-light.png') });
  report.screenshots.push('login-expiry-sidebar-light.png');
  await closeApp(light);
  check('loginExpiryContrast', [soonContrast, expiredContrast, lightSoon, lightExpired].every((c) => c >= 4.5), { soonContrast, expiredContrast, lightSoon, lightExpired });

  // 儀表板：a1 一行灰字、a2 琥珀色的一行，「重新登入」改成主要按鈕；沒有到期資料的帳戶沒有這一行
  Object.assign(a2, { status: 'ready', statusText: '已登入', loginExpiresAt: NOW + 2 * DAYMS + 5 * HOURMS });
  host.pushAccounts();
  await host.send({ type: 'showView', view: 'dashboard' });
  await sleep(1300);
  const dash = await page.evaluate(() => Object.fromEntries(Array.from(document.querySelectorAll('#dashboard .acard')).map((c) => {
    const n = c.querySelector('.note.login-exp');
    const btn = Array.from(c.querySelectorAll('.btn')).find((b) => /登入/.test(b.textContent) && !b.hidden);
    return [c.dataset.key, { note: n ? n.textContent : null, tone: n ? n.className.replace('note login-exp', '').trim() : null, primary: !!btn && btn.classList.contains('primary'), btn: btn ? btn.textContent : null }];
  })));
  check('loginExpiryDashboard', /^登入到期：\d+\/\d\d \d\d:\d\d（19 天後）$/.test(dash.a1.note) && dash.a1.tone === 'dim' && !dash.a1.primary
    && /^登入將在 2 天後到期/.test(dash.a2.note) && dash.a2.tone === 'warn' && dash.a2.primary && dash.a2.btn === '重新登入'
    && Object.entries(dash).filter(([k]) => k !== 'a1' && k !== 'a2').every(([, v]) => v.note === null), dash);
  await page.locator('#dashboard .acard[data-key="a2"]').screenshot({ path: path.join(OUT, 'login-expiry-dashboard-dark.png') });
  report.screenshots.push('login-expiry-dashboard-dark.png');
  await closeApp(app);
}

async function runTokens(vp) {
  const app = await openApp('tokens', { vp, layout: MAIN_LAYOUT });
  const { page, host } = app;
  await settle(page, 600);
  const byKey = (rows) => Object.fromEntries(rows.map((r) => [r.key, r]));
  const r0 = byKey(await page.evaluate(tokenRows));
  // a1 的 in 1,226,418、out 57,913（精簡格式，無條件捨去）；a4 未登入（狀態那一行照舊是「尚未登入」）；a5 已登入、今天 0；a6 不到 1 萬照舊千分位
  check('tokensInitial', r0.a1.shown && r0.a1.inSubRow && r0.a1.in === '1.226M' && r0.a1.out === '57.91K' && r0.a1.tags.join('/') === 'in/out' && !r0.a1.sep
    && r0.a2.in === '318.0K' && r0.a2.out === '12.87K' && r0.a5.shown && r0.a5.in === '0' && r0.a5.out === '0' && r0.a6.in === '12.33K' && r0.a6.out === '880'
    && !r0.a4.shown && r0.a4.subText === '尚未登入'
    && /token 合計：1,284,331/.test(r0.a1.title) && !/今日/.test(r0.a1.title + r0.a1.rowText) && /in（輸入）：1,226,418（其中快取讀取 1,018,772）/.test(r0.a1.title) && /out（輸出）：57,913/.test(r0.a1.title)
    && /近 30 天：in 31,870,552・out 1,388,410/.test(r0.a1.title) && !/子代理/.test(r0.a1.title), r0);   // Claude 子代理 2026-10-25 起即時記，不再寫晚到的說明
  // 放得下：兩個數字都在狀態那一行之內，沒有蓋到過時的時鐘與裝置小點，也沒用到右緣的裁切；最寬的值（6 個字）也一樣。
  // a1（兩個圓環）照樣有 in／out 標籤；a2 同一行還有過時的時鐘，收起標籤、改用斜線
  check('tokensFit', Object.values(r0).filter((r) => r.shown).every((r) => r.fit && r.fit.ok && !r.fit.clipped)
    && !!r0.a2.fit && r0.a2.fit.nextLeft !== null && r0.a2.tags.length === 0 && r0.a2.sep && r0.a6.tags.join('/') === 'in/out',
    Object.fromEntries(Object.entries(r0).map(([k, v]) => [k, { fit: v.fit, tags: v.tags, sep: v.sep }])));
  // 狀態那一行的方案名稱換成 token 數，卡片背景沒有淡淡的方案名稱（2026-10-23 拿掉）；卡片還是兩行高（跟沒有 token 的 a4 一樣高）；沒有數字雨的 canvas
  check('tokensReplacePlan', r0.a1.subText === null && Object.values(r0).every((r) => !r.mark)
    && r0.a1.height === r0.a4.height && Object.values(r0).every((r) => !r.canvas),
  Object.fromEntries(Object.entries(r0).map(([k, v]) => [k, { subText: v.subText, mark: v.mark, height: v.height }])));
  await page.locator('#sidebar .sb-sec-accts').screenshot({ path: path.join(OUT, 'tokens-sidebar-dark.png') });
  report.screenshots.push('tokens-sidebar-dark.png');

  // 新的真實值：解碼途中變動的位數是亂碼（不是數字）、前面沒變的位數照舊，旁邊不顯示增量（2026-10-23 拿掉）；結束後是新值
  host.tokens.accounts.a1.today.input += 46_873;
  await host.pushTokens();
  await sleep(90);
  const mid = byKey(await page.evaluate(tokenRows)).a1;
  await page.locator('#sidebar .macct[data-key="a1"]').screenshot({ path: path.join(OUT, 'tokens-decoding-dark.png') });
  report.screenshots.push('tokens-decoding-dark.png');
  const done = await until(async () => { const r = byKey(await page.evaluate(tokenRows)).a1; return r.scrambling === 0 && r.in === '1.273M' ? r : null; }, 3000);
  // 1.226M → 1.273M：前面沒變的「1.2」與單位照舊、後兩位是亂碼；out 沒變不動
  check('tokensDecode', mid.scrambling > 0 && mid.in.startsWith('1.2') && mid.in.endsWith('M') && !/^1\.\d{3}M$/.test(mid.in) && mid.out === '57.91K'
    && !mid.delta && !!done && done.out === '57.91K', { mid, done });
  await page.locator('#sidebar .macct[data-key="a1"]').screenshot({ path: path.join(OUT, 'tokens-settled-dark.png') });
  report.screenshots.push('tokens-settled-dark.png');
  // 同樣的值再送一次：不動
  await host.pushTokens();
  await sleep(90);
  const same = byKey(await page.evaluate(tokenRows)).a1;
  check('tokensSameValueStill', same.scrambling === 0 && same.in === '1.273M', same);
  // 登出：狀態那一行換回狀態文字
  const acc = host.accounts.find((a) => a.id === 'a3');
  Object.assign(acc, { status: 'notLoggedIn', statusText: '尚未登入' });
  await host.pushAccounts();
  await sleep(700);
  const out = byKey(await page.evaluate(tokenRows)).a3;
  check('tokensLoggedOutShowsStatus', !out.shown && out.subText === '尚未登入' && !out.mark, out);
  await closeApp(app);

  // 最後一張卡（a6 Grok）執行中：圓環外圈刻度與雷達是旋轉的偽元素，轉到斜角時外框超出卡片底部，
  // 帳戶清單（overflow-y: auto、高度剛好等於內容）就多出一兩 px 可捲動的高度，捲軸一下出現一下消失、卡片跟著左右跳。
  // 取樣 2 秒（刻度一圈 1.8 秒、雷達 2.4 秒）：清單內容放得下（前提；視窗加高，900 高時六張卡本來就超過 46%）時，每一格都不能有可捲動的溢出。
  // 無頭 Chrome 隱藏捲軸（不佔寬度），所以只量 scrollHeight／scrollWidth
  const run = await openApp('sb-run-overflow', { vp: { w: vp.w, h: 1000 }, layout: MAIN_LAYOUT,
    hostSetup: (h) => { h.accounts.find((a) => a.id === 'a6').sessions = { open: 1, running: 1, waiting: 0 }; } });
  await settle(run.page, 600);
  const ov = await run.page.evaluate(() => new Promise((resolve) => {
    const list = document.querySelector('#sidebar .sb-accts');
    const last = list.lastElementChild;
    const meter = last.querySelector('.mmeter');
    const r = { lastKey: last.dataset.key, running: !!last.querySelector('.macct-run:not([hidden])'),
      rotating: meter ? getComputedStyle(meter, '::before').animationName : null,
      fits: list.getBoundingClientRect().bottom + 0.5 >= last.getBoundingClientRect().bottom,
      frames: 0, maxOverflowY: 0, maxOverflowX: 0 };
    const t0 = performance.now();
    const tick = () => {
      r.frames++;
      r.maxOverflowY = Math.max(r.maxOverflowY, list.scrollHeight - list.clientHeight);
      r.maxOverflowX = Math.max(r.maxOverflowX, list.scrollWidth - list.clientWidth);
      if (performance.now() - t0 < 2000) requestAnimationFrame(tick); else resolve(r);
    };
    requestAnimationFrame(tick);
  }));
  check('sidebarRunNoPhantomScroll', ov.lastKey === 'a6' && ov.running && ov.rotating === 'fx-rot' && ov.fits && ov.frames > 30
    && ov.maxOverflowY === 0 && ov.maxOverflowX === 0, ov);
  // 裁切看不出來：動畫停在幾個角度（刻度每 50ms 轉 10°），圓環一帶的截圖和拿掉裁切（overflow: visible）時逐像素相同
  const box = await run.page.locator('#sidebar .macct[data-key="a6"] .mmeter').boundingBox();
  const clip = { x: box.x - 12, y: box.y - 12, width: box.width + 24, height: box.height + 24 };
  const diffs = [];
  for (const t of [0, 200, 250, 450, 700, 1150]) {
    // 捲動驅動的動畫（對話小地圖的範圍框，進度以捲動位置算）不能設絕對時間：只停時間軸是文件時間的
    await run.page.evaluate((ms) => { for (const a of document.getAnimations()) { if (a.timeline && !(a.timeline instanceof DocumentTimeline)) continue; a.pause(); a.currentTime = ms; } }, t);
    const clipped = await run.page.screenshot({ clip });
    const off = await run.page.addStyleTag({ content: '.mmeter { overflow: visible !important; }' });
    const visible = await run.page.screenshot({ clip });
    await off.evaluate((el) => el.remove());
    if (!clipped.equals(visible)) diffs.push(t);
  }
  check('sidebarRunClipInvisible', diffs.length === 0, { differentAt: diffs });
  await closeApp(run);

  // 淺色主題：對比與截圖
  const light = await openApp('tokens-light', { vp, layout: MAIN_LAYOUT, settings: { theme: 'light' }, colorScheme: 'light' });
  await settle(light.page, 900);
  const contrast = await light.page.evaluate(contrastOf, '#sidebar .macct[data-key="a3"] .mtok-c');
  check('tokensContrastLight', contrast !== null && contrast >= 4.5, contrast);
  await light.page.locator('#sidebar .sb-sec-accts').screenshot({ path: path.join(OUT, 'tokens-sidebar-light.png') });
  report.screenshots.push('tokens-sidebar-light.png');
  await closeApp(light);

  // 減少動態：直接換成新值、沒有亂碼
  const calm = await openApp('tokens-reduced', { vp, layout: MAIN_LAYOUT, reducedMotion: 'reduce' });
  await settle(calm.page, 600);
  calm.host.tokens.accounts.a1.today.output += 1_000;
  await calm.host.pushTokens();
  await sleep(60);
  const c1 = byKey(await calm.page.evaluate(tokenRows));
  check('tokensReducedMotion', c1.a1.scrambling === 0 && c1.a1.out === '58.91K' && c1.a1.in === '1.226M', c1.a1);
  await closeApp(calm);

  // 舊宿主（沒有 tokens）：狀態那一行照舊是方案名稱
  const none = await openApp('tokens-none', { vp, layout: MAIN_LAYOUT, tokens: null });
  await settle(none.page, 300);
  const n1 = byKey(await none.page.evaluate(tokenRows));
  check('tokensAbsentHidden', Object.values(n1).every((r) => !r.shown && !r.mark) && n1.a1.subText === 'Claude Max',
    Object.fromEntries(Object.entries(n1).map(([k, v]) => [k, [v.shown, v.subText, v.mark]])));
  await closeApp(none);

  // 〔同步〕所有配對電腦的合計（2026-10-23）：a1 這台 1 個執行中、其他電腦 2 個；a2 只有其他電腦在執行、等你回應；
  // a1 的 token 已加上其他電腦的（宿主帶 here＝這台今天的），a2 沒有 here（沒連結：只算這台）
  const peers = await openApp('tokens-peers', { vp, layout: MAIN_LAYOUT, hostSetup: (h) => {
    Object.assign(h.accounts.find((a) => a.id === 'a1'), { sessions: { open: 1, running: 1, waiting: 0 }, otherSessions: { open: 2, running: 2, waiting: 0 } });
    Object.assign(h.accounts.find((a) => a.id === 'a2'), { sessions: { open: 0, running: 0, waiting: 0 }, otherSessions: { open: 1, running: 1, waiting: 1 } });
    h.tokens.accounts.a1.here = { input: 26_418, cached: 18_772, output: 7_913 };
  } });
  await settle(peers.page, 600);
  const pr = await peers.page.evaluate(() => Object.fromEntries(['a1', 'a2'].map((k) => {
    const card = document.querySelector(`#sidebar .macct[data-key="${k}"]`);
    const run = card.querySelector('.macct-run');
    const wait = card.querySelector('.macct-wait');
    return [k, { run: run.hidden ? null : run.querySelector('.macct-run-n').textContent, runTitle: run.title, wait: !wait.hidden, waitTitle: wait.title,
      tokTitle: card.querySelector('.macct-tok').title }];
  })));
  check('tokensPeersTotals', pr.a1.run === '3' && pr.a1.runTitle === '3 個對話執行中（其中其他電腦 2 個）' && !pr.a1.wait
    && pr.a2.run === '1' && pr.a2.runTitle === '1 個對話執行中（都在其他電腦）' && pr.a2.wait && pr.a2.waitTitle === '1 個對話等待你回應（都在其他電腦）'
    && /其中這台電腦：in 26,418・out 7,913/.test(pr.a1.tokTitle) && /所有電腦上/.test(pr.a1.tokTitle) && /只算這台電腦/.test(pr.a2.tokTitle), pr);
  await peers.page.locator('#sidebar .sb-sec-accts').screenshot({ path: path.join(OUT, 'tokens-peers-sidebar-dark.png') });
  report.screenshots.push('tokens-peers-sidebar-dark.png');
  await closeApp(peers);

  // 背景程式（2026-10-24）：a3 這台 1 個對話＋其他電腦 1 個的背景程式（脫離 CLI 的程式）還在跑（這一輪已經結束），另有 1 個等背景工作回報；
  // a6（Grok）只有其他電腦的。標示是靜態的終端機徽章（不轉圈、不算執行中），和等背景工作回報的虛線圈並排，名稱那一行放得下
  const bgApp = await openApp('background-programs', { vp, layout: MAIN_LAYOUT, hostSetup: (h) => {
    Object.assign(h.accounts.find((a) => a.id === 'a3'), {
      sessions: { open: 2, running: 0, waiting: 0, background: 1, programs: 1, subagents: 2 }, otherSessions: { open: 0, running: 0, waiting: 0, background: 0, programs: 1, subagents: 1 },
      programs: [{ conversationId: 'c-bg', title: '開發工作六', programs: ['bash chain7.sh', 'python worker6.py --batch'], more: 0 }],
    });
    Object.assign(h.accounts.find((a) => a.id === 'a6'), { otherSessions: { open: 0, running: 0, waiting: 0, background: 0, programs: 2 } });
  } });
  await settle(bgApp.page, 600);
  const bg = await bgApp.page.evaluate(() => Object.fromEntries(['a1', 'a3', 'a6'].map((k) => {
    const card = document.querySelector(`#sidebar .macct[data-key="${k}"]`);
    const b = card.querySelector('.macct-prog');
    const wait = card.querySelector('.macct-bg');
    const name = card.querySelector('.macct-name').getBoundingClientRect();
    const top = card.querySelector('.macct-top').getBoundingClientRect();
    const box = b.getBoundingClientRect();
    return [k, { shown: getComputedStyle(b).display !== 'none' && box.width > 0, n: b.querySelector('.macct-prog-n').textContent, title: b.title,
      bgWait: !wait.hidden && wait.getBoundingClientRect().width > 0 && wait.getBoundingClientRect().right <= box.left,
      icon: !!b.querySelector('svg'), anim: getComputedStyle(b).animationName, running: !card.querySelector('.macct-run').hidden,
      fits: box.right <= top.right + 0.5 && name.width >= 40 }];
  })));
  check('backgroundBadge', !bg.a1.shown && bg.a3.shown && bg.a3.n === '2' && bg.a3.icon && bg.a3.anim === 'none' && !bg.a3.running && bg.a3.fits && bg.a3.bgWait
    && /^2 個對話的背景程式還在跑/.test(bg.a3.title) && /・開發工作六：bash chain7\.sh、python worker6\.py --batch/.test(bg.a3.title) && /・其他電腦 1 個對話/.test(bg.a3.title)
    && bg.a6.shown && bg.a6.n === '2' && /・其他電腦 2 個對話/.test(bg.a6.title), bg);
  // 執行中的子代理（2026-10-28）：a3 這台 2 個＋其他電腦 1 個——側欄帳戶卡是青色的子代理徽章（個數，不是對話數）、靜態，名稱那一行放得下；
  // 儀表板大卡列「子代理 3」；沒有子代理的 a1 不顯示
  const sub = await bgApp.page.evaluate(() => Object.fromEntries(['a1', 'a3'].map((k) => {
    const card = document.querySelector(`#sidebar .macct[data-key="${k}"]`);
    const b = card.querySelector('.macct-agents');
    const top = card.querySelector('.macct-top').getBoundingClientRect();
    const box = b.getBoundingClientRect();
    return [k, { shown: !b.hidden && box.width > 0, n: b.querySelector('.macct-agents-n').textContent, title: b.title, icon: !!b.querySelector('svg'),
      anim: getComputedStyle(b).animationName, fits: box.right <= top.right + 0.5 }];
  })));
  await bgApp.page.click('#viewSwitch .seg-btn[data-view="dashboard"]');
  await settle(bgApp.page, 400);
  const subDash = await bgApp.page.evaluate(() => Object.fromEntries(['a1', 'a3'].map((k) => {
    const s = document.querySelector(`#dashboard .acard[data-key="${k}"] .sess.sub`);
    return [k, s ? { text: s.textContent, title: s.title } : null];
  })));
  check('subagentBadge', !sub.a1.shown && sub.a3.shown && sub.a3.n === '3' && sub.a3.icon && sub.a3.anim === 'none' && sub.a3.fits
    && sub.a3.title === '3 個子代理執行中（其中其他電腦 1 個）' && subDash.a1 === null && !!subDash.a3 && subDash.a3.text === '子代理3'
    && subDash.a3.title === '3 個子代理執行中（其中其他電腦 1 個）', { sidebar: sub, dashboard: subDash });
  await bgApp.page.click('#viewSwitch .seg-btn[data-view="chat"]');
  await settle(bgApp.page, 300);
  await bgApp.page.locator('#sidebar .sb-sec-accts').screenshot({ path: path.join(OUT, 'background-programs-sidebar-dark.png') });
  report.screenshots.push('background-programs-sidebar-dark.png');
  await closeApp(bgApp);
}

// ───── 儀表板帳戶卡：寬度夠時一排最多三張；持續的 HUD 動態只在執行中、等你回應時，而且沒有要求減少動態 ─────
/** 在頁面內量帳戶卡的版面：欄數（和第一張同一列的卡數）、每張卡的圓環是否在同一列、卡片內容與按鈕列是否溢出或換行 */
function dashGridOf() {
  const grid = document.querySelector('#dashboard .acct-grid');
  const kids = Array.from(grid.children).filter((x) => x.getBoundingClientRect().width > 0);
  const top0 = Math.round(kids[0].getBoundingClientRect().top);
  const cols = kids.filter((x) => Math.round(x.getBoundingClientRect().top) === top0).length;
  const cards = Array.from(grid.querySelectorAll('.acard')).map((c) => {
    const ringTops = new Set(Array.from(c.querySelectorAll('.rings > .ring-col')).map((x) => Math.round(x.getBoundingClientRect().top)));
    const footTops = new Set(Array.from(c.querySelectorAll('.acard-foot > *')).filter((x) => x.getBoundingClientRect().width > 0 && !x.classList.contains('grow'))
      .map((x) => Math.round(x.getBoundingClientRect().top + x.getBoundingClientRect().height / 2)));
    // 溢出＝有元素超出卡片的內框（不看 scrollWidth：執行中轉動的外弧是方形的框，轉到 45° 時角落會超出，但被卡片裁掉、看不到）
    const right = c.getBoundingClientRect().right - parseFloat(getComputedStyle(c).borderRightWidth);
    const wide = Array.from(c.querySelectorAll('*')).filter((x) => x.getBoundingClientRect().right > right + 0.5)
      .slice(0, 3).map((x) => (typeof x.className === 'string' ? x.className : x.tagName) + '@' + Math.round(x.getBoundingClientRect().right - right));
    const overflow = wide.length > 0;
    // 按鈕列看得到的按鈕都要看得到圖示（窄卡片只留圖示時不能連圖示一起藏掉）
    const blankButtons = Array.from(c.querySelectorAll('.acard-foot .btn')).filter((b) => b.getBoundingClientRect().width > 0)
      .filter((b) => { const ic = b.querySelector('.ic'); return !ic || ic.getBoundingClientRect().width === 0; }).map((b) => b.className);
    return { key: c.dataset.key, width: Math.round(c.getBoundingClientRect().width), ringRows: ringTops.size, footRows: footTops.size, overflow, wide, blankButtons };
  });
  return { cols, cards };
}

/** #dashboard 裡的 CSS 動畫（不含轉場）：loops＝無限重複而且正在跑的；all＝全部（含一次性的） */
function dashAnimationsOf() {
  const dash = document.getElementById('dashboard');
  const name = (a) => {
    const t = a.effect.target;
    return (typeof t.className === 'string' ? t.className : t.tagName) + (a.effect.pseudoElement || '') + ':' + a.animationName;
  };
  const anims = document.getAnimations().filter((a) => a instanceof CSSAnimation && a.effect && a.effect.target && dash.contains(a.effect.target));
  return { loops: anims.filter((a) => a.effect.getComputedTiming().iterations === Infinity && a.playState === 'running').map(name), all: anims.map(name) };
}

/** 帳戶卡的持續動畫（儀表板的大卡 .acard 或側欄的小卡 .macct；fxrules.accountPulse）：每張卡的 data-pulse、--fx-tempo，
 *  與卡片裡正在跑的無限循環動畫（元素與偽元素:動畫名稱、一圈的毫秒數）；「執行中」徽章的轉圈（.spin，固定速度、減少動態時也留著）不算 */
function cardPulsesOf(sel) {
  const anims = document.getAnimations().filter((a) => a instanceof CSSAnimation && a.effect && a.effect.target
    && a.effect.getComputedTiming().iterations === Infinity && a.playState === 'running' && !a.effect.target.classList?.contains('spin'));
  return Array.from(document.querySelectorAll(sel)).map((card) => ({
    key: card.dataset.key, pulse: card.dataset.pulse || null, tempo: card.style.getPropertyValue('--fx-tempo'),
    loops: anims.filter((a) => card.contains(a.effect.target)).map((a) => {
      const t = a.effect.target;
      const cls = (typeof t.className === 'string' ? t.className : t.tagName).split(' ')[0];
      return { name: cls + (a.effect.pseudoElement || '') + ':' + a.animationName, ms: Math.round(a.effect.getComputedTiming().duration) };
    }),
  }));
}

/** 怠速的循環動畫：只能是這幾個（慢、淡，fx.css 的 data-pulse="idle"），一圈至少 5 秒 */
const IDLE_LOOPS = {
  acard: ['acard-mark::before:fx-idle-glow', 'ring-wrap::before:fx-rot', 'spark-end::after:fx-ping-rest'],
  macct: ['macct-bar::after:fx-idle-glow', 'mmeter::before:fx-rot'],
};
const idleOk = (card, kind) => card.loops.length > 0 && card.loops.every((l) => IDLE_LOOPS[kind].includes(l.name) && l.ms >= 5000);
/** 執行中的卡：同一個動畫在 b（執行量較多）的一圈時間是 a 的 1／ratio（容許 2 ms 的進位） */
const tempoOk = (a, b, ratio) => {
  const both = a.loops.filter((l) => b.loops.some((m) => m.name === l.name));
  return both.length > 0 && both.every((l) => Math.abs(b.loops.find((m) => m.name === l.name).ms * ratio - l.ms) <= 2 * ratio);
};

async function runDashGrid() {
  // 儀表板不顯示側欄，內容區就是視窗寬減左右留白。2560：內容區上限下仍是三欄（不會變四欄）；1920、1440：三欄；
  // 1290：三欄裡卡片最窄（約 400px）；1100、944（主視窗最小寬度）：兩欄。每種寬度的圓環與按鈕列都在同一列、沒有溢出、按鈕都有圖示
  const sizes = [{ w: 2560, h: 1300, cols: 3 }, { w: 1920, h: 1080, cols: 3 }, { w: 1440, h: 900, cols: 3 }, { w: 1290, h: 900, cols: 3 },
    { w: 1100, h: 700, cols: 2 }, { w: 944, h: 700, cols: 2 }];
  const grids = {};
  let ok = true;
  for (const s of sizes) {
    const app = await openApp('dash-grid-' + s.w, { vp: { w: s.w, h: s.h }, layout: onePane('c1', 'dashboard') });
    await settle(app.page, 1300); // 圓環長出、卡片開場的掃描
    const g = await app.page.evaluate(dashGridOf);
    grids[s.w] = g;
    if (g.cols !== s.cols || g.cards.some((c) => c.ringRows > 1 || c.footRows !== 1 || c.overflow || c.blankButtons.length)) ok = false;
    await shot(app.page, 'dash-grid-' + s.w);
    await closeApp(app);
  }
  check('dashGridUpToThree', ok, grids);
  // Codex 的「使用重置」（fixtures 的 a3 有 1 次）：按了先跳確認框、取消不送，確認才送 accountResetCredit
  {
    const app = await openApp('dash-reset-credit', { vp: { w: 1440, h: 900 }, layout: onePane('c1', 'dashboard') });
    await settle(app.page, 1300);
    const sent = () => app.page.evaluate(() => window.__posted.filter((m) => m.cmd === 'accountResetCredit'));
    const btn = app.page.locator('.reset-credit-btn');
    const r = { buttons: await btn.count() };
    await btn.first().click();
    await app.page.locator('.dlg-confirm').waitFor({ timeout: 3000 });
    r.dialog = await app.page.locator('.dlg-confirm').innerText();
    await shot(app.page, 'dash-reset-credit-confirm');
    await app.page.locator('.dlg-confirm .btn:not(.primary)').first().click();
    await settle(app.page, 300);
    r.afterCancel = (await sent()).length;
    await btn.first().click();
    await app.page.locator('.dlg-confirm .btn.primary').click();
    await settle(app.page, 300);
    r.afterConfirm = await sent();
    await closeApp(app);
    check('dashResetCreditConfirm', r.buttons === 1 && r.dialog.includes('1 次') && r.afterCancel === 0
      && r.afterConfirm.length === 1 && r.afterConfirm[0].accountId === 'a3', r);
  }
  // 淺色主題的截圖
  {
    const app = await openApp('dash-grid-light', { vp: { w: 1920, h: 1080 }, layout: onePane('c1', 'dashboard'), settings: { theme: 'light' }, colorScheme: 'light' });
    await settle(app.page, 1300);
    await shot(app.page, 'dash-grid-1920-light');
    await closeApp(app);
  }
  // 持續的動畫：fixtures 的 a1、a2 有對話在執行（有）；全部閒置（沒有）；要求減少動態（連一次性的開場掃描都沒有）
  const loopsOf = async (name, o, ms) => {
    const app = await openApp(name, Object.assign({ vp: { w: 1920, h: 1080 }, layout: onePane('c1', 'dashboard') }, o));
    await settle(app.page, ms);
    const r = await app.page.evaluate(dashAnimationsOf);
    await closeApp(app);
    return r;
  };
  const running = await loopsOf('dash-anim-running', {}, 1500);
  const calm = await loopsOf('dash-anim-calm', { reducedMotion: 'reduce' }, 150);
  // 帳戶卡的怠速與執行量（2026-10-24 使用者要求：沒有在執行也有小幅的動畫、像已連上在怠速；執行中依執行量快慢，側欄同一個原則）：
  // a1 一個在跑、a2 三個在跑（快 2 倍；2026-10-25 起每多一個加 0.5 倍、最快 4 倍）、其他閒置——已登入的只有慢而淡的怠速動畫，沒登入的（a4）不動；
  // 側欄的小卡同樣（對話畫面，側欄展開）；要求減少動態時兩邊都沒有
  const pulseSetup = (host) => {
    for (const a of host.accounts) a.sessions = Object.assign({}, a.sessions, { running: a.id === 'a1' ? 1 : a.id === 'a2' ? 3 : 0, waiting: 0 });
  };
  const pulses = async (name, o, sel) => {
    const app = await openApp(name, Object.assign({ vp: { w: 1920, h: 1080 }, hostSetup: pulseSetup }, o));
    await settle(app.page, 1500);
    const r = await app.page.evaluate(cardPulsesOf, sel);
    await shot(app.page, name);
    await sleep(2600); // 怠速的呼吸半圈之後再一張（兩張比對看得出光的明暗、刻度往前走）
    await shot(app.page, name + '-b');
    await closeApp(app);
    return r;
  };
  const verify = (cards, kind) => {
    const by = Object.fromEntries(cards.map((c) => [c.key, c]));
    const idle = cards.filter((c) => c.pulse === 'idle');
    return by.a1?.pulse === 'run' && by.a1.tempo === '1' && by.a2?.pulse === 'run' && by.a2.tempo === '2'
      && tempoOk(by.a1, by.a2, 2) && by.a1.loops.some((l) => l.ms < 5000)
      && by.a4?.pulse === 'off' && by.a4.loops.length === 0
      && idle.length >= 3 && idle.every((c) => idleOk(c, kind));
  };
  const dashCards = await pulses('dash-pulse', { layout: onePane('c1', 'dashboard') }, '#dashboard .acard[data-key]');
  const sideCards = await pulses('side-pulse', { layout: onePane('c1') }, '.sidebar .macct[data-key]');
  const calmSide = await pulses('side-pulse-calm', { layout: onePane('c1'), reducedMotion: 'reduce' }, '.sidebar .macct[data-key]');
  check('dashLoopsPulse', running.loops.length > 0 && calm.all.length === 0 && verify(dashCards, 'acard'),
    { running: running.loops.length, calm: calm.all, cards: dashCards });
  check('sidebarLoopsPulse', verify(sideCards, 'macct') && calmSide.every((c) => c.loops.length === 0), { cards: sideCards, calm: calmSide });
  // 全部帳戶的執行量（2026-10-25 使用者要求：同時跑很多代理時轉得更快）：a1 一個、a2 三個＝四個在跑，頂部品牌圖示的方塊環與儀表板摘要底下的光都是 2.5 倍
  {
    const app = await openApp('dash-total-tempo', { vp: { w: 1920, h: 1080 }, hostSetup: pulseSetup, layout: onePane('c1', 'dashboard') });
    await settle(app.page, 1500);
    const tt = await app.page.evaluate(() => {
      const dur = (el, pseudo) => { const a = el && el.getAnimations({ subtree: true }).find((x) => x.effect && x.effect.pseudoElement === pseudo); return a ? Math.round(a.effect.getTiming().duration) : null; };
      const mark = document.querySelector('.brand-mark'), sum = document.querySelector('.dash-summary');
      return { markTempo: mark && mark.style.getPropertyValue('--fx-tempo'), markMs: dur(mark, '::after'), sumTempo: sum && sum.style.getPropertyValue('--fx-tempo'), sumMs: dur(sum, '::after') };
    });
    await closeApp(app);
    check('dashTotalTempo', tt.markTempo === '2.5' && tt.markMs === 480 && tt.sumTempo === '2.5' && tt.sumMs === 1360, tt);
  }
  // 執行中的對話數變了（a1 從 1 個變 3 個）：動畫分幾步加速，每個影格的進度連續——以前直接換 --fx-tempo，瀏覽器依新的長度重算進度、位置瞬間跳走（2026-10-25 使用者要求不能跳）
  {
    const app = await openApp('side-tempo', { vp: { w: 1920, h: 1080 }, hostSetup: pulseSetup, layout: onePane('c1') });
    await settle(app.page, 1500);
    const sampling = app.page.evaluate(() => new Promise((resolve) => {
      const card = document.querySelector('.sidebar .macct[data-key="a1"]');
      // 圓環裡順時針掃的雷達光（.mmeter::after，spin，2.4 秒 ÷ --fx-tempo、線性）
      const anim = card && card.getAnimations({ subtree: true }).find((a) => a.animationName === 'spin' && a.effect.pseudoElement === '::after'
        && a.effect.target && a.effect.target.classList.contains('mmeter'));
      if (!anim) { resolve(null); return; }
      const out = [];
      const t0 = performance.now();
      const step = () => {
        const t = anim.effect.getTiming();
        // 時間取動畫時間軸的（這一格的時間）：performance.now() 是回呼真的跑到的時間，主執行緒忙的時候和動畫的進度對不起來
        out.push({ t: document.timeline.currentTime, p: anim.effect.getComputedTiming().progress, dur: t.duration });
        if (performance.now() - t0 < 2200) requestAnimationFrame(step); else resolve({ out, tempo: card.style.getPropertyValue('--fx-tempo') });
      };
      requestAnimationFrame(step);
    }));
    await sleep(500);
    for (const a of app.host.accounts) if (a.id === 'a1') a.sessions = Object.assign({}, a.sessions, { running: 3 });
    app.host.pushAccounts();
    const r = await sampling;
    let worst = null, durs = [];
    if (r) {
      durs = [...new Set(r.out.map((x) => Math.round(x.dur)))];
      worst = 0;
      for (let i = 1; i < r.out.length; i++) {
        const a = r.out[i - 1], b = r.out[i];
        const moved = ((b.p - a.p) % 1 + 1) % 1; // 一圈接下一圈時從 0.99 到 0.01 也是往前一點點
        const allowed = ((b.t - a.t) * 1.1) / Math.min(a.dur, b.dur) + 0.02; // 以前直接換值一次跳 0.52 圈
        worst = Math.max(worst, moved - allowed);
      }
    }
    await closeApp(app);
    check('sidebarTempoSmooth', !!r && r.tempo === '2' && durs.length >= 3 && worst <= 0, { tempo: r && r.tempo, durs, worst });
  }
  // 〔同步〕這台全部閒置、只有其他電腦在跑 a2 的對話（otherSessions，2026-10-24）：a2 的大卡照樣是執行中（.sess.run 帶動卡片的動畫）、
  // 開著也加上其他電腦的，摘要的執行中同樣算進去；滑過寫「都在其他電腦」
  {
    const app = await openApp('dash-anim-peers', { vp: { w: 1920, h: 1080 }, layout: onePane('c1', 'dashboard'), hostSetup: (host) => {
      for (const a of host.accounts) a.sessions = Object.assign({}, a.sessions, { open: 0, running: 0, waiting: 0 });
      host.accounts.find((a) => a.id === 'a2').otherSessions = { open: 2, running: 1, waiting: 0, background: 0, programs: 0 };
    } });
    await settle(app.page, 1500);
    const r = await app.page.evaluate(() => {
      const card = document.querySelector('#dashboard .acard[data-key="a2"]');
      const sess = (label) => Array.from(card.querySelectorAll('.acard-sess .sess')).find((x) => x.firstElementChild.textContent === label);
      const run = sess('執行中');
      const sum = document.querySelector('#dashboard .dash-summary .sum-item.run');
      const busy = Array.from(document.querySelectorAll('#dashboard .acard')).filter((c) => c.querySelector('.sess.run')).map((c) => c.dataset.key);
      return { busy, run: run.querySelector('b').textContent, runTone: run.classList.contains('run'), runTitle: run.title,
        open: sess('開著').querySelector('b').textContent, openTitle: sess('開著').title,
        sum: sum ? sum.querySelector('.sum-n').textContent : null, sumTitle: sum ? sum.title : null };
    });
    const loops = (await app.page.evaluate(dashAnimationsOf)).loops;
    await shot(app.page, 'dash-anim-peers');
    await closeApp(app);
    check('dashPeersRunning', r.busy.join() === 'a2' && r.runTone && r.run === '1' && r.runTitle === '1 個對話執行中（都在其他電腦）'
      && r.open === '2' && r.openTitle === '2 個對話開著（都在其他電腦）' && r.sum === '1' && r.sumTitle === '1 個對話執行中（都在其他電腦）'
      && loops.length > 0, Object.assign(r, { loops: loops.length }));
  }
}

/** 頂部列品牌名旁的版號（#appVer，versionrules.js）：看不看得到以 getComputedStyle 與大小判斷（不信 hidden 屬性）、文字、色調、提示 */
function versionBadgeOf() {
  const b = document.getElementById('appVer');
  if (!b) return null;
  const r = b.getBoundingClientRect();
  const top = document.querySelector('.topbar').getBoundingClientRect();
  return {
    shown: getComputedStyle(b).display !== 'none' && r.width > 0 && r.top >= top.top && r.bottom <= top.bottom,
    pre: b.querySelector('.app-ver-pre')?.textContent ?? null, text: b.querySelector('.app-ver-num')?.textContent ?? null,
    time: b.querySelector('.app-ver-time')?.textContent ?? null,
    timeBeforeNum: !!b.querySelector('.app-ver-time + .app-ver-num'),
    timeShown: (() => { const t = b.querySelector('.app-ver-time'); return !!t && getComputedStyle(t).display !== 'none' && t.getBoundingClientRect().width > 0; })(),
    tone: b.dataset.tone ?? null, title: b.title, label: b.getAttribute('aria-label'),
  };
}

/** 在頁面內量 app 更新橫幅的版面：橫幅與文字都不溢出、每個按鈕（含 ×）都在橫幅框內；橫幅沒顯示回 null */
function bannerLayoutOf() {
  const el = document.getElementById('appBanner');
  if (!el || el.hidden) return null;
  const r = el.getBoundingClientRect();
  const text = el.querySelector('.app-banner-text');
  const buttons = Array.from(el.querySelectorAll('button')).map((b) => b.getBoundingClientRect());
  return {
    overflow: el.scrollWidth > el.clientWidth || !text || text.scrollWidth > text.clientWidth + 1,
    buttons: buttons.length,
    buttonsInside: buttons.every((b) => b.width > 0 && b.left >= r.left - 0.5 && b.right <= r.right + 0.5 && b.top >= r.top - 0.5 && b.bottom <= r.bottom + 0.5),
    height: Math.round(r.height),
  };
}
const bannerLayoutOk = (l) => !!l && !l.overflow && l.buttonsInside;

/** 淺色的 app 更新橫幅（已備妥但現在不能重啟、無法安裝）與設定頁的 Clode Myriagent 區塊：截圖、文字對比、橫幅版面（runLight 與 runHotUpdate 的淺色共用） */
async function appUpdateLightShots(page, host, w) {
  const contrast = {}, layout = {};
  await host.send({ type: 'appUpdate', state: F.appUpdate('staged', { blocker: 'CLI 正在更新，更新完成後才重啟' }) });
  await page.waitForSelector('#appBanner:not([hidden])');
  contrast.staged = await page.evaluate(contrastOf, '#appBanner .app-banner-text');
  layout.staged = await page.evaluate(bannerLayoutOf);
  await shot(page, `app-banner-staged-light-${w}`);
  await host.send({ type: 'appUpdate', state: F.appUpdate('blocked') });
  await sleep(150);
  contrast.blocked = await page.evaluate(contrastOf, '#appBanner .app-banner-text');
  layout.blocked = await page.evaluate(bannerLayoutOf);
  await shot(page, `app-banner-blocked-light-${w}`);
  check('appBannerContrast_light_' + w, Object.values(contrast).every((r) => r !== null && r >= 4.5), contrast);
  check('appBannerLayout_light_' + w, Object.values(layout).every(bannerLayoutOk), layout);
  await page.click('#btnSettings');
  // Clode Myriagent 區塊在「版本與更新」分頁（runHotUpdate 的淺色那輪之前看的可能是 CLI 分頁）
  await page.click('.dlg-settings .set-tab[data-tab="app"]');
  await page.waitForSelector('.dlg-settings .set-sec[data-sec="app"]');
  await page.evaluate(() => document.querySelector('.dlg-settings .set-sec[data-sec="app"]').scrollIntoView({ block: 'start' }));
  await shot(page, `settings-app-light-${w}`);
  await page.keyboard.press('Escape');
}

async function runVisualChecks() {
  // 文字對比：用量列、執行時間、日期分組、按鍵提示、帳戶色文字、狀態膠囊等資訊文字，深淺色主題都至少 4.5:1
  const low = {};
  for (const theme of ['dark', 'light']) {
    const app = await openApp('contrast-' + theme, { vp: { w: 1440, h: 900 }, layout: onePane('c1'), settings: { theme }, colorScheme: theme });
    const { page } = app;
    await waitConv(page, 'c1');
    await settle(page, 300);
    for (const s of ['.run-time', '.sb-group', '.card-keys', '.macct-run', '.q-chip', '.cli-badge']) {
      const r = await page.evaluate(contrastOf, s);
      if (r === null || r < 4.5) low[theme + ' ' + s] = r;
    }
    // 輸入框下方的 token 數：2026-10-25 曾是深藍偏靛（深色底上約 3:1，門檻放寬到 2.8），2026-10-26 改回綠、再深一點，深淺色都要 4.5（同 fx-probe 的 fxTokenGreen）
    const tok = await page.evaluate(contrastOf, '.cinfo .ci-turn');
    if (tok === null || tok < 4.5) low[theme + ' .cinfo .ci-turn'] = tok;
    await page.click('#viewSwitch [data-view="dashboard"]');
    await sleep(500);
    for (const s of ['.note.dim', '.pill.st-ready', '.pill.st-notLoggedIn', '.ccard-rows dt']) {
      const r = await page.evaluate(contrastOf, s);
      if (r === null || r < 4.5) low[theme + ' ' + s] = r;
    }
    await closeApp(app);
  }
  check('textContrastAA', Object.keys(low).length === 0, low);

  // 焦點框：鍵盤聚焦到實心按鈕、色票、等待作答的卡片時，外框至少 2px、畫在外側
  {
    const app = await openApp('focus', { vp: { w: 1100, h: 760 }, layout: onePane('c1') });
    const { page } = app;
    await waitConv(page, 'c1');
    const ring = () => page.evaluate(() => {
      const el = document.activeElement, cs = getComputedStyle(el);
      return { cls: el.className, focusVisible: el.matches(':focus-visible'), style: cs.outlineStyle, width: parseFloat(cs.outlineWidth), offset: parseFloat(cs.outlineOffset) };
    });
    const outside = (r) => r.focusVisible && r.style !== 'none' && r.width >= 2 && r.offset >= 2;
    await page.focus(`${convSel('c1')} .it[data-id="i19"] .opt >> nth=0`);
    await page.keyboard.press('Shift+Tab');
    const card = await ring();
    await page.focus('.crow[data-key="c:c5"]');
    await page.keyboard.press('Delete');
    await page.waitForSelector('.dlg-confirm');
    await sleep(200);
    await page.keyboard.press('Tab');
    const danger = await ring();
    await shot(page, 'focus-danger-1100');
    await page.keyboard.press('Escape');
    await sleep(200);
    await page.click('#viewSwitch [data-view="dashboard"]');
    await page.click('.acard-add');
    await page.waitForSelector('.dlg-add');
    await sleep(200);
    await page.focus('.dlg-add .swatch >> nth=4');
    await page.keyboard.press('Tab');
    const swatch = await ring();
    await shot(page, 'focus-swatch-1100');
    await page.keyboard.press('Escape');
    check('focusRingOutside', /\bit-card\b/.test(card.cls) && outside(card) && /danger-solid/.test(danger.cls) && outside(danger) && /swatch/.test(swatch.cls) && outside(swatch), { card, danger, swatch });
    await closeApp(app);
  }

  // 分頁多到溢出：剛開的作用中分頁捲進可視範圍、滾輪可以左右捲；側欄帳戶篩選籤全部看得到
  {
    const app = await openApp('tabs', { vp: { w: 1100, h: 700 }, layout: onePane('c1') });
    const { page } = app;
    await waitConv(page, 'c1');
    for (const id of ['c2', 'c3', 'c4', 'c5', 'c6', 'c7', 'c8', 'c9', 'c10', 'c11', 'c12']) {
      await page.click(`.crow[data-key="c:${id}"]`);
      await waitConv(page, id);
    }
    await sleep(200);
    const strip = () => page.evaluate(() => {
      const t = document.querySelector('.pane[data-pane="p1"] .tabs');
      const a = t.querySelector('.tab.active').getBoundingClientRect(), b = t.getBoundingClientRect();
      return { overflow: t.scrollWidth > t.clientWidth, scrollLeft: Math.round(t.scrollLeft), active: t.querySelector('.tab.active').dataset.key, activeVisible: a.left >= b.left - 1 && a.right <= b.right + 1 };
    });
    const opened = await strip();
    await shot(page, 'tabs-overflow-1100');
    const box = await page.locator('.pane[data-pane="p1"] .tabs').boundingBox();
    await page.mouse.move(box.x + 60, box.y + box.height / 2);
    await page.mouse.wheel(0, -240);
    await sleep(150);
    const wheeled = await strip();
    check('tabsOverflowRevealAndWheel', opened.overflow && opened.active === 'c12' && opened.activeVisible && wheeled.scrollLeft < opened.scrollLeft, { opened, wheeled });
    // 側欄的帳戶篩選是一個下拉（不是一排按鈕）：按鈕在側欄裡、選單列出「所有帳戶」＋6 個帳戶與各自的對話數；
    // 選了一個帳戶清單只剩那個帳戶的對話、按鈕換成帳戶名稱；沒有其他電腦的對話時電腦下拉不出現
    const pickState = () => page.evaluate(() => {
      const b = document.querySelector('#sidebar .sb-filters [data-field="historyAccount"]');
      const s = document.querySelector('#sidebar').getBoundingClientRect(), r = b.getBoundingClientRect();
      return {
        visible: getComputedStyle(b).display !== 'none' && r.width > 0, inside: r.left >= s.left - 0.5 && r.right <= s.right + 0.5,
        label: b.querySelector('.sb-pick-label').textContent, on: b.classList.contains('on'),
        dev: getComputedStyle(document.querySelector('#sidebar [data-field="historyDevice"]')).display !== 'none',
        rows: [...document.querySelectorAll('#sidebar .sb-list .crow')].map((x) => x.title.split('\n')[1] || ''),
      };
    });
    const menuItems = () => page.$$eval('.menu .menu-item', (xs) => xs.map((x) => ({ label: x.querySelector('.mi-label').textContent, n: x.querySelector('.mi-hint') ? x.querySelector('.mi-hint').textContent : null, checked: x.classList.contains('checked') })));
    const p0 = await pickState();
    await page.click('#sidebar [data-field="historyAccount"]');
    await page.waitForSelector('.menu', { timeout: 2000 }).catch(() => {});
    const items0 = await menuItems();
    // 搜尋與篩選在清單下方、側欄的最左下角（2026-10-23）：清單在整組上面，接著是搜尋框（靠左），篩選下拉是最後一列、貼著側欄底部；篩選的選單往上開
    const tools = await page.evaluate(() => {
      const r = (sel) => { const e = document.querySelector(sel); return e ? e.getBoundingClientRect() : null; };
      const s = r('#sidebar'), q = r('#sidebar .sb-search-input'), f = r('#sidebar .sb-filters'), sc = r('#sidebar .sb-scroll'), b = r('#sidebar [data-field="historyAccount"]'), m = r('.menu');
      return {
        filtersBottomGap: Math.round(s.bottom - f.bottom), searchLeft: Math.round(q.left - s.left), searchAboveFilters: q.bottom <= f.top + 0.5,
        listAboveSearch: sc.bottom <= q.top + 0.5, menuAbove: !!m && m.bottom <= b.top + 0.5, menuInView: !!m && m.top >= 0,
      };
    });
    check('sidebarSearchBottomLeft', tools.filtersBottomGap >= 0 && tools.filtersBottomGap <= 12 && tools.searchLeft >= 0 && tools.searchLeft <= 14
      && tools.searchAboveFilters && tools.listAboveSearch && tools.menuAbove && tools.menuInView, tools);
    await shot(page, 'filter-account-menu-1100');
    await page.click('.menu .menu-item >> nth=3');
    await sleep(150);
    const p1 = await pickState();
    await page.click('#sidebar [data-field="historyAccount"]');
    await page.waitForSelector('.menu', { timeout: 2000 }).catch(() => {});
    const items1 = await menuItems();
    await page.click('.menu .menu-item >> nth=0');
    await sleep(150);
    const p2 = await pickState();
    const pickedN = items0[3] ? Number(items0[3].n) : -1;
    check('filterAccountDropdown', p0.visible && p0.inside && p0.label === '所有帳戶' && !p0.on && !p0.dev
      && items0.length === 7 && items0[0].label === '所有帳戶' && items0[0].checked && Number(items0[0].n) === p0.rows.length
      && items0.slice(1).reduce((n, x) => n + Number(x.n), 0) <= p0.rows.length // 帳戶不在清單裡的對話（測試資料有一個）只算在「所有帳戶」
      && p1.label === items0[3].label && p1.on && p1.rows.length === pickedN && pickedN > 0 && p1.rows.every((x) => x === items0[3].label)
      && items1[3] && items1[3].checked && !items1[0].checked && p2.label === '所有帳戶' && !p2.on && p2.rows.length === p0.rows.length,
    { p0: { ...p0, rows: p0.rows.length }, items0, p1, items1: items1.map((x) => x.checked), p2: { ...p2, rows: p2.rows.length } });

    // 工作區篩選：測試資料原本都在同一個資料夾＝下拉不出現；改成用到三個工作區（SampleShop 的大小寫與斜線不同也算同一個、一個沒選資料夾）之後出現，
    // 和帳戶下拉同一排、都在側欄裡。選單照最近的對話排、預設工作區在最後；選了 SampleShop 清單只剩那三個、按鈕換成資料夾名稱、提示是完整路徑；
    // 再篩帳戶（工作用 Max）時清單空了要說明，工作區選單的數字照帳戶篩選算。
    // 搜尋框右邊的「全部」：有篩選下拉就出現、和搜尋框同一排；沒有篩選時是按下的樣子，按下去帳戶、工作區與搜尋一起清掉
    const folderState = () => page.evaluate(() => {
      const b = document.querySelector('#sidebar [data-field="historyFolder"]');
      const a = document.querySelector('#sidebar [data-field="historyAccount"]').getBoundingClientRect();
      const s = document.querySelector('#sidebar').getBoundingClientRect(), r = b.getBoundingClientRect();
      return {
        visible: getComputedStyle(b).display !== 'none' && r.width > 0, inside: r.left >= s.left - 0.5 && r.right <= s.right + 0.5,
        sameRow: Math.abs(r.top - a.top) < 1 && r.left > a.right, label: b.querySelector('.sb-pick-label').textContent, on: b.classList.contains('on'), title: b.title,
        rows: [...document.querySelectorAll('#sidebar .sb-list .crow')].map((x) => x.dataset.key.slice(2)),
        empty: document.querySelector('#sidebar .sb-empty').hidden ? null : document.querySelector('#sidebar .sb-empty span').textContent,
        acct: document.querySelector('#sidebar [data-field="historyAccount"] .sb-pick-label').textContent,
        search: document.querySelector('#sidebar .sb-search-input').value,
        all: (() => {
          const x = document.querySelector('#sidebar .sb-all'), q = document.querySelector('#sidebar .sb-search-input').getBoundingClientRect(), xr = x.getBoundingClientRect();
          return { visible: getComputedStyle(x).display !== 'none' && xr.width > 0, on: x.classList.contains('on'), pressed: x.getAttribute('aria-pressed'),
            sameRow: Math.abs(xr.top - q.top) < 1 && xr.left > q.right && xr.right <= s.right - 0.5 };
        })(),
      };
    });
    const folderItems = () => page.$$eval('.menu .menu-item', (xs) => xs.map((x) => ({
      label: x.querySelector('.mi-label').textContent, desc: x.querySelector('.mi-desc') ? x.querySelector('.mi-desc').textContent : null,
      n: x.querySelector('.mi-hint') ? Number(x.querySelector('.mi-hint').textContent) : null, checked: x.classList.contains('checked'),
    })));
    const pickMenu = async (field, nth) => {
      await page.click(`#sidebar [data-field="${field}"]`);
      await page.waitForSelector('.menu', { timeout: 2000 }).catch(() => {});
      const items = field === 'historyFolder' ? await folderItems() : await menuItems();
      if (nth !== null) { await page.click(`.menu .menu-item >> nth=${nth}`); await sleep(150); }
      return items;
    };
    const f0 = await folderState();
    const conv = (id) => app.host.conversations.find((x) => x.id === id);
    Object.assign(conv('c2'), { workingDirectory: 'C:\\repo\\SampleShop' });
    Object.assign(conv('c5'), { workingDirectory: 'C:\\repo\\SampleShop\\' });
    Object.assign(conv('c8'), { workingDirectory: 'c:/repo/sampleshop' });
    Object.assign(conv('c4'), { customFolder: false, workingDirectory: 'C:\\Users\\Lin\\AppData\\Local\\Myriagent\\conversations\\c4\\workspace' });
    app.host.pushConversations();
    await sleep(200);
    const f1 = await folderState();
    await page.click('#sidebar [data-field="historyFolder"]');
    await page.waitForSelector('.menu', { timeout: 2000 }).catch(() => {});
    await sleep(150);
    await shot(page, 'filter-folder-menu-1100');
    await page.keyboard.press('Escape');
    await sleep(100);
    const fi0 = await pickMenu('historyFolder', 2);
    const f2 = await folderState();
    await pickMenu('historyAccount', 1);
    const f3 = await folderState();
    const fi1 = await pickMenu('historyFolder', null);
    await page.keyboard.press('Escape');
    await sleep(100);
    await page.fill('#sidebar .sb-search-input', 'Codex');
    await sleep(150);
    const f3s = await folderState();
    await page.click('#sidebar .sb-all');
    await sleep(150);
    const f4 = await folderState();
    check('filterFolderDropdown', !f0.visible && f0.all.visible && f0.all.on && f0.all.pressed === 'true' && f0.all.sameRow && f1.visible && f1.inside && f1.sameRow && f1.label === '所有工作區' && !f1.on
      && JSON.stringify(fi0.map((x) => [x.label, x.n])) === JSON.stringify([['所有工作區', 14], ['myriagent', 10], ['SampleShop', 3], ['預設工作區', 1]])
      && fi0[0].checked && fi0[1].desc === 'C:\\repo\\myriagent' && fi0[2].desc === 'C:\\repo\\SampleShop' && fi0[3].desc === '各對話自己的資料夾'
      && f2.label === 'SampleShop' && f2.on && !f2.all.on && f2.all.pressed === 'false' && f2.title === '依工作區篩選對話：C:\\repo\\SampleShop' && f2.rows.join() === 'c2,c5,c8'
      && f3.rows.length === 0 && f3.empty === '「工作用 Max」在「SampleShop」沒有對話'
      && JSON.stringify(fi1.map((x) => [x.n, x.checked])) === JSON.stringify([[5, false], [4, false], [0, true], [1, false]])
      && f3s.search === 'Codex' && !f3s.all.on
      && f4.label === '所有工作區' && !f4.on && f4.acct === '所有帳戶' && f4.search === '' && f4.all.on && f4.rows.length === 14,
    { f0: { ...f0, rows: f0.rows.length }, f1: { ...f1, rows: f1.rows.length }, fi0, f2, f3, fi1, f3s, f4: { ...f4, rows: f4.rows.length } });
    await closeApp(app);
  }

  // 2×2：執行中、有佇列＋待辦＋附件的對話（c1）輸入區不超出窗格、訊息區留得下空間；上排窗格的 / 指令提示不超出視窗；輸入很多行時送出鈕還按得到
  const dockFit = (page) => page.evaluate(() => {
    const c = document.querySelector('.conv[data-conv="c1"]');
    const pane = c.closest('.pane').getBoundingClientRect(), dock = c.querySelector('.dock').getBoundingClientRect();
    return { scroller: Math.round(c.querySelector('.scroller').getBoundingClientRect().height), dockOverflow: Math.round(dock.bottom - pane.bottom) };
  });
  {
    const app = await openApp('squeeze-1100', { vp: { w: 1100, h: 700 }, layout: FOUR_PANES });
    const { page, host } = app;
    for (const id of ['c1', 'c3', 'c4', 'c7']) await waitConv(page, id);
    await settle(page, 300);
    const m = await dockFit(page);
    await shot(page, 'chat-4pane-busy-1100');
    // 使用者把 6 則的佇列展開：清單在自己的框內捲動，停止鈕仍在窗格內
    const queue = Array.from({ length: 6 }, (_, i) => ({ id: 'qx' + i, text: '排隊訊息 ' + (i + 1) }));
    await host.send({ type: 'composer', conversationId: 'c1', composer: Object.assign({}, host.data.c1.composer, { queue }) });
    await sleep(100);
    await page.click(`${convSel('c1')} .queue-head`);
    await sleep(150);
    const open = await page.evaluate(() => {
      const c = document.querySelector('.conv[data-conv="c1"]');
      const pane = c.closest('.pane').getBoundingClientRect(), dock = c.querySelector('.dock').getBoundingClientRect();
      const b = c.querySelector('.cbtn.stop').getBoundingClientRect();
      const hit = document.elementFromPoint(b.x + b.width / 2, b.y + b.height / 2);
      return { queueRows: c.querySelectorAll('.queue-row').length, dockOverflow: Math.round(dock.bottom - pane.bottom), stopReachable: !!(hit && c.contains(hit) && hit.closest('.cbtn.stop')) };
    });
    await shot(page, 'chat-4pane-queue-open-1100');
    // 對話欄很矮：最近的指令收起來（訊息串裡就有那則），訊息區才留得下空間
    const lpShown = await page.evaluate(() => getComputedStyle(document.querySelector('.conv[data-conv="c1"] .lastprompt')).display !== 'none');
    check('fourPaneComposerYields', m.dockOverflow <= 0 && m.scroller >= 60 && !lpShown && open.queueRows === 6 && open.dockOverflow <= 0 && open.stopReachable, { collapsed: m, lastPromptShown: lpShown, queueOpen: open });
    await closeApp(app);
  }
  {
    const app = await openApp('squeeze-944', { vp: { w: 944, h: 581 }, layout: FOUR_PANES });
    const { page } = app;
    for (const id of ['c1', 'c3', 'c4', 'c7']) await waitConv(page, id);
    await settle(page, 300);
    const fit = await dockFit(page);
    const c3 = convSel('c3');
    await page.click(`${c3} .cinput`);
    await page.keyboard.type('/');
    await page.waitForSelector(`${c3} .suggest:not([hidden]) .sg-row`);
    await sleep(150);
    const sg = await page.evaluate((s) => {
      const box = document.querySelector(s + ' .suggest').getBoundingClientRect(), sel = document.querySelector(s + ' .suggest .sg-row.sel').getBoundingClientRect();
      return { top: Math.round(box.top), height: Math.round(box.height), selTop: Math.round(sel.top) };
    }, c3);
    await shot(page, 'slash-menu-4pane-944');
    check('suggestInsideViewport', sg.top >= 0 && sg.selTop >= sg.top, sg);
    await page.keyboard.press('Escape');
    await page.fill(`${c3} .cinput`, Array.from({ length: 25 }, (_, i) => '第 ' + (i + 1) + ' 行：錯誤堆疊 at Foo.Bar()').join('\n'));
    await page.dispatchEvent(`${c3} .cinput`, 'input');
    await sleep(150);
    const grow = await page.evaluate((s) => {
      const c = document.querySelector(s), b = c.querySelector('.cbtn.send').getBoundingClientRect();
      const hit = document.elementFromPoint(b.x + b.width / 2, b.y + b.height / 2);
      return { textarea: Math.round(c.querySelector('.cinput').getBoundingClientRect().height), sendReachable: !!(hit && c.contains(hit) && hit.closest('.cbtn.send')) };
    }, c3);
    check('fourPaneMinWindowFits', fit.dockOverflow <= 0 && grow.sendReachable, { c1: fit, c3LongText: grow });
    await closeApp(app);
  }

  // 窗格格狀：從 4 個一路分割到 12 個，最多 4 欄 × 3 排、排滿工作區、不重疊；12 個時分割鈕停用、Ctrl+\ 不再多開；
  // 每個窗格的輸入區都留在窗格內、輸入框點得到；存下的 12 個窗格的版面照樣還原
  const GRID = { 4: [2, 2], 5: [3, 2], 6: [3, 2], 7: [3, 3], 8: [3, 3], 9: [3, 3], 10: [4, 3], 11: [4, 3], 12: [4, 3] };
  const paneGrid = (page) => page.evaluate(() => {
    const ws = document.getElementById('workspace').getBoundingClientRect();
    const rs = Array.from(document.querySelectorAll('#workspace > .pane')).map((p) => p.getBoundingClientRect());
    const overlaps = rs.some((a, i) => rs.some((b, j) => j > i && Math.min(a.right, b.right) - Math.max(a.left, b.left) > 0.5 && Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top) > 0.5));
    const inside = rs.every((r) => r.left >= ws.left - 0.5 && r.right <= ws.right + 0.5 && r.top >= ws.top - 0.5 && r.bottom <= ws.bottom + 0.5);
    const area = rs.reduce((n, r) => n + r.width * r.height, 0);
    return {
      n: rs.length, cols: new Set(rs.map((r) => Math.round(r.left))).size, rows: new Set(rs.map((r) => Math.round(r.top))).size,
      overlaps, inside, coverage: Math.round(area / (ws.width * ws.height) * 1000) / 1000,
      minW: Math.round(Math.min(...rs.map((r) => r.width))), minH: Math.round(Math.min(...rs.map((r) => r.height))),
    };
  });
  const gridOk = (g) => !!GRID[g.n] && g.cols === GRID[g.n][0] && g.rows === GRID[g.n][1] && !g.overlaps && g.inside && g.coverage >= 0.97;
  // 輸入框、停止與送出鈕（看得到的）都在窗格內、點得到（量元素本身：輸入區被擠矮時內容會畫出輸入區的框外）
  const docksFit = (page) => page.evaluate(() => Array.from(document.querySelectorAll('#workspace > .pane')).map((p) => {
    const pane = p.getBoundingClientRect(), c = p.querySelector('.conv:not([hidden])');
    const targets = c ? [c.querySelector('.cinput'), ...c.querySelectorAll('.cbtn.stop, .cbtn.send')].filter((el) => el && !el.hidden) : [];
    if (targets.length < 2) return { pane: p.dataset.pane, missing: true };
    const bad = targets.filter((el) => {
      const r = el.getBoundingClientRect();
      const hit = document.elementFromPoint(r.x + r.width / 2, r.y + Math.min(r.height / 2, 10));
      return r.bottom > pane.bottom + 0.5 || r.right > pane.right + 0.5 || !hit || (hit !== el && !el.contains(hit));
    }).map((el) => el.className);
    return { pane: p.dataset.pane, conv: c.dataset.conv, bad };
  }).filter((x) => x.missing || x.bad.length));
  /** 每個開著的對話的標題列：有沒有擠回一行（.short-head）、晶片在不在標題下面的第二行、標題列高度 */
  const headRows = (page) => page.evaluate(() => Array.from(document.querySelectorAll('.pane .conv:not([hidden])')).map((c) => {
    const title = c.querySelector('.ch-title').getBoundingClientRect(), acct = c.querySelector('.ch-acct').getBoundingClientRect();
    return { conv: c.dataset.conv, short: c.classList.contains('short-head'), twoRows: acct.top >= title.bottom - 1 && title.height > 0,
      height: Math.round(c.querySelector('.ch').getBoundingClientRect().height), paneH: Math.round(c.getBoundingClientRect().height) };
  }));
  let heads4 = null;
  {
    const app = await openApp('grid-1440', { vp: { w: 1440, h: 900 }, layout: FOUR_PANES });
    const { page, host } = app;
    for (const id of ['c1', 'c3', 'c4', 'c7']) await waitConv(page, id);
    await settle(page, 150);
    heads4 = await headRows(page);
    const grids = [await paneGrid(page)];
    for (let n = 5; n <= 12; n++) {
      await page.click('.pane.focused .strip-actions .ibtn[title^="分割"]');
      await page.waitForSelector(`.pane[data-pane="p${n}"] .conv:not(.loading)`);
      await settle(page, 120);
      grids.push(await paneGrid(page));
      if (n === 6 || n === 9) await shot(page, `chat-${n}pane-1440`);
    }
    await settle(page, 300);
    await shot(page, 'chat-12pane-1440');
    const split = await page.evaluate(() => Array.from(document.querySelectorAll('.strip-actions .ibtn[title^="分割"], .strip-actions .ibtn[title^="最多"]')).map((b) => ({ disabled: b.disabled, title: b.title })));
    const before = host.received.filter((m) => m.cmd === 'newConversation').length;
    await page.keyboard.press('Control+Backslash');
    await sleep(300);
    const extra = host.received.filter((m) => m.cmd === 'newConversation').length - before;
    const layout12 = await until(() => { const l = host.last('layout'); return l && l.layout.panes.length === 12 ? l : null; }, 2000);
    const docks = await docksFit(page);
    check('paneGridUpTo4x3', grids.every(gridOk) && split.length === 12 && split.every((b) => b.disabled && b.title === '最多 12 個窗格') && extra === 0
      && !!layout12 && new Set(layout12.layout.panes.map((p) => p.id)).size === 12 && docks.length === 0,
      { grids: grids.filter((g) => !gridOk(g)), split: split.filter((b) => !b.disabled || b.title !== '最多 12 個窗格').length, splitCount: split.length, extra, layout12: !!layout12, docks });
    await closeApp(app);
  }
  {
    const ids = ['c1', 'c2', 'c3', 'c4', 'c5', 'c6', 'c7', 'c8', 'c9', 'c10', 'c11', 'c12'];
    const layout = { panes: ids.map((id, i) => ({ id: 'p' + (i + 1), tabs: [id], active: id })), focusedPane: 'p7', sidebarCollapsed: true, view: 'chat' };
    const app = await openApp('grid-restore-1100', { vp: { w: 1100, h: 700 }, layout });
    const { page } = app;
    for (const id of ids) await waitConv(page, id);
    await settle(page, 300);
    const g = await paneGrid(page);
    const restored = await page.evaluate(() => ({
      panes: Array.from(document.querySelectorAll('#workspace > .pane')).map((p) => p.dataset.pane + ':' + Array.from(p.querySelectorAll('.tab')).map((t) => t.dataset.key).join(',')),
      focused: (document.querySelector('.pane.focused') || {}).dataset?.pane || null,
    }));
    await shot(page, 'chat-12pane-restore-1100');
    const docks = await docksFit(page);
    check('paneGridRestores12', gridOk(g) && restored.panes.join(' ') === ids.map((id, i) => 'p' + (i + 1) + ':' + id).join(' ') && restored.focused === 'p7' && docks.length === 0,
      { grid: g, restored, docks });
    // 標題列：1440×900 的 2×2 分兩行；1100×700 的 12 個窗格太矮，擠回一行（兩行會把輸入區的按鈕擠出窗格）
    const heads12 = await headRows(page);
    check('headerRowsByPaneHeight', heads4.length === 4 && heads4.every((x) => !x.short && x.twoRows && x.height >= 60)
      && heads12.length === 12 && heads12.every((x) => x.short && !x.twoRows && x.height <= 41), { heads4, heads12 });
    await closeApp(app);
  }
  {
    // 主視窗最小的大小（960×620）、側欄展開：3 排放不下時每排固定 216px、工作區直向捲動，輸入區照樣留在窗格內
    const ids = ['c1', 'c2', 'c3', 'c4', 'c5', 'c6', 'c7', 'c8', 'c9', 'c10', 'c11', 'c12'];
    const layout = { panes: ids.map((id, i) => ({ id: 'p' + (i + 1), tabs: [id], active: id })), focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' };
    const app = await openApp('grid-min-944', { vp: { w: 944, h: 581 }, layout });
    const { page } = app;
    for (const id of ids) await waitConv(page, id);
    await settle(page, 300);
    const g = await page.evaluate(() => {
      const ws = document.getElementById('workspace');
      const rs = Array.from(ws.children).map((p) => p.getBoundingClientRect());
      return { scrolls: ws.scrollHeight > ws.clientHeight + 1, minH: Math.round(Math.min(...rs.map((r) => r.height))), cols: new Set(rs.map((r) => Math.round(r.left))).size, rows: new Set(rs.map((r) => Math.round(r.top))).size };
    });
    await shot(page, 'chat-12pane-min-944');
    await page.evaluate(() => { const ws = document.getElementById('workspace'); ws.scrollTop = ws.scrollHeight; });
    await sleep(150);
    await shot(page, 'chat-12pane-min-944-scrolled');
    // 捲到底之後最下排看得到；最上排的 c1（佇列＋附件＋執行中）捲回來看
    const docksBottom = await docksFit(page).then((d) => d.filter((x) => ['p9', 'p10', 'p11', 'p12'].includes(x.pane)));
    await page.evaluate(() => { document.getElementById('workspace').scrollTop = 0; });
    await sleep(150);
    const docksTop = await docksFit(page).then((d) => d.filter((x) => ['p1', 'p2', 'p3', 'p4'].includes(x.pane)));
    check('paneGridMinWindow', g.scrolls && g.cols === 4 && g.rows === 3 && g.minH >= 216 && docksTop.length === 0 && docksBottom.length === 0, { grid: g, docksTop, docksBottom });
    await closeApp(app);
  }

  // 檔案抽屜拉到最寬（對話欄剩 280px）：權限、模型、推理強度的內容不會畫到停止鈕上
  {
    const app = await openApp('drawer-max', { vp: { w: 1100, h: 700 }, layout: onePane('c1') });
    const { page } = app;
    await waitConv(page, 'c1');
    await page.click(`${convSel('c1')} .ch .ibtn[aria-label="檔案瀏覽器"]`);
    await page.waitForSelector(`${convSel('c1')} .drawer.open .fe`);
    await sleep(250);
    const hb = await page.locator(`${convSel('c1')} .dr-resize`).boundingBox();
    await page.mouse.move(hb.x + 3, hb.y + 200);
    await page.mouse.down();
    await page.mouse.move(5, hb.y + 200, { steps: 8 });
    await page.mouse.up();
    await sleep(250);
    const m = await page.evaluate(() => {
      const c = document.querySelector('.conv[data-conv="c1"]');
      const stop = c.querySelector('.cbtn.stop').getBoundingClientRect();
      const painted = Array.from(c.querySelectorAll('.cbar .perm, .cbar .pick')).filter((p) => !p.hidden).map((p) => {
        const r = p.getBoundingClientRect();
        const kids = getComputedStyle(p).overflow === 'visible' ? Array.from(p.children).map((k) => k.getBoundingClientRect().right) : [];
        return Math.round(Math.max(r.right, ...kids));
      });
      return { col: Math.round(c.querySelector('.conv-col').getBoundingClientRect().width), stopLeft: Math.round(stop.left), paintedRight: painted };
    });
    await shot(page, 'drawer-max-1100');
    check('drawerMaxComposerNoOverlap', m.col <= 300 && m.paintedRight.every((x) => x <= m.stopLeft), m);
    await closeApp(app);
  }

  // 主視窗最小寬度的儀表板：三個時間窗的圓環排在同一列
  {
    const app = await openApp('dash-944', { vp: { w: 944, h: 700 }, layout: onePane('c1', 'dashboard') });
    const { page } = app;
    await sleep(1300);
    const rows = await page.evaluate(() => new Set(Array.from(document.querySelectorAll('.acard[data-key="a1"] .rings > .ring-col')).map((x) => Math.round(x.getBoundingClientRect().top))).size);
    await shot(page, 'dashboard-944');
    check('dashboardRingsOneRowAt944', rows === 1, { rows });
    await closeApp(app);
  }
}

// ───── 儀表板：更新期間按鈕仍點得到、立即檢查的回饋、走勢線監聽器與橫軸、色票、過時說明、圓環初值、登出說明 ─────
async function runDashboard(vp) {
  const tag = String(vp.w);
  const app = await openApp('dashboard-' + tag, { vp, layout: null });
  const { page, host } = app;
  await host.send({ type: 'showView', view: 'dashboard' });
  await page.waitForSelector('.acard[data-key="a1"] .spark-svg');
  await sleep(1300);
  const pushCli = () => host.send({ type: 'cli', cli: host.cli });
  const pushAccounts = () => host.send({ type: 'accounts', accounts: host.accounts }); // 回傳送達的 promise（host.pushAccounts 不回傳）
  // 在 sel 上按下，按住期間 during() 送一則更新，再放開
  const pressDuring = async (sel, during) => {
    const loc = page.locator(sel).first();
    await loc.scrollIntoViewIfNeeded();
    await sleep(100);
    const b = await loc.boundingBox();
    await page.mouse.move(b.x + b.width / 2, b.y + b.height / 2);
    await page.mouse.down();
    await during();
    await sleep(30);
    await page.mouse.up();
    await sleep(100);
  };
  const btnState = (sel) => page.evaluate((s) => { const b = document.querySelector(s); return b && { text: b.textContent, disabled: b.disabled, spin: !!b.querySelector('.spin') }; }, sel);

  // 立即檢查：按下就顯示「檢查中…」並停用，每個 CLI 都檢查完才恢復，並以提示說明結果
  const checkSel = '.cli-head .btn';
  await page.locator(checkSel).scrollIntoViewIfNeeded();
  await page.click(checkSel);
  const ck0 = await btnState(checkSel);
  await shot(page, `dashboard-cli-checking-${tag}`);
  // 第一則 cli（只有 Claude 檢查完）到了以後仍在檢查中
  const firstCli = await until(() => page.evaluate((t) => Number(document.querySelector('.ccard[data-key="Claude"] [data-rel-at]').dataset.relAt) > t, NOW), 3000);
  const ck1 = await btnState(checkSel);
  const ckDone = await until(() => page.evaluate((s) => !document.querySelector(s).disabled, checkSel), 5000);
  const ck2 = await btnState(checkSel);
  const ckToast = await page.$$eval('.toast .toast-text', (n) => n.map((x) => x.textContent));
  check('dashCliCheckFeedback', !!host.last('cliCheck') && ck0.disabled && ck0.spin && ck0.text === '檢查中…' && !!firstCli && ck1.disabled && !!ckDone && ck2.text === '立即檢查' && !ck2.spin
    && ckToast.includes('檢查完成：Claude Code 已是最新版；Codex 有新版 0.162.0；Antigravity 已是最新版；Grok Build 已是最新版'), { afterClick: ck0, afterFirstCli: ck1, done: ck2, toasts: ckToast });

  // 查最新版失敗（離線）：宿主不更新 lastCheckedAt（「上次檢查」），只更新 checkedAt。按鈕照樣在每個 CLI 都檢查完時恢復，
  // 提示說明查不到最新版本、不說已是最新版
  host.cliCheckFail.add('Claude');
  await sleep(300);
  const claudeCard = () => page.evaluate(() => {
    const c = document.querySelector('.ccard[data-key="Claude"]'), shown = (el) => !!el && !el.hidden;
    return { relAt: c.querySelector('[data-rel-at]').dataset.relAt, pill: shown(c.querySelector('.pill')) ? c.querySelector('.pill').textContent : null, msg: shown(c.querySelector('.ccard-msg')) ? c.querySelector('.ccard-msg').textContent : null };
  });
  const offBefore = await claudeCard();
  await page.click(checkSel);
  const offBusy = await btnState(checkSel);
  const offDone = await until(() => page.evaluate((s) => !document.querySelector(s).disabled, checkSel), 5000);
  const offToast = await page.$$eval('.toast .toast-text', (n) => n.map((x) => x.textContent));
  const offAfter = await claudeCard();
  await shot(page, `dashboard-cli-check-offline-${tag}`);
  check('dashCliCheckLatestError', offBusy.disabled && !!offDone && offToast.includes('檢查完成：Claude Code 查不到最新版本；Codex 有新版 0.162.0；Antigravity 已是最新版；Grok Build 已是最新版')
    && offAfter.relAt === offBefore.relAt && offAfter.pill === null && offAfter.msg === '無法查詢最新版本：連線逾時', { before: offBefore, after: offAfter, done: !!offDone, toasts: offToast });
  host.cliCheckFail.clear();
  host.cli.Claude.latestError = null;
  await host.send({ type: 'cli', cli: host.cli });

  // 按下到放開之間收到內容相同的更新：點擊不能被吃掉（徽章在下載進度變動時、CLI 更新鈕、帳戶的重新登入鈕）
  Object.assign(host.cli.Codex, { busy: true, progress: '下載中 45%' });
  await pushCli();
  await sleep(100);
  await pressDuring('#cliBadge span:not(.spin)', async () => { host.cli.Codex.progress = '下載中 46%'; await pushCli(); });
  const badgeOpened = await until(() => page.$('.dlg-settings'), 1000);
  await page.keyboard.press('Escape');
  await sleep(200);
  Object.assign(host.cli.Codex, { busy: false, progress: null });
  await pushCli();
  await sleep(100);
  await pressDuring('.ccard[data-key="Codex"] .ccard-foot .btn span:not(.ic)', () => pushCli());
  const upd = host.last('cliUpdate');
  await sleep(900); // 等假宿主模擬的更新結束
  // 一排三張的窄卡片上「重新登入」只有圖示：按在圖示上（同樣是按鈕裡面的子元素）
  await pressDuring('.acard[data-key="a1"] .acard-foot .btn.ghost > .ic', () => pushAccounts());
  const login = host.last('accountLogin', (m) => m.accountId === 'a1');
  check('dashButtonsSurviveUpdates', !!badgeOpened && !!upd && upd.provider === 'Codex' && !!login, { badgeOpensSettings: !!badgeOpened, cliUpdate: upd, accountLogin: login });
  host.accounts[0].loginBusy = false;
  await pushAccounts();

  // 走勢線：重畫很多次也只有一組監聽器；歷史清空後滑過去不會跳出舊資料的提示框
  const cdp = await page.context().newCDPSession(page);
  const sparkSel = '.acard[data-key="a1"] .spark';
  const listeners = async () => {
    const { result } = await cdp.send('Runtime.evaluate', { expression: `document.querySelector(${JSON.stringify(sparkSel)})` });
    const { listeners: list } = await cdp.send('DOMDebugger.getEventListeners', { objectId: result.objectId });
    const c = {};
    for (const l of list) c[l.type] = (c[l.type] || 0) + 1;
    return c;
  };
  const before = await listeners();
  // 數重畫次數（每次重畫會放入新的 svg），確定 20 則更新真的都讓走勢線重畫了
  await page.evaluate((s) => {
    window.__sparkRenders = 0;
    new MutationObserver((ms) => { for (const m of ms) if (m.addedNodes.length) window.__sparkRenders++; }).observe(document.querySelector(s), { childList: true });
  }, sparkSel);
  const w0 = host.accounts[0].usage.windows[0];
  for (let i = 0; i < 20; i++) {
    w0.history[w0.history.length - 1][1] = 30 + (i % 2);
    await pushAccounts();
  }
  await sleep(100);
  const after = await listeners();
  const renders = await page.evaluate(() => window.__sparkRenders);
  const savedHistory = w0.history;
  w0.history = [];
  await pushAccounts();
  await sleep(100);
  const sb = await page.locator(sparkSel).first().boundingBox();
  await page.mouse.move(sb.x + sb.width * 0.5, sb.y + sb.height / 2);
  await sleep(150);
  const staleTip = await page.evaluate(() => { const t = document.querySelector('.tip'); return !!t && !t.hidden; });
  await page.mouse.move(5, 5);
  check('dashSparkListenersNoLeak', renders === 20 && before.pointermove === 1 && before.pointerleave === 1 && after.pointermove === 1 && after.pointerleave === 1 && !staleTip,
    { renders, before, after, tipShownOverEmptySpark: staleTip });

  // 走勢線橫軸固定是最近 7 天：只有 3 小時的資料畫在右端；中間缺漏兩天的資料在缺漏處斷開
  const HOUR = 3600e3, DAY = 86400e3;
  const tnow = Date.now();
  w0.history = [[tnow - 3 * HOUR, 5], [tnow - 2 * HOUR, 15], [tnow - HOUR, 30], [tnow - 5 * 60e3, 34]];
  const w1 = host.accounts[0].usage.windows[1];
  const savedWeekly = w1.history;
  w1.history = savedWeekly.filter(([t]) => t < tnow - 4 * DAY || t > tnow - 2 * DAY);
  await pushAccounts();
  await sleep(150);
  const axis = await page.$$eval('.acard[data-key="a1"] .spark-line', (n) => n.map((x) => x.getAttribute('d')));
  const firstX = (d) => Number(/^M([\d.]+)/.exec(d || '')[1]);
  const subpaths = (d) => (d.match(/M/g) || []).length;
  await page.evaluate(() => document.querySelector('.acard[data-key="a1"]').scrollIntoView({ block: 'start' }));
  await shot(page, `dashboard-spark-axis-${tag}`);
  // 整週的資料從左端開始：走勢線每 4 小時取一點（桶子依絕對時間切），第一點最晚落在 4 小時處（140px × 4／168 ≈ 3.33px），
  // 跟探針在幾點跑有關，門檻要容得下一整桶
  check('dashSparkWeekAxis', axis.length === 3 && firstX(axis[0]) > 130 && subpaths(axis[1]) === 2 && subpaths(axis[2]) === 1 && firstX(axis[2]) < 140 * 4 / 168 + 0.2,
    { shortHistoryStartsAt: axis[0] && firstX(axis[0]), gapSubpaths: axis[1] && subpaths(axis[1]), fullWeekStartsAt: axis[2] && firstX(axis[2]) });
  w0.history = savedHistory;
  w1.history = savedWeekly;
  await pushAccounts();

  // 色票：目前顏色不在色票裡（本機 Codex 的 #19C3A6）時多一個「目前顏色」並選取
  host.accounts[2].color = '#19C3A6';
  await pushAccounts();
  await sleep(100);
  await page.click('.acard[data-key="a3"] .acard-more');
  await page.click('.menu .menu-item:has(.mi-label:text-is("變更顏色…"))');
  await page.waitForSelector('.dlg .palette');
  await sleep(200);
  const pal = await page.evaluate(() => ({
    swatches: document.querySelectorAll('.dlg .palette .swatch').length,
    on: Array.from(document.querySelectorAll('.dlg .palette .swatch.on')).map((b) => ({ c: b.dataset.c, label: b.getAttribute('aria-label') })),
  }));
  await shot(page, `dashboard-palette-current-${tag}`);
  await page.keyboard.press('Escape');
  await sleep(200);
  check('dashPaletteCurrentColor', pal.swatches === 11 && pal.on.length === 1 && pal.on[0].c === '#19C3A6' && pal.on[0].label === '目前顏色', pal);

  // 從沒取得過用量（fetchedAt 為 null）又查詢失敗：不顯示沒有時間的「資料可能過時，取得於」
  Object.assign(host.accounts[3], { status: 'apiKey', statusText: 'API Key 登入',
    usage: { fetchedAt: null, stale: true, source: 'cache', error: '找不到登入憑證（API Key 或第三方登入沒有訂閱用量）', windows: [], credits: null, note: null } });
  await pushAccounts();
  await sleep(150);
  const notes = await page.$$eval('.acard[data-key="a4"] .acard-notes > *', (n) => n.map((x) => x.textContent));
  await shot(page, `dashboard-stale-null-${tag}`);
  check('dashStaleWithoutFetchedAt', notes.length === 2 && !notes.some((t) => /資料可能過時|取得於/.test(t)) && notes[1] === '找不到登入憑證（API Key 或第三方登入沒有訂閱用量）', notes);

  // 多電腦同步的共享用量（source=shared）：來源顯示中文，不是原始字串 shared
  const savedA4 = { status: host.accounts[3].status, statusText: host.accounts[3].statusText, usage: host.accounts[3].usage };
  Object.assign(host.accounts[3], { status: 'ready', statusText: '已登入',
    usage: { fetchedAt: Date.now() - 60 * 1000, stale: false, source: 'shared', error: null, credits: null, note: null,
      windows: [{ key: 'session', label: '5 小時', percent: 30, resetsAt: Date.now() + HOUR, severity: 'ok', history: [] }] } });
  await pushAccounts();
  await sleep(150);
  const sharedSrc = await page.$$eval('.acard[data-key="a4"] .note-src', (n) => n.map((x) => x.textContent));
  await shot(page, `dashboard-usage-shared-${tag}`);
  check('dashUsageSourceShared', sharedSrc.length === 1 && sharedSrc[0] === '其他電腦回報', sharedSrc);
  Object.assign(host.accounts[3], savedA4);
  await pushAccounts();
  await sleep(150);

  // 新帳戶的圓環：進場動畫的兩個影格內又收到更新，圓弧要停在最新的值
  const a9 = { id: 'a9', provider: 'Claude', label: '新帳戶', color: '#E5A04B', isSystemDefault: false, configDirectory: 'C:\\Users\\Lin\\AppData\\Local\\Myriagent\\accounts\\a9',
    email: null, plan: null, status: 'ready', statusText: '已登入', safeMode: false, loginBusy: false, sessions: { open: 0, running: 0, waiting: 0 },
    usage: { fetchedAt: Date.now(), stale: false, source: 'api', error: null, credits: null, note: null, windows: [{ key: 'session', label: '5 小時', percent: 10, resetsAt: Date.now() + HOUR, severity: 'ok', history: [] }] } };
  const a9b = JSON.parse(JSON.stringify(a9));
  Object.assign(a9b.usage.windows[0], { percent: 80, severity: 'high' });
  await page.evaluate(([m1, m2]) => { window.__host(m1); setTimeout(() => window.__host(m2), 0); },
    [{ type: 'accounts', accounts: host.accounts.concat([a9]) }, { type: 'accounts', accounts: host.accounts.concat([a9b]) }]);
  await sleep(1300);
  const ring = await page.evaluate(() => { const c = document.querySelector('.acard[data-key="a9"] .ring-col'); return c && { num: c.querySelector('.ring-num').textContent, offset: c.querySelector('.ring-arc').style.strokeDashoffset }; });
  check('dashRingFirstFrameUpdate', !!ring && ring.num === '80' && ring.offset === '20', ring);
  await pushAccounts();

  // 登出本機預設帳戶：確認框說明終端機與 VS Code 的 CLI 也會登出；隔離帳戶沒有這段
  await page.locator('.acard[data-key="a1"]').scrollIntoViewIfNeeded();
  await page.click('.acard[data-key="a1"] .acard-more');
  await page.click('.menu .menu-item:has(.mi-label:text-is("登出"))');
  await page.waitForSelector('.dlg-confirm');
  await sleep(200);
  const defDetail = await page.textContent('.dlg-confirm .confirm-detail').catch(() => null);
  await shot(page, `dashboard-logout-default-${tag}`);
  await page.click('.dlg-confirm .btn:not(.primary)');
  await sleep(200);
  await page.click('.acard[data-key="a2"] .acard-more');
  await page.click('.menu .menu-item:has(.mi-label:text-is("登出"))');
  await page.waitForSelector('.dlg-confirm');
  const isoDetail = await page.$('.dlg-confirm .confirm-detail');
  await page.click('.dlg-confirm .btn:not(.primary)');
  await sleep(200);
  check('dashLogoutDefaultExplains', !!defDetail && defDetail.includes('C:\\Users\\Lin\\.claude') && defDetail.includes('終端機') && defDetail.includes('Claude Code') && !isoDetail && !host.last('accountLogout'),
    { defaultDetail: defDetail, isolatedHasDetail: !!isoDetail });

  // 登入的 email 在帳戶大卡的副標（側欄精簡卡不顯示）；沒有 email（尚未登入）就不出現
  const dashSubs = await page.$$eval('.acard[data-key] .acard-sub', (n) => Object.fromEntries(n.map((x) => [x.closest('.acard').dataset.key, x.textContent])));
  check('dashCardEmail', (dashSubs.a1 || '').includes('lin.dev@example.com') && (dashSubs.a2 || '').includes('lin.home@example.com') && !(dashSubs.a4 || '@').includes('@'), dashSubs);

  // Antigravity：帳戶卡有四個額度窗（Gemini 與 Claude/GPT 各 5 小時＋每週）、選單沒有精簡模式、
  // 登出要在 agy 視窗輸入 /logout；CLI 區依 providers 的順序四張卡
  await page.locator('.acard[data-key="a5"]').scrollIntoViewIfNeeded();
  const agyRings = await page.$$eval('.acard[data-key="a5"] .ring-label', (n) => n.map((x) => x.textContent));
  const agyNotes = await page.$$eval('.acard[data-key="a5"] .acard-notes > *', (n) => n.map((x) => x.textContent));
  const agySub = await page.textContent('.acard[data-key="a5"] .acard-sub');
  await shot(page, `dashboard-antigravity-${tag}`);
  await page.click('.acard[data-key="a5"] .acard-more');
  const agyMenu = await page.$$eval('.menu .mi-label', (n) => n.map((x) => x.textContent));
  await page.click('.menu .menu-item:has(.mi-label:text-is("登出"))');
  await page.waitForSelector('.dlg-confirm');
  await sleep(200);
  const agyLogout = await page.textContent('.dlg-confirm .confirm-detail').catch(() => null);
  await page.click('.dlg-confirm .btn:not(.primary)');
  await sleep(200);
  const cliKeys = await page.$$eval('#dashboard .cli-grid > .ccard', (n) => n.map((x) => x.dataset.key));
  check('dashAntigravity', agyRings.join(',') === '5 小時・Gemini,每週・Gemini,5 小時・Claude/GPT,每週・Claude/GPT'
    && agyNotes.some((x) => x.includes('更新於') && x.includes('官方用量端點'))
    && !!agySub && agySub.includes('Antigravity') && agySub.includes('lin.agy@example.com') && agySub.includes('本機預設')
    && !agyMenu.includes('精簡模式') && agyMenu.includes('開啟設定資料夾') && !!agyLogout && agyLogout.includes('/logout') && !host.last('accountLogout')
    && cliKeys.join(',') === 'Claude,Codex,Antigravity,Grok', { agyRings, agyNotes, agySub, agyMenu, agyLogout, cliKeys });

  // Grok：帳戶卡有一個每週額度窗與方案、選單沒有精簡模式（grok 沒有對應的開關）；CLI 卡的安裝方式是原生安裝
  await page.locator('.acard[data-key="a6"]').scrollIntoViewIfNeeded();
  const grokCard = await page.evaluate(() => {
    const c = document.querySelector('.acard[data-key="a6"]');
    return c && { text: c.textContent, sub: (c.querySelector('.acard-sub') || {}).textContent || null };
  });
  await shot(page, `dashboard-grok-${tag}`);
  await page.click('.acard[data-key="a6"] .acard-more');
  const grokMenu = await page.$$eval('.menu .mi-label', (n) => n.map((x) => x.textContent));
  await page.keyboard.press('Escape');
  await sleep(150);
  const grokCli = await page.evaluate(() => { const c = document.querySelector('#dashboard .ccard[data-key="Grok"]'); return c && c.textContent; });
  check('dashGrok', !!grokCard && grokCard.text.includes('每週') && grokCard.text.includes('SuperGrok') && !!grokCard.sub && grokCard.sub.includes('本機預設')
    && !grokMenu.includes('精簡模式') && grokMenu.includes('開啟設定資料夾') && grokMenu.includes('登出')
    && !!grokCli && grokCli.includes('原生安裝') && grokCli.includes('1.0.52') && grokCli.includes('Grok Build'), { grokCard, grokMenu, grokCli });

  // 共用預設帳戶的設定：只在支援的 CLI 的隔離帳戶顯示（系統預設帳戶本身就是來源），勾選照 shareCustomizations、點了送 accountUpdate
  const shareItem = async (key) => {
    await page.locator(`.acard[data-key="${key}"]`).scrollIntoViewIfNeeded();
    await page.click(`.acard[data-key="${key}"] .acard-more`);
    await page.waitForSelector('.menu');
    const item = await page.evaluate(() => {
      const b = [...document.querySelectorAll('.menu .menu-item')].find((x) => (x.querySelector('.mi-label') || {}).textContent === '共用預設帳戶的設定');
      return b ? { checked: b.getAttribute('aria-checked'), desc: (b.querySelector('.mi-desc') || {}).textContent || '' } : null;
    });
    return item;
  };
  const shareA2 = await shareItem('a2');
  await shot(page, `account-menu-share-${tag}`);
  await page.keyboard.press('Escape');
  await sleep(150);
  const shareA4 = await shareItem('a4');
  await page.click('.menu .menu-item:has(.mi-label:text-is("共用預設帳戶的設定"))');
  const shareCmd = await until(() => host.last('accountUpdate', (m) => m.accountId === 'a4' && m.shareCustomizations !== undefined), 2000);
  await sleep(150);
  const shareA1 = await shareItem('a1');
  await page.keyboard.press('Escape');
  await sleep(150);
  const shareA6 = await shareItem('a6');
  await page.keyboard.press('Escape');
  await sleep(150);
  check('dashShareCustomizations', !!shareA2 && shareA2.checked === 'true' && shareA2.desc.includes('精簡模式下不作用')
    && !!shareA4 && shareA4.checked === 'false' && shareA4.desc.includes('AGENTS.md') && !!shareCmd && shareCmd.shareCustomizations === true
    && shareA1 === null && shareA6 === null, { shareA2, shareA4, shareCmd, shareA1, shareA6 });
  await closeApp(app);
}

// ───── 對話窗格：載入中、草稿、卡片焦點、待辦、建立順序、「最新」鈕、影片、拖放遮罩、刪除確認 ─────
async function runConversationFixes(vp) {
  const tag = String(vp.w);
  const app = await openApp('convfix-' + tag, { vp, layout: { panes: [{ id: 'p1', tabs: ['c1', 'c2'], active: 'c1' }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' } });
  const { page, host, context } = app;
  await waitConv(page, 'c1');
  await settle(page, 300);
  const c1 = convSel('c1'), c2 = convSel('c2');
  const activeName = () => page.evaluate(() => { const a = document.activeElement; return !a || a === document.body ? 'body' : (a.classList.contains('cinput') ? 'cinput' : a.className); });

  // 宿主讀檔較慢：先用清單摘要的標題與帳戶色（和分頁一致），藏起還沒有內容的晶片與輸入列選項，訊息區顯示載入中
  host.delayOpen = 1500;
  await page.click('.crow[data-key="c:c5"]');
  await sleep(400);
  const loadSnap = () => page.evaluate(() => {
    const conv = document.querySelector('.conv[data-conv="c5"]');
    const vis = (el) => !!el && getComputedStyle(el).display !== 'none' && el.getBoundingClientRect().width > 0;
    const status = Array.from(conv.querySelectorAll('.thread [role="status"]')).find(vis);
    return {
      loading: conv.classList.contains('loading'), title: conv.querySelector('.ch-title-text').textContent,
      chips: vis(conv.querySelector('.ch-chips')), toolbar: Array.from(conv.querySelectorAll('.cbar .perm, .cbar .pick')).some(vis),
      indicator: status ? status.textContent : null,
      acc: getComputedStyle(conv).getPropertyValue('--acc').trim(), tabAcc: getComputedStyle(document.querySelector('.tab[data-key="c5"]')).getPropertyValue('--acc').trim(),
    };
  });
  const whileLoading = await loadSnap();
  await shot(page, `conv-loading-${tag}`);
  host.delayOpen = 0;
  await waitConv(page, 'c5');
  await sleep(150);
  const afterLoad = await loadSnap();
  check('loadingState', whileLoading.loading && whileLoading.title === '整理 CLI 自動更新的流程' && !whileLoading.chips && !whileLoading.toolbar &&
    whileLoading.indicator === '載入對話中…' && whileLoading.acc === whileLoading.tabAcc && afterLoad.chips && afterLoad.toolbar && !afterLoad.indicator, { whileLoading, afterLoad });

  // 草稿（帳戶尚未登入）：「在新視窗開啟」停用並說明原因；帳戶登入後，空白頁的提示跟著更新
  await page.hover('.macct[data-key="a4"]'); // 「＋」只在滑過帳戶卡時出現
  await page.click('.macct[data-key="a4"] .macct-new');
  const draftId = await until(() => page.evaluate(() => { const t = document.querySelector('.pane[data-pane="p1"] .tab.active'); return t && /^d\d+$/.test(t.dataset.key) ? t.dataset.key : null; }), 3000);
  const dsel = convSel(draftId);
  await page.waitForSelector(`${dsel}:not(.loading) .draft:not([hidden])`);
  const popBtn = await page.evaluate((s) => { const b = document.querySelector(s + ' .ch .ibtn[aria-label="在新視窗開啟"]'); return { disabled: b.disabled, title: b.title }; }, dsel);
  await page.click(`${dsel} .ch .ibtn[aria-label="更多動作"]`);
  await page.waitForSelector('.menu');
  const popItemDisabled = await page.evaluate(() => { const b = Array.from(document.querySelectorAll('.menu .menu-item')).find((x) => x.textContent.includes('在新視窗開啟')); return b ? b.disabled : null; });
  await page.keyboard.press('Escape');
  const draftWarn = () => page.evaluate((s) => { const w = document.querySelector(s + ' .draft-warn'); return w ? w.textContent : null; }, dsel);
  const warnBefore = await draftWarn();
  Object.assign(host.accounts.find((a) => a.id === 'a4'), { status: 'ready', statusText: '已登入' });
  host.pushAccounts();
  await sleep(250);
  const warnAfter = await draftWarn();
  check('draftPopoutDisabled', popBtn.disabled && popBtn.title === '送出第一則訊息後才能在新視窗開啟' && popItemDisabled === true, { popBtn, popItemDisabled });
  check('draftWarnFollowsAccount', /尚未登入/.test(warnBefore || '') && warnAfter === null, { warnBefore, warnAfter });
  await page.click(`.tab[data-key="${draftId}"] .tab-x`);

  // 送出後焦點留在輸入框：等待核准的卡片出現時不搶焦點（接著打的「1」不能變成核准）
  await page.click('.tab[data-key="c2"]');
  await page.click(`${c2} .cinput`);
  await page.keyboard.type('先跑一次測試');
  await page.keyboard.press('Enter');
  await until(() => host.last('send', (m) => m.conversationId === 'c2'), 2000);
  // 對話上方常駐的「最近的指令」：送出的這則立刻換上
  const lpText = (s) => page.evaluate((sel) => { const el = document.querySelector(sel + ' .lastprompt'); return el && !el.hidden ? el.querySelector('.lp-body').textContent : null; }, s);
  const lpSent = await until(async () => ((await lpText(c2)) === '最近的指令先跑一次測試' ? true : null), 2000);
  check('lastPromptFollowsSend', !!lpSent, await lpText(c2));
  const cv2 = host.conv('c2');
  const approval = (id) => ({ id, kind: 'Approval', status: 'Waiting', time: Date.now(), toolKind: 'bash', toolName: '執行指令', title: 'rm -rf build', detail: '', canAllowForSession: true });
  const ap1 = approval('fx-ap1');
  cv2.items.push(ap1);
  host.pushItem('c2', ap1);
  await until(() => page.$(`${c2} .it[data-id="fx-ap1"]`), 2000);
  await sleep(100);
  const focusWhileTyping = await activeName();
  await page.keyboard.type('1 build');
  await sleep(200);
  const approvedWhileTyping = !!host.last('approve', (m) => m.itemId === 'fx-ap1');
  const typed = await page.inputValue(`${c2} .cinput`);
  check('cardKeepsComposerFocus', focusWhileTyping === 'cinput' && !approvedWhileTyping && typed === '1 build', { focusWhileTyping, approvedWhileTyping, typed });
  // 焦點不在任何輸入欄位時：照舊移到卡片，數字鍵直接作答
  await page.fill(`${c2} .cinput`, '');
  await page.evaluate(() => { if (document.activeElement) document.activeElement.blur(); });
  const ap2 = approval('fx-ap2');
  cv2.items.push(ap2);
  host.pushItem('c2', ap2);
  await until(() => page.$(`${c2} .it[data-id="fx-ap2"]`), 2000);
  await sleep(100);
  const focusWhenIdle = await activeName();
  await page.keyboard.press('1');
  const approvedIdle = await until(() => host.last('approve', (m) => m.itemId === 'fx-ap2'), 2000);
  check('cardTakesFocusWhenIdle', /it-approval/.test(focusWhenIdle) && !!approvedIdle && approvedIdle.choice === 'allow', { focusWhenIdle, approve: approvedIdle });

  // 待辦：這一輪進行中的清單顯示在輸入框上方；上一輪的（宿主結束時標成 Done）不在之後每一輪顯示
  await sleep(500); // 等假宿主模擬的這一輪結束
  const todoText = () => page.evaluate((s) => { const t = document.querySelector(s + ' .todo'); return getComputedStyle(t).display !== 'none' ? t.textContent : null; }, c2);
  const plan = { id: 'fx-plan', kind: 'Plan', status: 'Running', time: Date.now(), html: '', plan: [{ text: '讀取設定', status: 'completed' }, { text: '修改程式', status: 'inProgress' }] };
  cv2.items.push(plan);
  cv2.header = Object.assign({}, cv2.header, { running: true, status: '思考中…', startedAt: Date.now() });
  host.pushHeader('c2'); host.pushItem('c2', plan);
  await sleep(200);
  const todoThisTurn = await todoText();
  plan.status = 'Done';
  cv2.header = Object.assign({}, cv2.header, { running: false, status: null, startedAt: null });
  host.pushItem('c2', plan); host.pushHeader('c2');
  await sleep(150);
  cv2.header = Object.assign({}, cv2.header, { running: true, status: '思考中…', startedAt: Date.now() });
  host.pushHeader('c2');
  await sleep(200);
  const todoNextTurn = await todoText();
  cv2.header = Object.assign({}, cv2.header, { running: false, status: null, startedAt: null });
  host.pushHeader('c2');
  check('todoOnlyCurrentTurn', !!todoThisTurn && todoThisTurn.includes('修改程式') && todoNextTurn === null, { todoThisTurn, todoNextTurn });

  // 只載入最後一頁時：時鐘往回調後的新項目（time 比第一筆早、seq 在最後）照常接在最後；真正較早的項目不插進來
  await page.click('.tab[data-key="c1"]');
  await sleep(200);
  const first = host.conv('c1').items[0];
  host.pushItem('c1', { id: 'fx-clock', seq: 1000, kind: 'Notice', status: 'Done', time: first.time - 3600e3, html: '<p>時鐘往回調之後的新項目</p>' });
  host.pushItem('c1', { id: 'fx-older', seq: 1, kind: 'Notice', status: 'Done', time: first.time - 7200e3, html: '<p>還沒載入的較早項目</p>' });
  await sleep(300);
  const seqState = await page.evaluate((s) => { const ids = Array.from(document.querySelectorAll(s + ' .items > .it')).map((n) => n.dataset.id); return { last: ids[ids.length - 1], olderInserted: ids.includes('fx-older') }; }, c1);
  check('newItemOrderBySeq', seqState.last === 'fx-clock' && !seqState.olderInserted, seqState);

  // 往上捲出現「最新」鈕之後宿主重送整個對話（換帳戶等）：已經在最底，「最新」鈕要收起
  const sbox = await page.locator(`${c1} .scroller`).boundingBox();
  await page.mouse.move(sbox.x + sbox.width / 2, sbox.y + sbox.height / 2);
  for (let i = 0; i < 4; i++) { await page.mouse.wheel(0, -500); await sleep(80); }
  await sleep(300);
  const jumpBefore = await visible(page, `${c1} .jump`);
  host.sendConversation('c1');
  await sleep(400);
  const jumpAfter = await visible(page, `${c1} .jump`);
  check('jumpHiddenAfterReload', jumpBefore && !jumpAfter, { jumpBefore, jumpAfter });

  // 回覆裡的影片播放中切到別的分頁：暫停（隱藏不會讓影片停下來）
  if (HAS_VIDEO) {
    await scrollItem(page, 'c1', 'i15', 'center');
    // 影片在 iframe 裡（framedvideo.js：內建控制列的載入轉圈會讓主文件的動畫每轉一圈都叫醒主執行緒），外框照影片的長寬比；
    // iframe 裡的右鍵、拖放、按鍵照樣交給頁面：右鍵不出瀏覽器的選單、拖檔案進來出附加遮罩、播放器裡按 Esc 關閉
    const framed = await page.evaluate(async (s) => {
      const f = document.querySelector(s + ' .mvid iframe.vframe');
      const v = f && f.contentDocument && f.contentDocument.querySelector('video');
      if (!v) return { frame: !!f, video: false };
      for (let i = 0; i < 40 && v.readyState < 1; i++) await new Promise((r) => setTimeout(r, 100));
      const menu = new MouseEvent('contextmenu', { bubbles: true, cancelable: true });
      v.dispatchEvent(menu);
      const dt = new DataTransfer();
      dt.items.add(new File(['x'], 'a.txt', { type: 'text/plain' }));
      v.dispatchEvent(new DragEvent('dragenter', { bubbles: true, cancelable: true, dataTransfer: dt }));
      const drop = document.querySelector(s + ' .drop');
      const dropShown = !!drop && !drop.hidden;
      document.querySelector(s).dispatchEvent(new DragEvent('dragleave', { bubbles: true, cancelable: true, dataTransfer: dt }));
      // 焦點移進 iframe 時 window 會收到 blur：視窗其實還有焦點，裝飾的動畫不停（theme.js）
      v.focus();
      window.dispatchEvent(new Event('blur'));
      await new Promise((r) => setTimeout(r, 50));
      const activeWhileInFrame = document.documentElement.dataset.window !== 'inactive' && document.activeElement === f;
      v.blur();
      return { activeWhileInFrame, frame: true, video: true, ready: v.readyState, aspect: f.style.aspectRatio, controls: v.controls, inMainDoc: document.querySelectorAll('video').length,
        menuPrevented: menu.defaultPrevented, dropShown, dropHiddenAfter: !!drop && drop.hidden };
    }, c1);
    check('videoFramed', framed.video && framed.ready >= 1 && framed.aspect === '640 / 360' && framed.controls && framed.inMainDoc === 0
      && framed.menuPrevented && framed.dropShown && framed.dropHiddenAfter && framed.activeWhileInFrame, framed);
    await page.click(c1 + ' .mvid-cap button[aria-label="放大播放"]');
    await page.waitForFunction(() => { const f = document.querySelector('.vp-stage iframe.vframe'); const v = f && f.contentDocument && f.contentDocument.querySelector('video'); return v && v.readyState >= 1; }, null, { timeout: 5000 }).catch(() => {});
    const viewerEsc = await page.evaluate(async () => {
      const v = document.querySelector('.vp-stage iframe.vframe').contentDocument.querySelector('video');
      v.pause();
      v.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
      await new Promise((r) => setTimeout(r, 300));
      return { closed: !document.querySelector('.vp') };
    });
    check('videoViewerEscFromFrame', viewerEsc.closed, viewerEsc);
    const playing = await page.evaluate(async (s) => { const v = document.querySelector(s + ' .mvid iframe.vframe').contentDocument.querySelector('video'); v.muted = true; try { await v.play(); } catch (e) { return String(e); } return !v.paused; }, c1);
    await sleep(500);
    await page.click('.tab[data-key="c2"]');
    await sleep(300);
    const pausedAfterSwitch = await page.evaluate((s) => document.querySelector(s + ' .mvid iframe.vframe').contentDocument.querySelector('video').paused, c1);
    check('videoPausesWhenHidden', playing === true && pausedAfterSwitch, { playing, pausedAfterSwitch });
  }

  // 拖放遮罩：串流中拖過正在長的訊息再拖離（拖到側欄、拖出視窗），遮罩不能卡住；放下照常附加
  // （CDP 的拖放和從檔案總管拖進來走同一條路徑；CDP 的 dragCancel 不送事件，拖出視窗改用視窗外的 dragOver）
  await page.click('.crow[data-key="c:c3"]');
  await waitConv(page, 'c3');
  await sleep(300);
  const c3 = convSel('c3');
  const dragFile = path.join(OUT, 'drag-probe.txt');
  fs.writeFileSync(dragFile, 'probe');
  const cdp = await context.newCDPSession(page);
  const dragData = { items: [], files: [dragFile], dragOperationsMask: 1 };
  const drag = (type, x, y) => cdp.send('Input.dispatchDragEvent', { type, x, y, data: dragData });
  const overlayShown = () => visible(page, `${c3} .drop`);
  await page.evaluate(() => {
    let n = 0;
    window.__probeStream = setInterval(() => {
      n++;
      window.__host({ type: 'item', conversationId: 'c3', item: { id: 'k4', kind: 'Assistant', status: 'Running', time: Date.now(), html: '<p>' + '串流中的回覆，'.repeat(60 + n) + '</p>' } });
    }, 50);
  });
  await sleep(200);
  const over = await page.evaluate((s) => { const r = document.querySelector(s + ' .it[data-id="k4"] .md p').getBoundingClientRect(); return [Math.round(r.x + 60), Math.round(r.y + 12)]; }, c3);
  const side = await page.evaluate(() => { const r = document.querySelector('#sidebar').getBoundingClientRect(); return [Math.round(r.x + 60), Math.round(r.y + r.height / 2)]; });
  const hover = async () => { await drag('dragEnter', over[0], over[1]); for (let i = 0; i < 12; i++) { await drag('dragOver', over[0] + (i % 3), over[1]); await sleep(40); } };
  await hover();
  const shownWhileOver = await overlayShown();
  await drag('dragOver', side[0], side[1]);
  await sleep(80);
  const shownAtSidebar = await overlayShown();
  await drag('dragCancel', side[0], side[1]);
  await hover();
  await drag('dragOver', -40, -40);
  await sleep(80);
  const shownOutsideWindow = await overlayShown();
  await hover();
  await drag('drop', over[0], over[1]);
  const dropped = await until(() => host.last('addFiles', (m) => m.conversationId === 'c3'), 2000);
  const shownAfterDrop = await overlayShown();
  await page.evaluate(() => clearInterval(window.__probeStream));
  check('dropOverlayWhileStreaming', shownWhileOver && !shownAtSidebar && !shownOutsideWindow && !!dropped && !shownAfterDrop,
    { shownWhileOver, shownAtSidebar, shownOutsideWindow, addFiles: dropped && dropped.__files, shownAfterDrop });

  // 刪除確認：沒選資料夾（預設工作區）的對話要說清楚工作區裡的檔案也會一起刪除（標題列與側欄兩條路）
  const wsOf = (id) => 'C:\\Users\\Lin\\AppData\\Local\\Myriagent\\conversations\\' + id + '\\workspace';
  const c6 = host.conv('c6');
  // 兩個對話都還有另開的程式在跑（2026-10-24）：確認框多一個「一併停止」的勾選，標頭帶 programs、清單帶 programCount
  const progs = [{ id: '11-1', pid: 11, name: 'node.exe', command: 'node server.js', startedAt: Date.now() - 60e3 }, { id: '12-1', pid: 12, name: 'python.exe', command: 'python job.py', startedAt: Date.now() - 30e3 }];
  c6.header = Object.assign({}, c6.header, { customFolder: false, workingDirectory: wsOf('c6'), programs: progs });
  for (const id of ['c6', 'c8']) Object.assign(host.conversations.find((x) => x.id === id), { customFolder: false, workingDirectory: wsOf(id), programCount: id === 'c8' ? 1 : 2 });
  host.pushConversations();
  await page.click('.crow[data-key="c:c6"]');
  await waitConv(page, 'c6');
  await page.click(`${convSel('c6')} .ch .ibtn[aria-label="更多動作"]`);
  await page.click('.menu .menu-item:has(.mi-label:text-is("刪除對話…"))');
  await page.waitForSelector('.dlg-confirm');
  await sleep(200);
  const fromHeader = await page.textContent('.dlg-confirm .dlg-body');
  await shot(page, `delete-confirm-workspace-${tag}`);
  await page.click('.dlg-confirm .btn:not(.danger-solid)');
  await sleep(200);
  await page.click('.crow[data-key="c:c8"]', { button: 'right' });
  await page.click('.menu .menu-item:has(.mi-label:text-is("刪除…"))');
  await page.waitForSelector('.dlg-confirm');
  await sleep(200);
  const fromSidebar = await page.textContent('.dlg-confirm .dlg-body');
  await page.click('.dlg-confirm .btn:not(.danger-solid)');
  await sleep(200);
  check('deleteWarnsDefaultWorkspace', /預設工作區裡的所有檔案/.test(fromHeader) && /預設工作區裡的所有檔案/.test(fromSidebar) && !host.last('delete'), { fromHeader, fromSidebar });
  check('deleteAsksStopPrograms', /一併停止這個對話另開、還在跑的 2 個程式/.test(fromHeader) && /一併停止這個對話另開、還在跑的程式/.test(fromSidebar), { fromHeader, fromSidebar });
  // 勾著（預設）確認：delete 帶 stopPrograms
  await page.click('.crow[data-key="c:c8"]', { button: 'right' });
  await page.click('.menu .menu-item:has(.mi-label:text-is("刪除…"))');
  await page.waitForSelector('.dlg-confirm');
  const boxChecked = await page.$eval('.dlg-confirm .check-row input[type=checkbox]', (b) => b.checked);
  await shot(page, `delete-confirm-programs-${tag}`);
  await page.click('.dlg-confirm .btn.danger-solid');
  const delWithStop = await until(() => host.last('delete', (m) => m.conversationId === 'c8'), 2000);
  check('deleteStopsPrograms', boxChecked && !!delWithStop && delWithStop.stopPrograms === true, { boxChecked, delete: delWithStop });

  // 最近的指令不在已載入的那一頁（長的一輪）：桌面的標頭附 lastPrompt；按箭頭先往前載入到那則再捲過去
  const steps = Array.from({ length: 30 }, (_, i) => ({ id: 'lp-t' + i, kind: 'Tool', status: 'Done', time: Date.now() - 60e3, toolKind: 'bash', toolName: '執行指令', title: 'npm test #' + i }));
  const prompt = { id: 'lp-u', kind: 'User', status: 'Done', time: Date.now() - 3600e3, html: '把整個專案的測試跑完，<br>失敗的都修好', attachments: [] };
  Object.assign(host.conv('c6'), { items: steps.slice(10), older: [prompt].concat(steps.slice(0, 10)), hasMore: true });
  host.conv('c6').header = Object.assign({}, host.conv('c6').header, { lastPrompt: { id: 'lp-u', seq: 1, time: prompt.time, text: '把整個專案的測試跑完，\n失敗的都修好', attachments: 0 } });
  // c6 在上面已經開著：照宿主整份重送（同一個分頁重建）
  await page.click('.crow[data-key="c:c6"]');
  await waitConv(page, 'c6');
  host.sendConversation('c6');
  await until(() => page.$(`${convSel('c6')} .it[data-id="lp-t29"]`), 2000);
  await settle(page, 200);
  const lpConv = convSel('c6');
  const lp6 = () => page.evaluate((s) => { const el = document.querySelector(s + ' .lastprompt'); return el && { hidden: el.hidden, older: el.classList.contains('lp-older'), text: el.querySelector('.lp-body').textContent }; }, lpConv);
  const before = await lp6();
  await shot(page, `chat-lastprompt-header-${tag}`);
  await page.click(`${lpConv} .lastprompt .ibtn`);
  const older6 = await until(() => host.last('loadOlder', (m) => m.conversationId === 'c6'), 2000);
  const landed = await until(() => page.evaluate((s) => {
    const n = document.querySelector(s + ' .it[data-id="lp-u"]');
    if (!n) return null;
    const off = n.getBoundingClientRect().top - document.querySelector(s + ' .scroller').getBoundingClientRect().top;
    return off >= 0 && off < 40 ? Math.round(off) : null;
  }, lpConv), 2500);
  check('lastPromptFromHeaderJumpLoadsOlder', !!before && !before.hidden && !before.older && before.text === '最近的指令把整個專案的測試跑完，\n失敗的都修好'
    && !!older6 && older6.beforeId === 'lp-t10' && landed !== null, { before, loadOlder: older6, landed });
  await closeApp(app);
}

// ───── 輸入區、項目與窗格：串流中的點擊與選取、推理強度、貼上、提示、Esc、輸入框高度、晚到的回覆 ─────
/** 按住 holdMs 毫秒再放開（串流中節點若在按下到放開之間被換掉，click 就不會觸發） */
async function holdClick(page, selector, holdMs = 100) {
  const box = await page.locator(selector).first().boundingBox();
  if (!box) return false;
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.down();
  await sleep(holdMs);
  await page.mouse.up();
  return true;
}

/** 在頁面裡每 ms 毫秒送一次宿主訊息，模擬串流：item 的最後一段越來越長、header 的狀態文字一直變 */
async function startStream(page, o) {
  await page.evaluate(({ conv, ms, item, header }) => {
    let i = 0;
    window.__stream = setInterval(() => {
      i++;
      if (item) window.__host({ type: 'item', conversationId: conv, item: Object.assign({}, item, { time: Date.now(), html: item.html + '<p>' + '串流文字'.repeat(i) + '</p>' }) });
      if (header) window.__host({ type: 'header', conversationId: conv, header: Object.assign({}, header, { status: '執行指令：第 ' + i + ' 次更新' }) });
    }, ms);
  }, { conv: o.conv, ms: o.ms || 50, item: o.item || null, header: o.header || null });
}
const stopStream = (page) => page.evaluate(() => clearInterval(window.__stream));

/** 輸入框內容是否完整可見（沒被裁掉：內容不比框高，或已經可以捲動） */
const taFits = (page, conv) => page.evaluate((c) => {
  const t = document.querySelector(`.conv[data-conv="${c}"] .cinput`);
  const oy = getComputedStyle(t).overflowY;
  return { clientH: t.clientHeight, scrollH: t.scrollHeight, overflowY: oy, fits: t.scrollHeight <= t.clientHeight + 1 || oy === 'auto' };
}, conv);

async function runComposerFixes(vp) {
  const tag = String(vp.w);
  const app = await openApp('fixes-' + tag, { vp, layout: { panes: [{ id: 'p1', tabs: ['c1', 'c2'], active: 'c1' }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' } });
  const { page, host } = app;
  await waitConv(page, 'c1');
  await settle(page, 300);
  const c1 = convSel('c1');
  const ta1 = `${c1} .cinput`;
  const hd1 = host.data.c1.header;
  const count = (cmd, pred = () => true) => host.received.filter((m) => m.cmd === cmd && pred(m)).length;

  // 待辦標題與排隊的「×」：執行中每 50ms 一則項目＋header，按住 100ms 再放開也要有反應
  await startStream(page, { conv: 'c1', item: { id: 'sx1', kind: 'Assistant', status: 'Running', html: '<p>開頭</p>' }, header: hd1 });
  await sleep(250);
  let todoToggles = 0;
  for (let i = 0; i < 6; i++) {
    const before = await page.evaluate((s) => document.querySelector(s + ' .todo').classList.contains('open'), c1);
    await holdClick(page, `${c1} .todo-head`);
    await sleep(80);
    const after = await page.evaluate((s) => document.querySelector(s + ' .todo').classList.contains('open'), c1);
    if (before !== after) todoToggles++;
  }
  if (!(await page.$(`${c1} .queue-row`))) await page.click(`${c1} .queue-head`);
  const cancels = [];
  for (let i = 0; i < 2; i++) {
    const n = count('cancelQueued');
    await holdClick(page, `${c1} .queue-row .ibtn`);
    cancels.push(!!(await until(() => count('cancelQueued') > n, 1000)));
    await sleep(150);
  }
  await stopStream(page);
  check('clicksSurviveStreamingUpdates', todoToggles === 6 && cancels.every(Boolean), { todoToggles, cancels });

  // 串流中的回覆：前面已完成的程式碼區塊「複製」按住再放開要觸發；選取的文字不被每次更新清掉
  const intro = '<p>先看這段：</p><pre><code class="language-js">const timeout = 3 * 60 * 1000;\n</code></pre>' + '<p>說明：逾時後回報登入沒有完成，並清掉 loginBusy。</p>'.repeat(14);
  await startStream(page, { conv: 'c1', item: { id: 'sx2', kind: 'Assistant', status: 'Running', html: intro }, header: hd1 });
  await sleep(300);
  await page.evaluate(() => {
    window.__copyClicks = 0;
    document.addEventListener('click', (e) => { if (e.target.closest && e.target.closest('.it[data-id="sx2"] .code-copy')) window.__copyClicks++; }, true);
    document.querySelector('.conv[data-conv="c1"] .it[data-id="sx2"] .code').scrollIntoView({ block: 'center' });
  });
  await sleep(250);
  for (let i = 0; i < 5; i++) { await holdClick(page, `${c1} .it[data-id="sx2"] .code-copy`); await sleep(120); }
  const copyClicks = await page.evaluate(() => window.__copyClicks);
  const selection = await page.evaluate(async () => {
    const p = document.querySelector('.conv[data-conv="c1"] .it[data-id="sx2"] .md > p');
    const r = document.createRange();
    r.selectNodeContents(p);
    window.getSelection().removeAllRanges();
    window.getSelection().addRange(r);
    await new Promise((res) => setTimeout(res, 450));
    return { text: String(window.getSelection()), connected: p.isConnected };
  });
  const actsWhileRunning = await page.evaluate(() => { const a = document.querySelector('.conv[data-conv="c1"] .it[data-id="sx2"] .it-acts'); return a ? getComputedStyle(a).display : null; });
  await stopStream(page);
  await page.evaluate(() => window.getSelection().removeAllRanges());
  check('streamingKeepsFinishedBlocks', copyClicks === 5 && selection.text === '先看這段：' && selection.connected && actsWhileRunning === 'none',
    { copyClicks, selection, actsWhileRunning });

  // 代理回覆完成後：滑過出現「複製」，點了送 copyItem（宿主複製 Markdown 原文）
  await scrollItem(page, 'c1', 'i12', 'center');
  await page.hover(`${c1} .it[data-id="i12"] .md h3`);
  await sleep(250);
  const actsOpacity = await page.evaluate(() => { const a = document.querySelector('.conv[data-conv="c1"] .it[data-id="i12"] .it-acts'); return a ? getComputedStyle(a).opacity : null; });
  await shot(page, `fix-copy-reply-hover-${tag}`);
  await page.click(`${c1} .it[data-id="i12"] .it-acts .code-copy`, { timeout: 2000 }).catch(() => {});
  const copyItem = await until(() => host.last('copyItem', (m) => m.itemId === 'i12' && m.conversationId === 'c1'), 1000);
  check('copyWholeReply', actsOpacity === '1' && !!copyItem, { actsOpacity, copyItem });

  // 推理強度：header 帶著模型不支援的值時如實顯示「不支援」、選單可改回預設；換到不支援推理強度的模型時按鈕隱藏（宿主改回預設）
  host.data.c1.header = Object.assign({}, host.data.c1.header, { model: 'sonnet', effort: 'max' });
  await host.pushHeader('c1');
  await sleep(150);
  const effLabel = await page.textContent(`${c1} .pick[title^="推理強度"] .pick-label`);
  await page.click(`${c1} .pick[title^="推理強度"]`);
  await page.waitForSelector('.menu');
  const effItems = await page.$$eval('.menu .menu-item', (n) => n.map((x) => x.querySelector('.mi-label').textContent + (x.classList.contains('checked') ? '✓' : '')));
  await shot(page, `fix-effort-unsupported-${tag}`);
  await page.click('.menu .menu-item:has(.mi-label:text-is("預設"))');
  const effDefault = await until(() => host.last('setOption', (m) => m.key === 'effort' && m.value === 'default'), 1000);
  host.data.c1.header = Object.assign({}, host.data.c1.header, { model: 'opus', effort: 'max' });
  await host.pushHeader('c1');
  await sleep(150);
  await page.click(`${c1} .pick[title^="模型"]`);
  await page.click('.menu .menu-item:has(.mi-label:text-is("Haiku 5"))');
  const haiku = await until(() => host.last('setOption', (m) => m.key === 'model' && m.value === 'haiku'), 1000);
  await sleep(200);
  const effHidden = !(await visible(page, `${c1} .pick[title^="推理強度"]`));
  check('effortShownTruthfully', effLabel === '最高（不支援）' && effItems.join('|') === '預設|低|中|高|最高✓' && !!effDefault && !!haiku && effHidden && host.data.c1.header.effort === 'default',
    { effLabel, effItems, effDefault: !!effDefault, haiku: !!haiku, effHidden, hostEffort: host.data.c1.header.effort });

  // Ultracode：Claude 帳戶能用時支援 xhigh 的模型多一項（名稱 Ultracode、附說明、提示是 CLI 的值 ultracode，排在最後）
  const ucBefore = host.data.c1.header;
  host.data.c1.header = Object.assign({}, ucBefore, { model: 'opus', effort: 'xhigh' });
  await host.pushHeader('c1');
  await sleep(150);
  await page.click(`${c1} .pick[title^="推理強度"]`);
  await page.waitForSelector('.menu');
  const ucItem = await page.$$eval('.menu .menu-item', (n) => {
    const x = n.find((i) => i.querySelector('.mi-label').textContent === 'Ultracode');
    return x ? { desc: x.querySelector('.mi-desc') ? x.querySelector('.mi-desc').textContent : null, hint: x.querySelector('.mi-hint') ? x.querySelector('.mi-hint').textContent : null, last: n[n.length - 1] === x } : null;
  });
  await shot(page, `effort-ultracode-${tag}`);
  await page.click('.menu .menu-item:has(.mi-label:text-is("Ultracode"))');
  const ucSet = await until(() => host.last('setOption', (m) => m.key === 'effort' && m.value === 'ultracode'), 1000);
  await sleep(150);
  const ucLabel = await page.textContent(`${c1} .pick[title^="推理強度"] .pick-label`);
  check('effortUltracode', !!ucItem && ucItem.last && ucItem.hint === 'ultracode' && !!ucItem.desc && !!ucSet && ucLabel === 'Ultracode', { ucItem, ucSet: !!ucSet, ucLabel });
  host.data.c1.header = ucBefore;
  await host.pushHeader('c1');
  await sleep(150);

  // 貼上：剪貼簿有文字＋系統合成的點陣圖（Excel 等）時照常貼文字；只有點陣圖（截圖）才附加
  const pasteText = await page.evaluate(() => {
    const t = document.querySelector('.conv[data-conv="c1"] .cinput');
    const dt = new DataTransfer();
    dt.setData('text/plain', '項目\t前\t後\n首次載入\t2.1 秒\t0.3 秒');
    dt.setData('text/html', '<table><tr><td>項目</td><td>前</td><td>後</td></tr></table>');
    dt.items.add(new File([new Uint8Array([137, 80, 78, 71])], 'image.png', { type: 'image/png' }));
    const ev = new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true });
    t.dispatchEvent(ev);
    return { prevented: ev.defaultPrevented };
  });
  await sleep(300);
  const imageFromText = count('pasteImage');
  await page.evaluate(() => {
    const t = document.querySelector('.conv[data-conv="c1"] .cinput');
    const dt = new DataTransfer();
    dt.items.add(new File([new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10])], 'image.png', { type: 'image/png' }));
    t.dispatchEvent(new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true }));
  });
  const bitmap = await until(() => host.last('pasteImage', (m) => m.name === 'image.png'), 1500);
  check('pasteTextWithBitmapKeepsText', !pasteText.prevented && imageFromText === 0 && !!bitmap, { pasteText, imageFromText, bitmapAttached: !!bitmap });

  // @ 檔案提示還在搜尋時按 Enter：不送出半成品，結果一到插入第一項；找不到時保留原文
  host.fileSearchDelay = 500;
  await page.fill(ta1, '');
  await page.focus(ta1);
  const sendsBefore = count('send');
  await page.keyboard.type('請看 @app');
  await page.keyboard.press('Enter');
  await sleep(100);
  const loadingText = await page.textContent(`${c1} .suggest`).catch(() => '');
  await until(() => page.evaluate((s) => /@src\\Myriagent\\Web\\app\.(js|css) $/.test(document.querySelector(s).value), ta1), 3000);
  const inserted = await page.inputValue(ta1);
  await page.fill(ta1, '');
  await page.keyboard.type('@zzqqxx');
  await until(() => page.evaluate((s) => /找不到符合/.test((document.querySelector(s + ' .suggest') || {}).textContent || ''), c1), 3000);
  await page.keyboard.press('Enter');
  await sleep(100);
  const kept = await page.inputValue(ta1);
  host.fileSearchDelay = 40;
  await page.fill(ta1, '');
  check('enterWhileSearchingDoesNotSend', count('send') === sendsBefore && /搜尋檔案中/.test(loadingText) && /^請看 @src\\Myriagent\\Web\\app\.(js|css) $/.test(inserted) && kept === '@zzqqxx',
    { sends: count('send') - sendsBefore, loadingText, inserted, kept });

  // 工具輸出：捲到中間時新輸出不會把位置拉回頂端；跟著尾端看時完成也維持在尾端
  const tool = host.data.c1.items.find((x) => x.id === 'i21');
  const lines = (n) => Array.from({ length: n }, (_, i) => '  測試 ' + (i + 1) + '：通過').join('\n');
  await scrollItem(page, 'c1', 'i21', 'center');
  await page.click(`${c1} .it[data-id="i21"] .t-row`);
  tool.output = lines(60);
  await host.pushItem('c1', tool);
  await sleep(150);
  const outSel = `${c1} .it[data-id="i21"] .pre.out`;
  await page.evaluate((s) => { document.querySelector(s).scrollTop = 120; }, outSel);
  tool.output = lines(70);
  await host.pushItem('c1', tool);
  await sleep(150);
  const midTop = await page.evaluate((s) => document.querySelector(s).scrollTop, outSel);
  await page.evaluate((s) => { const o = document.querySelector(s); o.scrollTop = o.scrollHeight; }, outSel);
  tool.output = lines(80);
  tool.status = 'Done';
  await host.pushItem('c1', tool);
  await sleep(150);
  const endState = await page.evaluate((s) => { const o = document.querySelector(s); return { top: Math.round(o.scrollTop), max: o.scrollHeight - o.clientHeight }; }, outSel);
  check('toolOutputKeepsScroll', Math.abs(midTop - 120) <= 2 && endState.max > 0 && endState.top >= endState.max - 8, { midTop, endState });

  // 核准卡輸入框裡按 Esc 不會停止；拒絕與「繼續規劃」附的說明送出後留在卡片上
  await scrollItem(page, 'c1', 'i20', 'center');
  const stopsBeforeTell = count('stop');
  await page.focus(`${c1} .it[data-id="i20"] .ap-tell`);
  await page.keyboard.press('Escape');
  await sleep(100);
  const tellEscStops = count('stop') - stopsBeforeTell;
  await page.fill(`${c1} .it[data-id="i20"] .ap-tell`, '請改用資源回收筒');
  await page.click(`${c1} .it[data-id="i20"] .opt.deny`);
  await until(() => page.$(`${c1} .it[data-id="i20"] .card-note`), 1500);
  const apNote = await page.textContent(`${c1} .it[data-id="i20"] .card-note`).catch(() => null);
  await page.fill(`${c1} .it[data-id="i18"] .ap-tell`, '先補測試再把逾時移到設定');
  await page.click(`${c1} .it[data-id="i18"] .opt.deny`);
  await until(() => page.$(`${c1} .it[data-id="i18"] .card-note`), 1500);
  const planNote = await page.textContent(`${c1} .it[data-id="i18"] .card-note`).catch(() => null);
  await scrollItem(page, 'c1', 'i20', 'center');
  await shot(page, `fix-approval-feedback-${tag}`);
  check('decidedCardsShowFeedback', tellEscStops === 0 && apNote === '你的說明：請改用資源回收筒' && planNote === '你的說明：先補測試再把逾時移到設定', { tellEscStops, apNote, planNote });

  // 執行中按 Esc＝停止這一輪；/ 提示開著時第一下只關提示
  await page.fill(ta1, '');
  await page.focus(ta1);
  await page.keyboard.type('/');
  await until(() => page.$(`${c1} .suggest[data-type="cmd"]:not([hidden])`), 1500);
  const stops0 = count('stop');
  await page.keyboard.press('Escape');
  await sleep(100);
  const suggestClosed = !(await visible(page, `${c1} .suggest`));
  const stops1 = count('stop');
  await page.keyboard.press('Escape');
  const stopped = await until(() => count('stop', (m) => m.conversationId === 'c1') > stops1, 1000);
  await page.fill(ta1, '');
  check('escStopsRunningTurn', suggestClosed && stops1 === stops0 && !!stopped, { suggestClosed, stopsOnFirstEsc: stops1 - stops0, stopped: !!stopped });

  // 開啟對話的回覆抵達前：工具列的選單按鈕不顯示（點不到）；回覆到達前就關掉分頁，晚到的回覆不會再開出分頁
  host.holdOpen.add('c6');
  await page.click('.crow[data-key="c:c6"]');
  await until(() => host.last('open', (m) => m.conversationId === 'c6'), 1000);
  await sleep(100);
  const pickersHidden = await page.evaluate(() => {
    const bs = Array.from(document.querySelectorAll('.conv[data-conv="c6"] .cbar .perm, .conv[data-conv="c6"] .cbar .pick'));
    return bs.length === 6 && bs.every((b) => getComputedStyle(b).display === 'none'); // 權限、模型、推理強度、代理、風格、Codex 工具
  });
  await page.keyboard.press('Control+w');
  const closedC6 = await until(() => host.last('close', (m) => m.conversationId === 'c6'), 1000);
  await host.release('c6');
  await sleep(300);
  const tabsAfter = await page.$$eval('.tab', (n) => n.map((x) => x.dataset.key));
  host.holdOpen.delete('c6');
  await page.click('.crow[data-key="c:c6"]');
  await waitConv(page, 'c6');
  const reopened = await page.evaluate(() => !!document.querySelector('.tab.active[data-key="c6"]'));
  check('lateOpenReplyAfterCloseIgnored', pickersHidden && !!closedC6 && !tabsAfter.includes('c6') && reopened, { pickersHidden, closed: !!closedC6, tabsAfter, reopened });

  // 宿主主動重送整份（reload：複本整份換掉）：不是 open 的回覆。open 的回覆到達前先來一則，不抵掉等待的計數（之後關掉分頁，晚到的回覆照樣丟掉）；
  // 已經關掉的分頁收到不會重開（第二輪合併前審查 R1）
  const reloadOf = (id) => {
    const c = host.conv(id);
    return { type: 'conversation', conversationId: id, paneId: null, header: c.header, items: c.items, hasMore: c.hasMore, composer: c.composer, reload: true };
  };
  const closesBefore = count('close', (m) => m.conversationId === 'c6');
  await page.keyboard.press('Control+w');
  await until(() => count('close', (m) => m.conversationId === 'c6') > closesBefore, 1000);
  host.holdOpen.add('c6');
  await page.click('.crow[data-key="c:c6"]');
  await until(() => host.last('open', (m) => m.conversationId === 'c6'), 1000);
  await host.send(reloadOf('c6'));
  await sleep(100);
  const closes1 = count('close', (m) => m.conversationId === 'c6');
  await page.keyboard.press('Control+w');
  const closedAgain = await until(() => count('close', (m) => m.conversationId === 'c6') > closes1, 1000);
  await host.release('c6');
  await sleep(300);
  host.holdOpen.delete('c6');
  const tabsAfterLateReply = await page.$$eval('.tab', (n) => n.map((x) => x.dataset.key));
  await host.send(reloadOf('c6'));
  await sleep(300);
  const tabsAfterReload = await page.$$eval('.tab', (n) => n.map((x) => x.dataset.key));
  check('reloadDoesNotReopenClosedTab', !!closedAgain && !tabsAfterLateReply.includes('c6') && !tabsAfterReload.includes('c6'),
    { closedAgain: !!closedAgain, tabsAfterLateReply, tabsAfterReload });

  // 多行草稿：分割窗格（輸入框變窄）、改字級、分頁隱藏時被退回，輸入框高度都會重算，不會被裁掉
  await page.click('.tab[data-key="c2"]');
  await waitConv(page, 'c2');
  const ta2 = `${convSel('c2')} .cinput`;
  const draft = Array.from({ length: 4 }, (_, i) => '第 ' + (i + 1) + ' 點：把縮圖產生搬到背景執行緒、用 LRU 限制記憶體，並在資料夾很大時分批載入，避免畫面卡住。').join('\n');
  await page.fill(ta2, draft);
  await sleep(150);
  const fitBefore = await taFits(page, 'c2');
  await page.keyboard.press('Control+Backslash');
  await page.waitForSelector('.pane[data-pane="p2"] .conv:not(.loading)');
  await sleep(400);
  const fitSplit = await taFits(page, 'c2');
  await shot(page, `fix-split-draft-${tag}`);
  await host.send({ type: 'settings', settings: Object.assign({}, host.settings, { fontSize: 18 }) });
  await sleep(300);
  const fitFont = await taFits(page, 'c2');
  await host.send({ type: 'settings', settings: Object.assign({}, host.settings, { fontSize: 14 }) });
  await sleep(200);
  await page.fill(ta2, '');
  await page.click('.pane[data-pane="p1"] .tab[data-key="c1"]');
  await sleep(150);
  await host.send({ type: 'sendRejected', conversationId: 'c2', text: draft });
  await sleep(150);
  await page.click('.pane[data-pane="p1"] .tab[data-key="c2"]');
  await sleep(300);
  const fitRestored = await taFits(page, 'c2');
  check('draftHeightFollowsPaneChanges', fitBefore.fits && fitSplit.fits && fitFont.fits && fitRestored.fits && fitRestored.clientH > 60,
    { fitBefore, fitSplit, fitFont, fitRestored });
  await closeApp(app);

  // 3～4 窗格（2×2）＋長草稿：輸入框上限以窗格高度計算，訊息串留得下來、送出／停止鈕不被下一排窗格蓋住
  const grid = await openApp('grid-draft', {
    vp: { w: 1366, h: 657 },
    layout: { panes: [{ id: 'p1', tabs: ['c3'], active: 'c3' }, { id: 'p2', tabs: ['c2'], active: 'c2' }, { id: 'p3', tabs: ['c1'], active: 'c1' }, { id: 'p4', tabs: ['c4'], active: 'c4' }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' },
  });
  await waitConv(grid.page, 'c4');
  await settle(grid.page, 300);
  await grid.page.fill(`${convSel('c3')} .cinput`, Array.from({ length: 25 }, (_, i) => 'Error ' + (i + 1) + ': 登入逾時，WaitForLoginAsync 沒有回應').join('\n'));
  await sleep(250);
  const g = await grid.page.evaluate(() => {
    const conv = document.querySelector('.conv[data-conv="c3"]');
    const pr = conv.closest('.pane').getBoundingClientRect();
    const dr = conv.querySelector('.dock').getBoundingClientRect();
    const hitOk = (b) => { const r = b.getBoundingClientRect(); const el = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2); return !!el && (el === b || b.contains(el)); };
    const t = conv.querySelector('.cinput');
    return {
      scrollerH: conv.querySelector('.scroller').clientHeight, dockOverflow: Math.round(dr.bottom - pr.bottom),
      sendHit: hitOk(conv.querySelector('.cbtn.send')), stopHit: hitOk(conv.querySelector('.cbtn.stop')), taH: t.clientHeight, overflowY: getComputedStyle(t).overflowY,
    };
  });
  await shot(grid.page, 'fix-grid-longdraft-1366x657');
  check('gridLongDraftFitsPane', g.scrollerH >= 40 && g.dockOverflow <= 0 && g.sendHit && g.stopHit && g.overflowY === 'auto', g);
  await closeApp(grid);
}

// ───── 殼層互動：雙擊、鍵盤焦點、選單、檢視器、帳戶色、主題 ─────
/** 真人速度的雙擊：兩下間隔 150ms，第二下帶 clickCount 2（Playwright 的 dblclick 兩下之間沒有間隔） */
async function humanDblclick(page, sel) {
  const loc = page.locator(sel);
  await loc.scrollIntoViewIfNeeded();
  const b = await loc.boundingBox();
  await humanDblclickAt(page, b.x + b.width / 2, b.y + b.height / 2);
}
async function humanDblclickAt(page, x, y) {
  await page.mouse.move(x, y);
  await page.mouse.down({ clickCount: 1 });
  await page.mouse.up({ clickCount: 1 });
  await sleep(150);
  await page.mouse.down({ clickCount: 2 });
  await page.mouse.up({ clickCount: 2 });
}
/** 目前焦點：fe:檔名／lb:…（燈箱內）／app:…（主介面）／body */
const focusDesc = (page) => page.evaluate(() => {
  const a = document.activeElement;
  if (!a || a === document.body) return 'body';
  const fe = a.closest('.fe');
  const where = a.closest('.lb') ? 'lb:' : a.closest('.vp') ? 'vp:' : a.closest('#app') ? 'app:' : '';
  return fe ? 'fe:' + fe.querySelector('.fe-name').textContent : where + (a.getAttribute('aria-label') || a.className || a.tagName);
});
const focusChanged = (page, prev) => until(async () => { const d = await focusDesc(page); return d !== prev ? d : null; }, 2000);

/** WCAG 對比（與網頁的實作分開算，確認 inkOn 的結果） */
function wcagContrast(a, b) {
  const lum = (hex) => {
    const n = parseInt(hex.slice(1), 16);
    const lin = (v) => { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); };
    return 0.2126 * lin((n >> 16) & 255) + 0.7152 * lin((n >> 8) & 255) + 0.0722 * lin(n & 255);
  };
  const x = lum(a), y = lum(b);
  return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05);
}
const rgbToHex = (rgb) => '#' + (rgb.match(/\d+/g) || []).slice(0, 3).map((v) => Number(v).toString(16).padStart(2, '0')).join('');

async function runShell(vp) {
  const tag = String(vp.w);
  const app = await openApp('shell-' + tag, { vp, layout: { panes: [{ id: 'p1', tabs: ['c2'], active: 'c2' }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' } });
  const { page, host } = app;
  await waitConv(page, 'c2');
  const c2 = convSel('c2');
  const assets = F.ROOT + '\\assets';
  // app 內無法顯示的圖片與影片（宿主只對可直接顯示的格式給 url）
  host.files[assets].push(
    { name: '相機原檔.heic', path: assets + '\\相機原檔.heic', kind: 'image', size: 2400000, modified: NOW - 3600000, thumbUrl: null, url: null, hidden: false },
    { name: '螢幕錄影.mkv', path: assets + '\\螢幕錄影.mkv', kind: 'video', size: 8400000, modified: NOW - 7200000, thumbUrl: null, url: null, hidden: false });

  // 檔案抽屜：雙擊資料夾只進入一層，第二下不會開到新清單同位置的檔案
  await page.click(`${c2} .ch .ibtn[aria-label="檔案瀏覽器"]`);
  await page.waitForSelector(`${c2} .drawer.open .fe`);
  const mark = host.received.length;
  await humanDblclick(page, `${c2} .drawer .fe.k-dir:has(.fe-name:text-is("docs"))`);
  await sleep(500);
  const dbl = host.received.slice(mark).filter((m) => m.cmd === 'listFiles' || m.cmd === 'openPath').map((m) => m.cmd + ' ' + (m.path || ''));
  check('drawerDblclickFolderOnce', dbl.length === 1 && dbl[0] === 'listFiles ' + F.ROOT + '\\docs', dbl);

  // 鍵盤（焦點在雙擊第二下按到的項目上）：Backspace 回上層後焦點在剛才的資料夾，Enter 進入後焦點在第一個項目，可以一直操作下去
  const k0 = await focusDesc(page);
  await page.keyboard.press('Backspace');
  const k1 = await focusChanged(page, k0);
  await page.keyboard.press('Enter');
  const k2 = await focusChanged(page, k1);
  await page.keyboard.press('Backspace');
  const k3 = await focusChanged(page, k2);
  await page.keyboard.press('Enter');
  const k4 = await focusChanged(page, k3);
  await page.keyboard.press('Backspace');
  const k5 = await focusChanged(page, k4);
  await sleep(150);
  await shot(page, `drawer-keyboard-focus-${tag}`);
  check('drawerKeyboardKeepsFocus', k0 && k0.startsWith('fe:') && k1 === 'fe:docs' && k2 === 'fe:plans' && k3 === 'fe:docs' && k4 === 'fe:plans' && k5 === 'fe:docs', { k0, k1, k2, k3, k4, k5 });

  // 右鍵選單：app 內無法顯示的圖片／影片只有一個「用預設程式開啟」；Esc 後焦點回到項目
  await page.click(`${c2} .drawer .fe.k-dir:has(.fe-name:text-is("assets"))`);
  await page.waitForSelector(`${c2} .drawer .fe:has(.fe-name:text-is("相機原檔.heic"))`);
  const menus = [];
  for (const n of ['相機原檔.heic', '螢幕錄影.mkv']) {
    await page.click(`${c2} .drawer .fe:has(.fe-name:text-is("${n}"))`, { button: 'right' });
    await page.waitForSelector('.menu');
    menus.push(await page.$$eval('.menu .mi-label', (x) => x.map((y) => y.textContent)));
    if (menus.length === 1) await shot(page, `drawer-menu-heic-${tag}`);
    await page.keyboard.press('Escape');
    await sleep(100);
  }
  const ctxEsc = await focusDesc(page);
  check('drawerMenuSingleOpenWithDefault', menus.every((l) => l.filter((x) => x === '用預設程式開啟').length === 1 && !l.includes('在 app 內檢視')) && ctxEsc === 'fe:螢幕錄影.mkv', { menus, ctxEsc });

  // 雙擊圖片：燈箱留著、也不會被第二下切到 100%／200%
  await humanDblclick(page, `${c2} .drawer .fe.k-image >> nth=3`);
  await sleep(500);
  const lbDbl = await page.evaluate(() => ({ open: !!document.querySelector('.lb:not(.leaving)'), zoomed: !!document.querySelector('.lb .lb-stage.zoomed') }));
  await page.keyboard.press('Escape');
  await sleep(250);
  // 關閉的淡出動畫期間（Esc 後 5ms）再按 Enter：燈箱要開著、焦點在燈箱裡
  await page.focus(`${c2} .drawer .fe.k-image >> nth=1`);
  await page.keyboard.press('Enter');
  await page.waitForSelector('.lb .lb-img');
  await sleep(300);
  await page.keyboard.press('Escape');
  await sleep(5);
  await page.keyboard.press('Enter');
  await sleep(500);
  const lbReopen = await page.evaluate(() => !!document.querySelector('.lb:not(.leaving)') && !!document.activeElement.closest('.lb'));
  // Tab 只在燈箱裡循環，不會跑到被遮罩蓋住的介面
  const tabs = [];
  for (let i = 0; i < 14; i++) { await page.keyboard.press('Tab'); tabs.push(await focusDesc(page)); }
  await shot(page, `lightbox-tab-${tag}`);
  await page.keyboard.press('Escape');
  await sleep(250);
  const lbBack = await focusDesc(page);
  // 單擊背景照樣關閉（左上角的空白：避開垂直置中的「上一張」按鈕）
  await page.click(`${c2} .drawer .fe.k-image >> nth=0`);
  await page.waitForSelector('.lb .lb-img');
  await sleep(400);
  await page.mouse.click(20, 120);
  await sleep(300);
  const lbBgClose = await page.evaluate(() => !document.querySelector('.lb:not(.leaving)'));
  if (!lbBgClose) { await page.keyboard.press('Escape'); await sleep(250); } // 沒關掉也收起來，後面的檢查才不會被燈箱擋住
  check('lightboxDblclickReopenTab', lbDbl.open && !lbDbl.zoomed && lbReopen && tabs.every((t) => !t.startsWith('app:')) && lbBack === 'fe:首頁設計-淺色.png' && lbBgClose,
    { lbDbl, lbReopen, tabs, lbBack, lbBgClose });

  // 雙擊影片：第二下落在影片外的背景（把磚移到播放器左側空白處）也不關閉；單擊背景照樣關閉
  if (HAS_VIDEO) {
    const y = Math.round(vp.h / 2);
    await page.evaluate((top) => { const t = document.querySelector('.conv[data-conv="c2"] .drawer .fe.k-video'); Object.assign(t.style, { position: 'fixed', left: '0px', top: (top - 30) + 'px', width: '60px', zIndex: '5' }); }, y);
    await humanDblclickAt(page, 20, y);
    await sleep(600);
    const vpDbl = await page.evaluate(() => !!document.querySelector('.vp:not(.leaving) iframe.vframe'));
    await page.evaluate(() => document.querySelector('.conv[data-conv="c2"] .drawer .fe.k-video').removeAttribute('style'));
    await page.mouse.click(20, y);
    await sleep(300);
    const vpBgClose = await page.evaluate(() => !document.querySelector('.vp:not(.leaving)'));
    check('videoDblclickStaysOpen', vpDbl && vpBgClose, { vpDbl, vpBgClose });
  }
  await page.click(`${c2} .drawer .ibtn[aria-label="關閉檔案瀏覽器"]`);

  // 選單：沒有作用項目時 ↑ 跳到最後一項；Esc 後焦點回到「更多」；從選單開的對話框關閉後也回到「更多」
  await page.focus('.crow[data-key="c:c5"]');
  await page.keyboard.press('Tab');
  await page.keyboard.press('Enter');
  await page.waitForSelector('.menu');
  await page.keyboard.press('ArrowUp');
  const upItem = await page.textContent('.menu .menu-item.active .mi-label').catch(() => null);
  await page.keyboard.press('Escape');
  await sleep(100);
  const afterEsc = await focusDesc(page);
  await page.focus('.crow[data-key="c:c5"] .crow-more'); // 不管上一步焦點在哪，都從「更多」再開一次
  await page.keyboard.press('Enter');
  await page.waitForSelector('.menu');
  await page.keyboard.press('End'); // 刪除…
  await page.keyboard.press('Enter');
  await page.waitForSelector('.dlg-confirm');
  await sleep(150);
  await page.keyboard.press('Escape');
  await sleep(250);
  const afterDialog = await focusDesc(page);
  check('menuArrowUpAndFocusRestore', upItem === '刪除…' && afterEsc === 'app:更多' && afterDialog === 'app:更多', { upItem, afterEsc, afterDialog });

  // 側欄點一下就切過去、雙擊不改名（2026-10-26 使用者要求；改名用右鍵選單或 F2）：連點兩下只是開啟，沒有改名框、焦點在輸入框
  const sbRow = () => page.evaluate(() => ({
    active: (document.querySelector('.sb-list .crow.active') || { dataset: {} }).dataset.key || null,
    first: document.querySelector('.sb-list .crow').dataset.key,
    renaming: !!document.querySelector('.sb-list .crow-input'),
  }));
  await humanDblclick(page, '.crow[data-key="c:c4"] .crow-title');
  await sleep(300);
  const dblRow = await sbRow();
  const dblFocus = await focusDesc(page);
  check('sidebarDblclickOpensNoRename', dblRow.active === 'c:c4' && !dblRow.renaming && !host.last('rename', (m) => m.conversationId === 'c4')
    && !dblFocus.includes('對話標題'), { dblRow, dblFocus });

  // 按著的時候清單重排（這一輪結束、別的對話跳到最上面，按著的那一列被往下擠）：放開照樣切到按下的那一列，不必點第二下；放開後才補上新的順序
  const convOrder = host.conversations.slice();
  const c8 = host.conversations.find((c) => c.id === 'c8');
  const c8Was = { updatedAt: c8.updatedAt, group: c8.group };
  const c5Box = await page.locator('.crow[data-key="c:c5"]').boundingBox();
  await page.mouse.move(c5Box.x + c5Box.width / 2, c5Box.y + c5Box.height / 2);
  await page.mouse.down();
  Object.assign(c8, { updatedAt: Date.now(), group: '今天' });
  host.conversations = [c8].concat(host.conversations.filter((c) => c !== c8));
  host.pushConversations();
  await sleep(200);
  const heldRow = await sbRow();
  await page.mouse.up();
  await sleep(200);
  const pressRow = await sbRow();
  check('sidebarClickSurvivesReorder', heldRow.first === 'c:c1' && pressRow.active === 'c:c5' && pressRow.first === 'c:c8', { heldRow, pressRow });
  Object.assign(c8, c8Was);
  host.conversations = convOrder;
  host.pushConversations();
  await sleep(150);

  // 側欄右鍵：自動重新命名（autoTitle）；產生期間（清單的 titling）這一列轉圈、選單項目停用，結果回來後換標題
  await page.click('.crow[data-key="c:c4"]', { button: 'right' });
  await page.waitForSelector('.menu');
  const rowMenuLabels = await page.$$eval('.menu .menu-item .mi-label', (ns) => ns.map((n) => n.textContent));
  await page.click('.menu .menu-item:has(.mi-label:text-is("自動重新命名"))');
  const rowAuto = await until(() => host.last('autoTitle', (m) => m.conversationId === 'c4'), 2000);
  const c4 = host.conversations.find((c) => c.id === 'c4');
  c4.titling = true;
  host.pushConversations();
  await sleep(150);
  const rowSpin = await page.$eval('.crow[data-key="c:c4"] .crow-titling', (n) => n.getBoundingClientRect().width > 0 && !n.closest('.crow').querySelector('.crow-spin')).catch(() => false);
  await shot(page, `sidebar-autotitle-${tag}`);
  await page.click('.crow[data-key="c:c4"]', { button: 'right' });
  await page.waitForSelector('.menu');
  const rowBusy = await page.$eval('.menu .menu-item:has(.mi-label:text-is("正在產生標題…"))', (n) => n.disabled).catch(() => null);
  await page.keyboard.press('Escape');
  Object.assign(c4, { titling: false, title: '側欄自動命名' });
  host.pushConversations();
  await sleep(150);
  const rowAfter = await page.$eval('.crow[data-key="c:c4"]', (n) => ({ text: n.querySelector('.crow-title').textContent, spin: !!n.querySelector('.crow-titling') }));
  check('sidebarContextMenuAutoTitle', rowMenuLabels.join('/') === '開啟/重新命名/自動重新命名/在新視窗開啟/延伸成新對話/刪除…' && !!rowAuto && rowSpin && rowBusy === true
    && rowAfter.text === '側欄自動命名' && !rowAfter.spin, { rowMenuLabels, autoTitle: rowAuto, rowSpin, rowBusy, rowAfter });

  // 改名中清單重排（例如這一輪結束、對話跳到最上面）：改名框留著，打到一半的標題不會被送出
  await page.focus('.crow[data-key="c:c6"]');
  await page.keyboard.press('F2');
  await page.keyboard.press('Control+a');
  await page.keyboard.type('新名');
  const c6 = host.conversations.find((c) => c.id === 'c6');
  Object.assign(c6, { updatedAt: Date.now(), group: '今天' });
  host.conversations = [c6].concat(host.conversations.filter((c) => c !== c6));
  await host.pushConversations();
  await sleep(150);
  const midRename = await page.evaluate(() => { const i = document.querySelector('.crow[data-key="c:c6"] .crow-input'); return !!i && document.activeElement === i; });
  const early = host.last('rename', (m) => m.conversationId === 'c6');
  await page.keyboard.type('稱');
  await page.keyboard.press('Enter');
  const finalRename = await until(() => host.last('rename', (m) => m.conversationId === 'c6'), 2000);
  await sleep(150);
  const firstRow = await page.$eval('.sb-list .crow', (n) => n.dataset.key);
  check('renameSurvivesListReorder', midRename && !early && !!finalRename && finalRename.title === '新名稱' && firstRow === 'c:c6', { midRename, early, finalRename, firstRow });

  // 帳戶精簡卡內的「＋」按 Enter：用這個帳戶開新對話，不切換篩選
  await page.focus('.macct[data-key="a2"]');
  await page.keyboard.press('Tab');
  const onPlus = await page.evaluate(() => !!document.activeElement && document.activeElement.classList.contains('macct-new'));
  const ncBefore = host.received.filter((m) => m.cmd === 'newConversation').length;
  await page.keyboard.press('Enter');
  const plus = await until(() => (host.received.filter((m) => m.cmd === 'newConversation').length > ncBefore ? host.last('newConversation') : null), 2000);
  const filtered = await page.evaluate(() => !!document.querySelector('.macct.filtered'));
  check('accountCardPlusKeyboard', onPlus && !!plus && plus.accountId === 'a2' && !filtered, { onPlus, newConversation: plus, filtered });

  // 帳戶精簡卡不顯示 email（只在儀表板的帳戶大卡）：卡片上沒有 email、高度不隨 email 有無改變；滑過的提示仍帶 email
  const readCards = () => page.$$eval('.macct', (cards) => Object.fromEntries(cards.map((c) => [c.dataset.key,
    { text: c.textContent, height: Math.round(c.getBoundingClientRect().height), title: c.title }])));
  const mc0 = await readCards();
  host.accounts.find((a) => a.id === 'a4').email = 'team@example.com';
  host.accounts.find((a) => a.id === 'a1').email = null;
  host.pushAccounts();
  const mc1 = await until(async () => { const r = await readCards(); return r.a4.title.includes('team@example.com') ? r : null; }, 2000);
  const noEmail = (m) => !!m && Object.values(m).every((x) => !x.text.includes('@'));
  check('accountCardNoEmail', noEmail(mc0) && noEmail(mc1) && mc0.a1.title.includes('lin.dev@example.com') && !mc1.a1.title.includes('@')
    && mc1.a4.title.includes('team@example.com') && Object.keys(mc0).every((k) => mc0[k].height === mc1[k].height),
    { before: mc0, after: mc1 });

  // 帳戶精簡卡的用量圓環：在卡片右側、佔名稱與狀態兩行的高度，百分比（帶 %）與重置倒數寫在圈內（軌道內徑 28px）；
  // 執行中的帳戶外圈刻度一格一格繞（fx-rot）、圈內有雷達掃描（spin），沒在執行的沒有動畫；100% 也放得進圈內
  host.accounts.find((a) => a.id === 'a3').usage.windows[0].percent = 100;
  host.pushAccounts();
  const readGauges = () => page.$$eval('.macct', (cards) => Object.fromEntries(cards.map((c) => {
    const top = c.querySelector('.macct-top').getBoundingClientRect(), sub = c.querySelector('.macct-sub').getBoundingClientRect();
    return [c.dataset.key, Array.from(c.querySelectorAll('.mmeter')).map((m) => {
      const mb = m.getBoundingClientRect(), tb = m.querySelector('.mmeter-txt').getBoundingClientRect();
      const cx = mb.left + mb.width / 2, cy = mb.top + mb.height / 2;
      const before = getComputedStyle(m, '::before'), after = getComputedStyle(m, '::after');
      return {
        text: m.querySelector('.mmeter-n').textContent, size: [Math.round(mb.width), Math.round(mb.height)],
        spansRows: mb.top <= top.top + 1 && mb.bottom >= sub.bottom - 1 && mb.left >= Math.max(top.right, sub.right),
        inside: Math.abs(tb.left + tb.width / 2 - cx) <= 1.5 && Math.abs(tb.top + tb.height / 2 - cy) <= 1.5 && tb.width <= 28 && tb.height <= 28,
        anim: [before.animationName, after.content === 'none' ? 'none' : after.animationName],
      };
    })];
  })));
  const gz = await until(async () => { const r = await readGauges(); return r.a3[0] && r.a3[0].text === '100%' ? r : null; }, 2000) || await readGauges();
  await shot(page, `sidebar-gauges-${tag}`);
  const gaugesOk = (list, n, anim) => list.length === n && list.every((g) => g.size.join() === '40,40' && g.spansRows && g.inside && g.anim.join() === anim);
  // 沒在執行的卡：已登入的是怠速（fxrules.accountPulse 的 idle：外圈一段淡的亮格繞，fx-rot，沒有雷達），沒登入的不動
  const pulses = await page.$$eval('.macct', (cards) => Object.fromEntries(cards.map((c) => [c.dataset.key, c.dataset.pulse || null])));
  const restAnim = (k) => (pulses[k] === 'idle' ? 'fx-rot,none' : 'none,none');
  check('accountCardGauge', gaugesOk(gz.a1, 2, 'fx-rot,spin') && gaugesOk(gz.a3, 1, restAnim('a3')) && gaugesOk(gz.a5, 2, restAnim('a5')) && gaugesOk(gz.a6, 1, restAnim('a6'))
    && gz.a1[0].text === '34%' && gz.a3[0].text === '100%' && gz.a4.length === 0 && gz.a5[0].text === '19%' && gz.a5[1].text === '8%', Object.assign({ pulses }, gz));
  host.accounts.find((a) => a.id === 'a3').usage.windows[0].percent = 18;
  host.pushAccounts();
  await until(async () => { const r = await readGauges(); return r.a3[0] && r.a3[0].text === '18%'; }, 2000);

  // 帳戶精簡卡的重置倒數：每個圓環內百分比下方的短格式（無條件捨去：3d／2h／47m／<1m），每分鐘的計時器會刷新；已過或沒有重置時間就不顯示
  const mn = 60e3, hr = 3600e3, dy = 86400e3;
  const t0 = Date.now();
  const setReset = (id, i, at) => { host.accounts.find((a) => a.id === id).usage.windows[i].resetsAt = at; };
  setReset('a1', 0, t0 + (2 * 60 + 13) * mn + 30000);
  setReset('a1', 1, t0 + 3 * dy + 4 * hr);
  setReset('a2', 0, t0 + 47 * mn + 30000);
  setReset('a2', 1, t0 + 30000);
  setReset('a3', 0, null);
  host.pushAccounts();
  const readResets = () => page.$$eval('.macct', (cards) => Object.fromEntries(cards.map((c) => [c.dataset.key, Array.from(c.querySelectorAll('.mmeter')).map((m) => {
    const r = m.querySelector('.mmeter-reset');
    const shown = !!r && getComputedStyle(r).display !== 'none' && r.getBoundingClientRect().width > 0;
    // 上下排：重置時間在百分比正下方、置中對齊，而且在圓環內、沒有超出帳戶卡
    const nb = m.querySelector('.mmeter-n').getBoundingClientRect(), rb = r && r.getBoundingClientRect(), cb = c.getBoundingClientRect();
    const mb = m.getBoundingClientRect();
    const stacked = !shown || (rb.top >= nb.bottom - 1 && Math.abs(rb.left + rb.width / 2 - (nb.left + nb.width / 2)) <= 1
      && rb.top >= mb.top && rb.bottom <= mb.bottom && rb.bottom <= cb.bottom);
    return { text: r ? r.textContent : null, shown, stacked, title: m.title };
  })])));
  const rs0 = await until(async () => { const r = await readResets(); return r.a1[0] && r.a1[0].text === '2h' && /2 小時 13 分/.test(r.a1[0].title) ? r : null; }, 2000) || await readResets();
  const resetsAre = (list, want) => list.length === want.length && list.every((m, i) => m.stacked && (want[i] === null ? !m.shown : m.shown && m.text === want[i]));
  // 頁面的時鐘往後撥 2 小時：app 的 1 秒計時器跨分鐘時刷新倒數（不必真的等一分鐘）
  await page.evaluate((shift) => { window.__realNow = Date.now; Date.now = () => window.__realNow() + shift; }, 2 * hr);
  const rs1 = await until(async () => { const r = await readResets(); return r.a1[0] && r.a1[0].text === '13m' ? r : null; }, 3000) || await readResets();
  await page.evaluate(() => { Date.now = window.__realNow; });
  check('accountCardResetCountdown', resetsAre(rs0.a1, ['2h', '3d']) && resetsAre(rs0.a2, ['47m', '<1m']) && resetsAre(rs0.a3, [null])
    && /5 小時：\d+%，2 小時 13 分後重置/.test(rs0.a1[0].title) && (rs0.a4 || []).length === 0
    && resetsAre(rs1.a1, ['13m', '3d']) && resetsAre(rs1.a2, [null, null]),
    { initial: rs0, afterTwoHours: rs1 });

  // 用量過時（權杖過期查不到等）：圓環前面有時鐘＋資料的年紀（琥珀色），圓環變灰；滑過看取得時間與原因；每分鐘刷新年紀；恢復後標示消失
  const a2u = host.accounts.find((a) => a.id === 'a2').usage;
  Object.assign(a2u, { stale: true, fetchedAt: Date.now() - (8 * 60 + 5) * mn, error: '登入權杖已過期，正在讓 Claude CLI 換發權杖…' });
  host.pushAccounts();
  const readStale = () => page.$$eval('.macct', (cards) => Object.fromEntries(cards.map((c) => {
    const b = c.querySelector('.mstale');
    const arc = c.querySelector('.mring-arc');
    const cb = c.getBoundingClientRect(), bb = b && b.getBoundingClientRect();
    return [c.dataset.key, {
      badge: b ? { text: b.textContent, title: b.title, color: getComputedStyle(b).color, inside: bb.right <= cb.right && bb.width > 0 } : null,
      arc: arc ? getComputedStyle(arc).stroke : null,
      meterTitle: (c.querySelector('.mmeter') || {}).title || null,
    }];
  })));
  const sv0 = await until(async () => { const r = await readStale(); return r.a2.badge && r.a2.badge.text === '8h' ? r : null; }, 2000) || await readStale();
  const gray = await page.evaluate(() => { const d = document.createElement('i'); d.style.color = 'var(--text-3)'; document.body.append(d); const c = getComputedStyle(d).color; d.remove(); return c; });
  await shot(page, `sidebar-stale-${tag}`);
  // 撥 3 小時（不是 2 小時）：上一段撥過 2 小時又撥回，計時器若還沒在真實時間跑過一次，同樣撥 2 小時會落在它記得的那一分鐘而不刷新
  await page.evaluate((shift) => { window.__realNow = Date.now; Date.now = () => window.__realNow() + shift; }, 3 * hr);
  const sv1 = await until(async () => { const r = await readStale(); return r.a2.badge && r.a2.badge.text === '11h' ? r : null; }, 3000) || await readStale();
  await page.evaluate(() => { Date.now = window.__realNow; });
  Object.assign(a2u, { stale: false, fetchedAt: Date.now(), error: null });
  host.pushAccounts();
  const sv2 = await until(async () => { const r = await readStale(); return !r.a2.badge ? r : null; }, 2000) || await readStale();
  check('accountCardStaleBadge', !!sv0.a2.badge && sv0.a2.badge.inside && sv0.a2.badge.color !== gray
    && /^用量資料可能過時，取得於 (今天|昨天) \d\d:\d\d\n登入權杖已過期，正在讓 Claude CLI 換發權杖…$/.test(sv0.a2.badge.title)
    && sv0.a2.arc === gray && sv0.a1.arc !== gray && !sv0.a1.badge && /\n用量資料可能過時/.test(sv0.a2.meterTitle) && !/過時/.test(sv0.a1.meterTitle)
    && !!sv1.a2.badge && sv1.a2.badge.text === '11h'
    && !sv2.a2.badge && sv2.a2.arc !== gray && !/過時/.test(sv2.a2.meterTitle),
    { stale: sv0, gray, afterThreeHours: sv1, recovered: sv2 });

  // 設定頁「模型設定」：每家一個子分頁（provTab 先切過去才點得到），各有權限模式、每個模型一個推理強度；完全自動要先確認；
  // 宿主重送設定（模型清單變了）時畫面跟著更新
  await page.click('#btnSettings');
  await page.click('.dlg-settings .set-tab[data-tab="defaults"]');
  await page.waitForSelector('.dlg-settings .set-sec[data-sec="defaults"] .set-prov[data-provider="Codex"]', { state: 'attached' });
  const provTab = (provider) => page.click(`.dlg-settings .set-subtab[data-provider="${provider}"]`);
  const readDefaults = () => page.$$eval('.dlg-settings .set-prov', (ps) => Object.fromEntries(ps.map((p) => [p.dataset.provider, {
    model: p.querySelector('.set-defmodel .set-pick-text').textContent,
    perm: p.querySelector('.set-perm .set-pick-text').textContent,
    models: Array.from(p.querySelectorAll('.set-model')).map((r) => ({
      v: r.dataset.model, label: r.querySelector('.set-label').textContent,
      effort: r.querySelector('.set-effort .set-pick-text').textContent, disabled: r.querySelector('.set-effort').disabled,
    })),
  }])));
  const d0 = await readDefaults();
  await shot(page, 'settings-defaults-' + tag);
  // Antigravity 一塊：沒有每步詢問（預設自動接受編輯）；模型是合併後的基本 id，每個都能選推理強度
  const agyDef = d0.Antigravity;
  check('settingsDefaultsAntigravity', !!agyDef && agyDef.perm === '自動接受編輯'
    && agyDef.models.map((m) => m.v).join(',') === 'default,gemini-4-argon,gemini-3.8-flash,gpt-oss-120b'
    && agyDef.models.every((m) => !m.disabled), agyDef);
  // Grok 一塊：五種權限模式都有、預設每步詢問；模型清單照 grok 回報（預設在最前面）
  const grokDef = d0.Grok;
  check('settingsDefaultsGrok', !!grokDef && grokDef.perm === '每步詢問' && grokDef.models.map((m) => m.v).join(',') === 'default,grok-4.7,grok-4.6,grok-4.5', grokDef);
  const sentSince = (n) => host.received.slice(n).filter((m) => m.cmd === 'setConversationDefault');
  // 預設模型：沿用上次選擇在最前面，其他是這一家的模型清單；不在清單裡的設定值也列出來（打勾）
  const readModelMenu = async (provider) => {
    await provTab(provider);
    await page.click(`.dlg-settings .set-prov[data-provider="${provider}"] .set-defmodel`);
    await page.waitForSelector('.menu .menu-item');
    return page.$$eval('.menu .menu-item', (n) => n.map((b) => (b.classList.contains('checked') ? '✓' : '') + b.querySelector('.mi-label').textContent));
  };
  let dm = host.received.length;
  const codexModelMenu = await readModelMenu('Codex');
  await page.click('.menu .menu-item:has(.mi-label:text-is("GPT-6 Astra"))');
  await until(async () => (await readDefaults()).Codex.model === 'GPT-6 Astra', 2000);
  await readModelMenu('Claude');
  await page.click('.menu .menu-item:has(.mi-label:text-is("沿用上次選擇"))');
  await until(async () => (await readDefaults()).Claude.model === '沿用上次選擇', 2000);
  const modelSent = sentSince(dm);
  host.settings.conversationDefaults.Grok.model = 'grok-4.4';
  host.send({ type: 'settings', settings: host.settings });
  const staleModel = await until(async () => { const r = await readDefaults(); return r.Grok.model === 'grok-4.4' ? r.Grok.model : null; }, 2000);
  const grokModelMenu = await readModelMenu('Grok');
  await page.keyboard.press('Escape');
  check('settingsDefaultModel', d0.Claude.model === 'Opus 5.5' && d0.Codex.model === '沿用上次選擇'
    && codexModelMenu.join(',') === '✓沿用上次選擇,預設（gpt-6.1-sol）,GPT-6.1 Sol,GPT-6 Astra'
    && JSON.stringify(modelSent.map((m) => [m.provider, m.key, m.value])) === JSON.stringify([['Codex', 'model', 'gpt-6-astra'], ['Claude', 'model', '']])
    && staleModel === 'grok-4.4' && grokModelMenu[grokModelMenu.length - 1] === '✓grok-4.4' && grokModelMenu.filter((x) => x.startsWith('✓')).length === 1,
    { initial: [d0.Claude.model, d0.Codex.model], codexModelMenu, modelSent, staleModel, grokModelMenu });
  dm = host.received.length;
  await provTab('Claude');
  await page.click('.dlg-settings .set-model[data-model="opus"] .set-effort');
  await page.click('.menu .menu-item:has(.mi-label:text-is("高"))');
  await until(async () => (await readDefaults()).Claude.models.find((x) => x.v === 'opus').effort === '高', 2000);
  await page.click('.dlg-settings .set-model[data-model="opus"] .set-effort');
  await page.click('.menu .menu-item:has(.mi-label:text-is("CLI 預設"))');
  await until(async () => (await readDefaults()).Claude.models.find((x) => x.v === 'opus').effort === 'CLI 預設', 2000);
  const effortSent = sentSince(dm);
  // 完全自動：取消不送；確認才送
  dm = host.received.length;
  await page.click('.dlg-settings .set-prov[data-provider="Claude"] .set-perm');
  await page.click('.menu .menu-item:has(.mi-label:text-is("完全自動"))');
  await page.waitForSelector('.dlg-confirm');
  const confirmShown = !!(await page.$('.dlg-confirm'));
  await page.click('.dlg-confirm .btn:not(.danger-solid)');
  await sleep(200);
  const afterCancel = sentSince(dm).length;
  await page.click('.dlg-settings .set-prov[data-provider="Claude"] .set-perm');
  await page.click('.menu .menu-item:has(.mi-label:text-is("完全自動"))');
  await page.click('.dlg-confirm .btn.danger-solid');
  await until(async () => (await readDefaults()).Claude.perm === '完全自動', 2000);
  const permSent = sentSince(dm);
  const codexMenu = await (async () => {
    await provTab('Codex');
    await page.click('.dlg-settings .set-prov[data-provider="Codex"] .set-perm');
    const labels = await page.$$eval('.menu .menu-item .mi-label', (n) => n.map((x) => x.textContent));
    await page.keyboard.press('Escape');
    return labels;
  })();
  // 宿主重送設定：新回報的模型出現在清單裡
  host.settings.conversationDefaults.Claude.models.push({ value: 'claude-opus-9', label: 'Opus 9', description: '新模型', efforts: ['low', 'high', 'max'], effort: null });
  host.send({ type: 'settings', settings: host.settings });
  const d1 = await until(async () => { const r = await readDefaults(); return r.Claude.models.some((x) => x.v === 'claude-opus-9') ? r : null; }, 2000) || await readDefaults();
  await page.keyboard.press('Escape');
  await sleep(150);
  check('settingsConversationDefaults',
    d0.Claude.perm === '自動' && d0.Codex.perm === '每步詢問'
    && d0.Claude.models.map((x) => x.v).join(',') === 'default,opus,sonnet,haiku'
    && d0.Claude.models.find((x) => x.v === 'default').effort === '高' && d0.Claude.models.find((x) => x.v === 'opus').effort === '最高'
    && d0.Claude.models.find((x) => x.v === 'sonnet').effort === 'CLI 預設'
    && d0.Claude.models.find((x) => x.v === 'haiku').disabled && d0.Claude.models.find((x) => x.v === 'opus').label === 'Opus 5.5'
    && d0.Codex.models.find((x) => x.v === 'gpt-6-astra').effort === '極限'
    && JSON.stringify(effortSent.map((m) => [m.provider, m.key, m.model, m.value])) === JSON.stringify([['Claude', 'effort', 'opus', 'high'], ['Claude', 'effort', 'opus', 'default']])
    && confirmShown && afterCancel === 0
    && permSent.length === 1 && permSent[0].provider === 'Claude' && permSent[0].key === 'permission' && permSent[0].value === 'FullAccess'
    && codexMenu.join(',') === '每步詢問,自動,完全自動'
    && d1.Claude.models.some((x) => x.v === 'claude-opus-9'),
    { initial: d0, effortSent, confirmShown, afterCancel, permSent, codexMenu, afterPush: d1 && d1.Claude.models.map((x) => x.v) });

  // 帳戶色上的字：色票與預設色都至少 4.5:1，儀表板「＋新對話」實際算出來的顏色也是
  const ink = await page.evaluate(async () => {
    const u = await import('./util.js');
    const d = await import('./dashboard.js');
    const colors = d.PALETTE.map((p) => p.c).concat(Object.values(u.DEFAULT_COLORS));
    return { pairs: colors.map((c) => [c, u.inkOn(c)]), rootInk: getComputedStyle(document.documentElement).getPropertyValue('--acc-ink').trim(), rootAcc: getComputedStyle(document.documentElement).getPropertyValue('--acc').trim() };
  });
  await page.click('#viewSwitch [data-view="dashboard"]');
  await sleep(900);
  const buttons = await page.$$eval('.acard .btn.acc', (n) => n.map((b) => { const cs = getComputedStyle(b); return [cs.color, cs.backgroundColor]; }));
  const ratios = ink.pairs.map(([c, i]) => Math.round(wcagContrast(c, i) * 100) / 100);
  const btnRatios = buttons.map(([fg, bg]) => Math.round(wcagContrast(rgbToHex(fg), rgbToHex(bg)) * 100) / 100);
  check('accentInkContrast', Math.min(...ratios) >= 4.5 && buttons.length >= 3 && Math.min(...btnRatios) >= 4.5 && wcagContrast(ink.rootAcc, ink.rootInk) >= 4.5,
    { palette: ink.pairs.map((p, i) => p.concat(ratios[i])), buttons: btnRatios, root: [ink.rootAcc, ink.rootInk] });
  await closeApp(app);
}

/** 淺色主題、init 晚到（UI 執行緒忙）：從第一個影格到 init 之後都不能出現深色 */
async function runThemeBoot(vp) {
  const recorder = `(() => {
    window.__themeFrames = [];
    const tick = () => {
      const t = document.documentElement && document.documentElement.getAttribute('data-theme');
      if (t && window.__themeFrames[window.__themeFrames.length - 1] !== t) window.__themeFrames.push(t);
      const app = document.getElementById('app');
      if (!(app && app.classList.contains('ready'))) requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  })();`;
  const app = await openApp('theme-boot', { vp, layout: { panes: [{ id: 'p1', tabs: ['c1'], active: 'c1' }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' },
    settings: { theme: 'light' }, colorScheme: 'light', initDelay: 300, initScript: recorder });
  await sleep(200);
  const frames = await app.page.evaluate(() => window.__themeFrames);
  const final = await app.page.getAttribute('html', 'data-theme');
  check('lightThemeNoDarkFlash', frames.length > 0 && frames.every((t) => t === 'light') && final === 'light', { frames, final });
  await closeApp(app);
}

// ───── 〔熱更新〕CLI 換版：對話標頭的 11 種狀態、Notice 與失效的卡片、CLI 卡（儀表板與設定對話框）、「CLI 更新後」設定 ─────
const hotSetup = (host) => {
  const hc = F.makeHotConversation(NOW);
  host.conversations.unshift(hc.summary);
  host.data.c15 = hc.data;
};

/** 對話標頭的換版狀態列（shown＝看得到：不在 hidden 裡、有大小） */
const restartBar = (page, conv) => page.evaluate((sel) => {
  const shown = (el) => !!el && !el.closest('[hidden]') && getComputedStyle(el).display !== 'none' && el.getBoundingClientRect().height > 0;
  const c = document.querySelector(sel);
  const bar = c.querySelector('.ch-restart');
  const btn = (label) => Array.from(bar.querySelectorAll('.chr-acts .btn')).find((b) => b.textContent.trim() === label);
  const pane = c.getBoundingClientRect();
  const acts = Array.from(bar.querySelectorAll('.chr-acts .btn')).filter(shown).map((b) => Math.round(b.getBoundingClientRect().right));
  return {
    shown: shown(bar), state: bar.dataset.state || null, tone: bar.dataset.tone || null,
    text: bar.querySelector('.chr-text').textContent, ver: shown(bar.querySelector('.chr-ver')) ? bar.querySelector('.chr-ver').textContent : null,
    wait: shown(bar.querySelector('.chr-wait')) ? bar.querySelector('.chr-wait').textContent : null,
    apply: shown(btn('立即套用')), cont: shown(btn('繼續')), spinner: !!bar.querySelector('.chr-icwrap .spin'),
    running: c.classList.contains('running'),
    overflow: bar.scrollWidth > bar.clientWidth + 1, buttonsInside: acts.every((r) => r <= Math.round(pane.right)),
  };
}, convSel(conv));

/** CLI 卡的狀態（scope：儀表板 #dashboard 或設定對話框 .dlg-settings） */
const cliCardState = (page, p, scope = '#dashboard') => page.evaluate(([sc, key]) => {
  const shown = (el) => !!el && !el.closest('[hidden]') && getComputedStyle(el).display !== 'none' && el.getBoundingClientRect().height > 0;
  const c = document.querySelector(sc + ' .cli-grid .ccard[data-key="' + key + '"]');
  const txt = (sel) => { const el = c.querySelector(sel); return shown(el) ? el.textContent : null; };
  const rows = {};
  for (const dt of c.querySelectorAll('.ccard-rows dt')) rows[dt.textContent] = dt.nextElementSibling.textContent;
  const upd = c.querySelector('.cc-upd');
  const rs = c.querySelector('.cc-restart');
  return {
    pill: txt('.pill'), broken: txt('.cc-broken .cc-main'), changing: shown(c.querySelector('.cc-changing')),
    upd: shown(upd) ? {
      title: txt('.cc-upd .cc-main'), when: txt('.cc-upd .cc-when'), detail: txt('.cc-upd .cc-sub'), q: txt('.cc-q'), tone: upd.dataset.tone,
      cur: Array.from(c.querySelectorAll('.cc-step')).findIndex((s) => s.classList.contains('cur')), done: c.querySelectorAll('.cc-step.done').length,
      progress: Array.from(upd.querySelectorAll('.cc-sub')).filter(shown).map((x) => x.textContent),
    } : null,
    restart: shown(rs) ? {
      title: txt('.cc-restart .cc-main'), hint: txt('.cc-restart .cc-sub'),
      parts: Array.from(c.querySelectorAll('.cc-part')).filter(shown).map((x) => x.textContent),
      convs: Array.from(c.querySelectorAll('.cc-conv')).filter(shown).map((x) => [x.dataset.conv, x.querySelector('.cc-conv-title').textContent, x.querySelector('.cc-conv-state').textContent]),
    } : null,
    buttons: Array.from(c.querySelectorAll('button')).filter(shown).filter((b) => !b.classList.contains('cc-conv')).map((b) => b.textContent.trim()),
    rows, msg: txt('.ccard-msg'),
    // 卡片自己的內容溢出；卡片超出所在的格子（設定對話框的兩欄），或格子超出外層（對話框、儀表板）
    overflow: c.scrollWidth > c.clientWidth + 1 || Array.from(c.querySelectorAll('.cc-block')).filter(shown).some((b) => b.scrollWidth > b.clientWidth + 1),
    outside: (() => {
      const grid = c.parentElement, outer = grid.parentElement;
      const cr = c.getBoundingClientRect(), gr = grid.getBoundingClientRect(), or = outer.getBoundingClientRect();
      return cr.left < gr.left - 1 || cr.right > gr.right + 1 || gr.right > or.right + 1 || grid.scrollWidth > grid.clientWidth + 1;
    })(),
  };
}, [scope, p]);

const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);

async function runHotUpdate(vp, theme, behavior) {
  const tag = vp.w + '-' + theme;
  const sfx = '_' + vp.w + '_' + theme;
  const app = await openApp('hot-' + tag, { vp, layout: onePane('c15'), settings: { theme }, colorScheme: theme, hostSetup: hotSetup });
  const { page, host } = app;
  const c15 = convSel('c15');
  await waitConv(page, 'c15');
  await settle(page, 300);
  const count = (cmd, pred = () => true) => host.received.filter((m) => m.cmd === cmd && pred(m)).length;
  const setRestart = async (id, r) => {
    const c = host.conv(id);
    c.header = Object.assign({}, c.header, { restart: r }, r ? { running: F.RESTART_RUNNING.has(r.state) } : {});
    await host.send({ type: 'header', conversationId: id, header: c.header });
    await sleep(90);
  };
  let casedHeader = null, casedNotice = false;
  const lowContrast = {};
  const measure = async (sels) => {
    for (const s of sels) {
      const r = await page.evaluate(contrastOf, s);
      if (r === null || r < 4.5) lowContrast[s] = r;
    }
  };

  // ── 對話標頭：11 種狀態各自的說明、色調、按鈕；detail 是 null 時用狀態的預設說法；restart 為 null 時收起 ──
  const states = {};
  let statesOk = true;
  let appUpdateIcon = false;
  for (const s of F.RESTART_STATES) {
    const r = F.restartHeader(s);
    await setRestart('c15', r);
    const b = await restartBar(page, 'c15');
    states[s] = b;
    const ok = b.shown && b.state === s && b.text === r.detail && b.apply === !!r.canApplyNow && b.cont === !!r.canContinue
      && b.spinner === (s === 'restarting') && b.running === F.RESTART_RUNNING.has(s) && !b.overflow && b.buttonsInside
      && b.ver === (r.from || r.to ? (r.from || '舊版') + ' → ' + (r.to || '新版') : null)
      && (r.since ? /^已等 3:2\d$/.test(b.wait || '') : b.wait === null);   // 已等待的時間（3 分 25 秒前排定；探針慢的話多幾秒）
    if (!ok) statesOk = false;
    if (['waitingTool', 'waitingUser', 'continued', 'failed', 'nextSend'].includes(s)) await measure(['.ch-restart .chr-text', '.ch-restart .chr-ver']);
    if (s === 'waitingBackground') await measure(['.ch-restart .chr-wait']);
    if (s === 'appUpdate') await measure(['.ch-restart .chr-text']);   // 沒有版本箭頭
    // 等 app 更新：下載圖示（不認得的狀態會退回時鐘圖示，色調同樣是 wait，只比色調分不出來）
    if (s === 'appUpdate') {
      appUpdateIcon = await page.evaluate(async (sel) => {
        const { icon } = await import('./icons.js');
        const ic = document.querySelector(sel + ' .ch-restart .chr-icwrap .ic');
        return !!ic && ic.innerHTML === icon('download').innerHTML;
      }, c15);
    }
  }
  await setRestart('c15', F.restartHeader('waitingTool', { detail: null }));
  const fallback = await restartBar(page, 'c15');
  await setRestart('c15', F.restartHeader('appUpdate', { detail: null }));
  const appFallback = await restartBar(page, 'c15');
  // app 更新後沒有接續的 notContinued：from／to 為 null（沒有版本箭頭），照樣有「繼續」
  await setRestart('c15', F.restartHeader('notContinued', { from: null, to: null, detail: '上次 Clode Myriagent 更新沒有完成，這個對話沒有自動接續；請確認後按「繼續」' }));
  const appNotContinued = await restartBar(page, 'c15');
  await setRestart('c15', null);
  const cleared = await restartBar(page, 'c15');
  check('hotHeaderStates' + sfx, statesOk && fallback.shown && fallback.text === 'CLI 已更新到 2.1.297，等目前的工具完成後切換' && !cleared.shown
    && states.appUpdate.tone === 'wait' && appUpdateIcon && appFallback.text === '已停下，等 Clode Myriagent 更新後自動接續'
    && appNotContinued.shown && appNotContinued.ver === null && appNotContinued.cont,
    { states, fallback, appUpdateIcon, appFallback, appNotContinued, cleared });

  // ── Notice（noticeKind: cliUpdate）與 CLI 重開後失效的卡片 ──
  {
    const c = host.conv('c15');
    const n = { id: 'h-app', seq: 2000, kind: 'Notice', noticeKind: 'appUpdate', status: 'Done', time: Date.now(), html: '<p>Clode Myriagent 已更新為 0.2.1530，已請代理從中斷處繼續</p>' };
    c.items.push(n);
    host.pushItem('c15', n);
    await sleep(120);
  }
  const appNotice = await page.evaluate(async (sel) => {
    const { icon } = await import('./icons.js');
    const el = document.querySelector(sel + ' .it[data-id="h-app"]');
    return { cls: !!el && el.classList.contains('app-update'), downloadIcon: !!el && el.querySelector('.note-ic').innerHTML === icon('download').innerHTML };
  }, c15);
  check('appUpdateNotice' + sfx, appNotice.cls && appNotice.downloadIcon, appNotice);
  // 額度用完自動換帳戶（noticeKind: accountSwitch）：人像圖示、同 cliUpdate 的框
  {
    const c = host.conv('c15');
    const n = { id: 'h-switch', seq: 2001, kind: 'Notice', noticeKind: 'accountSwitch', status: 'Done', time: Date.now(), html: '<p>「主帳戶」的額度已用完（10/24 06:10 重置），已自動改用「副帳戶」（2 小時 5 分後重置）繼續這個對話；新帳戶會收到先前對話的節錄，從中斷處接著做。</p>' };
    c.items.push(n);
    host.pushItem('c15', n);
    await sleep(120);
  }
  const switchNotice = await page.evaluate(async (sel) => {
    const { icon } = await import('./icons.js');
    const el = document.querySelector(sel + ' .it[data-id="h-switch"]');
    return { cls: !!el && el.classList.contains('account-switch'), userIcon: !!el && el.querySelector('.note-ic').innerHTML === icon('user').innerHTML,
      bg: el ? getComputedStyle(el).backgroundColor : null, text: el ? el.querySelector('.md').textContent : null };
  }, c15);
  check('accountSwitchNotice' + sfx, switchNotice.cls && switchNotice.userIcon && !!switchNotice.bg && !/rgba\(0, 0, 0, 0\)|transparent/.test(switchNotice.bg)
    && /已自動改用「副帳戶」/.test(switchNotice.text), switchNotice);
  const items = await page.evaluate(async (sel) => {
    const { icon } = await import('./icons.js');
    const same = (el, name) => !!el && el.innerHTML === icon(name).innerHTML;
    const it = (id) => document.querySelector(sel + ' .it[data-id="' + id + '"]');
    const card = (id) => ({ tag: it(id).querySelector('.result') ? it(id).querySelector('.result').textContent : null, note: it(id).querySelector('.card-note') ? it(id).querySelector('.card-note').textContent : null });
    const n6 = it('h6'), n9 = it('h9');
    return {
      cliNotice: { cls: n6.classList.contains('cli-update'), terminalIcon: same(n6.querySelector('.note-ic'), 'terminal'), text: n6.querySelector('.md').textContent, bg: getComputedStyle(n6).backgroundColor },
      plainNotice: { cls: n9.classList.contains('cli-update'), infoIcon: same(n9.querySelector('.note-ic'), 'info') },
      approval: card('h3'), question: card('h4'), plan: card('h5'), endedTurn: card('h8'),
    };
  }, c15);
  const restarted = 'CLI 已重新啟動，這張卡已失效；代理會視需要重新提出';
  const invalid = (x) => x.tag === '已失效' && x.note === restarted;
  check('hotNoticeAndInvalidCards' + sfx, items.cliNotice.cls && items.cliNotice.terminalIcon && /2\.1\.297 自動接續/.test(items.cliNotice.text) && !/rgba\(0, 0, 0, 0\)|transparent/.test(items.cliNotice.bg)
    && !items.plainNotice.cls && items.plainNotice.infoIcon && invalid(items.approval) && invalid(items.question) && invalid(items.plan)
    && items.endedTurn.tag === '已失效（這一輪已結束）' && items.endedTurn.note === null, items);
  await measure(['.it-notice.cli-update .md p', '.it-notice.app-update .md p', '.it-approval .card-note']);

  // 截圖：標頭在等工具結束（訊息串最底：Notice 與失效的卡片）、沒有自動接續（「繼續」）
  await setRestart('c15', F.restartHeader('waitingTool'));
  await page.evaluate((s) => { const sc = document.querySelector(s + ' .scroller'); sc.scrollTop = sc.scrollHeight; }, c15);
  await shot(page, 'hot-conv-waitingTool-' + tag);
  await setRestart('c15', F.restartHeader('notContinued'));
  await shot(page, 'hot-conv-notContinued-' + tag);

  if (behavior) {
    // 標頭的「立即套用」：先確認（說明會中斷工具），取消就不送；確認後送 cliApplyNow {conversationId}（不帶 provider）
    await setRestart('c15', F.restartHeader('waitingTool'));
    const applySel = c15 + ' .ch-restart .chr-acts .btn:has-text("立即套用")';
    await page.click(applySel);
    await page.waitForSelector('.dlg-confirm');
    await page.click('.dlg-confirm .btn:not(.primary)');
    await sleep(200);
    const afterCancel = count('cliApplyNow');
    await page.click(applySel);
    await page.waitForSelector('.dlg-confirm');
    const confirmText = await page.textContent('.dlg-confirm .dlg-body');
    await shot(page, 'hot-header-apply-confirm-' + tag);
    await page.click('.dlg-confirm .btn.primary');
    const applied = await until(() => host.last('cliApplyNow', (m) => m.conversationId === 'c15'), 2000);
    const restartingNow = await until(async () => { const b = await restartBar(page, 'c15'); return b.state === 'restarting' ? b : null; }, 2000);
    const continuedNow = await until(async () => { const b = await restartBar(page, 'c15'); return b.state === 'continued' ? b : null; }, 3000);
    await shot(page, 'hot-header-continued-' + tag);
    check('hotHeaderApplyNow', afterCancel === 0 && /中斷/.test(confirmText || '') && !!applied && applied.provider === undefined && !!restartingNow && !!continuedNow && !continuedNow.apply,
      { afterCancel, confirmText, applied, restartingNow: !!restartingNow, continuedNow });

    // 標頭的「繼續」：標頭每 50ms 更新一次（執行中）時按住再放開也要送出 cliContinue；送出後先停用。
    // 串流會一直重送舊的標頭：這段期間假宿主先不回應（只記錄），免得狀態列被收起又被串流叫回來
    await setRestart('c15', F.restartHeader('notContinued'));
    host.ignore.add('cliContinue');
    await startStream(page, { conv: 'c15', header: host.conv('c15').header });
    await sleep(250);
    const contSel = c15 + ' .ch-restart .chr-acts .btn:has-text("繼續")';
    await holdClick(page, contSel, 120);
    const cont = await until(() => host.last('cliContinue', (m) => m.conversationId === 'c15'), 1500);
    await sleep(150);
    const disabledAfter = await page.evaluate((s) => { const b = Array.from(document.querySelectorAll(s + ' .ch-restart .chr-acts .btn')).find((x) => x.textContent.trim() === '繼續'); return b ? b.disabled : null; }, c15);
    await stopStream(page);
    host.ignore.delete('cliContinue');
    // 新的狀態（宿主重送）：按鈕恢復可按；這次假宿主照常回應——收起狀態列、加一則 cliUpdate 的 Notice
    await setRestart('c15', F.restartHeader('notContinued', { detail: 'CLI 已更新到 2.1.297；這一輪沒有自動接續，原因：上一輪因 API 錯誤結束' }));
    const reenabled = await page.evaluate((s) => { const b = Array.from(document.querySelectorAll(s + ' .ch-restart .chr-acts .btn')).find((x) => x.textContent.trim() === '繼續'); return b ? !b.disabled : null; }, c15);
    await page.click(contSel);
    await until(() => host.received.filter((m) => m.cmd === 'cliContinue').length >= 2, 1500);
    await sleep(250);
    const afterContinue = await restartBar(page, 'c15');
    const contNotice = await page.evaluate((s) => { const n = Array.from(document.querySelectorAll(s + ' .it-notice.cli-update .md')).pop(); return n ? n.textContent : null; }, c15);
    check('hotHeaderContinue', !!cont && cont.provider === undefined && disabledAfter === true && reenabled === true && !afterContinue.shown && /不是你送的/.test(contNotice || ''),
      { cliContinue: cont, disabledAfter, reenabled, afterContinue: afterContinue.shown, contNotice });

    // 大駝峰的 state 與 noticeKind（結果併在後面的 hotEnumCasing）
    await setRestart('c15', F.restartHeader('waitingUser', { state: 'WaitingUser' }));
    casedHeader = await restartBar(page, 'c15');
    await host.send({ type: 'item', conversationId: 'c15', item: { id: 'hpc', seq: 2000, kind: 'Notice', noticeKind: 'CliUpdate', status: 'Done', time: Date.now(), html: '<p>大駝峰的 noticeKind</p>' } });
    await sleep(150);
    casedNotice = await page.evaluate((s) => { const n = document.querySelector(s + ' .it[data-id="hpc"]'); return !!n && n.classList.contains('cli-update'); }, c15);
    await host.send({ type: 'removeItem', conversationId: 'c15', id: 'hpc' });
    await setRestart('c15', null);
  }

  // ── CLI 卡（儀表板）：各情境 ──
  await page.click('#viewSwitch [data-view="dashboard"]');
  await sleep(900);
  const pushCli = async (scenario) => { host.cli = F.hotCli(NOW, scenario); await host.send({ type: 'cli', cli: host.cli }); await sleep(150); };
  const cliShot = async (name) => {
    const file = name + '.png';
    await settle(page, 150);
    await page.locator('#dashboard .cli-grid').screenshot({ path: path.join(OUT, file) });
    report.screenshots.push(file);
  };
  const cards = {};
  // 頂部列的 CLI 徽章：與 CLI 卡同一套換版／安裝狀態規則（hotupdate.js 的 cliHotState／cliBadgeState）
  const badges = {};
  const badgeOf = () => page.evaluate(() => {
    const b = document.getElementById('cliBadge');
    const shown = !!b && !b.hidden && getComputedStyle(b).display !== 'none' && b.getBoundingClientRect().height > 0;
    return { shown, text: b.textContent.trim(), tone: b.dataset.tone || null, state: b.dataset.state || null, title: b.title, spinner: !!b.querySelector('.spin') };
  });

  await pushCli('restarting');
  cards.restarting = { Claude: await cliCardState(page, 'Claude'), Codex: await cliCardState(page, 'Codex') };
  badges.broken = await badgeOf();
  await measure(['.cli-badge']);
  const summaryLine = await page.textContent('#dashboard .dash-summary');
  await page.evaluate(() => document.querySelector('#dashboard .cli-head').scrollIntoView({ block: 'start' }));
  await shot(page, 'hot-dashboard-restarting-' + tag);
  await measure(['.cc-restart .cc-main', '.cc-part', '.cc-conv-state', '.cc-conv-title', '.cc-tag', '.pill.st-busy', '.cc-broken .cc-main', '.cc-broken .cc-sub', '.cli-policy .chip-label']);
  const r1 = cards.restarting;
  const restartingOk = r1.Claude.pill === '換版中' && r1.Claude.restart && r1.Claude.restart.title === '4 個對話換版中'
    && same(r1.Claude.restart.parts, ['1 個等工具結束', '1 個等你回應', '1 個重新啟動中', '1 個已接續'])
    && same(r1.Claude.restart.convs, [['c1', '修正登入流程卡住並補上單元測試', '等工具結束：dotnet test Myriagent.slnx -c Release'], ['c6', '分頁拖曳到另一個窗格時的狀態同步', '等你回應'], ['c9', 'Markdig 停用原始 HTML 的設定', '重新啟動中']])
    && r1.Claude.buttons.includes('立即套用') && /2\.1\.295 → 2\.1\.297/.test(r1.Claude.rows['最近換版'] || '') && /外部更新/.test(r1.Claude.rows['最近換版'] || '') && !('換版結果' in r1.Claude.rows)
    && r1.Codex.pill === '安裝不一致' && r1.Codex.broken === 'Codex 安裝不一致（package.json 0.162.0、執行檔 0.160.1），請修復' && r1.Codex.restart === null
    && r1.Codex.buttons.includes('重新安裝') && !r1.Codex.buttons.includes('立即套用') && !r1.Codex.buttons.some((t) => /更新到/.test(t))
    && /3\s*個對話換版中/.test(summaryLine) && !r1.Claude.overflow && !r1.Codex.overflow;

  await pushCli('draining');
  cards.draining = { Claude: await cliCardState(page, 'Claude'), Codex: await cliCardState(page, 'Codex') };
  badges.draining = await badgeOf();
  if (vp.w === 1440 && theme === 'dark' || vp.w === 1100 && theme === 'dark') await cliShot('hot-cli-draining-' + tag);
  await measure(['.cc-upd .cc-main', '.cc-upd .cc-sub', '.cc-when', '.cc-changing .cc-sub']);
  const d1 = cards.draining;
  const drainingOk = d1.Codex.pill === '更新中' && d1.Codex.upd && d1.Codex.upd.title === '第 1／4 步・等對話停下' && d1.Codex.upd.detail === '1 個已停下、2 個還在執行（到安全點就停）'
    && d1.Codex.upd.q === null && d1.Codex.upd.cur === 0 && d1.Codex.upd.done === 0 && /^開始於 /.test(d1.Codex.upd.when || '')
    && d1.Codex.buttons.includes('現在中斷') && !d1.Codex.buttons.includes('取消更新') && !d1.Codex.buttons.includes('繼續等') && !d1.Codex.buttons.includes('立即套用')
    && !d1.Codex.buttons.some((t) => /更新到|更新中/.test(t)) && d1.Codex.msg === null
    && d1.Codex.restart && d1.Codex.restart.title === '3 個對話換版中' && same(d1.Codex.restart.parts, ['2 個等工具結束', '1 個等更新完成'])
    && d1.Claude.pill === '安裝變動中' && d1.Claude.changing && !d1.Codex.overflow;

  await pushCli('overdue');
  cards.overdue = { Claude: await cliCardState(page, 'Claude'), Codex: await cliCardState(page, 'Codex') };
  if (vp.w === 1440) await cliShot('hot-cli-overdue-' + tag);
  await measure(['.cc-q']);
  const o1 = cards.overdue;
  const overdueOk = o1.Codex.upd && o1.Codex.upd.q === '還有 2 個對話在執行工具，要等還是現在中斷？' && o1.Codex.upd.tone === 'user'
    && ['現在中斷', '取消更新', '繼續等'].every((t) => o1.Codex.buttons.includes(t))
    && o1.Claude.restart === null && o1.Claude.rows['換版結果'] === '2 個已用新版接續、3 個閒置的已關閉舊程序、1 個沒有自動接續' && o1.Claude.pill === '已是最新版';

  await pushCli('installing');
  cards.installing = { Claude: await cliCardState(page, 'Claude'), Codex: await cliCardState(page, 'Codex') };
  badges.installing = await badgeOf();
  if (vp.w === 1440 && theme === 'dark' || vp.w === 1100 && theme === 'light') await cliShot('hot-cli-installing-' + tag);
  const i1 = cards.installing;
  const installingOk = i1.Codex.upd && i1.Codex.upd.title === '第 3／4 步・換檔中' && i1.Codex.upd.cur === 2 && i1.Codex.upd.done === 2
    && i1.Codex.upd.progress.includes('正在替換 CLI 的檔案，這段期間請不要結束 app') && i1.Codex.upd.progress.includes('npm install -g @openai/codex@latest')
    && !['現在中斷', '取消更新', '繼續等', '立即套用'].some((t) => i1.Codex.buttons.includes(t))
    && i1.Codex.restart && same(i1.Codex.restart.parts, ['3 個等更新完成']);

  await pushCli('nextSend');
  cards.nextSend = { Claude: await cliCardState(page, 'Claude'), Codex: await cliCardState(page, 'Codex') };
  badges.nextSend = await badgeOf();
  if (vp.w === 1440 && theme === 'dark') await cliShot('hot-cli-nextSend-' + tag);
  const n1 = cards.nextSend;
  const nextSendOk = n1.Claude.restart && n1.Claude.restart.title === '2 個對話還在用舊版' && n1.Claude.restart.hint === '下次送出時才換新版' && n1.Claude.restart.convs.length === 0
    && n1.Claude.buttons.includes('立即套用') && /app 更新/.test(n1.Claude.rows['最近換版'] || '') && n1.Codex.restart === null;

  // 舊宿主（沒有任何〔熱更新〕欄位）：CLI 卡跟以前一樣
  host.cli = F.makeCli(NOW);
  await host.send({ type: 'cli', cli: host.cli });
  await sleep(150);
  const legacy = { Claude: await cliCardState(page, 'Claude'), Codex: await cliCardState(page, 'Codex') };
  badges.legacy = await badgeOf();
  const legacyOk = ['Claude', 'Codex'].every((p) => !legacy[p].upd && !legacy[p].restart && !legacy[p].broken && !legacy[p].changing && !('最近換版' in legacy[p].rows))
    && legacy.Claude.pill === '已是最新版' && legacy.Codex.pill === '有新版' && legacy.Codex.buttons.includes('更新到 0.162.0');

  // 徽章：只有換版中（Codex 沒有其他狀態）、只有安裝變動中；兩種 CLI 同一個狀態時合併、優先順序（純函式）
  host.cli = F.hotCli(NOW, 'restarting');
  Object.assign(host.cli.Codex, { installBroken: null, updateAvailable: false });
  await host.send({ type: 'cli', cli: host.cli });
  await sleep(150);
  badges.restarting = await badgeOf();
  const restartingPill = (await cliCardState(page, 'Claude')).pill;
  await measure(['.cli-badge']);
  if (vp.w === 1440) {
    const file = 'hot-cli-badge-restarting-' + tag + '.png';
    await page.locator('.topbar').screenshot({ path: path.join(OUT, file) });
    report.screenshots.push(file);
  }
  host.cli = F.hotCli(NOW, 'draining');
  Object.assign(host.cli.Codex, F.HOT_IDLE, { busy: false, progress: null, updateAvailable: false });
  await host.send({ type: 'cli', cli: host.cli });
  await sleep(150);
  badges.changing = await badgeOf();
  const changingPill = (await cliCardState(page, 'Claude')).pill;
  await measure(['.cli-badge']);
  const badgeRules = await page.evaluate(async () => {
    const m = await import('./hotupdate.js');
    const base = { installed: true, version: '1.0.0', latest: '1.0.0', updateAvailable: false, busy: false };
    const v = (o) => Object.assign({}, base, o);
    return {
      bothBroken: m.cliBadgeState({ Claude: v({ installBroken: 'A 壞了' }), Codex: v({ installBroken: 'B 壞了' }) }),
      bothRestarting: m.cliBadgeState({
        Claude: v({ restartPending: 2, restartWaiting: [{ conversationId: 'x', state: 'waitingTool' }, { conversationId: 'y', state: 'restarting' }] }),
        Codex: v({ restartPending: 1, restartWaiting: [{ conversationId: 'z', state: 'waitingUser' }] }),
      }),
      brokenBeatsUpdate: m.cliBadgeState({ Claude: v({ busy: true, progress: '下載中 45%' }), Codex: v({ installBroken: 'B 壞了' }) }),
      none: m.cliBadgeState({ Claude: base, Codex: base }),
      changingWhileBusy: m.cliHotState(v({ changing: true, busy: true })),
      brokenNoRestart: m.cliHotState(v({ installBroken: 'X', restartPending: 3 })),
    };
  });
  host.cli = F.makeCli(NOW);
  await host.send({ type: 'cli', cli: host.cli });
  await sleep(150);
  const b = badges;
  const badgeOk = b.broken.shown && b.broken.text === 'Codex 安裝不一致' && b.broken.tone === 'err' && b.broken.state === 'broken' && /package\.json 0\.162\.0/.test(b.broken.title)
    && cards.restarting.Codex.pill === '安裝不一致'
    && b.draining.shown && b.draining.text === 'Codex 更新中：等對話停下' && b.draining.spinner && b.draining.state === 'update' && cards.draining.Codex.pill === '更新中'
    && b.installing.text === 'Codex 更新中：npm install -g @openai/codex@latest' && b.installing.spinner
    && b.restarting.shown && b.restarting.text === 'Claude Code 換版中' && b.restarting.tone === 'busy' && !b.restarting.spinner && b.restarting.state === 'restarting'
    && /4 個對話換版中：1 個等工具結束、1 個等你回應、1 個重新啟動中、1 個已接續/.test(b.restarting.title) && restartingPill === '換版中'
    && b.changing.shown && b.changing.text === 'Claude Code 安裝變動中…' && b.changing.spinner && b.changing.tone === 'busy' && changingPill === '安裝變動中'
    && b.nextSend.text === 'Codex 有新版 0.162.0' && b.nextSend.state === 'available'
    && b.legacy.shown && b.legacy.text === 'Codex 有新版 0.162.0' && b.legacy.tone === 'update' && b.legacy.title === 'Codex 0.160.1 → 0.162.0'
    && badgeRules.bothBroken.text === 'CLI 安裝不一致' && /A 壞了/.test(badgeRules.bothBroken.title) && /B 壞了/.test(badgeRules.bothBroken.title)
    && badgeRules.bothRestarting.text === 'CLI 換版中' && badgeRules.brokenBeatsUpdate.key === 'broken'
    && badgeRules.none === null && badgeRules.changingWhileBusy === null && badgeRules.brokenNoRestart.key === 'broken';
  check('hotCliBadge' + sfx, badgeOk, { badges, restartingPill, changingPill, badgeRules });
  const cardsFit = Object.values(cards).every((sc) => ['Claude', 'Codex'].every((p) => !sc[p].overflow && !sc[p].outside));
  check('hotCliCards' + sfx, restartingOk && drainingOk && overdueOk && installingOk && nextSendOk && legacyOk && cardsFit,
    { restartingOk, drainingOk, overdueOk, installingOk, nextSendOk, legacyOk, cardsFit, cards, summaryLine, legacy });

  if (behavior) {
    // 列舉字串的大小寫：協定是小駝峰（draining、waitingTool、external），C# 列舉直接轉成字串是大駝峰——兩種都要認得
    host.cli = F.hotCli(NOW, 'draining');
    const pascal = (s) => s.charAt(0).toUpperCase() + s.slice(1);
    host.cli.Codex.update = Object.assign({}, host.cli.Codex.update, { phase: 'Draining' });
    host.cli.Codex.restartWaiting = host.cli.Codex.restartWaiting.map((w) => Object.assign({}, w, { state: pascal(w.state) }));
    Object.assign(host.cli.Claude, { changing: false, lastChange: { from: '2.1.295', to: '2.1.297', origin: 'External', at: NOW } });
    await host.send({ type: 'cli', cli: host.cli });
    await sleep(150);
    const casedCli = { Claude: await cliCardState(page, 'Claude'), Codex: await cliCardState(page, 'Codex') };
    check('hotEnumCasing', casedCli.Codex.upd && casedCli.Codex.upd.title === '第 1／4 步・等對話停下' && casedCli.Codex.buttons.includes('現在中斷')
      && casedCli.Codex.restart && same(casedCli.Codex.restart.parts, ['2 個等工具結束', '1 個等更新完成']) && /外部更新/.test(casedCli.Claude.rows['最近換版'] || '')
      && casedHeader.state === 'waitingUser' && casedHeader.tone === 'user' && casedHeader.apply && casedNotice,
      { cli: casedCli, header: casedHeader, notice: casedNotice });

    // 「立即套用」→ 確認 → cliApplyNow {provider}；「重新安裝」→ cliInstall；點換版中的對話＝開啟它並切到對話
    await pushCli('restarting');
    await page.click('#dashboard .ccard[data-key="Claude"] .cc-restart .btn:has-text("立即套用")');
    await page.waitForSelector('.dlg-confirm');
    const applyText = await page.textContent('.dlg-confirm .dlg-body');
    await page.click('.dlg-confirm .btn.primary');
    const applyAll = await until(() => host.last('cliApplyNow', (m) => m.provider === 'Claude'), 2000);
    await sleep(200);
    const afterApply = await cliCardState(page, 'Claude');
    await page.click('#dashboard .ccard[data-key="Codex"] .ccard-foot .btn');
    const reinstall = await until(() => host.last('cliInstall', (m) => m.provider === 'Codex'), 2000);
    await sleep(900);
    const afterReinstall = await cliCardState(page, 'Codex');
    await page.click('#dashboard .ccard[data-key="Claude"] .cc-conv[data-conv="c6"]');
    const opened = await until(() => host.last('open', (m) => m.conversationId === 'c6'), 2000);
    await waitConv(page, 'c6');
    const toChat = await visible(page, '#workspace');
    check('hotCliCardActions', !!applyAll && applyAll.conversationId === undefined && /以新版重開/.test(applyText || '') && afterApply.restart && afterApply.restart.parts[0] === '3 個重新啟動中'
      && !!reinstall && afterReinstall.broken === null && afterReinstall.pill !== '安裝不一致' && !!opened && toChat,
      { applyAll, applyText, afterApply: afterApply.restart, reinstall, afterReinstall: afterReinstall.pill, opened: !!opened, toChat });
    await page.click('#viewSwitch [data-view="dashboard"]');
    await sleep(400);

    // 排空中：「現在中斷」→ 確認 → cliApplyNow {provider}；逾時：「取消更新」→ cliCancelUpdate；「繼續等」什麼都不送、收起問句
    await pushCli('draining');
    await page.click('#dashboard .ccard[data-key="Codex"] .cc-upd .btn:has-text("現在中斷")');
    await page.waitForSelector('.dlg-confirm');
    await page.click('.dlg-confirm .btn.primary');
    const interrupted = await until(() => host.last('cliApplyNow', (m) => m.provider === 'Codex'), 2000);
    await sleep(150);
    const afterInterrupt = await cliCardState(page, 'Codex');
    await pushCli('overdue');
    await page.click('#dashboard .ccard[data-key="Codex"] .cc-upd .btn:has-text("取消更新")');
    const cancelled = await until(() => host.last('cliCancelUpdate', (m) => m.provider === 'Codex'), 2000);
    await sleep(150);
    const afterCancelUpd = await cliCardState(page, 'Codex');
    await pushCli('overdue');
    const before = host.received.length;
    await page.click('#dashboard .ccard[data-key="Codex"] .cc-upd .btn:has-text("繼續等")');
    await sleep(200);
    const afterWait = await cliCardState(page, 'Codex');
    const sentOnWait = host.received.slice(before).filter((m) => m.cmd !== 'layout' && m.cmd !== 'visible').map((m) => m.cmd);
    await cliShot('hot-cli-keep-waiting-' + tag);
    // 同一次更新再收到逾時的狀態也不再問；新的一次更新（startedAt 不同）照樣問
    await host.send({ type: 'cli', cli: host.cli });
    await sleep(120);
    const stillDismissed = await cliCardState(page, 'Codex');
    host.cli.Codex.update = Object.assign({}, host.cli.Codex.update, { startedAt: NOW - 15 * 60e3 });
    await host.send({ type: 'cli', cli: host.cli });
    await sleep(120);
    const newUpdate = await cliCardState(page, 'Codex');
    check('hotCliUpdateActions', !!interrupted && afterInterrupt.upd && afterInterrupt.upd.title === '第 2／4 步・等其他程序結束' && !afterInterrupt.buttons.includes('現在中斷')
      && !!cancelled && afterCancelUpd.upd === null && afterCancelUpd.restart && afterCancelUpd.restart.parts[0] === '3 個重新啟動中'
      && afterWait.upd && afterWait.upd.q === null && afterWait.buttons.includes('現在中斷') && afterWait.buttons.includes('取消更新') && !afterWait.buttons.includes('繼續等') && sentOnWait.length === 0
      && stillDismissed.upd.q === null && newUpdate.upd.q === '還有 2 個對話在執行工具，要等還是現在中斷？',
      { interrupted, afterInterrupt: afterInterrupt.upd, cancelled, afterCancelUpd: afterCancelUpd.upd, afterWait, sentOnWait, stillDismissed: stillDismissed.upd, newUpdate: newUpdate.upd });

    // 「繼續等」讓儀表板與開著的設定對話框兩邊一起收起問句；CLI 卡與「立即檢查」按鈕不記在模組的集合裡（從畫面上找），
    // 開關設定對話框幾次之後，強制回收也不會留下脫離畫面的 CLI 卡（以前關掉的對話框整棵 DOM 都被留著）
    host.cli.Codex.update = Object.assign({}, host.cli.Codex.update, { startedAt: NOW - 20 * 60e3 });
    await host.send({ type: 'cli', cli: host.cli });
    await sleep(120);
    await page.evaluate(() => document.getElementById('btnSettings').click());
    await page.waitForFunction(() => !!document.querySelector('.dlg-settings .cli-grid .ccard[data-key="Codex"]'));
    await page.evaluate(() => document.querySelector('.dlg-settings .set-tab[data-tab="cli"]').click());
    await sleep(200);
    const dlgAsk = await cliCardState(page, 'Codex', '.dlg-settings');
    // 對話框蓋在儀表板上：直接觸發儀表板那張卡的「繼續等」
    await page.evaluate(() => Array.from(document.querySelectorAll('#dashboard .ccard[data-key="Codex"] .cc-upd .btn')).find((b) => b.textContent.trim() === '繼續等').click());
    await sleep(200);
    const dlgAfter = await cliCardState(page, 'Codex', '.dlg-settings');
    const dashAfter = await cliCardState(page, 'Codex');
    await page.keyboard.press('Escape');
    await sleep(250);
    for (let i = 0; i < 6; i++) {
      await page.evaluate(() => document.getElementById('btnSettings').click());
      await page.waitForFunction(() => !!document.querySelector('.dlg-settings .cli-grid .ccard'));
      await page.keyboard.press('Escape');
      await sleep(200);
    }
    const cdp = await page.context().newCDPSession(page);
    for (let i = 0; i < 3; i++) await cdp.send('HeapProfiler.collectGarbage');
    const proto = await cdp.send('Runtime.evaluate', { expression: 'HTMLElement.prototype' });
    const all = await cdp.send('Runtime.queryObjects', { prototypeObjectId: proto.result.objectId });
    // queryObjects 也會列出 HTMLDivElement.prototype 這類原型物件：讀它們的 nodeType 會丟 Illegal invocation，逐一 try 掉
    const detached = await cdp.send('Runtime.callFunctionOn', {
      objectId: all.objects.objectId, returnByValue: true,
      functionDeclaration: 'function () { let total = 0, n = 0; for (const e of this) { try { if (e.nodeType !== 1) continue; total++; if (!e.isConnected && (e.classList.contains("ccard") || e.classList.contains("dlg-settings"))) n++; } catch (err) { } } return { total, n }; }',
    });
    await cdp.detach().catch(() => {});
    const counted = detached.result && detached.result.value;
    const leaked = counted && counted.total > 0 ? counted.n : null;
    check('hotCliCardsNoLeak', dlgAsk.upd && dlgAsk.upd.q !== null && dlgAfter.upd && dlgAfter.upd.q === null && dashAfter.upd && dashAfter.upd.q === null && leaked === 0,
      { dialogBefore: dlgAsk.upd && dlgAsk.upd.q, dialogAfter: dlgAfter.upd && dlgAfter.upd.q, dashboardAfter: dashAfter.upd && dashAfter.upd.q,
        elements: counted && counted.total, detachedCardsOrDialogs: leaked, error: detached.exceptionDetails && detached.exceptionDetails.text });
  }

  // ── 設定對話框：「CLI 更新後」三選一（預設在安全點切換）、CLI 卡在窄欄位也不溢出 ──
  await pushCli('restarting');
  await page.click('#btnSettings');
  await page.click('.dlg-settings .set-tab[data-tab="cli"]');
  await page.waitForSelector('.dlg-settings .set-radios');
  await sleep(250);
  const radios = () => page.$$eval('.dlg-settings .set-radio', (n) => n.map((b) => ({ v: b.dataset.value, on: b.getAttribute('aria-checked') === 'true', tab: b.tabIndex, focus: document.activeElement === b })));
  const r0 = await radios();
  const compact = { Claude: await cliCardState(page, 'Claude', '.dlg-settings'), Codex: await cliCardState(page, 'Codex', '.dlg-settings') };
  await page.evaluate(() => document.querySelector('.dlg-settings .set-sec[data-sec="cli"]').scrollIntoView({ block: 'start' }));
  await sleep(150);
  await shot(page, 'hot-settings-' + tag);
  await measure(['.set-radio-desc', '.set-radio-label']);
  check('hotSettingsLayout' + sfx, r0.length === 3 && same(r0.map((x) => x.v), ['SafePoint', 'AfterTurn', 'NextSend']) && r0[0].on && !r0[1].on && !r0[2].on && r0[0].tab === 0 && r0[1].tab === -1
    && !compact.Claude.overflow && !compact.Codex.overflow && !compact.Claude.outside && !compact.Codex.outside
    && compact.Claude.restart && compact.Claude.restart.title === '4 個對話換版中' && compact.Codex.buttons.includes('重新安裝'),
    { radios: r0, compact });
  if (behavior) {
    await page.click('.dlg-settings .set-radio[data-value="AfterTurn"]');
    const s1 = await until(() => host.last('setting', (m) => m.key === 'cliRestartPolicy' && m.value === 'AfterTurn'), 2000);
    await sleep(150);
    const r1s = await radios();
    await page.keyboard.press('ArrowDown');
    const s2 = await until(() => host.last('setting', (m) => m.key === 'cliRestartPolicy' && m.value === 'NextSend'), 2000);
    await sleep(150);
    const r2s = await radios();
    // 宿主改回去（例如另一個視窗改了設定）：選項跟著更新
    host.settings.cliRestartPolicy = 'SafePoint';
    await host.send({ type: 'settings', settings: host.settings });
    await sleep(150);
    const r3s = await radios();
    // 設定對話框裡點換版中的對話：關掉對話框、開啟那個對話
    await page.click('.dlg-settings .ccard[data-key="Claude"] .cc-conv[data-conv="c1"]');
    const openedC1 = await until(() => host.last('open', (m) => m.conversationId === 'c1'), 2000);
    await sleep(300);
    const dlgClosed = !(await page.$('.dlg-settings'));
    check('hotSettingsPolicy', !!s1 && r1s[1].on && !r1s[0].on && !!s2 && r2s[2].on && r2s[2].focus && r2s[2].tab === 0 && r3s[0].on && !r3s[2].on && !!openedC1 && dlgClosed,
      { afterClick: r1s, afterArrow: r2s, afterHost: r3s, openedC1: !!openedC1, dlgClosed });

    // 儀表板 CLI 區的「CLI 更新後」晶片：顯示目前的政策，選單切換＝送 setting cliRestartPolicy
    await page.click('#viewSwitch [data-view="dashboard"]');
    await sleep(400);
    const chip0 = await page.textContent('#dashboard .cli-policy .chip-label');
    await page.click('#dashboard .cli-policy');
    await page.waitForSelector('.menu.menu-policy');
    const menuItems = await page.$$eval('.menu.menu-policy .menu-item', (n) => n.map((x) => x.querySelector('.mi-label').textContent + (x.classList.contains('checked') ? '✓' : '')));
    await shot(page, 'hot-dashboard-policy-menu-' + tag);
    await page.click('.menu.menu-policy .menu-item:has(.mi-label:text-is("等這一輪結束再切換"))');
    const s3 = await until(() => host.last('setting', (m) => m.key === 'cliRestartPolicy' && m.value === 'AfterTurn' && host.received.indexOf(m) > host.received.indexOf(s2)), 2000);
    await sleep(150);
    const chip1 = await page.textContent('#dashboard .cli-policy .chip-label');
    const checkBtnStill = await page.$$eval('.cli-head .btn', (n) => n.map((b) => b.textContent));
    check('hotDashboardPolicyChip', chip0 === 'CLI 更新後：在安全點立即切換' && same(menuItems, ['在安全點立即切換並自動接續（預設）✓', '等這一輪結束再切換', '下次送出時才用新版'])
      && !!s3 && chip1 === 'CLI 更新後：等這一輪結束再切換' && checkBtnStill.length === 1,
      { chip0, menuItems, setting: s3, chip1, cliHeadButtons: checkBtnStill });
  } else {
    await page.keyboard.press('Escape');
    await sleep(150);
  }

  check('hotTextContrastAA' + sfx, Object.keys(lowContrast).length === 0, lowContrast);
  // 淺色的 app 更新橫幅與設定頁：runLight 只跑第一種寬度（--hot 時不跑），其他寬度在這裡補（設計第 13 節「深淺色與兩種寬度」）
  if (theme === 'light' && (HOT_ONLY || vp.w !== VIEWPORTS[0].w)) await appUpdateLightShots(page, host, vp.w);
  await closeApp(app);
}

/** 2×2 窗格：同時看四種換版狀態（窄的窗格裡狀態列折行、版本藏起來、按鈕不超出窗格） */
async function runHotGrid(vp, theme) {
  const tag = vp.w + '-' + theme;
  const layout = { panes: [{ id: 'p1', tabs: ['c15'], active: 'c15' }, { id: 'p2', tabs: ['c3'], active: 'c3' }, { id: 'p3', tabs: ['c4'], active: 'c4' }, { id: 'p4', tabs: ['c7'], active: 'c7' }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' };
  const app = await openApp('hot-grid-' + tag, { vp, layout, settings: { theme }, colorScheme: theme, hostSetup: hotSetup });
  const { page, host } = app;
  const ids = ['c15', 'c3', 'c4', 'c7'];
  for (const id of ids) await waitConv(page, id);
  await settle(page, 300);
  const groups = [F.RESTART_STATES.slice(0, 4), F.RESTART_STATES.slice(4, 8), F.RESTART_STATES.slice(8)];
  const result = [];
  let ok = true;
  for (let g = 0; g < groups.length; g++) {
    for (let i = 0; i < ids.length; i++) {
      const c = host.conv(ids[i]);
      const s = groups[g][i];
      const r = s ? F.restartHeader(s) : null;
      c.header = Object.assign({}, c.header, { restart: r }, r ? { running: F.RESTART_RUNNING.has(s) } : {});
      await host.send({ type: 'header', conversationId: ids[i], header: c.header });
    }
    await sleep(200);
    for (let i = 0; i < ids.length; i++) {
      const s = groups[g][i];
      const r = s ? F.restartHeader(s) : null;
      const b = await restartBar(page, ids[i]);
      const paneW = await page.evaluate((sel) => Math.round(document.querySelector(sel).closest('.pane').getBoundingClientRect().width), convSel(ids[i]));
      result.push({ state: s || null, conv: ids[i], paneW, shown: b.shown, overflow: b.overflow, buttonsInside: b.buttonsInside, ver: b.ver });
      // 版本箭頭只在寬於 500px 的窗格、而且狀態帶版本時才要看得到；app 更新的狀態（from／to 為 null）在任何寬度都沒有箭頭
      if (s ? !(b.shown && b.state === s && !b.overflow && b.buttonsInside && (paneW > 500 && (r.from || r.to) ? !!b.ver : b.ver === null)) : b.shown) ok = false;
    }
    await shot(page, 'hot-header-grid-' + (g + 1) + '-' + tag);
  }
  check('hotHeaderGrid_' + vp.w + '_' + theme, ok, result);
  await closeApp(app);
}

// ───── 對話工作資料夾的 git 晶片（gitStatus／gitRefresh；Web/gitrules.js）─────
const GIT_BASE = {
  root: 'C:\\repo\\myriagent', branch: 'main', head: '19fb6c2', upstream: 'origin/main', upstreamGone: false, ahead: 0, behind: 0,
  base: null, baseAhead: null, baseBehind: null, changed: 0, staged: 0, unstaged: 0, untracked: 0, conflicts: 0, stashes: 0,
  operation: null, remote: 'origin', remoteNew: false, remoteCheckedAt: NOW - 60000, checkedAt: NOW - 5000,
};
/** git 晶片：看不看得到、色調、名稱（與名稱看不看得到）、徽章、說明、在不在標題列裡、是不是緊接在資料夾晶片後面 */
function gitChipState(sel) {
  const chip = document.querySelector(sel + ' .ch-git');
  if (!chip) return null;
  const vis = (el) => { const cs = getComputedStyle(el); const r = el.getBoundingClientRect(); return cs.display !== 'none' && r.width > 0 && r.height > 0; };
  const r = chip.getBoundingClientRect();
  const head = chip.closest('.ch').getBoundingClientRect();
  return {
    shown: vis(chip), tone: chip.dataset.tone || null, label: chip.querySelector('.chip-label').textContent, labelShown: vis(chip.querySelector('.chip-label')),
    badges: Array.from(chip.querySelectorAll('.git-badge')).map((b) => b.textContent), badgesShown: vis(chip.querySelector('.git-badges')),
    title: chip.title, height: Math.round(r.height),
    // 晶片在標題列的第二行裡、徽章在晶片框裡（不被擠出去）、不蓋到標題列的按鈕
    inside: r.right <= head.right + 0.5 && r.left >= head.left - 0.5 && !!chip.closest('.ch-sub')
      && Array.from(chip.querySelectorAll('.git-badge')).every((b) => { const br = b.getBoundingClientRect(); return br.width === 0 || (br.right <= r.right + 0.5 && br.left >= r.left - 0.5); })
      && Array.from(chip.closest('.ch').querySelectorAll('.ibtn')).every((b) => {
        const br = b.getBoundingClientRect();
        return br.width === 0 || br.bottom <= r.top + 0.5 || br.top >= r.bottom - 0.5 || br.left >= r.right - 0.5 || br.right <= r.left + 0.5;
      }),
    afterFolder: !!chip.previousElementSibling && chip.previousElementSibling.classList.contains('ch-folder'),
  };
}
async function runGit(vp) {
  const app = await openApp('git', { vp, layout: MAIN_LAYOUT });
  const { page, host } = app;
  await waitConv(page, 'c1');
  // 徽章的數字變了會先閃過亂碼再鎖定（fx-probe 的 fxGitBadgeDecode）：等解碼結束（沒有亂碼、也沒有剛鎖定的字）才讀；訊息可能下一個影格才套用
  const chip = async () => {
    await sleep(80);
    await until(() => page.evaluate((s) => !document.querySelector(s + ' .ch-git :is(.mtok-c.scr, .mtok-c.lock)') || null, convSel('c1')), 3000, 20);
    return page.evaluate(gitChipState, convSel('c1'));
  };
  const headShot = async (file) => { await settle(page, 150); await page.locator(convSel('c1') + ' .ch').screenshot({ path: path.join(OUT, file) }); report.screenshots.push(file); };
  const g0 = await chip();
  check('gitHiddenWithoutStatus', !!g0 && !g0.shown, g0);

  await host.send({ type: 'gitStatus', conversationId: 'c1', git: GIT_BASE });
  const clean = await chip();
  check('gitClean', clean.shown && clean.tone === 'clean' && clean.label === 'main' && clean.badges.length === 0 && clean.afterFolder && clean.inside && /工作目錄乾淨，與上游同步/.test(clean.title), clean);
  await headShot('git-header-clean-dark.png');

  const dirty = Object.assign({}, GIT_BASE, { changed: 3, staged: 1, unstaged: 2, untracked: 2, ahead: 2, behind: 1, remoteNew: true, stashes: 1,
    autoPull: { state: 'skipped', text: '這次不拉：這裡有 2 個提交不在 origin/main，不能快轉', at: NOW } });
  await host.send({ type: 'gitStatus', conversationId: 'c1', git: dirty });
  const g1 = await chip();
  check('gitDirtyAheadBehind', g1.shown && g1.tone === 'warn' && g1.badges.join(' ') === '●5 ↑2 ↓1+' && g1.inside && g1.height === clean.height
    && /未提交：5 個檔案/.test(g1.title) && /未推送：2 個提交/.test(g1.title), g1);
  await headShot('git-header-dirty-dark.png');

  // 選單：repo 路徑、明細、重新檢查送 gitRefresh
  await page.click(convSel('c1') + ' .ch-git');
  const menu = await until(() => page.evaluate(() => {
    const m = document.querySelector('.menu.menu-git');
    return m ? { path: m.querySelector('.menu-path-value').textContent, rows: Array.from(m.querySelectorAll('.git-row-k')).map((k) => k.textContent + '：' + k.nextElementSibling.textContent) } : null;
  }), 2000);
  check('gitMenuRows', !!menu && menu.path === GIT_BASE.root && menu.rows.includes('未提交：5 個檔案（已暫存 1、未暫存 2、未追蹤 2）')
    && menu.rows.includes('未推送：2 個提交') && menu.rows.includes('未拉取：1 個提交；遠端還有新的提交沒下載') && menu.rows.includes('stash：1 筆')
    && menu.rows.includes('自動拉取：這次不拉：這裡有 2 個提交不在 origin/main，不能快轉'), menu);
  await shot(page, 'git-menu-dark');
  await page.click('.menu-git .menu-item');
  const refresh = await until(() => host.last('gitRefresh', (m) => m.conversationId === 'c1'), 2000);
  check('gitRefreshSent', !!refresh, refresh);

  // 長期獨立分支：上游已同步，預設分支的歷史差異只在明細，不亮警告。
  const independent = Object.assign({}, GIT_BASE, { branch: 'projects/long-lived', upstream: 'origin/projects/long-lived', base: 'origin/develop-5.26', baseAhead: 1579, baseBehind: 108 });
  await host.send({ type: 'gitStatus', conversationId: 'c1', git: independent });
  const gi = await chip();
  const independentAria = await page.locator(convSel('c1') + ' .ch-git').getAttribute('aria-label');
  check('gitIndependentSynced', gi.tone === 'clean' && gi.badges.length === 0 && gi.label === 'projects/long-lived'
    && !/未合併/.test(gi.title) && /與上游同步/.test(independentAria), { ...gi, aria: independentAria });
  await page.click(convSel('c1') + ' .ch-git');
  const independentMenu = await until(() => page.evaluate(() => {
    const m = document.querySelector('.menu.menu-git');
    return m ? { text: m.textContent, warnings: m.querySelectorAll('.git-row-v[data-tone="warn"]').length } : null;
  }), 2000);
  check('gitIndependentDetails', !!independentMenu && independentMenu.text.includes('與預設分支差異')
    && independentMenu.text.includes('本分支獨有 1579 個提交，預設分支獨有 108 個提交')
    && independentMenu.text.includes('差異不代表需要合併') && !independentMenu.text.includes('未合併') && independentMenu.warnings === 0, independentMenu);
  await shot(page, 'git-menu-independent-dark');
  await page.keyboard.press('Escape');

  // 合併衝突：紅色、操作與衝突排最前面
  await host.send({ type: 'gitStatus', conversationId: 'c1', git: Object.assign({}, dirty, { conflicts: 2, operation: 'merge' }) });
  const g2 = await chip();
  check('gitConflictDanger', g2.tone === 'danger' && g2.badges.slice(0, 2).join(' ') === '合併中 衝突 2', g2);
  await headShot('git-header-conflict-dark.png');

  // 分離的 HEAD：預設分支差異不冒充未推送，實際未提交仍要提示。
  await host.send({ type: 'gitStatus', conversationId: 'c1', git: Object.assign({}, GIT_BASE, { branch: null, upstream: null, base: 'origin/main', baseAhead: 3, baseBehind: 0, changed: 1, unstaged: 1 }) });
  const g3 = await chip();
  check('gitDetached', g3.label === 'HEAD 19fb6c2' && g3.badges.join(' ') === '●1' && /與預設分支差異：origin\/main/.test(g3.title)
    && /無法判定推送／拉取狀態/.test(g3.title), g3);
  await headShot('git-header-detached-dark.png');

  // 窄窗格（≤500px）：名稱藏起來、徽章留著；不超出標題列
  await page.setViewportSize({ width: 760, height: vp.h });
  await settle(page, 300);
  const g4 = await chip();
  check('gitNarrowPane', g4.shown && !g4.labelShown && g4.badgesShown && g4.inside, g4);
  await headShot('git-header-narrow-dark.png');
  await page.setViewportSize({ width: vp.w, height: vp.h });

  // 換了不是 repo 的資料夾：藏起來
  await host.send({ type: 'gitStatus', conversationId: 'c1', git: null });
  const g5 = await chip();
  check('gitHiddenOnNull', !g5.shown, g5);
  await closeApp(app);

  // 徽章字的對比（深淺色；每種徽章都要 4.5:1）
  const all = Object.assign({}, dirty, { conflicts: 1, operation: 'rebase', branch: 'feat', upstream: 'origin/feat', base: 'origin/main', baseAhead: 4, baseBehind: 0 });
  for (const theme of ['dark', 'light']) {
    const t = await openApp('git-' + theme, { vp, layout: MAIN_LAYOUT, ...(theme === 'light' ? { settings: { theme: 'light' }, colorScheme: 'light' } : {}) });
    await waitConv(t.page, 'c1');
    await t.host.send({ type: 'gitStatus', conversationId: 'c1', git: all });
    await settle(t.page, 200);
    const contrasts = {};
    for (const kind of ['op', 'conflict', 'dirty', 'ahead', 'behind']) contrasts[kind] = await t.page.evaluate(contrastOf, `${convSel('c1')} .git-badge[data-kind="${kind}"]`);
    check('gitBadgeContrast_' + theme, Object.values(contrasts).every((c) => c !== null && c >= 4.5), contrasts);
    await t.page.locator(convSel('c1') + ' .ch').screenshot({ path: path.join(OUT, 'git-header-all-' + theme + '.png') });
    report.screenshots.push('git-header-all-' + theme + '.png');
    await closeApp(t);
  }
}

// ───── 側欄點對話：標題列閃爍（ConvView.flashHead、chat.css 的 ch-flash） ─────
const FLASH_LAYOUT = { panes: [{ id: 'p1', tabs: ['c1'], active: 'c1' }, { id: 'p2', tabs: ['c2'], active: 'c2' }, { id: 'p3', tabs: ['c3'], active: 'c3' }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' };

/**
 * 點了之後每個影格取樣標題列的狀態，到標上 ch-flash 之後 1.6 秒（最多 3.5 秒）：閃爍的底色（::before）的 opacity、有沒有 ch-flash、
 * 其他對話有沒有被標。沒有 ch-flash 時偽元素不存在、computed 的 opacity 是預設的 1，記成 0
 */
function flashSamples(id) {
  return new Promise((resolve) => {
    const head = () => document.querySelector(`.conv[data-conv="${id}"] > .ch`);
    const out = [];
    const t0 = performance.now();
    let tOn = null;
    const step = () => {
      const t = performance.now() - t0;
      const el = head();
      if (el) {
        const cs = getComputedStyle(el, '::before');
        const on = el.classList.contains('ch-flash');
        if (on && tOn === null) tOn = t;
        out.push({ t: Math.round(t), at: t0 + t, on, o: on ? Number(cs.opacity) : 0, anim: cs.animationName, bg: cs.backgroundColor,
          others: Array.from(document.querySelectorAll('.ch.ch-flash')).filter((x) => x !== el).length });
      }
      if ((tOn === null || t - tOn < 1600) && t < 3500) requestAnimationFrame(step); else resolve(out);
    };
    step();
  });
}

/** 取樣的摘要：opacity 由下往上越過 0.6 的次數（閃幾下）、最大值、閃完有沒有收掉、亮著的時間、其他標題列有沒有一起被標 */
function flashSummary(s) {
  let rises = 0;
  for (let i = 1; i < s.length; i++) if (s[i - 1].o < 0.6 && s[i].o >= 0.6) rises++;
  const live = s.filter((x) => x.on);
  return { rises, max: s.length ? Math.max(...s.map((x) => x.o)) : null, endOff: !!s.length && !s[s.length - 1].on,
    onMs: live.length ? live[live.length - 1].t - live[0].t : 0, others: s.length ? Math.max(...s.map((x) => x.others)) : null, anim: (live[0] || {}).anim,
    tinted: live.length > 0 && live.every((x) => x.bg !== 'rgba(0, 0, 0, 0)'), staticOn: live.length > 0 && live.every((x) => x.o === 1), samples: s.length };
}

async function runHeadFlash(vp) {
  const app = await openApp('flash', { vp, layout: FLASH_LAYOUT });
  const { page } = app;
  for (const id of ['c1', 'c2', 'c3']) await waitConv(page, id);
  await settle(page, 300);
  const noneAtStart = await page.evaluate(() => document.querySelectorAll('.ch.ch-flash').length === 0);

  // 已開在別的窗格（p2）：切過去、只有那個對話的標題列閃三下，約 1.3 秒後收掉；閃爍最亮時截圖
  const clickAndSample = async (sel, id, shotName) => {
    const sampling = page.evaluate(flashSamples, id);
    await page.click(sel);
    if (shotName) { await sleep(70); await page.screenshot({ path: path.join(OUT, shotName + '.png') }); report.screenshots.push(shotName + '.png'); }
    return flashSummary(await sampling);
  };
  const open = await clickAndSample('.crow[data-key="c:c2"]', 'c2', 'flash-open-pane-dark');
  const focused = await page.evaluate(() => document.querySelector('.pane[data-pane="p2"]').classList.contains('focused'));
  check('headFlashOpenPane', noneAtStart && open.rises === 3 && open.max > 0.95 && open.others === 0 && open.anim === 'ch-flash' && open.tinted && open.endOff
    && open.onMs >= 1100 && open.onMs <= 1450 && focused, { noneAtStart, focused, ...open });

  // 標題照樣在底色上面（底色以 z-index: -1 墊在下面）、標題列高度不變（兩行或擠回一行都一樣）
  const headH = await page.$eval('.conv[data-conv="c2"] > .ch', (n) => Math.round(n.getBoundingClientRect().height));
  await page.click('.crow[data-key="c:c2"]');
  await sleep(90);
  const onTop = await page.evaluate(() => {
    const head = document.querySelector('.conv[data-conv="c2"] > .ch');
    const t = head.querySelector('.ch-title-text').getBoundingClientRect();
    const hit = document.elementFromPoint(t.left + Math.min(20, t.width / 2), t.top + t.height / 2);
    return { flashing: head.classList.contains('ch-flash'), hitTitle: !!hit && !!hit.closest('.ch-title'), height: Math.round(head.getBoundingClientRect().height) };
  });
  check('headFlashUnderTitle', onTop.flashing && onTop.hitTitle && onTop.height === headH, { ...onTop, before: headH });
  await sleep(1500);

  // 還沒開的對話：開在聚焦的窗格，新分頁的標題列一樣閃；鍵盤 Enter 與右鍵選單的「開啟」也閃
  const fresh = await clickAndSample('.crow[data-key="c:c5"]', 'c5');
  check('headFlashNewTab', fresh.rises === 3 && fresh.others === 0 && fresh.endOff, fresh);
  await page.focus('.crow[data-key="c:c1"]');
  const kbSampling = page.evaluate(flashSamples, 'c1');
  await page.keyboard.press('Enter');
  const kb = flashSummary(await kbSampling);
  await page.click('.crow[data-key="c:c3"]', { button: 'right' });
  await page.waitForSelector('.menu .menu-item');
  const menuSampling = page.evaluate(flashSamples, 'c3');
  await page.click('.menu .menu-item:has-text("開啟")');
  const menu = flashSummary(await menuSampling);
  check('headFlashKeyboardAndMenu', kb.rises === 3 && kb.others === 0 && menu.rises === 3 && menu.others === 0, { kb, menu });

  // 連點：從頭再閃（只看第二下之後的取樣：照樣閃滿三下、亮著的時間從第二下算）
  await page.evaluate(() => document.addEventListener('click', () => { window.__probeClickAt = performance.now(); }, true));
  await page.click('.crow[data-key="c:c2"]');
  await sleep(500);
  const againSampling = page.evaluate(flashSamples, 'c2');
  await page.click('.crow[data-key="c:c2"]');
  const againRaw = await againSampling;
  const clickAt = await page.evaluate(() => window.__probeClickAt);
  const again = flashSummary(againRaw.filter((x) => x.at >= clickAt));
  check('headFlashRestarts', again.rises === 3 && again.onMs >= 1100 && again.endOff, again);

  // 點分頁切換不閃（只有側欄清單）
  await page.click('.tab[data-key="c1"]');
  await sleep(100);
  const tabClick = await page.evaluate(() => document.querySelectorAll('.ch.ch-flash').length);
  check('headFlashOnlyFromList', tabClick === 0, { tabClick });
  await closeApp(app);

  // 淺色主題的截圖
  const light = await openApp('flash-light', { vp, layout: FLASH_LAYOUT, settings: { theme: 'light' }, colorScheme: 'light' });
  for (const id of ['c1', 'c2', 'c3']) await waitConv(light.page, id);
  await settle(light.page, 300);
  await light.page.click('.crow[data-key="c:c2"]');
  await sleep(70);
  await light.page.screenshot({ path: path.join(OUT, 'flash-open-pane-light.png') });
  report.screenshots.push('flash-open-pane-light.png');
  await closeApp(light);

  // 系統要求減少動態、設定關掉動態效果：不閃，改成同樣長度的靜態標示
  for (const [name, o] of [['reduced', { reducedMotion: 'reduce' }], ['fxoff', { settings: { visualEffects: false } }]]) {
    const calm = await openApp('flash-' + name, { vp, layout: FLASH_LAYOUT, ...o });
    for (const id of ['c1', 'c2', 'c3']) await waitConv(calm.page, id);
    await settle(calm.page, 300);
    const sampling = calm.page.evaluate(flashSamples, 'c2');
    await calm.page.click('.crow[data-key="c:c2"]');
    const s = flashSummary(await sampling);
    check('headFlashStatic_' + name, s.rises === 1 && s.staticOn && s.anim === 'none' && s.tinted && s.endOff && s.onMs >= 1100 && s.onMs <= 1450, s);
    await closeApp(calm);
  }
}

// ───── 上下文用量提示（composer.js 的 renderContext、contextrules.js；Header.context）─────
/** 一個對話的上下文提示：看不看得到、字與嚴重度、刻度、說明，以及位置（在工具列內、送出鈕左邊、與權限鈕同一條中線）與窗格寬度 */
function contextState(sel) {
  const root = document.querySelector(sel);
  const el = root && root.querySelector('.ctxm');
  if (!el) return null;
  const vis = (x) => { if (!x) return false; const cs = getComputedStyle(x); const r = x.getBoundingClientRect(); return cs.display !== 'none' && cs.visibility !== 'hidden' && r.width > 0 && r.height > 0; };
  const r = el.getBoundingClientRect();
  const send = root.querySelector('.cbtn.send').getBoundingClientRect();
  const bar = root.querySelector('.cbar').getBoundingClientRect();
  const perm = root.querySelector('.cbar .perm').getBoundingClientRect();
  const col = root.getBoundingClientRect();
  const num = el.querySelector('.ctxm-n');
  const arc = el.querySelector('.ctxm-arc');
  const tick = el.querySelector('.ctxm-mark');
  return {
    shown: vis(el), sev: el.dataset.sev || null, label: num ? num.textContent : null, numShown: vis(num),
    tick: !!tick && getComputedStyle(tick).display !== 'none',
    title: el.title, aria: el.getAttribute('aria-label'), color: getComputedStyle(el).color, stroke: getComputedStyle(arc).stroke, dash: arc.style.strokeDasharray,
    leftOfSend: r.right <= send.left + 0.5, insideBar: r.left >= bar.left - 0.5 && r.right <= bar.right + 0.5,
    // 和工具列左邊的權限、模型鈕同一條中線（送出鈕本身比工具列高一點，不拿它比）
    sendInside: send.right <= col.right + 0.5, centered: Math.abs((r.top + r.bottom) / 2 - (perm.top + perm.bottom) / 2) <= 1,
    width: Math.round(r.width), colWidth: Math.round(col.width),
  };
}

async function runContext(vp) {
  const app = await openApp('context', { vp, layout: MAIN_LAYOUT });
  const { page, host } = app;
  await waitConv(page, 'c1');
  await settle(page, 300);
  const sel = convSel('c1');
  const state = () => page.evaluate(contextState, sel);
  const barShot = async (file) => { await settle(page, 450); await page.locator(sel + ' .cbox').screenshot({ path: path.join(OUT, file) }); report.screenshots.push(file); };
  const setContext = async (ctx) => { host.data.c1.header = Object.assign({}, host.data.c1.header, { context: ctx }); host.pushHeader('c1'); await settle(page, 200); };

  // Claude（有自動壓縮門檻）：41%、灰色、圓環上有門檻的刻度；在工具列內、送出鈕左邊、與權限鈕同一條中線
  const ok = await state();
  check('contextShownClaude', !!ok && ok.shown && ok.label === '41%' && ok.sev === 'ok' && ok.tick && ok.leftOfSend && ok.insideBar && ok.centered && ok.dash.replace(/[ ,]+/g, ' ') === '41.2 100'
    && /上下文：412,380／1,000,000 tokens（41%）/.test(ok.title) && /自動壓縮門檻 967,000 tokens，還有 554,620/.test(ok.title) && !/compact/.test(ok.title)
    && ok.aria === '上下文：412,380／1,000,000 tokens（41%）', ok);
  await barShot('context-ok-dark.png');

  // 接近自動壓縮門檻：紅色、說明建議 /compact
  await setContext({ used: 930_000, window: 1_000_000, percent: 93, autoCompactAt: 967_000, severity: 'max' });
  const max = await state();
  check('contextMaxWarns', max.shown && max.label === '93%' && max.sev === 'max' && max.color !== ok.color && max.stroke !== ok.stroke
    && /還有 37,000/.test(max.title) && max.title.includes('可以送 /compact 壓縮較早的內容'), max);
  await barShot('context-max-dark.png');
  // 沒有門檻（Codex、Grok）：沒有刻度、說明不提自動壓縮
  await setContext({ used: 140_000, window: 200_000, percent: 70, autoCompactAt: null, severity: 'warn' });
  const warn = await state();
  check('contextWarnNoTick', warn.shown && warn.label === '70%' && warn.sev === 'warn' && !warn.tick && warn.color !== ok.color && warn.color !== max.color
    && !/自動壓縮門檻/.test(warn.title) && !/compact/.test(warn.title), warn);
  // 不到 1% 寫 <1%
  await setContext({ used: 2_300, window: 1_000_000, percent: 0.2, autoCompactAt: 967_000, severity: 'ok' });
  const tiny = await state();
  check('contextTinyLabel', tiny.shown && tiny.label === '<1%', tiny);

  // 沒有資料（Antigravity、瀏覽器版、副本、app 剛重開）：不顯示
  await setContext(null);
  const none = await state();
  check('contextHiddenWithoutData', !!none && !none.shown, none);
  await closeApp(app);

  // 4 個窗格的窄對話欄（500px 以下）：平常收起（權限、模型、推理強度的字不會因為多了圓環而被擠短）；
  // 接近上限才出現，只留圓環（百分比在說明裡），送出鈕照樣在窗格內
  const grid = await openApp('context-grid', { vp: { w: 1100, h: 700 }, layout: FOUR_PANES });
  await waitConv(grid.page, 'c1');
  await settle(grid.page, 300);
  const labels = () => grid.page.evaluate((s) => Array.from(document.querySelectorAll(s + ' .cbar .perm-label, ' + s + ' .cbar .pick-label'))
    .map((x) => Math.round(x.getBoundingClientRect().width)), sel);
  const narrowOk = await grid.page.evaluate(contextState, sel);
  const okLabels = await labels();
  grid.host.data.c1.header = Object.assign({}, grid.host.data.c1.header, { context: null });
  grid.host.pushHeader('c1');
  await settle(grid.page, 250);
  const noneLabels = await labels();
  grid.host.data.c1.header = Object.assign({}, grid.host.data.c1.header, { context: { used: 188_000, window: 200_000, percent: 94, autoCompactAt: null, severity: 'high' } });
  grid.host.pushHeader('c1');
  await settle(grid.page, 450);
  const narrowHigh = await grid.page.evaluate(contextState, sel);
  check('contextNarrowPane', !!narrowOk && narrowOk.colWidth <= 500 && !narrowOk.shown && okLabels.join() === noneLabels.join()
    && narrowHigh.shown && !narrowHigh.numShown && narrowHigh.width <= 26 && narrowHigh.sendInside && narrowHigh.insideBar && narrowHigh.leftOfSend,
  { narrowOk, narrowHigh, okLabels, noneLabels });
  await grid.page.locator(sel + ' .cbox').screenshot({ path: path.join(OUT, 'context-narrow-dark.png') });
  report.screenshots.push('context-narrow-dark.png');
  await closeApp(grid);

  // 淺色主題：嚴重度的顏色在淺色背景上看得清楚
  const light = await openApp('context-light', { vp, layout: MAIN_LAYOUT, settings: { theme: 'light' }, colorScheme: 'light' });
  await waitConv(light.page, 'c1');
  light.host.data.c1.header = Object.assign({}, light.host.data.c1.header, { context: { used: 930_000, window: 1_000_000, percent: 93, autoCompactAt: 967_000, severity: 'max' } });
  light.host.pushHeader('c1');
  await settle(light.page, 450);
  const contrast = await light.page.evaluate(contrastOf, sel + ' .ctxm-n');
  check('contextLightContrast', contrast !== null && contrast >= 3, contrast);
  await light.page.locator(sel + ' .cbox').screenshot({ path: path.join(OUT, 'context-max-light.png') });
  report.screenshots.push('context-max-light.png');
  await closeApp(light);
}

// ───── 等背景工作回報（Header.background、ConversationSummary.background；liverules.backgroundLine）─────
const BG_LAYOUT = { panes: [{ id: 'p1', tabs: ['c1', 'c4'], active: 'c4' }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' };
// 開始時間（2026-10-25）：最早的 dotnet test 跑了 12 分 34 秒，狀態列後面計時；子代理剛開始
// 時間以情境開始的那一刻往前推（不是探針開始的 NOW：完整執行時到這裡已過了好幾分鐘，等背景工作那一行的計時器照現在算）
const bgWork = (now) => ({ count: 2, startedAt: now - 754_000, tasks: [{ tool: '執行指令', title: 'dotnet test Myriagent.slnx -c Release', startedAt: now - 754_000 },
  { tool: '子代理', title: '檢查文件裡的連結', startedAt: now - 20_000 }] });

/** c4 的對話框、分頁與側欄那一列：等背景工作回報、執行中的樣貌各有沒有出現，以及持續動畫動到的屬性 */
function bgState() {
  const vis = (x) => { if (!x) return false; const cs = getComputedStyle(x); const r = x.getBoundingClientRect(); return cs.display !== 'none' && cs.visibility !== 'hidden' && r.width > 0 && r.height > 0; };
  const root = document.querySelector('.conv[data-conv="c4"]');
  const run = root.querySelector('.runline');
  const ring = root.querySelector('.run-bg');
  const tab = document.querySelector('.tab[data-key="c4"]');
  const row = document.querySelector('.crow[data-key="c:c4"]');
  const cboxAfter = getComputedStyle(root.querySelector('.cbox'), '::after');
  // 這個對話與它的分頁、側欄那一列上的無限循環動畫：動到哪些屬性（只能是 transform／opacity）
  const scope = [root, tab, row].filter(Boolean);
  const loops = document.getAnimations().filter((a) => a.effect && a.effect.getTiming().iterations === Infinity
    && scope.some((s) => s.contains(a.effect.target)))
    .map((a) => ({ name: a.animationName || '', props: [...new Set(a.effect.getKeyframes().flatMap((k) => Object.keys(k)))].filter((p) => !['offset', 'computedOffset', 'easing', 'composite'].includes(p)) }));
  return {
    bgWait: root.classList.contains('bg-wait'), running: root.classList.contains('running'),
    runShown: vis(run), runBg: run.classList.contains('bg'), ringShown: vis(ring), spinShown: vis(root.querySelector('.run-spin')),
    text: root.querySelector('.run-text').textContent, title: run.title, time: root.querySelector('.run-time').textContent, timeShown: vis(root.querySelector('.run-time')),
    ringAnim: ring ? getComputedStyle(ring).animationName : null, ringBorder: ring ? getComputedStyle(ring).borderTopStyle : null,
    vu: getComputedStyle(run, '::before').content, cboxAnim: cboxAfter.animationName,
    tabRing: !!(tab && tab.querySelector('.tab-state .tab-bg')), tabSpin: !!(tab && tab.querySelector('.tab-spin')), tabTitle: tab ? tab.title : null,
    rowRing: !!(row && row.querySelector('.crow-bg')), rowSpin: !!(row && row.querySelector('.crow-spin')),
    loops, mainThreadLoops: loops.filter((l) => l.props.some((p) => p !== 'transform' && p !== 'opacity')),
  };
}

// 執行中換帳戶（使用者 2026-10-24）：選了另一個帳戶先選時機——現在停下並改用，或目前步驟完成後換（預設）；
// 等這一輪結束的在帳戶晶片標出時鐘與要換的帳戶，選單裡可以取消（送目前的帳戶）
const ACCT_LAYOUT = { panes: [{ id: 'p1', tabs: ['c3'], active: 'c3' }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' };

async function runAccountSwitch(vp) {
  const app = await openApp('account-switch', { vp, layout: ACCT_LAYOUT });
  const { page, host } = app;
  const c3 = convSel('c3');
  await waitConv(page, 'c3');
  await settle(page, 300);
  const menuState = () => page.evaluate(() => ({
    heads: [...document.querySelectorAll('.menu .menu-head')].map((e) => e.textContent),
    items: [...document.querySelectorAll('.menu .menu-item')].map((b) => ({
      label: b.querySelector('.mi-label').textContent, desc: (b.querySelector('.mi-desc') || {}).textContent || null, checked: b.getAttribute('aria-checked'), disabled: b.disabled,
    })),
  }));
  const chipState = () => page.evaluate((sel) => {
    const chip = document.querySelector(sel + ' .ch-acct');
    const next = chip.querySelector('.chip-next');
    const r = chip.getBoundingClientRect();
    return { title: chip.title, nextShown: getComputedStyle(next).display !== 'none', nextLabel: next.textContent, width: Math.round(r.width) };
  }, c3);

  await page.click(`${c3} .ch-acct`);
  await page.waitForSelector('.menu');
  const menu = await menuState();
  await shot(page, 'account-running-menu');
  // 2026-10-24 起四種代理之間都能互換：同一種在前，其他照 init 的 providers 各一段
  check('accountRunningMenu', JSON.stringify(menu.heads) === JSON.stringify(['Claude（立即換或等目前步驟完成後換）', 'Codex（會以前文節錄開新對話）',
    'Antigravity（會以前文節錄開新對話）', 'Grok（會以前文節錄開新對話）'])
    && JSON.stringify(menu.items.map((x) => x.label)) === JSON.stringify(['工作用 Max', '個人 Pro', 'Codex Pro', 'Codex 團隊', 'Antigravity（本機）', 'Grok（本機）'])
    && menu.items[1].checked === 'true'
    && menu.items[2].disabled === false
    && menu.items[3].disabled === true, menu);

  // 換到 Codex（2026-10-24 起執行中也能互換）：說明會以前文節錄開新的對話
  await page.click('.menu .menu-item:has(.mi-label:text-is("Codex Pro"))');
  await page.waitForSelector('.dlg-choice');
  const crossMsg = await page.evaluate(() => document.querySelector('.dlg-choice .confirm-msg').textContent);
  await shot(page, 'account-running-choice-cross');
  await page.click('.dlg-choice .dlg-foot button:text-is("取消")');
  await settle(page, 200);
  check('accountRunningCross', crossMsg === '這一輪還在執行。Claude Code 和 Codex 的對話不能直接接續，「Codex Pro」會帶著先前對話的節錄開新的對話；可以等目前步驟完成後換，或現在就停下、由它從中斷處接著做。', crossMsg);
  await page.click(`${c3} .ch-acct`);
  await page.waitForSelector('.menu');

  // 選另一個帳戶：三個按鈕、預設焦點是「目前步驟完成後換」；取消不送任何指令
  await page.click('.menu .menu-item:has(.mi-label:text-is("工作用 Max"))');
  await page.waitForSelector('.dlg-choice');
  await settle(page, 200);
  const dlg = await page.evaluate(() => {
    const d = document.querySelector('.dlg-choice');
    const foot = d.querySelector('.dlg-foot');
    const fr = foot.getBoundingClientRect();
    const btns = [...foot.querySelectorAll('button')];
    return {
      title: d.querySelector('.dlg-title').textContent, body: d.querySelector('.dlg-body').textContent,
      buttons: btns.map((b) => b.textContent), focused: document.activeElement && document.activeElement.textContent,
      inside: btns.every((b) => { const r = b.getBoundingClientRect(); return r.left >= fr.left - 0.5 && r.right <= fr.right + 0.5; }),
    };
  });
  await shot(page, 'account-running-choice');
  const sent0 = host.received.filter((m) => m.cmd === 'setAccount').length;
  await page.click('.dlg-choice .dlg-foot button:text-is("取消")');
  await settle(page, 200);
  const cancelled = host.received.filter((m) => m.cmd === 'setAccount').length === sent0;
  check('accountRunningChoice', dlg.title === '改用「工作用 Max」繼續？' && dlg.body.includes('可以等目前步驟完成後換')
    && JSON.stringify(dlg.buttons) === JSON.stringify(['取消', '現在停下並改用', '目前步驟完成後換']) && dlg.focused === '目前步驟完成後換' && dlg.inside && cancelled, { dlg, cancelled });

  // 目前步驟完成後換：送 when turnEnd，晶片標出時鐘與要換的帳戶
  await page.click(`${c3} .ch-acct`);
  await page.waitForSelector('.menu');
  await page.click('.menu .menu-item:has(.mi-label:text-is("工作用 Max"))');
  await page.waitForSelector('.dlg-choice');
  await page.click('.dlg-choice .dlg-foot button:text-is("目前步驟完成後換")');
  const later = await until(() => host.last('setAccount', (m) => m.conversationId === 'c3' && m.when === 'turnEnd'), 2000);
  await page.waitForFunction((sel) => getComputedStyle(document.querySelector(sel + ' .ch-acct .chip-next')).display !== 'none', c3, { timeout: 2000 }).catch(() => {});
  const chip = await chipState();
  await page.locator(`${c3} .ch-sub`).screenshot({ path: path.join(OUT, 'account-running-pending.png') }).catch(() => {});
  report.screenshots.push('account-running-pending.png');
  check('accountRunningPendingChip', !!later && later.accountId === 'a1' && chip.nextShown && chip.nextLabel === '工作用 Max'
    && chip.title.endsWith('目前步驟完成後改用「工作用 Max」（點一下可以取消）') && chip.width <= 210, { later, chip });

  // 選單裡取消：送目前的帳戶（不帶 when），標記消失
  await page.click(`${c3} .ch-acct`);
  await page.waitForSelector('.menu');
  const pendMenu = await menuState();
  await shot(page, 'account-running-pending-menu');
  const n0 = host.received.filter((m) => m.cmd === 'setAccount').length;
  await page.click('.menu .menu-item:has(.mi-label:text-is("取消換帳戶"))');
  const cancel = await until(() => host.received.filter((m) => m.cmd === 'setAccount')[n0] || null, 2000);
  await page.waitForFunction((sel) => getComputedStyle(document.querySelector(sel + ' .ch-acct .chip-next')).display === 'none', c3, { timeout: 2000 }).catch(() => {});
  const after = await chipState();
  check('accountRunningCancel', pendMenu.items.some((x) => x.label === '工作用 Max' && x.desc === '目前步驟完成後改用「工作用 Max」')
    && pendMenu.items.some((x) => x.label === '取消換帳戶' && x.desc === '目前步驟完成後改用「工作用 Max」')
    && !!cancel && cancel.accountId === 'a2' && !('when' in cancel) && !after.nextShown
    && after.title.endsWith('執行中也可以換帳戶（立即或等目前步驟完成後換）'), { pendMenu, cancel, after });

  // 現在停下並改用：不帶 when（舊版的電腦也收）
  await page.click(`${c3} .ch-acct`);
  await page.waitForSelector('.menu');
  await page.click('.menu .menu-item:has(.mi-label:text-is("工作用 Max"))');
  await page.waitForSelector('.dlg-choice');
  const n1 = host.received.filter((m) => m.cmd === 'setAccount').length;
  await page.click('.dlg-choice .dlg-foot button:text-is("現在停下並改用")');
  const now = await until(() => host.received.filter((m) => m.cmd === 'setAccount')[n1] || null, 2000);
  check('accountRunningNow', !!now && now.accountId === 'a1' && !('when' in now), now);

  // 窄螢幕：三個按鈕換行、不超出對話框
  await page.setViewportSize({ width: 360, height: 720 });
  await settle(page, 300);
  await page.evaluate(() => import('./ui.js').then((ui) => { window.__choice = ui.choiceDialog({ title: '改用「工作用 Max」繼續？', message: '這一輪還在執行。', choices: [{ value: 'now', label: '現在停下並改用' }, { value: 'turnEnd', label: '目前步驟完成後換', primary: true }] }); }));
  await page.waitForSelector('.dlg-choice');
  await settle(page, 200);
  const narrow = await page.evaluate(() => {
    const d = document.querySelector('.dlg-choice');
    const cr = d.getBoundingClientRect();
    return { cardRight: Math.round(cr.right), vw: window.innerWidth, overflow: [...d.querySelectorAll('.dlg-foot button')].some((b) => b.getBoundingClientRect().right > cr.right + 0.5) };
  });
  await shot(page, 'account-running-choice-narrow');
  check('accountChoiceNarrow', narrow.cardRight <= narrow.vw && !narrow.overflow, narrow);
  await closeApp(app);
}

/**
 * 這一輪結束了、背景工作還在跑時換帳戶（2026-10-27：換帳戶會關掉程序、背景工作跟著停）：先問現在換或等背景工作結束（預設），
 * 等的送 when turnEnd、晶片與選單標出、可以取消；現在換的送 when now
 */
async function runAccountSwitchBackground(vp) {
  const setup = (host) => {
    host.conv('c4').header = Object.assign({}, host.conv('c4').header, { running: false, background: bgWork(Date.now()) });
    Object.assign(host.conversations.find((x) => x.id === 'c4'), { background: true });
  };
  const app = await openApp('account-switch-bg', { vp, layout: BG_LAYOUT, hostSetup: setup });
  const { page, host } = app;
  const c4 = convSel('c4');
  await waitConv(page, 'c4');
  await settle(page, 300);
  const current = host.conv('c4').header.accountId;
  const menuState = () => page.evaluate(() => [...document.querySelectorAll('.menu .menu-item')].map((b) => ({
    label: b.querySelector('.mi-label').textContent, desc: (b.querySelector('.mi-desc') || {}).textContent || null, checked: b.getAttribute('aria-checked'), disabled: b.disabled,
  })));
  const chipState = () => page.evaluate((sel) => {
    const chip = document.querySelector(sel + ' .ch-acct');
    const next = chip.querySelector('.chip-next');
    return { title: chip.title, nextShown: getComputedStyle(next).display !== 'none', nextLabel: next.textContent };
  }, c4);
  const sameProvider = host.accounts.filter((a) => a.provider === host.conv('c4').header.provider && a.id !== current && !a.remoteOnly);
  const target = sameProvider[0];
  const pick = async () => {
    await page.click(`${c4} .ch-acct`);
    await page.waitForSelector('.menu');
    await page.click(`.menu .menu-item:has(.mi-label:text-is("${target.label}"))`);
    await page.waitForSelector('.dlg-choice');
    await settle(page, 200);
  };

  await pick();
  const dlg = await page.evaluate(() => {
    const d = document.querySelector('.dlg-choice');
    return {
      title: d.querySelector('.dlg-title').textContent, body: d.querySelector('.dlg-body').textContent,
      buttons: [...d.querySelectorAll('.dlg-foot button')].map((b) => b.textContent), focused: document.activeElement && document.activeElement.textContent,
    };
  });
  await shot(page, 'account-bg-choice');
  check('accountBgChoice', !!target && dlg.title === '改用「' + target.label + '」繼續？'
    && dlg.body.startsWith('還有 2 個背景工作在跑（') && dlg.body.includes('換帳戶會關掉目前的程序，背景工作會跟著停止。')
    && JSON.stringify(dlg.buttons) === JSON.stringify(['取消', '現在換（停掉背景工作）', '背景工作結束後再換']) && dlg.focused === '背景工作結束後再換', { target, dlg });

  // 背景工作結束後再換：送 when turnEnd，晶片標出要換的帳戶，選單有「取消換帳戶」
  await page.click('.dlg-choice .dlg-foot button:text-is("背景工作結束後再換")');
  const later = await until(() => host.last('setAccount', (m) => m.conversationId === 'c4' && m.when === 'turnEnd'), 2000);
  await page.waitForFunction((sel) => getComputedStyle(document.querySelector(sel + ' .ch-acct .chip-next')).display !== 'none', c4, { timeout: 2000 }).catch(() => {});
  const chip = await chipState();
  await page.locator(`${c4} .ch-sub`).screenshot({ path: path.join(OUT, 'account-bg-pending.png') }).catch(() => {});
  report.screenshots.push('account-bg-pending.png');
  check('accountBgDeferredChip', !!later && later.accountId === target.id && chip.nextShown && chip.nextLabel === target.label
    && chip.title.endsWith('背景工作結束後改用「' + target.label + '」（點一下可以取消或現在就換）'), { later, chip });

  await page.click(`${c4} .ch-acct`);
  await page.waitForSelector('.menu');
  const pendMenu = await menuState();
  await shot(page, 'account-bg-pending-menu');
  const n0 = host.received.filter((m) => m.cmd === 'setAccount').length;
  await page.click('.menu .menu-item:has(.mi-label:text-is("取消換帳戶"))');
  const cancel = await until(() => host.received.filter((m) => m.cmd === 'setAccount')[n0] || null, 2000);
  await page.waitForFunction((sel) => getComputedStyle(document.querySelector(sel + ' .ch-acct .chip-next')).display === 'none', c4, { timeout: 2000 }).catch(() => {});
  const after = await chipState();
  check('accountBgCancel', pendMenu.some((x) => x.label === target.label && x.desc === '背景工作結束後改用「' + target.label + '」')
    && pendMenu.some((x) => x.label === '取消換帳戶' && x.desc === '背景工作結束後改用「' + target.label + '」')
    && !!cancel && cancel.accountId === current && !after.nextShown, { pendMenu, cancel, after });

  // 現在換：送 when now（宿主照換、告訴新帳戶被停掉的背景工作）
  await pick();
  const n1 = host.received.filter((m) => m.cmd === 'setAccount').length;
  await page.click('.dlg-choice .dlg-foot button:text-is("現在換（停掉背景工作）")');
  const now = await until(() => host.received.filter((m) => m.cmd === 'setAccount')[n1] || null, 2000);
  check('accountBgNow', !!now && now.accountId === target.id && now.when === 'now', now);
  await closeApp(app);
}

async function runBackgroundWait(vp) {
  const setup = (host) => {
    host.conv('c4').header = Object.assign({}, host.conv('c4').header, { running: false, background: bgWork(Date.now()) });
    Object.assign(host.conversations.find((x) => x.id === 'c4'), { background: true });
    // 帳戶卡的等背景工作回報（2026-10-24）：a1 這台 1 個；a2 只有其他電腦的 2 個（同步的 otherSessions）
    Object.assign(host.accounts.find((a) => a.id === 'a1'), { sessions: { open: 1, running: 0, waiting: 0, background: 1 } });
    Object.assign(host.accounts.find((a) => a.id === 'a2'), { sessions: { open: 0, running: 0, waiting: 0, background: 0 },
      otherSessions: { open: 2, running: 0, waiting: 0, background: 2 } });
  };
  const app = await openApp('bgwait', { vp, layout: BG_LAYOUT, hostSetup: setup });
  const { page, host } = app;
  await waitConv(page, 'c4');
  await settle(page, 400);

  // 代理這一輪結束了、背景工作還在跑：虛線圈（不是轉圈）、說明與滑過的清單、輸入框的角框慢慢呼吸；沒有音量計；分頁與側欄那一列也是虛線圈
  const bg = await page.evaluate(bgState);
  check('bgWaitConversation', bg.bgWait && !bg.running && bg.runShown && bg.runBg && bg.ringShown && !bg.spinShown
    && bg.text === '等 2 個背景工作回報：dotnet test Myriagent.slnx -c Release 等'
    && bg.title.startsWith('代理這一輪已結束，背景工作完成後會自動接著處理') && bg.title.includes('\n・檢查文件裡的連結（已執行不到 1 分）')
    && bg.title.includes('\n・dotnet test Myriagent.slnx -c Release（已執行 12 分）') && bg.timeShown && /^1[23]:\d\d$/.test(bg.time)
    && bg.ringAnim === 'fx-rot' && bg.ringBorder === 'dashed' && bg.cboxAnim === 'fx-breathe' && (bg.vu === 'none' || bg.vu === 'normal'), bg);
  check('bgWaitTabAndRow', bg.tabRing && !bg.tabSpin && bg.tabTitle.includes('\n等背景工作回報') && bg.rowRing && !bg.rowSpin, bg);
  check('bgWaitCompositedOnly', bg.loops.length > 0 && bg.mainThreadLoops.length === 0, { loops: bg.loops });
  await page.locator(convSel('c4')).screenshot({ path: path.join(OUT, 'bgwait-dark.png') });
  report.screenshots.push('bgwait-dark.png');

  // 側欄帳戶卡：等背景工作回報的對話數是虛線圈＋數字（和執行中的轉圈分得出來），滑過的說明寫其中幾個在其他電腦；沒有就不顯示
  const cards = await page.evaluate(() => Object.fromEntries(['a1', 'a2', 'a3'].map((k) => {
    const card = document.querySelector(`#sidebar .macct[data-key="${k}"]`);
    if (!card) return [k, null];
    const bgEl = card.querySelector('.macct-bg');
    const ring = bgEl.querySelector('.bg-ring');
    return [k, { shown: !bgEl.hidden && bgEl.getBoundingClientRect().width > 0, n: bgEl.querySelector('.macct-bg-n').textContent, title: bgEl.title,
      ringBorder: getComputedStyle(ring).borderTopStyle, run: !card.querySelector('.macct-run').hidden }];
  })));
  check('bgWaitAccountCards', cards.a1.shown && cards.a1.n === '1' && cards.a1.title === '1 個對話等背景工作回報' && cards.a1.ringBorder === 'dashed' && !cards.a1.run
    && cards.a2.shown && cards.a2.n === '2' && cards.a2.title === '2 個對話等背景工作回報（都在其他電腦）'
    && (cards.a3 === null || !cards.a3.shown), cards);
  await page.locator('#sidebar .sb-sec-accts').screenshot({ path: path.join(OUT, 'bgwait-accounts-dark.png') });
  report.screenshots.push('bgwait-accounts-dark.png');

  // 背景工作完成、CLI 自己接著開一輪：換回執行中的樣貌（實心轉圈、狀態文字）
  host.data.c4.header = Object.assign({}, host.data.c4.header, { running: true, status: '背景工作完成，代理接著處理…', startedAt: NOW, background: null });
  Object.assign(host.conversations.find((x) => x.id === 'c4'), { running: true, background: false });
  host.pushHeader('c4');
  host.pushConversations();
  await settle(page, 300);
  const run = await page.evaluate(bgState);
  check('bgWaitThenRunning', !run.bgWait && run.running && run.spinShown && !run.ringShown && run.text === '背景工作完成，代理接著處理…'
    && run.tabSpin && !run.tabRing && run.rowSpin && !run.rowRing, run);

  // Codex 的背景指令跑完、代理接著處理：那則提示（noticeKind backgroundTask）用 CLI 換版那種框與自己的圖示（時鐘）
  host.pushItem('c4', { id: 'bgn1', kind: 'Notice', status: 'Done', time: NOW, html: '<p>背景指令已結束：<code>npm test</code>（結束碼 1，執行了 27 秒），代理接著處理。</p>', noticeKind: 'backgroundTask' });
  await settle(page, 300);
  const note = await page.evaluate(() => {
    const n = document.querySelector('.conv[data-conv="c4"] .it[data-id="bgn1"]');
    if (!n) return null;
    const cs = getComputedStyle(n);
    const ic = n.querySelector('.note-ic');
    return { framed: n.classList.contains('bg-task'), border: cs.borderTopStyle, radius: cs.borderTopLeftRadius, icon: ic ? ic.innerHTML : null, text: n.textContent };
  });
  const clockSvg = await page.evaluate(async () => (await import('./icons.js')).icon('clock', 'x').innerHTML);
  check('bgTaskNotice', !!note && note.framed && note.border === 'solid' && note.radius === '8px' && note.icon === clockSvg && note.text.includes('代理接著處理'), { note, clockSvg: clockSvg.slice(0, 60) });

  // 那一輪結束、沒有背景工作了：回到閒置
  host.data.c4.header = Object.assign({}, host.data.c4.header, { running: false, status: '', startedAt: null, background: null });
  Object.assign(host.conversations.find((x) => x.id === 'c4'), { running: false, background: false });
  host.pushHeader('c4');
  host.pushConversations();
  await settle(page, 300);
  const idle = await page.evaluate(bgState);
  check('bgWaitThenIdle', !idle.bgWait && !idle.running && !idle.runShown && !idle.tabRing && !idle.tabSpin && !idle.rowRing && !idle.rowSpin, idle);

  // 代理另開、脫離 CLI 還在跑的程式（header.programs，2026-10-24）：晶片列多一個「背景程式」晶片，不是等背景工作回報（沒有虛線圈）；
  // 選單每個程式一列（命令列、已執行多久、PID），點了送 programStop
  // 已執行多久以網頁的現在算：以這一刻（不是探針開始的 NOW，完整執行時到這裡已過了好幾分鐘）往前推，多留 20 秒讓分鐘數不會在檢查前跳過去
  const progNow = Date.now() - 20000;
  host.data.c4.header = Object.assign({}, host.data.c4.header, { programs: [
    { id: '4321-133000000000000000', pid: 4321, name: 'node.exe', command: 'node server.js --port 5173', startedAt: progNow - 12 * 60000 },
    { id: '4400-133000000000000001', pid: 4400, name: 'python.exe', command: 'python -m http.server 8080', startedAt: progNow - 125 * 60000 },
  ] });
  host.pushHeader('c4');
  await settle(page, 300);
  const progChip = () => page.evaluate((sel) => {
    const c = document.querySelector(sel + ' .ch-prog');
    const r = c.getBoundingClientRect();
    return { shown: !c.hidden && r.width > 0, label: c.querySelector('.chip-label').textContent, title: c.title,
      bgWait: document.querySelector(sel).classList.contains('bg-wait'), icColor: getComputedStyle(c.querySelector('.chip-ic')).color };
  }, convSel('c4'));
  const pc = await progChip();
  check('programsChip', pc.shown && pc.label === '2 個背景程式' && pc.title.startsWith('代理另開、脫離 CLI 還在跑的程式')
    && pc.title.includes('\n・node server.js --port 5173') && !pc.bgWait, pc);
  await page.locator(convSel('c4') + ' .ch-chips').screenshot({ path: path.join(OUT, 'programs-chip-dark.png') });
  report.screenshots.push('programs-chip-dark.png');
  await page.click(convSel('c4') + ' .ch-prog');
  const progMenu = await until(() => page.evaluate(() => {
    const m = document.querySelector('.menu.menu-prog');
    return m ? { note: m.querySelector('.prog-note').textContent, rows: Array.from(m.querySelectorAll('.menu-item')).map((b) => ({
      label: b.querySelector('.mi-label').textContent, desc: (b.querySelector('.mi-desc') || {}).textContent || '', hint: (b.querySelector('.mi-hint') || {}).textContent || '' })) } : null;
  }), 2000);
  check('programsMenu', !!progMenu && progMenu.note.includes('跑完不會通知代理') && progMenu.rows.length === 2
    && progMenu.rows[0].label === 'node server.js --port 5173' && progMenu.rows[0].desc === '已執行 12 分・PID 4321' && progMenu.rows[0].hint === '停止'
    && progMenu.rows[1].desc === '已執行 2 小時 5 分・PID 4400', progMenu);
  await shot(page, 'programs-menu-dark');
  await page.click('.menu-prog .menu-item');
  const stopCmd = await until(() => host.last('programStop', (m) => m.conversationId === 'c4'), 2000);
  check('programStopSent', !!stopCmd && stopCmd.programId === '4321-133000000000000000', stopCmd);

  // 程式自己結束：那則提示（noticeKind backgroundProgram）用同樣的框、終端機圖示；清單沒了晶片就藏起來（先閃一下）
  host.pushItem('c4', { id: 'bgp1', kind: 'Notice', status: 'Done', time: NOW, html: '<p>代理另開的程式「python -m http.server 8080」已結束（結束碼 1，執行了 2 小時 5 分）</p>', noticeKind: 'backgroundProgram' });
  host.data.c4.header = Object.assign({}, host.data.c4.header, { programs: null });
  host.pushHeader('c4');
  await settle(page, 300);
  const progNote = await page.evaluate(() => {
    const n = document.querySelector('.conv[data-conv="c4"] .it[data-id="bgp1"]');
    if (!n) return null;
    const cs = getComputedStyle(n);
    const ic = n.querySelector('.note-ic');
    return { framed: n.classList.contains('bg-program'), border: cs.borderTopStyle, radius: cs.borderTopLeftRadius, icon: ic ? ic.innerHTML : null };
  });
  const termSvg = await page.evaluate(async () => (await import('./icons.js')).icon('terminal', 'x').innerHTML);
  check('programExitNotice', !!progNote && progNote.framed && progNote.border === 'solid' && progNote.radius === '8px' && progNote.icon === termSvg, progNote);
  // 最後一個自己結束時晶片先閃一下琥珀色（conversation.js 的 renderPrograms、fx-probe 的 fxProgramsChip），0.9 秒後才藏
  const progChipGone = await until(async () => (!(await progChip()).shown ? true : null), 1500, 20);
  check('programsChipHiddenWhenNone', !!progChipGone);

  // MCP 伺服器的 elicitation（問卷卡帶 toolName）與 /btw 旁支提問（noticeKind sideQuestion）
  host.pushItem('c4', { id: 'mcpq1', kind: 'Question', status: 'Waiting', time: NOW, html: '', toolName: 'MCP：notion',
    detailHtml: '<p>MCP 伺服器「notion」要你填寫：</p><p>請選擇要連的工作區</p>', answers: {}, answerAsMessage: false, questions: [
      { id: 'workspace', header: '工作區', text: '要連哪個工作區？', multi: false, allowOther: false, secret: false, options: [{ label: '個人' }, { label: '公司' }] },
      { id: 'note', header: '備註', text: '備註（選填）', multi: false, allowOther: true, secret: false, options: [{ label: '不填', description: '不填這一項' }] },
    ] });
  host.pushItem('c4', { id: 'btw1', kind: 'Notice', status: 'Done', time: NOW, noticeKind: 'sideQuestion',
    html: '<blockquote>\n<p>剛才改了哪些檔案？</p>\n</blockquote>\n<p>改了 <code>ClaudeStreamRules.cs</code> 與兩個測試檔。</p>\n' });
  await settle(page, 300);
  const mcpCards = await page.evaluate(() => {
    const q = document.querySelector('.conv[data-conv="c4"] .it[data-id="mcpq1"]');
    const b = document.querySelector('.conv[data-conv="c4"] .it[data-id="btw1"]');
    return {
      qTitle: q && q.querySelector('.card-title').textContent, detailLabel: q && (q.querySelector('.q-plan-sum > span:last-child') || {}).textContent,
      detailOpen: q && q.querySelector('.q-plan').open, blocks: q && q.querySelectorAll('.q-block').length,
      btwFramed: b && b.classList.contains('side-q'), btwBorder: b && getComputedStyle(b).borderTopStyle,
      btwLabel: b && getComputedStyle(b.querySelector('.md'), '::before').content, btwQuote: b && !!b.querySelector('.md > blockquote'),
    };
  });
  check('mcpQuestionCard', mcpCards.qTitle === 'MCP：notion 想問你' && mcpCards.detailLabel === '說明' && mcpCards.detailOpen && mcpCards.blocks === 2, mcpCards);
  check('sideQuestionNotice', mcpCards.btwFramed && mcpCards.btwBorder === 'dashed' && String(mcpCards.btwLabel).includes('旁支提問') && mcpCards.btwQuote, mcpCards);
  await page.locator('.conv[data-conv="c4"] .it[data-id="mcpq1"]').screenshot({ path: path.join(OUT, 'mcp-question-dark.png') });
  report.screenshots.push('mcp-question-dark.png');
  await page.locator('.conv[data-conv="c4"] .it[data-id="btw1"]').screenshot({ path: path.join(OUT, 'side-question-dark.png') });
  report.screenshots.push('side-question-dark.png');
  await closeApp(app);

  // 系統要求減少動態、設定關掉動態效果：照樣是虛線圈與說明，只是不動
  for (const [name, o] of [['reduced', { reducedMotion: 'reduce' }], ['fxoff', { settings: { visualEffects: false } }]]) {
    const calm = await openApp('bgwait-' + name, { vp, layout: BG_LAYOUT, hostSetup: setup, ...o });
    await waitConv(calm.page, 'c4');
    await settle(calm.page, 300);
    const s = await calm.page.evaluate(bgState);
    check('bgWaitStatic_' + name, s.bgWait && s.ringShown && s.ringBorder === 'dashed' && s.text.startsWith('等 2 個背景工作回報') && s.tabRing && s.rowRing
      && s.loops.every((l) => l.name === 'spin'), s);
    await closeApp(calm);
  }

  // 淺色主題的截圖
  const light = await openApp('bgwait-light', { vp, layout: BG_LAYOUT, hostSetup: setup, settings: { theme: 'light' }, colorScheme: 'light' });
  await waitConv(light.page, 'c4');
  await settle(light.page, 400);
  await light.page.locator(convSel('c4')).screenshot({ path: path.join(OUT, 'bgwait-light.png') });
  report.screenshots.push('bgwait-light.png');
  await closeApp(light);
}

// ───── Antigravity 自訂代理選單、Artifact／Mermaid 預覽與匯入外部對話 ─────
async function runAgyAll8(vp) {
  const setup = (host) => {
    const c = host.conv('c4');
    c.header = Object.assign({}, c.header, {
      provider: 'Antigravity',
      isDraft: true,
      sessionStarted: false,
      agent: null,
      agents: [
        { value: 'reviewer', label: 'reviewer', description: '負責程式碼審查與邊界檢查' },
        { value: 'planner', label: 'planner', description: '負責架構拆解與實作計畫' },
      ],
    });
    c.items.push({
      id: 'agy-art-1',
      kind: 'Tool',
      status: 'Done',
      time: NOW - 10000,
      title: '寫入檔案：plan.md',
      detailHtml: '<div class="artifact-preview"><blockquote class="md-alert md-alert-important"><p class="md-alert-title">IMPORTANT</p><p>請先確認介面合約</p></blockquote><pre><code class="language-mermaid">flowchart LR\n  A["前端"] --> B["AgentHub"]\n  B --> C["CLI"]</code></pre></div>',
    });
    c.header.isDraft = false;
  };
  const app = await openApp('agy-all8', { vp, layout: { panes: [{ id: 'p1', tabs: ['c4'], active: 'c4' }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' }, hostSetup: setup });
  const { page, host } = app;
  await waitConv(page, 'c4');
  await settle(page, 350);

  // 1) 自訂代理選單：顯示預設、點擊選單改為 reviewer 送出 setAgent，對話已啟動（sessionStarted=true）後鎖定唯讀
  const agentBtnSel = convSel('c4') + ' .pick[data-field="agent"]';
  const agentVis0 = await visible(page, agentBtnSel);
  const agentText0 = agentVis0 ? await page.textContent(agentBtnSel + ' .pick-label') : null;
  if (agentVis0) {
    await page.click(agentBtnSel);
    await page.waitForSelector('.menu');
    await page.click('.menu .menu-item:has(.mi-label:text-is("reviewer"))');
  }
  const setAgentCmd = await until(() => host.last('setAgent', (m) => m.conversationId === 'c4' && m.agent === 'reviewer'), 1500);
  await sleep(150);
  const agentText1 = agentVis0 ? await page.textContent(agentBtnSel + ' .pick-label') : null;
  host.conv('c4').header.sessionStarted = true;
  host.pushHeader('c4');
  await sleep(150);
  await page.click(agentBtnSel);
  await page.waitForSelector('.menu');
  const lockedItems = await page.$$eval('.menu .menu-item', (ns) => ns.map((n) => ({ label: n.querySelector('.mi-label').textContent, disabled: n.disabled || n.classList.contains('disabled') || n.getAttribute('aria-disabled') === 'true' })));
  await page.keyboard.press('Escape');
  const plannerLocked = lockedItems.some((x) => x.label === 'planner' && x.disabled);
  check('agyAgentPicker', agentVis0 && agentText0 === '預設' && !!setAgentCmd && agentText1 === 'reviewer' && plannerLocked,
    { agentVis0, agentText0, setAgentCmd, agentText1, lockedItems });

  // 2) 工具卡 detailHtml 展開：GitHub Alert 與離線 Mermaid SVG 圖表＋原始碼切換（檢驗真實計算樣式互斥顯示）
  await page.click(convSel('c4') + ' .it[data-id="agy-art-1"] .t-row');
  await sleep(200);
  const artInfo0 = await page.evaluate((sel) => {
    const it = document.querySelector(sel + ' .it[data-id="agy-art-1"]');
    if (!it) return null;
    const badge = it.querySelector('.t-art-badge');
    const alert = it.querySelector('.md-alert.md-alert-important');
    const merm = it.querySelector('.code-mermaid.is-diagram');
    const diag = merm && merm.querySelector('.code-diagram');
    const pre = merm && merm.querySelector('pre');
    const svg = diag && diag.querySelector('svg');
    const r = svg && svg.getBoundingClientRect();
    return {
      badge: badge ? badge.textContent : null,
      hasAlert: !!alert,
      hasMermaid: !!merm,
      diagDisplay: diag ? getComputedStyle(diag).display : null,
      preDisplay: pre ? getComputedStyle(pre).display : null,
      svgW: r ? Math.round(r.width) : 0,
      svgH: r ? Math.round(r.height) : 0,
    };
  }, convSel('c4'));
  await page.click(convSel('c4') + ' .it[data-id="agy-art-1"] .code-mermaid .mm-toggle');
  await sleep(60);
  const artAfterToggle = await page.evaluate((sel) => {
    const merm = document.querySelector(sel + ' .it[data-id="agy-art-1"] .code-mermaid');
    if (!merm) return null;
    const diag = merm.querySelector('.code-diagram');
    const pre = merm.querySelector('pre');
    return {
      isDiagram: merm.classList.contains('is-diagram'),
      diagDisplay: diag ? getComputedStyle(diag).display : null,
      preDisplay: pre ? getComputedStyle(pre).display : null,
    };
  }, convSel('c4'));
  await page.click(convSel('c4') + ' .it[data-id="agy-art-1"] .code-mermaid .mm-toggle');
  await sleep(60);
  const artBackToDiagram = await page.evaluate((sel) => {
    const merm = document.querySelector(sel + ' .it[data-id="agy-art-1"] .code-mermaid');
    return !!merm && merm.classList.contains('is-diagram')
      && getComputedStyle(merm.querySelector('.code-diagram')).display !== 'none'
      && getComputedStyle(merm.querySelector('pre')).display === 'none';
  }, convSel('c4'));
  check(
    'artifactAlertAndMermaid',
    !!artInfo0
      && artInfo0.badge === '文件'
      && artInfo0.hasAlert
      && artInfo0.hasMermaid
      && artInfo0.diagDisplay !== 'none'
      && artInfo0.preDisplay === 'none'
      && artInfo0.svgW > 40
      && artInfo0.svgH > 20
      && !!artAfterToggle
      && artAfterToggle.isDiagram === false
      && artAfterToggle.diagDisplay === 'none'
      && artAfterToggle.preDisplay !== 'none'
      && artBackToDiagram,
    { artInfo0, artAfterToggle, artBackToDiagram },
  );

  // 3) 側欄「匯入本機 CLI 歷史對話」：開啟對話框送出 listExternalConversations、搜尋篩選、點選匯入送出 importExternalConversation
  await page.click('.sb-head button[title="匯入本機 CLI 歷史對話"]');
  const listExtCmd = await until(() => host.last('listExternalConversations'), 1500);
  await page.waitForSelector('.imp-row');
  const rows0 = await page.$$eval('.imp-row .imp-title', (ns) => ns.map((n) => n.textContent));
  await page.fill('.imp-search-input', '狀態機');
  await sleep(100);
  const rowsFiltered = await page.$$eval('.imp-row .imp-title', (ns) => ns.map((n) => n.textContent));
  await page.click('.imp-row');
  const impCmd = await until(() => host.last('importExternalConversation', (m) => m.sourceKey === 'sk-agy-1'), 1500);
  await waitConv(page, 'imp1');
  const impActive = await page.evaluate(() => !!document.querySelector('.tab.active[data-key="imp1"]'));
  check('importExternalDialog', !!listExtCmd && rows0.length === 2 && rowsFiltered.length === 1 && rowsFiltered[0] === '重構狀態機架構' && !!impCmd && impActive,
    { listExt: !!listExtCmd, rows0, rowsFiltered, impCmd, impActive });

  await closeApp(app);
}

// ───── 執行 ─────
const VIEWPORTS =[{ w: 1440, h: 900 }, { w: 1100, h: 700 }].filter((v) => !ONLY || String(v.w) === ONLY);
let exitCode = 1;
try {
  browser = await chromium.launch({ channel: 'chrome', headless: true });
  report.chrome = browser.version();
  for (const vp of VIEWPORTS) {
    const full = vp.w === VIEWPORTS[0].w;
    if (AGY_ONLY) { if (full) await runAgyAll8(vp); continue; }
    if (FIXES_ONLY) { if (full) await runConversationFixes(vp); continue; }
    if (COMPOSER_ONLY) { if (full) await runComposerFixes(vp); continue; }
    if (SHELL_ONLY) { if (full) await runShell(vp); continue; }
    if (TOKENS_ONLY) { if (full) await runTokens(vp); continue; }
    if (DASH_ONLY) { if (full) await runDashGrid(); continue; }
    if (GIT_ONLY) { if (full) await runGit(vp); continue; }
    if (FLASH_ONLY) { if (full) await runHeadFlash(vp); continue; }
    if (CONTEXT_ONLY) { if (full) await runContext(vp); continue; }
    if (BG_ONLY) { if (full) await runBackgroundWait(vp); continue; }
    if (ACCOUNT_ONLY) { if (full) { await runAccountSwitch(vp); await runAccountSwitchBackground(vp); } continue; }
    if (PERF_ONLY) { if (full) await runPerf({ openApp, closeApp, check, NOW }); continue; }
    const searchCtx = { openApp, closeApp, check, until, settle, contrastOf, outFile: (f) => { report.screenshots.push(f); return path.join(OUT, f); } };
    if (SEARCH_ONLY) { if (full) await runSearch(searchCtx, vp); continue; }
    if (HEALTH_ONLY) { if (full) await runHealth(searchCtx, vp); continue; }
    if (USAGESEG_ONLY) { if (full) await runUsageSegments(searchCtx, vp); continue; }
    if (MCP_ONLY) { if (full) await runMcp(searchCtx, vp); continue; }
    if (SCHED_ONLY) { if (full) await runSchedules(searchCtx, vp); continue; }
    if (CODEX_ONLY) { if (full) await runCodex(searchCtx, vp); continue; }
    if (CLAUDEOPT_ONLY) { if (full) await runClaudeOpt(searchCtx, vp); continue; }
    if (FORK_ONLY) { if (full) await runFork(searchCtx, vp); continue; }
    if (REPLICA_RESTORE_ONLY) { if (full) await runReplicaRestore(vp); continue; }
    if (FX_ONLY) { if (full) await runFx(searchCtx, vp); continue; }
    if (SETTINGS_ONLY) { if (full) await runSettings(searchCtx, vp); continue; }
    if (CONTRAST_ONLY) { if (full) await runVisualChecks(); continue; }
    if (LOGIN_ONLY) { if (full) await runLoginExpiry(vp); continue; }
    if (ACCT_NAME_ONLY) { if (full) await runAccountNameMarquee(vp); continue; }
    const launchCtx = Object.assign({ convSel, waitConv, MAIN_LAYOUT, FOUR_PANES }, searchCtx);
    if (LAUNCH_ONLY) { if (full) await runLaunch(launchCtx, vp); continue; }
    if (SENDING_ONLY) { if (full) await runSending(launchCtx, vp); continue; }
    // 電腦狀態面板：第一種寬度跑一次（裡面另外以 1100、700 寬檢查預設收起與抽屜）
    const outFile = (f) => { report.screenshots.push(f); return path.join(OUT, f); };
    if (full && !HOT_ONLY && !SYNC_ONLY) await runMonitor({ openApp, closeApp, check, NOW, shot, outFile }, vp);
    if (MONITOR_ONLY) continue;
    if (!HOT_ONLY && !SYNC_ONLY) {
      await runMain(vp, full);
      await runPopout(vp, full);
      if (full) { await runExtras(vp); await runNoLayout(vp); await runDraftRestore(vp); await runReplicaRestore(vp); await runLight(vp); await runVisualChecks(); }
      if (full) await runDashboard(vp);
      if (full) await runDashGrid();
      if (full) await runTokens(vp);
      if (full) await runLoginExpiry(vp);
      if (full) await runAccountNameMarquee(vp);
      if (full) await runGit(vp);
      if (full) await runHeadFlash(vp);
      if (full) await runSearch(searchCtx, vp);
      if (full) await runHealth(searchCtx, vp);
      if (full) await runUsageSegments(searchCtx, vp);
      if (full) await runMcp(searchCtx, vp);
      if (full) await runSchedules(searchCtx, vp);
      if (full) await runCodex(searchCtx, vp);
      if (full) await runClaudeOpt(searchCtx, vp);
      if (full) await runFork(searchCtx, vp);
      if (full) await runFx(searchCtx, vp);
      if (full) await runSettings(searchCtx, vp);
      if (full) await runContext(vp);
      if (full) await runBackgroundWait(vp);
      if (full) await runAgyAll8(vp);
      if (full) await runAccountSwitch(vp);
      if (full) await runAccountSwitchBackground(vp);
      if (full) await runLaunch(launchCtx, vp);
      if (full) await runSending(launchCtx, vp);
      if (full) await runPerf({ openApp, closeApp, check, NOW });
      if (full) await runConversationFixes(vp);
      if (full) await runComposerFixes(vp);
      if (full) { await runShell(vp); await runThemeBoot(vp); }
    }
    // 〔熱更新〕兩種寬度、深淺色都跑；按鈕與指令的互動只在第一種寬度的深色檢查一次
    if (!SYNC_ONLY) {
      for (const theme of ['dark', 'light']) await runHotUpdate(vp, theme, full && theme === 'dark');
      await runHotGrid(vp, vp.w === 1440 ? 'dark' : 'light');
    }
    // 〔同步〕設定頁「多電腦同步」：第一種寬度跑一次（裡面另外以 1100 寬、深淺色檢查版面與對比）
    if (full && !HOT_ONLY) await runSyncSettings({ openApp, closeApp, check, NOW, shot, contrastOf }, vp);
  }
  if (!HAS_VIDEO) report.checks.videoLoadsMetadata = 'skipped（沒有 ffmpeg）';
  const errs = report.consoleErrors.filter((e) => !/favicon/i.test(e));
  check('noConsoleErrors', errs.length === 0, errs);
  const boolChecks = Object.values(report.checks).filter((v) => typeof v === 'boolean');
  report.allPassed = boolChecks.every(Boolean);
  exitCode = report.allPassed ? 0 : 1;
} catch (e) {
  report.fatal = String(e && e.stack || e);
  exitCode = 2;
} finally {
  // 先寫結果，再關瀏覽器（關閉加時限）
  fs.writeFileSync(path.join(OUT, 'report.json'), JSON.stringify(report, null, 2), 'utf8');
  console.log(JSON.stringify({ outDir: OUT, allPassed: report.allPassed, checks: report.checks, screenshots: report.screenshots.length, consoleErrors: report.consoleErrors.length, fatal: report.fatal || null }, null, 2));
  if (browser) await Promise.race([browser.close().catch(() => {}), sleep(15000)]);
  process.exit(exitCode);
}
