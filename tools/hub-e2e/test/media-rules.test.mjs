// lib/media-rules.mjs 的單元測試（node:test；WP4.9 步 16）：PNG 與 MP4 夾具、確定的假資料、Content-Range、狀態碼、blob 配額的上傳計畫。
// 不啟動任何程序、不開瀏覽器、不碰檔案
import { test } from 'node:test';
import assert from 'node:assert/strict';
import zlib from 'node:zlib';
import { mp4Bytes, parseContentRange, PNG_SIGNATURE, pngImage, pngSize, quotaPlan, seededBytes, statusOf } from '../lib/media-rules.mjs';

/** PNG 的每個區塊（長度、型別、資料、CRC 對不對） */
function chunks(buf) {
  const out = [];
  let o = 8;
  while (o < buf.length) {
    const length = buf.readUInt32BE(o);
    const type = buf.toString('ascii', o + 4, o + 8);
    const data = buf.subarray(o + 8, o + 8 + length);
    const crc = buf.readUInt32BE(o + 8 + length);
    out.push({ type, data, crcOk: (zlib.crc32(buf.subarray(o + 4, o + 8 + length)) >>> 0) === crc });
    o += 12 + length;
  }
  return out;
}

test('seededBytes_同一個種子同樣的位元組_不同種子不同_0也可以', () => {
  assert.deepEqual(seededBytes(7, 64), seededBytes(7, 64));
  assert.notDeepEqual(seededBytes(7, 64), seededBytes(8, 64));
  assert.equal(seededBytes(0, 16).length, 16);
  assert.ok(seededBytes(0, 16).some((b) => b !== 0), '種子 0 換成非零的固定值');
  assert.deepEqual(seededBytes(5, 0), Buffer.alloc(0));
  assert.throws(() => seededBytes(1, -1), /非負整數/);
});

test('pngImage_合格的PNG_簽章IHDR與每個區塊的CRC_解壓後的大小對得上_同樣的參數同樣的位元組', () => {
  const png = pngImage(40, 30, 3);
  assert.ok(png.subarray(0, 8).equals(PNG_SIGNATURE));
  const list = chunks(png);
  assert.deepEqual(list.map((c) => c.type), ['IHDR', 'IDAT', 'IEND']);
  assert.ok(list.every((c) => c.crcOk), '每個區塊的 CRC 都對');
  assert.deepEqual(pngSize(png), { width: 40, height: 30 });
  assert.deepEqual([...list[0].data.subarray(8)], [8, 2, 0, 0, 0], '8 位元 RGB、不交錯');
  const raw = zlib.inflateSync(list[1].data);
  assert.equal(raw.length, 30 * (1 + 40 * 3));
  assert.ok([...Array(30).keys()].every((y) => raw[y * (1 + 40 * 3)] === 0), '每列的濾波都是 0');
  assert.deepEqual(pngImage(40, 30, 3), png);
  assert.notDeepEqual(pngImage(40, 30, 4), png);
  assert.throws(() => pngImage(0, 10), /1～4096/);
  assert.equal(pngSize(Buffer.from('not a png at all, really not')), null);
});

test('mp4Bytes_ftyp加mdat_總長與box的大小對得上', () => {
  const mp4 = mp4Bytes(4096, 9);
  assert.equal(mp4.length, 4096);
  assert.equal(mp4.readUInt32BE(0), 24);
  assert.equal(mp4.toString('ascii', 4, 12), 'ftypisom');
  assert.equal(mp4.toString('ascii', 16, 24), 'isommp41');
  assert.equal(mp4.readUInt32BE(24), 4096 - 24, 'mdat 一直到檔尾');
  assert.equal(mp4.toString('ascii', 28, 32), 'mdat');
  assert.deepEqual(mp4Bytes(4096, 9), mp4);
  assert.throws(() => mp4Bytes(31), /32/);
});

test('parseContentRange_區間與不滿足的兩種形狀_其他是null', () => {
  assert.deepEqual(parseContentRange('bytes 0-1023/1048576'), { start: 0, end: 1023, total: 1048576, unsatisfied: false });
  assert.deepEqual(parseContentRange(' bytes 5-5/6 '), { start: 5, end: 5, total: 6, unsatisfied: false });
  assert.deepEqual(parseContentRange('bytes */1048576'), { start: null, end: null, total: 1048576, unsatisfied: true });
  for (const bad of [null, '', 'bytes 10-5/20', 'bytes 0-20/20', 'bytes=0-1/2', 'items 0-1/2', 'bytes 0-1/*']) assert.equal(parseContentRange(bad), null, String(bad));
});

test('statusOf_回應的status_主控台的status_of_N_其他是null', () => {
  assert.equal(statusOf({ status: 507, path: '/api/v1/uploads/x' }), 507);
  assert.equal(statusOf({ type: 'error', text: 'Failed to load resource: the server responded with a status of 416 (Range Not Satisfiable)' }), 416);
  assert.equal(statusOf({ type: 'error', text: 'WebSocket connection failed' }), null);
  assert.equal(statusOf(null), null);
});

test('quotaPlan_補到告警與拒收之間_再傳就超過拒收門檻_調低的配額讓用量是98%', () => {
  const quota = 16 * 1024 * 1024;
  const p = quotaPlan({ used: 400_000, quota });
  assert.equal(p.afterFill, Math.ceil(quota * 0.85));
  assert.equal(p.fill, p.afterFill - 400_000);
  assert.ok(p.afterFill * 100 >= quota * 80 && p.afterFill * 100 < quota * 95, '補完是 warn');
  assert.ok((p.afterFill + p.refused) * 100 >= quota * 95, '再傳 refused 個位元組就到拒收門檻（507）');
  assert.ok(p.lowered < quota && p.afterFill * 100 >= p.lowered * 95, '調低之後用量已經到拒收門檻（refuse）');
  assert.throws(() => quotaPlan({ used: Math.ceil(quota * 0.8), quota }), /告警門檻/);
  assert.throws(() => quotaPlan({ used: 1, quota, target: 96 }), /target/);
  assert.throws(() => quotaPlan({ used: 1, quota: 0 }), /配額/);
});
