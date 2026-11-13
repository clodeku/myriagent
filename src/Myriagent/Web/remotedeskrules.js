// 遠端桌面看的一方的純規則（docs/sync-protocol.md 14.16、計畫 docs/sync-protocol.md 14.15 第 2 階段；
// 畫面在 remotedesk.js）：鍵碼換算、座標、捲輪、輸入批次、影格拼裝、觸控手勢、畫面大小。和那台的 DeskRules 照同一份格式

/** 一包輸入最多幾個事件（同 DeskRules.MaxEvents） */
export const MAX_EVENTS = 64;

/** 兩包輸入之間至少隔多久（毫秒）：瀏覽器每個工作階段每秒最多 20 個指令，確認併在輸入裡，每秒最多 15 包 */
export const SEND_GAP_MS = 66;

/** 一個文字事件最多幾個字（同 DeskRules.MaxText） */
export const MAX_TEXT = 1000;

/** 多久沒有新的影格就要求整個畫面重送（影格在路上掉了、伺服器重連） */
export const STALL_MS = 6000;

/** 那台接下之後多久沒有畫面也沒有說明，就不再只顯示「連線到…」 */
export const FIRST_FRAME_MS = 8000;

/**
 * 等第一張畫面時顯示的文字：一開始是「連線到…」；那台接下之後（waitedMs 從那時算）過了 FIRST_FRAME_MS 還沒有畫面也沒有說明，
 * 改成可能的原因（2026-11-12 使用者回報：那台鎖定中，舊版什麼都不送，手機一直停在「連線到…」。新版的電腦會自己送說明，
 * 這裡是那台還沒更新、或擷取卡住時的保險）
 */
export function waitingText(name, waitedMs) {
  if (!(waitedMs >= FIRST_FRAME_MS)) return '連線到「' + name + '」…';
  return '「' + name + '」接受了連線，但一直沒有送畫面過來。那台可能鎖定中或螢幕關著（鎖定畫面遠端看不到也操作不了，要在那台解除鎖定），'
    + '也可能那台的版本還沒更新。畫面來了這裡會自動顯示';
}

/** 畫質的選項 */
export const QUALITIES = [
  { id: 'auto', label: '自動' },
  { id: 'sharp', label: '清晰' },
  { id: 'smooth', label: '流暢' },
];

const VK = {
  Enter: 0x0d, NumpadEnter: 0x0d, Escape: 0x1b, Backspace: 0x08, Tab: 0x09, Space: 0x20,
  ShiftLeft: 0xa0, ShiftRight: 0xa1, ControlLeft: 0xa2, ControlRight: 0xa3, AltLeft: 0xa4, AltRight: 0xa5,
  MetaLeft: 0x5b, MetaRight: 0x5c, OSLeft: 0x5b, OSRight: 0x5c, ContextMenu: 0x5d, CapsLock: 0x14,
  ArrowLeft: 0x25, ArrowUp: 0x26, ArrowRight: 0x27, ArrowDown: 0x28,
  Insert: 0x2d, Delete: 0x2e, Home: 0x24, End: 0x23, PageUp: 0x21, PageDown: 0x22,
  PrintScreen: 0x2c, ScrollLock: 0x91, Pause: 0x13, NumLock: 0x90,
  NumpadMultiply: 0x6a, NumpadAdd: 0x6b, NumpadSubtract: 0x6d, NumpadDecimal: 0x6e, NumpadDivide: 0x6f,
  Minus: 0xbd, Equal: 0xbb, BracketLeft: 0xdb, BracketRight: 0xdd, Backslash: 0xdc, Semicolon: 0xba, Quote: 0xde,
  Backquote: 0xc0, Comma: 0xbc, Period: 0xbe, Slash: 0xbf, IntlBackslash: 0xe2,
  Convert: 0x1c, NonConvert: 0x1d, KanaMode: 0x15, Lang1: 0x15, Lang2: 0x19,
};

/** KeyboardEvent.code → Windows 的虛擬鍵碼（認不得回 null） */
export function vkOf(code) {
  if (typeof code !== 'string') return null;
  let m;
  if ((m = /^Key([A-Z])$/.exec(code))) return m[1].charCodeAt(0);
  if ((m = /^Digit([0-9])$/.exec(code))) return 0x30 + Number(m[1]);
  if ((m = /^Numpad([0-9])$/.exec(code))) return 0x60 + Number(m[1]);
  if ((m = /^F([1-9]|1[0-9]|2[0-4])$/.exec(code))) return 0x6f + Number(m[1]);
  return Object.prototype.hasOwnProperty.call(VK, code) ? VK[code] : null;
}

