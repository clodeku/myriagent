// 排程頁的純規則（Web/schedulerules.js）：node --test schedulerules.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  entriesOf, ledOf, filterEntries, sortEntries, originCounts, summaryOf, badgeCount, historyBars, timelineOf, timelineTicks,
  lastText, primaryTask, provenanceText, providerKeyOf, pickSelection, quickActions, listActions, isManaged, DEFAULT_ORIGINS,
  hourTicks, isSoon, soonestPoint, soonText, runStamp, freshRuns,
} from '../../src/Myriagent/Web/schedulerules.js';

const NOW = new Date(2026, 9, 26, 10, 0, 0).getTime();
const H = 3600e3;
const e = (o) => Object.assign({ path: '\\X', name: 'X', state: 'ready', enabled: true, origin: 'agent', last: null, missed: 0, runs: [], upcoming: [] }, o);

test('entriesOf：不是陣列、缺 path 的略過', () => {
  assert.deepEqual(entriesOf(null), []);
  assert.deepEqual(entriesOf({ entries: 'x' }), []);
  assert.equal(entriesOf({ entries: [e({}), { name: 'no path' }, null] }).length, 1);
});

test('ledOf：執行中、停用、失敗、警告與錯過、成功、沒有結果、讀不到', () => {
  assert.equal(ledOf(e({ state: 'running', last: { level: 'fail' } })), 'run');
  assert.equal(ledOf(e({ enabled: false, last: { level: 'fail' } })), 'dis');
  assert.equal(ledOf(e({ last: { level: 'fail' } })), 'fail');
  assert.equal(ledOf(e({ last: { level: 'warn' } })), 'warn');
  assert.equal(ledOf(e({ last: { level: 'ok' }, missed: 2 })), 'warn');
  assert.equal(ledOf(e({ last: { level: 'ok' } })), 'ok');
  assert.equal(ledOf(e({})), 'info');
  assert.equal(ledOf(e({ state: 'error' })), 'warn');
});

test('filterEntries：預設只列代理建立、納入管理、疑似；狀態篩選', () => {
  const list = [e({ path: '\\A', origin: 'agent', last: { level: 'fail' } }), e({ path: '\\B', origin: 'suspected', state: 'running' }),
    e({ path: '\\C', origin: 'other' }), e({ path: '\\D', origin: 'ignored', enabled: false }), e({ path: '\\E', origin: 'adopted' })];
  assert.deepEqual(filterEntries(list, null).map((x) => x.path), ['\\A', '\\B', '\\E']);
  assert.deepEqual(filterEntries(list, { origins: ['other', 'ignored'] }).map((x) => x.path), ['\\C', '\\D']);
  assert.deepEqual(filterEntries(list, { origins: DEFAULT_ORIGINS, state: 'fail' }).map((x) => x.path), ['\\A']);
  assert.deepEqual(filterEntries(list, { origins: DEFAULT_ORIGINS, state: 'running' }).map((x) => x.path), ['\\B']);
  assert.deepEqual(filterEntries(list, { origins: ['ignored'], state: 'disabled' }).map((x) => x.path), ['\\D']);
});

test('sortEntries：失敗、執行中、警告、成功、停用；同一類照名稱', () => {
  const list = [e({ name: 'b', last: { level: 'ok' } }), e({ name: 'dis', enabled: false }), e({ name: 'run', state: 'running' }),
    e({ name: 'a', last: { level: 'ok' } }), e({ name: 'fail', last: { level: 'fail' } }), e({ name: 'warn', missed: 1 })];
  assert.deepEqual(sortEntries(list).map((x) => x.name), ['fail', 'run', 'warn', 'a', 'b', 'dis']);
});

