// Myriagent Hub 瀏覽器端到端（WP2.5，設計 11.3；擁有者登入改成單一帳號＋密碼，設計 14.4）。
// Playwright 用本機 Chrome（channel:'chrome'，不下載瀏覽器），兩個瀏覽器內容（各自一份 cookie）走完整的擁有者流程：
//   登入頁 → 密碼錯誤、帳號錯誤 401 → 以預設密碼登入 → 登出（舊 cookie 也失效）→ 再登入、第二個瀏覽器也登入
//   → 撤銷第二個瀏覽器的工作階段（被撤銷的立即 401）→ 改密碼（目前密碼錯 403、太短 400、成功後其他工作階段立即 401）
//   → 舊密碼不能登入、新密碼可以 → 再驗證 → CSP 0 違規＋插入 inline script 被擋（正向對照）→ 跨站 Origin 的 POST 被拒
//   → 改回預設密碼 → 版本頁的模組載入失敗時登入頁與配對核准頁照常 → 主控台 0 個非預期錯誤 → 未對應路徑（/favicon.ico 等）一律 404、/api 的未對應路徑匿名 401
//   → 稽核紀錄（雜湊鏈、不含密碼）→ 停止 → 主機副作用 → 結果檔與伺服器記錄不含密碼。
// 結果每一步先寫進結果檔（browser.close() 可能不返回，所以關閉加時限）。密碼只在記憶體裡：結果檔與主控台只寫密碼的名稱。
// 與 gateway.mjs（含其他 worktree 的兩支腳本）以 %TEMP%\myriagent-tools\hub-e2e.lock 序列化（lock.mjs，見 README）：取得鎖之後才記下金鑰檔、啟動 MyriagentHub。
//
// 用法（先 dotnet build src/MyriagentHub/MyriagentHub.csproj -c Release、cd tools/hub-e2e && npm install）：
//   node hub-e2e.mjs                                  自己以暫存資料根、高埠啟動 MyriagentHub，跑完以 admin stop 正常結束
//   node hub-e2e.mjs --hub <MyriagentHub.exe> --port <埠> --work <資料夾> --out <結果檔> --headed
//   node hub-e2e.mjs --base https://localhost:<埠>    對已經在執行、擁有者密碼還是預設的伺服器（不負責啟停；跑完把密碼改回預設）
//     --base 過正式站守門（計畫 D18，lib/guard-rules.mjs 的 checkExternalBase）：只收 https＋迴路主機、拒絕任何 example.com；連上之後 /readyz 要 200、
//     environment 是 console、憑證與 /readyz 的 tls 不是 hub.example.com 或公開 CA（擋「ssh -L」轉送到正式服務）。不過就以結束碼 2 結束，不開瀏覽器、不送任何登入
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
import { childEnv } from './lib/child-env.mjs';
import { checkExternalBase } from './lib/guard-rules.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '..', '..');
const args = parseArgs(process.argv.slice(2));
// --base 的正式站守門（D18）第一關：連線之前只看網址
if (args.base !== undefined) {
  const pre = checkExternalBase(args.base, null, null);
  if (!pre.ok) {
    console.error(`${pre.reason}（正式站守門 D18）`);
    process.exit(2);
  }
}
const stamp = new Date().toISOString().replace(/[-:]/g, '').replace('T', '-').slice(0, 15);
const work = path.resolve(args.work ?? path.join(os.tmpdir(), 'myriagent-tools', 'hub-e2e', stamp));
const outFile = path.resolve(args.out ?? path.join(work, 'result.json'));
const hubExe = path.resolve(args.hub ?? path.join(repo, 'src', 'MyriagentHub', 'bin', 'Release', 'net10.0', 'win-x64', 'MyriagentHub.exe'));
const external = Boolean(args.base);

fs.mkdirSync(work, { recursive: true });
const result = { tool: 'hub-e2e', startedAt: new Date().toISOString(), work, hubExe: external ? null : hubExe, base: null, steps: [], ok: null };
// MyriagentHub（file 憑證模式）匯入 PFX 時寫在使用者金鑰存放區的金鑰檔：正常結束會刪，被強制結束就變孤兒。
// 金鑰存放區是整個使用者共用的：只算這個 MyriagentHub 啟動期間新增的（啟動前列一次、/healthz 200 之後再列一次，同 pairing.mjs；WP4.9 步 14），
// 之後才出現的（別的 worktree 的測試、別的工具）只記成觀察
const keysDir = path.join(process.env.APPDATA ?? path.join(os.homedir(), 'AppData', 'Roaming'), 'Microsoft', 'Crypto', 'Keys');
let keysBefore = null; // 啟動 MyriagentHub 之前
let keysStarted = null; // /healthz 200 之後（或啟動失敗時）
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
function save() {
  fs.writeFileSync(outFile, JSON.stringify(result, null, 2));
}
function step(name, ok, summary, data) {
  result.steps.push({ name, ok, at: new Date().toISOString(), summary, ...(data === undefined ? {} : { data }) });
  save(); // 每一步先寫檔
  console.log(`[${ok === null ? '觀察' : ok ? '通過' : '失敗'}] ${name}：${summary}`);
}

// 擁有者帳號（OwnerAccount）：首次啟動以預設密碼建立；第 2 階段 passkey 版的帳號名是 owner（啟動時改名成 admin）
const OWNER = 'admin';
const LEGACY_OWNER = 'owner';
const SESSION_COOKIE = '__Host-myriagenthub';
// 這次用到的密碼（名稱只用來回報）。只用小寫字母、數字與連字號：伺服器的 JSON 不會把它們跳脫，原文搜尋才不會漏
const PASSWORDS = {
  default: { label: '預設密碼', value: 'changeme2026' },
  wrongLogin: { label: '錯誤的登入密碼', value: `e2e-wrong-login-${word(8)}` },
  wrongCurrent: { label: '錯誤的目前密碼', value: `e2e-wrong-current-${word(8)}` },
  tooShort: { label: '太短的新密碼', value: `t${word(6)}` }, // 7 字（下限 8）
  next: { label: '新密碼', value: `e2e-next-${word(10)}` },
  wrongStepUp: { label: '錯誤的再驗證密碼', value: `e2e-wrong-stepup-${word(8)}` },
};
const PW = Object.fromEntries(Object.entries(PASSWORDS).map(([k, v]) => [k, v.value]));

// 登入類端點（/api/v1/auth/login、stepup、password 共用 RateLimitPolicies.Login）照正式設定每分鐘 10 次（固定時間窗）：
// 未登入的以 IP 分區、已登入的以工作階段分區。這裡只記未登入的登入（一次流程 8 次）；再驗證與改密碼都在已登入的工作階段裡，
// 每個工作階段最多 7 次、不吃 IP 的額度。額度不夠時等到「最後一次使用的 61 秒後」——之前用過的一定落在已經結束的時間窗
const LOGIN_PER_MINUTE = 10;
const loginBudget = { used: 0, lastAt: 0, total: 0, waitedMs: 0 };

// 預期的 HTTP 錯誤：瀏覽器會把它們記成主控台的「Failed to load resource」，檢查「主控台 0 錯誤」時排除。
// auth.js 以 /api/v1/auth/me 與 /api/v1/sessions 的 401 判斷要顯示登入畫面（refresh → renderAccount 同時取兩個）；
// 有 count 的是這支腳本刻意送的錯誤，次數也要剛好
const EXPECTED_HTTP_ERRORS = [
  { path: '/api/v1/auth/me', status: 401, why: '未登入時判斷畫面／probe.me()' },
  { path: '/api/v1/sessions', status: 401, why: '未登入時 renderAccount 同時取' },
  { path: '/api/v1/auth/login', status: 401, count: 3, why: '刻意的錯誤密碼、錯誤帳號、改密碼後的舊密碼' },
  { path: '/api/v1/auth/password', status: 403, count: 1, why: '改密碼時目前的密碼錯誤（刻意）' },
  { path: '/api/v1/auth/password', status: 400, count: 1, why: '新密碼太短（刻意）' },
  { path: '/api/v1/auth/stepup', status: 403, count: 1, why: '再驗證的密碼錯誤（刻意）' },
  // /favicon.ico 不在這裡：auth.html 以 <link rel="icon" href="/app/favicon.ico"> 明確指定圖示，瀏覽器不會去要它，伺服器對未對應路徑也一律 404
];

