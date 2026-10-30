using Myriagent.Hub;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FakeDevice;

/// <summary>控制通道（<c>run --control-stdin</c>）解析過的一個指令：<paramref name="Id"/> 是呼叫端給的 id（回覆原樣帶回；沒給是 null）</summary>
internal sealed record FakeControlCommand(JToken? Id, string Op, JObject Args);

/// <summary>一行的解析結果：成功時 <see cref="Command"/> 不是 null；失敗時 <see cref="Error"/> 是原因，<see cref="Id"/>、<see cref="Op"/> 盡量取出來（回覆照樣帶）</summary>
internal sealed record FakeControlParse(FakeControlCommand? Command, JToken? Id, string? Op, string? Error);

/// <summary>
/// 假裝置的控制通道與劇本裡同名 op 的純函式規則（WP4.9 步 11）：一行一個 JSON 指令 <c>{"id":…,"op":"…",參數…}</c> 的解析與參數檢查（未知的 op、
/// 未知的參數鍵、型別與範圍都在這裡擋下，執行端不必再猜）、回覆的格式、路徑的檢查（XSS 語料檔只收 repo 的 Golden 資料夾與 TEMP 底下的 .json；
/// 回收區一定在資料根底下；放 XSS 檔名的資料夾在 TEMP 底下、資料根以外），以及 Windows 合法的檔名子集。執行在 <see cref="FakeControl"/>
/// </summary>
internal static class FakeControlRules
{
    /// <summary>
    /// 控制通道認得的 op（劇本另外認得這些，quit 除外）。fakeCodexComplete、fakeCodexCalls（WP4.9 步 13）要以 <c>--fake-codex</c> 啟動：
    /// 寫假 app-server 的 complete.flag、讀它的 calls.txt。unpair（步 17）在 run 的途中解除配對（回傳匯出的遠端操作紀錄）
    /// </summary>
    public static IReadOnlyList<string> Ops { get; } =
    [
        "status", "stream", "nextRemoteTurn", "setTitle", "setPolicy", "ords", "remoteAudit", "xssFill", "restoreFromTrash", "fakeCodexComplete", "fakeCodexCalls",
        "localCommand", "replicas", "unpair", "quit",
    ];

    /// <summary>
    /// localCommand（WP4.9 步 16）收的本機檢視指令：這台的使用者在桌面上做的事，經中樞的 HandleCommand（同 app 的主視窗）——
    /// 開關對話（複本要開著才拉項目）、改名與送出（對複本是寫入，中樞擋下並提示唯讀）、這個對話的同步政策（只能在桌面改）、暫停同步（斷線＝電腦離線）。
    /// 其他指令不收：假裝置的控制通道只做全流程用得到的，不當成任意的中樞指令入口
    /// </summary>
    public static IReadOnlyList<string> LocalCommands { get; } = ["open", "close", "rename", "send", "syncSetConversationPolicy", "syncPause"];

    /// <summary>xssFill 放得進去的欄位</summary>
    public static IReadOnlyList<string> XssFields { get; } = ["title", "deviceName", "accountLabel", "text", "tool", "question", "approval", "fileNames"];

    /// <summary>
    /// 假裝置放不進去的欄位（通知的標題、toast、fileRef 的錯誤文字都由中樞或伺服器產生）：可以要求，結果記在 <c>uncovered</c>，由 conv-probe 與伺服器的測試覆蓋
    /// </summary>
    public static IReadOnlyList<string> XssUncoverableFields { get; } = ["notifyTitle", "toast", "fileRefError"];

    /// <summary>遠端操作政策（同網頁的 syncSetPolicy）</summary>
    public static IReadOnlyList<string> PolicyLevels { get; } = ["Off", "View", "Control"];

    /// <summary>一行最多幾個字元（劇本整份放在 nextRemoteTurn 裡也夠）</summary>
    public const int MaxLineChars = 4_000_000;

    /// <summary>remoteAudit 一次最多幾列</summary>
    public const int MaxAuditLimit = 500;

    /// <summary>xssFill 一次最多幾筆語料</summary>
    public const int MaxXssCount = 10_000;

    /// <summary>XSS 檔名（不含序號前綴與副檔名）最多幾個 UTF-16 字元</summary>
    public const int MaxFileNameStem = 60;

    private static readonly JsonSerializerSettings JsonSettings = new() { DateParseHandling = DateParseHandling.None };

