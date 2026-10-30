// 網頁即時檢視的測試資料（WP4.6 計畫 docs/plans/2026-10-19-wp4.6-web-live.md 步 3 起；live-probe.mjs 用，WP4.9 的全流程探針也可以直接重用）。
// 形狀照 docs/web-protocol.md：
// - 瀏覽器（伺服器送的）：ConversationSummary／Header 帶 P5 的 deviceId、deviceName、deviceOnline（src/MyriagentHub/Realtime/ConversationViewRules.cs）；
//   卡片（Approval、Question、PlanApproval）帶 requestDigest；Composer 的附件一律空的、帶 queueMore；佔位項目；媒體是本站的相對網址 /m/、/t/。
// - 伺服器草稿：isDraft、標題「新對話」、標頭帶網頁選的 deviceId（步 7 由伺服器填；名稱與在線由網頁從 remote.devices 查）。
// - 桌面：其他電腦的唯讀副本（清單帶 deviceId；標頭帶 replica、accountMissing:false、recentFolders 與 commands 是空陣列，D22）、
//   有接收者的本機對話的 conversationSync（D25）、SyncStatus 的 backfill／remoteSupported／local.notifyRemoteActions。
// 時間都相對於傳入的 now；沒有亂數。不可信的字串（電腦名稱、標題、帳戶名）帶 XSS，畫面一律要照字面顯示。
import { XSS, browserInit, desktopInit, syncOnline, sharedUsage } from './remote-fixtures.mjs';
import { AGY_MODES, CLAUDE_MODES, CODEX_MODES, GROK_MODES } from './fixtures.mjs';

export { XSS };

export const MIN = 60e3;
export const HOUR = 3600e3;
export const DAY = 86400e3;

// ───── id（線上的對話 id 是 32 個小寫十六進位；伺服器草稿是 d 加 26 字 Crockford）─────

/** 第 n 個對話的 id（32 個小寫十六進位，可以重現） */
export const convId = (n) => (0x5f3a0000 + n).toString(16).padStart(8, '0') + 'c0ffee00'.repeat(3);
/** 第 n 份伺服器草稿的 id */
export const draftId = (n) => 'd01J9QDRAFT' + String(n).padStart(16, '0');

export const CONV = {
  /** OFFICE-PC 上執行中、有等你核准的卡片 */
  running: convId(1),
  /** OFFICE-PC 上閒置、有未讀 */
  idle: convId(2),
  /** 筆電（離線）上的對話；標題帶 XSS */
  offline: convId(3),
  /** HOME-PC（政策 View）上的對話 */
  view: convId(4),
  /** Codex 對話（完全自動） */
  codex: convId(5),
  /** 不在清單裡（超過 1000 筆被擠出、還開在舊分頁）的對話 */
  unlisted: convId(6),
};
export const DRAFT = draftId(1);

/** 三台未撤銷的電腦（DeviceView）＋一台已撤銷 */
export const DEVICE = { control: 'd_A', offline: 'd_B', view: 'd_V', revoked: 'd_R' };

export function liveDevices(now) {
  return [
    {
      id: DEVICE.control, name: 'OFFICE-PC', signedName: 'OFFICE-PC', self: false, machineName: 'OFFICE-PC', appVersion: '0.4.0', fingerprint: 'ABCD-EFGH',
      keyStorage: 'tpm', online: true, lastSeenAt: now - MIN, remoteControlLevel: 'Control', allowRemoteFullAccess: true, verified: null, revoked: null,
    },
    {
      id: DEVICE.offline, name: '筆電 ' + XSS, signedName: 'LAPTOP', self: false, machineName: 'LAPTOP-7', appVersion: '0.4.0', fingerprint: 'JKMN-PQRS',
      keyStorage: 'software', online: false, lastSeenAt: now - 3 * HOUR, remoteControlLevel: 'Control', allowRemoteFullAccess: false, verified: null, revoked: null,
    },
    {
      id: DEVICE.view, name: 'HOME-PC', signedName: 'HOME-PC', self: false, machineName: 'HOME-PC', appVersion: '0.4.0', fingerprint: 'TVWX-YZ12',
      keyStorage: 'software', online: true, lastSeenAt: now - 2 * MIN, remoteControlLevel: 'View', allowRemoteFullAccess: false, verified: null, revoked: null,
    },
    {
      id: DEVICE.revoked, name: '舊桌機', signedName: '舊桌機', self: false, machineName: 'OLD-PC', appVersion: '0.2.0', fingerprint: '3456-789A',
      keyStorage: 'software', online: false, lastSeenAt: now - 20 * DAY, remoteControlLevel: 'Off', allowRemoteFullAccess: false, verified: null,
      revoked: { at: now - 10 * DAY, reason: 'retired' },
    },
  ];
}

