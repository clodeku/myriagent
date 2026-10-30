// 儀表板的「每週額度每 5% 的 token」：純規則（沒有 import，tools/ui-probe/usagesegrules.test.mjs 直接測）。
// 數字都由宿主算好（web-protocol 的 usageSegments／UsageSegmentsView：彙總的每 5% 已依實際前進的百分點換算），這裡只負責整理成要顯示的字與 CSV

const num = (x) => (Number.isFinite(x) ? x : 0);
const count = (x) => (Number.isFinite(x) && x > 0 ? Math.floor(x) : 0);
/** 千分位（同 tokenrules.fmtTokens） */
const fmtTokens = (n) => String(count(n)).replace(/\B(?=(\d{3})+(?!\d))/g, ',');
const arr = (x) => (Array.isArray(x) ? x.filter((v) => v && typeof v === 'object') : []);

/** 宿主送的 usageSegments（不是物件回 null）：陣列一律存在 */
export function segView(v) {
  if (!v || typeof v !== 'object') return null;
  return {
    path: typeof v.path === 'string' ? v.path : '',
    total: count(v.total),
    summary: arr(v.summary),
    open: arr(v.open),
    records: arr(v.records),
  };
}

/** 百分比的字（最多一位小數，去掉多餘的 0）："12.3%"、"15%" */
export function pct(p) {
  const v = Math.round(num(p) * 10) / 10;
  return (Number.isInteger(v) ? String(v) : v.toFixed(1)) + '%';
}

/** 一筆紀錄的區間："10% → 15%"；實際看到的使用率另外寫 */
export function rangeText(r) {
  return pct(r.fromBucket) + ' → ' + pct(r.toBucket);
}

/** 實際看到的使用率："實際 12.3% → 15.2%（+2.9）" */
export function observedText(r) {
  const d = Math.round((num(r.endPercent) - num(r.startPercent)) * 10) / 10;
  return '實際 ' + pct(r.startPercent) + ' → ' + pct(r.endPercent) + '（+' + (Number.isInteger(d) ? d : d.toFixed(1)) + '）';
}

/** 經過的時間："3 天 4 小時"、"2 小時 5 分"、"12 分"、"不到 1 分" */
export function durationText(ms) {
  const totalMin = Math.floor(num(ms) / 60000);
  if (totalMin < 1) return '不到 1 分';
  const d = Math.floor(totalMin / 1440), hr = Math.floor((totalMin % 1440) / 60), mi = totalMin % 60;
  if (d > 0) return d + ' 天' + (hr ? ' ' + hr + ' 小時' : '');
  if (hr > 0) return hr + ' 小時' + (mi ? ' ' + mi + ' 分' : '');
  return mi + ' 分';
}

/** 各模型相加：{ input, cached, cacheWrite, output } */
export function totalOf(models) {
  const t = { input: 0, cached: 0, cacheWrite: 0, output: 0 };
  for (const m of arr(models)) {
    t.input += count(m.input);
    t.cached += count(m.cached);
    t.cacheWrite += count(m.cacheWrite);
    t.output += count(m.output);
  }
  return t;
}

/**
 * 這個模型是不是這個額度窗算的：Claude 的「每週・Opus」只算 Opus 的模型（模型 id 含額度窗名稱，不分大小寫）；
 * 不分模型的額度窗（weekly、Codex、Antigravity、Grok）一律 true
 */
export function modelCounts(windowKey, model) {
  const key = typeof windowKey === 'string' ? windowKey : '';
  if (!key.startsWith('weekly:')) return true;
  const scope = key.slice('weekly:'.length).trim().toLowerCase();
  return !scope || (typeof model === 'string' && model.toLowerCase().includes(scope));
}

/** 本機時間 "2026-10-25 14:03"（CSV 用） */
export function isoLocal(ms) {
  if (!Number.isFinite(ms) || ms <= 0) return '';
  const d = new Date(ms);
  const p = (n) => String(n).padStart(2, '0');
  return d.getFullYear() + '-' + p(d.getMonth() + 1) + '-' + p(d.getDate()) + ' ' + p(d.getHours()) + ':' + p(d.getMinutes());
}

