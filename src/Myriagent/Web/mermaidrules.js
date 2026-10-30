// Mermaid 圖表純函式解析與自動排版規則（零 DOM、零外部依賴；供 mermaid.js 與單元測試共用）。
// 支援 flowchart／graph（TD、TB、BT、LR、RL）、sequenceDiagram、stateDiagram／stateDiagram-v2，
// 產出統一的純幾何圖元清單（shapes），不認識的語法或超過安全上限時回傳 { ok: false, reason } 退回程式碼框。

export const MAX_MERMAID_CHARS = 24000;
export const MAX_MERMAID_NODES = 180;
export const MAX_MERMAID_EDGES = 360;

const FONT_NODE = 13;
const FONT_EDGE = 11.5;
const FONT_SUB = 12;
const LINE_H = 18;
const EDGE_LINE_H = 16;
const PAD_CANVAS = 16;

// ───── 文字處理與寬高估算 ─────

/** 將字串中的換行標記切成多行純文字（不解析任何標籤，保留字面內容） */
export function splitLabelLines(raw) {
  let s = String(raw ?? '').trim();
  if ((s.startsWith('"') && s.endsWith('"') && s.length >= 2) ||
      (s.startsWith("'") && s.endsWith("'") && s.length >= 2)) {
    s = s.slice(1, -1);
  }
  s = s.replace(/#quot;/gi, '"').replace(/#amp;/gi, '&').replace(/#lt;/gi, '<').replace(/#gt;/gi, '>');
  const parts = s.split(/<br\s*\/?>|\\n|\r?\n/i).map((x) => x.trim());
  const nonEmpty = parts.filter((x, idx) => x.length > 0 || (parts.length > 1 && idx > 0 && idx < parts.length - 1));
  return nonEmpty.length ? nonEmpty : [''];
}

/** 估算單行文字像素寬度（中日韓全形與寬字元以全寬計，ASCII 半形以 0.6 倍計） */
export function estimateTextWidth(text, fontSize = FONT_NODE) {
  let units = 0;
  const s = String(text ?? '');
  for (let i = 0; i < s.length; i++) {
    const code = s.charCodeAt(i);
    if (code <= 0x7f) units += 0.6;
    else if (code >= 0xd800 && code <= 0xdbff) { units += 1.05; i++; }
    else units += 1.04;
  }
  return Math.ceil(units * fontSize);
}

function measureBox(lines, fontSize, padX, padY, minW = 56, minH = 34) {
  let maxW = 0;
  for (const ln of lines) maxW = Math.max(maxW, estimateTextWidth(ln, fontSize));
  const lh = fontSize <= 12 ? EDGE_LINE_H : LINE_H;
  return {
    width: Math.max(minW, maxW + padX * 2),
    height: Math.max(minH, lines.length * lh + padY * 2),
    lineHeight: lh,
  };
}

function fmt(n) {
  const r = Math.round(n * 10) / 10;
  return Number.isInteger(r) ? String(r) : r.toFixed(1);
}

/** 計算指向 (tx, ty)、方向來自 (fx, fy) 的箭頭三角形頂點與退縮後的線段終點 */
function arrowPolygon(fx, fy, tx, ty, len = 8.5, halfW = 4.2) {
  const dx = tx - fx;
  const dy = ty - fy;
  const dist = Math.hypot(dx, dy) || 1;
  const ux = dx / dist;
  const uy = dy / dist;
  const bx = tx - ux * len;
  const by = ty - uy * len;
  const px = -uy * halfW;
  const py = ux * halfW;
  return {
    points: `${fmt(tx)},${fmt(ty)} ${fmt(bx + px)},${fmt(by + py)} ${fmt(bx - px)},${fmt(by - py)}`,
    endX: tx - ux * (len * 0.72),
    endY: ty - uy * (len * 0.72),
  };
}

function makeTextShape(cls, x, y, lines, lineHeight = LINE_H, anchor = 'middle') {
  const clean = lines.filter((l) => l !== undefined && l !== null);
  if (!clean.length || (clean.length === 1 && !clean[0])) return null;
  const startY = y - ((clean.length - 1) * lineHeight) / 2;
  return {
    tag: 'text',
    cls,
    x: fmt(x),
    y: fmt(startY),
    anchor,
    lines: clean.map((t, idx) => ({ text: t, dy: idx === 0 ? '0' : String(lineHeight) })),
  };
}

// ───── 入口 ─────

export function parseMermaid(source) {
  if (typeof source !== 'string') return { ok: false, reason: 'empty' };
  const trimmed = source.trim();
  if (!trimmed) return { ok: false, reason: 'empty' };
  if (trimmed.length > MAX_MERMAID_CHARS) return { ok: false, reason: 'too_large' };

  const rawLines = trimmed.split(/\r?\n/);
  // 移除 %%{init: ...}%% 前置設定與 %% 單行註解
  const lines = [];
  let inDirective = false;
  for (const raw of rawLines) {
    const t = raw.trim();
    if (!t) continue;
    if (inDirective) {
      if (t.includes('}%%')) inDirective = false;
      continue;
    }
    if (t.startsWith('%%{')) {
      if (!t.includes('}%%')) inDirective = true;
      continue;
    }
    if (t.startsWith('%%')) continue;
    lines.push(t);
  }
  if (!lines.length) return { ok: false, reason: 'empty' };

  const first = lines[0];
  const flowMatch = /^(?:flowchart|graph)(?:\s+(TB|TD|BT|RL|LR))?\s*;?$/i.exec(first);
  if (flowMatch) {
    const dir = (flowMatch[1] || 'TD').toUpperCase();
    return parseFlowchart(lines.slice(1), dir === 'TB' ? 'TD' : dir);
  }
  if (/^sequenceDiagram\s*;?$/i.test(first)) {
    return parseSequenceDiagram(lines.slice(1));
  }
  if (/^stateDiagram(?:-v2)?\s*;?$/i.test(first)) {
    return parseStateDiagram(lines.slice(1));
  }
  if (/^(?:classDiagram|erDiagram)\s*;?$/i.test(first)) {
    const kind = /^erDiagram/i.test(first) ? 'erDiagram' : 'classDiagram';
    return parseRelationDiagram(lines.slice(1), kind);
  }
  return { ok: false, reason: 'unsupported_type' };
}

// ───── Flowchart / Graph 解析 ─────

const EDGE_OPERATORS = [
  { re: /^<==>/, style: 'thick', arrowStart: true, arrowEnd: true },
  { re: /^<-.->/, style: 'dotted', arrowStart: true, arrowEnd: true },
  { re: /^<-->/, style: 'solid', arrowStart: true, arrowEnd: true },
  { re: /^===/, style: 'thick', arrowStart: false, arrowEnd: false },
  { re: /^==>/, style: 'thick', arrowStart: false, arrowEnd: true },
  { re: /^-\.->/, style: 'dotted', arrowStart: false, arrowEnd: true },
  { re: /^-\.-/, style: 'dotted', arrowStart: false, arrowEnd: false },
  { re: /^--->/, style: 'solid', arrowStart: false, arrowEnd: true },
  { re: /^-->/, style: 'solid', arrowStart: false, arrowEnd: true },
  { re: /^--x/, style: 'solid', arrowStart: false, arrowEnd: true },
  { re: /^--o/, style: 'solid', arrowStart: false, arrowEnd: true },
  { re: /^---/, style: 'solid', arrowStart: false, arrowEnd: false },
  { re: /^~~~/, style: 'dotted', arrowStart: false, arrowEnd: false },
];

function splitStatements(bodyLines) {
  const out = [];
  for (const line of bodyLines) {
    let cur = '';
    let quote = '';
    for (let i = 0; i < line.length; i++) {
      const ch = line[i];
      if (quote) {
        cur += ch;
        if (ch === quote && line[i - 1] !== '\\') quote = '';
        continue;
      }
      if (ch === '"' || ch === "'") { quote = ch; cur += ch; continue; }
      if (ch === '%' && line[i + 1] === '%') break;
      if (ch === ';') {
        if (cur.trim()) out.push(cur.trim());
        cur = '';
        continue;
      }
      cur += ch;
    }
    if (cur.trim()) out.push(cur.trim());
  }
  return out;
}

/** 讀取從 pos 開始的括號內容，尊重內層雙引號字串，回傳 { content, nextPos } 或 null */
function readBracketContent(s, pos, openSeq, closeSeq) {
  if (!s.startsWith(openSeq, pos)) return null;
  let i = pos + openSeq.length;
  let quote = '';
  let depth = 1;
  const singleChar = openSeq.length === 1 && closeSeq.length === 1 && openSeq !== closeSeq;
  while (i < s.length) {
    const ch = s[i];
    if (quote) {
      if (ch === quote && s[i - 1] !== '\\') quote = '';
      i++;
      continue;
    }
    if (ch === '"' || ch === "'") { quote = ch; i++; continue; }
    if (s.startsWith(closeSeq, i)) {
      depth--;
      if (depth === 0) return { content: s.slice(pos + openSeq.length, i), nextPos: i + closeSeq.length };
      i += closeSeq.length;
      continue;
    }
    if (singleChar && ch === openSeq) depth++;
    i++;
  }
  return null;
}

const NODE_BRACKETS = [
  ['(((', ')))', 'doubleCircle'],
  ['((', '))', 'circle'],
  ['([', '])', 'stadium'],
  ['[[', ']]', 'subroutine'],
  ['[(', ')]', 'cylinder'],
  ['{{', '}}', 'hexagon'],
  ['[/', '/]', 'parallelogram'],
  ['[\\', '\\]', 'parallelogram'],
  ['[', ']', 'rect'],
  ['(', ')', 'round'],
  ['{', '}', 'diamond'],
];

function skipSpaces(s, pos) {
  while (pos < s.length && /\s/.test(s[pos])) pos++;
  return pos;
}

function readSingleNode(s, startPos) {
  let pos = skipSpaces(s, startPos);
  const idMatch = /^([A-Za-z0-9_.\-\u0080-\uffff]+)/.exec(s.slice(pos));
  if (!idMatch) return null;
  const id = idMatch[1];
  // 避免把保留字誤當節點
  if (/^(?:subgraph|end|direction|classDef|class|style|linkStyle|click)$/i.test(id) && pos === skipSpaces(s, 0)) {
    return null;
  }
  pos += id.length;
  let shape = null;
  let rawLabel = null;
  for (const [openSeq, closeSeq, kind] of NODE_BRACKETS) {
    const br = readBracketContent(s, pos, openSeq, closeSeq);
    if (br) {
      shape = kind;
      rawLabel = br.content;
      pos = br.nextPos;
      break;
    }
  }
  // 剝除 :::className 樣式尾綴
  const clsMatch = /^:::([A-Za-z0-9_-]+)/.exec(s.slice(pos));
  if (clsMatch) pos += clsMatch[0].length;
  return { id, shape, rawLabel, nextPos: pos };
}

/** 讀取以 `&` 並聯的一組節點：如 `A[甲] & B[乙]` */
function readNodeGroup(s, startPos) {
  const group = [];
  let pos = startPos;
  while (true) {
    const node = readSingleNode(s, pos);
    if (!node) return group.length ? { nodes: group, nextPos: pos } : null;
    group.push(node);
    pos = skipSpaces(s, node.nextPos);
    if (s[pos] === '&') {
      pos++;
      continue;
    }
    break;
  }
  return { nodes: group, nextPos: pos };
}

/** 讀取邊運算子與邊標籤（支援 `-->|文字|` 與 `-- 文字 -->`、`-. 文字 .->`、`== 文字 ==>`） */
function readEdgeOperator(s, startPos) {
  let pos = skipSpaces(s, startPos);
  const rest = s.slice(pos);

  // 先檢查夾在中間的標籤寫法：`-- 文字 -->`、`-. 文字 .->`、`== 文字 ==>`
  const inlinePatterns = [
    { re: /^--\s+(.+?)\s+-->/, style: 'solid', arrowStart: false, arrowEnd: true },
    { re: /^--\s+(.+?)\s+---/, style: 'solid', arrowStart: false, arrowEnd: false },
    { re: /^-\.\s*(.+?)\s*\.->/, style: 'dotted', arrowStart: false, arrowEnd: true },
    { re: /^-\.\s*(.+?)\s*\.-/, style: 'dotted', arrowStart: false, arrowEnd: false },
    { re: /^==\s+(.+?)\s+==>/, style: 'thick', arrowStart: false, arrowEnd: true },
    { re: /^==\s+(.+?)\s+===/, style: 'thick', arrowStart: false, arrowEnd: false },
  ];
  for (const pat of inlinePatterns) {
    const m = pat.re.exec(rest);
    if (m) {
      return {
        style: pat.style,
        arrowStart: pat.arrowStart,
        arrowEnd: pat.arrowEnd,
        label: m[1].trim(),
        nextPos: pos + m[0].length,
      };
    }
  }

  for (const op of EDGE_OPERATORS) {
    const m = op.re.exec(rest);
    if (m) {
      pos += m[0].length;
      pos = skipSpaces(s, pos);
      let label = '';
      if (s[pos] === '|') {
        const endPipe = s.indexOf('|', pos + 1);
        if (endPipe > pos) {
          label = s.slice(pos + 1, endPipe).trim();
          pos = endPipe + 1;
        }
      }
      return {
        style: op.style,
        arrowStart: op.arrowStart,
        arrowEnd: op.arrowEnd,
        label,
        nextPos: pos,
      };
    }
  }
  return null;
}

function parseSubgraphHeader(stmt, fallbackIndex) {
  const body = stmt.replace(/^subgraph(?:\s+|$)/i, '').trim();
  if (!body) return { id: 'sg_' + fallbackIndex, title: '' };
  const br = /^([A-Za-z0-9_.\-]+)\s*\[(.+)\]$/.exec(body);
  if (br) return { id: br[1], title: splitLabelLines(br[2]).join(' ') };
  return { id: body, title: splitLabelLines(body).join(' ') };
}

function parseFlowchart(bodyLines, direction) {
  const stmts = splitStatements(bodyLines);
  const nodeMap = new Map(); // id -> { id, shape, lines, subgraphId }
  const edges = [];
  const subgraphs = []; // { id, title, parentId, nodeIds: [] }
  const sgStack = [];
  let autoSgSeq = 0;
  let dir = direction;

  function upsertNode(spec) {
    let existing = nodeMap.get(spec.id);
    const curSg = sgStack.length ? sgStack[sgStack.length - 1] : null;
    if (!existing) {
      if (nodeMap.size >= MAX_MERMAID_NODES) return null;
      existing = {
        id: spec.id,
        shape: spec.shape || 'rect',
        lines: spec.rawLabel != null ? splitLabelLines(spec.rawLabel) : [spec.id],
        subgraphId: curSg ? curSg.id : null,
      };
      nodeMap.set(spec.id, existing);
      if (curSg && !curSg.nodeIds.includes(spec.id)) curSg.nodeIds.push(spec.id);
    } else {
      if (spec.shape) existing.shape = spec.shape;
      if (spec.rawLabel != null) existing.lines = splitLabelLines(spec.rawLabel);
      if (!existing.subgraphId && curSg) {
        existing.subgraphId = curSg.id;
        if (!curSg.nodeIds.includes(spec.id)) curSg.nodeIds.push(spec.id);
      }
    }
    return existing;
  }

  for (const stmt of stmts) {
    if (/^(?:classDef|class|style|linkStyle|click)\b/i.test(stmt)) continue;
    const dirMatch = /^direction\s+(TB|TD|BT|RL|LR)$/i.exec(stmt);
    if (dirMatch) {
      if (!sgStack.length) dir = dirMatch[1].toUpperCase() === 'TB' ? 'TD' : dirMatch[1].toUpperCase();
      continue;
    }
    if (/^subgraph\b/i.test(stmt)) {
      const hdr = parseSubgraphHeader(stmt, ++autoSgSeq);
      const parent = sgStack.length ? sgStack[sgStack.length - 1] : null;
      const sg = { id: hdr.id + '#' + subgraphs.length, rawId: hdr.id, title: hdr.title, parentId: parent ? parent.id : null, nodeIds: [], childIds: [] };
      if (parent) parent.childIds.push(sg.id);
      subgraphs.push(sg);
      sgStack.push(sg);
      continue;
    }
    if (/^end$/i.test(stmt)) {
      if (sgStack.length) sgStack.pop();
      continue;
    }

    const firstGroup = readNodeGroup(stmt, 0);
    if (!firstGroup) continue;
    let prevNodes = [];
    for (const n of firstGroup.nodes) {
      const created = upsertNode(n);
      if (!created) return { ok: false, reason: 'too_many_nodes' };
      prevNodes.push(created);
    }
    let pos = firstGroup.nextPos;
    while (pos < stmt.length) {
      const op = readEdgeOperator(stmt, pos);
      if (!op) break;
      const nextGroup = readNodeGroup(stmt, op.nextPos);
      if (!nextGroup) break;
      const curNodes = [];
      for (const n of nextGroup.nodes) {
        const created = upsertNode(n);
        if (!created) return { ok: false, reason: 'too_many_nodes' };
        curNodes.push(created);
      }
      for (const a of prevNodes) {
        for (const b of curNodes) {
          if (edges.length >= MAX_MERMAID_EDGES) return { ok: false, reason: 'too_many_edges' };
          edges.push({
            from: a.id,
            to: b.id,
            labelLines: op.label ? splitLabelLines(op.label) : [],
            style: op.style,
            arrowStart: op.arrowStart,
            arrowEnd: op.arrowEnd,
          });
        }
      }
      prevNodes = curNodes;
      pos = nextGroup.nextPos;
    }
  }

  if (!nodeMap.size) return { ok: false, reason: 'no_nodes' };
  return layoutDirectedGraph('flowchart', dir, Array.from(nodeMap.values()), edges, subgraphs);
}

// ───── State Diagram 解析 ─────

function parseStateDiagram(bodyLines) {
  const stmts = splitStatements(bodyLines);
  const nodeMap = new Map();
  const edges = [];
  const subgraphs = [];
  const sgStack = [];
  let starSeq = 0;

  function ensureState(rawId, labelOverride, shapeOverride) {
    if (rawId === '[*]') {
      const id = '__state_star_' + (++starSeq);
      const shape = shapeOverride || 'startState';
      const st = { id, shape, lines: [''], subgraphId: sgStack.length ? sgStack[sgStack.length - 1].id : null };
      nodeMap.set(id, st);
      if (sgStack.length) sgStack[sgStack.length - 1].nodeIds.push(id);
      return st;
    }
    const id = splitLabelLines(rawId)[0] || rawId;
    let st = nodeMap.get(id);
    const curSg = sgStack.length ? sgStack[sgStack.length - 1] : null;
    if (!st) {
      if (nodeMap.size >= MAX_MERMAID_NODES) return null;
      st = {
        id,
        shape: shapeOverride || 'round',
        lines: labelOverride ? splitLabelLines(labelOverride) : [id],
        subgraphId: curSg ? curSg.id : null,
      };
      nodeMap.set(id, st);
      if (curSg && !curSg.nodeIds.includes(id)) curSg.nodeIds.push(id);
    } else if (labelOverride) {
      st.lines = splitLabelLines(labelOverride);
    }
    return st;
  }

  let dir = 'TD';
  for (const stmt of stmts) {
    const dirM = /^direction\s+(TB|TD|BT|RL|LR)$/i.exec(stmt);
    if (dirM) {
      if (!sgStack.length) dir = dirM[1].toUpperCase() === 'TB' ? 'TD' : dirM[1].toUpperCase();
      continue;
    }
    if (stmt === '}') {
      if (sgStack.length) sgStack.pop();
      continue;
    }
    const compM = /^state\s+(?:"([^"]+)"\s+as\s+)?([A-Za-z0-9_.\-\u0080-\uffff]+)\s*\{$/i.exec(stmt);
    if (compM) {
      const sgId = compM[2] + '#' + subgraphs.length;
      const parent = sgStack.length ? sgStack[sgStack.length - 1] : null;
      const sg = { id: sgId, rawId: compM[2], title: compM[1] || compM[2], parentId: parent ? parent.id : null, nodeIds: [], childIds: [] };
      if (parent) parent.childIds.push(sg.id);
      subgraphs.push(sg);
      sgStack.push(sg);
      continue;
    }
    const aliasM = /^state\s+"([^"]+)"\s+as\s+([A-Za-z0-9_.\-\u0080-\uffff]+)$/i.exec(stmt);
    if (aliasM) {
      if (!ensureState(aliasM[2], aliasM[1])) return { ok: false, reason: 'too_many_nodes' };
      continue;
    }
    const transM = /^(\[\*\]|[A-Za-z0-9_.\-\u0080-\uffff"]+)\s*-->\s*(\[\*\]|[A-Za-z0-9_.\-\u0080-\uffff"]+)(?:\s*:\s*(.+))?$/.exec(stmt);
    if (transM) {
      const a = ensureState(transM[1].trim(), null, transM[1].trim() === '[*]' ? 'startState' : null);
      const b = ensureState(transM[2].trim(), null, transM[2].trim() === '[*]' ? 'endState' : null);
      if (!a || !b) return { ok: false, reason: 'too_many_nodes' };
      if (edges.length >= MAX_MERMAID_EDGES) return { ok: false, reason: 'too_many_edges' };
      edges.push({
        from: a.id,
        to: b.id,
        labelLines: transM[3] ? splitLabelLines(transM[3]) : [],
        style: 'solid',
        arrowStart: false,
        arrowEnd: true,
      });
      continue;
    }
    const descM = /^([A-Za-z0-9_.\-\u0080-\uffff]+)\s*:\s*(.+)$/.exec(stmt);
    if (descM && !/^note\b/i.test(descM[1])) {
      const existing = nodeMap.get(descM[1]);
      if (existing) {
        const merged = existing.lines.length === 1 && existing.lines[0] === existing.id
          ? [existing.id, descM[2].trim()]
          : existing.lines.concat([descM[2].trim()]);
        existing.lines = merged;
      } else if (!ensureState(descM[1], `${descM[1]}\n${descM[2].trim()}`)) {
        return { ok: false, reason: 'too_many_nodes' };
      }
    }
  }

  // 若子狀態圖名稱與邊端點重名，讓指向複合狀態的邊連到第一個子節點
  for (const sg of subgraphs) {
    if (!sg.nodeIds.length) continue;
    const firstChild = sg.nodeIds[0];
    for (const e of edges) {
      if (e.from === sg.rawId && !nodeMap.has(sg.rawId)) e.from = firstChild;
      if (e.to === sg.rawId && !nodeMap.has(sg.rawId)) e.to = firstChild;
    }
  }

  if (!nodeMap.size) return { ok: false, reason: 'no_nodes' };
  return layoutDirectedGraph('stateDiagram', dir, Array.from(nodeMap.values()), edges, subgraphs);
}

