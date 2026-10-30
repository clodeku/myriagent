// Myriagent Hub 瀏覽器遠端操作的端到端（WP4.6 計畫 docs/plans/2026-10-19-wp4.6-web-live.md 步 13；設計 11.3）。
// 本機 MyriagentHub（暫存資料根、自動挑的高埠、Development）＋一台 tools/fake-device 假裝置（不執行任何 CLI），以真的瀏覽器（本機 Chrome 無頭、暫存設定檔）走：
//   配對（自動核准）→ 假裝置劇本：種對話、加帳戶、政策 Control（policy op）、XSS 語料對話（200 筆以上）、兩張等核准的卡片 → 清單的電腦徽章與 XSS 標題照字面
//   → 核准卡片後量串流延遲（S7）→ 送出（commandResult 比 Cmd 的回傳先到也對得上）→ requestDigest 與 stale
//   → Grok 與 Antigravity 的對話（標頭照代理種類、Grok 的核准卡、執行中送出只排隊不插入、Antigravity 沒有卡片；Grok／Antigravity 同步的 P5）→ 政策 View、
//   電腦剛斷線（重新連線中，寬限期縮成 5 秒）與離線都擋住送出、重啟後恢復
//   → 草稿換成正式對話（兩個分頁）→ 兩個分頁的 notify 與未讀 → 剪貼簿（授權、拒絕）→ 上傳、/f/ 預覽、dirsOnly 選資料夾、SEC-23
//   → admin stop 後在同一個資料根重啟（重連、重新 open、結果不明、沒有換版橫幅、草稿重開）→ XSS 語料全部載入 0 執行、主控台只有預期的錯誤
//   → 收尾：結束假裝置、unpair、admin stop，MyriagentHub 與裝置的金鑰檔都不殘留 → 結果檔與記錄不含密碼。
// 結果每一步先寫進結果檔（browser.close() 可能不返回，所以關閉加時限）。
// 與 hub-e2e.mjs、gateway.mjs、pairing.mjs（含其他 worktree 的）以 %TEMP%\myriagent-tools\hub-e2e.lock 序列化（lock.mjs，見 README）。
//
// 用法（先 dotnet build src/MyriagentHub/MyriagentHub.csproj -c Release、dotnet build tools/fake-device -c Release、cd tools/hub-e2e && npm install）：
//   node remote-control.mjs              自己啟動 MyriagentHub 與假裝置，跑完全部收掉（約 45 秒）
//   node remote-control.mjs --hub <MyriagentHub.exe> --fake-device <FakeDevice.exe> --port <埠> --work <資料夾> --out <結果檔> --headed
import { chromium } from 'playwright-core';
import { spawn } from 'node:child_process';
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
const work = path.resolve(args.work ?? path.join(os.tmpdir(), 'myriagent-tools', 'remote-control', stamp));
const outFile = path.resolve(args.out ?? path.join(work, 'result.json'));
const hubExe = path.resolve(args.hub ?? path.join(repo, 'src', 'MyriagentHub', 'bin', 'Release', 'net10.0', 'win-x64', 'MyriagentHub.exe'));
const fakeExe = path.resolve(args['fake-device'] ?? path.join(repo, 'tools', 'fake-device', 'bin', 'Release', 'net10.0-windows', 'win-x64', 'FakeDevice.exe'));

const OWNER = 'admin';
// 伺服器的 OwnerAccount.DefaultPassword（首次啟動以它建立擁有者）；結果檔與主控台只寫名稱
const PASSWORDS = { default: { label: '預設密碼', value: 'changeme2026' } };
const DEFAULT_PASSWORD = PASSWORDS.default.value;
// 假裝置自動核准時讀的環境變數（只給 pair 那一個子程序；名稱不在剝除的前綴裡）
const OWNER_PASSWORD_ENV = 'FD_OWNER_PW';
const XSS_IMG = '<img src=x onerror=alert(1)>';
// 裝置名稱與 XSS 對話的標題都是 XSS 夾具：清單、標頭、閘門說明、提示都要照字面
const DEVICE_NAME = 'E2E-PC ' + XSS_IMG;
const XSS_TITLE = XSS_IMG + ' XSS 語料';
const ACCOUNT_ALIAS = 'e2e-claude';
// Grok（隔離帳戶）與 Antigravity（本機預設帳戶的形狀）：假裝置不執行任何 CLI（Grok／Antigravity 同步的 P5）
const GROK_ALIAS = 'e2e-grok';
const AGY_ALIAS = 'e2e-agy';
const GROK_N = 40;
// 標頭的權限選單（AgentPermissionModes.For；Antigravity 不能在對話中詢問）與送出鈕的說明（composer.js）
const MODES = { Grok: ['Ask', 'AcceptEdits', 'Plan', 'Auto', 'FullAccess'], Antigravity: ['AcceptEdits', 'Plan', 'Auto', 'FullAccess'] };
const QUEUE_TITLE = '加入佇列（這一輪完成後送出）';
const STREAM_N = 40;
const SEED = { count: 3, olderThanDays: 1, itemsPerConversation: 6, seed: 20261021, baseTimeUtc: '2026-10-20T00:00:00Z' };
// 子程序不繼承的環境變數（本 session 的 Claude／agy 工作階段、各代理的設定與金鑰、同步覆寫、ASP.NET Core 環境、GC 設定的 DOTNET_GC* 與 COMPlus_*；
// 同 lib/child-env.mjs 與 measure_sync.py）
const STRIP_PREFIXES = ['CLAUDE', 'ANTHROPIC_', 'OPENAI_', 'ANTIGRAVITY_', 'GROK_', 'XAI_', 'MYRIAGENT_', 'CODEX_', 'ASPNETCORE_', 'DOTNET_ENVIRONMENT', 'DOTNET_GC', 'COMPLUS_'];
const DEVICE_KEY_PREFIX = 'Myriagent.Device.';
const LAYOUT_KEY = 'myriagent.app.layout.v1';
const HUB_PATH = '/hubs/v1/browser';
const RS = String.fromCharCode(0x1e);
// MediaHeaderRules.MediaCsp（逐字比對；改了那邊這裡也要改）
const MEDIA_CSP = "default-src 'none'; img-src 'self'; media-src 'self'; style-src 'unsafe-inline'; sandbox";
const TEXT = {
  view: `在 ${DEVICE_NAME} 的設定中允許遠端操作`,
  offline: `電腦離線：${DEVICE_NAME} 重新上線後才能送出`,
  // 剛斷線的寬限期內（fae8085：伺服器換版時每台都會斷線一下）閘門照樣擋，說明是「重新連線中」
  reconnecting: `電腦重新連線中：${DEVICE_NAME} 連上後才能送出`,
  noConnection: '目前沒有連線，連上之後才能送出',
  unknown: '結果不明，請看對話內容確認',
  build: '伺服器已換版本，重新載入以取得對應的網頁',
  chip: '電腦：' + DEVICE_NAME,
};
const SEC23_MARK = 'SEC23-RAN';
// 「重新連線中」的寬限期：Development 的 MyriagentHub 照 --Hub:Browser:ReconnectGraceMs 在 init 帶給網頁（BrowserTuningRules；正式站一律 60 秒）
const RECONNECT_GRACE_MS = 5000;

