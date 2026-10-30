// FF3 遠端操作一輪（WP4.9 步 15，計畫塊 12）：OFFICE 一個新對話，瀏覽器的分頁 P 照順序做——送出 → 帶 requestDigest 核准 → 回答含秘密題目的問卷 →
// 串流中 stop → 再送一則、執行中又送一則（排隊，這一輪完成後接著送）；之後第二個分頁 Q 按同一張（已處理的）卡、再以同一個 commandId 重送那個核准。
// 斷言：假會話每個動作只收到一次（status 的 session）；Q 與重送都得到 stale；秘密回答不在 hub-data 與伺服器記錄的位元組裡（UTF-8、UTF-16LE）；
// 每條連線每個 commandId 至多一則 commandResult（可能比 Cmd 的回傳先到：記下順序）；近期驗證之後讀稽核，commands.issued 的增量＝command.issued 筆數；
// 假裝置的 remoteAudit 每個指令一列（指令 id 的集合＝稽核的 target）。
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { appLayout, openApp, postCmd, waitFor } from '../lib/harness.mjs';
import { containsSecret, counterDelta, countBy, resultsPerCommand } from '../lib/flow-rules.mjs';
import {
  auditSince, ffEvents, ffItems, ffList, ffWaitResult, flowRecorder, opsStatus, rawClose, rawCmd, rawCompletion, rawConnect, rawMessages, rawWait, resultOrder,
  stampRecords, stepUp, trackHubFrames,
} from '../lib/flow-page.mjs';
import { control, deviceStatus, newConversation, pageCleanStep, sleep, waitSnapshots } from './common.mjs';

/** 預期的遠端指令：送出三則、核准、回答、停止、Q 的核准、重送的核准 */
const EXPECTED_COMMANDS = 8;

const convSel = (id) => '.conv[data-conv="' + id + '"]';
const taSel = (id) => convSel(id) + ' textarea.cinput';
const cardSel = (c, i) => convSel(c) + ' .it[data-id="' + i + '"]';

/** 「第 0001 段。」連起來（每段一樣長，切成 n 段時剛好一段一句） */
const segmentsText = (n) => Array.from({ length: n }, (_, i) => `第 ${String(i + 1).padStart(4, '0')} 段。`).join('');

/** startEnv 的選項：預設的兩台；頁面記錄器 */
export const env = {
  initOpts: { lightMessages: true },
  initScripts: [{ fn: flowRecorder }],
};

/** 在輸入框打字、按 Enter（真的鍵盤事件）；回這次送出的 Cmd 框（frames 裡第一個之後出現的 send） */
async function typeSend(app, conv, text) {
  await app.page.bringToFront();
  const from = app.frames.length;
  await app.page.fill(taSel(conv), text);
  await app.page.press(taSel(conv), 'Enter');
  return waitFor(() => app.frames.slice(from).find((f) => f.kind === 'cmd' && f.cmd === 'send' && f.conv === conv) ?? null, 5000, 50);
}

/** 等某種卡片在等待（記錄器的項目；有 requestDigest） */
const waitCard = (app, conv, kind, ms = 20000) =>
  waitFor(async () => (await ffItems(app, conv)).find((i) => i.kind === kind && i.status === 'Waiting' && typeof i.digest === 'string') ?? null, ms, 100);

/** 檔案（或資料夾底下的每個檔）裡有沒有這些字串（UTF-8 與 UTF-16LE）：{ scanned, hits: { 名稱: [相對路徑…] } } */
function scanFiles(roots, needles, base) {
  const files = [];
  const walk = (p) => {
    let st;
    try { st = fs.statSync(p); } catch { return; }
    if (st.isDirectory()) {
      for (const e of fs.readdirSync(p)) walk(path.join(p, e));
    } else if (st.isFile()) {
      files.push(p);
    }
  };
  for (const r of roots) walk(r);
  const hits = Object.fromEntries(Object.keys(needles).map((k) => [k, []]));
  for (const f of files) {
    let buf;
    try { buf = fs.readFileSync(f); } catch { continue; }
    for (const [k, s] of Object.entries(needles)) {
      const c = containsSecret(buf, s);
      if (c.utf8 || c.utf16) hits[k].push(`${path.relative(base, f)}（${[c.utf8 ? 'UTF-8' : null, c.utf16 ? 'UTF-16LE' : null].filter(Boolean).join('、')}）`);
    }
  }
  return { scanned: files.length, hits };
}

