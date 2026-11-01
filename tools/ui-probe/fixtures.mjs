// 介面探針的測試資料：帳戶（2 個 Claude、2 個 Codex、1 個 Antigravity、1 個 Grok）、14 筆對話、一個含所有項目種類的對話、檔案樹、程式產生的測試圖片。
// 時間都相對於探針啟動的 now；亂數用固定種子，同一個 now 會產生同一份資料。
import zlib from 'node:zlib';

export const ORIGIN = 'https://myriagent.local';
export const ROOT = 'C:\\repo\\myriagent';
const MIN = 60e3, HOUR = 3600e3, DAY = 86400e3;

export const media = (p) => `${ORIGIN}/media?path=${encodeURIComponent(p)}`;
export const thumb = (p, size = 160) => `${ORIGIN}/thumb?path=${encodeURIComponent(p)}&size=${size}`;

function rng(seed) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6D2B79F5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

// ───── 用量歷史（7 天、每小時一點） ─────
function usageHistory(now, kind, current, seed) {
  const r = rng(seed);
  const pts = [];
  const start = now - 7 * DAY;
  let weekly = 0;
  for (let i = 0; i < 168; i++) {
    const t = start + i * HOUR;
    const hour = new Date(t).getHours();
    const work = hour >= 9 && hour <= 23 ? 1 : 0.15;
    let v;
    if (kind === 'session') {
      const phase = i % 5;
      v = phase === 0 ? 2 + r() * 4 : Math.min(98, (phase / 4) * (30 + r() * 50) * work + r() * 6);
    } else {
      weekly += (current / 168) * (0.4 + r() * 1.2) * (work > 0.5 ? 1.3 : 0.4);
      v = Math.min(100, weekly);
    }
    pts.push([t, Math.round(v * 10) / 10]);
  }
  pts[pts.length - 1][1] = current;
  return pts;
}

function sev(p) { return p >= 95 ? 'max' : p >= 80 ? 'high' : p >= 50 ? 'warn' : 'ok'; }

function win(now, key, label, percent, resetIn, kind, seed) {
  return { key, label, percent, resetsAt: resetIn === null ? null : now + resetIn, severity: sev(percent), history: usageHistory(now, kind, percent, seed) };
}

// ───── 帳戶 ─────
export function makeAccounts(now) {
  return [
    {
      id: 'a1', provider: 'Claude', label: '工作用 Max', color: '#D97757', isSystemDefault: true, configDirectory: 'C:\\Users\\Lin\\.claude',
      email: 'lin.dev@example.com', plan: 'Claude Max', status: 'ready', statusText: '已登入', safeMode: false, loginBusy: false,
      usage: {
        fetchedAt: now - 2 * MIN, stale: false, source: 'api', error: null, credits: null, note: null,
        windows: [
          win(now, 'session', '5 小時', 34, 2 * HOUR + 13 * MIN, 'session', 11),
          win(now, 'weekly', '每週', 63, 3 * DAY + 4 * HOUR, 'weekly', 12),
          win(now, 'weekly:Fable', '每週・Fable', 41, 3 * DAY + 4 * HOUR, 'weekly', 13),
        ],
      },
      sessions: { open: 3, running: 1, waiting: 1 },
    },
    {
      id: 'a2', provider: 'Claude', label: '個人 Pro', color: '#A872E0', isSystemDefault: false,
      configDirectory: 'C:\\Users\\Lin\\AppData\\Local\\Myriagent\\accounts\\a2',
      email: 'lin.home@example.com', plan: 'Claude Pro', status: 'ready', statusText: '已登入', safeMode: true, shareCustomizations: true, loginBusy: false,
      usage: {
        fetchedAt: now - 38 * MIN, stale: true, source: 'cache', error: null, credits: null, note: null,
        windows: [
          win(now, 'session', '5 小時', 88, 47 * MIN, 'session', 21),
          win(now, 'weekly', '每週', 72, 5 * DAY + 9 * HOUR, 'weekly', 22),
        ],
      },
      sessions: { open: 1, running: 1, waiting: 0 },
    },
    {
      id: 'a3', provider: 'Codex', label: 'Codex Pro', color: '#1FAE8E', isSystemDefault: true, configDirectory: 'C:\\Users\\Lin\\.codex',
      email: 'lin.dev@example.com', plan: 'Pro', status: 'ready', statusText: '已登入（ChatGPT）', safeMode: false, loginBusy: false,
      usage: {
        fetchedAt: now - 4 * MIN, stale: false, source: 'api', error: null, credits: '12345.68', note: null,
        resetCredits: 1, resetCreditExpiresAt: now + 9 * DAY + 3 * HOUR,
        windows: [win(now, 'codex:primary', '每週', 18, 5 * DAY + 2 * HOUR, 'weekly', 31)],
      },
      sessions: { open: 1, running: 0, waiting: 0 },
    },
    {
      id: 'a4', provider: 'Codex', label: 'Codex 團隊', color: '#4C8DF6', isSystemDefault: false,
      configDirectory: 'C:\\Users\\Lin\\AppData\\Local\\Myriagent\\accounts\\a4',
      email: null, plan: null, status: 'notLoggedIn', statusText: '尚未登入', safeMode: false, shareCustomizations: false, loginBusy: false,
      usage: null, sessions: { open: 0, running: 0, waiting: 0 },
    },
    {
      // Antigravity：只有本機預設帳戶，以 agy -p /usage --output-format json 查兩個模型群組共四個額度窗
      id: 'a5', provider: 'Antigravity', label: 'Antigravity（本機）', color: '#7C8CF8', isSystemDefault: true,
      configDirectory: 'C:\\Users\\Lin\\.gemini\\antigravity-cli',
      email: 'lin.agy@example.com', plan: null, status: 'ready', statusText: '已登入', safeMode: false, loginBusy: false,
      usage: {
        fetchedAt: now - 3 * MIN, stale: false, source: 'api', error: null, credits: null, note: null,
        windows: [
          win(now, 'agy-gemini-5h', '5 小時・Gemini', 19, 4 * HOUR + 12 * MIN, 'session', 51),
          win(now, 'agy-gemini-weekly', '每週・Gemini', 8, 5 * DAY + 15 * HOUR, 'weekly', 52),
          win(now, 'agy-3p-5h', '5 小時・Claude/GPT', 4, 4 * HOUR + 54 * MIN, 'session', 53),
          win(now, 'agy-3p-weekly', '每週・Claude/GPT', 0, 6 * DAY + 2 * HOUR, 'weekly', 54),
        ],
      },
      sessions: { open: 0, running: 0, waiting: 0 },
    },
    {
      // Grok：本機預設帳戶（~/.grok），額度只有 _x.ai/billing 的一個每週額度窗
      id: 'a6', provider: 'Grok', label: 'Grok（本機）', color: '#A3B83A', isSystemDefault: true,
      configDirectory: 'C:\\Users\\Lin\\.grok',
      email: 'lin.grok@example.com', plan: 'SuperGrok', status: 'ready', statusText: '已登入', safeMode: false, loginBusy: false,
      usage: {
        fetchedAt: now - 6 * MIN, stale: false, source: 'api', error: null, credits: null, note: null,
        windows: [win(now, 'grok-credits', '每週', 12, 4 * DAY + 9 * HOUR, 'weekly', 41)],
      },
      sessions: { open: 0, running: 0, waiting: 0 },
    },
  ];
}

/** 側欄帳戶卡的 token 計數（init.tokens 與 tokens 訊息，web-protocol 的 Tokens）：a4 未登入、近 30 天沒用過（不顯示） */
export function makeTokens() {
  const c = (input, cached, output) => ({ input, cached, output });
  return {
    day: '2026-10-22',
    accounts: {
      a1: { today: c(1_226_418, 1_018_772, 57_913), week: c(9_482_113, 8_001_224, 402_771), month: c(31_870_552, 27_114_009, 1_388_410) },
      a2: { today: c(318_004, 250_120, 12_877), week: c(1_902_440, 1_500_002, 88_120), month: c(5_002_118, 4_100_771, 230_455) },
      a3: { today: c(88_412, 56_320, 1_934), week: c(612_330, 420_118, 14_002), month: c(2_330_918, 1_800_330, 52_118) },
      a4: { today: c(0, 0, 0), week: c(0, 0, 0), month: c(0, 0, 0) },
      a5: { today: c(0, 0, 0), week: c(41_220, 30_118, 2_330), month: c(41_220, 30_118, 2_330) },
      a6: { today: c(12_330, 0, 880), week: c(12_330, 0, 880), month: c(12_330, 0, 880) },
    },
  };
}

/** 宿主 AccountRules 的字樣（init 的 providers） */
export const PROVIDERS = [
  { key: 'Claude', name: 'Claude Code', isolatedAccounts: true, safeMode: true, sharedCustomizations: true },
  { key: 'Codex', name: 'Codex', isolatedAccounts: true, safeMode: true, sharedCustomizations: true },
  { key: 'Antigravity', name: 'Antigravity', isolatedAccounts: false, safeMode: false, sharedCustomizations: false },
  { key: 'Grok', name: 'Grok Build', isolatedAccounts: true, safeMode: false, sharedCustomizations: false },
];

