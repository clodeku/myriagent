// 瀏覽器版的傳輸層（WP3.5；契約 docs/web-protocol.md 第六節第 13 點，與設計的差異見計畫 docs/plans/2026-10-16-wp3.5-web-transport.md 第 3 節）。
// 伺服器在 /app/ 的 index.html 把這支（傳統腳本，不是 module）插在 SignalR 程式庫之後、app.js 之前，掛 window.myriagentHubTransport；
// Web/transport.js 只在 version === 1 而且 ok === true 時改走這裡。
// 最上層只定義函式與掛全域：不碰 document、不掛事件（tools/ui-probe/rules.test.mjs 以沒有 DOM 的 node:vm 載入，證明這一點）；
// 頁面送出第一則 ready 時才開始連線、掛事件。ok：不在 WebView2 裡、而且 SignalR 程式庫已載入（self.signalR.HubConnectionBuilder）。
(function () {
  'use strict';

  // ───── 常數（web-protocol 第六節第 5、11 點；伺服器的 MessageGuard 與 BrowserRegistration）─────

  /** 單則訊息上限（伺服器 MaximumReceiveMessageSize，128 KB）：整個 SignalR 呼叫框＋0x1E 的 UTF-8 位元組 */
  const MAX_FRAME_BYTES = 131072;
  /** 容器巢狀深度上限（根容器是 1） */
  const MAX_DEPTH = 8;
  /** 節點（容器與值，不含屬性名）上限 */
  const MAX_NODES = 4096;
  /**
   * 每個字串值的長度上限（UTF-16 單位，同 C# 的 string.Length）：伺服器讀名稱之前的第一段上限（轉送給電腦的指令的代理文字上限）。
   * 不轉送的指令伺服器另以 4096 檢查，超過時回 invalid（伺服器是唯一的權威）
   */
  const MAX_STRING = 16000;
  /** 每個屬性名的長度上限（UTF-16 單位） */
  const MAX_NAME = 4096;
  /** 估呼叫框大小時 invocationId 以 10 位數計（實際是從 0 起算的流水號，只會更短） */
  const INVOCATION_ID_ESTIMATE = '0000000000';
  /** 退避的各階（毫秒）：0、1、2、5、10、30 秒，之後一直 30 秒 */
  const BACKOFF_STEPS_MS = [0, 1000, 2000, 5000, 10000, 30000];
  /** 退避到這一階（30 秒）起，狀態列改成「連不上伺服器」（offline） */
  const OFFLINE_ATTEMPT = 5;
  /** too_many_connections：固定 30 秒乘 1.0～1.2（web-protocol 第六節第 6 點「30 秒起」） */
  const TOO_MANY_MS = 30000;
  /** 網頁自己會送的指令（不是使用者操作；側欄搜尋的 searchConversations 由網頁自己說明沒有連線）：沒有連線時靜默丟棄、不提示 */
  const AUTO_COMMANDS = ['visible', 'open', 'close', 'loadOlder', 'newConversation', 'listFiles', 'fileSearch', 'searchConversations'];
  /** 版面存在瀏覽器的哪個鍵（傳輸層從這裡注入 init.layout） */
  const LAYOUT_KEY = 'myriagent.app.layout.v1';
  /** 版面最多幾個窗格（同 Web/workspace.js 的 MAX_PANES） */
  const MAX_PANES = 12;
  /** ULID 的字母表（大寫 Crockford Base32） */
  const ULID_ALPHABET = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
  /** 〔遠端〕ClientState 的 openConversationIds 最多幾個（伺服器 MessageGuard.MaxOpenConversationIds） */
  const MAX_VIEWING = 100;
  /** 〔遠端〕線上的對話 id：32 個小寫十六進位（伺服器草稿的 d… 不算） */
  const CONVERSATION_ID = /^[0-9a-f]{32}$/;
  /** 〔遠端〕同源的這些路徑（媒體、縮圖、檔案預覽）開新分頁，不讓整頁離開 /app/（WP4.6 D8） */
  const NEW_TAB_PATHS = ['/m/', '/t/', '/f/'];

  const isObj = (v) => !!v && typeof v === 'object' && !Array.isArray(v);
  const isContainer = (v) => !!v && typeof v === 'object';
  const isStr = (v) => typeof v === 'string';

  // ───── commandId ─────

  /**
   * ULID：前 10 字是 nowMs 的 48 位元（大端、每 5 位元一字，首字自然 ≤ 7），後 16 字是 bytes10（10 個位元組＝80 位元）。
   * bytes10 沒給就用 crypto.getRandomValues
   */
  function ulid(nowMs, bytes10) {
    let time = Math.floor(Number(nowMs));
    if (!Number.isFinite(time) || time < 0) time = 0;
    time = Math.min(time, 281474976710655); // 2^48 − 1
    let head = '';
    for (let i = 0; i < 10; i++) {
      head = ULID_ALPHABET[time % 32] + head;
      time = Math.floor(time / 32);
    }
    const bytes = bytes10 && bytes10.length === 10 ? bytes10 : window.crypto.getRandomValues(new Uint8Array(10));
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

  // ───── 重連 ─────

  /**
   * 下一次嘗試前等多久（毫秒）。attempt 是連續失敗的次數（收到 init 才歸零）：0、1、2、5、10、30、30… 秒，
   * 大於 0 的乘 0.8～1.0（只往下抖，最慢 30 秒）；reason 'tooManyConnections' 固定 30 秒乘 1.0～1.2（30～36 秒）
   */
  function backoffDelayMs(attempt, reason, rand01) {
    let r = typeof rand01 === 'number' && Number.isFinite(rand01) ? rand01 : Math.random();
    r = Math.min(Math.max(r, 0), 0.999999);
    if (reason === 'tooManyConnections') return Math.round(TOO_MANY_MS * (1 + 0.2 * r));
    const n = Math.trunc(Number(attempt));
    const i = Number.isFinite(n) ? Math.min(Math.max(n, 0), BACKOFF_STEPS_MS.length - 1) : 0;
    const base = BACKOFF_STEPS_MS[i];
    return base === 0 ? 0 : Math.round(base * (0.8 + 0.2 * r));
  }

  /** 連線狀態：已連線 → online；瀏覽器說離線、或退避已到 30 秒那一階 → offline；其他 → reconnecting */
  function connectionState(connected, navigatorOnline, attempt) {
    if (connected === true) return 'online';
    if (navigatorOnline === false) return 'offline';
    return Number(attempt) >= OFFLINE_ATTEMPT ? 'offline' : 'reconnecting';
  }

  /**
   * 伺服器的錯誤代碼：訊息裡「HubException: <代碼>」的代碼，沒有回 null。實際字串前面還有別的字，例如
   * 「Server returned an error on close: Connection closed with an error. HubException: too_many_connections」
   * 與「An unexpected error occurred invoking 'ClientState' on the server. HubException: rate_limited」，所以一律以「包含」比對
   */
  function hubErrorCode(errorOrMessage) {
    let text = null;
    if (isStr(errorOrMessage)) text = errorOrMessage;
    else if (errorOrMessage && isStr(errorOrMessage.message)) text = errorOrMessage.message;
    if (!text) return null;
    const m = /HubException: ([A-Za-z0-9_]+)/.exec(text);
    return m ? m[1] : null;
  }

  /** 連線前 GET /api/v1/auth/me 的結果怎麼處理：200 連線、401 回登入頁、429 照 Retry-After、其他（含 0＝網路錯誤）照退避 */
  function authDecision(status) {
    if (status === 200) return 'connect';
    if (status === 401) return 'redirect';
    if (status === 429) return 'retryAfter';
    return 'retry';
  }

  // ───── 送出前自檢（web-protocol 第六節第 11 點）─────

  /** 伺服器實際會看到的值（JSON 往返：undefined 欄位消失、陣列裡的 undefined 變 null）；序列化不了（循環參照）回 {ok:false} */
  function toWire(value) {
    try {
      const text = JSON.stringify(value);
      return { ok: true, value: text === undefined ? null : JSON.parse(text) };
    } catch {
      return { ok: false, value: null };
    }
  }

  /**
   * 照伺服器的 MessageGuard.WithinShape 的第一段：根容器深度 1、子容器＋1、值不加深度；節點含容器與值（不含屬性名）；
   * 字串值 ≤ 16,000、屬性名 ≤ 4096（單則 128 KB 另由 fits 檢查）
   */
  function wireShapeFits(value) {
    let count = 0;
    const stack = [[value, isContainer(value) ? 1 : 0]];
    while (stack.length > 0) {
      const [current, depth] = stack.pop();
      if (++count > MAX_NODES || depth > MAX_DEPTH) return false;
      if (Array.isArray(current)) {
        for (const item of current) stack.push([item, isContainer(item) ? depth + 1 : depth]);
      } else if (isContainer(current)) {
        for (const key of Object.keys(current)) {
          if (key.length > MAX_NAME) return false;
          const item = current[key];
          stack.push([item, isContainer(item) ? depth + 1 : depth]);
        }
      } else if (isStr(current) && current.length > MAX_STRING) {
        return false;
      }
    }
    return true;
  }

  function utf8Length(text) {
    return new TextEncoder().encode(text).length;
  }

  function wireFrameBytes(target, wireArg) {
    const frame = JSON.stringify({ type: 1, invocationId: INVOCATION_ID_ESTIMATE, target: String(target), arguments: [wireArg] });
    return utf8Length(frame) + 1; // 0x1E
  }

  /** 形狀是否在伺服器的上限內（先做 JSON 往返） */
  function shapeFits(value) {
    const w = toWire(value);
    return w.ok && wireShapeFits(w.value);
  }

  /** 送出 invoke(target, arg) 時整個呼叫框（invocationId 以 10 位數估）＋0x1E 的 UTF-8 位元組；序列化不了回 Infinity */
  function frameBytes(target, arg) {
    const w = toWire(arg);
    return w.ok ? wireFrameBytes(target, w.value) : Infinity;
  }

  /** 形狀與呼叫框大小都在上限內才送 */
  function fits(target, arg) {
    const w = toWire(arg);
    return w.ok && wireShapeFits(w.value) && wireFrameBytes(target, w.value) <= MAX_FRAME_BYTES;
  }

  // ───── 指令的分類 ─────

  /** rate_limited 重送時的合併鍵：同一個鍵只留最新的一筆（舊值不會蓋掉新值）；其他指令回 null（各自重送） */
  function mergeKey(cmd) {
    if (!isObj(cmd)) return null;
    switch (cmd.cmd) {
      case 'setting': return isStr(cmd.key) ? 'setting:' + cmd.key : null;
      case 'accountUpdate': return isStr(cmd.accountId) ? 'accountUpdate:' + cmd.accountId : null;
      case 'accountReorder': return 'accountReorder';
      case 'deviceRename': return isStr(cmd.deviceId) ? 'deviceRename:' + cmd.deviceId : null;
      default: return null;
    }
  }

  /** 網頁自己會送的指令（可以給指令物件或名稱）：斷線時靜默丟棄 */
  function isAutoCommand(cmd) {
    const name = isStr(cmd) ? cmd : (isObj(cmd) ? cmd.cmd : null);
    return isStr(name) && AUTO_COMMANDS.indexOf(name) >= 0;
  }

  /** 〔遠端〕畫面上顯示中的對話（最近一次 visible 指令的清單）：只留對話 id 形狀的（草稿被濾掉）、去重、照原順序、最多 100 個（WP4.6 D2） */
  function viewingIds(ids) {
    if (!Array.isArray(ids)) return [];
    const out = [];
    const seen = new Set();
    for (const id of ids) {
      if (!isStr(id) || !CONVERSATION_ID.test(id) || seen.has(id)) continue;
      seen.add(id);
      out.push(id);
      if (out.length >= MAX_VIEWING) break;
    }
    return out;
  }

  /**
   * ClientState 的內容：tzOffset 照 new Date().getTimezoneOffset()（臺灣 −480，不反號）；openConversationIds 是 viewing（最近一次 visible 的清單）
   * 經 viewingIds 過濾的結果，省略時是 []（第 3 階段一律 []）。features 是這個網頁收得了的伺服器訊息：patch1＝即時更新的增量 itemPatch
   * （app.js 與 liverules.js 的 applyItemPatch；舊伺服器忽略這個欄位）
   */
  function clientState(visible, focused, tzOffset, viewing) {
    let tz = Math.round(Number(tzOffset));
    if (!Number.isFinite(tz)) tz = 0;
    tz = Math.min(Math.max(tz, -840), 840);
    return { visible: visible === true, focused: focused === true, openConversationIds: viewingIds(viewing), tzOffsetMinutes: tz, features: ['patch1'] };
  }

  // ───── 版面（存在瀏覽器，不送伺服器）─────

  function validPane(p) {
    if (!isObj(p) || !isStr(p.id)) return false;
    if (!Array.isArray(p.tabs) || !p.tabs.every(isStr)) return false;
    return p.active === undefined || p.active === null || isStr(p.active);
  }

  /** 存下的版面：壞 JSON、窗格超過 12 個、型別不對 → null；合格的原樣回來 */
  function parseLayout(text) {
    if (!isStr(text) || !text) return null;
    let v;
    try {
      v = JSON.parse(text);
    } catch {
      return null;
    }
    if (!isObj(v) || !Array.isArray(v.panes) || v.panes.length > MAX_PANES || !v.panes.every(validPane)) return null;
    if (v.focusedPane !== undefined && v.focusedPane !== null && !isStr(v.focusedPane)) return null;
    if (v.sidebarCollapsed !== undefined && typeof v.sidebarCollapsed !== 'boolean') return null;
    if (v.view !== undefined && v.view !== 'chat' && v.view !== 'dashboard') return null;
    return v;
  }

  // ───── 在瀏覽器裡處理、不送伺服器的指令（設計 4.11）─────

  /**
   * openLink：http／https 才開（同源 sameOrigin:true，其他協定一律 reject；〔遠端〕同源的 /m/、/t/、/f/ 另帶 newTab:true）；
   * popout：彈出視窗的網址（對話 id 編碼）；copyText；layout。其他指令回 null（照常送伺服器）
   */
  function localAction(msg, origin) {
    if (!isObj(msg) || !isStr(msg.cmd)) return null;
    switch (msg.cmd) {
      case 'openLink': {
        if (!isStr(msg.href) || !msg.href) return { kind: 'reject' };
        let url;
        try {
          url = new URL(String(msg.href), isStr(origin) && origin ? origin : undefined);
        } catch {
          return { kind: 'reject' };
        }
        if (url.protocol !== 'http:' && url.protocol !== 'https:') return { kind: 'reject' };
        const sameOrigin = isStr(origin) && url.origin === origin;
        const action = { kind: 'openLink', url: url.href, sameOrigin };
        if (sameOrigin && NEW_TAB_PATHS.some((p) => url.pathname.startsWith(p))) action.newTab = true;
        return action;
      }
      case 'popout':
        if (!isStr(msg.conversationId) || !msg.conversationId) return { kind: 'reject' };
        return { kind: 'popout', url: '/app/?mode=popout&conversation=' + encodeURIComponent(msg.conversationId) };
      case 'copyText':
        return { kind: 'copyText' };
      case 'layout':
        return { kind: 'layout', layout: msg.layout };
      default:
        return null;
    }
  }

  const rules = Object.freeze({
    ulid,
    backoffDelayMs,
    hubErrorCode,
    authDecision,
    connectionState,
    shapeFits,
    frameBytes,
    fits,
    mergeKey,
    isAutoCommand,
    clientState,
    viewingIds,
    parseLayout,
    LAYOUT_KEY,
    localAction,
    MAX_FRAME_BYTES,
  });

  // ───── 連線（D2～D7：每次嘗試前先問 auth/me、只用 WebSockets＋skipNegotiation、不用自動重連與 stateful reconnect）─────

  const HUB_PATH = '/hubs/v1/browser';
  const AUTH_PATH = '/api/v1/auth/me';
  /** online 之後多久沒收到 init 就關掉重連 */
  const INIT_TIMEOUT_MS = 20000;
  /** start() 多久沒完成（WebSocket 連不上、握手沒回）就放棄這一次 */
  const START_TIMEOUT_MS = 30000;
  /** GET /api/v1/auth/me 多久沒回應就當成網路錯誤 */
  const AUTH_TIMEOUT_MS = 15000;
  /** 429 的 Retry-After 最多照幾秒 */
  const MAX_RETRY_AFTER_MS = 15 * 60000;

  const ok = !(window.chrome && window.chrome.webview) && !!(window.signalR && typeof window.signalR.HubConnectionBuilder === 'function');

  const subscribers = [];
  let readyMsg = null;
  let started = false;
  let redirected = false;
  let conn = null;
  /** 連線世代：每次嘗試加一，舊連線的回呼一律比對世代後忽略 */
  let generation = 0;
  let connecting = false;
  let connected = false;
  let hubInit = false;
  /** 連續失敗的次數（收到 init 才歸零） */
  let attempt = 0;
  let stateName = null;
  let reason = null;
  let retryAt = null;
  let lastCloseAt = null;
  let retryTimer = 0;
  let initTimer = 0;

  // ───── 指令層的狀態（D5、D8～D12）─────

  /** 同一則本地提示多久內只出現一次 */
  const TOAST_DEDUPE_MS = 10000;
  /** Cmd 被 rate_limited 之後第幾次重送前等多久：1、2、4 秒，最多 3 次（第 4 次還被限流就提示） */
  const RATE_LIMIT_RETRY_MS = [1000, 2000, 4000];
  /** 可見度與焦點的變化合併多久才送 ClientState */
  const CLIENT_STATE_DEBOUNCE_MS = 200;
  /** ClientState 被 rate_limited 之後多久重送（只重送一次） */
  const CLIENT_STATE_RETRY_MS = 2000;
  const TEXT_OFFLINE = '目前沒有連線，這個變更沒有送出';
  const TEXT_BUSY = '伺服器忙碌，這個變更沒有送出';
  const TEXT_TOO_LARGE = '這個變更太大，沒有送出';

  /** 本地提示：文字 → 上次送出的時間 */
  const toastAt = new Map();
  /** rate_limited 的待重送：合併鍵 → {cmd, commandId, tries, timer} */
  const pendingRetry = new Map();
  /** 每個合併鍵最近一次送出的序號：舊指令的結果晚到時不重送（不會讓舊值蓋掉新值） */
  const latestSeq = new Map();
  let cmdSeq = 0;
  let clientStateTimer = 0;
  let clientStateRetryTimer = 0;
  /** 這條連線上次送出的 ClientState（JSON 文字）；內容沒變不送 */
  let lastClientState = null;
  /** 〔遠端〕最近一次 visible 指令的對話（viewingIds 過濾過）：ClientState 的 openConversationIds，每條新連線也帶（WP4.6 D2） */
  let viewing = [];
  /**
   * accountUpdate 是部分更新（改名只帶 label、改色只帶 color）：合併鍵 → 最近一次送出、還沒有確定結果的那一筆。
   * 同一個帳戶之後的指令要帶上它的欄位，不然改名被限流後馬上改色，改名就靜默遺失
   */
  const unsettledUpdate = new Map();
  /** accountUpdate 之間要合併的欄位（瀏覽器版只送這兩個；safeMode 在瀏覽器整筆回 desktop_only，不能併進別的指令） */
  const ACCOUNT_UPDATE_FIELDS = ['label', 'color'];
  /** 伺服器最近一次給這個分頁的設定（init 與 settings 推送）：setting 被拒或沒送出時畫面改回這個值 */
  let serverSettings = null;

  const clone = (v) => JSON.parse(JSON.stringify(v));

  /** 依註冊順序交給每個訂閱者；各自攔例外並以 reportError 報出（不吞掉、也不影響其他訂閱者） */
  function deliver(msg) {
    for (const fn of subscribers.slice()) {
      try {
        fn(msg);
      } catch (e) {
        if (typeof window.reportError === 'function') window.reportError(e);
        else setTimeout(() => { throw e; });
      }
    }
  }

  function setState(state, why, at) {
    stateName = state;
    reason = state === 'online' ? null : why;
    retryAt = state === 'online' ? null : at;
    deliver({ type: 'connection', state, reason, retryAt });
  }

  function navigatorOnline() {
    return !(window.navigator && window.navigator.onLine === false);
  }

  function parseRetryAfter(value) {
    if (!value) return 0;
    const secs = Number(value);
    let ms = Number.isFinite(secs) ? secs * 1000 : Date.parse(value) - Date.now();
    if (!Number.isFinite(ms) || ms < 0) ms = 0;
    return Math.min(ms, MAX_RETRY_AFTER_MS);
  }

  async function checkAuth() {
    const abort = new AbortController();
    const timer = setTimeout(() => abort.abort(), AUTH_TIMEOUT_MS);
    try {
      const r = await fetch(AUTH_PATH, { credentials: 'same-origin', cache: 'no-store', signal: abort.signal });
      return { status: r.status, retryAfterMs: r.status === 429 ? parseRetryAfter(r.headers.get('Retry-After')) : 0 };
    } catch {
      return { status: 0, retryAfterMs: 0 };
    } finally {
      clearTimeout(timer);
    }
  }

  /**
   * track(socket)：每建一條 WebSocket 就交給它。WebSocket 的開啟握手卡住（伺服器或中間設備收了 TCP、不回 101）時，
   * 程式庫的 stop() 要先等 start() 的內部流程，結束不了 start()（要等瀏覽器自己的握手逾時，Chromium 約 240 秒）：
   * 以 withUrl 的 WebSocket 選項包一層記下它，逾時時直接關。參數原樣轉傳（程式庫只傳 url；protocols 傳 undefined 會被拒），
   * 靜態的 OPEN 等常數由繼承取得
   */
  function build(track) {
    const s = window.signalR;
    const NativeWebSocket = window.WebSocket;
    class TrackedWebSocket extends NativeWebSocket {
      constructor(...args) {
        super(...args);
        track(this);
      }
    }
    return new s.HubConnectionBuilder()
      .withUrl(HUB_PATH, { transport: s.HttpTransportType.WebSockets, skipNegotiation: true, WebSocket: TrackedWebSocket })
      .configureLogging(s.LogLevel.None)
      .withServerTimeout(30000)
      .withKeepAliveInterval(15000)
      .build();
  }

  /** 一次連線嘗試：auth/me → 連線 → online → ready → ClientState → init 看門狗 */
  async function attemptNow() {
    clearTimeout(retryTimer);
    retryTimer = 0;
    if (connecting || connected || redirected) return;
    connecting = true;
    const gen = ++generation;
    const auth = await checkAuth();
    if (gen !== generation) return;
    const decision = rules.authDecision(auth.status);
    if (decision === 'redirect') {
      // 沒登入或工作階段失效：回登入頁（之後不再重試）
      redirected = true;
      connecting = false;
      window.location.replace('/');
      return;
    }
    if (decision !== 'connect') {
      fail(gen, null, decision === 'retryAfter' ? auth.retryAfterMs : 0);
      return;
    }
    let socket = null;
    const c = build((ws) => { socket = ws; });
    conn = c;
    c.on('Msg', (m) => { if (gen === generation) received(gen, m); });
    c.onclose((error) => fail(gen, error, 0));
    const startTimer = setTimeout(() => {
      // start() 30 秒沒完成就放棄這一次：WebSocket 還在 CONNECTING 時直接關掉（start() 才會真的失敗、不留卡住的 socket），
      // 再照失敗處理排下一次（萬一 start() 還是沒結束，狀態也會離開「連線中」）；晚到的 reject、onclose 由 fail() 的世代與狀態擋掉
      if (gen !== generation || connected || !connecting) return;
      try {
        if (socket && socket.readyState === 0) socket.close();
      } catch {
        // 關不掉也照樣放棄這一次
      }
      c.stop();
      fail(gen, null, 0);
    }, START_TIMEOUT_MS);
    try {
      await c.start();
    } catch (e) {
      clearTimeout(startTimer);
      fail(gen, e, 0);
      return;
    } finally {
      clearTimeout(startTimer);
    }
    if (gen !== generation || !connecting) {
      c.stop();
      return;
    }
    connecting = false;
    connected = true;
    hubInit = false;
    lastClientState = null;
    setState('online', null, null);
    if (readyMsg) sendCmd(readyMsg, null, 0);
    sendClientState(false);
    initTimer = setTimeout(() => {
      // 伺服器握手之後才登記連線、等到第一份快照才送 init：20 秒都沒有就換一條
      if (gen === generation && connected && !hubInit) c.stop();
    }, INIT_TIMEOUT_MS);
  }

  /** 這一次嘗試失敗或連線斷了：照退避排下一次，送 connection（reconnecting／offline） */
  function fail(gen, error, minDelayMs) {
    if (gen !== generation || redirected) return;
    if (!connecting && !connected) return; // 同一世代已經處理過（start 失敗與 onclose 都會到這裡）
    connecting = false;
    connected = false;
    hubInit = false;
    conn = null;
    clearTimeout(initTimer);
    clearTimeout(clientStateTimer);
    clearTimeout(clientStateRetryTimer);
    lastCloseAt = Date.now();
    const tooMany = rules.hubErrorCode(error) === 'too_many_connections';
    const delay = Math.max(rules.backoffDelayMs(attempt, tooMany ? 'tooManyConnections' : null), minDelayMs || 0);
    const online = navigatorOnline();
    const state = rules.connectionState(false, online, attempt);
    attempt++;
    const why = tooMany ? 'tooManyConnections' : !online ? 'offline' : state === 'offline' ? 'server' : null;
    setState(state, why, lastCloseAt + delay);
    dropPending();
    retryTimer = setTimeout(attemptNow, delay);
  }

  function received(gen, m) {
    if (isObj(m) && m.type === 'init') {
      hubInit = true;
      attempt = 0;
      clearTimeout(initTimer);
      serverSettings = isObj(m.settings) ? Object.assign({}, m.settings) : null;
      m = withStoredLayout(m);
    } else if (isObj(m) && m.type === 'settings' && isObj(m.settings)) {
      serverSettings = Object.assign(serverSettings || {}, m.settings);
    }
    deliver(m);
  }

  // ───── 本地提示、版面 ─────

  /** 給所有訂閱者一則本地提示（形狀同伺服器的 toast，app.js 照常顯示）；同一則 10 秒內只一次 */
  function localToast(level, text) {
    const now = Date.now();
    const last = toastAt.get(text);
    if (last !== undefined && now - last < TOAST_DEDUPE_MS) return;
    toastAt.set(text, now);
    deliver({ type: 'toast', level, text });
  }

  /** 網頁自己會送的指令（含 ready）：送不出去時不提示 */
  function quiet(cmd) {
    return cmd.cmd === 'ready' || rules.isAutoCommand(cmd);
  }

  /** 彈出視窗（ready 的 mode；還沒送 ready 時看網址）：不存、不注入版面 */
  function isPopout() {
    if (readyMsg) return readyMsg.mode === 'popout';
    try {
      return new URLSearchParams(window.location.search).get('mode') === 'popout';
    } catch {
      return false;
    }
  }

  /** 讀寫都包 try/catch：私密瀏覽、儲存空間滿了或被擋時版面不存，不影響其他功能 */
  function loadLayout() {
    try {
      return rules.parseLayout(window.localStorage.getItem(LAYOUT_KEY));
    } catch {
      return null;
    }
  }

  function saveLayout(layout) {
    if (isPopout()) return;
    try {
      const text = JSON.stringify(layout);
      if (rules.parseLayout(text) === null) return;
      window.localStorage.setItem(LAYOUT_KEY, text);
    } catch {
      // 同上：存不了就算了
    }
  }

  /** init 的 layout 是 null（伺服器不保存版面）而且不是彈出視窗：換成存在這個瀏覽器的（合格才用） */
  function withStoredLayout(init) {
    if ((init.layout !== null && init.layout !== undefined) || init.mode === 'popout' || isPopout()) return init;
    const layout = loadLayout();
    return layout ? Object.assign({}, init, { layout }) : init;
  }

  /**
   * openLink、popout、copyText、layout：在瀏覽器裡處理，不送伺服器（D10、D9）。〔遠端〕同源的媒體與檔案（newTab）開新分頁；
   * copyText 交給網頁一則 copyFallback（網頁開可以選取的文字框：Clipboard API 已在網頁試過、寫不進去才會送 copyText；WP4.6 D6）
   */
  function runLocal(action, msg) {
    switch (action.kind) {
      case 'openLink':
        if (action.sameOrigin && !action.newTab) window.location.assign(action.url);
        else window.open(action.url, '_blank', 'noopener,noreferrer');
        return;
      case 'popout':
        window.open(action.url, '_blank', 'noopener');
        return;
      case 'copyText':
        if (isStr(msg.text)) deliver({ type: 'copyFallback', text: msg.text });
        else console.warn('[transport-hub] 不處理這則 copyText（text 不是字串）');
        return;
      case 'layout':
        saveLayout(action.layout);
        return;
      default:
        console.warn('[transport-hub] 不處理這則 ' + msg.cmd + (msg.cmd === 'openLink' ? '（只開 http、https 的連結）' : '（缺少對話 id）'));
    }
  }

  // ───── 送出指令（D8、D12）─────

  function sendable() {
    return !!conn && connected && hubInit;
  }

  /**
   * 〔遠端〕傳輸層合成的 commandResult（WP4.6 D4；web-protocol 第六節第 13 點）：這一筆伺服器沒有處理、傳輸層也不會再送
   * （Cmd 最終被拒、限流重送放棄、限流後沒有重送就被丟掉），交給網頁一則 rejected，code 是 Cmd 的 error、rate_limited 或 not_sent。
   * 不另外提示（被拒時伺服器已另送 toast 或 sendRejected）。Cmd 呼叫因斷線被取消時不合成（伺服器可能已經執行，新的 init 時網頁當成 unknown）
   */
  function synthResult(commandId, code) {
    if (!isStr(commandId)) return;
    deliver({ type: 'commandResult', commandId, state: 'rejected', code: isStr(code) ? code : null, message: null, tier: null, origin: 'transport' });
  }

  /** 送一則 Cmd（cmd 是 JSON 往返過的副本）；commandId 給了就沿用（rate_limited 的重送），tries 是已重送幾次 */
  function sendCmd(cmd, commandId, tries) {
    const c = conn;
    if (!c) return;
    const arg = { commandId: commandId || rules.ulid(Date.now()), cmd };
    if (!rules.fits('Cmd', arg)) {
      // 超過上限的框會讓伺服器關掉整條連線（web-protocol 第六節第 11 點）：不送
      console.warn('[transport-hub] 指令超過伺服器的上限（128 KB、深度 8、4096 個節點、字串 16,000 字、屬性名 4096 字），沒有送出：' + cmd.cmd);
      if (!quiet(cmd)) localToast('error', TEXT_TOO_LARGE);
      revertSetting(cmd);
      return;
    }
    const key = rules.mergeKey(cmd) || 'cmd:' + arg.commandId;
    const seq = ++cmdSeq;
    latestSeq.set(key, seq);
    if (key.startsWith('accountUpdate:')) unsettledUpdate.set(key, cmd);
    c.invoke('Cmd', arg).then((r) => onCmdResult(c, key, seq, arg, tries, r), () => {
      // 連線斷了，呼叫被取消：不重送（沒有離線佇列；伺服器可能已經執行，也不提示）
      if (latestSeq.get(key) === seq) {
        latestSeq.delete(key);
        unsettledUpdate.delete(key);
      }
    });
  }

  function onCmdResult(c, key, seq, arg, tries, r) {
    const latest = latestSeq.get(key) === seq;
    if (latest) latestSeq.delete(key);
    if (!r || r.accepted !== false) {
      if (latest) unsettledUpdate.delete(key);
      return;
    }
    if (r.error !== 'rate_limited') {
      // 被拒是這一筆的結果（不重送）：之後的指令不再帶它的欄位；setting 的畫面改回伺服器的值（同一個鍵有更新的在路上時不改）
      if (latest) {
        unsettledUpdate.delete(key);
        revertSetting(arg.cmd);
      }
      // desktop_only、not_available、conflict、unavailable：伺服器已另送提示，這裡不重複
      if (r.error === 'invalid') console.warn('[transport-hub] 伺服器說指令格式不正確：' + arg.cmd.cmd);
      synthResult(arg.commandId, r.error);
      return;
    }
    // 同一個合併鍵之後又送過新的（新的已經帶上這一筆的欄位、會自己處理），或連線已經換了：不重送（伺服器沒處理被限流的這一筆）
    if (!latest || c !== conn) {
      synthResult(arg.commandId, 'not_sent');
      return;
    }
    if (tries >= RATE_LIMIT_RETRY_MS.length) {
      unsettledUpdate.delete(key);
      revertSetting(arg.cmd);
      if (quiet(arg.cmd)) console.warn('[transport-hub] 伺服器忙碌，指令沒有送出：' + arg.cmd.cmd);
      else localToast('error', TEXT_BUSY);
      synthResult(arg.commandId, 'rate_limited');
      return;
    }
    const entry = { cmd: arg.cmd, commandId: arg.commandId, tries: tries + 1, timer: 0 };
    entry.timer = setTimeout(() => {
      if (pendingRetry.get(key) !== entry) return;
      pendingRetry.delete(key);
      // ready 本來就在 init 之前送：只要原本那條連線還連著就重送（被限流的 ready 伺服器沒有處理，重送不會排兩次 init）；
      // 其他的照 D5 要「連著而且收到過 init」
      const canSend = entry.cmd.cmd === 'ready' ? c === conn && connected : sendable();
      if (!canSend) {
        unsettledUpdate.delete(key);
        revertSetting(entry.cmd);
        if (!quiet(entry.cmd)) localToast('error', TEXT_OFFLINE);
        synthResult(entry.commandId, 'not_sent');
        return;
      }
      sendCmd(entry.cmd, entry.commandId, entry.tries);
    }, RATE_LIMIT_RETRY_MS[tries]);
    pendingRetry.set(key, entry);
  }

  /** accountUpdate：同一個帳戶還沒確定送達的欄位（等待重送的、路上的）併進新指令 cmd（新值優先；重送同一個值是冪等的） */
  function mergeUnsettledUpdate(key, cmd) {
    if (!key.startsWith('accountUpdate:')) return;
    const pending = pendingRetry.get(key);
    const prev = (pending && pending.cmd) || unsettledUpdate.get(key);
    if (!prev) return;
    for (const f of ACCOUNT_UPDATE_FIELDS) {
      if (!Object.prototype.hasOwnProperty.call(cmd, f) && Object.prototype.hasOwnProperty.call(prev, f)) cmd[f] = prev[f];
    }
  }

  /**
   * 網頁先改畫面再送 setting（settings.js 的 set）：這一筆被伺服器拒絕或沒有送出、同一個鍵也沒有更新的指令在路上或等待重送時，
   * 以伺服器的值送一則只有這個鍵的 settings 給訂閱者（app.js 照常合併、套用主題、重畫設定頁），畫面才和伺服器一致
   */
  function revertSetting(cmd) {
    if (!isObj(cmd) || cmd.cmd !== 'setting' || !isStr(cmd.key) || !serverSettings) return;
    if (!Object.prototype.hasOwnProperty.call(serverSettings, cmd.key)) return;
    const key = 'setting:' + cmd.key;
    if (latestSeq.has(key) || pendingRetry.has(key)) return;
    const settings = {};
    settings[cmd.key] = serverSettings[cmd.key];
    deliver({ type: 'settings', settings });
  }

  /** 同一個合併鍵有新的指令：等待中的舊值不再重送（它的欄位已併進新的一筆；舊的 commandId 是 not_sent） */
  function cancelRetry(key) {
    const entry = pendingRetry.get(key);
    if (!entry) return;
    clearTimeout(entry.timer);
    pendingRetry.delete(key);
    synthResult(entry.commandId, 'not_sent');
  }

  /** 連線斷了：等待中的重送一律放棄（沒有離線佇列；每一筆都是 not_sent）；其中有使用者操作的指令時提示一次 */
  function dropPending() {
    let user = false;
    const dropped = [];
    for (const entry of pendingRetry.values()) {
      clearTimeout(entry.timer);
      if (!quiet(entry.cmd)) user = true;
      dropped.push(entry);
    }
    pendingRetry.clear();
    latestSeq.clear();
    unsettledUpdate.clear();
    if (user) localToast('error', TEXT_OFFLINE);
    for (const entry of dropped) synthResult(entry.commandId, 'not_sent');
  }

  // ───── ClientState（D11）─────

  function currentClientState() {
    const visible = !(window.document && window.document.visibilityState === 'hidden');
    const focused = !!(window.document && typeof window.document.hasFocus === 'function' && window.document.hasFocus());
    return rules.clientState(visible, focused, new Date().getTimezoneOffset(), viewing);
  }

  /** 可見度、焦點或〔遠端〕畫面上的對話變了：200 ms 內的變化合併成一次 */
  function scheduleClientState() {
    if (!connected) return;
    clearTimeout(clientStateTimer);
    clientStateTimer = setTimeout(() => sendClientState(false), CLIENT_STATE_DEBOUNCE_MS);
  }

  /** 送目前的 ClientState（和這條連線上次送的一樣就不送）；retried：這是 rate_limited 之後的那一次重送 */
  function sendClientState(retried) {
    clearTimeout(clientStateTimer);
    clientStateTimer = 0;
    const c = conn;
    if (!c || !connected) return;
    const state = currentClientState();
    const text = JSON.stringify(state);
    if (text === lastClientState) return;
    lastClientState = text;
    c.invoke('ClientState', state).catch((e) => {
      if (c !== conn) return; // 連線已經換了：新連線會自己送
      const code = rules.hubErrorCode(e);
      if (code === 'invalid') {
        console.warn('[transport-hub] 伺服器說 ClientState 格式不正確');
      } else if (code === 'rate_limited') {
        // 伺服器沒收下：之後一定要再送；2 秒後重送一次（重送也被限流就等下一次變化）
        if (lastClientState === text) lastClientState = null;
        if (!retried) {
          clearTimeout(clientStateRetryTimer);
          clientStateRetryTimer = setTimeout(() => { if (c === conn) sendClientState(true); }, CLIENT_STATE_RETRY_MS);
        }
      }
    });
  }

  function onVisibility() {
    scheduleClientState();
    if (window.document.visibilityState !== 'hidden') retryNow();
  }

  function onOffline() {
    // 已經斷線的話把狀態列換成「離線：網路中斷」；連著的等連線自己斷
    if (!connected && !connecting && stateName !== null) setState('offline', 'offline', retryAt);
  }

  /** 第一則 ready：開始連線、掛事件 */
  function start() {
    if (started) return;
    started = true;
    window.document.addEventListener('visibilitychange', onVisibility);
    window.addEventListener('focus', scheduleClientState);
    window.addEventListener('blur', scheduleClientState);
    window.addEventListener('online', retryNow);
    window.addEventListener('offline', onOffline);
    attemptNow();
  }

  /** 取消等待中的計時器、立刻嘗試（次數不變）；連著、正在連或還沒開始時不做事 */
  function retryNow() {
    if (!started || connecting || connected || redirected) return;
    attemptNow();
  }

  /**
   * 網頁送出的指令：ready 留住（每條新連線重送）；openLink、popout、copyText、layout 在瀏覽器裡處理；
   * 其他的要「連著而且這條連線收到過 init」才送（D5），送不出去時使用者操作的提示、網頁自動送的靜默丟棄。
   * 〔遠端〕回傳這一筆的 commandId（網頁以它對應 commandResult，WP4.6 D3）；沒有送出（ready、在瀏覽器裡處理、序列化不了、沒有連線、太大）回 null。
   * visible 的清單另記成 ClientState 的 openConversationIds（送不送得出去都記），內容變了就重送 ClientState（D2）
   */
  function post(msg) {
    if (!isObj(msg)) return null;
    if (msg.cmd === 'ready') {
      readyMsg = clone(msg);
      if (!started) start();
      else if (connected) sendCmd(readyMsg, null, 0);
      return null;
    }
    if (msg.cmd === 'visible') {
      viewing = rules.viewingIds(msg.conversationIds);
      scheduleClientState();
    }
    const local = rules.localAction(msg, window.location.origin);
    if (local) {
      runLocal(local, msg);
      return null;
    }
    const w = toWire(msg);
    if (!w.ok || !isObj(w.value)) {
      // 循環參照之類的程式錯誤：伺服器也收不了
      console.warn('[transport-hub] 指令序列化不了，沒有送出：' + String(msg.cmd));
      return null;
    }
    if (!sendable()) {
      if (!rules.isAutoCommand(msg)) localToast('error', TEXT_OFFLINE);
      revertSetting(w.value);
      return null;
    }
    const key = rules.mergeKey(w.value);
    if (key) {
      mergeUnsettledUpdate(key, w.value);
      cancelRetry(key);
    }
    // 太大的照舊交給 sendCmd（警告、提示、改回各一次），這裡只用同一個檢查決定回傳值
    const commandId = rules.ulid(Date.now());
    const fitsFrame = rules.fits('Cmd', { commandId, cmd: w.value });
    sendCmd(w.value, commandId, 0);
    return fitsFrame ? commandId : null;
  }

  function onMessage(fn) {
    if (typeof fn === 'function') subscribers.push(fn);
  }

  function status() {
    return Object.freeze({ state: stateName, reason, attempt, retryAt, lastCloseAt, hubInit, generation });
  }

  window.myriagentHubTransport = Object.freeze({
    version: 1,
    ok,
    rules,
    post,
    onMessage,
    status,
    retryNow,
  });
})();
