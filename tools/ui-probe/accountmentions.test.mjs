import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../../src/Myriagent/Web/accountmentions.js', import.meta.url), 'utf8');
const { accountSuggestions, accountMention } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));
const header = { accountId: 'claude', accountDelegation: true };
const accounts = [
  { id: 'claude', label: 'Claude', status: 'ready', provider: 'Claude' },
  { id: 'codex1', label: 'Codex 主帳戶', status: 'ready', provider: 'Codex' },
  { id: 'codex2', label: 'Codex 主帳戶', status: 'ready', provider: 'Codex' },
  { id: 'remote', label: '遠端', status: 'ready', provider: 'Codex', remoteOnly: true },
  { id: 'loggedout', label: '未登入', status: 'notLoggedIn', provider: 'Codex' },
];

test('@ 只列出其他本機已登入帳戶，依名稱或供應商搜尋', () => {
  assert.deepEqual(accountSuggestions(accounts, header, 'codex').map(a => a.value), ['codex1', 'codex2']);
  assert.deepEqual(accountSuggestions(accounts, header, '').map(a => a.value), ['codex1', 'codex2']);
  assert.deepEqual(accountSuggestions(accounts, {}, ''), []);
});

test('同名帳戶保留不同身分，名稱不能破壞提及格式', () => {
  const items = accountSuggestions(accounts, header, '');
  assert.equal(accountMention(items[0]), '@[Codex 主帳戶](account:codex1)');
  assert.notEqual(accountMention(items[0]), accountMention(items[1]));
  assert.equal(accountMention({ main: 'a]\nb', value: 'codex1' }), '@[a  b](account:codex1)');
});

test('已送出的訊息：提及拆成文字與提及的片段，沒有提及回 null', async () => {
  const { mentionParts, mentionPlain, mentionView } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));
  assert.deepEqual(mentionParts('@[Codex 主帳戶](account:codex1) 請檢查，再問 @[Grok](account:g-2)'),
    [{ label: 'Codex 主帳戶', id: 'codex1' }, ' 請檢查，再問 ', { label: 'Grok', id: 'g-2' }]);
  assert.deepEqual(mentionParts('前面 @[A](account:a1)'), ['前面 ', { label: 'A', id: 'a1' }]);
  assert.equal(mentionParts('沒有提及'), null);
  assert.equal(mentionParts('[連結](account:bad id)'), null, '格式不對（沒有 @、id 有空白）照原文');
  assert.equal(mentionParts(null), null);
  assert.equal(mentionPlain('@[Codex 主帳戶](account:codex1) 請檢查'), '@Codex 主帳戶 請檢查');
  assert.equal(mentionPlain('照原文'), '照原文');
  assert.equal(mentionPlain(undefined), '');
});

test('提及的晶片：認得的帳戶用目前的名稱與帳戶色，不認得的照訊息裡的名稱', async () => {
  const { mentionView } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));
  const known = mentionView('舊名稱', 'codex1', { id: 'codex1', label: 'Codex 主帳戶', provider: 'Codex', email: 'a@b.c', color: '#10a37f' });
  assert.deepEqual(known, { text: '@Codex 主帳戶', title: 'AI 帳戶：Codex 主帳戶（Codex・a@b.c）', known: true, color: '#10a37f', provider: 'Codex' });
  const unknown = mentionView('別台的帳戶', 'zzz', undefined);
  assert.equal(unknown.text, '@別台的帳戶');
  assert.equal(unknown.known, false);
  assert.equal(unknown.color, null);
  assert.equal(mentionView('x', 'a', { id: 'b', label: 'B' }).known, false, 'id 對不上不算');
});
