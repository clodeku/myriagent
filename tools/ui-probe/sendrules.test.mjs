// 送出中的純規則（src/Myriagent/Web/sendrules.js）：要不要先顯示、宿主的使用者訊息怎麼對上（html 還原、空白、seq）、
// 排進佇列的（含附件的寫法、正在插入的不算）、被拒絕的是哪一則、沒有送出的標示與原因（failureOf、failureUpdates），
// 以及 sendrules.js 沒有 import、沒有 HTML 寫入點。
// 規則模組沒有 import，以 data: URL 匯入（同 launchrules.test.mjs）。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const webDir = path.resolve(here, '..', '..', 'src', 'Myriagent', 'Web');
const source = readFileSync(path.join(webDir, 'sendrules.js'), 'utf8');
const S = await import('data:text/javascript;base64,' + Buffer.from(source + '\n// sendrules.test\n', 'utf8').toString('base64'));

/** 宿主 AgentMarkdownRenderer.PlainToHtml 的寫法：WebUtility.HtmlEncode（160～255 與代理字元對轉成數字參照）＋換行轉 <br> */
function plainToHtml(text) {
  let out = '';
  for (const ch of text) {
    const cp = ch.codePointAt(0);
    if (ch === '<') out += '&lt;';
    else if (ch === '>') out += '&gt;';
    else if (ch === '&') out += '&amp;';
    else if (ch === '"') out += '&quot;';
    else if (ch === "'") out += '&#39;';
    else if ((cp >= 160 && cp <= 255) || cp > 0xffff) out += '&#' + cp + ';';
    else out += ch;
  }
  return out.replace(/\r\n/g, '\n').replace(/\n/g, '<br>');
}

const user = (text, seq) => ({ id: 'u' + seq, kind: 'User', status: 'Done', html: plainToHtml(text), seq });
const entry = (text, o = {}) => Object.assign({ key: S.sendKey(text), text, files: 0, afterSeq: 10 }, o);

test('showsSending：有字或附件才顯示；/clear 不顯示；執行中只有本機、能插入的才顯示', () => {
  assert.equal(S.showsSending({ text: '幫我跑測試' }), true);
  assert.equal(S.showsSending({ text: '   ', files: 2 }), true);
  assert.equal(S.showsSending({ text: '  ' }), false);
  assert.equal(S.showsSending({ text: '' }), false);
  assert.equal(S.showsSending({ text: ' /clear ' }), false);
  assert.equal(S.showsSending({ text: '/clear 之後再說' }), true);
  // /btw 旁支提問：宿主加的是一則旁支提問的提示，不是使用者訊息
  assert.equal(S.showsSending({ text: ' /btw 現在在做什麼？' }), false);
  assert.equal(S.showsSending({ text: '/btw' }), false);
  assert.equal(S.showsSending({ text: '/btw\n多行' }), false);
  assert.equal(S.showsSending({ text: '/btwx 不是旁支提問' }), true);
  assert.equal(S.showsSending({ text: '插一句', running: true, steers: true }), true);
  assert.equal(S.showsSending({ text: '排隊', running: true, steers: false }), false);
  assert.equal(S.showsSending({ text: '遠端插入', running: true, steers: true, remote: true }), false);
  assert.equal(S.showsSending({ text: '遠端送出', running: false, remote: true }), true);
});

test('htmlText：還原宿主的 HtmlEncode 與 <br>，具名與數字參照都認得，不認得的照原樣', () => {
  const text = '<b>粗體</b> & "引號" \'單引號\'\n第二行 é ✓ 😀';
  assert.equal(S.htmlText(plainToHtml(text)), text);
  assert.equal(S.htmlText('a&nbsp;b&#x41;&#66;&unknown;'), 'a b' + 'AB&unknown;');
  assert.equal(S.htmlText('<p>段落<br/>換行</p>'), '段落\n換行');
  assert.equal(S.htmlText(null), '');
  assert.equal(S.htmlText(undefined), '');
});

