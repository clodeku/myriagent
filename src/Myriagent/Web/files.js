// 檔案瀏覽器抽屜：麵包屑、上一層、重新整理、格狀／清單、篩選、縮圖、右鍵選單、拖曳調寬
// 〔遠端〕瀏覽器版（WP4.6 步 11）：檔案以 fileFetch 預覽或下載（D34）、沒有「在檔案總管顯示」與「加入附件」；
// 「瀏覽資料夾…」的挑選模式 pick({onPick})：listFiles 帶 dirsOnly、只列資料夾，最上層是那台的最近資料夾（D33）；
// 新對話（伺服器草稿）也可以：伺服器把它轉給草稿選的那台（listFolders）
import { h, iconButton, fmtBytes, fmtRelative, relativeTo, copyToClipboard, clamp, fadeInImg, replayClass } from './util.js';
import { LIST_ENTRANCE_MAX, LIST_STAGGER_MS, sizeFrac, recentlyModified } from './fxrules.js';
import { icon, FILE_ICON } from './icons.js';
import { post, nextRequestId, isHub } from './store.js';
import { controllable, listFailText } from './liverules.js';
import { openMenu, toast, spinner } from './ui.js';
import { openLightbox, openVideo, openDocument } from './viewer.js';
import { docPreviewKind } from './filelinkrules.js';
import { downloadUrl } from './mediamenu.js';
import { track } from './cmdtrack.js';

let lastWidth = 340;
let lastMode = 'grid';
/** 〔遠端〕listFiles 的結果不明（重連、過期）：files 等不到了 */
const LIST_UNKNOWN_TEXT = '沒有收到那台電腦的回覆，請按重新整理再試一次';

export class FileDrawer {
  constructor(conv) {
    this.conv = conv;
    this.isOpen = false;
    this.path = null;
    this.root = conv.header ? conv.header.workingDirectory : null;
    this.parent = null;
    this.entries = [];
    this.truncated = false;
    this.error = null;
    this.loading = false;
    this.reqId = null;
    this.mode = lastMode;
    this.filter = '';
    this.loadedOnce = false;
    // 〔遠端〕挑選模式（「瀏覽資料夾…」）：{ onPick(path) }；null＝一般的檔案瀏覽。pickRoots：最上層列出的資料夾（可以往下挑的範圍）
    this.picking = null;
    this.pickRoots = [];
    this.build();
  }

