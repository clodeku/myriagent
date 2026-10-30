// Myriagent Hub 版本頁（/#releases，app 自我更新 U2 的 T9；releases.js、releaserules.js、AppReleaseOwnerEndpoints）的瀏覽器端到端（OWNER-1）。
// pairing.mjs 的 13.5 只看「還沒有收到任何版本」；這支在有版本時走完擁有者的操作：
//   1. 序列化鎖（lock.mjs）→ 以暫存資料根、自己挑的高埠啟動 MyriagentHub（lib/hub-process.mjs 的 startHub：複製 bin、Testing 環境、file 憑證）
//      → admin stop（hub.db 已由遷移建好）
//   2. 種版本：伺服器只認內建的正式金鑰、不能加任何信任別的金鑰的設定（CLAUDE.md），所以停機時直接寫 hub.db 的 app_releases（node:sqlite）
//      與 <資料根>\app-releases\<release>\ 的三個檔（zip 長度、manifest 的 SHA-256、簽章檔的 kid 都跟資料列相符：AppReleaseStore.TryLoad 只比這三樣、不驗章）。
//      三版：0.2.300 可用、0.2.301 已撤回、0.2.302 可用（最新）→ 同一個資料根重新啟動（啟動對帳刪掉撤回版的資料夾、可用的兩版都留著）
//   3. 本機 Chrome（channel:'chrome'）匿名開 /#releases → 表單登入後留在版本頁 → 版本表的順序、狀態、「目前提供」、大小、提交、kid、收件時間、動作
//      → GET /api/v1/app/releases 一致 → 點「下載」存下的檔與種的 zip 位元組相同、標頭（ETag、檔名）；撤回版的套件 410
//      → 撤回 0.2.300 時在確認框按取消：沒有送出、表不變 → 撤回目前提供的 0.2.302：確認框的文字、表與「目前提供」改成 0.2.300、API 一致、套件 410
//      → 再撤回一次回 alreadyWithdrawn、不存在 404、名稱不合法 400 → 稽核只有一筆 app.release.withdrawn（owner）→ 重新整理按鈕 → 截圖
//      → CSP 0 違規、主控台與 ≥400 的回應只有預期的（次數剛好）、對話框剛好兩個
//   4. 關閉瀏覽器（加時限）、admin stop（--data）、這個 MyriagentHub 匯入的金鑰檔都已刪除、釋放鎖。
// 結果每一步先寫進結果檔（browser.close() 可能不返回，所以關閉加時限）。
//
// 用法（先 dotnet build src/MyriagentHub/MyriagentHub.csproj -c Release、cd tools/hub-e2e && npm install）：
//   node releases.mjs [--hub <MyriagentHub.exe>] [--work <TEMP 底下的資料夾>] [--out <結果檔>] [--headed]
// 結束碼：0 全部通過、1 有檢查失敗、2 用法錯誤。
import { chromium } from 'playwright-core';
import crypto from 'node:crypto';
import fs from 'node:fs';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { DatabaseSync } from 'node:sqlite';
import { fileURLToPath } from 'node:url';
import { acquireLock, lockFile, LOCK_WAIT_MS, releaseLock } from './lock.mjs';
import { newContext } from './lib/quiet-browser.mjs';
import { startLockHeartbeat } from './lib/lock-heartbeat.mjs';
import { keyFilesNaming, startHub } from './lib/hub-process.mjs';
import { isUnderTemp } from './lib/guard-rules.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '..', '..');
let args;
try {
  args = parseArgs(process.argv.slice(2));
} catch (e) {
  console.error(String(e?.message ?? e));
  console.error('用法：node releases.mjs [--hub <MyriagentHub.exe>] [--work <TEMP 底下的資料夾>] [--out <結果檔>] [--headed]');
  process.exit(2);
}
const stamp = new Date().toISOString().replace(/[-:]/g, '').replace('T', '-').slice(0, 15);
const work = path.resolve(args.work ?? path.join(os.tmpdir(), 'myriagent-tools', 'releases', stamp));
if (!isUnderTemp(work)) {
  console.error(`工作資料夾要在暫存資料夾底下：${work}`);
  process.exit(2);
}
const outFile = path.resolve(args.out ?? path.join(work, 'result.json'));
const hubExe = path.resolve(args.hub ?? path.join(repo, 'src', 'MyriagentHub', 'bin', 'Release', 'net10.0', 'win-x64', 'MyriagentHub.exe'));

const OWNER = 'admin';
// 伺服器的 OwnerAccount.DefaultPassword（首次啟動以它建立擁有者；同 lib/harness.mjs 的 DEFAULT_PASSWORD）；結果檔與主控台只寫名稱
const DEFAULT_PASSWORD = 'changeme2026';
const MIN_RUNTIME = 'Microsoft.WindowsDesktop.App 10.0.0';
const HOUR = 3600 * 1000;

fs.mkdirSync(work, { recursive: true });
fs.mkdirSync(path.dirname(outFile), { recursive: true });
const result = { tool: 'releases', startedAt: new Date().toISOString(), work, hubExe, base: null, steps: [], ok: null };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
function save() {
  fs.writeFileSync(outFile, JSON.stringify(result, null, 2));
}
function step(name, ok, summary, data) {
  result.steps.push({ name, ok, at: new Date().toISOString(), summary, ...(data === undefined ? {} : { data }) });
  save(); // 每一步先寫檔
  console.log(`[${ok === null ? '觀察' : ok ? '通過' : '失敗'}] ${name}：${summary}`);
}