/** 送出特殊按鍵（工具列的選單）：label 與依序按下、反序放開的鍵 */
export const SPECIAL_KEYS = [
  { label: 'Ctrl + Shift + Esc（工作管理員）', keys: [0xa2, 0xa0, 0x1b] },
  { label: 'Windows 鍵（開始）', keys: [0x5b] },
  { label: 'Alt + Tab（切換視窗）', keys: [0xa4, 0x09] },
  { label: 'Alt + F4（關閉視窗）', keys: [0xa4, 0x73] },
  { label: 'Windows + D（顯示桌面）', keys: [0x5b, 0x44] },
  { label: 'Windows + E（檔案總管）', keys: [0x5b, 0x45] },
  { label: 'Ctrl + C（複製）', keys: [0xa2, 0x43] },
  { label: 'Ctrl + V（貼上）', keys: [0xa2, 0x56] },
  { label: 'Ctrl + Z（復原）', keys: [0xa2, 0x5a] },
  { label: 'Esc', keys: [0x1b] },
];

/** 一組按鍵的事件：依序按下、反序放開 */
export function comboEvents(keys) {
  const list = Array.isArray(keys) ? keys.filter((k) => Number.isInteger(k) && k >= 1 && k <= 254) : [];
  return [...list.map((k) => ({ t: 'kd', k })), ...list.slice().reverse().map((k) => ({ t: 'ku', k }))];
}

const round4 = (v) => Math.round(v * 10000) / 10000;

/** 螢幕座標 → 畫面上的比例（0～1，超出的夾回邊上）；rect 是 canvas 的 getBoundingClientRect */
export function normPoint(clientX, clientY, rect) {
  if (!rect || !(rect.width > 0) || !(rect.height > 0)) return { x: 0, y: 0 };
  const x = Math.min(1, Math.max(0, (clientX - rect.left) / rect.width));
  const y = Math.min(1, Math.max(0, (clientY - rect.top) / rect.height));
  return { x: round4(x), y: round4(y) };
}

/** 滑鼠鍵（PointerEvent.button）→ 那台的 b（0 左、1 中、2 右）；其他的鍵不送回 null */
export function buttonOf(button) {
  return button === 0 || button === 1 || button === 2 ? button : null;
}

/**
 * 捲輪（WheelEvent 的 deltaY／deltaMode）→ Windows 的捲動量（往上是正的、120 是一格）。
 * 像素模式 100 像素算一格；行模式 3 行一格；頁模式一頁三格。回 0 表示不送
 */
export function wheelUnits(delta, mode) {
  if (typeof delta !== 'number' || !Number.isFinite(delta) || delta === 0) return 0;
  const perUnit = mode === 1 ? 40 : mode === 2 ? 360 : 1.2;
  const v = Math.round(-delta * perUnit);
  return Math.max(-2400, Math.min(2400, v));
}

/**
 * 輸入的批次：滑鼠移動只留最後一個（連著的 mm 合併），其他照順序；確認只留最新的。
 * take() 回要送的 body（{ events?, ack? }）或 null（沒東西）
 */
export function createBatch() {
  let events = [];
  let ack = null;
  return {
    push(ev) {
      if (!ev || typeof ev.t !== 'string') return;
      const last = events[events.length - 1];
      if (ev.t === 'mm' && last && last.t === 'mm') events[events.length - 1] = ev;
      else events.push(ev);
    },
    ack(seq) {
      if (Number.isInteger(seq) && seq >= 0 && (ack === null || seq > ack)) ack = seq;
    },
    get size() { return events.length; },
    get full() { return events.length >= MAX_EVENTS; },
    take() {
      if (!events.length && ack === null) return null;
      const body = {};
      if (events.length) body.events = events.slice(0, MAX_EVENTS);
      if (ack !== null) body.ack = ack;
      events = events.slice(MAX_EVENTS);
      ack = null;
      return body;
    },
  };
}

/** 文字拆成一個個 tx 事件（每個 ≤ MAX_TEXT 字；控制字元只留換行與 tab） */
export function textEvents(text) {
  const clean = String(text || '').replace(/\r\n?/g, '\n').replace(/[\u0000-\u0008\u000b-\u001f\u007f]/g, '');
  const out = [];
  for (let i = 0; i < clean.length; i += MAX_TEXT) out.push({ t: 'tx', s: clean.slice(i, i + MAX_TEXT) });
  return out;
}

/**
 * 影格的拼裝：一個影格可能拆成幾包（body.part／parts），同一個 seq 的都到了才回整個影格
 * （{ seq, w, h, key, tiles, cur, screens, screen, q }）；比正在拼的 seq 新的到了，舊的丟掉（不會再來）
 */
