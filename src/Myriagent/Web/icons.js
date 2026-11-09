// 圖示：16×16 描邊圖示（currentColor），以 <template> 快取後複製，避免每次重新解析
const A = 'fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"';
const svg = (body, extra = '') => `<svg viewBox="0 0 16 16" ${A} aria-hidden="true" focusable="false" ${extra}>${body}</svg>`;
const FILL = 'fill="currentColor" stroke="none"';

/** 齒輪外形：8 齒，程式產生路徑（自繪，不引用外部圖示集） */
function gearPath() {
  const cx = 8, cy = 8, ro = 6.7, ri = 5.1, n = 8, tw = (Math.PI * 2) / n;
  const p = (r, a) => `${(cx + r * Math.cos(a)).toFixed(2)} ${(cy + r * Math.sin(a)).toFixed(2)}`;
  let d = '';
  for (let i = 0; i < n; i++) {
    const c = i * tw - Math.PI / 2;
    const rootStart = c - 0.3 * tw, toothStart = c - 0.17 * tw, toothEnd = c + 0.17 * tw, rootEnd = c + 0.3 * tw;
    d += (i === 0 ? `M${p(ri, rootStart)}` : `L${p(ri, rootStart)}`);
    d += `L${p(ro, toothStart)}A${ro} ${ro} 0 0 1 ${p(ro, toothEnd)}L${p(ri, rootEnd)}`;
    d += `A${ri} ${ri} 0 0 1 ${p(ri, c + 0.7 * tw)}`;
  }
  return d + 'Z';
}

const FILE = '<path d="M9.5 1.8H4.6A1.6 1.6 0 0 0 3 3.4v9.2a1.6 1.6 0 0 0 1.6 1.6h6.8a1.6 1.6 0 0 0 1.6-1.6V5.3z"/><path d="M9.5 1.8v3.5H13"/>';

