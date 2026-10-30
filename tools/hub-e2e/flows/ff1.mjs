// FF1 配對與回補（S19；WP4.9 步 15，計畫塊 12）：
// HOME 種 30 個、OFFICE 種 300 個（100 個最近的、200 個超過 90 天的），依序配對（HOME 先、OFFICE 後，OFFICE 不等 outbox 清空：控制通道一開就交給情境，
// 回補在情境裡進行）。OFFICE 回補期間，瀏覽器看著 OFFICE 兩個新開的對話串流：即時那一條（執行中的項目，每秒 4 段）與持久那一條（每段一個完成的項目，每秒 2 段）。
// 斷言：回補期間兩條的 S7 p95 ≤500 ms、p99 ≤1 秒（以標記的時間落在回補期間的才算）；回補結束後 ops/status 的 conversations.count＝種子數＋新對話；
// 抽 20 個對話，瀏覽器 open／loadOlder 拿到的項目 id 與 Ord（seq）集合等於假裝置的 ords。
import { appLayout, openApp, waitFor } from '../lib/harness.mjs';
import { backfillWindow, compareIdOrd, countBy, pickEvenly } from '../lib/flow-rules.mjs';
import { ffList, flowRecorder, opsStatus, rawClose, rawConnect, rawReadAll, recorderSummary, stampRecords } from '../lib/flow-page.mjs';
import { control, deviceStatus, newConversation, pageCleanStep, rafAlive, s7Of, s7Text, sleep, waitSnapshots, waitStreamsDone } from './common.mjs';

const HOME_SEED = 30;
const OFFICE_RECENT = 100;
const OFFICE_OLD = 200;
const ITEMS = 12;
const LIVE = { segments: 200, delayMs: 250 };
const DONE = { segments: 100, delayMs: 500 };
const SAMPLE = { OFFICE: 15, HOME: 3 };

/** startEnv 的選項：HOME 先配對；OFFICE 的兩段種子、不等 outbox 清空；頁面記錄器（probeInit 只記型別） */
export const env = {
  devices: [
    { name: 'HOME', seed: { count: HOME_SEED, seed: 20261531, itemsPerConversation: ITEMS } },
    {
      name: 'OFFICE',
      seed: [
        { count: OFFICE_RECENT, seed: 20261532, olderThanDays: 1, itemsPerConversation: ITEMS },
        { count: OFFICE_OLD, seed: 20261533, olderThanDays: 91, itemsPerConversation: ITEMS },
      ],
      drain: false,
    },
  ],
  initOpts: { lightMessages: true },
  initScripts: [{ fn: flowRecorder }],
};

/** 回補的取樣（每 500 ms 讀一次 OFFICE 的 status）：start() 之後背景跑，stop() 回時間線 */
function backfillMonitor(dev) {
  const timeline = [];
  let running = true;
  const loop = (async () => {
    while (running) {
      const s = await deviceStatus(dev, 10000);
      const b = s?.backfill ?? null;
      timeline.push({ t: Date.now(), left: b?.progress?.conversationsLeft ?? null, bytesLeft: b?.progress?.bytesLeft ?? null, pause: b?.pause?.reason ?? null,
        outbox: s?.outbox ?? null });
      await sleep(500);
    }
  })();
  return {
    timeline,
    /** 最近一次的取樣 */
    last: () => timeline.at(-1) ?? null,
    async stop() {
      running = false;
      await loop;
      return timeline;
    },
  };
}

/** outbox 清空：除了 Acked、Rejected 以外的狀態都是 0（同 FakeDevice 的 WaitDrainedAsync；status 的 outbox 是 { 狀態: 列數 }） */
const outboxEmpty = (o) => !!o && Object.entries(o).filter(([k]) => k !== 'Acked' && k !== 'Rejected').reduce((n, [, v]) => n + (Number(v) || 0), 0) === 0;

