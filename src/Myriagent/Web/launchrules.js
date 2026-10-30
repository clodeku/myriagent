// 執行列（宿主送的 launchStatus／launchLog，LaunchView 見 docs/web-protocol.md）：按鈕能不能按、狀態文字與色調、快捷鍵、執行模式、輸出的合併與錯誤行。
// 純函式、沒有 import（tools/ui-probe/launchrules.test.mjs 以 data: URL 匯入）；顯示一律經 textContent

const PHASES = ['idle', 'building', 'running', 'paused', 'stopping'];

/** 網頁保留的輸出行數（同宿主） */
export const LOG_CAPACITY = 2000;

/** 「把錯誤貼到輸入框」最多幾行 */
export const MAX_ERROR_LINES = 20;

const KIND_LABEL = { dotnet: '.NET', electron: 'Electron' };

/** 執行模式（每個專案各自記得；宿主沒送或不認得時當 Debug） */
export const MODES = ['debug', 'release'];
const MODE_LABEL = { debug: 'Debug', release: 'Release' };

/** 選的專案的執行模式 */
export function modeOf(launch) {
  const m = launch && launch.mode;
  return MODES.includes(m) ? m : 'debug';
}

/** 這一次執行的模式（閒置、宿主沒送是 null） */
export function runModeOf(launch) {
  const m = launch && launch.runMode;
  return phaseOf(launch) !== 'idle' && MODES.includes(m) ? m : null;
}

export function modeLabel(mode) {
  return MODE_LABEL[mode] || MODE_LABEL.debug;
}

/** 模式的說明（選單的說明那一行、晶片與狀態的滑過說明）；.NET 另外說明建置的組態 */
export function modeNote(mode, kind) {
  const dotnet = kind !== 'electron';
  return mode === 'release'
    ? (dotnet ? 'Release 組態建置，' : '') + '獨立執行：關掉或重新啟動 Clode Myriagent 不影響它（重新開啟後接回）'
    : (dotnet ? 'Debug 組態建置，可以附加偵錯工具；' : '') + '跟著 Clode Myriagent：關掉或重新啟動時一起關閉';
}

/**
 * 模式晶片：{ mode, label, title }。執行中的模式和選的不同（執行中換了模式）時，說明下一次才套用
 */
export function modeChip(launch) {
  const t = selectedTarget(launch);
  const mode = modeOf(launch);
  const run = runModeOf(launch);
  const lines = ['執行模式：' + modeLabel(mode), modeNote(mode, t && t.kind)];
  if (run && run !== mode) lines.push('這次以 ' + modeLabel(run) + ' 模式執行；下次啟動或重新建置並執行時改用 ' + modeLabel(mode));
  lines.push('（點一下換模式）');
  return { mode, label: modeLabel(mode), title: lines.join('\n') };
}

/** 不認得的階段當閒置（只有啟動能按；按了宿主照實際狀態處理） */
export function phaseOf(launch) {
  const p = launch && launch.phase;
  return PHASES.includes(p) ? p : 'idle';
}

/** 選的目標（找不到時第一個；沒有目標是 null） */
export function selectedTarget(launch) {
  if (!launch || !Array.isArray(launch.targets) || !launch.targets.length) return null;
  return launch.targets.find((t) => t && t.id === launch.selected) || launch.targets[0];
}

/**
 * 四顆按鈕：start（閒置是啟動；暫停中變成繼續，送 resume）、pause、stop、restart。
 * { start: { enabled, action, label }, pause, stop, restart }；action 是要送的 launchAction 的 action
 */
export function launchButtons(phase) {
  const paused = phase === 'paused';
  return {
    start: paused
      ? { enabled: true, action: 'resume', label: '繼續', key: 'F5', icon: 'play' }
      : { enabled: phase === 'idle', action: 'start', label: '建置並執行', key: 'F5', icon: 'play' },
    pause: { enabled: phase === 'running', action: 'pause', label: '暫停', key: '', icon: 'pause' },
    stop: { enabled: phase === 'building' || phase === 'running' || paused, action: 'stop', label: '停止', key: 'Shift+F5', icon: 'stop' },
    restart: { enabled: phase !== 'idle', action: 'restart', label: '重新建置並執行', key: 'Ctrl+Shift+F5', icon: 'refresh' },
  };
}

/** 按鈕的說明（含快捷鍵） */
export function buttonTitle(b) {
  return b.key ? b.label + '（' + b.key + '）' : b.label;
}

/**
 * 快捷鍵（VS Code 的慣例）：F5 啟動／繼續、Shift+F5 停止、Ctrl+Shift+F5 重新建置並執行。
 * 回要送的 action，或 null（不是這些鍵、現在不能做）。ev 只看 key、shiftKey、ctrlKey、altKey、metaKey
 */
