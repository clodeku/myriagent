// 送出後先顯示在對話欄的訊息（「送出中」）的純規則：conversation.js 照它加、收掉，composer.js 照它決定要不要加。
// 按下送出到宿主回傳使用者訊息之間，宿主要檢查資料夾、存檔、插進這一輪，遠端還要繞伺服器與那台電腦一圈；這段期間先畫出來，
// 宿主的訊息到了再換成正式的；真的沒有送出（被拒絕、一直沒有回應）時留在對話欄、標出原因，可以重新送出。
// 沒有 import（tools/ui-probe/sendrules.test.mjs 以 data: URL 匯入）。

/**
 * 本機送出等多久還沒對上，就標成「不確定有沒有送出」（宿主的拒絕都會回 sendRejected；這是最後的保險）。
 * 遠端的不用它：指令追蹤（cmdtrack）150 秒等不到結果時給 unknown
 */
export const SENDING_TIMEOUT_MS = 90_000;

/** 遠端已經送到那台電腦（指令結果 done）之後，使用者訊息經同步拉回來之前最多留多久（到了就安靜收掉：確定已經送出） */
export const ACCEPTED_LINGER_MS = 300_000;

/**
 * 輸入區「繼續」鈕預設送出的那一句（2026-10-24 使用者指定的原文，不加標點；宿主 AgentHubRules.DefaultContinueText 同值）。
 * 使用者可以在設定頁改成自己的：桌面存在宿主的 continueText，瀏覽器版存在這個瀏覽器（composer.js 的 continueLocal）
 */
export const CONTINUE_TEXT = '進度如何呢如果還沒做完就繼續';

/** 自訂的「繼續」指令最多幾個字元（UTF-16；宿主 AgentHubRules.ContinueTextMaxLength 同值） */
export const CONTINUE_MAX = 2000;

/** 說明裡引用那一句時最多幾個字（多的換成「…」） */
const CONTINUE_PREVIEW_CHARS = 40;

/** 實際要送的那一句：設定的字去掉頭尾空白，空的（或不是字串）用預設；超過上限的截掉（不切開代理字元對） */
export function continueTextOf(value) {
  let t = typeof value === 'string' ? value.trim() : '';
  if (t.length > CONTINUE_MAX) {
    let cut = CONTINUE_MAX;
    const c = t.charCodeAt(cut - 1);
    if (c >= 0xd800 && c <= 0xdbff) cut--;
    t = t.slice(0, cut).trimEnd();
  }
  return t || CONTINUE_TEXT;
}

/** 說明裡引用的那一句：換行與連續空白併成一個空白，超過 40 個字截斷加「…」 */
export function continuePreview(text) {
  const chars = Array.from(continueTextOf(text).replace(/\s+/g, ' '));
  return chars.length > CONTINUE_PREVIEW_CHARS ? chars.slice(0, CONTINUE_PREVIEW_CHARS).join('') + '…' : chars.join('');
}

/**
 * 「繼續」鈕的說明：和送出鈕一樣，執行中能插入的插進這一輪、不能插入的排進佇列（送出的就是一般的 send）；
 * text 是設定的那一句（沒有就是預設）
 * @param {{ running?: boolean, steers?: boolean, text?: string }} o
 */
export function continueTitle(o = {}) {
  const say = '「' + continuePreview(o.text) + '」';
  if (o.running && o.steers) return '繼續：把' + say + '插入這一輪';
  if (o.running) return '繼續：把' + say + '加入佇列（這一輪完成後送出）';
  return '繼續：送出' + say;
}

/** 遠端指令被拒的原因（commandResult 的 code） */
const CODE_TEXT = {
  not_sent: '沒有連上伺服器', rate_limited: '送得太頻繁', offline: '那台電腦離線', policy: '那台電腦的設定不允許', busy: '那台電腦忙碌',
  conflict: '那台電腦沒有接受', invalid: '內容不合格', unsupported: '那台電腦的版本不支援', expired: '已過期', step_up_required: '要重新輸入密碼',
  not_owner: '不是那台電腦的對話', stale: '對話已經變了', not_available: '伺服器不支援',
};

