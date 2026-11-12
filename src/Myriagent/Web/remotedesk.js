// 遠端桌面看的一方（docs/sync-protocol.md 14.16、計畫 docs/sync-protocol.md 14.15 第 2 階段；2026-11-09 擁有者要求
// 「必需高效能，桌面手機版也能使用、反應快速」）：電腦卡片的右鍵選單開一個蓋滿畫面的檢視，deskOpen 之後收 desk 影格畫在 canvas 上
// （JPEG 區塊在背景解碼、依序畫、畫完才確認），滑鼠、鍵盤、捲輪、觸控換成輸入事件，和確認併成一包、每 66 ms 最多送一包。
// 那台的游標另外送（位置與形狀），疊在畫面上；手機要求小一點的傳送寬度。規則在 remotedeskrules.js；文字一律 textContent，class 一律 rd- 前綴
import { h, setHidden } from './util.js';
import { icon } from './icons.js';
import { post, isHub, peerId } from './store.js';
import { openMenu, toast } from './ui.js';
import { track } from './cmdtrack.js';
import * as R from './remotedeskrules.js';

/** 目前的工作階段（同時只開一個） */
let s = null;

const coarse = () => typeof window.matchMedia === 'function' && window.matchMedia('(pointer: coarse)').matches;

/** 開那台的遠端桌面（name 是電腦名稱） */
export function openRemoteDesk(deviceId, name) {
  if (s) {
    if (s.deviceId === deviceId) { s.stage.focus(); return; }
    toast('一次只能遠端操控一台電腦：先關掉「' + s.name + '」的遠端桌面', 'info');
    return;
  }
  const id = peerId();
  s = {
    id, deviceId, name: name || '那台電腦',
    assembler: R.createAssembler(), batch: R.createBatch(), sendTimer: 0, lastSendAt: 0,
    drawing: Promise.resolve(), lastFrameAt: 0, frameTimes: [], bytes: 0,
    w: 0, h: 0, screens: [], screen: 0, quality: 'auto', cursorImg: null, cursorHot: { x: 0, y: 0 }, cursorScale: 1, cursor: null,
    keysDown: new Set(), touch: null, closed: false, opened: false, openedAt: 0, statusText: null, statusLocal: false,
    // 放大縮小（2026-11-10）：view 是 transform 的倍數與位置；fit 是符合框的大小；sentMaxWidth 是要求那台傳的寬度（那台預設 1920）
    view: { zoom: 1, left: 0, top: 0 }, fit: { width: 0, height: 0 }, box: { w: 0, h: 0 }, sentMaxWidth: 1920, sharpTimer: 0,
    pointers: new Map(), edge: null, edgeRaf: 0, lastMouse: null,
  };
  build(s);
  setStatus(R.waitingText(s.name, 0), true);
  const cid = post({ cmd: 'deskOpen', deviceId, requestId: id });
  if (cid === null) { end('沒有送出：目前沒有連上伺服器'); return; }
  const mine = s;
  track(cid, {
    onSettle: (state, code) => {
      if (s !== mine) return;
      if (state !== 'done') { end(openFailText(code)); return; }
      mine.opened = true;
      mine.openedAt = performance.now();
      // 手機：要求小一點的傳送寬度（省頻寬、畫面更快）；放大之後再要求清楚一點的
      requestSharpness(mine);
    },
  });
  mine.stallTimer = setInterval(() => checkStall(mine), 1000);
}

function openFailText(code) {
  switch (code) {
    case 'policy': return '那台不允許遠端操控（遠端操作政策要是「可以操作」，app 也不能以系統管理員執行）';
    case 'unsupported': return '那台的版本還不支援遠端桌面，請先更新那台';
    case 'conflict': return '已經有人在遠端操控那台電腦';
    case 'offline': return '那台電腦離線';
    default: return '開不了遠端桌面（' + (code || '結果不明') + '）';
  }
}

