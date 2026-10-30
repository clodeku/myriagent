using Myriagent.Sync;
using Newtonsoft.Json;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>
/// 各帳戶 token 計數的同步（tok1、tok2，sync-protocol 14.9、14.10）：伺服器的檢查、桌面換成上傳的格與決定送什麼、伺服器加總成瀏覽器的 Tokens、
/// 伺服器給每台的其他電腦合計、桌面收件的檢查與取天數合計
/// </summary>
public sealed class TokenTallySyncRulesTests
{
    private static readonly string La1 = SyncIds.NewLogicalAccountId();
    private static readonly string La2 = SyncIds.NewLogicalAccountId();

    [Fact]
    public void 檢查_帳戶日期數字與重複的格()
    {
        TokenTallySyncRules.Check(new TokensPublish(true, [])).Should().BeNull("空的整份＝這台沒有任何計數");
        TokenTallySyncRules.Check(new TokensPublish(false, [new TokenTallyRow(La1, "2026-10-23", 10, 4, 1), new TokenTallyRow(La1, "2026-10-22", 0, 0, 0)])).Should().BeNull();
        TokenTallySyncRules.Check(null).Should().NotBeNull();
        TokenTallySyncRules.Check(new TokensPublish(true, null!)).Should().NotBeNull();
        TokenTallySyncRules.Check(new TokensPublish(true, [null!])).Should().NotBeNull();
        TokenTallySyncRules.Check(new TokensPublish(true, [new TokenTallyRow("acct-1", "2026-10-23", 1, 0, 1)])).Should().NotBeNull("要是邏輯帳戶 id");
        foreach (var day in new[] { "2026-02-30", "2026-1-23", "20261023", "2026-10-23T00:00", "", null })
            TokenTallySyncRules.Check(new TokensPublish(true, [new TokenTallyRow(La1, day!, 1, 0, 1)])).Should().NotBeNull(day ?? "null");
        TokenTallySyncRules.Check(new TokensPublish(true, [new TokenTallyRow(La1, "2026-10-23", -1, 0, 1)])).Should().NotBeNull("負數");
        TokenTallySyncRules.Check(new TokensPublish(true, [new TokenTallyRow(La1, "2026-10-23", TokenTallySyncRules.MaxCount + 1, 0, 1)])).Should().NotBeNull("超過上限");
        TokenTallySyncRules.Check(new TokensPublish(true, [new TokenTallyRow(La1, "2026-10-23", 1, 0, 1), new TokenTallyRow(La1, "2026-10-23", 2, 0, 2)]))
            .Should().NotBeNull("同一格兩次");
        var many = Enumerable.Range(0, TokenTallySyncRules.MaxRows + 1).Select(i => new TokenTallyRow(SyncIds.NewLogicalAccountId(), "2026-10-23", 1, 0, 1)).ToList();
        TokenTallySyncRules.Check(new TokensPublish(true, many)).Should().NotBeNull("格數太多");
        TokenTallySyncRules.Check(new TokensPublish(true, many.Take(TokenTallySyncRules.MaxRows).ToList())).Should().BeNull();
    }

    [Fact]
    public void 上限內的整份放得進一則訊息()
    {
        var rows = Enumerable.Range(0, TokenTallySyncRules.MaxRows)
            .Select(i => new TokenTallyRow(SyncIds.NewLogicalAccountId(), "2026-10-23", TokenTallySyncRules.MaxCount, TokenTallySyncRules.MaxCount, TokenTallySyncRules.MaxCount))
            .ToList();
        SyncProtocol.FitsInvocation(System.Text.Encoding.UTF8.GetByteCount(SyncJson.Serialize(new TokensPublish(true, rows)))).Should().BeTrue();
    }

