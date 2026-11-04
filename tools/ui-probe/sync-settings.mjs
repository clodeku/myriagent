// 〔同步〕設定頁「多電腦同步」分頁的探針（WP3.4 第 7～9 步）。probe.mjs 只有接縫：init 帶 opts.remote、假宿主的 onCmd、
// 主迴圈呼叫 runSyncSettings（--sync 時只跑這一組）。夾具與檢查都在這個檔案，fixtures.mjs 不動。
// 檢查名稱見 docs/plans/2026-10-16-wp3.4-accounts-directory.md 第 7～9 步的表（第 9 步是瀏覽器模式的 browser*）；結果一樣寫進 out/report.json。

const SERVER = 'https://hub.example.com';
const DEVICE_ID = 'd_0123456789ABCDEFGHJKMNPQRS';
const HOME_ID = 'd_1123456789ABCDEFGHJKMNPQRS';
const OLD_ID = 'd_2123456789ABCDEFGHJKMNPQRS';
const STRANGE_ID = 'd_3123456789ABCDEFGHJKMNPQRS';
export const XSS_NAME = '<img src=x onerror=alert(1)>';
const CODE = 'MTLZ-XFCH';
const HUB_FP = 'ABCD-EFGH-JKMN-PQRS';
const NEW_HUB_FP = 'WXYZ-2345-6789-ABCD';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function until(fn, ms = 3000, step = 40) {
  const t0 = Date.now();
  while (Date.now() - t0 < ms) {
    const v = await fn();
    if (v) return v;
    await sleep(step);
  }
  return null;
}

/** SyncStatus 夾具（WPR 的 SyncStatus；local 是桌面的本機設定） */
export function syncStatus(now, kind) {
  const local = { paused: false, remotePaused: false, remoteControlLevel: 'Off', allowRemoteFullAccess: false };
  const base = {
    state: 'online', detail: null, server: SERVER, pending: 0, lastSyncAt: now - 90_000, clockSkewMs: null, deviceId: DEVICE_ID,
    deviceFingerprint: 'K7QF-M2XA', hubFingerprint: HUB_FP, pairing: null, confirm: null, local,
  };
  const pairing = (p) => Object.assign({}, base, { state: 'pairing', deviceId: null, hubFingerprint: null, lastSyncAt: null, pairing: p });
  // 配對碼的期限從產生夾具的當下起算：探針的 now 是啟動時刻，完整執行跑到這裡常已超過 10 分鐘，以 now 起算會顯示「已過期」（syncCodeShown 失敗）
  const code = { userCode: CODE, verificationUri: SERVER + '/#pair', verificationUriComplete: SERVER + '/#pair=' + CODE, expiresAt: Date.now() + 600_000, error: null, openable: true };
  switch (kind) {
    case 'unpairedNull': return null;
    // 宿主的 SyncCoordinatorRules.PairingStarting：還沒拿到配對碼時全是空字串
    case 'starting': return pairing({ userCode: '', verificationUri: '', verificationUriComplete: '', expiresAt: 0, error: null, openable: false });
    case 'code': return pairing(code);
    case 'codeNotOpenable': return pairing(Object.assign({}, code, { verificationUriComplete: 'https://hub.example.com.evil.x/#pair=' + CODE, openable: false }));
    case 'error': return pairing(Object.assign({}, code, { error: '配對碼已過期' }));
    case 'online': return base;
    case 'offlinePending': return Object.assign({}, base, { state: 'offline', pending: 12, detail: '伺服器連不上，30 秒後重試' });
    case 'paused': return Object.assign({}, base, { state: 'paused', local: Object.assign({}, local, { paused: true }) });
    case 'stopped': return Object.assign({}, base, { state: 'stopped', detail: '這台的權杖已被撤銷，請解除配對後重新配對' });
    case 'confirm': return Object.assign({}, base, { state: 'stopped', detail: '伺服器身分換了，要在這台確認', confirm: { kind: 'hubIdentity', fingerprint: NEW_HUB_FP } });
    case 'skew': return Object.assign({}, base, { clockSkewMs: 185_000 });
    // 系統匣「暫停遠端操作」：本機設定照舊（可以操作），回報給伺服器的是 Off
    case 'remotePaused': return Object.assign({}, base, { local: Object.assign({}, local, { remotePaused: true, remoteControlLevel: 'Control' }) });
    default: throw new Error('沒有這種 SyncStatus 夾具：' + kind);
  }
}

/** DeviceView 夾具：這台、另一台（名稱是 XSS 字串、離線）、沒有名冊的陌生裝置、已撤銷的舊電腦（宿主排在最後） */
export function syncDevices(now) {
  const dev = (o) => Object.assign({
    id: DEVICE_ID, name: 'OFFICE-PC', signedName: 'OFFICE-PC', self: false, machineName: 'OFFICE-PC', appVersion: '0.3.0', fingerprint: 'K7QF-M2XA',
    keyStorage: 'software', online: true, lastSeenAt: now - 5000, remoteControlLevel: 'Off', allowRemoteFullAccess: false, verified: true, revoked: null,
  }, o);
  return [
    dev({ self: true }),
    dev({ id: HOME_ID, name: XSS_NAME, signedName: 'HOME-PC', machineName: 'HOME-PC', appVersion: '0.2.9', fingerprint: 'H0ME-PC12', online: false, lastSeenAt: now - 3 * 3600_000, remoteControlLevel: 'View' }),
    dev({ id: STRANGE_ID, name: 'STRANGER', signedName: null, machineName: null, appVersion: null, fingerprint: 'STRA-NGE1', online: false, lastSeenAt: null, verified: false }),
    dev({ id: OLD_ID, name: 'OLD-PC', signedName: 'OLD-PC', machineName: 'OLD-PC', fingerprint: 'OLD0-PC12', online: false, lastSeenAt: now - 40 * 86400_000, revoked: { at: now - 30 * 86400_000, reason: 'retired' } }),
  ];
}

/** init.remote（桌面）：sync 是 null 時整個 remote 是 null（未配對） */
export function remoteOf(now, kind, devices) {
  const sync = syncStatus(now, kind);
  return sync ? { mode: 'desktop', deviceId: sync.deviceId, devices: devices || [], sync } : null;
}

// CSP 違規（securitypolicyviolation）記在頁面上
const CSP_WATCH = `(() => { window.__csp = []; document.addEventListener('securitypolicyviolation', (e) => window.__csp.push(e.violatedDirective + ' ' + (e.blockedURI || ''))); })();`;

/**
 * 開 app、打開設定的「多電腦同步」分頁。onCmd：假宿主收到同步指令時的回應（預設不回，指令照樣記在 host.received）；
 * initExtra：併進假宿主 init 的欄位（例如 syncStarting；不動 probe.mjs 的假宿主）
 */
async function openSync(ctx, name, vp, remote, o = {}) {
  const app = await ctx.openApp(name, {
    vp, remote, settings: o.settings, colorScheme: o.colorScheme, initScript: CSP_WATCH,
    hostSetup: (host) => {
      if (o.onCmd) host.onCmd = o.onCmd;
      if (o.initExtra) {
        const send = host.send.bind(host);
        host.send = (msg) => send(msg && msg.type === 'init' ? Object.assign({}, msg, o.initExtra) : msg);
      }
    },
  });
  const { page } = app;
  await page.click('#btnSettings');
  await page.waitForSelector('.dlg-settings');
  await page.click('.dlg-settings .set-tab[data-tab="sync"]');
  await page.waitForSelector('.dlg-settings .sync[data-phase]');
  await sleep(150);
  return app;
}

const phaseOf = (page) => page.$eval('.dlg-settings .sync', (n) => n.dataset.phase).catch(() => null);
const waitPhase = (page, phase, ms = 3000) => until(async () => (await phaseOf(page)) === phase, ms);
const textOf = (page, sel) => page.$eval(sel, (n) => n.textContent).catch(() => null);
const shown = (page, sel) => page.evaluate((s) => {
  const el = document.querySelector(s);
  if (!el) return false;
  const cs = getComputedStyle(el);
  const r = el.getBoundingClientRect();
  return cs.display !== 'none' && cs.visibility !== 'hidden' && r.width > 0 && r.height > 0;
}, sel);
const count = (host, cmd) => host.received.filter((m) => m.cmd === cmd).length;
const sel = (act) => `.dlg-settings .sync [data-act="${act}"]`;
// 截圖前把分組捲到設定面板的頂端（已配對的分組在狀態之下，不捲看不到）
const scrollTo = (page, group) => page.$eval(`.dlg-settings .sync [data-group="${group}"]`, (n) => n.scrollIntoView({ block: 'start' })).catch(() => {});

