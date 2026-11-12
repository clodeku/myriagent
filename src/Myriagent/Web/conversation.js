// 一個開著的分頁＝一個 ConvView：保留自己的對話狀態與 DOM，非作用中時隱藏（切分頁不必重新 open）
import { h, iconButton, baseName, fmtElapsed, setAcc, safeColor, replayClass, setHidden } from './util.js';
import { icon } from './icons.js';
import { post, state, accountOf, summaryOf, emit, providerKeys, isHub, isCollaborator, on, nextRequestId } from './store.js';
import { canManageCollaborators, openShareDialog } from './collab.js';
import { sharedWith } from './collabrules.js';
import { openMenu, openDialog, confirmDialog, choiceDialog, toast, spinner } from './ui.js';
import {
  originOf, deviceOf, deviceLine, statusLine, composerGate, cardGate, deleteConfirmText, deleteProgramsCheckbox, policyChoices, needsLocalOnlyConfirm, LOCAL_ONLY_CONFIRM_TEXT, loadChunks, LAZY_BATCH, loadRetryDelay, LOAD_RETRY_MS,
  attachGate, folderPickerGate, ownerRunning, mediaPolicyNote, peerOf, controllable, peerDraftDevices, peerDraftAccount, clearBoundary,
  latestPrompt, promptHistory, promptLine, itemTimeText, itemTimeTitle, dayKey, backgroundLine, backgroundStopMenu, detailTasks, programsView, mergeHeaderLists, missingHeaderLists, HEADER_LISTS_REOPEN_MS, touchUnsticks, touchScrolling,
  remoteAccountMenu, headerAccountId, findAccount, originLink,
} from './liverules.js';
import { track } from './cmdtrack.js';
import { deviceChoices, requestCopy, fetchFile, takeReloadDraft } from './liveview.js';
import { createItem, updateItem, fillsInPlace, applyCardGate, markItemCopied, refreshTimes, settleRetryNotes, playStepPeek, stopStepPeek, tickToolElapsed, tickThinkTimes, tickCardAges, paintAgentUsage, chipMentions, paintDelegation } from './items.js';
import { mentionPlain } from './accountmentions.js';
import { Composer } from './composer.js';
import { FileDrawer } from './files.js';
import { openLightbox, openVideo } from './viewer.js';
import { mediaOfTarget, openMediaMenu } from './mediamenu.js';
import { enumKey, restartStateOf, restartIcon, restartText, restartWaitText, versionArrow, confirmApplyNowConversation } from './hotupdate.js';
import { gitView, gitKey } from './gitrules.js';
import { LaunchBar } from './launchbar.js';
import { Minimap } from './minimap.js';
import { entranceAllowed, turnEndTone, cardSettleFx, splitCount, titleDecodeFx, peekWanted, dropSummary, handoffFx, turnPosition, parseTurnTokens, turnItems, turnSummary, TURN_SUM_MS, turnRules } from './fxrules.js';
import { calmMotion } from './theme.js';
import { setDecodedText } from './textfx.js';
import { tokenCounter } from './tokenfx.js';
import { runningAccountMenu, runningSwitchConfirm, runningSwitchCommand, pendingSwitchView, accountChipHint, backgroundSwitchConfirm, backgroundSwitchCommand, SWITCH_NOW } from './accountrules.js';
import { SENDING_TIMEOUT_MS, ACCEPTED_LINGER_MS, matchSent, queuedSent, rejectedSent, sendKey, failureOf, failureUpdates } from './sendrules.js';
import { pauseFramedVideos } from './framedvideo.js';
import { ScrollAnchor } from './scrollanchor.js';
import { isFlashing } from './attnflash.js';
import { goalView, canRewind } from './claudeoptrules.js';
import { goalSummary, GOAL_STATUS } from './codexrules.js';
import { openCodexPanel } from './codexcontrols.js';
import { openRewind } from './rewind.js';
import { openTaskDetail } from './taskdetail.js';
import { reportIssue } from './errorreport.js';
import { speakItem } from './quickchat.js';

/**
 * 項目照實際高度排版的瀏覽器（同 chat.css 的 .it：觸控裝置、不支援捲動錨定的）。每次載入時才問（平板接上或拆下鍵盤會變）
 */
function fullLayout() {
  const coarse = typeof window.matchMedia === 'function' && window.matchMedia('(pointer: coarse)').matches;
  const anchors = typeof CSS !== 'undefined' && typeof CSS.supports === 'function' && CSS.supports('overflow-anchor', 'auto');
  return coarse || !anchors;
}

const NEAR_BOTTOM = 4;   // 視為「在最底下」
const LEAVE_BOTTOM = 80; // 離底部超過這個距離才停止自動捲動
const DROP_IDLE_MS = 1200; // 拖曳中瀏覽器每隔幾百毫秒就送一次 dragover（滑鼠不動也會）；這麼久沒收到＝已拖走或取消
const HEAD_FLASH_MS = 1300; // 標題列閃爍的長度（chat.css 的 ch-flash；減少動態時是靜態標示這麼久）
const PROG_EXIT_FX_MS = 900; // 背景程式自己結束時晶片閃一下（fx.css 的 .ch-prog.fx-prog-exit）
const DROP_LOCK_FX_MS = 700; // 拖檔案進來時遮罩的角框從外面收進來鎖定（fx.css 的 .drop.fx-drop-lock）
const HANDOFF_FX_MS = 900;   // 換帳戶的交接：帳戶晶片新的帳戶從下面接上、外框閃一下（fx.css 的 .ch-acct.fx-handoff）
const OLDER_SEAM_MS = 1300;  // 載入較早的訊息：接縫（原本最上面那一則的上緣）掃過一道光（fx.css 的 .older-seam）
const POS_SHOW_MS = 1600;    // 捲動位置讀數浮出多久（fx.css 的 .conv-pos.fx-pos）
const CARD_SETTLE_FX_MS = 900; // 卡片有了結果（fx.css 的 .it-card.fx-c-*：閃兩下 700ms）
const ENTER_FX_MS = 900;    // 即時新增項目的進場（fx.css 的 .it.fx-in；卡片的鎖定最長，約 0.8 秒）
const TURN_FX_MS = 1400;    // 一輪結束時輸入框的燈（fx.css 的 .conv.fx-turn-ok／fx-turn-err，1.3 秒）
const COOL_FX_MS = 1300;
const SHOW_FX_MS = 400;     // 切到這個分頁時內容淡入（fx.css 的 .conv.fx-show）    // 停下時輸入框的帳戶色括號與燈慢慢淡掉（fx.css 的 .conv.fx-cool，1.2 秒）
const POPOUT_DRAFT_HINT = '送出第一則訊息後才能在新視窗開啟';
const AUTO_TITLE_BUSY = '正在依目前的工作產生標題…';
/** 標題後面自動重新命名圖示的說明 */
const AUTO_TITLE_HINT = '自動重新命名（依目前正在做或最後做的事產生標題）';
const SHORT_PANE = 360; // 對話窗格比這矮時標題列擠回一行（fitHeader）
/** 開著的對話（刪除的確認與追蹤要找到畫面；側欄也會刪除開著的對話） */
const views = new Map();

/** 瀏覽器版：已連線而且這條連線收到過 init（桌面一律 true） */
function hubLive() {
  return !isHub || !!(state.connection && state.connection.state === 'online' && state.hubInit);
}

/** 電腦晶片說明裡的在線註記：離線是「（離線）」，剛斷線（deviceOf 的 reconnecting）是「（重新連線中）」，其他沒有 */
function offlineNote(device) {
  if (!device || device.online !== false) return '';
  return device.reconnecting === true ? '（重新連線中）' : '（離線）';
}

export class ConvView {
  constructor(id, ws) {
    this.id = id;
    this.ws = ws;
    this.header = null;
    this.items = new Map();
    this.nodes = new Map();
    this.order = [];
    // 〔遠端〕瀏覽器版分批載入大對話（D39）：還沒建節點的較早項目 { list, ranges }（liverules.loadChunks）與下一批的 rAF
    this.chunks = null;
    this.chunkRaf = 0;
    // 〔遠端〕觸控的瀏覽器版載入時先不排版的項目（.it-lazy）：[{ i, node }]，載入完之後由 nextLazy 逐批換回實際高度；與下一批的 rAF
    this.lazyNodes = null;
    this.lazyRaf = 0;
    this.lazyLoad = false;
    this.hasMore = false;
    this.loadingOlder = false;
    // /clear 的分界（liverules.clearBoundary；order 的索引，-1＝沒有藏）與這個分頁按過「顯示清除前的對話」（applyClear）
    this.clearAt = -1;
    this.showCleared = false;
    this.clearDirty = false;
    // 送出中的 loadOlder 的 beforeId（olderItems 回帶時比對）；卡片閘門上次套的結果（refreshCardGates）
    this.olderBefore = null;
    this.cardGateKey = null;
    this.loaded = false;
    this.expanded = new Set();
    // 使用者收起來的思考（思考預設展開，items.js 的 createThinking；其他可展開的項目照 expanded）
    this.collapsed = new Set();
    this.qstate = new Map();
    this.stick = true;
    this.visible = false;
    this.savedTop = null;
    // 送出中（sendrules.js）：按下送出、宿主的使用者訊息還沒到的那幾則，與沒有送出、不確定的 { key, text, files, afterSeq, node, timer, state, failure }
    this.sending = [];
    this.sendCount = 0;
    this.drawer = null;
    // 對話上方常駐的「最近的指令」：目前顯示的（liverules.latestPrompt）、上次畫的內容、往前載入找那則訊息中（{ jump }）
    this.prompt = null;
    this.promptKey = null;
    this.promptSeek = null;
    // 「歷史指令」鈕按了、等宿主的 promptHistory 中（{ requestId, timer }）
    this.historyWait = null;
    // 訊息時間的日期：跨過午夜時重畫（今天的只有時:分）
    this.timeDay = dayKey(Date.now());
    this.ctx = {
      convId: id,
      // 這個對話正顯示在畫面上（背景分頁的計數器不解碼，tokenfx.js）
      shown: () => this.visible,
      // 回 post 的結果：瀏覽器版是 commandId（沒送出是 null），桌面是 undefined
      post: (cmd, extra) => post(Object.assign({ cmd, conversationId: id }, extra)),
      expanded: this.expanded,
      collapsed: this.collapsed,
      qstate: this.qstate,
      // 對話裡的影片帶 conversationId：播放器的「用預設程式開啟／在檔案總管顯示」宿主才認得出副本（WP4.6 步 10）；
      // 〔遠端〕那台的政策是 Off 時影片放不出來，播放器改說明只能看縮圖（步 11）
      openVideo: (m) => openVideo(Object.assign({ conversationId: id, errorText: this.videoErrorText() }, m)),
      videoErrorText: () => this.videoErrorText(),
      // 〔遠端〕瀏覽器版的檔案預覽（fileFetch → fileRef，D34）：沒送出回 false
      fetchFile: (path, name) => fetchFile(id, path, name),
      // 〔遠端〕指令結果的追蹤（cmdtrack）、送出與卡片的閘門（D14、D18）、卡片的送出中與結果不明（重新載入項目時照它畫）
      track: (cid, info) => track(cid, info),
      // 〔遠端〕卡片的回答送到了：輸入框下方亮一下往返時間（composer.showRtt）
      rtt: (ms) => { if (this.composer) this.composer.showRtt(ms); },
      gate: () => this.gate(),
      cardGate: () => this.cardGate(),
      nodeOf: (itemId) => { this.flushChunks(); return this.nodes.get(itemId) || null; },
      // texts：核准卡「告訴代理改怎麼做」與計畫卡「要調整的地方」打到一半的字（項目 id → 字；重新載入項目時照它還原）
      cards: { pending: new Set(), notes: new Map(), texts: new Map() },
      // 〔遠端〕Assistant 的「複製」：送 copyItem、等伺服器回 clipboard（D7、D42；結果由 markCopied 畫）；沒送出回 false
      copyItem: (itemId) => requestCopy(id, itemId),
      // 臨時對話的「朗讀」（quickchat.js）：桌面送 speakItem 讓宿主念，瀏覽器版從畫面上的回覆取文字自己念
      speakItem: (itemId, mdEl) => speakItem(id, itemId, mdEl),
      // 使用者訊息的「回溯」（rewind.js）：只有桌面這台的 Claude 對話、不在執行中
      rewind: isHub ? null : (itemId) => this.openRewindFor(itemId),
      // 子代理的即時用量（Header.subagents）：Agent 工具卡上的 token 晶片（items.paintAgentUsage）
      subagentUsage: (itemId) => (this.header && this.header.subagents && this.header.subagents[itemId]) || null,
      // 帳戶委派的工具卡「開啟」子對話（items.paintDelegation）
      openConversation: (cid) => this.ws.openConversation(cid),
    };
    this.build();
    views.set(id, this);
    // 〔同步〕電腦上下線、改名（remote-changed）與瀏覽器的連線狀態（hub-live）：裝置行、送出與卡片的閘門跟著更新
    this.offs = [on('remote-changed', () => this.refreshLive()), on('hub-live', () => this.refreshLive())];
  }

  /** 這個對話的來源與所在電腦（WP4.6；瀏覽器版每個對話都有電腦，桌面只有其他電腦的副本） */
  liveInfo() {
    const hd = this.header || summaryOf(this.id);
    const mode = isHub ? 'hub' : 'desktop';
    const origin = originOf(hd, mode);
    // 草稿標頭的 deviceName／deviceOnline 沒有意義（伺服器還沒有擁有者電腦）：只從 remote.devices 查，查不到是不明（閘門不擋）
    // 〔桌面遠端操作〕桌面草稿選了其他電腦（header.deviceId）時也查那台
    const remoteDraft = !isHub && origin === 'draft' && !!(hd && hd.deviceId);
    const device = isHub || origin === 'replica' || remoteDraft ? deviceOf(hd && hd.deviceId, state.remote, origin === 'draft' ? null : hd) : null;
    // 〔桌面遠端操作〕這台能不能對其他電腦下指令（liverules.peerOf）：副本照它決定唯讀或比照瀏覽器版
    const peer = peerOf(state.remote, mode);
    return { mode, origin, device, header: hd, peer, remoteDraft };
  }

  /** 〔桌面遠端操作〕要在其他電腦開的新對話（桌面草稿選了其他電腦） */
  isRemoteDraft() {
    return this.liveInfo().remoteDraft;
  }

  /** 〔桌面遠端操作〕這個草稿可以改開在哪些其他電腦（liverules.peerDraftDevices）；不能操作其他電腦時是空的 */
  peerDraftTargets() {
    if (isHub || peerOf(state.remote, 'desktop') !== 'live') return [];
    const r = state.remote;
    return peerDraftDevices({ accounts: state.accounts, devices: r && Array.isArray(r.devices) ? r.devices : [], ownDeviceId: r ? r.deviceId : null });
  }

  /**
   * 〔同步〕桌面上其他電腦的副本（D22）：不能換資料夾、瀏覽檔案、附加、刪除；桌面遠端操作能用時可以送出、回應卡片、改名、換模型與權限、
   * 換那台的工作資料夾、換成那台已登入的帳戶（2026-10-25）
   */
  isReplica() {
    return this.liveInfo().origin === 'replica';
  }

  /** 〔遠端〕對話在其他電腦上而且可以操作（瀏覽器版、桌面可以操作的副本）：資料夾選單、檔案瀏覽走遠端 */
  remoteLike() {
    if (isHub) return true;
    const { origin, peer } = this.liveInfo();
    return controllable(origin, peer);
  }

  /** 〔桌面遠端操作〕不能從這台操作的副本（同步沒連上、伺服器或這台不支援）：改名等也一起停用 */
  readOnlyReplica() {
    const { origin, peer } = this.liveInfo();
    return origin === 'replica' && !controllable(origin, peer);
  }

  /**
   * 多人協作：對話的設定（改名、帳戶、資料夾、刪除、延伸）只有擁有者能改——協作人只能對話與核准（擁有者 2026-10-30 的決定）；
   * 唯讀的副本也一樣（readOnlyReplica）
   */
  settingsLocked() {
    return isCollaborator() || this.readOnlyReplica();
  }

  /** 對話所在的那台正在執行（liverules.ownerRunning）：換工作資料夾的限制看它，副本看擁有者那邊的 */
  folderBusy() {
    return ownerRunning(this.header, this.liveInfo().origin);
  }

  /** 標頭的裝置行（「在 OFFICE-PC 執行・線上」）：這台的對話與草稿沒有 */
  renderDevice() {
    const hd = this.header;
    const { origin, device } = this.liveInfo();
    this.renderDeviceChip(origin, device);
    const line = hd ? deviceLine(origin, device, origin === 'replica' ? hd.replica : hd) : null;
    this.deviceEl.hidden = !line;
    if (!line) return;
    this.deviceEl.dataset.online = device.online === true ? 'true' : device.online === false ? 'false' : 'unknown';
    const text = this.deviceEl.lastChild;
    if (text.textContent !== line) text.textContent = line;
    this.deviceEl.title = line;
  }

  /** 〔遠端〕瀏覽器版草稿的「電腦：OFFICE-PC」晶片（D16）：讀 header.deviceId，名稱與在線從 remote.devices 查；點了換電腦 */
  renderDeviceChip(origin, device) {
    if (!isHub) { this.renderPeerDeviceChip(origin, device); return; }
    const show = isHub && origin === 'draft' && !!device && !!device.id;
    this.devChip.hidden = !show;
    if (!show) return;
    const online = device.online === true ? 'true' : device.online === false ? 'false' : 'unknown';
    if (this.devChip.dataset.online !== online) this.devChip.dataset.online = online;
    const label = this.devChip.querySelector('.chip-label');
    const text = '電腦：' + device.name;
    if (label.textContent !== text) label.textContent = text;
    this.devChip.title = '新對話會開在 ' + device.name + offlineNote(device) + '；點一下換電腦';
  }

  /**
   * 〔桌面遠端操作〕桌面草稿的「電腦：這台／OFFICE-PC」晶片：能操作其他電腦而且有其他電腦可以開（或已經選了其他電腦）時才顯示
   */
  renderPeerDeviceChip(origin, device) {
    const hd = this.header;
    const remote = !!(hd && hd.deviceId);
    const show = origin === 'draft' && (remote || this.peerDraftTargets().length > 0);
    this.devChip.hidden = !show;
    if (!show) return;
    const online = !remote ? 'true' : device && device.online === true ? 'true' : device && device.online === false ? 'false' : 'unknown';
    if (this.devChip.dataset.online !== online) this.devChip.dataset.online = online;
    const label = this.devChip.querySelector('.chip-label');
    const name = remote ? (device ? device.name : '其他電腦') : '這台';
    const text = '電腦：' + name;
    if (label.textContent !== text) label.textContent = text;
    this.devChip.title = (remote ? '新對話會開在 ' + name + offlineNote(device) : '新對話會開在這台') + '；點一下換電腦';
  }

  /** 〔桌面遠端操作〕桌面草稿換電腦：這台＋可以開的其他電腦（換過去時沿用目前的帳戶，那台沒有就用那台的第一個） */
  openPeerDeviceMenu() {
    const hd = this.header;
    if (!hd || !hd.isDraft) return;
    const targets = this.peerDraftTargets();
    const items = [{ label: '這台電腦', checked: !hd.deviceId, onSelect: () => { if (hd.deviceId) post({ cmd: 'draftSetDevice', conversationId: this.id, deviceId: null }); } }];
    if (targets.length) items.push({ type: 'sep' }, { type: 'header', label: '在其他電腦開（經伺服器）' });
    for (const t of targets) {
      items.push({
        label: t.name, checked: t.deviceId === hd.deviceId, desc: t.accounts.map((a) => a.label).join('、'),
        onSelect: () => {
          const a = peerDraftAccount(t, hd.accountId);
          if (a && (t.deviceId !== hd.deviceId || a.shownId !== hd.accountId)) post({ cmd: 'draftSetDevice', conversationId: this.id, deviceId: t.deviceId, accountId: a.id, provider: a.provider });
        },
      });
    }
    if (!targets.length) items.push({ label: '沒有其他可以開新對話的電腦（要在線、允許遠端操作、登入了帳戶）', disabled: true });
    openMenu(this.devChip, items, { minWidth: 260 });
  }

