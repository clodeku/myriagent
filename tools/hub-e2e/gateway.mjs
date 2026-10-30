// Myriagent Hub 瀏覽器閘道端到端（WP3.6 計畫步驟 12；契約 docs/web-protocol.md 第六節、與設計的差異見設計 14.6）。
// Playwright 用本機 Chrome（channel:'chrome'，不下載瀏覽器），以真的瀏覽器驗證：
//   /app 302 到 /app/、/app/ 帶 CSP 與 no-store → 嵌入的桌面網頁在伺服器的 CSP 下 0 違規
//   （WP3.5 的 transport-hub.js 在時：匿名開 /app/ 被傳輸層導到登入頁 /，只有預期的 401）
//   → 頁面內登入 → 頁面內 new WebSocket 走 SignalR 的 JSON 協定：握手、ready → init（瀏覽器的 remote、只有 Claude 與 Codex）
//   → setting theme light（帶 ULID 的 commandId 原樣帶回）→ settings 推送、重新載入 /app/ 是淺色（傳輸層連上、收到 init）→ CSP 正向對照
//   → 連線上限：同一個登入第 9 條連線收到 too_many_connections、/app/ 的狀態列與退避、關掉之後 retryNow() 恢復（WP3.5 步驟 8）
//   → 方法呼叫被拒的錯誤字串（不合格的 ClientState）→ Origin 檢查：帶有效 cookie＋錯誤 Origin 的 negotiate 與升級 403、正確 Origin 的升級 101 → 登出後開著的 WebSocket 2 秒內被關、
//   開著 /app/ 的分頁被導到 / → 稽核筆數 → 主控台只有預期的錯誤（次數剛好）→ 正常停止、這個 MyriagentHub 匯入的金鑰檔都已刪除。
// 結果每一步先寫進結果檔（browser.close() 可能不返回，所以關閉加時限）。
// 與 hub-e2e.mjs（含其他 worktree 的兩支腳本）以 %TEMP%\myriagent-tools\hub-e2e.lock 序列化（lock.mjs，見 README）。
//
// 用法（先 dotnet build src/MyriagentHub/MyriagentHub.csproj -c Release、cd tools/hub-e2e && npm install）：
//   node gateway.mjs                                  自己以暫存資料根、高埠啟動 MyriagentHub，跑完以 admin stop 正常結束
//   node gateway.mjs --hub <MyriagentHub.exe> --port <埠> --work <資料夾> --out <結果檔> --headed
import { chromium } from 'playwright-core';
import { spawn, spawnSync } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import https from 'node:https';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { acquireLock, lockFile, LOCK_WAIT_MS, releaseLock } from './lock.mjs';
import { newContext } from './lib/quiet-browser.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '..', '..');
const args = parseArgs(process.argv.slice(2));
const stamp = new Date().toISOString().replace(/[-:]/g, '').replace('T', '-').slice(0, 15);
const work = path.resolve(args.work ?? path.join(os.tmpdir(), 'myriagent-tools', 'gateway', stamp));
const outFile = path.resolve(args.out ?? path.join(work, 'result.json'));
const hubExe = path.resolve(args.hub ?? path.join(repo, 'src', 'MyriagentHub', 'bin', 'Release', 'net10.0', 'win-x64', 'MyriagentHub.exe'));

// SignalR 文字協定的記錄分隔字元（0x1E）
const RS = String.fromCharCode(0x1e);
const OWNER = 'admin';
// 伺服器的 OwnerAccount.DefaultPassword（首次啟動以它建立擁有者）；結果檔與主控台只寫名稱
const DEFAULT_PASSWORD = 'changeme2026';
const SESSION_COOKIE = '__Host-myriagenthub';
const NO_HOST_TEXT = '這個頁面要在 Clode Myriagent 裡開啟';
const EVIL_ORIGIN = 'https://evil.example';

fs.mkdirSync(work, { recursive: true });
const result = { tool: 'gateway', startedAt: new Date().toISOString(), work, hubExe, base: null, steps: [], ok: null };
// MyriagentHub（file 憑證模式）匯入 PFX 時寫在使用者金鑰存放區的金鑰檔：正常結束會刪，被強制結束就變孤兒。
// 存放區是整個使用者共用的：啟動前（A）與 /healthz 200 之後（B）各列一次，B 減 A 才算這個 MyriagentHub 的
const keysDir = path.join(process.env.APPDATA ?? path.join(os.homedir(), 'AppData', 'Roaming'), 'Microsoft', 'Crypto', 'Keys');
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
function save() {
  fs.writeFileSync(outFile, JSON.stringify(result, null, 2));
}
function step(name, ok, summary, data) {
  result.steps.push({ name, ok, at: new Date().toISOString(), summary, ...(data === undefined ? {} : { data }) });
  save(); // 每一步先寫檔
  console.log(`[${ok === null ? '觀察' : ok ? '通過' : '失敗'}] ${name}：${summary}`);
}

let lockHeld = false;
let hubProc = null;
let hubExit = null;
let browser = null;
let dataDir = null;
let base = null;
let keysBefore = null;
let keysStarted = null;
const consoleLog = [];
const httpErrors = [];
// CSP 違規（exposeBinding 送回 Node 端累積：分頁導覽之後也不會遺失）
const cspLog = [];
const pageLabels = new Map();

