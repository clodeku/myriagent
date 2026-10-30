// 對話框的燈號（宿主的 activity，docs/web-protocol.md；composer.js 的 .ci-leds）用到的純規則：沒有 import、沒有 DOM
// （tools/ui-probe/activityrules.test.mjs 以 data: URL 匯入測試）。燈只在宿主量到真的數字時亮：
// 資料流＝app 與這個對話的 CLI 之間串流的位元組、硬碟＝這個對話的程序樹讀寫的量（扣掉那些串流）、網路＝整台電腦硬體網卡的收送量。
// token 數後面的 CPU 波形（2026-10-26 使用者要求）＝這個對話的程序樹（CLI 與它執行的工具）加上它開出、脫離 CLI 的背景程式的處理器使用率，同工作管理員「處理程序」頁的算法。

/** 燈號：運行、資料流、網路、硬碟（畫面上依這個順序） */
export const LEDS = ['run', 'data', 'net', 'disk'];

/** 燈號的字 */
export const LED_LABEL = { run: '運行', data: '資料', net: '網路', disk: '硬碟' };

/** 一次有動靜燈亮多久：宿主每 250ms 取一次樣，持續有動靜時看起來像硬碟燈在閃 */
export const LED_HOLD_MS = 160;

/** 系統要求減少動態或設定關掉動態效果時亮久一點（連續的動靜看起來是一直亮著，不閃） */
export const LED_HOLD_CALM_MS = 900;

/** 整台電腦的網路：一次取樣收送合計少於這麼多不亮（背景零星的封包） */
export const NET_MIN_BYTES = 1024;

const num = (v) => {
  const n = Number(v);
  return Number.isFinite(n) && n > 0 ? n : 0;
};

/**
 * activity 訊息 → 這個對話各燈這一次要不要亮、每秒多少：data／disk 是這個對話的增量（訊息裡沒有它＝這次沒動靜），net 是整台電腦的
 * （收送分開算速率）。ms 是這次取樣涵蓋的毫秒數
 */
export function ledPulse(message, conversationId) {
  const m = message && typeof message === 'object' ? message : {};
  const ms = Math.max(1, num(m.ms) || 1);
  const list = Array.isArray(m.conversations) ? m.conversations : [];
  const item = list.find((c) => c && c.id === conversationId) || null;
  const rate = (bytes) => (bytes * 1000) / ms;
  const data = item ? num(item.data) : 0;
  const disk = item ? num(item.disk) : 0;
  const rx = m.net ? num(m.net.rx) : 0;
  const tx = m.net ? num(m.net.tx) : 0;
  return {
    data: { on: data > 0, rate: rate(data) },
    disk: { on: disk > 0, rate: rate(disk) },
    net: { on: rx + tx >= NET_MIN_BYTES, rate: rate(rx + tx), rx: rate(rx), tx: rate(tx) },
  };
}

/** 每秒位元組 → 「12.3 KB/s」（1024 進位；100 以上不帶小數） */
export function rateText(bytesPerSec) {
  let v = num(bytesPerSec);
  const units = ['B/s', 'KB/s', 'MB/s', 'GB/s'];
  let i = 0;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  const text = i === 0 || v >= 100 ? String(Math.round(v)) : v.toFixed(1);
  return text + ' ' + units[i];
}

/** 距離 at 多久（秒）的說法：剛剛、3 秒前、2 分鐘前 */
function agoText(at, now) {
  const s = Math.max(0, Math.round((now - at) / 1000));
  if (s < 1) return '剛剛';
  if (s < 60) return s + ' 秒前';
  return Math.floor(s / 60) + ' 分鐘前';
}

const SOURCE = {
  run: '運行：這個對話正在執行時亮著',
  data: '資料流：app 與這個對話的 CLI 之間串流的資料（模型的回覆、工具的結果、送出的訊息）',
  net: '網路：整台電腦的網路收送（硬體網卡，不分對話）',
  disk: '硬碟：這個對話的程序（CLI 與它執行的工具）讀寫的量，扣掉和 app 之間的串流（Windows 的 I/O 計數器，含工具輸出的管線）',
};

/**
 * 燈的說明（滑過時）：資料從哪裡來，加上最近一次量到的速率與時間。last 是 { rate, at, rx, tx } 或 null（還沒量到過）；
 * run 的 last 是 { running }
 */