export function makeCli(now) {
  return {
    Claude: {
      provider: 'Claude', installed: true, version: '2.1.295', latest: '2.1.295', updateAvailable: false, installKind: 'native',
      path: 'C:\\Users\\Lin\\.local\\bin\\claude.exe', autoUpdate: true, busy: false, progress: null, lastCheckedAt: now - 12 * MIN, checkedAt: now - 12 * MIN, error: null,
    },
    Codex: {
      provider: 'Codex', installed: true, version: '0.160.1', latest: '0.162.0', updateAvailable: true, installKind: 'npm',
      path: 'C:\\Users\\Lin\\AppData\\Roaming\\npm\\codex.cmd', autoUpdate: true, busy: false, progress: null, lastCheckedAt: now - 12 * MIN, checkedAt: now - 12 * MIN, error: null,
    },
    Antigravity: {
      provider: 'Antigravity', installed: true, version: '1.3.5', latest: '1.3.5', updateAvailable: false, installKind: 'native',
      path: 'C:\\Users\\Lin\\AppData\\Local\\agy\\bin\\agy.exe', autoUpdate: true, busy: false, progress: null, lastCheckedAt: now - 12 * MIN, checkedAt: now - 12 * MIN, error: null,
    },
    Grok: {
      provider: 'Grok', installed: true, version: '1.0.52', latest: '1.0.52', updateAvailable: false, installKind: 'native',
      path: 'C:\\Users\\Lin\\.grok\\bin\\grok.exe',
      autoUpdate: true, busy: false, progress: null, lastCheckedAt: now - 12 * MIN, checkedAt: now - 12 * MIN, error: null,
    },
  };
}

export const SETTINGS = {
  theme: 'dark', fontSize: 14, closeToTray: true, autoStart: false, notifications: true, voiceNotifications: true, autoUpdateCli: true, enterToSend: true, claudeRemoteControl: true, claudeIdleTokenRefresh: true, autoSwitchAccount: true, autoSwitchCrossProvider: false, shareCrossCliCustomizations: false,
  instructions: '回覆一律使用臺灣繁體中文。工作資料夾是 {cwd}，使用者附加的檔案在 {attachments}。',
};

// ───── 權限模式、模型、指令 ─────
export const CLAUDE_MODES = [
  { value: 'Ask', label: '每步詢問', description: '執行指令、修改檔案前先問你' },
  { value: 'AcceptEdits', label: '自動接受編輯', description: '修改檔案不再詢問，執行指令仍會問' },
  { value: 'Plan', label: '規劃模式', description: '只讀取與規劃，不修改任何東西' },
  { value: 'Auto', label: '自動', description: '低風險操作直接執行，高風險才問' },
  { value: 'FullAccess', label: '完全自動', description: '所有操作都不詢問（略過權限檢查）' },
];
export const CODEX_MODES = [
  { value: 'Ask', label: '每步詢問', description: '執行指令、修改檔案前先問你（untrusted）' },
  { value: 'Auto', label: '自動', description: '在工作資料夾內自動執行，需要時才問（on-request）' },
  { value: 'FullAccess', label: '完全自動', description: '不詢問、不限制檔案存取（danger-full-access）' },
];
const CLAUDE_MODELS = [
  { value: 'default', label: '預設（建議）', description: '帳戶預設模型', resolved: 'Opus 5.5', efforts: ['low', 'medium', 'high', 'xhigh', 'max', 'ultracode'] },
  { value: 'opus', label: 'Opus 5.5', description: '最強的推理與程式能力', efforts: ['low', 'medium', 'high', 'xhigh', 'max', 'ultracode'] },
  { value: 'sonnet', label: 'Sonnet 5', description: '速度與能力平衡', efforts: ['low', 'medium', 'high'] },
  { value: 'haiku', label: 'Haiku 5', description: '最快、最省額度', efforts: [] },
];
// 與 AgentModels.cs 的 AntigravityOptions 相同（agy 沒有每步詢問；--mode auto 實測這個帳戶不能用）
export const AGY_MODES = [
  { value: 'AcceptEdits', label: '自動接受編輯', description: '可以讀取與修改檔案；執行指令等需要核准的操作會被自動拒絕（agy 無法在對話中詢問）' },
  { value: 'Plan', label: '規劃模式', description: '只讀取與分析，寫出計畫後等你決定；要執行時改成其他模式' },
  { value: 'FullAccess', label: '完全自動', description: '一律放行，包括執行指令' },
];
// agy models 的實測清單合併後的樣子：同一個模型的幾個強度合併成一項（gpt-oss-120b 只有 medium）
const AGY_MODELS = [
  { value: 'default', label: '預設', description: 'agy 設定的預設模型（在 agy 互動介面以 /model 選的）', efforts: ['low', 'medium', 'high'] },
  { value: 'gemini-4-argon', label: 'Gemini 4 Argon', description: null, efforts: ['low', 'medium', 'high'] },
  { value: 'gemini-3.8-flash', label: 'Gemini 3.8 Flash', description: null, efforts: ['low', 'medium', 'high'] },
  { value: 'gpt-oss-120b', label: 'GPT-OSS 120B', description: null, efforts: ['medium'] },
];
// 與 AgentModels.cs 的 GrokOptions 相同（五種都有；自動接受編輯由 app 代為核准工作資料夾內的編輯）
export const GROK_MODES = [
  { value: 'Ask', label: '每步詢問', description: '執行指令、修改檔案前先問你' },
  { value: 'AcceptEdits', label: '自動接受編輯', description: '工作資料夾內修改檔案不再詢問，執行指令仍會問' },
  { value: 'Plan', label: '規劃模式', description: '只讀取與分析，先提出計畫給你確認' },
  { value: 'Auto', label: '自動', description: '由 Grok 判斷，高風險操作才問（帳戶沒開放時照每步詢問）' },
  { value: 'FullAccess', label: '完全自動', description: '一律放行，不再詢問' },
];
// grok 1.0.52 實測的模型清單（initialize 的 modelState；預設取 currentModelId 的思考強度）
const GROK_MODELS = [
  { value: 'default', label: '預設', description: 'grok 的預設模型（目前是 Grok 4.7）', resolved: 'Grok 4.7', efforts: ['low', 'medium', 'high', 'xhigh'] },
  { value: 'grok-4.7', label: 'Grok 4.7', description: 'latest frontier model', efforts: ['low', 'medium', 'high', 'xhigh'] },
  { value: 'grok-4.6', label: 'Grok 4.6', description: null, efforts: ['low', 'medium', 'high', 'xhigh'] },
  { value: 'grok-4.5', label: 'Grok 4.5', description: null, efforts: ['low', 'medium', 'high'] },
];
const CODEX_MODELS = [
  { value: 'default', label: '預設（gpt-6.1-sol）', description: '帳戶預設模型', efforts: ['minimal', 'low', 'medium', 'high', 'xhigh'] },
  { value: 'gpt-6.1-sol', label: 'GPT-6.1 Sol', description: '程式任務的旗艦模型', efforts: ['minimal', 'low', 'medium', 'high', 'xhigh'] },
  { value: 'gpt-6-astra', label: 'GPT-6 Astra', description: '長時間代理任務', efforts: ['low', 'medium', 'high', 'xhigh', 'ultra'] },
];
/**
 * 設定頁「模型設定」（每次開 app 重建一份，假宿主會改它）：Claude 新對話用 Opus、預設自動、預設模型高、Opus 最高、Haiku 不支援推理強度；
 * Codex 沿用上次選擇、每步詢問
 */
export function makeConversationDefaults() {
  const rows = (models, efforts) => models.map((m) => Object.assign({}, m, { effort: efforts[m.value] || null }));
  return {
    // 自動壓縮上限（AutoCompactRules）：Claude、Codex 以 token 數、Grok 以百分比，Antigravity 沒有
    Claude: { model: 'opus', permission: 'Auto', permissionModes: CLAUDE_MODES, models: rows(CLAUDE_MODELS, { default: 'high', opus: 'max' }), autoCompact: null, autoCompactRange: { min: 67000, max: 967000 }, autoCompactPercent: null, autoCompactPercentRange: null },
    Codex: { model: null, permission: 'Ask', permissionModes: CODEX_MODES, models: rows(CODEX_MODELS, { 'gpt-6-astra': 'ultra' }), autoCompact: 200000, autoCompactRange: { min: 20000, max: 2000000 }, autoCompactPercent: null, autoCompactPercentRange: null },
    Antigravity: { model: null, permission: 'AcceptEdits', permissionModes: AGY_MODES, models: rows(AGY_MODELS, {}), autoCompact: null, autoCompactRange: null, autoCompactPercent: null, autoCompactPercentRange: null },
    Grok: { model: null, permission: 'Ask', permissionModes: GROK_MODES, models: rows(GROK_MODELS, {}), autoCompact: null, autoCompactRange: null, autoCompactPercent: null, autoCompactPercentRange: { min: 20, max: 95 } },
  };
}