  /** 〔桌面遠端操作〕遠端草稿換帳戶：那台可以用的帳戶 */
  openPeerAccountMenu(anchor) {
    const hd = this.header;
    const t = this.peerDraftTargets().find((x) => x.deviceId === hd.deviceId);
    const items = t ? t.accounts.map((a) => ({
      label: a.label, color: safeColor(a.color, a.provider), checked: a.shownId === hd.accountId, desc: a.provider,
      onSelect: () => { if (a.shownId !== hd.accountId) post({ cmd: 'draftSetDevice', conversationId: this.id, deviceId: t.deviceId, accountId: a.id, provider: a.provider }); },
    })) : [];
    if (!items.length) items.push({ label: '那台現在沒有可以用的帳戶', disabled: true });
    openMenu(anchor || this.acctChip, items, { minWidth: 260 });
  }

  /** 草稿換電腦的選單：這個帳戶可以開新對話的電腦（目前的打勾）；沒有候選時說明原因 */
  openDeviceMenu() {
    if (!isHub) { this.openPeerDeviceMenu(); return; }
    const hd = this.header;
    if (!hd || !hd.isDraft) return;
    const r = deviceChoices(hd.accountId, hd.deviceId);
    const items = r.choices.map((c) => ({
      label: c.name, checked: c.deviceId === hd.deviceId,
      onSelect: () => { if (c.deviceId !== hd.deviceId) this.ws.switchDraftDevice(this.id, c.deviceId); },
    }));
    if (!items.length) items.push({ label: r.reason || '沒有其他可以開新對話的電腦', disabled: true });
    else if (!r.choices.some((c) => c.deviceId === hd.deviceId)) items.unshift({ type: 'header', label: '目前的電腦不能開新對話，換一台：' });
    openMenu(this.devChip, items, { minWidth: 240 });
  }

  /** 送出的閘門（liverules.composerGate，D14）：{canSend, readOnly, reason} */
  gate() {
    const { mode, origin, device, header, peer, remoteDraft } = this.liveInfo();
    // 〔遠端〕網頁上傳還沒加進輸入區（D32）：送出要等（輸入區建好之前還沒有上傳）
    return composerGate({ mode, origin, header, device, live: hubLive(), uploading: !!this.composer && this.composer.uploading(), peer, remoteDraft });
  }

  /** 附加檔案的閘門（liverules.attachGate，D32）：瀏覽器版的草稿、沒連線、電腦離線、政策；{enabled, reason} */
  attachGate() {
    const { mode, origin, device, peer, remoteDraft } = this.liveInfo();
    return attachGate({ mode, origin, device, live: hubLive(), peer, remoteDraft });
  }

  /** 「瀏覽資料夾…」的閘門（liverules.folderPickerGate，D33）：{shown, enabled, reason} */
  folderPicker() {
    const { mode, origin, device, header, peer } = this.liveInfo();
    return folderPickerGate({ mode, origin, header, device, live: hubLive(), peer });
  }

  /** 〔遠端〕其他電腦的對話（瀏覽器版、桌面的副本）的帳戶選單：只列那台已登入的帳戶（liverules.remoteAccountMenu）；這台的對話與草稿 remote 是 false */
  remoteAccounts() {
    const { mode, origin, device, header, peer } = this.liveInfo();
    return remoteAccountMenu({ mode, origin, header, device, live: hubLive(), peer, accounts: state.accounts });
  }

  /** 〔遠端〕影片載入失敗時的說明：那台的政策是 Off（或暫停）時只能看縮圖；其他 null */
  videoErrorText() {
    return isHub ? mediaPolicyNote(this.liveInfo().device) : null;
  }

  /** 卡片的閘門（liverules.cardGate，D18）：{enabled, reason} */
  cardGate() {
    const { mode, origin, device, peer } = this.liveInfo();
    return cardGate({ mode, origin, device, live: hubLive(), peer });
  }

  /** remote-changed／hub-live 之後：裝置行、草稿的電腦晶片、帳戶晶片（副本能不能從這台換帳戶）、送出與卡片的閘門 */
  refreshLive() {
    if (this.header) {
      this.renderDevice();
      this.refreshAccountChip();
    }
    this.refreshGates();
  }

  refreshGates() {
    this.composer.updateSend();
    this.refreshCardGates(true);
  }

  /**
   * 已建好的卡片重套閘門：force 之外只在閘門改變時才掃節點（執行中的標頭約每 50ms 更新一次）。
   * load() 先建項目才套標頭，對話不在清單裡時建卡片的那一刻還不知道電腦（合併前審查 WP46-XSS-RO-1），所以 setHeader 也要呼叫
   */
  refreshCardGates(force) {
    const g = this.cardGate();
    const key = g.enabled + '|' + (g.reason || '');
    if (!force && key === this.cardGateKey) return;
    this.cardGateKey = key;
    for (const node of this.nodes.values()) if (node.classList.contains('it-card')) applyCardGate(node, g);
  }

  build() {
    this.root = h('section', { class: 'conv loading', dataset: { conv: this.id }, hidden: true });
    // 宿主回 conversation 之前（讀檔、組裝較慢時）：標題與帳戶色先用對話清單的摘要（和分頁一致），
    // 還沒有內容的晶片與輸入列選項先藏起來，訊息區顯示載入中
    const sum = summaryOf(this.id);
    if (sum) { const acc = accountOf(sum.accountId); setAcc(this.root, acc ? acc.color : null, sum.provider); }
    // 標題列
    // 自動重新命名產生標題期間（header.titling）標題後面轉圈；右鍵選單有重新命名與自動重新命名
    this.titleSpin = spinner('ch-title-spin');
    this.titleSpin.hidden = true;
    // 標題放不下時是跑馬燈（fitHeader）：.ch-title-text 是裁切的框，文字在裡面的 .ch-title-run（捲動的是它）
    this.titleRun = h('span', { class: 'ch-title-run', text: (sum && sum.title) || '載入中…' });
    this.titleText = h('span', { class: 'ch-title-text' }, this.titleRun);
    this.titleEl = h('div', { class: 'ch-title', title: '雙擊重新命名' }, this.titleText, this.titleSpin);
    this.titleEl.addEventListener('dblclick', () => this.startRename());
    this.titleEl.addEventListener('contextmenu', (e) => { e.preventDefault(); this.openTitleMenu({ x: e.clientX, y: e.clientY }); });
    // 標題後面的自動重新命名圖示：點了直接產生標題（只有能自動重新命名的對話，canAutoTitle；產生期間藏起來、改成轉圈）
    this.autoTitleBtn = iconButton('autoTitle', AUTO_TITLE_HINT, () => this.autoTitle(), 'sm ch-title-auto');
    this.autoTitleBtn.dataset.field = 'autoTitle';
    this.autoTitleBtn.hidden = true;
    this.acctChip = h('button', { type: 'button', class: 'chip ch-acct', 'aria-haspopup': 'menu' }, h('span', { class: 'chip-dot' }), h('span', { class: 'chip-label' }),
      // 執行中選了另一個帳戶、還沒換過去（header.pendingAccount）：時鐘＋要換過去的帳戶
      h('span', { class: 'chip-next', hidden: true }, icon('clock', 'chip-next-ic'), h('span', { class: 'chip-next-label' })), icon('chevDown', 'chip-chev'));
    this.acctChip.addEventListener('click', () => this.openAccountMenu());
    this.folderChip = h('button', { type: 'button', class: 'chip ch-folder', 'aria-haspopup': 'menu' }, icon('folder', 'chip-ic'), h('span', { class: 'chip-label' }), icon('chevDown', 'chip-chev'));
    this.folderChip.addEventListener('click', () => this.openFolderMenu());
    // 工作資料夾的 git 狀態（宿主的 gitStatus；只有桌面這台的對話）：分支＋徽章（未提交、未推送、未拉取、衝突…），點了看明細或重新檢查
    this.git = null;
    this.gitRendered = null;
    this.gitChip = h('button', { type: 'button', class: 'chip ch-git', 'aria-haspopup': 'menu', hidden: true, dataset: { field: 'git' } },
      icon('gitBranch', 'chip-ic'), h('span', { class: 'chip-label' }), h('span', { class: 'git-badges' }), icon('chevDown', 'chip-chev'));
    this.gitChip.addEventListener('click', () => this.openGitMenu());
    // 代理另開、脫離 CLI 還在跑的程式（header.programs；只有桌面這台的對話）：點了列出、可以停止
    this.progSig = '';
    this.progChip = h('button', { type: 'button', class: 'chip ch-prog', 'aria-haspopup': 'menu', hidden: true, dataset: { field: 'programs' } },
      icon('terminal', 'chip-ic'), h('span', { class: 'prog-leds', 'aria-hidden': 'true' }), h('span', { class: 'chip-label' }), icon('chevDown', 'chip-chev'));
    this.progChip.addEventListener('click', () => this.openProgramsMenu());
    // Claude 進行中的目標（header.goal；只有桌面這台的對話）：點了看完整的條件、清除
    this.goalChip = h('button', { type: 'button', class: 'chip ch-goal', 'aria-haspopup': 'menu', hidden: true, dataset: { field: 'goal' } },
      icon('checklist', 'chip-ic'), h('span', { class: 'chip-label' }), icon('chevDown', 'chip-chev'));
    this.goalChip.addEventListener('click', () => this.openGoalMenu());
    // 第十批：委派的子對話、延伸出來的對話寫來源（liverules.originLink，清單的 parentId／forkedFrom）：點了開啟主對話或原對話
    this.originSig = '';
    this.origin = null;
    this.originChip = h('button', { type: 'button', class: 'chip ch-origin', hidden: true, dataset: { field: 'origin' } });
    this.originChip.addEventListener('click', () => { const o = this.origin; if (o && o.open) this.ws.openConversation(o.id); });
    this.progIds = new Set();     // 上一次畫的程式 id（消失的、不是自己按停止的＝自己結束了，晶片閃一下琥珀色）
    this.progStopped = new Set(); // 在選單按了停止的程式 id
    this.drawerBtn = iconButton('panelRight', '檔案瀏覽器', () => this.toggleDrawer(), 'ch-drawer');
    this.popBtn = iconButton('popout', '在新視窗開啟', () => post({ cmd: 'popout', conversationId: this.id }), 'ch-pop');
    this.moreBtn = iconButton('more', '更多動作', () => this.openMoreMenu(), 'ch-more');
    // 放大／還原這個窗格（workspace.js 的 togglePaneZoom 接點擊）；兩個圖示依 .pane.zoomed 顯示其中一個，只有一個窗格時不顯示（chat.css）
    this.zoomBtn = iconButton('paneExpand', '放大或還原這個窗格', null, 'ch-zoom');
    this.zoomBtn.dataset.field = 'paneZoom';
    this.zoomBtn.append(icon('paneRestore'));
    // 〔遠端〕瀏覽器版草稿開在哪台電腦（D16）：點了換電腦
    this.devChip = h('button', { type: 'button', class: 'chip ch-devchip', 'aria-haspopup': 'menu', hidden: true, dataset: { field: 'draftDevice' } },
      h('span', { class: 'ch-dev-dot', 'aria-hidden': 'true' }), h('span', { class: 'chip-label' }), icon('chevDown', 'chip-chev'));
    this.devChip.addEventListener('click', () => this.openDeviceMenu());
    // 多人協作：分享給協作人（擁有者看；collaborators 改了就更新）
    this.collabChip = h('button', { type: 'button', class: 'chip ch-collab', hidden: true, dataset: { field: 'collab' } }, icon('user', 'chip-ic'), h('span', { class: 'chip-label' }));
    this.collabChip.addEventListener('click', () => this.openShare());
    if (this.offCollab) this.offCollab();
    this.offCollab = on('collaborators-changed', () => this.renderCollabChip());
    this.chipsEl = h('div', { class: 'ch-chips', hidden: true }, this.acctChip, this.folderChip, this.gitChip, this.goalChip, this.originChip, this.progChip, this.devChip, this.collabChip); // setHeader 填好內容才顯示
    // 〔同步〕所在電腦（瀏覽器版每個對話、桌面其他電腦的副本）：在線小點＋「在 OFFICE-PC 執行・線上」（textContent）
    this.deviceEl = h('span', { class: 'ch-dev', hidden: true }, h('span', { class: 'ch-dev-dot', 'aria-hidden': 'true' }), h('span', { class: 'ch-dev-text' }));
    // 〔同步〕伺服器刪除了這個本機對話（header.conversationSync.serverDeleted，D25）：之後只能留在這台
    this.syncDelEl = h('span', { class: 'tag ch-sync-del', hidden: true, dataset: { field: 'serverDeleted' }, text: '已從伺服器刪除',
      title: '伺服器上的這個對話已刪除，網頁與其他電腦都看不到；這台的對話照常可以用' });
    // 兩行：第一行標題與右邊的按鈕，第二行帳戶、工作資料夾、git 晶片、執行列（下面建好才插進來）與所在電腦（擠在同一行時標題只剩幾個字）
    this.head = h('header', { class: 'ch' },
      h('div', { class: 'ch-top' }, this.titleEl, this.autoTitleBtn, h('div', { class: 'grow' }), this.drawerBtn, this.popBtn, this.zoomBtn, this.moreBtn),
      h('div', { class: 'ch-sub' }, this.chipsEl, this.deviceEl, this.syncDelEl));
    if (isFlashing(this.id)) this.head.classList.add('attn-flash'); // 開分頁之前就在閃（要你回來看，attnflash.js）：標頭跟著閃
    this.warn = h('div', { class: 'ch-warn', hidden: true }, icon('warning'), h('span', { class: 'ch-warn-text', text: '這個對話使用的帳戶已被移除，換一個帳戶才能繼續。' }),
      h('button', { type: 'button', class: 'btn sm', text: '更換帳戶', onClick: () => this.openAccountMenu(this.warn.querySelector('.btn')) }));
    this.buildRestartBar();

    // 訊息串
    this.olderBtn = h('button', { type: 'button', class: 'older-btn' }, h('span', { text: '載入較早的訊息' }));
    this.olderBtn.addEventListener('click', () => { if (this.clearHiding()) this.revealCleared(); else this.loadOlder(); });
    this.olderRow = h('div', { class: 'older', hidden: true }, this.olderBtn);
    this.empty = h('div', { class: 'draft', hidden: true });
    this.itemsEl = h('div', { class: 'items' });
    this.runText = h('span', { class: 'run-text' });
    this.runTime = h('span', { class: 'run-time' });
    this.runSpin = spinner('run-spin');
    // 等背景工作回報時取代轉圈的虛線圈（和執行中的實心轉圈分得出來）
    this.runBg = h('span', { class: 'bg-ring run-bg', hidden: true, 'aria-hidden': 'true' });
    // 等背景工作回報時的「詳細」（taskDetail，2026-10-25）：這個背景工作在做什麼、指令、目前的輸出，裡面也能停止它
    this.runDetail = h('button', { type: 'button', class: 'btn sm run-detail', hidden: true, text: '詳細', title: '這個背景工作在做什麼、目前的輸出', dataset: { field: 'taskDetail' } });
    this.runDetail.addEventListener('click', () => this.openTaskDetails());
    // 等背景工作回報時強制停止背景工作（taskStop，2026-10-25；liverules.backgroundStopMenu）：選單裡選停哪一個或全部
    this.runStop = h('button', { type: 'button', class: 'btn sm run-stop', hidden: true, title: '強制停止背景工作', dataset: { field: 'taskStop' } }, icon('stop'), h('span', { text: '停止' }));
    this.runStop.addEventListener('click', () => this.openTaskStop());
    // 執行中把前景的指令與子代理移到背景（backgroundTasks，2026-11-11；同終端機的 Ctrl+B）：標頭帶 canBackground 才有，只有桌面這台的對話
    this.runToBg = h('button', { type: 'button', class: 'btn sm run-tobg', hidden: true,
      title: '把執行中的指令或子代理移到背景繼續跑，這一輪接著往下做（同終端機的 Ctrl+B）', dataset: { field: 'backgroundTasks' } },
    icon('arrowDown'), h('span', { text: '移到背景' }));
    this.runToBg.addEventListener('click', () => {
      post({ cmd: 'backgroundTasks', conversationId: this.id });
      this.runToBg.disabled = true;
      setTimeout(() => { this.runToBg.disabled = false; }, 5000);
    });
    this.runEl = h('div', { class: 'runline', hidden: true, role: 'status' }, this.runSpin, this.runBg, this.runText, this.runTime, this.runToBg, this.runDetail, this.runStop);
    // 載入中：很快就回來時不閃一下，稍等才出現；load() 移除
    // 第十一批：狀態列（轉圈、說明、自動重試用了幾次的 3 格）＋底下幾塊靜態的訊息骨架（點狀網格與四角括號，同圖片的佔位；不做流光）
    this.loadText = h('span', { class: 'run-text', text: '載入對話中…' });
    this.loadTries = h('span', { class: 'load-tries', 'aria-hidden': 'true' }, ...LOAD_RETRY_MS.map(() => h('i')));
    this.loadLine = h('div', { class: 'runline load-line' }, spinner('run-spin'), this.loadText, this.loadTries);
    this.loadingEl = h('div', { class: 'load-wrap', hidden: true, role: 'status' }, this.loadLine,
      h('div', { class: 'load-skel', 'aria-hidden': 'true' }, h('div', { class: 'sk sk-user' }), h('div', { class: 'sk sk-reply' }), h('div', { class: 'sk sk-reply sk-short' })));
    this.loadingTimer = setTimeout(() => { if (this.loadingEl) this.loadingEl.hidden = false; }, 200);
    // 載入看門狗（2026-11-07 使用者回報：分頁一直「載入對話中…」）：看得到卻一直沒收到 conversation 就自動重送 open，用完改成「重試」鈕
    this.loadAttempt = 0;
    this.loadWatch = 0;
    // 送出中的訊息另放一層、排在所有項目之後：等待期間代理新增的項目（插入這一輪時）不會排到它後面
    this.sendingEl = h('div', { class: 'sending-list' });
    this.thread = h('div', { class: 'thread' }, this.olderRow, this.loadingEl, this.empty, this.itemsEl, this.sendingEl, this.runEl);
    this.buildPromptBar();
    this.scroller = h('div', { class: 'scroller' }, this.thread);
    this.jumpLabel = h('span', { text: '最新' });
    this.jumpBtn = h('button', { type: 'button', class: 'jump', hidden: true }, icon('arrowDown'), this.jumpLabel);
    this.jumpBtn.addEventListener('click', () => this.stickToBottom());
    this.composer = new Composer(this);
    this.composer.el.prepend(this.jumpBtn); // 浮在輸入區上方
    // 一輪的結算單（fxrules.turnSummary）：一輪結束時浮在輸入區上方約 4 秒
    this.turnSumEl = h('div', { class: 'turn-sum', role: 'status', hidden: true });
    this.composer.el.prepend(this.turnSumEl);
    // 〔遠端〕伺服器換版、網頁自動重新載入之前沒送出的字（liveview.takeReloadDraft）：填回輸入框；
    // 伺服器草稿另記帳戶與電腦（標頭還沒到、新版的伺服器已經沒有這個草稿時，workspace 照它們重開）
    const saved = takeReloadDraft(this.id);
    this.reloadHeader = saved && (saved.accountId || saved.deviceId) ? { accountId: saved.accountId, deviceId: saved.deviceId } : null;
    if (saved) this.composer.restoreText(saved.text);
    // 執行列（宿主的 launchStatus；只有桌面這台的對話）：工作資料夾是視窗程式專案時，標題列第二行 git 晶片後面的啟動、暫停、停止、
    // 重新建置並執行；輸出面板在輸入區最上方展開
    this.launchBar = new LaunchBar(this);
    this.chipsEl.after(this.launchBar.el);
    this.composer.queueEl.before(this.launchBar.outEl);
    // 權限、模型、推理按鈕要等 header 才有內容（renderToolbar 會依 header 決定顯示與否）
    for (const b of [this.composer.permBtn, this.composer.modelBtn, this.composer.effortBtn]) b.hidden = true;
    this.col = h('div', { class: 'conv-col' }, this.promptEl, this.scroller, this.composer.el);
    // 對話小地圖（minimap.js）：捲動軸左邊的細軌，標出你的訊息、錯誤、等你回應的卡片、改了檔案的工具
    this.minimap = new Minimap(this);
    // 捲動位置讀數（捲動停下時在小地圖旁浮出「第 3／12 輪」，fxrules.turnPosition）
    this.posEl = h('div', { class: 'conv-pos', hidden: true, 'aria-hidden': 'true' });
    this.col.append(this.minimap.el, this.posEl);
    this.main = h('div', { class: 'conv-main' }, this.col);
    // 拖放的讀數（fxrules.dropSummary）：拖曳中瀏覽器只給每個檔案的類型與個數（不給檔名），照它寫「放開以附加 3 個檔案」與「圖片 2・文件 1」
    this.dropText = h('span', { class: 'drop-text', text: '放開以附加到這個對話' });
    this.dropSum = h('span', { class: 'drop-sum', hidden: true });
    this.dropEl = h('div', { class: 'drop', hidden: true }, h('div', { class: 'drop-box' }, icon('paperclip'), h('span', { class: 'drop-lines' }, this.dropText, this.dropSum)));
    this.root.append(this.head, this.restartEl, this.warn, this.main, this.dropEl);

    // 捲動：使用者往上捲就停止自動捲動
    this.scroller.addEventListener('scroll', () => this.onScroll(), { passive: true });
    this.scroller.addEventListener('scrollend', () => this.onScrollEnd(), { passive: true });
    this.scroller.addEventListener('wheel', (e) => { if (e.deltaY < 0 && this.canScroll()) this.setStick(false); }, { passive: true });
    this.scroller.addEventListener('keydown', (e) => { if (e.key === 'PageUp' || e.key === 'Home' || e.key === 'ArrowUp') this.setStick(false); });
    // 觸控（手機、平板）：手指往下拖就停止自動貼底（同滾輪往上；不然串流每長一次就被 ResizeObserver 拉回最底、和手指搶）；
    // 手指按著與放開後的慣性捲動期間，較早的項目先不插進最前面（liverules.touchScrolling）
    this.touch = { touching: false, startY: null, touchEndAt: null, lastScrollAt: null };
    this.scroller.addEventListener('touchstart', (e) => this.onTouch(e, 'start'), { passive: true });
    this.scroller.addEventListener('touchmove', (e) => this.onTouch(e, 'move'), { passive: true });
    this.scroller.addEventListener('touchend', (e) => this.onTouch(e, 'end'), { passive: true });
    this.scroller.addEventListener('touchcancel', (e) => this.onTouch(e, 'end'), { passive: true });
    // 沒有捲動錨定的瀏覽器（iOS Safari 26 以前）：上方的項目變高（延遲載入的圖片、圖表）時自己補，看著的內容不跳（scrollanchor.js）
    this.anchor = new ScrollAnchor(this.scroller, this.itemsEl, () => !this.stick && this.visible);
    // 內容長高（串流、圖片載入、展開、執行列的輸出打開）或窗格變矮（分割、抽屜）時，貼底的對話維持在最底。
    // ResizeObserver 在瀏覽器排完版之後才呼叫，這時讀 scrollHeight 不必再排一次版。串流的更新另由 app.js 提早套用後先貼好
    // （pinTarget／pinTo：在影格之前捲，捲動事件才不用多等一格），到這裡已經在最底、什麼都不做；影格先到的那幾次照舊靠它
    this.resizeObs = new ResizeObserver(() => { if (this.stick && this.visible) this.scrollToBottom(); this.minimap.onLayout(); });
    this.resizeObs.observe(this.thread);
    this.resizeObs.observe(this.scroller);
    // 窗格變大變小、標題的框變寬變窄（分割、顯示出來）時，重新決定標題列分不分兩行與跑馬燈
    this.headObs = new ResizeObserver(() => this.scheduleFitHeader());
    this.headObs.observe(this.root);
    this.headObs.observe(this.titleText);
    this.olderObs = new IntersectionObserver((ents) => {
      if (ents.some((e) => e.isIntersecting) && this.hasMore && !this.loadingOlder && this.visible && this.loaded && this.userScrolled) this.loadOlder();
    }, { root: this.scroller, rootMargin: '200px 0px 0px 0px' });
    this.olderObs.observe(this.olderRow);

    // 圖片燈箱（對話內所有圖片，依畫面順序）
    this.root.addEventListener('click', (e) => {
      const t = e.target.closest('[data-lb-url]');
      if (!t || !this.root.contains(t)) return;
      e.preventDefault();
      this.flushChunks(); // 分批載入還沒補完時，燈箱的清單也要含較早的圖片
      const scope = this.composer.el.contains(t) ? this.composer.el : this.thread;
      const list = Array.from(scope.querySelectorAll('[data-lb-url]'));
      openLightbox(list.map((n) => ({ url: n.dataset.lbUrl, name: n.dataset.lbName || '', path: n.dataset.lbPath || null, conversationId: this.id })), Math.max(0, list.indexOf(t)), t);
    });
    // 圖片與影片上按右鍵或觸控長按（app.js 的 longPressAsContextMenu 轉成 contextmenu；對話裡的影片在 iframe 裡，framedvideo.js 轉出來）：
    // 媒體選單（複製、另存新檔、用預設程式開啟、在檔案總管顯示）
    this.root.addEventListener('contextmenu', (e) => {
      const m = mediaOfTarget(e.target);
      if (!m) return;
      e.preventDefault();
      openMediaMenu(Object.assign(m, { conversationId: this.id }), { x: e.clientX, y: e.clientY });
    });

    // 拖放檔案到窗格＝附件（分頁拖曳由 workspace 處理）。
    // 不用 dragenter／dragleave 計數：串流時滑鼠下的元素每 50ms 被換掉，Chromium 把 dragleave 送給已脫離文件的舊節點（不會冒泡到窗格），
    // 計數只增不減，遮罩就一直蓋著對話。改成：遮罩顯示後自己接住拖放事件（.drop 不設 pointer-events: none），拖放目標不再是會被換掉的訊息；
    // dragleave 的 relatedTarget 不在窗格內（拖到窗格外；拖出視窗或按 Esc 取消時是 null）才收起；dragover 停了也收起（最後一道保險）。
    const isFiles = (e) => e.dataTransfer && Array.from(e.dataTransfer.types || []).includes('Files');
    let dropIdle = 0;
    let dropKey = '';
    const hideDrop = () => { clearTimeout(dropIdle); dropIdle = 0; this.dropEl.hidden = true; dropKey = ''; };
    const showDrop = (e) => {
      const opening = this.dropEl.hidden;
      this.dropEl.hidden = false;
      const types = Array.from((e.dataTransfer && e.dataTransfer.items) || []).filter((it) => it.kind === 'file').map((it) => it.type);
      const sum = dropSummary(types);
      const key = sum.count + '|' + sum.label;
      if (key !== dropKey) {
        dropKey = key;
        this.dropText.textContent = sum.count ? '放開以附加 ' + sum.count + ' 個檔案' : '放開以附加到這個對話';
        this.dropSum.textContent = sum.label;
        this.dropSum.hidden = !sum.label;
      }
      if (opening) replayClass(this.dropEl, 'fx-drop-lock', DROP_LOCK_FX_MS);
      clearTimeout(dropIdle);
      dropIdle = setTimeout(hideDrop, DROP_IDLE_MS);
    };
    // 〔同步〕副本不能附加：不顯示遮罩、放開也不加（app.js 全域的 drop 擋掉瀏覽器開檔）
    this.root.addEventListener('dragenter', (e) => { if (!isFiles(e) || this.isReplica()) return; e.preventDefault(); showDrop(e); });
    this.root.addEventListener('dragover', (e) => { if (!isFiles(e) || this.isReplica()) return; e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; showDrop(e); });
    this.root.addEventListener('dragleave', (e) => { if (isFiles(e) && !(e.relatedTarget && this.root.contains(e.relatedTarget))) hideDrop(); });
    this.root.addEventListener('drop', (e) => {
      if (!isFiles(e)) return;
      e.preventDefault();
      hideDrop();
      if (this.isReplica()) return;
      const files = e.dataTransfer.files;
      if (files && files.length) this.composer.addFiles(files);
    });
  }

