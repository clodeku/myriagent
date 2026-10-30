// 第 4 階段的全流程端到端（WP4.9 步 14 起，計畫 docs/plans/2026-10-20-wp4.9-e2e-perf-deploy.md）：瀏覽器 → 本機 MyriagentHub（經延遲代理）→ 兩台假裝置
// （OFFICE、HOME，假 CLI）→ 回到瀏覽器。只補 remote-control.mjs（單台裝置、網頁面的流程）沒有的多裝置、回補、持久性、回滾、帳戶與本機政策（計畫 D22）。
// 共用環境在 lib/harness.mjs（序列化鎖與續期、tripwire、延遲代理、正式站守門、兩台假裝置的控制通道、瀏覽器登入、收尾與金鑰檢查）。
//
// 用法（先建置 MyriagentHub 與 FakeDevice 的 Release、cd tools/hub-e2e && npm install）：
//   node full-flow.mjs [--only smoke,FF1,…] [--repeat N] [--work <TEMP 底下的資料夾>] [--out <結果檔>] [--hub <MyriagentHub.exe>] [--fake-device <FakeDevice.exe>] [--headed] [--prev-ref <提交>]
// 情境：smoke（步 14：環境起得來、四個功能都協商出、127.0.0.1 來源登入、cmd1 的遠端指令 done、收得乾淨）；
// FF1 配對與回補、FF2 多裝置即時與 S7、FF3 遠端操作一輪（步 15，flows/ff1.mjs～ff3.mjs）；FF4 草稿轉新對話與副本、FF5 副本與同步政策、
// FF6 媒體與檔案（離線退路、範圍外的路徑、blob 配額；步 16，flows/ff4.mjs～ff6.mjs）；FF7 遠端帳戶與 CLI、FF8 本機政策與桌面安全、FF10 撤銷
// （步 17，flows/ff7.mjs、ff8.mjs、ff10.mjs）；FF11 Grok 與 Antigravity 的對話（Grok／Antigravity 同步的 P5，flows/ff11.mjs）；FF9 持久性與回滾（步 18，flows/ff9.mjs：強制結束時的在途批次、同一資料根的三段回滾鏈、PRIV-R3、備份還原；
// 舊組建照計畫 D12 由 MYRIAGENT_SYNC_OLD_HUB、MYRIAGENT_SYNC_PREV_HUB、MYRIAGENT_SYNC_DEPLOYED_PREV_HUB＋--prev-ref <提交> 給，沒設的記「未執行」、整體不通過）。沒給 --only 時跑全部已有的。
// 每個情境各自一個全新的環境（自己的種子、配對順序、瀏覽器內容；工作資料夾 run-N\<情境>），收尾後下一個情境再取鎖。
// 沒有 --base：只對自己以暫存資料根啟動的伺服器跑（同 pairing.mjs）。--repeat N 每次都是全新的環境，最後以 sameOutcome 比對每一次的結果。
// 結果檔預設在 tools/hub-e2e/out/full-flow-<時間>.json（每一步先寫；runs[].measurements 是 S7 的樣本摘要）。結束碼：0 全部通過、1 有檢查失敗、2 用法錯誤、
// 130 按了 Ctrl+C（lib/harness.mjs 照 env.close() 收尾之後結束，最多約 2 分鐘；再按一次直接結束）。
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { missingFeatures, parseFullFlowArgs, REQUIRED_FEATURES } from './lib/harness-rules.mjs';
import { createReport, front, latestList, openApp, postCmd, startEnv, unexpected, waitCommandResult, waitFor } from './lib/harness.mjs';
import { sameOutcome } from './lib/stats-rules.mjs';
import { xssState } from '../ui-probe/hub-page.mjs';
import * as ff1 from './flows/ff1.mjs';
import * as ff2 from './flows/ff2.mjs';
import * as ff3 from './flows/ff3.mjs';
import * as ff4 from './flows/ff4.mjs';
import * as ff5 from './flows/ff5.mjs';
import * as ff6 from './flows/ff6.mjs';
import * as ff7 from './flows/ff7.mjs';
import * as ff8 from './flows/ff8.mjs';
import * as ff9 from './flows/ff9.mjs';
import * as ff10 from './flows/ff10.mjs';
import * as ff11 from './flows/ff11.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
let args;
try {
  args = parseFullFlowArgs(process.argv.slice(2));
} catch (e) {
  console.error(String(e?.message ?? e));
  console.error('用法：node full-flow.mjs [--only smoke,…] [--repeat N] [--work <TEMP 底下>] [--out <結果檔>] [--hub <MyriagentHub.exe>] [--fake-device <FakeDevice.exe>] [--headed] [--prev-ref <提交>]');
  process.exit(2);
}

const stamp = new Date().toISOString().replace(/[-:]/g, '').replace('T', '-').slice(0, 15);
const workRoot = path.resolve(args.work ?? path.join(os.tmpdir(), 'myriagent-tools', 'full-flow', stamp));
const outFile = path.resolve(args.out ?? path.join(here, 'out', `full-flow-${stamp}.json`));
const result = { tool: 'full-flow', startedAt: new Date().toISOString(), args: { only: args.only, repeat: args.repeat, prevRef: args.prevRef }, workRoot, runs: [], same: null, ok: null };
let current = null;
const report = createReport(outFile, result, () => current);