fs.mkdirSync(work, { recursive: true });
const result = { tool: 'remote-control', startedAt: new Date().toISOString(), work, hubExe, fakeExe, base: null, steps: [], screenshots: [], measurements: {}, ok: null };
// 金鑰存放區（整個使用者共用）：每次啟動 MyriagentHub 前後各列一次，後減前算那一次的（file 憑證模式匯入 PFX 的金鑰檔）；裝置金鑰以 device.json 記的名稱認
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
let lockTimer = null;
let browser = null;
let context = null;
let dataDir = null;
let base = null;
let port = null;
// 每一次啟動 MyriagentHub：{ n, proc, exit, log, before（啟動前的金鑰檔）, started（/healthz 200 之後）, stopped }
const hubRuns = [];
const consoleLog = [];
const httpErrors = [];
const dialogs = [];
const cspLog = [];
const pageLabels = new Map();
const pageNavs = new Map();
// 瀏覽器的 SignalR 框（page.on('websocket')）：Cmd 呼叫、呼叫的回傳、commandResult 的順序
const frames = [];
let frameSeq = 0;
// 停機期間（admin stop 到所有分頁重新收到 init）：這段時間連不上伺服器的錯誤是預期的
const downtime = { from: null, to: null };
const fakes = [];
let fakeSeq = 0;
const device = { data: path.join(work, 'fd'), id: null, keyName: null, run: null };
const corpus = xssCorpus();
// 主流程在檔案最後才呼叫（下面以 const 宣告的輔助函式與狀態要先初始化）
async function main() {
try {
  // ───── 0. 假裝置的建置不能比原始碼舊（審查 8）─────
  const fresh = fakeFreshness();
  step('FakeDevice.exe 不比原始碼舊', fresh.ok, fresh.ok
    ? `${path.relative(repo, fakeExe)} 修改時間 ${new Date(fresh.exeTime).toISOString()}，不早於 FakeScript.cs 與 Runtime\\*.cs（${fresh.sources} 個檔）`
    : `先跑 V-fd（dotnet build tools/fake-device -c Release -nodeReuse:false -p:UseSharedCompilation=false）：${fresh.why}`, fresh);
  if (!fresh.ok) {
    console.log('先跑 V-fd');
    throw new Error('先跑 V-fd：FakeDevice.exe 不存在或比原始碼舊');
  }

  // ───── 1. 序列化鎖 ─────
  const lock = await acquireLock('remote-control', work);
  if (!lock.ok) throw new Error(`等不到序列化鎖（${lockFile}，${(LOCK_WAIT_MS / 1000).toFixed(0)} 秒）：另一個 hub-e2e／gateway／pairing／remote-control 還在跑，持有者 ${lock.holder ?? '不明'}`);
  lockHeld = true;
  // 鎖檔超過 10 分鐘會被別人當成舊鎖刪掉：這支可能跑比較久，每分鐘更新一次修改時間（只更新自己的）
  lockTimer = setInterval(touchLock, 60000);
  step('取得序列化鎖', true, `${lockFile}（等了 ${lock.waitedMs} ms${lock.staleRemoved ? '，清掉了超過 10 分鐘的舊鎖' : ''}）`, { lockFile, waitedMs: lock.waitedMs, staleRemoved: lock.staleRemoved });

  // ───── 2. 伺服器 ─────
  if (!fs.existsSync(hubExe)) throw new Error(`找不到 ${hubExe}（先 dotnet build src/MyriagentHub/MyriagentHub.csproj -c Release，或用 --hub 指定）`);
  if (!fs.existsSync(path.join(path.dirname(hubExe), 'wwwroot', 'transport-hub.js'))) throw new Error(`${hubExe} 的 wwwroot 沒有 transport-hub.js`);
  port = args.port ? Number(args.port) : await freePort();
  dataDir = path.join(work, 'hub-data');
  base = `https://localhost:${port}`;
  result.base = base;
  const run1 = await startHub();
  step('啟動 MyriagentHub', run1.ok, run1.summary, run1.data);
  if (!run1.ok) throw new Error('MyriagentHub 沒有起來');
  const build = run1.data.healthz.build;

  // ───── 3. 配對假裝置（擁有者自動核准）、跑劇本 ─────
  const pair = await fakeOnce('pair', device.data, ['pair', '--server', base, '--name', DEVICE_NAME, '--dev-trust', '--owner-user', OWNER, '--owner-password-env', OWNER_PASSWORD_ENV,
    '--owner-origin', base, '--timeout', '120'], 120000, { [OWNER_PASSWORD_ENV]: DEFAULT_PASSWORD });
  const paired = pair.json && pair.json.command === 'pair' ? pair.json : null;
  device.id = paired?.deviceId ?? null;
  device.keyName = readKeyName(device.data);
  const keyHits = device.keyName ? keyFilesContaining([device.keyName]) : {};
  const pairOk = pair.code === 0 && paired?.state === 'Live' && paired?.drained === true && !!device.id && !!device.keyName && device.keyName.startsWith(DEVICE_KEY_PREFIX)
    && keyHits[device.keyName]?.length === 1;
  step('配對假裝置（自動核准）', pairOk, `結束碼 ${pair.code}、state ${paired?.state ?? '—'}、deviceId ${device.id ?? '—'}、outbox ${paired?.drained === true ? '清空' : '沒清空'}；`
    + `裝置金鑰 ${device.keyName ?? '—'} 在金鑰存放區 ${keyHits[device.keyName]?.length ?? 0} 個檔（正向對照，收尾時同樣方法確認不在）`,
  { result: paired, keyName: device.keyName, stderr: pairOk ? undefined : pair.stderr });
  if (!pairOk) throw new Error('假裝置沒有配對成功');

  const script1 = writeScript('run1', [
    { op: 'seedConversations', ...SEED, account: ACCOUNT_ALIAS, provider: 'Claude' },
    { op: 'addAccount', alias: ACCOUNT_ALIAS, provider: 'Claude', label: 'E2E Claude', email: 'e2e@example.test', orgId: 'org-e2e', plan: 'max' },
    { op: 'addAccount', alias: GROK_ALIAS, provider: 'Grok', label: 'E2E Grok', email: 'e2e-grok@example.test' },
    { op: 'addAccount', alias: AGY_ALIAS, provider: 'Antigravity', label: 'E2E Antigravity', email: 'e2e-agy@example.test' },
    { op: 'cli', provider: 'Claude', version: '2.1.0' },
    { op: 'cli', provider: 'Grok', version: '1.0.52' },
    { op: 'cli', provider: 'Antigravity', version: '1.4.5' },
    { op: 'policy', level: 'Control', fullAccess: false, notify: true },
    { op: 'chat', conversation: 'xss', account: ACCOUNT_ALIAS, message: XSS_TITLE, seconds: 180,
      script: { steps: corpus.map((c) => ({ text: c.text, segments: 1 })), outcome: 'completed' } },
    { op: 'chat', conversation: 'cards', account: ACCOUNT_ALIAS, message: '卡片與附件', seconds: 60,
      script: { steps: [{ approval: '寫入檔案', kind: 'write', title: 'notes.md' }, { text: '核准之後的回覆', segments: 1 }], outcome: 'completed' } },
    { op: 'chat', conversation: 'stream', account: ACCOUNT_ALIAS, message: '串流延遲量測', seconds: 60,
      script: { steps: [{ approval: '執行指令', kind: 'bash', title: 'npm test' },
        ...Array.from({ length: STREAM_N }, (_, i) => ({ text: `串流第 ${i + 1} 段：整理測試結果`, segments: 1, delayMs: 100 }))], outcome: 'completed' } },
    // Grok：思考、待辦、工具、等核准的卡片，核准之後的長串流（網頁在這段時間送出，只能排隊）
    { op: 'chat', conversation: 'grok', account: GROK_ALIAS, message: 'Grok 的對話', seconds: 60,
      script: { steps: [{ thinking: '先看資料夾裡有什麼', segments: 2 }, { todo: [{ text: '讀設定', status: 'completed' }, { text: '跑測試', status: 'inProgress' }] },
        { tool: '執行指令', kind: 'bash', title: 'dotnet build', output: '建置成功' }, { approval: '執行指令', kind: 'bash', title: 'npm test' },
        { text: Array.from({ length: GROK_N }, (_, i) => `第 ${i + 1} 段。`).join(''), segments: GROK_N, delayMs: 150 }], outcome: 'completed' } },
    // Antigravity：沒有核准、問卷與計畫確認卡（agy 不能在對話中詢問）
    { op: 'chat', conversation: 'agy', account: AGY_ALIAS, message: 'Antigravity 的對話', seconds: 60,
      script: { steps: [{ tool: '執行指令', kind: 'bash', title: 'Get-ChildItem', output: 'a.txt' }, { text: '資料夾裡只有 a.txt', segments: 2 }], outcome: 'completed' } },
    { op: 'waitDrained', seconds: 120 },
    { op: 'wait', ms: 600000 },
  ]);
  device.run = startFake('run1', device.data, ['run', '--script', script1, '--dev-trust']);
  const drained1 = await waitFor(() => (/第 \d+ 步 waitDrained/.test(device.run.stderr) ? true : device.run.done ? false : null), 180000);
  const steps1 = scriptSteps(device.run);
  const at = (op, n = 0) => steps1.filter((s) => s.op === op)[n]?.result ?? null;
  const conv = { xss: at('chat', 0)?.conversationId ?? null, cards: at('chat', 1)?.conversationId ?? null, stream: at('chat', 2)?.conversationId ?? null,
    grok: at('chat', 3)?.conversationId ?? null, agy: at('chat', 4)?.conversationId ?? null };
  const seedResult = at('seedConversations');
  const policy1 = at('policy');
  const script1Ok = drained1 === true && !device.run.done && !!seedResult && seedResult.conversations === SEED.count && policy1?.level === 'Control' && policy1?.fullAccess === false
    && policy1?.notify === true && at('chat', 0)?.outcome === 'Completed' && at('chat', 0)?.items === corpus.length + 1
    && at('chat', 1)?.waiting === true && at('chat', 2)?.waiting === true && at('chat', 3)?.waiting === true && at('chat', 4)?.outcome === 'Completed'
    && Object.values(conv).every(Boolean);
  step('假裝置劇本：種對話、政策 Control、XSS 語料、兩張等核准的卡片、Grok 與 Antigravity 的對話', script1Ok,
    `${drained1 === true ? '跑到 waitDrained（outbox 清空），現在在長 wait 撐住在線' : drained1 === false ? `提早結束（${device.run.exit}）` : '180 秒內沒有跑到 waitDrained'}；`
      + `種 ${seedResult?.conversations ?? '—'} 個舊對話（${seedResult?.items ?? '—'} 則、指紋 ${String(seedResult?.sha256 ?? '—').slice(0, 12)}…）；policy 回讀 ${policy1 ? `${policy1.level}、fullAccess ${policy1.fullAccess}、notify ${policy1.notify}` : '—'}；`
      + `XSS 語料 ${corpus.length} 筆（對話 ${at('chat', 0)?.items ?? '—'} 則、${at('chat', 0)?.outcome ?? '—'}）；卡片對話與串流對話都停在等核准（${at('chat', 1)?.waiting}、${at('chat', 2)?.waiting}）；`
      + `Grok 的對話停在等核准（${at('chat', 3)?.waiting}）、Antigravity 的對話 ${at('chat', 4)?.outcome ?? '—'}`,
    { steps: steps1, conversations: conv, corpus: corpus.length });
  if (!script1Ok) throw new Error('劇本沒有跑到預期的狀態');

  // ───── 4. 瀏覽器：登入 ─────
  browser = await chromium.launch({ channel: 'chrome', headless: !args.headed, args: ['--ignore-certificate-errors'] });
  context = await newContext(browser, { ignoreHTTPSErrors: true, locale: 'zh-TW', timezoneId: 'Asia/Taipei', viewport: { width: 1440, height: 900 } });
  await context.exposeBinding('__pgCsp', (source, v) => { cspLog.push({ page: pageLabels.get(source.page) ?? '?', ...v }); });
  await context.addInitScript(pageInit);
  const L = await context.newPage();
  watch(L, 'L');
  await L.goto(`${base}/`, { waitUntil: 'load' });
  await waitReady(L);
  const login = await probe(L, 'login', OWNER, DEFAULT_PASSWORD);
  step('擁有者登入', !login.error, login.error ? `登入失敗：${login.message}` : '登入頁的 __hubProbe.login（預設密碼）成功', { login: login.error ? login : { ok: true } });
  if (login.error) throw new Error('登入失敗');
  await L.close();

  // ───── 5. 清單：電腦徽章、XSS 標題照字面 ─────
  const P = await openApp('P', layout([]));
  const listP = await waitFor(async () => {
    const l = await latestList(P);
    return l && Object.values(conv).every((id) => l.some((c) => c.id === id)) && l.filter((c) => c.deviceId === device.id).length === SEED.count + 5 ? l : null;
  }, 30000);
  const seeded = (listP ?? []).filter((c) => !Object.values(conv).includes(c.id)).map((c) => c.id);
  const badges = await sidebarRows(P);
  const xssRow = badges.find((b) => b.title === XSS_TITLE) ?? null;
  const xss2 = await xssState(P);
  step('清單：每列有電腦徽章、XSS 標題與電腦名稱照字面', !!P.live && !!listP && badges.length === SEED.count + 5 && badges.every((b) => b.shown && b.name === DEVICE_NAME && b.online === 'true')
    && !!xssRow && xssRow.imgs === 0 && xss2.ok,
  `/app/ ${P.live ? '連上' : '沒有連上'}；清單 ${listP ? listP.length : 0} 筆（舊對話 ${seeded.length}）；側欄 ${badges.length} 列、徽章 ${badges.filter((b) => b.shown).length} 個、名稱照字面 ${badges.filter((b) => b.name === DEVICE_NAME).length} 個、`
    + `在線 ${badges.filter((b) => b.online === 'true').length} 個；XSS 標題列${xssRow ? `「${xssRow.title}」、列裡的 img ${xssRow.imgs} 個` : '找不到'}；${xss2.summary}`,
  { badges, seeded, xss: xss2 });

  // ───── 6. 開串流對話、核准卡片（帶 requestDigest）→ 量串流延遲（S7）─────
  await P.page.bringToFront();
  await clickRow(P, '串流延遲量測');
  const streamLoaded = await waitLoaded(P, conv.stream, 10000);
  const snapS = await snapshotOf(P, conv.stream);
  const cardS = snapS?.items.find((i) => i.kind === 'Approval' && i.status === 'Waiting') ?? null;
  const beforeS = cardS ? await cardState(P, conv.stream, cardS.id) : null;
  const mark3 = await msgCount(P);
  const f3 = frames.length;
  if (cardS) await P.page.click(cardSel(conv.stream, cardS.id) + ' .opt[data-idx="1"]');
  const approveS = await waitFor(() => frames.slice(f3).find((f) => f.page === 'P' && f.kind === 'cmd' && f.cmd === 'approve') ?? null, 5000);
  const resS = approveS ? await waitResult(P, approveS.commandId, 10000) : null;
  const streamed = await waitFor(async () => {
    const items = (await recs(P, mark3)).filter((m) => m.type === 'item' && m.c === conv.stream && m.kind === 'Assistant');
    const first = new Map();
    for (const m of items) if (!first.has(m.id)) first.set(m.id, m);
    return first.size >= STREAM_N ? [...first.values()] : null;
  }, 30000);
  const lat = (streamed ?? []).map((m) => m.t - m.time).sort((a, b) => a - b);
  const pct = (p) => (lat.length ? lat[Math.min(lat.length - 1, Math.ceil(p * lat.length) - 1)] : null);
  const s7 = { samples: lat.length, p50: pct(0.5), p95: pct(0.95), p99: pct(0.99), max: lat.at(-1) ?? null, min: lat[0] ?? null };
  result.measurements.s7 = s7;
  step('核准卡片帶 requestDigest、串流延遲 S7 p95 ≤500 ms', !!streamLoaded && !!cardS && !!beforeS?.enabled && beforeS.waiting && !!approveS && approveS.digest === cardS.digest
    && typeof cardS.digest === 'string' && cardS.digest.length > 0 && resS?.state === 'done' && lat.length === STREAM_N && s7.p95 !== null && s7.p95 <= 500,
  `串流對話${streamLoaded ? '載入' : '沒有載入'}；卡片 ${cardS ? cardS.id : '找不到'}${beforeS ? `（等你回應 ${beforeS.waiting}、按鈕可按 ${beforeS.enabled}）` : ''}；approve 的 requestDigest ${approveS ? (approveS.digest === cardS?.digest ? '等於項目的' : `不同（${approveS.digest}）`) : '沒送'}、`
    + `結果 ${resS ? resS.state : '—'}；收到 ${lat.length}／${STREAM_N} 段（到達時間減項目時間，同一台電腦的時鐘）：p50 ${s7.p50} ms、p95 ${s7.p95} ms、p99 ${s7.p99} ms、最大 ${s7.max} ms（沒有人工延遲，WP4.9 另量）`,
  { card: cardS, before: beforeS, approve: approveS, result: resS, s7, latencies: lat });

  // ───── 7. 送出：一般分頁與 commandResult 比 Cmd 的回傳先到的分頁都對得上 ─────
  await waitFor(async () => ((await latestList(P))?.find((c) => c.id === conv.stream)?.running === false ? true : null), 15000);
  await sleep(500);
  const f4 = frames.length;
  await typeAndEnter(P, conv.stream, '串流做完了，再跑一次');
  const sendP = await waitFor(() => frames.slice(f4).find((f) => f.page === 'P' && f.kind === 'cmd' && f.cmd === 'send') ?? null, 5000);
  const resP = sendP ? await waitResult(P, sendP.commandId, 15000) : null;
  await sleep(400);
  const compP = await composerState(P, conv.stream);
  const orderP = sendP ? orderOf(sendP) : null;
  // 分頁 C：WebSocket 經 page.routeWebSocket 轉送，這一次 send 的 Cmd 回傳扣到 commandResult 送進頁面之後才放
  const C = await openApp('C', layout([conv.stream], conv.stream), { route: routeC });
  await waitLoaded(C, conv.stream, 10000);
  routeState.holdNextSend = true;
  const c4 = await msgCount(C);
  await typeAndEnter(C, conv.stream, '這一則的結果比 Cmd 的回傳先到');
  const heldSend = await waitFor(() => routeState.log.find((l) => l.what === 'send') ?? null, 5000);
  const resC = heldSend ? await waitResult(C, heldSend.commandId, 15000) : null;
  const released = heldSend ? await waitFor(() => routeState.log.find((l) => l.what === 'releasedCompletion' && l.commandId === heldSend.commandId) ?? null, 15000) : null;
  await sleep(1500);
  const compC = await composerState(C, conv.stream);
  const resultFirst = !!heldSend && routeState.log.findIndex((l) => l.what === 'result' && l.commandId === heldSend.commandId)
    < routeState.log.findIndex((l) => l.what === 'releasedCompletion' && l.commandId === heldSend.commandId);
  const userItemC = (await recs(C, c4)).some((m) => m.type === 'item' && m.c === conv.stream && m.kind === 'User');
  step('送出：commandResult 比 Cmd 的回傳先到也對得上', !!sendP && resP?.state === 'done' && compP?.text === '' && compP.settle === null && compP.gate === null
    && !!heldSend && resC?.state === 'done' && !!released && resultFirst && compC?.text === '' && compC.settle === null && compC.gate === null && userItemC,
  `分頁 P：send ${sendP ? sendP.commandId : '沒送'}、結果 ${resP ? resP.state : '—'}（${orderP ?? '—'}）、輸入框${compP?.text === '' ? '清空' : `是「${compP?.text}」`}、說明 ${compP?.settle ?? '無'}；`
    + `分頁 C（扣住 Cmd 的回傳）：結果 ${resC ? resC.state : '—'}${resultFirst ? '先送進頁面、之後才放 Cmd 的回傳' : '沒有先到'}、輸入框${compC?.text === '' ? '清空' : `是「${compC?.text}」`}、說明 ${compC?.settle ?? '無'}、`
    + `User 項目${userItemC ? '有' : '沒有'}出現`,
  { P: { send: sendP, result: resP, order: orderP, composer: compP }, C: { send: heldSend, result: resC, released, composer: compC, route: routeState.log.slice(0, 20) } });
  await closeApp(C);

  // ───── 8. requestDigest：已核准的卡片再送舊 digest、改過的 digest → stale；之後照常核准 ─────
  await P.page.bringToFront();
  const staleS = cardS ? await tpost(P, { cmd: 'approve', conversationId: conv.stream, itemId: cardS.id, choice: 'allow', requestDigest: cardS.digest }) : null;
  const staleSRes = staleS ? await waitResult(P, staleS, 10000) : null;
  await clickRow(P, '卡片與附件');
  await waitLoaded(P, conv.cards, 10000);
  const snapA = await snapshotOf(P, conv.cards);
  const cardA = snapA?.items.find((i) => i.kind === 'Approval' && i.status === 'Waiting') ?? null;
  const tampered = cardA?.digest ? cardA.digest.slice(0, -1) + (cardA.digest.endsWith('0') ? '1' : '0') : null;
  const staleA = tampered ? await tpost(P, { cmd: 'approve', conversationId: conv.cards, itemId: cardA.id, choice: 'allow', requestDigest: tampered }) : null;
  const staleARes = staleA ? await waitResult(P, staleA, 10000) : null;
  await sleep(500);
  const stillA = cardA ? await cardState(P, conv.cards, cardA.id) : null;
  const f5 = frames.length;
  if (cardA) await P.page.click(cardSel(conv.cards, cardA.id) + ' .opt[data-idx="1"]');
  const approveA = await waitFor(() => frames.slice(f5).find((f) => f.page === 'P' && f.kind === 'cmd' && f.cmd === 'approve') ?? null, 5000);
  const approveARes = approveA ? await waitResult(P, approveA.commandId, 10000) : null;
  const doneA = cardA ? await waitFor(async () => { const s = await cardState(P, conv.cards, cardA.id); return s && !s.waiting ? s : null; }, 10000) : null;
  step('requestDigest：舊的與改過的回 stale，帶正確的才核准', staleSRes?.state === 'rejected' && staleSRes.code === 'stale' && !!cardA && staleARes?.state === 'rejected' && staleARes.code === 'stale'
    && !!stillA && stillA.waiting && stillA.enabled && !!approveA && approveA.digest === cardA.digest && approveARes?.state === 'done' && !!doneA,
  `已核准的串流卡片再送原本的 digest：${staleSRes ? `${staleSRes.state}／${staleSRes.code}` : '沒有結果'}；卡片對話的卡片 ${cardA ? cardA.id : '找不到'} 送改過一個字的 digest：`
    + `${staleARes ? `${staleARes.state}／${staleARes.code}` : '沒有結果'}、卡片${stillA?.waiting ? '仍在等待、按鈕可按' : '沒有在等'}；按「允許」帶正確的 digest：${approveARes ? approveARes.state : '—'}、卡片${doneA ? '不再等待' : '還在等'}`,
  { staleStream: staleSRes, staleCards: staleARes, still: stillA, approve: approveA, approveResult: approveARes });

  // ───── 8b. Grok 與 Antigravity 的對話（Grok／Antigravity 同步的 P5）：標頭照代理種類；Grok 的核准卡帶 requestDigest、執行中送出只排隊不插入；Antigravity 沒有卡片 ─────
  await P.page.bringToFront();
  await clickRow(P, 'Grok 的對話');
  const grokLoaded = await waitLoaded(P, conv.grok, 10000);
  const snapG = await snapshotOf(P, conv.grok);
  const headG = snapG?.header ?? null;
  const kindsG = (snapG?.items ?? []).map((i) => i.kind);
  const cardG = snapG?.items.find((i) => i.kind === 'Approval' && i.status === 'Waiting') ?? null;
  const fG = frames.length;
  if (cardG) await P.page.click(cardSel(conv.grok, cardG.id) + ' .opt[data-idx="1"]');
  const approveG = await waitFor(() => frames.slice(fG).find((f) => f.page === 'P' && f.kind === 'cmd' && f.cmd === 'approve') ?? null, 5000);
  const approveGRes = approveG ? await waitResult(P, approveG.commandId, 10000) : null;
  const mG = await msgCount(P);
  const streamingG = await waitFor(async () => ((await recs(P, mG)).some((m) => m.type === 'item' && m.c === conv.grok && m.kind === 'Assistant') ? true : null), 10000);
  await P.page.fill(taSel(conv.grok), 'Grok 執行中送出的一則');
  const uiG = await P.page.evaluate((sel) => {
    const root = document.querySelector(sel);
    return { title: root?.querySelector('.cbtn.send')?.title ?? null, placeholder: root?.querySelector('textarea.cinput')?.placeholder ?? null };
  }, convSel(conv.grok)).catch(() => ({ title: null, placeholder: null }));
  const fG2 = frames.length;
  await P.page.press(taSel(conv.grok), 'Enter');
  const sendG = await waitFor(() => frames.slice(fG2).find((f) => f.page === 'P' && f.kind === 'cmd' && f.cmd === 'send') ?? null, 5000);
  const sendGRes = sendG ? await waitResult(P, sendG.commandId, 15000) : null;
  const queuedG = await waitFor(async () => ((await recs(P, mG)).some((m) => m.type === 'composer' && m.c === conv.grok && m.queue === 1) ? true : null), 5000);
  const doneG = await waitFor(async () => {
    const l = (await latestList(P))?.find((c) => c.id === conv.grok);
    const q = (await recs(P, mG)).filter((m) => m.type === 'composer' && m.c === conv.grok).at(-1);
    return l && l.running === false && q && q.queue === 0 ? true : null;
  }, 30000);
  const headS = snapS?.header ?? null;
  step('Grok 的對話：標頭照代理種類、核准卡帶 requestDigest、執行中送出只排隊不插入（這一輪完成後接著送）', !!grokLoaded && headG?.provider === 'Grok' && headG.steer === false
    && JSON.stringify(headG.modes) === JSON.stringify(MODES.Grok) && headS?.steer === true && ['Thinking', 'Plan', 'Tool', 'Approval'].every((k) => kindsG.includes(k))
    && !!cardG && !!approveG && approveG.digest === cardG.digest && approveGRes?.state === 'done' && !!streamingG && uiG.title === QUEUE_TITLE
    && sendGRes?.state === 'done' && !!queuedG && !!doneG,
  `標頭 provider ${headG?.provider ?? '—'}、steer ${headG?.steer}（Claude 的串流對話 ${headS?.steer}）、權限選單 ${(headG?.modes ?? []).join('/')}；項目 ${kindsG.join('、')}；`
      + `核准卡 ${cardG ? cardG.id : '找不到'}、approve ${approveG ? (approveG.digest === cardG?.digest ? '帶項目的 requestDigest' : '的 digest 不同') : '沒送'}、結果 ${approveGRes?.state ?? '—'}；`
      + `串流${streamingG ? '中' : '沒開始'}打字：送出鈕「${uiG.title ?? '—'}」、提示「${uiG.placeholder ?? '—'}」；送出 ${sendGRes?.state ?? '—'}、佇列${queuedG ? '一則' : '沒有'}；${doneG ? '這一輪完成後接著送、佇列清空' : '30 秒內沒有跑完'}`,
  { header: headG, kinds: kindsG, card: cardG, approve: approveG, send: sendG, ui: uiG });

  await clickRow(P, 'Antigravity 的對話');
  const agyLoaded = await waitLoaded(P, conv.agy, 10000);
  const snapAgy = await snapshotOf(P, conv.agy);
  const headAgy = snapAgy?.header ?? null;
  const kindsAgy = (snapAgy?.items ?? []).map((i) => i.kind);
  const fAgy = frames.length;
  await typeAndEnter(P, conv.agy, 'Antigravity 從網頁送出');
  const sendAgy = await waitFor(() => frames.slice(fAgy).find((f) => f.page === 'P' && f.kind === 'cmd' && f.cmd === 'send') ?? null, 5000);
  const sendAgyRes = sendAgy ? await waitResult(P, sendAgy.commandId, 15000) : null;
  const doneAgy = await waitFor(async () => ((await latestList(P))?.find((c) => c.id === conv.agy)?.running === false ? true : null), 15000);
  step('Antigravity 的對話：標頭照代理種類（沒有「每步詢問」）、沒有卡片、從網頁送出', !!agyLoaded && headAgy?.provider === 'Antigravity' && headAgy.steer === false
    && headAgy.permission === 'AcceptEdits' && JSON.stringify(headAgy.modes) === JSON.stringify(MODES.Antigravity)
    && kindsAgy.includes('Tool') && !kindsAgy.some((k) => ['Approval', 'Question', 'PlanApproval'].includes(k)) && sendAgyRes?.state === 'done' && !!doneAgy,
  `標頭 provider ${headAgy?.provider ?? '—'}、steer ${headAgy?.steer}、權限 ${headAgy?.permission ?? '—'}（選單 ${(headAgy?.modes ?? []).join('/')}）；項目 ${kindsAgy.join('、')}；送出 ${sendAgyRes?.state ?? '—'}、${doneAgy ? '跑完' : '15 秒內沒跑完'}`,
  { header: headAgy, kinds: kindsAgy, send: sendAgy });
  // 第 9 步看卡片對話的輸入區閘門：切回它（作用中的對話才隨電腦的狀態更新閘門）
  await clickRow(P, '卡片與附件');
  await waitLoaded(P, conv.cards, 10000);

  // ───── 9. 政策改成 View → 送出停用；結束假裝置 → 電腦離線 ─────
  const stop1 = await stopFake(device.run);
  device.run = await startRun('run2', [{ op: 'policy', level: 'View', notify: true }, { op: 'waitDrained', seconds: 60 }, { op: 'wait', ms: 600000 }]);
  await typeInto(P, conv.cards, '政策是 View 時打的字');
  const viewGate = await waitComposer(P, conv.cards, (c) => c.gate === TEXT.view, 30000);
  const fView = frames.length;
  if (viewGate) await P.page.press(taSel(conv.cards), 'Enter');
  await sleep(500);
  const viewAfter = await composerState(P, conv.cards);
  const sentInView = frames.slice(fView).filter((f) => f.kind === 'cmd' && f.cmd === 'send').length;
  const policy2 = scriptSteps(device.run).find((s) => s.op === 'policy')?.result ?? null;
  step('政策 View：送出停用、說明逐字', stop1.done && policy2?.level === 'View' && !!viewGate && viewGate.sendDisabled && viewAfter.text === '政策是 View 時打的字' && sentInView === 0,
    `結束第一次的劇本程序（${stop1.done ? '已結束' : '沒結束'}）、以同一個資料根跑 policy View（回讀 ${policy2?.level ?? '—'}）；輸入區說明「${viewGate?.gate ?? viewAfter.gate ?? '—'}」、`
      + `送出鈕${viewGate?.sendDisabled ? '停用' : '可按'}；按 Enter 之後字${viewAfter.text === '政策是 View 時打的字' ? '還在' : '不見了'}、送出的 send ${sentInView} 筆`,
    { gate: viewGate, after: viewAfter, policy: policy2 });
  const stop2 = await stopFake(device.run);
  const stoppedAt = Date.now();
  // 剛斷線：寬限期內閘門照樣擋、說明是「重新連線中」；寬限期過後換成「離線」（網頁到期時自己重算，不必等新的裝置清單）
  const reGate = await waitComposer(P, conv.cards, (c) => c.gate === TEXT.reconnecting, 30000);
  const reAt = Date.now();
  const offGate = await waitComposer(P, conv.cards, (c) => c.gate === TEXT.offline, RECONNECT_GRACE_MS + 15000);
  const offAt = Date.now();
  const offRow = (await sidebarRows(P)).find((r) => r.title === '卡片與附件') ?? null;
  const lastGate = offGate ?? (await composerState(P, conv.cards));
  step('結束假裝置：先是「重新連線中」、寬限期過後「離線」，兩段都擋住送出', stop2.done && !!reGate && reGate.sendDisabled && !!offGate && offGate.sendDisabled
    && offAt - reAt >= RECONNECT_GRACE_MS - 2000 && offRow?.online === 'false',
  `假裝置${stop2.done ? '已結束' : '沒結束'}；${reGate ? `${reAt - stoppedAt} ms 後輸入區說明「${reGate.gate}」、送出鈕${reGate.sendDisabled ? '停用' : '可按'}` : '30 秒內沒有出現「重新連線中」'}；`
      + `${offGate ? `再 ${offAt - reAt} ms 後（寬限期 ${RECONNECT_GRACE_MS} ms）說明「${offGate.gate}」、送出鈕${offGate.sendDisabled ? '停用' : '可按'}` : `寬限期過後沒有換成「離線」（最後是「${lastGate?.gate ?? '沒有說明'}」、送出鈕${lastGate?.sendDisabled ? '停用' : '可按'}）`}；`
      + `清單的徽章 data-online ${offRow?.online ?? '—'}`,
  { reconnecting: reGate, gate: offGate, last: lastGate, row: offRow, ms: { reconnecting: reAt - stoppedAt, offline: offAt - reAt } });

  // ───── 10. 同一個資料根重啟假裝置（政策改回 Control）→ 恢復 ─────
  device.run = await startRun('run3', [{ op: 'policy', level: 'Control', notify: true }, { op: 'waitDrained', seconds: 60 }, { op: 'wait', ms: 600000 }]);
  const backGate = await waitComposer(P, conv.cards, (c) => c.gate === null && !c.sendDisabled, 30000);
  const f7 = frames.length;
  if (backGate) await P.page.press(taSel(conv.cards), 'Enter');
  const send7 = await waitFor(() => frames.slice(f7).find((f) => f.page === 'P' && f.kind === 'cmd' && f.cmd === 'send') ?? null, 5000);
  const res7 = send7 ? await waitResult(P, send7.commandId, 15000) : null;
  const onRow = (await sidebarRows(P)).find((r) => r.title === '卡片與附件') ?? null;
  step('重啟假裝置：恢復、留著的字可以送出', !!backGate && !!send7 && send7.text === '政策是 View 時打的字' && res7?.state === 'done' && onRow?.online === 'true',
    `${backGate ? '說明消失、送出鈕可按' : '30 秒內沒有恢復'}；送出留著的字：${send7 ? `「${send7.text}」` : '沒送'}、結果 ${res7 ? res7.state : '—'}；清單的徽章 data-online ${onRow?.online ?? '—'}`,
    { gate: backGate, send: send7, result: res7 });

  // ───── 11. 草稿：選電腦、標頭晶片、送出第一則換成正式對話（兩個分頁）─────
  await waitFor(async () => ((await latestList(P))?.find((c) => c.id === conv.cards)?.running === false ? true : null), 15000);
  await P.page.bringToFront();
  const f8 = frames.length;
  const m8 = await msgCount(P);
  await P.page.keyboard.press('Control+n');
  const nc = await waitFor(() => frames.slice(f8).find((f) => f.page === 'P' && f.kind === 'cmd' && f.cmd === 'newConversation') ?? null, 5000);
  const draftMsg = await waitFor(async () => (await recs(P, m8)).find((m) => m.type === 'conversation' && m.isDraft) ?? null, 10000);
  const draft = draftMsg?.c ?? null;
  const chip = draft ? await waitFor(() => draftChip(P, draft), 5000) : null;
  const wsBefore = await wsState(P);
  // 網頁存版面有 300 ms 的防抖：等存下的版面有這份草稿，分頁 Q 才照它開
  const savedDraft = draft ? await waitFor(() => P.page.evaluate(([k, id]) => {
    try { return (localStorage.getItem(k) || '').includes(id) ? true : null; } catch { return null; }
  }, [LAYOUT_KEY, draft]).catch(() => null), 5000) : null;
  const Q = await openApp('Q', undefined);
  const qDraft = draft ? await waitLoaded(Q, draft, 10000) : null;
  await P.page.bringToFront();
  const mP = await msgCount(P);
  const mQ = await msgCount(Q);
  if (draft) await typeAndEnter(P, draft, '草稿的第一則訊息');
  const closedP = await waitFor(async () => (await recs(P, mP)).find((m) => m.type === 'conversationClosed' && m.c === draft) ?? null, 20000);
  const closedQ = await waitFor(async () => (await recs(Q, mQ)).find((m) => m.type === 'conversationClosed' && m.c === draft) ?? null, 20000);
  const replaced = closedP?.replacedBy ?? null;
  const loadedP = replaced ? await waitLoaded(P, replaced, 10000) : null;
  const loadedQ = replaced ? await waitLoaded(Q, replaced, 10000) : null;
  const wsAfterP = await wsState(P);
  const wsAfterQ = await wsState(Q);
  const samePlace = (before, after) => {
    const pb = before.panes.find((p) => p.tabs.includes(draft));
    const pa = after.panes.find((p) => p.id === pb?.id);
    return !!pb && !!pa && pa.tabs.indexOf(replaced) === pb.tabs.indexOf(draft) && !pa.tabs.includes(draft) && pa.active === replaced;
  };
  const replacedTitle = replaced ? (await latestList(P))?.find((c) => c.id === replaced)?.title ?? null : null;
  step('草稿：自動選電腦、標頭晶片、送出第一則後兩個分頁都換成正式對話（replacedBy）', !!nc && nc.deviceId === device.id && !!nc.accountId && !!draft && !!chip && chip.shown
    && chip.text === TEXT.chip && chip.online === 'true' && !!qDraft && !!replaced && closedQ?.replacedBy === replaced && !!loadedP && !!loadedQ
    && samePlace(wsBefore, wsAfterP) && wsAfterQ.panes.some((p) => p.tabs.includes(replaced) && !p.tabs.includes(draft)) && replacedTitle === '草稿的第一則訊息',
  `Ctrl+N：newConversation 帶 deviceId ${nc?.deviceId === device.id ? '這台' : nc?.deviceId ?? '—'}、accountId ${nc?.accountId ?? '—'}（只有一台候選，不問）；草稿 ${draft ?? '—'}、晶片「${chip?.text ?? '—'}」（在線 ${chip?.online ?? '—'}）；`
    + `分頁 Q 照存下的版面也開著這份草稿${qDraft ? '' : '（沒有）'}；送出第一則後 P 收到 replacedBy ${replaced ?? '—'}、Q ${closedQ?.replacedBy ?? '—'}；P ${samePlace(wsBefore, wsAfterP) ? '在同一個位置換成新對話' : '位置不對'}、`
    + `Q ${wsAfterQ.panes.some((p) => p.tabs.includes(replaced)) ? '也換了' : '沒換'}；新對話標題「${replacedTitle ?? '—'}」`,
  { newConversation: nc, draft, savedDraft, chip, closedP, closedQ, panes: { before: wsBefore.panes, afterP: wsAfterP.panes, afterQ: wsAfterQ.panes } });
  await closeApp(Q);
  await closeApp(P);

  // ───── 12. 兩個分頁的 notify：A 看 X、B 看 Y；A 切到 Z 之後 X 有事件 ─────
  const [X, Y, Z] = seeded;
  const titleOf = async (page, id) => (await latestList(page))?.find((c) => c.id === id)?.title ?? null;
  const A = await openApp('A', layout([X], X));
  await waitLoaded(A, X, 10000);
  const B = await openApp('B', layout([Y], Y));
  await waitLoaded(B, Y, 10000);
  const titles = { X: await titleOf(A, X), Y: await titleOf(A, Y), Z: await titleOf(A, Z) };
  await A.page.bringToFront();
  await clickRow(A, titles.Z);
  await waitLoaded(A, Z, 10000);
  const wsA = await wsState(A);
  await sleep(800); // ClientState 有 200 ms 的合併
  const vis = { A: await A.page.evaluate(() => document.visibilityState), B: await B.page.evaluate(() => document.visibilityState) };
  const nA = await msgCount(A);
  const nB = await msgCount(B);
  const sendX = await tpost(B, { cmd: 'send', conversationId: X, text: 'X 的事件' });
  const resX = sendX ? await waitResult(B, sendX, 15000) : null;
  const notifyAX = await waitFor(async () => (await recs(A, nA)).find((m) => m.type === 'notify' && m.c === X) ?? null, 15000);
  const unreadX = await waitFor(async () => ((await latestList(A))?.find((c) => c.id === X)?.unread === true ? true : null), 10000);
  const dotX = await waitFor(async () => ((await sidebarRows(A)).find((r) => r.title === titles.X)?.unread ? true : null), 5000);
  await sleep(1000);
  const notifyBX = (await recs(B, nB)).filter((m) => m.type === 'notify' && m.c === X);
  const toastA = (await domToasts(A)).find((t) => t.startsWith(DEVICE_NAME + '：')) ?? null;
  // B 看著 Y：Y 有事件時 B 收不到、A 收得到，Y 不標未讀
  const nA2 = await msgCount(A);
  const nB2 = await msgCount(B);
  const sendY = await tpost(A, { cmd: 'send', conversationId: Y, text: 'Y 的事件' });
  const resY = sendY ? await waitResult(A, sendY, 15000) : null;
  const notifyAY = await waitFor(async () => (await recs(A, nA2)).find((m) => m.type === 'notify' && m.c === Y) ?? null, 15000);
  await sleep(2000);
  const notifyBY = (await recs(B, nB2)).filter((m) => m.type === 'notify' && m.c === Y);
  const unreadY = (await latestList(B))?.find((c) => c.id === Y)?.unread ?? null;
  step('兩個分頁的 notify：切走的對話有事件會通知並標未讀、看著的分頁收不到', vis.A === 'visible' && vis.B === 'visible' && wsA.panes[0]?.active === Z && resX?.state === 'done'
    && notifyAX?.kind === 'completed' && notifyAX.title === titles.X && notifyAX.deviceName === DEVICE_NAME && unreadX === true && dotX === true
    && resY?.state === 'done' && !!notifyAY && notifyBY.length === 0 && unreadY === false,
  `可見度 A ${vis.A}、B ${vis.B}；A 從 X 切到 Z（作用中 ${wsA.panes[0]?.active === Z ? 'Z' : wsA.panes[0]?.active}）；B 對 X 送出（${resX ? resX.state : '—'}）→ A 收到 notify ${notifyAX ? `${notifyAX.kind}「${notifyAX.title}」（電腦名稱照字面 ${notifyAX.deviceName === DEVICE_NAME}）` : '沒有'}、`
    + `X 在 A 的清單${unreadX ? '標未讀' : '沒有標未讀'}${dotX ? '、有未讀點' : ''}、頁內提示「${toastA ?? '—'}」；B 也看不到 X，照伺服器的規則也收到 ${notifyBX.length} 則（D28：送給所有看不到的連線）；`
    + `A 對 Y 送出（${resY ? resY.state : '—'}）→ 看著 Y 的 B 收到 ${notifyBY.length} 則、A 收到 ${notifyAY ? 1 : 0} 則、Y 在 B 的清單 unread ${unreadY}`,
  { visibility: vis, titles, notifyAX, notifyBX, notifyAY, notifyBY, unreadX, unreadY, toastA, panesA: wsA.panes });
  await closeApp(A);
  await closeApp(B);

  // ───── 13. 剪貼簿：授權與拒絕（頁面的 navigator.clipboard 換成假的：不碰這台電腦真的剪貼簿）─────
  const M = await openApp('M', layout([conv.xss], conv.xss));
  await waitLoaded(M, conv.xss, 15000);
  const lastTwo = corpus.slice(-2);
  const domIdOf = (label) => M.page.evaluate(([cid, l]) => {
    const n = [...document.querySelectorAll('.conv[data-conv="' + cid + '"] .it[data-kind="Assistant"]')].find((x) => x.textContent.trim().startsWith(l));
    return n ? n.dataset.id : null;
  }, [conv.xss, label]);
  const idAllow = await waitFor(() => domIdOf(lastTwo[1].label), 10000);
  const idDeny = await domIdOf(lastTwo[0].label);
  await M.page.evaluate(() => { window.__e2eClipboard = 'allow'; });
  const m10 = await msgCount(M);
  if (idAllow) await clickCopy(M, conv.xss, idAllow);
  // 「已複製」只留 1.4 秒：和等寫入同時輪詢
  const copiedP = idAllow ? waitFor(async () => { const b = await copyBtn(M, conv.xss, idAllow); return b && b.done ? b : null; }, 10000) : Promise.resolve(null);
  const written = await waitFor(async () => { const w = await M.page.evaluate(() => window.__clipboard.slice()); return w.length ? w : null; }, 10000);
  const copied = await copiedP;
  const clipMsg = (await recs(M, m10)).find((m) => m.type === 'clipboard') ?? null;
  await M.page.evaluate(() => { window.__e2eClipboard = 'deny'; });
  if (idDeny) await clickCopy(M, conv.xss, idDeny);
  const fallback = await waitFor(() => fallbackState(M), 10000);
  const writes = await M.page.evaluate(() => window.__clipboard.slice());
  if (fallback) await M.page.click('.dlg-copy-fallback .dlg-foot button').catch(() => {});
  await waitFor(async () => ((await M.page.evaluate(() => document.querySelectorAll('.dlg-back').length)) === 0 ? true : null), 3000);
  step('剪貼簿：授權時寫入原文、拒絕時開退路框', !!idAllow && !!written && written.length === 1 && written[0].ok && written[0].text === lastTwo[1].text && clipMsg?.text === lastTwo[1].text
    && !!copied && !!idDeny && !!fallback && fallback.value === lastTwo[0].text && fallback.readOnly && fallback.imgs === 0 && writes.length === 2 && !writes[1].ok,
  `${lastTwo[1].label}：伺服器回的 clipboard ${clipMsg ? (clipMsg.text === lastTwo[1].text ? '等於劇本的原文' : '和原文不同') : '沒有'}、寫進剪貼簿 ${written ? written.length : 0} 次${written?.[0]?.text === lastTwo[1].text ? '（原文）' : ''}、按鈕「${copied?.text ?? '—'}」；`
    + `${lastTwo[0].label}（剪貼簿被拒）：退路框${fallback ? `的文字${fallback.value === lastTwo[0].text ? '等於原文' : '不同'}、唯讀 ${fallback.readOnly}、img ${fallback.imgs} 個` : '沒有出現'}`,
  { allow: { id: idAllow, written, clipboard: clipMsg && { same: clipMsg.text === lastTwo[1].text }, button: copied }, deny: { id: idDeny, fallback: fallback && { ...fallback, value: fallback.value === lastTwo[0].text ? '(相同)' : fallback.value } } });

  // ───── 14. 上傳、/f/ 對話框、SEC-23、dirsOnly 選資料夾 ─────
  const workspace = path.join(device.data, 'conversations', conv.cards, 'workspace');
  const attachDir = path.join(device.data, 'conversations', conv.cards, 'attachments');
  fs.mkdirSync(path.join(workspace, 'picked'), { recursive: true });
  const REPORT = '報表第一行\n第二行：遠端預覽的文字檔\n';
  fs.writeFileSync(path.join(workspace, 'report.txt'), REPORT, 'utf8');
  const SVG = `<svg xmlns="http://www.w3.org/2000/svg" onload="console.log('${SEC23_MARK}')"><script>console.log('${SEC23_MARK}');alert('SEC23')</script><text y="20">svg</text></svg>`;
  const HTML = `<!doctype html><title>evil</title><script>console.log('${SEC23_MARK}');alert('SEC23')</script><img src=x onerror="console.log('${SEC23_MARK}')">`;
  fs.writeFileSync(path.join(workspace, 'evil.svg'), SVG, 'utf8');
  fs.writeFileSync(path.join(workspace, 'evil.html'), HTML, 'utf8');
  fs.writeFileSync(path.join(workspace, 'evil.png'), SVG, 'utf8');
  const uploads = [
    { name: '上傳的文字.txt', buffer: Buffer.from('遠端上傳的第一個檔案\n', 'utf8'), mimeType: 'text/plain' },
    { name: 'upload-b.bin', buffer: seededBytes(20261021, 70000), mimeType: 'application/octet-stream' },
  ];
  const localSha = Object.fromEntries(uploads.map((u) => [u.name, sha256(u.buffer)]));
  await M.page.bringToFront();
  await clickRow(M, '卡片與附件');
  await waitLoaded(M, conv.cards, 10000);
  const f11 = frames.length;
  const m11 = await msgCount(M);
  const [chooser] = await Promise.all([M.page.waitForEvent('filechooser', { timeout: 5000 }), M.page.click(convSel(conv.cards) + ' [data-field="attach"]')]);
  await chooser.setFiles(uploads);
  const attached = await untilChips(M, conv.cards, (s) => s.chips.length === 2 && s.chips.every((c) => c.state === 'attached'), 30000);
  const add = frames.slice(f11).find((f) => f.page === 'M' && f.kind === 'cmd' && f.cmd === 'addUploaded') ?? null;
  const addRes = add ? await waitResult(M, add.commandId, 10000) : null;
  const puts = httpLog.filter((r) => r.page === 'M' && r.method === 'PUT' && r.path.startsWith('/api/v1/uploads/'));
  await typeAndEnter(M, conv.cards, '附上兩個檔案');
  const send11 = await waitFor(() => frames.slice(f11).find((f) => f.page === 'M' && f.kind === 'cmd' && f.cmd === 'send') ?? null, 5000);
  const res11 = send11 ? await waitResult(M, send11.commandId, 30000) : null;
  const cleared = await untilChips(M, conv.cards, (s) => s.chips.length === 0, 10000);
  const stored = listFilesDeep(attachDir).map((f) => ({ file: path.relative(attachDir, f), sha256: sha256(fs.readFileSync(f)) }));
  const shaOk = uploads.every((u) => stored.some((s) => s.sha256 === localSha[u.name]))
    && (add?.uploads ?? []).length === 2 && add.uploads.every((u) => u.sha256 === localSha[u.name] && u.size === uploads.find((x) => x.name === u.name)?.buffer.length);
  step('上傳：addUploaded 的 SHA-256 與電腦存下的附件等於本機算的、送出 done 後晶片清掉', !!attached && puts.length === 2 && puts.every((r) => r.status === 201 || r.status === 200)
    && addRes?.state === 'done' && res11?.state === 'done' && !!cleared && shaOk,
  `迴紋針選 2 個檔：PUT ${puts.map((r) => r.status).join('、') || '—'}；晶片${attached ? '都是已附加' : '沒有都附加'}；addUploaded ${add ? `${add.uploads?.length} 個、結果 ${addRes?.state ?? '—'}` : '沒送'}；`
    + `送出 ${res11 ? res11.state : '—'}、晶片${cleared ? '清掉了' : '還在'}；電腦的 attachments\\ 有 ${stored.length} 個檔，SHA-256 ${shaOk ? '都等於本機算的' : '對不上'}`,
  { puts, addUploaded: add, local: localSha, stored, send: send11, result: res11 });

  // 檔案抽屜：listFiles → report.txt 的 /f/ 對話框（內容與標頭）
  await M.page.click(convSel(conv.cards) + ' header.ch button[aria-label="檔案瀏覽器"]');
  const listed = await waitFor(async () => {
    const n = await drawerNames(M, conv.cards);
    return n && ['report.txt', 'evil.svg', 'evil.html', 'evil.png', 'picked'].every((x) => n.includes(x)) ? n : null;
  }, 15000);
  const filesMsg = (await recs(M, m11)).filter((m) => m.type === 'files' && m.c === conv.cards).at(-1) ?? null;
  const fileRef = async (name) => {
    const n0 = await msgCount(M);
    await clickEntry(M, conv.cards, name);
    const ref = await waitFor(async () => (await recs(M, n0)).find((m) => m.type === 'fileRef') ?? null, 10000);
    const dlg = await waitFor(() => fileRefDialog(M), 5000);
    await M.page.keyboard.press('Escape');
    await waitFor(async () => ((await fileRefDialog(M)) ? null : true), 3000);
    return { ref, dlg };
  };
  const report = await fileRef('report.txt');
  const reportGet = report.dlg?.link ? await pageFetch(M, report.dlg.link.href) : null;
  step('檔案抽屜：listFiles、/f/ 對話框與內容', !!listed && !!report.ref && !!report.dlg?.link && report.dlg.link.href === report.ref.url && /^\/f\//.test(report.dlg.link.href)
    && report.dlg.link.rel === 'noopener noreferrer' && report.dlg.link.target === '_blank' && reportGet?.status === 200 && reportGet.text === REPORT
    && reportGet.headers['content-type'] === 'text/plain; charset=utf-8' && reportGet.headers['content-security-policy'] === MEDIA_CSP,
  `抽屜列出 ${listed ? listed.length : 0} 項（${(listed ?? []).join('、')}）；report.txt 的 fileRef ${report.ref?.url ?? report.ref?.error ?? '—'}、對話框連結 ${report.dlg?.link?.href ?? '—'}（rel ${report.dlg?.link?.rel ?? '—'}）；`
    + `GET ${reportGet ? `${reportGet.status}、${reportGet.headers['content-type']}、內容${reportGet.text === REPORT ? '相同' : '不同'}、CSP ${reportGet.headers['content-security-policy'] === MEDIA_CSP ? '等於 MediaCsp' : reportGet.headers['content-security-policy']}` : '沒有'}`,
  { listed, report, get: reportGet && { status: reportGet.status, headers: reportGet.headers, same: reportGet.text === REPORT } });

  // SEC-23：直接開 /m/（內容是 SVG 的 .png）與 /f/（.svg、.html）：0 次腳本執行、CSP 逐字等於 MediaCsp；正向對照證明偵測得到
  const pngEntry = filesMsg?.entries?.find((e) => e.name === 'evil.png') ?? null;
  const svgRef = await fileRef('evil.svg');
  const htmlRef = await fileRef('evil.html');
  const ctrl = await secControl(HTML);
  const opened = [];
  for (const [label, href] of [['SEC-M-png', pngEntry?.url], ['SEC-F-svg', svgRef.dlg?.link?.href], ['SEC-F-html', htmlRef.dlg?.link?.href]]) opened.push(await secOpen(label, href));
  step('SEC-23：直接開 /m/、/f/ 的 SVG／HTML 不執行腳本、CSP 逐字等於 MediaCsp（含 sandbox）', ctrl.ran && opened.length === 3 && opened.every((o) => o.ok),
    `正向對照（同一段 HTML 放進沒有 CSP 的空白頁）：${ctrl.ran ? `偵測到執行（主控台 ${ctrl.marks} 筆、對話框 ${ctrl.dialogs} 個）` : '偵測不到，偵測器無效'}；`
      + opened.map((o) => `${o.label} ${o.href ?? '沒有網址'}：${o.status ?? '—'}、${o.contentType ?? '—'}、CSP ${o.csp === MEDIA_CSP ? '相同' : o.csp ?? '—'}、nosniff ${o.nosniff}、執行 ${o.marks + o.dialogs} 次`).join('；'),
    { control: ctrl, opened, note: '電腦宣告的型別不在白名單時伺服器改成 octet-stream 加 attachment（MediaHeaderRules.SafeMediaContentType），真的電腦宣告不出 SVG／HTML 的 /m/：以內容是 SVG 的 .png 與 /f/ 的 .svg、.html 代替' });

  // dirsOnly：資料夾選單「瀏覽資料夾…」→ 目前的工作資料夾 → picked → 選這個資料夾 → setFolder
  await M.page.keyboard.press('Escape');
  const f12 = frames.length;
  await M.page.click(convSel(conv.cards) + ' .ch-folder');
  await M.page.waitForSelector('.menu', { timeout: 5000 });
  await M.page.locator('.menu .menu-item', { hasText: '瀏覽資料夾…' }).first().click();
  const pickTop = await waitFor(async () => { const d = await drawerState(M, conv.cards); return d && d.picking && d.names.includes('目前的工作資料夾') ? d : null; }, 10000);
  await clickEntry(M, conv.cards, '目前的工作資料夾');
  const pickWs = await waitFor(async () => { const d = await drawerState(M, conv.cards); return d && d.names.includes('picked') ? d : null; }, 10000);
  await clickEntry(M, conv.cards, 'picked');
  const pickSub = await waitFor(async () => { const d = await drawerState(M, conv.cards); return d && /[\\/]picked$/.test(d.pickPath) && !d.pickDisabled ? d : null; }, 10000);
  if (pickSub) await M.page.click(convSel(conv.cards) + ' [data-field="pickFolder"]');
  const setF = await waitFor(() => frames.slice(f12).find((f) => f.page === 'M' && f.kind === 'cmd' && f.cmd === 'setFolder') ?? null, 5000);
  const setRes = setF ? await waitResult(M, setF.commandId, 15000) : null;
  const dirLists = frames.slice(f12).filter((f) => f.page === 'M' && f.kind === 'cmd' && f.cmd === 'listFiles');
  const folderNow = await waitFor(() => M.page.evaluate((sel) => { const c = document.querySelector(sel + ' .ch-folder .chip-label'); return c && /picked/.test(c.textContent) ? c.textContent : null; }, convSel(conv.cards)), 10000);
  step('dirsOnly 選資料夾後 setFolder', !!pickTop && !!pickWs && !!pickSub && dirLists.length >= 2 && dirLists.every((f) => f.dirsOnly === true) && dirLists[0].path === null
    && !!setF && path.normalize(setF.path ?? '') === path.join(workspace, 'picked') && setRes?.state === 'done' && !!folderNow,
  `抽屜挑選模式${pickTop ? `（最上層 ${pickTop.names.join('、')}）` : '沒有出現'}；listFiles ${dirLists.length} 次、都帶 dirsOnly ${dirLists.every((f) => f.dirsOnly === true)}、第一次 path ${dirLists[0] ? dirLists[0].path : '—'}；`
    + `setFolder ${setF ? setF.path : '沒送'}、結果 ${setRes ? setRes.state : '—'}；資料夾晶片「${folderNow ?? '—'}」`,
  { top: pickTop, workspace: pickWs, sub: pickSub, lists: dirLists, setFolder: setF, result: setRes });

  // ───── 15. admin stop 後在同一個資料根重啟：重連、重新 open、結果不明、沒有換版橫幅、草稿重開 ─────
  await M.page.bringToFront();
  const f13 = frames.length;
  await M.page.keyboard.press('Control+n');
  const nc13 = await waitFor(() => frames.slice(f13).find((f) => f.page === 'M' && f.kind === 'cmd' && f.cmd === 'newConversation') ?? null, 5000);
  const draft13 = await waitFor(async () => (await recs(M)).filter((m) => m.type === 'conversation' && m.isDraft).at(-1)?.c ?? null, 10000);
  if (draft13) await waitLoaded(M, draft13, 10000);
  if (draft13) await typeInto(M, draft13, '伺服器重開前打的字');
  const wsM0 = await wsState(M);
  const C2 = await openApp('C2', layout([conv.stream], conv.stream), { route: routeC });
  await waitLoaded(C2, conv.stream, 10000);
  routeState.dropNextResult = true;
  await typeAndEnter(C2, conv.stream, '結果送不回來的訊息');
  const dropped = await waitFor(() => routeState.log.find((l) => l.what === 'droppedResult') ?? null, 15000);
  const settling = await composerState(C2, conv.stream);
  const navs0 = { M: pageNavs.get('M') ?? 0, C2: pageNavs.get('C2') ?? 0 };
  const i0 = { M: (await recs(M)).filter((m) => m.type === 'init').length, C2: (await recs(C2)).filter((m) => m.type === 'init').length };
  const mM = await msgCount(M);
  const mC = await msgCount(C2);
  downtime.from = Date.now();
  const stopped = await stopHub(hubRuns.at(-1));
  const run1Left = keysLeft(hubRuns.at(-1));
  const run2 = await startHub();
  step('admin stop 後在同一個資料根重啟 MyriagentHub', stopped.ok && run1Left.length === 0 && run2.ok && run2.data.healthz.build === build,
    `${stopped.summary}；第一次啟動匯入的金鑰檔停止後還在 ${run1Left.length} 個；${run2.summary}`, { stopped, run1Left, run2: run2.data });
  if (!run2.ok) throw new Error('MyriagentHub 沒有重新起來');
  const back = {};
  for (const [label, app] of [['M', M], ['C2', C2]]) {
    back[label] = await waitFor(async () => {
      const r = await recs(app);
      return r.filter((m) => m.type === 'init').length > i0[label] ? true : null;
    }, 60000);
  }
  downtime.to = Date.now();
  await sleep(1500);
  const reopenM = (await recs(M, mM)).filter((m) => m.type === 'conversation').map((m) => m.c);
  const reopenC = (await recs(C2, mC)).filter((m) => m.type === 'conversation').map((m) => m.c);
  const unknownC = await waitComposer(C2, conv.stream, (c) => c.settle === TEXT.unknown, 10000);
  const recreated = await waitFor(async () => {
    const s = await wsState(M);
    const pane = s.panes.find((p) => p.id === wsM0.panes.find((x) => x.tabs.includes(draft13))?.id);
    const id = pane?.tabs[wsM0.panes.find((x) => x.tabs.includes(draft13))?.tabs.indexOf(draft13)];
    if (!id || id === draft13 || !/^d/.test(id)) return null;
    const t = await M.page.evaluate((cid) => { const ta = document.querySelector('.conv[data-conv="' + cid + '"] textarea.cinput'); return ta ? ta.value : null; }, id);
    return t === '伺服器重開前打的字' ? { id, panes: s.panes, active: pane.active } : null;
  }, 15000);
  const noticeM = await M.page.evaluate(() => [...document.querySelectorAll('.live-notices > .live-notice')].map((n) => n.dataset.notice));
  const noticeC = await C2.page.evaluate(() => [...document.querySelectorAll('.live-notices > .live-notice')].map((n) => n.dataset.notice));
  const navs1 = { M: pageNavs.get('M') ?? 0, C2: pageNavs.get('C2') ?? 0 };
  const tabsM = wsM0.panes.flatMap((p) => p.tabs).filter((id) => id !== draft13);
  step('重啟後：分頁重連並重新 open、等結果的指令變 unknown、同一個 build 沒有橫幅、草稿在同一窗格重開並還原字', !!nc13 && !!draft13 && !!dropped && settling?.settle !== TEXT.unknown
    && back.M === true && back.C2 === true && tabsM.every((id) => reopenM.includes(id)) && reopenC.includes(conv.stream) && !!unknownC && !!recreated
    && !noticeM.includes('build') && !noticeC.includes('build') && navs1.M === navs0.M && navs1.C2 === navs0.C2,
  `停機 ${downtime.to - downtime.from} ms；M ${back.M ? '收到新的 init' : '沒有重連'}、重新 open ${reopenM.length} 次（開著的 ${tabsM.length} 個對話${tabsM.every((id) => reopenM.includes(id)) ? '都有' : '沒有都'}）；`
    + `C2 ${back.C2 ? '收到新的 init' : '沒有重連'}、重新 open ${reopenC.includes(conv.stream) ? '串流對話' : '沒有'}；結果被丟掉的 send ${dropped ? dropped.commandId : '—'} → 輸入區說明「${unknownC?.settle ?? '—'}」；`
    + `草稿 ${draft13 ?? '—'} ${recreated ? `在同一個位置換成 ${recreated.id}、字還原` : '沒有重開或字沒還原'}；換版橫幅 M ${noticeM.includes('build') ? '有' : '沒有'}、C2 ${noticeC.includes('build') ? '有' : '沒有'}；重新載入 M ${navs1.M - navs0.M} 次、C2 ${navs1.C2 - navs0.C2} 次`,
  { draft: draft13, recreated, reopen: { M: reopenM, C2: reopenC }, unknown: unknownC, notices: { M: noticeM, C2: noticeC }, navs: { before: navs0, after: navs1 }, downtime: { ...downtime } });
  await closeApp(C2);

  // ───── 16. XSS 語料全部載入：0 執行；CSP 正向對照；主控台只有預期的錯誤 ─────
  await M.page.bringToFront();
  await clickRow(M, XSS_TITLE);
  await waitLoaded(M, conv.xss, 10000);
  let olderClicks = 0;
  for (let i = 0; i < 20; i++) {
    const more = await M.page.evaluate((sel) => { const r = document.querySelector(sel + ' .older'); return !!r && !r.hidden; }, convSel(conv.xss));
    if (!more) break;
    const n0 = await msgCount(M);
    await M.page.click(convSel(conv.xss) + ' .older-btn');
    olderClicks++;
    await waitFor(async () => ((await recs(M, n0)).some((m) => m.type === 'olderItems' && m.c === conv.xss) ? true : null), 10000);
    await sleep(300);
  }
  const rendered = await waitFor(async () => {
    const n = await M.page.evaluate((sel) => document.querySelectorAll(sel + ' .it[data-id]').length, convSel(conv.xss));
    return n >= corpus.length + 1 ? n : null;
  }, 15000);
  const dom = await domXssScan(M);
  const titleText = await M.page.evaluate((sel) => { const t = document.querySelector(sel + ' .ch-title-text'); return t ? t.textContent : null; }, convSel(conv.xss));
  const sec = dialogs.filter((d) => !d.page.startsWith('SEC-CTRL'));
  step(`XSS 語料 ${corpus.length} 筆全部載入：0 執行`, rendered !== null && dom.ok && titleText === XSS_TITLE && sec.length === 0,
    `載入較早的訊息 ${olderClicks} 次；對話裡 ${rendered ?? '不到 ' + (corpus.length + 1)} 個項目；標頭標題照字面 ${titleText === XSS_TITLE}；${dom.summary}；對話框 ${sec.length} 個（正向對照的不算）`,
    { rendered, dom, dialogs: sec });
  const cspBefore = cspLog.filter((v) => v.page === 'M').length;
  const inlineRan = await M.page.evaluate(() => {
    const s = document.createElement('script');
    s.textContent = 'window.__inlineRan = true;';
    document.head.append(s);
    return window.__inlineRan === true;
  });
  await sleep(300);
  const cspAfter = cspLog.filter((v) => v.page === 'M');
  step('CSP 正向對照：插入 inline script 被擋', cspBefore === 0 && inlineRan === false && cspAfter.length === 1 && /^script-src/.test(cspAfter[0].directive) && cspAfter[0].blocked === 'inline',
    `分頁 M 對照前違規 ${cspBefore} 筆；inline script ${inlineRan ? '竟然執行了' : '沒有執行'}，之後 ${cspAfter.length} 筆（${cspAfter[0]?.directive ?? '—'}）`, { cspBefore, inlineRan, cspAfter });
  const errors = consoleLog.filter((m) => m.type === 'error' || m.type === 'pageerror');
  const unexpected = errors.filter((m) => !expectedConsole(m));
  const unexpectedHttp = httpErrors.filter((e) => !expectedHttp(e));
  const otherCsp = cspLog.filter((v) => !(v.page === 'M' && /^script-src/.test(v.directive) && v.blocked === 'inline'));
  step('主控台只有預期的錯誤', unexpected.length === 0 && unexpectedHttp.length === 0 && otherCsp.length === 0,
    `各分頁的主控台錯誤 ${errors.length} 筆，排除預期的之後 ${unexpected.length} 筆；≥400 的回應 ${httpErrors.length} 筆（非預期 ${unexpectedHttp.length}）；CSP 違規 ${cspLog.length} 筆（正向對照以外 ${otherCsp.length}）`,
    { console: errors.map((m) => ({ ...m, expected: expectedConsole(m) })), http: httpErrors.map((e) => ({ ...e, expected: expectedHttp(e) })), csp: cspLog });
} catch (e) {
  step('例外', false, String(e?.message ?? e), { stack: String(e?.stack ?? e) });
} finally {
  if (browser) {
    const closed = await Promise.race([browser.close().then(() => true, () => false), sleep(5000).then(() => false)]);
    if (!closed) step('關閉瀏覽器', false, 'browser.close() 5 秒內沒有返回');
  }
  await cleanupDevice();
  const last = hubRuns.at(-1);
  if (last && !last.stopped) {
    const s = await stopHub(last);
    step('停止 MyriagentHub', s.ok, s.summary);
  }
  if (hubRuns.length > 0) {
    await sleep(1000);
    checkKeys();
  }
  if (lockTimer) clearInterval(lockTimer);
  if (lockHeld) {
    const released = releaseLock();
    if (!released.ok) step('釋放序列化鎖', false, released.message);
  }
  scanForPasswords();
  result.finishedAt = new Date().toISOString();
  result.ok = result.steps.every((s) => s.ok !== false);
  save();
  const onDisk = leaks(expandJson(fs.readFileSync(outFile, 'utf8')));
  if (onDisk.length > 0) {
    result.ok = false;
    console.log(`[失敗] 結果檔寫出後出現密碼（${onDisk.join('、')}）`);
  }
  console.log(result.ok ? `全部通過，結果檔：${outFile}` : `有檢查失敗，結果檔：${outFile}`);
  process.exit(result.ok ? 0 : 1);
}
}

