using Myriagent.Security;
using Myriagent.Sync;
using MyriagentHub.Data;
using MyriagentHub.Pairing;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.TestSupport.BrowserFixtures;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 各帳戶的 token 計數（sync-protocol 14.9，tok1）：裝置的 TokensPublish 寫進 token_tallies（full 整份取代、否則逐格取代、刪掉保留天數以外的），
/// 瀏覽器的 init 與 tokens 訊息是各電腦的合計（伺服器的本機日期；略過撤銷的裝置、刪除的帳戶）；沒協商出 tok1、內容不合格回 invalid。
/// 協商出 tok2 的裝置連線收 TokensUpdate：它以外的電腦的合計（14.10）
/// </summary>
public sealed class TokenTallyTests
{
    /// <summary>HubTestHost 的時鐘是 2026-10-11 09:00 UTC（本機時區 UTC）</summary>
    private const string Today = "2026-10-11";

    [Fact]
    public async Task 端到端_各電腦合計_逐格取代_整份取代_撤銷的裝置不算()
    {
        await using var host = await HubTestHost.StartAsync();
        using var a = await TestDevice.AddAsync(host, "電腦甲");
        using var b = await TestDevice.AddAsync(host, "電腦乙");
        var (la1, la2, la3) = await AddAccountsAsync(host);
        await using var ca = await DeviceHubClient.ConnectAsync(host, a.Token);
        await using var cb = await DeviceHubClient.ConnectAsync(host, b.Token);
        var observed = DeviceHubClient.ObservedSpki(host);
        (await ca.HandshakeAsync(a, observed, features: [DeviceFeatureRules.Tok1])).Hello.Features.Should().Contain(DeviceFeatureRules.Tok1);
        await cb.HandshakeAsync(b, observed, features: [DeviceFeatureRules.Tok1]);

        await ca.Connection.InvokeAsync(DeviceHubMethods.TokensPublish, new TokensPublish(true,
        [
            new TokenTallyRow(la1, Today, 100, 40, 10),
            new TokenTallyRow(la1, "2026-10-05", 50, 0, 5),
            new TokenTallyRow(la2, "2026-09-12", 7, 0, 3),
            new TokenTallyRow(la2, "2026-09-01", 9, 0, 9),
            new TokenTallyRow(la3, Today, 1, 0, 1),
        ]));
        await cb.Connection.InvokeAsync(DeviceHubMethods.TokensPublish, new TokensPublish(true, [new TokenTallyRow(la1, Today, 1, 0, 1)]));

        CountRows(host, "day = '2026-09-01'").Should().Be(0, "保留天數以外的列寫入時就刪掉");

        await using var owner = await LoginAndConnectAsync(host);
        var tokens = (JObject)(await owner.Hub.ReadyAsync())["tokens"]!;
        tokens["day"]!.Value<string>().Should().Be(Today, "伺服器的本機日期");
        tokens["scope"]!.Value<string>().Should().Be("all");
        Count(tokens, la1, "today").Should().Be((101, 40, 11), "兩台的合計");
        Count(tokens, la1, "week").Should().Be((151, 40, 16), "10-05 在近 7 天內");
        Count(tokens, la2, "today").Should().Be((0, 0, 0));
        Count(tokens, la2, "week").Should().Be((0, 0, 0));
        Count(tokens, la2, "month").Should().Be((7, 0, 3), "09-12 是近 30 天的第一天");
        ((JObject)tokens["accounts"]!).Should().NotContainKey(la3, "刪除的帳戶不列");

        // 逐格取代：只送變了的格，其他格照舊
        await ca.Connection.InvokeAsync(DeviceHubMethods.TokensPublish, new TokensPublish(false, [new TokenTallyRow(la1, Today, 200, 80, 20)]));
        var pushed = await owner.Hub.WaitForAsync("tokens", m => Count((JObject)m["tokens"]!, la1, "today") == (201, 80, 21), 10_000);
        Count((JObject)pushed["tokens"]!, la1, "week").Should().Be((251, 80, 26));

        // 整份取代：甲這台的格全部換掉（空的＝甲沒有任何計數）
        await ca.Connection.InvokeAsync(DeviceHubMethods.TokensPublish, new TokensPublish(true, []));
        pushed = await owner.Hub.WaitForAsync("tokens", m => Count((JObject)m["tokens"]!, la1, "today") == (1, 0, 1), 10_000);
        ((JObject)pushed["tokens"]!["accounts"]!).Should().NotContainKey(la2, "只有甲用過的帳戶不再列出");

        // 撤銷乙：它的格留在表裡，但不再算
        await host.Services.GetRequiredService<PairingService>().RevokeAsync(b.Id, DeviceRegistryRules.RevokedRetired, null, null);
        pushed = await owner.Hub.WaitForAsync("tokens", m => ((JObject)m["tokens"]!["accounts"]!).Count == 0, 10_000);
        pushed["tokens"]!["scope"]!.Value<string>().Should().Be("all");
        CountRows(host, "device_id = '" + b.Id + "'").Should().Be(1, "撤銷不刪列，讀的一方略過");
    }

