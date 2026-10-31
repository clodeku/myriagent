// 網頁版遠端帳戶與 CLI 操作的純規則（WP4.8）：矩陣格子與各電腦 CLI 的按鈕、停用與原因、確認框的文字、「在所有電腦登出」、
// 送出時的提示、分頁自己追蹤的遠端登入（D25）；以及對話標頭的帳戶選單在執行中的樣子（桌面與瀏覽器版共用）。協定見 docs/web-protocol.md（accountLoginDevice、accountLogout、cliInstall、cliUpdate）。
// 沒有 import、不碰 DOM、不讀時鐘：現在時間一律由呼叫端傳入（tools/ui-probe/accountrules.test.mjs 以 data: URL 直接匯入測試）。
// 一律回新物件、不改傳入的參數；電腦名稱、帳戶名稱、statusText 都是不可信的字串，畫面一律以 textContent 顯示。
// 瀏覽器的帳戶 id 是邏輯帳戶（la_…）：accountId、logicalAccountId 都放它，由伺服器換成那台的本機帳戶。

/** 送出登入之後多久還沒看到那台開始登入（loginBusy），就當成被拒（伺服器已經另外提示過） */
export const LOGIN_REJECT_GUESS_MS = 60_000;
/** 一次遠端登入最多追蹤多久（裝置碼 15 分鐘＋緩衝） */
export const LOGIN_MAX_MS = 16 * 60_000;
/** 遠端「立即檢查」最多等多久（同 dashboard.js 本機的 CLI_CHECK_TIMEOUT） */
export const CLI_CHECK_TIMEOUT_MS = 3 * 60_000;

const isObj = (v) => !!v && typeof v === 'object' && !Array.isArray(v);
const str = (v) => (typeof v === 'string' && v ? v : null);
const num = (v) => (typeof v === 'number' && Number.isFinite(v) ? v : null);
const count = (v) => Math.max(0, Math.trunc(num(v) || 0));

/** 產品名稱（同宿主 AgentProviders.ProductName）。2026-10-22 起四種都同步、看得到 */
const PRODUCT = { Claude: 'Claude Code', Codex: 'Codex', Antigravity: 'Antigravity', Grok: 'Grok Build' };
/**
 * 能從網頁操作帳戶與 CLI（登入、登出、安裝、更新）的種類：四種都可以（Grok、Antigravity 自設計 2026-10-22-grok-antigravity-sync 的 P3）。
 * 各種類的差別：Codex、Grok 用裝置碼登入；Claude 由那台給登入網址、在這裡登入後貼回授權碼（2026-10-26；那台的 CLI 太舊時改開登入視窗）；
 * Antigravity 在那台開登入視窗；Antigravity 沒有登出指令（D4）
 */
const REMOTE = new Set(['Claude', 'Codex', 'Grok', 'Antigravity']);

/** 有登出子指令、能從網頁登出的種類（Antigravity 只有互動介面的 /logout） */
const canLogout = (p) => p !== 'Antigravity';
const remoteProvider = (p) => typeof p === 'string' && REMOTE.has(p);

/** 給人看的產品名稱（Claude → Claude Code、Grok → Grok Build）；不認得的種類照原樣 */
export function productName(provider) {
  return typeof provider === 'string' && Object.prototype.hasOwnProperty.call(PRODUCT, provider) ? PRODUCT[provider] : String(provider || '');
}

/**
 * 執行中能換帳戶的種類（同中樞 AccountSwitchRules.SupportsSwitchWhileRunning；使用者 2026-10-24 起四種之間都能互換，
 * 換到另一種代理時新的一方以前文節錄開新對話）。順序同 init 的 providers；不認得的種類（之後新增、這版還不知道的）不行
 */
const SWITCH_WHILE_RUNNING = ['Claude', 'Codex', 'Antigravity', 'Grok'];

/** 側欄帳戶卡最多畫幾個用量圓環 */
export const SIDEBAR_GAUGES = 2;

/**
 * 側欄帳戶卡的用量圓環要畫哪幾個時間窗（照宿主給的順序取前 SIDEBAR_GAUGES 個）。
 * Claude Code 不列「每週・Fable」（2026-10-31 使用者要求）：每週額度用完時沒有「5 小時」，前兩個就變成「每週」＋「每週・Fable」；
 * 儀表板照舊全部列。認 key（weekly:Fable，不分大小寫），沒有 key 時看標籤（每週・Fable）。不改傳入的陣列
 */
export function sidebarWindows(provider, windows) {
  const list = Array.isArray(windows) ? windows.filter((w) => isObj(w)) : [];
  const kept = provider === 'Claude' ? list.filter((w) => !isClaudeFableWindow(w)) : list;
  return kept.slice(0, SIDEBAR_GAUGES);
}

function isClaudeFableWindow(w) {
  const key = str(w.key);
  if (key) return key.toLowerCase() === 'weekly:fable';
  const label = str(w.label);
  return !!label && /^每週・\s*fable$/i.test(label.trim());
}

