using Myriagent.Agents;
using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Rules;

/// <summary>複本的純函式（WP4.3 計畫步 14、15，D26、D28）：存著的線上 payload → 中樞要的樣子；解不開的一律當成沒有</summary>
public sealed class ReplicaRulesTests
{
    private const string Owner = "d_01J9Q0000000000000000000OW";

    private static string MetaJson(string policy = ConversationSyncPolicies.Full, string provider = "Codex", string? folder = @"C:\repo\owner",
        IReadOnlyList<ConversationModelOption>? models = null, string? localAccountId = "acc-9")
        => ConversationWire.Serialize(new ConversationMetaPayload(provider, localAccountId, "擁有者的對話", "FullAccess", "gpt-5", "high", folder is not null, folder, policy,
            1791500000000, 1791500060000, true, true, models));

    [Fact]
    public void 中繼資料_擁有者那台的本機帳戶另外帶著_不當成這台的帳戶()
    {
        var id = new string('a', 32);
        var meta = ReplicaRules.ToMeta(new ReplicaMetaRow(id, Owner, 1, MetaJson(), false, 1))!;

        meta.OwnerAccountId.Should().Be("acc-9", "標頭的帳戶晶片以目錄換成邏輯帳戶（2026-10-25）");
        meta.Meta.AccountId.Should().BeEmpty("擁有者電腦的本機帳戶在這台沒有意義");
        ReplicaRules.ToMeta(new ReplicaMetaRow(id, Owner, 1, MetaJson(localAccountId: null), false, 1))!.OwnerAccountId.Should().BeNull("帳戶不明（meta.json 重建）");
    }

    [Fact]
    public void 中繼資料_擁有者發布的模型清單()
    {
        var id = new string('a', 32);
        ConversationModelOption[] published = [new("gpt-5", "GPT-5", "旗艦", ["low", "high"]), new("gpt-5-mini", "GPT-5 mini", null, null)];

        var models = ReplicaRules.ToMeta(new ReplicaMetaRow(id, Owner, 1, MetaJson(models: published), false, 1))!.Models!;

        models.Select(m => (m.Value, m.Label, m.Description)).Should().Equal(("gpt-5", "GPT-5", "旗艦"), ("gpt-5-mini", "GPT-5 mini", null));
        models[0].Efforts.Should().Equal("low", "high");
        models[1].Efforts.Should().BeEmpty("沒有推理強度");
        ReplicaRules.ToMeta(new ReplicaMetaRow(id, Owner, 1, MetaJson(), false, 1))!.Models.Should().BeNull("舊版的擁有者不送：標頭用內建的預設選單");

        // 存著的內容不完全可信：value 空的、重複的略過，label 空的用 value，一項都沒有回 null
        ReplicaRules.Models([new("", "空的", null, null), new("o", "", null, ["", "high"]), new("o", "重複", null, null)])!
            .Select(m => (m.Value, m.Label, string.Join(",", m.Efforts))).Should().Equal(("o", "o", "high"));
        ReplicaRules.Models([new("", "空的", null, null)]).Should().BeNull();
        ReplicaRules.Models([]).Should().BeNull();
        ReplicaRules.Models(null).Should().BeNull();
    }

    [Fact]
    public void 政策_只認三種_其他當成沒有項目()
    {
        ReplicaRules.PolicyOf(MetaJson()).Should().Be(ConversationSyncPolicies.Full);
        ReplicaRules.PolicyOf(MetaJson(ConversationSyncPolicies.Metadata)).Should().Be(ConversationSyncPolicies.Metadata);
        ReplicaRules.PolicyOf(MetaJson(ConversationSyncPolicies.Paused)).Should().Be(ConversationSyncPolicies.Paused);
        ReplicaRules.PolicyOf(MetaJson("future")).Should().Be(ConversationSyncPolicies.LocalOnly);
        ReplicaRules.PolicyOf("{ 不是 JSON").Should().Be(ConversationSyncPolicies.LocalOnly);
        ReplicaRules.PolicyOf("").Should().Be(ConversationSyncPolicies.LocalOnly);

        ReplicaRules.HasItems(MetaJson()).Should().BeTrue();
        ReplicaRules.HasItems(MetaJson(ConversationSyncPolicies.Metadata)).Should().BeFalse();
        ReplicaRules.HasItems(MetaJson(ConversationSyncPolicies.Paused)).Should().BeFalse();
    }

