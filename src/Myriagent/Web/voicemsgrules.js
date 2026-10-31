// 輸入區語音訊息的純規則（麥克風鈕，voicemsg.js；web-protocol 的 voiceMessage）：沒有 import、不碰 DOM，以 tools/ui-probe/voicemsgrules.test.mjs 測試。
// 錄音：錄好之後交給這台（桌面）或對話所在的電腦（瀏覽器版、桌面操作其他電腦的對話），由那台以本機的 faster-whisper 轉成逐字稿、
// 連錄音檔交給代理，代理先用文字複誦使用者說的內容再照做。格式、檔名與上限和電腦端的 VoiceMessageRules 相同。
// 手機與平板的瀏覽器版（2026-10-24 使用者要求）改用系統的語音辨識（瀏覽器的 Web Speech API：Android 是 Google 的語音服務、iPhone／iPad
// 是 Apple 的聽寫），說的話即時打進輸入框、改好再送；瀏覽器沒有語音辨識、或系統不讓瀏覽器用時才錄音。桌面不用（WebView2 沒有語音辨識），
// 也不呼叫 Windows 的語音輸入

/** 一則最長幾秒：到了自動停止並送出 */
export const MAX_SECONDS = 300;
/** 比這個短的當成誤按（不送） */
export const MIN_MS = 600;
/** 錄音檔上限（位元組；電腦端同） */
export const MAX_BYTES = 20 * 1024 * 1024;
/** 錄音的位元率（Opus 32 kbps：5 分鐘約 1.2 MB，語音夠用） */
export const BITS_PER_SECOND = 32000;
/** 依序試的錄音格式：Chromium（桌面 WebView2、Android）是 WebM／Opus，Safari（iOS）是 MP4／AAC，Firefox 是 Ogg */
export const MIME_CANDIDATES = Object.freeze(['audio/webm;codecs=opus', 'audio/webm', 'audio/mp4', 'audio/ogg;codecs=opus']);
/** 錄音檔名的前綴（電腦以它認出是語音訊息的錄音） */
export const FILE_PREFIX = '語音訊息_';

/** 第一個瀏覽器支援的格式；都不支援回空字串（交給瀏覽器的預設） */
export function pickMime(isSupported) {
  if (typeof isSupported !== 'function') return '';
  for (const m of MIME_CANDIDATES) {
    try {
      if (isSupported(m)) return m;
    } catch {
      // 不支援的格式丟例外的瀏覽器：當成不支援
    }
  }
  return '';
}

/** 錄音格式 → 副檔名（同電腦端：WebM 存成 .weba，不被當成影片）；不認得回 null */
export function extensionFor(mime) {
  const type = (typeof mime === 'string' ? mime : '').split(';')[0].trim().toLowerCase();
  switch (type) {
    case 'audio/webm': case 'video/webm': return '.weba';
    case 'audio/mp4': case 'audio/x-m4a': case 'audio/aac': case 'video/mp4': return '.m4a';
    case 'audio/ogg': case 'audio/opus': return '.ogg';
    case 'audio/wav': case 'audio/x-wav': case 'audio/wave': return '.wav';
    default: return null;
  }
}

const pad = (n) => String(n).padStart(2, '0');

/** 錄音檔名：語音訊息_yyyyMMdd_HHmmss.副檔名（本機時間） */
export function voiceFileName(date, ext) {
  const d = date instanceof Date ? date : new Date(date);
  return FILE_PREFIX + d.getFullYear() + pad(d.getMonth() + 1) + pad(d.getDate()) + '_' + pad(d.getHours()) + pad(d.getMinutes()) + pad(d.getSeconds()) + ext;
}

/**
 * 麥克風鈕怎麼做：'dictation'＝系統的語音辨識打進輸入框（手機與平板的瀏覽器版，dictation 由呼叫端依 isHub、有沒有語音辨識、
 * isHandheld 決定）；'remote'＝錄音、先上傳再送 voiceMessage {uploads}（瀏覽器版、桌面操作其他電腦的對話）；'local'＝桌面這台的對話，
 * 錄音以 dataUrl 直接交給宿主（init 的 voiceInput 是 true 才有）；null＝不顯示（瀏覽器不能錄音，或這台沒有辨識程序）
 */
