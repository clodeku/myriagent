// lib/delay-proxy.mjs 的單元測試（node:test）：對本機的純 TCP echo／sink 伺服器（127.0.0.1、系統挑的埠），不碰 TLS 與 MyriagentHub
import { test } from 'node:test';
import assert from 'node:assert/strict';
import net from 'node:net';
import { startDelayProxy } from '../lib/delay-proxy.mjs';

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/** 收到什麼就回什麼（prefix 不空時每個區塊前面加上它，分得出是哪一台） */
function startEcho(prefix = '') {
  const server = net.createServer((s) => {
    s.on('data', (d) => s.write(prefix ? Buffer.concat([Buffer.from(prefix), d]) : d));
    s.on('error', () => {});
  });
  return new Promise((resolve) => server.listen(0, '127.0.0.1', () => resolve({ port: server.address().port, close: () => new Promise((r) => server.close(() => r())) })));
}

/** 只收不回，記下收到的位元組與每次 data 的時間 */
function startSink() {
  const received = [];
  const sockets = new Set();
  const server = net.createServer((s) => {
    sockets.add(s);
    s.on('data', (d) => received.push({ at: performance.now(), data: d }));
    s.on('error', () => {});
  });
  return new Promise((resolve) => server.listen(0, '127.0.0.1', () => resolve({
    port: server.address().port,
    received,
    text: () => Buffer.concat(received.map((r) => r.data)).toString('utf8'),
    close: () => {
      for (const s of sockets) s.destroy();
      return new Promise((r) => server.close(() => r()));
    },
  })));
}

function connect(port) {
  return new Promise((resolve, reject) => {
    const s = net.connect({ host: '127.0.0.1', port }, () => resolve(s));
    s.once('error', reject);
  });
}

/** 寫一段、等到收齊同樣長度的回應：回傳 { text, ms } */
function roundTrip(socket, payload) {
  return new Promise((resolve) => {
    const started = performance.now();
    let got = '';
    const onData = (d) => {
      got += d.toString('utf8');
      if (got.length >= payload.length) {
        socket.off('data', onData);
        resolve({ text: got, ms: performance.now() - started });
      }
    };
    socket.on('data', onData);
    socket.write(payload);
  });
}

test('延遲代理_往返至少兩倍延遲而且不超過加50毫秒', async () => {
  const echo = await startEcho();
  const proxy = await startDelayProxy({ upstreamPort: echo.port, delayMs: 20 });
  const s = await connect(proxy.port);
  try {
    const samples = [];
    for (let i = 0; i < 7; i++) {
      const r = await roundTrip(s, `ping-${i}`);
      assert.equal(r.text, `ping-${i}`);
      samples.push(r.ms);
    }
    for (const ms of samples) assert.ok(ms >= 40, `往返 ${ms.toFixed(1)} ms 少於 40 ms`);
    // 上限看中位數（機器忙時單次計時器可能晚到）
    const median = [...samples].sort((a, b) => a - b)[Math.floor(samples.length / 2)];
    assert.ok(median < 40 + 50, `往返中位數 ${median.toFixed(1)} ms 超過 90 ms（${samples.map((v) => v.toFixed(1)).join(', ')}）`);
    const { lag } = proxy.stats();
    assert.equal(lag.up.count, 7);
    assert.equal(lag.down.count, 7);
    assert.ok(lag.up.meanMs >= 20 && lag.down.meanMs >= 20, JSON.stringify(lag));
    console.log(`往返 ${samples.map((v) => v.toFixed(1)).join(', ')} ms；實際延遲 ${JSON.stringify(lag)}`);
  } finally {
    s.destroy();
    await proxy.close();
    await echo.close();
  }
});

