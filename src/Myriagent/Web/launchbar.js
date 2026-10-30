// 執行列（宿主的 launchStatus／launchLog；只有桌面這台的對話）：工作資料夾是視窗程式專案時，標題列第二行（git 晶片後面）的
// 建置並執行、暫停／繼續、停止、重新建置並執行，選目標的晶片、執行模式（Debug／Release）的晶片、狀態與「輸出」鈕；輸出面板另外掛在輸入區上方（outEl）。
// 規則在 launchrules.js，文字一律 textContent
import { h, copyToClipboard, replayClass } from './util.js';
import { icon } from './icons.js';
import { post } from './store.js';
import { openMenu, toast, spinner, hasLayer } from './ui.js';
import {
  phaseOf, selectedTarget, launchButtons, buttonTitle, keyAction, statusView, failureKey, targetTitle, targetDesc, launchKey,
  mergeLog, needsLog, logRequest, errorLines, fixPrompt, LOG_CAPACITY, buildDiagnostics, isWarningLine, MODES, modeOf, modeLabel, modeNote, modeChip,
} from './launchrules.js';
import { launchFx } from './fxrules.js';

const BUTTONS = ['start', 'pause', 'stop', 'restart'];
/** 換階段時閃一下的長度（fx.css 的 .lbar.fx-l-*；餘暉 1.2 秒最長） */
const LAUNCH_FX_MS = 1300;
/** 輸出捲到離底部這麼近就當成貼底（新的輸出進來時跟著捲） */
const LOG_STICK = 24;

export class LaunchBar {
  /** conv：ConvView（id、composer、root） */
  constructor(conv) {
    this.conv = conv;
    this.launch = null;
    this.rendered = null;
    this.log = null;          // launchrules.mergeLog 的狀態
    this.logWaiting = false;  // 送了 launchLog、還沒回來
    this.outOpen = false;
    this.failShown = null;    // 自動打開過輸出面板的那次失敗（failureKey）
    this.wantLog = false;     // 一次建置剛結束：面板關著也拿一次輸出，只為了算錯誤與警告數
    this.diag = null;         // 建置的錯誤與警告數（launchrules.buildDiagnostics）
    this.build();
  }