/** 宿主或伺服器送來的 desk 訊息 */
export function onDesk(m) {
  if (!s || !m || m.sessionId !== s.id) return;
  switch (m.kind) {
    case 'frame': {
      s.bytes += JSON.stringify(m.body || {}).length;
      const frame = s.assembler.add(m);
      if (frame) queueFrame(s, frame);
      break;
    }
    case 'status':
      setStatus(m.body && typeof m.body.text === 'string' ? m.body.text : null);
      break;
    case 'end':
      end(R.endText(m.body), false);
      break;
  }
}

// ───── 畫面 ─────

function build(s) {
  s.canvas = h('canvas', { class: 'rd-canvas', width: 16, height: 9 });
  s.ctx = s.canvas.getContext('2d', { alpha: false, desynchronized: true });
  s.cursorEl = h('img', { class: 'rd-cursor', alt: '', draggable: false });
  setHidden(s.cursorEl, true);
  s.statusEl = h('div', { class: 'rd-status' });
  s.stage = h('div', { class: 'rd-stage', tabIndex: 0, 'aria-label': '「' + s.name + '」的畫面' }, s.canvas, s.cursorEl, s.statusEl);
  s.keyInput = h('textarea', { class: 'rd-keyinput', 'aria-label': '打字到遠端', autocapitalize: 'off', autocomplete: 'off', spellcheck: false });
  s.info = h('span', { class: 'rd-info' });
  s.screenBtn = toolBtn('desktop', '螢幕', () => pickScreen(s));
  setHidden(s.screenBtn, true);
  s.qualityBtn = toolBtn('gauge', '畫質：自動', () => pickQuality(s));
  const keysBtn = toolBtn('keyboard', '特殊按鍵', () => pickKeys(s, keysBtn));
  const typeBtn = toolBtn('edit', '打字', () => { s.keyInput.focus(); });
  setHidden(typeBtn, !coarse());
  const fullBtn = toolBtn('appWindow', '全螢幕', () => toggleFullscreen(s));
  // 放大縮小：縮小、目前的倍數（按了回到符合視窗）、放大；滑鼠是 Ctrl＋滾輪（觸控板的兩指捏合也是），手機是兩指捏合
  const zoomOutBtn = iconTool('zoomOut', '縮小', () => zoomBy(s, 1 / R.ZOOM_STEP));
  s.zoomLabel = h('button', { type: 'button', class: 'btn rd-zoom', title: '回到符合視窗的大小', text: R.zoomLabel(1) });
  s.zoomLabel.addEventListener('click', () => setView(s, R.fitView(s.fit, s.box.w, s.box.h)));
  const zoomInBtn = iconTool('zoomIn', '放大', () => zoomBy(s, R.ZOOM_STEP));
  const closeBtn = h('button', { type: 'button', class: 'btn rd-close' }, icon('x'), h('span', { text: '中斷' }));
  closeBtn.addEventListener('click', () => end(null));
  s.bar = h('div', { class: 'rd-bar' },
    h('span', { class: 'rd-title', text: '遠端桌面：' + s.name }), s.info,
    h('span', { class: 'rd-spacer' }), h('span', { class: 'rd-zoomgroup' }, zoomOutBtn, s.zoomLabel, zoomInBtn),
    s.screenBtn, s.qualityBtn, keysBtn, typeBtn, fullBtn, closeBtn);
  s.root = h('div', { class: 'rd-wrap', role: 'dialog', 'aria-modal': 'true', 'aria-label': '遠端桌面：' + s.name }, s.bar, s.stage, s.keyInput);
  document.body.append(s.root);
  wireInput(s);
  s.resize = () => layout(s);
  window.addEventListener('resize', s.resize);
  s.stage.focus();
}

function iconTool(iconName, label, onClick) {
  const b = h('button', { type: 'button', class: 'btn rd-tool rd-icon', title: label, 'aria-label': label }, icon(iconName));
  b.addEventListener('click', onClick);
  return b;
}

function toolBtn(iconName, label, onClick) {
  const b = h('button', { type: 'button', class: 'btn rd-tool', title: label }, icon(iconName), h('span', { class: 'rd-tool-label', text: label }));
  b.addEventListener('click', onClick);
  return b;
}

