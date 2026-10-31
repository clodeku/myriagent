// 輸入區語音訊息的純規則（src/Myriagent/Web/voicemsgrules.js）：錄音格式、檔名（同電腦端 VoiceMessageRules）、怎麼送、計時、能不能送、
// 麥克風錯誤與按鈕說明；手機與平板的系統語音辨識（認出手機平板、接到游標處、Android 重複的結果、改回錄音的錯誤）；
// voicemsgrules.js 沒有 import，voicemsg.js 沒有 HTML 寫入點、語音辨識只在瀏覽器版、不送 voiceTyping。
//   (cd tools/ui-probe && node --test voicemsgrules.test.mjs)
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const webDir = path.resolve(here, '..', '..', 'src', 'Myriagent', 'Web');
const source = readFileSync(path.join(webDir, 'voicemsgrules.js'), 'utf8');
const V = await import('data:text/javascript;base64,' + Buffer.from(source + '\n// voicemsgrules.test\n', 'utf8').toString('base64'));

test('錄音格式：照順序挑第一個支援的，都不支援交給瀏覽器；丟例外的當不支援', () => {
  assert.equal(V.pickMime((m) => m.startsWith('audio/webm')), 'audio/webm;codecs=opus');
  assert.equal(V.pickMime((m) => m === 'audio/mp4'), 'audio/mp4', 'Safari');
  assert.equal(V.pickMime(() => false), '');
  assert.equal(V.pickMime(() => { throw new Error('x'); }), '');
  assert.equal(V.pickMime(null), '');
});

test('副檔名同電腦端：WebM 存成 .weba、MP4 是 .m4a、Ogg、WAV；不認得回 null', () => {
  assert.equal(V.extensionFor('audio/webm;codecs=opus'), '.weba');
  assert.equal(V.extensionFor('AUDIO/MP4'), '.m4a');
  assert.equal(V.extensionFor('audio/ogg; codecs=opus'), '.ogg');
  assert.equal(V.extensionFor('audio/wav'), '.wav');
  assert.equal(V.extensionFor('video/x-matroska'), null);
  assert.equal(V.extensionFor(undefined), null);
});

test('檔名：語音訊息_yyyyMMdd_HHmmss（本機時間）', () => {
  assert.equal(V.voiceFileName(new Date(2026, 9, 23, 9, 5, 7), '.weba'), '語音訊息_20261023_090507.weba');
  assert.ok(V.voiceFileName(new Date(), '.m4a').startsWith(V.FILE_PREFIX));
});

test('怎麼送：手機平板用系統的語音辨識；不能錄音不顯示；上傳的對話是 remote；桌面這台的要有辨識程序', () => {
  assert.equal(V.voiceMode({ supported: false, remote: true, voiceInput: true }), null);
  assert.equal(V.voiceMode({ supported: true, remote: true, voiceInput: false }), 'remote', '瀏覽器版由對話所在的電腦辨識');
  assert.equal(V.voiceMode({ supported: true, remote: false, voiceInput: true }), 'local');
  assert.equal(V.voiceMode({ supported: true, remote: false, voiceInput: false }), null, '這台沒有辨識程序');
  assert.equal(V.voiceMode({ supported: true, remote: true, dictation: true }), 'dictation');
  assert.equal(V.voiceMode({ supported: false, remote: true, dictation: true }), 'dictation', '聽寫不用錄音器');
  assert.equal(V.voiceMode({ supported: true, remote: true, dictation: 1 }), 'remote', '只認 true');
  assert.equal(V.voiceMode(), null);
});

