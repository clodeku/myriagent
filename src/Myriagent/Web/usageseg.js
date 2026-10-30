// 儀表板的「每週額度每 5% 的 token」（只有桌面；web-protocol 的 usageSegments）：每週額度每跨過一個 5% 的邊界，宿主記一筆這段期間
// 各模型用掉的 token（usage-segments\records.jsonl）。這裡顯示：各帳戶 × 額度窗 × 主要模型的平均（完整的段落，每 5% 依實際前進的百分點換算）、
// 正在記的各段、最近的紀錄；可複製成 CSV、在檔案總管顯示紀錄檔。數字都由宿主算好，這裡只負責顯示
import { h, iconButton, fmtDateTime, fmtRelative, copyToClipboard } from './util.js';
import { icon, providerMark } from './icons.js';
import { post, state, isHub } from './store.js';
import { toast } from './ui.js';
import { fmtShort, fmtTokens } from './tokenrules.js';
import { segView, pct, rangeText, observedText, durationText, totalOf, modelCounts, csvOf, summaryTip, barFracs, modelShares, sharesTip, segProgress } from './usagesegrules.js';

/** 請宿主送目前的紀錄（切到儀表板與每分鐘；之後結了新的一筆宿主會主動送） */
export function requestUsageSegments() {
  if (isHub || state.mode === 'popout') return;
  post({ cmd: 'usageSegments' });
}

/** 數字格：精簡格式，滑過看精確值；frac（0～1，第九批）是這一欄的比例底條（和這一欄最大的比，fx.css 的 .us-bar） */
function numCell(n, cls, frac) {
  const bar = Number.isFinite(frac);
  return h('td', { class: 'us-num' + (cls ? ' ' + cls : '') + (bar ? ' us-bar' : ''), title: fmtTokens(n), text: fmtShort(n),
    style: bar ? { '--us-f': frac.toFixed(3) } : null });
}

/** 各模型的組成條（第九批）：佔輸入＋輸出的比例，顏色依模型的色系；沒有 token 回 null */
function sharesBar(models) {
  const shares = modelShares(models);
  if (!shares.length) return null;
  return h('span', { class: 'us-mix', role: 'img', 'aria-label': sharesTip(shares).replace(/\n/g, '、'), title: sharesTip(shares) },
    ...shares.map((s) => h('i', { class: 'mt-' + s.tone, style: { 'flex-grow': s.frac.toFixed(4) } })));
}

/** 這一欄每一列的比例（第九批的比例底條） */
const colFracs = (list, pick) => barFracs(list.map(pick));

function accountCell(provider, label) {
  return h('td', null, h('span', { class: 'us-acct' }, providerMark(provider, 'us-mark'), h('span', { class: 'us-acct-name', text: label || '' })));
}

function modelTip(models) {
  return (Array.isArray(models) ? models : []).map((m) => m.model + '：輸入 ' + fmtTokens(m.input) + '（快取讀取 ' + fmtTokens(m.cached)
    + '、快取寫入 ' + fmtTokens(m.cacheWrite) + '）、輸出 ' + fmtTokens(m.output)).join('\n');
}

