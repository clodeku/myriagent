// lib/tripwire.mjs 的單元測試（node:test）：墊片放在暫存資料夾、只以完整路徑呼叫墊片本身（絕不呼叫真的 CLI）
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { killHard } from '../lib/hub-process.mjs';
import { createTripwire, descendants, isForbiddenProcess, listProcesses, pathKey, scanOwnTree, scanOwnTreeAsync, SHIM_EXIT_CODE, SHIM_NAMES, shimScript } from '../lib/tripwire.mjs';

function tempDir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'myriagent-tripwire-test-'));
}

test('tripwire_以完整路徑呼叫墊片後標記檔存在_結束碼9', () => {
  const dir = tempDir();
  try {
    const tw = createTripwire(path.join(dir, 'shims'));
    assert.deepEqual(tw.tripped(), []);
    for (const name of SHIM_NAMES) assert.ok(fs.existsSync(tw.shims[name]), name);
    // Node 不准不經殼層啟動 .cmd：以 cmd.exe 執行墊片的完整路徑（只呼叫墊片，絕不呼叫真的 claude）
    const r = spawnSync(`"${tw.shims.claude}" --version`, { shell: true, windowsHide: true, encoding: 'utf8' });
    assert.equal(r.status, SHIM_EXIT_CODE);
    assert.ok(fs.existsSync(tw.marker), '標記檔要存在');
    assert.deepEqual(tw.tripped(), ['claude']);
    assert.throws(() => tw.assertNotTripped(), /墊片被呼叫了（claude）/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('tripwire_墊片只有ASCII', () => {
  assert.match(shimScript(), /^[\x20-\x7e\r\n]+$/u);
  assert.match(shimScript(), /exit \/b 9/);
});

test('verifyShims_墊片在PATH最前面時通過_故意排錯的PATH丟例外', () => {
  const dir = tempDir();
  try {
    const tw = createTripwire(path.join(dir, 'shims'));
    // 另一個資料夾放同名的「冒牌」（也是墊片，不會真的執行）：它排在前面時 where 的第一筆不是我們的墊片
    const decoy = path.join(dir, 'decoy');
    fs.mkdirSync(decoy);
    for (const name of SHIM_NAMES) fs.writeFileSync(path.join(decoy, `${name}.cmd`), shimScript(), 'ascii');
    const base = `${decoy};${process.env.SystemRoot ?? 'C:\\Windows'}\\System32`;
    const withPath = (value) => {
      const env = Object.fromEntries(Object.entries(process.env).filter(([k]) => k.toUpperCase() !== 'PATH'));
      env.Path = value;
      return env;
    };
    const good = tw.childEnv(withPath(base));
    assert.equal(pathKey(good), 'Path');
    assert.equal(tw.verifyShims(good), true);
    assert.throws(() => tw.verifyShims(withPath(`${base};${tw.dir}`)), /不是墊片/);
    assert.throws(() => tw.verifyShims(withPath(`${process.env.SystemRoot ?? 'C:\\Windows'}\\System32`)), /找不到|不是墊片/);
    assert.deepEqual(tw.tripped(), [], 'where 不執行墊片');
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('childEnv_保留PATH鍵的大小寫_不多一個PATH鍵', () => {
  const dir = tempDir();
  try {
    const tw = createTripwire(path.join(dir, 'shims'));
    const env = tw.childEnv({ Path: 'C:\\a;C:\\b', X: '1' });
    assert.deepEqual(Object.keys(env).filter((k) => k.toUpperCase() === 'PATH'), ['Path']);
    assert.equal(env.Path, `${tw.dir};C:\\a;C:\\b`);
    assert.equal(tw.childPath(`${tw.dir};C:\\a`), `${tw.dir};C:\\a`, '已經有的不重複放');
    assert.throws(() => createTripwire('C:\\Windows\\shims'), /暫存/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('descendants_只沿父PID鏈往下_擋掉PID重用', () => {
  const procs = [
    { pid: 100, ppid: 1, name: 'node.exe', createdAt: 1000 },
    { pid: 200, ppid: 100, name: 'MyriagentHub.exe', createdAt: 1100 },
    { pid: 300, ppid: 200, name: 'conhost.exe', createdAt: 1200 },
    { pid: 400, ppid: 100, name: 'claude.exe', createdAt: 900 }, // 父 PID 是 100，但比 100 早建立：PID 重用，不是我們的
    { pid: 500, ppid: 7, name: 'claude.exe', createdAt: 1300 }, // 別的 session 的
    { pid: 600, ppid: 300, name: 'codex.exe', createdAt: 1400 },
  ];
  assert.deepEqual(descendants(procs, [100]).map((p) => p.pid).sort(), [200, 300, 600]);
  assert.deepEqual(scanOwnTree([100], procs).map((p) => p.pid), [600]);
  assert.deepEqual(scanOwnTree([], procs), []);
  assert.deepEqual(scanOwnTree([200], procs).map((p) => p.pid), [600]);
});

test('isForbiddenProcess_執行檔名稱與npm版的命令列', () => {
  assert.equal(isForbiddenProcess({ name: 'claude.exe' }), true);
  assert.equal(isForbiddenProcess({ name: 'AGY.EXE' }), true);
  assert.equal(isForbiddenProcess({ name: 'grok' }), true);
  assert.equal(isForbiddenProcess({ name: 'node.exe', commandLine: 'node C:\\npm\\node_modules\\@anthropic-ai\\claude-code\\cli.js -p' }), true);
  assert.equal(isForbiddenProcess({ name: 'node.exe', commandLine: 'node C:/npm/node_modules/@openai/codex/bin/codex.js' }), true);
  assert.equal(isForbiddenProcess({ name: 'MyriagentHub.exe', commandLine: 'MyriagentHub.exe --data x' }), false);
  assert.equal(isForbiddenProcess({ name: 'FakeDevice.exe', commandLine: 'FakeDevice.exe run --script claude-like.json' }), false);
  assert.equal(isForbiddenProcess({ name: 'cmd.exe', commandLine: 'cmd /c "C:\\tmp\\shims\\claude.cmd"' }), false, '墊片由標記檔抓');
});

test('scanOwnTree_實際列程序清單_找得到自己的子程序_樹裡沒有CLI', async () => {
  // 陽性對照：自己啟動一個活著的子程序，Win32_Process 的父 PID 鏈要找得到它（解析壞掉時不會只是「沒找到 CLI」而照過）
  const child = spawn(process.execPath, ['-e', 'setTimeout(()=>{},15000)'], { windowsHide: true, stdio: 'ignore' });
  try {
    await new Promise((r) => child.once('spawn', r));
    const procs = listProcesses();
    assert.ok(procs.length > 10, `程序清單只有 ${procs.length} 筆`);
    const tree = descendants(procs, [process.pid]);
    assert.ok(tree.some((p) => p.pid === child.pid && /node/i.test(p.name)), '自己的子程序要在樹裡');
    assert.deepEqual(tree.filter(isForbiddenProcess), []);
  } finally {
    killHard(child.pid);
  }
});

test('scanOwnTreeAsync_不擋事件迴圈_找得到自己的子程序_沒有根時不列', async () => {
  assert.deepEqual(await scanOwnTreeAsync([]), { hits: [], scanned: 0 });
  // 陽性對照同上；另以計時器確認列程序清單的幾秒裡事件迴圈照常（harness 的延遲代理在同一個程序裡）
  const child = spawn(process.execPath, ['-e', 'setTimeout(()=>{},15000)'], { windowsHide: true, stdio: 'ignore' });
  let ticks = 0;
  const timer = setInterval(() => { ticks++; }, 20);
  try {
    await new Promise((r) => child.once('spawn', r));
    const started = Date.now();
    const r = await scanOwnTreeAsync([process.pid]);
    const elapsed = Date.now() - started;
    assert.ok(r.scanned >= 1, `樹裡只有 ${r.scanned} 個程序`);
    assert.deepEqual(r.hits, []);
    // 擋住的話整段時間只會有 0～1 次；不擋時每 20 ms 一次（Windows 計時器解析度約 15.6 ms，至少要有一半）
    assert.ok(ticks >= Math.floor(elapsed / 20 / 2), `列了 ${elapsed} ms，計時器只跑了 ${ticks} 次`);
  } finally {
    clearInterval(timer);
    killHard(child.pid);
  }
});
