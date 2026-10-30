// 程式碼框的語法上色（items.js 的 enhanceMarkdown → syntax.js）用到的純規則：沒有 import、沒有 DOM
// （tools/ui-probe/syntaxrules.test.mjs 以 data: URL 匯入測試）。
// tokenize(text, lang) 回傳 [類別, 字] 的陣列：類別是 null（不上色）或 TOKEN_TYPES 之一，所有的字接起來一定等於原文。
// 只求「看起來像編輯器」：不做完整的語法分析，認不得的照原樣不上色；語言認不得（langOf 回 null）時整段不上色。

/** 類別：關鍵字、型別、函式、字串、數字、註解、變數、鍵（JSON／YAML 的鍵、屬性名、CSS 屬性、命令列參數）、標籤、中繼（前置處理、區段）、diff 的增刪 */
export const TOKEN_TYPES = ['kw', 'ty', 'fn', 'str', 'num', 'com', 'var', 'key', 'tag', 'meta', 'add', 'del'];

/** 超過這麼多字的程式碼不上色（分詞與幾萬個節點都太貴；照原樣顯示） */
export const MAX_HIGHLIGHT = 40_000;

const words = (s) => new Set(s.split(/\s+/).filter(Boolean));
const lower = (set) => new Set(Array.from(set, (w) => w.toLowerCase()));

// ───── 共用的小工具 ─────
/** 這個位置之前、同一行只有空白（行首） */
function atBol(t, i) {
  let j = i - 1;
  while (j >= 0 && (t[j] === ' ' || t[j] === '\t')) j--;
  return j < 0 || t[j] === '\n';
}
const afterSpace = (t, i) => i === 0 || t[i - 1] === ' ' || t[i - 1] === '\t' || t[i - 1] === '\n';
const CALL = /[ \t]*\(/y;
/** 後面接著左括號（函式呼叫或定義） */
function isCall(t, end) {
  CALL.lastIndex = end;
  return CALL.test(t);
}
/** 首字大寫、而且有小寫字母：Console、List、IOException 是型別，MAX_SIZE、ID 不算 */
const TYPE_LIKE = /^[A-Z][A-Za-z0-9_]*[a-z][A-Za-z0-9_]*$/;
const NEW_BEFORE = /\bnew\s+$/;
/** 宣告的名稱：class、interface、struct、enum、record、trait、impl、type 後面的名字是型別（C# 的主要建構式、Python 的基底類別寫法也一樣） */
const DECL_BEFORE = /\b(?:class|interface|struct|enum|record|trait|impl|type)\s+$/;
const declared = (t, start) => DECL_BEFORE.test(t.slice(Math.max(0, start - 12), start));

/** 依序試每一條規則（sticky 正規式）；都不符合的字併進前一段不上色的字 */
function scan(text, rules) {
  const out = [];
  let plain = '';
  const st = {};
  let i = 0;
  const n = text.length;
  outer: while (i < n) {
    for (const r of rules) {
      if (r.when && !r.when(text, i, st)) continue;
      r.re.lastIndex = i;
      const m = r.re.exec(text);
      if (!m || !m[0]) continue;
      const end = i + m[0].length;
      const type = typeof r.type === 'function' ? r.type(m[0], text, i, end, st) : r.type;
      if (type === undefined) continue; // 規則自己放棄（讓後面的規則試）
      if (type) {
        if (plain) { out.push([null, plain]); plain = ''; }
        out.push([type, m[0]]);
      } else plain += m[0];
      i = end;
      continue outer;
    }
    plain += text[i++];
  }
  if (plain) out.push([null, plain]);
  return out;
}

const WS = { re: /[ \t\r\n]+/y, type: null };
const NUM = { re: /0[xX][\da-fA-F_]+[uUlLnN]*|0[bB][01_]+[uUlLnN]*|(?:\d[\d_]*(?:\.\d[\d_]*)?|\.\d[\d_]*)(?:[eE][+-]?\d+)?[fFdDmMuUlLnN]{0,2}/y, type: 'num' };
const SLASH_LINE = { re: /\/\/[^\n]*/y, type: 'com' };
const SLASH_BLOCK = { re: /\/\*[\s\S]*?(?:\*\/|$)/y, type: 'com' };
const HASH_LINE = { re: /#[^\n]*/y, type: 'com' };
const DQ = { re: /"(?:[^"\\\n]|\\.)*"?/y, type: 'str' };
const SQ = { re: /'(?:[^'\\\n]|\\.)*'?/y, type: 'str' };

