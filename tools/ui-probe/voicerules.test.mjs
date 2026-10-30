// 語音通知的純規則（src/Myriagent/Web/voicerules.js）：開關的預設、語速與音調（範圍同桌面）、挑中文聲音、同一個瀏覽器的分頁之間去重、被擋下的那一句多久內補念，
// 以及 voicerules.js 沒有 import、voice.js 不把念的文字寫進 DOM。規則模組沒有 import，以 data: URL 匯入（同 gitrules.test.mjs）。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const webDir = path.resolve(here, '..', '..', 'src', 'Myriagent', 'Web');
const source = readFileSync(path.join(webDir, 'voicerules.js'), 'utf8');
const V = await import('data:text/javascript;base64,' + Buffer.from(source + '\n// voicerules.test\n', 'utf8').toString('base64'));

const voice = (lang, name = lang, localService = true) => ({ lang, name, localService });

test('開關：沒存過是開，只有 off 是關', () => {
  assert.equal(V.voiceEnabled(null), true);
  assert.equal(V.voiceEnabled('on'), true);
  assert.equal(V.voiceEnabled('off'), false);
  assert.equal(V.voiceEnabled('garbage'), true);
});

test('挑聲音：臺灣華語優先（本機的先），再來正體中文、其他中文；沒有中文回 null', () => {
  const en = voice('en-US');
  const cn = voice('zh-CN', '曉曉');
  const hk = voice('zh-HK', '曉佳');
  const twOnline = voice('zh-TW', '曉臻 Online', false);
  const twLocal = voice('zh_TW', '雅婷');
  assert.equal(V.pickVoice([en, cn, hk, twOnline, twLocal]), twLocal, 'Android 的 zh_TW 也算');
  assert.equal(V.pickVoice([en, cn, hk, twOnline]), twOnline);
  assert.equal(V.pickVoice([en, cn, hk]), hk);
  assert.equal(V.pickVoice([en, cn]), cn);
  assert.equal(V.pickVoice([en]), null);
  assert.equal(V.pickVoice([]), null);
  assert.equal(V.pickVoice(null), null);
  assert.equal(V.pickVoice([null, { name: 'x' }, voice('cmn-Hant-TW', 'iOS')]).name, 'iOS');
});

test('語速與音調：範圍與預設同桌面的 SpeechAudioRules（預設 1.5 倍、升 2 個半音），試聽句同 VoiceNotifyRules', () => {
  const cs = readFileSync(path.resolve(here, '..', '..', 'src', 'Myriagent', 'Speech', 'SpeechAudioRules.cs'), 'utf8');
  const constOf = (name) => Number((cs.match(new RegExp('public const double ' + name + ' = (-?[0-9.]+);')) || [])[1]);
  assert.equal(V.SPEED_MIN, constOf('MinSpeed'));
  assert.equal(V.SPEED_MAX, constOf('MaxSpeed'));
  assert.equal(V.SPEED_STEP, constOf('SpeedStep'));
  assert.equal(V.SPEED_DEFAULT, constOf('DefaultSpeed'));
  assert.equal(V.PITCH_MIN, constOf('MinPitch'));
  assert.equal(V.PITCH_MAX, constOf('MaxPitch'));
  assert.equal(V.PITCH_DEFAULT, constOf('DefaultPitch'));
  assert.equal(V.SPEED_DEFAULT, 1.5);
  assert.equal(V.PITCH_DEFAULT, 2);
  const rules = readFileSync(path.resolve(here, '..', '..', 'src', 'Myriagent.Protocol', 'Hub', 'VoiceNotifyRules.cs'), 'utf8');
  assert.ok(rules.includes('TuningSample = "' + V.TUNING_SAMPLE + '"'));
  assert.ok(rules.includes('EnabledSample = "' + V.ENABLED_SAMPLE + '"'));
  assert.equal(V.MAX_QUEUED, 3);
});