let lockHeld = false;
let hubProc = null;
let hubExit = null;
let browser = null;
let dataDir = null;
let base = null;
const consoleLog = [];
const httpErrors = [];

// --base 的正式站守門（D18）第二關：取鎖、開瀏覽器、送任何登入之前讀 /readyz 與連線看到的憑證
if (external) {
  const readyzUrl = new URL('/readyz', args.base).href;
  const ready = await request(readyzUrl).catch((e) => ({ status: null, body: '', cert: null, error: String(e?.message ?? e) }));
  const json = parseJson(ready.body);
  const guard = checkExternalBase(args.base, { status: ready.status, json }, ready.cert);
  step('正式站守門（--base 的 /readyz 與憑證，D18）', guard.ok,
    guard.ok
      ? `${readyzUrl} ${ready.status}、environment ${json?.environment}、憑證 ${ready.cert?.subject?.CN ?? '—'}（簽發者 ${ready.cert?.issuer?.CN ?? '—'}）：不是正式站`
      : `${guard.reason}${ready.error ? `（${ready.error}）` : ''}：不開瀏覽器、不送任何登入`,
    { status: ready.status, environment: json?.environment ?? null, subject: ready.cert?.subject ?? null, issuer: ready.cert?.issuer ?? null });
  if (!guard.ok) {
    result.finishedAt = new Date().toISOString();
    result.ok = false;
    save();
    process.exit(2);
  }
}

