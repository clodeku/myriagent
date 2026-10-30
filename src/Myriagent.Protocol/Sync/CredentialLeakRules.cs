using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Myriagent.Agents;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>
/// 權杖 DLP（設計 8.8、14.11 D43、D44；docs/sync-protocol.md 14.2、14.3）：要上傳的項目在擁有者電腦先把疑似權杖的片段換成
/// <see cref="Replacement"/>（只換上傳的副本，本機不動）；伺服器收到時再掃一次當第二道。憑證本來就不離開電腦（同步不變式），
/// 這裡擋的是代理執行過 <c>type .credentials.json</c> 之類的指令後、權杖出現在逐字稿與工具輸出裡被一起同步出去。純函式、不碰檔案系統。
/// <list type="bullet">
/// <item>前綴夠獨特、不設長度下限：<c>sk-ant-…-</c>、<c>sk-proj-</c>、<c>sk-svcacct-</c>、<c>sk-admin-</c>、<c>github_pat_</c>。</item>
/// <item>有長度下限（前綴之後）：<c>gh[pousr]_</c> ≥20、<c>xox?-</c> ≥10、<c>ya29.</c> ≥20、<c>1//0</c> ≥20、<c>xai-</c> ≥20、
/// <c>chd_</c>／<c>cha_</c> ≥16、舊式 OpenAI 的 <c>sk-</c> ≥40（設計 8.8 的表）。</item>
/// <item>固定長度（前後都不能接英數字）：<c>AKIA</c>／<c>ASIA</c> 後 16 碼、<c>AIza</c> 後 35 碼。</item>
/// <item>其他：JWT、JSON 與查詢字串的權杖類欄位（只換值；跳脫過的 <c>\"name\":\"value\"</c> 也認）、<c>access|refresh|id|session</c>
/// 接 <c>_</c>／<c>-</c> 接 <c>token</c> 的欄位以 <c>:</c>／<c>=</c> 接 ≥16 字的值（PowerShell、YAML、Python、環境變數的寫法）、
/// AWS 的秘密金鑰（<c>aws_secret_access_key</c>、<c>SecretAccessKey</c> 接 40 碼）與 <c>aws_session_token</c>、
/// 私鑰區塊（沒有結尾時換到最後）、<c>Bearer</c> 後的權杖、<c>__Host-myriagenthub=</c> cookie 的值。</item>
/// <item>F10（WP4.3，<see cref="Version"/> 2）：<c>Authorization:</c> 接 <c>Basic</c>／<c>Token</c>／<c>Bearer</c> 的值（≥8 字；沒有數字而且全大寫或含底線的
/// <c>YOUR_TOKEN</c> 這類說明文字不算）；網址 <c>scheme://user:password@</c> 的密碼（userinfo 到 authority 裡最後一個 <c>@</c> 為止，
/// 一定要有 <c>@</c>；只有使用者名稱的不遮，除非使用者名稱像權杖：≥20 個英數字、底線、連字號，字母與數字都有，這時整段 userinfo 換掉；
/// <c>$VAR</c>、<c>%s</c>、全是 <c>*</c> 的密碼是佔位，不算）；<c>http.extraheader</c> 的整個值（引號裡的到引號為止，沒有引號的到行尾，
/// 含 <c>git -c</c>、<c>git config</c> 與 <c>.gitconfig</c> 的寫法）；base64 片段（≥64 個 base64 字元、可跨等寬的折行，解碼後最多看
/// 64 KB）解碼後 <see cref="HasLeak"/> 或含權杖欄位名稱（<see cref="TokenFieldNames"/>）才整段換掉，片段裡的 base64 再解一層。</item>
/// </list>
/// 截斷與串流的文字另外處理（<see cref="MaskItem"/>、<see cref="MaskLive"/>）：開頭被截掉的工具輸出（「…（前 N 字省略）」）第一行整行換掉
/// （<see cref="MaskTruncated"/>：切點可能在權杖中間，剩下的片段沒有前綴、比對不到），結尾被截斷的文字先去掉尾端的權杖字元
/// （<see cref="SafeLength"/>、<see cref="MaskCut"/>）；差異逐行存放，私鑰從開頭標記到結尾標記的每一行都整行換掉。
/// 比對分兩段：<see cref="RegexOptions.NonBacktracking"/> 的樣式只認「前綴＋最短的本體」（長度固定、有上限，病態的輸入也是線性時間；
/// 不能用環視與反向參照），之後在程式裡把結尾延伸到本體字元結束（權杖再長也整段換掉），最後檢查邊界與固定長度。
/// NonBacktracking 遇到沒有上限的迴圈、第一次比對到很長的片段時要好幾秒（實測 1 MB 約 2.5 秒），有上限的迴圈又受節點數限制，所以延伸不交給樣式。
/// 前綴類的樣式要求前一個字元不是英數字（<c>task-proj-…</c> 不算），不用 <c>\b</c>：中文字也算「字」，<c>權杖ghp_…</c> 的 <c>\b</c> 不成立。
/// <para>
/// 樣式的定義（<see cref="Definitions"/>：名稱、原文、選項、延伸的字元、邊界、固定長度、群組、比對之後的判斷）與已編譯的 Regex（<see cref="CompiledPatterns"/>）分開
/// （WP4.9 步 4、計畫 D4）：已編譯的在第一次用到時才建（<see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>），桌面閒置時以
/// <see cref="ReleaseCompiledPatterns"/> 丟掉——NonBacktracking 的狀態快取與字元集合的 BDD 都掛在 Regex 實例底下，300 個對話的啟動重整後約 15 MB
/// （設計 14.13 的 S6），只有丟掉實例才收得回來。每一次公開的呼叫只取一次已編譯樣式的快照往下傳，中途被釋放也照同一份做完；
/// 釋放之後下一次呼叫重建，輸出逐位元組相同，<see cref="Version"/> 與 <see cref="PatternFingerprint"/> 也不變（由定義算，不必建 Regex）。
/// 伺服器在閒置修剪時也釋放（MyriagentHub 的 <c>Ops/IdleTrimRules.cs</c>，S9 的閒置記憶體，設計 14.20）
/// </para>
/// </summary>
internal static class CredentialLeakRules
{
    /// <summary>上傳副本裡取代命中片段的文字（設計 8.8）；不含任何 ASCII，遮蔽過的文字不會再被任何樣式比對到</summary>
    public const string Replacement = "［疑似權杖已遮蔽］";

    private const RegexOptions Options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;

    /// <summary>
    /// 樣式的版本（手動維護，WP4.3 計畫 D17）：改了任何樣式——名稱、正規式、延伸的字元、邊界、比對之後的判斷、base64 解碼後認的欄位名稱——就加 1。
    /// 測試以 <see cref="PatternFingerprint"/> 的雜湊釘住內容，只改內容、忘了升版時測試失敗。
    /// 2＝WP4.3 的 F10（Authorization 的 Basic／Token／Bearer、網址裡的密碼、http.extraheader、base64 包起來的憑證檔）。
    /// 桌面宣告對話同步（conv1）的必要版本也是 2（ConversationPublisher.RequiredDlpVersion，D38）：之後只改樣式時升這裡就好，
    /// 必要版本只在「不補這個洞就不能上線」時才升
    /// </summary>
    public const int Version = 2;