    [Fact]
    public void 本機的格換成邏輯帳戶_同格加總_沒連結與0與保留以外的略過()
    {
        var today = new DateOnly(2026, 10, 23);
        var links = new Dictionary<string, string> { ["a1"] = La1, ["a2"] = La1, ["a3"] = La2 };
        var rows = TokenTallySyncRules.ToRows(
        [
            new LocalTokenTally("a1", "2026-10-23", 100, 40, 10),
            new LocalTokenTally("a2", "2026-10-23", 5, 0, 1),
            new LocalTokenTally("a3", "2026-10-22", 7, 0, 3),
            new LocalTokenTally("a3", "2026-10-21", 0, 0, 0),
            new LocalTokenTally("a3", "2026-09-24", 1, 0, 1),
            new LocalTokenTally("a3", "2026-09-23", 1, 0, 1),
            new LocalTokenTally("a3", "2026-10-25", 2, 0, 2),
            new LocalTokenTally("a3", "bad", 2, 0, 2),
            new LocalTokenTally("gone", "2026-10-23", 9, 9, 9),
        ], links, today);

        rows.Should().Equal(
            new TokenTallyRow(La2, "2026-10-25", 2, 0, 2),
            new TokenTallyRow(La1, "2026-10-23", 105, 40, 11),
            new TokenTallyRow(La2, "2026-10-22", 7, 0, 3),
            new TokenTallyRow(La2, "2026-09-24", 1, 0, 1));
    }

    [Fact]
    public void 決定送什麼_還沒送過或少了格或變小送整份_否則只送變了的()
    {
        var a = new TokenTallyRow(La1, "2026-10-23", 100, 40, 10);
        var b = new TokenTallyRow(La2, "2026-10-23", 5, 0, 1);
        var current = new List<TokenTallyRow> { a, b };
        TokenTallySyncRules.Plan(null, current).Should().BeEquivalentTo(new TokensPublish(true, current));
        TokenTallySyncRules.Plan(null, []).Should().BeEquivalentTo(new TokensPublish(true, []), "空的也送一次：清掉伺服器上這台的舊格");

        var sent = current.ToDictionary(r => (r.LogicalAccountId, r.Day));
        TokenTallySyncRules.Plan(sent, current).Should().BeNull("沒變不送");

        var grown = a with { Input = 150, Output = 12 };
        var c = new TokenTallyRow(La1, "2026-10-24", 1, 0, 1);
        TokenTallySyncRules.Plan(sent, [grown, b, c]).Should().BeEquivalentTo(new TokensPublish(false, [grown, c]));

        TokenTallySyncRules.Plan(sent, [a]).Should().BeEquivalentTo(new TokensPublish(true, [a]), "少了一格（連結拿掉、滑出保留範圍）");
        TokenTallySyncRules.Plan(sent, [a with { Cached = 39 }, b]).Should().BeEquivalentTo(new TokensPublish(true, [a with { Cached = 39 }, b]), "變小（計數檔被重設）");
    }