try {
  // ───── 0. 序列化鎖 ─────
  const lock = await acquireLock('gateway', work);
  if (!lock.ok) throw new Error(`等不到序列化鎖（${lockFile}，${(LOCK_WAIT_MS / 1000).toFixed(0)} 秒）：另一個 hub-e2e／gateway 還在跑，持有者 ${lock.holder ?? '不明'}`);
  lockHeld = true;
  step('取得序列化鎖', true, `${lockFile}（等了 ${lock.waitedMs} ms${lock.staleRemoved ? '，清掉了超過 10 分鐘的舊鎖' : ''}）`, { lockFile, waitedMs: lock.waitedMs, staleRemoved: lock.staleRemoved });

  // ───── 1. 伺服器 ─────
  if (!fs.existsSync(hubExe)) throw new Error(`找不到 ${hubExe}（先 dotnet build src/MyriagentHub/MyriagentHub.csproj -c Release，或用 --hub 指定）`);
  const transportHub = fs.existsSync(path.join(path.dirname(hubExe), 'wwwroot', 'transport-hub.js'));
  const port = args.port ? Number(args.port) : await freePort();
  dataDir = path.join(work, 'hub-data');
  base = `https://localhost:${port}`;
  result.base = base;
  const hubArgs = ['--data', dataDir, '--environment', 'Development', `--Hub:HttpsPort=${port}`, '--Hub:BindAddress=localhost',
    '--Hub:BelowNormalPriority=false', '--Hub:AdminInbox:PollSeconds=1', '--Hub:Backup:Enabled=false', '--Hub:SelfTest:Enabled=false'];
  keysBefore = new Set(listKeys());
  const logFd = fs.openSync(path.join(work, 'hub-console.log'), 'w');
  hubProc = spawn(hubExe, hubArgs, { cwd: path.dirname(hubExe), stdio: ['ignore', logFd, logFd], windowsHide: true });
  hubProc.on('exit', (code) => { hubExit = code; });
  const started = Date.now();
  let health = null;
  while (Date.now() - started < 60000 && hubExit === null) {
    try {
      const r = await request(`${base}/healthz`);
      if (r.status === 200) { health = JSON.parse(r.body); break; }
    } catch { /* 還沒起來 */ }
    await sleep(300);
  }
  if (!health) throw new Error(`MyriagentHub 沒有起來（結束碼 ${hubExit}），看 ${path.join(work, 'hub-console.log')}`);
  keysStarted = new Set(listKeys());
  const importedKeys = [...keysStarted].filter((k) => !keysBefore.has(k));
  step('啟動 MyriagentHub', true, `${base}（${Date.now() - started} ms 後 /healthz 200，build ${health.build}）；啟動期間新增的金鑰檔 ${importedKeys.length} 個（算這個 MyriagentHub 的）；`
    + `wwwroot ${transportHub ? '有' : '沒有'} transport-hub.js`, { pid: hubProc.pid, dataDir, healthz: health, importedKeys, transportHub });

  // ───── 2. /app 302 到 /app/；/app/ 帶 CSP 與 no-store ─────
  const redirect = await request(`${base}/app`);
  const redirectQuery = await request(`${base}/app?mode=popout&conversation=c-1`);
  const appAnonymous = await request(`${base}/app/`);
  const csp = appAnonymous.headers['content-security-policy'] ?? '';
  const injected = appAnonymous.body.includes('/transport-hub.js');
  step('/app 302 到 /app/、/app/ 帶 CSP 與 no-store', redirect.status === 302 && redirect.headers.location === '/app/'
    && redirectQuery.status === 302 && redirectQuery.headers.location === '/app/?mode=popout&conversation=c-1'
    && appAnonymous.status === 200 && /script-src 'self'/.test(csp) && !/script-src[^;]*unsafe-inline/.test(csp)
    && appAnonymous.headers['cache-control'] === 'no-store' && appAnonymous.body.includes('data-theme="dark"') && injected === transportHub,
    `/app ${redirect.status} → ${redirect.headers.location}、帶查詢字串 ${redirectQuery.status} → ${redirectQuery.headers.location}；/app/ ${appAnonymous.status}、`
      + `Cache-Control ${appAnonymous.headers['cache-control']}、CSP 的 script-src 只有 'self'、匿名是深色；傳輸腳本${injected ? '有' : '沒有'}插入（wwwroot ${transportHub ? '有' : '沒有'} transport-hub.js）`,
    { redirect: { status: redirect.status, location: redirect.headers.location }, redirectQuery: { status: redirectQuery.status, location: redirectQuery.headers.location },
      app: { status: appAnonymous.status, cacheControl: appAnonymous.headers['cache-control'], etag: appAnonymous.headers.etag ?? null, csp }, injected });

  // ───── 3. 頁面載入：CSP 0 違規；有 transport-hub.js 時匿名被導到登入頁 / ─────
  browser = await chromium.launch({ channel: 'chrome', headless: !args.headed, args: ['--ignore-certificate-errors'] });
  const context = await newContext(browser, { ignoreHTTPSErrors: true, locale: 'zh-TW' });
  // CSP 違規收集器：CDP 注入（不受頁面的 CSP 影響、比頁面的任何腳本都早）；每筆連同 location.href 經 exposeBinding 送回 Node 端，
  // 導覽之後也不會遺失（F16）。window.__csp 只留給正向對照（同一個頁面內前後比較）
  await context.exposeBinding('__gwCsp', (source, v) => {
    cspLog.push({ page: pageLabels.get(source.page) ?? '?', ...v });
  });
  await context.addInitScript(() => {
    window.__csp = [];
    document.addEventListener('securitypolicyviolation', (e) => {
      const v = { directive: e.violatedDirective, blocked: e.blockedURI, source: e.sourceFile, line: e.lineNumber, href: location.href };
      window.__csp.push(v);
      if (typeof window.__gwCsp === 'function') window.__gwCsp(v);
    });
  });
  const page = await context.newPage();
  watch(page, 'A');
  const response = await page.goto(`${base}/app/`, { waitUntil: 'load' });
  if (transportHub) {
    // 匿名：傳輸層先問 GET /api/v1/auth/me，401 就 location.replace('/')；等登入頁的 auth.js 跑完（__hubProbe.ready）次數才固定（F15）
    const atAuth = await waitFor(() => page.evaluate(() => (location.pathname === '/' && !!document.querySelector('meta[name="myriagenthub-page"][content="auth"]') ? true : null)).catch(() => null), 5000);
    const probeReady = await waitFor(() => page.evaluate(() => (window.__hubProbe && window.__hubProbe.ready === true ? true : null)).catch(() => null), 10000);
    await page.waitForTimeout(300);
    const at = await page.evaluate(() => ({ path: location.pathname, secure: window.isSecureContext, view: window.__hubProbe?.view ?? null }));
    const order = ['/lib/signalr-', '/transport-hub.js', 'src="app.js"'].map((x) => appAnonymous.body.indexOf(x));
    const scriptsOk = order.every((i) => i > 0) && order[0] < order[1] && order[1] < order[2];
    const http = compareHttp('A');
    const violations = cspLog.filter((v) => v.page === 'A');
    const unexpectedConsole = consoleErrors().filter((m) => m.page === 'A' && !expectedConsole(m));
    step('頁面載入：匿名被導到登入頁、只有預期的 401、CSP 0 違規', response.status() === 200 && !!atAuth && !!probeReady && at.path === '/' && at.secure
      && scriptsOk && http.ok && violations.length === 0 && unexpectedConsole.length === 0,
      `/app/ ${response.status()}（腳本依序 /lib/signalr-…、/transport-hub.js、app.js${scriptsOk ? '' : '：順序不對'}）；5 秒內${atAuth ? '' : '沒有'}導到 /（現在 ${at.path}）、`
        + `登入頁${probeReady ? '跑完' : '沒跑完'}；≥400 的回應 ${http.summary}；CSP 違規 ${violations.length} 筆；非預期的主控台錯誤 ${unexpectedConsole.length} 筆`,
      { at, scriptsOk, http, violations, unexpectedConsole });
  } else {
    const appReady = await waitAppStarted(page, transportHub);
    await page.waitForTimeout(300);
    const loaded = await page.evaluate(() => ({
      noHost: document.querySelector('.no-host')?.textContent ?? null,
      theme: document.documentElement.dataset.theme ?? null,
      scripts: [...document.scripts].map((s) => ({ src: s.getAttribute('src'), type: s.getAttribute('type') })),
      violations: window.__csp.slice(),
      secure: window.isSecureContext,
      title: document.title,
    }));
    const scriptsOk = loaded.scripts.length === 1 && loaded.scripts.at(-1)?.src === 'app.js' && loaded.scripts.at(-1)?.type === 'module';
    const loadErrors = consoleErrors();
    const noHostOk = (loaded.noHost ?? '').includes(NO_HOST_TEXT);
    step('頁面載入：CSP 0 違規、主控台 0 錯誤', response.status() === 200 && appReady && loaded.secure && loaded.violations.length === 0 && loadErrors.length === 0
      && httpErrors.length === 0 && scriptsOk && noHostOk,
      `/app/ ${response.status()}、app.js 執行完${appReady ? '' : '（逾時）'}、安全環境 ${loaded.secure}；CSP 違規 ${loaded.violations.length} 筆、主控台錯誤 ${loadErrors.length} 筆、`
        + `≥400 的回應 ${httpErrors.length} 筆；腳本 ${loaded.scripts.map((s) => s.src).join('、')}；畫面顯示「${loaded.noHost ?? '（沒有）'}」`,
      { ...loaded, consoleErrors: loadErrors, httpErrors: httpErrors.slice() });
  }

  // ───── 4. 頁面內登入 ─────
  const login = await pageFetch(page, 'POST', '/api/v1/auth/login', { userName: OWNER, password: DEFAULT_PASSWORD });
  const cookie = (await context.cookies(base)).find((c) => c.name === SESSION_COOKIE);
  step('頁面內登入', login.status === 200 && login.json?.ok === true && cookie?.httpOnly === true && cookie?.sameSite === 'Strict',
    `POST /api/v1/auth/login（預設密碼）${login.status}；cookie ${cookie?.name ?? '（沒有）'} HttpOnly ${cookie?.httpOnly}、SameSite ${cookie?.sameSite}`,
    { status: login.status, body: login.json, cookie: cookie ? { name: cookie.name, httpOnly: cookie.httpOnly, secure: cookie.secure, sameSite: cookie.sameSite } : null });

  // ───── 5. 瀏覽器 hub：頁面內 new WebSocket（cookie 由瀏覽器帶）、握手、ready → init ─────
  const handshake = await openHub(page);
  await invoke(page, '1', 'Cmd', { cmd: { cmd: 'ready', mode: 'main' } });
  const readyDone = await completion(page, '1');
  const init = await waitMsg(page, (m) => m.type === 'init');
  const providerKeys = (init?.providers ?? []).map((p) => p.key);
  const settingKeys = Object.keys(init?.settings ?? {}).sort();
  step('瀏覽器 hub：ready → init', handshake.ok && JSON.stringify(handshake.message) === '{}' && readyDone?.result?.accepted === true && !!init
    && /permessage-deflate/.test(handshake.extensions ?? '') && /server_no_context_takeover/.test(handshake.extensions ?? '')
    && init.remote?.mode === 'browser' && init.remote?.deviceId === null && init.remote?.sync === null && Array.isArray(init.remote?.devices)
    && JSON.stringify(providerKeys) === JSON.stringify(['Claude', 'Codex', 'Antigravity', 'Grok']) && init.providers.every((p) => p.safeMode === false)
    && JSON.stringify(settingKeys) === JSON.stringify(['enterToSend', 'fontSize', 'notifications', 'theme']) && init.mode === 'main'
    && JSON.stringify(init.cli) === '{}' && Array.isArray(init.conversations) && init.conversations.length === 0 && init.layout === null,
    `握手${handshake.ok ? '成功' : '失敗：' + handshake.error}（WebSocket 擴充「${handshake.extensions ?? '—'}」）；ready 的結果 ${JSON.stringify(readyDone?.result ?? null)}；`
      + `init${init ? '' : '沒有收到'}：remote.mode ${init?.remote?.mode}、providers ${providerKeys.join('、')}、settings ${settingKeys.join('、')}`,
    { handshake, ready: readyDone, init });

  // ───── 6. setting theme light（ULID 的 commandId 原樣帶回）→ settings；重新載入 /app/ 是淺色 ─────
  const commandId = await page.evaluate(ulidInPage);
  await invoke(page, '2', 'Cmd', { commandId, cmd: { cmd: 'setting', key: 'theme', value: 'light' } });
  const settingDone = await completion(page, '2');
  const settingsMsg = await waitMsg(page, (m) => m.type === 'settings' && m.settings?.theme === 'light');
  // 另開分頁重新載入（第 5 步的 WebSocket 留到第 8 步）
  const pageB = await context.newPage();
  watch(pageB, 'B');
  const reload = await pageB.goto(`${base}/app/`, { waitUntil: 'load' });
  const appB = await waitAppStarted(pageB, transportHub);
  const themeAfter = await pageB.evaluate(() => document.documentElement.dataset.theme ?? null);
  const ownerCookies = await cookieHeader(context);
  const ownerIndex = await request(`${base}/app/`, { headers: { Cookie: ownerCookies } });
  const anonymousIndex = await request(`${base}/app/`);
  step('setting theme light → settings、重新載入是淺色', /^[0-7][0-9A-HJKMNP-TV-Z]{25}$/.test(commandId) && settingDone?.result?.accepted === true
    && settingDone?.result?.commandId === commandId && settingDone?.result?.error === undefined && !!settingsMsg && reload.status() === 200 && themeAfter === 'light'
    && ownerIndex.body.includes('data-theme="light"') && anonymousIndex.body.includes('data-theme="dark"') && appB,
    `commandId ${commandId}；結果 ${JSON.stringify(settingDone?.result ?? null)}；settings 推送${settingsMsg ? '收到 theme=light' : '沒有收到'}；`
      + `重新載入的分頁 data-theme=${themeAfter}${transportHub ? `、傳輸層${appB ? '已連上並收到 init（狀態列隱藏）' : '沒有連上'}` : ''}；`
      + `同一份 cookie 的 /app/ ${ownerIndex.body.includes('data-theme="light"') ? '淺色' : '不是淺色'}、匿名${anonymousIndex.body.includes('data-theme="dark"') ? '仍是深色' : '不是深色'}`,
    { commandId, result: settingDone, settings: settingsMsg?.settings ?? null, themeAfter, transport: transportHub ? await transportStatus(pageB) : null });

  // CSP 正向對照（第二個分頁）：插入 inline script 被擋，證明收集器收得到違規
  const violationsBeforeInline = await pageB.evaluate(() => window.__csp.slice());
  const inlineRan = await pageB.evaluate(() => {
    const s = document.createElement('script');
    s.textContent = 'window.__inlineRan = true;';
    document.head.append(s);
    return window.__inlineRan === true;
  });
  await pageB.waitForTimeout(300);
  const violationsAfterInline = await pageB.evaluate(() => window.__csp.slice());
  step('CSP 正向對照：插入 inline script 被擋', violationsBeforeInline.length === 0 && inlineRan === false && violationsAfterInline.length === 1
    && /^script-src/.test(violationsAfterInline[0].directive) && violationsAfterInline[0].blocked === 'inline',
    `重新載入的分頁對照前違規 ${violationsBeforeInline.length} 筆；inline script ${inlineRan ? '竟然執行了' : '沒有執行'}，之後違規 ${violationsAfterInline.length} 筆（${violationsAfterInline[0]?.directive ?? '—'}）`,
    { violationsBeforeInline, inlineRan, violationsAfterInline });

  // ───── 連線上限：同一個登入最多 8 條（web-protocol 第六節第 11 點；計畫 WP3.5 步驟 8）─────
  // 此時這個工作階段有 pageA 的 __gw（第 5 步）＋pageB 的傳輸層；pageA 另開原生 WebSocket（__gwExtra，不碰 __gw，第 8 步要用）湊滿 8 條，
  // 第 9 條（原生）要收到帶 too_many_connections 的關閉訊息；有 transport-hub.js 時再開 pageC 的 /app/：狀態列「開了太多分頁」、30～36 秒後才重試，
  // 關掉另開的之後 retryNow() 恢復
  const existing = transportHub ? 2 : 1;
  await page.evaluate((rsCode) => {
    const rs = String.fromCharCode(rsCode);
    window.__gwExtra = [];
    // 開一條、完成握手（收到 {}）才 resolve；每 10 秒 ping（用戶端逾時 30 秒）；type 7（關閉訊息）記在 frames
    window.__gwOpenExtra = () => new Promise((resolve) => {
      const x = { frames: [], handshake: null, closedAt: null, closeCode: null };
      const ws = new WebSocket(`wss://${location.host}/hubs/v1/browser`);
      x.ws = ws;
      window.__gwExtra.push(x);
      const timer = setTimeout(() => resolve({ ok: false, error: '10 秒內沒有握手回應' }), 10000);
      ws.onopen = () => ws.send(JSON.stringify({ protocol: 'json', version: 1 }) + rs);
      ws.onclose = (e) => {
        x.closedAt = Date.now();
        x.closeCode = e.code;
        clearInterval(x.pinger);
        clearTimeout(timer);
        if (x.handshake === null) resolve({ ok: false, error: `握手前就關閉（代碼 ${e.code}）` });
      };
      ws.onmessage = (e) => {
        for (const part of String(e.data).split(rs)) {
          if (!part) continue;
          const m = JSON.parse(part);
          if (x.handshake === null) {
            x.handshake = m;
            clearTimeout(timer);
            resolve({ ok: JSON.stringify(m) === '{}', message: m });
            continue;
          }
          if (m.type !== 6) x.frames.push(m);
        }
      };
      x.pinger = setInterval(() => { if (ws.readyState === 1) ws.send(JSON.stringify({ type: 6 }) + rs); }, 10000);
    });
  }, 0x1e);
  const extras = [];
  for (let i = 0; i < 8 - existing; i++) extras.push(await page.evaluate(() => window.__gwOpenExtra()));
  // 握手之後伺服器才在 OnConnectedAsync 登記：等一下再確認這幾條都還開著
  await page.waitForTimeout(500);
  const extrasOpen = await page.evaluate(() => window.__gwExtra.filter((x) => x.ws.readyState === 1 && x.closedAt === null).length);
  // 第 9 條（原生）：握手成功之後收到關閉訊息（type 7）
  const ninthHandshake = await page.evaluate(() => window.__gwOpenExtra());
  const ninth = await waitFor(() => page.evaluate(() => {
    const x = window.__gwExtra.at(-1);
    return x.closedAt !== null ? { closeCode: x.closeCode, frames: x.frames } : null;
  }), 5000);
  const ninthClose = ninth?.frames.find((m) => m.type === 7) ?? null;
  let pageC = null;
  let tooMany = null;
  let recovered = null;
  if (transportHub) {
    pageC = await context.newPage();
    watch(pageC, 'C');
    await pageC.goto(`${base}/app/`, { waitUntil: 'load' });
    tooMany = await waitFor(() => pageC.evaluate(() => {
      const st = window.myriagentHubTransport && window.myriagentHubTransport.status();
      const bar = document.querySelector('.hub-status');
      const text = bar && !bar.hidden ? bar.querySelector('.hub-status-text').textContent : '';
      return st && st.reason === 'tooManyConnections' && text.includes('開了太多分頁') ? { status: Object.assign({}, st), text } : null;
    }).catch(() => null), 10000);
    // 關掉另開的（第 9 條已經被伺服器關了），等 1 秒讓伺服器移除，再 retryNow()（最多 3 次、間隔 1 秒），5 秒內恢復
    await page.evaluate(() => { for (const x of window.__gwExtra) { clearInterval(x.pinger); if (x.ws.readyState <= 1) x.ws.close(); } });
    await page.waitForTimeout(1000);
    const isLive = () => pageC.evaluate(() => {
      const st = window.myriagentHubTransport && window.myriagentHubTransport.status();
      return document.getElementById('app')?.classList.contains('ready') && st && st.state === 'online' && st.hubInit === true ? Object.assign({}, st) : null;
    }).catch(() => null);
    const t0 = Date.now();
    for (let i = 0; i < 3 && !recovered; i++) {
      await pageC.evaluate(() => window.myriagentHubTransport.retryNow());
      recovered = await waitFor(isLive, 1000);
    }
    if (!recovered) recovered = await waitFor(isLive, Math.max(0, 5000 - (Date.now() - t0)));
    if (recovered) recovered = { ...recovered, ms: Date.now() - t0 };
    await pageC.close();
  } else {
    await page.evaluate(() => { for (const x of window.__gwExtra) { clearInterval(x.pinger); if (x.ws.readyState <= 1) x.ws.close(); } });
  }
  const waitMs = tooMany ? tooMany.status.retryAt - tooMany.status.lastCloseAt : null;
  step('連線上限：同一個登入第 9 條被拒、關掉之後恢復', extras.length === 8 - existing && extras.every((x) => x.ok) && extrasOpen === 8 - existing
    && ninthHandshake.ok && !!ninthClose && /HubException: too_many_connections/.test(ninthClose.error ?? '')
    && (!transportHub || (!!tooMany && waitMs >= 30000 && waitMs <= 36000 && !!recovered)),
    `已有 ${existing} 條，另開 ${extras.length} 條（握手成功 ${extras.filter((x) => x.ok).length}、0.5 秒後還開著 ${extrasOpen}）；`
      + `第 9 條${ninthHandshake.ok ? '握手成功之後' : '握手失敗、'}${ninthClose ? `收到關閉訊息「${ninthClose.error}」` : '沒有收到關閉訊息'}（WebSocket 代碼 ${ninth?.closeCode ?? '—'}）`
      + (transportHub ? `；pageC 的 /app/ ${tooMany ? `狀態列「${tooMany.text}」、${waitMs} ms 後才重試` : '10 秒內沒有顯示「開了太多分頁」'}；`
        + `關掉另開的之後${recovered ? ` ${recovered.ms} ms 恢復（online、收到 init）` : ' 5 秒內沒有恢復'}` : ''),
    { existing, extras, extrasOpen, ninthHandshake, ninth, tooMany, waitMs, recovered });

  // 方法呼叫被拒時 completion 的 error 字串（計畫 WP3.5 D7、R4：傳輸層以「包含 HubException: <代碼>」比對）：
  // 用第 5 步那條連線送不合格的 ClientState，伺服器丟 HubException("invalid")，連線照常（第 8 步要用）
  await invoke(page, '3', 'ClientState', { visible: 'x' });
  const clientStateDone = await completion(page, '3');
  const stillOpen = await page.evaluate(() => window.__gw.ws.readyState === 1 && window.__gw.closedAt === null);
  step('方法呼叫被拒的錯誤字串', !!clientStateDone && /HubException: invalid/.test(clientStateDone.error ?? '') && clientStateDone.result === undefined && stillOpen,
    `不合格的 ClientState：completion 的 error「${clientStateDone?.error ?? '（沒有收到）'}」；連線${stillOpen ? '照常開著' : '被關了'}`,
    { completion: clientStateDone, stillOpen });

  // ───── 7. Origin 檢查：從 Node 帶有效 cookie ─────
  const negotiateUrl = `${base}/hubs/v1/browser/negotiate?negotiateVersion=1`;
  const negotiateEvil = await request(negotiateUrl, { method: 'POST', headers: { Cookie: ownerCookies, Origin: EVIL_ORIGIN, 'Content-Length': '0' } });
  // 要求 stateful reconnect：伺服器允許時才會在回應帶 useStatefulReconnect:true（不帶就一定沒有，檢查不出設定被改開）
  const negotiateOk = await request(`${negotiateUrl}&useStatefulReconnect=true`, { method: 'POST', headers: { Cookie: ownerCookies, Origin: base, 'Content-Length': '0' } });
  const negotiated = parseJson(negotiateOk.body);
  const transports = (negotiated?.availableTransports ?? []).map((t) => t.transport);
  const upgradeEvil = await upgrade(`${base}/hubs/v1/browser`, { Cookie: ownerCookies, Origin: EVIL_ORIGIN });
  const upgradeOk = await upgrade(`${base}/hubs/v1/browser`, { Cookie: ownerCookies, Origin: base });
  step('Origin 檢查：有效 cookie＋錯誤 Origin 403、正確 Origin 101', negotiateEvil.status === 403 && parseJson(negotiateEvil.body)?.error === 'forbidden_origin'
    && negotiateOk.status === 200 && JSON.stringify(transports) === '["WebSockets"]' && negotiated?.useStatefulReconnect !== true
    && upgradeEvil.upgraded === false && upgradeEvil.status === 403 && parseJson(upgradeEvil.body)?.error === 'forbidden_origin'
    && upgradeOk.upgraded === true && upgradeOk.status === 101,
    `negotiate：Origin ${EVIL_ORIGIN} ${negotiateEvil.status} ${negotiateEvil.body}、本站 ${negotiateOk.status}（傳輸 ${transports.join('、') || '—'}、stateful reconnect ${negotiated?.useStatefulReconnect === true ? '開' : '不開'}）；`
      + `WebSocket 升級：Origin ${EVIL_ORIGIN} ${upgradeEvil.status}${upgradeEvil.upgraded ? '（竟然升級了）' : '、沒有升級'}、本站 ${upgradeOk.status}${upgradeOk.upgraded ? '（升級，正向對照）' : '（沒有升級）'}——同一份有效 cookie，擋下的是 Origin`,
    { negotiateEvil: { status: negotiateEvil.status, body: negotiateEvil.body }, negotiateOk: { status: negotiateOk.status, transports, useStatefulReconnect: negotiated?.useStatefulReconnect ?? null },
      upgradeEvil, upgradeOk });

  // ───── 8. 登出：第 5 步開著的 WebSocket 2 秒內被關；舊 cookie 的升級 401 ─────
  const logout = await page.evaluate(async () => {
    // 登出前還開著（不然「關閉」可能是別的原因、而且早就發生了）
    const wasOpen = window.__gw.ws.readyState === 1 && window.__gw.closedAt === null;
    const before = { readyState: window.__gw.ws.readyState, closeCode: window.__gw.closeCode };
    const t0 = Date.now();
    const r = await fetch('/api/v1/auth/logout', { method: 'POST', headers: { 'X-Myriagent-Csrf': '1', 'Content-Type': 'application/json' }, body: '{}', credentials: 'same-origin', cache: 'no-store' });
    return { status: r.status, t0, wasOpen, before };
  });
  const closedAt = await waitFor(() => page.evaluate(() => window.__gw.closedAt), 5000);
  const closeMs = closedAt === null ? null : closedAt - logout.t0;
  const closeInfo = await page.evaluate(() => ({ code: window.__gw.closeCode, open: window.__gw.ws.readyState }));
  const upgradeAfterLogout = await upgrade(`${base}/hubs/v1/browser`, { Cookie: ownerCookies, Origin: base });
  const closeText = !logout.wasOpen
    ? `登出前 WebSocket 已關閉（readyState ${logout.before.readyState}、代碼 ${logout.before.closeCode ?? '—'}）`
    : closeMs === null ? '5 秒內沒有關閉' : `${closeMs} ms 後關閉（代碼 ${closeInfo.code}）`;
  // 開著 /app/ 的分頁（B）：伺服器關掉它的連線 → 傳輸層重連前問 auth/me 得到 401 → 導到 /（之後 auth.js 再問一次）
  let pageBRedirect = null;
  if (transportHub) {
    const t0 = Date.now();
    const atAuth = await waitFor(() => pageB.evaluate(() => (location.pathname === '/' && !!document.querySelector('meta[name="myriagenthub-page"][content="auth"]') ? true : null)).catch(() => null), 5000);
    const ms = atAuth ? Date.now() - t0 : null;
    const ready = await waitFor(() => pageB.evaluate(() => (window.__hubProbe && window.__hubProbe.ready === true ? true : null)).catch(() => null), 10000);
    await pageB.waitForTimeout(300);
    pageBRedirect = { ok: !!atAuth && !!ready, ms, path: await pageB.evaluate(() => location.pathname).catch(() => null) };
  }
  step('登出後開著的 WebSocket 2 秒內被關閉', logout.status === 200 && logout.wasOpen === true && closeMs !== null && closeMs >= 0 && closeMs <= 2000
    && upgradeAfterLogout.upgraded === false && upgradeAfterLogout.status === 401 && (!transportHub || pageBRedirect.ok),
    `POST /api/v1/auth/logout ${logout.status}；WebSocket ${closeText}；`
      + `登出前的 cookie 再升級 ${upgradeAfterLogout.status}${upgradeAfterLogout.upgraded ? '（竟然升級了）' : ''}`
      + (transportHub ? `；開著 /app/ 的分頁${pageBRedirect.ok ? ` ${pageBRedirect.ms} ms 內被導到 /` : `沒有在 5 秒內被導到 /（現在 ${pageBRedirect.path}）`}` : ''),
    { logout: logout.status, wasOpen: logout.wasOpen, before: logout.before, closeMs, close: closeInfo, upgradeAfterLogout, pageBRedirect });

  // ───── 9. 稽核：admin verify-audit、近期驗證內 GET /api/v1/audit，筆數剛好 ─────
  const relogin = await pageFetch(page, 'POST', '/api/v1/auth/login', { userName: OWNER, password: DEFAULT_PASSWORD });
  const v = spawnSync(hubExe, ['admin', 'verify-audit', '--data', dataDir, '--wait', '15'], { encoding: 'utf8', windowsHide: true, timeout: 30000 });
  const verify = { status: v.status, stdout: (v.stdout ?? '').trim() };
  const audit = await pageFetch(page, 'GET', '/api/v1/audit?limit=200');
  const entries = audit.json?.entries ?? [];
  const countOf = (action, outcome) => entries.filter((e) => e.action === action && e.outcome === outcome).length;
  const expectedAudit = [
    ['owner.created', 'ok', 1],
    ['auth.login', 'ok', 2],
    ['auth.login', 'denied', 0],
    ['prefs.set', 'ok', 1],
    ['prefs.set', 'denied', 0],
    ['auth.logout', 'ok', 1],
  ];
  const auditCounts = expectedAudit.map(([a, o, n]) => ({ action: a, outcome: o, expected: n, actual: countOf(a, o) }));
  const prefs = entries.find((e) => e.action === 'prefs.set');
  const prefsOk = prefs?.target === 'theme' && JSON.stringify(prefs?.detail ?? '').includes('light');
  const chain = audit.json?.chain ?? null;
  step('稽核筆數', relogin.status === 200 && verify.status === 0 && audit.status === 200 && auditCounts.every((x) => x.actual === x.expected) && prefsOk && chain?.ok === true,
    `再登入 ${relogin.status}；admin verify-audit 結束碼 ${verify.status}；/api/v1/audit ${audit.status}，${entries.length} 筆：`
      + `${auditCounts.map((x) => `${x.action}${x.outcome === 'ok' ? '' : '（' + x.outcome + '）'}×${x.actual}`).join('、')}；`
      + `prefs.set 的目標 ${prefs?.target ?? '—'}、內容${prefsOk ? '有 light' : '不對'}；雜湊鏈${chain?.ok === true ? '正常' : '不一致或沒驗證'}`,
    { relogin: relogin.status, verify, status: audit.status, counts: auditCounts, chain,
      entries: entries.map((e) => ({ seq: e.seq, actorType: e.actorType, action: e.action, target: e.target, outcome: e.outcome, detail: e.detail })) });

  // ───── 10. 主控台只有預期的錯誤、≥400 的回應次數剛好（全部分頁；CSP 違規只有正向對照那一筆）─────
  const errors = consoleErrors();
  const unexpected = errors.filter((m) => !expectedConsole(m));
  const http = compareHttp(null);
  const inlineViolations = cspLog.filter((v) => v.page === 'B' && /^script-src/.test(v.directive) && v.blocked === 'inline');
  const otherViolations = cspLog.filter((v) => !inlineViolations.includes(v));
  step('主控台只有預期的錯誤', unexpected.length === 0 && http.ok && inlineViolations.length === 1 && otherViolations.length === 0,
    `各分頁的主控台錯誤 ${errors.length} 筆，排除預期的之後 ${unexpected.length} 筆；≥400 的回應 ${http.summary}；`
      + `CSP 違規 ${cspLog.length} 筆（正向對照 ${inlineViolations.length} 筆、其他 ${otherViolations.length} 筆）`,
    { console: errors.map((m) => ({ ...m, expected: expectedConsole(m) })), http, csp: cspLog });
} catch (e) {
  step('例外', false, String(e?.message ?? e), { stack: String(e?.stack ?? e) });
} finally {
  if (browser) {
    const closed = await Promise.race([browser.close().then(() => true, () => false), sleep(5000).then(() => false)]);
    if (!closed) step('關閉瀏覽器', false, 'browser.close() 5 秒內沒有返回');
  }
  if (hubProc) {
    await stopHub();
    await sleep(1000);
    checkKeys();
  }
  if (lockHeld) {
    const released = releaseLock();
    if (!released.ok) step('釋放序列化鎖', false, released.message);
  }
  result.finishedAt = new Date().toISOString();
  result.ok = result.steps.every((s) => s.ok !== false);
  save();
  console.log(result.ok ? `全部通過，結果檔：${outFile}` : `有檢查失敗，結果檔：${outFile}`);
  process.exit(result.ok ? 0 : 1);
}