// ───── 種的三個版本（zip 的大小挑成 formatBytes 的固定寫法：1536 → 1.5 KB、2000 → 2.0 KB、40960 → 40.0 KB）─────
const now = Date.now();
const SEEDS = [
  { release: '0.2.300-a1b2c3d', state: 'available', ingestedAt: now - 3 * HOUR, withdrawnAt: null, zipBytes: 40960, sizeText: '40.0 KB' },
  { release: '0.2.301-b2c3d4e', state: 'withdrawn', ingestedAt: now - 2 * HOUR, withdrawnAt: now - 90 * 60 * 1000, zipBytes: 2000, sizeText: '2.0 KB' },
  { release: '0.2.302-c3d4e5f', state: 'available', ingestedAt: now - 1 * HOUR, withdrawnAt: null, zipBytes: 1536, sizeText: '1.5 KB' },
].map((s, i) => seedPackage(s, i));
const [OLD, WITHDRAWN, NEW] = SEEDS;
// 版本頁照收件時間由新到舊
const EXPECTED_ORDER = [NEW.release, WITHDRAWN.release, OLD.release];

// 預期的 ≥400 回應（次數要剛好）：瀏覽器把它們記成主控台的「Failed to load resource」
const EXPECTED_HTTP = [
  { path: '/api/v1/auth/me', status: 401, count: 1, why: '匿名開 /#releases：auth.js 的 refresh 判斷要顯示登入頁' },
  { path: '/api/v1/sessions', status: 401, count: 1, why: '匿名時 renderAccount 同時取' },
  { path: `/api/v1/app/releases/${WITHDRAWN.release}/package`, status: 410, count: 1, why: '撤回版的套件（刻意）' },
  { path: `/api/v1/app/releases/${NEW.release}/package`, status: 410, count: 1, why: '剛撤回的套件（刻意）' },
  { path: '/api/v1/app/releases/0.2.999-abcdef0/withdraw', status: 404, count: 1, why: '撤回不存在的版本（刻意）' },
  { path: '/api/v1/app/releases/not-a-release/withdraw', status: 400, count: 1, why: '撤回名稱不合法的版本（刻意）' },
];

let lockHeld = false;
let heartbeat = null;
let hub = null;
let browser = null;
const consoleLog = [];
const httpErrors = [];
const cspLog = [];
const dialogs = [];
// 確認框的處理：依序取一個（true＝確定、false＝取消）；沒有安排的一律取消並記成非預期
const dialogPlan = [];
const withdrawPosts = [];
const pageLabels = new Map();

