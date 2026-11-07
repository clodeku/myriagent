// 對話項目的繪製與就地更新。代理產生的文字一律 textContent；只有欄位名稱以 html 結尾的字串用 innerHTML。
import { h, baseName, extOf, copyToClipboard, iconButton, replayClass, fadeInImg, setHidden, setAcc, safeColor } from './util.js';
import { icon, TOOL_ICON } from './icons.js';
import { post, isHub, state } from './store.js';
import { authorLabel, resolvedLabel, collabColor } from './collabrules.js';
import { mentionParts, mentionView } from './accountmentions.js';
import { toast, spinner } from './ui.js';
import { settleText, itemTimeText, itemTimeTitle } from './liverules.js';
import { diffRatioCells, codeLineCount, toolSettleFx, parseRetryDelayMs, retryCountdown, retryOutcome, toolPeekLines, thinkPeekLines, toolPeekTiming, PEEK_ROWS, PEEK_CUT_MS, elapsedShort, SLOW_TOOL_MS, waitAgeLabel, STALE_WAIT_MS, thinkDuration, thinkTimeLabel, diffMapRuns, diffMapGradient, planCells, planNewlyDone, commandOutcome, outcomeChips, diagLineRanges, toolHits, subagentProgress, remoteExecOf, remoteExecChips, mcpToolOf, delegationOf, delegateChild, delegationChips, handoffLabels, pathParts } from './fxrules.js';
import { calmMotion } from './theme.js';
import { tokenCounter } from './tokenfx.js';
import { queueHighlight, queueDiff, queueGutter } from './syntax.js';
import { commandLang, commandParts, detailLang, outputLang, guessLang } from './syntaxrules.js';
import { enhanceMermaidBlock, queueMermaid, flushMermaid } from './mermaid.js';
import { framedVideo } from './framedvideo.js';
import { agentUsageView } from './claudeoptrules.js';
import { noteFieldShown, buildNotes, splitAnswer, NOTE_MAX, progressCells } from './questionrules.js';

const DIFF_LIMIT = 1200;
const COPY_FX_MS = 900; // 複製成功的閃光（fx.css 的 .fx-copied）
const OPEN_FX_MS = 300; // 展開時內容淡入（fx.css 的 .fx-open）

// ───── 入口 ─────
const RENDER = {
  User: { create: createUser, fill: fillUser },
  Assistant: { create: createAssistant, fill: fillAssistant },
  Thinking: { create: createThinking, fill: fillThinking },
  Tool: { create: createTool, fill: fillTool },
  Question: { create: createQuestion, sig: (it) => JSON.stringify([it.status, it.decision, it.answers, it.questions, it.answerAsMessage, it.output, it.detailHtml, it.filePath]) },
  Approval: { create: createApproval, sig: (it) => JSON.stringify([it.status, it.decision, it.toolName, it.title, it.detail, it.canAllowForSession, it.allowSessionLabel, it.diff && it.diff.length, it.output]) },
  PlanApproval: { create: createPlanApproval, sig: (it) => JSON.stringify([it.status, it.decision, it.detailHtml, it.output]) },
  Plan: { create: createPlan, fill: fillPlan },
  Notice: { create: (it, c) => createNote(it, c, 'notice'), fill: fillNote },
  Error: { create: (it, c) => createNote(it, c, 'error'), fill: fillNote },
};

export function createItem(item, ctx) {
  const r = RENDER[item.kind] || RENDER.Notice;
  const node = r.create(item, ctx);
  if (r.sig) node._sig = r.sig(item);
  return node;
}

/** 就地更新；必要時回傳新的節點（呼叫端負責替換） */
export function updateItem(node, item, prev, ctx) {
  const r = RENDER[item.kind] || RENDER.Notice;
  if (!prev || prev.kind !== item.kind) return createItem(item, ctx);
  if (r.fill) { r.fill(node, item, prev, ctx); return node; }
  if (r.sig) {
    const sig = r.sig(item);
    if (sig === node._sig) return node; // 沒有實質變化：保留 DOM（避免打斷正在輸入的答案）
    return createItem(item, ctx);
  }
  return createItem(item, ctx);
}

function shell(item, cls) {
  return h('div', { class: 'it ' + cls, dataset: { id: item.id, kind: item.kind } });
}

/** 訊息後方的時間（滑過看完整時間）；跨過午夜時由 refreshTimes 重畫（今天的只有時:分，之後要加日期） */
function timeEl(item) {
  return h('div', { class: 'it-time', text: itemTimeText(item.time, Date.now()), title: itemTimeTitle(item.time) || null, dataset: { ms: String(item.time || '') } });
}

/** 重畫 root 底下所有訊息的時間（ConvView 跨過午夜時） */
export function refreshTimes(root, now) {
  for (const n of root.querySelectorAll('.it-time[data-ms]')) n.textContent = itemTimeText(Number(n.dataset.ms), now);
}

// ───── Markdown 強化：程式碼區塊（語言標籤＋複製）、表格捲動、圖片燈箱 ─────
export function setHtml(container, html, ctx) {
  container.innerHTML = html || '';
  enhanceMarkdown(container, ctx);
  container._blocks = null;
}

/**
 * 串流中的就地更新：宿主每 50ms 送一次整則的 html，這裡只換掉內容有變的頂層區塊（通常只有最後一段），
 * 前面已完成的段落、程式碼區塊（含複製鈕）與連結保留原節點，點擊、拖曳選取與程式碼的水平捲動不會被打斷。
 */
export function patchHtml(container, html, ctx, streaming = false) {
  const tmp = document.createElement('div');
  tmp.innerHTML = html || '';
  const next = Array.from(tmp.childNodes);
  let old = container._blocks;
  if (!old) { container.textContent = ''; old = []; }
  let keep = 0;
  while (keep < old.length && keep < next.length && old[keep].raw === rawHtmlOf(next[keep])) keep++;
  for (let i = keep; i < old.length; i++) for (const node of old[i].nodes) node.remove();
  const blocks = old.slice(0, keep);
  for (let i = keep; i < next.length; i++) {
    const raw = rawHtmlOf(next[i]);
    const wrap = document.createElement('div');
    wrap.append(next[i]);
    enhanceMarkdown(wrap, ctx, streaming && i === next.length - 1 ? { deferMermaid: true } : null); // 程式碼區塊會被包成 div.code，所以記下強化後的節點
    const nodes = Array.from(wrap.childNodes);
    container.append(...nodes);
    blocks.push({ raw, nodes });
  }
  container._blocks = blocks;
}

function rawHtmlOf(node) {
  return node.nodeType === Node.ELEMENT_NODE ? node.outerHTML : '#' + node.nodeType + ':' + node.nodeValue;
}

/**
 * 行內程式碼裡的路徑與網址（第十一批，fxrules.pathParts）：分隔符號後面可以換行（窄窗格不撐出橫向捲動），
 * 最後一段（檔名）加亮、前面的資料夾變淡。只處理只有一段純文字的行內程式碼；程式碼區塊（pre）不碰。只用文字節點
 */
function decoratePaths(root) {
  for (const code of root.querySelectorAll('code')) {
    if (code.closest('pre') || code.classList.contains('md-path') || code.childNodes.length !== 1 || code.firstChild.nodeType !== Node.TEXT_NODE) continue;
    const pp = pathParts(code.firstChild.nodeValue);
    if (!pp) continue;
    const dir = h('span', { class: 'p-dir' });
    pp.parts.slice(0, pp.base).forEach((part) => { dir.append(document.createTextNode(part), document.createElement('wbr')); });
    code.replaceChildren(dir, h('span', { class: 'p-base', text: pp.parts.slice(pp.base).join('') }));
    code.classList.add('md-path');
  }
}

