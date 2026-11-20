// 輸入區的輸入紀錄（↑↓ 帶入最近送出的訊息）：純函式，沒有 import（tools/ui-probe/rules.test.mjs 以 data: URL 匯入測試）
// 紀錄是每個對話各自一份（在哪個對話送出就記在哪個對話），只存在網頁的 localStorage（composer.js 讀寫；讀寫失敗一律當成沒有），
// 不經宿主、不進同步。↑↓ 翻的是它和對話本身已載入的使用者訊息合併的結果（mergeRecall）：
// 只靠 localStorage 時，別的電腦、手機、瀏覽器版、從 claude.ai 送的訊息翻不到，儲存被清掉或寫入失敗就沒了

/** 每個對話最多記幾則 */
export const HISTORY_LIMIT = 50;
/** localStorage 的鍵：[{ id: 對話 id, list: 紀錄（最新的在前） }]，最近送出過的對話在前 */
export const HISTORY_KEY = 'myriagent.app.inputHistory.v2';
/** 舊版所有對話共用的一份：分不出是哪個對話送的，不搬；寫入新的一份時刪掉 */
export const LEGACY_HISTORY_KEY = 'myriagent.app.inputHistory.v1';
/** 太長的（貼上整份記錄檔之類）不記 */
export const HISTORY_MAX_CHARS = 100_000;
/** 最多記幾個對話（最久沒在裡面送出的先丟） */
export const HISTORY_CONVERSATIONS = 200;
/** 所有對話的紀錄加起來最多幾個字，不把 localStorage 的額度用光：超過時從最久沒在裡面送出的對話丟起 */
export const HISTORY_TOTAL_CHARS = 2_000_000;

const usable = (t) => typeof t === 'string' && t.trim().length > 0 && t.length <= HISTORY_MAX_CHARS;
const validId = (id) => typeof id === 'string' && id.length > 0 && id.length <= 200;

/** 清過的一份紀錄：不是字串、空白、太長、重複的略過，最多 HISTORY_LIMIT 則 */
function cleanList(list) {
  const out = [];
  if (!Array.isArray(list)) return out;
  for (const t of list) {
    if (!usable(t) || out.includes(t)) continue;
    out.push(t);
    if (out.length >= HISTORY_LIMIT) break;
  }
  return out;
}

/**
 * 依上限修剪：最多 HISTORY_CONVERSATIONS 個對話，字數從最前面的對話、每個對話最新的一則算起，
 * 加起來放不下的那則連同後面的都丟（最前面的對話放不下全部 50 則時，丟的是它最舊的幾則：只有一則一則都貼了幾萬字的長文才會發生）
 */
function capStore(entries) {
  const out = [];
  let total = 0;
  for (const e of entries) {
    if (out.length >= HISTORY_CONVERSATIONS) break;
    const list = [];
    let full = false;
    for (const t of e.list) {
      if (total + t.length > HISTORY_TOTAL_CHARS) { full = true; break; }
      total += t.length;
      list.push(t);
    }
    if (list.length) out.push({ id: e.id, list });
    if (full) break;
  }
  return out;
}

/** 讀回 localStorage 存的字串：不是 JSON 陣列回 []；id 不對、重複的對話與沒有紀錄的略過，每個對話的紀錄照 cleanList 清過、再依上限修剪 */
export function parseStore(raw) {
  let data;
  try {
    data = typeof raw === 'string' && raw ? JSON.parse(raw) : null;
  } catch {
    data = null;
  }
  if (!Array.isArray(data)) return [];
  const seen = new Set();
  const entries = [];
  for (const e of data) {
    if (!e || typeof e !== 'object' || !validId(e.id) || seen.has(e.id)) continue;
    seen.add(e.id);
    const list = cleanList(e.list);
    if (list.length) entries.push({ id: e.id, list });
  }
  return capStore(entries);
}

/** 這個對話的紀錄（最新的在前；沒有是 []） */
export function historyOf(store, id) {
  const e = Array.isArray(store) && validId(id) ? store.find((x) => x && x.id === id) : null;
  return e ? e.list : [];
}

/** 一份紀錄送出一則之後：放到最前面、拿掉同樣內容的舊的、最多 HISTORY_LIMIT 則；空白或太長的不記（原樣回傳） */
export function pushHistory(list, text) {
  const prev = Array.isArray(list) ? list : [];
  if (!usable(text)) return prev;
  return [text, ...prev.filter((t) => t !== text)].slice(0, HISTORY_LIMIT);
}

/** 在這個對話送出一則之後：這個對話的紀錄照 pushHistory 更新、移到最前面，再依上限修剪；空白、太長或 id 不對時原樣回傳 */
export function pushStore(store, id, text) {
  const prev = Array.isArray(store) ? store : [];
  if (!validId(id) || !usable(text)) return prev;
  const list = pushHistory(historyOf(prev, id), text);
  return capStore([{ id, list }, ...prev.filter((x) => x && x.id !== id)]);
}

/**
 * 〔遠端〕草稿換成正式對話（conversationClosed 的 replacedBy）：草稿送出的紀錄併到新的 id
 * （新的 id 原本有的在前、同樣內容只留一則、最多 HISTORY_LIMIT 則）並移到最前面；草稿沒有紀錄時原樣回傳
 */
