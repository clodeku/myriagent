// 工具與核准卡的差異檢視（items.js 的 renderDiff、syntax.js 的 queueDiff）用到的純規則：沒有 import、沒有 DOM
// （tools/ui-probe/diffrules.test.mjs 以 data: URL 匯入測試）。分詞器由呼叫端傳進來（syntaxrules.tokenize 綁好語言）。
// 每一行切成片段 [類別或 null, 文字, 是不是改動的字]：接起來一定等於原本那一行。

/** 逐字比對：單行超過這麼長不比、字詞格數（兩行字詞數的乘積）超過這麼多改比頭尾、改動超過這個比例不標（整行重寫，標了全是顏色） */
export const INTRA_MAX_LINE = 400;
export const INTRA_MAX_CELLS = 40_000;
export const INTRA_MAX_RATIO = 0.6;

/** 一行拆成字詞：字母數字底線連成一個、空白連成一個、其他符號各自一個 */
export function wordTokens(s) {
  return String(s ?? '').match(/[\p{L}\p{N}_]+|\s+|[^\p{L}\p{N}_\s]/gu) || [];
}

/**
 * 相鄰的刪除行與新增行兩兩配對（一段連續的刪除後面緊接著一段連續的新增，依序配對到較少的那邊為止）：
 * 回傳 Map（行的索引 → 配對那一行的索引，兩個方向都有）
 */
export function pairLines(lines) {
  const pairs = new Map();
  const t = (i) => (lines[i] && lines[i].t) || 'ctx';
  let i = 0;
  while (i < lines.length) {
    if (t(i) !== 'del') { i++; continue; }
    const d0 = i;
    while (i < lines.length && t(i) === 'del') i++;
    const a0 = i;
    while (i < lines.length && t(i) === 'add') i++;
    const n = Math.min(a0 - d0, i - a0);
    for (let k = 0; k < n; k++) { pairs.set(d0 + k, a0 + k); pairs.set(a0 + k, d0 + k); }
  }
  return pairs;
}

/** 把相鄰的改動字詞併成字元範圍 [起, 迄) */
function rangesOf(tokens, changed) {
  const out = [];
  let pos = 0;
  for (let k = 0; k < tokens.length; k++) {
    const end = pos + tokens[k].length;
    if (changed[k]) {
      const last = out[out.length - 1];
      if (last && last[1] === pos) last[1] = end; else out.push([pos, end]);
    }
    pos = end;
  }
  return out;
}

/**
 * 一對刪除行（a）與新增行（b）裡實際改了哪些字：{ a: [[起, 迄], …], b: […] }；一樣、太長、改動太多（INTRA_MAX_RATIO）或沒有共同的部分時回 null。
 * 以字詞為單位做最長共同子序列；格數超過 INTRA_MAX_CELLS 時改成去掉共同的開頭與結尾
 */
export function intraRanges(a, b) {
  a = String(a ?? ''); b = String(b ?? '');
  if (a === b || a.length > INTRA_MAX_LINE || b.length > INTRA_MAX_LINE) return null;
  const ta = wordTokens(a), tb = wordTokens(b);
  const ca = new Array(ta.length).fill(true), cb = new Array(tb.length).fill(true);
  if (ta.length * tb.length <= INTRA_MAX_CELLS) {
    // 最長共同子序列（由後往前填表，再由前往後走出共同的字詞）
    const w = tb.length + 1;
    const dp = new Uint16Array((ta.length + 1) * w);
    for (let i = ta.length - 1; i >= 0; i--) {
      for (let j = tb.length - 1; j >= 0; j--) {
        dp[i * w + j] = ta[i] === tb[j] ? dp[(i + 1) * w + j + 1] + 1 : Math.max(dp[(i + 1) * w + j], dp[i * w + j + 1]);
      }
    }
    let i = 0, j = 0;
    while (i < ta.length && j < tb.length) {
      if (ta[i] === tb[j]) { ca[i++] = false; cb[j++] = false; }
      else if (dp[(i + 1) * w + j] >= dp[i * w + j + 1]) i++;
      else j++;
    }
  } else {
    let p = 0;
    while (p < ta.length && p < tb.length && ta[p] === tb[p]) { ca[p] = false; cb[p] = false; p++; }
    let q = 0;
    while (q < ta.length - p && q < tb.length - p && ta[ta.length - 1 - q] === tb[tb.length - 1 - q]) { ca[ta.length - 1 - q] = false; cb[tb.length - 1 - q] = false; q++; }
  }
  // 只差在空白的字詞不算共同的部分：兩邊共同的都只有空白時當成整行重寫
  const shared = ta.some((x, k) => !ca[k] && /\S/.test(x));
  if (!shared) return null;
  const ra = rangesOf(ta, ca), rb = rangesOf(tb, cb);
  const size = (rs) => rs.reduce((n, [s, e]) => n + e - s, 0);
  if (size(ra) > a.length * INTRA_MAX_RATIO || size(rb) > b.length * INTRA_MAX_RATIO) return null;
  if (!ra.length && !rb.length) return null;
  return { a: ra, b: rb };
}

