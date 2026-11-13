// 設定對話框的純規則（settings.js 用；沒有 import、沒有 DOM，tools/ui-probe 的 settingsrules.test.mjs 測）：
// 搜尋設定（拆詞、比對與排序、說明的節錄、標出命中的字）、各設定鍵另外搜得到的說法、分頁列的方向鍵

/**
 * 各設定鍵（設定列的 data-setting）另外搜得到的字：標籤與說明裡沒有、但使用者可能這樣找的說法（英文、口語、別名）。
 * 只用來比對，不顯示
 */
export const SETTING_KEYWORDS = Object.freeze({
  theme: '深色 淺色 暗色 亮色 夜間 dark light 顏色 外觀',
  fontSize: '字體 字型 文字大小 放大 縮小 font',
  visualEffects: '動畫 特效 效能 耗電 cpu gpu',
  autoStart: '開機 啟動 登入 windows 自動登入',
  autoStartLock: '鎖定 自動登入 開機 啟動 windows',
  closeToTray: '系統匣 背景 最小化 關閉視窗 tray',
  notifications: '通知 提醒 toast',
  voiceNotifications: '語音 朗讀 念出 tts 聲音 雅婷 語速 音調',
  voiceLocal: '語音 朗讀 念出 tts 聲音 語速 音調',
  scheduleAutoDiagnose: '排程 工作排程器 schtasks 失敗 診斷 自動',
  enterToSend: 'enter ctrl 換行 快捷鍵 送出 手機 平板 觸控',
  continueText: '繼續 指令 按鈕',
  continueLocal: '繼續 指令 按鈕',
  autoSwitchAccount: '額度 用量 上限 換帳戶 備援 quota',
  autoSwitchCrossProvider: '額度 用量 備援 跨代理 換代理',
  claudeRemoteControl: '手機 遠端 remote claude.ai app 接手',
  instructions: '提示詞 prompt 系統提示 說明 指示',
  autoUpdateCli: '更新 升級 update',
  shareCrossCliCustomizations: 'skills 技能 claude.md agents.md gemini.md 指示 共用 同步',
  claudeIdleTokenRefresh: '權杖 token 登入 過期 用量',
  cliRestartPolicy: '更新 重開 換版 安全點',
  autoUpdateApp: '更新 升級 版本 update',
  defaultModel: '模型 model 預設',
  defaultPermission: '權限 核准 完全自動 每步詢問 permission',
  fallbackModel: '備援 過載 overloaded fallback',
  effort: '推理 思考 強度 effort reasoning thinking',
  autoCompact: '自動壓縮 壓縮 上下文 context compact autocompact token 上限',
});

/** 比對用的字：全形英數轉半形（NFKC）、轉小寫 */
/**
 * 自動壓縮上限的輸入（同宿主的 AutoCompactRules.TryParse）：空白或 default＝CLI 預設（null）；數字可帶 k／m 與千分位逗號、底線、空白，
 * 例如 400k、400,000、0.4M。看不懂的、0、負數回 undefined。範圍由宿主夾（設定送回來時照宿主的值顯示）
 */
export function parseTokens(text) {
  let s = foldText(text).trim();
  if (!s || s === 'default') return null;
  s = s.replace(/[,_\s]/g, '');
  let unit = 1;
  if (s.endsWith('k')) unit = 1e3;
  else if (s.endsWith('m')) unit = 1e6;
  if (unit !== 1) s = s.slice(0, -1);
  if (!/^(\d+(\.\d*)?|\.\d+)$/.test(s)) return undefined;
  const n = Number(s);
  if (!(n > 0) || n > 1e9) return undefined;
  const tokens = Math.round(n * unit);
  return tokens > 0 ? tokens : undefined;
}

/**
 * 自動壓縮門檻的百分比（同宿主的 AutoCompactRules.TryParsePercent）：空白或 default＝CLI 預設（null）；整數，可帶 %（例如 70、70%）。
 * 小數、0、負數、看不懂的回 undefined。範圍由宿主夾
 */
