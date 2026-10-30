// 電腦狀態面板的純規則（src/Myriagent/Web/monitorrules.js）：格式化、分級、deviceStats 的整理、歷史、要畫的電腦清單、走勢圖的路徑，
// 以及 monitor.js、monitorrules.js 沒有 HTML 寫入點、monitorrules.js 沒有 import。
// 規則模組沒有 import，以 data: URL 匯入（同 rules.test.mjs）。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const webDir = path.resolve(here, '..', '..', 'src', 'Myriagent', 'Web');
const source = readFileSync(path.join(webDir, 'monitorrules.js'), 'utf8');
const M = await import('data:text/javascript;base64,' + Buffer.from(source + '\n// monitorrules.test\n', 'utf8').toString('base64'));

const KB = 1024, MB = 1024 * KB, GB = 1024 * MB;
const stat = (o = {}) => Object.assign({ id: 'd_A', self: false, name: null, seq: 1, ageMs: 0, cpu: 12.5, cores: 8, cpuName: 'CPU X', memUsed: 8 * GB, memTotal: 16 * GB,
  disks: [{ name: 'C:', used: 100 * GB, total: 200 * GB }], netRx: 1500, netTx: 300, rttMs: 42, uptimeSec: 3600 }, o);
const dev = (id, o = {}) => Object.assign({ id, name: 'PC-' + id.slice(2), signedName: 'PC', self: false, online: true, lastSeenAt: 1000, revoked: null }, o);

test('位元組、速率、成對容量的格式', () => {
  assert.equal(M.fmtBytes(0), '0 B');
  assert.equal(M.fmtBytes(512), '512 B');
  assert.equal(M.fmtBytes(1536), '1.5 KB');
  assert.equal(M.fmtBytes(10 * KB), '10 KB');
  assert.equal(M.fmtBytes(12.6 * MB), '13 MB');
  assert.equal(M.fmtBytes(2 * GB), '2 GB', '整數不留 .0');
  assert.equal(M.fmtBytes(1.5 * 1024 * GB), '1.5 TB');
  assert.equal(M.fmtBytes(-1), '—');
  assert.equal(M.fmtBytes(null), '—');
  assert.equal(M.fmtBytes(NaN), '—');
  assert.equal(M.fmtRate(980), '980 B/s');
  assert.equal(M.fmtRate(1.2 * MB), '1.2 MB/s');
  assert.equal(M.fmtRate(undefined), '—');
  assert.equal(M.fmtPair(512 * GB, 931 * GB), '512 / 931 GB', '同單位只寫一次');
  assert.equal(M.fmtPair(800 * MB, 1.5 * GB), '800 MB / 1.5 GB');
  assert.equal(M.fmtPair(null, 1), '—');
});

test('百分比、開機時間、多久以前、延遲數字', () => {
  assert.equal(M.fmtPct(23.4), '23');
  assert.equal(M.fmtPct(3.44), '3.4', '小於 10 留一位小數');
  assert.equal(M.fmtPct(0), '0');
  assert.equal(M.fmtPct(120), '100', '夾在 0～100');
  assert.equal(M.fmtPct(null), '—');
  assert.equal(M.fmtUptime(30), '1 分鐘');
  assert.equal(M.fmtUptime(12 * 60), '12 分鐘');
  assert.equal(M.fmtUptime(5 * 3600 + 12 * 60), '5 小時 12 分');
  assert.equal(M.fmtUptime(3 * 86400 + 4 * 3600), '3 天 4 小時');
  assert.equal(M.fmtUptime(2 * 86400), '2 天');
  assert.equal(M.fmtUptime(null), null);
  assert.equal(M.fmtAgo(1000), '剛剛');
  assert.equal(M.fmtAgo(12000), '12 秒前');
  assert.equal(M.fmtAgo(3 * 60000), '3 分鐘前');
  assert.equal(M.fmtAgo(2 * 3600000), '2 小時前');
  assert.equal(M.fmtAgo(3 * 86400000), '3 天前');
  assert.equal(M.fmtRtt(42.4), '42');
  assert.equal(M.fmtRtt(4.25), '4.3');
  assert.equal(M.fmtRtt(null), '—');
  assert.equal(M.pct(1, 4), 25);
  assert.equal(M.pct(5, 4), 100);
  assert.equal(M.pct(1, 0), null);
});