/** 這種代理的對話執行中能不能換帳戶（換到能在執行中換的任何一種代理的帳戶） */
export function switchableWhileRunning(provider) {
  return typeof provider === 'string' && SWITCH_WHILE_RUNNING.includes(provider);
}

/**
 * 對話標頭的帳戶選單在執行中的樣子：列這台能用、能在執行中換的帳戶（含目前的；2026-10-24 起四種都可以），同一種代理在前、
 * 其他種類照 providers（init 的 providers；沒給用內建的順序）各一段（會以前文節錄開新對話）——
 * 選了之後再選時機（runningSwitchConfirm）：立即＝中樞先停下這一輪、結束時換過去，新帳戶帶著前文節錄從中斷處接著做；
 * 目前步驟完成後＝等工具、背景工作與待回應請求結束，再停下交接（AgentHub.SwitchAccountWhileRunning）。
 * pending 是 pendingSwitchView 的結果：等目前步驟完成的另給「取消換帳戶」（cancel），要換過去的那個帳戶標上說明（pendingId、pendingHint）。
 * 其他種類不開選單，回提示。accounts 是 state.accounts（不改它）。
 * 回 { allowed, reason, sections: [{ provider, header, accounts }], empty, cancel, pendingId, pendingHint }
 */
export function runningAccountMenu(provider, accounts, pending, providers, currentAccountId) {
  if (!switchableWhileRunning(provider)) {
    return { allowed: false, reason: '這一輪執行中不能更換 ' + productName(provider) + ' 帳戶，請先停止或等它完成', sections: [], empty: null, cancel: null, pendingId: null, pendingHint: null, disabledIds: [] };
  }
  const usable = (Array.isArray(accounts) ? accounts : []).filter((a) => isObj(a) && switchableWhileRunning(a.provider) && !a.remoteOnly);
  // 桌面本機帳戶（logicalId !== id）若本機尚未登入或未安裝 CLI，標進 disabledIds 避免點選後才被擋；
  // 瀏覽器版的邏輯帳戶（logicalId === id）其 status 是跨電腦聚合摘要，照舊允許點選並由目標電腦中樞判定
  const disabledIds = usable
    .filter((a) => a.logicalId !== a.id && a.id !== currentAccountId && (a.status === 'notInstalled' || a.status === 'notLoggedIn'))
    .map((a) => a.id)
    .filter((id) => typeof id === 'string' && id);
  const known = Array.isArray(providers) && providers.length ? providers.filter(switchableWhileRunning) : SWITCH_WHILE_RUNNING;
  const order = [provider, ...known.filter((x) => x !== provider)];
  const sections = order.map((x) => ({
    provider: x,
    header: x === provider ? x + '（立即換或等目前步驟完成後換）' : x + '（會以前文節錄開新對話）',
    accounts: usable.filter((a) => a.provider === x),
  })).filter((s) => s.accounts.length);
  const p = isObj(pending) && str(pending.accountId) ? pending : null;
  return {
    allowed: true, reason: null, sections,
    empty: usable.length > 1 ? null : '沒有其他帳戶',
    cancel: p && p.turnEnd ? { label: '取消換帳戶', desc: p.hint } : null,
    pendingId: p ? p.accountId : null,
    pendingHint: p ? p.hint : null,
    disabledIds,
  };
}

/** setAccount 的 when（同中樞 AccountSwitchRules.WhenTurnEnd）：目前步驟完成後換，沿用 turnEnd 協定值 */
export const SWITCH_AT_TURN_END = 'turnEnd';

/**
 * 執行中選了另一個帳戶的選擇框（ui.choiceDialog 的參數）：
 * choices 的 value 是 'now'（停下這一輪，新帳戶接著做）或 'turnEnd'（等目前步驟完成，到了安全點就交接）。
 * 等目前步驟完成是預設。fromProvider、toProvider 不同（換到另一種代理）時另說明會以前文節錄開新的對話。
 * label 是不可信的字串，畫面以 textContent 顯示
 */
export function runningSwitchConfirm(label, fromProvider, toProvider) {
  const name = '「' + String(label || '') + '」';
  const cross = typeof fromProvider === 'string' && typeof toProvider === 'string' && fromProvider !== toProvider;
  return {
    title: '改用' + name + '繼續？',
    message: cross
      ? '這一輪還在執行。' + productName(fromProvider) + ' 和 ' + productName(toProvider) + ' 的對話不能直接接續，' + name
        + '會帶著先前對話的節錄開新的對話；可以等目前步驟完成後換，或現在就停下、由它從中斷處接著做。'
      : '這一輪還在執行。可以等目前步驟完成後換，或現在就停下，由' + name + '帶著先前對話的節錄從中斷處接著做。',
    detail: '等目前步驟完成：等執行中的工具、背景工作與待回答的請求結束後，自動改由' + name + '接續剩餘工作，不必等整個任務做完。現在停下：正在執行的指令會被中斷。',
    choices: [
      { value: 'now', label: '現在停下並改用' },
      { value: SWITCH_AT_TURN_END, label: '目前步驟完成後換', primary: true },
    ],
  };
}

