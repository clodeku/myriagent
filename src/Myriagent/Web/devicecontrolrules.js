// 電腦狀態面板的電腦選單：喇叭音量、靜音、立即關閉螢幕（2026-11-17；deviceControl，web-protocol）的純規則。不碰 DOM

const isObj = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);

/** 拖動音量時多久送一次（放開時另外馬上送） */
export const VOLUME_SEND_MS = 200;

/**
 * 送給宿主的 deviceControl：這台的卡片不帶 deviceId（宿主自己做），其他電腦的帶 deviceId（經伺服器轉給那台）。
 * volume 是 0～100 的字串（型錄沒有整數欄位）；沒給的不帶
 */
export function controlCommand({ dev, action, volume, mute, requestId }) {
  const cmd = { cmd: 'deviceControl', action, requestId };
  if (dev) cmd.deviceId = dev;
  if (Number.isFinite(volume)) cmd.volume = String(Math.max(0, Math.min(100, Math.round(volume))));
  if (typeof mute === 'boolean') cmd.mute = mute;
  return cmd;
}

/** 宿主回的 deviceControlState → { requestId, action, volume, muted, error }；形狀不對回 null */
export function parseState(m) {
  if (!isObj(m) || m.type !== 'deviceControlState' || typeof m.action !== 'string') return null;
  const volume = Number.isInteger(m.volume) && m.volume >= 0 && m.volume <= 100 ? m.volume : null;
  return {
    requestId: typeof m.requestId === 'string' ? m.requestId : null,
    action: m.action,
    volume,
    muted: typeof m.muted === 'boolean' ? m.muted : null,
    error: typeof m.error === 'string' && m.error ? m.error : null,
  };
}

/** 音量的文字：靜音時寫「靜音」 */
export function volumeLabel(volume, muted) {
  if (muted) return '靜音';
  return Number.isInteger(volume) ? volume + '%' : '—';
}

/** 喇叭圖示的種類：靜音、小聲（≤33）、中、大 */
export function speakerLevel(volume, muted) {
  if (muted || volume === 0) return 'mute';
  if (!Number.isInteger(volume)) return 'mid';
  return volume <= 33 ? 'low' : volume <= 66 ? 'mid' : 'high';
}
