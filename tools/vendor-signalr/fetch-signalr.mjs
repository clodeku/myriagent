// SignalR JS 用戶端（npm @microsoft/signalr）的可重現取檔與檢查（WP3.5 步驟 4；說明見同資料夾的 README.md）。
// 只用 Node 標準庫（https、zlib、crypto、自寫的 ustar 解析），不經 npm、不留 node_modules。
//
//   node tools/vendor-signalr/fetch-signalr.mjs [--check]   檢查 repo 裡的檔案（預設；不連網）
//   node tools/vendor-signalr/fetch-signalr.mjs --fetch      下載、驗證雜湊後寫進 src/MyriagentHub/wwwroot/lib/（檔案已存在就拒絕）
//   node tools/vendor-signalr/fetch-signalr.mjs --fetch --force   覆寫已存在的檔案
//
// 結束碼：0＝通過；1＝檢查或驗證失敗；2＝參數不對。
import https from 'node:https';
import zlib from 'node:zlib';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

/** 固定的版本與雜湊（2026-10-16 從 npm registry 與 GitHub 實查；換版程序見 README.md） */
export const PIN = Object.freeze({
  version: '10.0.11',
  tarballUrl: 'https://registry.npmjs.org/@microsoft/signalr/-/signalr-10.0.11.tgz',
  integrity: 'sha512-FulOJ2EEtKvLQcswe/U7v8pzyXyk7Jua5xgWJPPwyQU/2Z9ORvKcVjyO75VvLsem+CJ1ORRexJ+7Bz1FCei2aw==',
  libraryEntry: 'package/dist/browser/signalr.min.js',
  libraryFile: 'signalr-10.0.11.min.js',
  librarySha256: '97e9b97e642a72e5a470917147a2bf79f86cad829a5c6786adb10614d248bb95',
  librarySize: 47668,
  licenseUrl: 'https://raw.githubusercontent.com/dotnet/aspnetcore/v10.0.11/LICENSE.txt',
  licenseSha256: 'cfc21f5e8bd655ae997eec916138b707b1d290b83272c02a95c9f821b8c87310',
  licenseSize: 1116,
  noticesEntry: 'package/src/third-party-notices.txt',
  noticesSha256: '960aca06f3691a21af07472d0d56cdb6a420cf4afa4f5fe283bd8482db2d42e0',
  noticesSize: 1595,
  licenseFile: 'signalr-10.0.11.LICENSE.txt',
});

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '..', '..');
export const libDir = path.join(repo, 'src', 'MyriagentHub', 'wwwroot', 'lib');

const LICENSE_MARK = '===== LICENSE.txt =====\n';
const NOTICES_MARK = '===== third-party-notices.txt =====\n';

const sha256 = (buf) => crypto.createHash('sha256').update(buf).digest('hex');
const sriSha512 = (buf) => 'sha512-' + crypto.createHash('sha512').update(buf).digest('base64');
const toLf = (text) => text.replace(/\r\n?/g, '\n');

/** 授權檔的檔頭：兩段的來源網址與原檔的 SHA-256（--check 以此重組比對） */
export function licenseHeader() {
  return [
    `SignalR JavaScript 用戶端（npm @microsoft/signalr ${PIN.version}，MIT 授權）的授權全文與第三方聲明。`,
    '這個檔由 tools/vendor-signalr/fetch-signalr.mjs --fetch 產生，不要手改；--check 會比對下面兩段的來源雜湊。',
    '',
    `[LICENSE.txt] ${PIN.licenseUrl}`,
    `sha256 ${PIN.licenseSha256}`,
    '',
    `[third-party-notices.txt] ${PIN.tarballUrl} 裡的 ${PIN.noticesEntry}（換行正規化成 LF 之前）`,
    `sha256 ${PIN.noticesSha256}`,
    '',
    '',
  ].join('\n');
}

/** 授權檔的內容：檔頭＋LICENSE.txt（原樣，必要時補結尾換行）＋third-party-notices（換行正規化成 LF） */
export function composeLicense(licenseText, noticesText) {
  const license = licenseText.endsWith('\n') ? licenseText : licenseText + '\n';
  return licenseHeader() + LICENSE_MARK + license + NOTICES_MARK + toLf(noticesText);
}

/** 從授權檔拆出兩段；格式不對回 null */
export function splitLicense(text) {
  const header = licenseHeader();
  if (!text.startsWith(header + LICENSE_MARK)) return null;
  const rest = text.slice(header.length + LICENSE_MARK.length);
  const at = rest.indexOf('\n' + NOTICES_MARK);
  if (at < 0 || rest.indexOf('\n' + NOTICES_MARK, at + 1) >= 0) return null;
  return { license: rest.slice(0, at + 1), notices: rest.slice(at + 1 + NOTICES_MARK.length) };
}

// ───── ustar ─────