export async function run(env) {
  const office = env.devices.get('OFFICE');
  const home = env.devices.get('HOME');
  const outcome = {};
  const monitor = backfillMonitor(office);
  try {
    // 1. OFFICE 正在回補（控制通道一開就開始取樣）
    const seen = await waitFor(() => (monitor.timeline.some((p) => p.left > 0) ? true : null), 30000, 200);
    outcome.backfillSeen = !!seen;
    env.step('FF1：OFFICE 配對後開始回補', !!seen,
      seen ? `回補中：還有 ${monitor.timeline.find((p) => p.left > 0).left} 個對話（${office.cfg.seed.count} 個種子，${OFFICE_OLD} 個超過 90 天）` : '30 秒內沒有看到回補（conversationsLeft）',
      { first: monitor.timeline.slice(0, 5) });
    if (!seen) throw new Error('沒有看到回補');

    // 2. 兩個新對話（OFFICE），瀏覽器以兩個窗格看著
    const liveId = await newConversation(office, 'ff1-live', 'FF1 即時');
    const doneId = await newConversation(office, 'ff1-done', 'FF1 持久');
    const P = await openApp(env, 'P', { layout: appLayout([{ tabs: [liveId] }, { tabs: [doneId] }]) });
    const snaps = P.live ? await waitSnapshots(P, [liveId, doneId], 20000) : false;
    const raf = await rafAlive([P]);
    outcome.watching = P.live && snaps;
    env.step('FF1：回補中開兩個新對話，瀏覽器的兩個窗格看著', P.live && snaps,
      `/app/ ${P.live ? '連上' : '沒有連上'}；即時 ${liveId}、持久 ${doneId} 的快照${snaps ? '都收到' : '沒有收到'}；requestAnimationFrame ${raf.ok ? '在跑' : '停住'}（${JSON.stringify(raf.counts)}）；`
        + `回補還有 ${monitor.last()?.left ?? '—'} 個`,
      { liveId, doneId, raf });
    if (!outcome.watching) throw new Error('瀏覽器沒有看著兩個新對話');

    // 3. 回補期間的串流：即時（每秒 4 段，一個執行中的項目）與持久（每秒 2 段，每段一個完成的項目）
    const leftAtStart = monitor.last()?.left ?? null;
    await control(office, 'stream', { conversation: 'ff1-live', message: 'FF1 回補中的即時串流', segments: LIVE.segments, delayMs: LIVE.delayMs, stamp: true });
    await control(office, 'stream', { conversation: 'ff1-done', message: 'FF1 回補中的持久串流', segments: DONE.segments, delayMs: DONE.delayMs, stamp: true, completeEachSegment: true });
    const streams = await waitStreamsDone(office, ['ff1-live', 'ff1-done'], 180000);
    await sleep(3000);
    const recs = await stampRecords(P, [liveId, doneId]);
    const perConv = countBy(recs, 'c');
    const allSeen = (perConv[liveId] ?? 0) === LIVE.segments && (perConv[doneId] ?? 0) === DONE.segments;
    outcome.streams = { live: streams?.['ff1-live']?.outcome ?? null, done: streams?.['ff1-done']?.outcome ?? null, allStampsSeen: allSeen };
    env.step('FF1：兩條串流跑完、每個標記都收到', !!streams && streams['ff1-live'].outcome === 'Completed' && streams['ff1-done'].outcome === 'Completed' && allSeen,
      `即時 ${streams?.['ff1-live']?.outcome ?? '沒結束'}（標記 ${perConv[liveId] ?? 0}／${LIVE.segments}）、持久 ${streams?.['ff1-done']?.outcome ?? '沒結束'}（標記 ${perConv[doneId] ?? 0}／${DONE.segments}）；`
        + `串流開始時回補還有 ${leftAtStart ?? '—'} 個`,
      { streams, perConv, recorder: await recorderSummary(P) });

    // 4. 回補結束、outbox 清空
    const finished = await waitFor(() => {
      const t = monitor.timeline;
      const after = t.findIndex((p) => p.left > 0);
      return after >= 0 && t.slice(after).some((p) => p.left === null) && outboxEmpty(monitor.last()?.outbox) ? true : null;
    }, 300000, 500);
    const timeline = await monitor.stop();
    const window = backfillWindow(timeline);
    const pauses = countBy(timeline.filter((p) => p.pause), 'pause');
    outcome.backfillDone = !!finished;
    env.step('FF1：OFFICE 回補完成、outbox 清空', !!finished,
      `${window ? `回補期間約 ${(((window.to ?? Date.now()) - window.from) / 1000).toFixed(1)} 秒` : '沒有回補期間'}（取樣 ${timeline.length} 次、暫停的取樣 ${JSON.stringify(pauses)}）；outbox ${JSON.stringify(timeline.at(-1)?.outbox ?? null)}`,
      { window, pauses, samples: timeline.length, head: timeline.slice(0, 3), tail: timeline.slice(-3),
        // 整條時間線（[時間, 還剩幾個對話, 還剩幾個位元組]）：拿來對照 S7 最慢的那幾個標記
        timeline: timeline.map((p) => [p.t, p.left, p.bytesLeft]) });

    // 5. S7（只算標記落在回補期間的）：即時＝第一次出現（執行中），持久＝第一次出現在完成的項目裡
    const domOk = raf.ok;
    const live = s7Of(recs, { path: 'live', conversations: [liveId], window, domOk });
    const done = s7Of(recs, { path: 'persistent', conversations: [doneId], window, domOk });
    const liveAll = s7Of(recs, { path: 'live', conversations: [liveId], domOk });
    const doneAll = s7Of(recs, { path: 'persistent', conversations: [doneId], domOk });
    const ack = (await deviceStatus(office))?.batchAck ?? null;
    outcome.s7 = { source: live.source, live: live.ok, persistent: done.ok };
    env.measure?.('FF1', { window, live, persistent: done, liveAll, persistentAll: doneAll, batchAck: ack });
    env.step('FF1：回補期間的 S7（p95 ≤500 ms、p99 ≤1 秒）', live.ok && done.ok,
      `${s7Text('即時', live)}；${s7Text('持久', done)}。整段串流（不限回補期間）：即時 p95 ${liveAll.summary.p95 ?? '—'} ms、持久 p95 ${doneAll.summary.p95 ?? '—'} ms；`
        + `OFFICE 的 BatchAck ${ack ? `${ack.count} 次、p50 ${ack.p50Ms} ms、p95 ${ack.p95Ms} ms、最大 ${ack.maxMs} ms` : '—'}`,
      { window, live, persistent: done, liveAll, persistentAll: doneAll, batchAck: ack });

    // 6. conversations.count＝種子數＋新對話
    const expectCount = HOME_SEED + OFFICE_RECENT + OFFICE_OLD + 2;
    const counted = await waitFor(async () => {
      const s = await opsStatus(P);
      return s.json?.conversations?.count === expectCount ? s.json.conversations : null;
    }, 60000, 1000);
    const lastOps = counted ?? (await opsStatus(P)).json?.conversations ?? null;
    outcome.count = { expected: expectCount, ok: !!counted };
    env.step('FF1：ops/status 的 conversations.count＝種子數＋新對話', !!counted,
      `count ${lastOps?.count ?? '—'}／${expectCount}（HOME ${HOME_SEED}＋OFFICE ${OFFICE_RECENT + OFFICE_OLD}＋新對話 2）、items ${lastOps?.items ?? '—'}`,
      { conversations: lastOps ? { count: lastOps.count, items: lastOps.items, deleted: lastOps.deleted, devices: lastOps.devices } : null });

    // 7. 抽 20 個對話：瀏覽器 open／loadOlder 的 id 與 Ord＝假裝置的 ords
    const list = (await ffList(P)) ?? [];
    const byDevice = (dev) => list.filter((c) => c.deviceId === dev.deviceId && c.id !== liveId && c.id !== doneId)
      .sort((a, b) => (String(b.updatedAt) < String(a.updatedAt) ? -1 : String(b.updatedAt) > String(a.updatedAt) ? 1 : (a.id < b.id ? -1 : 1)));
    const sample = [
      ...pickEvenly(byDevice(office), SAMPLE.OFFICE).map((c) => ({ id: c.id, dev: office })),
      ...pickEvenly(byDevice(home), SAMPLE.HOME).map((c) => ({ id: c.id, dev: home })),
      { id: liveId, dev: office },
      { id: doneId, dev: office },
    ];
    // 假裝置的 Ord：中樞記憶體裡的（載入過、配過的），沒有就用 sync.db 的 published_items 記的（回補的對話中樞沒載入，
    // Ord 由引擎重整時規劃、記在 published_items）；兩個都有時要相同
    const raw = await rawConnect(P);
    const rows = [];
    for (const s of sample) {
      const b = raw.ok ? await rawReadAll(P, s.id) : { ok: false, items: [], pages: 0, error: raw.error };
      const d = await s.dev.run.control('ords', { conversation: s.id }, 30000);
      const devItems = d.ok ? d.result.items : [];
      const conflicts = devItems.filter((i) => i.ord !== null && i.publishedOrd !== null && i.ord !== i.publishedOrd).length;
      const cmp = compareIdOrd(b.items, devItems.map((i) => ({ id: i.id, ord: i.ord ?? i.publishedOrd })));
      rows.push({ id: s.id, dev: s.dev.name, pages: b.pages, browserOk: b.ok, error: b.error ?? (d.ok ? null : d.error), conflicts,
        hubOrds: devItems.filter((i) => i.ord !== null).length, ...cmp });
    }
    await rawClose(P);
    const equal = rows.filter((r) => r.equal && r.browserOk && r.conflicts === 0).length;
    const paged = rows.filter((r) => r.pages > 1).length;
    outcome.sample = { count: rows.length, equal, loadOlderUsed: paged > 0, list: list.length };
    env.step('FF1：抽 20 個對話，open／loadOlder 的項目 id 與 Ord 等於假裝置的 ords', rows.length === 20 && equal === 20 && paged > 0,
      `清單 ${list.length} 個；抽 ${rows.length} 個（OFFICE ${rows.filter((r) => r.dev === 'OFFICE').length}、HOME ${rows.filter((r) => r.dev === 'HOME').length}，含兩個新對話）：`
        + `相同 ${equal} 個、用到 loadOlder ${paged} 個（共 ${rows.reduce((n, r) => n + r.browser, 0)} 個項目；假裝置的 Ord 來自中樞記憶體的 ${rows.reduce((n, r) => n + r.hubOrds, 0)} 個、`
        + `其餘來自 published_items）`
        + `${rows.some((r) => !r.equal || r.conflicts) ? `；不同：${rows.filter((r) => !r.equal || r.conflicts).map((r) => `${r.id.slice(0, 8)}（瀏覽器 ${r.browser}、裝置 ${r.device}、只在瀏覽器 ${r.onlyBrowser.length}、只在裝置 ${r.onlyDevice.length}、Ord 不同 ${r.ordMismatch.length}、沒有 Ord ${r.withoutOrd}、中樞與 published_items 不同 ${r.conflicts}${r.error ? `、${r.error}` : ''}）`).join('、')}` : ''}`,
      { rows });

    // 8. 頁面乾淨
    outcome.page = await pageCleanStep(env, [P], 'FF1：頁面乾淨（主控台、回應、CSP、對話框、XSS 計數）');
    return outcome;
  } finally {
    await monitor.stop().catch(() => {});
  }
}