  // ───── 顯示／隱藏 ─────
  show() {
    if (this.visible) return;
    this.root.hidden = false;
    this.visible = true;
    replayClass(this.root, 'fx-show', SHOW_FX_MS); // 切到這個分頁：內容淡入
    if (this.stick) this.scrollToBottom();
    else if (this.savedTop !== null) this.scroller.scrollTop = this.savedTop;
    this.minimap.request();
    this.tick(Date.now());
    this.scheduleLazy(); // 在背景載入的分頁：切過來才把先不排版的項目換回實際高度
    this.armLoadWatch();
  }

  /** 看得到、還沒載入完成：排下一次檢查（liverules.loadRetryDelay）；已經排著、載好了、看不到時不排（切回來的 show 再排） */
  armLoadWatch() {
    if (this.loaded || this.loadWatch || !this.visible || !this.loadingEl) return;
    const delay = loadRetryDelay(this.loadAttempt);
    if (delay == null) { this.showLoadFailed(); return; }
    this.loadWatch = setTimeout(() => {
      this.loadWatch = 0;
      if (this.loaded || !this.loadingEl) return;
      if (!this.visible) return; // 看不到的時間不算：切回來再從這一次接著等
      this.loadAttempt++;
      this.markLoadTry();
      const st = this.ws.retryOpen ? this.ws.retryOpen(this.id) : null;
      reportIssue('對話一直沒有載入完成，第 ' + this.loadAttempt + ' 次重送 open（' + this.id + '）', st ? JSON.stringify(st) : null);
      this.armLoadWatch();
    }, delay);
  }

  /**
   * 收到省略清單的標頭、這個對話卻沒有上一份清單（liverules.missingHeaderLists）：重送 open 拿整份快照（每 HEADER_LISTS_REOPEN_MS 最多一次），
   * 並回報當時的狀態，找出是哪一條路讓兩邊對不上
   */
  recoverHeaderLists(missing, hd) {
    const now = Date.now();
    if (this.listsReopenAt && now - this.listsReopenAt < HEADER_LISTS_REOPEN_MS) return;
    this.listsReopenAt = now;
    const st = this.ws.retryOpen ? this.ws.retryOpen(this.id) : null;
    reportIssue('標頭省略了清單，但這個對話沒有上一份清單（' + this.id + '）', JSON.stringify({
      missing, loaded: this.loaded, hadHeader: !!this.header, provider: hd && hd.provider, running: !!(hd && hd.running), visible: this.visible, open: st,
    }));
  }

  /** 自動重送了一次（第十一批）：說明寫第幾次、重試的格子亮一格 */
  markLoadTry() {
    if (!this.loadText) return;
    this.loadText.textContent = this.loadAttempt > 0 ? '載入對話中…（自動重試第 ' + this.loadAttempt + ' 次）' : '載入對話中…';
    Array.from(this.loadTries.children).forEach((c, i) => c.classList.toggle('on', i < this.loadAttempt));
  }

  /** 自動重送都用完了：轉圈換成說明與「重試」鈕（按了重新開始計次） */
  showLoadFailed() {
    const el = this.loadingEl;
    if (!el || el.classList.contains('load-failed')) return;
    el.hidden = false;
    el.classList.add('load-failed');
    const retry = h('button', { type: 'button', class: 'btn sm', text: '重試', dataset: { field: 'loadRetry' } });
    retry.addEventListener('click', () => {
      el.classList.remove('load-failed');
      this.loadAttempt = 0;
      this.markLoadTry();
      this.loadLine.replaceChildren(spinner('run-spin'), this.loadText, this.loadTries);
      if (this.ws.retryOpen) this.ws.retryOpen(this.id);
      this.armLoadWatch();
    });
    this.loadLine.replaceChildren(icon('info'), h('span', { class: 'run-text', text: '這個對話一直沒有載入完成' }), this.loadTries, retry);
  }

  hide() {
    if (!this.visible) return;
    this.stopPeek();
    this.savedTop = this.scroller.scrollTop;
    this.root.hidden = true;
    this.visible = false;
    // 隱藏（display: none）不會讓影片停下來：切到別的分頁或儀表板時暫停回覆裡正在播的影片，免得在看不到的地方繼續出聲
    pauseFramedVideos(this.root);
  }

  destroy() {
    if (this.offCollab) { this.offCollab(); this.offCollab = null; }
    if (views.get(this.id) === this) views.delete(this.id);
    for (const off of this.offs) off();
    clearTimeout(this.loadingTimer);
    clearTimeout(this.loadWatch);
    clearTimeout(this.flashTimer);
    clearTimeout(this.posTimer);
    clearTimeout(this.turnSumTimer);
    cancelAnimationFrame(this.turnEndRaf);
    cancelAnimationFrame(this.olderRaf);
    this.anchor.destroy();
    this.cancelChunks();
    this.cancelLazy();
    for (const s of this.sending) clearTimeout(s.timer);
    this.sending = [];
    this.resizeObs.disconnect();
    this.headObs.disconnect();
    cancelAnimationFrame(this.fitHeaderFrame);
    this.olderObs.disconnect();
    this.minimap.destroy();
    this.composer.destroy();
    this.launchBar.destroy();
    this.root.remove();
  }

  focusInput() { this.composer.focus(); }

  /**
   * 一輪結束（執行中→停下）：輸入框的燈閃一下（fxrules.turnEndTone 決定綠、紅或不閃）。
   * 下一個影格才看最後一個項目：錯誤項目可能和標頭分開、稍後才到；那時已經開始下一輪（佇列接著送出）就不閃
   */
  scheduleTurnEnd() {
    cancelAnimationFrame(this.turnEndRaf);
    this.turnEndRaf = requestAnimationFrame(() => {
      this.turnEndRaf = 0;
      if (this.root.classList.contains('running')) return;
      const last = this.order.length ? this.items.get(this.order[this.order.length - 1]) : null;
      const tone = turnEndTone(last && last.kind, this.composer.stopAt, Date.now());
      this.showTurnSummary(tone);
      // 背景工作還在跑：還沒真的完成，不播「完成」的餘暉（失敗照播）
      if (tone === 'ok' && this.root.classList.contains('bg-wait')) return;
      if (tone) replayClass(this.root, tone === 'err' ? 'fx-turn-err' : 'fx-turn-ok', TURN_FX_MS);
      // 這個對話現在看不到（背景分頁、另一個窗格藏著）：分頁本身閃一下綠或紅（workspace.js 的 turn-end）
      if (tone && !this.visible) emit('turn-end', { id: this.id, tone });
    });
  }

  /**
   * 一輪的結算單（第七批視覺效果）：花了多久、工具幾次、改了幾個檔案、token、首次回應（fxrules.turnSummary），
   * 浮在輸入區上方約 4 秒後淡出；看不到的對話不畫（背景分頁有自己的閃燈）。開始下一輪時收起來
   */
  showTurnSummary(tone) {
    const startedAt = this.turnStartedAt;
    this.turnStartedAt = null;
    if (!this.visible) return;
    const bg = this.root.classList.contains('bg-wait');
    const state = tone === 'err' ? 'err' : !tone ? 'stop' : bg ? 'bg' : 'ok';
    const parts = turnSummary({
      state, startedAt, endedAt: Date.now(),
      items: turnItems(this.order, (id) => this.items.get(id), startedAt),
      tokens: parseTurnTokens(this.header && this.header.lastTurn),
    });
    if (!parts) return;
    const el = this.turnSumEl;
    el.textContent = '';
    for (const p of parts) {
      const span = h('span', { class: 'ts-' + p.k, text: p.text });
      if (p.k === 'files') span.append(h('b', { class: 'add', text: ' +' + p.add }), h('b', { class: 'del', text: ' −' + p.del }));
      el.append(span);
    }
    el.dataset.state = state;
    el.hidden = false;
    replayClass(el, 'fx-in', TURN_SUM_MS);
    clearTimeout(this.turnSumTimer);
    this.turnSumTimer = setTimeout(() => { el.hidden = true; }, TURN_SUM_MS);
  }

  hideTurnSummary() {
    clearTimeout(this.turnSumTimer);
    this.turnSumEl.hidden = true;
  }

  /** 標題列閃幾下（側欄點了這個對話：窗格多的時候一眼找到它在哪）；連點時從頭再閃 */
  flashHead() {
    const el = this.head;
    el.classList.remove('ch-flash');
    void el.offsetWidth; // 重新開始動畫
    el.classList.add('ch-flash');
    clearTimeout(this.flashTimer);
    this.flashTimer = setTimeout(() => el.classList.remove('ch-flash'), HEAD_FLASH_MS);
  }

  // ───── 宿主資料 ─────
  load(msg) {
    this.loaded = true;
    this.root.classList.remove('loading');
    clearTimeout(this.loadingTimer);
    clearTimeout(this.loadWatch);
    this.loadWatch = 0;
    this.loadAttempt = 0;
    if (this.loadingEl) { this.loadingEl.remove(); this.loadingEl = null; }
    this.cancelChunks();
    this.cancelLazy();
    // 觸控的瀏覽器版（項目一律照實際高度排版，chat.css）：載入時新建的項目先帶 it-lazy（content-visibility: auto，畫面外的先不排版），
    // 第一個畫面不必等整批排版完；之後 nextLazy 逐批換回實際高度
    this.lazyLoad = isHub && fullLayout();
    this.stopPeek();
    this.items.clear();
    this.nodes.clear();
    this.cardGateKey = null; // 卡片重建了：setHeader 一定重套一次閘門
    this.order = [];
    this.itemsEl.textContent = '';
    const list = [];
    for (const it of msg.items || []) {
      if (!it || !it.id || this.items.has(it.id)) continue;
      this.items.set(it.id, it);
      this.order.push(it.id);
      list.push(it);
    }
    // buildNodes 照它把 /clear 之前的節點藏起來（分批載入時 list 的索引就是 order 的索引）
    this.clearAt = this.showCleared ? -1 : clearBoundary(list);
    // 桌面（與小對話）一次建完；瀏覽器版的大對話先建最後一批（畫面看得到的），較早的每個影格往前補一批，
    // 免得收到整個對話的那一個工作卡住主執行緒（D39）。項目資料與順序一開始就完整，只有節點晚建
    const ranges = loadChunks(list.length, isHub ? 'hub' : 'desktop');
    if (ranges.length) this.itemsEl.append(this.buildNodes(list, ranges[0]));
    if (ranges.length > 1) this.chunks = { list, ranges: ranges.slice(1) };
    this.hasMore = !!msg.hasMore;
    // 整份重建：還在路上的 olderItems 是舊視窗的（loadOlder 之後先到整份 conversation），到了也不採用（合併前審查 LIVE-2）
    this.loadingOlder = false;
    this.olderBefore = null;
    this.promptSeek = null;
    this.userScrolled = false;
    this.renderOlder();
    // 整份重送（重新 open、副本一次變了一大批）時送出中的那則可能已經在裡面
    for (const it of list) this.settleSending(it);
    if (msg.header) this.setHeader(msg.header);
    this.composer.setComposer(msg.composer || null);
    this.settleQueued(msg.composer);
    if (msg.draft) this.composer.restoreText(msg.draft); // app 更新前沒送出的字（宿主只送一次；輸入框是空的才填入）
    this.composer.draftSync.loaded(); // 其他電腦、瀏覽器版打到一半的字（draftsync.js）
    this.afterChange(true);
    this.setStick(true); // 經過 setStick 才會一併收起「最新」鈕（重送整個對話時可能正顯示著）
    this.scrollToBottom();
    requestAnimationFrame(() => { if (this.stick) this.scrollToBottom(); });
    this.maybeFocusCard();
    if (this.chunks) this.chunkRaf = requestAnimationFrame(() => this.nextChunk());
    this.scheduleLazy();
  }