    /// <summary>JSON 解析的深度上限（工具的輸入輸出；超過就當成一般文字遮蔽）</summary>
    private const int MaxJsonDepth = 64;

    /// <summary>base64 片段至少要這麼多個 base64 字元（不含換行與縮排）才解碼</summary>
    private const int MinBase64Chars = 64;

    /// <summary>base64 片段解碼的上限（解碼後的位元組）：更長的只看開頭這一段（憑證檔都很小；很長的 base64 是圖片與檔案）</summary>
    private const int MaxDecodeBytes = 64 * 1024;

    /// <summary>解碼 <see cref="MaxDecodeBytes"/> 要的 base64 字元數（4 的倍數）</summary>
    private const int MaxDecodeChars = MaxDecodeBytes / 3 * 4;

    /// <summary>解碼的層數上限：解碼後的內容裡的 base64 再解一層（kubeconfig 的 client-key-data 是 base64 裡的 base64），再裡面的不解</summary>
    private const int MaxDecodeDepth = 2;

    // 下面的靜態欄位都在 Definitions 之前（BuildDefinitions 不用它們，但照「後宣告的靜態欄位在初始化時是 null」的規矩放前面）

    /// <summary>
    /// base64 解碼後出現就整段遮蔽的權杖欄位名稱（不分大小寫、子字串比對）：憑證檔的內容未必命中任何樣式（Codex 的 refresh_token 沒有前綴），
    /// 欄位名稱在檔案裡一定看得到。改了這份清單也要升 <see cref="Version"/>
    /// </summary>
    private static readonly string[] TokenFields =
    [
        "access_token", "accesstoken", "refresh_token", "refreshtoken", "id_token", "session_token", "sessiontoken",
        "client_secret", "clientsecret", "api_key", "api-key", "apikey", "private_key", "privatekey",
        "secret_access_key", "secretaccesskey", "client-key-data", "password", "passwd", "auths",
    ];

    private static readonly SearchValues<string> TokenFieldSearch = SearchValues.Create(TokenFields, StringComparison.OrdinalIgnoreCase);

    /// <summary>base64 本體的字元（標準字母表；<c>=</c> 只在結尾）</summary>
    private static readonly SearchValues<char> Base64Body = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/");

    /// <summary>
    /// 網址 authority 的字元（RFC 3986 的 unreserved、sub-delims 去掉單引號、<c>:</c>、<c>@</c>、<c>%</c>、IPv6 的方括號）：
    /// 遇到 <c>/ ? #</c>、空白、引號、反斜線與非 ASCII 就結束（遮蔽過的文字不含 ASCII，不會再被比對到）
    /// </summary>
    private static readonly SearchValues<char> AuthorityChars = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~!$&()*+,;=:%@[]");

    /// <summary>像權杖的使用者名稱的字元</summary>
    private static readonly SearchValues<char> TokenUserChars = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-");

    /// <summary>私鑰區塊的開頭與結尾標記的原文（差異逐行存放、截斷的輸出：<see cref="MaskDiff"/>、<see cref="MaskTruncated(string?)"/>；已編譯的在 <see cref="CompiledPatterns"/>）</summary>
    private const string PrivateKeyBeginSource = @"-----BEGIN[ A-Z0-9]{0,64}PRIVATE KEY(?: BLOCK)?-----";

    private const string PrivateKeyEndSource = @"-----END[ A-Z0-9]{0,64}PRIVATE KEY(?: BLOCK)?-----";

    /// <summary>私鑰本體一行的字元（base64）</summary>
    private static readonly SearchValues<char> KeyBodyChars = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=");

    /// <summary>私鑰標記行的字元（<c>-----BEGIN RSA PRIVATE KEY-----</c> 被切掉開頭後剩下的尾巴）</summary>
    private static readonly SearchValues<char> MarkerChars = SearchValues.Create(" ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-");

    /// <summary>
    /// 所有樣式的定義（套用順序）：先換整個私鑰區塊，再換「欄位名稱＋值」類（只換值，留下欄位名稱讓人看得懂），最後是有前綴的權杖。
    /// 遮蔽的文字不含 ASCII，所以前面換過的不會被後面的樣式再比對到。最後一個有初始值的靜態欄位（建構時用到的都是常數與方法）；
    /// 已編譯的 Regex 不在這裡（<see cref="CompiledPatterns"/>）
    /// </summary>
    private static readonly PatternDefinition[] Definitions = BuildDefinitions();

    /// <summary>已編譯的樣式；null＝還沒用過或已釋放（<see cref="Current"/> 延遲建立、<see cref="ReleaseCompiledPatterns"/> 丟掉）</summary>
    private static CompiledPatterns? compiled;

    /// <summary>樣式的名稱（由定義讀，不必編譯）</summary>
    public static IReadOnlyList<string> PatternNames => Definitions.Select(p => p.Name).ToList();

    /// <summary>每個樣式的 Regex 選項（由定義讀，和編譯時傳給 Regex 的相同；測試確認一律是 NonBacktracking，編譯得過另由 <see cref="EnsureCompiled"/> 的測試確認）</summary>
    internal static IReadOnlyList<RegexOptions> PatternOptions => Definitions.Select(p => p.Options).ToList();

    /// <summary>base64 解碼後認的權杖欄位名稱（測試與文件用）</summary>
    internal static IReadOnlyList<string> TokenFieldNames => TokenFields;

    /// <summary>
    /// 樣式內容的指紋（測試以 SHA-256 釘住，改了樣式要升 <see cref="Version"/>）：依套用順序，每個樣式的名稱、正規式原文與選項、延伸的字元、
    /// 前一個字元的邊界、固定長度、只換的群組、比對之後的判斷（方法名稱），最後接 <see cref="TokenFieldNames"/>。
    /// 由定義算（正規式原文與選項就是編譯時傳給 Regex 的，和以前讀已編譯實例的 <c>Regex.ToString()</c>、<c>Regex.Options</c> 逐位元組相同），讀它不會建已編譯的樣式
    /// </summary>
    internal static string PatternFingerprint
        => string.Join("\n", Definitions.Select(p => string.Join("\t",
               p.Name, p.Source, ((int)p.Options).ToString(CultureInfo.InvariantCulture), p.ExtendChars ?? "-",
               p.PrefixBoundary ? "1" : "0", p.ExactLength?.ToString(CultureInfo.InvariantCulture) ?? "-", p.SecretGroup ?? "-", p.Finish?.Method.Name ?? "-")))
           + "\n" + string.Join("\n", TokenFields);

    /// <summary>已編譯的樣式在不在（false＝還沒用過或已釋放；下一次遮蔽時重建）</summary>
    internal static bool IsCompiled => Volatile.Read(ref compiled) != null;

    /// <summary>預熱：已編譯的樣式不在就先建好（桌面在有回合或有人觀看時於引擎執行緒呼叫，第一則即時更新不必等建置）</summary>
    internal static void EnsureCompiled() => _ = Current();

