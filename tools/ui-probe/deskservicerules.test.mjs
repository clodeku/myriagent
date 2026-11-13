// 遠端桌面的鎖定畫面支援在設定頁的純規則（src/Myriagent/Web/deskservicerules.js；web-protocol 的 deskService）
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { deskServiceShown, deskServiceVersion, deskServiceSummary, deskServiceProbeText, deskServiceConfirm } from '../../src/Myriagent/Web/deskservicerules.js';

const base = {
  type: 'deskService', offered: true, known: true, installed: false, running: false, version: null, build: null,
  bundled: { version: '0.2.950', build: 'abc1234' }, canInstall: true, update: false, system: null, busy: null, error: null,
};
const s = (o) => Object.assign({}, base, o);
const acts = (v) => v.actions.map((a) => a.act + ':' + a.label);

test('要不要顯示：宿主沒送、offered 不是 true 都不顯示', () => {
  assert.equal(deskServiceShown(null), false);
  assert.equal(deskServiceShown(undefined), false);
  assert.equal(deskServiceShown(s({ offered: false })), false);
  assert.equal(deskServiceShown(s({ offered: 'yes' })), false);
  assert.equal(deskServiceShown(s({})), true);
});

test('版本的寫法：發佈版帶提交、開發版標出來', () => {
  assert.equal(deskServiceVersion('0.2.950', 'abc1234'), '0.2.950（abc1234）');
  assert.equal(deskServiceVersion('0.2.0', 'dev'), '0.2.0（開發版）');
  assert.equal(deskServiceVersion('0.2.0', null), '0.2.0（開發版）');
  assert.equal(deskServiceVersion(null, 'abc1234'), '?（abc1234）');
});

test('還沒查過：檢查中、沒有按鈕', () => {
  const v = deskServiceSummary(s({ known: false, busy: 'check' }));
  assert.equal(v.label, '檢查中…');
  assert.deepEqual(v.actions, []);
  assert.equal(deskServiceSummary(null).label, '檢查中…');
});

test('未安裝：可以安裝；app 沒帶服務檔時說明、沒有按鈕', () => {
  const v = deskServiceSummary(s({}));
  assert.equal(v.label, '未安裝');
  assert.deepEqual(acts(v), ['install:安裝…']);
  const none = deskServiceSummary(s({ canInstall: false, bundled: null }));
  assert.deepEqual(none.actions, []);
  assert.match(none.detail, /不能安裝/);
});

test('進行中：沒有按鈕，安裝與移除提醒按 UAC；已安裝時的 install 是更新', () => {
  const installing = deskServiceSummary(s({ busy: 'install' }));
  assert.equal(installing.label, '安裝中…');
  assert.equal(installing.tone, 'st-busy');
  assert.deepEqual(installing.actions, []);
  assert.match(installing.detail, /按「是」/);
  assert.equal(deskServiceSummary(s({ busy: 'install', installed: true })).label, '更新中…');
  assert.equal(deskServiceSummary(s({ busy: 'uninstall', installed: true })).label, '移除中…');
  assert.equal(deskServiceSummary(s({ busy: 'check', installed: true, running: true })).label, '檢查中…');
  // 第一次按安裝時還沒查過也一樣
  assert.equal(deskServiceSummary(s({ known: false, busy: 'install' })).label, '安裝中…');
});

test('已安裝、執行中：綠色、只有移除', () => {
  const v = deskServiceSummary(s({ installed: true, running: true, system: true, version: '0.2.950', build: 'abc1234' }));
  assert.equal(v.tone, 'st-ready');
  assert.equal(v.label, '執行中');
  assert.match(v.detail, /0\.2\.950（abc1234）/);
  assert.deepEqual(acts(v), ['uninstall:移除…']);
});

test('已安裝但沒有回應：紅色、可以重新安裝或移除', () => {
  const v = deskServiceSummary(s({ installed: true, running: false, version: '0.2.950', build: 'abc1234' }));
  assert.equal(v.tone, 'st-error');
  assert.deepEqual(acts(v), ['install:重新安裝…', 'uninstall:移除…']);
  assert.deepEqual(acts(deskServiceSummary(s({ installed: true, running: false, canInstall: false }))), ['uninstall:移除…']);
});

test('有更新：說明兩邊的版本，可以更新或移除；app 沒帶服務檔就不提更新', () => {
  const v = deskServiceSummary(s({ installed: true, running: true, system: true, version: '0.2.940', build: '0123456', update: true }));
  assert.equal(v.label, '有更新');
  assert.match(v.detail, /0\.2\.940（0123456）/);
  assert.match(v.detail, /0\.2\.950（abc1234）/);
  assert.deepEqual(acts(v), ['install:更新…', 'uninstall:移除…']);
  assert.equal(deskServiceSummary(s({ installed: true, running: true, system: true, update: true, canInstall: false })).label, '執行中');
});

test('不是系統權限（開發用的前景模式）：標出來', () => {
  const v = deskServiceSummary(s({ installed: true, running: true, system: false, version: '0.2.0', build: 'dev' }));
  assert.equal(v.label, '不是系統權限');
  assert.match(v.detail, /看不到鎖定畫面/);
});

test('服務看到的輸入桌面：使用者的桌面、安全桌面、開不了', () => {
  assert.equal(deskServiceProbeText(s({})), null);
  assert.match(deskServiceProbeText(s({ probe: { desktop: 'Default', secure: false, error: 0 } })), /Default（使用者的桌面）/);
  assert.match(deskServiceProbeText(s({ probe: { desktop: 'Winlogon', secure: true, error: 0 } })), /Winlogon（鎖定或系統管理員確認畫面）/);
  assert.match(deskServiceProbeText(s({ probe: { desktop: null, secure: false, error: 5 } })), /錯誤碼 5/);
});

test('確認框：安裝說明是系統權限的服務與 UAC；已安裝時是更新；移除說明之後還能用遠端桌面', () => {
  const install = deskServiceConfirm('install', s({}));
  assert.equal(install.title, '安裝鎖定畫面支援');
  assert.equal(install.confirmLabel, '安裝');
  assert.match(install.message, /系統權限/);
  assert.match(install.detail, /UAC/);
  assert.equal(deskServiceConfirm('install', s({ installed: true })).confirmLabel, '更新');
  const remove = deskServiceConfirm('uninstall', s({ installed: true }));
  assert.equal(remove.confirmLabel, '移除');
  assert.match(remove.message, /照常可用/);
  assert.match(remove.detail, /UAC/);
});
