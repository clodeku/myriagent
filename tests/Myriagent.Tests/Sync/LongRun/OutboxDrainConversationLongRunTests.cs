using System.Diagnostics;
using Myriagent.Agents;
using Myriagent.Sync;
using Myriagent.Tests.Sync.WithHub;
using Myriagent.Tests.TestSupport;
using FakeDevice;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Myriagent.Tests.Sync.LongRun;

/// <summary>
/// S5 的「積壓 1,000 筆（含對話事件）30 秒內補完、0 重複」（WP4.9 計畫步 10；設計:184；<c>MYRIAGENT_SYNC_IT=1</c>）：自己的 MyriagentHub 子程序＋
/// 一台 LongRunDevice（真的中樞、發布者與引擎，預設退避）。先連上、協商出 conv1，停伺服器；離線時開 <see cref="Conversations"/> 個對話各跑一輪
/// （每段各成一個完成的項目）：發布者照寫對話的生命週期事件（conv.created、conv.meta、conv.turn、conv.attention）進 outbox，項目剛出現時還沒有
/// Ord，引擎記成要重取快照、連上之後以分頁補送（D8；實測離線時 0 列 conv.item）。再補上不合併的 cli.state，待送的剛好 1,000 列。
/// 伺服器回來後 30 秒內（從啟動子程序起算）：1,000 列與連上之後補送的項目全部確認，伺服器上這台的事件剛好是 deviceSeq 1..N 各一筆、
/// 內容等於本機 outbox、沒有被擋下的，每個項目都有 conv.item
/// </summary>
[Collection(MyriagentHubCollection.Name)]
public sealed class OutboxDrainConversationLongRunTests(ITestOutputHelper output)
{
    private const int Total = 1_000;

    /// <summary>離線時開幾個對話（每個都有自己的生命週期事件）</summary>
    private const int Conversations = 40;

    /// <summary>每個對話那一輪的段數（每段一個完成的項目）</summary>
    private const int Segments = 10;

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static readonly FakeAccountSpec Account = LongRunDevice.Account("drain", AgentProvider.Claude, "drain@example.test", "org-drain");

    [EnvFact(LongRun.Gate, "積壓 1,000 筆（含對話事件）、恢復後 30 秒內補完（自己的 MyriagentHub，約 1 分鐘）")]
    public async Task 積壓1000筆含對話事件_恢復後30秒內補完_0重複()
    {
        await using var hub = await MyriagentHubProcess.StartAsync();
        using var owner = await OwnerClient.LoginAsync(hub);
        var db = new HubDb(hub.DataDir);
        await using var device = await LongRunDevice.PairAndStartAsync(hub, owner, "drain-conv", [Account]);
        await device.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.Features.Contains(DeviceFeatureRules.Conv1), LongRun.Wait, "連上、協商出 conv1");
        var (settled, _, _) = await device.Runtime.WaitDrainedAsync(LongRun.Wait);
        settled.Should().BeTrue("開始前的目錄事件都送完");
        var engine = await device.EngineAsync();
        var ackedBefore = await device.Inspect(v => v.Outbox.Count);
        db.DeviceEvents(device.DeviceId).Should().HaveCount(ackedBefore);

        await hub.StopAsync();
        await device.WaitUntilAsync(v => v.State == SyncEngineState.Backoff, LongRun.Wait, "伺服器停了：退避");
        var offline = Stopwatch.StartNew();

        // 對話事件：離線時（上一次進 Live 有 conv1）發布者照寫進 outbox
        var items = 0;
        for (var i = 0; i < Conversations; i++)
        {
            var turn = ScriptedTurn.Parse(new JArray(new JObject { ["text"] = string.Concat(Enumerable.Repeat("積壓", Segments)), ["segments"] = Segments, ["completeEachSegment"] = true }));
            var chat = await device.Runtime.ChatAsync(new ChatSpec($"drain-{i:00}", Account.Alias, $"離線時的對話 {i}", [], turn, LongRun.Wait));
            chat.Outcome.Should().Be(AgentTurnOutcome.Completed);
            chat.Created.Should().BeTrue("每一輪都是新的對話");
            items += chat.Items;
        }

