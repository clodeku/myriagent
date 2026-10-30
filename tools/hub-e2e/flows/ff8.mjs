// FF8 本機政策與桌面安全（WP4.9 步 17，計畫塊 14）：HOME（Control、不允許遠端完全自動）兩個對話 c1、c2；網頁 P 開著 c1，另有一條原始連線。
// 斷言：改完全自動（setOption permission FullAccess）被 AllowRemoteFullAccess 擋下（policy；OFFICE 允許完全自動、同一個指令 done 是對照）；
// 桌面專屬指令（syncSetPolicy、syncSetConversationPolicy、syncPause、桌面專屬的 setting 鍵）回 desktop_only、沒有轉給電腦、政策沒變；
// HOME 改成 View、Off 時輸入區的停用說明逐字等於 WP4.6／WP4.8 共用的那一句「在 HOME 的設定中允許遠端操作」（P48:926）、按 Enter 不送出、
// 直接送的 send 回 policy；遠端刪除 c2 之後回收區每個檔的 SHA-256 等於刪除前（SEC-27；多出來的只能是伺服器確認刪除後寫進去的
// sync-server-deleted.json）；restoreFromTrash 之後寫了 sync-server-deleted.json、
// 伺服器上仍是刪除（設計:3234）；解除配對後有 {資料根}\remote-audit\{deviceId}.jsonl（P44:1135），裡面有這一段的遠端指令。
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { appLayout, openApp, postCmd, waitFor } from '../lib/harness.mjs';
import { ffList, ffWaitResult, flowRecorder, rawClose, rawCmd, rawCompletion, rawConnect, rawMessages, rawWait, trackHubFrames } from '../lib/flow-page.mjs';
import { control, newConversation, pageCleanStep, serverConversation, sleep, waitSnapshots } from './common.mjs';

/** WP4.6（liverules.policyText）與 WP4.8（accountrules.remoteAllowed）共用的那一句（P48:926） */
const POLICY_SENTENCE = (name) => `在 ${name} 的設定中允許遠端操作`;

/** 伺服器刪除的旁檔（ConversationStore.ServerDeletedFileName） */
const SERVER_DELETED_FILE = 'sync-server-deleted.json';

const convSel = (id) => '.conv[data-conv="' + id + '"]';
const taSel = (id) => convSel(id) + ' textarea.cinput';

/** startEnv 的選項：預設的兩台；頁面記錄器 */
export const env = {
  initOpts: { lightMessages: true },
  initScripts: [{ fn: flowRecorder }],
};

const sha256 = (buf) => crypto.createHash('sha256').update(buf).digest('hex');

/** 資料夾底下每個檔的 SHA-256（相對路徑 → 雜湊，依路徑排序）；資料夾不在回 null */
function hashTree(root) {
  if (!fs.existsSync(root)) return null;
  const out = {};
  const walk = (dir) => {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      const p = path.join(dir, e.name);
      if (e.isDirectory()) walk(p);
      else if (e.isFile()) out[path.relative(root, p).split(path.sep).join('/')] = sha256(fs.readFileSync(p));
    }
  };
  walk(root);
  return Object.fromEntries(Object.entries(out).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0)));
}

/** 兩份雜湊表的差異：{ missing（之前有、之後沒有）, changed, extra（之後才有） } */
function treeDiff(before, after) {
  const b = before ?? {};
  const a = after ?? {};
  return {
    missing: Object.keys(b).filter((k) => !(k in a)),
    changed: Object.keys(b).filter((k) => k in a && a[k] !== b[k]),
    extra: Object.keys(a).filter((k) => !(k in b)),
  };
}

/** 原始連線送一個指令：{ commandId, accepted, error（Cmd 的回傳）, state, code（commandResult；Cmd 被拒時沒有）, ms } */
async function rawResult(app, cmd, ms = 20000) {
  const t0 = Date.now();
  const sent = await rawCmd(app, cmd);
  const done = sent.invocationId ? await rawCompletion(app, sent.invocationId, ms) : null;
  const accepted = done?.result?.accepted ?? null;
  const error = done?.result?.error ?? done?.error ?? null;
  const r = sent.invocationId && accepted !== false ? await rawWait(app, { type: 'commandResult', commandId: sent.commandId }, { ms }) : null;
  return { commandId: sent.commandId, accepted, error, state: r?.m.state ?? null, code: r?.m.code ?? null, message: r?.m.message ?? null, ms: Date.now() - t0 };
}

