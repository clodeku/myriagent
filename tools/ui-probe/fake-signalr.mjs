// 假的瀏覽器 hub（WP3.5 的 remote-probe.mjs 用）：以 Playwright 的 routeWebSocket 在頁內攔下 wss://…/hubs/v1/browser，
// 照 SignalR 的 JSON 文字協定回應（docs/web-protocol.md 第六節；伺服器是 src/MyriagentHub/Realtime/BrowserHub.cs）。
// - 握手：收到 {"protocol":"json","version":1}＋0x1E 回 {}＋0x1E；之後每 10 秒送 {"type":6}（用戶端的伺服器逾時是 30 秒）。
// - 呼叫（type 1）：Cmd 回 type 3 的 result {accepted, commandId?, error?}（null 欄位省略）；ClientState 回沒有 result 的 type 3。
//   ready 被接受之後推 init（type 1、target Msg）；init 的內容由 onReady 決定（預設照建構時給的 init 函式）。
// - 推送 push(msg)、伺服器關閉 closeWithError(text)（type 7 帶 error）、直接斷線 drop()、下一個某指令被拒 rejectNext、
//   ClientState 丟錯 rejectClientState、收到 ready 不回 init holdInit(ms)、下 n 條 WebSocket 一直停在 CONNECTING hangNext(n)、
//   〔WP4.6〕某個指令的結果與回推的訊息 onCmd(name, fn)（例如 open 回推 conversation）。
// - 每一框都記在 frames（方向、時間、第幾條連線、解析後的物件）。
// routeWebSocket 是頁內模擬：頁面的 CSP（connect-src 的 wss）與真網路測不到，那兩項由 tools/hub-e2e 對真伺服器驗。

export const RS = String.fromCharCode(0x1e);
export const PING_MS = 10000;

export class FakeSignalRHub {
  /**
   * @param {{ url: string, init: (ready: object, conn: object) => object|null, log?: string[] }} o
   */
  constructor(o) {
    this.url = o.url;
    this.initFor = o.init;
    this.log = o.log || [];
    this.connections = [];
    this.frames = [];
    this.rejects = [];
    this.clientStateErrors = [];
    this.hold = 0;
    this.hangs = 0;
    this.readyHandler = null;
    this.seq = 0;
  }

  /** 掛到 BrowserContext（或 Page）上；之後頁面開的每一條 WebSocket 都是一條假連線 */
  async attach(target) {
    await target.routeWebSocket(this.url, (ws) => this.accept(ws));
  }

  accept(ws) {
    const conn = { id: ++this.seq, ws, openedAt: Date.now(), handshakeAt: null, closedAt: null, closeCode: null, pings: [], timer: null, open: true, hung: false };
    this.connections.push(conn);
    if (this.hangs > 0) {
      // 路由的處理函式不結束，Playwright 就不替頁面完成開啟：頁面的 WebSocket 一直停在 CONNECTING（像伺服器收了 TCP、不回 101）。
      // 頁面在 CONNECTING 時關掉它：照真的瀏覽器回一個 close 事件給頁面（程式庫的 start() 才會失敗）
      this.hangs--;
      conn.hung = true;
      return new Promise((resolve) => {
        ws.onClose((code, reason) => {
          if (!conn.open) return;
          this.closed(conn, code, reason);
          try {
            ws.close({ code: 1006, reason: 'probe: closed while connecting' });
          } catch {
            // 頁面已經關了
          }
          resolve();
        });
      });
    }
    ws.onMessage((data) => this.receive(conn, data));
    ws.onClose((code, reason) => this.closed(conn, code, reason));
    return undefined;
  }

  closed(conn, code, reason) {
    if (!conn.open) return;
    conn.open = false;
    conn.closedAt = Date.now();
    conn.closeCode = code ?? null;
    conn.closeReason = reason ?? null;
    clearInterval(conn.timer);
    this.frames.push({ dir: 'close', at: conn.closedAt, conn: conn.id, code: conn.closeCode });
  }