try {
  // ───── 1. 序列化鎖、第一次啟動（建 hub.db）─────
  const lock = await acquireLock('releases', work);
  if (!lock.ok) throw new Error(`等不到序列化鎖（${lockFile}，${(LOCK_WAIT_MS / 1000).toFixed(0)} 秒）：另一支端到端還在跑，持有者 ${lock.holder ?? '不明'}`);
  lockHeld = true;
  heartbeat = startLockHeartbeat();
  step('取得序列化鎖', true, `${lockFile}（等了 ${lock.waitedMs} ms${lock.staleRemoved ? '，清掉了超過 10 分鐘的舊鎖' : ''}）`, { waitedMs: lock.waitedMs, staleRemoved: lock.staleRemoved });

  if (!fs.existsSync(hubExe)) throw new Error(`找不到 ${hubExe}（先 dotnet build src/MyriagentHub/MyriagentHub.csproj -c Release，或用 --hub 指定）`);
  for (const f of ['releases.js', 'releaserules.js']) {
    if (!fs.existsSync(path.join(path.dirname(hubExe), 'wwwroot', f))) throw new Error(`${hubExe} 的 wwwroot 沒有 ${f}：這支要 U2 之後的建置`);
  }
  const dataDir = path.join(work, 'hub-data');
  const port = await freePort();
  const firstStart = Date.now();
  hub = await startHub({ exe: hubExe, dataDir, port });
  result.base = hub.base;
  const firstKeys = hub.instanceKeys();
  step('第一次啟動 MyriagentHub（建 hub.db）', true, `${hub.base}（${Date.now() - firstStart} ms 後 /healthz 200，Testing、暫存資料根）；這個實例匯入的金鑰檔 ${firstKeys.length} 個`,
    { pid: hub.pid, dataDir, needle: hub.needle, instanceKeys: firstKeys });
  const firstStop = await hub.stop();
  step('admin stop（--data）', !firstStop.forced && firstStop.adminCode === 0 && firstStop.keysLeft.length === 0,
    `admin stop 結束碼 ${firstStop.adminCode}${firstStop.forced ? '、30 秒內沒結束（已強制結束）' : '、正常結束'}；伺服器沒刪掉的金鑰檔 ${firstStop.keysLeft.length} 個`, firstStop);
  if (firstStop.forced) throw new Error('第一次啟動的 MyriagentHub 沒有正常停止');

  // ───── 2. 種版本（停機時）→ 重新啟動 ─────
  const seeded = seedReleases(dataDir);
  step('種三個版本（hub.db 的 app_releases＋版本資料夾）', seeded.ok, seeded.summary, seeded.data);
  if (!seeded.ok) throw new Error('種版本失敗');
  const secondStart = Date.now();
  await hub.restart();
  const releasesDir = path.join(dataDir, 'app-releases');
  const folders = SEEDS.map((s) => ({ release: s.release, exists: fs.existsSync(path.join(releasesDir, s.release)) }));
  const foldersOk = folders.every((f) => f.exists === (f.release !== WITHDRAWN.release));
  step('同一個資料根重新啟動（啟動對帳：撤回版的資料夾刪掉、可用的留著）', foldersOk,
    `${Date.now() - secondStart} ms 後 /healthz 200；版本資料夾：${folders.map((f) => `${f.release} ${f.exists ? '在' : '不在'}`).join('、')}；匯入的金鑰檔累計 ${hub.instanceKeys().length} 個`,
    { folders, instanceKeys: hub.instanceKeys() });

  // ───── 3. 瀏覽器：匿名開 /#releases → 表單登入 ─────
  browser = await chromium.launch({ channel: 'chrome', headless: !args.headed, args: ['--ignore-certificate-errors'] });
  const context = await newContext(browser, { ignoreHTTPSErrors: true, acceptDownloads: true, locale: 'zh-TW', timezoneId: 'Asia/Taipei', viewport: { width: 1440, height: 900 } });
  // CSP 違規收集器（同 pairing.mjs）：比頁面的任何腳本都早掛上，經 exposeBinding 送回 Node 端
  await context.exposeBinding('__rlCsp', (source, v) => { cspLog.push({ page: pageLabels.get(source.page) ?? '?', ...v }); });
  await context.addInitScript(() => {
    document.addEventListener('securitypolicyviolation', (e) => {
      const v = { directive: e.violatedDirective, blocked: e.blockedURI, source: e.sourceFile, line: e.lineNumber, href: location.href };
      if (typeof window.__rlCsp === 'function') window.__rlCsp(v);
    });
  });
  const P = await context.newPage();
  watch(P, 'P');
  await P.goto(`${hub.base}/#releases`, { waitUntil: 'load' });
  const ready = await P.waitForFunction(() => window.__hubProbe && window.__hubProbe.ready === true, null, { timeout: 20000 }).then(() => true, () => false);
  const anonymous = await P.evaluate(() => ({ view: window.__hubProbe?.view ?? null, releases: window.__hubProbe?.releases?.() ?? null }));
  await P.fill('#login-user', OWNER);
  await P.fill('#login-password', DEFAULT_PASSWORD);
  await P.click('#btn-login');
  const first = await waitFor(async () => {
    const s = await viewState(P);
    return s.view === 'releases' && s.probe && s.rows.length > 0 ? s : null;
  }, 15000);
  step('匿名開 /#releases → 表單登入後留在版本頁', ready && anonymous.view === 'login' && anonymous.releases === null && !!first && first.path === '/' && first.hash === '#releases',
    `登入頁${ready ? '就緒' : '沒有就緒'}、匿名時畫面 ${anonymous.view}（版本資料 ${anonymous.releases === null ? '沒有載入' : '已載入'}）；`
      + `登入後${first ? `畫面 ${first.view}、網址 ${first.path}${first.hash}、${first.rows.length} 列` : ' 15 秒內沒有畫出版本表'}`,
    { anonymous, path: first?.path ?? null, hash: first?.hash ?? null });
  if (!first) throw new Error('版本頁沒有畫出來');

  // ───── 4. 版本表 ─────
  const expectFormatted = await P.evaluate((list) => {
    const fmt = new Intl.DateTimeFormat('zh-TW', { year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hour12: false });
    return Object.fromEntries(list.map(([k, ms]) => [k, fmt.format(new Date(ms))]));
  }, SEEDS.flatMap((s) => [[`${s.release}:in`, s.ingestedAt], ...(s.withdrawnAt ? [[`${s.release}:out`, s.withdrawnAt]] : [])]));
  const tableProblems = checkTable(first, NEW.release, new Set([WITHDRAWN.release]), expectFormatted);
  step('版本表：順序、狀態、「目前提供」、大小、提交、kid、收件時間、動作', tableProblems.length === 0 && first.table && !first.empty && first.devicesEmpty === '還沒有配對任何電腦' && !first.devicesTable,
    tableProblems.length === 0
      ? `${first.rows.map((r) => `${r.release}（${r.state}${r.badge ? `、${r.badge}` : ''}、${r.size}${r.withdraw ? '、下載＋撤回' : '、—'}）`).join('、')}；電腦表顯示「${first.devicesEmpty}」`
      : `不符：${tableProblems.join('；')}`,
    { rows: first.rows, problems: tableProblems, devicesEmpty: first.devicesEmpty });

  const api1 = await pageFetch(P, 'GET', '/api/v1/app/releases');
  const api1Ok = api1.status === 200 && api1.json?.latest === NEW.release && sameList(api1.json?.releases?.map((r) => r.release), EXPECTED_ORDER)
    && api1.json.releases.every((r) => r.state === seedOf(r.release).state && r.zipBytes === seedOf(r.release).zipBytes && r.kid === seedOf(r.release).kid && r.commit === seedOf(r.release).commit)
    && sameJson(first.probe?.latest, api1.json?.latest) && sameList(first.probe?.releases?.map((r) => r.release), EXPECTED_ORDER) && Array.isArray(api1.json?.devices) && api1.json.devices.length === 0;
  step('GET /api/v1/app/releases 與畫面一致', api1Ok,
    `${api1.status}、latest ${api1.json?.latest ?? '—'}、${(api1.json?.releases ?? []).map((r) => `${r.release} ${r.state}`).join('、')}、電腦 ${(api1.json?.devices ?? []).length} 台；畫面的 latest ${first.probe?.latest ?? '—'}`,
    { status: api1.status, latest: api1.json?.latest ?? null, releases: api1.json?.releases ?? null });

  // ───── 5. 擁有者下載：點「下載」、標頭；撤回版 410 ─────
  const dl = await clickDownload(P, OLD);
  step(`點 ${OLD.release} 的「下載」：存下的檔與種的 zip 相同`, dl.ok, dl.summary, dl.data);
  const head = await pageFetch(P, 'GET', `/api/v1/app/releases/${OLD.release}/package`, null, { binary: true });
  const headOk = head.status === 200 && head.sha256 === OLD.zipSha256 && head.headers['content-type'] === 'application/zip'
    && head.headers.etag === `"${OLD.zipSha256}"` && /myriagent-0\.2\.300-a1b2c3d\.zip/.test(head.headers['content-disposition'] ?? '');
  step('擁有者下載的回應標頭（ETag、Content-Type、檔名）', headOk,
    `${head.status}、${head.size} 位元組、SHA-256 ${head.sha256 === OLD.zipSha256 ? '相同' : '不同'}；Content-Type ${head.headers['content-type'] ?? '—'}、ETag ${head.headers.etag ?? '—'}、Content-Disposition ${head.headers['content-disposition'] ?? '—'}`,
    { status: head.status, size: head.size, headers: head.headers });
  const gone1 = await pageFetch(P, 'GET', `/api/v1/app/releases/${WITHDRAWN.release}/package`);
  step('撤回版的套件 410 gone', gone1.status === 410 && gone1.json?.error === 'gone', `${gone1.status} ${gone1.text.slice(0, 80)}`, { status: gone1.status, body: gone1.json });

  // ───── 6. 撤回：取消 → 沒有送出 ─────
  dialogPlan.push(false);
  const postsBefore = withdrawPosts.length;
  await P.click(`tr[data-release="${OLD.release}"] .rr-actions button`);
  await sleep(800);
  const afterCancel = await viewState(P);
  const cancelDialog = dialogs.at(-1) ?? null;
  const cancelOk = cancelDialog?.type === 'confirm' && cancelDialog.accepted === false
    && cancelDialog.message.startsWith(`撤回 ${OLD.release}？`) && cancelDialog.message.includes('它不是目前提供的版本，目前提供的版本不變')
    && withdrawPosts.length === postsBefore && rowOf(afterCancel, OLD.release)?.state === '可用' && afterCancel.probe?.latest === NEW.release;
  step(`撤回 ${OLD.release} 時在確認框按取消：沒有送出、表不變`, cancelOk,
    `確認框 ${cancelDialog ? `${cancelDialog.type}「${firstLine(cancelDialog.message)}」（${cancelDialog.message.includes('它不是目前提供的版本') ? '說明不是目前提供的版本' : '說明不對'}）` : '沒有出現'}；`
      + `送出的撤回 ${withdrawPosts.length - postsBefore} 次；${OLD.release} 仍是 ${rowOf(afterCancel, OLD.release)?.state ?? '—'}、目前提供 ${afterCancel.probe?.latest ?? '—'}`,
    { dialog: cancelDialog });

  // ───── 7. 撤回目前提供的版本：確定 ─────
  dialogPlan.push(true);
  await P.click(`tr[data-release="${NEW.release}"] .rr-actions button`);
  const afterWithdraw = await waitFor(async () => {
    const s = await viewState(P);
    return s.message.text === `已撤回 ${NEW.release}` && s.probe?.latest === OLD.release ? s : null;
  }, 10000);
  const okDialog = dialogs.at(-1) ?? null;
  const confirmOk = okDialog?.type === 'confirm' && okDialog.accepted === true && okDialog.message.startsWith(`撤回 ${NEW.release}？`)
    && okDialog.message.includes('它是目前提供的版本，改提供前一個可用的版本（沒有就不提供更新）') && okDialog.message.includes('同名的版本也永遠不能再收');
  const post = withdrawPosts.at(-1) ?? null;
  const postOk = withdrawPosts.length === postsBefore + 1 && post?.path === `/api/v1/app/releases/${NEW.release}/withdraw` && post.csrf === '1' && post.status === 200;
  const withdrawProblems = afterWithdraw ? checkTable(afterWithdraw, OLD.release, new Set([WITHDRAWN.release, NEW.release]), expectFormatted, { skipWithdrawnTitle: NEW.release }) : ['10 秒內沒有顯示「已撤回」'];
  const newRow = afterWithdraw ? rowOf(afterWithdraw, NEW.release) : null;
  step(`撤回目前提供的 ${NEW.release}：確認框、送出、表與「目前提供」改成 ${OLD.release}`, confirmOk && postOk && withdrawProblems.length === 0
    && afterWithdraw.message.tone === 'ok' && /^撤回時間 /.test(newRow?.stateTitle ?? '') && newRow.stateTitle !== '撤回時間 —',
    `確認框 ${okDialog ? `「${firstLine(okDialog.message)}」（${confirmOk ? '說明是目前提供的版本' : '文字不對'}）` : '沒有出現'}；POST ${post ? `${post.path} ${post.status}（X-Myriagent-Csrf ${post.csrf ?? '沒有'}）` : '沒有送出'}；`
      + `${afterWithdraw ? `訊息「${afterWithdraw.message.text}」、${afterWithdraw.rows.map((r) => `${r.release} ${r.state}${r.badge ? `（${r.badge}）` : ''}`).join('、')}` : ''}`
      + `${withdrawProblems.length ? `；不符：${withdrawProblems.join('；')}` : ''}`,
    { dialog: okDialog, post, rows: afterWithdraw?.rows ?? null, problems: withdrawProblems });

  const api2 = await pageFetch(P, 'GET', '/api/v1/app/releases');
  const r2 = api2.json?.releases?.find((r) => r.release === NEW.release) ?? null;
  const api2Ok = api2.status === 200 && api2.json?.latest === OLD.release && r2?.state === 'withdrawn' && typeof r2.withdrawnAt === 'number' && r2.withdrawnAt >= now
    && sameList(api2.json.releases.map((r) => r.release), EXPECTED_ORDER);
  step('撤回之後 API：latest 改成前一個可用的版本、撤回時間有值', api2Ok,
    `${api2.status}、latest ${api2.json?.latest ?? '—'}、${NEW.release} ${r2?.state ?? '—'}（withdrawnAt ${r2?.withdrawnAt ?? '—'}）`, { latest: api2.json?.latest ?? null, release: r2 });
  const gone2 = await pageFetch(P, 'GET', `/api/v1/app/releases/${NEW.release}/package`);
  step('剛撤回的套件 410 gone（資料夾留給下一次保存刪）', gone2.status === 410 && gone2.json?.error === 'gone',
    `${gone2.status} ${gone2.text.slice(0, 80)}；資料夾${fs.existsSync(path.join(releasesDir, NEW.release)) ? '還在' : '已刪'}`, { status: gone2.status });

  // ───── 8. 撤回 API 的其他結果（頁面內同源 fetch，帶 X-Myriagent-Csrf）─────
  const again = await pageFetch(P, 'POST', `/api/v1/app/releases/${NEW.release}/withdraw`, {});
  const missing = await pageFetch(P, 'POST', '/api/v1/app/releases/0.2.999-abcdef0/withdraw', {});
  const invalid = await pageFetch(P, 'POST', '/api/v1/app/releases/not-a-release/withdraw', {});
  step('再撤回一次 alreadyWithdrawn、不存在 404、名稱不合法 400', again.status === 200 && again.json?.alreadyWithdrawn === true && again.json?.state === 'withdrawn'
    && missing.status === 404 && missing.json?.error === 'not_found' && invalid.status === 400 && invalid.json?.error === 'invalid_release',
    `再撤回 ${again.status}（alreadyWithdrawn ${again.json?.alreadyWithdrawn}）、不存在 ${missing.status} ${missing.json?.error ?? '—'}、不合法 ${invalid.status} ${invalid.json?.error ?? '—'}`,
    { again: again.json, missing: missing.json, invalid: invalid.json });

  // ───── 9. 稽核：只有一筆 app.release.withdrawn（owner）─────
  const audit = await pageFetch(P, 'GET', '/api/v1/audit?limit=200');
  const entries = audit.json?.entries ?? [];
  const withdrawnAudit = entries.filter((e) => e.action === 'app.release.withdrawn');
  const auditOk = audit.status === 200 && withdrawnAudit.length === 1 && withdrawnAudit[0].target === NEW.release && withdrawnAudit[0].outcome === 'ok'
    && withdrawnAudit[0].actorType === 'owner';
  step('稽核：只有一筆 app.release.withdrawn（owner、ok；取消、再撤回與錯誤都不寫）', auditOk,
    `/api/v1/audit ${audit.status}、${entries.length} 筆；app.release.withdrawn ${withdrawnAudit.length} 筆${withdrawnAudit[0] ? `（${withdrawnAudit[0].actorType}、${withdrawnAudit[0].target}、${withdrawnAudit[0].outcome}）` : ''}`,
    { status: audit.status, withdrawn: withdrawnAudit });

  // ───── 10. 重新整理按鈕、截圖 ─────
  await P.click('#btn-releases-reload');
  const reloaded = await waitFor(async () => {
    const s = await viewState(P);
    return s.message.text === '已重新整理' ? s : null;
  }, 10000);
  step('重新整理按鈕', !!reloaded && reloaded.message.tone === 'ok' && reloaded.probe?.latest === OLD.release && sameList(reloaded.rows.map((r) => r.release), EXPECTED_ORDER),
    reloaded ? `訊息「${reloaded.message.text}」（${reloaded.message.tone}）、目前提供 ${reloaded.probe?.latest ?? '—'}` : '10 秒內沒有顯示「已重新整理」');
  const shot = path.join(work, 'releases.png');
  await P.screenshot({ path: shot, fullPage: true });
  step('截圖', null, shot);

  // ───── 11. 頁面乾淨：CSP、主控台、≥400 的回應、對話框 ─────
  const probeViolations = await P.evaluate(() => window.__hubProbe?.violations ?? []);
  const errors = consoleLog.filter((m) => m.type === 'error' || m.type === 'pageerror');
  const unexpected = errors.filter((m) => !expectedConsole(m));
  const http = compareHttp();
  const dialogsOk = dialogs.length === 2 && dialogs.every((d) => d.planned) && dialogPlan.length === 0;
  step('頁面乾淨：CSP 0 違規、主控台與 ≥400 的回應只有預期的、對話框剛好兩個', cspLog.length === 0 && probeViolations.length === 0 && unexpected.length === 0 && http.ok && dialogsOk,
    `CSP 違規 ${cspLog.length} 筆（auth.js 記的 ${probeViolations.length} 筆）；主控台錯誤 ${errors.length} 筆，排除預期的之後 ${unexpected.length} 筆；≥400 的回應 ${http.summary}；`
      + `對話框 ${dialogs.length} 個（${dialogs.map((d) => `${d.type}${d.planned ? (d.accepted ? '確定' : '取消') : '非預期'}`).join('、')}）`,
    { csp: cspLog, probeViolations, console: errors.map((m) => ({ ...m, expected: expectedConsole(m) })), http, dialogs: dialogs.map((d) => ({ ...d, message: firstLine(d.message) })) });
} catch (e) {
  step('例外', false, String(e?.message ?? e), { stack: String(e?.stack ?? e) });
} finally {
  if (browser) {
    // 先關內容再關瀏覽器，各加時限（同時有別的 session 在跑 Chrome 時 browser.close() 偶爾好幾秒才返回；全域陷阱檔 verification-pitfalls）
    const started = Date.now();
    await Promise.race([Promise.all(browser.contexts().map((c) => c.close().catch(() => {}))), sleep(5000)]);
    const closed = await Promise.race([browser.close().then(() => true, () => false), sleep(15000).then(() => false)]);
    if (!closed) step('關閉瀏覽器', false, `browser.close() ${((Date.now() - started) / 1000).toFixed(0)} 秒內沒有返回`);
  }
  if (hub) {
    const needle = hub.needle;
    const stopped = await hub.close().catch((e) => ({ forced: null, adminCode: null, keysLeft: [], error: String(e?.message ?? e) }));
    await sleep(500);
    const left = keyFilesNaming(needle);
    step('admin stop（--data）、這個 MyriagentHub 匯入的金鑰檔都已刪除', stopped.forced === false && left.length === 0 && (stopped.keysLeft ?? []).length === 0,
      `${stopped.error ? `停止時例外：${stopped.error}；` : `admin stop 結束碼 ${stopped.adminCode}${stopped.forced ? '、沒有正常結束（已強制結束並代刪金鑰檔）' : '、正常結束'}；`}`
        + `金鑰存放區裡記著這份複製（${needle}）的檔 ${left.length} 個`, { stopped, left });
  }
  heartbeat?.stop();
  if (lockHeld) {
    const released = releaseLock();
    if (!released.ok) step('釋放序列化鎖', false, released.message);
  }
  result.finishedAt = new Date().toISOString();
  result.durationMs = Date.parse(result.finishedAt) - Date.parse(result.startedAt);
  result.ok = result.steps.length > 0 && result.steps.every((s) => s.ok !== false);
  save();
  console.log(result.ok ? `全部通過（${(result.durationMs / 1000).toFixed(1)} 秒），結果檔：${outFile}` : `有檢查失敗，結果檔：${outFile}`);
  process.exit(result.ok ? 0 : 1);
}