async function closeWithCsp(ctx, app, csp) {
  const v = await app.page.evaluate(() => window.__csp || []).catch(() => ['（讀不到）']);
  csp.push(...v.map((x) => app.name + '：' + x));
  await ctx.closeApp(app);
}

/** 第 7、8 步的檢查（vp＝主要尺寸、深色跑全部；另外以 1100 寬、深淺色跑版面與對比） */
export async function runSyncSettings(ctx, vp) {
  const { check } = ctx;
  const NOW = Date.now();
  const csp = [];
  const T = (name) => 'sync-' + name + '-' + vp.w;

  // syncUnpairedPair：沒填 Hub 網址不送、顯示說明；網址補上 https:// 並只留 origin；名稱照填，留空時沒有 deviceName
  {
    const app = await openSync(ctx, T('unpaired'), vp, null);
    const { page, host } = app;
    const phase = await phaseOf(page);
    const maxLength = await page.$eval('.dlg-settings .sync [data-field="pairName"]', (i) => i.maxLength);
    await page.evaluate(() => { try { localStorage.removeItem('myriagent.pairServer'); } catch { /* 沒有 localStorage */ } });
    await page.fill('.dlg-settings .sync [data-field="pairServer"]', '');
    await page.click(sel('pair'));
    await sleep(200);
    const noServer = count(host, 'syncPair');
    const errShown = await page.$eval('.dlg-settings .sync [data-field="pairServerError"]', (e) => !e.hidden && e.textContent.length > 0);
    await page.fill('.dlg-settings .sync [data-field="pairServer"]', 'hub.example.com/');
    await page.fill('.dlg-settings .sync [data-field="pairName"]', '辦公室的電腦');
    await page.click(sel('pair'));
    const named = await until(() => host.last('syncPair'));
    await page.fill('.dlg-settings .sync [data-field="pairName"]', '   ');
    await page.click(sel('pair'));
    await until(() => count(host, 'syncPair') === 2);
    const blank = host.last('syncPair');
    await ctx.shot(page, T('unpaired'));
    check('syncUnpairedPair', phase === 'unpaired' && maxLength === 60 && noServer === 0 && errShown
      && !!named && named.deviceName === '辦公室的電腦' && named.server === 'https://hub.example.com'
      && !!blank && !('deviceName' in blank) && blank.server === 'https://hub.example.com', { phase, maxLength, noServer, errShown, named, blank });
    await closeWithCsp(ctx, app, csp);
  }

  // syncComponentStarting（F4）：init.syncStarting 時（已配對的電腦啟動後頭幾秒 remote 也是 null）不顯示配對表單、顯示啟動中；
  // 收到 sync:null 才是未配對，收到狀態就是已配對
  {
    const app = await openSync(ctx, T('component-starting'), vp, null, { initExtra: { syncStarting: true } });
    const { page, host } = app;
    const p0 = await phaseOf(page);
    const text = await textOf(page, '.dlg-settings .sync');
    const form = !!(await page.$('.dlg-settings .sync [data-field="pairName"]'));
    const pairBtn = !!(await page.$(sel('pair')));
    await ctx.shot(page, T('component-starting'));
    await host.send({ type: 'sync', sync: null });
    const p1 = await waitPhase(page, 'unpaired');
    await closeWithCsp(ctx, app, csp);

    const paired = await openSync(ctx, T('component-starting-paired'), vp, null, { initExtra: { syncStarting: true } });
    const q0 = await phaseOf(paired.page);
    await paired.host.send({ type: 'sync', sync: syncStatus(NOW, 'online') });
    const q1 = await waitPhase(paired.page, 'paired');
    check('syncComponentStarting', p0 === 'starting' && !!text && text.includes('同步元件啟動中') && !form && !pairBtn && !!p1
      && q0 === 'starting' && !!q1, { p0, text, form, pairBtn, unpaired: !!p1, q0, paired: !!q1 });
    await closeWithCsp(ctx, paired, csp);
  }

  // syncStartingCancel：取得配對碼中，「取消」送 syncCancelPair
  {
    const app = await openSync(ctx, T('starting'), vp, remoteOf(NOW, 'starting'));
    const { page, host } = app;
    const phase = await phaseOf(page);
    const text = await textOf(page, '.dlg-settings .sync .sync-box');
    await page.click(sel('cancelPair'));
    const cancel = await until(() => host.last('syncCancelPair'));
    check('syncStartingCancel', phase === 'pairingStart' && !!text && text.includes(SERVER) && !!cancel, { phase, text, cancel });
    await closeWithCsp(ctx, app, csp);
  }

  // syncCodeShown：碼、網址、指紋都在；倒數遞減；倒數期間其他節點不重建；複製（Clipboard API 與宿主備援）；在瀏覽器開啟
  {
    const app = await openSync(ctx, T('code'), vp, remoteOf(NOW, 'code'));
    const { page, host } = app;
    const phase = await phaseOf(page);
    const code = await textOf(page, '.dlg-settings .sync .sync-code');
    const uri = await textOf(page, '.dlg-settings .sync [data-field="verificationUri"]');
    const fp = await textOf(page, '.dlg-settings .sync [data-field="deviceFingerprint"]');
    await page.evaluate(() => {
      for (const n of document.querySelectorAll('.dlg-settings .sync .sync-code, .dlg-settings .sync .sync-phase, .dlg-settings .sync [data-act="cancelPair"]')) n.__probeMark = true;
    });
    const t1 = await textOf(page, '.dlg-settings .sync .sync-countdown');
    await sleep(2200);
    const t2 = await textOf(page, '.dlg-settings .sync .sync-countdown');
    const kept = await page.evaluate(() => Array.from(document.querySelectorAll('.dlg-settings .sync .sync-code, .dlg-settings .sync .sync-phase, .dlg-settings .sync [data-act="cancelPair"]')).every((n) => n.__probeMark === true));
    const secs = (t) => { const m = /(\d+):(\d\d)/.exec(t || ''); return m ? Number(m[1]) * 60 + Number(m[2]) : null; };
    // 複製：Clipboard API 成功時不送 copyText；失敗時改送 copyText
    await page.evaluate(() => { navigator.clipboard.writeText = async (t) => { window.__copied = t; }; });
    await page.click(sel('copyUri'));
    const copied = await until(() => page.evaluate(() => window.__copied));
    const copyTextAfterApi = count(host, 'copyText');
    await page.evaluate(() => { navigator.clipboard.writeText = async () => { throw new Error('denied'); }; });
    await page.click(sel('copyUri'));
    const fallback = await until(() => host.last('copyText'));
    const openShown = await shown(page, sel('openPairing'));
    await page.click(sel('openPairing'));
    const opened = await until(() => host.last('openLink'));
    await ctx.shot(page, T('code'));
    await page.click(sel('cancelPair'));
    const cancel = await until(() => host.last('syncCancelPair'));
    check('syncCodeShown', phase === 'pairingCode' && code === CODE && uri === SERVER + '/#pair' && fp === 'K7QF-M2XA'
      && secs(t1) !== null && secs(t2) !== null && secs(t2) < secs(t1) && kept
      && copied === SERVER + '/#pair' && copyTextAfterApi === 0 && !!fallback && fallback.text === SERVER + '/#pair'
      && openShown && !!opened && opened.href === SERVER + '/#pair=' + CODE && !!cancel,
    { phase, code, uri, fp, countdown: [t1, t2], kept, copied, copyTextAfterApi, fallback, openShown, opened, cancel });
    await closeWithCsp(ctx, app, csp);
  }

  // syncCodeNotOpenable：openable 是 false 時沒有「在瀏覽器開啟」，網址文字仍在
  {
    const app = await openSync(ctx, T('code-not-openable'), vp, remoteOf(NOW, 'codeNotOpenable'));
    const { page, host } = app;
    const openShown = await shown(page, sel('openPairing'));
    const uri = await textOf(page, '.dlg-settings .sync [data-field="verificationUri"]');
    // 宿主改成 openable 之後才出現
    await host.send({ type: 'sync', sync: syncStatus(NOW, 'code') });
    const later = await until(() => shown(page, sel('openPairing')));
    check('syncCodeNotOpenable', !openShown && uri === SERVER + '/#pair' && !!later && count(host, 'openLink') === 0, { openShown, uri, later: !!later });
    await closeWithCsp(ctx, app, csp);
  }

  // syncCodeFx（第七批）：換一組配對碼時逐字解碼；到期倒數條（20 格，總長是 600 秒的有效期）剛出現時是滿的，剩不到一分鐘縮到最後一格、轉琥珀色
  {
    const app = await openSync(ctx, T('code-fx'), vp, remoteOf(NOW, 'code'));
    const { page, host } = app;
    const bar0 = await until(() => page.evaluate(() => {
      const b = document.querySelector('.dlg-settings .sync .sync-cd');
      return b && !b.hidden ? { scale: new DOMMatrix(getComputedStyle(b.firstChild).transform).a, warn: b.classList.contains('warn'), color: getComputedStyle(b.firstChild).backgroundColor } : null;
    }), 2500);
    await page.evaluate(() => {
      window.__decode = null;
      const code = document.querySelector('.dlg-settings .sync .sync-code');
      const mo = new MutationObserver(() => { if (!window.__decode && code.querySelector('.tdx-c.scr')) window.__decode = code.querySelectorAll('.tdx-c').length; });
      mo.observe(code, { childList: true, subtree: true, attributes: true, attributeFilter: ['class'] });
    });
    const NEW_CODE = 'QRST-UVWX';
    const soon = Object.assign(syncStatus(NOW, 'code'));
    soon.pairing = Object.assign({}, soon.pairing, { userCode: NEW_CODE, verificationUriComplete: SERVER + '/#pair=' + NEW_CODE, expiresAt: Date.now() + 600_000 });
    await host.send({ type: 'sync', sync: soon });
    const decoded = await until(() => page.evaluate(() => window.__decode), 1500, 10);
    const settled = await until(() => page.evaluate((c) => { const e = document.querySelector('.dlg-settings .sync .sync-code'); return e.textContent === c && !e.querySelector('.tdx-c') ? true : null; }, NEW_CODE), 2000);
    // 到期時間改成 30 秒後（等於時間快轉）：倒數條縮到最後一格、轉琥珀色
    const late = Object.assign({}, soon, { pairing: Object.assign({}, soon.pairing, { expiresAt: Date.now() + 30_000 }) });
    await host.send({ type: 'sync', sync: late });
    const bar1 = await until(() => page.evaluate(() => {
      const b = document.querySelector('.dlg-settings .sync .sync-cd');
      // 倒數條的 transform 與顏色有轉場（600ms、300ms）：等走完
      const v = b && b.classList.contains('warn') ? { scale: new DOMMatrix(getComputedStyle(b.firstChild).transform).a, color: getComputedStyle(b.firstChild).backgroundColor } : null;
      return v && v.scale <= 0.1 ? v : null;
    }), 2500);
    await ctx.shot(page, T('code-fx'));
    check('syncCodeFx', !!bar0 && bar0.scale > 0.95 && !bar0.warn && decoded === NEW_CODE.length && !!settled && !!bar1 && bar1.scale <= 0.1 && bar1.color !== bar0.color,
      { bar0, decoded, settled, bar1 });
    await closeWithCsp(ctx, app, csp);
  }

  // syncErrorRetry：錯誤文字顯示；可以重新配對、取消
  {
    const app = await openSync(ctx, T('error'), vp, remoteOf(NOW, 'error'));
    const { page, host } = app;
    const phase = await phaseOf(page);
    const text = await textOf(page, '.dlg-settings .sync .note.err');
    await ctx.shot(page, T('error'));
    await page.click(sel('pair'));
    const again = await until(() => host.last('syncPair'));
    await page.click(sel('cancelPair'));
    const cancel = await until(() => host.last('syncCancelPair'));
    check('syncErrorRetry', phase === 'pairingError' && !!text && text.includes('配對碼已過期') && !!again && !!cancel, { phase, text, again, cancel });
    await closeWithCsp(ctx, app, csp);
  }

  // syncOnlineDetails：已配對的欄位都顯示；各狀態的膠囊文字與樣式照表
  {
    const app = await openSync(ctx, T('online'), vp, remoteOf(NOW, 'online'));
    const { page, host } = app;
    const read = () => page.evaluate(() => {
      const q = (f) => { const n = document.querySelector('.dlg-settings .sync [data-field="' + f + '"]'); return n ? n.textContent : null; };
      const pill = document.querySelector('.dlg-settings .sync [data-field="state"]');
      return { phase: document.querySelector('.dlg-settings .sync').dataset.phase, pill: pill ? pill.textContent : null, cls: pill ? pill.className : null,
        server: q('server'), device: q('deviceFingerprint'), hub: q('hubFingerprint'), pending: q('pending'), last: q('lastSyncAt') };
    });
    const online = await read();
    await ctx.shot(page, T('online'));
    const expect = { online: ['已連線', 'st-ready'], connecting: ['連線中', 'st-busy'], pairing: ['配對中', 'st-busy'], offline: ['離線', 'st-notLoggedIn'], paused: ['已暫停', 'st-checking'], stopped: ['已停止', 'st-error'] };
    const pills = {};
    for (const [st, [label, tone]] of Object.entries(expect)) {
      await host.send({ type: 'sync', sync: Object.assign(syncStatus(NOW, 'online'), { state: st }) });
      const r = await until(async () => { const v = await read(); return v.pill === label ? v : null; });
      pills[st] = { ok: !!r && r.cls.split(' ').includes(tone), got: r ? [r.pill, r.cls] : null };
    }
    check('syncOnlineDetails', online.phase === 'paired' && online.pill === '已連線' && online.cls.includes('st-ready') && online.server === SERVER
      && online.device === 'K7QF-M2XA' && online.hub === HUB_FP && online.pending === '0 筆' && !!online.last && online.last !== '還沒有'
      && Object.values(pills).every((p) => p.ok), { online, pills });
    await closeWithCsp(ctx, app, csp);
  }

  // syncOfflinePending：離線與待送筆數、說明
  {
    const app = await openSync(ctx, T('offline'), vp, remoteOf(NOW, 'offlinePending'));
    const { page } = app;
    const pill = await textOf(page, '.dlg-settings .sync [data-field="state"]');
    const pending = await textOf(page, '.dlg-settings .sync [data-field="pending"]');
    const detail = await textOf(page, '.dlg-settings .sync [data-field="detail"]');
    await ctx.shot(page, T('offline'));
    check('syncOfflinePending', pill === '離線' && pending === '12 筆' && detail === '伺服器連不上，30 秒後重試', { pill, pending, detail });
    await closeWithCsp(ctx, app, csp);
  }

  // syncPauseResume：依 local.paused 顯示暫停或繼續，各送一筆（宿主重送 sync 才換按鈕）；syncRetrySends：立即重試
  {
    const onCmd = (m, host) => {
      if (m.cmd !== 'syncPause') return;
      const s = syncStatus(NOW, m.paused ? 'paused' : 'online');
      host.send({ type: 'sync', sync: s });
    };
    const app = await openSync(ctx, T('pause'), vp, remoteOf(NOW, 'online'), { onCmd });
    const { page, host } = app;
    const before = await textOf(page, sel('pause'));
    await page.click(sel('pause'));
    const p1 = await until(() => host.last('syncPause'));
    const resumeShown = await until(() => page.$(sel('resume')));
    const after = await textOf(page, sel('resume'));
    await page.click(sel('resume'));
    await until(() => count(host, 'syncPause') === 2);
    const p2 = host.last('syncPause');
    const backToPause = await until(() => page.$(sel('pause')));
    check('syncPauseResume', before === '暫停同步' && !!p1 && p1.paused === true && !!resumeShown && after === '繼續同步' && !!p2 && p2.paused === false && !!backToPause
      && count(host, 'syncPause') === 2, { before, p1, after, p2 });
    await page.click(sel('retry'));
    const retry = await until(() => host.last('syncRetry'));
    check('syncRetrySends', !!retry && count(host, 'syncRetry') === 1, { retry });
    await closeWithCsp(ctx, app, csp);
  }

  // syncUnpairNeedsConfirm：確認框按取消送 0 筆 syncUnpair，按確認送 1 筆
  {
    const app = await openSync(ctx, T('unpair'), vp, remoteOf(NOW, 'online'));
    const { page, host } = app;
    await page.click(sel('unpair'));
    await page.waitForSelector('.dlg-confirm');
    const text = await textOf(page, '.dlg-confirm');
    await ctx.shot(page, T('unpair-confirm'));
    await page.click('.dlg-confirm .btn:not(.danger-solid)');
    await sleep(250);
    const afterCancel = count(host, 'syncUnpair');
    await page.click(sel('unpair'));
    await page.waitForSelector('.dlg-confirm .btn.danger-solid');
    await page.click('.dlg-confirm .btn.danger-solid');
    await until(() => count(host, 'syncUnpair') === 1);
    check('syncUnpairNeedsConfirm', afterCancel === 0 && count(host, 'syncUnpair') === 1 && !!text && text.includes('本機帳戶與對話不動') && text.includes('撤銷這台'),
      { afterCancel, unpair: count(host, 'syncUnpair'), text });
    await closeWithCsp(ctx, app, csp);
  }

  // syncConfirmFingerprintExact：確認前 0 筆；確認後 fingerprint 和夾具完全相同
  {
    const app = await openSync(ctx, T('confirm'), vp, remoteOf(NOW, 'confirm'));
    const { page, host } = app;
    const phase = await phaseOf(page);
    const fp = await textOf(page, '.dlg-settings .sync [data-field="confirmFingerprint"]');
    const box = await textOf(page, '.dlg-settings .sync .sync-box.warn');
    await ctx.shot(page, T('confirm'));
    await page.click(sel('confirmHub'));
    await page.waitForSelector('.dlg-confirm .btn.primary');
    const beforeOk = count(host, 'syncConfirm');
    await page.click('.dlg-confirm .btn.primary');
    const sent = await until(() => host.last('syncConfirm'));
    check('syncConfirmFingerprintExact', phase === 'confirm' && fp === NEW_HUB_FP && !!box && box.includes('admin status') && box.includes('不要拿網頁上的比')
      && beforeOk === 0 && !!sent && sent.kind === 'hubIdentity' && sent.fingerprint === NEW_HUB_FP && count(host, 'syncConfirm') === 1,
    { phase, fp, beforeOk, sent });
    await closeWithCsp(ctx, app, csp);
  }

  // syncConfirmChangedNoSend：確認框開著時宿主送了新的指紋：按確認送 0 筆、提示重新比對、畫面換成新的指紋
  {
    const app = await openSync(ctx, T('confirm-changed'), vp, remoteOf(NOW, 'confirm'));
    const { page, host } = app;
    await page.click(sel('confirmHub'));
    await page.waitForSelector('.dlg-confirm .btn.primary');
    const changed = Object.assign(syncStatus(NOW, 'confirm'), { confirm: { kind: 'hubIdentity', fingerprint: 'QQQQ-RRRR-SSSS-TTTT' } });
    await host.send({ type: 'sync', sync: changed });
    await sleep(200);
    await page.click('.dlg-confirm .btn.primary');
    await sleep(300);
    const note = await shown(page, '.dlg-settings .sync [data-field="confirmChanged"]');
    const fp = await textOf(page, '.dlg-settings .sync [data-field="confirmFingerprint"]');
    await ctx.shot(page, T('confirm-changed'));
    check('syncConfirmChangedNoSend', count(host, 'syncConfirm') === 0 && note && fp === 'QQQQ-RRRR-SSSS-TTTT', { sent: count(host, 'syncConfirm'), note, fp });
    await closeWithCsp(ctx, app, csp);
  }

  // syncSkewWarning：時間差 3 分鐘時有警告；沒有時間差時沒有
  {
    const app = await openSync(ctx, T('skew'), vp, remoteOf(NOW, 'skew'));
    const { page, host } = app;
    const on = await shown(page, '.dlg-settings .sync [data-field="skew"]');
    const text = await textOf(page, '.dlg-settings .sync [data-field="skew"]');
    await host.send({ type: 'sync', sync: syncStatus(NOW, 'online') });
    const off = await until(async () => !(await shown(page, '.dlg-settings .sync [data-field="skew"]')));
    check('syncSkewWarning', on && !!text && text.includes('約 3 分鐘') && !!off, { on, text, off: !!off });
    await closeWithCsp(ctx, app, csp);
  }

  // syncSelfBuildsRemote：init.remote 是 null 之後收到 sync，區塊自己出現；syncNullResets：收到 sync:null 回到未配對
  {
    const app = await openSync(ctx, T('self-build'), vp, null);
    const { page, host } = app;
    const p0 = await phaseOf(page);
    await host.send({ type: 'sync', sync: syncStatus(NOW, 'code') });
    const p1 = await waitPhase(page, 'pairingCode');
    await host.send({ type: 'devices', devices: syncDevices(NOW) });
    await host.send({ type: 'sync', sync: syncStatus(NOW, 'online') });
    const p2 = await waitPhase(page, 'paired');
    check('syncSelfBuildsRemote', p0 === 'unpaired' && !!p1 && !!p2, { p0, code: !!p1, paired: !!p2 });
    await host.send({ type: 'sync', sync: null });
    const p3 = await waitPhase(page, 'unpaired');
    // 回到未配對之後的 devices 不會讓區塊又出現
    await host.send({ type: 'devices', devices: syncDevices(NOW) });
    await sleep(150);
    const p4 = await phaseOf(page);
    check('syncNullResets', !!p3 && p4 === 'unpaired', { reset: !!p3, afterDevices: p4 });
    await closeWithCsp(ctx, app, csp);
  }

  // ── 第 8 步：其他電腦的遠端操作、這台改名、已配對的電腦 ──
  // 假宿主照 syncSetPolicy 改 local 再重送 sync（WPR：改了之後宿主立刻重送）
  const policyHost = (start) => {
    let status = start;
    return (m, host) => {
      if (m.cmd !== 'syncSetPolicy') return;
      const local = Object.assign({}, status.local);
      if (typeof m.remoteControlLevel === 'string') local.remoteControlLevel = m.remoteControlLevel;
      if (typeof m.allowRemoteFullAccess === 'boolean') local.allowRemoteFullAccess = m.allowRemoteFullAccess;
      status = Object.assign({}, status, { local });
      host.send({ type: 'sync', sync: status });
    };
  };
  const levelSel = '.dlg-settings .sync [data-field="remoteControlLevel"]';
  const fullSel = '.dlg-settings .sync [data-field="allowRemoteFullAccess"]';
  const levelOn = (page) => page.$eval(levelSel + ' .seg-btn.on', (b) => b.dataset.value).catch(() => null);
  const fullOn = (page) => page.$eval(fullSel, (i) => i.checked).catch(() => null);
  const policies = (host) => host.received.filter((m) => m.cmd === 'syncSetPolicy');

  // syncPolicySends：三種層級各送一筆、值正確（只帶 remoteControlLevel）；改成「可以操作」先確認、按確認才送（WP4.6 D30）；
  // 畫面照宿主重送的 local；文字不寫成 Remote Control。「只能查看」不收任何遠端指令、連停止也不收（WP4.4 的閘門 5e）：說明不能寫成可以停止
  {
    const app = await openSync(ctx, T('policy'), vp, remoteOf(NOW, 'online', syncDevices(NOW)), { onCmd: policyHost(syncStatus(NOW, 'online')) });
    const { page, host } = app;
    const group = await textOf(page, '.dlg-settings .sync [data-group="policy"]');
    const viewTitle = await page.$eval(levelSel + ' .seg-btn[data-value="View"]', (b) => b.title).catch(() => null);
    const initial = await levelOn(page);
    const steps = [];
    let confirmText = null;
    for (const v of ['View', 'Control', 'Off']) {
      const n = policies(host).length;
      await page.click(levelSel + ` .seg-btn[data-value="${v}"]`);
      if (v === 'Control') {
        await page.waitForSelector('.dlg-confirm .btn.danger-solid');
        await sleep(150);
        // 確認框開著時還沒送
        const before = policies(host).length;
        confirmText = await textOf(page, '.dlg-confirm');
        await page.click('.dlg-confirm .btn.danger-solid');
        if (before !== n) steps.push({ v, early: before - n, ok: false });
      }
      await until(() => policies(host).length === n + 1);
      const sent = policies(host)[n];
      const on = await until(async () => ((await levelOn(page)) === v ? v : null));
      steps.push({ v, sent, on, ok: !!sent && sent.remoteControlLevel === v && !('allowRemoteFullAccess' in sent) && on === v });
    }
    await sleep(150);
    await scrollTo(page, 'policy');
    await ctx.shot(page, T('policy'));
    const viewOk = !!viewTitle && viewTitle.includes('不能做任何操作') && viewTitle.includes('連停止也不收') && !viewTitle.includes('可以停止')
      && !!group && group.includes('連停止也不行') && !group.includes('也可以停止');
    check('syncPolicySends', initial === 'Off' && steps.every((x) => x.ok) && policies(host).length === 3 && !!group
      && group.includes('其他電腦的遠端操作') && group.includes('只能在這台改，馬上生效。') && !group.includes('第 4 階段') && !/Remote Control/i.test(group)
      && !!confirmText && confirmText.includes('知道擁有者密碼、或拿到瀏覽器登入狀態的人，都能在這台送訊息、核准與回答') && viewOk,
    { initial, steps, sent: policies(host).length, confirmText, viewTitle, viewOk });
    await closeWithCsp(ctx, app, csp);
  }

  // syncPolicyControlCancel：從「不接受」改成「可以操作」，確認框按取消——一筆都不送、分段選擇回到「不接受」
  {
    const app = await openSync(ctx, T('policy-control-cancel'), vp, remoteOf(NOW, 'online', syncDevices(NOW)), { onCmd: policyHost(syncStatus(NOW, 'online')) });
    const { page, host } = app;
    const initial = await levelOn(page);
    await page.click(levelSel + ' .seg-btn[data-value="Control"]');
    await page.waitForSelector('.dlg-confirm .btn.danger-solid');
    const text = await textOf(page, '.dlg-confirm');
    await ctx.shot(page, T('policy-control-confirm'));
    await page.click('.dlg-confirm .btn:not(.danger-solid)');
    const back = await until(async () => ((await levelOn(page)) === 'Off' ? 'Off' : null));
    await sleep(250);
    const dialog = await page.$('.dlg-confirm');
    check('syncPolicyControlCancel', initial === 'Off' && !!text && text.includes('允許操作') && policies(host).length === 0 && back === 'Off' && !dialog,
      { initial, text, sent: policies(host).length, back, dialog: !!dialog });
    await closeWithCsp(ctx, app, csp);
  }

  // syncFullAccessConfirmCancel：打開時先確認——取消送 0 筆、開關回到關；確認送 1 筆 true；關閉不確認、直接送 false
  {
    const app = await openSync(ctx, T('full-access'), vp, remoteOf(NOW, 'online', syncDevices(NOW)), { onCmd: policyHost(syncStatus(NOW, 'online')) });
    const { page, host } = app;
    await page.click(fullSel);
    await page.waitForSelector('.dlg-confirm .btn.danger-solid');
    const text = await textOf(page, '.dlg-confirm');
    await ctx.shot(page, T('full-access-confirm'));
    await page.click('.dlg-confirm .btn:not(.danger-solid)');
    await sleep(250);
    const afterCancel = policies(host).length;
    const offAfterCancel = await fullOn(page);
    await page.click(fullSel);
    await page.waitForSelector('.dlg-confirm .btn.danger-solid');
    await page.click('.dlg-confirm .btn.danger-solid');
    await until(() => policies(host).length === 1);
    const on = policies(host)[0];
    const onShown = await until(() => fullOn(page));
    await sleep(150);
    await page.click(fullSel);
    await until(() => policies(host).length === 2);
    const off = policies(host)[1];
    const dialogOnOff = await page.$('.dlg-confirm');
    const offShown = await until(async () => (await fullOn(page)) === false);
    check('syncFullAccessConfirmCancel', afterCancel === 0 && offAfterCancel === false && !!text && text.includes('完全自動')
      && !!on && on.allowRemoteFullAccess === true && !('remoteControlLevel' in on) && !!onShown
      && !!off && off.allowRemoteFullAccess === false && !dialogOnOff && !!offShown && policies(host).length === 2,
    { afterCancel, offAfterCancel, on, off, dialogOnOff: !!dialogOnOff, offShown: !!offShown });
    await closeWithCsp(ctx, app, csp);
  }

  // syncPolicyFromLocal：離線、devices 是空的時候政策照 local；之後伺服器回音（DeviceView）不同也不跟著變；確認階段也有政策與清單
  {
    const offline = Object.assign(syncStatus(NOW, 'offlinePending'), { local: { paused: false, remotePaused: false, remoteControlLevel: 'View', allowRemoteFullAccess: true } });
    const app = await openSync(ctx, T('policy-local'), vp, { mode: 'desktop', deviceId: DEVICE_ID, devices: [], sync: offline });
    const { page, host } = app;
    const level0 = await levelOn(page);
    const full0 = await fullOn(page);
    const empty = await shown(page, '.dlg-settings .sync [data-field="devicesEmpty"]');
    // 伺服器的 DeviceView 是另一個值（例如系統匣暫停時回報的 Off）
    const echo = syncDevices(NOW).map((d) => (d.self ? Object.assign({}, d, { remoteControlLevel: 'Off', allowRemoteFullAccess: false }) : d));
    await host.send({ type: 'devices', devices: echo });
    await until(() => page.$('.dlg-settings .sync .sync-dev'));
    const level1 = await levelOn(page);
    const full1 = await fullOn(page);
    await closeWithCsp(ctx, app, csp);
    const app2 = await openSync(ctx, T('policy-confirm'), vp, remoteOf(NOW, 'confirm', syncDevices(NOW)));
    const inConfirm = { policy: await shown(app2.page, '.dlg-settings .sync [data-group="policy"]'), devices: await shown(app2.page, '.dlg-settings .sync [data-group="devices"]'),
      rename: await shown(app2.page, '.dlg-settings .sync [data-group="rename"]') };
    await closeWithCsp(ctx, app2, csp);
    check('syncPolicyFromLocal', level0 === 'View' && full0 === true && empty && level1 === 'View' && full1 === true
      && inConfirm.policy && inConfirm.devices && inConfirm.rename, { level0, full0, empty, level1, full1, inConfirm });
  }

  // syncRemotePausedNote：local.remotePaused 時有說明（政策仍照本機的值）；恢復後說明消失
  {
    const app = await openSync(ctx, T('remote-paused'), vp, remoteOf(NOW, 'remotePaused', syncDevices(NOW)));
    const { page, host } = app;
    const on = await shown(page, '.dlg-settings .sync [data-field="remotePaused"]');
    const text = await textOf(page, '.dlg-settings .sync [data-field="remotePaused"]');
    const level = await levelOn(page);
    await scrollTo(page, 'policy');
    await ctx.shot(page, T('remote-paused'));
    await host.send({ type: 'sync', sync: syncStatus(NOW, 'online') });
    const off = await until(async () => !(await shown(page, '.dlg-settings .sync [data-field="remotePaused"]')));
    check('syncRemotePausedNote', on && !!text && text.includes('已從系統匣暫停遠端操作') && level === 'Control' && !!off, { on, text, level, off: !!off });
    await closeWithCsp(ctx, app, csp);
  }

  // syncRename：欄位是這台現在的名稱；儲存（按鈕與 Enter）送 deviceRename {deviceId, name}；空白不送；改到一半不被宿主的更新蓋掉
  {
    const app = await openSync(ctx, T('rename'), vp, remoteOf(NOW, 'online', syncDevices(NOW)));
    const { page, host } = app;
    const input = '.dlg-settings .sync [data-field="renameInput"]';
    const initial = await page.$eval(input, (i) => i.value);
    const maxLength = await page.$eval(input, (i) => i.maxLength);
    await page.fill(input, '  書房的電腦  ');
    // 改到一半時清單上的名稱被別處改了：欄位不換；存了之後，存的當下清單上的名稱也不蓋回來
    await host.send({ type: 'devices', devices: syncDevices(NOW).map((d) => (d.self ? Object.assign({}, d, { name: 'OFFICE-PC-2' }) : d)) });
    await sleep(150);
    const keptWhileTyping = await page.$eval(input, (i) => i.value);
    await page.click(sel('rename'));
    const first = await until(() => host.last('deviceRename'));
    await host.send({ type: 'sync', sync: syncStatus(NOW, 'online') });
    await sleep(150);
    const keptAfterSave = await page.$eval(input, (i) => i.value);
    await page.fill(input, '   ');
    await page.click(sel('rename'));
    await page.fill(input, '');
    await page.click(sel('rename'));
    await sleep(200);
    const afterBlank = count(host, 'deviceRename');
    await page.fill(input, 'DEN-PC');
    await page.press(input, 'Enter');
    await until(() => count(host, 'deviceRename') === 2);
    const second = host.last('deviceRename');
    // 宿主以 devices 回來：欄位換成清單裡的新名稱
    await host.send({ type: 'devices', devices: syncDevices(NOW).map((d) => (d.self ? Object.assign({}, d, { name: 'DEN PC' }) : d)) });
    const echoed = await until(async () => ((await page.$eval(input, (i) => i.value)) === 'DEN PC' ? true : null));
    check('syncRename', initial === 'OFFICE-PC' && maxLength === 60 && keptWhileTyping === '  書房的電腦  ' && keptAfterSave === '書房的電腦'
      && !!first && first.deviceId === DEVICE_ID && first.name === '書房的電腦' && afterBlank === 1
      && !!second && second.deviceId === DEVICE_ID && second.name === 'DEN-PC' && count(host, 'deviceRename') === 2 && !!echoed,
    { initial, maxLength, keptWhileTyping, keptAfterSave, first, afterBlank, second, echoed: !!echoed });
    await closeWithCsp(ctx, app, csp);
  }

  // syncDevicesTextOnly：清單裡沒有 img（名稱是 HTML 字串也一樣）、名稱的 textContent 等於原字串、沒有觸發 alert
  // syncDevicesRevoked：已撤銷的有標籤、排在最後（宿主給的順序不對也一樣）；這台、未驗證的標籤
  {
    const devs = syncDevices(NOW);
    const shuffled = [devs[3], devs[0], devs[1], devs[2]];
    const app = await openSync(ctx, T('devices'), vp, remoteOf(NOW, 'online', shuffled));
    const { page } = app;
    await page.evaluate(() => { window.__alerts = 0; window.alert = () => { window.__alerts++; }; });
    await sleep(100);
    const r = await page.evaluate(() => {
      const list = document.querySelector('.dlg-settings .sync [data-field="devices"]');
      const rows = Array.from(list.querySelectorAll('.sync-dev'));
      return {
        imgs: list.querySelectorAll('img').length,
        rows: rows.map((n) => ({ key: n.dataset.key, name: n.querySelector('.sync-dev-name').textContent, tags: Array.from(n.querySelectorAll('.tag')).map((t) => t.textContent),
          state: n.querySelector('.sync-dev-state').textContent, meta: n.querySelector('.sync-dev-meta').textContent })),
        alerts: window.__alerts,
      };
    });
    await scrollTo(page, 'rename');
    await ctx.shot(page, T('devices'));
    const row = (id) => r.rows.find((x) => x.key === id) || null;
    const home = row(HOME_ID);
    check('syncDevicesTextOnly', r.imgs === 0 && r.alerts === 0 && r.rows.length === 4 && !!home && home.name === XSS_NAME
      && home.meta.includes('配對時的名稱 HOME-PC') && home.meta.includes('遠端操作：只能查看') && home.meta.includes('版本 0.2.9') && home.meta.includes('H0ME-PC12') && home.state.startsWith('最後上線'),
    { imgs: r.imgs, alerts: r.alerts, home });
    const self = row(DEVICE_ID);
    const strange = row(STRANGE_ID);
    const last = r.rows[r.rows.length - 1];
    check('syncDevicesRevoked', !!last && last.key === OLD_ID && last.tags.includes('已撤銷') && last.state.includes('撤銷於') && last.state.includes('退役')
      && r.rows.filter((x) => x.tags.includes('已撤銷')).length === 1 && !!self && self.tags.includes('這台') && self.state === '在線'
      && !!strange && strange.tags.includes('未驗證') && strange.state === '還沒有上線紀錄' && strange.meta.includes('版本不明'),
    { order: r.rows.map((x) => x.key), last, self, strange });
    // syncDevicesLeds（第七批）：名稱前面的燈——在線綠、5 分鐘內上線過淡綠、更早或沒有紀錄暗、已撤銷紅框
    const leds = () => page.evaluate(() => Object.fromEntries(Array.from(document.querySelectorAll('.dlg-settings .sync [data-field="devices"] .sync-dev'))
      .map((n) => [n.dataset.key, n.dataset.led + ':' + getComputedStyle(n.querySelector('.sync-dev-name'), '::before').backgroundColor])));
    const led0 = await leds();
    const recentDevs = syncDevices(NOW).map((d) => (d.id === HOME_ID ? Object.assign({}, d, { lastSeenAt: Date.now() - 60_000 }) : d));
    await app.host.send({ type: 'devices', devices: recentDevs });
    const led1 = await until(async () => { const l = await leds(); return l[HOME_ID] && l[HOME_ID].startsWith('recent:') ? l : null; }, 2000);
    const ledOf = (m, id) => (m && m[id] ? m[id].split(':')[0] : null);
    check('syncDevicesLeds', ledOf(led0, DEVICE_ID) === 'on' && ledOf(led0, HOME_ID) === 'off' && ledOf(led0, STRANGE_ID) === 'off' && ledOf(led0, OLD_ID) === 'revoked'
      && !!led1 && led1[HOME_ID].split(':')[1] !== led0[HOME_ID].split(':')[1] && led1[DEVICE_ID].split(':')[1] !== led1[HOME_ID].split(':')[1],
    { led0, led1 });
    await closeWithCsp(ctx, app, csp);
  }

  // syncNoOverflow（1100 寬）與 syncContrast（深淺色）：各階段的版面不溢出、文字對比 ≥ 4.5:1
  const overflow = {};
  const low = {};
  const contrastSels = ['.sync .set-desc', '.sync .set-label', '.sync .sync-code', '.sync .sync-fp', '.sync .sync-countdown', '.sync .pill',
    '.sync .note.err', '.sync .note.warn', '.sync .sync-box.warn .set-desc', '.sync .btn',
    '.sync .set-h', '.sync .seg-btn', '.sync .seg-btn.on', '.sync .sync-dev-name', '.sync .sync-dev-seen', '.sync .sync-dev-meta', '.sync .sync-dev-meta .mono',
    '.sync .sync-dev .tag', '.sync .sync-dev .tag.warn', '.sync .sync-dev .tag.bad', '.sync .sync-dev .pill'];
  for (const theme of ['dark', 'light']) {
    for (const kind of ['unpairedNull', 'code', 'error', 'offlinePending', 'paused', 'skew', 'confirm', 'remotePaused']) {
      const narrow = { w: 1100, h: 700 };
      const app = await openSync(ctx, 'sync-layout-' + kind + '-' + theme, narrow, remoteOf(NOW, kind, syncDevices(NOW)), { settings: { theme }, colorScheme: theme });
      const { page } = app;
      const o = await page.evaluate(() => {
        const panels = document.querySelector('.dlg-settings .set-panels');
        const pr = panels.getBoundingClientRect();
        const out = Array.from(document.querySelectorAll('.dlg-settings .sync *')).filter((n) => {
          const r = n.getBoundingClientRect();
          return r.width > 0 && (r.right > pr.right + 1 || r.left < pr.left - 1);
        }).map((n) => n.className || n.tagName);
        return { scroll: panels.scrollWidth > panels.clientWidth + 1, out: out.slice(0, 5) };
      });
      if (o.scroll || o.out.length) overflow[kind + ' ' + theme] = o;
      for (const s of contrastSels) {
        const r = await page.evaluate(ctx.contrastOf, '.dlg-settings ' + s);
        if (r !== null && r < 4.5) low[theme + ' ' + kind + ' ' + s] = r;
      }
      if (theme === 'light') await ctx.shot(page, 'sync-layout-' + kind + '-light-1100');
      await closeWithCsp(ctx, app, csp);
    }
  }
  check('syncNoOverflow', Object.keys(overflow).length === 0, overflow);
  check('syncContrast', Object.keys(low).length === 0, low);

  await runBrowserSettings(ctx, vp, csp);
  check('syncNoCspViolations', csp.length === 0, csp);
}