export function enhanceMarkdown(root, _ctx, opts) {
  decoratePaths(root);
  for (const pre of root.querySelectorAll('pre')) {
    if (pre.parentElement && pre.parentElement.classList.contains('code')) continue;
    const code = pre.querySelector('code');
    const m = code ? /language-([\w+#.-]+)/.exec(code.className || '') : null;
    const lang = m ? m[1] : '';
    const copy = h('button', { type: 'button', class: 'code-copy', title: '複製程式碼' }, icon('copy'), h('span', { text: '複製' }));
    copy.addEventListener('click', async () => {
      // 寫不進去時交給宿主（copyText）：桌面由宿主複製；瀏覽器版由傳輸層開退路框（copyFallback），不標「已複製」（WP4.6 D7）
      const ok = await copyToClipboard((code || pre).textContent, (text) => post({ cmd: 'copyText', text }));
      if (ok || !isHub) { setCopyButton(copy, 'done'); replayClass(block, 'fx-copied', COPY_FX_MS); }
    });
    const lines = codeLineCount((code || pre).textContent);
    const block = h('div', { class: 'code' }, h('div', { class: 'code-head' }, h('span', { class: 'code-lang', text: lang || '文字' }),
      lines ? h('span', { class: 'code-lines', text: lines + ' 行' }) : null, copy));
    pre.replaceWith(block);
    block.append(pre);
    if (code && lang.toLowerCase() === 'mermaid') {
      if (opts && opts.deferMermaid) queueMermaid(block, code);
      else enhanceMermaidBlock(block, code);
    }
    // 兩行以上加行號欄（不能選取、不會被複製；橫向捲動時固定在左邊，fx.css 的 .code.has-ln）：和語法上色一樣靜置一下才加
    // （串流中還在長的程式碼框每次更新都重建，不加；行數變多也不會讓程式碼往右跳，syntax.js 的 queueGutter）
    if (lines >= 2) queueGutter(block, lines);
    // 語法上色：靜置一下還在畫面上才上（串流中還在長的區塊不上，syntax.js）；沒標語言的猜一下（syntaxrules.guessLang：JSON、指令、XML）
    if (code) queueHighlight(code, lang || guessLang(code.textContent));
  }
  for (const t of root.querySelectorAll('table')) {
    if (t.parentElement && t.parentElement.classList.contains('tbl')) continue;
    const w = h('div', { class: 'tbl' });
    t.replaceWith(w);
    w.append(t);
  }
  for (const img of root.querySelectorAll('img')) {
    img.loading = 'lazy';
    img.decoding = 'async';
    img.classList.add('md-img');
    fadeInImg(img);
    const src = img.getAttribute('src') || '';
    if (src) {
      img.dataset.lbUrl = img.src;
      const p = pathFromMediaUrl(img.src);
      img.dataset.lbName = img.alt || baseName(p || src);
      if (p) img.dataset.lbPath = p;
    }
  }
}

/** 複製鈕的狀態：'busy'（複製中…）、'done'（已複製，1.4 秒後回到「複製」）、其他（複製） */
function setCopyButton(btn, st) {
  clearTimeout(btn._copyTimer);
  btn.classList.toggle('done', st === 'done');
  btn.classList.toggle('busy', st === 'busy');
  btn.lastChild.textContent = st === 'done' ? '已複製' : st === 'busy' ? '複製中…' : '複製';
  if (st === 'done') btn._copyTimer = setTimeout(() => setCopyButton(btn, 'idle'), 1400);
}

/** 〔遠端〕Assistant 項目的 copyItem 有結果了（ConvView.markCopied）：寫入成功標「已複製」，否則回到「複製」 */
export function markItemCopied(node, ok) {
  if (node && node._copy) setCopyButton(node._copy, ok ? 'done' : 'idle');
  if (node && ok) replayClass(node, 'fx-copied', COPY_FX_MS);
}

export function pathFromMediaUrl(url) {
  try { return new URL(url, location.href).searchParams.get('path'); } catch { return null; }
}

/**
 * 語音訊息的錄音波形（附件的 wave：錄音時麥克風實際的音量，一格一個 0～9 的數字，宿主 VoiceMessageRules.NormalizeWaveform）：
 * 一格一條、高度照數字（fx.css 的 .att-wave）；不是那個格式回 null（照常顯示檔案大小）
 */
function voiceWave(wave) {
  if (typeof wave !== 'string' || !/^[0-9]{1,64}$/.test(wave)) return null;
  return h('span', { class: 'att-wave', 'aria-hidden': 'true' }, ...Array.from(wave, (d) => h('i', { style: { '--h': d } })));
}

// ───── User ─────
function createUser(item, ctx) {
  const n = shell(item, 'it-user');
  n._box = h('div', { class: 'u-box' });
  // 框的右下方：來源（從 claude.ai 或 Claude App〔Remote Control〕送來的才有）與送出時間
  const meta = h('div', { class: 'u-meta' });
  // 多人協作：誰送的（協作人送的標出名稱；協作人看擁有者送的標「管理員」）
  const author = authorLabel(item, state.viewer);
  if (author) meta.append(h('div', { class: 'u-author' + (item.author ? ' is-collab' : ''), title: item.author ? '這則訊息是協作人送的' : '這則訊息是擁有者送的' }, icon('user'), h('span', { text: author })));
  // 協作人的顏色（第十一批，collabrules.collabColor）：名稱前的小方塊與框左緣的線，多人一起用時分得出誰說的
  const authorTint = item.author ? collabColor(item.author) : null;
  if (authorTint) { n.classList.add('by-collab'); n.style.setProperty('--collab', authorTint); }
  if (item.source === 'remote') {
    meta.append(h('div', { class: 'u-src', title: '這則訊息是從 claude.ai 或 Claude App 送出的（Remote Control）' },
      icon('globe'), h('span', { text: '從 claude.ai／Claude App 送出' })));
  }
  // 回溯（只有桌面這台的 Claude 對話：對話欄的 data-rewind 決定顯示，rewind.js）
  if (ctx.rewind) {
    const rw = h('button', { type: 'button', class: 'u-rewind', title: '回溯到這則訊息之前（對話、檔案）' }, icon('clock'), h('span', { text: '回溯' }));
    rw.addEventListener('click', () => ctx.rewind(item.id));
    meta.append(rw);
  }
  meta.append(timeEl(item));
  n.append(n._box, meta);
  fillUser(n, item, null, ctx);
  return n;
}

function fillUser(n, item, prev, ctx) {
  if (prev && prev.html === item.html && sameJson(prev.attachments, item.attachments)) return;
  const box = n._box;
  box.textContent = '';
  if (item.attachments && item.attachments.length) box.append(renderAttachments(item.attachments, ctx));
  if (item.html) {
    const t = h('div', { class: 'md u-text' });
    setHtml(t, item.html, ctx);
    chipMentions(t);
    box.append(t);
  }
}

/**
 * 使用者訊息裡的帳戶提及（@[名稱](account:id)，帳戶委派）畫成帳戶色的晶片（第十批）：
 * 使用者訊息是純文字轉的 HTML，原本照原文露出整串；複製出來是「@名稱」
 */
export function chipMentions(root) {
  if (!root || !root.textContent.includes('](account:')) return;
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  const hits = [];
  for (let t = walker.nextNode(); t; t = walker.nextNode()) if (t.data.includes('](account:')) hits.push(t);
  for (const t of hits) {
    const parts = mentionParts(t.data);
    if (parts) t.replaceWith(...parts.map((p) => (typeof p === 'string' ? p : mentionChip(p.label, p.id))));
  }
}

function mentionChip(label, id) {
  const v = mentionView(label, id, state.accountMap && state.accountMap.get(id));
  const chip = h('span', { class: 'acct-mention' + (v.known ? '' : ' unknown'), title: v.title, dataset: { account: id } },
    h('span', { class: 'am-dot', 'aria-hidden': 'true' }), v.text);
  if (v.known) setAcc(chip, v.color, v.provider);
  return chip;
}

// ───── Assistant ─────
function createAssistant(item, ctx) {
  const n = shell(item, 'it-assistant');
  n._md = h('div', { class: 'md' });
  // 複製整則：宿主把這則的 Markdown 原文寫進剪貼簿（copyItem），保留清單、表格、程式碼的格式。
  // 〔遠端〕瀏覽器版：伺服器回 clipboard、網頁寫入之後才標「已複製」，等的時候是「複製中…」（liveview.requestCopy → ConvView.markCopied，WP4.6 D7）
  const copy = h('button', { type: 'button', class: 'code-copy', title: '複製這則回覆（Markdown）' }, icon('copy'), h('span', { text: '複製' }));
  copy.addEventListener('click', () => {
    if (isHub) {
      if (!copy.classList.contains('busy') && ctx.copyItem && ctx.copyItem(item.id)) setCopyButton(copy, 'busy');
      return;
    }
    ctx.post('copyItem', { itemId: item.id });
    setCopyButton(copy, 'done');
    replayClass(n, 'fx-copied', COPY_FX_MS); // 整則的四角括號亮綠、閃兩下（同程式碼框的複製）
  });
  n._copy = copy;
  n._acts = h('div', { class: 'it-acts' }, copy);
  n.append(n._md, n._acts, timeEl(item));
  fillAssistant(n, item, null, ctx);
  return n;
}

function fillAssistant(n, item, prev, ctx) {
  const running = item.status === 'Running';
  n.classList.toggle('running', running);
  if (!prev || prev.html !== item.html) patchHtml(n._md, item.html, ctx, running);
  if (prev && prev.status === 'Running' && !running) flushMermaid(n._md);
  setHidden(n._acts, running || !item.html); // 串流中還不完整，完成後才給複製
  if (!prev || !sameJson(prev.media, item.media)) {
    if (n._media) { n._media.remove(); n._media = null; }
    if (item.media && item.media.length) { n._media = renderMedia(item.media, ctx); n.insertBefore(n._media, n._acts); }
  }
}

// ───── Thinking（預設展開，2026-10-25 使用者要求；收起來的記在 ctx.collapsed，收著時內容在展開時才填，省下長篇思考的解析成本） ─────
function createThinking(item, ctx) {
  const n = shell(item, 'it-thinking it-step');
  const d = h('details', { class: 'think' });
  const label = h('span', { class: 'think-label' });
  // 思考的耗時（每秒由 conversation.tick 呼叫 tickThinkTimes：執行中從項目的時間起算，結束後以下一個項目的時間推算）
  const time = h('span', { class: 'think-time' });
  const sum = h('summary', { class: 'think-sum' }, h('span', { class: 'step-dot' }), h('span', { class: 'think-glyph', text: '✻' }), label, time, icon('chevRight', 'think-chev'));
  const md = h('div', { class: 'md think-md' });
  d.append(sum, md);
  d.open = !ctx.collapsed.has(item.id);
  // 點標題展開時內容淡入（只動透明度與位移、不動高度）；建立時就展開的（預設展開、沒收起來過的）不播
  sum.addEventListener('click', () => { n._userToggle = true; });
  d.addEventListener('toggle', () => {
    if (d.open) stopStepPeek(n); // 正在掃過一遍的小框讓給真的內容
    if (d.open) { ctx.collapsed.delete(item.id); if (n._pendingHtml !== undefined) { patchHtml(md, n._pendingHtml, ctx); n._pendingHtml = undefined; } }
    else ctx.collapsed.add(item.id);
    if (d.open && n._userToggle) replayClass(md, 'fx-open', OPEN_FX_MS);
    n._userToggle = false;
  });
  n._d = d; n._md = md; n._label = label; n._time = time;
  n.append(d);
  fillThinking(n, item, null, ctx);
  return n;
}

function fillThinking(n, item, prev) {
  const running = item.status === 'Running';
  n.classList.toggle('running', running);
  n._label.textContent = running ? '思考中…' : n._durMs != null ? '思考了' : '思考過程';
  if (!running && n._durMs == null && prev && prev.status === 'Running') n._time.textContent = '';
  if (!prev || prev.html !== item.html) {
    if (n._d.open) { patchHtml(n._md, item.html); n._pendingHtml = undefined; } else n._pendingHtml = item.html || '';
  }
}

// ───── Tool：精簡單行，點開看輸入、差異、輸出 ─────
function createTool(item, ctx) {
  const n = shell(item, 'it-tool it-step');
  const row = h('div', { class: 't-row', role: 'button', tabIndex: 0, 'aria-expanded': 'false' });
  n._dot = h('span', { class: 'step-dot' });
  n._ic = h('span', { class: 't-icwrap' });
  n._name = h('span', { class: 't-name' });
  n._title = h('span', { class: 't-title' });
  n._stats = h('span', { class: 't-stats' });
  // 執行中的工具已經跑了多久（每秒由 conversation.tick 呼叫 tickToolElapsed 更新；超過 30 秒變琥珀色）
  n._elapsed = h('span', { class: 't-elapsed', hidden: true });
  row.append(n._dot, n._ic, n._name, n._title, n._stats, n._elapsed, icon('chevRight', 't-chev'));
  const toggle = () => {
    stopStepPeek(n); // 正在掃過一遍的小框讓給真的內容
    if (ctx.expanded.has(item.id)) ctx.expanded.delete(item.id); else ctx.expanded.add(item.id);
    renderToolBody(n, n._item, ctx);
    if (n._body) replayClass(n._body, 'fx-open', OPEN_FX_MS); // 點開時內容淡入（同思考區塊；執行中重建內容時不播）
  };
  row.addEventListener('click', (e) => { if (!e.target.closest('a,button')) toggle(); });
  row.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(); } });
  n._row = row;
  n.append(row);
  fillTool(n, item, null, ctx);
  return n;
}

/** 執行中工具的計時：已經跑了多久、超過 SLOW_TOOL_MS 標成慢（琥珀色） */
function paintElapsed(el, now) {
  const ms = now - Number(el.dataset.since);
  const text = elapsedShort(ms);
  if (el.textContent !== text) el.textContent = text;
  el.classList.toggle('slow', ms >= SLOW_TOOL_MS);
}

/** 每秒（conversation.tick，只有看得到的對話）：執行中的工具卡的計時 */
export function tickToolElapsed(root, now) {
  for (const el of root.querySelectorAll('.t-elapsed[data-since]')) paintElapsed(el, now);
}

/**
 * 每秒（conversation.tick）：思考的耗時。執行中的寫「思考中… 12 秒」；結束的以下一個項目的時間推算（fxrules.thinkDuration），
 * 寫成「思考了 18 秒」；下一個項目來了就標上 think-timed、不再算（推算不出來的照舊「思考過程」），還沒來的下一秒再看
 */
