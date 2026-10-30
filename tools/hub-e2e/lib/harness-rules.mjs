// 全流程共用環境（full-flow、real-exe）的純函式（WP4.9 步 14；harness.mjs 用，test/harness.test.mjs 測）：
// 裝置設定的正規化、假裝置的參數與劇本、命令列、控制通道的一行、協商功能的檢查、建置新鮮度的判斷。
// 不碰檔案、網路、程序與時鐘。

/** 第 4 階段的四個功能（smoke 要四個都協商出；U2 的 update1 另外可能有，不要求） */
export const REQUIRED_FEATURES = ['conv1', 'live1', 'cmd1', 'media1'];

/** 假 Codex app-server 的情境（同 tools/fake-device 的 FakeCodexAppServerScript.Scenarios） */
export const FAKE_CODEX_SCENARIOS = ['ok', 'evil_host', 'http', 'missing_fields', 'unsupported', 'complete_fail', 'init_fail'];

/**
 * 假 Codex app-server 登入完成後 account/read 回的信箱（同 FakeCodexAppServerScript.Email；WP4.9 步 17 的 FF7）：遠端登入完成後電腦以信箱比對帳戶目錄的身分，
 * 不同就在同一個資料夾自動登出——要登入成功，那台 Codex 帳戶的信箱（codexEmail）就要是它
 */
export const FAKE_CODEX_EMAIL = 'device@example.test';

/** 假 CLI 的種類（劇本的 cli op：只宣告安裝與版本，不會執行任何程序；2026-10-22 起四種都進同步，Grok、Antigravity 只在協商出 prov1 時送） */
const CLI_PROVIDERS = ['Claude', 'Codex', 'Grok', 'Antigravity'];

/**
 * 每台另外加的代理種類（設計 2026-10-22-grok-antigravity-sync 的 P5）：Grok 是隔離帳戶、Antigravity 是本機預設帳戶的形狀（假裝置不執行任何 CLI）。
 * 別名 {小寫名稱}-grok／-agy、信箱 {小寫名稱}-grok@example.test／-agy@example.test（身分鍵 grok:{信箱}、antigravity:{信箱}）
 */
const EXTRA_PROVIDERS = { Grok: 'grok', Antigravity: 'agy' };

/** 擁有者帳號（OwnerAccount）與預設密碼的名稱；密碼本身由呼叫端放在記憶體，只經環境變數給假裝置 */
export const OWNER_USER = 'admin';
export const OWNER_PASSWORD_ENV = 'FD_OWNER_PW';

/** 遠端操作政策的層級（同 SyncPolicy） */
const POLICY_LEVELS = ['Off', 'View', 'Control'];

// 預設的種子（FakeScript 的 seedConversations）：時間只由 baseTimeUtc 決定、id 與內容只由 seed 決定
const SEED_BASE = { olderThanDays: 1, itemsPerConversation: 6, baseTimeUtc: '2026-10-20T00:00:00Z' };

/** 預設的兩台：OFFICE 允許遠端完全存取、HOME 不允許（計畫步 14 第 4 點；FF8 要 HOME 擋下完全自動）；種子不同（同種子的第一個對話 id 相同，後上傳的 not_owner） */
export const DEFAULT_DEVICES = [
  { name: 'OFFICE', seed: { count: 3, seed: 20261101 }, policy: { level: 'Control', fullAccess: true, notify: false } },
  { name: 'HOME', seed: { count: 2, seed: 20261102 }, policy: { level: 'Control', fullAccess: false, notify: false } },
];

const NAME = /^[A-Z][A-Z0-9-]{0,31}$/u;

/** 一段種子的檢查（count 0～5000 的整數、seed 是整數） */
function checkSeedPart(name, part) {
  if (!Number.isInteger(part.count) || part.count < 0 || part.count > 5000) throw new Error(`${name} 的種子對話數要是 0～5000 的整數：${part.count}`);
  if (!Number.isInteger(part.seed)) throw new Error(`${name} 的 seed 要是整數：${part.seed}`);
  if (!Number.isInteger(part.olderThanDays) || part.olderThanDays < 0) throw new Error(`${name} 的 olderThanDays 要是非負整數：${part.olderThanDays}`);
  return part;
}