// ── 第 9 步：瀏覽器模式（WP3.6 閘道）的設定頁只有四個漫遊鍵 ──

const ROAMING = ['theme', 'fontSize', 'enterToSend', 'notifications'];
/** 伺服器的 init.settings（WPR 第六節第 8 點：只有四個鍵） */
const BROWSER_SETTINGS = () => ({ theme: 'dark', fontSize: 14, enterToSend: true, notifications: true });
/** 瀏覽器模式的 init.remote（WPR 第六節第 8 點） */
const browserRemote = (now) => ({ mode: 'browser', deviceId: null, devices: syncDevices(now), sync: null });
/** WPR 第六節第 8 點「桌面專屬（desktop_only）」的指令，加上 CLI 的「立即檢查」（這台的 CLI，瀏覽器模式不建） */
const DESKTOP_ONLY = new Set(['setConversationDefault', 'cliApplyNow', 'cliContinue', 'cliCancelUpdate', 'cliCheck', 'chooseFolder', 'attach', 'addFiles',
  'pasteImage', 'addPathAttachment', 'openFolder', 'openPath', 'revealPath', 'saveFile', 'openLink', 'copyText', 'popout', 'accountOpenFolder', 'autoTitle']);
const desktopOnlySent = (host) => host.received.filter((m) => (typeof m.cmd === 'string' && m.cmd.startsWith('sync')) || DESKTOP_ONLY.has(m.cmd)
  || (m.cmd === 'setting' && !ROAMING.includes(m.key)));