// ───── 種版本 ─────

/** 決定性的位元組（SHA-256 計數器模式）：同一個 label 永遠產生同一串 */
function bytes(n, label) {
  const out = Buffer.alloc(n);
  for (let off = 0, i = 0; off < n; i++) {
    const block = crypto.createHash('sha256').update(`${label}:${i}`).digest();
    off += block.copy(out, off);
  }
  return out;
}

function sha256(buf) {
  return crypto.createHash('sha256').update(buf).digest('hex');
}

/**
 * 一個版本的三個檔與資料列。zip 是決定性的位元組（伺服器只比長度、整檔照送）；manifest 照 ReleaseManifestRules.Build 的欄位順序與縮排
 * （伺服器只比原文的 SHA-256，不解析）；簽章檔是格式正確的 {kid,sig}（伺服器只比 kid，不驗章）
 */
function seedPackage(seed, index) {
  const [version, build] = seed.release.split('-');
  const commit = (build + sha256(`commit:${seed.release}`)).slice(0, 40);
  const zip = bytes(seed.zipBytes, `zip:${seed.release}`);
  const zipSha256 = sha256(zip);
  const exe = { path: 'Myriagent.exe', size: seed.zipBytes * 2, sha256: sha256(`exe:${seed.release}`) };
  const manifest = Buffer.from(JSON.stringify({
    schema: 1, name: 'Myriagent', release: seed.release, version, build, commit,
    createdAt: new Date(seed.ingestedAt - 10 * 60 * 1000).toISOString().replace(/\.\d{3}Z$/, 'Z'),
    entry: 'Myriagent.exe', minRuntime: { name: 'Microsoft.WindowsDesktop.App', version: '10.0.0' }, protocolVersion: 1,
    zip: `myriagent-${seed.release}.zip`, zipSha256, fileCount: 1, totalBytes: exe.size, files: [exe],
  }, null, 2), 'utf8');
  // 第二個版本換一把 kid（像換過金鑰），表上顯示的 kid 要跟著列
  const kid = sha256(`kid:${index === 1 ? 'old' : 'current'}`).slice(0, 16);
  const signature = JSON.stringify({ kid, sig: bytes(64, `sig:${seed.release}`).toString('base64url') });
  return { ...seed, version, build, commit, zip, zipSha256, manifest, manifestSha256: sha256(manifest), kid, signature };
}

