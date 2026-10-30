// FF11 Grok 與 Antigravity 的對話（設計 docs/plans/2026-10-22-grok-antigravity-sync.md 的 P5；第 4 階段 FF3 的同一套做法換成這兩種代理）：
// OFFICE 另有 Grok（隔離帳戶）與 Antigravity（本機預設帳戶的形狀）帳戶、宣告四種假 CLI；HOME 照舊只有 Claude。兩台都協商出 prov1。
// OFFICE 開 Grok、Antigravity、Claude 各一個對話（假會話，不執行任何 CLI），瀏覽器的分頁 P 三個窗格各開一個，照順序做——
//   標頭：provider、steer（Claude true；Grok、Antigravity false）、權限選單照種類（Antigravity 沒有「每步詢問」「自動」）；HOME 有兩個對話的副本；
//   帳戶矩陣有 grok、antigravity 的邏輯帳戶（OFFICE 那一格）；
//   Grok：送出 → 思考、待辦、工具 → 核准卡（真的點擊，帶 requestDigest）→ 計畫確認卡（approvePlan 帶 requestDigest）→ 串流中再送一則：送出鈕是「加入佇列」、
//         假會話沒有插入的嘗試、輸入區的佇列一則 → 這一輪完成後接著送、上一輪的用量照劇本 → 再送一則、串流中 stop；
//   Antigravity：送出 → 串流（沒有任何卡片）→ 再送一則排隊（同上）→ 接著送完 → 再送一則、串流中 stop；遠端登出 → rejected／policy（agy 沒有登出指令，D4）；
//   Claude（對照）：串流中再送一則 → 送出鈕是「插入這一輪」、中樞嘗試插入一次（假會話一律不插，照舊排隊、接著送）。
// 斷言：假會話每個動作只收到一次（status 的 session，含 steers）；refusedCli 不變（遠端登出在前置條件就被拒）；頁面乾淨。
import { appLayout, openApp, pageMsgs, postCmd, waitFor } from '../lib/harness.mjs';
import { counterDelta } from '../lib/flow-rules.mjs';
import { ffEvents, ffItems, ffList, ffSnapshot, ffWaitResult, flowRecorder, stampRecords, trackHubFrames } from '../lib/flow-page.mjs';
import { control, deviceStatus, pageCleanStep, replicaOf, sleep, waitSnapshots } from './common.mjs';

/** 標頭的權限選單（AgentPermissionModes.For：Antigravity 不能在對話中詢問，沒有「每步詢問」，--mode auto 不開放） */
const MODES = {
  Grok: ['Ask', 'AcceptEdits', 'Plan', 'Auto', 'FullAccess'],
  Antigravity: ['AcceptEdits', 'Plan', 'FullAccess'],
  Claude: ['Ask', 'AcceptEdits', 'Plan', 'Auto', 'FullAccess'],
};

/** 送出鈕的說明（composer.js）：執行中能插入這一輪的（Claude、Codex）與只能排隊的（Grok、Antigravity） */
const SEND_TITLE = { steer: '插入這一輪（不等這一輪完成，代理下一步就讀到）', queue: '加入佇列（這一輪完成後送出）' };

/** RemoteAccountRules.AntigravityLogoutMessage */
const AGY_LOGOUT = 'Antigravity 沒有登出指令，請到那台的 agy 互動介面輸入 /logout';

/** Grok 第二輪的用量（劇本的 usage → 標頭的 lastTurn；伺服器以 InvariantCulture 的 N0 寫） */
const GROK_USAGE = { input: 2345, output: 67, cached: 12 };

const convSel = (id) => '.conv[data-conv="' + id + '"]';
const taSel = (id) => convSel(id) + ' textarea.cinput';
const cardSel = (c, i) => convSel(c) + ' .it[data-id="' + i + '"]';

/** 「第 0001 段。」連起來（同 FF3） */
const segmentsText = (n) => Array.from({ length: n }, (_, i) => `第 ${String(i + 1).padStart(4, '0')} 段。`).join('');

/** startEnv 的選項：OFFICE 另有 Grok 與 Antigravity、四種假 CLI；完整的訊息（帳戶矩陣要整則看）；頁面記錄器 */
export const env = {
  devices: [
    { name: 'OFFICE', providers: ['Grok', 'Antigravity'], cli: { Claude: '2.1.0', Grok: '1.0.52', Antigravity: '1.4.5' } },
    'HOME',
  ],
  initOpts: {},
  initScripts: [{ fn: flowRecorder }],
};

