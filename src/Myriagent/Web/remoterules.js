// 同步畫面的純規則（WP3.5）：Remote 的更新、帳戶 × 電腦矩陣、裝置小點、電腦頁、各電腦 CLI、標題列的同步小點、
// 瀏覽器的連線狀態列、擁有者操作的錯誤文字。協定見 docs/web-protocol.md（第一節 devices／sync／connection、第三節的資料形狀、第六節）。
// 沒有 import、不碰 DOM、不讀時鐘：現在時間一律由呼叫端傳入（tools/ui-probe/rules.test.mjs 以 data: URL 直接匯入測試）。
// 時間只用來顯示（到期提示、倒數），不用來排序或判定先後（CLAUDE.md「同步不變式」：排序只看伺服器）。

const MINUTE = 60000;
const DAY = 24 * 60 * MINUTE;
/** Claude 登入到期前多久提示「登入即將到期」 */
export const LOGIN_EXPIRING_MS = 3 * DAY;
/** Claude 存取權杖到期前多久（含已過期）提示「權杖待更新」 */
export const TOKEN_STALE_MS = 10 * MINUTE;
/** 裝置名稱清理後的長度上限（UTF-16 單位，同 PairingRules.MaxDeviceNameLength） */
export const DEVICE_NAME_MAX = 60;

const STATUS_TEXT = { checking: '檢查中', ready: '已登入', notLoggedIn: '尚未登入', apiKey: 'API Key 登入', notInstalled: '未安裝 CLI', error: '發生錯誤' };
const STATUS_TONE = { checking: 'info', error: 'bad', apiKey: 'warn' };
const POLICY_TEXT = { Off: '不接受', View: '只能查看', Control: '可以操作' };
const REVOKE_REASON = { retired: '退役', compromised: '疑似遭入侵' };

const isObj = (v) => !!v && typeof v === 'object' && !Array.isArray(v);
const str = (v) => (typeof v === 'string' && v ? v : null);
const num = (v) => (typeof v === 'number' && Number.isFinite(v) ? v : null);
const count = (v) => Math.max(0, Math.trunc(num(v) || 0));

// ───── Remote（web-protocol 第一節 devices、sync 的規則；remote 是 undefined 當成 null）─────

function normalizeRemote(r) {
  if (!isObj(r)) return null;
  const sync = isObj(r.sync) ? r.sync : null;
  return Object.assign({}, r, {
    mode: r.mode === 'browser' ? 'browser' : 'desktop',
    // 配對完成後 init.remote.deviceId 不會跟著更新：這台的 deviceId 以 sync.deviceId 為準
    deviceId: (sync && str(sync.deviceId)) || str(r.deviceId),
    devices: Array.isArray(r.devices) ? r.devices : [],
    sync,
  });
}

/** init 的 remote：沒有（未配對、舊宿主）或不是物件時是 null */
export function remoteFromInit(init) {
  return normalizeRemote(isObj(init) ? init.remote : null);
}

/**
 * 收到 sync（只有桌面）：sync 是 null → null（解除配對、取消配對）；remote 是 null 而 sync 不是 → 自己建 desktop 的 remote；
 * 其他整份取代 remote.sync。瀏覽器的 remote 不受 sync 影響（伺服器不送 sync）
 */
export function applySync(remote, sync) {
  if (isObj(remote) && remote.mode === 'browser') return remote;
  if (!isObj(sync)) return null;
  if (!isObj(remote)) return { mode: 'desktop', deviceId: str(sync.deviceId), devices: [], sync };
  return Object.assign({}, remote, { deviceId: str(sync.deviceId) || str(remote.deviceId), sync });
}

/** 收到 devices：整份取代 remote.devices；remote 是 null 時不變（未配對的桌面不顯示裝置） */
export function applyDevices(remote, list) {
  if (!isObj(remote)) return null;
  return Object.assign({}, remote, { devices: Array.isArray(list) ? list : [] });
}

// ───── 短暫斷線：先顯示「重新連線中」─────

/** 電腦離線後這麼久之內顯示「重新連線中」而不是「離線」（伺服器部署換版時每台電腦都會斷線，幾秒到幾十秒內連回來） */
export const RECONNECT_GRACE_MS = MINUTE;

/**
 * 這份 remote 的寬限期：init 的 remote.reconnectGraceMs（伺服器只在 Development、Testing 環境給，端到端縮短等待；1000～60000 的整數）；
 * 沒有或不合格是 RECONNECT_GRACE_MS（只能縮短、不能加長）
 */
export function reconnectGraceOf(remote) {
  const v = isObj(remote) ? remote.reconnectGraceMs : null;
  return Number.isInteger(v) && v >= 1000 && v <= RECONNECT_GRACE_MS ? v : RECONNECT_GRACE_MS;
}

