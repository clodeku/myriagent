// 手機上的儲存與分享（Web/sharerules.js；2026-11-18）的純規則
import { test } from 'node:test';
import assert from 'node:assert/strict';
import * as R from '../../src/Myriagent/Web/sharerules.js';

test('shareableUrl_只收本站的媒體與檔案網址', () => {
  assert.equal(R.shareableUrl('/m/d_A-vid1'), true);
  assert.equal(R.shareableUrl('/t/k3a?s=160'), true);
  assert.equal(R.shareableUrl('/f/d_A-ref4'), true);
  assert.equal(R.shareableUrl('//evil.example/m/x'), false);
  assert.equal(R.shareableUrl('https://evil.example/m/x'), false);
  assert.equal(R.shareableUrl('/media?p=x'), false, '桌面的本機網址不走這裡');
  assert.equal(R.shareableUrl('data:image/png;base64,AA'), false);
  assert.equal(R.shareableUrl(null), false);
});

test('saveVia_只有瀏覽器版的iPhone_iPad而且能分享檔案才改走分享選單', () => {
  assert.equal(R.saveVia({ hub: true, appleTouch: true, canShareFiles: true }), 'share');
  assert.equal(R.saveVia({ hub: true, appleTouch: true, canShareFiles: false }), 'download', '舊的 iOS 照舊下載');
  assert.equal(R.saveVia({ hub: true, appleTouch: false, canShareFiles: true }), 'download', 'Android、電腦下載得了');
  assert.equal(R.saveVia({ hub: false, appleTouch: true, canShareFiles: true }), 'download', '桌面 app 不走這裡');
});

test('shareOffered_瀏覽器版的手機與平板能分享檔案才給', () => {
  assert.equal(R.shareOffered({ hub: true, handheld: true, canShareFiles: true }), true);
  assert.equal(R.shareOffered({ hub: true, handheld: false, canShareFiles: true }), false, '電腦的瀏覽器不給');
  assert.equal(R.shareOffered({ hub: true, handheld: true, canShareFiles: false }), false);
  assert.equal(R.shareOffered({ hub: false, handheld: true, canShareFiles: true }), false);
});

test('mimeFor_伺服器給的具體類型優先_否則照副檔名', () => {
  assert.equal(R.mimeFor('a.mp4', 'video/mp4'), 'video/mp4');
  assert.equal(R.mimeFor('a.mov', 'application/octet-stream'), 'video/quicktime');
  assert.equal(R.mimeFor('a.MP4', ''), 'video/mp4');
  assert.equal(R.mimeFor('畫面.png', 'image/png; charset=binary'), 'image/png');
  assert.equal(R.mimeFor('x', ''), 'application/octet-stream');
  assert.equal(R.mimeFor('季報.xlsx', null), 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet');
});

test('fileNameFor_去掉路徑與不能用的字元_沒有副檔名依類型補', () => {
  assert.equal(R.fileNameFor('操作錄影.mp4', 'video/mp4', 'video'), '操作錄影.mp4');
  assert.equal(R.fileNameFor('C:\\repo\\shot.png', 'image/png', 'image'), 'shot.png');
  assert.equal(R.fileNameFor('a:b?c*.txt', 'text/plain', 'file'), 'abc.txt');
  assert.equal(R.fileNameFor('', 'video/quicktime', 'video'), '影片.mov');
  assert.equal(R.fileNameFor(null, 'image/jpeg', 'image'), '圖片.jpg');
  assert.equal(R.fileNameFor('錄影', 'application/x-unknown', 'video'), '錄影', '不知道的類型不亂補');
  const long = R.fileNameFor('字'.repeat(200) + '.mp4', 'video/mp4', 'video');
  assert.ok(long.length <= 120 && long.endsWith('.mp4'));
});

test('progressText與readyHint', () => {
  assert.equal(R.progressText(0, 0), '準備中…');
  assert.equal(R.progressText(512, 0), '已下載 1 KB');
  assert.equal(R.progressText(5 * 1024 * 1024, 20 * 1024 * 1024), '5.0 MB／20.0 MB（25%）');
  assert.equal(R.progressText(30, 20), '1 KB／1 KB（100%）');
  assert.match(R.readyHint('save', 'video'), /儲存影片/);
  assert.match(R.readyHint('save', 'image'), /儲存影像/);
  assert.match(R.readyHint('save', 'file'), /儲存到檔案/);
  assert.match(R.readyHint('share', 'video'), /分享/);
});
