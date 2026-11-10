// 臨時對話（右上角的對話鈕開的浮動面板，2026-11-11 使用者要求；quickchat.js）的純規則：面板的位置與大小、朗讀的分段。

/** 面板記號（workspace 的 dock、quickChat 的 paneId） */
export const QUICK_DOCK = 'quick:dock';

/** 面板的位置與大小存在 localStorage（每個瀏覽器、桌面各自） */
export const QUICK_RECT_KEY = 'myriagent.quickChat.rect.v1';

/** 上次用的帳戶 */
export const QUICK_ACCOUNT_KEY = 'myriagent.quickChat.account.v1';

export const QUICK_MIN_W = 320;
export const QUICK_MIN_H = 360;

/** 視窗比這個窄（手機）時面板鋪滿頂端列以下 */
export const QUICK_FULL_BELOW = 600;

/**
 * 面板的位置與大小：存下的照用、拉回視窗裡（視窗變小了也看得到標題列）；沒存過是靠右上角、寬 420、高為視窗的 70%。
 * 視窗窄於 QUICK_FULL_BELOW（手機）或比最小高度還矮時鋪滿頂端列以下
 */
export function panelRect(saved, vw, vh, top = 48) {
  const margin = 12;
  if (vw < QUICK_FULL_BELOW || vh < QUICK_MIN_H + top + margin) return { x: 0, y: top, w: vw, h: Math.max(200, vh - top), full: true };
  const maxW = vw - margin * 2;
  const maxH = vh - top - margin;
  const num = (v) => (typeof v === 'number' && Number.isFinite(v) ? v : null);
  let w = num(saved && saved.w) ?? Math.min(420, maxW);
  let h = num(saved && saved.h) ?? Math.round(Math.min(maxH, Math.max(QUICK_MIN_H, vh * 0.7)));
  w = Math.min(Math.max(w, QUICK_MIN_W), maxW);
  h = Math.min(Math.max(h, QUICK_MIN_H), maxH);
  let x = num(saved && saved.x) ?? vw - w - margin;
  let y = num(saved && saved.y) ?? top + 4;
  x = Math.min(Math.max(x, margin), vw - w - margin);
  y = Math.min(Math.max(y, top), vh - 40);
  return { x: Math.round(x), y: Math.round(y), w: Math.round(w), h: Math.round(h), full: false };
}

/**
 * 朗讀的分段（瀏覽器的語音合成一次念太長會在中途停下）：照句號、問號、驚嘆號、換行切，每段不超過 max 字，
 * 太長的一句照逗號、再照字數切；空白的段落丟掉
 */
export function speechChunks(text, max = 160) {
  if (typeof text !== 'string') return [];
  const out = [];
  const push = (s) => { const t = s.trim(); if (t) out.push(t); };
  const sentences = text.replace(/\r\n/g, '\n').split(/(?<=[。！？!?；;])|\n+/);
  let buf = '';
  for (const raw of sentences) {
    const s = (raw || '').trim();
    if (!s) continue;
    if (s.length > max) {
      push(buf); buf = '';
      let part = '';
      for (const piece of s.split(/(?<=[，,、：:])/)) {
        if ((part + piece).length > max) { push(part); part = ''; }
        if (piece.length > max) { for (let i = 0; i < piece.length; i += max) push(piece.slice(i, i + max)); continue; }
        part += piece;
      }
      push(part);
      continue;
    }
    if ((buf + s).length > max) { push(buf); buf = ''; }
    buf += (buf ? ' ' : '') + s;
  }
  push(buf);
  return out;
}

/** 朗讀的文字（瀏覽器版從畫面上的回覆取）：程式碼區塊換成一句說明、空白合併（同 C# 的 QuickChatRules.SpeechText） */
export const CODE_BLOCK_SPOKEN = '（這裡有一段程式碼，略過）';