export function ledTitle(kind, last, now) {
  const head = SOURCE[kind] || '';
  if (kind === 'run') return head + '\n' + (last && last.running ? '目前：執行中' : '目前：閒置');
  if (!last || !Number.isFinite(last.at)) return head + '\n最近：還沒有動靜';
  const when = '（' + agoText(last.at, now) + '）';
  if (kind === 'net') return head + '\n最近：↓ ' + rateText(last.rx) + '　↑ ' + rateText(last.tx) + when;
  return head + '\n最近：' + rateText(last.rate) + when;
}

// ───── CPU 波形（composer.js 的 .ci-cpu，2026-10-26 使用者選的「極光面積、依負載變色」）─────
// 宿主每次取樣（執行中每 250ms）畫一次 canvas：底部漸隱的填色面積＋頂端亮線，顏色沿著時間依每一格的負載（青 → 綠 → 黃 → 橘 → 紅）。
// 兩格之間由合成器平移（canvas 的 transform 動畫，主執行緒不參與每個影格）：canvas 比看得到的窗口寬兩格、從左邊多出一格開始，
// 最新的一格畫在 canvas 的右緣（剛好在窗口外），平移一格的時間把它推進來；下一格到時重畫（每一格往左移一格）並從目前的位置接著移（cpuSlideFrom），畫面連續不跳

/** 看得到幾格：每 250ms 一格＝最近 5.5 秒 */
export const CPU_WAVE_LEN = 22;

/** 一格幾個 CSS 像素（窗口 66px 寬） */
export const CPU_WAVE_STEP = 3;

/** 波形高度（CSS 像素） */
export const CPU_WAVE_HEIGHT = 16;

/** 記幾格：看得到的加上窗口左右各一格與平移中露出的一格 */
export const CPU_WAVE_KEEP = CPU_WAVE_LEN + 3;

/** canvas 的寬度（CSS 像素）：比窗口寬兩格，放在窗口左邊外一格 */
export const CPU_WAVE_CANVAS = (CPU_WAVE_LEN + 2) * CPU_WAVE_STEP;

/** 波形高度的下限（%）：使用率很低時（CLI 在等模型）也矮，不會被自動縮放放大成滿格 */
export const CPU_WAVE_FLOOR = 5;

/** 刻度往下收的速度：尖峰離開波形後每格收 15%，不會一下子全部跳高 */
export const CPU_WAVE_DECAY = 0.85;

/** 多久沒收到這個對話的 CPU 就收起（宿主只量執行中或有背景工作的對話；一輪結束就不再送） */
export const CPU_STALE_MS = 1500;

/** 視窗沒有焦點、或要求減少動態時，最快多久重畫一次（資料照樣每格都記；不平移，一格一格跳） */
export const CPU_SLOW_DRAW_MS = 1000;

/** 負載的顏色（深色主題：亮；淺色主題：深，在白底上看得清楚），由低到高 */
export const CPU_HEAT = {
  dark: ['#22d3ee', '#34d399', '#facc15', '#fb923c', '#f43f5e'],
  light: ['#0e7490', '#047857', '#a16207', '#c2410c', '#be123c'],
};

/** activity 訊息裡這個對話的 CPU 使用率（0～100）；沒有（沒量程序樹）回 null */
export function cpuOf(message, conversationId) {
  const list = message && Array.isArray(message.conversations) ? message.conversations : [];
  const item = list.find((c) => c && c.id === conversationId);
  if (!item || item.cpu === undefined || item.cpu === null) return null;
  const v = Number(item.cpu);
  return Number.isFinite(v) ? Math.min(100, Math.max(0, v)) : null;
}

/** 記一格：新的放最後，超過 keep 格丟掉最舊的（不改原陣列） */
export function pushCpu(history, value, keep = CPU_WAVE_KEEP) {
  const h = Array.isArray(history) ? history.slice(-(keep - 1)) : [];
  h.push(value);
  return h;
}

/** 波形的刻度（滿格是多少 %）：看得到的這幾格的最高值、上一個刻度往下收一點、下限，三者取大 */
export function cpuWaveScale(prevScale, history, len = CPU_WAVE_LEN) {
  const h = Array.isArray(history) ? history.slice(-len) : [];
  const prev = Number.isFinite(prevScale) ? prevScale * CPU_WAVE_DECAY : 0;
  return Math.min(100, Math.max(CPU_WAVE_FLOOR, ...h, prev));
}

/**
 * 每一格在 canvas 上的位置（舊的在前）：x（CSS 像素，最新的在 canvas 右緣，越舊越往左一格）、v（%）、a（高度 0～1，依刻度）。
 * 只回 canvas 上畫得到的（x ≥ 0）
 */