export function voiceMode({ supported, remote, voiceInput, dictation } = {}) {
  if (dictation === true) return 'dictation';
  if (!supported) return null;
  if (remote) return 'remote';
  return voiceInput === true ? 'local' : null;
}

/** 系統語音辨識的語言 */
export const DICTATION_LANG = 'zh-TW';

/**
 * 是不是手機或平板（用系統的語音辨識；app.js 也拿來讓平板的側欄帳戶區預設收起）：主要的指標是觸控（pointer: coarse），或使用者代理是手機、平板——iPad 接了觸控板時主要指標
 * 變成精細的，iPadOS 的 Safari 又自稱 Macintosh，以觸控點數認出來。觸控筆電、桌機（主要指標是滑鼠）不算
 */
export function isHandheld({ coarsePointer, userAgent, maxTouchPoints } = {}) {
  if (coarsePointer === true) return true;
  const ua = typeof userAgent === 'string' ? userAgent : '';
  if (/Android|iPhone|iPad|iPod|Mobile/i.test(ua)) return true;
  return /Macintosh/.test(ua) && Number(maxTouchPoints) > 1;
}

/**
 * 是不是 iPhone、iPad：使用者代理是 iPhone、iPad、iPod，或自稱 Macintosh 但有多點觸控（iPadOS 的 Safari）。dictationUtterances 與
 * app.js 的 data-os="ios"（app.css 依它調上下緣的留白）用。不用 CSS 的 @supports (-webkit-touch-callout: none) 判斷：2026-10-30 試過，iPhone 18 的網頁版沒有套用
 */
export function isAppleTouch({ userAgent, maxTouchPoints } = {}) {
  const ua = typeof userAgent === 'string' ? userAgent : '';
  if (/iPhone|iPad|iPod/.test(ua)) return true;
  return /Macintosh/.test(ua) && Number(maxTouchPoints) > 1;
}

/**
 * 一次只聽一句、聽完以同一個辨識器自己接著聽下一句嗎（iPhone、iPad；iPadOS 的 Safari 自稱 Macintosh，以觸控點數認出來）。
 * iOS 的連續辨識（continuous）不穩，每次按麥克風都新建一個辨識器時，開 app 之後只有第一次收得到字、之後的照樣發 start 卻一個字都沒有
 * （WebKit bug 321436 的 iOS 27.2 回報：頁面沒播過任何聲音也一樣；2026-10-28 使用者回報主畫面 app 關掉語音輸入再開就再也打不出字）。
 * 在 iPhone 上修好的做法都是 continuous=false、整頁共用同一個辨識器、一句結束後稍等再 start、停下只用 stop()
 */
export function dictationUtterances({ userAgent, maxTouchPoints } = {}) {
  return isAppleTouch({ userAgent, maxTouchPoints });
}

/** 一句聽完之後隔多久接著聽下一句（毫秒）：讓 iOS 先放開這一句的錄音，馬上 start 常收不到聲音 */
export const DICTATION_RESTART_MS = 250;
/** 一句一句聽時，這麼久都沒有聽到任何字就停下（毫秒）：按了麥克風就走開，不讓麥克風一直開著 */
export const DICTATION_QUIET_MS = 30000;

/**
 * 一句一句聽時，一句結束（end）之後要做什麼：'restart'＝接著聽下一句；'end'＝結束（使用者停下了、或真的出錯：沒有權限、網路、
 * 被中斷）；'quiet'＝DICTATION_QUIET_MS 都沒聽到字，停下並說明。沒說話（no-speech）不算錯，照樣接著聽
 */
export function dictationNext({ stopped, error, quietMs } = {}) {
  if (stopped === true) return 'end';
  if (error && error !== 'no-speech') return 'end';
  return (Number(quietMs) || 0) < DICTATION_QUIET_MS ? 'restart' : 'quiet';
}

const WORD_END = /[A-Za-z0-9]$/;
const WORD_START = /^[A-Za-z0-9]/;

/**
 * 辨識出的文字接到游標處（before＝游標前、after＝游標後的原文）：前後都是英數時補一個空白，中文不補。
 * 回新的內容與游標位置（接在辨識文字後面）
 */