/**
 * 送不出去的那則怎麼標：kind 是 'failed'（確定沒有送出）或 'unknown'（不確定有沒有送出），label 是泡泡下方的說明，
 * specific 是有沒有具體原因（之後才到、沒有原因的通知不蓋掉有原因的）
 * @param {{ reason?: string, state?: 'rejected'|'unknown'|'timeout', code?: string }} o
 */
export function failureOf(o = {}) {
  const reason = typeof o.reason === 'string' ? o.reason.trim() : '';
  if (reason) return { kind: 'failed', label: '沒有送出：' + reason, specific: true };
  if (o.state === 'timeout') return { kind: 'unknown', label: '等了 ' + Math.round(SENDING_TIMEOUT_MS / 1000) + ' 秒都沒有回應，可能沒有送出', specific: true };
  if (o.state === 'unknown') return { kind: 'unknown', label: '不確定有沒有送出：一直沒有收到結果，請看對話內容確認', specific: true };
  const why = typeof o.code === 'string' && Object.prototype.hasOwnProperty.call(CODE_TEXT, o.code) ? CODE_TEXT[o.code] : '';
  return { kind: 'failed', label: why ? '沒有送出：' + why : '沒有送出', specific: !!why };
}

/**
 * 新到的失敗通知要不要換掉這則目前的標示（current 是 null＝還在送）：確定沒有送出蓋過不確定、有原因的蓋過沒有原因的；
 * 不確定的不蓋掉已經確定的（被拒絕的 sendRejected 與指令結果可能先後都到）
 */
export function failureUpdates(current, next) {
  if (!next) return false;
  if (!current) return true;
  if (current.kind !== next.kind) return next.kind === 'failed';
  return next.specific && !current.specific;
}

/**
 * 這次送出要不要先顯示在對話欄。不顯示：沒有內容（空白送出＝從佇列接著送）、/clear（宿主自己清，不會有使用者訊息）、
 * /btw（旁支提問：宿主加一則旁支提問的提示，不是使用者訊息）、
 * 執行中而且這則會排進佇列（不能插入的代理；其他電腦的對話與瀏覽器版的插入由那台決定、佇列不標「正在插入」，先進佇列再出現在對話欄）
 * @param {{ text?: string, files?: number, running?: boolean, steers?: boolean, remote?: boolean }} o
 */
export function showsSending(o) {
  const text = typeof o.text === 'string' ? o.text.trim() : '';
  const files = Number.isFinite(o.files) && o.files > 0 ? o.files : 0;
  if (!text && !files) return false;
  if (text === '/clear') return false;
  if (/^\/btw(\s|$)/.test(text)) return false;
  if (o.running && (o.remote || !o.steers)) return false;
  return true;
}

const NAMED = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: ' ' };

/** 使用者訊息的 html 還原成文字（宿主 PlainToHtml＝HtmlEncode＋換行轉 <br>；瀏覽器版是伺服器淨化過的同一份）。只用來比對 */
export function htmlText(html) {
  if (typeof html !== 'string' || !html) return '';
  return html
    .replace(/<br\s*\/?>/gi, '\n')
    .replace(/<[^>]*>/g, '')
    .replace(/&(#x[0-9a-f]+|#\d+|[a-z]+);/gi, (m, e) => {
      if (e[0] === '#') {
        const cp = e[1] === 'x' || e[1] === 'X' ? parseInt(e.slice(2), 16) : parseInt(e.slice(1), 10);
        return Number.isFinite(cp) && cp >= 0 && cp <= 0x10ffff ? String.fromCodePoint(cp) : m;
      }
      const v = NAMED[e.toLowerCase()];
      return v === undefined ? m : v;
    });
}

