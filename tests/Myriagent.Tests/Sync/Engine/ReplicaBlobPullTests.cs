using Myriagent.Agents;
using Myriagent.Media;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Engine;

/// <summary>
/// 引擎拉複本時的 blob 預取（WP4.7 計畫步 12，D39）：協商出 media1 時，解碼一頁之前以 E6 取回其他電腦的大項目欄位，換回內容存進 replica_items；
/// 暫時取不到的那個串流從那筆起停住、游標不前進，30 秒後重拉；這一頁放不下的馬上拉下一頁；永久取不到的照 WP4.3 轉不回來、游標照樣前進。
/// 協商出 media1 時也把複本的媒體來源（E9）掛到盒子上。假的 E6 是 <see cref="FakeBlobEndpoint"/>
/// </summary>
public sealed class ReplicaBlobPullTests
{
    private static readonly DateTime T0 = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Local);

    private static readonly string[] AllFeatures = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1, DeviceFeatureRules.Media1];

    private sealed class Rig : IAsyncDisposable
    {
        public Rig(bool media1 = true)
        {
            Box = new ConversationChangeBox();
            H = new EngineHarness(conversations: Box);
            Ui = new FakeConversationUi(Box, () => H.Engine);
            Box.UiPort = Ui;
            Box.ConversationsRoot = H.Dir.CreateDirectory("conversations");
            H.Server.Features = media1 ? AllFeatures : [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1];
            _ = H.Server.Conversations;
            Blobs = new FakeBlobEndpoint(Relay);
        }

        public EngineHarness H { get; }

        public ConversationChangeBox Box { get; }

        public FakeConversationUi Ui { get; }

        public FakeRelayHandler Relay { get; } = new();

        public FakeBlobEndpoint Blobs { get; }

        public string Other { get; } = SyncIds.NewDeviceId();

        public string Id { get; } = Guid.NewGuid().ToString("N");

        public string Stream => SyncStreams.Conversation(Id);

        /// <summary>每頁取回的 blob 合計上限（啟動前設定）</summary>
        public long PageBytes { get; set; } = ReplicaBlobRules.MaxPageBytes;

        /// <summary>啟動並進 Live；另一台先建立這個對話（created 在項目之前，串流的游標才看得出停在哪一筆）</summary>
        public async Task StartAsync()
        {
            await H.StartAsync(e =>
            {
                e.MediaHttpHandlerForTest = Relay;
                e.MediaGuardForTest = MediaTestFiles.Guard();
                e.ReplicaBlobMaxPageBytesForTest = PageBytes;
            });
            Publish(ConversationEventKinds.Created, ConversationWire.Serialize(new ConversationMetaPayload("Claude", "acc-9", "別台的對話", "Ask", null, null, false, null,
                ConversationSyncPolicies.Full, 1791500000000, 1791500060000, false, false)));
        }

        public long Publish(string kind, string json) => H.Server.PublishConversation(kind, Id, json, Other);

        /// <summary>擁有者的大項目：text、output 超過內嵌上限的換成 blob（照步 10 的發布路徑），blob 放進假的 E6</summary>
        public (long Seq, AgentChatItem Source, IReadOnlyList<string> Shas) PublishBig(string itemId, long ord, int seed, bool bigText = true, bool storeBlobs = true)
        {
            var source = new AgentChatItem
            {
                Id = itemId, Ord = ord, Kind = AgentItemKind.Tool, Status = AgentItemStatus.Done, ToolName = "Bash", Title = "跑測試",
                Text = bigText ? new string((char)('甲' + seed), 20_000) : "短的",
                Output = new string((char)('a' + seed), 40_000),
                CreatedAt = T0, UpdatedAt = T0,
            };
            ConversationWire.TryItem(source, 1, out var wire).Should().BeTrue();
            ConversationEventRules.TryFit(wire!, out var blobs).Should().BeTrue();
            if (storeBlobs)
            {
                foreach (var blob in blobs)
                    Blobs.Put(blob.Content);
            }

            return (Publish(ConversationEventKinds.Item, ConversationWire.Serialize(wire!)), source, blobs.Select(b => b.Sha256).ToList());
        }

        public long PublishSmall(string itemId, long ord, string text)
        {
            var item = new AgentChatItem { Id = itemId, Ord = ord, Kind = AgentItemKind.Assistant, Status = AgentItemStatus.Done, Text = text, CreatedAt = T0, UpdatedAt = T0 };
            ConversationWire.TryItem(item, 1, out var wire).Should().BeTrue();
            return Publish(ConversationEventKinds.Item, ConversationWire.Serialize(wire!));
        }

        /// <summary>另一台的對話出現在清單上，中樞打開它（拉它的串流），等引擎拉完</summary>
        public async Task OpenAsync()
        {
            H.Server.Current!.Notify([SyncClasses.Meta]);
            await H.IdleAsync();
            Ui.Put(new ReplicaRequested(Id));
            await H.IdleAsync();
        }

        public async Task NotifyItemsAsync()
        {
            H.Server.Current!.Notify([SyncClasses.Item]);
            await H.IdleAsync();
        }

        public Task<IReadOnlyList<ReplicaItemRow>> ItemsAsync() => H.Inspect(v => v.Store!.LoadReplicaItems(Id));

        public async Task<long?> CursorAsync() => (await H.Inspect(v => v.Store!.GetConversationCursors())).TryGetValue(Stream, out var seq) ? seq : null;

        /// <summary>依序套用交給中樞的項目變化，得到中樞看到的最後內容（項目 id → 項目）</summary>
        public Dictionary<string, AgentChatItem> UiItems()
        {
            var items = new Dictionary<string, AgentChatItem>(StringComparer.Ordinal);
            foreach (var change in Ui.ReplicaDeltas.SelectMany(d => d.Items).Where(i => i.ConversationId == Id))
            {
                if (change.Reset)
                    items.Clear();
                foreach (var removed in change.Removed)
                    items.Remove(removed);
                foreach (var item in change.Upserts)
                    items[item.Id] = item;
            }

            return items;
        }

        public ValueTask DisposeAsync() => H.DisposeAsync();
    }

    [Fact]
    public async Task 預取_取到全部才解碼_換回內容存進replica_items()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var (seq, source, shas) = rig.PublishBig("i1", 1, 0);
        shas.Should().HaveCount(2);

        await rig.OpenAsync();

        rig.Blobs.Gets.Should().BeEquivalentTo(shas, "一頁兩個 blob，E6 各一次");
        var shown = rig.UiItems().Should().ContainKey("i1").WhoseValue;
        shown.Text.Should().Be(source.Text);
        shown.Output.Should().Be(source.Output);
        var stored = (await rig.ItemsAsync()).Should().ContainSingle().Subject;
        ReplicaBlobRules.Collect((JObject)JToken.Parse(stored.Payload)).Should().BeEmpty("存的是換回內容的 payload：之後打開不必再取");
        ReplicaRules.ToItem(stored.Payload)!.Output.Should().Be(source.Output);
        (await rig.CursorAsync()).Should().Be(seq);

        // 重新打開（從 sync.db 整份交給中樞）也看得到內容，不再取
        rig.Ui.Put(new ReplicaRequested(rig.Id));
        await rig.H.IdleAsync();
        rig.UiItems()["i1"].Output.Should().Be(source.Output);
        rig.Blobs.Gets.Should().HaveCount(2);
    }

    [Fact]
    public async Task 預取_有一個取不到時游標不前進_30秒後同一頁重拉()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var first = rig.PublishSmall("i1", 1, "前面的");
        var (seq, source, shas) = rig.PublishBig("i2", 2, 1);
        var last = rig.PublishSmall("i3", 3, "後面的");
        var broken = shas[1];
        rig.Blobs.GetResult = sha => sha == broken ? new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable) : null;

        await rig.OpenAsync();

        rig.UiItems().Keys.Should().Equal("i1");
        (await rig.ItemsAsync()).Select(r => r.ItemId).Should().Equal("i1");
        (await rig.CursorAsync()).Should().Be(first, "停在取不到的那筆之前");
        var gets = rig.Blobs.Gets.Count;
        gets.Should().Be(2);

        // 通知以外不馬上重拉（伺服器的 more 也不排回去）
        await rig.H.AdvanceAsync(ReplicaBlobRules.RetryAfter - TimeSpan.FromSeconds(1));
        rig.Blobs.Gets.Should().HaveCount(gets, "30 秒內不再取");

        rig.Blobs.GetResult = null;
        var pulls = rig.H.Server.Pulls.Count;
        await rig.H.AdvanceAsync(TimeSpan.FromSeconds(1));

        rig.H.Server.Pulls.Skip(pulls).Should().Contain(p => p.Cursors.Any(c => c.Stream == rig.Stream && c.After == first), "從停住那筆重拉");
        rig.UiItems().Keys.Should().Equal("i1", "i2", "i3");
        rig.UiItems()["i2"].Text.Should().Be(source.Text);
        (await rig.ItemsAsync()).Select(r => r.ItemId).Should().BeEquivalentTo(new[] { "i1", "i2", "i3" });
        (await rig.CursorAsync()).Should().Be(last);
        seq.Should().BeGreaterThan(first);
    }

    [Fact]
    public async Task 永久取不到_照WP4點3轉不回來_游標照樣前進()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var (seq, _, _) = rig.PublishBig("i1", 1, 2, storeBlobs: false);
        var after = rig.PublishSmall("i2", 2, "後面的");

        await rig.OpenAsync();

        rig.Blobs.Gets.Should().HaveCount(2, "404 是永久取不到");
        rig.UiItems().Keys.Should().Equal("i2");
        (await rig.CursorAsync()).Should().Be(after);
        var rows = await rig.ItemsAsync();
        rows.Select(r => r.ItemId).Should().BeEquivalentTo(new[] { "i1", "i2" }, "照 WP4.3：原本的 payload 照存");
        ReplicaBlobRules.Collect((JObject)JToken.Parse(rows.Single(r => r.ItemId == "i1").Payload)).Should().HaveCount(2);
        seq.Should().BeLessThan(after);
    }

    [Fact]
    public async Task 預取_合計上限_放不下的下一頁接著取_每個只取一次()
    {
        await using var rig = new Rig { PageBytes = 100_000 };
        await rig.StartAsync();
        var published = Enumerable.Range(0, 5).Select(i => rig.PublishBig("i" + (i + 1), i + 1, i, bigText: false)).ToList();
        published.Should().OnlyContain(p => p.Shas.Count == 1, "只有 output 換成 blob（40 KB）");

        await rig.OpenAsync();

        rig.UiItems().Keys.Should().BeEquivalentTo(new[] { "i1", "i2", "i3", "i4", "i5" });
        rig.UiItems().Values.Select(i => i.Output).Should().BeEquivalentTo(published.Select(p => p.Source.Output));
        rig.Blobs.Gets.Should().BeEquivalentTo(published.SelectMany(p => p.Shas), "每頁 ≤100 KB（2 個），停住的串流從那筆接著拉，每個只取一次");
        (await rig.CursorAsync()).Should().Be(published[^1].Seq);
        rig.H.Server.Pulls.Count(p => p.Cursors.Any(c => c.Stream == rig.Stream)).Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task 沒協商media1_不預取_帶blob的那筆起停住_協商出media1之後補回內容()
    {
        // 合併前審查 DI-3、INT-6：伺服器回滾到 WP4.7 之前（沒有 media1）的期間拉到其他電腦的 blob 版項目。原本照永久取不到前進：
        // replica_items 存參照版本、游標越過它，伺服器再升級回來也不會回頭重取，那筆在複本裡一直不顯示
        await using var rig = new Rig(media1: false);
        await rig.StartAsync();
        var first = rig.PublishSmall("i0", 1, "前面的");
        var (_, source, shas) = rig.PublishBig("i1", 2, 3);
        var last = rig.PublishSmall("i2", 3, "後面的");

        await rig.OpenAsync();

        rig.Box.ReplicaMedia.Should().BeNull();
        rig.Relay.Requests.Should().BeEmpty("沒有 media1：E6 一次都不呼叫");
        rig.UiItems().Keys.Should().Equal("i0");
        (await rig.ItemsAsync()).Select(r => r.ItemId).Should().Equal("i0");
        (await rig.CursorAsync()).Should().Be(first, "停在帶 blob 參照的那筆之前");

        // 沒有 media1 怎麼拉都取不到：不排 30 秒的重試
        var pulls = rig.H.Server.Pulls.Count;
        await rig.H.AdvanceAsync(ReplicaBlobRules.RetryAfter + TimeSpan.FromSeconds(1));
        rig.H.Server.Pulls.Skip(pulls).Should().NotContain(p => p.Cursors.Any(c => c.Stream == rig.Stream));

        // 伺服器再升級回來：新的連線協商出 media1，從停住那筆重拉、預取、換回內容
        rig.H.Server.Features = AllFeatures;
        rig.H.Server.Current!.Drop();
        await rig.H.IdleAsync();
        rig.H.Engine.RetryNow();
        await rig.H.IdleAsync();
        await rig.H.AdvanceAsync(TimeSpan.FromSeconds(2));

        rig.Blobs.Gets.Should().BeEquivalentTo(shas);
        rig.UiItems().Keys.Should().BeEquivalentTo(new[] { "i0", "i1", "i2" });
        rig.UiItems()["i1"].Output.Should().Be(source.Output);
        var stored = (await rig.ItemsAsync()).Single(r => r.ItemId == "i1");
        ReplicaBlobRules.Collect((JObject)JToken.Parse(stored.Payload)).Should().BeEmpty("存的是換回內容的版本");
        (await rig.CursorAsync()).Should().Be(last);
    }

    [Fact]
    public async Task 協商出media1時盒子上有媒體來源_重開之後換新的()
    {
        await using var rig = new Rig();
        await rig.StartAsync();

        var first = rig.Box.ReplicaMedia;
        first.Should().BeOfType<ReplicaMediaSource>();

        await rig.H.RestartAsync();

        rig.Box.ReplicaMedia.Should().NotBeNull().And.NotBeSameAs(first);
        (await first!.FetchAsync(MediaKeyRules.Format(rig.Other, MediaTestFiles.NewId()), MediaPurposes.Media, null, null, CancellationToken.None))
            .Should().BeNull("停止時 Dispose");
    }
}
