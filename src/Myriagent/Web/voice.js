// 瀏覽器版的語音通知：以這台裝置的語音念 speak（web-protocol）。收到 speak 時那個對話開始閃、閃到使用者點它為止（attnflash.js；不管念不念），
// 開始出聲時它還在閃就讓標頭另外閃三下。
// 桌面 app 由宿主以 Windows 語音念、網頁不念（宿主不送 speak）。瀏覽器版的開關、語速與音調存在這個瀏覽器（localStorage），不經伺服器、不跟其他裝置同步。
// 瀏覽器在使用者還沒點過頁面之前不准發聲（自動播放政策；手機一樣）：被擋下的那一句先記著，第一次點畫面時補念，提示交給 liveview（onBlockedChange）。
// 同一個瀏覽器開好幾個分頁時，每筆事件只有搶到的那個分頁念（Web Locks＋localStorage 的紀錄）。念的文字一律只交給 SpeechSynthesisUtterance，不進 DOM
// 這一頁在用麥克風（聽寫、錄音）時先不念，用完再補念（holdSpeech）
import * as ws from './workspace.js';
import { isHub } from './store.js';
import { startFlash, isFlashing } from './attnflash.js';
import {
  ENABLED_SAMPLE, TUNING_SAMPLE, MAX_QUEUED, voiceEnabled, normSpeed, normPitch, webPitch, pickVoice, wasSpoken, rememberSpoken, speakKey, speakText, pendingFresh,
} from './voicerules.js';

// ───── 瀏覽器版的語音（speak） ─────

const STORE_KEY = 'myriagent.voice';         // 'on' | 'off'（沒存過＝開）
const SPEED_KEY = 'myriagent.voice.speed';   // 語速（總倍速；沒存過＝預設）
const PITCH_KEY = 'myriagent.voice.pitch';   // 音調（半音；沒存過＝預設）
const SPOKEN_KEY = 'myriagent.voice.spoken'; // 最近念過的事件（其他分頁不重念）

/** 這個瀏覽器能念嗎（Web Speech API 的語音合成） */
export function voiceSupported() {
  return typeof window !== 'undefined' && 'speechSynthesis' in window && typeof window.SpeechSynthesisUtterance === 'function';
}

/** 這個瀏覽器的語音通知開著嗎 */
export function voiceOn() {
  try {
    return voiceEnabled(localStorage.getItem(STORE_KEY));
  } catch {
    return true;
  }
}

/** 設定頁的開關：打開時念一句試聽（在點按當下念，順便解開瀏覽器的發聲限制），關掉時停下正在念的 */
export function setVoiceOn(on) {
  try {
    localStorage.setItem(STORE_KEY, on ? 'on' : 'off');
  } catch {
    // 無痕模式等存不了：這次執行照樣生效
  }
  if (!voiceSupported()) return;
  if (on) {
    unlocked = true;
    say(ENABLED_SAMPLE, null, voiceTuning(), false);
  } else {
    pending = null;
    deferred = [];
    queued = 0;
    notifyBlocked(false);
    window.speechSynthesis.cancel();
  }
}

/** 這個瀏覽器的語速與音調 { speed, pitch }（正規化過） */
export function voiceTuning() {
  let speed = null;
  let pitch = null;
  try {
    speed = localStorage.getItem(SPEED_KEY);
    pitch = localStorage.getItem(PITCH_KEY);
  } catch {
    // 存不了的瀏覽器：用預設
  }
  return { speed: normSpeed(speed), pitch: normPitch(pitch) };
}

/** 設定頁的滑桿放開時：存語速（'speed'）或音調（'pitch'），開關開著就先停下正在念的、以新的設定念一句試聽（在點按當下念） */
export function setVoiceTuning(key, value) {
  const before = voiceTuning();
  try {
    if (key === 'speed') localStorage.setItem(SPEED_KEY, String(normSpeed(value)));
    else if (key === 'pitch') localStorage.setItem(PITCH_KEY, String(normPitch(value)));
    else return;
  } catch {
    // 存不了：這次照樣試聽
  }
  const after = key === 'speed' ? { ...before, speed: normSpeed(value) } : { ...before, pitch: normPitch(value) };
  if (!voiceSupported() || !voiceOn() || (after.speed === before.speed && after.pitch === before.pitch)) return;
  unlocked = true;
  window.speechSynthesis.cancel();
  queued = 0;
  say(TUNING_SAMPLE, null, after, false);
}