  build() {
    this.crumbs = h('nav', { class: 'dr-crumbs', 'aria-label': '路徑' });
    this.upBtn = iconButton('up', '上一層', () => this.goUp());
    this.refreshBtn = iconButton('refresh', '重新整理', () => this.list(this.path));
    this.modeBtn = iconButton(this.mode === 'grid' ? 'list' : 'grid', '', () => this.setMode(this.mode === 'grid' ? 'list' : 'grid'));
    this.closeBtn = iconButton('x', '關閉檔案瀏覽器', () => this.conv.toggleDrawer(false));
    this.filterInput = h('input', { type: 'search', class: 'input dr-filter', placeholder: '篩選這個資料夾', autocomplete: 'off', spellcheck: 'false', 'aria-label': '篩選檔案' });
    this.filterInput.addEventListener('input', () => { this.filter = this.filterInput.value; this.renderEntries(); });
    this.filterInput.addEventListener('keydown', (e) => { if (e.key === 'Escape' && this.filterInput.value) { e.stopPropagation(); this.filterInput.value = ''; this.filter = ''; this.renderEntries(); } });
    this.body = h('div', { class: 'dr-body' });
    this.foot = h('div', { class: 'dr-foot' });
    this.handle = h('div', { class: 'dr-resize', title: '拖曳調整寬度', role: 'separator', 'aria-orientation': 'vertical' });
    this.wRead = h('span', { class: 'dr-wread', 'aria-hidden': 'true' });
    this.titleEl = h('span', { class: 'dr-title', text: '檔案' });
    // 〔遠端〕挑選模式的底列：目前的資料夾、「取消」「選這個資料夾」
    this.pickPath = h('span', { class: 'dr-pick-path', dataset: { field: 'pickPath' } });
    this.pickBtn = h('button', { type: 'button', class: 'btn sm primary', dataset: { field: 'pickFolder' }, text: '選這個資料夾' });
    this.pickBtn.addEventListener('click', () => this.choosePicked());
    const pickCancel = h('button', { type: 'button', class: 'btn sm', dataset: { field: 'pickCancel' }, text: '取消' });
    pickCancel.addEventListener('click', () => this.conv.toggleDrawer(false));
    this.pickBar = h('div', { class: 'dr-pick', hidden: true, dataset: { field: 'pickBar' } }, this.pickPath, h('span', { class: 'dr-pick-acts' }, pickCancel, this.pickBtn));
    this.el = h('aside', { class: 'drawer', 'aria-label': '檔案瀏覽器' },
      this.handle,
      h('div', { class: 'dr-head' }, this.titleEl, h('span', { class: 'grow' }), this.upBtn, this.refreshBtn, this.modeBtn, this.closeBtn),
      this.crumbs,
      h('div', { class: 'dr-tools' }, icon('filter', 'dr-filter-ic'), this.filterInput),
      this.body, this.pickBar, this.foot);
    this.el.style.width = lastWidth + 'px';
    this.el.append(this.wRead);
    this.updateModeBtn();
    this.bindResize();
    this.body.addEventListener('click', (e) => {
      // 單擊就開啟：雙擊的第二下（detail=2）會落在剛列出的新清單上，不能再開一次同位置的項目
      if (e.detail > 1) return;
      const n = e.target.closest('.fe');
      if (n) this.activate(this.entryOf(n));
    });
    this.body.addEventListener('contextmenu', (e) => {
      const n = e.target.closest('.fe');
      if (!n) return;
      e.preventDefault();
      if (this.picking) return; // 挑選模式只有打開資料夾
      this.entryMenu(this.entryOf(n), { x: e.clientX, y: e.clientY });
    });
    this.body.addEventListener('keydown', (e) => {
      const n = e.target.closest('.fe');
      if (!n) return;
      if (e.key === 'Enter') { e.preventDefault(); this.activate(this.entryOf(n)); }
      else if (e.key === 'ContextMenu' || (e.key === 'F10' && e.shiftKey)) {
        e.preventDefault();
        if (this.picking) return;
        const r = n.getBoundingClientRect();
        this.entryMenu(this.entryOf(n), { x: r.left + 24, y: r.bottom - 4 });
      } else if (e.key === 'Backspace' && this.canGoUp()) { e.preventDefault(); this.goUp(); }
    });
  }

  /** 上一層：有 parent 就去；挑選模式在最近資料夾之下、沒有 parent 時回到最近資料夾的清單 */
  canGoUp() {
    return !!this.parent || (!!this.picking && !!this.path);
  }

  goUp() {
    if (this.parent) this.list(this.parent);
    else if (this.picking && this.path) this.list(null);
  }

  /**
   * 〔遠端〕挑選模式（D33）：從那台的最近資料夾開始、只列資料夾；「選這個資料夾」交給 onPick(目前的資料夾)。
   * 關掉抽屜或改回一般瀏覽（endPick）就結束
   */
  pick({ onPick }) {
    this.picking = { onPick };
    this.el.classList.add('picking');
    this.titleEl.textContent = '選擇工作資料夾';
    this.resetListing();
    this.renderPickBar();
    if (this.isOpen) this.list(null);
  }

  endPick() {
    if (!this.picking) return;
    this.picking = null;
    this.el.classList.remove('picking');
    this.titleEl.textContent = '檔案';
    this.pickBar.hidden = true;
    this.resetListing();
    if (this.isOpen) this.list(null);
  }

  resetListing() {
    // 還在路上的回應不再採用（挑選模式的 dirsOnly 清單不能落進一般瀏覽，反之亦然）
    this.reqId = null;
    this.loading = false;
    this.path = null;
    this.parent = null;
    this.entries = [];
    this.error = null;
    this.truncated = false;
    this.loadedOnce = false;
    this.filter = '';
    this.filterInput.value = '';
    this.pickRoots = [];
  }

