// 側欄帳戶卡的 token 計數：純規則（沒有 import，tools/ui-probe/rules.test.mjs 直接測）。
// 數字一律是宿主送的真實計數（tokens 訊息，docs/web-protocol.md 的 Tokens）；動畫只在「舊值 → 新值」之間把變動的位數先顯示成方塊亂碼、
// 再由左到右逐位鎖定成新值，畫面上不會出現推估或內插出來的數字（亂碼不用數字，才不會被看成某個值）。

/** 亂碼用的字：方塊與幾何符號（不含數字） */
export const GLYPHS = '▖▗▘▙▚▛▜▝▞▟▀▄▌▐■□▪▫';
/** 駭客任務風格的亂碼（輸入框下方綠色、側欄帳戶色的 token 計數器用：半形片假名與數字，tokenfx 的 glyphs 選項） */
export const MATRIX_GLYPHS = 'ｦｧｨｩｪｫｬｭｮｯｰｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾅﾆﾇﾈﾉﾊﾋﾌﾍﾎﾏﾐﾑﾒﾓﾔﾕﾖﾗﾘﾙﾚﾛﾜﾝ0123456789';

/** 解碼的時間（毫秒）：全部先亂跳 BASE，接著每 STAGGER 鎖定一位 */
export const DECODE_BASE_MS = 240;
export const DECODE_STAGGER_MS = 42;

const count = (x) => (Number.isFinite(x) && x > 0 ? Math.floor(x) : 0);

/** 一份計數（{ input, cached, output }）的總數＝輸入（含快取）＋輸出；不是物件回 0 */
export function tokenTotal(c) {
  return c && typeof c === 'object' ? count(c.input) + count(c.output) : 0;
}

/** 一份計數拆成帳戶卡上的 in（輸入，含快取）與 out（輸出）；不是物件、不是正數當 0 */
export function tokenSplit(c) {
  return c && typeof c === 'object' ? { input: count(c.input), output: count(c.output) } : { input: 0, output: 0 };
}

/** 帳戶的計數（tokens.accounts[id]）：{ today, week, month }；沒有（舊宿主、新帳戶、瀏覽器版沒有電腦回報過的帳戶）回 null */
export function tokensOf(tokens, accountId) {
  const list = tokens && typeof tokens === 'object' ? tokens.accounts : null;
  const t = list && typeof list === 'object' && Object.prototype.hasOwnProperty.call(list, accountId) ? list[accountId] : null;
  return t && typeof t === 'object' ? t : null;
}

/**
 * 帳戶卡要不要顯示 token 計數（取代狀態那一行的方案名稱）：宿主有送 tokens、這台有這個帳戶而且已登入。
 * 瀏覽器版（scope 是 'all'，各電腦的合計）只列有電腦回報過的帳戶：沒列的照舊顯示方案名稱（例如用它的電腦還是舊版），不顯示 0。
 * 沒登入、出錯時那一行照舊顯示狀態（比 token 數重要）
 */
export function showTokens(account, tokens) {
  if (!account || account.remoteOnly === true || account.status !== 'ready' || !tokens || typeof tokens !== 'object') return false;
  return tokens.scope !== 'all' || tokensOf(tokens, account.id) !== null;
}

/** 千分位：1284331 → "1,284,331" */
export function fmtTokens(n) {
  return String(count(n)).replace(/\B(?=(\d{3})+(?!\d))/g, ',');
}

const UNITS = ['K', 'M', 'B', 'T'];

/**
 * 帳戶卡上的精簡格式（in、out 兩個數字並排，寬度有限）：1 萬以下照舊千分位（"9,999"），以上取 4 位有效數字加單位
 * （"35.07K"、"1.264M"、"366.0M"），最多 6 個字。一律無條件捨去，不進位成比真實值大的數字；精確值在滑過的說明
 */
export function fmtShort(n) {
  const v = count(n);
  if (v < 10_000) return fmtTokens(v);
  let u = 0;
  let base = 1_000;
  while (u < UNITS.length - 1 && v >= base * 1_000) { u++; base *= 1_000; }
  const decimals = Math.max(0, 4 - String(Math.floor(v / base)).length);
  // base 是 1000 的次方、decimals ≤ 3，除數一定是整數，整數運算不會有浮點誤差
  const digits = String(Math.floor(v / (base / 10 ** decimals)));
  return (decimals ? digits.slice(0, -decimals) + '.' + digits.slice(-decimals) : digits) + UNITS[u];
}

