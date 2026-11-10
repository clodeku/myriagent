// Claude 對話的選項與操作的純規則（src/Myriagent/Web/claudeoptrules.js）：主代理與輸出風格的選單、目標晶片、
// 背景工作的輸出、插入訊息的取消、回溯。規則模組沒有 import，以 data: URL 匯入（同 mcprules.test.mjs）。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const webDir = path.resolve(here, '..', '..', 'src', 'Myriagent', 'Web');
const source = readFileSync(path.join(webDir, 'claudeoptrules.js'), 'utf8');
const R = await import('data:text/javascript;base64,' + Buffer.from(source + '\n// claudeoptrules.test\n', 'utf8').toString('base64'));

const local = { isHub: false, isReplica: false };

test('規則模組沒有 import、沒有 HTML 寫入點', () => {
  assert.doesNotMatch(source, /^\s*import\s/m);
  assert.doesNotMatch(source, /innerHTML|outerHTML|insertAdjacentHTML|document\.write/);
});

test('輸出風格：照設定在最前面，內建的有中文名稱，目前的值不在清單裡另列', () => {
  const hd = { provider: 'Claude', outputStyle: null, outputStyles: ['default', 'Explanatory', 'my-style'] };
  const items = R.styleMenu(hd);
  assert.deepEqual(items.map((i) => i.value), [null, 'default', 'Explanatory', 'my-style']);
  assert.equal(items[0].checked, true);
  assert.equal(items[2].label, '解說');
  assert.equal(items[2].hint, 'Explanatory');
  assert.equal(items[3].label, 'my-style');
  assert.equal(items[3].hint, null);

  const gone = R.styleMenu({ provider: 'Claude', outputStyle: 'Pirate', outputStyles: ['default'] });
  assert.deepEqual(gone.map((i) => i.value), [null, 'default', 'Pirate']);
  assert.equal(gone[2].checked, true);
  assert.equal(R.styleLabel(null), '照設定');
  assert.equal(R.styleLabel('Learning'), '學習');
});

test('輸出風格與主代理鈕：只有本機的對話；風格只有 Claude', () => {
  const hd = { provider: 'Claude', outputStyles: ['default'], agents: [{ value: 'default' }, { value: 'x' }], agent: 'default' };
  assert.equal(R.showStyle(hd, local), true);
  assert.equal(R.showAgent(hd, local), true);
  assert.equal(R.showStyle(hd, { isHub: true, isReplica: false }), false);
  assert.equal(R.showStyle(hd, { isHub: false, isReplica: true }), false);
  assert.equal(R.showStyle({ ...hd, isDraft: true, deviceId: 'd1' }, local), false, '要在其他電腦開的草稿');
  assert.equal(R.showStyle({ provider: 'Claude', outputStyles: [] }, local), false, '還沒回報過清單');
  assert.equal(R.showStyle({ provider: 'Claude', outputStyles: [], outputStyle: 'Concise' }, local), true, '已經選了一個');
  assert.equal(R.showStyle({ ...hd, provider: 'Codex' }, local), false);
  assert.equal(R.showAgent({ provider: 'Claude', agents: [{ value: 'default' }], agent: 'default' }, local), false, '只有預設');
  assert.equal(R.showAgent({ provider: 'Grok', agents: hd.agents }, local), false);
});

test('主代理：Antigravity 建立對話後就鎖住，Claude 隨時可以換', () => {
  assert.equal(R.agentLocked({ provider: 'Antigravity', sessionStarted: true }), true);
  assert.equal(R.agentLocked({ provider: 'Antigravity', running: true }), true);
  assert.equal(R.agentLocked({ provider: 'Antigravity' }), false);
  assert.equal(R.agentLocked({ provider: 'Claude', sessionStarted: true, running: true }), false);
  assert.match(R.defaultAgentDesc('Claude'), /Claude Code/);
});

test('目標晶片：沒有條件不顯示，晶片上截短，執行中不能清除', () => {
  assert.equal(R.goalView(null, false), null);
  assert.equal(R.goalView({ condition: '  ' }, false), null);
  const v = R.goalView({ condition: '工作資料夾裡有 z1.txt 到 z9.txt 九個檔案，每建立一個之前先執行 ping' }, false);
  assert.equal(v.label, '目標：工作資料夾裡有 z1.txt 到 z9.txt …');
  assert.equal(v.canClear, true);
  assert.match(v.title, /ping/);
  const running = R.goalView({ condition: '短的' }, true);
  assert.equal(running.label, '目標：短的');
  assert.equal(running.canClear, false);
  assert.match(running.clearDesc, /先按停止/);
});

