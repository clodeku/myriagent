// 輸入框草稿的同步（web-protocol 的 composerDraft／composerDrafts；規則在 draftsyncrules.js）。
// 每個輸入框一個 createDraftSync：打字停下 DRAFT_SYNC_DELAY_MS 送 composerDraft（送出之後是空字串＝清掉），
// 收到別處的（宿主轉來的 composerDrafts）照 remoteDraftAction 換掉輸入框、或在輸入框上方提示「換成它／保留這裡的」。
// 記得的草稿（別處送來的）放在這個模組：分頁晚一點才開也拿得到。協作人不同步（草稿是擁有者打的字）
import { h, setHidden } from './util.js';
import { icon } from './icons.js';
import { post, isCollaborator } from './store.js';
import { isDraftId } from './liverules.js';
import { DRAFT_SYNC_DELAY_MS, shouldPublish, remoteDraftAction, mergeDrafts, draftPreview, draftSourceLabel, isBlankDraft, SENT_KEY, parseSent, rememberSent, wasSent } from './draftsyncrules.js';

const drafts = new Map();

/** 宿主的 composerDrafts：記下來，變了的交給開著的那個對話的輸入框 */
export function onComposerDrafts(msg, getConv) {
  for (const c of mergeDrafts(drafts, msg)) {
    const cv = getConv(c.conversationId);
    if (cv && cv.composer && cv.composer.draftSync) cv.composer.draftSync.remote(c);
  }
}

/**
 * 這個分頁自己送出了草稿（或清掉）：記得的那份跟著換（2026-11-11 使用者回報：送出之後舊字又出現在瀏覽器版的輸入框——
 * 伺服器不把這個分頁送的回送給它，記得的還是別處先前那份，重新載入對話〔loaded〕就填回去）。rev 照舊（伺服器之後送來的比較新）
 */
function noteLocal(id, text) {
  const known = drafts.get(id);
  if (isBlankDraft(text)) drafts.delete(id);
  else drafts.set(id, { text, rev: known ? known.rev : 0, from: null, at: Date.now() });
}

/** 最近送出的訊息（localStorage，同一個瀏覽器的分頁共用；讀寫失敗當成沒有） */
function sentList() {
  try { return parseSent(localStorage.getItem(SENT_KEY), Date.now()); } catch { return []; }
}

/** 這個對話送出了這則（輸入框送出時，composer.send）：之後別處傳來同樣的字不填回輸入框 */
export function noteSent(id, text) {
  if (typeof id !== 'string' || !id) return;
  try { localStorage.setItem(SENT_KEY, JSON.stringify(rememberSent(sentList(), id, text, Date.now()))); } catch { /* 存不了就算了 */ }
}

/** 這個對話記得的別處草稿（沒有是 null） */
export function storedDraft(id) {
  const d = drafts.get(id);
  return d ? { conversationId: id, text: d.text, from: d.from, rev: d.rev } : null;
}

/**
 * 一個輸入框的同步：composer 要有 ta（textarea）、conv（id）、autosize()、updateSend()。
 * changed()：輸入框的字可能變了（updateSend 每次都呼叫，便宜：字沒變什麼都不做）；input()：使用者打了字；flush()：馬上送
 */
