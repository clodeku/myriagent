// 對話工作資料夾 git 晶片的純規則（src/Myriagent/Web/gitrules.js）：和誰比、徽章、色調、明細與說明文字，
// 以及 gitrules.js 沒有 import、沒有 HTML 寫入點。規則模組沒有 import，以 data: URL 匯入（同 rules.test.mjs）。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const webDir = path.resolve(here, '..', '..', 'src', 'Myriagent', 'Web');
const source = readFileSync(path.join(webDir, 'gitrules.js'), 'utf8');
const G = await import('data:text/javascript;base64,' + Buffer.from(source + '\n// gitrules.test\n', 'utf8').toString('base64'));

const git = (o = {}) => Object.assign({
  root: 'C:\\repo\\proj', branch: 'main', head: '1a2b3c4', upstream: 'origin/main', upstreamGone: false, ahead: 0, behind: 0,
  base: null, baseAhead: null, baseBehind: null, changed: 0, staged: 0, unstaged: 0, untracked: 0, conflicts: 0, stashes: 0,
  operation: null, remote: 'origin', remoteNew: false, remoteCheckedAt: null, checkedAt: null,
}, o);
const kinds = (v) => v.badges.map((b) => b.kind);
const texts = (v) => v.badges.map((b) => b.text);
const row = (v, key) => v.rows.find((r) => r.key === key);

test('沒有 git（不是 repo）時沒有晶片', () => {
  assert.equal(G.gitView(null), null);
  assert.equal(G.gitView(undefined), null);
  assert.equal(G.gitView('x'), null);
  assert.equal(G.gitKey(null), '');
});

test('乾淨且與上游同步：只有分支、色調 clean，說明同步狀態', () => {
  const v = G.gitView(git());
  assert.equal(v.label, 'main');
  assert.equal(v.tone, 'clean');
  assert.deepEqual(v.badges, []);
  assert.equal(row(v, 'dirty').value, '沒有');
  assert.equal(row(v, 'ahead').value, '沒有');
  assert.match(v.title, /^Git：C:\\repo\\proj\n分支：main → origin\/main\n/);
  assert.match(v.title, /工作目錄乾淨，與上游同步/);
  assert.equal(v.summary, '工作目錄乾淨，與上游同步');
});

test('未提交、未推送、未拉取、遠端有新提交：徽章依序、色調 warn', () => {
  const v = G.gitView(git({ changed: 3, staged: 1, unstaged: 2, untracked: 2, ahead: 2, behind: 1, remoteNew: true }));
  assert.deepEqual(kinds(v), ['dirty', 'ahead', 'behind']);
  assert.deepEqual(texts(v), ['●5', '↑2', '↓1+']);
  assert.equal(v.tone, 'warn');
  assert.equal(row(v, 'dirty').value, '5 個檔案（已暫存 1、未暫存 2、未追蹤 2）');
  assert.equal(v.badges[1].title, '2 個提交未推送到 origin/main');
  assert.equal(v.badges[2].title, 'origin/main 有 1 個提交還沒拉取；遠端還有新的提交沒下載（git fetch／git pull）');
  assert.equal(row(v, 'behind').value, '1 個提交；遠端還有新的提交沒下載');
});

test('只有遠端有新提交（還沒 fetch）：↓+', () => {
  const v = G.gitView(git({ remoteNew: true }));
  assert.deepEqual(texts(v), ['↓+']);
  assert.equal(v.tone, 'warn');
});

test('衝突與進行中的操作：排在最前面、色調 danger', () => {
  const v = G.gitView(git({ conflicts: 2, changed: 2, unstaged: 2, operation: 'merge' }));
  assert.deepEqual(kinds(v), ['op', 'conflict', 'dirty']);
  assert.deepEqual(texts(v).slice(0, 2), ['合併中', '衝突 2']);
  assert.equal(v.tone, 'danger');
  assert.equal(row(v, 'op').tone, 'danger');
  assert.equal(G.gitView(git({ operation: 'rebase' })).tone, 'danger');
  assert.equal(G.gitView(git({ operation: 'unknown' })).tone, 'clean', '不認得的操作不顯示');
});

