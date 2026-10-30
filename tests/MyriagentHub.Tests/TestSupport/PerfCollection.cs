namespace MyriagentHub.Tests.TestSupport;

/// <summary>
/// 量測（<c>MYRIAGENT_HUB_PERF=1</c>；WP4.2 計畫步 14 的 <c>ConversationLatencyTests</c>、<c>ConversationWriteBench</c>；每次都跑的 WP4.5 <c>CommandRoundTripTests</c>）的集合：
/// 不和彼此、也不和其他測試同時跑（xUnit 在平行的集合都跑完之後才依序跑它），量到的延遲不受其他測試的負載影響
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PerfCollection
{
    public const string Name = "量測";
}
