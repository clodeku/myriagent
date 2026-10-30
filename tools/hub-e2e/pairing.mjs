// Myriagent Hub 配對與儀表板端到端（WP3.5 計畫 docs/plans/2026-10-16-wp3.5-web-transport.md 步驟 13；設計 13.3 的演示）。
// 本機 MyriagentHub（暫存資料根、自動挑的高埠、Development）＋兩台 tools/fake-device 假裝置，以真的瀏覽器（本機 Chrome 無頭、暫存設定檔）走：
//   匿名開 A 的核准網址 → 以表單登入、留在核准頁 → 核准 A（一律先再輸入一次密碼）→ 手機寬度的分頁手動輸入 B 的配對碼（B 的名稱是 XSS 字串，照字面顯示）→ 核准 B
//   → 兩台跑劇本（同一個 Claude 身分、A 另有 Codex，劇本最後長 wait 撐住在線）→ 網頁版：矩陣、每列一台查用量、各電腦 CLI、電腦頁、CSP 正向對照
//   → 手機寬度的儀表板 → 改電腦名稱、改帳戶名稱、上移 → 撤銷 B（--stepup：近期驗證過期、跳密碼框）→ 移除已撤銷的 B → 從目錄刪除 Codex
//   → 登出時開著 /app/ 的分頁被導回 / → 排空（admin drain：A 收到 shutdown；重新啟動的 A 的 Hello 回 draining；undrain 後回到 Live、留在線上）
//   → 稽核筆數 → 版本頁（/#releases）→ 主控台只有預期的錯誤（次數剛好）
//   → 收尾：結束假裝置、unpair、admin stop，MyriagentHub 與裝置的金鑰檔都不殘留 → 結果檔與記錄不含密碼。
// 結果每一步先寫進結果檔（browser.close() 可能不返回，所以關閉加時限）。
// 與 hub-e2e.mjs、gateway.mjs（含其他 worktree 的）以 %TEMP%\myriagent-tools\hub-e2e.lock 序列化（lock.mjs，見 README）。
//
// 用法（先 dotnet build src/MyriagentHub/MyriagentHub.csproj -c Release、dotnet build tools/fake-device -c Release、cd tools/hub-e2e && npm install）：
//   node pairing.mjs                     自己啟動 MyriagentHub 與兩台假裝置，跑完全部收掉（約 1 分鐘）
//   node pairing.mjs --stepup            近期驗證改成 1 分鐘、撤銷前等到最後一次輸入密碼的 65 秒後：撤銷時跳密碼框（約 2 分鐘）
//   node pairing.mjs --hub <MyriagentHub.exe> --fake-device <FakeDevice.exe> --port <埠> --work <資料夾> --out <結果檔> --headed
import { chromium } from 'playwright-core';
import { spawn } from 'node:child_process';
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
const work = path.resolve(args.work ?? path.join(os.tmpdir(), 'myriagent-tools', 'pairing', stamp));
const outFile = path.resolve(args.out ?? path.join(work, 'result.json'));
const hubExe = path.resolve(args.hub ?? path.join(repo, 'src', 'MyriagentHub', 'bin', 'Release', 'net10.0', 'win-x64', 'MyriagentHub.exe'));
const fakeExe = path.resolve(args['fake-device'] ?? path.join(repo, 'tools', 'fake-device', 'bin', 'Release', 'net10.0-windows', 'win-x64', 'FakeDevice.exe'));
const stepUpMode = args.stepup === true;

const OWNER = 'admin';
// 伺服器的 OwnerAccount.DefaultPassword（首次啟動以它建立擁有者）；結果檔與主控台只寫名稱
const PASSWORDS = { default: { label: '預設密碼', value: 'changeme2026' } };
const DEFAULT_PASSWORD = PASSWORDS.default.value;
const NAME_A = 'E2E-A';
const NAME_A2 = 'E2E-A2';
// B 的裝置名稱是 XSS 夾具：核准頁、矩陣、電腦頁都要照字面顯示，不能變成元素、不能跳出對話框
const NAME_B = '<img src=x onerror=alert(1)>';
const CLAUDE_LABEL = 'E2E Claude';
const CLAUDE_LABEL2 = 'E2E Claude 改名';
const DELETE_TEXT = '電腦之後再回報同一個登入身分時，這個帳戶會再出現';
// 移除已撤銷的電腦（設計 14.22）的確認框要寫明對話一起刪除
const REMOVE_TEXT = '網頁版與其他電腦上的這些對話也會跟著消失';
// 子程序不繼承的環境變數（本 session 的 Claude／agy 工作階段、各代理的設定與金鑰、同步覆寫、ASP.NET Core 環境、GC 設定的 DOTNET_GC* 與 COMPlus_*；
// 同 lib/child-env.mjs 與 measure_sync.py）
const STRIP_PREFIXES = ['CLAUDE', 'ANTHROPIC_', 'OPENAI_', 'ANTIGRAVITY_', 'GROK_', 'XAI_', 'MYRIAGENT_', 'CODEX_', 'ASPNETCORE_', 'DOTNET_ENVIRONMENT', 'DOTNET_GC', 'COMPLUS_'];
const DEVICE_KEY_PREFIX = 'Myriagent.Device.';
// --stepup：近期驗證 1 分鐘，撤銷前等到「最後一次輸入密碼」的 65 秒後
const RECENT_AUTH_WAIT_MS = 65000;

fs.mkdirSync(work, { recursive: true });
const result = { tool: 'pairing', startedAt: new Date().toISOString(), work, hubExe, fakeExe, stepUp: stepUpMode, base: null, steps: [], screenshots: [], ok: null };
// MyriagentHub（file 憑證模式）匯入 PFX 時寫在使用者金鑰存放區的金鑰檔：正常結束會刪，被強制結束就變孤兒。裝置金鑰（軟體 KSP）也在這裡，unpair 時刪。
// 存放區是整個使用者共用的：取得鎖之後（A）與 /healthz 200 之後（B）各列一次，B 減 A 算這個 MyriagentHub 的；裝置金鑰以 device.json 記的名稱認
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
const dialogs = [];
// CSP 違規（exposeBinding 送回 Node 端累積：分頁導覽之後也不會遺失）
const cspLog = [];
const pageLabels = new Map();
// 假裝置：所有啟動過的程序（收尾時還在跑的一律結束）、兩台的資料根與裝置金鑰名稱
const fakes = [];
let fakeSeq = 0;
const devices = {
  A: { data: path.join(work, 'fdA'), name: NAME_A, id: null, keyName: null, run: null },
  B: { data: path.join(work, 'fdB'), name: NAME_B, id: null, keyName: null, run: null },
};

