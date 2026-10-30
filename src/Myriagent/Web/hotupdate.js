// 〔熱更新〕CLI 執行中被更新後的換版狀態怎麼呈現：狀態的說法、色調與圖示（CLI 卡、對話標頭共用）、CLI 卡的彙總句、
// 更新屏障的階段、「CLI 更新後」政策、「立即套用」的確認。狀態全由宿主產生（docs/web-protocol.md 第五節），
// 這裡只決定怎麼說、怎麼畫，不自行推算換版；欄位不存在（舊宿主）＝沒有換版要處理。
import { icon } from './icons.js';
import { confirmDialog, spinner } from './ui.js';
import { fmtElapsed } from './util.js';

export const CLI_NAME = { Claude: 'Claude Code', Codex: 'Codex', Antigravity: 'Antigravity', Grok: 'Grok Build' };
export const cliName = (p) => CLI_NAME[p] || p || 'CLI';

/**
 * 宿主送來的列舉字串：協定是小駝峰（waitingTool、draining、external），C# 列舉直接轉成字串會是大駝峰（WaitingTool）——兩種都認，
 * 一律換成小駝峰再查表
 */
export const enumKey = (s) => (typeof s === 'string' && s ? s.charAt(0).toLowerCase() + s.slice(1) : '');

const num = (n) => (typeof n === 'number' && isFinite(n) && n > 0 ? Math.floor(n) : 0);
const updatedTo = (r) => 'CLI 已更新' + (r && r.to ? '到 ' + r.to : '');

/**
 * HeaderRestart.state（restartWaiting[].state 是同一組）：
 * short＝CLI 卡計數與清單的簡短說法、tone＝色調（CSS 的 data-tone）、icon＝圖示（null＝轉圈）、
 * text＝宿主沒給 detail 時對話標頭的預設說明
 */
export const RESTART_STATES = {
  waitingTool: { short: '等工具結束', tone: 'wait', icon: 'clock', text: (r) => updatedTo(r) + '，等目前的工具完成後切換' },
  waitingUser: { short: '等你回應', tone: 'user', icon: 'question', text: (r) => updatedTo(r) + '，等你回應後再切換' },
  waitingBackground: { short: '等背景工作結束', tone: 'wait', icon: 'clock', text: (r) => updatedTo(r) + '，等背景工作結束後切換' },
  waitingTurn: { short: '等這一輪結束', tone: 'wait', icon: 'clock', text: (r) => updatedTo(r) + '，這一輪結束後切換' },
  waitingUpdate: { short: '等更新完成', tone: 'wait', icon: 'download', text: () => '已停下，等 CLI 更新完成後以新版接續' },
  restarting: { short: '重新啟動中', tone: 'busy', icon: null, text: () => '正在以新版重新啟動…' },
  continued: { short: '已接續', tone: 'ok', icon: 'check', text: (r) => '已用新版' + (r && r.to ? ' ' + r.to : '') + '自動接續' },
  notContinued: { short: '沒有自動接續', tone: 'warn', icon: 'warning', text: (r) => updatedTo(r) + '；這一輪沒有自動接續' },
  failed: { short: '重開失敗', tone: 'err', icon: 'error', text: () => '以新版重新啟動失敗' },
  nextSend: { short: '下次送出時換新版', tone: 'info', icon: 'info', text: (r) => updatedTo(r) + '，下次送出時才用新版' },
  appUpdate: { short: '等 app 更新', tone: 'wait', icon: 'download', text: () => '已停下，等 Clode Myriagent 更新後自動接續' },
};

/** 不認得的 state（較新的宿主）：照樣顯示宿主的說明，色調用一般的等待 */
const UNKNOWN_STATE = { short: '處理中', tone: 'wait', icon: 'clock', text: (r) => updatedTo(r) };
export const restartStateOf = (state) => RESTART_STATES[enumKey(state)] || UNKNOWN_STATE;

/** 換版狀態的圖示：重新啟動中是轉圈，其他是靜態圖示 */
export function restartIcon(state, cls) {
  const s = restartStateOf(state);
  return s.icon ? icon(s.icon, cls) : spinner(cls);
}

/** 對話標頭的說明：宿主的 detail，沒有就用狀態的預設說法 */
export function restartText(r) {
  if (!r) return '';
  const d = typeof r.detail === 'string' ? r.detail.trim() : '';
  return d || restartStateOf(r.state).text(r);
}

/** 對話標頭的已等待時間「已等 3:25」：宿主的 since（開始等待的時間，Unix 毫秒；還在等的狀態才有），沒有（舊宿主、不是等待中）回空字串 */
export function restartWaitText(r, now) {
  const since = r && r.since;
  if (typeof since !== 'number' || !isFinite(since) || since <= 0) return '';
  return '已等 ' + fmtElapsed(now - since);
}