export function parsePercent(text) {
  let s = foldText(text).trim();
  if (!s || s === 'default') return null;
  if (s.endsWith('%')) s = s.slice(0, -1).trimEnd();
  if (!/^\d{1,4}$/.test(s)) return undefined;
  const n = Number(s);
  return n > 0 ? n : undefined;
}

/** 百分比的顯示：70 → 70%；null 是空字串 */
export const fmtPercent = (n) => (n == null || !Number.isFinite(n) ? '' : n + '%');

/**
 * 模型在選單與按鈕上的名稱：「預設」那一項宿主知道實際是哪個模型時（resolved，DefaultModelRules）寫成「預設（Opus 5.5）」，
 * 其他照 label（沒有 label 用 value）
 */
export function modelText(m) {
  if (!m) return '';
  if (m.value === 'default' && m.resolved) return '預設（' + m.resolved + '）';
  return m.label || m.value;
}

/**
 * 推理強度鈕的文字（2026-11-12 使用者要求：選「預設」時也顯示實際是哪個強度）：預設而且知道它是哪個強度時「預設（中）」，
 * 不知道時「預設」；其他照名稱，目前的模型不支援時加「（不支援）」。labels 是強度的中文名稱（store.EFFORT_LABEL）
 */
export function effortText(value, defaultEffort, labels, supported = true) {
  const name = (v) => (labels && labels[v]) || v;
  if (!value || value === 'default') return typeof defaultEffort === 'string' && defaultEffort ? '預設（' + name(defaultEffort) + '）' : '預設';
  return name(value) + (supported ? '' : '（不支援）');
}

/** token 數的短寫：400000 → 400k、1500000 → 1.5M、367500 → 367.5k、999 → 999；null 是空字串 */
export function fmtTokens(n) {
  if (n == null || !Number.isFinite(n)) return '';
  const trim = (x) => String(Math.round(x * 10) / 10);
  if (n >= 1e6 && n % 1e5 === 0) return trim(n / 1e6) + 'M';
  if (n >= 1e3 && n % 100 === 0) return trim(n / 1e3) + 'k';
  return String(n);
}

/**
 * 配對時使用者輸入的 Hub 網址：沒寫 https:// 的補上；只收 https、只有主機與連接埠（可以有結尾的 /），
 * 帶路徑、查詢字串、帳號密碼或反斜線的不收（同宿主的 DevTrustRules.ValidateServerUrl）。合格回 origin（https://host[:port]），否則 null
 */