try {
  // ───── 1. 序列化鎖 ─────
  const lock = await acquireLock('pairing', work);
  if (!lock.ok) throw new Error(`等不到序列化鎖（${lockFile}，${(LOCK_WAIT_MS / 1000).toFixed(0)} 秒）：另一個 hub-e2e／gateway／pairing 還在跑，持有者 ${lock.holder ?? '不明'}`);
  lockHeld = true;
  step('取得序列化鎖', true, `${lockFile}（等了 ${lock.waitedMs} ms${lock.staleRemoved ? '，清掉了超過 10 分鐘的舊鎖' : ''}）`, { lockFile, waitedMs: lock.waitedMs, staleRemoved: lock.staleRemoved });

  // ───── 2. 伺服器 ─────
  if (!fs.existsSync(hubExe)) throw new Error(`找不到 ${hubExe}（先 dotnet build src/MyriagentHub/MyriagentHub.csproj -c Release，或用 --hub 指定）`);
  if (!fs.existsSync(fakeExe)) throw new Error(`找不到 ${fakeExe}（先 dotnet build tools/fake-device -c Release，或用 --fake-device 指定）`);
  if (!fs.existsSync(path.join(path.dirname(hubExe), 'wwwroot', 'transport-hub.js'))) throw new Error(`${hubExe} 的 wwwroot 沒有 transport-hub.js：這支要 WP3.5 之後的建置`);
  const port = args.port ? Number(args.port) : await freePort();
  dataDir = path.join(work, 'hub-data');
  base = `https://localhost:${port}`;
  result.base = base;
  const hubArgs = ['--data', dataDir, '--environment', 'Development', `--Hub:HttpsPort=${port}`, '--Hub:BindAddress=localhost',
    '--Hub:BelowNormalPriority=false', '--Hub:AdminInbox:PollSeconds=1', '--Hub:Backup:Enabled=false', '--Hub:SelfTest:Enabled=false'];
  if (stepUpMode) hubArgs.push('--Hub:Session:RecentAuthMinutes=1');
  keysBefore = new Set(listKeys());
  const logFd = fs.openSync(path.join(work, 'hub-console.log'), 'w');
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
  if (!health) throw new Error(`MyriagentHub 沒有起來（結束碼 ${hubExit}），看 ${path.join(work, 'hub-console.log')}`);
  keysStarted = new Set(listKeys());
  const importedKeys = [...keysStarted].filter((k) => !keysBefore.has(k));
  step('啟動 MyriagentHub', true, `${base}（${Date.now() - started} ms 後 /healthz 200，build ${health.build}）；啟動期間新增的金鑰檔 ${importedKeys.length} 個（算這個 MyriagentHub 的）`
    + (stepUpMode ? '；近期驗證 1 分鐘（--stepup）' : ''), { pid: hubProc.pid, dataDir, healthz: health, importedKeys, hubArgs });

  browser = await chromium.launch({ channel: 'chrome', headless: !args.headed, args: ['--ignore-certificate-errors'] });
  const context = await newContext(browser, { ignoreHTTPSErrors: true, locale: 'zh-TW', timezoneId: 'Asia/Taipei', viewport: { width: 1440, height: 900 } });
  // CSP 違規收集器：CDP 注入（不受頁面的 CSP 影響、比頁面的任何腳本都早）；每筆連同 location.href 經 exposeBinding 送回 Node 端。
  // window.__csp 只留給正向對照（同一個頁面內前後比較）
  await context.exposeBinding('__pgCsp', (source, v) => {
    cspLog.push({ page: pageLabels.get(source.page) ?? '?', ...v });
  });
  await context.addInitScript(() => {
    window.__csp = [];
    document.addEventListener('securitypolicyviolation', (e) => {
      const v = { directive: e.violatedDirective, blocked: e.blockedURI, source: e.sourceFile, line: e.lineNumber, href: location.href };
      window.__csp.push(v);
      if (typeof window.__pgCsp === 'function') window.__pgCsp(v);
    });
  });

  // ───── 3. 假裝置 A 開始配對（不自動核准）─────
  const pairA = await startPairing('A');
  step('A 開始配對', pairA.ok, pairA.summary, pairA.data);
  if (!pairA.ok) throw new Error('A 沒有開始配對');

  // ───── 4. 分頁 P（1440×900）：匿名開核准網址 → 表單登入 → 留在核准頁 → 核准 A ─────
  const P = await context.newPage();
  watch(P, 'P');
  await P.goto(pairA.first.verificationUriComplete, { waitUntil: 'load' });
  await waitReady(P);
  const anonymous = await authState(P);
  await P.fill('#login-user', OWNER);
  await P.fill('#login-password', DEFAULT_PASSWORD);
  await P.click('#btn-login');
  let lastPasswordAt = Date.now();
  const pairViewA = await waitFor(async () => { const s = await authState(P); return s.view === 'pair' && s.detail ? s : null; }, 10000);
  const approvedA = pairViewA ? await approveOn(P) : null;
  if (approvedA?.passwordAt) lastPasswordAt = approvedA.passwordAt;
  const doneA = approvedA?.ok ? await finishPairing('A', 60000) : null;
  step('核准 A：匿名開核准網址、表單登入後留在核准頁', anonymous.view === 'login' && anonymous.pairHint && !!pairViewA && pairViewA.path === '/' && pairViewA.hash.startsWith('#pair=')
    && pairViewA.device === NAME_A && pairViewA.fp === pairA.first.deviceFingerprint && !!approvedA?.ok && !!doneA?.ok,
    `匿名時畫面 ${anonymous.view}${anonymous.pairHint ? '、有「登入後核准」提示' : '、沒有配對提示'}；表單登入後${pairViewA ? `留在 ${pairViewA.path}${pairViewA.hash.slice(0, 6)}…、畫面 pair、`
      + `名稱「${pairViewA.device}」、指紋 ${pairViewA.fp}（裝置端 ${pairA.first.deviceFingerprint}）` : ' 10 秒內沒有顯示核准頁的細節'}；`
      + `${approvedA ? `核准：${approvedA.summary}` : '沒有核准'}；${doneA ? doneA.summary : 'A 沒有完成'}`,
    { anonymous, pairView: pairViewA, approve: approvedA, done: doneA?.data ?? null });
  if (!doneA?.ok) throw new Error('A 沒有完成配對');

  // ───── 5. 假裝置 B（XSS 名稱）→ 分頁 P2（390×844、同一個瀏覽器內容）手動輸入配對碼 → 核准 ─────
  const pairB = await startPairing('B');
  step('B 開始配對（XSS 名稱）', pairB.ok, pairB.summary, pairB.data);
  if (!pairB.ok) throw new Error('B 沒有開始配對');
  const P2 = await context.newPage();
  watch(P2, 'P2');
  await P2.setViewportSize({ width: 390, height: 844 });
  await P2.goto(`${base}/#pair`, { waitUntil: 'load' });
  await waitReady(P2);
  const emptyB = await authState(P2);
  const typed = pairB.first.userCode.toLowerCase().replace('-', ' ');
  await P2.fill('#pair-code', typed);
  await P2.click('#btn-pair-lookup');
  const pairViewB = await waitFor(async () => { const s = await authState(P2); return s.view === 'pair' && s.detail ? s : null; }, 10000);
  await sleep(300);
  const xssPair = await P2.evaluate(() => ({ imgs: document.querySelectorAll('#view-pair img, img[src="x"]').length, xss: typeof window.__xss }));
  await shot(P2, 'pair-b-390');
  const overflowB = await overflow(P2);
  const approvedB = pairViewB ? await approveOn(P2) : null;
  if (approvedB?.passwordAt) lastPasswordAt = approvedB.passwordAt;
  const doneB = approvedB?.ok ? await finishPairing('B', 60000) : null;
  step('核准 B：手機寬度手動輸入小寫加空白的配對碼、名稱照字面', emptyB.view === 'pair' && !emptyB.detail && !!pairViewB && pairViewB.device === NAME_B
    && pairViewB.fp === pairB.first.deviceFingerprint && pairViewB.code === pairB.first.userCode && xssPair.imgs === 0 && xssPair.xss === 'undefined' && dialogs.length === 0
    && overflowB.ok && !!approvedB?.ok && !!doneB?.ok,
    `已登入開 /#pair：畫面 ${emptyB.view}、${emptyB.detail ? '竟然有細節' : '等輸入'}；輸入「${typed}」查詢後${pairViewB ? `欄位顯示 ${pairViewB.code}、名稱照字面「${pairViewB.device}」、`
      + `指紋 ${pairViewB.fp}（裝置端 ${pairB.first.deviceFingerprint}）` : ' 10 秒內沒有細節'}；核准頁的 img ${xssPair.imgs} 個、對話框 ${dialogs.length} 個；`
      + `390 寬 ${overflowB.summary}；${approvedB ? `核准：${approvedB.summary}` : '沒有核准'}；${doneB ? doneB.summary : 'B 沒有完成'}`,
    { empty: emptyB, typed, pairView: pairViewB, xss: xssPair, overflow: overflowB, approve: approvedB, done: doneB?.data ?? null, dialogs: dialogs.slice() });
  if (!doneB?.ok) throw new Error('B 沒有完成配對');

  // 裝置金鑰的正向對照：device.json 記的金鑰名稱在金鑰存放區找得到（收尾時用同一個方法確認 unpair 之後不在）
  const keyHits = keyFilesContaining([devices.A.keyName, devices.B.keyName].filter(Boolean));
  const keyControl = [devices.A, devices.B].every((d) => d.keyName && d.keyName.startsWith(DEVICE_KEY_PREFIX) && keyHits[d.keyName]?.length === 1);
  step('裝置金鑰找得到（正向對照）', keyControl, `A ${devices.A.keyName ?? '—'}：${keyHits[devices.A.keyName]?.length ?? 0} 個檔；B ${devices.B.keyName ?? '—'}：${keyHits[devices.B.keyName]?.length ?? 0} 個檔`
    + '（以 UTF-16LE 的金鑰名稱比對檔案內容）', { keyNames: { A: devices.A.keyName, B: devices.B.keyName }, hits: keyHits });

  // ───── 6. 兩台跑劇本（長 wait 撐住在線）─────
  const scriptA = writeScript('A', [
    { op: 'addAccount', alias: 'claude-a', provider: 'Claude', label: CLAUDE_LABEL, email: 'a@example.test', orgId: 'org-e2e', plan: 'max' },
    { op: 'addAccount', alias: 'codex-a', provider: 'Codex', label: 'E2E Codex', email: 'c@example.test', plan: 'pro' },
    { op: 'cli', provider: 'Claude', version: '2.1.0' },
    { op: 'cli', provider: 'Codex', version: '0.50.0' },
    { op: 'poll', account: 'claude-a', windows: { session: 12, weekly: 34 } },
    { op: 'poll', account: 'codex-a', windows: { session: 5, weekly: 21 } },
    { op: 'waitDrained', seconds: 60 },
    // 指派要等伺服器重算（每 15 秒、上下線與目錄變動時）：再查一次，負責的那台才真的上傳共享用量（只記錄，不判定）
    { op: 'wait', ms: 20000 },
    { op: 'poll', account: 'claude-a', windows: { session: 13, weekly: 35 } },
    { op: 'poll', account: 'codex-a', windows: { session: 6, weekly: 22 } },
    { op: 'wait', ms: 600000 },
  ]);
  const scriptB = writeScript('B', [
    { op: 'addAccount', alias: 'claude-b', provider: 'Claude', label: 'E2E Claude（B）', email: 'a@example.test', orgId: 'org-e2e', plan: 'max' },
    { op: 'cli', provider: 'Claude', version: '2.0.9' },
    { op: 'poll', account: 'claude-b', windows: { session: 12, weekly: 34 } },
    { op: 'waitDrained', seconds: 60 },
    { op: 'wait', ms: 20000 },
    { op: 'poll', account: 'claude-b', windows: { session: 13, weekly: 35 } },
    { op: 'wait', ms: 600000 },
  ]);
  devices.A.run = startFake('runA', devices.A.data, ['run', '--script', scriptA, '--dev-trust']);
  devices.B.run = startFake('runB', devices.B.data, ['run', '--script', scriptB, '--dev-trust']);
  const drained = await waitFor(() => ([devices.A.run, devices.B.run].every((f) => /第 \d+ 步 waitDrained/.test(f.stderr)) ? true
    : [devices.A.run, devices.B.run].some((f) => f.done) ? false : null), 90000);
  step('兩台跑劇本（同一個 Claude 身分、A 另有 Codex）', drained === true && !devices.A.run.done && !devices.B.run.done,
    `A、B 的劇本${drained === true ? '都跑到 waitDrained（outbox 清空），現在在長 wait 撐住在線' : drained === false ? '有一台提早結束' : ' 90 秒內沒有跑到 waitDrained'}`
      + `（A ${devices.A.run.done ? `已結束 ${devices.A.run.exit}` : '執行中'}、B ${devices.B.run.done ? `已結束 ${devices.B.run.exit}` : '執行中'}）`,
    { A: scriptSteps(devices.A.run), B: scriptSteps(devices.B.run) });
  if (drained !== true) throw new Error('劇本沒有跑到 waitDrained');

  // ───── 7. 分頁 P 點「開啟網頁版」：矩陣、查用量、各電腦 CLI、電腦頁、XSS 照字面、CSP 正向對照 ─────
  const A = devices.A.id;
  const B = devices.B.id;
  // 操作哪個分頁就先把它帶到前景：背景分頁的 requestAnimationFrame 停住，Playwright 等元素穩定要多等約 4 秒
  await P.bringToFront();
  const t7 = Date.now();
  await Promise.all([P.waitForURL((u) => new URL(u).pathname === '/app/', { timeout: 10000 }), P.click('#pair-done a')]);
  const appP = await waitAppStarted(P);
  let view7 = null;
  const live = await waitFor(async () => {
    view7 = await dashboardState(P);
    return matrixReady(view7, A, B) ? view7 : null;
  }, 30000);
  const liveMs = live ? Date.now() - t7 : null;
  const v7 = live ?? view7;
  const claudeRow = v7?.matrix.rows.find((r) => /Claude/.test(r.prov ?? '')) ?? null;
  const codexRow = v7?.matrix.rows.find((r) => /Codex/.test(r.prov ?? '')) ?? null;
  const claudeId = claudeRow?.id ?? null;
  const codexId = codexRow?.id ?? null;
  const claudeCard = v7?.cards.find((c) => c.id === claudeId) ?? null;
  const dcliIds = (v7?.dcli ?? []).map((d) => d.device).sort();
  // 各電腦 CLI：照劇本的 cli（A：Claude 2.1.0、Codex 0.50.0；B：Claude 2.0.9、沒有 Codex）；假裝置不查最新版，所以不會有「有新版」。
  // 2026-10-22 起（prov1，設計 2026-10-22-grok-antigravity-sync）Antigravity、Grok 也同步：劇本沒裝，列成未安裝
  const dcliOf = (id) => JSON.stringify((v7?.dcli ?? []).find((d) => d.device === id)?.rows ?? null);
  const notInstalled = [['Antigravity', 'notInstalled', '未安裝'], ['Grok', 'notInstalled', '未安裝']];
  const dcliOk = dcliOf(A) === JSON.stringify([['Claude', 'ok', '2.1.0'], ['Codex', 'ok', '0.50.0'], ...notInstalled])
    && dcliOf(B) === JSON.stringify([['Claude', 'ok', '2.0.9'], ['Codex', 'notInstalled', '未安裝'], ...notInstalled]);
  const devA = v7?.devices.find((d) => d.id === A) ?? null;
  const devB = v7?.devices.find((d) => d.id === B) ?? null;
  const xss7 = await P.evaluate(() => ({ imgs: document.querySelectorAll('img[src="x"]').length, xss: typeof window.__xss }));
  const colB = v7?.matrix.cols.find((c) => c.id === B) ?? null;
  step('網頁版：矩陣、每列一台查用量、各電腦 CLI、電腦頁', appP && !!live && claudeCard?.pill === '2 台已登入（2 台在線）' && JSON.stringify(dcliIds) === JSON.stringify([A, B].sort()) && dcliOk
    && devA?.state === '在線' && devB?.state === '在線' && devB?.name === NAME_B && colB?.name === NAME_B && xss7.imgs === 0 && xss7.xss === 'undefined' && dialogs.length === 0,
    `/app/ ${appP ? '連上、收到 init' : '沒有連上'}；${live ? `按「開啟網頁版」後 ${liveMs} ms 內` : '30 秒內沒有'}看到：Claude 列 ${cellsText(claudeRow, A, B)}、Codex 列 ${cellsText(codexRow, A, B)}`
      + `（每列在線格子的「查用量」：Claude ${pollerCount(claudeRow, v7)}、Codex ${pollerCount(codexRow, v7)}）；Claude 卡片「${claudeCard?.pill ?? '—'}」；`
      + `各電腦 CLI ${dcliIds.length} 台（${dcliOk ? 'A：Claude 2.1.0、Codex 0.50.0；B：Claude 2.0.9、Codex 未安裝' : '版本和劇本不符'}）；電腦頁 A ${devA?.state ?? '—'}、B ${devB?.state ?? '—'}、B 的名稱照字面「${devB?.name ?? '—'}」；img[src=x] ${xss7.imgs} 個、對話框 ${dialogs.length} 個`,
    { matrix: v7?.matrix ?? null, cards: v7?.cards ?? null, dcli: v7?.dcli ?? null, devices: v7?.devices ?? null, xss: xss7, dialogs: dialogs.slice() });
  if (!live || !claudeId || !codexId) throw new Error('矩陣沒有到預期的狀態');
  const usage7 = await waitFor(async () => {
    const c = (await dashboardState(P)).cards.find((x) => x.id === claudeId);
    return c && c.rings > 0 ? c : null;
  }, 30000);
  step('共享用量（只記錄）', null, usage7 ? `Claude 卡片 ${usage7.rings} 個圓環、來源「${usage7.source ?? '—'}」` : '30 秒內 Claude 卡片沒有圓環（負責的那台還沒上傳）', { card: usage7 });

  // CSP 正向對照（分頁 P 的 /app/）：插入 inline script 被擋，收集器正好多一筆
  const cspBefore = cspLog.filter((v) => v.page === 'P').length;
  const inlineRan = await P.evaluate(() => {
    const s = document.createElement('script');
    s.textContent = 'window.__inlineRan = true;';
    document.head.append(s);
    return window.__inlineRan === true;
  });
  await P.waitForTimeout(300);
  const cspAfter = cspLog.filter((v) => v.page === 'P');
  step('CSP 正向對照：插入 inline script 被擋', cspBefore === 0 && inlineRan === false && cspAfter.length === 1 && /^script-src/.test(cspAfter[0].directive) && cspAfter[0].blocked === 'inline',
    `分頁 P 對照前違規 ${cspBefore} 筆；inline script ${inlineRan ? '竟然執行了' : '沒有執行'}，之後 ${cspAfter.length} 筆（${cspAfter[0]?.directive ?? '—'}）`, { cspBefore, inlineRan, cspAfter });

  // ───── 8. 分頁 P2（390×844）開 /app/：手機的儀表板不水平捲動 ─────
  await P2.bringToFront();
  await P2.goto(`${base}/app/`, { waitUntil: 'load' });
  const appP2 = await waitAppStarted(P2);
  const phoneLive = await waitFor(async () => (matrixReady(await dashboardState(P2), A, B) ? true : null), 15000);
  await sleep(300);
  const phone = await P2.evaluate(() => {
    const dash = document.getElementById('dashboard');
    const w = window.innerWidth;
    const wide = [...document.querySelectorAll('body *')].filter((el) => {
      if (el.closest('.mx-wrap')) return false;
      const r = el.getBoundingClientRect();
      return r.width > 0 && (r.right > w + 0.5 || r.left < -0.5);
    }).slice(0, 8).map((el) => (el.id ? '#' + el.id : el.tagName.toLowerCase() + '.' + el.className));
    return { docScroll: document.documentElement.scrollWidth, innerWidth: w, dashScroll: dash.scrollWidth, dashClient: dash.clientWidth, wide };
  });
  await shot(P2, 'dashboard-390');
  await P2.evaluate(() => document.querySelector('#dashboard .dash-sec-mx')?.scrollIntoView());
  await shot(P2, 'matrix-390');
  step('手機寬度的儀表板（390×844）', appP2 && !!phoneLive && phone.docScroll <= phone.innerWidth && phone.dashScroll <= phone.dashClient && phone.wide.length === 0,
    `/app/ ${appP2 ? '連上' : '沒有連上'}、矩陣${phoneLive ? '畫好' : '沒畫好'}；文件寬 ${phone.docScroll}／視窗 ${phone.innerWidth}、儀表板 ${phone.dashScroll}／${phone.dashClient}、`
      + `矩陣以外超出畫面的元素 ${phone.wide.length} 個`, phone);

  // ───── 9. 改電腦名稱、改帳戶名稱、Codex 上移（分頁 P）─────
  await P.bringToFront();
  const t9 = marks();
  await P.click(`#dashboard .dev[data-device=${JSON.stringify(A)}] .dev-rename`);
  t9.mark('click');
  await P.waitForSelector('.dlg-dev-rename input.input', { timeout: 3000 });
  await P.fill('.dlg-dev-rename input.input', NAME_A2);
  t9.mark('fill');
  await P.click('.dlg-dev-rename .btn.primary');
  t9.mark('save');
  const renamed = await waitFor(async () => {
    const s = await dashboardState(P);
    const d = s.devices.find((x) => x.id === A);
    const c = s.matrix.cols.find((x) => x.id === A);
    return d && d.name === NAME_A2 && d.signed === `配對時名稱：${NAME_A}` && c && c.name === NAME_A2 ? { device: d, col: c } : null;
  }, 10000);
  t9.mark('pushed');
  step('從網頁改電腦名稱', !!renamed, renamed ? `devices 推回「${renamed.device.name}」、仍標「${renamed.device.signed}」、矩陣欄頭也換了（按儲存後 ${t9.between('save', 'pushed')} ms）`
    : '10 秒內沒有推回新名稱', { renamed, ms: t9.all() });

  const t9b = marks();
  await openCardMenu(P, claudeId);
  t9b.mark('menu');
  await clickMenuItem(P, '重新命名…');
  t9b.mark('item');
  await P.waitForSelector('.dlg input.input', { timeout: 3000 });
  await P.fill('.dlg input.input', CLAUDE_LABEL2);
  t9b.mark('fill');
  await P.click('.dlg .dlg-foot .btn.primary');
  t9b.mark('save');
  const relabeled = await waitFor(async () => {
    const s = await dashboardState(P);
    const c = s.cards.find((x) => x.id === claudeId);
    const r = s.matrix.rows.find((x) => x.id === claudeId);
    return c && c.name === CLAUDE_LABEL2 && r && r.name === CLAUDE_LABEL2 ? c.name : null;
  }, 10000);
  t9b.mark('relabeled');
  const order0 = (await dashboardState(P)).cards.map((c) => c.id);
  await openCardMenu(P, codexId);
  t9b.mark('menu2');
  await clickMenuItem(P, '上移');
  t9b.mark('up');
  const reordered = await waitFor(async () => {
    const o = (await dashboardState(P)).cards.map((c) => c.id);
    return o[0] === codexId ? o : null;
  }, 10000);
  t9b.mark('reordered');
  step('從網頁改帳戶名稱、上移', !!relabeled && JSON.stringify(order0) === JSON.stringify([claudeId, codexId]) && !!reordered && reordered[1] === claudeId,
    `Claude 帳戶${relabeled ? `改名為「${relabeled}」（accounts 推送，卡片與矩陣都換了）` : ' 10 秒內沒有推回新名稱'}；`
      + `順序 ${order0.map(label).join('、')} → Codex 上移 → ${reordered ? reordered.map(label).join('、') : '10 秒內沒有改變'}`,
    { relabeled, order0, reordered, ms: t9b.all() });

  // ───── 10. 撤銷 B（退役）：B 排到最後、矩陣的 B 欄消失、Claude 變 1 台；結束 B 之後它不是 Live ─────
  if (stepUpMode) {
    const waitMs = Math.max(0, lastPasswordAt + RECENT_AUTH_WAIT_MS - Date.now());
    await sleep(waitMs);
    const s = await dashboardState(P);
    const online = [A, B].map((id) => s.devices.find((d) => d.id === id)?.state ?? null);
    step('等近期驗證過期（--stepup）', online.every((x) => x === '在線'), `等了 ${(waitMs / 1000).toFixed(1)} 秒（最後一次輸入密碼的 ${RECENT_AUTH_WAIT_MS / 1000} 秒後）；A ${online[0]}、B ${online[1]}`, { waitMs, online });
  }
  const revokePath = `/api/v1/devices/${encodeURIComponent(B)}/revoke`;
  const revokeOk = P.waitForResponse((r) => new URL(r.url()).pathname === revokePath && r.request().method() === 'POST' && r.status() === 200, { timeout: 30000 }).catch(() => null);
  const t10 = marks();
  await P.click(`#dashboard .dev[data-device=${JSON.stringify(B)}] .dev-revoke`);
  await P.waitForSelector('.dlg-revoke', { timeout: 3000 });
  t10.mark('dialog');
  await P.check('.dlg-revoke input[name="revoke-reason"][value="retired"]');
  await P.click('.dlg-revoke .btn.danger-solid');
  let stepUpDialog = false;
  if (stepUpMode) {
    stepUpDialog = await P.waitForSelector('.dlg-stepup input[type="password"]', { timeout: 10000 }).then(() => true, () => false);
    if (stepUpDialog) {
      await P.fill('.dlg-stepup input[type="password"]', DEFAULT_PASSWORD);
      await P.click('.dlg-stepup .dlg-foot .btn.primary');
      lastPasswordAt = Date.now();
    }
  }
  const revokeResponse = await revokeOk;
  t10.mark('response');
  const revokedAt = Date.now();
  const colGone = await waitFor(async () => {
    const s = await dashboardState(P);
    return s.matrix.cols.length === 1 && s.matrix.cols[0].id === A ? Date.now() - revokedAt : null;
  }, 15000);
  const after10 = await waitFor(async () => {
    const s = await dashboardState(P);
    const last = s.devices.at(-1);
    const c = s.cards.find((x) => x.id === claudeId);
    return last && last.id === B && last.revoked && /^已撤銷・退役/.test(last.state ?? '') && c && c.pill === '1 台已登入（1 台在線）' ? s : null;
  }, 15000);
  t10.mark('pushed');
  const stepUpShownUnexpectedly = !stepUpMode && await P.evaluate(() => !!document.querySelector('.dlg-stepup'));
  const stopB = await stopFake(devices.B.run);
  t10.mark('stopped');
  const statusB = await fakeRetry('statusB', devices.B.data, ['status', '--dev-trust', '--wait', '5'], 60000);
  t10.mark('status');
  step('撤銷 B：排到最後、矩陣的欄消失、Claude 變 1 台、B 不再是 Live', !!revokeResponse && colGone !== null && !!after10 && (stepUpMode ? stepUpDialog : !stepUpShownUnexpectedly)
    && stopB.done && statusB.code === 0 && !!statusB.json && statusB.json.state !== 'Live',
    `撤銷（退役）${revokeResponse ? '200' : '30 秒內沒有 200'}${stepUpMode ? (stepUpDialog ? '，先跳密碼框、輸入預設密碼後重送' : '，竟然沒有跳密碼框') : ''}；`
      + `${colGone !== null ? `回應後 ${colGone} ms 矩陣的 B 欄消失` : '15 秒內 B 欄沒有消失'}；電腦頁${after10 ? ` B 排最後「${after10.devices.at(-1).state}」、Claude「1 台已登入（1 台在線）」` : '沒有到預期'}；`
      + `結束 B 的劇本程序（${stopB.forced ? '強制' : '已自己結束'}），status 結束碼 ${statusB.code}、state ${statusB.json?.state ?? '—'}（${statusB.json?.stopReason ?? '—'}：${statusB.json?.detail ?? '—'}）`,
    { revokeStatus: revokeResponse ? revokeResponse.status() : null, stepUpDialog, colGoneMs: colGone, ms: t10.all(), devices: after10?.devices ?? null, stopB, statusB: { code: statusB.code, attempts: statusB.attempts, json: statusB.json } });

  // ───── 10b. 移除 B（設計 14.22）：已撤銷的卡片只剩「移除…」、確認框寫明對話一起刪除、200 之後電腦頁不再列 B（近期驗證剛做過，不跳密碼框）─────
  const removePath = `/api/v1/devices/${encodeURIComponent(B)}/remove`;
  const removeOk = P.waitForResponse((r) => new URL(r.url()).pathname === removePath && r.request().method() === 'POST', { timeout: 30000 }).catch(() => null);
  const t10b = marks();
  const removeButtons = await P.evaluate((id) => {
    const card = document.querySelector(`#dashboard .dev[data-device="${CSS.escape(id)}"]`);
    return { revoke: !!card?.querySelector('.dev-revoke'), remove: !!card?.querySelector('.dev-remove'), removeOnA: false };
  }, B);
  removeButtons.removeOnA = await P.evaluate((id) => !!document.querySelector(`#dashboard .dev[data-device="${CSS.escape(id)}"] .dev-remove`), A);
  await P.click(`#dashboard .dev[data-device=${JSON.stringify(B)}] .dev-remove`);
  await P.waitForSelector('.dlg.dlg-confirm', { timeout: 3000 });
  t10b.mark('dialog');
  const removeText = await P.evaluate(() => document.querySelector('.dlg.dlg-confirm').textContent);
  await P.click('.dlg-confirm .btn.danger-solid');
  const removeResponse = await removeOk;
  t10b.mark('response');
  const removeBody = removeResponse ? await removeResponse.json().catch(() => null) : null;
  const removeStepUp = await P.evaluate(() => !!document.querySelector('.dlg-stepup'));
  const after10b = await waitFor(async () => {
    const s = await dashboardState(P);
    return !s.devices.some((d) => d.id === B) && s.devices.some((d) => d.id === A) && s.matrix.cols.length === 1 ? s : null;
  }, 15000);
  t10b.mark('pushed');
  step('移除 B：只有已撤銷的有「移除…」、確認框寫明對話一起刪除、電腦頁不再列它',
    !removeButtons.revoke && removeButtons.remove && !removeButtons.removeOnA && removeText.includes(REMOVE_TEXT) && removeResponse?.status() === 200
      && removeBody?.ok === true && Number.isInteger(removeBody?.conversations) && !removeStepUp && !!after10b,
    `B 的卡片${removeButtons.remove ? '有' : '沒有'}「移除…」、${removeButtons.revoke ? '還有' : '沒有'}「撤銷…」，A ${removeButtons.removeOnA ? '竟然有' : '沒有'}「移除…」；`
      + `確認框${removeText.includes(REMOVE_TEXT) ? '寫明對話一起刪除' : '沒有寫明對話一起刪除'}；移除 ${removeResponse ? removeResponse.status() : '沒有回應'}（刪了 ${removeBody?.conversations ?? '—'} 個對話）`
      + `${removeStepUp ? '，竟然跳了密碼框' : ''}；${after10b ? `推送後電腦頁剩 ${after10b.devices.length} 台（沒有 B）` : '15 秒內 B 沒有從電腦頁消失'}`,
    { buttons: removeButtons, removeText, removeStatus: removeResponse ? removeResponse.status() : null, removeBody, removeStepUp, devices: after10b?.devices ?? null, ms: t10b.all() });

  // ───── 11. 從目錄刪除 Codex：accounts 不再有它，3 秒後仍沒有 ─────
  const deletePath = `/api/v1/accounts/${encodeURIComponent(codexId)}/delete`;
  const deleteOk = P.waitForResponse((r) => new URL(r.url()).pathname === deletePath && r.request().method() === 'POST', { timeout: 30000 }).catch(() => null);
  const t11 = marks();
  await openCardMenu(P, codexId);
  t11.mark('menu');
  await clickMenuItem(P, '從目錄刪除…');
  await P.waitForSelector('.dlg.dlg-confirm', { timeout: 3000 });
  t11.mark('dialog');
  const confirmText = await P.evaluate(() => document.querySelector('.dlg.dlg-confirm').textContent);
  await P.click('.dlg-confirm .btn.danger-solid');
  const deleteResponse = await deleteOk;
  t11.mark('response');
  const gone = await waitFor(async () => {
    const s = await dashboardState(P);
    return !s.cards.some((c) => c.id === codexId) && !s.matrix.rows.some((r) => r.id === codexId) ? true : null;
  }, 10000);
  t11.mark('gone');
  await sleep(3000);
  const s11 = await dashboardState(P);
  const stillGone = !s11.cards.some((c) => c.id === codexId);
  step('從目錄刪除 Codex', confirmText.includes(DELETE_TEXT) && deleteResponse?.status() === 200 && !!gone && stillGone && s11.cards.length === 1,
    `確認框${confirmText.includes(DELETE_TEXT) ? '寫明「之後再回報同一個登入身分時會再出現」' : '沒有寫明會再出現'}；刪除 ${deleteResponse ? deleteResponse.status() : '沒有回應'}；`
      + `${gone ? 'accounts 推送後卡片與矩陣列都消失' : '10 秒內沒有消失'}、3 秒後${stillGone ? '仍沒有' : '又出現了'}（剩 ${s11.cards.length} 張卡）`,
    { confirmText, deleteStatus: deleteResponse ? deleteResponse.status() : null, cards: s11.cards, ms: t11.all() });

  // ───── 12. 分頁 Q 開 /app/；P2 回到 / 按「登出」→ P 與 Q 5 秒內被導回 / ─────
  const Q = await context.newPage();
  watch(Q, 'Q');
  await Q.goto(`${base}/app/`, { waitUntil: 'load' });
  const appQ = await waitAppStarted(Q);
  await P2.bringToFront();
  await P2.goto(`${base}/`, { waitUntil: 'load' });
  await waitReady(P2);
  const accountView = await authState(P2);
  const logoutAt = Date.now();
  await P2.click('#btn-logout');
  const loggedOut = await waitFor(async () => ((await authState(P2)).view === 'login' ? true : null), 10000);
  const redirected = {};
  for (const [name, page] of [['P', P], ['Q', Q]]) {
    const at = await waitFor(() => page.evaluate(() => (location.pathname === '/' && !!document.querySelector('meta[name="myriagenthub-page"][content="auth"]') ? true : null)).catch(() => null), 5000);
    redirected[name] = at ? Date.now() - logoutAt : null;
    await waitFor(() => page.evaluate(() => (window.__hubProbe && window.__hubProbe.ready === true ? true : null)).catch(() => null), 10000);
  }
  await sleep(300);
  step('登出：開著 /app/ 的分頁被導回 /', appQ && accountView.view === 'account' && !!loggedOut && redirected.P !== null && redirected.Q !== null,
    `分頁 Q 的 /app/ ${appQ ? '連上' : '沒有連上'}；P2 在帳戶頁按「登出」${loggedOut ? '後回到登入頁' : '後沒有回到登入頁'}；`
      + `P ${redirected.P !== null ? `${redirected.P} ms` : '5 秒內沒有'}、Q ${redirected.Q !== null ? `${redirected.Q} ms` : '5 秒內沒有'}被導到 /`,
    { accountView: accountView.view, redirected });

  // ───── 12.5 排空：admin drain → A 收到 shutdown → 重新啟動的 A 的 Hello 回 draining → admin undrain → A 回到 Live ─────
  // 桌面的 Log 寫進資料根的 logs\yyyy-MM-dd.log、不印在標準錯誤：以記錄裡的字樣判斷（SyncEngine.Session 的 OnServerNotice 與 Fail）。
  // 重新啟動的 A 的劇本最後長 wait 撐住在線（同第 6 步）：之後的步驟要 A 在線，收尾照舊結束它並 unpair。
  // run 的 --timeout 是「等到 Live 才跑劇本」的上限：最壞情況是第 20 秒才看到 draining、undrain 之後還要等一輪 16 秒（±20% 抖動）的退避，給 90 秒
  const SHUTDOWN_LOG = '伺服器通知即將停機';
  // U3 第二步（85202ee）起桌面認得 draining：記「伺服器排空中（draining）：… 之後再重連」（SyncEngine.Releases.cs），不再是「不認得的錯誤」
  const DRAINING_LOG = '伺服器排空中（draining）';
  const t125 = marks();
  const noticeBefore = logCount('A', SHUTDOWN_LOG);
  const drainCmd = await runProcess(hubExe, ['admin', 'drain', '--data', dataDir, '--wait', '15'], 30000);
  t125.mark('drain');
  const noticed = await waitFor(() => (logCount('A', SHUTDOWN_LOG) > noticeBefore ? true : devices.A.run.done ? false : null), 15000);
  t125.mark('notice');
  const stopA = await stopFake(devices.A.run);
  t125.mark('stopped');
  const drainingBefore = logCount('A', DRAINING_LOG);
  const scriptA2 = writeScript('A2', [{ op: 'waitDrained', seconds: 60 }, { op: 'wait', ms: 600000 }]);
  devices.A.run = await startFakeWhenFree('runA2', devices.A.data, ['run', '--script', scriptA2, '--dev-trust', '--timeout', '90']);
  const drainingSeen = await waitFor(() => (logCount('A', DRAINING_LOG) > drainingBefore ? true : devices.A.run.done ? false : null), 20000);
  t125.mark('draining');
  // 排空中交握不了：run 還在等 Live，劇本第 1 步還沒開始
  const heldBack = drainingSeen === true && !devices.A.run.done && scriptSteps(devices.A.run).length === 0;
  const undrainCmd = await runProcess(hubExe, ['admin', 'undrain', '--data', dataDir, '--wait', '15'], 30000);
  t125.mark('undrain');
  // run 先等到 Live 才跑劇本：第 1 步 waitDrained 印出來、而且沒有「還沒連上伺服器」＝回到 Live
  const back = await waitFor(() => (scriptSteps(devices.A.run).find((s) => s.op === 'waitDrained') ?? (devices.A.run.done ? false : null)), 40000);
  t125.mark('live');
  const lateStart = devices.A.run.stderr.includes('還沒連上伺服器');
  step('排空：shutdown、draining、重連', drainCmd.code === 0 && noticed === true && stopA.done && heldBack && undrainCmd.code === 0
    && !!back && back.result?.drained === true && !lateStart && !devices.A.run.done,
    `admin drain 結束碼 ${drainCmd.code}；A ${noticed === true ? `${t125.between('drain', 'notice')} ms 內記下「${SHUTDOWN_LOG}」` : `15 秒內沒有記下「${SHUTDOWN_LOG}」`}；`
      + `結束 A（${stopA.forced ? '強制' : '已自己結束'}）、以同一個資料根重新啟動：`
      + `${drainingSeen === true ? `${t125.between('stopped', 'draining')} ms 內記下「${DRAINING_LOG}」${heldBack ? '，劇本還在等連線' : '，但劇本已經開始或程序已結束'}` : drainingSeen === false ? `還沒記下 draining 就結束了（結束碼 ${devices.A.run.exit}）` : '20 秒內沒有記下 draining'}；`
      + `admin undrain 結束碼 ${undrainCmd.code}；`
      + `${back ? `${t125.between('undrain', 'live')} ms 內回到 Live（outbox ${back.result?.drained === true ? '清空' : '沒清空'}）${devices.A.run.done ? '，但程序結束了' : '，留在線上'}` : back === false ? `A 結束了（結束碼 ${devices.A.run.exit}）` : '40 秒內沒有回到 Live'}`
      + `${lateStart ? '；劇本開始時還沒連上伺服器' : ''}`,
    { drain: { code: drainCmd.code, stdout: drainCmd.stdout.trim() }, undrain: { code: undrainCmd.code, stdout: undrainCmd.stdout.trim() }, stopA, ms: t125.all(),
      steps: scriptSteps(devices.A.run), stderr: back ? undefined : devices.A.run.stderr.slice(-1500) });

  // ───── 13. 稽核：再登入 → admin verify-audit → GET /api/v1/audit，筆數剛好 ─────
  const relogin = await probe(P2, 'login', OWNER, DEFAULT_PASSWORD);
  const verify = await runProcess(hubExe, ['admin', 'verify-audit', '--data', dataDir, '--wait', '15'], 30000);
  const audit = await pageFetch(P2, 'GET', '/api/v1/audit?limit=200');
  const entries = audit.json?.entries ?? [];
  const countOf = (action, outcome) => entries.filter((e) => e.action === action && e.outcome === outcome).length;
  const expectedAudit = [
    ['owner.created', 'ok', 1],
    ['auth.login', 'ok', 2],
    ['auth.login', 'denied', 0],
    ['pair.started', 'ok', 2],
    ['pair.approved', 'ok', 2],
    ['pair.completed', 'ok', 2],
    ['pair.denied', 'ok', 0],
    ['device.renamed', 'ok', 1],
    ['account.updated', 'ok', 1],
    ['account.reordered', 'ok', 1],
    ['device.revoked', 'ok', 1],
    ['device.removed', 'ok', 1],
    ['account.deleted', 'ok', 1],
    ['auth.logout', 'ok', 1],
    ['auth.step_up', 'ok', stepUpMode ? 3 : 2], // 核准 A、B 各一次（核准一律先再輸入密碼）；--stepup 的撤銷再一次
    ['auth.step_up', 'denied', 0],
    ['prefs.set', 'ok', 0],
    ['admin.command', 'ok', 3], // 排空那一步的 drain、undrain，與這一步的 verify-audit
    ['server.drain', 'ok', 0], // 管理指令的排空記成 admin.command；server.drain 只在部署的排空逾時自動解除時寫
  ];
  const auditCounts = expectedAudit.map(([a, o, n]) => ({ action: a, outcome: o, expected: n, actual: countOf(a, o) }));
  const recorded = ['device.hello', 'account.linked'].map((a) => ({ action: a, count: entries.filter((e) => e.action === a).length }));
  const denied = entries.filter((e) => e.outcome !== 'ok');
  const chain = audit.json?.chain ?? null;
  const auditLeaks = leaks(expandJson(audit.text ?? ''));
  step('稽核筆數', relogin.ok !== false && !relogin.error && verify.code === 0 && audit.status === 200 && auditCounts.every((x) => x.actual === x.expected) && denied.length === 0
    && chain?.ok === true && auditLeaks.length === 0,
    `再登入${relogin.error ? '失敗：' + relogin.message : '成功'}；admin verify-audit 結束碼 ${verify.code}；/api/v1/audit ${audit.status}，${entries.length} 筆：`
      + `${auditCounts.map((x) => `${x.action}${x.outcome === 'ok' ? '' : '（' + x.outcome + '）'}×${x.actual}${x.actual === x.expected ? '' : `（預期 ${x.expected}）`}`).join('、')}；`
      + `只記錄：${recorded.map((x) => `${x.action}×${x.count}`).join('、')}；不是 ok 的 ${denied.length} 筆；雜湊鏈${chain?.ok === true ? '正常' : '不一致或沒驗證'}；`
      + `回應裡${auditLeaks.length === 0 ? '沒有' : '出現'}密碼`,
    { verify: { code: verify.code, stdout: verify.stdout.trim() }, status: audit.status, counts: auditCounts, recorded, chain, leaks: auditLeaks,
      entries: entries.map((e) => ({ seq: e.seq, actorType: e.actorType, action: e.action, target: e.target, outcome: e.outcome, detail: e.detail })) });

  // ───── 13.5 版本頁（/#releases，U2）：還沒有收到任何版本、電腦表只有 A（B 已撤銷、移除）、A 的 app 版本同 Hello 回報的、沒有 CSP 違規 ─────
  // A 的 Hello 回報的版本取第 13 步讀到的稽核（由新到舊）裡 A 最新的一筆 device.hello（ok）的 detail.appVersion；不斷言在線（前面的步驟可能重新啟動過 A）
  const R = await context.newPage();
  watch(R, 'R');
  await R.goto(`${base}/#releases`, { waitUntil: 'load' });
  await waitReady(R);
  const releasesData = await waitFor(() => R.evaluate(() => (window.__hubProbe && window.__hubProbe.view === 'releases' ? window.__hubProbe.releases() : null)).catch(() => null), 10000);
  const releasesView = await R.evaluate(() => {
    const el = (id) => document.getElementById(id);
    const shown = (id) => !!el(id) && !el(id).hidden;
    const t = (row, sel) => { const x = row.querySelector(sel); return x ? x.textContent : null; };
    return {
      view: window.__hubProbe ? window.__hubProbe.view : null, path: location.pathname, hash: location.hash,
      empty: shown('releases-empty') ? el('releases-empty').textContent : null, releaseTable: shown('releases-table'),
      releaseRows: document.querySelectorAll('#releases-rows tr').length, deviceTable: shown('devices-table'),
      devices: [...document.querySelectorAll('#devices-rows tr')].map((tr) => ({
        id: tr.dataset.device ?? null, name: t(tr, '.rd-name'), version: t(tr, '.rd-version'), conn: t(tr, '.rd-conn'), update: t(tr, '.rd-update'),
      })),
    };
  });
  await shot(R, 'releases');
  const helloA = entries.find((e) => e.action === 'device.hello' && e.outcome === 'ok' && e.target === A);
  let helloVersion = null;
  try {
    helloVersion = helloA?.detail ? JSON.parse(helloA.detail).appVersion ?? null : null;
  } catch {
    helloVersion = null;
  }
  const rowA = releasesView.devices.find((d) => d.id === A) ?? null;
  const dataA = releasesData?.devices?.find((d) => d.deviceId === A) ?? null;
  const releaseCsp = cspLog.filter((v) => v.page === 'R');
  step('版本頁', releasesView.view === 'releases' && releasesView.path === '/' && releasesView.empty === '還沒有收到任何版本' && !releasesView.releaseTable
    && releasesView.releaseRows === 0 && Array.isArray(releasesData?.releases) && releasesData.releases.length === 0 && releasesData.latest === null
    && releasesView.deviceTable && releasesView.devices.length === 1 && !!rowA && !releasesView.devices.some((d) => d.id === B)
    && typeof helloVersion === 'string' && rowA.version === helloVersion && dataA?.appVersion === helloVersion && releaseCsp.length === 0,
    `畫面 ${releasesView.view}（${releasesView.path}${releasesView.hash}）；版本表${releasesView.empty ? `顯示「${releasesView.empty}」` : '沒有顯示空的說明'}、${releasesView.releaseRows} 列、`
      + `latest ${releasesData ? JSON.stringify(releasesData.latest) : '（沒有資料）'}；電腦表 ${releasesView.devices.length} 台`
      + `${rowA ? `：A「${rowA.name}」app 版本 ${rowA.version}（Hello 回報 ${helloVersion ?? '找不到'}）、${rowA.conn}` : '，沒有 A'}`
      + `${releasesView.devices.some((d) => d.id === B) ? '、列出了已撤銷的 B' : '、沒有 B（已撤銷）'}；CSP 違規 ${releaseCsp.length} 筆`,
    { view: releasesView, data: releasesData, helloVersion, csp: releaseCsp });

  // ───── 14. 主控台只有預期的錯誤、≥400 的回應次數剛好、CSP 違規只有正向對照、沒有對話框 ─────
  const errors = consoleErrors();
  const unexpected = errors.filter((m) => !expectedConsole(m));
  const http = compareHttp(B);
  const inlineViolations = cspLog.filter((v) => v.page === 'P' && /^script-src/.test(v.directive) && v.blocked === 'inline');
  const otherViolations = cspLog.filter((v) => !inlineViolations.includes(v));
  step('主控台只有預期的錯誤', unexpected.length === 0 && http.ok && inlineViolations.length === 1 && otherViolations.length === 0 && dialogs.length === 0,
    `各分頁的主控台錯誤 ${errors.length} 筆，排除預期的之後 ${unexpected.length} 筆；≥400 的回應 ${http.summary}；`
      + `CSP 違規 ${cspLog.length} 筆（正向對照 ${inlineViolations.length} 筆、其他 ${otherViolations.length} 筆）；對話框 ${dialogs.length} 個`,
    { console: errors.map((m) => ({ ...m, expected: expectedConsole(m) })), http, csp: cspLog, dialogs });
} catch (e) {
  step('例外', false, String(e?.message ?? e), { stack: String(e?.stack ?? e) });
} finally {
  if (browser) {
    const closed = await Promise.race([browser.close().then(() => true, () => false), sleep(5000).then(() => false)]);
    if (!closed) step('關閉瀏覽器', false, 'browser.close() 5 秒內沒有返回');
  }
  await cleanupDevices();
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
  // 寫出去的檔案本身再確認一次（最後一次 save 只多了完成時間與判定）
  const onDisk = leaks(expandJson(fs.readFileSync(outFile, 'utf8')));
  if (onDisk.length > 0) {
    result.ok = false;
    console.log(`[失敗] 結果檔寫出後出現密碼（${onDisk.join('、')}）`);
  }
  console.log(result.ok ? `全部通過，結果檔：${outFile}` : `有檢查失敗，結果檔：${outFile}`);
  process.exit(result.ok ? 0 : 1);
}