export function keyAction(ev, phase) {
  if (!ev || ev.key !== 'F5' || ev.altKey || ev.metaKey) return null;
  const b = launchButtons(phase);
  let pick;
  if (ev.ctrlKey && ev.shiftKey) pick = b.restart.enabled ? b.restart : b.start;   // 閒置時重新建置＝啟動
  else if (ev.ctrlKey) return null;
  else if (ev.shiftKey) pick = b.stop;
  else pick = b.start;
  return pick.enabled ? pick.action : null;
}

/** 經過時間：分:秒，一小時以上 時:分:秒 */
export function elapsed(ms) {
  const s = Math.max(0, Math.floor((Number(ms) || 0) / 1000));
  const p2 = (n) => String(n).padStart(2, '0');
  const hr = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
  return hr > 0 ? hr + ':' + p2(m) + ':' + p2(sec) : m + ':' + p2(sec);
}

/**
 * 狀態那一段：{ tone: idle／busy／ok／paused／warn／danger, text, title, spin, timed }。
 * title 以 text 開頭（窄窗格只留顏色點時，滑過看得到狀態），下一行是說明；timed＝要每秒更新經過時間（建置中、執行中、暫停中）
 */
export function statusView(launch, now) {
  const phase = phaseOf(launch);
  const since = launch && Number.isFinite(launch.since) ? launch.since : null;
  const time = since !== null ? elapsed(now - since) : '';
  const withTime = (t) => (time ? t + ' ' + time : t);
  const v = (tone, text, note, spin, timed) => ({ tone, text, title: note ? text + '\n' + note : text, spin, timed });
  // 執行中、暫停中的說明另外寫這一次的模式（會不會跟著 Clode Myriagent 關閉）
  const run = runModeOf(launch);
  const t = selectedTarget(launch);
  const runNote = run ? '\n' + modeLabel(run) + ' 模式：' + modeNote(run, t && t.kind) : '';
  switch (phase) {
    case 'building': return v('busy', withTime('建置中'), '正在建置（dotnet build' + (run ? ' -c ' + modeLabel(run) : '') + '）', true, true);
    case 'running': return v('ok', withTime('執行中'), '暫停會凍結它的所有程序（視窗會沒有回應）' + runNote, false, true);
    case 'paused': return v('paused', withTime('已暫停'), '所有程序都凍結了；按繼續或 F5 恢復' + runNote, false, true);
    case 'stopping': return v('busy', '停止中…', '正在結束程式', true, false);
    default: break;
  }
  const last = launch && launch.last;
  if (!last || typeof last !== 'object') return { tone: 'idle', text: '', title: '', spin: false, timed: false };
  const text = typeof last.text === 'string' ? last.text : '';
  switch (last.kind) {
    case 'buildFailed': return v('danger', '建置失敗', text, false, false);
    case 'startFailed': return { tone: 'danger', text: '無法啟動', title: text || '無法啟動', spin: false, timed: false };
    case 'exited': {
      const bad = Number.isFinite(last.code) && last.code !== 0;
      return { tone: bad ? 'warn' : 'idle', text: text || '已結束', title: text || '已結束', spin: false, timed: false };
    }
    case 'stopped': return { tone: 'idle', text: text || '已停止', title: text || '已停止', spin: false, timed: false };
    default: return { tone: 'idle', text: '', title: '', spin: false, timed: false };
  }
}

/** 失敗（建置失敗、無法啟動）時自動打開輸出面板；回這次失敗的鍵（同一次只開一次），沒有是 null */
export function failureKey(launch) {
  const last = launch && launch.last;
  if (!last || (last.kind !== 'buildFailed' && last.kind !== 'startFailed') || phaseOf(launch) !== 'idle') return null;
  return (launch.selected || '') + ':' + (last.at || 0) + ':' + (launch.logEpoch || 0);
}

/** 目標晶片的說明：名稱、說明（WinForms · net10.0-windows）、相對路徑 */
export function targetTitle(t) {
  if (!t) return '';
  return [t.name || '', t.detail || KIND_LABEL[t.kind] || '', t.path || ''].filter(Boolean).join('\n');
}

/** 選單裡目標的說明那一行：說明・路徑（執行中的另外標） */
export function targetDesc(t) {
  if (!t) return '';
  const phase = PHASES.includes(t.phase) ? t.phase : 'idle';
  const tag = phase === 'running' ? '執行中・' : phase === 'paused' ? '已暫停・' : phase === 'building' ? '建置中・' : '';
  return tag + [t.detail || KIND_LABEL[t.kind] || '', t.path || ''].filter(Boolean).join('・');
}

/** 執行列的鍵：內容沒變就不重畫（狀態的經過時間由 tick 另外更新） */
export function launchKey(launch) {
  if (!launch) return '';
  const t = selectedTarget(launch);
  return JSON.stringify([launch.folder, launch.selected, phaseOf(launch), launch.since, launch.last, modeOf(launch), runModeOf(launch),
    (launch.targets || []).map((x) => [x.id, x.name, x.detail, x.path, x.phase]), t && t.id]);
}

