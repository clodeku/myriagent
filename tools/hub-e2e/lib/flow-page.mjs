// 全流程 FF1～FF3 的頁面工具（WP4.9 步 15）：
// - flowRecorder：每個頁面在網頁的任何腳本之前掛上的記錄器（harness 的 initScripts；接在 ui-probe/hub-page.mjs 的 probeInit 後面，
//   probeInit 以 lightMessages 只記型別，這裡只記比對要用的欄位，大清單與長串流不複製整則）。記：
//   1. 時間標記（計畫 D8，全形括號裡 13 位數字的 UTC 毫秒）：每個 (對話, 標記) 第一次經傳輸層收到（ws）、第一次在完成的項目裡收到（wsDone）、
//      第一次出現在畫面上（dom，MutationObserver）、第一次出現在不是執行中的項目上（domDone）。網頁以 requestAnimationFrame 合併畫面更新，
//      所以 dom 只在前景的分頁有意義（同一個瀏覽器內容裡只有前景的分頁跑 rAF；第二個頁面開在另一個內容，見 harness 的 addBrowserContext）。
//   2. 項目的最新狀態（id、kind、status、seq〔瀏覽器版填的是 Ord〕、requestDigest、decision）、每個對話最近一次的 conversation 與 olderItems、
//      最新的對話清單（只留 id、deviceId、title、updatedAt、running、waiting）、commandResult、toast、conversationClosed、輸入區的佇列長度、標頭的 running；
//      conversation 另記草稿、開在哪台、標題、paneId（步 16 的 FF4）。
// - 原始的 SignalR 連線（rawConnect、rawCmd、rawWait、rawClose）：在頁面裡另開一條 /hubs/v1/browser（同一個工作階段的 cookie、本站 Origin），
//   不經 app.js——FF1 拿來 open／loadOlder 抽樣的對話（不在網頁開一堆分頁），FF3 拿來以同一個 commandId 重送。
// - trackHubFrames：Node 端看頁面的 SignalR 框（Cmd 的呼叫與回傳、commandResult 的到達順序；同 remote-control.mjs）。
import { ulid } from './flow-rules.mjs';
import crypto from 'node:crypto';

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const RS = String.fromCharCode(0x1e);
const HUB_PATH = '/hubs/v1/browser';

/** 新的 commandId（ULID；時間是現在、亂數來自 crypto） */
export function newCommandId() {
  return ulid(Date.now(), [...crypto.randomBytes(10)]);
}