test('分級：嚴重度一律帶文字，延遲依門檻分四格', () => {
  assert.deepEqual(M.cpuLevel(69.9), { sev: 'ok', text: '正常' });
  assert.deepEqual(M.cpuLevel(70), { sev: 'warn', text: '偏高' });
  assert.deepEqual(M.cpuLevel(90), { sev: 'max', text: '滿載' });
  assert.equal(M.cpuLevel(null).sev, 'none');
  assert.equal(M.memLevel(80).text, '偏高');
  assert.equal(M.memLevel(92).text, '吃緊');
  assert.equal(M.diskLevel(84.9).sev, 'ok');
  assert.equal(M.diskLevel(95).text, '快滿');
  assert.deepEqual(M.latencyLevel(60), { tier: 'good', text: '良好', bars: 4 });
  assert.deepEqual(M.latencyLevel(61), { tier: 'fair', text: '普通', bars: 3 });
  assert.deepEqual(M.latencyLevel(150), { tier: 'fair', text: '普通', bars: 3 });
  assert.deepEqual(M.latencyLevel(400), { tier: 'slow', text: '偏慢', bars: 2 });
  assert.deepEqual(M.latencyLevel(401), { tier: 'slow', text: '很慢', bars: 1 });
  assert.deepEqual(M.latencyLevel(null), { tier: 'none', text: '未連線', bars: 0 });
  assert.equal(M.latencyLevel(null, true).text, '未配對');
  assert.equal(M.spinRate(null), 0, '沒有資料不轉');
  assert.ok(M.spinRate(100) > M.spinRate(50) && M.spinRate(50) > M.spinRate(0) && M.spinRate(0) > 0, 'CPU 越高轉越快');
  assert.equal(M.spinRate(500), M.spinRate(100), '夾在 100');
});

test('deviceStats 的整理：不合格的略過、數字夾範圍、同一台只留第一筆', () => {
  assert.equal(M.normalizeStats(null), null);
  assert.equal(M.normalizeStats({ self: false }), null, '不是這台又沒有 id');
  const self = M.normalizeStats({ id: null, self: true, name: 'OFFICE', seq: 3, cpu: 140, memUsed: 20, memTotal: 10, disks: [{ name: 'C:', used: 5, total: 0 }, { name: 'D:', used: 50, total: 40 }, { used: 1, total: 2 }], netRx: -5, rttMs: 'x' });
  assert.equal(self.self, true);
  assert.equal(self.cpu, 100);
  assert.equal(self.memUsed, 10, '用量不超過總量');
  assert.deepEqual(self.disks, [{ name: 'D:', used: 40, total: 40 }], '總量不是正數或沒有名稱的磁碟略過');
  assert.equal(self.netRx, null);
  assert.equal(self.rttMs, null);
  assert.equal(M.statsKey(self), 'self');
  const list = M.parseDeviceStats([stat(), stat({ seq: 9 }), { bogus: true }, stat({ id: 'd_B' }), self]);
  assert.deepEqual(list.map((s) => [M.statsKey(s), s.seq]), [['d_A', 1], ['d_B', 1], ['self', 3]]);
  assert.deepEqual(M.parseDeviceStats('x'), []);
});

