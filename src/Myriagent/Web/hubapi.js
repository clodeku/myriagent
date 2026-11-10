// 瀏覽器版的擁有者 HTTP 操作（WP3.5 步驟 10；web-protocol 第六節第 12 點）：撤銷電腦、移除已撤銷的電腦（設計 14.22）、從目錄刪除帳戶、上傳附件（E4，WP4.6 步 11）。只在 isHub 時呼叫（桌面不會走到這裡）。
// - 一律同源 POST、帶 X-Myriagent-Csrf: 1 與 Content-Type: application/json（伺服器的 OriginGuard；撤銷的原因不是 JSON 會被當成沒有原因）。
// - 要近期驗證：403 step_up_required → 密碼對話框 → POST /api/v1/auth/stepup → 原請求重送一次；重送後還是 step_up_required 就回錯誤，不迴圈。
// - 任何一個請求 401（登入失效）→ location.replace('/')。
// - 成功之後不自己改畫面：結果由伺服器推送的 devices／accounts 帶回來。伺服器回的錯誤只取 error 代碼，給人看的文字由 remoterules.ownerErrorText 產生。
// - 密碼只放在輸入框裡，送出時取值、立刻清空輸入框，不存在任何變數或狀態裡。
import { h } from './util.js';
import { openDialog } from './ui.js';
import { ownerErrorText } from './remoterules.js';

/** POST 擁有者端點 → { ok:true, json } | { ok:false, status（0＝網路錯誤）, code, retryAfter（秒或 null）} */
export async function ownerPost(path, body) {
  let res;
  try {
    res = await fetch(path, {
      method: 'POST',
      headers: { 'X-Myriagent-Csrf': '1', 'Content-Type': 'application/json' },
      body: JSON.stringify(body ?? {}),
      credentials: 'same-origin',
      cache: 'no-store',
    });
  } catch {
    return { ok: false, status: 0, code: null, retryAfter: null };
  }
  let json = null;
  try {
    const text = await res.text();
    json = text ? JSON.parse(text) : null;
  } catch {
    json = null;
  }
  if (res.ok) return { ok: true, json };
  if (res.status === 401) location.replace('/');
  const code = json && typeof json.error === 'string' ? json.error : null;
  const ra = Number(res.headers.get('Retry-After'));
  return { ok: false, status: res.status, code, retryAfter: Number.isFinite(ra) && ra > 0 ? ra : null };
}

const needsStepUp = (r) => !r.ok && r.status === 403 && r.code === 'step_up_required';

/**
 * 執行 run（回 ownerPost 的結果）；要近期驗證時先請擁有者再輸入一次密碼、通過後重送一次。
 * 取消密碼對話框 → { ok:false, cancelled:true }（不再送任何請求）
 */
export async function withStepUp(run) {
  const first = await run();
  if (!needsStepUp(first)) return first;
  if (!(await askStepUp())) return { ok: false, cancelled: true };
  return run();
}

export const revokeDevice = (id, reason) => withStepUp(() => ownerPost(`/api/v1/devices/${encodeURIComponent(id)}/revoke`, { reason }));
export const removeDevice = (id) => withStepUp(() => ownerPost(`/api/v1/devices/${encodeURIComponent(id)}/remove`, {}));
export const deleteAccount = (id) => withStepUp(() => ownerPost(`/api/v1/accounts/${encodeURIComponent(id)}/delete`, {}));

/**
 * 再輸入一次密碼的對話框：送出 POST /api/v1/auth/stepup。通過回 true；取消（按鈕、Esc、關閉、點背景）回 false。
 * 密碼錯誤、太頻繁等錯誤顯示在框內，可以再試
 */
