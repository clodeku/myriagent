// lib/quiet-browser.mjs 的單元測試（node:test）：工具的瀏覽器不准經系統的語音引擎出聲
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { quietSpeech } from '../lib/quiet-browser.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

/** tools/hub-e2e 的腳本與 lib（不含 node_modules、test） */
function scripts() {
  const out = [];
  for (const dir of [root, path.join(root, 'lib')]) {
    for (const name of fs.readdirSync(dir)) {
      if (name.endsWith('.mjs')) out.push(path.join(dir, name));
    }
  }
  return out;
}

test('工具的瀏覽器內容一律經_quiet_browser_的_newContext_建立', () => {
  const files = scripts();
  assert.ok(files.length > 5, '掃到的檔案太少：' + files.length);
  const offenders = [];
  for (const file of files) {
    if (path.basename(file) === 'quiet-browser.mjs') continue;
    const lines = fs.readFileSync(file, 'utf8').split('\n');
    lines.forEach((line, i) => {
      if (/\.newContext\(|launchPersistentContext\(/.test(line)) offenders.push(`${path.relative(root, file)}:${i + 1}`);
    });
  }
  assert.deepEqual(offenders, [], '直接建立瀏覽器內容會讓測試的網頁以系統語音念出語音通知：改用 lib/quiet-browser.mjs 的 newContext');
});

/** 假的瀏覽器環境：真的 speak 被呼叫就記下來 */
function fakeSynth() {
  const real = [];
  const synth = {
    speak(u) { real.push(u); },
    cancel() { real.push('cancel'); },
    getVoices() { return []; },
  };
  return { synth, real };
}

class FakeUtterance extends EventTarget {
  constructor(text) {
    super();
    this.text = text;
    this.events = [];
    for (const type of ['start', 'end', 'error']) this.addEventListener(type, (e) => this.events.push(type === 'error' ? 'error:' + e.error : type));
  }
}

const wait = (ms) => new Promise((r) => setTimeout(r, ms));

test('quietSpeech_speak_不交給系統_照樣依序送_start_與_end', async () => {
  const { synth, real } = fakeSynth();
  globalThis.speechSynthesis = synth;
  try {
    quietSpeech();
    const a = new FakeUtterance('「整理報表」已完成');
    const b = new FakeUtterance('第二句');
    synth.speak(a);
    synth.speak(b);
    await wait(120);
    assert.deepEqual(real, []);
    assert.deepEqual(a.events, ['start', 'end']);
    assert.deepEqual(b.events, ['start', 'end']);
  } finally {
    delete globalThis.speechSynthesis;
  }
});

test('quietSpeech_cancel_對還沒念完的送_interrupted_之後不再送事件', async () => {
  const { synth, real } = fakeSynth();
  globalThis.speechSynthesis = synth;
  try {
    quietSpeech();
    const u = new FakeUtterance('會被取消');
    synth.speak(u);
    synth.cancel();
    await wait(120);
    assert.deepEqual(real, []);
    assert.deepEqual(u.events, ['error:interrupted']);
    synth.cancel();
    assert.deepEqual(u.events, ['error:interrupted']);
  } finally {
    delete globalThis.speechSynthesis;
  }
});

test('quietSpeech_沒有語音合成的環境不動', () => {
  delete globalThis.speechSynthesis;
  assert.doesNotThrow(() => quietSpeech());
  assert.equal(globalThis.speechSynthesis, undefined);
});