test('顯示卡與 CPU 溫度：整理、分級、格式、來源、列首標籤', () => {
  const s = M.normalizeStats(stat({
    gpus: [{ name: 'NVIDIA GeForce RTX 4070', util: 120, memUsed: 20 * GB, memTotal: 16 * GB }, { name: 'AMD Radeon(TM) Graphics', util: 'x', memUsed: 5 }, { util: 3 }, null, { name: '' }],
    cpuTemp: 67.84, cpuTempSource: 'hwinfo',
  }));
  assert.deepEqual(s.gpus, [
    { name: 'NVIDIA GeForce RTX 4070', util: 100, memUsed: 16 * GB, memTotal: 16 * GB },
    { name: 'AMD Radeon(TM) Graphics', util: null, memUsed: null, memTotal: null },
  ], '沒有名稱的略過、使用率夾住、記憶體要總量與已用都有');
  assert.equal(s.cpuTemp, 67.84);
  assert.equal(s.cpuTempSource, 'hwinfo');
  const old = M.normalizeStats(stat());
  assert.deepEqual([old.gpus, old.cpuTemp, old.cpuTempSource], [[], null, null], '舊版沒有這些欄位');
  assert.deepEqual([M.normalizeStats(stat({ cpuTemp: 'hot', cpuTempSource: 'acpi' })).cpuTemp, M.normalizeStats(stat({ cpuTemp: 'hot', cpuTempSource: 'acpi' })).cpuTempSource], [null, null]);
  assert.equal(M.normalizeStats(stat({ cpuTemp: 999 })).cpuTemp, null);
  assert.equal(M.normalizeStats(stat({ gpus: 'x' })).gpus.length, 0);

  assert.deepEqual([M.tempLevel(79.9).sev, M.tempLevel(80).text, M.tempLevel(90).text, M.tempLevel(null).sev], ['ok', '偏高', '過熱', 'none']);
  assert.deepEqual([M.gpuLevel(69).sev, M.gpuLevel(70).text, M.gpuLevel(95).text], ['ok', '偏高', '滿載']);
  assert.equal(M.fmtTemp(67.5), '68°C');
  assert.equal(M.fmtTemp(null), '—');
  assert.deepEqual([M.tempPct(-5), M.tempPct(52.5), M.tempPct(130), M.tempPct(null)], [0, 52.5, 100, null]);
  assert.deepEqual(M.tempSource('lhm'), { short: 'LHM', long: 'LibreHardwareMonitor' });
  assert.match(M.tempSource('acpi').long, /未必是 CPU/);
  assert.deepEqual(M.tempSource('some-new-tool'), { short: 'some-new', long: 'some-new-tool' }, '不認得的照原樣');
  assert.deepEqual(M.tempSource('__proto__'), { short: '__proto_', long: '__proto__' }, '不會拿到原型上的東西');
  assert.equal(M.tempSource(null), null);

  assert.deepEqual(M.gpuLabels([{ name: 'NVIDIA GeForce RTX 4070' }]), ['GPU'], '只有一張');
  assert.deepEqual(M.gpuLabels([{ name: 'NVIDIA GeForce RTX 4070' }, { name: 'AMD Radeon(TM) Graphics' }]), ['NVIDIA', 'AMD']);
  assert.deepEqual(M.gpuLabels([{ name: 'NVIDIA RTX A6000' }, { name: 'Intel(R) UHD Graphics 770' }, { name: 'NVIDIA RTX A6000' }, { name: 'Matrox G200eW3' }]),
    ['NVIDIA1', 'Intel', 'NVIDIA2', 'GPU4'], '同廠牌加編號、看不出廠牌的依位置');
  assert.deepEqual(M.gpuLabels([]), []);
  assert.deepEqual(M.gpuLabels(null), []);
  assert.equal(M.gpuVendor('Intel(R) Arc(TM) A770 Graphics'), 'Intel');
  assert.equal(M.gpuVendor('Radeon RX 7900 XTX'), 'AMD');
  assert.equal(M.gpuVendor('Qualcomm(R) Adreno(TM) X1-85 GPU'), 'Adreno');
  assert.equal(M.gpuVendor('Microsoft Basic Display Adapter'), null);
  assert.equal(M.gpuVendor('Camden'), null, '字中間的 amd 不算');
});