  build() {
    this.btns = {};
    for (const name of BUTTONS) {
      const b = h('button', { type: 'button', class: 'lbtn', dataset: { act: name } }, icon(name === 'restart' ? 'refresh' : name === 'start' ? 'play' : name));
      b.addEventListener('click', () => this.act(name));
      this.btns[name] = b;
    }
    this.btnGroup = h('div', { class: 'lbar-btns', role: 'toolbar', 'aria-label': '執行列' }, ...BUTTONS.map((n) => this.btns[n]));
    this.nameEl = h('span', { class: 'chip-label lbar-name' });
    this.targetBtn = h('button', { type: 'button', class: 'chip lbar-target', 'aria-haspopup': 'menu' }, icon('appWindow', 'chip-ic'), this.nameEl, icon('chevDown', 'chip-chev'));
    this.targetBtn.addEventListener('click', () => this.openTargetMenu());
    // 執行模式（Debug：跟著 app 關閉；Release：獨立執行）：每個專案各自記得，下一次啟動才套用
    // 很窄的窗格只留第一個字母（D／R，chat.css）；完整名稱與說明在滑過的說明
    this.modeLabelEl = h('span', { class: 'chip-label lbar-mode-full' });
    this.modeShortEl = h('span', { class: 'lbar-mode-short', 'aria-hidden': 'true' });
    this.modeBtn = h('button', { type: 'button', class: 'chip lbar-mode', 'aria-haspopup': 'menu' }, this.modeLabelEl, this.modeShortEl);
    this.modeBtn.addEventListener('click', () => this.openModeMenu());
    this.statusSpin = spinner('lbar-spin');
    this.statusText = h('span', { class: 'lbar-status-text' });
    this.statusEl = h('span', { class: 'lbar-status', role: 'status' }, this.statusSpin, this.statusText);
    // 建置的錯誤與警告數（✕ 3 ⚠ 12）：點了打開輸出、跳到第一個錯誤
    this.diagErr = h('span', { class: 'ld-err' });
    this.diagWarn = h('span', { class: 'ld-warn' });
    this.diagEl = h('button', { type: 'button', class: 'lbar-diag', hidden: true }, this.diagErr, this.diagWarn);
    this.diagEl.addEventListener('click', () => this.openDiag());
    this.outBtn = h('button', { type: 'button', class: 'lbar-out', 'aria-expanded': 'false', title: '建置與執行的輸出' }, icon('terminal'), h('span', { class: 'lbar-out-label', text: '輸出' }));
    this.outBtn.addEventListener('click', () => this.toggleOutput());
    // 一列（el）：放在標題列第二行、git 晶片後面，「輸出」鈕靠右（對齊第一行右邊的按鈕）
    this.el = h('div', { class: 'lbar', hidden: true, dataset: { field: 'launch' } }, this.btnGroup, this.targetBtn, this.modeBtn, this.statusEl, this.diagEl, h('span', { class: 'grow' }), this.outBtn);

    // 輸出面板（outEl）：由 ConvView 放在輸入區上方，建置失敗時「把錯誤貼到輸入框」就在輸入框旁邊
    this.outTitle = h('span', { class: 'lout-title' });
    this.fixBtn = h('button', { type: 'button', class: 'btn sm lout-fix', hidden: true, title: '把建置錯誤貼到這個對話的輸入框，請代理修正' }, icon('sparkle'), h('span', { text: '把錯誤貼到輸入框' }));
    this.fixBtn.addEventListener('click', () => this.pasteErrors());
    this.copyBtn = h('button', { type: 'button', class: 'ibtn sm', title: '複製輸出', 'aria-label': '複製輸出' }, icon('copy'));
    this.copyBtn.addEventListener('click', () => this.copyOutput());
    this.closeBtn = h('button', { type: 'button', class: 'ibtn sm', title: '收起輸出', 'aria-label': '收起輸出' }, icon('x'));
    this.closeBtn.addEventListener('click', () => this.toggleOutput(false));
    this.outBody = h('div', { class: 'lout-body', tabIndex: 0, 'aria-label': '輸出' });
    this.outEmpty = h('div', { class: 'lout-empty', text: '還沒有輸出' });
    this.outEl = h('div', { class: 'lout', hidden: true, dataset: { field: 'launchOutput' } },
      h('div', { class: 'lout-head' }, this.outTitle, h('span', { class: 'grow' }), this.fixBtn, this.copyBtn, this.closeBtn),
      this.outEmpty, this.outBody);
    this.onKey = (e) => this.onKeyDown(e);
    this.conv.root.addEventListener('keydown', this.onKey);
  }

  destroy() {
    this.conv.root.removeEventListener('keydown', this.onKey);
    clearTimeout(this.diagTimer);
    clearTimeout(this.logTimer);
  }

  // ───── 宿主資料 ─────
  /** launchStatus 的 LaunchView（null＝沒有可以執行的專案：藏起來） */
  set(launch) {
    this.launch = launch && typeof launch === 'object' && Array.isArray(launch.targets) && launch.targets.length ? launch : null;
    this.render();
    if (!this.launch) return;
    // 建置失敗、無法啟動：打開輸出面板（同一次失敗只開一次，使用者收起之後不再打開）
    const fail = failureKey(this.launch);
    if (fail && fail !== this.failShown) {
      this.failShown = fail;
      this.toggleOutput(true);
    }
    if (this.outOpen || this.wantLog) this.requestLog();
  }