/** 原始連線看到的某台 DeviceView（最新的 devices，沒有時看 init.remote.devices） */
async function rawDevice(app, id) {
  const lists = await rawMessages(app, 'devices');
  const list = lists.length ? lists.at(-1).devices : (await rawMessages(app, 'init')).at(-1)?.remote?.devices;
  return (list ?? []).find((d) => d.id === id) ?? null;
}

/** 輸入區的停用說明（閘門顯示著才回文字；computed style 判斷看不看得到） */
function gateText(app, conv) {
  return app.page.evaluate((sel) => {
    const el = document.querySelector(sel);
    if (!el || el.hidden || getComputedStyle(el).display === 'none') return null;
    return (el.querySelector('.cgate-text') || el).textContent;
  }, convSel(conv) + ' [data-field="sendGate"]').catch(() => null);
}

/** HOME 的遠端操作紀錄裡 seq 大於 after 的列（舊到新） */
async function auditAfter(dev, after) {
  const r = await control(dev, 'remoteAudit', { limit: 200 });
  return (r.rows ?? []).filter((x) => x.seq > after).sort((a, b) => a.seq - b.seq);
}

export async function run(env) {
  const office = env.devices.get('OFFICE');
  const home = env.devices.get('HOME');
  const outcome = {};

  // 1. HOME 兩個對話、OFFICE 一個（完全自動的對照）；P 開著 c1、另有一條原始連線
  const c1 = await newConversation(home, 'ff8a', 'FF8 本機政策');
  const c2 = await newConversation(home, 'ff8b', 'FF8 回收區');
  const o1 = await newConversation(office, 'ff8o', 'FF8 完全自動的對照');
  const P = await openApp(env, 'P', { layout: appLayout([{ tabs: [c1] }]), beforeGoto: trackHubFrames });
  const ready = P.live && (await waitSnapshots(P, [c1]));
  const raw = await rawConnect(P);
  const audit0 = ((await control(home, 'remoteAudit', { limit: 1 })).rows ?? [])[0]?.seq ?? 0;
  env.step('FF8：HOME 的 c1、c2 與 OFFICE 的對照；P 開著 c1', !!ready && raw.ok,
    `P ${P.live ? '連上' : '沒連上'}、c1 的快照${ready ? '收到' : '沒收到'}、原始連線${raw.ok ? '連上' : `沒連上：${raw.error}`}；HOME 的遠端紀錄從 seq ${audit0} 之後算`, { c1, c2, o1 });
  if (!ready || !raw.ok) throw new Error('頁面沒有開著 HOME 的對話');

  // 2. 完全自動：HOME 不允許 → policy；OFFICE 允許 → done（對照）
  const fullHome = await rawResult(P, { cmd: 'setOption', conversationId: c1, key: 'permission', value: 'FullAccess' });
  const fullOffice = await rawResult(P, { cmd: 'setOption', conversationId: o1, key: 'permission', value: 'FullAccess' });
  const fullRows = await waitFor(async () => {
    const rows = (await auditAfter(home, audit0)).filter((r) => r.cmd === 'setOption');
    return rows.length >= 1 ? rows : null;
  }, 15000, 300);
  const fullRow = fullRows?.[0] ?? null;
  outcome.fullAccess = { home: `${fullHome.state}/${fullHome.code}`, office: `${fullOffice.state}${fullOffice.code ? `/${fullOffice.code}` : ''}`, audit: fullRow ? `${fullRow.state}/${fullRow.code}/${fullRow.step}/${fullRow.fullAccess}` : null };
  env.step('FF8：完全自動被 AllowRemoteFullAccess 擋下（policy）；允許的那台照常', fullHome.state === 'rejected' && fullHome.code === 'policy' && fullOffice.state === 'done'
    && fullRow?.state === 'rejected' && fullRow.code === 'policy' && fullRow.fullAccess === true,
    `HOME（不允許）：${fullHome.state ?? '沒有結果'}${fullHome.code ? `（${fullHome.code}）` : ''}${fullHome.message ? `「${fullHome.message}」` : ''}；OFFICE（允許）：${fullOffice.state ?? '沒有結果'}${fullOffice.code ? `（${fullOffice.code}）` : ''}；`
      + `HOME 的遠端紀錄：${fullRow ? `${fullRow.cmd} ${fullRow.state}／${fullRow.code}、閘門 ${fullRow.step}、層級 ${fullRow.tier}、FullAccess ${fullRow.fullAccess}` : '15 秒內沒有'}`,
    { fullHome, fullOffice, row: fullRow });

  // 3. 桌面專屬指令：伺服器回 desktop_only、不轉給電腦；HOME 的政策沒變
  const desktopOnly = [
    { label: 'syncSetPolicy（打開遠端完全自動）', cmd: { cmd: 'syncSetPolicy', remoteControlLevel: 'Control', allowRemoteFullAccess: true } },
    { label: 'syncSetConversationPolicy', cmd: { cmd: 'syncSetConversationPolicy', conversationId: c1, policy: 'localOnly' } },
    { label: 'syncPause', cmd: { cmd: 'syncPause', paused: true } },
    { label: 'setting autoUpdateCli', cmd: { cmd: 'setting', key: 'autoUpdateCli', value: false } },
    { label: 'setting instructions', cmd: { cmd: 'setting', key: 'instructions', value: 'FF8 不該寫進去' } },
  ];
  const denied = [];
  for (const d of desktopOnly) denied.push({ label: d.label, ...(await rawResult(P, d.cmd, 10000)) });
  await sleep(1500);
  const forwarded = (await auditAfter(home, audit0)).filter((r) => r.cmd !== 'setOption');
  const homeView = await rawDevice(P, home.deviceId);
  const deniedOk = denied.every((d) => d.accepted === false && d.error === 'desktop_only');
  outcome.desktopOnly = { errors: denied.map((d) => d.error), forwarded: forwarded.length, level: homeView?.remoteControlLevel ?? null, full: homeView?.allowRemoteFullAccess ?? null };
  env.step('FF8：桌面專屬指令回 desktop_only、沒有轉給電腦、政策沒變', deniedOk && forwarded.length === 0 && homeView?.remoteControlLevel === 'Control' && homeView.allowRemoteFullAccess === false,
    denied.map((d) => `${d.label}：${d.accepted === false ? `拒絕（${d.error}）` : d.accepted === true ? '接受了' : '沒有回傳'}`).join('；')
      + `；HOME 的遠端紀錄多了 ${forwarded.length} 列；HOME 的政策 ${homeView ? `${homeView.remoteControlLevel}、遠端完全自動 ${homeView.allowRemoteFullAccess}` : '—'}`,
    { denied, forwarded });

  // 4. View、Off：輸入區的停用說明逐字等於共用的那一句、按 Enter 不送出、直接送的 send 回 policy
  const sentence = POLICY_SENTENCE(home.name);
  const levels = {};
  for (const level of ['View', 'Off']) {
    await control(home, 'setPolicy', { level, fullAccess: false });
    const seen = await waitFor(async () => ((await rawDevice(P, home.deviceId))?.remoteControlLevel === level ? true : null), 20000, 200);
    await P.page.bringToFront();
    // 打字讓輸入區重算閘門（政策變了也會重畫；打字確保送出鈕的狀態是最新的）
    await P.page.fill(taSel(c1), `FF8 ${level} 下送出`);
    const text = await waitFor(async () => ((await gateText(P, c1)) === sentence ? sentence : null), 10000, 200);
    const shownText = text ?? (await gateText(P, c1));
    const from = P.frames.length;
    await P.page.press(taSel(c1), 'Enter');
    await sleep(800);
    const uiSent = P.frames.slice(from).filter((f) => f.kind === 'cmd' && f.cmd === 'send').length;
    const commandId = await postCmd(P, { cmd: 'send', conversationId: c1, text: `FF8 ${level} 直接送` });
    const res = commandId ? await ffWaitResult(P, commandId, 20000) : null;
    levels[level] = { seen: !!seen, text: shownText, exact: shownText === sentence, uiSent, state: res?.state ?? null, code: res?.code ?? null, message: res?.message ?? null };
    await P.page.fill(taSel(c1), '');
  }
  await control(home, 'setPolicy', { level: 'Control', fullAccess: false });
  const back = await waitFor(async () => ((await rawDevice(P, home.deviceId))?.remoteControlLevel === 'Control' ? true : null), 20000, 200);
  const gateCleared = back ? await waitFor(async () => ((await gateText(P, c1)) === null ? true : null), 10000, 200) : null;
  outcome.policy = Object.fromEntries(Object.entries(levels).map(([k, v]) => [k, { exact: v.exact, uiSent: v.uiSent, result: `${v.state}/${v.code}` }]));
  outcome.policyRestored = !!back && !!gateCleared;
  env.step('FF8：View、Off 下送出回 policy，停用說明逐字等於共用的那一句（P48:926）',
    Object.values(levels).every((v) => v.seen && v.exact && v.uiSent === 0 && v.state === 'rejected' && v.code === 'policy') && !!back && !!gateCleared,
    Object.entries(levels).map(([k, v]) => `${k}：P ${v.seen ? '看到了' : '20 秒內沒看到'}政策、輸入區「${v.text ?? '沒有說明'}」${v.exact ? '（逐字相同）' : `（預期「${sentence}」）`}、按 Enter 送出 ${v.uiSent} 則、`
      + `直接送 ${v.state ?? '沒有結果'}${v.code ? `（${v.code}）` : ''}`).join('；') + `；改回 Control：${back ? '看到了' : '沒看到'}、停用說明${gateCleared ? '消失' : '還在'}`,
    { sentence, levels });

  // 5. 遠端刪除 c2：回收區每個檔的 SHA-256 等於刪除前（SEC-27）
  const convDir = path.join(home.data, 'conversations', c2);
  const trashDir = path.join(home.data, 'conversations-trash', c2);
  const before = hashTree(convDir);
  const del = await rawResult(P, { cmd: 'delete', conversationId: c2 });
  const moved = await waitFor(() => (fs.existsSync(trashDir) && !fs.existsSync(convDir) ? true : null), 20000, 200);
  const inTrash = hashTree(trashDir);
  const trashDiff = treeDiff(before, inTrash);
  const srvDeleted = await waitFor(async () => ((await serverConversation(env, c2))?.deleted === true ? true : null), 20000, 300);
  const files = Object.keys(before ?? {}).length;
  // 多出來的只能是伺服器刪除的旁檔（刪除經伺服器確認後寫進回收區裡的資料夾：之後還原的對話只留在這台，設計:3234）
  const extraOk = trashDiff.extra.every((f) => f === SERVER_DELETED_FILE);
  const trashOk = del.state === 'done' && !!moved && files > 0 && trashDiff.missing.length === 0 && trashDiff.changed.length === 0 && extraOk;
  outcome.trash = { result: `${del.state}${del.code ? `/${del.code}` : ''}`, moved: !!moved, same: trashDiff.missing.length === 0 && trashDiff.changed.length === 0, extra: trashDiff.extra, serverDeleted: !!srvDeleted };
  env.step('FF8：遠端刪除之後回收區每個檔的 SHA-256 等於刪除前（SEC-27）', trashOk && !!srvDeleted,
    `delete：${del.state ?? '沒有結果'}${del.code ? `（${del.code}）` : ''}；資料夾${moved ? '搬進回收區' : '20 秒內沒有搬進回收區'}；刪除前 ${files} 個檔、回收區 ${Object.keys(inTrash ?? {}).length} 個：`
      + `少了 ${trashDiff.missing.length}、內容不同 ${trashDiff.changed.length}、多了 ${trashDiff.extra.join('、') || '沒有'}${extraOk ? '' : `（只容許 ${SERVER_DELETED_FILE}）`}；伺服器上${srvDeleted ? '標為刪除' : '20 秒內沒有標為刪除'}`,
    { before, diff: trashDiff });

  // 6. restoreFromTrash（這台的回收區還原鈕）：搬回來、寫了 sync-server-deleted.json（只在這台）、伺服器上仍是刪除、清單裡沒有它
  const restore = await control(home, 'restoreFromTrash', { conversation: c2 });
  await sleep(3000);
  const restored = hashTree(convDir);
  const restoredDiff = treeDiff(before, restored);
  const marker = fs.existsSync(path.join(convDir, SERVER_DELETED_FILE));
  const srvAfter = await serverConversation(env, c2);
  const listed = ((await ffList(P)) ?? []).some((c) => c.id === c2);
  const restoreOk = restore.restored === true && restore.wasInTrash === true && restore.serverDeleted === true && restore.inList === true && marker
    && restoredDiff.missing.length === 0 && restoredDiff.changed.length === 0 && srvAfter?.deleted === true && !listed;
  outcome.restore = { restored: restore.restored, serverDeleted: restore.serverDeleted, marker, extra: restoredDiff.extra, same: restoredDiff.missing.length === 0 && restoredDiff.changed.length === 0, stillDeleted: srvAfter?.deleted ?? null, listed };
  env.step('FF8：restoreFromTrash 之後寫了 sync-server-deleted.json、伺服器上仍是刪除（設計:3234）', restoreOk,
    `還原：${restore.restored ? '搬回來了' : '沒有搬回來'}（之前${restore.wasInTrash ? '在' : '不在'}回收區、回收區 ${restore.trashRoot}）；sync-server-deleted.json ${marker ? '有' : '沒有'}；`
      + `原本的檔少了 ${restoredDiff.missing.length}、內容不同 ${restoredDiff.changed.length}、多了 ${restoredDiff.extra.join('、') || '沒有'}；伺服器上${srvAfter?.deleted ? '仍是刪除' : '不是刪除'}；網頁的清單${listed ? '又有它' : '沒有它'}`,
    { restore, diff: restoredDiff, server: srvAfter });

  // 7. 這一段的遠端紀錄（解除配對之前讀：之後 sync.db 會被刪）；解除配對後有 {資料根}\remote-audit\{deviceId}.jsonl
  const rows = await auditAfter(home, audit0);
  await rawClose(P);
  const unpair = await control(home, 'unpair', {}, 60000);
  const exportPath = path.join(home.data, 'remote-audit', `${home.deviceId}.jsonl`);
  const lines = fs.existsSync(exportPath) ? fs.readFileSync(exportPath, 'utf8').split('\n').filter((l) => l.trim().length > 0) : [];
  const parsed = lines.map((l) => { try { return JSON.parse(l); } catch { return null; } });
  const cmds = parsed.filter(Boolean).map((r) => r.cmd ?? r.Cmd ?? null);
  const wanted = ['setOption', 'send', 'delete'];
  const exportOk = unpair.deviceId === home.deviceId && unpair.remoteAudit?.exists === true && path.resolve(unpair.remoteAudit.path) === path.resolve(exportPath)
    && lines.length > 0 && parsed.every(Boolean) && wanted.every((c) => cmds.includes(c));
  outcome.export = { exists: fs.existsSync(exportPath), parsed: parsed.every(Boolean), cmds: [...new Set(cmds)].sort() };
  env.step('FF8：解除配對後有 {資料根}\\remote-audit\\{deviceId}.jsonl（P44:1135）', exportOk,
    `unpair：${unpair.deviceId === home.deviceId ? '裝置 id 相同' : `裝置 id ${unpair.deviceId}`}；${exportPath}：${fs.existsSync(exportPath) ? `${lines.length} 行（${unpair.remoteAudit?.bytes ?? '—'} 位元組）、${parsed.every(Boolean) ? '每行都是 JSON' : '有行不是 JSON'}` : '不存在'}；`
      + `指令 ${[...new Set(cmds)].join('、') || '—'}（要有 ${wanted.join('、')}）；解除配對前 sync.db 裡這一段的列 ${rows.length} 列`,
    { unpair, rows: rows.map((r) => ({ cmd: r.cmd, state: r.state, code: r.code, step: r.step })) });

  // 8. 頁面乾淨
  outcome.page = await pageCleanStep(env, [P], 'FF8：頁面乾淨（主控台、回應、CSP、對話框、XSS 計數）');
  return outcome;
}