const COMMANDS = [
  { name: 'compact', description: '壓縮對話，保留重點摘要', argumentHint: '[要保留的重點]' },
  { name: 'clear', description: '清除對話紀錄，重新開始', argumentHint: '' },
  { name: 'review', description: '審查目前的程式變更', argumentHint: '[範圍]' },
  { name: 'init', description: '為這個專案建立 CLAUDE.md', argumentHint: '' },
  { name: 'cost', description: '顯示這個對話用掉的額度', argumentHint: '' },
  { name: 'memory', description: '編輯記憶檔案', argumentHint: '' },
  { name: 'model', description: '切換模型', argumentHint: '[模型]' },
  { name: 'agents', description: '管理子代理', argumentHint: '' },
  { name: 'mcp', description: '管理 MCP 伺服器', argumentHint: '' },
  { name: 'pr-comments', description: '讀取 GitHub PR 的留言', argumentHint: '[PR 編號]' },
  { name: 'security-review', description: '檢查目前變更的安全性問題', argumentHint: '' },
  { name: 'context', description: '顯示目前用掉多少上下文', argumentHint: '' },
];

const accountMeta = { a1: 'Claude', a2: 'Claude', a3: 'Codex', a4: 'Codex', a5: 'Antigravity', a6: 'Grok' };

export function baseHeader(id, accountId, title, extra = {}) {
  const provider = accountMeta[accountId] || 'Claude';
  return Object.assign({
    id, accountId, provider, title,
    model: 'default', effort: 'default', permission: 'Ask',
    permissionModes: provider === 'Codex' ? CODEX_MODES : provider === 'Antigravity' ? AGY_MODES : provider === 'Grok' ? GROK_MODES : CLAUDE_MODES,
    models: provider === 'Codex' ? CODEX_MODELS : provider === 'Antigravity' ? AGY_MODELS : provider === 'Grok' ? GROK_MODELS : CLAUDE_MODELS,
    commands: provider === 'Claude' ? COMMANDS : [],
    workingDirectory: ROOT, customFolder: true,
    recentFolders: [ROOT, 'C:\\repo\\SampleShop', 'D:\\work\\design-assets', 'C:\\Users\\Lin\\Downloads'],
    isDraft: false, running: false, status: null, startedAt: null, lastTurn: null, accountMissing: false,
  }, extra);
}

// ───── 對話歷史 ─────
export function makeConversations(now) {
  const c = (id, accountId, title, ago, extra = {}) => Object.assign({
    id, accountId, provider: accountMeta[accountId], title, updatedAt: now - ago, workingDirectory: ROOT, customFolder: true,
    running: false, waiting: false, unread: false,
  }, extra);
  const list = [
    c('c1', 'a1', '修正登入流程卡住並補上單元測試', 1 * MIN, { running: true, waiting: true }),
    c('c3', 'a2', '重構檔案瀏覽器的縮圖快取', 3 * MIN, { running: true }),
    c('c2', 'a3', 'Codex 用量端點的欄位解析', 25 * MIN, { unread: true }),
    c('c4', 'a1', '產生三張儀表板示意圖', 2 * HOUR),
    c('c5', 'a3', '整理 CLI 自動更新的流程', 5 * HOUR),
    c('c6', 'a1', '分頁拖曳到另一個窗格時的狀態同步', 26 * HOUR),
    c('c7', 'a2', '撰寫 README 的安裝說明', 30 * HOUR, { unread: true }),
    c('c8', 'a3', '找出記憶體用量偏高的原因', 3 * DAY),
    c('c9', 'a1', 'Markdig 停用原始 HTML 的設定', 4 * DAY),
    c('c10', 'a2', '系統匣圖示的深淺色版本', 6 * DAY),
    c('c11', 'a1', 'WebView2 的 CSP 與 inline script', 9 * DAY),
    c('c12', 'a3', 'Job Object 帶走子程序的寫法', 14 * DAY),
    c('c13', 'a0', '帳戶隔離的環境變數規則', 22 * DAY), // 帳戶已被移除
    c('c14', 'a2', '九月的用量報表整理', 34 * DAY),
  ];
  for (const x of list) x.group = groupOf(now, x.updatedAt);
  return list;
}

export function groupOf(now, t) {
  const d = new Date(now);
  const today = new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
  if (t >= today) return '今天';
  if (t >= today - DAY) return '昨天';
  if (t >= today - 7 * DAY) return '過去 7 天';
  if (t >= today - 30 * DAY) return '過去 30 天';
  const m = new Date(t);
  return m.getFullYear() + ' 年 ' + (m.getMonth() + 1) + ' 月';
}

// ───── 對話內容 ─────
const P = {
  shot: 'C:\\Users\\Lin\\Pictures\\登入卡住的畫面.png',
  rec: 'C:\\Users\\Lin\\Videos\\重現步驟.mp4',
  log: 'C:\\Users\\Lin\\AppData\\Local\\Myriagent\\logs\\agent.log',
  am: ROOT + '\\src\\Myriagent\\Accounts\\AccountManager.cs',
  chartA: ROOT + '\\assets\\用量圖表-本週.png',
  shotB: ROOT + '\\assets\\登入畫面-修正後.png',
  shotC: ROOT + '\\assets\\登入畫面-逾時提示.png',
  demo: ROOT + '\\assets\\登入流程示範.mp4',
  newShot: 'C:\\Users\\Lin\\Pictures\\設定畫面.png',
  spec: 'C:\\Users\\Lin\\Documents\\登入流程規格.pdf',
};