try {
  // ───── 序列化鎖 ─────
  const lock = await acquireLock('hub-e2e', work);
  if (!lock.ok) throw new Error(`等不到序列化鎖（${lockFile}，${(LOCK_WAIT_MS / 1000).toFixed(0)} 秒）：另一個 hub-e2e／gateway 還在跑，持有者 ${lock.holder ?? '不明'}`);
  lockHeld = true;
  step('取得序列化鎖', true, `${lockFile}（等了 ${lock.waitedMs} ms${lock.staleRemoved ? '，清掉了超過 10 分鐘的舊鎖' : ''}）`, { lockFile, waitedMs: lock.waitedMs, staleRemoved: lock.staleRemoved });

  // ───── 伺服器 ─────
  if (external) {
    base = args.base.replace(/\/+$/, '');
  } else {
    if (!fs.existsSync(hubExe)) throw new Error(`找不到 ${hubExe}（先 dotnet build src/MyriagentHub/MyriagentHub.csproj -c Release，或用 --hub 指定）`);
    const port = args.port ? Number(args.port) : await freePort();
    dataDir = path.join(work, 'hub-data');
    base = `https://localhost:${port}`;
    const hubArgs = ['--data', dataDir, '--environment', 'Development', `--Hub:HttpsPort=${port}`, '--Hub:BindAddress=localhost',
      '--Hub:BelowNormalPriority=false', '--Hub:AdminInbox:PollSeconds=1', '--Hub:Backup:Enabled=false', '--Hub:SelfTest:Enabled=false'];
    const logFd = fs.openSync(path.join(work, 'hub-console.log'), 'w');
    keysBefore = new Set(listKeys());
    // 環境變數剝掉代理與同步相關的、ASP.NET Core 環境與 GC 設定（lib/child-env.mjs，同 pairing.mjs；WP4.9 步 14）
    hubProc = spawn(hubExe, hubArgs, { cwd: path.dirname(hubExe), env: childEnv(), stdio: ['ignore', logFd, logFd], windowsHide: true });
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
    // 憑證在 /healthz 200 之前就匯入了：沒起來也先記下，收尾時照樣檢查
    keysStarted = new Set(listKeys());
    if (!health) throw new Error(`MyriagentHub 沒有起來（結束碼 ${hubExit}），看 ${path.join(work, 'hub-console.log')}`);
    const importedKeys = [...keysStarted].filter((k) => !keysBefore.has(k));
    step('啟動 MyriagentHub', true, `${base}（${Date.now() - started} ms 後 /healthz 200，build ${health.build}）；啟動期間新增的金鑰檔 ${importedKeys.length} 個（算這個 MyriagentHub 的）`,
      { pid: hubProc.pid, dataDir, healthz: health, importedKeys });
  }
  result.base = base;

  browser = await chromium.launch({ channel: 'chrome', headless: !args.headed, args: ['--ignore-certificate-errors'] });
  const contextA = await newContext(browser, { ignoreHTTPSErrors: true, locale: 'zh-TW' });
  const pageA = await contextA.newPage();
  watch(pageA, 'A');

  // ───── 1. 載入登入頁 ─────
  const response = await pageA.goto(`${base}/`);
  await waitReady(pageA);
  const loaded = await pageA.evaluate(() => ({
    view: window.__hubProbe.view,
    marker: document.querySelector('meta[name="myriagenthub-page"]')?.content ?? null,
    isSecureContext: window.isSecureContext,
    loginForm: {
      user: document.getElementById('login-user')?.autocomplete ?? null,
      passwordType: document.getElementById('login-password')?.type ?? null,
      password: document.getElementById('login-password')?.autocomplete ?? null,
    },
    status: document.getElementById('status')?.textContent ?? null,
    userAgent: navigator.userAgent,
  }));
  const meAnonymous = await probe(pageA, 'me');
  const csp = response.headers()['content-security-policy'] ?? '';
  step('載入登入頁', loaded.view === 'login' && loaded.marker === 'auth' && loaded.isSecureContext && loaded.loginForm.passwordType === 'password'
    && meAnonymous.status === 401 && /script-src 'self'/.test(csp) && !/script-src[^;]*unsafe-inline/.test(csp),
    `畫面 ${loaded.view}、固定標記 ${loaded.marker}、安全環境 ${loaded.isSecureContext}、密碼欄 type=${loaded.loginForm.passwordType}；`
      + `未登入 /api/v1/auth/me ${meAnonymous.status}；CSP 的 script-src 只有 'self'`,
    { ...loaded, me: meAnonymous, status: response.status(), csp });

  // ───── 2. 密碼錯誤、帳號錯誤 401 ─────
  await reserveLogin(2, '錯誤的密碼與帳號');
  const wrongPassword = await loginOn(pageA, OWNER, PW.wrongLogin);
  const wrongUser = await loginOn(pageA, LEGACY_OWNER, PW.default);
  const viewAfterWrong = await view(pageA);
  const meAfterWrong = await probe(pageA, 'me');
  step('密碼錯誤、帳號錯誤都是 401', wrongPassword.status === 401 && wrongPassword.code === 'login_failed' && wrongUser.status === 401 && wrongUser.code === 'login_failed'
    && viewAfterWrong === 'login' && meAfterWrong.status === 401,
    `admin＋錯誤密碼 ${wrongPassword.status} ${wrongPassword.code}；舊帳號名 owner＋預設密碼 ${wrongUser.status} ${wrongUser.code}；仍在 ${viewAfterWrong} 畫面、/api/v1/auth/me ${meAfterWrong.status}`,
    { wrongPassword, wrongUser, view: viewAfterWrong, me: meAfterWrong });

  // ───── 3. 以預設密碼登入 ─────
  await reserveLogin(1, '以預設密碼登入');
  const login = await loginOn(pageA, OWNER, PW.default);
  const afterLogin = await view(pageA);
  const meA = await probe(pageA, 'me');
  const factUser = await pageA.evaluate(() => document.getElementById('fact-user')?.textContent ?? null);
  const cookieA = (await contextA.cookies(base)).find((c) => c.name === SESSION_COOKIE);
  const persistentDays = cookieA && cookieA.expires > 0 ? (cookieA.expires * 1000 - Date.now()) / 86400000 : null;
  // 這次之前就在的工作階段（--base 的伺服器上別的瀏覽器或中途失敗的上一次留下的）：之後的工作階段數都扣掉它們；自己啟動的伺服器一定是 0 個
  const sessionsAtLogin = await listSessions(pageA);
  const preexisting = sessionsAtLogin.filter((s) => !s.current).map((s) => s.id);
  step('以預設密碼登入', login.ok === true && afterLogin === 'account' && meA.name === OWNER && meA.session?.recentAuth === true && factUser === OWNER
    && cookieA?.httpOnly === true && cookieA?.secure === true && cookieA?.sameSite === 'Strict' && cookieA?.path === '/' && persistentDays > 6.9 && persistentDays < 7.01
    && sessionsAtLogin.filter((s) => s.current).length === 1 && (external || preexisting.length === 0),
    `登入${login.ok ? '成功' : '失敗：' + login.message}、畫面 ${afterLogin}、me().name ${meA.name}、近期驗證 ${meA.session?.recentAuth}、頁面顯示帳號 ${factUser}；`
      + `cookie ${cookieA?.name ?? '（沒有）'} HttpOnly ${cookieA?.httpOnly}、Secure ${cookieA?.secure}、SameSite ${cookieA?.sameSite}、${persistentDays?.toFixed(2) ?? '—'} 天後到期；`
      + `之前就在的工作階段 ${preexisting.length} 個`,
    { login, view: afterLogin, me: meA, factUser, cookie: cookieSummary(cookieA, persistentDays), sessions: sessionsAtLogin.map(sessionSummary) });
  const ours = (list) => list.filter((s) => !preexisting.includes(s.id));

  // ───── 4. 登出（伺服器端工作階段刪掉，舊 cookie 也失效）─────
  const cookieBeforeLogout = await cookieHeader(contextA);
  const oldCookieBefore = await request(`${base}/api/v1/auth/me`, { headers: { Cookie: cookieBeforeLogout } });
  const logout = await probe(pageA, 'logout');
  const afterLogout = await view(pageA);
  const meLoggedOut = await probe(pageA, 'me');
  const oldCookieAfter = await request(`${base}/api/v1/auth/me`, { headers: { Cookie: cookieBeforeLogout } });
  const cookieGone = !(await contextA.cookies(base)).some((c) => c.name === SESSION_COOKIE);
  step('登出（舊 cookie 也失效）', oldCookieBefore.status === 200 && logout.ok === true && afterLogout === 'login' && meLoggedOut.status === 401
    && oldCookieAfter.status === 401 && cookieGone,
    `登出前用同一份 cookie 從頁面外查 /api/v1/auth/me ${oldCookieBefore.status}；登出後畫面 ${afterLogout}、/api/v1/auth/me ${meLoggedOut.status}、`
      + `重送登出前的 cookie ${oldCookieAfter.status}、瀏覽器${cookieGone ? '已刪掉' : '還留著'} cookie`,
    { oldCookieBefore: oldCookieBefore.status, logout, view: afterLogout, me: meLoggedOut, oldCookieAfter: oldCookieAfter.status, cookieGone });

  // ───── 5. 再登入；第二個瀏覽器（另一份 cookie）也以預設密碼登入 ─────
  await reserveLogin(2, '再登入與第二個瀏覽器登入');
  const reLogin = await loginOn(pageA, OWNER, PW.default);
  const viewAReLogin = await view(pageA);
  const contextB = await newContext(browser, { ignoreHTTPSErrors: true, locale: 'zh-TW' });
  const pageB = await contextB.newPage();
  watch(pageB, 'B');
  await pageB.goto(`${base}/`);
  await waitReady(pageB);
  const viewBBefore = await view(pageB);
  const loginB = await loginOn(pageB, OWNER, PW.default);
  const viewBAfter = await view(pageB);
  const meB = await probe(pageB, 'me');
  const sessions = await listSessions(pageA);
  const mine = ours(sessions);
  const other = mine.find((s) => !s.current);
  step('再登入、第二個瀏覽器也登入', reLogin.ok === true && viewAReLogin === 'account' && viewBBefore === 'login' && loginB.ok === true && viewBAfter === 'account'
    && meB.name === OWNER && mine.length === 2 && sessions.length === 2 + preexisting.length && mine.filter((s) => s.current).length === 1
    && !!other && other.id === meB.session?.id,
    `擁有者再登入${reLogin.ok ? '成功' : '失敗'}；第二個瀏覽器登入前畫面 ${viewBBefore}、登入${loginB.ok ? '成功' : '失敗：' + loginB.message}、畫面 ${viewBAfter}；`
      + `擁有者看到這次的 ${mine.length} 個工作階段（之前就在的 ${sessions.length - mine.length} 個），另一個就是第二個瀏覽器的（${other?.id === meB.session?.id ? '識別碼相符' : '識別碼不符'}）`,
    { reLogin, viewBBefore, loginB, viewBAfter, secondBrowserMe: meB.name, sessions: sessions.map(sessionSummary) });

  // ───── 6. 撤銷第二個瀏覽器的工作階段（被撤銷的立即 401）─────
  const revoke = other ? await probe(pageA, 'revokeSession', other.id) : { error: true, message: '找不到另一個工作階段' };
  const revokedAt = Date.now();
  const meBRevoked = await probe(pageB, 'me');
  const firstFailMs = Date.now() - revokedAt;
  const meAKept = await probe(pageA, 'me');
  const sessionsAfterRevoke = await listSessions(pageA);
  await pageB.reload();
  await waitReady(pageB);
  const viewBRevoked = await view(pageB);
  const mineAfterRevoke = ours(sessionsAfterRevoke);
  step('撤銷工作階段（被撤銷的立即 401）', revoke.ok === true && revoke.self === false && meBRevoked.status === 401
    && meAKept.name === OWNER && mineAfterRevoke.length === 1 && mineAfterRevoke[0]?.current === true && sessionsAfterRevoke.length === 1 + preexisting.length
    && !sessionsAfterRevoke.some((s) => s.id === other?.id) && viewBRevoked === 'login',
    `撤銷第二個瀏覽器的之後，它下一個請求就是 ${meBRevoked.status}（${firstFailMs} ms）、重新整理回到 ${viewBRevoked}；擁有者自己的仍有效（me().name ${meAKept.name}）、這次的剩 ${mineAfterRevoke.length} 個`,
    { revoke, secondBrowserMe: meBRevoked, ownerMe: meAKept.name, sessionsAfter: sessionsAfterRevoke.map(sessionSummary), secondBrowserView: viewBRevoked });

  // ───── 7. 改密碼：目前密碼錯 403、太短 400、成功後其他工作階段立即 401（目前這個保留）─────
  await reserveLogin(3, '第二個瀏覽器重新登入與改密碼後的登入');
  const loginBAgain = await loginOn(pageB, OWNER, PW.default);
  const sessionsBeforeChange = await listSessions(pageA);
  const wrongCurrent = await changeOn(pageA, PW.wrongCurrent, PW.next);
  const tooShort = await changeOn(pageA, PW.default, PW.tooShort);
  const meBAfterFailures = await probe(pageB, 'me');
  const changed = await changeOn(pageA, PW.default, PW.next);
  const changedAt = Date.now();
  const meBAfterChange = await probe(pageB, 'me');
  const changeFailMs = Date.now() - changedAt;
  const meAAfterChange = await probe(pageA, 'me');
  const sessionsAfterChange = await listSessions(pageA);
  // 改密碼撤銷「目前這個以外的全部」：這次的第二個瀏覽器，加上之前就在的
  const othersBeforeChange = sessionsBeforeChange.filter((s) => !s.current).length;
  step('改密碼（其他工作階段立即 401）', loginBAgain.ok === true && ours(sessionsBeforeChange).length === 2 && othersBeforeChange === 1 + preexisting.length
    && wrongCurrent.status === 403 && wrongCurrent.code === 'password_failed' && tooShort.status === 400 && tooShort.code === 'password_too_short'
    && meBAfterFailures.name === OWNER && changed.ok === true && changed.revokedSessions === othersBeforeChange && meBAfterChange.status === 401
    && meAAfterChange.name === OWNER && meAAfterChange.session?.recentAuth === true && sessionsAfterChange.length === 1 && sessionsAfterChange[0]?.current === true,
    `第二個瀏覽器重新登入後這次的 ${ours(sessionsBeforeChange).length} 個工作階段（其他 ${othersBeforeChange} 個）；目前的密碼錯 ${wrongCurrent.status} ${wrongCurrent.code}、新密碼太短 ${tooShort.status} ${tooShort.code}，`
      + `兩次失敗後第二個瀏覽器仍${meBAfterFailures.name === OWNER ? '有效' : '無效'}；改成新密碼 ${changed.ok ? 'ok' : '失敗：' + changed.message}、revokedSessions ${changed.revokedSessions}，`
      + `第二個瀏覽器下一個請求 ${meBAfterChange.status}（${changeFailMs} ms）；擁有者自己的仍有效、近期驗證 ${meAAfterChange.session?.recentAuth}、剩 ${sessionsAfterChange.length} 個`,
    { loginBAgain, sessionsBefore: sessionsBeforeChange.map(sessionSummary), wrongCurrent, tooShort, secondBrowserAfterFailures: meBAfterFailures.name ?? meBAfterFailures,
      changed, secondBrowserAfterChange: meBAfterChange, ownerSession: meAAfterChange.session ?? null, sessionsAfter: sessionsAfterChange.map(sessionSummary) });

  // ───── 8. 改密碼後：舊密碼不能登入、新密碼可以 ─────
  await pageB.reload();
  await waitReady(pageB);
  const viewBAfterChange = await view(pageB);
  const oldLogin = await loginOn(pageB, OWNER, PW.default);
  const viewBOld = await view(pageB);
  const newLogin = await loginOn(pageB, OWNER, PW.next);
  const viewBNew = await view(pageB);
  const meBNew = await probe(pageB, 'me');
  step('舊密碼不能登入、新密碼可以', viewBAfterChange === 'login' && oldLogin.status === 401 && oldLogin.code === 'login_failed' && viewBOld === 'login'
    && newLogin.ok === true && viewBNew === 'account' && meBNew.name === OWNER,
    `第二個瀏覽器重新整理是 ${viewBAfterChange} 畫面；預設（舊）密碼 ${oldLogin.status} ${oldLogin.code}、仍在 ${viewBOld}；新密碼登入${newLogin.ok ? '成功' : '失敗：' + newLogin.message}、畫面 ${viewBNew}`,
    { viewBefore: viewBAfterChange, oldLogin, viewAfterOld: viewBOld, newLogin, viewAfterNew: viewBNew, me: meBNew.name ?? meBNew });

  // ───── 9. 再驗證：錯的密碼 403、近期驗證時間不變；對的密碼更新近期驗證 ─────
  const meBeforeStepUp = await probe(pageA, 'me');
  const wrongStepUp = await stepUpOn(pageA, PW.wrongStepUp);
  const meAfterWrongStepUp = await probe(pageA, 'me');
  await sleep(20);
  const stepUp = await stepUpOn(pageA, PW.next);
  const meAfterStepUp = await probe(pageA, 'me');
  const uv0 = meBeforeStepUp.session?.lastUvAt;
  const uv1 = meAfterWrongStepUp.session?.lastUvAt;
  const uv2 = meAfterStepUp.session?.lastUvAt;
  step('再驗證', wrongStepUp.status === 403 && wrongStepUp.code === 'password_failed' && meAfterWrongStepUp.name === OWNER && typeof uv0 === 'number' && uv1 === uv0
    && stepUp.ok === true && typeof stepUp.recentAuthUntil === 'number' && uv2 > uv0 && meAfterStepUp.session?.recentAuth === true
    && meAfterStepUp.session?.recentAuthUntil === stepUp.recentAuthUntil,
    `錯的密碼 ${wrongStepUp.status} ${wrongStepUp.code}、仍在登入狀態、最後驗證時間${uv1 === uv0 ? '不變' : '竟然變了'}；`
      + `對的密碼 ${stepUp.ok ? 'ok' : '失敗：' + stepUp.message}、最後驗證時間 +${uv2 - uv0} ms、近期驗證到 ${iso(stepUp.recentAuthUntil)}（me() ${meAfterStepUp.session?.recentAuthUntil === stepUp.recentAuthUntil ? '相同' : '不同'}）`,
    { wrongStepUp, stepUp, lastUvAt: { before: uv0, afterWrong: uv1, afterRight: uv2 }, recentAuthUntil: meAfterStepUp.session?.recentAuthUntil ?? null });

  // ───── 10. CSP：0 違規＋inline script 被擋（正向對照）─────
  const violationsBefore = await pageA.evaluate(() => window.__hubProbe.violations.slice());
  const violationsB = await pageB.evaluate(() => window.__hubProbe.violations.slice());
  const inlineRan = await pageA.evaluate(() => window.__hubProbe.tryInlineScript());
  await pageA.waitForTimeout(300);
  const violationsAfter = await pageA.evaluate(() => window.__hubProbe.violations.slice());
  const blockedInline = violationsAfter.filter((v) => /^script-src/.test(v.directive) && v.blocked === 'inline');
  step('CSP：0 違規＋插入 inline script 被擋', violationsBefore.length === 0 && violationsB.length === 0 && inlineRan === false
    && violationsAfter.length === 1 && blockedInline.length === 1,
    `正向對照前違規 ${violationsBefore.length} 筆（第二個瀏覽器 ${violationsB.length} 筆）；inline script ${inlineRan ? '竟然執行了' : '沒有執行'}，之後違規 ${violationsAfter.length} 筆（${blockedInline[0]?.directive ?? '—'}）`,
    { violationsBefore, secondBrowserViolations: violationsB, inlineRan, violationsAfter });

  // ───── 11. 跨站 Origin 的 POST 被拒 ─────
  const cookies = await cookieHeader(contextA);
  const json = { 'Content-Type': 'application/json', Cookie: cookies };
  const crossOrigin = await request(`${base}/api/v1/auth/logout`, { method: 'POST', headers: { ...json, Origin: 'https://evil.example', 'X-Myriagent-Csrf': '1' }, body: '{}' });
  const noCsrf = await request(`${base}/api/v1/auth/logout`, { method: 'POST', headers: { ...json, Origin: base }, body: '{}' });
  const noOrigin = await request(`${base}/api/v1/auth/logout`, { method: 'POST', headers: { ...json, 'X-Myriagent-Csrf': '1' }, body: '{}' });
  // 正向對照：同一份 cookie、同站 Origin＋CSRF 標頭的 POST（以目前的密碼再驗證，只更新近期驗證）要過，證明上面三個 403 不是別的原因
  const control = await request(`${base}/api/v1/auth/stepup`, { method: 'POST', headers: { ...json, Origin: base, 'X-Myriagent-Csrf': '1' },
    body: JSON.stringify({ password: PW.next }) });
  const controlJson = parseJson(control.body);
  // 真的從別的網站送出表單（瀏覽器帶 Origin: https://evil.test；SameSite=Strict 的 cookie 本來就不會帶）
  const form = await crossSiteForm(contextA);
  const stillLoggedIn = await probe(pageA, 'me');
  step('跨站 Origin 的 POST 被拒', crossOrigin.status === 403 && /forbidden_origin/.test(crossOrigin.body) && noCsrf.status === 403 && noOrigin.status === 403
    && control.status === 200 && controlJson?.ok === true && form.status === 403 && /forbidden_origin/.test(form.body) && form.origin === 'https://evil.test'
    && stillLoggedIn.name === OWNER,
    `錯誤 Origin ${crossOrigin.status}、同站但沒有 X-Myriagent-Csrf ${noCsrf.status}、沒有 Origin ${noOrigin.status}、別的網站送出的表單（Origin ${form.origin}）${form.status}；`
      + `正向對照（同站＋CSRF 標頭的 POST /api/v1/auth/stepup）${control.status}；擁有者仍在登入狀態`,
    {
      crossOrigin: { status: crossOrigin.status, body: crossOrigin.body },
      sameOriginWithoutCsrfHeader: { status: noCsrf.status, body: noCsrf.body },
      withoutOrigin: { status: noOrigin.status, body: noOrigin.body },
      control: { status: control.status, body: control.body },
      crossSiteForm: form,
      ownerStillLoggedIn: stillLoggedIn.name === OWNER,
    });

  // ───── 12. 改回預設密碼（--base 的伺服器維持原狀，下次照樣能跑）─────
  const restore = await changeOn(pageA, PW.next, PW.default);
  const meBRestore = await probe(pageB, 'me');
  const meARestore = await probe(pageA, 'me');
  step('改回預設密碼', restore.ok === true && restore.revokedSessions === 1 && meBRestore.status === 401 && meARestore.name === OWNER,
    `新密碼改回預設密碼 ${restore.ok ? 'ok' : '失敗：' + restore.message}、revokedSessions ${restore.revokedSessions}（以新密碼登入的第二個瀏覽器 ${meBRestore.status}）；擁有者自己的仍有效`,
    { restore, secondBrowserMe: meBRestore, ownerMe: meARestore.name ?? meARestore });

  // ───── 12.5 版本頁的模組載入失敗：登入頁與配對核准頁照常 ─────
  const faults = await releaseModuleFaults(contextA);
  step('版本頁的模組載入失敗不影響登入頁與配對核准頁', faults.every((f) => f.ok),
    faults.map((f) => `${f.label}：${f.ok ? '' : '（失敗）'}畫面 ${f.seen.view ?? '—（auth.js 沒有執行）'}、狀態列「${f.seen.status ?? '—'}」`
      + `${f.seen.pairResult ? `、配對結果「${f.seen.pairResult}」` : ''}、要了 ${f.requested.join('＋') || '（沒有）'}、攔下 ${f.aborted.join('＋') || '（沒有）'}`).join('；'),
    { faults });

  // ───── 13. 主控台 0 錯誤（排除預期的 HTTP 錯誤與 CSP 正向對照）─────
  const relevant = consoleLog.filter((m) => m.type === 'error' || m.type === 'pageerror');
  const classified = relevant.map((m) => ({ ...m, expected: expectedConsole(m) }));
  const unexpected = classified.filter((m) => !m.expected);
  const unexpectedHttp = httpErrors.filter((e) => !EXPECTED_HTTP_ERRORS.some((x) => x.path === new URL(e.url).pathname && x.status === e.status));
  const countMismatch = EXPECTED_HTTP_ERRORS.filter((x) => x.count !== undefined)
    .map((x) => ({ path: x.path, status: x.status, expected: x.count, actual: httpErrors.filter((e) => new URL(e.url).pathname === x.path && e.status === x.status).length }))
    .filter((x) => x.actual !== x.expected);
  step('主控台 0 個非預期錯誤', unexpected.length === 0 && unexpectedHttp.length === 0 && countMismatch.length === 0,
    `兩個瀏覽器的主控台錯誤 ${relevant.length} 筆，排除預期的（${summarize(classified)}）後非預期 ${unexpected.length} 筆；非預期的 HTTP 錯誤 ${unexpectedHttp.length} 筆；`
      + `刻意送的錯誤次數${countMismatch.length === 0 ? '都相符' : '不符 ' + countMismatch.map((x) => `${x.path} ${x.status}：${x.actual}／${x.expected}`).join('、')}`,
    { expectedHttpErrors: EXPECTED_HTTP_ERRORS, console: classified, httpErrors, unexpected, unexpectedHttp, countMismatch });

  // 沒有對應端點的路徑：/api 底下匿名 401（探測不出端點是否存在）；其他的（瀏覽器自動要的 /favicon.ico 等）不論是否登入一律 404，
  // 而且登入頁明確指定圖示 /app/favicon.ico（匿名 200，與桌面 app 圖示同一張圖），瀏覽器不會去要 /favicon.ico（主控台與回應裡都不該出現）
  const ownerCookies = await cookieHeader(contextA);
  const faviconAnonymous = await request(`${base}/favicon.ico`);
  const faviconOwner = await request(`${base}/favicon.ico`, { headers: { Cookie: ownerCookies } });
  const ownerMeNode = await request(`${base}/api/v1/auth/me`, { headers: { Cookie: ownerCookies } });
  const unknownAnonymous = await request(`${base}/no-such-page`);
  const apiAnonymous = await request(`${base}/api/v1/no-such-endpoint`);
  const appIcon = await request(`${base}/app/favicon.ico`);
  const faviconSeen = classified.filter((m) => m.url && new URL(m.url).pathname === '/favicon.ico').length
    + httpErrors.filter((e) => new URL(e.url).pathname === '/favicon.ico').length;
  step('未對應路徑：一律 404（/api 底下匿名 401）、瀏覽器不要 /favicon.ico', faviconAnonymous.status === 404 && faviconOwner.status === 404 && ownerMeNode.status === 200
    && unknownAnonymous.status === 404 && apiAnonymous.status === 401 && faviconSeen === 0
    && appIcon.status === 200 && appIcon.headers['content-type'] === 'image/x-icon',
    `匿名 /favicon.ico ${faviconAnonymous.status}、登入後 ${faviconOwner.status}（同一份 cookie 的 /api/v1/auth/me ${ownerMeNode.status}）、匿名 /no-such-page ${unknownAnonymous.status}、`
      + `匿名 /api/v1/no-such-endpoint ${apiAnonymous.status}；兩個瀏覽器的主控台與回應裡 /favicon.ico ${faviconSeen} 筆；`
      + `登入頁的圖示（匿名 /app/favicon.ico）${appIcon.status} ${appIcon.headers['content-type']}`,
    { faviconAnonymous: faviconAnonymous.status, faviconOwner: faviconOwner.status, ownerMe: ownerMeNode.status, unknownAnonymous: unknownAnonymous.status,
      apiAnonymous: apiAnonymous.status, faviconSeen, appIcon: appIcon.status, appIconType: appIcon.headers['content-type'] });

  // 媒體路徑（WP4.7 D9）：/m/、/t/、/f/ 與複本代理一律套媒體的 sandbox CSP＋nosniff（匿名的 401 也是）；
  // 擁有者要不存在的鍵回 404 not_found、no-store（沒有任何電腦連著，只驗路由、授權與安全標頭）
  const MEDIA_CSP = "default-src 'none'; img-src 'self'; media-src 'self'; style-src 'unsafe-inline'; sandbox";
  const mediaKey = `d_${'0'.repeat(26)}-${'0'.repeat(26)}`;
  const mediaChecks = [];
  for (const [label, url, cookie, status] of [
    ['匿名 /m/', `/m/${mediaKey}`, null, 401],
    ['匿名 /t/', `/t/${mediaKey}?s=320`, null, 401],
    ['匿名 /f/', `/f/${mediaKey}`, null, 401],
    ['匿名複本代理', `/api/v1/device/media/${mediaKey}?purpose=media`, null, 401],
    ['擁有者 /m/', `/m/${mediaKey}`, ownerCookies, 404],
    ['擁有者 /t/', `/t/${mediaKey}?s=320`, ownerCookies, 404],
    ['擁有者 /f/', `/f/${mediaKey}`, ownerCookies, 404],
  ]) {
    const r = await request(`${base}${url}`, { headers: cookie ? { Cookie: cookie } : {} });
    const error = parseJson(r.body)?.error ?? null;
    const ok = r.status === status && r.headers['content-security-policy'] === MEDIA_CSP && r.headers['x-content-type-options'] === 'nosniff'
      && (status !== 404 || (error === 'not_found' && r.headers['cache-control'] === 'no-store'));
    mediaChecks.push({ label, url, status: r.status, expected: status, ok, error, csp: r.headers['content-security-policy'] ?? null, cacheControl: r.headers['cache-control'] ?? null });
  }
  step('媒體路徑：sandbox CSP＋nosniff、匿名 401、擁有者不存在的鍵 404', mediaChecks.every((c) => c.ok),
    mediaChecks.map((c) => `${c.label} ${c.status}${c.ok ? '' : `（預期 ${c.expected}，CSP ${c.csp === MEDIA_CSP ? '對' : '不對'}，${c.error ?? '-'}，${c.cacheControl ?? '-'}）`}`).join('、'),
    { mediaChecks });

  // ───── 14. 稽核紀錄（近期驗證內可讀；自己啟動的伺服器先以 admin verify-audit 驗過整條鏈）─────
  let verify = null;
  if (!external) {
    const v = spawnSync(hubExe, ['admin', 'verify-audit', '--data', dataDir, '--wait', '15'], { encoding: 'utf8', windowsHide: true, timeout: 30000, env: childEnv() });
    verify = { status: v.status, stdout: (v.stdout ?? '').trim() };
  }
  const audit = await pageFetch(pageA, 'GET', '/api/v1/audit?limit=200');
  const entries = audit.json?.entries ?? [];
  const countOf = (action, outcome) => entries.filter((e) => e.action === action && e.outcome === outcome).length;
  // 自己啟動的伺服器次數要剛好；--base 的伺服器可能有之前的紀錄，只要求至少
  const expectedAudit = [
    ['owner.created', 'ok', external ? 0 : 1],
    ['auth.login', 'ok', 5],
    ['auth.login', 'denied', 3],
    ['auth.logout', 'ok', 1],
    ['session.revoked', 'ok', 1],
    ['owner.password_changed', 'ok', 2],
    ['owner.password_changed', 'denied', 1],
    ['auth.step_up', 'ok', 2],
    ['auth.step_up', 'denied', 1],
  ];
  const auditCounts = expectedAudit.map(([a, o, n]) => ({ action: a, outcome: o, expected: n, actual: countOf(a, o) }));
  const countsOk = auditCounts.every((x) => (external ? x.actual >= x.expected : x.actual === x.expected));
  const created = entries.find((e) => e.action === 'owner.created');
  const createdBySystem = external || (created?.actorType === 'system' && created?.target === OWNER);
  const chain = audit.json?.chain ?? null;
  const ownSeqs = entries.filter((e) => !(e.action === 'admin.command' && e.target === 'verify-audit')).map((e) => e.seq);
  const lastOwnSeq = ownSeqs.length ? Math.max(...ownSeqs) : null;
  const chainOk = external ? chain?.ok !== false : verify.status === 0 && chain?.ok === true && chain.lastSeq >= lastOwnSeq;
  const auditLeaks = leaks(expandJson(audit.text ?? ''));
  const selfTest = scannerSelfTest();
  step('稽核紀錄（不含密碼）', audit.status === 200 && countsOk && createdBySystem && chainOk && auditLeaks.length === 0 && selfTest.found === selfTest.total,
    `/api/v1/audit ${audit.status}，${entries.length} 筆：${auditCounts.map((x) => `${x.action}${x.outcome === 'ok' ? '' : '（' + x.outcome + '）'}×${x.actual}`).join('、')}；`
      + `owner.created 的執行者 ${created?.actorType ?? '—'}；雜湊鏈${chainOk ? '正常' : '不一致或沒驗證'}`
      + `${external ? '' : `（admin verify-audit 結束碼 ${verify.status}，驗到 seq ${chain?.lastSeq ?? '—'}、這次最後一筆 ${lastOwnSeq}）`}；`
      + `回應全文搜尋 ${Object.keys(PASSWORDS).length} 個用過的密碼：${auditLeaks.length === 0 ? '都沒有出現' : '出現 ' + auditLeaks.join('、')}`
      + `（正向對照：包在 detail 形狀裡的 ${selfTest.found}／${selfTest.total} 個找得到）`,
    { status: audit.status, counts: auditCounts, chain, verify, lastOwnSeq, leaks: auditLeaks, scannerSelfTest: selfTest,
      entries: entries.map((e) => ({ seq: e.seq, actorType: e.actorType, action: e.action, target: e.target, outcome: e.outcome, ip: e.ip, detail: e.detail })) });

  // ───── 15. 結束時登出（第二個瀏覽器的已在改回預設密碼時撤銷；--base 的伺服器不留下這次的工作階段）─────
  const finalCookie = await cookieHeader(contextA);
  const finalLogout = await probe(pageA, 'logout');
  const finalMe = await request(`${base}/api/v1/auth/me`, { headers: { Cookie: finalCookie } });
  step('結束時登出', finalLogout.ok === true && finalMe.status === 401 && (await view(pageA)) === 'login',
    `擁有者登出${finalLogout.ok ? '成功' : '失敗：' + finalLogout.message}、重送登出前的 cookie ${finalMe.status}（這次建立的工作階段都已結束）`,
    { logout: finalLogout, oldCookie: finalMe.status });
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
  scanForPasswords();
  result.finishedAt = new Date().toISOString();
  result.ok = result.steps.every((s) => s.ok !== false);
  save();
  // 寫出去的檔案本身再確認一次（最後一次 save 只多了上一步的計數與完成時間）
  const onDisk = leaks(expandJson(fs.readFileSync(outFile, 'utf8')));
  if (onDisk.length > 0) {
    result.ok = false;
    console.log(`[失敗] 結果檔寫出後出現密碼（${onDisk.join('、')}）`);
  }
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
  const admin = spawnSync(hubExe, ['admin', 'stop', '--data', dataDir, '--wait', '15'], { encoding: 'utf8', windowsHide: true, timeout: 30000, env: childEnv() });
  while (hubExit === null && Date.now() - started < 30000) await sleep(200);
  if (hubExit === null) {
    hubProc.kill();
    step('停止 MyriagentHub', false, 'admin stop 後 30 秒沒有結束，已強制結束（file 模式會留下孤兒金鑰檔）', { admin: admin.stdout });
    return;
  }
  step('停止 MyriagentHub', admin.status === 0 && hubExit === 0, `admin stop 結束碼 ${admin.status}，伺服器 ${Date.now() - started} ms 後正常結束（結束碼 ${hubExit}）`);
}