test('插入的那則：輸入區上方的文字', () => {
  assert.equal(R.steeredLabel({ id: 'i1', text: '順便跑測試' }), '已插入這一輪：順便跑測試');
  assert.equal(R.steeredLabel({ id: 'i1', text: '' }), '已插入這一輪：（附件）');
  assert.match(R.steeredLabel({ id: 'i1', text: 'x'.repeat(80) }), /…$/);
});

test('回溯：只有本機的 Claude 對話、不在執行中；預覽的說明', () => {
  assert.equal(R.canRewind({ provider: 'Claude' }, local), true);
  assert.equal(R.canRewind({ provider: 'Claude', running: true }, local), false);
  assert.equal(R.canRewind({ provider: 'Claude', isDraft: true }, local), false);
  assert.equal(R.canRewind({ provider: 'Codex' }, local), false);
  assert.equal(R.canRewind({ provider: 'Claude' }, { isHub: true, isReplica: false }), false);

  assert.deepEqual(R.rewindPlan({ error: '這一輪還在執行：先按停止再回溯' }), { error: '這一輪還在執行：先按停止再回溯' });
  const p = R.rewindPlan({ conversation: true, laterMessages: 3, files: ['a.txt', 'src\\b.cs'], fileCount: 2, insertions: 4, deletions: 1, filesError: null });
  assert.equal(p.canConversation, true);
  assert.match(p.conversationNote, /之後的 2 則訊息/);
  assert.equal(p.canFiles, true);
  assert.equal(p.filesNote, '還原 2 個檔案（+4 −1 行）');
  assert.deepEqual(p.fileList, ['a.txt', 'src\\b.cs']);
  const none = R.rewindPlan({ conversation: false, conversationError: '找不到分支點', laterMessages: 1, files: [], fileCount: 0, filesError: null });
  assert.equal(none.canConversation, false);
  assert.equal(none.conversationNote, '找不到分支點');
  assert.match(none.filesNote, /沒有以編輯工具改過檔案/);
  const err = R.rewindPlan({ conversation: true, laterMessages: 1, filesError: '這則訊息沒有檔案還原點' });
  assert.equal(err.canFiles, false);
  assert.equal(err.filesNote, '這則訊息沒有檔案還原點');
  assert.match(err.conversationNote, /^這則從對話移除/);
});

test('Claude 的代理與風格：預設時沒有常駐的晶片，從模型選單底部進入', () => {
  const hd = { provider: 'Claude', agent: 'default', agents: [{ value: 'default', label: '預設' }, { value: 'Explore', label: 'Explore' }], outputStyle: null, outputStyles: ['default', 'Concise'] };
  assert.equal(R.agentChipShown(hd, local), false);
  assert.equal(R.styleChipShown(hd, local), false);
  assert.deepEqual(R.claudeMenuEntries(hd, local), [{ kind: 'agent', label: '主代理：預設' }, { kind: 'style', label: '輸出風格：照設定' }]);
  const picked = { ...hd, agent: 'Explore', outputStyle: 'Concise' };
  assert.equal(R.agentChipShown(picked, local), true);
  assert.equal(R.styleChipShown(picked, local), true);
  assert.deepEqual(R.claudeMenuEntries(picked, local).map((e) => e.label), ['主代理：Explore', '輸出風格：精簡']);
  // Antigravity 照舊：有得選就顯示晶片，模型選單不加
  const agy = { provider: 'Antigravity', agent: 'default', agents: hd.agents };
  assert.equal(R.agentChipShown(agy, local), true);
  assert.deepEqual(R.claudeMenuEntries(agy, local), []);
  assert.deepEqual(R.claudeMenuEntries(hd, { isHub: true, isReplica: false }), []);
});

// ───── 2026-10-26：子代理用量、閒置後送出的快取提醒、等額度重置 ─────

