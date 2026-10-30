// Myriagent Hub 的版本頁（/#releases，app 自我更新 U2）：收進來的桌面版、目前提供給裝置的版本、各電腦的 app 版本與更新狀態；撤回與擁有者下載。
// auth.js 第一次顯示版本頁時才以動態 import 載入這個模組（載入失敗不影響登入頁與配對核准頁），以 initReleases({ request, api, ApiError, refresh })
// 交出它的 HTTP 函式與重新整理（這裡不 import auth.js，避免循環）。
// 伺服器傳來的字串一律 textContent。撤回先 window.confirm 再 POST（request 帶 X-Myriagent-Csrf: 1）：只要登入的工作階段，不再輸入密碼（U2 設計 D21）。
// 判斷與顯示文字在 releaserules.js（純函式，tools/hub-e2e/releaserules.test.mjs）。

import { formatBytes, releaseStateText, appPhaseText, shortCommit, withdrawConfirmText } from '/releaserules.js';

const $ = (id) => document.getElementById(id);
const fmt = new Intl.DateTimeFormat('zh-TW', { year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hour12: false });
const when = (ms) => (typeof ms === 'number' && Number.isFinite(ms) && ms > 0 ? fmt.format(new Date(ms)) : '—');
const str = (v) => (typeof v === 'string' && v !== '' ? v : null);

// 撤回的錯誤說明（伺服器的錯誤代碼）；其他照 auth.js 的 ApiError 訊息
const ERRORS = {
  invalid_release: '版本名稱不正確',
  not_found: '找不到這個版本',
};

let deps = null;
let seq = 0;       // 載入的序號：連續重新整理時只採用最後一次的結果
let shown = null;  // 目前畫出的資料（__hubProbe.releases() 回傳）

/** auth.js 啟動時呼叫一次：d 是 { request, api, ApiError, refresh } */
export function initReleases(d) {
  deps = d;
  $('btn-releases-reload').addEventListener('click', () => guard($('btn-releases-reload'), async () => {
    if (await loadReleases()) setMessage('已重新整理', 'ok');
  }));
}

/**
 * 取 GET /api/v1/app/releases 並畫出來；成功回 true。401 往上丟（auth.js 的 refresh() 改顯示登入頁），其他錯誤寫在頁面上、回 false。
 * 晚到的舊結果不採用（回 false）
 */
export async function loadReleases() {
  const mine = ++seq;
  setMessage('載入中…');
  try {
    const view = await deps.request('GET', '/api/v1/app/releases');
    if (mine !== seq) return false;
    render(view);
    setMessage('');
    return true;
  } catch (e) {
    if (mine !== seq) return false;
    if (e instanceof deps.ApiError && e.status === 401) throw e;
    setMessage(errorText(e), 'error');
    return false;
  }
}

/** 畫出版本表與電腦表（view 是 GET /api/v1/app/releases 的回應；欄位不對的當成空的） */
export function render(view) {
  const releases = Array.isArray(view?.releases) ? view.releases.filter((r) => r && typeof r === 'object') : [];
  const devices = Array.isArray(view?.devices) ? view.devices.filter((d) => d && typeof d === 'object') : [];
  const latest = str(view?.latest);
  shown = { releases, latest, devices };
  $('releases-empty').hidden = releases.length > 0;
  $('releases-table').hidden = releases.length === 0;
  $('releases-rows').replaceChildren(...releases.map((r) => releaseRow(r, latest)));
  $('devices-empty').hidden = devices.length > 0;
  $('devices-table').hidden = devices.length === 0;
  $('devices-rows').replaceChildren(...devices.map(deviceRow));
}

/** 目前畫出的資料（給端到端測試；還沒載入過是 null） */
export function renderedReleases() {
  return shown === null ? null : JSON.parse(JSON.stringify(shown));
}