/** 執行中換帳戶的 setAccount：when 只在等目前步驟完成時帶（沿用 turnEnd，舊桌面仍會等整輪結束） */
export function runningSwitchCommand(conversationId, accountId, when) {
  const cmd = { cmd: 'setAccount', conversationId, accountId };
  if (when === SWITCH_AT_TURN_END) cmd.when = SWITCH_AT_TURN_END;
  return cmd;
}

/** setAccount 的 when（同中樞 AccountSwitchRules.WhenNow）：閒置、背景工作還在跑時確認了現在就換 */
export const SWITCH_NOW = 'now';

/**
 * 這一輪結束了、背景工作還在跑時選了另一個帳戶的選擇框（2026-10-27：換帳戶會關掉程序，背景工作跟著停；ui.choiceDialog 的參數）：
 * choices 的 value 是 'now'（現在換，背景工作停掉，新帳戶收到被停掉的背景工作再接著做）或 'turnEnd'（等背景工作都結束、代理處理完結果才換，預設）。
 * background 是標頭的 background（只有桌面這台的對話有）：列出前兩個背景工作。label、背景工作的說明都是不可信的字串，畫面以 textContent 顯示
 */
export function backgroundSwitchConfirm(label, fromProvider, toProvider, background) {
  const name = '「' + String(label || '') + '」';
  const cross = typeof fromProvider === 'string' && typeof toProvider === 'string' && fromProvider !== toProvider;
  const bg = isObj(background) ? background : {};
  const n = Math.max(1, count(bg.count));
  const titles = (Array.isArray(bg.tasks) ? bg.tasks : []).map((t) => (isObj(t) ? str(t.title) || str(t.tool) : null)).filter(Boolean);
  const listed = titles.length ? '（' + titles.slice(0, 2).join('、') + (titles.length > 2 || n > titles.length ? ' 等' : '') + '）' : '';
  return {
    title: '改用' + name + '繼續？',
    message: '還有 ' + n + ' 個背景工作在跑' + listed + '。換帳戶會關掉目前的程序，背景工作會跟著停止。'
      + (cross ? productName(fromProvider) + ' 和 ' + productName(toProvider) + ' 的對話不能直接接續，' + name + '會帶著先前對話的節錄開新的對話。' : ''),
    detail: '等背景工作結束：背景工作做完、代理處理完結果後才換。現在換：背景工作會被停止，' + name + '會收到先前對話的節錄與被停掉的背景工作，確認做到哪裡再接著做。',
    choices: [
      { value: SWITCH_NOW, label: '現在換（停掉背景工作）' },
      { value: SWITCH_AT_TURN_END, label: '背景工作結束後再換', primary: true },
    ],
  };
}

/** 背景工作還在跑時換帳戶的 setAccount：一律帶 when（沒帶的宿主等背景工作結束才換，瀏覽器版看不到背景工作就是這樣） */
export function backgroundSwitchCommand(conversationId, accountId, when) {
  return { cmd: 'setAccount', conversationId, accountId, when: when === SWITCH_NOW ? SWITCH_NOW : SWITCH_AT_TURN_END };
}

/**
 * 標頭的 pendingAccount（執行中選了另一個帳戶、還沒換過去；或選了帳戶、在等背景工作結束才換〔background，執行中與閒置都有〕；
 * 只有本機的對話有，瀏覽器版與其他電腦的對話沒有）給帳戶晶片與帳戶選單用：
 * 沒有、形狀不對、就是目前的帳戶，或不在執行中又不是等背景工作的回 null；回 { accountId, label, turnEnd, background, hint }（label 是不可信的字串）
 */
export function pendingSwitchView(header) {
  if (!isObj(header) || !isObj(header.pendingAccount)) return null;
  const p = header.pendingAccount;
  const background = p.background === true;
  if (header.running !== true && !background) return null;
  const id = str(p.accountId);
  if (!id || id === header.accountId) return null;
  const label = str(p.label) || id;
  const turnEnd = background || p.when === SWITCH_AT_TURN_END;
  const hint = background ? '背景工作結束後改用「' + label + '」' : turnEnd ? '目前步驟完成後改用「' + label + '」' : '停下這一輪後改用「' + label + '」';
  return { accountId: id, label, turnEnd, background, hint };
}

/** 帳戶晶片的說明最後一行：副本、執行中（已選了要換的、能不能換）、等背景工作結束才換、平常。pending 是 pendingSwitchView 的結果 */
export function accountChipHint(provider, running, replica, pending) {
  if (replica) return '其他電腦的對話不能更換帳戶';
  if (!running) return isObj(pending) && pending.background && str(pending.hint) ? pending.hint + '（點一下可以取消或現在就換）' : '點一下更換帳戶';
  if (isObj(pending) && str(pending.hint)) return pending.hint + (pending.turnEnd ? '（點一下可以取消）' : '');
  return switchableWhileRunning(provider)
    ? '執行中也可以換帳戶（立即或等目前步驟完成後換）'
    : '執行中不能更換帳戶';
}