export function cpuWavePoints(history, scale, step = CPU_WAVE_STEP, width = CPU_WAVE_CANVAS) {
  const h = Array.isArray(history) ? history : [];
  const s = scale > 0 ? scale : CPU_WAVE_FLOOR;
  return h.map((v, i) => ({ x: width - (h.length - 1 - i) * step, v, a: Math.min(1, Math.max(0, v / s)) })).filter((p) => p.x >= 0);
}

/** 負載 → 顏色（rgb 字串）：依開根號的比例在 CPU_HEAT 之間內插——多核心的電腦上單一核心滿載只有幾 %，開根號讓低負載也看得出變化 */
export function cpuColor(pct, theme) {
  const stops = CPU_HEAT[theme === 'light' ? 'light' : 'dark'];
  const t = Math.sqrt(Math.min(100, Math.max(0, Number(pct) || 0)) / 100) * (stops.length - 1);
  const i = Math.min(stops.length - 2, Math.floor(t));
  const f = t - i;
  const rgb = (s) => [1, 3, 5].map((k) => parseInt(s.slice(k, k + 2), 16));
  const a = rgb(stops[i]);
  const b = rgb(stops[i + 1]);
  return 'rgb(' + a.map((x, k) => Math.round(x + (b[k] - x) * f)).join(', ') + ')';
}

/**
 * 下一段平移從哪裡開始（px，往左移到 -step 為止）：上一段做到 progress（0～1；沒有在移、或已經移完是 null／1）。
 * 重畫讓每一格往左一格，所以新的起點是目前的位置加一格——下一格早到時從右邊一點的地方開始、這段稍微快一點，畫面不跳
 */
export function cpuSlideFrom(progress, step = CPU_WAVE_STEP) {
  const p = progress === null || progress === undefined || !Number.isFinite(progress) ? 1 : Math.min(1, Math.max(0, progress));
  return step - p * step;
}

/** 這一段平移多久：這次取樣涵蓋的時間（≈ 到下一格的時間），限在 120～1000ms */
export function cpuSlideMs(ms) {
  const v = Number(ms);
  return Math.min(1000, Math.max(120, Number.isFinite(v) ? v : 250));
}

/** 使用率的字：10% 以下帶一位小數（多核心的電腦上單一核心滿載只有幾 %），以上取整數 */
export function cpuText(pct) {
  const v = Math.min(100, Math.max(0, Number(pct) || 0));
  return (v < 10 ? v.toFixed(1) : String(Math.round(v))) + '%';
}

/** activity 訊息裡這個對話的記憶體（位元組，和 CPU 同一組程序的私人工作集加總）；沒有回 null */
export function memOf(message, conversationId) {
  const list = message && Array.isArray(message.conversations) ? message.conversations : [];
  const item = list.find((c) => c && c.id === conversationId);
  if (!item || item.mem === undefined || item.mem === null) return null;
  const v = Number(item.mem);
  return Number.isFinite(v) && v >= 0 ? v : null;
}

/** 記憶體的字（1024 進位）：1 GB 以下寫整數 MB，10 GB 以下帶一位小數，以上取整數 */
export function memText(bytes) {
  const v = Math.max(0, Number(bytes) || 0);
  const mb = v / 1048576;
  if (Math.round(mb) < 1024) return Math.max(mb > 0 ? 1 : 0, Math.round(mb)) + ' MB';
  const gb = mb / 1024;
  return (gb < 10 ? gb.toFixed(1) : String(Math.round(gb))) + ' GB';
}

/** CPU 與記憶體的說明（滑過時）：量的是什麼、顏色的意思、最近的平均與最高、目前的記憶體 */
export function cpuTitle(history, mem) {
  const h = Array.isArray(history) ? history : [];
  const memLine = Number.isFinite(mem) && mem !== null
    ? '\n記憶體 ' + memText(mem) + '：同一組程序的私人工作集加總（同工作管理員的「記憶體」欄）' : '';
  const head = 'CPU：這個對話的 CLI、子代理、它執行的工具（建置、測試…）與它開出的背景程式用的處理器，佔整台電腦的百分比（同工作管理員「處理程序」頁）；'
    + '模型在雲端思考不算、多個對話共用的建置常駐程序（MSBuild 節點、VBCSCompiler）不算。'
    + '顏色依負載：青、綠是低，黃、橘、紅是高';
  if (!h.length) return head + memLine;
  const avg = h.reduce((a, b) => a + b, 0) / h.length;
  return head + '\n最近 ' + Math.round((h.length * 250) / 1000) + ' 秒：平均 ' + cpuText(avg) + '、最高 ' + cpuText(Math.max(...h)) + memLine;
}