test('分離的 HEAD：只顯示預設分支差異，不推斷提交是否推送過', () => {
  const v = G.gitView(git({ branch: null, upstream: null, base: 'origin/main', baseAhead: 3, baseBehind: 4, remoteNew: false }));
  assert.equal(v.label, 'HEAD 1a2b3c4');
  assert.deepEqual(texts(v), []);
  assert.equal(v.tone, 'clean');
  assert.equal(row(v, 'branch').value, '分離的 HEAD（1a2b3c4）');
  assert.equal(row(v, 'ahead'), undefined);
  assert.equal(row(v, 'behind'), undefined);
  assert.match(row(v, 'base').value, /本分支獨有 3 個提交，預設分支獨有 4 個提交/);
  assert.equal(row(v, 'sync').value, '分離的 HEAD，沒有上游，無法判定推送／拉取狀態');
  assert.equal(v.summary, '工作目錄乾淨；同步狀態未確認');
  assert.equal(G.gitLabel(git({ branch: null, head: null })), 'HEAD');
});

test('功能分支：預設分支差異放中性明細，不當成待合併警告', () => {
  const v = G.gitView(git({ branch: 'feat', upstream: 'origin/feat', base: 'origin/main', baseAhead: 4, baseBehind: 0 }));
  assert.deepEqual(kinds(v), []);
  assert.equal(v.tone, 'clean');
  assert.equal(row(v, 'base').value, 'origin/main：本分支獨有 4 個提交，預設分支獨有 0 個提交；差異不代表需要合併');
  assert.equal(row(v, 'base').tone, null);
  assert.equal(row(v, 'merge'), undefined);
  // 沒設追蹤關係不能證明從未推送，也不能拿預設分支當成上游。
  const local = G.gitView(git({ branch: 'feat', upstream: null, base: 'origin/main', baseAhead: 2, baseBehind: 0 }));
  assert.deepEqual(kinds(local), []);
  assert.equal(row(local, 'branch').value, 'feat（未設定上游）');
  assert.equal(row(local, 'sync').value, '未設定上游，無法判定推送／拉取狀態');
  assert.doesNotMatch(local.title, /還沒推送過|未合併/);
});

test('上游已刪除：保留提示，不把預設分支差異當成未拉取', () => {
  const v = G.gitView(git({ branch: 'feat', upstream: 'origin/feat', upstreamGone: true, ahead: null, behind: null, base: 'origin/main', baseAhead: 0, baseBehind: 2 }));
  assert.deepEqual(kinds(v), ['gone']);
  assert.equal(row(v, 'branch').value, 'feat → origin/feat（已刪除）');
  assert.equal(row(v, 'branch').tone, 'warn');
  assert.equal(v.tone, 'warn');
  assert.equal(row(v, 'behind'), undefined);
  assert.equal(row(v, 'sync').value, '上游分支已刪除，無法判定推送／拉取狀態');
  assert.match(row(v, 'base').value, /預設分支獨有 2 個提交/);
});

test('沒有遠端：不顯示未推送未拉取，明細說明無法判定', () => {
  const v = G.gitView(git({ upstream: null, remote: null, remoteNew: null }));
  assert.deepEqual(v.badges, []);
  assert.equal(row(v, 'ahead'), undefined);
  assert.equal(row(v, 'sync').value, '未設定上游，無法判定推送／拉取狀態');
  assert.equal(row(v, 'behind'), undefined);
});

test('長期分支與上游同步：1579／108 個歷史差異不產生警告', () => {
  const s = git({ branch: 'projects/long-lived', upstream: 'origin/projects/long-lived', base: 'origin/develop-5.26', baseAhead: 1579, baseBehind: 108 });
  const v = G.gitView(s);
  assert.equal(v.tone, 'clean');
  assert.deepEqual(v.badges, []);
  assert.equal(row(v, 'ahead').value, '沒有');
  assert.equal(row(v, 'behind').value, '沒有');
  assert.equal(row(v, 'base').value, 'origin/develop-5.26：本分支獨有 1579 個提交，預設分支獨有 108 個提交；差異不代表需要合併');
  assert.doesNotMatch(v.title, /未合併|還沒合併/);
  // 真正的未提交與上游同步差異仍要警告，不因分支名稱而略過。
  const changed = G.gitView({ ...s, changed: 1, unstaged: 1, ahead: 2, behind: 3 });
  assert.deepEqual(texts(changed), ['●1', '↑2', '↓3']);
  assert.equal(changed.tone, 'warn');
  assert.deepEqual(texts(G.gitView({ ...s, remoteNew: true })), ['↓+']);
});