function seedOf(release) {
  return SEEDS.find((s) => s.release === release) ?? {};
}

/** 停機時寫 hub.db 的 app_releases 與版本資料夾：資料表要已經由遷移建好、而且是空的 */
function seedReleases(dataDir) {
  const dbFile = path.join(dataDir, 'db', 'hub.db');
  const dir = path.join(dataDir, 'app-releases');
  if (!fs.existsSync(dbFile)) return { ok: false, summary: `找不到 ${dbFile}`, data: null };
  const db = new DatabaseSync(dbFile);
  try {
    const table = db.prepare("SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'app_releases'").get();
    if (!table) return { ok: false, summary: 'hub.db 沒有 app_releases（遷移沒跑？）', data: null };
    const before = db.prepare('SELECT COUNT(*) AS n FROM app_releases').get().n;
    if (before !== 0) return { ok: false, summary: `app_releases 已經有 ${before} 列`, data: null };
    const insert = db.prepare(`INSERT INTO app_releases (release, version, build, "commit", manifest_sha256, zip_sha256, zip_bytes, protocol_version, min_runtime, kid, state, ingested_at, withdrawn_at)
      VALUES (?, ?, ?, ?, ?, ?, ?, 1, ?, ?, ?, ?, ?)`);
    db.exec('BEGIN');
    for (const s of SEEDS) {
      insert.run(s.release, s.version, s.build, s.commit, s.manifestSha256, s.zipSha256, s.zipBytes, MIN_RUNTIME, s.kid, s.state, s.ingestedAt, s.withdrawnAt);
    }
    db.exec('COMMIT');
    const after = db.prepare('SELECT release, state FROM app_releases ORDER BY release').all().map((r) => ({ ...r }));
    for (const s of SEEDS) {
      const folder = path.join(dir, s.release);
      fs.mkdirSync(folder, { recursive: true });
      fs.writeFileSync(path.join(folder, `myriagent-${s.release}.zip`), s.zip);
      fs.writeFileSync(path.join(folder, `myriagent-${s.release}.manifest.json`), s.manifest);
      fs.writeFileSync(path.join(folder, `myriagent-${s.release}.manifest.sig`), s.signature);
    }
    return {
      ok: after.length === SEEDS.length,
      summary: `${after.map((r) => `${r.release} ${r.state}`).join('、')}；資料夾 ${dir}`,
      data: { rows: after, packages: SEEDS.map((s) => ({ release: s.release, zipBytes: s.zipBytes, zipSha256: s.zipSha256, manifestSha256: s.manifestSha256, kid: s.kid })) },
    };
  } finally {
    db.close();
  }
}