test('originCounts、summaryOf、badgeCount', () => {
  const list = [e({ origin: 'agent', last: { level: 'fail' }, upcoming: [NOW + 10 * 60e3, NOW + 2 * H] }), e({ origin: 'suspected', state: 'running', missed: 1 }),
    e({ origin: 'other', last: { level: 'fail' }, upcoming: [NOW + 60e3] })];
  assert.deepEqual(originCounts(list), { agent: 1, adopted: 0, suspected: 1, timed: 0, other: 1, ignored: 0 }, '定時訊息的數量由頁面另外補');
  assert.deepEqual(summaryOf(list, NOW), { count: 2, running: 1, failing: 1, missed: 1, nextHour: 1 });
  assert.equal(badgeCount({ failing: 3, entries: [] }), 3);
  assert.equal(badgeCount({ entries: list }), 1, '沒有 failing 時自己算：只算列入管理的');
  assert.equal(badgeCount(null), 0);
  assert.equal(isManaged(e({ origin: 'adopted' })), true);
  assert.equal(isManaged(e({ origin: 'suspected' })), false);
});

test('historyBars：最近 n 次、舊的在左', () => {
  const runs = [1, 2, 3, 4].map((i) => ({ at: NOW - i * H, level: i === 1 ? 'fail' : 'ok', text: 't' + i, gap: i === 3 }));
  const bars = historyBars(e({ runs }), 3);
  assert.deepEqual(bars.map((b) => b.text), ['t3', 't2', 't1']);
  assert.equal(bars[0].gap, true);
  assert.equal(bars[2].level, 'fail');
  assert.deepEqual(historyBars(e({ runs: null })), []);
});

test('timelineOf：24 小時內的點；太多次改成帶狀', () => {
  const many = Array.from({ length: 96 }, (_, i) => NOW + (i + 1) * 15 * 60e3);
  const pts = timelineOf([e({ path: '\\A', upcoming: [NOW - H, NOW + 6 * H, NOW + 30 * H] }), e({ path: '\\B', upcoming: many })], NOW, 24 * H);
  const a = pts.filter((p) => p.path === '\\A');
  assert.equal(a.length, 1);
  assert.equal(a[0].x, 0.25);
  const b = pts.filter((p) => p.path === '\\B');
  assert.equal(b.length, 1);
  assert.equal(b[0].band, true);
  assert.equal(b[0].count, 95, '最後一次剛好在 24 小時的邊界上，不算');
});

test('timelineTicks：從現在起對齊 4 的倍數整點', () => {
  const ticks = timelineTicks(new Date(2026, 9, 26, 10, 20, 0).getTime(), 24 * H, 4);
  assert.deepEqual(ticks.map((t) => t.label).slice(0, 3), ['12:00', '16:00', '20:00']);
  assert.ok(ticks.every((t) => t.x >= 0 && t.x < 1));
});

test('lastText、primaryTask、provenanceText、providerKeyOf', () => {
  assert.equal(lastText(e({ state: 'running' })), '執行中');
  assert.equal(lastText(e({ last: { level: 'fail', text: '結束碼 1' } })), '結束碼 1');
  assert.equal(lastText(e({})), '還沒執行過');
  assert.equal(primaryTask(e({ last: { level: 'fail' } })), 'diagnose');
  assert.equal(primaryTask(e({ last: { level: 'ok' } })), 'explain');
  assert.equal(provenanceText(e({ provenance: { source: 'myriagent', title: '筆記庫同步' } })), '筆記庫同步');
  assert.equal(provenanceText(e({ provenance: { source: 'transcript', title: null } })), '外部 CLI 的工作階段');
  assert.equal(provenanceText(e({ origin: 'suspected' })), '疑似（腳本在工作資料夾）');
  assert.equal(provenanceText(e({ origin: 'other' })), '系統或廠商');
  assert.equal(providerKeyOf('Codex'), 'Codex');
  assert.equal(providerKeyOf('Unknown'), null);
});

test('pickSelection：留著還在的，否則第一個失敗的、再來第一個', () => {
  const list = [e({ path: '\\A' }), e({ path: '\\B', last: { level: 'fail' } })];
  assert.equal(pickSelection(list, '\\A'), '\\A');
  assert.equal(pickSelection(list, '\\gone'), '\\B');
  assert.equal(pickSelection([e({ path: '\\A' })], null), '\\A');
  assert.equal(pickSelection([], null), null);
});

