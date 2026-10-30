// FF2 多裝置即時與 S7（WP4.9 步 15，計畫塊 12）：OFFICE 3 個、HOME 2 個對話各每秒 4 段（一個執行中的項目，即時那一條），合計 1,200 段；
// HOME 另一個對話每秒一段完成的項目（持久那一條）。兩個瀏覽器內容各一個頁面（同一個內容裡只有前景的分頁跑 requestAnimationFrame，網頁以它合併畫面的更新）：
// A 的四個窗格看 OFFICE 三個與 HOME 一個、B 的兩個窗格看 HOME 另一個與持久那個。
// 斷言：即時與持久分開算 p50／p95／p99，p95 ≤500 ms、p99 ≤1 秒；liveDropped 的增量只有預期的原因（final、stale）；
// 之後 HOME 串流一個沒有人看得到的對話（只在一條沒送 ClientState 的原始連線上 open）：HOME 的 liveRelay 一則都沒送（不在 WatchChanged 裡），持久的版本照樣到。
import { addBrowserContext, appLayout, openApp, waitFor } from '../lib/harness.mjs';
import { countBy, counterDelta, unexpectedDrops } from '../lib/flow-rules.mjs';
import { flowRecorder, opsStatus, rawClose, rawConnect, rawCmd, rawMark, rawMessages, rawWait, recorderSummary, stampRecords } from '../lib/flow-page.mjs';
import { parseStamps } from '../lib/stats-rules.mjs';
import { control, deviceStatus, newConversation, pageCleanStep, rafAlive, s7Of, s7Text, sleep, waitSnapshots, waitStreamsDone } from './common.mjs';

const LIVE = { segments: 240, delayMs: 250 };
const DONE = { segments: 60, delayMs: 1000 };
const HIDDEN = { segments: 20, delayMs: 250 };

/** startEnv 的選項：預設的兩台（OFFICE 3 個、HOME 2 個種子）；頁面記錄器 */
export const env = {
  initOpts: { lightMessages: true },
  initScripts: [{ fn: flowRecorder }],
};