test('matchSent：同樣的文字（空白不計）而且比送出時新才對上，取最早的那則', () => {
  const pending = [entry('第一則'), entry('同一句 話'), entry('同一句話')];
  assert.equal(S.matchSent(pending, user('同一句話', 11)), 1);
  assert.equal(S.matchSent(pending, user('第一則\n', 12)), 0);
  assert.equal(S.matchSent(pending, user('不一樣', 13)), -1);
  // 之前送過同樣文字的舊訊息（重新載入整個對話時會再出現一次）不算
  assert.equal(S.matchSent(pending, user('第一則', 9)), -1);
  assert.equal(S.matchSent(pending, user('第一則', 10)), -1);
  // 沒有 seq 的一邊不比
  assert.equal(S.matchSent([entry('x', { afterSeq: null })], user('x', 1)), 0);
  assert.equal(S.matchSent([entry('x')], { kind: 'User', html: 'x' }), 0);
  // 不是使用者訊息、沒有送出中的
  assert.equal(S.matchSent(pending, { kind: 'Assistant', html: '第一則', seq: 20 }), -1);
  assert.equal(S.matchSent([], user('x', 20)), -1);
  // 只附檔不打字：html 是空的
  assert.equal(S.matchSent([entry('', { files: 1 })], { id: 'u', kind: 'User', html: '', seq: 30, attachments: [{}] }), 0);
});

test('queuedSent：佇列文字（含附件的寫法）對上的送出中訊息，正在插入的那則不算', () => {
  const pending = [entry('接著跑測試'), entry('看這張圖', { files: 2 }), entry('還在插入')];
  const queue = [{ id: 'q1', text: '接著跑測試' }, { id: 'q2', text: '看這張圖（附 2 個檔案）' }, { id: 'q3', text: '還在插入' }];
  assert.deepEqual(S.queuedSent(pending, queue, 'q3'), [0, 1]);
  assert.deepEqual(S.queuedSent(pending, queue, null), [0, 1, 2]);
  // 同樣文字兩則送出中、佇列只有一則：只收一則
  assert.deepEqual(S.queuedSent([entry('同'), entry('同')], [{ id: 'a', text: '同' }], null), [0]);
  // 瀏覽器版的佇列文字被截短（夠長時比開頭）；太短的開頭不算
  const long = '很長的訊息'.repeat(40);
  assert.deepEqual(S.queuedSent([entry(long)], [{ id: 'a', text: long.slice(0, 120) + '…' }], null), [0]);
  assert.deepEqual(S.queuedSent([entry('abc')], [{ id: 'a', text: 'ab' }], null), []);
  assert.deepEqual(S.queuedSent([], queue, null), []);
  assert.deepEqual(S.queuedSent(pending, undefined, null), []);
});

test('queuedText：照宿主 RenderComposer 的寫法', () => {
  assert.equal(S.queuedText(' 看圖 ', 0), '看圖');
  assert.equal(S.queuedText('看圖', 3), '看圖（附 3 個檔案）');
  assert.equal(S.queuedText('', 1), '（附 1 個檔案）');
});

test('rejectedSent：同樣文字的最新一則；只附檔的只對沒有文字的那則', () => {
  const pending = [entry('a'), entry('b'), entry('a'), entry('', { files: 1 })];
  assert.equal(S.rejectedSent(pending, ' a '), 2);
  assert.equal(S.rejectedSent(pending, 'b'), 1);
  assert.equal(S.rejectedSent(pending, 'c'), -1);
  assert.equal(S.rejectedSent(pending, ''), 3);
  assert.equal(S.rejectedSent([entry('a')], ''), -1);
  assert.equal(S.rejectedSent([], 'a'), -1);
});

