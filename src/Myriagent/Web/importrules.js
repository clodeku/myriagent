// 匯入本機外部 CLI 歷史對話的純函式規則（篩選、關鍵字比對、資料夾簡稱與計數摘要）。
// 沒有 import、不碰 DOM、不讀時鐘：可直接匯入由單元測試驗證。

const isObj = (v) => !!v && typeof v === 'object' && !Array.isArray(v);
const str = (v) => (typeof v === 'string' && v.trim() ? v.trim() : '');

/** 支援篩選的代理種類順序 */
export const IMPORT_PROVIDERS = [
  { key: 'all', label: '全部' },
  { key: 'Claude', label: 'Claude' },
  { key: 'Codex', label: 'Codex' },
  { key: 'Antigravity', label: 'Antigravity' },
  { key: 'Grok', label: 'Grok' },
];

/** 正規化宿主傳來的外部對話項目清單 */
export function normalizeExternalItems(rawItems) {
  if (!Array.isArray(rawItems)) return [];
  const out = [];
  for (const it of rawItems) {
    if (!isObj(it)) continue;
    const sourceKey = str(it.sourceKey);
    const provider = str(it.provider);
    const sessionId = str(it.sessionId);
    if (!sourceKey || !provider || !sessionId) continue;
    out.push({
      sourceKey,
      provider,
      accountId: str(it.accountId),
      accountLabel: str(it.accountLabel) || provider,
      sessionId,
      title: str(it.title) || '未命名對話',
      workingDirectory: str(it.workingDirectory) || null,
      updatedAt: typeof it.updatedAt === 'number' && Number.isFinite(it.updatedAt) ? it.updatedAt : null,
      messageCount: typeof it.messageCount === 'number' && it.messageCount > 0 ? Math.trunc(it.messageCount) : 1,
    });
  }
  return out;
}

/** 統計各代理種類在完整清單中的筆數 */
export function countExternalByProvider(items) {
  const counts = { all: 0, Claude: 0, Codex: 0, Antigravity: 0, Grok: 0 };
  if (!Array.isArray(items)) return counts;
  for (const it of items) {
    if (!isObj(it)) continue;
    counts.all++;
    if (Object.prototype.hasOwnProperty.call(counts, it.provider)) {
      counts[it.provider]++;
    }
  }
  return counts;
}

/** 依代理種類與關鍵字（空白分隔多詞 AND 比對標題、資料夾、帳戶、SessionId）篩選外部對話 */
export function filterExternalItems(items, providerFilter, query) {
  const list = Array.isArray(items) ? items : [];
  const pf = str(providerFilter);
  const terms = str(query).toLowerCase().split(/\s+/).filter(Boolean);

  return list.filter((it) => {
    if (!isObj(it)) return false;
    if (pf && pf !== 'all' && it.provider !== pf) return false;
    if (!terms.length) return true;
    const hay = [
      it.title || '',
      it.workingDirectory || '',
      it.accountLabel || '',
      it.provider || '',
      it.sessionId || '',
    ].join('\n').toLowerCase();
    return terms.every((t) => hay.includes(t));
  });
}

/** 工作資料夾的顯示名稱（取最後兩層路徑讓同名專案也分得出來；沒有時回「預設工作區」） */
export function formatExternalFolder(workingDirectory) {
  const p = str(workingDirectory);
  if (!p) return '預設工作區';
  const parts = p.replace(/\//g, '\\').replace(/\\+$/, '').split('\\').filter(Boolean);
  if (parts.length <= 2) return parts.join('\\') || p;
  return parts.slice(-2).join('\\');
}
