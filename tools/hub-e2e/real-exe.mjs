// 真的 Myriagent.exe 的冒煙（WP4.9 步 20 的 R34，計畫 D23）：AgentHub 建構式裡的處理者已由假裝置涵蓋，但 app 的 IAgentHost、系統匣提醒與
// 同步接線只有真的 exe 走得到。流程：
//   1. 受測的桌面建置不比原始碼舊、Myriagent.dll 認得 MYRIAGENT_AGENT_PROBE_NO_CLI（UTF-16LE；認不得的建置會找到並執行這台電腦真正的 CLI，不跑）；
//      整個 bin 複製到工作資料夾（不鎖住 repo 的建置輸出）
//   2. lib/harness.mjs 的環境：延遲代理＋本機 MyriagentHub、一台假裝置 REAL（配對、種一個對話與一個隔離的假 Claude 帳戶、政策 Control）；網頁登入、
//      清單看得到那個對話；假裝置 quit（outbox 清空、配對資料留著）
//   3. 同一個資料根、同一個 Windows 使用者啟動真的 exe：探針（不送訊息、不找 CLI、結束後不關閉）、MYRIAGENT_SYNC_DEV_TRUST=1、空的 CLAUDE_CONFIG_DIR／CODEX_HOME、
//      剝掉本 session 的環境變數、tripwire 的 PATH（lib/real-exe-rules.mjs 的 appEnv）
//   4. 等 <資料根>\logs\ 出現「啟動提醒：遠端操作是 Control」；清單上那個對話的電腦重新上線；網頁對它送 rename → commandResult done、伺服器的標題變了
//   5. 只以這個 PID 結束真的 exe 的整棵樹（taskkill /PID <pid> /T /F）；收尾照 harness（假裝置 unpair、admin stop、金鑰、tripwire、密碼掃描）
// 系統匣的氣球由擁有者在 P4 目視（不在 WebView 的截圖裡）。只對自己以暫存資料根啟動的伺服器跑（沒有 --base）。
//
// 用法（先建置 slnx 與 tools/fake-device 的 Release、cd tools/hub-e2e && npm install）：
//   node real-exe.mjs [--work <TEMP 底下>] [--out <結果檔>] [--app <Myriagent.exe>] [--hub <MyriagentHub.exe>] [--fake-device <FakeDevice.exe>] [--headed] [--wait-seconds N]
// 結果檔預設在 tools/hub-e2e/out/real-exe-<時間>.json（每一步先寫）。結束碼：0 全部通過、1 有檢查失敗、2 用法錯誤。
import { spawn } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { appArgs, appEnv, hasNoCliSwitch, NO_CLI_VARIABLE, parseRealExeArgs, startupReminder } from './lib/real-exe-rules.mjs';
import { staleOutputs } from './lib/harness-rules.mjs';
import { createReport, killTree, latestList, openApp, postCmd, REPO, sleep, startEnv, waitCommandResult, waitFor } from './lib/harness.mjs';
import { pageCleanStep, serverConversation } from './flows/common.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const DEFAULT_APP_EXE = path.join(REPO, 'src', 'Myriagent', 'bin', 'Release', 'net10.0-windows', 'win-x64', 'Myriagent.exe');

/** 這台假裝置（預設的 OFFICE、HOME 以外的名稱：種子與政策都自己給） */
const DEVICE = { name: 'REAL', seed: { count: 1, seed: 20261201 }, policy: { level: 'Control', fullAccess: false, notify: false } };

let args;
try {
  args = parseRealExeArgs(process.argv.slice(2));
} catch (e) {
  console.error(String(e?.message ?? e));
  console.error('用法：node real-exe.mjs [--work <TEMP 底下>] [--out <結果檔>] [--app <Myriagent.exe>] [--hub <MyriagentHub.exe>] [--fake-device <FakeDevice.exe>] [--headed] [--wait-seconds N]');
  process.exit(2);
}