/** 版本變化「2.1.295 → 2.1.297」（不明的一邊寫「舊版」「新版」，例如更新還沒完成時還不知道新版號）；兩個都不明回空字串 */
export function versionArrow(from, to) {
  if (!from && !to) return '';
  return (from || '舊版') + ' → ' + (to || '新版');
}

const WAITING_ORDER = ['waitingTool', 'waitingUser', 'waitingBackground', 'waitingTurn', 'waitingUpdate', 'restarting'];
const DONE_PARTS = [['continued', '已接續', 'ok'], ['notContinued', '沒有自動接續', 'warn'], ['failed', '重開失敗', 'err']];

/** 只有程序還在跑舊版（沒有排定重開）時的說明：依政策 */
const OLD_VERSION_HINT = { NextSend: '下次送出時才換新版', AfterTurn: '這一輪結束後換新版', SafePoint: '到安全點就換新版' };

/**
 * CLI 卡的換版彙總（web-protocol 第五節）：「3 個對話換版中：1 個等工具結束、1 個等你回應、1 個已接續」——
 * 依 restartWaiting[].state 計數，加上最近一波 restartDone 的已接續、沒有自動接續、重開失敗。
 * 沒有排定重開、只有程序還在跑舊版（runningOnOldVersion）時是「2 個對話還在用舊版」加上政策的說明。
 * 回 { title, parts: [{ key, n, text, tone }], waiting, pending, hint }；兩者都沒有回 null
 */
export function cliRestartSummary(v) {
  if (!v || typeof v !== 'object') return null;
  const waiting = Array.isArray(v.restartWaiting) ? v.restartWaiting.filter((w) => w && typeof w === 'object' && w.conversationId) : [];
  const pending = Math.max(num(v.restartPending), waiting.length);
  const old = num(v.runningOnOldVersion);
  if (!pending && !old) return null;
  // 只有計數、沒有清單（restartPending 與 restartWaiting 不一致）：照計數顯示，不列對話
  if (!waiting.length && pending) return { title: pending + ' 個對話換版中', parts: [], waiting, pending, hint: '' };
  if (!waiting.length) return { title: old + ' 個對話還在用舊版', parts: [], waiting, pending: 0, hint: OLD_VERSION_HINT[policyOf(v.restartPolicy).value] };
  const counts = new Map();
  for (const w of waiting) { const k = enumKey(w.state); counts.set(k, (counts.get(k) || 0) + 1); }
  const parts = [];
  const add = (key, n, short, tone) => { if (n > 0) parts.push({ key, n, text: n + ' 個' + short, tone }); };
  for (const key of WAITING_ORDER) add(key, counts.get(key) || 0, RESTART_STATES[key].short, RESTART_STATES[key].tone);
  for (const [key, n] of counts) if (!WAITING_ORDER.includes(key)) add(key, n, restartStateOf(key).short, restartStateOf(key).tone);
  const done = v.restartDone && typeof v.restartDone === 'object' ? v.restartDone : null;
  let finished = 0;
  for (const [key, short, tone] of DONE_PARTS) {
    const n = num(done && done[key]);
    finished += n;
    add(key, n, short, tone);
  }
  return { title: (waiting.length + finished) + ' 個對話換版中', parts, waiting, pending, hint: '' };
}

/**
 * 最近一波換版的結果（沒有進行中的換版時顯示在 CLI 卡的「換版結果」）：
 * 「2 個已用新版接續、3 個閒置的已關閉舊程序」；沒有換版過或全是 0 回 null
 */
export function cliRestartDoneText(v) {
  const d = v && v.restartDone;
  if (!d || typeof d !== 'object') return null;
  const parts = [];
  if (num(d.continued)) parts.push(num(d.continued) + ' 個已用新版接續');
  if (num(d.restarted)) parts.push(num(d.restarted) + ' 個閒置的已關閉舊程序');
  if (num(d.notContinued)) parts.push(num(d.notContinued) + ' 個沒有自動接續');
  if (num(d.failed)) parts.push(num(d.failed) + ' 個重開失敗');
  return parts.length ? parts.join('、') : null;
}

/** 更新屏障的四個階段（CliView.update.phase） */
export const UPDATE_PHASES = [
  { key: 'draining', label: '等對話停下' },
  { key: 'waitingProcesses', label: '等其他程序結束' },
  { key: 'installing', label: '換檔中' },
  { key: 'verifying', label: '驗證新版' },
];

/**
 * 更新進度的說明：{ index（0 起，不認得的階段為 -1）, label, detail }。
 * draining 的 detail 是「1 個已停下、2 個還在執行（到安全點就停）」，installing 提醒不要結束 app
 */