/** 離線起算的時間：這個頁面看到它從線上變離線的時間（offlineSeenAt，本機時鐘）與伺服器的 lastSeenAt 取較晚的；都沒有是 null */
function offlineSince(d) {
  const seen = num(d.offlineSeenAt);
  const last = num(d.lastSeenAt);
  if (seen === null) return last;
  return last === null ? seen : Math.max(seen, last);
}

/**
 * 這台離線的電腦是不是剛斷線（離線起算 graceMs 內；預設 RECONNECT_GRACE_MS）。lastSeenAt 是伺服器的時間，和本機差一點時可能在未來：
 * 差不到 graceMs 照樣算，差更多（時鐘不準）一律當成離線。已撤銷、線上、不知道（online 不是 false）都不是
 */
export function isReconnecting(device, nowMs, graceMs = RECONNECT_GRACE_MS) {
  if (!isObj(device) || device.online !== false || device.revoked) return false;
  const since = offlineSince(device);
  if (since === null || num(nowMs) === null) return false;
  const age = nowMs - since;
  return age > -graceMs && age < graceMs;
}

/**
 * 收到新的裝置清單（devices、init，以及計時器到期時以同一份重算）時標記剛斷線的電腦：上一份是線上、這一份離線的，
 * offlineSeenAt 記成 nowMs；上一份已經離線的沿用它的 offlineSeenAt。離線的電腦一律帶 offlineSeenAt（沒有是 null）與 reconnecting（isReconnecting），
 * 其他照原樣。這兩個欄位只在網頁裡（不是 DeviceView 的欄位）；只影響顯示的文字，閘門照樣擋（那台確實沒連著伺服器）
 */
export function trackDeviceLinks(prev, next, nowMs, graceMs = RECONNECT_GRACE_MS) {
  const before = new Map();
  for (const d of Array.isArray(prev) ? prev : []) {
    if (isObj(d) && str(d.id) && !before.has(d.id)) before.set(d.id, d);
  }
  return (Array.isArray(next) ? next : []).map((d) => {
    if (!isObj(d) || d.online !== false) return d;
    const p = str(d.id) ? before.get(d.id) : undefined;
    let seen = null;
    if (p && p.online === true) seen = num(nowMs);
    else if (p && p.online === false) seen = num(p.offlineSeenAt);
    const out = Object.assign({}, d, { offlineSeenAt: seen });
    out.reconnecting = isReconnecting(out, nowMs, graceMs);
    return out;
  });
}

/** 最早一台「重新連線中」的電腦還要多久變成「離線」（毫秒，到時以 trackDeviceLinks(同一份, 同一份, 現在) 重算再重畫）；沒有是 null */
export function nextLinkChange(devices, nowMs, graceMs = RECONNECT_GRACE_MS) {
  let wait = null;
  for (const d of Array.isArray(devices) ? devices : []) {
    if (!isReconnecting(d, nowMs, graceMs)) continue;
    const left = Math.max(0, offlineSince(d) + graceMs - nowMs);
    if (wait === null || left < wait) wait = left;
  }
  return wait;
}

// ───── 帳戶 × 電腦矩陣（設計 6.8）─────

function lookupDevice(devicesById, id) {
  if (!devicesById || !id) return null;
  if (devicesById instanceof Map) return devicesById.get(id) || null;
  if (Array.isArray(devicesById)) return devicesById.find((d) => isObj(d) && d.id === id) || null;
  if (typeof devicesById === 'object' && Object.prototype.hasOwnProperty.call(devicesById, id)) return devicesById[id] || null;
  return null;
}

function cellsById(account) {
  const map = new Map();
  for (const c of account.devices) {
    if (isObj(c) && str(c.deviceId) && !map.has(c.deviceId)) map.set(c.deviceId, c);
  }
  return map;
}

/**
 * 矩陣的模型：欄＝remote.devices 裡未撤銷的電腦（照原本的順序）；列＝devices 是陣列的帳戶（沒連結的帳戶沒有列）；
 * cells 與 columns 一一對應，那台沒有這個帳戶的格子是 null
 */
export function matrixModel(accounts, remote, nowMs) {
  const r = isObj(remote) ? remote : null;
  const columns = r && Array.isArray(r.devices) ? r.devices.filter((d) => isObj(d) && str(d.id) && !d.revoked) : [];
  const selfId = r ? str(r.deviceId) : null;
  const rows = [];
  for (const a of Array.isArray(accounts) ? accounts : []) {
    if (!isObj(a) || !Array.isArray(a.devices)) continue;
    const byId = cellsById(a);
    const cells = columns.map((d) => {
      const c = byId.get(d.id);
      if (!c) return null;
      const self = c.self === true || d.self === true || (!!selfId && d.id === selfId);
      return cellView(c, d, nowMs, { remoteOnly: a.remoteOnly === true, self, provider: a.provider });
    });
    rows.push({ account: a, cells });
  }
  return { columns, rows };
}