export function joinTranscript(before, text, after) {
  const b = String(before || '');
  const a = String(after || '');
  const t = String(text || '');
  if (!t) return { value: b + a, caret: b.length };
  const mid = (WORD_END.test(b) && WORD_START.test(t) ? ' ' : '') + t + (WORD_END.test(t) && WORD_START.test(a) ? ' ' : '');
  return { value: b + mid + a, caret: b.length + mid.length };
}

/** 比對用的字（去掉空白、標點與符號）：辨識器重送同一段時常只差標點或空白 */
function stem(s) {
  return String(s || '').replace(/[\s\p{P}\p{S}]/gu, '');
}

function commonPrefix(a, b) {
  let i = 0;
  while (i < a.length && i < b.length && a[i] === b[i]) i++;
  return i;
}

/** 兩段接起來：前後都是英數時補一個空白（同 joinTranscript），中文不補 */
function glue(a, b) {
  if (!a) return b;
  if (!b) return a;
  return a + (WORD_END.test(a) && WORD_START.test(b) ? ' ' : '') + b;
}

/**
 * iPhone／iPad 的語音辨識有時把同一段話在一個結果裡給兩次（「幫我整理報表幫我整理報表」，Apple 開發者論壇 thread 694847）：收成一次。
 * 只收前後兩半一字不差、一半至少 4 個字而且不是同一個字重複的（「哈哈哈哈」「謝謝謝謝」這類照原樣）
 */
export function undouble(s) {
  const t = String(s || '');
  const m = /^(.+?)\s*\1$/su.exec(t);
  if (!m) return t;
  const half = stem(m[1]);
  return half.length >= 4 && new Set(half).size >= 2 ? m[1] : t;
}

/**
 * 一次辨識事件的全部結果（連續辨識時是開始以來的每一段）：確定的文字與還在變的文字分開接起來。Android 的 Chrome 連續辨識時會把
 * 同一段越說越長的結果各給一個確定的（後一個以前一個開頭），只留最長的那個，不重複打字。iPhone／iPad 會把確定的那段再送一次、
 * 或最後給一個從頭到尾的確定結果、或還在變的結果又從確定的那段開頭（2026-10-27 使用者回報有些部分重複）：重送的不再接上去
 * （一個字的照接：「好」「好」可能真的說了兩次）
 */
export function collectResults(results) {
  let finals = [];
  let interim = '';
  for (const r of Array.from(results || [])) {
    const alt = r && r[0];
    const s = undouble(alt && typeof alt.transcript === 'string' ? alt.transcript : '');
    if (r && r.isFinal) {
      if (!s) continue;
      const prev = finals.length ? finals[finals.length - 1] : '';
      const joined = finals.join('');
      if (stem(s).length >= 2 && (s === prev || joined.endsWith(s))) continue; // 重送同一段
      if (joined && s.length > joined.length && s.startsWith(joined)) finals = [s]; // 從頭到尾的確定結果
      else if (prev && s.length > prev.length && s.startsWith(prev)) finals[finals.length - 1] = s;
      else finals.push(s);
    } else {
      interim += s;
    }
  }
  const done = finals.join('');
  if (done && interim) {
    if (interim.startsWith(done)) interim = interim.slice(done.length); // 還在變的從確定的那段開頭
    else if (stem(interim).length >= 2 && done.endsWith(interim)) interim = ''; // 確定之後又以還在變的送一次
  }
  return { finals: done, interim };
}

/** 一次聽寫（一個辨識器）開始時的狀態：kept＝辨識器已經丟掉、要留著的前幾段；last＝上一次事件的文字 */
export function dictationStart() {
  return { kept: [], last: '' };
}

/**
 * 辨識器把前面的字整個丟掉、從新的一段重新開始了嗎（iPhone／iPad 的連續辨識只有一個越說越長的結果，停頓之後會清掉重來——
 * iOS 18 起的 SFSpeechRecognizer，Apple 開發者論壇 thread 770278；2026-10-27 使用者回報有些部分被截斷）：這次的明顯比上次短、
 * 而且開頭相同的不到上次的一半。只是改了幾個字（越說越長、改錯字、補標點）不算
 */