// 結果檔（到目前為止的內容）與伺服器記錄（主控台輸出、資料根 diag\ 的記錄檔）裡不得出現任何用過的密碼
function scanForPasswords() {
  const scanned = [];
  const found = {};
  const scan = (name, text) => {
    scanned.push(name);
    for (const label of leaks(text)) (found[label] ??= []).push(name);
  };
  scan('結果檔', expandJson(JSON.stringify(result)));
  if (!external) {
    scan('hub-console.log', readText(path.join(work, 'hub-console.log')));
    const diag = path.join(dataDir, 'diag');
    for (const f of listFiles(diag)) scan(path.relative(work, f), readText(f));
  }
  const labels = Object.keys(found);
  const selfTest = scannerSelfTest();
  step('結果檔與伺服器記錄不含密碼', labels.length === 0 && selfTest.found === selfTest.total,
    `搜尋 ${scanned.length} 個檔案（${scanned.join('、')}）裡 ${Object.keys(PASSWORDS).length} 個用過的密碼：`
      + `${labels.length === 0 ? '都沒有出現' : labels.map((l) => `${l}出現在 ${found[l].join('、')}`).join('；')}（正向對照 ${selfTest.found}／${selfTest.total} 個找得到）`,
    { scanned, found, scannerSelfTest: selfTest });
}