test('agentUsageView_tokens與工具次數_結束後不寫最近的工具', () => {
  const running = R.agentUsageView({ tokens: 15500, tools: 2, ms: 3200, last: 'Grep', done: false });
  assert.equal(running.text, '15.5K tokens');
  assert.equal(running.done, false);
  assert.match(running.title, /到目前用了 15,500 tokens、呼叫工具 2 次、已經跑了 3 秒；最近的工具：Grep/);
  const done = R.agentUsageView({ tokens: 2_000_000, tools: 9, ms: 125000, last: 'Grep', done: true });
  assert.equal(done.text, '2M tokens');
  assert.match(done.title, /用了 2,000,000 tokens、呼叫工具 9 次、跑了 2 分 5 秒$/);
  assert.equal(R.agentUsageView({ tokens: 0 }), null);
  assert.equal(R.agentUsageView(null), null);
  assert.equal(R.agentUsageView({ tokens: '5' }), null);
});

const NOW = Date.UTC(2026, 9, 26, 4, 0, 0);
const HOUR = 3600000;
const hd = (over) => ({ provider: 'Claude', running: false, isDraft: false, idleSince: NOW - 2 * HOUR, context: { used: 150000 }, ...over });

test('cacheCountdown（第九批）：Claude 閒置中才有；剩下的比例、最後 10 分鐘琥珀、過了 1 小時過期', () => {
  const fresh = R.cacheCountdown(hd({ idleSince: NOW - 20 * 60000 }), NOW);
  assert.equal(fresh.state, 'fresh');
  assert.ok(Math.abs(fresh.frac - 40 / 60) < 1e-9);
  assert.equal(fresh.mins, 40);
  assert.match(fresh.title, /^提示快取還有約 40 分鐘（Claude 保留 1 小時，已閒置 20 分鐘）/);
  const low = R.cacheCountdown(hd({ idleSince: NOW - 55 * 60000 - 30000 }), NOW);
  assert.equal(low.state, 'low');
  assert.equal(low.mins, 5, '4.5 分鐘無條件進位');
  const expired = R.cacheCountdown(hd({ idleSince: NOW - 2 * HOUR }), NOW);
  assert.deepEqual([expired.state, expired.frac, expired.mins], ['expired', 0, 0]);
  assert.match(expired.title, /已經過期（閒置 2 小時）/);
  assert.equal(R.cacheCountdown(hd({ running: true }), NOW), null, '執行中不顯示');
  assert.equal(R.cacheCountdown(hd({ isDraft: true }), NOW), null);
  assert.equal(R.cacheCountdown(hd({ provider: 'Codex' }), NOW), null);
  assert.equal(R.cacheCountdown(hd({ idleSince: undefined }), NOW), null);
  assert.equal(R.cacheCountdown(null, NOW), null);
  assert.equal(R.CACHE_LOW_MS, 10 * 60000);
});

test('quotaWaitCells（第九批）：剩下的比例換成 12 格、無條件進位、時間到 0 格', () => {
  assert.equal(R.quotaWaitCells(NOW + HOUR, NOW, HOUR), 12, '剛開始等');
  assert.equal(R.quotaWaitCells(NOW + HOUR / 2, NOW, HOUR), 6);
  assert.equal(R.quotaWaitCells(NOW + 60000, NOW, HOUR), 1, '還沒到就至少一格');
  assert.equal(R.quotaWaitCells(NOW, NOW, HOUR), 0);
  assert.equal(R.quotaWaitCells(NOW - 1, NOW, HOUR), 0);
  assert.equal(R.quotaWaitCells(NOW + 2 * HOUR, NOW, HOUR), 12, '不超過總格數');
  assert.equal(R.quotaWaitCells(NaN, NOW, HOUR), 0);
  assert.equal(R.quotaWaitCells(NOW + HOUR, NOW, 0), 0);
  assert.equal(R.quotaWaitCells(NOW + HOUR, NOW, HOUR, 5), 5);
});

// 2026-11-11：Claude 回報的重送成本（Header.coldResume；perMillion 40＝每百萬 tokens 用掉 5 小時額度的 40%，150K 是 6%）
const costly = (over) => hd({ coldResume: { perMillion: 40, minPercent: 5 }, ...over });