test('手機與平板：觸控為主、手機平板的使用者代理、iPadOS 自稱 Macintosh 但有多點觸控；桌機與觸控筆電不算', () => {
  const win = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36';
  const mac = 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15';
  assert.equal(V.isHandheld({ coarsePointer: true, userAgent: win, maxTouchPoints: 10 }), true, 'Windows 平板模式');
  assert.equal(V.isHandheld({ coarsePointer: false, userAgent: 'Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 Chrome/141.0 Mobile Safari/537.36' }), true);
  assert.equal(V.isHandheld({ coarsePointer: false, userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148' }), true);
  assert.equal(V.isHandheld({ coarsePointer: false, userAgent: mac, maxTouchPoints: 5 }), true, 'iPad 接了觸控板');
  assert.equal(V.isHandheld({ coarsePointer: false, userAgent: mac, maxTouchPoints: 0 }), false, 'Mac');
  assert.equal(V.isHandheld({ coarsePointer: false, userAgent: win, maxTouchPoints: 10 }), false, '觸控筆電（主要是滑鼠）');
  assert.equal(V.isHandheld(), false);
});

test('iPhone、iPad：使用者代理是 iPhone／iPad，或 iPadOS 自稱 Macintosh 但有多點觸控；Android、Mac、Windows 不算', () => {
  const mac = 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15';
  assert.equal(V.isAppleTouch({ userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 27_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148', maxTouchPoints: 5 }), true);
  assert.equal(V.isAppleTouch({ userAgent: 'Mozilla/5.0 (iPad; CPU OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148' }), true);
  assert.equal(V.isAppleTouch({ userAgent: mac, maxTouchPoints: 5 }), true, 'iPadOS 的 Safari');
  assert.equal(V.isAppleTouch({ userAgent: mac, maxTouchPoints: 0 }), false, 'Mac');
  assert.equal(V.isAppleTouch({ userAgent: 'Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 Chrome/141.0 Mobile Safari/537.36', maxTouchPoints: 5 }), false, 'Android');
  assert.equal(V.isAppleTouch({ userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/141.0 Safari/537.36', maxTouchPoints: 10 }), false);
  assert.equal(V.isAppleTouch(), false);
});

test('聽寫的字接到游標處：英數相鄰補空白、中文不補；沒有字時原樣', () => {
  assert.deepEqual(V.joinTranscript('請幫我', '整理報表', ''), { value: '請幫我整理報表', caret: 7 });
  assert.deepEqual(V.joinTranscript('open', 'README', 'now'), { value: 'open README now', caret: 12 });
  assert.deepEqual(V.joinTranscript('前', '', '後'), { value: '前後', caret: 1 });
  assert.deepEqual(V.joinTranscript(null, '你好', undefined), { value: '你好', caret: 2 });
});

test('辨識結果：確定的與還在變的分開；Android 越說越長的確定結果只留最長的', () => {
  const R = (t, f) => Object.assign([{ transcript: t }], { isFinal: f });
  assert.deepEqual(V.collectResults([R('整理', false)]), { finals: '', interim: '整理' });
  assert.deepEqual(V.collectResults([R('請幫我', true), R('整理報表', false)]), { finals: '請幫我', interim: '整理報表' });
  assert.deepEqual(V.collectResults([R('整理報表', true), R('然後寄出', true)]), { finals: '整理報表然後寄出', interim: '' });
  assert.deepEqual(V.collectResults([R('整理', true), R('整理報表', true), R('整理報表然後寄出', true)]), { finals: '整理報表然後寄出', interim: '' }, 'Android');
  assert.deepEqual(V.collectResults([R('好', true), R('好', true)]), { finals: '好好', interim: '' }, '一樣長的不合併');
  assert.deepEqual(V.collectResults(null), { finals: '', interim: '' });
  assert.deepEqual(V.collectResults([null, [{}]]), { finals: '', interim: '' });
});

test('iPhone／iPad 的重複：重送的確定結果、從頭到尾的確定結果、還在變的又從確定的開頭、同一段在一個結果裡給兩次，都只留一次', () => {
  const R = (t, f) => Object.assign([{ transcript: t }], { isFinal: f });
  assert.deepEqual(V.collectResults([R('整理報表', true), R('整理報表', true)]), { finals: '整理報表', interim: '' }, '重送同一段');
  assert.deepEqual(V.collectResults([R('整理報表', true), R('然後寄出', true), R('然後寄出', true)]), { finals: '整理報表然後寄出', interim: '' });
  assert.deepEqual(V.collectResults([R('整理報表', true), R('然後寄出', true), R('整理報表然後寄出給我', true)]), { finals: '整理報表然後寄出給我', interim: '' }, '從頭到尾');
  assert.deepEqual(V.collectResults([R('整理報表', true), R('整理報表然後', false)]), { finals: '整理報表', interim: '然後' }, '還在變的從確定的開頭');
  assert.deepEqual(V.collectResults([R('整理報表', true), R('報表', false)]), { finals: '整理報表', interim: '' }, '確定之後又以還在變的送一次');
  assert.deepEqual(V.collectResults([R('整理報表', true), R('表', false)]), { finals: '整理報表', interim: '表' }, '一個字的照接');
  assert.equal(V.undouble('幫我整理報表幫我整理報表'), '幫我整理報表');
  assert.equal(V.undouble('open the file open the file'), 'open the file');
  assert.equal(V.undouble('哈哈哈哈'), '哈哈哈哈');
  assert.equal(V.undouble('謝謝你謝謝你'), '謝謝你謝謝你', '一半不到 4 個字');
  assert.equal(V.undouble('好好好好好好好好'), '好好好好好好好好', '同一個字重複');
  assert.equal(V.undouble('整理報表'), '整理報表');
  assert.equal(V.undouble(null), '');
});

test('一次聽寫的累積：iPhone 停頓之後清掉重來時前面的字留著；又整段送回來不重複；改字、越說越長、空的結果不截斷', () => {
  const R = (t, f) => Object.assign([{ transcript: t }], { isFinal: f });
  const run = (events) => {
    let st = V.dictationStart();
    const out = [];
    for (const ev of events) {
      const r = V.dictationStep(st, ev);
      st = r.state;
      out.push(r.text);
    }
    return out;
  };
  // iOS：一個越說越長的結果，停頓之後清掉重來，最後停下時給從頭到尾的確定結果
  assert.deepEqual(run([
    [R('今天天氣', false)],
    [R('今天天氣很好', false)],
    [R('我們', false)],
    [R('我們去公園', false)],
    [R('', false)],
    [R('今天天氣很好我們去公園散步', true)],
  ]), ['今天天氣', '今天天氣很好', '今天天氣很好我們', '今天天氣很好我們去公園', '今天天氣很好我們去公園', '今天天氣很好我們去公園散步']);
  // 清掉重來的新一段剛好和前一段同一個字開頭
  assert.deepEqual(run([[R('今天天氣很好', false)], [R('今', false)], [R('今晚吃什麼', false)]]),
    ['今天天氣很好', '今天天氣很好今', '今天天氣很好今晚吃什麼']);
  // 清掉重來之後又把丟掉的那段重送一次再接著說
  assert.deepEqual(run([[R('今天天氣很好', false)], [R('我們', false)], [R('今天天氣很好', false)], [R('今天天氣很好我們去', false)]]),
    ['今天天氣很好', '今天天氣很好我們', '今天天氣很好', '今天天氣很好我們去']);
  // 只是改字、補標點、越說越長：不是清掉重來
  assert.deepEqual(run([[R('今天天器', false)], [R('今天天氣很好', false)], [R('今天天氣很好，', false)], [R('今天天氣', false)]]),
    ['今天天器', '今天天氣很好', '今天天氣很好，', '今天天氣']);
  // Android：確定的一段一段加上去、越說越長的確定結果
  assert.deepEqual(run([[R('整理', false)], [R('整理報表', true), R('然後', false)], [R('整理報表', true), R('整理報表然後寄出', true)]]),
    ['整理', '整理報表然後', '整理報表然後寄出']);
  // 英文的段落之間補空白
  assert.deepEqual(run([[R('open the file', false)], [R('now', false)]]), ['open the file', 'open the file now']);
  assert.equal(V.dictationReset('今天天氣很好', '我們'), true);
  assert.equal(V.dictationReset('今天天氣很好', '今天天氣'), false, '前面一樣的超過一半');
  assert.equal(V.dictationReset('好', ''), false);
  assert.equal(V.dictationStep(null, null).text, '');
});

test('收不到字：之前收得到字的說明 iOS 的問題、改成錄音、重新整理才再用語音打字；之前沒收到過的請再說一次', () => {
  assert.ok(V.DICTATION_DEAF_MS > V.DICTATION_START_TIMEOUT_MS, '完全沒有動靜的先由開始的逾時說明');
  assert.match(V.dictationDeafNotice(true), /iOS 的已知問題/);
  assert.match(V.dictationDeafNotice(true), /改成錄音/);
  assert.match(V.dictationDeafNotice(true), /重新整理/);
  assert.doesNotMatch(V.dictationDeafNotice(false), /改成錄音/);
  assert.match(V.dictationDeafNotice(false), /再說一次/);
});

test('聽寫的錯誤：系統不讓用、不支援中文改回錄音；沒有權限、找不到麥克風、網路有說明；沒說話、中止不提示', () => {
  assert.equal(V.dictationFallback('service-not-allowed'), true);
  assert.equal(V.dictationFallback('language-not-supported'), true);
  assert.equal(V.dictationFallback('not-allowed'), false, '錄音也要麥克風的權限');
  assert.equal(V.dictationFallback('network'), false);
  assert.match(V.dictationError('service-not-allowed'), /改成錄音/);
  assert.match(V.dictationError('not-allowed'), /麥克風的權限/);
  assert.equal(V.dictationError('audio-capture'), '找不到麥克風');
  assert.match(V.dictationError('network'), /網路/);
  assert.equal(V.dictationError('no-speech'), null);
  assert.equal(V.dictationError('aborted'), null);
  assert.match(V.dictationError('bad-grammar'), /bad-grammar/);
  assert.equal(V.DICTATION_LANG, 'zh-TW');
});

test('開始聽之前：前一個辨識器還沒結束就等、等太久中止它、再久就不管它；剛停下語音通知時至少等一下、等它不再出聲；都在 1 秒內', () => {
  const W = V.DICTATION_WAIT;
  assert.ok(W.giveUpMs < 1000, 'iPhone 的 Safari 只把點按轉給 1 秒內的計時器');
  assert.ok(W.speechSettleMs < W.abortAfterMs && W.abortAfterMs < W.giveUpMs);
  assert.equal(V.dictationWaitStep({ waitedMs: 0, closing: 0 }), 'start', '沒有要等的：在點按當下就開始');
  assert.equal(V.dictationWaitStep({ waitedMs: 0, closing: 1 }), 'wait');
  assert.equal(V.dictationWaitStep({ waitedMs: W.abortAfterMs - 1, closing: 1 }), 'wait', '按停之後的最後一段還在路上');
  assert.equal(V.dictationWaitStep({ waitedMs: W.abortAfterMs, closing: 2 }), 'abort');
  assert.equal(V.dictationWaitStep({ waitedMs: W.giveUpMs, closing: 1 }), 'start', '等不到 end（卡住了）：不管它');
  assert.equal(V.dictationWaitStep({ waitedMs: 0, closing: 0, speechStopped: true, speaking: false }), 'wait', '剛停下語音通知');
  assert.equal(V.dictationWaitStep({ waitedMs: W.speechSettleMs, closing: 0, speechStopped: true, speaking: false }), 'start');
  assert.equal(V.dictationWaitStep({ waitedMs: W.speechSettleMs, closing: 0, speechStopped: true, speaking: true }), 'wait', '還在出聲');
  assert.equal(V.dictationWaitStep({ waitedMs: W.giveUpMs, closing: 0, speechStopped: true, speaking: true }), 'start');
  assert.equal(V.dictationWaitStep({ waitedMs: 0, closing: 0, speechStopped: false, speaking: true }), 'start', '不是這次停下的不等');
  assert.equal(V.dictationWaitStep(), 'start');
  assert.ok(V.DICTATION_START_TIMEOUT_MS >= 5000 && V.DICTATION_END_GRACE_MS > W.giveUpMs);
  assert.match(V.DICTATION_NO_START, /再按一下麥克風/);
});

test('iPhone／iPad 一次聽一句、聽完接著聽：只認 iPhone、iPad（含自稱 Macintosh 的 iPadOS）；沒說話照樣接著聽、停下或真的出錯就結束、太久沒聽到字停下', () => {
  const ios = 'Mozilla/5.0 (iPhone; CPU iPhone OS 27_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/27.2 Mobile/15E148 Safari/604.1';
  const mac = 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/27.2 Safari/605.1.15';
  const android = 'Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 Chrome/141.0 Mobile Safari/537.36';
  assert.equal(V.dictationUtterances({ userAgent: ios, maxTouchPoints: 5 }), true);
  assert.equal(V.dictationUtterances({ userAgent: ios.replace('iPhone; CPU iPhone', 'iPad; CPU') }), true);
  assert.equal(V.dictationUtterances({ userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 27_2 like Mac OS X) AppleWebKit/605.1.15 CriOS/141.0 Mobile/15E148' }), true, 'iOS 的 Chrome 也是 WebKit');
  assert.equal(V.dictationUtterances({ userAgent: mac, maxTouchPoints: 5 }), true, 'iPadOS 的 Safari');
  assert.equal(V.dictationUtterances({ userAgent: mac, maxTouchPoints: 0 }), false, 'Mac');
  assert.equal(V.dictationUtterances({ userAgent: android, maxTouchPoints: 5 }), false, 'Android 照舊連續辨識');
  assert.equal(V.dictationUtterances(), false);
  assert.ok(V.DICTATION_RESTART_MS >= 200 && V.DICTATION_RESTART_MS <= 400);
  assert.ok(V.DICTATION_QUIET_MS > V.DICTATION_DEAF_MS * 2, '收不到聲音時至少換一次新的辨識器才停下');
  assert.equal(V.dictationNext({ stopped: false, error: null, quietMs: 0 }), 'restart', '一句說完');
  assert.equal(V.dictationNext({ stopped: false, error: 'no-speech', quietMs: 5000 }), 'restart', '這一句沒說話');
  assert.equal(V.dictationNext({ stopped: true, error: null, quietMs: 0 }), 'end', '按停、打字、送出');
  assert.equal(V.dictationNext({ stopped: false, error: 'not-allowed', quietMs: 0 }), 'end');
  assert.equal(V.dictationNext({ stopped: false, error: 'aborted', quietMs: 0 }), 'end', '被其他程式拿去用');
  assert.equal(V.dictationNext({ stopped: false, error: 'no-speech', quietMs: V.DICTATION_QUIET_MS }), 'quiet');
  assert.equal(V.dictationNext(), 'restart');
  assert.match(V.dictationEndNotice({ quiet: true, heard: false }), /一陣子沒有聽到聲音/);
  assert.match(V.dictationEndNotice({ quiet: true, heard: true }), /一陣子沒有聽到聲音/);
  assert.match(V.dictationEndNotice({ quiet: true, deaf: true, heard: false }), /收不到聲音.*多工畫面/);
  assert.match(V.dictationEndNotice({ quiet: true, deaf: true, heard: true }), /一陣子沒有聽到聲音/, '這次聽得到字：只是後來沒說話');
  assert.equal(V.dictationEndNotice({ stopped: true, quiet: true, deaf: true }), null);
});

test('iPhone／iPad 開始前耐心等前一個辨識器結束（不然只能新建、新建的常收不到聲音）；診斷記錄的做法與文字', () => {
  const U = V.DICTATION_WAIT_UTTERANCES;
  assert.ok(U.abortAfterMs > V.DICTATION_END_GRACE_MS, '停止之後的收尾（DICTATION_END_GRACE_MS 才 abort）等得到');
  assert.ok(U.giveUpMs > U.abortAfterMs);
  assert.equal(V.dictationWaitStep({ waitedMs: 1000, closing: 1, patient: true }), 'wait', '一般的 800ms 早就不等了');
  assert.equal(V.dictationWaitStep({ waitedMs: 1000, closing: 1 }), 'start');
  assert.equal(V.dictationWaitStep({ waitedMs: U.abortAfterMs, closing: 1, patient: true }), 'abort');
  assert.equal(V.dictationWaitStep({ waitedMs: U.giveUpMs, closing: 1, patient: true }), 'start');
  assert.equal(V.dictationWaitStep({ waitedMs: 0, closing: 0, patient: true }), 'start', '沒有要等的照樣在點按當下開始');
  assert.deepEqual([...V.DIAG_MODES], ['gumHold', 'shared', 'fresh', 'ttsFresh', 'sessionFlip', 'gumPrime']);
  assert.equal(V.diagDb(1), '0 dBFS');
  assert.equal(V.diagDb(0.01), '-40 dBFS');
  assert.equal(V.diagDb(0), '-∞ dBFS');
  for (const m of V.DIAG_MODES) assert.equal(typeof V.DIAG_MODE_LABELS[m], 'string');
  assert.equal(V.diagMode('gumHold'), 'gumHold');
  assert.equal(V.diagMode('shared'), 'shared');
  assert.equal(V.diagMode('weird'), 'gumHold', '沒選過、不認得：預設先開麥克風（iOS 27 實機只有這樣第二次之後收得到字）');
  assert.equal(V.diagMode(null), 'gumHold');
  assert.equal(V.diagText('今天天氣很好我們去公園散步然後回家吃飯'), '今天天氣很好我們去公園散步然後回…');
  assert.equal(V.diagText('a\n b', 16), 'a b');
  assert.equal(V.diagText(undefined), '');
});

test('聽寫自己結束時的說明：被中斷一律說；沒收到字說沒聽到；自己停下、已提示過、有收到字的不說', () => {
  assert.match(V.dictationEndNotice({ heard: false }), /沒有聽到聲音/, '沒有錯誤就結束了');
  assert.match(V.dictationEndNotice({ heard: false, error: 'no-speech' }), /沒有聽到聲音/);
  assert.match(V.dictationEndNotice({ heard: false, error: 'aborted' }), /被中斷/);
  assert.match(V.dictationEndNotice({ heard: true, error: 'aborted' }), /被中斷/);
  assert.equal(V.dictationEndNotice({ heard: true }), null, 'Android 說完一段就結束');
  assert.equal(V.dictationEndNotice({ stopped: true, heard: false, error: 'aborted' }), null, '按停、打字、送出、換輸入區');
  assert.equal(V.dictationEndNotice({ noticed: true, heard: false, error: 'network' }), null, 'onerror 已經提示過');
  assert.match(V.dictationEndNotice(), /沒有聽到聲音/);
});

test('聽寫中的 input 算不算自己打字：和聽寫寫進去的一樣不算（鍵盤原樣交回）；不知道該是什麼一律算', () => {
  assert.equal(V.dictationTyped('請幫我整理', '請幫我整理'), false);
  assert.equal(V.dictationTyped('請幫我整理X', '請幫我整理'), true);
  assert.equal(V.dictationTyped('請幫', '請幫我'), true, '刪字');
  assert.equal(V.dictationTyped('', ''), false);
  assert.equal(V.dictationTyped('任何', null), true);
  assert.equal(V.dictationTyped('任何', undefined), true);
});

test('計時、能不能送、麥克風錯誤', () => {
  assert.equal(V.formatElapsed(0), '0:00');
  assert.equal(V.formatElapsed(65_400), '1:05');
  assert.equal(V.formatElapsed(-5), '0:00');
  assert.match(V.checkRecording(300, 1000), /太短/);
  assert.match(V.checkRecording(5000, 0), /太短/, '空的');
  assert.match(V.checkRecording(5000, V.MAX_BYTES + 1), /20 MB/);
  assert.equal(V.checkRecording(5000, 1000), null);
  assert.match(V.micError('NotAllowedError'), /麥克風的權限/);
  assert.equal(V.micError('NotFoundError'), '找不到麥克風');
  assert.match(V.micError('NotReadableError'), /占用/);
  assert.match(V.micError('Weird'), /Weird/);
  assert.equal(V.MAX_SECONDS, 300);
});

test('按鈕說明：閒置說明會先複誦；錄音中帶時間與 Esc；停用帶原因', () => {
  assert.match(V.micTitle('idle'), /先用文字複誦/);
  assert.equal(V.micTitle('recording', { elapsedMs: 12_000 }), '錄音中 0:12：再按一下送出（Esc 取消）');
  assert.match(V.micTitle('sending'), /正在送出/);
  assert.equal(V.micTitle('idle', { reason: '目前沒有連線' }), '語音訊息：目前沒有連線');
  assert.match(V.micTitle('idle', { dictation: true }), /^語音輸入：.*打進輸入框/);
  assert.match(V.micTitle('dictating'), /再按一下停止/);
});

test('voicemsgrules.js 沒有 import；voicemsg.js 沒有 HTML 寫入點、語音辨識只在瀏覽器版、不送 voiceTyping', () => {
  assert.doesNotMatch(source, /^\s*import\s/m);
  const ctl = readFileSync(path.join(webDir, 'voicemsg.js'), 'utf8');
  assert.doesNotMatch(ctl, /innerHTML|outerHTML|insertAdjacentHTML/);
  assert.match(ctl, /return isHub && !!Recognition && !dictationOff/, '桌面的 WebView2 不用語音辨識');
  assert.equal((ctl.match(/new Recognition\(/g) || []).length, 1);
  assert.doesNotMatch(ctl, /voiceTyping/);
  assert.match(ctl, /cmd: 'voiceMessage'/);
});

test('錄音音量表：方均根換成 dBFS 分 5 格，下限以下 0 格、有聲音至少 1 格、滿格以上不超過；變小每次最多掉一格', () => {
  assert.equal(V.LEVEL_CELLS, 5);
  assert.equal(V.levelCells(0), 0);
  assert.equal(V.levelCells(NaN), 0);
  assert.equal(V.levelCells(10 ** (-60 / 20)), 0, '-60 dB 在下限以下');
  assert.equal(V.levelCells(10 ** (-54 / 20)), 1, '剛過下限就有 1 格');
  assert.equal(V.levelCells(10 ** (-30 / 20)), 3);
  assert.equal(V.levelCells(10 ** (-8 / 20)), 5);
  assert.equal(V.levelCells(1), 5, '0 dB 不超過 5 格');
  assert.equal(V.rmsOf([]), 0);
  assert.equal(V.rmsOf(null), 0);
  assert.ok(Math.abs(V.rmsOf([0.5, -0.5, 0.5, -0.5]) - 0.5) < 1e-12);
  assert.ok(Math.abs(V.rmsOf(Float32Array.from({ length: 4800 }, (_, i) => Math.sin(i / 10))) - Math.SQRT1_2) < 0.01, '正弦波的方均根是振幅的 0.707');
  assert.equal(V.meterStep(1, 4), 4, '變大立刻跟上');
  assert.equal(V.meterStep(4, 0), 3, '變小一次最多掉一格');
  assert.equal(V.meterStep(4, 3), 3);
  assert.equal(V.meterStep(undefined, 2), 2);
});

test('錄音波形：整段的音量分成最多 40 格、每格取最大的換成 0～9；讀數比格數少時一個讀數一格；沒讀數或全安靜回 null', () => {
  assert.equal(V.WAVE_BARS, 40);
  assert.equal(V.waveOf([]), null);
  assert.equal(V.waveOf(null), null);
  assert.equal(V.waveOf([0, 0, 1e-5]), null, '全在下限以下');
  const loud = 10 ** (V.LEVEL_FULL_DB / 20);
  assert.equal(V.waveOf([0, loud, 0]), '090');
  const w = V.waveOf(Array.from({ length: 200 }, (_, i) => (i % 50 === 0 ? loud : 0)));
  assert.equal(w.length, 40);
  assert.match(w, /^[0-9]+$/);
  assert.equal([...w].filter((d) => d === '9').length, 4, '每 50 個讀數有一次大聲：4 格滿');
  const mid = V.waveOf([10 ** (-30 / 20)]);
  assert.ok(mid > '0' && mid < '9', '中等音量是中間的數字');
});