// 用過的密碼有哪些出現在文字裡（回傳名稱，不回傳密碼本身）
function leaks(text) {
  return Object.values(PASSWORDS).filter((p) => text.includes(p.value)).map((p) => p.label);
}

// 搜尋器的正向對照：每個密碼包在「JSON 字串裡的 JSON」（稽核 detail 的形狀）裡都要找得到
function scannerSelfTest() {
  const found = Object.values(PASSWORDS).filter((p) => leaks(expandJson(JSON.stringify({ entries: [{ detail: JSON.stringify({ x: p.value }) }] }))).includes(p.label)).length;
  return { found, total: Object.keys(PASSWORDS).length };
}

// JSON 原文＋解開後的所有字串（含字串裡又是 JSON 的，例如稽核的 detail）：跳脫過的內容也搜得到
function expandJson(text) {
  const parts = [text];
  const walk = (v) => {
    if (typeof v === 'string') {
      parts.push(v);
      const t = v.trim();
      if (t.startsWith('{') || t.startsWith('[')) {
        try { walk(JSON.parse(t)); } catch { /* 不是 JSON */ }
      }
    } else if (Array.isArray(v)) {
      v.forEach(walk);
    } else if (v && typeof v === 'object') {
      for (const [k, x] of Object.entries(v)) {
        parts.push(k);
        walk(x);
      }
    }
  };
  try { walk(JSON.parse(text)); } catch { /* 不是 JSON：只搜原文 */ }
  return parts.join('\n');
}

