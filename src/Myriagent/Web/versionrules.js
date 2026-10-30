// 頂部列品牌名旁的版號（純函式；module、沒有 import、不碰 DOM）：讓使用者看得出自己用的是哪一版、是不是最新的。
// - 桌面：這台 app 的版本（init.app），提示寫完整版本、build 與更新狀態（AppUpdateView）；有比這版新的版本時換色調。
// - 瀏覽器版：提供這個網頁的伺服器 build（這次載入第一則 init 的 app.build；伺服器的版本號固定，只有 build 會變）。
//   伺服器之後換了版、網頁還沒重新載入時換成警告色調（重新載入的橫幅另由 liveview.js 顯示）。
// - 版號前面是這一版的日期時間（commitAt：建置時寫進組件的提交時間，Unix 毫秒；CommitTimeRules），讓使用者知道是哪一天、哪個時候的更新。
// 回傳 null＝不顯示；否則 { prefix, time, text, title, label, tone }：prefix 是窄螢幕可以省略的前綴（沒有是 ''），
// time 是版號前面的日期時間（沒有是 ''），text 是版號，title 是滑鼠提示（多行），label 是給讀屏的一句話，tone 是 'plain'｜'update'｜'warn'。

const str = (v) => (typeof v === 'string' && v.length > 0 ? v : null);
const isObj = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);

/** 開發版的 build（ReleaseIdentity.DevBuild）：沒有提交雜湊的本機建置 */
export const DEV_BUILD = 'dev';

/** commitAt：正的有限數字才算；缺少、字串、0 一律 null（舊版的宿主或伺服器沒有這個欄位，建置時沒有 git 也是 null） */
export function normCommitAt(v) {
  return typeof v === 'number' && Number.isFinite(v) && v > 0 ? v : null;
}

const pad2 = (n) => String(n).padStart(2, '0');

/**
 * 版號前面的日期時間（使用者的當地時間）：{ short, full }；沒有時間回 null。
 * full 一律有年份（「2026/10/23 14:05」，提示、讀屏、設定頁用）；short 是版號旁的，和 now 同一年時省略年份（「10/23 14:05」）
 */
export function versionTime(commitAt, now = Date.now()) {
  const ms = normCommitAt(commitAt);
  if (ms === null) return null;
  const d = new Date(ms);
  const md = `${pad2(d.getMonth() + 1)}/${pad2(d.getDate())} ${pad2(d.getHours())}:${pad2(d.getMinutes())}`;
  const full = `${d.getFullYear()}/${md}`;
  return { short: d.getFullYear() === new Date(now).getFullYear() ? md : full, full };
}

/** 設定頁「目前版本」那一格：完整的日期時間接版本文字（「2026/10/23 14:05 · 0.2.470（ab12cd3）」）；沒有版本文字回 '' */
export function versionWithTime(text, commitAt) {
  const t = str(text);
  if (!t) return '';
  const time = versionTime(commitAt);
  return time ? `${time.full} · ${t}` : t;
}

/** 原因接在後面（沒有原因就不接） */
const withDetail = (text, detail) => (detail ? `${text}：${detail}` : text);

/**
 * 桌面的更新狀態一句話與「有沒有比這版新的版本」（AppUpdateView 的欄位都可能是 null）。
 * 新版以已備妥的版本為準（staged），其次是有的新版（available）；和目前版本相同的不算
 */