export function tickThinkTimes(root, now, itemOf, order) {
  const nodes = root.querySelectorAll('.it-thinking:not(.think-timed)');
  if (!nodes.length) return;
  let pos = null;
  for (const n of nodes) {
    const it = itemOf(n.dataset.id);
    if (!it || !n._time || !Number.isFinite(it.time)) continue;
    if (it.status === 'Running') {
      const text = thinkTimeLabel(now - it.time);
      if (n._time.textContent !== text) n._time.textContent = text;
      continue;
    }
    pos = pos || new Map(order.map((id, i) => [id, i]));
    const i = pos.get(it.id);
    const next = i == null ? null : itemOf(order[i + 1]);
    if (!next) continue;
    // 下一個項目來了就只算這一次：推算不出來的（被停下、隔太久）照舊寫「思考過程」
    n.classList.add('think-timed');
    const ms = thinkDuration(it, next);
    if (ms == null) continue;
    n._durMs = ms;
    n._label.textContent = '思考了';
    n._time.textContent = thinkTimeLabel(ms);
    n.classList.add('think-timed');
  }
}

/**
 * 每秒（conversation.tick）：等你作答的卡片標題列右邊寫出已經等了多久（項目的時間起算，fxrules.waitAgeLabel），超過 STALE_WAIT_MS 那顆燈變琥珀色。
 * 卡片重畫過（新的節點）就補一個；文字沒變不動 DOM
 */
export function tickCardAges(root, now, itemOf) {
  for (const n of root.querySelectorAll('.it-card.waiting')) {
    const it = itemOf(n.dataset.id);
    if (!it || !Number.isFinite(it.time)) continue;
    if (!n._age || !n._age.isConnected) { n._age = h('span', { class: 'card-age', 'aria-hidden': 'true' }); (n.querySelector(':scope > .card-head') || n).append(n._age); }
    const ms = now - it.time;
    const text = waitAgeLabel(ms);
    if (n._age.textContent !== text) n._age.textContent = text;
    n._age.classList.toggle('stale', ms >= STALE_WAIT_MS);
  }
}

const TOOL_STATUS = { Running: '執行中', Waiting: '等待核准', Done: '完成', Failed: '失敗', Declined: '已拒絕', Cancelled: '已停止' };

function fillTool(n, item, prev, ctx) {
  n._item = item;
  n.dataset.status = item.status || 'Done';
  const settle = toolSettleFx(prev && prev.status, item.status);
  if (settle) replayClass(n, settle, 900);
  const kind = TOOL_ICON[item.toolKind] ? item.toolKind : 'other';
  if (!prev || prev.toolKind !== item.toolKind) { n._ic.textContent = ''; n._ic.append(icon(TOOL_ICON[kind], 't-ic')); }
  // 第十批：帳戶委派寫成「委派給 ■帳戶」＋工作說明，其他 MCP 工具寫成「MCP 伺服器 › 工具」（fxrules.delegationOf、mcpToolOf）
  const dlg = delegationOf(item);
  const mcp = dlg ? null : mcpToolOf(item);
  paintToolName(n, item, dlg, mcp);
  // 指令的那一行照語法上色（syntaxrules.commandLang）；字沒變不重設，已經上好的色不會在每次更新時閃掉
  const titleText = (dlg && (dlg.op === 'list' ? '列出可以委派的帳戶' : dlg.prompt)) || item.title || '';
  if (n._title.textContent !== titleText) {
    n._title.textContent = titleText;
    const lang = commandLang(item);
    n._title.classList.toggle('syn', !!lang);
    if (lang && titleText) queueHighlight(n._title, lang);
  }
  const timing = item.status === 'Running' && Number.isFinite(item.time);
  n._elapsed.hidden = !timing;
  if (timing) { n._elapsed.dataset.since = String(item.time); paintElapsed(n._elapsed, Date.now()); }
  else delete n._elapsed.dataset.since;
  n._row.title = (dlg ? n._name.textContent : (item.toolName || '工具')) + (titleText ? '：' + titleText : '') + '（' + (TOOL_STATUS[item.status] || '') + '）';
  n._stats.textContent = '';
  if (item.detailHtml) n._stats.append(h('span', { class: 't-art-badge', text: '文件' }));
  if (item.diffStats && (item.diffStats.add || item.diffStats.del)) {
    n._stats.append(h('span', { class: 'add', text: '+' + (item.diffStats.add || 0) }), h('span', { class: 'del', text: '−' + (item.diffStats.del || 0) }),
      h('span', { class: 't-ratio', 'aria-hidden': 'true' }, diffRatioCells(item.diffStats.add, item.diffStats.del).map((c) => h('span', { class: 'tr-' + c }))));
  }
  // 指令做完後輸出裡的測試與建置總結（fxrules.commandOutcome）：「✓ 7894」「✕ 2」「! 5」小晶片；同一份輸出只算一次，執行中不算
  // myriagent-remote（第九批）：「遠端：電腦名」（執行中也有）與 myriagent-remote 自己的結束碼的意義（fxrules.remoteExecChips）
  for (const c of remoteChips(item)) n._stats.append(h('span', { class: 't-outcome', dataset: { kind: c.kind }, title: c.title, text: c.text }));
  // 帳戶委派：子對話的狀態與「開啟」（對話清單變了時 conversation.refreshListLinks 只重畫這一段）
  if (dlg && dlg.op === 'delegate') {
    if (!n._dlg) n._dlg = h('span', { class: 't-dlg' });
    n._stats.append(n._dlg);
    paintDelegation(n, ctx);
  }
  for (const c of commandChips(n, item)) n._stats.append(h('span', { class: 't-outcome', dataset: { kind: c.kind }, title: c.title, text: c.text }));
  // 搜尋找到幾筆、讀的是哪個網站（fxrules.toolHits，做完才有）
  const hits = toolHits(item);
  if (hits) n._stats.append(h('span', { class: 't-hits', dataset: { kind: hits.kind }, title: hits.title, text: hits.text }));
  if (item.toolKind === 'agent') { appendAgentProgress(n, item, prev); paintAgentUsage(n, ctx.subagentUsage ? ctx.subagentUsage(item.id) : null); }
  const bodyChanged = !prev || prev.detail !== item.detail || prev.output !== item.output || prev.filePath !== item.filePath ||
    prev.detailHtml !== item.detailHtml || !sameJson(prev.diff, item.diff) || prev.status !== item.status;
  if ((bodyChanged || !n._bodyRendered) && !(prev && patchToolOutput(n, item, prev))) renderToolBody(n, item, ctx);
  if (!prev || !sameJson(prev.media, item.media)) {
    if (n._media) { n._media.remove(); n._media = null; }
    if (item.media && item.media.length) { n._media = renderMedia(item.media, ctx); n._media.classList.add('t-media'); n.append(n._media); }
  }
}

/**
 * 展開中的工具卡只有輸出（或狀態）變了：就地更新輸出框，不重建整個內容——
 * 使用者捲到的位置、選取的文字都保留；原本跟著尾端看的，新輸出到了或完成時都維持在尾端。
 */
function patchToolOutput(n, item, prev) {
  const out = n._body && n._body.querySelector('.pre.out');
  if (!out || !prev.output || !item.output || prev.detail !== item.detail || prev.filePath !== item.filePath || prev.detailHtml !== item.detailHtml || !sameJson(prev.diff, item.diff)) return false;
  if (prev.output !== item.output) {
    const atEnd = isAtEnd(out);
    // 串流通常只是接在後面：只補上新的部分，前面的文字節點（與選取）不動
    if (item.output.startsWith(prev.output)) out.append(item.output.slice(prev.output.length));
    else out.textContent = item.output;
    if (atEnd) out.scrollTop = out.scrollHeight;
  }
  if (!out.classList.contains('syn')) {
    const top = out.scrollTop, atEnd = isAtEnd(out);
    markDiagLines(out, item);
    out.scrollTop = atEnd ? out.scrollHeight : top;
  }
  return true;
}

const isAtEnd = (el) => el.scrollTop + el.clientHeight >= el.scrollHeight - 8;

/**
 * 子代理的進度（fxrules.subagentProgress）：「12 步」與錯誤數，執行中另寫它正在做的那一個工具；步數多了時晶片亮一下。
 * 輸出開頭被截掉時步數照輸出算會變少，所以執行中只增不減（記在節點上）
 */
function appendAgentProgress(n, item, prev) {
  const running = item.status === 'Running';
  const p = subagentProgress(item.output, running);
  if (!p) return;
  const steps = running ? Math.max(p.steps, n._agentSteps || 0) : p.steps;
  const grew = !!prev && running && steps > (n._agentSteps || 0) && (n._agentSteps || 0) > 0;
  n._agentSteps = steps;
  const chip = h('span', { class: 't-agent-steps', title: '子代理已執行 ' + steps + (p.more ? ' 項以上' : ' 項') + '步驟', text: steps + (p.more ? '+' : '') + ' 步' });
  n._stats.append(chip);
  if (p.errors) n._stats.append(h('span', { class: 't-outcome', dataset: { kind: 'fail' }, title: '子代理的工具失敗 ' + p.errors + ' 次', text: '✕ ' + p.errors }));
  if (p.current) n._stats.append(h('span', { class: 't-agent-now', title: p.current, text: p.current }));
  if (grew) replayClass(chip, 'fx-step', 500);
}

/**
 * 子代理的 token 用量（Header.subagents，claudeoptrules.agentUsageView）：工具卡標題列的晶片。fillTool 會清掉 _stats，
 * 標頭更新時（conversation.setHeader）也呼叫這裡補畫；沒有用量就拿掉
 */
export function paintAgentUsage(n, usage) {
  if (!n || !n._stats) return;
  const v = agentUsageView(usage);
  if (!v) { if (n._agentUsage) { n._agentUsage.remove(); n._agentUsage = null; } return; }
  if (!n._agentUsage || n._agentUsage.parentNode !== n._stats) {
    n._agentUsage = h('span', { class: 't-agent-tokens' });
    n._stats.append(n._agentUsage);
  }
  if (n._agentUsage.textContent !== v.text) n._agentUsage.textContent = v.text;
  n._agentUsage.title = v.title;
}

/** 做完的指令工具（不是執行中）：要標錯誤行、算結果晶片 */
const finishedCommand = (item) => !!item.output && item.status !== 'Running' && !!commandLang(item);

/** 工具卡的名稱（第十批）：委派與 MCP 換成晶片，其他照宿主的名稱；內容沒變不重建 */
function paintToolName(n, item, dlg, mcp) {
  const acc = dlg && dlg.accountId && state.accountMap ? state.accountMap.get(dlg.accountId) : null;
  const sig = dlg ? 'd|' + dlg.op + '|' + (dlg.accountId || '') + '|' + (acc ? acc.label + '|' + acc.color : '')
    : mcp ? 'm|' + mcp.server : 't|' + (item.toolName || '');
  if (n._nameSig === sig) return;
  n._nameSig = sig;
  n.classList.toggle('t-deleg', !!dlg);
  n.classList.toggle('t-mcp', !!mcp);
  n._name.textContent = '';
  if (dlg && dlg.op === 'delegate') {
    // 卡片的帳戶色換成交辦對象的（角框、點、晶片）；認不得的帳戶（後來刪了）照對話的顏色
    if (acc) setAcc(n, acc.color, acc.provider);
    else { n.style.removeProperty('--acc'); n.style.removeProperty('--acc-ink'); }
    n._name.append('委派給', mentionChip(acc ? acc.label : (dlg.accountId || '其他帳戶'), dlg.accountId || ''));
  } else if (dlg) {
    n._name.textContent = 'AI 帳戶';
  } else if (mcp) {
    n._name.append(h('span', { class: 't-mcp-tag', text: 'MCP' }), h('span', { class: 't-mcp-srv', text: mcp.server }));
  } else {
    n._name.textContent = item.toolName || '工具';
  }
}