/** 設定對話框的分頁、面板、區塊與列（含沒選取的分頁） */
const readSettingsDialog = (page) => page.evaluate(() => {
  // 關閉中的舊對話框（.dlg-back.leaving）淡出時還在畫面上，不算
  const d = document.querySelector('.dlg-back:not(.leaving) .dlg-settings');
  if (!d) return null;
  const sel = d.querySelector('.set-tab[aria-selected="true"]');
  return {
    tabs: Array.from(d.querySelectorAll('.set-tab')).map((t) => ({ id: t.dataset.tab, label: t.textContent })),
    sel: sel ? sel.dataset.tab : null,
    panels: Array.from(d.querySelectorAll('.set-panel')).map((p) => ({
      tab: p.dataset.tab,
      secs: Array.from(p.querySelectorAll('.set-sec')).map((s) => ({ id: s.dataset.sec, rows: Array.from(s.querySelectorAll('.set-row')).map((r) => r.dataset.setting || null) })),
    })),
    rows: Array.from(d.querySelectorAll('.set-row')).map((r) => ({ setting: r.dataset.setting || null, label: (r.querySelector('.set-label') || {}).textContent || null })),
    desktopNodes: d.querySelectorAll('.sync, .cli-grid, textarea.instr, [data-cli-check], .set-provs, .set-radios').length,
  };
});