// ───── XSS 語料（Markdown：電腦以 Markdig 渲染、伺服器淨化、網頁顯示）─────

/**
 * 200 筆以上：危險網址的連結與圖片、白名單以外的標籤（Markdig 停用原始 HTML：當成文字）、不合語法的寫法、Markdown 特有的寫法。
 * 每筆第一段是編號（XSS-001…），控制字元在執行時組（原始檔裡不放看不見的字元）；不放 NUL（同步協定的 JSON 不收）
 */
function xssCorpus() {
  const ch = String.fromCharCode;
  const TAB = ch(9);
  const LF = ch(10);
  const CR = ch(13);
  const BS = ch(92);
  const SOH = ch(1);
  const urls = [
    'javascript:alert(1)', 'JaVaScRiPt:alert(1)', 'JAVASCRIPT:alert(1)', ' javascript:alert(1)', TAB + 'javascript:alert(1)', 'java' + TAB + 'script:alert(1)',
    'java' + LF + 'script:alert(1)', 'java' + CR + 'script:alert(1)', SOH + 'javascript:alert(1)', '&#106;avascript:alert(1)', '&#x6A;avascript:alert(1)',
    '&#X6a;avascript:alert(1)', '&#0000106;avascript:alert(1)', '&#106;&#97;&#118;&#97;&#115;&#99;&#114;&#105;&#112;&#116;&#58;alert(1)', 'java&#x09;script:alert(1)',
    'java&#10;script:alert(1)', 'java&#0;script:alert(1)', '&#14;javascript:alert(1)', 'javascript&colon;alert(1)', 'javascript:alert&lpar;1&rpar;',
    'javascript:alert(1)//https://example.com', 'vbscript:msgbox(1)', 'VBScript:msgbox(1)', 'livescript:alert(1)', 'data:text/html,<script>alert(1)</script>',
    'data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==', 'data:image/svg+xml;base64,PHN2ZyBvbmxvYWQ9YWxlcnQoMSk+', 'DATA:text/html,x', '//evil.example/x',
    BS + BS + 'evil' + BS + 'share' + BS + 'a.png', BS + '/evil.example', '/' + BS + 'evil.example', '///evil.example', 'file:///C:/Windows/win.ini', 'jar:https://evil.example!/',
    'feed:javascript:alert(1)', 'x-javascript:alert(1)', '/api/v1/ops/status', '/m/../api/v1/ops/status', '/m/%2e%2e/api', '/t/x/../../api', '/f//evil.example',
    '/m/x' + BS + '..' + BS + 'api', '/M/x', 'ftp://evil.example/x', 'tel:123', 'about:blank', 'chrome://settings', 'javascript:window.__xss=1',
  ];
  const html = [
    '<script>alert(1)</script>', '<script src="https://evil.example/x.js"></script>', '前<script>alert(1)</script>後', '<style>body{background:url(javascript:alert(1))}</style>',
    '<iframe src="javascript:alert(1)"></iframe>', '<iframe srcdoc="&lt;script&gt;alert(1)&lt;/script&gt;"></iframe>', '<svg onload="alert(1)"></svg>', '<svg/onload=alert(1)>',
    '<svg><a href="javascript:alert(1)"><text>x</text></a></svg>', '<svg><animate attributename="href" to="javascript:alert(1)" /></svg>', '<math href="javascript:alert(1)">x</math>',
    '<object data="javascript:alert(1)"></object>', '<embed src="javascript:alert(1)" />', '<base href="https://evil.example/" />', '<form action="https://evil.example"><button>送出</button></form>',
    '<button formaction="javascript:alert(1)">x</button>', '<input type="image" src="javascript:alert(1)" />', '<input autofocus onfocus="alert(1)">', '<textarea>x</textarea>',
    '<meta http-equiv="refresh" content="0;url=javascript:alert(1)" />', '<link rel="stylesheet" href="https://evil.example/x.css" />', '<video src="/m/x" onerror="alert(1)"></video>',
    '<audio src="x" onerror="alert(1)"></audio>', '<details open ontoggle="alert(1)">x</details>', '<marquee onstart="alert(1)">x</marquee>', '<xmp><img src="x" onerror="alert(1)"></xmp>',
    '<noscript><p title="</noscript><img src=x onerror=alert(1)>">x</p></noscript>', '<div onclick="alert(1)">x</div>', '<a href="javascript:alert(1)">x</a>', '<img src=x onerror=window.__xss=1>',
    '<p onmouseover="alert(1)">文字</p>', '<span style="background:url(javascript:alert(1))">x</span>', '<table><tr><td onclick="alert(1)">x</td></tr></table>',
    '<img src="/m/ok" srcset="javascript:alert(1)" alt="圖">', '<h1 onclick="alert(1)">標題</h1>', '<template><img src=x onerror=alert(1)></template>', '<portal src="https://evil.example"></portal>',
    '<frameset><frame src="javascript:alert(1)"></frameset>', '<isindex action="javascript:alert(1)">', '<keygen autofocus onfocus="alert(1)">', '<a2 href="javascript:alert(1)">x</a2>',
  ];
  const broken = [
    '<a href=javascript:alert(1)>x</a>', "<a href='javascript:alert(1)'>x</a>", '<a href="javascript:alert(1)>x</a>', XSS_IMG, '<a href="#"onclick="alert(1)">x</a>',
    '<!--<script>alert(1)</script>-->', '<!DOCTYPE html><p>x</p>', '<?xml version="1.0"?><p>x</p>', '<![CDATA[<script>alert(1)</script>]]>', '<a' + LF + 'href="javascript:alert(1)">x</a>',
    '< script>alert(1)</script>', '<script/src="https://evil.example/x.js"></script>', '<SCRIPT>alert(1)</SCRIPT>', '<ScRiPt>alert(1)</sCrIpT>', '<IMG SRC="javascript:alert(1)">',
    '<a HREF="javascript:alert(1)">x</a>', 'a > b < c', 'a & b', '&unknown;', '&#106', '&#xZZ;', '&#99999999;', '<a href="#">x</a', 'x<', '<img src="/m/ok" alt="<script>alert(1)</script>" />',
    '<p title="x>y">x</p>', '</p onclick="alert(1)">', '<xss:script>alert(1)</xss:script>', '<img src="/m/ok" alt="a"/onerror=alert(1)>', '<p' + TAB + '>x</p>',
    '<a href="#" title="x" title="y">x</a>', '<img """><script>alert(1)</script>">', '<<script>alert(1)//<</script>', '<scr<script>ipt>alert(1)</scr</script>ipt>',
    '"><img src=x onerror=alert(1)>', "'><svg/onload=alert(1)>", '</textarea><script>alert(1)</script>', '</title><script>alert(1)</script>', '--><script>alert(1)</script>',
  ];
  const md = [
    '[x](javascript:alert(1) "標題")', '[x]( javascript:alert(1) )', '[x](<javascript:alert(1)>)', '[x][r1]' + LF + LF + '[r1]: javascript:alert(1)', '![x][r2]' + LF + LF + '[r2]: javascript:alert(1)',
    '<javascript:alert(1)>', '<vbscript:msgbox(1)>', '<data:text/html,x>', '[x](https://example.com "onmouseover=alert(1)")', "[x](https://example.com '\"><img src=x onerror=alert(1)>')",
    '![x" onerror="alert(1)](https://example.com/a.png)', '[![x](javascript:alert(1))](javascript:alert(2))', '*[x](javascript:alert(1))*', '[a](<javascript:alert(1)> "t")',
    '[x](&#106;avascript:alert(1))', '[x](javas%63ript:alert(1))', '[x](JaVaScRiPt:alert(1))', 'www.evil.example/' + XSS_IMG, 'https://example.com/"onmouseover="alert(1)',
    '> <script>alert(1)</script>', '- [ ] ' + XSS_IMG, '1. <iframe src=javascript:alert(1)>', '**' + XSS_IMG + '**', '~~<svg/onload=alert(1)>~~',
    '```html' + LF + '<script>alert(1)</script>' + LF + '```', '```" onmouseover="alert(1)' + LF + 'x' + LF + '```', '    <script>alert(1)</script>',
    '<details><summary>x</summary>' + XSS_IMG + '</details>', '[^1]' + LF + LF + '[^1]: ' + XSS_IMG, '| a | b |' + LF + '|---|---|' + LF + '| ' + XSS_IMG + ' | [x](javascript:alert(1)) |',
    '&lt;img src=x onerror=alert(1)&gt;', '`' + XSS_IMG + '`',
  ];
  const texts = [];
  for (const u of urls) {
    texts.push(`[點我](${u})`);
    texts.push(`![圖](${u})`);
  }
  texts.push(...html, ...broken, ...md);
  return texts.map((t, i) => {
    const label = 'XSS-' + String(i + 1).padStart(3, '0');
    return { label, text: label + LF + LF + t };
  });
}

