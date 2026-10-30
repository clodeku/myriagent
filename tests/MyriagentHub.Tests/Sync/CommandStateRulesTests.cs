using Myriagent.Remote;
using Myriagent.Security;
using MyriagentHub.Sync;

namespace MyriagentHub.Tests.Sync;

/// <summary>
/// 遠端指令列的狀態轉移與期限（WP4.5 計畫步 2；D13、D18、D19、D27、D28）
/// </summary>
public sealed class CommandStateRulesTests
{
    private const long Created = 1_791_580_000_000;

    private const long Day = 24L * 60 * 60 * 1000;

    private static readonly string[] Finals = [CommandStates.Done, CommandStates.Rejected, CommandStates.Unknown, CommandStates.Expired];

    private static readonly string[] Opens = [CommandStates.Pending, CommandStates.Delivered];

    [Fact]
    public void ResultDeadlineMs是100秒()
    {
        CommandStateRules.ResultDeadlineMs.Should().Be(100_000);
        CommandStateRules.ResultDeadlineMs.Should().BeGreaterThanOrEqualTo(CommandEnvelope.MaxLifetimeMs + 30_000, "信封 60 秒＋電腦等完成信號 30 秒，還要留傳輸的時間");
        CommandStateRules.ExpiresAt(durable: false, Created).Should().Be(Created + 100_000);
        CommandStateRules.ExpiresAt(durable: true, Created).Should().Be(Created + 7 * Day);
    }

    [Fact]
    public void 其他常數照計畫()
    {
        CommandStateRules.OutputGraceMs.Should().Be(30_000);
        CommandStateRules.RetentionMs.Should().Be(30 * Day);
        CommandStateRules.MaxBusyRetries.Should().Be(3);
        CommandStateRules.BusyRetryDelayMs.Should().Be(5_000);
        CommandStateRules.RescanIntervalMs.Should().Be(60_000);
        CommandStateRules.PruneBatch.Should().Be(500);
        CommandStates.All.Should().Equal(["pending", "delivered", "done", "rejected", "unknown", "expired"]);
    }

    [Fact]
    public void 期限剛好到_unknown_差1毫秒不是()
    {
        var expiresAt = CommandStateRules.ExpiresAt(durable: false, Created);
        CommandStateRules.OnDeadline(CommandStates.Delivered, durable: false, expiresAt, now: expiresAt).Should().Be(CommandStates.Unknown);
        CommandStateRules.OnDeadline(CommandStates.Delivered, durable: false, expiresAt, now: expiresAt + 60_000).Should().Be(CommandStates.Unknown);
        CommandStateRules.OnDeadline(CommandStates.Delivered, durable: false, expiresAt, now: expiresAt - 1).Should().BeNull();
    }

    [Fact]
    public void durable的delivered到期_不變()
    {
        var expiresAt = CommandStateRules.ExpiresAt(durable: true, Created);
        CommandStateRules.OnDeadline(CommandStates.Delivered, durable: true, expiresAt, now: expiresAt + Day).Should().BeNull("durable 送達後沒回結果時保持 delivered，下次 Prove 重簽");
        CommandStateRules.OnDeadline(CommandStates.Pending, durable: true, expiresAt, now: expiresAt + Day).Should().BeNull();
    }

    [Fact]
    public void 期限對已結束的列不變()
    {
        foreach (var state in Finals)
            CommandStateRules.OnDeadline(state, durable: false, Created, now: Created + Day).Should().BeNull(state);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("delivered")]
    public void delivered收到done_變done而且轉送(string state)
    {
        CommandStateRules.OnResult(state, durable: false, RemoteResultStates.Done, null)
            .Should().Be(new CommandResultStep(CommandStates.Done, Forward: true, Resend: false, Late: false));
        CommandStateRules.OnResult(state, durable: true, RemoteResultStates.Done, null)
            .Should().Be(new CommandResultStep(CommandStates.Done, Forward: true, Resend: false, Late: false));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("delivered")]
    public void rejected與unknown同理(string state)
    {
        CommandStateRules.OnResult(state, durable: false, RemoteResultStates.Rejected, RemoteResultCodes.Stale)
            .Should().Be(new CommandResultStep(CommandStates.Rejected, Forward: true, Resend: false, Late: false));
        CommandStateRules.OnResult(state, durable: false, RemoteResultStates.Unknown, null)
            .Should().Be(new CommandResultStep(CommandStates.Unknown, Forward: true, Resend: false, Late: false));
        CommandStateRules.OnResult(state, durable: true, RemoteResultStates.Rejected, RemoteResultCodes.Policy)
            .Should().Be(new CommandResultStep(CommandStates.Rejected, Forward: true, Resend: false, Late: false), "durable 被政策拒絕是最終的");
    }