    [Fact]
    public async Task 沒協商出tok1_或內容不合格_invalid()
    {
        await using var host = await HubTestHost.StartAsync();
        using var a = await TestDevice.AddAsync(host);
        using var b = await TestDevice.AddAsync(host);
        var la = SyncIds.NewLogicalAccountId();
        await using var ca = await DeviceHubClient.ConnectAsync(host, a.Token);
        await using var cb = await DeviceHubClient.ConnectAsync(host, b.Token);
        var observed = DeviceHubClient.ObservedSpki(host);
        await ca.HandshakeAsync(a, observed, features: [DeviceFeatureRules.Conv1]);
        await cb.HandshakeAsync(b, observed, features: [DeviceFeatureRules.Tok1]);

        await FluentActions.Invoking(() => ca.Connection.InvokeAsync(DeviceHubMethods.TokensPublish, new TokensPublish(true, [new TokenTallyRow(la, Today, 1, 0, 1)])))
            .Should().ThrowAsync<HubException>().WithMessage("*invalid*", "沒協商出 tok1");
        await FluentActions.Invoking(() => cb.Connection.InvokeAsync(DeviceHubMethods.TokensPublish, new TokensPublish(true, [new TokenTallyRow(la, "2026-02-30", 1, 0, 1)])))
            .Should().ThrowAsync<HubException>().WithMessage("*invalid*", "不存在的日期");
        await FluentActions.Invoking(() => cb.Connection.InvokeAsync(DeviceHubMethods.TokensPublish, new TokensPublish(true, [new TokenTallyRow(la, Today, -1, 0, 1)])))
            .Should().ThrowAsync<HubException>().WithMessage("*invalid*", "負數");
        CountRows(host, "1 = 1").Should().Be(0);

        await cb.Connection.InvokeAsync(DeviceHubMethods.TokensPublish, new TokensPublish(true, [new TokenTallyRow(la, Today, 1, 0, 1)]));
        CountRows(host, "1 = 1").Should().Be(1, "合格的照收（帳戶不在目錄裡的格只是不顯示）");
    }