/** 一格（AccountDeviceView）：那台已登入、身分相符 */
function cell(device, status, extra = {}) {
  return Object.assign({
    deviceId: device.id, deviceName: device.name, self: false, status, statusText: status === 'ready' ? '已登入' : '尚未登入',
    email: null, plan: null, accessExpiresAt: null, loginExpiresAt: null, identityOk: true, usagePoller: false, online: device.online, checkedAt: null,
  }, extra);
}

/** 瀏覽器形式的帳戶：Claude 在三台都登入；Codex 只在 OFFICE-PC；第三個帳戶沒有任何電腦登入（選電腦沒有候選） */
export function liveAccounts(now) {
  const [A, B, V] = liveDevices(now);
  const acct = (id, provider, label, color, status, statusText, devices, extra = {}) => Object.assign({
    id, provider, label, color, isSystemDefault: false, configDirectory: null, email: null, plan: null, status, statusText,
    safeMode: false, loginBusy: false, usage: null, sessions: { open: 0, running: 0, waiting: 0 }, logicalId: id, remoteOnly: false, devices,
  }, extra);
  return [
    acct('la_c1', 'Claude', '工作用 Max', '#D97757', 'ready', '3 台已登入（2 台在線）', [
      cell(A, 'ready', { email: 'lin.dev@example.com', plan: 'Claude Max', usagePoller: true }),
      cell(B, 'ready', { email: 'lin.dev@example.com', plan: 'Claude Max' }),
      cell(V, 'ready', { email: 'lin.dev@example.com', plan: 'Claude Max' }),
    ], { email: 'lin.dev@example.com', plan: 'Claude Max', usage: sharedUsage(now, 'Claude', 34), sessions: { open: 3, running: 1, waiting: 1 } }),
    acct('la_x1', 'Codex', 'Codex Pro', '#1FAE8E', 'ready', '1 台已登入（1 台在線）', [
      cell(A, 'ready', { email: 'lin.dev@example.com' }),
      cell(B, 'notLoggedIn'),
    ], { email: 'lin.dev@example.com', usage: sharedUsage(now, 'Codex', 18) }),
    acct('la_c9', 'Claude', '沒有電腦登入 ' + XSS, '#4C8DF6', 'notLoggedIn', '沒有電腦登入', [
      cell(A, 'notLoggedIn'),
    ]),
  ];
}

// ───── Grok 與 Antigravity（設計 docs/plans/2026-10-22-grok-antigravity-sync.md 的 P5、成功條件第 3 項）─────
// 不放進 liveAccounts／liveList（其他情境的筆數不變）：live-probe 的 runProviderCards 另外併進 init

/** Grok 與 Antigravity 的對話：Grok 在 OFFICE-PC 執行中、有等你核准的卡片；Antigravity 在 OFFICE-PC 閒置 */
export const PROVIDER_CONV = { grok: convId(7), agy: convId(8) };

/** Grok（OFFICE-PC 已登入、筆電沒登入）與 Antigravity（OFFICE-PC 已登入；一台只有一個 agy 登入、沒有隔離帳戶）的邏輯帳戶 */
export function providerAccounts(now) {
  const [A, B] = liveDevices(now);
  const acct = (id, provider, label, color, statusText, devices, extra = {}) => Object.assign({
    id, provider, label, color, isSystemDefault: false, configDirectory: null, email: null, plan: null, status: 'ready', statusText,
    safeMode: false, loginBusy: false, usage: null, sessions: { open: 0, running: 0, waiting: 0 }, logicalId: id, remoteOnly: false, devices,
  }, extra);
  return [
    acct('la_g1', 'Grok', 'Grok 個人 ' + XSS, '#111111', '1 台已登入（1 台在線）', [
      cell(A, 'ready', { email: 'lin.grok@example.com', plan: 'SuperGrok' }),
      cell(B, 'notLoggedIn'),
    ], { email: 'lin.grok@example.com', plan: 'SuperGrok', sessions: { open: 1, running: 1, waiting: 1 } }),
    acct('la_a1', 'Antigravity', 'Antigravity', '#4285F4', '1 台已登入（1 台在線）', [
      cell(A, 'ready', { email: 'lin.dev@gmail.example' }),
    ], { email: 'lin.dev@gmail.example' }),
  ];
}