/** 頁面的記錄器（以 context.addInitScript(flowRecorder) 掛上；每個頁面、每次導覽各一份，存在 window.__ff） */
export function flowRecorder() {
  const STAMP = /〔([0-9]{13})〕/g;
  const ff = {
    stamps: Object.create(null), items: Object.create(null), snaps: Object.create(null), older: Object.create(null),
    list: null, listAt: 0, lists: 0, results: [], toasts: [], closed: [], queue: Object.create(null), header: Object.create(null),
    counts: Object.create(null), domScans: 0, errors: [],
  };
  window.__ff = ff;
  const rec = (c, s) => {
    const k = c + '|' + s;
    let r = ff.stamps[k];
    if (!r) {
      r = { c, s, ws: null, wsStatus: null, wsDone: null, dom: null, domDone: null, item: null };
      ff.stamps[k] = r;
    }
    return r;
  };
  const scanText = (c, text, t, status, itemId) => {
    if (!text || text.indexOf('〔') < 0) return;
    STAMP.lastIndex = 0;
    let m;
    while ((m = STAMP.exec(text)) !== null) {
      const r = rec(c, m[1]);
      if (r.ws === null) { r.ws = t; r.wsStatus = status; r.item = itemId; }
      if (r.wsDone === null && status === 'Done') r.wsDone = t;
    }
  };
  const noteItem = (c, item, t) => {
    if (!c || !item || typeof item !== 'object') return;
    ff.items[c + '|' + item.id] = {
      c, id: item.id, kind: item.kind, status: item.status, seq: item.seq, t,
      digest: typeof item.requestDigest === 'string' ? item.requestDigest : null, decision: item.decision ?? null,
    };
    scanText(c, (item.html || '') + (item.output ? '\n' + item.output : ''), t, item.status, item.id);
  };
  const listOf = (items) => (Array.isArray(items) ? items.map((x) => ({ id: x.id, deviceId: x.deviceId ?? null, title: x.title, updatedAt: x.updatedAt, running: !!x.running, waiting: !!x.waiting })) : null);
  // 標頭裡依代理種類不同的欄位（Grok／Antigravity 同步的 P5，FF11）：provider、steer（執行中送出能不能插入這一輪）、權限選單、目前的權限、上一輪的用量
  const headerOf = (h) => (h ? {
    provider: h.provider ?? null, steer: h.steer ?? null, permission: h.permission ?? null,
    modes: Array.isArray(h.permissionModes) ? h.permissionModes.map((x) => x && x.value) : null, lastTurn: h.lastTurn ?? null,
  } : null);
  const onMsg = (m) => {
    try {
      const t = Date.now();
      const type = m && m.type;
      ff.counts[type] = (ff.counts[type] || 0) + 1;
      switch (type) {
        case 'item': noteItem(m.conversationId, m.item, t); break;
        case 'conversation':
          ff.snaps[m.conversationId] = {
            t, items: (m.items || []).map((i) => ({ id: i.id, seq: i.seq, kind: i.kind, status: i.status })), hasMore: !!m.hasMore, running: !!(m.header && m.header.running),
            // 步 16（FF4）：草稿、開在哪台、標題
            draft: !!(m.header && m.header.isDraft), deviceId: (m.header && m.header.deviceId) || null, title: (m.header && m.header.title) || null, paneId: m.paneId ?? null,
            header: headerOf(m.header),
          };
          for (const i of m.items || []) noteItem(m.conversationId, i, t);
          if (m.composer) ff.queue[m.conversationId] = Array.isArray(m.composer.queue) ? m.composer.queue.length : 0;
          break;
        case 'olderItems':
          (ff.older[m.conversationId] = ff.older[m.conversationId] || []).push({ t, beforeId: m.beforeId ?? null, items: (m.items || []).map((i) => ({ id: i.id, seq: i.seq })), hasMore: !!m.hasMore });
          break;
        case 'init': ff.list = listOf(m.conversations); ff.listAt = t; ff.lists++; break;
        case 'conversations': ff.list = listOf(m.items); ff.listAt = t; ff.lists++; break;
        case 'commandResult': ff.results.push({ t, commandId: m.commandId, state: m.state, code: m.code ?? null, message: m.message ?? null, origin: m.origin ?? null }); break;
        case 'toast': ff.toasts.push({ t, text: String(m.text ?? ''), level: m.level ?? null }); break;
        case 'conversationClosed': ff.closed.push({ t, c: m.conversationId, replacedBy: m.replacedBy ?? null }); break;
        case 'composer': ff.queue[m.conversationId] = m.composer && Array.isArray(m.composer.queue) ? m.composer.queue.length : 0; break;
        case 'header': ff.header[m.conversationId] = { t, running: !!(m.header && m.header.running), ...(headerOf(m.header) ?? {}) }; break;
        default: break;
      }
    } catch (e) {
      if (ff.errors.length < 20) ff.errors.push(String(e));
    }
  };
  // probeInit 已經把 myriagentHubTransport 定義成存取子：包一層，傳輸層掛上時多註冊這個訂閱者（probeInit 的照舊）
  const before = Object.getOwnPropertyDescriptor(window, 'myriagentHubTransport');
  let own;
  Object.defineProperty(window, 'myriagentHubTransport', {
    configurable: true,
    enumerable: true,
    get() { return before && before.get ? before.get.call(window) : own; },
    set(v) {
      if (before && before.set) before.set.call(window, v); else own = v;
      try {
        if (v && typeof v.onMessage === 'function') v.onMessage(onMsg);
      } catch (e) {
        ff.errors.push('訂閱失敗：' + String(e));
      }
    },
  });
  // 畫面：每一批變動找出受影響的項目（.it）與它的對話（.conv[data-conv]），讀一次文字找標記
  const domScan = (it, t) => {
    const conv = it.closest('.conv[data-conv]');
    if (!conv) return;
    const text = it.textContent || '';
    if (text.indexOf('〔') < 0) return;
    ff.domScans++;
    const c = conv.getAttribute('data-conv');
    const running = it.classList.contains('running');
    STAMP.lastIndex = 0;
    let m;
    while ((m = STAMP.exec(text)) !== null) {
      const r = rec(c, m[1]);
      if (r.dom === null) r.dom = t;
      if (!running && r.domDone === null) r.domDone = t;
    }
  };
  const observer = new MutationObserver((records) => {
    const t = Date.now();
    const set = new Set();
    for (const r of records) {
      let n = r.target;
      if (n && n.nodeType !== 1) n = n.parentElement;
      const it = n && n.closest ? n.closest('.it') : null;
      if (it) set.add(it);
      for (const a of r.addedNodes || []) {
        if (a.nodeType !== 1) continue;
        if (a.matches && a.matches('.it')) set.add(a);
        else if (a.querySelectorAll) for (const x of a.querySelectorAll('.it')) set.add(x);
      }
    }
    for (const it of set) {
      try { domScan(it, t); } catch (e) { if (ff.errors.length < 20) ff.errors.push(String(e)); }
    }
  });
  observer.observe(document, { subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: ['class'] });
}