export function createDraftSync(composer) {
  let posted = '';     // 上次送出去的
  let synced = '';     // 上次和別處一致的（送出或換上的）
  let seen = '';       // changed() 上次看到的字
  let lastInputAt = 0;
  let timer = 0;
  let offered = null;  // 提示中的那份 {text, from}

  const text = h('span', { class: 'steer-sum' });
  const takeBtn = h('button', { type: 'button', class: 'btn sm', text: '換成它', dataset: { field: 'draftTake' } });
  const keepBtn = h('button', { type: 'button', class: 'btn sm', text: '保留這裡的', dataset: { field: 'draftKeep' } });
  const el = h('div', { class: 'steered draft-offer', hidden: true, role: 'status', dataset: { field: 'draftOffer' } },
    h('div', { class: 'steer-head' }, icon('desktop', 'steer-ic'), text, takeBtn, keepBtn));

  const id = () => composer.conv && composer.conv.id;
  // 伺服器草稿（瀏覽器版還沒建立的新對話）沒有對應的對話；其他的 id 由宿主判斷（桌面不在這台的略過、伺服器只收線上 id）
  const eligible = () => typeof id() === 'string' && !!id() && !isDraftId(id()) && !isCollaborator() && !composer.destroyed;
  const editable = () => !!composer.ta && composer.ta.isConnected && !composer.ta.readOnly && !composer.ta.disabled;

  function publish() {
    clearTimeout(timer);
    timer = 0;
    if (!eligible()) return;
    const value = composer.ta.value;
    if (!shouldPublish(value, posted)) return;
    posted = isBlankDraft(value) ? '' : value;
    synced = posted;
    noteLocal(id(), posted);
    post({ cmd: 'composerDraft', conversationId: id(), text: posted });
    if (offered && offered.text === posted) hideOffer();
  }

  function setText(value) {
    composer.ta.value = value;
    seen = value;
    posted = synced = isBlankDraft(value) ? '' : value;
    composer.autosize();
    composer.updateSend();
  }

  function hideOffer() {
    offered = null;
    setHidden(el, true);
  }

  function showOffer(d) {
    offered = { text: d.text, from: d.from };
    text.textContent = draftSourceLabel(d.from) + '有不同的草稿：「' + draftPreview(d.text) + '」';
    el.title = d.text.length > 2000 ? d.text.slice(0, 2000) + '…' : d.text;
    setHidden(el, false);
  }

  takeBtn.addEventListener('click', () => {
    if (!offered || !editable()) return;
    const t = offered.text;
    hideOffer();
    setText(t);
    composer.focus();
    composer.ta.setSelectionRange(t.length, t.length);
  });
  keepBtn.addEventListener('click', () => {
    hideOffer();
    // 保留這裡的：馬上送，別處（沒在打的）跟著換成這裡的
    posted = null;
    publish();
  });

  return {
    el,
    /** 使用者打了字（輸入框的 input 事件） */
    input() {
      lastInputAt = Date.now();
    },
    /** 輸入框的字可能變了：變了就重新等 DRAFT_SYNC_DELAY_MS 再送 */
    changed() {
      if (!composer.ta || composer.ta.value === seen) return;
      seen = composer.ta.value;
      if (offered && offered.text === seen) hideOffer();
      if (!eligible()) return;
      clearTimeout(timer);
      timer = setTimeout(publish, DRAFT_SYNC_DELAY_MS);
    },
    /** 馬上送（分頁要關了） */
    flush() {
      if (timer) publish();
    },
    /** 別處的草稿（宿主轉來的） */
    remote(d) {
      if (!eligible() || !editable() || !d) return;
      // 就是這個對話剛送出的那句（伺服器那份還沒清掉、或這個分頁記得的是舊的）：不填回輸入框，再送一次清掉讓別處也清掉
      if (wasSent(sentList(), id(), d.text)) {
        drafts.delete(id());
        if (isBlankDraft(composer.ta.value)) {
          posted = synced = '';
          post({ cmd: 'composerDraft', conversationId: id(), text: '' });
        }
        return;
      }
      const now = Date.now();
      const action = remoteDraftAction({
        current: composer.ta.value, synced, remote: d.text,
        focused: document.activeElement === composer.ta, lastInputAt, now,
      });
      if (action === 'same') {
        posted = synced = isBlankDraft(d.text) ? '' : d.text;
        hideOffer();
      } else if (action === 'apply') {
        hideOffer();
        setText(d.text);
      } else if (action === 'offer') {
        showOffer(d);
      }
    },
    /** 對話載入了（conversation）：這個對話記得的別處草稿照規則套上 */
    loaded() {
      const d = storedDraft(id());
      if (d) this.remote(d);
    },
    destroy() {
      clearTimeout(timer);
      timer = 0;
    },
  };
}