  renderPickBar() {
    if (!this.picking) { this.pickBar.hidden = true; return; }
    this.pickBar.hidden = false;
    const ready = !!this.path && !this.loading && !this.error;
    this.pickBtn.disabled = !ready;
    const text = this.path || '打開要用的資料夾，再按「選這個資料夾」';
    this.pickPath.textContent = text;
    this.pickPath.title = text;
  }

  choosePicked() {
    if (!this.picking || !this.path || this.loading || this.error) return;
    const path = this.path;
    const { onPick } = this.picking;
    this.conv.toggleDrawer(false); // close() 會結束挑選模式
    onPick(path);
  }

  entryOf(node) { return this.shown[Number(node.dataset.i)]; }

  /** 工作資料夾換了：開著就重新列出新的工作資料夾，關著就等下次打開再列 */
  setRoot(root) {
    if (!root || root === this.root) return;
    const changed = !!this.root;
    this.root = root;
    if (!changed) return;
    this.loadedOnce = false;
    this.path = null;
    if (this.isOpen) this.list(null);
  }

  open() {
    this.isOpen = true;
    this.el.classList.add('open');
    this.conv.root.classList.add('drawer-open');
    // 挑選模式剛開始、已經在列（pick 時抽屜就開著）：不再送一次
    if (!this.loadedOnce && !(this.picking && this.loading)) this.list(null);
    setTimeout(() => { if (this.isOpen) this.filterInput.focus({ preventScroll: true }); }, 60);
  }

  close() {
    this.isOpen = false;
    this.el.classList.remove('open');
    this.conv.root.classList.remove('drawer-open');
    this.endPick();
  }

  list(path) {
    const reqId = nextRequestId('f');
    this.reqId = reqId;
    this.loading = true;
    this.renderLoading();
    if (this.picking) this.renderPickBar();
    const msg = { cmd: 'listFiles', requestId: reqId, conversationId: this.conv.id, path: path || null };
    if (this.picking) msg.dirsOnly = true;
    const cid = post(msg);
    // 〔遠端〕瀏覽器版：沒送出（沒有連線）或被拒（政策、不在範圍、電腦離線；原因看伺服器的提示）時不再等 files。
    // 結果不明（重連後的 init、150 秒過期、電腦逾時）：files 只送回發出的那條連線、也不跨 init 保留，等不到了，改成可以重試的說明
    // （合併前審查 LIVE-6、P1；之後真的到了同一個 requestId 的 files，apply 照樣採用）。〔桌面遠端操作〕可以操作的副本同瀏覽器版
    if (!this.remote()) return;
    if (cid === null) this.failList(reqId, '目前沒有連線，連上之後才能瀏覽');
    else if (typeof cid === 'string') {
      track(cid, {
        onSettle: (st, code) => {
          if (st === 'rejected') this.failList(reqId, listFailText(code, !!(msg.dirsOnly && this.conv.header && this.conv.header.isDraft)));
          else if (st === 'unknown') this.failList(reqId, LIST_UNKNOWN_TEXT);
        },
      });
    }
  }

  /** listFiles 等不到 files：還是最新一次請求才改畫面 */
  failList(reqId, text) {
    if (reqId !== this.reqId || !this.loading) return;
    this.loading = false;
    this.loadedOnce = true;
    this.error = text;
    this.renderCrumbs();
    this.renderEntries();
  }

