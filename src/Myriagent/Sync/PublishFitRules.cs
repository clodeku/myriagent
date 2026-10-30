using System.Globalization;
using System.Text;
using Myriagent.Agents;
using Myriagent.Hub;

namespace Myriagent.Sync;

/// <summary>
/// 要上傳的項目放進對話事件的上限（WP4.3 計畫 D16）：媒體同步（WP4.7）之前沒有 blob 可用，太大的欄位一律截斷，
/// 完整內容留在擁有者電腦（<c>published_items.truncated</c> 記一筆，WP4.7 協商出 <c>media1</c> 後重送）。
/// 截斷在權杖 DLP（<see cref="CredentialLeakRules.MaskItem"/>）**之前**做，切點交給 DLP 的既有規則處理：
/// <list type="bullet">
/// <item><c>text</c>、<c>detail</c>、<c>title</c> 留開頭：切點前連續的權杖字元先去掉（<see cref="CredentialLeakRules.SafeLength"/>：
/// 切點可能落在權杖中間，剩下的片段不夠長、比對不到），再加 <see cref="HeadMarker"/>。</item>
/// <item><c>output</c> 留結尾：加中樞的「…（前 N 字省略）」前綴（<see cref="AgentHubRules.OmittedPrefix"/>，原本已截斷過的字數累計），
/// <see cref="CredentialLeakRules.MaskTruncated"/> 把切點所在的第一行整行換掉。</item>
/// <item><c>diff</c>、<c>plan</c> 留前面幾個元素，最後補一個說明元素（同 <see cref="HeadMarker"/>）；放不下的第一個元素照 <c>text</c> 截。</item>
/// </list>
/// 欄位的上限是原文的 UTF-8 位元組（對到共用驗證的內嵌字串上限 <see cref="ConversationEventRules.MaxInlineStringBytes"/>，留遮蔽變長的餘裕）；
/// 整筆的上限以 JSON 跳脫後的位元組估計（偏大）。代理字元對一律不切開。每次截斷都從原本的內容重算（說明裡的字數一定是對原文的）。
/// 引擎在 <c>Validate</c> 仍嫌大時把預算減半重試（最多 <see cref="MaxHalvings"/> 次），再不行換成 <see cref="Oversized"/> 的佔位。純函式
/// </summary>
internal static class PublishFitRules
{
    /// <summary>每個欄位（原文 UTF-8）的上限</summary>
    public const int FieldBudgetBytes = 30 * 1024;

    /// <summary>整筆（JSON 跳脫後的估計）的上限</summary>
    public const int ItemBudgetBytes = 120 * 1024;

    /// <summary><c>Validate</c> 仍嫌大時預算減半重試的次數上限</summary>
    public const int MaxHalvings = 3;

    /// <summary>太大而換成佔位的項目的文字</summary>
    public const string OversizedText = "這個項目太大，未同步（完整內容在擁有者電腦）";

    /// <summary>可截斷的欄位（線上的名稱；<c>truncated</c> 依這個順序回報）</summary>
    public static IReadOnlyList<string> TruncatableFields { get; } = ["text", "detail", "title", "output", "diff", "plan"];

    /// <summary>估計大小時每個清單元素與欄位的 JSON 外框（鍵名、引號、逗號）的保守值</summary>
    private const int ElementOverhead = 48;

    /// <summary>估計整筆大小時固定欄位（id、ord、ver、kind、status、時間、布林值）的保守值</summary>
    private const int ItemOverhead = 1024;

    /// <summary>清單截斷時，放不下的第一個元素至少要能留這麼多位元組的內容才截進去（否則只補說明）</summary>
    private const int MinPartialBytes = 64;

