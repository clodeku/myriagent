// 遠端桌面輸入的端對端加密（src/Myriagent/Web/deskseal.js；docs/sync-protocol.md 14.16）。
// 固定的向量和 C# 的 DeskSealRulesTests 是同一組：看的一方（這裡）和被看的一方（宿主的 DeskSealRules）要算出一樣的金鑰與密文
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createViewerSeal, sealSupported, toBase64Url, fromBase64Url } from '../../src/Myriagent/Web/deskseal.js';
import { kxPublic, sealAction, createBatch, statusSecure, secureAction } from '../../src/Myriagent/Web/remotedeskrules.js';

const subtle = globalThis.crypto.subtle;
const ECDH = { name: 'ECDH', namedCurve: 'P-256' };

// 看的一方的私鑰是 1～32、被看的一方的是 33～64
const SESSION = '01JDESKSEALVECTOR00000000A';
const VIEWER = {
  d: 'AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA',
  x: 'UVw9brnjlrkE0_7Kf1T9zQzB6Ze_N13KUVrQpsO0A18',
  y: 'RTa-OlDzGPv5pUdZAqIhUCvvDVfgjFOyzApW8X2fk1Q',
};
const VIEWER_PUBLIC = 'BFFcPW6545a5BNP-yn9U_c0MwemXvzddylFa0KbDtANfRTa-OlDzGPv5pUdZAqIhUCvvDVfgjFOyzApW8X2fk1Q';
const TARGET_PUBLIC = 'BB8UAUa_sbJR-E9N2-DUzc_Xev2YSpUg41eUAh-DErue7JlaCLH6dwTfPcwLUKlmUmP7dxH5X5-KRJxQluR8iSs';
const EVENTS = [{ t: 'kd', k: 65 }, { t: 'tx', s: '密碼 P@ss' }];
const SEALED_1 = 'XjA1Tyyr5zCqEcCdBnSxWbNOWUNLQ3ZYAsmdOnH2U1tFVN0QEd9HBM6fVFyUoyEVPSsnKKMC5gv9Z2fliJfhVKcunI6Bk1KUYrat';
// 以服務的標籤（安全桌面的輸入）算的第 1 包：和 C# 的 DeskSecureInputTests.SecureSealed1 是同一個值
const SECURE_SEALED_1 = 'cCCyRwyyq8nlYt1hm-bmm-OwXbU19vwZEzLsMnuHf7-RUkBmOY9zix6JYtG2tdrYabKzLRdMWv7K9BmrRGJrMySEP22ba9VmHMUO';
const SEALED_7 = '0X9krb4Yq4F95cL_wDzsLutZjBm6s2lqVoaitRFAJ8dOspHDNqFy-w43GDaf1LYPtcIOLJk_FCnV2kZCLUhIHEB2-6FuhSs9ZbH2';

async function fixedViewerPair() {
  const jwk = { kty: 'EC', crv: 'P-256', x: VIEWER.x, y: VIEWER.y };
  return {
    privateKey: await subtle.importKey('jwk', { ...jwk, d: VIEWER.d }, ECDH, false, ['deriveBits']),
    publicKey: await subtle.importKey('jwk', jwk, ECDH, true, []),
  };
}

/** 被看的一方（測試扮演）：以自己的金鑰和看的一方的公鑰算出金鑰，回解開一包的函式 */
async function targetFor(sessionId, viewerPublic) {
  const pair = await subtle.generateKey(ECDH, false, ['deriveBits']);
  const targetRaw = new Uint8Array(await subtle.exportKey('raw', pair.publicKey));
  const viewerRaw = fromBase64Url(viewerPublic);
  const peer = await subtle.importKey('raw', viewerRaw, ECDH, false, []);
  const shared = await subtle.deriveBits({ name: 'ECDH', public: peer }, pair.privateKey, 256);
  const material = await subtle.importKey('raw', shared, 'HKDF', false, ['deriveKey']);
  const enc = new TextEncoder();
  const prefix = enc.encode('myriagent-desk-input-v1');
  const info = new Uint8Array(prefix.length + 1 + 130);
  info.set(prefix, 0); info.set(viewerRaw, prefix.length + 1); info.set(targetRaw, prefix.length + 66);
  const key = await subtle.deriveKey({ name: 'HKDF', hash: 'SHA-256', salt: enc.encode(sessionId), info }, material, { name: 'AES-GCM', length: 256 }, false, ['decrypt']);
  return {
    publicKey: toBase64Url(targetRaw),
    async open(n, ct) {
      const iv = new Uint8Array(12);
      new DataView(iv.buffer).setBigUint64(4, BigInt(n));
      const plain = await subtle.decrypt({ name: 'AES-GCM', iv, additionalData: enc.encode('myriagent-desk-input-v1|' + sessionId + '|' + n), tagLength: 128 }, key, fromBase64Url(ct));
      return JSON.parse(new TextDecoder().decode(plain));
    },
  };
}

