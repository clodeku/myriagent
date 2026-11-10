// Claude 對話的選項與操作的純規則（2026-10-25）：輸入列的主代理與輸出風格、標頭的目標晶片、
// 插進這一輪的訊息取消、回溯。web-protocol 的 Header.agent／outputStyle／goal、setAgent、setOutputStyle、cancelSteer、rewindPreview、rewind
// （背景工作的詳細與輸出在 liverules.js 的 taskDetailView：每種代理都有）

/** CLI 內建的輸出風格（2.1.310 的 available_output_styles）；其他是使用者或外掛自訂的，照名稱顯示 */
export const STYLE_LABEL = Object.freeze({ default: '預設', Explanatory: '解說', Learning: '學習', Concise: '精簡', Proactive: '主動' });
export const STYLE_DESC = Object.freeze({
  default: 'Claude Code 原本的風格：把事情做完、簡短回報',
  Explanatory: '邊做邊說明實作上的選擇與這個程式庫的慣例',
  Learning: '邊做邊教，留一些小地方讓你自己寫',
  Concise: '回覆盡量簡短',
  Proactive: '主動多做一步，看到相關的問題一起處理或提出',
});

/** 輸出風格的顯示名稱：null＝照帳戶的設定 */
export function styleLabel(style) {
  if (!style) return '照設定';
  return STYLE_LABEL[style] || style;
}

/** 輸出風格選單：第一項「照設定」，接著 CLI 回報的清單；目前的值不在清單裡時另列一項（看得到、改得回來） */
export function styleMenu(hd) {
  const styles = Array.isArray(hd && hd.outputStyles) ? hd.outputStyles.filter((s) => typeof s === 'string' && s) : [];
  const cur = (hd && hd.outputStyle) || null;
  const items = [{ value: null, label: '照設定', desc: '用帳戶設定裡的輸出風格（沒設就是預設）', checked: !cur }];
  for (const s of styles) {
    const label = styleLabel(s);
    items.push({ value: s, label, desc: STYLE_DESC[s] || null, hint: label !== s ? s : null, checked: cur === s });
  }
  if (cur && !styles.includes(cur)) items.push({ value: cur, label: cur, desc: '目前使用中（這個帳戶沒有回報這個風格）', checked: true, current: true });
  return items;
}

/** 本機的對話才有這些選項（瀏覽器版、其他電腦的副本、要在其他電腦開的草稿都沒有：清單是那台的 CLI 回報的） */
export function localOnly(hd, ctx) {
  return !!hd && !ctx.isHub && !ctx.isReplica && !(hd.isDraft && hd.deviceId);
}

/** 輸出風格鈕：只有 Claude 的本機對話，CLI 回報過清單或已經選了一個 */
export function showStyle(hd, ctx) {
  if (!localOnly(hd, ctx) || hd.provider !== 'Claude') return false;
  return (Array.isArray(hd.outputStyles) && hd.outputStyles.length > 0) || !!hd.outputStyle;
}

/** 主代理鈕：Antigravity 與 Claude 的本機對話，有預設以外的代理可選或已經選了一個 */
export function showAgent(hd, ctx) {
  if (!localOnly(hd, ctx) || (hd.provider !== 'Antigravity' && hd.provider !== 'Claude')) return false;
  const agents = Array.isArray(hd.agents) ? hd.agents : [];
  return agents.length > 1 || (!!hd.agent && hd.agent !== 'default');
}

/**
 * 輸入列上常駐的晶片：Antigravity 的代理照舊（有得選就顯示）；Claude 的代理與風格一直有得選（內建的代理與風格），
 * 常駐會把模型與推理強度的名稱擠短——只在選了預設以外的值時顯示，平常從模型選單底部進入（claudeMenuEntries）
 */
export function agentChipShown(hd, ctx) {
  if (!showAgent(hd, ctx)) return false;
  return hd.provider !== 'Claude' || (!!hd.agent && hd.agent !== 'default');
}

export function styleChipShown(hd, ctx) {
  return showStyle(hd, ctx) && !!hd.outputStyle;
}