export function dictationReset(last, cur) {
  const a = stem(last);
  const b = stem(cur);
  if (a.length < 2 || b.length >= a.length) return false;
  return commonPrefix(a, b) * 2 < a.length;
}

/**
 * 辨識器的一次 result 事件 → { state（下一次用）, text（這次聽寫到目前為止的全部文字）, interim（還在變的那段，預覽列用）}。
 * 辨識器丟掉的前幾段留在 kept、接在前面（dictationReset）；之後它又把那幾段整個送回來（停下時給從頭到尾的結果）就不再另外留。
 * 沒有任何字的事件不改（不把已經打進去的字清掉）
 */
export function dictationStep(state, results) {
  const st = state && Array.isArray(state.kept) && typeof state.last === 'string' ? state : dictationStart();
  const { finals, interim } = collectResults(results);
  const cur = finals + interim;
  if (!stem(cur)) return { state: st, text: glue(st.kept.reduce(glue, ''), st.last), interim: '' };
  let kept = st.kept.slice();
  if (dictationReset(st.last, cur)) kept.push(st.last);
  const now = stem(cur);
  for (let i = 0; i < kept.length; i++) {
    const tail = stem(kept.slice(i).join(''));
    if (tail.length >= 2 && now.startsWith(tail)) {
      kept = kept.slice(0, i);
      break;
    }
  }
  return { state: { kept, last: cur }, text: glue(kept.reduce(glue, ''), cur), interim };
}

/** 系統不讓瀏覽器用語音辨識（iPhone／iPad 沒開聽寫等）或不支援中文：這一頁改成錄音（SpeechRecognitionErrorEvent.error） */
export function dictationFallback(code) {
  return code === 'service-not-allowed' || code === 'language-not-supported';
}

/** 錯誤代碼的說明；不用提示的（自己停下、沒說話）回 null */
export function dictationError(code) {
  switch (code) {
    case 'not-allowed':
      return '沒有麥克風的權限：請在瀏覽器（網址列旁的網站設定）允許這個網站使用麥克風';
    case 'service-not-allowed':
    case 'language-not-supported':
      return '這個瀏覽器不能用系統的語音辨識（iPhone／iPad 請確認設定裡的聽寫已開啟），麥克風改成錄音交給代理：再按一下開始錄音';
    case 'audio-capture':
      return '找不到麥克風';
    case 'network':
      return '語音辨識需要網路，請稍後再試';
    case 'no-speech':
    case 'aborted':
      return null;
    default:
      return '語音輸入發生錯誤（' + String(code || '不明') + '）';
  }
}

/**
 * 開始聽之前的等待（毫秒）：瀏覽器同時只能有一個辨識器在聽，前一個還在收尾（按停之後等最後一段、被中止）時就開始下一個，
 * Android 的語音服務還在忙、iPhone 的新一個收不到聲音——按了講話沒反應、換個對話過一下又好了（2026-10-24 使用者回報）。
 * 前一個等 abortAfterMs 還沒結束就中止它，giveUpMs 還沒結束就不管它；剛停下語音通知時至少等 speechSettleMs、等它不再出聲。
 * 全部在點按之後 1 秒內（iPhone 的 Safari 只把「使用者按的」轉給 1 秒內的計時器），每 stepMs 看一次
 */
export const DICTATION_WAIT = Object.freeze({ abortAfterMs: 450, giveUpMs: 800, speechSettleMs: 300, stepMs: 40 });

/**
 * iPhone／iPad 一句一句聽時的等待：整頁共用一個辨識器，前一次按停的還沒 end 就開始，只能另外新建一個——iOS 新建的常收不到聲音
 * （dictationUtterances）。所以耐心等它結束（stop 之後最後一段要等辨識完；DICTATION_END_GRACE_MS 到了才中止它）
 */
export const DICTATION_WAIT_UTTERANCES = Object.freeze({ abortAfterMs: 3200, giveUpMs: 3600, speechSettleMs: 300, stepMs: 40 });

