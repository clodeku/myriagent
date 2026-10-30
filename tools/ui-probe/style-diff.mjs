// 比對兩份計算樣式快照（perf-probe.mjs 的 MYRIAGENT_PERF_STYLES）：改法不得改變呈現結果時，證明改前改後每個元素的計算樣式與動畫都一樣。
// 用法：node style-diff.mjs <改前.json> <改後.json> [--again-a <改前再拍一次.json>] [--again-b <改後再拍一次.json>]
//   --again-a／--again-b：同一版拍兩次之間就會不同的元素與動畫（隨時間轉的指針、剛好在進行或剛結束的轉場）當成雜訊、不算差異；
//   兩版各拍兩次都給，才涵蓋兩邊各自的雜訊。雜訊元素上真的差異也會被略過：雜訊數字一併印出，數量要和同一版自己比的相當
// 沒有差異時結束碼 0，有差異時列出各狀態的元素路徑並以 1 結束
import fs from 'node:fs';

const args = process.argv.slice(2);
const again = [];
const files = [];
for (let i = 0; i < args.length; i++) {
  if (args[i] === '--again-a') again.push([0, args[++i]]);
  else if (args[i] === '--again-b') again.push([1, args[++i]]);
  else files.push(args[i]);
}
if (files.length !== 2) {
  console.error('用法：node style-diff.mjs <改前.json> <改後.json> [--again-a <改前再拍一次.json>] [--again-b <改後再拍一次.json>]');
  process.exit(2);
}
const read = (f) => JSON.parse(fs.readFileSync(f, 'utf8'));
const [a, b] = files.map(read);

/**
 * 兩份快照在某個狀態下不同的元素路徑與動畫（對稱差）。轉場（trans:）不比：資料改變時才有，拍的當下還在不在進行只看時間
 * （電腦狀態面板每秒更新圓環的 stroke-dashoffset）；轉場的設定（transition-*）在計算樣式裡照樣比
 */
function differences(x, y, state) {
  const sx = x[state], sy = y[state];
  if (!sx || !sy) return { styles: new Set(['（狀態不存在）']), anims: new Set() };
  const styles = new Set();
  for (const k of new Set([...Object.keys(sx.styles), ...Object.keys(sy.styles)])) if (sx.styles[k] !== sy.styles[k]) styles.add(k);
  const animsOf = (s) => new Set(s.anims.filter((v) => !v.includes(' trans:')));
  const ax = animsOf(sx), ay = animsOf(sy);
  const anims = new Set([...ax].filter((v) => !ay.has(v)).concat([...ay].filter((v) => !ax.has(v))));
  return { styles, anims };
}

// 雜訊：同一版與它再拍一次之間就不同的
const noise = {};
for (const [side, f] of again) {
  const base = side === 0 ? a : b;
  const n = read(f);
  for (const state of Object.keys(base)) {
    const d = differences(base, n, state);
    noise[state] = noise[state] || { styles: new Set(), anims: new Set() };
    for (const k of d.styles) noise[state].styles.add(k);
    for (const k of d.anims) noise[state].anims.add(k);
  }
}

let bad = 0;
for (const state of new Set([...Object.keys(a), ...Object.keys(b)])) {
  const d = differences(a, b, state);
  const nz = noise[state] || { styles: new Set(), anims: new Set() };
  const styles = [...d.styles].filter((k) => !nz.styles.has(k));
  const anims = [...d.anims].filter((k) => !nz.anims.has(k));
  const total = a[state] ? Object.keys(a[state].styles).length : 0;
  console.log(`${state.padEnd(28)} 元素 ${String(total).padStart(5)}  樣式不同 ${styles.length}（雜訊 ${nz.styles.size}）  動畫不同 ${anims.length}（雜訊 ${nz.anims.size}）`);
  for (const k of styles.slice(0, 10)) console.log('    樣式 ' + k);
  for (const k of anims.slice(0, 10)) console.log('    動畫 ' + k);
  bad += styles.length + anims.length;
}
console.log(bad ? `有 ${bad} 處不同` : '計算樣式與動畫完全相同');
process.exit(bad ? 1 : 0);