/**
 * 一格的呈現（AccountDeviceView）。優先順序（取第一個符合的）：
 * absent（一定最先：伺服器給 absent 格的 identityOk 固定是 true）＞身分不符＞未安裝 CLI＞checking／error／apiKey＞notLoggedIn＞ready（到期提示）；
 * 疊加：離線的格子調暗並附那台最後上線的時間（那台剛斷線時 reconnecting 是 true，trackDeviceLinks）、負責查用量的格子標「查用量」。
 * 回傳 { kind, text, hint, tone, dim, reconnecting, lastSeenAt, poller, expiring, tokenStale, action }；
 * tone：ok｜warn｜bad｜info｜muted｜none；expiring：null｜'soon'｜'expired'；action：null｜'loginHere'（這台的 remoteOnly 帳戶）
 */
export function cellView(cell, device, nowMs, opts) {
  const o = isObj(opts) ? opts : {};
  const c = isObj(cell) ? cell : {};
  const d = isObj(device) ? device : {};
  const self = o.self === true;
  const now = num(nowMs);
  const where = self ? '這台' : (str(d.name) || str(c.deviceName) || '那台電腦');
  const v = { kind: '', text: '', hint: null, tone: 'muted', dim: false, reconnecting: false, lastSeenAt: null, poller: c.usagePoller === true, expiring: null, tokenStale: false, action: null };
  const status = c.status;

  if (status === 'absent') {
    v.kind = 'absent';
    v.text = '尚未加入';
    v.tone = 'none';
    if (self && o.remoteOnly === true) {
      v.action = 'loginHere';
      v.hint = '這台還沒有這個帳戶';
    }
  } else if (c.identityOk === false) {
    v.kind = 'identity';
    v.text = '身分不符';
    v.hint = str(c.email) ? `${self ? '這台' : '那台'}登入的是 ${c.email}` : null;
    v.tone = 'bad';
  } else if (status === 'notInstalled') {
    v.kind = 'notInstalled';
    v.text = STATUS_TEXT.notInstalled;
  } else if (status === 'checking' || status === 'error' || status === 'apiKey') {
    v.kind = status;
    v.text = str(c.statusText) || STATUS_TEXT[status];
    v.tone = STATUS_TONE[status];
  } else if (status === 'notLoggedIn') {
    v.kind = 'notLoggedIn';
    v.text = str(c.statusText) || STATUS_TEXT.notLoggedIn;
    const at = self ? '到這台' : `到 ${where} `;
    v.hint = o.provider === 'Codex' || o.provider === 'Grok' ? `${at}登入，或用裝置碼從網頁登入` : `${at}完成登入`;
  } else if (status === 'ready') {
    v.kind = 'ready';
    const loginLeft = now !== null && num(c.loginExpiresAt) !== null ? c.loginExpiresAt - now : null;
    const accessLeft = now !== null && num(c.accessExpiresAt) !== null ? c.accessExpiresAt - now : null;
    v.expiring = loginLeft === null ? null : loginLeft <= 0 ? 'expired' : loginLeft <= LOGIN_EXPIRING_MS ? 'soon' : null;
    v.tokenStale = accessLeft !== null && accessLeft <= TOKEN_STALE_MS;
    v.text = v.expiring === 'expired' ? '登入已到期' : (str(c.statusText) || STATUS_TEXT.ready);
    const notes = [str(c.email)];
    if (v.expiring === 'soon') notes.push('登入即將到期');
    // 權杖待更新不給動作：CLI 下次用的時候自己更新，app 絕不替它 refresh
    if (v.tokenStale && v.expiring !== 'expired') notes.push('權杖待更新');
    v.hint = notes.filter(Boolean).join('・') || null;
    v.tone = v.expiring === 'expired' ? 'bad' : (v.expiring === 'soon' || v.tokenStale ? 'warn' : 'ok');
  } else {
    // 不認得的狀態一律當錯誤（伺服器的 SafeStatus 也是）
    v.kind = 'error';
    v.text = str(c.statusText) || STATUS_TEXT.error;
    v.tone = 'bad';
  }

  const online = typeof c.online === 'boolean' ? c.online : d.online !== false;
  if (!online) {
    v.dim = true;
    v.reconnecting = d.reconnecting === true;
    v.lastSeenAt = num(d.lastSeenAt);
  }
  return v;
}

/** 裝置小點的色調：身分不符、錯誤、登入已到期＝bad（離線也是：那是要處理的設定問題）＞離線＝off＞ready（即將到期、權杖待更新＝warn）＞其他＝muted */
function dotTone(v) {
  if (v.kind === 'identity' || v.kind === 'error' || v.expiring === 'expired') return 'bad';
  if (v.dim) return 'off';
  if (v.kind === 'ready') return v.expiring === 'soon' || v.tokenStale ? 'warn' : 'ok';
  return 'muted';
}

