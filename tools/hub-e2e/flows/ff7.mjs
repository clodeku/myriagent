// FF7 遠端帳戶與 CLI（WP4.9 步 17，計畫塊 14）：HOME 帶假 Codex app-server（ok）、OFFICE 帶 evil_host，兩台的 Codex 帳戶信箱相同（連到同一個邏輯帳戶）；
// HOME 另宣告裝了 Claude 與 Codex 的 CLI（假的，不會執行任何程序）。網頁 P、Q 是同一個瀏覽器內容的兩個分頁（儀表板）。
// 斷言：P 在矩陣按 HOME 那一格的「用裝置碼登入」→ accountLoginDevice 25 秒內 done（P48:903）；驗證碼只到 P（Q 的訊息與畫面都沒有）；
// fakeCodexComplete 之後那一格變成已登入；以 UTF-8 與 UTF-16LE 掃伺服器資料根、記錄檔與結果檔都沒有驗證碼（P48:990，正向對照是假 app-server 的腳本）；
// evil_host 的那台回 rejected、P 沒有收到任何 accountLogin；所有電腦登出時 HOME 先離線：P 只收到一則 done（設計:3260），HOME 上線後恰好登出一次；
// cliUpdate 在 FD：HOME 已裝的 Claude 回 done（已開始）、更新跑到 CLI 時被假裝置拒絕（refusedCli 加 1），OFFICE 沒裝的回 rejected（conflict、refusedCli 不變）；
// cliCheck 之後 devicesCli 的 checkedAt 更新（放在更新之後：假 CLI 的執行檔不在磁碟上，檢查重新辨識之後就算沒裝）。每一段的 refusedCli 變化逐一比對預期。
import fs from 'node:fs';
import path from 'node:path';
import { openApp, pageMsgs, postCmd, waitFor } from '../lib/harness.mjs';
import { FAKE_CODEX_EMAIL } from '../lib/harness-rules.mjs';
import { containsSecret } from '../lib/flow-rules.mjs';
import { ffEvents, ffWaitResult, flowRecorder, trackHubFrames } from '../lib/flow-page.mjs';
import { control, deviceStatus, localCommand, pageCleanStep, sleep } from './common.mjs';

/** 假 app-server 的驗證碼（同 FakeCodexAppServerScript.UserCode）：不寫進結果檔（掃描時結果檔也在範圍裡） */
const USER_CODE = 'QWER-TY12Z';

/** 假 CLI 的版本（劇本的 cli op） */
const CLI = { Claude: '2.1.0', Codex: '0.50.0' };

/**
 * 每一段 refusedCli（假裝置拒絕執行的 CLI 指令數）的預期變化：帳戶的登入、登出、檢查走帳戶管理員的假 app-server（不經 CLI 管理員）；
 * HOME 的 Claude 更新（原生安裝：claude update）在執行時被拒一次；OFFICE 沒裝 Claude，cliUpdate 在前置條件就被拒（不會落進安裝）；
 * cliCheck 不執行 CLI（重新辨識讀檔案、最新版本查假的端點）
 */
const EXPECTED_REFUSED = {
  refresh: { OFFICE: 0, HOME: 0 },
  login: { OFFICE: 0, HOME: 0 },
  evil: { OFFICE: 0, HOME: 0 },
  logoutAll: { OFFICE: 0, HOME: 0 },
  updateHome: { OFFICE: 0, HOME: 1 },
  updateOffice: { OFFICE: 0, HOME: 0 },
  cliCheck: { OFFICE: 0, HOME: 0 },
};

/** startEnv 的選項：兩台都有假 Codex（信箱相同），HOME 宣告裝了兩種 CLI；完整的訊息（帳戶、devicesCli、accountLogin 要整則看） */
export const env = {
  devices: [
    { name: 'OFFICE', fakeCodex: 'evil_host', codexEmail: FAKE_CODEX_EMAIL },
    { name: 'HOME', fakeCodex: 'ok', codexEmail: FAKE_CODEX_EMAIL, cli: CLI },
  ],
  initOpts: {},
  initScripts: [{ fn: flowRecorder }],
};

