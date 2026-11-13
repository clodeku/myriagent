// 遠端桌面輸入的端對端加密（看的一方；docs/sync-protocol.md 14.16「輸入的加密」，功能 deske2e1；2026-11-13 擁有者決定：
// 鍵盤輸入在看的一方與被看的那台之間加密，伺服器只轉密文）。演算法和宿主、伺服器的 DeskSealRules 相同，兩邊以同一組固定的向量測：
//   金鑰協商：兩邊各一把這個工作階段才用的 ECDH P-256 金鑰；公鑰是未壓縮的點（65 位元組）的 base64url。
//             這一方的公鑰放在 deskOpen 的 viewerKey，被看的那台的在它第一個封包 status 的 kx.pub。
//   金鑰：HKDF-SHA256（輸入＝ECDH 的共同秘密、salt＝工作階段 id、info＝INFO＋0x00＋看的一方的公鑰＋被看的一方的公鑰）→ AES-256-GCM。
//   每包：n 從 1 起每包加 1；nonce＝4 個 0＋n 的 8 位元組大端；附加驗證資料＝「INFO|工作階段 id|n」；明文＝{"events":[…]}；ct＝密文接驗證碼的 base64url。
// 用瀏覽器內建的 Web Crypto（私鑰不可匯出）；只在安全的來源（https、app 自己的頁面）有，沒有就不加密。
// 公鑰經伺服器轉送、沒有另外簽章：擋的是伺服器看到或留下輸入的內容，不擋主動把公鑰換掉的伺服器（伺服器是擁有者自己的主機）

const INFO = 'myriagent-desk-input-v1';
// 安全桌面的輸入（鎖定畫面、UAC）用的第二把金鑰：那台那一端是鎖定畫面支援的服務（系統權限）而不是 app。
// 服務的公鑰在 status 的 svc.pub；用這把金鑰加密的那一包多帶 k: 'svc'，那台的 app 解不開、原樣轉給服務
const SECURE_INFO = 'myriagent-desk-secure-input-v1';
const PUBLIC_KEY_BYTES = 65;
const encoder = new TextEncoder();

/** 位元組 → base64url（沒有填充） */
export function toBase64Url(bytes) {
  let s = '';
  for (const b of bytes) s += String.fromCharCode(b);
  return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

/** base64url → 位元組；不是合格的 base64url 回 null */
export function fromBase64Url(text) {
  if (typeof text !== 'string' || !/^[A-Za-z0-9_-]*$/.test(text) || text.length % 4 === 1) return null;
  try {
    const bin = atob(text.replace(/-/g, '+').replace(/_/g, '/') + '='.repeat((4 - (text.length % 4)) % 4));
    const out = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
    return out;
  } catch {
    return null;
  }
}

/** 這個環境能不能加密（Web Crypto 只在安全的來源有） */
export function sealSupported() {
  const c = globalThis.crypto;
  return !!(c && c.subtle && typeof c.subtle.generateKey === 'function');
}

/**
 * 產生這個工作階段的金鑰。回 { publicKey, accept, acceptSecure }：publicKey 放進 deskOpen 的 viewerKey；
 * accept(sessionId, targetPublic) 在收到被看的那台的公鑰之後呼叫，回加密器 { seal(events) → {n, ct} }，對方的公鑰不合格回 null；
 * acceptSecure(sessionId, servicePublic) 同樣的做法、對象是那台的系統服務（安全桌面的輸入），加密出來的多帶 k: 'svc'。
 * seal 一次一包、照呼叫的順序編號：呼叫的人要等上一包好了再叫下一包（送出去的順序才和編號一致）。
 * keyPair 只給測試（固定的金鑰）
 */
export async function createViewerSeal(keyPair) {
  const subtle = globalThis.crypto.subtle;
  const pair = keyPair || await subtle.generateKey({ name: 'ECDH', namedCurve: 'P-256' }, false, ['deriveBits']);
  const viewerRaw = new Uint8Array(await subtle.exportKey('raw', pair.publicKey));
  const derive = async (sessionId, targetPublic, label) => {
      const targetRaw = fromBase64Url(targetPublic);
      if (!targetRaw || targetRaw.length !== PUBLIC_KEY_BYTES || targetRaw[0] !== 4) return null;
      let key;
      try {
        const peer = await subtle.importKey('raw', targetRaw, { name: 'ECDH', namedCurve: 'P-256' }, false, []);
        const shared = await subtle.deriveBits({ name: 'ECDH', public: peer }, pair.privateKey, 256);
        const material = await subtle.importKey('raw', shared, 'HKDF', false, ['deriveKey']);
        const prefix = encoder.encode(label);
        const info = new Uint8Array(prefix.length + 1 + PUBLIC_KEY_BYTES * 2);
        info.set(prefix, 0);
        info.set(viewerRaw, prefix.length + 1);
        info.set(targetRaw, prefix.length + 1 + PUBLIC_KEY_BYTES);
        key = await subtle.deriveKey({ name: 'HKDF', hash: 'SHA-256', salt: encoder.encode(sessionId), info },
          material, { name: 'AES-GCM', length: 256 }, false, ['encrypt']);
      } catch {
        // 不在曲線上的點、瀏覽器不支援
        return null;
      }
      let n = 0;
      return {
        async seal(events) {
          const mine = ++n;
          const iv = new Uint8Array(12);
          new DataView(iv.buffer).setBigUint64(4, BigInt(mine));
          const sealed = await subtle.encrypt(
            { name: 'AES-GCM', iv, additionalData: encoder.encode(label + '|' + sessionId + '|' + mine), tagLength: 128 },
            key, encoder.encode(JSON.stringify({ events })));
          const out = { n: mine, ct: toBase64Url(new Uint8Array(sealed)) };
          if (label === SECURE_INFO) out.k = 'svc';
          return out;
        },
      };
  };
  return {
    publicKey: toBase64Url(viewerRaw),
    accept: (sessionId, targetPublic) => derive(sessionId, targetPublic, INFO),
    acceptSecure: (sessionId, servicePublic) => derive(sessionId, servicePublic, SECURE_INFO),
  };
}
