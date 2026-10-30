// 延遲代理（WP4.9 步 7、計畫 D9）：TLS 直通的 TCP 代理（不解 TLS，SPKI 不變），每個方向各自延遲（預設 20 ms）、各自一條佇列、保持順序；
// 只聽 127.0.0.1。執行中可以 setUpstream（換伺服器不換網址：FF9 換舊版伺服器時前端的網址、Hub:PublicOrigin 都不變），
// hold('up' | 'down' | 'both') 暫停某個方向的轉送（保證切換或強制結束時有在途批次）、release 之後依序一次送出。
// up＝用戶端→伺服器，down＝伺服器→用戶端。
import net from 'node:net';

const DIRECTIONS = ['up', 'down'];

function directionsOf(direction) {
  if (direction === 'both') return DIRECTIONS;
  if (!DIRECTIONS.includes(direction)) throw new Error(`方向只能是 up、down 或 both，收到 ${direction}`);
  return [direction];
}

/**
 * 啟動代理：{ port, setUpstream(port), hold(direction), release(direction), stats(), close() }。
 * 新的連線一律連「目前的」上游；已經建立的連線不換（伺服器換掉時它們會被上游關掉）
 */
export function startDelayProxy({ upstreamPort, delayMs = 20, host = '127.0.0.1' } = {}) {
  if (host !== '127.0.0.1') throw new Error('延遲代理只聽 127.0.0.1');
  if (!Number.isInteger(upstreamPort) || upstreamPort <= 0) throw new Error('要有上游的埠');
  let upstream = upstreamPort;
  const held = { up: false, down: false };
  const totals = { connections: 0, open: 0, bytesUp: 0, bytesDown: 0, chunksUp: 0, chunksDown: 0 };
  // 實際加上的延遲（寫出時間減收到時間；含 hold 的時間）：Windows 的計時器解析度預設約 15.6 毫秒，實際延遲常比 delayMs 長，報告照這個記
  const lag = { up: { count: 0, sumMs: 0, maxMs: 0 }, down: { count: 0, sumMs: 0, maxMs: 0 } };
  const pipes = new Set();

  /** 一個方向的轉送：佇列裡依序到期才寫；被 hold 時累積，release 時照到期時間依序送 */
  function makeLane(direction, target) {
    const queue = [];
    let timer = null;
    const lane = {
      push(chunk) {
        const now = performance.now();
        queue.push({ due: now + delayMs, at: now, chunk });
        if (direction === 'up') { totals.bytesUp += chunk.length; totals.chunksUp++; } else { totals.bytesDown += chunk.length; totals.chunksDown++; }
        lane.pump();
      },
      pump() {
        if (timer || held[direction]) return;
        while (queue.length > 0) {
          // performance.now（小於 1 毫秒的解析度）：Date.now 只到毫秒，到期判斷會提早最多 1 毫秒、延遲不足 delayMs
          const wait = queue[0].due - performance.now();
          if (wait > 0) {
            timer = setTimeout(() => { timer = null; lane.pump(); }, wait);
            return;
          }
          const { chunk, at } = queue.shift();
          if (!target.destroyed) target.write(chunk);
          const l = lag[direction];
          const ms = performance.now() - at;
          l.count++;
          l.sumMs += ms;
          if (ms > l.maxMs) l.maxMs = ms;
          if (held[direction]) return;
        }
        if (lane.ending && !target.destroyed) target.end();
      },
      ending: false,
      end() {
        lane.ending = true;
        lane.pump();
      },
      pending: () => queue.length,
      clear() {
        if (timer) clearTimeout(timer);
        timer = null;
        queue.length = 0;
      },
    };
    return lane;
  }

  // 兩邊都允許半關閉：一邊送 FIN 時，另一個方向佇列裡還沒到期的資料照樣送完才轉送 FIN（預設會立刻關掉整條、丟掉佇列）
  const server = net.createServer({ allowHalfOpen: true }, (client) => {
    totals.connections++;
    totals.open++;
    const remote = net.connect({ host: '127.0.0.1', port: upstream, allowHalfOpen: true });
    const up = makeLane('up', remote);
    const down = makeLane('down', client);
    const pipe = { client, remote, lanes: { up, down } };
    pipes.add(pipe);
    client.on('data', (chunk) => up.push(chunk));
    remote.on('data', (chunk) => down.push(chunk));
    client.on('end', () => up.end());
    remote.on('end', () => down.end());
    const done = () => {
      if (!pipes.delete(pipe)) return;
      totals.open--;
      up.clear();
      down.clear();
      client.destroy();
      remote.destroy();
    };
    client.on('close', done);
    remote.on('close', done);
    client.on('error', done);
    remote.on('error', done);
  });

  return new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, host, () => {
      server.off('error', reject);
      const { port } = server.address();
      resolve({
        port,
        /** 之後的新連線連這個埠 */
        setUpstream(next) {
          if (!Number.isInteger(next) || next <= 0) throw new Error('上游的埠不合格');
          upstream = next;
        },
        /** 暫停這個方向（up、down、both）的轉送：資料留在佇列裡 */
        hold(direction) {
          for (const d of directionsOf(direction)) held[d] = true;
        },
        /** 恢復轉送：佇列裡的依序送出 */
        release(direction) {
          for (const d of directionsOf(direction)) held[d] = false;
          for (const p of pipes) for (const d of directionsOf(direction)) p.lanes[d].pump();
        },
        stats() {
          let queuedUp = 0;
          let queuedDown = 0;
          for (const p of pipes) {
            queuedUp += p.lanes.up.pending();
            queuedDown += p.lanes.down.pending();
          }
          const lagOf = (l) => ({ count: l.count, meanMs: l.count === 0 ? null : Math.round((l.sumMs / l.count) * 10) / 10, maxMs: Math.round(l.maxMs * 10) / 10 });
          return { ...totals, upstream, delayMs, held: { ...held }, queuedUp, queuedDown, lag: { up: lagOf(lag.up), down: lagOf(lag.down) } };
        },
        /** 關掉代理與所有連線 */
        close() {
          for (const p of [...pipes]) {
            p.client.destroy();
            p.remote.destroy();
          }
          pipes.clear();
          return new Promise((r) => server.close(() => r()));
        },
      });
    });
  });
}
