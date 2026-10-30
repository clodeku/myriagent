// Myriagent Hub 版本頁（/#releases，app 自我更新 U2）的純函式（module、沒有 import、不碰 DOM）：大小、版本狀態、更新階段、提交的顯示、撤回確認的文字，以及網址片段是不是版本頁。
// 測試：(cd tools/hub-e2e && node --test releaserules.test.mjs)（以 data: URL 匯入）。
// 狀態與階段的值以伺服器為準（AppReleaseStates、AppStatePhases）；認不得的值照原樣顯示，不猜。

const STATE_TEXT = {
  available: '可用',
  withdrawn: '已撤回',
  pruned: '已清除（檔案已刪）',
};

const PHASE_TEXT = {
  idle: '沒有進行中的更新',
  downloading: '下載中',
  staged: '已下載，等待套用',
  waiting: '等待閒置後套用',
  draining: '等待執行中的工作結束',
  applying: '套用中',
  rolledBack: '新版啟動失敗，已退回',
  blocked: '無法更新（缺執行階段、空間不足或未啟用）',
};

const UNITS = ['KB', 'MB', 'GB', 'TB'];

/** 位元組數的顯示：1024 以下是「n B」，以上以 1024 進位、小數一位（4096 → '4.0 KB'）；不是有限的非負數回 '—' */
export function formatBytes(n) {
  if (typeof n !== 'number' || !Number.isFinite(n) || n < 0) return '—';
  if (n < 1024) return `${Math.trunc(n)} B`;
  let value = n / 1024;
  let unit = 0;
  while (value >= 1024 && unit < UNITS.length - 1) {
    value /= 1024;
    unit += 1;
  }
  return `${value.toFixed(1)} ${UNITS[unit]}`;
}

/** 版本的狀態（app_releases.state）：available／withdrawn／pruned 的說明；其他字串照原樣括起來，不是字串回 '—' */
export function releaseStateText(state) {
  if (typeof state !== 'string') return '—';
  return Object.hasOwn(STATE_TEXT, state) ? STATE_TEXT[state] : `不明的狀態（${state}）`;
}

/** 電腦的更新階段（app.state 的 phase，大小寫要相符）：八個值的說明；其他字串照原樣括起來，不是字串回 '—' */
export function appPhaseText(phase) {
  if (typeof phase !== 'string') return '—';
  return Object.hasOwn(PHASE_TEXT, phase) ? PHASE_TEXT[phase] : `不明的狀態（${phase}）`;
}

/** 提交雜湊的顯示：前 7 碼；空字串或不是字串回 '—' */
export function shortCommit(commit) {
  if (typeof commit !== 'string' || commit === '') return '—';
  return commit.slice(0, 7);
}

/**
 * 撤回前確認框的文字（latest 是版本頁目前畫出的「目前提供」的版本）：撤回的正好是目前提供的版本時，伺服器改提供前一個可用的版本（沒有就不提供更新）；
 * 撤回較舊的可用版本時目前提供的版本不變。兩種都提醒已經備妥的電腦仍可能套用、撤回不能復原、同名的版本永遠不能再收（U2 設計 D14）
 */
export function withdrawConfirmText(release, latest) {
  const effect = typeof release === 'string' && release === latest
    ? '它是目前提供的版本，改提供前一個可用的版本（沒有就不提供更新）'
    : '它不是目前提供的版本，目前提供的版本不變';
  return `撤回 ${release}？\n\n裝置不再下載這一版；${effect}。已經下載好、等著套用的電腦仍可能套用它（裝置不會降版，修正要發佈更新的版本）。`
    + '撤回之後不能復原，同名的版本也永遠不能再收。';
}

/** 網址片段（location.hash）是不是版本頁：只認 '#releases'（大小寫要相符，'#releases=x'、'#releasesx' 都不是） */
export function isReleasesHash(hash) {
  return hash === '#releases';
}