// ───── C 家族（C#、JavaScript／TypeScript、Java、Go、Rust、C／C++、Kotlin、Swift） ─────
function clike(cfg) {
  const ident = (w, t, start, end) => {
    if (cfg.kw.has(w)) return 'kw';
    if (declared(t, start)) return 'ty';
    if (cfg.upperType && NEW_BEFORE.test(t.slice(Math.max(0, start - 8), start)) && /^[A-Z]/.test(w)) return 'ty';
    if (isCall(t, end)) return 'fn';
    if (cfg.upperType && TYPE_LIKE.test(w)) return 'ty';
    if (cfg.types && cfg.types.has(w)) return 'ty';
    return null;
  };
  const rules = [WS, SLASH_LINE, SLASH_BLOCK];
  if (cfg.hashLine) rules.push({ re: /#[^\n]*/y, type: 'meta', when: atBol });
  if (cfg.csharp) {
    rules.push(
      { re: /\$*"""[\s\S]*?(?:"""|$)/y, type: 'str' },                 // 原始字串
      { re: /(?:\$@|@\$|@)"(?:[^"]|"")*"?/y, type: 'str' },              // 逐字字串
      { re: /\$?"(?:[^"\\\n]|\\.)*"?/y, type: 'str' },                   // 一般與插值字串
      { re: /'(?:[^'\\\n]|\\.){1,8}'/y, type: 'str' },                   // 字元
      { re: /@[A-Za-z_]\w*/y, type: null },                              // @識別字
    );
  } else {
    rules.push(DQ);
    if (cfg.rust) rules.push({ re: /'(?:[^'\\\n]|\\.)'/y, type: 'str' }, { re: /'[A-Za-z_]\w*/y, type: 'var' }); // 字元與生命週期
    else rules.push(SQ);
    if (cfg.template) rules.push({ re: /`(?:[^`\\]|\\[\s\S])*`?/y, type: 'str' });
  }
  if (cfg.decorators) rules.push({ re: /@[A-Za-z_][\w.]*/y, type: 'var' });
  rules.push({ re: cfg.dollar ? /[A-Za-z_$][\w$]*/y : /[A-Za-z_]\w*/y, type: ident }, NUM);
  return rules;
}

const CS = clike({
  csharp: true, hashLine: true, upperType: true, kw: words(`abstract as async await base bool break byte case catch char checked class const continue
    decimal default delegate do double dynamic else enum event explicit extern false file finally fixed float for foreach get global goto if implicit in
    init int interface internal is lock long nameof namespace new nint notnull nuint null object operator out override params partial private protected
    public readonly record ref required return sbyte scoped sealed set short sizeof stackalloc static string struct switch this throw true try typeof uint
    ulong unchecked unmanaged unsafe ushort using value var virtual void volatile when where while with yield`),
});
const JS_KW = `async await break case catch class const continue debugger default delete do else export extends false finally for from function get if
  import in instanceof let new null of return set static super switch this throw true try typeof undefined var void while with yield`;
const JS = clike({ dollar: true, template: true, decorators: true, upperType: true, kw: words(JS_KW) });
const TS = clike({
  dollar: true, template: true, decorators: true, upperType: true, kw: words(JS_KW + ` abstract any as asserts bigint boolean declare enum implements
    infer interface is keyof namespace never number object override private protected public readonly satisfies string symbol type unique unknown`),
});
const JAVA = clike({
  decorators: true, upperType: true, kw: words(`abstract assert boolean break byte case catch char class const continue default do double else enum extends
    false final finally float for goto if implements import instanceof int interface long native new null package private protected public record return
    sealed short static super switch synchronized this throw throws transient true try var void volatile while yield`),
});
const GO = clike({
  template: true, upperType: true, kw: words(`break case chan const continue default defer else fallthrough false for func go goto if import interface iota map nil
    package range return select struct switch true type var bool byte complex64 complex128 error float32 float64 int int8 int16 int32 int64 rune string
    uint uint8 uint16 uint32 uint64 uintptr any`),
});
const RUST = clike({
  rust: true, upperType: true, hashLine: true, kw: words(`as async await break const continue crate dyn else enum extern false fn for if impl in let loop match mod
    move mut pub ref return self Self static struct super trait true type unsafe use where while bool char f32 f64 i8 i16 i32 i64 i128 isize str u8 u16 u32
    u64 u128 usize`),
});
const CPP = clike({
  hashLine: true, upperType: true, kw: words(`alignas alignof auto bool break case catch char class const constexpr const_cast continue decltype default delete do
    double dynamic_cast else enum explicit extern false float for friend goto if inline int long mutable namespace new noexcept nullptr operator private
    protected public register reinterpret_cast return short signed sizeof static static_cast struct switch template this throw true try typedef typename
    union unsigned using virtual void volatile while size_t NULL`),
});
const KOTLIN = clike({
  template: false, decorators: true, upperType: true, kw: words(`as break class continue do else false for fun if in interface is null object package return super
    this throw true try typealias typeof val var when while by catch constructor companion data enum final finally get import init inline internal lateinit
    open override private protected public sealed set suspend`),
});
const SWIFT = clike({
  decorators: true, upperType: true, kw: words(`associatedtype class deinit enum extension fileprivate func import init inout internal let open operator private
    protocol public rethrows static struct subscript typealias var break case continue default defer do else fallthrough for guard if in repeat return
    switch where while as Any catch false is nil super self Self throw throws true try async await`),
});

// ───── Python ─────
const PY_KW = words(`and as assert async await break case class continue def del elif else except False finally for from global if import in is lambda
  match None nonlocal not or pass raise return self cls True try while with yield`);
const PY = [
  WS, HASH_LINE,
  { re: /(?:[rRbBuUfF]{1,2})?(?:"""[\s\S]*?(?:"""|$)|'''[\s\S]*?(?:'''|$)|"(?:[^"\\\n]|\\.)*"?|'(?:[^'\\\n]|\\.)*'?)/y, type: 'str' },
  { re: /@[A-Za-z_][\w.]*/y, type: 'var' },
  { re: /[A-Za-z_]\w*/y, type: (w, t, s, e) => (PY_KW.has(w) ? 'kw' : declared(t, s) ? 'ty' : isCall(t, e) ? 'fn' : TYPE_LIKE.test(w) ? 'ty' : null) },
  NUM,
];

// ───── JSON（含 JSONC 的註解） ─────
const AFTER_COLON = /[ \t]*:/y;
const JSON_RULES = [
  WS, SLASH_LINE, SLASH_BLOCK,
  { re: /"(?:[^"\\\n]|\\.)*"?/y, type: (w, t, s, e) => { AFTER_COLON.lastIndex = e; return AFTER_COLON.test(t) ? 'key' : 'str'; } },
  { re: /-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?/y, type: 'num' },
  { re: /true|false|null/y, type: 'kw' },
];

// ───── YAML、TOML／INI ─────
const YAML_LIT = words('true false null yes no on off True False Null Yes No TRUE FALSE NULL ~');
const YAML_RULES = [
  WS,
  { re: /#[^\n]*/y, type: 'com', when: afterSpace },
  { re: /---|\.\.\./y, type: 'meta', when: atBol },
  { re: /-[ \t]+/y, type: null, when: atBol },
  { re: /[^\s:#'"{}[\],&*!|>][^:\n#]*?(?=[ \t]*:(?:[ \t]|$|\n))/y, type: 'key', when: (t, i) => atBol(t, i) || /-[ \t]+$/.test(t.slice(Math.max(0, i - 6), i)) },
  DQ, SQ,
  { re: /[&*][\w-]+/y, type: 'var' },
  { re: /[A-Za-z_~][\w.~-]*/y, type: (w) => (YAML_LIT.has(w) ? 'kw' : null) },
  NUM,
];
const TOML_RULES = [
  WS,
  { re: /[#;][^\n]*/y, type: 'com', when: afterSpace },
  { re: /\[\[?[^\]\n]*\]\]?/y, type: 'ty', when: atBol },
  { re: /[A-Za-z0-9_.-]+(?=[ \t]*=)/y, type: 'key', when: atBol },
  { re: /"""[\s\S]*?(?:"""|$)|'''[\s\S]*?(?:'''|$)/y, type: 'str' },
  DQ, SQ,
  { re: /true|false/y, type: 'kw' },
  NUM,
];

// ───── 命令列（Bash／sh、PowerShell） ─────
const SH_KW = words('if then else elif fi for in do done while until case esac function return export local readonly declare unset break continue select time');
/** 命令的位置：行首，或接在 | ; & ( 之後 */
function commandPos(t, i) {
  let j = i - 1;
  while (j >= 0 && (t[j] === ' ' || t[j] === '\t')) j--;
  return j < 0 || t[j] === '\n' || t[j] === '|' || t[j] === ';' || t[j] === '&' || t[j] === '(';
}
const SH_RULES = [
  WS,
  { re: /#[^\n]*/y, type: 'com', when: afterSpace },
  { re: /'[^']*'?/y, type: 'str' },
  { re: /"(?:[^"\\]|\\[\s\S])*"?/y, type: 'str' },
  { re: /\$(?:\{[^}\n]*\}?|[A-Za-z_]\w*|[@*#?$!0-9-])/y, type: 'var' },
  { re: /--?[A-Za-z][\w-]*/y, type: 'key', when: afterSpace },
  { re: /[A-Za-z_./~][\w./~+-]*/y, type: (w, t, s) => (SH_KW.has(w) ? 'kw' : commandPos(t, s) ? 'fn' : null) },
  NUM,
];
const PS_KW = lower(words(`begin break catch class continue data do dynamicparam else elseif end enum exit filter finally for foreach from function hidden if in
  param process return static switch throw trap try until using var while workflow`));
const PS_RULES = [
  WS,
  { re: /<#[\s\S]*?(?:#>|$)/y, type: 'com' },
  { re: /#[^\n]*/y, type: 'com' },
  { re: /@"[\s\S]*?(?:\n"@|$)|@'[\s\S]*?(?:\n'@|$)/y, type: 'str' },
  { re: /"(?:[^"`]|`[\s\S])*"?/y, type: 'str' },
  { re: /'(?:[^']|'')*'?/y, type: 'str' },
  { re: /\$(?:\{[^}\n]*\}?|[\w:?]+)/y, type: 'var' },
  { re: /\[[A-Za-z][\w.]*(?:\[\])?\]/y, type: 'ty' },
  { re: /-[A-Za-z][\w-]*/y, type: 'key', when: afterSpace },
  { re: /[A-Za-z]+-[A-Za-z][\w]*/y, type: 'fn' },
  { re: /[A-Za-z_][\w.]*/y, type: (w) => (PS_KW.has(w.toLowerCase()) ? 'kw' : null) },
  NUM,
];

// ───── CSS（含 SCSS、LESS 的常見寫法） ─────
const CSS_RULES = [
  WS, SLASH_BLOCK,
  { re: /\/\/[^\n]*/y, type: 'com', when: afterSpace },
  DQ, SQ,
  { re: /@[\w-]+/y, type: 'kw' },
  { re: /!important/y, type: 'kw' },
  { re: /#[\da-fA-F]{3,8}\b/y, type: (w, t, s, e, st) => (st.depth > 0 ? 'num' : 'tag') },
  { re: /[{}]/y, type: (w, t, s, e, st) => { st.depth = Math.max(0, (st.depth || 0) + (w === '{' ? 1 : -1)); return null; } },
  { re: /--[\w-]+/y, type: 'var' },
  { re: /-?(?:\d*\.\d+|\d+)(?:%|[a-z]+)?/y, type: (w, t, s, e, st) => (st.depth > 0 ? 'num' : null) },
  { re: /[\w-]+(?=\()/y, type: 'fn' },
  { re: /[A-Za-z_-][\w-]*(?=[ \t]*:(?!:))/y, type: (w, t, s, e, st) => (st.depth > 0 ? 'key' : 'tag') },
  { re: /[.#]?[A-Za-z_-][\w-]*/y, type: (w, t, s, e, st) => (st.depth > 0 ? null : 'tag') },
];

// ───── HTML／XML（含 XAML、SVG、csproj） ─────
const inTag = (t, i, st) => !!st.inTag;
const outTag = (t, i, st) => !st.inTag;
const MARKUP_RULES = [
  { re: /<!--[\s\S]*?(?:-->|$)/y, type: 'com', when: outTag },
  { re: /<![A-Za-z][^>]*>?|<\?[\s\S]*?(?:\?>|$)/y, type: 'meta', when: outTag },
  { re: /<\/?[A-Za-z][\w:.-]*/y, type: (w, t, s, e, st) => { st.inTag = true; return 'tag'; }, when: outTag },
  { re: /&[#\w]+;/y, type: 'kw', when: outTag },
  { re: /[^<&]+/y, type: null, when: outTag },
  // 標籤裡：屬性名、屬性值、結尾的 > 或 />
  { re: /\/?>/y, type: (w, t, s, e, st) => { st.inTag = false; return 'tag'; }, when: inTag },
  { re: /[ \t\r\n]+/y, type: null, when: inTag },
  { re: /"[^"]*"?|'[^']*'?/y, type: 'str', when: inTag },
  { re: /[^\s"'<>/=]+/y, type: 'key', when: inTag },
];

// ───── SQL ─────
const SQL_KW = lower(words(`add all alter and any as asc begin between by case cast check column commit constraint create cross database declare default delete
  desc distinct drop else end exec execute exists foreign from full go group having if in index inner insert into is join key left like limit merge not
  null offset on or order outer over partition primary procedure references return right rollback select set table then top transaction trigger truncate
  union unique update use values view when where while with int bigint smallint tinyint bit decimal numeric float real date datetime datetime2 time char
  varchar nchar nvarchar text ntext uniqueidentifier varbinary max output nocount`));
const SQL_RULES = [
  WS,
  { re: /--[^\n]*/y, type: 'com' }, SLASH_BLOCK,
  { re: /[nN]?'(?:[^']|'')*'?/y, type: 'str' },
  { re: /@@?[\w]+/y, type: 'var' },
  { re: /[A-Za-z_#][\w#$]*/y, type: (w, t, s, e) => (SQL_KW.has(w.toLowerCase()) ? 'kw' : isCall(t, e) ? 'fn' : null) },
  NUM,
];

// ───── diff ─────
function diffTokens(text) {
  const out = [];
  for (const line of text.match(/[^\n]*\n|[^\n]+$/g) || []) {
    const type = /^(?:\+\+\+|---|diff |index )/.test(line) ? 'meta' : line[0] === '+' ? 'add' : line[0] === '-' ? 'del' : line.startsWith('@@') ? 'var' : null;
    if (!type && out.length && out[out.length - 1][0] === null) out[out.length - 1][1] += line;
    else out.push([type, line]);
  }
  return out;
}

// ───── 語言名稱 ─────
const LANGS = {
  csharp: CS, js: JS, ts: TS, java: JAVA, go: GO, rust: RUST, cpp: CPP, kotlin: KOTLIN, swift: SWIFT, python: PY, json: JSON_RULES, yaml: YAML_RULES,
  toml: TOML_RULES, shell: SH_RULES, powershell: PS_RULES, css: CSS_RULES, markup: MARKUP_RULES, sql: SQL_RULES, diff: 'diff',
};
const ALIASES = {
  cs: 'csharp', csharp: 'csharp', 'c#': 'csharp', csx: 'csharp',
  js: 'js', javascript: 'js', mjs: 'js', cjs: 'js', jsx: 'js', node: 'js',
  ts: 'ts', typescript: 'ts', tsx: 'ts', mts: 'ts', cts: 'ts',
  java: 'java', go: 'go', golang: 'go', rust: 'rust', rs: 'rust',
  c: 'cpp', h: 'cpp', cpp: 'cpp', 'c++': 'cpp', cc: 'cpp', cxx: 'cpp', hpp: 'cpp', objc: 'cpp',
  kotlin: 'kotlin', kt: 'kotlin', kts: 'kotlin', swift: 'swift',
  py: 'python', python: 'python', python3: 'python', py3: 'python',
  json: 'json', jsonc: 'json', json5: 'json', jsonl: 'json',
  yaml: 'yaml', yml: 'yaml', toml: 'toml', ini: 'toml', cfg: 'toml', conf: 'toml', editorconfig: 'toml', properties: 'toml',
  sh: 'shell', bash: 'shell', shell: 'shell', zsh: 'shell', console: 'shell', shellsession: 'shell', terminal: 'shell',
  ps: 'powershell', ps1: 'powershell', psm1: 'powershell', powershell: 'powershell', pwsh: 'powershell',
  css: 'css', scss: 'css', less: 'css', sass: 'css',
  markup: 'markup', html: 'markup', htm: 'markup', xml: 'markup', xaml: 'markup', svg: 'markup', xhtml: 'markup', csproj: 'markup', vbproj: 'markup', fsproj: 'markup',
  props: 'markup', targets: 'markup', resx: 'markup', vue: 'markup', razor: 'markup', cshtml: 'markup',
  sql: 'sql', tsql: 'sql', mssql: 'sql', mysql: 'sql', postgresql: 'sql', postgres: 'sql', psql: 'sql', sqlite: 'sql', plsql: 'sql',
  diff: 'diff', patch: 'diff', udiff: 'diff',
};

/** 程式碼框的語言名稱（Markdig 的 language-xxx）對到認得的語言；認不得回 null（不上色） */
export function langOf(name) {
  const k = String(name || '').trim().toLowerCase();
  return Object.prototype.hasOwnProperty.call(ALIASES, k) ? ALIASES[k] : null;
}

/** 分詞：認不得的語言或太長的程式碼整段一個不上色的片段；空字串回空陣列 */
export function tokenize(text, name) {
  const s = String(text ?? '');
  if (!s) return [];
  const lang = langOf(name);
  if (!lang || s.length > MAX_HIGHLIGHT) return [[null, s]];
  const rules = LANGS[lang];
  return rules === 'diff' ? diffTokens(s) : scan(s, rules);
}

// ───── 工具卡與核准卡的指令、程式碼（2026-10-25 使用者要求：對話框裡的程式碼與指令都照語法上色）─────

/** 指令工具的名稱（宿主的 toolName）：查看、停止背景工作的種類也是 bash，但標題是工作編號，不是指令 */
const COMMAND_TOOLS = ['執行指令', '執行 PowerShell'];

/** 看起來是 PowerShell 的指令：Verb-Noun 的 cmdlet、$env:、[System.…]、PowerShell 常見的參數（-eq 這類比較運算子 bash 的 test 也有，不算） */
const PS_HINT = /(?:^|[\s;|({])(?:Get|Set|New|Remove|Start|Stop|Test|Invoke|Select|Where|ForEach|Write|Out|Import|Export|Copy|Move|Join|Split|Resolve|Add|Clear|Format|Measure|Sort|Group|ConvertTo|ConvertFrom|Update|Wait|Push|Pop|Rename|Expand|Compress|Register|Unregister)-[A-Z][A-Za-z]+|\$env:|\[System\.|-ExecutionPolicy\b|-NoProfile\b/;

/**
 * 工具卡、核准卡的指令要用哪一種語言上色：PowerShell 工具一律 powershell；一般的「執行指令」看內容像 PowerShell 就是 powershell，
 * 其他是 shell；不是指令（讀檔、搜尋、查看背景工作…）回 null
 */
export function commandLang(item) {
  if (!item || item.toolKind !== 'bash' || !COMMAND_TOOLS.includes(item.toolName)) return null;
  if (item.toolName === '執行 PowerShell') return 'powershell';
  return PS_HINT.test(String(item.detail || item.title || '')) ? 'powershell' : 'shell';
}

/**
 * 指令工具展開後的「輸入」拆成說明與指令（宿主寫成「說明＋空行＋指令」，沒有說明時整份就是指令；標題是指令的第一行）：
 * 回 { note, command }；找不到標題（不是這個格式）回 null（照原文、不上色）
 */
export function commandParts(detail, title) {
  const d = typeof detail === 'string' ? detail : '';
  const t = typeof title === 'string' ? title.trim() : '';
  if (!d || !t) return null;
  if (d.startsWith(t)) return { note: null, command: d };
  const i = d.indexOf('\n\n' + t);
  return i > 0 ? { note: d.slice(0, i), command: d.slice(i + 2) } : null;
}

/** 其他工具展開後的「輸入」：JSON（MCP 與各家工具的參數）就以 json 上色，其他照原文 */
export function detailLang(detail) {
  const d = typeof detail === 'string' ? detail.trim() : '';
  if (d.length < 2 || d.length > MAX_HIGHLIGHT || !/^[[{]/.test(d)) return null;
  try { JSON.parse(d); return 'json'; } catch { return null; }
}

/** 讀檔工具的輸出是檔案內容：依副檔名上色，回副檔名（認不得的語言、其他工具的輸出回 null，照原文） */
export function outputLang(item) {
  if (!item || item.toolKind !== 'read' || typeof item.filePath !== 'string' || !item.output) return null;
  const name = item.filePath.slice(Math.max(item.filePath.lastIndexOf('/'), item.filePath.lastIndexOf(String.fromCharCode(92))) + 1);
  const dot = name.lastIndexOf('.');
  const ext = dot > 0 ? name.slice(dot + 1) : '';
  return ext && langOf(ext) ? ext : null;
}

/** 常見命令列工具（沒標語言的程式碼框，第一個字是這些就當成指令） */
const SHELL_HEADS = new Set(['git', 'gh', 'npm', 'npx', 'pnpm', 'yarn', 'node', 'deno', 'bun', 'dotnet', 'python', 'python3', 'py', 'pip', 'pip3', 'uv', 'cargo', 'rustup', 'go',
  'java', 'mvn', 'gradle', 'docker', 'kubectl', 'helm', 'az', 'aws', 'gcloud', 'curl', 'wget', 'ssh', 'scp', 'cd', 'ls', 'cat', 'grep', 'rg', 'find', 'echo', 'mkdir', 'rm', 'cp', 'mv',
  'chmod', 'chown', 'sudo', 'apt', 'apt-get', 'brew', 'winget', 'choco', 'scoop', 'make', 'cmake', 'msbuild', 'claude', 'codex', 'grok', 'agy', 'export', 'set', 'tar', 'unzip',
  'sqlcmd', 'bcp', 'robocopy', 'xcopy', 'ffmpeg', 'code', 'cmd', 'powershell', 'pwsh', 'bash', 'sh', 'source']);

/**
 * 沒標語言的程式碼框猜語言（2026-10-25：指令也要上色）：JSON、PowerShell、常見命令列工具開頭的指令（允許「$ 」或「> 」提示字元）、
 * XML／HTML。只猜有把握的，其他回 null（照舊不上色）
 */
export function guessLang(text) {
  const t = String(text ?? '').trim();
  if (t.length < 2 || t.length > MAX_HIGHLIGHT) return null;
  if (/^[[{]/.test(t)) { try { JSON.parse(t); return 'json'; } catch { /* 不是 JSON */ } }
  if (/^<[A-Za-z?!]/.test(t) && /<\/?[A-Za-z][^>]*>/.test(t)) return 'markup';
  const first = t.split('\n').map((l) => l.trim()).find((l) => l && !l.startsWith('#')) || '';
  const head = first.replace(/^(?:\$|>|PS [^>]*>)\s+/, '').split(/\s+/)[0].toLowerCase();
  if (PS_HINT.test(t)) return 'powershell';
  if (SHELL_HEADS.has(head.replace(/\.exe$/, ''))) return 'shell';
  return null;
}