/**
 * 輸出的合併：state 是網頁目前的 { targetId, epoch, seq, lines }（沒有是 null），msg 是宿主的 launchLog。
 * 不同目標或 reset 時換掉整份，否則接在後面；只留最後 LOG_CAPACITY 行。回新的 state
 */
export function mergeLog(state, msg) {
  if (!msg || typeof msg !== 'object') return state;
  const lines = Array.isArray(msg.lines) ? msg.lines.filter((l) => typeof l === 'string') : [];
  const same = state && state.targetId === msg.targetId && state.epoch === msg.epoch && !msg.reset;
  const all = same ? state.lines.concat(lines) : lines;
  return {
    targetId: msg.targetId,
    epoch: Number.isFinite(msg.epoch) ? msg.epoch : 0,
    seq: Number.isFinite(msg.seq) ? msg.seq : 0,
    lines: all.length > LOG_CAPACITY ? all.slice(all.length - LOG_CAPACITY) : all,
  };
}

/** 面板開著時要不要再拿輸出：目標換了、epoch 或 logSeq 比網頁的新 */
export function needsLog(state, launch) {
  const t = selectedTarget(launch);
  if (!t) return false;
  if (!state || state.targetId !== t.id) return true;
  const epoch = Number.isFinite(launch.logEpoch) ? launch.logEpoch : 0;
  const seq = Number.isFinite(launch.logSeq) ? launch.logSeq : 0;
  return epoch !== state.epoch || seq > state.seq;
}

/** launchLog 指令要帶的 epoch、after（網頁的輸出是別的目標或還沒有時要整份） */
export function logRequest(state, targetId) {
  if (!state || state.targetId !== targetId) return { epoch: -1, after: 0 };
  return { epoch: state.epoch, after: state.seq };
}

const MSBUILD_ERROR = /^\s*\S.*?\(\d+(?:,\d+)*\)\s*:\s*error\s+[A-Za-z]+\d+\s*:/;
const GENERIC_ERROR = /(?:^|:\s*)error\s+[A-Za-z]+\d+\s*:/;

/** 輸出裡的錯誤行（MSBuild 的「檔案(行,欄): error CS1002: …」、NETSDK／NU 等），去掉重複，最多 MAX_ERROR_LINES 行 */
export function errorLines(lines) {
  const out = [];
  const seen = new Set();
  for (const raw of Array.isArray(lines) ? lines : []) {
    if (typeof raw !== 'string' || !(MSBUILD_ERROR.test(raw) || GENERIC_ERROR.test(raw))) continue;
    const line = raw.trim();
    if (seen.has(line)) continue;
    seen.add(line);
    out.push(line);
    if (out.length >= MAX_ERROR_LINES) break;
  }
  return out;
}

const WARNING_LINE = /\bwarning\s+[A-Za-z]+\d+\s*:/;

/**
 * 建置的錯誤與警告數（第七批視覺效果：執行列狀態旁的「✕ 3 ⚠ 12」）：錯誤行同 errorLines、警告是「warning CS0168:」這類，
 * 同一行只算一次（MSBuild 結尾的摘要會再列一次）。回 { errors, warnings, first }，first 是第一個錯誤的行號（沒有錯誤就第一個警告，都沒有是 -1）
 */
export function buildDiagnostics(lines) {
  let errors = 0, warnings = 0, firstErr = -1, firstWarn = -1;
  const seen = new Set();
  const list = Array.isArray(lines) ? lines : [];
  for (let i = 0; i < list.length; i++) {
    const raw = list[i];
    if (typeof raw !== 'string') continue;
    const isErr = MSBUILD_ERROR.test(raw) || GENERIC_ERROR.test(raw);
    if (!isErr && !WARNING_LINE.test(raw)) continue;
    const key = raw.trim();
    if (seen.has(key)) continue;
    seen.add(key);
    if (isErr) { errors++; if (firstErr < 0) firstErr = i; }
    else { warnings++; if (firstWarn < 0) firstWarn = i; }
  }
  return { errors, warnings, first: firstErr >= 0 ? firstErr : firstWarn };
}

/** 是不是警告行（輸出面板的標示） */
export function isWarningLine(line) {
  return typeof line === 'string' && WARNING_LINE.test(line);
}

/** 貼到輸入框的文字：說明＋錯誤行（沒有錯誤行時用 last.text） */
export function fixPrompt(launch, lines) {
  const t = selectedTarget(launch);
  const errs = errorLines(lines);
  const body = errs.length ? errs : (launch && launch.last && typeof launch.last.text === 'string' && launch.last.text ? [launch.last.text] : []);
  if (!body.length) return '';
  const name = t ? (t.path || t.name || '') : '';
  return (name ? '執行列建置 ' + name + ' 失敗，請修正：' : '建置失敗，請修正：') + '\n```\n' + body.join('\n') + '\n```';
}