    /// <summary>解析一行；空白行、不是 JSON 物件、id 不是整數或字串、沒有 op、參數不合格都回 <see cref="FakeControlParse.Error"/></summary>
    public static FakeControlParse Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return new FakeControlParse(null, null, null, "空白的一行");
        if (line.Length > MaxLineChars)
            return new FakeControlParse(null, null, null, $"一行最多 {MaxLineChars} 個字元");
        JToken? token;
        try
        {
            token = JsonConvert.DeserializeObject<JToken>(line, JsonSettings);
        }
        catch (JsonException ex)
        {
            return new FakeControlParse(null, null, null, "不是 JSON：" + ex.Message);
        }

        if (token is not JObject json)
            return new FakeControlParse(null, null, null, "每一行要是一個 JSON 物件 {\"op\":…}");
        var id = json["id"]?.DeepClone();
        if (id is not null && id.Type is not (JTokenType.Integer or JTokenType.String or JTokenType.Null))
            return new FakeControlParse(null, null, null, "id 要是整數或字串");
        if (json["op"] is not JValue { Type: JTokenType.String, Value: string op } || op.Length == 0)
            return new FakeControlParse(null, id, null, "缺少 op");
        var args = (JObject)json.DeepClone();
        args.Remove("id");
        args.Remove("op");
        var error = Validate(op, args);
        return error is null ? new FakeControlParse(new FakeControlCommand(id, op, args), id, op, null) : new FakeControlParse(null, id, op, error);
    }

    /// <summary>參數的檢查（不含 op 與 id 本身）：只收這個 op 認得的鍵、型別與範圍；合格回 null</summary>
    public static string? Validate(string op, JObject args) => op switch
    {
        "status" or "quit" or "fakeCodexComplete" or "fakeCodexCalls" or "unpair" => Keys(args),
        "stream" => Keys(args, "conversation", "account", "message", "text", "segments", "delayMs", "stamp", "completeEachSegment", "wait", "seconds")
                    ?? Text(args, "conversation", required: true) ?? Text(args, "account") ?? Text(args, "message") ?? Text(args, "text")
                    ?? Int(args, "segments", 1, ScriptedTurn.MaxSegments) ?? Int(args, "delayMs", 0, ScriptedTurn.MaxDelayMs)
                    ?? Bool(args, "stamp") ?? Bool(args, "completeEachSegment") ?? Bool(args, "wait") ?? Int(args, "seconds", 1, 3600),
        "nextRemoteTurn" => Keys(args, "turn") ?? Turn(args),
        "setTitle" => Keys(args, "conversation", "title") ?? Text(args, "conversation", required: true) ?? Text(args, "title", required: true),
        "setPolicy" => Keys(args, "level", "fullAccess", "notify") ?? Level(args) ?? Bool(args, "fullAccess") ?? Bool(args, "notify"),
        "ords" => Keys(args, "conversation") ?? Text(args, "conversation", required: true),
        "remoteAudit" => Keys(args, "beforeAt", "beforeSeq", "limit") ?? Long(args, "beforeAt") ?? Long(args, "beforeSeq") ?? Int(args, "limit", 1, MaxAuditLimit)
                         ?? (Present(args, "beforeSeq") && !Present(args, "beforeAt") ? "beforeSeq 要和 beforeAt 一起給（上一頁最後一列的 at 與 seq）" : null),
        "xssFill" => Keys(args, "payloadFile", "fields", "from", "count", "conversation", "account", "folder") ?? XssFill(args),
        "restoreFromTrash" => Keys(args, "conversation") ?? Text(args, "conversation", required: true)
                              ?? (ConversationTrashRules.IsTrashableId(args.Value<string>("conversation"))
                                  ? null
                                  : "conversation 要是 32 個十六進位字元的對話 id（回收區只收 app 建立的資料夾名稱）"),
        "localCommand" => Keys(args, "cmd", "args") ?? Text(args, "cmd", required: true) ?? LocalCommand(args),
        "replicas" => Keys(args, "conversation") ?? Text(args, "conversation")
                      ?? (Present(args, "conversation") && !ConversationTrashRules.IsTrashableId(args.Value<string>("conversation"))
                          ? "conversation 要是 32 個十六進位字元的對話 id（複本的 id 是線上的 id）"
                          : null),
        _ => $"不認得的 op：{op}（可用：{string.Join("、", Ops)}）",
    };

    /// <summary>localCommand：cmd 在 <see cref="LocalCommands"/> 裡；args（可省略）是物件、不能有 cmd（指令名稱只看外層）</summary>
    private static string? LocalCommand(JObject args)
    {
        var cmd = args.Value<string>("cmd")!;
        if (!LocalCommands.Contains(cmd, StringComparer.Ordinal))
            return $"cmd 不收 {cmd}（可用：{string.Join("、", LocalCommands)}）";
        return args["args"] switch
        {
            null or { Type: JTokenType.Null } => null,
            JObject body when body.ContainsKey("cmd") => "args 不能有 cmd（指令名稱只看外層的 cmd）",
            JObject => null,
            _ => "args 要是物件（中樞指令的參數，例如 {\"conversationId\":…}）",
        };
    }

    /// <summary>xssFill 要放的欄位（已通過 <see cref="Validate"/>）</summary>
    public static IReadOnlyList<string> XssFieldsOf(JObject args) => ((JArray)args["fields"]!).Select(f => f.ToString()).ToList();

    private static string? XssFill(JObject args)
    {
        var error = Text(args, "payloadFile", required: true) ?? Int(args, "from", 0, 1_000_000) ?? Int(args, "count", 1, MaxXssCount)
                    ?? Text(args, "conversation") ?? Text(args, "account") ?? Text(args, "folder");
        if (error is not null)
            return error;
        if (args["fields"] is not JArray fields || fields.Count == 0 || fields.Any(f => f.Type != JTokenType.String))
            return "fields 要是非空的字串陣列";
        var names = fields.Select(f => f.ToString()).ToList();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count)
            return "fields 不能重複";
        if (names.FirstOrDefault(n => !XssFields.Contains(n) && !XssUncoverableFields.Contains(n)) is { } unknown)
            return $"fields 不認得 {unknown}（放得進的：{string.Join("、", XssFields)}；放不進、只記未覆蓋的：{string.Join("、", XssUncoverableFields)}）";
        if ((names.Contains("title") || names.Contains("accountLabel")) && !Present(args, "account"))
            return "title（每筆開一個新對話）與 accountLabel 要給 account（帳戶別名）";
        if (names.Contains("fileNames") && !Present(args, "folder"))
            return "fileNames 要給 folder（TEMP 底下、資料根以外的資料夾：資料根在遠端讀檔的禁止清單裡）";
        return null;
    }

    // ───────────────────────────── 回覆 ─────────────────────────────

    /// <summary>成功的回覆：<c>{"id","op","ok":true,"result":{…}}</c></summary>
    public static JObject Ok(JToken? id, string op, JObject result) =>
        new() { ["id"] = id?.DeepClone() ?? JValue.CreateNull(), ["op"] = op, ["ok"] = true, ["result"] = result };

    /// <summary>失敗的回覆：<c>{"id","op","ok":false,"error":"…"}</c>（通道照常讀下一行）</summary>
    public static JObject Fail(JToken? id, string? op, string error) =>
        new() { ["id"] = id?.DeepClone() ?? JValue.CreateNull(), ["op"] = op, ["ok"] = false, ["error"] = error };

    // ───────────────────────────── 路徑 ─────────────────────────────

    /// <summary>
    /// XSS 語料檔：完整的本機路徑（不收 UNC、裝置路徑、替代資料流）、副檔名 .json、在 <paramref name="allowedRoots"/>（repo 的 Golden 資料夾與 TEMP）
    /// 其中一個的底下（以 <see cref="Path.GetFullPath(string)"/> 正規化之後比，<c>..</c> 走出去的不算）；合格回 null
    /// </summary>
    public static string? CheckPayloadFile(string path, IReadOnlyList<string> allowedRoots)
    {
        if (CheckLocalPath(path, "payloadFile") is { } problem)
            return problem;
        var full = Path.GetFullPath(path);
        if (!full.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return $"payloadFile 只收 .json：{path}";
        return allowedRoots.Any(root => IsUnder(full, root))
            ? null
            : $"payloadFile 要在 repo 的 Golden 資料夾或 TEMP 底下（{string.Join("、", allowedRoots)}）：{full}";
    }

    /// <summary>回收區一定在資料根底下（設計:3052；還原只動這台資料根裡的檔）；合格回 null</summary>
    public static string? CheckTrashRoot(string trashRoot, string dataRoot)
    {
        if (CheckLocalPath(trashRoot, "回收區") is { } problem)
            return problem;
        return IsUnder(Path.GetFullPath(trashRoot), dataRoot) ? null : $"回收區 {trashRoot} 不在資料根 {dataRoot} 底下";
    }

    /// <summary>放 XSS 檔名的資料夾：在 TEMP 底下、不在資料根裡（資料根在遠端讀檔的禁止清單裡，放進去 listFiles 一律被拒）；合格回 null</summary>
    public static string? CheckXssFolder(string folder, string tempRoot, string dataRoot)
    {
        if (CheckLocalPath(folder, "folder") is { } problem)
            return problem;
        var full = Path.GetFullPath(folder);
        if (!IsUnder(full, tempRoot))
            return $"folder 要在 TEMP（{tempRoot}）底下：{full}";
        if (IsUnder(full, dataRoot) || SameDirectory(full, dataRoot))
            return $"folder 不能在資料根（{dataRoot}）裡：資料根在遠端讀檔的禁止清單裡";
        return null;
    }

    /// <summary><paramref name="path"/> 在 <paramref name="root"/> 底下（不含 root 本身；兩邊都先正規化，不分大小寫）</summary>
    public static bool IsUnder(string path, string root)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameDirectory(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>完整的本機磁碟路徑：不是空的、不是 UNC 或裝置路徑（<c>\\</c>、<c>//</c> 開頭）、是完整路徑、磁碟代號之後沒有冒號（替代資料流）、沒有萬用字元</summary>
    private static string? CheckLocalPath(string? path, string name)
    {
        if (string.IsNullOrWhiteSpace(path))
            return $"{name} 是空的";
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) || path.StartsWith(@"\/", StringComparison.Ordinal)
            || path.StartsWith(@"/\", StringComparison.Ordinal))
            return $"{name} 不收 UNC 與裝置路徑：{path}";
        if (!Path.IsPathFullyQualified(path))
            return $"{name} 要是完整路徑（例如 C:\\…）：{path}";
        if (path.IndexOf(':', 2) >= 0)
            return $"{name} 不收替代資料流：{path}";
        if (path.IndexOfAny(['*', '?', '<', '>', '|', '"']) >= 0 || path.Any(char.IsControl))
            return $"{name} 有不合法的字元：{path}";
        return null;
    }

    // ───────────────────────────── stream、xssFill 的劇本 ─────────────────────────────

    /// <summary>stream 沒給 segments 時的段數</summary>
    public const int DefaultStreamSegments = 10;

    /// <summary>stream 沒給 delayMs 時每段之前等的毫秒數（桌面的節奏：每個對話每秒最多 4 則，設計:3187）</summary>
    public const int DefaultStreamDelayMs = 250;

    /// <summary>
    /// stream 沒給 text 時的文字：每段一樣長的「第 0001 段。」（7 個字），<see cref="ScriptedSession.Split"/> 切成 <paramref name="segments"/> 段時剛好一段一句；
    /// 數字只有 4 位（不像任何權杖，DLP 不改它）
    /// </summary>
    public static string StreamText(int segments)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(segments, 1);
        var text = new System.Text.StringBuilder(segments * 7);
        for (var k = 1; k <= segments; k++)
            text.Append('第').Append(k.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)).Append("段。");
        return text.ToString();
    }

    /// <summary>stream 沒給 seconds 時的時限：30 秒加上兩倍的串流時間，最多 3600 秒</summary>
    public static int StreamSeconds(int segments, int delayMs) =>
        (int)Math.Clamp(30 + Math.Ceiling(2.0 * segments * delayMs / 1000), 30, 3600);

    /// <summary>
    /// xssFill 的卡片那一輪：每筆語料依序一段代理文字（text）、一個工具（tool：名稱、標題、細節、輸出都是它）、一張問卷（question：題目與唯一的選項是它，
    /// 答這個選項）、一張核准卡（approval：工具名稱、標題、細節是它，拒絕），只放 <paramref name="fields"/> 裡有的；題目 id 是 <c>x{序號}</c>
    /// </summary>
    public static ScriptedTurn XssTurn(IReadOnlyList<string> payloads, int from, IReadOnlyCollection<string> fields)
    {
        var steps = new List<ScriptedStep>();
        for (var i = 0; i < payloads.Count; i++)
        {
            var payload = payloads[i];
            if (fields.Contains("text"))
                steps.Add(new ScriptedText(payload, 1, 0));
            if (fields.Contains("tool"))
                steps.Add(new ScriptedTool(payload, "bash", payload, payload, payload, 1, 0, Failed: false, FilePath: null));
            if (fields.Contains("question"))
            {
                var questionId = $"x{from + i}";
                var question = new Myriagent.Agents.AgentQuestion
                {
                    Id = questionId,
                    Text = payload,
                    Options = [new Myriagent.Agents.AgentQuestionOption { Label = payload }],
                    AllowOther = true,
                };
                steps.Add(new ScriptedQuestion([question], new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [questionId] = [payload] }, Skip: false, 0));
            }

            if (fields.Contains("approval"))
                steps.Add(new ScriptedApproval(payload, "write", payload, payload, "deny", 0));
        }

        if (steps.Count == 0)
            throw new ArgumentException("fields 沒有 text、tool、question、approval 其中一個", nameof(fields));
        return new ScriptedTurn(steps);
    }

    // ───────────────────────────── 檔名 ─────────────────────────────

    /// <summary>
    /// 把一筆 XSS 語料變成 Windows 合法的檔名：<c>{序號:D3}-</c> 前綴（不會撞到 CON、NUL 這類保留名稱，也不會重複）＋語料裡合法的字元
    /// （控制字元與 <c>&lt;&gt;:"/\|?*</c>、落單的代理字元換成底線；最多 <see cref="MaxFileNameStem"/> 個 UTF-16 字元，不切開代理字對）＋<c>.txt</c>。
    /// 放不進的字元（尖括號、引號…）由 conv-probe 覆蓋（計畫風險 19）
    /// </summary>
    public static string SafeFileName(string payload, int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        var stem = new System.Text.StringBuilder();
        for (var i = 0; i < payload.Length && stem.Length < MaxFileNameStem; i++)
        {
            var c = payload[i];
            if (char.IsHighSurrogate(c) && i + 1 < payload.Length && char.IsLowSurrogate(payload[i + 1]))
            {
                if (stem.Length + 2 > MaxFileNameStem)
                    break;
                stem.Append(c).Append(payload[i + 1]);
                i++;
                continue;
            }

            stem.Append(char.IsSurrogate(c) || c < ' ' || c == '\x7f' || "<>:\"/\\|?*".Contains(c) ? '_' : c);
        }

        // 結尾的點與空白 Windows 會吃掉（之後的副檔名讓它們不在結尾，但前綴之後整段是空白時仍留一個底線）
        var text = stem.ToString();
        if (text.Trim().Length == 0)
            text = "_";
        return $"{index:D3}-{text}.txt";
    }

    // ───────────────────────────── 參數 ─────────────────────────────

    private static bool Present(JObject args, string key) => args[key] is { Type: not JTokenType.Null };

    private static string? Keys(JObject args, params string[] allowed)
    {
        var unknown = args.Properties().Select(p => p.Name).Where(n => !allowed.Contains(n, StringComparer.Ordinal)).ToList();
        return unknown.Count == 0 ? null : $"不認得的參數：{string.Join("、", unknown)}" + (allowed.Length == 0 ? "（這個 op 不收參數）" : $"（可用：{string.Join("、", allowed)}）");
    }

    private static string? Text(JObject args, string key, bool required = false) => args[key] switch
    {
        null or { Type: JTokenType.Null } => required ? $"缺少 {key}" : null,
        JValue { Type: JTokenType.String, Value: string text } when text.Length > 0 => null,
        _ => $"{key} 要是非空字串",
    };

    private static string? Bool(JObject args, string key) => args[key] switch
    {
        null or { Type: JTokenType.Null } or { Type: JTokenType.Boolean } => null,
        _ => $"{key} 要是 true 或 false",
    };

    // 超過 long 的整數（JValue 存成 BigInteger）一律不合格
    private static string? Int(JObject args, string key, int min, int max) => args[key] switch
    {
        null or { Type: JTokenType.Null } => null,
        JValue { Type: JTokenType.Integer, Value: long n } when n >= min && n <= max => null,
        _ => $"{key} 要是 {min}～{max} 的整數",
    };

    private static string? Long(JObject args, string key) => args[key] switch
    {
        null or { Type: JTokenType.Null } => null,
        JValue { Type: JTokenType.Integer, Value: long n } when n >= 0 => null,
        _ => $"{key} 要是非負的整數",
    };

    private static string? Level(JObject args) =>
        args["level"] is JValue { Type: JTokenType.String, Value: string level } && PolicyLevels.Contains(level)
            ? null
            : $"level 要是 {string.Join("、", PolicyLevels)} 其中一個";

    private static string? Turn(JObject args)
    {
        if (args["turn"] is not { Type: JTokenType.Array or JTokenType.Object } turn)
            return "turn 要是劇本（步驟陣列，或 {\"steps\":[…],\"outcome\":…}）";
        try
        {
            ScriptedTurn.Parse(turn);
            return null;
        }
        catch (FormatException ex)
        {
            return "turn：" + ex.Message;
        }
    }
}