  /** 宿主回 files：只採用最新一次請求的回應 */
  apply(msg) {
    if (msg.requestId !== this.reqId) return;
    // 焦點在項目或麵包屑上（鍵盤操作）時，重建清單會把焦點丟到 body：記下來，列完再放回清單
    const active = document.activeElement;
    const keepFocus = !!active && (this.body.contains(active) || this.crumbs.contains(active));
    const focusedNode = keepFocus ? active.closest('.fe') : null;
    const focusedPath = focusedNode && this.entryOf(focusedNode) ? this.entryOf(focusedNode).path : null;
    const from = this.path;
    this.loading = false;
    this.loadedOnce = true;
    const changedDir = msg.path !== this.path;
    this.path = msg.path;
    this.parent = msg.parent || null;
    this.root = msg.root || this.root;
    // 資料夾在前（宿主已排序；這裡只做穩定的分組，不改變宿主的名稱排序）
    const list = Array.isArray(msg.entries) ? msg.entries : [];
    this.entries = list.filter((e) => e.kind === 'dir').concat(this.picking ? [] : list.filter((e) => e.kind !== 'dir'));
    // 〔遠端〕挑選模式的最上層（path 是 null）是那台的最近資料夾：目前的工作資料夾（root）放第一個，好往下挑子資料夾
    if (this.picking && !msg.path && typeof msg.root === 'string' && msg.root
      && !this.entries.some((e) => typeof e.path === 'string' && e.path.toLowerCase() === msg.root.toLowerCase())) {
      this.entries.unshift({ name: '目前的工作資料夾', path: msg.root, kind: 'dir', current: true });
    }
    // 〔遠端〕挑選模式的最上層：記下可以往下挑的根（麵包屑以它判斷哪些上層可以點）
    if (this.picking && !msg.path) this.pickRoots = this.entries.map((e) => e.path).filter((x) => typeof x === 'string' && x);
    this.truncated = !!msg.truncated;
    this.error = msg.error || null;
    if (changedDir && this.filter) { this.filter = ''; this.filterInput.value = ''; }
    this.renderCrumbs();
    this.renderEntries();
    if (this.picking) this.renderPickBar();
    if (changedDir) this.body.scrollTop = 0;
    if (keepFocus) this.focusAfterList(from, focusedPath);
  }

  /** 回到上層（或從麵包屑跳回祖先）時聚焦剛才所在的資料夾；同一個資料夾聚焦原本的項目；否則聚焦第一個項目 */
  focusAfterList(from, focusedPath) {
    const shown = this.error ? [] : this.shown || [];
    let i = from ? shown.findIndex((e) => e.kind === 'dir' && relativeTo(e.path, from) !== null) : -1;
    if (i < 0 && focusedPath) i = shown.findIndex((e) => relativeTo(e.path, focusedPath) === '');
    // 空資料夾或讀取失敗：沒有項目可聚焦，改放在「上一層」（在最上層時放在篩選框）
    const node = this.body.querySelector('.fe[data-i="' + Math.max(0, i) + '"]') || (this.upBtn.disabled ? this.filterInput : this.upBtn);
    node.focus();
  }

  setMode(m) {
    this.mode = lastMode = m;
    this.updateModeBtn();
    this.renderEntries();
  }

  updateModeBtn() {
    this.modeBtn.textContent = '';
    this.modeBtn.append(icon(this.mode === 'grid' ? 'list' : 'grid'));
    const t = this.mode === 'grid' ? '改用清單檢視' : '改用格狀檢視';
    this.modeBtn.title = t;
    this.modeBtn.setAttribute('aria-label', t);
  }

  renderLoading() {
    if (!this.loadedOnce) {
      this.body.textContent = '';
      this.body.append(h('div', { class: 'dr-state' }, spinner(), h('span', { text: '讀取資料夾中…' })));
    } else this.el.classList.add('busy');
  }

