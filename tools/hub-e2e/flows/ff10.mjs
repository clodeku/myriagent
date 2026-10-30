// FF10 撤銷（WP4.9 步 17，計畫塊 14）：HOME 一個對話 h1，網頁 P 開著它（另有一條原始連線）。網頁做近期驗證（第 3 階段照舊）之後撤銷 HOME。
// 斷言：撤銷 200 之後 1 秒內 HOME 斷線（假裝置的同步狀態離開 Live）、之後重連的 negotiate 401（同步停在 Unauthorized「權杖已撤銷…」）；
// 對 HOME 的指令立即被拒；HOME 的對話仍可讀、狀態寫「電腦離線」（設計:3111）；裝置清單標為撤銷；再撤銷一次 409 already_revoked。
// 收尾：被撤銷的那台以控制通道的 unpair 刪掉本機的配對資料（它連不上，quit 等 outbox 清空只會逾時）。
import { appLayout, openApp, waitFor } from '../lib/harness.mjs';
import { flowRecorder, rawClose, rawCmd, rawCompletion, rawConnect, rawMark, rawMessages, rawWait, stepUp, trackHubFrames } from '../lib/flow-page.mjs';
import { control, deviceStatus, newConversation, pageCleanStep, sleep, waitSnapshots } from './common.mjs';

/** 伺服器給離線電腦的對話狀態（ConversationViewRules.OfflineStatus） */
const OFFLINE_STATUS = '電腦離線';

/** 撤銷之後多久內要斷線 */
const DISCONNECT_MS = 1000;

/** startEnv 的選項：預設的兩台；頁面記錄器 */
export const env = {
  initOpts: { lightMessages: true },
  initScripts: [{ fn: flowRecorder }],
};

/** 在頁面裡以同源的 cookie POST 一個本站的擁有者 API（X-Myriagent-Csrf: 1）：{ status, json } */
function pagePost(app, url, body) {
  return app.page.evaluate(async ([u, b]) => {
    const r = await fetch(u, { method: 'POST', credentials: 'same-origin', cache: 'no-store', headers: { 'Content-Type': 'application/json', 'X-Myriagent-Csrf': '1' }, body: JSON.stringify(b) });
    let json = null;
    try { json = await r.json(); } catch { /* 不是 JSON */ }
    return { status: r.status, json };
  }, [url, body]).catch((e) => ({ status: null, json: null, error: String(e?.message ?? e) }));
}

/** 原始連線看到的某台 DeviceView（最新的 devices，沒有時看 init.remote.devices） */
async function rawDevice(app, id) {
  const lists = await rawMessages(app, 'devices');
  const list = lists.length ? lists.at(-1).devices : (await rawMessages(app, 'init')).at(-1)?.remote?.devices;
  return (list ?? []).find((d) => d.id === id) ?? null;
}