export const ICONS = {
  sidebar: svg('<rect x="2" y="2.5" width="12" height="11" rx="2"/><path d="M6 2.5v11"/>'),
  chat: svg('<path d="M3.2 3h9.6A1.2 1.2 0 0 1 14 4.2v6.1a1.2 1.2 0 0 1-1.2 1.2H8.4L5.3 14v-2.5H3.2A1.2 1.2 0 0 1 2 10.3V4.2A1.2 1.2 0 0 1 3.2 3z"/>'),
  gauge: svg('<path d="M2.4 11.2a5.6 5.6 0 1 1 11.2 0"/><path d="m8 11 2.6-3.4"/><circle cx="8" cy="11" r="1" ' + FILL + '/>'),
  // 電腦狀態（右側面板）：螢幕裡一條心跳線
  pulse: svg('<rect x="1.8" y="2.6" width="12.4" height="9" rx="1.6"/><path d="M4 7.4h1.7l1-2.1 1.6 4 1-1.9H12M5.6 14h4.8"/>'),
  // 伺服器（電腦狀態面板的 MyriagentHub 主機）：兩層機櫃
  desktop: svg('<rect x="1.8" y="2.4" width="12.4" height="8.4" rx="1.3"/><path d="M5.4 13.6h5.2M8 10.8v2.8"/>'),
  server: svg('<rect x="2.4" y="2.2" width="11.2" height="4.8" rx="1.2"/><rect x="2.4" y="9" width="11.2" height="4.8" rx="1.2"/><path d="M5 4.6h.1M5 11.4h.1M8.2 4.6h3M8.2 11.4h3"/>'),
  gear: svg(`<path d="${gearPath()}"/><circle cx="8" cy="8" r="2.1"/>`),
  plus: svg('<path d="M8 3v10M3 8h10"/>'),
  minus: svg('<path d="M3.5 8h9"/>'),
  split: svg('<rect x="2" y="2.5" width="12" height="11" rx="2"/><path d="M8 2.5v11"/>'),
  x: svg('<path d="m4.2 4.2 7.6 7.6M11.8 4.2l-7.6 7.6"/>'),
  more: svg('<circle cx="3.5" cy="8" r="1.15" ' + FILL + '/><circle cx="8" cy="8" r="1.15" ' + FILL + '/><circle cx="12.5" cy="8" r="1.15" ' + FILL + '/>'),
  chevDown: svg('<path d="m4.5 6.2 3.5 3.5 3.5-3.5"/>'),
  chevRight: svg('<path d="m6.2 4.5 3.5 3.5-3.5 3.5"/>'),
  chevLeft: svg('<path d="M9.8 4.5 6.3 8l3.5 3.5"/>'),
  chevUp: svg('<path d="m4.5 9.8 3.5-3.5 3.5 3.5"/>'),
  paneExpand: svg('<path d="M9.5 2.5h4v4"/><path d="M13.5 2.5 9 7"/><path d="M6.5 13.5h-4v-4"/><path d="M2.5 13.5 7 9"/>'),
  paneRestore: svg('<path d="M9 3v4h4"/><path d="M13.5 2.5 9 7"/><path d="M7 13V9H3"/><path d="M2.5 13.5 7 9"/>'),
  // git 分支（對話標頭的 git 晶片）：兩個提交一條主線，旁邊分出一條
  gitBranch: svg('<circle cx="4.6" cy="3.6" r="1.6"/><circle cx="4.6" cy="12.4" r="1.6"/><circle cx="11.4" cy="5" r="1.6"/><path d="M4.6 5.2v5.6M11.4 6.6c0 2.6-2 3.6-5.3 4.6"/>'),
  folder: svg('<path d="M2 4.6A1.6 1.6 0 0 1 3.6 3h2.6l1.5 1.6h4.7A1.6 1.6 0 0 1 14 6.2v5.2a1.6 1.6 0 0 1-1.6 1.6H3.6A1.6 1.6 0 0 1 2 11.4z"/>'),
  folderOpen: svg('<path d="M2 12V4.6A1.6 1.6 0 0 1 3.6 3h2.6l1.5 1.6h4.1A1.6 1.6 0 0 1 13.4 6.2V7"/><path d="M2.1 12.2 3.7 8a1.5 1.5 0 0 1 1.4-1h8.6a.8.8 0 0 1 .7 1.1l-1.5 3.9a1.6 1.6 0 0 1-1.5 1H2.9a.8.8 0 0 1-.8-.8z"/>'),
  file: svg(FILE),
  fileCode: svg(FILE + '<path d="M6.6 8.3 5.3 9.7l1.3 1.4M9.4 8.3l1.3 1.4-1.3 1.4"/>'),
  fileDoc: svg(FILE + '<path d="M5.6 8.6h4.8M5.6 11h3.2"/>'),
  fileArchive: svg(FILE + '<path d="M7 5.5h1M8 7h1M7 8.5h1"/><rect x="6.6" y="10" width="2.6" height="2.4" rx=".6"/>'),
  image: svg('<rect x="2" y="3" width="12" height="10" rx="1.7"/><circle cx="5.8" cy="6.4" r="1.15"/><path d="m14 10.6-3.1-3.1-6.4 5.5"/>'),
  video: svg('<rect x="1.8" y="3.6" width="9.2" height="8.8" rx="1.7"/><path d="m11 7 3.2-1.9v5.8L11 9"/>'),
  audio: svg('<path d="M6 11.6V4.2l7-1.6v7.6"/><circle cx="4.4" cy="11.6" r="1.6"/><circle cx="11.4" cy="10.2" r="1.6"/>'),
  search: svg('<circle cx="7" cy="7" r="4.4"/><path d="m13.4 13.4-3.3-3.3"/>'),
  paperclip: svg('<path d="m13 7.6-4.9 4.9a3.1 3.1 0 0 1-4.4-4.4l5.2-5.2a2.1 2.1 0 0 1 3 3L6.7 11.1a1 1 0 0 1-1.5-1.5l4.8-4.8"/>'),
  at: svg('<circle cx="8" cy="8" r="2.4"/><path d="M10.4 8v.9a1.8 1.8 0 0 0 3.6 0V8a6 6 0 1 0-2.4 4.8"/>'),
  send: svg('<path d="M8 13V3.6M4.2 7.4 8 3.6l3.8 3.8"/>', 'stroke-width="1.7"'),
  stop: svg('<rect x="4.2" y="4.2" width="7.6" height="7.6" rx="1.4" ' + FILL + '/>'),
  // 輸入區的「繼續」：直槓＋播放三角（同除錯器的「繼續執行」）
  continue: svg('<path d="M3.8 3.6v8.8"/><path d="M6.8 4v8a.6.6 0 0 0 .92.5l5.6-4a.6.6 0 0 0 0-1l-5.6-4a.6.6 0 0 0-.92.5z"/>'),
  mic: svg('<rect x="5.9" y="1.8" width="4.2" height="7.6" rx="2.1"/><path d="M3.6 7.4a4.4 4.4 0 0 0 8.8 0M8 11.8V14M5.8 14h4.4"/>'),
  arrowDown: svg('<path d="M8 3v9.6M4.2 8.8 8 12.6l3.8-3.8"/>'),
  arrowUp: svg('<path d="M8 13V3.4M4.2 7.2 8 3.4l3.8 3.8"/>'),
  terminal: svg('<rect x="1.8" y="2.8" width="12.4" height="10.4" rx="1.8"/><path d="m4.6 6.2 2 1.8-2 1.8M8.4 10.2h3"/>'),
  eye: svg('<path d="M1.6 8S4 3.6 8 3.6 14.4 8 14.4 8 12 12.4 8 12.4 1.6 8 1.6 8z"/><circle cx="8" cy="8" r="1.9"/>'),
  filePlus: svg(FILE + '<path d="M8 7.6v4M6 9.6h4"/>'),
  edit: svg('<path d="M10.7 2.8a1.6 1.6 0 0 1 2.3 2.3l-7.4 7.4-3.1.9.9-3.1z"/><path d="m9.6 3.9 2.3 2.3"/>'),
  globe: svg('<circle cx="8" cy="8" r="6"/><path d="M2 8h12M8 2c1.7 1.7 2.5 3.7 2.5 6S9.7 12.3 8 14C6.3 12.3 5.5 10.3 5.5 8S6.3 3.7 8 2z"/>'),
  bot: svg('<rect x="3" y="5" width="10" height="8" rx="2.2"/><path d="M8 2.6V5M6 8.4v1M10 8.4v1"/>'),
  plug: svg('<path d="M6 2v3M10 2v3M4.6 5h6.8v2.4a3.4 3.4 0 0 1-6.8 0zM8 10.8V14"/>'),
  checklist: svg('<path d="m2.4 4.6 1.2 1.2 2.3-2.4M2.4 10.6l1.2 1.2 2.3-2.4M8.4 4.8H14M8.4 10.8H14"/>'),
  map: svg('<path d="M2 4.1 6 2.6l4 1.5 4-1.5v9.3l-4 1.5-4-1.5-4 1.5z"/><path d="M6 2.6v9.3M10 4.1v9.3"/>'),
  sparkle: svg('<path d="M8 1.8c.45 2.9 1.7 4.6 4.9 5.2-3.2.6-4.45 2.3-4.9 5.2-.45-2.9-1.7-4.6-4.9-5.2C6.3 6.4 7.55 4.7 8 1.8z"/>'),
  check: svg('<path d="m3.4 8.4 3 3 6.2-6.8"/>', 'stroke-width="1.6"'),
  ban: svg('<circle cx="8" cy="8" r="5.6"/><path d="m4.1 4.1 7.8 7.8"/>'),
  refresh: svg('<path d="M13.2 8a5.2 5.2 0 1 1-1.6-3.75"/><path d="M13.3 2.6v2.7h-2.7"/>'),
  grid: svg('<rect x="2.2" y="2.2" width="4.8" height="4.8" rx="1.1"/><rect x="9" y="2.2" width="4.8" height="4.8" rx="1.1"/><rect x="2.2" y="9" width="4.8" height="4.8" rx="1.1"/><rect x="9" y="9" width="4.8" height="4.8" rx="1.1"/>'),
  list: svg('<path d="M5.6 4h8M5.6 8h8M5.6 12h8"/><circle cx="2.7" cy="4" r=".75" ' + FILL + '/><circle cx="2.7" cy="8" r=".75" ' + FILL + '/><circle cx="2.7" cy="12" r=".75" ' + FILL + '/>'),
  up: svg('<path d="M6 3 2.6 6.4 6 9.8"/><path d="M2.6 6.4h7.2a3.8 3.8 0 0 1 3.8 3.8V13"/>'),
  popout: svg('<path d="M9.2 2.4h4.4v4.4M13.6 2.4 7.6 8.4"/><path d="M12 9.4v3a1.6 1.6 0 0 1-1.6 1.6H3.6A1.6 1.6 0 0 1 2 12.4V5.6A1.6 1.6 0 0 1 3.6 4h3"/>'),
  panelRight: svg('<rect x="2" y="2.5" width="12" height="11" rx="2"/><path d="M10 2.5v11"/>'),
  copy: svg('<rect x="5.2" y="5.2" width="8.4" height="8.4" rx="1.6"/><path d="M10.8 5.2V3.8a1.6 1.6 0 0 0-1.6-1.6H3.8a1.6 1.6 0 0 0-1.6 1.6v5.4a1.6 1.6 0 0 0 1.6 1.6h1.4"/>'),
  zoomIn: svg('<circle cx="7" cy="7" r="4.4"/><path d="m13.4 13.4-3.3-3.3M5 7h4M7 5v4"/>'),
  zoomOut: svg('<circle cx="7" cy="7" r="4.4"/><path d="m13.4 13.4-3.3-3.3M5 7h4"/>'),
  fit: svg('<path d="M2.6 6V3.6a1 1 0 0 1 1-1H6M10 2.6h2.4a1 1 0 0 1 1 1V6M13.4 10v2.4a1 1 0 0 1-1 1H10M6 13.4H3.6a1 1 0 0 1-1-1V10"/>'),
  play: svg('<path d="M5 3.3v9.4a.6.6 0 0 0 .9.5l7.4-4.7a.6.6 0 0 0 0-1L5.9 2.8a.6.6 0 0 0-.9.5z" ' + FILL + '/>'),
  // 執行列：暫停（兩條實心直槓）與目標晶片的視窗程式（標題列＋三個視窗按鈕的點）
  pause: svg('<rect x="4" y="3.2" width="2.7" height="9.6" rx=".8" ' + FILL + '/><rect x="9.3" y="3.2" width="2.7" height="9.6" rx=".8" ' + FILL + '/>'),
  appWindow: svg('<rect x="1.8" y="2.6" width="12.4" height="10.8" rx="1.6"/><path d="M1.8 5.6h12.4"/><circle cx="4" cy="4.1" r=".55" ' + FILL + '/><circle cx="5.8" cy="4.1" r=".55" ' + FILL + '/>'),
  warning: svg('<path d="M7.1 2.7a1 1 0 0 1 1.8 0l5.4 9.6a1 1 0 0 1-.9 1.5H2.6a1 1 0 0 1-.9-1.5z"/><path d="M8 6.4v3M8 11.3v.1"/>'),
  info: svg('<circle cx="8" cy="8" r="6"/><path d="M8 7.3V11M8 5v.1"/>'),
  error: svg('<circle cx="8" cy="8" r="6"/><path d="m5.9 5.9 4.2 4.2M10.1 5.9l-4.2 4.2"/>'),
  question: svg('<circle cx="8" cy="8" r="6"/><path d="M6.3 6.3a1.8 1.8 0 0 1 3.5.5c0 1.2-1.8 1.4-1.8 2.6M8 11.3v.1"/>'),
  shield: svg('<path d="M8 1.8 3 3.8v3.9c0 3.1 2.1 5.4 5 6.5 2.9-1.1 5-3.4 5-6.5V3.8z"/>'),
  login: svg('<path d="M9.6 2.4h2.8A1.6 1.6 0 0 1 14 4v8a1.6 1.6 0 0 1-1.6 1.6H9.6M6.4 5l3 3-3 3M9.4 8H2"/>'),
  logout: svg('<path d="M6.4 2.4H3.6A1.6 1.6 0 0 0 2 4v8a1.6 1.6 0 0 0 1.6 1.6h2.8M10.6 5l3 3-3 3M13.6 8H6"/>'),
  palette: svg('<path d="M8 2a6 6 0 1 0 0 12c.9 0 1.3-.6 1.3-1.2 0-.8-.6-1.1-.6-1.8 0-.6.5-1 1.1-1h1.5A2.7 2.7 0 0 0 14 7.3C14 4.3 11.3 2 8 2z"/><circle cx="5" cy="7.2" r=".85" ' + FILL + '/><circle cx="7.6" cy="4.9" r=".85" ' + FILL + '/><circle cx="10.6" cy="5.7" r=".85" ' + FILL + '/>'),
  trash: svg('<path d="M2.6 4h10.8M6.4 4V2.8a.8.8 0 0 1 .8-.8h1.6a.8.8 0 0 1 .8.8V4M4 4l.7 8.6A1.6 1.6 0 0 0 6.3 14h3.4a1.6 1.6 0 0 0 1.6-1.4L12 4"/>'),
  rename: svg('<path d="M2.6 13.6h10.8"/><path d="M9.9 2.8a1.4 1.4 0 0 1 2 2l-5.8 5.8-2.7.7.7-2.7z"/>'),
  // 自動重新命名（標題的右鍵選單）：改名的筆＋左上角實心的星芒
  autoTitle: svg('<path d="M2.6 13.6h10.8"/><path d="M9.9 2.8a1.4 1.4 0 0 1 2 2l-5.8 5.8-2.7.7.7-2.7z"/><path d="M3.6 1.2c.25 1.55.85 2.15 2.4 2.4-1.55.25-2.15.85-2.4 2.4-.25-1.55-.85-2.15-2.4-2.4 1.55-.25 2.15-.85 2.4-2.4z" ' + FILL + '/>'),
  clock: svg('<circle cx="8" cy="8" r="6"/><path d="M8 4.8V8l2.2 1.4"/>'),
  lock: svg('<rect x="3" y="7" width="10" height="7" rx="1.6"/><path d="M5.5 7V5a2.5 2.5 0 0 1 5 0v2"/>'),
  download: svg('<path d="M8 2.4v8M4.6 7.4 8 10.8l3.4-3.4M2.6 13.6h10.8"/>'),
  user: svg('<circle cx="8" cy="5.4" r="2.8"/><path d="M2.8 14a5.2 5.2 0 0 1 10.4 0"/>'),
  filter: svg('<path d="M2.2 3h11.6L9.4 8.4V13l-2.8-1.4V8.4z"/>'),
  link: svg('<path d="M6.8 9.2a2.8 2.8 0 0 0 4 0l2-2a2.8 2.8 0 0 0-4-4l-.6.6"/><path d="M9.2 6.8a2.8 2.8 0 0 0-4 0l-2 2a2.8 2.8 0 0 0 4 4l.6-.6"/>'),
  insert: svg('<path d="M3 4h10M3 8h5M3 12h5"/><path d="M12 8.6v4.8M9.6 11h4.8"/>'),
  keyboard: svg('<rect x="1.6" y="3.6" width="12.8" height="8.8" rx="1.6"/><path d="M4.4 6.4h.1M7 6.4h.1M9.6 6.4h.1M12 6.4h-.1M5 9.6h6"/>'),
  dot: svg('<circle cx="8" cy="8" r="3" ' + FILL + '/>'),
};

