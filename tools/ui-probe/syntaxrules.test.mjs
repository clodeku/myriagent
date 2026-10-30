// 程式碼框語法上色的純規則（src/Myriagent/Web/syntaxrules.js）：各語言的分詞結果、別名、長度上限、
// 不變式（所有片段接起來等於原文、沒有空片段、類別都在 TOKEN_TYPES 裡；以固定種子的亂數字串對每種語言各跑一批），
// 以及 syntaxrules.js 沒有 import、不碰 DOM，syntax.js 沒有 HTML 寫入點。規則模組以 data: URL 匯入（同 launchrules.test.mjs）。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const webDir = path.resolve(here, '..', '..', 'src', 'Myriagent', 'Web');
const source = readFileSync(path.join(webDir, 'syntaxrules.js'), 'utf8');
const S = await import('data:text/javascript;base64,' + Buffer.from(source + '\n// syntaxrules.test\n', 'utf8').toString('base64'));

/** 有上色的片段：「類別:字」 */
const colored = (text, lang) => S.tokenize(text, lang).filter(([t]) => t).map(([t, s]) => t + ':' + s);
const joined = (text, lang) => S.tokenize(text, lang).map(([, s]) => s).join('');

test('langOf：常見的名稱與別名，大小寫不分；認不得的（含 Object 原型上的名稱）回 null', () => {
  const cases = { 'C#': 'csharp', cs: 'csharp', CSharp: 'csharp', js: 'js', TSX: 'ts', Python: 'python', py: 'python', json: 'json', jsonc: 'json',
    yml: 'yaml', toml: 'toml', ini: 'toml', bash: 'shell', sh: 'shell', console: 'shell', ps1: 'powershell', PowerShell: 'powershell', pwsh: 'powershell',
    css: 'css', scss: 'css', html: 'markup', xml: 'markup', xaml: 'markup', csproj: 'markup', sql: 'sql', tsql: 'sql', diff: 'diff', patch: 'diff',
    rust: 'rust', go: 'go', java: 'java', cpp: 'cpp', c: 'cpp', kotlin: 'kotlin', swift: 'swift' };
  for (const [name, want] of Object.entries(cases)) assert.equal(S.langOf(name), want, name);
  for (const name of ['', null, undefined, 'text', 'plaintext', 'brainfuck', 'toString', '__proto__', 'constructor']) assert.equal(S.langOf(name), null, String(name));
  // syntax.js 把 langOf 的結果再交給 tokenize：每個語言 id 本身也要認得（markup 漏過一次，HTML 整段沒上色）
  for (const id of new Set(Object.values(cases))) {
    assert.equal(S.langOf(id), id, id);
    assert.ok(S.tokenize('x = "y" // z', id).length > 0, id);
  }
  assert.ok(S.tokenize('<a href="x">y</a>', 'markup').some(([t]) => t === 'tag'), 'markup 的 id 直接分詞');
});

test('認不得的語言、太長的程式碼整段不上色；空字串回空陣列', () => {
  assert.deepEqual(S.tokenize('var x = 1;', 'text'), [[null, 'var x = 1;']]);
  assert.deepEqual(S.tokenize('var x = 1;', null), [[null, 'var x = 1;']]);
  const long = 'x'.repeat(S.MAX_HIGHLIGHT + 1);
  assert.deepEqual(S.tokenize(long, 'csharp'), [[null, long]]);
  assert.deepEqual(S.tokenize('', 'csharp'), []);
  assert.equal(S.MAX_HIGHLIGHT, 40_000);
});