test('歷史：seq 變了才加點、上限 HISTORY_MAX、時間往回走從頭畫', () => {
  const hist = new Map();
  const a = M.parseDeviceStats([stat({ seq: 1, ageMs: 500 })]);
  assert.deepEqual(M.mergeHistory(hist, a, 10000), ['d_A']);
  assert.deepEqual(M.mergeHistory(hist, a, 11000), [], '同一個 seq 在別台更新時重送：不加點');
  assert.equal(hist.get('d_A').points.length, 1);
  assert.equal(hist.get('d_A').points[0].t, 9500, '樣本時間＝收到時間 − ageMs');
  assert.equal(hist.get('d_A').points[0].mem, 50);
  M.mergeHistory(hist, M.parseDeviceStats([stat({ seq: 2 })]), 12000);
  assert.equal(hist.get('d_A').points.length, 2);
  M.mergeHistory(hist, M.parseDeviceStats([stat({ seq: 1 })]), 13000);
  assert.equal(hist.get('d_A').points.length, 3, 'seq 變小（宿主重開）也算新樣本');
  M.mergeHistory(hist, M.parseDeviceStats([stat({ seq: 4, ageMs: 9000 })]), 13500);
  assert.equal(hist.get('d_A').points.length, 1, '樣本時間比上一點早：從頭畫');
  for (let i = 0; i < M.HISTORY_MAX + 15; i++) M.mergeHistory(hist, M.parseDeviceStats([stat({ seq: 100 + i })]), 20000 + i * 1000);
  assert.equal(hist.get('d_A').points.length, M.HISTORY_MAX);
  M.mergeHistory(hist, [], 999999);
  assert.ok(hist.has('d_A'), '這則沒有的電腦保留歷史');
});

test('電腦清單（桌面）：這台第一、在線的照 remote.devices 的順序、沒資料的在線、離線排後、撤銷不列', () => {
  const remote = { mode: 'desktop', deviceId: 'd_SELF', devices: [
    dev('d_OFF', { online: false, lastSeenAt: 500 }),
    dev('d_SELF', { self: true, name: '我的電腦' }),
    dev('d_B'),
    dev('d_REV', { revoked: { at: 1, reason: 'retired' } }),
    dev('d_ND'),
  ] };
  const entries = M.parseDeviceStats([stat({ id: 'd_SELF', self: true, name: 'OFFICE-PC' }), stat({ id: 'd_B' }), stat({ id: 'd_NEW' }), stat({ id: 'd_OFF' })]);
  const rows = M.buildRows({ remote, entries, receivedAt: 50000, now: 50000, isHub: false });
  assert.deepEqual(rows.map((r) => [r.key, r.state]), [['self', 'live'], ['d_B', 'live'], ['d_ND', 'nodata'], ['d_NEW', 'live'], ['d_OFF', 'offline']]);
  assert.equal(rows[0].name, '我的電腦', '這台的名稱優先用 remote.devices 的顯示名稱');
  assert.equal(rows[0].paired, true);
  assert.equal(rows[3].name, 'd_NEW', '不在清單裡的用 id 開頭（短的照原樣）');
  assert.equal(rows[4].lastSeenAt, 500);
  assert.equal(rows[4].stats, null, '離線的不看統計');
  assert.deepEqual(M.onlineSummary(rows), { online: 4, total: 5, text: '4 / 5 在線' });
});

test('電腦清單：未配對的桌面只有這台；還沒收到資料是 waiting；超過 STALE_MS 是 stale', () => {
  const none = M.buildRows({ remote: null, entries: [], now: 1000, isHub: false });
  assert.deepEqual(none.map((r) => [r.key, r.state, r.name, r.paired]), [['self', 'waiting', '這台電腦', false]]);
  const entries = M.parseDeviceStats([stat({ id: null, self: true, name: 'HOME', ageMs: 1000 })]);
  const fresh = M.buildRows({ remote: null, entries, receivedAt: 10000, now: 10000 + M.STALE_MS - 1001, isHub: false });
  assert.equal(fresh[0].state, 'live');
  assert.equal(fresh[0].name, 'HOME', '未配對時用宿主送的電腦名稱');
  const stale = M.buildRows({ remote: null, entries, receivedAt: 10000, now: 10000 + M.STALE_MS, isHub: false });
  assert.equal(stale[0].state, 'stale');
  assert.equal(stale[0].sampleAt, 9000);
});