// ───── 頁面 ─────

function watch(page, label) {
  pageLabels.set(page, label);
  page.on('console', (m) => consoleLog.push({ page: label, type: m.type(), text: m.text(), url: m.location()?.url ?? '' }));
  page.on('pageerror', (e) => consoleLog.push({ page: label, type: 'pageerror', text: String(e?.message ?? e), url: '' }));
  page.on('response', (r) => {
    if (r.status() >= 400) httpErrors.push({ page: label, method: r.request().method(), status: r.status(), url: r.url() });
    if (r.request().method() === 'POST' && /\/withdraw$/.test(new URL(r.url()).pathname)) {
      // 只記版本頁自己送的（這支在頁面內的 fetch 由 pageFetch 加 X-E2E-Test 標記）
      const headers = r.request().headers();
      if (headers['x-e2e-test'] !== '1') withdrawPosts.push({ path: new URL(r.url()).pathname, status: r.status(), csrf: headers['x-myriagent-csrf'] ?? null });
    }
  });
  page.on('dialog', (d) => {
    const planned = dialogPlan.length > 0;
    const accept = planned ? dialogPlan.shift() : false;
    dialogs.push({ page: label, type: d.type(), message: d.message(), planned, accepted: accept });
    (accept ? d.accept() : d.dismiss()).catch(() => {});
  });
}