function nameOf(device, fallback) {
  return (isObj(device) && str(device.name)) || str(fallback) || '那台電腦';
}

/**
 * 那台電腦能不能接受遠端操作（D24）：已撤銷或離線 →「〈名稱〉目前離線」（剛斷線是「〈名稱〉重新連線中」，remoterules.trackDeviceLinks）；remoteControlLevel 不是 Control →「在〈名稱〉的設定中允許遠端操作」
 * （和 WP4.6 的停用說明同一句；〈〉是佔位，實際是名稱前後各一個空白）。回傳 { ok, reason }
 */
export function remoteAllowed(device) {
  const name = nameOf(device);
  if (!isObj(device) || device.revoked) return { ok: false, reason: `${name} 已撤銷，不能遠端操作` };
  if (device.online !== true) return { ok: false, reason: device.reconnecting === true ? `${name} 重新連線中` : `${name} 目前離線` };
  if (device.remoteControlLevel !== 'Control') return { ok: false, reason: `在 ${name} 的設定中允許遠端操作` };
  return { ok: true, reason: null };
}

function action(allowed, key, label, cmd, confirm) {
  return { key, label, cmd, disabled: !allowed.ok, reason: allowed.reason, confirm: confirm || null };
}

function runningText(running) {
  const n = count(running);
  return n ? `這個帳戶目前有 ${n} 個對話執行中（所有電腦合計），登出後可能無法繼續。` : '這個帳戶目前沒有對話執行中。';
}

function logoutConfirm(name, running) {
  return {
    title: `登出 ${name} 的這個帳戶？`,
    message: `${name} 的這個帳戶會登出，之後要重新登入才能在那台使用。`,
    detail: [runningText(running), '那台若使用電腦預設的設定資料夾（~/.claude、~/.codex、~/.grok），終端機與 VS Code 擴充也會一起登出。'].join('\n'),
  };
}

function antigravityLoginConfirm(name) {
  return {
    title: `在 ${name} 開啟 Antigravity 登入視窗？`,
    message: '會在那台電腦開啟 agy 視窗，要有人在那台電腦前完成 Google 登入。那台的 agy 只有一個登入：登入別的帳號會換掉原本的。',
    detail: null,
  };
}

function claudeLoginConfirm(name) {
  return {
    title: `在 ${name} 登入 Claude？`,
    message: '那台電腦會給出 Claude 的登入網址：在這裡（例如這支手機）打開並登入，再把登入頁上的授權碼貼回帳戶卡。',
    detail: '那台的 Claude CLI 太舊時改在那台開登入視窗，要有人在那台電腦前完成。',
  };
}

/** 安裝的確認框：Claude、Antigravity、Grok 會在那台開看得見的安裝視窗（要有人關）；Codex 不需要確認 */
function installConfirm(provider, name, reinstall) {
  switch (provider) {
    case 'Claude':
    case 'Antigravity':
    case 'Grok':
      return {
        title: `在 ${name} ${reinstall ? '重新安裝' : '安裝'} ${productName(provider)} CLI？`,
        message: '會在那台電腦開啟安裝視窗，要有人在那台電腦前完成並關閉視窗。',
        detail: null,
      };
    case 'Codex':
      return null;
    default:
      return null;
  }
}

function updateConfirm(provider, name) {
  return {
    title: `更新 ${name} 的 ${productName(provider)} CLI？`,
    message: '那台執行中的對話會在安全點停下、換版後接續；停不下來時要在那台選擇。',
    detail: null,
  };
}

/**
 * 矩陣一格的按鈕（R22）：view＝remoterules.cellView 的結果（只看 kind、expiring，不重判優先順序）；
 * ctx＝{ provider, accountId（la_…）, deviceId, deviceName, device（DeviceView）, pendingHere（這個分頁發起的登入）, loginBusy（帳戶層級：所有電腦的 Any）,
 * running（帳戶的執行中對話數）, pendingElsewhere（這個分頁正在別台登入這個帳戶時是那台的名稱：登入類的按鈕停用，一個帳戶同時只追蹤一次） }。
 * 這個分頁沒有追蹤這個帳戶、帳戶卻在登入中（重新整理之後、別的分頁、那台本機發起的；審查 F4）：在線而且可能在登入的格子另外多一顆「取消登入」。
 * 回傳 [{ key, label, cmd, disabled, reason, confirm: { title, message, detail } | null, hint? }]；空陣列就不畫。全部經 remoteAllowed 決定停用
 */
