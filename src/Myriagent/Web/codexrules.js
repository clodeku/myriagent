// Codex 控制面板的純規則；名稱與 id 分開，速度 id 一律使用 CLI 回報的值。
export const GOAL_STATUS = Object.freeze({ active: '進行中', paused: '已暫停', blocked: '等待補充資訊', usageLimited: '額度已用完', budgetLimited: '已達預算', complete: '已完成' });

export function codexShown(hd, ctx) {
  return !!hd && hd.provider === 'Codex' && !ctx.isHub && !ctx.isReplica && !(hd.isDraft && hd.deviceId);
}

export function goalSummary(goal) {
  if (!goal) return '尚未設定持續目標';
  const used = Number.isFinite(goal.tokensUsed) ? goal.tokensUsed.toLocaleString('zh-TW') : '0';
  const budget = Number.isFinite(goal.tokenBudget) ? '／' + goal.tokenBudget.toLocaleString('zh-TW') : '';
  const seconds = Number.isFinite(goal.timeUsedSeconds) ? Math.max(0, goal.timeUsedSeconds) : 0;
  return (GOAL_STATUS[goal.status] || goal.status || '未知狀態') + ' · ' + used + budget + ' tokens · ' + Math.floor(seconds / 60) + ' 分 ' + seconds % 60 + ' 秒';
}

export function speedOptions(tiers) {
  const out = [{ id: 'default', label: '標準', description: '使用標準速度與額度' }];
  for (const t of Array.isArray(tiers) ? tiers : []) {
    if (!t || typeof t.id !== 'string' || !t.id || out.some(x => x.id === t.id)) continue;
    out.push({ id: t.id, label: t.name || t.id, description: '較快，會消耗較多額度；是否可用仍以實際請求結果為準' });
  }
  return out;
}

export function reviewCommand(kind, value = '') {
  const v = value.trim();
  if (kind === 'uncommittedChanges') return '/review';
  if (kind === 'commit') {
    if (!/^[a-f\d]{7,64}$/i.test(v)) throw new Error('請填 commit 雜湊（7～64 位十六進位字元）');
    return '/review commit ' + v;
  }
  if (kind === 'baseBranch') {
    if (!v || v.length > 250 || v.startsWith('-') || /[\r\n\x00]/.test(v)) throw new Error('請填有效的比較分支名稱');
    return '/review base ' + v;
  }
  if (kind === 'custom' && v) return '/review ' + v;
  throw new Error('請填審查範圍或說明');
}