    [Fact]
    public async Task tok2_給每台其他電腦的合計_連上就送_有變才送_刪除的帳戶與撤銷的裝置不算_沒協商出的不送()
    {
        await using var host = await HubTestHost.StartAsync();
        using var a = await TestDevice.AddAsync(host, "電腦甲");
        using var b = await TestDevice.AddAsync(host, "電腦乙");
        using var c = await TestDevice.AddAsync(host, "電腦丙");
        var (la1, la2, la3) = await AddAccountsAsync(host);
        await using var ca = await DeviceHubClient.ConnectAsync(host, a.Token);
        await using var cb = await DeviceHubClient.ConnectAsync(host, b.Token);
        await using var cc = await DeviceHubClient.ConnectAsync(host, c.Token);
        var observed = DeviceHubClient.ObservedSpki(host);
        (await ca.HandshakeAsync(a, observed, features: [DeviceFeatureRules.Tok1, DeviceFeatureRules.Tok2])).Hello.Features.Should().Contain(DeviceFeatureRules.Tok2);
        var sinceProven = System.Diagnostics.Stopwatch.StartNew();
        await cb.HandshakeAsync(b, observed, features: [DeviceFeatureRules.Tok1, DeviceFeatureRules.Tok2]);
        await cc.HandshakeAsync(c, observed, features: [DeviceFeatureRules.Tok1]);

        (await ca.WaitForAsync<TokensUpdate>(timeoutMs: 20_000)).Rows.Should().BeEmpty("連上就送一次（還沒有任何計數）");
        sinceProven.Elapsed.Should().BeGreaterThan(TokenTallySyncRules.FirstSendDelay - TimeSpan.FromSeconds(1),
            "證明完成之後等一下才送：舊版桌面在交握完成前收到會斷線");

        await cb.Connection.InvokeAsync(DeviceHubMethods.TokensPublish, new TokensPublish(true,
            [new TokenTallyRow(la1, Today, 100, 40, 10), new TokenTallyRow(la3, Today, 1, 0, 1)]));
        await cc.Connection.InvokeAsync(DeviceHubMethods.TokensPublish, new TokensPublish(true,
            [new TokenTallyRow(la1, Today, 1, 0, 1), new TokenTallyRow(la2, "2026-10-05", 7, 0, 3)]));
        await ca.Connection.InvokeAsync(DeviceHubMethods.TokensPublish, new TokensPublish(true, [new TokenTallyRow(la1, Today, 5000, 0, 500)]));

        var toA = await ca.WaitForAsync<TokensUpdate>(u => u.Rows.Count == 2 && u.Rows[0].Input == 101, 15_000);
        toA.Rows.Should().Equal(new TokenTallyRow(la1, Today, 101, 40, 11), new TokenTallyRow(la2, "2026-10-05", 7, 0, 3));
        var toB = await cb.WaitForAsync<TokensUpdate>(u => u.Rows.Any(r => r.LogicalAccountId == la1 && r.Input == 5001), 15_000);
        toB.Rows.Should().Equal(new[] { new TokenTallyRow(la1, Today, 5001, 0, 501), new TokenTallyRow(la2, "2026-10-05", 7, 0, 3) },
            "乙收到的是甲與丙的合計（不含自己的），刪除的 la3 不算");

        // 內容沒變不重送：乙改了 la3（刪除的帳戶）的格、甲改了自己的，甲收到的合計都一樣
        await cb.Connection.InvokeAsync(DeviceHubMethods.TokensPublish, new TokensPublish(false, [new TokenTallyRow(la3, Today, 2, 0, 2)]));
        await ca.Connection.InvokeAsync(DeviceHubMethods.TokensPublish, new TokensPublish(false, [new TokenTallyRow(la1, Today, 6000, 0, 600)]));
        await cb.WaitForAsync<TokensUpdate>(u => u.Rows.Any(r => r.LogicalAccountId == la1 && r.Input == 6001), 15_000);
        await Task.Delay(500);
        Drain(ca).OfType<TokensUpdate>().Should().BeEmpty("甲的合計沒變");

        // 撤銷丙：它的格不再算（下一次重讀）
        await host.Services.GetRequiredService<PairingService>().RevokeAsync(c.Id, DeviceRegistryRules.RevokedRetired, null, null);
        await cb.Connection.InvokeAsync(DeviceHubMethods.TokensPublish, new TokensPublish(false, [new TokenTallyRow(la1, Today, 200, 80, 20)]));
        var afterRevoke = await ca.WaitForAsync<TokensUpdate>(u => u.Rows.Count == 1, 15_000);
        afterRevoke.Rows.Should().Equal(new TokenTallyRow(la1, Today, 200, 80, 20));

        Drain(cc).OfType<TokensUpdate>().Should().BeEmpty("丙沒協商出 tok2");
    }

    /// <summary>已經收到的伺服器訊息（不等）</summary>
    private static List<object> Drain(DeviceHubClient client)
    {
        var list = new List<object>();
        while (client.Received.Reader.TryRead(out var message))
            list.Add(message);
        return list;
    }

    [Fact]
    public async Task 沒有任何電腦回報過_tokens是空的清單()
    {
        await using var host = await HubTestHost.StartAsync();
        var data = host.Services.GetRequiredService<AccountViewBuilder>().Read(BrowserParts.All, null, host.Clock.NowMs);
        data.TokensJson.Should().Be("""{"day":"2026-10-11","scope":"all","accounts":{}}""");
    }

    /// <summary>三個邏輯帳戶：la1、la2 照常，la3 已刪除</summary>
    private static async Task<(string, string, string)> AddAccountsAsync(HubTestHost host)
    {
        var ids = (SyncIds.NewLogicalAccountId(), SyncIds.NewLogicalAccountId(), SyncIds.NewLogicalAccountId());
        await host.Services.GetRequiredService<SyncWriter>().WriteAsync(ctx =>
        {
            var i = 0;
            foreach (var id in new[] { ids.Item1, ids.Item2, ids.Item3 })
            {
                ctx.Execute("INSERT INTO logical_accounts(logical_account_id, provider, identity_key, label, color, sort_order, version, created_at) "
                    + "VALUES($id, 'Claude', $key, '帳戶', '#D97757', $i, 1, 1)", ("$id", id), ("$key", "claude:org:" + id + "@example.com"), ("$i", i++));
            }
            ctx.Execute("UPDATE logical_accounts SET deleted_at = 1 WHERE logical_account_id = $id", ("$id", ids.Item3));
        });
        return ids;
    }

    private static (long Input, long Cached, long Output) Count(JObject tokens, string account, string range)
    {
        var c = tokens["accounts"]![account]![range]!;
        return (c.Value<long>("input"), c.Value<long>("cached"), c.Value<long>("output"));
    }

    private static long CountRows(HubTestHost host, string where)
    {
        using var read = host.Services.GetRequiredService<SyncWriter>().OpenRead();
        return (long)SqliteSetup.Scalar(read, "SELECT COUNT(*) FROM token_tallies WHERE " + where)!;
    }
}