export function makeConversationData(now, hasVideo) {
  const t = (minAgo) => now - minAgo * MIN;
  const vid = (p) => (hasVideo ? media(p) : null);
  const items = [
    { id: 'i1', kind: 'User', status: 'Done', time: t(42), html: '<p>帳戶沒有登入時，按「登入」之後畫面就卡住不動。幫我找出原因、修好，並補上單元測試。附上截圖、錄影和記錄檔。</p>',
      attachments: [
        { id: 'x1', name: '登入卡住的畫面.png', kind: 'Image', size: '182 KB', path: P.shot, thumbUrl: thumb(P.shot), url: media(P.shot), videoInfo: null },
        { id: 'x2', name: '重現步驟.mp4', kind: 'Video', size: '1.2 MB', path: P.rec, thumbUrl: thumb(P.rec), url: vid(P.rec), videoInfo: '0:03・6 張畫面' },
        { id: 'x3', name: 'agent.log', kind: 'Document', size: '12 KB', path: P.log, thumbUrl: null, url: null, videoInfo: null },
      ] },
    { id: 'i2', kind: 'Thinking', status: 'Done', time: t(41), html: '<p>截圖顯示登入視窗關閉後主畫面仍是「登入中」。先看 <code>AccountManager.LoginAsync</code> 怎麼等登入結果，再看視窗關閉時有沒有通知中樞。</p><p>錄影裡第二次按登入沒有反應，可能是第一次的等待還沒結束、<code>loginBusy</code> 一直是 true。</p>' },
    { id: 'i3', kind: 'Assistant', status: 'Done', time: t(41), html: '<p>我先看一下登入流程相關的程式。</p>' },
    { id: 'i4', kind: 'Tool', status: 'Done', time: t(40), toolKind: 'search', toolName: '搜尋', title: 'LoginAsync|WaitForLoginAsync',
      detail: 'pattern: LoginAsync|WaitForLoginAsync\npath: src', output: 'src\\Myriagent\\Accounts\\AccountManager.cs:142:    public async Task<LoginResult> LoginAsync(AgentAccount account)\nsrc\\Myriagent\\Accounts\\AccountManager.cs:188:    private async Task<bool> WaitForLoginAsync(AgentAccount account)\nsrc\\Myriagent\\Hub\\AgentHub.Accounts.cs:57:        var result = await accounts.LoginAsync(account);' },
    { id: 'i5', kind: 'Tool', status: 'Done', time: t(40), toolKind: 'read', toolName: '讀取檔案', title: 'src\\Myriagent\\Accounts\\AccountManager.cs', filePath: P.am,
      detail: '第 120～220 行', output: '' },
    { id: 'i6', kind: 'Tool', status: 'Done', time: t(38), toolKind: 'edit', toolName: '修改檔案', title: 'src\\Myriagent\\Accounts\\AccountManager.cs', filePath: P.am,
      diffStats: { add: 12, del: 3 },
      diff: [
        { t: 'hunk', s: '@@ -140,9 +140,18 @@ public async Task<LoginResult> LoginAsync(AgentAccount account)' },
        { t: 'ctx', s: '        var status = await CheckStatusAsync(account);' },
        { t: 'del', s: '        if (status == AccountStatus.NotLoggedIn)' },
        { t: 'del', s: '            await WaitForLoginAsync(account);' },
        { t: 'del', s: '        return LoginResult.Ok(account.Id);' },
        { t: 'add', s: '        if (status == AccountStatus.NotLoggedIn)' },
        { t: 'add', s: '        {' },
        { t: 'add', s: '            using var cts = new CancellationTokenSource(LoginTimeout);' },
        { t: 'add', s: '            var ok = await WaitForLoginAsync(account, cts.Token);' },
        { t: 'add', s: '            if (!ok)' },
        { t: 'add', s: '            {' },
        { t: 'add', s: '                return LoginResult.TimedOut(account.Id);' },
        { t: 'add', s: '            }' },
        { t: 'add', s: '        }' },
        { t: 'add', s: '' },
        { t: 'add', s: '        account.LoginBusy = false;' },
        { t: 'add', s: '        return LoginResult.Ok(account.Id);' },
        { t: 'ctx', s: '    }' },
        { t: 'hunk', s: '@@ -186,6 +195,7 @@ private async Task<bool> WaitForLoginAsync(AgentAccount account, CancellationToken token)' },
        { t: 'ctx', s: '        var watcher = CreateCredentialWatcher(account);' },
        { t: 'ctx', s: '        await using var _ = token.Register(() => watcher.Dispose());' },
        { t: 'add', s: '        loginWindowClosed += (_, _) => RequestRecheck(account);' },
        { t: 'ctx', s: '        return await watcher.WaitAsync(token);' },
      ] },
    { id: 'i7', kind: 'Tool', status: 'Failed', time: t(37), toolKind: 'bash', toolName: '執行指令', title: 'dotnet build Myriagent.slnx -c Release',
      detail: 'dotnet build Myriagent.slnx -c Release',
      output: '  正在判斷要還原的專案...\n  所有專案都在最新狀態，可進行還原。\nC:\\repo\\myriagent\\src\\Myriagent\\Accounts\\AccountManager.cs(147,58): error CS0103: 名稱 \'LoginTimeout\' 不存在於目前的內容中\n\n建置失敗。\n    0 個警告\n    1 個錯誤' },
    { id: 'i8', kind: 'Tool', status: 'Declined', time: t(36), toolKind: 'web', toolName: '瀏覽網頁', title: 'https://learn.microsoft.com/dotnet/api/system.threading.cancellationtokensource' },
    { id: 'i9', kind: 'Tool', status: 'Cancelled', time: t(36), toolKind: 'agent', toolName: '子代理', title: '檢查其他呼叫 LoginAsync 的地方', detail: '找出所有呼叫 LoginAsync 的地方，確認逾時回傳不會讓呼叫端出錯。' },
    { id: 'i10', kind: 'Tool', status: 'Done', time: t(35), toolKind: 'mcp', toolName: 'MCP：github', title: 'search_issues "login hang" repo:example-user/myriagent', output: '找到 1 個相關議題：#12 登入視窗關掉後卡在登入中' },
    { id: 'i11', kind: 'Plan', status: 'Running', time: t(34), html: '', plan: [
      { text: '找出登入卡住的原因', status: 'completed' },
      { text: '在 LoginAsync 加上逾時與取消', status: 'completed' },
      { text: '登入視窗關閉時立即重新檢查', status: 'inProgress' },
      { text: '補上 4 個單元測試', status: 'pending' },
      { text: '跑完整的 dotnet test', status: 'pending' },
    ] },
    { id: 'i12', kind: 'Assistant', status: 'Done', time: t(33), html:
      '<h3>找到原因了</h3>' +
      '<p>帳戶沒有登入時，<code>WaitForLoginAsync</code> 沒有逾時；登入視窗被關掉後，它就一直等下去，<code>loginBusy</code> 也不會清掉。我做了三件事：</p>' +
      '<ol><li>加上 <strong>3 分鐘</strong>逾時，逾時回傳 <code>LoginResult.TimedOut</code></li><li>登入視窗關閉時立即重新檢查狀態</li><li>補上 4 個單元測試，涵蓋逾時、關閉視窗與成功三種情況</li></ol>' +
      '<table><thead><tr><th>情境</th><th>修改前</th><th>修改後</th></tr></thead><tbody>' +
      '<tr><td>關掉登入視窗</td><td>一直顯示「登入中」</td><td>立即回報「登入沒有完成」</td></tr>' +
      '<tr><td>3 分鐘沒有完成</td><td>一直等待</td><td>逾時並可以重新登入</td></tr>' +
      '<tr><td>正常登入</td><td>正常</td><td>正常</td></tr></tbody></table>' +
      '<pre><code class="language-csharp">using var cts = new CancellationTokenSource(LoginTimeout);\nvar ok = await WaitForLoginAsync(account, cts.Token);\nif (!ok)\n{\n    return LoginResult.TimedOut(account.Id);\n}\n</code></pre>' +
      '<p>參考：<a href="https://learn.microsoft.com/dotnet/standard/threading/cancellation-in-managed-threads">受控執行緒的取消</a>。修改的檔案：<a href="' + ORIGIN + '/open?path=' + encodeURIComponent(P.am) + '">AccountManager.cs</a></p>' +
      '<blockquote><p>逾時時間之後可以在設定檔調整。</p></blockquote>' },
    { id: 'i13', kind: 'Question', status: 'Done', time: t(30), decision: 'answered', answerAsMessage: false,
      questions: [{ id: 'qa', header: '分支', text: '修改要放在哪個分支？', multi: false, allowOther: true, secret: false,
        options: [{ label: '直接改 main', description: null, preview: null }, { label: '開新分支 fix/login-timeout', description: null, preview: null }] }],
      answers: { qa: ['開新分支 fix/login-timeout'] } },
    { id: 'i14', kind: 'Approval', status: 'Done', time: t(29), decision: 'allowSession', toolKind: 'edit', toolName: '修改檔案', title: 'tests\\Myriagent.Tests\\AccountRulesTests.cs', canAllowForSession: true },
    { id: 'i15', kind: 'Assistant', status: 'Done', time: t(20), html: '<p>重現步驟的畫面和修正後的樣子整理在下面，最後一段是修正後的操作錄影：</p>',
      media: [
        { url: media(P.chartA), kind: 'image', path: P.chartA, name: '用量圖表-本週.png' },
        { url: media(P.shotB), kind: 'image', path: P.shotB, name: '登入畫面-修正後.png' },
        { url: media(P.shotC), kind: 'image', path: P.shotC, name: '登入畫面-逾時提示.png' },
        ...(hasVideo ? [{ url: media(P.demo), kind: 'video', path: P.demo, name: '登入流程示範.mp4' }] : []),
      ] },
    { id: 'i16', kind: 'Notice', status: 'Done', time: t(15), html: '<p>已自動壓縮較早的對話內容，保留最近 80 則</p>' },
    { id: 'i17', kind: 'Error', status: 'Done', time: t(12), html: '<p>Claude 程序意外結束（結束碼 1）：<code>ECONNRESET</code>。已用前文自動重新連線。</p>' },
    { id: 'i18', kind: 'PlanApproval', status: 'Waiting', time: t(6), html: '', detailHtml:
      '<h4>補測試與收尾的計畫</h4><ol><li>在 <code>AccountRulesTests</code> 新增逾時、視窗關閉、成功三組測試</li><li>把 <code>LoginTimeout</code> 移到設定，預設 3 分鐘</li><li>跑 <code>dotnet test</code> 確認全部通過</li></ol><p>預計修改 3 個檔案，不會動到其他帳戶的設定資料夾。</p>' },
    { id: 'i19', kind: 'Question', status: 'Waiting', time: t(4), answerAsMessage: false, questions: [
      { id: 'q1', header: '逾時', text: '登入要等多久才算逾時？', multi: false, allowOther: true, secret: false, options: [
        { label: '3 分鐘（建議）', description: '和 CLI 預設的登入等待時間一致', preview: null },
        { label: '5 分鐘', description: '給需要兩步驟驗證的帳戶多一點時間', preview: null },
        { label: '不要逾時', description: '維持現在的行為，只修正關閉視窗的情況', preview: null } ] },
      { id: 'q2', header: '測試', text: '要補哪些測試？', multi: true, allowOther: false, secret: false, options: [
        { label: '逾時回報', description: null, preview: null },
        { label: '關閉視窗後重新檢查', description: null, preview: null },
        { label: '多個帳戶同時登入', description: '需要模擬兩個設定資料夾', preview: 'AccountManagerTests.LoginTwoAccountsAtOnce()' } ] },
      { id: 'q3', header: '金鑰', text: '整合測試用的測試帳號 API 金鑰（只存在這台電腦）', multi: false, allowOther: true, secret: true, options: [] },
    ] },
    { id: 'i20', kind: 'Approval', status: 'Waiting', time: t(3), toolKind: 'bash', toolName: '執行指令', title: 'dotnet test tests/Myriagent.Tests -c Release --filter "FullyQualifiedName~Login"',
      detail: '在 tests/Myriagent.Tests 執行登入相關的單元測試（約 20 秒），不會連線到外部服務。', canAllowForSession: true, allowSessionLabel: '本對話同樣的指令都允許' },
    { id: 'i21', kind: 'Tool', status: 'Running', time: t(1), toolKind: 'bash', toolName: '執行指令', title: 'dotnet test Myriagent.slnx -c Release',
      detail: 'dotnet test Myriagent.slnx -c Release', output: '  正在判斷要還原的專案...\n  已還原 C:\\repo\\myriagent\\tests\\Myriagent.Tests\\Myriagent.Tests.csproj\n  Myriagent -> C:\\repo\\myriagent\\src\\Myriagent\\bin\\Release\\net10.0-windows\\win-x64\\Myriagent.dll\n  測試回合 Myriagent.Tests.dll (.NETCoreApp,Version=v10.0)\n  正在啟動測試執行，請稍候...' },
  ];
  const older = [
    { id: 'o1', kind: 'User', status: 'Done', time: t(95), html: '<p>先幫我看看新增帳戶之後，側欄為什麼沒有馬上出現新的帳戶卡。</p>' },
    { id: 'o2', kind: 'Tool', status: 'Done', time: t(94), toolKind: 'read', toolName: '讀取檔案', title: 'src\\Myriagent\\Web\\sidebar.js', filePath: ROOT + '\\src\\Myriagent\\Web\\sidebar.js' },
    { id: 'o3', kind: 'Assistant', status: 'Done', time: t(93), html: '<p>側欄是用 <code>accounts</code> 訊息整份更新的，宿主新增帳戶後沒有送出這則訊息。我在 <code>AgentHub.AddAccount</code> 結束時補上通知。</p>' },
    { id: 'o4', kind: 'Tool', status: 'Done', time: t(92), toolKind: 'edit', toolName: '修改檔案', title: 'src\\Myriagent\\Hub\\AgentHub.Accounts.cs', diffStats: { add: 1, del: 0 },
      diff: [{ t: 'hunk', s: '@@ -88,6 +88,7 @@' }, { t: 'ctx', s: '        store.Save(accounts);' }, { t: 'add', s: '        PushAccounts();' }, { t: 'ctx', s: '        return account;' }] },
    // 從 claude.ai 或 Claude App 送來的（Remote Control）：網頁一直標示來源
    { id: 'o5', kind: 'User', status: 'Done', time: t(60), html: '<p>好，這個沒問題了。</p>', source: 'remote' },
    { id: 'o6', kind: 'Assistant', status: 'Done', time: t(59), html: '<p>收到。還有其他要處理的嗎？</p>' },
  ];
  older.concat(items).forEach((it, i) => { it.seq = i + 1; }); // 宿主給的建立順序（較早的一頁在前）
  const c1 = {
    header: baseHeader('c1', 'a1', '修正登入流程卡住並補上單元測試', {
      running: true, status: '執行指令：dotnet test Myriagent.slnx -c Release', startedAt: now - 83e3,
      lastTurn: '輸入 12,345・輸出 2,100 tokens',
      // 上下文用量提示（Header.context）：Claude 有自動壓縮門檻
      context: { used: 412_380, window: 1_000_000, percent: 41.2, autoCompactAt: 967_000, severity: 'ok' },
    }),
    items, hasMore: true, older,
    composer: {
      attachments: [
        { id: 'p1', name: '設定畫面.png', kind: 'Image', size: '96 KB', path: P.newShot, thumbUrl: thumb(P.newShot), url: media(P.newShot), videoInfo: null },
        { id: 'p2', name: '登入流程規格.pdf', kind: 'Document', size: '1.4 MB', path: P.spec, thumbUrl: null, url: null, videoInfo: null },
      ],
      busy: false, busyText: null,
      queue: [{ id: 'q1', text: '接著幫我跑一次完整的整合測試' }, { id: 'q2', text: '最後把這次的變更整理成 commit 訊息' }],
    },
  };
  const c2 = {
    header: baseHeader('c2', 'a3', 'Codex 用量端點的欄位解析', { lastTurn: '輸入 8,210・輸出 1,544 tokens', permission: 'Auto', model: 'gpt-6.1-sol', effort: 'high',
      context: { used: 189_540, window: 258_400, percent: 73.4, autoCompactAt: null, severity: 'warn' } }),
    hasMore: false,
    items: [
      { id: 'b1', kind: 'User', status: 'Done', time: t(30), html: '<p><code>account/rateLimits/read</code> 回來的 primary 和 secondary 要怎麼對應到「5 小時」和「每週」？</p>' },
      { id: 'b2', kind: 'Tool', status: 'Done', time: t(29), toolKind: 'bash', toolName: '執行指令', title: 'codex app-server --probe rateLimits', output: '{ "primary": { "usedPercent": 18, "windowDurationMins": 10080, "resetsAt": 1792000000 } }' },
      { id: 'b3', kind: 'Assistant', status: 'Done', time: t(28), html: '<p>依 <code>windowDurationMins</code> 命名就好：</p><ul><li><code>300</code> → 5 小時</li><li><code>10080</code> → 每週</li></ul><p>目前這個 Pro 帳戶只有 primary，也就是每週額度，用了 <strong>18%</strong>。</p>' },
    ],
    composer: { attachments: [], busy: false, busyText: null, queue: [] },
  };
  const c3 = {
    header: baseHeader('c3', 'a2', '重構檔案瀏覽器的縮圖快取', { running: true, status: '思考中…', startedAt: now - 12e3, permission: 'AcceptEdits', model: 'opus', effort: 'xhigh' }),
    hasMore: false,
    items: [
      { id: 'k1', kind: 'User', status: 'Done', time: t(3), html: '<p>縮圖快取在資料夾很大時會卡 UI，改成背景執行緒產生、用 LRU 限制記憶體。</p>' },
      { id: 'k2', kind: 'Tool', status: 'Done', time: t(2), toolKind: 'read', toolName: '讀取檔案', title: 'src\\Myriagent\\Media\\ThumbnailService.cs', filePath: ROOT + '\\src\\Myriagent\\Media\\ThumbnailService.cs' },
      { id: 'k3', kind: 'Thinking', status: 'Running', time: t(1), html: '<p>目前是在 UI 執行緒呼叫 Shell 縮圖，要改成 STA 背景執行緒＋佇列…</p>' },
      { id: 'k4', kind: 'Assistant', status: 'Running', time: t(0), html: '<p>我會把縮圖產生搬到專用的 STA 背景執行緒，並用 LRU 快取限制在 64 MB。先列出要改的地方：</p><ol><li><code>ThumbnailService</code> 改成佇列</li></ol>' },
    ],
    composer: { attachments: [], busy: false, busyText: null, queue: [] },
  };
  const c4 = {
    header: baseHeader('c4', 'a1', '產生三張儀表板示意圖', { lastTurn: '輸入 3,020・輸出 880 tokens' }),
    hasMore: false,
    items: [
      { id: 'm1', kind: 'User', status: 'Done', time: t(130), html: '<p>依照目前的設計，產生三張儀表板示意圖給我挑。</p>' },
      { id: 'm2', kind: 'Tool', status: 'Done', time: t(128), toolKind: 'image', toolName: '產生圖片', title: '儀表板示意圖 ×3',
        media: [
          { url: media(ROOT + '\\assets\\儀表板草稿-A.png'), kind: 'image', path: ROOT + '\\assets\\儀表板草稿-A.png', name: '儀表板草稿-A.png' },
          { url: media(ROOT + '\\assets\\儀表板草稿-B.png'), kind: 'image', path: ROOT + '\\assets\\儀表板草稿-B.png', name: '儀表板草稿-B.png' },
          { url: media(ROOT + '\\assets\\儀表板草稿-C.png'), kind: 'image', path: ROOT + '\\assets\\儀表板草稿-C.png', name: '儀表板草稿-C.png' },
        ] },
      { id: 'm3', kind: 'Assistant', status: 'Done', time: t(127), html: '<p>三張都放在 <code>assets</code> 資料夾了。A 比較緊湊、B 圓環比較大、C 把走勢線放在最上面。</p>' },
    ],
    composer: { attachments: [], busy: false, busyText: null, queue: [] },
  };
  const c5 = {
    header: baseHeader('c5', 'a3', '整理 CLI 自動更新的流程', { lastTurn: '輸入 5,400・輸出 1,020 tokens' }),
    hasMore: false,
    items: [
      { id: 'n1', kind: 'User', status: 'Done', time: t(300), html: '<p>Codex 更新時如果有對話在跑會 EBUSY，要怎麼處理比較好？</p>' },
      { id: 'n2', kind: 'Assistant', status: 'Done', time: t(298), html: '<p>只在沒有任何 codex 程序時更新：</p><ol><li>偵測到新版就先標記「待更新」</li><li>所有對話閒置後再執行 <code>npm install -g @openai/codex@latest</code></li><li>失敗就保留舊版並在 CLI 卡上顯示原因</li></ol>' },
    ],
    composer: { attachments: [], busy: false, busyText: null, queue: [] },
  };
  return { c1, c2, c3, c4, c5 };
}