// ───── 讀頁面的記錄 ─────

/** 標記紀錄（只要這些對話；沒給就全部）：[{ c, s, ws, wsStatus, wsDone, dom, domDone, item }] */
export function stampRecords(app, conversations = null) {
  return app.page.evaluate((cs) => {
    const ff = window.__ff;
    if (!ff) return [];
    const only = cs ? new Set(cs) : null;
    return Object.values(ff.stamps).filter((r) => !only || only.has(r.c));
  }, conversations).catch(() => []);
}

/** 記錄器的摘要：清單（只有數量與每台的對話數）、各型別的訊息數、錯誤 */
export function recorderSummary(app) {
  return app.page.evaluate(() => {
    const ff = window.__ff;
    if (!ff) return null;
    const perDevice = {};
    for (const x of ff.list || []) perDevice[x.deviceId] = (perDevice[x.deviceId] || 0) + 1;
    return { listCount: ff.list ? ff.list.length : null, lists: ff.lists, perDevice, counts: { ...ff.counts }, domScans: ff.domScans, errors: ff.errors.slice(), stamps: Object.keys(ff.stamps).length };
  }).catch(() => null);
}

/** 最新的對話清單（記錄器的精簡版） */
export function ffList(app) {
  return app.page.evaluate(() => (window.__ff ? window.__ff.list : null)).catch(() => null);
}

/** 某個對話最近一次的 conversation（{ t, items, hasMore, running }；沒有是 null） */
export function ffSnapshot(app, c) {
  return app.page.evaluate((x) => (window.__ff ? window.__ff.snaps[x] ?? null : null), c).catch(() => null);
}

/** 收到的草稿（conversation 的 header.isDraft；步 16 的 FF4）：[{ id, deviceId, paneId, t }] */
export function ffDrafts(app) {
  return app.page.evaluate(() => {
    const ff = window.__ff;
    if (!ff) return [];
    return Object.entries(ff.snaps).filter(([, s]) => s.draft).map(([id, s]) => ({ id, deviceId: s.deviceId, paneId: s.paneId, t: s.t }));
  }).catch(() => []);
}

/** 某個對話所有項目的最新狀態（依 seq 排） */
export function ffItems(app, c) {
  return app.page.evaluate((x) => {
    const ff = window.__ff;
    if (!ff) return [];
    return Object.values(ff.items).filter((i) => i.c === x).sort((a, b) => (a.seq ?? 0) - (b.seq ?? 0));
  }, c).catch(() => []);
}

/** commandResult、toast、conversationClosed 的紀錄（from 之後的） */
export function ffEvents(app) {
  return app.page.evaluate(() => {
    const ff = window.__ff;
    return ff ? { results: ff.results.slice(), toasts: ff.toasts.slice(), closed: ff.closed.slice(), queue: { ...ff.queue }, header: { ...ff.header } } : null;
  }).catch(() => null);
}

/** 等某個 commandId 的 commandResult（記錄器的；ms 內沒有回 null） */
export async function ffWaitResult(app, commandId, ms = 30000) {
  const started = Date.now();
  while (Date.now() - started < ms) {
    const r = await app.page.evaluate((id) => (window.__ff ? window.__ff.results.find((x) => x.commandId === id) ?? null : null), commandId).catch(() => null);
    if (r) return r;
    await sleep(100);
  }
  return null;
}