/** 分詞結果（[類別或 null, 文字]，可能跨行）依換行切回每一行 */
function splitLines(tokens, count) {
  const out = Array.from({ length: count }, () => []);
  let line = 0;
  for (const [type, text] of tokens) {
    const parts = String(text).split('\n');
    for (let k = 0; k < parts.length; k++) {
      if (k > 0) line++;
      if (parts[k] && line < count) out[line].push([type || null, parts[k]]);
    }
  }
  return out;
}

/** 一行的語法片段依改動的字元範圍再切開：[類別, 文字, 改動] */
function applyRanges(pieces, ranges) {
  const out = [];
  let pos = 0;
  for (const [type, text] of pieces) {
    let s = 0;
    while (s < text.length) {
      const at = pos + s;
      const r = ranges.find(([a, b]) => at >= a && at < b);
      const next = r ? r[1] : Math.min(...ranges.map(([a]) => a).filter((a) => a > at), Infinity);
      const len = Math.min(text.length - s, (Number.isFinite(next) ? next : Infinity) - at);
      const part = text.slice(s, s + len);
      const prev = out[out.length - 1];
      if (prev && prev[0] === type && prev[2] === !!r) prev[1] += part; else out.push([type, part, !!r]);
      s += len;
    }
    pos += text.length;
  }
  return out;
}

/**
 * 差異每一行的片段：[[類別或 null, 文字, 改動], …]（hunk 行整行一個不上色的片段）。
 * 語法上色：兩個 hunk 之間的一段，舊的（未變＋刪除）與新的（未變＋新增）各自整段分詞（跨行的註解、字串才對），再切回每一行；
 * tokenize 是 null（認不得語言）時只有逐字比對。什麼都沒有可加的（不上色、也沒有可比對的配對）回 null，呼叫端保持原樣
 */
export function diffSegments(lines, tokenize) {
  const n = lines.length;
  const text = (i) => (lines[i] && lines[i].s != null ? String(lines[i].s) : '');
  const kind = (i) => { const t = lines[i] && lines[i].t; return t === 'add' || t === 'del' || t === 'hunk' ? t : 'ctx'; };
  const pairs = pairLines(lines);
  const intra = new Map();
  for (const [i, j] of pairs) {
    if (kind(i) !== 'del') continue;
    const r = intraRanges(text(i), text(j));
    if (r) { intra.set(i, r.a); intra.set(j, r.b); }
  }
  if (!tokenize && !intra.size) return null;
  const syn = new Array(n).fill(null);
  if (tokenize) {
    let start = 0;
    const flush = (end) => {
      for (const side of ['old', 'new']) {
        const idx = [];
        for (let i = start; i < end; i++) {
          const k = kind(i);
          if (k === 'ctx' || (side === 'old' ? k === 'del' : k === 'add')) idx.push(i);
        }
        if (!idx.length) continue;
        const per = splitLines(tokenize(idx.map(text).join('\n')), idx.length);
        idx.forEach((i, k) => { if (!syn[i] || kind(i) !== 'ctx') syn[i] = per[k]; });
      }
    };
    for (let i = 0; i <= n; i++) {
      if (i === n || kind(i) === 'hunk') { flush(i); start = i + 1; }
    }
  }
  return Array.from({ length: n }, (_, i) => {
    const s = text(i);
    if (!s) return [];
    if (kind(i) === 'hunk') return [[null, s, false]];
    const pieces = syn[i] && syn[i].map((p) => p[1]).join('') === s ? syn[i] : [[null, s]];
    const r = intra.get(i);
    return r ? applyRanges(pieces, r) : pieces.map(([type, t]) => [type, t, false]);
  });
}