  /** 建 list[start, end) 的節點（登記到 nodes），回傳待插入的片段 */
  buildNodes(list, [start, end]) {
    const frag = document.createDocumentFragment();
    for (let i = start; i < end; i++) {
      const node = createItem(list[i], this.ctx);
      if (i < this.clearAt) node.classList.add('pre-clear');
      if (this.lazyLoad) {
        node.classList.add('it-lazy');
        (this.lazyNodes || (this.lazyNodes = [])).push({ i, node });
      }
      this.nodes.set(list[i].id, node);
      frag.append(node);
    }
    return frag;
  }

  /** 分批載入的下一批（往前補） */
  nextChunk() {
    this.chunkRaf = 0;
    const c = this.chunks;
    if (!c) return;
    // 觸控捲動中先不往前補（停下來再補：捲動中改 scrollTop 在 iOS 會跳一下）
    if (this.isTouchScrolling()) { this.chunkRaf = requestAnimationFrame(() => this.nextChunk()); return; }
    const range = c.ranges.shift();
    if (!c.ranges.length) this.chunks = null;
    this.prependNodes(this.buildNodes(c.list, range));
    if (this.chunks) this.chunkRaf = requestAnimationFrame(() => this.nextChunk());
  }

  /** 剩下的批次一次補完：要改項目、載入較早的訊息、找節點之前（剩下的範圍連續，從 0 到第一個範圍的結尾） */
  flushChunks() {
    const c = this.chunks;
    if (!c) return;
    this.cancelChunks();
    this.prependNodes(this.buildNodes(c.list, [0, c.ranges[0][1]]));
  }

  cancelChunks() {
    if (this.chunkRaf) cancelAnimationFrame(this.chunkRaf);
    this.chunkRaf = 0;
    this.chunks = null;
  }

  /** 還有先不排版的項目：下一個影格開始換回（看不到時等 show） */
  scheduleLazy() {
    if (this.lazyNodes && !this.lazyRaf && this.visible) this.lazyRaf = requestAnimationFrame(() => this.nextLazy());
  }

  /**
   * 先不排版的項目換回實際高度的下一批（liverules.LAZY_BATCH 則，從最新的往前：貼底時畫面上方最先捲到的那幾則先換）。
   * 分批載入還沒補完、觸控捲動中（換回時上方項目的高度會變，同分批載入）、看不到時先不換；高度變了由 ConvView 的 ResizeObserver 維持貼底、
   * 沒貼底時由 scrollanchor.js 補位置（同延遲載入的圖片）
   */
  nextLazy() {
    this.lazyRaf = 0;
    const list = this.lazyNodes;
    if (!list || !this.visible) return;
    if (this.chunks || this.isTouchScrolling()) { this.scheduleLazy(); return; }
    list.sort((a, b) => a.i - b.i);
    for (const { node } of list.splice(Math.max(0, list.length - LAZY_BATCH))) node.classList.remove('it-lazy');
    if (!list.length) this.lazyNodes = null;
    else this.scheduleLazy();
  }

  cancelLazy() {
    if (this.lazyRaf) cancelAnimationFrame(this.lazyRaf);
    this.lazyRaf = 0;
    this.lazyNodes = null;
  }

  /** 把較早的節點插到最前面：貼底的照舊貼底，往上捲過的停在原本看的位置 */
  prependNodes(frag) {
    const before = this.scroller.scrollHeight - this.scroller.scrollTop;
    this.itemsEl.prepend(frag);
    this.paintTurnRules(); // 同載入較早的訊息：在還原位置與錨定重記之前
    if (this.stick) this.scrollToBottom();
    else this.scroller.scrollTop = this.scroller.scrollHeight - before;
    this.anchor.resync();
    this.minimap.request();
  }

  /** 同一影格合併後的更新 */
  applyBatch(b) {
    this.flushChunks();
    if (b.header) this.setHeader(b.header);
    let planTouched = false, added = false;
    const fresh = [];
    let peekId = null;
    for (const [id, item] of b.ops) {
      if (item) {
        const step = item.kind === 'Tool' || item.kind === 'Thinking';
        const prevStatus = step ? (this.items.get(item.id) || {}).status : undefined;
        const isNew = this.upsert(item);
        // 換掉送出中的那則：它進場時已經動過，正式的這則不再進場一次
        if (isNew) { added = true; if (!this.settleSending(item)) fresh.push(this.nodes.get(item.id)); }
        if (item.kind === 'Plan') planTouched = true;
        // 工具做完、思考結束了：這一批裡最後的那個，內容掃過一遍再收起來（fxrules.peekWanted）
        if (step && peekWanted(prevStatus, item.status, isNew, item.kind === 'Thinking' ? !this.collapsed.has(item.id) : this.expanded.has(item.id))) peekId = item.id;
      } else this.remove(id);
    }
    // 即時新增的項目淡入、上移進場（fxrules.entranceAllowed：一次補上一大批時不做）；載入與載入較早的訊息不經這裡
    if (entranceAllowed(fresh.length)) for (const n of fresh) replayClass(n, 'fx-in', ENTER_FX_MS);
    if (peekId && (!fresh.length || entranceAllowed(fresh.length))) this.playPeek(peekId);
    if (fresh.length && !this.stick) this.noteUnseen(fresh.length);
    if (this.clearDirty) { this.clearDirty = false; this.applyClear(); }
    if (b.composer) { this.composer.setComposer(b.composer); this.settleQueued(b.composer); }
    this.afterChange(planTouched || !!b.header);
    // 貼底不在這裡做：以前這裡改完 DOM 馬上讀 scrollHeight，每一批都逼瀏覽器同步排一次版，
    // 好幾個窗格同時串流時一個影格排好幾次（2026-10-23 剖析：串流時主執行緒的 JS 時間幾乎都在這裡）。
    // 現在由 app.js 在整批都套用完之後一起做（提早套用：pinTarget 全部讀完再 pinTo，整批只排一次版），其餘靠 ResizeObserver
    if (added) this.maybeFocusCard();
  }

  /**
   * 工具做完、思考結束時內容掃過一遍再收起來（items.js 的 playStepPeek）：只在這個對話看得到、跟著最底（往上捲著在看別的就不動）、
   * 頁面沒被藏起來、沒有要求減少動態也沒關掉動態效果時；同時只有一個，上一個還沒收完就快速收起來讓給新的
   */
  playPeek(id) {
    if (!this.visible || !this.stick || document.hidden || calmMotion()) return;
    const node = this.nodes.get(id), item = this.items.get(id);
    if (!node || !item || !node.isConnected) return;
    if (this.peekNode && this.peekNode !== node) stopStepPeek(this.peekNode, true);
    this.peekNode = playStepPeek(node, item) ? node : null;
  }

  /** 正在掃過的小框直接拿掉（整份重建、切走這個分頁：display:none 之後再顯示時 CSS 動畫會從頭播） */
  stopPeek() {
    if (this.peekNode) stopStepPeek(this.peekNode);
    this.peekNode = null;
  }

  // ───── 送出中（sendrules.js） ─────

  /**
   * 按下送出（composer.send 照 sendrules.showsSending 決定）：宿主的使用者訊息到之前，先在所有項目之後畫一則虛線框、寫著「送出中…」的。
   * 宿主檢查資料夾、存檔、插進這一輪，遠端還要繞伺服器與那台電腦一圈；那些都做完之前畫面不會停在什麼都沒有
   */
  addSending(text, files, { tracked = false } = {}) {
    const t = String(text || '').trim();
    let afterSeq = null;
    for (const it of this.items.values()) if (typeof it.seq === 'number' && (afterSeq === null || it.seq > afterSeq)) afterSeq = it.seq;
    const node = createItem({ id: 'sending-' + (++this.sendCount), kind: 'User', status: 'Done', time: Date.now() }, this.ctx);
    node.classList.add('sending');
    if (t) {
      const tx = h('div', { class: 'md u-text sending-text', text: t });
      chipMentions(tx);
      node._box.append(tx);
    }
    if (files > 0) node._box.append(h('div', { class: 'sending-files', text: '附 ' + files + ' 個檔案' }));
    node._meta = node.querySelector('.u-meta');
    // state：pending（送出中）→ accepted（遠端已送到那台電腦）／failed（沒有送出）／unknown（不確定有沒有送出）
    const entry = { key: sendKey(t), text: t, files: files > 0 ? files : 0, afterSeq, node, timer: 0, state: 'pending', failure: null };
    this.renderSendingState(entry);
    // 本機的一直沒有回應就標成不確定；遠端的由指令追蹤在 150 秒時給結果不明（composer.onSendSettled）
    if (!tracked) entry.timer = setTimeout(() => this.failEntry(entry, failureOf({ state: 'timeout' }), true), SENDING_TIMEOUT_MS);
    this.sending.push(entry);
    this.sendingEl.append(node);
    if (entranceAllowed(1)) replayClass(node, 'fx-in', ENTER_FX_MS);
    this.afterChange(false);
  }

  /** 泡泡下方的狀態：送出中、已送到那台電腦，或沒有送出／不確定（原因＋重新送出、放回輸入框、移除） */
  renderSendingState(entry) {
    const { node, state } = entry;
    node.classList.toggle('failed', state === 'failed');
    node.classList.toggle('unknown', state === 'unknown');
    const meta = node._meta;
    if (!meta) return;
    meta.textContent = '';
    if (state === 'pending' || state === 'accepted') {
      meta.append(h('span', { class: 'it-time sending-state', text: state === 'accepted' ? '已送到那台電腦，等待同步…' : '送出中…' }));
      return;
    }
    const act = (cls, label, title, fn) => h('button', { type: 'button', class: 'btn sm ' + cls, text: label, title, onClick: fn });
    meta.append(
      h('span', { class: 'sending-fail', role: 'alert' }, icon('warning'), h('span', { class: 'sending-fail-text', text: entry.failure ? entry.failure.label : '沒有送出' })),
      h('span', { class: 'sending-acts' },
        act('primary sending-resend', '重新送出', '再送一次這則訊息', () => this.composer.resend(entry.text)),
        act('sending-putback', '放回輸入框', '把這則訊息放回輸入框修改', () => { this.composer.putBack(entry.text); this.removeSending(entry); }),
        act('sending-remove', '移除', '移除這則沒有送出的訊息', () => this.removeSending(entry))));
  }

  /**
   * 這則沒有送出或不確定（sendrules.failureOf）：留在對話欄、標出原因，標題列閃一下提醒；timeout＝本機一直沒有回應，另跳提示
   * （宿主拒絕時宿主自己會跳提示）
   */
  failEntry(entry, failure, timeout = false) {
    if (!this.sending.includes(entry) || !failureUpdates(entry.failure, failure)) return;
    const first = !entry.failure;
    clearTimeout(entry.timer);
    entry.timer = 0;
    entry.state = failure.kind;
    entry.failure = failure;
    this.renderSendingState(entry);
    if (!first) return;
    if (timeout) toast(failure.label + '：' + (entry.text || '（附件）').slice(0, 40), 'error');
    if (this.visible) this.flashHead();
  }

  /** sendRejected、遠端指令的結果不是 done：同樣文字的那則標成沒有送出或不確定（failSending），回傳有沒有對上 */
  failSending(text, failure) {
    const i = rejectedSent(this.sending, text);
    if (i < 0) return false;
    this.failEntry(this.sending[i], failure);
    return true;
  }

  /** 〔遠端〕這則的指令結果是 done：已經送到那台電腦，使用者訊息之後經同步拉回來（等太久就安靜收掉，確定已經送出） */
  acceptSending(text) {
    const i = rejectedSent(this.sending, text);
    const entry = i >= 0 ? this.sending[i] : null;
    if (!entry || entry.state !== 'pending') return;
    clearTimeout(entry.timer);
    entry.state = 'accepted';
    entry.timer = setTimeout(() => this.removeSending(entry), ACCEPTED_LINGER_MS);
    this.renderSendingState(entry);
  }

  /** 重新送出了同樣的文字（按「重新送出」或從輸入框再送）：之前標成沒有送出、不確定的那幾則收掉 */
  clearFailed(text) {
    const key = sendKey(text);
    for (const s of this.sending.filter((x) => x.key === key && (x.state === 'failed' || x.state === 'unknown'))) this.removeSending(s);
  }

  removeSending(entry) {
    const i = this.sending.indexOf(entry);
    if (i < 0) return;
    this.sending.splice(i, 1);
    clearTimeout(entry.timer);
    entry.node.remove();
    if (!this.sending.length) this.afterChange(false);
  }

  /** 宿主的使用者訊息到了：收掉對上的那則送出中的（sendrules.matchSent），回傳有沒有收 */
  settleSending(item) {
    const i = matchSent(this.sending, item);
    if (i < 0) return false;
    this.removeSending(this.sending[i]);
    return true;
  }

  /** 宿主的佇列裡有了（排進佇列、等這一輪結束；sendrules.queuedSent）：由佇列顯示，對話欄收掉 */
  settleQueued(composer) {
    if (!this.sending.length || !composer) return;
    const hit = queuedSent(this.sending, composer.queue, composer.steering || null);
    for (const i of hit.reverse()) this.removeSending(this.sending[i]);
  }

  upsert(item) {
    if (!item || !item.id) return false;
    const prev = this.items.get(item.id);
    this.items.set(item.id, item);
    if (prev) {
      const node = this.nodes.get(item.id);
      const next = updateItem(node, item, prev, this.ctx);
      if (next !== node) {
        if (node.classList.contains('pre-clear')) next.classList.add('pre-clear');
        node.replaceWith(next); this.nodes.set(item.id, next); if (next._autofocus) this.pendingFocus = next;
        // 等你作答的卡片有了結果：角框鎖定成結果的顏色閃兩下（fxrules.cardSettleFx：允許綠、拒絕紅、略過琥珀，失效不閃）
        const settle = cardSettleFx(item.kind, prev.status, item);
        if (settle) replayClass(next, settle, CARD_SETTLE_FX_MS);
      }
      return false;
    }
    // 還沒載入的較早項目被更新（只載入最後 80 筆時）：不要錯插到最後面，等使用者載入較早的訊息時再拿到。
    // 比建立順序 seq，不比 time：time 是建立當下的系統時間，時鐘往回調後新項目會比第一筆還「早」而被全部丟掉
    if (this.hasMore && this.order.length && typeof item.seq === 'number') {
      const first = this.items.get(this.order[0]);
      if (first && typeof first.seq === 'number' && item.seq < first.seq) { this.items.delete(item.id); return false; }
    }
    const node = createItem(item, this.ctx);
    this.nodes.set(item.id, node);
    this.order.push(item.id);
    this.itemsEl.append(node);
    if (node._autofocus) this.pendingFocus = node;
    // 又清除了一次：之前按過「顯示清除前的對話」也重新藏起來（applyBatch 處理完這一批再套）
    if (clearBoundary([item]) === 0) { this.showCleared = false; this.clearDirty = true; }
    return true;
  }

  remove(id) {
    const node = this.nodes.get(id);
    if (node) node.remove();
    this.nodes.delete(id);
    if (clearBoundary([this.items.get(id)]) === 0) this.clearDirty = true;
    if (this.items.delete(id)) this.order = this.order.filter((x) => x !== id);
  }

  /**
   * olderItems：只收自己在等的那一頁（合併前審查 LIVE-2）。loadOlder 之後先到整份 conversation（重連後重新 open、伺服器主動重送）時，
   * load() 已清掉 loadingOlder，晚到的舊頁不能插到新視窗前面（中間整段會不見、hasMore 也會被改錯）；宿主回帶 beforeId 時另比對是不是這一次的請求
   */
  prependOlder(msg) {
    if (!this.loadingOlder || (typeof msg.beforeId === 'string' && msg.beforeId !== this.olderBefore)) return;
    // 觸控捲動中（往上滑到頂端附近自動載入的那一頁多半在這時到）：等停下來再插，到時候再檢查一次是不是還在等這一頁
    if (this.isTouchScrolling()) { this.deferOlder(msg); return; }
    this.flushChunks();
    this.loadingOlder = false;
    this.olderBefore = null;
    const seamAt = this.itemsEl.firstElementChild; // 原本最上面那一則：接縫在它的上緣
    const before = this.scroller.scrollHeight - this.scroller.scrollTop;
    const frag = document.createDocumentFragment();
    const ids = [];
    for (const it of msg.items || []) {
      if (!it || !it.id || this.items.has(it.id)) continue;
      const node = createItem(it, this.ctx);
      this.items.set(it.id, it);
      this.nodes.set(it.id, node);
      ids.push(it.id);
      frag.append(node);
    }
    this.order = ids.concat(this.order);
    this.itemsEl.prepend(frag);
    this.hasMore = !!msg.hasMore;
    // 較早的這一頁可能有清除提示（清除之後的訊息超過一頁時）：照它重新藏，也會重畫上方的按鈕
    this.applyClear();
    // 每一輪的分隔線在還原捲動位置、錨定重記之前畫（原本最上面那一則這時才有前一段）：晚一格畫，錨定只看到上方項目變高、會多補一次
    this.paintTurnRules();
    // 插入後保持畫面停在原本看的位置（自己補了：錨定照現在的排版重記高度、不重複補，清除前的項目藏起來也算在這裡）
    this.scroller.scrollTop = this.scroller.scrollHeight - before;
    this.anchor.resync();
    // 接縫掃過一道光：較早的這一頁接在原本最上面那一則之前（畫在那一則裡面的上緣，播完移除）
    if (ids.length && seamAt && seamAt.isConnected && !calmMotion()) {
      const seam = h('i', { class: 'older-seam', 'aria-hidden': 'true' });
      seamAt.append(seam);
      setTimeout(() => seam.remove(), OLDER_SEAM_MS);
    }
    this.minimap.request();
    this.renderPrompt();
    this.continuePromptSeek();
  }

  /** 較早的這一頁等觸控捲動停下來再插（每個影格看一次） */
  deferOlder(msg) {
    cancelAnimationFrame(this.olderRaf);
    const wait = () => {
      this.olderRaf = 0;
      if (this.isTouchScrolling()) this.olderRaf = requestAnimationFrame(wait);
      else this.prependOlder(msg);
    };
    this.olderRaf = requestAnimationFrame(wait);
  }

  loadOlder() {
    // 藏著 /clear 之前的項目時不往前載入（按「顯示清除前的對話」才照舊載入）
    if (!this.hasMore || this.loadingOlder || !this.order.length || this.clearAt >= 0) return;
    this.flushChunks();
    this.loadingOlder = true;
    this.olderBefore = this.order[0];
    this.renderOlder();
    post({ cmd: 'loadOlder', conversationId: this.id, beforeId: this.olderBefore });
  }

  renderOlder() {
    const hiding = this.clearHiding();
    this.olderRow.hidden = !this.hasMore && !hiding;
    // 載入中：按鈕上方一條分格的載入列（fx.css 的 .older.loading，只在真的在等宿主回應的期間）
    this.olderRow.classList.toggle('loading', !!this.loadingOlder);
    this.olderBtn.disabled = this.loadingOlder;
    this.olderBtn.textContent = '';
    if (this.loadingOlder) this.olderBtn.append(spinner(), h('span', { text: '載入中…' }));
    else this.olderBtn.append(h('span', { text: hiding ? '顯示清除前的對話' : '載入較早的訊息' }));
  }

  /** /clear 之前有藏起來的項目，或宿主還有較早的（清除提示是載入的第一則）：上方的按鈕是「顯示清除前的對話」 */
  clearHiding() {
    return this.clearAt > 0 || (this.clearAt === 0 && this.hasMore);
  }