// ───── 工具 ─────

async function stopHub() {
  if (hubExit !== null) {
    step('停止 MyriagentHub', false, `伺服器提早結束（結束碼 ${hubExit}）`);
    return;
  }
  const started = Date.now();
  const admin = spawnSync(hubExe, ['admin', 'stop', '--data', dataDir, '--wait', '15'], { encoding: 'utf8', windowsHide: true, timeout: 30000 });
  while (hubExit === null && Date.now() - started < 30000) await sleep(200);
  if (hubExit === null) {
    hubProc.kill();
    step('停止 MyriagentHub', false, 'admin stop 後 30 秒沒有結束，已強制結束（file 模式會留下孤兒金鑰檔）', { admin: admin.stdout });
    return;
  }
  step('停止 MyriagentHub', admin.status === 0 && hubExit === 0, `admin stop 結束碼 ${admin.status}，伺服器 ${Date.now() - started} ms 後正常結束（結束碼 ${hubExit}）`);
}

// 這個 MyriagentHub 匯入的（啟動期間新增的）停止後都要不在；其他新出現的只記成觀察（同一個使用者的其他程序，例如別的 worktree 的測試）
function checkKeys() {
  if (!keysBefore || !keysStarted) return;
  const now = listKeys();
  const mine = [...keysStarted].filter((k) => !keysBefore.has(k));
  const left = mine.filter((k) => now.includes(k));
  step('主機副作用（孤兒金鑰檔）', left.length === 0, `這個 MyriagentHub 啟動期間新增的金鑰檔 ${mine.length} 個，停止後還在 ${left.length} 個（${keysDir}）`, { mine, left });
  const others = now.filter((k) => !keysBefore.has(k) && !keysStarted.has(k));
  if (others.length > 0) {
    step('其他程序的金鑰檔', null, `/healthz 之後才出現、現在還在的金鑰檔 ${others.length} 個：不是這個 MyriagentHub 啟動時匯入的，只記錄、不算失敗`, { others });
  }
}