/** 兩個對話的清單列 */
export function providerList(now) {
  return [
    summary(now, PROVIDER_CONV.grok, 'Grok 的對話（執行中）', { updatedAt: now - 2 * MIN, running: true, waiting: true, accountId: 'la_g1', provider: 'Grok' }),
    summary(now, PROVIDER_CONV.agy, 'Antigravity 的對話', { updatedAt: now - 30 * MIN, accountId: 'la_a1', provider: 'Antigravity' }),
  ];
}

/** 標頭（ConversationViewRules.Header 照代理種類：permissionModes、steer）；不是這兩個對話回 null */
export function providerHeader(id) {
  if (id === PROVIDER_CONV.grok) return { provider: 'Grok', accountId: 'la_g1', permissionModes: GROK_MODES, steer: false, running: true, lastTurn: '輸入 57,071・輸出 90 tokens' };
  if (id === PROVIDER_CONV.agy) return { provider: 'Antigravity', accountId: 'la_a1', permission: 'AcceptEdits', permissionModes: AGY_MODES, steer: false, lastTurn: '輸入 24,908・輸出 113 tokens' };
  return null;
}

/** 項目（形狀同 ItemRenderer 黃金檔 grok.json、antigravity.json 的伺服器渲染）：Grok 有思考、待辦、工具卡、等核准的卡片；agy 沒有任何卡片 */
export function providerItems(now, id) {
  const t = (minAgo) => now - minAgo * MIN;
  const items = id === PROVIDER_CONV.grok ? [
    { id: 'g1', kind: 'User', status: 'Done', time: t(5), html: '在 C:\\w 建 a.txt，再找 TODO', attachments: [] },
    { id: 'g2', kind: 'Thinking', status: 'Done', time: t(5), html: '<p>先看資料夾裡有什麼</p>\n' },
    { id: 'g3', kind: 'Plan', status: 'Running', time: t(4), html: '', plan: [{ text: '讀設定', status: 'completed' }, { text: '寫測試', status: 'inProgress' }, { text: '跑測試', status: 'pending' }] },
    { id: 'g4', kind: 'Tool', status: 'Done', time: t(4), toolKind: 'search', toolName: '搜尋內容', title: 'TODO\u3000src', detail: '{\n  "pattern": "TODO",\n  "path": "src"\n}',
      output: 'src\\a.cs:3:// TODO 補測試\n', canAllowForSession: true },
    { id: 'g5', kind: 'Tool', status: 'Done', time: t(3), toolKind: 'write', toolName: '寫入檔案', title: 'C:\\w\\a.txt', filePath: 'C:\\w\\a.txt',
      diff: [{ t: 'add', s: 'hello' }], diffStats: { add: 1, del: 0 }, canAllowForSession: true },
    { id: 'g6', kind: 'Approval', status: 'Waiting', time: t(2), toolKind: 'bash', toolName: '執行指令', title: 'npm test', detail: '跑測試\n\nnpm test',
      canAllowForSession: true, allowSessionLabel: '本對話的指令都不再詢問', requestDigest: DIGEST.approval },
  ] : id === PROVIDER_CONV.agy ? [
    { id: 'a1', kind: 'User', status: 'Done', time: t(31), html: '列出資料夾裡的檔案', attachments: [] },
    { id: 'a2', kind: 'Tool', status: 'Failed', time: t(31), toolKind: 'bash', toolName: '執行指令', title: 'Get-ChildItem', detail: 'Get-ChildItem',
      output: 'permission check failed for command "Get-ChildItem": user denied permission to run command', canAllowForSession: true },
    { id: 'a3', kind: 'Notice', status: 'Done', time: t(31), html: '<p>agy 自動拒絕了需要核准的操作（RunCommand）：Antigravity 在這裡無法於對話中詢問。要讓代理執行指令，請把權限模式改成「完全自動」。</p>\n' },
    { id: 'a4', kind: 'Tool', status: 'Done', time: t(30), toolKind: 'read', toolName: '讀取檔案', title: 'C:\\w\\hello.txt', filePath: 'C:\\w\\hello.txt', output: '2 lines, 17 bytes', canAllowForSession: true },
    { id: 'a5', kind: 'Assistant', status: 'Done', time: t(30), html: '<p>資料夾裡只有 hello.txt</p>\n' },
  ] : null;
  if (items) items.forEach((it, i) => { it.seq = i + 1; });
  return items;
}