// ───── 頁面初始化（每個頁面、每次導覽：頁面的任何腳本之前）─────

function pageInit() {
  // CSP 違規收集器（exposeBinding 送回 Node 端：分頁導覽之後也不會遺失）
  document.addEventListener('securitypolicyviolation', (e) => {
    const v = { directive: e.violatedDirective, blocked: e.blockedURI, source: e.sourceFile, line: e.lineNumber, href: location.href };
    if (typeof window.__pgCsp === 'function') window.__pgCsp(v);
  });
  // 剪貼簿：換成假的（allow 時記下並成功；deny 時照瀏覽器沒有授權的樣子丟 NotAllowedError）。不碰這台電腦真的剪貼簿
  window.__e2eClipboard = 'allow';
  window.__clipboard = [];
  const fake = {
    writeText(text) {
      const ok = window.__e2eClipboard !== 'deny';
      window.__clipboard.push({ at: Date.now(), text: String(text), ok });
      return ok ? Promise.resolve() : Promise.reject(new DOMException('端到端：剪貼簿被拒絕', 'NotAllowedError'));
    },
    readText() { return Promise.reject(new DOMException('端到端：不讀剪貼簿', 'NotAllowedError')); },
  };
  try { Object.defineProperty(Navigator.prototype, 'clipboard', { configurable: true, get() { return fake; } }); } catch { /* 不支援就算了 */ }
  // 傳輸層掛上全域時先註冊一個記錄訂閱者（在 app.js 之前）：每則訊息只記比對要用的欄位
  window.__e2e = { msgs: [] };
  const pick = (m) => {
    const r = { t: Date.now(), type: m && m.type };
    if (!m || typeof m !== 'object') return r;
    const itemOf = (i) => ({ id: i.id, kind: i.kind, status: i.status, time: i.time, digest: typeof i.requestDigest === 'string' ? i.requestDigest : null });
    const listOf = (items) => (Array.isArray(items) ? items.map((c) => ({ id: c.id, title: c.title, deviceId: c.deviceId, unread: c.unread, running: c.running, waiting: c.waiting })) : null);
    switch (m.type) {
      case 'init': r.build = m.app && m.app.build; r.list = listOf(m.conversations); break;
      case 'conversations': r.list = listOf(m.items); break;
      case 'conversation': r.c = m.conversationId; r.paneId = m.paneId; r.hasMore = m.hasMore; r.isDraft = !!(m.header && m.header.isDraft); r.items = (m.items || []).map(itemOf);
        r.header = m.header ? { provider: m.header.provider ?? null, steer: m.header.steer ?? null, permission: m.header.permission ?? null,
          modes: Array.isArray(m.header.permissionModes) ? m.header.permissionModes.map((x) => x && x.value) : null } : null;
        if (m.composer) r.queue = Array.isArray(m.composer.queue) ? m.composer.queue.length : 0;
        break;
      case 'composer': r.c = m.conversationId; r.queue = m.composer && Array.isArray(m.composer.queue) ? m.composer.queue.length : 0; break;
      case 'olderItems': r.c = m.conversationId; r.n = (m.items || []).length; r.hasMore = m.hasMore; break;
      case 'item': r.c = m.conversationId; if (m.item) Object.assign(r, itemOf(m.item)); break;
      case 'commandResult': Object.assign(r, { commandId: m.commandId, state: m.state, code: m.code, origin: m.origin || null }); break;
      case 'conversationClosed': r.c = m.conversationId; r.replacedBy = m.replacedBy || null; break;
      case 'notify': Object.assign(r, { c: m.conversationId, kind: m.kind, title: m.title, deviceName: m.deviceName }); break;
      case 'clipboard': Object.assign(r, { c: m.conversationId, itemId: m.itemId, text: m.text }); break;
      case 'files': Object.assign(r, { c: m.conversationId, path: m.path, root: m.root, error: m.error, entries: (m.entries || []).map((e) => ({ name: e.name, path: e.path, kind: e.kind, url: e.url || null })) }); break;
      case 'fileRef': Object.assign(r, { c: m.conversationId, url: m.url || null, name: m.name || null, error: m.error || null }); break;
      case 'connection': r.state = m.state; break;
      case 'toast': r.text = m.text; break;
      default: break;
    }
    return r;
  };
  let th;
  Object.defineProperty(window, 'myriagentHubTransport', {
    configurable: true,
    enumerable: true,
    get() { return th; },
    set(v) {
      th = v;
      try {
        if (v && typeof v.onMessage === 'function') v.onMessage((m) => window.__e2e.msgs.push(pick(m)));
      } catch (e) {
        window.__e2e.recorderError = String(e);
      }
    },
  });
}