/**
 * 委派工具卡的子對話狀態與「開啟」（fxrules.delegateChild、delegationChips）：fillTool 與對話清單變了時呼叫；
 * 內容沒變不動，剛變成等你回應時晶片彈一下
 */
export function paintDelegation(n, ctx) {
  const item = n && n._item;
  const dlg = item && delegationOf(item);
  if (!dlg || dlg.op !== 'delegate' || !n._dlg) return;
  const child = delegateChild(state.conversations, ctx.convId, dlg);
  const chips = delegationChips(dlg, child, item.status);
  const open = child && ctx.openConversation ? child.id : null;
  const sig = JSON.stringify([chips.map((c) => c.kind + ':' + c.text), open]);
  if (sig === n._dlgSig) return;
  const wasWaiting = !!n._dlgSig && n._dlgSig.includes('"wait:');
  n._dlgSig = sig;
  n._dlg.textContent = '';
  for (const c of chips) n._dlg.append(h('span', { class: 't-outcome', dataset: { kind: c.kind }, title: c.title, text: c.text }));
  if (open) {
    const b = h('button', { type: 'button', class: 't-dlg-open', title: '開啟子對話' + (child.title ? '：' + child.title : ''), text: '開啟' });
    b.addEventListener('click', (e) => { e.stopPropagation(); ctx.openConversation(open); });
    b.addEventListener('keydown', (e) => e.stopPropagation()); // Enter／空白鍵不要讓整列展開
    n._dlg.append(b);
  }
  const wait = n._dlg.querySelector('[data-kind="wait"]');
  if (wait && !wasWaiting) replayClass(wait, 'fx-badge-in', 600);
}

function remoteChips(item) {
  if (!commandLang(item)) return [];
  return remoteExecChips(remoteExecOf(item.detail || item.title, item.status === 'Running' ? null : item.output));
}

function commandChips(n, item) {
  if (!finishedCommand(item)) return [];
  if (n._outcomeOf !== item.output) {
    n._outcomeOf = item.output;
    n._outcomeChips = outcomeChips(commandOutcome(item.output));
  }
  return n._outcomeChips;
}

/**
 * 做完的指令的輸出：錯誤與警告那幾行包成 span（fxrules.diagLineRanges，最多 300 行；文字和原文一字不差），左緣標紅、琥珀。
 * 執行中不標（輸出還在接），完成時就地重排一次
 */
function markDiagLines(pre, item) {
  if (!finishedCommand(item) || pre._diagOf === item.output) return;
  pre._diagOf = item.output;
  const ranges = diagLineRanges(item.output);
  if (!ranges.length) return;
  const text = item.output;
  const frag = document.createDocumentFragment();
  let at = 0;
  for (const r of ranges) {
    if (r.start > at) frag.append(text.slice(at, r.start));
    frag.append(h('span', { class: 'ol-' + r.kind, text: text.slice(r.start, r.end) }));
    at = r.end;
  }
  if (at < text.length) frag.append(text.slice(at));
  pre.textContent = '';
  pre.append(frag);
}

/**
 * 工具做完、思考結束時，內容在那一列底下的小框裡快速捲過一遍再收起來（fxrules 的 toolPeekLines／thinkPeekLines／toolPeekTiming，
 * fx.css 的 .t-peek）：工具取差異、輸出或輸入的前幾十行，一行一列不換行；思考取各段的文字，會換行，插進去之後量一次實際排成幾行。
 * 打開與收起只動這個小框的高度、捲動是裡面一格一行的 translateY。展開著的、沒有內容的不播；回傳有沒有播。
 * 呼叫端（conversation.js 的 playPeek）負責只在看得到、貼底、沒有要求減少動態時呼叫
 */
export function playStepPeek(n, item) {
  stopStepPeek(n);
  const think = item.kind === 'Thinking';
  if (think ? n._d.open : n.classList.contains('open')) return false;
  const src = think ? thinkPeekLines(item) : toolPeekLines(item);
  if (!src) return false;
  const run = h('div', { class: 't-peek-run' }, src.lines.map((l) => h('div', { class: l.t ? 'tp-l ' + l.t : 'tp-l', text: l.s || ' ' })));
  const peek = h('div', { class: 't-peek' + (think ? ' wrap' : ''), 'aria-hidden': 'true' }, h('div', { class: 't-peek-box' }, run));
  peek.style.setProperty('--tp-rows', String(PEEK_ROWS));
  (think ? n._d : n._row).after(peek);
  let rows = src.lines.length;
  if (think) {
    // 換行之後實際的行數（同一個工作裡量完、改好，畫面上不會出現中間的樣子）：每行都是 1.5 倍字高（fx.css 的 --tp-lh）
    const lh = 1.5 * parseFloat(getComputedStyle(run).fontSize);
    const cs = getComputedStyle(run);
    const content = run.getBoundingClientRect().height - parseFloat(cs.paddingTop) - parseFloat(cs.paddingBottom);
    if (lh > 0 && content > 0) rows = Math.max(1, Math.round(content / lh));
  }
  const t = toolPeekTiming(rows);
  if (t.steps) peek.classList.add('sweep');
  peek.style.cssText = `--tp-rows:${t.rows};--tp-steps:${t.steps};--tp-open:${t.open}ms;--tp-sweep:${t.sweep}ms;--tp-fold:${t.fold}ms`;
  n.classList.add('peeking');
  n._peek = peek;
  // 收起來：小框收回、箭頭轉回去；收完拿掉
  n._peekTimer = setTimeout(() => {
    n.classList.remove('peeking');
    peek.classList.add('fold');
    n._peekTimer = setTimeout(() => stopStepPeek(n), t.fold);
  }, t.foldAt);
  return true;
}

/** 拿掉工具卡或思考正在掃過的小框；cut 是被下一個接手：快速收起來再拿掉（不是一下子不見） */
export function stopStepPeek(n, cut = false) {
  const peek = n && n._peek;
  if (!peek) return;
  n.classList.remove('peeking');
  if (cut && peek.isConnected) {
    // 已經在收的就讓它收完
    if (!peek.classList.contains('fold')) {
      clearTimeout(n._peekTimer);
      peek.style.setProperty('--tp-fold', PEEK_CUT_MS + 'ms');
      peek.classList.add('fold');
      n._peekTimer = setTimeout(() => stopStepPeek(n), PEEK_CUT_MS);
    }
    return;
  }
  clearTimeout(n._peekTimer);
  peek.remove();
  n._peek = null;
  n._peekTimer = 0;
}

/**
 * 工具卡、核准卡展開後的「輸入」：指令照 shell／PowerShell 上色（宿主寫在前面的說明另成一段、不上色，整份文字和原文一字不差）；
 * JSON 參數以 json 上色；其他照原文（syntaxrules.commandLang、commandParts、detailLang）
 */
function detailPre(item) {
  const lang = commandLang(item);
  const parts = lang ? commandParts(item.detail, item.title) : null;
  if (parts) {
    const pre = h('pre', { class: 'pre syn' });
    if (parts.note) pre.append(h('span', { class: 't-cmd-note', text: parts.note + '\n\n' }));
    const code = h('span', { class: 't-cmd', text: parts.command });
    pre.append(code);
    queueHighlight(code, lang);
    return pre;
  }
  const pre = h('pre', { class: 'pre', text: item.detail });
  const dl = detailLang(item.detail);
  if (dl) { pre.classList.add('syn'); queueHighlight(pre, dl); }
  return pre;
}

function renderToolBody(n, item, ctx) {
  const open = ctx.expanded.has(item.id);
  n.classList.toggle('open', open);
  n._row.setAttribute('aria-expanded', String(open));
  const oldOut = n._body && n._body.querySelector('.pre.out');
  const wasAtEnd = oldOut ? isAtEnd(oldOut) : item.status === 'Running';
  const oldTop = oldOut ? oldOut.scrollTop : 0;
  if (n._body) { n._body.remove(); n._body = null; }
  n._bodyRendered = true;
  if (!open) return;
  stopStepPeek(n);
  const b = h('div', { class: 't-body' });
  if (item.filePath) b.append(fileLink(item.filePath, ctx));
  if (item.detail) b.append(section('輸入', detailPre(item)));
  if (item.detailHtml) {
    const previewMd = h('div', { class: 'md t-art-md' });
    setHtml(previewMd, item.detailHtml, ctx);
    if (item.diff && item.diff.length) {
      const diffEl = renderDiff(item.diff, item.filePath || item.title);
      let mode = n._artTab === 'diff' ? 'diff' : 'preview';
      const pvBtn = h('button', { type: 'button', class: 't-art-tab', text: '預覽' });
      const dfBtn = h('button', { type: 'button', class: 't-art-tab', text: '差異' });
      const syncArtTab = () => {
        n._artTab = mode;
        pvBtn.classList.toggle('on', mode === 'preview');
        dfBtn.classList.toggle('on', mode === 'diff');
        previewMd.hidden = mode !== 'preview';
        diffEl.hidden = mode !== 'diff';
      };
      pvBtn.addEventListener('click', () => { mode = 'preview'; syncArtTab(); });
      dfBtn.addEventListener('click', () => { mode = 'diff'; syncArtTab(); });
      syncArtTab();
      b.append(h('div', { class: 't-sec' },
        h('div', { class: 't-sec-head' }, h('div', { class: 't-sec-label', text: '文件產物' }), h('div', { class: 't-art-tabs' }, pvBtn, dfBtn)),
        previewMd, diffEl));
    } else {
      b.append(section('預覽', previewMd));
    }
  } else if (item.diff && item.diff.length) {
    b.append(section('差異', renderDiff(item.diff, item.filePath || item.title)));
  }
  let out = null;
  if (item.output) {
    out = h('pre', { class: 'pre out', text: item.output });
    // 讀檔的輸出是檔案內容：依副檔名上色（syntaxrules.outputLang；指令的輸出照原文）
    const ol = outputLang(item);
    if (ol) { out.classList.add('syn'); queueHighlight(out, ol); }
    else markDiagLines(out, item);
    b.append(section('輸出', out));
  }
  if (!item.filePath && !item.detail && !item.detailHtml && !(item.diff && item.diff.length) && !item.output) {
    b.append(h('div', { class: 't-empty', text: item.status === 'Running' ? '執行中…' : '沒有輸出' }));
  }
  n._body = b;
  if (n._media) n.insertBefore(b, n._media); else n.append(b);
  // 重建後回到原本的位置：在尾端就留在尾端（含剛完成），否則還原捲到的位置；第一次展開執行中的工具從尾端開始看
  if (out) out.scrollTop = wasAtEnd ? out.scrollHeight : oldTop;
}

function section(label, content) {
  return h('div', { class: 't-sec' }, h('div', { class: 't-sec-label', text: label }), content);
}

/**
 * openPath／revealPath 的訊息：對話裡的（工具、核准、附件、媒體）帶 conversationId（WP4.6 步 10），
 * 宿主才認得出是其他電腦的副本、不開這台的同名路徑（副本只收看的指令）
 */
function pathCmd(cmd, path, ctx) {
  return ctx && ctx.convId ? { cmd, path, conversationId: ctx.convId } : { cmd, path };
}

