// 對話框的視覺效果（fx.css 的「對話框」兩段）用到的純規則：沒有 import、沒有 DOM
// （tools/ui-probe/rules.test.mjs 以 data: URL 匯入測試）。

/** 一批即時新增的項目最多幾個做進場動畫：重新連線補上一大批時整批直接出現，不要幾十個一起動 */
export const ENTRANCE_MAX = 6;

/** 這一批即時新增了 count 個項目，要不要做進場動畫 */
export function entranceAllowed(count) {
  return Number.isInteger(count) && count > 0 && count <= ENTRANCE_MAX;
}

/**
 * 差異統計後面的方塊比例條（像 GitHub）：cells 格裡，增刪的行數各佔幾格。
 * 上色的格數是 min(cells, 增刪合計)，所以小改動只亮幾格；兩種都有時各至少一格。沒有增刪時回空陣列。
 * 回傳每一格的種類：'add'、'del'、'none'
 */
export function diffRatioCells(add, del, cells = 5) {
  const a = Math.max(0, Math.floor(Number(add) || 0));
  const d = Math.max(0, Math.floor(Number(del) || 0));
  const total = a + d;
  if (!total) return [];
  const colored = Math.min(cells, total);
  let na = Math.round(colored * a / total);
  if (a && !na) na = 1;
  if (d && na === colored) na = colored - 1;
  const nd = colored - na;
  const out = [];
  for (let i = 0; i < cells; i++) out.push(i < na ? 'add' : i < na + nd ? 'del' : 'none');
  return out;
}

/** 用量的 5 格刻度（輸入框下方那一行，時間窗由 infoWindows 挑）：每 20% 一格（無條件進位）、有用量時至少一格、超過 100% 算滿；沒有用量 0 格 */
export function meterCells(percent, cells = 5) {
  const p = Number(percent);
  if (!Number.isFinite(p) || p <= 0) return 0;
  return Math.min(cells, Math.max(1, Math.ceil(p / (100 / cells))));
}

/** 程式碼框標頭的行數：結尾的一個換行不算一行，空的程式碼是 0（不顯示） */
export function codeLineCount(text) {
  const s = String(text ?? '').replace(/\r\n?/g, '\n').replace(/\n$/, '');
  return s ? s.split('\n').length : 0;
}

// 宿主（AgentHub.Render）與伺服器（ConversationViewRules.LastTurnText）都寫成「輸入 {N0}・輸出 {N0} tokens」
const TURN_RE = /^輸入 (\d{1,3}(?:,\d{3})*)・輸出 (\d{1,3}(?:,\d{3})*) tokens$/;

/** 對話底部「上一輪」的 token 數：認得的格式回 { input, output }，其他（null、別的格式）回 null，照原文顯示 */
export function parseTurnTokens(text) {
  const m = TURN_RE.exec(typeof text === 'string' ? text : '');
  return m ? { input: Number(m[1].replace(/,/g, '')), output: Number(m[2].replace(/,/g, '')) } : null;
}

/** 在這個對話按停止之後這麼久之內結束的一輪，不閃結束的燈（是使用者自己停的，不是做完了） */
export const STOP_QUIET_MS = 15_000;

/**
 * 一輪結束（執行中→停下）時輸入框閃的燈色：剛在這裡按過停止回 null（不閃）、最後一個項目是錯誤回 'err'、其他 'ok'。
 * stopAt 是這個對話最近一次按停止的時間（沒有就是 undefined／0）
 */
export function turnEndTone(lastKind, stopAt, now) {
  if (Number.isFinite(stopAt) && stopAt > 0 && now - stopAt >= 0 && now - stopAt < STOP_QUIET_MS) return null;
  return lastKind === 'Error' ? 'err' : 'ok';
}

/** 工具從執行中變成結束時那一列閃的效果：完成 'fx-done'、失敗 'fx-fail'；其他（拒絕、停止、沒有經過執行中）不閃 */
export function toolSettleFx(prevStatus, status) {
  if (prevStatus !== 'Running') return null;
  if (status === 'Done') return 'fx-done';
  if (status === 'Failed') return 'fx-fail';
  return null;
}

const SEV_RANK = { ok: 0, warn: 1, high: 2, max: 3 };
/** 用量百分比一次掉了這麼多（額度重置、上下文壓縮）才算「轉回去」 */
export const GAUGE_RESET_DROP = 10;

/**
 * 用量圓環（輸入框的上下文 .ctxm、側欄的額度 .mmeter）的一次性效果：嚴重度升高 'up'（圓環彈一下）、
 * 百分比一次掉了 GAUGE_RESET_DROP 點以上 'reset'（圓環倒轉一圈）；第一次顯示（prevSev 沒有值）與其他 null
 */
export function gaugeFx(prevSev, sev, prevPct, pct) {
  if (prevSev === undefined || prevSev === null) return null;
  if ((SEV_RANK[sev] ?? 0) > (SEV_RANK[prevSev] ?? 0)) return 'up';
  if (Number.isFinite(prevPct) && Number.isFinite(pct) && prevPct - pct >= GAUGE_RESET_DROP) return 'reset';
  return null;
}

/**
 * 執行列換階段時閃的效果（launchbar.js 加上 'fx-l-' + 結果）：建置完成開始執行 'ok'（綠）、回到閒置而上一次是建置失敗或無法啟動 'err'（紅）、
 * 暫停 'pause'（琥珀）、其他回到閒置（停止、結束）'cool'（餘暉）；第一次顯示、沒有換階段、其他的換法 null
 */
export function launchFx(prev, next, lastKind) {
  if (!prev || prev === next) return null;
  if (prev === 'building' && next === 'running') return 'ok';
  if (next === 'paused') return 'pause';
  if (next !== 'idle') return null;
  return lastKind === 'buildFailed' || lastKind === 'startFailed' ? 'err' : 'cool';
}

/**
 * 等你作答的卡片（問卷、核准、計畫確認）有了結果，角框鎖定成哪個顏色閃兩下（conversation.js 加上 class）：
 * 回答、允許、核准 'fx-c-ok'（綠）；拒絕 'fx-c-no'（紅）；略過問卷、繼續規劃 'fx-c-skip'（琥珀）。
 * 失效（Cancelled：這一輪結束、CLI 重開）不是你做的決定，不閃；不是從等待中變過來的（載入、重畫）也不閃
 */
export function cardSettleFx(kind, prevStatus, item) {
  if (prevStatus !== 'Waiting' || !item || item.status === 'Waiting' || item.status === 'Cancelled') return null;
  if (kind !== 'Question' && kind !== 'Approval' && kind !== 'PlanApproval') return null;
  const d = item.decision;
  if (kind === 'Approval') return d === 'deny' || item.status === 'Declined' ? 'fx-c-no' : 'fx-c-ok';
  if (kind === 'Question') return d === 'skip' || item.status === 'Declined' ? 'fx-c-skip' : 'fx-c-ok';
  if (d === 'keepPlanning') return 'fx-c-skip';
  return d === 'acceptEdits' || d === 'manual' ? 'fx-c-ok' : null;
}

/** 側欄清單一次最多幾列做進場（之後的直接出現），以及每列錯開多久 */
export const LIST_ENTRANCE_MAX = 12;
export const LIST_STAGGER_MS = 24;

/**
 * 側欄清單重繪後，新出現的列（新對話、搜尋結果、換篩選）有幾列要淡入：上一次一列都沒有（剛啟動、第一次載入）不做，
 * 其他依清單順序最多 LIST_ENTRANCE_MAX 列
 */
export function listEntranceCount(prevRows, freshRows) {
  if (!(prevRows > 0) || !(freshRows > 0)) return 0;
  return Math.min(freshRows, LIST_ENTRANCE_MAX);
}

/**
 * 徽章、摘要這類「字＋數字＋字」的文字拆成三段，數字那段給計數器解碼（'↑3' → { pre: '↑', n: 3, post: '' }、'↓2+'、'未合併 4'）；
 * 沒有數字或有兩段數字時回 null（照原文顯示）
 */
export function splitCount(text) {
  const m = typeof text === 'string' ? /^(\D*)(\d+)(\D*)$/.exec(text) : null;
  return m ? { pre: m[1], n: Number(m[2]), post: m[3] } : null;
}

/**
 * 自動命名產生了新標題（換之前或換之後在產生標題，titling）：標題先閃過亂碼再逐字鎖定。
 * 手動改名、其他電腦同步來的改名、第一次顯示都直接換
 */
export function titleDecodeFx(prev, next) {
  if (!prev || !next || typeof next.title !== 'string' || !next.title) return false;
  return prev.title !== next.title && (!!prev.titling || !!next.titling);
}

const DECODE_GLYPHS = '▖▗▘▙▚▛▜▝▞▟▀▄▌▐■□▪▫'; // 同 tokenrules.js 的 GLYPHS
/** 標題解碼：第一個字鎖定的時間、最後一個字最晚幾毫秒鎖定（長標題每個字錯開得比較密）、每個字最多錯開多久、鎖定後亮多久 */
export const TITLE_DECODE_BASE_MS = 90;
export const TITLE_DECODE_MAX_MS = 560;
export const TITLE_STAGGER_MS = 40;
export const TITLE_LOCK_MS = 220;

/** 標題的第 i 個字（以碼位算，共 len 個）幾毫秒時鎖定 */
export function titleLockAt(i, len) {
  const step = len > 1 ? Math.min(TITLE_STAGGER_MS, (TITLE_DECODE_MAX_MS - TITLE_DECODE_BASE_MS) / (len - 1)) : 0;
  return TITLE_DECODE_BASE_MS + i * step;
}

/** 整段標題解碼要多久（含最後一個字鎖定後亮的時間）；空字串 0 */
export function titleDecodeDuration(text) {
  const len = Array.from(typeof text === 'string' ? text : '').length;
  return len ? titleLockAt(len - 1, len) + TITLE_LOCK_MS : 0;
}

/**
 * 解碼到 elapsed 毫秒時標題的每一個字（碼位）：{ ch: 真的字, g: 疊在上面的亂碼（只有 scramble 有）, state }。
 * state：'scramble'（還沒鎖定，真的字透明、上面畫亂碼，所以寬度和讀到的文字一直是真的）、'lock'（剛鎖定，亮一下）、'done'。
 * 空白一直是 'done'。rand 回 [0,1)（測試可固定）
 */
export function titleDecodeCells(text, elapsed, rand = Math.random) {
  const chars = Array.from(typeof text === 'string' ? text : '');
  return chars.map((ch, i) => {
    if (/\s/.test(ch)) return { ch, g: null, state: 'done' };
    const at = titleLockAt(i, chars.length);
    if (elapsed >= at) return { ch, g: null, state: elapsed - at < TITLE_LOCK_MS ? 'lock' : 'done' };
    return { ch, g: DECODE_GLYPHS[Math.floor(rand() * DECODE_GLYPHS.length) % DECODE_GLYPHS.length], state: 'scramble' };
  });
}