// ───── 假裝置 ─────

/** 背景啟動 FakeDevice（不經殼層；環境變數剝掉代理與同步相關的）：stdout／stderr 收在記憶體並寫進工作資料夾 */
function startFake(label, data, argv) {
  const name = `${label}-${++fakeSeq}`;
  const outLog = path.join(work, `${name}.stdout.log`);
  const errLog = path.join(work, `${name}.stderr.log`);
  const proc = spawn(fakeExe, ['--data', data, ...argv], { cwd: path.dirname(fakeExe), env: childEnv(), stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true });
  const f = { label: name, proc, pid: proc.pid, argv, stdout: '', stderr: '', done: false, exit: null, signal: null, startedAt: Date.now() };
  f.exited = new Promise((resolve) => {
    proc.on('exit', (code, signal) => {
      f.done = true;
      f.exit = code;
      f.signal = signal;
      resolve(code);
    });
    proc.on('error', (e) => {
      f.stderr += `\n啟動失敗：${e?.message ?? e}`;
      f.done = true;
      resolve(null);
    });
  });
  proc.stdout.setEncoding('utf8');
  proc.stdout.on('data', (d) => { f.stdout += d; fs.appendFileSync(outLog, d); });
  proc.stderr.setEncoding('utf8');
  proc.stderr.on('data', (d) => { f.stderr += d; fs.appendFileSync(errLog, d); });
  fakes.push(f);
  return f;
}