/** local：這一頁自己的等待文字（第一張畫面來了就收掉）；那台送來的說明由那台自己清掉 */
function setStatus(text, local = false) {
  if (!s) return;
  s.statusText = text;
  s.statusLocal = !!text && local;
  s.statusEl.textContent = text || '';
  setHidden(s.statusEl, !text);
}

/**
 * canvas 放進舞台裡：canvas 本身是「符合框」的大小，放大與平移以 transform 做（合成器在做，捏合時不重排）；
 * 框的大小或畫面的寬高比變了就重算、夾住位置
 */
function layout(s) {
  if (!s.w || !s.h) return;
  const box = s.stage.getBoundingClientRect();
  s.box = { w: box.width, h: box.height };
  s.fit = R.fit(box.width, box.height, s.w, s.h);
  s.canvas.style.width = s.fit.width + 'px';
  s.canvas.style.height = s.fit.height + 'px';
  setView(s, s.view);
}

/** 套用 view（夾住之後）：canvas 的 transform、倍數的文字、游標；倍數變了就看要不要請那台傳清楚一點的畫面 */
function setView(s, view) {
  const before = s.view.zoom;
  s.view = R.clampView(view, s.fit, s.box.w, s.box.h);
  s.canvas.style.transform = 'translate(' + s.view.left.toFixed(1) + 'px,' + s.view.top.toFixed(1) + 'px) scale(' + s.view.zoom.toFixed(4) + ')';
  s.zoomLabel.textContent = R.zoomLabel(s.view.zoom);
  s.root.classList.toggle('rd-zoomed', s.view.zoom > 1.001);
  placeCursor(s);
  if (Math.abs(before - s.view.zoom) > 0.001) requestSharpness(s);
}

/** 以框的正中間（或給的那一點）放大縮小 */
function zoomBy(s, factor, px = s.box.w / 2, py = s.box.h / 2) {
  setView(s, R.zoomAt(s.view, s.view.zoom * factor, px, py, s.fit, s.box.w, s.box.h));
}

/** 放大之後請那台傳清楚一點（寬一點）的畫面、縮回來再請它傳小一點的；停下 0.5 秒才送（每次都會整個畫面重送） */
function requestSharpness(s) {
  clearTimeout(s.sharpTimer);
  s.sharpTimer = setTimeout(() => {
    if (!s.opened || s.closed) return;
    const want = R.wantedMaxWidth(s.box.w || window.innerWidth, window.devicePixelRatio, s.view.zoom, coarse());
    if (want === s.sentMaxWidth) return;
    s.sentMaxWidth = want;
    sendPacket('config', { maxWidth: want });
  }, s.opened && s.sentMaxWidth === 1920 && s.view.zoom <= 1.001 ? 0 : 500);
}

function placeCursor(s) {
  const c = s.cursor;
  if (!c || !c.v || !s.cursorImg || !s.w) { setHidden(s.cursorEl, true); return; }
  const rect = s.canvas.getBoundingClientRect();
  const stage = s.stage.getBoundingClientRect();
  // 游標的圖是那台的實際像素：依「畫面在這裡顯示的寬度／那台螢幕的寬度」縮放
  const scale = R.cursorScale(rect.width, s.cursorScale);
  const x = rect.left - stage.left + c.x * rect.width - s.cursorHot.x * scale;
  const y = rect.top - stage.top + c.y * rect.height - s.cursorHot.y * scale;
  s.cursorEl.style.transform = 'translate(' + Math.round(x) + 'px,' + Math.round(y) + 'px) scale(' + scale.toFixed(3) + ')';
  setHidden(s.cursorEl, false);
}