test('C#：關鍵字、new 後面的型別、呼叫、字串（插值、逐字、字元）、數字、註解、前置處理', () => {
  assert.deepEqual(colored('using var cts = new CancellationTokenSource(LoginTimeout);', 'cs'),
    ['kw:using', 'kw:var', 'kw:new', 'ty:CancellationTokenSource', 'ty:LoginTimeout']);
  assert.deepEqual(colored('Console.WriteLine("hi"); // 註解', 'csharp'), ['ty:Console', 'fn:WriteLine', 'str:"hi"', 'com:// 註解']);
  assert.deepEqual(colored('var s = $"x{y}"; var v = @"a""b"; char c = \'a\'; int n = 0x1F + 2.5f;', 'cs'),
    ['kw:var', 'str:$"x{y}"', 'kw:var', 'str:@"a""b"', 'kw:char', "str:'a'", 'kw:int', 'num:0x1F', 'num:2.5f']);
  assert.deepEqual(colored('#region 登入\n  #if DEBUG\nx = MAX_SIZE; /* 區塊\n註解 */', 'cs'), ['meta:#region 登入', 'meta:#if DEBUG', 'com:/* 區塊\n註解 */']);
  assert.deepEqual(colored('var raw = """\n  {"a": 1}\n  """;', 'cs'), ['kw:var', 'str:"""\n  {"a": 1}\n  """']);
});

test('JavaScript／TypeScript：樣板字串、裝飾器、型別關鍵字只在 TypeScript', () => {
  assert.deepEqual(colored('const f = async (x) => await fetch(`/api/${x}`); // done', 'js'),
    ['kw:const', 'kw:async', 'kw:await', 'fn:fetch', 'str:`/api/${x}`', 'com:// done']);
  assert.deepEqual(colored('@Component\nclass A extends React.Component {}', 'ts'), ['var:@Component', 'kw:class', 'ty:A', 'kw:extends', 'ty:React', 'ty:Component']);
  assert.deepEqual(colored('let n: number = $el.length;', 'ts'), ['kw:let', 'kw:number']);
  assert.deepEqual(colored('let n: number = $el.length;', 'js'), ['kw:let']);
});

test('Python：裝飾器、def 的函式名、各種前綴與三引號的字串、註解', () => {
  assert.deepEqual(colored('@app.route("/")\ndef main(argv):\n    return f"hi {argv}"  # 結尾', 'py'),
    ['var:@app.route', 'str:"/"', 'kw:def', 'fn:main', 'kw:return', 'str:f"hi {argv}"', 'com:# 結尾']);
  assert.deepEqual(colored('class MyModel(Base):\n    """說明"""\n    x = rb\'\\x00\' if self.ok else None', 'python'),
    ['kw:class', 'ty:MyModel', 'ty:Base', 'str:"""說明"""', "str:rb'\\x00'", 'kw:if', 'kw:self', 'kw:else', 'kw:None']);
  assert.deepEqual(colored('buffer = format(1e-3)', 'py'), ['fn:format', 'num:1e-3']);
});

test('JSON：鍵與字串值分開、數字、true／false／null、JSONC 的註解', () => {
  assert.deepEqual(colored('{"name": "x", "n": -1.5e3, "ok": true, "v": null} // c', 'jsonc'),
    ['key:"name"', 'str:"x"', 'key:"n"', 'num:-1.5e3', 'key:"ok"', 'kw:true', 'key:"v"', 'kw:null', 'com:// c']);
});

test('YAML 與 TOML：鍵、清單項目的鍵、區段、註解、字串、布林值', () => {
  assert.deepEqual(colored('name: build\non:\n  push:\n    branches: [main]\n# 註解\n- run: echo "hi"\nok: true', 'yml'),
    ['key:name', 'key:on', 'key:push', 'key:branches', 'com:# 註解', 'key:run', 'str:"hi"', 'key:ok', 'kw:true']);
  assert.deepEqual(colored('url: http://example.test/a', 'yaml'), ['key:url']);
  assert.deepEqual(colored('[features]\nweb_search = true # c\nmodel = "gpt"', 'toml'), ['ty:[features]', 'key:web_search', 'kw:true', 'com:# c', 'key:model', 'str:"gpt"']);
});

test('Bash：命令位置的字是函式、參數、變數、字串、註解；# 只有前面是空白才是註解', () => {
  assert.deepEqual(colored('git commit -m "msg" --amend # c\nexport PATH=$HOME/bin:$PATH', 'bash'),
    ['fn:git', 'key:-m', 'str:"msg"', 'key:--amend', 'com:# c', 'kw:export', 'var:$HOME', 'var:$PATH']);
  assert.deepEqual(colored('ls a#b | grep x && cd ..', 'sh'), ['fn:ls', 'fn:grep', 'fn:cd']);
});

