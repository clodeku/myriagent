// 同步畫面的測試資料（WP3.5 的 remote-probe.mjs 用）：瀏覽器形式的 init（伺服器的 /app/）、桌面已配對的 init 與 sync／devices 序列、
// remoteOnly 帳戶、可用裝置碼登入的 Codex 帳戶。形狀照 docs/web-protocol.md 第三節（AccountView、AccountDeviceView、DeviceView、
// DeviceCliView、SyncStatus）與伺服器的 src/MyriagentHub/Sync/AccountViewRules.cs。時間都相對於傳入的 now；沒有亂數。
// 不可信的字串（帳戶名、email、statusText、裝置名）帶 XSS，畫面一律要照字面顯示。

export const ORIGIN = 'https://hub.probe.local';
export const DESKTOP_ORIGIN = 'https://myriagent.local';
/** 會執行就設 window.__xss 的 HTML：畫面只能把它當成文字 */
export const XSS = '<img src=x onerror=window.__xss=1>';

const MIN = 60e3;
const HOUR = 3600e3;
const DAY = 86400e3;

// ───── 用量（短歷史：每 2 小時一點、一天） ─────

function history(now, current) {
  const pts = [];
  for (let i = 12; i >= 1; i--) pts.push([now - i * 2 * HOUR, Math.max(0, Math.round((current * (12 - i)) / 12))]);
  pts.push([now, current]);
  return pts;
}

function sev(p) {
  return p >= 95 ? 'max' : p >= 80 ? 'high' : p >= 50 ? 'warn' : 'ok';
}

function win(now, key, label, percent, resetIn) {
  return { key, label, percent, resetsAt: now + resetIn, severity: sev(percent), history: history(now, percent) };
}

/** 伺服器的共享用量（AccountViewRules.Usage）：source 只有 api／session／null */
export function sharedUsage(now, provider, percent = 42) {
  if (provider === 'Codex') {
    return { fetchedAt: now - 3 * MIN, stale: false, source: 'api', error: null, windows: [win(now, 'codex:primary', '每週', percent, 4 * DAY)], credits: null, note: null };
  }
  return {
    fetchedAt: now - 2 * MIN, stale: false, source: 'api', error: null,
    windows: [win(now, 'session', '5 小時', percent, 2 * HOUR), win(now, 'weekly', '每週', Math.min(100, percent + 21), 3 * DAY)],
    credits: null, note: null,
  };
}

// ───── 電腦（DeviceView）─────

/** 兩台未撤銷（A 在線、B 離線，B 的名稱帶 XSS、改過名）與一台已撤銷（排最後） */
export function browserDevices(now) {
  return [
    {
      id: 'd_A', name: 'OFFICE-PC', signedName: 'OFFICE-PC', self: false, machineName: 'OFFICE-PC', appVersion: '0.3.0', fingerprint: 'ABCD-EFGH',
      keyStorage: 'tpm', online: true, lastSeenAt: now - MIN, remoteControlLevel: 'Off', allowRemoteFullAccess: false, verified: null, revoked: null,
    },
    {
      id: 'd_B', name: '筆電 ' + XSS, signedName: 'LAPTOP', self: false, machineName: 'LAPTOP-7', appVersion: '0.3.0', fingerprint: 'JKMN-PQRS',
      keyStorage: 'software', online: false, lastSeenAt: now - 3 * HOUR, remoteControlLevel: 'View', allowRemoteFullAccess: false, verified: null, revoked: null,
    },
    {
      id: 'd_R', name: '舊桌機', signedName: '舊桌機', self: false, machineName: 'OLD-PC', appVersion: '0.2.0', fingerprint: 'TVWX-YZ12',
      keyStorage: 'software', online: false, lastSeenAt: now - 20 * DAY, remoteControlLevel: 'Off', allowRemoteFullAccess: false, verified: null,
      revoked: { at: now - 10 * DAY, reason: 'retired' },
    },
  ];
}

/** 一格（AccountDeviceView）：預設是那台在線、身分相符、已登入 */
function cell(device, status, extra = {}) {
  return Object.assign({
    deviceId: device.id, deviceName: device.name, self: false, status,
    statusText: { ready: '已登入', notLoggedIn: '尚未登入', notInstalled: '未安裝 CLI', checking: '檢查中', error: '發生錯誤', apiKey: 'API Key 登入', absent: '尚未加入' }[status] || status,
    email: null, plan: null, accessExpiresAt: null, loginExpiresAt: null, identityOk: true, usagePoller: false, online: device.online, checkedAt: null,
  }, extra);
}