  /**
   * /clear（liverules.clearBoundary）：最後一則清除提示之前的項目藏起來、不往前載入；這個分頁按過「顯示清除前的對話」就照舊全部顯示，
   * 之後又清除一次才再藏（upsert）。宿主不刪項目，藏的只有畫面
   */
  applyClear() {
    this.clearAt = this.showCleared ? -1 : clearBoundary(this.order.map((id) => this.items.get(id)));
    this.order.forEach((id, i) => { const n = this.nodes.get(id); if (n) n.classList.toggle('pre-clear', i < this.clearAt); });
    this.renderOlder();
    this.minimap.request();
  }

  /** 「顯示清除前的對話」：藏起來的照舊顯示（停在原本看的位置）；清除提示就是載入的第一則時直接往前載入 */
  revealCleared() {
    const at = this.clearAt;
    this.showCleared = true;
    const before = this.scroller.scrollHeight - this.scroller.scrollTop;
    this.applyClear();
    this.scroller.scrollTop = this.scroller.scrollHeight - before;
    this.anchor.resync();
    if (at === 0) this.loadOlder();
  }

  setHeader(hd) {
    const prevHd = this.header; // 自動命名的標題解碼要比前後兩份（fxrules.titleDecodeFx）
    // 標頭更新沒變就不帶指令與模型清單：沿用上一份（快照一律整份）。上一份也沒有＝和宿主對不上了：重送 open 拿整份快照（2026-11-07）
    const missing = missingHeaderLists(this.header, hd);
    if (missing.length) this.recoverHeaderLists(missing, hd);
    hd = mergeHeaderLists(this.header, hd);
    this.header = hd;
    const acc = accountOf(hd.accountId);
    const color = acc ? acc.color : null;
    setAcc(this.root, safeColor(color, hd.provider), hd.provider);
    // 〔同步〕桌面上其他電腦的副本（D22）：唯讀，不能改名、換帳戶、換資料夾、瀏覽檔案；不看 accountMissing
    const replica = this.isReplica();
    // 臨時對話（quickchat.js）：回覆顯示「朗讀」鈕（chat.css 的 .conv.quick .it-speak）
    this.root.classList.toggle('quick', !!hd.ephemeral);
    // 自動命名產生了新標題：先閃過亂碼再逐字鎖定（fxrules.titleDecodeFx；手動改名直接換）
    this.setTitleText(hd.isDraft ? (hd.ephemeral && hd.title ? hd.title : '新對話') : (hd.title || '未命名對話'), !hd.isDraft && titleDecodeFx(prevHd, hd));
    this.titleSpin.hidden = !hd.titling;
    this.autoTitleBtn.hidden = !this.canAutoTitle() || !!hd.titling;
    this.titleEl.title = hd.isDraft ? '新對話（送出第一則訊息後會自動命名）'
      : hd.titling ? AUTO_TITLE_BUSY
        : (hd.title || '') + (this.settingsLocked() ? '' : this.canAutoTitle() ? '（雙擊重新命名，點後面的圖示可自動重新命名）' : '（雙擊重新命名）');
    // 宿主只讓已開始的對話彈出（草稿沒有人顯示就會被丟棄）：草稿先停用，說明原因
    this.popBtn.disabled = !!hd.isDraft;
    this.popBtn.title = hd.isDraft ? POPOUT_DRAFT_HINT : '在新視窗開啟';
    // 〔遠端〕伺服器草稿不能列檔（只收設定類的指令）：送出第一則訊息後才能瀏覽；副本的檔案在其他電腦上
    const readOnly = this.readOnlyReplica();
    this.drawerBtn.disabled = (isHub && !!hd.isDraft) || readOnly;
    this.drawerBtn.title = readOnly ? '其他電腦的對話不能瀏覽檔案' : this.drawerBtn.disabled ? '送出第一則訊息後才能瀏覽檔案' : '檔案瀏覽器';
    if (readOnly && this.drawer && this.drawer.isOpen) this.toggleDrawer(false);
    this.chipsEl.hidden = false;
    this.refreshAccountChip();
    this.renderCollabChip();
    // 換了帳戶（手動、額度用完自動換、跨代理備援）：帳戶晶片的新帳戶從下面接上、外框閃一下新帳戶的顏色（fxrules.handoffFx）；
    // 副本比的是那台用的帳戶（replica.accountId）
    const fxOf = (x) => (x && replica ? { accountId: headerAccountId(x, 'replica'), provider: x.provider } : x);
    if (handoffFx(fxOf(prevHd), fxOf(hd))) replayClass(this.acctChip, 'fx-handoff', HANDOFF_FX_MS);
    const dir = hd.workingDirectory || '';
    this.folderChip.querySelector('.chip-label').textContent = hd.customFolder ? (baseName(dir) || dir || '自選資料夾') : '預設工作區';
    // 〔遠端〕瀏覽器版的預設工作區在那台電腦上（伺服器不給路徑）：不顯示路徑
    this.folderChip.title = readOnly ? '工作資料夾在其他電腦上（這台不能更換或瀏覽）'
      : ((isHub || replica) && !hd.customFolder ? '預設工作區（在對話所在的電腦上）' : '工作資料夾：' + dir) + (this.folderBusy() ? '（執行中不能更換）' : '');
    this.folderChip.classList.toggle('custom', !!hd.customFolder);
    // 〔桌面遠端操作〕要在其他電腦開的新對話：送出之前就能選那台的資料夾（輸入路徑或挑那台最近用過的；第一次送出時那台檢查），建立中不能換
    const remoteDraft = !isHub && !!hd.isDraft && !!hd.deviceId;
    if (remoteDraft) {
      const where = '在 ' + ((this.liveInfo().device || {}).name || '那台電腦') + ' 上';
      this.folderChip.title = hd.customFolder ? '工作資料夾（' + where + '）：' + dir : '預設工作區（' + where + '，這個對話自己的資料夾）';
    }
    this.folderChip.disabled = readOnly || (remoteDraft && hd.peerCreating === true);
    this.warn.hidden = !hd.accountMissing || replica;
    this.syncDelEl.hidden = !(!replica && hd.conversationSync && hd.conversationSync.serverDeleted === true);
    this.renderRestart(hd.restart);
    this.renderPrograms(hd.programs);
    this.renderGoal(hd);
    this.renderOrigin();
    this.renderSubagentUsage(hd, prevHd);
    this.root.dataset.rewind = canRewind(hd, { isHub, isReplica: this.isReplica() }) ? '1' : '';
    // 狀態列（liverules.statusLine）：桌面只在執行中；瀏覽器有伺服器的狀態文字就顯示（電腦離線、伺服器正在整理這個對話…），只有執行中才轉圈。
    // 執行中的動畫跟著轉圈：其他電腦的副本照擁有者那邊的（header.running 是這台的，一律 false）
    const sl = statusLine(hd, isHub ? 'hub' : 'desktop');
    // 代理這一輪結束了、背景工作還在跑（liverules.backgroundLine）：還算在進行，換成「等背景工作回報」的樣貌（虛線圈、角框慢慢呼吸）
    const bg = sl.spinning ? null : backgroundLine(hd);
    const wasRunning = this.root.classList.contains('running');
    // 停下時和拿掉 running 同一次加上 fx-cool：同一次樣式計算，帳戶色的括號與燈才是從亮著開始淡掉（按停止的也淡）
    if (wasRunning && !sl.spinning) replayClass(this.root, 'fx-cool', COOL_FX_MS);
    this.root.classList.toggle('running', sl.spinning);
    this.root.classList.toggle('bg-wait', !!bg);
    // 結算單的開始時間：執行中的標頭帶著這一輪開始的時間（停下之後的標頭是 null）
    if (sl.spinning && Number.isFinite(hd.startedAt)) this.turnStartedAt = hd.startedAt;
    if (!wasRunning && sl.spinning) this.hideTurnSummary();
    if (wasRunning && !sl.spinning) this.scheduleTurnEnd();
    // 開始執行：倒數中的「60 秒後自動重試」收尾（重試開始了閃一下、提早開始的是取消，items.settleRetryNotes）
    if (!wasRunning && sl.spinning) settleRetryNotes(this.ctx);
    this.runEl.hidden = !sl.spinning && !sl.text && !bg;
    this.runSpin.hidden = !sl.spinning;
    this.runBg.hidden = !bg;
    this.runEl.classList.toggle('idle', !sl.spinning);
    this.runEl.classList.toggle('bg', !!bg);
    this.runEl.title = bg ? bg.title : '';
    this.runDetail.hidden = !bg || isHub || this.isReplica() || detailTasks(hd.background).length === 0;
    // 停止只有桌面這台的對話（背景工作屬於這台活著的 CLI 程序）；新的標頭到了就能再按（停了的話背景工作的清單也跟著變）
    this.runStop.hidden = !bg || isHub || this.isReplica();
    this.runStop.disabled = false;
    // 移到背景：執行中、宿主說前景有能移的指令或子代理（Header.canBackground）
    this.runToBg.hidden = !sl.spinning || hd.canBackground !== true || isHub || this.isReplica();
    this.runToBg.disabled = false;
    this.runText.textContent = sl.spinning ? sl.text || '思考中…' : bg ? bg.text : sl.text || '';
    this.renderDevice();
    this.tick(Date.now());
    this.composer.setHeader(hd);
    // 標頭帶來的電腦（清單外的對話第一次載入）或它的在線改了：已建好的卡片也照新的閘門（送出的閘門由 composer.setHeader 重算）
    this.refreshCardGates();
    if (this.drawer) this.drawer.setRoot(dir);
    emit('conv-header', this.id);
  }

  // ───── 〔熱更新〕CLI 換版狀態列（header.restart；宿主產生，網頁只顯示） ─────
  buildRestartBar() {
    this.restartIc = h('span', { class: 'chr-icwrap' });
    this.restartTextEl = h('span', { class: 'chr-text' });
    // 已等待的時間（宿主的 since；每秒在 tick 更新，宿主不必每秒重送標頭）
    this.restartWait = h('span', { class: 'chr-wait', hidden: true, title: '從排定換版起已經等了多久' });
    this.restartVer = h('span', { class: 'chr-ver' });
    // 按鈕只建立一次、之後只切換顯示：執行中的標頭每 50ms 更新一次，每次重建的話按下到放開之間按鈕被換掉，點了沒反應
    this.restartApply = h('button', { type: 'button', class: 'btn sm', title: '不等安全點，現在就以新版重開這個對話' }, icon('refresh'), h('span', { text: '立即套用' }));
    this.restartApply.addEventListener('click', () => this.applyRestartNow());
    this.restartCont = h('button', { type: 'button', class: 'btn sm primary', title: '請代理從中斷處繼續（送出接續訊息，不會顯示成你的訊息）' }, icon('send'), h('span', { text: '繼續' }));
    this.restartCont.addEventListener('click', () => this.continueAfterRestart());
    this.restartEl = h('div', { class: 'ch-restart', hidden: true, role: 'status' },
      this.restartIc, this.restartTextEl, this.restartWait, this.restartVer, h('span', { class: 'chr-acts' }, this.restartApply, this.restartCont));
    this.restartSig = '';
    this.restartCur = null;
  }

  renderRestart(r) {
    const valid = !!(r && typeof r === 'object' && r.state);
    const sig = valid ? JSON.stringify([r.state, r.from || null, r.to || null, r.detail || null, !!r.canApplyNow, !!r.canContinue, r.since || null]) : '';
    if (sig === this.restartSig) return;
    this.restartSig = sig;
    this.restartCur = valid ? r : null;
    this.restartEl.hidden = !valid;
    this.renderRestartWait(Date.now());
    if (!valid) return;
    const st = restartStateOf(r.state);
    this.restartEl.dataset.state = enumKey(r.state);
    this.restartEl.dataset.tone = st.tone;
    if (this.restartIc._state !== r.state) {
      this.restartIc._state = r.state;
      this.restartIc.textContent = '';
      this.restartIc.append(restartIcon(r.state, 'chr-ic'));
    }
    const text = restartText(r);
    this.restartTextEl.textContent = text;
    this.restartTextEl.title = text;
    const ver = versionArrow(r.from, r.to);
    this.restartVer.textContent = ver;
    this.restartVer.title = ver ? 'CLI 版本：' + ver : '';
    this.restartVer.hidden = !ver;
    // 狀態變了：按鈕恢復可按（送出指令後先停用，避免連按）
    this.restartApply.hidden = !r.canApplyNow;
    this.restartApply.disabled = false;
    this.restartCont.hidden = !r.canContinue;
    this.restartCont.disabled = false;
  }

  /** 已等待的時間：狀態列看得到、宿主給了 since 才顯示；字一樣時不寫 DOM */
  renderRestartWait(now) {
    const text = this.restartCur ? restartWaitText(this.restartCur, now) : '';
    if (this.restartWait.textContent !== text) this.restartWait.textContent = text;
    this.restartWait.hidden = !text;
  }

  /** 「立即套用」：說明會中斷什麼、確認後送 cliApplyNow {conversationId}（確認框開著時狀態變得不能套用就不送） */
  async applyRestartNow() {
    const r = this.restartCur;
    if (!r || !r.canApplyNow) return;
    if (!(await confirmApplyNowConversation(r))) return;
    if (!this.restartCur || !this.restartCur.canApplyNow) return;
    this.restartApply.disabled = true;
    post({ cmd: 'cliApplyNow', conversationId: this.id });
    this.reenableLater(this.restartApply);
  }

  /** 「繼續」：宿主送出接續訊息（不顯示成使用者訊息，對話加一則 Notice） */
  continueAfterRestart() {
    const r = this.restartCur;
    if (!r || !r.canContinue) return;
    this.restartCont.disabled = true;
    post({ cmd: 'cliContinue', conversationId: this.id });
    this.reenableLater(this.restartCont);
  }

  /** 宿主沒有回應（狀態沒變，例如指令被忽略）時，幾秒後讓按鈕恢復可按 */
  reenableLater(btn) {
    const sig = this.restartSig;
    setTimeout(() => { if (this.restartSig === sig) btn.disabled = false; }, 5000);
  }

  refreshAccountChip() {
    const hd = this.header;
    if (!hd) return;
    const { origin } = this.liveInfo();
    // 〔同步〕副本的帳戶在其他電腦上：顯示那台用的帳戶（replica.accountId，邏輯帳戶），也不是「帳戶已移除」
    const replica = origin === 'replica';
    const acc = replica ? findAccount(state.accounts, headerAccountId(hd, origin)) : accountOf(hd.accountId);
    const label = this.acctChip.querySelector('.chip-label');
    // 〔遠端〕其他電腦的對話只能換成那台已登入的帳戶；副本不能從這台操作時停用（liverules.remoteAccountMenu）
    const remote = this.remoteAccounts();
    this.acctChip.disabled = remote.remote && !remote.enabled;
    const pending = replica ? null : pendingSwitchView(hd);
    const next = this.acctChip.querySelector('.chip-next');
    // 每則標頭都會到這裡（執行中的對話一秒好幾則）：值沒變不寫（hidden、文字節點、title 每寫一次都要重算樣式，util.setHidden）
    setHidden(next, !pending);
    next.classList.toggle('stopping', !!pending && !pending.turnEnd);
    setTextIfChanged(next.lastChild, pending ? pending.label : '');
    const hint = !remote.remote ? accountChipHint(hd.provider, !!hd.running, false, pending)
      : !remote.enabled ? remote.reason
        : accountChipHint(hd.provider, ownerRunning(hd, origin), false, pending) + '\n只能換成' + remote.scope;
    if (acc) {
      setTextIfChanged(label, acc.label);
      setAcc(this.root, acc.color, hd.provider);
      setTitle(this.acctChip, acc.provider + '・' + acc.label + (acc.plan ? '（' + acc.plan + '）' : '') + '\n' + hint);
    } else {
      setTextIfChanged(label, hd.accountMissing && !replica ? '帳戶已移除' : hd.provider);
      setTitle(this.acctChip, replica ? hd.provider + '（其他電腦的帳戶）\n' + hint : '更換帳戶');
    }
    this.acctChip.classList.toggle('missing', !acc && !replica);
  }

  refreshAccount() {
    this.refreshAccountChip();
    this.composer.refreshAccount();
    // 空白草稿頁的帳戶名稱與「無法使用」提示也跟著帳戶狀態更新（例如到儀表板登入完成）
    if (!this.empty.hidden) this.renderEmpty();
  }

  /**
   * 每一輪的分隔線（第八批，fxrules.turnRules）：每則使用者訊息上緣一條刻度線，寫出前面那一段花了多久、工具幾次、改了幾個檔案。
   * 只在項目的順序或建好的節點變了時重算（串流中只是字變長時不走）；分隔線的高度從那則的上邊距扣掉（fx.css 的 .has-rule），
   * 加上去或拿掉都不會讓下面的內容移動。一律在改 DOM 的同一個工作裡畫（插入較早的項目時在還原捲動位置、錨定重記之前）：
   * 晚一格才畫的話，沒有原生捲動錨定的瀏覽器（scrollanchor.js 看各則的高度）會把上方那則變高 14px 當成內容變了、多補一次 scrollTop
   */
  paintTurnRules() {
    const order = this.order;
    const sig = order.length + '|' + this.nodes.size + '|' + (order[0] || '') + '|' + (order[order.length - 1] || '');
    if (sig === this.turnRuleSig) return;
    this.turnRuleSig = sig;
    const rules = new Map(turnRules(order.map((id) => this.items.get(id))).map((r) => [r.id, r.parts]));
    for (const id of order) {
      const node = this.nodes.get(id);
      if (!node || !node.classList.contains('it-user')) continue;
      const parts = rules.get(id);
      const key = parts ? JSON.stringify(parts) : '';
      if (node._ruleKey === key) continue;
      node._ruleKey = key;
      if (node._rule) { node._rule.remove(); node._rule = null; }
      node.classList.toggle('has-rule', !!parts);
      if (!parts) continue;
      const rule = h('div', { class: 'turn-rule' });
      for (const p of parts) {
        const span = h('span', { class: 'rl-' + p.k, text: p.text, title: p.title || null });
        if (p.k === 'files') span.append(h('b', { class: 'add', text: ' +' + p.add }), h('b', { class: 'del', text: ' −' + p.del }));
        rule.append(span);
      }
      node._rule = rule;
      node.prepend(rule);
    }
  }

  afterChange(planCheck) {
    const hd = this.header;
    this.renderPrompt();
    this.minimap.request();
    this.paintTurnRules();
    const isEmpty = this.order.length === 0 && this.sending.length === 0;
    setHidden(this.empty, !(hd && isEmpty && !this.hasMore));
    if (!this.empty.hidden) this.renderEmpty();
    if (planCheck || this.root.classList.contains('running')) {
      // 只看最新一個 Plan，而且要還在進行（Running）：宿主在每一輪結束時把它標成 Done（步驟全部完成時也是 Done），
      // 上一輪的待辦不該在之後每一輪都顯示成「目前步驟」
      let plan = null;
      for (let i = this.order.length - 1; i >= 0; i--) {
        const it = this.items.get(this.order[i]);
        if (it && it.kind === 'Plan') { plan = it.status === 'Running' ? it : null; break; }
      }
      this.composer.renderTodo(plan, this.root.classList.contains('running'));
    }
  }