function tarString(buf, start, length) {
  const slice = buf.subarray(start, start + length);
  const nul = slice.indexOf(0);
  return slice.subarray(0, nul < 0 ? slice.length : nul).toString('utf8');
}

function tarOctal(buf, start, length) {
  const text = tarString(buf, start, length).trim();
  if (!/^[0-7]*$/.test(text)) throw new Error(`tar 標頭的數字欄位不是八進位：${JSON.stringify(text)}`);
  return text ? parseInt(text, 8) : 0;
}

/** 解析 ustar（含 pax 的 path 覆寫）：回傳 Map(路徑 → Buffer)，只收一般檔案 */
export function parseTar(buf) {
  const files = new Map();
  let offset = 0;
  let paxPath = null;
  while (offset + 512 <= buf.length) {
    const header = buf.subarray(offset, offset + 512);
    if (header.every((b) => b === 0)) break;
    let sum = 0;
    for (let i = 0; i < 512; i++) sum += i >= 148 && i < 156 ? 32 : header[i];
    if (sum !== tarOctal(header, 148, 8)) throw new Error(`tar 標頭的檢查碼不對（位移 ${offset}）`);
    const size = tarOctal(header, 124, 12);
    const type = String.fromCharCode(header[156] || 48);
    const prefix = tarString(header, 345, 155);
    const name = tarString(header, 0, 100);
    const body = buf.subarray(offset + 512, offset + 512 + size);
    if (body.length !== size) throw new Error('tar 被截斷');
    if (type === 'x') {
      // pax 延伸標頭：「長度 key=value\n」逐筆，只認 path
      const text = body.toString('utf8');
      const m = /(?:^|\n)\d+ path=([^\n]*)\n/.exec(text);
      paxPath = m ? m[1] : null;
    } else if (type === '0' || type === '\0') {
      const full = paxPath || (prefix ? prefix + '/' + name : name);
      files.set(full, Buffer.from(body));
      paxPath = null;
    } else {
      paxPath = null;
    }
    offset += 512 + Math.ceil(size / 512) * 512;
  }
  return files;
}

// ───── 下載 ─────

function download(url, redirects = 3) {
  return new Promise((resolve, reject) => {
    const req = https.get(url, { headers: { 'User-Agent': 'myriagent-vendor-signalr' }, timeout: 30000 }, (res) => {
      if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location && redirects > 0) {
        res.resume();
        resolve(download(new URL(res.headers.location, url).toString(), redirects - 1));
        return;
      }
      if (res.statusCode !== 200) {
        res.resume();
        reject(new Error(`${url} 回 HTTP ${res.statusCode}`));
        return;
      }
      const chunks = [];
      res.on('data', (c) => chunks.push(c));
      res.on('end', () => resolve(Buffer.concat(chunks)));
      res.on('error', reject);
    });
    req.on('timeout', () => req.destroy(new Error(`${url} 逾時`)));
    req.on('error', reject);
  });
}

function expect(ok, message, problems) {
  if (!ok) problems.push(message);
  return ok;
}

// ───── 檢查 repo 裡的檔案（不連網）─────

export function check(dir = libDir) {
  const problems = [];
  if (!fs.existsSync(dir)) return [`${dir} 不存在`];
  const names = fs.readdirSync(dir).sort();
  const libraries = names.filter((n) => /^signalr-.*\.min\.js$/.test(n));
  expect(libraries.length === 1, `lib/ 要正好一支 signalr-*.min.js，實際 ${libraries.length} 支：${libraries.join('、')}`, problems);
  for (const n of names) {
    if (n.endsWith('.map')) problems.push(`lib/ 不能有 source map：${n}`);
    else if (/^signalr-.*\.js$/.test(n) && !n.endsWith('.min.js')) problems.push(`lib/ 不能有未壓縮的程式庫：${n}`);
    else if (n !== PIN.libraryFile && n !== PIN.licenseFile) problems.push(`lib/ 只放程式庫與授權檔，多了：${n}`);
  }

  const libPath = path.join(dir, PIN.libraryFile);
  if (expect(fs.existsSync(libPath), `缺少 ${PIN.libraryFile}`, problems)) {
    const lib = fs.readFileSync(libPath);
    expect(lib.length === PIN.librarySize, `${PIN.libraryFile} 是 ${lib.length} 位元組，應該是 ${PIN.librarySize}`, problems);
    expect(sha256(lib) === PIN.librarySha256, `${PIN.libraryFile} 的 SHA-256 是 ${sha256(lib)}，應該是 ${PIN.librarySha256}`, problems);
  }

  const licPath = path.join(dir, PIN.licenseFile);
  if (expect(fs.existsSync(licPath), `缺少 ${PIN.licenseFile}`, problems)) {
    const bytes = fs.readFileSync(licPath);
    const text = bytes.toString('utf8');
    expect(!text.includes('\r'), `${PIN.licenseFile} 要是 LF 換行`, problems);
    const parts = splitLicense(text);
    if (expect(parts !== null, `${PIN.licenseFile} 的檔頭或分段標記和 fetch-signalr.mjs 產生的不同`, problems)) {
      const license = Buffer.from(parts.license, 'utf8');
      expect(sha256(license) === PIN.licenseSha256, `LICENSE.txt 那一段的 SHA-256 是 ${sha256(license)}，應該是 ${PIN.licenseSha256}`, problems);
      // third-party-notices 原檔是 CRLF：換回 CRLF 才和原檔的雜湊比得起來
      const notices = Buffer.from(parts.notices.replace(/\n/g, '\r\n'), 'utf8');
      expect(sha256(notices) === PIN.noticesSha256, `third-party-notices 那一段換回 CRLF 後的 SHA-256 是 ${sha256(notices)}，應該是 ${PIN.noticesSha256}`, problems);
      expect(composeLicense(parts.license, parts.notices) === text, `${PIN.licenseFile} 重組後和原檔不同`, problems);
    }
  }
  return problems;
}