function jsonLines(f) {
  return f.stdout.split(/\r?\n/).map((l) => l.trim()).filter((l) => l.startsWith('{')).map(parseJson).filter(Boolean);
}

async function waitExit(f, ms) {
  if (f.done) return true;
  return Promise.race([f.exited.then(() => true), sleep(ms).then(() => false)]);
}

/** 結束一個還在跑的假裝置並等它真的結束（kill 是 TerminateProcess；10 秒沒結束再 taskkill /T /F） */
async function stopFake(f) {
  if (!f) return { done: true, already: true };
  if (f.done) return { done: true, already: true, forced: false, code: f.exit };
  f.proc.kill();
  let done = await waitExit(f, 10000);
  if (!done) {
    await runProcess('taskkill', ['/T', '/F', '/PID', String(f.pid)], 15000);
    done = await waitExit(f, 10000);
  }
  return { done, forced: true, code: f.exit, signal: f.signal };
}

/** 跑一次假裝置的指令並等它結束（逾時就結束它）：{code, json（最後一行 JSON）, stderr 結尾} */
async function fakeOnce(label, data, argv, timeoutMs) {
  const f = startFake(label, data, argv);
  if (!(await waitExit(f, timeoutMs))) await stopFake(f);
  return { code: f.exit, done: f.done, json: jsonLines(f).at(-1) ?? null, stderr: f.stderr.slice(-1500) };
}

