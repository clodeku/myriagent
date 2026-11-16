// 電腦狀態面板的電腦選單：喇叭音量、靜音、立即關閉螢幕（2026-11-17 使用者要求；web-protocol 的 deviceControl）。
// 這台的卡片宿主自己做；其他電腦的經伺服器轉給那台（那台要「可以操作」，舊版回「版本太舊」）。結果是 deviceControlState，依 requestId 交回。
// 文字一律 textContent，class 一律 dc- 前綴
import { h } from './util.js';
import { icon } from './icons.js';
import { post, nextRequestId } from './store.js';
import { openDialog, toast } from './ui.js';
import { track } from './cmdtrack.js';
import * as R from './devicecontrolrules.js';

/** requestId → 收到 deviceControlState 時的回呼 */
const waiting = new Map();

/** 宿主或伺服器回的 deviceControlState（app.js 呼叫）：自己送的才收 */
export function onState(m) {
  const st = R.parseState(m);
  if (!st || !st.requestId) return;
  const cb = waiting.get(st.requestId);
  if (!cb) return;
  waiting.delete(st.requestId);
  cb(st);
}

/** 送一次、回 requestId；被拒或沒送出時回呼收到帶 error 的結果（原因的提示由伺服器或傳輸層給） */
function send(dev, action, extra, cb) {
  const requestId = nextRequestId('dc');
  if (cb) waiting.set(requestId, cb);
  const id = post(R.controlCommand(Object.assign({ dev, action, requestId }, extra)));
  const fail = (error) => {
    if (!waiting.has(requestId)) return;
    waiting.delete(requestId);
    if (cb) cb({ requestId, action, volume: null, muted: null, error });
  };
  if (id === null) { fail('沒有送出：目前沒有連上伺服器'); return requestId; }
  if (typeof id === 'string') track(id, { onSettle: (state) => { if (state !== 'done') fail(state === 'rejected' ? '那台沒有接受' : '不知道那台有沒有收到'); } });
  return requestId;
}

/** 選單的兩項（devicefiles.js 的電腦卡片選單）：dev 是 null＝這台；disabled／why 照檔案傳輸的條件 */
export function controlMenuItems(dev, name, disabled = false, why = null) {
  return [
    { label: '喇叭音量…', icon: 'speaker', disabled, desc: why, onSelect: () => openVolume(dev, name) },
    { label: '立即關閉螢幕', icon: 'moon', disabled, desc: why || '動滑鼠或鍵盤就會亮；不鎖定、程式照跑', onSelect: () => screenOff(dev, name) },
  ];
}

/** 立即關閉那台的螢幕 */
export function screenOff(dev, name) {
  send(dev, 'screenOff', {}, (st) => {
    if (st.error) toast(st.error, 'error');
    else toast(`已關閉「${name}」的螢幕`, 'success');
  });
}

/** 喇叭音量的對話框：滑桿＋靜音，打開時先讀目前的值；拖動時每 200 毫秒送一次、放開時馬上送 */
export function openVolume(dev, name) {
  const slider = h('input', { type: 'range', min: '0', max: '100', step: '1', value: '0', class: 'dc-slider', 'aria-label': '音量', disabled: true });
  const value = h('span', { class: 'dc-val', text: '—' });
  const muteBtn = h('button', { type: 'button', class: 'btn dc-mute', disabled: true, title: '靜音' }, icon('speaker'), h('span', { text: '靜音' }));
  const status = h('div', { class: 'dc-status', text: '讀取中…' });
  let muted = false;
  let dragging = false;
  let latest = null;
  let timer = 0;
  let closed = false;

  const render = (volume) => {
    value.textContent = R.volumeLabel(volume, muted);
    muteBtn.classList.toggle('on', muted);
    muteBtn.lastChild.textContent = muted ? '取消靜音' : '靜音';
    muteBtn.dataset.level = R.speakerLevel(volume, muted);
  };
  const apply = (st) => {
    if (closed) return;
    // 晚到的舊結果（拖動中先送的幾次）不蓋掉後來的位置
    if (st.action === 'audioSet' && latest && st.requestId !== latest && !st.error) return;
    if (st.error) { status.textContent = st.error; return; }
    status.textContent = '';
    if (typeof st.muted === 'boolean') muted = st.muted;
    if (Number.isInteger(st.volume) && !dragging) slider.value = String(st.volume);
    slider.disabled = false;
    muteBtn.disabled = false;
    render(Number(slider.value));
  };
  const sendVolume = () => {
    timer = 0;
    latest = send(dev, 'audioSet', { volume: Number(slider.value) }, apply);
  };
  slider.addEventListener('input', () => {
    dragging = true;
    render(Number(slider.value));
    if (!timer) timer = setTimeout(sendVolume, R.VOLUME_SEND_MS);
  });
  slider.addEventListener('change', () => {
    dragging = false;
    clearTimeout(timer);
    sendVolume();
  });
  muteBtn.addEventListener('click', () => {
    latest = send(dev, 'audioSet', { mute: !muted }, apply);
  });

  const d = openDialog({
    title: `「${name}」的喇叭`, width: 380, className: 'dc-dlg',
    body: h('div', { class: 'dc-body' }, h('div', { class: 'dc-vol' }, muteBtn, slider, value), status),
    onClose: () => { closed = true; clearTimeout(timer); },
  });
  send(dev, 'audioGet', {}, apply);
  return d;
}