/**
 * 這台的種子（WP4.9 步 15）：物件＝一段（蓋在預設上，同步 14）；陣列＝好幾段（每段一個 seedConversations，FF1 的「100 個最近的＋200 個超過 90 天的」），
 * 每段都要自己給 count 與 seed（不從預設推，免得兩段撞種子）。回傳 count 大於 0 的那幾段
 */
function seedPartsOf(name, base, given, override) {
  const list = Array.isArray(override) ? override : Array.isArray(given) ? given : null;
  if (list) {
    if (list.length === 0) throw new Error(`${name} 的種子陣列是空的`);
    return list.map((p) => {
      if (p === null || typeof p !== 'object' || Array.isArray(p)) throw new Error(`${name} 的每一段種子要是物件`);
      if (p.count === undefined || p.seed === undefined) throw new Error(`${name} 的每一段種子都要給 count 與 seed`);
      return checkSeedPart(name, { ...SEED_BASE, ...p });
    }).filter((p) => p.count > 0);
  }
  const one = checkSeedPart(name, { ...SEED_BASE, ...base.seed, ...(given ?? {}), ...(override ?? {}) });
  return { single: one };
}

/** 信箱的檢查（只收英數字與 ._+- 加一個 @：同 FakeCodexAppServerScript 收的字元，放進劇本的 addAccount） */
const EMAIL = /^[A-Za-z0-9._+-]{1,64}@[A-Za-z0-9.-]{1,190}$/u;

/** 假 CLI 的版本（劇本的 cli op） */
const CLI_VERSION = /^[0-9][0-9A-Za-z.+-]{0,31}$/u;

/** 這台的假 CLI（WP4.9 步 17）：{ Claude?: 版本, Codex?: 版本 }；照 CLI_PROVIDERS 的順序回 [{ provider, version }]，不合格丟例外 */
function cliOf(name, given) {
  if (given === undefined || given === null) return [];
  if (typeof given !== 'object' || Array.isArray(given)) throw new Error(`${name} 的 cli 要是 { Claude?: 版本, Codex?: 版本 }`);
  for (const key of Object.keys(given)) {
    if (!CLI_PROVIDERS.includes(key)) throw new Error(`${name} 的 cli 只收 ${CLI_PROVIDERS.join('、')}：${key}`);
    if (typeof given[key] !== 'string' || !CLI_VERSION.test(given[key])) throw new Error(`${name} 的 ${key} CLI 版本不合格：${given[key]}`);
  }
  return CLI_PROVIDERS.filter((p) => Object.hasOwn(given, p)).map((p) => ({ provider: p, version: given[p] }));
}

/** 另外加的帳戶（providers：Grok、Antigravity 的陣列）：照 EXTRA_PROVIDERS 的順序、不重複 */
function extraAccountsOf(name, given) {
  if (given === undefined || given === null) return [];
  if (!Array.isArray(given)) throw new Error(`${name} 的 providers 要是陣列（${Object.keys(EXTRA_PROVIDERS).join('、')}）`);
  for (const p of given) if (!Object.hasOwn(EXTRA_PROVIDERS, p)) throw new Error(`${name} 的 providers 只收 ${Object.keys(EXTRA_PROVIDERS).join('、')}：${p}`);
  if (new Set(given).size !== given.length) throw new Error(`${name} 的 providers 重複`);
  const lower = name.toLowerCase();
  return Object.keys(EXTRA_PROVIDERS).filter((p) => given.includes(p)).map((p) => ({
    alias: `${lower}-${EXTRA_PROVIDERS[p]}`, provider: p, label: `${name} ${p}`, email: `${lower}-${EXTRA_PROVIDERS[p]}@example.test`,
  }));
}

/**
 * 裝置清單的正規化：devices 是名稱字串或 { name, seed, policy, fakeCodex, appVersion, drain, codexEmail, cli, providers }；seeds 是 { 名稱: 種子 }（蓋過裝置自己的；
 * 陣列是好幾段，見 seedPartsOf），fakeCodex 是 { 名稱: 情境 }。drain（預設 true）：第一份劇本最後等 outbox 清空；false 時控制通道一開就交給情境
 * （FF1 要在回補期間串流）。codexEmail（WP4.9 步 17，預設 {小寫名稱}-codex@example.test）：有假 Codex 時那個 Codex 帳戶的信箱（遠端登入要成功就給
 * FAKE_CODEX_EMAIL）；cli（步 17）：這台宣告安裝的假 CLI 與版本（劇本的 cli op）；providers（Grok、Antigravity，P5）：另外加的帳戶（extraAccounts）。
 * 回傳 [{ name, alias, account, codexAccount, extraAccounts, seed（合計的 count，
 * 其他欄位是第一段的）, seedParts, policy, fakeCodex, appVersion, drain, cli }]，不合格丟例外：名稱是大寫英數字與連字號、不重複；所有裝置所有段的
 * seed 不重複（同種子的第一個對話 id 相同）；情境要在 FAKE_CODEX_SCENARIOS 裡
 */
