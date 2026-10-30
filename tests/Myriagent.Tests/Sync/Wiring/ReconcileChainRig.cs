using System.Security.Cryptography;
using System.Text;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.Tests.Hub;
using Myriagent.Tests.Sync.Engine;
using Myriagent.Tests.Sync.Fakes;
using Xunit.Sdk;

namespace Myriagent.Tests.Sync.Wiring;

/// <summary>
/// 重整與回補的整條串接（WP4.3 計畫步 12：D10 與 S19 桌面端）：真的中樞（<see cref="HubHarness"/>，對話預先存在暫存的對話資料夾、項目沒載入）＋
/// 真的發布者＋同步引擎＋照 WP4.2 定案模擬的假伺服器。發布者與引擎共用一個可撥的時鐘。要放在 <c>AppPathsCollection</c> 裡（中樞改了 AppPaths）
/// </summary>
internal sealed class ReconcileChainRig : IAsyncDisposable
{
    public static readonly DateTime T0 = new(2026, 10, 16, 9, 0, 0, DateTimeKind.Local);

    /// <param name="seeds">中樞啟動之前存進對話資料夾的對話（WP4.3 之前的格式：項目沒有 Ord、meta 沒有 NextOrd）</param>
    public ReconcileChainRig(IReadOnlyList<(AgentConversation Meta, List<AgentChatItem> Items)> seeds)
    {
        Seeds = seeds;
        Hub = new HubHarness(seedStore: store =>
        {
            foreach (var (meta, items) in seeds)
            {
                store.SaveConversation(meta);
                store.SaveItems(meta.Id, items);
            }
        });
        E = new EngineHarness(conversations: Box);
        E.Server.Features = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1];
        _ = E.Server.Conversations;
        E.OpenStore = (path, id) =>
        {
            var store = SyncStore.Open(path, id);
            store.FreeDiskBytesForTest = long.MaxValue;
            return store;
        };
    }

    public IReadOnlyList<(AgentConversation Meta, List<AgentChatItem> Items)> Seeds { get; }

    public HubHarness Hub { get; }

    public EngineHarness E { get; }

    public ConversationChangeBox Box { get; } = new();

    public ConversationPublisher Publisher { get; private set; } = null!;

    public FakeConversations Model => E.Server.Conversations;

    /// <summary>一個舊格式的對話（Claude 主帳戶；項目沒有 Ord）</summary>
    public static (AgentConversation Meta, List<AgentChatItem> Items) Seed(DateTime updatedAt, int count, string title = "舊對話")
    {
        var meta = new AgentConversation
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = title,
            AccountId = "acc-claude",
            Provider = AgentProvider.Claude,
            CreatedAt = updatedAt.AddHours(-1),
            UpdatedAt = updatedAt,
        };
        var items = new List<AgentChatItem>(count);
        for (var i = 0; i < count; i++)
        {
            items.Add(new AgentChatItem
            {
                Kind = i % 2 == 0 ? AgentItemKind.User : AgentItemKind.Assistant,
                Status = AgentItemStatus.Done,
                Text = $"{title} 第 {i} 則。",
                CreatedAt = updatedAt,
                UpdatedAt = updatedAt,
            });
        }

        return (meta, items);
    }

    /// <summary>引擎建好、啟動之前掛上發布者（盒子的 UI 端），再把它掛到中樞上</summary>
    public async Task StartAsync()
    {
        await E.StartAsync(engine => Publisher = Hub.OnUi(() => ConversationPublisher.TryCreate(Hub.Hub, engine, E.Time))!);
        Hub.Ui.Invoke(() => Hub.Hub.AttachConversationSink(Publisher));
        await SettleAsync();
    }

    /// <summary>UI 執行緒與引擎輪流做完手上的工作</summary>
    public async Task SettleAsync()
    {
        for (var i = 0; i < 3; i++)
        {
            await Hub.Ui.DrainAsync();
            await E.IdleAsync();
        }
    }

    /// <summary>一秒一秒撥快，直到重整與回補做完、outbox 的列都完成</summary>
    public async Task DrainAsync(int maxSeconds = 600)
    {
        for (var i = 0; i < maxSeconds; i++)
        {
            await E.Time.AdvanceAsync(TimeSpan.FromSeconds(1), SettleAsync);
            await SettleAsync();
            if (Box.Count == 0 && await E.Inspect(v => v.ReconcileQueue.Count == 0 && v.ActiveReconcile is null && !v.Refreshing && v.Outbox.All(e => e.IsDone)))
                return;
        }

        throw new XunitException($"{maxSeconds} 秒（測試時鐘）內重整與回補沒有做完");
    }

    public string DirOf(string conversationId) => Hub.Store.GetConversationDirectory(conversationId);

    /// <summary>照中樞第一次準備發布時的規則（沒有旁檔、沒有 NextOrd、沒有發布紀錄）會配到的 Ord</summary>
    public static Dictionary<string, long> Planned(IReadOnlyList<AgentChatItem> items)
    {
        var plan = OrdAssignRules.PlanLoad(items.Select(i => (i.Id, i.Ord)).ToList(), null, new OrdReservation(null, false), null);
        return items.Select((item, index) => (item.Id, Ord: plan.Ords[index])).ToDictionary(p => p.Id, p => p.Ord, StringComparer.Ordinal);
    }

    /// <summary>資料夾裡每個檔的相對路徑、最後寫入時間與內容的 SHA-256（唯讀的證明：什麼都沒寫，連同內容的重寫也算）</summary>
    public static string FolderDigest(string directory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(directory, file) + "|" + File.GetLastWriteTimeUtc(file).Ticks + "\n"));
            hash.AppendData(File.ReadAllBytes(file));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>載入對話、以 sync.db 的發布紀錄（hint）準備 Ord（同引擎要快照時），回傳中樞的 id → Ord</summary>
    public async Task<Dictionary<string, long>> LoadAndPrepareAsync(string conversationId)
    {
        var hint = await E.Inspect(v => v.Store!.OrdHintOf(conversationId));
        return Hub.OnUi(() =>
        {
            Hub.Hub.GetItems(conversationId);
            Hub.Hub.PrepareOrdsForPublish(conversationId, hint).Should().BeTrue("Ord 準備得了");
            return Hub.Hub.GetItems(conversationId).ToDictionary(i => i.Id, i => i.Ord!.Value, StringComparer.Ordinal);
        });
    }

    public async ValueTask DisposeAsync()
    {
        await E.DisposeAsync();
        Hub.Dispose();
    }
}
