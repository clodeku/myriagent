// 正式站守門的純函式（WP4.9 步 7、計畫 D18；C# 的 tools/fake-device/Runtime/LocalHubTargetRules.cs 是同一張測試表）：
// 工具只連自己在這台電腦啟動的 MyriagentHub。連線之前只收 https 加迴路主機名稱、拒絕任何 example.com；連上之後再讀 /readyz 與憑證，
// 正式服務（environment 是 service）、憑證名稱含 hub.example.com、或簽發者是公開 CA 任一成立就拒絕——只看主機名稱擋不住
// 「ssh -L 本機埠:127.0.0.1:443」的轉送（/readyz 對本機迴路開放，轉送過去會以迴路身分讀到正式站）。另有新鮮度與暫存路徑的判斷。
import os from 'node:os';
import path from 'node:path';

/** 只收這三個主機名稱（URL.hostname 的寫法；IPv6 帶方括號） */
const LOOPBACK_HOSTS = new Set(['127.0.0.1', 'localhost', '[::1]']);

/** 正式站的網域（任何主機名稱、憑證名稱含它就拒絕） */
export const PRODUCTION_DOMAIN = 'example.com';

/** 正式站的主機名稱（憑證與 /readyz 的 tls.name） */
export const PRODUCTION_HOST = 'hub.example.com';

// 公開 CA 的簽發者名稱（Let's Encrypt 的中繼憑證 R10～R14、E5～E9、ISRG，與常見的商業 CA）；本機的自簽與 Pebble 都不在裡面
const PUBLIC_CA = /Let'?s Encrypt|\bISRG\b|\bCN=(?:R1\d|E\d+|YE\d|YR\d)\b|DigiCert|Sectigo|GlobalSign|ZeroSSL|Google Trust|GoDaddy|Entrust|Amazon|Microsoft Azure|TWCA|Chunghwa/iu;

/** 連線之前：只收 https、主機是迴路（127.0.0.1、localhost、[::1]）、沒有帳號密碼；任何含 example.com 的主機一律拒絕 */
export function isAllowedServerUrl(url) {
  let u;
  try {
    u = new URL(String(url));
  } catch {
    return false;
  }
  if (u.protocol !== 'https:') return false;
  if (u.username || u.password) return false;
  const host = u.hostname.toLowerCase();
  if (host.includes(PRODUCTION_DOMAIN)) return false;
  return LOOPBACK_HOSTS.has(host);
}

function texts(...values) {
  const out = [];
  for (const v of values) {
    if (v == null) continue;
    if (Array.isArray(v)) out.push(...texts(...v));
    else if (typeof v === 'object') out.push(JSON.stringify(v)); // Node 的 getPeerCertificate：subject、issuer 是 { CN, O, … }
    else out.push(String(v));
  }
  return out;
}

/**
 * 連上之後：是不是正式站（是就拒絕）。readyz 是 /readyz 的內容（JSON），tls 是連線看到的憑證
 * （{ subject, name, dnsNames, subjectaltname, issuer } 任一子集；Node 的 getPeerCertificate 或 /readyz 的 tls 區塊都行）。
 * environment 是 service、憑證或 /readyz 的 tls 名稱含 hub.example.com、或簽發者是公開 CA 任一成立就回 true
 */
export function isProductionTarget(readyz, tls) {
  if (readyz?.environment === 'service') return true;
  const names = texts(tls?.name, tls?.subject, tls?.dnsNames, tls?.subjectaltname, readyz?.tls?.name, readyz?.tls?.subject, readyz?.tls?.dnsNames);
  if (names.some((n) => n.toLowerCase().includes(PRODUCTION_HOST))) return true;
  const issuers = texts(tls?.issuer, readyz?.tls?.issuer);
  return issuers.some((i) => PUBLIC_CA.test(i));
}

/**
 * hub-e2e.mjs 的 --base（對已經在跑的伺服器，WP4.9 合併前審查）：連線之前網址要過 isAllowedServerUrl；連上之後 /readyz 要 200、
 * environment 是 console（本機主控台）、而且 isProductionTarget 不成立——只看主機名稱擋不住「ssh -L」轉送到正式服務。
 * readyz 是 { status, json }（還沒連線時給 null：只判網址），cert 是連線看到的憑證（Node 的 getPeerCertificate）。回 { ok, reason }
 */
export function checkExternalBase(url, readyz, cert) {
  if (!isAllowedServerUrl(url)) return { ok: false, reason: `--base 只收 https＋127.0.0.1／localhost／[::1]、不帶帳號密碼，任何 ${PRODUCTION_DOMAIN} 一律拒絕：${url}` };
  if (readyz === null || readyz === undefined) return { ok: true, reason: null };
  if (readyz.status !== 200) return { ok: false, reason: `/readyz 回 ${readyz.status ?? '沒有回應'}（要 200）：不知道對方是不是正式服務` };
  if (readyz.json?.environment !== 'console') return { ok: false, reason: `/readyz 的 environment 是 ${readyz.json?.environment ?? '（沒有）'}（要 console；service 是正式的 Windows 服務）` };
  if (isProductionTarget(readyz.json, cert)) return { ok: false, reason: `連線看到的憑證或 /readyz 的 tls 是正式站（${PRODUCTION_HOST} 或公開 CA）：可能是 ssh 轉送到正式服務` };
  return { ok: true, reason: null };
}

/** exe 比任何一個原始檔舊（要重新建置）：exeMtime 與 sourceMtimes 都是毫秒；沒有 exe 也算舊 */
export function isExeStale(exeMtime, sourceMtimes) {
  if (!Number.isFinite(exeMtime)) return true;
  return (sourceMtimes ?? []).some((m) => Number.isFinite(m) && m > exeMtime);
}

/** 路徑在暫存資料夾底下（不是暫存資料夾本身；不分大小寫）：工具只刪、只寫這種路徑 */
export function isUnderTemp(target, tempRoot = os.tmpdir()) {
  if (typeof target !== 'string' || target.length === 0) return false;
  const root = path.resolve(tempRoot).toLowerCase();
  const full = path.resolve(target).toLowerCase();
  const rel = path.relative(root, full);
  return rel.length > 0 && !rel.startsWith('..') && !path.isAbsolute(rel);
}