/*
 * 代理標誌：各家的官方圖示，照官方配色放在 app 圖示風格的圓角磚上（上半部的光澤、內緣的高光、字形下方的淡影）。
 * 字形與配色的來源（本機安裝的官方套件）：
 *   Claude      Claude Code 擴充套件 resources/claude-logo.svg 的星芒；底色 #D97757、星芒 #FAF9F5 取自同處的 claude-logo.png
 *   Codex       ChatGPT（Codex）擴充套件 webview/assets/homepage-logo-*.js 的雲朵與 >_；漸層取自 codex-app-ga-logo-*.png
 *   Antigravity Antigravity 安裝目錄 out/media/jetski-logo-black.svg 的拱形；底色 #202124、四色漸層取自 out/media/code-icon.svg
 *   Grok        xAI 的 Grok 標誌（Grok Build 沒有附圖示，路徑取自 @lobehub/icons-static-svg 的 grok.svg）；黑底白標
 * 字形與漸層只在 MARK_DEFS 定義一次（第一次用到時放進 body），各標誌以 <use>、url(#pm-…) 引用：
 * 標誌是同一份範本複製出來的，SVG 內各自帶 id 會讓同一頁出現重複的 id。
 */
const MARK_DEFS = '<svg id="pm-defs" class="pmark-defs" aria-hidden="true" focusable="false"><defs>' +
  '<path id="pm-g-claude" d="M4.709 15.955l4.72-2.647.08-.23-.08-.128H9.2l-.79-.048-2.698-.073-2.339-.097-2.266-.122-.571-.121L0 11.784l.055-.352.48-.321.686.06 1.52.103 2.278.158 1.652.097 2.449.255h.389l.055-.157-.134-.098-.103-.097-2.358-1.596-2.552-1.688-1.336-.972-.724-.491-.364-.462-.158-1.008.656-.722.881.06.225.061.893.686 1.908 1.476 2.491 1.833.365.304.145-.103.019-.073-.164-.274-1.355-2.446-1.446-2.49-.644-1.032-.17-.619a2.97 2.97 0 01-.104-.729L6.283.134 6.696 0l.996.134.42.364.62 1.414 1.002 2.229 1.555 3.03.456.898.243.832.091.255h.158V9.01l.128-1.706.237-2.095.23-2.695.08-.76.376-.91.747-.492.584.28.48.685-.067.444-.286 1.851-.559 2.903-.364 1.942h.212l.243-.242.985-1.306 1.652-2.064.73-.82.85-.904.547-.431h1.033l.76 1.129-.34 1.166-1.064 1.347-.881 1.142-1.264 1.7-.79 1.36.073.11.188-.02 2.856-.606 1.543-.28 1.841-.315.833.388.091.395-.328.807-1.969.486-2.309.462-3.439.813-.042.03.049.061 1.549.146.662.036h1.622l3.02.225.79.522.474.638-.079.485-1.215.62-1.64-.389-3.829-.91-1.312-.329h-.182v.11l1.093 1.068 2.006 1.81 2.509 2.33.127.578-.322.455-.34-.049-2.205-1.657-.851-.747-1.926-1.62h-.128v.17l.444.649 2.345 3.521.122 1.08-.17.353-.608.213-.668-.122-1.374-1.925-1.415-2.167-1.143-1.943-.14.08-.674 7.254-.316.37-.729.28-.607-.461-.322-.747.322-1.476.389-1.924.315-1.53.286-1.9.17-.632-.012-.042-.14.018-1.434 1.967-2.18 2.945-1.726 1.845-.414.164-.717-.37.067-.662.401-.589 2.388-3.036 1.44-1.882.93-1.086-.006-.158h-.055L4.132 18.56l-1.13.146-.487-.456.061-.746.231-.243 1.908-1.312-.006.006z"/>' +
  '<path id="pm-g-codex" d="M247.429 247.43C257.73 208.911 292.871 180.543 334.638 180.543C359.555 180.543 382.115 190.64 398.449 206.964C405.906 204.97 413.743 203.905 421.829 203.905C471.681 203.906 512.096 244.32 512.096 294.173C512.096 302.259 511.031 310.096 509.037 317.553C525.361 333.887 535.458 356.446 535.458 381.364C535.458 423.131 507.09 458.271 468.571 468.572C458.271 507.091 423.131 535.459 381.364 535.459C356.446 535.459 333.886 525.362 317.552 509.037C310.095 511.031 302.258 512.097 294.172 512.097C244.319 512.097 203.906 471.682 203.906 421.829C203.906 413.743 204.969 405.905 206.963 398.448C190.639 382.115 180.543 359.555 180.543 334.638C180.543 292.871 208.91 257.73 247.429 247.43Z"/>' +
  '<path id="pm-g-agy" d="M89.754 92.75c4.667 3.5 11.667 1.167 5.25-5.25-19.25-18.667-15.167-70-39.083-70-23.917 0-19.834 51.333-39.084 70-7 7 .584 8.75 5.25 5.25C40.171 80.5 39.004 58.917 55.921 58.917c16.916 0 15.75 21.583 33.833 33.833Z"/>' +
  '<path id="pm-g-grok" fill-rule="evenodd" d="M9.27 15.29l7.978-5.897c.391-.29.95-.177 1.137.272.98 2.369.542 5.215-1.41 7.169-1.951 1.954-4.667 2.382-7.149 1.406l-2.711 1.257c3.889 2.661 8.611 2.003 11.562-.953 2.341-2.344 3.066-5.539 2.388-8.42l.006.007c-.983-4.232.242-5.924 2.75-9.383.06-.082.12-.164.179-.248l-3.301 3.305v-.01L9.267 15.292M7.623 16.723c-2.792-2.67-2.31-6.801.071-9.184 1.761-1.763 4.647-2.483 7.166-1.425l2.705-1.25a7.808 7.808 0 00-1.829-1A8.975 8.975 0 005.984 5.83c-2.533 2.536-3.33 6.436-1.962 9.764 1.022 2.487-.653 4.246-2.34 6.022-.599.63-1.199 1.259-1.682 1.925l7.62-6.815"/>' +
  // 共用的光澤（上亮下透）與內緣（深色磚用白色高光、淺色磚用淡的深色邊）
  '<linearGradient id="pm-gloss" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fff" stop-opacity=".24"/><stop offset=".55" stop-color="#fff" stop-opacity="0"/></linearGradient>' +
  '<linearGradient id="pm-rim" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fff" stop-opacity=".5"/><stop offset="1" stop-color="#fff" stop-opacity=".07"/></linearGradient>' +
  '<linearGradient id="pm-rim-light" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fff" stop-opacity=".9"/><stop offset="1" stop-color="#1B1F5C" stop-opacity=".16"/></linearGradient>' +
  '<linearGradient id="pm-claude-bg" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#E4896B"/><stop offset="1" stop-color="#CB6645"/></linearGradient>' +
  '<linearGradient id="pm-codex-bg" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fff"/><stop offset="1" stop-color="#E7EAFB"/></linearGradient>' +
  '<linearGradient id="pm-codex-fill" gradientUnits="userSpaceOnUse" x1="358" y1="180" x2="358" y2="536"><stop offset="0" stop-color="#B1A7FF"/><stop offset=".5" stop-color="#7A9DFF"/><stop offset="1" stop-color="#3941FF"/></linearGradient>' +
  '<linearGradient id="pm-agy-bg" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#303236"/><stop offset="1" stop-color="#161719"/></linearGradient>' +
  '<radialGradient id="pm-agy-glow"><stop offset="0" stop-color="#3186FF" stop-opacity=".34"/><stop offset="1" stop-color="#3186FF" stop-opacity="0"/></radialGradient>' +
  '<radialGradient id="pm-agy-green" gradientUnits="userSpaceOnUse" cx="35" cy="42" r="24"><stop offset="0" stop-color="#00B95C"/><stop offset=".5" stop-color="#00B95C" stop-opacity=".55"/><stop offset="1" stop-color="#00B95C" stop-opacity="0"/></radialGradient>' +
  '<radialGradient id="pm-agy-yellow" gradientUnits="userSpaceOnUse" cx="47" cy="20" r="20"><stop offset="0" stop-color="#FFE432"/><stop offset=".5" stop-color="#FBBC04" stop-opacity=".7"/><stop offset="1" stop-color="#FBBC04" stop-opacity="0"/></radialGradient>' +
  '<radialGradient id="pm-agy-red" gradientUnits="userSpaceOnUse" cx="70" cy="27" r="22"><stop offset="0" stop-color="#FC413D"/><stop offset=".5" stop-color="#FC413D" stop-opacity=".6"/><stop offset="1" stop-color="#FC413D" stop-opacity="0"/></radialGradient>' +
  '<radialGradient id="pm-agy-violet" gradientUnits="userSpaceOnUse" cx="82" cy="56" r="17"><stop offset="0" stop-color="#9D6BEA" stop-opacity=".7"/><stop offset="1" stop-color="#9D6BEA" stop-opacity="0"/></radialGradient>' +
  '<linearGradient id="pm-grok-bg" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#2A2A2E"/><stop offset="1" stop-color="#030304"/></linearGradient>' +
  '</defs></svg>';