test('預設分支只有落後或沒有差異仍提供中性明細，未知數字不冒充零', () => {
  for (const [ahead, behind] of [[0, 108], [0, 0]]) {
    const v = G.gitView(git({ base: 'origin/main', baseAhead: ahead, baseBehind: behind }));
    assert.equal(v.tone, 'clean');
    assert.equal(row(v, 'base').tone, null);
    assert.match(row(v, 'base').value, new RegExp('預設分支獨有 ' + behind + ' 個提交'));
  }
  assert.equal(row(G.gitView(git({ base: 'origin/main', baseAhead: 4, baseBehind: null })), 'base'), undefined);
  const unknown = G.gitView(git({ behind: null, base: 'origin/main', baseAhead: 4, baseBehind: 1 }));
  assert.equal(row(unknown, 'behind'), undefined);
  assert.equal(row(unknown, 'sync').value, '尚未取得上游差異，無法判定推送／拉取狀態');
  assert.equal(unknown.summary, '工作目錄乾淨；同步狀態未確認');
});

test('沒有上游時遠端預設分支的變更只列明細，不顯示未拉取徽章', () => {
  const v = G.gitView(git({ upstream: null, base: 'origin/main', baseAhead: 3, baseBehind: 4, remoteNew: true }));
  assert.deepEqual(v.badges, []);
  assert.equal(row(v, 'remote').value, 'origin/main 已變更，尚未更新本機追蹤資料');
  assert.equal(row(v, 'remote').tone, null);
  assert.equal(row(v, 'behind'), undefined);
});

test('stash 與檢查時間只在明細', () => {
  const at = new Date(2026, 9, 23, 9, 5).getTime();
  const v = G.gitView(git({ stashes: 2, checkedAt: at, remoteCheckedAt: at }));
  assert.equal(row(v, 'stash').value, '2 筆');
  assert.equal(row(v, 'time').value, '09:05（遠端 09:05）');
  assert.ok(!/檢查時間/.test(v.title), '說明不放時間（不會跟著更新）');
  assert.equal(row(G.gitView(git({ checkedAt: at })), 'time').value, '09:05（遠端還沒查）');
  assert.equal(v.tone, 'clean');
});

test('gitrules.js 沒有 import、沒有 HTML 寫入點', () => {
  assert.ok(!/^\s*import\s/m.test(source));
  assert.ok(!/innerHTML|outerHTML|insertAdjacentHTML|document\.write/.test(source));
});

test('自動拉取：一列說明；剛拉完與失敗帶時間、失敗標 warn、不拉的原因不加時間', () => {
  const at = new Date(2026, 9, 23, 11, 40).getTime();
  const pulled = G.gitView(git({ autoPull: { state: 'pulled', text: '已從 origin/main 快轉拉取 2 個提交', at } }));
  assert.deepEqual(row(pulled, 'pull'), { key: 'pull', label: '自動拉取', value: '已從 origin/main 快轉拉取 2 個提交（11:40）', tone: null });
  assert.equal(pulled.tone, 'clean', '拉完了、沒有其他要處理的事');
  assert.match(pulled.title, /自動拉取：已從 origin\/main 快轉拉取 2 個提交（11:40）/);

  const failed = G.gitView(git({ behind: 1, autoPull: { state: 'failed', text: '失敗：fatal: x', at } }));
  assert.deepEqual(row(failed, 'pull'), { key: 'pull', label: '自動拉取', value: '失敗：fatal: x（11:40）', tone: 'warn' });

  const skipped = G.gitView(git({ behind: 1, changed: 1, unstaged: 1, autoPull: { state: 'skipped', text: '這次不拉：有 1 個已追蹤的檔案還沒提交', at } }));
  assert.equal(row(skipped, 'pull').value, '這次不拉：有 1 個已追蹤的檔案還沒提交');
  assert.equal(row(skipped, 'pull').tone, null);

  assert.equal(row(G.gitView(git({ autoPull: null })), 'pull'), undefined);
  assert.equal(row(G.gitView(git({ autoPull: { state: 'pulled' } })), 'pull'), undefined, '沒有文字不顯示');
  assert.equal(row(G.gitView(git({ autoPull: 'x' })), 'pull'), undefined);
});