export function fileLink(path, ctx) {
  const name = baseName(path);
  const dir = path.length > name.length ? path.slice(0, path.length - name.length).replace(/[\\/]+$/, '') : '';
  // 〔遠端〕瀏覽器版：檔案在對話所在的電腦上，點了送 fileFetch 預覽或下載（D34）；沒有「在檔案總管顯示」
  const remote = isHub && !!ctx && typeof ctx.fetchFile === 'function';
  const open = h('button', { type: 'button', class: 'flink', title: (remote ? '預覽或下載：' : '用預設程式開啟：') + path, dataset: remote ? { field: 'fileFetch' } : null }, icon('file'),
    h('span', { class: 'flink-name', text: name }), dir ? h('span', { class: 'flink-dir', text: dir }) : null);
  open.addEventListener('click', () => {
    if (!remote) post(pathCmd('openPath', path, ctx));
    else if (!ctx.fetchFile(path, name)) toast('現在不能讀取這個檔案', 'error');
  });
  const reveal = remote ? null : iconButton('folderOpen', '在檔案總管顯示', () => post(pathCmd('revealPath', path, ctx)), 'sm');
  return h('div', { class: 'frow' }, open, reveal);
}

export function renderDiff(lines, name) {
  const box = h('div', { class: 'diff' });
  const frag = document.createDocumentFragment();
  const n = Math.min(lines.length, DIFF_LIMIT);
  for (let i = 0; i < n; i++) {
    const ln = lines[i] || {};
    const t = ln.t === 'add' || ln.t === 'del' || ln.t === 'hunk' ? ln.t : 'ctx';
    const sign = t === 'add' ? '+' : t === 'del' ? '−' : '';
    const row = document.createElement('div');
    row.className = 'dl ' + t;
    const s = document.createElement('span');
    s.className = 'dl-s';
    s.textContent = sign;
    const tx = document.createElement('span');
    tx.className = 'dl-t';
    tx.textContent = ln.s == null ? '' : String(ln.s);
    row.append(s, tx);
    frag.append(row);
  }
  box.append(frag);
  if (lines.length > n) box.append(h('div', { class: 'dl more', text: '還有 ' + (lines.length - n) + ' 行未顯示' }));
  // 靜置後依檔名的語言上色、標出相鄰的刪除與新增行實際改了哪些字（syntax.js 的 queueDiff、diffrules.js）
  queueDiff(box, n < lines.length ? lines.slice(0, n) : lines, extOf(name || ''));
  return withDiffMap(box, lines, n);
}

/**
 * 比較長的差異：右側加一條變動地圖（fxrules.diffMapRuns；左半刪除紅、右半新增綠），畫出時算一次、做成靜態背景，
 * 捲動時不跑任何程式；點了把差異框捲到那裡。短的照舊只有差異框
 */
function withDiffMap(box, lines, n) {
  const runs = diffMapRuns(lines, n);
  if (!runs) return box;
  const map = h('div', { class: 'diff-map', title: '變動地圖：紅色是刪除、綠色是新增，點了跳過去', 'aria-hidden': 'true' });
  map.style.backgroundImage = `${diffMapGradient(runs.del, 'var(--red)')}, ${diffMapGradient(runs.add, 'var(--green)')}`;
  map.addEventListener('click', (e) => {
    const r = map.getBoundingClientRect();
    if (!r.height) return;
    const frac = Math.min(1, Math.max(0, (e.clientY - r.top) / r.height));
    box.scrollTop = Math.max(0, frac * box.scrollHeight - box.clientHeight / 2);
  });
  return h('div', { class: 'diff-wrap' }, box, map);
}

// ───── 選項清單（問卷、核准、計畫確認共用）：數字鍵 1～9 ─────
const KEY_PRESS_MS = 260; // 按了數字鍵時那顆鍵帽下沉、亮一下（fx.css 的 .opt-key.fx-press）

/** 按了數字鍵選選項：那顆數字鍵帽像被按下去一樣下沉、亮一下（只有鍵盤；滑鼠點選項照舊） */
function pressKey(scope, key) {
  const cap = scope && scope.querySelector('.opt[data-idx="' + key + '"] > .opt-key');
  if (cap) replayClass(cap, 'fx-press', KEY_PRESS_MS);
}

function optionButton(index, label, desc, cls) {
  return h('button', { type: 'button', class: 'opt' + (cls ? ' ' + cls : ''), dataset: { idx: String(index) } },
    h('span', { class: 'opt-key', text: String(index) }),
    h('span', { class: 'opt-mark' }),
    h('span', { class: 'opt-text' }, h('span', { class: 'opt-label', text: label }), desc ? h('span', { class: 'opt-desc', text: desc }) : null));
}

function cardHead(iconName, title, hint) {
  return h('div', { class: 'card-head' }, icon(iconName, 'card-ic'), h('span', { class: 'card-title', text: title }), hint ? h('span', { class: 'card-hint', text: hint }) : null);
}

function resultTag(text, tone) {
  return h('div', { class: 'result ' + (tone || 'gray') }, icon(tone === 'ok' ? 'check' : tone === 'no' ? 'ban' : 'info'), h('span', { text }));
}

/**
 * 已決定的卡片附的說明（宿主放在 output）：拒絕或「繼續規劃」時附給代理的說明（「你的說明：…」）；
 * 〔熱更新〕卡片因 CLI 重新啟動而失效時的說明（「CLI 已重新啟動，這張卡已失效；代理會視需要重新提出」）
 */
function appendFeedback(n, item) {
  if (item.output) n.append(h('div', { class: 'card-note', text: item.output }));
}

/** 已失效的卡片：宿主有說明（output）時原因寫在說明裡，不能說成「這一輪已結束」（CLI 重開後這一輪還在繼續） */
function cancelledTag(item) {
  return resultTag(item.output ? '已失效' : '已失效（這一輪已結束）', 'gray');
}

// ───── Question ─────
function qState(ctx, item) {
  let s = ctx.qstate.get(item.id);
  if (!s) {
    s = { sel: {}, other: {}, note: {}, active: 0 };
    for (const q of item.questions || []) { s.sel[q.id] = []; s.other[q.id] = ''; s.note[q.id] = ''; }
    ctx.qstate.set(item.id, s);
  }
  return s;
}