    [Fact]
    public void 加總成瀏覽器的Tokens_今天7天30天_比今天晚與30天前的不算_依帳戶排序()
    {
        var today = new DateOnly(2026, 10, 23);
        var view = TokenTallySyncRules.View(
        [
            new TokenTallyRow(La2, "2026-10-23", 100, 40, 10),
            new TokenTallyRow(La2, "2026-10-23", 1, 0, 1),
            new TokenTallyRow(La2, "2026-10-17", 10, 0, 1),
            new TokenTallyRow(La2, "2026-10-16", 20, 0, 2),
            new TokenTallyRow(La2, "2026-09-24", 30, 0, 3),
            new TokenTallyRow(La2, "2026-09-23", 999, 0, 9),
            new TokenTallyRow(La2, "2026-10-24", 999, 0, 9),
            new TokenTallyRow(La1, "2026-09-23", 999, 0, 9),
        ], today);

        view.ToString(Formatting.None).Should().Be(
            "{\"day\":\"2026-10-23\",\"scope\":\"all\",\"accounts\":{\"" + La2 + "\":{"
            + "\"today\":{\"input\":101,\"cached\":40,\"output\":11},"
            + "\"week\":{\"input\":111,\"cached\":40,\"output\":12},"
            + "\"month\":{\"input\":161,\"cached\":40,\"output\":17}}}}",
            "La1 只有 30 天前的格：不列（網頁照舊顯示方案名稱）");

        var sorted = TokenTallySyncRules.View([new TokenTallyRow("la_b", "2026-10-23", 1, 0, 1), new TokenTallyRow("la_a", "2026-10-23", 1, 0, 1)], today);
        ((Newtonsoft.Json.Linq.JObject)sorted["accounts"]!).Properties().Select(p => p.Name).Should().Equal("la_a", "la_b");
        TokenTallySyncRules.View([], today).ToString(Formatting.None).Should().Be("{\"day\":\"2026-10-23\",\"scope\":\"all\",\"accounts\":{}}");
    }
    [Fact]
    public void 其他電腦的合計_不算收件的這台_同一格相加_全是0的不送_日期新到舊()
    {
        const string self = "d_self", b = "d_b", c = "d_c";
        var update = TokenTallySyncRules.Others(
        [
            (self, new TokenTallyRow(La1, "2026-10-23", 999, 999, 999)),
            (b, new TokenTallyRow(La1, "2026-10-23", 100, 40, 10)),
            (c, new TokenTallyRow(La1, "2026-10-23", 1, 0, 1)),
            (b, new TokenTallyRow(La2, "2026-10-22", 7, 0, 3)),
            (c, new TokenTallyRow(La2, "2026-10-21", 0, 0, 0)),
            (self, new TokenTallyRow(La2, "2026-10-20", 5, 0, 5)),
        ], self);

        update.Rows.Should().Equal(new TokenTallyRow(La1, "2026-10-23", 101, 40, 11), new TokenTallyRow(La2, "2026-10-22", 7, 0, 3));
        TokenTallySyncRules.CheckUpdate(update).Should().BeNull("伺服器組的一定合格");
        TokenTallySyncRules.Others([(self, new TokenTallyRow(La1, "2026-10-23", 1, 0, 1))], self).Rows.Should().BeEmpty("只有這台自己");

        var huge = TokenTallySyncRules.Others(
            [(b, new TokenTallyRow(La1, "2026-10-23", TokenTallySyncRules.MaxCount, 0, 1)), (c, new TokenTallyRow(La1, "2026-10-23", TokenTallySyncRules.MaxCount, 0, 1))], self);
        huge.Rows.Single().Input.Should().Be(TokenTallySyncRules.MaxCount, "相加不超過上限（桌面的檢查才過得了）");
        TokenTallySyncRules.CheckUpdate(huge).Should().BeNull();
    }

    [Fact]
    public void 桌面收件的檢查同伺服器的檢查()
    {
        TokenTallySyncRules.CheckUpdate(new TokensUpdate([])).Should().BeNull("其他電腦都沒有計數");
        TokenTallySyncRules.CheckUpdate(null).Should().NotBeNull();
        TokenTallySyncRules.CheckUpdate(new TokensUpdate(null!)).Should().NotBeNull();
        TokenTallySyncRules.CheckUpdate(new TokensUpdate([new TokenTallyRow(La1, "2026-02-30", 1, 0, 1)])).Should().NotBeNull("不存在的日期");
        TokenTallySyncRules.CheckUpdate(new TokensUpdate([new TokenTallyRow(La1, "2026-10-23", 1, 0, 1), new TokenTallyRow(La1, "2026-10-23", 1, 0, 1)]))
            .Should().NotBeNull("同一格兩次");
    }

    [Fact]
    public void 取一個帳戶的天數合計_依這台的日期_比今天晚的不算()
    {
        var today = new DateOnly(2026, 10, 23);
        TokenTallyRow[] rows =
        [
            new(La1, "2026-10-23", 100, 40, 10),
            new(La1, "2026-10-17", 10, 0, 1),
            new(La1, "2026-10-16", 20, 0, 2),
            new(La1, "2026-09-24", 30, 0, 3),
            new(La1, "2026-09-23", 999, 0, 9),
            new(La1, "2026-10-24", 999, 0, 9),
            new(La2, "2026-10-23", 5, 5, 5),
        ];

        TokenTallySyncRules.Sum(rows, La1, today, 1).Should().Be((100L, 40L, 10L));
        TokenTallySyncRules.Sum(rows, La1, today, 7).Should().Be((110L, 40L, 11L), "10-17 是近 7 天的第一天");
        TokenTallySyncRules.Sum(rows, La1, today, TokenTallySyncRules.RetentionDays).Should().Be((160L, 40L, 16L), "09-24 是近 30 天的第一天");
        TokenTallySyncRules.Sum(rows, "la_none", today, 30).Should().Be((0L, 0L, 0L));
    }
}