  renderCrumbs() {
    this.el.classList.remove('busy');
    this.crumbs.textContent = '';
    const p = this.path || '';
    const rel = this.picking ? null : relativeTo(this.root, p);
    const segs = [];
    // 〔遠端〕挑選模式：第一段是「最近的資料夾」（那台的最近資料夾清單），之後是完整路徑的每一段
    if (this.picking) segs.push({ label: '最近的資料夾', path: null, home: true });
    if (rel !== null) {
      segs.push({ label: '工作資料夾', path: this.root, home: true });
      let acc = this.root.replace(/[\\/]+$/, '');
      for (const part of rel.split(/[\\/]+/).filter(Boolean)) { acc += '\\' + part; segs.push({ label: part, path: acc }); }
    } else {
      const parts = p.split(/[\\/]+/).filter(Boolean);
      let acc = '';
      parts.forEach((part, i) => {
        acc = i === 0 ? part + (/:$/.test(part) ? '\\' : '') : acc.replace(/[\\/]+$/, '') + '\\' + part;
        // 〔遠端〕挑選模式只能列最近資料夾與工作資料夾底下（電腦以 Scope 拒絕其他路徑）：範圍外的上層不給點（合併前審查 P4）
        const out = !!this.picking && !this.pickRoots.some((r) => relativeTo(r, acc) !== null);
        segs.push({ label: part, path: acc, out });
      });
    }
    segs.forEach((s, i) => {
      if (i) this.crumbs.append(icon('chevRight', 'cr-sep'));
      const last = i === segs.length - 1;
      const out = !last && !!s.out;
      const b = h('button', {
        type: 'button', class: 'cr' + (last ? ' cur' : '') + (s.home ? ' home' : '') + (out ? ' out' : ''),
        title: out ? '不在可選的範圍內：' + s.path : s.path || s.label, 'aria-current': last ? 'page' : null, disabled: out,
      }, s.home ? icon(this.picking ? 'clock' : 'folder', 'cr-ic') : null, h('span', { text: s.label }));
      if (!last && !out) b.addEventListener('click', () => this.list(s.path));
      this.crumbs.append(b);
    });
    this.upBtn.disabled = !this.canGoUp();
    requestAnimationFrame(() => { this.crumbs.scrollLeft = this.crumbs.scrollWidth; });
  }

  renderEntries() {
    this.el.classList.remove('busy');
    this.body.textContent = '';
    this.body.className = 'dr-body ' + this.mode;
    if (this.error) {
      this.body.append(h('div', { class: 'dr-state err' }, icon('warning'), h('span', { text: this.error })));
      this.foot.textContent = '';
      return;
    }
    const q = this.filter.trim().toLowerCase();
    this.shown = q ? this.entries.filter((e) => (e.name || '').toLowerCase().includes(q)) : this.entries;
    if (!this.shown.length) {
      const empty = this.picking ? (this.path ? '這個資料夾裡沒有子資料夾' : '那台電腦沒有最近使用的資料夾') : '這個資料夾是空的';
      this.body.append(h('div', { class: 'dr-state' }, icon(q ? 'search' : 'folder'), h('span', { text: q ? '沒有符合「' + this.filter.trim() + '」的項目' : empty })));
    }
    const frag = document.createDocumentFragment();
    // 大小比例條的基準：這一頁最大的檔（資料夾不算）；最近一小時改過的檔案前面亮一顆燈（fxrules.sizeFrac、recentlyModified）
    this.maxSize = this.shown.reduce((m, e) => (e.kind !== 'dir' && Number.isFinite(e.size) && e.size > m ? e.size : m), 0);
    this.now = Date.now();
    this.shown.forEach((e, i) => frag.append(this.mode === 'grid' ? this.tile(e, i) : this.row(e, i)));
    this.body.append(frag);
    // 換了資料夾（點進去、上一層、路徑列）：前幾個項目由上往下錯開滑進來（同側欄的列，fx.css 的 .fx-row-in）；第一次開、篩選、換檢視、重新整理不播
    const moved = this.fxPath !== undefined && this.fxPath !== this.path;
    this.fxPath = this.path;
    if (moved) {
      const kids = Array.from(this.body.children).slice(0, LIST_ENTRANCE_MAX);
      kids.forEach((k, i) => { k.style.setProperty('--fx-i', i); replayClass(k, 'fx-row-in', 260 + i * LIST_STAGGER_MS); });
    }
    const dirs = this.entries.filter((e) => e.kind === 'dir').length;
    const files = this.entries.length - dirs;
    const count = this.picking ? dirs + ' 個資料夾' : (dirs ? dirs + ' 個資料夾、' : '') + files + ' 個檔案';
    this.foot.textContent = (q ? this.shown.length + '／' : '') + count + (this.truncated ? '（只列出前 ' + this.entries.length + ' 筆）' : '');
  }