/**
 * 帳戶卡與側欄的裝置小點：每台有這個帳戶的電腦一點（absent 與已撤銷的不畫），照帳戶 devices 的順序。
 * devicesById 可以是 Map、{id: DeviceView} 或 DeviceView 陣列。回傳 [{deviceId, tone, title}]
 */
export function deviceDots(account, devicesById, nowMs) {
  if (!isObj(account) || !Array.isArray(account.devices)) return [];
  const out = [];
  for (const [id, c] of cellsById(account)) {
    if (c.status === 'absent') continue;
    const d = lookupDevice(devicesById, id);
    if (d && d.revoked) continue;
    const v = cellView(c, d, nowMs, { remoteOnly: account.remoteOnly === true, self: c.self === true || (!!d && d.self === true), provider: account.provider });
    const name = (d && str(d.name)) || str(c.deviceName) || id;
    const title = `${name}：${v.text}` + (v.hint ? `（${v.hint}）` : '') + (v.dim ? (v.reconnecting ? '・重新連線中' : '・離線') : '');
    out.push({ deviceId: id, tone: dotTone(v), title });
  }
  return out;
}

// ───── 電腦頁（DeviceView）─────

/**
 * 電腦頁的一台：顯示名稱與配對時的名稱不同才另標；金鑰位置、其他電腦的遠端操作政策（只供顯示）、撤銷原因；
 * verified === false（桌面：這台採用的名冊裡沒有這一筆）時給警告；剛斷線（trackDeviceLinks 標的 reconnecting）照樣帶出。不含任何伺服器身分指紋
 */
export function deviceView(device) {
  if (!isObj(device)) return null;
  const signed = str(device.signedName);
  const name = str(device.name) || signed || '未命名的電腦';
  const level = Object.prototype.hasOwnProperty.call(POLICY_TEXT, device.remoteControlLevel) ? device.remoteControlLevel : 'Off';
  let revoked = null;
  if (isObj(device.revoked)) {
    const reason = Object.prototype.hasOwnProperty.call(REVOKE_REASON, device.revoked.reason) ? device.revoked.reason : null;
    revoked = { at: num(device.revoked.at), reason, text: reason ? `已撤銷・${REVOKE_REASON[reason]}` : '已撤銷' };
  }
  return {
    id: str(device.id),
    name,
    signedNameNote: signed && signed !== name ? `配對時名稱：${signed}` : null,
    machineName: str(device.machineName),
    appVersion: str(device.appVersion),
    fingerprint: str(device.fingerprint),
    keyStorageText: device.keyStorage === 'tpm' ? 'TPM' : '軟體',
    online: device.online === true && !revoked,
    reconnecting: device.reconnecting === true && !revoked,
    lastSeenAt: num(device.lastSeenAt),
    policyText: `其他電腦的遠端操作：${POLICY_TEXT[level]}` + (level !== 'Off' && device.allowRemoteFullAccess === true ? '（含完全自動）' : ''),
    self: device.self === true,
    verifiedWarning: device.verified === false ? '這台採用的裝置名冊裡沒有這一筆，請到設定的「多電腦同步」檢查' : null,
    revoked,
    canRename: !revoked,
    // 只有已撤銷的才能從清單移除（設計 14.22；瀏覽器版才有按鈕）
    canRemove: !!revoked,
  };
}

/** 移除確認框的對話數：清單裡擁有者是這台的對話（瀏覽器版的 ConversationSummary.deviceId）；只是預告，實際刪了幾個以伺服器的回應為準 */
export function countDeviceConversations(conversations, deviceId) {
  if (!Array.isArray(conversations) || !str(deviceId)) return 0;
  return conversations.filter((c) => isObj(c) && c.deviceId === deviceId).length;
}

/**
 * 電腦頁的清單：remote.devices 每台的 deviceView，未撤銷的照原本的順序在前、已撤銷的在後（伺服器也這樣排，這裡再保證一次）；
 * 桌面的 remote.deviceId 也算「這台」
 */
export function deviceList(remote) {
  if (!isObj(remote) || !Array.isArray(remote.devices)) return [];
  const selfId = str(remote.deviceId);
  const views = remote.devices.map(deviceView).filter((v) => v && v.id).map((v) => (selfId && v.id === selfId ? Object.assign(v, { self: true }) : v));
  return views.filter((v) => !v.revoked).concat(views.filter((v) => v.revoked));
}

// ───── 各電腦 CLI（DeviceCliView）─────