// ───── 情境 ─────

/**
 * smoke（步 14）：兩台都協商出 conv1、live1、cmd1、media1；/app/ 在 127.0.0.1 來源連上；清單有兩台的種子對話；
 * 對兩台各送一個 rename（cmd1 的遠端指令；信封的 aud 是 PublicOrigin＝配對網址，R35）→ commandResult done、清單的標題跟著變；
 * 主控台、CSP、對話框、XSS 計數都乾淨。回傳不含時間與 id 的結果（sameOutcome 比對）
 */
async function smoke(env) {
  const outcome = { features: {}, missing: {}, live: false, listed: {}, rename: {}, titleShown: {}, page: null };
  // 1. 協商出的功能（假裝置的 status）
  for (const dev of env.devices.values()) {
    const s = await dev.run.control('status', {}, 30000);
    const features = s.ok ? s.result.features ?? [] : [];
    outcome.features[dev.name] = REQUIRED_FEATURES.filter((f) => features.includes(f));
    outcome.missing[dev.name] = missingFeatures(features);
    dev.status = s.ok ? s.result : dev.status;
  }
  const featuresOk = [...env.devices.keys()].every((n) => outcome.missing[n].length === 0);
  env.step(`協商出四個功能（${REQUIRED_FEATURES.join('、')}）`, featuresOk,
    [...env.devices.values()].map((d) => `${d.name}：${(d.status?.features ?? []).join('、') || '—'}${outcome.missing[d.name].length ? `（少 ${outcome.missing[d.name].join('、')}）` : ''}`).join('；'),
    { features: Object.fromEntries([...env.devices.values()].map((d) => [d.name, d.status?.features ?? null])) });

  // 2. /app/ 連上
  const P = await openApp(env, 'P');
  outcome.live = P.live;
  env.step('/app/ 連上（127.0.0.1 來源）', P.live, `${env.base}/app/：傳輸層 ${P.transport?.state ?? '—'}、收到 init ${P.transport?.hubInit === true}`, { transport: P.transport });
  if (!P.live) throw new Error('/app/ 沒有連上');

  // 3. 清單有兩台的種子對話
  const expectCount = (d) => d.cfg.seed.count;
  const list = await waitFor(async () => {
    const l = await latestList(P);
    return l && [...env.devices.values()].every((d) => l.filter((c) => c.deviceId === d.deviceId).length >= expectCount(d)) ? l : null;
  }, 60000, 300);
  for (const d of env.devices.values()) outcome.listed[d.name] = (list ?? (await latestList(P)) ?? []).filter((c) => c.deviceId === d.deviceId).length;
  const listOk = !!list && [...env.devices.values()].every((d) => outcome.listed[d.name] === expectCount(d));
  env.step('清單有兩台的種子對話', listOk, [...env.devices.values()].map((d) => `${d.name} ${outcome.listed[d.name]}／${expectCount(d)} 個`).join('、'), { listed: outcome.listed });

  // 4. cmd1：對兩台各送一個 rename（id 最小的那個種子對話）
  const rows = [];
  for (const d of env.devices.values()) {
    const conv = (list ?? []).filter((c) => c.deviceId === d.deviceId).map((c) => c.id).sort()[0] ?? null;
    const title = `smoke ${d.name} 改名`;
    await front(P);
    const commandId = conv ? await postCmd(P, { cmd: 'rename', conversationId: conv, title }) : null;
    const res = commandId ? await waitCommandResult(P, commandId, 30000) : null;
    const shown = res?.state === 'done'
      ? !!(await waitFor(async () => ((await latestList(P)) ?? []).some((c) => c.id === conv && c.title === title), 15000, 200))
      : false;
    outcome.rename[d.name] = res ? `${res.state}${res.code ? `/${res.code}` : ''}` : commandId ? 'no-result' : 'not-sent';
    outcome.titleShown[d.name] = shown;
    rows.push({ device: d.name, conversationId: conv, commandId, state: res?.state ?? null, code: res?.code ?? null, origin: res?.origin ?? null, shown });
  }
  const renameOk = rows.every((r) => r.state === 'done' && r.shown);
  env.step('cmd1：遠端改名 done（信封 aud＝PublicOrigin＝配對網址，R35）', renameOk,
    rows.map((r) => `${r.device}：${r.commandId ? `commandResult ${r.state ?? '沒有'}${r.code ? `（${r.code}）` : ''}` : '沒有送出'}、清單的標題${r.shown ? '跟著變了' : '沒變'}`).join('；'), { rows });

  // 5. 頁面乾淨：主控台、≥400 的回應、CSP、對話框、XSS 計數
  const u = unexpected(env);
  const xss = await xssState(P).catch(() => ({ xss: null }));
  outcome.page = { console: u.console.length, http: u.http.length, csp: u.csp.length, dialogs: u.dialogs.length, xss: xss.xss };
  env.step('頁面乾淨（主控台、回應、CSP、對話框、XSS 計數）', u.console.length === 0 && u.http.length === 0 && u.csp.length === 0 && u.dialogs.length === 0 && xss.xss === 0,
    `主控台錯誤 ${u.consoleTotal} 筆（非預期 ${u.console.length}）、≥400 的回應 ${u.httpTotal} 筆（非預期 ${u.http.length}）、CSP 違規 ${u.csp.length} 筆、對話框 ${u.dialogs.length} 個、XSS 計數 ${xss.xss}`,
    { console: u.console, http: u.http, csp: u.csp, dialogs: u.dialogs });
  return outcome;
}