function createQuestion(item, ctx) {
  const waiting = item.status === 'Waiting';
  const n = shell(item, 'it-question it-card' + (waiting ? ' waiting' : ''));
  const qs = item.questions || [];
  // toolName：不是代理自己問的（MCP 伺服器的 elicitation，「MCP：伺服器名稱」）；代理問的省略
  const asker = typeof item.toolName === 'string' && item.toolName ? item.toolName : null;
  n.append(cardHead(asker ? 'plug' : 'question', asker ? (waiting ? asker + ' 想問你' : asker + ' 的提問') : (waiting ? '代理想問你' : '代理的提問'),
    waiting ? (item.answerAsMessage ? '回答會當作下一則訊息送出' : (qs.length > 1 ? '共 ' + qs.length + ' 題' : null)) : null));
  if (item.filePath) n.append(fileLink(item.filePath, ctx));
  if (item.detailHtml) {
    const planKey = item.id + ':plan';
    const planDet = h('details', { class: 'q-plan' },
      h('summary', { class: 'q-plan-sum' }, icon('chevRight', 'sum-chev'), h('span', { text: asker ? '說明' : '計畫內容' })));
    const planMd = h('div', { class: 'md pa-plan' });
    setHtml(planMd, item.detailHtml, ctx);
    planDet.append(planMd);
    planDet.open = ctx.expanded.has(planKey) || waiting;
    planDet.addEventListener('toggle', () => { if (planDet.open) ctx.expanded.add(planKey); else ctx.expanded.delete(planKey); });
    n.append(planDet);
  }
  if (!waiting) { n.append(answeredView(item)); if (item.status === 'Cancelled') appendFeedback(n, item); return n; }

  const st = qState(ctx, item);
  n.tabIndex = 0;
  const blocks = [];
  // 作答進度（第十一批）：多題時標頭下方一題一格——答完的亮、有補充說明的多一個小點、目前那題有外框；點了跳到那一題（questionrules.progressCells）
  const prog = qs.length > 1 ? h('div', { class: 'q-progress', role: 'group', 'aria-label': '作答進度' }) : null;
  if (prog) {
    qs.forEach((q, qi) => {
      const cell = h('button', { type: 'button', class: 'q-cell', tabIndex: -1 });
      cell.addEventListener('click', () => { setActive(qi); blocks[qi].scrollIntoView({ block: 'nearest' }); });
      prog.append(cell);
    });
    n.append(prog);
  }
  qs.forEach((q, qi) => {
    const block = h('div', { class: 'q-block', dataset: { qi: String(qi) } });
    const top = h('div', { class: 'q-top' });
    if (q.header) top.append(h('span', { class: 'q-chip', text: q.header }));
    top.append(h('span', { class: 'q-text', text: q.text || '' }));
    if (q.multi) top.append(h('span', { class: 'q-multi', text: '可複選' }));
    block.append(top);
    const opts = h('div', { class: 'opts' + (q.multi ? ' multi' : '') });
    (q.options || []).forEach((o, oi) => {
      const b = optionButton(oi + 1, o.label, o.description, null);
      if (o.preview) b.querySelector('.opt-text').append(h('pre', { class: 'opt-preview', text: o.preview }));
      b.addEventListener('click', () => { pick(qi, oi); });
      opts.append(b);
    });
    block.append(opts);
    const hasOptions = !!(q.options && q.options.length);
    if (q.allowOther || !hasOptions) {
      const other = h('input', {
        type: q.secret ? 'password' : 'text', class: 'q-other input',
        placeholder: hasOptions ? '自己輸入答案' : '輸入你的回答', autocomplete: 'off', spellcheck: 'false',
        'aria-label': hasOptions ? '其他（自己輸入答案）' : '你的回答',
      });
      other.value = st.other[q.id] || '';
      other.addEventListener('input', () => {
        st.other[q.id] = other.value;
        if (!q.multi && other.value.trim() && st.sel[q.id].length) st.sel[q.id] = [];
        syncBlock(qi);
      });
      other.addEventListener('keydown', (e) => {
        if (e.key === 'Enter' && !e.isComposing && e.keyCode !== 229) { e.preventDefault(); submit(); }
      });
      other.addEventListener('focus', () => setActive(qi));
      if (hasOptions) {
        // 「其他」是答案之一（2026-11-07 使用者回報：和補充說明兩個一樣的輸入框會搞混）：排在選項清單的最後一列，有自己的數字鍵與單選／複選記號，
        // 輸入框在那一列裡；單選題打了字就取消已選的選項（同以前）
        const row = h('div', { class: 'opt opt-other', dataset: { idx: String(q.options.length + 1) } },
          h('span', { class: 'opt-key', text: String(q.options.length + 1) }),
          h('span', { class: 'opt-mark' }),
          h('span', { class: 'opt-text' }, h('span', { class: 'opt-label', text: '其他' }), q.secret ? icon('lock', 'q-lock') : null, other));
        row.addEventListener('click', (e) => { if (e.target !== other) other.focus(); });
        opts.append(row);
      } else {
        block.append(h('div', { class: 'q-other-row' }, q.secret ? icon('lock', 'q-lock') : null, other));
      }
    }
    // 補充說明（2026-11-05）：選了答案之後另外補一段，和「其他」互不清除；Claude、Grok 以 annotations.notes 交給 CLI。
    // 和答案分開：選項下面隔一條線、有自己的標題與說明（2026-11-07），看得出是附在答案上的，不是另一個答案
    if (noteFieldShown(q, asker)) {
      const note = h('input', {
        type: 'text', class: 'q-note input', placeholder: '例如：選這個的原因、要注意的地方', maxLength: NOTE_MAX, autocomplete: 'off', spellcheck: 'false',
        dataset: { field: 'questionNote' }, 'aria-label': '補充說明（選填）',
      });
      note.value = st.note[q.id] || '';
      note.addEventListener('input', () => { st.note[q.id] = note.value; renderProgress(); });
      note.addEventListener('keydown', (e) => {
        if (e.key === 'Enter' && !e.isComposing && e.keyCode !== 229) { e.preventDefault(); submit(); }
      });
      note.addEventListener('focus', () => setActive(qi));
      block.append(h('div', { class: 'q-note-box' },
        h('div', { class: 'q-note-head' }, icon('edit', 'q-note-ic'), h('span', { class: 'q-note-title', text: '補充說明（選填）' }),
          h('span', { class: 'q-note-hint', text: '會和上面的答案一起送出' })),
        note));
    }
    block.addEventListener('pointerdown', () => setActive(qi));
    blocks.push(block);
    n.append(block);
  });

  const submitBtn = h('button', { type: 'button', class: 'btn acc', text: '送出' });
  const skipBtn = h('button', { type: 'button', class: 'btn ghost', text: '略過' });
  submitBtn.addEventListener('click', submit);
  // 〔遠端〕略過不帶 requestDigest（D18），照樣追蹤結果
  skipBtn.addEventListener('click', () => sendCard(n, item, ctx, 'skipQuestion', { itemId: item.id }));
  n.append(h('div', { class: 'card-actions' }, submitBtn, skipBtn, h('span', { class: 'card-keys', text: '數字鍵選擇　Enter 送出' })));
  qs.forEach((q, qi) => syncBlock(qi)); // 重畫時照記著的作答標出選項與進度

  function setActive(qi) {
    st.active = qi;
    blocks.forEach((b, i) => b.classList.toggle('active', i === qi && blocks.length > 1));
    renderProgress();
  }
  function renderProgress() {
    if (!prog) return;
    const cells = progressCells(qs, st);
    Array.from(prog.children).forEach((c, i) => {
      const x = cells[i];
      c.classList.toggle('done', x.answered);
      c.classList.toggle('noted', x.note);
      c.classList.toggle('cur', i === (st.active || 0));
      const title = '第 ' + (i + 1) + ' 題：' + (x.answered ? '已回答' : '還沒回答') + (x.note ? '（有補充說明）' : '');
      if (c.title !== title) { c.title = title; c.setAttribute('aria-label', title); }
    });
  }
  function syncBlock(qi) {
    const q = qs[qi];
    const sel = st.sel[q.id] || [];
    blocks[qi].querySelectorAll('.opt:not(.opt-other)').forEach((b, oi) => {
      const on = sel.includes((q.options[oi] || {}).label);
      b.classList.toggle('sel', on);
      b.setAttribute('aria-pressed', String(on));
    });
    // 「其他」那一列：有打字就算選了它
    const otherRow = blocks[qi].querySelector('.opt-other');
    if (otherRow) otherRow.classList.toggle('sel', !!(st.other[q.id] || '').trim());
    renderProgress();
  }
  function pick(qi, oi) {
    const q = qs[qi];
    const o = (q.options || [])[oi];
    if (!o) return;
    setActive(qi);
    const sel = st.sel[q.id] || (st.sel[q.id] = []);
    const at = sel.indexOf(o.label);
    if (q.multi) { if (at >= 0) sel.splice(at, 1); else sel.push(o.label); }
    else {
      st.sel[q.id] = at >= 0 ? [] : [o.label];
      if (at < 0) {
        st.other[q.id] = '';
        const other = blocks[qi].querySelector('.q-other');
        if (other) other.value = '';
      }
    }
    syncBlock(qi);
    // 單選答完自動跳下一題
    if (!q.multi && at < 0 && qi < qs.length - 1) setActive(qi + 1);
  }
  function submit() {
    if (n._blocked) return; // 〔遠端〕閘門擋著（電腦離線、政策、沒連線）：按鍵也不送
    const answers = {};
    let missing = -1, count = 0;
    qs.forEach((q, qi) => {
      const vals = (st.sel[q.id] || []).slice();
      const other = (st.other[q.id] || '').trim();
      if (other) vals.push(other);
      if (vals.length) { answers[q.id] = vals; count++; } else if (missing < 0) missing = qi;
    });
    if (missing >= 0) {
      toast(count === 0 ? '請先選擇或輸入答案' : '還有 ' + (qs.length - count) + ' 題沒有回答');
      setActive(missing);
      blocks[missing].scrollIntoView({ block: 'nearest' });
      return;
    }
    // 補充說明只在有填時才帶（舊版的電腦不認得 notes，帶了會拒絕整個回答）
    const notes = buildNotes(qs, st.note, asker);
    sendCard(n, item, ctx, 'answer', withDigest(notes ? { itemId: item.id, answers, notes } : { itemId: item.id, answers }, item));
  }

  n.addEventListener('keydown', (e) => {
    if (e.target.tagName === 'INPUT') return;
    if (/^[1-9]$/.test(e.key) && !e.ctrlKey && !e.altKey && !e.metaKey) {
      e.preventDefault();
      if (!n._blocked && !n.classList.contains('sending')) {
        const qi = st.active || 0;
        const otherInput = blocks[qi].querySelector('.opt-other .q-other');
        pressKey(blocks[qi], e.key);
        // 「其他」那一列的數字鍵：游標移到它的輸入框
        if (otherInput && Number(e.key) === (qs[qi].options || []).length + 1) otherInput.focus();
        else pick(qi, Number(e.key) - 1);
      }
    } else if (e.key === 'Enter' && e.target === n) { e.preventDefault(); if (!n.classList.contains('sending')) submit(); }
  });
  qs.forEach((_, qi) => syncBlock(qi));
  setActive(Math.min(st.active || 0, Math.max(0, qs.length - 1)));
  finishCard(n, item, ctx);
  n._autofocus = true;
  return n;
}

function answeredView(item) {
  if (item.status === 'Cancelled') return cancelledTag(item);
  if (item.decision === 'skip' || item.status === 'Declined') return resultTag('已略過', 'gray');
  const box = h('div', { class: 'answered' });
  for (const q of item.questions || []) {
    // 補充說明（宿主併成「補充說明：…」那一筆）另起一行，不畫成答案的晶片
    const { answers: a, notes } = splitAnswer((item.answers && item.answers[q.id]) || []);
    const row = h('div', { class: 'ans-row' }, h('span', { class: 'ans-q', text: q.text || q.header || '' }));
    const vals = h('span', { class: 'ans-v' });
    if (!a.length) vals.append(h('span', { class: 'ans-none', text: '未回答' }));
    for (const v of a) vals.append(h('span', { class: 'ans-chip', text: q.secret ? '••••••' : v }));
    row.append(vals);
    box.append(row);
    for (const note of q.secret ? [] : notes) box.append(h('div', { class: 'ans-note', dataset: { field: 'answerNote' } }, icon('edit', 'ans-note-ic'), h('span', { text: note })));
  }
  appendResolvedBy(box, item);
  return box;
}

/** 多人協作：卡片是哪位協作人回應的（擁有者回應的不寫） */
function appendResolvedBy(n, item) {
  const label = resolvedLabel(item, state.viewer);
  const tint = label ? collabColor(item.resolvedBy) : null;
  if (label) n.append(h('div', { class: 'resolved-by', style: tint ? { '--collab': tint } : null }, icon('user'), h('span', { text: label })));
}

// ───── Approval ─────
function createApproval(item, ctx) {
  const waiting = item.status === 'Waiting';
  const n = shell(item, 'it-approval it-card' + (waiting ? ' waiting' : ''));
  const tool = item.toolName || '執行操作';
  n.append(cardHead('shield', waiting ? '要允許「' + tool + '」嗎？' : tool, waiting ? '核准後才會執行' : null));
  if (item.title) {
    // 要核准的指令照語法上色（同工具卡）
    const cmd = h('div', { class: 'ap-cmd', text: item.title });
    const lang = commandLang(item);
    if (lang) { cmd.classList.add('syn'); queueHighlight(cmd, lang); }
    n.append(cmd);
  }
  if (item.filePath && item.filePath !== item.title) n.append(fileLink(item.filePath, ctx));
  if (item.diff && item.diff.length) n.append(renderDiff(item.diff, item.filePath || item.title));
  if (item.detail) {
    const det = h('details', { class: 'ap-detail' }, h('summary', {}, icon('chevRight', 'sum-chev'), h('span', { text: '詳細內容' })), detailPre(item));
    det.open = ctx.expanded.has(item.id);
    det.addEventListener('toggle', () => { if (det.open) ctx.expanded.add(item.id); else ctx.expanded.delete(item.id); });
    n.append(det);
  }
  if (!waiting) {
    cardState(ctx).texts.delete(item.id);
    if (item.status === 'Cancelled') n.append(cancelledTag(item));
    else if (item.decision === 'deny' || item.status === 'Declined') n.append(resultTag('已拒絕', 'no'));
    else if (item.decision === 'allowSession') n.append(resultTag(item.allowSessionLabel ? '已允許：' + item.allowSessionLabel : '已允許，這個對話之後同類操作不再詢問', 'ok'));
    else n.append(resultTag('已允許', 'ok'));
    appendFeedback(n, item);
    appendResolvedBy(n, item);
    return n;
  }
  const choices = [{ v: 'allow', label: '是' }];
  // allowSessionLabel（選用）：宿主依操作種類給的說法，例如「本對話同樣的指令都允許」
  if (item.canAllowForSession) {
    choices.push(item.allowSessionLabel
      ? { v: 'allowSession', label: item.allowSessionLabel }
      : { v: 'allowSession', label: '是，本對話都允許', desc: '這個對話之後的同類操作不再詢問' });
  }
  choices.push({ v: 'deny', label: '否', desc: '可以在下方告訴代理改怎麼做' });
  const opts = h('div', { class: 'opts' });
  const tell = h('input', { type: 'text', class: 'input ap-tell', placeholder: '告訴代理改怎麼做（會隨「否」一起送出）', autocomplete: 'off', spellcheck: 'false' });
  keepCardText(tell, item, ctx);
  const decide = (v) => {
    if (n._blocked || n.classList.contains('sending')) return;
    const msg = tell.value.trim();
    const extra = { itemId: item.id, choice: v };
    if (v === 'deny' && msg) extra.message = msg;
    sendCard(n, item, ctx, 'approve', withDigest(extra, item));
  };
  choices.forEach((c, i) => {
    const b = optionButton(i + 1, c.label, c.desc, c.v === 'deny' ? 'deny' : null);
    b.addEventListener('click', () => decide(c.v));
    opts.append(b);
  });
  tell.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && !e.isComposing && e.keyCode !== 229) { e.preventDefault(); decide('deny'); }
  });
  n.append(opts, h('div', { class: 'tell-row' }, tell));
  n.tabIndex = 0;
  n.addEventListener('keydown', (e) => {
    if (e.target.tagName === 'INPUT') return;
    if (/^[1-9]$/.test(e.key) && !e.ctrlKey && !e.altKey) {
      const c = choices[Number(e.key) - 1];
      if (c) { e.preventDefault(); pressKey(n, e.key); decide(c.v); }
    }
  });
  finishCard(n, item, ctx);
  n._autofocus = true;
  return n;
}