/** 頁面收到的最新一則符合的值（pick 回 undefined 表示這則不算）；沒有回 null */
async function latest(app, pick) {
  const msgs = await pageMsgs(app);
  for (let i = msgs.length - 1; i >= 0; i--) {
    const v = pick(msgs[i]);
    if (v !== undefined) return v;
  }
  return null;
}

const accountsOf = (m) => (m?.type === 'accounts' || m?.type === 'init') && Array.isArray(m.accounts) ? m.accounts : undefined;
const devicesOf = (m) => (m?.type === 'devices' && Array.isArray(m.devices) ? m.devices : m?.type === 'init' && Array.isArray(m.remote?.devices) ? m.remote.devices : undefined);
const devicesCliOf = (m) => ((m?.type === 'cli' || m?.type === 'init') && m.devicesCli && typeof m.devicesCli === 'object' ? m.devicesCli : undefined);

/** 邏輯帳戶 la 在某台的那一格（AccountDeviceView）；沒有回 null */
async function cellOf(app, la, deviceId) {
  const accounts = (await latest(app, accountsOf)) ?? [];
  const a = accounts.find((x) => x.id === la);
  return a && Array.isArray(a.devices) ? a.devices.find((c) => c.deviceId === deviceId) ?? null : null;
}

/** 等那一格變成某個狀態：回那一格，ms 內沒有回 null */
function waitCell(app, la, deviceId, status, ms) {
  return waitFor(async () => {
    const c = await cellOf(app, la, deviceId);
    return c && c.status === status ? c : null;
  }, ms, 300);
}

/** 頁面收到的 accountLogin（只回比對要用的：帳戶、有沒有驗證碼、驗證碼是不是假 app-server 的；不回驗證碼本身） */
async function loginMsgs(app) {
  return (await pageMsgs(app)).filter((m) => m?.type === 'accountLogin').map((m) => ({
    accountId: m.accountId ?? null, done: !!m.done, hasCode: !!m.deviceCode, codeIsFake: m.deviceCode?.userCode === USER_CODE,
    textHasCode: String(m.message ?? '').includes(USER_CODE),
  }));
}

/** 這個頁面的任何地方（訊息與 DOM 的文字）有沒有驗證碼 */
async function pageHasCode(app) {
  const msgs = JSON.stringify(await pageMsgs(app)).includes(USER_CODE);
  const dom = await app.page.evaluate((c) => (document.documentElement.textContent || '').includes(c), USER_CODE).catch(() => null);
  return { msgs, dom };
}

/** 兩台的 refusedCli */
async function refused(devs) {
  const out = {};
  for (const d of devs) out[d.name] = (await deviceStatus(d, 30000))?.refusedCli ?? null;
  return out;
}

const delta = (a, b) => Object.fromEntries(Object.keys(b).map((k) => [k, a[k] === null || b[k] === null ? null : b[k] - a[k]]));

/** 送一個指令（經 app.js 的傳輸層）、等它的 commandResult：{ commandId, state, code, message } */
async function sendCmd(app, cmd, ms = 30000) {
  const commandId = await postCmd(app, cmd);
  const r = commandId ? await ffWaitResult(app, commandId, ms) : null;
  return { commandId, state: r?.state ?? null, code: r?.code ?? null, message: r?.message ?? null };
}

/** roots 底下的檔（遞迴；只看一層的放在 flat）裡有沒有 needle（UTF-8 與 UTF-16LE）：{ scanned, hits: [相對路徑（編碼）] } */
function scanFor(needle, { roots = [], flat = [], files = [] }, base) {
  const list = [...files];
  const walk = (p) => {
    let st;
    try { st = fs.statSync(p); } catch { return; }
    if (st.isDirectory()) {
      for (const e of fs.readdirSync(p)) walk(path.join(p, e));
    } else if (st.isFile()) {
      list.push(p);
    }
  };
  for (const r of roots) walk(r);
  for (const dir of flat) {
    let names = [];
    try { names = fs.readdirSync(dir); } catch { /* 不存在 */ }
    for (const n of names) {
      const p = path.join(dir, n);
      try { if (fs.statSync(p).isFile() && /\.(log|json|txt|jsonl)$/iu.test(n)) list.push(p); } catch { /* 刪掉了 */ }
    }
  }
  const hits = [];
  for (const f of [...new Set(list)]) {
    let buf;
    try { buf = fs.readFileSync(f); } catch { continue; }
    const c = containsSecret(buf, needle);
    if (c.utf8 || c.utf16) hits.push(`${path.relative(base, f)}（${[c.utf8 ? 'UTF-8' : null, c.utf16 ? 'UTF-16LE' : null].filter(Boolean).join('、')}）`);
  }
  return { scanned: new Set(list).size, hits };
}