// ───── 清單、標頭、項目、輸入區（伺服器送的形狀）─────

const DEVICE_OF = {
  [CONV.running]: DEVICE.control, [CONV.idle]: DEVICE.control, [CONV.offline]: DEVICE.offline, [CONV.view]: DEVICE.view,
  [CONV.codex]: DEVICE.control, [CONV.unlisted]: DEVICE.control,
};

/** 電腦的 P5 欄位（伺服器查裝置目錄填的） */
function deviceFields(now, deviceId) {
  const d = liveDevices(now).find((x) => x.id === deviceId);
  return { deviceId: deviceId || null, deviceName: d ? d.name : null, deviceOnline: d ? d.online : false };
}

/** 清單的一列（ConversationViewRules.Summary）；group 由伺服器依連線的時區算好 */
export function summary(now, id, title, o = {}) {
  const deviceId = o.deviceId !== undefined ? o.deviceId : DEVICE_OF[id] || DEVICE.control;
  return Object.assign({
    id, accountId: 'la_c1', provider: 'Claude', title, group: '今天', updatedAt: now - 5 * MIN, workingDirectory: null, customFolder: false,
    running: false, waiting: false, unread: false,
  }, deviceFields(now, deviceId), o);
}

/** 清單（不含 CONV.unlisted）：執行中、未讀、離線（標題帶 XSS）、政策 View、Codex */
export function liveList(now) {
  return [
    summary(now, CONV.running, '修正登入流程（執行中）', { updatedAt: now - MIN, running: true, waiting: true }),
    summary(now, CONV.idle, '整理用量報表', { updatedAt: now - 9 * MIN, unread: true }),
    summary(now, CONV.offline, '筆電上的對話 ' + XSS, { updatedAt: now - 2 * HOUR }),
    summary(now, CONV.view, '只能看的電腦上的對話', { updatedAt: now - 5 * HOUR }),
    summary(now, CONV.codex, 'Codex 完全自動的對話', { updatedAt: now - DAY - HOUR, group: '昨天', accountId: 'la_x1', provider: 'Codex' }),
  ];
}

const CLAUDE_DEFAULT_MODEL = [{ value: 'default', label: '預設', description: '帳戶預設模型', efforts: [] }];

/** 伺服器組的標頭（ConversationViewRules.Header）：commands、recentFolders 是空的，models 只有預設與目前的 */
export function serverHeader(now, id, title, o = {}) {
  const deviceId = o.deviceId !== undefined ? o.deviceId : DEVICE_OF[id] || DEVICE.control;
  const dev = deviceFields(now, deviceId);
  const provider = o.provider || 'Claude';
  const running = !!o.running && dev.deviceOnline === true;
  return Object.assign({
    id, accountId: provider === 'Codex' ? 'la_x1' : 'la_c1', provider, title, model: 'default', effort: 'default', permission: 'Ask',
    permissionModes: provider === 'Codex' ? CODEX_MODES : CLAUDE_MODES, models: CLAUDE_DEFAULT_MODEL, commands: [],
    workingDirectory: null, customFolder: false, recentFolders: [], isDraft: false, running,
    status: dev.deviceOnline === true ? (running ? '執行指令：npm test' : null) : '電腦離線',
    startedAt: running ? now - 42e3 : null, lastTurn: '輸入 1,234・輸出 567 tokens', accountMissing: false, restart: null,
  }, dev, o, { running });
}

/** 伺服器的輸入區（ConversationViewRules.Composer）：附件一律空的；佇列帶 queueMore（還有幾則沒帶到） */
export function serverComposer(o = {}) {
  return Object.assign({ attachments: [], busy: false, busyText: null, queue: [{ id: 'q1', text: '接著幫我跑一次完整的測試' }], queueMore: 3 }, o);
}

export const DIGEST = { approval: 'rd1:approval-0001', question: 'rd1:question-0001', plan: 'rd1:plan-0001' };
export const PLACEHOLDER_TEXT = '內容太大，暫時無法在瀏覽器顯示';

