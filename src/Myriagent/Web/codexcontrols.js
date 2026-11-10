import { h } from './util.js';
import { post, onHostMessage } from './store.js';
import { openMenu, openDialog, toast, spinner } from './ui.js';
import { goalSummary, speedOptions, reviewCommand } from './codexrules.js';

const pending = new Map();
let serial = 0;
let opened = null;
onHostMessage(m => {
  if (!m || m.type !== 'codexControl') return;
  const p = pending.get(m.requestId);
  if (!p) return;
  pending.delete(m.requestId);
  clearTimeout(p.timer);
  if (m.error) p.reject(new Error(m.error)); else p.resolve(m.result || {});
});

function request(composer, action, args = {}) {
  return new Promise((resolve, reject) => {
    const requestId = 'codex-' + Date.now() + '-' + ++serial;
    const timer = setTimeout(() => { pending.delete(requestId); reject(new Error('Codex 操作尚未回覆，請稍後重新整理')); }, 90000);
    pending.set(requestId, { resolve, reject, timer });
    post({ cmd: 'codexControl', conversationId: composer.conv.id, requestId, action, ...args });
  });
}

function sendOperation(composer, text) {
  if (composer.runningNow()) { toast('請等這一輪結束後再執行', 'error'); return false; }
  if (composer.sendGate()) return false;
  if (composer.state.attachments && composer.state.attachments.length || composer.attachedUploads().length) {
    toast('審查與壓縮不接受附件，請先移除附件', 'error'); return false;
  }
  // 與「繼續」鈕相同：不清掉使用者正在撰寫的草稿。
  if (!composer.dispatch(text, [])) return false;
  composer.conv.stickToBottom();
  return true;
}

export function openCodexMenu(composer, anchor) {
  const cx = composer.header.codex || {};
  openMenu(anchor, [
    { label: '規劃模式…', desc: cx.mode === 'plan' ? '目前先規劃，確認後再執行' : '一般執行', disabled: composer.runningNow(), onSelect: () => openCodexPanel(composer, 'mode') },
    { label: '開始執行計畫', disabled: composer.runningNow() || cx.mode !== 'plan' || !cx.hasPlan,
      onSelect: async () => {
        if (composer.sendGate()) return;
        if (composer.state.attachments && composer.state.attachments.length || composer.attachedUploads().length) { toast('請先移除附件，再開始執行計畫', 'error'); return; }
        try {
          await request(composer, 'mode', { mode: 'default' });
          composer.header.codex = { ...composer.header.codex, mode: 'default', busy: false };
          sendOperation(composer, '請依照上方已確認的計畫開始執行。');
        } catch (ex) { toast(ex.message, 'error'); }
      } },
    { type: 'sep' },
    { label: '持續目標…', desc: goalSummary(cx.goal), onSelect: () => openCodexPanel(composer, 'goal') },
    { label: '指定技能…', desc: (cx.selectedSkills || []).map(s => s.name).join('、') || '搜尋這個工作資料夾可用的技能', onSelect: () => openCodexPanel(composer, 'skills') },
    { label: '速度模式…', desc: cx.serviceTier ? (cx.serviceTier === 'default' ? '標準' : '已自訂速度') : '沿用 CLI 設定', onSelect: () => openCodexPanel(composer, 'speed') },
    { type: 'sep' },
    { label: '程式碼審查…', disabled: composer.runningNow(), onSelect: () => openReview(composer) },
    { label: '壓縮目前對話', desc: '整理較早的內容，釋放上下文空間', disabled: composer.runningNow() || !composer.header.sessionStarted,
      onSelect: () => sendOperation(composer, '/compact') },
    { type: 'sep' },
    { type: 'header', label: '子代理的任務與過程會顯示在對話的「子代理」卡片' },
  ], { placement: 'top-start', minWidth: 290, restoreFocus: composer.ta });
}

export function refreshCodexPanel(composer) {
  if (opened && opened.id === composer.conv.id) opened.refresh(composer.header);
}