export function updatePhaseInfo(u) {
  const phase = enumKey(u && u.phase);
  const index = UPDATE_PHASES.findIndex((p) => p.key === phase);
  const label = index >= 0 ? UPDATE_PHASES[index].label : '更新中';
  let detail = '';
  if (phase === 'draining') {
    const parts = [];
    if (num(u.held)) parts.push(num(u.held) + ' 個已停下');
    if (num(u.waiting)) parts.push(num(u.waiting) + ' 個還在執行（到安全點就停）');
    detail = parts.join('、');
  } else if (phase === 'waitingProcesses') detail = '確認舊版的程序都已結束、清理上次留下的檔案';
  else if (phase === 'installing') detail = '正在替換 CLI 的檔案，這段期間請不要結束 app';
  else if (phase === 'verifying') detail = '確認新版的版本資訊一致';
  return { index, phase, label, detail };
}

/**
 * CLI 的換版／安裝狀態：CLI 卡的狀態膠囊與頂部列的 CLI 徽章共用同一套規則。優先順序：
 * 安裝不一致（installBroken）＞更新中（app 觸發的更新屏障，update.phase）＞安裝變動中（changing；app 自己在安裝或更新時不算）＞
 * 換版中（有對話已排定以新版重開、還沒完成：restartPending／restartWaiting；安裝不一致時宿主不重開任何對話，不算）。
 * 回 { key: 'broken'|'update'|'changing'|'restarting', label, tone: 'err'|'busy', detail, pending }；都沒有回 null
 */
export function cliHotState(v) {
  if (!v || typeof v !== 'object') return null;
  const broken = typeof v.installBroken === 'string' && v.installBroken.trim() ? v.installBroken.trim() : null;
  if (broken) return { key: 'broken', label: '安裝不一致', tone: 'err', detail: broken, pending: 0 };
  const upd = v.update && typeof v.update === 'object' && v.update.phase ? v.update : null;
  if (upd) return { key: 'update', label: '更新中', tone: 'busy', detail: updatePhaseInfo(upd).label, pending: 0 };
  if (v.changing && !v.busy) return { key: 'changing', label: '安裝變動中', tone: 'busy', detail: '偵測到 CLI 正在換檔，新的對話程序等安裝穩定才啟動', pending: 0 };
  const summary = cliRestartSummary(v);
  if (summary && summary.pending > 0) return { key: 'restarting', label: '換版中', tone: 'busy', detail: summary.title, pending: summary.pending };
  return null;
}

/** 宿主在更新開始時放的通用進度字樣：已經有階段說明時不再重複 */
const GENERIC_PROGRESS = new Set(['更新中…', '安裝中…', '處理中…']);

/**
 * 頂部列的 CLI 徽章（點了開設定的 CLI 區）：換版／安裝狀態與 CLI 卡同一套規則（cliHotState），再來是一般的安裝／更新進度與「有新版」。
 * 優先順序：安裝不一致＞更新中（更新屏障、安裝或更新進行中）＞安裝變動中＞換版中＞有新版；兩種 CLI 是同一個狀態時合併成「CLI …」。
 * 回 { key, icon（'spinner'＝轉圈）, text, tone（'err'｜'busy'｜'update'）, title }；沒有要提醒的回 null（徽章收起）
 */
export function cliBadgeState(cli) {
  const list = Object.keys(cli || {}).map((p) => ({ p, v: cli[p] })).filter((x) => x.v && typeof x.v === 'object')
    .map((x) => Object.assign(x, { hot: cliHotState(x.v) }));
  const who = (xs) => (xs.length > 1 ? 'CLI' : cliName(xs[0].p));
  const lines = (xs, f) => xs.map((x) => cliName(x.p) + '：' + f(x)).join('\n');

  const broken = list.filter((x) => x.hot && x.hot.key === 'broken');
  if (broken.length) {
    return { key: 'broken', icon: 'warning', text: who(broken) + ' 安裝不一致', tone: 'err', title: lines(broken, (x) => x.hot.detail) + '\n安裝修好之前不會重開任何對話' };
  }
  const updating = list.filter((x) => x.v.busy || (x.hot && x.hot.key === 'update'));
  if (updating.length) {
    const x = updating[0];
    const prog = typeof x.v.progress === 'string' ? x.v.progress.trim() : '';
    let text;
    if (x.hot && x.hot.key === 'update') text = cliName(x.p) + ' 更新中：' + (prog && !GENERIC_PROGRESS.has(prog) ? prog : x.hot.detail);
    else text = cliName(x.p) + (prog ? '：' + prog : ' 更新中');
    return { key: 'update', icon: 'spinner', text, tone: 'update', title: lines(updating, (y) => (y.hot && y.hot.key === 'update' ? '更新中・' + y.hot.detail : (y.v.progress || '更新中'))) };
  }
  const changing = list.filter((x) => x.hot && x.hot.key === 'changing');
  if (changing.length) return { key: 'changing', icon: 'spinner', text: who(changing) + ' 安裝變動中…', tone: 'busy', title: lines(changing, (x) => x.hot.detail) };
  const restarting = list.filter((x) => x.hot && x.hot.key === 'restarting');
  if (restarting.length) {
    // 對話數與各狀態放在提示裡（同 CLI 卡的彙總句：「4 個對話換版中：1 個等工具結束、…」）
    return {
      key: 'restarting', icon: 'refresh', text: who(restarting) + ' 換版中', tone: 'busy',
      title: lines(restarting, (x) => { const s = cliRestartSummary(x.v); return s.title + (s.parts.length ? '：' + s.parts.map((q) => q.text).join('、') : ''); }),
    };
  }
  const ups = list.filter((x) => x.v.updateAvailable);
  if (ups.length) {
    return {
      key: 'available', icon: 'download', text: ups.length > 1 ? 'CLI 有新版' : cliName(ups[0].p) + ' 有新版 ' + (ups[0].v.latest || ''), tone: 'update',
      title: ups.map((x) => cliName(x.p) + ' ' + (x.v.version || '') + ' → ' + (x.v.latest || '')).join('\n'),
    };
  }
  return null;
}

