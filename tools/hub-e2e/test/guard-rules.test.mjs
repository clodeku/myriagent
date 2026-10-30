// lib/guard-rules.mjs 的單元測試（node:test）：正式站守門讀 guard-cases.json（C# 的 LocalHubTargetRulesTests 讀同一份）
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { checkExternalBase, isAllowedServerUrl, isExeStale, isProductionTarget, isUnderTemp } from '../lib/guard-rules.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const cases = JSON.parse(fs.readFileSync(path.join(here, 'guard-cases.json'), 'utf8'));

test('isAllowedServerUrl_共用測試表', () => {
  assert.ok(cases.allowedServerUrl.length >= 15);
  for (const c of cases.allowedServerUrl) assert.equal(isAllowedServerUrl(c.url), c.allowed, `${c.url}（${c.why}）`);
});

test('isAllowedServerUrl_計畫列的五個拒絕三個接受', () => {
  for (const url of ['https://hub.example.com', 'https://hub.example.com:443', 'https://localhost.hub.example.com', 'http://127.0.0.1:1', 'https://10.0.0.5:1']) {
    assert.equal(isAllowedServerUrl(url), false, url);
  }
  for (const url of ['https://127.0.0.1:1', 'https://localhost:5443', 'https://[::1]:2']) assert.equal(isAllowedServerUrl(url), true, url);
  assert.equal(isAllowedServerUrl(undefined), false);
});

test('isProductionTarget_共用測試表', () => {
  assert.ok(cases.productionTarget.some((c) => c.production) && cases.productionTarget.some((c) => !c.production));
  for (const c of cases.productionTarget) assert.equal(isProductionTarget(c.readyz, c.tls), c.production, c.why);
});

test('isProductionTarget_Node的getPeerCertificate形狀', () => {
  // getPeerCertificate 的 subject、issuer 是物件、subjectaltname 是「DNS:a, DNS:b」字串
  assert.equal(isProductionTarget({ environment: 'console' }, { subject: { CN: 'localhost' }, issuer: { CN: 'localhost' }, subjectaltname: 'DNS:localhost' }), false);
  assert.equal(isProductionTarget({ environment: 'console' }, { subject: { CN: 'hub.example.com' } }), true);
  assert.equal(isProductionTarget({ environment: 'console' }, { subject: { CN: 'x' }, subjectaltname: 'DNS:x, DNS:hub.example.com' }), true);
  assert.equal(isProductionTarget({ environment: 'console' }, { subject: { CN: 'localhost' }, issuer: { C: 'US', O: "Let's Encrypt", CN: 'R10' } }), true);
});

test('checkExternalBase_hub-e2e的--base_共用測試表', () => {
  // hub-e2e.mjs --base（WP4.9 合併前審查）：example.com、ssh 轉送（迴路網址但 environment=service 或憑證 hub.example.com）都拒絕，正常的本機放行
  assert.ok(cases.externalBase.some((c) => c.ok) && cases.externalBase.some((c) => !c.ok));
  for (const c of cases.externalBase) {
    const r = checkExternalBase(c.url, c.readyz, c.tls);
    assert.equal(r.ok, c.ok, `${c.url}（${c.why}）：${r.reason}`);
    assert.equal(r.ok ? r.reason === null : typeof r.reason === 'string' && r.reason.length > 0, true, c.why);
  }
});

test('isExeStale_比任何一個原始檔舊或沒有exe', () => {
  assert.equal(isExeStale(1000, [900, 999]), false);
  assert.equal(isExeStale(1000, [900, 1001]), true);
  assert.equal(isExeStale(undefined, [1]), true);
  assert.equal(isExeStale(NaN, []), true);
  assert.equal(isExeStale(1000, []), false);
});

test('isUnderTemp_只收暫存資料夾底下', () => {
  const tmp = os.tmpdir();
  assert.equal(isUnderTemp(path.join(tmp, 'myriagent-tools', 'x')), true);
  assert.equal(isUnderTemp(path.join(tmp, 'myriagent-tools', 'x').toUpperCase()), true);
  assert.equal(isUnderTemp(tmp), false);
  assert.equal(isUnderTemp(path.join(tmp, '..')), false);
  assert.equal(isUnderTemp(path.join(tmp, 'a', '..', '..', 'b')), false);
  assert.equal(isUnderTemp('C:\\ProgramData\\MyriagentHub'), false);
  assert.equal(isUnderTemp(''), false);
  assert.equal(isUnderTemp(null), false);
});