export async function run(env) {
  const office = env.devices.get('OFFICE');
  const home = env.devices.get('HOME');
  const devs = [office, home];
  const outcome = { refused: {} };
  const rc0 = await refused(devs);

  // 1. 兩個分頁（儀表板）；兩台的 Codex 帳戶連到同一個邏輯帳戶
  const P = await openApp(env, 'P', { beforeGoto: trackHubFrames });
  const Q = await openApp(env, 'Q', { beforeGoto: trackHubFrames });
  const la = await waitFor(async () => {
    const accounts = (await latest(P, accountsOf)) ?? [];
    const a = accounts.find((x) => x.provider === 'Codex' && Array.isArray(x.devices)
      && [office, home].every((d) => x.devices.some((c) => c.deviceId === d.deviceId && c.status !== 'absent')));
    return a ? a.id : null;
  }, 30000, 300);
  outcome.linked = !!la;
  env.step('FF7：P、Q 連上；兩台的 Codex 帳戶（同一個信箱）連到同一個邏輯帳戶', P.live && Q.live && !!la,
    `P ${P.live ? '連上' : '沒連上'}、Q ${Q.live ? '連上' : '沒連上'}；邏輯帳戶 ${la ?? '30 秒內沒有兩台都連上的 Codex 帳戶'}；refusedCli OFFICE ${rc0.OFFICE}、HOME ${rc0.HOME}`, { la, rc0 });
  if (!P.live || !Q.live || !la) throw new Error('頁面沒連上或兩台的 Codex 帳戶沒有連到同一個邏輯帳戶');

  // 2. 重新檢查兩台（假 app-server 還沒完成登入：account/read 回未登入）→ 兩格都是未登入
  let rc = await refused(devs);
  const refresh = {};
  for (const d of devs) refresh[d.name] = await sendCmd(P, { cmd: 'accountRefresh', accountId: la, deviceId: d.deviceId });
  const notLogged = {};
  for (const d of devs) notLogged[d.name] = !!(await waitCell(P, la, d.deviceId, 'notLoggedIn', 30000));
  let rcNext = await refused(devs);
  outcome.refused.refresh = delta(rc, rcNext);
  rc = rcNext;
  outcome.refresh = Object.fromEntries(devs.map((d) => [d.name, `${refresh[d.name].state}${refresh[d.name].code ? `/${refresh[d.name].code}` : ''}`]));
  outcome.notLoggedIn = notLogged;
  env.step('FF7：網頁的 accountRefresh → 兩格都是未登入', devs.every((d) => refresh[d.name].state === 'done' && notLogged[d.name]),
    devs.map((d) => `${d.name}：${refresh[d.name].state ?? '沒有結果'}${refresh[d.name].code ? `（${refresh[d.name].code}）` : ''}、格子${notLogged[d.name] ? '未登入' : '30 秒內沒有變成未登入'}`).join('；'),
    { refresh, cells: { OFFICE: await cellOf(P, la, office.deviceId), HOME: await cellOf(P, la, home.deviceId) } });

  // 3. P 在矩陣按 HOME 那一格的「用裝置碼登入」：accountLoginDevice 25 秒內 done；驗證碼只到 P
  await P.page.bringToFront();
  const btn = `tr[data-account="${la}"] td.mx-cell[data-device="${home.deviceId}"] .ra-btn[data-act="login"]`;
  const shown = await P.page.waitForSelector(btn, { state: 'visible', timeout: 20000 }).then(() => true, () => false);
  const qLoginsBefore = (await loginMsgs(Q)).length;
  const from = P.frames.length;
  const clickedAt = Date.now();
  if (shown) await P.page.click(btn);
  const loginCmd = shown ? await waitFor(() => P.frames.slice(from).find((f) => f.kind === 'cmd' && f.cmd === 'accountLoginDevice') ?? null, 5000, 50) : null;
  const loginRes = loginCmd ? await ffWaitResult(P, loginCmd.commandId, 25000) : null;
  const loginMs = loginRes ? loginRes.t - clickedAt : null;
  const pLogins = await loginMsgs(P);
  const pCode = pLogins.filter((m) => m.accountId === la && m.hasCode && m.codeIsFake);
  // P 的畫面：帳戶卡的大字驗證碼（前景的分頁才畫）
  const pDom = await waitFor(() => P.page.evaluate((c) => [...document.querySelectorAll('.login-code-val')].some((el) => el.textContent === c), USER_CODE).catch(() => null), 10000, 200);
  // Q：帶到前景讓它畫完再看（訊息與整份 DOM 的文字都不能有）
  await Q.page.bringToFront();
  await sleep(1500);
  const qHas = await pageHasCode(Q);
  const qLogins = (await loginMsgs(Q)).slice(qLoginsBefore);
  await P.page.bringToFront();
  const loginOk = loginRes?.state === 'done' && loginMs !== null && loginMs <= 25000 && pCode.length === 1 && !!pDom && !qHas.msgs && qHas.dom === false && qLogins.length === 0;
  outcome.login = { button: shown, result: loginRes?.state ?? null, within25s: loginMs !== null && loginMs <= 25000, pCodeMsgs: pCode.length, pDom: !!pDom, qMsgs: qHas.msgs, qDom: qHas.dom, qLogins: qLogins.length };
  env.step('FF7：P 按「用裝置碼登入」→ 25 秒內 done（P48:903）；驗證碼只到發起的分頁', loginOk,
    `按鈕${shown ? '看得到' : '20 秒內沒出現'}；accountLoginDevice ${loginRes ? `${loginRes.state}${loginRes.code ? `（${loginRes.code}）` : ''}、按下到結果 ${loginMs} ms` : '沒有結果'}；`
      + `P 收到帶驗證碼的 accountLogin ${pCode.length} 則（是假 app-server 的驗證碼）、畫面${pDom ? '有' : '沒有'}大字驗證碼；`
      + `Q 的訊息${qHas.msgs ? '有' : '沒有'}驗證碼、畫面${qHas.dom === null ? '讀不到' : qHas.dom ? '有' : '沒有'}、收到 accountLogin ${qLogins.length} 則`,
    { commandId: loginCmd?.commandId ?? null, result: loginRes && { state: loginRes.state, code: loginRes.code }, loginMs, pLogins, qLogins });

  // 4. 「在別的裝置核准了」：fakeCodexComplete → 那一格變成已登入（身分相符）
  const complete = await control(home, 'fakeCodexComplete');
  const ready = await waitCell(P, la, home.deviceId, 'ready', 60000);
  const readyDom = ready ? await waitFor(() => P.page.evaluate(([a, d]) => !!document.querySelector(`tr[data-account="${a}"] td.mx-cell[data-device="${d}"][data-kind="ready"]`), [la, home.deviceId]).catch(() => null), 10000, 200) : null;
  const officeCell = await cellOf(P, la, office.deviceId);
  rcNext = await refused(devs);
  outcome.refused.login = delta(rc, rcNext);
  rc = rcNext;
  outcome.ready = { cell: !!ready, identityOk: ready?.identityOk ?? null, dom: !!readyDom, office: officeCell?.status ?? null };
  env.step('FF7：fakeCodexComplete 之後 HOME 那一格變成已登入', !!ready && ready.identityOk === true && !!readyDom && officeCell?.status === 'notLoggedIn',
    `complete.flag ${complete.alreadyComplete ? '已經有' : '寫了'}；HOME 的格子${ready ? `${ready.status}（身分${ready.identityOk ? '相符' : '不符'}、${ready.email ?? '—'}）` : ' 60 秒內沒有變成已登入'}、畫面${readyDom ? '是已登入' : '不是已登入'}；OFFICE 的格子 ${officeCell?.status ?? '—'}`,
    { ready, office: officeCell });

  // 5. 驗證碼不在任何記錄裡：伺服器資料根（含 sync.db、hub.db）、工作資料夾的記錄（伺服器主控台、假裝置的輸出）、假裝置的 logs\、結果檔
  env.report.save();
  const scan = scanFor(USER_CODE, { roots: [env.hub.dataDir, ...devs.map((d) => path.join(d.data, 'logs'))], flat: [env.work], files: env.resultFile ? [env.resultFile] : [] }, env.work);
  const positive = scanFor(USER_CODE, { roots: [path.join(home.data, 'fake-codex')] }, env.work);
  // 觀察：HOME 資料根的其他地方（帳戶資料夾、sync.db、對話；假 app-server 的資料夾除外）
  const homeRest = scanFor(USER_CODE, { roots: fs.readdirSync(home.data).filter((n) => n !== 'fake-codex').map((n) => path.join(home.data, n)) }, env.work);
  outcome.scan = { hits: scan.hits.length, positive: positive.hits.length > 0, resultFile: !!env.resultFile };
  env.step('FF7：驗證碼不在伺服器資料根、記錄檔與結果檔（UTF-8、UTF-16LE；P48:990）', scan.hits.length === 0 && positive.hits.length > 0 && !!env.resultFile,
    `掃了 ${scan.scanned} 個檔（伺服器資料根、兩台的 logs\\、工作資料夾的 .log／.json、結果檔）：${scan.hits.length === 0 ? '都沒有' : `出現在 ${scan.hits.join('、')}`}；`
      + `正向對照（假 app-server 的資料夾）${positive.hits.length > 0 ? `找得到（${positive.hits.join('、')}）` : '找不到'}`,
    { scanned: scan.scanned, hits: scan.hits, positive: positive.hits });
  env.step('FF7：HOME 資料根的其他地方有沒有驗證碼（觀察）', null,
    `掃了 ${homeRest.scanned} 個檔（假 app-server 的資料夾除外）：${homeRest.hits.length === 0 ? '都沒有' : `出現在 ${homeRest.hits.join('、')}`}`, { hits: homeRest.hits });

  // 6. evil_host：OFFICE 的假 app-server 給的驗證網址不是 auth.openai.com → rejected，P 沒有收到任何 accountLogin
  const pLoginsBefore = (await loginMsgs(P)).length;
  const officeCallsBefore = await control(office, 'fakeCodexCalls');
  const evil = await sendCmd(P, { cmd: 'accountLoginDevice', logicalAccountId: la, deviceId: office.deviceId, method: 'deviceCode' }, 40000);
  await sleep(1500);
  const pLoginsAfter = (await loginMsgs(P)).slice(pLoginsBefore);
  const officeCalls = await control(office, 'fakeCodexCalls');
  const newCalls = (officeCalls.calls ?? []).slice((officeCallsBefore.calls ?? []).length);
  const started = newCalls.some((c) => c.startsWith('account/login/start'));
  const canceled = newCalls.some((c) => c.startsWith('account/login/cancel'));
  rcNext = await refused(devs);
  outcome.refused.evil = delta(rc, rcNext);
  rc = rcNext;
  outcome.evil = { result: `${evil.state}${evil.code ? `/${evil.code}` : ''}`, accountLogins: pLoginsAfter.length, started, canceled };
  env.step('FF7：evil_host 的那台回 rejected、沒有任何輸出', evil.state === 'rejected' && pLoginsAfter.length === 0 && started && canceled,
    `OFFICE 的 accountLoginDevice：${evil.state ?? '沒有結果'}${evil.code ? `（${evil.code}）` : ''}${evil.message ? `「${evil.message}」` : ''}；P 收到 accountLogin ${pLoginsAfter.length} 則；`
      + `OFFICE 的假 app-server：${started ? '開始了裝置碼登入' : '沒有開始登入'}、${canceled ? '之後取消' : '沒有取消'}`,
    { evil, calls: newCalls.filter((c) => !c.startsWith('pid=') && !c.startsWith('codexHome=')) });

  // 7. 所有電腦登出：HOME 先離線（暫停同步）→ P 只收到一則 done；HOME 上線後恰好登出一次
  const homeLogouts0 = (await control(home, 'fakeCodexCalls')).logouts ?? 0;
  const officeLogouts0 = officeCalls.logouts ?? 0;
  let paused = null;
  let offline = null;
  let all = null;
  let resultsAll = null;
  let whileOffline = null;
  let resumed = null;
  try {
    paused = await localCommand(home, 'syncPause', { paused: true });
    offline = await waitFor(async () => ((await latest(P, devicesOf)) ?? []).some((d) => d.id === home.deviceId && d.online === false) || null, 30000, 300);
    all = await sendCmd(P, { cmd: 'accountLogout', accountId: la }, 30000);
    await sleep(3000);
    whileOffline = (await control(home, 'fakeCodexCalls')).logouts ?? 0;
  } finally {
    await localCommand(home, 'syncPause', { paused: false }).catch(() => null);
    resumed = await waitFor(async () => ((await deviceStatus(home, 10000))?.state === 'Live' ? true : null), 60000, 500);
  }
  const homeLogout = await waitFor(async () => {
    const n = (await control(home, 'fakeCodexCalls')).logouts ?? 0;
    return n > homeLogouts0 ? n : null;
  }, 60000, 500);
  await sleep(8000);
  const homeLogouts = (await control(home, 'fakeCodexCalls')).logouts ?? 0;
  const officeLogouts = (await control(office, 'fakeCodexCalls')).logouts ?? 0;
  resultsAll = ((await ffEvents(P))?.results ?? []).filter((r) => r.commandId === all?.commandId);
  const homeAfter = await waitCell(P, la, home.deviceId, 'notLoggedIn', 30000);
  rcNext = await refused(devs);
  outcome.refused.logoutAll = delta(rc, rcNext);
  rc = rcNext;
  outcome.logoutAll = {
    paused: paused?.paused ?? null, offline: !!offline, results: resultsAll.map((r) => `${r.state}${r.code ? `/${r.code}` : ''}`), whileOffline: whileOffline - homeLogouts0,
    resumed: !!resumed, homeLogouts: homeLogouts - homeLogouts0, officeLogouts: officeLogouts - officeLogouts0, homeCell: homeAfter?.status ?? null,
  };
  env.step('FF7：所有電腦登出時 HOME 先離線：只收到一則 done（設計:3260）、HOME 上線後恰好登出一次',
    !!offline && resultsAll.length === 1 && resultsAll[0].state === 'done' && whileOffline === homeLogouts0 && !!resumed && !!homeLogout
      && homeLogouts === homeLogouts0 + 1 && officeLogouts === officeLogouts0 && !!homeAfter,
    `HOME 暫停同步：${paused ? `paused ${paused.paused}` : '—'}、P 看到 HOME ${offline ? '離線' : '30 秒內沒看到離線'}；accountLogout（不帶 deviceId）：commandResult ${resultsAll.length} 則`
      + `（${resultsAll.map((r) => `${r.state}${r.code ? `/${r.code}` : ''}`).join('、') || '—'}）；離線期間 HOME 的登出 ${whileOffline - homeLogouts0} 次；`
      + `恢復同步後 HOME ${resumed ? '回到 Live' : '60 秒內沒有回到 Live'}、登出 ${homeLogouts - homeLogouts0} 次（再等 8 秒）；OFFICE（本來就未登入）登出 ${officeLogouts - officeLogouts0} 次；HOME 的格子 ${homeAfter?.status ?? '30 秒內沒有變成未登入'}`,
    { all, results: resultsAll, logouts: { home0: homeLogouts0, home: homeLogouts, office0: officeLogouts0, office: officeLogouts } });

  // 8. cliUpdate：HOME 已裝的 Claude → done（已開始），更新跑到 CLI 時被假裝置拒絕；OFFICE 沒裝 → rejected（conflict）。
  //    在 cliCheck 之前：假 CLI 的執行檔不在磁碟上，檢查重新辨識之後就算沒裝（FD 的限制），之後的更新會落進「還沒安裝」
  const upHome = await sendCmd(P, { cmd: 'cliUpdate', provider: 'Claude', deviceId: home.deviceId });
  const settled = await waitFor(async () => {
    const s = await deviceStatus(home, 15000);
    return s && s.refusedCli !== null && s.refusedCli >= rc.HOME + EXPECTED_REFUSED.updateHome.HOME ? s : null;
  }, 30000, 300);
  await sleep(3000);
  rcNext = await refused(devs);
  outcome.refused.updateHome = delta(rc, rcNext);
  rc = rcNext;
  const upOffice = await sendCmd(P, { cmd: 'cliUpdate', provider: 'Claude', deviceId: office.deviceId });
  await sleep(3000);
  rcNext = await refused(devs);
  outcome.refused.updateOffice = delta(rc, rcNext);
  rc = rcNext;
  outcome.cliUpdate = { home: `${upHome.state}${upHome.code ? `/${upHome.code}` : ''}`, office: `${upOffice.state}${upOffice.code ? `/${upOffice.code}` : ''}` };
  env.step('FF7：cliUpdate 在 FD：HOME 已裝的回 done（已開始）、執行 CLI 時被拒；OFFICE 沒裝的回 rejected', upHome.state === 'done' && !!settled && upOffice.state === 'rejected' && upOffice.code === 'conflict',
    `HOME 的 Claude：${upHome.state ?? '沒有結果'}${upHome.code ? `（${upHome.code}）` : ''}、refusedCli ${settled ? '加了' : '30 秒內沒有加'}；OFFICE 的 Claude（沒裝）：${upOffice.state ?? '沒有結果'}${upOffice.code ? `（${upOffice.code}）` : ''}${upOffice.message ? `「${upOffice.message}」` : ''}`,
    { home: upHome, office: upOffice });

  // 9. cliCheck：HOME 收到就開始檢查、回 done；devicesCli 的 checkedAt 更新（假 CLI 的執行檔不在磁碟上：檢查之後 installed 是 false，只記錄）
  const cliBefore = ((await latest(P, devicesCliOf)) ?? {})[home.deviceId] ?? null;
  const check = await sendCmd(P, { cmd: 'cliCheck', deviceId: home.deviceId });
  const checkedOf = (cli) => Object.fromEntries(['Claude', 'Codex'].map((p) => [p, cli?.[p]?.checkedAt ?? null]));
  const before = checkedOf(cliBefore);
  const cliAfter = await waitFor(async () => {
    const cli = ((await latest(P, devicesCliOf)) ?? {})[home.deviceId] ?? null;
    const now = checkedOf(cli);
    return ['Claude', 'Codex'].every((p) => now[p] !== null && now[p] !== before[p]) ? cli : null;
  }, 60000, 500);
  rcNext = await refused(devs);
  outcome.refused.cliCheck = delta(rc, rcNext);
  rc = rcNext;
  const installed = Object.fromEntries(['Claude', 'Codex'].map((p) => [p, cliAfter?.[p]?.installed ?? null]));
  outcome.cliCheck = { result: `${check.state}${check.code ? `/${check.code}` : ''}`, checkedAtChanged: !!cliAfter, installed };
  env.step('FF7：cliCheck 之後 devicesCli 的 checkedAt 更新', check.state === 'done' && !!cliAfter,
    `cliCheck：${check.state ?? '沒有結果'}${check.code ? `（${check.code}）` : ''}；HOME 的 checkedAt：Claude ${before.Claude ?? '—'} → ${checkedOf(cliAfter).Claude ?? '沒變'}、Codex ${before.Codex ?? '—'} → ${checkedOf(cliAfter).Codex ?? '沒變'}；`
      + `檢查之後的安裝（假 CLI 的執行檔不在磁碟上，重新辨識後是沒裝）：Claude ${installed.Claude}、Codex ${installed.Codex}`,
    { before: cliBefore, after: cliAfter });

  // 10. refusedCli 只有預期的次數
  const refusedOk = Object.entries(EXPECTED_REFUSED).every(([k, v]) => devs.every((d) => outcome.refused[k]?.[d.name] === v[d.name]));
  const rcEnd = await refused(devs);
  env.step('FF7：refusedCli 只有預期的次數（FD 不執行 CLI）', refusedOk,
    Object.keys(EXPECTED_REFUSED).map((k) => `${k} ${devs.map((d) => `${d.name} ${outcome.refused[k]?.[d.name] ?? '—'}／${EXPECTED_REFUSED[k][d.name]}`).join('、')}`).join('；')
      + `；合計 OFFICE ${rc0.OFFICE} → ${rcEnd.OFFICE}、HOME ${rc0.HOME} → ${rcEnd.HOME}`,
    { refused: outcome.refused, expected: EXPECTED_REFUSED, rc0, rcEnd });

  // 11. 頁面乾淨
  outcome.page = await pageCleanStep(env, [P, Q], 'FF7：頁面乾淨（主控台、回應、CSP、對話框、XSS 計數）');
  return outcome;
}