test('電腦清單（瀏覽器版）：沒有這台、self 的統計不採用', () => {
  const remote = { mode: 'browser', deviceId: null, devices: [dev('d_A'), dev('d_C', { online: false })] };
  const entries = M.parseDeviceStats([stat({ id: 'd_A' }), stat({ id: null, self: true })]);
  const rows = M.buildRows({ remote, entries, receivedAt: 0, now: 0, isHub: true });
  assert.deepEqual(rows.map((r) => [r.key, r.state]), [['d_A', 'live'], ['d_C', 'offline']]);
  assert.deepEqual(M.buildRows({ remote: undefined, entries: [], now: 0, isHub: true }), []);
  assert.equal(M.shortId('d_7K3MABCDEF'), 'd_7K3M…');
});

test('副標、刻度', () => {
  assert.equal(M.cpuSubtitle({ cpuName: 'AMD Ryzen 9', cores: 16 }), 'AMD Ryzen 9', '核心數在 CPU 量表下面');
  assert.equal(M.cpuSubtitle({ cpuName: 'Xeon' }, 'Hub 主機'), 'Hub 主機 · Xeon', '主機前面加電腦名稱');
  assert.equal(M.cpuSubtitle({ cpuName: null, cores: null }), '');
  assert.equal(M.cpuSubtitle(null, 'HUB-PC'), 'HUB-PC');
  assert.equal(M.niceMax(0, 1), 1);
  assert.equal(M.niceMax(3), 5);
  assert.equal(M.niceMax(120), 200);
  assert.equal(M.niceMax(500), 500);
  assert.equal(M.niceMax(501), 1000);
  assert.equal(M.niceMax(10, 100), 100, '至少 floor');
  const pts = [{ t: 0, rx: 3000, tx: 9000 }, { t: 1, rx: 100, tx: null }];
  assert.equal(M.scaleFor(pts, ['rx', 'tx'], 1), 10000, '上傳下載共用一個刻度');
  assert.equal(M.scaleFor(pts, ['rx'], 1), 5000);
  assert.equal(M.scaleFor([], ['rx'], M.MIN_RATE_SCALE), 10000, '沒有點時用 floor 往上取整');
});

test('走勢圖的路徑：最新一點在右緣、超出視窗的不畫、null 斷開、只有一點畫一小段', () => {
  const t1 = 500000;
  const pts = [
    { t: t1 - M.WINDOW_MS - 1, v: 9 },
    { t: t1 - M.WINDOW_MS, v: 0 },
    { t: t1 - M.WINDOW_MS / 2, v: 5 },
    { t: t1 - 1000, v: null },
    { t: t1, v: 10 },
  ];
  const p = M.sparkPaths(pts, 'v', 240, 40, 10);
  assert.equal(p.line, 'M0 40L120 20M240 0L240 0', '視窗外的點不畫、null 斷開；最後一段只有一點時往右補 1.5（夾在右緣）');
  assert.ok(p.area.startsWith('M0 40L0 40L120 20L120 40Z'), p.area);
  assert.deepEqual(M.sparkPaths([], 'v', 240, 40, 10), { line: '', area: '' });
  assert.deepEqual(M.sparkPaths(pts, 'v', 240, 40, 0), { line: '', area: '' });
  assert.equal(M.windowPoints(pts).length, 4);
  const hp = M.hoverPoint(pts, 0.5);
  assert.equal(hp.point.t, t1 - M.WINDOW_MS / 2);
  assert.equal(hp.frac, 0.5);
  assert.equal(M.hoverPoint(pts, 2).point.t, t1, '超出右緣夾在最新一點');
  assert.equal(M.hoverPoint([], 0.5), null);
});

