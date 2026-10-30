using MyriagentHub.Audit;
using MyriagentHub.Data;
using MyriagentHub.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace MyriagentHub.Tests.Security;

/// <summary>SEC-17：稽核雜湊鏈——改任一列、刪列、插列、調換順序、刪掉開頭的列都被偵測</summary>
public sealed class AuditChainTests
{
    [Fact]
    public void 完整的鏈驗證通過()
    {
        var rows = Chain(10);
        var result = AuditRules.Verify(rows, AuditRules.GenesisHash(), 1);
        result.Ok.Should().BeTrue();
        result.Checked.Should().Be(10);
        result.LastSeq.Should().Be(10);
    }

    [Theory]
    [InlineData("detail")]
    [InlineData("actor")]
    [InlineData("outcome")]
    [InlineData("at")]
    [InlineData("null-to-empty")]
    public void 改任何一列的任何欄位_在那一列被偵測(string field)
    {
        var rows = Chain(10);
        var r = rows[4];
        var content = field switch
        {
            "detail" => r.Content with { Detail = "{\"x\":2}" },
            "actor" => r.Content with { ActorType = "owner" },
            "outcome" => r.Content with { Outcome = "ok" },
            "at" => r.Content with { At = r.Content.At + 1 },
            _ => r.Content with { Target = string.Empty },
        };
        rows[4] = r with { Content = content };
        var result = AuditRules.Verify(rows, AuditRules.GenesisHash(), 1);
        result.Ok.Should().BeFalse();
        result.BrokenAtSeq.Should().Be(5);
    }

    [Fact]
    public void 刪掉中間一列_在下一列被偵測()
    {
        var rows = Chain(10);
        rows.RemoveAt(4);
        AuditRules.Verify(rows, AuditRules.GenesisHash(), 1).BrokenAtSeq.Should().Be(6);
    }

    [Fact]
    public void 調換兩列_被偵測()
    {
        var rows = Chain(10);
        (rows[3], rows[4]) = (rows[4] with { Seq = 4 }, rows[3] with { Seq = 5 });
        AuditRules.Verify(rows, AuditRules.GenesisHash(), 1).Ok.Should().BeFalse();
    }

    [Fact]
    public void 插入一列_被偵測()
    {
        var rows = Chain(10);
        var forged = new AuditContent(9, "admin", null, null, "owner.reset", null, "ok", null);
        rows.Insert(5, new AuditChainRow(6, forged, rows[4].Hash, AuditRules.ComputeHash(rows[4].Hash, forged)));
        for (var i = 6; i < rows.Count; i++)
            rows[i] = rows[i] with { Seq = i + 1 };
        AuditRules.Verify(rows, AuditRules.GenesisHash(), 1).BrokenAtSeq.Should().Be(7, "插入列之後的那一列 prev_hash 對不上");
    }

    [Fact]
    public void 刪掉開頭的列_與錨點不符()
    {
        var rows = Chain(10);
        var anchor = rows[3].PrevHash;
        var trimmed = rows.Skip(3).ToList();
        AuditRules.Verify(trimmed, anchor, 1).Ok.Should().BeTrue("保留期清除後從錨點接起");
        AuditRules.Verify(trimmed.Skip(1), anchor, 1).Ok.Should().BeFalse("再多刪一列就與錨點不符");
        AuditRules.Verify(trimmed, AuditRules.GenesisHash(), 1).Ok.Should().BeFalse("沒有錨點時開頭的列不能少");
    }

    [Fact]
    public void 欄位邊界明確_不能靠搬移字元偽造相同雜湊()
    {
        var a = new AuditContent(1, "owner", "ab", null, "c", null, "ok", null);
        var b = new AuditContent(1, "owner", "a", null, "bc", null, "ok", null);
        AuditRules.ComputeHash(AuditRules.GenesisHash(), a).Should().NotEqual(AuditRules.ComputeHash(AuditRules.GenesisHash(), b));
        var nullTarget = new AuditContent(1, "owner", null, null, "x", null, "ok", null);
        var emptyTarget = nullTarget with { Target = string.Empty };
        AuditRules.ComputeHash(AuditRules.GenesisHash(), nullTarget).Should().NotEqual(AuditRules.ComputeHash(AuditRules.GenesisHash(), emptyTarget));
    }

    [Fact]
    public async Task 資料庫裡的稽核列被改_驗證找出那一列且健康狀態降級()
    {
        await using var owner = await OwnerHost.StartAsync();
        var audit = owner.Host.Services.GetRequiredService<AuditLog>();
        for (var i = 0; i < 5; i++)
            await audit.WriteAsync(new AuditEntry(AuditActors.System, null, null, "test.event", "t" + i, AuditOutcomes.Ok, new { i }));
        (await audit.VerifyAsync()).Ok.Should().BeTrue();

        long target;
        using (var scope = owner.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            target = await db.Audit.Where(a => a.Action == "test.event" && a.Target == "t2").Select(a => a.Seq).SingleAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE audit SET outcome = 'denied' WHERE seq = {target}");
        }
        var result = await audit.VerifyAsync();
        result.Ok.Should().BeFalse();
        result.BrokenAtSeq.Should().Be(target);

        using var client = owner.Host.CreateClient();
        var health = await client.GetJsonAsync("/healthz");
        health.GetProperty("status").GetString().Should().Be("degraded");
    }

    [Fact]
    public async Task 保留期清除舊列後鏈仍可驗證_錨點記在kv()
    {
        await using var owner = await OwnerHost.StartAsync();
        var audit = owner.Host.Services.GetRequiredService<AuditLog>();
        await audit.WriteAsync(new AuditEntry(AuditActors.System, null, null, "test.old", null, AuditOutcomes.Ok));
        owner.Host.Clock.Advance(TimeSpan.FromDays(401));
        await audit.WriteAsync(new AuditEntry(AuditActors.System, null, null, "test.new", null, AuditOutcomes.Ok));
        var removed = await audit.PruneAsync(400);
        removed.Should().BeGreaterThan(0);
        var verification = await audit.VerifyAsync();
        verification.Ok.Should().BeTrue(verification.Reason);

        var anchor = await owner.Host.Services.GetRequiredService<KvStore>().GetAsync<AuditAnchor>(KvKeys.AuditAnchor);
        anchor.Should().NotBeNull();
        using (var scope = owner.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            (await db.Audit.AnyAsync(a => a.Action == "test.old")).Should().BeFalse();
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM audit WHERE seq = {anchor!.Seq}");
        }
        (await audit.VerifyAsync()).Ok.Should().BeFalse("錨點之後的開頭列被刪也偵測得到");
    }

    private static List<AuditChainRow> Chain(int count)
    {
        var rows = new List<AuditChainRow>();
        var prev = AuditRules.GenesisHash();
        for (var i = 1; i <= count; i++)
        {
            var content = new AuditContent(1_000 + i, "system", null, "203.0.113.1", "event." + i, "target" + i, "denied", "{\"i\":" + i + "}");
            var hash = AuditRules.ComputeHash(prev, content);
            rows.Add(new AuditChainRow(i, content, prev, hash));
            prev = hash;
        }
        return rows;
    }
}