/**
 * 一台電腦上一種 CLI：安裝損壞＞未安裝＞有新版＞已安裝；舊版執行中、待換版的對話數放在 notes。
 * 那台沒有回報這種 CLI（view 不是物件）時 kind 是 'unknown'
 */
export function deviceCliView(view) {
  if (!isObj(view)) return { kind: 'unknown', text: '沒有回報', tone: 'muted', notes: [], version: null, latest: null, installKind: null, checkedAt: null };
  const version = str(view.version);
  const latest = str(view.latest);
  const notes = [];
  const old = count(view.runningOnOldVersion);
  const pending = count(view.restartPending);
  if (old) notes.push(`${old} 個對話仍在用舊版執行`);
  if (pending) notes.push(`${pending} 個對話待換版`);
  let kind;
  let text;
  let tone;
  if (str(view.installBroken)) {
    kind = 'broken';
    text = view.installBroken;
    tone = 'bad';
  } else if (view.installed !== true) {
    kind = 'notInstalled';
    text = '未安裝';
    tone = 'muted';
    notes.length = 0;
  } else if (view.updateAvailable === true) {
    kind = 'update';
    text = latest ? `${version || '目前版本'} → ${latest}（有新版）` : `${version || '目前版本'}（有新版）`;
    tone = 'warn';
  } else {
    kind = 'ok';
    text = version || '已安裝';
    tone = 'ok';
  }
  return { kind, text, tone, notes, version, latest, installKind: str(view.installKind), checkedAt: num(view.checkedAt) };
}

// ───── 標題列的同步小點（桌面，SyncStatus）─────

/**
 * 同步小點：sync 是 null → null（不顯示）。confirm 不是 null 時一律錯誤色「要在這台確認伺服器身分」；
 * clockSkewMs 有值時提示同步 Windows 時間。回傳 { state, text, tone, title }
 */
export function syncDot(sync) {
  if (!isObj(sync)) return null;
  const state = ['pairing', 'connecting', 'online', 'offline', 'paused', 'stopped'].includes(sync.state) ? sync.state : 'stopped';
  const detail = str(sync.detail);
  const pending = count(sync.pending);
  let text;
  let tone;
  switch (state) {
    case 'pairing': text = '同步：配對中'; tone = 'info'; break;
    case 'connecting': text = '同步：連線中'; tone = 'info'; break;
    case 'online': text = '同步：已連線'; tone = 'ok'; break;
    case 'offline': text = pending ? `同步：離線（${pending} 筆待送）` : '同步：離線'; tone = 'warn'; break;
    case 'paused': text = '同步：已暫停'; tone = 'muted'; break;
    default: text = detail ? `同步：已停止（${detail}）` : '同步：已停止'; tone = 'bad'; break;
  }
  if (isObj(sync.confirm)) {
    text = '同步：要在這台確認伺服器身分';
    tone = 'bad';
  }
  const lines = [text];
  if (detail && state !== 'stopped') lines.push(detail);
  const pairingError = isObj(sync.pairing) ? str(sync.pairing.error) : null;
  if (pairingError) lines.push(pairingError);
  const skew = num(sync.clockSkewMs);
  if (skew !== null) {
    lines.push(`這台和伺服器的時間差了約 ${Math.max(1, Math.round(Math.abs(skew) / MINUTE))} 分鐘，請同步 Windows 時間`);
    if (tone === 'ok' || tone === 'info' || tone === 'muted') tone = 'warn';
  }
  return { state, text, tone, title: lines.join('\n') };
}

// ───── 瀏覽器的連線狀態列（connection 訊息）─────

/**
 * 狀態列：null＝隱藏（已連線而且這條連線收到過 init）。conn 是 null（還沒開始連）→「連線中…」；
 * online 但還沒 init →「載入中…」；斷線依 reason 給文字與倒數（retryAt 減現在、無條件進位到秒）。
 * 回傳 { kind, text, tone, retry, retryAt }；retry＝顯示「立即重試」
 */
export function connectionBanner(conn, hubInit, nowMs) {
  if (!isObj(conn)) return { kind: 'connecting', text: '連線中…', tone: 'info', retry: false, retryAt: null };
  if (conn.state === 'online') {
    return hubInit === true ? null : { kind: 'loading', text: '載入中…', tone: 'info', retry: false, retryAt: null };
  }
  const retryAt = num(conn.retryAt);
  const now = num(nowMs);
  const secs = retryAt !== null && now !== null ? Math.max(0, Math.ceil((retryAt - now) / 1000)) : null;
  const after = secs ? `（${secs} 秒後重試）` : '';
  if (conn.reason === 'tooManyConnections') {
    return { kind: 'tooMany', text: `這個瀏覽器開了太多分頁（每個登入最多 8 個），關掉一些後會自動重新連線${after}`, tone: 'warn', retry: true, retryAt };
  }
  if (conn.state === 'offline') {
    if (conn.reason === 'offline') return { kind: 'offline', text: '離線：網路中斷', tone: 'warn', retry: false, retryAt };
    return { kind: 'server', text: `連不上伺服器${after}`, tone: 'bad', retry: true, retryAt };
  }
  return { kind: 'reconnecting', text: `重新連線中…${after}`, tone: 'warn', retry: true, retryAt };
}