/** 打字之後讀送出鈕的說明與輸入框的提示，再按 Enter（真的鍵盤事件）：{ frame（這次送出的 Cmd 框）, title, placeholder } */
async function typeSend(app, conv, text) {
  await app.page.bringToFront();
  const from = app.frames.length;
  await app.page.fill(taSel(conv), text);
  const ui = await app.page.evaluate((sel) => {
    const root = document.querySelector(sel);
    const send = root && root.querySelector('.cbtn.send');
    const ta = root && root.querySelector('textarea.cinput');
    return { title: send ? send.title : null, placeholder: ta ? ta.placeholder : null };
  }, convSel(conv)).catch(() => ({ title: null, placeholder: null }));
  await app.page.press(taSel(conv), 'Enter');
  const frame = await waitFor(() => app.frames.slice(from).find((f) => f.kind === 'cmd' && f.cmd === 'send' && f.conv === conv) ?? null, 5000, 50);
  return { frame, ...ui };
}

/** 等某種卡片在等待（記錄器的項目；有 requestDigest） */
const waitCard = (app, conv, kind, ms = 20000) =>
  waitFor(async () => (await ffItems(app, conv)).find((i) => i.kind === kind && i.status === 'Waiting' && typeof i.digest === 'string') ?? null, ms, 100);

/** 這個對話的串流收到至少 n 段（時間標記） */
const streamed = (app, conv, n, ms = 20000) => waitFor(async () => ((await stampRecords(app, [conv])).length >= n ? true : null), ms, 100);

/** 這個對話不在執行、佇列是空的 */
const idle = (app, conv, ms = 30000) => waitFor(async () => {
  const l = ((await ffList(app)) ?? []).find((c) => c.id === conv);
  return l && l.running === false && (await ffEvents(app))?.queue?.[conv] === 0 ? true : null;
}, ms, 200);

/** 頁面收到的最新帳戶清單（init 或 accounts） */
async function latestAccounts(app) {
  const msgs = await pageMsgs(app);
  for (let i = msgs.length - 1; i >= 0; i--) {
    const m = msgs[i];
    if ((m?.type === 'accounts' || m?.type === 'init') && Array.isArray(m.accounts)) return m.accounts;
  }
  return [];
}

/** 開一個對話（這個帳戶別名的固定回覆，wait:true）：回對話 id */
async function open(dev, alias, account, message) {
  const r = await control(dev, 'stream', { conversation: alias, account, message, segments: 1, delayMs: 0, wait: true }, 90000);
  if (r.outcome !== 'Completed' || typeof r.conversationId !== 'string') throw new Error(`${dev.name} 開對話 ${alias} 沒有完成：${JSON.stringify(r)}`);
  return r.conversationId;
}

/** 送一個指令（經 app.js 的傳輸層）、等它的 commandResult：{ commandId, state, code, message } */
async function sendCmd(app, cmd, ms = 30000) {
  const commandId = await postCmd(app, cmd);
  const r = commandId ? await ffWaitResult(app, commandId, ms) : null;
  return { commandId, state: r?.state ?? null, code: r?.code ?? null, message: r?.message ?? null };
}

const steersOf = async (dev) => (await deviceStatus(dev))?.session ?? null;