export function normalizeDevices(devices = DEFAULT_DEVICES.map((d) => d.name), { seeds = {}, fakeCodex = {}, existing = [] } = {}) {
  if (!Array.isArray(devices) || devices.length === 0) throw new Error('要有至少一台裝置');
  const defaults = new Map(DEFAULT_DEVICES.map((d) => [d.name, d]));
  const out = [];
  const known = (n) => out.find((d) => d.name === n) ?? existing.find((d) => d.name === n) ?? null;
  for (const raw of devices) {
    const given = typeof raw === 'string' ? { name: raw } : { ...raw };
    const name = String(given.name ?? '');
    if (!NAME.test(name)) throw new Error(`裝置名稱只收大寫英數字與連字號（開頭是字母、最多 32 字）：${name}`);
    if (known(name)) throw new Error(`裝置名稱重複：${name}`);
    // sameSeedAs（WP4.9 步 18 的 PRIV-R3-3）：故意用另一台的種子（同 id 的對話，後上傳的這台 not_owner 封鎖）；沒給 seed 時照抄那台的每一段
    const twin = given.sameSeedAs === undefined ? null : known(String(given.sameSeedAs));
    if (given.sameSeedAs !== undefined && !twin) throw new Error(`${name} 的 sameSeedAs 要是前面或已經有的裝置：${given.sameSeedAs}`);
    if (twin && twin.seedParts.length === 0) throw new Error(`${name} 的 sameSeedAs 那台（${twin.name}）沒有種子`);
    const base = defaults.get(name) ?? { seed: { count: 0, seed: 20261100 + out.length + existing.length + 1 }, policy: { level: 'Control', fullAccess: false, notify: false } };
    const parts = twin && given.seed === undefined && seeds[name] === undefined
      ? twin.seedParts.map((p) => ({ ...p }))
      : seedPartsOf(name, base, given.seed, seeds[name]);
    const seedParts = Array.isArray(parts) ? parts : parts.single.count > 0 ? [parts.single] : [];
    const first = Array.isArray(parts) ? (parts[0] ?? { ...SEED_BASE, ...base.seed }) : parts.single;
    const seed = { ...first, count: seedParts.reduce((n, p) => n + p.count, 0) };
    if (seed.count > 5000) throw new Error(`${name} 的種子對話合計最多 5000 個：${seed.count}`);
    if (given.drain !== undefined && typeof given.drain !== 'boolean') throw new Error(`${name} 的 drain 要是 true 或 false`);
    const policy = { ...base.policy, ...(given.policy ?? {}) };
    if (!POLICY_LEVELS.includes(policy.level)) throw new Error(`${name} 的政策層級只收 ${POLICY_LEVELS.join('、')}：${policy.level}`);
    const scenario = fakeCodex[name] ?? given.fakeCodex ?? null;
    if (scenario !== null && !FAKE_CODEX_SCENARIOS.includes(scenario)) throw new Error(`${name} 的假 Codex 情境不認得：${scenario}`);
    const appVersion = given.appVersion ?? null;
    if (appVersion !== null && !/^[A-Za-z0-9.+-]{1,64}$/u.test(appVersion)) throw new Error(`${name} 的 appVersion 不合格：${appVersion}`);
    const lower = name.toLowerCase();
    const codexEmail = given.codexEmail ?? `${lower}-codex@example.test`;
    if (typeof codexEmail !== 'string' || !EMAIL.test(codexEmail)) throw new Error(`${name} 的 codexEmail 不合格：${codexEmail}`);
    if (given.codexEmail !== undefined && scenario === null) throw new Error(`${name} 沒有假 Codex（fakeCodex），codexEmail 用不到`);
    const cli = cliOf(name, given.cli);
    const extraAccounts = extraAccountsOf(name, given.providers);
    const account = { alias: `${lower}-claude`, provider: 'Claude', label: `${name} Claude`, email: `${lower}@example.test`, orgId: `org-${lower}`, plan: 'max' };
    const codexAccount = scenario === null ? null : { alias: `${lower}-codex`, provider: 'Codex', label: `${name} Codex`, email: codexEmail, plan: 'pro' };
    out.push({ name, alias: account.alias, account, codexAccount, extraAccounts, seed, seedParts, policy: { level: policy.level, fullAccess: !!policy.fullAccess, notify: !!policy.notify }, fakeCodex: scenario, appVersion,
      drain: given.drain !== false, cli, sameSeedAs: twin?.name ?? null });
  }
  const bySeed = new Map();
  for (const d of existing) for (const part of d.seedParts ?? []) if (!bySeed.has(part.seed)) bySeed.set(part.seed, d.name);
  for (const d of out) {
    for (const part of d.seedParts) {
      const owner = bySeed.get(part.seed);
      if (d.sameSeedAs !== null) {
        // 刻意同種子：每一段都要是那一台的，不能撞到第三台
        if (owner !== d.sameSeedAs) throw new Error(`${d.name} 的種子 ${part.seed} 不是 ${d.sameSeedAs} 的${owner ? `（是 ${owner} 的）` : ''}：sameSeedAs 只能照抄那一台的種子`);
        continue;
      }
      if (owner !== undefined) throw new Error(`${owner} 與 ${d.name} 的種子相同（${part.seed}）：第一個對話 id 會相同，後上傳的那台 not_owner`);
      bySeed.set(part.seed, d.name);
    }
  }
  return out;
}