test('這個環境有 Web Crypto', () => {
  assert.equal(sealSupported(), true);
});

test('base64url：來回一致、不合格的回 null', () => {
  const bytes = new Uint8Array([0, 1, 2, 250, 251, 252, 253, 254, 255]);
  assert.deepEqual(fromBase64Url(toBase64Url(bytes)), bytes);
  assert.equal(toBase64Url(new Uint8Array([251, 255])), '-_8');
  assert.equal(fromBase64Url('a+b/'), null);
  assert.equal(fromBase64Url('abcde'), null);
  assert.equal(fromBase64Url(5), null);
  assert.equal(fromBase64Url(VIEWER_PUBLIC).length, 65);
});

test('固定的向量：公鑰、第 1 包與第 7 包的密文和宿主算的一樣', async () => {
  const viewer = await createViewerSeal(await fixedViewerPair());
  assert.equal(viewer.publicKey, VIEWER_PUBLIC);
  const sealer = await viewer.accept(SESSION, TARGET_PUBLIC);
  assert.ok(sealer);
  const first = await sealer.seal(EVENTS);
  assert.deepEqual(first, { n: 1, ct: SEALED_1 });
  let last = first;
  for (let i = 2; i <= 7; i++) last = await sealer.seal(EVENTS);
  assert.deepEqual(last, { n: 7, ct: SEALED_7 });
});

test('固定的向量（安全桌面的輸入）：以服務的標籤算，密文和宿主的一樣、多帶 k: svc，和給 app 的那一把不同', async () => {
  const viewer = await createViewerSeal(await fixedViewerPair());
  const secure = await viewer.acceptSecure(SESSION, TARGET_PUBLIC);
  assert.ok(secure);
  assert.deepEqual(await secure.seal(EVENTS), { n: 1, ct: SECURE_SEALED_1, k: 'svc' });
  assert.notEqual(SECURE_SEALED_1, SEALED_1);
  // 兩把各自編號：給服務的不佔給 app 的編號
  const app = await viewer.accept(SESSION, TARGET_PUBLIC);
  assert.deepEqual(await app.seal(EVENTS), { n: 1, ct: SEALED_1 });
  assert.equal((await secure.seal(EVENTS)).n, 2);
  assert.equal(await viewer.acceptSecure(SESSION, 'garbage'), null);
});

test('statusSecure：secure 與服務的公鑰（svc 版本 1）', () => {
  assert.deepEqual(statusSecure({ text: null, secure: true, svc: { v: 1, pub: TARGET_PUBLIC } }), { secure: true, svc: TARGET_PUBLIC });
  assert.deepEqual(statusSecure({ text: null, secure: true, note: '只能看' }), { secure: true, svc: null });
  assert.deepEqual(statusSecure({ text: null }), { secure: false, svc: null });
  assert.deepEqual(statusSecure({ secure: 'yes', svc: { v: 2, pub: TARGET_PUBLIC } }), { secure: false, svc: null });
  assert.deepEqual(statusSecure({ svc: { v: 1, pub: 5 } }), { secure: false, svc: null });
  assert.deepEqual(statusSecure({ svc: 'x' }), { secure: false, svc: null });
  assert.deepEqual(statusSecure(null), { secure: false, svc: null });
});

