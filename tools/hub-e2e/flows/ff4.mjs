// FF4 草稿到新對話（多裝置部分；WP4.9 步 16，計畫塊 13）：瀏覽器的分頁 P 在側欄 HOME 的帳戶卡按「用這個帳戶開新對話」（只有 HOME 登入這個帳戶：
// 選電腦直接是 HOME，不跳選擇框）→ 伺服器的草稿（標頭的 deviceId 是 HOME）→ 打第一則、按 Enter（真的鍵盤）→ conversationClosed 的 replacedBy。
// 斷言：新對話 id＝idFromCommandId(伺服器發給 HOME 的 conversationCreate 的指令 id)（設計:3264；指令 id 從 HOME 的遠端紀錄與伺服器稽核的
// command.issued 兩邊讀，要相同）；HOME 有這個本機對話（不是複本、標題是第一則、Ord 都配好）；OFFICE 的 replicas 出現它的複本（擁有者 HOME）；
// 清單裡它在 HOME 底下。同一窗格與第二個分頁的 replacedBy 由 remote-control.mjs 第 8 段守（P46:1093），這裡不重複。
import crypto from 'node:crypto';
import { appLayout, openApp, waitFor } from '../lib/harness.mjs';
import { countBy } from '../lib/flow-rules.mjs';
import { idFromCommandId } from '../lib/stats-rules.mjs';
import { auditSince, ffDrafts, ffEvents, ffList, ffWaitResult, flowRecorder, stepUp, trackHubFrames } from '../lib/flow-page.mjs';
import { control, deviceStatus, localCommand, newConversation, pageCleanStep, replicaOf, waitSnapshots } from './common.mjs';

/** 草稿的第一則（也是新對話的標題） */
const FIRST = 'FF4 草稿的第一則訊息';

const convSel = (id) => '.conv[data-conv="' + id + '"]';
const taSel = (id) => convSel(id) + ' textarea.cinput';

/** startEnv 的選項：預設的兩台；頁面記錄器 */
export const env = {
  initOpts: { lightMessages: true },
  initScripts: [{ fn: flowRecorder }],
};