export async function run(env) {
  const office = env.devices.get('OFFICE');
  const home = env.devices.get('HOME');
  const outcome = {};

  // 1. 開對話（各一段固定回覆）：即時 o1～o3（OFFICE）、h1、h2（HOME）；持久 p1、沒人看的 hidden（HOME）
  const ids = {};
  for (const k of ['o1', 'o2', 'o3']) ids[k] = await newConversation(office, `ff2-${k}`, `FF2 ${k}`);
  for (const k of ['h1', 'h2', 'p1', 'hidden']) ids[k] = await newConversation(home, `ff2-${k}`, `FF2 ${k}`);
  const liveKeys = ['o1', 'o2', 'o3', 'h1', 'h2'];

  // 2. 兩個瀏覽器內容：A 看 o1、o2、o3、h1，B 看 h2、p1
  const A = await openApp(env, 'A', { layout: appLayout([{ tabs: [ids.o1] }, { tabs: [ids.o2] }, { tabs: [ids.o3] }, { tabs: [ids.h1] }]) });
  const ctxB = await addBrowserContext(env, 'B');
  const B = ctxB.login.ok ? await openApp(env, 'B', { context: ctxB.context, layout: appLayout([{ tabs: [ids.h2] }, { tabs: [ids.p1] }]) }) : { live: false, label: 'B' };
  const snapA = A.live ? await waitSnapshots(A, [ids.o1, ids.o2, ids.o3, ids.h1]) : false;
  const snapB = B.live ? await waitSnapshots(B, [ids.h2, ids.p1]) : false;
  const raf = A.live && B.live ? await rafAlive([A, B]) : { ok: false, counts: {} };
  outcome.watching = { A: A.live && snapA, B: B.live && snapB, raf: raf.ok };
  env.step('FF2：兩個瀏覽器內容各看幾個對話（A：OFFICE 3＋HOME 1；B：HOME 1＋持久）', A.live && B.live && snapA && snapB,
    `A ${A.live ? '連上' : '沒連上'}、快照${snapA ? '都收到' : '沒收到'}；B ${B.live ? '連上' : '沒連上'}（另一個工作階段${ctxB.login.ok ? '登入' : '登入失敗'}）、快照${snapB ? '都收到' : '沒收到'}；`
      + `requestAnimationFrame ${raf.ok ? '兩個頁面都在跑' : '有停住的'}（${JSON.stringify(raf.counts)}）`,
    { ids, raf });
  if (!(A.live && B.live && snapA && snapB)) throw new Error('瀏覽器沒有看著這些對話');

  // 3. 串流：五個即時（各 240 段、每秒 4 段）與一個持久（60 段、每秒一段完成的項目）
  const opsBefore = (await opsStatus(A)).json;
  const relayBefore = { OFFICE: (await deviceStatus(office))?.liveRelay ?? null, HOME: (await deviceStatus(home))?.liveRelay ?? null };
  for (const k of ['o1', 'o2', 'o3']) await control(office, 'stream', { conversation: `ff2-${k}`, message: `FF2 ${k} 串流`, segments: LIVE.segments, delayMs: LIVE.delayMs, stamp: true });
  for (const k of ['h1', 'h2']) await control(home, 'stream', { conversation: `ff2-${k}`, message: `FF2 ${k} 串流`, segments: LIVE.segments, delayMs: LIVE.delayMs, stamp: true });
  await control(home, 'stream', { conversation: 'ff2-p1', message: 'FF2 每秒一段完成的項目', segments: DONE.segments, delayMs: DONE.delayMs, stamp: true, completeEachSegment: true });
  const doneO = await waitStreamsDone(office, ['ff2-o1', 'ff2-o2', 'ff2-o3'], 180000);
  const doneH = await waitStreamsDone(home, ['ff2-h1', 'ff2-h2', 'ff2-p1'], 180000);
  await sleep(3000);
  const recsA = await stampRecords(A, [ids.o1, ids.o2, ids.o3, ids.h1]);
  const recsB = await stampRecords(B, [ids.h2, ids.p1]);
  const recs = [...recsA, ...recsB];
  const perConv = countBy(recs, 'c');
  const expected = Object.fromEntries([...liveKeys.map((k) => [ids[k], LIVE.segments]), [ids.p1, DONE.segments]]);
  const allSeen = Object.entries(expected).every(([c, n]) => (perConv[c] ?? 0) === n);
  const outcomes = { ...(doneO ?? {}), ...(doneH ?? {}) };
  const streamsOk = !!doneO && !!doneH && Object.values(outcomes).every((s) => s.outcome === 'Completed');
  outcome.streams = { completed: streamsOk, produced: { live: liveKeys.length * LIVE.segments, persistent: DONE.segments }, allStampsSeen: allSeen };
  env.step('FF2：六條串流跑完、每個標記都收到（即時 1,200 段＋持久 60 段）', streamsOk && allSeen,
    Object.entries(ids).filter(([k]) => k !== 'hidden').map(([k, c]) => `${k} ${outcomes[`ff2-${k}`]?.outcome ?? '沒結束'}（${perConv[c] ?? 0}／${expected[c]}）`).join('、'),
    { outcomes, perConv, recorderA: await recorderSummary(A), recorderB: await recorderSummary(B) });

  // 4. S7：即時（五個對話，第一次出現）與持久（p1，第一次出現在完成的項目裡）分開算
  const live = s7Of(recs, { path: 'live', conversations: liveKeys.map((k) => ids[k]), domOk: raf.ok });
  const done = s7Of(recs, { path: 'persistent', conversations: [ids.p1], domOk: raf.ok });
  const perDevice = {
    OFFICE: s7Of(recs, { path: 'live', conversations: [ids.o1, ids.o2, ids.o3], domOk: raf.ok }).summary,
    HOME: s7Of(recs, { path: 'live', conversations: [ids.h1, ids.h2], domOk: raf.ok }).summary,
  };
  outcome.s7 = { source: live.source, live: live.ok, persistent: done.ok };
  env.measure?.('FF2', { live, persistent: done, perDevice });
  env.step('FF2：即時與持久的 S7（p95 ≤500 ms、p99 ≤1 秒）', live.ok && done.ok,
    `${s7Text('即時', live)}；${s7Text('持久', done)}；即時分台 p95：OFFICE ${perDevice.OFFICE.p95 ?? '—'} ms、HOME ${perDevice.HOME.p95 ?? '—'} ms`,
    { live, persistent: done, perDevice });

  // 5. liveDropped 只有預期的原因
  const opsAfter = (await opsStatus(A)).json;
  const dropDelta = counterDelta(opsBefore?.conversations?.liveDropped, opsAfter?.conversations?.liveDropped);
  const bad = unexpectedDrops(dropDelta);
  const relayAfter = { OFFICE: (await deviceStatus(office))?.liveRelay ?? null, HOME: (await deviceStatus(home))?.liveRelay ?? null };
  const relayDelta = { OFFICE: counterDelta(relayBefore.OFFICE, relayAfter.OFFICE), HOME: counterDelta(relayBefore.HOME, relayAfter.HOME) };
  outcome.liveDropped = { unexpected: bad.map((b) => b.reason), read: !!opsBefore && !!opsAfter };
  env.step('FF2：liveDropped 的增量只有預期的原因（final、stale）', !!opsBefore && !!opsAfter && bad.length === 0,
    `liveDropped 增量 ${JSON.stringify(dropDelta)}${bad.length ? `；非預期：${bad.map((b) => `${b.reason} ${b.count}`).join('、')}` : ''}；`
      + `假裝置的即時更新：OFFICE 送出 ${relayDelta.OFFICE.sent ?? '—'} 則（遮蔽 ${relayDelta.OFFICE.masked ?? '—'}、太大 ${relayDelta.OFFICE.tooLarge ?? '—'}）、HOME 送出 ${relayDelta.HOME.sent ?? '—'} 則（遮蔽 ${relayDelta.HOME.masked ?? '—'}）`,
    { dropDelta, relayDelta });

  // 6. 沒人看得到的對話：只在原始連線上 open（沒送 ClientState，不算看得到）；HOME 一則即時更新都不送、持久的版本照樣到
  const raw = await rawConnect(A);
  const mark = await rawMark(A);
  if (raw.ok) await rawCmd(A, { cmd: 'open', conversationId: ids.hidden });
  const opened = raw.ok ? await rawWait(A, { type: 'conversation', conversationId: ids.hidden }, { from: mark, ms: 15000 }) : null;
  const hb = (await deviceStatus(home))?.liveRelay ?? null;
  const hiddenRun = await control(home, 'stream', { conversation: 'ff2-hidden', message: 'FF2 沒人看得到的串流', segments: HIDDEN.segments, delayMs: HIDDEN.delayMs, stamp: true, wait: true }, 120000);
  const arrived = await waitFor(async () => {
    const items = (await rawMessages(A, 'item')).filter((m) => m.conversationId === ids.hidden && m.item?.status === 'Done' && m.item?.kind === 'Assistant');
    const stamps = new Set(items.flatMap((m) => parseStamps(m.item.html ?? '')));
    return stamps.size >= HIDDEN.segments ? { stamps: stamps.size, items: items.length } : null;
  }, 15000, 300);
  await sleep(1000);
  const ha = (await deviceStatus(home))?.liveRelay ?? null;
  const hd = counterDelta(hb, ha);
  const runningSeen = (await rawMessages(A, 'item')).filter((m) => m.conversationId === ids.hidden && m.item?.status === 'Running').length;
  await rawClose(A);
  // 陽性對照（WP4.9 合併前審查）：同一個計數器在有人看時（第 5 步，h1、h2 有 A、B 在看）要大於 0，否則 hd.sent === 0 證明不了「沒人看就不送」
  const watchedSent = { OFFICE: relayDelta.OFFICE.sent ?? null, HOME: relayDelta.HOME.sent ?? null };
  const counterAlive = watchedSent.HOME > 0 && watchedSent.OFFICE > 0;
  outcome.hidden = { opened: !!opened, completed: hiddenRun?.outcome === 'Completed', liveSent: hd.sent ?? null, persistentArrived: !!arrived, counterAlive };
  env.step('FF2：沒人看得到的對話收不到即時更新（持久的照樣到）', !!opened && hiddenRun?.outcome === 'Completed' && counterAlive && hd.sent === 0 && !!arrived,
    `陽性對照：有人看時的即時更新 OFFICE ${watchedSent.OFFICE ?? '—'} 則、HOME ${watchedSent.HOME ?? '—'} 則（計數器${counterAlive ? '有在數' : '沒在數：這一步不能判'}）；`
      + `原始連線 open ${opened ? '收到快照' : '沒有快照'}（沒送 ClientState）；HOME 串流 ${HIDDEN.segments} 段 ${hiddenRun?.outcome ?? '—'}：即時更新送出 ${hd.sent ?? '—'} 則、遮蔽 ${hd.masked ?? '—'} 則；`
      + `持久的版本${arrived ? `到了（完成的項目帶 ${arrived.stamps} 個標記）` : '沒到'}；原始連線收到執行中的項目 ${runningSeen} 則（持久的檢查點）`,
    { relayBefore: hb, relayAfter: ha, delta: hd, watchedSent, arrived, runningSeen });

  // 7. 頁面乾淨
  outcome.page = await pageCleanStep(env, [A, B], 'FF2：頁面乾淨（主控台、回應、CSP、對話框、XSS 計數）');
  return outcome;
}
