// 自動化瀏覽器一律不出聲（2026-10-23）：Playwright 帶的 --mute-audio 只靜音網頁自己播的聲音，網頁的語音合成（speechSynthesis）
// 在 Windows 由瀏覽器交給系統的語音引擎念，照樣從這台電腦的喇叭出來。假裝置一完成對話，伺服器就對瀏覽器送 speak，
// 測試的網頁用涵涵念出來，和桌面 app 的雅婷疊在一起（使用者聽到「兩組聲音」，查到的是 remote-control.mjs 開的 Chrome）。
// 工具的瀏覽器內容一律經 newContext 建立（test/quiet-browser.test.mjs 掃 tools/hub-e2e 有沒有直接呼叫 browser.newContext）：
// 載入任何頁面之前把語音合成的 speak、cancel 換成不出聲的版本，照樣依序回報開始與念完，網頁的流程不變。
// harness.mjs 另掛 ui-probe 的 probeInit（假的語音合成，會記下念了什麼），兩個一起掛也不衝突。

/**
 * 在頁面裡執行（addInitScript）：speak 不交給系統的語音引擎，10 毫秒後送 start、再 30 毫秒後送 end；
 * cancel 對還沒念完的送 error（interrupted，同瀏覽器取消時的行為）。沒有語音合成的環境不動
 */
export function quietSpeech() {
  const g = globalThis;
  const synth = g.speechSynthesis;
  if (!synth || typeof synth.speak !== 'function') return;
  const live = new Map(); // 還沒念完的 utterance → 計時器
  const fire = (u, type) => {
    let ev;
    try {
      ev = type === 'error' && typeof g.SpeechSynthesisErrorEvent === 'function'
        ? new g.SpeechSynthesisErrorEvent('error', { utterance: u, error: 'interrupted' })
        : new Event(type);
    } catch {
      ev = new Event(type);
    }
    if (type === 'error' && !('error' in ev)) Object.defineProperty(ev, 'error', { value: 'interrupted' });
    u.dispatchEvent(ev);
  };
  const speak = function (u) {
    if (!u || typeof u.dispatchEvent !== 'function') throw new TypeError('speechSynthesis.speak 要一個 SpeechSynthesisUtterance');
    live.set(u, setTimeout(() => {
      fire(u, 'start');
      live.set(u, setTimeout(() => {
        live.delete(u);
        fire(u, 'end');
      }, 30));
    }, 10));
  };
  const cancel = function () {
    const pending = [...live.entries()];
    live.clear();
    for (const [u, timer] of pending) {
      clearTimeout(timer);
      fire(u, 'error');
    }
  };
  Object.defineProperty(synth, 'speak', { configurable: true, writable: true, value: speak });
  Object.defineProperty(synth, 'cancel', { configurable: true, writable: true, value: cancel });
}

/** 建立瀏覽器內容（參數同 browser.newContext），先掛上 quietSpeech 再回傳 */
export async function newContext(browser, options) {
  const context = await browser.newContext(options);
  await context.addInitScript(quietSpeech);
  return context;
}