function csvCell(v) {
  const s = v == null ? '' : String(v);
  return /[",\r\n]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s;
}

export const CSV_HEADER = ['開始', '結束', '帳戶', '代理', '額度窗', '起點', '終點', '開始時的使用率', '結束時的使用率', '完整', '模型',
  '輸入（含快取）', '快取讀取', '快取寫入', '輸出'];

/** 紀錄 → CSV（一筆紀錄的每個模型一列；數字不加千分位） */
export function csvOf(records) {
  const rows = [CSV_HEADER.map(csvCell).join(',')];
  for (const r of arr(records)) {
    const models = arr(r.models);
    for (const m of models.length ? models : [{ model: '' }]) {
      rows.push([
        isoLocal(r.startedAt), isoLocal(r.endedAt), r.accountLabel || '', r.provider || '', r.windowLabel || r.windowKey || '',
        num(r.fromBucket), num(r.toBucket), num(r.startPercent), num(r.endPercent), r.complete ? '是' : '否', m.model || '',
        count(m.input), count(m.cached), count(m.cacheWrite), count(m.output),
      ].map(csvCell).join(','));
    }
  }
  return rows.join('\r\n') + '\r\n';
}

/** 彙總一列的說明（滑過）：段數、實際前進的百分點、合計與每 5% 的範圍 */
export function summaryTip(g) {
  const t = g.tokens || {};
  return [
    g.records + ' 段完整的紀錄，實際前進 ' + pct(g.percent),
    '合計：輸入 ' + fmtTokens(t.input) + '（快取讀取 ' + fmtTokens(t.cached) + '、快取寫入 ' + fmtTokens(t.cacheWrite) + '）、輸出 ' + fmtTokens(t.output),
    '每 5% 的輸入＋輸出：最少 ' + fmtTokens(g.minPer5) + '、最多 ' + fmtTokens(g.maxPer5),
  ].join('\n');
}

// ───── 第九批視覺效果（2026-10-26）：數字格的比例底條、各模型的組成條、正在記的那一段的進度條 ─────

/** 一欄數字的比例底條：各列除以這一欄最大的（0～1）；最大是 0 時都是 0 */
export function barFracs(values) {
  const v = (Array.isArray(values) ? values : []).map(count);
  const max = Math.max(0, ...v);
  return v.map((x) => (max > 0 ? x / max : 0));
}

/** 模型的色系（組成條的顏色）：Opus、Sonnet、Haiku、GPT／Codex、Gemini、Grok，其他 */
export function modelTone(model) {
  const m = typeof model === 'string' ? model.toLowerCase() : '';
  if (m.includes('opus')) return 'opus';
  if (m.includes('sonnet')) return 'sonnet';
  if (m.includes('haiku')) return 'haiku';
  if (m.includes('fable')) return 'fable';
  if (m.includes('gemini')) return 'gemini';
  if (m.includes('grok')) return 'grok';
  if (/gpt|codex|^o\d/.test(m)) return 'gpt';
  return 'other';
}

/** 各模型佔輸入＋輸出的比例（組成條）：由大到小、去掉 0；[{ model, frac, tone }] */
export function modelShares(models) {
  const list = arr(models).map((m) => ({ model: String(m.model || ''), n: count(m.input) + count(m.output) })).filter((m) => m.n > 0);
  const total = list.reduce((s, m) => s + m.n, 0);
  return list.sort((a, b) => b.n - a.n).map((m) => ({ model: m.model, frac: m.n / total, tone: modelTone(m.model) }));
}

/** 組成條的說明：「claude-opus-5-5 98.9%」一行一個 */
export function sharesTip(shares) {
  return (Array.isArray(shares) ? shares : []).map((s) => s.model + ' ' + pct(s.frac * 100)).join('\n');
}

/** 正在記的那一段走到哪裡（進度條）：(現在的使用率 − 這一段的起點) ÷ 5，0～1 */
export function segProgress(s) {
  const f = (num(s && s.lastPercent) - num(s && s.bucket)) / 5;
  return Math.max(0, Math.min(1, f));
}