// ───── PlanApproval（Claude 規劃模式結束時的計畫確認） ─────
function createPlanApproval(item, ctx) {
  const waiting = item.status === 'Waiting';
  const n = shell(item, 'it-planapproval it-card' + (waiting ? ' waiting' : ''));
  n.append(cardHead('map', waiting ? '計畫擬好了，要開始執行嗎？' : '計畫', null));
  if (item.detailHtml) {
    const md = h('div', { class: 'md pa-plan' });
    setHtml(md, item.detailHtml, ctx);
    n.append(md);
  }
  if (!waiting) {
    const tag = {
      acceptEdits: ['已核准，自動接受編輯', 'ok'],
      manual: ['已核准，每次編輯前詢問', 'ok'],
      keepPlanning: ['繼續規劃', 'gray'],
    }[item.decision] || null;
    cardState(ctx).texts.delete(item.id);
    if (tag) n.append(resultTag(tag[0], tag[1]));
    else n.append(item.status === 'Cancelled' ? cancelledTag(item) : resultTag('已處理', 'gray'));
    appendFeedback(n, item);
    appendResolvedBy(n, item);
    return n;
  }
  const choices = [
    { v: 'acceptEdits', label: '是，並自動接受編輯', desc: '開始執行，修改檔案不再逐一詢問' },
    { v: 'manual', label: '是，手動核准每次編輯', desc: '開始執行，每次修改前先問你' },
    { v: 'keepPlanning', label: '否，繼續規劃', desc: '留在規劃模式，可在下方說明要調整的地方' },
  ];
  const opts = h('div', { class: 'opts' });
  const note = h('input', { type: 'text', class: 'input ap-tell', placeholder: '要調整的地方（選「否，繼續規劃」時一併送出）', autocomplete: 'off', spellcheck: 'false' });
  keepCardText(note, item, ctx);
  const decide = (v) => {
    if (n._blocked || n.classList.contains('sending')) return;
    const extra = { itemId: item.id, choice: v };
    const msg = note.value.trim();
    if (msg && v === 'keepPlanning') extra.message = msg;
    sendCard(n, item, ctx, 'approvePlan', withDigest(extra, item));
  };
  choices.forEach((c, i) => {
    const b = optionButton(i + 1, c.label, c.desc, c.v === 'keepPlanning' ? 'deny' : null);
    b.addEventListener('click', () => decide(c.v));
    opts.append(b);
  });
  note.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && !e.isComposing && e.keyCode !== 229) { e.preventDefault(); decide('keepPlanning'); }
  });
  n.append(opts, h('div', { class: 'tell-row' }, note));
  n.tabIndex = 0;
  n.addEventListener('keydown', (e) => {
    if (e.target.tagName === 'INPUT') return;
    const c = /^[1-3]$/.test(e.key) ? choices[Number(e.key) - 1] : null;
    if (c && !e.ctrlKey && !e.altKey) { e.preventDefault(); pressKey(n, e.key); decide(c.v); }
  });
  finishCard(n, item, ctx);
  n._autofocus = true;
  return n;
}

// ───── 〔遠端〕卡片的回應：requestDigest、追蹤結果、閘門（WP4.6 D5、D18）─────

/** 瀏覽器版的卡片帶 requestDigest（字串才帶，原樣送回）；桌面的項目沒有這個欄位，不變 */
function withDigest(extra, item) {
  if (typeof item.requestDigest === 'string') extra.requestDigest = item.requestDigest;
  return extra;
}

function setCardDisabled(n, on) {
  n.querySelectorAll('button, input').forEach((b) => { b.disabled = on; });
}

/** 這個對話的卡片狀態（ConvView 的 ctx.cards）：送出中（等結果）的項目 id、結果不明的說明、卡片輸入欄打到一半的字 */
function cardState(ctx) {
  if (!ctx.cards) ctx.cards = { pending: new Set(), notes: new Map(), texts: new Map() };
  if (!ctx.cards.texts) ctx.cards.texts = new Map();
  return ctx.cards;
}

/**
 * 核准卡「告訴代理改怎麼做」、計畫卡「要調整的地方」：字記在 ctx.cards.texts（合併前審查 LIVE-5）。
 * 重連、resync、伺服器整份重送時卡片會重建，建好時還原；卡片不再等你回應時清掉
 */
function keepCardText(input, item, ctx) {
  const cs = cardState(ctx);
  input.value = cs.texts.get(item.id) || '';
  input.addEventListener('input', () => { if (input.value) cs.texts.set(item.id, input.value); else cs.texts.delete(item.id); });
}

function settleNote(text) {
  return h('div', { class: 'card-settle', dataset: { field: 'cardSettle' } }, icon('info'), h('span', { text }));
}

/**
 * 送出卡片的回應：鎖住卡片再送。瀏覽器版以回傳的 commandId 追蹤：rejected（伺服器已提示）解鎖、unknown 解鎖並說明「結果不明，
 * 請看對話內容確認」（不自動重送）、done 等項目更新換掉卡片；沒送出（null：沒有連線、太大，傳輸層已提示）馬上解鎖。
 * 桌面（回 undefined）照舊鎖住，等宿主更新項目
 */
function sendCard(n, item, ctx, cmd, extra) {
  const cs = cardState(ctx);
  n.classList.add('sending');
  setCardDisabled(n, true);
  cs.notes.delete(item.id);
  for (const x of n.querySelectorAll(':scope > .card-settle')) x.remove();
  const id = ctx.post(cmd, extra);
  if (id === null) { unlockCard(n, ctx, null); return; }
  if (typeof id !== 'string' || !ctx.track) return;
  cs.pending.add(item.id);
  ctx.track(id, {
    onSettle: (state, code, origin, elapsed) => {
      cs.pending.delete(item.id);
      if (state === 'done') { if (ctx.rtt) ctx.rtt(elapsed); return; }
      const note = settleText(state);
      if (note) cs.notes.set(item.id, note);
      // 等結果時項目可能重畫過（重新 open）：解鎖目前畫面上的那張
      const cur = (ctx.nodeOf && ctx.nodeOf(item.id)) || n;
      unlockCard(cur, ctx, note);
    },
  });
}

function unlockCard(n, ctx, note) {
  if (!n.classList.contains('waiting')) return;
  n.classList.remove('sending');
  if (note && !n.querySelector(':scope > .card-settle')) n.append(settleNote(note));
  applyCardGate(n, ctx.cardGate ? ctx.cardGate() : null);
}

/** 等你回應的卡片建好時（含重新載入）：送出中的照舊鎖住、結果不明的加說明，再套閘門 */
function finishCard(n, item, ctx) {
  const cs = cardState(ctx);
  if (cs.pending.has(item.id)) { n.classList.add('sending'); setCardDisabled(n, true); }
  const note = cs.notes.get(item.id);
  if (note) n.append(settleNote(note));
  applyCardGate(n, ctx.cardGate ? ctx.cardGate() : null);
}

/**
 * 〔遠端〕卡片的閘門（liverules.cardGate，D18）：不允許（副本、沒連線、電腦離線、政策）時停用按鈕與輸入欄，卡片底下一行說明（textContent）；
 * 送出中的卡片不解鎖（結果到了才解）。只套在等你回應的卡片
 */
export function applyCardGate(n, gate) {
  if (!n || !n.classList.contains('waiting')) return;
  const blocked = !!(gate && gate.enabled === false);
  n._blocked = blocked;
  let line = n.querySelector(':scope > .card-gate');
  if (blocked) {
    if (!line) { line = h('div', { class: 'card-gate', dataset: { field: 'cardGate' } }, icon('info'), h('span')); n.append(line); }
    const text = gate.reason || '';
    if (line.lastChild.textContent !== text) line.lastChild.textContent = text;
  } else if (line) line.remove();
  if (!n.classList.contains('sending')) setCardDisabled(n, blocked);
}

// ───── Plan（待辦清單） ─────
function createPlan(item, ctx) {
  const n = shell(item, 'it-plan');
  n._head = h('div', { class: 'plan-head' });
  n._list = h('ol', { class: 'plan-list' });
  n.append(n._head, n._list);
  fillPlan(n, item, null, ctx);
  return n;
}

function fillPlan(n, item, prev, ctx) {
  if (prev && sameJson(prev.plan, item.plan) && prev.html === item.html) return;
  const steps = item.plan || [];
  const done = steps.filter((s) => s.status === 'completed').length;
  // 完成數與總數用側欄同款的計數器（tokenfx.js），掛在節點上沿用：標頭重建之後數字變了才解碼（第一次直接顯示）
  if (!n._done) { n._done = tokenCounter(String, ctx && ctx.shown); n._total = tokenCounter(String, ctx && ctx.shown); }
  n._head.textContent = '';
  // 分格進度（fxrules.planCells）：每一步一格，完成綠、進行中帳戶色、還沒做空框
  const bar = h('span', { class: 'plan-bar', 'aria-hidden': 'true' }, planCells(steps).map((c) => h('span', { class: 'pb-' + c })));
  n._head.append(icon('checklist', 'plan-ic'), h('span', { class: 'plan-title', text: '待辦' }), bar,
    h('span', { class: 'plan-count' }, n._done.num, '／', n._total.num, ' 完成'));
  n._done.set(done);
  n._total.set(steps.length);
  if (n._intro) { n._intro.remove(); n._intro = null; }
  if (item.html) { n._intro = h('div', { class: 'md plan-intro' }); setHtml(n._intro, item.html, ctx); n._head.after(n._intro); }
  n._list.textContent = '';
  const rows = steps.map(planRow);
  n._list.append(...rows);
  // 這次更新才完成的那幾步閃一下綠（重新載入、第一次畫不閃；動畫在 fx.css 第八批）
  for (const i of planNewlyDone(prev && prev.plan, steps)) replayClass(rows[i], 'fx-ps-done', 900);
}

export function planRow(s) {
  const st = s.status === 'completed' || s.status === 'inProgress' ? s.status : 'pending';
  const mark = h('span', { class: 'ps-mark' });
  if (st === 'completed') mark.append(icon('check'));
  else if (st === 'inProgress') mark.append(spinner('ps-spin'));
  return h('li', { class: 'ps ' + st }, mark, h('span', { class: 'ps-text', text: s.text || '' }));
}

// ───── Notice／Error ─────
/**
 * Notice 的種類（noticeKind）：〔熱更新〕cliUpdate＝CLI 換版的說明，加 CLI 圖示與自己的樣式；appUpdate＝app 自我更新的接續說明（下載圖示、同樣的框）；
 * clear＝/clear 清除了對話（重新整理圖示、同樣的框；它之前的項目由 ConvView.applyClear 藏起來）；
 * accountSwitch＝額度用完、自動改用另一個帳戶繼續（人像圖示、同 cliUpdate 的框）；backgroundTask＝Codex 的背景指令跑完、代理接著處理
 * （時鐘圖示、同 cliUpdate 的框）；sideQuestion＝/btw 旁支提問的問與答（不在對話的上下文，問號圖示、自己的框）；
 * fork＝延伸出來的新對話的開頭說明（forkConversation；分支圖示、同 cliUpdate 的框）；其他（省略或不認得）是一般提示
 */
