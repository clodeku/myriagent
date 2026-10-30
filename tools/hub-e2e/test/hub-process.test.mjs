// lib/hub-process.mjs 的單元測試（node:test）：不啟動 MyriagentHub、不碰使用者金鑰存放區（金鑰檔的比對用暫存資料夾）
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { adminCommand, adminCommandAsync, adminStop, adminStopAsync, hubArguments, hubCopyName, keyFilesNaming, keyNeedle, killHard, startHub } from '../lib/hub-process.mjs';

test('adminStop_沒有dataDir丟例外', () => {
  assert.throws(() => adminStop({}), /--data/);
  assert.throws(() => adminStop(), /--data/);
  assert.throws(() => adminStop({ exe: 'C:\\x\\MyriagentHub.exe' }), /--data/);
  assert.throws(() => adminCommand({ exe: 'C:\\x\\MyriagentHub.exe', args: ['status'] }), /--data/);
  // 不擋事件迴圈的版本（步 14）：同樣在啟動任何程序之前就丟（不回傳會被拒絕的 Promise，呼叫端忘了 await 也擋得住）
  assert.throws(() => adminStopAsync({}), /--data/);
  assert.throws(() => adminStopAsync({ exe: 'C:\\x\\MyriagentHub.exe' }), /--data/);
  assert.throws(() => adminCommandAsync({ exe: 'C:\\x\\MyriagentHub.exe', args: ['status'] }), /--data/);
});

test('adminCommandAsync_帶data_回結束碼與輸出', async () => {
  // 拿 node 當「MyriagentHub.exe」：參數是 admin … --data <資料夾>，node 會把 admin 當腳本找不到而以非 0 結束（只驗參數組裝與非同步的回傳形狀）
  const r = await adminCommandAsync({ exe: process.execPath, dataDir: path.join(os.tmpdir(), 'myriagent-tools', 'no-such'), args: ['status'], timeoutMs: 20000 });
  assert.notEqual(r.code, 0);
  assert.equal(typeof r.stdout, 'string');
  assert.match(r.stderr, /admin/);
});

test('needle是複製後的路徑', () => {
  const name = hubCopyName(1234, 5);
  assert.equal(name, 'hub-1234-5');
  assert.equal(keyNeedle(name), 'hub-1234-5\\MyriagentHub.exe');
  assert.match(hubCopyName(), new RegExp(`^hub-${process.pid}-\\d+$`));
  assert.throws(() => keyNeedle(''), /名稱/);
});

test('keyFilesNaming_UTF16LE位元組_任何對齊_只回含needle的', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'myriagent-keys-test-'));
  try {
    const mine = 'C:\\Users\\x\\AppData\\Local\\Temp\\myriagent-tools\\hub-1234-5\\MyriagentHub.exe';
    // 奇數位移（前面 3 個位元組）：整檔當 UTF-16 解碼會錯位
    fs.writeFileSync(path.join(dir, 'a'), Buffer.concat([Buffer.from([1, 2, 3]), Buffer.from(mine, 'utf16le'), Buffer.from([0, 0])]));
    fs.writeFileSync(path.join(dir, 'b'), Buffer.from(mine.replace('hub-1234-5', 'hub-1234-6'), 'utf16le'));
    fs.writeFileSync(path.join(dir, 'c'), Buffer.from(mine, 'utf8')); // UTF-8 的不算（金鑰檔記的是 UTF-16LE）
    fs.writeFileSync(path.join(dir, 'd'), Buffer.from([0, 0, 0, 0]));
    assert.deepEqual(keyFilesNaming(keyNeedle('hub-1234-5'), dir), ['a']);
    assert.deepEqual(keyFilesNaming(keyNeedle('hub-1234-5'), path.join(dir, 'missing')), []);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('hubArguments_Testing環境_迴路_file憑證_PublicOrigin預設是base', () => {
  const args = hubArguments({ dataDir: 'D', port: 12345 });
  assert.deepEqual(args.slice(0, 4), ['--data', 'D', '--environment', 'Testing']);
  assert.ok(args.includes('--Hub:BindAddress=127.0.0.1'));
  assert.ok(args.includes('--Hub:Tls:CertStore=file'));
  assert.ok(args.includes('--Hub:HttpsPort=12345'));
  assert.ok(args.includes('--Hub:PublicOrigin=https://127.0.0.1:12345'));
  assert.ok(hubArguments({ dataDir: 'D', port: 1, publicOrigin: 'https://127.0.0.1:9' }).includes('--Hub:PublicOrigin=https://127.0.0.1:9'));
  assert.deepEqual(hubArguments({ dataDir: 'D', port: 1, extraArgs: ['--x=1'] }).slice(-1), ['--x=1']);
});

test('startHub_資料根不在暫存或PublicOrigin不是迴路就拒絕_不啟動', async () => {
  const exe = process.execPath; // 存在的檔案；檢查在啟動之前就擋下
  await assert.rejects(startHub({ exe, dataDir: 'C:\\ProgramData\\MyriagentHub', port: 40000 }), /暫存/);
  await assert.rejects(startHub({ exe, dataDir: path.join(os.tmpdir(), 'myriagent-tools', 'x'), port: 80 }), /高埠/);
  await assert.rejects(startHub({ exe, dataDir: path.join(os.tmpdir(), 'myriagent-tools', 'x'), port: 40000, publicOrigin: 'https://hub.example.com' }), /迴路/);
  await assert.rejects(startHub({ exe: 'C:\\no\\such\\MyriagentHub.exe', dataDir: path.join(os.tmpdir(), 'myriagent-tools', 'x'), port: 40000 }), /找不到/);
});

test('killHard_只收PID', () => {
  assert.throws(() => killHard(undefined), /PID/);
  assert.throws(() => killHard(0), /PID/);
  assert.throws(() => killHard('MyriagentHub.exe'), /PID/);
});
