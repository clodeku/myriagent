using System.Security.Cryptography;
using System.Text;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Hub;
using Newtonsoft.Json;

namespace FakeDevice;

/// <summary>seedConversations 的參數（格式見 README）</summary>
/// <param name="Count">對話數</param>
/// <param name="OlderThanDays">最新的那個對話比 <paramref name="BaseTimeUtc"/> 早幾天（之後每個再早一小時）</param>
/// <param name="ItemsPerConversation">每個對話的項目數</param>
/// <param name="Seed">id 與內容的亂數種子（<see cref="Random"/>，同一個種子產生同一份）</param>
/// <param name="BaseTimeUtc">時間的基準（UTC；不用 DateTime.Now，重跑才產生同一份）</param>
/// <param name="Account">對話的帳戶別名（<see cref="DeviceRuntime.AccountId"/>；chat 要用這些對話時，先以同一個別名 addAccount）</param>
internal sealed record SeedSpec(int Count, int OlderThanDays, int ItemsPerConversation, int Seed, DateTime BaseTimeUtc, string Account = "seed",
    AgentProvider Provider = AgentProvider.Claude);

/// <summary>寫好的對話：筆數與指紋（SHA-256，與時區無關，見 <see cref="ConversationSeeder.Fingerprint"/>）、依產生順序的 id</summary>
internal sealed record SeedResult(int Conversations, int Items, string Sha256, IReadOnlyList<string> Ids);

/// <summary>
/// 可重現的「N 個舊對話」（WP4.3 計畫步 13）：WP4.3 之前的格式（項目沒有 Ord、meta 沒有 NextOrd、沒有同步政策＝full），在中樞啟動之前寫進
/// 資料根的 conversations\。id 與內容只由種子決定、時間只由基準時間決定（不用 <see cref="DateTime.Now"/>、<see cref="Guid.NewGuid"/>），
/// 同樣的參數在任何電腦、任何時區都產生同一份（指紋相同）。內容是一般的中英文字，沒有任何像憑證的字串（權杖 DLP 不會遮蔽、測得到真正的上傳量）
/// </summary>
internal static class ConversationSeeder
{
    public const int MaxCount = 100_000;

    public const int MaxItemsPerConversation = 10_000;

    public const int MaxOlderThanDays = 3_650;

    /// <summary>每幾則有一則工具（執行指令）</summary>
    private const int ToolEvery = 5;

    private static readonly string[] Words =
    [
        "對話", "同步", "伺服器", "桌面", "回補", "項目", "順序", "測試", "建置", "部署", "設定", "帳戶", "資料夾", "檔案", "畫面", "按鈕",
        "清單", "記錄", "版本", "更新", "重新", "整理", "確認", "完成", "開始", "結束", "等待", "回覆", "問題", "答案", "工具", "輸出",
        "build", "test", "commit", "branch", "review", "outbox", "batch", "cursor", "replica", "policy", "draft", "queue", "render", "golden",
    ];

    /// <summary>指紋用的序列化：時間一律以 UTC 寫（存檔的 JsonFile 轉成本機時區，不同時區的電腦位元組不同）、不縮排、略過 null</summary>
    private static readonly JsonSerializerSettings FingerprintSettings = new()
    {
        Formatting = Formatting.None,
        NullValueHandling = NullValueHandling.Ignore,
        DateTimeZoneHandling = DateTimeZoneHandling.Utc,
    };

    /// <summary>依參數產生對話（不寫檔）；參數不合格丟 <see cref="ArgumentException"/></summary>
    public static IReadOnlyList<(AgentConversation Meta, List<AgentChatItem> Items)> Generate(SeedSpec spec)
    {
        Validate(spec);
        var random = new Random(spec.Seed);
        var accountId = DeviceRuntime.AccountId(spec.Account);
        var newest = spec.BaseTimeUtc.AddDays(-spec.OlderThanDays);
        var result = new List<(AgentConversation, List<AgentChatItem>)>(spec.Count);
        for (var i = 0; i < spec.Count; i++)
        {
            var updated = newest.AddHours(-i);
            var created = updated.AddMinutes(-spec.ItemsPerConversation);
            var title = $"舊對話 {i + 1}";
            var meta = new AgentConversation
            {
                Id = Hex(random),
                Title = title,
                AccountId = accountId,
                Provider = spec.Provider,
                SessionId = new Guid(Bytes(random)).ToString("D"),
                SessionStarted = true,
                SessionAccountId = accountId,
                CreatedAt = created,
                UpdatedAt = updated,
            };
            var items = new List<AgentChatItem>(spec.ItemsPerConversation);
            for (var j = 0; j < spec.ItemsPerConversation; j++)
            {
                var at = created.AddMinutes(j);
                var item = new AgentChatItem
                {
                    Id = Hex(random),
                    Status = AgentItemStatus.Done,
                    CreatedAt = at,
                    UpdatedAt = at,
                };
                if (j % ToolEvery == ToolEvery - 1)
                {
                    item.Kind = AgentItemKind.Tool;
                    item.ToolName = "執行指令";
                    item.ToolKind = "bash";
                    item.Title = $"dotnet test --filter {Words[random.Next(Words.Length)]}";
                    item.Output = Sentence(random, 5, 60);
                }
                else
                {
                    item.Kind = j % 2 == 0 ? AgentItemKind.User : AgentItemKind.Assistant;
                    item.Text = j == 0 ? $"{title}：{Sentence(random, 3, 20)}" : Sentence(random, 3, 80);
                }

                items.Add(item);
            }

            result.Add((meta, items));
        }

        return result;
    }

