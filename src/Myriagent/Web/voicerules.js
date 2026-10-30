// 語音通知的純規則（voice.js、settings.js 用；tools/ui-probe/voicerules.test.mjs）：開關、語速與音調、挑聲音、同一個瀏覽器的分頁之間去重。
// 桌面 app 以 Windows 的雅婷念（宿主的 SpeechAudioRules）；瀏覽器用這台裝置的中文語音。語速與音調兩邊同一套範圍（設定頁「語音通知」底下的滑桿）：
// 桌面存在宿主的設定（voiceSpeed、voicePitch），瀏覽器存在這個瀏覽器（localStorage）

/** 語速（總倍速，1＝原速）的範圍、間距與預設（同桌面 SpeechAudioRules 的 MinSpeed、MaxSpeed、SpeedStep、DefaultSpeed） */
export const SPEED_MIN = 0.5;
export const SPEED_MAX = 2.5;
export const SPEED_STEP = 0.1;
export const SPEED_DEFAULT = 1.5;

/** 音調（升幾個半音，負的是降；整數）的範圍與預設（同桌面 SpeechAudioRules 的 MinPitch、MaxPitch、DefaultPitch） */
export const PITCH_MIN = -6;
export const PITCH_MAX = 6;
export const PITCH_DEFAULT = 2;

/** 打開開關時念的試聽句（同桌面的 VoiceNotifyRules.EnabledSample） */
export const ENABLED_SAMPLE = '語音通知已開啟';

/** 調了語速或音調時念的試聽句（同桌面的 VoiceNotifyRules.TuningSample） */
export const TUNING_SAMPLE = '「整理報表」已完成：月報已匯出成 Excel';

/** 語速：數字（含 localStorage 的字串）限制在範圍內、取到 0.1；不是有限的數字回預設 */
export function normSpeed(v) {
  const n = typeof v === 'string' && v.trim() !== '' ? Number(v) : v;
  if (typeof n !== 'number' || !Number.isFinite(n)) return SPEED_DEFAULT;
  return Math.round(Math.min(SPEED_MAX, Math.max(SPEED_MIN, n)) * 10) / 10;
}

/** 音調：數字（含 localStorage 的字串）限制在範圍內、取到整數半音；不是有限的數字回預設 */
export function normPitch(v) {
  const n = typeof v === 'string' && v.trim() !== '' ? Number(v) : v;
  if (typeof n !== 'number' || !Number.isFinite(n)) return PITCH_DEFAULT;
  return Math.round(Math.min(PITCH_MAX, Math.max(PITCH_MIN, n))) || 0;
}

/** 設定頁顯示的語速：「1.5×」 */
export function fmtSpeed(v) {
  return normSpeed(v).toFixed(1) + '×';
}

/** 設定頁顯示的音調：「原調」「+2 半音」「-3 半音」 */
export function fmtPitch(v) {
  const p = normPitch(v);
  return p === 0 ? '原調' : (p > 0 ? '+' : '') + p + ' 半音';
}

/** Web Speech API 的 pitch（0～2，1＝原調）：升 N 個半音＝2^(N/12)，取到小數三位 */
export function webPitch(semitones) {
  return Math.round(Math.min(2, Math.max(0, 2 ** (normPitch(semitones) / 12))) * 1000) / 1000;
}

/** 同一筆事件在這段時間內只念一次（同一個瀏覽器開好幾個分頁時） */
export const SPOKEN_WINDOW_MS = 10 * 60 * 1000;

/** 最多排幾句（同桌面的 VoiceNotifyRules.MaxPending；同時完成很多個對話時，後面的不念） */
export const MAX_QUEUED = 3;

/** 記住最近念過的幾筆 */
export const SPOKEN_MAX = 50;

/** 被瀏覽器擋下（還沒點過畫面）的那一句，點了畫面之後多久內還補念 */
export const PENDING_MAX_MS = 2 * 60 * 1000;

/** localStorage 存的開關：沒存過（null）＝開，只有 'off' 是關 */
export function voiceEnabled(stored) {
  return stored !== 'off';
}

/** 語言標籤正規化：底線換成連字號、小寫（Android 的 zh_TW、iOS 的 zh-TW 都比得到） */
export function normLang(lang) {
  return String(lang || '').replace(/_/g, '-').toLowerCase();
}

/** 聲音的順位（小的優先）：臺灣華語（本機的先）、其他正體中文、其他中文；不是中文回 null */
export function voiceRank(voice) {
  if (!voice || typeof voice.lang !== 'string') return null;
  const l = normLang(voice.lang);
  if (l === 'zh-tw' || l === 'zh-hant-tw' || l === 'cmn-hant-tw' || l === 'cmn-tw') return voice.localService === false ? 1 : 0;
  if (l.startsWith('zh-hant') || l === 'zh-hk' || l === 'yue-hk') return 2;
  if (l.startsWith('zh') || l.startsWith('cmn')) return 3;
  return null;
}

/** 從 speechSynthesis.getVoices() 挑一個中文的聲音（同順位取先出現的）；沒有中文的回 null（交給瀏覽器依 lang 選） */
export function pickVoice(voices) {
  let best = null;
  let bestRank = Infinity;
  for (const v of Array.from(voices || [])) {
    const r = voiceRank(v);
    if (r !== null && r < bestRank) {
      best = v;
      bestRank = r;
    }
  }
  return best;
}

/** 這筆事件最近念過了嗎（時鐘倒退的紀錄不算） */
export function wasSpoken(list, id, now) {
  return Array.isArray(list) && list.some((e) => e && e.id === id && now >= e.at && now - e.at < SPOKEN_WINDOW_MS);
}

/** 記下這筆念過了：丟掉過期與格式不對的、最多留 SPOKEN_MAX 筆 */
export function rememberSpoken(list, id, now) {
  const kept = (Array.isArray(list) ? list : [])
    .filter((e) => e && typeof e.id === 'string' && typeof e.at === 'number' && now >= e.at && now - e.at < SPOKEN_WINDOW_MS);
  kept.push({ id, at: now });
  return kept.slice(-SPOKEN_MAX);
}

/** 去重用的鍵：對話＋伺服器的 seq（同一筆事件在每個分頁都一樣） */
export function speakKey(m) {
  return String(m && m.conversationId) + ':' + String(m && m.id);
}

/** speak 訊息要念的文字：字串、去掉頭尾空白、不是空的；不合格回 null */
export function speakText(m) {
  if (!m || typeof m.text !== 'string') return null;
  const t = m.text.trim();
  return t ? t : null;
}

/** 被擋下的那一句還要不要補念 */
export function pendingFresh(pending, now) {
  return !!pending && typeof pending.at === 'number' && now >= pending.at && now - pending.at <= PENDING_MAX_MS;
}
