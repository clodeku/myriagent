// 版本頁純規則的單元測試（node:test，不開瀏覽器、不啟動伺服器）：(cd tools/hub-e2e && node --test releaserules.test.mjs)
// src/MyriagentHub/wwwroot/releaserules.js 沒有 import，以 data: URL 匯入（同 authrules.test.mjs）。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const file = path.resolve(here, '..', '..', 'src', 'MyriagentHub', 'wwwroot', 'releaserules.js');
const source = readFileSync(file, 'utf8');
const R = await import('data:text/javascript;base64,' + Buffer.from(source, 'utf8').toString('base64'));

test('releaserules_沒有import也不碰DOM_匯出正好六個', () => {
  // 註解拿掉再看（說明裡會寫到 location.hash）
  const code = source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/.*$/gm, '');
  assert.doesNotMatch(code, /^\s*import\s/m);
  assert.doesNotMatch(code, /document|window|location|innerHTML|fetch\(/);
  assert.deepEqual(Object.keys(R).sort(), ['appPhaseText', 'formatBytes', 'isReleasesHash', 'releaseStateText', 'shortCommit', 'withdrawConfirmText']);
});

test('formatBytes_1024以下是位元組', () => {
  assert.equal(R.formatBytes(0), '0 B');
  assert.equal(R.formatBytes(1), '1 B');
  assert.equal(R.formatBytes(1023), '1023 B');
  assert.equal(R.formatBytes(512.7), '512 B', '小數捨去');
});

test('formatBytes_以1024進位_小數一位', () => {
  assert.equal(R.formatBytes(1024), '1.0 KB');
  assert.equal(R.formatBytes(4096), '4.0 KB');
  assert.equal(R.formatBytes(1536), '1.5 KB');
  assert.equal(R.formatBytes(1024 * 1024 - 1), '1024.0 KB', '還沒滿 1 MB');
  assert.equal(R.formatBytes(1024 * 1024), '1.0 MB');
  assert.equal(R.formatBytes(52 * 1024 * 1024 + 300 * 1024), '52.3 MB');
  assert.equal(R.formatBytes(3 * 1024 ** 3), '3.0 GB');
  assert.equal(R.formatBytes(2 * 1024 ** 4), '2.0 TB');
  assert.equal(R.formatBytes(4096 * 1024 ** 4), '4096.0 TB', 'TB 是最大的單位');
});

test('formatBytes_不是有限的非負數回破折號', () => {
  for (const v of [-1, NaN, Infinity, -Infinity, '4096', null, undefined, {}, []]) {
    assert.equal(R.formatBytes(v), '—', String(v));
  }
});

test('releaseStateText_三種狀態與不認得的值', () => {
  assert.equal(R.releaseStateText('available'), '可用');
  assert.equal(R.releaseStateText('withdrawn'), '已撤回');
  assert.equal(R.releaseStateText('pruned'), '已清除（檔案已刪）');
  assert.equal(R.releaseStateText('Available'), '不明的狀態（Available）', '大小寫要相符');
  assert.equal(R.releaseStateText('toString'), '不明的狀態（toString）', '不吃原型鏈上的名稱');
  assert.equal(R.releaseStateText(''), '不明的狀態（）');
  assert.equal(R.releaseStateText(null), '—');
  assert.equal(R.releaseStateText(undefined), '—');
  assert.equal(R.releaseStateText(1), '—');
});

test('appPhaseText_八個階段', () => {
  assert.equal(R.appPhaseText('idle'), '沒有進行中的更新');
  assert.equal(R.appPhaseText('downloading'), '下載中');
  assert.equal(R.appPhaseText('staged'), '已下載，等待套用');
  assert.equal(R.appPhaseText('waiting'), '等待閒置後套用');
  assert.equal(R.appPhaseText('draining'), '等待執行中的工作結束');
  assert.equal(R.appPhaseText('applying'), '套用中');
  assert.equal(R.appPhaseText('rolledBack'), '新版啟動失敗，已退回');
  assert.equal(R.appPhaseText('blocked'), '無法更新（缺執行階段、空間不足或未啟用）');
});

