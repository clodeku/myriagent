// 假的麥克風、錄音器與系統語音辨識（輸入區的語音訊息，Web/voicemsg.js）：probe.mjs 與 hub-page.mjs 以 page.addInitScript(fakeMic) 注入，
// 不碰這台真的麥克風、不連語音服務（語音辨識的 window.__sr 見檔尾）。window.__mic 控制與記錄：
// - deny：getUserMedia 要丟的錯誤名稱（例如 'NotAllowedError'；null＝給假的串流）；requests：要了幾次；constraints：最後一次的條件
// - bytes：錄音器停止時給的資料大小（0＝空的）；recorders：每個錄音器（mimeType、opts、state、started、stopped）
// - tracksStopped：串流的軌道被停掉幾次（送出或取消後要關掉麥克風）
// - tone：null＝給假的串流物件（音量表拿不到音訊、不顯示）；數字＝給真的音訊串流（正弦波振盪器，振幅就是這個數），
//   錄音音量表讀得到真的音量；toneGain 是那個增益節點（改 gain.value 模擬變大聲、安靜）
export function fakeMic() {
  window.__mic = { deny: null, bytes: 4096, requests: 0, constraints: null, recorders: [], tracksStopped: 0, tone: null, toneGain: null };
  const toneStream = (amp) => {
    const ac = new AudioContext();
    const osc = ac.createOscillator();
    const g = ac.createGain();
    g.gain.value = amp;
    const dest = ac.createMediaStreamDestination();
    osc.connect(g);
    g.connect(dest);
    osc.start();
    window.__mic.toneGain = g;
    const s = dest.stream;
    const tracks = s.getTracks();
    s.getTracks = () => tracks.map((t) => ({ kind: t.kind, stop() { window.__mic.tracksStopped++; t.stop(); } }));
    return s;
  };
  const stream = () => ({ getTracks: () => [{ kind: 'audio', stop() { window.__mic.tracksStopped++; } }] });
  if (!navigator.mediaDevices) Object.defineProperty(navigator, 'mediaDevices', { configurable: true, value: {} });
  Object.defineProperty(navigator.mediaDevices, 'getUserMedia', {
    configurable: true,
    value: async (constraints) => {
      window.__mic.requests++;
      window.__mic.constraints = constraints;
      if (window.__mic.deny) {
        const e = new Error('假的麥克風錯誤');
        e.name = window.__mic.deny;
        throw e;
      }
      return window.__mic.tone != null ? toneStream(window.__mic.tone) : stream();
    },
  });
  function FakeRecorder(s, opts) {
    this.stream = s;
    this.opts = opts || null;
    this.mimeType = (opts && opts.mimeType) || 'audio/webm';
    this.state = 'inactive';
    this.started = 0;
    this.stopped = 0;
    this.ondataavailable = null;
    this.onstop = null;
    window.__mic.recorders.push(this);
  }
  FakeRecorder.isTypeSupported = (m) => /^audio\/webm/.test(m);
  FakeRecorder.prototype.start = function () {
    this.state = 'recording';
    this.started++;
  };
  FakeRecorder.prototype.stop = function () {
    this.state = 'inactive';
    this.stopped++;
    setTimeout(() => {
      if (this.ondataavailable && window.__mic.bytes > 0) this.ondataavailable({ data: new Blob([new Uint8Array(window.__mic.bytes)], { type: this.mimeType }) });
      if (this.onstop) this.onstop({});
    }, 5);
  };
  window.MediaRecorder = FakeRecorder;

  // 假的系統語音辨識（手機與平板的聽寫，Web Speech API）：不連 Google／Apple 的語音服務。window.__sr 控制與記錄：
  // - instances：每個辨識器（lang、continuous、interimResults、started、stopped、aborted、createdAt、startedAt、live、deaf、onstart 發了沒〔began〕）
  // - emit([[文字, 是否確定], …], i)：第 i 個（沒給＝最後一個）辨識器的 onresult 收到這份完整的結果（連續辨識時是開始以來的每一段，同瀏覽器）；
  //   聽不到的（deaf）不給結果、記進 dropped
  // - fail(code)：最後一個辨識器的 onerror（error 是 code）再 onend
  // - end(i)：第 i 個（沒給＝最後一個）辨識器自己結束（沒有錯誤，同 Android 說完一段）
  // - stopDelay：stop() 之後幾毫秒才 end（預設 5；手機按停之後要等最後一段）；abort() 一律 5 毫秒
  // - exclusive：true＝同時只能一個在聽（手機的語音服務）：前一個還沒 end 就開始的新辨識器聽不到（不發 onstart、不給結果）——按了講話沒反應
  // - silent：true＝之後開始的辨識器什麼事件都不發（卡住）
  // - mute：true＝之後開始的辨識器照樣發 onstart／onaudiostart，卻收不到聲音（不給結果、不發錯誤或結束；iOS 播放過聲音之後，WebKit bug 321436）
  // - freshDeaf：true＝第一個以外新建的辨識器開始後同 mute（iOS 27.2 的回報：開 app 之後只有第一個新建的收得到字）
  // - start() 在辨識器還在聽（start 了、還沒 end）時丟 InvalidStateError（同瀏覽器）
  window.__sr = { instances: [], dropped: 0, stopDelay: 5, exclusive: false, silent: false, mute: false, freshDeaf: false };
  const last = () => window.__sr.instances[window.__sr.instances.length - 1] || null;
  const pick = (i) => (Number.isInteger(i) ? window.__sr.instances[i] || null : last());
  const ended = (r, ms = 5) => setTimeout(() => {
    if (!r.live) return;
    r.live = false;
    if (r.onend) r.onend({});
  }, ms);
  function FakeRecognition() {
    this.lang = '';
    this.continuous = false;
    this.interimResults = false;
    this.maxAlternatives = 1;
    this.started = 0;
    this.stopped = 0;
    this.aborted = 0;
    this.createdAt = Date.now();
    this.startedAt = 0;
    this.live = false;
    this.deaf = false;
    this.mute = false;
    this.began = false;
    this.onstart = null;
    this.onaudiostart = null;
    this.onresult = null;
    this.onerror = null;
    this.onend = null;
    window.__sr.instances.push(this);
  }
  FakeRecognition.prototype.start = function () {
    if (this.live) throw new DOMException('recognition has already started', 'InvalidStateError');
    this.started++;
    this.startedAt = Date.now();
    this.deaf = window.__sr.silent || (window.__sr.exclusive && window.__sr.instances.some((x) => x !== this && x.live));
    this.mute = window.__sr.mute || (window.__sr.freshDeaf && window.__sr.instances.indexOf(this) > 0);
    this.live = true;
    if (this.deaf) return;
    setTimeout(() => {
      if (!this.live) return;
      this.began = true;
      if (this.onstart) this.onstart({});
      if (this.onaudiostart) this.onaudiostart({});
    }, 5);
  };
  FakeRecognition.prototype.stop = function () {
    this.stopped++;
    if (!window.__sr.silent) ended(this, window.__sr.stopDelay);
  };
  FakeRecognition.prototype.abort = function () {
    this.aborted++;
    if (!window.__sr.silent) ended(this);
  };
  window.__sr.emit = (list, i) => {
    const r = pick(i);
    if (r && (r.deaf || r.mute)) {
      window.__sr.dropped++;
      return;
    }
    const results = list.map(([text, isFinal]) => Object.assign([{ transcript: text, confidence: 0.9 }], { isFinal: !!isFinal }));
    if (r && r.onresult) r.onresult({ results, resultIndex: 0 });
  };
  window.__sr.fail = (code) => {
    const r = last();
    if (r && r.onerror) r.onerror({ error: code });
    if (r) ended(r);
  };
  window.__sr.end = (i) => {
    const r = pick(i);
    if (r) ended(r);
  };
  window.SpeechRecognition = FakeRecognition;
  window.webkitSpeechRecognition = FakeRecognition;
}