// app.js（module）跑完：沒有 transport-hub.js 時它在工作區放「要在 Clode Myriagent 裡開啟」；
// 有的話（已登入）等 #app.ready、傳輸層 online 而且這條連線收到 init（狀態列隱藏）、沒有 no-host
async function waitAppStarted(target, transportHub) {
  if (transportHub) {
    const ok = await waitFor(() => target.evaluate(() => {
      const st = window.myriagentHubTransport && window.myriagentHubTransport.status();
      const bar = document.querySelector('.hub-status');
      return document.getElementById('app')?.classList.contains('ready') && st && st.state === 'online' && st.hubInit === true
        && !!bar && bar.hidden && !document.querySelector('.no-host') ? true : null;
    }).catch(() => null), 15000);
    return !!ok;
  }
  try {
    await target.waitForSelector('.no-host', { timeout: 15000 });
    return true;
  } catch {
    return false;
  }
}

function transportStatus(target) {
  return target.evaluate(() => (window.myriagentHubTransport ? Object.assign({}, window.myriagentHubTransport.status()) : null)).catch(() => null);
}

// 頁面內的 SignalR 用戶端（JSON 文字協定，不用程式庫）：握手 {"protocol":"json","version":1}＋RS，之後每則訊息以 RS 結尾。
// 伺服器→瀏覽器：type 1 target "Msg"（宿主訊息）、type 3（呼叫的結果）、type 6（ping）、type 7（關閉）
function openHub(target) {
  return target.evaluate((rsCode) => new Promise((resolve) => {
    const rs = String.fromCharCode(rsCode);
    const gw = { msgs: [], completions: {}, frames: [], handshake: null, closedAt: null, closeCode: null, errors: 0 };
    window.__gw = gw;
    const ws = new WebSocket(`wss://${location.host}/hubs/v1/browser`);
    gw.ws = ws;
    const timer = setTimeout(() => resolve({ ok: false, error: '10 秒內沒有握手回應' }), 10000);
    ws.onopen = () => ws.send(JSON.stringify({ protocol: 'json', version: 1 }) + rs);
    ws.onerror = () => { gw.errors++; };
    ws.onclose = (e) => {
      gw.closedAt = Date.now();
      gw.closeCode = e.code;
      clearInterval(gw.pinger);
      clearTimeout(timer);
      if (gw.handshake === null) resolve({ ok: false, error: `握手前就關閉（代碼 ${e.code}）` });
    };
    ws.onmessage = (e) => {
      for (const part of String(e.data).split(rs)) {
        if (!part) continue;
        const m = JSON.parse(part);
        if (gw.handshake === null) {
          gw.handshake = m;
          clearTimeout(timer);
          // extensions：伺服器選定的 WebSocket 擴充（瀏覽器 hub 預設 permessage-deflate＋server_no_context_takeover，丙）
          resolve({ ok: !m.error, message: m, error: m.error ?? null, extensions: ws.extensions });
          continue;
        }
        if (m.type === 3) gw.completions[m.invocationId] = m;
        else if (m.type === 1 && m.target === 'Msg') gw.msgs.push(m.arguments[0]);
        else if (m.type !== 6) gw.frames.push(m);
      }
    };
    gw.invoke = (id, name, arg) => ws.send(JSON.stringify({ type: 1, invocationId: id, target: name, arguments: [arg] }) + rs);
    // 用戶端逾時是 30 秒：每 10 秒送一次 ping
    gw.pinger = setInterval(() => { if (ws.readyState === 1) ws.send(JSON.stringify({ type: 6 }) + rs); }, 10000);
  }), 0x1e);
}