  receive(conn, data) {
    const text = typeof data === 'string' ? data : Buffer.from(data).toString('utf8');
    for (const part of text.split(RS)) {
      if (!part) continue;
      let obj;
      try {
        obj = JSON.parse(part);
      } catch {
        this.log.push(`[假 hub] 第 ${conn.id} 條：看不懂的框 ${part.slice(0, 120)}`);
        continue;
      }
      this.frames.push({ dir: 'in', at: Date.now(), conn: conn.id, obj, bytes: Buffer.byteLength(part, 'utf8') + 1 });
      if (conn.handshakeAt === null) {
        this.handshake(conn, obj);
        continue;
      }
      this.message(conn, obj);
    }
  }

  handshake(conn, obj) {
    if (obj.protocol !== 'json' || obj.version !== 1) {
      this.sendRaw(conn, { error: 'unsupported protocol' });
      conn.ws.close({ code: 1000 });
      return;
    }
    conn.handshakeAt = Date.now();
    this.sendRaw(conn, {});
    conn.timer = setInterval(() => {
      if (!conn.open) return;
      conn.pings.push(Date.now());
      this.sendRaw(conn, { type: 6 });
    }, PING_MS);
  }

  message(conn, m) {
    if (m.type !== 1) return; // ping（6）、用戶端的關閉（7）只記錄
    if (m.target === 'Cmd') {
      const arg = m.arguments && m.arguments[0];
      const cmd = arg && arg.cmd;
      const name = cmd && cmd.cmd;
      const result = { accepted: true };
      if (arg && typeof arg.commandId === 'string') result.commandId = arg.commandId;
      const i = this.rejects.findIndex((r) => r.cmd === name);
      if (i >= 0) {
        const r = this.rejects[i];
        if (--r.times <= 0) this.rejects.splice(i, 1);
        result.accepted = false;
        result.error = r.error;
      }
      const reply = result.accepted && name !== 'ready' ? this.cmdReply(conn, name, cmd, arg, result) : null;
      if (m.invocationId !== undefined) this.sendRaw(conn, { type: 3, invocationId: m.invocationId, result });
      if (name === 'ready' && result.accepted) this.ready(conn, cmd);
      if (reply) this.pushReply(conn, reply);
    } else if (m.target === 'ClientState') {
      const err = this.clientStateErrors.shift();
      if (m.invocationId === undefined) return;
      if (err) this.sendRaw(conn, { type: 3, invocationId: m.invocationId, error: `An unexpected error occurred invoking 'ClientState' on the server. HubException: ${err}` });
      else this.sendRaw(conn, { type: 3, invocationId: m.invocationId });
    } else if (m.invocationId !== undefined) {
      this.sendRaw(conn, { type: 3, invocationId: m.invocationId, error: `Unknown hub method '${m.target}'` });
    }
  }

  ready(conn, cmd) {
    const make = () => {
      if (!conn.open) return;
      const init = this.readyHandler ? this.readyHandler(cmd, conn) : this.initFor(cmd, conn);
      if (init) this.sendMsg(conn, init);
    };
    const hold = this.hold;
    this.hold = 0;
    if (hold === Infinity) return;
    if (hold > 0) setTimeout(make, hold);
    else make();
  }

  sendRaw(conn, obj) {
    if (!conn.open) return false;
    this.frames.push({ dir: 'out', at: Date.now(), conn: conn.id, obj });
    try {
      conn.ws.send(JSON.stringify(obj) + RS);
      return true;
    } catch (e) {
      this.log.push(`[假 hub] 第 ${conn.id} 條送不出去：${e.message}`);
      return false;
    }
  }

  sendMsg(conn, msg) {
    return this.sendRaw(conn, { type: 1, target: 'Msg', arguments: [msg] });
  }

  /** 目前的連線：最後一條握手過、還開著的 */
  current() {
    for (let i = this.connections.length - 1; i >= 0; i--) {
      const c = this.connections[i];
      if (c.open && c.handshakeAt !== null) return c;
    }
    return null;
  }

  /** 推一則宿主訊息給目前的連線 */
  push(msg) {
    const c = this.current();
    return c ? this.sendMsg(c, msg) : false;
  }

