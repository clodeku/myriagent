// 輸入框草稿同步的純規則（src/Myriagent/Web/draftsyncrules.js）：什麼時候送、別處的要換掉、提示還是不理、composerDrafts 的合併。
// 規則模組沒有 import，以 data: URL 匯入（同 tabdragrules.test.mjs）。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const source = readFileSync(path.resolve(here, '..', '..', 'src', 'Myriagent', 'Web', 'draftsyncrules.js'), 'utf8');
const D = await import('data:text/javascript;base64,' + Buffer.from(source + '\n// draftsyncrules.test\n', 'utf8').toString('base64'));

const act = (o) => D.remoteDraftAction({ focused: false, lastInputAt: 0, now: 100000, ...o });

test('shouldPublish：和上次送的不同才送；空白算清掉；超過上限不送', () => {
  assert.equal(D.shouldPublish('abc', ''), true);
  assert.equal(D.shouldPublish('abc', 'abc'), false);
  assert.equal(D.shouldPublish('  ', ''), false, '空白和空的一樣');
  assert.equal(D.shouldPublish('', 'abc'), true, '刪光了要送清掉');
  assert.equal(D.shouldPublish('abc', null), true, '「保留這裡的」：一律再送一次');
  assert.equal(D.shouldPublish('x'.repeat(D.DRAFT_MAX_CHARS + 1), ''), false);
  assert.equal(D.shouldPublish('x'.repeat(D.DRAFT_MAX_CHARS), ''), true);
});

test('沒改過：別處的直接換上（含別處清掉了，例如在那邊送出了）', () => {
  assert.equal(act({ current: '', synced: '', remote: '幫我' }), 'apply');
  assert.equal(act({ current: '幫我', synced: '幫我', remote: '幫我看一下' }), 'apply');
  assert.equal(act({ current: '幫我', synced: '幫我', remote: '' }), 'apply');
});

test('已經一樣、別處的就是上次同步的：same／ignore', () => {
  assert.equal(act({ current: '幫我', synced: '', remote: '幫我' }), 'same');
  assert.equal(act({ current: '幫我改', synced: '幫我', remote: '幫我' }), 'ignore');
});

test('正在打的不蓋掉：改過了、或剛打過字還有焦點 → 提示', () => {
  assert.equal(act({ current: '我自己打的', synced: '', remote: '別處的' }), 'offer');
  assert.equal(act({ current: '幫我', synced: '幫我', remote: '幫我看', focused: true, lastInputAt: 99000 }), 'offer', '剛送出去、手還在打');
  assert.equal(act({ current: '幫我', synced: '幫我', remote: '幫我看', focused: true, lastInputAt: 100000 - D.DRAFT_EDIT_GRACE_MS }), 'apply', '停下夠久了');
  assert.equal(act({ current: '幫我', synced: '幫我', remote: '幫我看', focused: false, lastInputAt: 99000 }), 'apply', '沒有焦點');
});

test('正在打、別處清掉了：留著這裡的字', () => {
  assert.equal(act({ current: '', synced: '別處的', remote: '' }), 'same', '這裡也是空的時一樣');
  assert.equal(act({ current: '我自己打的', synced: '別處的', remote: '' }), 'ignore');
  assert.equal(act({ current: '我自己打的', synced: '', remote: '' }), 'ignore');
});

test('mergeDrafts：新的記下、舊的 rev 不要、清掉的拿掉，回傳變了的', () => {
  const map = new Map();
  let c = D.mergeDrafts(map, { full: false, drafts: [{ conversationId: 'a', text: 'x', rev: 5, from: '桌機', at: 1 }] });
  assert.deepEqual(c, [{ conversationId: 'a', text: 'x', from: '桌機', rev: 5 }]);
  assert.deepEqual(D.mergeDrafts(map, { full: false, drafts: [{ conversationId: 'a', text: 'old', rev: 4 }] }), [], '舊的 rev');
  assert.deepEqual(D.mergeDrafts(map, { full: false, drafts: [{ conversationId: 'a', text: 'x', rev: 5, from: '桌機' }] }), [], '一樣的');
  c = D.mergeDrafts(map, { full: false, drafts: [{ conversationId: 'a', text: '', rev: 6, from: '網頁版' }] });
  assert.deepEqual(c, [{ conversationId: 'a', text: '', from: '網頁版', rev: 6 }]);
  assert.equal(map.has('a'), false);
  assert.deepEqual(D.mergeDrafts(map, { full: false, drafts: [{ conversationId: 'b', text: ' ', rev: 7 }] }), [], '清掉一則本來就沒有的');
});