/** 頁面裡 requestAnimationFrame 在 ms 毫秒內跑了幾次（0＝這個分頁在背景，畫面的更新停住） */
export function rafCount(app, ms = 400) {
  return app.page.evaluate((x) => new Promise((resolve) => {
    let n = 0;
    const until = performance.now() + x;
    const tick = () => { n++; if (performance.now() < until) requestAnimationFrame(tick); };
    requestAnimationFrame(tick);
    setTimeout(() => resolve(n), x + 50);
  }), ms).catch(() => null);
}

// ───── 原始的 SignalR 連線（頁面裡另開一條，不經 app.js）─────

/**
 * 在頁面裡開一條 /hubs/v1/browser（JSON 協定、只用 WebSocket；SignalR 的握手、每 10 秒一個 ping）並送 ready、等 init。
 * 回 { ok, error }；連線的狀態在 window.__ffRaw（msgs：伺服器送的 Msg，completions：Cmd 的回傳）
 */
export function rawConnect(app, readyCommandId = newCommandId(), timeoutMs = 20000) {
  return app.page.evaluate(([readyId, ms, rs, hubPath]) => new Promise((resolve) => {
    const raw = { msgs: [], completions: Object.create(null), next: 1, open: false, closed: null, errors: [] };
    window.__ffRaw = raw;
    const ws = new WebSocket(`wss://${location.host}${hubPath}`);
    raw.ws = ws;
    let handshaken = false;
    let buf = '';
    const timer = setTimeout(() => resolve({ ok: false, error: `${ms} ms 內沒有收到 init` }), ms);
    raw.send = (obj) => ws.send(JSON.stringify(obj) + rs);
    raw.cmd = (commandId, cmd) => {
      const invocationId = String(raw.next++);
      raw.send({ type: 1, invocationId, target: 'Cmd', arguments: [commandId ? { commandId, cmd } : { cmd }] });
      return invocationId;
    };
    ws.onopen = () => {
      raw.open = true;
      ws.send(JSON.stringify({ protocol: 'json', version: 1 }) + rs);
    };
    ws.onerror = () => raw.errors.push('error');
    ws.onclose = (e) => { raw.closed = { code: e.code, reason: e.reason }; clearInterval(raw.ping); };
    ws.onmessage = (e) => {
      buf += typeof e.data === 'string' ? e.data : '';
      let i;
      while ((i = buf.indexOf(rs)) >= 0) {
        const part = buf.slice(0, i);
        buf = buf.slice(i + 1);
        let o;
        try { o = JSON.parse(part); } catch { continue; }
        if (!handshaken) {
          handshaken = true;
          if (o.error) { clearTimeout(timer); resolve({ ok: false, error: '握手失敗：' + o.error }); return; }
          raw.ping = setInterval(() => { try { raw.send({ type: 6 }); } catch { /* 已關 */ } }, 10000);
          raw.cmd(readyId, { cmd: 'ready', mode: 'main' });
          continue;
        }
        if (o.type === 1 && o.target === 'Msg') {
          const m = (o.arguments && o.arguments[0]) || null;
          raw.msgs.push({ t: Date.now(), m });
          if (m && m.type === 'init') { clearTimeout(timer); resolve({ ok: true, error: null }); }
        } else if (o.type === 3) {
          raw.completions[o.invocationId] = { t: Date.now(), result: o.result ?? null, error: o.error ?? null };
        } else if (o.type === 7) {
          raw.closed = { code: 'close', reason: o.error ?? null };
        }
      }
    };
  }), [readyCommandId, timeoutMs, RS, HUB_PATH]).catch((e) => ({ ok: false, error: String(e?.message ?? e) }));
}

/** 原始連線送一個 Cmd（commandId 沒給就產生一個新的）：回 { commandId, invocationId } */
export async function rawCmd(app, cmd, commandId = newCommandId()) {
  const invocationId = await app.page.evaluate(([id, c]) => (window.__ffRaw && window.__ffRaw.open ? window.__ffRaw.cmd(id, c) : null), [commandId, cmd]).catch(() => null);
  return { commandId, invocationId };
}