  renderEmpty() {
    const hd = this.header;
    const acc = accountOf(hd.accountId);
    this.empty.textContent = '';
    // 〔同步〕沒有內容的副本不是草稿：說明內容在哪裡（擁有者的政策只同步標題與狀態時沒有項目，D22）
    if (this.isReplica()) {
      const noItems = !!(hd.replica && hd.replica.itemsAvailable === false);
      this.empty.append(h('div', { class: 'draft-lines', dataset: { field: 'replicaEmpty' } },
        h('div', { class: 'draft-title', text: '其他電腦的對話' }),
        h('div', { class: 'draft-sub', text: noItems ? '那台電腦只同步標題與狀態，內容只在那台電腦上。' : '還沒有訊息。' })));
      return;
    }
    // 〔桌面遠端操作〕要在其他電腦開的新對話：送出之後、那台建好之前
    if (hd.isDraft && hd.deviceId && hd.peerCreating === true) {
      const dev = this.liveInfo().device;
      this.empty.append(h('div', { class: 'draft-lines', dataset: { field: 'peerCreating' } },
        h('div', { class: 'draft-title' }, spinner(), h('span', { text: '正在' + (dev ? ' ' + dev.name + ' ' : '那台電腦') + '上建立新對話…' })),
        h('div', { class: 'draft-sub', text: '建好之後會自動換成那台的對話。' })));
      return;
    }
    // 臨時對話（header.ephemeral，quickchat.js）：日常的問題、不用工作資料夾、關掉就刪掉
    if (hd.ephemeral) {
      this.empty.append(h('div', { class: 'draft-lines', dataset: { field: 'quickChatEmpty' } },
        h('div', { class: 'draft-title', text: (acc ? '用「' + acc.label + '」' : '') + '問點什麼' }),
        h('div', { class: 'draft-sub', text: '臨時對話：不用選工作資料夾，不會出現在對話清單；關掉面板就連同紀錄一起刪掉。回覆可以按「朗讀」念出來。' })));
      return;
    }
    const remoteDraft = !isHub && !!hd.isDraft && !!hd.deviceId;
    const lines = h('div', { class: 'draft-lines' },
      h('div', { class: 'draft-title', text: (acc ? '用「' + acc.label + '」開始新的對話' : '開始新的對話') + (remoteDraft ? '（在 ' + ((this.liveInfo().device || {}).name || '其他電腦') + ' 上）' : '') }),
      h('div', { class: 'draft-sub', text: '在下方描述要做的事。代理會在工作資料夾裡讀寫檔案、執行指令。' }));
    const tips = h('div', { class: 'draft-tips' },
      tip('at', '@', '引用工作資料夾裡的檔案'),
      (hd.commands && hd.commands.length) ? tip('terminal', '/', '執行指令或技能') : null,
      tip('shield', 'Shift+Tab', '切換權限模式'),
      tip('paperclip', '拖放／貼上', '附加圖片、影片、文件'));
    this.empty.append(lines, tips);
    if (acc && acc.status && acc.status !== 'ready' && acc.status !== 'checking') {
      this.empty.append(h('div', { class: 'draft-warn' }, icon('warning'), h('span', { text: (acc.statusText || '這個帳戶目前無法使用') + '，可以到儀表板登入或換一個帳戶。' })));
    }
  }

  // ───── 捲動 ─────
  canScroll() { return this.scroller.scrollHeight > this.scroller.clientHeight + 2; }

  onScroll() {
    // 自己捲到最底引起的那一次捲動事件：一定在最底，不必再讀捲動位置（讀了又要排一次版）
    if (this.autoScrolled) { this.autoScrolled = false; return; }
    this.touch.lastScrollAt = performance.now();
    const sc = this.scroller;
    const dist = sc.scrollHeight - sc.scrollTop - sc.clientHeight;
    if (dist <= NEAR_BOTTOM) this.setStick(true);
    else if (dist > LEAVE_BOTTOM) { this.setStick(false); this.userScrolled = true; }
  }

  /**
   * 捲動停下（scrollend）：不在最底時，在小地圖旁浮出畫面最上面那一則屬於第幾輪（往前最近的使用者訊息是第幾則／共幾則），
   * POS_SHOW_MS 後淡掉。只在停下的那一刻找一次最上面的項目（elementFromPoint），捲動中不讀版面
   */
  onScrollEnd() {
    clearTimeout(this.posTimer);
    if (this.stick || !this.visible) { setHidden(this.posEl, true); return; }
    const sc = this.scroller;
    const r = sc.getBoundingClientRect();
    const t = this.thread.getBoundingClientRect();
    const x = t.left + t.width / 2;
    let node = null;
    for (const dy of [14, 40, 80, 140]) {
      const el = document.elementFromPoint(x, r.top + dy);
      node = el && el.closest('.it');
      if (node && this.itemsEl.contains(node)) break;
      node = null;
    }
    const idx = node ? this.order.indexOf(node.dataset.id) : -1;
    const pos = idx >= 0 ? turnPosition(this.order.map((id) => (this.items.get(id) || {}).kind), idx) : null;
    if (!pos) { this.posEl.hidden = true; return; }
    this.posEl.textContent = '第 ' + pos.n + '／' + pos.total + ' 輪';
    this.posEl.style.top = (sc.offsetTop + 10) + 'px';
    this.posEl.hidden = false;
    replayClass(this.posEl, 'fx-pos', POS_SHOW_MS);
    this.posTimer = setTimeout(() => { this.posEl.hidden = true; }, POS_SHOW_MS);
  }

  onTouch(e, phase) {
    const t = this.touch;
    const y = e.touches && e.touches.length ? e.touches[0].clientY : null;
    if (phase === 'start') { t.touching = true; t.startY = y; return; }
    if (phase === 'move') {
      if (touchUnsticks(t.startY, y) && this.canScroll()) { this.setStick(false); this.userScrolled = true; }
      return;
    }
    // 還有別的手指按著就還沒放開
    if (y !== null) return;
    t.touching = false;
    t.startY = null;
    t.touchEndAt = performance.now();
  }

  isTouchScrolling() { return touchScrolling(this.touch, performance.now()); }

  setStick(v) {
    if (this.stick === v) return;
    this.stick = v;
    this.jumpBtn.hidden = v;
    if (v && this.unseen) { this.unseen = 0; this.jumpLabel.textContent = '最新'; }
  }

  /** 捲上去看舊訊息時有新項目進來：「↓ 最新」寫出多了幾則、閃一下 */
  noteUnseen(n) {
    this.unseen = (this.unseen || 0) + n;
    this.jumpLabel.textContent = '最新 · ' + this.unseen;
    replayClass(this.jumpBtn, 'fx-ping', 700);
  }

  scrollToBottom() {
    const sc = this.scroller;
    // 已經在最底就不寫（不會有捲動事件，留著的旗標會吃掉使用者的下一次捲動）
    if (sc.scrollHeight - sc.clientHeight - sc.scrollTop < 1) return;
    this.autoScrolled = true;
    sc.scrollTop = sc.scrollHeight;
  }

  /**
   * 這則項目的更新是不是就地修補已經在畫面上的節點（app.js 的提早套用只收這種）：新項目、要整個重建的（卡片、換了種類）不算——
   * 新建的 .it 有 content-visibility: auto，第一格畫面決定它看得到之前內容不算樣式、高度是估計的
   */
  patchesInPlace(itemId, item) {
    const prev = this.items.get(itemId);
    return !!item && !!prev && this.nodes.has(itemId) && fillsInPlace(prev.kind, item.kind);
  }

  /** 貼底要捲到哪（讀版面）：沒跟著最底、看不到、已經在最底的回 null（app.js 提早套用之後，整批讀完再一起 pinTo） */
  pinTarget() {
    if (!this.stick || !this.visible) return null;
    const sc = this.scroller;
    const h = sc.scrollHeight;
    return h - sc.clientHeight - sc.scrollTop < 1 ? null : h;
  }

  /** 捲到 pinTarget 給的位置（同 scrollToBottom：記下是自己捲的，那一次捲動事件不必再讀位置） */
  pinTo(h) {
    this.autoScrolled = true;
    this.scroller.scrollTop = h;
  }

  stickToBottom() {
    this.setStick(true);
    this.scrollToBottom();
    requestAnimationFrame(() => this.scrollToBottom());
  }

  /** 新出現等待作答的卡片：使用者沒有停在輸入區或任何輸入欄位、輸入框也是空的，才把焦點移到卡片（數字鍵可直接選） */
  maybeFocusCard() {
    const node = this.pendingFocus;
    this.pendingFocus = null;
    if (!node || !this.visible || !node.isConnected) return;
    if (this.composer.ta.value.trim()) return;
    const active = document.activeElement;
    if (active && active !== document.body && !this.root.contains(active)) return;
    // 停在輸入區（剛送出、正要打下一則排隊訊息）或其他輸入欄位時不搶焦點：卡片把數字鍵當選項，
    // 搶走焦點後打的「1」會在使用者沒看到卡片的情況下直接核准
    if (active && (this.composer.el.contains(active) || active.matches('input, textarea, select') || active.isContentEditable)) return;
    if (document.querySelector('.dlg-back, .lb, .vp')) return;
    try { node.focus({ preventScroll: true }); } catch { /* 忽略 */ }
  }

  // ───── 每秒更新（只在可見時呼叫） ─────
  tick(now) {
    const hd = this.header;
    // 執行中：這一輪已經多久；等背景工作回報：最早開始、還在跑的那個背景工作已經跑了多久（滑過的清單也寫每一個的）
    const bg = this.root.classList.contains('bg-wait') ? backgroundLine(hd, now) : null;
    if (hd && hd.running && hd.startedAt) this.runTime.textContent = fmtElapsed(now - hd.startedAt);
    else if (bg && bg.startedAt) this.runTime.textContent = fmtElapsed(now - bg.startedAt);
    else this.runTime.textContent = '';
    if (bg) this.runEl.title = bg.title;
    this.renderRestartWait(now);
    this.launchBar.tick(now);
    // 執行中的工具計時、等你作答的卡片等了多久、這一輪的輸出速度
    tickToolElapsed(this.itemsEl, now);
    tickThinkTimes(this.itemsEl, now, (id) => this.items.get(id), this.order);
    tickCardAges(this.itemsEl, now, (id) => this.items.get(id));
    this.composer.tickRate(now);
    this.composer.tickQuota(now);
    // 跨過午夜：昨天的訊息時間要加上「昨天」
    const day = dayKey(now);
    if (day !== this.timeDay) {
      this.timeDay = day;
      refreshTimes(this.itemsEl, now);
      this.promptKey = null;
      this.renderPrompt();
    }
  }

