// 網頁即時檢視與操作的純規則（WP4.6 計畫步 1）：對話與草稿的 id、還原分頁、所在電腦與在線、送出與卡片的閘門、
// 權限選單、狀態列、指令結果的追蹤、通知、預設密碼警告、換版重新載入、剪貼簿、相對網址、遠端操作紀錄、回收區、
// 對話的同步政策、選電腦、草稿換電腦、刪除的確認文字、歷史篩選（電腦、工作區）、上傳與附件晶片、檔案預覽、大對話的分批載入、訊息時間與最近的指令。協定見 docs/web-protocol.md。
// 沒有 import、不碰 DOM、不讀時鐘與儲存：現在時間、設定、儲存讀到的值一律由呼叫端傳入
// （tools/ui-probe/live-rules.test.mjs 以 data: URL 直接匯入測試）。
// 回傳的文字都是純文字：裝置名稱、標題、發起者等來自其他電腦或伺服器的字串，呼叫端一律以 textContent 顯示。

const MINUTE = 60000;
const DAY = 24 * 60 * MINUTE;
const MB = 1024 * 1024;

/** visible／openConversationIds 最多帶幾個對話 id（D2） */
export const MAX_VIEWING = 100;
/** 等不到 commandResult 時多久當成結果不明（伺服器 iat＋100 秒之外的保險，D5） */
export const TRACK_MAX_MS = 150000;
/** clipboard 只收送出 copyItem 之後多久之內的（D42） */
export const CLIPBOARD_MAX_MS = 30000;
/** 換版之後多久之內最多自動重新載入一次（D13） */
export const RELOAD_MIN_INTERVAL_MS = 60000;
/** 換版之後使用者停手多久才自動重新載入（看得到分頁時；D13） */
export const RELOAD_IDLE_MS = 10000;
/** 換版之後還不能重新載入時多久重查一次（D13） */
export const RELOAD_CHECK_MS = 2000;
/** 重新載入前最多存幾個輸入框的草稿（D13） */
export const MAX_RELOAD_DRAFTS = 100;
/** 網頁上傳的單檔上限（E4 的 512 MB，D32） */
export const UPLOAD_MAX_BYTES = 512 * MB;
/** 相對網址的長度上限（D34） */
export const MAX_URL_LENGTH = 2048;
/** 媒體與檔案的相對網址前綴（/m/ 原圖、/t/ 縮圖、/f/ 檔案預覽） */
export const MEDIA_PREFIXES = Object.freeze(['/m/', '/t/', '/f/']);
/** 電腦不明時的名稱 */
export const UNKNOWN_DEVICE = '未知電腦';
/** 指令結果不明時卡片與輸入區的說明（D5） */
export const UNKNOWN_RESULT_TEXT = '結果不明，請看對話內容確認';
/** 預設密碼的常駐警告（D20） */
export const DEFAULT_PASSWORD_TEXT = '密碼還是預設值，知道它的人都能遠端操作你開了遠端控制的電腦';
/** fileRef 有錯誤（或網址不合格）時一律顯示的文字（D34） */
export const FILE_REF_ERROR_TEXT = '這個檔案不能從網頁讀取';
/** 上傳中（或加進輸入區中）時送出的說明（D14、D32） */
export const UPLOADING_TEXT = '附件上傳中，完成後才能送出';
/** 改成「只留在這台」之前的確認（D25） */
export const LOCAL_ONLY_CONFIRM_TEXT = '會從伺服器刪除這個對話，網頁與其他電腦都看不到，之後不能再同步';
/** 草稿的預設值（標頭的 model、effort、permission 與標題；伺服器草稿的 permission 是 null＝每步詢問） */
export const DRAFT_DEFAULTS = Object.freeze({ model: 'default', effort: 'default', permission: 'Ask', title: '新對話' });

const isObj = (v) => !!v && typeof v === 'object' && !Array.isArray(v);
const str = (v) => (typeof v === 'string' && v ? v : null);
const num = (v) => (typeof v === 'number' && Number.isFinite(v) ? v : null);
const bool = (v) => (typeof v === 'boolean' ? v : null);
const isHubMode = (mode) => mode === 'hub';
/** 對照表查值：只認表自己的鍵（'toString'、'constructor' 之類原型上的名稱不算），沒有時 null */
const own = (map, key) => (typeof key === 'string' && Object.prototype.hasOwnProperty.call(map, key) ? map[key] : null);

/** 裝置名稱放進句子：認得的名稱前後依需要加空白（「在 OFFICE-PC 執行」），不明時是「未知電腦」、不加空白（「在未知電腦執行」） */
function devLabel(dev, left, right) {
  const name = dev && str(dev.name);
  if (!name || name === UNKNOWN_DEVICE) return UNKNOWN_DEVICE;
  return (left ? ' ' : '') + name + (right ? ' ' : '');
}

/** 字串裡有控制字元、空白、DEL 或反斜線（相對網址一律不收） */
function hasUnsafeChar(s) {
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i);
    if (c <= 0x20 || c === 0x7f || c === 0x5c) return true;
  }
  return false;
}

// ───── id 與還原分頁 ─────

/** 對話 id：32 個小寫十六進位（伺服器與桌面的線上形狀） */
export function isConversationId(id) {
  return typeof id === 'string' && /^[0-9a-f]{32}$/.test(id);
}

/** 伺服器草稿的 id：d 加 26 字的 ULID（大寫 Crockford Base32，不含 I、L、O、U） */
export function isDraftId(id) {
  return typeof id === 'string' && /^d[0-9A-HJKMNP-TV-Z]{26}$/.test(id);
}

/** visible 與 ClientState 的 openConversationIds：只留對話 id（草稿被濾掉）、去重、照原順序、最多 MAX_VIEWING 個（D2） */
export function viewingIds(ids) {
  if (!Array.isArray(ids)) return [];
  const out = [];
  const seen = new Set();
  for (const id of ids) {
    if (!isConversationId(id) || seen.has(id)) continue;
    seen.add(id);
    out.push(id);
    if (out.length >= MAX_VIEWING) break;
  }
  return out;
}

/**
 * 還原版面時這個分頁要不要保留（D9）：桌面照清單；瀏覽器另外保留清單外的對話 id 與草稿 id
 * （清單最多 1000 筆；不在了伺服器回 conflict 並送 conversationClosed，分頁才關）
 */
export function keepOnRestore(id, inList, mode, replicasPending) {
  if (!isHubMode(mode)) return inList === true || awaitsList(id, inList, mode, replicasPending);
  return inList === true || isConversationId(id) || isDraftId(id);
}

/**
 * 〔同步〕桌面還原版面時這個分頁要留著等清單（2026-10-29：重開機後其他電腦的對話分頁全部不見）：同步元件在主視窗載入後才啟動，
 * 其他電腦的對話（複本）要等引擎從 sync.db 讀出來才進清單。宿主說清單還沒載完（init／conversations 的 replicasPending）時，
 * 不在清單、形狀是對話 id 的分頁先照原位留著、不送 open；清單到了再接上，載完仍不在的才關（workspace.settleAwaiting）
 */
export function awaitsList(id, inList, mode, replicasPending) {
  return !isHubMode(mode) && inList !== true && replicasPending === true && isConversationId(id);
}

// ───── 桌面遠端操作（sync-protocol 14.7；擁有者 2026-10-22 決定「已配對就能操作」） ─────

/** 同步沒連上時，副本的輸入區與卡片的說明 */
export const PEER_DOWN_TEXT = '目前沒有連上伺服器，連上之後才能操作其他電腦的對話';

/**
 * 這台桌面能不能對其他電腦下指令（remote.sync.peer）：'live' 能；'down' 同步沒連上（連上之後就能）；'unsupported' 連著但伺服器或這台不支援
 * （照舊唯讀）；'off' 瀏覽器版或沒有配對
 */
export function peerOf(remote, mode) {
  if (isHubMode(mode)) return 'off';
  const s = isObj(remote) && isObj(remote.sync) ? remote.sync : null;
  if (!s) return 'off';
  if (s.peer === true) return 'live';
  return s.state === 'online' ? 'unsupported' : 'down';
}

/** 可以從這台操作的副本（origin 是 replica、peer 是 live）：送出、卡片、權限比照瀏覽器版的遠端對話 */
export function controllable(origin, peer) {
  return origin === 'replica' && peer === 'live';
}

const ULID_ALPHABET = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';

/**
 * ULID（26 字，Crockford base32）：桌面遠端操作的 commandId（同瀏覽器 transport-hub 的 ulid）。bytes10 是 10 個隨機位元組
 * （呼叫端以 crypto.getRandomValues 產生；這個檔不碰全域）
 */
export function ulid(nowMs, bytes10) {
  let time = Math.floor(Number(nowMs));
  if (!Number.isFinite(time) || time < 0) time = 0;
  time = Math.min(time, 281474976710655);
  let head = '';
  for (let i = 0; i < 10; i++) {
    head = ULID_ALPHABET[time % 32] + head;
    time = Math.floor(time / 32);
  }
  const bytes = bytes10 && bytes10.length === 10 ? bytes10 : new Uint8Array(10);
  let tail = '';
  let acc = 0;
  let bits = 0;
  for (let i = 0; i < 10; i++) {
    acc = (acc << 8) | (bytes[i] & 255);
    bits += 8;
    while (bits >= 5) {
      bits -= 5;
      tail += ULID_ALPHABET[(acc >> bits) & 31];
    }
    acc &= (1 << bits) - 1;
  }
  return head + tail;
}

/**
 * 由電腦執行的遠端指令（型錄 RemoteCommandCatalog 路由 Owner／Device 的網頁指令；conversationCreate 不是網頁指令）：桌面只對這些帶 commandId。
 * 和型錄不同步時 C# 的 PeerHubRulesTests 會紅
 */
export const PEER_COMMANDS = Object.freeze([
  'send', 'stop', 'cancelQueued', 'answer', 'skipQuestion', 'approve', 'approvePlan', 'rename', 'delete', 'setOption', 'setAccount', 'setFolder',
  'resetFolder', 'removeAttachment', 'addUploaded', 'voiceMessage', 'listFiles', 'fileSearch', 'fileFetch',
  'accountAdd', 'accountLogin', 'accountLoginCancel', 'accountLogout', 'accountRemove', 'accountRefresh', 'cliCheck', 'cliInstall', 'cliUpdate', 'accountLoginDevice',
  'accountLoginCode',
  'scheduleAction', 'scheduleAgent', 'timedSave', 'timedAction',
  'deviceBrowse', 'deviceFetch', 'devicePut', 'deviceTree', 'deviceFetchMany',
  'deskOpen',
]);
const PEER_COMMAND_SET = new Set(PEER_COMMANDS);

/**
 * 桌面送給宿主的指令要不要帶 commandId（宿主照型錄決定要不要交給同步；帶了才追得到結果）：由電腦執行的遠端指令（PEER_COMMANDS）、還沒帶，
 * 而且對象是副本（convOrigin 是 replica）或帶的 deviceId 不是這台（其他電腦的帳戶與 CLI）。瀏覽器版不帶（transport-hub 自己產生）
 */
export function wantsPeerId(msg, mode, convOrigin, ownDeviceId) {
  if (isHubMode(mode) || !isObj(msg) || str(msg.commandId) || !str(msg.cmd) || !PEER_COMMAND_SET.has(msg.cmd)) return false;
  if (convOrigin === 'replica') return true;
  const dev = str(msg.deviceId);
  return !!dev && !!str(ownDeviceId) && dev !== ownDeviceId;
}

/** 對話的來源：'draft'（草稿）、'remote'（瀏覽器的對話）、'replica'（桌面上其他電腦的副本）、'local'（桌面這台的） */
export function originOf(conv, mode) {
  if (isObj(conv) && conv.isDraft === true) return 'draft';
  if (isHubMode(mode)) return 'remote';
  if (isObj(conv) && (isObj(conv.replica) || str(conv.deviceId))) return 'replica';
  return 'local';
}

// ───── 所在電腦 ─────

/**
 * 對話所在的電腦（D16、D22）：先查 remote.devices（名稱、在線、政策），找不到時用 fallback（Summary／Header 的 deviceName、deviceOnline）；
 * 都沒有時名稱是「未知電腦」、online 是 null。政策只來自 remote.devices：不明時 level、allowFull 是 null（閘門不擋，交給伺服器）
 */
export function deviceOf(deviceId, remote, fallback) {
  const id = str(deviceId);
  const list = isObj(remote) && Array.isArray(remote.devices) ? remote.devices : [];
  const d = id ? list.find((x) => isObj(x) && x.id === id) : null;
  const fb = isObj(fallback) ? fallback : {};
  if (d) {
    const level = d.remoteControlLevel === 'Off' || d.remoteControlLevel === 'View' || d.remoteControlLevel === 'Control' ? d.remoteControlLevel : null;
    const online = bool(d.online);
    return {
      id,
      name: str(d.name) || str(fb.deviceName) || UNKNOWN_DEVICE,
      online: online !== null ? online : bool(fb.deviceOnline),
      level,
      allowFull: bool(d.allowRemoteFullAccess),
      known: true,
      // 剛斷線（remoterules.trackDeviceLinks）：文字是「重新連線中」，閘門照樣擋
      reconnecting: d.online === false && d.reconnecting === true,
    };
  }
  return { id, name: str(fb.deviceName) || UNKNOWN_DEVICE, online: bool(fb.deviceOnline), level: null, allowFull: null, known: false, reconnecting: false };
}