export async function run(env) {
  const office = env.devices.get('OFFICE');
  const home = env.devices.get('HOME');
  const outcome = {};

  // 1. 兩台協商出 prov1；OFFICE 開三個對話（Grok、Antigravity、Claude）
  const features = {};
  for (const d of [office, home]) features[d.name] = (await deviceStatus(d))?.features ?? [];
  const G = await open(office, 'ff11-grok', 'office-grok', 'FF11 Grok 的對話');
  const A = await open(office, 'ff11-agy', 'office-agy', 'FF11 Antigravity 的對話');
  const C = await open(office, 'ff11-claude', office.cfg.alias, 'FF11 Claude 的對照');
  const prov1 = Object.values(features).every((f) => f.includes('prov1'));
  outcome.prov1 = prov1;
  env.step('FF11：兩台協商出 prov1；OFFICE 開 Grok、Antigravity、Claude 各一個對話', prov1,
    `OFFICE ${features.OFFICE.join('、') || '—'}；HOME ${features.HOME.join('、') || '—'}；對話 Grok ${G}、Antigravity ${A}、Claude ${C}`, { features, G, A, C });
  if (!prov1) throw new Error('沒有協商出 prov1：伺服器或桌面沒有 Grok／Antigravity 的同步');

  // 2. P 三個窗格各開一個；標頭照代理種類；HOME 有兩個對話的副本
  const P = await openApp(env, 'P', { layout: appLayout([{ tabs: [G] }, { tabs: [A] }, { tabs: [C] }]), beforeGoto: trackHubFrames });
  const ready = P.live && (await waitSnapshots(P, [G, A, C]));
  const heads = {};
  for (const [k, id] of Object.entries({ Grok: G, Antigravity: A, Claude: C })) heads[k] = (await ffSnapshot(P, id))?.header ?? null;
  const headOk = (k, steer) => heads[k]?.provider === k && heads[k].steer === steer && JSON.stringify(heads[k].modes) === JSON.stringify(MODES[k]);
  const replicas = {};
  for (const [k, id] of Object.entries({ Grok: G, Antigravity: A })) {
    replicas[k] = await waitFor(async () => { const r = await replicaOf(home, id); return r && !r.deleted ? r : null; }, 30000, 500);
  }
  outcome.headers = Object.fromEntries(Object.entries(heads).map(([k, h]) => [k, h && { provider: h.provider, steer: h.steer, modes: h.modes, permission: h.permission }]));
  outcome.replicas = Object.fromEntries(Object.entries(replicas).map(([k, r]) => [k, !!r]));
  env.step('FF11：標頭照代理種類（provider、steer、權限選單）；HOME 有 Grok 與 Antigravity 對話的副本', !!ready && headOk('Grok', false) && headOk('Antigravity', false)
    && headOk('Claude', true) && heads.Antigravity.permission === 'AcceptEdits' && !!replicas.Grok && !!replicas.Antigravity,
  `P ${P.live ? '連上' : '沒連上'}、快照${ready ? '都收到' : '沒收到'}；`
      + Object.entries(heads).map(([k, h]) => `${k}：provider ${h?.provider ?? '—'}、steer ${h?.steer}、權限 ${h?.permission ?? '—'}（選單 ${(h?.modes ?? []).join('/')}）`).join('；')
      + `；HOME 的副本 Grok ${replicas.Grok ? `有（${replicas.Grok.policy}）` : '30 秒內沒有'}、Antigravity ${replicas.Antigravity ? `有（${replicas.Antigravity.policy}）` : '30 秒內沒有'}`,
  { heads, replicas });
  if (!ready) throw new Error('分頁沒有開著三個對話');

  // 3. 帳戶矩陣：Grok、Antigravity 的邏輯帳戶，OFFICE 那一格不是 absent
  const logical = await waitFor(async () => {
    const accounts = await latestAccounts(P);
    const pick = (p) => accounts.find((a) => a.provider === p && Array.isArray(a.devices) && a.devices.some((c) => c.deviceId === office.deviceId && c.status !== 'absent'));
    const g = pick('Grok');
    const a = pick('Antigravity');
    return g && a ? { Grok: g.id, Antigravity: a.id } : null;
  }, 30000, 300);
  outcome.logical = !!logical;
  env.step('FF11：帳戶矩陣有 Grok 與 Antigravity 的邏輯帳戶（OFFICE 那一格）', !!logical,
    logical ? `Grok ${logical.Grok}、Antigravity ${logical.Antigravity}` : '30 秒內沒有兩種的邏輯帳戶', { logical });

  const before = { session: await steersOf(office), refused: (await deviceStatus(office))?.refusedCli ?? null };

  // 4. Grok：送出 → 核准（真的點擊）→ 計畫確認（approvePlan）→ 串流中再送一則（排隊）→ 接著送完、用量
  await control(office, 'nextRemoteTurn', { turn: { steps: [
    { thinking: '先看資料夾裡有什麼', segments: 2 },
    { todo: [{ text: '讀設定', status: 'completed' }, { text: '改程式', status: 'inProgress' }, { text: '跑測試' }] },
    { tool: '執行指令', kind: 'bash', title: 'dotnet build', detail: '建置\n\ndotnet build', output: '建置成功' },
    { approval: '執行指令', kind: 'bash', title: 'npm test' },
    { planApproval: '# 計畫\n\n1. 改 `a.cs`\n2. 跑測試' },
    { text: segmentsText(24), segments: 24, delayMs: 250, stamp: true },
  ], outcome: 'completed' } });
  const g1 = await typeSend(P, G, 'FF11 Grok 第一則');
  const g1Res = g1.frame ? await ffWaitResult(P, g1.frame.commandId, 15000) : null;
  const card = await waitCard(P, G, 'Approval');
  const f4 = P.frames.length;
  if (card) {
    await P.page.bringToFront();
    await P.page.click(cardSel(G, card.id) + ' .opt[data-idx="1"]', { timeout: 10000 });
  }
  const approve = await waitFor(() => P.frames.slice(f4).find((f) => f.kind === 'cmd' && f.cmd === 'approve') ?? null, 5000, 50);
  const approveRes = approve ? await ffWaitResult(P, approve.commandId, 15000) : null;
  const plan = await waitCard(P, G, 'PlanApproval');
  const planRes = plan ? await sendCmd(P, { cmd: 'approvePlan', conversationId: G, itemId: plan.id, choice: 'manual', requestDigest: plan.digest }) : null;
  const kinds = (await ffItems(P, G)).map((i) => i.kind);
  outcome.grokCards = { send: g1Res?.state ?? null, approve: approveRes?.state ?? null, digestEqual: !!approve && approve.digest === card?.digest, plan: planRes?.state ?? null };
  env.step('FF11：Grok 送出 → 思考、待辦、工具 → 核准卡（帶 requestDigest）→ 計畫確認卡', g1Res?.state === 'done' && !!card && approve?.digest === card.digest
    && approveRes?.state === 'done' && !!plan && planRes?.state === 'done' && ['Thinking', 'Plan', 'Tool', 'Approval', 'PlanApproval'].every((k) => kinds.includes(k)),
  `送出 ${g1Res?.state ?? '—'}（送出鈕「${g1.title ?? '—'}」）；項目 ${kinds.join('、')}；核准卡 ${card ? card.id : '沒出現'}、approve ${approve ? (approve.digest === card?.digest ? '帶項目的 requestDigest' : '的 digest 不同') : '沒送'}、`
      + `結果 ${approveRes?.state ?? '—'}；計畫確認卡 ${plan ? plan.id : '沒出現'}、approvePlan ${planRes ? `${planRes.state}${planRes.code ? `（${planRes.code}）` : ''}` : '沒送'}`,
  { send: g1.frame, card, approve, plan, planRes, kinds });

  await control(office, 'nextRemoteTurn', { turn: { steps: [{ text: segmentsText(2), segments: 2, delayMs: 50 }], outcome: 'completed', usage: GROK_USAGE } });
  const gRunning = await streamed(P, G, 2);
  const gSteers0 = await steersOf(office);
  const g2 = await typeSend(P, G, 'FF11 Grok 排隊的一則');
  const g2Res = g2.frame ? await ffWaitResult(P, g2.frame.commandId, 15000) : null;
  const gQueued = await waitFor(async () => ((await ffEvents(P))?.queue?.[G] === 1 ? true : null), 5000, 100);
  const gSteers1 = await steersOf(office);
  const gDone = await idle(P, G);
  const lastTurn = await waitFor(async () => (await ffEvents(P))?.header?.[G]?.lastTurn ?? null, 10000, 200);
  outcome.grokQueue = { title: g2.title, queued: !!gQueued, steers: gSteers1 && gSteers0 ? gSteers1.steers - gSteers0.steers : null, drained: !!gDone, lastTurn };
  env.step('FF11：Grok 串流中再送一則 → 只排隊不插入（送出鈕「加入佇列」、沒有插入的嘗試）→ 這一輪完成後接著送；上一輪的用量照劇本',
    !!gRunning && g2.title === SEND_TITLE.queue && g2Res?.state === 'done' && !!gQueued && outcome.grokQueue.steers === 0 && !!gDone && lastTurn === '輸入 2,345・輸出 67 tokens',
    `串流${gRunning ? '中' : '沒開始'}；送出鈕「${g2.title ?? '—'}」、提示「${g2.placeholder ?? '—'}」；送出 ${g2Res?.state ?? '—'}、佇列${gQueued ? '一則' : '沒有'}；假會話的插入嘗試 +${outcome.grokQueue.steers ?? '—'}；`
      + `${gDone ? '這一輪完成後接著送、跑完、佇列清空' : '30 秒內沒有跑完'}；標頭的上一輪「${lastTurn ?? '—'}」`,
    { send: g2.frame, sessionBefore: gSteers0, sessionAfter: gSteers1 });

  await control(office, 'nextRemoteTurn', { turn: { steps: [{ text: segmentsText(40), segments: 40, delayMs: 250, stamp: true }], outcome: 'completed' } });
  const stopG = await stopMidStream(P, G, 'FF11 Grok 停下的一則');
  outcome.grokStop = stopG.outcome;
  env.step('FF11：Grok 串流中 stop', stopG.ok, stopG.summary, stopG.data);

  // 5. Antigravity：送出 → 串流（沒有卡片）→ 再送一則排隊 → 接著送完 → 串流中 stop
  await control(office, 'nextRemoteTurn', { turn: { steps: [
    { tool: '執行指令', kind: 'bash', title: 'Get-ChildItem', detail: 'Get-ChildItem', output: 'a.txt' },
    { text: segmentsText(16), segments: 16, delayMs: 250, stamp: true },
  ], outcome: 'completed', usage: { input: 24908, output: 113 } } });
  const a1 = await typeSend(P, A, 'FF11 agy 第一則');
  const a1Res = a1.frame ? await ffWaitResult(P, a1.frame.commandId, 15000) : null;
  await control(office, 'nextRemoteTurn', { turn: { steps: [{ text: segmentsText(2), segments: 2, delayMs: 50 }], outcome: 'completed' } });
  const aRunning = await streamed(P, A, 2);
  const aSteers0 = await steersOf(office);
  const a2 = await typeSend(P, A, 'FF11 agy 排隊的一則');
  const a2Res = a2.frame ? await ffWaitResult(P, a2.frame.commandId, 15000) : null;
  const aQueued = await waitFor(async () => ((await ffEvents(P))?.queue?.[A] === 1 ? true : null), 5000, 100);
  const aSteers1 = await steersOf(office);
  const aDone = await idle(P, A);
  const aKinds = (await ffItems(P, A)).map((i) => i.kind);
  const aCards = aKinds.filter((k) => ['Approval', 'Question', 'PlanApproval'].includes(k));
  outcome.agyQueue = { title: a2.title, queued: !!aQueued, steers: aSteers1 && aSteers0 ? aSteers1.steers - aSteers0.steers : null, drained: !!aDone, cards: aCards.length };
  env.step('FF11：Antigravity 送出 → 串流（沒有卡片）→ 再送一則只排隊不插入 → 接著送完',
    a1Res?.state === 'done' && !!aRunning && a2.title === SEND_TITLE.queue && a2Res?.state === 'done' && !!aQueued && outcome.agyQueue.steers === 0 && !!aDone && aCards.length === 0,
    `送出 ${a1Res?.state ?? '—'}；串流${aRunning ? '中' : '沒開始'}；送出鈕「${a2.title ?? '—'}」；送出 ${a2Res?.state ?? '—'}、佇列${aQueued ? '一則' : '沒有'}；假會話的插入嘗試 +${outcome.agyQueue.steers ?? '—'}；`
      + `${aDone ? '接著送完' : '30 秒內沒有跑完'}；項目 ${aKinds.join('、')}（卡片 ${aCards.length} 張）`,
    { sends: [a1.frame, a2.frame], kinds: aKinds });

  await control(office, 'nextRemoteTurn', { turn: { steps: [{ text: segmentsText(40), segments: 40, delayMs: 250, stamp: true }], outcome: 'completed' } });
  const stopA = await stopMidStream(P, A, 'FF11 agy 停下的一則');
  outcome.agyStop = stopA.outcome;
  env.step('FF11：Antigravity 串流中 stop', stopA.ok, stopA.summary, stopA.data);

  // 6. Claude 對照：串流中再送一則 → 送出鈕「插入這一輪」、中樞嘗試插入一次（假會話不插，照舊排隊、接著送）
  await control(office, 'nextRemoteTurn', { turn: { steps: [{ text: segmentsText(16), segments: 16, delayMs: 250, stamp: true }], outcome: 'completed' } });
  const c1 = await typeSend(P, C, 'FF11 Claude 第一則');
  const c1Res = c1.frame ? await ffWaitResult(P, c1.frame.commandId, 15000) : null;
  await control(office, 'nextRemoteTurn', { turn: { steps: [{ text: segmentsText(2), segments: 2, delayMs: 50 }], outcome: 'completed' } });
  const cRunning = await streamed(P, C, 2);
  const cSteers0 = await steersOf(office);
  const c2 = await typeSend(P, C, 'FF11 Claude 插入的一則');
  const c2Res = c2.frame ? await ffWaitResult(P, c2.frame.commandId, 15000) : null;
  const cSteered = await waitFor(async () => { const s = await steersOf(office); return s && cSteers0 && s.steers - cSteers0.steers >= 1 ? s : null; }, 10000, 200);
  const cDone = await idle(P, C);
  outcome.claudeSteer = { title: c2.title, steers: cSteered && cSteers0 ? cSteered.steers - cSteers0.steers : null, drained: !!cDone };
  env.step('FF11：Claude 對照：串流中再送一則 → 送出鈕「插入這一輪」、中樞嘗試插入一次（假會話不插，照舊排隊、接著送）',
    c1Res?.state === 'done' && !!cRunning && c2.title === SEND_TITLE.steer && c2Res?.state === 'done' && outcome.claudeSteer.steers === 1 && !!cDone,
    `送出 ${c1Res?.state ?? '—'}；串流${cRunning ? '中' : '沒開始'}；送出鈕「${c2.title ?? '—'}」、提示「${c2.placeholder ?? '—'}」；送出 ${c2Res?.state ?? '—'}；假會話的插入嘗試 +${outcome.claudeSteer.steers ?? '—'}；`
      + `${cDone ? '這一輪完成後接著送完' : '30 秒內沒有跑完'}`,
    { send: c2.frame });

  // 7. Antigravity 的遠端登出 → rejected／policy（agy 沒有登出指令）；refusedCli 不變
  const logout = logical ? await sendCmd(P, { cmd: 'accountLogout', accountId: logical.Antigravity, deviceId: office.deviceId }) : null;
  const refusedAfter = (await deviceStatus(office))?.refusedCli ?? null;
  outcome.agyLogout = logout ? `${logout.state}/${logout.code}` : null;
  env.step('FF11：Antigravity 的遠端登出 → rejected／policy（agy 沒有登出指令）', logout?.state === 'rejected' && logout.code === 'policy' && logout.message === AGY_LOGOUT
    && before.refused !== null && refusedAfter === before.refused,
  `accountLogout：${logout ? `${logout.state ?? '沒有結果'}${logout.code ? `／${logout.code}` : ''}「${logout.message ?? ''}」` : '沒有邏輯帳戶，沒送'}；refusedCli ${before.refused} → ${refusedAfter}`, { logout });

  // 8. 假會話每個動作只收到一次
  await sleep(500);
  const after = await steersOf(office);
  const delta = counterDelta(before.session, after);
  const want = { sends: 8, interrupts: 2, answers: 0, declines: 0, approvals: 1, plans: 1, steers: 1 };
  outcome.session = delta;
  env.step('FF11：假會話每個動作只收到一次（插入的嘗試只有 Claude 那一則）', Object.entries(want).every(([k, n]) => delta[k] === n),
    `增量 ${JSON.stringify(delta)}（預期 ${JSON.stringify(want)}：Grok、Antigravity 各三則〔第二則是佇列接著送〕、Claude 兩則〔第二則插入沒成、排隊接著送〕；核准、計畫各一次；停止兩次）`,
    { before: before.session, after, delta });

  // 9. 頁面乾淨
  outcome.page = await pageCleanStep(env, [P], 'FF11：頁面乾淨（主控台、回應、CSP、對話框、XSS 計數）');
  return outcome;
}

/** 送一則長串流、收到 3 段以上就 stop：{ ok, summary, data, outcome } */
async function stopMidStream(P, conv, text) {
  const before = (await stampRecords(P, [conv])).length;
  const s = await typeSend(P, conv, text);
  const sRes = s.frame ? await ffWaitResult(P, s.frame.commandId, 15000) : null;
  const going = await streamed(P, conv, before + 3);
  const stopId = going ? await postCmd(P, { cmd: 'stop', conversationId: conv }) : null;
  const stopRes = stopId ? await ffWaitResult(P, stopId, 15000) : null;
  const done = await idle(P, conv, 15000);
  await sleep(800);
  const got = (await stampRecords(P, [conv])).length - before;
  const ok = sRes?.state === 'done' && stopRes?.state === 'done' && !!done && got < 40;
  return {
    ok,
    summary: `送出 ${sRes?.state ?? '—'}；收到 ${going ? '3 段以上' : '不到 3 段'}之後 stop：${stopRes?.state ?? '—'}；這一輪${done ? '結束' : '沒結束'}；收到 ${got}／40 段`,
    data: { send: s.frame, stopId, stopRes },
    outcome: { stop: stopRes?.state ?? null, idle: !!done, partial: got < 40 },
  };
}