const NOTICE_KIND = {
  cliUpdate: { cls: 'cli-update', icon: 'terminal' },
  appUpdate: { cls: 'app-update', icon: 'download' },
  clear: { cls: 'cleared', icon: 'refresh' },
  accountSwitch: { cls: 'account-switch', icon: 'user' },
  backgroundTask: { cls: 'bg-task', icon: 'clock' },
  backgroundProgram: { cls: 'bg-program', icon: 'terminal' },
  overloadRetry: { cls: 'retry-wait', icon: 'clock' },
  sideQuestion: { cls: 'side-q', icon: 'question' },
  fork: { cls: 'forked', icon: 'gitBranch' },
};

function createNote(item, ctx, kind) {
  const n = shell(item, 'it-' + kind);
  n._md = h('div', { class: 'md' });
  n._ic = h('span', { class: 'note-icwrap' });
  n.append(n._ic, n._md);
  setNoteKind(n, item, kind);
  setHtml(n._md, item.html, ctx);
  paintHandoff(n);
  startRetryCountdown(n, item, ctx);
  return n;
}

/**
 * 「60 秒後自動重試」的倒數條（noticeKind overloadRetry；fxrules.retryCountdown）：從提示的時間起算，以 Web Animations 縮 scaleX（只動 transform）。
 * 倒數中這個對話開始執行時 settleRetryNotes 收尾：快到時間是重試開始了（閃一下），更早是重試取消了（倒數條淡掉）。
 * 已經過了時間（載入舊的提示）、減少動態或關掉動態效果時不畫
 */
function startRetryCountdown(n, item, ctx) {
  if (item.noticeKind !== 'overloadRetry' && item.noticeKind !== 'OverloadRetry') return;
  const delayMs = parseRetryDelayMs(n._md ? n._md.textContent : '');
  const c = retryCountdown(Number(item.time), Date.now(), delayMs);
  if (!c || c.remaining <= 0 || calmMotion()) return;
  const fill = h('i');
  n.append(h('span', { class: 'retry-bar', 'aria-hidden': 'true' }, fill));
  if (fill.animate) fill.animate([{ transform: 'scaleX(' + c.frac + ')' }, { transform: 'scaleX(0)' }], { duration: c.remaining, easing: 'linear', fill: 'forwards' });
  n._retryTime = Number(item.time);
  n._retryDelayMs = delayMs;
  (ctx.retryNotes || (ctx.retryNotes = new Set())).add(n);
}

/** 這個對話開始執行（conversation.js 的 setHeader）：倒數中的重試提示收尾——重試開始了閃一下，提早開始（取消）倒數條淡掉 */
export function settleRetryNotes(ctx) {
  if (!ctx.retryNotes || !ctx.retryNotes.size) return;
  const now = Date.now();
  for (const n of ctx.retryNotes) {
    const outcome = n.isConnected ? retryOutcome(n._retryTime, now, n._retryDelayMs) : null;
    if (outcome) replayClass(n, outcome === 'go' ? 'fx-retry-go' : 'fx-retry-cancel', 900);
    n.classList.add('retry-done');
  }
  ctx.retryNotes.clear();
}

function setNoteKind(n, item, kind) {
  // 協定是小駝峰（cliUpdate）；C# 列舉直接轉成字串是大駝峰（CliUpdate）：兩種都認
  const nkName = typeof item.noticeKind === 'string' && item.noticeKind ? item.noticeKind.charAt(0).toLowerCase() + item.noticeKind.slice(1) : '';
  const nk = kind === 'notice' ? NOTICE_KIND[nkName] || null : null;
  const key = nk ? nkName : kind;
  if (n._kindKey === key) return;
  if (n._kindCls) n.classList.remove(n._kindCls);
  n._kindCls = nk ? nk.cls : null;
  if (nk) n.classList.add(nk.cls);
  n._kindKey = key;
  n._ic.textContent = '';
  n._ic.append(icon(nk ? nk.icon : kind === 'error' ? 'error' : 'info', 'note-ic'));
}

function fillNote(n, item, prev, ctx) {
  setNoteKind(n, item, item.kind === 'Error' ? 'error' : 'notice');
  if (!prev || prev.html !== item.html) {
    setHtml(n._md, item.html, ctx);
    paintHandoff(n);
  }
}

/**
 * 換帳戶提示的交接條（第十批，fxrules.handoffLabels）：提示底下一條舊帳戶色→新帳戶色的線、圖示用新帳戶色；
 * 名稱對不到帳戶（改了名、刪了）時那一端用中性色。剛出現的（fx-in）有一道光從舊帳戶那端滑到新帳戶（fx.css）
 */
function paintHandoff(n) {
  const ho = n._kindKey === 'accountSwitch' ? handoffLabels(n._md.textContent) : null;
  n.classList.toggle('ho', !!ho);
  if (!ho) return;
  const colorOf = (label) => {
    const a = label ? (state.accounts || []).find((x) => x.label === label) : null;
    return a ? safeColor(a.color, a.provider) : null;
  };
  n.style.setProperty('--ho-from', colorOf(ho.from) || 'var(--text-3)');
  n.style.setProperty('--ho-to', colorOf(ho.to) || 'var(--tone-auto)');
}

// ───── 附件與媒體 ─────
/** 〔遠端〕瀏覽器版的文件附件沒有本機路徑，網頁讀不到 */
const REMOTE_ATTACHMENT_TEXT = '這個附件在對話所在的電腦上，網頁版不能開啟';

export function renderAttachments(list, ctx, opts = {}) {
  const row = h('div', { class: 'atts' + (opts.composer ? ' in-composer' : ' in-msg') });
  for (const a of list) row.append(attachmentTile(a, ctx, opts));
  return row;
}

export function attachmentTile(a, ctx, opts = {}) {
  const composer = !!opts.composer;
  const title = a.name + (a.size ? '（' + a.size + '）' : '');
  let tile;
  if (a.kind === 'Image' && (a.thumbUrl || a.url)) {
    tile = h('button', { type: 'button', class: 'att att-img', title });
    tile.append(fadeInImg(h('img', { src: a.thumbUrl || a.url, alt: a.name || '', loading: 'lazy', decoding: 'async', draggable: 'false' })));
    tile.dataset.lbUrl = a.url || a.thumbUrl;
    tile.dataset.lbName = a.name || '';
    if (a.path) tile.dataset.lbPath = a.path;
    if (composer) tile.append(h('span', { class: 'att-cap', text: a.name || '' }));
  } else if (a.kind === 'Video') {
    tile = h('button', { type: 'button', class: 'att att-vid', title });
    markVideo(tile, a.url, a.name, a.path);
    if (a.thumbUrl) tile.append(fadeInImg(h('img', { src: a.thumbUrl, alt: '', loading: 'lazy', decoding: 'async', draggable: 'false' })));
    tile.append(h('span', { class: 'att-play' }, icon('play')));
    const info = a.videoInfo || a.size;
    if (info) tile.append(h('span', { class: 'att-badge', text: info }));
    if (composer) tile.append(h('span', { class: 'att-cap', text: a.name || '' }));
    tile.addEventListener('click', () => {
      if (a.url && ctx && ctx.openVideo) ctx.openVideo({ url: a.url, name: a.name, path: a.path });
      else if (a.path) post(pathCmd('openPath', a.path, ctx));
    });
  } else {
    // 〔遠端〕瀏覽器版的附件沒有本機路徑（伺服器拿掉 path）：沒有東西可以 fileFetch，說明檔案只在那台電腦上
    const remoteOnly = isHub && !a.path;
    tile = h('button', { type: 'button', class: 'att att-doc', title: remoteOnly ? title + '\n' + REMOTE_ATTACHMENT_TEXT : title },
      h('span', { class: 'att-ext', text: (extOf(a.name) || 'file').slice(0, 4).toUpperCase() }),
      h('span', { class: 'att-meta' }, h('span', { class: 'att-name', text: a.name || '' }), voiceWave(a.wave) || h('span', { class: 'att-size', text: a.size || '' })));
    if (a.wave) tile.classList.add('att-voice');
    tile.addEventListener('click', () => {
      if (a.path) post(pathCmd('openPath', a.path, ctx));
      else if (remoteOnly && !composer) toast(REMOTE_ATTACHMENT_TEXT);
    });
  }
  if (!composer) return tile;
  const wrap = h('div', { class: 'chip-att', dataset: { key: a.id } }, tile);
  if (opts.onRemove) wrap.append(iconButton('x', '移除附件', () => opts.onRemove(a), 'chip-x'));
  return wrap;
}

/** 影片的右鍵選單（mediamenu.js 的 mediaOfTarget）讀的網址、名稱、路徑；沒有網址的不標（沒有東西可以複製或下載） */
function markVideo(el, url, name, path) {
  if (!url) return;
  el.dataset.mediaKind = 'video';
  el.dataset.mediaUrl = url;
  el.dataset.mediaName = name || '';
  if (path) el.dataset.mediaPath = path;
}

export function renderMedia(media, ctx) {
  const box = h('div', { class: 'media' });
  const imgs = media.filter((m) => m.kind !== 'video');
  const vids = media.filter((m) => m.kind === 'video');
  if (imgs.length) {
    const grid = h('div', { class: 'mgrid n' + Math.min(imgs.length, 4) });
    for (const m of imgs) {
      const b = h('button', { type: 'button', class: 'mimg', title: m.name || m.path || '' },
        fadeInImg(h('img', { src: m.url, alt: m.name || '', loading: 'lazy', decoding: 'async', draggable: 'false' })));
      b.dataset.lbUrl = m.url;
      b.dataset.lbName = m.name || baseName(m.path);
      if (m.path) b.dataset.lbPath = m.path;
      grid.append(b);
    }
    box.append(grid);
  }
  for (const m of vids) {
    // 有控制列的影片放在 iframe 裡（framedvideo.js：控制列的載入轉圈會讓整頁的動畫每轉一圈都叫醒主執行緒）
    let wrap = null;
    // 〔遠端〕影片載入失敗（伺服器 403 policy）而且那台的政策是 Off 或暫停：說明只能看縮圖（擁有者的 WP4.7 Q1）
    const onError = isHub && ctx && typeof ctx.videoErrorText === 'function' ? () => {
      const text = ctx.videoErrorText();
      if (!text || !wrap || wrap.querySelector(':scope > .mvid-note')) return;
      wrap.append(h('div', { class: 'mvid-note', role: 'status', dataset: { field: 'mediaPolicyNote' } }, icon('info'), h('span', { text })));
    } : null;
    const fv = framedVideo({ src: m.url, title: '影片：' + (m.name || baseName(m.path)), playsinline: true, onError });
    const cap = h('div', { class: 'mvid-cap' }, icon('video'), h('span', { class: 'mvid-name', text: m.name || baseName(m.path) }),
      iconButton('fit', '放大播放', () => {
        const v = fv.video();
        if (v) v.pause();
        ctx.openVideo({ url: m.url, name: m.name, path: m.path, time: fv.currentTime() });
      }, 'sm'),
      m.path ? iconButton('folderOpen', '在檔案總管顯示', () => post(pathCmd('revealPath', m.path, ctx)), 'sm') : null);
    wrap = h('div', { class: 'mvid' }, fv.el, cap);
    markVideo(wrap, m.url, m.name || baseName(m.path), m.path);
    box.append(wrap);
  }
  return box;
}

function sameJson(a, b) {
  if (a === b) return true;
  if (!a || !b) return !a && !b;
  try { return JSON.stringify(a) === JSON.stringify(b); } catch { return false; }
}