export function cellActions(view, ctx) {
  const v = isObj(view) ? view : null;
  const c = isObj(ctx) ? ctx : {};
  const accountId = str(c.accountId);
  const deviceId = str(c.deviceId);
  if (!v || !accountId || !deviceId || !remoteProvider(c.provider)) return [];
  const provider = c.provider;
  const name = nameOf(c.device, c.deviceName);
  const allowed = remoteAllowed(c.device);
  const cancel = (hint) => Object.assign(action(allowed, 'cancel', '取消登入', { cmd: 'accountLoginCancel', accountId, deviceId }, null), { hint });

  // 這個分頁發起的登入：那台開始登入後只給「取消登入」；還沒開始時先不給任何會送出的按鈕
  if (c.pendingHere === true) {
    // 斷線或重新整理時驗證碼只送一次、會掉（輸出不保存）：驗證碼仍在那台的帳戶卡上
    if (c.loginBusy === true) return [cancel(`取消 ${name} 正在進行的登入；這裡看不到驗證碼時（例如斷線之後），驗證碼仍顯示在那台的帳戶卡上`)];
    return [{ key: 'waiting', label: '登入中…', cmd: null, disabled: true, reason: `已送出，正在等 ${name} 開始登入`, confirm: null }];
  }

  const actions = baseCellActions(v, c, provider, accountId, deviceId, name, allowed);
  // 帳戶在登入中、這個分頁卻沒有追蹤它（重新整理之後、別的分頁、那台本機發起的；審查 F4）：loginBusy 是帳戶層級、看不出是哪台，
  // 所以在線、不是尚未加入或沒裝 CLI（那兩種不會在登入）的格子另外多一顆「取消登入」（那台沒有在登入時什麼都不做）。
  // 登入類的按鈕照舊、不因帳戶層級的 busy 停用（離線電腦留下的 busy 會讓每一格都不能登入）
  const busyUntracked = c.loginBusy === true && !str(c.pendingElsewhere) && isObj(c.device) && c.device.online === true
    && v.kind !== 'absent' && v.kind !== 'notInstalled';
  if (!busyUntracked) return actions;
  return actions.concat([cancel(`這個帳戶正在某台電腦登入（例如從別的分頁、重新整理之前，或在那台本機）：取消 ${name} 正在進行的登入；${name} 沒有在登入時什麼都不做。驗證碼顯示在那台的帳戶卡上`)]);
}

/** cellActions 在這個分頁沒有發起登入時的按鈕（依格子的種類） */
function baseCellActions(v, c, provider, accountId, deviceId, name, allowed) {
  // 這個分頁正在別台登入這個帳戶：登入類的按鈕停用（離線、政策的原因優先）
  const elsewhere = str(c.pendingElsewhere);
  const loginAllowed = allowed.ok && elsewhere ? { ok: false, reason: `這個帳戶正在 ${elsewhere} 登入，完成或取消之後才能在其他電腦登入` } : allowed;
  const login = (key, label) => {
    switch (provider) {
      case 'Codex':
      case 'Grok':
        return action(loginAllowed, key, label, { cmd: 'accountLoginDevice', logicalAccountId: accountId, deviceId, method: 'deviceCode' }, null);
      case 'Claude':
        return action(loginAllowed, key, label, { cmd: 'accountLoginDevice', logicalAccountId: accountId, deviceId }, claudeLoginConfirm(name));
      case 'Antigravity':
        return action(loginAllowed, key, label, { cmd: 'accountLoginDevice', logicalAccountId: accountId, deviceId }, antigravityLoginConfirm(name));
      default:
        return null;
    }
  };
  const logout = (label) => action(allowed, 'logout', label, { cmd: 'accountLogout', accountId, deviceId }, logoutConfirm(name, c.running));
  const logouts = (label) => (canLogout(provider) ? [logout(label)] : []);

  switch (v.kind) {
    case 'ready':
      if (v.expiring !== 'expired') return logouts('登出這台');
      switch (provider) {
        case 'Claude': return [login('relogin', '在那台重新登入'), logout('登出這台')];
        default: return logouts('登出這台');
      }
    case 'notLoggedIn':
      switch (provider) {
        case 'Codex':
        case 'Grok':
          return [login('login', '用裝置碼登入')];
        case 'Claude':
          return [login('login', '在那台登入')];
        case 'Antigravity':
          return [login('login', '在那台開登入視窗')];
        default: return [];
      }
    case 'absent':
      // Antigravity 一台只有一個 agy 登入：不能從目錄在那台建立（電腦也回 policy）
      return provider === 'Antigravity' ? [] : [login('create', '在那台建立並登入')];
    case 'notInstalled':
      return [action(allowed, 'install', '安裝', { cmd: 'cliInstall', provider, deviceId }, installConfirm(provider, name, false))];
    case 'error':
    case 'checking':
      return [action(allowed, 'recheck', '重新檢查', { cmd: 'accountRefresh', accountId, deviceId }, null)];
    case 'identity':
      return logouts('登出那台');
    case 'apiKey':
    default:
      return [];
  }
}