/**
 * 同一個資料根重新啟動那台的劇本（WP4.9 步 18 的 FF9）：不再種對話、不再加帳戶（都存在資料根裡），只設政策；drain（預設 true）時等 outbox 清空
 * （連不上伺服器、或刻意留著待送列時給 false）
 */
export function restartScript(device, { drain = true } = {}) {
  const steps = [{ op: 'policy', level: device.policy.level, fullAccess: device.policy.fullAccess, notify: device.policy.notify }];
  if (drain) steps.push({ op: 'waitDrained', seconds: 180 });
  return { steps };
}

/** 回滾鏈的三個舊組建（計畫 D12）：環境變數、提交；DEPLOYED_PREV 的提交由 --prev-ref 給 */
export const ROLLBACK_BUILDS = [
  { key: 'OLD', variable: 'MYRIAGENT_SYNC_OLD_HUB', commit: '1724718', label: 'WP4.1 契約的最後一版（沒有 conv1）' },
  { key: 'PREV', variable: 'MYRIAGENT_SYNC_PREV_HUB', commit: '5538705', label: 'WP4.2 合併後的 main（有 conv1、live1，沒有 cmd1）' },
  { key: 'DEPLOYED_PREV', variable: 'MYRIAGENT_SYNC_DEPLOYED_PREV_HUB', commit: null, label: 'WP4.9 的 S6 推送之前 main 上最後一次部署的那一版' },
];

/**
 * 回滾鏈要跑的舊組建（不碰檔案）：vars 是環境變數、prevRef 是 --prev-ref。回 [{ key, variable, commit, label, exe（沒有是 null）, missing（為什麼沒有，有就是 null） }]，
 * 照 ROLLBACK_BUILDS 的順序。沒設環境變數、或 DEPLOYED_PREV 沒給 --prev-ref 都算沒有（那一段記「未執行」，整體不算通過）
 */
export function rollbackBuilds(vars = {}, prevRef = null) {
  return ROLLBACK_BUILDS.map((b) => {
    const exe = typeof vars[b.variable] === 'string' && vars[b.variable].trim() !== '' ? vars[b.variable].trim() : null;
    const commit = b.commit ?? prevRef ?? null;
    const missing = !exe ? `沒設 ${b.variable}` : !commit ? '沒給 --prev-ref <提交>' : null;
    return { key: b.key, variable: b.variable, commit, label: b.label, exe, missing };
  });
}