test('quickActions 與 listActions', () => {
  assert.deepEqual(quickActions(e({ state: 'running' })).map((a) => a.action), ['stop', 'disable']);
  assert.deepEqual(quickActions(e({ enabled: false, state: 'disabled' })).map((a) => a.action), ['run', 'enable']);
  assert.deepEqual(quickActions(e({ state: 'error' })), []);
  assert.deepEqual(listActions(e({ origin: 'suspected' })).map((a) => a.action), ['ignore', 'adopt']);
  assert.deepEqual(listActions(e({ origin: 'adopted' })).map((a) => a.action), ['ignore', 'unadopt']);
  assert.deepEqual(listActions(e({ origin: 'ignored' })).map((a) => a.action), ['unignore']);
  assert.deepEqual(listActions(e({ origin: 'agent' })).map((a) => a.action), ['ignore'], '有證據的不能取消納入');
});

test('定時訊息：形狀、狀態、時間寫法的預填、可以送進去的對話', async () => {
  const { timedOf, timedAsEntry, timedStateText, whenOf, localConversations } = await import('../../src/Myriagent/Web/schedulerules.js');
  assert.deepEqual(timedOf({ timed: [{ id: 'tm_1' }, { label: 'x' }, null] }).map((t) => t.id), ['tm_1']);
  const e = timedAsEntry({ id: 'tm_1', label: '日報', enabled: true, done: false, last: { level: 'fail' }, upcoming: [1], runs: [] });
  assert.equal(e.path, 'tm_1');
  assert.equal(ledOf(e), 'fail');
  assert.equal(ledOf(timedAsEntry({ id: 'tm_2', enabled: false })), 'dis');
  assert.equal(ledOf(timedAsEntry({ id: 'tm_3', enabled: true, done: true })), 'dis', '做完的算停用');
  assert.equal(timedStateText({ done: true }), '已完成');
  assert.equal(timedStateText({ enabled: false }), '已暫停');
  assert.equal(timedStateText({ enabled: true }), '');
  assert.deepEqual(whenOf({ cron: '0 9 * * *' }), { kind: 'daily', value: '09:00' });
  assert.deepEqual(whenOf({ cron: '30 18 * * 1-5' }), { kind: 'weekdays', value: '18:30' });
  assert.deepEqual(whenOf({ cron: '*/15 * * * *' }), { kind: 'every', value: '15m' });
  assert.deepEqual(whenOf({ cron: '0 */2 * * *' }), { kind: 'every', value: '2h' });
  assert.deepEqual(whenOf({ cron: '0 9 1 * *' }), { kind: 'cron', value: '0 9 1 * *' });
  assert.deepEqual(whenOf({ cron: null, at: new Date(2026, 9, 27, 14, 5).getTime() }), { kind: 'at', value: '2026-10-27 14:05' });
  assert.deepEqual(whenOf(null), { kind: 'daily', value: '09:00' });
  assert.deepEqual(localConversations([{ id: 'a', updatedAt: 1 }, { id: 'b', updatedAt: 3 }, { id: 'r', deviceId: 'd_x', updatedAt: 9 }]).map((c) => c.id), ['b', 'a']);
});

