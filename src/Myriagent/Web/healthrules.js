// 儀表板健康度的純規則（health.js 用；tools/ui-probe/healthrules.test.mjs 測）：等級的文字、電腦層級指示檔的狀態與涵蓋率、
// 「用代理合併」可以選的帳戶。資料形狀見 web-protocol 的 HealthView、AccountHealthView、InstructionsView

/** 等級的文字與排序（info 只是說明，不算問題） */
export const LEVELS = {
  ok: { label: '良好', rank: 0 },
  info: { label: '說明', rank: 0 },
  warn: { label: '注意', rank: 1 },
  bad: { label: '問題', rank: 2 },
};

/** 不認得的等級當 info（新版宿主加了等級時不至於顯示成良好或問題） */
export function levelOf(level) {
  return Object.prototype.hasOwnProperty.call(LEVELS, level) ? level : 'info';
}

export function levelLabel(level) {
  return LEVELS[levelOf(level)].label;
}

/** 這個帳戶的健康度（AccountHealthView）；宿主沒送、不是物件時 null */
export function accountHealthOf(health, accountId) {
  const map = health && health.accounts && typeof health.accounts === 'object' ? health.accounts : null;
  const v = map && Object.prototype.hasOwnProperty.call(map, accountId) ? map[accountId] : null;
  return v && typeof v === 'object' && Array.isArray(v.items) ? v : null;
}

/** 帳戶卡上的一行：「CLAUDE.md 偏大」；良好時「良好」，沒有摘要時用等級的文字 */
export function stripText(v) {
  if (!v) return '';
  const s = typeof v.summary === 'string' ? v.summary.trim() : '';
  return s || levelLabel(v.level);
}

/** 電腦層級指示檔要不要顯示這一區：有兩份以上，或只有一份但它有問題（太大）時 */
export function instructionsVisible(health) {
  const ins = health && health.instructions;
  if (!ins || !Array.isArray(ins.docs) || !ins.docs.length) return false;
  return ins.docs.length > 1 || ins.docs.some((d) => d.overLimit);
}

/** 一份指示檔的狀態膠囊：{ text, tone }（tone：ok／warn／bad／dim） */
export function docStatus(doc) {
  if (doc.overLimit) return { text: '超過上限', tone: 'bad' };
  switch (doc.status) {
    case 'missing': return { text: '不存在', tone: 'warn' };
    case 'empty': return { text: '空的', tone: 'warn' };
    case 'complete': return { text: '完整', tone: 'ok' };
    case 'partial': return { text: '缺 ' + (Number(doc.missingCount) || 0) + ' 條', tone: 'warn' };
    default: return { text: String(doc.status || ''), tone: 'dim' };
  }
}

/** 涵蓋率（0～100 的整數）；聯集是 0（大家都空的）算 100 */
export function coveragePct(doc) {
  const union = Number(doc.union) || 0;
  if (union <= 0) return 100;
  return Math.max(0, Math.min(100, Math.round((Number(doc.covered) || 0) * 100 / union)));
}

/** 「涵蓋 120／135 條（89%）」 */
export function coverageText(doc) {
  const union = Number(doc.union) || 0;
  return '涵蓋 ' + (Number(doc.covered) || 0) + '／' + union + ' 條（' + coveragePct(doc) + '%）';
}

/** 「用代理…」的各種工作（healthFix 的 task）：按鈕文字、選帳戶選單的標題、按鈕的說明 */
export const FIX_TASKS = {
  merge: { label: '用代理合併…', menu: '用哪個帳戶合併' },
  trim: { label: '用代理精簡…', menu: '用哪個帳戶精簡', title: '開新對話、填好精簡說明（不會自動送出）：代理先給計畫，等你確認才改檔，改之前會備份' },
  memory: { label: '用代理整理…', menu: '用哪個帳戶整理', title: '開新對話、填好整理記憶索引的說明（不會自動送出）：代理先給計畫，等你確認才改檔，改之前會備份整個 memory 資料夾' },
};

/** 明細的一項要不要放「用代理…」按鈕：全域指示看 canTrim、自動記憶索引看 canTrimMemory；回 task 名稱或 null */
export function itemFixTask(v, it) {
  if (!v || !it) return null;
  if (it.key === 'instructions' && v.canTrim === true) return 'trim';
  if (it.key === 'memory' && v.canTrimMemory === true) return 'memory';
  return null;
}

/**
 * 「用代理合併／精簡／整理」可以用的帳戶：這台的、已登入的（remoteOnly、未登入、沒安裝的不行）；
 * prefer 的帳戶排第一，其他照原本的順序
 */
export function fixCandidates(accounts, prefer) {
  const list = (accounts || []).filter((a) => a && a.remoteOnly !== true && a.status === 'ready');
  if (!prefer) return list;
  const i = list.findIndex((a) => a.id === prefer);
  if (i > 0) list.unshift(list.splice(i, 1)[0]);
  return list;
}

/** 帳戶卡「健康度」那一行的分格量表：每個檢查項目一格（照宿主的順序），等級照 levelOf 正規化；沒有項目回空陣列 */
export function healthCells(v) {
  const items = v && Array.isArray(v.items) ? v.items : [];
  return items.filter((i) => i && typeof i === 'object').map((i) => ({ key: String(i.key || ''), title: String(i.title || ''), value: String(i.value || ''), level: levelOf(i.level) }));
}
