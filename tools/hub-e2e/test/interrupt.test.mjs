// lib/harness.mjs 的 Ctrl+C 收尾（node:test；WP4.9 合併前審查）：Node 預設收到 SIGINT 直接結束、finally 不跑，
// 假裝置（全管線、windowsHide，收不到主控台的 Ctrl+C）的裝置金鑰與真的 exe 都會留下。harness 載入時註冊處理器：
// 每個登記著的環境先 beforeInterruptClose、再 env.close()，之後以 130 結束。這裡在子程序以 process.emit('SIGINT') 模擬
// （Windows 上 child.kill('SIGINT') 是直接結束程序，不會進處理器），不啟動 MyriagentHub、假裝置或瀏覽器
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const harnessUrl = pathToFileURL(path.join(here, '..', 'lib', 'harness.mjs')).href;

function runChild(body) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'hub-e2e-interrupt-'));
  try {
    const marker = path.join(dir, 'marker.txt');
    const script = path.join(dir, 'child.mjs');
    fs.writeFileSync(script, `
import fs from 'node:fs';
import { trackEnv, interrupted } from ${JSON.stringify(harnessUrl)};
const marker = ${JSON.stringify(marker)};
const log = (s) => fs.appendFileSync(marker, s + '\\n');
${body}
// 沒有處理器的話 emit 什麼都不做：5 秒後以 0 結束（測試要紅）
setTimeout(() => { log('timeout'); process.exit(0); }, 5000);
process.emit('SIGINT', 'SIGINT');
`);
    const r = spawnSync(process.execPath, [script], { encoding: 'utf8', windowsHide: true, timeout: 30000 });
    const lines = fs.existsSync(marker) ? fs.readFileSync(marker, 'utf8').split(/\r?\n/u).filter(Boolean) : [];
    return { code: r.status, lines, stdout: r.stdout, stderr: r.stderr };
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
}

test('Ctrl+C_每個登記的環境先beforeInterruptClose再close_以130結束', () => {
  const r = runChild(`
trackEnv({ step() {}, beforeInterruptClose: async () => { log('before:A'); }, close: async () => { log('close:A interrupted=' + interrupted()); } });
trackEnv({ step() {}, close: async () => { log('close:B'); } });
`);
  assert.equal(r.code, 130, `${r.stdout}\n${r.stderr}`);
  assert.deepEqual(r.lines, ['before:A', 'close:A interrupted=true', 'close:B']);
});

test('Ctrl+C_close丟例外也照樣收下一個_取消登記的不收', () => {
  const r = runChild(`
const untrack = trackEnv({ step() {}, close: async () => { log('close:gone'); } });
trackEnv({ step() {}, close: async () => { log('close:A'); throw new Error('收尾失敗'); } });
trackEnv({ step() {}, close: async () => { log('close:B'); } });
untrack();
`);
  assert.equal(r.code, 130, `${r.stdout}\n${r.stderr}`);
  assert.deepEqual(r.lines, ['close:A', 'close:B']);
});
