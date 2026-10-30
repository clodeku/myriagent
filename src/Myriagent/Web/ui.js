// 共用介面元件：選單、對話框、確認框、提示（toast）、浮動說明、開關、分段控制
import { h, iconButton, clamp, replayClass } from './util.js';
import { icon } from './icons.js';
import { calmMotion } from './theme.js';

// ───── 圖層堆疊（Esc 由上往下關） ─────
const layers = [];
export function pushLayer(layer) { layers.push(layer); }
export function removeLayer(layer) {
  const i = layers.indexOf(layer);
  if (i >= 0) layers.splice(i, 1);
}
export const hasLayer = () => layers.length > 0 || !!currentMenu;

/** Esc：先關選單，再關最上層的對話框／燈箱；有處理回 true */
export function handleEscape() {
  if (currentMenu) { closeMenu(); return true; }
  const top = layers[layers.length - 1];
  if (top && top.dismissable !== false) { top.close(); return true; }
  return false;
}

// ───── 選單 ─────
let currentMenu = null;

export function closeMenu() {
  if (!currentMenu) return;
  const m = currentMenu;
  currentMenu = null;
  m.el.remove();
  if (m.anchor) { m.anchor.classList.remove('is-open'); m.anchor.setAttribute('aria-expanded', 'false'); }
  document.removeEventListener('pointerdown', m.onDocDown, true);
  window.removeEventListener('resize', closeMenu);
  window.removeEventListener('blur', closeMenu);
  document.removeEventListener('scroll', m.onScroll, true);
  if (m.restoreFocus && document.activeElement && (document.activeElement === document.body || m.el.contains(document.activeElement))) {
    try { m.restoreFocus.focus({ preventScroll: true }); } catch { /* 忽略 */ }
  }
  if (m.onClose) m.onClose();
}

/**
 * 開選單。items：{ label, desc, hint, icon, color, checked, disabled, danger, onSelect, keepOpen, title, cls（多加在這一項上的 class） }
 * 或 { type: 'sep' }／{ type: 'header', label }／{ type: 'custom', node }。
 * opts：{ placement: 'bottom-start'|'bottom-end'|'top-start'|'top-end', at: {x,y}, className, minWidth, onClose, focus, restoreFocus }
 * restoreFocus 預設是錨點（右鍵選單是開啟前聚焦的元素）：選單移除後焦點不會掉到 body，從選單開出的對話框關閉後也回得來
 */
