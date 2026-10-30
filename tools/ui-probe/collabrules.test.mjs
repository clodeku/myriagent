// 多人協作的純規則（Web/collabrules.js）：node --test collabrules.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  isCollaboratorViewer, cleanUserName, cleanDisplayName, passwordProblem, errorText, listOf, sharedWith, shareCount, statusText,
  authorLabel, resolvedLabel, shareIds,
} from '../../src/Myriagent/Web/collabrules.js';

const OWNER = { role: 'owner' };
const COLLAB = { role: 'collaborator', name: '小明', userName: 'ming' };
const STATE = {
  list: [
    { id: 'u1', userName: 'ming', displayName: '小明', disabled: false, online: true },
    { id: 'u2', userName: 'hua', displayName: '小華', disabled: true, online: false, lastLoginAt: 1000 },
    { id: 'u3', userName: 'new', displayName: '新人', disabled: false, online: false, lastLoginAt: null },
    null,
    { userName: 'no-id' },
  ],
  shares: { c1: ['u2', 'u1', 'gone'], c2: ['u1'], c3: [] },
};

test('isCollaboratorViewer：只有 role 是 collaborator 才算', () => {
  assert.equal(isCollaboratorViewer(COLLAB), true);
  assert.equal(isCollaboratorViewer(OWNER), false);
  assert.equal(isCollaboratorViewer(null), false);
  assert.equal(isCollaboratorViewer('collaborator'), false);
});

test('cleanUserName：同伺服器的 CollaboratorRules（小寫、3～32 字、英數字開頭、保留名稱）', () => {
  assert.equal(cleanUserName('  Ming.Lee  '), 'ming.lee');
  assert.equal(cleanUserName('a_b-c@x'), 'a_b-c@x');
  assert.equal(cleanUserName('ab'), null);
  assert.equal(cleanUserName('x'.repeat(33)), null);
  assert.equal(cleanUserName('_ming'), null);
  assert.equal(cleanUserName('ming lee'), null);
  assert.equal(cleanUserName('明明明'), null);
  assert.equal(cleanUserName('Admin'), null);
  assert.equal(cleanUserName('owner'), null);
  assert.equal(cleanUserName(null), null);
});

test('cleanDisplayName：空白摺成一個、1～32 個 Unicode 字元、不能有控制或格式字元', () => {
  assert.equal(cleanDisplayName('  小明   王 '), '小明 王');
  assert.equal(cleanDisplayName('   '), null);
  assert.equal(cleanDisplayName('名'.repeat(32)), '名'.repeat(32));
  assert.equal(cleanDisplayName('名'.repeat(33)), null);
  assert.equal(cleanDisplayName('😀'.repeat(32)), '😀'.repeat(32), '代理字元對算一個字');
  assert.equal(cleanDisplayName('小明' + String.fromCharCode(0x202e)), null, '由右至左覆寫');
  assert.equal(cleanDisplayName('小' + String.fromCharCode(0x200b) + '明'), null, '零寬空格');
  assert.equal(cleanDisplayName('a' + String.fromCharCode(7)), null);
});

test('passwordProblem：8～128 字、不能有控制字元', () => {
  assert.equal(passwordProblem('12345678'), null);
  assert.equal(passwordProblem('1234567'), 'password_too_short');
  assert.equal(passwordProblem(undefined), 'password_too_short');
  assert.equal(passwordProblem('x'.repeat(129)), 'password_too_long');
  assert.equal(passwordProblem('abcdefgh' + String.fromCharCode(10)), 'password_invalid');
});

test('errorText：認得的代碼都有說明，其他帶原代碼', () => {
  for (const code of ['invalid', 'user_name_taken', 'not_found', 'too_many', 'password_too_short', 'password_too_long', 'password_invalid', 'unavailable'])
    assert.ok(!errorText(code).includes('原因不明') && !errorText(code).includes(code), code);
  assert.match(errorText('weird'), /weird/);
});

test('listOf、sharedWith、shareCount：略過壞的項目，分享依清單的順序、不在清單的 id 不算', () => {
  assert.deepEqual(listOf(STATE).map((c) => c.id), ['u1', 'u2', 'u3']);
  assert.deepEqual(listOf(null), []);
  assert.deepEqual(sharedWith(STATE, 'c1').map((c) => c.id), ['u1', 'u2']);
  assert.deepEqual(sharedWith(STATE, 'c3'), []);
  assert.deepEqual(sharedWith(STATE, 'nope'), []);
  assert.deepEqual(sharedWith(null, 'c1'), []);
  assert.equal(shareCount(STATE, 'u1'), 2);
  assert.equal(shareCount(STATE, 'u3'), 0);
  assert.equal(shareCount(null, 'u1'), 0);
});

test('statusText：停用 > 在線 > 最後登入 > 還沒登入過', () => {
  assert.equal(statusText(STATE.list[0]), '在線');
  assert.equal(statusText(STATE.list[1]), '已停用');
  assert.equal(statusText({ ...STATE.list[1], disabled: false }, () => '3 天前'), '最後登入 3 天前');
  assert.equal(statusText(STATE.list[2]), '還沒登入過');
});

test('authorLabel：協作人送的標名稱；協作人看擁有者送的標「管理員」；擁有者看自己的不標', () => {
  const collabItem = { kind: 'User', author: '小明' };
  const ownerItem = { kind: 'User' };
  assert.equal(authorLabel(collabItem, OWNER), '協作人 小明');
  assert.equal(authorLabel(collabItem, null), '協作人 小明', '舊宿主沒有 viewer：當成擁有者');
  assert.equal(authorLabel(collabItem, COLLAB), '小明');
  assert.equal(authorLabel(ownerItem, OWNER), null);
  assert.equal(authorLabel(ownerItem, COLLAB), '管理員');
  assert.equal(authorLabel({ kind: 'User', source: 'remote' }, COLLAB), null, 'claude.ai 送的已經有自己的來源標示');
  assert.equal(authorLabel(null, COLLAB), null);
});

test('resolvedLabel：協作人回應的寫名稱，其他不寫', () => {
  assert.equal(resolvedLabel({ resolvedBy: '小明' }, OWNER), '協作人 小明 回應');
  assert.equal(resolvedLabel({ resolvedBy: '小明' }, COLLAB), '小明 回應');
  assert.equal(resolvedLabel({}, OWNER), null);
  assert.equal(resolvedLabel({ resolvedBy: '' }, OWNER), null);
});

test('shareIds：只留清單裡的、依清單的順序、去重', () => {
  assert.deepEqual(shareIds(STATE, ['u3', 'u1', 'u1', 'gone']), ['u1', 'u3']);
  assert.deepEqual(shareIds(STATE, null), []);
});
