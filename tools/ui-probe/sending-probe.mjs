// 送出中（Web/sendrules.js、conversation.js 的 addSending）：probe.mjs 匯入，--sending 只跑它，完整執行時也跑。
// 假宿主的 send 只記錄、不回應（host.ignore），探針自己決定什麼時候回使用者訊息、佇列或 sendRejected，模擬宿主忙、遠端繞一大圈。
// 真的沒有送出時（sendRejected、本機 90 秒都沒有回應）那則留在對話欄、標出原因，有「重新送出、放回輸入框、移除」。
// 輸入區的「繼續」鈕（預設 sendrules.CONTINUE_TEXT，可在設定頁改成自己的 continueText）送的也是一般的 send：一樣先畫送出中、輸入框打到一半的字不動，說明跟著執行中與能不能插入。
// 檢查名稱 sending*，截圖 sending-*.png
const LAYOUT = { panes: [{ id: 'p1', tabs: ['c2'], active: 'c2' }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' };

const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
/** 宿主 PlainToHtml 的寫法：HtmlEncode＋換行轉 <br> */
const plainToHtml = (s) => esc(s).replace(/\r\n/g, '\n').replace(/\n/g, '<br>');

/** 對話欄的狀態：送出中（與沒有送出、不確定）的那幾則、最後一則正式的使用者訊息、位置與捲動、輸入框與佇列 */
function sendingState(sel) {
  const root = document.querySelector(sel);
  if (!root) return null;
  const list = root.querySelector('.sending-list');
  const items = root.querySelector('.items');
  const pend = Array.from(list ? list.querySelectorAll(':scope > .it-user.sending') : []);
  const users = Array.from(items.querySelectorAll(':scope > .it-user'));
  const last = items.lastElementChild;
  const first = pend[0];
  const scroller = root.querySelector('.scroller');
  const text = (n, s) => { const el = n.querySelector(s); return el ? el.textContent : null; };
  const kind = (n) => (n.classList.contains('failed') ? 'failed' : n.classList.contains('unknown') ? 'unknown' : 'pending');
  return {
    pending: pend.map((n) => ({
      kind: kind(n), text: text(n, '.sending-text'), state: text(n, '.sending-state'), fail: text(n, '.sending-fail-text'), files: text(n, '.sending-files'),
      time: !!n.querySelector('.it-time[data-ms]'), acts: Array.from(n.querySelectorAll('.sending-acts button')).map((b) => b.textContent),
      alert: !!n.querySelector('[role="alert"]'),
    })),
    injected: !!(list && list.querySelector('b, img, script')),
    lastUser: users.length ? users[users.length - 1].querySelector('.u-text').textContent : null,
    userCount: users.length,
    // 送出中的排在所有項目之後，而且和上面的項目隔開（第一則照樣有 margin）
    afterItems: first && last ? first.getBoundingClientRect().top - last.getBoundingClientRect().bottom : null,
    atBottom: scroller ? scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight < 6 : null,
    input: root.querySelector('.cinput').value,
    queueRows: Array.from(root.querySelectorAll('.queue-row .queue-text')).map((n) => n.textContent),
    queueSum: (root.querySelector('.queue:not([hidden]) .queue-sum') || {}).textContent || null,
    toasts: Array.from(document.querySelectorAll('.toast')).map((t) => t.textContent),
  };
}

/** 執行中輸入區右下角的停止鈕與送出鈕：送出鈕相對停止鈕的位置與大小、箭頭偏離送出鈕中心多少、送出鈕自己的上下 margin 與框線 */
function sendButtonAlign(sel) {
  const root = document.querySelector(sel);
  const stop = root && root.querySelector('.cbtn.stop'), send = root && root.querySelector('.cbtn.send');
  if (!stop || !send || stop.hidden || send.hidden) return null;
  const a = stop.getBoundingClientRect(), b = send.getBoundingClientRect(), ic = send.querySelector('.ic').getBoundingClientRect();
  const r = (v) => Math.round(v * 10) / 10;
  const cs = getComputedStyle(send);
  return {
    dTop: r(b.top - a.top), dHeight: r(b.height - a.height), dWidth: r(b.width - a.width),
    iconDx: r(ic.left + ic.width / 2 - (b.left + b.width / 2)), iconDy: r(ic.top + ic.height / 2 - (b.top + b.height / 2)),
    marginY: cs.marginTop + ' ' + cs.marginBottom, border: cs.borderTopWidth + ' ' + cs.borderTopStyle, cls: send.className,
  };
}
const aligned = (m) => !!m && [m.dTop, m.dHeight, m.dWidth, m.iconDx, m.iconDy].every((v) => Math.abs(v) < 0.5)
  && m.marginY === '0px 0px' && m.border.startsWith('0px');

/** 一個開著的 app 上的操作：送出、回使用者訊息、退回、按泡泡上的按鈕 */
function driver(ctx, app, id) {
  const { until, settle } = ctx;
  const { page, host } = app;
  const sel = ctx.convSel(id);
  const ta = sel + ' .cinput';
  const c = host.conv(id);
  let seq = 9000;
  const state = () => page.evaluate(sendingState, sel);
  const sends = () => host.received.filter((m) => m.cmd === 'send');
  return {
    sel, ta, c, state, sends,
    async submit(text) {
      const before = sends().length;
      await page.fill(ta, text);
      await page.focus(ta);
      await page.keyboard.press('Enter');
      await until(() => sends().length > before, 2000);
      await settle(page, 120);
      return state();
    },
    async reply(text) {
      const user = { id: 'su' + (++seq), kind: 'User', status: 'Done', time: Date.now(), html: plainToHtml(text), seq };
      c.items.push(user);
      host.pushItem(id, user);
      await settle(page, 120);
      return state();
    },
    async reject(text, reason) {
      await host.send(Object.assign({ type: 'sendRejected', conversationId: id, text }, reason ? { reason } : {}));
      await settle(page, 150);
      return state();
    },
    async act(cls) {
      await page.click(sel + ' .sending-list .it-user.sending .' + cls);
      await settle(page, 150);
      return state();
    },
  };
}

export async function runSending(ctx, vp) {
  const { openApp, closeApp, check, settle, contrastOf, outFile, waitConv } = ctx;
  const app = await openApp('sending', { vp, layout: LAYOUT, hostSetup: (host) => host.ignore.add('send') });
  const { page } = app;
  try {
    await waitConv(page, 'c2');
    await settle(page, 200);
    const d = driver(ctx, app, 'c2');
    const { sel, ta, c } = d;
    const s0 = await d.state();

    // 不在執行中：按下送出就先畫出來（宿主還沒回）；文字照原樣（不當 HTML、保留換行），寫「送出中…」，排在最後、貼底
    const text = '請看這段 <b>不是粗體</b> & 符號\n第二行';
    const s1 = await d.submit(text);
    check('sendingShownBeforeHost', s1.pending.length === 1 && s1.pending[0].text === text && s1.pending[0].state === '送出中…' && !s1.pending[0].time
      && s1.pending[0].kind === 'pending' && !s1.injected && s1.input === '' && s1.userCount === s0.userCount && s1.afterItems >= 12 && s1.atBottom,
    { s0: { userCount: s0.userCount }, s1 });
    await settle(page, 900); // 進場動畫（fx-in）跑完再拍、再量對比
    await page.locator(sel).screenshot({ path: outFile('sending-pending-dark.png') });
    const contrast = await page.evaluate(contrastOf, sel + ' .sending-text');
    check('sendingTextContrast', contrast !== null && contrast >= 4.5, { contrast });

    // 宿主的使用者訊息到了：換成正式的，不重複
    const s2 = await d.reply(text);
    // 正式的那則是宿主轉的 HTML（換行是 <br>，textContent 沒有換行字元）
    check('sendingReplacedByHostItem', s2.pending.length === 0 && s2.userCount === s0.userCount + 1 && s2.lastUser === text.split(String.fromCharCode(10)).join(''), s2);

    // 被拒絕（桌面的 sendRejected 帶 reason）：留在對話欄、紅色標出原因與三個按鈕，字照舊還原到輸入框
    const reason = '工作資料夾不存在：D:\\gone，請重新選擇';
    const s3 = await d.submit('這則會被拒絕');
    const s4 = await d.reject('這則會被拒絕', reason);
    const f = s4.pending[0];
    check('sendingFailedStaysWithReason', s3.pending.length === 1 && s4.pending.length === 1 && f.kind === 'failed' && f.fail === '沒有送出：' + reason
      && f.alert && f.acts.join('、') === '重新送出、放回輸入框、移除' && f.text === '這則會被拒絕' && s4.input === '這則會被拒絕' && s4.userCount === s2.userCount,
    { s3, s4 });
    await settle(page, 300);
    await page.locator(sel).screenshot({ path: outFile('sending-failed-dark.png') });
    const failContrast = await page.evaluate(contrastOf, sel + ' .sending-fail-text');
    check('sendingFailedContrast', failContrast !== null && failContrast >= 4.5, { contrast: failContrast });

    // 重新送出：再送同樣的字（輸入框裡被退回的同一段一起清掉），沒有送出的那則換成新的「送出中」，宿主回了就換成正式的
    const before = d.sends().length;
    const s5 = await d.act('sending-resend');
    const resent = d.sends().slice(before);
    const s6 = await d.reply('這則會被拒絕');
    check('sendingResend', resent.length === 1 && resent[0].text === '這則會被拒絕' && s5.pending.length === 1 && s5.pending[0].kind === 'pending'
      && s5.input === '' && s6.pending.length === 0 && s6.lastUser === '這則會被拒絕', { resent, s5, s6 });

    // 移除：只收掉對話欄那則，輸入框的字照舊
    await d.submit('要移除的');
    const s7 = await d.reject('要移除的', '帳戶未登入');
    const s8 = await d.act('sending-remove');
    check('sendingRemove', s7.pending.length === 1 && s7.pending[0].kind === 'failed' && s8.pending.length === 0 && s8.input === '要移除的', { s7, s8 });

    // 放回輸入框：輸入框已經有別的字時插進去（不覆寫），對話欄那則收掉；沒有原因的退回只寫「沒有送出」，再到的指令結果帶原因時換上
    await page.fill(ta, '');
    await d.submit('放回去的');
    await page.fill(ta, '別的字');
    const s9 = await d.reject('放回去的');
    const s10 = await d.act('sending-putback');
    check('sendingPutBack', s9.pending.length === 1 && s9.pending[0].fail === '沒有送出' && s9.input === '別的字' && s10.pending.length === 0
      && s10.input.includes('別的字') && s10.input.includes('放回去的'), { s9, s10 });
    await page.fill(ta, '');

    // 繼續鈕：輸入框有打到一半的字時按下，送出固定的一句（一般的 send、先畫送出中），輸入框的字不動
    const contSel = sel + ' .cbar .ibtn.cont';
    const contInfo = () => page.evaluate((s) => {
      const b = document.querySelector(s);
      if (!b) return null;
      const r = b.getBoundingClientRect();
      return { title: b.title, label: b.getAttribute('aria-label'), disabled: b.disabled, w: r.width, h: r.height, icon: !!b.querySelector('svg') };
    }, contSel);
    await page.fill(ta, '打到一半的字');
    const cont0 = await contInfo();
    const contBefore = d.sends().length;
    await page.click(contSel);
    await ctx.until(() => d.sends().length > contBefore, 2000);
    await settle(page, 150);
    const contSent = d.sends().slice(contBefore);
    const cs1 = await d.state();
    await page.locator(sel + ' .cbox').screenshot({ path: outFile('sending-continue-dark.png') });
    const cs2 = await d.reply('進度如何呢如果還沒做完就繼續');
    check('sendingContinueButton', !!cont0 && cont0.title === '繼續：送出「進度如何呢如果還沒做完就繼續」' && cont0.label === cont0.title && !cont0.disabled
      && cont0.w >= 24 && cont0.h >= 24 && cont0.icon
      && contSent.length === 1 && contSent[0].text === '進度如何呢如果還沒做完就繼續' && contSent[0].conversationId === 'c2'
      && cs1.pending.length === 1 && cs1.pending[0].text === '進度如何呢如果還沒做完就繼續' && cs1.input === '打到一半的字'
      && cs2.pending.length === 0 && cs2.lastUser === '進度如何呢如果還沒做完就繼續', { cont0, contSent, cs1, cs2 });
    await page.fill(ta, '');

    // 自訂的指令（設定頁「對話 → 輸入」）：離開輸入框時送 setting continueText，按鈕改送那一句（去頭尾空白）、說明跟著換；
    // 「恢復預設」送回預設的那一句，按鈕也變回去
    const contRow = '.dlg-settings .set-row[data-setting="continueText"]';
    const openChatSettings = async () => {
      await page.click('#btnSettings');
      await page.waitForSelector('.dlg-settings');
      await page.click('.dlg-settings .set-tab[data-tab="chat"]');
      await settle(page, 150);
    };
    const closeSettings = async () => { await page.keyboard.press('Escape'); await settle(page, 300); };
    const settingsSent = () => app.host.received.filter((m) => m.cmd === 'setting' && m.key === 'continueText');
    await openChatSettings();
    const field0 = await page.$eval(contRow + ' textarea', (t) => ({ value: t.value, max: t.maxLength, reset: t.closest('.set-row').querySelector('.set-actions .btn').disabled }));
    await page.fill(contRow + ' textarea', '  看一下測試結果，沒過就修  ');
    await page.click(contRow + ' .set-label'); // 離開輸入框就儲存
    await ctx.until(() => settingsSent().length >= 1, 2000);
    await settle(page, 150);
    const field1 = await page.$eval(contRow + ' textarea', (t) => ({ value: t.value, reset: t.closest('.set-row').querySelector('.set-actions .btn').disabled }));
    await page.locator('.dlg-settings').screenshot({ path: outFile('sending-continue-settings-dark.png') });
    await closeSettings();
    const contCustom = await contInfo();
    const customBefore = d.sends().length;
    await page.click(contSel);
    await ctx.until(() => d.sends().length > customBefore, 2000);
    await settle(page, 150);
    const customSent = d.sends().slice(customBefore);
    const cs3 = await d.reply('看一下測試結果，沒過就修');
    await openChatSettings();
    await page.click(contRow + ' .set-actions .btn');
    await ctx.until(() => settingsSent().length >= 2, 2000);
    await settle(page, 150);
    const field2 = await page.$eval(contRow + ' textarea', (t) => ({ value: t.value, reset: t.closest('.set-row').querySelector('.set-actions .btn').disabled }));
    await closeSettings();
    const contBack = await contInfo();
    const sentSettings = settingsSent().map((m) => m.value);
    check('sendingContinueCustomText', field0.value === '進度如何呢如果還沒做完就繼續' && field0.max === 2000 && field0.reset === true
      && sentSettings.length === 2 && sentSettings[0] === '  看一下測試結果，沒過就修  ' && sentSettings[1] === '進度如何呢如果還沒做完就繼續'
      && field1.value === '看一下測試結果，沒過就修' && field1.reset === false
      && !!contCustom && contCustom.title === '繼續：送出「看一下測試結果，沒過就修」'
      && customSent.length === 1 && customSent[0].text === '看一下測試結果，沒過就修' && cs3.pending.length === 0 && cs3.lastUser === '看一下測試結果，沒過就修'
      && field2.value === '進度如何呢如果還沒做完就繼續' && field2.reset === true && !!contBack && contBack.title === '繼續：送出「進度如何呢如果還沒做完就繼續」',
    { field0, field1, field2, sentSettings, contCustom, customSent, cs3, contBack });

    // 執行中、能插入：先畫出來；佇列標「正在插入」的那則照樣留著，插進去（使用者訊息到了）才換掉
    c.header = Object.assign({}, c.header, { running: true, status: '思考中…', startedAt: Date.now(), steer: true });
    app.host.pushHeader('c2');
    await settle(page, 150);
    const contSteer = await contInfo();
    // 執行中打了字：送出鈕（插入這一輪）和停止鈕同高、同大小，箭頭置中（送出鈕的狀態 class 以前叫 queue，和佇列面板的 .queue 撞名，
    // 套到 margin-bottom、左右不對稱的 padding 與虛線框：送出鈕往上歪、箭頭偏右）
    await page.fill(ta, '執行中再插一句');
    await settle(page, 100);
    const alignSteer = await page.evaluate(sendButtonAlign, sel);
    await page.locator(sel + ' .cbar').screenshot({ path: outFile('sending-running-buttons.png') });
    await page.fill(ta, '');
    const s11 = await d.submit('插進這一輪');
    c.composer = Object.assign({}, c.composer, { queue: [{ id: 'qs1', text: '插進這一輪' }], steering: 'qs1' });
    app.host.pushComposer('c2');
    await settle(page, 150);
    const s12 = await d.state();
    c.composer = Object.assign({}, c.composer, { queue: [], steering: null });
    app.host.pushComposer('c2');
    const s13 = await d.reply('插進這一輪');
    check('sendingKeptWhileSteering', s11.pending.length === 1 && s12.pending.length === 1 && s12.queueRows.includes('插進這一輪')
      && s13.pending.length === 0 && s13.lastUser === '插進這一輪', { s11, s12, s13 });

    // 插不進去、留在佇列（沒有標正在插入）：對話欄收掉，由佇列顯示
    const s14 = await d.submit('插不進去');
    c.composer = Object.assign({}, c.composer, { queue: [{ id: 'qs2', text: '插不進去' }], steering: null });
    app.host.pushComposer('c2');
    await settle(page, 150);
    const s15 = await d.state();
    check('sendingMovesToQueue', s14.pending.length === 1 && s15.pending.length === 0 && s15.queueRows.includes('插不進去') && s15.lastUser === '插進這一輪', { s14, s15 });

    // 執行中、不能插入（會排進佇列）：不畫在對話欄
    c.header = Object.assign({}, c.header, { steer: false });
    app.host.pushHeader('c2');
    await settle(page, 150);
    const s16 = await d.submit('排隊的訊息');
    check('sendingSkippedWhenQueued', s16.pending.length === 0 && s16.input === '', s16);
    // 不能插入（加入佇列）的送出鈕也一樣對齊
    await page.fill(ta, '排到這一輪之後');
    await settle(page, 100);
    const alignQueue = await page.evaluate(sendButtonAlign, sel);
    await page.fill(ta, '');
    check('sendingRunningButtonsAligned', aligned(alignSteer) && aligned(alignQueue), { alignSteer, alignQueue });
    // 繼續鈕的說明跟著送出鈕：執行中能插入的插進這一輪、不能插入的加入佇列（按鈕照樣可以按）
    const contQueue = await contInfo();
    check('sendingContinueTitleWhileRunning', !!contSteer && contSteer.title === '繼續：把「進度如何呢如果還沒做完就繼續」插入這一輪' && !contSteer.disabled
      && !!contQueue && contQueue.title === '繼續：把「進度如何呢如果還沒做完就繼續」加入佇列（這一輪完成後送出）' && !contQueue.disabled, { contSteer, contQueue });
  } finally {
    await closeApp(app);
  }

  // 淺色：本機一直沒有回應（假時鐘快轉 90 秒）→ 琥珀色「不確定有沒有送出」＋提示，晚到的使用者訊息照樣換掉；紅色、琥珀色的說明對比都夠
  const light = await openApp('sending-light', { vp, layout: LAYOUT, settings: { theme: 'light' }, colorScheme: 'light', hostSetup: (host) => host.ignore.add('send') });
  try {
    await waitConv(light.page, 'c2');
    await light.page.clock.install();
    await settle(light.page, 200);
    const d = driver(ctx, light, 'c2');
    const t1 = await d.submit('一直沒有回應');
    await light.page.clock.fastForward(91_000);
    await settle(light.page, 200);
    const t2 = await d.state();
    const u = t2.pending[0];
    const amber = await light.page.evaluate(contrastOf, d.sel + ' .it-user.unknown .sending-fail-text');
    await light.page.locator(d.sel).screenshot({ path: outFile('sending-unknown-light.png') });
    const t3 = await d.reply('一直沒有回應');
    check('sendingTimeoutUnknown', t1.pending.length === 1 && t1.pending[0].kind === 'pending' && t2.pending.length === 1 && u.kind === 'unknown'
      && u.fail === '等了 90 秒都沒有回應，可能沒有送出' && t2.toasts.some((x) => x.includes('等了 90 秒都沒有回應')) && t2.input === ''
      && t3.pending.length === 0 && t3.lastUser === '一直沒有回應', { t1, t2, t3 });
    await d.submit('淺色被拒');
    await d.reject('淺色被拒', '帳戶未登入');
    const red = await light.page.evaluate(contrastOf, d.sel + ' .it-user.failed .sending-fail-text');
    check('sendingFailLightContrast', amber !== null && amber >= 4.5 && red !== null && red >= 4.5, { amber, red });
  } finally {
    await closeApp(light);
  }
}