export function openMenu(anchor, items, opts = {}) {
  const reopenSame = currentMenu && anchor && currentMenu.anchor === anchor;
  closeMenu();
  if (reopenSame && !opts.force) return null; // 再按一次同一顆按鈕＝關閉
  const before = document.activeElement;
  const restoreFocus = opts.restoreFocus || anchor || (before && before !== document.body ? before : null);
  const hasCheck = items.some((it) => it && it.checked !== undefined && !it.type);
  const menu = h('div', { class: 'menu' + (hasCheck ? ' has-check' : '') + (opts.className ? ' ' + opts.className : ''), role: 'menu', tabIndex: -1 });
  if (opts.minWidth) menu.style.minWidth = opts.minWidth + 'px';
  // 選到的那一項左緣的小方塊用開選單那顆按鈕的帳戶色（按鈕在對話或側欄的列裡會繼承 --acc；選單掛在 body 底下拿不到，fx.css）
  const acc = anchor ? getComputedStyle(anchor).getPropertyValue('--acc').trim() : '';
  if (acc) menu.style.setProperty('--menu-acc', acc);
  const entries = [];
  let active = -1;
  const setActive = (i, focus) => {
    if (active >= 0 && entries[active]) entries[active].classList.remove('active');
    active = i;
    if (i >= 0 && entries[i]) {
      entries[i].classList.add('active');
      if (focus) entries[i].focus({ preventScroll: false });
    }
  };
  for (const it of items) {
    if (!it) continue;
    if (it.type === 'sep') { menu.append(h('div', { class: 'menu-sep', role: 'separator' })); continue; }
    if (it.type === 'header') { menu.append(h('div', { class: 'menu-head', text: it.label })); continue; }
    if (it.type === 'custom') { menu.append(it.node); continue; }
    const btn = h('button', {
      type: 'button',
      class: 'menu-item' + (it.danger ? ' danger' : '') + (it.checked ? ' checked' : '') + (it.desc ? ' has-desc' : '') + (it.cls ? ' ' + it.cls : ''),
      role: it.checked !== undefined ? 'menuitemradio' : 'menuitem',
      'aria-checked': it.checked !== undefined ? String(!!it.checked) : null,
      disabled: !!it.disabled,
      title: it.title || null,
    });
    if (hasCheck) btn.append(h('span', { class: 'mi-check' }, it.checked ? icon('check') : null));
    if (it.color) btn.append(h('span', { class: 'mi-dot', style: { '--acc': it.color } }));
    else if (it.icon) btn.append(icon(it.icon, 'mi-ic'));
    btn.append(h('span', { class: 'mi-text' },
      h('span', { class: 'mi-label', text: it.label }),
      it.desc ? h('span', { class: 'mi-desc', text: it.desc }) : null));
    if (it.hint) btn.append(h('span', { class: 'mi-hint', text: it.hint }));
    btn.addEventListener('click', (e) => {
      e.stopPropagation();
      if (!it.keepOpen) closeMenu();
      if (it.onSelect) it.onSelect(e);
    });
    btn.addEventListener('pointermove', () => { const i = entries.indexOf(btn); if (i !== active) setActive(i, false); });
    entries.push(btn);
    menu.append(btn);
  }

  menu.addEventListener('keydown', (e) => {
    const enabled = entries.map((b, i) => (b.disabled ? -1 : i)).filter((i) => i >= 0);
    if (!enabled.length) return;
    const pos = enabled.indexOf(active);
    if (e.key === 'ArrowDown') { e.preventDefault(); setActive(enabled[(pos + 1) % enabled.length], true); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setActive(enabled[pos < 0 ? enabled.length - 1 : (pos - 1 + enabled.length) % enabled.length], true); } // 還沒有作用項目：跳到最後一個
    else if (e.key === 'Home') { e.preventDefault(); setActive(enabled[0], true); }
    else if (e.key === 'End') { e.preventDefault(); setActive(enabled[enabled.length - 1], true); }
    else if (e.key === 'Tab') { e.preventDefault(); closeMenu(); }
    else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); closeMenu(); }
  });

  document.body.append(menu);
  placeFloating(menu, anchor, opts);

  const onDocDown = (e) => {
    if (menu.contains(e.target)) return;
    if (anchor && anchor.contains(e.target)) return; // 按鈕自己會切換
    closeMenu();
  };
  // 捲動：錨點真的移動了才關（慣性捲動或剛結束的程式捲動不該把剛開的選單關掉）
  const openedAt = performance.now();
  const r0 = anchor ? anchor.getBoundingClientRect() : null;
  const onScroll = (e) => {
    if (menu.contains(e.target)) return;
    if (r0) {
      const r = anchor.getBoundingClientRect();
      if (Math.abs(r.top - r0.top) < 2 && Math.abs(r.left - r0.left) < 2) return;
    } else if (performance.now() - openedAt < 200) return;
    closeMenu();
  };
  document.addEventListener('pointerdown', onDocDown, true);
  document.addEventListener('scroll', onScroll, true);
  window.addEventListener('resize', closeMenu);
  window.addEventListener('blur', closeMenu);
  if (anchor) { anchor.classList.add('is-open'); anchor.setAttribute('aria-expanded', 'true'); }
  currentMenu = { el: menu, anchor, onClose: opts.onClose, onDocDown, onScroll, restoreFocus };

  if (opts.focus !== false) {
    const checkedIdx = entries.findIndex((b) => b.classList.contains('checked') && !b.disabled);
    const first = checkedIdx >= 0 ? checkedIdx : entries.findIndex((b) => !b.disabled);
    if (opts.keyboard && first >= 0) setActive(first, true);
    else menu.focus({ preventScroll: true });
  }
  return { el: menu, close: closeMenu };
}