// ───── 擁有者 HTTP 操作的錯誤文字（web-protocol 第六節第 12 點）─────

/**
 * action：'revoke'（撤銷電腦）｜'remove'（移除已撤銷的電腦）｜'delete'（從目錄刪除帳戶）｜'stepup'（再輸入一次密碼）；status 0＝網路錯誤；
 * code 是回應的 error；retryAfterSec 是 429 的 Retry-After（秒）
 */
export function ownerErrorText(action, status, code, retryAfterSec) {
  const s = num(status) || 0;
  if (s === 0) return '連不上伺服器，請確認網路後再試';
  if (s === 429) {
    const n = num(retryAfterSec);
    return n !== null && n > 0 ? `嘗試太頻繁，${Math.ceil(n)} 秒後再試` : '嘗試太頻繁，請稍後再試';
  }
  if (s === 401) return '登入已失效，請重新登入';
  if (s === 403) {
    if (code === 'password_failed') return '密碼錯誤';
    if (code === 'step_up_required') return '這個操作要先再輸入一次密碼';
    if (code === 'forbidden_origin') return '請求來源不符，請重新整理頁面';
    return '沒有權限執行這個操作';
  }
  if (action === 'revoke') {
    if (s === 400) return '撤銷原因不正確';
    if (s === 404) return '找不到這台電腦（可能已經刪除）';
    if (s === 409) return '這台電腦已經撤銷了';
    if (s === 503) return '伺服器暫時無法處理，可能已經撤銷；請等電腦清單更新後再決定要不要重試';
  } else if (action === 'remove') {
    if (s === 404) return '找不到這台電腦（可能已經移除）';
    if (s === 409) return code === 'not_revoked' ? '要先撤銷這台電腦才能移除' : '這台電腦已經移除了';
    if (s === 503) return '伺服器暫時無法處理，可能已經移除；請等電腦清單更新後再決定要不要重試';
  } else if (action === 'delete') {
    if (s === 404) return '找不到這個帳戶（可能已經從目錄刪除）';
    if (s === 503) return '伺服器暫時無法處理，請稍後再試';
  }
  if (s >= 500) return `伺服器發生錯誤（${s}），請稍後再試`;
  return `伺服器回應 ${s}`;
}

// ───── 其他 ─────

/** 照伺服器的 LabelRules.Clean：去掉控制、格式、行與段落分隔字元與孤立的代理字元，再去頭尾空白；1～60 字才合格，不合格回 null */
export function cleanDeviceName(name) {
  if (typeof name !== 'string') return null;
  const cleaned = name.replace(/[\p{Cc}\p{Cf}\p{Zl}\p{Zp}\p{Cs}]/gu, '').trim();
  return cleaned.length >= 1 && cleaned.length <= DEVICE_NAME_MAX ? cleaned : null;
}

/** 改名對話框：清理後 1～60 字才送 */
export const validDeviceName = (name) => cleanDeviceName(name) !== null;

/** 本機帳戶數（不算 remoteOnly：那是目錄裡有、這台沒有的帳戶） */
export function countLocalAccounts(accounts) {
  return Array.isArray(accounts) ? accounts.filter((a) => isObj(a) && a.remoteOnly !== true).length : 0;
}

/**
 * remoteOnly 帳戶在其他電腦有沒有本機帳戶：devices 裡有不是這台（self 不是 true）、狀態不是 absent 的格子才回 true。
 * 從這台移除帳戶之後，目錄裡的邏輯帳戶還在（設計 6.7「從這台移除」），只在這台用過的會以 remoteOnly 回來、devices 只剩 absent：
 * 這時不能說「在其他電腦登入過」
 */
export function remoteOnlyElsewhere(a) {
  return isObj(a) && Array.isArray(a.devices) && a.devices.some((d) => isObj(d) && d.self !== true && d.status !== 'absent');
}

const sessionCount = (s, key) => (isObj(s) && Number.isFinite(s[key]) && s[key] > 0 ? Math.floor(s[key]) : 0);