// 登入類端點的額度：這一段要用 n 次，加上這個時間窗已用的超過上限就先等
async function reserveLogin(n, purpose) {
  if (loginBudget.used + n <= LOGIN_PER_MINUTE) return;
  const ms = Math.max(0, loginBudget.lastAt + 61000 - Date.now());
  step('等登入類端點的額度', null,
    `正式設定未登入的登入每 IP 每分鐘 ${LOGIN_PER_MINUTE} 次：這個時間窗已用 ${loginBudget.used} 次，${purpose}要 ${n} 次，等 ${(ms / 1000).toFixed(1)} 秒`);
  await sleep(ms);
  loginBudget.waitedMs += ms;
  loginBudget.used = 0;
}

// 送一個未登入的登入請求（計入 IP 的額度）
async function limited(work) {
  try {
    return await work();
  } finally {
    loginBudget.used++;
    loginBudget.total++;
    loginBudget.lastAt = Date.now();
  }
}

function loginOn(page, userName, password) {
  return limited(() => probe(page, 'login', userName, password));
}

// 改密碼與再驗證：已登入的工作階段分區，不計入 IP 的額度
function changeOn(page, current, next) {
  return probe(page, 'changePassword', current, next);
}

function stepUpOn(page, password) {
  return probe(page, 'stepUp', password);
}

