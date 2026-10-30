using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;

namespace Myriagent.Tests.Sync.Engine;

/// <summary>
/// 各帳戶 token 計數的引擎（sync-protocol 14.9）：有 port 才宣告 tok1；協商出來時交握完成就送一次整份（依目錄的連結換成邏輯帳戶），
/// 計數變了只送變了的格、同一條連線至少隔 5 秒，沒有通知時 60 秒重算一次；被拒只記錄不重連、下一次送整份；重連後從整份開始；沒協商出 tok1 不送。
/// 收得下其他電腦的計數（ITokenTallyViewPort）才宣告 tok2，協商出時 TokensUpdate 驗過整份交出（14.10）
/// </summary>
public sealed class TokensEngineTests
{
    /// <summary>EngineHarness 的時鐘是 2026-10-16 09:00 UTC（本機時區 UTC）</summary>
    private const string Today = "2026-10-16";

    private static FakeTokensPort UseTokensPort(EngineHarness h, bool serverTokens)
    {
        var port = new FakeTokensPort(h.Host);
        h.HostPort = port;
        h.Server.Features = serverTokens ? [DeviceFeatureRules.Tok1] : [];
        // 目錄裡這台的 acc-1 連結到 EngineHarness.Logical
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount());
        return port;
    }

    [Fact]
    public async Task 宣告tok1_交握完成送整份_變了只送變的格_至少隔5秒()
    {
        await using var h = new EngineHarness();
        var port = UseTokensPort(h, serverTokens: true);
        port.Set(new LocalTokenTally(EngineHarness.LocalAccount, Today, 100, 40, 10), new LocalTokenTally("acc-unlinked", Today, 9, 9, 9));
        await h.StartAsync();

        h.Server.Hellos.Last().Features.Should().Equal(DeviceFeatureRules.Tok1);
        await CommandEngineTests.WaitUntilAsync(() => h.Server.TokensPublishes.Count >= 1, "交握完成就送");
        h.Server.TokensPublishes.First().Full.Should().BeTrue("這條連線第一次送整份");
        // 交握當下目錄可能還沒拉到（連結還沒有）：拉到之後照最短間隔補送
        await h.AdvanceAsync(65);
        ServerRows(h).Should().Equal(new TokenTallyRow(EngineHarness.Logical, Today, 100, 40, 10));

        var count = h.Server.TokensPublishes.Count;
        port.Set(new LocalTokenTally(EngineHarness.LocalAccount, Today, 150, 40, 12), new LocalTokenTally(EngineHarness.LocalAccount, "2026-10-15", 7, 0, 1));
        h.Engine.TokensChanged();
        await CommandEngineTests.WaitUntilAsync(() => h.Server.TokensPublishes.Count == count + 1, "距上一次已經 5 秒以上：立刻送");
        var delta = h.Server.TokensPublishes.Last();
        delta.Full.Should().BeFalse();
        delta.Rows.Should().Equal(new TokenTallyRow(EngineHarness.Logical, Today, 150, 40, 12), new TokenTallyRow(EngineHarness.Logical, "2026-10-15", 7, 0, 1));

        port.Set(new LocalTokenTally(EngineHarness.LocalAccount, Today, 160, 40, 13), new LocalTokenTally(EngineHarness.LocalAccount, "2026-10-15", 7, 0, 1));
        h.Engine.TokensChanged();
        await h.AdvanceAsync(4);
        h.Server.TokensPublishes.Should().HaveCount(count + 1, "距上一次不到 5 秒");
        await h.AdvanceAsync(1);
        await CommandEngineTests.WaitUntilAsync(() => h.Server.TokensPublishes.Count == count + 2, "滿 5 秒");
        h.Server.TokensPublishes.Last().Should().BeEquivalentTo(new TokensPublish(false, [new TokenTallyRow(EngineHarness.Logical, Today, 160, 40, 13)]), "只送變了的格");

        await h.AdvanceAsync(130);
        h.Server.TokensPublishes.Should().HaveCount(count + 2, "沒變就不送（60 秒重算一次）");

        // 少了一格（例如滑出保留範圍）：送整份
        port.Set(new LocalTokenTally(EngineHarness.LocalAccount, Today, 160, 40, 13));
        h.Engine.TokensChanged();
        await CommandEngineTests.WaitUntilAsync(() => h.Server.TokensPublishes.Count == count + 3, "少了一格");
        h.Server.TokensPublishes.Last().Should().BeEquivalentTo(new TokensPublish(true, [new TokenTallyRow(EngineHarness.Logical, Today, 160, 40, 13)]));
        ServerRows(h).Should().Equal(new TokenTallyRow(EngineHarness.Logical, Today, 160, 40, 13));
    }

    [Fact]
    public async Task 沒有通知也每60秒重算_被拒只記錄不重連_下一次送整份_重連後從整份開始()
    {
        await using var h = new EngineHarness();
        var port = UseTokensPort(h, serverTokens: true);
        port.Set(new LocalTokenTally(EngineHarness.LocalAccount, Today, 1, 0, 1));
        await h.StartAsync();
        await h.AdvanceAsync(65);
        ServerRows(h).Should().Equal(new TokenTallyRow(EngineHarness.Logical, Today, 1, 0, 1));
        var hellos = h.Server.Hellos.Count;
        var count = h.Server.TokensPublishes.Count;

        h.Server.TokensFailure = _ => new SyncTransportException(SyncFailureRules.Classify(null, SyncErrorCodes.Invalid, false));
        port.Set(new LocalTokenTally(EngineHarness.LocalAccount, Today, 2, 0, 2));
        await h.AdvanceAsync(60);
        await CommandEngineTests.WaitUntilAsync(() => h.Server.TokensPublishes.Count == count + 1, "沒有通知，60 秒重算時發現變了");
        h.Server.TokensPublishes.Last().Full.Should().BeFalse();
        await h.IdleAsync();
        h.Server.Hellos.Should().HaveCount(hellos, "被拒不重連");

        h.Server.TokensFailure = null;
        await h.AdvanceAsync(60);
        await CommandEngineTests.WaitUntilAsync(() => h.Server.TokensPublishes.Count == count + 2, "下一次重算");
        h.Server.TokensPublishes.Last().Full.Should().BeTrue("被拒之後不知道伺服器有什麼：送整份");

        h.Server.Current!.Drop();
        await h.IdleAsync();
        await h.AdvanceAsync(1);
        await CommandEngineTests.WaitUntilAsync(() => h.Server.Hellos.Count == hellos + 1 && h.Server.TokensPublishes.Count >= count + 3, "重連");
        h.Server.TokensPublishes.ElementAt(count + 2).Should().BeEquivalentTo(new TokensPublish(true, [new TokenTallyRow(EngineHarness.Logical, Today, 2, 0, 2)]),
            "新連線從整份開始");
    }

    [Fact]
    public async Task 伺服器沒有tok1_不送()
    {
        await using var h = new EngineHarness();
        var port = UseTokensPort(h, serverTokens: false);
        port.Set(new LocalTokenTally(EngineHarness.LocalAccount, Today, 1, 0, 1));
        await h.StartAsync();
        h.Engine.TokensChanged();
        await h.AdvanceAsync(70);

        h.Server.Hellos.Last().Features.Should().Equal(DeviceFeatureRules.Tok1);
        h.Server.TokensPublishes.Should().BeEmpty();
    }

    [Fact]
    public async Task 沒有port_不宣告tok1()
    {
        await using var h = new EngineHarness();
        h.Server.Features = [DeviceFeatureRules.Tok1];
        await h.StartAsync();
        h.Engine.TokensChanged();
        await h.AdvanceAsync(10);

        h.Server.Hellos.Last().Features.Should().BeNull();
        h.Server.TokensPublishes.Should().BeEmpty();
    }

    [Fact]
    public async Task 交握完成前到的TokensUpdate_暫存_交握完成後交出最新的一則_連線不斷()
    {
        // 伺服器在連線證明完成的那一刻就送（TokenTallyFanout 的 ConnectionProven），常常比 ProveResponse 在桌面處理完還早到：
        // 0.2.621～0.2.624 的早到緩衝不認得它、丟例外，每次 app 啟動都斷線重連一次
        await using var h = new EngineHarness();
        var port = new FakeTokensViewPort(h.Host);
        h.HostPort = port;
        h.Server.Features = [DeviceFeatureRules.Tok1, DeviceFeatureRules.Tok2];
        var older = new TokensUpdate([new TokenTallyRow(EngineHarness.Logical, Today, 1, 0, 1)]);
        var newer = new TokensUpdate([new TokenTallyRow(EngineHarness.Logical, Today, 50, 10, 5)]);
        h.Server.BeforeProveReturns = s =>
        {
            s.Events.OnTokensUpdate(older);
            s.Events.OnTokensUpdate(newer);
        };

        await h.StartAsync();
        await CommandEngineTests.WaitUntilAsync(() => port.Delivered.Count >= 1, "交握完成後交出");
        await h.IdleAsync();

        port.Delivered.Should().ContainSingle("整份取代的資料只留最新的一則").Which.Should().Equal(newer.Rows);
        h.Server.Hellos.Should().HaveCount(1, "連線沒有因為早到的訊息斷掉重連");
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
    }

    [Fact]
    public async Task 收得下其他電腦的計數才宣告tok2_TokensUpdate驗過交出_不合格略過_斷線不清()
    {
        await using var h = new EngineHarness();
        var port = new FakeTokensViewPort(h.Host);
        h.HostPort = port;
        h.Server.Features = [DeviceFeatureRules.Tok1, DeviceFeatureRules.Tok2];
        await h.StartAsync();

        h.Server.Hellos.Last().Features.Should().Equal(DeviceFeatureRules.Tok1, DeviceFeatureRules.Tok2);
        var rows = new[] { new TokenTallyRow(EngineHarness.Logical, Today, 50, 10, 5) };
        h.Server.Current!.Events.OnTokensUpdate(new TokensUpdate(rows));
        await CommandEngineTests.WaitUntilAsync(() => port.Delivered.Count == 1, "交出");
        port.Delivered.Single().Should().Equal(rows);

        h.Server.Current!.Events.OnTokensUpdate(new TokensUpdate([new TokenTallyRow(EngineHarness.Logical, "2026-02-30", 1, 0, 1)]));
        await h.IdleAsync();
        port.Delivered.Should().HaveCount(1, "格式不合格整則略過");

        h.Server.Current!.Drop();
        await h.IdleAsync();
        port.Delivered.Should().HaveCount(1, "斷線不交空的：計數只會變大，最後收到的仍是對的下限");
    }

    [Fact]
    public async Task 伺服器沒有tok2_TokensUpdate略過()
    {
        await using var h = new EngineHarness();
        var port = new FakeTokensViewPort(h.Host);
        h.HostPort = port;
        h.Server.Features = [DeviceFeatureRules.Tok1];
        await h.StartAsync();

        h.Server.Current!.Events.OnTokensUpdate(new TokensUpdate([new TokenTallyRow(EngineHarness.Logical, Today, 1, 0, 1)]));
        await h.IdleAsync();
        port.Delivered.Should().BeEmpty("沒協商出 tok2 的伺服器訊息不交給登記點");
    }

    /// <summary>照伺服器的語意重播收到的 TokensPublish（full 整份取代、否則逐格取代）：伺服器現在有的格，依（日期新到舊、帳戶）</summary>
    private static List<TokenTallyRow> ServerRows(EngineHarness h)
    {
        var rows = new Dictionary<(string, string), TokenTallyRow>();
        foreach (var publish in h.Server.TokensPublishes)
        {
            if (publish.Full)
                rows.Clear();
            foreach (var row in publish.Rows)
                rows[(row.LogicalAccountId, row.Day)] = row;
        }
        return rows.Values.OrderByDescending(r => r.Day, StringComparer.Ordinal).ThenBy(r => r.LogicalAccountId, StringComparer.Ordinal).ToList();
    }

    /// <summary>測試用的宿主：<see cref="FakeTokensPort"/> 之外再收其他電腦的計數（<see cref="ITokenTallyViewPort"/>）</summary>
    private sealed class FakeTokensViewPort(FakeHostPort inner) : ISyncHostPort, ITokenTallyPort, ITokenTallyViewPort
    {
        public System.Collections.Concurrent.ConcurrentQueue<IReadOnlyList<TokenTallyRow>> Delivered { get; } = new();

        public IReadOnlyList<LocalTokenTally> CurrentTokens() => [];

        public void DeliverOthers(IReadOnlyList<TokenTallyRow> rows) => Delivered.Enqueue(rows);

        public long UiAliveAt => inner.UiAliveAt;

        public Task RunOnUiAsync(Action action, CancellationToken ct) => inner.RunOnUiAsync(action, ct);

        public void PublishStatus(SyncStatusSnapshot status) => inner.PublishStatus(status);

        public void PublishPollSnapshot(UsagePollSnapshot snapshot) => inner.PublishPollSnapshot(snapshot);

        public void PingUi() => inner.PingUi();
    }

    /// <summary>測試用的宿主：包一個 <see cref="FakeHostPort"/>，另外實作 <see cref="ITokenTallyPort"/></summary>
    private sealed class FakeTokensPort(FakeHostPort inner) : ISyncHostPort, ITokenTallyPort
    {
        private volatile LocalTokenTally[] current = [];

        public void Set(params LocalTokenTally[] tallies) => current = tallies;

        public IReadOnlyList<LocalTokenTally> CurrentTokens() => current;

        public long UiAliveAt => inner.UiAliveAt;

        public Task RunOnUiAsync(Action action, CancellationToken ct) => inner.RunOnUiAsync(action, ct);

        public void PublishStatus(SyncStatusSnapshot status) => inner.PublishStatus(status);

        public void PublishPollSnapshot(UsagePollSnapshot snapshot) => inner.PublishPollSnapshot(snapshot);

        public void PingUi() => inner.PingUi();
    }
}
