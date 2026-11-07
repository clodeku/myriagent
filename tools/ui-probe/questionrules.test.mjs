// 問卷卡的補充說明規則（Web/questionrules.js）；node --test questionrules.test.mjs。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../../src/Myriagent/Web/questionrules.js', import.meta.url), 'utf8');
const R = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

const color = { id: '要用哪種顏色？', options: [{ label: '紅色' }, { label: '藍色' }] };
const secret = { id: 'pwd', secret: true, options: [{ label: 'x' }] };
const free = { id: '數量', options: [] };

test('補充欄：有選項、不是密碼類、不是外部來源（MCP）的提問才給', () => {
  assert.equal(R.noteFieldShown(color, null), true);
  assert.equal(R.noteFieldShown(secret, null), false, '密碼類的補充會留在畫面與存檔');
  assert.equal(R.noteFieldShown(free, null), false, '沒有選項的本來就是自由輸入');
  assert.equal(R.noteFieldShown(color, 'MCP：filesystem'), false, 'MCP 的表單沒有地方放補充');
  assert.equal(R.noteFieldShown(null, null), false);
});

test('送出的 notes：只收有補充欄的題目、去空白、截斷；沒有任何補充回 null（不帶欄位）', () => {
  assert.deepEqual(R.buildNotes([color, secret, free], { [color.id]: '  深一點  ', pwd: '不該送', 數量: '三' }, null), { [color.id]: ['深一點'] });
  assert.equal(R.buildNotes([color], { [color.id]: '   ' }, null), null);
  assert.equal(R.buildNotes([color], {}, null), null);
  assert.equal(R.buildNotes([color], { [color.id]: '紅' }, 'MCP：x'), null);
  assert.equal(R.buildNotes([color], { [color.id]: '長'.repeat(R.NOTE_MAX + 9) }, null)[color.id][0].length, R.NOTE_MAX);
});

test('已回答的值：「補充說明：」開頭的另起一行，其他照舊是答案', () => {
  assert.deepEqual(R.splitAnswer(['紅色', R.NOTE_PREFIX + '深一點']), { answers: ['紅色'], notes: ['深一點'] });
  assert.deepEqual(R.splitAnswer(['紅色']), { answers: ['紅色'], notes: [] });
  assert.deepEqual(R.splitAnswer(null), { answers: [], notes: [] });
});

test('第十一批：作答進度——選了選項或「其他」有字算回答，補充說明有字標 note，空白不算', () => {
  const qs = [{ id: 'a' }, { id: 'b' }, { id: 'c' }];
  const st = { sel: { a: ['SQLite'], b: [], c: [] }, other: { a: '', b: '  ', c: '自己的答案' }, note: { a: '', b: '原因', c: ' ' } };
  assert.deepEqual(R.progressCells(qs, st), [{ answered: true, note: false }, { answered: false, note: true }, { answered: true, note: false }]);
  assert.deepEqual(R.progressCells(qs, null), [{ answered: false, note: false }, { answered: false, note: false }, { answered: false, note: false }]);
  assert.deepEqual(R.progressCells(null, st), []);
});