const stamp = new Date().toISOString().replace(/[-:]/g, '').replace('T', '-').slice(0, 15);
const work = path.resolve(args.work ?? path.join(os.tmpdir(), 'myriagent-tools', 'real-exe', stamp));
const outFile = path.resolve(args.out ?? path.join(here, 'out', `real-exe-${stamp}.json`));
const appExe = path.resolve(args.app ?? DEFAULT_APP_EXE);
const waitMs = args.waitSeconds * 1000;
const result = { tool: 'real-exe', startedAt: new Date().toISOString(), work, app: appExe, steps: [], outcome: {}, ok: null };
const report = createReport(outFile, result, () => result);
const step = (name, ok, summary, data) => report.step(name, ok, summary, data);

// ───── 1. 受測的桌面建置（startEnv 之前：這時還沒有延遲代理，同步 I/O 不擋任何轉送）─────

function walk(dir, accept, out = []) {
  let entries = [];
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return out;
  }
  for (const e of entries) {
    const full = path.join(dir, e.name);
    if (e.isDirectory()) {
      if (e.name === 'bin' || e.name === 'obj' || e.name === 'node_modules' || e.name.startsWith('.')) continue;
      walk(full, accept, out);
    } else if (accept(e.name)) {
      out.push(full);
    }
  }
  return out;
}

const mtimeOf = (f) => {
  try {
    return fs.statSync(f).mtimeMs;
  } catch {
    return null;
  }
};

/** 桌面建置的新鮮度：Myriagent.dll 不比桌面的原始碼（含嵌入的 Web）舊、Myriagent.Protocol.dll 不比 Protocol 的原始碼舊 */
function appFreshness(bin) {
  const sources = (files) => files.map((f) => ({ path: path.relative(REPO, f), mtime: mtimeOf(f) }));
  const src = (...p) => path.join(REPO, 'src', ...p);
  const outputs = [
    { label: 'Myriagent.dll', mtime: mtimeOf(path.join(bin, 'Myriagent.dll')), sources: sources(walk(src('Myriagent'), () => true).filter((f) => !/\.(md|txt)$/iu.test(f))) },
    { label: 'Myriagent.Protocol.dll', mtime: mtimeOf(path.join(bin, 'Myriagent.Protocol.dll')), sources: sources(walk(src('Myriagent.Protocol'), (n) => n.endsWith('.cs'))) },
  ];
  if (!fs.existsSync(path.join(bin, 'Myriagent.exe'))) outputs.push({ label: 'Myriagent.exe', mtime: null, sources: [] });
  return { ...staleOutputs(outputs), checked: outputs.length, sources: outputs.reduce((n, o) => n + o.sources.length, 0) };
}

function preflight() {
  const bin = path.dirname(appExe);
  const fresh = appFreshness(bin);
  step('桌面建置不比原始碼舊', fresh.ok, fresh.ok
    ? `${bin}：${fresh.checked} 個輸出、${fresh.sources} 個原始檔，都不比輸出新`
    : `先重建（dotnet build Myriagent.slnx -c Release -nodeReuse:false -p:UseSharedCompilation=false）：${fresh.stale.map((s) => `${s.label}${s.missing ? ' 不存在' : ` 比 ${s.newerCount} 個原始檔舊（${s.newer.slice(0, 3).join('、')}）`}`).join('；')}`,
  { stale: fresh.stale });
  if (!fresh.ok) return null;
  let dll = null;
  try { dll = fs.readFileSync(path.join(bin, 'Myriagent.dll')); } catch { /* 下面判 */ }
  const noCli = hasNoCliSwitch(dll);
  const sha256 = dll ? crypto.createHash('sha256').update(dll).digest('hex') : null;
  step(`受測的 Myriagent.dll 認得 ${NO_CLI_VARIABLE}（UTF-16LE）`, noCli,
    noCli ? `認得（SHA-256 ${sha256}）：探針不找、不執行任何 claude／codex／agy／grok` : '認不得：這個建置的探針會找到並執行這台電腦真正的 CLI，不跑', { sha256 });
  if (!noCli) return null;
  // 整個 bin 複製到工作資料夾（app 執行期間鎖住的是複本；同 measure_sync）
  const appDir = path.join(work, 'app');
  fs.rmSync(appDir, { recursive: true, force: true });
  fs.cpSync(bin, appDir, { recursive: true });
  const copied = walk(appDir, () => true);
  const copiedDll = fs.readFileSync(path.join(appDir, 'Myriagent.dll'));
  const same = crypto.createHash('sha256').update(copiedDll).digest('hex') === sha256;
  step('桌面建置複製到工作資料夾', same && copied.length > 0, `${copied.length} 個檔 → ${appDir}；Myriagent.dll 的 SHA-256 ${same ? '相同' : '不同'}`, { appDir, files: copied.length });
  return same ? { appDir, exe: path.join(appDir, 'Myriagent.exe'), sha256 } : null;
}

