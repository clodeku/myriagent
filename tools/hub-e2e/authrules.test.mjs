// 登入頁純規則的單元測試（node:test，不開瀏覽器、不啟動伺服器）：(cd tools/hub-e2e && node --test authrules.test.mjs)
// src/MyriagentHub/wwwroot/authrules.js 沒有 import，以 data: URL 匯入（不依賴 Node 怎麼判定 .js 是不是 module）。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const file = path.resolve(here, '..', '..', 'src', 'MyriagentHub', 'wwwroot', 'authrules.js');
const source = readFileSync(file, 'utf8');
const A = await import('data:text/javascript;base64,' + Buffer.from(source, 'utf8').toString('base64'));

test('authrules_沒有import也不碰DOM', () => {
  // 註解拿掉再看（說明裡會寫到 location.hash）
  const code = source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/.*$/gm, '');
  assert.doesNotMatch(code, /^\s*import\s/m);
  assert.doesNotMatch(code, /document|window|location|innerHTML|fetch\(/);
  assert.deepEqual(Object.keys(A).sort(), ['afterLoginTarget', 'canArmApprove', 'cleanPairCode', 'decodeFrame', 'decodeLocked', 'decodeSteps', 'formatUserCode', 'isCollaboratorMe', 'pairErrorText', 'parsePairHash']);
});

test('parsePairHash_空字串與沒有片段', () => {
  assert.deepEqual(A.parsePairHash(''), { pair: false });
  assert.deepEqual(A.parsePairHash('#'), { pair: false });
  assert.deepEqual(A.parsePairHash(undefined), { pair: false });
  assert.deepEqual(A.parsePairHash(null), { pair: false });
});

test('parsePairHash_只有pair', () => {
  assert.deepEqual(A.parsePairHash('#pair'), { pair: true, code: null });
  assert.deepEqual(A.parsePairHash('#pair='), { pair: true, code: null }, '等號後面沒有碼');
});

test('parsePairHash_帶碼', () => {
  assert.deepEqual(A.parsePairHash('#pair=MTLZ-XFCH'), { pair: true, code: 'MTLZXFCH' });
  assert.deepEqual(A.parsePairHash('#pair=MTLZXFCH'), { pair: true, code: 'MTLZXFCH' });
});

test('parsePairHash_小寫加空白', () => {
  assert.deepEqual(A.parsePairHash('#pair=mtlz xfch'), { pair: true, code: 'MTLZXFCH' });
  assert.deepEqual(A.parsePairHash('#pair= mtlz - xfch '), { pair: true, code: 'MTLZXFCH' });
});

test('parsePairHash_URL編碼', () => {
  assert.deepEqual(A.parsePairHash('#pair=mtlz%20xfch'), { pair: true, code: 'MTLZXFCH' });
  assert.deepEqual(A.parsePairHash('#pair=MTLZ%2DXFCH'), { pair: true, code: 'MTLZXFCH' });
  assert.deepEqual(A.parsePairHash('#pair=%E9%85%8DMTLZ'), { pair: true, code: 'MTLZ' }, '中文字解碼後拿掉');
});

test('parsePairHash_壞的編碼當成沒有碼', () => {
  assert.deepEqual(A.parsePairHash('#pair=%E0%A4%A'), { pair: true, code: null });
  assert.deepEqual(A.parsePairHash('#pair=MTLZ%'), { pair: true, code: null });
});

test('parsePairHash_不是pair片段', () => {
  assert.deepEqual(A.parsePairHash('#pairx'), { pair: false });
  assert.deepEqual(A.parsePairHash('#pairing=MTLZ'), { pair: false });
  assert.deepEqual(A.parsePairHash('#other'), { pair: false });
  assert.deepEqual(A.parsePairHash('#PAIR'), { pair: false }, '大小寫要相符');
  assert.deepEqual(A.parsePairHash('pair=MTLZ'), { pair: false }, '沒有 # 不是片段');
});

test('parsePairHash_超長只留16字_符號一律拿掉', () => {
  const r = A.parsePairHash('#pair=' + 'ab'.repeat(40));
  assert.equal(r.pair, true);
  assert.equal(r.code, 'AB'.repeat(8));
  assert.equal(r.code.length, 16);
  assert.deepEqual(A.parsePairHash('#pair=<img src=x>'), { pair: true, code: 'IMGSRCX' });
  assert.deepEqual(A.parsePairHash('#pair=---'), { pair: true, code: null }, '清完是空的');
  assert.deepEqual(A.parsePairHash('#pair=stra%C3%9Fe'), { pair: true, code: 'STRAE' }, '先過濾再轉大寫：ß 不會變成 SS');
});

test('cleanPairCode', () => {
  assert.equal(A.cleanPairCode('mtlz-xfch'), 'MTLZXFCH');
  assert.equal(A.cleanPairCode('  MTLZ XFCH  '), 'MTLZXFCH');
  assert.equal(A.cleanPairCode(''), null);
  assert.equal(A.cleanPairCode('－＿'), null, '全形符號也拿掉');
  assert.equal(A.cleanPairCode(null), null);
  assert.equal(A.cleanPairCode(12345678), null);
});

test('afterLoginTarget', () => {
  assert.equal(A.afterLoginTarget(''), '/app/');
  assert.equal(A.afterLoginTarget('#other'), '/app/');
  assert.equal(A.afterLoginTarget('#pairx'), '/app/');
  assert.equal(A.afterLoginTarget('#pair'), null);
  assert.equal(A.afterLoginTarget('#pair=MTLZ-XFCH'), null);
  assert.equal(A.afterLoginTarget('#pair=%E0%A4%A'), null, '壞的編碼仍是配對頁');
  // 多人協作：協作人只有網頁版（配對、版本頁都不是他的）
  assert.equal(A.afterLoginTarget('#pair=MTLZ-XFCH', 'collaborator'), '/app/');
  assert.equal(A.afterLoginTarget('#releases', 'collaborator'), '/app/');
  assert.equal(A.afterLoginTarget('#pair', 'owner'), null);
});