export function createUsageSegmentsSection() {
  const copy = h('button', { type: 'button', class: 'btn sm' }, icon('copy'), h('span', { text: '複製成 CSV' }));
  copy.addEventListener('click', async () => {
    const v = segView(state.usageSegments);
    if (!v || !v.records.length) { toast('還沒有紀錄可以複製'); return; }
    if (await copyToClipboard(csvOf(v.records), (t) => post({ cmd: 'copyText', text: t }))) toast('已複製最近 ' + v.records.length + ' 筆紀錄（CSV）', 'success');
  });
  const reveal = iconButton('folderOpen', '在檔案總管中顯示紀錄檔（records.jsonl，一行一筆，全部的紀錄）', () => {
    const v = segView(state.usageSegments);
    if (v && v.path) post({ cmd: 'revealPath', path: v.path });
  }, 'us-reveal');
  const note = h('p', { class: 'us-note', text: '只算這台 app 經手的對話。同一個帳戶在其他電腦、終端機的 CLI 或網頁版用掉的額度也會讓使用率上升，那幾段每 5% 的 token 會偏少；'
    + '每 5% 依實際看到的使用率換算（不假設剛好 5%），開始追蹤時已在一段中間的那筆標成不完整、不算進平均。' });
  const empty = h('p', { class: 'us-empty', hidden: true });
  const sumWrap = h('div', { class: 'us-block' }, h('h3', { class: 'us-h', text: '每 5% 的平均（完整的段落）' }));
  const openWrap = h('div', { class: 'us-block' }, h('h3', { class: 'us-h', text: '正在記的' }));
  const recWrap = h('div', { class: 'us-block' }, h('h3', { class: 'us-h', text: '最近的紀錄' }));
  const sec = h('section', { class: 'dash-sec dash-sec-us', 'aria-label': '每週額度每 5% 的 token', hidden: true },
    h('div', { class: 'dash-sec-head dash-sec-gap' }, h('h2', { class: 'dash-h', text: '每週額度每 5% 的 token' }),
      h('span', { class: 'dash-sub', text: '每週額度每跨過一個 5%，記一筆這段期間各模型用掉的輸入與輸出' }), h('span', { class: 'grow' }), copy, reveal),
    note, empty, sumWrap, openWrap, recWrap);
  sec._els = { copy, reveal, empty, sumWrap, openWrap, recWrap };
  return sec;
}

export function renderUsageSegmentsSection(sec, now = Date.now()) {
  if (!sec) return;
  const e = sec._els;
  const v = isHub ? null : segView(state.usageSegments);
  sec.hidden = !v;
  if (!v) return;
  const sig = JSON.stringify([v, Math.floor(now / 60000)]);
  if (sec._sig === sig) return;
  sec._sig = sig;
  e.copy.disabled = !v.records.length;
  e.reveal.disabled = !v.path;

  const nothing = !v.summary.length && !v.open.length && !v.records.length;
  e.empty.hidden = !nothing;
  e.empty.textContent = nothing ? '還沒有資料：帳戶的每週用量查到之後就開始記，使用率每跨過一個 5% 記一筆。' : '';

  renderSummary(e.sumWrap, v.summary);
  renderOpen(e.openWrap, v.open, now);
  renderRecords(e.recWrap, v.records, v.total);
}

function replaceTable(wrap, table) {
  const old = wrap.querySelector('.us-scroll, .us-none');
  if (old) old.remove();
  wrap.append(table);
}

function renderSummary(wrap, list) {
  wrap.hidden = !list.length;
  if (!list.length) return;
  const head = h('tr', null, ...['帳戶', '額度窗', '模型', '段數', '每 5% 輸入', '其中快取讀取', '其中快取寫入', '每 5% 輸出', '每 5% 輸入＋輸出的範圍']
    .map((t, i) => h('th', { class: i >= 3 ? 'us-num' : null, text: t })));
  const f = ['input', 'cached', 'cacheWrite', 'output'].map((k) => colFracs(list, (g) => (g.per5 || {})[k]));
  const rows = list.map((g, i) => {
    const counts = modelCounts(g.windowKey, g.model);
    const p5 = g.per5 || {};
    return h('tr', { class: counts ? null : 'us-dim', title: summaryTip(g) + (counts ? '' : '\n這個額度窗只算名稱相符的模型，這一列的 token 不算在它的額度裡') },
      accountCell(g.provider, g.accountLabel), h('td', { text: g.windowLabel || g.windowKey }), h('td', { class: 'us-model', text: g.model }),
      h('td', { class: 'us-num', text: String(g.records) }), numCell(p5.input, null, f[0][i]), numCell(p5.cached, 'us-sub', f[1][i]), numCell(p5.cacheWrite, 'us-sub', f[2][i]),
      numCell(p5.output, null, f[3][i]), h('td', { class: 'us-num us-sub', text: fmtShort(g.minPer5) + ' ～ ' + fmtShort(g.maxPer5) }));
  });
  replaceTable(wrap, h('div', { class: 'us-scroll' }, h('table', { class: 'us-table' }, h('thead', null, head), h('tbody', null, ...rows))));
}