test('MyriagentHub 主機（hub）：整理、最上面一張、不算進電腦計數、歷史的鍵是 hub', () => {
  assert.equal(M.normalizeHub(null), null);
  assert.equal(M.normalizeHub(undefined), null);
  assert.equal(M.normalizeHub([1]), null);
  const hub = M.normalizeHub({ name: 'HUB-PC', seq: 7, ageMs: 300, cpu: 22.5, cores: 8, cpuName: 'Xeon', memUsed: 20 * GB, memTotal: 64 * GB,
    disks: [{ name: 'C:', used: 1, total: 2 }], netRx: 10, netTx: 20, uptimeSec: 9, devices: 3, browsers: 2.4, rttMs: 99, id: 'd_X', self: true });
  assert.equal(hub.hub, true);
  assert.equal(hub.id, null);
  assert.equal(hub.self, false, '伺服器帶 self 也不算這台');
  assert.equal(hub.rttMs, null, '主機沒有延遲');
  assert.equal(hub.browsers, 2);
  assert.equal(hub.name, 'HUB-PC');
  assert.equal(M.statsKey(hub), 'hub');
  assert.equal(M.normalizeHub({ name: 'X', devices: -1 }).devices, null);
  assert.equal(M.hubConnections(hub), '3 台電腦、2 個瀏覽器');
  assert.equal(M.hubConnections({ devices: null, browsers: null }), '');

  const hist = new Map();
  assert.deepEqual(M.mergeHistory(hist, [hub], 1000), ['hub']);
  const remote = { mode: 'browser', deviceId: null, devices: [dev('d_A'), dev('d_C', { online: false })] };
  const rows = M.buildRows({ remote, entries: M.parseDeviceStats([stat({ id: 'd_A' })]), hub, receivedAt: 1000, now: 1000, isHub: true });
  assert.deepEqual(rows.map((r) => [r.key, r.state]), [['hub', 'live'], ['d_A', 'live'], ['d_C', 'offline']]);
  assert.equal(rows[0].name, 'Hub 主機');
  assert.equal(rows[0].machine, 'HUB-PC');
  assert.equal(rows[0].sampleAt, 700);
  assert.deepEqual(M.onlineSummary(rows), { online: 1, total: 2, text: '1 / 2 在線' }, '主機不算進電腦');
  const stale = M.buildRows({ remote: null, entries: [], hub, receivedAt: 1000, now: 1000 + M.STALE_MS, isHub: false });
  assert.deepEqual(stale.map((r) => [r.key, r.state]), [['hub', 'stale'], ['self', 'waiting']], '桌面：主機在這台之前');
  assert.deepEqual(M.buildRows({ remote: null, entries: [], hub: null, now: 0, isHub: false }).map((r) => r.key), ['self'], 'hub 是 null 不顯示');
});

test('速率刻度取 1024 進位的整數、橫軸長度夾在 MIN_SPAN_MS～WINDOW_MS', () => {
  assert.equal(M.niceRate(0), 10 * KB, '至少 MIN_RATE_SCALE（8 KB）往上取整');
  assert.equal(M.niceRate(2.6 * MB), 5 * MB);
  assert.equal(M.niceRate(400 * KB), 500 * KB);
  assert.equal(M.niceRate(600 * KB), 1 * MB, '數字到 500 以上換大一級的單位');
  assert.equal(M.niceRate(9.8 * MB), 10 * MB);
  assert.equal(M.fmtRate(M.niceRate(2.6 * MB)), '5 MB/s');
  assert.equal(M.scaleFor([{ rx: 2.6 * MB, tx: 1 }], ['rx', 'tx'], M.MIN_RATE_SCALE, M.niceRate), 5 * MB);
  assert.equal(M.spanOf([]), M.MIN_SPAN_MS);
  assert.equal(M.spanOf([{ t: 0 }, { t: 5000 }]), M.MIN_SPAN_MS, '剛開始的幾點不拉成整片');
  assert.equal(M.spanOf([{ t: 0 }, { t: 60000 }]), 60000, '還不到 WINDOW_MS：拉滿現有的長度');
  assert.equal(M.spanOf([{ t: 0 }, { t: 500000 }]), M.WINDOW_MS);
  const p = M.sparkPaths([{ t: 0, v: 10 }, { t: 15000, v: 0 }], 'v', 300, 30, 10);
  assert.equal(p.line, 'M150 0L300 30', '15 秒的歷史畫在 30 秒寬的右半邊');
  assert.equal(M.hoverPoint([{ t: 0, v: 1 }, { t: 15000, v: 2 }], 0.5).point.t, 0);
});