/** 沒有特別準備內容的對話：簡單的一問一答 */
export function genericConversation(now, summary) {
  return {
    header: baseHeader(summary.id, summary.accountId, summary.title, { lastTurn: '輸入 2,400・輸出 610 tokens' }),
    hasMore: false,
    items: [
      { id: summary.id + '-u', kind: 'User', status: 'Done', time: summary.updatedAt - 60e3, html: '<p>' + summary.title + '：幫我整理重點。</p>' },
      { id: summary.id + '-a', kind: 'Assistant', status: 'Done', time: summary.updatedAt, html: '<p>整理好了，重點有三個：</p><ol><li>先確認需求範圍</li><li>列出要改的檔案</li><li>補上測試再提交</li></ol>' },
    ],
    composer: { attachments: [], busy: false, busyText: null, queue: [] },
  };
}

// ───── 〔熱更新〕CLI 換版（docs/web-protocol.md 第五節） ─────
/** HeaderRestart.state 的 11 種狀態（協定表格的順序） */
export const RESTART_STATES = ['waitingTool', 'waitingUser', 'waitingBackground', 'waitingTurn', 'waitingUpdate', 'restarting', 'continued', 'notContinued', 'failed', 'nextSend', 'appUpdate'];

/** 這些狀態下對話仍在執行中（重開期間 running 維持 true，使用者看到的是同一輪） */
export const RESTART_RUNNING = new Set(['waitingTool', 'waitingUser', 'waitingBackground', 'waitingTurn', 'waitingUpdate', 'restarting', 'continued', 'nextSend', 'appUpdate']);