/** 伺服器渲染的項目：一般訊息、/m/ 圖片、三種等你回應的卡片（帶 requestDigest）、佔位項目 */
export function liveItems(now, prefix = 'i') {
  const t = (minAgo) => now - minAgo * MIN;
  const items = [
    { id: prefix + '1', kind: 'User', status: 'Done', time: t(30), html: '<p>帳戶沒登入時按「登入」畫面就卡住，幫我修好。</p>', attachments: [] },
    { id: prefix + '2', kind: 'Assistant', status: 'Done', time: t(29), html: '<p>找到原因了，修改後的畫面：</p><p><img src="/m/k3a?s=160&amp;v=1" alt="登入畫面"></p>',
      media: [{ url: '/m/k3b', kind: 'image', name: '登入畫面-修正後.png' }] },
    { id: prefix + '3', kind: 'Notice', status: 'Done', time: t(20), html: '<p>' + PLACEHOLDER_TEXT + '</p>' },
    { id: prefix + '4', kind: 'Question', status: 'Waiting', time: t(6), answerAsMessage: false, requestDigest: DIGEST.question, questions: [
      { id: 'q1', header: '逾時', text: '登入要等多久才算逾時？', multi: false, allowOther: true, secret: false, options: [
        { label: '3 分鐘（建議）', description: '和 CLI 預設的登入等待時間一致', preview: null },
        { label: '5 分鐘', description: null, preview: null }] }] },
    { id: prefix + '5', kind: 'PlanApproval', status: 'Waiting', time: t(4), html: '', requestDigest: DIGEST.plan,
      detailHtml: '<h4>補測試的計畫</h4><ol><li>新增逾時的測試</li><li>跑 <code>dotnet test</code></li></ol>' },
    { id: prefix + '6', kind: 'Approval', status: 'Waiting', time: t(2), toolKind: 'bash', toolName: '執行指令', title: 'dotnet test -c Release',
      detail: '執行單元測試（約 20 秒）', canAllowForSession: true, allowSessionLabel: '本對話同樣的指令都允許', requestDigest: DIGEST.approval },
  ];
  items.forEach((it, i) => { it.seq = i + 1; });
  return items;
}

/**
 * 大對話（步 12 的長工作量測，D39）：n 筆 User 與 Assistant 交替，html 的 UTF-8 位元組合計約 bytes（User 每筆約 2 KB，其餘給 Assistant）。
 * Assistant 依序重複段落、清單、程式碼區塊、表格（enhanceMarkdown 要包的），沒有圖片（桌面與伺服器的媒體網址不同，兩邊才能用同一份）；沒有亂數
 */
export function bigItems(now, n = 80, bytes = 1024 * 1024) {
  const size = (s) => Buffer.byteLength(s, 'utf8');
  const users = Math.ceil(n / 2);
  const userBytes = 2048;
  const assistantBytes = Math.floor((bytes - users * userBytes) / Math.max(1, n - users));
  const blocks = [
    (k) => `<p>第 ${k} 段：把縮圖產生搬到背景執行緒，<code>ThumbnailService</code> 改成佇列，並加上 LRU 快取；<strong>首次載入</strong>從 2.1 秒降到 0.3 秒，記憶體上限 64 MB。</p>`,
    (k) => `<p>第 ${k} 段的補充：佇列滿了就丟掉最舊的工作，<em>使用者捲回去</em>時再排一次；快取以完整路徑加修改時間當鍵。</p>`,
    (k) => `<ul><li>步驟 ${k}-1：新增 <code>LruCache</code></li><li>步驟 ${k}-2：縮圖改在背景產生</li><li>步驟 ${k}-3：跑 <code>dotnet test</code></li></ul>`,
    (k) => `<pre><code class="language-csharp">// 第 ${k} 個片段\nvar cache = new LruCache&lt;string, Bitmap&gt;(64 * 1024 * 1024);\nforeach (var key in keys) cache.GetOrAdd(key, Load);\n</code></pre>`,
    (k) => `<table><thead><tr><th>項目 ${k}</th><th>前</th><th>後</th></tr></thead><tbody><tr><td>首次載入</td><td>2.1 秒</td><td>0.3 秒</td></tr><tr><td>記憶體</td><td>310 MB</td><td>96 MB</td></tr></tbody></table>`,
  ];
  const fill = (target, pick) => {
    let html = '';
    for (let k = 0; size(html) < target; k++) html += pick(k);
    return html;
  };
  const items = [];
  for (let i = 0; i < n; i++) {
    const id = 'big' + String(i + 1).padStart(3, '0');
    const time = now - (n - i) * MIN;
    const user = i % 2 === 0;
    const html = user
      ? fill(userBytes, (k) => `<p>第 ${i + 1} 則的第 ${k + 1} 句：縮圖還是很慢，幫我看一下 <code>ThumbnailService</code> 為什麼卡住介面。</p>`)
      : fill(assistantBytes, (k) => blocks[k % blocks.length](k + 1));
    items.push(user ? { id, kind: 'User', status: 'Done', time, seq: i + 1, html, attachments: [] } : { id, kind: 'Assistant', status: 'Done', time, seq: i + 1, html });
  }
  return items;
}