// ───── 下載並寫入 ─────

export async function fetchAndWrite({ force = false, dir = libDir, log = console.log } = {}) {
  const libPath = path.join(dir, PIN.libraryFile);
  const licPath = path.join(dir, PIN.licenseFile);
  if (!force) {
    for (const p of [libPath, licPath]) {
      if (fs.existsSync(p)) throw new Error(`${p} 已存在；要覆寫請加 --force`);
    }
  }

  log(`下載 ${PIN.tarballUrl}`);
  const tgz = await download(PIN.tarballUrl);
  if (sriSha512(tgz) !== PIN.integrity) throw new Error(`tarball 的 integrity 是 ${sriSha512(tgz)}，應該是 ${PIN.integrity}`);
  const files = parseTar(zlib.gunzipSync(tgz));

  const lib = files.get(PIN.libraryEntry);
  if (!lib) throw new Error(`tarball 裡沒有 ${PIN.libraryEntry}`);
  if (lib.length !== PIN.librarySize || sha256(lib) !== PIN.librarySha256) {
    throw new Error(`${PIN.libraryEntry} 是 ${lib.length} 位元組、SHA-256 ${sha256(lib)}，和固定值不同`);
  }
  const notices = files.get(PIN.noticesEntry);
  if (!notices) throw new Error(`tarball 裡沒有 ${PIN.noticesEntry}`);
  if (notices.length !== PIN.noticesSize || sha256(notices) !== PIN.noticesSha256) {
    throw new Error(`${PIN.noticesEntry} 是 ${notices.length} 位元組、SHA-256 ${sha256(notices)}，和固定值不同`);
  }

  log(`下載 ${PIN.licenseUrl}`);
  const license = await download(PIN.licenseUrl);
  if (license.length !== PIN.licenseSize || sha256(license) !== PIN.licenseSha256) {
    throw new Error(`LICENSE.txt 是 ${license.length} 位元組、SHA-256 ${sha256(license)}，和固定值不同`);
  }

  const noticesText = notices.toString('utf8');
  if (toLf(noticesText).replace(/\n/g, '\r\n') !== noticesText) {
    throw new Error('third-party-notices 的換行不是一律 CRLF：正規化成 LF 之後換不回原檔，--check 會比不起來');
  }

  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(libPath, lib);
  fs.writeFileSync(licPath, Buffer.from(composeLicense(license.toString('utf8'), noticesText), 'utf8'));
  log(`已寫入 ${path.relative(repo, libPath)}（${lib.length} 位元組）與 ${path.relative(repo, licPath)}`);
}

// ───── 命令列 ─────

async function main(argv) {
  const args = new Set(argv);
  const known = new Set(['--check', '--fetch', '--force']);
  for (const a of args) {
    if (!known.has(a)) {
      console.error(`不認得的參數：${a}（用法：--check｜--fetch [--force]）`);
      return 2;
    }
  }
  if (args.has('--check') && args.has('--fetch')) {
    console.error('--check 與 --fetch 只能擇一（--fetch 寫完會自己檢查）');
    return 2;
  }
  if (args.has('--force') && !args.has('--fetch')) {
    console.error('--force 只能和 --fetch 一起用');
    return 2;
  }
  try {
    if (args.has('--fetch')) await fetchAndWrite({ force: args.has('--force') });
  } catch (e) {
    console.error(`[失敗] ${e?.message ?? e}`);
    return 1;
  }
  const problems = check();
  if (problems.length) {
    for (const p of problems) console.error(`[失敗] ${p}`);
    return 1;
  }
  console.log(`[通過] ${path.relative(repo, libDir)}：${PIN.libraryFile}（${PIN.librarySize} 位元組、SHA-256 ${PIN.librarySha256}）與 ${PIN.licenseFile} 都和固定值一致`);
  return 0;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = await main(process.argv.slice(2));
}