/** API 過載的預設自動重試等多久（宿主 OverloadRetryRules.Delay）；開始執行時距離預定時間這麼近以內算是重試開始了，更早就是使用者自己送出、重試取消 */
export const RETRY_DELAY_MS = 60_000;
export const RETRY_GO_SLACK_MS = 3_000;

/** 從重試提示文字（「…，10 秒後自動重試…」「…，2 分鐘後自動重試…」）解析這次倒數總長（毫秒）；對不到回預設 60 秒 */
export function parseRetryDelayMs(text) {
  if (typeof text !== 'string' || !text) return RETRY_DELAY_MS;
  const m = /(\d+)\s*(秒|分鐘)後自動重試/.exec(text);
  if (!m) return RETRY_DELAY_MS;
  const n = Number(m[1]);
  if (!Number.isFinite(n) || n <= 0) return RETRY_DELAY_MS;
  return m[2] === '分鐘' ? n * 60_000 : n * 1_000;
}

/**
 * 自動重試提示（noticeKind overloadRetry）的倒數：從提示的時間起算，回 { remaining（毫秒，≥0）, frac（還剩的比例 0～1） }；
 * 時間不明回 null（不畫倒數條）
 */
export function retryCountdown(itemTime, now, delayMs = RETRY_DELAY_MS) {
  if (!Number.isFinite(itemTime) || itemTime <= 0 || !Number.isFinite(now)) return null;
  const total = Number.isFinite(delayMs) && delayMs > 0 ? delayMs : RETRY_DELAY_MS;
  const remaining = Math.max(0, Math.min(total, itemTime + total - now));
  return { remaining, frac: remaining / total };
}

/** 倒數中這個對話開始執行：快到預定時間（RETRY_GO_SLACK_MS 以內或已過）是重試開始了 'go'（閃一下），更早是重試取消了 'cancel'（倒數條淡掉） */
export function retryOutcome(itemTime, now, delayMs = RETRY_DELAY_MS) {
  const c = retryCountdown(itemTime, now, delayMs);
  if (!c) return null;
  const total = Number.isFinite(delayMs) && delayMs > 0 ? delayMs : RETRY_DELAY_MS;
  const slack = Math.min(RETRY_GO_SLACK_MS, total * 0.5);
  return c.remaining <= slack ? 'go' : 'cancel';
}

// ───── 工具做完、思考結束時內容掃過一遍再收起來（items.js 的 playStepPeek、conversation.js 的 playPeek） ─────
/** 小框最多露出幾行、最多取幾行（段）來捲（只是一閃而過，不必完整）、工具的每行最多幾個字、思考最多取幾個字 */
export const PEEK_ROWS = 5;
export const PEEK_MAX_LINES = 60;
export const PEEK_MAX_COLS = 200;
export const PEEK_THINK_MAX_CHARS = 2400;
/** 打開、內容捲過（超過小框的行數才捲：每行 PEEK_MS_PER_LINE，介於最短與最長之間）、停一下、收起來；被下一個工具接手時收得更快 */
export const PEEK_OPEN_MS = 120;
export const PEEK_SWEEP_MIN_MS = 260;
export const PEEK_SWEEP_MAX_MS = 820;
export const PEEK_MS_PER_LINE = 16;
export const PEEK_HOLD_MS = 110;
export const PEEK_SHORT_HOLD_MS = 380;
export const PEEK_FOLD_MS = 180;
export const PEEK_CUT_MS = 110;

/**
 * 這次更新要不要讓工具卡或思考的內容掃過一遍：從執行中變成完成或失敗，或一進來就已經完成或失敗（isNew：很快就做完的）。
 * 使用者展開著的（open）不播（內容本來就看得到）；拒絕、停止、等待核准、還在執行的不播
 */
export function peekWanted(prevStatus, status, isNew, open) {
  if (open || (status !== 'Done' && status !== 'Failed')) return false;
  return isNew ? prevStatus === undefined || prevStatus === null : prevStatus === 'Running';
}

/** 文字的前 max 行（換行統一成 \n、跳過開頭的空行、每行截到 PEEK_MAX_COLS 個字）；回 { lines, more } */
function headLines(text, max) {
  // 只看開頭夠用的那一段：輸出可能有好幾 MB
  const limit = max * (PEEK_MAX_COLS + 2) * 2;
  const cut = text.length > limit;
  let s = text.slice(0, limit).replace(/\r\n?/g, '\n').replace(/^(?:[ \t]*\n)+/, '');
  if (!cut) s = s.replace(/(?:\n[ \t]*)+$/, ''); // 結尾的空行不算「後面還有」
  const all = s.split('\n', max + 1);
  const more = all.length > max || cut;
  const lines = all.slice(0, max);
  while (lines.length && !lines[lines.length - 1].trim()) lines.pop();
  return { lines: lines.map((x) => x.length > PEEK_MAX_COLS ? x.slice(0, PEEK_MAX_COLS) : x), more };
}

/**
 * 掃過的內容：差異（編輯）優先、其次輸出、再其次輸入。回 { lines: [{ s: 文字, t: 'add'|'del'|'hunk'|'more'|null }] }，
 * 最多 PEEK_MAX_LINES 行（後面還有就再接一行 '…'，t 是 'more'）；都沒有內容回 null
 */
export function toolPeekLines(item) {
  if (!item) return null;
  let lines = [];
  let more = false;
  if (Array.isArray(item.diff) && item.diff.length) {
    for (const ln of item.diff.slice(0, PEEK_MAX_LINES)) {
      const t = ln && (ln.t === 'add' || ln.t === 'del' || ln.t === 'hunk') ? ln.t : null;
      const s = ln && ln.s != null ? String(ln.s) : '';
      lines.push({ s: s.length > PEEK_MAX_COLS ? s.slice(0, PEEK_MAX_COLS) : s, t });
    }
    more = item.diff.length > PEEK_MAX_LINES;
  } else {
    const text = [item.output, item.detail].find((x) => typeof x === 'string' && x.trim());
    if (!text) return null;
    const head = headLines(text, PEEK_MAX_LINES);
    lines = head.lines.map((s) => ({ s, t: null }));
    more = head.more;
  }
  if (!lines.length) return null;
  if (more) lines.push({ s: '…', t: 'more' });
  return { lines };
}

const ENTITIES = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: ' ' };

/** 一段 html 實體解碼成字（&amp;、&#39;、&#x4e2d; 這類；不認得的照原文） */
function decodeEntity(m, e) {
  if (e[0] === '#') {
    const cp = e[1] === 'x' || e[1] === 'X' ? parseInt(e.slice(2), 16) : parseInt(e.slice(1), 10);
    return Number.isInteger(cp) && cp > 0 && cp <= 0x10FFFF ? String.fromCodePoint(cp) : m;
  }
  return ENTITIES[e.toLowerCase()] ?? m;
}

/**
 * 思考的 html（宿主以 Markdig 轉好、停用原始 HTML）拆成一段一段的純文字，不經 DOM：段落、清單項目、標題、引用、表格列的結尾與換行標籤當成分段，
 * 程式碼框一行一段，其他標籤拿掉、實體解碼。只看開頭夠用的那一段（思考可能很長）；回 { blocks, cut（後面還有沒看的） }。
 * 結果只當文字用（textContent），不會再當成 html
 */