test('PowerShell：cmdlet、參數、變數（含 $env:）、型別、關鍵字不分大小寫、兩種註解與 here-string', () => {
  assert.deepEqual(colored('Get-ChildItem -Path $env:TEMP | ForEach-Object { IF ($_.Length -gt 0) { [int]$x = 1 } } # c', 'ps1'),
    ['fn:Get-ChildItem', 'key:-Path', 'var:$env:TEMP', 'fn:ForEach-Object', 'kw:IF', 'var:$_', 'key:-gt', 'num:0', 'ty:[int]', 'var:$x', 'num:1', 'com:# c']);
  assert.deepEqual(colored('<# 區塊 #>\n$s = @"\n多行 $x\n"@', 'powershell'), ['com:<# 區塊 #>', 'var:$s', 'str:@"\n多行 $x\n"@']);
});

test('CSS：選擇器、屬性、顏色與單位、自訂屬性、函式、!important、@規則', () => {
  assert.deepEqual(colored('.a:hover { color: #fff; margin: 0 4px; --x: 1; transform: translateX(10px) !important; }', 'css'),
    ['tag:.a', 'tag:hover', 'key:color', 'num:#fff', 'key:margin', 'num:0', 'num:4px', 'var:--x', 'num:1', 'key:transform', 'fn:translateX', 'num:10px', 'kw:!important']);
  assert.deepEqual(colored('@media (max-width: 640px) { #main { opacity: .5 } }', 'scss').slice(0, 1), ['kw:@media']);
});

test('HTML／XML：註解、標籤、屬性名與值、沒有值的屬性、實體；標籤外的文字不上色', () => {
  assert.deepEqual(colored('<!-- c --><div class="a" hidden>Hi &amp; bye</div>', 'html'),
    ['com:<!-- c -->', 'tag:<div', 'key:class', 'str:"a"', 'key:hidden', 'tag:>', 'kw:&amp;', 'tag:</div', 'tag:>']);
  assert.deepEqual(colored('<?xml version="1.0"?>\n<Project Sdk="Microsoft.NET.Sdk" />', 'csproj'),
    ['meta:<?xml version="1.0"?>', 'tag:<Project', 'key:Sdk', 'str:"Microsoft.NET.Sdk"', 'tag:/>']);
});

test('SQL：關鍵字不分大小寫、函式、變數、N 字串、註解', () => {
  assert.deepEqual(colored("SELECT TOP 10 name, COUNT(*) FROM dbo.Users WHERE id = @id AND tag = N'甲' -- c", 'sql'),
    ['kw:SELECT', 'kw:TOP', 'num:10', 'fn:COUNT', 'kw:FROM', 'kw:WHERE', 'var:@id', 'kw:AND', "str:N'甲'", 'com:-- c']);
});

test('diff：檔頭、區段、增刪的行；其他行併成一段不上色', () => {
  assert.deepEqual(S.tokenize('diff --git a/x b/x\n--- a/x\n+++ b/x\n@@ -1,2 +1,2 @@\n ctx\n ctx2\n-old\n+new', 'diff'), [
    ['meta', 'diff --git a/x b/x\n'], ['meta', '--- a/x\n'], ['meta', '+++ b/x\n'], ['var', '@@ -1,2 +1,2 @@\n'],
    [null, ' ctx\n ctx2\n'], ['del', '-old\n'], ['add', '+new'],
  ]);
});

test('沒有收尾的字串與註解吃到該停的地方，不丟字', () => {
  assert.deepEqual(colored('var s = "沒有收尾\nvar t = 1;', 'cs'), ['kw:var', 'str:"沒有收尾', 'kw:var', 'num:1']);
  assert.deepEqual(colored('x = 1 /* 沒有收尾', 'js'), ['num:1', 'com:/* 沒有收尾']);
  assert.equal(joined('<div class="a', 'html'), '<div class="a');
});