/**
 * 「在所有電腦登出…」（D29）：accountLogout 不帶 deviceId（伺服器轉給每台有這個帳戶的電腦，durable）。
 * 確認框寫明：離線的電腦在 7 天內上線才執行、用電腦預設資料夾的那台終端機與 VS Code 擴充也會登出、這個帳戶執行中的對話數（所有電腦合計）。
 * 回傳 { cmd, title, message, detail }；不是帳戶回 null
 */
export function logoutAllPlan(account) {
  if (!isObj(account) || !str(account.id) || !canLogout(account.provider)) return null;
  const label = str(account.label) || '這個帳戶';
  return {
    cmd: { cmd: 'accountLogout', accountId: account.id },
    title: `在所有電腦登出「${label}」？`,
    message: '每台有這個帳戶的電腦都會登出；離線的電腦要在 7 天內上線才會執行。',
    detail: [
      '使用電腦預設的設定資料夾（~/.claude、~/.codex、~/.grok）的電腦，終端機與 VS Code 擴充也會一起登出。',
      runningText(isObj(account.sessions) ? account.sessions.running : 0),
    ].join('\n'),
  };
}

function cliRowAction(provider, view, deviceId, name, allowed) {
  if (!remoteProvider(provider) || !isObj(view)) return null;
  switch (view.kind) {
    case 'broken':
      return action(allowed, 'reinstall', '重新安裝', { cmd: 'cliInstall', provider, deviceId }, installConfirm(provider, name, true));
    case 'notInstalled':
      return action(allowed, 'install', '安裝', { cmd: 'cliInstall', provider, deviceId }, installConfirm(provider, name, false));
    case 'update':
      return action(allowed, 'update', '更新', { cmd: 'cliUpdate', provider, deviceId }, updateConfirm(provider, name));
    default:
      return null;
  }
}

/**
 * 各電腦 CLI 卡的按鈕：entry＝remoterules.deviceCliModel 的一台；device＝那台的 DeviceView（停用看 remoteAllowed）；ctx＝{ checking }（遠端「立即檢查」進行中）。
 * 回傳 { check: 卡頭的「立即檢查」, rows: { [provider]: 那一列的「安裝／重新安裝／更新」或 null } }；cliCheck 留在退路，那台開了自動更新時會接著更新（D18，寫在 hint）
 */
export function deviceCliActions(entry, device, ctx) {
  if (!isObj(entry) || !str(entry.deviceId)) return { check: null, rows: {} };
  const deviceId = entry.deviceId;
  const name = nameOf(device, entry.name);
  const allowed = remoteAllowed(device);
  const checking = isObj(ctx) && ctx.checking === true;
  const check = {
    key: 'check',
    label: checking ? '檢查中…' : '立即檢查',
    cmd: { cmd: 'cliCheck', deviceId },
    disabled: !allowed.ok || checking,
    reason: allowed.reason,
    confirm: null,
    hint: `檢查 ${name} 的 CLI 版本；那台開了 CLI 自動更新時，查到新版會接著更新`,
  };
  const rows = {};
  for (const cli of Array.isArray(entry.clis) ? entry.clis : []) {
    if (!isObj(cli) || !str(cli.provider)) continue;
    rows[cli.provider] = cliRowAction(cli.provider, cli.view, deviceId, name, allowed);
  }
  return { check, rows };
}

/** 按「立即檢查」當下那台每種 CLI 的 checkedAt（cliOfDevice＝devicesCli[deviceId]）：{ [provider]: checkedAt | null } */
export function cliCheckSnapshot(cliOfDevice) {
  const out = {};
  if (!isObj(cliOfDevice)) return out;
  for (const p of Object.keys(cliOfDevice)) {
    if (isObj(cliOfDevice[p])) out[p] = num(cliOfDevice[p].checkedAt);
  }
  return out;
}

/** 遠端「立即檢查」完成了沒：before 的每一種 CLI，現在的 checkedAt 都有值而且變了才 true（同本機 settleCliCheck） */
export function cliCheckDone(before, cliOfDevice) {
  if (!isObj(before)) return false;
  const now = isObj(cliOfDevice) ? cliOfDevice : {};
  return Object.keys(before).every((p) => {
    const at = isObj(now[p]) ? num(now[p].checkedAt) : null;
    return at !== null && at !== before[p];
  });
}

/**
 * 遠端「立即檢查」完成時的提示（同本機 dashboard.cliCheckResult 的說法）：「OFFICE-PC 檢查完成：Claude Code 已是最新版；Codex 有新版 0.51.0」。
 * cliOfDevice＝devicesCli[deviceId]（DeviceCliView）；order＝種類的順序（providers），沒回報的種類略過。回傳 { text, level }
 */