export async function run(env) {
  const office = env.devices.get('OFFICE');
  const outcome = {};
  const nonce = crypto.randomBytes(8).toString('hex');
  // 秘密回答與正向對照（送出的訊息，應該在 sync.db 裡）：執行時才組，不是任何權杖的樣式
  const secret = `秘密回答-${nonce}-甲乙丙`;
  const marker = `ff3m${nonce}`;

  // 1. 對話與三輪的劇本（T1：核准 → 問卷〔含秘密題目〕→ 40 段串流；T2：16 段串流；T3：4 段）
  const conv = await newConversation(office, 'ff3', 'FF3 遠端操作');
  await control(office, 'nextRemoteTurn', { turn: { steps: [
    { approval: '執行指令', kind: 'bash', title: 'npm run deploy' },
    { question: [{ id: 'target', text: '部署到哪裡？', options: ['測試站', '正式站'], allowOther: false }, { id: 'token', text: '部署密碼', secret: true }] },
    { text: segmentsText(40), segments: 40, delayMs: 250, stamp: true },
  ], outcome: 'completed' } });
  await control(office, 'nextRemoteTurn', { turn: { steps: [{ text: segmentsText(16), segments: 16, delayMs: 250, stamp: true }], outcome: 'completed' } });
  await control(office, 'nextRemoteTurn', { turn: { steps: [{ text: segmentsText(4), segments: 4, delayMs: 100 }], outcome: 'completed' } });

  // 2. 兩個分頁 P、Q（同一個瀏覽器內容、同一個工作階段）都開著這個對話；之前的計數
  const P = await openApp(env, 'P', { layout: appLayout([{ tabs: [conv] }]), beforeGoto: trackHubFrames });
  const Q = await openApp(env, 'Q', { layout: appLayout([{ tabs: [conv] }]), beforeGoto: trackHubFrames });
  const ready = P.live && Q.live && (await waitSnapshots(P, [conv])) && (await waitSnapshots(Q, [conv]));
  const before = {
    session: (await deviceStatus(office))?.session ?? null,
    commands: (await opsStatus(P)).json?.commands ?? null,
    audit: (await stepUp(P, env.password)).status === 200 ? (await auditSince(P, Number.MAX_SAFE_INTEGER)).top : null,
    remote: ((await control(office, 'remoteAudit', { limit: 1 })).rows ?? [])[0]?.seq ?? 0,
  };
  env.step('FF3：兩個分頁開著 OFFICE 的對話；之前的計數', !!ready && !!before.session && !!before.commands && before.audit !== null,
    `P ${P.live ? '連上' : '沒連上'}、Q ${Q.live ? '連上' : '沒連上'}、快照${ready ? '都收到' : '沒收到'}；假會話 ${JSON.stringify(before.session)}；commands.issued ${before.commands?.issued ?? '—'}；`
      + `稽核最新 seq ${before.audit ?? '—'}（近期驗證之後讀）；假裝置的遠端紀錄最新 seq ${before.remote}`,
    { conv, before });
  if (!ready) throw new Error('兩個分頁沒有開著對話');

  // 3. 送出（真的鍵盤）→ done
  const send1 = await typeSend(P, conv, `FF3 部署一次 ${marker}`);
  const send1Res = send1 ? await ffWaitResult(P, send1.commandId, 15000) : null;
  // 4. 核准卡：Q 也看到同一張（同一個 digest）；P 按「允許」（真的點擊），approve 帶的 requestDigest＝項目的
  const cardP = await waitCard(P, conv, 'Approval');
  const cardQ = cardP ? await waitFor(async () => (await ffItems(Q, conv)).find((i) => i.id === cardP.id && i.status === 'Waiting') ?? null, 10000, 100) : null;
  const f4 = P.frames.length;
  if (cardP) {
    await P.page.bringToFront();
    await P.page.click(cardSel(conv, cardP.id) + ' .opt[data-idx="1"]', { timeout: 10000 });
  }
  const approve = await waitFor(() => P.frames.slice(f4).find((f) => f.kind === 'cmd' && f.cmd === 'approve') ?? null, 5000, 50);
  const approveRes = approve ? await ffWaitResult(P, approve.commandId, 15000) : null;
  const approveOk = send1Res?.state === 'done' && !!cardP && !!cardQ && cardQ.digest === cardP.digest && !!approve && approve.digest === cardP.digest && approve.choice === 'allow' && approveRes?.state === 'done';
  outcome.sendApprove = { send: send1Res?.state ?? null, approve: approveRes?.state ?? null, digestEqual: !!approve && approve.digest === cardP?.digest, qSameCard: !!cardQ && cardQ.digest === cardP?.digest };
  env.step('FF3：送出 → 帶 requestDigest 核准', approveOk,
    `送出 ${send1 ? send1Res?.state ?? '沒有結果' : '沒送出'}；核准卡 ${cardP ? `${cardP.id}（digest ${cardP.digest.slice(0, 12)}…）` : '沒出現'}、Q ${cardQ ? (cardQ.digest === cardP.digest ? '看到同一張' : '的 digest 不同') : '沒看到'}；`
      + `按「允許」：approve ${approve ? `choice ${approve.choice}、requestDigest ${approve.digest === cardP?.digest ? '等於項目的' : '不同'}` : '沒送'}、結果 ${approveRes?.state ?? '—'}`,
    { send: send1, sendResult: send1Res, card: cardP, cardQ, approve, approveResult: approveRes });

  // 5. 問卷（含秘密題目）：answer 帶 requestDigest → done；畫面上沒有秘密回答
  const question = await waitCard(P, conv, 'Question');
  const answerId = question ? await postCmd(P, { cmd: 'answer', conversationId: conv, itemId: question.id, answers: { target: ['測試站'], token: [secret] }, requestDigest: question.digest }) : null;
  const answerRes = answerId ? await ffWaitResult(P, answerId, 15000) : null;
  const answered = question ? await waitFor(async () => ((await ffItems(P, conv)).find((i) => i.id === question.id)?.status !== 'Waiting' ? true : null), 10000, 100) : null;
  const shown = await P.page.evaluate((s) => document.body.innerText.includes(s), secret).catch(() => null);
  outcome.answer = { result: answerRes?.state ?? null, cardDone: !!answered, secretShown: shown };
  env.step('FF3：回答含秘密題目的問卷', answerRes?.state === 'done' && !!answered && shown === false,
    `問卷 ${question ? question.id : '沒出現'}；answer ${answerRes?.state ?? '—'}、卡片${answered ? '不再等待' : '還在等'}；畫面上${shown === false ? '沒有' : shown ? '有' : '讀不到'}秘密回答`,
    { question, answerId, answerResult: answerRes });

  // 6. 串流中 stop：收到 3 段以上就停 → done、這一輪結束、40 段沒跑完
  const streaming = await waitFor(async () => ((await stampRecords(P, [conv])).length >= 3 ? true : null), 20000, 100);
  const stopId = streaming ? await postCmd(P, { cmd: 'stop', conversationId: conv }) : null;
  const stopRes = stopId ? await ffWaitResult(P, stopId, 15000) : null;
  const idle = await waitFor(async () => ((await ffList(P)) ?? []).find((c) => c.id === conv && c.running === false) ?? null, 15000, 200);
  await sleep(1000);
  const t1Stamps = (await stampRecords(P, [conv])).length;
  outcome.stop = { result: stopRes?.state ?? null, idle: !!idle, partial: t1Stamps < 40 };
  env.step('FF3：串流中 stop', stopRes?.state === 'done' && !!idle && t1Stamps < 40,
    `收到 ${streaming ? '3 段以上' : '不到 3 段'}之後 stop：${stopRes?.state ?? '—'}；這一輪${idle ? '結束' : '沒結束'}；收到 ${t1Stamps}／40 段`,
    { stopId, stopResult: stopRes });

  // 7. 再送一則（T2，16 段）；執行中又送一則（T3）→ 排進佇列（composer.queue 一則），T2 完成後接著送、T3 完成
  const send2 = await typeSend(P, conv, 'FF3 第二則');
  const send2Res = send2 ? await ffWaitResult(P, send2.commandId, 15000) : null;
  const t2Running = await waitFor(async () => ((await stampRecords(P, [conv])).length >= t1Stamps + 2 ? true : null), 15000, 100);
  const send3 = await typeSend(P, conv, 'FF3 排隊的第三則');
  const send3Res = send3 ? await ffWaitResult(P, send3.commandId, 15000) : null;
  const queued = await waitFor(async () => ((await ffEvents(P))?.queue?.[conv] === 1 ? true : null), 5000, 100);
  const sessionDone = await waitFor(async () => {
    const s = (await deviceStatus(office, 10000))?.session;
    const l = ((await ffList(P)) ?? []).find((c) => c.id === conv);
    return s && before.session && s.sends - before.session.sends >= 3 && l && l.running === false && (await ffEvents(P))?.queue?.[conv] === 0 ? s : null;
  }, 30000, 300);
  outcome.queue = { send2: send2Res?.state ?? null, send3: send3Res?.state ?? null, queued: !!queued, drained: !!sessionDone };
  env.step('FF3：執行中再送一則（排隊，這一輪完成後接著送）', send2Res?.state === 'done' && !!t2Running && send3Res?.state === 'done' && !!queued && !!sessionDone,
    `第二則 ${send2Res?.state ?? '—'}（${t2Running ? '執行中' : '沒開始'}）；第三則 ${send3Res?.state ?? '—'}、輸入區的佇列${queued ? '有一則' : '沒有'}；${sessionDone ? '第二輪完成後接著送、第三輪也完成、佇列清空' : '30 秒內沒有跑完'}`,
    { send2, send3, send2Res, send3Res });

  // 8. Q 按同一張（已處理的）卡：照 Q 看到的 digest 送 approve → stale
  const qId = cardQ ? await postCmd(Q, { cmd: 'approve', conversationId: conv, itemId: cardQ.id, choice: 'allow', requestDigest: cardQ.digest }) : null;
  const qRes = qId ? await ffWaitResult(Q, qId, 15000) : null;
  // 9. 同一個 commandId 重送那個核准（Q 的頁面另開一條連線；伺服器不對 bcid 去重，WP4.5 D21：成為新的指令、到電腦被 stale 擋下）
  const raw = await rawConnect(Q);
  const resend = raw.ok && approve ? await rawCmd(Q, { cmd: 'approve', conversationId: conv, itemId: cardP.id, choice: 'allow', requestDigest: cardP.digest }, approve.commandId) : null;
  const resendRes = resend ? await rawWait(Q, { type: 'commandResult', commandId: approve.commandId }, { ms: 15000 }) : null;
  const resendDone = resend ? await rawCompletion(Q, resend.invocationId, 5000) : null;
  await sleep(1500);
  const rawResults = await rawMessages(Q, 'commandResult');
  await rawClose(Q);
  outcome.stale = { q: qRes ? `${qRes.state}/${qRes.code}` : null, resend: resendRes ? `${resendRes.m.state}/${resendRes.m.code}` : null };
  env.step('FF3：第二個分頁按同一張卡、同一個 commandId 重送 → stale', qRes?.state === 'rejected' && qRes.code === 'stale' && resendRes?.m.state === 'rejected' && resendRes.m.code === 'stale',
    `Q：${qRes ? `${qRes.state}／${qRes.code}「${qRes.message ?? ''}」` : '沒有結果'}；重送（commandId ${approve?.commandId ?? '—'}）：Cmd 的回傳 ${resendDone ? JSON.stringify(resendDone.result ?? resendDone.error) : '沒有'}、`
      + `${resendRes ? `${resendRes.m.state}／${resendRes.m.code}「${resendRes.m.message ?? ''}」` : '沒有結果'}`,
    { q: { id: qId, result: qRes }, resend: { commandId: approve?.commandId ?? null, completion: resendDone, result: resendRes?.m ?? null } });

  // 10. 假會話每個動作只收到一次
  const after = (await deviceStatus(office))?.session ?? null;
  const delta = counterDelta(before.session, after);
  const wantDelta = { sends: 3, interrupts: 1, answers: 1, declines: 0, approvals: 1, plans: 0 };
  const sessionOk = Object.entries(wantDelta).every(([k, n]) => delta[k] === n);
  outcome.session = delta;
  env.step('FF3：假會話每個動作只收到一次', sessionOk,
    `增量 ${JSON.stringify(delta)}（預期 ${JSON.stringify(wantDelta)}：送出三輪〔第三輪是佇列接著送〕、核准一次、回答一次、停止一次；Q 與重送停在中樞的 requestDigest 檢查）`,
    { before: before.session, after, delta });

  // 11. 每條連線每個 commandId 至多一則 commandResult；記下 commandResult 與 Cmd 回傳的先後
  const evP = await ffEvents(P);
  const evQ = await ffEvents(Q);
  const perP = resultsPerCommand(evP?.results ?? []);
  const perQ = resultsPerCommand(evQ?.results ?? []);
  const perRaw = resultsPerCommand(rawResults);
  const mine = [send1, approve, send2, send3].filter(Boolean).map((f) => ({ cmd: f.cmd, order: resultOrder(P, f.commandId, f.sock) }));
  const atMostOne = perP.max <= 1 && perQ.max <= 1 && perRaw.max <= 1;
  outcome.results = { atMostOne, P: perP.max, Q: perQ.max, raw: perRaw.max };
  env.step('FF3：每條連線每個 commandId 至多一則 commandResult', atMostOne,
    `P 每個 commandId 最多 ${perP.max} 則（${Object.keys(perP.per).length} 個）、Q 最多 ${perQ.max} 則、重送的連線最多 ${perRaw.max} 則；順序（同一條 WebSocket）：${mine.map((m) => `${m.cmd} ${m.order}`).join('、')}`,
    { P: perP, Q: perQ, raw: perRaw, order: mine });

  // 12. 近期驗證之後讀稽核：commands.issued 的增量＝command.issued 筆數；假裝置的 remoteAudit 每個指令一列
  await sleep(1500);
  const step = await stepUp(P, env.password);
  const audit = step.status === 200 ? await auditSince(P, before.audit) : { status: null, entries: [] };
  const issuedEntries = audit.entries.filter((e) => e.action === 'command.issued');
  const commandsAfter = (await opsStatus(P)).json?.commands ?? null;
  const cmdDelta = counterDelta(before.commands, commandsAfter);
  const remote = await control(office, 'remoteAudit', { limit: 100 });
  const remoteRows = (remote.rows ?? []).filter((r) => r.seq > before.remote);
  const auditIds = new Set(issuedEntries.map((e) => e.target));
  const remoteIds = new Set(remoteRows.map((r) => r.commandId));
  const sameIds = auditIds.size === remoteIds.size && [...auditIds].every((id) => remoteIds.has(id));
  const auditOk = step.status === 200 && audit.status === 200 && cmdDelta.issued === issuedEntries.length && cmdDelta.rejectedAtServer === 0 && issuedEntries.length === EXPECTED_COMMANDS
    && remoteRows.length === issuedEntries.length && remoteIds.size === remoteRows.length && sameIds;
  outcome.audit = { stepUp: step.status, issued: cmdDelta.issued ?? null, entries: issuedEntries.length, remoteRows: remoteRows.length, sameIds };
  env.step('FF3：commands.issued 的增量＝稽核的 command.issued 筆數；假裝置的遠端紀錄每個指令一列', auditOk,
    `stepup ${step.status}；稽核 ${audit.status ?? '—'}：command.issued ${issuedEntries.length} 筆（結果 ${JSON.stringify(countBy(issuedEntries, 'outcome'))}）；ops/status commands：issued +${cmdDelta.issued ?? '—'}、`
      + `rejectedAtServer +${cmdDelta.rejectedAtServer ?? '—'}、done +${cmdDelta.done ?? '—'}、rejected +${cmdDelta.rejected ?? '—'}（預期 ${EXPECTED_COMMANDS} 個指令）；`
      + `假裝置的遠端紀錄 ${remoteRows.length} 列（${remoteIds.size} 個不同的指令 id，${sameIds ? '和稽核的 target 相同' : '和稽核的 target 不同'}；${JSON.stringify(countBy(remoteRows, 'cmd'))}）`,
    { stepUp: step.status, entries: issuedEntries, commandsDelta: cmdDelta, remoteRows: remoteRows.map((r) => ({ seq: r.seq, commandId: r.commandId, cmd: r.cmd, state: r.state, code: r.code })) });

  // 13. 秘密回答不在 hub-data 與伺服器的主控台記錄裡（UTF-8、UTF-16LE）；送出的訊息（正向對照）找得到
  const roots = [env.hub.dataDir, env.hub.logFile].filter(Boolean);
  const scan = scanFiles(roots, { secret, marker }, env.work);
  const scanOk = scan.hits.secret.length === 0 && scan.hits.marker.length > 0;
  outcome.secretScan = { secretFound: scan.hits.secret.length > 0, markerFound: scan.hits.marker.length > 0 };
  env.step('FF3：秘密回答不在 hub-data 與伺服器記錄的位元組裡', scanOk,
    `掃 ${scan.scanned} 個檔（hub-data 底下全部，含 db、-wal、-shm；伺服器的主控台記錄）：秘密回答${scan.hits.secret.length ? `出現在 ${scan.hits.secret.join('、')}` : '都沒有'}；`
      + `正向對照（送出的訊息）${scan.hits.marker.length ? `在 ${scan.hits.marker.slice(0, 3).join('、')}` : '找不到（掃描沒有讀到內容）'}`,
    { scanned: scan.scanned, secretHits: scan.hits.secret, markerHits: scan.hits.marker });

  // 14. 頁面乾淨
  outcome.page = await pageCleanStep(env, [P, Q], 'FF3：頁面乾淨（主控台、回應、CSP、對話框、XSS 計數）');
  return outcome;
}