/** 伺服器回 open 的 conversation（paneId、draft 是 null） */
export function conversationMessage(now, id, title, o = {}) {
  return {
    type: 'conversation', conversationId: id, paneId: o.paneId === undefined ? null : o.paneId,
    header: serverHeader(now, id, title, o.header || {}), items: o.items || liveItems(now), hasMore: !!o.hasMore,
    composer: serverComposer(o.composer || {}), draft: null,
  };
}

/** 伺服器草稿的 conversation（newConversation 的回應）：沒有項目、標頭帶網頁選的 deviceId（沒選是 null） */
export function draftMessage(now, id, o = {}) {
  const deviceId = o.deviceId === undefined ? DEVICE.control : o.deviceId;
  const header = serverHeader(now, id, '新對話', Object.assign({ deviceId, isDraft: true, lastTurn: null, status: null }, o.header || {}));
  // 草稿還沒有擁有者電腦：名稱與在線由網頁從 remote.devices 查（BrowserDraftRules.Header 用 ConversationDeviceInfo.None）
  header.deviceName = null;
  header.deviceOnline = false;
  header.status = null;
  return { type: 'conversation', conversationId: id, paneId: o.paneId === undefined ? 'p1' : o.paneId, header, items: [], hasMore: false,
    composer: { attachments: [], busy: false, busyText: null, queue: [] }, draft: null };
}

/** 瀏覽器的 init：三台電腦、清單；o 照 browserInit（另可給 conversations、build、commitAt） */
export function liveBrowserInit(now, o = {}) {
  const init = browserInit(now, Object.assign({ accounts: liveAccounts(now), devices: liveDevices(now) }, o));
  init.conversations = o.conversations === undefined ? liveList(now) : o.conversations;
  if (o.build !== undefined) init.app = Object.assign({}, init.app, { build: o.build });
  if (o.commitAt !== undefined) init.app = Object.assign({}, init.app, { commitAt: o.commitAt });
  return init;
}

/** 伺服器送的 control（WP4.5）：defaultPassword 預設 false */
export function controlMessage(o = {}) {
  return Object.assign({ type: 'control', tier1Until: null, tier2Until: null, defaultPassword: false }, o);
}

/** 伺服器送的 notify */
export function notifyMessage(id, kind, title, deviceName = 'OFFICE-PC') {
  return { type: 'notify', conversationId: id, kind, title, deviceName };
}

// ───── 桌面：其他電腦的唯讀副本、同步政策、SyncStatus ─────

/** 桌面的本機對話（deviceId 省略）與副本（deviceId 是擁有者；running／waiting 是擁有者那邊的） */
export function desktopList(now) {
  const local = (id, title, ago, o = {}) => Object.assign({
    id, accountId: 'a1', provider: 'Claude', title, group: '今天', updatedAt: now - ago, workingDirectory: 'C:\\repo\\demo', customFolder: true,
    running: false, waiting: false, unread: false,
  }, o);
  return [
    local('c1', '這台的對話', 2 * MIN),
    local(CONV.running, '筆電上執行中的對話（副本）', 3 * MIN, { accountId: '', deviceId: DEVICE.offline, running: true, waiting: true, workingDirectory: null, customFolder: false }),
    local('c2', '這台只在本機的對話', 10 * MIN),
    local(CONV.view, 'HOME-PC 的對話（副本） ' + XSS, HOUR, { accountId: '', deviceId: DEVICE.view, workingDirectory: null, customFolder: false }),
  ];
}