function askStepUp() {
  return new Promise((resolve) => {
    let settled = false;
    let busy = false;
    const settle = (ok) => { if (settled) return; settled = true; resolve(ok); };
    // 隱藏的使用者名稱欄讓密碼管理員認得這是哪個帳號（同登入頁的 dlg-stepup）
    const user = h('input', { class: 'sr-only', name: 'username', autocomplete: 'username', value: 'admin', tabIndex: -1, 'aria-hidden': 'true', readonly: 'readonly' });
    const input = h('input', { type: 'password', class: 'input', name: 'password', autocomplete: 'current-password', required: 'required', 'aria-label': '密碼', spellcheck: 'false' });
    const err = h('p', { class: 'stepup-err', role: 'alert', hidden: true });
    const form = h('form', { class: 'form stepup-form', novalidate: 'novalidate' },
      h('p', { class: 'confirm-detail', text: '這個操作要先確認是你本人。' }),
      user,
      h('label', { class: 'field' }, h('span', { class: 'field-label', text: '密碼' }), input),
      err);
    const cancel = h('button', { type: 'button', class: 'btn', text: '取消' });
    const ok = h('button', { type: 'button', class: 'btn primary', text: '確認' });
    const d = openDialog({ title: '再輸入一次密碼', body: form, footer: [cancel, ok], width: 400, className: 'dlg-stepup', initialFocus: input, onClose: () => { input.value = ''; settle(false); } });
    const showError = (text) => { err.textContent = text; err.hidden = false; };
    const submit = async (e) => {
      if (e) e.preventDefault();
      if (busy) return;
      const password = input.value;
      if (!password) { showError('請輸入密碼'); input.focus(); return; }
      input.value = '';
      busy = true;
      ok.disabled = true;
      err.hidden = true;
      const r = await ownerPost('/api/v1/auth/stepup', { password });
      busy = false;
      ok.disabled = false;
      if (settled) return; // 等回應的時候對話框被關掉了
      if (r.ok) { settle(true); d.close(); return; }
      showError(ownerErrorText('stepup', r.status, r.code, r.retryAfter));
      input.focus();
    };
    form.addEventListener('submit', submit);
    ok.addEventListener('click', submit);
    input.addEventListener('keydown', (e) => { if (e.key === 'Enter' && !e.isComposing && e.keyCode !== 229) submit(e); });
    cancel.addEventListener('click', () => d.close());
  });
}

/**
 * 〔遠端〕網頁上傳（E4，WP4.6 步 11、D32）：XMLHttpRequest PUT /api/v1/uploads/{uploadId}，本文是檔案的位元組，帶 X-Myriagent-Csrf: 1
 * （fetch 沒有上傳進度）；onProgress(已送位元組, 總位元組) 隨上傳回報。→ Promise<{ ok, status（0＝網路錯誤或中止）, json }>，
 * 不 reject。401（登入失效）→ location.replace('/')。結果怎麼解讀（id、雜湊、錯誤文字）照 liverules.uploadResult。
 * signal（選用的 AbortSignal）：中止時 xhr.abort()，結果同中止（status 0）；伺服器收到中斷就立刻釋放預留（合併前審查 PERF-2）
 */
export function uploadFile(uploadId, file, onProgress, signal) {
  return new Promise((resolve) => {
    let done = false;
    const finish = (status, text) => {
      if (done) return;
      done = true;
      let json = null;
      try {
        json = text ? JSON.parse(text) : null;
      } catch {
        json = null;
      }
      if (status === 401) location.replace('/');
      resolve({ ok: status >= 200 && status < 300, status, json });
    };
    let xhr;
    try {
      xhr = new XMLHttpRequest();
      xhr.open('PUT', '/api/v1/uploads/' + encodeURIComponent(uploadId), true);
      xhr.setRequestHeader('X-Myriagent-Csrf', '1');
      if (typeof onProgress === 'function') {
        xhr.upload.addEventListener('progress', (e) => {
          try { onProgress(e.loaded, e.lengthComputable ? e.total : file.size); } catch { /* 畫面的錯誤不影響上傳 */ }
        });
      }
      xhr.addEventListener('load', () => finish(xhr.status, xhr.responseText));
      xhr.addEventListener('error', () => finish(0, null));
      xhr.addEventListener('abort', () => finish(0, null));
      xhr.addEventListener('timeout', () => finish(0, null));
      if (signal) {
        if (signal.aborted) { finish(0, null); return; }
        signal.addEventListener('abort', () => { try { xhr.abort(); } catch { /* 忽略 */ } finish(0, null); }, { once: true });
      }
      xhr.send(file);
    } catch {
      finish(0, null);
    }
  });
}

/**
 * 〔電腦狀態〕瀏覽器版下載整個資料夾的一個檔：GET 本站的 /f/{key}（fileRefs 給的網址），回 Response（呼叫端以 body 串流寫進選的資料夾）。
 * 只收本站 /f/ 開頭的相對網址；401（登入失效）→ location.replace('/')
 */
export async function fetchTransfer(url, signal, range) {
  if (typeof url !== 'string' || !url.startsWith('/f/')) throw new Error('網址不合格');
  // range：「bytes=0-8388607」（電腦狀態面板的資料夾下載一段一段要，暫停之後接著要；2026-11-10）
  const headers = typeof range === 'string' && /^bytes=\d+-\d*$/.test(range) ? { Range: range } : undefined;
  const res = await fetch(url, { credentials: 'same-origin', signal, headers });
  if (res.status === 401) location.replace('/');
  return res;
}