function invoke(target, id, name, arg) {
  return target.evaluate(([i, n, a]) => window.__gw.invoke(i, n, a), [id, name, arg]);
}

async function completion(target, id) {
  return waitFor(() => target.evaluate((i) => window.__gw.completions[i] ?? null, id), 10000);
}

async function waitMsg(target, predicate, timeoutMs = 10000) {
  return waitFor(async () => (await target.evaluate(() => window.__gw.msgs.slice())).find(predicate) ?? null, timeoutMs);
}

// 輪詢到不是 null 為止（逾時回 null）
async function waitFor(read, timeoutMs) {
  const started = Date.now();
  while (true) {
    const value = await read();
    if (value !== null && value !== undefined) return value;
    if (Date.now() - started >= timeoutMs) return null;
    await sleep(50);
  }
}

// 契約（web-protocol 第六節第 5 點）的 commandId：前 10 字是 Date.now() 的 48 位元（大端、每 5 位元一字），後 16 字是 crypto.getRandomValues 的 80 位元
function ulidInPage() {
  const alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
  let time = Date.now();
  let head = '';
  for (let i = 0; i < 10; i++) {
    head = alphabet[time % 32] + head;
    time = Math.floor(time / 32);
  }
  let tail = '';
  let acc = 0;
  let bits = 0;
  for (const b of crypto.getRandomValues(new Uint8Array(10))) {
    acc = (acc << 8) | b;
    bits += 8;
    while (bits >= 5) {
      bits -= 5;
      tail += alphabet[(acc >> bits) & 31];
    }
    acc &= (1 << bits) - 1;
  }
  return head + tail;
}