/** 有接收者的本機對話的 conversationSync（LocalSyncViewRules.ConversationSyncView） */
export function conversationSync(policy = 'full', o = {}) {
  const serverDeleted = !!o.serverDeleted;
  const syncable = o.syncable !== false;
  return { policy, choices: syncable && !serverDeleted ? ['full', 'metadata', 'localOnly'] : ['localOnly'], serverDeleted };
}

/** 副本的標頭（LocalSyncViewRules.ApplyReplicaHeader）：這台沒有程序（running false），擁有者的狀態在 replica */
export function replicaHeader(id, title, ownerDeviceId, o = {}) {
  return Object.assign({
    id, accountId: '', provider: 'Claude', title, model: 'default', effort: 'default', permission: 'Ask', permissionModes: CLAUDE_MODES,
    models: CLAUDE_DEFAULT_MODEL, commands: [], workingDirectory: null, customFolder: false, recentFolders: [], isDraft: false, running: false,
    status: null, startedAt: null, lastTurn: null, accountMissing: false, restart: null, deviceId: ownerDeviceId,
    replica: { running: !!o.running, waiting: !!o.waiting, itemsAvailable: o.itemsAvailable !== false, accountId: o.accountId === undefined ? null : o.accountId },
  }, o.header || {});
}

/** 桌面的副本 conversation */
export function replicaMessage(now, id, title, ownerDeviceId, o = {}) {
  return {
    type: 'conversation', conversationId: id, paneId: null, header: replicaHeader(id, title, ownerDeviceId, o),
    items: o.items || liveItems(now, 'r').map((it) => {
      // 桌面的 Item 沒有 requestDigest（只有瀏覽器模式的才有）
      const copy = Object.assign({}, it);
      delete copy.requestDigest;
      return copy;
    }), hasMore: false,
    composer: { attachments: [], busy: false, busyText: null, queue: [] }, draft: null,
  };
}

/**
 * 桌面的項目（媒體是桌面的 /media 網址）：訊息、圖片、做完的核准（帶檔案路徑，點了是 openPath／revealPath）、等你核准的卡片。
 * 副本與本機對話共用（步 10 的「開路徑帶 conversationId」）
 */
export function desktopItems(now, prefix = 'k') {
  const t = (minAgo) => now - minAgo * MIN;
  const items = [
    { id: prefix + '1', kind: 'User', status: 'Done', time: t(12), html: '<p>幫我看一下 a.cs 為什麼編譯不過。</p>', attachments: [] },
    { id: prefix + '2', kind: 'Assistant', status: 'Done', time: t(11), html: '<p>改好了，畫面如下：</p>',
      media: [{ url: '/media?p=shot', kind: 'image', name: '畫面.png', path: 'C:\\repo\\demo\\shot.png' }] },
    { id: prefix + '3', kind: 'Approval', status: 'Done', time: t(10), toolKind: 'edit', toolName: '編輯檔案', title: '修改 a.cs',
      filePath: 'C:\\repo\\demo\\a.cs', detail: null, canAllowForSession: false },
    { id: prefix + '4', kind: 'Approval', status: 'Waiting', time: t(2), toolKind: 'bash', toolName: '執行指令', title: 'dotnet test -c Release',
      detail: '執行單元測試（約 20 秒）', canAllowForSession: false },
  ];
  items.forEach((it, i) => { it.seq = i + 1; });
  return items;
}

/** 桌面的本機正式對話（帳戶 a1、自選資料夾）；o.conversationSync：省略＝完整同步，null＝不帶這個鍵（未配對、草稿） */
export function localMessage(now, id, title, o = {}) {
  const header = Object.assign({
    id, accountId: 'a1', provider: 'Claude', title, model: 'default', effort: 'default', permission: 'Ask', permissionModes: CLAUDE_MODES,
    models: CLAUDE_DEFAULT_MODEL, commands: [], workingDirectory: 'C:\\repo\\demo', customFolder: true, recentFolders: [], isDraft: false, running: false,
    status: null, startedAt: null, lastTurn: null, accountMissing: false, restart: null,
  }, o.conversationSync === null ? {} : { conversationSync: o.conversationSync || conversationSync('full') }, o.header || {});
  return {
    type: 'conversation', conversationId: id, paneId: null, header, items: o.items || desktopItems(now, 'l'), hasMore: false,
    composer: { attachments: [], busy: false, busyText: null, queue: [] }, draft: null,
  };
}

