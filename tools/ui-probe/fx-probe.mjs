// 對話框的視覺效果（fx.css 的「對話框」兩段、Web/fxrules.js）的情境：probe.mjs 匯入，--fx 只跑它，完整執行時也跑。
// 檢查名稱 fx*：
// - 靜態裝飾：程式碼框的行數與角框、差異比例條、時間軸的方塊點、兩側刻度尺不壓到對話內容（單窗格與四窗格）、選取色是帳戶色
// - 一次性動畫：新項目進場（一批補進很多時不做、做完會拿掉 class）、一進場就結束的工具掃一次光、工具從執行中變成完成
//   （進場不到一秒就做完時結束的效果優先）、停下的餘暉、一輪結束的綠燈與紅燈、按停止後不閃、卡片出現的鎖定（鎖定期間不受繪製限制）、
//   複製程式碼、底部 token 數與待辦數字的解碼
// - 共用元件與回覆內容：選單的角框與選到那一項的帳戶色方塊、通知的角框與倒數條（滑過停住）、浮動說明的角框、設定頁的區塊標題、
//   清單符號、引用的方塊線、表格的等寬數字；卡片有了結果（允許綠、拒絕紅、略過琥珀、失效不閃）、整則回覆的複製、點開思考與工具時淡入、
//   側欄新出現的列（新對話、清掉搜尋時最多 12 列錯開）、自動命名的標題解碼（手動改名不解碼）、git 徽章與儀表板摘要的數字解碼
// - 2026-10-24 第三批：輸入框下方的 token 數在一輪進行中即時（turnLive）、差異的語法上色與逐字標示、/ 提示的角框與帳戶色方塊、換帳戶排程的虛線框與箭頭、
//   橫幅的方塊串與重新連線中的光、分隔線的刻度、帳戶卡數字解碼與徽章彈出（對話列的「!」也是）、附件淡入、語音訊息的波形、錄音音量表（假麥克風給真的音訊串流）、
//   檔案抽屜換資料夾時錯開進場、新分頁滑進來、打開儀表板時走勢線畫出來
// - 第四批：過載重試的倒數條與收尾、背景程式晶片的燈與結束的閃光、Ultracode、程式碼框行號、背景分頁一輪結束的閃燈、分頁徽章彈出、「繼續」鈕擴散、
//   對話框角框、分段選擇滑動、開機掃描（手機聽寫的預覽列在 live-probe 的 hubVoiceDictationPreview）
// - 工具做完時內容掃過一遍（fxToolPeek*）：長的輸出一格一行捲到最後、短的不捲、播完拿掉、下一個做完時上一個快速收起來、展開著的不播、點開時讓給真的內容
// - 思考預設展開（fxThinkDefaultOpen：執行中與結束後都是展開的、內容直接填）；收起來的思考結束時內容掃過一遍（fxThinkPeek*）：換行排、
//   量出的行數剛好是整數個行高、箭頭轉一下、短的不捲、展開著的不播、點開時讓給真的內容
// - 第五批：載入較早的訊息（載入列、接縫）、對話小地圖（標記、捲動時間軸的範圍框、點了跳過去）、拖放讀數、換帳戶交接、錯誤的訊號干擾、
//   Mermaid（關係、依序畫出、滑過亮起相連的）、主題切換的掃描、開關燈與游標、搜尋命中、額度用完的鎖定與重置倒數、健康度分格、滑鼠探照光
//   （遠端往返讀數在 live-probe 的 hubRttReadout）
// - 第六批：駭客任務綠的 token 數（片假名亂碼）、輸出速度、工具計時、等你回應的時間、自動壓縮的說明、捲動位置、燈箱的影像資訊、
//   檔案抽屜（比例條、最近改過、寬度讀數）、匯入的雷達與比例條、MCP 燈列、下載新版的讀數（矩陣十字準線在 remote-probe 的 matrixCrosshair）
// - 第七批：一輪的結算單、思考的耗時、差異的變動地圖（點了跳過去）、任務清單的方塊與計數、問卷數字鍵按下、用量燃燒預估、分頁拖放的角框與插入位置、
//   工具卡與核准卡的指令、JSON 參數、讀檔輸出的語法上色
//   （執行列的錯誤與警告數在 launch-probe 的 launchDiag、配對碼與遠端操作紀錄、裝置的燈在 sync-settings 的 sync*Fx、瀏覽器版的分頁圖示與上傳讀數在 live-probe）
// - 第十批：帳戶委派的工具卡與子對話的狀態、MCP 工具的晶片、使用者訊息的帳戶提及晶片、換帳戶提示的交接條、輸入框跟著權限模式、圖片載入前的佔位、
//   換頁的進場、側欄的子對話、標頭的來源晶片、每一輪分隔線的間隔、標題與段落的換行（延伸窗格的滑入在 fork-probe）
// - 系統要求減少動態、設定關掉動態效果時都不播（含工具與思考的內容掃過）、數字與標題不解碼、通知沒有倒數條
// 截圖 fx-*.png：深淺色的一輪結束與卡片鎖定（動畫停在固定的時間點）、標題解碼中的標題列、選單、通知、回覆內容、卡片的結果、整則複製、差異、語音波形、錄音音量表、
//   工具與思考的內容掃到一半（fx-tool-peek.png、fx-think-peek.png）

import { makeHealth } from './health-probe.mjs';
import { makeMcp } from './mcp-probe.mjs';
import * as F from './fixtures.mjs';