  render() {
    const key = launchKey(this.launch);
    if (key !== this.rendered) {
      this.rendered = key;
      this.el.hidden = !this.launch;
      // 標題列擠回一行（.short-head）時執行列換到下一行（chat.css）；不用 :has()，免得整個窗格的樣式跟著重算
      this.conv.root.classList.toggle('with-launch', !!this.launch);
      if (!this.launch) { this.prevPhase = undefined; this.toggleOutput(false); return; }
      const t = selectedTarget(this.launch);
      const phase = phaseOf(this.launch);
      // 換階段時閃一下（fxrules.launchFx）：和 data-phase 同一次改，停下的餘暉才是從原本的顏色開始淡
      const fx = launchFx(this.prevPhase, phase, this.launch.last && this.launch.last.kind);
      const wasPhase = this.prevPhase;
      this.prevPhase = phase;
      // 開始建置：上一次的錯誤與警告數收起來；建置結束（成功或失敗）：拿一次輸出來算
      if (phase === 'building') { this.diag = null; this.paintDiag(); }
      else if (wasPhase === 'building') this.wantLog = true;
      if (fx) {
        for (const k of ['ok', 'err', 'pause', 'cool']) if (k !== fx) this.el.classList.remove('fx-l-' + k); // 上一個還沒播完的讓位
        replayClass(this.el, 'fx-l-' + fx, LAUNCH_FX_MS);
      }
      this.el.dataset.phase = phase;
      const bs = launchButtons(phase);
      for (const name of BUTTONS) {
        const b = this.btns[name];
        const spec = bs[name];
        b.disabled = !spec.enabled;
        b.title = buttonTitle(spec);
        b.setAttribute('aria-label', spec.label);
        // 暫停中第一顆是「繼續」：圖示不變（▷），說明與讀屏跟著換
        b.dataset.action = spec.action;
      }
      this.nameEl.textContent = t ? t.name || '' : '';
      this.targetBtn.title = targetTitle(t) + (this.launch.targets.length > 1 ? '\n（點一下換別的專案）' : '');
      this.targetBtn.setAttribute('aria-label', '執行的專案：' + (t ? t.name || '' : ''));
      const mc = modeChip(this.launch);
      this.modeLabelEl.textContent = mc.label;
      this.modeShortEl.textContent = mc.label.charAt(0);
      this.modeBtn.title = mc.title;
      this.modeBtn.dataset.mode = mc.mode;
      this.modeBtn.setAttribute('aria-label', '執行模式：' + mc.label);
      this.outTitle.textContent = '輸出' + (t ? ' — ' + (t.name || '') : '');
      this.renderFix();
    }
    this.tick(Date.now());
  }

  /** 每秒（只在可見時）：狀態的經過時間 */
  tick(now) {
    if (!this.launch) return;
    const v = statusView(this.launch, now);
    this.statusEl.dataset.tone = v.tone;
    this.statusSpin.hidden = !v.spin;
    if (this.statusText.textContent !== v.text) this.statusText.textContent = v.text;
    if (this.statusEl.title !== v.title) this.statusEl.title = v.title;
    this.statusEl.hidden = !v.text;
  }

  // ───── 按鈕與快捷鍵 ─────
  act(name) {
    if (!this.launch) return;
    const spec = launchButtons(phaseOf(this.launch))[name];
    if (!spec || !spec.enabled) return;
    this.send(spec.action);
  }

  send(action) {
    const t = selectedTarget(this.launch);
    if (!t) return;
    post({ cmd: 'launchAction', conversationId: this.conv.id, targetId: t.id, action });
  }

  /** F5／Shift+F5／Ctrl+Shift+F5：焦點在這個窗格裡、執行列看得到、沒有對話框或選單時 */
  onKeyDown(e) {
    if (!this.launch || this.el.hidden || e.defaultPrevented || e.repeat || e.isComposing || hasLayer()) return;
    const action = keyAction(e, phaseOf(this.launch));
    if (!action) {
      // 不能做的時候也吃掉 F5：不讓 WebView2 開了開發者工具時重新載入頁面
      if (e.key === 'F5') e.preventDefault();
      return;
    }
    e.preventDefault();
    this.send(action);
  }