/**
 * 遠端操作紀錄（remoteAudit 的 entries，新到舊依 (at, seq)）：第一頁三筆（hasMore）、第二頁兩筆。
 * 第二頁的第一筆和第一頁最舊一筆同一毫秒（要以 beforeSeq 才接得上）；來源有 XSS、有 null（顯示替代文字）；狀態含 executing
 */
export function auditPages(now) {
  const e = (o) => Object.assign({ commandId: null, cmd: 'send', conversationId: null, issuer: 'Chrome・Windows', tier: 1, fullAccess: false, policy: 'Control',
    state: 'done', code: null, defaultPassword: false, count: 1 }, o);
  const oldestAt = now - 10 * MIN;
  const first = [
    e({ at: now - MIN, seq: 9, commandId: '01J9QAUDIT0000000000000009', cmd: 'send', conversationId: 'c1' }),
    e({ at: now - 5 * MIN, seq: 8, commandId: '01J9QAUDIT0000000000000008', cmd: 'approve', issuer: 'Edge・' + XSS, state: 'executing', defaultPassword: true, count: 2 }),
    e({ at: oldestAt, seq: 7, commandId: '01J9QAUDIT0000000000000007', cmd: 'delete', issuer: null, tier: 2, policy: 'View', state: 'rejected', code: 'policy' }),
  ];
  const second = [
    e({ at: oldestAt, seq: 6, commandId: '01J9QAUDIT0000000000000006', cmd: 'stop', state: 'unknown' }),
    e({ at: now - DAY, seq: 5, commandId: '01J9QAUDIT0000000000000005', cmd: 'setOption', fullAccess: true }),
  ];
  return { first, second, oldestAt, oldestSeq: 7 };
}

/** 回收區（trash 的 entries，新到舊）：一筆完整（標題帶 XSS）、一筆標題與發起者是 null（顯示替代文字）、一筆一小時後到期（還剩 1 天） */
export function trashEntries(now) {
  return [
    { conversationId: convId(21), title: '遠端刪掉的對話 ' + XSS, trashedAt: now - 2 * DAY, issuer: 'Chrome・Windows', commandId: '01J9QTRASH0000000000000001', expiresAt: now - 2 * DAY + 30 * DAY },
    { conversationId: convId(22), title: null, trashedAt: now - 5 * DAY, issuer: null, commandId: null, expiresAt: now - 5 * DAY + 30 * DAY },
    { conversationId: convId(23), title: '快到期的對話', trashedAt: now - 30 * DAY + HOUR, issuer: 'Safari・iPhone', commandId: null, expiresAt: now + HOUR },
  ];
}

/** 這台（d_A）已配對的 SyncStatus：o.backfill（{conversationsLeft, bytesLeft}｜null）、o.remoteSupported、o.notifyRemoteActions（省略＝不帶這個鍵） */
export function syncWith(now, o = {}) {
  const local = { paused: false, remotePaused: false, remoteControlLevel: o.remoteControlLevel || 'Control', allowRemoteFullAccess: !!o.allowRemoteFullAccess };
  if (o.notifyRemoteActions !== undefined) local.notifyRemoteActions = o.notifyRemoteActions;
  const s = syncOnline(now, { local });
  if (o.backfill !== undefined && o.backfill !== null) s.backfill = o.backfill;
  if (o.remoteSupported !== undefined) s.remoteSupported = o.remoteSupported;
  return s;
}

/** 回補的幾種情況（SyncStatus.backfill） */
export const BACKFILL = {
  busy: { conversationsLeft: 12, bytesLeft: 50 * 1024 * 1024 },
  small: { conversationsLeft: 1, bytesLeft: 300 * 1024 },
  done: null,
};

/** 桌面已配對的 init：這台是 d_A（self），清單有本機與副本；o.sync 換掉 SyncStatus */
export function liveDesktopInit(now, o = {}) {
  const devices = liveDevices(now).map((d) => (d.id === DEVICE.control ? Object.assign({}, d, { self: true, verified: false }) : Object.assign({}, d, { verified: true })));
  const init = desktopInit(now, { remote: { mode: 'desktop', deviceId: DEVICE.control, devices, sync: o.sync || syncWith(now, { remoteSupported: true }) } });
  init.conversations = o.conversations === undefined ? desktopList(now) : o.conversations;
  if (o.layout) init.layout = o.layout;
  return init;
}
