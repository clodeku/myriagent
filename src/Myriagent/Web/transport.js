// 傳輸抽象（WP3.5，設計 4.9.1；契約見 docs/web-protocol.md 第六節第 13 點）：網頁↔宿主的訊息走哪一條路。
// - webview：桌面的 WebView2（window.chrome.webview），與換傳輸層之前完全相同。
// - hub：瀏覽器版（伺服器在 index.html 插入的 transport-hub.js 掛 window.myriagentHubTransport，version 1 而且 ok 為 true）。
// - none：兩者都沒有（直接開檔、伺服器沒插腳本）：網頁顯示「要在 Clode Myriagent 裡開啟」。
// 判斷只看這兩個全域，不看主機名（localhost 與介面探針下主機名會誤判）。
// 這個檔不得有任何連線程式與 hub 程式庫的名稱：桌面也帶這個檔，「未配對不載入同步元件」在網頁的對應（WebTransportFileTests 把關）。

const wv = window.chrome && window.chrome.webview;
const hubTransport = window.myriagentHubTransport;
const hub = !wv && !!hubTransport && typeof hubTransport === 'object' && hubTransport.version === 1 && hubTransport.ok === true ? hubTransport : null;

/** 'webview' | 'hub' | 'none' */
export const kind = wv ? 'webview' : hub ? 'hub' : 'none';
/** 有宿主可以收送（kind 不是 none） */
export const available = kind !== 'none';

/**
 * 送指令給宿主。〔遠端〕瀏覽器版回傳這一筆的 commandId（對應 commandResult）或 null（沒有送出：ready、在瀏覽器裡處理、
 * 沒有連線、太大）；桌面回指令自己帶的 commandId（桌面遠端操作，store.js 帶上的），沒帶是 undefined
 */
export function post(msg) {
  if (wv) {
    wv.postMessage(msg);
    return msg && typeof msg.commandId === 'string' && msg.commandId ? msg.commandId : undefined;
  }
  if (hub) return hub.post(msg);
  return undefined;
}

/** 〔遠端〕瀏覽器版產生一個 ULID（例如上傳的 uploadId）；其他傳輸是 null */
export function newId() {
  return hub && hub.rules && typeof hub.rules.ulid === 'function' ? hub.rules.ulid(Date.now()) : null;
}

/** 附 File 物件送給宿主（拖放／貼上檔案）；瀏覽器版不走這裡（上傳是 hubapi.js 的 uploadFile，E4），回 false */
export function postWithFiles(msg, files) {
  if (wv && typeof wv.postMessageWithAdditionalObjects === 'function') {
    wv.postMessageWithAdditionalObjects(msg, files);
    return true;
  }
  return false;
}

/** 收宿主訊息；可以註冊多個訂閱者（依註冊順序收到） */
export function onMessage(fn) {
  if (wv) wv.addEventListener('message', (e) => fn(e.data));
  else if (hub) hub.onMessage(fn);
}

/** 瀏覽器版的連線狀態（唯讀副本）；其他傳輸是 null */
export function status() {
  return hub && typeof hub.status === 'function' ? hub.status() : null;
}

/** 瀏覽器版：取消等待中的重連、立刻再試一次；其他傳輸不做事 */
export function retryNow() {
  if (hub && typeof hub.retryNow === 'function') hub.retryNow();
}
