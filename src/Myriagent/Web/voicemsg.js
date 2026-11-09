// 輸入區的語音訊息（麥克風鈕，composer.js；web-protocol 的 voiceMessage，2026-10-23 使用者要求）：按一下開始錄音，再按一下停止並送出，Esc 或 × 取消。
// 錄好的音不在網頁辨識：桌面這台的對話以 dataUrl 交給宿主；瀏覽器版與桌面操作其他電腦的對話先上傳（同附件的上傳），再送 voiceMessage {uploads}
// 給對話所在的電腦。那台以本機的 faster-whisper 轉成逐字稿、連錄音檔交給代理，代理先用文字複誦使用者說的內容再照做。
// 手機與平板的瀏覽器版（2026-10-24 使用者要求）改用系統的語音辨識（Web Speech API）：按一下開始聽，說的話即時打進輸入框的游標處，
// 再按一下、Esc、送出或自己打字就停，改好再送出；瀏覽器沒有語音辨識、或系統不讓瀏覽器用（dictationFallback）時這一頁改回錄音。
// iPhone／iPad 停頓後清掉重來、重送同一段的結果由 voicemsgrules.dictationStep 接好（2026-10-27 使用者回報）。
// iPhone／iPad 一次只聽一句、整頁共用同一個辨識器、一句聽完稍等再接著聽、停下只用 stop()（voicemsgrules.dictationUtterances；2026-10-28
// 使用者回報主畫面 app 關掉語音輸入再開就再也打不出字：iOS 每次新建的連續辨識器只有開 app 後的第一個收得到字，WebKit bug 321436）；
// 聽著卻 DICTATION_DEAF_MS 都沒有任何事件時換一個新的辨識器接著聽。其他手機與平板（Android）照舊連續辨識、每次新建，
// 收不到字的過 DICTATION_DEAF_MS 停下說明、這一頁改回錄音。
// 那樣改了 iPhone 照樣第二次打不出字；診斷記錄查出要開著麥克風、又接上 Web Audio 讀音量，iOS 的語音辨識之後才收得到聲音
// （voicemsgrules.DIAG_MODES 的 'gumHold'，iPhone／iPad 的預設；2026-10-28 使用者在 iOS 27.0.1 主畫面 app 確認正常）。
// 診斷記錄：長按麥克風鈕、或聽寫時預覽列的「記錄」打開，列出這一頁每次按下、每個辨識器的事件與時間、麥克風的音量，
// 可以複製或放進輸入框送出；裡面可以換做法（DIAG_MODES）。
// 不呼叫 Windows 的語音輸入。同時只有一個輸入區在錄音或聽；麥克風只在使用者按下之後開，送出、停止或取消就關掉
import { h, iconButton, setHidden } from './util.js';
import { icon } from './icons.js';
import { post, state, isHub } from './store.js';
import { toast, openDialog } from './ui.js';
import { holdSpeech, speechActive, speechPlays } from './voice.js';
import {
  BITS_PER_SECOND, MAX_SECONDS, pickMime, extensionFor, voiceFileName, voiceMode, formatElapsed, checkRecording, micError, micTitle,
  DICTATION_LANG, isHandheld, joinTranscript, dictationStart, dictationStep, dictationFallback, dictationError,
  DICTATION_WAIT, dictationWaitStep, DICTATION_START_TIMEOUT_MS, DICTATION_END_GRACE_MS, DICTATION_NO_START, dictationEndNotice, dictationTyped,
  DICTATION_DEAF_MS, dictationDeafNotice, dictationUtterances, DICTATION_RESTART_MS, dictationNext, DICTATION_QUIET_MS,
  DIAG_MODES, DIAG_MODE_LABELS, diagMode, diagText, diagDb,
  LEVEL_CELLS, levelCells, rmsOf, meterStep, waveOf,
} from './voicemsgrules.js';

const SUPPORTED = typeof navigator !== 'undefined' && !!navigator.mediaDevices && typeof navigator.mediaDevices.getUserMedia === 'function'
  && typeof window !== 'undefined' && typeof window.MediaRecorder === 'function';
const Recognition = typeof window !== 'undefined' ? (window.SpeechRecognition || window.webkitSpeechRecognition || null) : null;
const COARSE = typeof window !== 'undefined' && typeof window.matchMedia === 'function' ? window.matchMedia('(pointer: coarse)') : null;
/** 這一頁的系統語音辨識不能用（系統不讓瀏覽器用、不支援中文、iOS 的辨識器收不到聲音了）：麥克風改回錄音 */
let dictationOff = false;
/** 這一頁的聽寫收到過字（之後一直收不到字就是辨識器壞掉了，voicemsgrules.dictationDeafNotice） */
let heardInPage = false;
/** iPhone／iPad：一次聽一句、聽完以同一個辨識器接著聽（voicemsgrules.dictationUtterances） */
const UTTERANCES = typeof navigator !== 'undefined' && dictationUtterances({ userAgent: navigator.userAgent, maxTouchPoints: navigator.maxTouchPoints });
/**
 * iPhone／iPad 這一頁共用的辨識器 { r, live（start 了、還沒 end）, plays（開始時語音合成出聲過幾句）}：跨輸入區、跨每次聽寫都用它；
 * 它還在聽、或之後語音通知出聲過（出聲之前建好的辨識器之後常收不到聲音，新建的才收得到）才另外新建
 */
let shared = null;

/** 共用的辨識器現在能拿來接著用嗎 */
function reusable() {
  return UTTERANCES && shared && !shared.live && shared.plays === speechPlays() ? shared.r : null;
}

/** 新的辨識器（zh-TW、即時結果；iPhone／iPad 一次一句，其他連續） */
function newRecognizer() {
  const r = new Recognition();
  r.lang = DICTATION_LANG;
  r.interimResults = true;
  r.continuous = !UTTERANCES;
  r.maxAlternatives = 1;
  recIds.set(r, ++recSeq);
  trace('新建辨識器 ' + recName(r));
  return r;
}

// ───── 診斷記錄（只在記憶體，最多 DIAG_MAX 行；openDiag） ─────
const DIAG_MAX = 400;
const DIAG_KEY = 'myriagent.voice.diagMode';
const diagLines = [];
const recIds = new WeakMap();
let recSeq = 0;
let envLogged = false;

