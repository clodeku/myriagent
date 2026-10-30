// 共用狀態與宿主橋接。狀態的真相在 C#（AgentHub；瀏覽器版是伺服器），這裡只保留畫面需要的副本。
// 收送一律經 transport.js（桌面 WebView2／瀏覽器 hub／都沒有），其他模組只用這裡的 post、postWithFiles、onHostMessage、hasBridge。
import * as transport from './transport.js';
import { wantsPeerId, originOf, ulid } from './liverules.js';

/**
 * 送指令給宿主。〔桌面遠端操作〕對其他電腦的對話（副本）或其他電腦的帳戶、CLI 下的指令先帶上 commandId（liverules.wantsPeerId）：
 * 宿主照型錄交給同步，結果以 commandResult 回來，回傳值就是這個 commandId（同瀏覽器版）
 */
export function post(msg) {
  if (!isHub && msg && typeof msg === 'object') {
    const conv = typeof msg.conversationId === 'string' ? state.convMap.get(msg.conversationId) : null;
    const own = state.remote && typeof state.remote === 'object' ? state.remote.deviceId : null;
    if (wantsPeerId(msg, 'desktop', conv ? originOf(conv, 'desktop') : null, own)) msg = Object.assign({}, msg, { commandId: peerId() });
  }
  return transport.post(msg);
}

/** 〔桌面遠端操作〕commandId（ULID） */
export function peerId() {
  const bytes = new Uint8Array(10);
  if (window.crypto && typeof window.crypto.getRandomValues === 'function') window.crypto.getRandomValues(bytes);
  return ulid(Date.now(), bytes);
}

/** 附 File 物件送給宿主（拖放／貼上檔案）；送不出去（瀏覽器版、沒有宿主）回 false */
export function postWithFiles(msg, files) {
  return transport.postWithFiles(msg, files);
}

/** 收宿主訊息；兩種傳輸都可以註冊多個訂閱者 */
export function onHostMessage(fn) {
  transport.onMessage(fn);
}

export const hasBridge = transport.available;
export const newId = transport.newId; // 〔遠端〕瀏覽器版產生 ULID（上傳的 uploadId）；其他傳輸是 null
/** 瀏覽器版（伺服器的 /app/，經 hub 連線） */
export const isHub = transport.kind === 'hub';

const params = new URLSearchParams(location.search);
// 宿主送出 index.html 時已把 <html data-theme> 換成目前的主題：init 到達前就沿用它，淺色主題才不會先畫一次深色
const initialTheme = document.documentElement.dataset.theme === 'light' ? 'light' : 'dark';

export const state = {
  mode: params.get('mode') === 'popout' ? 'popout' : 'main',
  popoutId: params.get('conversation') || null,
  /** 〔多視窗〕桌面的完整視窗是主視窗（'primary'）還是另外開的視窗（'extra'，搬走最後一個分頁時自己關掉）；init 的 windowRole，其他是 null */
  windowRole: null,
  ready: false,
  app: { version: '' },
  settings: { theme: initialTheme, fontSize: 14, closeToTray: true, notifications: true, voiceNotifications: true, autoUpdateCli: true, autoUpdateApp: true, enterToSend: true, claudeRemoteControl: true, claudeIdleTokenRefresh: true, autoSwitchAccount: true, autoSwitchCrossProvider: false, shareCrossCliCustomizations: false, instructions: '' },
  accounts: [],
  accountMap: new Map(),
  conversations: [],
  convMap: new Map(),
  /** 支援的 CLI（init 的 providers，順序＝宿主的 AgentProviders.All）：{ key, name（產品名稱）, isolatedAccounts, safeMode, sharedCustomizations } */
  providers: [],
  cli: {},
  /** app 自我更新的狀態（AppUpdateView；init.appUpdate、appUpdate 訊息）；宿主還沒送過為 null（瀏覽器版一直是 null） */
  appUpdate: null,
  // 瀏覽器版：init 之前是儀表板；第一則 init 有存下的版面時照它的 view，沒有時留在儀表板（WP4.6 D10）
  view: isHub ? 'dashboard' : 'chat',
  sidebarCollapsed: false,
  /** 電腦狀態面板（monitor.js）：'open'／'closed'＝使用者選過（存在版面的 monitor）；null＝沒選過，視窗夠寬才顯示 */
  monitorPref: null,
  /** 帳戶登入提示（accountLogin 訊息）：accountId → { message, done, success, at } */
  login: new Map(),
  /**
   * 側欄歷史篩選：帳戶（null＝所有帳戶）、電腦（null＝所有電腦、'local'＝桌面這台、其他＝那台的 deviceId；存在版面的 historyScope／historyDevice）
   * 與工作區（null＝所有工作區、'default'＝預設工作區、其他＝'dir:'＋路徑的比較鍵，見 liverules.folderKeyOf；不存進版面）
   */
  filterAccount: null,
  filterDevice: null,
  filterFolder: null,
  search: '',
  /**
   * 〔同步〕init.remote 與之後的 sync／devices（web-protocol 第一節）：undefined＝還沒收到 init、null＝沒有同步（未配對）、
   * 其他是 { mode: 'desktop'|'browser', deviceId, devices: [DeviceView], sync: SyncStatus|null }（桌面的同步狀態在 remote.sync）
   */
  remote: undefined,
  /** 〔同步〕各電腦的 CLI：{ deviceId: { Claude?, Codex? } }（init 與 cli 的 devicesCli）；沒有是 null */
  devicesCli: null,
  /** 瀏覽器版的連線狀態（transport-hub.js 產生的 connection 訊息）：{ state, reason, retryAt }；還沒收到是 null */
  connection: null,
  /** 瀏覽器版：目前這條連線收到過 init */
  hubInit: false,
  /** 各帳戶的 token 計數（init.tokens 與 tokens 訊息，web-protocol 的 Tokens）：{ day, scope?, accounts: { id: { today, week, month } } }；舊宿主沒有，是 null */
  tokens: null,
  /** 儀表板的健康度（HealthView；init.health、health 訊息）：只有桌面，宿主還沒送過或瀏覽器版是 null */
  health: null,
  // 排程頁（schedules.js）：宿主的 ScheduleView（只有桌面；沒掛讀取器是 null）
  schedules: null,
  /** 多人協作（collab.js）：init.viewer（{ role: 'owner'|'collaborator', name?, userName? }）；舊宿主沒有，是 null（當成擁有者） */
  viewer: null,
  /** 多人協作：擁有者看的協作人清單與分享（init.collaborators、collaborators 訊息：{ list, shares }）；未配對、協作人、舊宿主是 null */
  collaborators: null,
  /** 儀表板各 Claude 帳戶的 MCP 伺服器（mcp 訊息，web-protocol 的 mcp）：只有桌面，宿主還沒送過或瀏覽器版是 null */
  mcp: null,
  /** 儀表板的每週額度每 5% 的 token（usageSegments 訊息，web-protocol 的 UsageSegmentsView）：只有桌面，宿主還沒送過或瀏覽器版是 null */
  usageSegments: null,
  // 輸入區的語音訊息（voicemsg.js）：桌面這台有辨識程序（init 的 voiceInput）；瀏覽器版由對話所在的電腦辨識，不看它
  voiceInput: false,
};