/** 結束碼 3（資料根的實例鎖還沒放開：剛結束的程序）時等 1 秒重試，最多 10 秒 */
async function fakeRetry(label, data, argv, timeoutMs) {
  const started = Date.now();
  let attempts = 0;
  let r;
  while (true) {
    attempts++;
    r = await fakeOnce(label, data, argv, timeoutMs);
    if (r.code !== 3 || Date.now() - started >= 10000) break;
    await sleep(1000);
  }
  return { ...r, attempts };
}

/** pair --no-approve：等第一行 JSON（配對碼、指紋、核准網址），斷言網址是伺服器的 PublicOrigin＋/#pair[=碼] */
async function startPairing(which) {
  const d = devices[which];
  const f = startFake(`pair${which}`, d.data, ['pair', '--server', base, '--name', d.name, '--dev-trust', '--no-approve', '--timeout', '120']);
  d.pair = f;
  const first = await waitFor(() => (jsonLines(f).find((j) => typeof j.userCode === 'string') ?? (f.done ? false : null)), 30000);
  if (!first) {
    return { ok: false, summary: `30 秒內沒有拿到配對碼（${f.done ? `已結束 ${f.exit}` : '還在跑'}）：${f.stderr.slice(-300)}`, data: { stderr: f.stderr.slice(-1500) } };
  }
  const ok = first.verificationUri === `${base}/#pair` && first.verificationUriComplete === `${base}/#pair=${first.userCode}` && /^[A-Z0-9]{4}-[A-Z0-9]{4}$/.test(first.userCode)
    && typeof first.deviceFingerprint === 'string' && first.deviceFingerprint.length > 0;
  return {
    ok, first,
    summary: `「${d.name}」配對碼 ${first.userCode}、裝置指紋 ${first.deviceFingerprint}；核准網址 ${first.verificationUri}、${first.verificationUriComplete}${ok ? '' : '（和伺服器的來源不符）'}`,
    data: { first },
  };
}