let unlocked = false;   // 這個頁面點過了（沒有 navigator.userActivation 的瀏覽器用它判斷）
let queued = 0;         // 交給瀏覽器、還沒念完的句數（同時完成很多個對話時，多的不念，同桌面）
let pending = null;     // 被擋下的那一句 { text, conversationId, at }
let blockedListener = null;
let held = false;       // 這一頁正在用麥克風（holdSpeech）：先不念
let deferred = [];      // 用麥克風時延後的句子 { text, conversationId, at }（最多 MAX_QUEUED 句）
const inFlight = new Set(); // 交給瀏覽器、還沒念完的句子（停下時延後補念）

/**
 * 這一頁在用麥克風（手機與平板的聽寫、錄音，voicemsg.js）時先不念語音通知：念的聲音會被麥克風收進去（打進輸入框、錄進語音訊息），
 * 手機上還會跟語音辨識搶聲音、讓它聽不到（2026-10-24 使用者回報按了講話沒反應）。on＝開始用：停下正在念的、之後到的先記著；
 * off＝用完了：還不算太久的（pendingFresh）照順序補念。回 true＝這次停下了正在念的（呼叫端稍等一下再開麥克風）
 */
export function holdSpeech(on) {
  if (!isHub || !voiceSupported()) return false;
  if (on) {
    if (held) return false;
    held = true;
    const synth = window.speechSynthesis;
    const busy = inFlight.size > 0 || !!synth.speaking || !!synth.pending;
    for (const p of inFlight) defer(p);
    inFlight.clear();
    if (busy) {
      queued = 0;
      synth.cancel();
    }
    return busy;
  }
  if (!held) return false;
  held = false;
  const now = Date.now();
  const list = deferred.filter((p) => pendingFresh(p, now));
  deferred = [];
  if (voiceOn()) for (const p of list) say(p.text, p.conversationId);
  return false;
}

let plays = 0; // 這一頁的語音合成真的出聲過幾句（speechPlays）

/**
 * 這一頁的語音合成真的出聲過幾句：iPhone／iPad 上，出聲之前就建好的語音辨識器之後常收不到聲音、新建的才收得到
 * （WebKit bug 321436 的 iOS 27.0 回報），聽寫（voicemsg.js）以它決定共用的辨識器還能不能用
 */
export function speechPlays() {
  return plays;
}

/** 語音合成還在出聲嗎（開麥克風之前等它停） */
export function speechActive() {
  return voiceSupported() && !!window.speechSynthesis.speaking;
}

function defer(p) {
  if (deferred.length < MAX_QUEUED) deferred.push(p);
}

/** liveview 的提示：被擋下時 true（顯示「點一下畫面就能聽到」），補念了或關掉時 false */
export function onBlockedChange(fn) {
  blockedListener = fn;
}

function notifyBlocked(blocked) {
  if (blockedListener) blockedListener(blocked);
}

/** 瀏覽器允許這個頁面發聲了嗎（點過、按過鍵） */
function activated() {
  const ua = typeof navigator !== 'undefined' ? navigator.userActivation : null;
  return unlocked || !!(ua && ua.hasBeenActive);
}

/**
 * speak：那個對話開始閃，閃到使用者點它為止（每個分頁都閃；語音關著、念不出來也閃）。
 * 開關開著、這個分頁搶到這筆事件才念；還不准發聲就記著等第一次點畫面
 */
export async function onSpeak(m) {
  const text = speakText(m);
  if (!isHub || !text) return;
  const conversationId = typeof m.conversationId === 'string' ? m.conversationId : null;
  if (conversationId) startFlash(conversationId);
  if (!voiceSupported() || !voiceOn()) return;
  if (!(await claim(speakKey(m)))) return;
  if (held) {
    defer({ text, conversationId, at: Date.now() });
    return;
  }
  if (!activated()) {
    pending = { text, conversationId, at: Date.now() };
    notifyBlocked(true);
    return;
  }
  say(text, conversationId);
}