function watch(target, label) {
  pageLabels.set(target, label);
  target.on('console', (m) => consoleLog.push({ page: label, type: m.type(), text: m.text(), url: m.location()?.url ?? '' }));
  target.on('pageerror', (e) => consoleLog.push({ page: label, type: 'pageerror', text: String(e?.message ?? e), url: '' }));
  target.on('response', (r) => {
    if (r.status() >= 400) httpErrors.push({ page: label, method: r.request().method(), status: r.status(), url: r.url() });
  });
}

function consoleErrors() {
  return consoleLog.filter((m) => m.type === 'error' || m.type === 'pageerror');
}

// 預期的 ≥400 回應（有 transport-hub.js 時；次數要剛好）：瀏覽器把它們記成主控台的「Failed to load resource」。
// 匿名開 /app/（A）與登出後還開著 /app/ 的分頁（B）都是「傳輸層連線前問一次 → 401 → 導到 / → auth.js 的 refresh 再問 auth/me 與 sessions」
function expectedHttp() {
  if (!fs.existsSync(path.join(path.dirname(hubExe), 'wwwroot', 'transport-hub.js'))) return [];
  return [
    { page: 'A', path: '/api/v1/auth/me', status: 401, count: 2, why: '匿名開 /app/：傳輸層連線前一次、導到 / 之後 auth.js 一次' },
    { page: 'A', path: '/api/v1/sessions', status: 401, count: 1, why: '導到 / 之後 auth.js 的 renderAccount' },
    { page: 'B', path: '/api/v1/auth/me', status: 401, count: 2, why: '登出後：傳輸層重連前一次、導到 / 之後 auth.js 一次' },
    { page: 'B', path: '/api/v1/sessions', status: 401, count: 1, why: '導到 / 之後 auth.js 的 renderAccount' },
  ];
}