/** 核准之後等 pair 程序結束（結束碼 0、最後一行 state:Live），記下 deviceId 與 device.json 的金鑰名稱 */
async function finishPairing(which, timeoutMs) {
  const d = devices[which];
  const f = d.pair;
  const exited = await waitExit(f, timeoutMs);
  const last = jsonLines(f).filter((j) => j.command === 'pair').at(-1) ?? null;
  d.id = last?.deviceId ?? null;
  d.keyName = readKeyName(d.data);
  const ok = exited && f.exit === 0 && last?.state === 'Live' && !!d.id && last?.drained === true;
  return {
    ok,
    summary: `${which} ${exited ? `結束碼 ${f.exit}` : `${timeoutMs / 1000} 秒內沒有結束`}、state ${last?.state ?? '—'}、deviceId ${d.id ?? '—'}、outbox ${last?.drained === true ? '清空' : '沒清空'}`,
    data: { exit: f.exit, result: last, keyName: d.keyName, stderr: ok ? undefined : f.stderr.slice(-1500) },
  };
}

function readKeyName(data) {
  for (const file of ['device.json', 'pending-key.json']) {
    const j = parseJson(readText(path.join(data, 'sync', file)));
    if (j && typeof j.keyName === 'string') return j.keyName;
  }
  return null;
}

function writeScript(which, steps) {
  const file = path.join(work, `script-${which}.json`);
  fs.writeFileSync(file, JSON.stringify({ steps }, null, 2));
  return file;
}

/** 劇本每一步的結果（假裝置印在標準錯誤：「第 N 步 op：{…}」） */
function scriptSteps(f) {
  return f.stderr.split(/\r?\n/).map((l) => /^第 (\d+) 步 (\w+)：(.*)$/.exec(l.trim())).filter(Boolean).map((m) => ({ step: Number(m[1]), op: m[2], result: parseJson(m[3]) }));
}

/**
 * 背景啟動 FakeDevice；剛結束的程序還沒放開資料根的實例鎖（結束碼 3）時每秒重新啟動、最多 10 秒。
 * 拿不到實例鎖時約 1～2 秒內以結束碼 3 結束（先等前一個實例回應 1 秒）：5 秒內沒結束就是拿到了。回傳還在跑的（或結束碼不是 3 的）那一個
 */
async function startFakeWhenFree(label, data, argv) {
  const started = Date.now();
  while (true) {
    const f = startFake(label, data, argv);
    if (!(await waitExit(f, 5000)) || f.exit !== 3 || Date.now() - started >= 10000) return f;
    await sleep(1000);
  }
}

/** 假裝置的桌面記錄（資料根的 logs\*.log；Log 在背景執行緒寫檔，不印在標準錯誤）裡某個字樣出現的次數 */
function logCount(which, text) {
  return listFiles(path.join(devices[which].data, 'logs')).filter((f) => f.endsWith('.log')).map(readText).join('\n').split(text).length - 1;
}

/** 收尾（不論成敗）：結束還在跑的假裝置、兩台 unpair（只剩 pending-key.json 時跑一次 status 讓配對清理刪掉） */
async function cleanupDevices() {
  const stopped = [];
  for (const f of fakes) {
    if (!f.done) stopped.push({ label: f.label, ...(await stopFake(f)) });
  }
  const unpaired = {};
  let ok = stopped.every((s) => s.done);
  for (const [which, d] of Object.entries(devices)) {
    const sync = path.join(d.data, 'sync');
    // 中途失敗（還沒讀到金鑰名稱）時在這裡讀：收尾之後照樣檢查這把金鑰不在
    d.keyName ??= readKeyName(d.data);
    if (fs.existsSync(path.join(sync, 'device.json'))) {
      const r = await fakeRetry(`unpair${which}`, d.data, ['unpair'], 60000);
      unpaired[which] = { command: 'unpair', code: r.code, attempts: r.attempts, json: r.json, stderr: r.code === 0 ? undefined : r.stderr };
      ok = ok && r.code === 0;
    } else if (fs.existsSync(path.join(sync, 'pending-key.json'))) {
      const r = await fakeRetry(`status${which}`, d.data, ['status', '--dev-trust', '--wait', '1'], 60000);
      unpaired[which] = { command: 'status', code: r.code, attempts: r.attempts };
    }
    const pendingLeft = fs.existsSync(path.join(sync, 'pending-key.json'));
    const deviceLeft = fs.existsSync(path.join(sync, 'device.json'));
    if (unpaired[which]) Object.assign(unpaired[which], { pendingLeft, deviceLeft });
    ok = ok && !pendingLeft && !deviceLeft;
  }
  if (fakes.length > 0) {
    step('收尾：結束假裝置、解除配對', ok, `結束還在跑的 ${stopped.length} 個（${stopped.map((s) => `${s.label}${s.done ? '' : '沒結束'}`).join('、') || '—'}）；`
      + Object.entries(unpaired).map(([w, u]) => `${w} ${u.command} 結束碼 ${u.code}${u.attempts > 1 ? `（試了 ${u.attempts} 次）` : ''}${u.deviceLeft || u.pendingLeft ? '、配對資料還在' : ''}`).join('；'),
    { stopped, unpaired });
  }
}