/** 把浮動元素放在錨點附近、不超出視窗；設定 transform-origin 讓動畫從觸發點長出 */
export function placeFloating(el, anchor, opts = {}) {
  const m = 6;
  const vw = window.innerWidth, vh = window.innerHeight;
  el.style.maxHeight = Math.max(160, vh - 2 * m) + 'px';
  const w = el.offsetWidth, hgt = el.offsetHeight;
  let x, y, ox = 'left', oy = 'top';
  if (opts.at) {
    x = opts.at.x; y = opts.at.y;
    if (x + w > vw - m) { x = x - w; ox = 'right'; }
    if (y + hgt > vh - m) { y = y - hgt; oy = 'bottom'; }
  } else if (anchor) {
    const r = anchor.getBoundingClientRect();
    const placement = opts.placement || 'bottom-start';
    const below = r.bottom + 4, above = r.top - 4 - hgt;
    if (placement.startsWith('top')) {
      if (above >= m) { y = above; oy = 'bottom'; } else y = below;
    } else if (below + hgt <= vh - m || above < m) y = below;
    else { y = above; oy = 'bottom'; }
    if (placement.endsWith('end')) { x = r.right - w; ox = 'right'; } else x = r.left;
  } else { x = (vw - w) / 2; y = (vh - hgt) / 2; ox = 'center'; oy = 'center'; }
  el.style.left = Math.round(clamp(x, m, Math.max(m, vw - m - w))) + 'px';
  el.style.top = Math.round(clamp(y, m, Math.max(m, vh - m - hgt))) + 'px';
  el.style.transformOrigin = oy + ' ' + ox;
}

// ───── 對話框 ─────
/**
 * openDialog({ title, body, footer, className, width, onClose, dismissable, initialFocus })
 * 回傳 { el, card, close }
 */
export function openDialog(opts) {
  const back = h('div', { class: 'dlg-back' });
  const card = h('div', {
    class: 'dlg' + (opts.className ? ' ' + opts.className : ''),
    role: 'dialog', 'aria-modal': 'true', 'aria-label': opts.title || '對話框', tabIndex: -1,
  });
  if (opts.width) card.style.width = 'min(' + opts.width + 'px, calc(100vw - 32px))';
  const head = h('div', { class: 'dlg-head' }, h('h2', { class: 'dlg-title', text: opts.title || '' }));
  if (opts.dismissable !== false) head.append(iconButton('x', '關閉', () => close(), 'dlg-x'));
  const body = h('div', { class: 'dlg-body' }, opts.body);
  card.append(head, body);
  if (opts.footer) card.append(h('div', { class: 'dlg-foot' }, opts.footer));
  back.append(card);
  const prevFocus = document.activeElement;
  let closed = false;
  const layer = { el: back, dismissable: opts.dismissable !== false, close: () => close() };
  function close() {
    if (closed) return;
    closed = true;
    removeLayer(layer);
    back.classList.add('leaving');
    setTimeout(() => back.remove(), 120);
    if (prevFocus && prevFocus.isConnected) { try { prevFocus.focus({ preventScroll: true }); } catch { /* 忽略 */ } }
    if (opts.onClose) opts.onClose();
  }
  back.addEventListener('pointerdown', (e) => { if (e.target === back && layer.dismissable) close(); });
  card.addEventListener('keydown', (e) => {
    if (e.key === 'Tab') trapTab(card, e);
  });
  document.body.append(back);
  pushLayer(layer);
  const target = opts.initialFocus || card.querySelector('[autofocus]') || firstFocusable(body) || card;
  requestAnimationFrame(() => { try { target.focus({ preventScroll: true }); } catch { /* 忽略 */ } });
  return { el: back, card, body, close };
}

const FOCUSABLE = 'button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';
function firstFocusable(root) {
  return root.querySelector('input:not([disabled]):not([type=hidden]), textarea:not([disabled]), select:not([disabled])');
}
function trapTab(root, e) {
  const list = Array.from(root.querySelectorAll(FOCUSABLE)).filter((n) => n.offsetParent !== null);
  if (!list.length) return;
  const first = list[0], last = list[list.length - 1];
  if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
  else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
}

/**
 * 確認對話框，取代 window.confirm。
 * confirmDialog({ title, message, detail, confirmLabel, cancelLabel, danger, checkbox: { label, checked, disabled, note } })
 * → Promise<{ ok, checked }>
 */