test('monitor.js、monitorrules.js 沒有 HTML 寫入點；規則模組沒有 import', () => {
  const sinks = /innerHTML|outerHTML|insertAdjacentHTML|document\.write|\beval\(|new Function|createContextualFragment|setHTMLUnsafe|parseHTMLUnsafe|DOMParser|srcdoc/;
  for (const name of ['monitor.js', 'monitorrules.js']) {
    const lines = readFileSync(path.join(webDir, name), 'utf8').split('\n').filter((l) => sinks.test(l));
    assert.deepEqual(lines, [], name);
  }
  assert.ok(!/^\s*import\s/m.test(source), 'monitorrules.js 不能有 import（以 data: URL 匯入測試）');
  const css = readFileSync(path.join(webDir, 'index.html'), 'utf8');
  assert.ok(css.includes('<link rel="stylesheet" href="monitor.css">'));
  assert.ok(css.includes('<script type="module" src="app.js"></script>'), 'app.js 錨點不變');
});

test('動畫倍速跟著負載：CPU、顯示卡、溫度、網路，不明是 1', () => {
  assert.equal(M.loadRate(0), M.RATE_MIN);
  assert.equal(M.loadRate(100), M.RATE_MAX);
  assert.equal(M.loadRate(50), 1.8);
  assert.equal(M.loadRate(150), M.RATE_MAX, '夾在範圍內');
  assert.equal(M.loadRate(null), 1);
  assert.equal(M.tempRate(30), M.RATE_MIN);
  assert.equal(M.tempRate(95), M.RATE_MAX);
  assert.ok(M.tempRate(70) > 1 && M.tempRate(70) < M.RATE_MAX);
  assert.equal(M.tempRate(undefined), 1);
  assert.equal(M.netRate(500), M.RATE_MIN);
  assert.equal(M.netRate(100 * MB), M.RATE_MAX);
  assert.ok(M.netRate(1 * MB) > M.netRate(10 * KB) && M.netRate(10 * MB) > M.netRate(1 * MB), '越多越快');
  assert.equal(M.netRate(-5), 1);
});

test('倍速每秒只往目標走一段：幾秒內連續變快變慢，最後剛好到目標', () => {
  assert.equal(M.easeRate(undefined, 2), 2, '第一次直接用目標');
  let r = 0.6;
  const seen = [r];
  for (let i = 0; i < 12; i++) { r = M.easeRate(r, 3); seen.push(r); }
  assert.equal(r, 3);
  for (let i = 1; i < seen.length; i++) {
    assert.ok(seen[i] >= seen[i - 1], '不倒退');
    assert.ok(seen[i] - seen[i - 1] <= (3 - 0.6) * 0.45 + 1e-9, '每秒變化不超過剩下的 45%');
  }
  assert.ok(M.easeRate(3, 0.6) > 0.6 && M.easeRate(3, 0.6) < 3, '減速也是一段一段');
  assert.equal(M.easeRate(1.99, 2), 2, '差很小直接到');
  assert.equal(M.easeRate(1.5, NaN), 1.5);
});