/**
 * 標頭的裝置行：「在 OFFICE-PC 執行・線上」「…・離線」（剛斷線是「…・重新連線中」），執行中是「在 OFFICE-PC 執行中・線上」，不明的電腦「在未知電腦執行」。
 * state 是有 running 的物件（瀏覽器給 Header，桌面副本給 header.replica）；這台的對話與草稿沒有裝置行（null）
 */
export function deviceLine(origin, dev, state) {
  if (origin !== 'remote' && origin !== 'replica') return null;
  const running = isObj(state) && state.running === true;
  const online = dev ? dev.online : null;
  const suffix = online === true ? '・線上' : online === false ? (dev.reconnecting === true ? '・重新連線中' : '・離線') : '';
  return '在' + devLabel(dev, true, true) + (running ? '執行中' : '執行') + suffix;
}

// ───── 送出、卡片與權限的閘門 ─────

/** 副本的唯讀說明（輸入區與卡片共用） */
function replicaText(dev) {
  return '這是' + devLabel(dev, true, true) + '的對話（唯讀），到網頁版或' + devLabel(dev, true, true) + '上回應';
}

/** 政策不是 Control 時的說明（和 WP4.8 的 remoteAllowed 同一句；用語對應設定頁「其他電腦的遠端操作」） */
function policyText(dev) {
  return '在' + devLabel(dev, true, true) + '的設定中允許遠端操作';
}

/**
 * 送出的閘門（D14，依序判斷）：副本（桌面遠端操作不能用時唯讀、同步沒連上時擋著）→ 瀏覽器沒連線 → 電腦離線 → 政策不是 Control
 * → 完全自動而那台沒允許 → 上傳中。可以操作的副本（peer 是 live）比照瀏覽器版。
 * 電腦不明（online、level、allowFull 是 null）時那一項不擋，交給伺服器。回 {canSend, readOnly, reason}
 */
export function composerGate({ mode, origin, header, device, live, uploading, peer, remoteDraft } = {}) {
  const ok = { canSend: true, readOnly: false, reason: null };
  const block = (reason) => ({ canSend: false, readOnly: false, reason });
  // 〔桌面遠端操作〕要在其他電腦開的新對話：比照可以操作的副本（同步沒連上時擋著）
  if (remoteDraft && origin === 'draft' && !isHubMode(mode)) {
    if (peer !== 'live') return block(PEER_DOWN_TEXT);
    origin = 'replica';
  }
  if (origin === 'replica') {
    if (peer === 'down') return block(PEER_DOWN_TEXT);
    if (peer !== 'live') return { canSend: false, readOnly: true, reason: replicaText(device) };
  }
  if (isHubMode(mode) || controllable(origin, peer)) {
    if (isHubMode(mode) && live !== true) return block('目前沒有連線，連上之後才能送出');
    if (device && device.online === false) return block(offlineReason(device, '送出'));
    if (device && typeof device.level === 'string' && device.level !== 'Control') return block(policyText(device));
    if (isObj(header) && header.permission === 'FullAccess' && device && device.allowFull === false)
      return block(devLabel(device, false, true) + '沒有允許遠端完全自動，先把權限改成其他模式');
  }
  if (uploading) return block(UPLOADING_TEXT);
  return ok;
}

/**
 * 卡片（核准、計畫確認、問卷）的閘門（D18）：副本（桌面遠端操作不能用、同步沒連上）、沒連線（live 是 false）、電腦離線、政策不是 Control 時停用並說明；
 * 可以操作的副本比照瀏覽器版
 */
export function cardGate({ mode, origin, device, live, peer } = {}) {
  if (origin === 'replica') {
    if (peer === 'down') return { enabled: false, reason: PEER_DOWN_TEXT };
    if (peer !== 'live') return { enabled: false, reason: replicaText(device) };
  }
  if (isHubMode(mode) || controllable(origin, peer)) {
    if (isHubMode(mode) && live === false) return { enabled: false, reason: '目前沒有連線，連上之後才能回應' };
    if (device && device.online === false) return { enabled: false, reason: offlineReason(device, '回應') };
    if (device && typeof device.level === 'string' && device.level !== 'Control') return { enabled: false, reason: policyText(device) };
  }
  return { enabled: true, reason: null };
}

/** 權限選單（D15）：瀏覽器版與可以操作的副本，那台沒允許遠端完全自動時，FullAccess 停用並說明；其他照 header.permissionModes */
export function permissionChoices(modes, device, mode, origin, peer) {
  if (!Array.isArray(modes)) return [];
  const blockFull = (isHubMode(mode) || controllable(origin, peer)) && !!device && device.allowFull === false;
  return modes.filter((m) => isObj(m) && str(m.value)).map((m) => {
    const disabled = blockFull && m.value === 'FullAccess';
    return {
      value: m.value,
      label: str(m.label) || m.value,
      description: str(m.description),
      disabled,
      reason: disabled ? devLabel(device, false, true) + '沒有允許遠端完全自動' : null,
    };
  });
}

/**
 * 狀態列（D14 的 R4）：桌面只在執行中顯示；瀏覽器有伺服器的狀態文字就顯示（「電腦離線」「伺服器正在整理這個對話」「等待電腦重新上傳」），
 * 只有 running 才轉圈。桌面上其他電腦的副本看擁有者那邊的 header.replica.running（副本的 header.running 是這台的，一律 false；
 * 這台也沒有它的狀態文字，執行中時寫「執行中…」）。spinning 也是對話畫面的執行中動畫（.conv.running）
 */
export function statusLine(header, mode) {
  const own = isObj(header) && header.running === true;
  const status = isObj(header) ? str(header.status) : null;
  if (isHubMode(mode)) return { spinning: own, text: status };
  const replica = !own && isObj(header) && isObj(header.replica) && header.replica.running === true;
  if (replica) return { spinning: true, text: status || '執行中…' };
  return { spinning: own, text: own ? status : null };
}

/**
 * 等背景工作回報（Header.background，只有桌面這台的對話）：代理這一輪結束了、它開始的背景工作（背景指令、背景子代理）還在跑——
 * 還算在進行，只是樣貌和執行中不同（虛線圈、不跑掃描光）。執行中照執行中顯示（回 null）。
 * 回 {count, text, title, startedAt}：text 是狀態列的一行（「等背景工作回報：npm test」「等 3 個背景工作回報：npm test 等」），title 是滑過看的完整說明
 * （給了 now 時每一個後面寫已執行多久），startedAt 是最早開始、還在跑的那一個的開始時間（Unix 毫秒；狀態列後面的計時器，不明是 null）
 */
export function backgroundLine(header, now) {
  if (!isObj(header) || header.running === true || !isObj(header.background)) return null;
  const count = num(header.background.count);
  if (count === null || count < 1) return null;
  const tasks = Array.isArray(header.background.tasks) ? header.background.tasks.filter(isObj) : [];
  const listed = tasks.map((t) => ({ name: str(t.title) || str(t.tool), at: num(t.startedAt) })).filter((t) => t.name !== null);
  const head = count === 1 ? '等背景工作回報' : '等 ' + count + ' 個背景工作回報';
  const text = listed.length ? head + '：' + listed[0].name + (count > 1 ? ' 等' : '') : head;
  const more = count - listed.length;
  const ran = (at) => {
    if (at === null || at <= 0 || num(now) === null) return '';
    const span = runningFor(at, now);
    return '（已執行' + (span.startsWith('不到') ? '' : ' ') + span + '）';
  };
  const title = '代理這一輪已結束，背景工作完成後會自動接著處理'
    + listed.map((t) => '\n・' + t.name + ran(t.at)).join('') + (listed.length && more > 0 ? '\n・另外 ' + more + ' 個' : '');
  const startedAt = num(header.background.startedAt);
  return { count, text, title, startedAt: startedAt !== null && startedAt > 0 ? startedAt : null };
}

/** 分頁與側欄那一列要不要顯示「等背景工作回報」：清單的 background（或標頭的 Header.background）而且不在執行中 */
export function backgroundWaiting(conv) {
  if (!isObj(conv) || conv.running === true) return false;
  return conv.background === true || backgroundLine(conv) !== null;
}

/** 背景工作停止選單的一列最多幾個字（指令很長時截短） */
const STOP_LABEL_MAX = 80;

/**
 * 等背景工作回報那一行的「停止」選單（桌面的 taskStop，2026-10-25）：能單獨停止的（Header.background.tasks 裡有 stopId 的，Claude、Codex）各一列，
 * 再加「全部停止」——只有一個背景工作、而且就是能單獨停止的那一個時不加（同一件事）；Antigravity、Grok 沒有 stopId，只有「停止背景工作」那一列。
 * 回 {note, rows:[{label, desc, stopId}], all:{label}|null}；沒有背景工作是 null
 */
export function backgroundStopMenu(background, now) {
  if (!isObj(background)) return null;
  const count = num(background.count);
  if (count === null || count < 1) return null;
  const tasks = Array.isArray(background.tasks) ? background.tasks.filter(isObj) : [];
  const rows = [];
  for (const t of tasks) {
    const stopId = str(t.stopId);
    if (stopId === null) continue;
    const name = str(t.title) || str(t.tool) || '背景工作';
    const at = num(t.startedAt);
    const desc = [str(t.title) ? str(t.tool) : null, at === null || at <= 0 || num(now) === null ? null : '已執行 ' + runningFor(at, now)]
      .filter((x) => x !== null).join('・');
    rows.push({ label: name.length > STOP_LABEL_MAX ? name.slice(0, STOP_LABEL_MAX) + '…' : name, desc, stopId });
  }
  const all = count === 1 && rows.length === 1 ? null
    : { label: count === 1 ? '停止背景工作' : '全部停止（' + count + ' 個背景工作）' };
  return { note: '強制停止代理放到背景執行的工作。停止後代理不會自動接著處理，要它接手就送一則訊息。', rows, all };
}

/** 「詳細」能看的背景工作：Header.background.tasks 裡有 key 的（宿主以它找回那一個，taskDetail） */
export function detailTasks(background) {
  const tasks = isObj(background) && Array.isArray(background.tasks) ? background.tasks : [];
  return tasks.filter((t) => isObj(t) && str(t.key) !== null);
}

/** 「詳細」對話框開著時多久問一次（輸出、還在不在） */
export const TASK_DETAIL_POLL_MS = 3000;

/** 輸出的大小：272 B／2.0 KB／1.5 MB */
function byteSize(n) {
  return n < 1024 ? n + ' B' : n < 1048576 ? (n / 1024).toFixed(1) + ' KB' : (n / 1048576).toFixed(1) + ' MB';
}

/**
 * 宿主回的 taskDetail → 「詳細」對話框的內容（task 是標頭的那一列，now 算已執行多久）：
 * {ended, explain, agentNote, facts, content, contentLabel, hasOutput, text, meta}。ended＝已經結束了（meta 是原因，網頁停止再問、輸出留著）；
 * 有 output 字串才有輸出（meta 是大小、是否只顯示最後的部分），沒有時 meta 是 outputNote（看不到的原因）
 */
export function taskDetailView(msg, task, now) {
  const m = isObj(msg) ? msg : {};
  const t = isObj(task) ? task : {};
  const at = num(t.startedAt);
  const facts = [str(m.tool) || str(t.tool), at === null || at <= 0 || num(now) === null ? null : '已執行 ' + runningFor(at, now)]
    .filter((x) => x !== null).join('・');
  const base = { explain: str(m.explain), agentNote: str(m.agentNote), facts, content: str(m.content), contentLabel: str(m.contentLabel) || '內容' };
  if (str(m.error)) return { ...base, ended: true, hasOutput: false, text: '', meta: m.error };
  if (typeof m.output !== 'string') return { ...base, ended: false, hasOutput: false, text: '', meta: str(m.outputNote) || '看不到這個背景工作的輸出' };
  const text = m.output.replace(/\r\n/g, '\n');
  const total = num(m.totalBytes);
  const parts = [];
  if (!text) parts.push('還沒有輸出');
  if (total !== null && total > 0) parts.push('共 ' + byteSize(total));
  if (m.truncated === true) parts.push('只顯示最後的部分');
  parts.push('每 3 秒更新');
  return { ...base, ended: false, hasOutput: true, text, meta: parts.join('・') };
}

/**
 * 「詳細」裡的停止鈕：能單獨停止的（有 stopId）停這一個；沒有的（Antigravity、Grok）只能一起停止這個對話的所有背景工作。
 * 回 {label, stopId|null, title}
 */
export function taskStopAction(task, background) {
  const stopId = isObj(task) ? str(task.stopId) : null;
  if (stopId !== null) return { label: '停止這個背景工作', stopId, title: '強制停止這個背景工作' };
  const count = isObj(background) ? num(background.count) : null;
  return {
    label: count !== null && count > 1 ? '停止全部 ' + count + ' 個背景工作' : '停止背景工作',
    stopId: null,
    title: '這種代理不能單獨停止一個背景工作，會一起停止這個對話所有的背景工作',
  };
}