export function setAccounts(list) {
  state.accounts = Array.isArray(list) ? list : [];
  state.accountMap = new Map(state.accounts.map((a) => [a.id, a]));
}

export function setConversations(list) {
  state.conversations = Array.isArray(list) ? list : [];
  state.convMap = new Map(state.conversations.map((c) => [c.id, c]));
}

/** 宿主沒送 providers 時（舊的探針資料）只認得 Claude、Codex */
const FALLBACK_PROVIDERS = [
  { key: 'Claude', name: 'Claude Code', isolatedAccounts: true, safeMode: true },
  { key: 'Codex', name: 'Codex', isolatedAccounts: true, safeMode: true },
];

export function setProviders(list) {
  const ok = Array.isArray(list) ? list.filter((p) => p && typeof p.key === 'string' && p.key) : [];
  state.providers = ok.length ? ok : FALLBACK_PROVIDERS;
}

/** 支援的 CLI 的 key（'Claude'、'Codex'、'Antigravity'、'Grok'），依宿主的順序 */
export const providerKeys = () => state.providers.map((p) => p.key);
/** 這種 CLI 的資訊；不認得的 key 給保守的預設（不能新增隔離帳戶、沒有精簡模式） */
export const providerInfo = (key) => state.providers.find((p) => p.key === key) || { key, name: key || 'CLI', isolatedAccounts: false, safeMode: false };

export const accountOf = (id) => (id ? state.accountMap.get(id) : null) || null;
/** 多人協作：這份網頁的使用者是協作人（init.viewer.role；只看分享給他的對話與自己的介面設定，collab.js） */
export const isCollaborator = () => !!state.viewer && state.viewer.role === 'collaborator';
export const summaryOf = (id) => state.convMap.get(id) || null;

// ───── 簡易事件匯流排 ─────
const handlers = new Map();
export function on(evt, fn) {
  if (!handlers.has(evt)) handlers.set(evt, new Set());
  handlers.get(evt).add(fn);
  return () => handlers.get(evt).delete(fn);
}
export function emit(evt, ...args) {
  const set = handlers.get(evt);
  if (set) for (const fn of set) fn(...args);
}

let reqSeq = 0;
export const nextRequestId = (prefix = 'r') => prefix + (++reqSeq);

export const EFFORT_LABEL = { low: '低', medium: '中', high: '高', xhigh: '超高', max: '最高', ultra: '極限', minimal: '最低', ultracode: 'Ultracode' };

/** 推理強度選單的說明（只有不看名稱猜不出來的才有） */
export const EFFORT_DESC = { ultracode: '超高強度，並可用 Workflow 同時派多個代理' };

export const STATUS_LABEL = {
  checking: '檢查中', ready: '已登入', notLoggedIn: '尚未登入', apiKey: 'API Key 登入', notInstalled: '未安裝 CLI', error: '發生錯誤',
};

/**
 * 權限模式的色調。協定只給 value／label，這裡依 value（退而用 label）推斷：
 * 每步詢問＝neutral、自動接受編輯＝edit（紫）、規劃＝plan（青）、自動＝auto（藍）、完全自動＝danger（紅）
 */
export function permissionTone(mode) {
  const v = String((mode && mode.value) || mode || '').toLowerCase();
  const label = String((mode && mode.label) || '');
  if (/full|bypass|danger|never/.test(v) || label.includes('完全')) return 'danger';
  if (/accept|edit/.test(v) || label.includes('接受編輯')) return 'edit';
  if (/plan/.test(v) || label.includes('規劃')) return 'plan';
  if (/auto|on-request/.test(v) || label.includes('自動')) return 'auto';
  return 'neutral';
}