export function moveStore(store, from, to) {
  const prev = Array.isArray(store) ? store : [];
  if (!validId(from) || !validId(to) || from === to) return prev;
  const moving = historyOf(prev, from);
  if (!moving.length) return prev;
  const list = cleanList([...historyOf(prev, to), ...moving]);
  return capStore([{ id: to, list }, ...prev.filter((x) => x && x.id !== from && x.id !== to)]);
}

/** ↑ 最多翻幾則（這個瀏覽器記的與對話裡的使用者訊息合併後） */
export const RECALL_LIMIT = 50;

/** 比對用：換行統一成 \n、去掉結尾空白（textarea 會把 CRLF 換成 LF；對話項目的文字是從 HTML 取回的，結尾的空白可能不同） */
const recallKey = (t) => t.replace(/\r\n?/g, '\n').replace(/\s+$/, '');

/**
 * ↑↓ 要翻的紀錄（最新的在前）：local 是這個瀏覽器在這個對話記的（historyOf，最新的在前、文字是當時輸入框裡的原樣），
 * sent 是對話本身已載入的使用者訊息（最新的在前；含別的電腦、手機、瀏覽器版、claude.ai 送的）。
 * - 對話裡的訊息是骨幹、照它的順序；同一則兩邊都有時用 local 的原文。
 * - local 有、對話裡沒有的：排在兩邊都有的第一則之前的，是比對話裡最新的那則還新的（排隊中、剛送出還沒回來）——放最前面；
 *   之後的（在已載入的那一頁之前送的）接在最後。兩邊沒有共同的一則時，local 的全部放最前面（多半是剛送出、還在排隊的）。
 * - 空白、太長、重複（照 recallKey 比）的略過，最多 limit 則
 */
export function mergeRecall(local, sent, limit = RECALL_LIMIT) {
  const own = (Array.isArray(local) ? local : []).filter(usable);
  const chat = (Array.isArray(sent) ? sent : []).filter(usable);
  const exact = new Map();
  for (const t of own) if (!exact.has(recallKey(t))) exact.set(recallKey(t), t);
  const chatKeys = new Set(chat.map(recallKey));
  let firstCommon = own.findIndex((t) => chatKeys.has(recallKey(t)));
  if (firstCommon < 0) firstCommon = own.length;
  const ordered = [
    ...own.slice(0, firstCommon),
    ...chat.map((t) => exact.get(recallKey(t)) ?? t),
    ...own.slice(firstCommon).filter((t) => !chatKeys.has(recallKey(t))),
  ];
  const out = [];
  const seen = new Set();
  for (const t of ordered) {
    const k = recallKey(t);
    if (seen.has(k)) continue;
    seen.add(k);
    out.push(t);
    if (out.length >= limit) break;
  }
  return out;
}

/** 沒有在翻紀錄的狀態（每個輸入區一份） */
export function idleNav() {
  return { index: -1, draft: '', shown: null, list: [] };
}

/**
 * 在輸入框按 ↑（dir 'up'）或 ↓（'down'）：要換成紀錄的哪一則。回 null＝不處理（照瀏覽器預設移動游標），
 * 否則回 { nav, value }（輸入框換成 value、游標放到最後）。
 * - ↑：游標在最前面（含輸入框是空的），或正在看一則沒改過的紀錄而且游標還在最後（剛帶入時的位置）時，換成更早的一則；
 *   從頭開始翻時記下目前的文字（draft）、略過和目前文字相同的紀錄，翻的期間用開始時的那份紀錄（別的視窗送出的不插進來）。
 * - ↓：正在看一則沒改過的紀錄而且游標在最後時，換成較新的一則；已經是最新的一則就還原開始翻之前的文字。
 * - 有選取範圍時不處理；改過帶入的文字之後，改過的文字就是新的 draft（再按 ↑ 從最新的一則重新開始翻）。
 */
export function historyMove(nav, dir, value, selStart, selEnd, stored) {
  if (typeof value !== 'string' || selStart !== selEnd) return null;
  const n = nav && typeof nav === 'object' ? nav : idleNav();
  const browsing = n.index >= 0 && Array.isArray(n.list) && value === n.shown;
  const atEnd = selStart === value.length;
  if (dir === 'up') {
    if (browsing && (atEnd || selStart === 0)) {
      for (let i = n.index + 1; i < n.list.length; i++) {
        if (n.list[i] !== n.draft) return { nav: { index: i, draft: n.draft, shown: n.list[i], list: n.list }, value: n.list[i] };
      }
      return null;
    }
    if (selStart !== 0) return null;
    const list = Array.isArray(stored) ? stored : [];
    const i = list.findIndex((t) => t !== value);
    if (i < 0) return null;
    return { nav: { index: i, draft: value, shown: list[i], list }, value: list[i] };
  }
  if (dir === 'down') {
    if (!browsing || !atEnd) return null;
    for (let i = n.index - 1; i >= 0; i--) {
      if (n.list[i] !== n.draft) return { nav: { index: i, draft: n.draft, shown: n.list[i], list: n.list }, value: n.list[i] };
    }
    return { nav: idleNav(), value: n.draft };
  }
  return null;
}