/** 這台第一次 run 的劇本：種對話（每一段一步，要在最前面）、帳戶、假 CLI（步 17）、政策、等 outbox 清空（drain 是 false 時不等）；之後的事都從控制通道來 */
export function deviceScript(device) {
  const steps = [];
  for (const part of device.seedParts ?? []) {
    const { count, seed, olderThanDays, itemsPerConversation, baseTimeUtc } = part;
    steps.push({ op: 'seedConversations', count, seed, olderThanDays, itemsPerConversation, baseTimeUtc, account: device.alias, provider: 'Claude' });
  }
  steps.push({ op: 'addAccount', ...device.account });
  if (device.codexAccount) steps.push({ op: 'addAccount', ...device.codexAccount });
  for (const a of device.extraAccounts ?? []) steps.push({ op: 'addAccount', ...a });
  for (const c of device.cli ?? []) steps.push({ op: 'cli', provider: c.provider, version: c.version });
  steps.push({ op: 'policy', level: device.policy.level, fullAccess: device.policy.fullAccess, notify: device.policy.notify });
  if (device.drain !== false) steps.push({ op: 'waitDrained', seconds: 180 });
  return { steps };
}

function globalArgs(dataDir, device) {
  if (!dataDir) throw new Error('要有資料根');
  return ['--data', dataDir, ...(device?.appVersion ? ['--app-version', device.appVersion] : [])];
}

/** pair 的參數（一律經延遲代理的網址：aud＝PublicOrigin；擁有者自動核准，密碼只從環境變數讀） */
export function pairArgs({ dataDir, server, device, timeoutSeconds = 120 }) {
  if (!server) throw new Error('要有伺服器網址');
  return [...globalArgs(dataDir, device), 'pair', '--server', server, '--name', device.name, '--dev-trust',
    '--owner-user', OWNER_USER, '--owner-password-env', OWNER_PASSWORD_ENV, '--owner-origin', server, '--timeout', String(timeoutSeconds)];
}

/** run --control-stdin 的參數（劇本跑完接著讀標準輸入的控制指令） */
export function runArgs({ dataDir, scriptFile, device, timeoutSeconds = 120 }) {
  if (!scriptFile) throw new Error('要有劇本檔');
  return [...globalArgs(dataDir, device), 'run', '--script', scriptFile, '--control-stdin', '--dev-trust', '--timeout', String(timeoutSeconds),
    ...(device?.fakeCodex ? ['--fake-codex', device.fakeCodex] : [])];
}

/** unpair 的參數（只刪本機的配對資料，不連線） */
export function unpairArgs({ dataDir, device }) {
  return [...globalArgs(dataDir, device), 'unpair'];
}

/**
 * status 的參數（WP4.9 合併前審查）：收尾時資料根只剩 sync\pending-key.json（配對到一半被逾時以 PID 結束）就跑一次，
 * SyncCoordinator.LoadAsync 的 PairingRecovery.Cleanup 刪掉那把配對到一半的裝置金鑰；沒有 device.json 時不連線（同 measure_sync）
 */
export function statusArgs({ dataDir, device }) {
  return [...globalArgs(dataDir, device), 'status'];
}

/**
 * 收尾時這台的配對資料要怎麼清（不碰檔案；hasDevice＝sync\device.json 在、hasPending＝sync\pending-key.json 在）：
 * 'unpair'（有 device.json，不論有沒有 pending）、'status'（只剩 pending-key.json）、null（都沒有）
 */
export function cleanupAction({ hasDevice, hasPending }) {
  if (hasDevice) return 'unpair';
  if (hasPending) return 'status';
  return null;
}

/** 控制通道的一行（UTF-8、一行一個 JSON、\n 結尾）；op 是必填，參數不能蓋掉 id、op */
export function controlLine(id, op, params = {}) {
  if (typeof op !== 'string' || op.length === 0) throw new Error('要有 op');
  if (params && (Object.hasOwn(params, 'id') || Object.hasOwn(params, 'op'))) throw new Error('參數不能有 id 或 op');
  return JSON.stringify({ id, op, ...(params ?? {}) }) + '\n';
}

/** 假裝置標準輸出的一行：JSON 物件回物件，其他（空行、不是 JSON、不是物件）回 null */
export function parseOutputLine(line) {
  const t = String(line ?? '').trim();
  if (!t.startsWith('{')) return null;
  try {
    const v = JSON.parse(t);
    return v && typeof v === 'object' && !Array.isArray(v) ? v : null;
  } catch {
    return null;
  }
}

/** 少了哪些功能（features 不是陣列時全部都算少） */
export function missingFeatures(features, required = REQUIRED_FEATURES) {
  const have = new Set(Array.isArray(features) ? features : []);
  return required.filter((f) => !have.has(f));
}