// ───── 對話開出的背景程式（Header.programs；標頭晶片列的「背景程式」與選單） ─────

/** 已執行多久：不到 1 分／12 分／2 小時 13 分／3 天 4 小時 */
function runningFor(startedAt, now) {
  const totalMin = Math.max(0, Math.floor((now - startedAt) / 60000));
  const d = Math.floor(totalMin / 1440), hr = Math.floor((totalMin % 1440) / 60), mi = totalMin % 60;
  if (d > 0) return d + ' 天' + (hr ? ' ' + hr + ' 小時' : '');
  if (hr > 0) return hr + ' 小時' + (mi ? ' ' + mi + ' 分' : '');
  return mi > 0 ? mi + ' 分' : '不到 1 分';
}

/**
 * 標頭的背景程式（docs/web-protocol.md 的 Header.programs）：代理另開、脫離 CLI 還在跑的程式（只有桌面這台的對話有）。
 * 沒有回 null；有的話 { count, label, title, rows: [{ id, command, desc }] }——desc 是「已執行 12 分・PID 4321」（不明的省略）。
 * 沒有 id 的那一筆不列（停止要帶它）
 */
export function programsView(programs, now) {
  if (!Array.isArray(programs)) return null;
  const rows = [];
  for (const p of programs) {
    if (!isObj(p) || !str(p.id)) continue;
    const started = num(p.startedAt);
    const pid = num(p.pid);
    const desc = [started === null ? null : '已執行 ' + runningFor(started, now), pid === null ? null : 'PID ' + pid].filter((x) => x !== null).join('・');
    rows.push({ id: p.id, command: str(p.command) || str(p.name) || '（不明的程式）', desc });
  }
  if (!rows.length) return null;
  return {
    count: rows.length,
    label: rows.length === 1 ? '背景程式' : rows.length + ' 個背景程式',
    title: '代理另開、脫離 CLI 還在跑的程式（跑完不會通知代理）' + rows.map((r) => '\n・' + r.command).join(''),
    rows,
  };
}

// ───── 指令結果的追蹤（D5；W/cmdtrack.js 用） ─────

const RESULT_STATES = new Set(['done', 'rejected', 'unknown']);

/** 新的追蹤表 */
export function trackNew() {
  return { items: new Map() };
}

/** 登記一個送出的指令（commandId 不是非空字串或已登記過時不登記、回 false） */
export function trackAdd(t, id, info, now) {
  if (!t || !(t.items instanceof Map) || !str(id) || t.items.has(id)) return false;
  t.items.set(id, { info, at: num(now) ?? 0 });
  return true;
}

/**
 * commandResult 到了：認得的 id 移出追蹤表，回 {info, state, code, origin}；不認得、已結束的 id 回 null。
 * 不認得的 state 當成 unknown；origin 是 msg.origin（傳輸層合成的是 'transport'），沒有時 null
 */
export function trackSettle(t, msg) {
  if (!t || !(t.items instanceof Map) || !isObj(msg)) return null;
  const id = str(msg.commandId);
  if (!id || !t.items.has(id)) return null;
  const entry = t.items.get(id);
  t.items.delete(id);
  return { info: entry.info, state: RESULT_STATES.has(msg.state) ? msg.state : 'unknown', code: str(msg.code), origin: str(msg.origin) };
}

/**
 * send 的結果要不要由網頁還原輸入框的字（D5）：只有傳輸層合成的 rate_limited、not_sent（伺服器沒處理這則、不會送 sendRejected）。
 * 伺服器的拒絕由 sendRejected 還原；unknown 可能已執行，不還原
 */
export function sendRestorePlan(settled) {
  return isObj(settled) && settled.origin === 'transport' && settled.state === 'rejected'
    && (settled.code === 'rate_limited' || settled.code === 'not_sent');
}

/** 新的 init：全部當成結果不明，回全部的 info 並清空 */
export function trackAbandon(t) {
  if (!t || !(t.items instanceof Map)) return [];
  const infos = Array.from(t.items.values(), (e) => e.info);
  t.items.clear();
  return infos;
}

/** 過期（登記之後超過 maxMs）的移出並回它們的 info；剛好 maxMs 還不算 */
export function trackExpire(t, now, maxMs = TRACK_MAX_MS) {
  if (!t || !(t.items instanceof Map)) return [];
  const out = [];
  for (const [id, e] of t.items) {
    if (now - e.at > maxMs) {
      out.push(e.info);
      t.items.delete(id);
    }
  }
  return out;
}

/** 結果的說明：unknown 是「結果不明，請看對話內容確認」，其他不另外說明（伺服器已另送提示） */
export function settleText(state) {
  return state === 'unknown' ? UNKNOWN_RESULT_TEXT : null;
}

// ───── 通知、預設密碼、換版 ─────

const NOTIFY_BODY = { completed: '已完成', question: '有問題等你回答', approval: '等你核准', failed: '發生錯誤，沒有完成' };

/** notify 怎麼顯示（D19）：設定關閉 → 'none'；分頁隱藏而且已授權 → 'system'（系統通知）；其他 → 'toast'（頁內提示） */
export function notifyPlan(msg, { enabled, hidden, permission } = {}) {
  if (!isObj(msg) || enabled === false) return 'none';
  if (hidden === true && permission === 'granted') return 'system';
  return 'toast';
}

/** 通知的標題「OFFICE-PC：對話標題」與本文（依 kind）；都是純文字 */
export function notifyText(msg) {
  const m = isObj(msg) ? msg : {};
  const title = str(m.title) || '對話';
  const dev = str(m.deviceName);
  return { title: dev ? dev + '：' + title : title, body: own(NOTIFY_BODY, m.kind) || '有新的動態' };
}

/** control 訊息的預設密碼常駐警告（D20）：只有 defaultPassword 是 true 才有 */
export function controlBanner(control) {
  if (!isObj(control) || control.defaultPassword !== true) return null;
  return { text: DEFAULT_PASSWORD_TEXT, href: '/', linkText: '改密碼' };
}

/** init.app.build：不是非空字串（缺少、數字、空字串）一律 null */
export function normBuild(v) {
  return typeof v === 'string' && v.length > 0 ? v : null;
}

/** 伺服器換版了嗎（D13）：兩邊正規化之後不同就算，包括從有變成沒有（回滾到沒有這個欄位的舊伺服器） */
export function buildChanged(first, next) {
  return normBuild(first) !== normBuild(next);
}

/**
 * 換版之後怎麼做（D13）：
 * - 儲存不能用（記不下這一次，可能無限重新載入；輸入框的字也存不下）→ 'banner'：只顯示橫幅，按了才重新載入
 * - 有重新載入會弄丟的東西（hasUnsentInput：等你回應的卡片打到一半、還在上傳）、開著對話框、有指令還在等結果（busy）、
 *   使用者正在用（看得到這個分頁時：焦點在有字的輸入框〔editing〕，或 RELOAD_IDLE_MS 內操作過）、60 秒內自動重新載入過
 *   → 'wait'：顯示橫幅，之後重查，狀況解除就自動重新載入
 * - 其他 → 'auto'：馬上重新載入（輸入框沒送出的字先存起來，載入後還原）
 * lastReloadAt 是儲存讀到的時間（讀不到傳 null，當成沒載入過）；lastInputAt 是最後一次操作的時間（沒有傳 null）；
 * hidden 是分頁看不到（看不到時不管 editing 與最近的操作）
 */
export function reloadPlan({ hasUnsentInput, dialogOpen, busy, editing, hidden, lastInputAt, lastReloadAt, now, storageOk = true } = {}) {
  if (storageOk === false) return 'banner';
  if (hasUnsentInput || dialogOpen || busy) return 'wait';
  if (hidden !== true) {
    if (editing) return 'wait';
    const input = num(lastInputAt);
    if (input !== null && now - input < RELOAD_IDLE_MS) return 'wait';
  }
  const last = num(lastReloadAt);
  if (last !== null && now - last < RELOAD_MIN_INTERVAL_MS) return 'wait';
  return 'auto';
}

/** 存下的草稿的一筆：對話 id 或伺服器草稿 id、有字的才算；accountId、deviceId 不是非空字串一律 null。不合格回 null */
function reloadDraftOf(d) {
  if (!isObj(d) || !(isConversationId(d.conversationId) || isDraftId(d.conversationId))) return null;
  if (typeof d.text !== 'string' || !d.text.trim()) return null;
  return { conversationId: d.conversationId, text: d.text, accountId: str(d.accountId), deviceId: str(d.deviceId) };
}

/**
 * 重新載入前要存下的輸入框草稿（workspace.draftTexts 的 [{conversationId, text}]，伺服器草稿另帶 accountId、deviceId）：
 * 合格的才留（reloadDraftOf），同一個對話只留第一筆，最多 MAX_RELOAD_DRAFTS 筆。回 JSON 字串；沒有要存的回 null
 */
export function reloadDraftsJson(drafts) {
  const out = [];
  const seen = new Set();
  for (const raw of Array.isArray(drafts) ? drafts : []) {
    const d = reloadDraftOf(raw);
    if (!d || seen.has(d.conversationId)) continue;
    seen.add(d.conversationId);
    out.push(d);
    if (out.length >= MAX_RELOAD_DRAFTS) break;
  }
  return out.length ? JSON.stringify(out) : null;
}

/** 讀回重新載入前存下的草稿（字串）：對話 id → {text, accountId, deviceId}；不是 JSON 陣列、形狀不對的一律略過 */
export function parseReloadDrafts(raw) {
  const map = new Map();
  if (typeof raw !== 'string' || !raw) return map;
  let list;
  try {
    list = JSON.parse(raw);
  } catch {
    return map;
  }
  if (!Array.isArray(list)) return map;
  for (const item of list) {
    const d = reloadDraftOf(item);
    if (!d || map.has(d.conversationId)) continue;
    map.set(d.conversationId, { text: d.text, accountId: d.accountId, deviceId: d.deviceId });
    if (map.size >= MAX_RELOAD_DRAFTS) break;
  }
  return map;
}

// ───── 剪貼簿與相對網址 ─────

/**
 * clipboard 只收自己要的（D42）：pending 是送 copyItem 時記下的 {conversationId, itemId, at}（陣列或 Map 的值），
 * 有同一個對話與項目、而且 now - at 不超過 maxMs 才收
 */
export function clipboardAccept(pending, msg, now, maxMs = CLIPBOARD_MAX_MS) {
  if (!isObj(msg) || !str(msg.conversationId) || !str(msg.itemId)) return false;
  const list = pending instanceof Map ? Array.from(pending.values()) : Array.isArray(pending) ? pending : [];
  return list.some((p) => isObj(p) && p.conversationId === msg.conversationId && p.itemId === msg.itemId
    && num(p.at) !== null && now - p.at <= maxMs);
}

/**
 * 伺服器給的相對網址（D34；/m/、/t/、/f/）：要以其中一個前綴開頭而且後面還有字、不超過 2048 字、
 * 沒有空白、控制字元與反斜線，路徑的每一段（百分比解碼後）不是 . 或 ..、不含斜線或反斜線。合格回原字串，否則 null
 */