        var conversationRows = await StablePendingAsync(device, e => e.ConversationId is not null);
        var kinds = await device.Inspect(v => v.Outbox.Where(e => !e.IsDone && e.ConversationId is not null).GroupBy(e => e.Kind).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}").ToList());
        conversationRows.Should().BeInRange(Conversations * 3, Total - 100, "每個對話至少 conv.created 與一輪的開始、結束");

        // 裝置事件補到剛好 1,000 列待送
        var pendingNow = await device.Inspect(v => v.Outbox.Count(e => !e.IsDone));
        Backlog.Append(engine, 1, Total - pendingNow);
        await device.WaitUntilAsync(v => v.Outbox.Count(e => !e.IsDone) == Total, LongRun.Wait, "1,000 列都進了 outbox");
        var pending = await device.Inspect(v => v.Outbox.Where(e => !e.IsDone).ToList());
        var deviceRows = pending.Count(e => e.ConversationId is null);
        pending.Count(e => e.ConversationId is not null).Should().Be(conversationRows);
        deviceRows.Should().BeGreaterThanOrEqualTo(100, "兩種事件混合");
        db.DeviceEvents(device.DeviceId).Should().HaveCount(ackedBefore, "停機期間一筆都送不出去");
        // 確認之後對話列在記憶體裡換成骨架（payload 與雜湊清空，OutboxSliceRules.Skeleton）：內容的雜湊先記下來，補完之後和伺服器的比
        var before = await device.Inspect(v => v.Outbox.ToDictionary(e => e.DeviceSeq, e => e.PayloadSha256));
        before.Values.Should().OnlyContain(sha => sha.Length > 0, "還沒確認的列與第 3 階段的列都不是骨架");

        await hub.LaunchAsync();
        var drained = await WaitDrainedAsync(db, engine, device.DeviceId, items, hub.LaunchedAt, LongRun.Wait);
        var events = db.DeviceEvents(device.DeviceId);
        var conversationEvents = events.Count(e => e.Kind.StartsWith("conv.", StringComparison.Ordinal));
        var itemEvents = events.Count(e => e.Kind == ConversationEventKinds.Item);
        var compared = await AssertExactlyOnceAsync(db, engine, device.DeviceId, before);
        output.WriteLine($"離線 {offline.Elapsed.TotalSeconds:0.0} 秒積壓 {Total} 列（對話 {conversationRows}：{string.Join("、", kinds)}；裝置 {deviceRows}；之前已確認 {ackedBefore} 列；" +
                         $"{Conversations} 個對話共 {items} 個項目，離線時 0 列 conv.item）：從啟動子程序起 {drained.TotalSeconds:0.0} 秒補完；" +
                         $"退避 {string.Join(", ", engine.BackoffHistory.Select(d => d.TotalSeconds.ToString("0.0")))} 秒；伺服器上這台的事件 {events.Count} 筆" +
                         $"（對話 {conversationEvents}、其中 conv.item {itemEvents}；連上之後補送 {events.Count - ackedBefore - Total} 筆）；比對內容 {compared} 筆");