/**
 * 建置的新鮮度：outputs 是 [{ label, mtime（毫秒；沒有檔是 null）, sources: [{ path, mtime }] }]。
 * 回 { ok, stale: [{ label, missing, newer: [路徑…（最多 10 個）], newerCount }] }：輸出不存在、或有任何原始檔比它新就算舊
 */
export function staleOutputs(outputs) {
  const stale = [];
  for (const o of outputs ?? []) {
    if (!Number.isFinite(o.mtime)) {
      stale.push({ label: o.label, missing: true, newer: [], newerCount: 0 });
      continue;
    }
    const newer = (o.sources ?? []).filter((s) => Number.isFinite(s.mtime) && s.mtime > o.mtime).map((s) => s.path);
    if (newer.length > 0) stale.push({ label: o.label, missing: false, newer: newer.slice(0, 10), newerCount: newer.length });
  }
  return { ok: stale.length === 0, stale };
}

/** 選得到的情境（步 14 的 smoke；步 15 加 FF1～FF3；步 16 加 FF4～FF6；步 17 加 FF7、FF8、FF10；步 18 加 FF9；Grok／Antigravity 同步的 P5 加 FF11） */
export const SCENARIOS = ['smoke', 'FF1', 'FF2', 'FF3', 'FF4', 'FF5', 'FF6', 'FF7', 'FF8', 'FF9', 'FF10', 'FF11'];

/** 之後的步驟才有的情境（列出來是為了給清楚的錯誤訊息；WP4.9 步 18 之後沒有） */
export const PLANNED_SCENARIOS = [];

/** --prev-ref 的提交（7～40 位小寫十六進位） */
const COMMIT = /^[0-9a-f]{7,40}$/u;

/**
 * full-flow.mjs 的命令列：--only <名稱,…>（預設全部已有的）、--repeat N（1～20，預設 1）、--work、--out、--hub、--fake-device、--headed、
 * --prev-ref <提交>（FF9 的 MYRIAGENT_SYNC_DEPLOYED_PREV_HUB 是哪一個提交建的，計畫 D12）。
 * 拒絕 --base（只對自己啟動的伺服器跑：同 pairing.mjs）、不認得的選項、還沒有的情境；回 { only, repeat, work, out, hub, fakeDevice, headed, prevRef }
 */
export function parseFullFlowArgs(list, { available = SCENARIOS, planned = PLANNED_SCENARIOS } = {}) {
  const valued = new Set(['only', 'repeat', 'work', 'out', 'hub', 'fake-device', 'prev-ref']);
  const flags = new Set(['headed']);
  const raw = {};
  for (let i = 0; i < list.length; i++) {
    const a = list[i];
    if (!a.startsWith('--')) throw new Error(`多出來的參數：${a}`);
    const key = a.slice(2);
    if (key === 'base') throw new Error('full-flow.mjs 不收 --base：只對自己以暫存資料根啟動的 MyriagentHub 跑（不碰任何已經在跑的伺服器）');
    if (flags.has(key)) {
      raw[key] = true;
    } else if (valued.has(key)) {
      if (i + 1 >= list.length || list[i + 1].startsWith('--')) throw new Error(`${a} 後面要有值`);
      raw[key] = list[++i];
    } else {
      throw new Error(`不認得的選項：${a}`);
    }
  }
  const only = raw.only === undefined ? [...available] : raw.only.split(',').map((s) => s.trim()).filter(Boolean);
  if (only.length === 0) throw new Error('--only 沒有任何情境');
  for (const name of only) {
    if (available.includes(name)) continue;
    if (planned.includes(name)) throw new Error(`${name} 還沒有實作；現在有：${available.join('、')}`);
    throw new Error(`不認得的情境：${name}（有：${available.join('、')}）`);
  }
  if (new Set(only).size !== only.length) throw new Error('--only 有重複的情境');
  const repeat = raw.repeat === undefined ? 1 : Number(raw.repeat);
  if (!Number.isInteger(repeat) || repeat < 1 || repeat > 20) throw new Error(`--repeat 要是 1～20 的整數：${raw.repeat}`);
  const prevRef = raw['prev-ref'] ?? null;
  if (prevRef !== null && !COMMIT.test(prevRef)) throw new Error(`--prev-ref 要是 7～40 位小寫十六進位的提交：${prevRef}`);
  return { only, repeat, work: raw.work ?? null, out: raw.out ?? null, hub: raw.hub ?? null, fakeDevice: raw['fake-device'] ?? null, headed: raw.headed === true, prevRef };
}
