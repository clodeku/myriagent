// 對話工作資料夾的 git 狀態（宿主送的 gitStatus，GitStatusView 見 docs/web-protocol.md）：標頭 git 晶片上的分支與徽章、說明文字、選單的明細。
// 純函式、沒有 import（tools/ui-probe/gitrules.test.mjs 以 data: URL 匯入）；顯示一律經 textContent

const OPS = { merge: '合併', rebase: '重定基底（rebase）', cherryPick: '揀選（cherry-pick）', revert: '還原（revert）', bisect: '二分搜尋（bisect）' };
const OP_SHORT = { merge: '合併中', rebase: 'rebase 中', cherryPick: '揀選中', revert: '還原中', bisect: 'bisect 中' };

const num = (v) => (Number.isFinite(v) && v > 0 ? v : 0);

/**
 * 推送／拉取狀態只和仍存在、且成功取得差異的上游比。預設分支只用來呈現歷史差異，不能推斷分支用途或是否推送過。
 * { name, ahead, behind }
 */
export function compareTarget(git) {
  if (!git) return null;
  if (git.upstream && !git.upstreamGone && Number.isFinite(git.ahead) && Number.isFinite(git.behind)) return { name: git.upstream, ahead: num(git.ahead), behind: num(git.behind) };
  return null;
}

/** 晶片上的名稱：分支；分離的 HEAD 是「HEAD 1a2b3c4」 */
export function gitLabel(git) {
  if (!git) return '';
  if (git.branch) return git.branch;
  return git.head ? 'HEAD ' + git.head : 'HEAD';
}

/** 時:分（本機時間） */
function clock(ms) {
  if (!Number.isFinite(ms) || ms <= 0) return '';
  const d = new Date(ms);
  return String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0');
}

/**
 * 晶片要顯示的東西；git 是 null（不是 repo、還沒讀到）回 null。
 * { label, tone: clean／warn／danger, badges: [{kind, text, title}], rows: [{key, label, value, tone}], summary, title }
 * 徽章依序：進行中的操作、衝突、未提交、未推送（↑）、未拉取（↓；遠端有還沒下載的提交時加「+」）、上游已刪除。預設分支差異只放中性明細。
 */