/** 排空超過時限（overdue）時的問句：「還有 2 個對話在執行工具，要等還是現在中斷？」 */
export function overdueQuestion(u) {
  const n = num(u && u.waiting);
  return (n ? '還有 ' + n + ' 個對話在執行工具' : '對話還沒停下') + '，要等還是現在中斷？';
}

/** 設定「CLI 更新後」（Settings.cliRestartPolicy；只能在這台電腦改） */
export const RESTART_POLICIES = [
  {
    value: 'SafePoint', label: '在安全點立即切換並自動接續', short: '在安全點立即切換', tag: '預設',
    desc: '沒有工具在執行、也沒有等你回應時，立刻以新版重開並自動接著做；工具執行中就等它結束，不會打斷。',
  },
  {
    value: 'AfterTurn', label: '等這一輪結束再切換', short: '等這一輪結束再切換',
    desc: '執行中的這一輪照舊用原本的版本做完，結束後才換；閒置的對話馬上換新版。',
  },
  {
    value: 'NextSend', label: '下次送出時才用新版', short: '下次送出時才用新版',
    desc: '不主動關閉或重開任何對話；你下一次送出訊息時才換成新版。',
  },
];
export const policyOf = (value) => RESTART_POLICIES.find((p) => p.value.toLowerCase() === String(value || '').toLowerCase()) || RESTART_POLICIES[0];

/**
 * CLI 卡的「立即套用」／更新屏障排空中的「現在中斷」：說明會中斷什麼，確認後回 true（呼叫端送 cliApplyNow {provider}）
 */
export async function confirmApplyNowProvider(provider, v, interrupt) {
  const name = cliName(provider);
  const to = v && (v.lastChange && v.lastChange.to ? v.lastChange.to : v.version);
  const r = await confirmDialog(interrupt
    ? {
      title: '現在中斷並更新 ' + name + '？',
      message: '還在執行的對話會立刻停下：執行中的工具與背景工作會被中斷（結果未知），等你回應的卡片會失效。更新完成後，這些對話會以新版自動接續，接續訊息會說明中斷時的狀態；被中斷的是部署、發布這類高風險操作就不自動接續，等你確認後再按「繼續」。',
      confirmLabel: '現在中斷',
    }
    : {
      title: '立即套用 ' + name + (to ? ' ' + to : '') + '？',
      message: '還在等安全點的對話會立刻以新版重開：執行中的工具與背景工作會被中斷（結果未知，代理接續時會先確認是否已生效），等你回應的卡片會失效、代理接續後會重新提出。',
      confirmLabel: '立即套用',
    });
  return r.ok;
}

/** 對話標頭的「立即套用」：依等待的原因說明會中斷什麼，確認後回 true（呼叫端送 cliApplyNow {conversationId}） */
export async function confirmApplyNowConversation(r) {
  const state = enumKey(r && r.state);
  const detail = r && typeof r.detail === 'string' ? r.detail : '';
  let message;
  if (state === 'waitingTool') message = '目前在執行的工具會被中斷，結果未知；代理以新版接續時會先確認它是否已經生效，可以安全重跑才重跑。';
  else if (state === 'waitingUser') message = '等你回應的卡片會失效，代理以新版接續後會重新提出。';
  else if (state === 'waitingBackground') message = '這個對話的背景工作會被停止（結果未知）；代理以新版接續時會先確認它們做到哪裡，可以安全重跑才重跑。';
  else message = '這一輪會被中斷並以新版重開，接續訊息會說明中斷時的狀態。';
  const res = await confirmDialog({
    title: '立即以新版重開這個對話？',
    message,
    detail: detail || null,
    confirmLabel: '立即套用',
  });
  return res.ok;
}