    /// <summary>
    /// 截斷：先把超過 <paramref name="fieldBudget"/> 的欄位截掉，整筆仍超過 <paramref name="itemBudget"/> 時從最大的欄位開始再截，
    /// 直到放得下或每個欄位都截不動。沒有截斷時回傳原本的實例；有截斷時回傳新的實例（<see cref="ChatItemCopyRules.Copy"/>），原本的不動
    /// </summary>
    /// <param name="truncated">截斷了哪些欄位（<see cref="TruncatableFields"/> 的名稱與順序）</param>
    public static AgentChatItem Truncate(AgentChatItem item, int fieldBudget, int itemBudget, out IReadOnlyList<string> truncated)
    {
        ArgumentNullException.ThrowIfNull(item);
        var fields = new Fields(item);
        foreach (var name in TruncatableFields)
        {
            if (fields.Size(name) > fieldBudget)
                fields.Shrink(name, fieldBudget);
        }

        var stuck = new HashSet<string>(StringComparer.Ordinal);
        var total = Estimate(fields);
        while (total > itemBudget)
        {
            string? largest = null;
            var largestSize = 0L;
            foreach (var name in TruncatableFields)
            {
                var size = fields.Size(name);
                if (!stuck.Contains(name) && size > largestSize)
                {
                    largest = name;
                    largestSize = size;
                }
            }

            if (largest == null)
                break;
            var target = Math.Max(0, Math.Min(largestSize - 1, largestSize - (total - itemBudget)));
            if (!fields.Shrink(largest, (int)Math.Min(target, int.MaxValue)))
                stuck.Add(largest);
            total = Estimate(fields);
        }

        truncated = TruncatableFields.Where(fields.Changed).ToList();
        if (truncated.Count == 0)
            return item;
        var copy = ChatItemCopyRules.Copy(item);
        fields.ApplyTo(copy);
        return copy;
    }

    /// <summary>開頭留下、後面截掉時接在最後的說明（N 是省略的字數，UTF-16 單位，同中樞的「前 N 字省略」）</summary>
    public static string HeadMarker(long omittedChars)
        => string.Create(CultureInfo.InvariantCulture, $"…（後 {omittedChars:N0} 字未同步，完整內容在擁有者電腦）");

    /// <summary>
    /// 預算減半到底仍放不下時的佔位：只保留 Id、Kind、Status、Ord 與時間，文字換成 <see cref="OversizedText"/>，其他內容都不帶
    /// （題目、答案、差異、附件都沒有：遠端看不到、也不能回應這張卡片）
    /// </summary>
    public static AgentChatItem Oversized(AgentChatItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new AgentChatItem
        {
            Id = item.Id,
            Kind = item.Kind,
            Status = item.Status,
            Ord = item.Ord,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt,
            Text = OversizedText,
        };
    }

    /// <summary>
    /// 整筆的估計（JSON 跳脫後的 UTF-8 位元組，偏大）：會上線的字串欄位與清單元素，每個加上外框，再加固定欄位。
    /// 只在本機的欄位（附件的存放路徑、給模型看的圖、程序實例、媒體參照）不算
    /// </summary>
    internal static long EstimateBytes(AgentChatItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Estimate(new Fields(item));
    }

    private static long Estimate(Fields fields)
    {
        var item = fields.Item;
        long total = ItemOverhead;
        foreach (var name in TruncatableFields)
            total += fields.JsonSize(name);
        total += JsonBytes(item.ToolName) + JsonBytes(item.ToolKind) + JsonBytes(item.FilePath) + JsonBytes(item.Decision)
                 + JsonBytes(item.AllowSessionLabel) + JsonBytes(item.RequestDigest);
        foreach (var a in item.Attachments ?? new List<AgentAttachment>())
            total += a == null ? 0 : JsonBytes(a.FileName) + JsonBytes(a.Id) + ElementOverhead;
        foreach (var q in item.Questions ?? new List<AgentQuestion>())
        {
            if (q == null)
                continue;
            total += JsonBytes(q.Id) + JsonBytes(q.Header) + JsonBytes(q.Text) + ElementOverhead;
            foreach (var o in q.Options ?? new List<AgentQuestionOption>())
                total += o == null ? 0 : JsonBytes(o.Label) + JsonBytes(o.Description) + JsonBytes(o.Preview) + ElementOverhead;
        }

        foreach (var (key, values) in item.Answers ?? new Dictionary<string, List<string>>())
        {
            total += JsonBytes(key) + ElementOverhead;
            foreach (var v in values ?? new List<string>())
                total += JsonBytes(v) + 4;
        }

        foreach (var path in item.MediaPaths ?? new List<string>())
            total += JsonBytes(path) + ElementOverhead;
        return total;
    }