/**
 * 側欄帳戶卡與儀表板帳戶大卡的執行中、等你回應、等背景工作回報：這台的 sessions 加上〔同步〕其他在線電腦的 otherSessions（桌面已連結到帳戶目錄的帳戶才有；
 * 瀏覽器版的 sessions 本來就是各電腦的合計）。background＝代理這一輪已回覆完畢、背景工作還在跑的對話（舊版的宿主與伺服器沒有這個欄位，當成 0）；
 * programs＝這一輪結束了、但代理開的程式脫離 CLI 還在跑的對話（背景程式，2026-10-24；同樣沒有就當成 0）；open＝開著的對話（儀表板大卡的「開著」）。
 * 回傳合計與其中其他電腦的數字（滑過的說明分開寫）
 */
export function sessionTotals(a) {
  const here = isObj(a) ? a.sessions : null;
  const other = isObj(a) ? a.otherSessions : null;
  const otherOpen = sessionCount(other, 'open');
  const otherRunning = sessionCount(other, 'running');
  const otherWaiting = sessionCount(other, 'waiting');
  const otherBackground = sessionCount(other, 'background');
  const otherPrograms = sessionCount(other, 'programs');
  const otherSubagents = sessionCount(other, 'subagents');
  return {
    running: sessionCount(here, 'running') + otherRunning,
    waiting: sessionCount(here, 'waiting') + otherWaiting,
    background: sessionCount(here, 'background') + otherBackground,
    programs: sessionCount(here, 'programs') + otherPrograms,
    subagents: sessionCount(here, 'subagents') + otherSubagents,
    open: sessionCount(here, 'open') + otherOpen,
    otherOpen,
    otherRunning,
    otherWaiting,
    otherBackground,
    otherPrograms,
    otherSubagents,
  };
}

/** 執行中的子代理的提示（個數，不是對話數：「3 個子代理執行中（其中其他電腦 1 個）」）；0 個是空字串 */
export function subagentsTitle(total, other) {
  if (!total) return '';
  return total + ' 個子代理執行中' + (other ? (other === total ? '（都在其他電腦）' : '（其中其他電腦 ' + other + ' 個）') : '');
}

/**
 * 帳戶卡子代理徽章滑過的說明（第十一批）：第一行同 subagentsTitle，下面列這台哪幾個對話各有幾個（AccountView.subagentList，最多 5 個），
 * 其他電腦的只有個數
 */
export function subagentsTooltip(a) {
  const s = sessionTotals(a);
  if (!s.subagents) return '';
  const lines = [subagentsTitle(s.subagents, s.otherSubagents)];
  const local = isObj(a) && Array.isArray(a.subagentList) ? a.subagentList.filter(isObj) : [];
  for (const c of local) {
    const n = Number.isInteger(c.count) && c.count > 0 ? c.count : 0;
    if (n) lines.push('・' + (str(c.title) || '（沒有標題）') + '：' + n + ' 個');
  }
  if (s.otherSubagents) lines.push('・其他電腦 ' + s.otherSubagents + ' 個');
  return lines.join('\n');
}

/** 執行中、等你回應的提示（「3 個對話執行中（其中其他電腦 2 個）」）；0 個是空字串 */
export function sessionTitle(total, other, what) {
  if (!total) return '';
  return total + ' 個對話' + what + (other ? (other === total ? '（都在其他電腦）' : '（其中其他電腦 ' + other + ' 個）') : '');
}

/**
 * 背景程式標示的提示：幾個對話、這台的每個對話列標題與程式（宿主的 AccountView.programs：{title, programs, more}，最多列 5 個對話），
 * 其他電腦只寫個數（瀏覽器版只有個數）。沒有背景程式回空字串
 */
export function programsTitle(a) {
  const s = sessionTotals(a);
  if (!s.programs) return '';
  const lines = [s.programs + ' 個對話的背景程式還在跑（這一輪已經結束，代理開的程式還在執行，跑完不會通知代理）'];
  const local = isObj(a) && Array.isArray(a.programs) ? a.programs.filter(isObj) : [];
  for (const c of local.slice(0, 5)) {
    const programs = Array.isArray(c.programs) ? c.programs.filter((p) => typeof p === 'string' && p) : [];
    const more = Number.isFinite(c.more) && c.more > 0 ? '…等 ' + (programs.length + c.more) + ' 個' : '';
    lines.push('・' + (str(c.title) || '（沒有標題）') + (programs.length ? '：' + programs.join('、') + more : ''));
  }
  if (local.length > 5) lines.push('・還有 ' + (local.length - 5) + ' 個對話');
  if (s.otherPrograms) lines.push('・其他電腦 ' + s.otherPrograms + ' 個對話');
  return lines.join('\n');
}

// ───── 儀表板的區塊（WP3.5 步驟 9）─────

/** 「帳戶 × 電腦」區要不要顯示：remote 不是 null，而且是瀏覽器、或至少一個帳戶的 devices 是陣列（桌面已連結到目錄的帳戶） */
export function matrixVisible(accounts, remote) {
  if (!isObj(remote)) return false;
  if (remote.mode === 'browser') return true;
  return Array.isArray(accounts) && accounts.some((a) => isObj(a) && Array.isArray(a.devices));
}