// 別的網站（evil.test，由 Playwright 攔截供應）自動送出表單 POST 到 /api/v1/auth/logout，取回瀏覽器實際送出的 Origin 與回應
async function crossSiteForm(context) {
  await context.route('https://evil.test/**', (route) => route.fulfill({
    status: 200,
    contentType: 'text/html; charset=utf-8',
    body: `<!doctype html><meta charset="utf-8"><form id="f" method="POST" action="${base}/api/v1/auth/logout" enctype="text/plain">`
      + '<input name="x" value="1"></form><script>document.getElementById("f").submit();</script>',
  }));
  const page = await context.newPage();
  try {
    const [r] = await Promise.all([
      page.waitForResponse((x) => x.url().startsWith(`${base}/api/v1/auth/logout`), { timeout: 15000 }),
      page.goto('https://evil.test/attack'),
    ]);
    const headers = await r.request().allHeaders();
    return { status: r.status(), origin: headers.origin ?? null, cookieSent: 'cookie' in headers, body: await r.text().catch(() => '') };
  } catch (e) {
    return { status: null, origin: null, body: '', error: String(e?.message ?? e) };
  } finally {
    await page.close();
    await context.unroute('https://evil.test/**');
  }
}

// 版本頁的兩個模組（releases.js、releaserules.js）載入失敗時，auth.js 的登入頁與配對核准頁照常（auth.js 以動態 import 載入它們）：每個情境另開瀏覽器內容，
// 以 route 讓指定的模組請求失敗。不監看這些內容（被攔下的模組會在主控台留下載入失敗，不算進第 13 步）；已登入的情境沿用擁有者的 cookie
// （不再登入：不吃登入的額度、不多寫稽核）。要了哪些模組與攔下了哪些是正向對照：攔截真的發生了，畫面照常才有意義；
// 不是版本頁時 releases.js 一次都不要（第一次顯示版本頁才載入）
async function releaseModuleFaults(ownerContext) {
  const ownerState = await ownerContext.storageState();
  const MODULES = ['/releaserules.js', '/releases.js'];
  const scenarios = [
    { label: '兩個都失敗、未登入的 /#pair', hash: '#pair', block: MODULES, owner: false, requested: ['/releaserules.js'], aborted: ['/releaserules.js'],
      expect: (s) => s.view === 'login' && s.pairHint === true && s.status === '請登入' },
    { label: '兩個都失敗、已登入的 /#pair=碼', hash: '#pair=ZZZZ-ZZZZ', block: MODULES, owner: true, requested: ['/releaserules.js'], aborted: ['/releaserules.js'],
      expect: (s) => s.view === 'pair' && s.pairView === true && /找不到這個配對碼/.test(s.pairResult ?? '') && s.status === '' },
    { label: '只有 releases.js 失敗、已登入的 /#releases', hash: '#releases', block: ['/releases.js'], owner: true, requested: MODULES, aborted: ['/releases.js'],
      expect: (s) => s.view === 'releases' && s.status === '版本頁載入失敗，請重新整理頁面' && s.tone === 'error' && s.releases === null },
    { label: '只有 releaserules.js 失敗、已登入的 /#releases', hash: '#releases', block: ['/releaserules.js'], owner: true, requested: ['/releaserules.js'],
      aborted: ['/releaserules.js'], expect: (s) => s.view === 'account' && s.status === '' },
  ];
  const results = [];
  for (const s of scenarios) {
    const context = await newContext(browser, { ignoreHTTPSErrors: true, locale: 'zh-TW', ...(s.owner ? { storageState: ownerState } : {}) });
    const requested = [];
    const aborted = [];
    try {
      await context.route((url) => s.block.includes(url.pathname), (route) => {
        aborted.push(new URL(route.request().url()).pathname);
        return route.abort();
      });
      const page = await context.newPage();
      page.on('request', (r) => {
        const p = new URL(r.url()).pathname;
        if (MODULES.includes(p)) requested.push(p);
      });
      await page.goto(`${base}/${s.hash}`);
      const ready = await page.waitForFunction(() => window.__hubProbe && window.__hubProbe.ready, null, { timeout: 10000 }).then(() => true, () => false);
      const seen = await page.evaluate(() => {
        const el = (id) => document.getElementById(id);
        const probe = window.__hubProbe;
        return {
          view: probe ? probe.view : null,
          status: el('status')?.textContent ?? null,
          tone: el('status')?.dataset.tone ?? null,
          pairHint: el('login-pair-hint') ? !el('login-pair-hint').hidden : null,
          pairView: el('view-pair') ? !el('view-pair').hidden : null,
          pairResult: el('pair-result')?.textContent ?? null,
          releases: probe && typeof probe.releases === 'function' ? probe.releases() : 'no-probe',
          violations: probe ? probe.violations.length : null,
        };
      });
      const same = (a, b) => JSON.stringify([...new Set(a)].sort()) === JSON.stringify([...b].sort());
      const ok = ready && s.expect(seen) && seen.violations === 0 && same(requested, s.requested) && same(aborted, s.aborted);
      results.push({ label: s.label, hash: s.hash, blocked: s.block, owner: s.owner, ok, ready, seen, requested, aborted,
        expectedRequested: s.requested, expectedAborted: s.aborted });
    } catch (e) {
      results.push({ label: s.label, hash: s.hash, blocked: s.block, owner: s.owner, ok: false, ready: false, seen: {}, requested, aborted, error: String(e?.message ?? e) });
    } finally {
      await context.close();
    }
  }
  return results;
}