    [Fact]
    public void 已結束的再收到結果_狀態不變_Late()
    {
        foreach (var state in Finals)
        {
            foreach (var (result, code) in new (string State, string? Code)[] { (RemoteResultStates.Done, null), (RemoteResultStates.Rejected, RemoteResultCodes.Busy), (RemoteResultStates.Unknown, null) })
            {
                foreach (var durable in new[] { false, true })
                {
                    CommandStateRules.OnResult(state, durable, result, code)
                        .Should().Be(new CommandResultStep(state, Forward: false, Resend: false, Late: true), $"{state} 收到 {result}");
                }
            }
        }
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("expired")]
    public void durable收到busy或expired_回pending而且重送(string code)
    {
        foreach (var state in Opens)
        {
            CommandStateRules.OnResult(state, durable: true, RemoteResultStates.Rejected, code)
                .Should().Be(new CommandResultStep(CommandStates.Pending, Forward: false, Resend: true, Late: false), state);
        }
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("expired")]
    public void 非durable的busy是rejected而且轉送(string code)
    {
        // 突變：不分 durable 會讓這裡變成 pending＋重送（非 durable 絕不重送）
        CommandStateRules.OnResult(CommandStates.Delivered, durable: false, RemoteResultStates.Rejected, code)
            .Should().Be(new CommandResultStep(CommandStates.Rejected, Forward: true, Resend: false, Late: false));
    }

    [Fact]
    public void 不認得的結果狀態丟例外_不認得的列狀態不動()
    {
        var act = () => CommandStateRules.OnResult(CommandStates.Delivered, false, "accepted", null);
        act.Should().Throw<ArgumentOutOfRangeException>();
        CommandStateRules.OnResult("someday", false, RemoteResultStates.Done, null).Should().Be(new CommandResultStep("someday", false, false, true));
        CommandStateRules.OnStartup("someday", false, Created, Created + Day).Should().BeNull();
        CommandStateRules.OnDeadline("someday", false, Created, Created + Day).Should().BeNull();
        CommandStateRules.Prunable("someday", Created, Created + 365 * Day).Should().BeFalse();
        CommandStateRules.ClearsUnsignedBody("someday").Should().BeFalse();
    }

    [Fact]
    public void 啟動時非durable未結束_unknown()
    {
        foreach (var state in Opens)
        {
            CommandStateRules.OnStartup(state, durable: false, expiresAt: Created + 100_000, now: Created + 1).Should().Be(CommandStates.Unknown, $"{state}：絕不重送非 durable 的");
            CommandStateRules.OnStartup(state, durable: false, expiresAt: Created + 100_000, now: Created + Day).Should().Be(CommandStates.Unknown);
        }
    }

    [Fact]
    public void 啟動時durable過期_expired()
    {
        var expiresAt = CommandStateRules.ExpiresAt(durable: true, Created);
        foreach (var state in Opens)
        {
            CommandStateRules.OnStartup(state, durable: true, expiresAt, now: expiresAt).Should().Be(CommandStates.Expired, $"{state}：剛好 7 天");
            CommandStateRules.OnStartup(state, durable: true, expiresAt, now: expiresAt + 1).Should().Be(CommandStates.Expired);
        }
    }

    [Fact]
    public void 啟動時durable的delivered_pending()
    {
        var expiresAt = CommandStateRules.ExpiresAt(durable: true, Created);
        CommandStateRules.OnStartup(CommandStates.Delivered, durable: true, expiresAt, now: expiresAt - 1).Should().Be(CommandStates.Pending);
        CommandStateRules.OnStartup(CommandStates.Pending, durable: true, expiresAt, now: expiresAt - 1).Should().BeNull("pending 照舊等下次 Prove");
    }

    [Fact]
    public void 啟動時最終狀態不變()
    {
        foreach (var state in Finals)
        {
            foreach (var durable in new[] { false, true })
                CommandStateRules.OnStartup(state, durable, Created, now: Created + 30 * Day).Should().BeNull($"{state} durable={durable}");
        }
    }

    [Fact]
    public void durable的7天期限_剛好到期是expired_差1毫秒不是()
    {
        var expiresAt = CommandStateRules.ExpiresAt(durable: true, Created);
        foreach (var state in new[] { CommandStates.Pending, CommandStates.Delivered })
        {
            CommandStateRules.OnDurableExpiry(state, durable: true, expiresAt, now: expiresAt).Should().Be(CommandStates.Expired, $"{state}：剛好 7 天");
            CommandStateRules.OnDurableExpiry(state, durable: true, expiresAt, now: expiresAt + 1).Should().Be(CommandStates.Expired);
            CommandStateRules.OnDurableExpiry(state, durable: true, expiresAt, now: expiresAt - 1).Should().BeNull($"{state}：差 1 毫秒");
            CommandStateRules.OnDurableExpiry(state, durable: false, expiresAt, now: expiresAt + Day).Should().BeNull($"{state}：非 durable 的期限是 OnDeadline");
        }
        foreach (var state in Finals.Append("someday"))
            CommandStateRules.OnDurableExpiry(state, durable: true, expiresAt, now: expiresAt + Day).Should().BeNull($"{state}：已結束（或不認得）的不動");
    }