/** 原始連線的 Cmd 回傳（ms 內沒有回 null） */
export async function rawCompletion(app, invocationId, ms = 10000) {
  const started = Date.now();
  while (Date.now() - started < ms) {
    const r = await app.page.evaluate((inv) => (window.__ffRaw ? window.__ffRaw.completions[inv] ?? null : null), invocationId).catch(() => null);
    if (r) return r;
    await sleep(50);
  }
  return null;
}

/**
 * 等原始連線收到符合條件的 Msg（pred 是在頁面裡執行的 (m) => boolean 的原始碼字串之外的形式不行：這裡只收「型別＋鍵值」）：
 * match = { type, ...鍵值 }（每個鍵都要相等）。回第一則（from 之後的），ms 內沒有回 null
 */
export async function rawWait(app, match, { from = 0, ms = 15000 } = {}) {
  const started = Date.now();
  while (Date.now() - started < ms) {
    const r = await app.page.evaluate(([mt, f]) => {
      const raw = window.__ffRaw;
      if (!raw) return null;
      for (let i = f; i < raw.msgs.length; i++) {
        const m = raw.msgs[i].m;
        if (m && Object.entries(mt).every(([k, v]) => m[k] === v)) return { index: i, t: raw.msgs[i].t, m };
      }
      return null;
    }, [match, from]).catch(() => null);
    if (r) return r;
    await sleep(50);
  }
  return null;
}

/** 原始連線目前收到幾則 Msg（之後的 rawWait 從這裡開始找） */
export function rawMark(app) {
  return app.page.evaluate(() => (window.__ffRaw ? window.__ffRaw.msgs.length : 0)).catch(() => 0);
}

/** 原始連線收到的某種 Msg（只回比對要用的欄位） */
export function rawMessages(app, type) {
  return app.page.evaluate((t) => (window.__ffRaw ? window.__ffRaw.msgs.filter((x) => x.m && x.m.type === t).map((x) => ({ t: x.t, ...x.m })) : []), type).catch(() => []);
}

/** 關掉原始連線 */
export function rawClose(app) {
  return app.page.evaluate(() => {
    const raw = window.__ffRaw;
    if (!raw) return false;
    clearInterval(raw.ping);
    try { raw.ws.close(); } catch { /* 已關 */ }
    return true;
  }).catch(() => false);
}

/**
 * 原始連線上 open 一個對話、loadOlder 到沒有更早的：回 { ok, items: [{ id, seq }], pages, hasMore, error }（open 之後 close）。
 * 伺服器一頁最多 80 筆；beforeId 是這一頁最早的那筆
 */
export async function rawReadAll(app, conversationId, { ms = 15000, maxPages = 50 } = {}) {
  const mark = await rawMark(app);
  await rawCmd(app, { cmd: 'open', conversationId });
  const snap = await rawWait(app, { type: 'conversation', conversationId }, { from: mark, ms });
  if (!snap) return { ok: false, items: [], pages: 0, hasMore: null, error: 'open 之後沒有收到 conversation' };
  let items = (snap.m.items ?? []).map((i) => ({ id: i.id, seq: i.seq }));
  let hasMore = !!snap.m.hasMore;
  let pages = 1;
  while (hasMore && pages < maxPages) {
    const beforeId = items[0]?.id ?? null;
    const m2 = await rawMark(app);
    await rawCmd(app, { cmd: 'loadOlder', conversationId, beforeId });
    const older = await rawWait(app, { type: 'olderItems', conversationId, beforeId }, { from: m2, ms });
    if (!older) return { ok: false, items, pages, hasMore, error: `loadOlder（${beforeId}）之後沒有收到 olderItems` };
    items = [...(older.m.items ?? []).map((i) => ({ id: i.id, seq: i.seq })), ...items];
    hasMore = !!older.m.hasMore;
    pages++;
  }
  await rawCmd(app, { cmd: 'close', conversationId });
  return { ok: !hasMore, items, pages, hasMore, error: hasMore ? `${maxPages} 頁之後還有更早的` : null };
}

// ───── 擁有者的 HTTP API（在頁面裡以同源的 cookie 呼叫）─────