// ───── Class / ER 基礎關聯圖解析 ─────

function parseRelationDiagram(bodyLines, kind) {
  const nodeMap = new Map();
  const edges = [];
  let currentBlock = null;

  function ensureEntity(rawName) {
    const id = rawName.replace(/^"|"$/g, '').trim();
    if (!id) return null;
    let ent = nodeMap.get(id);
    if (!ent) {
      if (nodeMap.size >= MAX_MERMAID_NODES) return null;
      ent = { id, shape: 'rect', lines: [id], members: [], subgraphId: null };
      nodeMap.set(id, ent);
    }
    return ent;
  }

  for (const line of bodyLines) {
    if (currentBlock) {
      if (line === '}') { currentBlock = null; continue; }
      if (currentBlock.members.length < 10) currentBlock.members.push(line);
      continue;
    }
    const openM = /^(?:class\s+)?([A-Za-z0-9_.\-\u0080-\uffff"]+)\s*\{$/.exec(line);
    if (openM) {
      currentBlock = ensureEntity(openM[1]);
      continue;
    }
    const clsDecl = /^class\s+([A-Za-z0-9_.\-\u0080-\uffff"]+)$/.exec(line);
    if (clsDecl) {
      ensureEntity(clsDecl[1]);
      continue;
    }
    const memberM = /^([A-Za-z0-9_.\-\u0080-\uffff]+)\s*:\s*(.+)$/.exec(line);
    if (memberM && kind === 'classDiagram' && !/[<>|*-]{2}/.test(line)) {
      const ent = ensureEntity(memberM[1]);
      if (ent && ent.members.length < 10) ent.members.push(memberM[2].trim());
      continue;
    }
    // 關聯邊（支援 classDiagram 的 <|--, *--, o--, -->, ..> 與 erDiagram 的 ||--o{, }o--|| 等）
    const relM = /^([A-Za-z0-9_.\-\u0080-\uffff"]+)\s+(?:"[^"]*"\s+)?([<|*o}{.\-=]+)\s+(?:"[^"]*"\s+)?([A-Za-z0-9_.\-\u0080-\uffff"]+)(?:\s*:\s*(.+))?$/.exec(line);
    if (relM) {
      const a = ensureEntity(relM[1]);
      const b = ensureEntity(relM[3]);
      if (!a || !b) return { ok: false, reason: 'too_many_nodes' };
      const op = relM[2];
      const dotted = op.includes('..');
      const arrowStart = op.startsWith('<');
      const arrowEnd = op.endsWith('>') || (!arrowStart && kind === 'classDiagram');
      edges.push({
        from: a.id,
        to: b.id,
        labelLines: relM[4] ? splitLabelLines(relM[4]) : [],
        style: dotted ? 'dotted' : 'solid',
        arrowStart,
        arrowEnd,
      });
    }
  }

  for (const ent of nodeMap.values()) {
    if (ent.members.length) {
      ent.lines = [ent.id, '────────'].concat(ent.members);
    }
  }
  if (!nodeMap.size) return { ok: false, reason: 'no_nodes' };
  return layoutDirectedGraph(kind, 'TD', Array.from(nodeMap.values()), edges, []);
}

// ───── Sugiyama 有向圖階層排版與幾何產生 ─────

function sizeGraphNode(n) {
  if (n.shape === 'startState') return { width: 20, height: 20, lineHeight: LINE_H };
  if (n.shape === 'endState') return { width: 24, height: 24, lineHeight: LINE_H };
  if (n.shape === 'circle' || n.shape === 'doubleCircle') {
    const base = measureBox(n.lines, FONT_NODE, 14, 10, 44, 44);
    const d = Math.max(base.width, base.height) + (n.shape === 'doubleCircle' ? 8 : 0);
    return { width: d, height: d, lineHeight: base.lineHeight };
  }
  if (n.shape === 'diamond') {
    const base = measureBox(n.lines, FONT_NODE, 18, 12, 68, 44);
    return { width: Math.ceil(base.width * 1.38), height: Math.ceil(base.height * 1.38), lineHeight: base.lineHeight };
  }
  if (n.shape === 'hexagon' || n.shape === 'parallelogram') {
    return measureBox(n.lines, FONT_NODE, 22, 10, 76, 38);
  }
  if (n.shape === 'cylinder') {
    const base = measureBox(n.lines, FONT_NODE, 16, 14, 68, 44);
    return { width: base.width, height: base.height + 10, lineHeight: base.lineHeight };
  }
  if (n.shape === 'subroutine') {
    return measureBox(n.lines, FONT_NODE, 20, 10, 72, 36);
  }
  return measureBox(n.lines, FONT_NODE, 16, 10, 64, 36);
}

function layoutDirectedGraph(kind, direction, nodes, edges, subgraphs) {
  const horiz = direction === 'LR' || direction === 'RL';
  const reverseAxis = direction === 'BT' || direction === 'RL';
  const byId = new Map();
  nodes.forEach((n, idx) => {
    const sz = sizeGraphNode(n);
    n.width = sz.width;
    n.height = sz.height;
    n.lineHeight = sz.lineHeight;
    n.orderHint = idx;
    byId.set(n.id, n);
  });

  // 1. DFS 標記回邊（破環）與自迴圈
  const outAdj = new Map(nodes.map((n) => [n.id, []]));
  const workEdges = [];
  for (const e of edges) {
    if (!byId.has(e.from) || !byId.has(e.to)) continue;
    if (e.from === e.to) {
      workEdges.push({ ...e, selfLoop: true, reversed: false });
      continue;
    }
    outAdj.get(e.from).push(e);
    workEdges.push({ ...e, selfLoop: false, reversed: false });
  }

  const state = new Map(); // 0=未訪, 1=訪問中, 2=完成
  function dfs(u) {
    state.set(u, 1);
    for (const we of workEdges) {
      if (we.selfLoop || we.from !== u) continue;
      const st = state.get(we.to) || 0;
      if (st === 1) we.reversed = true;
      else if (st === 0) dfs(we.to);
    }
    state.set(u, 2);
  }
  for (const n of nodes) if (!state.get(n.id)) dfs(n.id);

  // 2. 分層（Longest-path rank assignment）
  const rank = new Map(nodes.map((n) => [n.id, 0]));
  const dagEdges = workEdges.filter((e) => !e.selfLoop).map((e) => ({
    u: e.reversed ? e.to : e.from,
    v: e.reversed ? e.from : e.to,
    raw: e,
  }));
  for (let iter = 0; iter < nodes.length; iter++) {
    let changed = false;
    for (const de of dagEdges) {
      const nextR = (rank.get(de.u) || 0) + 1;
      if (nextR > (rank.get(de.v) || 0)) {
        rank.set(de.v, nextR);
        changed = true;
      }
    }
    if (!changed) break;
  }

  let maxRank = 0;
  for (const r of rank.values()) maxRank = Math.max(maxRank, r);
  const layers = Array.from({ length: maxRank + 1 }, () => []);
  for (const n of nodes) layers[rank.get(n.id) || 0].push(n);

  // 3. 重心法減少交叉（同時將同一個 subgraph 的節點微幅拉近）
  const sgOrder = new Map();
  subgraphs.forEach((sg, idx) => sgOrder.set(sg.id, idx + 1));

  for (let sweep = 0; sweep < 4; sweep++) {
    const forward = sweep % 2 === 0;
    const start = forward ? 1 : layers.length - 2;
    const end = forward ? layers.length : -1;
    const step = forward ? 1 : -1;
    for (let r = start; r !== end; r += step) {
      const prevLayer = layers[r - step];
      const posInPrev = new Map();
      prevLayer.forEach((n, idx) => posInPrev.set(n.id, idx));
      layers[r].forEach((n, curIdx) => {
        const neighbors = [];
        for (const de of dagEdges) {
          if (forward && de.v === n.id && posInPrev.has(de.u)) neighbors.push(posInPrev.get(de.u));
          else if (!forward && de.u === n.id && posInPrev.has(de.v)) neighbors.push(posInPrev.get(de.v));
        }
        const sgBias = n.subgraphId ? (sgOrder.get(n.subgraphId) || 0) * 0.02 : 0;
        n._bary = neighbors.length
          ? neighbors.reduce((a, b) => a + b, 0) / neighbors.length + sgBias
          : curIdx + sgBias;
      });
      layers[r].sort((a, b) => (a._bary - b._bary) || (a.orderHint - b.orderHint));
    }
  }

  // 4. 計算各層座標（若有子圖與邊標籤則保留充裕間距）
  const hasSubgraphs = subgraphs.length > 0;
  const crossGap = (horiz ? 28 : 34) + (hasSubgraphs ? 18 : 0);
  const baseLayerGap = horiz ? 68 : 56;
  const layerSpan = layers.map((layer) => {
    let m = 0;
    for (const n of layer) m = Math.max(m, horiz ? n.width : n.height);
    return m;
  });
  const layerCrossTotal = layers.map((layer) => {
    let sum = 0;
    layer.forEach((n, i) => {
      sum += horiz ? n.height : n.width;
      if (i > 0) sum += crossGap;
    });
    return sum;
  });
  const maxCross = Math.max(120, ...layerCrossTotal);

  // 若相鄰層之間有帶文字標籤的邊，自動撐開該層間距
  const gapAfterLayer = new Array(layers.length).fill(baseLayerGap + (hasSubgraphs ? 20 : 0));
  for (const de of dagEdges) {
    if (!de.raw.labelLines.length) continue;
    const ru = rank.get(de.u) || 0;
    const labelBox = measureBox(de.raw.labelLines, FONT_EDGE, 8, 4, 30, 20);
    const needed = (horiz ? labelBox.width + 34 : labelBox.height + 36);
    gapAfterLayer[ru] = Math.max(gapAfterLayer[ru], needed);
  }

  const orderedLayers = reverseAxis ? layers.slice().reverse() : layers;
  const orderedSpan = reverseAxis ? layerSpan.slice().reverse() : layerSpan;
  const orderedGaps = reverseAxis ? gapAfterLayer.slice(0, -1).reverse().concat([0]) : gapAfterLayer;

  let mainCursor = 0;
  orderedLayers.forEach((layer, li) => {
    const span = orderedSpan[li];
    const crossTotal = layer.reduce((acc, n, idx) => acc + (horiz ? n.height : n.width) + (idx > 0 ? crossGap : 0), 0);
    let crossCursor = (maxCross - crossTotal) / 2;
    for (const n of layer) {
      if (horiz) {
        n.cx = mainCursor + span / 2;
        n.cy = crossCursor + n.height / 2;
        crossCursor += n.height + crossGap;
      } else {
        n.cx = crossCursor + n.width / 2;
        n.cy = mainCursor + span / 2;
        crossCursor += n.width + crossGap;
      }
    }
    mainCursor += span + orderedGaps[li];
  });

  // 5. 計算 Subgraph 包圍框（由內而外）
  const sgById = new Map(subgraphs.map((s) => [s.id, s]));
  const laidSubgraphs = [];
  function computeSgBounds(sg) {
    if (sg._bounds) return sg._bounds;
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    for (const nid of sg.nodeIds) {
      const n = byId.get(nid);
      if (!n) continue;
      minX = Math.min(minX, n.cx - n.width / 2);
      maxX = Math.max(maxX, n.cx + n.width / 2);
      minY = Math.min(minY, n.cy - n.height / 2);
      maxY = Math.max(maxY, n.cy + n.height / 2);
    }
    for (const cid of sg.childIds) {
      const child = sgById.get(cid);
      const cb = child ? computeSgBounds(child) : null;
      if (!cb) continue;
      minX = Math.min(minX, cb.x);
      maxX = Math.max(maxX, cb.x + cb.width);
      minY = Math.min(minY, cb.y);
      maxY = Math.max(maxY, cb.y + cb.height);
    }
    if (!Number.isFinite(minX)) return null;
    const titleW = sg.title ? estimateTextWidth(sg.title, FONT_SUB) + 28 : 0;
    const padSide = 16;
    const padTop = sg.title ? 30 : 16;
    const padBot = 14;
    const rawW = maxX - minX + padSide * 2;
    const finalW = Math.max(rawW, titleW);
    const cx = (minX + maxX) / 2;
    sg._bounds = {
      id: sg.rawId,
      title: sg.title,
      x: cx - finalW / 2,
      y: minY - padTop,
      width: finalW,
      height: maxY - minY + padTop + padBot,
    };
    laidSubgraphs.push(sg._bounds);
    return sg._bounds;
  }
  for (const sg of subgraphs) computeSgBounds(sg);

  // 6. 產生邊曲線與標籤（並納入整體畫布邊界）
  const shapes = [];
  const edgeShapes = [];
  const labelShapes = [];

  // 統計同一對節點之間的多重邊以錯開曲線偏移
  const pairCount = new Map();
  const pairSeen = new Map();
  for (const we of workEdges) {
    const key = we.from < we.to ? `${we.from}|${we.to}` : `${we.to}|${we.from}`;
    pairCount.set(key, (pairCount.get(key) || 0) + 1);
  }

  let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
  function includeRect(x, y, w, h) {
    minX = Math.min(minX, x);
    minY = Math.min(minY, y);
    maxX = Math.max(maxX, x + w);
    maxY = Math.max(maxY, y + h);
  }
  for (const n of nodes) includeRect(n.cx - n.width / 2, n.cy - n.height / 2, n.width, n.height);
  for (const sb of laidSubgraphs) includeRect(sb.x, sb.y, sb.width, sb.height);

  // 每條邊加的圖元（線、箭頭、標籤）標上兩端的節點 id（from、to），網頁滑過節點時照它亮起相連的線（mermaid.js）
  let edgeMark = null;
  const tagEdgeShapes = () => {
    if (!edgeMark) return;
    for (let i = edgeMark.e0; i < edgeShapes.length; i++) { edgeShapes[i].from = edgeMark.we.from; edgeShapes[i].to = edgeMark.we.to; }
    for (let i = edgeMark.l0; i < labelShapes.length; i++) { labelShapes[i].from = edgeMark.we.from; labelShapes[i].to = edgeMark.we.to; }
    edgeMark = null;
  };
  for (const we of workEdges) {
    tagEdgeShapes();
    edgeMark = { we, e0: edgeShapes.length, l0: labelShapes.length };
    const src = byId.get(we.from);
    const dst = byId.get(we.to);
    if (!src || !dst) continue;
    const edgeCls = 'mm-edge' + (we.style === 'dotted' ? ' mm-dotted' : we.style === 'thick' ? ' mm-thick' : '');

    if (we.selfLoop) {
      const rx = src.cx + src.width / 2;
      const topY = src.cy - 10;
      const botY = src.cy + 10;
      const loopW = 34;
      const arrow = we.arrowEnd ? arrowPolygon(rx + loopW, botY + 8, rx + 1, botY) : null;
      const ex = arrow ? arrow.endX : rx;
      const ey = arrow ? arrow.endY : botY;
      const d = `M ${fmt(rx)} ${fmt(topY)} C ${fmt(rx + loopW)} ${fmt(topY - 14)}, ${fmt(rx + loopW)} ${fmt(botY + 14)}, ${fmt(ex)} ${fmt(ey)}`;
      edgeShapes.push({ tag: 'path', cls: edgeCls, d });
      if (arrow) edgeShapes.push({ tag: 'polygon', cls: 'mm-arrow', points: arrow.points });
      includeRect(rx, topY - 14, loopW + 8, (botY - topY) + 28);
      if (we.labelLines.length) {
        const lb = measureBox(we.labelLines, FONT_EDGE, 6, 3, 24, 18);
        const lx = rx + loopW + lb.width / 2 - 4;
        const ly = src.cy;
        labelShapes.push({ tag: 'rect', cls: 'mm-edge-bg', x: fmt(lx - lb.width / 2), y: fmt(ly - lb.height / 2), width: fmt(lb.width), height: fmt(lb.height), rx: '4' });
        const ts = makeTextShape('mm-edge-text', lx, ly, we.labelLines, lb.lineHeight);
        if (ts) labelShapes.push(ts);
        includeRect(lx - lb.width / 2, ly - lb.height / 2, lb.width, lb.height);
      }
      continue;
    }

    const key = we.from < we.to ? `${we.from}|${we.to}` : `${we.to}|${we.from}`;
    const total = pairCount.get(key) || 1;
    const idx = pairSeen.get(key) || 0;
    pairSeen.set(key, idx + 1);
    const offset = total > 1 ? (idx - (total - 1) / 2) * 26 : 0;

    const pStart = borderPoint(src, dst.cx, dst.cy, offset, horiz);
    const pEnd = borderPoint(dst, src.cx, src.cy, offset, horiz);
    const dx = pEnd.x - pStart.x;
    const dy = pEnd.y - pStart.y;
    const nx = -dy / (Math.hypot(dx, dy) || 1);
    const ny = dx / (Math.hypot(dx, dy) || 1);

    let cx1, cy1, cx2, cy2;
    if (horiz) {
      cx1 = pStart.x + dx * 0.42 + nx * offset;
      cy1 = pStart.y + dy * 0.1 + ny * offset;
      cx2 = pStart.x + dx * 0.58 + nx * offset;
      cy2 = pEnd.y - dy * 0.1 + ny * offset;
    } else {
      cx1 = pStart.x + dx * 0.1 + nx * offset;
      cy1 = pStart.y + dy * 0.42 + ny * offset;
      cx2 = pEnd.x - dx * 0.1 + nx * offset;
      cy2 = pStart.y + dy * 0.58 + ny * offset;
    }

    let sx = pStart.x, sy = pStart.y, ex = pEnd.x, ey = pEnd.y;
    let startArrow = null, endArrow = null;
    if (we.arrowStart) {
      startArrow = arrowPolygon(cx1, cy1, sx, sy);
      sx = startArrow.endX;
      sy = startArrow.endY;
    }
    if (we.arrowEnd) {
      endArrow = arrowPolygon(cx2, cy2, ex, ey);
      ex = endArrow.endX;
      ey = endArrow.endY;
    }

    const d = `M ${fmt(sx)} ${fmt(sy)} C ${fmt(cx1)} ${fmt(cy1)}, ${fmt(cx2)} ${fmt(cy2)}, ${fmt(ex)} ${fmt(ey)}`;
    edgeShapes.push({ tag: 'path', cls: edgeCls, d });
    if (startArrow) edgeShapes.push({ tag: 'polygon', cls: 'mm-arrow', points: startArrow.points });
    if (endArrow) edgeShapes.push({ tag: 'polygon', cls: 'mm-arrow', points: endArrow.points });

    if (we.labelLines.length) {
      // 三次貝茲曲線 t=0.5 中點
      const mx = 0.125 * sx + 0.375 * cx1 + 0.375 * cx2 + 0.125 * ex;
      const my = 0.125 * sy + 0.375 * cy1 + 0.375 * cy2 + 0.125 * ey;
      const lb = measureBox(we.labelLines, FONT_EDGE, 7, 3, 24, 18);
      labelShapes.push({
        tag: 'rect',
        cls: 'mm-edge-bg',
        x: fmt(mx - lb.width / 2),
        y: fmt(my - lb.height / 2),
        width: fmt(lb.width),
        height: fmt(lb.height),
        rx: '4',
      });
      const ts = makeTextShape('mm-edge-text', mx, my, we.labelLines, lb.lineHeight);
      if (ts) labelShapes.push(ts);
      includeRect(mx - lb.width / 2, my - lb.height / 2, lb.width, lb.height);
    }
  }
  tagEdgeShapes();

  // 外層 Subgraph 先畫、內層後畫（面積大的在底層）
  laidSubgraphs.sort((a, b) => (b.width * b.height) - (a.width * a.height));
  for (const sb of laidSubgraphs) {
    shapes.push({
      tag: 'rect',
      cls: 'mm-subgraph',
      x: fmt(sb.x),
      y: fmt(sb.y),
      width: fmt(sb.width),
      height: fmt(sb.height),
      rx: '8',
    });
    if (sb.title) {
      const ts = makeTextShape('mm-subgraph-title', sb.x + 12, sb.y + 16, [sb.title], EDGE_LINE_H, 'start');
      if (ts) shapes.push(ts);
    }
  }

  shapes.push(...edgeShapes, ...labelShapes);

  // 7. 節點幾何與文字
  for (const n of nodes) {
    shapes.push(...buildNodeShapes(n).map((sh) => Object.assign(sh, { nid: n.id }))); // 節點的圖元與文字標上節點 id
  }

  // 8. 平移所有座標，使左上角固定為 PAD_CANVAS
  const shiftX = PAD_CANVAS - minX;
  const shiftY = PAD_CANVAS - minY;
  const totalW = Math.ceil(maxX - minX + PAD_CANVAS * 2);
  const totalH = Math.ceil(maxY - minY + PAD_CANVAS * 2);
  shiftShapes(shapes, shiftX, shiftY);

  return {
    ok: true,
    kind,
    direction,
    width: totalW,
    height: totalH,
    viewBox: `0 0 ${totalW} ${totalH}`,
    nodes: nodes.map((n) => ({
      id: n.id,
      shape: n.shape,
      lines: n.lines,
      x: Math.round((n.cx - n.width / 2 + shiftX) * 10) / 10,
      y: Math.round((n.cy - n.height / 2 + shiftY) * 10) / 10,
      width: n.width,
      height: n.height,
    })),
    edges: workEdges.map((e) => ({ from: e.from, to: e.to, label: e.labelLines.join('\n'), style: e.style })),
    subgraphs: laidSubgraphs.map((s) => ({
      id: s.id,
      title: s.title,
      x: Math.round((s.x + shiftX) * 10) / 10,
      y: Math.round((s.y + shiftY) * 10) / 10,
      width: Math.round(s.width * 10) / 10,
      height: Math.round(s.height * 10) / 10,
    })),
    shapes,
  };
}

/** 計算節點外框朝目標點 (tx, ty) 的射線交點 */
function borderPoint(node, tx, ty, offset, horiz) {
  let dx = tx - node.cx;
  let dy = ty - node.cy;
  if (offset) {
    if (horiz) dy += offset * 0.35;
    else dx += offset * 0.35;
  }
  if (dx === 0 && dy === 0) dy = 1;
  const hw = node.width / 2;
  const hh = node.height / 2;

  if (node.shape === 'circle' || node.shape === 'doubleCircle' || node.shape === 'startState' || node.shape === 'endState') {
    const r = Math.min(hw, hh);
    const len = Math.hypot(dx, dy) || 1;
    return { x: node.cx + (dx / len) * r, y: node.cy + (dy / len) * r };
  }
  if (node.shape === 'diamond') {
    // 菱形邊界：|x|/hw + |y|/hh = 1
    const scale = 1 / (Math.abs(dx) / hw + Math.abs(dy) / hh);
    return { x: node.cx + dx * scale, y: node.cy + dy * scale };
  }
  const sx = hw / (Math.abs(dx) || 0.0001);
  const sy = hh / (Math.abs(dy) || 0.0001);
  const s = Math.min(sx, sy);
  return { x: node.cx + dx * s, y: node.cy + dy * s };
}

function buildNodeShapes(n) {
  const out = [];
  const x = n.cx - n.width / 2;
  const y = n.cy - n.height / 2;
  const w = n.width;
  const h = n.height;

  if (n.shape === 'startState') {
    out.push({ tag: 'circle', cls: 'mm-state-start', cx: fmt(n.cx), cy: fmt(n.cy), r: '8' });
    return out;
  }
  if (n.shape === 'endState') {
    out.push({ tag: 'circle', cls: 'mm-node', cx: fmt(n.cx), cy: fmt(n.cy), r: '10' });
    out.push({ tag: 'circle', cls: 'mm-state-start', cx: fmt(n.cx), cy: fmt(n.cy), r: '5.5' });
    return out;
  }
  if (n.shape === 'circle') {
    out.push({ tag: 'circle', cls: 'mm-node', cx: fmt(n.cx), cy: fmt(n.cy), r: fmt(w / 2) });
  } else if (n.shape === 'doubleCircle') {
    out.push({ tag: 'circle', cls: 'mm-node', cx: fmt(n.cx), cy: fmt(n.cy), r: fmt(w / 2) });
    out.push({ tag: 'circle', cls: 'mm-node-inner', cx: fmt(n.cx), cy: fmt(n.cy), r: fmt(w / 2 - 4) });
  } else if (n.shape === 'diamond') {
    const pts = `${fmt(n.cx)},${fmt(y)} ${fmt(x + w)},${fmt(n.cy)} ${fmt(n.cx)},${fmt(y + h)} ${fmt(x)},${fmt(n.cy)}`;
    out.push({ tag: 'polygon', cls: 'mm-node', points: pts });
  } else if (n.shape === 'hexagon') {
    const cut = Math.min(14, w * 0.18);
    const pts = `${fmt(x + cut)},${fmt(y)} ${fmt(x + w - cut)},${fmt(y)} ${fmt(x + w)},${fmt(n.cy)} ${fmt(x + w - cut)},${fmt(y + h)} ${fmt(x + cut)},${fmt(y + h)} ${fmt(x)},${fmt(n.cy)}`;
    out.push({ tag: 'polygon', cls: 'mm-node', points: pts });
  } else if (n.shape === 'parallelogram') {
    const slant = Math.min(12, w * 0.14);
    const pts = `${fmt(x + slant)},${fmt(y)} ${fmt(x + w)},${fmt(y)} ${fmt(x + w - slant)},${fmt(y + h)} ${fmt(x)},${fmt(y + h)}`;
    out.push({ tag: 'polygon', cls: 'mm-node', points: pts });
  } else if (n.shape === 'cylinder') {
    const ry = 6;
    const bodyTop = y + ry;
    const bodyBot = y + h - ry;
    const d = `M ${fmt(x)} ${fmt(bodyTop)} L ${fmt(x)} ${fmt(bodyBot)} A ${fmt(w / 2)} ${ry} 0 0 0 ${fmt(x + w)} ${fmt(bodyBot)} L ${fmt(x + w)} ${fmt(bodyTop)} Z`;
    out.push({ tag: 'path', cls: 'mm-node', d });
    out.push({ tag: 'ellipse', cls: 'mm-node', cx: fmt(n.cx), cy: fmt(bodyTop), rx: fmt(w / 2), ry: String(ry) });
  } else if (n.shape === 'subroutine') {
    out.push({ tag: 'rect', cls: 'mm-node', x: fmt(x), y: fmt(y), width: fmt(w), height: fmt(h), rx: '5' });
    out.push({ tag: 'line', cls: 'mm-node-inner', x1: fmt(x + 7), y1: fmt(y), x2: fmt(x + 7), y2: fmt(y + h) });
    out.push({ tag: 'line', cls: 'mm-node-inner', x1: fmt(x + w - 7), y1: fmt(y), x2: fmt(x + w - 7), y2: fmt(y + h) });
  } else {
    const rx = n.shape === 'stadium' ? Math.round(h / 2) : n.shape === 'round' ? 10 : 6;
    out.push({ tag: 'rect', cls: 'mm-node', x: fmt(x), y: fmt(y), width: fmt(w), height: fmt(h), rx: String(rx) });
  }

  const textY = n.shape === 'cylinder' ? n.cy + 3 : n.cy;
  const ts = makeTextShape('mm-text', n.cx, textY, n.lines, n.lineHeight);
  if (ts) out.push(ts);
  return out;
}

function shiftShapes(shapes, dx, dy) {
  if (Math.abs(dx) < 0.05 && Math.abs(dy) < 0.05) return;
  for (const s of shapes) {
    if (s.tag === 'rect') {
      s.x = fmt(Number(s.x) + dx);
      s.y = fmt(Number(s.y) + dy);
    } else if (s.tag === 'circle' || s.tag === 'ellipse') {
      s.cx = fmt(Number(s.cx) + dx);
      s.cy = fmt(Number(s.cy) + dy);
    } else if (s.tag === 'line') {
      s.x1 = fmt(Number(s.x1) + dx);
      s.y1 = fmt(Number(s.y1) + dy);
      s.x2 = fmt(Number(s.x2) + dx);
      s.y2 = fmt(Number(s.y2) + dy);
    } else if (s.tag === 'text') {
      s.x = fmt(Number(s.x) + dx);
      s.y = fmt(Number(s.y) + dy);
    } else if (s.tag === 'polygon') {
      s.points = s.points.split(/\s+/).map((pt) => {
        const [px, py] = pt.split(',').map(Number);
        return `${fmt(px + dx)},${fmt(py + dy)}`;
      }).join(' ');
    } else if (s.tag === 'path') {
      s.d = shiftPathD(s.d, dx, dy);
    }
  }
}

function shiftPathD(d, dx, dy) {
  // 只使用我們產生的 M / L / C / A / Z 大寫絕對指令格式
  const tokens = d.trim().split(/[\s,]+/);
  const out = [];
  let i = 0;
  while (i < tokens.length) {
    const cmd = tokens[i++];
    out.push(cmd);
    if (cmd === 'Z') continue;
    if (cmd === 'M' || cmd === 'L') {
      out.push(fmt(Number(tokens[i++]) + dx), fmt(Number(tokens[i++]) + dy));
    } else if (cmd === 'C') {
      for (let k = 0; k < 3; k++) {
        out.push(fmt(Number(tokens[i++]) + dx), fmt(Number(tokens[i++]) + dy));
      }
    } else if (cmd === 'A') {
      out.push(tokens[i++], tokens[i++], tokens[i++], tokens[i++], tokens[i++]);
      out.push(fmt(Number(tokens[i++]) + dx), fmt(Number(tokens[i++]) + dy));
    }
  }
  return out.join(' ');
}

// ───── Sequence Diagram 解析與排版 ─────

function parseSequenceDiagram(bodyLines) {
  const participants = [];
  const partMap = new Map();
  const events = []; // { type: 'msg'|'note'|'blockStart'|'blockElse'|'blockEnd', ... }
  let autonumber = false;
  let msgSeq = 0;

  function ensurePart(id, label, isActor = false) {
    const cleanId = id.replace(/^"|"$/g, '').trim();
    if (!cleanId) return null;
    let p = partMap.get(cleanId);
    if (!p) {
      if (partMap.size >= MAX_MERMAID_NODES) return null;
      p = {
        id: cleanId,
        lines: splitLabelLines(label || cleanId),
        isActor,
        index: participants.length,
      };
      partMap.set(cleanId, p);
      participants.push(p);
    } else if (label) {
      p.lines = splitLabelLines(label);
    }
    return p;
  }

  for (const line of bodyLines) {
    if (/^autonumber\b/i.test(line)) { autonumber = true; continue; }
    if (/^(?:activate|deactivate|links|link|title)\b/i.test(line)) continue;

    const declM = /^(participant|actor)\s+("[^"]+"|[^\s]+)(?:\s+as\s+(.+))?$/i.exec(line);
    if (declM) {
      if (!ensurePart(declM[2], declM[3], declM[1].toLowerCase() === 'actor')) {
        return { ok: false, reason: 'too_many_nodes' };
      }
      continue;
    }

    const noteM = /^Note\s+(left of|right of|over)\s+([^:]+):\s*(.+)$/i.exec(line);
    if (noteM) {
      const pos = noteM[1].toLowerCase();
      const targets = noteM[2].split(',').map((x) => ensurePart(x.trim())).filter(Boolean);
      if (targets.length) {
        events.push({ type: 'note', pos, targets: targets.map((t) => t.id), lines: splitLabelLines(noteM[3]) });
      }
      continue;
    }

    const blkStart = /^(loop|alt|opt|par|critical|break|rect)\b(?:\s+(.*))?$/i.exec(line);
    if (blkStart) {
      events.push({ type: 'blockStart', kind: blkStart[1].toLowerCase(), label: (blkStart[2] || '').trim() });
      continue;
    }
    const blkElse = /^(else|and)\b(?:\s+(.*))?$/i.exec(line);
    if (blkElse) {
      events.push({ type: 'blockElse', kind: blkElse[1].toLowerCase(), label: (blkElse[2] || '').trim() });
      continue;
    }
    if (/^end$/i.test(line)) {
      events.push({ type: 'blockEnd' });
      continue;
    }

    const msgM = /^("[^"]+"|[A-Za-z0-9_.\-\u0080-\uffff]+)\s*(--?>>|--?[x)]|->|-->)([+-])?\s*("[^"]+"|[A-Za-z0-9_.\-\u0080-\uffff]+)\s*:\s*(.*)$/.exec(line);
    if (msgM) {
      const from = ensurePart(msgM[1]);
      const to = ensurePart(msgM[4]);
      if (!from || !to) return { ok: false, reason: 'too_many_nodes' };
      msgSeq++;
      const prefix = autonumber ? `${msgSeq}. ` : '';
      const rawText = (prefix + (msgM[5] || '').trim()).trim();
      events.push({
        type: 'msg',
        from: from.id,
        to: to.id,
        dotted: msgM[2].startsWith('--'),
        lines: rawText ? splitLabelLines(rawText) : [],
      });
    }
  }

  if (!participants.length) return { ok: false, reason: 'no_nodes' };

  // 計算參與者方框大小與各欄 X 座標
  for (const p of participants) {
    const sz = measureBox(p.lines, FONT_NODE, 16, 9, 84, 36);
    p.width = sz.width;
    p.height = sz.height;
    p.lineHeight = sz.lineHeight;
  }

  const colGap = new Array(Math.max(0, participants.length - 1)).fill(120);
  for (let i = 0; i < colGap.length; i++) {
    colGap[i] = Math.max(120, (participants[i].width + participants[i + 1].width) / 2 + 44);
  }
  for (const ev of events) {
    if (ev.type !== 'msg' || ev.from === ev.to || !ev.lines.length) continue;
    const a = partMap.get(ev.from).index;
    const b = partMap.get(ev.to).index;
    const lo = Math.min(a, b);
    const hi = Math.max(a, b);
    const needed = measureBox(ev.lines, FONT_EDGE, 12, 4, 40, 18).width + 36;
    const perSpan = Math.ceil(needed / (hi - lo));
    for (let k = lo; k < hi; k++) colGap[k] = Math.max(colGap[k], perSpan);
  }

  let cursorX = PAD_CANVAS + participants[0].width / 2;
  participants[0].cx = cursorX;
  for (let i = 1; i < participants.length; i++) {
    cursorX += colGap[i - 1];
    participants[i].cx = cursorX;
  }

  const headerH = Math.max(...participants.map((p) => p.height));
  const topY = PAD_CANVAS;
  let y = topY + headerH + 24;

  const blockShapes = [];
  const lifelineShapes = [];
  const fgShapes = [];
  const blockStack = [];

  let minX = PAD_CANVAS;
  let maxX = participants[participants.length - 1].cx + participants[participants.length - 1].width / 2;

  for (const ev of events) {
    if (ev.type === 'blockStart') {
      blockStack.push({ kind: ev.kind, label: ev.label, startY: y - 8, dividers: [] });
      y += 24;
      continue;
    }
    if (ev.type === 'blockElse') {
      const top = blockStack[blockStack.length - 1];
      if (top) {
        top.dividers.push({ y, label: ev.label });
        y += 24;
      }
      continue;
    }
    if (ev.type === 'blockEnd') {
      const blk = blockStack.pop();
      if (blk) {
        y += 8;
        const bx = minX - 6;
        const bw = (maxX - minX) + 12;
        const bh = Math.max(32, y - blk.startY);
        blockShapes.push({ tag: 'rect', cls: 'mm-subgraph', x: fmt(bx), y: fmt(blk.startY), width: fmt(bw), height: fmt(bh), rx: '6' });
        const tagText = blk.kind.toUpperCase();
        const tagW = estimateTextWidth(tagText, 11) + 16;
        blockShapes.push({
          tag: 'polygon',
          cls: 'mm-seq-tag',
          points: `${fmt(bx)},${fmt(blk.startY)} ${fmt(bx + tagW)},${fmt(blk.startY)} ${fmt(bx + tagW)},${fmt(blk.startY + 14)} ${fmt(bx + tagW - 6)},${fmt(blk.startY + 20)} ${fmt(bx)},${fmt(blk.startY + 20)}`,
        });
        const kt = makeTextShape('mm-subgraph-title', bx + 7, blk.startY + 10.5, [tagText], 14, 'start');
        if (kt) blockShapes.push(kt);
        if (blk.label) {
          const lt = makeTextShape('mm-edge-text', bx + tagW + 8, blk.startY + 10.5, [`[${blk.label}]`], 14, 'start');
          if (lt) blockShapes.push(lt);
        }
        for (const div of blk.dividers) {
          blockShapes.push({ tag: 'line', cls: 'mm-edge mm-dotted', x1: fmt(bx), y1: fmt(div.y), x2: fmt(bx + bw), y2: fmt(div.y) });
          if (div.label) {
            const dt = makeTextShape('mm-edge-text', bx + 10, div.y + 12, [`[${div.label}]`], 14, 'start');
            if (dt) blockShapes.push(dt);
          }
        }
        minX = Math.min(minX, bx);
        maxX = Math.max(maxX, bx + bw);
        y += 10;
      }
      continue;
    }
    if (ev.type === 'note') {
      const box = measureBox(ev.lines, FONT_EDGE, 12, 8, 76, 28);
      const firstP = partMap.get(ev.targets[0]);
      const lastP = partMap.get(ev.targets[ev.targets.length - 1]);
      let nx;
      if (ev.pos === 'left of') nx = firstP.cx - firstP.width / 2 - box.width / 2 - 12;
      else if (ev.pos === 'right of') nx = lastP.cx + lastP.width / 2 + box.width / 2 + 12;
      else nx = (firstP.cx + lastP.cx) / 2;
      fgShapes.push({ tag: 'rect', cls: 'mm-note', x: fmt(nx - box.width / 2), y: fmt(y), width: fmt(box.width), height: fmt(box.height), rx: '5' });
      const ts = makeTextShape('mm-text', nx, y + box.height / 2, ev.lines, box.lineHeight);
      if (ts) fgShapes.push(ts);
      minX = Math.min(minX, nx - box.width / 2);
      maxX = Math.max(maxX, nx + box.width / 2);
      y += box.height + 16;
      continue;
    }
    if (ev.type === 'msg') {
      const fp = partMap.get(ev.from);
      const tp = partMap.get(ev.to);
      const lineCls = 'mm-edge' + (ev.dotted ? ' mm-dotted' : '');
      const textBox = ev.lines.length ? measureBox(ev.lines, FONT_EDGE, 6, 2, 20, 16) : { width: 0, height: 0, lineHeight: EDGE_LINE_H };
      if (ev.from === ev.to) {
        const labelH = ev.lines.length ? ev.lines.length * EDGE_LINE_H + 4 : 4;
        y += labelH;
        if (ev.lines.length) {
          const ts = makeTextShape('mm-edge-text', fp.cx + 10, y - labelH / 2 - 2, ev.lines, EDGE_LINE_H, 'start');
          if (ts) fgShapes.push(ts);
          maxX = Math.max(maxX, fp.cx + 14 + textBox.width);
        }
        const loopW = 34;
        const loopH = 20;
        const arrow = arrowPolygon(fp.cx + loopW, y + loopH, fp.cx + 2, y + loopH);
        const d = `M ${fmt(fp.cx)} ${fmt(y)} L ${fmt(fp.cx + loopW)} ${fmt(y)} L ${fmt(fp.cx + loopW)} ${fmt(y + loopH)} L ${fmt(arrow.endX)} ${fmt(y + loopH)}`;
        fgShapes.push({ tag: 'path', cls: lineCls, d });
        fgShapes.push({ tag: 'polygon', cls: 'mm-arrow', points: arrow.points });
        maxX = Math.max(maxX, fp.cx + loopW + 8);
        y += loopH + 18;
      } else {
        const labelH = ev.lines.length ? ev.lines.length * EDGE_LINE_H + 6 : 6;
        y += labelH;
        const midX = (fp.cx + tp.cx) / 2;
        if (ev.lines.length) {
          fgShapes.push({
            tag: 'rect',
            cls: 'mm-edge-bg',
            x: fmt(midX - textBox.width / 2),
            y: fmt(y - labelH + 1),
            width: fmt(textBox.width),
            height: fmt(labelH - 4),
            rx: '3',
          });
          const ts = makeTextShape('mm-edge-text', midX, y - labelH / 2 - 1, ev.lines, EDGE_LINE_H);
          if (ts) fgShapes.push(ts);
        }
        const arrow = arrowPolygon(fp.cx, y, tp.cx, y);
        fgShapes.push({ tag: 'line', cls: lineCls, x1: fmt(fp.cx), y1: fmt(y), x2: fmt(arrow.endX), y2: fmt(y) });
        fgShapes.push({ tag: 'polygon', cls: 'mm-arrow', points: arrow.points });
        y += 18;
      }
    }
  }

  const footerY = y + 6;
  for (const p of participants) {
    lifelineShapes.push({
      tag: 'line',
      cls: 'mm-lifeline',
      x1: fmt(p.cx),
      y1: fmt(topY + headerH),
      x2: fmt(p.cx),
      y2: fmt(footerY),
    });
    // 上下兩端參與者盒
    for (const boxY of [topY + (headerH - p.height) / 2, footerY]) {
      fgShapes.push({
        tag: 'rect',
        cls: 'mm-node',
        x: fmt(p.cx - p.width / 2),
        y: fmt(boxY),
        width: fmt(p.width),
        height: fmt(p.height),
        rx: p.isActor ? '16' : '6',
      });
      const ts = makeTextShape('mm-text', p.cx, boxY + p.height / 2, p.lines, p.lineHeight);
      if (ts) fgShapes.push(ts);
    }
  }

  const shapes = [...blockShapes, ...lifelineShapes, ...fgShapes];
  const shiftX = PAD_CANVAS - minX;
  shiftShapes(shapes, shiftX, 0);
  const totalW = Math.ceil((maxX - minX) + PAD_CANVAS * 2);
  const totalH = Math.ceil(footerY + headerH + PAD_CANVAS);

  return {
    ok: true,
    kind: 'sequenceDiagram',
    direction: 'TD',
    width: totalW,
    height: totalH,
    viewBox: `0 0 ${totalW} ${totalH}`,
    nodes: participants.map((p) => ({
      id: p.id,
      shape: p.isActor ? 'actor' : 'participant',
      lines: p.lines,
      x: Math.round((p.cx - p.width / 2 + shiftX) * 10) / 10,
      y: topY,
      width: p.width,
      height: p.height,
    })),
    edges: events.filter((e) => e.type === 'msg').map((e) => ({
      from: e.from,
      to: e.to,
      label: e.lines.join('\n'),
      style: e.dotted ? 'dotted' : 'solid',
    })),
    subgraphs: [],
    shapes,
  };
}