/** 模型選單底部的「主代理…」「輸出風格…」（只有 Claude、有得選的）：{kind, label} */
export function claudeMenuEntries(hd, ctx) {
  if (!hd || hd.provider !== 'Claude') return [];
  const out = [];
  if (showAgent(hd, ctx)) {
    const agents = Array.isArray(hd.agents) ? hd.agents : [];
    const cur = hd.agent && hd.agent !== 'default' ? hd.agent : 'default';
    const a = agents.find((x) => x.value === cur);
    out.push({ kind: 'agent', label: '主代理：' + (a ? a.label || a.value : cur === 'default' ? '預設' : cur) });
  }
  if (showStyle(hd, ctx)) out.push({ kind: 'style', label: '輸出風格：' + styleLabel(hd.outputStyle) });
  return out;
}

/** Antigravity 的自訂代理只能在送出第一則訊息前選；Claude 隨時可以換（下一則訊息起生效） */
export function agentLocked(hd) {
  return !!hd && hd.provider === 'Antigravity' && !!(hd.sessionStarted || hd.running);
}

/**
 * 標頭的目標（Header.goal）→ 晶片：「目標：條件」（晶片上最多 24 字、滑過看完整的）；沒有目標回 null。
 * 執行中不能清除（插進這一輪的 /goal clear 不會被當成指令）：選單的那一項停用並說明
 */
export function goalView(goal, running) {
  const condition = goal && typeof goal.condition === 'string' ? goal.condition.trim() : '';
  if (!condition) return null;
  const short = condition.length > 24 ? condition.slice(0, 24) + '…' : condition;
  return {
    label: '目標：' + short,
    title: '進行中的目標（/goal）：' + condition + '\n每一輪結束時 Claude 會檢查它；還沒達成就接著做，按了停止之後下一則訊息也會接著追。',
    condition,
    canClear: !running,
    clearDesc: running ? '這一輪還在執行：先按停止再清除' : '送出 /goal clear，之後不再自動接著追這個目標',
  };
}

/** 預設主代理的說明 */
export function defaultAgentDesc(provider) {
  return provider === 'Claude' ? 'Claude Code 內建的主代理' : 'Antigravity 內建主代理';
}

/** 使用者訊息的「回溯」：只有桌面這台的 Claude 對話、不在執行中 */
export function canRewind(hd, ctx) {
  return localOnly(hd, ctx) && hd.provider === 'Claude' && !hd.isDraft && !hd.running;
}

/**
 * 宿主回的 rewindPreview → 對話框：{error} 或 {canConversation, conversationNote, canFiles, filesNote, fileList, fileCount}。
 * 預設兩個都勾（做得到的）；沒有要還原的檔案時檔案那一項不勾
 */
export function rewindPlan(msg) {
  if (!msg || msg.error) return { error: (msg && msg.error) || '不能回溯' };
  const later = Number.isFinite(msg.laterMessages) ? msg.laterMessages : 0;
  const canConversation = msg.conversation === true;
  const conversationNote = canConversation
    ? '這則' + (later > 1 ? '與之後的 ' + (later - 1) + ' 則訊息' : '') + '從對話移除、代理不再看得到，這則的文字放回輸入框'
    : (msg.conversationError || '對話回不去');
  const fileCount = Number.isFinite(msg.fileCount) ? msg.fileCount : 0;
  const canFiles = !msg.filesError;
  const ins = Number.isFinite(msg.insertions) ? msg.insertions : 0;
  const del = Number.isFinite(msg.deletions) ? msg.deletions : 0;
  const filesNote = !canFiles ? String(msg.filesError)
    : fileCount === 0 ? '代理在這則之後沒有以編輯工具改過檔案'
      : '還原 ' + fileCount + ' 個檔案（+' + ins + ' −' + del + ' 行）';
  const fileList = Array.isArray(msg.files) ? msg.files.filter((f) => typeof f === 'string').slice(0, 20) : [];
  return { canConversation, conversationNote, canFiles, filesNote, fileList, fileCount };
}

/** 已插進這一輪的那則（composer.steered）：輸入區上方那一行的文字 */
export function steeredLabel(steered) {
  const t = steered && typeof steered.text === 'string' ? steered.text.trim() : '';
  return '已插入這一輪：' + (t ? (t.length > 60 ? t.slice(0, 60) + '…' : t) : '（附件）');
}