/** 影格依序畫：區塊在背景解碼（createImageBitmap），整個影格解完才一起畫、畫完才確認 */
function queueFrame(s, frame) {
  s.drawing = s.drawing.then(async () => {
    if (s.closed) return;
    const bitmaps = await Promise.all(frame.tiles.map(async (t) => {
      try {
        return { t, bmp: await createImageBitmap(new Blob([b64(t.d)], { type: 'image/jpeg' })) };
      } catch {
        return null;
      }
    }));
    if (s.closed) { for (const b of bitmaps) if (b) b.bmp.close(); return; }
    if (frame.w !== s.w || frame.h !== s.h) {
      s.w = frame.w;
      s.h = frame.h;
      s.canvas.width = frame.w;
      s.canvas.height = frame.h;
      layout(s);
    }
    for (const b of bitmaps) {
      if (!b) continue;
      s.ctx.drawImage(b.bmp, b.t.x, b.t.y);
      b.bmp.close();
    }
    if (frame.screens) {
      s.screens = frame.screens;
      s.screen = Number.isInteger(frame.screen) ? frame.screen : 0;
      setHidden(s.screenBtn, s.screens.length < 2);
    }
    if (frame.cur) applyCursor(s, frame.cur);
    if (s.statusLocal) setStatus(null);
    const now = performance.now();
    s.lastFrameAt = now;
    s.frameTimes.push(now);
    if (s.frameTimes.length > 60) s.frameTimes.shift();
    s.info.textContent = R.fpsOf(s.frameTimes, now) + ' fps';
    s.batch.ack(frame.seq);
    scheduleSend(s);
  });
}

function applyCursor(s, cur) {
  if (typeof cur.img === 'string') {
    s.cursorImg = cur.img;
    s.cursorEl.src = 'data:image/png;base64,' + cur.img;
    s.cursorHot = { x: Number(cur.hx) || 0, y: Number(cur.hy) || 0 };
    s.cursorScale = Number(cur.sw) || s.w;
  }
  s.cursor = { x: Number(cur.x) || 0, y: Number(cur.y) || 0, v: !!cur.v };
  placeCursor(s);
}