  // ───── 對話上方常駐的「最近的指令」 ─────
  buildPromptBar() {
    // 只顯示一行（2026-10-25 使用者要求）：放不下時 lp-run 在 lp-text 裡跑馬燈（fitPrompt）；點一下展開全文
    this.lpLabel = h('span', { class: 'lp-label', text: '最近的指令' });
    this.lpRun = h('span', { class: 'lp-run' });
    this.lpText = h('div', { class: 'lp-text' }, this.lpRun);
    this.lpBody = h('div', { class: 'lp-body', role: 'button', tabIndex: 0, 'aria-expanded': 'false' }, this.lpLabel, this.lpText);
    this.lpBody.addEventListener('click', () => this.togglePrompt());
    this.lpBody.addEventListener('keydown', (e) => { if (e.target === this.lpBody && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); this.togglePrompt(); } });
    this.lpFiles = h('span', { class: 'lp-files', hidden: true });
    this.lpTime = h('span', { class: 'lp-time' });
    this.lpSpin = spinner('lp-spin');
    this.lpSpin.hidden = true;
    this.lpJump = iconButton('arrowUp', '捲到這則訊息', () => this.jumpToPrompt(), 'sm');
    // 行尾：最近 10 筆歷史指令（web-protocol 的 promptHistory），點一筆帶進輸入框
    this.lpHist = iconButton('clock', '歷史指令（最近 10 筆，點一筆帶進輸入框）', () => this.openPromptHistory(), 'sm lp-hist');
    this.lpHist.setAttribute('aria-haspopup', 'menu');
    this.promptEl = h('div', { class: 'lastprompt', hidden: true, dataset: { field: 'lastPrompt' } },
      h('div', { class: 'lp-inner' }, this.lpBody, h('div', { class: 'lp-side' }, this.lpSpin, this.lpFiles, this.lpTime, this.lpJump, this.lpHist)));
  }

  /** 照已載入的項目與標頭重畫（liverules.latestPrompt）；內容沒變就不動（展開、捲動、選取的文字都保留） */
  renderPrompt() {
    const hd = this.header;
    const p = this.loaded && hd && !hd.isDraft ? latestPrompt(this.order.map((id) => this.items.get(id)), hd, this.hasMore) : null;
    const key = JSON.stringify([p, !!this.promptSeek]);
    if (key === this.promptKey) return;
    this.promptKey = key;
    if (!p || !this.prompt || p.id !== this.prompt.id) this.setPromptOpen(false); // 換了一則：收合
    this.prompt = p;
    this.promptEl.hidden = !p;
    if (!p) return;
    this.promptEl.classList.toggle('lp-older', !!p.older);
    this.promptEl.classList.toggle('lp-dim', !!p.older || !p.text);
    if (p.older) {
      // 〔遠端〕瀏覽器版的標頭不附：那則在還沒載入的較早訊息裡
      this.lpRun.textContent = this.promptSeek ? '載入中…' : '在較早的訊息裡，點這裡載入';
      this.lpBody.title = '載入較早的訊息，找到最近的指令';
    } else {
      this.lpRun.textContent = mentionPlain(p.text) || '（只有附件）';
      this.lpBody.title = this.promptEl.classList.contains('open') ? '點一下收合' : '最近發給代理的指令（點一下看全文）';
    }
    this.lpSpin.hidden = !this.promptSeek;
    this.lpTime.textContent = p.older ? '' : itemTimeText(p.time, Date.now());
    this.lpTime.title = p.older ? '' : itemTimeTitle(p.time);
    this.lpFiles.hidden = !(p.attachments > 0);
    this.lpFiles.textContent = '';
    if (p.attachments > 0) {
      this.lpFiles.append(icon('paperclip'), h('span', { text: String(p.attachments) }));
      this.lpFiles.title = p.attachments + ' 個附件';
    }
    this.lpJump.hidden = !!p.older;
    this.lpJump.disabled = !!this.promptSeek;
    this.lpHist.hidden = !!p.older;
    this.scheduleFitHeader(); // 跑馬燈照新的文字重量
  }

  /**
   * 「最近的指令」只顯示一行：放不下時跑馬燈（同標題的 fitHeader：來回捲動、兩端停一下，每秒約 30px），展開全文時不捲。
   * 動畫只在 CSS 允許動態時才有（減少動態、關掉動態效果時照舊以「…」截斷）
   */
  fitPrompt() {
    const box = this.lpText;
    const open = this.promptEl.classList.contains('open');
    const over = this.promptEl.hidden || open || !box.clientWidth ? 0 : this.lpRun.offsetWidth - box.clientWidth;
    const on = over > 1;
    box.classList.toggle('marquee', on);
    if (!on) return;
    box.style.setProperty('--mq-dist', -Math.ceil(over) + 'px');
    box.style.setProperty('--mq-dur', Math.max(5, over / 21).toFixed(1) + 's');
  }

  // ───── 「最近的指令」那一行的歷史指令（web-protocol 的 promptHistory） ─────

  /** 桌面向宿主要完整的最近 10 則（回來才開清單；1.5 秒沒回就用已載入的項目）；瀏覽器版不問，直接從已載入的項目找 */
  openPromptHistory() {
    if (!this.prompt || this.prompt.older) return;
    if (isHub) { this.showPromptHistory(null); return; }
    if (this.historyWait) { clearTimeout(this.historyWait.timer); this.historyWait = null; }
    const requestId = nextRequestId('ph');
    this.historyWait = { requestId, timer: setTimeout(() => { this.historyWait = null; this.showPromptHistory(null); }, 1500) };
    post({ cmd: 'promptHistory', conversationId: this.id, requestId });
  }

  /** 宿主的 promptHistory：是等著的那一次才開清單 */
  applyPromptHistory(msg) {
    const w = this.historyWait;
    if (!w || !msg || msg.requestId !== w.requestId) return;
    clearTimeout(w.timer);
    this.historyWait = null;
    this.showPromptHistory(msg);
  }

  showPromptHistory(reply) {
    if (!this.prompt || this.promptEl.hidden || !this.lpHist.isConnected) return;
    const list = promptHistory(reply, this.order.map((id) => this.items.get(id)));
    const now = Date.now();
    const items = [{ type: 'header', label: list.length ? '歷史指令（最近 ' + list.length + ' 筆，點一筆帶進輸入框）' : '歷史指令' }];
    if (!list.length) items.push({ label: '還沒有送過指令', disabled: true });
    for (const p of list) {
      const when = itemTimeText(p.time, now);
      items.push({
        label: promptLine(mentionPlain(p.text)), desc: [when, p.attachments > 0 ? p.attachments + ' 個附件（不會帶進去）' : ''].filter(Boolean).join('・'),
        title: p.text.length > 2000 ? p.text.slice(0, 2000) + '…' : p.text, cls: 'mi-prompt',
        onSelect: () => this.composer.replaceText(p.text),
      });
    }
    openMenu(this.lpHist, items, { minWidth: 320, className: 'menu-prompt-history' }); // 再按一次同一顆按鈕＝關閉（ui.openMenu）
  }

  togglePrompt() {
    const p = this.prompt;
    if (!p) return;
    if (p.older) { this.seekPrompt(false); return; }
    // 正在選取全文裡的字（要複製）：不收合
    const sel = window.getSelection();
    if (sel && !sel.isCollapsed && this.lpBody.contains(sel.anchorNode)) return;
    this.setPromptOpen(!this.promptEl.classList.contains('open'));
  }

  setPromptOpen(open) {
    this.promptEl.classList.toggle('open', open);
    this.lpBody.setAttribute('aria-expanded', String(open));
    if (this.prompt && !this.prompt.older) this.lpBody.title = open ? '點一下收合' : '最近發給代理的指令（點一下看全文）';
    if (!open) this.lpText.scrollTop = 0;
    this.fitPrompt();
  }

  /** 捲到最近的指令那則訊息；標頭附的、還沒載入時先往前載入到那一則 */
  jumpToPrompt() {
    const p = this.prompt;
    if (!p || p.older) return;
    const node = this.ctx.nodeOf(p.id);
    if (node) this.scrollToItem(node);
    else this.seekPrompt(true);
  }

  /** 捲到這個項目（停止貼底），短暫標示；還沒畫過的項目高度是估的，下兩個影格照實際位置再對一次 */
  scrollToItem(node) {
    const sc = this.scroller;
    this.setStick(false);
    const go = () => { if (node.isConnected) sc.scrollTop = Math.max(0, node.getBoundingClientRect().top - sc.getBoundingClientRect().top + sc.scrollTop - 12); };
    go();
    requestAnimationFrame(() => { go(); requestAnimationFrame(go); });
    node.classList.remove('lp-flash');
    void node.offsetWidth; // 重新開始動畫
    node.classList.add('lp-flash');
    clearTimeout(node._flashTimer);
    node._flashTimer = setTimeout(() => node.classList.remove('lp-flash'), 1700);
  }

  /** 最近的指令不在已載入的項目裡：往前一頁一頁載入到找到為止（jump：找到後捲過去） */
  seekPrompt(jump) {
    if (this.promptSeek || !this.hasMore) return;
    this.promptSeek = { jump };
    this.loadOlder();
    if (!this.loadingOlder) this.promptSeek = null; // 沒送出（藏著 /clear 之前的項目等）
    this.renderPrompt();
  }

  /** 較早的一頁到了（prependOlder）：還沒找到就再載一頁，找到了（或沒有更早的）就停 */
  continuePromptSeek() {
    const seek = this.promptSeek;
    if (!seek) return;
    const p = this.prompt;
    const found = !!(p && !p.older && this.nodes.has(p.id));
    if (!found && p && this.hasMore) {
      this.loadOlder();
      if (this.loadingOlder) return;
    }
    this.promptSeek = null;
    this.renderPrompt();
    if (found && seek.jump) this.scrollToItem(this.nodes.get(p.id));
  }

  // ───── 標題列的行數與標題的跑馬燈 ─────
  setTitleText(text, decode = false) {
    if (this.titleRun.textContent === text) return;
    setDecodedText(this.titleRun, text, decode); // 解碼期間每個字的寬度不變（textfx.js），跑馬燈照常量
    this.scheduleFitHeader();
  }

  /** 下一影格再量（ResizeObserver 回呼裡改 class 可能觸發 ResizeObserver loop 錯誤；同一影格多次只量一次） */
  scheduleFitHeader() {
    if (this.fitHeaderFrame) return;
    this.fitHeaderFrame = requestAnimationFrame(() => { this.fitHeaderFrame = 0; this.fitHeader(); });
  }

  /**
   * 窗格很矮（SHORT_PANE 以下：12 個窗格、小視窗的 2×2）時標題列擠回一行（.short-head），兩行會把輸入區的停止、送出鈕擠出窗格；
   * 標題放不下時改成跑馬燈：在框裡來回捲動、兩端停一下，捲動的距離與時間照超出的寬度算（每秒約 30px），放得下就照常顯示。
   * 動畫只在 CSS 允許動態時才有（減少動態、設定關掉動態效果時照舊以「…」截斷）
   */
  fitHeader() {
    const height = this.root.clientHeight;
    if (height > 0) this.root.classList.toggle('short-head', height < SHORT_PANE);
    const box = this.titleText;
    const over = box.hidden || !box.clientWidth ? 0 : this.titleRun.offsetWidth - box.clientWidth;
    const on = over > 1;
    box.classList.toggle('marquee', on);
    if (!on) return;
    box.style.setProperty('--mq-dist', -Math.ceil(over) + 'px');
    box.style.setProperty('--mq-dur', Math.max(5, over / 21).toFixed(1) + 's');
    this.fitPrompt();
  }

  // ───── 重新命名、帳戶、資料夾、更多 ─────
  startRename() {
    const hd = this.header;
    if (!hd || hd.isDraft || this.settingsLocked() || this.titleEl.querySelector('input')) return;
    const span = this.titleText;
    const input = h('input', { class: 'input ch-rename', value: hd.title || '', 'aria-label': '對話標題' });
    span.hidden = true;
    this.titleEl.append(input);
    input.focus();
    input.select();
    let done = false;
    const finish = (save) => {
      if (done) return;
      done = true;
      const v = input.value.trim();
      input.remove();
      span.hidden = false;
      if (save && v && v !== hd.title) {
        hd.title = v;
        this.setTitleText(v);
        post({ cmd: 'rename', conversationId: this.id, title: v });
        emit('conv-header', this.id);
      }
    };
    input.addEventListener('keydown', (e) => {
      if (e.isComposing || e.keyCode === 229) return;
      if (e.key === 'Enter') { e.preventDefault(); finish(true); }
      else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); finish(false); }
    });
    input.addEventListener('blur', () => finish(true));
  }

  openAccountMenu(anchor) {
    const hd = this.header;
    if (!hd || isCollaborator()) return; // 多人協作：換帳戶是擁有者的
    if (hd.isDraft && this.isRemoteDraft()) { this.openPeerAccountMenu(anchor); return; }
    // 〔遠端〕其他電腦的對話（瀏覽器版、桌面的副本）：只列那台已登入的帳戶（id 是邏輯帳戶），那台離線、不允許遠端操作時只提示
    const remote = this.remoteAccounts();
    if (remote.remote && (!remote.enabled || remote.reason)) {
      if (remote.reason) toast(remote.reason);
      return;
    }
    const pool = remote.remote ? remote.accounts : state.accounts;
    const current = remote.remote ? remote.current : hd.accountId;
    const row = (a) => ({
      label: a.label, color: safeColor(a.color, a.provider), checked: a.id === current,
      desc: [a.plan, a.status === 'ready' ? null : (a.statusText || null)].filter(Boolean).join('，') || a.provider,
      onSelect: () => this.switchAccount(a),
    });
    // 執行中：列四種代理的帳戶（立即換或等這一輪結束後換；另一種代理以前文節錄開新對話，accountrules.runningAccountMenu）
    if (ownerRunning(hd, this.liveInfo().origin)) {
      const m = runningAccountMenu(hd.provider, pool, pendingSwitchView(hd), providerKeys(), current);
      if (!m.allowed) { toast(m.reason); return; }
      const disabledSet = new Set(m.disabledIds || []);
      const running = [];
      m.sections.forEach((s, i) => {
        if (i) running.push({ type: 'sep' });
        running.push({ type: 'header', label: s.header });
        s.accounts.forEach((a) => {
          const item = a.id === m.pendingId ? { ...row(a), desc: m.pendingHint } : row(a);
          running.push(disabledSet.has(a.id) ? { ...item, disabled: true } : item);
        });
      });
      const empty = remote.remote ? remote.empty : m.empty;
      if (empty) running.push({ label: empty, disabled: true });
      // 等這一輪結束才換的可以取消（選回目前的帳戶，宿主就不換了）
      if (m.cancel) running.push({ type: 'sep' }, { label: m.cancel.label, icon: 'x', desc: m.cancel.desc, onSelect: () => this.cancelPendingSwitch() });
      openMenu(anchor || this.acctChip, running, { minWidth: 260 });
      return;
    }
    // remoteOnly（只在其他電腦、這台沒有登入）不能拿來對話，不列（其他電腦的對話的候選已經只有那台登入的）
    const usable = remote.remote ? pool : pool.filter((a) => !a.remoteOnly);
    const same = usable.filter((a) => a.provider === hd.provider);
    const others = providerKeys().filter((p) => p !== hd.provider)
      .map((p) => usable.filter((a) => a.provider === p)).filter((list) => list.length);
    // 選了帳戶、在等背景工作結束才換的（桌面這台的對話）：那個帳戶標上說明，另給「取消換帳戶」
    const pending = remote.remote ? null : pendingSwitchView(hd);
    const idleRow = (a) => (pending && a.id === pending.accountId ? { ...row(a), desc: pending.hint } : row(a));
    const items = [];
    if (same.length) { items.push({ type: 'header', label: hd.provider }); same.forEach((a) => items.push(idleRow(a))); }
    for (const other of others) {
      if (items.length) items.push({ type: 'sep' });
      items.push({ type: 'header', label: hd.isDraft ? other[0].provider : other[0].provider + '（會以前文節錄開新對話）' });
      other.forEach((a) => items.push(idleRow(a)));
    }
    if (remote.remote && remote.empty) {
      if (items.length) items.push({ type: 'sep' });
      items.push({ label: remote.empty, disabled: true });
    }
    if (!items.length) items.push({ label: '還沒有帳戶', disabled: true });
    if (pending) items.push({ type: 'sep' }, { label: '取消換帳戶', icon: 'x', desc: pending.hint, onSelect: () => this.cancelPendingSwitch() });
    openMenu(anchor || this.acctChip, items, { minWidth: 260 });
  }

  async switchAccount(a) {
    const hd = this.header;
    if (!hd) return;
    const { origin } = this.liveInfo();
    // 〔桌面遠端操作〕副本的 setAccount 交給同步送給那台（帶邏輯帳戶），宿主不回 conversation：不記在等回覆
    const replica = origin === 'replica';
    if (a.id === (replica ? headerAccountId(hd, origin) : hd.accountId)) {
      // 執行中選了別的帳戶、要等這一輪結束才換：選回目前的帳戶＝取消
      const pending = pendingSwitchView(hd);
      if (pending && pending.turnEnd) this.cancelPendingSwitch();
      return;
    }
    if (ownerRunning(hd, origin) && !hd.isDraft) {
      // 執行中：選時機——現在停下這一輪（結束時換過去、從中斷處接著做），或等這一輪結束再換
      const when = await choiceDialog(runningSwitchConfirm(a.label, hd.provider, a.provider));
      if (!when) return;
      // 等這一輪結束的換過去之後才會有新的 conversation（可能很久以後），不算在等回覆
      if (when === 'now' && !replica) this.ws.notePendingSwitch(this.id);
      post(runningSwitchCommand(this.id, a.id, when));
      return;
    } else if (!replica && !hd.isDraft && hd.background && typeof hd.background === 'object') {
      // 這一輪結束了、背景工作還在跑（標頭的 background 只有桌面這台的對話有）：換帳戶會關掉程序、背景工作跟著停——
      // 選現在換（新帳戶收到被停掉的背景工作再接著做）或等背景工作結束再換（預設；跨代理的說明也在這裡）
      const when = await choiceDialog(backgroundSwitchConfirm(a.label, hd.provider, a.provider, hd.background));
      if (!when) return;
      // 等背景工作結束的換過去之後才會有新的 conversation（可能很久以後），不算在等回覆
      if (when === SWITCH_NOW) this.ws.notePendingSwitch(this.id);
      post(backgroundSwitchCommand(this.id, a.id, when));
      return;
    } else if (a.provider !== hd.provider && !hd.isDraft) {
      const r = await confirmDialog({
        title: '改用「' + a.label + '」繼續？',
        message: hd.provider + ' 和 ' + a.provider + ' 的對話不能直接接續。會把目前對話的前文節錄交給新帳戶，以新的對話繼續。',
        confirmLabel: '改用 ' + a.provider + ' 繼續',
      });
      if (!r.ok) return;
    }
    if (!replica) this.ws.notePendingSwitch(this.id);
    post({ cmd: 'setAccount', conversationId: this.id, accountId: a.id });
  }

  /** 取消「這一輪結束後換帳戶」：送目前的帳戶（宿主取消、提示；沒有新的 conversation） */
  cancelPendingSwitch() {
    const hd = this.header;
    if (!hd) return;
    post({ cmd: 'setAccount', conversationId: this.id, accountId: hd.accountId });
  }

  openFolderMenu() {
    const hd = this.header;
    if (!hd || this.settingsLocked()) return;
    const running = this.folderBusy();
    const dir = hd.workingDirectory || '';
    const recent = (hd.recentFolders || []).filter((p) => p && p.toLowerCase() !== dir.toLowerCase()).slice(0, 6);
    // 〔遠端〕瀏覽器版沒有檔案總管、資料夾對話框與附件資料夾（不在遠端指令型錄）：以「瀏覽資料夾…」挑那台的資料夾
    // （listFiles dirsOnly，那台要是 Control，D33；新對話由伺服器轉給選的那台，2026-10-27），挑不到時手輸路徑（setFolder）
    // 〔桌面遠端操作〕要在其他電腦開的新對話同瀏覽器版的草稿：手輸路徑或挑那台最近用過的（宿主從那台的副本取），第一次送出時那台檢查
    const remoteDraft = this.isRemoteDraft();
    const remote = this.remoteLike() || remoteDraft;
    const devName = remoteDraft ? ((this.liveInfo().device || {}).name || '那台電腦') : '';
    const pathLabel = hd.customFolder ? (remoteDraft ? devName + ' 上的工作資料夾' : '工作資料夾')
      : remoteDraft ? '預設工作區（在 ' + devName + ' 上，這個對話自己的資料夾）' : '預設工作區（在對話所在的電腦上）';
    const picker = this.folderPicker();
    const items = remote ? [
      { type: 'custom', node: h('div', { class: 'menu-path', title: dir }, h('span', { class: 'menu-path-label', text: pathLabel }), hd.customFolder ? h('span', { class: 'menu-path-value', text: dir }) : null) },
      hd.isDraft ? null : { label: '瀏覽檔案', icon: 'panelRight', onSelect: () => { if (this.drawer) this.drawer.endPick(); this.toggleDrawer(true); } },
      { type: 'sep' },
      picker.shown ? { label: '瀏覽資料夾…', icon: 'folderOpen', disabled: !picker.enabled, desc: picker.enabled ? null : picker.reason, title: picker.reason, onSelect: () => this.openFolderPicker() } : null,
      { label: '輸入資料夾路徑…', icon: 'folder', disabled: running, title: running ? '執行中不能更換' : null, onSelect: () => this.promptFolder() },
    ] : [
      { type: 'custom', node: h('div', { class: 'menu-path', title: dir }, h('span', { class: 'menu-path-label', text: hd.customFolder ? '工作資料夾' : '預設工作區（這個對話自己的資料夾）' }), h('span', { class: 'menu-path-value', text: dir })) },
      { label: '在檔案總管開啟', icon: 'folderOpen', onSelect: () => post({ cmd: 'openFolder', conversationId: this.id, which: 'working' }) },
      { label: '在 app 內瀏覽檔案', icon: 'panelRight', onSelect: () => this.toggleDrawer(true) },
      { type: 'sep' },
      { label: '更換資料夾…', icon: 'folder', disabled: running, title: running ? '執行中不能更換' : null, onSelect: () => post({ cmd: 'chooseFolder', conversationId: this.id }) },
    ];
    if (recent.length) {
      items.push({ type: 'header', label: remoteDraft ? '最近使用（' + devName + '）' : '最近使用' });
      for (const p of recent) items.push({ label: baseName(p) || p, desc: p, icon: 'clock', disabled: running, onSelect: () => post({ cmd: 'setFolder', conversationId: this.id, path: p }) });
    }
    items.push({ label: '恢復預設工作區', icon: 'refresh', disabled: running || !hd.customFolder, onSelect: () => post({ cmd: 'resetFolder', conversationId: this.id }) });
    // 附件資料夾在這台的檔案總管開：瀏覽器版、其他電腦的副本與遠端草稿沒有（openFolder 不在遠端指令型錄，副本送了只會被當成唯讀擋下）
    if (!remote) {
      items.push({ type: 'sep' });
      items.push({ label: '開啟附件資料夾', icon: 'paperclip', onSelect: () => post({ cmd: 'openFolder', conversationId: this.id, which: 'attachments' }) });
    }
    openMenu(this.folderChip, items, { minWidth: 300, className: 'menu-folder' });
  }

  /** 〔遠端〕「瀏覽資料夾…」（D33）：檔案抽屜的挑選模式（listFiles dirsOnly），選定後送 setFolder（那台檢查存不存在、在不在禁止清單） */
  openFolderPicker() {
    const fp = this.folderPicker();
    if (!fp.enabled) { if (fp.reason) toast(fp.reason, 'error'); return; }
    this.ensureDrawer();
    this.drawer.pick({
      onPick: (path) => { if (this.header && !this.folderBusy()) post({ cmd: 'setFolder', conversationId: this.id, path }); },
    });
    this.toggleDrawer(true);
  }

  /**
   * 〔遠端〕瀏覽器版的「輸入資料夾路徑…」：路徑是對話所在那台電腦上的（setFolder，那台檢查存不存在）。
   * 〔桌面遠端操作〕遠端草稿的宿主只記下來，第一次送出時那台才檢查（不行就整則沒有送出、文字回到輸入框）
   */
  promptFolder() {
    const hd = this.header;
    if (!hd || this.folderBusy()) return;
    const remoteDraft = this.isRemoteDraft();
    const input = h('input', { type: 'text', class: 'input', value: hd.customFolder ? hd.workingDirectory || '' : '', placeholder: '例如 C:\\repo\\demo', autocomplete: 'off', spellcheck: 'false', 'aria-label': '資料夾路徑', dataset: { field: 'folderPath' } });
    const dev = this.liveInfo().device;
    const where = '輸入' + (dev ? dev.name + ' ' : '') + '上的完整路徑；';
    const detail = remoteDraft ? where + '送出第一則訊息時那台電腦會檢查，找不到這個資料夾就不會送出。' : where + '那台電腦找不到這個資料夾時不會更換。';
    const body = h('div', { class: 'confirm' }, h('p', { class: 'confirm-detail', text: detail }), input);
    const cancel = h('button', { type: 'button', class: 'btn', text: '取消' });
    const ok = h('button', { type: 'button', class: 'btn primary', text: remoteDraft ? '選定' : '更換' });
    const d = openDialog({ title: remoteDraft ? '選擇工作資料夾' : '更換工作資料夾', body, footer: [cancel, ok], className: 'dlg-confirm', width: 480, initialFocus: input });
    const submit = () => {
      const path = input.value.trim();
      if (!path) { input.focus(); return; }
      d.close();
      if (this.header && !this.folderBusy()) post({ cmd: 'setFolder', conversationId: this.id, path });
    };
    ok.addEventListener('click', submit);
    cancel.addEventListener('click', () => d.close());
    input.addEventListener('keydown', (e) => { if (e.key === 'Enter' && !e.isComposing && e.keyCode !== 229) { e.preventDefault(); submit(); } });
  }

  // ───── 執行列（launchStatus／launchLog；launchbar.js） ─────
  /** 宿主送來的 LaunchView（null＝沒有可以執行的專案、還沒偵測完或換了資料夾） */
  setLaunch(launch) { this.launchBar.set(launch); }

  applyLaunchLog(m) { this.launchBar.applyLog(m); }

  // ───── 工作資料夾的 git 狀態（gitStatus；gitrules.js） ─────
  /** 宿主送來的 GitStatusView（null＝不是 repo、還沒讀到或換了資料夾） */
  setGit(git) {
    this.git = git && typeof git === 'object' ? git : null;
    this.renderGit();
  }

  renderGit() {
    const key = gitKey(this.git);
    if (key === this.gitRendered) return;
    this.gitRendered = key;
    const v = gitView(this.git);
    this.gitChip.hidden = !v;
    if (!v) { if (this.gitCounters) this.gitCounters.clear(); return; }
    this.gitChip.dataset.tone = v.tone;
    this.gitChip.querySelector('.chip-label').textContent = v.label;
    // 徽章裡的數字（↑3、↓2、●5）用側欄同款的計數器，每種一個沿用：數字變了先閃過亂碼再鎖定（fxrules.splitCount；第一次直接顯示）
    const counters = this.gitCounters || (this.gitCounters = new Map());
    const values = [];
    this.gitChip.querySelector('.git-badges').replaceChildren(...v.badges.map((b) => {
      const parts = splitCount(b.text);
      if (!parts) return h('span', { class: 'git-badge', dataset: { kind: b.kind }, text: b.text });
      let c = counters.get(b.kind);
      if (!c) { c = tokenCounter(String, this.ctx.shown); counters.set(b.kind, c); }
      values.push([c, parts.n]);
      return h('span', { class: 'git-badge', dataset: { kind: b.kind } }, parts.pre, c.num, parts.post);
    }));
    for (const [c, n] of values) c.set(n);
    for (const k of [...counters.keys()]) if (!v.badges.some((b) => b.kind === k)) counters.delete(k); // 消失過的徽章再出現時直接顯示
    this.gitChip.title = v.title;
    this.gitChip.setAttribute('aria-label', 'Git：' + v.label + '，' + v.summary);
  }

  // ───── 代理另開的背景程式（header.programs；liverules.programsView） ─────
  renderPrograms(programs) {
    const v = programsView(programs, Date.now());
    const sig = v ? v.rows.map((r) => r.id + ' ' + r.command).join('\n') : '';
    if (sig === this.progSig) return;
    this.progSig = sig;
    // 程式自己結束了（從清單消失、不是在選單按停止的）：晶片閃一下琥珀色（fx.css 的 .ch-prog.fx-prog-exit）；
    // 最後一個也結束時晶片多留一下、閃完才藏起來
    const ids = new Set(v ? v.rows.map((r) => r.id) : []);
    const exited = [...this.progIds].filter((id) => !ids.has(id) && !this.progStopped.has(id));
    for (const id of this.progStopped) if (!ids.has(id)) this.progStopped.delete(id);
    this.progIds = ids;
    clearTimeout(this.progHideTimer);
    if (exited.length && !this.progChip.hidden) replayClass(this.progChip, 'fx-prog-exit', PROG_EXIT_FX_MS);
    if (!v) {
      if (exited.length && !this.progChip.hidden) this.progHideTimer = setTimeout(() => { if (!this.progSig) this.progChip.hidden = true; }, PROG_EXIT_FX_MS);
      else this.progChip.hidden = true;
      return;
    }
    this.progChip.hidden = false;
    // 每個還在跑的程式一顆綠燈（最多 5 顆）
    const leds = this.progChip.querySelector('.prog-leds');
    const want = Math.min(5, v.rows.length);
    while (leds.childElementCount < want) leds.append(h('i'));
    while (leds.childElementCount > want) leds.lastElementChild.remove();
    this.progChip.querySelector('.chip-label').textContent = v.label;
    this.progChip.title = v.title;
    this.progChip.setAttribute('aria-label', v.label + '：' + v.rows.map((r) => r.command).join('、'));
  }

  openRewindFor(itemId) {
    if (!canRewind(this.header, { isHub, isReplica: this.isReplica() })) {
      toast(this.header && this.header.running ? '這一輪還在執行：先按停止再回溯' : '這個對話不能回溯');
      return;
    }
    const node = this.nodes.get(itemId);
    const box = node && node._box ? node._box.querySelector('.u-text') : null;
    const text = box ? (box.textContent || '').trim().split('\n')[0] : '';
    // 回溯預覽：對話框開著的時候，會被捨棄的（這則與之後的）淡下去，這則上緣標出回溯點
    this.markRewind(itemId);
    openRewind(this.id, itemId, text.length > 60 ? text.slice(0, 60) + '…' : text, () => this.markRewind(null));
  }

  /** 回溯預覽（fx.css 的 .rewind-preview）：itemId 是回溯點的使用者訊息，null 收起來 */
  markRewind(itemId) {
    if (this.rewindNode) { this.rewindNode.classList.remove('rw-from'); this.rewindNode = null; }
    const node = itemId ? this.nodes.get(itemId) : null;
    this.itemsEl.classList.toggle('rewind-preview', !!node);
    if (!node) return;
    node.classList.add('rw-from');
    this.rewindNode = node;
  }

  /** 「詳細」：只有一個背景工作就直接開，多個時先選 */
  openTaskDetails() {
    const bg = this.header && this.header.background;
    const tasks = detailTasks(bg);
    if (tasks.length === 1) { openTaskDetail(this.id, tasks[0], bg); return; }
    if (!tasks.length) return;
    openMenu(this.runDetail, tasks.map((t) => ({ label: t.title || t.tool || '背景工作', desc: t.title ? t.tool : null, icon: 'terminal', onSelect: () => openTaskDetail(this.id, t, bg) })), { minWidth: 280 });
  }

  /** 「停止」：選單列出能單獨停止的背景工作與「全部停止」，選了送 taskStop（停了之後宿主在對話加一則提示） */
  openTaskStop() {
    const v = backgroundStopMenu(this.header && this.header.background, Date.now());
    if (!v) return;
    const stop = (stopId) => {
      // 送出後先停用：停了的話新的標頭會換掉這一行；停不了（宿主只提示原因）時 5 秒後可以再按
      this.runStop.disabled = true;
      setTimeout(() => { this.runStop.disabled = false; }, 5000);
      post(stopId ? { cmd: 'taskStop', conversationId: this.id, stopId } : { cmd: 'taskStop', conversationId: this.id });
    };
    const items = [
      { type: 'custom', node: h('div', { class: 'prog-note', text: v.note }) },
      { type: 'sep' },
      ...v.rows.map((r) => ({ label: r.label, desc: r.desc || null, icon: 'stop', hint: '停止', title: '強制停止這個背景工作', onSelect: () => stop(r.stopId) })),
    ];
    if (v.all) items.push({ label: v.all.label, icon: 'stop', danger: true, onSelect: () => stop(null) });
    openMenu(this.runStop, items, { minWidth: 320, className: 'menu-prog' });
  }

  /** 子代理的即時用量變了：補畫那幾張 Agent 工具卡（標頭沒帶就不動，卡片照 fillTool 清掉） */
  renderSubagentUsage(hd, prevHd) {
    const now = (hd && hd.subagents) || null;
    const before = (prevHd && prevHd.subagents) || null;
    if (!now && !before) return;
    const ids = new Set([...Object.keys(now || {}), ...Object.keys(before || {})]);
    for (const id of ids) {
      const node = this.nodes.get(id);
      if (node) paintAgentUsage(node, now ? now[id] : null);
    }
  }

  /** 標頭的來源晶片（第十批）：清單裡這個對話的 parentId／forkedFrom；內容沒變不動。瀏覽器版的清單沒有這兩欄 */
  renderOrigin() {
    const o = isHub ? null : originLink(state.convMap && state.convMap.get(this.id), state.convMap);
    const sig = o ? JSON.stringify(o) : '';
    if (sig === this.originSig) return;
    this.originSig = sig;
    this.origin = o;
    this.originChip.hidden = !o;
    this.originChip.textContent = '';
    if (!o) return;
    this.originChip.append(icon(o.kind === 'fork' ? 'gitBranch' : 'user', 'chip-ic'), h('span', { class: 'chip-label', text: o.label }));
    this.originChip.title = o.title;
    this.originChip.disabled = !o.open;
    this.originChip.dataset.kind = o.kind;
  }

  // ───── Claude 進行中的目標（header.goal；claudeoptrules.goalView） ─────
  renderGoal(hd) {
    const cg = hd.codex && hd.codex.goal;
    const v = isHub || this.isReplica() ? null : cg
      ? { label: '目標' + (cg.status === 'active' ? '' : '（' + (GOAL_STATUS[cg.status] || cg.status) + '）') + '：' + String(cg.objective || '').slice(0, 24), title: (cg.objective || '') + '\n' + goalSummary(cg) }
      : goalView(hd.goal, !!hd.running);
    this.goalChip.hidden = !v;
    if (!v) return;
    this.goalChip.querySelector('.chip-label').textContent = v.label;
    this.goalChip.title = v.title;
  }

  openGoalMenu() {
    if (this.header && this.header.provider === 'Codex') { openCodexPanel(this.composer, 'goal'); return; }
    const v = goalView(this.header && this.header.goal, !!(this.header && this.header.running));
    if (!v) return;
    const items = [
      { type: 'custom', node: h('div', { class: 'menu-path' }, h('span', { class: 'menu-path-label', text: '進行中的目標' }), h('span', { class: 'menu-path-value goal-cond', text: v.condition })) },
      { type: 'sep' },
      { label: '清除目標', desc: v.clearDesc, icon: 'x', disabled: !v.canClear, onSelect: () => post({ cmd: 'goalClear', conversationId: this.id }) },
    ];
    openMenu(this.goalChip, items, { minWidth: 300, className: 'menu-goal' });
  }

  /** 背景程式的選單：每個程式一列（命令列、已執行多久、PID），點了停止那個程式與它開的子程序 */
  openProgramsMenu() {
    const v = programsView(this.header && this.header.programs, Date.now());
    if (!v) return;
    const items = [
      { type: 'custom', node: h('div', { class: 'prog-note', text: '代理另開、脫離 CLI 還在跑的程式。它們跑完不會通知代理；自己結束時會在對話裡留一則提示。' }) },
      { type: 'sep' },
      ...v.rows.map((r) => ({
        label: r.command, desc: r.desc || null, icon: 'stop', hint: '停止', title: '停止這個程式（連同它開的子程序）',
        onSelect: () => { this.progStopped.add(r.id); post({ cmd: 'programStop', conversationId: this.id, programId: r.id }); },
      })),
    ];
    openMenu(this.progChip, items, { minWidth: 320, className: 'menu-prog' });
  }

  /** git 晶片的選單：repo 路徑、明細、重新檢查（連遠端一起問） */
  openGitMenu() {
    const v = gitView(this.git);
    if (!v) return;
    const rows = h('div', { class: 'git-rows' }, ...v.rows.flatMap((r) => [
      h('span', { class: 'git-row-k', text: r.label }),
      h('span', { class: 'git-row-v', dataset: { tone: r.tone || '' }, text: r.value }),
    ]));
    const items = [
      { type: 'custom', node: h('div', { class: 'menu-path', title: this.git.root || '' }, h('span', { class: 'menu-path-label', text: 'Git 儲存庫' }), h('span', { class: 'menu-path-value', text: this.git.root || '' })) },
      { type: 'custom', node: rows },
      { type: 'custom', node: h('div', { class: 'git-note', text: '每 30 秒重新整理一次；遠端每 5 分鐘以 ls-remote 查一次。有未拉取的提交時自動快轉拉取（fetch＋merge --ff-only）；有未提交的變更、未推送的提交或對話正在執行時不拉。' }) },
      { type: 'sep' },
      { label: '重新檢查（連遠端一起）', icon: 'refresh', onSelect: () => post({ cmd: 'gitRefresh', conversationId: this.id }) },
    ];
    openMenu(this.gitChip, items, { minWidth: 320, className: 'menu-git' });
  }

  /** 這個對話能不能自動重新命名（宿主依目前在做的事產生標題，autoTitle）：只有桌面這台的正式對話（瀏覽器版是 desktop_only，副本不處理） */
  canAutoTitle() {
    const hd = this.header;
    return !!hd && !hd.isDraft && !isHub && !this.isReplica();
  }

  /** 標題的右鍵選單：重新命名、自動重新命名（產生期間停用） */
  openTitleMenu(at) {
    const hd = this.header;
    if (!hd || hd.isDraft || this.settingsLocked() || this.titleEl.querySelector('input')) return;
    const items = [{ label: '重新命名', icon: 'rename', hint: '雙擊標題', onSelect: () => this.startRename() }];
    if (this.canAutoTitle()) {
      items.push({
        label: hd.titling ? '正在產生標題…' : '自動重新命名', icon: 'autoTitle', desc: '依目前正在做或最後做的事產生標題',
        disabled: !!hd.titling, onSelect: () => this.autoTitle(),
      });
    }
    openMenu(null, items, { at, minWidth: 240 });
  }

  /** 能不能延伸成新對話（forkConversation）：桌面完整視窗裡、這台的正式對話（彈出視窗開不了旁邊的窗格；副本與草稿宿主不處理） */
  canFork() {
    const hd = this.header;
    return !!hd && !hd.isDraft && !isHub && !this.isReplica() && state.mode === 'main';
  }

  /** 自動重新命名：結果照常以 header（titling、title）與 conversations 回來；失敗或改用最近的訊息命名時宿主送 toast */
  autoTitle() {
    if (!this.canAutoTitle() || this.header.titling) return;
    post({ cmd: 'autoTitle', conversationId: this.id });
  }

  openMoreMenu() {
    const hd = this.header;
    if (!hd) return;
    // 〔同步〕副本（其他電腦的對話）只留「在新視窗開啟」：不能瀏覽檔案、刪除（D22）；桌面遠端操作能用時可以改名
    const replica = this.isReplica();
    const items = [];
    if (!hd.isDraft && !this.settingsLocked()) items.push({ label: '重新命名', icon: 'rename', hint: '雙擊標題', onSelect: () => this.startRename() });
    items.push({ label: '在新視窗開啟', icon: 'popout', disabled: !!hd.isDraft, title: hd.isDraft ? POPOUT_DRAFT_HINT : null, onSelect: () => post({ cmd: 'popout', conversationId: this.id }) });
    // 〔多視窗〕把分頁搬到一個新的完整視窗（像 Chrome 的「將分頁移至新視窗」；也可以直接把分頁拖到視窗外）：草稿也行，輸入框的字一起帶過去
    if (!isHub && state.mode === 'main') {
      items.push({ label: '移到新視窗', icon: 'appWindow', desc: '也可以把分頁拖到視窗外', onSelect: () => post({ cmd: 'moveToNewWindow', conversationId: this.id, text: this.composer.ta.value }) });
    }
    if (!this.readOnlyReplica()) items.push({ label: this.drawer && this.drawer.isOpen ? '關閉檔案瀏覽器' : '瀏覽工作資料夾', icon: 'panelRight', onSelect: () => this.toggleDrawer() });
    // 延伸成新對話（forkConversation）：複製到目前為止的內容，交給新的代理在旁邊的窗格接著做別的事；只有桌面這台的正式對話（瀏覽器版是 desktop_only）
    if (this.canFork()) items.push({ label: '延伸成新對話', icon: 'gitBranch', desc: '複製目前的內容，由新的代理在旁邊的窗格接著做', onSelect: () => this.ws.forkConversation(this.id) });
    // 多人協作：擁有者把這個對話分享給協作人（設定頁有協作人的清單才有：桌面要已配對）
    if (!hd.isDraft && canManageCollaborators()) {
      const n = sharedWith(state.collaborators, this.id).length;
      items.push({ label: '協作人…', icon: 'user', desc: n ? `已分享給 ${n} 位` : '分享給協作人，讓他在網頁版和代理對話', onSelect: () => this.openShare() });
    }
    // 〔同步〕桌面本機對話的「同步到伺服器」（D25；header.conversationSync 的 choices，目前的打勾；權杖暫停時沒有打勾的）
    const choices = !isHub && !hd.isDraft && !replica ? policyChoices(hd.conversationSync) : [];
    if (choices.length) {
      items.push({ type: 'sep' });
      items.push({ type: 'header', label: hd.conversationSync.policy === 'paused' ? '同步到伺服器（偵測到權杖，已暫停）' : '同步到伺服器' });
      for (const c of choices) items.push({ label: c.label, checked: c.current, onSelect: () => this.setSyncPolicy(c.value) });
    }
    if (hd.isDraft) {
      items.push({ type: 'sep' });
      items.push({ label: '捨棄草稿', icon: 'x', onSelect: () => this.ws.closeTab(this.id) });
    } else if (!this.settingsLocked()) {
      // 〔桌面遠端操作〕副本的刪除在那台移到回收區（同瀏覽器版）
      items.push({ type: 'sep' });
      items.push({ label: '刪除對話…', icon: 'trash', danger: true, onSelect: () => this.confirmDelete() });
    }
    openMenu(this.moreBtn, items, { placement: 'bottom-end', minWidth: 200 });
  }

  /** 多人協作：分享給協作人的對話框（collab.js）；這台的對話沒有完整同步到伺服器時提醒協作人看不到內容 */
  openShare() {
    const hd = this.header;
    if (!hd) return;
    const policy = hd.conversationSync && hd.conversationSync.policy;
    const hint = !isHub && !this.isReplica() && policy && policy !== 'full'
      ? '這個對話的「同步到伺服器」不是完整同步，協作人看不到內容；要分享請先在「⋯」選單改成完整同步。' : null;
    openShareDialog(this.id, hd.title, hint);
  }

  /** 多人協作：標頭的「協作」晶片（擁有者看：分享給幾位，點了改分享；協作人不顯示） */
  renderCollabChip() {
    if (!this.collabChip) return;
    const shared = !isCollaborator() && state.collaborators ? sharedWith(state.collaborators, this.id) : [];
    this.collabChip.hidden = shared.length === 0;
    if (!shared.length) return;
    const label = this.collabChip.querySelector('.chip-label');
    const text = shared.length === 1 ? shared[0].displayName : `${shared.length} 位協作人`;
    if (label.textContent !== text) label.textContent = text;
    this.collabChip.title = '分享給：' + shared.map((c) => c.displayName).join('、') + '（點了修改）';
  }

  /**
   * 〔同步〕改這個對話的同步政策（syncSetConversationPolicy，只在這台改，D25）：和目前一樣不送；
   * 改成「只留在這台」會從伺服器刪除，先確認（LOCAL_ONLY_CONFIRM_TEXT）。結果由宿主送新的標頭
   */
  async setSyncPolicy(policy) {
    const cs = this.header && this.header.conversationSync;
    if (!cs || policy === cs.policy) return;
    if (needsLocalOnlyConfirm(cs.policy, policy)) {
      const r = await confirmDialog({ title: '只留在這台？', message: LOCAL_ONLY_CONFIRM_TEXT + '。', confirmLabel: '只留在這台', danger: true });
      if (!r.ok) return;
    }
    post({ cmd: 'syncSetConversationPolicy', conversationId: this.id, policy });
  }

  confirmDelete() {
    confirmDeleteConversation(this.id, this.header || summaryOf(this.id) || {});
  }

  ensureDrawer() {
    if (this.drawer) return;
    this.drawer = new FileDrawer(this);
    this.main.append(this.drawer.el);
  }

  toggleDrawer(force) {
    // 〔同步〕副本的檔案在其他電腦上：不開（已開著的照樣可以關）
    if (this.readOnlyReplica() && !(this.drawer && this.drawer.isOpen)) return;
    this.ensureDrawer();
    const open = force === undefined ? !this.drawer.isOpen : !!force;
    if (open) this.drawer.open(); else this.drawer.close();
    this.drawerBtn.classList.toggle('on', open);
    this.drawerBtn.setAttribute('aria-pressed', String(open));
  }

  hasWaiting() {
    for (const it of this.items.values()) if (it.status === 'Waiting' && (it.kind === 'Question' || it.kind === 'Approval' || it.kind === 'PlanApproval')) return true;
    return false;
  }

  restoreText(text) { this.composer.restoreText(text); }

  /** 〔遠端〕copyItem 的結果（liveview）：寫進剪貼簿了標「已複製」；沒寫進去（開了退路框）、被拒或等不到時回到「複製」 */
  markCopied(itemId, ok = true) { this.flushChunks(); markItemCopied(this.nodes.get(itemId), ok); }
}