/** 標頭帶開始等待時間（since）的狀態：還在等的與停下等更新的 */
export const RESTART_WAIT_STATES = new Set(['waitingTool', 'waitingUser', 'waitingBackground', 'waitingTurn', 'waitingUpdate', 'appUpdate']);

/** 宿主在各狀態會產生的 HeaderRestart（說明文字照協定的例子）；o 覆寫任何欄位 */
export function restartHeader(state, o = {}) {
  // since：還在等與停下等更新的狀態才有（CliRestartRules.ShowsWaitTime），這裡是 3 分 25 秒前排定的換版
  const since = Date.now() - (3 * 60 + 25) * 1000;
  const base = { state, from: '2.1.295', to: '2.1.297', detail: null, canApplyNow: false, canContinue: false,
    since: RESTART_WAIT_STATES.has(state) ? since : null };
  const by = {
    waitingTool: { detail: 'CLI 已更新到 2.1.297，等目前的指令（dotnet test Myriagent.slnx -c Release）完成後切換', canApplyNow: true },
    waitingUser: { detail: 'CLI 已更新到 2.1.297，等你回應核准後再切換（舊的卡片在重開後會失效）', canApplyNow: true },
    waitingBackground: { detail: 'CLI 已更新到 2.1.297，這個對話有背景工作（npm run dev）在跑，等它結束後切換', canApplyNow: true },
    waitingTurn: { detail: 'CLI 已更新到 2.1.297，這一輪結束後切換', canApplyNow: true },
    waitingUpdate: { from: '0.160.1', to: null, detail: '等待 Codex 更新完成…（已停下，更新完成後以新版接續）' },
    restarting: { detail: '正在以新版 2.1.297 重新啟動…' },
    continued: { detail: '已用新版 2.1.297 自動接續（中斷時正在執行：npm test，已執行 42 秒）' },
    notContinued: { detail: 'CLI 已更新到 2.1.297；這一輪沒有自動接續：上一個操作〈git push origin main〉結果未知，請確認後按「繼續」或送出新訊息', canContinue: true },
    failed: { detail: '以新版重新啟動失敗：找不到 claude.exe（系統找不到指定的檔案）；按送出繼續' },
    nextSend: { detail: 'CLI 已更新到 2.1.297；這個對話下次送出時才用新版' },
    appUpdate: { from: null, to: null, detail: '等待 Clode Myriagent 更新，更新後自動接續…' },
  };
  return Object.assign(base, by[state] || {}, o);
}

/** CliView 的〔熱更新〕欄位都沒有值的樣子（＝沒有換版要處理） */
export const HOT_IDLE = { changing: false, installBroken: null, lastChange: null, update: null, restartPolicy: 'SafePoint', runningOnOldVersion: 0, restartPending: 0, restartWaiting: [], restartDone: null };

/**
 * 各種換版情境的 CliView（兩個 CLI 一起）：
 * restarting＝Claude 外部更新後 3 個對話換版中（等工具、等你回應、重新啟動中）＋1 個已接續；Codex 安裝不一致
 * draining＝Codex 的 npm 更新在等對話停下；Claude 安裝變動中
 * overdue＝排空超過 10 分鐘（問要等還是現在中斷）
 * installing＝Codex 換檔中；Claude 這一波換版已完成（換版結果）
 * nextSend＝政策是「下次送出時才用新版」：Claude 還有 2 個對話在用舊版
 */
export function hotCli(now, scenario) {
  const cli = makeCli(now);
  const claude = Object.assign(cli.Claude, HOT_IDLE);
  const codex = Object.assign(cli.Codex, HOT_IDLE);
  const external = { from: '2.1.295', to: '2.1.297', origin: 'external', at: now - 4 * MIN };
  const drain = (overdue) => ({
    busy: true, progress: '更新中…', updateAvailable: true,
    update: { phase: 'draining', held: 1, waiting: 2, overdue, startedAt: now - (overdue ? 11 : 3) * MIN },
    runningOnOldVersion: 3, restartPending: 3,
    restartWaiting: [
      { conversationId: 'c2', state: 'waitingUpdate', detail: null },
      { conversationId: 'c5', state: 'waitingTool', detail: 'npm test' },
      { conversationId: 'c8', state: 'waitingTool', detail: 'cargo build --release' },
    ],
  });
  if (scenario === 'restarting') {
    Object.assign(claude, {
      version: '2.1.297', latest: '2.1.297', lastChange: external, runningOnOldVersion: 3, restartPending: 3,
      restartWaiting: [
        { conversationId: 'c1', state: 'waitingTool', detail: 'dotnet test Myriagent.slnx -c Release' },
        { conversationId: 'c6', state: 'waitingUser', detail: null },
        { conversationId: 'c9', state: 'restarting', detail: null },
      ],
      restartDone: { continued: 1, restarted: 2, notContinued: 0, failed: 0 },
    });
    Object.assign(codex, { installBroken: 'Codex 安裝不一致（package.json 0.162.0、執行檔 0.160.1），請修復' });
  } else if (scenario === 'draining' || scenario === 'overdue') {
    Object.assign(codex, drain(scenario === 'overdue'));
    if (scenario === 'draining') Object.assign(claude, { changing: true });
    else Object.assign(claude, { version: '2.1.297', latest: '2.1.297', lastChange: external, restartDone: { continued: 2, restarted: 3, notContinued: 1, failed: 0 } });
  } else if (scenario === 'installing') {
    Object.assign(codex, drain(false), {
      progress: 'npm install -g @openai/codex@latest',
      update: { phase: 'installing', held: 3, waiting: 0, overdue: false, startedAt: now - 12 * MIN },
      restartWaiting: ['c2', 'c5', 'c8'].map((id) => ({ conversationId: id, state: 'waitingUpdate', detail: null })),
    });
    Object.assign(claude, { version: '2.1.297', latest: '2.1.297', lastChange: external, restartDone: { continued: 2, restarted: 3, notContinued: 1, failed: 0 } });
  } else if (scenario === 'nextSend') {
    for (const v of [claude, codex]) v.restartPolicy = 'NextSend';
    Object.assign(claude, { version: '2.1.297', latest: '2.1.297', lastChange: { from: '2.1.295', to: '2.1.297', origin: 'app', at: now - 20 * MIN }, runningOnOldVersion: 2 });
  }
  return cli;
}

/** 換版測試用的對話：被中斷後自動接續的一輪、CLI 重開後失效的卡片、cliUpdate 的 Notice（加在對話清單最前面） */
export function makeHotConversation(now) {
  const t = (minAgo) => now - minAgo * MIN;
  const summary = { id: 'c15', accountId: 'a1', provider: 'Claude', title: 'CLI 換版時正在跑的測試', group: '今天', updatedAt: now - 10e3,
    workingDirectory: ROOT, customFolder: true, running: true, waiting: false, unread: false };
  const restartedOutput = 'CLI 已重新啟動，這張卡已失效；代理會視需要重新提出';
  const items = [
    { id: 'h1', kind: 'User', status: 'Done', time: t(9), html: '<p>跑完整的測試，失敗的話幫我修好，最後 push 到 main。</p>' },
    { id: 'h2', kind: 'Tool', status: 'Done', time: t(8), toolKind: 'bash', toolName: '執行指令', title: 'npm test', detail: 'npm test', output: '  42 passing (3s)' },
    { id: 'h3', kind: 'Approval', status: 'Cancelled', time: t(7), toolKind: 'bash', toolName: '執行指令', title: 'git push origin main', canAllowForSession: true, output: restartedOutput },
    { id: 'h4', kind: 'Question', status: 'Cancelled', time: t(7), answerAsMessage: false, output: restartedOutput,
      questions: [{ id: 'q1', header: '分支', text: '要 push 到哪個分支？', multi: false, allowOther: true, secret: false, options: [{ label: 'main', description: null, preview: null }, { label: 'release', description: null, preview: null }] }] },
    { id: 'h5', kind: 'PlanApproval', status: 'Cancelled', time: t(7), detailHtml: '<p>先修測試、再 push。</p>', output: restartedOutput },
    { id: 'h6', kind: 'Notice', noticeKind: 'cliUpdate', status: 'Done', time: t(6), html: '<p>已用 Claude Code 2.1.297 自動接續（中斷時正在執行：<code>npm test</code>，已執行 42 秒）</p>' },
    { id: 'h7', kind: 'Assistant', status: 'Done', time: t(5), html: '<p>CLI 剛更新並重新啟動。我先確認上一個 <code>npm test</code> 的結果：42 個測試都通過，接著要 push。</p>' },
    { id: 'h8', kind: 'Approval', status: 'Cancelled', time: t(4), toolKind: 'bash', toolName: '執行指令', title: 'git status', canAllowForSession: false },
    { id: 'h9', kind: 'Notice', status: 'Done', time: t(3), html: '<p>已停止這一輪</p>' },
    { id: 'h10', kind: 'Notice', noticeKind: 'cliUpdate', status: 'Done', time: t(2), html: '<p>CLI 已更新到 2.1.297；這一輪沒有自動接續，原因：上一個操作〈git push origin main〉結果未知，請確認後按「繼續」或送出新訊息</p>' },
  ];
  items.forEach((it, i) => { it.seq = i + 1; });
  const data = {
    header: baseHeader('c15', 'a1', summary.title, { running: true, status: '執行指令：dotnet test Myriagent.slnx -c Release', startedAt: now - 95e3, lastTurn: '輸入 9,120・輸出 1,480 tokens' }),
    hasMore: false, items,
    composer: { attachments: [], busy: false, busyText: null, queue: [] },
  };
  return { summary, data };
}