/**
 * 開始聽之前這一刻要做什麼：waitedMs＝按下之後等了多久、closing＝這一頁還沒結束的辨識器數、speechStopped＝按下時停下了正在念的語音通知、
 * speaking＝語音合成還在出聲。回 'start'（現在開始）、'wait'（再等一下）、'abort'（前一個等太久：中止它再等）
 */
export function dictationWaitStep({ waitedMs, closing, speechStopped, speaking, patient } = {}) {
  const W = patient === true ? DICTATION_WAIT_UTTERANCES : DICTATION_WAIT;
  const w = Number(waitedMs) || 0;
  if (w >= W.giveUpMs) return 'start';
  const n = Number(closing) || 0;
  const speech = speechStopped === true && (w < W.speechSettleMs || speaking === true);
  if (n <= 0 && !speech) return 'start';
  return n > 0 && w >= W.abortAfterMs ? 'abort' : 'wait';
}

/**
 * iPhone／iPad 聽寫的做法（長按麥克風鈕的診斷記錄裡可以換，存在這個瀏覽器；沒選過是 'gumHold'）：
 * 'gumHold'＝預設：開始前先以 getUserMedia 開麥克風、接上 Web Audio 讀音量（AudioContext＋AnalyserNode），開著到聽寫結束；
 * 'shared'＝只用語音辨識（共用辨識器）；'fresh'＝每次新建辨識器；'ttsFresh'＝先以語音合成念一聲、念完新建（WebKit bug 321436 的
 * iOS 27.0 回報）；'sessionFlip'＝先把 navigator.audioSession 切到 playback 再切回 auto、再新建；'gumPrime'＝開始前開一下麥克風
 * （0.6 秒，讀音量）就關、再開始。
 * 實機（2026-10-28，iOS 27.0.1 主畫面 app，使用者以診斷記錄回報）：只用語音辨識、只開麥克風不接 Web Audio，都只有開 app 後的
 * 第一次收得到字（之後 start 幾十毫秒就到、一直沒有 speechstart、停止時 aborted「No speech detected」）；開麥克風又接上 Web Audio
 * 讀音量之後，第二次、第三次都收得到字（使用者確認正常）。看起來要有東西真的在用麥克風的聲音，iOS 才讓語音辨識也收得到
 */
export const DIAG_MODES = Object.freeze(['gumHold', 'shared', 'fresh', 'ttsFresh', 'sessionFlip', 'gumPrime']);
export const DIAG_MODE_LABELS = Object.freeze({
  gumHold: '預設（先開麥克風）', shared: '只用語音辨識', fresh: '每次新建', ttsFresh: '先念一聲再新建', sessionFlip: '切換音訊模式再新建', gumPrime: '先開一下麥克風',
});

/** 診斷記錄裡的音量（方均根 → dBFS，整數）；沒有聲音是 -∞ */
export function diagDb(rms) {
  return rms > 0 ? Math.round(20 * Math.log10(rms)) + ' dBFS' : '-∞ dBFS';
}
export function diagMode(v) {
  return DIAG_MODES.includes(v) ? v : 'gumHold';
}

/** 診斷記錄裡的文字：只留前 max 個字、換行換成空白 */
export function diagText(s, max = 16) {
  const t = String(s == null ? '' : s).replace(/\s+/g, ' ');
  return t.length > max ? t.slice(0, max) + '…' : t;
}

/** 開始之後多久還沒有任何動靜（start、audiostart、結果、錯誤、結束）就當成沒開始：中止並說明，不讓按鈕一直停在「正在聽」 */
export const DICTATION_START_TIMEOUT_MS = 8000;
/** 停下（stop、abort）之後多久還沒收到 end 就當成結束了（辨識器卡住）：不再擋下一次開始，語音通知照念 */
export const DICTATION_END_GRACE_MS = 3000;
/** 開始了卻一直沒有動靜時的說明 */
export const DICTATION_NO_START = '語音辨識沒有回應：請再按一下麥克風（一直這樣請重新整理頁面）';

/**
 * 開始了（start、audiostart 都到了）卻一直沒有任何字、也沒有錯誤或結束：iPhone／iPad 的已知問題（WebKit bug 321436）——頁面播放過聲音
 * （語音通知的語音合成、影片）之後，下一次語音辨識照樣發 start、麥克風燈也亮，之後卻再也不給結果，也不發錯誤或結束，只有重新載入才恢復。
 * 2026-10-27 使用者回報第一次正常、第二次「正在聽」卻打不出字。這麼久沒有字就停下並說明
 */