export function openCodexPanel(composer, section) {
  const names = { goal: '持續目標', skills: '指定技能', speed: '速度模式', mode: '規劃模式' };
  const content = h('div', { class: 'codex-content' }, spinner(), h('span', { text: '讀取 Codex 狀態…' }));
  const error = h('p', { class: 'codex-error', role: 'alert', hidden: true });
  const body = h('div', { class: 'codex-panel' }, content, error);
  const close = h('button', { type: 'button', class: 'btn', text: '關閉' });
  const reload = h('button', { type: 'button', class: 'btn ghost', text: '重新整理' });
  let closed = false;
  let refresh = () => {};
  const d = openDialog({ title: 'Codex · ' + names[section], body, footer: [reload, h('span', { class: 'grow' }), close], width: 620,
    onClose: () => { closed = true; if (opened && opened.dialog === d) opened = null; } });
  close.addEventListener('click', () => d.close());
  opened = { id: composer.conv.id, dialog: d, refresh: hd => refresh(hd) };
  async function act(button, action, args) {
    button.disabled = true;
    error.hidden = true;
    try { const result = await request(composer, action, args); toast('已更新 Codex 設定'); return result; }
    catch (ex) { error.textContent = ex.message; error.hidden = false; return null; }
    finally { button.disabled = false; refresh(composer.header); }
  }
  async function load() {
    reload.disabled = true;
    error.hidden = true;
    try {
      const data = await request(composer, 'inspect');
      if (closed) return;
      content.replaceChildren();
      if (section === 'mode') {
        if (data.modesError) throw new Error(data.modesError);
        const available = new Set((data.modes || []).map(m => m.mode));
        const select = h('select', { class: 'input', 'aria-label': '規劃模式' });
        for (const [value, text] of [['default', '一般執行'], ['plan', '先規劃']])
          if (available.has(value)) select.append(h('option', { value, text }));
        select.value = (composer.header.codex || {}).mode || 'default';
        const apply = h('button', { type: 'button', class: 'btn primary', text: '套用模式', disabled: !select.options.length });
        apply.addEventListener('click', async () => {
          const result = await act(apply, 'mode', { mode: select.value });
          if (result) composer.header.codex.mode = result.mode;
        });
        content.append(h('label', { class: 'codex-field' }, h('span', { text: '下一輪的工作方式' }), select),
          h('p', { class: 'codex-note', text: '先規劃會讓 Codex 調查需求並提出計畫。確認內容後，從 Codex 工具選「開始執行計畫」；也可以繼續送訊息調整計畫。' }),
          h('div', { class: 'codex-actions' }, apply));
      }
      if (section === 'goal') {
        if (data.goalError) throw new Error(data.goalError);
        const goal = Object.hasOwn(data, 'goal') ? data.goal : (composer.header.codex || {}).goal;
        const objective = h('textarea', { class: 'input codex-objective', rows: 4, maxLength: 4000, placeholder: '例如：完成訂單匯出功能，並讓建置與測試通過' });
        objective.value = goal ? goal.objective || '' : '';
        const budget = h('input', { class: 'input', type: 'number', min: 1, step: 1, placeholder: '留空沿用既有預算；新目標無上限' });
        if (goal && goal.tokenBudget != null) budget.value = goal.tokenBudget;
        const status = h('p', { class: 'codex-status', role: 'status', text: goalSummary(goal) });
        const current = h('p', { class: 'codex-current', text: goal ? goal.objective : '' });
        const set = h('button', { type: 'button', class: 'btn primary', text: '設定並開始' });
        const pause = h('button', { type: 'button', class: 'btn', text: '暫停目標' });
        const resume = h('button', { type: 'button', class: 'btn', text: '繼續目標' });
        const clear = h('button', { type: 'button', class: 'btn ghost', text: '清除目標' });
        let lastGoal = goal;
        refresh = hd => {
          if (hd.codex && Object.hasOwn(hd.codex, 'goal')) lastGoal = hd.codex.goal;
          status.textContent = goalSummary(lastGoal);
          current.textContent = lastGoal ? lastGoal.objective || '' : '';
          const busy = !!(hd.codex && hd.codex.busy);
          set.disabled = busy || hd.running || !hd.sessionStarted;
          pause.disabled = busy || !lastGoal || lastGoal.status !== 'active';
          resume.disabled = busy || hd.running || !lastGoal || ['active', 'complete'].includes(lastGoal.status);
          clear.disabled = busy || !lastGoal;
        };
        set.addEventListener('click', async () => {
          const n = budget.value.trim() ? Number(budget.value) : null;
          if (!objective.value.trim() || n != null && (!Number.isSafeInteger(n) || n <= 0)) { error.textContent = '請填目標；token 預算必須是正整數'; error.hidden = false; return; }
          const result = await act(set, 'goalSet', { objective: objective.value.trim(), tokenBudget: n });
          if (result && result.goal) { composer.header.codex.goal = result.goal; refresh(composer.header); }
        });
        for (const [btn, action] of [[pause, 'goalPause'], [resume, 'goalResume'], [clear, 'goalClear']])
          btn.addEventListener('click', async () => {
            const result = await act(btn, action);
            if (result) { composer.header.codex.goal = action === 'goalClear' ? null : result.goal; refresh(composer.header); }
          });
        content.append(status, current, h('label', { class: 'codex-field' }, h('span', { text: '完成條件' }), objective),
          h('label', { class: 'codex-field' }, h('span', { text: 'token 預算（選填）' }), budget),
          h('div', { class: 'codex-actions' }, set, pause, resume, clear),
          h('p', { class: 'codex-note', text: '暫停目標會中斷目前這一輪，之後不會自動續跑。清除目標只移除後續自動執行的條件；一般的停止鈕不會清除目標。' }));
        if (!composer.header.sessionStarted) content.append(h('p', { class: 'codex-note', text: '請先送出第一則訊息，再設定持續目標。' }));
        // inspect 的結果可能比上一次 header 新。
        composer.header.codex = { ...(composer.header.codex || {}), goal };
        refresh(composer.header);
      }
      if (section === 'skills') {
        if (data.skillsError) throw new Error(data.skillsError);
        const selected = new Set(((composer.header.codex || {}).selectedSkills || []).map(s => s.path));
        const skills = [...(data.skills || [])];
        // 工作資料夾或技能清單改了，已指定的項目仍能取消，避免永遠卡在送出前驗證。
        for (const s of (composer.header.codex || {}).selectedSkills || [])
          if (!skills.some(x => x.path === s.path)) skills.push({ ...s, enabled: false, description: '已不在目前工作資料夾的技能清單中' });
        const search = h('input', { class: 'input', type: 'search', placeholder: '搜尋技能名稱或說明', 'aria-label': '搜尋技能' });
        const list = h('div', { class: 'codex-skills' });
        const apply = h('button', { type: 'button', class: 'btn primary', text: '套用指定技能' });
        const render = () => {
          const q = search.value.trim().toLocaleLowerCase();
          list.replaceChildren();
          for (const s of skills) {
            if (q && !(s.name + ' ' + s.description).toLocaleLowerCase().includes(q)) continue;
            const check = h('input', { type: 'checkbox', checked: selected.has(s.path), disabled: !s.enabled && !selected.has(s.path) });
            check.addEventListener('change', () => { if (check.checked) selected.add(s.path); else selected.delete(s.path); if (!s.enabled) check.disabled = !check.checked; });
            list.append(h('label', { class: 'codex-skill', title: s.path }, check,
              h('span', {}, h('strong', { text: s.name + (s.enabled ? '' : '（已停用）') }), h('small', { text: s.description || '' }), h('small', { class: 'codex-note', text: s.path }))));
          }
          if (!list.childElementCount) list.append(h('p', { class: 'codex-note', text: q ? '沒有符合的技能' : '目前沒有可用的技能' }));
        };
        search.addEventListener('input', render);
        apply.addEventListener('click', async () => {
          const result = await act(apply, 'skillSelect', { paths: [...selected] });
          if (result) composer.header.codex.selectedSkills = result.selectedSkills;
        });
        content.append(search, list, h('div', { class: 'codex-actions' }, apply), h('p', { class: 'codex-note', text: '這個對話之後每次送出都會明確指定勾選的技能；取消勾選後套用可移除。技能仍可由代理依任務自行選用。' }));
        for (const message of data.skillErrors || []) content.append(h('p', { class: 'codex-error', text: message }));
        render();
      }
      if (section === 'speed') {
        if (data.tiersError) throw new Error(data.tiersError);
        const select = h('select', { class: 'input', 'aria-label': '速度模式' });
        const currentTier = (composer.header.codex || {}).serviceTier;
        if (!currentTier) select.append(h('option', { value: '', text: '目前沿用 CLI 設定（尚未覆寫）', disabled: true }));
        const opts = speedOptions(data.tiers);
        for (const x of opts) select.append(h('option', { value: x.id, text: x.label }));
        select.value = currentTier || '';
        const note = h('p', { class: 'codex-note' });
        const change = () => { note.textContent = (opts.find(x => x.id === select.value) || {}).description || '請選擇要套用的速度'; };
        select.addEventListener('change', change);
        const apply = h('button', { type: 'button', class: 'btn primary', text: '套用速度' });
        apply.addEventListener('click', async () => {
          if (!select.value) return;
          const result = await act(apply, 'speed', { serviceTier: select.value });
          if (result) composer.header.codex.serviceTier = result.serviceTier;
        });
        content.append(h('label', { class: 'codex-field' }, h('span', { text: '速度模式' }), select), note,
          h('div', { class: 'codex-actions' }, apply), h('p', { class: 'codex-note', text: '與推理強度分開設定，從下一輪起生效。只列出目前模型的 CLI 目錄有回報的模式。' }));
        change();
      }
    } catch (ex) { error.textContent = ex.message; error.hidden = false; }
    finally { reload.disabled = false; }
  }
  reload.addEventListener('click', load);
  load();
}