/** 比對的鍵：去掉所有空白（宿主會去頭尾空白、換行轉成 <br>，兩邊的空白不必一樣） */
export function sendKey(text) {
  return String(text || '').replace(/\s+/g, '');
}

/** 佇列文字的寫法（宿主 RenderComposer）：有附件時後面加「（附 N 個檔案）」 */
export function queuedText(text, files) {
  const t = String(text || '').trim();
  return files > 0 ? (t + '（附 ' + files + ' 個檔案）').trim() : t;
}

/**
 * 新出現的使用者訊息對上哪一則送出中的：同樣的文字、而且比送出當時畫面上最新的項目新（seq 較大；沒有 seq 的不比），
 * 免得之前送過同樣文字的舊訊息（例如重新載入整個對話時）把它收掉。回傳 pending 的索引（最早的那則），沒有是 -1
 * @param {{ key: string, afterSeq: number|null }[]} pending
 */
export function matchSent(pending, item) {
  if (!item || item.kind !== 'User' || !Array.isArray(pending) || !pending.length) return -1;
  const key = sendKey(htmlText(item.html));
  return pending.findIndex((p) => p.key === key && (typeof item.seq !== 'number' || typeof p.afterSeq !== 'number' || item.seq > p.afterSeq));
}

/**
 * 宿主的佇列（composer.queue）裡已經有的送出中訊息：排進佇列了（不能插入、插不進去、CLI 更新或換版中延後），由佇列顯示，對話欄收掉。
 * 正在插入的那則（composer.steering）不算：插進去就會變成使用者訊息。瀏覽器版的佇列文字可能被截短，夠長時比開頭
 * @param {{ key: string, text: string, files: number }[]} pending
 * @param {{ id: string, text: string }[]} queue
 * @returns {number[]} pending 的索引
 */
export function queuedSent(pending, queue, steering) {
  if (!Array.isArray(pending) || !pending.length || !Array.isArray(queue) || !queue.length) return [];
  const keys = [];
  for (const q of queue) {
    if (!q || (steering && q.id === steering)) continue;
    keys.push(sendKey(String(q.text || '').replace(/…$/, '')));
  }
  const out = [];
  const used = new Set();
  pending.forEach((p, i) => {
    const full = sendKey(queuedText(p.text, p.files));
    const k = keys.findIndex((q, j) => !used.has(j) && (q === full || q === p.key || (q.length >= 100 && full.startsWith(q))));
    if (k >= 0) { used.add(k); out.push(i); }
  });
  return out;
}

/**
 * 某個結果（sendRejected、遠端指令的結果）是哪一則：同樣文字的最新一則（只附檔的是沒有文字的那則）。回傳索引或 -1。
 * 也用在重新送出：同樣文字已經標成沒有送出的那幾則收掉
 */
export function rejectedSent(pending, text) {
  if (!Array.isArray(pending) || !pending.length) return -1;
  const key = sendKey(text);
  for (let i = pending.length - 1; i >= 0; i--) if (pending[i].key === key) return i;
  return -1;
}

/**
 * 輸入框按 Enter 要不要送出（composer.js 的 onKeyDown；不送出就照預設換行）。
 * 手機、平板（觸控操作，voicemsgrules.isHandheld）的 Enter 一律換行，用送出鈕送出（2026-10-29 使用者要求：螢幕鍵盤沒有 Shift+Enter
 * 可以換行）；接了實體鍵盤時 Ctrl／⌘＋Enter 照樣送出。其他依設定 enterToSend：開著是 Enter 送出、Shift+Enter 換行，
 * 關掉是 Ctrl+Enter 送出。Alt+Enter 一律不送
 */
export function enterSends({ shiftKey, ctrlKey, metaKey, altKey, enterToSend, handheld } = {}) {
  if (altKey || shiftKey) return false;
  if (handheld) return !!(ctrlKey || metaKey);
  return enterToSend !== false || !!ctrlKey;
}