// ───── 檔案樹 ─────
const IMG_NAMES = ['首頁設計-深色.png', '首頁設計-淺色.png', '儀表板草稿-A.png', '儀表板草稿-B.png', '儀表板草稿-C.png', '用量圖表-本週.png',
  '用量圖表-本月.png', '登入畫面-修正後.png', '登入畫面-逾時提示.png', '品牌色票.png', '風景測試圖-山.png', '風景測試圖-海.png'];

export function makeFiles(now, hasVideo) {
  const d = (name, parent, extra = {}) => Object.assign({ name, path: parent + '\\' + name, kind: 'dir', size: null, modified: now - 2 * DAY, thumbUrl: null, url: null, hidden: false }, extra);
  const f = (name, parent, kind, size, ago, extra = {}) => Object.assign({ name, path: parent + '\\' + name, kind, size, modified: now - ago, thumbUrl: null, url: null, hidden: false }, extra);
  const img = (name, parent, size, ago) => { const p = parent + '\\' + name; return f(name, parent, 'image', size, ago, { thumbUrl: thumb(p), url: media(p) }); };
  const vidE = (name, parent, size, ago) => { const p = parent + '\\' + name; return f(name, parent, 'video', size, ago, { thumbUrl: thumb(p), url: hasVideo ? media(p) : null }); };
  const assets = ROOT + '\\assets', src = ROOT + '\\src', app = src + '\\Myriagent', web = app + '\\Web', accts = app + '\\Accounts', docs = ROOT + '\\docs', tests = ROOT + '\\tests', tools = ROOT + '\\tools';
  const tree = {
    [ROOT]: [d('.git', ROOT, { hidden: true }), d('assets', ROOT), d('docs', ROOT), d('src', ROOT), d('tests', ROOT), d('tools', ROOT),
      f('.gitignore', ROOT, 'file', 229, 3 * DAY, { hidden: true }), f('Myriagent.slnx', ROOT, 'code', 231, 3 * DAY), f('Directory.Build.props', ROOT, 'code', 299, 3 * DAY),
      f('README.md', ROOT, 'doc', 4310, 5 * HOUR), f('LICENSE', ROOT, 'file', 1070, 9 * DAY)],
    [assets]: [
      ...IMG_NAMES.map((n, i) => img(n, assets, 120000 + i * 37133, (i + 1) * 3 * HOUR)),
      vidE('登入流程示範.mp4', assets, 1258291, 20 * MIN), vidE('儀表板操作.mp4', assets, 3355443, 2 * DAY),
      f('素材說明.md', assets, 'doc', 1820, 4 * DAY), f('舊版圖示.zip', assets, 'archive', 482113, 12 * DAY)],
    [docs]: [d('plans', docs), f('web-protocol.md', docs, 'doc', 19989, 1 * HOUR), f('protocol-notes.md', docs, 'doc', 6579, 1 * HOUR)],
    [docs + '\\plans']: [f('2026-10-09-myriagent.md', docs + '\\plans', 'doc', 11130, 1 * DAY)],
    [src]: [d('Myriagent', src)],
    [app]: [d('Accounts', app), d('Hub', app), d('Media', app), d('UI', app), d('Web', app), f('Myriagent.csproj', app, 'code', 1838, 1 * DAY), f('Program.cs', app, 'code', 2410, 1 * DAY)],
    [accts]: [f('AccountManager.cs', accts, 'code', 18204, 38 * MIN), f('AccountRules.cs', accts, 'code', 6120, 1 * DAY), f('AgentAccount.cs', accts, 'code', 1990, 1 * DAY)],
    [web]: ['index.html', 'app.css', 'chat.css', 'dashboard.css', 'media.css', 'app.js', 'composer.js', 'conversation.js', 'dashboard.js', 'files.js', 'icons.js', 'items.js', 'settings.js', 'sidebar.js', 'store.js', 'theme.js', 'ui.js', 'util.js', 'viewer.js', 'workspace.js']
      .map((n, i) => f(n, web, 'code', 3000 + i * 1777, (i + 2) * 7 * MIN)),
    [tests]: [d('Myriagent.Tests', tests)],
    [tests + '\\Myriagent.Tests']: [f('AccountRulesTests.cs', tests + '\\Myriagent.Tests', 'code', 8211, 30 * MIN), f('UsageRulesTests.cs', tests + '\\Myriagent.Tests', 'code', 12034, 1 * DAY)],
    [tools]: [d('probes', tools), d('ui-probe', tools)],
  };
  tree[app + '\\Hub'] = [f('AgentHub.cs', app + '\\Hub', 'code', 30422, 2 * HOUR), f('AgentHub.Accounts.cs', app + '\\Hub', 'code', 9001, 2 * HOUR)];
  tree[app + '\\Media'] = [f('ThumbnailService.cs', app + '\\Media', 'code', 7012, 3 * MIN), f('MediaService.cs', app + '\\Media', 'code', 5310, 1 * DAY)];
  tree[app + '\\UI'] = [f('MainForm.cs', app + '\\UI', 'code', 11200, 1 * DAY), f('WebAssets.cs', app + '\\UI', 'code', 3044, 1 * DAY)];
  tree[tools + '\\probes'] = [f('claude_init_probe.py', tools + '\\probes', 'code', 4904, 1 * DAY), f('codex_account_probe.py', tools + '\\probes', 'code', 3501, 1 * DAY)];
  tree[tools + '\\ui-probe'] = [f('package.json', tools + '\\ui-probe', 'code', 420, 1 * HOUR), f('probe.mjs', tools + '\\ui-probe', 'code', 40000, 1 * HOUR), f('fixtures.mjs', tools + '\\ui-probe', 'code', 20000, 1 * HOUR)];
  return tree;
}

export function parentOf(p) {
  const i = p.lastIndexOf('\\');
  if (i <= 2) return /^[A-Za-z]:\\./.test(p) ? p.slice(0, 3) : null;
  return p.slice(0, i);
}

/** @ 檔案搜尋：子字串優先、再子序列，最多 30 筆 */
export function searchFiles(tree, query) {
  const all = [];
  for (const [dir, entries] of Object.entries(tree)) {
    for (const e of entries) {
      if (e.hidden) continue;
      const rel = e.path.slice(ROOT.length + 1);
      all.push({ relativePath: rel, fullPath: e.path, kind: e.kind === 'dir' ? 'dir' : 'file' });
    }
    void dir;
  }
  const q = (query || '').toLowerCase();
  if (!q) return all.filter((x) => x.kind === 'file').slice(0, 12);
  const scored = [];
  for (const x of all) {
    const rel = x.relativePath.toLowerCase();
    const name = rel.split('\\').pop();
    let s = -1;
    if (name.startsWith(q)) s = 0;
    else if (name.includes(q)) s = 1;
    else if (rel.includes(q)) s = 2;
    else {
      let i = 0;
      for (const ch of rel) if (ch === q[i]) i++;
      if (i === q.length) s = 3;
    }
    if (s >= 0) scored.push([s, rel.length, x]);
  }
  scored.sort((a, b) => a[0] - b[0] || a[1] - b[1]);
  return scored.slice(0, 30).map((x) => x[2]);
}

// ───── 測試圖片：程式產生的 PNG（不放二進位檔） ─────
const CRC = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xEDB88320 ^ (c >>> 1) : c >>> 1; t[n] = c >>> 0; }
  return t;
})();
function crc32(buf) { let c = 0xFFFFFFFF; for (let i = 0; i < buf.length; i++) c = CRC[(c ^ buf[i]) & 255] ^ (c >>> 8); return (c ^ 0xFFFFFFFF) >>> 0; }
function chunk(type, data) {
  const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
  const td = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(td));
  return Buffer.concat([len, td, crc]);
}