    /// <summary>
    /// 丟掉已編譯的樣式（桌面閒置時，WP4.9 步 5；MyriagentHub 的閒置修剪）：正在遮蔽的呼叫照它手上的快照做完，之後的呼叫重建；可以和遮蔽並行呼叫
    /// </summary>
    internal static void ReleaseCompiledPatterns() => Interlocked.Exchange(ref compiled, null);

    /// <summary>
    /// 取已編譯樣式的快照（不在就建；兩條執行緒同時建時只留先放進去的那一份）。每一次公開的呼叫都走到這裡（伺服器逐字串呼叫），
    /// 所以除了 <c>compiled</c> 不寫任何共用狀態（量測與閒置的判斷在呼叫端，WP4.9 合併前審查 DLP-1）
    /// </summary>
    private static CompiledPatterns Current()
    {
        var current = Volatile.Read(ref compiled);
        if (current != null)
            return current;
        var fresh = new CompiledPatterns(Definitions);
        return Interlocked.CompareExchange(ref compiled, fresh, null) ?? fresh;
    }

    /// <summary>把疑似權杖的片段換成 <see cref="Replacement"/>；null 與空字串原樣回傳。同一個輸入一定得到同一個輸出（題目 id 靠它在遠端回答時找回原題）</summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? Mask(string? text) => string.IsNullOrEmpty(text) ? text : Mask(Current(), text);

    [return: NotNullIfNotNull(nameof(text))]
    private static string? Mask(CompiledPatterns c, string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        var result = text;
        for (var i = 0; i < Definitions.Length; i++)
            result = Scan(c, i, result, mask: true, depth: 0) ?? result;
        return result;
    }

    /// <summary>
    /// 有沒有疑似權杖（逐字稿命中時暫停同步〔<c>paused</c>〕、檔案預覽命中時拒絕預覽，設計 8.8）。與 <see cref="Mask(string?)"/> 用同一組樣式與檢查
    /// </summary>
    public static bool HasLeak(string? text) => !string.IsNullOrEmpty(text) && HasLeak(Current(), text, depth: 0);

    /// <param name="depth">base64 解碼的層數（解碼後的內容再掃一次時是 1；到 <see cref="MaxDecodeDepth"/> 就不再解碼）</param>
    private static bool HasLeak(CompiledPatterns c, string? text, int depth)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        for (var i = 0; i < Definitions.Length; i++)
        {
            if (Scan(c, i, text, mask: false, depth) != null)
                return true;
        }