    [Fact]
    public void 結束30天剛好可清_未結束的永不清()
    {
        var doneAt = Created + 1000;
        foreach (var state in Finals)
        {
            CommandStateRules.Prunable(state, doneAt, now: doneAt + 30 * Day).Should().BeTrue($"{state} 剛好 30 天");
            CommandStateRules.Prunable(state, doneAt, now: doneAt + 30 * Day - 1).Should().BeFalse($"{state} 差 1 毫秒");
            CommandStateRules.Prunable(state, null, now: doneAt + 365 * Day).Should().BeFalse("沒有 done_at（不應發生）不清");
        }

        foreach (var state in Opens)
            CommandStateRules.Prunable(state, doneAt, now: doneAt + 365 * Day).Should().BeFalse($"{state} 永不清");
    }

    [Fact]
    public void ResignExp不超過created加7天()
    {
        CommandStateRules.ResignExp(Created, now: Created + 1000).Should().Be(Created + 1000 + CommandEnvelope.MaxLifetimeMs);
        var last = Created + 7 * Day - 10_000;
        CommandStateRules.ResignExp(Created, now: last).Should().Be(Created + 7 * Day, "最後一分鐘內重簽：exp 截在 created＋7 天");
        CommandStateRules.ResignExp(Created, now: Created + 7 * Day).Should().Be(Created + 7 * Day);
    }

    [Fact]
    public void ClearsUnsignedBody只在最終狀態與expired()
    {
        foreach (var state in Finals)
            CommandStateRules.ClearsUnsignedBody(state).Should().BeTrue(state);
        foreach (var state in Opens)
            CommandStateRules.ClearsUnsignedBody(state).Should().BeFalse($"{state} 還要靠它重簽");
        CommandStates.IsFinal(CommandStates.Expired).Should().BeTrue();
        CommandStates.IsOpen(CommandStates.Expired).Should().BeFalse();
    }

    // ───── result_json（步 7，D15、D18） ─────

    [Fact]
    public void ResultJson逐字()
    {
        CommandStateRules.ResultJson(CommandStates.Rejected, RemoteResultCodes.Stale, "這張卡片已經處理了", 1, false, Created)
            .Should().Be($$"""{"state":"rejected","code":"stale","message":"這張卡片已經處理了","tier":1,"fullAccess":false,"at":{{Created}}}""");
        CommandStateRules.ResultJson(CommandStates.Unknown, null, null, null, null, Created)
            .Should().Be($$"""{"state":"unknown","code":null,"message":null,"tier":null,"fullAccess":null,"at":{{Created}}}""", "伺服器自己判的 unknown 沒有代碼與層級");
    }

    [Fact]
    public void WithLate最多4筆_保留先到的_其他鍵不動()
    {
        var json = CommandStateRules.ResultJson(CommandStates.Done, null, "2026-10-20T09:00:00Z 完成", 0, false, Created);
        for (var i = 1; i <= 6; i++)
            json = CommandStateRules.WithLate(json, RemoteResultStates.Rejected, RemoteResultCodes.Conflict, null, null, null, Created + i);

        var root = Newtonsoft.Json.Linq.JObject.Parse(json);
        ((string?)root["state"], (string?)root["message"]).Should().Be((CommandStates.Done, "2026-10-20T09:00:00Z 完成"), "原本的結果不動（像日期的字串也原樣）");
        var late = (Newtonsoft.Json.Linq.JArray)root["late"]!;
        late.Should().HaveCount(CommandStateRules.MaxLateResults);
        late.Select(l => (long)l["at"]!).Should().Equal([Created + 1, Created + 2, Created + 3, Created + 4], "保留先到的");
        late[0].ToString(Newtonsoft.Json.Formatting.None).Should().Be(
            CommandStateRules.ResultJson(RemoteResultStates.Rejected, RemoteResultCodes.Conflict, null, null, null, Created + 1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("不是 JSON")]
    [InlineData("[1,2]")]
    public void WithLate從null或壞掉的result_json開始(string? existing)
    {
        var json = CommandStateRules.WithLate(existing, RemoteResultStates.Done, null, null, 1, true, Created);
        json.Should().Be($$"""{"late":[{"state":"done","code":null,"message":null,"tier":1,"fullAccess":true,"at":{{Created}}}]}""");
    }
}