/** 這個分頁搶下這筆事件（同一個瀏覽器的其他分頁已經念了就不念）；有 Web Locks 時在鎖裡讀寫紀錄，兩個分頁同時收到也只有一個念 */
async function claim(key) {
  const take = () => {
    const now = Date.now();
    let list = [];
    try {
      list = JSON.parse(localStorage.getItem(SPOKEN_KEY) || '[]');
    } catch {
      list = [];
    }
    if (wasSpoken(list, key, now)) return false;
    try {
      localStorage.setItem(SPOKEN_KEY, JSON.stringify(rememberSpoken(list, key, now)));
    } catch {
      // 存不了就只防這個分頁自己重念
    }
    return true;
  };
  try {
    if (navigator.locks && typeof navigator.locks.request === 'function') return await navigator.locks.request('myriagent.voice', take);
  } catch {
    // 沒有 Web Locks（舊瀏覽器、不安全的來源）：直接讀寫
  }
  return take();
}

/**
 * 念一句（瀏覽器自己排隊，最多 MAX_QUEUED 句；語速與音調照這個瀏覽器的設定，試聽時給剛調好的）；開始出聲時那個對話還在閃（使用者還沒點它）就讓標頭
 * 另外閃三下（和側欄點對話時同一種），一聽到就找得到念的是哪一個。被擋下（not-allowed）時記著等點畫面。
 * resume＝開麥克風停下它時用完再補念（試聽不補）
 */
function say(text, conversationId, tuning = voiceTuning(), resume = true) {
  const synth = window.speechSynthesis;
  if (queued >= MAX_QUEUED) return;
  queued++;
  let settled = false;
  const entry = { text, conversationId, at: Date.now() };
  const done = () => {
    if (settled) return;
    settled = true;
    inFlight.delete(entry);
    queued = Math.max(0, queued - 1);
  };
  const u = new window.SpeechSynthesisUtterance(text);
  u.lang = 'zh-TW';
  const voice = pickVoice(synth.getVoices());
  if (voice) u.voice = voice;
  u.rate = tuning.speed;
  u.pitch = webPitch(tuning.pitch);
  u.onstart = () => {
    plays++;
    if (conversationId && isFlashing(conversationId)) ws.flashConversation(conversationId);
  };
  u.onend = done;
  u.onerror = (e) => {
    done();
    if (e && e.error === 'not-allowed') {
      unlocked = false;
      pending = { text, conversationId, at: Date.now() };
      notifyBlocked(true);
    }
  };
  if (resume) inFlight.add(entry);
  synth.speak(u);
}

/**
 * 臨時對話的「朗讀」（quickchat.js）：先停下正在念的，再依序念這幾段（使用者按了鈕，發聲限制已解開；不受語音通知的開關與佇列上限影響）。
 * 回傳 false＝這個瀏覽器不能念
 */
export function readAloud(chunks) {
  if (!voiceSupported() || !Array.isArray(chunks) || !chunks.length) return false;
  unlocked = true;
  const synth = window.speechSynthesis;
  synth.cancel();
  queued = 0;
  inFlight.clear();
  const tuning = voiceTuning();
  const voice = pickVoice(synth.getVoices());
  for (const text of chunks) {
    const u = new window.SpeechSynthesisUtterance(text);
    u.lang = 'zh-TW';
    if (voice) u.voice = voice;
    u.rate = tuning.speed;
    u.pitch = webPitch(tuning.pitch);
    u.onstart = () => { plays++; };
    synth.speak(u);
  }
  return true;
}

/** 停止朗讀（也停下正在念的語音通知） */
export function stopReadAloud() {
  if (!voiceSupported()) return;
  window.speechSynthesis.cancel();
  queued = 0;
  inFlight.clear();
}

/** 第一次點畫面或按鍵：解開發聲限制，補念被擋下的那一句（太久以前的就不念了） */
function unlock() {
  unlocked = true;
  if (!pending) return;
  const p = pending;
  pending = null;
  notifyBlocked(false);
  if (!pendingFresh(p, Date.now()) || !voiceOn()) return;
  if (held) defer(p); // 聽寫或錄音中點了畫面：用完麥克風再念
  else say(p.text, p.conversationId);
}

/** 提示列的「播放」按鈕：在點按當下補念 */
export function playPending() {
  unlock();
}

if (isHub && typeof document !== 'undefined') {
  document.addEventListener('pointerdown', unlock, { capture: true, passive: true });
  document.addEventListener('keydown', unlock, { capture: true });
  // 有些瀏覽器的聲音清單是非同步載入的：先要一次，念的時候才拿得到
  if (voiceSupported()) {
    try {
      window.speechSynthesis.getVoices();
    } catch {
      // 忽略
    }
  }
}