        drained.Should().BeLessThan(Budget, "恢復後 30 秒內補完（從啟動子程序起算，含連上之後補送的項目）");
        compared.Should().BeGreaterThanOrEqualTo(ackedBefore + Total, "積壓的 1,000 列與之前的列都比對過內容");
        events.Count.Should().BeGreaterThanOrEqualTo(ackedBefore + Total + items, "積壓的 1,000 列加上每個項目一列 conv.item");
        itemEvents.Should().BeGreaterThanOrEqualTo(items, "每個項目都在伺服器上");
        conversationEvents.Should().BeGreaterThanOrEqualTo(conversationRows + items);
        hub.ForcedKill.Should().BeFalse();
    }

    /// <summary>
    /// 0 遺失、0 重複（同 <see cref="ExactlyOnce.AssertAsync"/>，但對話列確認之後是骨架）：本機 outbox 全部確認、伺服器上這台的事件剛好是
    /// deviceSeq 1..N 各一筆、沒有被擋下的；內容的雜湊和本機的比——本機還有雜湊的列比本機的，骨架比 <paramref name="before"/>（啟動伺服器前記下的）。
    /// 連上之後才寫、已經換成骨架的列（補送的項目）只看 deviceSeq。回傳比對過內容的筆數
    /// </summary>
    private static async Task<int> AssertExactlyOnceAsync(HubDb db, SyncEngine engine, string deviceId, IReadOnlyDictionary<long, string> before)
    {
        var outbox = await engine.InvokeAsync(v => v.Outbox);
        outbox.Should().OnlyContain(e => e.State == OutboxState.Acked, "本機 outbox 全部確認");
        var events = db.DeviceEvents(deviceId);
        events.Select(e => e.DeviceSeq).Should().Equal(Enumerable.Range(1, outbox.Count).Select(i => (long?)i), "deviceSeq 1..N 各一筆（0 遺失、0 重複）");
        db.Rejected(deviceId).Should().Be(0);
        var local = outbox.ToDictionary(e => e.DeviceSeq, e => e.PayloadSha256);
        var compared = 0;
        foreach (var e in events)
        {
            var seq = e.DeviceSeq!.Value;
            var expected = local[seq].Length > 0 ? local[seq] : before.GetValueOrDefault(seq);
            if (expected is null)
                continue;
            Convert.ToHexStringLower(e.PayloadSha256 ?? []).Should().Be(expected.ToLowerInvariant(), $"deviceSeq {seq} 的內容和本機 outbox 相同");
            compared++;
        }

        return compared;
    }

    /// <summary>等符合條件的待送列數連續 2 秒不變（發布者最多合併 1 秒），回傳列數</summary>
    private static async Task<int> StablePendingAsync(LongRunDevice device, Func<OutboxEntry, bool> which)
    {
        var watch = Stopwatch.StartNew();
        var last = -1;
        var since = Stopwatch.StartNew();
        while (watch.Elapsed < LongRun.Wait)
        {
            var count = await device.Inspect(v => v.Outbox.Count(e => !e.IsDone && which(e)));
            if (count != last)
            {
                last = count;
                since.Restart();
            }
            else if (count > 0 && since.Elapsed >= TimeSpan.FromSeconds(2))
            {
                return count;
            }

            await Task.Delay(100);
        }

        throw new XunitException($"{LongRun.Wait.TotalSeconds:0} 秒內待送的對話列沒有穩定下來（最後 {last} 列）");
    }

    /// <summary>
    /// 等本機 outbox 全部確認、伺服器的筆數等於 outbox、伺服器上的 conv.item 至少 <paramref name="items"/> 筆（連上之後補送的項目在第一批確認之後才寫進
    /// outbox，只看 outbox 會提早結束）。回傳從 <paramref name="launchedAt"/> 起算的時間
    /// </summary>
    private static async Task<TimeSpan> WaitDrainedAsync(HubDb db, SyncEngine engine, string deviceId, int items, long launchedAt, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var outbox = await engine.InvokeAsync(v => v.Outbox);
            var stored = db.DeviceEvents(deviceId);
            if (outbox.Count > 0 && outbox.All(e => e.State == OutboxState.Acked) && stored.Count == outbox.Count
                && stored.Count(e => e.Kind == ConversationEventKinds.Item) >= items)
                return Stopwatch.GetElapsedTime(launchedAt);
            if (watch.Elapsed > timeout)
            {
                var states = string.Join("、", outbox.GroupBy(e => e.State).Select(g => $"{g.Key} {g.Count()}"));
                throw new XunitException($"{timeout.TotalSeconds:0} 秒內沒有補完：伺服器 {stored.Count} 筆（conv.item {stored.Count(e => e.Kind == ConversationEventKinds.Item)}，要 {items}），outbox {outbox.Count} 列（{states}）");
            }

            await Task.Delay(200);
        }
    }
}