export function confirmDialog(o) {
  return new Promise((resolve) => {
    let checked = !!(o.checkbox && o.checkbox.checked && !o.checkbox.disabled);
    let settled = false;
    const settle = (ok) => { if (settled) return; settled = true; resolve({ ok, checked }); };
    const body = h('div', { class: 'confirm' });
    if (o.message) body.append(h('p', { class: 'confirm-msg', text: o.message }));
    if (o.detail) body.append(h('p', { class: 'confirm-detail', text: o.detail }));
    if (o.checkbox) {
      const box = h('input', { type: 'checkbox', checked, disabled: !!o.checkbox.disabled });
      box.addEventListener('change', () => { checked = box.checked; });
      body.append(h('label', { class: 'check-row' + (o.checkbox.disabled ? ' disabled' : '') }, box,
        h('span', { class: 'check-text' }, h('span', { text: o.checkbox.label }),
          o.checkbox.note ? h('small', { text: o.checkbox.note }) : null)));
    }
    const cancel = h('button', { type: 'button', class: 'btn', text: o.cancelLabel || '取消' });
    const ok = h('button', { type: 'button', class: 'btn ' + (o.danger ? 'danger-solid' : 'primary'), text: o.confirmLabel || '確定' });
    const d = openDialog({
      title: o.title || '確認', body, footer: [cancel, ok], className: 'dlg-confirm', width: o.width || 440,
      initialFocus: o.danger ? cancel : ok,
      onClose: () => settle(false),
    });
    ok.addEventListener('click', () => { settle(true); d.close(); });
    cancel.addEventListener('click', () => { settle(false); d.close(); });
  });
}

/**
 * 多個選項的對話框（confirmDialog 的多按鈕版）。
 * choiceDialog({ title, message, detail, choices: [{ value, label, primary, danger }], cancelLabel, width })
 * → Promise<value | null>（取消、關閉是 null）。按鈕照 choices 的順序排在取消後面，primary 的是預設焦點（沒有就是取消）
 */
export function choiceDialog(o) {
  return new Promise((resolve) => {
    let settled = false;
    const settle = (v) => { if (settled) return; settled = true; resolve(v); };
    const body = h('div', { class: 'confirm' });
    if (o.message) body.append(h('p', { class: 'confirm-msg', text: o.message }));
    if (o.detail) body.append(h('p', { class: 'confirm-detail', text: o.detail }));
    const choices = Array.isArray(o.choices) ? o.choices : [];
    const cancel = h('button', { type: 'button', class: 'btn', text: o.cancelLabel || '取消' });
    const buttons = choices.map((c) => h('button', {
      type: 'button', class: 'btn' + (c.danger ? ' danger-solid' : c.primary ? ' primary' : ''), text: c.label, dataset: { value: String(c.value) },
    }));
    const primary = buttons[choices.findIndex((c) => c.primary)] || cancel;
    const d = openDialog({
      title: o.title || '確認', body, footer: [cancel, ...buttons], className: 'dlg-confirm dlg-choice', width: o.width || 480,
      initialFocus: primary,
      onClose: () => settle(null),
    });
    buttons.forEach((b, i) => b.addEventListener('click', () => { settle(choices[i].value); d.close(); }));
    cancel.addEventListener('click', () => { settle(null); d.close(); });
  });
}

// ───── 提示（toast） ─────
let toastRoot = null;
export function toast(text, level = 'info') {
  if (!text) return;
  if (!toastRoot) { toastRoot = h('div', { class: 'toasts', 'aria-live': 'polite' }); document.body.append(toastRoot); }
  const lv = level === 'error' || level === 'success' ? level : 'info';
  const t = h('div', { class: 'toast ' + lv, role: lv === 'error' ? 'alert' : 'status' },
    icon(lv === 'error' ? 'error' : lv === 'success' ? 'check' : 'info', 'toast-ic'),
    h('div', { class: 'toast-text', text: String(text) }));
  const x = iconButton('x', '關閉', () => dismiss(), 'toast-x');
  t.append(x);
  toastRoot.append(t);
  while (toastRoot.children.length > 4) toastRoot.firstElementChild.remove();
  let remaining = lv === 'error' ? 7000 : 4200, started = 0, timer = 0;
  // 底部的倒數條：看得出還會停多久（只動 transform，fx.css 的 .toast-bar）；滑鼠移上去和計時器一起停。減少動態、關掉動態效果時不畫
  const total = remaining;
  const bar = calmMotion() ? null : h('span', { class: 'toast-bar', 'aria-hidden': 'true' });
  if (bar) t.append(bar);
  let drain = null;
  const start = () => {
    started = Date.now(); timer = setTimeout(dismiss, remaining);
    if (bar && bar.animate) {
      if (drain) drain.cancel();
      drain = bar.animate([{ transform: 'scaleX(' + Math.min(1, remaining / total) + ')' }, { transform: 'scaleX(0)' }], { duration: remaining, easing: 'linear', fill: 'forwards' });
    }
  };
  const pause = () => { clearTimeout(timer); remaining = Math.max(1200, remaining - (Date.now() - started)); if (drain) drain.pause(); };
  t.addEventListener('pointerenter', pause);
  t.addEventListener('pointerleave', start);
  function dismiss() {
    clearTimeout(timer);
    if (t.classList.contains('leaving')) return;
    t.classList.add('leaving');
    setTimeout(() => t.remove(), 160);
  }
  start();
}