function tip(iconName, key, text) {
  return h('div', { class: 'draft-tip' }, icon(iconName), h('kbd', { text: key }), h('span', { text }));
}

/**
 * 刪除對話前的確認（標題列「更多」選單與側欄共用）；info 是 Header 或 ConversationSummary（都有 title、running、customFolder）。
 * 文字照 liverules.deleteConfirmText（D43）：桌面這台的對話「無法復原」（沒有自選資料夾時連預設工作區的檔案一起刪，要明講）；
 * 瀏覽器版是「會移到 OFFICE-PC 的回收區，30 天內可在那台電腦的設定還原」，並追蹤結果（等結果時畫面標成刪除中，被拒或不明時恢復）。
 * 副本與草稿不提供刪除
 */
/**
 * 對話清單變了（子對話開始、等你回應、結束、改名）：各對話裡帳戶委派的工具卡重畫子對話的狀態（items.paintDelegation）、
 * 標頭的來源晶片跟著主對話與原對話的標題（renderOrigin）；都是沒變不動
 */
export function refreshListLinks() {
  for (const v of views.values()) {
    if (v.originChip) v.renderOrigin();
    if (!v.itemsEl) continue;
    for (const n of v.itemsEl.querySelectorAll('.it-tool.t-deleg')) paintDelegation(n, v.ctx);
  }
}

export async function confirmDeleteConversation(id, info) {
  const mode = isHub ? 'hub' : 'desktop';
  const origin = originOf(info, mode);
  const dev = isHub ? deviceOf(info && info.deviceId, state.remote, info) : null;
  const text = deleteConfirmText(mode, origin, dev ? dev.name : null, info);
  if (!text) return;
  // 桌面這台的對話還有另開的程式在跑：勾選要不要一併停止（預設勾；不停止的話刪除後在 Myriagent 裡就看不到了）
  const checkbox = deleteProgramsCheckbox(mode, origin, info);
  const r = await confirmDialog({ title: '刪除這個對話？', message: text.message, detail: text.detail, confirmLabel: '刪除', danger: true, checkbox });
  if (!r.ok) return;
  const cid = post(checkbox && r.checked ? { cmd: 'delete', conversationId: id, stopPrograms: true } : { cmd: 'delete', conversationId: id });
  if (typeof cid !== 'string') return;
  const mark = (on) => { const v = views.get(id); if (v) v.root.classList.toggle('deleting', on); };
  mark(true);
  // 完成時伺服器送 conversationClosed（分頁關掉）；被拒（伺服器已提示）或結果不明時恢復
  track(cid, { onSettle: (result) => { if (result !== 'done') mark(false); } });
}

/** 文字沒變就不寫（textContent 會換掉文字節點：插入、移除節點都要重算樣式與版面） */
function setTextIfChanged(el, text) {
  if (el.textContent !== text) el.textContent = text;
}

/** title 沒變就不寫 */
function setTitle(el, title) {
  if (el.title !== title) el.title = title;
}
