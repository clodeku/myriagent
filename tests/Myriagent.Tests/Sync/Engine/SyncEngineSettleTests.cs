using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;
using Xunit.Sdk;

namespace Myriagent.Tests.Sync.Engine;

/// <summary>
/// 引擎的安定快照（WP4.9 計畫步 1、D2；<see cref="ISyncSettleProbe"/>）：重整進行中、有在途批次時不安定，做完才安定；引擎停了不卡住。
/// 引擎＋<see cref="FakeConversationUi"/>（代替發布者與中樞），重整的中繼資料可以扣住
/// </summary>
public sealed class SyncEngineSettleTests
{
    private sealed class Rig : IAsyncDisposable
    {
        public Rig()
        {
            Box = new ConversationChangeBox();
            H = new EngineHarness(conversations: Box);
            Inner = new FakeConversationUi(Box, () => H.Engine);
            Ui = new HoldingUi(Inner);
            Box.UiPort = Ui;
            Root = H.Dir.Combine("conversations");
            Directory.CreateDirectory(Root);
            Box.ConversationsRoot = Root;
            H.Server.Features = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1];
            _ = H.Server.Conversations;
        }

        public EngineHarness H { get; }

        public ConversationChangeBox Box { get; }

        public FakeConversationUi Inner { get; }

        public HoldingUi Ui { get; }

        public string Root { get; }

        /// <summary>引擎的快照，回補是否做完照 UI 端最後一次收到的進度填（同探針以發布者的 Progress 填）</summary>
        public async Task<SyncSettleSnapshot> SnapshotAsync()
        {
            var snapshot = await H.Engine.SettleSnapshotAsync();
            snapshot.BackfillDone.Should().BeFalse("引擎回的快照一律留給呼叫端填");
            var last = Inner.Progress.LastOrDefault();
            return snapshot with { BackfillDone = last.Progress is null };
        }

        /// <summary>一秒一秒撥快，直到安定（測試時鐘最多 <paramref name="maxSeconds"/> 秒）</summary>
        public async Task<SyncSettleSnapshot> SettleAsync(int maxSeconds = 60)
        {
            for (var i = 0; i < maxSeconds; i++)
            {
                await H.AdvanceAsync(1);
                var snapshot = await SnapshotAsync();
                if (SyncSettleRules.IsSettled(snapshot))
                    return snapshot;
            }

            throw new XunitException($"{maxSeconds} 秒（測試時鐘）內沒有安定");
        }

        public ValueTask DisposeAsync() => H.DisposeAsync();
    }

    /// <summary>重整的中繼資料（RequestMeta(null)）扣住，等測試 <see cref="Release"/> 才交給 <see cref="FakeConversationUi"/> 回</summary>
    private sealed class HoldingUi(FakeConversationUi inner) : IConversationUiPort
    {
        private readonly object gate = new();
        private bool hold = true;
        private int held;

        public int Held
        {
            get
            {
                lock (gate)
                    return held;
            }
        }

        public void Release()
        {
            int count;
            lock (gate)
            {
                hold = false;
                count = held;
                held = 0;
            }

            for (var i = 0; i < count; i++)
                inner.RequestMeta(null);
        }

        public void RequestMeta(IReadOnlyList<string>? ids)
        {
            if (ids is null)
            {
                lock (gate)
                {
                    if (hold)
                    {
                        held++;
                        return;
                    }
                }
            }

            inner.RequestMeta(ids);
        }

        public void RequestItems(string conversationId, long afterOrd, int take, OrdHint? hint) => inner.RequestItems(conversationId, afterOrd, take, hint);

        public void SetWatched(IReadOnlySet<string> ids) => inner.SetWatched(ids);

        public void PauseForDlp(string conversationId) => inner.PauseForDlp(conversationId);

        public void MarkServerDeleted(string conversationId) => inner.MarkServerDeleted(conversationId);

        public void RequestQueue(string conversationId) => inner.RequestQueue(conversationId);

        public void Notify(PublishNoticeKind kind, int count) => inner.Notify(kind, count);

        public void ReportProgress(SyncBackfillProgress? progress, BackfillPause? pause) => inner.ReportProgress(progress, pause);

        public void ApplyReplicaDelta(ReplicaDelta delta) => inner.ApplyReplicaDelta(delta);
    }

    [Fact]
    public async Task 重整進行中不安定_中繼資料回完之後安定()
    {
        await using var rig = new Rig();
        await rig.H.StartAsync();
        await rig.H.AdvanceAsync(2);

        rig.Ui.Held.Should().Be(1, "前提：進 Live 時要了重整的中繼資料、被扣住");
        var during = await rig.SnapshotAsync();
        during.Live.Should().BeTrue("前提：已經進 Live");
        during.Refreshing.Should().BeTrue();
        SyncSettleRules.IsSettled(during).Should().BeFalse("重整的中繼資料還沒回完");

        rig.Ui.Release();
        var after = await rig.SettleAsync();
        after.Refreshing.Should().BeFalse();
        after.ReconcileQueue.Should().Be(0);
        after.ReconcileActive.Should().BeFalse();
        after.OutboxSettled.Should().BeTrue();
        after.InFlight.Should().Be(0);
    }

    [Fact]
    public async Task 有在途批次不安定_確認之後安定()
    {
        await using var rig = new Rig();
        rig.Ui.Release();
        await rig.H.StartAsync();
        await rig.SettleAsync();

        rig.H.Server.HoldAcks = true;
        rig.H.Append();
        await rig.H.AdvanceAsync(1);
        var waiting = await rig.SnapshotAsync();
        waiting.InFlight.Should().BeGreaterThan(0, "前提：批次送出了、確認被扣住");
        waiting.OutboxSettled.Should().BeFalse();
        SyncSettleRules.IsSettled(waiting).Should().BeFalse("有在途的批次");

        rig.H.Server.HoldAcks = false;
        rig.H.Server.Current!.ReleaseAcks();
        var after = await rig.SettleAsync();
        after.InFlight.Should().Be(0);
        after.OutboxSettled.Should().BeTrue();
    }

    [Fact]
    public async Task 引擎停了_快照不卡住_不在Live()
    {
        await using var rig = new Rig();
        rig.Ui.Release();
        await rig.H.StartAsync();
        await rig.SettleAsync();
        await rig.H.Engine.StopAsync(TimeSpan.FromSeconds(5));

        var stopped = await rig.H.Engine.SettleSnapshotAsync().WaitAsync(TimeSpan.FromSeconds(5));
        stopped.Live.Should().BeFalse();
        SyncSettleRules.IsSettled(stopped with { BackfillDone = true }).Should().BeFalse("停了的引擎永遠不算安定");
    }
}