  openTargetMenu() {
    if (!this.launch) return;
    const items = [{ type: 'header', label: '要執行的專案' }];
    for (const t of this.launch.targets) {
      items.push({
        label: t.name || '', desc: targetDesc(t), icon: 'appWindow', checked: t.id === this.launch.selected, title: t.path || '',
        onSelect: () => { if (t.id !== this.launch.selected) post({ cmd: 'launchSelect', conversationId: this.conv.id, targetId: t.id }); },
      });
    }
    items.push({ type: 'sep' });
    items.push({ label: '重新偵測專案', icon: 'refresh', onSelect: () => post({ cmd: 'launchRescan', conversationId: this.conv.id }) });
    items.push({ type: 'custom', node: h('div', { class: 'lbar-note', text: '啟動會先建置（dotnet build）再執行；選的專案與每個專案的執行模式會記住，重新開啟 Clode Myriagent 後照舊。Debug 模式的程式跟著 Clode Myriagent 關閉，Release 模式獨立執行（重新開啟後接回）。快捷鍵：F5 啟動／繼續、Shift+F5 停止、Ctrl+Shift+F5 重新建置並執行。' }) });
    openMenu(this.targetBtn, items, { minWidth: 300, className: 'menu-launch' });
  }

  /** 執行模式選單：Debug／Release（選了就記在這個專案上，執行中的下一次啟動或重新建置並執行才套用） */
  openModeMenu() {
    if (!this.launch) return;
    const t = selectedTarget(this.launch);
    if (!t) return;
    const current = modeOf(this.launch);
    const items = [{ type: 'header', label: '執行模式' }];
    for (const mode of MODES) {
      items.push({
        label: modeLabel(mode), desc: modeNote(mode, t.kind), checked: mode === current,
        onSelect: () => { if (mode !== current) post({ cmd: 'launchSelect', conversationId: this.conv.id, targetId: t.id, mode }); },
      });
    }
    if (phaseOf(this.launch) !== 'idle') {
      items.push({ type: 'custom', node: h('div', { class: 'lbar-note', text: '換模式之後，下次啟動或按「重新建置並執行」時套用。' }) });
    }
    openMenu(this.modeBtn, items, { minWidth: 300, className: 'menu-launch-mode' });
  }

  // ───── 輸出面板 ─────
  toggleOutput(open) {
    const next = open === undefined ? !this.outOpen : !!open;
    if (next && !this.launch) return;
    this.outOpen = next;
    this.outEl.hidden = !next;
    this.outBtn.setAttribute('aria-expanded', String(next));
    this.outBtn.classList.toggle('on', next);
    if (next) {
      this.requestLog();
      this.scrollLogToEnd();
    }
  }

  requestLog() {
    if (!(this.outOpen || this.wantLog) || this.logWaiting || !this.launch) return;
    const t = selectedTarget(this.launch);
    if (!t || !needsLog(this.log, this.launch)) { this.wantLog = false; this.scheduleDiag(); return; }
    const req = logRequest(this.log, t.id);
    this.logWaiting = true;
    post({ cmd: 'launchLog', conversationId: this.conv.id, targetId: t.id, epoch: req.epoch, after: req.after });
    // 宿主沒回（對話關了、宿主換版）時不一直卡著
    clearTimeout(this.logTimer);
    this.logTimer = setTimeout(() => { this.logWaiting = false; }, 5000);
  }

  /** 宿主的 launchLog */
  applyLog(msg) {
    this.logWaiting = false;
    clearTimeout(this.logTimer);
    const t = selectedTarget(this.launch);
    if (!msg || !t || msg.targetId !== t.id) { this.requestLog(); return; }
    const prev = this.log;
    const stick = this.isLogAtEnd();
    this.log = mergeLog(prev, msg);
    const appended = prev && prev.targetId === this.log.targetId && prev.epoch === this.log.epoch && !msg.reset;
    const fresh = Array.isArray(msg.lines) ? msg.lines.filter((l) => typeof l === 'string') : [];
    if (appended && prev.lines.length + fresh.length <= LOG_CAPACITY) this.outBody.append(...fresh.map(lineNode));
    else this.outBody.replaceChildren(...this.log.lines.map(lineNode));
    this.outEmpty.hidden = this.log.lines.length > 0;
    this.wantLog = false;
    this.scheduleDiag();
    this.renderFix();
    if (stick || !appended) this.scrollLogToEnd();
    // 等回覆的期間又有新的輸出
    this.requestLog();
  }