export function createAssembler() {
  let seq = -1;
  let parts = [];
  let got = 0;
  return {
    add(packet) {
      if (!packet || !packet.body || !Number.isInteger(packet.seq)) return null;
      const b = packet.body;
      const total = Number.isInteger(b.parts) && b.parts > 0 ? b.parts : 1;
      const part = Number.isInteger(b.part) && b.part >= 0 && b.part < total ? b.part : 0;
      if (packet.seq < seq) return null;
      if (packet.seq > seq) {
        seq = packet.seq;
        parts = new Array(total).fill(null);
        got = 0;
      }
      if (parts.length !== total || parts[part]) return null;
      parts[part] = b;
      got++;
      if (got < total) return null;
      const first = parts[0];
      const last = parts[total - 1];
      const frame = {
        seq,
        w: first.w, h: first.h, key: !!first.key, screen: first.screen, q: first.q,
        screens: Array.isArray(first.screens) ? first.screens : null,
        cur: last.cur || null,
        tiles: parts.flatMap((p) => (Array.isArray(p.tiles) ? p.tiles : [])),
      };
      parts = [];
      got = 0;
      return frame;
    },
  };
}

/** 畫面放進框裡的大小（等比例、可以放大也可以縮小） */
export function fit(boxW, boxH, w, h) {
  if (!(boxW > 0) || !(boxH > 0) || !(w > 0) || !(h > 0)) return { width: 0, height: 0 };
  const scale = Math.min(boxW / w, boxH / h);
  return { width: Math.max(1, Math.floor(w * scale)), height: Math.max(1, Math.floor(h * scale)) };
}

/** 手機、平板（觸控為主）要求的傳送寬度上限：螢幕的實際像素、640～1280；電腦不要求（null，那台用預設的上限） */
export function preferredMaxWidth(viewportWidth, dpr, coarse) {
  if (!coarse) return null;
  const px = Math.round((Number(viewportWidth) || 0) * (Number(dpr) || 1));
  return Math.max(640, Math.min(1280, px || 1280));
}

/** 觸控：長按多久算右鍵、移動多少像素就不算點一下 */
export const LONG_PRESS_MS = 550;
export const TAP_SLOP_PX = 10;

/**
 * 一隻手指放開時是什麼：tap（左鍵點一下）、long（右鍵；長按時已經送了）、drag（拖曳，按下與移動已經送了）。
 * moved 是從按下到現在移動的最大距離
 */
export function touchKind(durationMs, moved, longFired) {
  if (longFired) return 'long';
  if (moved > TAP_SLOP_PX) return 'drag';
  return durationMs >= LONG_PRESS_MS ? 'long' : 'tap';
}

/** 兩隻手指上下移動 → 捲動量（手指往上滑＝內容往下捲，同手機的習慣） */
export function twoFingerWheel(dyPx) {
  if (typeof dyPx !== 'number' || !Number.isFinite(dyPx)) return 0;
  return Math.max(-2400, Math.min(2400, Math.round(dyPx * 3)));
}

/** 結束的說明 */
export function endText(body) {
  if (body && typeof body.text === 'string' && body.text) return body.text;
  return '遠端桌面已經結束';
}

/** 每秒格數（最近 2 秒內畫好的影格時間） */
export function fpsOf(times, now) {
  const recent = (Array.isArray(times) ? times : []).filter((t) => now - t <= 2000);
  return Math.round(recent.length / 2);
}

/**
 * status 的附註（body.note）：有畫面時那台另外給的一句話（例：鎖定畫面目前只能看、還不能操作），和蓋在畫面中間的 text 分開。
 * 沒帶、不是字串、空白都當沒有；太長的截到 200 字
 */
export function statusNote(body) {
  const note = body && typeof body.note === 'string' ? body.note.trim() : '';
  return note ? note.slice(0, 200) : null;
}

/** 選單項目（電腦卡片的右鍵）：條件同檔案傳輸（devicefilesrules.transferMenu 的結果） */
export function deskMenuItem(avail) {
  if (!avail) return null;
  return { enabled: !!avail.enabled, reason: avail.enabled ? null : avail.reason };
}

/** 游標圖的縮放：照「畫面在這裡顯示的寬度／那台螢幕的寬度」，但夾在 0.6～1.5 倍（4K 縮小顯示時游標才不會小到看不見） */
export function cursorScale(shownWidth, screenWidth) {
  const raw = (Number(shownWidth) || 0) / Math.max(1, Number(screenWidth) || 1);
  return Math.min(1.5, Math.max(0.6, raw || 1));
}

// ───── 放大縮小與平移（2026-11-10 擁有者要求：手機畫面太小，要能把整個桌面放大縮小） ─────
// 畫面以 transform 放大（合成器在做，捏合時順暢）：view 是 { zoom（1＝符合框的大小）, left, top（畫面左上角在框裡的位置） }

/** 最多放大幾倍（相對於符合框的大小） */
export const ZOOM_MAX = 8;