export function cliCheckResult(deviceName, cliOfDevice, order) {
  const name = str(deviceName) || '那台電腦';
  const cli = isObj(cliOfDevice) ? cliOfDevice : {};
  const parts = [];
  let level = 'success';
  for (const p of Array.isArray(order) ? order : Object.keys(cli)) {
    const v = cli[p];
    if (!isObj(v)) continue;
    const product = productName(p);
    if (str(v.installBroken)) {
      parts.push(product + ' 無法執行');
      level = 'error';
    } else if (v.installed !== true) {
      parts.push(product + ' 尚未安裝');
    } else if (v.updateAvailable === true) {
      parts.push(product + (str(v.latest) ? ' 有新版 ' + v.latest : ' 有新版'));
      if (level === 'success') level = 'info';
    } else if (str(v.latest)) {
      parts.push(product + ' 已是最新版');
    } else {
      // 那台查最新版失敗（離線、registry 被擋）：不能說已是最新版
      parts.push(product + ' 查不到最新版本');
      level = 'error';
    }
  }
  return { text: `${name} 檢查完成` + (parts.length ? '：' + parts.join('；') : ''), level };
}

/** 追蹤用的格子種類：ready 但登入已到期另記 'expired'（那不是可以用的登入） */
function watchKind(cell) {
  if (!isObj(cell)) return 'none';
  if (cell.kind === 'ready') return cell.expiring === 'expired' ? 'expired' : 'ready';
  return str(cell.kind) || 'none';
}

/**
 * 送出登入時要記下的追蹤（D25；只在這個分頁的記憶體）：ctx 同 cellActions，view＝送出當下那一格的 cellView 結果，kind＝按鈕的 key，
 * commandId＝post 回的指令 id（步 12：對得上 cmdtrack 的結果才用，見 loginSettle；不是非空字串時 null）。
 * 回傳 { logicalId, deviceId, deviceName, provider, kind, at, fromKind, commandId, started:false, canceled:false }
 */
export function loginPending(ctx, view, nowMs, kind, commandId) {
  const c = isObj(ctx) ? ctx : {};
  return {
    logicalId: str(c.accountId),
    deviceId: str(c.deviceId),
    deviceName: nameOf(c.device, c.deviceName),
    provider: str(c.provider),
    kind: str(kind) || 'login',
    at: num(nowMs) ?? 0,
    fromKind: watchKind(view),
    commandId: str(commandId),
    started: false,
    canceled: false,
  };
}

function endedText(pending, cell, name) {
  const why = isObj(cell) ? str(cell.statusText) : null;
  // 「尚未登入…」是那台的預設說明（AccountRules.DescribeStatus），說不出原因：改寫常見原因；D15 的身分不符說明、錯誤訊息照原樣
  if (why && !why.startsWith('尚未登入')) return `${name} 的登入沒有完成：${why}`;
  switch (pending.provider) {
    case 'Codex': return `${name} 的登入沒有完成（常見原因：ChatGPT 的安全性設定沒有開啟裝置碼登入、驗證碼過期或被取消）`;
    case 'Grok': return `${name} 的登入沒有完成（常見原因：驗證碼過期（10 分鐘）或被取消）`;
    case 'Claude': return `${name} 的登入沒有完成（常見原因：沒有在時限內登入並貼回授權碼、授權碼不對，或那台的登入視窗被關掉）`;
    case 'Antigravity': return `${name} 的登入沒有完成（常見原因：agy 視窗被關掉，或沒有在時限內完成 Google 登入）`;
    default: return `${name} 的登入沒有完成`;
  }
}

/**
 * 分頁自己追蹤一次遠端登入（D25）：pending＝loginPending 的結果；account＝帳戶（看 loginBusy、label）；
 * cell＝那一格（remoterules.cellView 的結果加上伺服器原始的 statusText：看 kind、expiring、statusText）；login＝state.login 裡這個帳戶的那一筆（看驗證碼的到期，可以沒有）。
 * 回傳 { state, pending, clearCode, toast: { text, level } | null }：
 * state＝'pending'（繼續追蹤，pending 是新的物件）｜'ready'｜'ended'｜'expired'｜'rejected'｜'canceled'｜'none'（沒有追蹤）；
 * 結束時 pending 是 null、clearCode 是 true（清掉這個帳戶的驗證碼）。
 * loginBusy 是帳戶層級（所有電腦的 Any，離線電腦留下的也算；R16）：目標那一格從不是 ready 變成 ready 一律是成功、不看 busy（審查 F2）；
 * 別台留下的 busy 只會讓沒有完成的登入晚一點結束（到驗證碼到期或 16 分鐘才判 expired）
 */