/** GET /api/v1/ops/status：{ status, json } */
export function opsStatus(app) {
  return app.page.evaluate(async () => {
    const r = await fetch('/api/v1/ops/status', { credentials: 'same-origin', cache: 'no-store' });
    let json = null;
    try { json = await r.json(); } catch { /* 不是 JSON */ }
    return { status: r.status, json };
  }).catch((e) => ({ status: null, json: null, error: String(e?.message ?? e) }));
}

/** POST /api/v1/auth/stepup {password}（近期驗證；要 X-Myriagent-Csrf 與同源）：{ status, json }。密碼只在這個呼叫的參數裡，不寫進結果 */
export function stepUp(app, password) {
  return app.page.evaluate(async (p) => {
    const r = await fetch('/api/v1/auth/stepup', {
      method: 'POST', credentials: 'same-origin', cache: 'no-store',
      headers: { 'Content-Type': 'application/json', 'X-Myriagent-Csrf': '1' }, body: JSON.stringify({ password: p }),
    });
    let json = null;
    try { json = await r.json(); } catch { /* 不是 JSON */ }
    return { status: r.status, json };
  }, password).catch((e) => ({ status: null, json: null, error: String(e?.message ?? e) }));
}

/**
 * 稽核裡 seq 大於 afterSeq 的每一筆（GET /api/v1/audit?before=&limit=200，新到舊翻頁；要近期驗證）：
 * { status, entries: [{ seq, at, action, target, outcome }], top（最新一筆的 seq） }
 */
export function auditSince(app, afterSeq) {
  return app.page.evaluate(async (after) => {
    const out = [];
    let before = null;
    let top = null;
    for (let page = 0; page < 50; page++) {
      const r = await fetch(`/api/v1/audit?limit=200${before === null ? '' : `&before=${before}`}`, { credentials: 'same-origin', cache: 'no-store' });
      if (r.status !== 200) return { status: r.status, entries: out, top };
      const j = await r.json();
      const rows = j.entries || [];
      if (top === null) top = rows.length ? rows[0].seq : 0;
      let done = rows.length === 0;
      for (const e of rows) {
        if (e.seq <= after) { done = true; break; }
        out.push({ seq: e.seq, at: e.at, action: e.action, target: e.target, outcome: e.outcome });
      }
      if (done) break;
      before = rows[rows.length - 1].seq;
    }
    return { status: 200, entries: out, top };
  }, afterSeq).catch((e) => ({ status: null, entries: [], top: null, error: String(e?.message ?? e) }));
}

/**
 * 在頁面裡以同源的 cookie GET 一個本站網址（步 16 的 FF6：/m/、/t/）：{ status, headers（小寫名稱）, size, sha256, head（前 16 位元組的十六進位）}。
 * headers 是要帶的請求標頭（例如 Range）；本文只回雜湊與開頭，不回內容
 */
export function pageGet(app, url, headers = {}) {
  return app.page.evaluate(async ([u, h]) => {
    const r = await fetch(u, { credentials: 'same-origin', cache: 'no-store', headers: h });
    const buf = new Uint8Array(await r.arrayBuffer());
    const hex = (a) => Array.from(a, (b) => b.toString(16).padStart(2, '0')).join('');
    const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', buf));
    const out = {};
    r.headers.forEach((v, k) => { out[k] = v; });
    return { status: r.status, headers: out, size: buf.length, sha256: hex(digest), head: hex(buf.slice(0, 16)) };
  }, [url, headers]).catch((e) => ({ status: null, headers: {}, size: null, sha256: null, head: null, error: String(e?.message ?? e) }));
}

/**
 * 在頁面裡 PUT /api/v1/uploads/{uploadId}（瀏覽器上傳：同源、X-Myriagent-Csrf: 1）：內容在頁面裡產生（xorshift32，同 media-rules 的 seededBytes），
 * size 個位元組、不經 Playwright 傳大字串。回 { status, json, sha256 }
 */