function trace(msg) {
  const t = typeof performance !== 'undefined' && typeof performance.now === 'function' ? performance.now() : Date.now();
  diagLines.push((t / 1000).toFixed(2) + ' ' + msg);
  if (diagLines.length > DIAG_MAX) diagLines.splice(0, diagLines.length - DIAG_MAX);
}

function recName(r) {
  return r ? '#' + (recIds.get(r) || '?') : '-';
}

/** 這個瀏覽器的聽寫做法（只有 iPhone／iPad 用得到；沒選過是 gumHold） */
function currentDiagMode() {
  try {
    return diagMode(localStorage.getItem(DIAG_KEY));
  } catch {
    return diagMode(null);
  }
}

/** 瀏覽器還把這次當成使用者剛按的嗎（navigator.userActivation；iOS 有些版本沒有） */
function activation() {
  const ua = typeof navigator !== 'undefined' ? navigator.userActivation : null;
  return ua ? (ua.isActive ? '有' : '無') : '不明';
}

function logEnv() {
  if (envLogged) return;
  envLogged = true;
  const standalone = navigator.standalone === true || (typeof window.matchMedia === 'function' && window.matchMedia('(display-mode: standalone)').matches);
  trace('環境：' + navigator.userAgent);
  trace('主畫面 app=' + standalone + '，一句一句聽=' + UTTERANCES + '，觸控點=' + navigator.maxTouchPoints + '，做法=' + currentDiagMode());
}

/** navigator.audioSession 的類型與狀態（iOS 17 起的 Safari 才有） */
function audioSessionText() {
  const as = typeof navigator !== 'undefined' ? navigator.audioSession : null;
  return as ? as.type + '/' + as.state : '無';
}

/**
 * 先開的麥克風接上 Web Audio 讀音量（levelReader）：每 100 ms 讀一次、每秒記一次最大的。不只是記錄——iOS 要有東西真的在用麥克風的聲音，
 * 語音辨識第二次之後才收得到（voicemsgrules.DIAG_MODES 的 gumHold），不要拿掉。回停止的函式
 */
function meterStream(stream, label, onLevel) {
  const level = levelReader(stream);
  if (!level) {
    trace(label + '：讀不到音量');
    return () => {};
  }
  let peak = 0;
  let n = 0;
  const timer = setInterval(() => {
    const v = level.read();
    peak = Math.max(peak, v);
    if (onLevel) onLevel(v); // 聽寫時的音量格（第十一批，dictMeter）
    if (++n % 10 === 0) {
      trace(label + '音量 ' + diagDb(peak));
      peak = 0;
    }
  }, 100);
  return () => {
    clearInterval(timer);
    if (n % 10) trace(label + '音量 ' + diagDb(peak));
    if (onLevel) onLevel(null);
    level.close();
  };
}

function releaseTracks(stream) {
  try {
    for (const t of stream.getTracks()) t.stop();
  } catch {
    // 已經停了
  }
}

/** 用系統的語音辨識嗎：瀏覽器版、有語音辨識、手機或平板（桌面的 WebView2 沒有語音辨識） */
function canDictate() {
  return isHub && !!Recognition && !dictationOff
    && isHandheld({ coarsePointer: !!(COARSE && COARSE.matches), userAgent: navigator.userAgent, maxTouchPoints: navigator.maxTouchPoints });
}

/** 結果不明（瀏覽器版 150 秒等不到、重新連線）時的說明 */
const UNKNOWN_TEXT = '沒有收到那台電腦的回覆：語音訊息可能已經送出，請看對話的內容確認';

let active = null; // 正在錄音的那一個

/**
 * 這一頁開始了、還沒收到 end 的辨識器（每次聽寫的 d；跨輸入區）：瀏覽器同時只能有一個在聽，前一個還在收尾時就開始下一個，
 * 新的那個可能聽不到（按了講話沒反應，2026-10-24 使用者回報）。開始之前先等它們結束（voicemsgrules.dictationWaitStep）
 */
const recognizers = new Set();
/** 這一頁正在用麥克風的（聽寫的 d 到辨識器真的結束、錄音從要麥克風到關掉）：有就先不念語音通知（voice.js 的 holdSpeech） */
const micUsers = new Set();

/** 開始或結束用麥克風；回 true＝這次停下了正在念的語音通知 */
function useMic(who, on) {
  if (on) micUsers.add(who);
  else micUsers.delete(who);
  return holdSpeech(micUsers.size > 0);
}

/** 辨識器結束了（end，或停下之後等不到 end 當成結束）：不再等它、不再算在用麥克風 */
function recognizerEnded(d) {
  clearTimeout(d.endTimer);
  clearTimeout(d.deafTimer);
  recognizers.delete(d);
  useMic(d, false);
  if (d.stream) {
    if (d.unmeter) d.unmeter();
    d.unmeter = null;
    releaseTracks(d.stream); // 實驗做法 gumHold 開著的麥克風
    d.stream = null;
    trace('關掉先開的麥克風');
  }
}

/** 中止還在收尾的辨識器（下一次聽寫等它等太久）：之後才到的結果不寫回 */
function abortRecognizer(d) {
  if (d.aborting) return;
  d.aborting = true;
  d.stopped = true;
  d.discard = true;
  try {
    d.recognition.abort();
  } catch {
    recognizerEnded(d);
  }
}

/** 長按麥克風鈕多久打開診斷記錄（毫秒） */
const LONG_PRESS_MS = 700;

/** 錄音音量表多久讀一次麥克風的音量（毫秒）：約每秒 16 次，看得出說話的起伏又不必每一影格都讀 */
const METER_MS = 60;

/**
 * 麥克風實際收到的音量（Web Audio 的 AnalyserNode，和錄音器共用同一個串流）：回 { read()：這一刻的方均根, close() }。
 * 拿不到（瀏覽器不支援、不是真的音訊串流）回 null：錄音照常，只是沒有音量表
 */