const tile = (bg, body, rim = 'pm-rim') => '<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">' +
  `<rect width="24" height="24" rx="6.5" fill="url(#${bg})"/>${body}` +
  '<rect width="24" height="24" rx="6.5" fill="url(#pm-gloss)"/>' +
  `<rect x=".3" y=".3" width="23.4" height="23.4" rx="6.2" fill="none" stroke="url(#${rim})" stroke-width=".6"/></svg>`;

const MARKS = {
  Claude: tile('pm-claude-bg',
    '<use href="#pm-g-claude" fill="#8A341A" fill-opacity=".32" transform="translate(4.56 4.98) scale(.62)"/>' +
    '<use href="#pm-g-claude" fill="#FAF9F5" transform="translate(4.56 4.56) scale(.62)"/>'),
  Codex: tile('pm-codex-bg',
    '<g transform="translate(-4.04 -4.04) scale(.0448)">' +
    '<use href="#pm-g-codex" fill="#3941FF" fill-opacity=".2" transform="translate(0 12)"/>' +
    '<use href="#pm-g-codex" fill="url(#pm-codex-fill)"/>' +
    '<path d="M436.706 408.738H370.021M276.533 309.154L303.468 357.831C304.433 359.575 304.412 361.698 303.414 363.423L276.533 409.854" fill="none" stroke="#fff" stroke-width="30" stroke-linecap="round" stroke-linejoin="round"/></g>',
    'pm-rim-light'),
  Antigravity: tile('pm-agy-bg',
    '<ellipse cx="12" cy="18.5" rx="10" ry="6" fill="url(#pm-agy-glow)"/>' +
    '<g transform="translate(1.68 1.62) scale(.186)">' +
    '<use href="#pm-g-agy" fill="#3186FF"/><use href="#pm-g-agy" fill="url(#pm-agy-green)"/><use href="#pm-g-agy" fill="url(#pm-agy-yellow)"/>' +
    '<use href="#pm-g-agy" fill="url(#pm-agy-red)"/><use href="#pm-g-agy" fill="url(#pm-agy-violet)"/></g>'),
  Grok: tile('pm-grok-bg', '<use href="#pm-g-grok" fill="#fff" transform="translate(4.32 4.32) scale(.64)"/>'),
};