/**
 * 各電腦的 CLI：devicesCli 是 { deviceId: { Claude?, Codex? } }。電腦照 remote.devices 的順序（已撤銷的不列），
 * remote 裡沒有的電腦接在後面（照鍵的順序）；內容不是物件的電腦不列。種類照 providers 的順序、只列至少一台回報過的。
 * 回傳 [{ deviceId, name, self, online（true／false／null＝不知道）, reconnecting（剛斷線，trackDeviceLinks）, lastSeenAt, clis: [{ provider, view: deviceCliView }] }]
 */
export function deviceCliModel(devicesCli, remote, providers) {
  if (!isObj(devicesCli)) return [];
  const r = isObj(remote) ? remote : null;
  const devices = r && Array.isArray(r.devices) ? r.devices.filter((d) => isObj(d) && str(d.id)) : [];
  const selfId = r ? str(r.deviceId) : null;
  const revoked = new Set(devices.filter((d) => d.revoked).map((d) => d.id));
  const ids = devices.filter((d) => !d.revoked).map((d) => d.id).filter((id) => isObj(devicesCli[id]));
  for (const id of Object.keys(devicesCli)) {
    if (!ids.includes(id) && !revoked.has(id) && isObj(devicesCli[id])) ids.push(id);
  }
  const kinds = (Array.isArray(providers) ? providers : []).filter((p) => ids.some((id) => Object.prototype.hasOwnProperty.call(devicesCli[id], p)));
  return ids.map((id) => {
    const d = devices.find((x) => x.id === id) || null;
    return {
      deviceId: id,
      name: (d && str(d.name)) || id,
      self: !!d && (d.self === true || id === selfId),
      online: d ? d.online === true : null,
      reconnecting: !!d && d.reconnecting === true,
      lastSeenAt: d ? num(d.lastSeenAt) : null,
      clis: kinds.map((p) => ({ provider: p, view: deviceCliView(devicesCli[id][p]) })),
    };
  });
}

/**
 * 帳戶選單的「上移」「下移」（瀏覽器版沒有側欄可以拖曳）：回傳 accountReorder 的 ids（只含本機帳戶，不含 remoteOnly），
 * 不能移（第一個上移、最後一個下移、找不到、remoteOnly、delta 是 0）回 null。不改傳入的陣列
 */
export function reorderIds(accounts, id, delta) {
  if (!Array.isArray(accounts) || !delta) return null;
  const ids = accounts.filter((a) => isObj(a) && a.remoteOnly !== true && str(a.id)).map((a) => a.id);
  const i = ids.indexOf(id);
  const j = i + Math.sign(delta);
  if (i < 0 || j < 0 || j >= ids.length) return null;
  ids.splice(i, 1);
  ids.splice(j, 0, id);
  return ids;
}

/**
 * accountLogin 訊息的 loginUrl（Claude 的遠端登入，2026-10-26）：只認 https 的 claude.com／claude.ai（同伺服器的 RemoteLoginUrlRules）；
 * 回 { link } 或 null
 */
export function claudeLoginUrlView(loginUrl) {
  const url = str(loginUrl);
  if (!url || url.length > 4096) return null;
  let u;
  try { u = new URL(url); } catch { return null; }
  if (u.protocol !== 'https:' || u.username || u.password || u.port || !['claude.com', 'claude.ai'].includes(u.hostname.toLowerCase())) return null;
  return { link: u.href };
}

/** 貼回來的授權碼：去掉頭尾空白；空的、太長、中間有空白回 null（那台會再驗 state） */
export function loginCodeInput(text) {
  const t = typeof text === 'string' ? text.trim() : '';
  return t && t.length <= 2048 && !/\s/.test(t) ? t : null;
}

/**
 * accountLogin 訊息的 deviceCode（桌面的 Codex 裝置碼登入）：{ userCode, verificationUrl, link（只有 https 才給，可以點）, expiresAt, leftMs, expired }；
 * 沒有驗證碼或不是字串（或長得不像驗證碼）回 null
 */
export function loginCodeView(deviceCode, nowMs) {
  if (!isObj(deviceCode) || typeof deviceCode.userCode !== 'string') return null;
  const userCode = deviceCode.userCode.trim();
  if (!userCode || userCode.length > 64) return null;
  const url = str(deviceCode.verificationUrl);
  const expiresAt = num(deviceCode.expiresAt);
  const now = num(nowMs);
  const leftMs = expiresAt !== null && now !== null ? expiresAt - now : null;
  return { userCode, verificationUrl: url, link: url && /^https:\/\//i.test(url) ? url : null, expiresAt, leftMs, expired: leftMs !== null && leftMs <= 0 };
}
