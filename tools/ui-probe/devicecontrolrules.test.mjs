// 電腦選單的喇叭音量與螢幕（Web/devicecontrolrules.js；2026-11-17）的純規則
import { test } from 'node:test';
import assert from 'node:assert/strict';
import * as R from '../../src/Myriagent/Web/devicecontrolrules.js';

test('controlCommand_這台不帶deviceId_音量是0到100的字串', () => {
  assert.deepEqual(R.controlCommand({ dev: null, action: 'audioGet', requestId: 'dc-1' }), { cmd: 'deviceControl', action: 'audioGet', requestId: 'dc-1' });
  assert.deepEqual(R.controlCommand({ dev: 'd_x', action: 'audioSet', volume: 42.6, requestId: 'dc-2' }),
    { cmd: 'deviceControl', action: 'audioSet', requestId: 'dc-2', deviceId: 'd_x', volume: '43' });
  assert.equal(R.controlCommand({ dev: 'd_x', action: 'audioSet', volume: 140, requestId: 'r' }).volume, '100');
  assert.equal(R.controlCommand({ dev: 'd_x', action: 'audioSet', volume: -3, requestId: 'r' }).volume, '0');
  assert.deepEqual(R.controlCommand({ dev: 'd_x', action: 'audioSet', mute: true, requestId: 'r' }),
    { cmd: 'deviceControl', action: 'audioSet', requestId: 'r', deviceId: 'd_x', mute: true });
});

test('parseState_形狀不對回null_範圍外的音量當成沒有', () => {
  assert.deepEqual(R.parseState({ type: 'deviceControlState', requestId: 'dc-1', action: 'audioGet', volume: 35, muted: false, error: null }),
    { requestId: 'dc-1', action: 'audioGet', volume: 35, muted: false, error: null });
  assert.equal(R.parseState({ type: 'toast' }), null);
  assert.equal(R.parseState({ type: 'deviceControlState' }), null);
  assert.equal(R.parseState({ type: 'deviceControlState', action: 'audioGet', volume: 300 }).volume, null);
  assert.equal(R.parseState({ type: 'deviceControlState', action: 'screenOff', error: '關不了' }).error, '關不了');
});

test('volumeLabel與speakerLevel', () => {
  assert.equal(R.volumeLabel(45, false), '45%');
  assert.equal(R.volumeLabel(45, true), '靜音');
  assert.equal(R.volumeLabel(null, false), '—');
  assert.equal(R.speakerLevel(0, false), 'mute');
  assert.equal(R.speakerLevel(30, false), 'low');
  assert.equal(R.speakerLevel(60, false), 'mid');
  assert.equal(R.speakerLevel(90, false), 'high');
  assert.equal(R.speakerLevel(90, true), 'mute');
});
