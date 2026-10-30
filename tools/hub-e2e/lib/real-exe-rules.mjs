// real-exe.mjs（WP4.9 步 20 的 R34，計畫 D23）的純函式：參數、受測建置的靜態檢查、給真的 Myriagent.exe 的環境變數、
// 記錄檔裡的「啟動提醒」那一行。不碰檔案、不啟動程序（node --test test/real-exe-rules.test.mjs）。
import path from 'node:path';
import { isStripped } from './child-env.mjs';
import { isUnderTemp } from './guard-rules.mjs';

/** 探針「不找、不執行任何 CLI」的開關（ProbeRules.NoCliVariable）：受測的 Myriagent.dll 要認得它（.NET 字串常值是 UTF-16LE，同 measure_sync） */
export const NO_CLI_VARIABLE = 'MYRIAGENT_AGENT_PROBE_NO_CLI';

/** 記錄檔的啟動提醒（SyncCoordinator.Remote 的 RemindRemoteControlOnStartup：「啟動提醒：遠端操作是 {層級}（允許完全自動：{True|False}）」） */
export const STARTUP_REMINDER_PREFIX = '啟動提醒：遠端操作是 ';

const REMINDER = /啟動提醒：遠端操作是 (Off|View|Control)（允許完全自動：(True|False)）/gu;

/** 這份組件的內容認得 NO_CLI 開關（UTF-16LE 的字串常值）：認不得的建置會找到並執行這台電腦真正的 CLI，不跑 */
export function hasNoCliSwitch(buffer) {
  return Buffer.isBuffer(buffer) && buffer.includes(Buffer.from(NO_CLI_VARIABLE, 'utf16le'));
}

/**
 * 記錄檔文字裡的啟動提醒：回最後一筆 { level, fullAccess, line }（沒有回 null）。只認完整的那一句（層級是 Off、View、Control 之一、括號裡是 True 或 False）
 */
export function startupReminder(text) {
  let last = null;
  for (const m of String(text ?? '').matchAll(REMINDER)) {
    const start = String(text).lastIndexOf('\n', m.index) + 1;
    const end = String(text).indexOf('\n', m.index);
    last = { level: m[1], fullAccess: m[2] === 'True', line: String(text).slice(start, end < 0 ? undefined : end).trim() };
  }
  return last;
}

/**
 * 給真的 exe 的環境（計畫 D23；FDR 的「實機量測」同一組）：base 是已經剝過的子程序環境（child-env.mjs，加 tripwire 的 PATH），
 * 再放進探針（輸出資料夾、不送訊息、不找 CLI、結束後不關閉）、開發信任（本機 MyriagentHub 是自簽憑證），與空的 CLAUDE_CONFIG_DIR、CODEX_HOME。
 * base 裡還有會被剝掉的變數（例如本 session 的 CLAUDE*）、或任一路徑不在暫存資料夾底下時丟例外
 */
export function appEnv(base, { probeDir, claudeConfigDir, codexHome }) {
  const leftovers = Object.keys(base ?? {}).filter((k) => isStripped(k));
  if (leftovers.length > 0) throw new Error(`子程序環境還有該剝掉的變數：${leftovers.join('、')}`);
  for (const [name, dir] of [['探針輸出', probeDir], ['CLAUDE_CONFIG_DIR', claudeConfigDir], ['CODEX_HOME', codexHome]]) {
    if (typeof dir !== 'string' || !path.isAbsolute(dir) || !isUnderTemp(dir)) throw new Error(`${name} 要是暫存資料夾底下的完整路徑：${dir}`);
  }
  return {
    ...base,
    MYRIAGENT_AGENT_PROBE: probeDir,
    MYRIAGENT_AGENT_PROBE_SEND: '0',
    [NO_CLI_VARIABLE]: '1',
    MYRIAGENT_AGENT_PROBE_KEEP: '1',
    MYRIAGENT_SYNC_DEV_TRUST: '1',
    CLAUDE_CONFIG_DIR: claudeConfigDir,
    CODEX_HOME: codexHome,
  };
}

/** 真的 exe 的參數：只有 --data <資料根>（資料根要在暫存資料夾底下；同一個資料根、同一個 Windows 使用者，FDR「和真的 exe 共用資料根」） */
export function appArgs(dataDir) {
  if (typeof dataDir !== 'string' || !path.isAbsolute(dataDir) || !isUnderTemp(dataDir)) throw new Error(`資料根要是暫存資料夾底下的完整路徑：${dataDir}`);
  return ['--data', dataDir];
}

/**
 * real-exe.mjs 的參數：--work <TEMP 底下>、--out <結果檔>、--app <Myriagent.exe>、--hub <MyriagentHub.exe>、--fake-device <FakeDevice.exe>、--headed、
 * --wait-seconds N（等啟動提醒與重新上線的上限，10～600，預設 120）。不認得的參數、缺值、--base（只對自己啟動的伺服器跑）丟例外
 */
export function parseRealExeArgs(list) {
  const out = { work: null, out: null, app: null, hub: null, fakeDevice: null, headed: false, waitSeconds: 120 };
  const valued = { '--work': 'work', '--out': 'out', '--app': 'app', '--hub': 'hub', '--fake-device': 'fakeDevice' };
  for (let i = 0; i < list.length; i++) {
    const a = list[i];
    if (a === '--headed') {
      out.headed = true;
    } else if (a === '--wait-seconds') {
      const v = Number(list[++i]);
      if (!Number.isInteger(v) || v < 10 || v > 600) throw new Error(`--wait-seconds 要是 10～600 的整數：${list[i]}`);
      out.waitSeconds = v;
    } else if (Object.hasOwn(valued, a)) {
      const v = list[++i];
      if (v === undefined || v.startsWith('--')) throw new Error(`${a} 要有值`);
      out[valued[a]] = v;
    } else if (a === '--base') {
      throw new Error('real-exe.mjs 只對自己以暫存資料根啟動的伺服器跑，不收 --base');
    } else {
      throw new Error(`不認得的參數：${a}`);
    }
  }
  if (out.work !== null && !isUnderTemp(path.resolve(out.work))) throw new Error(`--work 要在暫存資料夾底下：${out.work}`);
  return out;
}
