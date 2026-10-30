// FF5 副本與同步政策（WP4.9 步 16，計畫塊 13）：HOME 一個對話 X（兩輪），OFFICE 有它的複本並開著（拉到全部項目）。
// 斷言：對複本的寫入（OFFICE 本機的改名、送出）被拒並跳「這是另一台電腦的對話（唯讀）」，HOME、伺服器、假會話都沒變；
// HOME 把 X 改成 metadata：伺服器清掉項目（設計:3100；items 與 conversation_runtime 沒有列、item_count 0）、網頁 open 只拿到標題（0 則、清單的標題還在）、
// OFFICE 的複本丟掉項目；改回 full：整份重送（伺服器、網頁、OFFICE 的複本都回到同樣的項目）；改成 localOnly：伺服器刪除（deleted_at）、清單沒有它、
// OFFICE 的複本跟著刪；再改回 full 被拒並跳「這個對話已從伺服器刪除…」（設計:3193），政策維持 localOnly。
import { appLayout, openApp, waitFor } from '../lib/harness.mjs';
import { ffList, flowRecorder, rawClose, rawConnect, rawReadAll, trackHubFrames } from '../lib/flow-page.mjs';
import { control, deviceStatus, localCommand, newConversation, pageCleanStep, replicaOf, serverConversation, sleep, waitSnapshots } from './common.mjs';

/** 對複本的本機寫入被拒的 toast（AgentHub.ReplicaReadOnlyMessage；逐字比對） */
const REPLICA_READ_ONLY = '這是另一台電腦的對話（唯讀）';

/** 已從伺服器刪除的對話改回 full／metadata 被拒的 toast（ConversationPolicyCommandRules.ServerDeletedReason；逐字比對） */
const SERVER_DELETED = '這個對話已從伺服器刪除，不能再同步；要同步請開新對話';

const itemSel = (id) => '.conv[data-conv="' + id + '"] .it';

/** startEnv 的選項：預設的兩台；頁面記錄器 */
export const env = {
  initOpts: { lightMessages: true },
  initScripts: [{ fn: flowRecorder }],
};

/** 畫面上這個對話有幾個項目（.it） */
const domItems = (app, id) => app.page.evaluate((sel) => document.querySelectorAll(sel).length, itemSel(id)).catch(() => null);

/** 兩組 id 一樣（不看順序） */
const sameIds = (a, b) => a.length === b.length && [...a].sort().join(',') === [...b].sort().join(',');

