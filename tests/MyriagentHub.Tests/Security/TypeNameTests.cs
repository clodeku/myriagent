using Myriagent.Sync;
using MyriagentHub.Hosting;

namespace MyriagentHub.Tests.Security;

/// <summary>
/// 伺服器與共用協定沒有同名的頂層型別：同命名空間的同名型別會遮蔽 Protocol 的型別，例如 <c>DeviceHub.cs</c> 在
/// <c>namespace MyriagentHub.Realtime</c> 裡不加限定名稱地呼叫 Protocol 的 <c>OutboxRules.ResyncFrom</c>，
/// 同命名空間多一個 <c>OutboxRules</c> 整個伺服器就編不過或呼叫到錯的型別（WP3.6 計畫第 0 節）
/// </summary>
public sealed class TypeNameTests
{
    [Fact]
    public void MyriagentHub與Protocol沒有同名的頂層型別()
    {
        var hub = TopLevelNames(typeof(HubApplication).Assembly);
        var protocol = TopLevelNames(typeof(SyncProtocol).Assembly);
        hub.Count.Should().BeGreaterThan(50, "確認讀到的是伺服器組件");
        protocol.Count.Should().BeGreaterThan(20, "確認讀到的是 Protocol 組件");
        hub.Intersect(protocol, StringComparer.Ordinal).Should().BeEmpty(
            "同命名空間的同名型別會遮蔽 Protocol 的型別（例：DeviceHub.cs 不加限定名稱地呼叫 OutboxRules），新型別請加 Client／Browser 之類的前綴");
    }

    /// <summary>
    /// 自己命名空間（MyriagentHub、Myriagent 開頭）裡非巢狀、不是編譯器產生（名稱不含 &lt;、沒有 CompilerGenerated）的型別名稱；泛型去掉 `n。
    /// 編譯器嵌進每個組件的屬性（NullableAttribute、EmbeddedAttribute…）在 System、Microsoft 命名空間，不算
    /// </summary>
    private static HashSet<string> TopLevelNames(System.Reflection.Assembly assembly) =>
        assembly.GetTypes()
            .Where(t => !t.IsNested && !t.Name.Contains('<')
                && t.Namespace is { } ns && (ns.StartsWith("MyriagentHub", StringComparison.Ordinal) || ns.StartsWith("Myriagent", StringComparison.Ordinal))
                && !t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false))
            .Select(t => t.Name.Split('`')[0])
            .ToHashSet(StringComparer.Ordinal);
}