function b64(text) {
  const bin = atob(text);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

/** 很久沒有新的影格（掉了、伺服器重連）：確認目前的、要求整個畫面重送 */
function checkStall(mine) {
  if (s !== mine || !mine.opened || mine.closed) return;
  const now = performance.now();
  // 那台接下了卻一直沒有畫面也沒有說明：不要一直停在「連線到…」
  if (!mine.lastFrameAt && mine.statusLocal) {
    const waiting = R.waitingText(mine.name, now - mine.openedAt);
    if (waiting !== mine.statusText) setStatus(waiting, true);
  }
  if (mine.lastFrameAt && now - mine.lastFrameAt > R.STALL_MS) {
    mine.lastFrameAt = now;
    sendPacket('config', { key: true });
  }
  mine.info.textContent = R.fpsOf(mine.frameTimes, now) + ' fps';
}

// ───── 輸入 ─────

function wireInput(s) {
  const stage = s.stage;
  const point = (e) => R.normPoint(e.clientX, e.clientY, s.canvas.getBoundingClientRect());
  stage.addEventListener('contextmenu', (e) => e.preventDefault());
  stage.addEventListener('pointerdown', (e) => {
    stage.focus();
    if (e.pointerType === 'touch') { touchDown(s, e); return; }
    const b = R.buttonOf(e.button);
    if (b === null) return;
    e.preventDefault();
    try { stage.setPointerCapture(e.pointerId); } catch { /* 忽略 */ }
    input(s, { t: 'md', b, ...point(e) });
  });
  stage.addEventListener('pointermove', (e) => {
    if (e.pointerType === 'touch') { touchMove(s, e); return; }
    s.lastMouse = { x: e.clientX, y: e.clientY };
    edgeCheck(s, e);
    input(s, { t: 'mm', ...point(e) });
  });
  stage.addEventListener('pointerleave', () => { s.edge = null; });
  stage.addEventListener('pointerup', (e) => {
    if (e.pointerType === 'touch') { touchUp(s, e); return; }
    const b = R.buttonOf(e.button);
    if (b !== null) input(s, { t: 'mu', b, ...point(e) });
  });
  stage.addEventListener('pointercancel', (e) => {
    if (e.pointerType !== 'touch') return;
    s.pointers.delete(e.pointerId);
    if (!s.pointers.size) s.touch = null;
  });
  stage.addEventListener('wheel', (e) => {
    e.preventDefault();
    // Ctrl＋滾輪（觸控板的兩指捏合也是這樣送）：放大縮小這裡的畫面，不送給那台
    if (e.ctrlKey) {
      const r = s.stage.getBoundingClientRect();
      zoomBy(s, Math.exp(-e.deltaY * (e.deltaMode === 1 ? 0.05 : 0.0025)), e.clientX - r.left, e.clientY - r.top);
      return;
    }
    const dy = R.wheelUnits(e.deltaY, e.deltaMode);
    const dx = -R.wheelUnits(e.deltaX, e.deltaMode);
    if (dy || dx) input(s, { t: 'wh', dy, dx });
  }, { passive: false });
  stage.addEventListener('keydown', (e) => {
    if (e.isComposing || e.keyCode === 229) return;
    const k = R.vkOf(e.code);
    e.preventDefault();
    e.stopPropagation();
    if (k === null) {
      if (e.key && e.key.length === 1) for (const ev of R.textEvents(e.key)) input(s, ev);
      return;
    }
    s.keysDown.add(k);
    input(s, { t: 'kd', k });
  });
  stage.addEventListener('keyup', (e) => {
    const k = R.vkOf(e.code);
    e.preventDefault();
    e.stopPropagation();
    if (k === null) return;
    s.keysDown.delete(k);
    input(s, { t: 'ku', k });
  });
  // 離開畫面（切到別的視窗、點工具列）：放開還按著的鍵，免得那台的 Ctrl 一直按著
  stage.addEventListener('blur', () => releaseKeys(s));
  // 打字框（手機的鍵盤、輸入法）：輸入完成的文字以 tx 送
  s.keyInput.addEventListener('input', () => {
    if (s.keyInput.value) { for (const ev of R.textEvents(s.keyInput.value)) input(s, ev); s.keyInput.value = ''; }
  });
  s.keyInput.addEventListener('keydown', (e) => {
    if (e.isComposing || e.keyCode === 229) return;
    if (e.key === 'Backspace' || e.key === 'Enter' || e.key === 'Tab' || e.key.startsWith('Arrow') || e.key === 'Escape') {
      e.preventDefault();
      e.stopPropagation();
      const k = R.vkOf(e.code) ?? R.vkOf(e.key);
      if (k !== null) for (const ev of R.comboEvents([k])) input(s, ev);
    }
  });
}

/** 放大時滑鼠貼著框的邊緣：每個影格往那邊捲一點，那台的游標跟著留在滑鼠底下 */
function edgeCheck(s, e) {
  if (s.view.zoom <= 1.001) { s.edge = null; return; }
  const r = s.stage.getBoundingClientRect();
  const d = R.edgePan(e.clientX - r.left, e.clientY - r.top, r.width, r.height);
  s.edge = d.dx || d.dy ? d : null;
  if (s.edge && !s.edgeRaf) s.edgeRaf = requestAnimationFrame(() => edgeTick(s));
}

function edgeTick(s) {
  s.edgeRaf = 0;
  if (!s.edge || s.closed || s.view.zoom <= 1.001) return;
  const before = s.view;
  setView(s, R.panBy(s.view, s.edge.dx, s.edge.dy, s.fit, s.box.w, s.box.h));
  if (before.left === s.view.left && before.top === s.view.top) return; // 到邊了
  if (s.lastMouse) input(s, { t: 'mm', ...R.normPoint(s.lastMouse.x, s.lastMouse.y, s.canvas.getBoundingClientRect()) });
  s.edgeRaf = requestAnimationFrame(() => edgeTick(s));
}

function releaseKeys(s) {
  for (const k of s.keysDown) input(s, { t: 'ku', k });
  s.keysDown.clear();
}

function input(s, ev) {
  if (!s.opened || s.closed) return;
  s.batch.push(ev);
  scheduleSend(s);
}

/** 輸入與確認併成一包：距上一包 66 ms 以上就馬上送，否則等到那時候 */
function scheduleSend(s) {
  if (s.closed) return;
  if (s.batch.full) { flush(s); return; }
  if (s.sendTimer) return;
  const wait = Math.max(0, R.SEND_GAP_MS - (performance.now() - s.lastSendAt));
  s.sendTimer = setTimeout(() => { s.sendTimer = 0; flush(s); }, wait);
}

function flush(s) {
  const body = s.batch.take();
  if (!body || s.closed) return;
  s.lastSendAt = performance.now();
  sendPacket('input', body);
  if (s.batch.size) scheduleSend(s);
}

let seq = 0;

function sendPacket(kind, body) {
  if (!s) return;
  post({ cmd: 'desk', sessionId: s.id, kind, seq: ++seq, body });
}

// ───── 觸控：點一下＝左鍵、長按＝右鍵、單指拖曳＝拖曳；兩指捏合＝放大縮小，兩指移動＝放大時平移、沒放大時捲動那台的畫面 ─────

function twoPoints(s) {
  return [...s.pointers.values()].slice(0, 2).map((p) => ({ x: p.x, y: p.y }));
}

function touchDown(s, e) {
  e.preventDefault();
  s.pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
  const t = s.touch;
  if (t && s.pointers.size === 2) {
    // 第二隻手指：不再是點、長按或拖曳（拖曳到一半的先放開）
    clearTimeout(t.longTimer);
    if (t.mode === 'drag') input(s, { t: 'mu', b: 0, ...t.last });
    const pts = twoPoints(s);
    t.mode = 'two';
    t.two = { start: pts, prev: pts, kind: null };
    return;
  }
  if (t) return;
  const p = R.normPoint(e.clientX, e.clientY, s.canvas.getBoundingClientRect());
  const one = { id: e.pointerId, x0: e.clientX, y0: e.clientY, at: performance.now(), moved: 0, mode: null, p, last: p, longFired: false };
  one.longTimer = setTimeout(() => {
    if (s.touch !== one || one.mode) return;
    one.longFired = true;
    input(s, { t: 'mm', ...one.p });
    for (const ev of [{ t: 'md', b: 2, ...one.p }, { t: 'mu', b: 2, ...one.p }]) input(s, ev);
  }, R.LONG_PRESS_MS);
  s.touch = one;
}

function touchMove(s, e) {
  if (s.pointers.has(e.pointerId)) s.pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
  const t = s.touch;
  if (!t) return;
  if (t.mode === 'two') {
    if (s.pointers.size < 2) return;
    const cur = twoPoints(s);
    if (!t.two.kind) {
      const total = R.twoFingerStep(t.two.start, cur);
      const d0 = Math.hypot(t.two.start[1].x - t.two.start[0].x, t.two.start[1].y - t.two.start[0].y);
      t.two.kind = R.twoFingerKind(total.scale * d0 - d0, Math.hypot(total.dx, total.dy), s.view.zoom);
      if (!t.two.kind) return;
      t.two.prev = t.two.start;
    }
    const step = R.twoFingerStep(t.two.prev, cur);
    t.two.prev = cur;
    const r = s.stage.getBoundingClientRect();
    if (t.two.kind === 'pinch') {
      // 捏合：以兩指的中心放大縮小，中心移動跟著平移
      const zoomed = R.zoomAt(s.view, s.view.zoom * step.scale, step.cx - r.left, step.cy - r.top, s.fit, s.box.w, s.box.h);
      setView(s, R.panBy(zoomed, step.dx, step.dy, s.fit, s.box.w, s.box.h));
    } else if (t.two.kind === 'pan') {
      setView(s, R.panBy(s.view, step.dx, step.dy, s.fit, s.box.w, s.box.h));
    } else {
      const wheel = R.twoFingerWheel(step.dy);
      if (wheel) input(s, { t: 'wh', dy: wheel });
    }
    return;
  }
  if (e.pointerId !== t.id) return;
  t.moved = Math.max(t.moved, Math.hypot(e.clientX - t.x0, e.clientY - t.y0));
  const p = R.normPoint(e.clientX, e.clientY, s.canvas.getBoundingClientRect());
  t.last = p;
  if (!t.mode && t.moved > R.TAP_SLOP_PX && !t.longFired) {
    clearTimeout(t.longTimer);
    t.mode = 'drag';
    input(s, { t: 'mm', ...t.p });
    input(s, { t: 'md', b: 0, ...t.p });
  }
  if (t.mode === 'drag') input(s, { t: 'mm', ...p });
}

function touchUp(s, e) {
  s.pointers.delete(e.pointerId);
  const t = s.touch;
  if (!t) return;
  if (t.mode === 'two') {
    // 兩指的手勢等手指都放開才結束（中途放開一隻不會變成點擊）
    if (!s.pointers.size) s.touch = null;
    return;
  }
  if (e.pointerId !== t.id) return;
  clearTimeout(t.longTimer);
  s.touch = null;
  const p = R.normPoint(e.clientX, e.clientY, s.canvas.getBoundingClientRect());
  const kind = t.mode === 'drag' ? 'drag' : R.touchKind(performance.now() - t.at, t.moved, t.longFired);
  if (kind === 'drag') input(s, { t: 'mu', b: 0, ...p });
  else if (kind === 'tap') for (const ev of [{ t: 'mm', ...t.p }, { t: 'md', b: 0, ...t.p }, { t: 'mu', b: 0, ...t.p }]) input(s, ev);
}

// ───── 工具列 ─────

function pickScreen(s) {
  openMenu(s.screenBtn, s.screens.map((sc) => ({
    label: (sc.name || '螢幕 ' + (sc.i + 1)) + '（' + sc.w + '×' + sc.h + (sc.primary ? '，主螢幕' : '') + '）',
    icon: sc.i === s.screen ? 'check' : 'desktop',
    onSelect: () => { s.screen = sc.i; sendPacket('config', { screen: sc.i }); },
  })), { className: 'rd-menu' });
}

function pickQuality(s) {
  openMenu(s.qualityBtn, R.QUALITIES.map((q) => ({
    label: q.label, icon: q.id === s.quality ? 'check' : 'gauge',
    onSelect: () => {
      s.quality = q.id;
      s.qualityBtn.querySelector('.rd-tool-label').textContent = '畫質：' + q.label;
      sendPacket('config', { quality: q.id });
    },
  })), { className: 'rd-menu' });
}

function pickKeys(s, anchor) {
  openMenu(anchor, R.SPECIAL_KEYS.map((k) => ({
    label: k.label, icon: 'keyboard',
    onSelect: () => { for (const ev of R.comboEvents(k.keys)) input(s, ev); s.stage.focus(); },
  })), { className: 'rd-menu' });
}

function toggleFullscreen(s) {
  if (document.fullscreenElement) { document.exitFullscreen().catch(() => {}); return; }
  if (!s.root.requestFullscreen) return;
  s.root.requestFullscreen().then(() => {
    // 全螢幕時接管 Esc、Alt+Tab 這類系統按鍵（Chrome、Edge 支援）
    if (navigator.keyboard && typeof navigator.keyboard.lock === 'function') navigator.keyboard.lock().catch(() => {});
    s.stage.focus();
  }).catch(() => {});
}

// ───── 結束 ─────

/** 結束（text 是給使用者看的說明，null＝自己按了中斷）；notifyTarget：送 close 給那台 */
function end(text, notifyTarget = true) {
  const mine = s;
  if (!mine) return;
  mine.closed = true;
  if (notifyTarget) {
    releaseKeys(mine);
    flush(mine);
    post({ cmd: 'desk', sessionId: mine.id, kind: 'close', seq: ++seq, body: { reason: 'viewerClosed', text: '看的一方結束了遠端桌面' } });
  }
  clearTimeout(mine.sendTimer);
  clearTimeout(mine.sharpTimer);
  if (mine.edgeRaf) cancelAnimationFrame(mine.edgeRaf);
  clearInterval(mine.stallTimer);
  window.removeEventListener('resize', mine.resize);
  if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
  if (navigator.keyboard && typeof navigator.keyboard.unlock === 'function') navigator.keyboard.unlock();
  mine.root.remove();
  s = null;
  if (text) toast(text, 'info');
}

/** 瀏覽器版重新連上（init）：伺服器那邊的工作階段已經跟著舊連線結束 */
export function onReconnect() {
  if (s && isHub) end('和伺服器的連線中斷，遠端桌面已經結束', false);
}

/** 測試與探針用：目前的工作階段 id */
export function currentSessionId() {
  return s ? s.id : null;
}
