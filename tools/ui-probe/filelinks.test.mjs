// 生成檔案連結的路由與預覽規則；node --test filelinks.test.mjs。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../../src/Myriagent/Web/filelinkrules.js', import.meta.url), 'utf8');
const { fileLink, filePreviewKind, localMediaUrl, isOfficeOrPdf, fileRefAction } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));
const pptx = 'C:\\repo\\slides\\watercolor_final\\output\\年度簡報_水彩版.pptx';

test('桌面與網頁連結保留中文、空白、百分比、加號與井字號檔名', () => {
  for (const path of [pptx, 'C:\\成果\\a b+20% #1.jpg']) {
    assert.deepEqual(fileLink('https://myriagent.local/open?path=' + encodeURIComponent(path)), { path, remote: false });
    assert.deepEqual(fileLink('#local-file=' + encodeURIComponent(path)), { path, remote: true });
    assert.equal(new URL(localMediaUrl(path)).searchParams.get('path'), path);
  }
});

test('一般網址、不合法標記與 UNC 不會當成本機檔案', () => {
  for (const href of [null, '', '#section', '#local-file=%ZZ', '#local-file=' + encodeURIComponent('\\\\server\\share\\x.pptx'),
    '#local-file=' + encodeURIComponent('javascript:alert(1)'), '#local-file=' + encodeURIComponent('C:\\a\n.pptx'),
    'https://example.com/open?path=C%3A%5Ca.pptx', 'https://myriagent.local.evil/open?path=C%3A%5Ca.pptx',
    'https://user@myriagent.local/open?path=C%3A%5Ca.pptx', 'http://myriagent.local/open?path=C%3A%5Ca.pptx',
    'https://myriagent.local/open?path=//server/share/a.pptx', 'https://myriagent.local/media?path=C%3A%5Ca.jpg']) {
    assert.equal(fileLink(href), null, String(href));
  }
});

test('只有宿主支援的圖片與影片進入預覽，PowerPoint 與 SVG 提供檔案操作', () => {
  for (const ext of ['png', 'JPG', 'jpeg', 'gif', 'webp', 'bmp', 'ico', 'avif']) assert.equal(filePreviewKind('C:\\a.' + ext), 'image');
  for (const ext of ['mp4', 'm4v', 'webm', 'mov']) assert.equal(filePreviewKind('C:\\a.' + ext), 'video');
  for (const path of [pptx, 'a.svg', 'a.jpg.exe', 'a.html', null]) assert.equal(filePreviewKind(path), null);
});

test('Word、Excel、PowerPoint、PDF 交給裝置自己的程式（2026-11-05）：桌面看其他電腦的對話下載後開、瀏覽器版電腦直接下載、手機平板給開啟鈕', () => {
  for (const name of ['a.pdf', 'B.DOCX', 'c.doc', 'd.xlsx', 'e.xls', 'f.pptx', 'g.ppt', 'h.odt', 'i.ods', 'j.odp', 'k.rtf', pptx]) {
    assert.equal(isOfficeOrPdf(name), true, name);
  }
  for (const name of ['a.txt', 'b.csv', 'c.png', 'd.pdf.exe', 'e.docx.lnk', '', null]) assert.equal(isOfficeOrPdf(name), false, String(name));
  assert.equal(fileRefAction({ name: '季報.xlsx', hub: false }), 'peerOpen');
  assert.equal(fileRefAction({ name: '季報.xlsx', hub: true, handheld: false }), 'download');
  assert.equal(fileRefAction({ name: '季報.xlsx', hub: true, handheld: true }), 'deviceOpen');
  assert.equal(fileRefAction({ name: 'notes.txt', hub: true, handheld: false }), null, '其他檔案照舊');
});