// ───── 3～5. 真的 exe ─────

/** 啟動真的 exe（非同步；不擋事件迴圈）：{ proc, pid, exited（Promise）, exit } */
function launchApp(exe, argv, env) {
  const proc = spawn(exe, argv, { cwd: path.dirname(exe), env, stdio: 'ignore', windowsHide: false });
  const app = { proc, pid: proc.pid, exit: undefined, error: null };
  app.exited = new Promise((resolve) => {
    proc.on('error', (e) => { app.error = String(e?.message ?? e); app.exit = null; resolve(null); });
    proc.on('exit', (code) => { app.exit = code; resolve(code); });
  });
  return app;
}

/** 資料根 logs\ 裡全部 .log 的文字（讀不了的略過） */
async function logText(dataDir) {
  const dir = path.join(dataDir, 'logs');
  let names = [];
  try { names = await fs.promises.readdir(dir); } catch { return ''; }
  let text = '';
  for (const n of names.filter((x) => x.endsWith('.log')).sort()) {
    try { text += await fs.promises.readFile(path.join(dir, n), 'utf8'); } catch { /* 寫入中，下次再讀 */ }
  }
  return text;
}

async function scenario(env, built) {
  const dev = env.devices.get(DEVICE.name);
  const outcome = result.outcome;

  // 2. 網頁看得到那個種子對話（電腦在線）
  const P = await openApp(env, 'P');
  if (!P.live) throw new Error('/app/ 沒有連上');
  const seeded = await waitFor(async () => ((await latestList(P)) ?? []).find((c) => c.deviceId === dev.deviceId && c.deviceOnline === true) ?? null, 60000, 300);
  step('網頁的清單有 REAL 的種子對話（假裝置在線）', !!seeded, seeded ? `對話 ${seeded.id}「${seeded.title}」` : '60 秒內沒有出現', { conversation: seeded ? { id: seeded.id, title: seeded.title } : null });
  if (!seeded) throw new Error('清單沒有種子對話');
  const conversationId = seeded.id;

  // 假裝置 quit：等 outbox 清空、配對資料留著（真的 exe 接著用同一個資料根）
  const q = await dev.run.quit(90000);
  const pairedLeft = fs.existsSync(path.join(dev.data, 'sync', 'device.json'));
  const quitOk = q.exit === 0 && !q.forced && q.final?.drained === true && pairedLeft;
  step('假裝置 quit（outbox 清空，配對資料留給真的 exe）', quitOk,
    `結束碼 ${q.exit}${q.forced ? '（逾時，已以 PID 結束）' : ''}、outbox ${q.final?.drained === true ? '清空' : '沒清空'}；sync\\device.json ${pairedLeft ? '在' : '不在'}`, { exit: q.exit, forced: q.forced, drained: q.final?.drained ?? null });
  if (!quitOk) throw new Error('假裝置沒有正常結束');
  const offline = await waitFor(async () => ((await latestList(P)) ?? []).find((c) => c.id === conversationId && c.deviceOnline === false) ?? null, 30000, 300);
  step('假裝置結束之後清單上的電腦離線', null, offline ? '離線了（真的 exe 上線之後才會再變成在線）' : '30 秒內清單沒有顯示離線（只記錄）');

  // 3. 真的 exe：同一個資料根、探針、開發信任、空的設定資料夾、tripwire 的 PATH
  const probeDir = path.join(work, 'probe');
  const claudeConfigDir = path.join(work, 'empty-config', 'claude');
  const codexHome = path.join(work, 'empty-config', 'codex');
  for (const d of [probeDir, claudeConfigDir, codexHome]) await fs.promises.mkdir(d, { recursive: true });
  const exeEnv = appEnv(env.internal.childEnv, { probeDir, claudeConfigDir, codexHome });
  const argv = appArgs(dev.data);
  const launchedAt = Date.now();
  let app = launchApp(built.exe, argv, exeEnv);
  // 剛結束的假裝置還沒放開這個資料根的單一實例鎖時，app 叫醒「既有的實例」後以 0 結束：等 1 秒重試，最多 15 秒
  let attempts = 1;
  while (true) {
    const early = await Promise.race([app.exited.then(() => true), sleep(3000).then(() => false)]);
    if (!early || Date.now() - launchedAt > 15000) break;
    attempts++;
    await sleep(1000);
    app = launchApp(built.exe, argv, exeEnv);
  }
  env.realApp = app;
  step('啟動真的 Myriagent.exe（同一個資料根、探針、不找 CLI、開發信任）', app.exit === undefined,
    `PID ${app.pid}${attempts > 1 ? `（試了 ${attempts} 次）` : ''}：${built.exe} --data ${dev.data}；CLAUDE_CONFIG_DIR、CODEX_HOME 是工作資料夾裡空的資料夾`,
    { pid: app.pid, attempts, env: Object.fromEntries(Object.entries(exeEnv).filter(([k]) => /^(MYRIAGENT_|CLAUDE_CONFIG_DIR$|CODEX_HOME$)/u.test(k))) });
  if (app.exit !== undefined) throw new Error(`真的 exe 起不來（結束碼 ${app.exit}${app.error ? `：${app.error}` : ''}）`);

  // 4a. 記錄檔的啟動提醒
  const reminder = await waitFor(async () => {
    if (app.exit !== undefined) return { exited: true };
    const r = startupReminder(await logText(dev.data));
    return r ? { ...r, ms: Date.now() - launchedAt } : null;
  }, waitMs, 500);
  outcome.reminder = reminder && !reminder.exited ? { level: reminder.level, fullAccess: reminder.fullAccess } : null;
  const reminderOk = !!reminder && !reminder.exited && reminder.level === 'Control' && reminder.fullAccess === DEVICE.policy.fullAccess;
  step('記錄檔：「啟動提醒：遠端操作是 Control」（R34）', reminderOk,
    reminder?.exited ? `真的 exe 在出現提醒之前結束了（結束碼 ${app.exit}）`
      : reminder ? `啟動之後 ${reminder.ms} ms：${reminder.line}` : `${args.waitSeconds} 秒內 ${path.join(dev.data, 'logs')} 沒有這一行`,
    { line: reminder?.line ?? null, ms: reminder?.ms ?? null });

  // 4b. 清單上的電腦重新上線（真的 exe 的同步連上）
  const online = await waitFor(async () => ((await latestList(P)) ?? []).find((c) => c.id === conversationId && c.deviceOnline === true) ?? null, waitMs, 300);
  outcome.online = !!online;
  step('真的 exe 連上：清單上那個對話的電腦在線', !!online, online ? `啟動之後 ${Date.now() - launchedAt} ms 內` : `${args.waitSeconds} 秒內沒有上線`);

  // 4c. 遠端 rename → done、伺服器的標題變了
  const title = `真的 exe 改名 ${crypto.randomBytes(3).toString('hex')}`;
  const commandId = online ? await postCmd(P, { cmd: 'rename', conversationId, title }) : null;
  const res = commandId ? await waitCommandResult(P, commandId, 30000) : null;
  const server = res?.state === 'done' ? await waitFor(async () => {
    const c = await serverConversation(env, conversationId);
    return c?.title === title ? c : null;
  }, 15000, 300) : null;
  const shown = res?.state === 'done' ? !!(await waitFor(async () => ((await latestList(P)) ?? []).some((c) => c.id === conversationId && c.title === title), 15000, 200)) : false;
  outcome.rename = res ? `${res.state}${res.code ? `/${res.code}` : ''}` : commandId ? 'no-result' : 'not-sent';
  outcome.serverTitle = !!server;
  outcome.titleShown = shown;
  step('網頁對真的 exe 送 rename：commandResult done、伺服器與清單的標題都變了', res?.state === 'done' && !!server && shown,
    `${commandId ? `commandResult ${res?.state ?? '沒有'}${res?.code ? `（${res.code}）` : ''}` : '沒有送出'}；伺服器的標題${server ? '變了' : '沒變'}；清單的標題${shown ? '跟著變了' : '沒變'}`,
    { commandId, result: res, title });

  // 真的 exe 的記錄檔：只記錄（錯誤行數、有沒有處理到遠端指令）
  const text = await logText(dev.data);
  const errors = text.split(/\r?\n/u).filter((l) => /\[ERROR\]/u.test(l));
  step('真的 exe 的記錄檔（只記錄）', null, `${text.split(/\r?\n/u).filter(Boolean).length} 行、[ERROR] ${errors.length} 行`, { errors: errors.slice(0, 20) });

  // 頁面乾淨
  outcome.page = await pageCleanStep(env, [P], '頁面乾淨（主控台、回應、CSP、對話框、XSS 計數）');
  return outcome;
}