// ───── 伺服器與金鑰檔 ─────

async function stopHub() {
  if (hubExit !== null) {
    step('停止 MyriagentHub', false, `伺服器提早結束（結束碼 ${hubExit}）`);
    return;
  }
  const started = Date.now();
  const admin = await runProcess(hubExe, ['admin', 'stop', '--data', dataDir, '--wait', '15'], 30000);
  while (hubExit === null && Date.now() - started < 30000) await sleep(200);
  if (hubExit === null) {
    hubProc.kill();
    step('停止 MyriagentHub', false, 'admin stop 後 30 秒沒有結束，已強制結束（file 模式會留下孤兒金鑰檔）', { admin: admin.stdout });
    return;
  }
  step('停止 MyriagentHub', admin.code === 0 && hubExit === 0, `admin stop 結束碼 ${admin.code}，伺服器 ${Date.now() - started} ms 後正常結束（結束碼 ${hubExit}）`);
}

// 這個 MyriagentHub 匯入的（啟動期間新增的）與這次兩台裝置的金鑰（device.json 記的名稱）停止後都要不在；
// 其他之後才出現、內容含裝置金鑰前綴的只記成觀察（同一個使用者的其他程序，例如別的 worktree 的測試）
function checkKeys() {
  if (!keysBefore || !keysStarted) return;
  const now = listKeys();
  const mine = [...keysStarted].filter((k) => !keysBefore.has(k));
  const left = mine.filter((k) => now.includes(k));
  const names = [devices.A.keyName, devices.B.keyName].filter(Boolean);
  const hits = keyFilesContaining(names);
  const deviceLeft = names.filter((n) => hits[n].length > 0);
  step('主機副作用（孤兒金鑰檔）', left.length === 0 && deviceLeft.length === 0,
    `這個 MyriagentHub 啟動期間新增的金鑰檔 ${mine.length} 個，停止後還在 ${left.length} 個；這次兩台的裝置金鑰 ${names.length} 把，解除配對後還在 ${deviceLeft.length} 把（${keysDir}）`,
    { mine, left, deviceKeys: names, deviceLeft: Object.fromEntries(deviceLeft.map((n) => [n, hits[n]])) });
  const prefix = Buffer.from(DEVICE_KEY_PREFIX, 'utf16le');
  const others = now.filter((k) => !keysBefore.has(k) && !keysStarted.has(k)).filter((k) => {
    try { return fs.readFileSync(path.join(keysDir, k)).includes(prefix); } catch { return false; }
  });
  if (others.length > 0) {
    step('其他程序的裝置金鑰檔', null, `執行期間出現、現在還在、含 ${DEVICE_KEY_PREFIX} 但不是這次兩台的金鑰檔 ${others.length} 個：只記錄、不算失敗`, { others });
  }
}

/** 金鑰存放區裡內容含這些金鑰名稱（UTF-16LE）的檔：{名稱: [檔名…]} */
function keyFilesContaining(names) {
  const needles = names.map((n) => [n, Buffer.from(n, 'utf16le')]);
  const hits = Object.fromEntries(names.map((n) => [n, []]));
  for (const k of listKeys()) {
    let buf;
    try { buf = fs.readFileSync(path.join(keysDir, k)); } catch { continue; }
    for (const [n, b] of needles) if (buf.includes(b)) hits[n].push(k);
  }
  return hits;
}

// ───── 瀏覽器 ─────

function watch(target, label) {
  pageLabels.set(target, label);
  target.on('console', (m) => consoleLog.push({ page: label, type: m.type(), text: m.text(), url: m.location()?.url ?? '' }));
  target.on('pageerror', (e) => consoleLog.push({ page: label, type: 'pageerror', text: String(e?.message ?? e), url: '' }));
  target.on('response', (r) => {
    if (r.status() >= 400) httpErrors.push({ page: label, method: r.request().method(), status: r.status(), url: r.url() });
  });
  // 任何對話框（alert 等）都是 XSS 或非預期的 confirm 的證據：記下來並關掉
  target.on('dialog', (d) => {
    dialogs.push({ page: label, type: d.type(), message: d.message() });
    d.dismiss().catch(() => {});
  });
}

function consoleErrors() {
  return consoleLog.filter((m) => m.type === 'error' || m.type === 'pageerror');
}

// 預期的 ≥400 回應（次數要剛好）：瀏覽器把它們記成主控台的「Failed to load resource」
function expectedHttp(deviceB) {
  const list = [
    { page: 'P', path: '/api/v1/auth/me', status: 401, count: 3, why: '匿名開核准網址時 auth.js 一次；登出後傳輸層重連前一次、導到 / 之後 auth.js 一次' },
    { page: 'P', path: '/api/v1/sessions', status: 401, count: 2, why: '匿名時與登出後被導到 / 時 auth.js 的 renderAccount' },
    { page: 'P2', path: '/api/v1/auth/me', status: 401, count: 1, why: '按「登出」之後 auth.js 的 refresh()' },
    { page: 'P2', path: '/api/v1/sessions', status: 401, count: 1, why: '同上，renderAccount' },
    { page: 'Q', path: '/api/v1/auth/me', status: 401, count: 2, why: '登出後：傳輸層重連前一次、導到 / 之後 auth.js 一次' },
    { page: 'Q', path: '/api/v1/sessions', status: 401, count: 1, why: '導到 / 之後 auth.js 的 renderAccount' },
  ];
  if (stepUpMode && deviceB) {
    list.push({ page: 'P', path: `/api/v1/devices/${encodeURIComponent(deviceB)}/revoke`, status: 403, count: 1, why: '--stepup：近期驗證過期，撤銷先回 step_up_required' });
  }
  return list;
}

// ≥400 的回應和預期清單逐項比對次數
function compareHttp(deviceB) {
  const expected = expectedHttp(deviceB);
  const rows = expected.map((x) => ({ ...x, actual: httpErrors.filter((e) => e.page === x.page && e.status === x.status && new URL(e.url).pathname === x.path).length }));
  const unexpected = httpErrors.filter((e) => !expected.some((x) => e.page === x.page && e.status === x.status && new URL(e.url).pathname === x.path));
  const ok = unexpected.length === 0 && rows.every((r) => r.actual === r.count);
  const summary = rows.map((r) => `${r.page} ${r.path} ${r.status}×${r.actual}${r.actual === r.count ? '' : `（預期 ${r.count}）`}`)
    .concat(unexpected.map((e) => `非預期 ${e.page} ${e.method} ${e.url} ${e.status}`)).join('、');
  return { ok, rows, unexpected, summary };
}

// 預期的主控台錯誤：分頁 P 的 CSP 正向對照（刻意插入的 inline script），與預期清單裡的回應（Failed to load resource）
function expectedConsole(m) {
  if (m.page === 'P' && /Content Security Policy/.test(m.text) && /inline/i.test(m.text)) return 'CSP 正向對照（刻意插入的 inline script）';
  if (/Failed to load resource/.test(m.text) && m.url) {
    let p = null;
    try { p = new URL(m.url).pathname; } catch { p = null; }
    const hit = expectedHttp(devices.B.id).find((x) => x.page === m.page && x.path === p && m.text.includes(`status of ${x.status}`));
    if (hit) return hit.why;
  }
  return null;
}

function waitReady(page) {
  return page.waitForFunction(() => window.__hubProbe && window.__hubProbe.ready === true, null, { timeout: 20000 });
}

/** 登入頁（auth.js）的狀態：畫面、網址、配對提示、核准頁的欄位 */
function authState(page) {
  return page.evaluate(() => {
    const t = (id) => { const el = document.getElementById(id); return el ? el.textContent : null; };
    const shown = (id) => { const el = document.getElementById(id); return !!el && !el.hidden; };
    return {
      view: window.__hubProbe ? window.__hubProbe.view : null, path: location.pathname, hash: location.hash,
      pairHint: shown('login-pair-hint'), detail: shown('pair-detail'), done: shown('pair-done'),
      code: document.getElementById('pair-code') ? document.getElementById('pair-code').value : null,
      device: t('pair-device'), fp: t('pair-fp'), machine: t('pair-machine'), key: t('pair-key'), version: t('pair-version'),
      result: shown('pair-result') ? t('pair-result') : null, tone: document.getElementById('pair-result') ? document.getElementById('pair-result').dataset.tone : null,
      stepUpOpen: !!document.getElementById('dlg-stepup') && document.getElementById('dlg-stepup').open,
    };
  });
}

/**
 * 按「核准」：核准鈕在細節出現（與分頁取得焦點、回到可見）1 秒後、擁有者真的操作過才解除停用，所以先等 1.1 秒、把滑鼠移到鈕上；
 * 按下去一律先跳密碼框（剛登入過也一樣）→ 輸入預設密碼 → 等結果「已核准…」（data-tone ok）與「開啟網頁版」。passwordAt：輸入密碼的時間（--stepup 的等待以它起算）
 */
async function approveOn(page) {
  await page.bringToFront();
  await sleep(1100);
  await page.hover('#btn-pair-approve');
  const armed = await page.getAttribute('#btn-pair-approve', 'aria-disabled');
  const clicked = await page.click('#btn-pair-approve', { timeout: 5000 }).then(() => true, () => false);
  const dialog = clicked && await page.waitForSelector('#dlg-stepup[open]', { timeout: 5000 }).then(() => true, () => false);
  let passwordAt = null;
  if (dialog) {
    await page.fill('#stepup-password', DEFAULT_PASSWORD);
    await page.click('#btn-stepup');
    passwordAt = Date.now();
  }
  const s = dialog ? await waitFor(async () => {
    const x = await authState(page);
    return !x.stepUpOpen && x.result && x.result !== '查詢中…' ? x : null;
  }, 10000) : null;
  const ok = armed === 'false' && dialog && !!s && s.tone === 'ok' && /^已核准/.test(s.result ?? '') && s.done;
  const summary = armed !== 'false' ? `滑鼠移過去之後核准鈕仍停用（aria-disabled=${armed}）`
    : !clicked ? '5 秒內按不下核准鈕' : !dialog ? '按了核准沒有跳密碼框'
      : !s ? '輸入密碼後 10 秒內沒有結果' : `先跳密碼框、輸入預設密碼後「${s.result}」${s.done ? '、有「開啟網頁版」' : ''}`;
  return { ok, passwordAt, summary, state: s };
}