function renderOpen(wrap, list, now) {
  wrap.hidden = !list.length;
  if (!list.length) return;
  const head = h('tr', null, ...['帳戶', '額度窗', '這一段', '現在', '已用輸入', '已用輸出', '開始', '模型組成']
    .map((t, i) => h('th', { class: i >= 4 && i <= 5 ? 'us-num' : null, text: t })));
  const totals = list.map((s) => totalOf(s.models));
  const fi = colFracs(totals, (t) => t.input), fo = colFracs(totals, (t) => t.output);
  const rows = list.map((s, i) => {
    const t = totals[i];
    // 「現在」後面一條 5 格的進度條（第九批）：這一段走到哪裡（例 47% 在 45% → 50% 走了 40%）
    const prog = segProgress(s);
    return h('tr', { title: modelTip(s.models) || '這一段還沒有 token' },
      accountCell(s.provider, s.accountLabel), h('td', { text: s.windowLabel || s.windowKey }),
      h('td', null, h('span', { text: pct(s.bucket) + ' → ' + pct(Number(s.bucket) + 5) }),
        s.complete ? null : h('span', { class: 'us-badge', text: '從 ' + pct(s.startPercent) + ' 開始，不完整' })),
      h('td', { class: 'us-num' }, h('span', { text: pct(s.lastPercent) }),
        h('span', { class: 'us-prog', role: 'img', 'aria-label': '這一段走了 ' + pct(prog * 100), title: '這一段走了 ' + pct(prog * 100) + '：' + pct(s.lastPercent) + '，到 ' + pct(Number(s.bucket) + 5) + ' 記一筆' },
          h('i', { style: { width: (prog * 100).toFixed(1) + '%' } }))),
      numCell(t.input, null, fi[i]), numCell(t.output, null, fo[i]),
      h('td', { class: 'us-when', text: fmtRelative(s.startedAt, now), title: fmtDateTime(s.startedAt) }), h('td', null, sharesBar(s.models)));
  });
  replaceTable(wrap, h('div', { class: 'us-scroll' }, h('table', { class: 'us-table' }, h('thead', null, head), h('tbody', null, ...rows))));
}

function renderRecords(wrap, list, total) {
  wrap.hidden = !list.length;
  if (!list.length) return;
  const title = wrap.querySelector('.us-h');
  title.textContent = total > list.length ? '最近的紀錄（共 ' + total + ' 筆，這裡列最近 ' + list.length + ' 筆）' : '最近的紀錄（共 ' + total + ' 筆）';
  const head = h('tr', null, ...['結束', '帳戶', '額度窗', '區間', '花了', '輸入', '輸出', '主要模型']
    .map((t, i) => h('th', { class: i === 5 || i === 6 ? 'us-num' : null, text: t })));
  const totals = list.map((r) => totalOf(r.models));
  const fi = colFracs(totals, (t) => t.input), fo = colFracs(totals, (t) => t.output);
  const rows = list.map((r, i) => {
    const t = totals[i];
    return h('tr', { class: r.complete ? null : 'us-partial', title: observedText(r) + '\n' + modelTip(r.models) },
      h('td', { class: 'us-when', text: fmtDateTime(r.endedAt) }), accountCell(r.provider, r.accountLabel), h('td', { text: r.windowLabel || r.windowKey }),
      h('td', null, h('span', { text: rangeText(r) }), r.complete ? null : h('span', { class: 'us-badge', text: '不完整' })),
      h('td', { class: 'us-when', text: durationText(Number(r.endedAt) - Number(r.startedAt)) }), numCell(t.input, null, fi[i]), numCell(t.output, null, fo[i]),
      h('td', { class: 'us-model' }, sharesBar(r.models), h('span', { text: r.model || '' })));
  });
  replaceTable(wrap, h('div', { class: 'us-scroll' }, h('table', { class: 'us-table' }, h('thead', null, head), h('tbody', null, ...rows))));
}