  thumb(e) {
    if (e.thumbUrl && (e.kind === 'image' || e.kind === 'video')) {
      const box = h('span', { class: 'fe-thumb' }, fadeInImg(h('img', { src: e.thumbUrl, alt: '', loading: 'lazy', decoding: 'async', draggable: 'false' })));
      if (e.kind === 'video') box.append(h('span', { class: 'fe-play' }, icon('play')));
      return box;
    }
    return h('span', { class: 'fe-icon k-' + (e.kind || 'file') }, icon(FILE_ICON[e.kind] || 'file'));
  }

  tile(e, i) {
    return h('button', { type: 'button', class: 'fe tile k-' + (e.kind || 'file') + (e.hidden ? ' hidden-file' : '') + (e.current ? ' current' : '') + (this.recent(e) ? ' recent' : ''), dataset: { i: String(i) }, title: this.entryTitle(e, e.name + (e.size !== null && e.size !== undefined ? '\n' + fmtBytes(e.size) : '')) },
      this.thumb(e), h('span', { class: 'fe-name', text: e.name }));
  }

  row(e, i) {
    const frac = e.kind === 'dir' ? null : sizeFrac(e.size, this.maxSize);
    const bar = frac === null ? null : h('span', { class: 'fe-bar', 'aria-hidden': 'true' }, h('i', { style: { transform: 'scaleX(' + frac.toFixed(3) + ')' } }));
    return h('button', { type: 'button', class: 'fe line k-' + (e.kind || 'file') + (e.hidden ? ' hidden-file' : '') + (e.current ? ' current' : '') + (this.recent(e) ? ' recent' : ''), dataset: { i: String(i) }, title: this.entryTitle(e, e.path) },
      this.thumb(e), h('span', { class: 'fe-name', text: e.name }),
      h('span', { class: 'fe-size', text: e.kind === 'dir' ? '' : fmtBytes(e.size) }),
      h('span', { class: 'fe-date', text: fmtRelative(e.modified) }), bar);
  }

  /** 最近一小時改過的檔案（資料夾不算） */
  recent(e) {
    return e.kind !== 'dir' && recentlyModified(e.modified, this.now || Date.now());
  }

  entryTitle(e, base) {
    return e.current ? '目前的工作資料夾：' + e.path : base;
  }

  /** 〔遠端〕對話在其他電腦上（瀏覽器版、桌面可以操作的副本）：列檔走遠端、檔案改成預覽或下載、沒有檔案總管與本機附件 */
  remote() {
    if (isHub) return true;
    const info = this.conv.liveInfo ? this.conv.liveInfo() : null;
    return !!info && controllable(info.origin, info.peer);
  }

  /** 〔遠端〕瀏覽器版的檔案：fileFetch 預覽或下載（D34；電腦檢查範圍、大小與憑證） */
  fetchEntry(e) {
    // 瀏覽器版的 PDF：拿到短效網址後直接預覽（附下載）；其他照舊在框裡給「開啟」
    const opts = isHub && docPreviewKind(e.path) === 'pdf'
      ? { onReady: (v) => openDocument({ url: v.url, name: v.name, conversationId: this.conv.id, onDownload: () => downloadUrl(v.url, v.name) }) }
      : undefined;
    if (!this.conv.ctx.fetchFile || !this.conv.ctx.fetchFile(e.path, e.name, opts)) toast('現在不能讀取這個檔案', 'error');
  }

