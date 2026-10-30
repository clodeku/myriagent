// 全流程 FF6 的純函式（WP4.9 步 16；flows/ff6.mjs 用，test/media-rules.test.mjs 測）：媒體夾具（確定的 PNG 與「看起來像 MP4」的位元組）、
// 確定的假資料、HTTP 的 Content-Range、回應的狀態碼（≥400 的回應與主控台的「status of N」）、blob 配額的上傳計畫。
// 不碰檔案、網路、程序與時鐘（只用 zlib 壓縮與 CRC-32）。
import zlib from 'node:zlib';

export const PNG_SIGNATURE = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

/** 媒體回應的 CSP（MyriagentHub 的 MediaHeaderRules.MediaCsp；逐字比對，改了那邊這裡也要改，同 remote-control.mjs） */
export const MEDIA_CSP = "default-src 'none'; img-src 'self'; media-src 'self'; style-src 'unsafe-inline'; sandbox";

/** 確定的假資料（xorshift32；seed 為 0 時換成固定的非零值）：同樣的 seed、length 一定是同樣的位元組。頁面裡產生上傳內容的版本見 ff6.mjs，演算法相同 */
export function seededBytes(seed, length) {
  if (!Number.isInteger(length) || length < 0) throw new Error('長度要是非負整數');
  let x = (seed >>> 0) || 0x9e3779b9;
  const out = Buffer.alloc(length);
  for (let i = 0; i < length; i++) {
    x ^= x << 13;
    x >>>= 0;
    x ^= x >>> 17;
    x ^= x << 5;
    x >>>= 0;
    out[i] = x & 0xff;
  }
  return out;
}

function pngChunk(type, data) {
  const length = Buffer.alloc(4);
  length.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(zlib.crc32(body) >>> 0);
  return Buffer.concat([length, body, crc]);
}

/**
 * 確定的 RGB PNG（8 位元、不交錯、每列的濾波是 0）：紅色隨 x、綠色隨 y 漸層，藍色是 8×8 方格的 (x/8 ^ y/8 ^ seed) 低 8 位元（壓得小）。
 * 桌面以它做 320 px 的縮圖（E8），伺服器的 /m/ 回原檔、/t/ 回縮圖
 */
export function pngImage(width, height, seed = 1) {
  if (![width, height].every((v) => Number.isInteger(v) && v >= 1 && v <= 4096)) throw new Error('寬高要是 1～4096 的整數');
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; // 位元深度
  ihdr[9] = 2; // RGB
  const row = 1 + width * 3;
  const raw = Buffer.alloc(row * height);
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const o = y * row + 1 + x * 3;
      raw[o] = Math.floor((x * 255) / Math.max(1, width - 1));
      raw[o + 1] = Math.floor((y * 255) / Math.max(1, height - 1));
      raw[o + 2] = ((x >> 3) ^ (y >> 3) ^ seed) & 0xff;
    }
  }
  return Buffer.concat([PNG_SIGNATURE, pngChunk('IHDR', ihdr), pngChunk('IDAT', zlib.deflateSync(raw, { level: 9 })), pngChunk('IEND', Buffer.alloc(0))]);
}

/** PNG 的寬高（不是 PNG 或沒有 IHDR 回 null） */
export function pngSize(buf) {
  if (!Buffer.isBuffer(buf) || buf.length < 24 || !buf.subarray(0, 8).equals(PNG_SIGNATURE) || buf.toString('ascii', 12, 16) !== 'IHDR') return null;
  return { width: buf.readUInt32BE(16), height: buf.readUInt32BE(20) };
}

/**
 * 看起來像 MP4 的位元組（ISO BMFF：24 位元組的 ftyp〔isom、相容 isom 與 mp41〕＋一個 mdat 裝確定的假資料），總長 size。
 * 不是播得出來的影片：只給媒體轉送、Range 與 416 用（伺服器與電腦都不解碼影片）
 */
export function mp4Bytes(size, seed = 1) {
  if (!Number.isInteger(size) || size < 32 || size > 0xffffffff) throw new Error('大小要是 32～4294967295 的整數');
  const ftyp = Buffer.alloc(24);
  ftyp.writeUInt32BE(24, 0);
  ftyp.write('ftypisom', 4, 'ascii');
  ftyp.writeUInt32BE(0x200, 12);
  ftyp.write('isommp41', 16, 'ascii');
  const mdat = Buffer.alloc(8);
  mdat.writeUInt32BE(size - 24, 0);
  mdat.write('mdat', 4, 'ascii');
  return Buffer.concat([ftyp, mdat, seededBytes(seed, size - 32)]);
}

/** Content-Range（RFC 9110 14.4）：「bytes 0-1023/2048」→ { start, end, total, unsatisfied:false }；「bytes *\/2048」→ unsatisfied:true；其他 null */
export function parseContentRange(value) {
  const text = typeof value === 'string' ? value.trim() : '';
  const m = /^bytes (\d+)-(\d+)\/(\d+)$/u.exec(text);
  if (m) {
    const [start, end, total] = m.slice(1).map(Number);
    return start <= end && end < total ? { start, end, total, unsatisfied: false } : null;
  }
  const u = /^bytes \*\/(\d+)$/u.exec(text);
  return u ? { start: null, end: null, total: Number(u[1]), unsatisfied: true } : null;
}

/** 一筆 ≥400 的回應（{ status }）或主控台錯誤（Chrome 的「Failed to load resource: the server responded with a status of 507 (…)」）的狀態碼；看不出來回 null */
export function statusOf(m) {
  if (m && Number.isInteger(m.status)) return m.status;
  const hit = /status of (\d{3})\b/u.exec(String(m?.text ?? ''));
  return hit ? Number(hit[1]) : null;
}

/**
 * blob 配額的上傳計畫（BlobQuotaRules：用量 ≥ 配額×告警% 是 warn、≥ 配額×拒收% 是 refuse；新的 blob 存了之後會到拒收門檻就 507）：
 * fill＝補到 target%（要在告警與拒收之間）的位元組；refused＝之後再傳就會超過拒收門檻的位元組（補到 overshoot%）；
 * lowered＝重啟時調低的配額（用量剛好是它的 98%，已經到拒收門檻）。用量已經到告警門檻、或百分比的順序不對時丟例外
 */
export function quotaPlan({ used, quota, alertPercent = 80, refusePercent = 95, target = 85, overshoot = 97 }) {
  if (![used, quota].every((v) => Number.isSafeInteger(v) && v >= 0) || quota === 0) throw new Error('用量與配額要是非負整數、配額大於 0');
  if (!(alertPercent < target && target < refusePercent && refusePercent < overshoot && overshoot <= 100)) throw new Error('要 告警% < target% < 拒收% < overshoot% ≤ 100');
  if (used * 100 >= quota * alertPercent) throw new Error(`用量（${used}）已經到告警門檻（配額 ${quota} 的 ${alertPercent}%）`);
  const fill = Math.ceil((quota * target) / 100) - used;
  const afterFill = used + fill;
  const refused = Math.ceil((quota * overshoot) / 100) - afterFill;
  const lowered = Math.floor((afterFill * 100) / 98);
  return { fill, afterFill, refused, lowered, warnAt: Math.ceil((quota * alertPercent) / 100), refuseAt: Math.ceil((quota * refusePercent) / 100) };
}
