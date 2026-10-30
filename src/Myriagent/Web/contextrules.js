// 輸入框旁的上下文用量提示（純規則，沒有 import：tools/ui-probe/rules.test.mjs 直接匯入）。
// 數字與嚴重度由宿主算（Header.context：used、window、percent、autoCompactAt、severity，docs/web-protocol.md），網頁只決定顯示與說明文字。

const nf = new Intl.NumberFormat('en-US');
const SEVERITIES = ['ok', 'warn', 'high', 'max'];

function num(v) {
  if (v === null || v === undefined || v === '') return NaN; // Number(null) 是 0：沒有值不能當成 0
  const n = Number(v);
  return Number.isFinite(n) ? n : NaN;
}

/** 有可顯示的上下文用量：宿主沒給（Antigravity、瀏覽器版、副本、用量或視窗還不明）就不顯示 */
export function contextShown(ctx) {
  return !!ctx && num(ctx.window) > 0 && num(ctx.used) >= 0;
}

/** 圓環填滿的比例（0～100，以視窗為分母） */
export function contextPercent(ctx) {
  const p = num(ctx && ctx.percent);
  return Number.isFinite(p) ? Math.max(0, Math.min(100, p)) : 0;
}

/** 圓環旁的字：不到 1% 寫「<1%」，其餘四捨五入到整數 */
export function contextLabel(ctx) {
  const p = contextPercent(ctx);
  if (p > 0 && p < 1) return '<1%';
  return Math.round(p) + '%';
}

/** 嚴重度（ok／warn／high／max；不認得的當 ok） */
export function contextSeverity(ctx) {
  const s = ctx && ctx.severity;
  return SEVERITIES.includes(s) ? s : 'ok';
}

/** 自動壓縮門檻在圓環上的位置（0～100）；沒有門檻、或門檻不在視窗內時 null */
export function contextMark(ctx) {
  const at = num(ctx && ctx.autoCompactAt);
  const w = num(ctx && ctx.window);
  if (!(at > 0) || !(w > 0) || at > w) return null;
  return Math.round((at / w) * 1000) / 10;
}

/** 一行摘要（說明的第一行，也是 aria-label） */
export function contextSummary(ctx) {
  return '上下文：' + nf.format(num(ctx.used)) + '／' + nf.format(num(ctx.window)) + ' tokens（' + contextLabel(ctx) + '）';
}

/** 滑過的說明（多行純文字，放 title）；provider 是 Header.provider */
export function contextTitle(ctx, provider) {
  const used = num(ctx.used);
  const lines = [contextSummary(ctx)];
  const at = contextMark(ctx) == null ? NaN : num(ctx.autoCompactAt);
  if (at > 0) {
    lines.push(used >= at
      ? '已到自動壓縮門檻（' + nf.format(at) + ' tokens），之後會自動壓縮較早的內容'
      : '自動壓縮門檻 ' + nf.format(at) + ' tokens，還有 ' + nf.format(at - used));
  }
  lines.push('最近一次模型呼叫的輸入（含快取）加輸出');
  const sev = contextSeverity(ctx);
  if (sev === 'high' || sev === 'max') {
    lines.push(['Claude', 'Codex'].includes(provider)
      ? '上下文快滿了：可以送 /compact 壓縮較早的內容，或開新對話'
      : '上下文快滿了：可以開新對話接著做');
  }
  return lines.join('\n');
}