const C = 'c1';
const SEL = `.conv[data-conv="${C}"]`;
const onePane = (id) => ({ panes: [{ id: 'p1', tabs: [id], active: id }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' });
const FOUR_PANES = { panes: [{ id: 'p1', tabs: ['c1'], active: 'c1' }, { id: 'p2', tabs: ['c3'], active: 'c3' }, { id: 'p3', tabs: ['c4'], active: 'c4' }, { id: 'p4', tabs: ['c7'], active: 'c7' }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/** 頁內：這個元素（pseudo 是 '::before'／'::after' 或 null）身上的 CSS 動畫，名稱、次數、長度（含已播完但 fill 還在的） */
function animsIn([selector, pseudo]) {
  const el = document.querySelector(selector);
  if (!el) return null;
  return document.getAnimations()
    .filter((a) => a.effect && a.effect.target === el && (a.effect.pseudoElement || null) === (pseudo || null))
    .map((a) => { const t = a.effect.getTiming(); return { name: a.animationName || a.transitionProperty || '?', iterations: t.iterations, duration: t.duration, state: a.playState }; });
}

/** 頁內：一個對話的兩側刻度尺與對話項目的左右邊界（刻度尺是 .conv-col 的偽元素，位置從計算樣式推） */
function rulerGeometry(conv) {
  const col = document.querySelector(`.conv[data-conv="${conv}"] .conv-col`);
  if (!col) return null;
  const r = col.getBoundingClientRect();
  const b = getComputedStyle(col, '::before'), a = getComputedStyle(col, '::after');
  const left = { x0: r.left + parseFloat(b.left), x1: r.left + parseFloat(b.left) + parseFloat(b.width) };
  const right = { x0: r.right - parseFloat(a.right) - parseFloat(a.width), x1: r.right - parseFloat(a.right) };
  const its = Array.from(col.querySelectorAll('.items > .it')).map((e) => e.getBoundingClientRect()).filter((x) => x.width > 0);
  const sc = col.querySelector('.scroller');
  const th = col.querySelector('.thread');
  return {
    conv, colW: Math.round(r.width), left, right, beforeContent: b.content, afterContent: a.content,
    scrollbar: sc ? sc.offsetWidth - sc.clientWidth : null, overflow: sc ? sc.scrollHeight > sc.clientHeight : null,
    threadPad: th ? getComputedStyle(th).paddingRight : null, colRight: r.right,
    itLeft: its.length ? Math.min(...its.map((x) => x.left)) : null, itRight: its.length ? Math.max(...its.map((x) => x.right)) : null, items: its.length,
  };
}

/** 頁內（初始化腳本）：盯著 #app 有沒有加上 fx-boot（開機掃描），記下出現時的動畫與之後有沒有拿掉 */
function bootWatch() {
  window.__fxBoot = { seen: false, anims: [], cleared: false };
  const start = () => {
    const a = document.getElementById('app');
    if (!a) return;
    new MutationObserver(() => {
      const on = a.classList.contains('fx-boot');
      if (on && !window.__fxBoot.seen) {
        window.__fxBoot.seen = true;
        requestAnimationFrame(() => { window.__fxBoot.anims = document.getAnimations().map((x) => x.animationName).filter(Boolean); });
      } else if (!on && window.__fxBoot.seen) window.__fxBoot.cleared = true;
    }).observe(a, { attributes: true, attributeFilter: ['class'] });
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
}

const rulersClear = (g) => !!g && g.items > 0 && g.beforeContent !== 'none' && g.afterContent !== 'none'
  && g.left.x1 <= g.itLeft && g.right.x0 >= g.itRight;

export async function runFx(ctx, vp) {
  const { openApp, closeApp, check, until, settle, outFile, contrastOf } = ctx;
  const open = async (name, o = {}) => {
    const app = await openApp(name, Object.assign({ vp, layout: onePane(C) }, o));
    await app.page.waitForSelector(`${SEL}:not(.loading) .items`, { state: 'attached', timeout: 8000 });
    await settle(app.page, 200);
    return app;
  };
  const anims = (page, selector, pseudo = null) => page.evaluate(animsIn, [selector, pseudo]);
  const names = async (page, selector, pseudo = null) => ((await anims(page, selector, pseudo)) || []).map((a) => a.name);
  const hasClass = (page, selector, cls) => page.evaluate(([s, c]) => { const e = document.querySelector(s); return !!e && e.classList.contains(c); }, [selector, cls]);
  // 同一個工作裡送好幾則宿主訊息（網頁在同一批套用）；假宿主的 send 是一則一則 evaluate，可能跨影格
  const sendNow = (page, msgs) => page.evaluate((list) => { for (const m of list) window.__host(m); }, msgs);
  const header = (host, patch) => Object.assign(host.conv(C).header, patch);
  const pushHeader = (host, patch) => { header(host, patch); return host.pushHeader(C); };
  const item = (id, extra) => Object.assign({ id, kind: 'Assistant', status: 'Done', time: Date.now(), html: '<p>' + id + '</p>' }, extra);
  const tool = (id, status, extra) => item(id, Object.assign({ kind: 'Tool', status, toolKind: 'read', toolName: '讀取檔案', title: 'src/' + id + '.cs', html: undefined }, extra));
  // 解碼：輪詢到出現亂碼的格子，再等到文字等於新值
  const decodeSeen = (page, selector) => until(() => page.evaluate((s) => !!document.querySelector(s + ' .mtok-c.scr'), selector), 1500, 15);
  const textOf = (page, selector) => page.evaluate((s) => { const e = document.querySelector(s); return e ? e.textContent : null; }, selector);
  // 拍照時凍住一次性動畫：拿掉元素的計時器（util.replayClass 到時間會拿掉 class，動畫跟著消失）、重新加上 class 從頭再播
  // （等到這裡時沒有 fill 的動畫可能已經播完）、停在指定的時間點；回傳凍住的動畫名稱。拍完以 thaw 拿掉 class
  const shots = [];
  const freeze = async (pg, selector, classes, times) => {
    const frozen = await pg.evaluate(([s, cls, t]) => {
      const el = document.querySelector(s);
      for (const k of Object.keys(el._fxTimers || {})) clearTimeout(el._fxTimers[k]);
      el.classList.remove(...cls);
      void el.offsetWidth;
      el.classList.add(...cls);
      const out = [];
      for (const a of document.getAnimations()) if (a.animationName in t && !(a.timeline && !(a.timeline instanceof DocumentTimeline))) { a.pause(); a.currentTime = t[a.animationName]; out.push(a.animationName); }
      return out;
    }, [selector, classes, times]);
    shots.push({ selector, frozen, want: Object.keys(times) });
  };
  const thaw = (pg, selector, classes) => pg.evaluate(([s, cls]) => {
    document.querySelector(s).classList.remove(...cls);
    for (const a of document.getAnimations()) if (a.playState === 'paused') a.play();
  }, [selector, classes]);
  const CARD_AT = { 'fx-scan-in': 50, 'fx-lock-in': 80, 'fx-lock-blink': 80 };
  // 每一種上色類別都出現一次的回覆（C#、Bash、HTML、diff），量各類別的字對程式碼框底色的對比
  const esc = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  const block = (lang, code) => '<pre><code class="language-' + lang + '">' + esc(code) + '</code></pre>';
  const KINDS_HTML = block('cs', '#region 範例\npublic class Foo { void Run() { var s = "x"; int n = 42; } } // 註解')
    + block('bash', 'git commit -m "msg" $HOME') + block('html', '<div class="a">x</div>') + block('diff', '@@ -1 +1 @@\n-old\n+new');
  const kindContrast = async (pg, h) => {
    await h.pushItem(C, item('fxk1', { html: KINDS_HTML }));
    const kinds = await until(() => pg.evaluate((s) => {
      const codes = document.querySelectorAll(s + ' .it[data-id="fxk1"] .code code');
      if (codes.length < 4 || Array.from(codes).some((c) => !c.querySelector('[class^="tk-"]'))) return null;
      return [...new Set(Array.from(document.querySelectorAll(s + ' .it[data-id="fxk1"] .code [class^="tk-"]')).map((x) => x.className))].sort();
    }, SEL), 3000);
    const out = {};
    for (const k of kinds || []) out[k] = await pg.evaluate(contrastOf, `${SEL} .it[data-id="fxk1"] .code .${k}`);
    if (!kinds) out.diag = await pg.evaluate((s) => Array.from(document.querySelectorAll(s + ' .it[data-id="fxk1"] .code code')).map((c) => c.className + ':' + c.children.length), SEL);
    return out;
  };
  const pushCard = async (pg, h, id) => {
    // 等你核准的卡（i20）：比問卷卡矮，整張拍得進畫面
    const q = Object.assign(JSON.parse(JSON.stringify(h.conv(C).items.find((x) => x.id === 'i20'))), { id, time: Date.now() });
    await h.pushItem(C, q);
    const s = `${SEL} .it[data-id="${id}"]`;
    await until(() => pg.$(s), 2000);
    return s;
  };

  // ───── 深色、一般動態 ─────
  const app = await open('fx');
  const { page, host } = app;

  // 靜態：程式碼框的行數與角框
  const codes = await page.evaluate((s) => Array.from(document.querySelectorAll(s + ' .md .code')).map((c) => {
    const pre = c.querySelector('pre');
    const text = (pre ? pre.textContent : '').replace(/\r\n?/g, '\n').replace(/\n$/, '');
    const want = text ? text.split('\n').length : 0;
    const el = c.querySelector('.code-head .code-lines');
    return { want, got: el ? el.textContent : null, bracket: getComputedStyle(c, '::after').content, lang: getComputedStyle(c.querySelector('.code-lang'), '::before').content };
  }), SEL);
  check('fxCodeLines', codes.length > 0 && codes.every((c) => c.want > 0 && c.got === c.want + ' 行' && c.bracket !== 'none' && c.lang !== 'none'), codes);

  // 靜態：差異比例條（i6 是 +12 −3）、時間軸的方塊點
  const ratio = await page.evaluate((s) => {
    const r = document.querySelector(s + ' .it[data-id="i6"] .t-ratio');
    return r ? { hidden: r.getAttribute('aria-hidden'), cells: Array.from(r.children).map((x) => x.className) } : null;
  }, SEL);
  check('fxDiffRatio', !!ratio && ratio.hidden === 'true' && ratio.cells.join(' ') === 'tr-add tr-add tr-add tr-add tr-del', ratio);
  const dots = await page.evaluate((s) => Array.from(document.querySelectorAll(s + ' .step-dot')).map((d) => getComputedStyle(d).borderRadius), SEL);
  check('fxStepDotSquare', dots.length > 0 && dots.every((r) => r === '1.5px'), [...new Set(dots)]);

  // 靜態：兩側刻度尺貼在對話串外側，不壓到任何項目
  const g1 = await page.evaluate(rulerGeometry, C);
  check('fxRulersOnePane', rulersClear(g1), g1);

  // 靜態：選取色是半透明帳戶色（不是全域的焦點色）
  const selection = await page.evaluate((s) => {
    const conv = document.querySelector(s);
    const p = conv.querySelector('.md p');
    const ref = document.createElement('i');
    ref.style.background = 'color-mix(in srgb, var(--acc) 34%, transparent)';
    conv.append(ref);
    const want = getComputedStyle(ref).backgroundColor;
    ref.remove();
    return { got: getComputedStyle(p, '::selection').backgroundColor, want, global: getComputedStyle(document.body, '::selection').backgroundColor };
  }, SEL);
  check('fxSelectionAccent', selection.got === selection.want && selection.got !== selection.global, selection);

  // 進場：一則新的回覆淡入、上移；0.9 秒後拿掉 class
  await host.pushItem(C, item('fx1'));
  const n1 = `${SEL} .it[data-id="fx1"]`;
  await until(() => page.$(n1), 2000);
  const in1 = { cls: await hasClass(page, n1, 'fx-in'), anims: await names(page, n1) };
  check('fxEntranceSingle', in1.cls && in1.anims.includes('fx-in'), in1);
  await sleep(1000);
  check('fxEntranceCleanup', !(await hasClass(page, n1, 'fx-in')));

  // 進場：一批補進 8 個（重新連線）整批直接出現
  await sendNow(page, Array.from({ length: 8 }, (_, i) => ({ type: 'item', conversationId: C, item: item('fxb' + i) })));
  await until(() => page.$(`${SEL} .it[data-id="fxb7"]`), 2000);
  const batch = await page.evaluate((s) => Array.from(document.querySelectorAll(s + ' .it[data-id^="fxb"]')).map((e) => e.classList.contains('fx-in')), SEL);
  check('fxEntranceBatchSkipped', batch.length === 8 && batch.every((x) => !x), batch);

  // 工具一進場就已經完成（很快就做完的）：整列的光帶掃一次、點外的方框鎖定一次
  await host.pushItem(C, tool('fxt1', 'Done'));
  const t1 = `${SEL} .it[data-id="fxt1"]`;
  await until(() => page.$(t1), 2000);
  const sweep = await anims(page, t1, '::after');
  const lock = await names(page, t1 + ' .step-dot', '::after');
  check('fxToolEntryScan', !!sweep && sweep.some((a) => a.name === 'fx-sweep' && a.iterations === 1) && lock.includes('fx-lock-on'), { sweep, lock });

  // 工具從執行中變成完成：小方塊擴散、整列一道綠光淡出
  await host.pushItem(C, tool('fxt2', 'Running'));
  const t2 = `${SEL} .it[data-id="fxt2"]`;
  await until(() => page.$(t2), 2000);
  await sleep(1000); // 進場的效果結束
  await host.pushItem(C, tool('fxt2', 'Done'));
  await until(() => hasClass(page, t2, 'fx-done'), 1500, 15);
  const done2 = { row: await names(page, t2, '::after'), dot: await names(page, t2 + ' .step-dot', '::after') };
  check('fxToolSettleDone', done2.row.includes('fx-fade-out') && done2.dot.includes('fx-ping-sq'), done2);

  // 進場不到一秒就做完：結束的效果優先（不是進場的掃光與鎖定）
  await host.pushItem(C, tool('fxt3', 'Running'));
  const t3 = `${SEL} .it[data-id="fxt3"]`;
  await until(() => page.$(t3), 2000);
  await sleep(150);
  await host.pushItem(C, tool('fxt3', 'Failed'));
  await until(() => hasClass(page, t3, 'fx-fail'), 1500, 15);
  const quick = { fxIn: await hasClass(page, t3, 'fx-in'), row: await names(page, t3, '::after'), dot: await names(page, t3 + ' .step-dot', '::after') };
  check('fxToolSettleBeatsEntrance', quick.fxIn && quick.row.includes('fx-fade-out') && !quick.row.includes('fx-sweep')
    && quick.dot.includes('fx-ping-sq') && !quick.dot.includes('fx-lock-on'), quick);

  // 工具做完時內容掃過一遍（items.js 的 playStepPeek）：40 行的輸出在小框裡一格一行捲到最後、收起來後拿掉
  const peekOut = Array.from({ length: 40 }, (_, i) => `src/Module${i}.cs:${i + 1}:    public void Run${i}() => Execute(${i});`).join('\n');
  // 一個或多個工具卡的小框狀態（多個時在同一個工作裡讀，中間不會剛好套用下一批）
  const peekState = (pg, s) => pg.evaluate((sels) => {
    const one = (sel) => {
      const n = document.querySelector(sel);
      const p = n && n.querySelector(':scope > .t-peek');
      if (!p) return { has: false, peeking: !!n && n.classList.contains('peeking') };
      const run = p.querySelector('.t-peek-run');
      const an = (el) => el.getAnimations().map((a) => ({ name: a.animationName, duration: a.effect.getTiming().duration, delay: a.effect.getTiming().delay }));
      const ch = n.querySelector('.t-chev, .think-chev');
      // 換行排的（思考）：內容高度是不是剛好整數個行高（捲幾格＝移幾個行高才會停在最後一行）
      const rcs = getComputedStyle(run);
      const lh = 1.5 * parseFloat(rcs.fontSize);
      const content = run.getBoundingClientRect().height - parseFloat(rcs.paddingTop) - parseFloat(rcs.paddingBottom);
      return {
        has: true, peeking: n.classList.contains('peeking'), sweep: p.classList.contains('sweep'), fold: p.classList.contains('fold'), wrap: p.classList.contains('wrap'),
        lines: run.children.length, rows: p.style.getPropertyValue('--tp-rows'), steps: p.style.getPropertyValue('--tp-steps'),
        height: Math.round(p.getBoundingClientRect().height), box: getComputedStyle(p.querySelector('.t-peek-box')).borderLeftColor,
        chev: ch ? getComputedStyle(ch).transform : null, peekAnims: an(p), runAnims: an(run), runTiming: rcs.animationTimingFunction,
        afterRow: p.previousElementSibling === n.querySelector(':scope > .t-row, :scope > details.think'), hidden: p.getAttribute('aria-hidden'),
        textRows: Math.round((content / lh) * 100) / 100, firstLine: run.firstElementChild ? run.firstElementChild.textContent : null,
      };
    };
    return Array.isArray(sels) ? sels.map(one) : one(sels);
  }, s);
  const colorOf = (pg, v) => pg.evaluate((x) => { const i = document.createElement('i'); i.style.color = x; document.body.append(i); const c = getComputedStyle(i).color; i.remove(); return c; }, v);
  await host.pushItem(C, tool('fxp1', 'Running', { toolKind: 'search', toolName: '搜尋', title: 'Execute(', detail: '{"pattern":"Execute("}' }));
  const p1 = `${SEL} .it[data-id="fxp1"]`;
  await until(() => page.$(p1), 2000);
  await sleep(950);
  await host.pushItem(C, tool('fxp1', 'Done', { toolKind: 'search', toolName: '搜尋', title: 'Execute(', detail: '{"pattern":"Execute("}', output: peekOut }));
  const ps1 = await until(async () => { const s = await peekState(page, p1); return s.has ? s : null; }, 1500, 10);
  const peekGreen = await colorOf(page, 'var(--green)');
  check('fxToolPeekPlays', !!ps1 && ps1.sweep && ps1.peeking && ps1.lines === 40 && ps1.rows === '5' && ps1.steps === '35' && ps1.afterRow && ps1.hidden === 'true'
    && ps1.box === peekGreen && ps1.peekAnims.some((a) => a.name === 'fx-peek-open') && ps1.runAnims.some((a) => a.name === 'fx-peek-sweep' && a.delay === 120 && a.duration === 35 * 16) && /steps\(35/.test(ps1.runTiming), ps1);
  // 截圖：停在捲到一半（拿掉計時器，免得拍到一半被收起來），拍完放開讓它照常收起來
  await page.evaluate((sel) => {
    const n = document.querySelector(sel);
    clearTimeout(n._peekTimer);
    for (const a of n.querySelector('.t-peek').getAnimations({ subtree: true })) { a.pause(); a.currentTime = a.animationName === 'fx-peek-sweep' ? 120 + 380 : 200; }
  }, p1);
  await page.locator(p1).screenshot({ path: outFile('fx-tool-peek.png') });
  await page.evaluate((sel) => { const n = document.querySelector(sel); n.classList.remove('peeking'); n.querySelector('.t-peek').classList.add('fold'); for (const a of n.querySelector('.t-peek').getAnimations({ subtree: true })) a.play(); setTimeout(() => n.querySelector('.t-peek')?.remove(), 200); }, p1);
  await sleep(300);

  // 很短的輸出（失敗）：不捲、停一下就收起來，左緣紅色；整個播完拿掉小框、箭頭轉回去
  await host.pushItem(C, tool('fxp2', 'Running', { toolKind: 'shell', toolName: '執行指令', title: 'dotnet test' }));
  const p2 = `${SEL} .it[data-id="fxp2"]`;
  await until(() => page.$(p2), 2000);
  await sleep(950);
  await host.pushItem(C, tool('fxp2', 'Failed', { toolKind: 'shell', toolName: '執行指令', title: 'dotnet test', output: 'error CS1002: ; expected\nBuild FAILED.' }));
  const ps2 = await until(async () => { const s = await peekState(page, p2); return s.has ? s : null; }, 1500, 10);
  const peekRed = await colorOf(page, 'var(--red)');
  check('fxToolPeekShort', !!ps2 && !ps2.sweep && ps2.lines === 2 && ps2.rows === '2' && ps2.steps === '0' && ps2.box === peekRed && ps2.runAnims.length === 0, ps2);
  const gone2 = await until(async () => { const s = await peekState(page, p2); return !s.has && !s.peeking ? s : null; }, 1500, 20);
  check('fxToolPeekCleanup', !!gone2, gone2);

  // 上一個還沒收完就有下一個做完：上一個快速收起來（fold），新的接著播
  await host.pushItem(C, tool('fxp3', 'Running', { title: 'a.cs' }));
  await host.pushItem(C, tool('fxp4', 'Running', { title: 'b.cs' }));
  const p3 = `${SEL} .it[data-id="fxp3"]`, p4 = `${SEL} .it[data-id="fxp4"]`;
  await until(() => page.$(p4), 2000);
  await sleep(950);
  await host.pushItem(C, tool('fxp3', 'Done', { title: 'a.cs', output: peekOut }));
  await until(async () => (await peekState(page, p3)).has, 1500, 10);
  await sleep(150);
  await host.pushItem(C, tool('fxp4', 'Done', { title: 'b.cs', output: peekOut }));
  const handoff = await until(async () => {
    const [a, b] = await peekState(page, [p3, p4]);
    return b.has ? { a, b } : null;
  }, 1500, 10);
  check('fxToolPeekHandoff', !!handoff && (!handoff.a.has || handoff.a.fold) && !handoff.a.peeking && handoff.b.peeking, handoff);
  await sleep(1500);
  // 展開著的工具做完不播；正在掃過時點開那一列，小框讓給真的內容
  await host.pushItem(C, tool('fxp5', 'Running', { title: 'c.cs' }));
  const p5 = `${SEL} .it[data-id="fxp5"]`;
  await until(() => page.$(p5), 2000);
  await page.click(p5 + ' .t-row');
  await sleep(950);
  await host.pushItem(C, tool('fxp5', 'Done', { title: 'c.cs', output: peekOut }));
  await sleep(200);
  const open5 = await peekState(page, p5);
  await host.pushItem(C, tool('fxp6', 'Running', { title: 'd.cs' }));
  const p6 = `${SEL} .it[data-id="fxp6"]`;
  await until(() => page.$(p6), 2000);
  await sleep(950);
  await host.pushItem(C, tool('fxp6', 'Done', { title: 'd.cs', output: peekOut }));
  await until(async () => (await peekState(page, p6)).has, 1500, 10);
  await page.click(p6 + ' .t-row');
  const click6 = await page.evaluate((sel) => { const n = document.querySelector(sel); return { peek: !!n.querySelector('.t-peek'), open: n.classList.contains('open'), body: !!n.querySelector('.t-body .pre.out'), peeking: n.classList.contains('peeking') }; }, p6);
  check('fxToolPeekNotWhenOpen', !open5.has && !open5.peeking && !click6.peek && click6.open && click6.body && !click6.peeking, { open5, click6 });
  await page.click(p6 + ' .t-row');
  await sleep(300);

  // 思考結束時內容也掃過一遍（playStepPeek 的 .wrap）：一段一段換行排，量出實際排成幾行再一格一行捲到最後；左緣帳戶色、箭頭轉一下
  const thinkHtml = Array.from({ length: 9 }, (_, i) => `<p>第 ${i + 1} 段：先讀 <code>items.js</code> 的 createThinking，確認思考的內容在展開時才填；`
    + `再看 conversation.js 的 applyBatch 怎麼挑這一批最後做完的項目，最後補上 fx-probe 的情境與截圖 &amp; 規則測試。</p>`).join('\n');
  const think = (id, status, html) => item(id, { kind: 'Thinking', status, html });
  // 思考預設展開（展開著的結束時不播）：先收起來
  const collapseThink = async (sel) => {
    await page.click(sel + ' .think-sum');
    await until(() => page.evaluate((s) => !document.querySelector(s + ' details.think').open || null, sel), 1000, 10);
  };
  await host.pushItem(C, think('fxk2', 'Running', '<p>想…</p>'));
  const k2 = `${SEL} .it[data-id="fxk2"]`;
  await until(() => page.$(k2), 2000);
  await collapseThink(k2);
  await sleep(950);
  await host.pushItem(C, think('fxk2', 'Done', thinkHtml));
  const ks2 = await until(async () => { const s = await peekState(page, k2); return s.has ? s : null; }, 1500, 10);
  const peekAcc = await page.evaluate((s) => { const n = document.querySelector(s); const i = document.createElement('i'); i.style.color = 'var(--acc)'; n.append(i); const c = getComputedStyle(i).color; i.remove(); return c; }, k2);
  const kSteps = ks2 ? Number(ks2.steps) : -1;
  check('fxThinkPeekPlays', !!ks2 && ks2.wrap && ks2.sweep && ks2.peeking && ks2.lines === 9 && ks2.rows === '5' && kSteps > 9 - 5 && ks2.afterRow && ks2.hidden === 'true'
    && Math.abs(ks2.textRows - (kSteps + 5)) < 0.05 && ks2.box === peekAcc && /^第 1 段：先讀 items\.js 的 createThinking/.test(ks2.firstLine || '')
    && ks2.runAnims.some((a) => a.name === 'fx-peek-sweep') && new RegExp('steps\\(' + kSteps).test(ks2.runTiming), ks2);
  await page.evaluate((sel) => {
    const n = document.querySelector(sel);
    clearTimeout(n._peekTimer);
    for (const a of n.querySelector('.t-peek').getAnimations({ subtree: true })) { a.pause(); a.currentTime = a.animationName === 'fx-peek-sweep' ? 120 + a.effect.getTiming().duration / 2 : 200; }
  }, k2);
  await page.locator(k2).screenshot({ path: outFile('fx-think-peek.png') });
  await sleep(200); // 箭頭的轉場 150ms
  const kChev = await page.evaluate((s) => getComputedStyle(document.querySelector(s + ' .think-chev')).transform, k2);
  check('fxThinkPeekChevron', /^matrix\(0, 1, -1, 0/.test(kChev), kChev);
  await page.evaluate((sel) => { const n = document.querySelector(sel); n.classList.remove('peeking'); n.querySelector('.t-peek').classList.add('fold'); for (const a of n.querySelector('.t-peek').getAnimations({ subtree: true })) a.play(); setTimeout(() => n.querySelector('.t-peek')?.remove(), 200); }, k2);
  await sleep(300);
  // 很短的思考：一行，不捲；播完拿掉
  await host.pushItem(C, think('fxk3', 'Running', '<p>想…</p>'));
  const k3 = `${SEL} .it[data-id="fxk3"]`;
  await until(() => page.$(k3), 2000);
  await collapseThink(k3);
  await sleep(950);
  await host.pushItem(C, think('fxk3', 'Done', '<p>直接改 CSS 就好。</p>'));
  const ks3 = await until(async () => { const s = await peekState(page, k3); return s.has ? s : null; }, 1500, 10);
  const kgone3 = await until(async () => { const s = await peekState(page, k3); return !s.has && !s.peeking ? s : null; }, 1500, 20);
  check('fxThinkPeekShort', !!ks3 && ks3.wrap && !ks3.sweep && ks3.rows === '1' && ks3.steps === '0' && !!kgone3, { ks3, kgone3 });
  // 思考一出現就是展開的、執行中的內容直接看得到；展開著的思考結束不播；正在掃過時點開標題，小框讓給真的內容
  await host.pushItem(C, think('fxk4', 'Running', '<p>想…</p>'));
  const k4 = `${SEL} .it[data-id="fxk4"]`;
  await until(() => page.$(k4), 2000);
  const thinkOpenState = (sel) => { const n = document.querySelector(sel); return { open: n.querySelector('details.think').open, md: (n.querySelector('.think-md').textContent || '').slice(0, 8) }; };
  const kdef4 = await page.evaluate(thinkOpenState, k4);
  await sleep(950);
  await host.pushItem(C, think('fxk4', 'Done', thinkHtml));
  await sleep(200);
  const kopen4 = await peekState(page, k4);
  const kdone4 = await page.evaluate(thinkOpenState, k4);
  check('fxThinkDefaultOpen', kdef4.open && kdef4.md === '想…' && kdone4.open && kdone4.md.startsWith('第 1 段'), { kdef4, kdone4 });
  await host.pushItem(C, think('fxk5', 'Running', '<p>想…</p>'));
  const k5 = `${SEL} .it[data-id="fxk5"]`;
  await until(() => page.$(k5), 2000);
  await collapseThink(k5);
  await sleep(950);
  await host.pushItem(C, think('fxk5', 'Done', thinkHtml));
  await until(async () => (await peekState(page, k5)).has, 1500, 10);
  await page.click(k5 + ' .think-sum');
  // <details> 的 toggle 事件在點擊之後的下一個工作才送：等它處理完再讀（點完立刻讀可能還沒輪到）
  const click5State = (sel) => { const n = document.querySelector(sel); return { peek: !!n.querySelector('.t-peek'), open: n.querySelector('details.think').open, md: (n.querySelector('.think-md').textContent || '').slice(0, 8), peeking: n.classList.contains('peeking') }; };
  const kclick5 = (await until(async () => { const s = await page.evaluate(click5State, k5); return !s.peek && s.md ? s : null; }, 1500, 10)) || await page.evaluate(click5State, k5);
  check('fxThinkPeekNotWhenOpen', !kopen4.has && !kopen4.peeking && !kclick5.peek && kclick5.open && kclick5.md.startsWith('第 1 段') && !kclick5.peeking, { kopen4, kclick5 });
  await page.click(k5 + ' .think-sum');

  // 一輪結束（成功）：餘暉＋綠燈、標題列底線的光
  await pushHeader(host, { running: false, status: null, startedAt: null });
  await until(() => hasClass(page, SEL, 'fx-turn-ok'), 1500, 15);
  const ok = {
    cool: await hasClass(page, SEL, 'fx-cool'), turn: await hasClass(page, SEL, 'fx-turn-ok'),
    cbox: await names(page, `${SEL} .cbox`, '::before'), head: await names(page, `${SEL} > .ch`, '::after'),
    color: await page.evaluate((s) => getComputedStyle(document.querySelector(s + ' .cbox'), '::before').borderTopColor, SEL),
    green: await page.evaluate(() => { const i = document.createElement('i'); i.style.color = 'var(--green)'; document.body.append(i); const c = getComputedStyle(i).color; i.remove(); return c; }),
  };
  check('fxTurnEndOk', ok.cool && ok.turn && ok.cbox.includes('fx-turn-end') && ok.head.includes('fx-glide') && ok.color === ok.green, ok);
  await freeze(page, SEL, ['fx-turn-ok', 'fx-cool'], { 'fx-turn-end': 0 });
  await page.locator(`${SEL} .dock`).screenshot({ path: outFile('fx-turn-dark.png') });
  await thaw(page, SEL, ['fx-turn-ok', 'fx-cool']);

  // 一輪結束（最後是錯誤）：紅燈
  await pushHeader(host, { running: true, status: '執行中', startedAt: Date.now() });
  await sleep(1600);
  header(host, { running: false, status: null, startedAt: null });
  await sendNow(page, [
    { type: 'item', conversationId: C, item: item('fxe1', { kind: 'Error', html: '<p>Claude 程序意外結束（結束碼 1）</p>' }) },
    { type: 'header', conversationId: C, header: host.conv(C).header },
  ]);
  const err = await until(() => hasClass(page, SEL, 'fx-turn-err'), 1500, 15);
  check('fxTurnEndErr', !!err && !(await hasClass(page, SEL, 'fx-turn-ok')), { err });

  // 在這裡按停止：不閃結束的燈，只有餘暉慢慢淡掉
  await pushHeader(host, { running: true, status: '執行中', startedAt: Date.now() });
  await sleep(1600);
  await page.click(`${SEL} .cbtn.stop`);
  await until(() => page.evaluate((s) => !document.querySelector(s).classList.contains('running'), SEL), 2000, 15);
  await sleep(120);
  const stopFx = {
    cool: await hasClass(page, SEL, 'fx-cool'),
    turn: (await hasClass(page, SEL, 'fx-turn-ok')) || (await hasClass(page, SEL, 'fx-turn-err')),
    cbox: await anims(page, `${SEL} .cbox`, '::before'),
  };
  check('fxStopNoTurnFlash', stopFx.cool && !stopFx.turn, stopFx);
  check('fxAfterglow', !!stopFx.cbox && stopFx.cbox.some((a) => a.name === 'fx-fade-out' && a.duration === 1200), stopFx.cbox);

  // 底部「上一輪」的 token 數：變了先閃過亂碼，最後和宿主送的文字一字不差（結尾的單位「tokens」不寫）
  await pushHeader(host, { lastTurn: '輸入 13,579・輸出 2,468 tokens' });
  const turnScr = await decodeSeen(page, `${SEL} .ci-turn`);
  const turnText = await until(async () => { const t = await textOf(page, `${SEL} .ci-turn`); return t === '↑13.57K↓2,468' ? t : null; }, 3000);
  check('fxTurnTokensDecode', !!turnScr && !!turnText, { turnScr, turnText });

  // 待辦的完成數：輸入框上方的「2／5」與回覆裡待辦卡的「2／5 完成」都解碼成 3
  await pushHeader(host, { running: true, status: '執行中', startedAt: Date.now() });
  await sleep(300);
  const plan = JSON.parse(JSON.stringify(host.conv(C).items.find((x) => x.id === 'i11')));
  plan.plan[2].status = 'completed';
  plan.plan[3].status = 'inProgress';
  const todoBefore = await textOf(page, `${SEL} .todo-count`);
  await host.pushItem(C, plan);
  const todoScr = await decodeSeen(page, `${SEL} .todo-count`);
  const planScr = await decodeSeen(page, `${SEL} .it[data-id="i11"] .plan-count`);
  const todoAfter = await until(async () => { const t = await textOf(page, `${SEL} .todo-count`); return t === '3／5' ? t : null; }, 3000);
  const planAfter = await until(async () => { const t = await textOf(page, `${SEL} .it[data-id="i11"] .plan-count`); return t === '3／5 完成' ? t : null; }, 3000);
  check('fxTodoDecode', todoBefore === '2／5' && !!todoScr && !!planScr && !!todoAfter && !!planAfter, { todoBefore, todoScr, planScr, todoAfter, planAfter });

  // 卡片出現：括號收進來、閃、掃描線；鎖定期間不受 .it 的繪製限制（收進來之前的括號超出卡片），之後恢復
  const qs = await pushCard(page, host, 'fxq1');
  const card = {
    cv: await page.evaluate((s) => getComputedStyle(document.querySelector(s)).contentVisibility, qs),
    after: await names(page, qs, '::after'), before: await names(page, qs, '::before'),
  };
  check('fxCardLockIn', card.cv === 'visible' && card.after.includes('fx-lock-in') && card.after.includes('fx-lock-blink') && card.before.includes('fx-scan-in'), card);
  await sleep(1000);
  check('fxCardLockInRestores', (await page.evaluate((s) => getComputedStyle(document.querySelector(s)).contentVisibility, qs)) === 'auto');
  const qs2 = await pushCard(page, host, 'fxq2');
  await page.evaluate((s) => document.querySelector(s).scrollIntoView({ block: 'center' }), qs2);
  await freeze(page, qs2, ['fx-in'], CARD_AT);
  await page.locator(qs2).screenshot({ path: outFile('fx-card-dark.png') });
  await thaw(page, qs2, ['fx-in']);

  // 複製程式碼：括號變綠並閃兩下
  const code = `${SEL} .md .code`;
  await page.evaluate((s) => document.querySelector(s).scrollIntoView({ block: 'center' }), code);
  await page.click(`${code} .code-copy`);
  await until(() => hasClass(page, code, 'fx-copied'), 1500, 15);
  const copied = await names(page, code, '::after');
  check('fxCopyFlash', (await hasClass(page, code, 'fx-copied')) && copied.includes('fx-blink2'), copied);

  // 語法上色：i12 的 C# 程式碼框靜置之後上色；只多了 span.tk-*（沒有別的元素與屬性）、文字和 Markdig 輸出的一字不差；各類別的字對程式碼框底色的對比 ≥ 4.5
  const i12Html = host.conv(C).items.find((x) => x.id === 'i12').html;
  const syn = await until(() => page.evaluate(([s, html]) => {
    const c = document.querySelector(s + ' .it[data-id="i12"] .code pre code');
    if (!c || !c.querySelector('[class^="tk-"]')) return null;
    const ref = new DOMParser().parseFromString(html, 'text/html').querySelector('pre code');
    return {
      kinds: [...new Set(Array.from(c.children).map((x) => x.className))].sort(),
      onlySpans: Array.from(c.querySelectorAll('*')).every((x) => x.tagName === 'SPAN' && x.parentElement === c && /^tk-[a-z]+$/.test(x.className) && x.attributes.length === 1),
      sameText: !!ref && ref.textContent === c.textContent,
    };
  }, [SEL, i12Html]), 3000);
  check('fxSyntaxHighlight', !!syn && syn.onlySpans && syn.sameText && ['tk-kw', 'tk-ty', 'tk-fn'].every((k) => syn.kinds.includes(k)), syn);
  const synContrast = await kindContrast(page, host);
  check('fxSyntaxContrastDark', Object.keys(synContrast).length === 12 && Object.values(synContrast).every((r) => r >= 4.5), synContrast);
  await page.evaluate((s) => document.querySelector(s + ' .it[data-id="i12"] .code').scrollIntoView({ block: 'center' }), SEL);
  await page.locator(`${SEL} .it[data-id="i12"] .code`).screenshot({ path: outFile('fx-syntax-dark.png') });

  // 串流中還在長的程式碼框不上色（每 60ms 換一次），停了才上色（syntax.js 的靜置）
  const streamItem = (n, status) => item('fxs1', { status, html: '<p>步驟：</p><pre><code class="language-js">' + Array.from({ length: n }, (_, i) => 'const v' + i + ' = await load(' + i + ');').join('\n') + '</code></pre>' });
  let midSpans = 0;
  let midGutter = 0;
  for (let k = 1; k <= 12; k++) {
    await host.pushItem(C, streamItem(k, 'Running'));
    await sleep(60);
    midSpans = Math.max(midSpans, await page.evaluate((s) => document.querySelectorAll(s + ' .it[data-id="fxs1"] code [class^="tk-"]').length, SEL));
    midGutter = Math.max(midGutter, await page.evaluate((s) => document.querySelectorAll(s + ' .it[data-id="fxs1"] .code-gutter').length, SEL));
  }
  await host.pushItem(C, streamItem(12, 'Done'));
  const endSpans = await until(() => page.evaluate((s) => document.querySelectorAll(s + ' .it[data-id="fxs1"] code [class^="tk-"]').length || null, SEL), 2000);
  const endGutter = await until(() => page.evaluate((s) => document.querySelectorAll(s + ' .it[data-id="fxs1"] .code-gutter').length || null, SEL), 2000);
  check('fxSyntaxSettles', midSpans === 0 && endSpans > 0 && midGutter === 0 && endGutter === 1, { midSpans, endSpans, midGutter, endGutter });

  // 錯誤項目的斜紋條、鍵盤焦點的光暈（按過鍵盤之後的 focus 才算 :focus-visible）
  const stripe = await page.evaluate((s) => getComputedStyle(document.querySelector(s + ' .it-error')).backgroundImage, SEL);
  check('fxErrorStripe', /repeating-linear-gradient/.test(stripe), stripe);
  await page.keyboard.press('Shift');
  const ring = await page.evaluate((s) => { const b = document.querySelector(s + ' .md .code .code-copy'); b.focus(); return { visible: b.matches(':focus-visible'), shadow: getComputedStyle(b).boxShadow }; }, SEL);
  check('fxFocusRing', ring.visible && ring.shadow !== 'none', ring);
  await page.evaluate(() => document.activeElement && document.activeElement.blur());

  // 執行列：建置中帳戶色與光帶、開始執行閃綠、暫停閃琥珀、停下的餘暉（角框 1.2 秒淡回）、建置失敗閃紅
  const launchMsg = (phase, last) => ({ type: 'launchStatus', conversationId: C, launch: { folder: 'C:/repo/fx-app', selected: 't1',
    targets: [{ id: 't1', name: 'FxApp', kind: 'dotnet', path: 'FxApp.csproj', detail: 'WinForms · net10.0-windows', phase }], phase, since: Date.now(), last: last || null, logSeq: 0, logEpoch: 0 } });
  const lbar = `${SEL} .lbar`;
  const lstep = async (phase, last, cls) => {
    await sendNow(page, [launchMsg(phase, last)]);
    await until(() => page.evaluate(([s, p]) => { const e = document.querySelector(s); return !!e && !e.hidden && e.dataset.phase === p; }, [lbar, phase]), 1500, 15);
    return cls ? until(() => hasClass(page, lbar, cls), 800, 15) : true;
  };
  await lstep('idle', null, null);
  await lstep('building', null, null);
  const lBuild = await names(page, `${lbar} .lbar-btns`, '::before');
  await freeze(page, lbar, [], { 'fx-glide': 700 });
  await page.locator(`${SEL} .ch`).screenshot({ path: outFile('fx-launch-dark.png') }); // 執行列在標題列第二行
  await thaw(page, lbar, []);
  const lOk = await lstep('running', null, 'fx-l-ok') && (await names(page, `${lbar} .lbar-btns`, '::after')).includes('fx-blink2');
  const lPause = !!(await lstep('paused', null, 'fx-l-pause'));
  const lCool = await lstep('idle', { kind: 'stopped', text: '已停止' }, 'fx-l-cool')
    && /^1\.2s/.test(await page.evaluate((s) => getComputedStyle(document.querySelector(s + ' .lbar-btns'), '::after').transitionDuration, lbar));
  await lstep('building', null, null);
  const lErr = await lstep('idle', { kind: 'buildFailed', text: '建置失敗' }, 'fx-l-err');
  await sleep(250); // 角框的顏色有 160ms 的轉場，轉完才讀
  const lErrColor = await page.evaluate((s) => getComputedStyle(document.querySelector(s + ' .lbar-btns'), '::after').borderTopColor, lbar);
  const red = await page.evaluate(() => { const i = document.createElement('i'); i.style.color = 'var(--red)'; document.body.append(i); const c = getComputedStyle(i).color; i.remove(); return c; });
  check('fxLaunchPhases', lBuild.includes('fx-glide') && lOk && lPause && lCool && !!lErr && lErrColor === red,
    { lBuild, lOk, lPause, lCool, lErr: !!lErr, lErrColor, red });
  await sendNow(page, [{ type: 'launchStatus', conversationId: C, launch: null }]);

  // 圖片：載好的都完整顯示（沒有卡在透明）；燈箱從縮圖放大、按 Esc 縮回縮圖、之後移除
  await page.evaluate((s) => document.querySelector(s + ' .it[data-id="i1"]').scrollIntoView({ block: 'center' }), SEL);
  await sleep(500);
  const imgs = await page.evaluate((s) => Array.from(document.querySelectorAll(s + ' .items img')).filter((i) => i.complete && i.naturalWidth > 0)
    .map((i) => ({ cls: i.className, op: getComputedStyle(i).opacity })), SEL);
  check('fxImgFadeIn', imgs.length > 0 && imgs.every((i) => i.op === '1') && imgs.some((i) => /fx-img-in/.test(i.cls)), imgs);
  await page.click(`${SEL} .it[data-id="i1"] [data-lb-url]`);
  const zoomIn = await until(() => page.evaluate(() => { const i = document.querySelector('.lb .lb-img'); return i && i.getAnimations().length ? i.getAnimations().length : null; }), 2500, 10);
  await sleep(300);
  await page.keyboard.press('Escape');
  const zoomBack = await page.evaluate(() => { const lb = document.querySelector('.lb'); const i = lb && lb.querySelector('.lb-img'); return { cls: lb ? lb.className : null, anims: i ? i.getAnimations().length : 0 }; });
  const gone = await until(() => page.evaluate(() => !document.querySelector('.lb')), 1500, 20);
  check('fxLightboxZoom', !!zoomIn && /zoom-back/.test(zoomBack.cls || '') && zoomBack.anims > 0 && !!gone, { zoomIn, zoomBack, gone });

  // 送出（執行中，假宿主排進佇列）：送出鈕擴散一圈、字疊在輸入框上往上滑出；新排進佇列的那一列滑進來，原本的列不動
  const rowsBefore = await page.evaluate((s) => document.querySelectorAll(s + ' .queue-row').length, SEL);
  await page.fill(`${SEL} .cbox textarea`, '幽靈文字測試\n第二行');
  await page.click(`${SEL} .cbtn.send`);
  const sent = await page.evaluate((s) => ({ ghost: !!document.querySelector(s + ' .send-ghost'), ring: document.querySelector(s + ' .cbtn.send').classList.contains('fx-sent') }), SEL);
  const newRow = await until(() => page.evaluate(([s, n]) => { const r = document.querySelectorAll(s + ' .queue-row'); return r.length > n ? Array.from(r).map((x) => x.classList.contains('fx-in')) : null; }, [SEL, rowsBefore]), 1500, 15);
  await sleep(400);
  const ghostGone = await page.evaluate((s) => !document.querySelector(s + ' .send-ghost'), SEL);
  check('fxSendFeel', sent.ghost && sent.ring && ghostGone && !!newRow && newRow[newRow.length - 1] && newRow.slice(0, -1).every((x) => !x), { sent, ghostGone, newRow });

  // 捲上去時有新項目進來：「↓ 最新」寫出幾則並閃一下；按了捲到底、文字恢復
  await page.evaluate((s) => { document.querySelector(s + ' .scroller').scrollTop = 0; }, SEL);
  await until(() => page.evaluate((s) => !document.querySelector(s + ' .jump').hidden, SEL), 1500, 20);
  await sendNow(page, [{ type: 'item', conversationId: C, item: item('fxu1') }, { type: 'item', conversationId: C, item: item('fxu2') }]);
  const jump = await until(() => page.evaluate((s) => { const j = document.querySelector(s + ' .jump'); return j.textContent === '最新 · 2' ? { text: j.textContent, ping: j.classList.contains('fx-ping') } : null; }, SEL), 1500, 15);
  await page.click(`${SEL} .jump`);
  const jumpReset = await until(() => page.evaluate((s) => { const j = document.querySelector(s + ' .jump'); return j.hidden && j.textContent === '最新'; }, SEL), 2000, 20);
  check('fxJumpUnseen', !!jump && jump.ping && !!jumpReset, { jump, jumpReset });

  // 用量圓環：上下文的嚴重度升高彈一下、壓縮後倒轉一圈；側欄的額度也一樣
  const ctx0 = host.conv(C).header.context;
  await pushHeader(host, { context: Object.assign({}, ctx0, { percent: 82, used: 820_000, severity: 'warn' }) });
  const ctxUp = await until(() => hasClass(page, `${SEL} .ctxm`, 'fx-g-up'), 1500, 15) && (await names(page, `${SEL} .ctxm .ctxm-ring`)).includes('fx-pop');
  await sleep(900);
  await pushHeader(host, { context: Object.assign({}, ctx0, { percent: 30, used: 300_000, severity: 'ok' }) });
  const ctxReset = await until(() => hasClass(page, `${SEL} .ctxm`, 'fx-g-reset'), 1500, 15) && (await names(page, `${SEL} .ctxm .ctxm-ring`)).includes('fx-spin-back');
  const w0 = host.accounts[0].usage.windows[0];
  Object.assign(w0, { percent: 88, severity: 'high' });
  host.pushAccounts();
  const barUp = await until(() => page.evaluate(() => !!document.querySelector('#sidebar .mmeter.fx-g-up')), 1500, 15);
  await sleep(900);
  Object.assign(w0, { percent: 2, severity: 'ok' });
  host.pushAccounts();
  const barReset = await until(() => page.evaluate(() => !!document.querySelector('#sidebar .mmeter.fx-g-reset')), 1500, 15);
  check('fxGauges', !!ctxUp && !!ctxReset && !!barUp && !!barReset, { ctxUp, ctxReset, barUp, barReset });

  // 輸入框下方那一行（單行）：最前面四顆燈（運行跟著執行中）、不寫帳戶名稱、用量只畫 5 小時與每週兩組 5 格刻度（34%＝2 格、63%＝4 格，先把額度還原；
  // 名稱、百分比在滑過的說明裡）、token 數不寫單位「tokens」、分隔是細直線、不換行（一行高）
  Object.assign(w0, { percent: 34, severity: 'ok' });
  host.pushAccounts();
  await sleep(300);
  const infoOf = (pg) => pg.evaluate((s) => {
    const row = document.querySelector(s + ' .cinfo');
    const leds = Array.from(row.querySelectorAll('.ci-leds > .ci-led'));
    const meters = Array.from(row.querySelectorAll('.ci-usage > .ci-meter'));
    const r = row.getBoundingClientRect();
    return {
      first: row.firstElementChild && row.firstElementChild.className,
      leds: leds.map((l) => l.dataset.led + (l.classList.contains('on') ? ':on' : '') + (l.hidden ? ':hidden' : '')),
      labels: leds.map((l) => l.textContent).join(''),
      ledLabelShown: leds.filter((l) => !l.hidden).map((l) => getComputedStyle(l.querySelector('span')).display !== 'none'),
      acc: !!row.querySelector('.ci-acc'),
      meters: meters.map((m) => { const b = m.getBoundingClientRect(); return { cells: m.querySelectorAll('i').length, lit: m.querySelectorAll('i.lit').length, sev: m.className, title: m.title,
        left: Math.round(b.left), top: Math.round(b.top), height: Math.round(b.height * 10) / 10 }; }),
      usageTitle: (row.querySelector('.ci-usage') || {}).title,
      usageText: (row.querySelector('.ci-usage') || {}).textContent,
      turn: (row.querySelector('.ci-turn') || {}).textContent,
      text: row.textContent,
      sepWidth: getComputedStyle(row.querySelector('.ci-sep')).width,
      wrap: getComputedStyle(row).flexWrap,
      tops: [...new Set(Array.from(row.children).filter((c) => !c.hidden && c.getClientRects().length).map((c) => Math.round(c.getBoundingClientRect().top + c.getBoundingClientRect().height / 2)))],
      height: Math.round(r.height), width: Math.round(r.width),
    };
  }, SEL);
  const info = await infoOf(page);
  const oneLine = (x) => x.wrap === 'nowrap' && x.height <= 24 && Math.max(...x.tops) - Math.min(...x.tops) <= 2;
  check('fxInfoLine', info.first === 'ci-leds' && info.leds.join(' ') === 'run:on data net disk' && info.labels === '運行資料網路硬碟' && info.ledLabelShown.every(Boolean)
    && !info.acc && !info.text.includes('工作用 Max') && !/tokens|%|小時|每週/.test(info.text) && info.usageText === ''
    && info.meters.length === 2 && info.meters.every((m) => m.cells === 5) && info.meters[0].lit === 2 && info.meters[1].lit === 4
    && /sev-ok/.test(info.meters[0].sev) && /sev-warn/.test(info.meters[1].sev)
    && /^5 小時 34%・.+後重置$/.test(info.meters[0].title) && /^每週 63%・.+後重置$/.test(info.meters[1].title)
    && info.usageTitle === info.meters[0].title + '\n' + info.meters[1].title
    // 兩組刻度上下疊（2026-10-26）：同一個左緣、每週在下面、每條 3px 高
    && info.meters[0].left === info.meters[1].left && info.meters[1].top > info.meters[0].top && info.meters.every((m) => m.height <= 3.5)
    // token 數是箭頭寫法：↑ 輸入、↓ 輸出，精簡數字（2026-10-26）
    && /^↑[\d.,]+[KMB]?↓[\d.,]+[KMB]?$/.test(info.turn || '') && info.sepWidth === '1px' && oneLine(info), info);
  // 窄的對話欄（4 欄的窗格）：燈號只留顏色點（名稱在滑過的說明裡），照樣一行
  const colWidth = (w) => page.evaluate(([s, x]) => { const c = document.querySelector(s + ' .conv-col'); c.style.flex = x ? 'none' : ''; c.style.width = x ? x + 'px' : ''; }, [SEL, w]);
  await colWidth(420);
  await sleep(150);
  const narrow = await infoOf(page);
  await colWidth(0);
  check('fxInfoLineNarrow', narrow.width <= 420 && narrow.ledLabelShown.length === 4 && narrow.ledLabelShown.every((x) => !x) && oneLine(narrow), narrow);

  // 燈號連動宿主的 activity：這個對話有資料流與硬碟、整台電腦有網路時亮，說明寫出量到的速率；160ms 後熄掉；別的對話的量不點亮這裡
  const ledsOf = (pg) => pg.evaluate((s) => Object.fromEntries(Array.from(document.querySelectorAll(s + ' .ci-led')).map((l) => [l.dataset.led, { on: l.classList.contains('on'), title: l.title }])), SEL);
  await sendNow(page, [{ type: 'activity', ms: 250, conversations: [{ id: 'other', data: 9_999, disk: 9_999 }] }]);
  const notMine = await ledsOf(page);
  await sendNow(page, [{ type: 'activity', ms: 250, conversations: [{ id: C, data: 5_000, disk: 8_192 }], net: { rx: 4_096, tx: 1_024 } }]);
  const lit = await ledsOf(page);
  await page.locator(`${SEL} .cinfo`).screenshot({ path: outFile('fx-infoline-dark.png') });
  await sleep(450);
  const off = await ledsOf(page);
  check('fxActivityLeds', !notMine.data.on && !notMine.disk.on && lit.data.on && lit.disk.on && lit.net.on && lit.run.on
    && /19\.5 KB\/s/.test(lit.data.title) && /32\.0 KB\/s/.test(lit.disk.title) && /↓ 16\.0 KB\/s　↑ 4\.0 KB\/s/.test(lit.net.title)
    && !off.data.on && !off.disk.on && !off.net.on && off.run.on && /（[^）]*秒前|剛剛）/.test(off.data.title),
  { notMine, lit, off });

  // token 數後面的 CPU 波形（2026-10-26，使用者選的「極光面積、依負載變色」）：宿主的 activity 帶這個對話的 cpu 才出現、放在 token 數後面；
  // 每收到一格畫一次（數 canvas 的 clearRect）、沒有新的一格時不畫；最高那格的面積最高、顏色依負載（高的偏暖、低的偏冷）、百分比的顏色同最新的那格；
  // 兩格之間是 canvas 的 transform 動畫（只有 transform、線性、跑一次就停，fxCpuWave），新的一格提早或晚到都從目前的位置接著移、畫面不跳
  // （fxCpuWaveSmooth：逐個影格記錄 transform 與重畫次數）；平移不讓主執行緒每個影格重算樣式（fxCpuWaveCompositor：Chrome 的 RecalcStyleCount）；
  // 視窗沒有焦點時不平移、最多每秒畫一次；1.5 秒沒收到就收起
  await page.evaluate(() => {
    window.__cpuDraws = 0;
    const orig = CanvasRenderingContext2D.prototype.clearRect;
    CanvasRenderingContext2D.prototype.clearRect = function (...a) {
      if (this.canvas.classList.contains('ci-cpu-wave')) window.__cpuDraws++;
      return orig.apply(this, a);
    };
  });
  const cpuState = () => page.evaluate((s) => {
    const info = document.querySelector(s + ' .cinfo');
    const el = info.querySelector('.ci-cpu');
    const kids = Array.from(info.children);
    const cv = el && el.querySelector('canvas');
    let cols = null;
    if (cv && cv.width) {
      // 每一格（k＝0 是最新的，在 canvas 右緣，越舊越往左 3px）那一直行：畫到的像素數（面積的高度）與最不透明那一點的顏色
      const d = cv.getContext('2d').getImageData(0, 0, cv.width, cv.height).data;
      const dpr = cv.width / 72;
      cols = Array.from({ length: 25 }, (_, k) => {
        const x = Math.max(0, Math.min(cv.width - 1, Math.round((72 - k * 3) * dpr) - 1));
        let n = 0, ba = 0, rgb = null;
        for (let y = 0; y < cv.height; y++) {
          const o = (y * cv.width + x) * 4;
          if (d[o + 3] > 20) n++;
          if (d[o + 3] > ba) { ba = d[o + 3]; rgb = [d[o], d[o + 1], d[o + 2]]; }
        }
        return { n, rgb };
      });
    }
    const anims = cv ? cv.getAnimations() : [];
    const a = anims[anims.length - 1];
    const rgbOf = (c) => (c.match(/\d+/g) || []).map(Number);
    return { shown: !!el, afterTurn: !!el && kids.indexOf(el) > kids.findIndex((k) => k.classList.contains('ci-turn')),
      text: el ? el.querySelector('.ci-cpu-n').textContent : null, numRgb: el ? rgbOf(el.querySelector('.ci-cpu-n').style.color) : null,
      label: el ? el.getAttribute('aria-label') : null, draws: window.__cpuDraws, cols,
      mem: el ? el.querySelector('.ci-mem-n').textContent : null, memShown: !!el && !el.querySelector('.ci-mem').hidden,
      memLabel: el ? el.querySelector('.ci-mem .ci-k').textContent : null,
      anims: anims.length, running: anims.filter((x) => x.playState === 'running').length,
      props: a ? [...new Set(a.effect.getKeyframes().flatMap((k) => Object.keys(k).filter((n) => !['offset', 'computedOffset', 'easing', 'composite'].includes(n))))] : null,
      easing: a ? a.effect.getTiming().easing : null, iterations: a ? a.effect.getTiming().iterations : null,
      inline: cv ? cv.style.transform : null };
  }, SEL);
  const warm = (c) => !!c && c[0] > c[2];
  const cool = (c) => !!c && c[2] > c[0];
  await sendNow(page, [{ type: 'activity', ms: 250, conversations: [{ id: 'other', data: 0, disk: 0, cpu: 50 }] }]);
  const cpuNotMine = await cpuState();
  const cpuSeries = [0, 0.4, 3.1, 12, 40, 25, 6, 2.5];
  for (const v of cpuSeries) {
    await sendNow(page, [{ type: 'activity', ms: 250, conversations: [{ id: C, data: 0, disk: 0, cpu: v, mem: 734003200 }] }]);
    await sleep(40);
  }
  const cpuOn = await cpuState();
  // 滑過的說明：量的是什麼、最近的平均與最高、記憶體
  await page.hover(`${SEL} .ci-cpu`);
  const cpuTip = await page.evaluate((s) => document.querySelector(s + ' .ci-cpu').title, SEL);
  await sleep(300);
  await page.locator(`${SEL} .cinfo`).screenshot({ path: outFile('fx-cpu-wave-dark.png') });
  await sleep(150);
  const cpuIdle = await cpuState();
  const recent = cpuOn.cols ? cpuOn.cols.slice(0, 8) : [];
  const tallest = recent.length ? recent.map((c) => c.n).indexOf(Math.max(...recent.map((c) => c.n))) : -1;
  check('fxCpuWave', !cpuNotMine.shown && cpuOn.shown && cpuOn.afterTurn && cpuOn.text === '2.5%' && cpuOn.label === 'CPU 2.5%，記憶體 700 MB'
    && cpuOn.memShown && cpuOn.mem === '700 MB' && cpuOn.memLabel === 'MEM' && /背景程式/.test(cpuTip) && /\n記憶體 700 MB：/.test(cpuTip)
    && cpuOn.draws === cpuSeries.length && tallest === 3 && warm(cpuOn.cols[3].rgb) && cool(cpuOn.cols[6].rgb) && cool(cpuOn.numRgb)
    && cpuOn.cols.slice(9).every((c) => c.n === 0)
    && cpuOn.anims === 1 && cpuOn.props && cpuOn.props.join() === 'transform' && cpuOn.easing === 'linear' && cpuOn.iterations === 1
    && cpuIdle.draws === cpuOn.draws && cpuIdle.running === 0,
  { cpuNotMine, cpuOn: { ...cpuOn, cols: cpuOn.cols && cpuOn.cols.slice(0, 10) }, cpuTip, cpuIdle: { draws: cpuIdle.draws, running: cpuIdle.running, anims: cpuIdle.anims }, tallest });

  // 流暢：取樣間隔忽快忽慢（180～320ms），逐個影格記錄 canvas 的 transform 與重畫次數；同一格數據在畫面上的位置＝平移量－重畫次數×3px，
  // 要一直往左、每個影格移動不超過 1px 或速度上限（畫面不跳、不倒退），整段走了約 7 格
  const cpuSmooth = await page.evaluate(async ([s, id]) => {
    const cv = document.querySelector(s + ' .ci-cpu-wave');
    const rec = [];
    let stop = false;
    const loop = () => {
      rec.push({ t: performance.now(), d: window.__cpuDraws, x: new DOMMatrixReadOnly(getComputedStyle(cv).transform).m41 });
      if (!stop) requestAnimationFrame(loop);
    };
    requestAnimationFrame(loop);
    const gaps = [250, 210, 300, 250, 180, 320, 250];
    for (const g of gaps) {
      await new Promise((r) => setTimeout(r, g));
      window.__host({ type: 'activity', ms: g, conversations: [{ id, data: 0, disk: 0, cpu: 5 + (g % 7) * 4 }] });
    }
    await new Promise((r) => setTimeout(r, 350));
    stop = true;
    const pos = rec.map((r) => r.x - r.d * 3);
    let maxJump = 0, maxBack = 0, worst = null;
    for (let i = 1; i < rec.length; i++) {
      const dp = pos[i] - pos[i - 1];
      const dt = rec[i].t - rec[i - 1].t;
      const limit = Math.max(1, 0.05 * dt);
      if (!worst || Math.abs(dp) / limit > worst.ratio) worst = { i, dp: Math.round(dp * 100) / 100, dt: Math.round(dt), limit, ratio: Math.abs(dp) / limit };
      maxJump = Math.max(maxJump, Math.abs(dp));
      maxBack = Math.max(maxBack, dp);
    }
    return { frames: rec.length, draws: rec[rec.length - 1].d - rec[0].d, travel: Math.round((pos[pos.length - 1] - pos[0]) * 10) / 10,
      maxJump: Math.round(maxJump * 100) / 100, maxBack: Math.round(maxBack * 100) / 100, worst };
  }, [SEL, C]);
  check('fxCpuWaveSmooth', cpuSmooth.frames > 40 && cpuSmooth.draws === 7 && cpuSmooth.worst && cpuSmooth.worst.ratio <= 1
    && cpuSmooth.maxBack <= 0.3 && cpuSmooth.travel <= -18 && cpuSmooth.travel >= -24, cpuSmooth);

  // 平移交給合成器：一直有新的一格、波形一直在移的 1.5 秒裡，Chrome 的樣式重算次數只多出每一格的那幾次（換數字與顏色、重開一段平移；
  // 實測每格約 7 次），不是每個影格一次（60 fps 的 1.5 秒約 90 次）
  const cpuCdp = await page.context().newCDPSession(page);
  await cpuCdp.send('Performance.enable');
  const perfOf = async () => Object.fromEntries((await cpuCdp.send('Performance.getMetrics')).metrics.map((x) => [x.name, x.value]));
  await sleep(1700); // 先讓上一段收起
  const cpuP0 = await perfOf();
  await sleep(1500);
  const cpuP1 = await perfOf();
  const cpuP2 = await perfOf();
  await page.evaluate(async (id) => {
    for (let i = 0; i < 6; i++) {
      window.__host({ type: 'activity', ms: 250, conversations: [{ id, data: 0, disk: 0, cpu: 10 + i * 5 }] });
      await new Promise((r) => setTimeout(r, 250));
    }
  }, C);
  const cpuP3 = await perfOf();
  await cpuCdp.send('Performance.disable');
  await cpuCdp.detach();
  const cpuPerf = { recalcIdle: cpuP1.RecalcStyleCount - cpuP0.RecalcStyleCount, recalcWave: cpuP3.RecalcStyleCount - cpuP2.RecalcStyleCount,
    layoutIdle: cpuP1.LayoutCount - cpuP0.LayoutCount, layoutWave: cpuP3.LayoutCount - cpuP2.LayoutCount,
    scriptMsWave: Math.round((cpuP3.ScriptDuration - cpuP2.ScriptDuration) * 10000) / 10 };
  check('fxCpuWaveCompositor', cpuPerf.recalcWave - cpuPerf.recalcIdle <= 6 * 9, cpuPerf);

  // 視窗沒有焦點：距上次畫不到 1 秒的不畫（資料照記），滿 1 秒的下一格才畫，而且不平移（直接停在推進來的位置）
  await page.evaluate(() => { document.documentElement.dataset.window = 'inactive'; });
  const cpuBefore = await cpuState();
  for (const v of [7, 8, 9]) await sendNow(page, [{ type: 'activity', ms: 250, conversations: [{ id: C, data: 0, disk: 0, cpu: v }] }]);
  const cpuInactive = await cpuState();
  await sleep(1050);
  await sendNow(page, [{ type: 'activity', ms: 250, conversations: [{ id: C, data: 0, disk: 0, cpu: 10 }] }]);
  const cpuInactiveLater = await cpuState();
  await page.evaluate(() => { delete document.documentElement.dataset.window; });
  // 成本（只記錄、不當條件）：處理一則 activity 的時間，帶 cpu（要畫波形）和不帶（只有燈號）各 200 則的平均
  const cpuCost = await page.evaluate(async (id) => {
    const run = async (withCpu) => {
      const t0 = performance.now();
      for (let i = 0; i < 200; i++) window.__host({ type: 'activity', ms: 250, conversations: [{ id, data: 10, disk: 0, ...(withCpu ? { cpu: (i * 7) % 40 } : {}) }] });
      const js = performance.now() - t0;
      await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));
      return { jsMsPerMessage: Math.round((js / 200) * 1000) / 1000 };
    };
    return { leds: await run(false), ledsAndCpu: await run(true) };
  }, C);
  await sleep(1700);
  const cpuGone = await cpuState();
  check('fxCpuWaveInactive', cpuInactive.draws === cpuBefore.draws && cpuInactiveLater.draws === cpuBefore.draws + 1 && cpuInactiveLater.text === '10%'
    && cpuInactiveLater.running === 0 && cpuInactiveLater.inline === 'translateX(-3px)' && !cpuGone.shown,
  { cpuBefore: { draws: cpuBefore.draws, shown: cpuBefore.shown }, cpuInactive: { draws: cpuInactive.draws, text: cpuInactive.text },
    cpuInactiveLater: { draws: cpuInactiveLater.draws, text: cpuInactiveLater.text, running: cpuInactiveLater.running, inline: cpuInactiveLater.inline },
    cpuGone: { shown: cpuGone.shown }, cpuCost });

  // 選單：角框畫在背景上（8 段線＋底色，選單捲動時不跟著內容走）；方向鍵選到的那一項左緣一格小方塊，顏色是開選單那顆按鈕的帳戶色
  await page.click(`${SEL} .ibtn[aria-label="更多動作"]`);
  await until(() => page.$('.menu'), 1500);
  await page.keyboard.press('ArrowDown');
  const menu = await page.evaluate((s) => {
    const m = document.querySelector('.menu');
    const a = m.querySelector('.menu-item.active');
    const ref = document.createElement('i');
    ref.style.color = 'var(--acc)';
    document.querySelector(s).append(ref);
    const acc = getComputedStyle(ref).color;
    ref.remove();
    const dot = a ? getComputedStyle(a, '::before') : null;
    return { layers: (getComputedStyle(m).backgroundImage.match(/linear-gradient/g) || []).length, active: !!a, dot: dot && dot.content, dotColor: dot && dot.backgroundColor, acc };
  }, SEL);
  await page.locator('.menu').screenshot({ path: outFile('fx-menu-dark.png') });
  await page.keyboard.press('Escape');
  check('fxMenuBrackets', menu.layers === 8 && menu.active && menu.dot !== 'none' && menu.dotColor === menu.acc, menu);

  // 通知：角框與燈是成功的綠色、括號從外面收進來閃兩下；底部的倒數條在縮，滑鼠移上去停住、移開繼續
  await host.send({ type: 'toast', text: '效果探針的通知', level: 'success' });
  const TOAST = '.toasts > .toast:last-child';
  await until(() => page.$(TOAST), 1500);
  const toast = await page.evaluate((sel) => {
    const t = document.querySelector(sel);
    const after = getComputedStyle(t, '::after');
    const bar = t.querySelector('.toast-bar');
    return {
      bracket: after.content, tone: getComputedStyle(t).getPropertyValue('--fx-tone').trim(), green: getComputedStyle(document.documentElement).getPropertyValue('--green').trim(),
      after: document.getAnimations().filter((a) => a.effect && a.effect.target === t && a.effect.pseudoElement === '::after').map((a) => a.animationName),
      bar: bar ? bar.getAnimations().map((a) => a.playState) : null,
    };
  }, TOAST);
  const barState = () => page.evaluate((sel) => { const b = document.querySelector(sel + ' .toast-bar'); const a = b && b.getAnimations()[0]; return a ? a.playState : null; }, TOAST);
  await page.hover(TOAST);
  const barHover = await barState();
  await page.locator(TOAST).screenshot({ path: outFile('fx-toast-dark.png') });
  await page.mouse.move(5, 5);
  const barLeave = await barState();
  check('fxToast', toast.bracket !== 'none' && toast.tone === toast.green && toast.after.includes('fx-lock-in-far') && toast.after.includes('fx-lock-blink')
    && !!toast.bar && toast.bar[0] === 'running' && barHover === 'paused' && barLeave === 'running', { toast, barHover, barLeave });

  // 浮動說明的角框；設定頁的區塊標題前面的菱形、後面的導線（只看樣式：在頁面上放一個同 class 的元素量）
  const statics = await page.evaluate(() => {
    const tip = document.createElement('div'); tip.className = 'tip'; tip.textContent = '說明';
    const sec = document.createElement('h3'); sec.className = 'set-h'; sec.textContent = '外觀';
    document.body.append(tip, sec);
    const out = { tip: getComputedStyle(tip, '::after').content, diamond: getComputedStyle(sec, '::before').transform, guide: getComputedStyle(sec, '::after').backgroundImage };
    tip.remove(); sec.remove();
    return out;
  });
  check('fxTipAndSettingHeads', statics.tip !== 'none' && statics.diamond !== 'none' && /repeating-linear-gradient/.test(statics.guide), statics);

  // 回覆內容：清單符號是小方塊、符號帶帳戶色；引用的左線是一串小方塊（不是實線框線）；表格數字等寬
  await host.pushItem(C, item('fxmd', { html: '<ul><li>一</li><li>二</li></ul><ol><li>甲</li></ol><blockquote><p>引用</p></blockquote>'
    + '<table><thead><tr><th>名稱</th><th>數量</th></tr></thead><tbody><tr><td>a</td><td>12</td></tr></tbody></table>' }));
  const mdSel = `${SEL} .it[data-id="fxmd"]`;
  await until(() => page.$(mdSel + ' td'), 2000);
  const mdx = await page.evaluate((s) => {
    const n = document.querySelector(s);
    const li = n.querySelector('ul > li'), bq = n.querySelector('blockquote'), td = n.querySelector('td');
    const ref = document.createElement('i');
    ref.style.color = 'color-mix(in srgb, var(--acc) 70%, var(--text-3))';
    n.append(ref);
    const want = getComputedStyle(ref).color;
    ref.remove();
    return { list: getComputedStyle(li).listStyleType, marker: getComputedStyle(li, '::marker').color, want, bqBorder: getComputedStyle(bq).borderLeftWidth,
      bqBg: getComputedStyle(bq).backgroundImage, num: getComputedStyle(td).fontVariantNumeric };
  }, mdSel);
  await page.evaluate((s) => document.querySelector(s).scrollIntoView({ block: 'center' }), mdSel);
  await page.locator(mdSel).screenshot({ path: outFile('fx-markdown-dark.png') });
  check('fxMarkdownDetails', mdx.list === 'square' && mdx.marker === mdx.want && mdx.bqBorder === '0px' && /repeating-linear-gradient/.test(mdx.bqBg) && mdx.num === 'tabular-nums', mdx);

  // 卡片有了結果：點「是」→ 綠色鎖定閃兩下、結果的標籤淡入；宿主改成拒絕 → 紅；問卷略過 → 琥珀；失效 → 不閃
  const cardOf = (src, id) => { const q = Object.assign(JSON.parse(JSON.stringify(host.conv(C).items.find((x) => x.id === src))), { id, time: Date.now() }); host.conv(C).items.push(q); return q; };
  const settleOf = async (id) => {
    const s = `${SEL} .it[data-id="${id}"]`;
    await until(() => page.evaluate((x) => { const n = document.querySelector(x); return !!n && !n.classList.contains('waiting'); }, s), 2000, 15);
    return page.evaluate((x) => {
      const n = document.querySelector(x);
      const cls = ['fx-c-ok', 'fx-c-no', 'fx-c-skip'].filter((c) => n.classList.contains(c));
      const after = document.getAnimations().filter((a) => a.effect && a.effect.target === n && a.effect.pseudoElement === '::after').map((a) => a.animationName);
      const res = n.querySelector('.result');
      return { cls, after, result: res ? res.getAnimations().map((a) => a.animationName) : null };
    }, s);
  };
  const ca = cardOf('i20', 'fxc1');
  await host.pushItem(C, ca);
  await until(() => page.$(`${SEL} .it[data-id="fxc1"] .opt[data-idx="1"]`), 2000);
  await sleep(950); // 出現時的鎖定播完
  await page.click(`${SEL} .it[data-id="fxc1"] .opt[data-idx="1"]`);
  const cOk = await settleOf('fxc1');
  const c1Sel = `${SEL} .it[data-id="fxc1"]`;
  await page.evaluate((x) => document.querySelector(x).scrollIntoView({ block: 'center' }), c1Sel);
  await freeze(page, c1Sel, ['fx-c-ok'], { 'fx-blink2': 0, 'fx-in': 239 });
  await page.locator(c1Sel).screenshot({ path: outFile('fx-card-settle-dark.png') });
  await thaw(page, c1Sel, ['fx-c-ok']);
  const cb = cardOf('i20', 'fxc2');
  await host.pushItem(C, cb);
  await until(() => page.$(`${SEL} .it[data-id="fxc2"]`), 2000);
  await host.pushItem(C, Object.assign({}, cb, { status: 'Declined', decision: 'deny' }));
  const cNo = await settleOf('fxc2');
  const cq = cardOf('i19', 'fxc3');
  await host.pushItem(C, cq);
  await until(() => page.$(`${SEL} .it[data-id="fxc3"]`), 2000);
  await host.pushItem(C, Object.assign({}, cq, { status: 'Declined', decision: 'skip' }));
  const cSkip = await settleOf('fxc3');
  const cc = cardOf('i20', 'fxc4');
  await host.pushItem(C, cc);
  await until(() => page.$(`${SEL} .it[data-id="fxc4"]`), 2000);
  await host.pushItem(C, Object.assign({}, cc, { status: 'Cancelled' }));
  const cGone = await settleOf('fxc4');
  check('fxCardSettle', cOk.cls.join() === 'fx-c-ok' && cOk.after.includes('fx-blink2') && !!cOk.result && cOk.result.includes('fx-in')
    && cNo.cls.join() === 'fx-c-no' && cSkip.cls.join() === 'fx-c-skip' && cGone.cls.length === 0 && !cGone.after.includes('fx-blink2'),
  { cOk, cNo, cSkip, cGone });

  // 整則回覆的「複製」：外圍出現綠色括號閃兩下後淡掉（這段時間拿掉 content-visibility，框外 6px 才不被裁掉）
  const r3 = `${SEL} .it[data-id="i3"]`;
  await page.evaluate((s) => { const n = document.querySelector(s); n.scrollIntoView({ block: 'center' }); n.querySelector('.it-acts .code-copy').click(); }, r3);
  const replyCopy = {
    cls: await hasClass(page, r3, 'fx-copied'), cv: await page.evaluate((s) => getComputedStyle(document.querySelector(s)).contentVisibility, r3),
    bracket: await page.evaluate((s) => getComputedStyle(document.querySelector(s), '::after').content, r3), after: await names(page, r3, '::after'),
  };
  await freeze(page, r3, ['fx-copied'], { 'fx-copy-flash': 0 });
  const r3Box = await page.locator(r3).boundingBox();
  await page.screenshot({ path: outFile('fx-reply-copy-dark.png'), clip: { x: r3Box.x - 12, y: r3Box.y - 12, width: r3Box.width + 24, height: Math.min(r3Box.height + 24, 420) } });
  await thaw(page, r3, ['fx-copied']);
  check('fxReplyCopyFlash', replyCopy.cls && replyCopy.cv === 'visible' && replyCopy.bracket !== 'none' && replyCopy.after.includes('fx-copy-flash'), replyCopy);

  // 點開思考與工具：內容淡入（只有點的時候，建立時就展開的不播）
  const thinkSel = `${SEL} .it[data-id="i2"]`;
  await page.evaluate((s) => document.querySelector(s).scrollIntoView({ block: 'center' }), thinkSel);
  if (await page.evaluate((s) => document.querySelector(s + ' details.think').open, thinkSel)) { await page.click(`${thinkSel} .think-sum`); await sleep(100); }
  await page.click(`${thinkSel} .think-sum`);
  const thinkOpen = await until(() => page.evaluate((s) => { const m = document.querySelector(s + ' .think-md'); return m.classList.contains('fx-open') && m.getAnimations().some((a) => a.animationName === 'fx-in') ? true : null; }, thinkSel), 1000, 10);
  const toolSel = `${SEL} .it[data-id="i4"]`;
  await page.evaluate((s) => document.querySelector(s).scrollIntoView({ block: 'center' }), toolSel);
  await page.click(`${toolSel} .t-row`);
  const toolOpen = await until(() => page.evaluate((s) => { const b = document.querySelector(s + ' .t-body'); return b && b.classList.contains('fx-open') && b.getAnimations().some((a) => a.animationName === 'fx-in') ? true : null; }, toolSel), 1000, 10);
  check('fxExpandFade', !!thinkOpen && !!toolOpen, { thinkOpen, toolOpen });

  // 側欄：新對話那一列從左邊滑進來；搜尋只剩一列後清掉搜尋，回來的一大批只有前 12 列錯開進場（--fx-i 0～11）、其他直接出現
  host.conversations.unshift(Object.assign({}, host.conversations.find((x) => x.id === C), { id: 'cfxnew', title: '效果探針的新對話', running: false, waiting: false, unread: false }));
  host.pushConversations();
  const rowNew = await until(() => page.evaluate(() => {
    const r = document.querySelector('.crow[data-key="c:cfxnew"]');
    return r ? { cls: r.classList.contains('fx-row-in'), anims: r.getAnimations().map((a) => a.animationName) } : null;
  }), 1500, 10);
  const searchBox = '#sidebar .sb-search-input';
  await page.fill(searchBox, '修正登入流程卡住');
  await until(() => page.evaluate(() => document.querySelectorAll('.crow').length <= 2 || null), 1500, 10);
  const before = await page.evaluate(() => document.querySelectorAll('.crow').length);
  await page.fill(searchBox, '');
  const back = await until(() => page.evaluate((n) => {
    const rows = Array.from(document.querySelectorAll('.crow'));
    if (rows.length <= n) return null;
    const lit = rows.filter((r) => r.classList.contains('fx-row-in'));
    return { rows: rows.length, lit: lit.length, idx: lit.map((r) => Number(r.style.getPropertyValue('--fx-i'))).sort((a, b) => a - b) };
  }, before), 1500, 10);
  check('fxSidebarRows', !!rowNew && rowNew.cls && rowNew.anims.includes('fx-row-in') && !!back && back.rows - before > 12 && back.lit === 12
    && back.idx.join() === Array.from({ length: 12 }, (_, i) => i).join(), { rowNew, before, back });

  // 自動命名：產生標題期間（titling）之後回來的新標題，標題列與側欄那一列先閃過亂碼再鎖定；讀到的文字一直是新標題、播完換回單一文字節點。
  // 手動改名（前後都不在產生標題）直接換
  const convRow = host.conversations.find((x) => x.id === C);
  const titleState = (pg) => pg.evaluate((s) => {
    const one = (el) => ({ text: el.textContent, scr: el.querySelectorAll('.tdx-c.scr').length, nodes: el.childNodes.length });
    return { head: one(document.querySelector(s + ' .ch-title-run')), row: one(document.querySelector('.crow[data-key="c:' + document.querySelector(s).dataset.conv + '"] .crow-title')) };
  }, SEL);
  header(host, { titling: true });
  convRow.titling = true;
  await sendNow(page, [{ type: 'header', conversationId: C, header: host.conv(C).header }, { type: 'conversations', items: host.conversations }]);
  await sleep(150);
  const NEW_TITLE = '效果探針自動命名的標題';
  header(host, { titling: false, title: NEW_TITLE });
  Object.assign(convRow, { titling: false, title: NEW_TITLE });
  await sendNow(page, [{ type: 'header', conversationId: C, header: host.conv(C).header }, { type: 'conversations', items: host.conversations }]);
  // 標頭的更新在下一個影格才套用：等標題列換字再讀
  const tDuring = await until(async () => { const t = await titleState(page); return t.head.text === NEW_TITLE ? t : null; }, 1500, 5);
  await page.locator(`${SEL} .ch`).screenshot({ path: outFile('fx-title-decode-dark.png') });
  await sleep(900);
  const tAfter = await titleState(page);
  header(host, { title: '手動改的標題' });
  Object.assign(convRow, { title: '手動改的標題' });
  await sendNow(page, [{ type: 'header', conversationId: C, header: host.conv(C).header }, { type: 'conversations', items: host.conversations }]);
  const tManual = await until(async () => { const t = await titleState(page); return t.head.text === '手動改的標題' ? t : null; }, 1500, 5);
  check('fxTitleDecode', !!tDuring && tDuring.row.text === NEW_TITLE && tDuring.head.scr > 0 && tDuring.row.scr > 0
    && tAfter.head.scr === 0 && tAfter.head.nodes === 1 && tAfter.row.nodes === 1 && tAfter.head.text === NEW_TITLE
    && !!tManual && tManual.head.scr === 0 && tManual.row.scr === 0 && tManual.row.text === '手動改的標題', { tDuring, tAfter, tManual });

  // git 晶片的徽章：未推送從 1 變 3 時數字先閃過亂碼，最後是「↑3」；第一次顯示不解碼
  const git = { root: 'C:/repo/myriagent', branch: 'main', head: '19fb6c2', upstream: 'origin/main', upstreamGone: false, ahead: 1, behind: 0,
    base: null, baseAhead: null, baseBehind: null, changed: 0, staged: 0, unstaged: 0, untracked: 0, conflicts: 0, stashes: 0,
    operation: null, remote: 'origin', remoteNew: false, remoteCheckedAt: Date.now() - 60000, checkedAt: Date.now() - 5000 };
  const BADGE = `${SEL} .ch-git .git-badge[data-kind="ahead"]`;
  await sendNow(page, [{ type: 'gitStatus', conversationId: C, git }]);
  await until(() => page.$(BADGE), 1500);
  const gitFirst = await page.evaluate((s) => document.querySelectorAll(s + ' .mtok-c.scr').length, BADGE);
  await sendNow(page, [{ type: 'gitStatus', conversationId: C, git: Object.assign({}, git, { ahead: 3, checkedAt: Date.now() }) }]);
  const gitScr = await decodeSeen(page, BADGE);
  const gitText = await until(async () => ((await textOf(page, BADGE)) === '↑3' ? true : null), 3000);
  check('fxGitBadgeDecode', gitFirst === 0 && !!gitScr && !!gitText, { gitFirst, gitScr, gitText });

  // 輸入框下方的 token 數：一輪進行中（turnLive）數字是綠色系的亮色（--mx）、說明寫即時；結束後換回上一輪
  const tkColor = await page.evaluate(() => { const i = document.createElement('i'); i.style.color = 'var(--mx)'; document.body.append(i); const c = getComputedStyle(i).color; i.remove(); return c; });
  await pushHeader(host, { running: true, status: '執行中', startedAt: Date.now(), lastTurn: '輸入 1,200・輸出 30 tokens', turnLive: true });
  const turnLive = await until(() => page.evaluate((s) => {
    const t = document.querySelector(s + ' .ci-turn');
    return t && t.classList.contains('live') ? { title: t.title, color: getComputedStyle(t.querySelector('.mtok-n')).color } : null;
  }, SEL), 1500, 15);
  await pushHeader(host, { running: false, status: null, startedAt: null, lastTurn: '輸入 1,250・輸出 32 tokens', turnLive: false });
  const turnDone = await until(() => page.evaluate((s) => { const t = document.querySelector(s + ' .ci-turn'); return t && !t.classList.contains('live') ? t.title : null; }, SEL), 1500, 15);
  check('fxTurnTokensLive', !!turnLive && turnLive.color === tkColor && /^這一輪到目前為止的 token（即時）：↑ 輸入 1,200、↓ 輸出 30$/.test(turnLive.title)
    && turnDone === '上一輪的 token：↑ 輸入 1,250、↓ 輸出 32', { turnLive, tkColor, turnDone });

  // 差異檢視：點開「修改檔案」後依副檔名語法上色、相鄰的刪除與新增行逐字標出改了的字；只多了 span、文字一字不差
  await host.pushItem(C, tool('fxd1', 'Done', { toolKind: 'edit', toolName: '修改檔案', title: 'src/Foo.cs', filePath: 'C:\\repo\\src\\Foo.cs',
    diff: [{ t: 'hunk', s: '@@ -1,3 +1,3 @@' }, { t: 'ctx', s: 'public class Foo {' }, { t: 'del', s: '    int count = 1;' }, { t: 'add', s: '    int total = 10;' }, { t: 'ctx', s: '}' }] }));
  const dSel = `${SEL} .it[data-id="fxd1"]`;
  await until(() => page.$(dSel), 2000);
  await sleep(1000);
  await page.evaluate((s) => document.querySelector(s).scrollIntoView({ block: 'center' }), dSel);
  await page.click(`${dSel} .t-row`);
  const diff = await until(() => page.evaluate((s) => {
    const rows = Array.from(document.querySelectorAll(s + ' .diff > .dl'));
    if (!rows.some((r) => r.querySelector('.tk-kw'))) return null;
    return {
      texts: rows.map((r) => r.querySelector('.dl-t').textContent).join('|'),
      kw: rows.filter((r) => r.querySelector('.tk-kw')).length,
      dx: rows.flatMap((r) => Array.from(r.querySelectorAll('.dx')).map((x) => (r.classList.contains('add') ? '+' : '-') + x.textContent)).join(','),
      onlySpans: rows.every((r) => Array.from(r.querySelector('.dl-t').querySelectorAll('*')).every((x) => x.tagName === 'SPAN' && x.parentElement.classList.contains('dl-t'))),
    };
  }, dSel), 3000, 20);
  await page.locator(`${dSel} .diff`).screenshot({ path: outFile('fx-diff-dark.png') });
  check('fxDiffHighlight', !!diff && diff.texts === '@@ -1,3 +1,3 @@|public class Foo {|    int count = 1;|    int total = 10;|}' && diff.kw >= 3
    && diff.dx === '-count,-1,+total,+10' && diff.onlySpans, diff);

  // 輸入框的 / 提示：角框畫在背景上（8 段線），選到的那一列左緣一格對話帳戶色的小方塊
  await page.fill(`${SEL} .cbox textarea`, '/');
  const sugg = await until(() => page.evaluate((s) => {
    const b = document.querySelector(s + ' .suggest');
    const row = b && !b.hidden && b.querySelector('.sg-row.sel');
    if (!row) return null;
    const ref = document.createElement('i');
    ref.style.color = 'var(--acc)';
    document.querySelector(s).append(ref);
    const acc = getComputedStyle(ref).color;
    ref.remove();
    return { layers: (getComputedStyle(b).backgroundImage.match(/linear-gradient/g) || []).length, dot: getComputedStyle(row, '::before').content, dotColor: getComputedStyle(row, '::before').backgroundColor, acc };
  }, SEL), 2000, 15);
  await page.fill(`${SEL} .cbox textarea`, '');
  await page.keyboard.press('Escape');
  check('fxSuggestMenu', !!sugg && sugg.layers === 8 && sugg.dot !== 'none' && sugg.dotColor === sugg.acc, sugg);

  // 標示：換帳戶的排程（虛線框＋箭頭）、橫幅左緣的方塊串、重新連線中下緣的光（只看樣式：在頁面上放同 class 的元素量）、分隔線的刻度
  const other = host.accounts.find((a) => a.id !== host.conv(C).header.accountId && a.provider === host.conv(C).header.provider) || host.accounts[1];
  await pushHeader(host, { running: true, status: '執行中', startedAt: Date.now(), pendingAccount: { accountId: other.id, label: other.label, when: 'turnEnd' } });
  await until(() => page.evaluate((s) => { const n = document.querySelector(s + ' .chip-next'); return !!n && !n.hidden; }, SEL), 1500, 15);
  await host.pushItem(C, item('fxhr', { html: '<p>上</p><hr><p>下</p>' }));
  await until(() => page.$(`${SEL} .it[data-id="fxhr"] hr`), 2000);
  const marks = await page.evaluate((s) => {
    const next = document.querySelector(s + ' .chip-next');
    const ban = document.createElement('div');
    ban.className = 'hub-status';
    ban.dataset.kind = 'reconnecting';
    ban.dataset.tone = 'warn';
    const app = document.createElement('div');
    app.className = 'app-banner';
    document.body.append(ban, app);
    const out = {
      nextBox: getComputedStyle(next, '::after').borderTopStyle, arrow: getComputedStyle(next.querySelector('.chip-next-label'), '::before').content,
      banner: getComputedStyle(app, '::before').backgroundImage, hubBar: getComputedStyle(ban, '::before').backgroundImage,
      glide: document.getAnimations().filter((a) => a.effect && a.effect.target === ban && a.effect.pseudoElement === '::after').map((a) => a.animationName),
      hr: getComputedStyle(document.querySelector(s + ' .it[data-id="fxhr"] hr')).backgroundImage,
    };
    ban.remove();
    app.remove();
    return out;
  }, SEL);
  await pushHeader(host, { running: false, status: null, startedAt: null, pendingAccount: null });
  check('fxMarks', marks.nextBox === 'dashed' && /→/.test(marks.arrow) && /repeating-linear-gradient/.test(marks.banner) && /repeating-linear-gradient/.test(marks.hubBar)
    && marks.glide.includes('fx-glide') && /repeating-linear-gradient/.test(marks.hr), marks);

  // 側欄帳戶卡：執行中的對話數變了先解碼；背景程式的徽章從沒有變成有時彈出一下、前面一顆綠燈；對話列的「!」出現時也彈出
  const accA = host.accounts[0];
  const sess0 = Object.assign({}, accA.sessions || {});
  const CARD = `#sidebar .macct[data-key="${accA.id}"]`;
  const runBase = Number(sess0.running || 0);
  accA.sessions = Object.assign({}, sess0, { running: runBase + 1, programs: 2 });
  host.pushAccounts();
  const prog = await until(() => page.evaluate((c) => {
    const p = document.querySelector(c + ' .macct-prog');
    return p && !p.hidden ? { pop: p.classList.contains('fx-badge-in'), led: getComputedStyle(p, '::before').content } : null;
  }, CARD), 1500, 10);
  const cardScr = runBase > 0 ? await decodeSeen(page, CARD + ' .macct-run-n') : true;
  const cardRun = await until(async () => ((await textOf(page, CARD + ' .macct-run-n')) === String(runBase + 1) ? true : null), 3000);
  const waitConv = host.conversations.find((x) => x.id !== C && !x.waiting && !x.deviceId) || host.conversations.find((x) => x.id !== C);
  waitConv.waiting = true;
  host.pushConversations();
  const rowPop = await until(() => page.evaluate((k) => { const b = document.querySelector('.crow[data-key="c:' + k + '"] .badge-wait'); return b && b.classList.contains('fx-badge-in') ? true : null; }, waitConv.id), 1500, 10);
  waitConv.waiting = false;
  accA.sessions = sess0;
  host.pushAccounts();
  host.pushConversations();
  check('fxSidebarBadges', !!prog && prog.pop && prog.led !== 'none' && !!cardScr && !!cardRun && !!rowPop, { prog, cardScr, cardRun, rowPop, runBase });

  // 附件：剛加進輸入框的那一個淡入，原本的不動
  host.conv(C).composer.attachments.push({ id: 'fxp3', name: '新附件.txt', kind: 'Document', size: '1 KB', path: 'C:\\x\\新附件.txt', thumbUrl: null, url: null, videoInfo: null });
  host.pushComposer(C);
  const chipIn = await until(() => page.evaluate((s) => {
    const chips = Array.from(document.querySelectorAll(s + ' .atts.in-composer > .chip-att'));
    const n = chips.find((x) => x.dataset.key === 'fxp3');
    return n ? { fresh: n.classList.contains('fx-in'), others: chips.filter((x) => x !== n && x.classList.contains('fx-in')).length } : null;
  }, SEL), 1500, 10);
  host.conv(C).composer.attachments.pop();
  host.pushComposer(C);
  check('fxAttachChipIn', !!chipIn && chipIn.fresh && chipIn.others === 0, chipIn);

  // 語音訊息的錄音波形：附件帶 wave 時畫成一格一條、高度照數字，取代檔案大小
  await host.pushItem(C, item('fxv1', { kind: 'User', html: '<p>語音訊息（0:07）</p>',
    attachments: [{ id: 'fxva', name: '語音訊息_20261024_101010.weba', kind: 'Document', size: '28 KB', path: 'C:\\x\\v.weba', thumbUrl: null, url: null, videoInfo: null, wave: '0136985310' }] }));
  const vSel = `${SEL} .it[data-id="fxv1"]`;
  await until(() => page.$(vSel + ' .att-wave'), 2000);
  const wave = await page.evaluate((s) => {
    const t = document.querySelector(s + ' .att-voice');
    return { bars: Array.from(t.querySelectorAll('.att-wave > i')).map((i) => Math.round(i.getBoundingClientRect().height * 10) / 10), size: !!t.querySelector('.att-size') };
  }, vSel);
  await page.evaluate((s) => document.querySelector(s).scrollIntoView({ block: 'center' }), vSel);
  await page.locator(`${vSel} .att-voice`).screenshot({ path: outFile('fx-voice-wave-dark.png') });
  check('fxVoiceWave', wave.bars.length === 10 && !wave.size && wave.bars[0] < wave.bars[1] && wave.bars[1] < wave.bars[3] && wave.bars[4] === Math.max(...wave.bars), wave);

  // 錄音音量表：假麥克風給真的音訊串流（正弦波），音量表跟著麥克風的音量亮、外擴光拿掉；變安靜後一格一格掉回 0；Esc 取消、不送
  await page.evaluate(() => { window.__mic.tone = 0.5; });
  await page.click(`${SEL} .cbtn.mic`);
  const loud = await until(() => page.evaluate((s) => {
    const m = document.querySelector(s + ' .mic-meter');
    const b = document.querySelector(s + ' .cbtn.mic');
    const lit = m && !m.hidden ? m.querySelectorAll('i.lit').length : 0;
    return lit >= 4 ? { lit, hasLevel: b.classList.contains('has-level'), ring: getComputedStyle(b, '::after').animationName } : null;
  }, SEL), 3000, 30);
  await page.locator(`${SEL} .dock`).screenshot({ path: outFile('fx-mic-meter-dark.png') });
  await page.evaluate(() => { window.__mic.toneGain.gain.value = 0; });
  const quiet = await until(() => page.evaluate((s) => { const m = document.querySelector(s + ' .mic-meter'); return m && m.querySelectorAll('i.lit').length === 0 ? true : null; }, SEL), 3000, 30);
  const sentBefore = host.received.filter((m) => m.cmd === 'voiceMessage').length;
  await page.keyboard.press('Escape');
  await sleep(200);
  const micOff = await page.evaluate((s) => document.querySelector(s + ' .mic-meter').hidden, SEL);
  await page.evaluate(() => { window.__mic.tone = null; });
  check('fxMicMeter', !!loud && loud.hasLevel && loud.ring === 'none' && !!quiet && micOff && host.received.filter((m) => m.cmd === 'voiceMessage').length === sentBefore, { loud, quiet, micOff });

  // 檔案抽屜：點進資料夾，前 12 個項目由上往下錯開滑進來（--fx-i 0～11）
  await page.click(`${SEL} .ch .ibtn[aria-label="檔案瀏覽器"]`);
  await page.waitForSelector(`${SEL} .drawer.open .fe`);
  await sleep(300);
  const firstFx = await page.evaluate((s) => document.querySelectorAll(s + ' .drawer .fe.fx-row-in').length, SEL);
  await page.click(`${SEL} .drawer .fe.k-dir:has(.fe-name:text-is("assets"))`);
  const drawerIn = await until(() => page.evaluate((s) => {
    const kids = Array.from(document.querySelectorAll(s + ' .drawer .dr-body > *'));
    if (!kids.length || !kids[0].classList.contains('fx-row-in')) return null;
    return { total: kids.length, lit: kids.filter((k) => k.classList.contains('fx-row-in')).map((k) => Number(k.style.getPropertyValue('--fx-i'))) };
  }, SEL), 3000, 15);
  await page.click(`${SEL} .ch .ibtn[aria-label="檔案瀏覽器"]`);
  check('fxDrawerStagger', firstFx === 0 && !!drawerIn && drawerIn.lit.length === Math.min(12, drawerIn.total) && drawerIn.lit.every((v, i) => v === i), { firstFx, drawerIn });

  // 開新分頁：側欄點一個沒開的對話，新的分頁往上滑進來；原本的分頁不動
  const tabConv = host.conversations.find((x) => x.id !== C && !x.deviceId); // 這個 app 只開了 c1 一個分頁
  await page.click(`.crow[data-key="c:${tabConv.id}"]`);
  const tabIn = await until(() => page.evaluate((k) => {
    const t = document.querySelector('.tab[data-key="' + k + '"]');
    return t ? { fresh: t.classList.contains('fx-tab-in'), others: document.querySelectorAll('.tab.fx-tab-in').length } : null;
  }, tabConv.id), 2000, 10);
  await page.click(`.tab[data-key="${C}"]`);
  await until(() => page.evaluate((s) => !document.querySelector(s).hidden, SEL), 1500, 10);
  check('fxTabIn', !!tabIn && tabIn.fresh && tabIn.others === 1, tabIn);

  // ───── 第四批（主要 app）：過載重試的倒數、背景程式晶片、Ultracode、程式碼框行號 ─────
  // 過載重試：「60 秒後自動重試」提示下方有倒數條（Web Animations 在縮）；提早開始執行＝取消（倒數條收掉、不閃），快到時間才開始＝重試開始了（框閃一下綠）
  const retryNote = (id, ago) => item(id, { kind: 'Notice', noticeKind: 'overloadRetry', time: Date.now() - ago,
    html: '<p>API 伺服器過載（529 Overloaded），60 秒後自動重試（第 1 次，最多 10 次）；在這之前送出訊息就改送你的訊息</p>' });
  const noteState = (id) => page.evaluate(([s, i]) => {
    const n = document.querySelector(s + ' .it[data-id="' + i + '"]');
    if (!n) return null;
    const fill = n.querySelector('.retry-bar > i');
    const after = document.getAnimations().filter((a) => a.effect && a.effect.target === n && a.effect.pseudoElement === '::after').map((a) => a.animationName);
    return { kind: n.classList.contains('retry-wait'), bar: !!fill, running: fill ? fill.getAnimations().map((a) => a.playState) : [],
      go: n.classList.contains('fx-retry-go'), cancel: n.classList.contains('fx-retry-cancel'), done: n.classList.contains('retry-done'), after };
  }, [SEL, id]);
  await pushHeader(host, { running: false, status: null, startedAt: null });
  await host.pushItem(C, retryNote('fxr1', 20_000));
  await until(() => page.$(`${SEL} .it[data-id="fxr1"] .retry-bar`), 2000);
  const r1 = await noteState('fxr1');
  await page.evaluate((s) => document.querySelector(s + ' .it[data-id="fxr1"]').scrollIntoView({ block: 'center' }), SEL);
  await page.locator(`${SEL} .it[data-id="fxr1"]`).screenshot({ path: outFile('fx-retry-dark.png') });
  await pushHeader(host, { running: true, status: '執行中', startedAt: Date.now() });
  const r1Cancel = await until(async () => { const x = await noteState('fxr1'); return x && x.done ? x : null; }, 1500, 15);
  await pushHeader(host, { running: false, status: null, startedAt: null });
  await host.pushItem(C, retryNote('fxr2', 59_000));
  await until(() => page.$(`${SEL} .it[data-id="fxr2"] .retry-bar`), 2000);
  await pushHeader(host, { running: true, status: '執行中', startedAt: Date.now() });
  const r2Go = await until(async () => { const x = await noteState('fxr2'); return x && x.done ? x : null; }, 1500, 15);
  await pushHeader(host, { running: false, status: null, startedAt: null });
  check('fxRetryCountdown', !!r1 && r1.kind && r1.bar && r1.running.includes('running') && !!r1Cancel && r1Cancel.cancel && !r1Cancel.go
    && !!r2Go && r2Go.go && r2Go.after.includes('fx-flash-out'), { r1, r1Cancel, r2Go });

  // 背景程式晶片：每個程式一顆綠燈；有一個自己結束了（不是在選單按停止的）晶片外框閃一下琥珀色
  const progRow = (id, cmd) => ({ id, pid: Number(id.split('-')[0]), name: 'node.exe', command: cmd, startedAt: Date.now() - 60_000 });
  await pushHeader(host, { programs: [progRow('4321-1', 'node server.js'), progRow('4322-1', 'python worker.py')] });
  const leds = await until(() => page.evaluate((s) => {
    const c = document.querySelector(s + ' .ch-prog');
    return c && !c.hidden ? c.querySelectorAll('.prog-leds > i').length : null;
  }, SEL), 1500, 15);
  await page.locator(`${SEL} .ch`).screenshot({ path: outFile('fx-prog-chip-dark.png') });
  await pushHeader(host, { programs: [progRow('4321-1', 'node server.js')] });
  const exitFx = await until(() => page.evaluate((s) => {
    const c = document.querySelector(s + ' .ch-prog');
    if (!c || !c.classList.contains('fx-prog-exit')) return null;
    return { leds: c.querySelectorAll('.prog-leds > i').length, after: document.getAnimations().filter((a) => a.effect && a.effect.target === c && a.effect.pseudoElement === '::after').map((a) => a.animationName) };
  }, SEL), 1500, 15);
  await pushHeader(host, { programs: null });
  const lingers = await page.evaluate((s) => !document.querySelector(s + ' .ch-prog').hidden, SEL);
  const hidden = await until(() => page.evaluate((s) => document.querySelector(s + ' .ch-prog').hidden || null, SEL), 2000, 20);
  check('fxProgramsChip', leds === 2 && !!exitFx && exitFx.leds === 1 && exitFx.after.includes('fx-flash-out') && lingers && !!hidden, { leds, exitFx, lingers, hidden });

  // Ultracode：強度選單裡那一項帶「多代理」記號與帳戶色描邊；選了它時輸入列的強度按鈕也帶帳戶色
  const hd0 = host.conv(C).header;
  const models = JSON.parse(JSON.stringify(hd0.models || []));
  for (const m of models) m.efforts = Array.from(new Set([...(m.efforts || ['low', 'medium', 'high']), 'xhigh', 'ultracode']));
  await pushHeader(host, { models, effort: 'ultracode' });
  await until(() => page.evaluate((s) => { const b = document.querySelector(s + ' .pick.ultra'); return b && !b.hidden ? true : null; }, SEL), 1500, 15);
  await page.click(`${SEL} .pick.ultra`);
  const ultra = await until(() => page.evaluate(() => {
    const it = document.querySelector('.menu .menu-item.mi-ultra');
    return it ? { mark: getComputedStyle(it.querySelector('.mi-label'), '::after').content, ring: getComputedStyle(it).boxShadow, label: it.querySelector('.mi-label').textContent } : null;
  }), 1500, 15);
  await page.locator('.menu').screenshot({ path: outFile('fx-ultra-menu-dark.png') });
  await page.keyboard.press('Escape');
  await pushHeader(host, { effort: 'default' });
  check('fxUltracode', !!ultra && /多代理/.test(ultra.mark) && ultra.ring !== 'none' && ultra.label === 'Ultracode', ultra);

  // 程式碼框行號：兩行以上有行號欄，行數對、字體與行高同程式碼（對得齊）、不能選取；複製只複製程式碼
  const gutter = await page.evaluate((s) => Array.from(document.querySelectorAll(s + ' .md .code')).map((c) => {
    const g = c.querySelector('.code-gutter'), pre = c.querySelector('pre');
    const lines = (pre.textContent.replace(/\r\n?/g, '\n').replace(/\n$/, '').split('\n')).length;
    if (!g) return { lines, gutter: null };
    const gs = getComputedStyle(g), ps = getComputedStyle(pre);
    return { lines, gutter: g.textContent.split('\n').length, last: g.textContent.split('\n').pop(), lineHeight: gs.lineHeight === ps.lineHeight, font: gs.fontFamily === ps.fontFamily,
      top: Math.abs((g.getBoundingClientRect().top + parseFloat(gs.paddingTop)) - (pre.getBoundingClientRect().top + parseFloat(ps.paddingTop))) < 1, select: gs.userSelect, grid: c.classList.contains('has-ln') };
  }), SEL);
  await page.evaluate((s) => document.querySelector(s + ' .it[data-id="i12"] .code').scrollIntoView({ block: 'center' }), SEL);
  await page.locator(`${SEL} .it[data-id="i12"] .code`).screenshot({ path: outFile('fx-code-gutter-dark.png') });
  check('fxCodeGutter', gutter.length > 0 && gutter.every((g) => (g.lines < 2 ? g.gutter === null : g.gutter === g.lines && g.last === String(g.lines) && g.lineHeight && g.font && g.top && g.select === 'none' && g.grid)), gutter);

  // ───── 第五批（主要 app，對話）：載入較早的訊息、對話小地圖、拖放讀數、換帳戶交接、錯誤的訊號干擾、Mermaid、主題掃描、開關燈與游標、搜尋命中 ─────
  // 載入較早的訊息：按下去到宿主回來之前，按鈕下方有分格的載入列與來回的光；回來之後原本最上面那一則的上緣掃過一道光（.older-seam）
  // 前面的步驟捲到最上面時已經載入過較早的一頁：再給宿主一頁較早的（含改了檔案的工具）、重送這個對話（hasMore）
  Object.assign(host.conv(C), { hasMore: true, older: [
    item('fx-old1', { kind: 'User', html: '<p>更早的訊息</p>' }),
    tool('fx-old2', 'Done', { toolKind: 'edit', toolName: '修改檔案', title: 'src/old.cs', diffStats: { add: 3, del: 1 } }),
  ] });
  await host.sendConversation(C);
  await until(() => page.evaluate((s) => { const r = document.querySelector(s + ' .older'); return (r && !r.hidden) || null; }, SEL), 2000, 20);
  // 實際的用法：捲到最上面時自動往前載入（IntersectionObserver）；載入列在宿主回來之前（約 120ms）才有，以 MutationObserver 記下那一刻
  await page.evaluate((s) => {
    const row = document.querySelector(s + ' .older');
    window.__older = null;
    new MutationObserver(() => {
      if (!row.classList.contains('loading') || window.__older) return;
      window.__older = { loading: true, bar: getComputedStyle(row, '::before').content,
        anims: document.getAnimations().filter((a) => a.effect && a.effect.target === row && a.effect.pseudoElement === '::after').map((a) => a.animationName) };
    }).observe(row, { attributes: true, attributeFilter: ['class'] });
  }, SEL);
  const olderBox = await page.locator(`${SEL} .scroller`).boundingBox();
  await page.mouse.move(olderBox.x + olderBox.width / 2, olderBox.y + olderBox.height / 2);
  for (let i = 0; i < 4; i++) { await page.mouse.wheel(0, -100000); await sleep(60); }
  const olderNow = await until(() => page.evaluate(() => window.__older), 2500, 15);
  const seam = await until(() => page.evaluate((s) => {
    const sm = document.querySelector(s + ' .older-seam');
    return sm ? document.getAnimations().filter((a) => a.effect && a.effect.target === sm && a.effect.pseudoElement === '::after').map((a) => a.animationName).concat(['css:' + getComputedStyle(sm, '::after').animationName]) : null;
  }, SEL), 2000, 15);
  const olderDone = await until(() => page.evaluate((s) => !document.querySelector(s + ' .older').classList.contains('loading') && !document.querySelector(s + ' .older-seam') || null, SEL), 2500, 20);
  check('fxOlderLoad', !!olderNow && olderNow.loading && olderNow.bar !== 'none' && olderNow.anims.includes('fx-glide') && !!seam && seam.includes('fx-glide') && !!olderDone,
    { olderNow, seam, olderDone });

  // 對話小地圖：補一批訊息讓對話夠長；軌道出現、標記依種類（你的訊息、錯誤、改了檔案）、「目前範圍」框由捲動時間軸驅動（捲到上面時框往上移）、
  // 點標記跳到那一則並閃一下
  for (let i = 0; i < 14; i++) {
    await host.pushItem(C, item('mm-u' + i, { kind: 'User', html: '<p>小地圖的第 ' + i + ' 則訊息</p>' }));
    await host.pushItem(C, item('mm-a' + i, { html: '<p>' + '回覆內容。'.repeat(60) + '</p>' }));
  }
  await host.pushItem(C, item('mm-err', { kind: 'Error', html: '<p>小地圖的錯誤</p>' }));
  await page.click(`${SEL} .jump`).catch(() => {});
  await sleep(1000); // 等小地圖量測（最多每 0.8 秒一次）與捲動時間軸更新
  const mm = await until(() => page.evaluate((s) => {
    const r = document.querySelector(s + ' .mmap');
    if (!r || r.hidden) return null;
    const marks = Array.from(r.querySelectorAll('.mmap-mark'));
    if (!marks.some((m) => m.dataset.kind === 'err')) return null;
    const v = r.querySelector('.mmap-view');
    const an = v.getAnimations()[0];
    const sc = document.querySelector(s + ' .scroller');
    const rr = r.getBoundingClientRect(), sr = sc.getBoundingClientRect();
    return { kinds: [...new Set(marks.map((m) => m.dataset.kind))].sort(), n: marks.length, timeline: an && an.timeline ? an.timeline.constructor.name : null,
      ty: new DOMMatrix(getComputedStyle(v).transform).m42, aligned: Math.abs(rr.top - sr.top) < 1.5 && Math.abs(rr.height - sc.clientHeight) < 1.5 && rr.right <= sr.right + 0.5 };
  }, SEL), 4000, 50);
  // 捲到最上面（以滾輪：conversation.js 才會停止貼底），等捲動時間軸更新
  const scBox = await page.locator(`${SEL} .scroller`).boundingBox();
  await page.mouse.move(scBox.x + scBox.width / 2, scBox.y + scBox.height / 2);
  for (let i = 0; i < 4; i++) { await page.mouse.wheel(0, -100000); await sleep(80); }
  await sleep(200);
  const mmTop = await page.evaluate((s) => new DOMMatrix(getComputedStyle(document.querySelector(s + ' .mmap-view')).transform).m42, SEL);
  const railBox = await page.locator(`${SEL} .mmap`).boundingBox();
  if (railBox) await page.screenshot({ path: outFile('fx-minimap-dark.png'), clip: { x: Math.max(0, railBox.x - 360), y: railBox.y, width: 380, height: railBox.height } });
  const mmJump = await page.evaluate((s) => {
    const m = document.querySelector(s + ' .mmap-mark[data-kind="err"]');
    const sc = document.querySelector(s + ' .scroller');
    const before = sc.scrollTop;
    m.click();
    return { id: m.dataset.id, before };
  }, SEL);
  const jumped = await until(() => page.evaluate(([s, j]) => {
    const n = document.querySelector(s + ' .it[data-id="' + j.id + '"]');
    const sc = document.querySelector(s + ' .scroller');
    if (!n || Math.abs(sc.scrollTop - j.before) < 50) return null;
    // 平滑捲動要捲完：兩次之間不再動
    if (window.__mmLast !== sc.scrollTop) { window.__mmLast = sc.scrollTop; return null; }
    const nr = n.getBoundingClientRect(), sr = sc.getBoundingClientRect();
    return { hit: n.classList.contains('fx-mmap-hit') || n._fxTimers && 'fx-mmap-hit' in n._fxTimers, inView: nr.top >= sr.top - 2 && nr.top < sr.bottom };
  }, [SEL, mmJump]), 2500, 30);
  check('fxMinimap', !!mm && mm.kinds.includes('user') && mm.kinds.includes('err') && mm.kinds.every((k) => ['user', 'err', 'wait', 'edit'].includes(k)) && mm.timeline === 'ScrollTimeline' && mm.aligned
    && mm.ty > mmTop + 20 && !!jumped && jumped.inView, { mm, mmTop, mmJump, jumped });
  await page.click(`${SEL} .jump`).catch(() => {});

  // 拖放讀數：拖進三個檔案（兩張圖、一份 PDF），遮罩寫出個數與種類，角框從外面收進來；拖走就收起
  const drop = await page.evaluate((s) => {
    const root = document.querySelector(s);
    const dt = new DataTransfer();
    dt.items.add(new File(['a'], 'a.png', { type: 'image/png' }));
    dt.items.add(new File(['b'], 'b.jpg', { type: 'image/jpeg' }));
    dt.items.add(new File(['c'], 'c.pdf', { type: 'application/pdf' }));
    root.querySelector('.scroller').dispatchEvent(new DragEvent('dragenter', { dataTransfer: dt, bubbles: true, cancelable: true }));
    const d = root.querySelector('.drop');
    return { shown: !d.hidden, text: d.querySelector('.drop-text').textContent, sum: d.querySelector('.drop-sum').textContent,
      anims: document.getAnimations().filter((a) => a.effect && a.effect.target === d && a.effect.pseudoElement === '::after').map((a) => a.animationName),
      bracket: getComputedStyle(d, '::after').content };
  }, SEL);
  await page.locator(`${SEL} .drop`).screenshot({ path: outFile('fx-drop-dark.png') }).catch(() => {});
  const dropGone = await page.evaluate((s) => {
    const root = document.querySelector(s);
    const dt = new DataTransfer();
    dt.items.add(new File(['a'], 'a.png', { type: 'image/png' }));
    root.querySelector('.drop').dispatchEvent(new DragEvent('dragleave', { dataTransfer: dt, bubbles: true, relatedTarget: null }));
    return root.querySelector('.drop').hidden;
  }, SEL);
  check('fxDropReadout', drop.shown && drop.text === '放開以附加 3 個檔案' && drop.sum === '圖片 2・文件 1' && drop.anims.includes('fx-lock-in-far') && drop.bracket !== 'none' && dropGone, { drop, dropGone });

  // 換帳戶的交接：標頭的帳戶換了，帳戶晶片的名稱從下面接上、外框閃一下（第一次的標頭與沒換不播）
  const acc0Id = host.conv(C).header.accountId;
  const otherAcc = host.accounts.find((a) => a.id !== acc0Id && a.provider === host.conv(C).header.provider) || host.accounts.find((a) => a.id !== acc0Id);
  await pushHeader(host, { accountId: otherAcc.id, provider: otherAcc.provider });
  const ho = await until(() => page.evaluate((s) => {
    const c = document.querySelector(s + ' .ch-acct');
    if (!c || !c.classList.contains('fx-handoff')) return null;
    const of = (el, pseudo) => document.getAnimations().filter((a) => a.effect && a.effect.target === el && (a.effect.pseudoElement || null) === pseudo).map((a) => a.animationName);
    return { label: of(c.querySelector('.chip-label'), null), ring: of(c, '::after') };
  }, SEL), 1500, 15);
  await pushHeader(host, { accountId: acc0Id, provider: host.accounts.find((a) => a.id === acc0Id).provider });
  await sleep(950);
  await pushHeader(host, {});
  const hoQuiet = await page.evaluate((s) => !document.querySelector(s + ' .ch-acct').classList.contains('fx-handoff'), SEL);
  check('fxHandoff', !!ho && ho.label.includes('fx-handoff-in') && ho.ring.includes('fx-flash-out') && hoQuiet, { ho, hoQuiet });

  // 失敗的訊號干擾：新的錯誤項目進場時左右錯位幾下、一道紅光掃過
  await host.pushItem(C, item('fx-err5', { kind: 'Error', html: '<p>這一輪失敗了</p>' }));
  const glitch = await until(() => page.evaluate((s) => {
    const n = document.querySelector(s + ' .it[data-id="fx-err5"]');
    if (!n || !n.classList.contains('fx-in')) return null;
    const of = (pseudo) => document.getAnimations().filter((a) => a.effect && a.effect.target === n && (a.effect.pseudoElement || null) === pseudo).map((a) => a.animationName);
    return { box: of(null), scan: of('::after') };
  }, SEL), 1500, 10);
  check('fxErrorGlitch', !!glitch && glitch.box.includes('fx-glitch') && glitch.box.includes('fx-in') && glitch.scan.includes('fx-err-scan'), glitch);

  // Mermaid：節點與邊帶關係（data-n、data-from／to）；第一次捲進畫面依序畫出（節點放大淡入、實線的邊畫出來）；滑過節點時相連的亮、其他淡
  const MERMAID = '<pre><code class="language-mermaid">flowchart LR\n  A["前端"] --&gt; B["中樞"]\n  B --&gt; C["CLI"]\n  A -.-&gt; D["伺服器"]</code></pre>';
  await host.pushItem(C, item('fx-mm', { html: MERMAID }));
  const mmd = await until(() => page.evaluate((s) => {
    const w = document.querySelector(s + ' .it[data-id="fx-mm"] .code-diagram');
    if (!w) return null;
    w.scrollIntoView({ block: 'center' });
    const svg = w.querySelector('svg');
    return { nodes: svg.querySelectorAll('[data-n]').length, edges: svg.querySelectorAll('[data-from]').length, drawn: svg.querySelectorAll('.mm-edge[pathLength]').length,
      grid: getComputedStyle(w).backgroundImage.includes('radial-gradient') };
  }, SEL), 3000, 30);
  const draw = await until(() => page.evaluate((s) => {
    const w = document.querySelector(s + ' .it[data-id="fx-mm"] .code-diagram');
    if (!w || !w.classList.contains('fx-mm-draw')) return null;
    return [...new Set(document.getAnimations().filter((a) => a.effect && a.effect.target && w.contains(a.effect.target)).map((a) => a.animationName))].sort();
  }, SEL), 2000, 15);
  await sleep(1700);
  const nodeB = await page.evaluate((s) => {
    const el = document.querySelector(s + ' .it[data-id="fx-mm"] .mm-node[data-n="B"]');
    const r = el.getBoundingClientRect();
    return { x: r.x + r.width / 2, y: r.y + r.height / 2 };
  }, SEL);
  nodeB.at = await page.evaluate(([x, y]) => { const e = document.elementFromPoint(x, y); return e ? (e.getAttribute('class') || e.tagName) + (e.getAttribute('data-n') ? '#' + e.getAttribute('data-n') : '') : null; }, [nodeB.x, nodeB.y]);
  await page.mouse.move(nodeB.x, nodeB.y, { steps: 3 });
  const hl = await until(() => page.evaluate((s) => {
    const svg = document.querySelector(s + ' .it[data-id="fx-mm"] svg.mm-svg');
    if (!svg.classList.contains('mm-focus')) return null;
    const lit = (q) => Array.from(svg.querySelectorAll(q)).filter((e) => e.classList.contains('mm-hl')).length;
    return { focus: true, ab: lit('.mm-edge[data-from="A"][data-to="B"]'), bc: lit('.mm-edge[data-from="B"][data-to="C"]'), ad: lit('.mm-edge[data-from="A"][data-to="D"]'),
      nodeA: lit('.mm-node[data-n="A"]'), nodeD: lit('.mm-node[data-n="D"]') };
  }, SEL), 1500, 15);
  await sleep(250); // 淡下去有 160ms 的轉場
  if (hl) hl.dim = await page.evaluate((s) => getComputedStyle(document.querySelector(s + ' .it[data-id="fx-mm"] svg.mm-svg .mm-node[data-n="D"]')).opacity, SEL);
  await page.locator(`${SEL} .it[data-id="fx-mm"] .code`).screenshot({ path: outFile('fx-mermaid-dark.png') }).catch(() => {});
  await page.mouse.move(5, 5);
  const unhl = await until(() => page.evaluate((s) => !document.querySelector(s + ' .it[data-id="fx-mm"] svg.mm-svg').classList.contains('mm-focus') || null, SEL), 1500, 15);
  // 「原始碼／圖表」切換：顯示圖表時原始碼藏起來，按一下換成原始碼、圖表藏起來，再按一下換回來
  const toggle = await page.evaluate((s) => {
    const blk = document.querySelector(s + ' .it[data-id="fx-mm"] .code-mermaid');
    const shown = () => ({ pre: getComputedStyle(blk.querySelector('pre')).display !== 'none', diagram: getComputedStyle(blk.querySelector('.code-diagram')).display !== 'none' });
    const a = shown();
    blk.querySelector('.mm-toggle').click();
    const b = shown();
    blk.querySelector('.mm-toggle').click();
    return { diagramMode: a, sourceMode: b, back: shown() };
  }, SEL);
  const toggleOk = !toggle.diagramMode.pre && toggle.diagramMode.diagram && toggle.sourceMode.pre && !toggle.sourceMode.diagram && !toggle.back.pre && toggle.back.diagram;
  check('fxMermaid', toggleOk && !!mmd && mmd.nodes >= 4 && mmd.edges >= 3 && mmd.drawn >= 2 && mmd.grid && !!draw && draw.includes('fx-mm-node') && draw.includes('fx-mm-edge')
    && !!hl && hl.ab > 0 && hl.bc > 0 && hl.ad === 0 && hl.nodeA > 0 && hl.nodeD === 0 && Number(hl.dim) < 0.5 && !!unhl, { mmd, draw, nodeB, hl, unhl, toggle });

  // 主題切換：換深淺色時以 View Transitions 由上往下掃過去（.vt-theme、::view-transition-new(root) 的 fx-theme-wipe）；第一次套用不掃
  await page.evaluate(() => {
    window.__vt = { seen: false, anims: [] };
    new MutationObserver(() => {
      if (document.documentElement.classList.contains('vt-theme') && !window.__vt.seen) {
        window.__vt.seen = true;
        requestAnimationFrame(() => requestAnimationFrame(() => { window.__vt.anims = document.getAnimations().map((a) => a.animationName + (a.effect && a.effect.pseudoElement ? a.effect.pseudoElement : '')); }));
      }
    }).observe(document.documentElement, { attributes: true, attributeFilter: ['class'] });
  });
  host.settings.theme = 'light';
  await host.send({ type: 'settings', settings: host.settings });
  const vt = await until(() => page.evaluate(() => (window.__vt.seen && window.__vt.anims.length ? window.__vt : null)), 2000, 20);
  const vtLight = await until(() => page.evaluate(() => (document.documentElement.dataset.theme === 'light' && !document.documentElement.classList.contains('vt-theme')) || null), 2500, 30);
  host.settings.theme = 'dark';
  await host.send({ type: 'settings', settings: host.settings });
  const vtDark = await until(() => page.evaluate(() => (document.documentElement.dataset.theme === 'dark' && !document.documentElement.classList.contains('vt-theme')) || null), 2500, 30);
  check('fxThemeWipe', !!vt && vt.anims.some((a) => a.startsWith('fx-theme-wipe::view-transition-new')) && !!vtLight && !!vtDark, { vt, vtLight, vtDark });

  // 開關的燈：打開時旋鈕上的小方塊亮著、切換的那一下閃一下；輸入游標是帳戶色
  await page.click('#btnSettings');
  await until(() => page.$('.dlg .switch'), 1500);
  const sw = await page.evaluate(() => {
    const wrap = Array.from(document.querySelectorAll('.dlg .switch')).find((w) => !w.input.disabled && w.getBoundingClientRect().width > 0);
    if (!wrap) return null;
    const knob = wrap.querySelector('.switch-knob');
    const led = () => getComputedStyle(knob, '::after').backgroundColor;
    const before = { checked: wrap.input.checked, led: led() };
    wrap.input.click();
    const after = { checked: wrap.input.checked, fx: wrap.classList.contains('fx-sw'),
      anims: document.getAnimations().filter((a) => a.effect && a.effect.target === knob && a.effect.pseudoElement === '::after').map((a) => a.animationName).filter(Boolean) };
    return new Promise((res) => setTimeout(() => { after.led = led(); wrap.input.click(); res({ before, after }); }, 300)); // 燈的顏色轉場 160ms 之後再讀；讀完改回來
  });
  await page.keyboard.press('Escape');
  const caret = await page.evaluate((s) => { const ta = document.querySelector(s + ' .cbox textarea'); const cs = getComputedStyle(ta); return { caret: cs.caretColor, color: cs.color }; }, SEL);
  check('fxSwitchLed', !!sw && sw.before.led !== sw.after.led && sw.after.fx && sw.after.anims.includes('fx-blink2') && caret.caret !== caret.color && caret.caret !== 'auto', { sw, caret });

  // 搜尋命中：側欄的標題裡符合的字有帳戶色方框；搜尋框裡寫找到幾個；清掉搜尋就恢復
  const firstTitle = (host.conversations.find((c) => c.title && c.title.length >= 4) || {}).title || '';
  const term = Array.from(firstTitle).slice(0, 2).join('');
  await page.fill('.sb-search-input', term);
  const sr = await until(() => page.evaluate(() => {
    const marks = document.querySelectorAll('.sb-list .crow-title mark.hit');
    const cnt = document.querySelector('.sb-count');
    if (!marks.length || !cnt || cnt.hidden) return null;
    return { marks: marks.length, text: marks[0].textContent, count: cnt.textContent, ring: getComputedStyle(marks[0]).boxShadow };
  }), 2000, 20);
  await page.locator('.sb-list').screenshot({ path: outFile('fx-search-dark.png') }).catch(() => {});
  await page.fill('.sb-search-input', '');
  const srClear = await until(() => page.evaluate(() => (!document.querySelector('.sb-list mark.hit') && document.querySelector('.sb-count').hidden) || null), 2000, 20);
  check('fxSearchMarks', !!term && !!sr && sr.text.toLowerCase() === term.toLowerCase() && /^\d+ 個$/.test(sr.count) && sr.ring !== 'none' && !!srClear, { term, sr, srClear });

  // ───── 第六批（主要 app，對話）：token 數的綠、輸出速度、工具計時、等你回應、自動壓縮、捲動位置、檔案抽屜、燈箱、匯入 ─────
  const cssColor = (v) => page.evaluate((x) => { const i = document.createElement('i'); i.style.color = 'var(' + x + ')'; document.body.append(i); const c = getComputedStyle(i).color; i.remove(); return c; }, v);
  const greenSoft = await cssColor('--mx-soft');
  const greenBright = await cssColor('--mx');
  // 綠色系的 token 數（2026-10-25 從駭客任務的綠改成紫、同日再改成深藍，2026-10-26 改回綠、再深一點）：輸入框下方的 token 數（側欄帳戶卡的今日 token 同日改成各帳戶的色系，fxTokenAccountHue）；解碼的亂碼是半形片假名
  await pushHeader(host, { running: true, status: '執行中', startedAt: Date.now(), lastTurn: '輸入 2,000・輸出 100 tokens', turnLive: true });
  await sleep(300);
  await pushHeader(host, { lastTurn: '輸入 2,000・輸出 4,321 tokens' });
  const kana = await until(() => page.evaluate((s) => {
    const cells = [...document.querySelectorAll(s + ' .ci-turn .mtok-c.scr')];
    return cells.some((c) => /[ｦ-ﾝ]/.test(c.textContent)) ? cells.map((c) => c.textContent).join('') : null;
  }, SEL), 1500, 8);
  const turnColor = await page.evaluate((s) => ({
    turn: getComputedStyle(document.querySelector(s + ' .ci-turn')).color,
  }), SEL);
  // 真的是深一點的綠：綠最多，色相（HSL）在 125°～150°（以前駭客任務的綠 #3dff7a 約 139°、狀態燈的 --green 約 151°），
  // 而且比以前暗（相對亮度 ≤ 0.4；以前的 #36d96c 是 0.52、#3dff7a 是 0.74）但看得清楚（對比 ≥ 4.5）
  const rgbOf = (c) => { const m = /^rgba?\((\d+), (\d+), (\d+)/.exec(c || ''); return m ? [+m[1], +m[2], +m[3]] : null; };
  const hueOf = (c) => {
    const v = rgbOf(c);
    if (!v) return null;
    const [r, g, b] = v;
    const min = Math.min(r, b);
    if (!(g > r && g > b) || g === min) return null;
    return 60 * ((b - r) / (g - min)) + 120;
  };
  const lum = (c) => { const v = rgbOf(c); return v ? v.map((x) => { x /= 255; return x <= 0.04045 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4; }).reduce((s, x, i) => s + x * [0.2126, 0.7152, 0.0722][i], 0) : null; };
  const paneBg = await cssColor('--bg-pane');
  const contrast = (c) => { const a = lum(c), b = lum(paneBg); return a === null || b === null ? null : (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05); };
  const isDeepGreen = (c) => { const hue = hueOf(c); return hue !== null && hue >= 125 && hue <= 150 && lum(c) <= 0.4 && contrast(c) >= 4.5; };
  check('fxTokenGreen', !!kana && turnColor.turn === greenSoft && isDeepGreen(greenSoft) && isDeepGreen(greenBright),
    { kana, turnColor, greenSoft, greenBright, hues: [hueOf(greenSoft), hueOf(greenBright)], lum: [lum(greenSoft), lum(greenBright)], contrast: [contrast(greenSoft), contrast(greenBright)] });

  // 側欄帳戶卡的今日 token 依各帳戶的顏色色系（2026-10-25 使用者要求）、in 與 out 一深一淺（2026-10-26 使用者要求）：每張卡的兩個數字都和它的帳戶色同色相（±25°，帳戶色不是灰的才比），
  // 深色主題 out 是主色（oklch 0.64；相對亮度依色相差一點，上限 0.31）、in 暗一階（out 的相對亮度至少是 in 的 1.3 倍），兩個都看得清楚（和窗格底色對比 ≥ 2.8）；
  // in 的標籤也跟著暗一階（--tk-dim 在 .mtok-in 重算，沒重算會沿用 out 的）；幾張卡的顏色不一樣。淺色主題反過來（fxTokenAccountHueLight，在下面淺色的那一頁）。顏色以 canvas 換成 sRGB（算出來的是 oklch）
  const tokShades = (pg) => pg.evaluate(() => {
    const cv = document.createElement('canvas').getContext('2d', { willReadFrequently: true });
    const rgb = (c) => { cv.clearRect(0, 0, 1, 1); cv.fillStyle = 'rgba(0, 0, 0, 0)'; cv.fillStyle = c; cv.fillRect(0, 0, 1, 1); const d = cv.getImageData(0, 0, 1, 1).data; return [d[0], d[1], d[2]]; };
    const col = (el) => (el ? rgb(getComputedStyle(el).color) : null);
    return Array.from(document.querySelectorAll('.sidebar .macct[data-key]')).map((card) => {
      const t = card.querySelector('.macct-tok:not([hidden])');
      const ns = t ? t.querySelectorAll(':scope > .mtok-v > .mtok-n') : [];
      const tags = t ? t.querySelectorAll(':scope > .mtok-tag') : [];
      return ns.length === 2 && tags.length === 2 ? { key: card.dataset.key, acc: rgb(getComputedStyle(card).getPropertyValue('--acc').trim()),
        tin: col(ns[0]), tout: col(ns[1]), tagIn: col(tags[0]), tagOut: col(tags[1]), inMarked: ns[0].parentElement.classList.contains('mtok-in') && tags[0].classList.contains('mtok-in') } : null;
    }).filter(Boolean);
  });
  const hsl = ([r, g, b]) => {
    const R = r / 255, G = g / 255, B = b / 255, max = Math.max(R, G, B), min = Math.min(R, G, B), d = max - min;
    if (!d) return { h: null, s: 0 };
    const h = max === R ? ((G - B) / d) % 6 : max === G ? (B - R) / d + 2 : (R - G) / d + 4;
    return { h: (h * 60 + 360) % 360, s: d / (1 - Math.abs(max + min - 1)) };
  };
  const rgbStr = (v) => 'rgb(' + v.join(', ') + ')';
  const round3 = (x) => Math.round(x * 1000) / 1000;
  const shadeRows = (rows, bg) => rows.map((x) => {
    const a = hsl(x.acc);
    const hueDiff = (v) => { const t = hsl(v); return a.h === null || t.h === null ? null : Math.round(Math.min(Math.abs(a.h - t.h), 360 - Math.abs(a.h - t.h))); };
    const cr = (v) => { const p = lum(rgbStr(v)), q = lum(bg); return Math.round((Math.max(p, q) + 0.05) / (Math.min(p, q) + 0.05) * 100) / 100; };
    return { key: x.key, acc: rgbStr(x.acc), tin: rgbStr(x.tin), tout: rgbStr(x.tout), gray: a.s < 0.2, inMarked: x.inMarked,
      hueIn: hueDiff(x.tin), hueOut: hueDiff(x.tout), lumIn: round3(lum(rgbStr(x.tin))), lumOut: round3(lum(rgbStr(x.tout))),
      tagLumIn: round3(lum(rgbStr(x.tagIn))), tagLumOut: round3(lum(rgbStr(x.tagOut))), crIn: cr(x.tin), crOut: cr(x.tout) };
  });
  const sameHue = (r) => r.gray || (r.hueIn !== null && r.hueIn <= 25 && r.hueOut !== null && r.hueOut <= 25);
  const accRows = shadeRows(await tokShades(page), paneBg);
  check('fxTokenAccountHue', accRows.length >= 3 && new Set(accRows.map((r) => r.tout)).size >= 3
    && accRows.every((r) => r.inMarked && sameHue(r) && r.lumOut <= 0.31 && r.lumOut >= r.lumIn * 1.3 && r.tagLumOut > r.tagLumIn
      && r.crIn >= 2.8 && r.crOut >= 2.8), accRows);
  await page.locator('.sidebar .sb-sec-accts').screenshot({ path: outFile('fx-token-shades-dark.png') }).catch(() => {});

  // 輸出速度：一輪進行中輸出數一直增加，輸入框下方 token 數後面寫「N/s」（不寫 token 這個字）；結束就收起
  for (let k = 1; k <= 4; k++) { await pushHeader(host, { lastTurn: '輸入 2,000・輸出 ' + (4321 + k * 300).toLocaleString('en-US') + ' tokens' }); await sleep(450); }
  const rate = await until(() => page.evaluate((s) => {
    const r = document.querySelector(s + ' .ci-rate');
    return r && !r.hidden && /^[0-9.]+K?\/s$/.test(r.textContent) && !/^0\//.test(r.textContent) ? { text: r.textContent, color: getComputedStyle(r).color } : null;
  }, SEL), 2500, 50);
  await page.locator(`${SEL} .cinfo`).screenshot({ path: outFile('fx-matrix-turn-dark.png') }).catch(() => {});
  await pushHeader(host, { running: false, status: null, startedAt: null, lastTurn: '輸入 2,000・輸出 5,521 tokens', turnLive: false });
  const rateGone = await until(() => page.evaluate((s) => { const r = document.querySelector(s + ' .ci-rate'); return !r || r.hidden || null; }, SEL), 1500, 15);
  check('fxTokenRate', !!rate && rate.color === greenBright && !!rateGone, { rate, rateGone });

  // 工具計時：執行中的工具卡寫出已經跑了多久，超過 30 秒變琥珀色；做完就收起
  await host.pushItem(C, tool('fx-tl', 'Running', { toolKind: 'shell', toolName: '執行指令', title: 'dotnet test', time: Date.now() - 45_000 }));
  const tl = await until(() => page.evaluate((s) => {
    const e = document.querySelector(s + ' .it[data-id="fx-tl"] .t-elapsed');
    return e && !e.hidden && /^\d+s$/.test(e.textContent) ? { text: e.textContent, slow: e.classList.contains('slow') } : null;
  }, SEL), 2500, 30);
  // 每秒的計時器（app.js 的 1 秒 setInterval；背景的頁面會被節流）：等文字變了
  const tl2 = await until(() => page.evaluate(([s, t]) => { const x = document.querySelector(s + ' .it[data-id="fx-tl"] .t-elapsed').textContent; return x !== t ? x : null; }, [SEL, tl ? tl.text : '']), 4000, 100);
  await host.pushItem(C, tool('fx-tl', 'Done', { toolKind: 'shell', toolName: '執行指令', title: 'dotnet test', time: Date.now() - 46_000 }));
  const tlDone = await until(() => page.evaluate((s) => { const e = document.querySelector(s + ' .it[data-id="fx-tl"] .t-elapsed'); return e && e.hidden ? true : null; }, SEL), 1500, 15);
  check('fxToolElapsed', !!tl && tl.slow && Number(tl.text.replace('s', '')) >= 45 && tl2 !== tl.text && !!tlDone, { tl, tl2, tlDone });

  // 等你作答的卡片：標題列右邊寫出等了多久，超過 10 分鐘燈變琥珀色
  const qAge = Object.assign(JSON.parse(JSON.stringify(host.conv(C).items.find((x) => x.id === 'i20'))), { id: 'fx-age', time: Date.now() - 12 * 60_000, status: 'Waiting' });
  await host.pushItem(C, qAge);
  const age = await until(() => page.evaluate((s) => {
    const a = document.querySelector(s + ' .it[data-id="fx-age"] .card-head .card-age');
    return a ? { text: a.textContent, stale: a.classList.contains('stale') } : null;
  }, SEL), 2500, 30);
  check('fxCardAge', !!age && age.text === '已等 12 分' && age.stale, age);
  await host.send({ type: 'removeItem', conversationId: C, id: 'fx-age' });

  // 自動壓縮上下文：用量大降時圓環上方浮出「已壓縮 412K → 96K」（百分比收縮一下）
  const ctx6 = host.conv(C).header.context;
  await pushHeader(host, { context: Object.assign({}, ctx6, { used: 412_380, percent: 41.2 }) });
  await sleep(200);
  await pushHeader(host, { context: Object.assign({}, ctx6, { used: 96_000, percent: 9.6 }) });
  const comp6 = await until(() => page.evaluate((s) => {
    const n = document.querySelector(s + ' .ctxm-note');
    return n && !n.hidden ? { text: n.textContent, anims: n.getAnimations().map((a) => a.animationName) } : null;
  }, SEL), 1500, 15);
  await pushHeader(host, { context: ctx6 });
  check('fxCompactNote', !!comp6 && comp6.text === '已壓縮 412K → 96K' && comp6.anims.includes('fx-note-in'), comp6);

  // 捲動位置：往上捲、停下時在小地圖旁浮出「第 n／m 輪」
  const posBox = await page.locator(`${SEL} .scroller`).boundingBox();
  await page.mouse.move(posBox.x + posBox.width / 2, posBox.y + posBox.height / 2);
  await page.mouse.wheel(0, -1200);
  const pos = await until(() => page.evaluate((s) => {
    const p = document.querySelector(s + ' .conv-pos');
    return p && !p.hidden ? p.textContent : null;
  }, SEL), 2500, 30);
  await page.click(`${SEL} .jump`).catch(() => {});
  check('fxScrollPos', !!pos && /^第 \d+／\d+ 輪$/.test(pos), { pos });

  // 燈箱：影像資訊（解析度・格式）與舞台的角框鎖定
  const imgSel = `${SEL} .it[data-id="i15"] .mimg`;
  const hasImg = await page.$(imgSel);
  let lbInfo = null;
  if (hasImg) {
    await page.evaluate((s) => document.querySelector(s).scrollIntoView({ block: 'center' }), imgSel);
    await page.click(imgSel + ' >> nth=0');
    lbInfo = await until(() => page.evaluate(() => {
      const r = document.querySelector('.lb .lb-res');
      const st = document.querySelector('.lb .lb-stage');
      return r && /^\d+ × \d+/.test(r.textContent) ? { res: r.textContent, bracket: getComputedStyle(st, '::after').content } : null;
    }), 3000, 30);
    await page.keyboard.press('Escape');
    await sleep(300);
  }
  check('fxLightboxInfo', !!lbInfo && lbInfo.bracket !== 'none', { hasImg: !!hasImg, lbInfo });

  // 檔案抽屜：大小比例條、最近一小時改過的燈、拖曳寬度的讀數
  await page.click(`${SEL} .ch .ibtn[aria-label="檔案瀏覽器"]`);
  await page.waitForSelector(`${SEL} .drawer.open .fe`, { timeout: 4000 }).catch(() => {});
  const lineMode = await page.$(`${SEL} .drawer .fe.line`);
  if (!lineMode) await page.click(`${SEL} .drawer .dr-head .ibtn >> nth=2`).catch(() => {});
  const fe = await until(() => page.evaluate((s) => {
    const lines = document.querySelectorAll(s + ' .drawer .fe.line');
    if (!lines.length) return null;
    return { lines: lines.length, bars: document.querySelectorAll(s + ' .drawer .fe.line .fe-bar').length, files: [...lines].filter((l) => !l.classList.contains('k-dir')).length,
      recent: document.querySelectorAll(s + ' .drawer .fe.recent').length };
  }, SEL), 2500, 30);
  const lastList = host.last('listFiles', (m) => m.conversationId === C);
  const rootEntries = (host.files && host.files[(lastList && lastList.path) || F.ROOT]) || [];
  const expectRecent = rootEntries.filter((e) => e.kind !== 'dir' && Date.now() - e.modified < 3_600_000).length;
  const hb = await page.locator(`${SEL} .drawer .dr-resize`).boundingBox();
  let wread = null;
  if (hb) {
    await page.mouse.move(hb.x + hb.width / 2, hb.y + 60);
    await page.mouse.down();
    await page.mouse.move(hb.x - 30, hb.y + 60, { steps: 4 });
    wread = await page.evaluate((s) => { const w = document.querySelector(s + ' .drawer .dr-wread'); return w ? { text: w.textContent, shown: getComputedStyle(w).display !== 'none' } : null; }, SEL);
    await page.locator(`${SEL} .drawer`).screenshot({ path: outFile('fx-drawer-dark.png') }).catch(() => {});
    await page.mouse.up();
  }
  await page.click(`${SEL} .ch .ibtn[aria-label="檔案瀏覽器"]`).catch(() => {});
  check('fxFileDrawer', !!fe && fe.bars === fe.files && fe.recent === expectRecent && !!wread && wread.shown && /^\d+ px$/.test(wread.text), { fe, expectRecent, wread });

  // 匯入本機對話：掃描中有雷達、結果有訊息數比例條、錯開進場
  // 宿主 30ms 就回結果：掃描中那一刻以 MutationObserver 記下雷達的動畫
  await page.evaluate(() => {
    window.__radar = null;
    const mo = new MutationObserver(() => {
      const r = document.querySelector('.imp-status .imp-radar > i');
      if (r && !window.__radar) { window.__radar = r.getAnimations().map((a) => a.animationName); mo.disconnect(); }
    });
    mo.observe(document.body, { childList: true, subtree: true });
  });
  await page.click('.sb-head button[title="匯入本機 CLI 歷史對話"]');
  const radar = await until(() => page.evaluate(() => window.__radar), 1500, 10);
  const imp = await until(() => page.evaluate(() => {
    const rows = [...document.querySelectorAll('.imp-row')];
    if (!rows.length) return null;
    return { rows: rows.length, bars: document.querySelectorAll('.imp-row .imp-bar').length, enter: rows.filter((r) => r.classList.contains('fx-row-in')).length };
  }), 2500, 15);
  await page.keyboard.press('Escape');
  check('fxImportScan', !!radar && radar.includes('fx-rot') && !!imp && imp.bars === imp.rows && imp.enter > 0, { radar, imp });

  // ───── 第七批（主要 app，對話）：一輪的結算單、思考的耗時、差異的變動地圖、任務清單、問卷數字鍵 ─────
  // 一輪的結算單：一輪結束時輸入區上方浮出「完成・花了多久・工具・檔案・token・首次回應」，約 4 秒後收起
  const t7 = Date.now() - 134_000;
  await pushHeader(host, { running: true, status: '執行中', startedAt: t7, lastTurn: '輸入 1,000・輸出 10 tokens', turnLive: true });
  await host.pushItem(C, item('fx7u', { kind: 'User', time: t7 - 300, html: '<p>跑一輪</p>' }));
  await host.pushItem(C, item('fx7k', { kind: 'Thinking', time: t7 + 1800, html: '<p>想一下</p>' }));
  await host.pushItem(C, tool('fx7e1', 'Done', { toolKind: 'edit', toolName: '修改檔案', title: 'src/A.cs', filePath: 'C:\\repo\\src\\A.cs', time: t7 + 20_000, diffStats: { add: 12, del: 3 } }));
  await host.pushItem(C, tool('fx7e2', 'Done', { toolKind: 'edit', toolName: '修改檔案', title: 'src/A.cs', filePath: 'C:\\repo\\src\\A.cs', time: t7 + 40_000, diffStats: { add: 2, del: 1 } }));
  await host.pushItem(C, tool('fx7r', 'Done', { time: t7 + 60_000 }));
  await host.pushItem(C, item('fx7a', { time: t7 + 130_000, html: '<p>做好了。</p>' }));
  await sleep(150);
  await pushHeader(host, { running: false, status: null, startedAt: null, lastTurn: '輸入 18,249・輸出 2,100 tokens', turnLive: false });
  const sum7 = await until(() => page.evaluate((s) => {
    const el = document.querySelector(s + ' .turn-sum');
    if (!el || el.hidden) return null;
    // 往上捲、出現「↓ 最新」時結算單移到按鈕上方：看得到、不和按鈕疊在一起
    const j = document.querySelector(s + ' .jump');
    const jr = j && !j.hidden ? j.getBoundingClientRect() : null;
    const r = el.getBoundingClientRect();
    return { parts: [...el.children].map((c) => c.className + ':' + c.textContent), state: el.dataset.state, anims: el.getAnimations().map((a) => a.animationName),
      visible: getComputedStyle(el).visibility === 'visible', jump: !!jr, clearOfJump: !jr || r.bottom <= jr.top + 1,
      box: (() => { const d = document.querySelector(s + ' .dock .cbox').getBoundingClientRect(); return { above: r.bottom <= d.top + 1, left: Math.round(r.left - d.left) }; })() };
  }, SEL), 2000, 15);
  // 結算單浮在輸入區上方：淡入（約 0.25 秒）完了再拍、連上方一起拍；那個位置最上層的元素要是它（沒被別的東西蓋住）
  await sleep(400);
  const sumTop = await page.evaluate((s) => {
    const el = document.querySelector(s + ' .turn-sum');
    const r = el.getBoundingClientRect();
    // 它是 pointer-events: none（elementFromPoint 會略過）：量的時候暫時打開
    el.style.pointerEvents = 'auto';
    const hit = document.elementFromPoint(r.left + 12, r.top + r.height / 2);
    el.style.pointerEvents = '';
    return !!hit && !!hit.closest('.turn-sum');
  }, SEL);
  const sumClip = await page.evaluate((s) => {
    const a = document.querySelector(s + ' .turn-sum').getBoundingClientRect(), b = document.querySelector(s + ' .dock').getBoundingClientRect();
    return { x: Math.min(a.left, b.left), y: a.top - 6, width: Math.max(a.right, b.right) - Math.min(a.left, b.left), height: b.bottom - a.top + 6 };
  }, SEL).catch(() => null);
  if (sumClip) await page.screenshot({ path: outFile('fx-turn-sum-dark.png'), clip: sumClip }).catch(() => {});
  const sum7Gone = await until(() => page.evaluate((s) => document.querySelector(s + ' .turn-sum').hidden || null, SEL), 6000, 100);
  // 花了多久從 134 秒前起算，探針自己的動作會多出一點
  check('fxTurnSummary', !!sum7 && sum7.state === 'ok' && sum7.box.above && sum7.visible && sum7.clearOfJump && sumTop && /^ts-dur:2 分 1[45] 秒$/.test(sum7.parts[1])
    && sum7.parts.filter((_, i) => i !== 1).join('|') === 'ts-state:完成|ts-tools:工具 3 次|ts-files:1 個檔案 +14 −4|ts-tok:輸入 18.2K・輸出 2.1K|ts-ttft:首次回應 1.8 秒'
    && sum7.anims.includes('fx-sum') && !!sum7Gone, { sum7, sumTop, sum7Gone });

  // 思考的耗時：執行中「思考中… 12 秒」每秒更新；結束後以下一個項目的時間推算「思考了 18 秒」
  const tk0 = Date.now() - 12_000;
  await host.pushItem(C, item('fx7t', { kind: 'Thinking', status: 'Running', time: tk0, html: '<p>想…</p>' }));
  const think1 = await until(() => page.evaluate((s) => {
    const n = document.querySelector(s + ' .it[data-id="fx7t"]');
    const t = n && n.querySelector('.think-time');
    return t && /^1[2-4] 秒$/.test(t.textContent) ? { label: n.querySelector('.think-label').textContent, time: t.textContent } : null;
  }, SEL), 3000, 50);
  await host.pushItem(C, item('fx7t', { kind: 'Thinking', status: 'Done', time: tk0, html: '<p>想好了</p>' }));
  await host.pushItem(C, item('fx7t2', { time: tk0 + 18_000, html: '<p>結論</p>' }));
  const think2 = await until(() => page.evaluate((s) => {
    const n = document.querySelector(s + ' .it[data-id="fx7t"]');
    return n && n.classList.contains('think-timed') ? { label: n.querySelector('.think-label').textContent, time: n.querySelector('.think-time').textContent } : null;
  }, SEL), 3000, 50);
  check('fxThinkTime', !!think1 && think1.label === '思考中…' && !!think2 && think2.label === '思考了' && think2.time === '18 秒', { think1, think2 });

  // 差異的變動地圖：60 行的差異（開頭刪 5 行、結尾加 9 行），差異框右側一條細軌，左半紅、右半綠；點了把差異框捲到那裡
  const longDiff = [{ t: 'hunk', s: '@@ -1,56 +1,60 @@' }];
  for (let i = 0; i < 59; i++) longDiff.push({ t: i < 5 ? 'del' : i >= 50 ? 'add' : 'ctx', s: '    line ' + i + ';' });
  await host.pushItem(C, tool('fx7d', 'Done', { toolKind: 'edit', toolName: '修改檔案', title: 'src/Long.cs', filePath: 'C:\\repo\\src\\Long.cs', diff: longDiff, diffStats: { add: 9, del: 5 } }));
  const d7 = `${SEL} .it[data-id="fx7d"]`;
  await until(() => page.$(d7), 2000);
  await page.evaluate((s) => document.querySelector(s).scrollIntoView({ block: 'center' }), d7);
  await page.click(`${d7} .t-row`);
  const map7 = await until(() => page.evaluate((s) => {
    const m = document.querySelector(s + ' .diff-wrap > .diff-map');
    const box = document.querySelector(s + ' .diff-wrap > .diff');
    if (!m || !box) return null;
    const cs = getComputedStyle(m);
    return { img: cs.backgroundImage, w: Math.round(m.getBoundingClientRect().width), h: Math.round(m.getBoundingClientRect().height), boxH: Math.round(box.getBoundingClientRect().height), scrolls: box.scrollHeight > box.clientHeight };
  }, d7), 2000, 20);
  let mapJump = null;
  if (map7) {
    await page.evaluate((s) => document.querySelector(s + ' .diff-map').scrollIntoView({ block: 'center' }), d7);
    await sleep(150);
    const mb = await page.locator(`${d7} .diff-map`).boundingBox();
    await page.mouse.click(mb.x + mb.width / 2, mb.y + mb.height * 0.9);
    mapJump = await until(() => page.evaluate((s) => { const b = document.querySelector(s + ' .diff-wrap > .diff'); return b.scrollTop > 0 ? b.scrollTop : null; }, d7), 1000, 10);
    await page.locator(`${d7} .diff-wrap`).screenshot({ path: outFile('fx-diff-map-dark.png') }).catch(() => {});
  }
  const shortNoMap = await page.evaluate((s) => !document.querySelector(s + ' .it[data-id="fxd1"] .diff-map'), SEL);
  // 第 0 行是 hunk：刪除在第 1～5 行（1.67%～10%）、新增在第 51～59 行（85%～100%）
  check('fxDiffMap', !!map7 && /rgb\(241, 76, 76\) 1\.67%/.test(map7.img) && /rgb\(241, 76, 76\) 10%/.test(map7.img) && /rgb\(76, 195, 138\) 85%/.test(map7.img) && map7.w === 6 && Math.abs(map7.h - map7.boxH) <= 1 && map7.scrolls
    && !!mapJump && shortNoMap, { map7, mapJump, shortNoMap });

  // 任務清單：核取方塊換成 HUD 小方塊（完成的綠色實心），清單尾端以 CSS 計數器寫「完成 N／M」
  await host.pushItem(C, item('fx7l', { html: '<ul class="contains-task-list"><li class="task-list-item"><input disabled="disabled" type="checkbox" checked="checked" /> 讀程式</li>'
    + '<li class="task-list-item"><input disabled="disabled" type="checkbox" checked="checked" /> 改規則</li><li class="task-list-item"><input disabled="disabled" type="checkbox" /> 補測試</li></ul>' }));
  const tl7 = await until(() => page.evaluate((s) => {
    const ul = document.querySelector(s + ' .it[data-id="fx7l"] ul.contains-task-list');
    if (!ul) return null;
    const boxes = [...ul.querySelectorAll('input')].map((i) => { const c = getComputedStyle(i); return { app: c.appearance, bg: c.backgroundColor, w: c.width }; });
    const li = getComputedStyle(ul.querySelector('li'));
    return { boxes, marker: li.listStyleType, after: getComputedStyle(ul, '::after').content, reset: getComputedStyle(ul).counterReset };
  }, SEL), 2000, 20);
  await page.locator(`${SEL} .it[data-id="fx7l"]`).screenshot({ path: outFile('fx-task-list-dark.png') }).catch(() => {});
  check('fxTaskList', !!tl7 && tl7.marker === 'none' && tl7.boxes.every((b) => b.app === 'none' && b.w === '10px') && tl7.boxes[0].bg !== tl7.boxes[2].bg
    && /counter\(task-done\)/.test(tl7.after) && /counter\(task-all\)/.test(tl7.after) && /task-done/.test(tl7.reset), tl7);

  // 問卷卡的數字鍵：按 1～9 選選項時那顆鍵帽下沉、亮一下
  const kSel = await pushCard(page, host, 'fx7c');
  await page.evaluate(() => {
    window.__press = null;
    const mo = new MutationObserver((recs) => {
      for (const r of recs) if (r.target.classList && r.target.classList.contains('fx-press') && !window.__press) {
        window.__press = { idx: r.target.parentElement.dataset.idx, anims: r.target.getAnimations().map((a) => a.animationName) };
      }
    });
    mo.observe(document.body, { attributes: true, attributeFilter: ['class'], subtree: true });
    window.__pressMo = mo;
  });
  await page.evaluate((s) => document.querySelector(s).focus(), kSel);
  await page.keyboard.press('2');
  const press = await until(() => page.evaluate(() => window.__press), 1000, 10);
  await page.evaluate(() => window.__pressMo && window.__pressMo.disconnect());
  check('fxKeyPress', !!press && press.idx === '2' && press.anims.includes('fx-key-press'), press);
  await host.send({ type: 'removeItem', conversationId: C, id: 'fx7c' });

  // 指令與程式碼的語法上色（2026-10-25）：工具卡標題列的指令、展開後的輸入（說明不上色）、JSON 參數、讀檔的輸出、核准卡的 PowerShell 指令；文字一字不差
  const SH = 'npm test --filter "Login Flow" | grep -v skip # 只看失敗';
  await host.pushItem(C, tool('fx7sh', 'Done', { toolKind: 'bash', toolName: '執行指令', title: SH, detail: '跑登入流程的測試\n\n' + SH, output: 'ok 12 passed' }));
  await host.pushItem(C, tool('fx7js', 'Done', { toolKind: 'mcp', toolName: 'MCP：github', title: 'search_issues', detail: '{"query": "login", "limit": 5, "open": true}' }));
  await host.pushItem(C, tool('fx7rd', 'Done', { toolKind: 'read', toolName: '讀取檔案', title: 'src/Login.cs', filePath: 'C:\\repo\\src\\Login.cs', output: 'public class Login { int tries = 3; } // 重試' }));
  for (const id of ['fx7sh', 'fx7js', 'fx7rd']) {
    const s7 = `${SEL} .it[data-id="${id}"]`;
    await until(() => page.$(s7), 2000);
    await page.evaluate((s) => document.querySelector(s).scrollIntoView({ block: 'center' }), s7);
    await page.click(`${s7} .t-row`);
  }
  const syn7 = await until(() => page.evaluate((s) => {
    const q = (id, sub) => document.querySelector(s + ' .it[data-id="' + id + '"] ' + sub);
    const kinds = (el) => (el ? [...new Set([...el.querySelectorAll('[class^="tk-"]')].map((x) => x.className))].sort() : []);
    const title = q('fx7sh', '.t-title');
    const input = q('fx7sh', '.t-body .t-sec .pre.syn');
    const json = q('fx7js', '.t-body .pre.syn');
    const read = q('fx7rd', '.t-body .pre.out.syn');
    if (!title || !input || !json || !read || !title.querySelector('.tk-fn') || !input.querySelector('.t-cmd .tk-fn') || !json.querySelector('.tk-key') || !read.querySelector('.tk-kw')) return null;
    return { titleText: title.textContent, titleKinds: kinds(title), inputText: input.textContent, noteColored: !!input.querySelector('.t-cmd-note [class^="tk-"]'),
      inputKinds: kinds(input), jsonKinds: kinds(json), readKinds: kinds(read), out: !!q('fx7sh', '.pre.out.syn') };
  }, SEL), 3000, 30);
  const synContrast7 = {};
  for (const k of (syn7 ? syn7.titleKinds : [])) synContrast7['title ' + k] = await page.evaluate(contrastOf, `${SEL} .it[data-id="fx7sh"] .t-title .${k}`);
  for (const k of (syn7 ? syn7.inputKinds : [])) synContrast7['input ' + k] = await page.evaluate(contrastOf, `${SEL} .it[data-id="fx7sh"] .t-body .pre.syn .${k}`);
  await page.locator(`${SEL} .it[data-id="fx7sh"]`).screenshot({ path: outFile('fx-command-syntax-dark.png') }).catch(() => {});
  // 核准卡：PowerShell 的指令照 powershell 上色
  const PS = 'Get-ChildItem -Path $env:TEMP -Recurse | Remove-Item -Force';
  const ap = Object.assign(JSON.parse(JSON.stringify(host.conv(C).items.find((x) => x.id === 'i20'))), { id: 'fx7ap', time: Date.now(), toolKind: 'bash', toolName: '執行 PowerShell', title: PS, detail: PS });
  await host.pushItem(C, ap);
  const ap7 = await until(() => page.evaluate((s) => {
    const c = document.querySelector(s + ' .it[data-id="fx7ap"] .ap-cmd.syn');
    return c && c.querySelector('.tk-var') ? { text: c.textContent, kinds: [...new Set([...c.querySelectorAll('[class^="tk-"]')].map((x) => x.className))].sort() } : null;
  }, SEL), 3000, 30);
  await host.send({ type: 'removeItem', conversationId: C, id: 'fx7ap' });
  check('fxCommandSyntax', !!syn7 && syn7.titleText === SH && syn7.inputText === '跑登入流程的測試\n\n' + SH && !syn7.noteColored && !syn7.out
    && ['tk-com', 'tk-fn', 'tk-key', 'tk-str'].every((k) => syn7.titleKinds.includes(k)) && syn7.jsonKinds.includes('tk-key') && syn7.jsonKinds.includes('tk-num')
    && syn7.readKinds.includes('tk-kw') && syn7.readKinds.includes('tk-com')
    && Object.values(synContrast7).length > 0 && Object.values(synContrast7).every((r) => r >= 4.5)
    && !!ap7 && ap7.text === PS && ap7.kinds.includes('tk-fn') && ap7.kinds.includes('tk-key'), { syn7, synContrast7, ap7 });

  // ───── 第八批（2026-10-25）─────
  // 待辦清單：標頭的分格進度（完成、進行中、還沒做）；一步從進行中變成完成時那一列閃綠、勾勾彈一下
  const plan8 = (steps) => item('fx8p', { kind: 'Plan', status: 'Running', html: '', plan: steps });
  await host.pushItem(C, plan8([{ text: '讀程式', status: 'completed' }, { text: '改規則', status: 'inProgress' }, { text: '補測試', status: 'pending' }, { text: '跑探針', status: 'pending' }]));
  const p8 = `${SEL} .it[data-id="fx8p"]`;
  await until(() => page.$(p8 + ' .plan-bar'), 2000);
  await page.evaluate(() => {
    window.__psDone = null;
    const mo = new MutationObserver((recs) => {
      for (const r of recs) if (r.target.classList && r.target.classList.contains('fx-ps-done') && !window.__psDone) {
        window.__psDone = { text: r.target.textContent, anims: r.target.getAnimations({ subtree: true }).map((a) => a.animationName) };
      }
    });
    mo.observe(document.body, { attributes: true, attributeFilter: ['class'], subtree: true });
    window.__psMo = mo;
  });
  await host.pushItem(C, plan8([{ text: '讀程式', status: 'completed' }, { text: '改規則', status: 'completed' }, { text: '補測試', status: 'inProgress' }, { text: '跑探針', status: 'pending' }]));
  const psDone = await until(() => page.evaluate(() => window.__psDone), 1500, 10);
  await page.evaluate(() => window.__psMo && window.__psMo.disconnect());
  const bar8 = await page.evaluate((s) => ({ cells: Array.from(document.querySelectorAll(s + ' .plan-bar > span')).map((c) => c.className),
    shadow: getComputedStyle(document.querySelector(s + ' .ps.inProgress')).boxShadow }), p8);
  await page.evaluate((s) => document.querySelector(s).scrollIntoView({ block: 'center' }), p8);
  await page.locator(p8).screenshot({ path: outFile('fx-plan-bar-dark.png') }).catch(() => {});
  check('fxPlanBar', bar8.cells.join(',') === 'pb-done,pb-done,pb-run,pb-todo' && !!psDone && psDone.text === '改規則' && psDone.anims.includes('fx-glow-out')
    && psDone.anims.includes('fx-pop') && /inset/.test(bar8.shadow), { bar8, psDone });

  // 指令的結果晶片與錯誤、警告行；搜尋的命中數、讀網頁的網域
  const OUT8 = 'Restore complete\nsrc/Login.cs(12,5): warning CS0168: 宣告了變數 e\nsrc/Auth.cs(3,1): error CS1002: 必須是 ;\n  失敗 Ns.LoginTests.Wrong [12 ms]\n'
    + '已通過! - 失敗:     1，通過:    41，略過:     0，總計:    42 - X.dll\n';
  await host.pushItem(C, tool('fx8c', 'Done', { toolKind: 'bash', toolName: '執行指令', title: 'dotnet test', detail: 'dotnet test', output: OUT8 }));
  await host.pushItem(C, tool('fx8g', 'Done', { toolKind: 'search', toolName: '搜尋檔案', title: '**/*.cs', output: 'Found 23 files\nC:/a.cs' }));
  await host.pushItem(C, tool('fx8w', 'Done', { toolKind: 'web', toolName: '讀取網頁', title: 'https://www.example.com/docs', output: '文件內容' }));
  const c8 = `${SEL} .it[data-id="fx8c"]`;
  await until(() => page.$(c8), 2000);
  await page.evaluate((s) => document.querySelector(s).scrollIntoView({ block: 'center' }), c8);
  await page.click(`${c8} .t-row`);
  const out8 = await until(() => page.evaluate(([s, sg, sw]) => {
    const pre = document.querySelector(s + ' .pre.out');
    if (!pre) return null;
    return { chips: Array.from(document.querySelectorAll(s + ' .t-outcome')).map((c) => c.dataset.kind + ':' + c.textContent), text: pre.textContent,
      marks: Array.from(pre.querySelectorAll('.ol-err, .ol-warn')).map((m) => m.className + ':' + m.textContent),
      hits: (document.querySelector(sg + ' .t-hits') || {}).textContent, host: (document.querySelector(sw + ' .t-hits') || {}).textContent };
  }, [c8, `${SEL} .it[data-id="fx8g"]`, `${SEL} .it[data-id="fx8w"]`]), 2000, 20);
  await page.locator(c8).screenshot({ path: outFile('fx-command-outcome-dark.png') }).catch(() => {});
  await page.evaluate((s) => document.querySelector(s).scrollIntoView({ block: 'center' }), `${SEL} .it[data-id="fx8g"]`);
  await page.locator(`${SEL} .it[data-id="fx8g"]`).screenshot({ path: outFile('fx-search-hits-dark.png') }).catch(() => {});
  check('fxCommandOutcome', !!out8 && out8.chips.join('|') === 'pass-dim:✓ 41|fail:✕ 1' && out8.text === OUT8 && out8.marks.length === 3
    && out8.marks[0] === 'ol-warn:src/Login.cs(12,5): warning CS0168: 宣告了變數 e' && out8.marks[2] === 'ol-err:  失敗 Ns.LoginTests.Wrong [12 ms]'
    && out8.hits === '23 個檔案' && out8.host === 'example.com', out8);

  // 第九批：myriagent-remote 的工具卡——「遠端：電腦名」（執行中也有）、myriagent-remote 自己的結束碼寫意義（124 逾時）；那台回報的名稱為準
  await host.pushItem(C, tool('fx9r', 'Failed', { toolKind: 'bash', toolName: '執行指令', title: 'myriagent-remote run --timeout 30 lab -- Get-Date', detail: 'myriagent-remote run --timeout 30 lab -- Get-Date',
    output: 'Exit code 124\n[myriagent-remote] 已送到「書房的桌機」\n[myriagent-remote] 逾時（30 秒），那台已停止\n' }));
  await host.pushItem(C, tool('fx9s', 'Running', { toolKind: 'bash', toolName: '執行指令', title: 'myriagent-remote run 書房的桌機 -- dir', detail: 'myriagent-remote run 書房的桌機 -- dir', output: '' }));
  const chipsOf = (id) => page.evaluate((s) => Array.from(document.querySelectorAll(s + ' .t-outcome')).map((c) => c.dataset.kind + ':' + c.textContent), `${SEL} .it[data-id="${id}"]`);
  await until(() => page.$(`${SEL} .it[data-id="fx9s"] .t-outcome`), 2000);
  const rx = { done: await chipsOf('fx9r'), running: await chipsOf('fx9s'),
    title: await page.evaluate((s) => (document.querySelector(s + ' .t-outcome[data-kind="fail"]') || {}).title, `${SEL} .it[data-id="fx9r"]`),
    color: await page.evaluate((s) => getComputedStyle(document.querySelector(s + ' .t-outcome[data-kind="remote"]')).color, `${SEL} .it[data-id="fx9r"]`) };
  await page.evaluate((s) => document.querySelector(s).scrollIntoView({ block: 'center' }), `${SEL} .it[data-id="fx9r"]`);
  await page.locator(`${SEL} .it[data-id="fx9r"]`).screenshot({ path: outFile('fx-remote-exec-dark.png') }).catch(() => {});
  check('fxRemoteExecChips', rx.done.join('|') === 'remote:遠端：書房的桌機|fail:124 逾時' && rx.running.join('|') === 'remote:遠端：書房的桌機'
    && /^myriagent-remote 結束碼 124：超過 --timeout/.test(rx.title || ''), rx);

  // 第十批：帳戶委派的工具卡（交辦對象的帳戶色、工作說明、子對話的狀態與「開啟」）、其他 MCP 工具（MCP 伺服器 › 工具）、使用者訊息的帳戶提及晶片
  const child10 = { id: 'cdl1', accountId: 'a3', provider: 'Codex', title: '@Codex Pro：檢查測試並回報', updatedAt: Date.now(), workingDirectory: 'C:\\repo\\myriagent', customFolder: true,
    running: true, waiting: false, unread: false, parentId: C };
  host.conversations.push(child10);
  host.pushConversations();
  const dlgDetail = '{\n  "accountId": "a3",\n  "prompt": "檢查測試並回報\\n第二行不顯示"\n}';
  await host.pushItem(C, tool('fx10d', 'Running', { toolKind: 'mcp', toolName: 'MCP：myriagent_accounts', title: 'delegate_task', detail: dlgDetail, output: '' }));
  await host.pushItem(C, tool('fx10m', 'Done', { toolKind: 'mcp', toolName: 'MCP：github', title: 'create_issue', detail: '{}', output: 'ok' }));
  await host.pushItem(C, item('fx10u', { kind: 'User', html: '請 @[Codex Pro](account:a3) 檢查測試，再問 @[舊帳戶](account:gone-1)' }));
  const d10 = `${SEL} .it[data-id="fx10d"]`;
  await until(() => page.$(d10 + ' .t-dlg .t-outcome'), 2000);
  const dlgState = () => page.evaluate((s) => {
    const n = document.querySelector(s);
    const chip = n.querySelector('.t-name .acct-mention');
    return { name: n.querySelector('.t-name').textContent, title: n.querySelector('.t-title').textContent, acc: n.style.getPropertyValue('--acc'),
      chipAcc: chip && chip.dataset.account, chips: Array.from(n.querySelectorAll('.t-dlg .t-outcome')).map((c) => c.dataset.kind + ':' + c.textContent),
      open: !!n.querySelector('.t-dlg-open'), deleg: n.classList.contains('t-deleg') };
  }, d10);
  const dRun = await dlgState();
  // 子對話開始等你回應：清單一變，晶片換成琥珀色的「等你回應」
  child10.waiting = true;
  host.pushConversations();
  await until(() => page.evaluate((s) => !!document.querySelector(s + ' .t-dlg .t-outcome[data-kind="wait"]'), d10), 2000);
  const dWait = await dlgState();
  await page.evaluate((s) => document.querySelector(s).scrollIntoView({ block: 'center' }), d10);
  await page.locator(d10).screenshot({ path: outFile('fx-delegation-wait-dark.png') }).catch(() => {});
  // 做完：結果的 JSON（result 被截斷也認得）→ 完成，子對話照結果裡的 id 找到、照樣能開
  child10.waiting = false; child10.running = false;
  host.pushConversations();
  await host.pushItem(C, tool('fx10d', 'Done', { toolKind: 'mcp', toolName: 'MCP：myriagent_accounts', title: 'delegate_task', detail: dlgDetail,
    output: '{"conversationId":"cdl1","accountId":"a3","status":"Completed","result":"測試都通過了，細節如下……' }));
  await until(() => page.evaluate((s) => !!document.querySelector(s + ' .t-dlg .t-outcome[data-kind="pass"]'), d10), 2000);
  const dDone = await dlgState();
  const mcp10 = await page.evaluate((s) => {
    const n = document.querySelector(s);
    return { tag: (n.querySelector('.t-mcp-tag') || {}).textContent, srv: (n.querySelector('.t-mcp-srv') || {}).textContent, title: n.querySelector('.t-title').textContent,
      sep: getComputedStyle(n.querySelector('.t-title'), '::before').content };
  }, `${SEL} .it[data-id="fx10m"]`);
  const men10 = await page.evaluate((s) => {
    const n = document.querySelector(s);
    return { text: n.querySelector('.u-text').textContent, chips: Array.from(n.querySelectorAll('.acct-mention')).map((c) => (c.classList.contains('unknown') ? '?' : '') + c.textContent + '@' + c.style.getPropertyValue('--acc')) };
  }, `${SEL} .it[data-id="fx10u"]`);
  await page.evaluate((s) => document.querySelector(s).scrollIntoView({ block: 'center' }), `${SEL} .it[data-id="fx10m"]`);
  await page.locator(`${SEL} .items`).screenshot({ path: outFile('fx-delegation-dark.png'), clip: undefined }).catch(() => {});
  check('fxDelegationCard', dRun.deleg && dRun.name === '委派給@Codex Pro' && dRun.chipAcc === 'a3' && /1fae8e/i.test(dRun.acc) && dRun.title === '檢查測試並回報'
    && dRun.chips.join('|') === 'deleg:執行中' && dRun.open
    && dWait.chips.join('|') === 'wait:等你回應' && dWait.open
    && dDone.chips.join('|') === 'pass:✓ 完成' && dDone.open, { dRun, dWait, dDone });
  check('fxMcpToolChip', mcp10.tag === 'MCP' && mcp10.srv === 'github' && mcp10.title === 'create_issue' && /›/.test(mcp10.sep), mcp10);
  check('fxAccountMentionChips', men10.text === '請 @Codex Pro 檢查測試，再問 @舊帳戶' && men10.chips.length === 2
    && /^@Codex Pro@#1fae8e$/i.test(men10.chips[0]) && men10.chips[1] === '?@舊帳戶@', men10);

  // 第十批：換帳戶提示的交接條（舊帳戶色→新帳戶色、剛出現時一道光滑過），等額度重置後接著做（同一個帳戶）沒有交接條
  await host.pushItem(C, item('fx10h', { kind: 'Notice', noticeKind: 'accountSwitch', html: '<p>「工作用 Max」的額度已用完，已自動改用「個人 Pro」繼續這個對話；新帳戶會收到先前對話的節錄，從中斷處接著做。</p>' }));
  await host.pushItem(C, item('fx10h2', { kind: 'Notice', noticeKind: 'accountSwitch', html: '<p>「工作用 Max」的額度已重置，自動接著做</p>' }));
  const h10 = `${SEL} .it[data-id="fx10h"]`;
  await until(() => page.$(h10 + '.ho'), 2000);
  const hoGlide = await names(page, h10, '::after');
  const ho10 = await page.evaluate(([s, s2]) => {
    const n = document.querySelector(s);
    return { from: n.style.getPropertyValue('--ho-from'), to: n.style.getPropertyValue('--ho-to'), bar: getComputedStyle(n, '::before').backgroundImage,
      ic: getComputedStyle(n.querySelector('.note-ic')).color, plain: document.querySelector(s2).classList.contains('ho') };
  }, [h10, `${SEL} .it[data-id="fx10h2"]`]);
  const purple = await page.evaluate(() => { const i = document.createElement('i'); i.style.color = '#A872E0'; document.body.append(i); const c = getComputedStyle(i).color; i.remove(); return c; });
  await page.evaluate((s) => document.querySelector(s).scrollIntoView({ block: 'center' }), h10);
  await page.locator(h10).screenshot({ path: outFile('fx-handoff-notice-dark.png') }).catch(() => {});
  check('fxHandoffNotice', /d97757/i.test(ho10.from) && /a872e0/i.test(ho10.to) && /linear-gradient/.test(ho10.bar) && ho10.ic === purple && !ho10.plain && hoGlide.includes('fx-glide'),
    { ho10, purple, hoGlide });

  // 第十批：輸入框跟著權限模式——完全自動的角框與燈是紅的、規劃是藍的，換模式時閃兩下；第一次畫不閃
  const cbox10 = `${SEL} .cbox`;
  const permOf = () => page.evaluate((s) => {
    const b = document.querySelector(s);
    const tone = (n) => getComputedStyle(document.documentElement).getPropertyValue(n).trim();
    return { perm: b.dataset.perm, flash: b.classList.contains('fx-perm'), led: getComputedStyle(b).getPropertyValue('--fx-led').trim(),
      danger: tone('--tone-danger'), plan: tone('--tone-plan'), anim: b.getAnimations({ subtree: true }).filter((a) => a.effect && a.effect.pseudoElement === '::after').map((a) => a.animationName) };
  }, cbox10);
  const perm0 = await permOf();
  const perm0Value = host.conv(C).header.permission;
  await pushHeader(host, { permission: 'FullAccess' });
  const permDanger = await until(async () => { const p = await permOf(); return p.perm === 'danger' ? p : null; }, 2000);
  await page.locator(cbox10).screenshot({ path: outFile('fx-perm-danger-dark.png') }).catch(() => {});
  await pushHeader(host, { permission: 'Plan' });
  const permPlan = await until(async () => { const p = await permOf(); return p.perm === 'plan' ? p : null; }, 2000);
  await pushHeader(host, { permission: perm0Value });
  check('fxPermissionFrame', perm0.perm !== 'danger' && perm0.perm !== 'plan' && !perm0.flash && !!permDanger && permDanger.flash && permDanger.led === permDanger.danger && permDanger.anim.includes('fx-blink2')
    && !!permPlan && permPlan.led === permPlan.plan, { perm0, permDanger, permPlan });

  // 第十批：圖片還沒載好時的佔位（點狀網格、四角括號，縮圖格寫檔名）；載不到時拿掉佔位
  const origin10 = new URL(page.url()).origin;
  const held10 = [];
  await page.route('**/ph-hold/**', (route) => { held10.push(route); });
  await host.pushItem(C, item('fx10i', { html: '<p>截圖：</p><p><img src="' + origin10 + '/ph-hold/b.png" alt="截圖"></p>',
    media: [{ kind: 'image', url: origin10 + '/ph-hold/a.png', name: 'screenshot-1440.png', path: 'C:\\shots\\screenshot-1440.png' }] }));
  const i10 = `${SEL} .it[data-id="fx10i"]`;
  await until(() => page.$(i10 + ' .md-img'), 2000);
  await page.evaluate((s) => document.querySelector(s).scrollIntoView({ block: 'center' }), i10);
  await sleep(300);
  const phOf = () => page.evaluate((s) => {
    const md = document.querySelector(s + ' .md-img'), tile = document.querySelector(s + ' .mimg'), ti = tile && tile.querySelector('img');
    const cs = getComputedStyle(md);
    return { mdCls: md.className, mdH: md.getBoundingClientRect().height, mdBg: cs.backgroundImage, tileBg: ti ? getComputedStyle(ti).backgroundImage : null,
      cap: tile ? getComputedStyle(tile, '::after').content : null, op: cs.opacity };
  }, i10);
  const ph = await phOf();
  await page.locator(i10).screenshot({ path: outFile('fx-img-placeholder-dark.png') }).catch(() => {});
  // 收尾：回一張真的圖（1×1 PNG），載好之後佔位拿掉（以 abort 收尾會在主控台留下載入失敗）
  const png10 = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=', 'base64');
  for (const r of held10) await r.fulfill({ status: 200, contentType: 'image/png', body: png10 }).catch(() => {});
  await page.unroute('**/ph-hold/**');
  const phGone = await until(async () => { const p = await phOf(); return /fx-img-in/.test(p.mdCls) ? p : null; }, 3000);
  check('fxImgPlaceholder', /radial-gradient/.test(ph.mdBg) && ph.mdH >= 135 && ph.op === '1' && /radial-gradient/.test(ph.tileBg || '') && /screenshot-1440\.png/.test(ph.cap || '')
    && !!phGone && phGone.mdBg === 'none', { ph, phGone, held: held10.length });

  // 第十批：換頁——儀表板的區塊依序進場（第 2 塊晚 40ms），回到對話時整個工作區淡入
  await page.click('#viewSwitch .seg-btn[data-view="dashboard"]');
  const view10 = await until(() => page.evaluate(() => {
    const d = document.getElementById('dashboard');
    const kids = Array.from(d.querySelectorAll('.dash-inner > *')).slice(0, 3);
    if (!d.classList.contains('fx-view-in') || !kids.length) return null;
    return kids.map((k) => k.getAnimations().filter((a) => a.animationName === 'fx-view-in').map((a) => a.effect.getTiming().delay)[0] ?? null);
  }), 2000, 10);
  await page.click('#viewSwitch .seg-btn[data-view="chat"]');
  const work10 = await until(() => page.evaluate(() => {
    const w = document.getElementById('workspace');
    return w.classList.contains('fx-view-in') ? w.getAnimations().map((a) => a.animationName) : null;
  }), 2000, 10);
  await sleep(700);
  check('fxViewSwitch', !!view10 && view10[0] === 0 && view10[1] === 40 && view10[2] === 80 && !!work10 && work10.includes('fx-view-in')
    && !(await hasClass(page, '#workspace', 'fx-view-in')), { view10, work10 });

  // 第十批：側欄的子對話（cdl1 的 parentId 是 c1）掛在主對話底下、最後一個接到底；標頭的來源晶片（c1 暫時當成從 c4 延伸出來的）
  const side10 = await until(() => page.evaluate(() => {
    const rows = Array.from(document.querySelectorAll('.crow'));
    const i = rows.findIndex((r) => r._id === 'c1');
    const k = rows.findIndex((r) => r._id === 'cdl1');
    if (i < 0 || k < 0) return null;
    const kid = rows[k];
    const cs = getComputedStyle(kid);
    return { parentAt: i, childAt: k, child: kid.classList.contains('crow-child'), last: kid.classList.contains('crow-last'),
      pad: cs.paddingLeft, lines: (cs.backgroundImage.match(/linear-gradient/g) || []).length, parentChild: rows[i].classList.contains('crow-child') };
  }), 2000);
  await page.locator('.crow-child').first().screenshot({ path: outFile('fx-sidebar-child-dark.png') }).catch(() => {});
  check('fxSidebarChild', !!side10 && side10.childAt === side10.parentAt + 1 && side10.child && side10.last && !side10.parentChild && side10.pad === '25px' && side10.lines === 3, side10);
  const c1Entry = host.conversations.find((x) => x.id === C);
  Object.assign(c1Entry, { forkedFrom: 'c4', forkedFromTitle: '舊的標題' });
  host.pushConversations();
  const orig10 = await until(() => page.evaluate((s) => {
    const b = document.querySelector(s + ' .ch-origin');
    return b && !b.hidden ? { text: b.textContent, kind: b.dataset.kind, disabled: b.disabled, title: b.title, icon: !!b.querySelector('svg') } : null;
  }, SEL), 2000);
  delete c1Entry.forkedFrom; delete c1Entry.forkedFromTitle;
  host.pushConversations();
  const originGone = await until(() => page.evaluate((s) => { const b = document.querySelector(s + ' .ch-origin'); return b && b.hidden ? true : null; }, SEL), 2000);
  check('fxOriginChip', !!orig10 && orig10.text === '延伸自「產生三張儀表板示意圖」' && orig10.kind === 'fork' && !orig10.disabled && orig10.icon
    && /點一下開啟原對話/.test(orig10.title) && !!originGone, { orig10, originGone });

  // 第十批：每一輪的分隔線——和前一則隔了 2 小時，摘要最前面寫「隔 2 小時」（滑過看兩邊的時間）
  await host.pushItem(C, item('fx10g', { kind: 'User', time: Date.now() + 2 * 3600000 + 60000, html: '隔了一陣子再問' }));
  const gap10 = await until(() => page.evaluate((s) => {
    const g = document.querySelector(s + ' .turn-rule .rl-gap');
    return g ? { text: g.textContent, title: g.title, first: g === g.parentElement.firstElementChild } : null;
  }, `${SEL} .it[data-id="fx10g"]`), 2000);
  check('fxTurnGap', !!gap10 && gap10.text === '隔 2 小時' && gap10.first && /^前一則在 .+，這則在 .+$/.test(gap10.title), gap10);

  // 第十批：文字排版——回覆的標題平衡換行、已結束的回覆段落避免最後一行只剩一個字，串流中的段落不套
  await host.pushItem(C, item('fx10w', { html: '<h2>標題平衡換行的示範：第二行不會只剩一兩個字</h2><p>已經結束的回覆段落避免最後一行只剩一個字。</p>' }));
  await host.pushItem(C, item('fx10r', { status: 'Running', html: '<p>串流中的段落照一般的換行</p>' }));
  const wrap10 = await until(() => page.evaluate(([a, b]) => {
    const h2 = document.querySelector(a + ' .md h2'), p = document.querySelector(a + ' .md p'), rp = document.querySelector(b + ' .md p');
    if (!h2 || !p || !rp) return null;
    const w = (e) => getComputedStyle(e).textWrapStyle || getComputedStyle(e).textWrap;
    return { h2: w(h2), done: w(p), running: w(rp) };
  }, [`${SEL} .it[data-id="fx10w"]`, `${SEL} .it[data-id="fx10r"]`]), 2000);
  await host.pushItem(C, item('fx10r', { status: 'Done', html: '<p>串流中的段落照一般的換行</p>' }));
  check('fxTextWrap', !!wrap10 && /balance/.test(wrap10.h2) && /pretty/.test(wrap10.done) && !/pretty|balance/.test(wrap10.running), wrap10);

  // 串流游標（2026-11-12，items.js 的 caretTyping）：字還在來的時候項目帶 fx-typing、游標不掛動畫（樣子是亮的），
  // 停下來約 250 ms 後才開始閃，而且亮暗的時刻從最後一次更新起算（和以前每次重建就從頭閃一樣：0～500 ms 亮、500～1000 ms 暗）；
  // 又有新的字就回到不掛動畫，結束時記號拿掉
  const caretSel = `${SEL} .it[data-id="fxcaret"]`;
  const caretState = () => page.evaluate((sel) => {
    const it = document.querySelector(sel);
    const last = it && it.querySelector('.md > :last-child');
    if (!last) return null;
    const anims = last.getAnimations({ subtree: true }).filter((a) => a.effect && a.effect.pseudoElement === '::after');
    const since = performance.now() - it._typedAt;
    return { typing: it.classList.contains('fx-typing'), anims: anims.map((a) => a.animationName), since: Math.round(since),
      drift: anims.length ? Math.round(anims[0].currentTime - since) : null, opacity: Number(getComputedStyle(last, '::after').opacity) };
  }, caretSel);
  const caretFlow = [];
  for (let i = 0; i < 8; i++) {
    await host.pushItem(C, item('fxcaret', { status: 'Running', html: '<p>串流中的字' + '再多一點'.repeat(i) + '</p>' }));
    await sleep(50);
    caretFlow.push(await caretState());
  }
  // 停下來之後每 130 ms 看一次（約 2 秒）：開始閃了、動畫的時間等於「最後一次更新到現在」、亮暗照 1 秒一輪（離交界 80 ms 內的不看）
  const caretRest = [];
  for (let i = 0; i < 15; i++) { await sleep(130); caretRest.push(await caretState()); }
  const blinking = caretRest.filter((c) => c && !c.typing && c.anims.join() === 'fx-blink');
  const phaseOk = blinking.every((c) => { const ph = c.since % 1000; return Math.abs(ph - 500) < 80 || ph < 80 || ph > 920 || (ph < 500 ? c.opacity > 0.9 : c.opacity < 0.5); });
  await host.pushItem(C, item('fxcaret', { status: 'Running', html: '<p>又來了新的字</p>' }));
  await sleep(80);
  const caretAgain = await caretState();
  await host.pushItem(C, item('fxcaret', { status: 'Done', html: '<p>又來了新的字</p>' }));
  await sleep(80);
  const caretDone = await page.evaluate((sel) => { const it = document.querySelector(sel); return { typing: it.classList.contains('fx-typing'), running: it.classList.contains('running') }; }, caretSel);
  check('fxStreamCaret', caretFlow.every((c) => c && c.typing && c.anims.length === 0 && c.opacity === 1)
    && blinking.length >= 10 && caretRest.filter((c) => c && c.typing).every((c) => c.since < 420)
    && blinking.every((c) => Math.abs(c.drift) < 120) && phaseOk && blinking.some((c) => c.opacity < 0.5) && blinking.some((c) => c.opacity > 0.9)
    && !!caretAgain && caretAgain.typing && caretAgain.anims.length === 0 && caretAgain.opacity === 1 && !caretDone.typing && !caretDone.running,
  { flow: caretFlow.slice(-2), rest: caretRest, caretAgain, caretDone });

  // 第十一批：分享給協作人的標記——側欄列是小人像＋人數、分頁是小人像，滑過列出名稱；不分享了就拿掉
  const collab11 = { list: [{ id: 'u1', userName: 'ming', displayName: '小明' }, { id: 'u2', userName: 'hua', displayName: '小華' }], shares: { [C]: ['u1', 'u2'] } };
  await sendNow(page, [{ type: 'collaborators', collaborators: collab11 }]);
  const shareState = () => page.evaluate((c) => {
    const row = document.querySelector('#sidebar .crow[data-key="c:' + c + '"] .crow-share');
    const tab = document.querySelector('.tab[data-key="' + c + '"] .tab-share');
    const vis = (e) => !!e && !e.hidden && e.getBoundingClientRect().width > 0;
    return { row: vis(row), rowN: row && row.textContent, rowTitle: row && row.title, tab: vis(tab), tabTitle: tab && tab.title };
  }, C);
  const share1 = await until(async () => { const x = await shareState(); return x.row && x.tab ? x : null; }, 2000);
  await sendNow(page, [{ type: 'collaborators', collaborators: { list: collab11.list, shares: {} } }]);
  const share0 = await until(async () => { const x = await shareState(); return !x.row && !x.tab ? x : null; }, 2000);
  check('fxShareBadge', !!share1 && share1.rowN === '2' && share1.rowTitle === '分享給協作人：小明、小華' && share1.tabTitle === share1.rowTitle && !!share0, { share1, share0 });

  // 第十一批：協作人的顏色——協作人送的訊息左緣那條線與名稱前的小方塊是同一個顏色（依名稱固定）
  await host.pushItem(C, item('fx11u', { kind: 'User', html: '<p>幫我看一下這個錯誤</p>', author: '小明' }));
  const tint = await until(() => page.evaluate((s) => {
    const n = document.querySelector(s);
    if (!n) return null;
    const box = n.querySelector('.u-box'), au = n.querySelector('.u-author');
    return { by: n.classList.contains('by-collab'), collab: n.style.getPropertyValue('--collab'), bar: getComputedStyle(box, '::before').backgroundColor,
      dot: au && getComputedStyle(au, '::before').backgroundColor, text: au && au.textContent };
  }, `${SEL} .it[data-id="fx11u"]`), 2000);
  const hexRgb = (hx) => { const v = parseInt(String(hx).slice(1), 16); return `rgb(${v >> 16 & 255}, ${v >> 8 & 255}, ${v & 255})`; };
  check('fxCollabColor', !!tint && tint.by && /^#[0-9a-f]{6}$/.test(tint.collab) && tint.bar === hexRgb(tint.collab) && tint.dot === tint.bar && tint.text === '協作人 小明', tint);

  // 第十一批：問卷的作答進度——兩題、一題一格；選了第一題的選項那格亮、目前那題跳到第二題；第二題補充說明有字那格多一個小點
  await host.pushItem(C, item('fx11q', { kind: 'Question', status: 'Waiting', html: undefined, requestKey: 'rq11',
    questions: [{ id: 'q1', text: '要用哪個資料庫？', options: [{ label: 'SQLite' }, { label: 'SQL Server' }] },
      { id: 'q2', text: '要不要加索引？', options: [{ label: '要' }, { label: '不要' }] }] }));
  const q11 = `${SEL} .it[data-id="fx11q"]`;
  await until(() => page.$(q11 + ' .q-progress .q-cell'), 2000);
  const cells = () => page.evaluate((s) => Array.from(document.querySelectorAll(s + ' .q-cell')).map((c) => (c.classList.contains('done') ? 'D' : '-') + (c.classList.contains('cur') ? 'C' : '') + (c.classList.contains('noted') ? 'N' : '')), q11);
  const qBefore = await cells();
  await page.click(q11 + ' .q-block[data-qi="0"] .opt:not(.opt-other)');
  const qAfter = await until(async () => { const c = await cells(); return c[0] === 'D' ? c : null; }, 1500);
  const note = await page.$(q11 + ' .q-block[data-qi="1"] .q-note');
  if (note) await note.fill('只對常用的欄位');
  const qNoted = await cells();
  check('fxQuestionProgress', JSON.stringify(qBefore) === '["-C","-"]' && !!qAfter && JSON.stringify(qAfter) === '["D","-C"]'
    && (!note || JSON.stringify(qNoted) === '["D","-CN"]'), { qBefore, qAfter, qNoted, noteField: !!note });

  // 第十一批：換版排空的進度與強制倒數——3 個對話在等、強制停下還剩 5 分鐘：3 顆亮、10 格倒數亮 5 格；剩 1 個時熄掉 2 顆
  const drain = (waiting) => ({ type: 'appUpdate', state: { phase: 'draining', managed: true, available: '0.2.999', waiting, forced: true, forceAt: Date.now() + 4.5 * 60_000 } });
  await sendNow(page, [drain(3)]);
  const hudOf = () => page.evaluate(() => {
    const b = document.getElementById('appBanner');
    const hud = b && b.querySelector('.drain-hud');
    return hud ? { pips: hud.querySelectorAll('.drain-pips > i').length, lit: hud.querySelectorAll('.drain-pips > i.on').length,
      cd: hud.querySelectorAll('.force-cd > i').length, cdLit: hud.querySelectorAll('.force-cd > i.on').length, title: hud.title } : null;
  });
  const hud3 = await until(hudOf, 2000);
  await sendNow(page, [drain(1)]);
  const hud1 = await until(async () => { const x = await hudOf(); return x && x.lit === 1 ? x : null; }, 2000);
  await page.locator('#appBanner').screenshot({ path: outFile('fx-drain-hud-dark.png') }).catch(() => {});
  await sendNow(page, [{ type: 'appUpdate', state: { phase: 'idle', managed: true } }]);
  const hudGone = await until(async () => ((await hudOf()) === null ? true : null), 2000);
  check('fxDrainHud', !!hud3 && hud3.pips === 3 && hud3.lit === 3 && hud3.cd === 10 && hud3.cdLit === 5 && /已停下 0／3 個對話/.test(hud3.title) && /再 5 分鐘強制停下/.test(hud3.title)
    && !!hud1 && hud1.pips === 3 && !!hudGone, { hud3, hud1, hudGone });

  // 第十一批：行內程式碼裡的路徑——資料夾變淡、檔名亮，分隔符號後面可以換行；不像路徑的（指令）照舊
  await host.pushItem(C, item('fx11p', { html: '<p>改了 <code>C:\\repo\\proj\\src\\Hub\\AgentHub.cs</code> 與 <code>src/Web/fx.css</code>，再跑 <code>npm run build</code></p>' }));
  const paths = await until(() => page.evaluate((s) => {
    const codes = Array.from(document.querySelectorAll(s + ' .md code'));
    if (codes.length < 3) return null;
    return codes.map((c) => ({ path: c.classList.contains('md-path'), base: (c.querySelector('.p-base') || {}).textContent || null, wbr: c.querySelectorAll('wbr').length, text: c.textContent,
      dim: c.querySelector('.p-dir') ? getComputedStyle(c.querySelector('.p-dir')).color : null, hi: c.querySelector('.p-base') ? getComputedStyle(c.querySelector('.p-base')).color : null }));
  }, `${SEL} .it[data-id="fx11p"]`), 2000);
  check('fxPathParts', !!paths && paths[0].path && paths[0].base === 'AgentHub.cs' && paths[0].wbr === 5 && paths[0].text === 'C:\\repo\\proj\\src\\Hub\\AgentHub.cs'
    && paths[0].dim !== paths[0].hi && paths[1].path && paths[1].base === 'fx.css' && !paths[2].path, paths);

  // 子代理：步數、錯誤數、正在做的工具；步數多了時晶片彈一下；結束後照「共 N 項步驟」、不寫正在做的
  const a8 = `${SEL} .it[data-id="fx8a"]`;
  const agent8 = (status, output) => tool('fx8a', status, { toolKind: 'agent', toolName: '子代理', title: '找出登入失敗的原因', output });
  await host.pushItem(C, agent8('Running', '▸ 讀取檔案：src/Login.cs\n💭 思考：先看呼叫端\n'));
  await until(() => page.$(a8 + ' .t-agent-steps'), 2000);
  await page.evaluate(() => {
    window.__step = null;
    const mo = new MutationObserver((recs) => { for (const r of recs) if (r.target.classList && r.target.classList.contains('fx-step')) window.__step = r.target.getAnimations().map((a) => a.animationName); });
    mo.observe(document.body, { attributes: true, attributeFilter: ['class'], subtree: true });
    window.__stepMo = mo;
  });
  await host.pushItem(C, agent8('Running', '▸ 讀取檔案：src/Login.cs\n💭 思考：先看呼叫端\n▸ 搜尋內容：LoginAsync\n  ✗ 找不到檔案\n'));
  const ag1 = await until(() => page.evaluate((s) => {
    const st = document.querySelector(s + ' .t-agent-steps');
    return st && st.textContent === '4 步' ? { now: (document.querySelector(s + ' .t-agent-now') || {}).textContent, err: (document.querySelector(s + ' .t-outcome[data-kind="fail"]') || {}).textContent } : null;
  }, a8), 2000, 20);
  const stepFx = await until(() => page.evaluate(() => window.__step), 1000, 10);
  await page.evaluate(() => window.__stepMo && window.__stepMo.disconnect());
  await page.evaluate((s) => document.querySelector(s).scrollIntoView({ block: 'center' }), a8);
  await page.locator(a8).screenshot({ path: outFile('fx-agent-steps-dark.png') }).catch(() => {});
  await host.pushItem(C, agent8('Done', '── 子代理執行紀錄（共 57 項步驟，只列最後 2 項）──\n▸ a\n▸ b\n\n── 子代理結論 ──\n原因是逾時'));
  const ag2 = await until(() => page.evaluate((s) => {
    const st = document.querySelector(s + ' .t-agent-steps');
    return st && st.textContent === '57 步' ? { now: !!document.querySelector(s + ' .t-agent-now') } : null;
  }, a8), 2000, 20);
  check('fxAgentSteps', !!ag1 && ag1.now === '搜尋內容：LoginAsync' && ag1.err === '✕ 1' && !!stepFx && stepFx.includes('fx-pop') && !!ag2 && !ag2.now, { ag1, stepFx, ag2 });

  // 每一輪的分隔線：新的使用者訊息上緣寫出前一段的摘要；高 14px、那則的上邊距扣成 4px，泡泡的位置和沒有分隔線時一樣
  await host.pushItem(C, item('fx8u', { kind: 'User', html: '<p>下一輪</p>' }));
  const u8 = `${SEL} .it[data-id="fx8u"]`;
  const rule8 = await until(() => page.evaluate((s) => {
    const u = document.querySelector(s);
    const r = u && u.querySelector(':scope > .turn-rule');
    if (!r) return null;
    const prev = u.previousElementSibling;
    const gapOf = () => Math.round((u.querySelector('.u-box').getBoundingClientRect().top - prev.getBoundingClientRect().bottom) * 10) / 10;
    const withRule = gapOf();
    // 同一個節點拿掉分隔線（class 與元素）再量一次：泡泡的位置要完全一樣
    u.classList.remove('has-rule');
    r.style.display = 'none';
    const without = gapOf();
    u.classList.add('has-rule');
    r.style.display = '';
    return { text: r.textContent, h: Math.round(r.getBoundingClientRect().height), mt: getComputedStyle(u).marginTop, withRule, without, all: document.querySelectorAll('.it-user.has-rule').length };
  }, u8), 2000, 20);
  await page.evaluate((s) => document.querySelector(s).scrollIntoView({ block: 'center' }), u8);
  const ruleClip = await page.evaluate((s) => { const r = document.querySelector(s).getBoundingClientRect(); return { x: r.left - 8, y: r.top - 70, width: r.width + 16, height: r.height + 80 }; }, u8);
  await page.screenshot({ path: outFile('fx-turn-rule-dark.png'), clip: ruleClip }).catch(() => {});
  check('fxTurnRule', !!rule8 && rule8.h === 14 && rule8.mt === '4px' && rule8.withRule === rule8.without && /工具 \d+ 次/.test(rule8.text) && rule8.all >= 2, rule8);

  // 佇列：每一列前面一格序號（CSS 計數器）、第一列標「下一則」（沒在執行時是「暫停中」）；離開的那一列往上淡出再移除
  const q8 = await page.evaluate((s) => {
    const rows = Array.from(document.querySelectorAll(s + ' .queue-row'));
    return rows.length >= 2 ? { n: rows.length, before: getComputedStyle(rows[0], '::before').content, first: getComputedStyle(rows[0], '::after').content, second: getComputedStyle(rows[1], '::after').content } : null;
  }, SEL);
  await page.locator(`${SEL} .queue`).screenshot({ path: outFile('fx-queue-dark.png') }).catch(() => {});
  host.conv(C).composer.queue.shift();
  host.pushComposer(C);
  const leave8 = await until(() => page.evaluate((s) => {
    const r = document.querySelector(s + ' .queue-row.fx-leave');
    return r ? { anims: r.getAnimations().map((a) => a.animationName), key: r.dataset.key || null } : null;
  }, SEL), 1000, 5);
  const leaveGone = await until(() => page.evaluate((s) => !document.querySelector(s + ' .queue-row.fx-leave'), SEL), 1500, 20);
  check('fxQueueOrder', !!q8 && /counter\(qn\)/.test(q8.before) && /^"(下一則|暫停中)"$/.test(q8.first) && q8.second === 'none'
    && !!leave8 && leave8.anims.includes('fx-q-leave') && !leave8.key && !!leaveGone, { q8, leave8, leaveGone });

  // 設定頁的滑桿：分格刻度的軌道、已選的比例與格數交給 CSS 變數
  await page.evaluate(() => import('/settings.js').then((m) => m.openSettings()));
  const rg8 = await until(() => page.evaluate(() => {
    const r = document.querySelector('.dlg-settings .range');
    if (!r) return null;
    r.scrollIntoView({ block: 'center' });
    return { app: getComputedStyle(r).appearance, fill: r.style.getPropertyValue('--fill'), ticks: r.style.getPropertyValue('--ticks'), value: Number(r.value), min: Number(r.min), max: Number(r.max), step: Number(r.step) };
  }), 2000, 20);
  if (rg8) await page.locator('.dlg-settings .range').first().locator('..').screenshot({ path: outFile('fx-range-dark.png') }).catch(() => {});
  await page.keyboard.press('Escape');
  await settle(page, 150);
  const wantFill = rg8 ? Math.round(((rg8.value - rg8.min) / (rg8.max - rg8.min)) * 1000) / 10 + '%' : null;
  check('fxRange', !!rg8 && rg8.app === 'none' && rg8.fill === wantFill && Number(rg8.ticks) === Math.round((rg8.max - rg8.min) / rg8.step), { rg8, wantFill });

  // 儀表板摘要：執行中的對話數變了，數字先閃過亂碼再鎖定
  await host.send({ type: 'showView', view: 'dashboard' });
  const RUN_N = '.dash-summary .sum-item:nth-child(2) .sum-n'; // 第二項是執行中的對話數
  await until(() => page.$('.dash-summary .sum-n .mtok-n'), 2000);
  const acc0 = host.accounts[0];
  const runBefore = await textOf(page, RUN_N);
  acc0.sessions = Object.assign({}, acc0.sessions, { running: Number((acc0.sessions || {}).running || 0) + 2 });
  host.pushAccounts();
  const sumScr = await decodeSeen(page, RUN_N);
  const runAfter = await until(async () => { const t = await textOf(page, RUN_N); return t !== runBefore && /^[0-9]+$/.test(t || '') ? t : null; }, 3000);
  check('fxSummaryDecode', !!sumScr && !!runAfter && Number(runAfter) === Number(runBefore) + 2, { runBefore, sumScr, runAfter });

  // 打開儀表板：走勢線由左往右畫出來（外層與走勢線反方向位移）、末端的點等線畫到了才淡入
  await host.send({ type: 'showView', view: 'chat' });
  await sleep(200);
  await host.send({ type: 'showView', view: 'dashboard' });
  const wipe = await until(() => page.evaluate(() => {
    const b = document.querySelector('.spark.fx-wipe');
    if (!b) return null;
    const names = (el) => el.getAnimations().map((a) => a.animationName);
    return { window: names(b.querySelector('.spark-wipe')), line: names(b.querySelector('.spark-svg')), end: names(b.querySelector('.spark-end')), count: document.querySelectorAll('.spark.fx-wipe').length };
  }), 1500, 10);
  check('fxSparkReveal', !!wipe && wipe.window.includes('fx-wipe-window') && wipe.line.includes('fx-wipe-content') && wipe.end.includes('fx-fade-in'), wipe);

  // ───── 第五批（主要 app，儀表板）：額度用完的鎖定與重置倒數、健康度的分格量表、滑鼠探照光 ─────
  // 額度用完：第一個時間窗到 100%（還有 2 小時重置、5 小時窗）：儀表板與側欄的圓環鎖上（那一下播一次），儀表板的倒數條亮 8／20 格
  const accQ = host.accounts.find((a) => a.usage && Array.isArray(a.usage.windows) && a.usage.windows.length) || host.accounts[0];
  const usage0 = JSON.parse(JSON.stringify(accQ.usage));
  accQ.usage = Object.assign({}, accQ.usage, { windows: accQ.usage.windows.map((w, i) => (i === 0 ? Object.assign({}, w, { key: 'session', label: '5 小時', percent: 100, severity: 'max', resetsAt: Date.now() + 2 * 3600e3 }) : w)) });
  host.pushAccounts();
  const ql = await until(() => page.evaluate((id) => {
    const card = document.querySelector('.acard[data-key="' + id + '"]');
    const col = card && card.querySelector('.ring-col.locked');
    if (!col) return null;
    const cd = col.querySelector('.ring-cd');
    const ring = col.querySelector('.ring');
    return { fx: col.classList.contains('fx-q-lock') || (col._fxTimers && 'fx-q-lock' in col._fxTimers), anims: ring.getAnimations().map((a) => a.animationName),
      cd: !cd.hidden, scale: new DOMMatrix(getComputedStyle(cd.firstChild).transform).a, outline: getComputedStyle(ring).outlineStyle,
      side: !!document.querySelector('.macct .mmeter.locked') };
  }, accQ.id), 2500, 20);
  await page.locator(`.acard[data-key="${accQ.id}"] .rings`).screenshot({ path: outFile('fx-quota-lock-dark.png') }).catch(() => {});
  accQ.usage = usage0;
  host.pushAccounts();
  const qUnlock = await until(() => page.evaluate((id) => !document.querySelector('.acard[data-key="' + id + '"] .ring-col.locked') || null, accQ.id), 2000, 20);
  check('fxQuotaLock', !!ql && ql.fx && ql.anims.includes('fx-q-lock') && ql.cd && Math.abs(ql.scale - 0.4) < 0.06 && ql.outline === 'dashed' && ql.side && !!qUnlock, { ql, qUnlock });

  // 健康度的分格：每個檢查項目一格、依等級上色；分析做完（running 由 true 變 false）時由左往右亮一次
  await host.send({ type: 'health', health: makeHealth({ running: true }) });
  await sleep(150);
  await host.send({ type: 'health', health: makeHealth() });
  const ah = await until(() => page.evaluate(() => {
    const b = document.querySelector('.acard[data-key="a1"] .acard-health');
    if (!b || b.hidden) return null;
    const cells = Array.from(b.querySelectorAll('.ah-segs > i'));
    return { levels: cells.map((c) => c.dataset.level), scan: b.classList.contains('fx-ah-scan'),
      anims: [...new Set(cells.flatMap((c) => c.getAnimations().map((a) => a.animationName)))] };
  }), 2000, 15);
  await page.locator('.acard[data-key="a1"] .acard-health').screenshot({ path: outFile('fx-health-segs-dark.png') }).catch(() => {});
  check('fxHealthSegs', !!ah && ah.levels.join(',') === 'warn,warn,warn,ok,info,ok' && ah.scan && ah.anims.includes('fx-ah-cell'), ah);

  // 滑鼠探照光：滑過帳戶卡，一圈光暈跟著滑鼠（只以 transform 移動）；離開卡片就熄
  const cardBox = await page.locator('.acard[data-key="a1"]').boundingBox();
  await page.mouse.move(cardBox.x + 60, cardBox.y + 50);
  const sp1 = await until(() => page.evaluate(() => {
    const c = document.querySelector('.acard[data-key="a1"]');
    const s = c && c.querySelector('.fx-spot > i');
    return c && c.classList.contains('fx-spot-on') && s ? new DOMMatrix(getComputedStyle(s).transform) : null;
  }).then((m) => (m ? { x: m.e, y: m.f } : null)), 1500, 15);
  await page.mouse.move(cardBox.x + 160, cardBox.y + 90);
  const sp2 = await until(() => page.evaluate(() => {
    const m = new DOMMatrix(getComputedStyle(document.querySelector('.acard[data-key="a1"] .fx-spot > i')).transform);
    return { x: m.e, y: m.f };
  }).then((p) => (p && Math.abs(p.x - 160) < 3 ? p : null)), 1500, 15);
  await page.screenshot({ path: outFile('fx-spotlight-dark.png'), clip: { x: cardBox.x, y: cardBox.y, width: cardBox.width, height: Math.min(cardBox.height, 260) } }).catch(() => {});
  await page.mouse.move(2, 2);
  const spOff = await until(() => page.evaluate(() => !document.querySelector('.acard[data-key="a1"]').classList.contains('fx-spot-on') || null), 1500, 15);
  check('fxSpotlight', !!sp1 && Math.abs(sp1.x - 60) < 3 && Math.abs(sp1.y - 50) < 3 && !!sp2 && Math.abs(sp2.y - 90) < 3 && !!spOff, { sp1, sp2, spOff });
  // ───── 第六批（主要 app，儀表板與頂部列）：MCP 燈列、下載新版的讀數 ─────
  // MCP：每個伺服器一顆燈（依狀態上色），從連不上變成已連線的那一顆閃一下綠
  await host.send({ type: 'mcp', mcp: makeMcp() });
  const mcpLedSt = await until(() => page.evaluate(() => {
    const b = document.querySelector('.acard[data-key="a1"] .acard-mcp');
    if (!b || b.hidden) return null;
    const ls = [...b.querySelectorAll('.mcp-leds > i')];
    return ls.length ? ls.map((i) => i.dataset.st) : null;
  }), 2500, 20);
  const mcpFixed = makeMcp();
  mcpFixed.accounts.a1.servers = mcpFixed.accounts.a1.servers.map((x) => (x.name === 'broken' ? Object.assign({}, x, { status: 'connected', error: null }) : x));
  await host.send({ type: 'mcp', mcp: mcpFixed });
  const mcpLedOk = await until(() => page.evaluate(() => {
    const i = [...document.querySelectorAll('.acard[data-key="a1"] .mcp-leds > i')].find((x) => x.dataset.name === 'broken');
    return i && i.dataset.st === 'connected' && i.classList.contains('fx-led-ok') ? i.getAnimations().map((a) => a.animationName) : null;
  }), 2000, 15);
  await page.locator('.acard[data-key="a1"] .acard-mcp').screenshot({ path: outFile('fx-mcp-leds-dark.png') }).catch(() => {});
  check('fxMcpLeds', !!mcpLedSt && mcpLedSt.join(',') === 'connected,needs-auth,connected,failed,connected' && !!mcpLedOk && mcpLedOk.includes('fx-led-ok'), { mcpLedSt, mcpLedOk });

  // 下載新版：版號旁寫已下載的 MB 與速度，每收到進度燈閃一下；不在下載時收起
  const dl = (bytes) => host.send({ type: 'appUpdate', state: Object.assign(F.appUpdate('downloading'), { downloaded: bytes }) });
  await dl(10 * 1048576);
  await sleep(600);
  await dl(12 * 1048576);
  const dlChip = await until(() => page.evaluate(() => {
    const c = document.getElementById('appDl');
    return c && !c.hidden && /MB\/s|KB\/s/.test(c.textContent) ? { text: c.textContent, led: c.firstChild.classList.contains('fx-dl') || c.firstChild.getAnimations().length > 0 } : null;
  }), 1500, 15);
  await page.locator('.brand').screenshot({ path: outFile('fx-download-chip-dark.png') }).catch(() => {});
  await host.send({ type: 'appUpdate', state: F.appUpdate('idle') });
  const chipGone = await until(() => page.evaluate(() => document.getElementById('appDl').hidden || null), 1500, 15);
  check('fxDownloadChip', !!dlChip && /^⇣ 12\.0 MB・\d/.test(dlChip.text) && dlChip.led && !!chipGone, { dlChip, chipGone });

  // ───── 第七批（主要 app，儀表板）：用量燃燒預估 ─────
  // 5 小時窗過了 3 小時、用了 70%：照目前速度約 1 小時 17 分後用完、比重置早，圓環下寫預估、剩下的部分是虛線；來得及重置時不顯示
  const accB = host.accounts.find((a) => a.usage && Array.isArray(a.usage.windows) && a.usage.windows.length) || host.accounts[0];
  const usageB = JSON.parse(JSON.stringify(accB.usage));
  accB.usage = Object.assign({}, accB.usage, { windows: accB.usage.windows.map((w, i) => (i === 0 ? Object.assign({}, w, { key: 'session', label: '5 小時', percent: 70, severity: 'high', resetsAt: Date.now() + 2 * 3600e3 }) : w)) });
  host.pushAccounts();
  const burn = await until(() => page.evaluate((id) => {
    const col = document.querySelector('.acard[data-key="' + id + '"] .ring-col');
    if (!col || !col.classList.contains('burning')) return null;
    const note = col.querySelector('.ring-burn-note');
    const ring = col.querySelector('.ring-burn');
    return { text: note.textContent, hidden: note.hidden, color: getComputedStyle(note).color, vis: ring.getAttribute('visibility'), dash: getComputedStyle(ring).strokeDasharray,
      fits: note.scrollWidth <= note.clientWidth + 1, title: note.title };
  }, accB.id), 2500, 20);
  await page.locator(`.acard[data-key="${accB.id}"] .rings`).screenshot({ path: outFile('fx-burn-dark.png') }).catch(() => {});
  accB.usage = Object.assign({}, accB.usage, { windows: accB.usage.windows.map((w, i) => (i === 0 ? Object.assign({}, w, { percent: 30, severity: 'ok' }) : w)) });
  host.pushAccounts();
  const burnOff = await until(() => page.evaluate((id) => {
    const col = document.querySelector('.acard[data-key="' + id + '"] .ring-col');
    return col && !col.classList.contains('burning') && col.querySelector('.ring-burn-note').hidden && col.querySelector('.ring-burn').getAttribute('visibility') === 'hidden' ? true : null;
  }, accB.id), 2000, 20);
  accB.usage = usageB;
  host.pushAccounts();
  const amber = await cssColor('--amber');
  check('fxBurnForecast', !!burn && /^1 小時 1[5-9] 分後用完$/.test(burn.text) && burn.fits && /^照目前速度/.test(burn.title) && !burn.hidden && burn.color === amber && burn.vis === 'visible' && burn.dash !== 'none' && !!burnOff, { burn, burnOff, amber });
  await closeApp(app);

  // ───── 第七批（兩個窗格）：分頁拖放——經過的窗格是拖著的分頁的帳戶色角框、分頁列上一排發光的小方塊標出插入位置、放下時角框收合一次 ─────
  const dropApp = await open('fx-tabdrop', { layout: { panes: [{ id: 'p1', tabs: ['c1', 'c2'], active: 'c1' }, { id: 'p2', tabs: ['c3'], active: 'c3' }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' } });
  const dp = dropApp.page;
  const tabAcc = await dp.evaluate(() => getComputedStyle(document.querySelector('.tab[data-key="c2"]')).getPropertyValue('--acc').trim());
  // Playwright 的滑鼠拖曳在途中看不到 dragover 的狀態：以 DragEvent 依序派送（同一個 DataTransfer）
  const fire = (type, selector, at) => dp.evaluate(([t, sel, pos]) => {
    const el = document.querySelector(sel);
    const r = el.getBoundingClientRect();
    const x = pos === 'right' ? r.right - 4 : r.left + r.width / 2;
    const ev = new DragEvent(t, { dataTransfer: window.__dt, bubbles: true, cancelable: true, clientX: x, clientY: r.top + r.height / 2 });
    el.dispatchEvent(ev);
  }, [type, selector, at || 'mid']);
  await dp.evaluate(() => { window.__dt = new DataTransfer(); });
  await fire('dragstart', '.tab[data-key="c2"]');
  await fire('dragover', '.pane[data-pane="p2"] .pane-body');
  const overBody = await until(() => dp.evaluate(() => {
    const p = document.querySelector('.pane[data-pane="p2"].tab-target');
    if (!p) return null;
    const a = getComputedStyle(p.querySelector('.pane-body'), '::after');
    // --drag-acc 只設給用到它的 ::after 與插入記號（workspace.js 的 setDragAcc；2026-10-25 起不設在 body 上：自訂屬性會繼承，整份文件都要重算樣式）
    return { img: a.backgroundImage, acc: a.getPropertyValue('--drag-acc').trim(), onBody: getComputedStyle(document.body).getPropertyValue('--drag-acc').trim() };
  }), 1500, 15);
  await dp.screenshot({ path: outFile('fx-tab-drop-dark.png') }).catch(() => {});
  await fire('dragover', '.tab[data-key="c3"]', 'right');
  const overStrip = await until(() => dp.evaluate(() => {
    const strip = document.querySelector('.pane[data-pane="p2"] .strip.ins-on');
    const m = strip && strip.querySelector('.tab-ins');
    if (!m) return null;
    const t = document.querySelector('.tab[data-key="c3"]').getBoundingClientRect();
    const r = m.getBoundingClientRect();
    return { shown: getComputedStyle(m).display !== 'none', x: Math.round(r.left), tabRight: Math.round(t.right), bodyTarget: !!document.querySelector('.pane[data-pane="p2"].tab-target') };
  }), 1500, 15);
  await dp.locator('.pane[data-pane="p2"] .strip').screenshot({ path: outFile('fx-tab-ins-dark.png') }).catch(() => {});
  await fire('dragover', '.pane[data-pane="p2"] .pane-body');
  await dp.evaluate(() => {
    window.__drop = null;
    const p = document.querySelector('.pane[data-pane="p2"]');
    const mo = new MutationObserver(() => {
      if (p.classList.contains('fx-dropped') && !window.__drop) window.__drop = document.getAnimations().filter((a) => a.effect && a.effect.pseudoElement === '::after' && a.effect.target.closest('.pane') === p).map((a) => a.animationName);
    });
    mo.observe(p, { attributes: true, attributeFilter: ['class'] });
  });
  await fire('drop', '.pane[data-pane="p2"] .pane-body');
  await fire('dragend', '.tab[data-key="c2"]');
  const dropped = await until(() => dp.evaluate(() => window.__drop), 1500, 10);
  const moved = await until(() => dp.evaluate(() => !!document.querySelector('.pane[data-pane="p2"] .tab[data-key="c2"]') || null), 1500, 15);
  check('fxTabDrop', !!overBody && overBody.acc === tabAcc && overBody.onBody === '' && /linear-gradient/.test(overBody.img) && !!overStrip && overStrip.shown && Math.abs(overStrip.x - (overStrip.tabRight - 2)) <= 2 && !overStrip.bodyTarget
    && !!dropped && dropped.includes('fx-drop-close') && !!moved, { tabAcc, overBody, overStrip, dropped, moved });
  await closeApp(dropApp);

  // ───── 分頁與窗格：切到另一個分頁時內容淡入；多窗格時點另一格，那一格的角框閃一下 ─────
  const tabsApp = await open('fx-tabs', { layout: { panes: [{ id: 'p1', tabs: ['c1', 'c2'], active: 'c1' }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' }, initScript: bootWatch });
  await tabsApp.page.click('.tab[data-key="c2"]');
  const tabShow = await until(() => tabsApp.page.evaluate(() => {
    const c = document.querySelector('.conv[data-conv="c2"]');
    return !!c && !c.hidden && c.classList.contains('fx-show') && c.getAnimations().some((a) => a.animationName === 'fx-show');
  }), 1500, 10);
  check('fxTabShow', !!tabShow);
  // ───── 第四批（雙分頁的 app）：背景分頁一輪結束的閃燈、分頁徽章彈出、對話框的角框、分段選擇滑動、「繼續」鈕擴散、開機掃描 ─────
  // 開機掃描：第一則 init 時 #app 加上 fx-boot（初始化腳本在頁面一開始就盯著，記下有沒有出現與當時的動畫）
  const boot = await tabsApp.page.evaluate(() => window.__fxBoot || null);
  check('fxBootScan', !!boot && boot.seen && boot.anims.includes('fx-boot-scan') && boot.anims.includes('fx-fade-out') && boot.cleared, boot);
  const tp = tabsApp.page, th = tabsApp.host;
  // 現在 c2 在前面、c1 在背景：c1 一輪結束，c1 的分頁底部閃一下綠
  th.conv('c1').header = Object.assign({}, th.conv('c1').header, { running: true, status: '執行中', startedAt: Date.now() });
  await th.pushHeader('c1');
  await sleep(300);
  th.conv('c1').header = Object.assign({}, th.conv('c1').header, { running: false, status: null, startedAt: null });
  await th.pushHeader('c1');
  const tabFlash = await until(() => tp.evaluate(() => {
    const t = document.querySelector('.tab[data-key="c1"]');
    if (!t || !t.classList.contains('fx-tab-ok')) return null;
    const dot = t.querySelector('.tab-dot');
    return document.getAnimations().filter((a) => a.effect && a.effect.target === dot && a.effect.pseudoElement === '::after').map((a) => a.animationName);
  }), 2000, 15);
  const c2NoFlash = await tp.evaluate(() => !document.querySelector('.tab[data-key="c2"]').classList.contains('fx-tab-ok'));
  check('fxTabTurnEnd', !!tabFlash && tabFlash.includes('fx-turn-end') && c2NoFlash, { tabFlash, c2NoFlash });
  // 分頁出現「!」（等你回應）：彈出一下。c1 的 fixture 本來就有等你作答的卡片（一開始就有「!」），改用沒有的 c2
  const c2Row = th.conversations.find((x) => x.id === 'c2');
  const c2Before = await tp.evaluate(() => !!document.querySelector('.tab[data-key="c2"] .badge-wait'));
  c2Row.waiting = true;
  th.pushConversations();
  const tabBadge = await until(() => tp.evaluate(() => { const b = document.querySelector('.tab[data-key="c2"] .badge-wait'); return b && b.classList.contains('fx-badge-in') ? true : null; }), 1500, 10);
  c2Row.waiting = false;
  th.pushConversations();
  check('fxTabBadgePop', !c2Before && !!tabBadge, { c2Before, tabBadge });
  // 「繼續」鈕：按下時擴散一圈（同送出鈕）
  await tp.click('.conv[data-conv="c2"] .ibtn.cont');
  const contFx = await tp.evaluate(() => {
    const b = document.querySelector('.conv[data-conv="c2"] .ibtn.cont');
    return { cls: b.classList.contains('fx-sent'), anims: document.getAnimations().filter((a) => a.effect && a.effect.target === b && a.effect.pseudoElement === '::after').map((a) => a.animationName) };
  });
  check('fxContinueRipple', contFx.cls && contFx.anims.includes('fx-ring-out'), contFx);
  // 對話框打開：角框從外面收進來、閃兩下；設定裡的分段選擇點了另一顆，底色滑過去（暫時的 .seg-slide），滑完移除
  await tp.click('#btnSettings');
  const dlg = await until(() => tp.evaluate(() => {
    const d = document.querySelector('.dlg');
    return d ? document.getAnimations().filter((a) => a.effect && a.effect.target === d && a.effect.pseudoElement === '::after').map((a) => a.animationName) : null;
  }), 1500, 10);
  const segTarget = await tp.evaluate(() => {
    const seg = Array.from(document.querySelectorAll('.dlg .seg')).find((s) => s.querySelectorAll('.seg-btn').length >= 2 && s.getBoundingClientRect().width > 0);
    if (!seg) return null;
    const b = Array.from(seg.querySelectorAll('.seg-btn')).find((x) => !x.classList.contains('on'));
    seg.dataset.fxProbe = '1';
    b.dataset.fxProbe = '1';
    return true;
  });
  let segFx = null, segGone = null;
  if (segTarget) {
    await tp.click('.seg-btn[data-fx-probe="1"]');
    segFx = await tp.evaluate(() => {
      const seg = document.querySelector('.seg[data-fx-probe="1"]');
      const g = seg.querySelector('.seg-slide');
      return { slide: !!g, moving: seg.classList.contains('seg-moving'), anims: g ? g.getAnimations().length : 0 };
    });
    segGone = await until(() => tp.evaluate(() => { const seg = document.querySelector('.seg[data-fx-probe="1"]'); return !seg.querySelector('.seg-slide') && !seg.classList.contains('seg-moving') ? true : null; }), 1500, 15);
  }
  await tp.keyboard.press('Escape');
  check('fxDialogAndSeg', !!dlg && dlg.includes('fx-lock-in-far') && dlg.includes('fx-lock-blink') && !!segTarget && !!segFx && segFx.slide && segFx.moving && segFx.anims > 0 && !!segGone,
    { dlg, segTarget, segFx, segGone });
  await closeApp(tabsApp);

  // ───── 四窗格（1100 寬）：窄的對話欄裡刻度尺貼在留白裡、不壓到項目 ─────
  const grid = await openApp('fx-grid', { vp: { w: 1100, h: 700 }, layout: FOUR_PANES });
  await grid.page.waitForSelector(`${SEL}:not(.loading) .items`, { state: 'attached', timeout: 8000 });
  await settle(grid.page, 300);
  const g4 = [];
  for (const id of ['c1', 'c3', 'c4', 'c7']) g4.push(await grid.page.evaluate(rulerGeometry, id));
  check('fxRulersFourPanes', g4.every(rulersClear), g4);
  // 點另一格（c3 在 p2）：焦點換過去，那一格四角的焦點色角框閃一下
  await grid.page.click('.conv[data-conv="c3"] .scroller');
  const paneFx = await until(() => grid.page.evaluate(() => {
    const p = document.querySelector('.conv[data-conv="c3"]').closest('.pane');
    const a = document.getAnimations().find((x) => x.effect && x.effect.target === p && x.effect.pseudoElement === '::after');
    return p.classList.contains('focused') && p.classList.contains('fx-focus') && a ? a.animationName : null;
  }), 1500, 10);
  check('fxPaneFocus', paneFx === 'fx-flash-out', paneFx);
  await closeApp(grid);

  // ───── 系統要求減少動態、設定關掉動態效果：不播、不解碼 ─────
  for (const [tag, o] of [['Reduced', { reducedMotion: 'reduce' }], ['Off', { settings: { visualEffects: false } }]]) {
    const a = await open('fx-' + tag.toLowerCase(), o);
    await a.host.pushItem(C, item('fxr1'));
    const r1 = `${SEL} .it[data-id="fxr1"]`;
    await until(() => a.page.$(r1), 2000);
    await sleep(60);
    const running = async (s, p) => ((await anims(a.page, s, p)) || []).filter((x) => x.state === 'running').map((x) => x.name);
    const entrance = await running(r1, null);
    a.host.conv(C).header.running = false;
    await a.host.pushHeader(C);
    await sleep(120);
    const turn = await running(`${SEL} .cbox`, '::before');
    Object.assign(a.host.conv(C).header, { lastTurn: '輸入 13,579・輸出 2,468 tokens' });
    await a.host.pushHeader(C);
    const scr = await until(() => a.page.evaluate((s) => !!document.querySelector(s + ' .ci-turn .mtok-c.scr'), SEL), 400, 10);
    const text = await textOf(a.page, `${SEL} .ci-turn`);
    await a.page.fill(`${SEL} .cbox textarea`, '不該有幽靈文字');
    await a.page.click(`${SEL} .cbtn.send`);
    const ghost = await a.page.evaluate((s) => !!document.querySelector(s + ' .send-ghost'), SEL);
    // 工具做完、思考結束時內容不掃過
    await a.host.pushItem(C, tool('fxrp', 'Running'));
    await a.host.pushItem(C, item('fxrk', { kind: 'Thinking', status: 'Running', html: '<p>想…</p>' }));
    await until(() => a.page.$(`${SEL} .it[data-id="fxrk"]`), 2000);
    await a.host.pushItem(C, tool('fxrp', 'Done', { output: 'a\nb\nc\nd\ne\nf\ng' }));
    await sleep(150);
    await a.host.pushItem(C, item('fxrk', { kind: 'Thinking', status: 'Done', html: '<p>一</p><p>二</p><p>三</p><p>四</p><p>五</p><p>六</p>' }));
    await sleep(150);
    const peek = await a.page.evaluate((s) => document.querySelectorAll(s + ' .it[data-id^="fxr"] .t-peek').length > 0, SEL);
    check('fx' + tag + 'NoOneShot', entrance.length === 0 && turn.length === 0 && !scr && text === '↑13.57K↓2,468' && !ghost && !peek, { entrance, turn, scr, text, ghost, peek });
    // 燈號照樣連動真的數字，但亮久一點（0.9 秒）、連續的動靜看起來一直亮著，不閃
    await sendNow(a.page, [{ type: 'activity', ms: 250, conversations: [{ id: C, data: 100, disk: 0 }] }]);
    await sleep(450);
    const calmOn = await a.page.evaluate((s) => document.querySelector(s + ' .ci-led[data-led="data"]').classList.contains('on'), SEL);
    check('fx' + tag + 'LedsSteady', calmOn, { calmOn });
    // 自動命名的新標題直接換（不解碼）、通知沒有倒數條、側欄新出現的列不播
    const ac = a.host.conversations.find((x) => x.id === C);
    Object.assign(a.host.conv(C).header, { titling: true });
    ac.titling = true;
    await sendNow(a.page, [{ type: 'header', conversationId: C, header: a.host.conv(C).header }, { type: 'conversations', items: a.host.conversations }]);
    await sleep(120);
    Object.assign(a.host.conv(C).header, { titling: false, title: '不解碼的標題' });
    Object.assign(ac, { titling: false, title: '不解碼的標題' });
    a.host.conversations.unshift(Object.assign({}, ac, { id: 'cfxcalm', title: '不播進場的新對話' }));
    await sendNow(a.page, [{ type: 'header', conversationId: C, header: a.host.conv(C).header }, { type: 'conversations', items: a.host.conversations }]);
    await a.host.send({ type: 'toast', text: '沒有倒數條', level: 'info' });
    await until(() => a.page.$('.toasts > .toast'), 1500);
    await until(() => a.page.evaluate((s) => document.querySelector(s + ' .ch-title-run').textContent === '不解碼的標題' || null, SEL), 1500, 5); // 標頭下一個影格才套用
    const calm = await a.page.evaluate((s) => ({
      head: document.querySelector(s + ' .ch-title-run').textContent, decoding: document.querySelectorAll('.tdx-c').length,
      bar: !!document.querySelector('.toast .toast-bar'),
      row: (() => { const r = document.querySelector('.crow[data-key="c:cfxcalm"]'); return r ? r.getAnimations().filter((x) => x.playState === 'running').map((x) => x.animationName) : null; })(),
    }), SEL);
    check('fx' + tag + 'NoDecodeNoBar', calm.head === '不解碼的標題' && calm.decoding === 0 && !calm.bar && !!calm.row && calm.row.length === 0, calm);
    // 帳戶卡的數字不解碼、徽章不彈出；打開儀表板時走勢線不播
    const ca = a.host.accounts[0];
    const cs0 = Object.assign({}, ca.sessions || {});
    ca.sessions = Object.assign({}, cs0, { running: Number(cs0.running || 0) + 1, programs: 1 });
    a.host.pushAccounts();
    await sleep(120);
    const calmCard = await a.page.evaluate((k) => {
      const card = document.querySelector('#sidebar .macct[data-key="' + k + '"]');
      const prog = card.querySelector('.macct-prog');
      return { scr: !!card.querySelector('.macct-run-n .mtok-c.scr'), pop: prog.getAnimations().filter((x) => x.playState === 'running').map((x) => x.animationName) };
    }, ca.id);
    await a.host.send({ type: 'showView', view: 'dashboard' });
    await sleep(150);
    const calmSpark = await a.page.evaluate(() => Array.from(document.querySelectorAll('.spark-wipe, .spark-svg')).flatMap((e) => e.getAnimations().filter((x) => x.playState === 'running').map((x) => x.animationName)));
    check('fx' + tag + 'NoNewOneShots', !calmCard.scr && calmCard.pop.length === 0 && calmSpark.length === 0, { calmCard, calmSpark });
    await closeApp(a);
  }

  // ───── 淺色：一輪結束的綠燈與卡片的鎖定（動畫停在固定的時間點截圖） ─────
  const light = await open('fx-light', { settings: { theme: 'light' }, colorScheme: 'light' });
  light.host.conv(C).header.running = false;
  await light.host.pushHeader(C);
  const lightTurn = await until(() => hasClass(light.page, SEL, 'fx-turn-ok'), 1500, 15);
  await freeze(light.page, SEL, ['fx-turn-ok', 'fx-cool'], { 'fx-turn-end': 0 });
  await light.page.locator(`${SEL} .dock`).screenshot({ path: outFile('fx-turn-light.png') });
  await thaw(light.page, SEL, ['fx-turn-ok', 'fx-cool']);
  const lqs = await pushCard(light.page, light.host, 'fxq3');
  await light.page.evaluate((s) => document.querySelector(s).scrollIntoView({ block: 'center' }), lqs);
  await freeze(light.page, lqs, ['fx-in'], CARD_AT);
  await light.page.locator(lqs).screenshot({ path: outFile('fx-card-light.png') });
  await thaw(light.page, lqs, ['fx-in']);
  check('fxShotsFrozen', !!lightTurn && shots.length === 7 && shots.every((x) => x.want.every((n) => x.frozen.includes(n))), { lightTurn, shots });
  // 淺色的語法上色：12 種類別的字對程式碼框底色的對比都 ≥ 4.5
  const lightContrast = await kindContrast(light.page, light.host);
  check('fxSyntaxContrastLight', Object.keys(lightContrast).length === 12 && Object.values(lightContrast).every((r) => r >= 4.5), lightContrast);
  // 淺色主題的帳戶卡今日 token（fxTokenAccountHue 的淺色版）：同色相；out 最深（對比 ≥ 7）、in 淡一階（in 的相對亮度至少是 out 的 1.3 倍、對比 ≥ 3.5），in 的標籤也淡一階
  const lightPaneBg = await light.page.evaluate(() => { const i = document.createElement('i'); i.style.color = 'var(--bg-pane)'; document.body.append(i); const c = getComputedStyle(i).color; i.remove(); return c; });
  const lightTok = shadeRows(await tokShades(light.page), lightPaneBg);
  check('fxTokenAccountHueLight', lightTok.length >= 3 && new Set(lightTok.map((r) => r.tout)).size >= 3
    && lightTok.every((r) => r.inMarked && sameHue(r) && r.lumIn >= r.lumOut * 1.3 && r.tagLumIn > r.tagLumOut && r.crOut >= 7 && r.crIn >= 3.5), { lightPaneBg, lightTok });
  await light.page.locator('.sidebar .sb-sec-accts').screenshot({ path: outFile('fx-token-shades-light.png') }).catch(() => {});
  await light.page.evaluate((s) => document.querySelector(s + ' .it[data-id="i12"] .code pre code [class^="tk-"]') || null, SEL);
  await light.page.evaluate((s) => document.querySelector(s + ' .it[data-id="i12"] .code').scrollIntoView({ block: 'center' }), SEL);
  await light.page.locator(`${SEL} .it[data-id="i12"] .code`).screenshot({ path: outFile('fx-syntax-light.png') });
  // 淺色的輸入框下方那一行（燈號亮著時）
  await sendNow(light.page, [{ type: 'activity', ms: 250, conversations: [{ id: C, data: 5_000, disk: 8_192 }], net: { rx: 4_096, tx: 1_024 } }]);
  await light.page.locator(`${SEL} .cinfo`).screenshot({ path: outFile('fx-infoline-light.png') });
  // 淺色的回覆內容與通知（滑過時倒數條停住）
  await light.host.pushItem(C, item('fxmdl', { html: '<ul><li>一</li><li>二</li></ul><ol><li>甲</li></ol><blockquote><p>引用</p></blockquote>'
    + '<table><thead><tr><th>名稱</th><th>數量</th></tr></thead><tbody><tr><td>a</td><td>12</td></tr></tbody></table>' }));
  const mdl = `${SEL} .it[data-id="fxmdl"]`;
  await until(() => light.page.$(mdl + ' td'), 2000);
  await light.page.evaluate((s) => document.querySelector(s).scrollIntoView({ block: 'center' }), mdl);
  await light.page.locator(mdl).screenshot({ path: outFile('fx-markdown-light.png') });
  await light.host.send({ type: 'toast', text: '效果探針的通知', level: 'error' });
  await until(() => light.page.$('.toasts > .toast:last-child'), 1500);
  await light.page.hover('.toasts > .toast:last-child');
  await light.page.locator('.toasts > .toast:last-child').screenshot({ path: outFile('fx-toast-light.png') });
  await closeApp(light);
}
