import { test } from 'node:test';
import assert from 'node:assert/strict';
import { codexShown, goalSummary, speedOptions, reviewCommand } from '../../src/Myriagent/Web/codexrules.js';

test('本機 Codex 才提供控制，遠端草稿與副本不露出無法執行的按鈕', () => {
  assert.equal(codexShown({ provider: 'Codex' }, {}), true);
  assert.equal(codexShown({ provider: 'Codex' }, { isHub: true }), false);
  assert.equal(codexShown({ provider: 'Codex', isDraft: true, deviceId: 'remote' }, {}), false);
  assert.equal(codexShown({ provider: 'Claude' }, {}), false);
});
test('持續目標顯示實際狀態、預算與時間', () => {
  assert.match(goalSummary({ status: 'budgetLimited', tokensUsed: 1200, tokenBudget: 1200, timeUsedSeconds: 65 }), /已達預算.*1,200／1,200.*1 分 5 秒/);
  assert.equal(goalSummary(null), '尚未設定持續目標');
});
test('Fast 使用回報的 priority，未知的 Ultrafast 不自行加入', () => {
  assert.deepEqual(speedOptions([{ id: 'priority', name: 'Fast' }]).map(x => x.id), ['default', 'priority']);
  assert.deepEqual(speedOptions(null).map(x => x.id), ['default']);
});
test('審查範圍明確且無效參數不送出', () => {
  assert.equal(reviewCommand('uncommittedChanges'), '/review');
  assert.equal(reviewCommand('commit', ' a123456 '), '/review commit a123456');
  assert.equal(reviewCommand('baseBranch', 'main'), '/review base main');
  assert.throws(() => reviewCommand('commit', 'not-a-hash'));
  assert.throws(() => reviewCommand('baseBranch', '-bad'));
});