/** 工具列的放大、縮小一次幾倍 */
export const ZOOM_STEP = 1.25;

/** 夾到 1～ZOOM_MAX 倍 */
export function clampZoom(z) {
  return Math.min(ZOOM_MAX, Math.max(1, Number.isFinite(z) ? z : 1));
}

/** 起始的 view：符合框的大小、置中 */
export function fitView(fitSize, boxW, boxH) {
  return clampView({ zoom: 1, left: 0, top: 0 }, fitSize, boxW, boxH);
}

/** 夾住位置：比框小的那一邊置中，比框大的那一邊不露出空白 */
export function clampView(view, fitSize, boxW, boxH) {
  const zoom = clampZoom(view && view.zoom);
  const w = (fitSize && fitSize.width || 0) * zoom;
  const hgt = (fitSize && fitSize.height || 0) * zoom;
  const axis = (pos, size, box) => (size <= box ? (box - size) / 2 : Math.min(0, Math.max(box - size, Number(pos) || 0)));
  return { zoom, left: axis(view && view.left, w, boxW), top: axis(view && view.top, hgt, boxH) };
}

/** 以框裡的一點（px, py）為中心換成 newZoom 倍：那一點底下的內容不動 */
export function zoomAt(view, newZoom, px, py, fitSize, boxW, boxH) {
  const old = clampView(view, fitSize, boxW, boxH);
  const zoom = clampZoom(newZoom);
  const w = fitSize.width * old.zoom;
  const hgt = fitSize.height * old.zoom;
  const u = w > 0 ? (px - old.left) / w : 0.5;
  const v = hgt > 0 ? (py - old.top) / hgt : 0.5;
  return clampView({ zoom, left: px - u * fitSize.width * zoom, top: py - v * fitSize.height * zoom }, fitSize, boxW, boxH);
}

/** 平移（手指或滑鼠移了 dx、dy） */
export function panBy(view, dx, dy, fitSize, boxW, boxH) {
  return clampView({ zoom: view.zoom, left: view.left + dx, top: view.top + dy }, fitSize, boxW, boxH);
}

/** 滑鼠貼著框的邊緣時往那邊捲（放大時看得到畫面外的部分）：回這一格要移的 dx、dy（越貼近邊緣越快） */
export function edgePan(x, y, boxW, boxH, margin = 40, maxStep = 18) {
  const step = (pos, size) => {
    if (pos < margin) return Math.round(maxStep * (1 - Math.max(0, pos) / margin));
    if (pos > size - margin) return -Math.round(maxStep * (1 - Math.max(0, size - pos) / margin));
    return 0;
  };
  return { dx: step(x, boxW), dy: step(y, boxH) };
}

/** 兩隻手指從 prev 移到 cur（各兩點）：距離的比例、中心點與中心的移動 */
export function twoFingerStep(prev, cur) {
  const dist = (p) => Math.hypot(p[1].x - p[0].x, p[1].y - p[0].y);
  const mid = (p) => ({ x: (p[0].x + p[1].x) / 2, y: (p[0].y + p[1].y) / 2 });
  const d0 = dist(prev);
  const d1 = dist(cur);
  const m0 = mid(prev);
  const m1 = mid(cur);
  return { scale: d0 > 0 ? d1 / d0 : 1, cx: m1.x, cy: m1.y, dx: m1.x - m0.x, dy: m1.y - m0.y };
}

/**
 * 兩隻手指的手勢是什麼（從放下開始累計）：距離變了超過 16 像素＝捏合縮放；中心移了超過 10 像素＝放大時平移、沒放大時捲動那台的畫面；
 * 都還沒到回 null（再看）
 */
export function twoFingerKind(distChange, centerMove, zoom) {
  if (Math.abs(distChange) > 16) return 'pinch';
  if (centerMove > 10) return zoom > 1.001 ? 'pan' : 'scroll';
  return null;
}

/** 工具列顯示的倍數：1 倍是「符合視窗」 */
export function zoomLabel(zoom) {
  return zoom <= 1.001 ? '符合視窗' : Math.round(zoom * 100) + '%';
}

/**
 * 要求那台傳多寬的畫面（裝置像素：框寬 × 螢幕倍率 × 放大倍數，取 1280／1920／2560／3840 裡夠用的最小一級）。
 * 沒放大時：手機照 preferredMaxWidth（小一點、省頻寬），電腦是預設的 1920
 */
export function wantedMaxWidth(boxW, dpr, zoom, coarse) {
  if (!(zoom > 1.001)) return preferredMaxWidth(boxW, dpr, coarse) ?? 1920;
  const need = (Number(boxW) || 0) * (Number(dpr) || 1) * zoom * 0.9;
  for (const level of [1280, 1920, 2560, 3840]) if (level >= need) return level;
  return 3840;
}