        return false;
    }

    /// <summary>
    /// 串流中的文字可以先送的長度（即時更新，docs/sync-protocol.md 14.3）：尾端連續的權杖字元（ASCII 英數字與 <c>-_.+/=~%:\</c>）
    /// 可能是還沒傳完的權杖——有長度下限與固定長度的樣式在還不夠長時比對不到——所以先不送，等後面出現別的字元或持久事件。
    /// 回傳值之前的部分經 <see cref="Mask"/> 後才送（<see cref="MaskLive"/>）
    /// </summary>
    public static int SafeLength(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        var end = text.Length;
        while (end > 0 && IsTokenChar(text[end - 1]))
            end--;
        return end;
    }

    /// <summary>
    /// 即時更新的文字：開頭被截掉的先換掉第一行（<see cref="MaskTruncated"/>），再去掉尾端可能還沒完的權杖片段（<see cref="SafeLength"/>）後遮蔽
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? MaskLive(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        var c = Current();
        var head = MaskTruncated(c, text);
        return Mask(c, head[..SafeLength(head)]);
    }

    /// <summary>
    /// 開頭被截掉的工具輸出（中樞只留尾段、加「…（前 N 字省略）」前綴，AgentHubRules.TrimToolOutput／AppendToolOutput）：切點可能落在權杖中間，
    /// 剩下的片段沒有前綴、也可能不夠長，任何樣式都比對不到——所以前綴之後到第一個換行的那一段整段換成 <see cref="Replacement"/>（沒有換行就換到最後）。
    /// 切在私鑰中間的另外處理：後面先出現結尾標記（沒有開頭標記）的換到結尾標記為止；第一行是標記行的尾巴或空的、下一行是 base64 的
    /// （切在沒有結尾的私鑰的開頭標記裡或剛好在它之後）換到下一個開頭標記或最後。不是截斷過的文字原樣回傳
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? MaskTruncated(string? text)
        => Hub.AgentHubRules.OmittedPrefixLength(text) == 0 ? text : MaskTruncated(Current(), text);

    [return: NotNullIfNotNull(nameof(text))]
    private static string? MaskTruncated(CompiledPatterns c, string? text)
    {
        var prefix = Hub.AgentHubRules.OmittedPrefixLength(text);
        if (prefix == 0)
            return text;
        var newline = text!.IndexOf('\n', prefix);
        var end = newline < 0 ? text.Length : newline;
        var endMarker = c.PrivateKeyEnd.Match(text, prefix);
        var beginMarker = c.PrivateKeyBegin.Match(text, prefix);
        if (endMarker.Success && (!beginMarker.Success || endMarker.Index < beginMarker.Index))
            end = Math.Max(end, endMarker.Index + endMarker.Length);
        else if (IsCutInsideKeyBegin(text, prefix, newline))
            end = beginMarker.Success ? beginMarker.Index : text.Length;
        if (end <= prefix)
            return text;
        return string.Concat(text.AsSpan(0, prefix), Replacement, text.AsSpan(end));
    }

    /// <summary>
    /// 截斷過的短文字（結尾是「…」：對話標題、佇列的顯示文字）：「…」前面連續的權杖字元先去掉（截斷可能切在權杖中間，剩下的不夠長、比對不到），
    /// 再遮蔽。沒有截斷的照 <see cref="Mask"/>
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? MaskCut(string? text)
    {
        if (string.IsNullOrEmpty(text) || text[^1] != '…')
            return Mask(text);
        var body = text[..^1];
        return Mask(Current(), body[..SafeLength(body)] + "…");
    }

    /// <summary>
    /// 工具的輸入與輸出（常是 JSON）：整段是一個 JSON 值時，先遮蔽解碼後的每個字串與屬性名稱（<c>"1\/\/0…"</c> 這種跳脫過的權杖原文比對不到），
    /// 有改到才重新序列化（原本有換行就縮排）；不是 JSON、解析失敗、後面還有別的內容時照一般文字遮蔽。結果一律再經 <see cref="Mask"/>
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? MaskJsonStrings(string? text) => string.IsNullOrEmpty(text) ? text : MaskJsonStrings(Current(), text);

    [return: NotNullIfNotNull(nameof(text))]
    private static string? MaskJsonStrings(CompiledPatterns c, string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        var start = text.AsSpan().TrimStart();
        if (start.Length > 0 && start[0] is '{' or '[' && TryParseJson(text, out var token) && MaskTokens(c, token))
            return Mask(c, token.ToString(text.Contains('\n') ? Formatting.Indented : Formatting.None));
        return Mask(c, text);
    }

    /// <summary>
    /// 要上傳的項目副本（設計 8.8、D43）：每個字串欄位都遮蔽——含題目的 id 與標題、答案的鍵、工具名稱、檔案路徑、「本對話都允許」的說明、
    /// 附件的檔名與路徑、媒體路徑與媒體參照、只在記憶體的欄位。本機的項目不動。識別碼（GUID、摘要、媒體參照）不會被任何樣式比對到，
    /// 合格的照原樣；題目 id 與答案的鍵、附件 id 與媒體參照的 AttachmentId、MediaPaths 與 MediaRefs 的 MediaPath 經同一個函式遮蔽，
    /// 彼此仍對得起來。存檔裡的 null 清單與 null 元素照樣是 null（投影時再清掉）。
    /// <c>Text</c>、<c>Detail</c>、<c>Output</c> 開頭被截掉的先經 <see cref="MaskTruncated"/>；執行中（串流到一半、持久的檢查點）的項目再去掉尾端還沒完的
    /// 權杖片段（<see cref="SafeLength"/>，同 <see cref="MaskLive"/>：持久事件永久留在伺服器，之後完整的版本遮了也收不回來）。
    /// 差異逐行處理，私鑰開頭標記到結尾標記之間的每一行（不論 add、del、ctx、hunk）整行換掉（<see cref="MaskDiff"/>）。
    /// 接著呼叫 <see cref="ConversationWire.TryItem"/>（WP4.3）
    /// </summary>
    public static AgentChatItem MaskItem(AgentChatItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var c = Current();
        var running = item.Status == AgentItemStatus.Running;
        return new AgentChatItem
        {
            Id = Mask(c, item.Id),
            Kind = item.Kind,
            Status = item.Status,
            Text = MaskText(c, item.Text, running),
            ToolName = Mask(c, item.ToolName),
            ToolKind = Mask(c, item.ToolKind),
            Title = Mask(c, item.Title),
            Detail = MaskText(c, item.Detail, running),
            Output = MaskText(c, item.Output, running),
            Diff = MaskDiff(c, item.Diff),
            FilePath = Mask(c, item.FilePath),
            Attachments = item.Attachments?.Select(a => MaskAttachment(c, a)).ToList()!,
            Questions = item.Questions?.Select(q => MaskQuestion(c, q)).ToList()!,
            Answers = MaskAnswers(c, item.Answers)!,
            Decision = Mask(c, item.Decision),
            CanAllowForSession = item.CanAllowForSession,
            AllowSessionLabel = Mask(c, item.AllowSessionLabel),
            AnswerAsMessage = item.AnswerAsMessage,
            MediaPaths = item.MediaPaths?.Select(p => Mask(c, p)).ToList()!,
            PlanSteps = item.PlanSteps?.Select(s => s == null ? null! : new AgentPlanStep { Text = Mask(c, s.Text), Status = Mask(c, s.Status) }).ToList()!,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt,
            ProcessInstance = Mask(c, item.ProcessInstance),
            NoticeKind = item.NoticeKind,
            Author = Mask(c, item.Author),
            ResolvedBy = Mask(c, item.ResolvedBy),
            CliUuid = Mask(c, item.CliUuid),
            Ord = item.Ord,
            RequestDigest = Mask(c, item.RequestDigest),
            Risk = item.Risk,
            MediaRefs = item.MediaRefs?.Select(r => r == null ? null! : new AgentMediaRef { Ref = Mask(c, r.Ref), AttachmentId = Mask(c, r.AttachmentId), MediaPath = Mask(c, r.MediaPath) }).ToList(),
            RequestKey = Mask(c, item.RequestKey),
            Seq = item.Seq,
        };
    }

    /// <summary>項目的長文字欄位：開頭截斷的第一行、執行中的尾端，再照 <see cref="MaskJsonStrings(string?)"/></summary>
    [return: NotNullIfNotNull(nameof(text))]
    private static string? MaskText(CompiledPatterns c, string? text, bool running)
    {
        var head = MaskTruncated(c, text);
        if (running && head != null)
            head = head[..SafeLength(head)];
        return MaskJsonStrings(c, head);
    }

    /// <summary>
    /// 差異逐行遮蔽：私鑰一行一行存（Write 的新檔、apply_patch 的增刪），單行的樣式只認得開頭那一行。開頭標記那一行照 <see cref="Mask"/>
    /// （從標記換到行尾），之後到結尾標記為止（含）的每一行整行換成 <see cref="Replacement"/>，沒有結尾就換到最後；同一行開了又關的不算
    /// </summary>
    private static List<AgentDiffLine>? MaskDiff(CompiledPatterns c, List<AgentDiffLine>? diff)
    {
        if (diff == null)
            return null;
        var result = new List<AgentDiffLine>(diff.Count);
        var inKey = false;
        foreach (var d in diff)
        {
            if (d == null)
            {
                result.Add(null!);
                continue;
            }

            string? s;
            if (inKey)
            {
                s = d.S == null ? null : Replacement;
                if (d.S != null && c.PrivateKeyEnd.IsMatch(d.S))
                    inKey = false;
            }
            else
            {
                s = Mask(c, d.S);
                inKey = d.S != null && OpensPrivateKey(c, d.S);
            }

            result.Add(new AgentDiffLine(Mask(c, d.T), s!));
        }

        return result;
    }

    /// <summary>這一行有私鑰的開頭標記，而且最後一個開頭標記之後沒有結尾標記（區塊延續到下一行）</summary>
    private static bool OpensPrivateKey(CompiledPatterns c, string line)
    {
        Match? last = null;
        for (var m = c.PrivateKeyBegin.Match(line); m.Success; m = m.NextMatch())
            last = m;
        return last != null && !c.PrivateKeyEnd.Match(line, last.Index + last.Length).Success;
    }

    /// <summary>
    /// 開頭截斷的輸出切在私鑰的開頭標記裡或剛好在它之後：第一行（前綴之後）是空的、或只有標記行的字元而且以 <c>-</c> 結尾，
    /// 下一行開頭是至少 40 個 base64 字元、大寫小寫數字都有（PEM 本體的第一行是 64／70 字；路徑、雜湊、一般的字很少這樣）
    /// </summary>
    private static bool IsCutInsideKeyBegin(string text, int prefix, int newline)
    {
        if (newline < 0)
            return false;
        var first = text.AsSpan(prefix, newline - prefix).TrimEnd('\r');
        if (!first.IsEmpty && (first[^1] != '-' || first.IndexOfAnyExcept(MarkerChars) >= 0))
            return false;
        var next = text.IndexOf('\n', newline + 1);
        var second = text.AsSpan(newline + 1, (next < 0 ? text.Length : next) - newline - 1);
        var run = second.IndexOfAnyExcept(KeyBodyChars);
        var body = run < 0 ? second : second[..run];
        return body.Length >= 40 && body.IndexOfAnyInRange('A', 'Z') >= 0 && body.IndexOfAnyInRange('a', 'z') >= 0 && body.IndexOfAnyInRange('0', '9') >= 0;
    }

    private static AgentAttachment MaskAttachment(CompiledPatterns c, AgentAttachment a) => a == null ? null! : new AgentAttachment
    {
        Id = Mask(c, a.Id),
        FileName = Mask(c, a.FileName),
        StoredPath = Mask(c, a.StoredPath),
        Kind = a.Kind,
        SizeBytes = a.SizeBytes,
        ModelImagePaths = a.ModelImagePaths?.Select(p => Mask(c, p)).ToList()!,
        FrameSeconds = a.FrameSeconds?.ToList()!,
        DurationSeconds = a.DurationSeconds,
        Waveform = Mask(c, a.Waveform),
    };

    private static AgentQuestion MaskQuestion(CompiledPatterns c, AgentQuestion q) => q == null ? null! : new AgentQuestion
    {
        Id = Mask(c, q.Id),
        Header = Mask(c, q.Header),
        Text = Mask(c, q.Text),
        Options = q.Options?.Select(o => o == null ? null! : new AgentQuestionOption { Label = Mask(c, o.Label), Description = Mask(c, o.Description), Preview = Mask(c, o.Preview) }).ToList()!,
        MultiSelect = q.MultiSelect,
        AllowOther = q.AllowOther,
        IsSecret = q.IsSecret,
    };

    /// <summary>答案的鍵（Claude 的題目 id 就是題目文字）與值都遮蔽；兩個鍵遮蔽後相同時值依原順序併在一起（不丟答案）</summary>
    private static Dictionary<string, List<string>>? MaskAnswers(CompiledPatterns c, Dictionary<string, List<string>>? answers)
    {
        if (answers == null)
            return null;
        var masked = new Dictionary<string, List<string>>(answers.Count, StringComparer.Ordinal);
        foreach (var (key, values) in answers)
        {
            var maskedValues = values?.Select(v => Mask(c, v)).ToList();
            var maskedKey = Mask(c, key);
            if (masked.TryGetValue(maskedKey, out var existing) && existing != null && maskedValues != null)
                existing.AddRange(maskedValues);
            else if (!masked.ContainsKey(maskedKey))
                masked[maskedKey] = maskedValues!;
        }

        return masked;
    }

    /// <summary>
    /// 以一個樣式掃一遍：<paramref name="mask"/> 時回傳遮蔽後的文字（沒有命中回 null），否則第一個命中就回原文（當成「有」）、沒有回 null。
    /// 樣式比對到前綴＋最短的本體之後，結尾延伸到本體字元結束；同一段本體只延伸一次（記住上一次延伸到哪裡），整體是線性時間。
    /// 有比對之後的判斷（<see cref="PatternDefinition.Finish"/>）的，由它決定要換掉的範圍；它拒絕時從它給的位置接著找（那一段裡不會再有命中，不重掃）。
    /// <paramref name="index"/> 是樣式在 <see cref="Definitions"/> 裡的位置（已編譯的 Regex 在 <paramref name="c"/> 的同一個位置）
    /// </summary>
    private static string? Scan(CompiledPatterns c, int index, string text, bool mask, int depth)
    {
        var pattern = Definitions[index];
        var regex = c.Regexes[index];
        StringBuilder? output = null;
        var copied = 0;
        var position = 0;
        var runFrom = -1;
        var runEnd = -1;
        while (position <= text.Length)
        {
            var match = regex.Match(text, position);
            if (!match.Success)
                break;
            var secret = pattern.SecretGroup == null ? match : match.Groups[pattern.SecretGroup];
            var start = secret.Index;
            var end = secret.Index + secret.Length;
            if (pattern.Extend is { } extend)
            {
                if (runFrom < 0 || end < runFrom || end > runEnd)
                {
                    runFrom = end;
                    var rest = text.AsSpan(end).IndexOfAnyExcept(extend);
                    runEnd = rest < 0 ? text.Length : end + rest;
                }

                end = runEnd;
            }

            if (pattern.Finish is { } finish)
            {
                var finished = finish(c, text, match.Index, start, end, depth);
                if (!finished.Accept)
                {
                    position = Math.Max(match.Index + 1, finished.End);
                    continue;
                }

                start = finished.Start;
                end = finished.End;
            }

            if (!Accepts(pattern, text, match.Index, end))
            {
                position = match.Index + 1;
                continue;
            }

            if (!mask)
                return text;
            output ??= new StringBuilder(text.Length);
            output.Append(text, copied, start - copied).Append(Replacement);
            copied = end;
            position = Math.Max(end, match.Index + 1);
        }

        if (output == null)
            return null;
        return output.Append(text, copied, text.Length - copied).ToString();
    }

    private static bool Accepts(PatternDefinition pattern, string text, int start, int end)
    {
        if (pattern.PrefixBoundary && start > 0 && char.IsAsciiLetterOrDigit(text[start - 1]))
            return false;
        if (pattern.ExactLength is { } length && (end - start != length || end < text.Length && char.IsAsciiLetterOrDigit(text[end])))
            return false;
        return true;
    }

    /// <summary>可能是權杖一部分的字元（<see cref="SafeLength"/>）：所有樣式的權杖本體都只用這些</summary>
    private static bool IsTokenChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '+' or '/' or '=' or '~' or '%' or ':' or '\\';

    /// <summary>樣式的定義（不建 Regex；已編譯的由 <see cref="CompiledPatterns"/> 依這份建）</summary>
    private static PatternDefinition[] BuildDefinitions()
    {
        const string alnum = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        const string url = alnum + "-_";
        const string upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var printable = new string(Enumerable.Range(0x21, 0x7E - 0x21 + 1).Select(c => (char)c).ToArray());

        PatternDefinition Prefix(string name, string regex, string extend)
            => new(name, regex, Options, extend, PrefixBoundary: true, ExactLength: null, SecretGroup: null);

        PatternDefinition Value(string name, string regex, string excluded)
            => new(name, regex, Options | RegexOptions.IgnoreCase, new string(printable.Where(c => !excluded.Contains(c)).ToArray()),
                PrefixBoundary: false, ExactLength: null, SecretGroup: "s");

        return
        [
            // 私鑰區塊：有結尾的整塊換掉；剩下沒有結尾的（串流中、被截斷的輸出）從開頭換到最後（「任意字元」的迴圈不受上面說的慢影響）
            new("privateKey", @"-----BEGIN[ A-Z0-9]{0,64}PRIVATE KEY(?: BLOCK)?-----[\s\S]*?-----END[ A-Z0-9]{0,64}PRIVATE KEY(?: BLOCK)?-----", Options,
                null, PrefixBoundary: false, ExactLength: null, SecretGroup: null),
            new("privateKeyUnterminated", @"-----BEGIN[ A-Z0-9]{0,64}PRIVATE KEY(?: BLOCK)?-----[\s\S]*", Options,
                null, PrefixBoundary: false, ExactLength: null, SecretGroup: null),

            // 欄位名稱＋值：只換值。值只收可見的 ASCII（權杖都是 ASCII）：遮蔽過的值（Replacement 不含 ASCII）不會再被當成權杖，
            // 伺服器的第二道 DLP 看到已遮蔽的副本不會誤判。字元類別以 \x 寫範圍（跳脫字元當範圍起點時 .NET 不當成範圍）
            Value("jsonTokenField",
                @"""(?:(?:access|refresh|id|session|auth)_?token|(?:[a-z0-9]{1,32}_)?api_?key|client_?secret)""\s{0,64}:\s{0,64}""(?<s>[\x21\x23-\x7E]{8})", "\""),
            // 跳脫過的 JSON（rg 印出的 jsonl 行、字串化的 JSON）：\"name\":\"value\"，值遇到反斜線就停（保留 \" 結尾）
            Value("jsonTokenFieldEscaped",
                @"\\""(?:(?:access|refresh|id|session|auth)_?token|(?:[a-z0-9]{1,32}_)?api_?key|client_?secret)\\""\s{0,64}:\s{0,64}\\""(?<s>[\x21\x23-\x5B\x5D-\x7E]{8})", "\"\\"),
            Value("tokenParameter", @"(?:access|refresh|id)_token=(?<s>[\x21\x23-\x25\x28-\x7E]{8})", "\"&'"),
            // 沒有 JSON 引號的寫法（PowerShell 的 fl、YAML、Python 的 dict、環境變數）：名稱一定有 _ 或 - 分隔（accessToken 這種程式變數不算），
            // 值只收權杖字元、≥16 字（getToken();、Optional[str]、string; 都不會被當成值）
            new("tokenFieldLoose", @"(?:access|refresh|id|session)[_-]token[""']?\s{0,64}[:=]\s{0,64}[""']?(?<s>[A-Za-z0-9._~+/=\-]{16})", Options | RegexOptions.IgnoreCase,
                alnum + "._~+/=-", PrefixBoundary: true, ExactLength: null, SecretGroup: "s"),
            // AWS 的秘密金鑰（存取金鑰 ID 本身不是秘密，秘密在這裡）：~/.aws/credentials、環境變數、sts／export-credentials 的 JSON
            new("awsSecretKey", @"(?:aws_?)?secret_?access_?key\\?[""']?\s{0,64}[:=]\s{0,64}\\?[""']?(?<s>[A-Za-z0-9/+=]{40})", Options | RegexOptions.IgnoreCase,
                alnum + "/+=", PrefixBoundary: false, ExactLength: null, SecretGroup: "s"),
            new("awsSessionToken", @"aws_?session_?token\\?[""']?\s{0,64}[:=]\s{0,64}\\?[""']?(?<s>[A-Za-z0-9/+=]{16})", Options | RegexOptions.IgnoreCase,
                alnum + "/+=", PrefixBoundary: false, ExactLength: null, SecretGroup: "s"),
            Value("bearer", @"Bearer\s{1,64}(?<s>[A-Za-z0-9._~+/=\-]{16})", "\"!#$%&'()*,:;<>?@[\\]^`{|}"),
            Value("myriagentHubCookie", @"__Host-myriagenthub(?:C[0-9]{1,4})?=(?<s>[\x21\x23-\x26\x28-\x2B\x2D-\x3A\x3C-\x7E])", "\"',;"),

            // F10（WP4.3）：HTTP 標頭 Authorization（Proxy-Authorization 也是）的 Basic／Token／Bearer 值，≥8 字（Basic 的帳號密碼很短）；
            // 冒號或等號前後可以有引號與空白（標頭、JSON、Python 的 dict、PowerShell 的雜湊表）
            new("authorizationHeader",
                @"Authorization[""']?\s{0,64}[:=]\s{0,64}[""']?\s{0,64}(?:Basic|Token|Bearer)\s{1,64}(?<s>[A-Za-z0-9_.~+/=\-]{8})", Options | RegexOptions.IgnoreCase,
                alnum + "_.~+/=-", PrefixBoundary: false, ExactLength: null, SecretGroup: "s", Finish: FinishAuthorization),
            // 網址的 userinfo：樣式認到 scheme:// 與 authority 的第一個字，之後在程式裡找 authority 裡最後一個 @（FinishUrlUserinfo）
            new("urlUserinfo", @"[a-z][a-z0-9+.\-]{1,15}://(?<s>[A-Za-z0-9\-._~!$&()*+,;=:%@\[\]])", Options | RegexOptions.IgnoreCase,
                null, PrefixBoundary: false, ExactLength: null, SecretGroup: "s", Finish: FinishUrlUserinfo),
            // git 的 http.extraheader（含 http.<url>.extraheader、.gitconfig 裡 [http] 段的 extraheader）：值像「標頭名稱:」才算，整個值換掉
            new("gitExtraHeader", @"extraheader[""']?(?:\s{0,64}=\s{0,64}|\s{1,64})[""']?(?<s>[A-Za-z][A-Za-z0-9\-]{0,64}:)", Options | RegexOptions.IgnoreCase,
                null, PrefixBoundary: true, ExactLength: null, SecretGroup: "s", Finish: FinishExtraHeader),

            // 前綴夠獨特、不設長度下限
            Prefix("anthropic", @"sk-ant-[A-Za-z0-9]{1,16}-", url),
            Prefix("openAiScoped", @"sk-(?:proj|svcacct|admin)-", url),
            Prefix("githubFineGrained", @"github_pat_", alnum + "_"),

            // 有長度下限：樣式只認最短的長度，之後延伸
            Prefix("github", @"gh[pousr]_[A-Za-z0-9]{20}", alnum),
            Prefix("slack", @"xox[a-z]-[A-Za-z0-9\-]{10}", alnum + "-"),
            Prefix("googleAccess", @"ya29\.[A-Za-z0-9_\-]{20}", url),
            Prefix("googleRefresh", @"1//0[A-Za-z0-9_\-]{20}", url),
            Prefix("xai", @"xai-[A-Za-z0-9]{20}", alnum),
            Prefix("myriagentHubToken", @"ch[da]_[A-Za-z0-9_\-]{16}", url),
            Prefix("openAiLegacy", @"sk-[A-Za-z0-9]{40}", alnum),

            // 固定長度：延伸之後長度剛好、前後都不接英數字才算（比對之後檢查，NonBacktracking 不支援環視）
            new("awsAccessKey", @"(?:AKIA|ASIA)[0-9A-Z]{16}", Options, upper, PrefixBoundary: true, ExactLength: 4 + 16, SecretGroup: null),
            new("googleApiKey", @"AIza[0-9A-Za-z_\-]{35}", Options, url, PrefixBoundary: true, ExactLength: 4 + 35, SecretGroup: null),

            // JWT（三段；第二段是 JSON 物件，一定以 eyJ 開頭）：認到第二段的開頭，延伸涵蓋第二段的其餘、點與簽章
            new("jwt", @"eyJ[A-Za-z0-9_\-]{10,1024}\.eyJ[A-Za-z0-9_\-]{10}", Options, alnum + "-_.",
                PrefixBoundary: false, ExactLength: null, SecretGroup: null),

            // F10（WP4.3）：base64 包起來的憑證檔（kubeconfig、Secret 的 data、cat auth.json | base64、PowerShell 的 -EncodedCommand）：
            // 樣式只認 16 個連續的 base64 字元，片段的範圍、折行、解碼與判斷在程式裡（FinishBase64）。放在最後：前面的樣式先換掉看得懂的部分
            new("base64Credential", @"[A-Za-z0-9+/]{16}", Options, null, PrefixBoundary: false, ExactLength: null, SecretGroup: null, Finish: FinishBase64),
        ];
    }

    /// <summary>
    /// Authorization 的值：沒有數字、而且含底線或沒有小寫字母的（<c>YOUR_TOKEN</c>、<c>your_api_key</c>、<c>PLACEHOLDER</c>）是說明文字的佔位，不算
    /// </summary>
    private static Finished FinishAuthorization(CompiledPatterns c, string text, int matchStart, int secretStart, int secretEnd, int depth)
    {
        var value = text.AsSpan(secretStart, secretEnd - secretStart);
        var placeholder = value.IndexOfAnyInRange('0', '9') < 0 && (value.Contains('_') || value.IndexOfAnyInRange('a', 'z') < 0);
        return placeholder ? Finished.Skip(matchStart + 1) : Finished.Take(secretStart, secretEnd);
    }

    /// <summary>
    /// 網址的 userinfo（<c>scheme://user:password@host</c>）：authority 到 <c>/ ? #</c>、空白、引號或非 ASCII 為止，userinfo 到裡面最後一個 <c>@</c> 為止
    /// （沒有跳脫的 <c>@</c> 也包進密碼）；沒有 <c>@</c> 就不是。使用者名稱像權杖（<see cref="IsTokenLikeUser"/>）時整段 userinfo 換掉，否則只換密碼；
    /// 只有使用者名稱（沒有密碼）、或密碼是佔位（<see cref="IsPasswordPlaceholder"/>）的不算。不算時跳到 authority 的結尾（裡面不會再有 <c>://</c>）
    /// </summary>
    private static Finished FinishUrlUserinfo(CompiledPatterns c, string text, int matchStart, int secretStart, int secretEnd, int depth)
    {
        var rest = text.AsSpan(secretStart).IndexOfAnyExcept(AuthorityChars);
        var runEnd = rest < 0 ? text.Length : secretStart + rest;
        var at = text.AsSpan(secretStart, runEnd - secretStart).LastIndexOf('@');
        if (at < 0)
            return Finished.Skip(runEnd);
        var userinfo = text.AsSpan(secretStart, at);
        var colon = userinfo.IndexOf(':');
        if (IsTokenLikeUser(colon < 0 ? userinfo : userinfo[..colon]))
            return Finished.Take(secretStart, secretStart + at);
        if (colon < 0 || IsPasswordPlaceholder(userinfo[(colon + 1)..]))
            return Finished.Skip(runEnd);
        return Finished.Take(secretStart + colon + 1, secretStart + at);
    }

    /// <summary>像權杖的使用者名稱（git 的 <c>https://&lt;權杖&gt;@github.com</c>）：≥20 個英數字、底線、連字號，字母與數字都有</summary>
    private static bool IsTokenLikeUser(ReadOnlySpan<char> user)
        => user.Length >= 20 && user.IndexOfAnyExcept(TokenUserChars) < 0
           && (user.IndexOfAnyInRange('A', 'Z') >= 0 || user.IndexOfAnyInRange('a', 'z') >= 0) && user.IndexOfAnyInRange('0', '9') >= 0;

    /// <summary>密碼的佔位：空的、全是 <c>*</c>（git 遮過的）、<c>$</c> 開頭（環境變數）、<c>%</c> 後面不是兩個十六進位字（<c>%s</c> 這類格式字串）</summary>
    private static bool IsPasswordPlaceholder(ReadOnlySpan<char> password)
        => password.IsEmpty || password.IndexOfAnyExcept('*') < 0 || password[0] == '$'
           || (password[0] == '%' && !(password.Length >= 3 && char.IsAsciiHexDigit(password[1]) && char.IsAsciiHexDigit(password[2])));

    /// <summary>http.extraheader 的值：前面是引號的換到同一個引號（或行尾）為止，沒有引號的換到行尾</summary>
    private static Finished FinishExtraHeader(CompiledPatterns c, string text, int matchStart, int secretStart, int secretEnd, int depth)
    {
        var quote = secretStart > matchStart && text[secretStart - 1] is '"' or '\'' ? text[secretStart - 1] : '\0';
        var value = text.AsSpan(secretStart);
        var stop = quote == '\0' ? value.IndexOfAny('\r', '\n') : value.IndexOfAny(quote, '\r', '\n');
        return Finished.Take(secretStart, stop < 0 ? text.Length : secretStart + stop);
    }

    /// <summary>
    /// base64 片段（<see cref="Base64Run"/>）：至少 <see cref="MinBase64Chars"/> 個 base64 字元才解碼（最多看開頭的 <see cref="MaxDecodeBytes"/>），
    /// 解碼後的內容（UTF-8；PowerShell 的 -EncodedCommand 是 UTF-16LE）含權杖欄位名稱、或有疑似權杖時整段換掉；不是時跳到片段的結尾。
    /// 片段的開頭未必對齊（網址的路徑 <c>/v1/secret/</c>、前面黏著的英數字也是 base64 字元）：四種對齊都解解看，對齊錯的只有開頭幾個位元組是亂碼
    /// </summary>
    private static Finished FinishBase64(CompiledPatterns c, string text, int matchStart, int secretStart, int secretEnd, int depth)
    {
        var decode = depth < MaxDecodeDepth;
        var collected = decode ? new StringBuilder() : null;
        var end = Base64Run(text, matchStart, collected, out var count);
        if (!decode || count < MinBase64Chars)
            return Finished.Skip(end);

        var body = collected!.ToString();
        var bytes = new byte[body.Length / 4 * 3];
        for (var offset = 0; offset < 4 && body.Length - offset >= 4; offset++)
        {
            if (!Convert.TryFromBase64Chars(body.AsSpan(offset, (body.Length - offset) / 4 * 4), bytes, out var written))
                continue;
            var decoded = DecodeText(bytes.AsSpan(0, written));
            if (decoded.AsSpan().ContainsAny(TokenFieldSearch) || HasLeak(c, decoded, depth + 1))
                return Finished.Take(matchStart, end);
        }

        return Finished.Skip(end);
    }

    /// <summary>
    /// 從 <paramref name="start"/> 起的 base64 片段的結尾：base64 本體字元連續的一段，結尾可帶 1～2 個 <c>=</c>（帶了就結束）；
    /// 行尾接換行、下一行（去掉開頭的空白）整行都是本體（可帶結尾的 <c>=</c>）而且不比第一行寬時接下去（折行；比第一行短的是最後一行）。
    /// <paramref name="count"/> 是本體與 <c>=</c> 的字數（不含換行與縮排）；<paramref name="body"/> 收開頭至多 <see cref="MaxDecodeChars"/> 個本體字元（不含 <c>=</c>）
    /// </summary>
    private static int Base64Run(string text, int start, StringBuilder? body, out int count)
    {
        count = 0;
        var width = -1;
        var i = start;
        while (true)
        {
            var rest = text.AsSpan(i).IndexOfAnyExcept(Base64Body);
            var segmentEnd = rest < 0 ? text.Length : i + rest;
            var length = segmentEnd - i;
            if (body != null && body.Length < MaxDecodeChars)
                body.Append(text, i, Math.Min(length, MaxDecodeChars - body.Length));
            count += length;
            i = segmentEnd;

            var padding = 0;
            while (i < text.Length && text[i] == '=' && padding < 2)
            {
                i++;
                padding++;
            }

            count += padding;
            if (padding > 0)
                return i;
            if (width < 0)
                width = length;
            else if (length < width)
                return i;

            // 折行：換行（\n 或 \r\n）、縮排，下一行整行都是本體（可帶結尾的 =）而且不比第一行寬
            var next = i;
            if (next < text.Length && text[next] == '\r')
                next++;
            if (next >= text.Length || text[next] != '\n')
                return i;
            next++;
            while (next < text.Length && text[next] is ' ' or '\t')
                next++;
            var lineRest = text.AsSpan(next).IndexOfAnyExcept(Base64Body);
            var lineEnd = lineRest < 0 ? text.Length : next + lineRest;
            var tail = lineEnd;
            while (tail < text.Length && text[tail] == '=' && tail - lineEnd < 2)
                tail++;
            if (lineEnd == next || lineEnd - next > width || (tail < text.Length && text[tail] is not ('\r' or '\n')))
                return i;
            i = next;
        }
    }

    /// <summary>解碼後的位元組當成文字：開頭有 UTF-16LE 的 BOM、或前兩個字元都是 ASCII 的 UTF-16LE（高位元組是 0）時以 UTF-16LE 解，否則 UTF-8（不合格的換成 U+FFFD）</summary>
    private static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.Length >= 4 && bytes[0] != 0 && bytes[1] == 0 && bytes[2] != 0 && bytes[3] == 0)
            return Encoding.Unicode.GetString(bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    private static bool TryParseJson(string text, [NotNullWhen(true)] out JToken? token)
    {
        token = null;
        try
        {
            using var reader = new JsonTextReader(new StringReader(text))
            {
                DateParseHandling = DateParseHandling.None,
                FloatParseHandling = FloatParseHandling.Decimal,
                MaxDepth = MaxJsonDepth,
            };
            var loaded = JToken.ReadFrom(reader);
            if (reader.Read())
                return false;
            token = loaded;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>就地遮蔽 JSON 裡的字串值與屬性名稱；有改到回 true。遮蔽後同名的屬性改加 #2、#3 區分（不丟值）</summary>
    private static bool MaskTokens(CompiledPatterns c, JToken token)
    {
        switch (token)
        {
            case JValue { Type: JTokenType.String } value:
            {
                var text = (string?)value.Value;
                var masked = Mask(c, text);
                if (masked == text)
                    return false;
                value.Value = masked;
                return true;
            }

            case JArray array:
            {
                var changed = false;
                foreach (var child in array)
                    changed |= MaskTokens(c, child);
                return changed;
            }

            case JObject obj:
            {
                var changed = false;
                foreach (var property in obj.Properties().ToList())
                {
                    changed |= MaskTokens(c, property.Value);
                    var name = Mask(c, property.Name);
                    if (name == property.Name)
                        continue;
                    var unique = name;
                    for (var n = 2; obj.ContainsKey(unique); n++)
                        unique = name + "#" + n;
                    property.Replace(new JProperty(unique, property.Value));
                    changed = true;
                }

                return changed;
            }

            default:
                return false;
        }
    }

    /// <summary>一個樣式的定義（不含已編譯的 Regex，那個在 <see cref="CompiledPatterns"/>）</summary>
    /// <param name="Source">正規式的原文（編譯時照原樣傳給 Regex；指紋也用它）</param>
    /// <param name="Options">Regex 的選項（一律含 NonBacktracking）</param>
    /// <param name="ExtendChars">比對之後把結尾延伸到這些字元結束（權杖的本體）；null＝樣式本身就是整段</param>
    /// <param name="PrefixBoundary">前一個字元不能是 ASCII 英數字</param>
    /// <param name="ExactLength">延伸之後整段要剛好這麼長、後一個字元也不能是 ASCII 英數字</param>
    /// <param name="SecretGroup">只換這個群組（欄位名稱＋值類；群組在樣式的最後，延伸的是它）；null＝整段換掉</param>
    /// <param name="Finish">比對與延伸之後的判斷（F10）：決定換不換、換哪一段；null＝照比對（與延伸）的範圍換</param>
    private sealed record PatternDefinition(string Name, string Source, RegexOptions Options, string? ExtendChars, bool PrefixBoundary, int? ExactLength, string? SecretGroup, Finisher? Finish = null)
    {
        /// <summary><see cref="ExtendChars"/> 的搜尋表（很小，跟著定義留著）</summary>
        public SearchValues<char>? Extend { get; } = ExtendChars == null ? null : SearchValues.Create(ExtendChars);
    }

    /// <summary>
    /// 已編譯的一整組樣式：私鑰區塊的開頭與結尾標記，與 <see cref="Definitions"/> 同順序的每個樣式的 Regex。建好之後不再改，
    /// 可以同時給多條執行緒用（Regex 的比對是執行緒安全的）；丟掉參照就整組（含 NonBacktracking 的快取）一起被回收
    /// </summary>
    private sealed class CompiledPatterns
    {
        public CompiledPatterns(PatternDefinition[] definitions)
        {
            PrivateKeyBegin = new Regex(PrivateKeyBeginSource, Options);
            PrivateKeyEnd = new Regex(PrivateKeyEndSource, Options);
            Regexes = Array.ConvertAll(definitions, d => new Regex(d.Source, d.Options));
        }

        public Regex PrivateKeyBegin { get; }

        public Regex PrivateKeyEnd { get; }

        /// <summary>與 <see cref="Definitions"/> 同順序</summary>
        public Regex[] Regexes { get; }
    }

    /// <summary>
    /// 比對之後的判斷：<paramref name="matchStart"/> 是整個比對的開頭，<paramref name="secretStart"/>～<paramref name="secretEnd"/> 是要換的群組（延伸之後）；
    /// <paramref name="depth"/> 是 base64 解碼的層數；<paramref name="c"/> 是這次呼叫的已編譯樣式（解碼後再掃一次時用同一份）
    /// </summary>
    private delegate Finished Finisher(CompiledPatterns c, string text, int matchStart, int secretStart, int secretEnd, int depth);

    /// <summary>判斷的結果：<see cref="Accept"/> 時換掉 <see cref="Start"/>～<see cref="End"/>；不換時從 <see cref="End"/> 接著找（那一段裡不會再有命中）</summary>
    private readonly record struct Finished(bool Accept, int Start, int End)
    {
        public static Finished Take(int start, int end) => new(true, start, end);

        public static Finished Skip(int resumeAt) => new(false, resumeAt, resumeAt);
    }
}
