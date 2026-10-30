// 網頁錯誤回報的純規則（src/Myriagent/Web/errorrules.js）：例外、Promise 拒絕、媒體載入失敗組成 clientError 的回報、截短、
// 不回報的訊息與網址、節流（同一則 10 分鐘一次、每次載入最多 100 則），以及 errorrules.js 沒有 import、errorreport.js 只經 store.js 送出。
// 規則模組沒有 import，以 data: URL 匯入（同 rules.test.mjs）。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const webDir = path.resolve(here, '..', '..', 'src', 'Myriagent', 'Web');
const source = readFileSync(path.join(webDir, 'errorrules.js'), 'utf8');
const E = await import('data:text/javascript;base64,' + Buffer.from(source + '\n// errorrules.test\n', 'utf8').toString('base64'));

test('例外：Error 寫名稱與訊息、帶來源與堆疊', () => {
  const error = new TypeError('x is undefined');
  const r = E.fromError({ message: 'Uncaught TypeError: x is undefined', filename: 'https://app.local/items.js', lineno: 12, colno: 3, error });
  assert.equal(r.kind, 'error');
  assert.equal(r.message, 'TypeError: x is undefined');
  assert.equal(r.source, 'https://app.local/items.js:12:3');
  assert.match(r.detail, /x is undefined/);
});

test('例外：丟出的不是 Error、沒有 error 物件、沒有檔名', () => {
  assert.equal(E.fromError({ message: 'boom', error: 'just a string' }).message, 'just a string');
  const r = E.fromError({ message: 'Uncaught boom' });
  assert.equal(r.message, 'Uncaught boom');
  assert.equal(r.source, null);
  assert.equal(r.detail, null);
  assert.equal(E.fromError(null), null);
  assert.equal(E.fromError({ message: '' }), null, '空的訊息不回報');
});

test('例外：瀏覽器的無害警告與跨來源的 Script error 不回報', () => {
  assert.equal(E.fromError({ message: 'ResizeObserver loop completed with undelivered notifications.' }), null);
  assert.equal(E.fromError({ message: 'Script error.' }), null);
  assert.equal(E.fromRejection('ResizeObserver loop limit exceeded'), null);
});

test('Promise 拒絕：Error、字串、undefined、轉不成文字的物件', () => {
  const r = E.fromRejection(new Error('fetch failed'));
  assert.equal(r.kind, 'rejection');
  assert.equal(r.message, 'Error: fetch failed');
  assert.equal(E.fromRejection('nope').message, 'nope');
  assert.equal(E.fromRejection(undefined).message, 'undefined');
  const weird = { toString() { throw new Error('no'); } };
  assert.equal(E.fromRejection(weird).message, '（無法轉成文字）');
});

test('媒體：網址當來源；沒有網址、等於頁面網址的不回報；data:、blob: 只寫通訊協定', () => {
  const r = E.fromMedia('img', 'https://hub.example.com/t/d_X-Y?s=320', 'https://hub.example.com/app/');
  assert.deepEqual(r, { kind: 'media', message: 'img 載入失敗', source: 'https://hub.example.com/t/d_X-Y?s=320', detail: null });
  assert.equal(E.fromMedia('img', '', 'https://a/'), null);
  assert.equal(E.fromMedia('img', 'https://a/', 'https://a/'), null, 'src 清空時解析成頁面網址');
  assert.equal(E.fromMedia('img', 'data:image/png;base64,' + 'A'.repeat(10000), 'https://a/').source, 'data:…');
  assert.equal(E.fromMedia('video', 'BLOB:https://a/1', 'https://a/').source, 'blob:…');
});

test('截短：訊息 500、來源 500、堆疊 2000，超過加「…」；只有空白的算沒有', () => {
  const r = E.report('error', 'm'.repeat(600), 's'.repeat(600), 'd'.repeat(3000));
  assert.equal(r.message.length, E.MAX_MESSAGE + 1);
  assert.ok(r.message.endsWith('…'));
  assert.equal(r.source.length, E.MAX_SOURCE + 1);
  assert.equal(r.detail.length, E.MAX_DETAIL + 1);
  assert.equal(E.report('error', '   ', null, null), null);
  assert.equal(E.report('error', ' ok ', '  ', 42).source, null);
  assert.equal(E.report('error', ' ok ', null, 42).detail, null, '不是字串的算沒有');
});

test('節流：同一則 10 分鐘內只送一次，不同來源各自算，總共最多 100 則', () => {
  let now = 0;
  const allow = E.createLimiter(() => now);
  const a = E.report('media', 'img 載入失敗', '/t/1', null);
  const b = E.report('media', 'img 載入失敗', '/t/2', null);
  assert.equal(allow(a), true);
  assert.equal(allow(a), false);
  assert.equal(allow(b), true, '來源不同');
  now = E.WINDOW_MS - 1;
  assert.equal(allow(a), false);
  now = E.WINDOW_MS;
  assert.equal(allow(a), true, '過了窗口再送一次');
  assert.equal(allow(null), false);

  const many = E.createLimiter(() => 0);
  for (let i = 0; i < E.MAX_REPORTS; i++) assert.equal(many(E.report('error', 'e' + i, null, null)), true);
  assert.equal(many(E.report('error', 'one more', null, null)), false, '每次載入最多 100 則');
});

test('errorrules.js 沒有 import；errorreport.js 只經 store.js 的 post 送出、不碰 chrome.webview', () => {
  assert.doesNotMatch(source, /^\s*import\s/m);
  const report = readFileSync(path.join(webDir, 'errorreport.js'), 'utf8');
  assert.match(report, /import \{ post \} from '\.\/store\.js';/);
  assert.match(report, /cmd: 'clientError'/);
  assert.doesNotMatch(report, /chrome\.webview/);
  const app = readFileSync(path.join(webDir, 'app.js'), 'utf8');
  assert.match(app, /^installErrorReporting\(\);$/m, 'app.js 一開始就裝上');
});