  activate(e) {
    if (!e) return;
    if (e.kind === 'dir') { this.list(e.path); return; }
    if (this.picking) return;
    if (e.kind === 'image' && e.url) {
      const imgs = this.shown.filter((x) => x.kind === 'image' && x.url);
      openLightbox(imgs.map((x) => ({ url: x.url, name: x.name, path: x.path, conversationId: this.conv.id })), Math.max(0, imgs.indexOf(e)));
      return;
    }
    if (e.kind === 'video' && e.url) { openVideo({ url: e.url, name: e.name, path: e.path, conversationId: this.conv.id }); return; }
    if (this.remote()) { this.fetchEntry(e); return; }
    // PDF、Word、Excel、PowerPoint：在這台直接預覽
    if (this.conv.ctx.openDocument && this.conv.ctx.openDocument({ path: e.path, name: e.name })) return;
    post({ cmd: 'openPath', path: e.path, conversationId: this.conv.id });
  }

  entryMenu(e, at) {
    if (!e) return;
    const isDir = e.kind === 'dir';
    const rel = relativeTo(this.root, e.path);
    const ref = rel ? rel : e.path;
    const media = !isDir && (e.kind === 'image' || e.kind === 'video') && !!e.url;
    // 〔遠端〕瀏覽器版（與桌面可以操作的副本）沒有預設程式、檔案總管與本機附件：檔案改成「預覽或下載」（fileFetch）
    const remote = this.remote();
    const openLabel = isDir ? '開啟資料夾' : media ? '在 app 內檢視' : remote ? '預覽或下載' : '用預設程式開啟';
    openMenu(null, [
      { label: openLabel, icon: isDir ? 'folderOpen' : 'popout', onSelect: () => this.activate(e) },
      // 沒有 url 的圖片／影片（.heic、.mkv…）第一項已經是「用預設程式開啟」
      media && !remote ? { label: '用預設程式開啟', icon: 'popout', onSelect: () => post({ cmd: 'openPath', path: e.path, conversationId: this.conv.id }) } : null,
      media && remote ? { label: '預覽或下載', icon: 'download', onSelect: () => this.fetchEntry(e) } : null,
      remote ? null : { label: '在檔案總管顯示', icon: 'folderOpen', onSelect: () => post({ cmd: 'revealPath', path: e.path, conversationId: this.conv.id }) },
      { type: 'sep' },
      isDir || remote ? null : { label: '加入附件', icon: 'paperclip', onSelect: () => { post({ cmd: 'addPathAttachment', conversationId: this.conv.id, path: e.path }); } },
      { label: '插入路徑到訊息', icon: 'at', desc: '@' + (/\s/.test(ref) ? '"' + ref + '"' : ref), onSelect: () => this.conv.composer.insertText('@' + (/\s/.test(ref) ? '"' + ref + '"' : ref) + ' ') },
      { label: '複製路徑', icon: 'copy', onSelect: async () => {
        const ok = await copyToClipboard(e.path, (text) => post({ cmd: 'copyText', text }));
        toast(ok ? '已複製路徑' : '已請宿主複製路徑', 'success');
      } },
    ].filter(Boolean), { at, minWidth: 220 });
  }

  bindResize() {
    let startX = 0, startW = 0, max = 800;
    const move = (e) => {
      const w = clamp(startW + (startX - e.clientX), 240, max);
      this.el.style.width = w + 'px';
      lastWidth = w;
      // 拖曳中的寬度讀數（.drawer.resizing 才看得到）
      this.wRead.textContent = Math.round(w) + ' px';
    };
    const up = (e) => {
      this.handle.releasePointerCapture(e.pointerId);
      this.handle.removeEventListener('pointermove', move);
      this.handle.removeEventListener('pointerup', up);
      this.el.classList.remove('resizing');
    };
    this.handle.addEventListener('pointerdown', (e) => {
      if (e.button !== 0) return;
      e.preventDefault();
      startX = e.clientX;
      startW = this.el.getBoundingClientRect().width;
      max = Math.max(260, this.conv.root.getBoundingClientRect().width - 280);
      this.handle.setPointerCapture(e.pointerId);
      this.handle.addEventListener('pointermove', move);
      this.handle.addEventListener('pointerup', up);
      this.el.classList.add('resizing');
    });
    this.handle.addEventListener('dblclick', () => { this.el.style.width = '340px'; lastWidth = 340; });
  }
}