// ───── 分頁 ─────

/** 版面（存在瀏覽器的 localStorage，myriagent.app.layout.v1）：一個窗格、聊天檢視 */
function layout(tabs, active = null) {
  return { panes: [{ id: 'p1', tabs, active }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' };
}

/**
 * 開一個 /app/ 分頁：layout 給了就在頁面的腳本之前寫進 localStorage（每個分頁只寫第一次；undefined＝用瀏覽器存下的，例如另一個分頁的版面）；
 * o.route：在導覽之前掛上的 page.routeWebSocket。回 { page, label, live }
 */
async function openApp(label, lay, o = {}) {
  const page = await context.newPage();
  watch(page, label);
  trackWs(page, label);
  if (o.route) await o.route(page);
  if (lay !== undefined) {
    await page.addInitScript(([key, text]) => {
      try {
        if (location.pathname === '/app/' && !sessionStorage.getItem('e2e.layout')) {
          localStorage.setItem(key, text);
          sessionStorage.setItem('e2e.layout', '1');
        }
      } catch { /* 存不了就照瀏覽器存下的 */ }
    }, [LAYOUT_KEY, JSON.stringify(lay)]);
  }
  await page.goto(`${base}/app/`, { waitUntil: 'load' });
  const live = await waitAppStarted(page);
  return { page, label, live };
}

async function closeApp(app) {
  await Promise.race([app.page.close().catch(() => {}), sleep(5000)]);
}

function watch(page, label) {
  pageLabels.set(page, label);
  pageNavs.set(label, 0);
  page.on('framenavigated', (f) => { if (f === page.mainFrame()) pageNavs.set(label, (pageNavs.get(label) ?? 0) + 1); });
  page.on('console', (m) => consoleLog.push({ page: label, at: Date.now(), type: m.type(), text: m.text(), url: m.location()?.url ?? '' }));
  page.on('pageerror', (e) => consoleLog.push({ page: label, at: Date.now(), type: 'pageerror', text: String(e?.message ?? e), url: '' }));
  page.on('response', (r) => {
    let p = '';
    try { p = new URL(r.url()).pathname; } catch { p = r.url(); }
    const row = { page: label, at: Date.now(), method: r.request().method(), status: r.status(), path: p };
    if (r.request().method() === 'PUT') httpLog.push(row);
    if (r.status() >= 400) httpErrors.push(row);
  });
  // 任何對話框（alert 等）都是 XSS 或非預期的 confirm 的證據：記下來並關掉
  page.on('dialog', (d) => {
    dialogs.push({ page: label, type: d.type(), message: d.message() });
    d.dismiss().catch(() => {});
  });
}
const httpLog = [];

/** 瀏覽器 hub 的 SignalR 框：Cmd 呼叫（送出）、呼叫的回傳與 commandResult（收到），依到達順序編號 */
function trackWs(page, label) {
  page.on('websocket', (ws) => {
    if (!ws.url().includes(HUB_PATH)) return;
    ws.on('framesent', (f) => onFrames(label, 'out', f.payload));
    ws.on('framereceived', (f) => onFrames(label, 'in', f.payload));
  });
}

function onFrames(label, dir, payload) {
  if (typeof payload !== 'string') return;
  for (const part of payload.split(RS)) {
    if (!part) continue;
    let o;
    try { o = JSON.parse(part); } catch { continue; }
    const base = { seq: ++frameSeq, t: Date.now(), page: label, dir };
    if (dir === 'out' && o.type === 1 && o.target === 'Cmd') {
      const a = (o.arguments && o.arguments[0]) || {};
      const c = a.cmd || {};
      frames.push({ ...base, kind: 'cmd', inv: o.invocationId ?? null, commandId: a.commandId, cmd: c.cmd, conv: c.conversationId ?? null, digest: c.requestDigest ?? null, text: c.text ?? null,
        uploads: c.uploads ?? null, path: c.path ?? null, dirsOnly: c.dirsOnly ?? null, itemId: c.itemId ?? null, deviceId: c.deviceId ?? null, accountId: c.accountId ?? null });
    } else if (dir === 'in' && o.type === 3) {
      frames.push({ ...base, kind: 'completion', inv: o.invocationId, result: o.result ?? null, error: o.error ?? null });
    } else if (dir === 'in' && o.type === 1 && o.target === 'Msg') {
      const m = (o.arguments && o.arguments[0]) || {};
      if (m.type === 'commandResult') frames.push({ ...base, kind: 'result', commandId: m.commandId, state: m.state, code: m.code ?? null });
    }
  }
}

/** 一次 Cmd 的回傳與 commandResult 哪個先到（同一條 WebSocket 的到達順序） */
function orderOf(cmd) {
  const done = frames.find((f) => f.page === cmd.page && f.kind === 'completion' && f.inv === cmd.inv && f.seq > cmd.seq);
  const res = frames.find((f) => f.page === cmd.page && f.kind === 'result' && f.commandId === cmd.commandId);
  if (!done || !res) return `Cmd 的回傳${done ? '有' : '沒有'}、commandResult ${res ? '有' : '沒有'}`;
  return res.seq < done.seq ? 'commandResult 先到' : 'Cmd 的回傳先到';
}

// 分頁 C、C2 的 WebSocket 轉送（page.routeWebSocket）：可以扣住下一次 send 的 Cmd 回傳直到它的 commandResult 送進頁面，或丟掉下一次 send 的 commandResult
const routeState = { holdNextSend: false, dropNextResult: false, held: new Set(), drop: new Set(), seen: new Set(), log: [] };
async function routeC(page) {
  await page.routeWebSocket((url) => url.pathname === HUB_PATH, (ws) => {
    const server = ws.connectToServer();
    const invToCmd = new Map();
    const stash = new Map();
    ws.onMessage((msg) => {
      if (typeof msg === 'string') {
        for (const part of msg.split(RS)) {
          if (!part) continue;
          let o = null;
          try { o = JSON.parse(part); } catch { continue; }
          if (o.type === 1 && o.target === 'Cmd' && o.arguments?.[0]?.cmd?.cmd === 'send') {
            const id = o.arguments[0].commandId;
            invToCmd.set(o.invocationId, id);
            if (routeState.holdNextSend) { routeState.holdNextSend = false; routeState.held.add(id); }
            if (routeState.dropNextResult) { routeState.dropNextResult = false; routeState.drop.add(id); }
            routeState.log.push({ t: Date.now(), what: 'send', commandId: id, inv: o.invocationId });
          }
        }
      }
      server.send(msg);
    });
    server.onMessage((msg) => {
      if (typeof msg !== 'string') { ws.send(msg); return; }
      const out = [];
      const after = [];
      for (const part of msg.split(RS)) {
        if (!part) continue;
        let o = null;
        try { o = JSON.parse(part); } catch { out.push(part); continue; }
        if (o.type === 3 && invToCmd.has(o.invocationId)) {
          const id = invToCmd.get(o.invocationId);
          if (routeState.held.has(id) && !routeState.seen.has(id)) {
            stash.set(id, part);
            routeState.log.push({ t: Date.now(), what: 'heldCompletion', commandId: id });
            // 保險：10 秒內沒有 commandResult 也放
            setTimeout(() => {
              if (!stash.has(id)) return;
              const p = stash.get(id);
              stash.delete(id);
              routeState.log.push({ t: Date.now(), what: 'releasedCompletionTimeout', commandId: id });
              try { ws.send(p + RS); } catch { /* 連線已經關了 */ }
            }, 10000);
            continue;
          }
        }
        if (o.type === 1 && o.target === 'Msg' && o.arguments?.[0]?.type === 'commandResult') {
          const id = o.arguments[0].commandId;
          if (routeState.drop.has(id)) { routeState.log.push({ t: Date.now(), what: 'droppedResult', commandId: id }); continue; }
          routeState.seen.add(id);
          if (routeState.held.has(id)) routeState.log.push({ t: Date.now(), what: 'result', commandId: id });
          out.push(part);
          if (stash.has(id)) {
            after.push(stash.get(id));
            stash.delete(id);
            routeState.log.push({ t: Date.now(), what: 'releasedCompletion', commandId: id });
          }
          continue;
        }
        out.push(part);
      }
      const all = out.concat(after);
      if (all.length) ws.send(all.map((p) => p + RS).join(''));
    });
  });
}

function waitReady(page) {
  return page.waitForFunction(() => window.__hubProbe && window.__hubProbe.ready === true, null, { timeout: 20000 });
}

/** /app/ 跑起來：#app.ready、傳輸層 online 而且這條連線收到 init（狀態列隱藏）、沒有 no-host */
async function waitAppStarted(page) {
  const ok = await waitFor(() => page.evaluate(() => {
    const st = window.myriagentHubTransport && window.myriagentHubTransport.status();
    const bar = document.querySelector('.hub-status');
    return document.getElementById('app')?.classList.contains('ready') && st && st.state === 'online' && st.hubInit === true
      && !!bar && bar.hidden && !document.querySelector('.no-host') ? true : null;
  }).catch(() => null), 20000);
  return !!ok;
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

/** 頁面內的同源 GET（帶 cookie）：{status, headers, text} */
function pageFetch(app, url) {
  return app.page.evaluate(async (u) => {
    const r = await fetch(u, { credentials: 'same-origin', cache: 'no-store' });
    const headers = {};
    r.headers.forEach((v, k) => { headers[k] = v; });
    return { status: r.status, headers, text: await r.text() };
  }, url).catch((e) => ({ status: null, headers: {}, text: String(e?.message ?? e) }));
}

/** 頁面直接呼叫 myriagentHubTransport.post（不經 app.js）：回 commandId（沒送出是 null） */
const tpost = (app, msg) => app.page.evaluate((m) => window.myriagentHubTransport.post(m), msg).catch(() => null);
const recs = (app, from = 0) => app.page.evaluate((f) => (window.__e2e ? window.__e2e.msgs.slice(f) : []), from).catch(() => []);
const msgCount = (app) => app.page.evaluate(() => (window.__e2e ? window.__e2e.msgs.length : 0)).catch(() => 0);
const waitResult = (app, commandId, ms) => waitFor(async () => (await recs(app)).find((m) => m.type === 'commandResult' && m.commandId === commandId) ?? null, ms);
/** 最新的對話清單（init 或 conversations） */
const latestList = async (app) => (await recs(app)).filter((m) => (m.type === 'init' || m.type === 'conversations') && Array.isArray(m.list)).at(-1)?.list ?? null;
/** 這個對話最近一次的 conversation（open 的回覆） */
const snapshotOf = async (app, id) => (await recs(app)).filter((m) => m.type === 'conversation' && m.c === id).at(-1) ?? null;
const convSel = (id) => '.conv[data-conv="' + id + '"]';
const taSel = (id) => convSel(id) + ' textarea.cinput';
const cardSel = (convId, itemId) => convSel(convId) + ' .it[data-id="' + itemId + '"]';

/** 側欄清單的每一列：標題、電腦徽章（顯示、名稱、在線）、未讀點、列裡的 img 數 */
const sidebarRows = (app) => app.page.evaluate(() => [...document.querySelectorAll('.sb-list .crow')].map((r) => {
  const b = r.querySelector('.crow-dev');
  return {
    title: r.querySelector('.crow-title').textContent, shown: !!b && !b.hidden, name: b ? b.querySelector('.crow-dev-name').textContent : null,
    online: b ? b.dataset.online : null, unread: !!r.querySelector('.dot-unread'), imgs: r.querySelectorAll('img').length,
  };
})).catch(() => []);

/** 點側欄標題正好是 title 的那一列（真的滑鼠點擊） */
async function clickRow(app, title) {
  await app.page.evaluate(() => { const b = document.querySelector('#viewSwitch [data-view="chat"]'); if (b && !b.classList.contains('on') && !b.getAttribute('aria-pressed')) b.click(); }).catch(() => {});
  const row = app.page.locator('.sb-list .crow').filter({ has: app.page.locator('.crow-title', { hasText: new RegExp('^' + escapeRe(title) + '$') }) }).first();
  await row.click({ timeout: 10000 });
}

/** 這個對話的分頁載入了（.conv 沒有 loading）：回 true，逾時 null */
const waitLoaded = (app, id, ms) => waitFor(() => app.page.evaluate((sel) => { const r = document.querySelector(sel); return r && !r.classList.contains('loading') ? true : null; }, convSel(id)).catch(() => null), ms);

const wsState = (app) => app.page.evaluate(() => ({
  panes: [...document.querySelectorAll('#workspace .pane')].map((p) => {
    const act = p.querySelector('.tab.active');
    return { id: p.dataset.pane, tabs: [...p.querySelectorAll('.tab')].map((t) => t.dataset.key), active: act ? act.dataset.key : null };
  }),
})).catch(() => ({ panes: [] }));

/** 輸入區：字、送出鈕停用、閘門說明、結果不明的說明 */
const composerState = (app, id) => app.page.evaluate((cid) => {
  const root = document.querySelector('.conv[data-conv="' + cid + '"]');
  if (!root) return null;
  const shown = (el) => !!el && !el.hidden && getComputedStyle(el).display !== 'none';
  const gate = root.querySelector('[data-field="sendGate"]');
  const settle = root.querySelector('[data-field="sendSettle"]');
  const send = root.querySelector('.cbtn.send');
  return {
    text: root.querySelector('textarea.cinput').value, sendDisabled: !!send && send.disabled,
    gate: shown(gate) ? gate.querySelector('.cgate-text').textContent : null,
    settle: shown(settle) ? settle.querySelector('.csettle-text').textContent : null,
  };
}, id).catch(() => null);
const waitComposer = (app, id, pred, ms) => waitFor(async () => { const c = await composerState(app, id); return c && pred(c) ? c : null; }, ms);

const typeInto = (app, id, text) => app.page.evaluate(([cid, t]) => {
  const ta = document.querySelector('.conv[data-conv="' + cid + '"] textarea.cinput');
  ta.value = t;
  ta.dispatchEvent(new Event('input', { bubbles: true }));
}, [id, text]);

async function typeAndEnter(app, id, text) {
  await app.page.bringToFront();
  await app.page.fill(taSel(id), text);
  await app.page.press(taSel(id), 'Enter');
}

/** 一張卡片：等你回應、送出中、按鈕全停用／全可按 */
const cardState = (app, convId, itemId) => app.page.evaluate((sel) => {
  const n = document.querySelector(sel);
  if (!n) return null;
  const btns = [...n.querySelectorAll('button')];
  return { waiting: n.classList.contains('waiting'), sending: n.classList.contains('sending'), enabled: btns.length > 0 && btns.every((b) => !b.disabled) };
}, cardSel(convId, itemId)).catch(() => null);

/** 草稿的電腦晶片 */
const draftChip = (app, id) => app.page.evaluate((cid) => {
  const c = document.querySelector('.conv[data-conv="' + cid + '"] [data-field="draftDevice"]');
  return c && !c.hidden ? { shown: true, text: c.querySelector('.chip-label').textContent, online: c.dataset.online } : null;
}, id).catch(() => null);

const domToasts = (app) => app.page.evaluate(() => [...document.querySelectorAll('.toasts .toast-text')].map((e) => e.textContent)).catch(() => []);

/** Assistant 項目的「複製」（滑鼠移到項目上才出現） */
async function clickCopy(app, convId, itemId) {
  await app.page.bringToFront();
  await app.page.hover(cardSel(convId, itemId));
  await app.page.click(cardSel(convId, itemId) + ' .it-acts .code-copy');
}
const copyBtn = (app, convId, itemId) => app.page.evaluate((sel) => {
  const b = document.querySelector(sel + ' .it-acts .code-copy');
  return b ? { text: b.lastChild.textContent, busy: b.classList.contains('busy'), done: b.classList.contains('done') } : null;
}, cardSel(convId, itemId)).catch(() => null);
const fallbackState = (app) => app.page.evaluate(() => {
  const d = document.querySelector('.dlg-copy-fallback');
  if (!d) return null;
  const ta = d.querySelector('textarea');
  return { value: ta ? ta.value : null, readOnly: !!ta && ta.readOnly, imgs: d.querySelectorAll('img').length };
}).catch(() => null);

/** 輸入區的上傳晶片 */
const uploadState = (app, id) => app.page.evaluate((cid) => {
  const root = document.querySelector('.conv[data-conv="' + cid + '"]');
  if (!root) return null;
  const box = root.querySelector('[data-field="uploads"]');
  return { chips: box ? [...box.querySelectorAll('.up-chip')].map((c) => ({ state: c.dataset.state, name: c.querySelector('.att-name').textContent })) : [] };
}, id).catch(() => null);
const untilChips = (app, id, pred, ms) => waitFor(async () => { const s = await uploadState(app, id); return s && pred(s) ? s : null; }, ms);

/** 檔案抽屜：挑選模式、項目名稱、挑選的路徑、「選這個資料夾」停用 */
const drawerState = (app, id) => app.page.evaluate((cid) => {
  const d = document.querySelector('.conv[data-conv="' + cid + '"] .drawer');
  if (!d) return null;
  const pick = d.querySelector('[data-field="pickFolder"]');
  const pp = d.querySelector('[data-field="pickPath"]');
  return {
    open: d.classList.contains('open'), picking: d.classList.contains('picking'), names: [...d.querySelectorAll('.dr-body .fe .fe-name')].map((n) => n.textContent),
    pickPath: pp ? pp.textContent : '', pickDisabled: !!pick && pick.disabled,
  };
}, id).catch(() => null);
const drawerNames = async (app, id) => { const d = await drawerState(app, id); return d && d.open ? d.names : null; };
const clickEntry = (app, id, name) => app.page.evaluate(([sel, n]) => {
  const fe = [...document.querySelectorAll(sel + ' .drawer .dr-body .fe')].find((x) => x.querySelector('.fe-name').textContent === n);
  if (fe) fe.click();
  return !!fe;
}, [convSel(id), name]);
const fileRefDialog = (app) => app.page.evaluate(() => {
  const d = document.querySelector('.dlg-file-ref');
  if (!d) return null;
  const a = d.querySelector('[data-field="fileRefOpen"]');
  const err = d.querySelector('[data-field="fileRefError"]');
  return { link: a ? { href: a.getAttribute('href'), rel: a.getAttribute('rel'), target: a.getAttribute('target') } : null, error: err ? err.textContent : null };
}).catch(() => null);

/** XSS 的痕跡：注入的 img、事件屬性、危險網址、window.__xss（各分頁的 /app/） */
async function domXssScan(app) {
  // 範圍是 body（head 裡網頁自己的 <link> 不算）
  const s = await app.page.evaluate(() => {
    const all = [...document.body.querySelectorAll('*')];
    const onAttr = all.filter((el) => [...el.attributes].some((a) => /^on/i.test(a.name))).map((el) => el.tagName.toLowerCase() + '[' + [...el.attributes].filter((a) => /^on/i.test(a.name)).map((a) => a.name).join(',') + ']');
    const bad = /^\s*(javascript|vbscript|data|livescript|file):/i;
    const urls = all.flatMap((el) => ['href', 'src', 'srcset', 'action', 'formaction', 'xlink:href'].map((n) => [el, el.getAttribute(n)]))
      .filter(([, v]) => typeof v === 'string' && bad.test(v)).map(([el, v]) => el.tagName.toLowerCase() + ' ' + v.slice(0, 40));
    const tags = ['script:not([src])', 'iframe', 'object', 'embed', 'svg [onload]', 'base', 'meta[http-equiv]', 'form'].map((q) => [q, document.querySelectorAll('#app ' + q).length]).filter(([, n]) => n > 0);
    return { imgX: document.querySelectorAll('img[src="x"]').length, onAttr: onAttr.slice(0, 10), onCount: onAttr.length, urls: urls.slice(0, 10), urlCount: urls.length, tags, xss: typeof window.__xss };
  }).catch((e) => ({ error: String(e?.message ?? e) }));
  const ok = !s.error && s.imgX === 0 && s.onCount === 0 && s.urlCount === 0 && s.tags.length === 0 && s.xss === 'undefined';
  return { ...s, ok, summary: s.error ? `掃描失敗：${s.error}` : `img[src=x] ${s.imgX} 個、on* 屬性 ${s.onCount} 個、危險網址 ${s.urlCount} 個、可執行的標籤 ${s.tags.length} 種、window.__xss ${s.xss}` };
}
const xssState = (app) => domXssScan(app);

/** SEC-23 的正向對照：同一段 HTML 放進沒有 CSP 的空白頁，偵測器要看得到它執行 */
async function secControl(html) {
  const page = await context.newPage();
  const label = 'SEC-CTRL';
  watch(page, label);
  await page.setContent(html, { waitUntil: 'load' }).catch(() => {});
  await sleep(800);
  const marks = consoleLog.filter((m) => m.page === label && m.text.includes(SEC23_MARK)).length;
  const dlg = dialogs.filter((d) => d.page === label).length;
  await Promise.race([page.close().catch(() => {}), sleep(3000)]);
  return { ran: marks > 0 && dlg > 0, marks, dialogs: dlg };
}

/** SEC-23：新分頁直接開媒體網址，記下標頭與有沒有執行（主控台的記號、對話框） */
async function secOpen(label, href) {
  if (!href) return { label, href: null, ok: false };
  const page = await context.newPage();
  watch(page, label);
  const resp = await page.goto(base + href, { waitUntil: 'load' }).catch(() => null);
  await sleep(1000);
  const h = resp ? resp.headers() : {};
  const marks = consoleLog.filter((m) => m.page === label && m.text.includes(SEC23_MARK)).length;
  const dlg = dialogs.filter((d) => d.page === label).length;
  await Promise.race([page.close().catch(() => {}), sleep(3000)]);
  const o = { label, href, status: resp ? resp.status() : null, contentType: h['content-type'] ?? null, csp: h['content-security-policy'] ?? null, nosniff: h['x-content-type-options'] === 'nosniff',
    disposition: h['content-disposition'] ?? null, marks, dialogs: dlg };
  o.ok = o.status === 200 && o.csp === MEDIA_CSP && o.nosniff && marks === 0 && dlg === 0;
  return o;
}

// ───── 預期的錯誤 ─────

/** 預期的 ≥400 回應：回原因（不是預期的回 null） */
function expectedHttp(e) {
  if (e.page === 'L' && e.status === 401 && (e.path === '/api/v1/auth/me' || e.path === '/api/v1/sessions')) return '登入前開登入頁：auth.js 查目前的登入（匿名 401）';
  if (e.page === 'M' && e.path.startsWith('/t/') && e.status >= 400) return '內容是 SVG 的 evil.png 做不出縮圖（SEC-23 的夾具）';
  return null;
}

/** 預期的主控台錯誤：回原因（不是預期的回 null） */
function expectedConsole(m) {
  if (m.page === 'M' && /Content Security Policy/.test(m.text) && /inline/i.test(m.text)) return 'CSP 正向對照（刻意插入的 inline script）';
  // SEC-23 直接開的媒體頁有 CSP sandbox（不透明來源）：瀏覽器自動要的 /favicon.ico 被 Cross-Origin-Resource-Policy 擋下
  if (/^SEC-[MF]-/.test(m.page) && /ERR_BLOCKED_BY_RESPONSE\.NotSameOrigin/.test(m.text) && /\/favicon\.ico$/.test(m.url)) return '沙箱化的媒體頁要 /favicon.ico 被 CORP same-origin 擋下';
  if (/Failed to load resource/.test(m.text) && m.url) {
    let p = null;
    try { p = new URL(m.url).pathname; } catch { p = null; }
    const status = /status of (\d{3})/.exec(m.text);
    const why = p && status ? expectedHttp({ page: m.page, path: p, status: Number(status[1]) }) : null;
    if (why) return why;
  }
  // 伺服器停機期間（admin stop 到分頁重新收到 init）：連不上的請求與 WebSocket
  if (downtime.from !== null && m.at >= downtime.from && (downtime.to === null || m.at <= downtime.to + 2000)
    && (/ERR_CONNECTION_REFUSED|ERR_CONNECTION_RESET|ERR_EMPTY_RESPONSE|ERR_CONNECTION_CLOSED/.test(m.text) || /WebSocket connection to .* failed/.test(m.text))) {
    return '伺服器停機期間連不上（重連前的嘗試）';
  }
  return null;
}

// ───── 伺服器 ─────

/** 啟動一次 MyriagentHub（同一個資料根、同一個埠）；回 { ok, summary, data } */
async function startHub() {
  const n = hubRuns.length + 1;
  const run = { n, proc: null, exit: null, log: path.join(work, n === 1 ? 'hub-console.log' : `hub-console-${n}.log`), before: new Set(listKeys()), started: null, stopped: false };
  hubRuns.push(run);
  const hubArgs = ['--data', dataDir, '--environment', 'Development', `--Hub:HttpsPort=${port}`, '--Hub:BindAddress=localhost',
    '--Hub:BelowNormalPriority=false', '--Hub:AdminInbox:PollSeconds=1', '--Hub:Backup:Enabled=false', '--Hub:SelfTest:Enabled=false',
    `--Hub:Browser:ReconnectGraceMs=${RECONNECT_GRACE_MS}`];
  const logFd = fs.openSync(run.log, 'w');
  run.proc = spawn(hubExe, hubArgs, { cwd: path.dirname(hubExe), env: childEnv(), stdio: ['ignore', logFd, logFd], windowsHide: true });
  run.proc.on('exit', (code) => { run.exit = code; });
  const started = Date.now();
  let health = null;
  while (Date.now() - started < 60000 && run.exit === null) {
    try {
      const r = await request(`${base}/healthz`);
      if (r.status === 200) { health = JSON.parse(r.body); break; }
    } catch { /* 還沒起來 */ }
    await sleep(300);
  }
  if (!health) {
    run.stopped = run.exit !== null;
    return { ok: false, summary: `第 ${n} 次啟動沒有起來（結束碼 ${run.exit}），看 ${run.log}`, data: { pid: run.proc.pid } };
  }
  run.started = new Set(listKeys());
  const imported = [...run.started].filter((k) => !run.before.has(k));
  return {
    ok: true,
    summary: `第 ${n} 次啟動 ${base}（${Date.now() - started} ms 後 /healthz 200，build ${health.build}）；啟動期間新增的金鑰檔 ${imported.length} 個（算這個 MyriagentHub 的）`,
    data: { pid: run.proc.pid, dataDir, healthz: health, importedKeys: imported, hubArgs },
  };
}

/** admin stop 一次啟動並等它結束；回 { ok, summary } */
async function stopHub(run) {
  if (run.exit !== null) {
    run.stopped = true;
    return { ok: false, summary: `伺服器提早結束（結束碼 ${run.exit}）` };
  }
  const started = Date.now();
  const admin = await runProcess(hubExe, ['admin', 'stop', '--data', dataDir, '--wait', '15'], 30000);
  while (run.exit === null && Date.now() - started < 30000) await sleep(200);
  run.stopped = true;
  if (run.exit === null) {
    run.proc.kill();
    return { ok: false, summary: 'admin stop 後 30 秒沒有結束，已強制結束（file 模式會留下孤兒金鑰檔）' };
  }
  return { ok: admin.code === 0 && run.exit === 0, summary: `admin stop 結束碼 ${admin.code}，伺服器 ${Date.now() - started} ms 後結束（結束碼 ${run.exit}）` };
}

/** 這一次啟動匯入的（啟動期間新增的）金鑰檔現在還在的 */
function keysLeft(run) {
  if (!run?.started) return [];
  const now = new Set(listKeys());
  return [...run.started].filter((k) => !run.before.has(k) && now.has(k));
}

// 每一次啟動匯入的金鑰檔與這台的裝置金鑰停止後都要不在；其他之後才出現、內容含裝置金鑰前綴的只記成觀察（同一個使用者的其他程序）
function checkKeys() {
  const per = hubRuns.filter((r) => r.started).map((r) => ({ run: r.n, mine: [...r.started].filter((k) => !r.before.has(k)), left: keysLeft(r) }));
  const names = [device.keyName].filter(Boolean);
  const hits = keyFilesContaining(names);
  const deviceLeft = names.filter((n) => hits[n].length > 0);
  step('主機副作用（孤兒金鑰檔）', per.every((p) => p.left.length === 0) && deviceLeft.length === 0,
    per.map((p) => `第 ${p.run} 次啟動新增的金鑰檔 ${p.mine.length} 個、停止後還在 ${p.left.length} 個`).join('；')
      + `；這台的裝置金鑰 ${names.length} 把，解除配對後還在 ${deviceLeft.length} 把（${keysDir}）`, { per, deviceKeys: names, deviceLeft });
  const known = new Set(hubRuns.flatMap((r) => [...(r.before ?? []), ...(r.started ?? [])]));
  const prefix = Buffer.from(DEVICE_KEY_PREFIX, 'utf16le');
  const others = listKeys().filter((k) => !known.has(k)).filter((k) => {
    try { return fs.readFileSync(path.join(keysDir, k)).includes(prefix); } catch { return false; }
  });
  if (others.length > 0) step('其他程序的裝置金鑰檔', null, `執行期間出現、現在還在、含 ${DEVICE_KEY_PREFIX} 但不是這台的金鑰檔 ${others.length} 個：只記錄、不算失敗`, { others });
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

function touchLock() {
  try {
    const owner = JSON.parse(fs.readFileSync(lockFile, 'utf8'));
    if (owner?.pid === process.pid) {
      const now = new Date();
      fs.utimesSync(lockFile, now, now);
    }
  } catch { /* 讀不到就算了：結束時 releaseLock 會說 */ }
}

// ───── 假裝置 ─────

/** FakeDevice.exe 存在、修改時間不早於 FakeScript.cs 與 Runtime\*.cs（審查 8：忘了重建會測到舊的劇本操作） */
function fakeFreshness() {
  const src = path.join(repo, 'tools', 'fake-device');
  const sources = [path.join(src, 'FakeScript.cs'), ...listFiles(path.join(src, 'Runtime')).filter((f) => f.endsWith('.cs'))];
  if (!fs.existsSync(fakeExe)) return { ok: false, why: `找不到 ${fakeExe}`, sources: sources.length };
  const exeTime = fs.statSync(fakeExe).mtimeMs;
  const newer = sources.filter((f) => fs.statSync(f).mtimeMs > exeTime).map((f) => path.relative(repo, f));
  return { ok: newer.length === 0, exeTime, sources: sources.length, newer, why: newer.length ? `比 exe 新的原始碼：${newer.join('、')}` : null };
}

/** 背景啟動 FakeDevice（不經殼層；環境變數剝掉代理與同步相關的，extraEnv 只給這一個子程序） */
function startFake(label, data, argv, extraEnv = null) {
  const name = `${label}-${++fakeSeq}`;
  const outLog = path.join(work, `${name}.stdout.log`);
  const errLog = path.join(work, `${name}.stderr.log`);
  const env = { ...childEnv(), ...(extraEnv ?? {}) };
  const proc = spawn(fakeExe, ['--data', data, ...argv], { cwd: path.dirname(fakeExe), env, stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true });
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
  if (f.done) return { done: true, already: true, code: f.exit };
  f.proc.kill();
  let done = await waitExit(f, 10000);
  if (!done) {
    await runProcess('taskkill', ['/T', '/F', '/PID', String(f.pid)], 15000);
    done = await waitExit(f, 10000);
  }
  return { done, code: f.exit, signal: f.signal };
}

/** 跑一次假裝置的指令並等它結束（逾時就結束它）：{code, json（最後一行 JSON）, stderr 結尾} */
async function fakeOnce(label, data, argv, timeoutMs, extraEnv = null) {
  const f = startFake(label, data, argv, extraEnv);
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

/** 以同一個資料根背景跑一份劇本（實例鎖還沒放開的結束碼 3 時重試），等到 waitDrained 那一步 */
async function startRun(label, steps) {
  const file = writeScript(label, steps);
  const started = Date.now();
  let f;
  while (true) {
    f = startFake(label, device.data, ['run', '--script', file, '--dev-trust']);
    const r = await waitFor(() => (/第 \d+ 步 waitDrained/.test(f.stderr) ? 'ok' : f.done ? 'exit' : null), 90000);
    if (r === 'ok' || f.exit !== 3 || Date.now() - started > 15000) break;
    await sleep(1000);
  }
  return f;
}

function readKeyName(data) {
  for (const file of ['device.json', 'pending-key.json']) {
    const j = parseJson(readText(path.join(data, 'sync', file)));
    if (j && typeof j.keyName === 'string') return j.keyName;
  }
  return null;
}

function writeScript(label, steps) {
  const file = path.join(work, `script-${label}.json`);
  fs.writeFileSync(file, JSON.stringify({ steps }, null, 2));
  return file;
}

/** 劇本每一步的結果（假裝置印在標準錯誤：「第 N 步 op：{…}」） */
function scriptSteps(f) {
  return f.stderr.split(/\r?\n/).map((l) => /^第 (\d+) 步 (\w+)：(.*)$/.exec(l.trim())).filter(Boolean).map((m) => ({ step: Number(m[1]), op: m[2], result: parseJson(m[3]) }));
}

/** 收尾（不論成敗）：結束還在跑的假裝置、unpair（只剩 pending-key.json 時跑一次 status 讓配對清理刪掉） */
async function cleanupDevice() {
  if (fakes.length === 0) return;
  const stopped = [];
  for (const f of fakes) if (!f.done) stopped.push({ label: f.label, ...(await stopFake(f)) });
  let ok = stopped.every((s) => s.done);
  const sync = path.join(device.data, 'sync');
  device.keyName ??= readKeyName(device.data);
  let un = null;
  if (fs.existsSync(path.join(sync, 'device.json'))) {
    const r = await fakeRetry('unpair', device.data, ['unpair'], 60000);
    un = { command: 'unpair', code: r.code, attempts: r.attempts, stderr: r.code === 0 ? undefined : r.stderr };
    ok = ok && r.code === 0;
  } else if (fs.existsSync(path.join(sync, 'pending-key.json'))) {
    const r = await fakeRetry('status', device.data, ['status', '--dev-trust', '--wait', '1'], 60000);
    un = { command: 'status', code: r.code, attempts: r.attempts };
  }
  const left = fs.existsSync(path.join(sync, 'device.json')) || fs.existsSync(path.join(sync, 'pending-key.json'));
  ok = ok && !left;
  step('收尾：結束假裝置、解除配對', ok, `結束還在跑的 ${stopped.length} 個（${stopped.map((s) => `${s.label}${s.done ? '' : '沒結束'}`).join('、') || '—'}）；`
    + `${un ? `${un.command} 結束碼 ${un.code}${un.attempts > 1 ? `（試了 ${un.attempts} 次）` : ''}` : '沒有配對資料'}${left ? '、配對資料還在' : ''}`, { stopped, unpair: un });
}

// ───── 密碼掃描（照 pairing.mjs）─────

function scanForPasswords() {
  const scanned = [];
  const found = {};
  const scan = (name, text) => {
    scanned.push(name);
    for (const l of leaks(text)) (found[l] ??= []).push(name);
  };
  scan('結果檔', expandJson(JSON.stringify(result)));
  for (const r of hubRuns) scan(path.basename(r.log), readText(r.log));
  if (dataDir) for (const f of listFiles(path.join(dataDir, 'diag'))) scan(path.relative(work, f), readText(f));
  for (const f of listFiles(work).filter((x) => /\.(stdout|stderr)\.log$/.test(x))) scan(path.relative(work, f), readText(f));
  for (const f of listFiles(path.join(device.data, 'logs'))) scan(path.relative(work, f), readText(f));
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

// JSON 原文＋解開後的所有字串（含字串裡又是 JSON 的）：跳脫過的內容也搜得到
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
function request(url) {
  return new Promise((resolve, reject) => {
    const req = https.request(url, { method: 'GET', rejectUnauthorized: false, timeout: 15000, agent: false }, (res) => {
      let data = '';
      res.setEncoding('utf8');
      res.on('data', (d) => { data += d; });
      res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, body: data }));
    });
    req.on('error', reject);
    req.on('timeout', () => req.destroy(new Error('逾時')));
    req.end();
  });
}

function sha256(buf) {
  return crypto.createHash('sha256').update(buf).digest('hex');
}

/** 固定種子的位元組（xorshift32）：同一個種子每次都一樣 */
function seededBytes(seed, n) {
  let x = seed >>> 0 || 1;
  const out = Buffer.alloc(n);
  for (let i = 0; i < n; i++) {
    x ^= x << 13; x >>>= 0;
    x ^= x >>> 17;
    x ^= x << 5; x >>>= 0;
    out[i] = x & 0xff;
  }
  return out;
}

function escapeRe(s) {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
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

function listFilesDeep(dir) {
  try {
    return fs.readdirSync(dir, { withFileTypes: true, recursive: true }).filter((d) => d.isFile()).map((d) => path.join(d.parentPath ?? d.path, d.name));
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
      const { port: p } = server.address();
      server.close(() => resolve(p));
    });
  });
}

function parseArgs(list) {
  const known = new Set(['hub', 'fake-device', 'port', 'work', 'out', 'headed']);
  const flags = new Set(['headed']);
  const out = {};
  for (let i = 0; i < list.length; i++) {
    const a = list[i];
    if (!a.startsWith('--')) throw new Error(`看不懂的參數：${a}`);
    const key = a.slice(2);
    if (key === 'base') throw new Error('沒有 --base：流程會配對、改政策、重啟伺服器，只對自己啟動的伺服器跑');
    if (!known.has(key)) throw new Error(`看不懂的參數：${a}`);
    if (flags.has(key)) { out[key] = true; continue; }
    if (i + 1 >= list.length) throw new Error(`--${key} 缺少值`);
    out[key] = list[++i];
  }
  return out;
}

await main();