export function gitView(git) {
  if (!git || typeof git !== 'object') return null;
  const badges = [];
  const rows = [];
  const target = compareTarget(git);
  const conflicts = num(git.conflicts);
  const dirty = num(git.changed) + num(git.untracked);
  const op = git.operation && OPS[git.operation] ? git.operation : null;

  // 分支與上游
  const branchText = git.branch
    ? git.branch + (git.upstream ? ' → ' + git.upstream + (git.upstreamGone ? '（已刪除）' : '') : '（未設定上游）')
    : '分離的 HEAD' + (git.head ? '（' + git.head + '）' : '');
  rows.push({ key: 'branch', label: '分支', value: branchText, tone: git.upstreamGone ? 'warn' : null });

  if (op) {
    badges.push({ kind: 'op', text: OP_SHORT[op], title: OPS[op] + '還沒完成' });
    rows.push({ key: 'op', label: '進行中', value: OPS[op] + '還沒完成', tone: 'danger' });
  }
  if (conflicts) {
    badges.push({ kind: 'conflict', text: '衝突 ' + conflicts, title: conflicts + ' 個檔案有合併衝突還沒解決' });
    rows.push({ key: 'conflict', label: '衝突', value: conflicts + ' 個檔案還沒解決', tone: 'danger' });
  }

  if (dirty) {
    const parts = [];
    if (num(git.staged)) parts.push('已暫存 ' + num(git.staged));
    if (num(git.unstaged)) parts.push('未暫存 ' + num(git.unstaged));
    if (num(git.untracked)) parts.push('未追蹤 ' + num(git.untracked));
    const detail = parts.length ? '（' + parts.join('、') + '）' : '';
    badges.push({ kind: 'dirty', text: '●' + dirty, title: dirty + ' 個檔案未提交' + detail });
    rows.push({ key: 'dirty', label: '未提交', value: dirty + ' 個檔案' + detail, tone: 'warn' });
  } else {
    rows.push({ key: 'dirty', label: '未提交', value: '沒有', tone: null });
  }

  if (target) {
    if (target.ahead) {
      badges.push({ kind: 'ahead', text: '↑' + target.ahead, title: target.ahead + ' 個提交未推送到 ' + target.name });
    }
    rows.push({ key: 'ahead', label: '未推送', value: target.ahead ? target.ahead + ' 個提交' : '沒有', tone: target.ahead ? 'warn' : null });

    const fresh = git.remoteNew === true;
    if (target.behind || fresh) {
      const title = [target.behind ? target.name + ' 有 ' + target.behind + ' 個提交還沒拉取' : '',
        fresh ? '遠端還有新的提交沒下載（git fetch／git pull）' : ''].filter(Boolean).join('；');
      badges.push({ kind: 'behind', text: '↓' + (target.behind || '') + (fresh ? '+' : ''), title });
    }
    const behindText = (target.behind ? target.behind + ' 個提交' : '沒有') + (fresh ? '；遠端還有新的提交沒下載' : '');
    rows.push({ key: 'behind', label: '未拉取', value: behindText, tone: target.behind || fresh ? 'warn' : null });
  } else {
    const reason = git.upstreamGone ? '上游分支已刪除' : git.upstream ? '尚未取得上游差異' : git.branch ? '未設定上游' : '分離的 HEAD，沒有上游';
    rows.push({ key: 'sync', label: '同步狀態', value: reason + '，無法判定推送／拉取狀態', tone: null });
    if (!git.upstream && git.base && git.remoteNew === true)
      rows.push({ key: 'remote', label: '遠端變更', value: git.base + ' 已變更，尚未更新本機追蹤資料', tone: null });
  }

  // 自動拉取（宿主的 GitAutoPullRules）：拉取中、剛拉完、這次不拉的原因、失敗的原因
  const ap = git.autoPull && typeof git.autoPull === 'object' && typeof git.autoPull.text === 'string' && git.autoPull.text ? git.autoPull : null;
  if (ap) {
    const at = ap.state === 'pulled' || ap.state === 'failed' ? clock(ap.at) : '';
    rows.push({ key: 'pull', label: '自動拉取', value: ap.text + (at ? '（' + at + '）' : ''), tone: ap.state === 'failed' ? 'warn' : null });
  }

  // 提交歷史有差異不代表需要合併；長期獨立分支、功能分支與 cherry-pick 都可能有不同歷史。
  if (git.base && Number.isFinite(git.baseAhead) && Number.isFinite(git.baseBehind)) {
    rows.push({ key: 'base', label: '與預設分支差異', value: git.base + '：本分支獨有 ' + num(git.baseAhead)
      + ' 個提交，預設分支獨有 ' + num(git.baseBehind) + ' 個提交；差異不代表需要合併', tone: null });
  }
  if (git.upstreamGone) {
    badges.push({ kind: 'gone', text: '上游已刪除', title: '上游分支 ' + git.upstream + ' 已經不在了（可能已合併並刪除）' });
  }
  if (num(git.stashes)) rows.push({ key: 'stash', label: 'stash', value: num(git.stashes) + ' 筆', tone: null });

  const checked = clock(git.checkedAt);
  const remoteAt = clock(git.remoteCheckedAt);
  if (checked) rows.push({ key: 'time', label: '檢查時間', value: checked + (remoteAt ? '（遠端 ' + remoteAt + '）' : git.remote ? '（遠端還沒查）' : ''), tone: null });

  const tone = op || conflicts ? 'danger' : badges.length ? 'warn' : 'clean';
  const summary = badges.length ? badges.map((b) => b.title).join('，')
    : target ? '工作目錄乾淨，與上游同步' : '工作目錄乾淨；同步狀態未確認';
  const title = ['Git：' + (git.root || '')]
    .concat(rows.filter((r) => r.key !== 'time').map((r) => r.label + '：' + r.value))
    .concat(tone === 'clean' ? [summary] : [])
    .concat(['點一下看明細或重新檢查'])
    .join('\n');
  return { label: gitLabel(git), tone, badges, rows, summary, title };
}

/** 晶片內容的指紋（沒變就不重畫） */
export function gitKey(git) {
  return git && typeof git === 'object' ? JSON.stringify(git) : '';
}