// ───── 浮動說明（走勢線 hover 用） ─────
let tipEl = null;
export function showTip(content, x, y) {
  if (!tipEl) { tipEl = h('div', { class: 'tip', role: 'tooltip' }); document.body.append(tipEl); }
  tipEl.textContent = '';
  if (typeof content === 'string') tipEl.textContent = content; else tipEl.append(content);
  tipEl.hidden = false;
  const w = tipEl.offsetWidth, hh = tipEl.offsetHeight;
  tipEl.style.left = Math.round(clamp(x - w / 2, 6, window.innerWidth - w - 6)) + 'px';
  tipEl.style.top = Math.round(y - hh - 10 < 6 ? y + 14 : y - hh - 10) + 'px';
}
export function hideTip() { if (tipEl) tipEl.hidden = true; }

// ───── 表單小元件 ─────
export function switchControl({ checked, onChange, label, disabled }) {
  const input = h('input', { type: 'checkbox', class: 'switch-input', checked: !!checked, disabled: !!disabled, role: 'switch', 'aria-label': label || null });
  const wrap = h('label', { class: 'switch' }, input, h('span', { class: 'switch-track' }, h('span', { class: 'switch-knob' })));
  // 切換的那一下旋鈕上的小方塊燈閃一下（fx.css 的 .switch.fx-sw；打開時燈亮著、關掉時熄）
  input.addEventListener('change', () => { replayClass(wrap, 'fx-sw', 520); if (onChange) onChange(input.checked); });
  wrap.input = input;
  return wrap;
}

export function segmented(options, value, onChange, cls) {
  const wrap = h('div', { class: 'seg' + (cls ? ' ' + cls : ''), role: 'radiogroup' });
  const set = (v) => {
    for (const b of wrap.children) {
      const on = b.dataset.value === v;
      b.classList.toggle('on', on);
      b.setAttribute('aria-checked', String(on));
    }
  };
  for (const o of options) {
    const b = h('button', { type: 'button', class: 'seg-btn', role: 'radio', dataset: { value: o.value }, title: o.title || null },
      o.icon ? icon(o.icon) : null, o.label ? h('span', { text: o.label }) : null);
    b.addEventListener('click', () => { slideSeg(wrap, b); set(o.value); if (onChange) onChange(o.value); });
    wrap.append(b);
  }
  set(value);
  wrap.setValue = set;
  return wrap;
}

/**
 * 分段選擇被點了另一顆：選中的底色從原本那顆滑到新的這顆（暫時的 .seg-slide 以 Web Animations 位移加縮放，只動 transform，滑完就移除；
 * 滑動期間新那顆的底色先藏起來，fx.css）。程式設定值（setValue）不滑；減少動態、關掉動態效果時不滑
 */
function slideSeg(wrap, target) {
  const from = wrap.querySelector('.seg-btn.on');
  if (!from || from === target || calmMotion() || !wrap.animate) return;
  const wr = wrap.getBoundingClientRect(), fr = from.getBoundingClientRect(), tr = target.getBoundingClientRect();
  if (!tr.width || !fr.width) return;
  for (const old of wrap.querySelectorAll('.seg-slide')) old.remove();
  const ghost = h('span', { class: 'seg-slide', 'aria-hidden': 'true', style: { left: (tr.left - wr.left - wrap.clientLeft) + 'px', width: tr.width + 'px' } });
  wrap.append(ghost);
  wrap.classList.add('seg-moving');
  const a = ghost.animate([{ transform: 'translateX(' + (fr.left - tr.left) + 'px) scaleX(' + (fr.width / tr.width) + ')' }, { transform: 'none' }],
    { duration: 180, easing: 'cubic-bezier(.2, .8, .2, 1)' });
  const done = () => { ghost.remove(); if (!wrap.querySelector('.seg-slide')) wrap.classList.remove('seg-moving'); };
  a.onfinish = done;
  a.oncancel = done;
}

export function spinner(cls) {
  return h('span', { class: 'spin' + (cls ? ' ' + cls : ''), 'aria-hidden': 'true' });
}