/** 品牌「萬象星群」（2026-10-31 使用者選定）：兩圈光點排成缺口朝右的 C（Myriagent＝萬個代理同時工作），中心是 AI 星芒。
 *  BRAND_SVG 是暗的那一層光點；會動的部分各是一層 HTML（brandMark 加上；SVG 元素的 transform 不交給合成器）：
 *  亮的那層光點由一道轉動的錐形遮罩照出、繞著 C 走（.brand-sweep 轉、裡面的 .brand-lit 反向轉，光點留在原地），星芒呼吸旋轉。
 *  平常靜止，有對話執行中才動（fx.css 的 .app.acct-running）；登入頁 auth.html 是同一個圖示的大版本、app.ico 由 tools/make_icon.py 畫 */
const BRAND_DOTS = (() => {
  // [半徑, 間隔數, 點的半徑, 顏色]：從 C 的下端（40°）順時針排到上端（320°），缺口朝右
  let s = '';
  for (const [r, n, size, fill] of [[10, 11, 1.05, '#2F6FEB'], [7, 8, 0.9, '#1FB3EC']]) {
    s += `<g fill="${fill}">`;
    for (let i = 0; i <= n; i++) {
      const a = ((40 + (280 * i) / n) * Math.PI) / 180;
      s += `<circle cx="${(12 + r * Math.cos(a)).toFixed(2)}" cy="${(12 + r * Math.sin(a)).toFixed(2)}" r="${size}"/>`;
    }
    s += '</g>';
  }
  return s;
})();
const brandSvg = (inner) => '<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">' + inner + '</svg>';
export const BRAND_SVG = brandSvg(`<g opacity=".42">${BRAND_DOTS}</g>`);
const BRAND_LIT_SVG = brandSvg(BRAND_DOTS);
const BRAND_SPARK_SVG = brandSvg('<defs><linearGradient id="brand-spark-g" x1="8" y1="8" x2="16" y2="16" gradientUnits="userSpaceOnUse">' +
  '<stop offset="0" stop-color="#E6FDFF"/><stop offset=".45" stop-color="#38BDF8"/><stop offset="1" stop-color="#2563EB"/></linearGradient></defs>' +
  '<path fill="url(#brand-spark-g)" d="M12 7.6C12.4 10.7 13.3 11.6 16.4 12C13.3 12.4 12.4 13.3 12 16.4C11.6 13.3 10.7 12.4 7.6 12C10.7 11.6 11.6 10.7 12 7.6Z"/>');