test('mergeDrafts：rev 0（這台的另一個視窗）一律收', () => {
  const map = new Map([['a', { text: 'x', rev: 9, from: '桌機', at: 1 }]]);
  const c = D.mergeDrafts(map, { full: false, drafts: [{ conversationId: 'a', text: 'y', rev: 0, from: null }] });
  assert.deepEqual(c, [{ conversationId: 'a', text: 'y', from: null, rev: 0 }]);
});

test('mergeDrafts：整份的沒列出、伺服器給過的當成清掉；rev 0 的與沒記過的不動', () => {
  const map = new Map([['a', { text: 'x', rev: 3, from: '桌機', at: 1 }], ['b', { text: 'y', rev: 0, from: null, at: 1 }]]);
  const c = D.mergeDrafts(map, { full: true, drafts: [{ conversationId: 'c', text: 'z', rev: 8, from: '筆電' }] });
  assert.deepEqual(c, [
    { conversationId: 'c', text: 'z', from: '筆電', rev: 8 },
    { conversationId: 'a', text: '', from: '桌機', rev: 3 },
  ]);
  assert.deepEqual([...map.keys()].sort(), ['b', 'c']);
});

test('mergeDrafts：不合格的訊息與項目略過', () => {
  const map = new Map();
  assert.deepEqual(D.mergeDrafts(map, null), []);
  assert.deepEqual(D.mergeDrafts(map, { drafts: 'x' }), []);
  assert.deepEqual(D.mergeDrafts(map, { drafts: [null, { conversationId: 1, text: 'x' }, { conversationId: 'a' }] }), []);
});

test('提示的文字：第一行、截短；來源名稱', () => {
  assert.equal(D.draftPreview('  第一行\n第二行'), '第一行');
  assert.equal(D.draftPreview('x'.repeat(60), 48), 'x'.repeat(48) + '…');
  assert.equal(D.draftSourceLabel('辦公室桌機'), '辦公室桌機');
  assert.equal(D.draftSourceLabel(null), '這台的另一個視窗');
  assert.equal(D.draftSourceLabel(' '), '這台的另一個視窗');
});

test('最近送出的訊息：記下、比對（前後空白不算）、過期與壞掉的丟掉、同一則只留最新', () => {
  const now = 1_000_000_000;
  let list = D.rememberSent([], 'c1', '  你要擔任交易員  ', now);
  assert.equal(D.wasSent(list, 'c1', '你要擔任交易員'), true);
  assert.equal(D.wasSent(list, 'c1', '你要擔任交易員' + String.fromCharCode(10)), true);
  assert.equal(D.wasSent(list, 'c2', '你要擔任交易員'), false, '別的對話不算');
  assert.equal(D.wasSent(list, 'c1', ''), false);
  list = D.rememberSent(list, 'c1', '你要擔任交易員', now + 5);
  assert.equal(list.length, 1);
  assert.equal(D.rememberSent(list, 'c1', '   ', now).length, 1, '空白不記');
  const raw = JSON.stringify([...list, { id: 'c9', text: 'old', at: now - D.SENT_TTL_MS - 1 }, { bad: true }]);
  assert.deepEqual(D.parseSent(raw, now + 10).map((e) => e.id), ['c1']);
  assert.deepEqual(D.parseSent('{壞掉', now), []);
  let many = [];
  for (let i = 0; i < D.SENT_MAX + 5; i++) many = D.rememberSent(many, 'c1', 't' + i, now + i);
  assert.equal(many.length, D.SENT_MAX);
  assert.equal(many[0].text, 't5');
});