class Img {
  constructor(w, h) { this.w = w; this.h = h; this.d = Buffer.alloc(w * h * 3); }
  px(x, y, c, a = 1) {
    if (x < 0 || y < 0 || x >= this.w || y >= this.h) return;
    const i = (y * this.w + x) * 3, d = this.d;
    d[i] = d[i] + (c[0] - d[i]) * a; d[i + 1] = d[i + 1] + (c[1] - d[i + 1]) * a; d[i + 2] = d[i + 2] + (c[2] - d[i + 2]) * a;
  }
  vgrad(top, bottom) {
    for (let y = 0; y < this.h; y++) {
      const k = y / (this.h - 1 || 1);
      const c = [top[0] + (bottom[0] - top[0]) * k, top[1] + (bottom[1] - top[1]) * k, top[2] + (bottom[2] - top[2]) * k];
      for (let x = 0; x < this.w; x++) { const i = (y * this.w + x) * 3; this.d[i] = c[0]; this.d[i + 1] = c[1]; this.d[i + 2] = c[2]; }
    }
  }
  rect(x, y, w, h, c, a = 1, r = 0) {
    const x0 = Math.max(0, Math.round(x)), y0 = Math.max(0, Math.round(y)), x1 = Math.min(this.w, Math.round(x + w)), y1 = Math.min(this.h, Math.round(y + h));
    for (let yy = y0; yy < y1; yy++) for (let xx = x0; xx < x1; xx++) {
      if (r) {
        const dx = Math.max(x + r - xx, 0, xx - (x + w - r)), dy = Math.max(y + r - yy, 0, yy - (y + h - r));
        if (dx * dx + dy * dy > r * r) continue;
      }
      this.px(xx, yy, c, a);
    }
  }
  circle(cx, cy, rad, c, a = 1) {
    for (let y = Math.floor(cy - rad); y <= cy + rad; y++) for (let x = Math.floor(cx - rad); x <= cx + rad; x++) {
      const dd = Math.hypot(x - cx, y - cy);
      if (dd <= rad) this.px(x, y, c, a * Math.min(1, rad - dd + 0.5));
    }
  }
  ridge(base, amp, freq, phase, c) {
    for (let x = 0; x < this.w; x++) {
      const top = base - amp * (0.6 * Math.sin(x * freq + phase) + 0.4 * Math.sin(x * freq * 2.7 + phase * 1.7));
      for (let y = Math.max(0, Math.floor(top)); y < this.h; y++) this.px(x, y, c);
    }
  }
  png() {
    const raw = Buffer.alloc((this.w * 3 + 1) * this.h);
    for (let y = 0; y < this.h; y++) { raw[y * (this.w * 3 + 1)] = 0; this.d.copy(raw, y * (this.w * 3 + 1) + 1, y * this.w * 3, (y + 1) * this.w * 3); }
    const ihdr = Buffer.alloc(13);
    ihdr.writeUInt32BE(this.w, 0); ihdr.writeUInt32BE(this.h, 4); ihdr[8] = 8; ihdr[9] = 2; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
    return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(raw, { level: 6 })), chunk('IEND', Buffer.alloc(0))]);
  }
}

function hsl(h, s, l) {
  h = ((h % 360) + 360) % 360; s /= 100; l /= 100;
  const k = (n) => (n + h / 30) % 12, a = s * Math.min(l, 1 - l);
  const f = (n) => l - a * Math.max(-1, Math.min(k(n) - 3, Math.min(9 - k(n), 1)));
  return [Math.round(f(0) * 255), Math.round(f(8) * 255), Math.round(f(4) * 255)];
}

function hashOf(s) { let x = 2166136261; for (const ch of s) { x ^= ch.codePointAt(0); x = Math.imul(x, 16777619); } return x >>> 0; }

/** 依檔名決定畫什麼：介面截圖、圖表或風景；同一個檔名在不同尺寸畫出同一張圖 */
export function renderImage(name, w, h) {
  const seed = hashOf(name);
  const r = rng(seed);
  const hue = seed % 360;
  const img = new Img(w, h);
  const s = w / 1280;
  if (/設計|畫面|截圖|screenshot|設定|登入/.test(name)) {
    const dark = !/淺色/.test(name);
    img.vgrad(dark ? [30, 31, 34] : [246, 246, 248], dark ? [24, 24, 27] : [236, 237, 240]);
    const panel = dark ? [37, 37, 40] : [255, 255, 255];
    const line = dark ? [52, 52, 56] : [222, 222, 228];
    img.rect(0, 0, w, 44 * s, dark ? [24, 24, 24] : [240, 240, 243]);
    img.rect(0, 44 * s, 230 * s, h, dark ? [26, 26, 28] : [248, 248, 250]);
    for (let i = 0; i < 7; i++) img.rect(18 * s, (70 + i * 40) * s, 190 * s, 24 * s, i === 1 ? hsl(hue, 55, dark ? 30 : 88) : line, 1, 6 * s);
    for (let i = 0; i < 3; i++) {
      const x = (260 + i * 330) * s;
      img.rect(x, 80 * s, 300 * s, 230 * s, panel, 1, 12 * s);
      img.circle(x + 150 * s, 180 * s, 62 * s, line);
      img.circle(x + 150 * s, 180 * s, 50 * s, panel);
      img.rect(x + 40 * s, 270 * s, 220 * s, 10 * s, hsl(hue + i * 40, 60, 55), 1, 5 * s);
    }
    img.rect(260 * s, 340 * s, 990 * s, 380 * s, panel, 1, 12 * s);
    for (let i = 0; i < 6; i++) img.rect(290 * s, (370 + i * 52) * s, (380 + r() * 520) * s, 18 * s, line, 1, 6 * s);
    if (/逾時/.test(name)) img.rect(440 * s, 300 * s, 520 * s, 120 * s, hsl(38, 85, dark ? 45 : 60), 0.92, 14 * s);
  } else if (/圖表|chart|用量|色票/.test(name)) {
    img.vgrad([22, 24, 30], [16, 17, 21]);
    for (let i = 0; i < 6; i++) img.rect(70 * s, (110 + i * 100) * s, 1140 * s, 1.5 * s, [46, 48, 56]);
    const n = /色票/.test(name) ? 10 : 14;
    for (let i = 0; i < n; i++) {
      const bh = (/色票/.test(name) ? 460 : 120 + r() * 440) * s;
      const x = (90 + i * (1110 / n)) * s;
      img.rect(x, 650 * s - bh, (1110 / n - 18) * s, bh, hsl(/色票/.test(name) ? i * 36 : hue + i * 5, 62, 56), 1, 6 * s);
    }
  } else {
    img.vgrad(hsl(hue, 55, 72), hsl(hue + 30, 60, 90));
    img.circle(w * 0.72, h * 0.3, 70 * s, [255, 244, 214], 0.95);
    img.ridge(h * 0.62, 70 * s, 0.006 / s, seed % 7, hsl(hue + 180, 25, 52));
    img.ridge(h * 0.74, 50 * s, 0.009 / s, seed % 5, hsl(hue + 190, 30, 38));
    if (/海/.test(name)) img.rect(0, h * 0.8, w, h * 0.2, hsl(205, 60, 40), 0.9);
    img.ridge(h * 0.88, 30 * s, 0.013 / s, seed % 3, hsl(hue + 200, 35, 24));
  }
  return img.png();
}

// ───── app 自我更新（docs/web-protocol.md 的 AppUpdateView） ─────
/** 版號前面的日期時間（init.app.commitAt、AppUpdateView.commitAt）：今年的 10/23 14:05（版號旁省略年份），探針哪一年跑都一樣 */
export const COMMIT_AT = new Date(new Date().getFullYear(), 9, 23, 14, 5).getTime();
/** COMMIT_AT 的完整寫法（提示、讀屏、設定頁） */
export const COMMIT_FULL = `${new Date().getFullYear()}/10/23 14:05`;

/** 宿主在各階段送的 AppUpdateView；o 覆寫任何欄位 */
export function appUpdate(phase, o = {}) {
  const base = {
    version: '0.2.1523', build: 'ab12cd3', display: '0.2.1523（ab12cd3）', commitAt: COMMIT_AT, managed: true, canInstall: false, phase,
    available: null, staged: null, downloaded: 0, total: 0, detail: null, blocker: null, waiting: 0,
    lastCheckAt: Date.now() - 5 * 60000, lastResult: '已更新到 0.2.1523', lastResultAt: Date.now() - 86400000,
  };
  const by = {
    idle: { detail: '已是最新版' },   // AppUpdateManager.SetIdle：檢查完沒有新版時的 detail
    downloading: { available: '0.2.1530', downloaded: 41_000_000, total: 96_000_000 },
    staged: { available: '0.2.1530', staged: '0.2.1530' },
    draining: { available: '0.2.1530', staged: '0.2.1530', waiting: 2 },
    blocked: { available: '0.2.1530', detail: '新版需要 Microsoft.WindowsDesktop.App 10.0.3 以上，請先安裝（https://dotnet.microsoft.com/download/dotnet）' },
    disabled: { managed: false, canInstall: true, detail: '開發版（不是從安裝位置執行），不自動更新' },
  };
  return Object.assign(base, by[phase] || {}, o);
}
