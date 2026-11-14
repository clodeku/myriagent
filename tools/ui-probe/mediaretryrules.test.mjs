// 影片與圖片載入失敗之後的自動重試（src/Myriagent/Web/mediaretryrules.js）的純規則
import { test } from 'node:test';
import assert from 'node:assert/strict';
import * as MR from '../../src/Myriagent/Web/mediaretryrules.js';
import * as E from '../../src/Myriagent/Web/errorrules.js';

/** 照規則跑一回：每次失敗就照指示重試（重試馬上又失敗），回每一步的結果 */
function failRepeatedly(state, start, times) {
  const steps = [];
  let now = start;
  for (let i = 0; i < times; i++) {
    const step = MR.onMediaError(state, now);
    steps.push(step);
    if (step.kind !== 'retry') break;
    now += step.delay;
    MR.noteRetry(state, now);
  }
  return { steps, now };
}

test('失敗之後接著重試三次，間隔越來越長；都不行才算失敗', () => {
  const state = MR.newRetryState();
  const { steps } = failRepeatedly(state, 1000, 10);
  assert.deepEqual(steps, [
    { kind: 'retry', delay: 1500, first: true },
    { kind: 'retry', delay: 5000, first: false },
    { kind: 'retry', delay: 12000, first: false },
    { kind: 'failed', first: true },
  ]);
  assert.equal(state.failed, true);
  // 失敗之後再收到 error（外面的訊號重試又失敗）：不再排重試、也不再當成第一次
  assert.deepEqual(MR.onMediaError(state, 60_000), { kind: 'failed', first: false });
});

test('只有每一回的第一次失敗要回報（重試之間的失敗不回報）', () => {
  const state = MR.newRetryState();
  assert.equal(MR.onMediaError(state, 0).first, true);
  // 還沒動手重試又來一個 error（同一次載入的第二個事件）：算同一次
  assert.equal(MR.onMediaError(state, 10).first, true, '重複的回報由節流擋（同一則 10 分鐘一次）');
  MR.noteRetry(state, 1500);
  assert.equal(MR.onMediaError(state, 1600).first, false);
});

test('失敗之後靠外面的訊號再試：有次數上限、兩次之間要隔一段', () => {
  const state = MR.newRetryState();
  assert.equal(MR.onSignal(state, 0), false, '沒有失敗的不必試');
  const { now } = failRepeatedly(state, 0, 10);
  assert.equal(MR.onSignal(state, now + 1000), false, '剛試過：訊號一下子來好幾個時不連著試');
  let t = now + MR.EXTRA_MIN_GAP_MS;
  let tried = 0;
  for (let i = 0; i < 10; i++) {
    if (MR.onSignal(state, t)) tried++;
    t += MR.EXTRA_MIN_GAP_MS;
  }
  assert.equal(tried, MR.EXTRA_RETRIES, '真的壞掉的檔不會一直重來');
});

test('載入成功：收掉失敗的狀態；次數不歸零，隔很久之後的失敗才算新的一回', () => {
  const state = MR.newRetryState();
  const { now } = failRepeatedly(state, 0, 10);
  assert.equal(MR.onMediaLoaded(state), true, '從失敗救回來');
  assert.equal(state.failed, false);
  assert.equal(MR.onMediaLoaded(state), false);

  // 讀得到開頭、播到一半又壞掉：短時間內不重新算（不然會無止盡地重來）
  assert.deepEqual(MR.onMediaError(state, now + 5000), { kind: 'failed', first: true });
  // 隔了很久：新的一回
  assert.deepEqual(MR.onMediaError(state, now + 5000 + MR.EPISODE_RESET_MS + 1), { kind: 'retry', delay: MR.RETRY_DELAYS_MS[0], first: true });
});

test('說明：MediaError 的代碼、頁面看不看得到、會不會重試', () => {
  assert.equal(MR.describeMediaError({ code: 2, networkState: 3, hidden: true, online: true, attempt: 0, willRetry: true }),
    'MediaError 2（網路）、networkState 3、頁面當時看不到、會自動重試');
  assert.equal(MR.describeMediaError({ code: 4, message: ' Format error ', networkState: 3, hidden: false, online: false, attempt: 3, willRetry: false }),
    'MediaError 4（來源不支援或載入不到）、Format error、networkState 3、瀏覽器回報沒有網路、已重試 3 次、不再自動重試（等頁面回到前景或重新連線）');
  assert.equal(MR.describeMediaError({ code: 9 }).startsWith('MediaError 9、'), true, '不認得的代碼照寫');
  // 圖片沒有 MediaError
  assert.equal(MR.describeMediaError({ hidden: false, online: true, attempt: 0, willRetry: true }), '會自動重試');
  assert.equal(typeof MR.describeMediaError(null), 'string');
});

test('圖片：只重試對話欄裡、真的網址的圖', () => {
  assert.equal(MR.imageRetryable('https://hub.example.com/m/d_X-Y', true), true);
  assert.equal(MR.imageRetryable('https://myriagent.local/media?p=1', true), true);
  assert.equal(MR.imageRetryable('https://hub.example.com/m/d_X-Y', false), false, '檢視器的圖有自己的失敗畫面');
  assert.equal(MR.imageRetryable('data:image/png;base64,AAAA', true), false);
  assert.equal(MR.imageRetryable('BLOB:https://a/1', true), false);
  assert.equal(MR.imageRetryable('', true), false);
  assert.equal(MR.imageRetryable(null, true), false);
});

test('回報：第一次失敗與重試後仍失敗是兩則（訊息不同，各自節流），說明放在 detail', () => {
  const page = 'https://hub.example.com/app/';
  const first = E.fromMedia('video', 'https://hub.example.com/m/d_X-Y', page, 'MediaError 2（網路）、會自動重試');
  assert.deepEqual(first, { kind: 'media', message: 'video 載入失敗', source: 'https://hub.example.com/m/d_X-Y', detail: 'MediaError 2（網路）、會自動重試' });
  const final = E.fromMedia('video', 'https://hub.example.com/m/d_X-Y', page, '已重試 3 次', MR.FINAL_NOTE);
  assert.equal(final.message, 'video 載入失敗（重試後仍失敗）');
  const allow = E.createLimiter(() => 0);
  assert.equal(allow(first), true);
  assert.equal(allow(first), false, '同一則 10 分鐘內只送一次');
  assert.equal(allow(final), true, '重試後仍失敗另外算');
  // 沒給說明時和以前一樣
  assert.deepEqual(E.fromMedia('img', 'https://a/t/1', page), { kind: 'media', message: 'img 載入失敗', source: 'https://a/t/1', detail: null });
});