export async function run(env) {
  const office = env.devices.get('OFFICE');
  const home = env.devices.get('HOME');
  const outcome = {};
  const nonce = crypto.randomBytes(4).toString('hex');
  const reply = `FF4 新對話的回覆 ${nonce}`;

  // 1. OFFICE 一個基準對話（版面開著它：網頁沒有分頁時會自己開草稿，FF4 要自己選電腦）；HOME 的帳戶（logicalId）與下一輪的回覆
  const base = await newConversation(office, 'ff4', 'FF4 基準');
  const homeStatus = await deviceStatus(home);
  const account = (homeStatus?.accounts ?? []).find((a) => a.provider === 'Claude') ?? null;
  const logical = account?.logicalId ?? null;
  await control(home, 'nextRemoteTurn', { turn: { steps: [{ text: reply }], outcome: 'completed' } });
  const P = await openApp(env, 'P', { layout: appLayout([{ tabs: [base] }]), beforeGoto: trackHubFrames });
  const ready = P.live && (await waitSnapshots(P, [base]));
  const cardSel = '.macct[data-key="' + logical + '"]';
  const card = logical ? await P.page.waitForSelector(cardSel, { timeout: 15000 }).then(() => true, () => false) : false;
  const before = {
    session: homeStatus?.session ?? null,
    remote: ((await control(home, 'remoteAudit', { limit: 1 })).rows ?? [])[0]?.seq ?? 0,
    audit: (await stepUp(P, env.password)).status === 200 ? (await auditSince(P, Number.MAX_SAFE_INTEGER)).top : null,
  };
  env.step('FF4：頁面開著 OFFICE 的對話；HOME 的帳戶卡在側欄', !!ready && card && before.audit !== null,
    `P ${P.live ? '連上' : '沒連上'}、快照${ready ? '收到' : '沒收到'}；HOME 的帳戶 ${account?.id ?? '—'}（logicalId ${logical ?? '—'}）的卡片${card ? '在' : '不在'}側欄；`
      + `HOME 的遠端紀錄最新 seq ${before.remote}；稽核最新 seq ${before.audit ?? '—'}`,
    { base, account, before });
  if (!ready || !card) throw new Error('頁面或 HOME 的帳戶卡沒有準備好');

  // 2. 選 HOME：帳戶卡的「+」（滑過才顯示）→ newConversation 帶 HOME 的 deviceId 與這個帳戶 → 草稿（標頭的 deviceId 是 HOME）
  await P.page.bringToFront();
  const f0 = P.frames.length;
  await P.page.hover(cardSel);
  await P.page.click(cardSel + ' .macct-new', { timeout: 5000 });
  const nc = await waitFor(() => P.frames.slice(f0).find((f) => f.kind === 'cmd' && f.cmd === 'newConversation') ?? null, 5000, 50);
  const draft = await waitFor(async () => (await ffDrafts(P)).find((d) => d.deviceId === home.deviceId) ?? null, 10000, 100);
  const asked = await P.page.evaluate(() => !!document.querySelector('.dlg-pick-dev')).catch(() => null);
  const shown = draft ? await P.page.waitForSelector(taSel(draft.id), { timeout: 10000 }).then(() => true, () => false) : false;
  outcome.draft = { newConversation: !!nc, deviceIsHome: nc?.deviceId === home.deviceId, accountIsHome: nc?.accountId === logical, draft: !!draft, asked, composer: shown };
  env.step('FF4：選 HOME 開新對話（伺服器的草稿，開在 HOME）', !!nc && nc.deviceId === home.deviceId && nc.accountId === logical && !!draft && asked === false && shown,
    `newConversation ${nc ? `帶 deviceId ${nc.deviceId === home.deviceId ? 'HOME' : nc.deviceId ?? '—'}、accountId ${nc.accountId === logical ? 'HOME 的帳戶' : nc.accountId ?? '—'}` : '沒送'}`
      + `（只有 HOME 登入這個帳戶：${asked === false ? '沒有跳選擇框' : asked ? '跳了選擇框' : '讀不到'}）；草稿 ${draft?.id ?? '—'}、標頭的電腦 ${draft ? 'HOME' : '—'}、輸入框${shown ? '在' : '不在'}`,
    { newConversation: nc, draft });
  if (!draft || !shown) throw new Error('沒有開在 HOME 的草稿');

  // 3. 第一則（真的鍵盤）→ send 的 commandResult、conversationClosed 的 replacedBy
  const f1 = P.frames.length;
  await P.page.fill(taSel(draft.id), FIRST);
  await P.page.press(taSel(draft.id), 'Enter');
  const send = await waitFor(() => P.frames.slice(f1).find((f) => f.kind === 'cmd' && f.cmd === 'send' && f.conv === draft.id) ?? null, 5000, 50);
  const sendRes = send ? await ffWaitResult(P, send.commandId, 20000) : null;
  const closed = await waitFor(async () => (await ffEvents(P))?.closed?.find((c) => c.c === draft.id && c.replacedBy) ?? null, 20000, 100);
  const newId = closed?.replacedBy ?? null;
  env.step('FF4：送出第一則，草稿換成正式對話（replacedBy）', sendRes?.state === 'done' && !!newId && /^[0-9a-f]{32}$/u.test(newId),
    `send ${send ? sendRes?.state ?? '沒有結果' : '沒送'}；conversationClosed ${closed ? `replacedBy ${newId}` : '沒收到'}`, { send, sendResult: sendRes, closed });
  if (!newId) throw new Error('草稿沒有換成正式對話');

  // 4. 新對話 id＝idFromCommandId(cid)：cid 是伺服器發給 HOME 的 conversationCreate（HOME 的遠端紀錄）、也是稽核 command.issued 的 target
  const created = await waitFor(async () => {
    const r = await control(home, 'remoteAudit', { limit: 20 });
    const rows = (r.rows ?? []).filter((x) => x.seq > before.remote && x.cmd === 'conversationCreate');
    return rows.length > 0 && rows.every((x) => x.state) ? rows : null;
  }, 20000, 300);
  const cid = created?.[0]?.commandId ?? null;
  const step = await stepUp(P, env.password);
  const audit = step.status === 200 && before.audit !== null ? await auditSince(P, before.audit) : { status: null, entries: [] };
  const issued = audit.entries.filter((e) => e.action === 'command.issued');
  const auditHasCid = issued.some((e) => e.target === cid);
  const derived = idFromCommandId(cid);
  outcome.id = {
    createRows: created?.length ?? 0, createState: created?.[0]?.state ?? null, equalsDerived: !!derived && derived === newId, auditHasCid,
    // 瀏覽器的 send 的 commandId 不是新對話的來源（伺服器另發 conversationCreate 的指令 id）
    fromBrowserCommandId: !!send && idFromCommandId(send.commandId) === newId,
  };
  env.step('FF4：新對話 id＝idFromCommandId(conversationCreate 的指令 id)（設計:3264）', created?.length === 1 && created[0].state === 'done' && derived === newId && auditHasCid,
    `HOME 的遠端紀錄 conversationCreate ${created?.length ?? 0} 列（${created?.[0]?.state ?? '—'}${created?.[0]?.code ? `／${created[0].code}` : ''}）、指令 id ${cid ?? '—'} → ${derived ?? '—'}，`
      + `${derived === newId ? '等於' : '不等於'} replacedBy；稽核（近期驗證 ${step.status}）command.issued ${issued.length} 筆（${JSON.stringify(countBy(issued, 'outcome'))}），${auditHasCid ? '有' : '沒有'}這個指令 id；`
      + `瀏覽器 send 的 commandId 換算${outcome.id.fromBrowserCommandId ? '也等於' : '不等於'}新對話 id`,
    { cid, derived, newId, createRows: created, audit: { stepUp: step.status, issued: issued.map((e) => ({ target: e.target, outcome: e.outcome })) } });

  // 5. 這一輪跑完（HOME 的假會話收到第一則、清單上不再執行中、標題是第一則、在 HOME 底下）
  const settled = await waitFor(async () => {
    const s = (await deviceStatus(home, 10000))?.session;
    const row = ((await ffList(P)) ?? []).find((c) => c.id === newId);
    return s && before.session && s.sends - before.session.sends === 1 && row && row.running === false ? { session: s, row } : null;
  }, 30000, 300);
  const listedOk = !!settled && settled.row.deviceId === home.deviceId && settled.row.title === FIRST;
  // HOME 有這個本機對話（不是複本）、Ord 都配好、發布過
  const local = (await localCommand(home, 'open', { conversationId: newId })).conversation;
  await localCommand(home, 'close', { conversationId: newId });
  const ords = await control(home, 'ords', { conversation: newId });
  const localOk = local?.exists === true && local.replica === false && local.title === FIRST && local.items >= 2 && ords.withoutOrd === 0 && ords.withoutPublishedOrd === 0;
  outcome.home = { sends: settled ? settled.session.sends - before.session.sends : null, listedUnderHome: settled?.row.deviceId === home.deviceId, title: settled?.row.title === FIRST,
    local: local?.exists ?? null, replica: local?.replica ?? null, ordsComplete: ords.withoutOrd === 0 && ords.withoutPublishedOrd === 0 };
  env.step('FF4：HOME 有這個本機對話（清單在 HOME 底下、標題是第一則）', listedOk && localOk,
    `假會話 send +${outcome.home.sends ?? '—'}；清單 ${settled ? `在 ${settled.row.deviceId === home.deviceId ? 'HOME' : settled.row.deviceId} 底下、標題「${settled.row.title}」` : '30 秒內沒有跑完'}；`
      + `HOME 的中樞：${local?.exists ? `${local.replica ? '是複本' : '本機的對話'}、標題「${local.title}」、${local.items} 則` : '沒有'}；Ord ${ords.count} 則（沒配的 ${ords.withoutOrd}、沒發布的 ${ords.withoutPublishedOrd}）`,
    { local, ords: { count: ords.count, withoutOrd: ords.withoutOrd, withoutPublishedOrd: ords.withoutPublishedOrd }, row: settled?.row ?? null });

  // 6. OFFICE 的 replicas 出現它的複本（擁有者 HOME、標題是第一則）
  const replica = await waitFor(async () => {
    const r = await replicaOf(office, newId);
    return r && !r.deleted && r.ownerDeviceId === home.deviceId && r.title === FIRST ? r : null;
  }, 30000, 500);
  const officeView = (await localCommand(office, 'open', { conversationId: newId })).conversation;
  await localCommand(office, 'close', { conversationId: newId });
  outcome.office = { replica: !!replica, owner: replica?.ownerDeviceId === home.deviceId, policy: replica?.policy ?? null, hubReplica: officeView?.replica ?? null };
  env.step('FF4：OFFICE 的 replicas 出現它的複本（擁有者 HOME）', !!replica && officeView?.exists === true && officeView.replica === true,
    `OFFICE 的複本 ${replica ? `擁有者 ${replica.ownerDeviceId === home.deviceId ? 'HOME' : replica.ownerDeviceId}、epoch ${replica.epoch}、政策 ${replica.policy}、標題「${replica.title}」、存著 ${replica.items} 則` : '30 秒內沒有出現'}；`
      + `OFFICE 的中樞把它當成${officeView?.exists ? (officeView.replica ? '複本（唯讀）' : '本機的對話') : '不存在'}`,
    { replica, officeView });

  // 7. 頁面乾淨
  outcome.page = await pageCleanStep(env, [P], 'FF4：頁面乾淨（主控台、回應、CSP、對話框、XSS 計數）');
  return outcome;
}
