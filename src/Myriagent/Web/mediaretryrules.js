// 影片與圖片載入失敗之後的自動重試（純規則；DOM 的部分在 framedvideo.js 與 mediaretry.js）。
// 起因（2026-11-14 檢查記錄）：瀏覽器版的影片載入失敗之後就一直是壞的，要重新整理頁面才播得了——
//   伺服器部署重啟的那二三十秒裡載入的影片（每次部署都有一批「video 載入失敗」）；
//   iPhone 選照片上傳時整頁十幾個影片同時出 error（頁面被系統的選取畫面蓋住，回來之後檔案明明都在、那台也在線）。
// 做法：失敗之後接著重試幾次（頁面看不到時等看得到才試）；都不行才算失敗、顯示說明，之後有外面的訊號
// （頁面回到前景、瀏覽器重新連上伺服器、電腦上下線）再試幾次。次數有上限：真的壞掉的檔不會一直重來。
// 沒有 import（live-rules.test.mjs、mediaretryrules.test.mjs 直接匯入）。

/** 失敗之後接著重試的等待（毫秒）：共三次，前後約 18 秒 */
export const RETRY_DELAYS_MS = [1500, 5000, 12000];

/** 接著重試都失敗之後，靠外面的訊號最多再試幾次 */
export const EXTRA_RETRIES = 4;

/** 靠訊號的重試之間至少隔多久（訊號可能一下子來好幾個） */
export const EXTRA_MIN_GAP_MS = 8000;

/** 離上一次失敗超過這麼久的失敗算新的一回（次數重新算）：同一個元素不會在短時間內無止盡地重來 */
export const EPISODE_RESET_MS = 2 * 60 * 1000;

/** 一個媒體元素的重試狀態 */
export function newRetryState() {
  return { attempt: 0, extra: 0, failed: false, lastErrorAt: 0, lastTryAt: 0 };
}

/**
 * 載入失敗（error 事件）時接下來怎麼做：
 *   { kind: 'retry', delay, first }  過 delay 毫秒再載入一次（first＝這一回的第一次失敗：回報一次）
 *   { kind: 'failed', first }        接著重試的次數用完了：顯示失敗（first＝這一回第一次到這裡：回報、顯示說明），之後只靠 onSignal
 */
export function onMediaError(state, now) {
  if (state.lastErrorAt && now - state.lastErrorAt > EPISODE_RESET_MS) {
    state.attempt = 0;
    state.extra = 0;
    state.failed = false;
  }
  const first = state.lastErrorAt === 0 || (state.attempt === 0 && !state.failed);
  state.lastErrorAt = now;
  if (state.attempt < RETRY_DELAYS_MS.length) return { kind: 'retry', delay: RETRY_DELAYS_MS[state.attempt], first };
  const firstFailure = !state.failed;
  state.failed = true;
  return { kind: 'failed', first: firstFailure };
}

/** 真的動手重試（接著重試的那幾次）之前呼叫：算一次 */
export function noteRetry(state, now) {
  state.attempt++;
  state.lastTryAt = now;
}

/** 外面的訊號（頁面回到前景、瀏覽器連上伺服器、電腦上下線）：已經算失敗的要不要再試一次（要就算一次） */
export function onSignal(state, now) {
  if (!state.failed || state.extra >= EXTRA_RETRIES || now - state.lastTryAt < EXTRA_MIN_GAP_MS) return false;
  state.extra++;
  state.lastTryAt = now;
  return true;
}

/**
 * 載入成功：回「是不是從失敗救回來的」（之前顯示過失敗的說明：要收掉）。次數不歸零——
 * 讀得到開頭卻每次播到一半壞掉的檔不能因此無止盡地重來，等 EPISODE_RESET_MS 之後才重新算
 */
export function onMediaLoaded(state) {
  const recovered = state.failed;
  state.failed = false;
  return recovered;
}

const ERROR_NAMES = { 1: '中止', 2: '網路', 3: '解碼', 4: '來源不支援或載入不到' };

/**
 * 回報用的說明（寫進記錄，之後才看得出是哪一種失敗）：MediaError 的代碼與訊息（圖片沒有）、networkState、頁面當時看不看得到、
 * 瀏覽器說有沒有網路、接下來會不會重試
 */
export function describeMediaError(e) {
  const o = e || {};
  const parts = [];
  if (typeof o.code === 'number') parts.push('MediaError ' + o.code + (ERROR_NAMES[o.code] ? '（' + ERROR_NAMES[o.code] + '）' : ''));
  if (typeof o.message === 'string' && o.message.trim()) parts.push(o.message.trim().slice(0, 200));
  if (typeof o.networkState === 'number') parts.push('networkState ' + o.networkState);
  if (o.hidden === true) parts.push('頁面當時看不到');
  if (o.online === false) parts.push('瀏覽器回報沒有網路');
  if (typeof o.attempt === 'number' && o.attempt > 0) parts.push('已重試 ' + o.attempt + ' 次');
  parts.push(o.willRetry ? '會自動重試' : '不再自動重試（等頁面回到前景或重新連線）');
  return parts.join('、');
}

/** 接著重試都失敗之後，回報的訊息多帶的一段（和第一次失敗的那一則分開算節流） */
export const FINAL_NOTE = '重試後仍失敗';

/** 圖片要不要自動重試：只管對話欄裡的圖（檢視器自己有失敗的畫面），而且是真的網址（data:、blob: 重試也一樣） */
export function imageRetryable(url, inItem) {
  if (!inItem || typeof url !== 'string' || !url) return false;
  const head = url.slice(0, 5).toLowerCase();
  return head !== 'data:' && head !== 'blob:';
}