/** 子代理的即時用量（Header.subagents 的一項：{tokens, tools, ms, last, done}）→ 工具卡上的晶片；沒有或不合格回 null */
export function agentUsageView(u) {
  if (!u || typeof u !== 'object' || !Number.isFinite(u.tokens) || u.tokens <= 0) return null;
  const tools = Number.isFinite(u.tools) ? u.tools : 0;
  const k = u.tokens >= 1e6 ? (u.tokens / 1e6).toFixed(1).replace(/\.0$/, '') + 'M' : u.tokens >= 1000 ? (u.tokens / 1000).toFixed(1).replace(/\.0$/, '') + 'K' : String(u.tokens);
  const secs = Number.isFinite(u.ms) && u.ms > 0 ? Math.round(u.ms / 1000) : null;
  const dur = secs == null ? '' : secs < 60 ? secs + ' 秒' : Math.floor(secs / 60) + ' 分 ' + (secs % 60) + ' 秒';
  const title = '子代理' + (u.done ? '用了 ' : '到目前用了 ') + u.tokens.toLocaleString('en-US') + ' tokens、呼叫工具 ' + tools + ' 次'
    + (dur ? '、' + (u.done ? '跑了 ' : '已經跑了 ') + dur : '') + (!u.done && typeof u.last === 'string' && u.last ? '；最近的工具：' + u.last : '');
  return { text: k + ' tokens', title, done: !!u.done };
}

/** Claude 的提示快取保留多久（訂閱帳戶實測是 1 小時的快取：result 的 cache_creation.ephemeral_1h_input_tokens） */
export const CACHE_TTL_MS = 60 * 60 * 1000;
/** CLI 沒給門檻時，重送用掉 5 小時額度的幾 % 以上才提醒（coldResumeQuota.minPercent 通常是 5） */
export const COLD_RESUME_MIN_PERCENT = 5;

/**
 * 快取過期後整份重送用掉 5 小時額度的幾 %（2026-11-11）：Header.coldResume.perMillion 是 Claude 回報的每百萬 tokens 的成本
 * （get_context_usage 的 coldResumeQuota，終端機「Resume this conversation?」用的同一份），Header.context.used 是目前的上下文；不知道回 null
 */
export function coldResumePercent(hd) {
  const q = hd && hd.coldResume;
  const used = hd && hd.context && Number.isFinite(hd.context.used) ? hd.context.used : 0;
  if (!q || !(q.perMillion > 0) || !(used > 0)) return null;
  return used / 1e6 * q.perMillion;
}

/** 百分比的寫法：10% 以上取整數、0.1% 以上一位小數，更小的寫「不到 0.1%」 */
export function percentText(p) {
  if (!(p >= 0.1)) return '不到 0.1%';
  return p >= 10 ? Math.round(p) + '%' : p.toFixed(1).replace(/\.0$/, '') + '%';
}

function spanText(ms) {
  const mins = Math.floor(ms / 60000);
  if (mins < 60) return mins + ' 分鐘';
  const hours = Math.floor(mins / 60);
  if (hours < 24) return hours + ' 小時' + (mins % 60 ? ' ' + (mins % 60) + ' 分' : '');
  return Math.floor(hours / 24) + ' 天';
}

function tokensText(n) {
  return n >= 1e6 ? (n / 1e6).toFixed(1).replace(/\.0$/, '') + 'M' : Math.round(n / 1000) + 'K';
}

/**
 * 送出前的快取提醒（Header.idleSince、Header.context、Header.coldResume）：Claude 對話閒置超過快取的保留時間，這一則會把整份上下文重算一次；
 * 2026-11-11 起照 Claude 回報的成本算這一則約用掉 5 小時額度的幾 %，到 Claude 給的門檻（minPercent，通常是 5%）才提醒——以前一律以 10 萬 tokens 為界，
 * 訂閱方案的費率下 50 萬 tokens 可能只用掉不到 1%，提醒的說法也不對。Claude 沒回報成本（沒有訂閱方案、閒置壓縮已經寫好摘要）不提醒。
 * 設定 claudeIdleResumeWarning 關掉、執行中、草稿不提醒；回 null 或 {title, message, detail, percent}
 */