    [Fact]
    public void 中繼資料_沒有本機帳戶與工作資料夾_擁有者與epoch照列上的()
    {
        var id = new string('a', 32);
        var meta = ReplicaRules.ToMeta(new ReplicaMetaRow(id, Owner, 3, MetaJson(), false, 42))!;

        meta.ConversationId.Should().Be(id);
        meta.OwnerDeviceId.Should().Be(Owner);
        meta.Running.Should().BeTrue();
        meta.Waiting.Should().BeTrue();
        meta.ItemsAvailable.Should().BeTrue();
        meta.Meta.Id.Should().Be(id);
        meta.Meta.Title.Should().Be("擁有者的對話");
        meta.Meta.Provider.Should().Be(AgentProvider.Codex);
        meta.Meta.AccountId.Should().BeEmpty("擁有者電腦的本機帳戶在這台沒有意義");
        meta.Meta.WorkingDirectory.Should().BeNull("擁有者電腦上的路徑");
        meta.CustomFolder.Should().BeTrue();
        meta.Folder.Should().Be(@"C:\repo\owner", "只供標頭與清單顯示");
        meta.Meta.SessionId.Should().BeNull();
        meta.Meta.PermissionMode.Should().Be(AgentPermissionMode.FullAccess);
        meta.Meta.Model.Should().Be("gpt-5");
        meta.Meta.Effort.Should().Be("high");
        meta.Meta.SyncPolicy.Should().Be(ConversationSyncPolicies.Full);
        meta.Meta.OwnerDeviceId.Should().Be(Owner);
        meta.Meta.LeaseEpoch.Should().Be(3);
        meta.Meta.CreatedAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1791500000000).LocalDateTime);
        meta.Meta.UpdatedAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1791500060000).LocalDateTime);

        ReplicaRules.ToMeta(new ReplicaMetaRow(id, Owner, 1, MetaJson(ConversationSyncPolicies.Metadata), false, 1))!.ItemsAvailable.Should().BeFalse();
        var workspace = ReplicaRules.ToMeta(new ReplicaMetaRow(id, Owner, 1, MetaJson(folder: null), false, 1))!;
        workspace.CustomFolder.Should().BeFalse("擁有者那邊是對話自己的 workspace");
        workspace.Folder.Should().BeNull();
        ReplicaRules.ToMeta(new ReplicaMetaRow(id, Owner, 1, MetaJson(), true, 1)).Should().BeNull("刪除的不顯示");
        ReplicaRules.ToMeta(new ReplicaMetaRow(id, Owner, 1, MetaJson(provider: "Hologram"), false, 1)).Should().BeNull("不認得的供應商");
        ReplicaRules.ToMeta(new ReplicaMetaRow(id, Owner, 1, "[]", false, 1)).Should().BeNull();
    }

    [Fact]
    public void 項目_照WireItemReader轉_blob還取不到與格式不對的是null()
    {
        var item = new AgentChatItem { Id = "i1", Ord = 2, Kind = AgentItemKind.Assistant, Status = AgentItemStatus.Done, Text = "回覆", MediaPaths = { @"C:\a\chart.png" } };
        ConversationWire.TryItem(item, 1, out var wire).Should().BeTrue();

        var read = ReplicaRules.ToItem(ConversationWire.Serialize(wire!))!;
        read.Id.Should().Be("i1");
        read.Ord.Should().Be(2);
        read.Text.Should().Be("回覆");
        read.MediaPaths.Should().BeEmpty("複本沒有本機的媒體路徑");

        var big = new AgentChatItem { Id = "i2", Ord = 3, Kind = AgentItemKind.Tool, Status = AgentItemStatus.Done, Output = new string('x', 40 * 1024) };
        ConversationWire.TryItem(big, 1, out var bigWire).Should().BeTrue();
        ConversationEventRules.TryFit(bigWire!, out var blobs).Should().BeTrue();
        blobs.Should().NotBeEmpty();
        ReplicaRules.ToItem(ConversationWire.Serialize(bigWire!)).Should().BeNull("blob 要等 WP4.7 才取得到");

        ReplicaRules.ToItem("{ 不是 JSON").Should().BeNull();
        ReplicaRules.ToItem("").Should().BeNull();
        ReplicaRules.ToItem(ConversationWire.Serialize(wire!).Replace("\"Assistant\"", "\"Hologram\"", StringComparison.Ordinal)).Should().BeNull("不認得的種類");
    }
}