/** 版本頁目前的樣子（DOM 與 __hubProbe.releases()） */
function viewState(page) {
  return page.evaluate(() => {
    const el = (id) => document.getElementById(id);
    const shown = (id) => {
      const e = el(id);
      return !!e && !e.hidden && getComputedStyle(e).display !== 'none';
    };
    const text = (root, sel) => root.querySelector(sel)?.textContent ?? null;
    const rows = [...document.querySelectorAll('#releases-rows tr')].map((tr) => {
      const version = tr.querySelector('.rr-version');
      const link = tr.querySelector('.rr-actions a');
      return {
        release: tr.dataset.release,
        gone: tr.classList.contains('is-gone'),
        version: version?.firstChild?.nodeType === Node.TEXT_NODE ? version.firstChild.textContent : null,
        badge: text(tr, '.rr-version .badge'),
        build: text(tr, '.rr-build'),
        commit: text(tr, '.rr-commit'),
        commitTitle: tr.querySelector('.rr-commit')?.title ?? null,
        size: text(tr, '.rr-size'),
        ingested: text(tr, '.rr-ingested'),
        state: text(tr, '.rr-state'),
        stateTitle: tr.querySelector('.rr-state')?.title ?? '',
        kid: text(tr, '.rr-kid code'),
        download: link?.getAttribute('href') ?? null,
        downloadAttr: link ? link.hasAttribute('download') : null,
        downloadText: link?.textContent ?? null,
        withdraw: tr.querySelector('.rr-actions button')?.textContent ?? null,
        actions: text(tr, '.rr-actions'),
      };
    });
    const message = el('releases-message');
    return {
      view: window.__hubProbe?.view ?? null,
      path: location.pathname,
      hash: location.hash,
      message: { text: shown('releases-message') ? message.textContent : '', tone: message?.dataset.tone ?? '' },
      empty: shown('releases-empty'),
      table: shown('releases-table'),
      devicesEmpty: shown('devices-empty') ? el('devices-empty').textContent : null,
      devicesTable: shown('devices-table'),
      rows,
      probe: window.__hubProbe?.releases?.() ?? null,
    };
  });
}

function rowOf(state, release) {
  return state.rows.find((r) => r.release === release) ?? null;
}

/**
 * 版本表逐列比對種的資料：順序（收件時間由新到舊）、版本、build、提交（前 7 碼，title 是完整的）、大小、收件時間、狀態文字、kid、
 * 「目前提供」只在 latest 那一列、撤回的列有 is-gone 與撤回時間、可用的列有下載連結與撤回按鈕。回傳不符的說明
 */