/**
 * 情境（WP4.9 步 15 起每個情境各自一個全新的環境：FF1 的種子與配對順序、FF2 的兩個瀏覽器內容都不同）：
 * { env: startEnv 的選項（devices、seeds、initOpts、initScripts…）, run(env) → 不含時間與 id 的結果 }
 */
const SCENARIOS = {
  smoke: { env: {}, run: smoke },
  FF1: ff1,
  FF2: ff2,
  FF3: ff3,
  FF4: ff4,
  FF5: ff5,
  FF6: ff6,
  FF7: ff7,
  FF8: ff8,
  FF9: ff9,
  FF10: ff10,
  FF11: ff11,
};

/** 收尾的結果裡不含時間與 id 的部分（sameOutcome 比對「兩次都收得乾淨」） */
function cleanupOutcome(c) {
  if (!c) return null;
  return {
    devices: c.devices.map((d) => ({ name: d.name, exit: d.quit?.exit ?? null, forced: d.quit?.forced ?? null, ended: d.quit?.ended ?? null, drained: d.quit?.drained ?? null, unpair: d.unpair?.code ?? null, left: d.left })),
    hub: c.hub ? { adminCode: c.hub.adminCode, forced: c.hub.forced, keysLeft: c.hub.keysLeft.length } : null,
    keys: c.keys ? { deviceLeft: c.keys.deviceLeft.length, hubLeft: c.keys.hubLeft.length, altHubLeft: c.keys.altHubLeft?.length ?? 0 } : null,
    tripwire: c.tripwire ? { tripped: c.tripwire.tripped.length, hits: c.tripwire.hits.length, scanned: c.tripwire.scans > 0 } : null,
    password: c.passwordScan ? c.passwordScan.found.length : null,
  };
}

// ───── 主流程 ─────

for (let r = 1; r <= args.repeat; r++) {
  const run = { repeat: r, work: path.join(workRoot, `run-${r}`), scenarios: {}, outcomes: {}, measurements: {}, ok: null };
  result.runs.push(run);
  console.log(`───── 第 ${r}／${args.repeat} 次（${args.only.join('、')}）：${run.work}`);
  for (const name of args.only) {
    const scenario = SCENARIOS[name];
    current = { name, work: path.join(run.work, name), steps: [], ok: null };
    run.scenarios[name] = current;
    console.log(`── ${name}：${current.work}`);
    let env = null;
    try {
      env = await startEnv({ ...scenario.env, work: current.work, report, hubExe: args.hub ?? undefined, fakeExe: args.fakeDevice ?? undefined, headed: args.headed, tool: 'full-flow' });
      // 量測（S7 的樣本摘要）另外存在 run.measurements：結果檔與附錄 B 的本機數字
      env.measure = (key, value) => { run.measurements[key] = value; report.save(); };
      // 結果檔（步 17 的 FF7 掃描驗證碼時也掃它）
      env.resultFile = outFile;
      // FF9 的 DEPLOYED_PREV 是哪個提交建的（--prev-ref，計畫 D12）
      env.prevRef = args.prevRef;
      run.outcomes[name] = await scenario.run(env);
    } catch (e) {
      env = env ?? e.env ?? null;
      run.outcomes[name] = { error: String(e?.message ?? e) };
      if (!e.env) report.step(`${name}：例外`, false, String(e?.message ?? e), { stack: String(e?.stack ?? e) });
    } finally {
      if (env) run.outcomes[`${name}.cleanup`] = cleanupOutcome(await env.close());
    }
    current.ok = current.steps.every((s) => s.ok !== false);
    report.save();
  }
  run.ok = Object.values(run.scenarios).every((s) => s.ok);
  report.save();
}

if (args.repeat > 1) {
  const first = result.runs[0];
  const differ = result.runs.filter((run) => !sameOutcome(run.outcomes, first.outcomes)).map((run) => run.repeat);
  result.same = differ.length === 0;
  current = { repeat: 'all', steps: [] };
  result.summary = current;
  report.step(`${args.repeat} 次的結果相同（sameOutcome：不看時間、耗時、PID、埠）`, result.same,
    result.same ? `${args.repeat} 次的情境與收尾結果都相同` : `第 ${differ.join('、')} 次和第 1 次不同`, { differ });
}
result.finishedAt = new Date().toISOString();
result.ok = result.runs.every((run) => run.ok) && result.same !== false;
report.save();
console.log(result.ok ? `全部通過，結果檔：${outFile}` : `有檢查失敗，結果檔：${outFile}`);
process.exit(result.ok ? 0 : 1);