test('cacheResumeWarning_閒置超過一小時而且重送成本到Claude給的門檻才提醒', () => {
  const w = R.cacheResumeWarning(costly(), NOW, {});
  assert.equal(w.title, '快取已經過期');
  assert.match(w.message, /閒置 2 小時/);
  assert.match(w.message, /約 150K tokens/);
  assert.match(w.message, /約用掉 5 小時額度的 6%（Claude 估的）/);
  assert.ok(Math.abs(w.percent - 6) < 1e-9);
  assert.equal(R.cacheResumeWarning(costly({ idleSince: NOW - HOUR + 1 }), NOW, {}), null, '還在 1 小時內');
  assert.ok(R.cacheResumeWarning(costly({ idleSince: NOW - HOUR }), NOW, {}), '剛好 1 小時');
  assert.equal(R.cacheResumeWarning(costly({ context: { used: 124999 } }), NOW, {}), null, '不到 5%');
  assert.ok(R.cacheResumeWarning(costly({ context: { used: 125000 } }), NOW, {}), '剛好 5%');
  assert.equal(R.cacheResumeWarning(costly({ context: null }), NOW, {}), null);
  assert.equal(R.cacheResumeWarning(costly({ idleSince: null }), NOW, {}), null);
});

test('cacheResumeWarning_Claude沒回報成本不提醒_50萬tokens只用掉不到1%也不提醒', () => {
  assert.equal(R.cacheResumeWarning(hd(), NOW, {}), null, '沒有 coldResume（沒有訂閱方案、閒置壓縮已寫好摘要）');
  assert.equal(R.cacheResumeWarning(hd({ coldResume: { perMillion: 0.9, minPercent: 5 }, context: { used: 500000 } }), NOW, {}), null);
  assert.ok(R.cacheResumeWarning(costly({ coldResume: { perMillion: 40 } }), NOW, {}), '沒有 minPercent 時以 5% 為界');
  assert.equal(R.cacheResumeWarning(costly({ coldResume: { perMillion: 40, minPercent: 7 } }), NOW, {}), null, '照 Claude 給的門檻');
  assert.equal(R.cacheResumeWarning(costly({ coldResume: { perMillion: 0 } }), NOW, {}), null);
});

test('cacheResumeWarning_設定關掉_執行中_草稿_其他代理不提醒', () => {
  assert.equal(R.cacheResumeWarning(costly(), NOW, { claudeIdleResumeWarning: false }), null);
  assert.ok(R.cacheResumeWarning(costly(), NOW, { claudeIdleResumeWarning: true }));
  assert.ok(R.cacheResumeWarning(costly(), NOW, null), '設定還沒到：照預設提醒');
  assert.equal(R.cacheResumeWarning(costly({ running: true }), NOW, {}), null);
  assert.equal(R.cacheResumeWarning(costly({ isDraft: true }), NOW, {}), null);
  assert.equal(R.cacheResumeWarning(costly({ provider: 'Codex' }), NOW, {}), null);
  assert.equal(R.cacheResumeWarning(null, NOW, {}), null);
  assert.match(R.cacheResumeWarning(costly({ idleSince: NOW - 3 * 24 * HOUR }), NOW, {}).message, /閒置 3 天/);
});

test('coldResumePercent與percentText', () => {
  assert.ok(Math.abs(R.coldResumePercent(costly()) - 6) < 1e-9);
  assert.equal(R.coldResumePercent(hd()), null);
  assert.equal(R.coldResumePercent(costly({ context: { used: 0 } })), null);
  assert.equal(R.percentText(0.46), '0.5%');
  assert.equal(R.percentText(6), '6%');
  assert.equal(R.percentText(12.4), '12%');
  assert.equal(R.percentText(0.04), '不到 0.1%');
  const expired = R.cacheCountdown(hd({ coldResume: { perMillion: 0.9, minPercent: 5 }, context: { used: 500000 } }), NOW);
  assert.match(expired.title, /，約用掉 5 小時額度的 0\.5%$/, '過期的圓環說明寫出重送成本');
});

test('quotaWaitView_帳戶與重置時間_剩不到一分鐘不寫還有多久', () => {
  const until = NOW + 2 * HOUR + 30 * 60000;
  const v = R.quotaWaitView({ until, account: '工作用 Max' }, NOW);
  const at = new Date(until);
  const hm = String(at.getHours()).padStart(2, '0') + ':' + String(at.getMinutes()).padStart(2, '0');
  assert.equal(v.text, '額度用完：等「工作用 Max」在 ' + hm + ' 重置後自動接著做（還有 2 小時 30 分）');
  assert.match(v.title, /取消/);
  assert.doesNotMatch(R.quotaWaitView({ until: NOW + 30000 }, NOW).text, /還有/);
  assert.match(R.quotaWaitView({ until: NOW + 30000 }, NOW).text, /等這個帳戶在/);
  assert.equal(R.quotaWaitView(null, NOW), null);
  assert.equal(R.quotaWaitView({ until: 'x' }, NOW), null);
});