// ≥400 的回應和預期清單逐項比對次數；page 是 null 時比全部分頁（給了就只比那個分頁的）
function compareHttp(pageLabel) {
  const expected = expectedHttp().filter((x) => !pageLabel || x.page === pageLabel);
  const actual = httpErrors.filter((e) => !pageLabel || e.page === pageLabel);
  const rows = expected.map((x) => ({ ...x, actual: actual.filter((e) => e.page === x.page && e.status === x.status && new URL(e.url).pathname === x.path).length }));
  const unexpected = actual.filter((e) => !expected.some((x) => e.page === x.page && e.status === x.status && new URL(e.url).pathname === x.path));
  const ok = unexpected.length === 0 && rows.every((r) => r.actual === r.count);
  const summary = rows.length === 0 && unexpected.length === 0 ? '0 筆'
    : rows.map((r) => `${r.page} ${r.path} ${r.status}×${r.actual}（預期 ${r.count}）`).concat(unexpected.map((e) => `非預期 ${e.page} ${e.method} ${e.url} ${e.status}`)).join('、');
  return { ok, rows, unexpected, summary };
}

// 預期的主控台錯誤：第二個分頁的 CSP 正向對照（刻意插入的 inline script），與預期清單裡的 401（Failed to load resource）
function expectedConsole(m) {
  if (m.page === 'B' && /Content Security Policy/.test(m.text) && /inline/i.test(m.text)) return 'CSP 正向對照（刻意插入的 inline script）';
  if (/Failed to load resource/.test(m.text) && m.url) {
    let p = null;
    try { p = new URL(m.url).pathname; } catch { p = null; }
    const hit = expectedHttp().find((x) => x.page === m.page && x.path === p && m.text.includes(`status of ${x.status}`));
    if (hit) return hit.why;
  }
  return null;
}