function releaseRow(r, latest) {
  const tr = document.createElement('tr');
  const release = str(r.release);
  tr.dataset.release = release ?? '';
  if (r.state !== 'available') tr.classList.add('is-gone');

  const version = cell('rr-version', str(r.version) ?? '—');
  if (release !== null && release === latest) {
    const badge = document.createElement('span');
    badge.className = 'badge';
    badge.textContent = '目前提供';
    version.append(badge);
  }
  const commit = cell('rr-commit', shortCommit(r.commit));
  if (str(r.commit)) commit.title = r.commit;
  const state = cell('rr-state', releaseStateText(r.state));
  // 狀態前面一顆燈（auth.css 的 data-led）：可用綠、撤回紅、清除灰
  state.dataset.led = r.state === 'available' ? 'ok' : r.state === 'withdrawn' ? 'bad' : 'off';
  if (r.state === 'withdrawn') state.title = `撤回時間 ${when(r.withdrawnAt)}`;
  const kid = cell('rr-kid', '');
  const code = document.createElement('code');
  code.textContent = str(r.kid) ?? '—';
  kid.append(code);

  const actions = cell('rr-actions', '');
  if (release !== null && r.state === 'available') {
    const box = document.createElement('div');
    box.className = 'cell-actions';
    const link = document.createElement('a');
    link.className = 'btn sm ghost';
    link.href = `/api/v1/app/releases/${encodeURIComponent(release)}/package`;
    link.setAttribute('download', '');
    link.textContent = '下載';
    const withdraw = document.createElement('button');
    withdraw.type = 'button';
    withdraw.className = 'btn sm danger';
    withdraw.textContent = '撤回';
    withdraw.addEventListener('click', () => guard(withdraw, () => withdrawRelease(release)));
    box.append(link, withdraw);
    actions.append(box);
  } else {
    actions.textContent = '—';
  }

  tr.append(version, cell('rr-build', str(r.build) ?? '—'), commit, cell('rr-size', formatBytes(r.zipBytes)), cell('rr-ingested', when(r.ingestedAt)),
    state, kid, actions);
  return tr;
}

function deviceRow(d) {
  const tr = document.createElement('tr');
  tr.dataset.device = str(d.deviceId) ?? '';
  const conn = d.updateOnly === true ? '只更新' : d.online === true ? '在線' : '離線';
  const update = cell('rd-update', '');
  const app = d.app && typeof d.app === 'object' ? d.app : null;
  if (app) {
    const phase = document.createElement('div');
    phase.textContent = appPhaseText(app.phase);
    update.append(phase);
    if (str(app.staged)) update.append(sub(`已備妥 ${app.staged}`));
    if (str(app.lastError)) {
      const error = sub(app.lastError);
      error.classList.add('rd-error');
      update.append(error);
    }
    update.title = `最後檢查 ${when(app.checkedAt)}`;
  } else {
    update.textContent = '—';
  }
  const connCell = cell('rd-conn', conn);
  connCell.dataset.led = d.updateOnly === true ? 'warn' : d.online === true ? 'ok' : 'off';
  tr.append(cell('rd-name', str(d.name) ?? '—'), cell('rd-version', str(d.appVersion) ?? '—'), cell('rd-hello', when(d.lastHelloAt)), connCell, update);
  return tr;
}

function cell(cls, text) {
  const td = document.createElement('td');
  td.className = cls;
  td.textContent = text;
  return td;
}

function sub(text) {
  const div = document.createElement('div');
  div.className = 'cell-sub';
  div.textContent = text;
  return div;
}

async function withdrawRelease(release) {
  const ok = window.confirm(withdrawConfirmText(release, shown?.latest ?? null));
  if (!ok) return;
  const result = await deps.api('POST', `/api/v1/app/releases/${encodeURIComponent(release)}/withdraw`, {});
  await loadReleases();
  setMessage(result && result.alreadyWithdrawn ? `${release} 先前已經撤回` : `已撤回 ${release}`, 'ok');
}

function setMessage(text, tone) {
  const el = $('releases-message');
  el.textContent = text || '';
  el.dataset.tone = tone || '';
  el.hidden = !text;
}

function errorText(e) {
  if (e instanceof deps.ApiError && Object.hasOwn(ERRORS, e.code ?? '')) return ERRORS[e.code];
  return e && e.message ? e.message : '發生錯誤，請稍後再試';
}

// 按鈕的共用處理：執行中停用；401 回登入頁（auth.js 的 refresh），其他錯誤寫在頁面上
async function guard(control, work) {
  control.disabled = true;
  try {
    await work();
  } catch (e) {
    if (e instanceof deps.ApiError && e.status === 401) {
      await deps.refresh().catch((err) => setMessage(errorText(err), 'error'));
      return;
    }
    setMessage(errorText(e), 'error');
  } finally {
    control.disabled = false;
  }
}