/**
 * 從哪一格開始解碼：兩個字串長度不同（位數變了、千分位多一個）就從頭；相同時從第一個不同的字開始。
 * 一樣的字串回 -1（不用動）
 */
export function decodeStart(oldText, newText) {
  if (oldText === newText) return -1;
  if (typeof oldText !== 'string' || oldText.length !== newText.length) return 0;
  for (let i = 0; i < newText.length; i++) if (oldText[i] !== newText[i]) return i;
  return -1;
}

/** 第 i 格（i ≥ start）鎖定成新值的時間 */
export function lockAt(i, start) {
  return DECODE_BASE_MS + (i - start) * DECODE_STAGGER_MS;
}

/** 整段解碼要多久（start 之後最後一格的鎖定時間）；不用動是 0 */
export function decodeDuration(text, start) {
  return start < 0 ? 0 : lockAt(text.length - 1, start);
}

/**
 * 解碼到 elapsed 毫秒時每一格顯示什麼：start 之前與已鎖定的是新值的字（state 'lock'＝這一格剛鎖定，畫面閃一下），
 * 還沒鎖定的數字顯示亂碼（state 'scramble'）；數字以外的字（千分位逗號、小數點、單位 K／M／B）一直是新值的字。rand 回 [0,1)（測試可固定）
 */
export function decodeCells(text, start, elapsed, rand = Math.random, glyphs = GLYPHS) {
  const out = [];
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (start < 0 || i < start || ch < '0' || ch > '9') { out.push({ ch, state: 'done' }); continue; }
    const at = lockAt(i, start);
    if (elapsed >= at) out.push({ ch, state: elapsed - at < 260 ? 'lock' : 'done' });
    else out.push({ ch: glyphs[Math.floor(rand() * glyphs.length) % glyphs.length], state: 'scramble' });
  }
  return out;
}

/** 近 N 天那一行：in 與 out 的精確值 */
const spanLine = (label, c) => {
  const s = tokenSplit(c);
  return label + '：in ' + fmtTokens(s.input) + '・out ' + fmtTokens(s.output);
};

/**
 * 滑過的說明：今天的總數、in（其中快取讀取）與 out 的精確值，近 7 天、近 30 天（2026-10-23 使用者要求：卡片與說明都不寫「今日」兩字）；說明算的是哪些對話
 * （scope 'all'＝瀏覽器版的各電腦合計；〔同步〕桌面的帳戶有 here＝已加上其他電腦的、here 是這台今天的；其他＝這台電腦）。
 * 用量晚到的代理（provider）多一行說明：Grok 整輪只在結束時回報，數字會在那時一次跳上去（Claude 的子代理 2026-10-25 起從逐字稿即時記，不再寫）
 */
export function tokensTitle(t, scope, provider) {
  const today = (t && t.today) || {};
  const s = tokenSplit(today);
  const here = t && t.here && typeof t.here === 'object' ? tokenSplit(t.here) : null;
  const lines = [
    'token 合計：' + fmtTokens(s.input + s.output),
    'in（輸入）：' + fmtTokens(s.input) + '（其中快取讀取 ' + fmtTokens(today.cached) + '）',
    'out（輸出）：' + fmtTokens(s.output),
  ];
  if (here) lines.push('其中這台電腦：in ' + fmtTokens(here.input) + '・out ' + fmtTokens(here.output));
  lines.push(
    spanLine('近 7 天', t && t.week),
    spanLine('近 30 天', t && t.month),
    scope === 'all' || here
      ? '所有電腦上 Clode Myriagent 的對話合計（CLI 回報的實際用量；舊版的電腦不回報）'
      : '只算這台電腦上 Clode Myriagent 的對話（CLI 回報的實際用量）',
  );
  if (provider === 'Grok') lines.push('Grok 在一輪結束時才回報這一輪的用量');
  return lines.join('\n');
}