export function htmlTextBlocks(html) {
  if (typeof html !== 'string' || !html) return { blocks: [], cut: false };
  const limit = PEEK_THINK_MAX_CHARS * 4;
  const cut = html.length > limit;
  const s = html.slice(0, limit)
    .replace(/<[^>]*$/, '') // 切在標籤中間
    .replace(/<\/(?:p|li|h[1-6]|blockquote|tr|pre|div)\s*>|<br\s*\/?>/gi, '\n')
    .replace(/<\/(?:td|th)\s*>/gi, ' ')
    .replace(/<[^>]*>/g, '')
    .replace(/&(#[xX][0-9a-fA-F]+|#\d+|[a-zA-Z]+);/g, decodeEntity);
  return { blocks: s.split('\n'), cut };
}

/**
 * 思考掃過的內容：html 拆成段落（htmlTextBlocks），每段的空白縮成一個、空的段落跳過；最多 PEEK_MAX_LINES 段、
 * 合計 PEEK_THINK_MAX_CHARS 個字（後面還有就接一行 '…'）。思考是散文，小框裡會換行（不像工具一行一列）。
 * 回 { lines: [{ s, t }] }，沒有內容回 null
 */
export function thinkPeekLines(item) {
  const { blocks, cut } = htmlTextBlocks(item && item.html);
  const lines = [];
  let left = PEEK_THINK_MAX_CHARS;
  let more = false;
  for (const b of blocks) {
    const s = typeof b === 'string' ? b.replace(/\s+/g, ' ').trim() : '';
    if (!s) continue;
    if (left <= 0 || lines.length >= PEEK_MAX_LINES) { more = true; break; }
    if (s.length > left) {
      let head = s.slice(0, left);
      // 不要切在 UTF-16 代理對的中間（表情符號會變成亂碼）
      const last = head.charCodeAt(head.length - 1);
      if (last >= 0xD800 && last <= 0xDBFF) head = head.slice(0, -1);
      lines.push({ s: head, t: null });
      more = true;
      break;
    }
    lines.push({ s, t: null });
    left -= s.length;
  }
  if (!lines.length) return null;
  if (more || cut) lines.push({ s: '…', t: 'more' });
  return { lines };
}

/**
 * 掃過一遍的時間（毫秒）：rows 是小框的行數（內容不到 PEEK_ROWS 行時就是內容的行數）、steps 是要捲幾行（一格一行）；
 * 不必捲的停久一點，看得到內容。total 是到整個收起來為止
 */
export function toolPeekTiming(lineCount) {
  const n = Math.max(0, Math.floor(Number(lineCount) || 0));
  const rows = Math.min(n, PEEK_ROWS);
  const steps = Math.max(0, n - PEEK_ROWS);
  const sweep = steps ? Math.min(PEEK_SWEEP_MAX_MS, Math.max(PEEK_SWEEP_MIN_MS, steps * PEEK_MS_PER_LINE)) : 0;
  const hold = steps ? PEEK_HOLD_MS : PEEK_SHORT_HOLD_MS;
  const foldAt = PEEK_OPEN_MS + sweep + hold;
  return { rows, steps, open: PEEK_OPEN_MS, sweep, hold, foldAt, fold: PEEK_FOLD_MS, total: foldAt + PEEK_FOLD_MS };
}

// ───── 第五批：對話小地圖、遠端往返時間、拖放讀數、換帳戶交接、額度鎖定與重置倒數 ─────

/** 對話小地圖的標記種類與優先順序（同一個位置只畫一個時留最高的）：錯誤、等你回應的卡片、你的訊息、改了檔案的工具 */
export const MINIMAP_PRIORITY = { err: 4, wait: 3, user: 2, edit: 1 };
/** 軌道上兩個標記的中心距離不到這麼多像素就併成一個 */
export const MINIMAP_MIN_GAP = 3;
/** 捲動內容比可見高度高出這個倍數才顯示小地圖（只多一點點不必導覽） */
export const MINIMAP_MIN_RATIO = 1.3;

/** 項目在小地圖上的標記種類；不標的回 null */
export function minimapKind(item) {
  if (!item || typeof item !== 'object') return null;
  switch (item.kind) {
    case 'User': return 'user';
    case 'Error': return 'err';
    case 'Question': case 'Approval': case 'PlanApproval': return item.status === 'Waiting' ? 'wait' : null;
    case 'Tool':
      if (item.status === 'Failed') return 'err';
      return item.diffStats && (item.diffStats.add || item.diffStats.del) ? 'edit' : null;
    default: return null;
  }
}

/** 捲動內容 scrollHeight、可見 clientHeight：要不要顯示小地圖 */
export function minimapShown(scrollHeight, clientHeight) {
  return Number.isFinite(scrollHeight) && Number.isFinite(clientHeight) && clientHeight > 0 && scrollHeight > clientHeight * MINIMAP_MIN_RATIO;
}

/**
 * 小地圖的標記：entries 是 [{ id, kind, top }]（項目在捲動內容裡的上緣，px），total 是捲動內容的高度、railPx 是軌道的高度。
 * 回 [{ id, kind, at（0～1，在軌道上的位置）, count（併進來幾個） }]，依位置排序；在軌道上距離不到 MINIMAP_MIN_GAP 像素的併成一個，
 * 位置留第一個的、種類留優先順序最高的（同級留先出現的）。不認得的種類、位置不是數字的略過
 */
export function minimapMarks(entries, total, railPx) {
  if (!Array.isArray(entries) || !(total > 0) || !(railPx > 0)) return [];
  const list = entries.filter((e) => e && MINIMAP_PRIORITY[e.kind] && Number.isFinite(e.top)).slice().sort((a, b) => a.top - b.top);
  const out = [];
  for (const e of list) {
    const at = Math.max(0, Math.min(1, e.top / total));
    const last = out[out.length - 1];
    if (last && (at - last.at) * railPx < MINIMAP_MIN_GAP) {
      last.count++;
      if (MINIMAP_PRIORITY[e.kind] > MINIMAP_PRIORITY[last.kind]) { last.kind = e.kind; last.id = e.id; }
      continue;
    }
    out.push({ id: e.id, kind: e.kind, at, count: 1 });
  }
  return out;
}

/** 遠端指令往返時間的讀數：1 秒以下寫毫秒、10 秒以下一位小數的秒、更久整數秒；不是有效的時間回 null（不顯示） */
export function rttLabel(ms) {
  const v = Number(ms);
  if (!Number.isFinite(v) || v < 0) return null;
  if (v < 1000) return Math.round(v) + ' ms';
  if (v < 10_000) return (v / 1000).toFixed(1) + ' s';
  return Math.round(v / 1000) + ' s';
}

const DROP_ORDER = ['圖片', '影片', '音訊', '文件', '檔案'];
const DOC_TYPES = /^(text\/|application\/(pdf|json|xml|rtf|msword|vnd\.(ms-|openxmlformats|oasis)))/;

/** 拖進來的一個檔案（MIME 類型）算哪一類：圖片、影片、音訊、文件，其他（含不知道類型的）是檔案 */
export function dropGroup(type) {
  const t = typeof type === 'string' ? type.toLowerCase() : '';
  if (t.startsWith('image/')) return '圖片';
  if (t.startsWith('video/')) return '影片';
  if (t.startsWith('audio/')) return '音訊';
  if (DOC_TYPES.test(t)) return '文件';
  return '檔案';
}

/** 拖放的讀數：types 是每個檔案的 MIME 類型，回 { count, label（「圖片 2・文件 1」） }；沒有檔案回 { count: 0, label: '' } */
export function dropSummary(types) {
  const list = Array.isArray(types) ? types : [];
  const n = new Map();
  for (const t of list) { const g = dropGroup(t); n.set(g, (n.get(g) || 0) + 1); }
  return { count: list.length, label: DROP_ORDER.filter((g) => n.has(g)).map((g) => g + ' ' + n.get(g)).join('・') };
}

/** 換帳戶的交接：前後兩份標頭的帳戶不同時回 'provider'（換到另一種代理）或 'account'（同一種），其他（第一次、沒換）回 null */
export function handoffFx(prev, next) {
  if (!prev || !next || !prev.accountId || !next.accountId || prev.accountId === next.accountId) return null;
  return prev.provider && next.provider && prev.provider !== next.provider ? 'provider' : 'account';
}

const HOUR = 3_600_000;

/**
 * 用量時間窗有多長（毫秒）：Codex 的 key 是「{limitId}:{分鐘數}」，其他依 key 與 label 認 5 小時、每週、每日；認不得回 null（不畫倒數條）
 */
export function windowSpanMs(key, label) {
  const k = typeof key === 'string' ? key : '';
  const l = typeof label === 'string' ? label : '';
  const m = /:(\d+)$/.exec(k);
  if (m && Number(m[1]) > 0) return Number(m[1]) * 60_000;
  if (k === 'session' || /5h/i.test(k) || /5\s*小時/.test(l)) return 5 * HOUR;
  if (/week/i.test(k) || /每週|週/.test(l)) return 7 * 24 * HOUR;
  if (/dai|day/i.test(k) || /每日|每天/.test(l)) return 24 * HOUR;
  return null;
}

const WEEK_MS = 7 * 24 * HOUR;

/**
 * 輸入框下方那一行的用量燈號畫哪幾個時間窗：主要的（第一個，通常是 5 小時）再加第一個每週的（windowSpanMs 認得是 7 天、而且不是主要的那個）；
 * 主要的本身就是每週（Codex、Grok 只有每週）時只畫它。沒有時間窗回空陣列
 */
export function infoWindows(windows) {
  const list = Array.isArray(windows) ? windows.filter((w) => w && typeof w === 'object') : [];
  if (!list.length) return [];
  const isWeek = (w) => windowSpanMs(w.key, w.label) === WEEK_MS;
  const first = list[0];
  if (isWeek(first)) return [first];
  const week = list.find((w, i) => i > 0 && isWeek(w));
  return week ? [first, week] : [first];
}

/** 時間窗已經用完（鎖住）：百分比到 100 */
export function quotaLocked(win) {
  return !!win && Number(win.percent) >= 100;
}

/** 重置倒數條：還剩多少（resetsAt 減現在，除以時間窗長度，0～1）；沒有重置時間、已過、認不得長度回 null */
export function resetCountdown(win, now) {
  if (!win || !Number.isFinite(Number(win.resetsAt)) || !Number.isFinite(now)) return null;
  const span = windowSpanMs(win.key, win.label);
  const left = Number(win.resetsAt) - now;
  if (!span || left <= 0) return null;
  return Math.min(1, left / span);
}

/** 倒數條亮幾格：還剩 frac（0～1）、共 cells 格，無條件進位（還沒到就至少一格） */
export function countdownCells(frac, cells = 20) {
  const f = Number(frac);
  if (!Number.isFinite(f) || f <= 0) return 0;
  return Math.min(cells, Math.max(1, Math.ceil(f * cells)));
}

/** 主題切換要不要掃過去換色：前後主題都有、而且不同（第一次套用、沒變不掃） */
export function themeWipe(prev, next) {
  return !!prev && !!next && prev !== next;
}

/** 帳戶卡的持續動畫每多一個執行中的對話快幾成，與最快幾倍（2026-10-25 使用者要求：同時跑很多代理時轉得更快、顯示出快速運算的感覺——以前每個加 0.4、最快 2.2 倍） */
export const PULSE_TEMPO_STEP = 0.5;
export const PULSE_TEMPO_MAX = 4;

/**
 * 帳戶卡（儀表板的大卡、側欄的小卡）的持續動畫（fx.css 的 data-pulse）：
 * 有對話在執行（含其他在線電腦的）是 run，執行中的對話越多越快——tempo 是動畫時間的除數，1 個是 1、每多一個加 PULSE_TEMPO_STEP、最多 PULSE_TEMPO_MAX；
 * 沒有在執行、這台已登入（status ready）是 idle（已連上、在怠速：慢而淡）；其他（沒登入、檢查中、錯誤、只在其他電腦登入）是 off，不動
 */
export function accountPulse(status, remoteOnly, running) {
  const n = Math.max(0, Math.floor(Number(running) || 0));
  if (n > 0) return { mode: 'run', tempo: Math.min(PULSE_TEMPO_MAX, Math.round((1 + (n - 1) * PULSE_TEMPO_STEP) * 100) / 100) };
  if (status === 'ready' && !remoteOnly) return { mode: 'idle', tempo: 1 };
  return { mode: 'off', tempo: 1 };
}

/** 怠速動畫的相位（0～1，fx.css 的 --fx-phase）：依帳戶 id 固定，各張卡錯開、不會一起動；同一個帳戶的大卡與小卡相同 */
export function pulsePhase(id) {
  let x = 2166136261;
  for (const ch of String(id || '')) x = Math.imul(x ^ ch.codePointAt(0), 16777619) >>> 0;
  return (x % 1000) / 1000;
}

// ───── 第六批：輸出速度、工具計時、等你回應的時間、下載速度、檔案大小比例、捲動位置、自動壓縮 ─────

/** 輸出速度看最近多久（毫秒）：Claude 一則訊息才報一次用量，窗太短會一直跳 0 */
export const RATE_WINDOW_MS = 10_000;

/**
 * 這一輪的輸出速度（tokens／秒）：samples 是依時間排序的 [{ t, out }]（out 是這一輪到目前的輸出 token），基準是視窗開始前的最後一筆
 * （沒有就用第一筆），到 now 至少 1 秒才算；沒有新的輸出就是 0（工具在跑、等你回應時真的沒有在輸出）。算不出來回 null
 */
export function tokenRate(samples, now, windowMs = RATE_WINDOW_MS) {
  if (!Array.isArray(samples) || samples.length < 2 || !Number.isFinite(now)) return null;
  const last = samples[samples.length - 1];
  let base = samples[0];
  for (const s of samples) { if (s.t >= now - windowMs) break; base = s; }
  const dt = (now - base.t) / 1000;
  if (!(dt >= 1) || !Number.isFinite(last.out) || !Number.isFinite(base.out)) return null;
  return Math.max(0, Math.round((last.out - base.out) / dt));
}

/** 只留視窗內的樣本（另外留視窗開始前的最後一筆當基準），最多 64 筆 */
export function pruneRateSamples(samples, now, windowMs = RATE_WINDOW_MS) {
  const list = Array.isArray(samples) ? samples : [];
  let i = 0;
  while (i + 1 < list.length && list[i + 1].t < now - windowMs) i++;
  return list.slice(Math.max(i, list.length - 64));
}

/** 速度的寫法：「42/s」，一千以上「1.2K/s」（輸入框下方那一行不寫 token 這個字，單位在滑過的說明裡） */
export function rateLabel(n) {
  if (!Number.isFinite(n) || n < 0) return null;
  return (n >= 1000 ? (n / 1000).toFixed(1) + 'K' : String(Math.round(n))) + '/s';
}

/** 執行中的工具跑超過這麼久算慢（計時變琥珀色） */
export const SLOW_TOOL_MS = 30_000;

/** 已經跑了多久的短寫法：「12s」「3m 05s」「1h 02m」 */
export function elapsedShort(ms) {
  if (!Number.isFinite(ms) || ms < 0) return '';
  const s = Math.floor(ms / 1000);
  if (s < 60) return s + 's';
  const m = Math.floor(s / 60);
  if (m < 60) return m + 'm ' + String(s % 60).padStart(2, '0') + 's';
  return Math.floor(m / 60) + 'h ' + String(m % 60).padStart(2, '0') + 'm';
}

/** 等你回應超過這麼久算久（卡片的燈變琥珀色） */
export const STALE_WAIT_MS = 10 * 60_000;

/** 等了多久：「剛剛」「已等 3 分」「已等 1 小時 5 分」 */
export function waitAgeLabel(ms) {
  if (!Number.isFinite(ms) || ms < 60_000) return '剛剛';
  const m = Math.floor(ms / 60_000);
  if (m < 60) return '已等 ' + m + ' 分';
  return '已等 ' + Math.floor(m / 60) + ' 小時' + (m % 60 ? ' ' + (m % 60) + ' 分' : '');
}

/** 下載速度（位元組／秒）：prev、next 是 { bytes, at }，next 比 prev 多、相隔 0.2 秒以上才算；算不出來回 null */
export function byteRate(prev, next) {
  if (!prev || !next || !Number.isFinite(prev.bytes) || !Number.isFinite(next.bytes)) return null;
  const dt = (next.at - prev.at) / 1000;
  if (!(dt >= 0.2) || next.bytes < prev.bytes) return null;
  return (next.bytes - prev.bytes) / dt;
}

/** 速度的寫法：「3.4 MB/s」「820 KB/s」「512 B/s」 */
export function fmtByteRate(bps) {
  if (!Number.isFinite(bps) || bps < 0) return '';
  if (bps >= 1048576) return (bps / 1048576).toFixed(1) + ' MB/s';
  if (bps >= 1024) return Math.round(bps / 1024) + ' KB/s';
  return Math.round(bps) + ' B/s';
}

/** 大小相對最大的比例（0～1；檔案抽屜的比例條、匯入清單的訊息數）：有大小的至少 0.04 看得到，沒有大小或最大是 0 回 null */
export function sizeFrac(size, max) {
  if (!Number.isFinite(size) || size < 0 || !Number.isFinite(max) || max <= 0) return null;
  return size === 0 ? 0 : Math.min(1, Math.max(0.04, size / max));
}

/** 最近改過的檔案（檔案抽屜那一列前面的燈）：一小時內 */
export const RECENT_FILE_MS = 3_600_000;
export function recentlyModified(modified, now) {
  return Number.isFinite(modified) && Number.isFinite(now) && modified <= now + 60_000 && now - modified < RECENT_FILE_MS;
}

/**
 * 捲動位置（第幾輪）：kinds 是對話項目的種類（依順序），topIndex 是畫面最上面那一則的索引；
 * 回 { n（它往前最近的使用者訊息是第幾則）, total（使用者訊息共幾則） }，少於兩則使用者訊息回 null
 */
export function turnPosition(kinds, topIndex) {
  if (!Array.isArray(kinds)) return null;
  let total = 0, n = 0;
  kinds.forEach((k, i) => { if (k === 'User') { total++; if (i <= topIndex) n = total; } });
  if (total < 2) return null;
  return { n: Math.max(1, n), total };
}

/** 自動壓縮上下文：用量降到原本的六成以下、而且少了至少 2 萬 token 才算；回 { from, to }，其他回 null */
export function compactDrop(prevUsed, used) {
  if (!Number.isFinite(prevUsed) || !Number.isFinite(used) || prevUsed <= 0 || used < 0) return null;
  if (used > prevUsed * 0.6 || prevUsed - used < 20_000) return null;
  return { from: prevUsed, to: used };
}

/** 上下文用量的短寫法（自動壓縮的說明）：「412K」「96K」「1.2M」，一千以下照寫 */
export function fmtTokK(n) {
  if (!Number.isFinite(n) || n < 0) return '';
  if (n >= 1_000_000) return (n / 1_000_000).toFixed(1).replace(/\.0$/, '') + 'M';
  if (n >= 1000) return Math.round(n / 1000) + 'K';
  return String(Math.round(n));
}

// ───── 第七批：一輪的結算單、思考的耗時、差異的變動地圖、上傳讀數、用量燃燒預估、建置的錯誤與警告數、配對碼倒數、遠端操作紀錄的燈 ─────

/** token 數的短寫法（結算單）：「842」「2.1K」「18.2K」「412K」「1.2M」 */
export function tokShort(n) {
  if (!Number.isFinite(n) || n < 0) return '';
  if (n >= 1_000_000) return (n / 1_000_000).toFixed(1).replace(/\.0$/, '') + 'M';
  if (n >= 100_000) return Math.round(n / 1000) + 'K';
  if (n >= 1000) return (n / 1000).toFixed(1).replace(/\.0$/, '') + 'K';
  return String(Math.round(n));
}

/** 一輪的結算單在輸入框上方留多久（fx.css 的 .turn-sum 淡出同一段時間） */
export const TURN_SUM_MS = 4200;

/** 經過時間的寫法（結算單、思考的耗時）：「14 秒」「2 分 14 秒」「1 小時 2 分」 */
export function durationLabel(ms) {
  if (!Number.isFinite(ms) || ms < 0) return '';
  const s = Math.max(0, Math.round(ms / 1000));
  if (s < 60) return s + ' 秒';
  const m = Math.floor(s / 60);
  if (m < 60) return m + ' 分' + (s % 60 ? ' ' + (s % 60) + ' 秒' : '');
  return Math.floor(m / 60) + ' 小時' + (m % 60 ? ' ' + (m % 60) + ' 分' : '');
}

/** 短的秒數（首次回應）：10 秒以下寫到小數一位「1.8 秒」，再長寫整數 */
export function secondsLabel(ms) {
  if (!Number.isFinite(ms) || ms < 0) return '';
  const s = ms / 1000;
  return (s < 10 ? (Math.round(s * 10) / 10).toFixed(1) : String(Math.round(s))) + ' 秒';
}

/**
 * 這一輪的項目（結算單用）：order 是項目 id 的順序、get 取項目；從最後往前找到開始這一輪的使用者訊息為止（不含它）
 * （這一輪中途插入的使用者訊息也在這一輪裡；不知道開始時間時停在最後一則使用者訊息）
 */
export function turnItems(order, get, startedAt) {
  const out = [];
  const known = Number.isFinite(startedAt);
  for (let i = order.length - 1; i >= 0; i--) {
    const it = get(order[i]);
    if (!it) continue;
    // 開始這一輪的那則（時間不晚於開始時間＋1 秒）以前的都不算；更晚的使用者訊息是中途插入的
    if (it.kind === 'User' && (!known || !(it.time > startedAt + 1000))) break;
    out.push(it);
  }
  return out.reverse();
}

/**
 * 一輪的結算單（輸入框上方浮出一行、約 4 秒後淡出）：
 * state 是 'ok'｜'err'｜'stop'｜'bg'（背景工作還在跑）；items 是這一輪的項目（turnItems）；tokens 是 parseTurnTokens 的 { input, output }。
 * 回 [{ k, text, add?, del? }]：狀態、花了多久、工具幾次、改了幾個檔案（+增 −刪，同一個檔案只算一次）、token、首次回應。
 * 除了狀態什麼都沒有時回 null（不值得浮出來）
 */
export function turnSummary({ state, startedAt, endedAt, items, tokens }) {
  const parts = [{ k: 'state', text: state === 'err' ? '失敗' : state === 'stop' ? '已停止' : state === 'bg' ? '等背景工作' : '完成' }];
  const timed = Number.isFinite(startedAt) && Number.isFinite(endedAt) && endedAt >= startedAt;
  if (timed) parts.push({ k: 'dur', text: durationLabel(endedAt - startedAt) });
  let tools = 0, add = 0, del = 0;
  const files = new Set();
  let first = null;
  for (const it of Array.isArray(items) ? items : []) {
    if (!it || it.kind === 'User') continue;
    if (first === null && Number.isFinite(it.time)) first = it.time;
    if (it.kind !== 'Tool') continue;
    tools++;
    const d = it.diffStats;
    if (d && ((d.add || 0) > 0 || (d.del || 0) > 0)) {
      files.add(it.filePath || it.id);
      add += d.add || 0;
      del += d.del || 0;
    }
  }
  if (tools) parts.push({ k: 'tools', text: '工具 ' + tools + ' 次' });
  if (files.size) parts.push({ k: 'files', text: files.size + ' 個檔案', add, del });
  if (tokens && Number.isFinite(tokens.input) && Number.isFinite(tokens.output) && tokens.input + tokens.output > 0) {
    parts.push({ k: 'tok', text: '輸入 ' + tokShort(tokens.input) + '・輸出 ' + tokShort(tokens.output) });
  }
  if (timed && first !== null && first >= startedAt && first - startedAt <= endedAt - startedAt) {
    parts.push({ k: 'ttft', text: '首次回應 ' + secondsLabel(first - startedAt) });
  }
  return parts.length > 1 ? parts : null;
}

/** 思考的耗時超過這麼久就不寫（下一個項目可能是很久以後才來的，推算出來的不是思考的時間） */
export const THINK_MAX_MS = 30 * 60_000;

/**
 * 結束的思考花了多久（毫秒）：項目沒有結束時間，以下一個項目的時間推算；下一個是使用者訊息（這一輪在思考時被停下）、
 * 時間不明、倒退或超過 THINK_MAX_MS 都回 null（不寫）
 */
export function thinkDuration(item, next) {
  if (!item || !next || next.kind === 'User' || !Number.isFinite(item.time) || !Number.isFinite(next.time)) return null;
  const ms = next.time - item.time;
  return ms >= 0 && ms <= THINK_MAX_MS ? ms : null;
}

/** 思考耗時的寫法：不到一秒寫「不到 1 秒」，其他同 durationLabel */
export function thinkTimeLabel(ms) {
  if (!Number.isFinite(ms) || ms < 0) return '';
  return ms < 1000 ? '不到 1 秒' : durationLabel(ms);
}

/** 差異多長才加變動地圖（差異框最高 420px，大約 22 行就要捲動） */
export const DIFF_MAP_MIN_LINES = 25;
/** 變動地圖最多分幾格（每格至少一行；420px 高時一格約 3.5px） */
export const DIFF_MAP_BUCKETS = 120;

/**
 * 差異的變動地圖（第七批視覺效果）：差異框右側的細軌，左半是刪除、右半是新增，標出改在哪裡。
 * lines 是 Item 的 diff（{ t, s }），只看前 n 行（畫出來的那些）；回 { add: [[起, 迄]…], del: […] }（0～100 的百分比，相鄰的格子併成一段），
 * 行數不到 DIFF_MAP_MIN_LINES 或沒有增刪回 null
 */
export function diffMapRuns(lines, n, buckets = DIFF_MAP_BUCKETS) {
  if (!Array.isArray(lines)) return null;
  const count = Math.min(Number.isFinite(n) ? n : lines.length, lines.length);
  if (count < DIFF_MAP_MIN_LINES) return null;
  const cells = Math.max(1, Math.min(count, buckets));
  const add = new Array(cells).fill(false);
  const del = new Array(cells).fill(false);
  for (let i = 0; i < count; i++) {
    const t = lines[i] && lines[i].t;
    if (t !== 'add' && t !== 'del') continue;
    (t === 'add' ? add : del)[Math.min(cells - 1, Math.floor(i * cells / count))] = true;
  }
  const pct = (b) => Math.round(b * 10000 / cells) / 100;
  const runs = (marks) => {
    const out = [];
    let start = -1;
    for (let b = 0; b <= cells; b++) {
      const on = b < cells && marks[b];
      if (on && start < 0) start = b;
      else if (!on && start >= 0) { out.push([pct(start), pct(b)]); start = -1; }
    }
    return out;
  };
  const r = { add: runs(add), del: runs(del) };
  return r.add.length || r.del.length ? r : null;
}

/** 一條直的漸層（變動地圖的一半）：runs 的區段是 color，其他透明 */
export function diffMapGradient(runs, color) {
  const stops = [];
  let prev = 0;
  for (const [from, to] of runs) {
    if (from > prev) stops.push(`transparent ${prev}% ${from}%`);
    stops.push(`${color} ${from}% ${to}%`);
    prev = to;
  }
  if (prev < 100) stops.push(`transparent ${prev}% 100%`);
  return `linear-gradient(to bottom, ${stops.join(', ')})`;
}

/** 上傳讀數的大小寫法：「512 KB」「3.2 MB」「12 MB」（10 以上不寫小數） */
function fmtSize(n) {
  if (!Number.isFinite(n) || n < 0) return '';
  if (n >= 1048576) { const v = n / 1048576; return (v < 10 ? v.toFixed(1) : String(Math.round(v))) + ' MB'; }
  if (n >= 1024) return Math.round(n / 1024) + ' KB';
  return Math.round(n) + ' B';
}

/**
 * 上傳中的讀數（瀏覽器版、操作其他電腦的對話上傳附件時，晶片上的那一行）：「3.2／12 MB・1.1 MB/s」；
 * 兩邊單位一樣時前面不寫單位；還算不出速度時只寫大小；總大小不明回 null（照舊寫百分比）
 */
export function uploadReadout(loaded, total, bps) {
  if (!Number.isFinite(total) || total <= 0 || !Number.isFinite(loaded) || loaded < 0) return null;
  const a = fmtSize(Math.min(loaded, total)), b = fmtSize(total);
  const unit = (s) => s.slice(s.lastIndexOf(' ') + 1);
  const head = unit(a) === unit(b) ? a.slice(0, a.lastIndexOf(' ')) + '／' + b : a + '／' + b;
  return Number.isFinite(bps) && bps > 0 ? head + '・' + fmtByteRate(bps) : head;
}

/** 上傳速度的平滑（每次進度回呼）：新的速度和上一次的加權平均，避免一直跳；第一次直接用 */
export function smoothRate(prev, next) {
  if (!Number.isFinite(next)) return Number.isFinite(prev) ? prev : null;
  return Number.isFinite(prev) ? prev * 0.6 + next * 0.4 : next;
}

/**
 * Codex 的免費重置次數（儀表板帳戶卡的一行，web-protocol Usage 的 resetCredits）：
 * 不知道（沒有欄位、不是非負整數）回 null；有就回 { count, expiresAt }，expiresAt 只在還有次數、最早到期的時間還沒到時才有，否則 null
 */
export function resetCreditsInfo(usage, now) {
  const count = usage ? usage.resetCredits : null;
  if (!Number.isInteger(count) || count < 0) return null;
  const at = Number(usage.resetCreditExpiresAt);
  return { count, expiresAt: count > 0 && Number.isFinite(at) && at > now ? at : null };
}

/**
 * 帳戶卡的「使用重置」（accountResetCredit）：這台已登入的 Codex 帳戶、還有次數才給；瀏覽器版、其他電腦的帳戶（remoteOnly）不給。
 * 回 null（不顯示）或 { busy }（正在使用中，按鈕停用）
 */
export function resetCreditAction(a, info, hub) {
  if (hub || !a || a.provider !== 'Codex' || a.remoteOnly || a.status !== 'ready' || !info || info.count <= 0) return null;
  return { busy: !!a.resetCreditBusy };
}

/**
 * 用量燃燒預估（儀表板的圓環）：照這個額度週期到目前的平均速度，什麼時候用完。
 * 週期至少過了一成而且 10 分鐘、用量至少 5% 才算（太早算出來的不準）；會比重置時間早用完才回 { at（用完的時間）, inMs（還有多久）, earlyMs（比重置早多久） }，
 * 其他（認不得時間窗長度、已經用完、來得及重置）回 null
 */
export function burnForecast(win, now) {
  if (!win || !Number.isFinite(now)) return null;
  const p = Number(win.percent), reset = Number(win.resetsAt);
  const span = windowSpanMs(win.key, win.label);
  if (!span || !Number.isFinite(p) || !Number.isFinite(reset) || p < 5 || p >= 100 || reset <= now) return null;
  const elapsed = now - (reset - span);
  if (elapsed < Math.max(span * 0.1, 10 * 60_000) || elapsed > span) return null;
  const inMs = (100 - p) * elapsed / p;
  const at = now + inMs;
  return at < reset ? { at, inMs, earlyMs: reset - at } : null;
}

/** 配對碼剩這麼久時倒數條轉琥珀色 */
export const PAIR_WARN_MS = 60_000;
/** 配對碼的有效期（同 Protocol 的 PairingRules.ExpiresInSeconds） */
export const PAIR_TTL_MS = 600_000;

/**
 * 配對碼的倒數條（設定頁的同步區）：宿主只給到期時間，總長取有效期（600 秒）與「這組配對碼第一次出現（startAt）到到期」較長的那個
 * （app 在配對途中重開也照真的剩餘比例畫）。回 { frac（還剩的比例 0～1）, warn（剩不到一分鐘） }；沒有到期時間或時間不合理回 null，已過期是 frac 0
 */
export function pairCountdown(expiresAt, startAt, now) {
  if (!Number.isFinite(expiresAt) || !Number.isFinite(startAt) || !Number.isFinite(now) || expiresAt <= startAt) return null;
  const left = expiresAt - now;
  return { frac: Math.max(0, Math.min(1, left / Math.max(PAIR_TTL_MS, expiresAt - startAt))), warn: left < PAIR_WARN_MS };
}

/** 裝置清單的燈：這麼久之內上線過的算剛剛還在（亮綠燈） */
export const DEVICE_RECENT_MS = 5 * 60_000;

/**
 * 設定頁裝置清單那一列前面的燈：'on'（在線）、'recent'（5 分鐘內上線過）、'off'（更早或沒有紀錄）、'revoked'（已撤銷）。
 * lastSeenAt 是伺服器的時間，比本機快一點（未來一分鐘內）也算剛剛
 */
export function deviceLed(device, now) {
  if (!device || typeof device !== 'object') return 'off';
  if (device.revoked) return 'revoked';
  if (device.online === true) return 'on';
  const seen = Number(device.lastSeenAt);
  if (Number.isFinite(seen) && Number.isFinite(now) && seen <= now + 60_000 && now - seen < DEVICE_RECENT_MS) return 'recent';
  return 'off';
}

// ───── 第八批 ─────

function planStepState(s) {
  return s && s.status === 'completed' ? 'done' : s && s.status === 'inProgress' ? 'run' : 'todo';
}

/**
 * 待辦清單標頭的分格進度（items.js 的 .plan-bar）：每一步一格，'done'（完成）、'run'（進行中）、'todo'（還沒做），照步驟的順序。
 * 超過 max 步時縮成 max 格、依比例排成完成→進行中→還沒做（有進行中的至少一格；還有沒完成的步驟時最後至少留一格空的，
 * 不會看起來已經做完）；沒有步驟回空陣列
 */
export function planCells(steps, max = 24) {
  const list = Array.isArray(steps) ? steps : [];
  const st = list.map(planStepState);
  const n = st.length;
  if (n <= max) return st;
  const done = st.filter((x) => x === 'done').length;
  const run = st.filter((x) => x === 'run').length;
  const nr = run ? Math.max(1, Math.round((max * run) / n)) : 0;
  let nd = Math.round((max * done) / n);
  if (done && !nd) nd = 1;
  if (done < n && nd + nr >= max) nd = Math.max(0, max - nr - 1);
  if (nd + nr > max) nd = max - nr;
  return [...Array(nd).fill('done'), ...Array(nr).fill('run'), ...Array(max - nd - nr).fill('todo')];
}

/**
 * 這次更新才完成的步驟（位置與文字都和上一版相同、上一版不是完成、這一版是）：那幾列閃一下綠。
 * 沒有上一版（第一次畫、重新載入）回空陣列——載入時不閃
 */
export function planNewlyDone(prev, next) {
  if (!Array.isArray(prev) || !Array.isArray(next)) return [];
  const out = [];
  next.forEach((s, i) => {
    const p = prev[i];
    if (p && s && p.text === s.text && planStepState(p) !== 'done' && planStepState(s) === 'done') out.push(i);
  });
  return out;
}

/** 指令輸出只看最後這麼多字找測試與建置的總結（總結都在結尾；好幾 MB 的輸出不整份掃） */
export const OUTCOME_TAIL = 64 * 1024;

function sumMatches(text, re, pick) {
  let found = false;
  const acc = {};
  for (const m of text.matchAll(re)) {
    found = true;
    const part = pick(m);
    for (const k of Object.keys(part)) acc[k] = (acc[k] || 0) + part[k];
  }
  return found ? acc : null;
}

function countWords(s) {
  const out = {};
  for (const m of String(s).matchAll(/(\d+)\s+(passed|failed|errors?|skipped|todo|total)\b/g)) {
    const k = m[2] === 'errors' || m[2] === 'error' ? 'failed' : m[2];
    out[k] = (out[k] || 0) + Number(m[1]);
  }
  return out;
}

/**
 * 指令輸出的測試與建置總結（第八批：工具列的結果晶片）。認得的格式：dotnet test（中英文的「失敗: 0，通過: 13，略過: 0」，
 * 多個測試組件相加）、node --test（ℹ／# pass、fail）、pytest（== 3 failed, 10 passed in 0.1s ==）、jest／vitest（Tests: 1 failed, 12 passed）、
 * cargo test（test result: ok. 12 passed; 0 failed）、dotnet／MSBuild 的建置摘要（「0 個錯誤」「3 個警告」、Error(s)／Warning(s)）。
 * 回 { tests: { pass, fail, skip } | null, build: { errors, warnings } | null }；都認不得回 null
 */
export function commandOutcome(output) {
  if (typeof output !== 'string' || !output) return null;
  const t = output.length > OUTCOME_TAIL ? output.slice(-OUTCOME_TAIL) : output;
  let tests = null;
  const dn = sumMatches(t, /(?:失敗|Failed)\s*:\s*(\d+)\s*[，,]\s*(?:通過|Passed)\s*:\s*(\d+)\s*[，,]\s*(?:略過|Skipped)\s*:\s*(\d+)/g,
    (m) => ({ fail: +m[1], pass: +m[2], skip: +m[3] }));
  if (dn) tests = dn;
  if (!tests) {
    const pass = t.match(/^(?:ℹ|#) pass (\d+)\s*$/m), fail = t.match(/^(?:ℹ|#) fail (\d+)\s*$/m);
    if (pass && fail) {
      const skip = t.match(/^(?:ℹ|#) skipped (\d+)\s*$/m);
      tests = { pass: +pass[1], fail: +fail[1], skip: skip ? +skip[1] : 0 };
    }
  }
  if (!tests) {
    const lines = [...t.matchAll(/^=+ (.*\b(?:passed|failed|errors?)\b.*) in [\d.]+s(?: \([^)]*\))? =+\s*$/gm)];
    const jest = [...t.matchAll(/^\s*Tests:?\s+(.*\b(?:passed|failed)\b.*)$/gm)];
    const src = lines.length ? lines[lines.length - 1][1] : jest.length ? jest[jest.length - 1][1] : null;
    if (src) {
      const c = countWords(src);
      tests = { pass: c.passed || 0, fail: c.failed || 0, skip: c.skipped || 0 };
    }
  }
  if (!tests) {
    const cargo = sumMatches(t, /test result: \w+\. (\d+) passed; (\d+) failed; (\d+) ignored/g, (m) => ({ pass: +m[1], fail: +m[2], skip: +m[3] }));
    if (cargo) tests = cargo;
  }
  let build = null;
  const zhErr = [...t.matchAll(/^\s*(\d+) 個錯誤\s*$/gm)], zhWarn = [...t.matchAll(/^\s*(\d+) 個警告\s*$/gm)];
  const enErr = [...t.matchAll(/^\s*(\d+) Error\(s\)\s*$/gm)], enWarn = [...t.matchAll(/^\s*(\d+) Warning\(s\)\s*$/gm)];
  const errs = zhErr.length ? zhErr : enErr, warns = zhWarn.length ? zhWarn : enWarn;
  if (errs.length) build = { errors: +errs[errs.length - 1][1], warnings: warns.length ? +warns[warns.length - 1][1] : 0 };
  return tests || build ? { tests, build } : null;
}

/**
 * 結果晶片（items.js 的 .t-outcome）：[{ kind, text, title }]。測試：通過 ✓N（有失敗時另一顆 ✕N 紅）；
 * 建置：有錯誤 ✕N 紅、有警告 !N 琥珀，都是 0 寫「建置 ✓」。沒有總結回空陣列
 */
export function outcomeChips(outcome) {
  if (!outcome) return [];
  const out = [];
  const t = outcome.tests;
  if (t) {
    out.push({ kind: t.fail ? 'pass-dim' : 'pass', text: '✓ ' + t.pass, title: '測試通過 ' + t.pass + (t.skip ? '、略過 ' + t.skip : '') });
    if (t.fail) out.push({ kind: 'fail', text: '✕ ' + t.fail, title: '測試失敗 ' + t.fail });
  }
  const b = outcome.build;
  if (b && !t) {
    if (b.errors) out.push({ kind: 'fail', text: '✕ ' + b.errors, title: '建置錯誤 ' + b.errors });
    if (b.warnings) out.push({ kind: 'warn', text: '! ' + b.warnings, title: '建置警告 ' + b.warnings });
    if (!b.errors && !b.warnings) out.push({ kind: 'pass', text: '建置 ✓', title: '建置成功，沒有錯誤與警告' });
  }
  return out;
}

/** 指令輸出最多標幾行（錯誤與警告；再多的不標，避免幾萬個 span） */
export const DIAG_MARK_MAX = 300;
/** 輸出超過這麼多字就不逐行找（好幾 MB 的輸出照原文） */
export const DIAG_SCAN_MAX = 2 * 1024 * 1024;

const DIAG_ERR = [
  /^\s*\S.*?\(\d+(?:,\d+)*\)\s*:\s*error\s+[A-Za-z]+\d+\s*:/,  // MSBuild、tsc：a.cs(3,5): error CS1002:
  /(?:^|:\s*)error\s+[A-Za-z]+\d+\s*:/,                        // error CS1002: …（沒有位置）
  /^\s*(?:error|fatal)(?:\[[A-Z]+\d+\])?:\s/i,                 // git、cargo、gcc 的 error: / fatal: / error[E0308]:
  /^\s+(?:失敗|Failed)\s+\S.*\[\s*[\d.]+\s*(?:ms|s|m)\s*\]\s*$/, // dotnet test 失敗的那一個測試
  /^\s*✖ /,                                                     // node --test 失敗的測試
  /^\s*FAIL\s+\S/,                                              // jest
];
const DIAG_WARN = [
  /\bwarning\s+[A-Za-z]+\d+\s*:/,                               // warning CS0168:
  /^\s*warning(?:\[[^\]]+\])?:\s/i,                             // warning: …
];

/**
 * 指令輸出裡的錯誤與警告行（展開的輸出左緣標紅、琥珀）：回 [{ start, end, kind: 'err'|'warn' }]（字元位置，end 不含換行），
 * 依位置排序、最多 DIAG_MARK_MAX 行；輸出太大回空陣列。同一行只算一次
 */
export function diagLineRanges(output) {
  if (typeof output !== 'string' || !output || output.length > DIAG_SCAN_MAX) return [];
  const out = [];
  let pos = 0;
  while (pos <= output.length && out.length < DIAG_MARK_MAX) {
    let nl = output.indexOf('\n', pos);
    if (nl < 0) nl = output.length;
    const end = nl > pos && output[nl - 1] === '\r' ? nl - 1 : nl;
    const line = output.slice(pos, end);
    if (line.length && line.length < 4000) {
      const kind = DIAG_ERR.some((re) => re.test(line)) ? 'err' : DIAG_WARN.some((re) => re.test(line)) ? 'warn' : null;
      if (kind) out.push({ start: pos, end, kind });
    }
    if (nl >= output.length) break;
    pos = nl + 1;
  }
  return out;
}

/** 網址的主機名稱（拿掉 www.）；不是 http(s) 網址回 null */
export function urlHost(text) {
  const m = /^\s*https?:\/\/([^/?#\s:@]+(?:@[^/?#\s:]+)?)(?::\d+)?(?:[/?#]|\s|$)/i.exec(String(text ?? ''));
  if (!m) return null;
  const host = m[1].includes('@') ? m[1].split('@').pop() : m[1];
  return host.toLowerCase().replace(/^www\./, '') || null;
}

const SEARCH_UNIT = { 搜尋檔案: '個檔案', 尋找檔案: '個檔案', 列出資料夾: '項', 搜尋內容: '行' };
const FOUND_UNIT = { file: '個檔案', files: '個檔案', line: '行', lines: '行', match: '筆', matches: '筆', occurrence: '筆', occurrences: '筆' };

function hitCount(n) {
  return n > 9999 ? '9999+' : String(n);
}

/**
 * 搜尋類與網頁工具做完時工具列的小晶片（第八批）：{ kind: 'hits'|'none'|'host', text, title }，認不得回 null。
 * 搜尋（toolKind search）：輸出開頭「Found 23 files」照它的數字與單位，「No files found」「No matches found」是 0，
 * 其他數非空白的行（工具的截斷說明不算）；網頁（toolKind web）：讀網頁寫主機名稱（標題就是網址），網路搜尋數「Links: [...]」的結果數
 */
export function toolHits(item) {
  if (!item || item.status === 'Running') return null;
  const out = typeof item.output === 'string' ? item.output : '';
  if (item.toolKind === 'search') {
    if (!out) return null;
    if (/^\s*No (?:files|matches) found\b/i.test(out)) return { kind: 'none', text: '0', title: '沒有找到' };
    const found = /^\s*Found (\d+) (files?|lines?|match(?:es)?|occurrences?)\b/im.exec(out);
    if (found) {
      const n = Number(found[1]);
      const unit = FOUND_UNIT[found[2].toLowerCase()] || '筆';
      return { kind: n ? 'hits' : 'none', text: hitCount(n) + ' ' + unit, title: '找到 ' + n + ' ' + unit };
    }
    const unit = SEARCH_UNIT[item.toolName];
    if (!unit) return null;
    const lines = out.split('\n').filter((l) => l.trim() && !/^\s*\((?:Results are truncated|結果)/i.test(l) && !/^\s*\[?(?:Showing|顯示)/i.test(l));
    return { kind: lines.length ? 'hits' : 'none', text: hitCount(lines.length) + ' ' + unit, title: '找到 ' + lines.length + ' ' + unit };
  }
  if (item.toolKind === 'web') {
    const host = urlHost(item.title);
    if (host) return { kind: 'host', text: host, title: '網站：' + host };
    const links = /^Links:\s*(\[.*\])\s*$/m.exec(out);
    if (links) {
      try {
        const list = JSON.parse(links[1]);
        if (Array.isArray(list)) {
          const hosts = [...new Set(list.map((x) => urlHost(x && x.url)).filter(Boolean))];
          return { kind: list.length ? 'hits' : 'none', text: list.length + ' 個結果', title: hosts.slice(0, 6).join('、') || '沒有結果' };
        }
      } catch { /* 不是 JSON：不顯示 */ }
    }
  }
  return null;
}

/**
 * 子代理（Agent 工具卡）的進度（第八批）：輸出是宿主接上的過程行（ClaudeStreamRules.DescribeSubagentStep：「▸ 工具：標題」「💬 文字」
 * 「💭 思考：…」「  ✗ 錯誤」），結束時開頭是「── 子代理執行紀錄（共 N 項步驟…）──」、後面接結論。
 * 回 { steps, more（輸出開頭被截掉了，步數至少這麼多）, errors, current（執行中最後一個工具「讀取檔案：a.cs」，結束了是 null） }；
 * 沒有過程行回 null
 */
export function subagentProgress(output, running) {
  if (typeof output !== 'string' || !output) return null;
  const total = /^── 子代理執行紀錄（共 (\d+) 項步驟/m.exec(output);
  let steps = 0, errors = 0, current = null;
  for (const l of output.split('\n')) {
    if (l.startsWith('── 子代理結論')) break;
    if (l.startsWith('▸ ')) { steps++; current = l.slice(2); }
    else if (l.startsWith('💬 ') || l.startsWith('💭 ')) steps++;
    else if (l.startsWith('  ✗')) { steps++; errors++; }
  }
  if (total) return { steps: Number(total[1]), more: false, errors, current: null };
  if (!steps) return null;
  return { steps, more: output[0] === '…', errors, current: running ? current : null };
}

/**
 * 每一輪的分隔線（第八批，conversation.js 的 paintTurnRules）：items 是載入的項目（依順序）。每則使用者訊息上緣寫出它前面那一段
 * （上一則使用者訊息之後到這則之前）的摘要：失敗、花了多久（那一段第一則到最後一則項目的時間）、工具次數、改了幾個檔案。
 * 回 [{ id（那則使用者訊息）, parts }]；第一則使用者訊息（前面沒有載入的那一段）與前面沒有回覆的不寫
 */
export function turnRules(items) {
  const out = [];
  let seg = null;
  for (const it of Array.isArray(items) ? items : []) {
    if (!it) continue;
    if (it.kind === 'User') {
      if (seg) {
        // 第十批：和前面那一段最後一則隔了 30 分鐘以上，摘要最前面寫「隔 2 小時」（捲回去看時分得出哪裡是隔了一陣子才接著做的）
        const parts = seg.items.length ? turnRuleParts(seg) : null;
        const gap = turnGap(seg, it.time);
        const all = gap ? [gap].concat(parts || []) : parts;
        if (all) out.push({ id: it.id, parts: all });
      }
      seg = { start: it.time, items: [] };
    } else if (seg) seg.items.push(it);
  }
  return out;
}

/** 分隔線寫出間隔的門檻（第十批） */
export const TURN_GAP_MS = 30 * 60000;

/** 間隔的說法：30～59 分鐘寫分鐘，10 小時以內寫到半小時，一天以內寫整數小時，再久寫天數；不到門檻回 null */
export function gapLabel(ms) {
  if (!(ms >= TURN_GAP_MS)) return null;
  const min = Math.floor(ms / 60000);
  if (min < 60) return '隔 ' + min + ' 分鐘';
  const hr = ms / 3600000;
  if (hr < 23.5) return '隔 ' + (hr < 10 ? Math.round(hr * 2) / 2 : Math.round(hr)) + ' 小時';
  return '隔 ' + Math.max(1, Math.round(hr / 24)) + ' 天';
}

function turnGap(seg, t) {
  let last = Number.isFinite(seg.start) ? seg.start : null;
  for (const x of seg.items) if (Number.isFinite(x.time) && (last === null || x.time > last)) last = x.time;
  if (!Number.isFinite(t) || last === null) return null;
  const text = gapLabel(t - last);
  if (!text) return null;
  const at = (ms) => { const d = new Date(ms); const p = (n) => String(n).padStart(2, '0'); return (d.getMonth() + 1) + '/' + d.getDate() + ' ' + p(d.getHours()) + ':' + p(d.getMinutes()); };
  return { k: 'gap', text, title: '前一則在 ' + at(last) + '，這則在 ' + at(t) };
}

function turnRuleParts(seg) {
  let end = null;
  for (const x of seg.items) if (Number.isFinite(x.time) && (end === null || x.time > end)) end = x.time;
  const last = seg.items[seg.items.length - 1];
  const failed = last.kind === 'Error';
  const parts = turnSummary({ state: failed ? 'err' : 'ok', startedAt: seg.start, endedAt: end, items: seg.items, tokens: null }) || [];
  // 歷史的分隔不寫「完成」（大部分都是）、不寫首次回應（只有開始與最後一則的時間，不準）；失敗照寫
  const keep = parts.filter((p) => (p.k === 'state' ? failed : p.k !== 'ttft'));
  return keep.some((p) => p.k !== 'state') || failed ? keep : null;
}

/** 滑桿已選的比例（0～100，設定頁的 .range 軌道帳戶色的那一段）；範圍不合理回 0 */
export function rangeFill(value, min, max) {
  const v = Number(value), lo = Number(min), hi = Number(max);
  if (![v, lo, hi].every(Number.isFinite) || hi <= lo) return 0;
  return Math.round(Math.max(0, Math.min(1, (v - lo) / (hi - lo))) * 1000) / 10;
}

/** 滑桿軌道的刻度格數：每一步一格，太多（超過 24）時合成最多 24 格的整數倍；不合理回 1 */
export function rangeTicks(min, max, step) {
  const lo = Number(min), hi = Number(max), st = Number(step);
  if (![lo, hi, st].every(Number.isFinite) || hi <= lo || st <= 0) return 1;
  const n = Math.round((hi - lo) / st);
  if (n <= 24) return Math.max(1, n);
  for (let k = 2; k <= n; k++) if (n % k === 0 && n / k <= 24) return n / k;
  return 24;
}

// ───── 第九批（2026-10-26）：myriagent-remote 的工具卡、@ 委派帳戶選單 ─────

/** myriagent-remote 自己的結束碼（src/MyriagentRemote 的 ExitCodes）：晶片上的短說法與滑過的說明 */
export const REMOTE_EXIT = {
  2: { short: '參數不對', long: '參數不對（myriagent-remote --help 看用法），沒有送出' },
  124: { short: '逾時', long: '超過 --timeout，那台已停止' },
  125: { short: '沒送出或被拒', long: '沒有送出或被拒（不在對話裡、沒配對、那台離線或沒開放、版本太舊…），指令沒有執行' },
  126: { short: '那台拒絕', long: '那台的人拒絕了高風險的指令（或時間內沒人同意），沒有執行' },
  130: { short: '已停止', long: '按了停止，那台已停止' },
  255: { short: '結果不明', long: '結果不明（連線中斷、那台的 app 結束、伺服器不再轉送），不知道有沒有執行完' },
};

/** 指令工具的結束碼：Claude 的指令失敗時輸出以「Exit code N」開頭；認不得回 null */
export function exitCodeOf(output) {
  const m = typeof output === 'string' ? output.match(/^(?:Error: )?Exit code (-?\d+)\b/) : null;
  return m ? Number(m[1]) : null;
}

// 指令列的字：雙引號（可跳脫）、單引號、其他連續的非空白
function shellWords(s) {
  const out = [];
  const re = /"((?:[^"\\]|\\.)*)"|'([^']*)'|(\S+)/g;
  let m;
  while ((m = re.exec(s))) out.push(m[1] ?? m[2] ?? m[3]);
  return out;
}

/**
 * 指令裡的 myriagent-remote（遠端執行指令，sync-protocol 14.11）：回 { op: 'run'|'list', device, code }，不是回 null。
 * device 取 run 後面第一個不是選項的字（--shell、--cwd、--timeout 帶一個值）；輸出裡有「[myriagent-remote] 已送到「X」」時以它為準
 * （那台的名稱）。code 是指令的結束碼（exitCodeOf；成功或認不得是 null）
 */
export function remoteExecOf(command, output) {
  const cmd = typeof command === 'string' ? command : '';
  const m = cmd.match(/(?:^|[\s;&|(`"'])(?:[^\s;&|"'`]*[\\/])?myriagent-remote(?:\.exe)?["']?[ \t]+(run|list)(?=[\s]|$)([^\r\n]*)/);
  if (!m) return null;
  const out = typeof output === 'string' ? output : '';
  const code = exitCodeOf(out);
  if (m[1] === 'list') return { op: 'list', device: null, code };
  let device = null;
  const w = shellWords(m[2]);
  for (let i = 0; i < w.length; i++) {
    const a = w[i];
    if (a === '--') break;
    if (a === '--shell' || a === '--cwd' || a === '--timeout') { i++; continue; }
    if (a.startsWith('-')) continue;
    device = a;
    break;
  }
  const sent = out.match(/\[myriagent-remote\] 已送到「([^」\r\n]+)」/);
  if (sent) device = sent[1];
  return { op: 'run', device, code };
}

/**
 * myriagent-remote 工具卡的晶片（items.js 的 .t-outcome）：「遠端：電腦名」（執行中也有），結束碼是 myriagent-remote 自己的（124、125、126、130、255、2）時
 * 另一顆寫意義（130 琥珀、其他紅）
 */
export function remoteExecChips(info) {
  if (!info) return [];
  const name = info.device || '?';
  const out = [info.op === 'list'
    ? { kind: 'remote', text: '遠端：列出電腦', title: '以 myriagent-remote 列出可以執行指令的已配對電腦' }
    : { kind: 'remote', text: '遠端：' + name, title: '以 myriagent-remote 在「' + name + '」執行（那台以一般權限執行，輸出傳回這裡）' }];
  const ex = Number.isInteger(info.code) ? REMOTE_EXIT[info.code] : null;
  if (ex) out.push({ kind: info.code === 130 ? 'warn' : 'fail', text: info.code + ' ' + ex.short, title: 'myriagent-remote 結束碼 ' + info.code + '：' + ex.long });
  return out;
}

/**
 * @ 委派帳戶選單的每一列（第九批）：上下疊的小用量刻度（同輸入框下方那一行：主要時間窗＋每週，各 5 格）與執行中的對話數。
 * 回 { meters: [{ sev, lit, tip }], running, title }
 */
export function delegateMeta(windows, running) {
  const tip = (w) => (w.label || '用量') + ' ' + Math.round(Number(w.percent) || 0) + '%';
  const meters = infoWindows(windows).map((w) => ({ sev: w.severity || 'ok', lit: meterCells(w.percent), tip: tip(w) }));
  const n = Number(running) > 0 ? Math.floor(Number(running)) : 0;
  return { meters, running: n, title: meters.map((x) => x.tip).concat(n ? [n + ' 個對話執行中'] : []).join('\n') };
}

// ───── 第十批：MCP 工具卡與帳戶委派（docs/account-delegation.md） ─────

/**
 * MCP 工具卡（toolKind mcp）的伺服器與工具：宿主把名稱寫成「MCP：伺服器」、標題是工具（Claude、Codex）；
 * Grok 的名稱是「MCP：mcp__伺服器__工具」。回 { server, tool }，不是 MCP 回 null
 */
export function mcpToolOf(item) {
  if (!item || item.toolKind !== 'mcp') return null;
  const name = String(item.toolName || '');
  const m = name.match(/^MCP：(.+)$/);
  if (!m) return null;
  const full = m[1].trim().match(/^mcp__(.+?)__(.+)$/);
  if (full) return { server: full[1], tool: full[2] };
  return { server: m[1].trim(), tool: String(item.title || '') };
}

const DELEGATION_SERVER = 'myriagent_accounts';

/** JSON 字串值（可能被截斷）的開頭：先照 JSON 解，截在跳脫中間時去掉不完整的尾巴再解，還是不行就照原文 */
function jsonStringPrefix(raw) {
  for (const s of [raw, raw.replace(/\\(u[0-9a-fA-F]{0,3})?$/, '')]) {
    try { return JSON.parse('"' + s + '"'); } catch { /* 下一個 */ }
  }
  return raw.replace(/\\n/g, '\n').replace(/\\"/g, '"');
}

/**
 * 帳戶委派的工具卡（myriagent_accounts 的 delegate_task、list_accounts）：參數（detail，可能被截斷）取帳戶 id 與工作說明的第一行，
 * 結果（output，宿主回的 JSON，result 很長時會被截斷，所以用比對而不是整份解析）取子對話 id 與完成狀態。
 * 回 { op: 'delegate'|'list', accountId, prompt, childId, outcome }，不是委派回 null
 */
export function delegationOf(item) {
  const mcp = mcpToolOf(item);
  if (!mcp || mcp.server !== DELEGATION_SERVER) return null;
  if (mcp.tool === 'list_accounts') return { op: 'list', accountId: null, prompt: '', childId: null, outcome: null };
  if (mcp.tool !== 'delegate_task') return null;
  const detail = typeof item.detail === 'string' ? item.detail : '';
  const out = typeof item.output === 'string' ? item.output : '';
  const acc = detail.match(/"accountId"\s*:\s*"([A-Za-z0-9_-]{1,100})"/);
  const pr = detail.match(/"prompt"\s*:\s*"((?:[^"\\]|\\.)*)/);
  const prompt = pr ? jsonStringPrefix(pr[1]).split(/\r?\n/).map((l) => l.trim()).find(Boolean) || '' : '';
  const child = out.match(/"conversationId"\s*:\s*"([A-Za-z0-9_-]{1,100})"/);
  const st = out.match(/"status"\s*:\s*"([A-Za-z]+)"/);
  return {
    op: 'delegate', accountId: acc ? acc[1] : null,
    prompt: prompt.length > 160 ? prompt.slice(0, 160) + '…' : prompt,
    childId: child ? child[1] : null, outcome: st ? st[1] : null,
  };
}

/**
 * 委派出去的子對話（對話清單的 parentId，桌面才有）：結果裡有 id 就用它；還在執行時以主對話＋目標帳戶找，
 * 只有一個時才算（同一個帳戶同時委派好幾件時認不出是哪一個，就不顯示）
 */
export function delegateChild(list, parentId, info) {
  if (!info || info.op !== 'delegate') return null;
  const all = Array.isArray(list) ? list : [];
  if (info.childId) return all.find((c) => c && c.id === info.childId) || null;
  if (!parentId || !info.accountId) return null;
  const mine = all.filter((c) => c && c.parentId === parentId && c.accountId === info.accountId && (c.running || c.waiting));
  return mine.length === 1 ? mine[0] : null;
}

/**
 * 委派工具卡的狀態晶片（items.js 的 .t-outcome）：結束了照結果（完成綠、已停止琥珀、未完成紅），
 * 還在執行時照子對話（等你回應琥珀、執行中帳戶色、還找不到子對話寫等待回報）
 */
export function delegationChips(info, child, toolStatus) {
  if (!info || info.op !== 'delegate') return [];
  if (toolStatus !== 'Running' && toolStatus !== 'Waiting') {
    if (info.outcome === 'Completed' && toolStatus !== 'Failed') return [{ kind: 'pass', text: '✓ 完成', title: '子對話完成了這件工作，結果已交回主對話' }];
    if (info.outcome === 'Interrupted' || toolStatus === 'Cancelled') return [{ kind: 'warn', text: '已停止', title: '委派的工作在完成前停下了；詳情看子對話' }];
    if (info.outcome || toolStatus === 'Failed') return [{ kind: 'fail', text: '✕ 未完成', title: '委派的工作失敗或沒有完成；詳情看子對話' }];
    return [];
  }
  if (child && child.waiting) return [{ kind: 'wait', text: '等你回應', title: '子對話有問卷、核准或計畫確認在等你（開啟子對話回應）' }];
  if (child && child.running) return [{ kind: 'deleg', text: '執行中', title: '子對話正在做這件工作' }];
  return [{ kind: 'deleg', text: '等待回報', title: '已交給這個帳戶，等它做完回報' }];
}

/**
 * 換帳戶提示（noticeKind accountSwitch，C# 的 AccountSwitchRules）的交接：從文字取「從哪個帳戶」換到「哪個帳戶」的名稱，
 * 提示底下畫一條舊帳戶色→新帳戶色的交接條。認得的寫法：自動改用「B」、已從「A」改用／換回「B」、改用你選的「B」；
 * 舊帳戶只寫「原帳戶」「備援帳戶」時 from 是 null。等額度重置後接著做（同一個帳戶）沒有改用、換回，回 null
 */
export function handoffLabels(text) {
  const s = typeof text === 'string' ? text : '';
  const to = s.match(/(?:改用|換回)(?:你選的)?「([^」]+)」/);
  if (!to) return null;
  const from = s.match(/已從「([^」]+)」/) || s.match(/^「([^」]+)」的額度已用完/);
  return { from: from ? from[1] : null, to: to[1] };
}

/** Codex 免費重置的次數燈（第十一批，2026-11-08）：最多畫幾顆，再多寫「+N」 */
export const CREDIT_PIPS_MAX = 8;

/** 次數燈：shown＝畫幾顆（0～CREDIT_PIPS_MAX）、more＝超過的次數（沒有是 0） */
export function creditPips(count) {
  const n = Number.isInteger(count) && count > 0 ? count : 0;
  return { shown: Math.min(n, CREDIT_PIPS_MAX), more: Math.max(0, n - CREDIT_PIPS_MAX) };
}

/** 最早到期的分格倒數有幾格（一格一天） */
export const CREDIT_EXPIRY_CELLS = 7;

/**
 * 最早到期的那次還剩幾天（第十一批：一格一天、亮的格數＝剩下的天數，超過 7 天全亮）：沒有到期時間或已過期回 null；
 * days＝剩下的天數（無條件進位）、lit＝亮幾格、soon＝剩 1 天內（轉琥珀色）
 */
export function creditExpiryCells(expiresAt, now) {
  const at = Number(expiresAt);
  if (!Number.isFinite(at) || !Number.isFinite(now) || at <= now) return null;
  const ms = at - now;
  const days = Math.ceil(ms / 86_400_000);
  return { days, lit: Math.min(CREDIT_EXPIRY_CELLS, days), soon: ms <= 86_400_000 };
}

/** 用掉了一次免費重置（次數比上次看到的少）：帳戶卡的圓環掃一圈（一次性）。上次沒看過（null）不算 */
export function creditUsed(prev, count) {
  return Number.isInteger(prev) && Number.isInteger(count) && count < prev;
}

/** 換版排空的進度最多畫幾顆（一顆一個對話；第十一批） */
export const DRAIN_PIPS_MAX = 12;

/**
 * 換版排空的進度（第十一批，2026-11-08：強制更新之後看得出還要等多少）：waiting＝宿主說還在等幾個對話到安全點、peak＝這次排空看過最多的
 * （網頁自己記，當成總數）。回 { total, left, shown（畫幾顆）}；都是 0 回 null。停下的熄掉、還在等的亮著
 */
export function drainProgress(waiting, peak) {
  const left = Number.isInteger(waiting) && waiting > 0 ? waiting : 0;
  const total = Math.max(left, Number.isInteger(peak) && peak > 0 ? peak : 0);
  if (!total) return null;
  return { total, left, shown: Math.min(total, DRAIN_PIPS_MAX) };
}

/** 強制更新：按下之後滿這麼久還沒停下的強制停下（同宿主的 AppUpdateRestartRules.ForceStopAfter） */
export const FORCE_STOP_MS = 10 * 60_000;

/** 強制停下的倒數有幾格（一格一分鐘） */
export const FORCE_CELLS = 10;

/**
 * 強制停下的倒數（第十一批）：forceAt＝宿主的 appUpdate.forceAt（到這個時間還沒停下的強制停下）。回 { lit（亮幾格＝還剩幾分鐘，無條件進位）, minutes }；
 * 沒有時間或已經到了回 null
 */
export function forceCountdown(forceAt, now) {
  const at = Number(forceAt);
  if (!Number.isFinite(at) || !Number.isFinite(now) || at <= now) return null;
  const minutes = Math.ceil((at - now) / 60_000);
  return { minutes, lit: Math.min(FORCE_CELLS, minutes) };
}

/**
 * 回覆裡的路徑與網址（第十一批，2026-11-08：行內程式碼裡很長的路徑在窄窗格撐出橫向捲動、看不出是哪個檔）：
 * 看起來像路徑或網址就回 { parts, base }——parts 是依分隔符號切好的片段（分隔符號留在前一段的結尾，畫面在它後面可以換行），
 * base 是最後一段（檔名或最後一層資料夾）在 parts 裡的位置；不像路徑回 null。
 * 認得：磁碟代號（C:\ 或 C:/）、UNC（\）、~/、./、../、/ 開頭，或「名稱/名稱」至少兩段；網址（http、https）。
 * 中間有空白的不算（指令或句子），太短（8 字以內）的不算（例如 a/b、1/2）
 */
export function pathParts(text) {
  if (typeof text !== 'string') return null;
  const t = text.trim();
  if (t.length <= 8 || t.length > 400 || /\s/.test(t) || t !== text) return null;
  const url = /^https?:\/\/[^/]+/i.test(t);
  const pathLike = url || /^(?:[A-Za-z]:[\\/]|\\\\|~[\\/]|\.{1,2}[\\/]|\/)/.test(t) || /^[\w.@-]+[\\/][\w.@-]/.test(t);
  if (!pathLike) return null;
  // 網址：協定與主機是第一段（https:// 中間不切），後面依 / 切
  const head = url ? /^https?:\/\/[^/]+\/?/i.exec(t)[0] : '';
  const parts = (head ? [head] : []).concat(t.slice(head.length).split(/(?<=[\\/])/).filter((x) => x !== ''));
  if (parts.length < 2) return null;
  // 最後一段：去掉結尾的分隔符號之後最後一個有字的片段
  let base = parts.length - 1;
  while (base > 0 && /^[\\/]+$/.test(parts[base])) base--;
  return { parts, base };
}