test('secureAction：安全桌面時事件只走服務的金鑰，沒有就不送', () => {
  assert.equal(secureAction(false, false), null);
  assert.equal(secureAction(false, true), null, '不是安全桌面：照平常的（給 app 的金鑰）');
  assert.equal(secureAction(true, true), 'secure');
  assert.equal(secureAction(true, false), 'drop');
});

test('每個工作階段的金鑰都不同；被看的一方解得開，編號或工作階段不對就解不開', async () => {
  const viewer = await createViewerSeal();
  const other = await createViewerSeal();
  assert.notEqual(viewer.publicKey, other.publicKey);
  assert.equal(viewer.publicKey.length, 87);

  const target = await targetFor('01JDESKSEALSESSION0000000B', viewer.publicKey);
  const sealer = await viewer.accept('01JDESKSEALSESSION0000000B', target.publicKey);
  const one = await sealer.seal(EVENTS);
  const two = await sealer.seal([{ t: 'mm', x: 0.25, y: 0.75 }]);
  assert.deepEqual([one.n, two.n], [1, 2]);
  assert.notEqual(one.ct, two.ct);
  assert.deepEqual(await target.open(1, one.ct), { events: EVENTS });
  assert.deepEqual(await target.open(2, two.ct), { events: [{ t: 'mm', x: 0.25, y: 0.75 }] });
  await assert.rejects(target.open(2, one.ct), '第 1 包不能當第 2 包');

  const elsewhere = await targetFor('01JDESKSEALSESSION0000000C', viewer.publicKey);
  await assert.rejects(elsewhere.open(1, one.ct), '別的工作階段、別的金鑰');
});

test('對方的公鑰不合格：不加密（回 null）', async () => {
  const viewer = await createViewerSeal();
  assert.equal(await viewer.accept(SESSION, 'garbage'), null);
  assert.equal(await viewer.accept(SESSION, TARGET_PUBLIC.slice(0, -2)), null);
  assert.equal(await viewer.accept(SESSION, 'A' + TARGET_PUBLIC.slice(1)), null, '開頭不是 0x04');
  const offCurve = new Uint8Array(65);
  offCurve[0] = 4; offCurve[1] = 1; offCurve[64] = 2;
  assert.equal(await viewer.accept(SESSION, toBase64Url(offCurve)), null, '不在曲線上的點');
  assert.equal(await viewer.accept(SESSION, null), null);
});

test('kxPublic：只認 status 裡版本 1 的 kx', () => {
  assert.equal(kxPublic('status', { text: null, kx: { v: 1, pub: TARGET_PUBLIC } }), TARGET_PUBLIC);
  assert.equal(kxPublic('frame', { kx: { v: 1, pub: TARGET_PUBLIC } }), null);
  assert.equal(kxPublic('status', { text: '那台電腦目前鎖定中' }), null);
  assert.equal(kxPublic('status', { kx: { v: 2, pub: TARGET_PUBLIC } }), null);
  assert.equal(kxPublic('status', { kx: { v: 1, pub: 5 } }), null);
  assert.equal(kxPublic('status', { kx: 'x' }), null);
  assert.equal(kxPublic('status', null), null);
});

test('sealAction：決定之前事件先留著，加密中事件一律加密，確認照送', () => {
  assert.equal(sealAction('off', true), 'plain');
  assert.equal(sealAction('off', false), 'plain');
  assert.equal(sealAction('wait', true), 'hold');
  assert.equal(sealAction('deriving', true), 'hold');
  assert.equal(sealAction('wait', false), 'plain', '只有確認：照送');
  assert.equal(sealAction('on', true), 'seal');
  assert.equal(sealAction('on', false), 'plain');
  assert.equal(sealAction(undefined, true), 'plain');
});

test('批次的 takeAck：只拿確認，事件留著', () => {
  const batch = createBatch();
  batch.push({ t: 'kd', k: 65 });
  batch.ack(3);
  assert.equal(batch.takeAck(), 3);
  assert.equal(batch.takeAck(), null);
  assert.equal(batch.size, 1);
  assert.deepEqual(batch.take(), { events: [{ t: 'kd', k: 65 }] });
});