export function cacheResumeWarning(hd, now, settings) {
  if (!hd || hd.provider !== 'Claude' || hd.running || hd.isDraft) return null;
  if (settings && settings.claudeIdleResumeWarning === false) return null;
  if (!Number.isFinite(hd.idleSince)) return null;
  const idle = now - hd.idleSince;
  if (idle < CACHE_TTL_MS) return null;
  const percent = coldResumePercent(hd);
  const min = hd.coldResume && hd.coldResume.minPercent > 0 ? hd.coldResume.minPercent : COLD_RESUME_MIN_PERCENT;
  if (percent == null || percent < min) return null;
  return {
    title: '快取已經過期',
    message: '這個對話已經閒置 ' + spanText(idle) + '，Claude 的提示快取（保留 1 小時）已經過期：這一則會把約 ' + tokensText(hd.context.used)
      + ' tokens 的上下文從頭再算一次，約用掉 5 小時額度的 ' + percentText(percent) + '（Claude 估的）。',
    detail: '要做的事和這個對話關係不大的話，開新對話比較省。',
    percent,
  };
}

/** 提示快取剩多久之內算快過期（送出鈕旁圓環的外圈轉琥珀，第九批） */
export const CACHE_LOW_MS = 10 * 60 * 1000;

/**
 * 提示快取的倒數（送出鈕旁上下文圓環的外圈，第九批、2026-10-26）：Claude、閒置中（不是執行中、不是草稿）、有 idleSince 時回
 * { frac: 剩下的比例 0～1, state: 'fresh'|'low'|'expired', mins: 剩幾分鐘（無條件進位）, title }，其他回 null。
 * 最後 10 分鐘是 low（琥珀），過了 1 小時是 expired（灰）——下一則會把整份上下文重算一次（cacheResumeWarning 送出前問的那件事，這裡先看得到）
 */
export function cacheCountdown(hd, now) {
  if (!hd || hd.provider !== 'Claude' || hd.running || hd.isDraft || !Number.isFinite(hd.idleSince)) return null;
  const idle = Math.max(0, now - hd.idleSince);
  const left = CACHE_TTL_MS - idle;
  if (left <= 0) {
    const cost = coldResumePercent(hd);
    return { frac: 0, state: 'expired', mins: 0, title: '提示快取已經過期（閒置 ' + spanText(idle) + '）：下一則會把整份上下文從頭再算一次'
      + (cost != null ? '，約用掉 5 小時額度的 ' + percentText(cost) : '') };
  }
  const mins = Math.ceil(left / 60000);
  return {
    frac: left / CACHE_TTL_MS, state: left <= CACHE_LOW_MS ? 'low' : 'fresh', mins,
    title: '提示快取還有約 ' + mins + ' 分鐘（Claude 保留 1 小時，已閒置 ' + spanText(idle) + '）：在這之前送出，上下文從快取讀、比較省額度',
  };
}

/**
 * 等額度重置的分格倒數（輸入區上方那一行，第九批）：剩下的時間佔 span（開始等的時候還剩多久）的比例，換成 cells 格裡還亮著幾格
 * （無條件進位：還沒到就至少一格；時間到或資料不合理 0 格）
 */
export function quotaWaitCells(until, now, span, cells = 12) {
  if (!Number.isFinite(until) || !(span > 0)) return 0;
  const left = until - now;
  if (left <= 0) return 0;
  return Math.min(cells, Math.ceil((left / span) * cells));
}

/** 等額度重置後自動接著做（Header.quotaWait：{until, account}）→ 輸入區上方那一行；沒有回 null */
export function quotaWaitView(qw, now) {
  if (!qw || !Number.isFinite(qw.until)) return null;
  const who = typeof qw.account === 'string' && qw.account ? '「' + qw.account + '」' : '這個帳戶';
  const at = new Date(qw.until);
  const hm = String(at.getHours()).padStart(2, '0') + ':' + String(at.getMinutes()).padStart(2, '0');
  const left = qw.until - now;
  return {
    text: '額度用完：等' + who + '在 ' + hm + ' 重置後自動接著做' + (left > 60000 ? '（還有 ' + spanText(left) + '）' : ''),
    title: '沒有其他帳戶可以接手；時間到了 Myriagent 會自動送出接續訊息。你自己送出訊息、換帳戶或按取消都會停止等待。',
  };
}