async function openBrowserSettings(ctx, name, vp, o = {}) {
  const app = await ctx.openApp(name, {
    vp, remote: o.remote === undefined ? browserRemote(ctx.NOW) : o.remote, colorScheme: o.colorScheme, initScript: CSP_WATCH,
    hostSetup: (host) => { host.settings = o.settings || BROWSER_SETTINGS(); host.opts.devicesCli = {}; },
  });
  await app.page.click('#btnSettings');
  await app.page.waitForSelector('.dlg-settings');
  await sleep(200);
  return app;
}

async function runBrowserSettings(ctx, vp, csp) {
  const { check } = ctx;
  const T = (name) => 'browser-settings-' + name + '-' + vp.w;
  const sameSet = (a, b) => a.length === b.length && [...a].sort().join('|') === [...b].sort().join('|');
  const near = (x, y) => typeof x === 'number' && Math.abs(x - y) < 1e-3; // SpeechSynthesisUtterance 的 pitch 是 32 位元浮點數

  // browserSettingsOnlyRoaming：只看得到主題、字級、Enter 送出、系統通知，加上這個瀏覽器自己的語音通知與語速、音調（voiceLocal，存在 localStorage、不送 setting）
  // 與「繼續」鈕的指令（continueLocal，存在 localStorage 的 myriagent.continueText、不送 setting；恢復預設就清掉）；
  // 切換它們送出的 setting 鍵都在四個鍵裡；0 筆桌面專屬的指令；
  // 程式以 openSettings({section}) 指定沒建的區塊（多電腦同步、CLI）也照常打開；匯出的 ROAMING_KEYS／isBrowserMode 照 WPR
  // browserNoEmptyTabs：只剩「一般」（外觀＋通知）與「對話」（Enter 送出、「繼續」鈕的指令），每個分頁、每個區塊都有列；方向鍵在兩個分頁間循環
  {
    const app = await openBrowserSettings(ctx, T('roaming'), vp);
    const { page, host } = app;
    const exported = await page.evaluate(() => import('/settings.js').then((m) => ({ keys: Array.from(m.ROAMING_KEYS), browser: m.isBrowserMode() })));
    const r0 = await readSettingsDialog(page);
    await ctx.shot(page, T('general'));
    // 一般：主題（淺色→深色）、字級、系統通知
    const rowSel = (key) => `.dlg-settings .set-row[data-setting="${key}"]`;
    await page.click(rowSel('theme') + ' .seg-btn[data-value="light"]');
    await until(() => host.last('setting', (m) => m.key === 'theme'));
    await page.click(rowSel('theme') + ' .seg-btn[data-value="dark"]');
    await page.$eval(rowSel('fontSize') + ' input[type="range"]', (i) => { i.value = '16'; i.dispatchEvent(new Event('input', { bubbles: true })); i.dispatchEvent(new Event('change', { bubbles: true })); });
    await page.click(rowSel('notifications') + ' .switch');
    // 這個瀏覽器的語音通知與它底下的語速、音調：存在 localStorage，不送 setting（下面以 settingKeys 只有漫遊鍵驗證），放開滑桿時以新的值念一句試聽；
    // 關掉時兩個滑桿停用。語音合成先換成只記錄、不出聲的版本（探針的 Chrome 帶 --mute-audio 也擋不住系統語音引擎從喇叭念）
    await page.evaluate(() => {
      const synth = window.speechSynthesis;
      window.__probeSpoken = [];
      synth.speak = (u) => { window.__probeSpoken.push({ text: u.text, rate: u.rate, pitch: u.pitch }); };
      synth.cancel = () => {};
    });
    const tune = (k, v) => page.$eval(rowSel('voiceLocal') + ` input[data-tune="${k}"]`, (i, x) => {
      i.value = x;
      i.dispatchEvent(new Event('input', { bubbles: true }));
      i.dispatchEvent(new Event('change', { bubbles: true }));
    }, v);
    await tune('speed', '2');
    await tune('pitch', '-1');
    const tuneStored = await page.evaluate(() => [localStorage.getItem('myriagent.voice.speed'), localStorage.getItem('myriagent.voice.pitch')]);
    const previews = await page.evaluate(() => window.__probeSpoken);
    await page.click(rowSel('voiceLocal') + ' .switch');
    const voiceStored = await page.evaluate(() => localStorage.getItem('myriagent.voice'));
    const tuneOff = await page.$eval(rowSel('voiceLocal'), (r) => { const ins = Array.from(r.querySelectorAll('input[data-tune]')); return ins.length === 2 && ins.every((i) => i.disabled); });
    // 對話：Enter 送出
    await page.click('.dlg-settings .set-tab[data-tab="chat"]');
    const r1 = await readSettingsDialog(page);
    await ctx.shot(page, T('chat'));
    await page.click(rowSel('enterToSend') + ' .switch');
    // 「繼續」鈕的指令：離開輸入框時存進這個瀏覽器（去頭尾空白），輸入區的按鈕跟著換；恢復預設就清掉
    const contStored0 = await page.evaluate(() => localStorage.getItem('myriagent.continueText'));
    await page.fill(rowSel('continueLocal') + ' textarea', '  手機上看：做到哪了？  ');
    await page.click(rowSel('continueLocal') + ' .set-label');
    await sleep(150);
    const contStored1 = await page.evaluate(() => localStorage.getItem('myriagent.continueText'));
    const contField1 = await page.$eval(rowSel('continueLocal') + ' textarea', (t) => t.value);
    const contTitle1 = await page.evaluate(() => { const b = document.querySelector('.cbar .ibtn.cont'); return b ? b.title : null; });
    await page.click(rowSel('continueLocal') + ' .set-actions .btn');
    await sleep(150);
    const contStored2 = await page.evaluate(() => localStorage.getItem('myriagent.continueText'));
    const contField2 = await page.$eval(rowSel('continueLocal') + ' textarea', (t) => t.value);
    await until(() => ROAMING.every((k) => host.last('setting', (m) => m.key === k)));
    const settingKeys = host.received.filter((m) => m.cmd === 'setting').map((m) => m.key);
    // 鍵盤：End 到最後一個分頁（對話）、再往右循環回「一般」
    await page.focus('.dlg-settings .set-tab[aria-selected="true"]');
    await page.keyboard.press('End');
    const endTab = (await readSettingsDialog(page)).sel;
    await page.keyboard.press('ArrowRight');
    const wrapTab = (await readSettingsDialog(page)).sel;
    // 指定沒建的區塊：照常打開（停在上次的分頁），一樣只有兩個分頁
    await page.keyboard.press('Escape');
    await sleep(200);
    await page.evaluate(() => import('/settings.js').then((m) => m.openSettings({ section: 'sync' })));
    await page.waitForSelector('.dlg-settings');
    await sleep(250);
    const rSync = await readSettingsDialog(page);
    await page.keyboard.press('Escape');
    await sleep(200);
    await page.evaluate(() => import('/settings.js').then((m) => m.openSettings({ section: 'cli' })));
    await page.waitForSelector('.dlg-settings');
    await sleep(250);
    const rCli = await readSettingsDialog(page);
    const bad = desktopOnlySent(host);

    const labels = r0.rows.map((x) => x.label);
    check('browserSettingsOnlyRoaming', exported.browser === true && sameSet(exported.keys, ROAMING)
      && sameSet(r0.rows.map((x) => x.setting), [...ROAMING, 'voiceLocal', 'continueLocal']) && sameSet(labels, ['主題', '對話字級', '系統通知', '語音通知', 'Enter 送出', '「繼續」鈕的指令']) && r0.desktopNodes === 0
      && contStored0 === null && contStored1 === '手機上看：做到哪了？' && contField1 === '手機上看：做到哪了？' && (contTitle1 === null || contTitle1 === '繼續：送出「手機上看：做到哪了？」')
      && contStored2 === null && contField2 === '進度如何呢如果還沒做完就繼續'
      && voiceStored === 'off' && tuneStored[0] === '2' && tuneStored[1] === '-1' && tuneOff
      && previews.length === 2 && previews.every((x) => x.text === '「整理報表」已完成：月報已匯出成 Excel' && x.rate === 2)
      && near(previews[0].pitch, 1.122) && near(previews[1].pitch, 0.944)
      && ROAMING.every((k) => settingKeys.includes(k)) && settingKeys.every((k) => ROAMING.includes(k)) && bad.length === 0
      && !!rSync && rSync.tabs.length === 2 && rSync.desktopNodes === 0 && !!rCli && rCli.tabs.length === 2 && rCli.desktopNodes === 0,
    { exported, rows: r0.rows, desktopNodes: r0.desktopNodes, settingKeys, bad, voiceStored, tuneStored, tuneOff, previews, contStored0, contStored1, contField1, contTitle1, contStored2, contField2, afterSyncSection: rSync && { tabs: rSync.tabs, sel: rSync.sel }, afterCliSection: rCli && { tabs: rCli.tabs, sel: rCli.sel } });

    const panelOf = (r, tab) => r.panels.find((p) => p.tab === tab) || null;
    const general = panelOf(r0, 'general');
    const chat = panelOf(r1, 'chat');
    const noEmpty = (r) => r.panels.length === r.tabs.length && r.panels.every((p) => p.secs.length > 0 && p.secs.every((s) => s.rows.length > 0));
    check('browserNoEmptyTabs', same(r0.tabs.map((t) => t.label), ['一般', '對話']) && same(r0.tabs.map((t) => t.id), ['general', 'chat']) && r0.sel === 'general'
      && noEmpty(r0) && noEmpty(rSync) && noEmpty(rCli)
      && !!general && same(general.secs.map((s) => s.id), ['look', 'notify']) && same(general.secs[1].rows, ['notifications', 'voiceLocal'])
      && !!chat && same(chat.secs.map((s) => s.id), ['chat']) && same(chat.secs[0].rows, ['enterToSend', 'continueLocal']) && r1.sel === 'chat'
      && endTab === 'chat' && wrapTab === 'general',
    { tabs: r0.tabs, panels: r0.panels, endTab, wrapTab });
    await closeWithCsp(ctx, app, csp);
  }

  // browserLateInitRebuilds：設定開著時才收到瀏覽器模式的 init（模式變了）：對話框照新的模式重建，桌面專屬的列與分頁不留著；
  // 桌面一開始（init 沒有 remote）六個分頁都在
  {
    const app = await ctx.openApp(T('late-init'), { vp, initScript: CSP_WATCH });
    const { page, host } = app;
    await page.click('#btnSettings');
    await page.waitForSelector('.dlg-settings');
    await sleep(200);
    const before = await readSettingsDialog(page);
    // 假宿主照原本的 init 重送一次，只換 settings 與 remote（WPR 第六節第 8 點：第二則 init 整份取代）
    host.settings = BROWSER_SETTINGS();
    host.opts.remote = browserRemote(ctx.NOW);
    host.opts.devicesCli = {};
    host.handle({ cmd: 'ready' });
    const after = await until(async () => { const r = await readSettingsDialog(page); return r && r.tabs.length === 2 ? r : null; });
    await sleep(300);
    const settled = await readSettingsDialog(page);
    const dialogs = await page.$$eval('.dlg-back:not(.leaving) .dlg-settings', (ns) => ns.length);
    check('browserLateInitRebuilds', !!before && before.tabs.length === 7 && before.desktopNodes > 0
      && !!after && !!settled && same(settled.tabs.map((t) => t.id), ['general', 'chat']) && settled.desktopNodes === 0 && sameSet(settled.rows.map((x) => x.setting), [...ROAMING, 'voiceLocal', 'continueLocal'])
      && dialogs === 1 && desktopOnlySent(host).length === 0,
    { before: before && before.tabs.map((t) => t.id), after: settled && { tabs: settled.tabs.map((t) => t.id), rows: settled.rows, desktopNodes: settled.desktopNodes }, dialogs });
    await closeWithCsp(ctx, app, csp);
  }
}

const same = (a, b) => Array.isArray(a) && Array.isArray(b) && a.length === b.length && a.every((x, i) => x === b[i]);