function levelReader(stream) {
  const AC = window.AudioContext || window.webkitAudioContext;
  if (!AC) return null;
  let ctx = null;
  try {
    ctx = new AC();
    const src = ctx.createMediaStreamSource(stream);
    const an = ctx.createAnalyser();
    an.fftSize = 1024;
    src.connect(an);
    const buf = new Float32Array(an.fftSize);
    if (ctx.state === 'suspended') ctx.resume().catch(() => {});
    const c = ctx;
    return {
      read: () => { an.getFloatTimeDomainData(buf); return rmsOf(buf); },
      close: () => { try { src.disconnect(); c.close(); } catch { /* 已經關了 */ } },
    };
  } catch {
    try { if (ctx) ctx.close(); } catch { /* 已經關了 */ }
    return null;
  }
}

/**
 * 一個輸入區的麥克風鈕：回 { el, update, cancel, recording }。cp 是輸入區（composer.js 的 Composer）：用它的 conv、remoteUploads()、
 * header 與 uploadVoice(file)（上傳，回 { ok, entry, error }）
 */
export function createVoiceMessage(cp) {
  const btn = h('button', { type: 'button', class: 'cbtn mic', title: micTitle('idle'), 'aria-label': '語音訊息', 'aria-pressed': 'false', hidden: true }, icon('mic'));
  const time = h('span', { class: 'mic-time', hidden: true });
  const cancelBtn = iconButton('x', '取消錄音（Esc）', () => cancel(), 'cbtn mic-cancel');
  cancelBtn.hidden = true;
  // 錄音時的音量表（5 格，麥克風實際的音量；voicemsgrules.levelCells）：麥克風靜音、選錯裝置時一眼看得出來
  const meter = h('span', { class: 'mic-meter', 'aria-hidden': 'true', hidden: true }, ...Array.from({ length: LEVEL_CELLS }, () => h('i')));
  const el = h('span', { class: 'mic-wrap', dataset: { field: 'voice' } }, cancelBtn, meter, time, btn);
  let rec = null;      // { recorder, stream, chunks, startedAt, timer, mime, discard, level（音量的讀取器）, meterTimer, levels（錄音期間的音量） }
  let dict = null;     // 系統語音辨識正在聽：{ recognition, before, after, discard }
  let lastDict = null; // 最近一次的聽寫（停下之後才到的最後一段照樣寫進去，除非已經自己打字或送出）
  let preview = null;  // 聽寫的預覽列（還沒確定的字；showPreview）
  let sending = false;
  let starting = false;

  btn.addEventListener('mousedown', (e) => e.preventDefault()); // 不搶輸入框的焦點（聽寫要知道游標在哪）
  // 長按（LONG_PRESS_MS）打開語音輸入的診斷記錄；長按之後的那一下點按不算
  let pressTimer = 0;
  let longPressed = false;
  btn.addEventListener('pointerdown', () => {
    longPressed = false;
    clearTimeout(pressTimer);
    pressTimer = setTimeout(() => {
      longPressed = true;
      openDiag();
    }, LONG_PRESS_MS);
  });
  for (const ev of ['pointerup', 'pointercancel', 'pointerleave']) btn.addEventListener(ev, () => clearTimeout(pressTimer));
  btn.addEventListener('contextmenu', (e) => e.preventDefault());
  btn.addEventListener('click', () => {
    if (longPressed) {
      longPressed = false;
      return;
    }
    if (rec) stop();
    else if (dict) stopDictation(true);
    else if (mode() === 'dictation') startDictation();
    else start();
  });
  // 聽寫中自己打字（含輸入法組字）：交給鍵盤，之後才到的辨識結果不蓋掉打的字（辨識寫入時發的 input 事件 isTrusted 是 false）
  cp.ta.addEventListener('input', (e) => {
    if (!e.isTrusted) return;
    const d = dict || lastDict;
    if (d && d.recognition && !dictationTyped(cp.ta.value, d.written)) return; // 鍵盤把聽寫寫進去的字原樣交回：不是自己打字
    if (lastDict) lastDict.discard = true;
    if (dict) stopDictation(false);
  });

  /** endDictation：送出了，停止聽寫、之後才到的辨識結果不再寫回輸入框（composer.js 的 send） */
  const ctl = {
    el, update, cancel,
    endDictation: () => {
      if (lastDict) lastDict.discard = true;
      stopDictation(false);
    },
    recording: () => rec !== null || dict !== null,
  };

  function mode() {
    return voiceMode({ supported: SUPPORTED, remote: cp.remoteUploads(), voiceInput: state.voiceInput === true, dictation: canDictate() });
  }

  /** 送得出去嗎：聽寫只打進輸入框（送出照輸入區的閘門）；上傳的照附件的閘門（草稿、沒連線、電腦離線、政策）；這台的對話帳戶被移除時不行 */
  function gate() {
    const m = mode();
    if (m === 'dictation') return { enabled: true, reason: null };
    if (m === 'remote') return cp.conv.attachGate ? cp.conv.attachGate() : { enabled: true, reason: null };
    const hd = cp.header;
    return hd && hd.accountMissing ? { enabled: false, reason: '這個對話的帳戶已被移除' } : { enabled: true, reason: null };
  }

  function update() {
    const m = mode();
    const on = rec !== null || dict !== null;
    const show = !!m || on;
    if (btn.hidden === show) btn.hidden = !show;
    const g = gate();
    const disabled = !on && (sending || starting || !g.enabled);
    if (btn.disabled !== disabled) btn.disabled = disabled;
    const st = dict ? 'dictating' : rec ? 'recording' : sending ? 'sending' : 'idle';
    btn.classList.toggle('listening', on);
    btn.classList.toggle('sending', sending);
    btn.setAttribute('aria-pressed', String(on));
    const title = micTitle(st, { elapsedMs: rec ? Date.now() - rec.startedAt : 0, reason: g.enabled ? null : g.reason, dictation: m === 'dictation' });
    if (btn.title !== title) {
      btn.title = title;
      btn.setAttribute('aria-label', title);
    }
    cancelBtn.hidden = !rec;
    time.hidden = !rec;
    // 錄音時一定顯示；聽寫時由 dictMeter 顯示（第十一批），聽寫結束就藏起來、清掉亮格
    const dictMeterOn = !rec && !!dict && meter.classList.contains('dict');
    meter.hidden = !((rec && rec.level) || dictMeterOn);
    if (!rec && !dictMeterOn && meter.classList.contains('dict')) { meter.classList.remove('dict'); paintMeter(0); }
    btn.classList.toggle('has-level', !!(rec && rec.level));
    if (rec) time.textContent = formatElapsed(Date.now() - rec.startedAt);
  }

  async function start() {
    if (rec || starting || sending || !mode() || !gate().enabled) return;
    const mic = {};
    useMic(mic, true); // 錄音時先不念語音通知（念的聲音會錄進去）；先占著再停別的輸入區，免得中間補念一下
    if (active && active !== ctl) active.cancel();
    starting = true;
    update();
    let stream;
    try {
      stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true } });
    } catch (e) {
      starting = false;
      useMic(mic, false);
      update();
      toast(micError(e && e.name), 'error');
      return;
    }
    starting = false;
    let recorder;
    const mime = pickMime((m) => window.MediaRecorder.isTypeSupported(m));
    try {
      recorder = new window.MediaRecorder(stream, mime ? { mimeType: mime, audioBitsPerSecond: BITS_PER_SECOND } : { audioBitsPerSecond: BITS_PER_SECOND });
    } catch {
      release(stream);
      useMic(mic, false);
      update();
      toast('這個瀏覽器不能錄音', 'error');
      return;
    }
    const r = { recorder, stream, chunks: [], startedAt: Date.now(), timer: 0, mime: recorder.mimeType || mime, discard: false, levels: [], mic };
    recorder.ondataavailable = (ev) => { if (ev.data && ev.data.size > 0) r.chunks.push(ev.data); };
    recorder.onstop = () => finish(r);
    rec = r;
    active = ctl;
    r.timer = setInterval(() => {
      if (rec !== r) return;
      if (Date.now() - r.startedAt >= MAX_SECONDS * 1000) stop(); // 到上限自動停止並送出
      else update();
    }, 250);
    window.addEventListener('keydown', onKey, true);
    try {
      recorder.start(1000);
    } catch {
      rec = null;
      cleanup(r);
      toast('錄音沒有開始，請再按一次', 'error');
      return;
    }
    r.level = levelReader(stream);
    if (r.level) meterLoop(r);
    update();
  }

  /**
   * 音量表：每 METER_MS 讀一次音量，格數變了才改（變小每次最多掉一格，voicemsgrules.meterStep）；錄音結束就停。
   * 用計時器不用 requestAnimationFrame：視窗縮小或被擋住時動畫影格會停，計時器只是變慢（約每秒一次），波形照樣記到整段錄音
   */
  function meterLoop(r) {
    let shown = 0;
    r.meterTimer = setInterval(() => {
      if (rec !== r || !r.level) return;
      const rms = r.level.read();
      r.levels.push(rms); // 波形（waveOf）：整段錄音每次讀到的音量
      const next = meterStep(shown, levelCells(rms));
      if (next !== shown) { shown = next; paintMeter(shown); }
    }, METER_MS);
  }

  /**
   * 聽寫時的音量（第十一批，2026-11-08）：iPhone／iPad 先開著的麥克風本來就每 100 ms 讀一次音量（meterStream），畫在錄音的那組 5 格上，
   * 看得出麥克風有沒有收到聲音（「正在聽」卻打不出字時分得出是不是沒收到聲音）；結束（null）或已經不是這一次的聽寫就藏起來
   */
  function dictMeter(d) {
    let shown = 0;
    return (rms) => {
      if (rms == null || dict !== d) {
        if (!rec) { paintMeter(0); setHidden(meter, true); meter.classList.remove('dict'); }
        return;
      }
      if (meter.hidden) { setHidden(meter, false); meter.classList.add('dict'); }
      const next = meterStep(shown, levelCells(rms));
      if (next !== shown) { shown = next; paintMeter(shown); }
    };
  }

  function paintMeter(n) {
    Array.from(meter.children).forEach((c, i) => c.classList.toggle('lit', i < n));
    meter.dataset.level = String(n);
  }

  function onKey(e) {
    if (e.key !== 'Escape' || (!rec && !dict)) return;
    e.preventDefault();
    e.stopPropagation();
    if (dict) stopDictation(true); // 聽寫：已經打進去的字留著
    else cancel();
  }

  /**
   * 系統的語音辨識（手機與平板的瀏覽器版）：連續辨識、即時結果寫進輸入框的游標處，停下也不送出。按下就換成「正在聽」、停下正在念的
   * 語音通知；這一頁還有辨識器在收尾（剛按停、剛換輸入區）或語音通知還在出聲時，先等它們結束（最多約 0.8 秒，dictationWaitStep）再開始
   */
  function startDictation() {
    if (dict || rec || sending) return;
    const d = {
      recognition: null, before: '', after: '', written: null, discard: false, acc: dictationStart(),
      stopped: false, started: false, heard: false, noticed: false, error: null, startTimer: 0, endTimer: 0, deafTimer: 0,
      // 一句一句聽（iPhone／iPad）：rec＝現在的辨識器、gap＝一句聽完到接著聽之間的計時器、joined＝最後寫進去的（下一句接在後面）、
      // quietSince＝上次聽到字的時間、uttError＝這一句的錯誤、recoveries＝換過幾次收不到聲音的辨識器、quiet＝太久沒聽到字而停下
      rec: null, gap: 0, joined: null, quietSince: 0, uttError: null, recoveries: 0, quiet: false,
    };
    logEnv();
    const dm = UTTERANCES ? currentDiagMode() : 'shared';
    trace('按下：開始聽寫（做法 ' + dm + '；共用 ' + (shared ? recName(shared.r) + (shared.live ? ' 還在聽' : ' 閒置')
      + (shared.plays !== speechPlays() ? '、語音通知出聲過' : '') : '無') + '；收尾中 ' + recognizers.size + '；使用者動作 ' + activation()
      + '；audioSession ' + audioSessionText() + '）');
    d.fresh = dm === 'fresh' || dm === 'ttsFresh' || dm === 'sessionFlip';
    const silenced = useMic(d, true); // 先占著再停別的輸入區，免得中間補念一下
    if (active && active !== ctl) active.cancel();
    dict = d;
    active = ctl;
    showPreview('');
    window.addEventListener('keydown', onKey, true);
    update();
    const t0 = Date.now();
    let aborted = false;
    // iPhone／iPad 耐心等前一個辨識器結束（DICTATION_WAIT_UTTERANCES：不然只能另外新建，新建的常收不到聲音）
    const step = () => {
      if (dict !== d) return; // 等的時候停下了（stopDictation 已收拾）
      const next = dictationWaitStep({ waitedMs: Date.now() - t0, closing: recognizers.size, speechStopped: silenced, speaking: speechActive(), patient: UTTERANCES });
      if (next === 'start') {
        if (Date.now() - t0 > 0 || recognizers.size) trace('開始前等了 ' + (Date.now() - t0) + 'ms（收尾中 ' + recognizers.size + '）');
        for (const x of recognizers) recognizerEnded(x); // 等不到 end（卡住了）：不管它
        beginDictation(d);
        return;
      }
      if (next === 'abort') {
        if (!aborted) trace('前一個等太久，abort');
        aborted = true;
        for (const x of recognizers) abortRecognizer(x);
      }
      setTimeout(step, DICTATION_WAIT.stepMs);
    };
    // 實驗做法（voicemsgrules.DIAG_MODES）：先念一聲
    if (dm === 'ttsFresh' && 'speechSynthesis' in window && typeof window.SpeechSynthesisUtterance === 'function') {
      const s0 = Date.now();
      let went = false;
      const go = (why) => {
        if (went) return;
        went = true;
        trace('念一聲' + why + ' ' + (Date.now() - s0) + 'ms（audioSession ' + audioSessionText() + '）');
        setTimeout(step, 150);
      };
      const u = new window.SpeechSynthesisUtterance('好');
      u.lang = 'zh-TW';
      u.volume = 0.3;
      u.onstart = () => trace('念一聲開始 ' + (Date.now() - s0) + 'ms');
      u.onend = () => go('結束');
      u.onerror = (e) => go('錯誤 ' + ((e && e.error) || '?'));
      setTimeout(() => go('逾時'), 2500);
      window.speechSynthesis.speak(u);
      return;
    }
    // 切換音訊模式
    if (dm === 'sessionFlip') {
      const as = navigator.audioSession;
      if (as) {
        try {
          as.type = 'playback';
        } catch (e) {
          trace('audioSession 設不了 playback：' + ((e && e.name) || e));
        }
        trace('audioSession → ' + audioSessionText());
        setTimeout(() => {
          try {
            as.type = 'auto';
          } catch (e) {
            trace('audioSession 設不了 auto：' + ((e && e.name) || e));
          }
          trace('audioSession → ' + audioSessionText());
          setTimeout(step, 150);
        }, 300);
        return;
      }
      trace('沒有 navigator.audioSession');
    }
    // 開始前先以 getUserMedia 開麥克風、接上 Web Audio，開著到結束（gumHold，iPhone／iPad 的預設）或開一下就關
    if ((dm === 'gumHold' || dm === 'gumPrime') && navigator.mediaDevices && typeof navigator.mediaDevices.getUserMedia === 'function') {
      const g0 = Date.now();
      navigator.mediaDevices.getUserMedia({ audio: true }).then((stream) => {
        trace('getUserMedia 拿到麥克風 ' + (Date.now() - g0) + 'ms（audioSession ' + audioSessionText() + '）');
        if (dict !== d) {
          releaseTracks(stream);
          return;
        }
        if (dm === 'gumHold') {
          d.stream = stream;
          d.unmeter = meterStream(stream, '先開的麥克風', dictMeter(d));
          step();
        } else {
          const unmeter = meterStream(stream, '先開的麥克風');
          setTimeout(() => {
            unmeter();
            releaseTracks(stream);
            trace('關掉先開的麥克風');
            setTimeout(step, 300);
          }, 600);
        }
      }, (e) => {
        trace('getUserMedia 失敗 ' + ((e && e.name) || e));
        step();
      });
      return;
    }
    step(); // 沒有要等的就在點按當下開始
  }

  function beginDictation(d) {
    const ta = cp.ta;
    const s = typeof ta.selectionStart === 'number' ? ta.selectionStart : ta.value.length;
    const e = typeof ta.selectionEnd === 'number' ? ta.selectionEnd : s;
    d.before = ta.value.slice(0, s);
    d.after = ta.value.slice(e);
    d.written = ta.value;
    d.quietSince = Date.now();
    if (lastDict && lastDict !== d) lastDict.discard = true; // 前一次才到的結果不再寫回（游標前後已經照現在的內容記下）
    lastDict = d;
    recognizers.add(d);
    // DICTATION_START_TIMEOUT_MS 內都沒有任何動靜，當成沒開始（辨識器卡住）：中止、說明，按鈕不一直停在「正在聽」
    d.startTimer = setTimeout(() => {
      if (d.started || dict !== d) return;
      d.noticed = true;
      toast(DICTATION_NO_START, 'error');
      stopDictation(false);
    }, DICTATION_START_TIMEOUT_MS);
    if (!UTTERANCES) {
      // 開始了卻一直沒有字、也沒有錯誤或結束（WebKit bug 321436）：停下並說明，之前收得到字的這一頁改成錄音
      const heardBefore = heardInPage;
      d.deafTimer = setTimeout(() => {
        if (!d.started || d.heard || d.error || dict !== d) return;
        d.noticed = true;
        if (heardBefore) dictationOff = true;
        toast(dictationDeafNotice(heardBefore), 'error');
        stopDictation(false);
      }, DICTATION_DEAF_MS);
    }
    const res = listen(d);
    if (res !== 'ok') {
      clearTimeout(d.startTimer);
      clearTimeout(d.deafTimer);
      d.stopped = true;
      recognizerEnded(d);
      if (dict === d) finishDictation();
      if (res === 'ctor') {
        dictationOff = true;
        toast(dictationError('service-not-allowed'), 'error');
      } else {
        toast('語音輸入沒有開始，請再按一下', 'error');
      }
      return;
    }
    update();
  }

  /**
   * 開始（或一句聽完後接著）聽：iPhone／iPad 用這一頁共用的那個（它沒在聽的話），其他每次新建；start 丟例外（還在收尾、壞了）時換一個
   * 新的再試一次。回 'ok'、'ctor'（建不了辨識器：系統不讓瀏覽器用）、'start'（開始不了）
   */
  function listen(d) {
    let r = d.fresh ? null : reusable();
    if (UTTERANCES && !r && shared) trace('不沿用 ' + recName(shared.r) + '：' + (d.fresh ? '這個做法一律新建' : shared.live ? '還在聽' : '語音通知出聲過'));
    for (let attempt = 0; attempt < 2; attempt++) {
      if (!r) {
        try {
          r = newRecognizer();
        } catch (e) {
          trace('新建辨識器失敗 ' + ((e && e.name) || e));
          return 'ctor';
        }
      }
      bind(r, d);
      try {
        r.start();
        trace(recName(r) + ' start() 呼叫完成（使用者動作 ' + activation() + '）');
      } catch (e) {
        trace(recName(r) + ' start() 丟出 ' + ((e && e.name) || e) + (e && e.message ? '：' + e.message : ''));
        d.rec = null;
        if (shared && shared.r === r) shared = null;
        r = null;
        continue;
      }
      if (UTTERANCES) {
        shared = { r, live: true, plays: speechPlays() };
        armDeaf(d);
      }
      return 'ok';
    }
    return 'start';
  }

  /** 辨識器的事件交給這次聽寫 d；d 已經換掉的辨識器（收不到聲音而換新的、開始不了的）之後的事件不理 */
  function bind(r, d) {
    d.rec = r;
    d.recognition = r;
    const mine = () => d.rec === r;
    // 有任何動靜就算開始了
    const alive = () => {
      if (d.started) return;
      d.started = true;
      clearTimeout(d.startTimer);
    };
    const tag = () => recName(r) + (mine() ? '' : '（已換掉）');
    const b0 = Date.now();
    const ms = () => (Date.now() - b0) + 'ms';
    r.onstart = () => {
      trace(tag() + ' start ' + ms());
      if (mine()) alive();
    };
    r.onaudiostart = () => {
      trace(tag() + ' audiostart ' + ms());
      if (mine()) alive();
    };
    for (const name of ['soundstart', 'speechstart', 'speechend', 'soundend', 'audioend', 'nomatch']) r['on' + name] = () => trace(tag() + ' ' + name + ' ' + ms());
    r.onresult = (ev) => {
      const list = ev && ev.results;
      const last = list && list.length ? list[list.length - 1] : null;
      trace(tag() + ' result ' + (list ? list.length : 0) + ' 段' + (last && last.isFinal ? '（確定）' : '') + '「' + diagText(last && last[0] && last[0].transcript) + '」 ' + ms());
      if (!mine()) return;
      alive();
      if (UTTERANCES) armDeaf(d);
      if (d.discard) return;
      // 每次都是這個辨識器到目前為止的全部結果；iOS 停頓後清掉重來、重送同一段的由 dictationStep 接好（不截斷、不重複）
      const step = dictationStep(d.acc, ev.results);
      d.acc = step.state;
      if (step.text) {
        d.heard = true;
        heardInPage = true;
        d.quietSince = Date.now();
      }
      applyDictation(d, step.text);
      if (dict === d) showPreview(step.interim); // 停下之後才到的最後一段只寫進輸入框，預覽列不再出現
    };
    r.onerror = (ev) => {
      trace(tag() + ' error ' + ((ev && ev.error) || '?') + (ev && ev.message ? '（' + ev.message + '）' : '') + ' ' + ms());
      if (!mine()) return;
      alive();
      const code = ev && ev.error;
      if (UTTERANCES) {
        clearTimeout(d.deafTimer);
        d.uttError = code || 'unknown';
        if (!d.stopped && code === 'no-speech') return; // 這一句沒說話：接著聽下一句（onend）
      }
      d.error = code || 'unknown';
      if (dictationFallback(code)) dictationOff = true; // 這一頁改回錄音（onend 之後按鈕的說明跟著換）
      if (d.discard) return; // 自己中止的（打字、送出、換輸入區、等太久）
      const msg = dictationError(code);
      if (msg) {
        d.noticed = true;
        toast(msg, 'error');
      }
    };
    r.onend = () => {
      trace(tag() + ' end ' + ms());
      if (shared && shared.r === r) shared.live = false;
      if (!mine()) return;
      alive();
      clearTimeout(d.deafTimer);
      if (UTTERANCES) {
        const next = dictationNext({ stopped: d.stopped || dict !== d, error: d.uttError, quietMs: Date.now() - d.quietSince });
        d.uttError = null;
        if (next === 'restart') {
          nextUtterance(d);
          return;
        }
        if (next === 'quiet') d.quiet = true;
      }
      dictationEnded(d);
    };
  }

  /** iPhone／iPad 一句聽完：這句的字留在輸入框（下一句接在後面），稍等 DICTATION_RESTART_MS 再接著聽；這時停下就直接結束 */
  function nextUtterance(d) {
    trace('一句結束，' + DICTATION_RESTART_MS + 'ms 後接著聽');
    if (d.joined) d.before = d.joined.value.slice(0, d.joined.caret);
    d.joined = null;
    d.acc = dictationStart();
    d.gap = setTimeout(() => {
      d.gap = 0;
      if (dict !== d || d.stopped) {
        dictationEnded(d);
        return;
      }
      if (listen(d) !== 'ok') {
        d.stopped = true;
        d.noticed = true;
        dictationEnded(d);
        toast('語音輸入沒有接著聽下去，請再按一下麥克風', 'error');
      }
    }, DICTATION_RESTART_MS);
  }

  /**
   * iPhone／iPad：聽著卻 DICTATION_DEAF_MS 都沒有任何事件（不給結果、不報錯、不結束；WebKit bug 321436）——丟掉這個辨識器、
   * 換一個新的接著聽；DICTATION_QUIET_MS 都沒聽到字就停下說明
   */
  function armDeaf(d) {
    clearTimeout(d.deafTimer);
    d.deafTimer = setTimeout(() => {
      if (dict !== d || d.stopped || !d.rec) return;
      const old = d.rec;
      trace(recName(old) + ' ' + DICTATION_DEAF_MS + 'ms 沒有任何事件：換新的辨識器');
      d.rec = null;
      if (shared && shared.r === old) shared = null;
      try {
        old.abort();
      } catch {
        // 已經結束
      }
      d.recoveries = (d.recoveries || 0) + 1;
      if (Date.now() - d.quietSince >= DICTATION_QUIET_MS) {
        d.quiet = true;
        dictationEnded(d);
        return;
      }
      nextUtterance(d);
    }, DICTATION_DEAF_MS);
  }

  /** 這次聽寫真的結束了（辨識器 end、一句一句聽時不再接著聽）：收起來、需要時說明 */
  function dictationEnded(d) {
    trace('聽寫結束（聽到字 ' + !!d.heard + (d.error ? '，錯誤 ' + d.error : '') + (d.quiet ? '，太久沒聽到' : '') + (d.recoveries ? '，換過 ' + d.recoveries + ' 次' : '') + '）');
    recognizerEnded(d);
    if (dict === d) finishDictation();
    else update();
    const note = dictationEndNotice({ stopped: d.stopped, noticed: d.noticed, heard: d.heard, error: d.error, quiet: d.quiet, deaf: !!d.recoveries });
    if (note) {
      d.noticed = true;
      toast(note, d.error === 'aborted' || (d.quiet && d.recoveries && !d.heard) ? 'error' : 'info');
    }
  }

  function applyDictation(d, text) {
    const ta = cp.ta;
    const j = joinTranscript(d.before, text, d.after);
    d.joined = j; // 一句一句聽時，下一句接在這句後面（nextUtterance）
    if (ta.value === j.value) return;
    ta.value = j.value;
    d.written = j.value;
    try {
      ta.setSelectionRange(j.caret, j.caret);
    } catch {
      // 輸入框不在畫面上時忽略
    }
    ta.dispatchEvent(new Event('input', { bubbles: true })); // 輸入區照常更新高度、送出鈕與草稿
  }

  /**
   * 聽寫的預覽列（輸入框上方，fx.css 的 .dict-preview）：只列還沒確定的那一段（淡色、虛線底線），確定之後就從這裡消失；
   * 沒有未確定的字時寫「正在聽…」。輸入框照樣即時寫進全部的字（中途自己打字時那段字要留著），這一條只是標出哪些字還會變
   */
  function showPreview(interim) {
    if (!preview) {
      const log = h('button', { type: 'button', class: 'dict-log', text: '記錄', title: '語音輸入的診斷記錄' });
      log.addEventListener('mousedown', (e) => e.preventDefault()); // 不搶輸入框的焦點
      log.addEventListener('click', () => openDiag());
      preview = h('div', { class: 'dict-preview' }, h('span', { class: 'dict-led', 'aria-hidden': 'true' }), h('span', { class: 'dict-text', 'aria-hidden': 'true' }), log);
      cp.ta.before(preview);
    }
    const text = (interim || '').trim();
    const el = preview.querySelector('.dict-text');
    if (el.textContent !== (text || '正在聽…')) el.textContent = text || '正在聽…';
    preview.classList.toggle('idle', !text);
    preview.hidden = false;
  }

  function finishDictation() {
    dict = null;
    if (preview) preview.hidden = true;
    window.removeEventListener('keydown', onKey, true);
    if (active === ctl) active = null;
    update();
  }

  /**
   * 停止聽寫。keepLate＝停下之後才到的最後一段照樣寫進去（按麥克風鈕、Esc）；false＝丟掉（送出了、自己開始打字、另一個輸入區開始、
   * 分頁關了：之後才到的結果會蓋掉輸入框的新內容）。已經寫進去的字都留著
   */
  function stopDictation(keepLate) {
    if (!dict) return;
    const d = dict;
    trace('停止聽寫（' + (keepLate ? '按鈕' : '打字、送出或取消') + '）' + recName(d.rec) + (d.gap ? '，在兩句之間' : ''));
    d.stopped = true;
    if (!keepLate) d.discard = true;
    clearTimeout(d.startTimer);
    clearTimeout(d.deafTimer);
    finishDictation();
    if (!d.recognition) {
      useMic(d, false); // 還在等前一個辨識器結束：沒開始過
      return;
    }
    if (d.gap || !d.rec) {
      // iPhone／iPad 一句聽完、還沒接著聽：辨識器已經 end 了
      clearTimeout(d.gap);
      d.gap = 0;
      dictationEnded(d);
      return;
    }
    const r = d.rec;
    // 等不到 end（辨識器卡住）也不一直等：過一下中止它、當成結束了，不再擋下一次開始、語音通知照念
    // （iOS 的辨識器還開著時念語音通知，之後的語音辨識會收不到聲音，WebKit bug 321436：先中止再放開）
    d.endTimer = setTimeout(() => {
      trace(recName(r) + ' 停止後 ' + DICTATION_END_GRACE_MS + 'ms 還沒 end：abort');
      try {
        r.abort();
      } catch {
        // 已經結束
      }
      recognizerEnded(d);
    }, DICTATION_END_GRACE_MS);
    try {
      // iPhone／iPad 一律 stop()：abort() 之後同一個辨識器常收不到聲音、每次都要重問麥克風（之後才到的結果有 discard 擋著）
      if (keepLate || UTTERANCES) r.stop();
      else r.abort();
    } catch {
      recognizerEnded(d); // 已經結束
    }
  }

  /** 停止並送出 */
  function stop() {
    if (!rec) return;
    rec.endedAt = Date.now();
    try {
      rec.recorder.stop();
    } catch {
      finish(rec);
    }
  }

  /** 取消（不送）：× 鈕、Esc、另一個輸入區開始錄音或聽寫、分頁關掉；聽寫中的停下（已經打進去的字留著） */
  function cancel() {
    if (dict) stopDictation(false);
    if (!rec) return;
    rec.discard = true;
    stop();
  }

  function release(stream) {
    try {
      for (const t of stream.getTracks()) t.stop();
    } catch {
      // 已經停了
    }
  }

  function cleanup(r) {
    clearInterval(r.timer);
    clearInterval(r.meterTimer);
    if (r.level) { r.level.close(); r.level = null; }
    paintMeter(0);
    release(r.stream);
    useMic(r.mic, false);
    window.removeEventListener('keydown', onKey, true);
    if (active === ctl) active = null;
    update();
  }

  function finish(r) {
    if (r.finished) return;
    r.finished = true;
    if (rec === r) rec = null;
    cleanup(r);
    if (r.discard) return;
    const blob = new Blob(r.chunks, { type: r.mime || 'audio/webm' });
    const ms = (r.endedAt || Date.now()) - r.startedAt;
    const problem = checkRecording(ms, blob.size);
    if (problem) {
      toast(problem, 'error');
      return;
    }
    send(blob, r.mime || blob.type, waveOf(r.levels));
  }

  async function send(blob, mime, wave) {
    sending = true;
    update();
    try {
      const ext = extensionFor(mime) || '.weba';
      if (mode() === 'remote') await sendRemote(new File([blob], voiceFileName(new Date(), ext), { type: (mime || '').split(';')[0] || 'audio/webm' }));
      else await sendLocal(blob, mime, wave);
    } catch (e) {
      toast('語音訊息沒有送出：' + ((e && e.message) || e), 'error');
    } finally {
      sending = false;
      update();
    }
  }

  /** 桌面這台的對話：dataUrl 交給宿主（宿主存進附件、辨識、送出；輸入區顯示「正在辨識語音…」） */
  /** 桌面這台的對話：dataUrl 交給宿主；wave 是錄音時記下的波形（存在錄音附件上，泡泡裡畫成波形條；遠端的不帶） */
  function sendLocal(blob, mime, wave) {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => {
        let url = String(reader.result || '');
        // MediaRecorder 的 Blob 沒有型別時 FileReader 給 application/octet-stream：換成錄音的格式
        const type = (mime || '').split(';')[0] || 'audio/webm';
        url = url.replace(/^data:[^,]*;base64,/, 'data:' + type + ';base64,');
        post({ cmd: 'voiceMessage', conversationId: cp.conv.id, dataUrl: url, ...(wave ? { wave } : {}) });
        resolve();
      };
      reader.onerror = () => reject(new Error('讀不了錄音'));
      reader.readAsDataURL(blob);
    });
  }

  /** 上傳之後送 voiceMessage {uploads}，等那台電腦的結果（被拒的原因由那台另送提示） */
  async function sendRemote(file) {
    const r = await cp.uploadVoice(file);
    if (!r || !r.ok) {
      toast((r && r.error) || '錄音沒有上傳成功', 'error');
      return;
    }
    const cid = post({ cmd: 'voiceMessage', conversationId: cp.conv.id, uploads: [r.entry] });
    if (typeof cid !== 'string') {
      toast('目前沒有連線，語音訊息沒有送出', 'error');
      return;
    }
    await new Promise((resolve) => {
      const tracked = cp.conv.ctx && cp.conv.ctx.track && cp.conv.ctx.track(cid, {
        onSettle: (st, code, origin, elapsed) => {
          if (st === 'unknown') toast(UNKNOWN_TEXT, 'error');
          else if (st === 'done' && cp.showRtt) cp.showRtt(elapsed); // 輸入框下方亮一下往返時間
          resolve();
        },
      });
      if (!tracked) resolve();
    });
  }

  /**
   * 語音輸入的診斷記錄：這一頁每次按下、每個辨識器的事件與時間（textContent，不解析）；複製、放進輸入框（送給代理看）、清除；
   * iPhone／iPad 另可選實驗的做法（存在這個瀏覽器，下次按麥克風起生效）
   */
  function openDiag() {
    const pre = h('pre', { class: 'voice-diag' });
    const fill = () => {
      pre.textContent = diagLines.length ? diagLines.join('\n') : '（還沒有記錄：用一次語音輸入再回來看）';
    };
    fill();
    const modeRow = h('div', { class: 'voice-diag-modes' });
    const paintModes = () => {
      const cur = currentDiagMode();
      for (const b of modeRow.children) b.setAttribute('aria-pressed', String(b.dataset.mode === cur));
    };
    for (const m of DIAG_MODES) {
      const b = h('button', { type: 'button', class: 'btn', text: DIAG_MODE_LABELS[m], dataset: { mode: m } });
      b.addEventListener('click', () => {
        try {
          localStorage.setItem(DIAG_KEY, m);
        } catch {
          // 存不了：照舊
        }
        trace('改用做法 ' + m);
        paintModes();
        fill();
      });
      modeRow.append(b);
    }
    paintModes();
    const body = h('div', { class: 'voice-diag-body' },
      h('p', { class: 'confirm-detail', text: '這一頁語音輸入的經過（只在這個頁面、重新整理就清掉）。複製或放進輸入框送出，就能讓代理看到哪一步出問題。' }),
      UTTERANCES ? h('p', { class: 'confirm-detail', text: '聽寫的做法（iPhone／iPad，下次按麥克風起生效；打不出字時可以換一個再試，不用重開 app）：' }) : null,
      UTTERANCES ? modeRow : null,
      pre);
    const text = () => '語音輸入診斷記錄：\n' + diagLines.join('\n');
    const clear = h('button', { type: 'button', class: 'btn', text: '清除' });
    const copy = h('button', { type: 'button', class: 'btn', text: '複製' });
    const put = h('button', { type: 'button', class: 'btn primary', text: '放進輸入框' });
    const d = openDialog({ title: '語音輸入診斷記錄', body, footer: [clear, h('span', { class: 'grow' }), copy, put], width: 640, initialFocus: put });
    clear.addEventListener('click', () => {
      diagLines.length = 0;
      fill();
    });
    copy.addEventListener('click', () => {
      const t = text();
      const done = () => toast('已複製診斷記錄', 'info');
      if (navigator.clipboard && typeof navigator.clipboard.writeText === 'function') {
        navigator.clipboard.writeText(t).then(done, () => toast('複製不了：請改按「放進輸入框」', 'error'));
      } else {
        toast('這個瀏覽器不能複製：請改按「放進輸入框」', 'error');
      }
    });
    put.addEventListener('click', () => {
      if (dict) stopDictation(false);
      const ta = cp.ta;
      ta.value = (ta.value ? ta.value + '\n\n' : '') + text();
      ta.dispatchEvent(new Event('input', { bubbles: true }));
      d.close();
    });
  }

  update();
  return ctl;
}