/** /app/ 跑起來：#app.ready、傳輸層 online 而且這條連線收到 init（狀態列隱藏）、沒有 no-host */
async function waitAppStarted(target) {
  const ok = await waitFor(() => target.evaluate(() => {
    const st = window.myriagentHubTransport && window.myriagentHubTransport.status();
    const bar = document.querySelector('.hub-status');
    return document.getElementById('app')?.classList.contains('ready') && st && st.state === 'online' && st.hubInit === true
      && !!bar && bar.hidden && !document.querySelector('.no-host') ? true : null;
  }).catch(() => null), 15000);
  return !!ok;
}

/** 儀表板上看得到的同步內容：矩陣、帳戶卡、各電腦 CLI、電腦頁 */
function dashboardState(page) {
  return page.evaluate(() => {
    const t = (el, sel) => { const x = el.querySelector(sel); return x ? x.textContent : null; };
    const cols = [...document.querySelectorAll('#dashboard .mx thead th.mx-dev')].map((th) => ({ id: th.dataset.device, name: t(th, '.mx-dev-name'), online: th.dataset.online === 'true' }));
    const rows = [...document.querySelectorAll('#dashboard .mx tbody tr')].map((tr) => ({
      id: tr.dataset.account, name: t(tr, '.mx-acct-name'), prov: t(tr, '.mx-acct-prov'),
      cells: [...tr.querySelectorAll('td.mx-cell')].map((td) => ({ device: td.dataset.device, kind: td.dataset.kind, text: t(td, '.mx-text'), poller: !!td.querySelector('.mx-poller') })),
    }));
    const cards = [...document.querySelectorAll('#dashboard article.acard')].map((c) => ({
      id: c.dataset.key, name: t(c, '.acard-name'), pill: t(c, '.pill'), rings: Number((c.querySelector('.rings') || {}).dataset?.count || 0), source: t(c, '.note-src'),
    }));
    const dcli = [...document.querySelectorAll('#dashboard .dcli')].map((d) => ({
      device: d.dataset.device, name: t(d, '.dcli-name'), rows: [...d.querySelectorAll('.dcli-row')].map((r) => [r.dataset.provider, r.dataset.kind, t(r, '.dcli-text')]),
    }));
    const devs = [...document.querySelectorAll('#dashboard .dev[data-device]')].map((d) => ({
      id: d.dataset.device, name: t(d, '.dev-name'), state: t(d, '.dev-state'), signed: t(d, '.dev-signed'), revoked: d.classList.contains('is-revoked'),
    }));
    return { matrix: { cols, rows }, cards, dcli, devices: devs };
  });
}

/** 矩陣到了第 7 步的狀態：兩欄都在線；Claude 列兩格 ready、Codex 列 A ready、B absent；每列在線的格子正好一格「查用量」 */
function matrixReady(s, a, b) {
  if (!s) return false;
  const ids = s.matrix.cols.map((c) => c.id).sort();
  if (JSON.stringify(ids) !== JSON.stringify([a, b].sort()) || !s.matrix.cols.every((c) => c.online) || s.matrix.rows.length !== 2) return false;
  const claude = s.matrix.rows.find((r) => /Claude/.test(r.prov ?? ''));
  const codex = s.matrix.rows.find((r) => /Codex/.test(r.prov ?? ''));
  const kind = (row, dev) => row?.cells.find((c) => c.device === dev)?.kind ?? null;
  return kind(claude, a) === 'ready' && kind(claude, b) === 'ready' && kind(codex, a) === 'ready' && kind(codex, b) === 'absent'
    && pollerCount(claude, s) === 1 && pollerCount(codex, s) === 1;
}

function pollerCount(row, s) {
  if (!row || !s) return null;
  const online = new Set(s.matrix.cols.filter((c) => c.online).map((c) => c.id));
  return row.cells.filter((c) => online.has(c.device) && c.poller).length;
}

function cellsText(row, a, b) {
  if (!row) return '（沒有）';
  const k = (dev) => row.cells.find((c) => c.device === dev)?.kind ?? '—';
  return `A ${k(a)}、B ${k(b)}`;
}

function label(id) {
  return id === null ? '—' : id.slice(0, 10);
}

/** 開某張帳戶卡的「帳戶動作」選單 */
async function openCardMenu(page, key) {
  await page.click(`#dashboard article.acard[data-key=${JSON.stringify(key)}] .acard-more`);
  await page.waitForSelector('.menu', { timeout: 3000 });
}

/** 按選單裡文字正好是 label 的項目（真的滑鼠點擊） */
function clickMenuItem(page, text) {
  return page.locator('.menu .menu-item').filter({ has: page.locator('.mi-label', { hasText: new RegExp('^' + text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '$') }) }).first().click();
}

/** 390 寬是否水平捲動：文件寬不超過視窗、沒有元素的右緣超出 */
function overflow(page) {
  return page.evaluate(() => {
    const w = document.documentElement.clientWidth;
    const wide = [...document.querySelectorAll('body *')].filter((e) => { const r = e.getBoundingClientRect(); return r.width > 0 && r.right > w + 1; })
      .slice(0, 8).map((e) => (e.id ? '#' + e.id : e.tagName.toLowerCase() + '.' + e.className));
    const ok = document.documentElement.scrollWidth <= w && wide.length === 0;
    return { ok, scrollWidth: document.documentElement.scrollWidth, clientWidth: w, wide, summary: `文件寬 ${document.documentElement.scrollWidth}／${w}、超出的元素 ${wide.length} 個` };
  });
}

async function shot(page, name) {
  const file = path.join(work, `${name}.png`);
  try {
    await page.screenshot({ path: file });
    result.screenshots.push(file);
  } catch (e) {
    step('截圖', null, `${name} 沒拍成：${e?.message ?? e}`);
  }
}

// 呼叫頁面上 window.__hubProbe 的流程（以名稱呼叫，不在頁面裡 eval）；例外轉成 { error, message, status, code }
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

// ───── 密碼掃描（照 hub-e2e.mjs）─────

// 結果檔（到目前為止的內容）、伺服器記錄（主控台輸出、資料根 diag\）、假裝置的輸出與記錄檔裡不得出現密碼
function scanForPasswords() {
  const scanned = [];
  const found = {};
  const scan = (name, text) => {
    scanned.push(name);
    for (const l of leaks(text)) (found[l] ??= []).push(name);
  };
  scan('結果檔', expandJson(JSON.stringify(result)));
  scan('hub-console.log', readText(path.join(work, 'hub-console.log')));
  if (dataDir) for (const f of listFiles(path.join(dataDir, 'diag'))) scan(path.relative(work, f), readText(f));
  for (const f of listFiles(work).filter((x) => /\.(stdout|stderr)\.log$/.test(x))) scan(path.relative(work, f), readText(f));
  for (const d of Object.values(devices)) for (const f of listFiles(path.join(d.data, 'logs'))) scan(path.relative(work, f), readText(f));
  const labels = Object.keys(found);
  const selfTest = scannerSelfTest();
  step('結果檔與記錄不含密碼', labels.length === 0 && selfTest.found === selfTest.total,
    `搜尋 ${scanned.length} 個檔案裡 ${Object.keys(PASSWORDS).length} 個用過的密碼：`
      + `${labels.length === 0 ? '都沒有出現' : labels.map((l) => `${l}出現在 ${found[l].join('、')}`).join('；')}（正向對照 ${selfTest.found}／${selfTest.total} 個找得到）`,
    { scanned, found, scannerSelfTest: selfTest });
}

function leaks(text) {
  return Object.values(PASSWORDS).filter((p) => text.includes(p.value)).map((p) => p.label);
}

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

// ───── 工具 ─────

function childEnv() {
  return Object.fromEntries(Object.entries(process.env).filter(([k]) => !STRIP_PREFIXES.some((p) => k.toUpperCase().startsWith(p))));
}

/** 跑一個程序並等它結束（不經殼層、不阻塞事件迴圈）：{code, stdout} */
function runProcess(exe, argv, timeoutMs) {
  return new Promise((resolve) => {
    let stdout = '';
    let settled = false;
    const p = spawn(exe, argv, { env: childEnv(), stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true });
    const timer = setTimeout(() => { if (!settled) { settled = true; p.kill(); resolve({ code: null, stdout, timedOut: true }); } }, timeoutMs);
    p.stdout.setEncoding('utf8');
    p.stdout.on('data', (d) => { stdout += d; });
    p.stderr.on('data', () => {});
    p.on('error', (e) => { if (!settled) { settled = true; clearTimeout(timer); resolve({ code: null, stdout: String(e?.message ?? e) }); } });
    p.on('exit', (code) => { if (!settled) { settled = true; clearTimeout(timer); resolve({ code, stdout }); } });
  });
}

/** 量各段花的時間（寫進結果檔）：mark(名稱) 記下距離開始的毫秒數 */
function marks() {
  const t0 = Date.now();
  const at = {};
  return {
    mark: (name) => { at[name] = Date.now() - t0; },
    between: (a, b) => (at[a] === undefined || at[b] === undefined ? null : at[b] - at[a]),
    all: () => ({ ...at }),
  };
}

// 輪詢到不是 null／undefined 為止（逾時回 null）
async function waitFor(read, timeoutMs) {
  const started = Date.now();
  while (true) {
    const value = await read();
    if (value !== null && value !== undefined) return value;
    if (Date.now() - started >= timeoutMs) return null;
    await sleep(100);
  }
}

// Node 端的 HTTPS 請求（自簽憑證：不驗證；只用來等伺服器起來）
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

function parseArgs(list) {
  const known = new Set(['hub', 'fake-device', 'port', 'work', 'out', 'headed', 'stepup']);
  const flags = new Set(['headed', 'stepup']);
  const out = {};
  for (let i = 0; i < list.length; i++) {
    const a = list[i];
    if (!a.startsWith('--')) throw new Error(`看不懂的參數：${a}`);
    const key = a.slice(2);
    if (key === 'base') throw new Error('沒有 --base：流程會配對、撤銷、刪除帳戶，只對自己啟動的伺服器跑');
    if (!known.has(key)) throw new Error(`看不懂的參數：${a}`);
    if (flags.has(key)) { out[key] = true; continue; }
    if (i + 1 >= list.length) throw new Error(`--${key} 缺少值`);
    out[key] = list[++i];
  }
  return out;
}