export async function run(env) {
  const office = env.devices.get('OFFICE');
  const home = env.devices.get('HOME');
  const outcome = {};

  // 1. HOME 的對話 X（兩輪）；伺服器有全部項目；OFFICE 有複本、開著時拉到全部項目
  const X = await newConversation(home, 'ff5', 'FF5 副本與政策');
  await control(home, 'stream', { conversation: 'ff5', account: home.cfg.alias, message: 'FF5 第二輪', segments: 3, delayMs: 0, wait: true }, 90000);
  const homeOrds = await control(home, 'ords', { conversation: X });
  const ids = (homeOrds.items ?? []).map((i) => i.id);
  const n = ids.length;
  const srv0 = await waitFor(async () => {
    const c = await serverConversation(env, X);
    return c && c.policy === 'full' && c.items === n && c.itemCount === n ? c : null;
  }, 30000, 500);
  const title = srv0?.title ?? null;
  const meta = await waitFor(async () => {
    const r = await replicaOf(office, X);
    return r && !r.deleted && r.ownerDeviceId === home.deviceId ? r : null;
  }, 30000, 500);
  const opened = meta ? (await localCommand(office, 'open', { conversationId: X })).conversation : null;
  const pulled = meta ? await waitFor(async () => {
    const r = await replicaOf(office, X);
    return r && r.open && r.items === n && r.hubItems === n ? r : null;
  }, 30000, 500) : null;
  outcome.setup = { items: n, server: !!srv0, replica: !!meta, opened: opened?.replica === true, pulled: !!pulled };
  env.step('FF5：HOME 的對話在伺服器上、OFFICE 的複本開著並拉到全部項目', n >= 4 && !!srv0 && !!meta && opened?.replica === true && !!pulled,
    `HOME 的 X ${X}：${n} 則；伺服器 ${srv0 ? `${srv0.items} 則、政策 ${srv0.policy}、標題「${title}」` : '30 秒內沒有全部項目'}；`
      + `OFFICE 的複本${meta ? `（擁有者 HOME、政策 ${meta.policy}）` : '沒出現'}、中樞把它當成${opened ? (opened.replica ? '複本' : '本機的對話') : '—'}、`
      + `${pulled ? `開著、存著 ${pulled.items} 則、中樞顯示 ${pulled.hubItems} 則` : '30 秒內沒有拉到全部項目'}`,
    { X, n, server: srv0, replica: pulled ?? meta, opened });
  if (!srv0 || !pulled) throw new Error('X 沒有在伺服器上，或 OFFICE 的複本沒有拉到全部項目');

  // 2. 對複本的寫入（OFFICE 本機的改名、送出）被拒；HOME、伺服器、兩邊的假會話都沒變
  const sessions = { home: (await deviceStatus(home))?.session ?? null, office: (await deviceStatus(office))?.session ?? null };
  const rename = await localCommand(office, 'rename', { conversationId: X, title: 'FF5 在複本上改的名稱' });
  const send = await localCommand(office, 'send', { conversationId: X, text: 'FF5 在複本上送出' });
  await sleep(2000);
  const homeView = (await localCommand(home, 'open', { conversationId: X })).conversation;
  await localCommand(home, 'close', { conversationId: X });
  const srvAfterWrite = await serverConversation(env, X);
  const replicaAfterWrite = await replicaOf(office, X);
  const after = { home: (await deviceStatus(home))?.session ?? null, office: (await deviceStatus(office))?.session ?? null };
  const unchanged = homeView?.title === title && srvAfterWrite?.title === title && srvAfterWrite?.items === n && replicaAfterWrite?.items === n
    && rename.conversation?.title === title && send.conversation?.items === n
    && after.home?.sends === sessions.home?.sends && after.office?.sends === sessions.office?.sends;
  const rejected = rename.toasts.includes(REPLICA_READ_ONLY) && send.toasts.includes(REPLICA_READ_ONLY);
  outcome.replicaWrite = { renameToast: rename.toasts.includes(REPLICA_READ_ONLY), sendToast: send.toasts.includes(REPLICA_READ_ONLY), unchanged };
  env.step('FF5：對複本的寫入被拒（唯讀），HOME、伺服器、假會話都沒變', rejected && unchanged,
    `OFFICE 改名 toast ${JSON.stringify(rename.toasts)}、送出 toast ${JSON.stringify(send.toasts)}；標題：OFFICE「${rename.conversation?.title ?? '—'}」、HOME「${homeView?.title ?? '—'}」、`
      + `伺服器「${srvAfterWrite?.title ?? '—'}」；項目：OFFICE 中樞 ${send.conversation?.items ?? '—'}、複本 ${replicaAfterWrite?.items ?? '—'}、伺服器 ${srvAfterWrite?.items ?? '—'}（原本 ${n}）；`
      + `假會話 send：HOME ${sessions.home?.sends ?? '—'}→${after.home?.sends ?? '—'}、OFFICE ${sessions.office?.sends ?? '—'}→${after.office?.sends ?? '—'}`,
    { rename, send, homeView, server: srvAfterWrite, replica: replicaAfterWrite, sessions, after });

  // 3. 網頁開著 X（版面）；另一條原始連線拿來 open 抽查
  const P = await openApp(env, 'P', { layout: appLayout([{ tabs: [X] }]), beforeGoto: trackHubFrames });
  const ready = P.live && (await waitSnapshots(P, [X]));
  const shownBefore = ready ? await waitFor(async () => ((await domItems(P, X)) === n ? n : null), 10000, 200) : null;
  const raw = await rawConnect(P);
  const read0 = raw.ok ? await rawReadAll(P, X) : null;
  env.step('FF5：網頁開著 X（畫面有全部項目）', !!ready && shownBefore === n && raw.ok && sameIds(read0.items.map((i) => i.id), ids),
    `P ${P.live ? '連上' : '沒連上'}；畫面 ${shownBefore ?? (await domItems(P, X)) ?? '—'}／${n} 則；原始連線 ${raw.ok ? `open 拿到 ${read0.items.length} 則${sameIds(read0.items.map((i) => i.id), ids) ? '（id 等於 HOME 的）' : '（id 不同）'}` : `沒連上：${raw.error}`}`,
    { read: read0 && { count: read0.items.length, pages: read0.pages } });
  if (!ready || !raw.ok) throw new Error('網頁沒有開著 X');

  // 4. metadata：伺服器清掉項目、網頁只剩標題、OFFICE 的複本丟掉項目
  const toMeta = await localCommand(home, 'syncSetConversationPolicy', { conversationId: X, policy: 'metadata' });
  const srvMeta = await waitFor(async () => {
    const c = await serverConversation(env, X);
    return c && c.policy === 'metadata' && c.items === 0 && c.itemCount === 0 && c.runtime === 0 ? c : null;
  }, 30000, 500);
  const readMeta = await rawReadAll(P, X);
  const rowMeta = ((await ffList(P)) ?? []).find((c) => c.id === X) ?? null;
  // waitFor 要真值才停：畫面沒有項目時回 true（不是 0）
  const domEmpty = await waitFor(async () => ((await domItems(P, X)) === 0 ? true : null), 15000, 200);
  const domMeta = domEmpty ? 0 : await domItems(P, X);
  const officeMeta = await waitFor(async () => {
    const r = await replicaOf(office, X);
    return r && !r.deleted && r.policy === 'metadata' && r.items === 0 && !r.hubItems ? r : null;
  }, 30000, 500);
  outcome.metadata = {
    toasts: toMeta.toasts.length, homePolicy: toMeta.conversation?.syncPolicy ?? null, serverPurged: !!srvMeta, browserItems: readMeta.ok ? readMeta.items.length : null,
    titleListed: rowMeta?.title === title, domItems: domMeta, officeDropped: !!officeMeta,
  };
  env.step('FF5：改成 metadata：伺服器清掉項目、網頁只剩標題、OFFICE 的複本丟掉項目（設計:3100）',
    toMeta.toasts.length === 0 && toMeta.conversation?.syncPolicy === 'metadata' && !!srvMeta && readMeta.ok && readMeta.items.length === 0 && rowMeta?.title === title && domMeta === 0 && !!officeMeta,
    `HOME 的政策 ${toMeta.conversation?.syncPolicy ?? '—'}（toast ${toMeta.toasts.length} 則）；伺服器 ${srvMeta ? `政策 metadata、items ${srvMeta.items} 列、item_count ${srvMeta.itemCount}、runtime ${srvMeta.runtime} 列、max_ord ${srvMeta.maxOrd}、標題「${srvMeta.title}」` : '30 秒內沒有清掉'}；`
      + `網頁 open ${readMeta.ok ? `${readMeta.items.length} 則` : readMeta.error}、清單的標題「${rowMeta?.title ?? '—'}」、畫面 ${domMeta ?? '—'} 則；`
      + `OFFICE 的複本 ${officeMeta ? `政策 metadata、存著 ${officeMeta.items} 則、中樞顯示 ${officeMeta.hubItems ?? '—'}` : '30 秒內沒有丟掉項目'}`,
    { server: srvMeta, browser: { count: readMeta.items.length, row: rowMeta }, office: officeMeta });

  // 5. 改回 full：整份重送（伺服器、網頁 open、OFFICE 的複本都回到同樣的項目）
  const toFull = await localCommand(home, 'syncSetConversationPolicy', { conversationId: X, policy: 'full' });
  const srvFull = await waitFor(async () => {
    const c = await serverConversation(env, X);
    return c && c.policy === 'full' && c.items === n && c.itemCount === n && c.removed === 0 ? c : null;
  }, 60000, 500);
  const readFull = srvFull ? await rawReadAll(P, X) : null;
  const domFull = srvFull ? await waitFor(async () => ((await domItems(P, X)) === n ? n : null), 15000, 200) : null;
  const officeFull = await waitFor(async () => {
    const r = await replicaOf(office, X);
    return r && !r.deleted && r.policy === 'full' && r.items === n && r.hubItems === n ? r : null;
  }, 60000, 500);
  const browserSame = !!readFull && readFull.ok && sameIds(readFull.items.map((i) => i.id), ids);
  outcome.backToFull = { toasts: toFull.toasts.length, server: !!srvFull, browserSame, domItems: domFull === n, office: !!officeFull };
  env.step('FF5：改回 full：整份重送', toFull.toasts.length === 0 && !!srvFull && browserSame && domFull === n && !!officeFull,
    `伺服器 ${srvFull ? `${srvFull.items} 則、政策 full` : '60 秒內沒有回來'}；網頁 open ${readFull ? `${readFull.items.length} 則${browserSame ? '（id 等於 HOME 的）' : '（id 不同）'}` : '—'}、畫面 ${domFull ?? (await domItems(P, X)) ?? '—'} 則；`
      + `OFFICE 的複本 ${officeFull ? `存著 ${officeFull.items} 則、中樞顯示 ${officeFull.hubItems}` : '60 秒內沒有回來'}`,
    { server: srvFull, office: officeFull });

  // 6. localOnly：伺服器刪除、清單沒有它、OFFICE 的複本跟著刪
  const toLocal = await localCommand(home, 'syncSetConversationPolicy', { conversationId: X, policy: 'localOnly' });
  const srvDel = await waitFor(async () => {
    const c = await serverConversation(env, X);
    return c && c.deleted ? c : null;
  }, 30000, 500);
  const gone = await waitFor(async () => (((await ffList(P)) ?? []).some((c) => c.id === X) ? null : true), 15000, 200);
  const officeDel = await waitFor(async () => {
    const r = await replicaOf(office, X);
    return !r || r.deleted ? { row: r } : null;
  }, 30000, 500);
  const marked = await waitFor(async () => {
    const c = (await localCommand(home, 'open', { conversationId: X })).conversation;
    return c?.serverDeleted ? c : null;
  }, 15000, 500);
  await localCommand(home, 'close', { conversationId: X });
  outcome.localOnly = { toasts: toLocal.toasts.length, homePolicy: toLocal.conversation?.syncPolicy ?? null, serverDeleted: !!srvDel, listGone: !!gone, officeGone: !!officeDel, marked: !!marked };
  env.step('FF5：改成 localOnly：伺服器刪除、清單沒有它、OFFICE 的複本跟著刪',
    toLocal.toasts.length === 0 && toLocal.conversation?.syncPolicy === 'localOnly' && !!srvDel && !!gone && !!officeDel && !!marked,
    `HOME 的政策 ${toLocal.conversation?.syncPolicy ?? '—'}、伺服器刪除的旁檔${marked ? '有' : '沒有'}；伺服器 ${srvDel ? `deleted_at 有值、items ${srvDel.items} 列` : '30 秒內沒有刪'}；`
      + `網頁的清單${gone ? '沒有它了' : '還有它'}；OFFICE 的複本${officeDel ? (officeDel.row ? '標成已刪除' : '整列拿掉') : '還在'}`,
    { server: srvDel, office: officeDel?.row ?? null, home: marked });

  // 7. 再改回 full：被拒並跳 toast（設計:3193），政策維持 localOnly、伺服器維持刪除
  const back = await localCommand(home, 'syncSetConversationPolicy', { conversationId: X, policy: 'full' });
  await sleep(1000);
  const srvStill = await serverConversation(env, X);
  outcome.refuseFull = { toast: back.toasts.includes(SERVER_DELETED), policy: back.conversation?.syncPolicy ?? null, serverDeleted: !!srvStill?.deleted };
  env.step('FF5：伺服器刪除之後改回 full 被拒並跳 toast（設計:3193）', back.toasts.includes(SERVER_DELETED) && back.conversation?.syncPolicy === 'localOnly' && !!srvStill?.deleted,
    `toast ${JSON.stringify(back.toasts)}；HOME 的政策 ${back.conversation?.syncPolicy ?? '—'}；伺服器${srvStill?.deleted ? '維持刪除' : '不是刪除狀態'}`, { back, server: srvStill });

  await rawClose(P);
  // 8. 頁面乾淨
  outcome.page = await pageCleanStep(env, [P], 'FF5：頁面乾淨（主控台、回應、CSP、對話框、XSS 計數）');
  return outcome;
}