function checkTable(state, latest, withdrawn, formatted, { skipWithdrawnTitle = null } = {}) {
  const problems = [];
  if (!sameList(state.rows.map((r) => r.release), EXPECTED_ORDER)) problems.push(`順序 ${state.rows.map((r) => r.release).join('、')}`);
  for (const r of state.rows) {
    const s = seedOf(r.release);
    const gone = withdrawn.has(r.release);
    const want = {
      version: s.version, build: s.build, commit: s.build, commitTitle: s.commit, size: s.sizeText, ingested: formatted[`${s.release}:in`],
      state: gone ? '已撤回' : '可用', kid: s.kid, gone, badge: r.release === latest ? '目前提供' : null,
      download: gone ? null : `/api/v1/app/releases/${encodeURIComponent(s.release)}/package`, downloadAttr: gone ? null : true,
      downloadText: gone ? null : '下載', withdraw: gone ? null : '撤回',
    };
    for (const [k, v] of Object.entries(want)) {
      if (r[k] !== v) problems.push(`${r.release} 的 ${k} 是 ${JSON.stringify(r[k])}（預期 ${JSON.stringify(v)}）`);
    }
    if (gone && r.actions !== '—') problems.push(`${r.release} 的動作欄是 ${JSON.stringify(r.actions)}（預期 —）`);
    if (gone && r.release !== skipWithdrawnTitle && r.stateTitle !== `撤回時間 ${formatted[`${s.release}:out`]}`) problems.push(`${r.release} 的撤回時間提示是 ${JSON.stringify(r.stateTitle)}`);
  }
  if (state.probe?.latest !== latest) problems.push(`__hubProbe 的 latest 是 ${state.probe?.latest ?? 'null'}（預期 ${latest}）`);
  return problems;
}

/** 點那一列的「下載」：Playwright 的 download 事件，存檔後比對位元組與建議檔名 */
async function clickDownload(page, seed) {
  const target = path.join(work, 'downloads', `myriagent-${seed.release}.zip`);
  fs.mkdirSync(path.dirname(target), { recursive: true });
  try {
    const [download] = await Promise.all([
      page.waitForEvent('download', { timeout: 15000 }),
      page.click(`tr[data-release="${seed.release}"] .rr-actions a`),
    ]);
    await download.saveAs(target);
    const saved = fs.readFileSync(target);
    const ok = download.suggestedFilename() === `myriagent-${seed.release}.zip` && saved.length === seed.zipBytes && sha256(saved) === seed.zipSha256 && !(await download.failure());
    return {
      ok,
      summary: `建議檔名 ${download.suggestedFilename()}、${saved.length} 位元組、SHA-256 ${sha256(saved) === seed.zipSha256 ? '與種的 zip 相同' : '不同'}；存到 ${target}`,
      data: { suggested: download.suggestedFilename(), size: saved.length, sha256: sha256(saved), url: download.url() },
    };
  } catch (e) {
    return { ok: false, summary: `沒有下載：${e?.message ?? e}`, data: null };
  }
}

/**
 * 頁面內的同源 fetch（帶 cookie；非 GET 加 CSRF 標頭，與 auth.js 相同；另加 X-E2E-Test，回應監看不把它算成版本頁的撤回）。
 * binary：回本文的 SHA-256 與大小（不回內容）
 */
function pageFetch(page, method, url, body, { binary = false } = {}) {
  return page.evaluate(async ([m, u, b, bin]) => {
    const headers = m === 'GET' ? { 'X-E2E-Test': '1' } : { 'X-Myriagent-Csrf': '1', 'Content-Type': 'application/json', 'X-E2E-Test': '1' };
    const r = await fetch(u, { method: m, headers, body: m === 'GET' ? undefined : JSON.stringify(b ?? {}), credentials: 'same-origin', cache: 'no-store' });
    const h = {};
    r.headers.forEach((v, k) => { h[k] = v; });
    if (bin) {
      const buf = await r.arrayBuffer();
      const digest = await crypto.subtle.digest('SHA-256', buf);
      return { status: r.status, headers: h, size: buf.byteLength, sha256: [...new Uint8Array(digest)].map((x) => x.toString(16).padStart(2, '0')).join(''), text: '', json: null };
    }
    const text = await r.text();
    let parsed = null;
    try { parsed = text ? JSON.parse(text) : null; } catch { parsed = null; }
    return { status: r.status, headers: h, json: parsed, text };
  }, [method, url, body ?? null, binary]);
}

// ≥400 的回應和預期清單逐項比對次數
function compareHttp() {
  const pathOf = (u) => {
    try { return new URL(u).pathname; } catch { return u; }
  };
  const rows = EXPECTED_HTTP.map((x) => ({ ...x, actual: httpErrors.filter((e) => e.status === x.status && pathOf(e.url) === x.path).length }));
  const unexpected = httpErrors.filter((e) => !EXPECTED_HTTP.some((x) => e.status === x.status && pathOf(e.url) === x.path));
  const ok = unexpected.length === 0 && rows.every((r) => r.actual === r.count);
  const summary = rows.map((r) => `${r.path} ${r.status}×${r.actual}（預期 ${r.count}）`).concat(unexpected.map((e) => `非預期 ${e.method} ${e.url} ${e.status}`)).join('、');
  return { ok, rows, unexpected, summary };
}

// 預期的主控台錯誤：預期清單裡的 ≥400（Failed to load resource）
function expectedConsole(m) {
  if (/Failed to load resource/.test(m.text) && m.url) {
    let p = null;
    try { p = new URL(m.url).pathname; } catch { p = null; }
    const hit = EXPECTED_HTTP.find((x) => x.path === p && m.text.includes(`status of ${x.status}`));
    if (hit) return hit.why;
  }
  return null;
}

// ───── 工具 ─────

async function waitFor(read, ms, stepMs = 200) {
  const started = Date.now();
  while (Date.now() - started < ms) {
    const v = await read().catch(() => null);
    if (v) return v;
    await sleep(stepMs);
  }
  return null;
}

function sameList(a, b) {
  return Array.isArray(a) && Array.isArray(b) && a.length === b.length && a.every((x, i) => x === b[i]);
}

function sameJson(a, b) {
  return JSON.stringify(a) === JSON.stringify(b);
}

function firstLine(text) {
  return String(text ?? '').split('\n')[0];
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
  const known = new Set(['hub', 'work', 'out', 'headed']);
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