test('延遲代理_1000個區塊順序不變', async () => {
  const echo = await startEcho();
  const proxy = await startDelayProxy({ upstreamPort: echo.port, delayMs: 5 });
  const s = await connect(proxy.port);
  try {
    let expected = '';
    let got = '';
    s.on('data', (d) => { got += d.toString('utf8'); });
    for (let i = 0; i < 1000; i++) {
      const line = `${i}\n`;
      expected += line;
      s.write(line);
      if (i % 50 === 0) await new Promise((r) => setImmediate(r)); // 讓代理收到很多個區塊，不是一整塊
    }
    const started = Date.now();
    while (got.length < expected.length && Date.now() - started < 10000) await sleep(10);
    assert.equal(got, expected);
    assert.ok(proxy.stats().chunksUp > 1, '代理收到的上行區塊要不只一個');
  } finally {
    s.destroy();
    await proxy.close();
    await echo.close();
  }
});

test('延遲代理_setUpstream之後新連線連新埠_舊連線不換', async () => {
  const a = await startEcho('A:');
  const b = await startEcho('B:');
  const proxy = await startDelayProxy({ upstreamPort: a.port, delayMs: 1 });
  const first = await connect(proxy.port);
  let second = null;
  try {
    assert.equal((await roundTrip(first, 'x')).text.slice(0, 3), 'A:x');
    proxy.setUpstream(b.port);
    assert.equal(proxy.stats().upstream, b.port);
    second = await connect(proxy.port);
    assert.equal((await roundTrip(second, 'y')).text.slice(0, 3), 'B:y');
    assert.equal((await roundTrip(first, 'z')).text.slice(0, 3), 'A:z');
    assert.throws(() => proxy.setUpstream(0), /不合格/);
  } finally {
    first.destroy();
    second?.destroy();
    await proxy.close();
    await a.close();
    await b.close();
  }
});

test('延遲代理_hold上行期間上游收不到_release後一次收到全部', async () => {
  const sink = await startSink();
  const proxy = await startDelayProxy({ upstreamPort: sink.port, delayMs: 5 });
  const s = await connect(proxy.port);
  try {
    proxy.hold('up');
    s.write('one;');
    await sleep(20);
    s.write('two;');
    await sleep(20);
    s.write('three;');
    await sleep(150);
    assert.equal(sink.text(), '', 'hold 期間上游不該收到任何位元組');
    assert.ok(proxy.stats().queuedUp >= 1);
    assert.deepEqual(proxy.stats().held, { up: true, down: false });
    const releasedAt = performance.now();
    proxy.release('up');
    const started = Date.now();
    while (sink.text().length < 'one;two;three;'.length && Date.now() - started < 5000) await sleep(5);
    assert.equal(sink.text(), 'one;two;three;');
    // 「一次收到」：三段都在 release 之後的同一小段時間內到（早就到期，不再各等一次延遲）
    const last = sink.received[sink.received.length - 1].at;
    assert.ok(last - releasedAt < 50, `release 之後 ${(last - releasedAt).toFixed(1)} ms 才收齊`);
    assert.equal(proxy.stats().queuedUp, 0);
  } finally {
    s.destroy();
    await proxy.close();
    await sink.close();
  }
});

test('延遲代理_hold下行只擋伺服器到用戶端', async () => {
  const echo = await startEcho();
  const proxy = await startDelayProxy({ upstreamPort: echo.port, delayMs: 1 });
  const s = await connect(proxy.port);
  try {
    let got = '';
    s.on('data', (d) => { got += d.toString('utf8'); });
    proxy.hold('down');
    s.write('hello');
    await sleep(100);
    assert.equal(got, '');
    assert.equal(proxy.stats().bytesUp, 5, '上行照常轉送');
    proxy.release('both');
    const started = Date.now();
    while (got.length < 5 && Date.now() - started < 5000) await sleep(5);
    assert.equal(got, 'hello');
    assert.throws(() => proxy.hold('sideways'), /方向/);
  } finally {
    s.destroy();
    await proxy.close();
    await echo.close();
  }
});

test('延遲代理_只聽127點0點0點1', async () => {
  assert.throws(() => startDelayProxy({ upstreamPort: 1, host: '0.0.0.0' }), /127\.0\.0\.1/);
  assert.throws(() => startDelayProxy({}), /上游/);
});