// 頁面內的同源 fetch（帶 cookie；非 GET 加 CSRF 標頭，與 auth.js 相同）
function pageFetch(target, method, url, body) {
  return target.evaluate(async ([m, u, b]) => {
    const headers = m === 'GET' ? {} : { 'X-Myriagent-Csrf': '1', 'Content-Type': 'application/json' };
    const r = await fetch(u, { method: m, headers, body: m === 'GET' ? undefined : JSON.stringify(b ?? {}), credentials: 'same-origin', cache: 'no-store' });
    const text = await r.text();
    let parsed = null;
    try { parsed = text ? JSON.parse(text) : null; } catch { parsed = null; }
    return { status: r.status, json: parsed, text };
  }, [method, url, body ?? null]);
}

async function cookieHeader(context) {
  return (await context.cookies(base)).map((c) => `${c.name}=${c.value}`).join('; ');
}

// Node 端的 HTTPS 請求（自簽憑證：不驗證）
function request(url, { method = 'GET', headers = {}, body } = {}) {
  return new Promise((resolve, reject) => {
    const req = https.request(url, { method, headers, rejectUnauthorized: false, timeout: 15000, agent: false }, (res) => {
      let data = '';
      res.setEncoding('utf8');
      res.on('data', (d) => { data += d; });
      res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, body: data }));
    });
    req.on('error', reject);
    req.on('timeout', () => req.destroy(new Error('逾時')));
    if (body !== undefined) req.write(body);
    req.end();
  });
}

// Node 端的 WebSocket 升級請求：101 → upgrade 事件（立刻關掉 socket）；其他狀態 → response 事件
function upgrade(url, headers) {
  return new Promise((resolve) => {
    const req = https.request(url, {
      method: 'GET',
      headers: { Connection: 'Upgrade', Upgrade: 'websocket', 'Sec-WebSocket-Version': '13', 'Sec-WebSocket-Key': crypto.randomBytes(16).toString('base64'), ...headers },
      rejectUnauthorized: false,
      timeout: 15000,
      agent: false,
    });
    req.on('upgrade', (res, socket) => {
      socket.destroy();
      resolve({ upgraded: true, status: res.statusCode });
    });
    req.on('response', (res) => {
      let data = '';
      res.setEncoding('utf8');
      res.on('data', (d) => { data += d; });
      res.on('end', () => resolve({ upgraded: false, status: res.statusCode, body: data }));
    });
    req.on('error', (e) => resolve({ upgraded: false, status: null, error: String(e?.message ?? e) }));
    req.on('timeout', () => req.destroy(new Error('逾時')));
    req.end();
  });
}

function parseJson(text) {
  try {
    return text ? JSON.parse(text) : null;
  } catch {
    return null;
  }
}

function listKeys() {
  try {
    return fs.readdirSync(keysDir);
  } catch {
    return [];
  }
}

function freePort() {
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.on('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const { port } = server.address();
      server.close(() => resolve(port));
    });
  });
}

function parseArgs(list) {
  const known = new Set(['hub', 'port', 'work', 'out', 'headed']);
  const out = {};
  for (let i = 0; i < list.length; i++) {
    const a = list[i];
    if (!a.startsWith('--')) throw new Error(`看不懂的參數：${a}`);
    const key = a.slice(2);
    if (!known.has(key)) throw new Error(`看不懂的參數：${a}`);
    if (key === 'headed') { out.headed = true; continue; }
    if (i + 1 >= list.length) throw new Error(`--${key} 缺少值`);
    out[key] = list[++i];
  }
  return out;
}