export const DICTATION_DEAF_MS = 9000;

/**
 * 收不到字（DICTATION_DEAF_MS）時的說明。heardBefore＝這一頁之前的聽寫收得到字：辨識器之後壞掉了（上面的 iOS 問題），這一頁改成錄音
 * （錄音走麥克風串流、不經語音辨識，照樣能用），重新整理頁面才再用語音打字；之前沒收到過字的可能只是沒說話或麥克風的問題，照舊
 */
export function dictationDeafNotice(heardBefore) {
  if (heardBefore === true) {
    return '語音辨識收不到聲音了（iPhone／iPad 播放過語音通知或其他聲音之後常這樣，是 iOS 的已知問題）。這一頁的麥克風先改成錄音交給代理：'
      + '再按一下開始錄音、說完再按一下送出；要再用語音打字，請重新整理頁面';
  }
  return '沒有收到任何文字：請按一下麥克風再說一次（一直這樣請重新整理頁面）';
}

/**
 * 聽寫自己結束（不是按停、打字、送出、換別的輸入區）時給使用者的說明，按了講話沒反應時至少知道發生了什麼：被中斷（aborted：麥克風被
 * 其他分頁或程式拿去用）一律說；沒有收到任何字（no-speech，或沒有錯誤就結束了）說沒聽到。自己停下的、onerror 已經提示過的（noticed）、
 * 有收到字而正常結束的（Android 說完一段就結束）回 null。
 * quiet＝一句一句聽時太久沒聽到字而停下（dictationNext 的 'quiet'）；deaf＝其間換過收不到聲音的辨識器（iOS 的已知問題）
 */
export function dictationEndNotice({ stopped, noticed, heard, error, quiet, deaf } = {}) {
  if (stopped === true || noticed === true) return null;
  if (error === 'aborted') return '語音輸入被中斷了（麥克風可能被其他分頁或程式拿去用）：請再按一下麥克風';
  if (quiet === true && deaf === true && heard !== true) {
    return '語音辨識收不到聲音（iPhone／iPad 的已知問題）：請再按一下麥克風；一直這樣請把 app 從多工畫面滑掉再重新開啟';
  }
  if (quiet === true) return '一陣子沒有聽到聲音，語音輸入先停下了：要繼續說請再按一下麥克風';
  if (heard === true) return null;
  return '沒有聽到聲音：請按一下麥克風再說一次';
}

/**
 * 輸入框的 input（isTrusted）是不是自己打字（聽寫就此停下、之後的結果不再寫回）：內容和聽寫最後寫進去的（還沒寫過就是開始聽時的內容）
 * 一樣的不算——Android 的鍵盤收尾組字時可能把同樣的字原樣交回、也發 input。不知道該是什麼（還在等前一個辨識器結束）時一律算
 */
export function dictationTyped(value, expected) {
  return typeof expected !== 'string' || String(value) !== expected;
}

/** 錄了多久（m:ss） */
export function formatElapsed(ms) {
  const s = Math.max(0, Math.floor((Number(ms) || 0) / 1000));
  return Math.floor(s / 60) + ':' + pad(s % 60);
}

/** 錄好的能不能送：太短（誤按）、空的、太大回說明；可以送回 null */
export function checkRecording(ms, bytes) {
  if (!(bytes > 0) || (Number(ms) || 0) < MIN_MS) return '錄音太短：按一下開始說話，說完再按一下送出';
  if (bytes > MAX_BYTES) return '錄音超過 20 MB，請分段說';
  return null;
}

/** 開麥克風失敗的說明（getUserMedia 的錯誤名稱） */
export function micError(name) {
  switch (name) {
    case 'NotAllowedError': case 'SecurityError': case 'PermissionDeniedError':
      return '沒有麥克風的權限：請在瀏覽器（網址列左邊）允許這個網站使用麥克風';
    case 'NotFoundError': case 'DevicesNotFoundError': case 'OverconstrainedError':
      return '找不到麥克風';
    case 'NotReadableError': case 'TrackStartError': case 'AbortError':
      return '麥克風被其他程式占用，或沒有辦法開啟';
    default:
      return '麥克風沒有開啟' + (name ? '（' + name + '）' : '');
  }
}