export function pageUpload(app, uploadId, size, seed) {
  return app.page.evaluate(async ([id, n, s]) => {
    const data = new Uint8Array(n);
    let x = (s >>> 0) || 0x9e3779b9;
    for (let i = 0; i < n; i++) {
      x ^= x << 13;
      x >>>= 0;
      x ^= x >>> 17;
      x ^= x << 5;
      x >>>= 0;
      data[i] = x & 0xff;
    }
    const hex = (a) => Array.from(a, (b) => b.toString(16).padStart(2, '0')).join('');
    const sha256 = hex(new Uint8Array(await crypto.subtle.digest('SHA-256', data)));
    const r = await fetch('/api/v1/uploads/' + encodeURIComponent(id), {
      method: 'PUT', credentials: 'same-origin', cache: 'no-store', headers: { 'X-Myriagent-Csrf': '1', 'Content-Type': 'application/octet-stream' }, body: data,
    });
    let json = null;
    try { json = await r.json(); } catch { /* 不是 JSON */ }
    return { status: r.status, json, sha256 };
  }, [uploadId, size, seed]).catch((e) => ({ status: null, json: null, sha256: null, error: String(e?.message ?? e) }));
}

// ───── Node 端看頁面的 SignalR 框 ─────

/**
 * 記下這個頁面 /hubs/v1/browser 的 Cmd 呼叫（送出）、呼叫的回傳與 commandResult（收到），依到達順序編號：app.frames。
 * 每條 WebSocket 一個編號（sock：app.js 的與 rawConnect 開的各自編號，invocationId 只在同一條裡唯一）。
 * 要在導覽之前呼叫（page.on('websocket') 只看得到之後開的連線；openApp 的 beforeGoto）
 */
export function trackHubFrames(app) {
  app.frames = [];
  let seq = 0;
  let sockets = 0;
  const onFrames = (sock, dir, payload) => {
    if (typeof payload !== 'string') return;
    for (const part of payload.split(RS)) {
      if (!part) continue;
      let o;
      try { o = JSON.parse(part); } catch { continue; }
      const base = { seq: ++seq, t: Date.now(), sock, dir };
      if (dir === 'out' && o.type === 1 && o.target === 'Cmd') {
        const a = (o.arguments && o.arguments[0]) || {};
        app.frames.push({ ...base, kind: 'cmd', inv: o.invocationId ?? null, commandId: a.commandId ?? null, cmd: a.cmd?.cmd ?? null, conv: a.cmd?.conversationId ?? null,
          item: a.cmd?.itemId ?? null, digest: a.cmd?.requestDigest ?? null, choice: a.cmd?.choice ?? null,
          // 步 16（FF4）：newConversation 選的帳戶與電腦、送出的文字
          accountId: a.cmd?.accountId ?? null, deviceId: a.cmd?.deviceId ?? null, text: typeof a.cmd?.text === 'string' ? a.cmd.text : null });
      } else if (dir === 'in' && o.type === 3) {
        app.frames.push({ ...base, kind: 'completion', inv: o.invocationId, accepted: o.result?.accepted ?? null, error: o.result?.error ?? o.error ?? null });
      } else if (dir === 'in' && o.type === 1 && o.target === 'Msg') {
        const m = (o.arguments && o.arguments[0]) || {};
        if (m.type === 'commandResult') app.frames.push({ ...base, kind: 'result', commandId: m.commandId, state: m.state, code: m.code ?? null });
      }
    }
  };
  app.page.on('websocket', (ws) => {
    if (!ws.url().includes(HUB_PATH)) return;
    const sock = ++sockets;
    ws.on('framesent', (f) => onFrames(sock, 'out', f.payload));
    ws.on('framereceived', (f) => onFrames(sock, 'in', f.payload));
  });
}

/**
 * 一個 commandId 在某一條 WebSocket 上的 Cmd 回傳與 commandResult 哪個先到（同一條的到達順序）。
 * sock 沒給就用第一個送出這個 commandId 的那一條
 */
export function resultOrder(app, commandId, sock = null) {
  const frames = app.frames ?? [];
  const cmd = frames.find((f) => f.kind === 'cmd' && f.commandId === commandId && (sock === null || f.sock === sock));
  if (!cmd) return '沒有這個 Cmd';
  const done = frames.find((f) => f.kind === 'completion' && f.sock === cmd.sock && f.inv === cmd.inv && f.seq > cmd.seq);
  const res = frames.find((f) => f.kind === 'result' && f.sock === cmd.sock && f.commandId === commandId);
  if (!done || !res) return `Cmd 的回傳${done ? '有' : '沒有'}、commandResult ${res ? '有' : '沒有'}`;
  return res.seq < done.seq ? 'commandResult 先到' : 'Cmd 的回傳先到';
}
