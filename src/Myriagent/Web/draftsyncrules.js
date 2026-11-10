// 輸入框草稿的同步（2026-11-10 使用者要求：在某台電腦或手機打到一半沒送出的字，其他電腦與瀏覽器版也看得到；web-protocol 的
// composerDraft／composerDrafts、sync-protocol 14.17）的純規則：什麼時候送、收到別處的要換掉輸入框、提示還是不理。
// 使用者選的做法：這一邊沒改過就換成新的，正在打的（改過、或剛打過字還在輸入框裡）不蓋掉，改成在輸入框上方提示。

/** 打字停下來多久才送（毫秒） */
export const DRAFT_SYNC_DELAY_MS = 700;

/** 剛打過字多久之內（輸入框還有焦點）算正在打：別處的不直接蓋掉（毫秒） */
export const DRAFT_EDIT_GRACE_MS = 4000;

/** 一則草稿的上限（字元；同 C# 的 ComposerDraftRules.MaxTextChars）：超過的不同步，輸入框照樣保留 */
export const DRAFT_MAX_CHARS = 16000;

/** 空白（含只有空白字元）算清掉 */
export function isBlankDraft(text) {
  return typeof text !== 'string' || text.trim() === '';
}

/** 要不要把輸入框目前的字送出去：和上次送的不同、沒超過上限 */
export function shouldPublish(text, lastPosted) {
  if (typeof text !== 'string' || text.length > DRAFT_MAX_CHARS) return false;
  const now = isBlankDraft(text) ? '' : text;
  const before = isBlankDraft(lastPosted) ? '' : lastPosted;
  return now !== before;
}

/**
 * 收到別處的草稿要怎麼辦：
 * same＝輸入框已經是這些字（只要記成已同步）；ignore＝不理；apply＝換掉輸入框；offer＝不蓋掉，在輸入框上方提示。
 * current：輸入框目前的字；synced：上次和別處一致的字（送出或換上的）；remote：別處的字（空字串＝那邊清掉了，例如已經送出）；
 * focused、lastInputAt、now：輸入框有沒有焦點、上次打字的時間
 */
export function remoteDraftAction({ current, synced, remote, focused, lastInputAt, now }) {
  const cur = isBlankDraft(current) ? '' : current;
  const base = isBlankDraft(synced) ? '' : synced;
  const rem = isBlankDraft(remote) ? '' : remote;
  if (rem === cur) return 'same';
  if (rem === base) return 'ignore';
  const typing = !!focused && typeof lastInputAt === 'number' && now - lastInputAt < DRAFT_EDIT_GRACE_MS;
  const edited = cur !== base || typing;
  if (!edited) return 'apply';
  // 這一邊正在打、那邊清掉了（多半是在那邊送出了）：留著這裡的字
  if (rem === '') return 'ignore';
  return 'offer';
}

/**
 * composerDrafts 訊息套進記得的草稿（Map：對話 id → {text, rev, from, at}）：回傳變了的那幾則 {conversationId, text, from, rev}。
 * rev 是伺服器給的（越大越新）；0 是這台的另一個視窗剛打的（桌面），一律收。整份的（full）沒列出、而且是伺服器給過的，當成清掉了
 * （這一邊自己送出、伺服器還沒回過的不在 Map 裡，不會因為整份的沒列出就被清掉）
 */
export function mergeDrafts(map, msg) {
  const changed = [];
  if (!(map instanceof Map) || !msg || !Array.isArray(msg.drafts)) return changed;
  const seen = new Set();
  for (const d of msg.drafts) {
    if (!d || typeof d.conversationId !== 'string' || typeof d.text !== 'string') continue;
    const id = d.conversationId;
    seen.add(id);
    const rev = typeof d.rev === 'number' && d.rev > 0 ? d.rev : 0;
    const known = map.get(id);
    if (known && rev > 0 && rev < known.rev) continue;
    const text = isBlankDraft(d.text) ? '' : d.text;
    if (known && known.text === text && known.rev === rev) continue;
    if (text === '') {
      if (!known) continue;
      map.delete(id);
    } else {
      map.set(id, { text, rev, from: typeof d.from === 'string' ? d.from : null, at: typeof d.at === 'number' ? d.at : 0 });
    }
    changed.push({ conversationId: id, text, from: typeof d.from === 'string' ? d.from : null, rev });
  }
  if (msg.full === true) {
    for (const [id, known] of [...map]) {
      if (seen.has(id) || !(known.rev > 0)) continue;
      map.delete(id);
      changed.push({ conversationId: id, text: '', from: known.from, rev: known.rev });
    }
  }
  return changed;
}

/** 提示裡的那段字：第一行、去頭尾空白、最多 max 字（超過加…） */
export function draftPreview(text, max = 48) {
  if (typeof text !== 'string') return '';
  const line = text.trim().split(/\r?\n/)[0].trim();
  return line.length > max ? line.slice(0, max) + '…' : line;
}

/** 提示的開頭：別處的名字（電腦名稱、「網頁版」），沒有是這台的另一個視窗 */
export function draftSourceLabel(from) {
  return typeof from === 'string' && from.trim() ? from.trim() : '這台的另一個視窗';
}