/** 5. 只以這個 PID 結束真的 exe 的整棵樹；探針的 report.json 只記錄。Ctrl+C 的處理器與主流程的 finally 同時呼叫時拿到同一次 */
function stopApp(env) {
  const app = env.realApp;
  if (!app) return Promise.resolve();
  app.stopping ??= stopAppOnce(app);
  return app.stopping;
}

async function stopAppOnce(app) {
  const wasRunning = app.exit === undefined;
  const code = wasRunning ? await killTree(app.pid) : null;
  const ended = await Promise.race([app.exited.then(() => true), sleep(15000).then(() => false)]);
  result.outcome.appStopped = wasRunning && ended;
  step('以 PID 結束真的 exe 的整棵樹（taskkill /PID /T /F）', wasRunning && ended,
    `${wasRunning ? `taskkill 結束碼 ${code}、` : `已經自己結束（結束碼 ${app.exit}）、`}程序${ended ? '結束了' : '15 秒內沒有結束'}`, { pid: app.pid, taskkill: code, exit: app.exit ?? null });
  let probe = null;
  try { probe = JSON.parse(await fs.promises.readFile(path.join(work, 'probe', 'report.json'), 'utf8')); } catch { /* 探針還沒寫完 */ }
  step('探針的 report.json（只記錄）', null, probe
    ? `ok ${probe.ok}、uiReadyMs ${probe.uiReadyMs ?? '—'}、同步 ${probe.sync?.state ?? '—'}、consoleErrors ${(probe.consoleErrors ?? []).length} 筆、cli ${typeof probe.cli === 'string' ? probe.cli : '—'}`
    : '結束之前探針還沒寫出 report.json（KEEP=1 時探針要等同步安定與閒置取樣）', probe ? { ok: probe.ok, uiReadyMs: probe.uiReadyMs ?? null, sync: probe.sync?.state ?? null, error: probe.error ?? null } : null);
}

