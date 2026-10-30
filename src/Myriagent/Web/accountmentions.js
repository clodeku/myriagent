// @ 帳戶與 @ 檔案共用選單；插入穩定 id，帳戶改名或同名時仍指向同一個帳戶。
export function accountSuggestions(accounts, header, query) {
  if (!header?.accountDelegation) return [];
  const q = String(query || '').toLocaleLowerCase();
  const matches = (accounts || []).filter((a) => a.id !== header.accountId && !a.remoteOnly && a.status === 'ready'
    && /^[A-Za-z0-9_-]{1,100}$/.test(a.id)
    && [a.label, a.provider, a.email].some((s) => String(s || '').toLocaleLowerCase().includes(q)));
  matches.sort((a, b) => Number(!String(a.label).toLocaleLowerCase().startsWith(q)) - Number(!String(b.label).toLocaleLowerCase().startsWith(q)));
  return matches.slice(0, 20).map((a) => ({
    kind: 'account', value: a.id, main: a.label || a.provider, sub: a.provider + ' · AI 帳戶' + (a.email ? ' · ' + a.email : ''),
    color: a.color, provider: a.provider,
  }));
}

export function accountMention(item) {
  const label = String(item.main || 'AI 帳戶').replace(/[\]\r\n]/g, ' ').slice(0, 120);
  return '@[' + label + '](account:' + item.value + ')';
}

// ───── 已送出的訊息裡的提及（第十批）：使用者訊息是純文字，原本照原文露出 @[名稱](account:id) ─────
// 格式同 C# 的 DelegationMcpRules（名稱 1～120 字、不含 ] 與換行；id 只有英數、底線、連字號）
const MENTION_RE = /@\[([^\]\r\n]{1,120})\]\(account:([A-Za-z0-9_-]{1,100})\)/g;

/** 拆成文字與提及（{ label, id }）的片段；沒有提及回 null（呼叫端什麼都不動） */
export function mentionParts(text) {
  const s = typeof text === 'string' ? text : '';
  if (!s.includes('](account:')) return null;
  const parts = [];
  let last = 0;
  for (const m of s.matchAll(MENTION_RE)) {
    if (m.index > last) parts.push(s.slice(last, m.index));
    parts.push({ label: m[1], id: m[2] });
    last = m.index + m[0].length;
  }
  if (!parts.length) return null;
  if (last < s.length) parts.push(s.slice(last));
  return parts;
}

/** 佇列、最近的指令這類只能放純文字的地方：提及寫成「@名稱」 */
export function mentionPlain(text) {
  const s = typeof text === 'string' ? text : '';
  return s.includes('](account:') ? s.replace(MENTION_RE, (_, label) => '@' + label) : s;
}

/**
 * 晶片的內容：認得的帳戶用目前的名稱（改名後照新的）與帳戶色，說明寫代理種類與信箱；
 * 不認得的（帳戶刪了、其他電腦的帳戶）照訊息裡的名稱、不帶顏色
 */
export function mentionView(label, id, account) {
  const known = !!(account && account.id === id);
  const name = (known && account.label) || label || 'AI 帳戶';
  const title = known
    ? 'AI 帳戶：' + name + '（' + (account.provider || '') + (account.email ? '・' + account.email : '') + '）'
    : 'AI 帳戶：' + name + '（這台沒有這個帳戶）';
  return { text: '@' + name, title, known, color: known ? account.color : null, provider: known ? account.provider : null };
}