test('不變式：每種語言對固定種子的亂數字串，片段接起來等於原文、沒有空片段、類別合法', () => {
  let seed = 20261023;
  const rnd = () => { seed = (seed * 1103515245 + 12345) % 2147483648; return seed / 2147483648; };
  const alphabet = ['a', 'Z', 'x', '_', '$', '@', '#', '-', '+', '.', ':', ';', '=', ',', '"', "'", '`', '/', '*', '\\', '<', '>', '{', '}', '[', ']',
    '(', ')', '&', '|', '!', '?', '%', '0', '7', ' ', ' ', '\t', '\n', '\n', '中', '文', 'e', 'x', 'if', 'var', 'def', 'SELECT', '"""', '<#', '#>', '--', '//', '/*', '*/', '@"', '$"', '<!--', '-->'];
  const types = new Set(S.TOKEN_TYPES);
  const langs = ['cs', 'js', 'ts', 'java', 'go', 'rust', 'cpp', 'kotlin', 'swift', 'py', 'json', 'yaml', 'toml', 'bash', 'ps1', 'css', 'html', 'sql', 'diff'];
  for (const lang of langs) {
    for (let k = 0; k < 150; k++) {
      let s = '';
      const len = 1 + Math.floor(rnd() * 120);
      for (let i = 0; i < len; i++) s += alphabet[Math.floor(rnd() * alphabet.length)];
      const toks = S.tokenize(s, lang);
      assert.equal(toks.map(([, x]) => x).join(''), s, lang + '：' + JSON.stringify(s));
      for (const [t, x] of toks) {
        assert.ok(x.length > 0, lang + ' 空片段：' + JSON.stringify(s));
        assert.ok(t === null || types.has(t), lang + ' 類別 ' + t);
      }
    }
  }
});

test('效能：接近上限的 C#（約 4 萬字）一次分詞不超過 300ms', () => {
  const unit = 'public async Task<int> RunAsync(string path) { var n = await File.ReadAllTextAsync(path); return n.Length + 42; } // 註解\n';
  const text = unit.repeat(Math.floor((S.MAX_HIGHLIGHT - 10) / unit.length));
  const t0 = performance.now();
  const toks = S.tokenize(text, 'cs');
  const ms = performance.now() - t0;
  assert.equal(toks.map(([, x]) => x).join(''), text);
  assert.ok(ms < 300, ms + 'ms');
});