const cache = new Map();

function fromTemplate(key, markup) {
  let t = cache.get(key);
  if (!t) {
    t = document.createElement('template');
    t.innerHTML = markup;
    cache.set(key, t);
  }
  return t.content.cloneNode(true);
}

/** 產生 <span class="ic">，內含圖示 SVG */
export function icon(name, cls) {
  const span = document.createElement('span');
  span.className = cls ? 'ic ' + cls : 'ic';
  span.append(fromTemplate('i:' + name, ICONS[name] || ICONS.dot));
  return span;
}

/** 代理標誌引用的字形與漸層：每份文件放一次 */
function ensureMarkDefs() {
  if (!document.getElementById('pm-defs')) document.body.append(fromTemplate('m:defs', MARK_DEFS));
}

/** 代理標誌 */
export function providerMark(provider, cls) {
  ensureMarkDefs();
  const span = document.createElement('span');
  span.className = cls ? 'pmark ' + cls : 'pmark';
  span.append(fromTemplate('m:' + provider, MARKS[provider] || MARKS.Claude));
  return span;
}

export function brandMark(cls) {
  const span = document.createElement('span');
  span.className = cls || 'brand-mark';
  span.append(fromTemplate('brand', BRAND_SVG));
  const layer = (c, child) => {
    const el = document.createElement('span');
    el.className = c;
    el.append(child);
    return el;
  };
  span.append(
    layer('brand-sweep', layer('brand-lit', fromTemplate('brand:lit', BRAND_LIT_SVG))),
    layer('brand-glow', document.createDocumentFragment()),
    layer('brand-spark', fromTemplate('brand:spark', BRAND_SPARK_SVG)),
  );
  return span;
}

/** 依工具種類決定圖示 */
export const TOOL_ICON = {
  bash: 'terminal', read: 'eye', write: 'filePlus', edit: 'edit', search: 'search', web: 'globe',
  agent: 'bot', mcp: 'plug', todo: 'checklist', image: 'image', plan: 'map', other: 'sparkle',
};

/** 依檔案種類決定圖示 */
export const FILE_ICON = {
  dir: 'folder', image: 'image', video: 'video', audio: 'audio', code: 'fileCode', doc: 'fileDoc', archive: 'fileArchive', file: 'file',
};