/** 按鈕的說明（滑過、無障礙名稱）：閒置、錄音中（含時間）、送出中、聽寫中、停用的原因；dictation＝系統語音辨識的模式 */
export function micTitle(st, { elapsedMs = 0, reason = null, dictation = false } = {}) {
  if (st === 'dictating') return '正在聽：說的話會打進輸入框，再按一下停止';
  if (st === 'recording') return '錄音中 ' + formatElapsed(elapsedMs) + '：再按一下送出（Esc 取消）';
  if (st === 'sending') return '正在送出語音訊息…';
  if (dictation) return reason ? '語音輸入：' + reason : '語音輸入：按一下開始說話，說的話會打進輸入框（系統的語音辨識），改好再送出';
  if (reason) return '語音訊息：' + reason;
  return '語音訊息：按一下開始錄音，說完再按一下送出（代理會先用文字複誦你說的內容）';
}

/** 錄音音量表：幾格、最小聽得到的音量（dBFS，以下是 0 格）、滿格的音量（再大就是快要破音） */
export const LEVEL_CELLS = 5;
export const LEVEL_FLOOR_DB = -55;
export const LEVEL_FULL_DB = -8;

/**
 * 一段取樣（-1～1）的方均根音量換成亮幾格（麥克風實際收到的音量；voicemsg.js 每約 60ms 從 AnalyserNode 讀一次）：
 * 以 dBFS 線性分格，LEVEL_FLOOR_DB 以下 0 格、LEVEL_FULL_DB 以上滿格；有聲音（高於下限）至少 1 格
 */
export function levelCells(rms) {
  if (!(rms > 0)) return 0;
  const db = 20 * Math.log10(rms);
  if (db <= LEVEL_FLOOR_DB) return 0;
  const frac = (db - LEVEL_FLOOR_DB) / (LEVEL_FULL_DB - LEVEL_FLOOR_DB);
  return Math.max(1, Math.min(LEVEL_CELLS, Math.ceil(frac * LEVEL_CELLS)));
}

/** 方均根：一段取樣（Float32Array 或陣列）；空的回 0 */
export function rmsOf(samples) {
  const n = samples ? samples.length : 0;
  if (!n) return 0;
  let sum = 0;
  for (let i = 0; i < n; i++) sum += samples[i] * samples[i];
  return Math.sqrt(sum / n);
}

/** 音量表的下一格數：變大立刻跟上，變小每次最多掉一格（看得出剛才的音量，不會一閃就沒） */
export function meterStep(prev, next) {
  const p = Number.isFinite(prev) ? prev : 0;
  return next >= p ? next : Math.max(next, p - 1);
}

/** 錄音波形最多幾格（宿主 VoiceMessageRules.MaxWaveformBars 是 64） */
export const WAVE_BARS = 40;

/**
 * 錄音期間每次讀到的音量（方均根，音量表那一份）壓成波形：平均分成最多 WAVE_BARS 段、每段取最大的，以 dBFS 換成一格一個 0～9 的數字
 * （同 levelCells 的下限與滿格）；沒有讀數或全是安靜時回 null（不送）
 */
export function waveOf(levels, bars = WAVE_BARS) {
  const n = Array.isArray(levels) ? levels.length : 0;
  if (!n) return null;
  const k = Math.min(bars, n);
  let out = '';
  for (let b = 0; b < k; b++) {
    let peak = 0;
    for (let i = Math.floor((b * n) / k); i < Math.floor(((b + 1) * n) / k); i++) peak = Math.max(peak, levels[i] || 0);
    const db = peak > 0 ? 20 * Math.log10(peak) : -Infinity;
    out += db <= LEVEL_FLOOR_DB ? '0' : String(Math.max(1, Math.min(9, Math.round(((db - LEVEL_FLOOR_DB) / (LEVEL_FULL_DB - LEVEL_FLOOR_DB)) * 9))));
  }
  return /[1-9]/.test(out) ? out : null;
}