test('failureOf：宿主的原因、遠端被拒的原因、結果不明、本機一直沒有回應', () => {
  assert.deepEqual(S.failureOf({ reason: ' 工作資料夾不存在：D:\\gone，請重新選擇 ' }),
    { kind: 'failed', label: '沒有送出：工作資料夾不存在：D:\\gone，請重新選擇', specific: true });
  assert.deepEqual(S.failureOf({ state: 'rejected', code: 'not_sent' }), { kind: 'failed', label: '沒有送出：沒有連上伺服器', specific: true });
  assert.deepEqual(S.failureOf({ state: 'rejected', code: 'policy' }), { kind: 'failed', label: '沒有送出：那台電腦的設定不允許', specific: true });
  // 不認得的 code、沒有原因的 sendRejected：只寫「沒有送出」；原型鏈上的名稱不當成原因
  assert.deepEqual(S.failureOf({ state: 'rejected', code: 'whatever' }), { kind: 'failed', label: '沒有送出', specific: false });
  assert.deepEqual(S.failureOf({ state: 'rejected', code: 'toString' }), { kind: 'failed', label: '沒有送出', specific: false });
  assert.deepEqual(S.failureOf({ reason: '   ' }), { kind: 'failed', label: '沒有送出', specific: false });
  assert.deepEqual(S.failureOf(), { kind: 'failed', label: '沒有送出', specific: false });
  assert.equal(S.failureOf({ state: 'unknown' }).kind, 'unknown');
  assert.match(S.failureOf({ state: 'unknown' }).label, /^不確定有沒有送出/);
  const t = S.failureOf({ state: 'timeout' });
  assert.equal(t.kind, 'unknown');
  assert.equal(t.label, '等了 90 秒都沒有回應，可能沒有送出');
});

test('failureUpdates：還在送的一律標；確定的蓋過不確定的、有原因的蓋過沒有原因的，反過來不蓋', () => {
  const generic = S.failureOf({ state: 'rejected' });
  const specific = S.failureOf({ reason: '帳戶未登入' });
  const unknown = S.failureOf({ state: 'unknown' });
  assert.equal(S.failureUpdates(null, generic), true);
  assert.equal(S.failureUpdates(null, unknown), true);
  assert.equal(S.failureUpdates(generic, specific), true, 'sendRejected 先到、指令結果的原因後到');
  assert.equal(S.failureUpdates(specific, generic), false, '沒有原因的不蓋掉有原因的');
  assert.equal(S.failureUpdates(unknown, generic), true, '後來確定被拒');
  assert.equal(S.failureUpdates(specific, unknown), false, '已經確定被拒，不改成不確定');
  assert.equal(S.failureUpdates(specific, S.failureOf({ reason: '另一個原因' })), false);
  assert.equal(S.failureUpdates(null, null), false);
});

test('繼續鈕：送出使用者指定的原文；說明跟著執行中、能不能插入', () => {
  assert.equal(S.CONTINUE_TEXT, '進度如何呢如果還沒做完就繼續');
  assert.equal(S.continueTitle(), '繼續：送出「進度如何呢如果還沒做完就繼續」');
  assert.equal(S.continueTitle({ running: false, steers: true }), '繼續：送出「進度如何呢如果還沒做完就繼續」');
  assert.equal(S.continueTitle({ running: true, steers: true }), '繼續：把「進度如何呢如果還沒做完就繼續」插入這一輪');
  assert.equal(S.continueTitle({ running: true, steers: false }), '繼續：把「進度如何呢如果還沒做完就繼續」加入佇列（這一輪完成後送出）');
  // 繼續鈕送的是一般的 send：閒置時照樣先畫「送出中」
  assert.equal(S.showsSending({ text: S.CONTINUE_TEXT }), true);
});

