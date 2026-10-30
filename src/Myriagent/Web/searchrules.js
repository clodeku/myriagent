// 側欄對話搜尋的純規則（標題與內文；web-protocol 的 searchConversations／conversationSearch）：沒有 import、不碰 DOM，
// 以 tools/ui-probe/searchrules.test.mjs 測試。關鍵字的切法與宿主的 ConversationSearchRules.Terms 相同：
// 整串先截到 200 字、以空白分開、不分大小寫去掉重複（留第一次的寫法）、最多 8 個；每個關鍵字都要出現在標題或內文。

export const MAX_QUERY = 200;
export const MAX_TERMS = 8;

/** 查詢 → 關鍵字（沒有要搜尋的回空陣列） */
export function searchTerms(query) {
  if (typeof query !== 'string') return [];
  const terms = [];
  const seen = new Set();
  for (const part of query.slice(0, MAX_QUERY).split(/\s+/)) {
    if (!part) continue;
    const key = part.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    terms.push(part);
    if (terms.length === MAX_TERMS) break;
  }
  return terms;
}

/** 比對同一個搜尋用的鍵（關鍵字不分大小寫、照順序，以空格接起來：關鍵字本身沒有空白）：鍵一樣的查詢，宿主的結果可以沿用；沒有關鍵字是空字串 */
export function searchKey(terms) {
  return Array.isArray(terms) ? terms.map((t) => t.toLowerCase()).join(' ') : '';
}

/** 標題含每一個關鍵字（不分大小寫）；沒有關鍵字一律 true */
export function titleMatches(title, terms) {
  if (!Array.isArray(terms) || terms.length === 0) return true;
  const t = (typeof title === 'string' ? title : '').toLowerCase();
  return terms.every((term) => t.includes(term.toLowerCase()));
}

/**
 * 宿主回的 conversationSearch → Map(對話 id → 片段或 null)。形狀不對的項目略過；hits 不是陣列時回空的 Map
 */
export function hitsFrom(message) {
  const map = new Map();
  const hits = message && Array.isArray(message.hits) ? message.hits : [];
  for (const hit of hits) {
    if (!hit || typeof hit.conversationId !== 'string' || !hit.conversationId) continue;
    map.set(hit.conversationId, typeof hit.snippet === 'string' && hit.snippet ? hit.snippet : null);
  }
  return map;
}

/**
 * 清單要列的對話：沒有關鍵字時全部；有的話標題符合或宿主說內文符合（hits 有這個 id）。順序照原清單。
 * hits 是 null＝內文的結果還沒來（只比標題）
 */
export function filterConversations(list, terms, hits) {
  if (!Array.isArray(list)) return [];
  if (!Array.isArray(terms) || terms.length === 0) return list;
  return list.filter((c) => titleMatches(c.title, terms) || (hits instanceof Map && hits.has(c.id)));
}

/**
 * 片段裡要標示的段落：[{ text, mark }]，依序接起來就是原文。每個位置挑最長的符合（不分大小寫），符合處不重疊
 */
export function markParts(text, terms) {
  const s = typeof text === 'string' ? text : '';
  const list = (Array.isArray(terms) ? terms : []).map((t) => t.toLowerCase()).filter(Boolean).sort((a, b) => b.length - a.length);
  if (!s || list.length === 0) return s ? [{ text: s, mark: false }] : [];
  const lower = s.toLowerCase();
  // toLowerCase 改變長度的字（極少數）會讓位置對不上：那時不標示
  if (lower.length !== s.length) return [{ text: s, mark: false }];
  const parts = [];
  let plain = '';
  let i = 0;
  while (i < s.length) {
    const term = list.find((t) => lower.startsWith(t, i));
    if (term) {
      if (plain) { parts.push({ text: plain, mark: false }); plain = ''; }
      parts.push({ text: s.slice(i, i + term.length), mark: true });
      i += term.length;
    } else {
      plain += s[i];
      i++;
    }
  }
  if (plain) parts.push({ text: plain, mark: false });
  return parts;
}