  /** 伺服器帶錯誤關閉（type 7），例如 'Connection closed with an error. HubException: too_many_connections' */
  closeWithError(text) {
    const c = this.current();
    if (!c) return false;
    this.sendRaw(c, { type: 7, error: text });
    // 用戶端收到 type 7 會自己關；保險起見 1 秒後還開著就由這邊關
    setTimeout(() => { if (c.open) c.ws.close({ code: 1000 }); }, 1000);
    return true;
  }

  /** 直接斷線（不送 type 7），像網路中斷或伺服器程序結束 */
  drop() {
    let n = 0;
    for (const c of this.connections) {
      if (!c.open) continue;
      c.ws.close({ code: 4000, reason: 'probe drop' });
      n++;
    }
    return n;
  }

  /** 下 times 個 cmd.cmd === cmdName 的 Cmd 回 {accepted:false, error} */
  rejectNext(cmdName, error, times = 1) {
    this.rejects.push({ cmd: cmdName, error, times });
  }

  /** 下一個 ClientState 丟 HubException: code */
  rejectClientState(code) {
    this.clientStateErrors.push(code);
  }

  /** 下 n 條 WebSocket 一直停在 CONNECTING（不完成開啟握手）；頁面關掉時才結束 */
  hangNext(n = 1) {
    this.hangs += n;
  }

  /** 下一個 ready 晚 ms 才回 init（Infinity＝不回） */
  holdInit(ms) {
    this.hold = ms;
  }

  /** 換掉 ready 的處理：fn(readyCmd, conn) 回 init（或 null＝不回） */
  onReady(fn) {
    this.readyHandler = fn;
  }

  /** 頁面送來的呼叫（type 1）；target 可給 'Cmd'、'ClientState' */
  invocations(target) {
    return this.frames.filter((f) => f.dir === 'in' && f.obj.type === 1 && (!target || f.obj.target === target));
  }

  /** 頁面送來的 Cmd 裡 cmd.cmd === name 的參數（{commandId, cmd}）；name 不給就全部 */
  cmds(name) {
    return this.invocations('Cmd').map((f) => Object.assign({ at: f.at, conn: f.conn }, f.obj.arguments[0])).filter((a) => !name || (a.cmd && a.cmd.cmd === name));
  }

  /**
   * 〔WP4.6〕登記某個指令（cmd.cmd === name；ready 除外）的處理：fn(cmd, arg, conn) 回 null／undefined 時照預設（accepted、不推任何訊息），
   * 否則回 {error?, push?, delayMs?}：error 是字串時 Cmd 回 {accepted:false, error}；push 是要在 Cmd 的結果之後推給同一條連線的訊息陣列
   * （delayMs 毫秒後才推）。fn 給 null 時取消登記。rejectNext 先比對：被它擋下的那一次不呼叫 fn。沒登記任何處理時行為和之前逐位元組相同
   */
  onCmd(name, fn) {
    if (!this.cmdHandlers) this.cmdHandlers = new Map();
    if (typeof fn === 'function') this.cmdHandlers.set(name, fn);
    else this.cmdHandlers.delete(name);
  }

  /** onCmd 的分派：改寫 result（被拒時），回要推的訊息（沒有回 null）；處理函式丟例外時記進 log、照預設 */
  cmdReply(conn, name, cmd, arg, result) {
    const fn = this.cmdHandlers && this.cmdHandlers.get(name);
    if (!fn) return null;
    let out;
    try {
      out = fn(cmd, arg, conn);
    } catch (e) {
      this.log.push(`[假 hub] onCmd(${name}) 丟出例外：${(e && e.message) || e}`);
      return null;
    }
    if (!out || typeof out !== 'object') return null;
    if (typeof out.error === 'string') {
      result.accepted = false;
      result.error = out.error;
    }
    return Array.isArray(out.push) && out.push.length ? { push: out.push, delayMs: Number(out.delayMs) || 0 } : null;
  }

  pushReply(conn, reply) {
    const send = () => { for (const msg of reply.push) this.sendMsg(conn, msg); };
    if (reply.delayMs > 0) setTimeout(send, reply.delayMs);
    else send();
  }

  stop() {
    for (const c of this.connections) clearInterval(c.timer);
  }
}