  isLogAtEnd() {
    const b = this.outBody;
    return b.scrollHeight - b.scrollTop - b.clientHeight <= LOG_STICK;
  }

  scrollLogToEnd() {
    requestAnimationFrame(() => { this.outBody.scrollTop = this.outBody.scrollHeight; });
  }

  /** 錯誤與警告數最多每 400 毫秒算一次（程式一直在輸出時 launchLog 一直來） */
  scheduleDiag() {
    if (this.diagTimer) return;
    this.diagTimer = setTimeout(() => {
      this.diagTimer = 0;
      if (!this.launch || phaseOf(this.launch) === 'building') return;
      this.diag = this.log ? buildDiagnostics(this.log.lines) : null;
      this.paintDiag();
    }, 400);
  }

  paintDiag() {
    const d = this.diag;
    const show = !!d && (d.errors > 0 || d.warnings > 0);
    this.diagEl.hidden = !show;
    if (!show) return;
    this.diagErr.hidden = !d.errors;
    this.diagWarn.hidden = !d.warnings;
    this.diagErr.textContent = String(d.errors);
    this.diagWarn.textContent = String(d.warnings);
    this.diagEl.title = (d.errors ? d.errors + ' 個錯誤' : '') + (d.errors && d.warnings ? '、' : '') + (d.warnings ? d.warnings + ' 個警告' : '')
      + '（點一下打開輸出、跳到第一個' + (d.errors ? '錯誤' : '警告') + '）';
    this.diagEl.setAttribute('aria-label', this.diagEl.title);
  }

  /** 點了錯誤與警告數：打開輸出面板，捲到第一個錯誤（沒有錯誤就第一個警告）並閃一下 */
  openDiag() {
    const d = this.diag;
    this.toggleOutput(true);
    if (!d || d.first < 0) return;
    // toggleOutput 會在下一個影格捲到最後：再下一個影格才捲到那一行
    requestAnimationFrame(() => requestAnimationFrame(() => {
      const b = this.outBody;
      const row = b.children[d.first];
      if (!row) return;
      b.scrollTop += row.getBoundingClientRect().top - b.getBoundingClientRect().top - b.clientHeight / 3;
      replayClass(row, 'fx-hit', 900);
    }));
  }

  renderFix() {
    const last = this.launch && this.launch.last;
    const failed = !!last && last.kind === 'buildFailed' && phaseOf(this.launch) === 'idle';
    this.fixBtn.hidden = !failed;
  }

  pasteErrors() {
    const text = fixPrompt(this.launch, this.log ? this.log.lines : []);
    if (!text) return;
    // 接在輸入框原有文字的最後面（另起一段）
    const c = this.conv.composer;
    const kept = c.ta.value.replace(/\s+$/, '');
    c.ta.value = kept ? kept + '\n\n' : '';
    c.ta.setSelectionRange(c.ta.value.length, c.ta.value.length);
    c.insertText(text);
  }

  async copyOutput() {
    const text = this.log ? this.log.lines.join('\n') : '';
    if (!text) return;
    if (await copyToClipboard(text, (t) => post({ cmd: 'copyText', text: t }))) toast('已複製輸出', 'success');
  }
}

/** 一行輸出：錯誤行（errorLines 認得的）與指令行（「> 」開頭）各自標示 */
function lineNode(line) {
  const kind = line.startsWith('> ') ? 'cmd' : errorLines([line]).length ? 'error' : isWarningLine(line) ? 'warn' : '';
  return h('div', { class: 'lout-line', dataset: kind ? { kind } : null, text: line || ' ' });
}