    /// <summary>
    /// 寫進 <c>{dataRoot}\conversations</c>（中樞啟動之前）。已經有同 id 的對話資料夾就不寫、丟 <see cref="InvalidOperationException"/>
    /// （不覆寫別人的對話；同一個資料根要重灌先刪掉資料根）
    /// </summary>
    public static SeedResult Write(string dataRoot, SeedSpec spec)
    {
        var seeds = Generate(spec);
        var store = new ConversationStore(Path.Combine(dataRoot, "conversations"));
        var existing = seeds.Select(s => s.Meta.Id).FirstOrDefault(id => Directory.Exists(store.GetConversationDirectory(id)));
        if (existing is not null)
            throw new InvalidOperationException($"資料根已經有對話 {existing}（同樣的種子灌過了）：不覆寫");
        foreach (var (meta, items) in seeds)
        {
            store.SaveConversation(meta);
            store.SaveItems(meta.Id, items);
        }

        return Describe(seeds);
    }

    /// <summary>筆數、指紋與 id（不寫檔）</summary>
    public static SeedResult Describe(IReadOnlyList<(AgentConversation Meta, List<AgentChatItem> Items)> seeds) =>
        new(seeds.Count, seeds.Sum(s => s.Items.Count), Fingerprint(seeds), seeds.Select(s => s.Meta.Id).ToList());

    /// <summary>
    /// 指紋：每個對話一行 <c>{"meta":…,"items":[…]}</c>（依產生順序；時間以 UTC 序列化、不縮排、略過 null），整份 UTF-8 的 SHA-256（小寫十六進位）。
    /// 與時區無關；存檔讀回來（時間是本機時區）再算也相同
    /// </summary>
    public static string Fingerprint(IReadOnlyList<(AgentConversation Meta, List<AgentChatItem> Items)> seeds)
    {
        var text = new StringBuilder();
        foreach (var (meta, items) in seeds)
            text.Append(JsonConvert.SerializeObject(new { meta, items }, FingerprintSettings)).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static void Validate(SeedSpec spec)
    {
        if (spec.Count is < 1 or > MaxCount)
            throw new ArgumentException($"count 要在 1～{MaxCount} 之間：{spec.Count}", nameof(spec));
        if (spec.ItemsPerConversation is < 1 or > MaxItemsPerConversation)
            throw new ArgumentException($"itemsPerConversation 要在 1～{MaxItemsPerConversation} 之間：{spec.ItemsPerConversation}", nameof(spec));
        if (spec.OlderThanDays is < 0 or > MaxOlderThanDays)
            throw new ArgumentException($"olderThanDays 要在 0～{MaxOlderThanDays} 之間：{spec.OlderThanDays}", nameof(spec));
        if (spec.BaseTimeUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("baseTimeUtc 要是 UTC 時間", nameof(spec));
        if (string.IsNullOrWhiteSpace(spec.Account))
            throw new ArgumentException("account 不可為空", nameof(spec));
        if (!AccountRules.SupportsIsolatedAccounts(spec.Provider))
            throw new ArgumentException($"{spec.Provider} 沒有隔離帳戶：只能是 Claude、Codex 或 Grok", nameof(spec));
    }

    private static byte[] Bytes(Random random)
    {
        var bytes = new byte[16];
        random.NextBytes(bytes);
        return bytes;
    }

    private static string Hex(Random random) => Convert.ToHexStringLower(Bytes(random));

    private static string Sentence(Random random, int minWords, int maxWords)
    {
        var count = random.Next(minWords, maxWords + 1);
        var text = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
                text.Append(i % 9 == 0 ? "。" : " ");
            text.Append(Words[random.Next(Words.Length)]);
        }

        return text.Append('。').ToString();
    }
}