    /// <summary>字串在 JSON 裡的位元組（偏大：控制字元一律當成 \uXXXX、引號與反斜線兩個、U+0085／U+2028／U+2029 當成 \uXXXX）</summary>
    private static long JsonBytes(string? s)
    {
        if (s == null)
            return 0;
        long bytes = 2;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c < 0x20 || c == 0x85 || c == 0x2028 || c == 0x2029)
                bytes += 6;
            else if (c is '"' or '\\')
                bytes += 2;
            else if (c < 0x80)
                bytes += 1;
            else if (c < 0x800)
                bytes += 2;
            else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                bytes += 4;
                i++;
            }
            else
                bytes += 3;
        }

        return bytes;
    }

    private static long Utf8Bytes(string? s) => s == null ? 0 : Encoding.UTF8.GetByteCount(s);

    /// <summary>一個字元（或代理字元對）的寬度與 UTF-8 位元組；單獨的代理字元以 3 位元組計（編碼器換成 U+FFFD）</summary>
    private static (int Width, int Bytes) CharAt(string s, int i)
    {
        var c = s[i];
        if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            return (2, 4);
        return (1, c < 0x80 ? 1 : c < 0x800 ? 2 : 3);
    }

    /// <summary>從頭留下不超過 <paramref name="maxBytes"/> 位元組的字數（不切開代理字元對）</summary>
    private static int HeadChars(string s, long maxBytes)
    {
        long bytes = 0;
        var i = 0;
        while (i < s.Length)
        {
            var (width, size) = CharAt(s, i);
            if (bytes + size > maxBytes)
                break;
            bytes += size;
            i += width;
        }

        return i;
    }

    /// <summary>從尾巴留下不超過 <paramref name="maxBytes"/> 位元組的起點（不切開代理字元對）</summary>
    private static int TailStart(string s, long maxBytes)
    {
        long bytes = 0;
        var i = s.Length;
        while (i > 0)
        {
            var pair = i >= 2 && char.IsLowSurrogate(s[i - 1]) && char.IsHighSurrogate(s[i - 2]);
            var size = pair ? 4 : CharAt(s, i - 1).Bytes;
            if (bytes + size > maxBytes)
                break;
            bytes += size;
            i -= pair ? 2 : 1;
        }

        return i;
    }

    /// <summary>
    /// 留開頭：切點前連續的權杖字元去掉、加說明，整段（含說明）不超過 <paramref name="budget"/>；說明本身就放不下時只剩說明。
    /// 本來就放得下的照原樣
    /// </summary>
    private static string CutHead(string s, long budget)
    {
        if (Utf8Bytes(s) <= budget)
            return s;
        var keep = HeadChars(s, Math.Max(0, budget - Utf8Bytes(HeadMarker(s.Length))));
        var safe = CredentialLeakRules.SafeLength(s[..keep]);
        return s[..safe] + HeadMarker(s.Length - safe);
    }

    /// <summary>留結尾：加中樞的「…（前 N 字省略）」前綴（原本已截斷過的，省略字數累計），整段不超過 <paramref name="budget"/>。本來就放得下的照原樣</summary>
    private static string CutTail(string s, long budget)
    {
        if (Utf8Bytes(s) <= budget)
            return s;
        var prefixLength = AgentHubRules.OmittedPrefixLength(s);
        var omitted = AgentHubRules.OmittedCount(s);
        var body = s[prefixLength..];
        var start = TailStart(body, Math.Max(0, budget - Utf8Bytes(AgentHubRules.OmittedPrefix(omitted + body.Length))));
        return AgentHubRules.OmittedPrefix(omitted + start) + body[start..];
    }

    /// <summary>
    /// 清單留前面：依序放得下的元素整個留下；第一個放不下的元素，剩下的預算夠的話把文字照 <see cref="CutHead"/> 的作法截進去（不另加說明），
    /// 最後補一個說明元素（省略的字數＝截掉的文字的字數）。null 元素照樣保留、不算大小。整份放得下的照原樣（同一個實例）
    /// </summary>
    private static List<T>? CutList<T>(List<T>? list, long budget, Func<T, long> sizeOf, Func<T, string?> textOf, Func<T, string, T> withText, Func<string, T> marker)
        where T : class
    {
        if (list == null || ListSize(list, sizeOf) <= budget)
            return list;
        var markerBytes = Utf8Bytes(HeadMarker(list.Sum(e => (long)(e == null ? 0 : textOf(e)?.Length ?? 0)))) + ElementOverhead;
        var room = Math.Max(0, budget - markerBytes);
        var result = new List<T>();
        long used = 0;
        var index = 0;
        for (; index < list.Count; index++)
        {
            var element = list[index];
            var size = element == null ? 0 : sizeOf(element);
            if (used + size > room)
                break;
            result.Add(element!);
            used += size;
        }

        long omitted = 0;
        var first = list[index];
        var firstText = textOf(first);
        var partialRoom = room - used - (sizeOf(first) - Utf8Bytes(firstText));
        if (firstText != null && partialRoom >= MinPartialBytes)
        {
            var safe = CredentialLeakRules.SafeLength(firstText[..HeadChars(firstText, partialRoom)]);
            result.Add(withText(first, firstText[..safe]));
            omitted += firstText.Length - safe;
            index++;
        }

        for (; index < list.Count; index++)
            omitted += list[index] == null ? 0 : textOf(list[index])?.Length ?? 0;
        result.Add(marker(HeadMarker(omitted)));
        return result;
    }

    private static long ListSize<T>(List<T>? list, Func<T, long> sizeOf)
        where T : class
        => list?.Sum(e => e == null ? 0 : sizeOf(e)) ?? 0;

    private static long DiffSize(AgentDiffLine d) => Utf8Bytes(d.T) + Utf8Bytes(d.S) + ElementOverhead;

    private static long PlanSize(AgentPlanStep p) => Utf8Bytes(p.Text) + Utf8Bytes(p.Status) + ElementOverhead;

    /// <summary>
    /// 可截斷欄位的工作副本：截斷一律從原本的內容以新的預算重算，採用時才換掉目前的值；最後一次寫進複本（原本的項目不動）
    /// </summary>
    private sealed class Fields(AgentChatItem item)
    {
        public AgentChatItem Item { get; } = item;

        private readonly Dictionary<string, object?> current = new(StringComparer.Ordinal)
        {
            ["text"] = item.Text,
            ["detail"] = item.Detail,
            ["title"] = item.Title,
            ["output"] = item.Output,
            ["diff"] = item.Diff,
            ["plan"] = item.PlanSteps,
        };

        /// <summary>欄位的原文 UTF-8 位元組（清單是每個元素的字串合計加外框）</summary>
        public long Size(string name) => SizeOf(name, current[name]);

        /// <summary>欄位在 JSON 裡的估計位元組</summary>
        public long JsonSize(string name) => current[name] switch
        {
            string s => JsonBytes(s),
            List<AgentDiffLine> diff => diff.Sum(d => d == null ? 4 : JsonBytes(d.T) + JsonBytes(d.S) + ElementOverhead),
            List<AgentPlanStep> plan => plan.Sum(p => p == null ? 4 : JsonBytes(p.Text) + JsonBytes(p.Status) + ElementOverhead),
            _ => 0,
        };

        /// <summary>這個欄位有沒有被截斷（和原本的內容不是同一個值）</summary>
        public bool Changed(string name) => !ReferenceEquals(current[name], Original(name));

        /// <summary>從原本的內容截到 <paramref name="budget"/> 以內；比目前的小才採用、回 true（截不動時回 false）</summary>
        public bool Shrink(string name, long budget)
        {
            object? candidate = name switch
            {
                "text" => Item.Text == null ? null : CutHead(Item.Text, budget),
                "detail" => Item.Detail == null ? null : CutHead(Item.Detail, budget),
                "title" => Item.Title == null ? null : CutHead(Item.Title, budget),
                "output" => Item.Output == null ? null : CutTail(Item.Output, budget),
                "diff" => CutList(Item.Diff, budget, DiffSize, d => d.S, (d, s) => d with { S = s }, s => new AgentDiffLine("ctx", s)),
                "plan" => CutList(Item.PlanSteps, budget, PlanSize, p => p.Text, (p, s) => new AgentPlanStep { Text = s, Status = p.Status }, s => new AgentPlanStep { Text = s, Status = "pending" }),
                _ => throw new ArgumentOutOfRangeException(nameof(name), name, "不是可截斷的欄位"),
            };
            if (SizeOf(name, candidate) >= Size(name))
                return false;
            current[name] = candidate;
            return true;
        }

        public void ApplyTo(AgentChatItem copy)
        {
            copy.Text = (string)current["text"]!;
            copy.Detail = (string?)current["detail"];
            copy.Title = (string?)current["title"];
            copy.Output = (string?)current["output"];
            copy.Diff = (List<AgentDiffLine>?)current["diff"];
            copy.PlanSteps = (List<AgentPlanStep>)current["plan"]!;
        }

        private object? Original(string name) => name switch
        {
            "text" => Item.Text,
            "detail" => Item.Detail,
            "title" => Item.Title,
            "output" => Item.Output,
            "diff" => Item.Diff,
            "plan" => Item.PlanSteps,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "不是可截斷的欄位"),
        };

        private static long SizeOf(string name, object? value) => value switch
        {
            string s => Utf8Bytes(s),
            List<AgentDiffLine> diff => ListSize(diff, DiffSize),
            List<AgentPlanStep> plan => ListSize(plan, PlanSize),
            null => 0,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "不是可截斷的欄位"),
        };
    }
}
