using Myriagent.Sync;
using Myriagent.Tests.Sync.Engine;
using Myriagent.Tests.TestSupport;
using Xunit.Abstractions;

namespace Myriagent.Tests.Sync.LongRun;

/// <summary>
/// 骨架的記憶體量到保留列數的上限（WP4.3 計畫步 9、D31、R23；<c>MYRIAGENT_SYNC_IT=1</c>，同一個程序裡跑、不連外）：
/// 每多 10 萬筆量一次，每一段都不超過 16 MB；確認路徑（照常成長的清單）的合計不超過「段數 × 16 MB」。
/// 實測值與 <see cref="ReconcileQueueRules.RetainedMaxRows"/> 的決定值記在計畫附錄 B
/// </summary>
[Collection(OutboxMemoryCollection.Name)]
public sealed class OutboxSlimLongRunTests(ITestOutputHelper output)
{
    [EnvFact(LongRun.Gate, "量到 RetainedMaxRows 筆骨架的記憶體（約 1 分鐘、暫存資料夾約 300 MB）")]
    public void 骨架到RetainedMaxRows為止_每10萬筆不超過16MB()
    {
        var (segments, ackPathTotal) = SkeletonMemory.Measure(ReconcileQueueRules.RetainedMaxRows, output);

        var count = ReconcileQueueRules.RetainedMaxRows / SkeletonMemory.SegmentRows;
        segments.Should().HaveCount(count);
        output.WriteLine($"最大的一段 {segments.Max() / 1048576.0:0.00} MB，最小 {segments.Min() / 1048576.0:0.00} MB；預算每段 {SkeletonMemory.BudgetPer100k / 1048576.0:0} MB");
        segments.Should().OnlyContain(b => b <= SkeletonMemory.BudgetPer100k, "每 10 萬筆的骨架 ≤16 MB（超過就要依比例調低 RetainedMaxRows）");
        ackPathTotal.Should().BeLessThanOrEqualTo(count * SkeletonMemory.BudgetPer100k);
    }
}