test('appPhaseText_不認得的值', () => {
  assert.equal(R.appPhaseText('rolledback'), '不明的狀態（rolledback）', '大小寫要相符');
  assert.equal(R.appPhaseText('exploded'), '不明的狀態（exploded）');
  assert.equal(R.appPhaseText('constructor'), '不明的狀態（constructor）', '不吃原型鏈上的名稱');
  assert.equal(R.appPhaseText(null), '—');
  assert.equal(R.appPhaseText(undefined), '—');
  assert.equal(R.appPhaseText(3), '—');
});

test('shortCommit_前7碼', () => {
  assert.equal(R.shortCommit('ab12cd3' + '0'.repeat(33)), 'ab12cd3');
  assert.equal(R.shortCommit('ab12cd3'), 'ab12cd3');
  assert.equal(R.shortCommit('ab1'), 'ab1', '不到 7 碼照原樣');
  assert.equal(R.shortCommit(''), '—');
  assert.equal(R.shortCommit(null), '—');
  assert.equal(R.shortCommit(undefined), '—');
  assert.equal(R.shortCommit(1234567), '—');
});

test('isReleasesHash_只認井號releases', () => {
  assert.equal(R.isReleasesHash('#releases'), true);
  for (const h of ['', '#', '#release', '#releasesx', '#releases=0.2.1', '#Releases', 'releases', '#pair', null, undefined]) {
    assert.equal(R.isReleasesHash(h), false, String(h));
  }
});

test('withdrawConfirmText_撤回目前提供的版本_改提供前一個可用的版本', () => {
  const text = R.withdrawConfirmText('0.2.12-aaaaaaa', '0.2.12-aaaaaaa');
  assert.match(text, /^撤回 0\.2\.12-aaaaaaa？\n\n/);
  assert.match(text, /它是目前提供的版本，改提供前一個可用的版本（沒有就不提供更新）/);
  assert.doesNotMatch(text, /目前提供的版本不變/);
  assert.match(text, /等著套用的電腦仍可能套用它/);
  assert.match(text, /撤回之後不能復原，同名的版本也永遠不能再收。$/);
});

test('withdrawConfirmText_撤回不是目前提供的版本_目前提供的版本不變', () => {
  // 撤回鈕在每一列可用的版本都有：撤回較舊的可用版本時，伺服器的 latest 不變（AppReleaseRules.PickLatest 只看可用版本中最新的）
  for (const latest of ['0.2.13-bbbbbbb', null, undefined]) {
    const text = R.withdrawConfirmText('0.2.12-aaaaaaa', latest);
    assert.match(text, /^撤回 0\.2\.12-aaaaaaa？\n\n/, String(latest));
    assert.match(text, /它不是目前提供的版本，目前提供的版本不變/, String(latest));
    assert.doesNotMatch(text, /前一個可用的版本/, String(latest));
    assert.match(text, /等著套用的電腦仍可能套用它/, String(latest));
    assert.match(text, /撤回之後不能復原，同名的版本也永遠不能再收。$/, String(latest));
  }
  assert.match(R.withdrawConfirmText(null, null), /目前提供的版本不變/, '沒有版本名稱不算目前提供的版本');
});

test('auth.js只靜態匯入authrules_版本頁的兩個模組以動態import載入', () => {
  // 版本頁的模組載入失敗（或 initReleases 丟例外）不能拖垮登入頁與配對核准頁：
  // 靜態 import 的模組一個失敗，整個 auth.js 就不執行。實際載入失敗時的行為由 hub-e2e.mjs 第 12.5 步在瀏覽器裡驗
  const authFile = path.resolve(here, '..', '..', 'src', 'MyriagentHub', 'wwwroot', 'auth.js');
  const auth = readFileSync(authFile, 'utf8').replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/.*$/gm, '');
  const staticImports = [...auth.matchAll(/^\s*import\s+(?:[^'";]*?\s+from\s+)?'([^']+)'/gm)].map((m) => m[1]);
  assert.deepEqual(staticImports, ['/authrules.js']);
  const dynamicImports = [...auth.matchAll(/\bimport\(\s*'([^']+)'\s*\)/g)].map((m) => m[1]).sort();
  assert.deepEqual(dynamicImports, ['/releaserules.js', '/releases.js']);
});