export async function run(env) {
  const home = env.devices.get('HOME');
  const outcome = {};
  const revokePath = `/api/v1/devices/${home.deviceId}/revoke`;
  // 回應（status）與主控台的「Failed to load resource … 409」（只有網址與文字）都算
  env.expected.push((m) => (m.path === revokePath && (m.status === 409 || /status of 409/u.test(m.text ?? '')) ? 'FF10：故意再撤銷一次（409 already_revoked）' : null));

  // 1. HOME 的對話；P 開著它、另有一條原始連線
  const h1 = await newConversation(home, 'ff10', 'FF10 撤銷');
  const P = await openApp(env, 'P', { layout: appLayout([{ tabs: [h1] }]), beforeGoto: trackHubFrames });
  const ready = P.live && (await waitSnapshots(P, [h1]));
  const raw = await rawConnect(P);
  const live0 = (await deviceStatus(home))?.state ?? null;
  env.step('FF10：P 開著 HOME 的對話；HOME 在線', !!ready && raw.ok && live0 === 'Live',
    `P ${P.live ? '連上' : '沒連上'}、快照${ready ? '收到' : '沒收到'}、原始連線${raw.ok ? '連上' : `沒連上：${raw.error}`}；HOME 的同步 ${live0 ?? '—'}`, { h1 });
  if (!ready || !raw.ok || live0 !== 'Live') throw new Error('頁面沒有開著 HOME 的對話或 HOME 不在線');

  // 2. 近期驗證（第 3 階段照舊）→ 撤銷；同時每 50 ms 看一次 HOME 的同步狀態
  const up = await stepUp(P, env.password);
  let offAt = null;
  let offState = null;
  let polling = true;
  const poller = (async () => {
    while (polling && offAt === null) {
      const s = await home.run.control('status', {}, 5000);
      if (s.ok && s.result.state !== 'Live') {
        offAt = Date.now();
        offState = s.result.state;
        break;
      }
      await sleep(50);
    }
  })();
  const sentAt = Date.now();
  const rv = await pagePost(P, revokePath, { reason: 'retired' });
  const okAt = Date.now();
  await Promise.race([poller, sleep(10000)]);
  polling = false;
  const disconnectMs = offAt === null ? null : Math.max(0, offAt - okAt);
  outcome.revoke = { stepUp: up.status, revoke: rv.status, ok: rv.json?.ok === true, disconnectedWithin1s: disconnectMs !== null && disconnectMs <= DISCONNECT_MS };
  env.step('FF10：近期驗證之後撤銷 HOME：200、1 秒內斷線', up.status === 200 && rv.status === 200 && rv.json?.ok === true && disconnectMs !== null && disconnectMs <= DISCONNECT_MS,
    `stepup ${up.status}；撤銷 ${rv.status}${rv.json?.error ? `（${rv.json.error}）` : ''}（請求 ${okAt - sentAt} ms）；HOME 的同步${offAt === null ? ' 10 秒內沒有離開 Live' : `在回應之後 ${disconnectMs} ms 變成 ${offState}`}（上限 ${DISCONNECT_MS} ms）`,
    { stepUp: up.status, revoke: rv, disconnectMs, offState, requestMs: okAt - sentAt });

  // 3. 之後的重連：negotiate 401 → 同步停在 Unauthorized（權杖已撤銷，請解除配對後重新配對）
  const stopped = await waitFor(async () => {
    const s = await deviceStatus(home, 10000);
    return s && s.state === 'Stopped' && s.stopReason === 'Unauthorized' ? s : null;
  }, 60000, 500);
  outcome.stopped = { state: stopped?.state ?? null, reason: stopped?.stopReason ?? null };
  env.step('FF10：之後 negotiate 401（同步停在 Unauthorized）', !!stopped,
    stopped ? `HOME 的同步 ${stopped.state}（${stopped.stopReason}）：「${stopped.detail ?? '—'}」` : '60 秒內沒有停在 Unauthorized', { status: stopped && { state: stopped.state, stopReason: stopped.stopReason, detail: stopped.detail } });

  // 4. 對 HOME 的指令立即被拒
  const t0 = Date.now();
  const sent = await rawCmd(P, { cmd: 'rename', conversationId: h1, title: 'FF10 撤銷之後改名' });
  const done = sent.invocationId ? await rawCompletion(P, sent.invocationId, 10000) : null;
  const accepted = done?.result?.accepted ?? null;
  const error = done?.result?.error ?? done?.error ?? null;
  const result = accepted !== false && sent.invocationId ? await rawWait(P, { type: 'commandResult', commandId: sent.commandId }, { ms: 10000 }) : null;
  const rejectMs = Date.now() - t0;
  // 撤銷專屬的拒絕（CommandRouteRules.CheckConversation：Cmd 照收、commandResult rejected/not_owner）：這時 HOME 已經 Unauthorized 斷線，
  // 只看「被拒」的話撤銷的檢查壞了也會以 offline 被拒而照樣通過（WP4.9 合併前審查）；Cmd 直接被拒（CmdError）也不是這條路
  const rejected = accepted !== false && result?.m.state === 'rejected' && result?.m.code === 'not_owner';
  outcome.command = { accepted, error, state: result?.m.state ?? null, code: result?.m.code ?? null, quick: rejectMs <= 3000 };
  env.step('FF10：對 HOME 的指令立即被拒（rejected／not_owner：撤銷的檢查，不是離線）', rejected && rejectMs <= 3000,
    `rename：Cmd ${accepted === false ? `拒絕（${error}）` : accepted === true ? '接受' : '沒有回傳'}${result ? `、commandResult ${result.m.state}${result.m.code ? `（${result.m.code}）` : ''}${result.m.message ? `「${result.m.message}」` : ''}` : ''}；${rejectMs} ms`,
    { accepted, error, result: result?.m ?? null, ms: rejectMs });

  // 5. HOME 的對話仍可讀、狀態寫「電腦離線」；裝置清單標為撤銷
  const mark = await rawMark(P);
  await rawCmd(P, { cmd: 'open', conversationId: h1 });
  const snap = await rawWait(P, { type: 'conversation', conversationId: h1 }, { from: mark, ms: 15000 });
  await rawCmd(P, { cmd: 'close', conversationId: h1 });
  const status = snap?.m.header?.status ?? null;
  const items = snap?.m.items?.length ?? 0;
  const view = await waitFor(async () => {
    const d = await rawDevice(P, home.deviceId);
    return d && d.revoked ? d : null;
  }, 15000, 300);
  const domStatus = await waitFor(() => P.page.evaluate(([sel, t]) => (document.querySelector(sel)?.textContent ?? '').includes(t), [`.conv[data-conv="${h1}"]`, OFFLINE_STATUS]).catch(() => null), 15000, 300);
  outcome.readable = { items: items > 0, status, dom: !!domStatus, revoked: view?.revoked?.reason ?? null, online: view?.online ?? null };
  env.step('FF10：HOME 的對話仍可讀、狀態寫「電腦離線」（設計:3111）；裝置清單標為撤銷', items > 0 && status === OFFLINE_STATUS && !!domStatus && view?.revoked?.reason === 'retired' && view.online === false,
    `open：${snap ? `${items} 則、標頭的狀態「${status ?? '—'}」` : '15 秒內沒有 conversation'}；P 的畫面${domStatus ? `有「${OFFLINE_STATUS}」` : `沒有「${OFFLINE_STATUS}」`}；`
      + `裝置清單的 HOME：${view ? `撤銷（${view.revoked.reason}）、${view.online ? '在線' : '離線'}` : '15 秒內沒有標為撤銷'}`,
    { header: snap?.m.header ?? null, device: view });

  // 6. 再撤銷一次：409 already_revoked
  const again = await pagePost(P, revokePath, { reason: 'retired' });
  outcome.again = `${again.status}/${again.json?.error ?? '—'}`;
  env.step('FF10：再撤銷一次回 409 already_revoked', again.status === 409 && again.json?.error === 'already_revoked',
    `${again.status}（${again.json?.error ?? '—'}）`, { again });
  await rawClose(P);

  // 7. 收掉被撤銷的那台：刪掉本機的配對資料（它連不上伺服器，之後 run 不等 outbox 清空）
  const unpair = await control(home, 'unpair', {}, 60000);
  outcome.unpaired = unpair.deviceId === home.deviceId;
  env.step('FF10：被撤銷的 HOME 解除本機配對', unpair.deviceId === home.deviceId,
    `unpair：裝置 id ${unpair.deviceId === home.deviceId ? '相同' : unpair.deviceId}；遠端操作紀錄匯出 ${unpair.remoteAudit?.exists ? `${unpair.remoteAudit.lines} 行` : '沒有'}`, { unpair });

  // 8. 頁面乾淨（故意的 409 除外）
  outcome.page = await pageCleanStep(env, [P], 'FF10：頁面乾淨（主控台、回應、CSP、對話框、XSS 計數）');
  return outcome;
}