test('第九批：每小時刻度、即將執行、新的執行結果', () => {
  const ticks = hourTicks(NOW + 20 * 60e3, 24 * H);
  assert.equal(ticks.length, 24, '整點對齊：10:20 起 24 小時有 24 個整點');
  assert.ok(Math.abs(ticks[0].x - (40 / 1440)) < 1e-9, '第一個是 11:00');
  assert.equal(ticks.filter((t) => t.major).length, 4, '0、6、12、18 點是粗刻度');
  assert.equal(hourTicks(NOW, 24 * H)[0].x, 0, '剛好整點時從 0 開始');

  assert.ok(isSoon(NOW + 59 * 60e3, NOW));
  assert.ok(!isSoon(NOW + 61 * 60e3, NOW));
  assert.ok(!isSoon(NOW - 1, NOW), '過去的不算');
  assert.ok(!isSoon(undefined, NOW));
  const pts = [{ path: 'a', band: false, at: NOW + 40 * 60e3 }, { path: 'b', band: false, at: NOW + 12 * 60e3 }, { path: 'c', band: true, at: NOW + 60e3 }, { path: 'd', band: false, at: NOW + 2 * H }];
  assert.equal(soonestPoint(pts, NOW).path, 'b', '一小時內最近的（帶狀的不算）');
  assert.equal(soonestPoint([{ path: 'd', band: false, at: NOW + 2 * H }], NOW), null);
  assert.equal(soonText(NOW + 12 * 60e3 + 30e3, NOW), '12 分後');
  assert.equal(soonText(NOW + 20e3, NOW), '即將執行');

  const a1 = e({ path: 'a', runs: [{ at: NOW - H, level: 'ok' }], last: { level: 'ok' } });
  const b1 = e({ path: 'b', lastRun: NOW - 2 * H, last: { level: 'ok' } });
  assert.equal(runStamp(a1), (NOW - H) + ':ok');
  assert.equal(runStamp(b1), (NOW - 2 * H) + ':ok', '沒有 runs 時用 lastRun');
  assert.equal(runStamp(e({})), null);
  const first = freshRuns(null, [{ key: 'a', e: a1 }, { key: 'b', e: b1 }]);
  assert.deepEqual(first.fresh, {}, '第一次只建基準');
  const a2 = e({ path: 'a', runs: [{ at: NOW, level: 'fail' }, { at: NOW - H, level: 'ok' }], last: { level: 'fail' } });
  const c2 = e({ path: 'c', runs: [{ at: NOW, level: 'ok' }], last: { level: 'ok' } });
  const second = freshRuns(first.seen, [{ key: 'a', e: a2 }, { key: 'b', e: b1 }, { key: 'c', e: c2 }]);
  assert.deepEqual(second.fresh, { a: 'fail' }, 'a 有新的一次（失敗）；b 沒變；c 剛出現不算');
  assert.equal(second.seen.c, NOW + ':ok');
});

test('跨電腦的定時訊息（2026-11-13）：那台的對話、timedSave 的形狀、沒改文字就保留原文', async () => {
  const { deviceConversations, timedSaveCommand } = await import('../../src/Myriagent/Web/schedulerules.js');
  const convs = [
    { id: 'a', updatedAt: 1 }, { id: 'b', updatedAt: 3 }, { id: 'd', isDraft: true, updatedAt: 5 },
    { id: 'r1', deviceId: 'd_x', updatedAt: 2 }, { id: 'r2', deviceId: 'd_x', updatedAt: 8 }, { id: 'r3', deviceId: 'd_y', updatedAt: 9 },
    { id: 'q', deviceId: 'd_x', ephemeral: true, updatedAt: 10 },
  ];
  assert.deepEqual(deviceConversations(convs, null).map((c) => c.id), ['b', 'a'], '這台：草稿不列');
  assert.deepEqual(deviceConversations(convs, 'd_x').map((c) => c.id), ['r2', 'r1'], '那台的對話，臨時對話不列');

  const base = { conversationId: 'b', newConversation: false, kind: 'daily', value: '09:00', label: '報表', text: '整理報表' };
  assert.deepEqual(timedSaveCommand(Object.assign({ dev: null, id: null }, base)),
    { cmd: 'timedSave', id: null, conversationId: 'b', newConversation: false, when: { kind: 'daily', value: '09:00' }, label: '報表', text: '整理報表' });
  assert.deepEqual(timedSaveCommand(Object.assign({ dev: 'd_x', id: 'tm_1' }, base)),
    { cmd: 'timedSave', deviceId: 'd_x', id: 'tm_1', conversation: 'b', newConversation: false, whenKind: 'daily', whenValue: '09:00', label: '報表', text: '整理報表' });
  const partial = { text: '整理報表…', textPartial: true };
  assert.equal('text' in timedSaveCommand(Object.assign({ dev: 'd_x', id: 'tm_1', original: partial }, base, { text: '整理報表…' })), false, '摘要不是原文、沒改：不帶，那台保留原文');
  assert.equal(timedSaveCommand(Object.assign({ dev: 'd_x', id: 'tm_1', original: partial }, base, { text: '改過了' })).text, '改過了');
  assert.equal(timedSaveCommand(Object.assign({ dev: 'd_x', id: 'tm_1', original: { text: '整理報表', textPartial: false } }, base)).text, '整理報表', '摘要就是原文：照樣帶');
  assert.equal(timedSaveCommand(Object.assign({ dev: 'd_x', id: null }, base, { label: '' })).label, null);
});