const sessions0 = { open: 0, running: 0, waiting: 0 };

/** 瀏覽器形式的帳戶（邏輯帳戶，id＝logicalId）：6 列 × 2 台，涵蓋矩陣每一種格子 */
export function browserAccounts(now) {
  const [A, B] = browserDevices(now);
  const acct = (id, provider, label, color, status, statusText, devices, extra = {}) => Object.assign({
    id, provider, label, color, isSystemDefault: false, configDirectory: null, email: null, plan: null, status, statusText,
    safeMode: false, loginBusy: false, usage: null, sessions: sessions0, logicalId: id, remoteOnly: false, devices,
  }, extra);
  return [
    acct('la_c1', 'Claude', '工作用 Max', '#D97757', 'ready', '2 台已登入（1 台在線）', [
      cell(A, 'ready', { email: 'lin.dev@example.com', plan: 'Claude Max', usagePoller: true, accessExpiresAt: now + 5 * HOUR, loginExpiresAt: now + 20 * DAY, checkedAt: now - MIN }),
      cell(B, 'ready', { email: 'lin.dev@example.com', plan: 'Claude Max', loginExpiresAt: now + 2 * DAY, checkedAt: now - 3 * HOUR }),
    ], { email: 'lin.dev@example.com', plan: 'Claude Max', usage: sharedUsage(now, 'Claude', 34), sessions: { open: 2, running: 1, waiting: 0 } }),
    acct('la_c2', 'Claude', 'XSS ' + XSS, '#A872E0', 'ready', XSS, [
      cell(A, 'ready', { identityOk: false, email: XSS + '@example.com', checkedAt: now - MIN }),
      cell(B, 'ready', { email: 'x@example.com', accessExpiresAt: now - MIN, loginExpiresAt: now + 30 * DAY }),
    ], { email: XSS }),
    acct('la_x1', 'Codex', 'Codex Pro', '#1FAE8E', 'notLoggedIn', '沒有電腦登入', [
      cell(A, 'notLoggedIn'),
      cell(B, 'absent', { statusText: '尚未加入' }),
    ]),
    acct('la_c3', 'Claude', '個人 Pro', '#4C8DF6', 'error', '1 台有問題', [
      cell(A, 'error', { statusText: '讀不到憑證 ' + XSS }),
      cell(B, 'checking'),
    ]),
    acct('la_c4', 'Claude', 'API 帳戶', '#E0B341', 'ready', '1 台已登入（0 台在線）', [
      cell(A, 'apiKey'),
      cell(B, 'ready', { email: 'api@example.com', loginExpiresAt: now - HOUR }),
    ]),
    acct('la_x2', 'Codex', 'Codex 團隊', '#E06C75', 'ready', '1 台已登入（0 台在線）', [
      cell(A, 'notInstalled'),
      cell(B, 'ready', { email: 'team@example.com', usagePoller: true }),
    ], { usage: sharedUsage(now, 'Codex', 18) }),
  ];
}

/** 各電腦的 CLI（DeviceCliView）：A 都是最新、Codex 有新版；B 的 Claude 舊版執行中、Codex 沒裝 */
export function browserDevicesCli(now) {
  const v = (o) => Object.assign({ installed: true, version: null, installKind: 'native', latest: null, updateAvailable: false, runningOnOldVersion: 0, restartPending: 0, installBroken: null, checkedAt: now - 5 * MIN }, o);
  return {
    d_A: { Claude: v({ version: '2.1.0', latest: '2.1.0' }), Codex: v({ version: '0.50.0', latest: '0.51.0', updateAvailable: true, installKind: 'npm' }) },
    d_B: { Claude: v({ version: '2.0.9', latest: '2.1.0', updateAvailable: true, runningOnOldVersion: 1, restartPending: 2 }), Codex: v({ installed: false, version: null, installKind: null }) },
  };
}

export const BROWSER_PROVIDERS = [
  { key: 'Claude', name: 'Claude Code', isolatedAccounts: true, safeMode: false },
  { key: 'Codex', name: 'Codex', isolatedAccounts: true, safeMode: false },
];

export const BROWSER_SETTINGS = { theme: 'dark', fontSize: 14, enterToSend: true, notifications: true };