export function normalizeHubUrl(text) {
  let t = String(text == null ? '' : text).trim();
  if (!t) return null;
  if (!/^[a-z][a-z0-9+.-]*:\/\//i.test(t)) t = 'https://' + t;
  if (/[@?#\\\s]/.test(t)) return null;
  let u;
  try { u = new URL(t); } catch { return null; }
  if (u.protocol !== 'https:' || !u.hostname || u.username || u.password || u.search || u.hash || u.pathname !== '/') return null;
  return u.origin;
}

export const foldText = (s) => String(s == null ? '' : s).normalize('NFKC').toLowerCase();

/** 搜尋字串拆成詞（空白分開；全形空白也算），全部轉成比對用的字；空字串是 [] */
export function searchTerms(q) {
  return foldText(q).split(/\s+/).filter(Boolean);
}

/** 各欄位命中一個詞的分數：標籤最重要，其次是所在的區塊與分頁、同義詞，說明最輕 */
const WEIGHT = Object.freeze({ label: 100, sec: 40, tab: 30, keywords: 30, desc: 10 });

/**
 * 一筆設定（{ label, desc, sec, tab, keywords }，都是字串、可省略）對這組詞的分數：每個詞都要命中至少一個欄位（AND），
 * 否則是 -1；分數是每個詞命中的最重欄位加總，標籤以這個詞開頭再加 20（「主題」找「主」時排在說明裡有「主」的前面）
 */
export function scoreEntry(entry, terms) {
  if (!terms.length) return -1;
  const fields = {};
  for (const k of Object.keys(WEIGHT)) fields[k] = foldText(entry && entry[k]);
  let total = 0;
  for (const t of terms) {
    let best = 0;
    for (const k of Object.keys(WEIGHT)) if (fields[k].includes(t) && WEIGHT[k] > best) best = WEIGHT[k];
    if (!best) return -1;
    total += best + (fields.label.startsWith(t) ? 20 : 0);
  }
  return total;
}

/**
 * 搜尋：回傳命中的設定（原物件），分數高的在前、同分照原本的順序（設定頁由上而下）；最多 limit 筆。
 * 詞是空的回 []（設定頁照常顯示分頁）
 */
export function searchSettings(entries, q, limit = 40) {
  const terms = searchTerms(q);
  if (!terms.length) return [];
  const hits = [];
  (entries || []).forEach((e, i) => {
    const s = scoreEntry(e, terms);
    if (s >= 0) hits.push({ e, s, i });
  });
  hits.sort((a, b) => b.s - a.s || a.i - b.i);
  return hits.slice(0, limit).map((x) => x.e);
}

/**
 * 把文字切成 [{ text, hit }]：命中任何一個詞的地方 hit 是 true（重疊、相鄰的合併成一段）。
 * 只轉小寫比對（不做 NFKC：全形字換成半形會改變長度、位置對不上）；轉小寫後長度變了（極少見的字）就整段不標
 */
export function highlightParts(text, terms) {
  const src = String(text == null ? '' : text);
  const lower = src.toLowerCase();
  if (!src || !terms || !terms.length || lower.length !== src.length) return src ? [{ text: src, hit: false }] : [];
  const ranges = [];
  for (const t of terms) {
    if (!t) continue;
    let from = 0;
    for (;;) {
      const i = lower.indexOf(t, from);
      if (i < 0) break;
      ranges.push([i, i + t.length]);
      from = i + t.length;
    }
  }
  if (!ranges.length) return [{ text: src, hit: false }];
  ranges.sort((a, b) => a[0] - b[0] || a[1] - b[1]);
  const merged = [ranges[0].slice()];
  for (const r of ranges.slice(1)) {
    const last = merged[merged.length - 1];
    if (r[0] <= last[1]) last[1] = Math.max(last[1], r[1]);
    else merged.push(r.slice());
  }
  const out = [];
  let at = 0;
  for (const [a, b] of merged) {
    if (a > at) out.push({ text: src.slice(at, a), hit: false });
    out.push({ text: src.slice(a, b), hit: true });
    at = b;
  }
  if (at < src.length) out.push({ text: src.slice(at), hit: false });
  return out;
}

/**
 * 說明的節錄：不超過 max 字時原樣回傳；第一個命中的詞落在前 lead 字以內就從頭截，
 * 否則從命中處往前 lead 字開始（前面加「…」），讓命中的字看得到；截斷的尾端加「…」
 */
export function snippet(text, terms, max = 90, lead = 24) {
  const src = String(text == null ? '' : text);
  if (src.length <= max) return src;
  const lower = src.toLowerCase();
  let first = -1;
  for (const t of terms || []) {
    const i = t ? lower.indexOf(t) : -1;
    if (i >= 0 && (first < 0 || i < first)) first = i;
  }
  const start = first > lead ? first - lead : 0;
  const body = src.slice(start, start + max);
  return (start > 0 ? '…' : '') + body + (start + max < src.length ? '…' : '');
}

/**
 * 分頁列的方向鍵：回傳要移到的索引（頭尾循環），不是方向鍵回 null。
 * 直排與橫排都認上下左右（直排的側欄在窄視窗會變成橫排，兩種按法都通）；Home／End 到頭尾
 */
export function tabKeyTarget(key, index, count) {
  if (count <= 0) return null;
  let next;
  switch (key) {
    case 'ArrowDown': case 'ArrowRight': next = index + 1; break;
    case 'ArrowUp': case 'ArrowLeft': next = index - 1; break;
    case 'Home': return 0;
    case 'End': return count - 1;
    default: return null;
  }
  return (next + count) % count;
}

/** 搜尋結果在各分頁的筆數（第九批：左側分類旁寫命中數）：{ 分頁 id: 筆數 } */
export function tabHitCounts(entries) {
  const out = {};
  for (const e of Array.isArray(entries) ? entries : []) {
    if (!e || typeof e.tabId !== 'string') continue;
    out[e.tabId] = (out[e.tabId] || 0) + 1;
  }
  return out;
}