function watch(page, label) {
  page.on('console', (m) => consoleLog.push({ page: label, type: m.type(), text: m.text(), url: m.location()?.url ?? '' }));
  page.on('pageerror', (e) => consoleLog.push({ page: label, type: 'pageerror', text: String(e?.message ?? e), url: '' }));
  page.on('response', (r) => {
    if (r.status() >= 400) httpErrors.push({ page: label, method: r.request().method(), status: r.status(), url: r.url() });
  });
}

// 主控台錯誤是否在預期內：預期的 HTTP 錯誤（依網址路徑與狀態碼），或 CSP 正向對照那一筆 inline script
function expectedConsole(m) {
  const status = /status of (\d{3})/.exec(m.text);
  if (status && m.url) {
    const p = new URL(m.url).pathname;
    const hit = EXPECTED_HTTP_ERRORS.find((x) => x.path === p && x.status === Number(status[1]));
    if (hit) return hit.why;
  }
  if (m.page === 'A' && /Content Security Policy/.test(m.text) && /inline/i.test(m.text)) return 'CSP 正向對照（刻意插入的 inline script）';
  return null;
}

function summarize(classified) {
  const counts = new Map();
  for (const m of classified) {
    if (m.expected) counts.set(m.expected, (counts.get(m.expected) ?? 0) + 1);
  }
  return [...counts].map(([why, n]) => `${why}×${n}`).join('；') || '無';
}

function waitReady(page) {
  return page.waitForFunction(() => window.__hubProbe && window.__hubProbe.ready, null, { timeout: 20000 });
}

function view(page) {
  return page.evaluate(() => window.__hubProbe.view);
}

// 呼叫頁面上 window.__hubProbe 的流程（以名稱呼叫，不在頁面裡 eval——頁面的 CSP 不允許）；例外轉成 { error, message, status, code }
function probe(page, method, ...params) {
  return page.evaluate(async ([m, p]) => {
    try {
      const r = await window.__hubProbe[m](...p);
      return r ?? { ok: true };
    } catch (e) {
      return { error: true, message: e.message, status: e.status ?? null, code: e.code ?? null };
    }
  }, [method, params]);
}

// 頁面內的同源 fetch（帶 cookie；非 GET 加 CSRF 標頭，與 auth.js 相同）
function pageFetch(page, method, url, body) {
  return page.evaluate(async ([m, u, b]) => {
    const headers = m === 'GET' ? {} : { 'X-Myriagent-Csrf': '1', 'Content-Type': 'application/json' };
    const r = await fetch(u, { method: m, headers, body: m === 'GET' ? undefined : JSON.stringify(b ?? {}), credentials: 'same-origin', cache: 'no-store' });
    const text = await r.text();
    let parsed = null;
    try { parsed = text ? JSON.parse(text) : null; } catch { parsed = null; }
    return { status: r.status, json: parsed, text };
  }, [method, url, body ?? null]);
}

async function listSessions(page) {
  return (await pageFetch(page, 'GET', '/api/v1/sessions')).json?.sessions ?? [];
}

async function cookieHeader(context) {
  return (await context.cookies(base)).map((c) => `${c.name}=${c.value}`).join('; ');
}

// Node 端的 HTTPS 請求（自簽憑證：不驗證；用來等伺服器起來、重送舊 cookie 與送刻意偽造的跨站請求）
function request(url, { method = 'GET', headers = {}, body } = {}) {
  return new Promise((resolve, reject) => {
    const req = https.request(url, { method, headers, rejectUnauthorized: false, timeout: 15000 }, (res) => {
      // 憑證要在回應開始時取（end 之後 socket 可能已經拆開）：--base 的正式站守門用
      const cert = res.socket?.getPeerCertificate?.() ?? null;
      let data = '';
      res.setEncoding('utf8');
      res.on('data', (d) => { data += d; });
      res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, body: data, cert }));
    });
    req.on('error', reject);
    req.on('timeout', () => req.destroy(new Error('逾時')));
    if (body !== undefined) req.write(body);
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

// 這個 MyriagentHub 匯入的（啟動期間新增的）停止後都要不在；之後才出現、現在還在的只記成觀察（同一個使用者的其他程序，
// 例如別的 worktree 的 Kestrel 測試：整個存放區的前後差會把它們算進來而誤判，WP4.9 步 14 改成只算自己的，同 pairing.mjs）
function checkKeys() {
  if (!keysBefore || !keysStarted) return;
  const now = listKeys();
  const mine = [...keysStarted].filter((k) => !keysBefore.has(k));
  const left = mine.filter((k) => now.includes(k));
  step('主機副作用（孤兒金鑰檔）', left.length === 0, `這個 MyriagentHub 啟動期間新增的金鑰檔 ${mine.length} 個，停止後還在 ${left.length} 個（${keysDir}）`, { mine, left });
  const others = now.filter((k) => !keysBefore.has(k) && !keysStarted.has(k));
  if (others.length > 0) step('其他程序的金鑰檔', null, `啟動之後才出現、現在還在的金鑰檔 ${others.length} 個（不是這個 MyriagentHub 匯入的）：只記錄、不算失敗`, { others });
}

function listFiles(dir) {
  try {
    return fs.readdirSync(dir, { withFileTypes: true }).filter((d) => d.isFile()).map((d) => path.join(dir, d.name));
  } catch {
    return [];
  }
}

function readText(file) {
  try {
    return fs.readFileSync(file, 'utf8');
  } catch {
    return '';
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

// 隨機字（不含十六進位字母與數字，搜尋時不會和雜湊、識別碼碰巧相同）
function word(n) {
  const alphabet = 'ghjkmnpqrstvwxyz';
  return Array.from({ length: n }, () => alphabet[crypto.randomInt(alphabet.length)]).join('');
}

function iso(ms) {
  return typeof ms === 'number' ? new Date(ms).toISOString() : '—';
}

function cookieSummary(c, days) {
  return c ? { name: c.name, httpOnly: c.httpOnly, secure: c.secure, sameSite: c.sameSite, path: c.path, persistentDays: days === null ? null : Number(days.toFixed(3)) } : null;
}

function sessionSummary(s) {
  return { id: s.id?.slice(0, 8), current: s.current, userAgent: s.userAgent?.slice(0, 60) ?? null, ip: s.ipLast ?? s.ipFirst ?? null };
}

function parseArgs(list) {
  const known = new Set(['hub', 'port', 'work', 'out', 'headed', 'base']);
  const out = {};
  for (let i = 0; i < list.length; i++) {
    const a = list[i];
    if (!a.startsWith('--')) throw new Error(`看不懂的參數：${a}`);
    const key = a.slice(2);
    if (key === 'data') throw new Error('--data 已不需要：擁有者改成帳號密碼登入，沒有一次性設定碼了');
    if (!known.has(key)) throw new Error(`看不懂的參數：${a}`);
    if (key === 'headed') { out.headed = true; continue; }
    if (i + 1 >= list.length) throw new Error(`--${key} 缺少值`);
    out[key] = list[++i];
  }
  return out;
}
