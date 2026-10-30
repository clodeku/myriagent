// text-wrap: pretty 的排版成本（第十批第 9 項，2026-10-27）：已經結束的回覆段落套 pretty（避免最後一行只剩一個字）之前，
// 量它比一般換行多花多少排版時間。用法：node textwrap-bench.mjs [--paras 400] [--rounds 12]
// 做法：本機 Chrome（channel: 'chrome'，同 probe.mjs）無頭開一頁，放 N 段中英混排的段落（固定內容、不用亂數），
// 每一輪改容器寬度（720 ↔ 704px）強迫整頁重新換行，以 CDP Performance.getMetrics 的 LayoutDuration 累計；
// 同一頁輪流量 wrap（一般）與 pretty，各 rounds 輪、交錯進行，回報每輪的中位數與比例。
// 這是最壞的情況：app 裡對話項目有 content-visibility（畫面外的不排版）、只有寬度改變（拖窗格、開關側欄）或項目結束那一下才重新換行。
import { chromium } from 'playwright';

const arg = (name, def) => { const i = process.argv.indexOf('--' + name); return i > 0 ? Number(process.argv[i + 1]) : def; };
const PARAS = arg('paras', 400);
const ROUNDS = arg('rounds', 12);

const SENT = [
  '這一輪改了 items.js 的工具卡，委派給其他帳戶時整列換成那個帳戶的顏色，',
  'MCP 的工具寫成伺服器與工具名稱，結果被截斷時也認得子對話的 id 與完成狀態；',
  'dotnet build Myriagent.slnx -c Release 通過，桌面 204 項測試與 522 項網頁規則都過了。',
  '換帳戶的提示底下多一條交接條，從舊帳戶的顏色漸變到新帳戶，剛出現時有一道光滑過。',
  'The placeholder draws a dotted grid and corner brackets with background layers only.',
];
const para = (i) => { let t = ''; for (let k = 0; k < 2 + (i % 4); k++) t += SENT[(i + k) % SENT.length]; return t; };

const html = '<!doctype html><meta charset="utf-8"><style>body{margin:0;font:14px/1.6 "Segoe UI","Microsoft JhengHei UI",sans-serif}'
  + '#c{width:720px}#c.pretty p{text-wrap:pretty}#c.wrap p{text-wrap:wrap}</style><div id="c" class="wrap">'
  + Array.from({ length: PARAS }, (_, i) => '<p>' + para(i) + '</p>').join('') + '</div>';

const browser = await chromium.launch({ channel: 'chrome', headless: true });
const page = await browser.newPage();
await page.setContent(html);
const cdp = await page.context().newCDPSession(page);
await cdp.send('Performance.enable');
const layoutMs = async () => (await cdp.send('Performance.getMetrics')).metrics.find((m) => m.name === 'LayoutDuration').value * 1000;

async function round(mode, i) {
  await page.evaluate(([m, w]) => { const c = document.getElementById('c'); c.className = m; c.style.width = w + 'px'; void c.offsetHeight; }, [mode, i % 2 ? 720 : 704]);
  const before = await layoutMs();
  await page.evaluate((w) => { const c = document.getElementById('c'); c.style.width = w + 'px'; void c.offsetHeight; }, i % 2 ? 704 : 720);
  return (await layoutMs()) - before;
}

const res = { wrap: [], pretty: [] };
for (let i = 0; i < 3; i++) { await round('wrap', i); await round('pretty', i); } // 暖身
for (let i = 0; i < ROUNDS; i++) { res.wrap.push(await round('wrap', i)); res.pretty.push(await round('pretty', i)); }
const paras = await page.evaluate(() => document.querySelectorAll('#c p').length);
await browser.close();

const med = (a) => { const s = [...a].sort((x, y) => x - y); return s[Math.floor(s.length / 2)]; };
const w = med(res.wrap), p = med(res.pretty);
console.log(JSON.stringify({ paras, rounds: ROUNDS, wrapMs: Math.round(w * 100) / 100, prettyMs: Math.round(p * 100) / 100,
  ratio: Math.round((p / w) * 100) / 100, perParaUs: Math.round(((p - w) / PARAS) * 1000 * 10) / 10 }, null, 2));