// ───── 主流程 ─────

let env = null;
try {
  const built = preflight();
  if (built) {
    try {
      env = await startEnv({ devices: [DEVICE], work: path.join(work, 'env'), report, hubExe: args.hub ?? undefined, fakeExe: args.fakeDevice ?? undefined, headed: args.headed, tool: 'real-exe' });
      // Ctrl+C（lib/harness.mjs 的處理器）：WinForms 的 exe 收不到主控台的 Ctrl+C，先以 PID 結束它再 env.close()
      const started = env;
      env.beforeInterruptClose = () => stopApp(started);
      await scenario(env, built);
    } catch (e) {
      env = env ?? e.env ?? null;
      if (!e.env) step('例外', false, String(e?.message ?? e), { stack: String(e?.stack ?? e) });
    } finally {
      if (env) {
        await stopApp(env);
        await env.close();
      }
    }
  }
} catch (e) {
  step('例外', false, String(e?.message ?? e), { stack: String(e?.stack ?? e) });
}
result.finishedAt = new Date().toISOString();
result.ok = result.steps.length > 0 && result.steps.every((s) => s.ok !== false);
report.save();
console.log(result.ok ? `全部通過，結果檔：${outFile}` : `有檢查失敗，結果檔：${outFile}`);
process.exit(result.ok ? 0 : 1);