test('isCollaboratorMe', () => {
  assert.equal(A.isCollaboratorMe({ role: 'collaborator', name: 'ming' }), true);
  assert.equal(A.isCollaboratorMe({ role: 'owner', name: 'admin' }), false);
  assert.equal(A.isCollaboratorMe({ name: 'admin' }), false, '舊伺服器沒有 role：擁有者');
  assert.equal(A.isCollaboratorMe(null), false);
});

test('pairErrorText_每個代碼', () => {
  assert.match(A.pairErrorText(404, 'not_found'), /找不到這個配對碼/);
  assert.match(A.pairErrorText(400, 'invalid_request'), /重新開始配對/);
  assert.match(A.pairErrorText(409, 'too_many_devices'), /已達上限/);
  assert.match(A.pairErrorText(429, 'rate_limited'), /太頻繁/);
  assert.match(A.pairErrorText(429, null), /太頻繁/, '限流沒有本文也認得');
  assert.match(A.pairErrorText(403, 'forbidden_origin'), /來源不符/);
  assert.match(A.pairErrorText(0, null), /連不上伺服器/);
  assert.equal(A.pairErrorText(500, null), '伺服器回應 500，請稍後再試。');
  assert.equal(A.pairErrorText(undefined, undefined), '發生錯誤，請稍後再試。');
});

test('pairErrorText_代碼優先於狀態_沒有代碼看狀態', () => {
  assert.match(A.pairErrorText(400, 'not_found'), /找不到這個配對碼/);
  assert.match(A.pairErrorText(404, null), /找不到這個配對碼/);
  assert.match(A.pairErrorText(409, 'other'), /已達上限/);
  assert.match(A.pairErrorText(400, 'whatever'), /不能核准/);
  assert.match(A.pairErrorText(403, 'step_up_required'), /請求被拒絕/);
});

test('formatUserCode', () => {
  assert.equal(A.formatUserCode('MTLZXFCH'), 'MTLZ-XFCH');
  assert.equal(A.formatUserCode('MTLZ'), 'MTLZ', '不是 8 字照原樣');
  assert.equal(A.formatUserCode('MTLZ-XFCH'), 'MTLZ-XFCH');
  assert.equal(A.formatUserCode('mtlzxfch'), 'mtlzxfch', '只格式化清理過的大寫碼');
  assert.equal(A.formatUserCode(null), '');
});

test('canArmApprove_全部成立而且滿1秒', () => {
  const ok = { shown: true, trusted: true, visible: true, focused: true, now: 11000, since: 10000 };
  assert.equal(A.canArmApprove(ok), true, '正好 1 秒');
  assert.equal(A.canArmApprove(Object.assign({}, ok, { now: 10999 })), false, '差 1 毫秒');
  assert.equal(A.canArmApprove(Object.assign({}, ok, { now: 60000 })), true);
});

test('canArmApprove_任一條件不成立都不解除', () => {
  const ok = { shown: true, trusted: true, visible: true, focused: true, now: 20000, since: 10000 };
  for (const k of ['shown', 'trusted', 'visible', 'focused']) {
    assert.equal(A.canArmApprove(Object.assign({}, ok, { [k]: false })), false, k + ' 是 false');
    assert.equal(A.canArmApprove(Object.assign({}, ok, { [k]: 'true' })), false, k + ' 只認布林 true');
  }
  assert.equal(A.canArmApprove(Object.assign({}, ok, { since: NaN })), false, 'since 不是數字');
  assert.equal(A.canArmApprove(Object.assign({}, ok, { now: undefined })), false, 'now 沒給');
  assert.equal(A.canArmApprove(null), false);
  assert.equal(A.canArmApprove(undefined), false);
});

test('decodeSteps_最多18格_空字串0格', () => {
  assert.equal(A.decodeSteps(0), 0);
  assert.equal(A.decodeSteps(5), 5);
  assert.equal(A.decodeSteps(47), 18);
  assert.equal(A.decodeSteps(undefined), 0);
});

test('decodeLocked_逐格增加_最後一格全部鎖定', () => {
  const n = 47, steps = A.decodeSteps(n);
  let prev = -1;
  for (let k = 1; k <= steps; k++) {
    const locked = A.decodeLocked(n, k, steps);
    assert.ok(locked >= prev, `第 ${k} 格不倒退`);
    prev = locked;
  }
  assert.equal(A.decodeLocked(n, steps, steps), n);
  assert.equal(A.decodeLocked(3, 9, 0), 3, '沒有格數時直接全部');
});

test('decodeFrame_鎖定的照原文_其他英數字換亂碼_中文與標點不動', () => {
  const fixed = () => 0; // 一律取第一個字 A
  assert.equal(A.decodeFrame('AB12-CD34', 0, fixed), 'AAAA-AAAA');
  assert.equal(A.decodeFrame('AB12-CD34', 4, fixed), 'AB12-AAAA');
  assert.equal(A.decodeFrame('工作機 pc', 0, fixed), '工作機 aa', '小寫照小寫、中文與空白照原樣');
  assert.equal(A.decodeFrame('AB12-CD34', 99, fixed), 'AB12-CD34');
  assert.equal(A.decodeFrame('😀x', 0, () => 0.9999), '😀9', '以碼位走；rand 接近 1 不超出字表');
  assert.equal(A.decodeFrame(null, 0, fixed), '');
});