/**
 * 伺服器送的 init（web-protocol 第六節第 8 點）。o.mode：'main'｜'popout'；o.accounts：換掉帳戶清單（例如重連後帳戶變少）；
 * o.layout：預設 null（由傳輸層從 localStorage 注入）；o.providers：換掉代理種類清單（真的伺服器 2026-10-22 起送四種，這裡預設只有 Claude、Codex）
 */
export function browserInit(now, o = {}) {
  return {
    type: 'init',
    mode: o.mode === 'popout' ? 'popout' : 'main',
    popoutConversationId: null,
    app: { version: '0.2.0' },
    settings: Object.assign({}, BROWSER_SETTINGS, o.settings || {}),
    accounts: o.accounts || browserAccounts(now),
    conversations: [],
    cli: {},
    providers: o.providers || BROWSER_PROVIDERS,
    devicesCli: o.devicesCli === undefined ? browserDevicesCli(now) : o.devicesCli,
    layout: o.layout === undefined ? null : o.layout,
    remote: { mode: 'browser', deviceId: null, devices: o.devices || browserDevices(now), sync: null },
  };
}

// ───── 桌面已配對 ─────

export const DESKTOP_SETTINGS = {
  theme: 'dark', fontSize: 14, closeToTray: true, notifications: true, voiceNotifications: true, autoUpdateCli: true, enterToSend: true, claudeRemoteControl: true, claudeIdleTokenRefresh: true, autoSwitchAccount: true, autoSwitchCrossProvider: false, shareCrossCliCustomizations: false, cliRestartPolicy: 'SafePoint',
  instructions: '回覆一律使用臺灣繁體中文。',
};

export const DESKTOP_PROVIDERS = [
  { key: 'Claude', name: 'Claude Code', isolatedAccounts: true, safeMode: true },
  { key: 'Codex', name: 'Codex', isolatedAccounts: true, safeMode: true },
  { key: 'Antigravity', name: 'Antigravity', isolatedAccounts: false, safeMode: false },
  { key: 'Grok', name: 'Grok Build', isolatedAccounts: true, safeMode: false },
];

export function desktopCli(now) {
  const c = (provider, version, installKind) => ({
    provider, installed: true, version, latest: version, updateAvailable: false, installKind, path: null, autoUpdate: true, busy: false, progress: null,
    lastCheckedAt: now - 12 * MIN, checkedAt: now - 12 * MIN, error: null,
  });
  return { Claude: c('Claude', '2.1.0', 'native'), Codex: c('Codex', '0.50.0', 'npm'), Antigravity: c('Antigravity', '1.3.5', 'native'), Grok: c('Grok', '1.0.52', 'native') };
}

/** 這台（d_A）的 DeviceView：self、verified:false（這台採用的名冊裡沒有這一筆）；其他兩台照瀏覽器的 */
export function desktopDevices(now) {
  const [A, B, R] = browserDevices(now);
  return [Object.assign({}, A, { self: true, verified: false }), Object.assign({}, B, { verified: true }), Object.assign({}, R, { verified: true })];
}

export function syncPairing(now) {
  return {
    state: 'pairing', detail: null, server: ORIGIN, pending: 0, lastSyncAt: null, clockSkewMs: null, deviceId: null, deviceFingerprint: 'ABCD-EFGH', hubFingerprint: null,
    pairing: { userCode: 'MTLZ-XFCH', verificationUri: ORIGIN + '/#pair', verificationUriComplete: ORIGIN + '/#pair=MTLZ-XFCH', expiresAt: now + 10 * MIN, error: null },
    confirm: null,
  };
}

export function syncOnline(now, extra = {}) {
  return Object.assign({
    state: 'online', detail: null, server: ORIGIN, pending: 0, lastSyncAt: now - MIN, clockSkewMs: null, deviceId: 'd_A', deviceFingerprint: 'ABCD-EFGH',
    hubFingerprint: 'HUBF-INGE-RPRI-NT16', pairing: null, confirm: null,
  }, extra);
}