export function safeRelativeUrl(url, prefixes = MEDIA_PREFIXES) {
  if (typeof url !== 'string' || url.length === 0 || url.length > MAX_URL_LENGTH) return null;
  if (hasUnsafeChar(url)) return null;
  const list = Array.isArray(prefixes) ? prefixes : [];
  if (!list.some((p) => typeof p === 'string' && p.length > 0 && url.startsWith(p) && url.length > p.length)) return null;
  const path = url.split(/[?#]/, 1)[0];
  for (const seg of path.split('/')) {
    let decoded;
    try {
      decoded = decodeURIComponent(seg);
    } catch {
      return null;
    }
    if (decoded === '.' || decoded === '..' || decoded.includes('/') || hasUnsafeChar(decoded)) return null;
  }
  return url;
}

// ───── 遠端操作紀錄與回收區（桌面設定的對話框） ─────

/** 紀錄的指令名稱（遠端指令型錄 RemoteCommandCatalog 的名稱；不認得的照原字串顯示） */
export const AUDIT_CMD_LABELS = Object.freeze({
  send: '送出訊息',
  cancelQueued: '取消排隊的訊息',
  stop: '停止',
  answer: '回答問卷',
  skipQuestion: '略過問卷',
  approve: '回應核准',
  approvePlan: '回應計畫確認',
  rename: '改名',
  delete: '刪除對話',
  setOption: '改對話設定',
  setAccount: '換帳戶',
  setFolder: '改資料夾',
  resetFolder: '恢復預設資料夾',
  removeAttachment: '移除附件',
  addUploaded: '加入上傳的附件',
  listFiles: '列出檔案',
  fileSearch: '搜尋檔案',
  fileFetch: '讀取檔案',
  conversationCreate: '開新對話',
  accountAdd: '新增帳戶',
  accountLogin: '登入帳戶',
  accountLoginCancel: '取消登入',
  accountLogout: '登出帳戶',
  accountRemove: '移除帳戶',
  accountRefresh: '重新檢查帳戶',
  accountLoginDevice: '在這台登入帳戶',
  cliCheck: '檢查 CLI',
  cliInstall: '安裝 CLI',
  cliUpdate: '更新 CLI',
});

const AUDIT_STATE_TEXT = { done: '已執行', rejected: '已拒絕', unknown: '結果不明', executing: '執行中或不明' };
const AUDIT_CODE_TEXT = {
  offline: '電腦離線', policy: '政策不允許', step_up_required: '要重新輸入密碼', stale: '卡片已處理或內容變了',
  not_owner: '不是這台的對話', rate_limited: '太頻繁', busy: '電腦忙碌', expired: '已過期', conflict: '指令編號衝突',
  invalid: '內容不合格', unsupported: '這一版不支援',
};
const POLICY_TEXT = { Off: '不接受', View: '只能查看', Control: '可以操作' };

/** 紀錄一筆的顯示（D28）：executing →「執行中或不明」、issuer 是 null →「來源名稱無法顯示」、tier 是 null →「—」 */
export function auditView(entry) {
  const e = isObj(entry) ? entry : {};
  const cmd = str(e.cmd);
  const code = str(e.code);
  const tier = num(e.tier);
  return {
    at: num(e.at),
    seq: num(e.seq),
    commandId: str(e.commandId),
    cmdLabel: own(AUDIT_CMD_LABELS, cmd) || cmd || '—',
    conversationId: str(e.conversationId),
    issuer: str(e.issuer) || '來源名稱無法顯示',
    tierText: tier !== null ? '層級 ' + tier : '—',
    fullAccess: e.fullAccess === true,
    policyText: own(POLICY_TEXT, e.policy) || '—',
    stateText: own(AUDIT_STATE_TEXT, e.state) || '—',
    code,
    codeText: code ? own(AUDIT_CODE_TEXT, code) || code : null,
    defaultPassword: e.defaultPassword === true,
    count: Math.max(1, Math.trunc(num(e.count) || 1)),
  };
}

/** 「載入更早的」的參數（D44）：這一頁最舊一筆（at 最小、同 at 時 seq 最小）的 {before: at, beforeSeq: seq}；沒有合格的列回 null */
export function auditNextPage(entries) {
  if (!Array.isArray(entries)) return null;
  let oldest = null;
  for (const e of entries) {
    if (!isObj(e) || num(e.at) === null || num(e.seq) === null) continue;
    if (!oldest || e.at < oldest.at || (e.at === oldest.at && e.seq < oldest.seq)) oldest = e;
  }
  return oldest ? { before: oldest.at, beforeSeq: oldest.seq } : null;
}

/** 回收區一筆的顯示：標題的替代「（沒有標題）」、發起者的替代、剩幾天（無條件進位，最少 0） */
export function trashView(entry, now) {
  const e = isObj(entry) ? entry : {};
  const expiresAt = num(e.expiresAt);
  const daysLeft = expiresAt !== null ? Math.max(0, Math.ceil((expiresAt - now) / DAY)) : null;
  return {
    conversationId: str(e.conversationId),
    title: str(e.title) || '（沒有標題）',
    trashedAt: num(e.trashedAt),
    issuer: str(e.issuer) || '來源名稱無法顯示',
    commandId: str(e.commandId),
    expiresAt,
    daysLeft,
    daysText: daysLeft === null ? null : daysLeft > 0 ? '還剩 ' + daysLeft + ' 天' : '今天到期',
  };
}

// ───── 對話的同步政策（桌面，D25） ─────

const POLICY_LABELS = { full: '完整同步', metadata: '只同步標題與狀態', localOnly: '只留在這台' };

/** 「同步到伺服器」選單：照 header.conversationSync.choices 的順序與標籤，不認得的值略過；current 標出目前的政策 */
export function policyChoices(conversationSync) {
  if (!isObj(conversationSync) || !Array.isArray(conversationSync.choices)) return [];
  const out = [];
  const seen = new Set();
  for (const v of conversationSync.choices) {
    if (!own(POLICY_LABELS, v) || seen.has(v)) continue;
    seen.add(v);
    out.push({ value: v, label: POLICY_LABELS[v], current: v === conversationSync.policy });
  }
  return out;
}

/** 只有從別的政策改成 localOnly 才要確認（會從伺服器刪除） */
export function needsLocalOnlyConfirm(current, next) {
  return next === 'localOnly' && current !== 'localOnly';
}

// ───── 桌面設定的同步區與副本的「在網頁版開啟」（步 10） ─────

/** 整數加千分位（12345 → 12,345；同 C# 的 N0） */
function groupDigits(n) {
  const s = String(n);
  let out = '';
  for (let i = 0; i < s.length; i++) {
    if (i > 0 && (s.length - i) % 3 === 0) out += ',';
    out += s[i];
  }
  return out;
}

/**
 * 回補的進度（SyncStatus.backfill）：「還有 12 個對話（約 50 MB）」。位元組無條件進位到 MB（千分位），不到 1 MB 寫「不到 1 MB」
 * （同系統匣的 TraySyncRules.BackfillText）；沒有在回補（省略、null）或數字不合格時 null（不顯示這一列）
 */
export function backfillText(backfill) {
  if (!isObj(backfill)) return null;
  const left = num(backfill.conversationsLeft);
  const bytes = num(backfill.bytesLeft);
  if (left === null || left < 0 || bytes === null || bytes < 0) return null;
  const size = bytes < MB ? '不到 1 MB' : '約 ' + groupDigits(Math.ceil(bytes / MB)) + ' MB';
  return '還有 ' + Math.trunc(left) + ' 個對話（' + size + '）';
}

/**
 * 副本的「在網頁版開啟」（D22）：伺服器（SyncStatus.server，https 的來源、沒有路徑）的 /app/?mode=popout&conversation=<id>，
 * 同 transport-hub 的彈出視窗網址；伺服器或 id 不合格時 null（不顯示按鈕）
 */
export function replicaWebUrl(server, conversationId) {
  const s = str(server);
  const id = str(conversationId);
  if (!s || !id || !/^https:\/\/[^/?#@\s]+\/*$/i.test(s)) return null;
  return s.replace(/\/+$/, '') + '/app/?mode=popout&conversation=' + encodeURIComponent(id);
}

// ───── 選電腦與草稿換電腦（D16） ─────

/**
 * 新對話開在哪台電腦（D16）。候選＝這個帳戶 accounts[].devices[] 裡 status 是 ready、identityOk 不是 false、online 的電腦，
 * 再交叉 devices（remote.devices）裡政策是 Control、沒撤銷、不是離線的。
 * 目前聚焦對話的那台是候選就用它，否則這個帳戶上次用的那台，否則只有一台時直接用、多台時要問（ask）；沒有候選時回原因。
 * choices 一律照「聚焦的、上次用的、其他照帳戶的順序」排，給選擇框與草稿的換電腦用。回 {deviceId, ask, choices:[{deviceId, name}], reason}
 */
export function pickDevice({ accountId, accounts, devices, focusedDeviceId, lastDeviceId } = {}) {
  const none = (reason) => ({ deviceId: null, ask: false, choices: [], reason });
  const account = Array.isArray(accounts) ? accounts.find((a) => isObj(a) && a.id === accountId) : null;
  if (!account) return none('找不到這個帳戶');
  const cells = Array.isArray(account.devices) ? account.devices.filter((c) => isObj(c) && str(c.deviceId)) : [];
  const list = Array.isArray(devices) ? devices : [];
  const ready = cells.filter((c) => c.status === 'ready' && c.identityOk !== false);
  if (!ready.length) return none('沒有一台電腦登入了這個帳戶');
  const online = ready.filter((c) => c.online === true);
  if (!online.length) return none('登入這個帳戶的電腦都不在線');
  const candidates = [];
  const seen = new Set();
  for (const c of online) {
    const d = list.find((x) => isObj(x) && x.id === c.deviceId);
    if (!d || d.remoteControlLevel !== 'Control' || d.revoked || d.online === false || seen.has(c.deviceId)) continue;
    seen.add(c.deviceId);
    candidates.push({ deviceId: c.deviceId, name: str(d.name) || str(c.deviceName) || UNKNOWN_DEVICE });
  }
  if (!candidates.length) return none('登入這個帳戶的電腦都沒有允許遠端操作：在那台電腦的設定中允許遠端操作');
  const rank = (c) => (c.deviceId === focusedDeviceId ? 0 : c.deviceId === lastDeviceId ? 1 : 2);
  const choices = candidates.map((c, i) => ({ c, i })).sort((a, b) => rank(a.c) - rank(b.c) || a.i - b.i).map((x) => x.c);
  const preferred = choices.find((c) => rank(c) < 2);
  if (preferred) return { deviceId: preferred.deviceId, ask: false, choices, reason: null };
  if (choices.length === 1) return { deviceId: choices[0].deviceId, ask: false, choices, reason: null };
  return { deviceId: null, ask: true, choices, reason: null };
}

/**
 * 草稿換電腦時要重送到新草稿的指令（D16）：model、effort、permission 和 defaults 不同時 setOption（model 在 effort 之前，
 * 換模型會重設強度）；customFolder 時 setFolder；標題不是預設的「新對話」時 rename。
 * defaults 是新草稿標頭的值（省略的用 DRAFT_DEFAULTS）；給了 conversationId（新草稿的 id）就每個指令都帶上
 */
export function draftCarryOver(oldHeader, defaults, conversationId) {
  if (!isObj(oldHeader)) return [];
  const d = Object.assign({}, DRAFT_DEFAULTS);
  if (isObj(defaults)) {
    for (const k of Object.keys(DRAFT_DEFAULTS)) if (str(defaults[k])) d[k] = defaults[k];
  }
  const out = [];
  for (const key of ['model', 'effort', 'permission']) {
    const v = str(oldHeader[key]);
    if (v && v !== d[key]) out.push({ cmd: 'setOption', key, value: v });
  }
  const folder = str(oldHeader.workingDirectory);
  if (oldHeader.customFolder === true && folder) out.push({ cmd: 'setFolder', path: folder });
  const title = str(oldHeader.title);
  if (title && title !== d.title) out.push({ cmd: 'rename', title });
  const id = str(conversationId);
  return id ? out.map((c) => Object.assign({ conversationId: id }, c)) : out;
}

// ───── 刪除、歷史篩選 ─────

/**
 * 刪除前「一併停止背景程式」的勾選（2026-10-24）：這個對話另開、還在跑的程式數。info 是 Header（programs 是陣列）或
 * ConversationSummary（programCount）；只有桌面這台的對話有，瀏覽器版與副本是 0（不顯示勾選）
 */
export function deleteProgramsCheckbox(mode, origin, info) {
  if (mode !== 'desktop' || origin !== 'local' || !isObj(info)) return null;
  const n = Array.isArray(info.programs) ? info.programs.length : (Number.isFinite(info.programCount) && info.programCount > 0 ? Math.floor(info.programCount) : 0);
  if (n <= 0) return null;
  return {
    label: n === 1 ? '一併停止這個對話另開、還在跑的程式' : `一併停止這個對話另開、還在跑的 ${n} 個程式`,
    note: '不停止的話它們會繼續跑，刪除後在 Myriagent 裡就看不到了',
    checked: true,
  };
}

/**
 * 刪除對話的確認文字（D43）：瀏覽器是「會移到 OFFICE-PC 的回收區，30 天內可在那台電腦的設定還原」；桌面這台的對話照舊「無法復原」；
 * 副本與草稿不提供刪除（null）。info 是 Header 或 ConversationSummary（title、running、customFolder）。回 {message, detail}
 */
export function deleteConfirmText(mode, origin, deviceName, info) {
  const i = isObj(info) ? info : {};
  const name = '「' + (str(i.title) || '未命名對話') + '」';
  const stop = i.running === true ? '執行中的這一輪會被停止。' : '';
  if (origin === 'replica' || origin === 'draft') return null;
  if (isHubMode(mode)) {
    return {
      message: name + '會移到' + devLabel({ name: deviceName }, true, true) + '的回收區，30 天內可在那台電腦的設定還原。' + stop,
      detail: null,
    };
  }
  const withWorkspace = i.customFolder === false;
  return {
    message: name + (withWorkspace ? '的對話紀錄、附件，以及預設工作區裡的所有檔案都會一起刪除，無法復原。' : '的對話紀錄與附件會一起刪除，無法復原。') + stop,
    detail: withWorkspace ? '這個對話沒有選工作資料夾，代理寫出的程式、文件都放在預設工作區；要保留請先複製到別的地方。' : null,
  };
}

/**
 * 側欄的電腦篩選（D31 起；2026-10-23 改成下拉、可選某一台）：device 省略、null 或 'all'＝全部；
 * 'local'＝桌面這台（沒有 deviceId 的，瀏覽器沒有「這台」、不篩）；其他字串＝那台的 deviceId
 */
export function filterHistory(list, device, mode) {
  if (!Array.isArray(list)) return [];
  const d = str(device);
  if (!d || d === 'all') return list;
  if (d === 'local') return isHubMode(mode) ? list : list.filter((c) => isObj(c) && !str(c.deviceId));
  return list.filter((c) => isObj(c) && str(c.deviceId) === d);
}

/**
 * 側欄電腦下拉的選項（不含「所有電腦」）：桌面是「這台電腦」（value 'local'）＋有副本的其他電腦，瀏覽器是有對話的電腦。
 * 其他電腦照 remote.devices 的順序，名冊裡沒有的排在後面（照名稱）。只有一台（桌面沒有副本、瀏覽器只有一台）時 choices 是空的＝不顯示下拉。
 * scoped 是套過其他篩選（帳戶）的清單，只用來算各台的對話數（省略＝list）。value 是實際生效的選擇：不在選項裡時 null（＝全部，選擇照樣留在版面裡）。
 * 回 { choices: [{ value, name, online, reconnecting, count }], value }
 */
export function deviceFilterChoices({ list, scoped, mode, remote, selected } = {}) {
  const all = Array.isArray(list) ? list.filter(isObj) : [];
  const counted = Array.isArray(scoped) ? scoped.filter(isObj) : all;
  const hub = isHubMode(mode);
  const firstOf = new Map(); // deviceId → 那台的第一個對話（名冊裡沒有時用它的 deviceName、deviceOnline）
  for (const c of all) {
    const id = str(c.deviceId);
    if (id && !firstOf.has(id)) firstOf.set(id, c);
  }
  if (firstOf.size < (hub ? 2 : 1)) return { choices: [], value: null };
  const roster = isObj(remote) && Array.isArray(remote.devices) ? remote.devices : [];
  const rank = (id) => { const i = roster.findIndex((d) => isObj(d) && d.id === id); return i < 0 ? roster.length : i; };
  const devs = [...firstOf.keys()].map((id) => deviceOf(id, remote, firstOf.get(id)))
    .sort((a, b) => rank(a.id) - rank(b.id) || (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
  const countOf = (pred) => counted.reduce((n, c) => n + (pred(str(c.deviceId)) ? 1 : 0), 0);
  const choices = [];
  if (!hub) choices.push({ value: 'local', name: '這台電腦', online: null, reconnecting: false, count: countOf((id) => !id) });
  for (const d of devs) choices.push({ value: d.id, name: d.name, online: d.online, reconnecting: d.reconnecting, count: countOf((id) => id === d.id) });
  const sel = str(selected);
  return { choices, value: choices.some((c) => c.value === sel) ? sel : null };
}

/** 版面存的電腦篩選：historyDevice（某台的 deviceId）優先，否則 historyScope 是 local＝這台，其他＝全部（null） */
export function layoutDeviceFilter(layout) {
  if (!isObj(layout)) return null;
  const id = str(layout.historyDevice);
  if (id && id !== 'local' && id !== 'all') return id;
  return layout.historyScope === 'local' ? 'local' : null;
}

/** 寫進版面的電腦篩選：historyScope 一律有（舊版只認它，選某一台時是 all）、選某一台時另有 historyDevice */
export function layoutDeviceFields(device) {
  const d = str(device);
  if (!d || d === 'all') return { historyScope: 'all' };
  if (d === 'local') return { historyScope: 'local' };
  return { historyScope: 'all', historyDevice: d };
}

/** 路徑的比較鍵：斜線統一成反斜線、去掉結尾的分隔符號、不分大小寫（Windows 的路徑） */
const pathKey = (p) => p.replace(/\//g, '\\').replace(/\\+$/, '').toLowerCase();

/** 路徑的最後一段（資料夾名稱；只有磁碟根目錄時是「D:」） */
function lastSegment(p) {
  const parts = p.split(/[\\/]+/).filter(Boolean);
  return parts.length ? parts[parts.length - 1] : p;
}

/**
 * 對話所在的工作區（側欄的工作區篩選，2026-10-23）：沒選資料夾（customFolder 是 false）＝'default'（各對話自己的預設工作區合成一項）；
 * 自選資料夾＝'dir:'＋路徑的比較鍵。不歸類的是 null（只算在「所有工作區」）：桌面上其他電腦的副本（清單帶的是那台電腦上的資料夾，
 * 同一個路徑也不是這台的同一個資料夾，不和這台的混在一起）、自選卻沒有路徑的
 */
export function folderKeyOf(conv, mode) {
  if (!isObj(conv) || originOf(conv, mode) === 'replica') return null;
  if (conv.customFolder === false) return 'default';
  const p = str(conv.workingDirectory);
  return p ? 'dir:' + pathKey(p) : null;
}

/**
 * 側欄工作區下拉的選項（不含「所有工作區」）：清單用到的自選資料夾照最近的對話排（updatedAt 新的在前，相同照清單順序），
 * 有沒選資料夾的對話時「預設工作區」排最後。name 是資料夾名稱（路徑最後一段）、path 是那個資料夾最近的對話寫的完整路徑（預設工作區是 null）。
 * 少於兩個選項時 choices 是空的＝不顯示下拉。value 是實際生效的選擇：不在選項裡時 null（＝全部，選擇照樣留著）。
 * 各選項的對話數由呼叫端照其他篩選算（folderCounts）。回 { choices: [{ value, name, path }], value }
 */
export function folderFilterChoices({ list, mode, selected } = {}) {
  const dirs = new Map(); // 比較鍵 → { at, order, path }
  let hasDefault = false;
  for (const c of Array.isArray(list) ? list : []) {
    const key = folderKeyOf(c, mode);
    if (key === 'default') hasDefault = true;
    if (!key || key === 'default') continue;
    const at = num(c.updatedAt) ?? -Infinity;
    const cur = dirs.get(key);
    if (!cur) dirs.set(key, { at, order: dirs.size, path: str(c.workingDirectory) });
    else if (at > cur.at) Object.assign(cur, { at, path: str(c.workingDirectory) });
  }
  const choices = [...dirs.entries()].sort(([, a], [, b]) => (b.at - a.at) || (a.order - b.order))
    .map(([value, d]) => ({ value, name: lastSegment(d.path), path: d.path }));
  if (hasDefault) choices.push({ value: 'default', name: '預設工作區', path: null });
  if (choices.length < 2) return { choices: [], value: null };
  const sel = str(selected);
  return { choices, value: choices.some((c) => c.value === sel) ? sel : null };
}

/** 依工作區篩選：value 省略或 null＝全部，其他是 folderFilterChoices 的 value */
export function filterFolder(list, value, mode) {
  if (!Array.isArray(list)) return [];
  const v = str(value);
  return v ? list.filter((c) => folderKeyOf(c, mode) === v) : list;
}

/** 各工作區的對話數（folderKeyOf 是 null 的不算）：Map（工作區 → 數目） */
export function folderCounts(list, mode) {
  const out = new Map();
  for (const c of Array.isArray(list) ? list : []) {
    const key = folderKeyOf(c, mode);
    if (key) out.set(key, (out.get(key) || 0) + 1);
  }
  return out;
}

// ───── 上傳、附件晶片與檔案預覽（D32、D34） ─────

/** 大小的文字（無條件捨去到 0.1；不到 1 KB 寫位元組） */
function sizeText(bytes) {
  if (bytes === null) return null;
  if (bytes < 1024) return bytes + ' B';
  if (bytes < MB) return (Math.floor(bytes / 102.4) / 10) + ' KB';
  if (bytes < 1024 * MB) return (Math.floor(bytes / (MB / 10)) / 10) + ' MB';
  return (Math.floor(bytes / (1024 * MB / 10)) / 10) + ' GB';
}

/** 上傳之前的本機檢查：大小要在 0～512 MB（含）之間（File 或有 size 的物件） */
export function uploadCheck(file) {
  const size = file && typeof file === 'object' ? num(file.size) : null;
  if (size === null || size < 0) return { ok: false, reason: '讀不到檔案大小' };
  if (size > UPLOAD_MAX_BYTES) return { ok: false, reason: '檔案超過 512 MB，不能從網頁上傳' };
  return { ok: true, reason: null };
}

/** E4 回應碼的說明（0＝網路錯誤） */
export function uploadErrorText(status) {
  switch (status) {
    case 409: return '這個檔案的上傳內容和先前不同，請重新附加';
    case 413: return '檔案超過 512 MB，不能從網頁上傳';
    case 507: return '伺服器的空間不足或待處理的上傳太多，請稍後再試';
    case 0: return '網路中斷，上傳沒有完成';
    default: return '上傳失敗（HTTP ' + (num(status) ?? '?') + '）';
  }
}

/** 附件晶片在事件之後（D32）：send 完成（sendDone）或對話關閉（closed）清空；sendRejected 與其他保留 */
export function chipsAfter(chips, event) {
  const list = Array.isArray(chips) ? chips : [];
  return event === 'sendDone' || event === 'closed' ? [] : list;
}

/** fileRef 的預覽對話框資料（D34）：error 不是 null、或網址不是 /f/ 開頭的合格相對網址，一律回固定的錯誤文字 */
export function fileRefView(msg) {
  const m = isObj(msg) ? msg : {};
  const base = { requestId: str(m.requestId), conversationId: str(m.conversationId) };
  if (!isObj(msg) || (m.error !== null && m.error !== undefined)) return Object.assign(base, { ok: false, error: FILE_REF_ERROR_TEXT });
  const url = safeRelativeUrl(m.url, ['/f/']);
  if (!url) return Object.assign(base, { ok: false, error: FILE_REF_ERROR_TEXT });
  const size = num(m.size);
  return Object.assign(base, {
    ok: true,
    error: null,
    url,
    name: str(m.name) || '檔案',
    size,
    sizeText: sizeText(size !== null && size >= 0 ? size : null),
    expiresAt: num(m.expiresAt),
  });
}

/** 一則訊息最多附加幾個網頁上傳的檔案（addUploaded 的 1～20 個，AgentAttachmentRules.MaxAttachmentsPerMessage） */
export const MAX_UPLOADS = 20;
/** 上傳的檔名上限（addUploaded 的 name 1～260 字，RemoteCommandShapeRules.MaxUploadNameLength） */
export const MAX_UPLOAD_NAME = 260;
/** addUploaded 的 size 上限（2 GB，AgentAttachmentRules.MaxAttachmentBytes） */
const MAX_UPLOADED_SIZE = 2048 * MB;
/** E4 的 uploadId：1～64 個英數字、_、-（UploadIdRules） */
const UPLOAD_ID_RE = /^[A-Za-z0-9_-]{1,64}$/;
const SHA256_RE = /^[0-9a-f]{64}$/;
/** 超過每則上限時沒加入的說明 */
export const UPLOAD_LIMIT_TEXT = '一則訊息最多附加 ' + MAX_UPLOADS + ' 個檔案';
/** E4 回 2xx 但內容對不上（沒有 uploadId、sha256、size，或 id 不是這次的）時的說明 */
export const UPLOAD_BAD_RESPONSE_TEXT = '伺服器的回應不正確，上傳沒有完成';

/**
 * 一批要上傳的檔案（D32）：逐一照 uploadCheck；輸入區已有 existing 個（上傳中、加入中與已附加）時，超過每則 MAX_UPLOADS 個的不收。
 * 回 { accept: [file…], tooLarge: [{ file, reason }], overLimit: 沒收的個數 }（accept 照原順序）
 */
export function uploadBatch(files, existing) {
  const list = Array.isArray(files) ? files : [];
  let room = Math.max(0, MAX_UPLOADS - (Number.isInteger(existing) && existing > 0 ? existing : 0));
  const out = { accept: [], tooLarge: [], overLimit: 0 };
  for (const f of list) {
    const c = uploadCheck(f);
    if (!c.ok) { out.tooLarge.push({ file: f, reason: c.reason }); continue; }
    if (room <= 0) { out.overLimit++; continue; }
    room--;
    out.accept.push(f);
  }
  return out;
}

/**
 * addUploaded 的檔名（1～260 字）：去掉前後空白；空的用 fallback（預設「貼上的檔案」）；太長時保留副檔名、從主檔名的尾端截掉
 * （不切開代理字元對；副檔名本身超過 16 字時整個名稱照截）
 */
export function uploadName(name, fallback) {
  const fb = str(fallback) || '貼上的檔案';
  const s = typeof name === 'string' ? name.trim() : '';
  if (!s) return fb;
  if (s.length <= MAX_UPLOAD_NAME) return s;
  const dot = s.lastIndexOf('.');
  const ext = dot > 0 && s.length - dot <= 16 ? s.slice(dot) : '';
  let base = s.slice(0, MAX_UPLOAD_NAME - ext.length);
  const last = base.charCodeAt(base.length - 1);
  if (last >= 0xd800 && last <= 0xdbff) base = base.slice(0, -1);
  return base + ext;
}

/** 一筆 addUploaded 的附件 {uploadId, name, size, sha256}：形狀都對才回物件，否則 null（照伺服器的 RemoteCommandShapeRules.CheckUploads） */
function uploadEntry(uploadId, name, size, sha256) {
  if (typeof uploadId !== 'string' || !UPLOAD_ID_RE.test(uploadId)) return null;
  if (typeof name !== 'string' || !name || name.length > MAX_UPLOAD_NAME) return null;
  if (!Number.isInteger(size) || size < 0 || size > MAX_UPLOADED_SIZE) return null;
  if (typeof sha256 !== 'string' || !SHA256_RE.test(sha256)) return null;
  return { uploadId, name, size, sha256 };
}

/**
 * E4 PUT 的結果（D32）：201／200 而且回應的 uploadId 是這次的、sha256 是 64 個小寫十六進位、size 是整數 → {ok:true, entry}
 * （size、sha256 用伺服器回的，瀏覽器不算雜湊）；其他 → {ok:false, error}（uploadErrorText；2xx 但內容對不上是 UPLOAD_BAD_RESPONSE_TEXT）
 */
export function uploadResult(status, json, uploadId, name) {
  if (status !== 200 && status !== 201) return { ok: false, error: uploadErrorText(status) };
  const j = isObj(json) ? json : {};
  const entry = j.uploadId === uploadId ? uploadEntry(uploadId, name, j.size, j.sha256) : null;
  return entry ? { ok: true, entry } : { ok: false, error: UPLOAD_BAD_RESPONSE_TEXT };
}

/** sessionStorage 存的附件晶片（D32 的 myriagent.app.uploaded.v1:{conversationId}）的鍵 */
export function uploadsKey(conversationId) {
  return 'myriagent.app.uploaded.v1:' + String(conversationId);
}

/** 讀回 sessionStorage 的附件晶片（字串）：不是 JSON 陣列回 []；每筆形狀照 addUploaded，不合格的與重複的 uploadId 略過，最多 MAX_UPLOADS 筆 */
export function storedUploads(raw) {
  let v;
  try {
    v = typeof raw === 'string' ? JSON.parse(raw) : null;
  } catch {
    return [];
  }
  if (!Array.isArray(v)) return [];
  const out = [];
  const seen = new Set();
  for (const x of v) {
    const e = isObj(x) ? uploadEntry(x.uploadId, x.name, x.size, x.sha256) : null;
    if (!e || seen.has(e.uploadId)) continue;
    seen.add(e.uploadId);
    out.push(e);
    if (out.length >= MAX_UPLOADS) break;
  }
  return out;
}

/** 電腦沒連著伺服器時的停用說明（送出、卡片、附加檔案、瀏覽資料夾）：剛斷線（deviceOf 的 reconnecting）是「重新連線中」，其他是「離線」 */
function offlineReason(device, what) {
  return device.reconnecting === true
    ? '電腦重新連線中：' + devLabel(device, false, true) + '連上後才能' + what
    : '電腦離線：' + devLabel(device, false, true) + '重新上線後才能' + what;
}

/** 這台電腦不能遠端操作時的停用說明（附加檔案、瀏覽資料夾共用）：沒連線、電腦離線、政策不是 Control；都不是回 null */
function remoteBlock(device, live, what) {
  if (live !== true) return '目前沒有連線，連上之後才能' + what;
  if (device && device.online === false) return offlineReason(device, what);
  if (device && typeof device.level === 'string' && device.level !== 'Control') return policyText(device);
  return null;
}

/**
 * 附加檔案的閘門（D32、1.2）：桌面只擋副本與沒連線的遠端草稿；瀏覽器版照沒連線、電腦離線、政策不是 Control 擋（完全自動不擋：擋的是送出）。
 * 新對話（瀏覽器版的伺服器草稿、桌面要在其他電腦開的草稿）在第一則送出之前也能附加（2026-11-13 使用者要求：新對話也要能貼圖）：
 * 檔先上傳到伺服器、記在草稿上，第一則送出時連檔一起交給那台。回 {enabled, reason}
 */
export function attachGate({ mode, origin, device, live, peer, remoteDraft } = {}) {
  // 〔桌面遠端操作〕要在其他電腦開的新對話：宿主代傳上傳（同副本），沒連上伺服器時不行
  if (remoteDraft && origin === 'draft' && !isHubMode(mode)) {
    if (peer === 'down') return { enabled: false, reason: PEER_DOWN_TEXT };
    if (peer !== 'live') return { enabled: false, reason: '目前沒有連上伺服器，連上之後才能附加檔案' };
    const blocked = remoteBlock(device, true, '附加檔案');
    return { enabled: !blocked, reason: blocked };
  }
  if (origin === 'replica') {
    if (peer === 'down') return { enabled: false, reason: PEER_DOWN_TEXT };
    if (peer !== 'live') return { enabled: false, reason: replicaText(device) };
    // 〔桌面遠端操作〕可以操作的副本：比照瀏覽器版（電腦離線、政策不是 Control 時擋；桌面的連線由 peer 判斷）
    const blocked = remoteBlock(device, true, '附加檔案');
    return { enabled: !blocked, reason: blocked };
  }
  if (!isHubMode(mode)) return { enabled: true, reason: null };
  const reason = remoteBlock(device, live, '附加檔案');
  return { enabled: !reason, reason };
}

/**
 * 對話所在的那台正在執行（換工作資料夾這類「執行中不能做」的限制）：桌面上其他電腦的副本看擁有者那邊的 header.replica.running
 * （副本的 header.running 是這台的，一律 false），其他看 header.running
 */
export function ownerRunning(header, origin) {
  if (!isObj(header)) return false;
  if (origin === 'replica') return isObj(header.replica) && header.replica.running === true;
  return header.running === true;
}

/**
 * 「瀏覽資料夾…」（D33）：瀏覽器版的對話與新對話（伺服器草稿，2026-10-27：伺服器把草稿的 listFiles dirsOnly 轉給選的那台，列它的最近資料夾）有；
 * 桌面用資料夾對話框，桌面的遠端草稿只能手輸路徑或挑最近用過的。
 * 執行中（副本看擁有者那邊的，ownerRunning）、沒連線、電腦離線、政策不是 Control（dirsOnly 是層級 2，要 Control）時停用並說明。回 {shown, enabled, reason}
 */
export function folderPickerGate({ mode, origin, header, device, live, peer } = {}) {
  // 〔桌面遠端操作〕可以操作的副本同瀏覽器版（桌面的連線由 peer 判斷）
  if (controllable(origin, peer)) live = true;
  else if (!isHubMode(mode) || (origin !== 'remote' && origin !== 'draft')) return { shown: false, enabled: false, reason: null };
  if (ownerRunning(header, origin)) return { shown: true, enabled: false, reason: '執行中不能更換' };
  const reason = remoteBlock(device, live, '瀏覽資料夾');
  return { shown: true, enabled: !reason, reason };
}

/** 〔遠端〕抽屜的 listFiles 被拒時的說明：新對話（草稿）被拒成 invalid 是那台的版本還不認得新對話的資料夾挑選（listFolders） */
export const FOLDER_LIST_OLD_TEXT = '那台電腦的 Clode Myriagent 版本太舊，還不能在新對話瀏覽資料夾；請先更新那台，或改用「輸入資料夾路徑…」';

export function listFailText(code, isDraft) {
  return isDraft === true && code === 'invalid' ? FOLDER_LIST_OLD_TEXT : '讀不到這個資料夾';
}

/** 影片載入失敗時的說明（擁有者的 WP4.7 Q1）：那台電腦的遠端政策是 Off 或暫停（DeviceView 的 remoteControlLevel 是 Off）時只看得到縮圖；其他 null */
export function mediaPolicyNote(device) {
  return isObj(device) && device.level === 'Off' ? '這台電腦的遠端政策是關閉，只能看縮圖' : null;
}

// ───── 一直沒有載入完成的對話 ─────

/**
 * 看得到的對話一直停在「載入對話中…」時，第幾次自動重送 open 之前要等多久（2026-11-07 使用者回報：分頁一直載入不完）。
 * 送出的 open 或回來的 conversation 在哪裡掉了都救得回來（宿主與伺服器收到重複的 open 只是再送一次整份）；用完就改成「重試」鈕
 */
export const LOAD_RETRY_MS = [6000, 15000, 30000];

/** 第 attempt 次（0 起算）自動重送之前要等的毫秒；用完回 null */
export function loadRetryDelay(attempt) {
  return Number.isInteger(attempt) && attempt >= 0 && attempt < LOAD_RETRY_MS.length ? LOAD_RETRY_MS[attempt] : null;
}

// ───── 一次 open 很多分頁的順序 ─────

/**
 * 瀏覽器版還原版面與重連（reopenAll）時每個分頁都送 open，伺服器對同一條連線照收到的順序一個一個回（送出佇列滿一半就等網頁收完），
 * 網頁收到一個就建一個：看得到的分頁排在後面就要等前面的都下載、畫完（手機的分頁越開越多，回到 app 載入就越來越久）。
 * 回傳送 open 的順序：先聚焦窗格作用中的分頁、再其他窗格作用中的分頁（窗格順序），最後其餘的分頁（窗格與分頁原本的順序）；
 * 每個 id 只出現一次。panes＝[{ id, tabs, active }]，壞的窗格與不是字串的 id 略過
 */
export function openOrder(panes, focusedPaneId) {
  const list = Array.isArray(panes) ? panes.filter((p) => p && Array.isArray(p.tabs)) : [];
  const out = [];
  const seen = new Set();
  const add = (id) => { if (typeof id === 'string' && id && !seen.has(id)) { seen.add(id); out.push(id); } };
  const focused = list.find((p) => p.id === focusedPaneId);
  if (focused && focused.tabs.includes(focused.active)) add(focused.active);
  for (const p of list) if (p.tabs.includes(p.active)) add(p.active);
  for (const p of list) for (const id of p.tabs) add(id);
  return out;
}

// ───── 大對話的分批載入（D39，步 12） ─────

/** 瀏覽器版載入對話時每批放進 DOM 的項目數 */
export const LOAD_CHUNK = 20;
/**
 * 觸控的瀏覽器版載入時先不排版的項目（.it-lazy，conversation.js 的 nextLazy）每個影格換回實際高度幾則：
 * 量法見 tools/ui-probe/open-cost.mjs
 */
export const LAZY_BATCH = 6;

/**
 * 載入對話時分批放進 DOM 的範圍（D39）：桌面一次全放；瀏覽器版先放最後 chunk 筆，其餘每個影格往前補 chunk 筆
 * （1 MB、80 項的對話一次建完是兩個 >50 ms 的長工作，比桌面多一個）。回 [[start, end), …]，依放進的順序（第一段是最後面的），
 * 合起來剛好是 [0, count) 各一次；count 不是正整數回 []
 */
export function loadChunks(count, mode, chunk = LOAD_CHUNK) {
  const n = Number.isInteger(count) && count > 0 ? count : 0;
  if (!n) return [];
  const size = Number.isInteger(chunk) && chunk > 0 ? chunk : LOAD_CHUNK;
  if (mode !== 'hub' || n <= size) return [[0, n]];
  const out = [];
  for (let end = n; end > 0; end -= size) out.push([Math.max(0, end - size), end]);
  return out;
}

// ───── 觸控捲動（手機、平板） ─────

/** 手指往下拖超過這麼多（內容往上捲、要看較早的）就停止自動貼底（同滑鼠滾輪往上） */
export const TOUCH_UNSTICK_PX = 6;
/** 放開手指之後多久內的捲動事件算慣性捲動（iOS 的慣性最長約兩三秒） */
export const MOMENTUM_MAX_MS = 3000;
/** 捲動事件停了這麼久就算停下來了 */
export const SCROLL_IDLE_MS = 150;

/** 手指從 startY 拖到 y：往下拖超過 TOUCH_UNSTICK_PX 才算（輕點、往上拖、橫向滑不算）；不是數字回 false */
export function touchUnsticks(startY, y) {
  return Number.isFinite(startY) && Number.isFinite(y) && y - startY > TOUCH_UNSTICK_PX;
}

/**
 * 項目的更新要不要不等影格、先在訊息的工作裡套用並貼底（app.js 的提早套用，2026-11-12）：
 * 跟著最底的對話每長一次就要捲到最底，以前在影格裡（ResizeObserver）捲——瀏覽器的捲動事件要等下一格才送得出去，
 * 每次串流更新之後就多畫一格什麼都沒變的畫面；在影格之前捲，事件就排進同一格。
 * 只在真的會捲的時候提早：頁面看得到、沒有在拖分頁（拖曳期間更新先排著）、對話已載入、看得到、跟著最底。
 * 排著的更新裡有會建新節點的（fresh：新項目、要整個重建的卡片、移除）就整批照舊在影格裡套用：新建的 .it 是
 * content-visibility: auto，第一格畫面決定它看得到之前內容不算樣式、高度是估計的——提早貼底也省不到那一格，
 * 而且影格之前讀頁面的程式（探針）會看到還沒有樣式的內容。
 * s＝{ hidden, dragging, fresh, loaded, visible, stick }
 */
export function earlyFlushWanted(s) {
  return !!s && s.hidden !== true && s.dragging !== true && s.fresh !== true && s.loaded === true && s.visible === true && s.stick === true;
}

/**
 * 使用者正在以觸控捲動：手指按著，或放開後 MOMENTUM_MAX_MS 內最近一次捲動事件不到 SCROLL_IDLE_MS（慣性還在動）。
 * 這段期間不把較早的項目插進最前面（分批渲染、載入較早的訊息）：插進去要改 scrollTop 才停得住原本看的位置，
 * iOS Safari 在捲動中改 scrollTop 會讓畫面跳一下、慣性中斷（使用者回報：手機往上滑一直閃）。
 * s＝{ touching, touchEndAt, lastScrollAt }（performance.now() 的毫秒；沒有的是 null）
 */
export function touchScrolling(s, now) {
  if (!s) return false;
  if (s.touching) return true;
  if (!Number.isFinite(s.touchEndAt) || !Number.isFinite(s.lastScrollAt) || !Number.isFinite(now)) return false;
  return now - s.touchEndAt < MOMENTUM_MAX_MS && now - s.lastScrollAt < SCROLL_IDLE_MS;
}

/**
 * 沒有捲動錨定的瀏覽器要補多少 scrollTop（scrollanchor.js）：changes 是這一個影格高度變了的項目，依文件順序，
 * 各帶變動之後的下緣（bottom，畫面座標）與高度的變化（delta）；viewTop 是捲動區的上緣。
 * 變動之前整則都在畫面上緣以上的（下緣 ≤ viewTop）才補：它變高變矮，畫面上看著的內容就跟著被推動。較前面的變動會推動後面的，
 * 所以變動之前的下緣＝現在的下緣 − 自己的 delta − 前面各則的 delta。跨在上緣的那一則不補（看不出變的是上面還是畫面裡的部分），
 * 畫面裡與下面的不補。不是數字的略過
 */
export function anchorShift(changes, viewTop) {
  if (!Array.isArray(changes) || !Number.isFinite(viewTop)) return 0;
  let shift = 0;
  let earlier = 0;
  for (const c of changes) {
    if (!c || !Number.isFinite(c.bottom) || !Number.isFinite(c.delta) || c.delta === 0) continue;
    if (c.bottom - c.delta - earlier <= viewTop + 0.5) shift += c.delta;
    earlier += c.delta;
  }
  return shift;
}

// ───── /clear ─────

/**
 * /clear 的分界：items（依畫面順序的項目）裡最後一則清除提示（Notice、noticeKind clear）的索引，沒有回 -1。
 * 畫面藏起分界之前的項目、不再自動載入較早的訊息（宿主不刪項目，按「顯示清除前的對話」照舊全部顯示）
 */
export function clearBoundary(items) {
  if (!Array.isArray(items)) return -1;
  for (let i = items.length - 1; i >= 0; i--) {
    const it = items[i];
    if (it && isClearNotice(it)) return i;
  }
  return -1;
}

function isClearNotice(it) {
  return it.kind === 'Notice' && typeof it.noticeKind === 'string' && it.noticeKind.toLowerCase() === 'clear';
}

// ───── 訊息時間與對話上方常駐的「最近的指令」 ─────

const pad2 = (n) => (n < 10 ? '0' : '') + n;
const isTime = (ms) => typeof ms === 'number' && Number.isFinite(ms) && ms > 0;

/** 本機日期的鍵（20261022）：跨過午夜時重畫訊息時間 */
export function dayKey(ms) {
  const d = new Date(ms);
  return d.getFullYear() * 10000 + (d.getMonth() + 1) * 100 + d.getDate();
}

/**
 * 訊息後方的時間（Item.time，本機時間的 Unix 毫秒）：今天的「14:05」、昨天的「昨天 14:05」、今年的「10/21 14:05」、
 * 更早的「2025/10/21 14:05」；沒有時間回空字串。now 由呼叫端傳入
 */
export function itemTimeText(ms, now) {
  if (!isTime(ms)) return '';
  const d = new Date(ms);
  const hm = pad2(d.getHours()) + ':' + pad2(d.getMinutes());
  const day = dayKey(ms);
  if (day === dayKey(now)) return hm;
  const n = new Date(now);
  if (day === dayKey(new Date(n.getFullYear(), n.getMonth(), n.getDate() - 1).getTime())) return '昨天 ' + hm;
  const md = (d.getMonth() + 1) + '/' + pad2(d.getDate()) + ' ' + hm;
  return d.getFullYear() === n.getFullYear() ? md : d.getFullYear() + '/' + md;
}

/** 滑過訊息時間時的完整時間「2026/01/02 14:05:33」；沒有時間回空字串 */
export function itemTimeTitle(ms) {
  if (!isTime(ms)) return '';
  const d = new Date(ms);
  return d.getFullYear() + '/' + pad2(d.getMonth() + 1) + '/' + pad2(d.getDate()) + ' ' + pad2(d.getHours()) + ':' + pad2(d.getMinutes()) + ':' + pad2(d.getSeconds());
}

const ENTITIES = { lt: '<', gt: '>', amp: '&', quot: '"', apos: "'" };

/**
 * 使用者訊息的 html（AgentMarkdownRenderer.PlainToHtml：HTML 編碼、換行成 &lt;br&gt;）→ 原本的純文字（呼叫端以 textContent 顯示）。
 * 不經 innerHTML：其他標籤一律去掉，認得的實體（具名的 lt、gt、amp、quot、apos 與數字）才解碼
 */
export function userHtmlText(html) {
  if (typeof html !== 'string' || !html) return '';
  return html.replace(/<br\s*\/?>/gi, '\n').replace(/<[^>]*>/g, '').replace(/&(#x[0-9a-f]+|#\d+|[a-z]+);/gi, (m, e) => {
    if (e[0] !== '#') return Object.prototype.hasOwnProperty.call(ENTITIES, e.toLowerCase()) ? ENTITIES[e.toLowerCase()] : m;
    const cp = e[1] === 'x' || e[1] === 'X' ? parseInt(e.slice(2), 16) : parseInt(e.slice(1), 10);
    return cp > 0 && cp <= 0x10ffff && !(cp >= 0xd800 && cp <= 0xdfff) ? String.fromCodePoint(cp) : m;
  });
}

/**
 * 對話上方常駐的「最近的指令」。items 是已載入的項目（畫面順序）：由新到舊找使用者訊息，先遇到清除提示＝清除之後還沒送過，沒有。
 * 已載入的都沒有時：標頭有 lastPrompt 這個鍵（桌面，web-protocol 的 Header.lastPrompt：長的一輪做完、那則不在已載入的那一頁）就照它，
 * null＝沒有；沒有這個鍵（瀏覽器版的標頭不附）而且還有較早的訊息回 { older: true }（顯示「在較早的訊息裡」，按了往前載入）。
 * 回傳 { id, time, text, attachments } 或 null；text 是純文字
 */
export function latestPrompt(items, header, hasMore) {
  const list = Array.isArray(items) ? items : [];
  for (let i = list.length - 1; i >= 0; i--) {
    const it = list[i];
    if (!it) continue;
    if (isClearNotice(it)) return null;
    if (it.kind === 'User') {
      return { id: it.id, time: it.time, text: userHtmlText(it.html), attachments: Array.isArray(it.attachments) ? it.attachments.length : 0 };
    }
  }
  if (header && typeof header === 'object' && Object.prototype.hasOwnProperty.call(header, 'lastPrompt')) {
    const p = header.lastPrompt;
    if (!p || typeof p !== 'object' || typeof p.id !== 'string') return null;
    return {
      id: p.id, time: p.time, text: typeof p.text === 'string' ? p.text : '',
      attachments: typeof p.attachments === 'number' && p.attachments > 0 ? p.attachments : 0,
    };
  }
  return hasMore ? { older: true } : null;
}

/** 「最近的指令」那一行展開的歷史指令最多幾則（同宿主 AgentHubRules.PromptHistoryLimit） */
export const PROMPT_HISTORY_LIMIT = 10;
/** 歷史指令每則最多幾個字：更長的不列（同宿主 AgentHubRules.PromptHistoryMaxChars） */
export const PROMPT_HISTORY_MAX_CHARS = 100000;
const VOICE_TEXT = /^語音訊息(（\d+(:\d{2}){1,2}）)?$/;

/**
 * 「最近的指令」那一行展開的歷史指令（web-protocol 的 promptHistory；2026-10-25）：reply 是宿主回的 promptHistory（桌面）就照它；
 * 沒有（瀏覽器版不問）時從已載入的項目由新到舊找使用者訊息，規則同宿主的 AgentHubRules.RecentPrompts：/clear 之前的也列，
 * 文字空白（只有附件）、太長、語音訊息與內容和較新的一則相同的略過。回 [{ id, time, text, attachments }]，最新的在前、最多 PROMPT_HISTORY_LIMIT 則
 */
export function promptHistory(reply, items) {
  const out = [];
  const seen = new Set();
  const add = (id, time, text, attachments) => {
    if (out.length >= PROMPT_HISTORY_LIMIT || typeof id !== 'string' || typeof text !== 'string' || !text.trim() || text.length > PROMPT_HISTORY_MAX_CHARS) return;
    const files = typeof attachments === 'number' && attachments > 0 ? attachments : 0;
    if (files > 0 && VOICE_TEXT.test(text)) return;
    if (seen.has(text)) return;
    seen.add(text);
    out.push({ id, time, text, attachments: files });
  };
  if (reply && typeof reply === 'object' && Array.isArray(reply.prompts)) {
    for (const p of reply.prompts) if (p && typeof p === 'object') add(p.id, p.time, p.text, p.attachments);
    return out;
  }
  const list = Array.isArray(items) ? items : [];
  for (let i = list.length - 1; i >= 0 && out.length < PROMPT_HISTORY_LIMIT; i--) {
    const it = list[i];
    if (it && it.kind === 'User') add(it.id, it.time, userHtmlText(it.html), Array.isArray(it.attachments) ? it.attachments.length : 0);
  }
  return out;
}

/** 歷史指令在清單裡的那一行：換行與連續空白成一個空格；太長的截斷加「…」（完整的在滑過的說明與帶進輸入框的文字） */
export function promptLine(text, max = 160) {
  const line = typeof text === 'string' ? text.replace(/\s+/g, ' ').trim() : '';
  return line.length > max ? line.slice(0, max) + '…' : line;
}

// ───── 〔桌面遠端操作〕在其他電腦開新對話（桌面的遠端草稿，sync-protocol 14.7） ─────

/**
 * 桌面草稿可以改開在哪些其他電腦（draftSetDevice）：不是這台、在線、沒撤銷、政策是 Control，而且至少有一個帳戶在那台登入可用
 * （AccountDeviceView 的 status 是 ready、identityOk 不是 false、online 不是 false）。每台附上可用的帳戶：id 是邏輯帳戶（指令用）、
 * shownId 是網頁 accounts 裡的 id（這台有連結時是本機 id、只在其他電腦的是邏輯 id）。依 remote.devices 的順序、帳戶依 accounts 的順序
 */
export function peerDraftDevices({ accounts, devices, ownDeviceId } = {}) {
  const list = Array.isArray(devices) ? devices : [];
  const accs = Array.isArray(accounts) ? accounts : [];
  const out = [];
  for (const d of list) {
    if (!isObj(d) || !str(d.id) || d.id === ownDeviceId || d.revoked === true || d.online !== true || d.remoteControlLevel !== 'Control') continue;
    const usable = [];
    for (const a of accs) {
      if (!isObj(a) || !str(a.provider)) continue;
      const logical = str(a.logicalId) || (a.remoteOnly === true ? str(a.id) : null);
      if (!logical || !Array.isArray(a.devices)) continue;
      const cell = a.devices.find((c) => isObj(c) && c.deviceId === d.id);
      if (!cell || cell.status !== 'ready' || cell.identityOk === false || cell.online === false) continue;
      usable.push({ id: logical, shownId: str(a.id) || logical, label: str(a.label) || a.provider, provider: a.provider, color: str(a.color) });
    }
    if (usable.length) out.push({ deviceId: d.id, name: str(d.name) || UNKNOWN_DEVICE, accounts: usable });
  }
  return out;
}

/** 換到那台時用哪個帳戶：目前標頭的帳戶（本機 id 或邏輯 id）在那台可用就沿用，否則那台的第一個；沒有回 null */
export function peerDraftAccount(target, headerAccountId) {
  if (!isObj(target) || !Array.isArray(target.accounts) || !target.accounts.length) return null;
  return target.accounts.find((a) => a.shownId === headerAccountId || a.id === headerAccountId) || target.accounts[0];
}

// ───── 〔遠端〕〔桌面遠端操作〕其他電腦的對話換帳戶（2026-10-25 使用者要求：桌面也能換，但只能換到那台電腦已登入的帳戶） ─────

/** 標頭目前用的帳戶：桌面的副本是 replica.accountId（擁有者那台的帳戶連結到的邏輯帳戶，不明是 null），其他是 header.accountId */
export function headerAccountId(header, origin) {
  if (!isObj(header)) return null;
  if (origin === 'replica') return isObj(header.replica) ? str(header.replica.accountId) : null;
  return str(header.accountId);
}

/** 網頁 accounts 裡的這個帳戶：先比 id、再比 logicalId（副本帶的是邏輯帳戶，這台有連結時 accounts 裡的 id 是本機的）；沒有回 null */
export function findAccount(accounts, id) {
  const key = str(id);
  if (!key || !Array.isArray(accounts)) return null;
  return accounts.find((a) => isObj(a) && a.id === key) || accounts.find((a) => isObj(a) && a.logicalId === key) || null;
}

/**
 * 在 deviceId 那台已登入可用的帳戶（AccountDeviceView 的 status 是 ready、identityOk 不是 false），依 accounts 的順序、同一個邏輯帳戶只列一次。
 * 每一項是新的物件：id、logicalId 是邏輯帳戶（setAccount 帶它，伺服器換成那台的本機帳戶；瀏覽器版的 accounts 本來就是邏輯帳戶）、
 * shownId 是 accounts 裡的 id；label、color、provider 照帳戶，plan、statusText 照那台的格子；status 一律 ready、remoteOnly 一律 false
 * （accountrules.runningAccountMenu 照這兩個欄位挑）
 */
export function deviceAccounts(accounts, deviceId) {
  const id = str(deviceId);
  if (!id || !Array.isArray(accounts)) return [];
  const out = [];
  const seen = new Set();
  for (const a of accounts) {
    if (!isObj(a) || !str(a.provider) || !Array.isArray(a.devices)) continue;
    const logical = str(a.logicalId) || (a.remoteOnly === true ? str(a.id) : null);
    if (!logical || seen.has(logical)) continue;
    const cell = a.devices.find((c) => isObj(c) && c.deviceId === id);
    if (!cell || cell.status !== 'ready' || cell.identityOk === false) continue;
    seen.add(logical);
    out.push({
      id: logical, logicalId: logical, shownId: str(a.id) || logical, provider: a.provider, label: str(a.label) || a.provider, color: str(a.color),
      plan: str(cell.plan) || str(a.plan), statusText: str(cell.statusText), status: 'ready', remoteOnly: false,
    });
  }
  return out;
}

/**
 * 其他電腦的對話的帳戶選單（瀏覽器版已開始的對話、桌面上的副本）：只列那台電腦已登入可用的帳戶（deviceAccounts）。
 * 回 { remote, enabled, reason, accounts, current, empty, scope }——remote 是 false：這台的對話與草稿（照本機的選單，其他欄位沒有意義）；
 * enabled 是 false：帳戶晶片停用（桌面的副本不能從這台操作：同步沒連上、伺服器或這台不支援），reason 是晶片的說明；
 * enabled 是 true 而 reason 不是 null：點了只提示 reason（沒連線、電腦離線、政策不是 Control）。
 * current 是目前用的帳戶（headerAccountId）；empty 是那台沒有其他帳戶可以換時的說明（有其他的是 null）；scope 是「在 OFFICE-PC 登入的帳戶」（晶片的說明用）
 */
export function remoteAccountMenu({ mode, origin, header, device, live, peer, accounts } = {}) {
  if (origin !== 'replica' && !(origin === 'remote' && isHubMode(mode))) {
    return { remote: false, enabled: true, reason: null, accounts: [], current: null, empty: null, scope: null };
  }
  const scope = '在' + devLabel(device, true, true) + '登入的帳戶';
  const base = { remote: true, enabled: true, reason: null, accounts: [], current: headerAccountId(header, origin), empty: null, scope };
  if (origin === 'replica') {
    if (peer === 'down') return { ...base, enabled: false, reason: PEER_DOWN_TEXT };
    if (peer !== 'live') return { ...base, enabled: false, reason: '其他電腦的對話不能更換帳戶' };
    live = true; // 桌面的連線由 peer 判斷
  }
  const blocked = remoteBlock(device, live, '更換帳戶');
  if (blocked) return { ...base, reason: blocked };
  const list = deviceAccounts(accounts, (device && str(device.id)) || (isObj(header) ? str(header.deviceId) : null));
  const empty = list.some((a) => a.id !== base.current) ? null : list.length ? '沒有其他' + scope : '沒有' + scope;
  return { ...base, accounts: list, empty };
}

// ───── 標頭瘦身（docs/web-protocol.md 的 header，2026-10-23）─────

/** 宿主沒變就省略的標頭欄位（宿主 HeaderListRules.Fields）：「/」指令清單、模型清單與自訂代理清單 */
export const HEADER_LIST_FIELDS = Object.freeze(['commands', 'models', 'agents', 'outputStyles']);

/**
 * 標頭更新沒帶指令或模型清單＝和上一份相同：沿用 prev 的（排著還沒套用的那一份，否則畫面上的）。帶了的照新的；
 * 沒有上一份可沿用時補空陣列（不應發生：宿主對沒送過整份的 view 一律送整份）。回傳新的物件，不改 prev、next
 */
/**
 * 收到省略了清單的標頭，但上一份（prev）也沒有那份清單：網頁和宿主對不上了（宿主以為這個視窗有清單才省略）。
 * 合併出來的清單會是空的——模型鈕不見、推理強度顯示「不支援」（2026-11-07 使用者回報 Claude 常偵測不到模型與推理強度）。
 * 回傳缺的欄位（沒有缺回空陣列）；呼叫端重送 open 拿整份快照
 */
export function missingHeaderLists(prev, next) {
  if (!isObj(next)) return [];
  // 只看整份標頭一定有的兩份（桌面與伺服器都一定帶）；agents、outputStyles 只有 Claude 才有，沒有不代表對不上
  return HEADER_LISTS_ALWAYS.filter((f) => !Object.prototype.hasOwnProperty.call(next, f) && !(isObj(prev) && Array.isArray(prev[f])));
}

/** 整份標頭一定有的清單欄位 */
export const HEADER_LISTS_ALWAYS = Object.freeze(['commands', 'models']);

/** 標頭清單對不上時，同一個對話多久之內最多重送一次 open */
export const HEADER_LISTS_REOPEN_MS = 10000;

export function mergeHeaderLists(prev, next) {
  if (!isObj(next)) return next;
  const out = Object.assign({}, next);
  for (const f of HEADER_LIST_FIELDS) {
    if (Object.prototype.hasOwnProperty.call(out, f)) continue;
    out[f] = isObj(prev) && Array.isArray(prev[f]) ? prev[f] : [];
  }
  return out;
}

/**
 * 同一批（同一個影格）裡排著的兩份標頭合併成一份（app.js 的 header）：next 沒帶的清單沿用 prev 帶了的，prev 也沒帶就照樣不帶。
 * 不能用 mergeHeaderLists：它在沒有上一份時補空陣列，兩份省略清單的標頭排在同一批，合併出來就「帶了空的模型與指令清單」，
 * 套用時蓋掉畫面上的清單——模型鈕不見、推理強度顯示「不支援」，自救（missingHeaderLists 只看欄位有沒有帶）也不會觸發
 * （2026-11-08 使用者回報：執行中的 Claude 對話標頭更新頻繁，常常這樣）。回傳新的物件，不改 prev、next
 */
export function mergeQueuedHeaders(prev, next) {
  if (!isObj(next) || !isObj(prev)) return next;
  const out = Object.assign({}, next);
  for (const f of HEADER_LIST_FIELDS) {
    if (!Object.prototype.hasOwnProperty.call(out, f) && Array.isArray(prev[f])) out[f] = prev[f];
  }
  return out;
}

// ───── 即時更新的增量（itemPatch，patch1；docs/plans/2026-10-23-live-delta-and-replica-live.md 3.5）─────

/** itemPatch 的長欄位（伺服器 ItemPatchRules.Fields）：其餘欄位整份在 set 裡 */
export const PATCH_FIELDS = Object.freeze(['html', 'detailHtml', 'detail', 'output']);
/** 增量對不上時，同一個對話多久之內最多重送一次 open */
export const PATCH_REOPEN_MS = 10000;

/**
 * 以基底（畫面上或排著還沒套用的那一筆 Item）與它的即時版本 baseRv 套用一則 itemPatch，回傳新的 Item；對不上回 null（呼叫端丟掉那則、重送 open）。
 * 新的 Item＝set 的欄位＋長欄位：有 op 的是「基底的前 at 個碼元＋s」（長度要等於 n），沒有的沿用基底。位置是 UTF-16 碼元（同伺服器）
 */
export function applyItemPatch(base, baseRv, patch) {
  if (!isObj(base) || !isObj(patch) || !isObj(patch.set)) return null;
  if (typeof baseRv !== 'number' || patch.baseRv !== baseRv || typeof patch.rv !== 'number' || !(patch.rv > baseRv)) return null;
  if (!str(patch.id) || base.id !== patch.id || patch.set.id !== patch.id) return null;
  const next = Object.assign({}, patch.set);
  for (const f of PATCH_FIELDS) if (Object.prototype.hasOwnProperty.call(next, f)) return null;
  const ops = Array.isArray(patch.ops) ? patch.ops : null;
  if (!ops || ops.length > PATCH_FIELDS.length) return null;
  const touched = new Set();
  for (const op of ops) {
    if (!isObj(op) || !PATCH_FIELDS.includes(op.f) || touched.has(op.f) || typeof op.s !== 'string') return null;
    touched.add(op.f);
    const old = typeof base[op.f] === 'string' ? base[op.f] : '';
    if (!Number.isInteger(op.at) || op.at < 0 || op.at > old.length) return null;
    const value = old.slice(0, op.at) + op.s;
    if (value.length !== op.n) return null;
    next[op.f] = value;
  }
  for (const f of PATCH_FIELDS) if (!touched.has(f) && typeof base[f] === 'string') next[f] = base[f];
  return next;
}

/** 增量對不上之後要不要重送 open：同一個對話距離上一次（lastAt，沒有是 undefined）至少 PATCH_REOPEN_MS */
export function patchReopenDue(lastAt, now) {
  return typeof lastAt !== 'number' || now - lastAt >= PATCH_REOPEN_MS;
}

// ───── 第十批：延伸與委派的關聯（ConversationSummary 的 parentId、forkedFrom，只有桌面的清單有） ─────

/**
 * 側欄的子對話：帳戶委派建立的子對話（parentId）掛在主對話底下，主對話也在這份清單裡才掛（不然照原本的位置）。
 * rows 是篩選、排序好的清單（新到舊）；一家子排在其中最上面那一列的位置（子對話還在跑、比主對話新時，整家跟著往上），
 * 主對話在前、子對話照原本的順序，分組標題照那一列的分組。flat（搜尋中）時不掛、照原本的順序。
 * 回 [{ c, child, last, g }]：child＝掛在上一個主對話底下、last＝那一家的最後一個子對話、g＝這一列要放在哪個分組
 */
export function nestFamilies(rows, flat) {
  const list = Array.isArray(rows) ? rows.filter((c) => c && typeof c.id === 'string') : [];
  if (flat) return list.map((c) => ({ c, child: false, last: false, g: c.group }));
  const byId = new Map(list.map((c) => [c.id, c]));
  const kids = new Map();
  for (const c of list) {
    const p = typeof c.parentId === 'string' ? byId.get(c.parentId) : null;
    if (!p || p === c || typeof p.parentId === 'string') continue; // 子對話不再往下委派：只掛一層
    if (!kids.has(p.id)) kids.set(p.id, []);
    kids.get(p.id).push(c);
  }
  const out = [];
  const done = new Set();
  for (const c of list) {
    if (done.has(c.id)) continue;
    const parent = typeof c.parentId === 'string' && kids.has(c.parentId) && kids.get(c.parentId).includes(c) ? byId.get(c.parentId) : null;
    const head = parent || c;
    const g = c.group;
    done.add(head.id);
    out.push({ c: head, child: false, last: false, g });
    const ks = (kids.get(head.id) || []).filter((k) => !done.has(k.id));
    ks.forEach((k, i) => { done.add(k.id); out.push({ c: k, child: true, last: i === ks.length - 1, g }); });
  }
  return out;
}

/**
 * 標頭的來源晶片：帳戶委派的子對話寫「由「主對話」委派」、延伸出來的寫「延伸自「原對話」」（標題太長截掉）。
 * byId 是清單的 Map（state.convMap）；來源還在清單裡時 open 為 true（點了開啟）。都不是回 null
 */
export function originLink(entry, byId) {
  if (!entry || typeof entry !== 'object') return null;
  const get = (id) => (byId && typeof byId.get === 'function' ? byId.get(id) || null : null);
  const cut = (t) => { const s = String(t || '').trim(); return s.length > 18 ? s.slice(0, 18) + '…' : s; };
  if (typeof entry.parentId === 'string' && entry.parentId) {
    const p = get(entry.parentId);
    return { kind: 'delegated', id: entry.parentId, label: '由「' + cut(p ? p.title : '主對話') + '」委派',
      title: p ? '帳戶委派的子對話：主對話是「' + p.title + '」（點一下開啟）' : '帳戶委派的子對話（主對話已不在清單裡）', open: !!p };
  }
  if (typeof entry.forkedFrom === 'string' && entry.forkedFrom) {
    const s = get(entry.forkedFrom);
    const t = (s && s.title) || entry.forkedFromTitle || '原對話';
    return { kind: 'fork', id: entry.forkedFrom, label: '延伸自「' + cut(t) + '」',
      title: '從「' + t + '」延伸出來的對話' + (s ? '（點一下開啟原對話）' : '（原對話已不在清單裡）'), open: !!s };
  }
  return null;
}