function openReview(composer) {
  const kind = h('select', { class: 'input', 'aria-label': '審查範圍' },
    ...[['uncommittedChanges', '尚未提交的修改'], ['commit', '指定 commit'], ['baseBranch', '與分支比較'], ['custom', '自訂審查說明']].map(([value, text]) => h('option', { value, text })));
  const value = h('textarea', { class: 'input', rows: 3, 'aria-label': '審查參數', hidden: true });
  const error = h('p', { class: 'codex-error', hidden: true, role: 'alert' });
  const submit = h('button', { type: 'button', class: 'btn primary', text: '開始審查' });
  const cancel = h('button', { type: 'button', class: 'btn', text: '取消' });
  const d = openDialog({ title: 'Codex · 程式碼審查', width: 540,
    body: h('div', { class: 'codex-panel' }, h('label', { class: 'codex-field' }, h('span', { text: '審查範圍' }), kind), value, error,
      h('p', { class: 'codex-note', text: '審查結果會出現在目前對話，可接著請代理修正。' })), footer: [cancel, submit] });
  kind.addEventListener('change', () => { value.hidden = kind.value === 'uncommittedChanges'; value.placeholder = kind.value === 'commit' ? 'commit 雜湊' : kind.value === 'baseBranch' ? '例如：main' : '要特別檢查什麼？'; });
  cancel.addEventListener('click', () => d.close());
  submit.addEventListener('click', () => {
    try { if (sendOperation(composer, reviewCommand(kind.value, value.value))) d.close(); }
    catch (ex) { error.textContent = ex.message; error.hidden = false; }
  });
}