test('繼續鈕的自訂指令：去頭尾空白、空的用預設、太長截斷不切開代理字元對；說明引用時併空白、超過 40 字加「…」', () => {
  const NL = String.fromCharCode(10);
  assert.equal(S.CONTINUE_MAX, 2000);
  assert.equal(S.continueTextOf('  做完了嗎？沒做完就接著做  '), '做完了嗎？沒做完就接著做');
  assert.equal(S.continueTextOf('第一行' + NL + '第二行'), '第一行' + NL + '第二行');
  assert.equal(S.continueTextOf(''), S.CONTINUE_TEXT);
  assert.equal(S.continueTextOf('  ' + NL + String.fromCharCode(9)), S.CONTINUE_TEXT);
  assert.equal(S.continueTextOf(null), S.CONTINUE_TEXT);
  assert.equal(S.continueTextOf(undefined), S.CONTINUE_TEXT);
  assert.equal(S.continueTextOf(42), S.CONTINUE_TEXT);
  assert.equal(S.continueTextOf('字'.repeat(S.CONTINUE_MAX + 5)), '字'.repeat(S.CONTINUE_MAX));
  assert.equal(S.continueTextOf('字'.repeat(S.CONTINUE_MAX)).length, S.CONTINUE_MAX, '剛好等於上限不截斷');
  assert.equal(S.continueTextOf('字'.repeat(S.CONTINUE_MAX - 1) + String.fromCodePoint(0x1f600)), '字'.repeat(S.CONTINUE_MAX - 1), '上限落在代理字元對中間：整個字元不要');
  assert.equal(S.continuePreview('看一下' + NL + NL + '  進度'), '看一下 進度');
  assert.equal(S.continuePreview('字'.repeat(40)), '字'.repeat(40));
  assert.equal(S.continuePreview('字'.repeat(41)), '字'.repeat(40) + '…');
  assert.equal(S.continuePreview(String.fromCodePoint(0x1f600).repeat(41)), String.fromCodePoint(0x1f600).repeat(40) + '…', '以字元算、不切開代理字元對');
  assert.equal(S.continueTitle({ text: '做完了嗎' }), '繼續：送出「做完了嗎」');
  assert.equal(S.continueTitle({ running: true, steers: true, text: '做完了嗎' }), '繼續：把「做完了嗎」插入這一輪');
  assert.equal(S.continueTitle({ text: '  ' }), '繼續：送出「進度如何呢如果還沒做完就繼續」', '空的用預設');
});

test('enterSends：手機、平板的 Enter 換行，其他依設定', () => {
  const E = (o) => S.enterSends(o);
  // 桌面、開著 Enter 送出
  assert.equal(E({ enterToSend: true }), true);
  assert.equal(E({}), true, '沒有設定當開著');
  assert.equal(E({ enterToSend: true, shiftKey: true }), false, 'Shift+Enter 換行');
  assert.equal(E({ enterToSend: true, ctrlKey: true }), true);
  assert.equal(E({ enterToSend: true, altKey: true }), false);
  // 桌面、關掉 Enter 送出
  assert.equal(E({ enterToSend: false }), false);
  assert.equal(E({ enterToSend: false, ctrlKey: true }), true);
  assert.equal(E({ enterToSend: false, ctrlKey: true, shiftKey: true }), false);
  assert.equal(E({ enterToSend: false, metaKey: true }), false, '桌面照舊只認 Ctrl');
  // 手機、平板：不管設定，Enter 一律換行
  assert.equal(E({ handheld: true, enterToSend: true }), false);
  assert.equal(E({ handheld: true, enterToSend: false }), false);
  assert.equal(E({ handheld: true }), false);
  assert.equal(E({ handheld: true, ctrlKey: true }), true, '實體鍵盤的 Ctrl+Enter 照樣送出');
  assert.equal(E({ handheld: true, metaKey: true }), true, 'iPad 實體鍵盤的 ⌘+Enter');
  assert.equal(E({ handheld: true, metaKey: true, shiftKey: true }), false);
  assert.equal(E({ handheld: true, ctrlKey: true, altKey: true }), false);
});

test('sendrules.js 沒有 import、沒有 HTML 寫入點', () => {
  assert.ok(!/^\s*import\s/m.test(source));
  assert.ok(!/innerHTML|outerHTML|insertAdjacentHTML|document\.write|setHtml|patchHtml/.test(source));
});