test('syntaxrules.js 沒有 import、不碰 DOM；syntax.js 不用 innerHTML／insertAdjacentHTML', () => {
  assert.ok(!/^\s*import\s[^\n]*\bfrom\s*['"]|^\s*import\s*\(|^\s*import\s*['"]/m.test(source), 'syntaxrules.js 不能有 import');
  assert.ok(!/\b(document|window|innerHTML)\b/.test(source), 'syntaxrules.js 不能碰 DOM');
  const dom = readFileSync(path.join(webDir, 'syntax.js'), 'utf8');
  assert.ok(!/innerHTML|outerHTML|insertAdjacentHTML|createContextualFragment/.test(dom), 'syntax.js 不能有 HTML 寫入點');
});

// ───── 工具卡與核准卡的指令、程式碼（2026-10-25）─────
test('指令的語言：PowerShell 工具一律 powershell；「執行指令」看內容像 PowerShell 就是 powershell、其他 shell；不是指令的工具不上色', () => {
  const cmd = (title, o = {}) => Object.assign({ toolKind: 'bash', toolName: '執行指令', title, detail: title }, o);
  assert.equal(S.commandLang(cmd('npm test')), 'shell');
  assert.equal(S.commandLang(cmd('git log --oneline | head -5')), 'shell');
  assert.equal(S.commandLang(cmd('Get-ChildItem -Recurse C:\\repo')), 'powershell');
  assert.equal(S.commandLang(cmd('echo $env:PATH')), 'powershell');
  assert.equal(S.commandLang(cmd('powershell -NoProfile -File x.ps1')), 'powershell');
  assert.equal(S.commandLang(cmd('ls', { toolName: '執行 PowerShell' })), 'powershell');
  assert.equal(S.commandLang(cmd('b1x2', { toolName: '查看背景工作' })), null, '查看背景工作的標題是工作編號');
  assert.equal(S.commandLang(cmd('src/a.cs', { toolKind: 'read', toolName: '讀取檔案' })), null);
  assert.equal(S.commandLang(null), null);
});

test('指令的輸入拆成說明與指令：整份就是指令、說明＋空行＋指令；不是這個格式的不拆（照原文）', () => {
  assert.deepEqual(S.commandParts('npm test', 'npm test'), { note: null, command: 'npm test' });
  assert.deepEqual(S.commandParts('npm test\necho ok', 'npm test'), { note: null, command: 'npm test\necho ok' });
  assert.deepEqual(S.commandParts('跑單元測試\n\nnpm test --filter x\necho ok', 'npm test --filter x'), { note: '跑單元測試', command: 'npm test --filter x\necho ok' });
  assert.equal(S.commandParts('完全不同的內容', 'npm test'), null);
  assert.equal(S.commandParts('', 'npm test'), null);
  assert.equal(S.commandParts('npm test', ''), null);
});

test('其他工具的 JSON 參數以 json 上色；讀檔的輸出依副檔名；其他照原文', () => {
  assert.equal(S.detailLang('{"path":"a","limit":3}'), 'json');
  assert.equal(S.detailLang('  [1, 2]  '), 'json');
  assert.equal(S.detailLang('{ 不是 JSON'), null);
  assert.equal(S.detailLang('找出登入的流程'), null);
  assert.equal(S.outputLang({ toolKind: 'read', filePath: 'C:\\repo\\src\\Foo.cs', output: 'x' }), 'cs');
  assert.equal(S.outputLang({ toolKind: 'read', filePath: '/repo/tools/run.PS1', output: 'x' }), 'PS1');
  assert.equal(S.outputLang({ toolKind: 'read', filePath: 'C:\\repo\\Makefile', output: 'x' }), null, '沒有副檔名');
  assert.equal(S.outputLang({ toolKind: 'read', filePath: 'C:\\repo\\a.unknownext', output: 'x' }), null);
  assert.equal(S.outputLang({ toolKind: 'read', filePath: 'C:\\repo\\a.cs', output: '' }), null);
  assert.equal(S.outputLang({ toolKind: 'bash', filePath: 'C:\\repo\\a.cs', output: 'x' }), null);
});

test('沒標語言的程式碼框：只猜有把握的——JSON、PowerShell、常見命令列工具開頭的指令（可帶提示字元）、XML／HTML；其他不上色', () => {
  assert.equal(S.guessLang('{ "name": "x", "n": 1 }'), 'json');
  assert.equal(S.guessLang('[1, 2, 3]'), 'json');
  assert.equal(S.guessLang('{ 不是 JSON'), null);
  assert.equal(S.guessLang('Get-ChildItem -Recurse | Select-Object Name'), 'powershell');
  assert.equal(S.guessLang('$env:PATH -split ";"'), 'powershell');
  assert.equal(S.guessLang('git status\ngit diff --stat'), 'shell');
  assert.equal(S.guessLang('$ npm install\n$ npm test'), 'shell');
  assert.equal(S.guessLang('# 先還原套件\ndotnet restore\ndotnet build -c Release'), 'shell');
  assert.equal(S.guessLang('winget.exe upgrade --all'), 'shell');
  assert.equal(S.guessLang('if [ "$a" -eq 1 ]; then echo ok; fi'), null, '不是常見工具開頭、也沒有 PowerShell 特徵');
  assert.equal(S.guessLang('<Project Sdk="Microsoft.NET.Sdk">\n</Project>'), 'markup');
  assert.equal(S.guessLang('a < b 而且 c > d'), null);
  assert.equal(S.guessLang('這是一段說明文字'), null);
  assert.equal(S.guessLang(''), null);
  assert.equal(S.commandLang({ toolKind: 'bash', toolName: '執行指令', title: '[ "$a" -eq 1 ] && echo ok' }), 'shell', 'bash 的 -eq 不算 PowerShell');
});