export function loginWatch(pending, account, cell, nowMs, login) {
  if (!isObj(pending)) return { state: 'none', pending: null, clearCode: false, toast: null };
  const now = num(nowMs) ?? 0;
  const at = num(pending.at) ?? now;
  const busy = isObj(account) && account.loginBusy === true;
  const ready = watchKind(cell) === 'ready';
  const name = str(pending.deviceName) || '那台電腦';
  const label = (isObj(account) && str(account.label)) || '這個帳戶';
  const started = pending.started === true;
  const end = (state, toast) => ({ state, pending: null, clearCode: true, toast });
  const success = () => end('ready', { text: `${name} 已登入「${label}」`, level: 'success' });

  // 目標那一格從不是 ready 變成 ready：成功，不管 busy（遠端登入期間 identityHold 擋住那台的查詢，只有這次登入完成才會變 ready；
  // 帳戶層級的 loginBusy 可能是別台留下的，審查 F2）。送出時已經是 ready 的（只有規則測試會構造）不算
  if (ready && pending.fromKind !== 'ready') return success();
  // 那台開始登入之後 loginBusy 變回 false：這次登入結束了
  if (started && !busy) {
    if (ready) return success();
    if (pending.canceled === true) return end('canceled', null);
    return end('ended', { text: endedText(pending, cell, name), level: 'error' });
  }
  if (!started && !busy && pending.canceled === true) return end('canceled', null);
  const codeExpiresAt = isObj(login) && isObj(login.deviceCode) ? num(login.deviceCode.expiresAt) : null;
  if ((codeExpiresAt !== null && now >= codeExpiresAt) || now - at >= LOGIN_MAX_MS) {
    return end('expired', { text: `${name} 的登入沒有在時限內完成`, level: 'error' });
  }
  // 送出 60 秒都沒看到那台開始登入：被拒了（伺服器已經提示過，不再提示）。步 12 起被拒的結果由 loginSettle 立刻結束，這條是收不到結果時的保險
  if (!started && !busy && now - at >= LOGIN_REJECT_GUESS_MS) return end('rejected', null);
  return { state: 'pending', pending: Object.assign({}, pending, busy ? { started: true } : null), clearCode: false, toast: null };
}

/**
 * 送出的登入指令有了結果（WP4.6 的 cmdtrack，步 12）：pending＝這個帳戶現在追蹤的那一筆（可以沒有）、commandId＝這個結果對應的指令、
 * state＝'done'｜'rejected'｜'unknown'。只有同一次送出（pending.commandId 相同）而且 rejected（伺服器、電腦或傳輸層都沒有執行）才立刻結束、
 * 不提示（伺服器或傳輸層已提示），取代 loginWatch 送出 60 秒的推斷；done 只代表那台已經開始（不是登入完成），unknown（新的 init、150 秒過期）
 * 可能已經在那台開始：都照 loginWatch 繼續追蹤。回傳同 loginWatch：沒有追蹤是 'none'；繼續追蹤是 'pending'、pending 是傳入的那一筆（不換新物件）
 */
export function loginSettle(pending, commandId, state) {
  if (!isObj(pending)) return { state: 'none', pending: null, clearCode: false, toast: null };
  const keep = { state: 'pending', pending, clearCode: false, toast: null };
  // 別次送出的結果（同一個帳戶結束之後又送了一次）不影響這一筆
  if (!str(commandId) || pending.commandId !== commandId) return keep;
  if (state === 'rejected') return { state: 'rejected', pending: null, clearCode: true, toast: null };
  return keep;
}

/**
 * 送出時的本機提示（D33）：伺服器只在 rejected／unknown 另送提示，done 沒有。單台登出、Claude 與 Antigravity 的登入、取消登入、重新檢查、CLI 安裝與更新
 * 回「已要求〈電腦〉…」；所有電腦登出（沒有 deviceId，伺服器另送 toast）、裝置碼登入（等驗證碼）、立即檢查（按鈕顯示檢查中）與其他指令回 null。
 * subject＝帳戶名稱（帳戶指令）或產品名稱（CLI 指令）
 */
export function sentText(cmd, deviceName, subject) {
  if (!isObj(cmd)) return null;
  const name = str(deviceName) || '那台電腦';
  const what = str(subject);
  const account = what ? `「${what}」` : '這個帳戶';
  switch (cmd.cmd) {
    case 'accountLogout':
      return str(cmd.deviceId) ? `已要求 ${name} 登出${account}` : null;
    case 'accountLoginDevice':
    case 'accountLogin':
      return cmd.method === 'deviceCode' ? null : `已要求 ${name} 開始${account}的登入`;
    case 'accountLoginCancel':
      return `已要求 ${name} 取消登入`;
    case 'accountRefresh':
      return `已要求 ${name} 重新檢查${account}`;
    case 'cliInstall':
      return `已要求 ${name} 安裝 ${what ? what + ' ' : ''}CLI`;
    case 'cliUpdate':
      return `已要求 ${name} 更新 ${what ? what + ' ' : ''}CLI`;
    default:
      return null;
  }
}

/** 驗證碼上方標示這次登入在哪台（瀏覽器版）：「在 OFFICE-PC」；沒有追蹤回 null。名稱照原樣（之後一律 textContent） */
export function originLabel(pending) {
  return isObj(pending) && str(pending.deviceName) ? `在 ${pending.deviceName}` : null;
}