export function desktopUpdateStatus(version, update) {
  if (!isObj(update)) return { text: '', newer: null };
  const staged = str(update.staged);
  const latest = staged ?? str(update.available);
  const newer = latest && latest !== version ? latest : null;
  const detail = str(update.detail);
  switch (update.phase) {
    case 'checking': return { text: '正在檢查更新…', newer };
    case 'downloading': return { text: newer ? `有新版 ${newer}，下載中` : '正在下載更新', newer };
    case 'staged': return { text: newer ? `新版 ${newer} 已下載，重新啟動後套用` : '新版已下載，重新啟動後套用', newer };
    case 'draining':
    case 'restarting': return { text: newer ? `正在套用新版 ${newer}` : '正在套用更新', newer };
    case 'installing': return { text: '正在安裝到這台電腦', newer };
    case 'blocked': return { text: withDetail(newer ? `有新版 ${newer}，無法安裝` : '無法更新', detail), newer };
    case 'failed': return { text: withDetail(newer ? `有新版 ${newer}，更新失敗` : '更新失敗', detail), newer };
    case 'disabled': return { text: withDetail(newer ? `有新版 ${newer}，不自動更新` : '不自動更新', detail), newer };
    default:
      if (newer) return { text: `有新版 ${newer}`, newer };
      // idle 的 detail 是檢查結果（已是最新版／沒有可用的發佈／已取消）；還沒檢查過就不說「已是最新版」
      return { text: detail ?? (update.lastCheckAt ? '已是最新版' : '還沒檢查更新'), newer: null };
  }
}

/**
 * 桌面：app 是 init.app（version、build、release、commitAt），update 是 AppUpdateView（宿主還沒送過是 null）。
 * 版本、build 與時間取同一個來源（不混用）：init.app 有版本就用它的，沒有時才整組用 AppUpdateView 的；兩邊都沒有就不顯示
 */
export function desktopVersionBadge(app, update, now = Date.now()) {
  const a = isObj(app) ? app : {};
  const u = isObj(update) ? update : {};
  const src = str(a.version) ? a : u;
  const version = str(src.version);
  if (!version) return null;
  const build = str(src.build);
  const dev = !build || build === DEV_BUILD;
  const status = desktopUpdateStatus(version, update);
  const time = versionTime(src.commitAt, now);
  const title = [`Clode Myriagent ${version}（${dev ? '開發版' : build}）`, time ? `更新於 ${time.full}` : '', status.text, '點一下開啟設定的「Clode Myriagent」']
    .filter(Boolean).join('\n');
  const label = `版本 ${version}` + (time ? `（${time.full}）` : '') + (status.newer ? `，有新版 ${status.newer}` : '');
  return { prefix: '', time: time ? time.short : '', text: version, title, label, tone: status.newer ? 'update' : 'plain' };
}

/**
 * 瀏覽器版：pageBuild、pageCommitAt 是這次載入第一則 init 的 app.build、app.commitAt（提供這個網頁的伺服器），
 * app 是最近一則 init 的 app（目前連著的伺服器）。時間跟著顯示的 build（第一則沒有 build 時兩個都用目前的）。
 * 兩個 build 都沒有就不顯示；不同時（含從有變成沒有：回滾到沒有這個欄位的舊伺服器）＝伺服器已換版、網頁還沒重新載入
 */
export function hubVersionBadge(pageBuild, app, pageCommitAt = null, now = Date.now()) {
  const a = isObj(app) ? app : {};
  const page = str(pageBuild);
  const current = str(a.build);
  if (!page && !current) return null;
  const shown = page ?? current;
  const server = str(a.version);
  const changed = page !== current;
  const time = versionTime(page ? pageCommitAt : a.commitAt, now);
  const at = (t) => (t ? `（${t.full}）` : '');
  const lines = changed
    ? [`這個網頁是伺服器 build ${shown}${at(time)} 提供的`,
      current ? `伺服器已換成 build ${current}${at(versionTime(a.commitAt, now))}，重新載入以取得對應的網頁` : '伺服器已換版，重新載入以取得對應的網頁']
    : [`Myriagent Hub ${server ? server + '（' + shown + '）' : 'build ' + shown}`, time ? `更新於 ${time.full}` : '', '這個網頁與伺服器是同一個版本'];
  lines.push('點一下開啟「版本與更新」：目前提供的桌面版與各電腦的版本');
  const label = `網頁版 build ${shown}` + at(time) + (changed ? '，伺服器已換版' : '');
  return { prefix: '網頁版', time: time ? time.short : '', text: shown, title: lines.filter(Boolean).join('\n'), label, tone: changed ? 'warn' : 'plain' };
}