test('語速與音調：正規化（含 localStorage 的字串）、限制在範圍內、取到間距，不是數字回預設', () => {
  assert.equal(V.normSpeed(1.5), 1.5);
  assert.equal(V.normSpeed('1.84'), 1.8);
  assert.equal(V.normSpeed(0.1), 0.5);
  assert.equal(V.normSpeed(9), 2.5);
  assert.equal(V.normSpeed(null), 1.5, '沒存過');
  assert.equal(V.normSpeed(''), 1.5);
  assert.equal(V.normSpeed('fast'), 1.5);
  assert.equal(V.normSpeed(NaN), 1.5);
  assert.equal(V.normSpeed(undefined), 1.5);
  assert.equal(V.normPitch(2), 2);
  assert.equal(V.normPitch('-2.6'), -3);
  assert.equal(V.normPitch(-20), -6);
  assert.equal(V.normPitch(20), 6);
  assert.equal(V.normPitch(-0.2), 0);
  assert.ok(Object.is(V.normPitch(-0.2), 0), '不是 -0');
  assert.equal(V.normPitch(null), 2);
  assert.equal(V.normPitch(Infinity), 2);
});

test('語速與音調：設定頁的顯示、Web Speech 的 pitch（升 N 個半音＝2^(N/12)）', () => {
  assert.equal(V.fmtSpeed(1.5), '1.5×');
  assert.equal(V.fmtSpeed(2), '2.0×');
  assert.equal(V.fmtPitch(0), '原調');
  assert.equal(V.fmtPitch(2), '+2 半音');
  assert.equal(V.fmtPitch(-3), '-3 半音');
  assert.equal(V.webPitch(0), 1);
  assert.equal(V.webPitch(2), 1.122);
  assert.equal(V.webPitch(12), 1.414, '限制在 ±6 個半音');
  assert.equal(V.webPitch(-6), 0.707);
});

test('去重：同一筆事件十分鐘內只念一次，過期與格式不對的紀錄丟掉，最多記 SPOKEN_MAX 筆', () => {
  const now = 1_800_000_000_000;
  let list = V.rememberSpoken([], 'c1:1', now);
  assert.equal(V.wasSpoken(list, 'c1:1', now + 1000), true);
  assert.equal(V.wasSpoken(list, 'c1:2', now + 1000), false);
  assert.equal(V.wasSpoken(list, 'c1:1', now + V.SPOKEN_WINDOW_MS), false, '過了十分鐘');
  assert.equal(V.wasSpoken(list, 'c1:1', now - 1), false, '時鐘倒退的紀錄不算');
  assert.equal(V.wasSpoken('not array', 'c1:1', now), false);
  list = V.rememberSpoken([{ id: 'old', at: now - V.SPOKEN_WINDOW_MS }, { id: 5, at: now }, null, ...list], 'c1:2', now + 10);
  assert.deepEqual(list.map((e) => e.id), ['c1:1', 'c1:2']);
  let many = [];
  for (let i = 0; i < V.SPOKEN_MAX + 10; i++) many = V.rememberSpoken(many, 'k' + i, now + i);
  assert.equal(many.length, V.SPOKEN_MAX);
  assert.equal(many[many.length - 1].id, 'k' + (V.SPOKEN_MAX + 9));
});

test('speak 訊息：鍵是對話＋seq，文字要是非空字串', () => {
  assert.equal(V.speakKey({ conversationId: 'c1', id: 42 }), 'c1:42');
  assert.equal(V.speakText({ text: '  「甲」已完成 ' }), '「甲」已完成');
  assert.equal(V.speakText({ text: '   ' }), null);
  assert.equal(V.speakText({ text: 5 }), null);
  assert.equal(V.speakText(null), null);
});

test('被擋下的那一句：兩分鐘內補念，太久或時鐘倒退就不念', () => {
  const now = 1_800_000_000_000;
  assert.equal(V.pendingFresh({ at: now - 1000 }, now), true);
  assert.equal(V.pendingFresh({ at: now - V.PENDING_MAX_MS - 1 }, now), false);
  assert.equal(V.pendingFresh({ at: now + 1000 }, now), false);
  assert.equal(V.pendingFresh(null, now), false);
});

test('voicerules.js 沒有 import；voice.js 念的文字不進 DOM（沒有 HTML 寫入點）', () => {
  assert.doesNotMatch(source, /^\s*import\s/m);
  const voiceSource = readFileSync(path.join(webDir, 'voice.js'), 'utf8');
  assert.doesNotMatch(voiceSource, /innerHTML|outerHTML|insertAdjacentHTML|document\.write/);
});