/** 桌面的本機帳戶：兩個連結到邏輯帳戶（devices 是陣列、self 格是這台），一個沒連結（devices: null） */
export function desktopAccounts(now) {
  const [A, B] = desktopDevices(now);
  const local = (id, provider, label, color, logicalId, devices, extra = {}) => Object.assign({
    id, provider, label, color, isSystemDefault: false, configDirectory: 'C:\\Users\\Lin\\AppData\\Local\\Myriagent\\accounts\\' + id,
    email: null, plan: null, status: 'ready', statusText: '已登入', safeMode: false, loginBusy: false, usage: null, sessions: sessions0,
    logicalId, remoteOnly: false, devices,
  }, extra);
  return [
    local('a1', 'Claude', '工作用 Max', '#D97757', 'la_c1', [
      cell(A, 'ready', { self: true, email: 'lin.dev@example.com', usagePoller: true }),
      cell(B, 'ready', { email: 'lin.dev@example.com', loginExpiresAt: now + 2 * DAY }),
    ], { email: 'lin.dev@example.com', plan: 'Claude Max', usage: sharedUsage(now, 'Claude', 34) }),
    local('a2', 'Codex', 'Codex Pro', '#1FAE8E', 'la_x1', [
      cell(A, 'ready', { self: true, email: 'lin.dev@example.com' }),
      cell(B, 'absent'),
    ], { email: 'lin.dev@example.com' }),
    local('a3', 'Claude', '個人 Pro（沒連結）', '#4C8DF6', null, null, { email: 'lin.home@example.com' }),
  ];
}

/**
 * remoteOnly 帳戶（目錄裡有、這台沒有）：一個有共享用量、一個沒有。id 等於 logicalId、status 為 notLoggedIn、configDirectory 為 null。
 * 共享用量照桌面的 SyncViewRules.UsageView：source 一律 shared、沒有歷史
 */
export function remoteOnlyAccounts(now) {
  const [A, B] = desktopDevices(now);
  const shared = (provider, percent) => {
    const u = sharedUsage(now, provider, percent);
    return Object.assign(u, { source: 'shared', windows: u.windows.map((w) => Object.assign({}, w, { history: [] })) });
  };
  const ro = (id, provider, label, color, usage) => ({
    id, provider, label, color, isSystemDefault: false, configDirectory: null, email: null, plan: null, status: 'notLoggedIn', statusText: '這台未登入',
    safeMode: false, loginBusy: false, usage, sessions: sessions0, logicalId: id, remoteOnly: true,
    devices: [cell(A, 'absent', { self: true }), cell(B, 'ready', { email: 'remote@example.com', usagePoller: true })],
  });
  return [ro('la_ro1', 'Claude', '只在筆電（有用量）', '#C678DD', shared('Claude', 55)), ro('la_ro2', 'Codex', '只在筆電（沒有用量）', '#56B6C2', null)];
}

/** 可以用裝置碼登入的 Codex 帳戶（WP3.4 的 deviceCodeLogin） */
export function deviceCodeAccount() {
  return {
    id: 'a9', provider: 'Codex', label: 'Codex 裝置碼', color: '#98C379', isSystemDefault: false, configDirectory: 'C:\\Users\\Lin\\AppData\\Local\\Myriagent\\accounts\\a9',
    email: null, plan: null, status: 'notLoggedIn', statusText: '尚未登入', safeMode: false, loginBusy: false, usage: null, sessions: sessions0,
    logicalId: null, remoteOnly: false, devices: null, deviceCodeLogin: true,
  };
}

/** 桌面的 init。o.remote：預設 null（未配對）；o.accounts、o.devicesCli 可換 */
export function desktopInit(now, o = {}) {
  return {
    type: 'init', mode: 'main', popoutConversationId: null, app: { version: '0.3.0' },
    settings: DESKTOP_SETTINGS, accounts: o.accounts || desktopAccounts(now), conversations: [], cli: desktopCli(now), providers: DESKTOP_PROVIDERS,
    devicesCli: o.devicesCli === undefined ? null : o.devicesCli,
    layout: { panes: [{ id: 'p1', tabs: [], active: null }], focusedPane: 'p1', sidebarCollapsed: false, view: 'dashboard' },
    remote: o.remote === undefined ? null : o.remote,
  };
}

export function desktopRemote(now) {
  return { mode: 'desktop', deviceId: 'd_A', devices: desktopDevices(now), sync: syncOnline(now) };
}

/** 未配對啟動之後：開始配對 → 配對完成（sync.deviceId 有值）→ devices（含 self、verified:false）→ 離線（12 筆待送） */
export function desktopSyncSequence(now) {
  return [
    { type: 'sync', sync: syncPairing(now) },
    { type: 'sync', sync: syncOnline(now) },
    { type: 'devices', devices: desktopDevices(now) },
    { type: 'sync', sync: syncOnline(now, { state: 'offline', pending: 12, detail: '伺服器連不上，30 秒後重試' }) },
    { type: 'cli', cli: desktopCli(now), devicesCli: browserDevicesCli(now) },
  ];
}
