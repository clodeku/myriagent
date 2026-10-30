// lib/lock-heartbeat.mjs 的單元測試（node:test）：用暫存資料夾的鎖檔，不碰共用的 %TEMP%\myriagent-tools\hub-e2e.lock
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { beat, startLockHeartbeat } from '../lib/lock-heartbeat.mjs';

function withLock(content, fn) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'myriagent-lock-test-'));
  const file = path.join(dir, 'hub-e2e.lock');
  fs.writeFileSync(file, content);
  const old = new Date(Date.now() - 9 * 60 * 1000);
  fs.utimesSync(file, old, old);
  try {
    return fn(file, old.getTime());
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
}

test('鎖續期_pid相同時mtime前進', () => {
  withLock(JSON.stringify({ pid: process.pid, tool: 'test' }), (file, oldMs) => {
    assert.equal(beat(file), true);
    assert.ok(fs.statSync(file).mtimeMs > oldMs + 8 * 60 * 1000, 'mtime 要被設成現在');
  });
});

test('鎖續期_pid不同時不動', () => {
  withLock(JSON.stringify({ pid: process.pid + 1, tool: 'other' }), (file, oldMs) => {
    assert.equal(beat(file), false);
    assert.ok(Math.abs(fs.statSync(file).mtimeMs - oldMs) < 1000, 'mtime 不該變');
  });
});

test('鎖續期_內容不是JSON或檔案不存在時不動', () => {
  withLock('not json', (file, oldMs) => {
    assert.equal(beat(file), false);
    assert.ok(Math.abs(fs.statSync(file).mtimeMs - oldMs) < 1000);
    assert.equal(beat(path.join(path.dirname(file), 'missing.lock')), false);
  });
});

test('鎖續期_計時器定期更新而且stop之後不再更新', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'myriagent-lock-test-'));
  const file = path.join(dir, 'hub-e2e.lock');
  try {
    fs.writeFileSync(file, JSON.stringify({ pid: process.pid }));
    const hb = startLockHeartbeat({ file, intervalMs: 20 });
    await new Promise((r) => setTimeout(r, 120));
    hb.stop();
    const beats = hb.beats();
    assert.ok(beats >= 2, `只續期了 ${beats} 次`);
    await new Promise((r) => setTimeout(r, 60));
    assert.equal(hb.beats(), beats);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
