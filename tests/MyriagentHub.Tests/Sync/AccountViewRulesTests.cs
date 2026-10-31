using System.Text.RegularExpressions;
using Myriagent.Agents;
using Myriagent.Sync;
using MyriagentHub.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Sync;

/// <summary>
/// 瀏覽器模式的 AccountView、DeviceView、DeviceCliView、Settings、ProviderView（web-protocol 第三、六節；設計 14.6）：
/// 輸出白名單、憑證與本機路徑不出伺服器、彙總不看電腦的時鐘；嚴重度與降採樣用桌面的同一組向量
/// </summary>
public sealed partial class AccountViewRulesTests
{
    private const long Minute = 60_000;
    private const long Hour = 3_600_000;

    private static readonly string La = SyncIds.NewLogicalAccountId();

    // ───── 嚴重度與降採樣（桌面 UsageRulesTests、UsageHistoryStoreTests 的向量） ─────

    [Theory]
    [InlineData(0d, false, "ok")]
    [InlineData(49.9d, false, "ok")]
    [InlineData(50d, false, "warn")]
    [InlineData(79.9d, false, "warn")]
    [InlineData(80d, false, "high")]
    [InlineData(94.9d, false, "high")]
    [InlineData(95d, false, "max")]
    [InlineData(100d, false, "max")]
    [InlineData(10d, true, "max")]
    public void Severity與桌面同一組向量(double percent, bool locked, string expected) =>
        AccountViewRules.Severity(percent, locked).Should().Be(expected);

    [Fact]
    public void Downsample與桌面同一組向量_只取最近7天_每小時取最後一個值()
    {
        var n = 1_791_000_000_000 / Hour * Hour + 30 * Minute;
        var hourStart = n / Hour * Hour;
        var points = new List<UsagePoint>
        {
            new(n - 8 * 24 * Hour, 1),
            new(n - 6 * 24 * Hour, 2),
            new(hourStart - Hour + 5 * Minute, 3),
            new(hourStart + 1 * Minute, 4),
            new(hourStart + 2 * Minute, 5),
            new(hourStart + 3 * Minute, 6),
        };

        var view = AccountViewRules.Downsample(points, n);

        view.Select(p => p.Percent).Should().Equal(2, 3, 6);
        view[^1].At.Should().Be(hourStart + 3 * Minute, "同一小時取最後一個點（保留原本的時間）");
    }

    [Fact]
    public void Downsample與桌面同一組向量_最多168點_保留最新的()
    {
        var n = 1_791_000_000_000 / Hour * Hour + 30 * Minute;
        var points = Enumerable.Range(0, 10 * 24).Select(i => new UsagePoint(n - (10 * 24 - 1 - i) * Hour, i)).ToList();

        var view = AccountViewRules.Downsample(points, n);

        view.Should().HaveCount(AccountViewRules.MaxHistoryPoints);
        view[^1].Percent.Should().Be(10 * 24 - 1);
        view.Select(p => p.At).Should().BeInAscendingOrder();
        AccountViewRules.Downsample([], n).Should().BeEmpty();
    }

    [Fact]
    public void 增量合併與一次降採樣結果相同()
    {
        var random = new Random(42);
        var start = 1_791_000_000_000L;
        var span = 9 * 24 * Hour;
        var times = Enumerable.Range(0, 5000).Select(_ => start + (long)(random.NextDouble() * span)).Order().ToList();
        // 每個 at 都有：同一個 at 的兩點（後到的取代先到的）
        times.Insert(2500, times[2500]);
        var points = times.Select(t => new UsagePoint(t, Math.Round(random.NextDouble() * 100, 1))).ToList();
        var now = points[^1].At + 30 * Minute;

        var buckets = new SortedDictionary<long, UsagePoint>();
        var batchSize = (points.Count + 6) / 7;
        for (var i = 0; i < points.Count; i += batchSize)
        {
            var batch = points.Skip(i).Take(batchSize).ToList();
            AccountViewRules.MergeHourly(buckets, batch, batch[^1].At);
        }

        var incremental = AccountViewRules.FromBuckets(buckets, now);
        var once = AccountViewRules.Downsample(points, now);
        once.Should().NotBeEmpty();
        incremental.Should().Equal(once, "分批合併與一次降採樣要逐點相同");
        incremental.Count.Should().BeLessThanOrEqualTo(AccountViewRules.MaxHistoryPoints);
        buckets.Count.Should().BeLessThanOrEqualTo(AccountViewRules.MaxHistoryPoints + 2, "過期的桶要丟掉");
    }

    // ───── provider ─────

    [Fact]
    public void providers是四種且照AgentProviders_All的順序_Antigravity不能建隔離帳戶()
    {
        var providers = AccountViewRules.Providers();
        providers.Select(p => (string)p["key"]!).Should().Equal(AgentProviders.All.Where(AccountViewRules.IsSyncedProvider).Select(p => p.ToString()));
        providers.Select(p => (string)p["key"]!).Should().Equal("Claude", "Codex", "Antigravity", "Grok");
        providers.Select(p => (string)p["name"]!).Should().Equal("Claude Code", "Codex", "Antigravity", "Grok Build");
        providers.Should().OnlyContain(p => (bool)p["safeMode"]! == false);
        providers.Select(p => (bool)p["isolatedAccounts"]!).Should().Equal(true, true, false, true);
    }

    [Fact]
    public void 未知的AgentProvider值丟例外()
    {
        AccountViewRules.IsSyncedProvider(AgentProvider.Claude).Should().BeTrue();
        AccountViewRules.IsSyncedProvider(AgentProvider.Codex).Should().BeTrue();
        AccountViewRules.IsSyncedProvider(AgentProvider.Antigravity).Should().BeTrue("2026-10-22 起四種都進目錄");
        AccountViewRules.IsSyncedProvider(AgentProvider.Grok).Should().BeTrue("2026-10-22 起四種都進目錄");
        var unknown = () => AccountViewRules.IsSyncedProvider((AgentProvider)99);
        unknown.Should().Throw<ArgumentOutOfRangeException>();
        var unknownIsolated = () => AccountViewRules.SupportsIsolatedAccounts((AgentProvider)99);
        unknownIsolated.Should().Throw<ArgumentOutOfRangeException>();
        foreach (var provider in AgentProviders.All)
        {
            var decided = () => AccountViewRules.IsSyncedProvider(provider);
            decided.Should().NotThrow($"{provider} 要明確決定同不同步");
            var isolated = () => AccountViewRules.SupportsIsolatedAccounts(provider);
            isolated.Should().NotThrow($"{provider} 要明確決定能不能建隔離帳戶");
        }
    }

    // ───── status 的彙總 ─────

    [Fact]
    public void 任一台ready且身分相符就是ready不論在不在線()
    {
        var links = new[] { Link("d_1", "error"), Link("d_2", "ready"), Link("d_3", "checking") };
        AccountViewRules.Aggregate(links, _ => false).Should().Be(("ready", "1 台已登入（0 台在線）"), "離線的那台仍是登入的");
        AccountViewRules.Aggregate(links, id => id == "d_2").Should().Be(("ready", "1 台已登入（1 台在線）"));
    }

    [Fact]
    public void ready但身分不符算error()
    {
        AccountViewRules.Aggregate([Link("d_1", "ready", identityOk: false)], _ => true).Should().Be(("error", "1 台有問題"));
        AccountViewRules.Aggregate([Link("d_1", "ready", identityOk: false), Link("d_2", "notLoggedIn")], _ => true).Status.Should().Be("error");
    }

    [Fact]
    public void 沒有連結是notLoggedIn() =>
        AccountViewRules.Aggregate([], _ => true).Should().Be(("notLoggedIn", "沒有電腦使用這個帳戶"));

    [Fact]
    public void 狀態優先序()
    {
        var order = new[] { "checking", "error", "apiKey", "notLoggedIn", "notInstalled" };
        for (var i = 0; i < order.Length; i++)
        {
            AccountViewRules.Aggregate([Link("d_1", order[i])], _ => true).Status.Should().Be(order[i]);
            for (var j = i + 1; j < order.Length; j++)
            {
                AccountViewRules.Aggregate([Link("d_1", order[i]), Link("d_2", order[j])], _ => true).Status.Should().Be(order[i], $"{order[i]} 優先於 {order[j]}");
                AccountViewRules.Aggregate([Link("d_1", order[j]), Link("d_2", order[i])], _ => true).Status.Should().Be(order[i], $"{order[i]} 優先於 {order[j]}（順序對調）");
            }
        }
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("READY")]
    [InlineData("<script>")]
    [InlineData("")]
    public void 不認得的status當error(string status)
    {
        AccountViewRules.SafeStatus(status).Should().Be("error");
        AccountViewRules.Aggregate([Link("d_1", status), Link("d_2", "notInstalled")], _ => true).Should().Be(("error", "1 台有問題"));
    }

    [Fact]
    public void statusText的格式()
    {
        AccountViewRules.Aggregate([Link("d_1", "ready"), Link("d_2", "ready"), Link("d_3", "error")], id => id != "d_2")
            .StatusText.Should().Be("2 台已登入（1 台在線）");
        AccountViewRules.Aggregate([Link("d_1", "error"), Link("d_2", "x"), Link("d_3", "ready", identityOk: false)], _ => true)
            .StatusText.Should().Be("3 台有問題");
        AccountViewRules.Aggregate([Link("d_1", "checking"), Link("d_2", "error")], _ => true).StatusText.Should().Be("檢查中");
        AccountViewRules.Aggregate([Link("d_1", "apiKey")], _ => true).StatusText.Should().Be("API Key 登入");
        AccountViewRules.Aggregate([Link("d_1", "notLoggedIn"), Link("d_2", "notInstalled")], _ => true).StatusText.Should().Be("沒有電腦登入");
        AccountViewRules.Aggregate([Link("d_1", "notInstalled")], _ => true).StatusText.Should().Be("未安裝 CLI");
    }

    [Fact]
    public void checkedAt不影響彙總()
    {
        // 兩組只差 checkedAt（電腦的時鐘，只供顯示）：結果相同
        var a = new[] { Link("d_1", "error", checkedAt: 1), Link("d_2", "notLoggedIn", checkedAt: 9_999_999_999_999) };
        var b = new[] { Link("d_1", "error", checkedAt: 9_999_999_999_999), Link("d_2", "notLoggedIn", checkedAt: 1) };
        AccountViewRules.Aggregate(a, _ => true).Should().Be(AccountViewRules.Aggregate(b, _ => true));
        var devices = new[] { Dev("d_1", 1), Dev("d_2", 2) };
        var viewA = AccountViewRules.Account(Acc(), a, null, NoHistory, null, devices);
        var viewB = AccountViewRules.Account(Acc(), b, null, NoHistory, null, devices);
        ((string?)viewA["status"]).Should().Be((string?)viewB["status"]);
        ((string?)viewA["statusText"]).Should().Be((string?)viewB["statusText"]);
    }

    // ───── AccountView 的其他欄位 ─────

    [Fact]
    public void email與plan先取expected()
    {
        var devices = new[] { Dev("d_1", 1), Dev("d_2", 2) };
        var links = new[]
        {
            Link("d_2", "ready", email: "second@example.com", plan: "Pro"),
            Link("d_1", "ready", identityOk: false, email: "wrong@example.com", plan: "Free"),
        };
        var withExpected = AccountViewRules.Account(Acc(expectedEmail: "me@example.com", planHint: "Claude Max"), links, null, NoHistory, null, devices);
        ((string?)withExpected["email"]).Should().Be("me@example.com");
        ((string?)withExpected["plan"]).Should().Be("Claude Max");

        var fromLinks = AccountViewRules.Account(Acc(), links, null, NoHistory, null, devices);
        ((string?)fromLinks["email"]).Should().Be("second@example.com", "身分不符的連結不取");
        ((string?)fromLinks["plan"]).Should().Be("Pro");

        var none = AccountViewRules.Account(Acc(), [], null, NoHistory, null, devices);
        none["email"]!.Type.Should().Be(JTokenType.Null);
        none["plan"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public void label空白時依序用expectedEmail與產品名稱()
    {
        ((string?)AccountViewRules.Account(Acc(label: " ", expectedEmail: "me@example.com"), [], null, NoHistory, null, [])["label"]).Should().Be("me@example.com");
        ((string?)AccountViewRules.Account(Acc(label: "", provider: "Codex"), [], null, NoHistory, null, [])["label"]).Should().Be("Codex");
        ((string?)AccountViewRules.Account(Acc(label: ""), [], null, NoHistory, null, [])["label"]).Should().Be("Claude Code");
    }

    [Fact]
    public void sessions加總_只算在線的電腦()
    {
        var devices = new[] { Dev("d_1", 1, online: true), Dev("d_2", 2, online: true), Dev("d_3", 3, online: true), Dev("d_4", 4) };
        var links = new[]
        {
            Link("d_1", "ready", sessions: new LinkSessions(2, 1, 0, 1, 2, 3)),
            Link("d_2", "ready", sessions: new LinkSessions(3, 2, 1, 2)),
            Link("d_3", "ready", sessions: null),
            Link("d_4", "ready", sessions: new LinkSessions(7, 7, 7, 7, 7)),
        };
        var view = AccountViewRules.Account(Acc(), links, null, NoHistory, null, devices);
        JToken.DeepEquals(view["sessions"], JObject.Parse("""{"open":5,"running":3,"waiting":1,"background":3,"programs":2,"subagents":3}""")).Should().BeTrue(view["sessions"]!.ToString(),
            "離線的 d_4 最後回報的數字停在當時，不算");
        ((bool)view["loginBusy"]!).Should().BeFalse();
        ((bool)AccountViewRules.Account(Acc(), [links[0], links[1] with { LoginBusy = true }], null, NoHistory, null, devices)["loginBusy"]!).Should().BeTrue();
    }

    [Fact]
    public void devices每台未撤銷的電腦一格沒有連結是absent()
    {
        var devices = new[] { Dev("d_B", 2, online: true), Dev("d_A", 1), Dev("d_R", 0, revokedAt: 5) };
        var links = new[] { Link("d_B", "ready"), Link("d_R", "ready"), Link("d_X", "ready") };
        var view = AccountViewRules.Account(Acc(), links, null, NoHistory, null, devices);
        var cells = (JArray)view["devices"]!;
        cells.Select(c => (string)c["deviceId"]!).Should().Equal("d_A", "d_B");
        var absent = cells[0];
        ((string?)absent["status"]).Should().Be("absent");
        ((string?)absent["statusText"]).Should().Be("尚未加入");
        ((bool)absent["identityOk"]!).Should().BeTrue();
        foreach (var field in new[] { "email", "plan", "accessExpiresAt", "loginExpiresAt", "checkedAt" })
            absent[field]!.Type.Should().Be(JTokenType.Null, field);
        ((bool)cells[1]["online"]!).Should().BeTrue();
        ((bool)cells[0]["online"]!).Should().BeFalse();
        cells.Should().OnlyContain(c => (bool)c["self"]! == false);
        ((string?)view["statusText"]).Should().Be("1 台已登入（1 台在線）", "已撤銷與不認得的裝置的連結不算");
    }

    [Fact]
    public void cell的status經SafeStatus()
    {
        var view = AccountViewRules.Account(Acc(), [Link("d_1", "<b>evil</b>")], null, NoHistory, null, [Dev("d_1", 1)]);
        ((string?)view["devices"]![0]!["status"]).Should().Be("error");
    }

    [Fact]
    public void usagePoller只標負責那台且空字串不算()
    {
        var devices = new[] { Dev("d_1", 1), Dev("d_2", 2) };
        var links = new[] { Link("d_1", "ready"), Link("d_2", "ready") };
        var cells = (JArray)AccountViewRules.Account(Acc(), links, null, NoHistory, "d_2", devices)["devices"]!;
        cells.Select(c => (bool)c["usagePoller"]!).Should().Equal(false, true);
        foreach (var none in new[] { "", null })
            ((JArray)AccountViewRules.Account(Acc(), links, null, NoHistory, none, devices)["devices"]!).Should().OnlyContain(c => (bool)c["usagePoller"]! == false);
    }

    [Theory]
    [InlineData("api", "api")]
    [InlineData("session", "session")]
    [InlineData("cache", null)]
    [InlineData("none", null)]
    [InlineData("<img>", null)]
    public void usage的source只放行api與session(string source, string? expected)
    {
        var usage = AccountViewRules.Usage(Usage(source: source), NoHistory)!;
        ((string?)usage["source"]).Should().Be(expected);
        AccountViewRules.Usage(null, NoHistory).Should().BeNull("沒有 usage_latest");
    }

    [Fact]
    public void usage視窗帶severity與最多168點history()
    {
        var points = Enumerable.Range(0, 200).Select(i => new UsagePoint(1_000_000 + i * Hour, i)).ToList();
        var history = new Dictionary<string, IReadOnlyList<UsagePoint>> { ["session"] = points };
        var usage = AccountViewRules.Usage(Usage(windows: [new("session", "5 小時", 81, 7000, false), new("weekly", "每週", 10, null, true)]), history)!;
        var windows = (JArray)usage["windows"]!;
        windows.Should().HaveCount(2);
        ((string?)windows[0]["severity"]).Should().Be("high");
        ((string?)windows[1]["severity"]).Should().Be("max", "已鎖定");
        var series = (JArray)windows[0]["history"]!;
        series.Should().HaveCount(AccountViewRules.MaxHistoryPoints);
        var last = (JArray)series[series.Count - 1];
        ((long)last[0]).Should().Be(points[^1].At);
        ((double)last[1]).Should().Be(199);
        ((JArray)windows[1]["history"]!).Should().BeEmpty("沒有歷史點是空陣列");
        windows[1]["resetsAt"]!.Type.Should().Be(JTokenType.Null);
        ((long)usage["fetchedAt"]!).Should().Be(6000);
    }

    [Fact]
    public void usage的key送還原後的鍵而走勢照伺服器存的鍵()
    {
        // 裝置上傳的是跳脫後的鍵（UsageWindowKeyRules.Encode），伺服器以它存與累積走勢；瀏覽器看到的和桌面一樣是原本的鍵（WP3.7 步驟 2）
        var points = new List<UsagePoint> { new(1_000_000, 40), new(1_000_000 + Hour, 41) };
        var history = new Dictionary<string, IReadOnlyList<UsagePoint>> { ["weekly_cOpus"] = points, ["codex_c300"] = [new(1_000_000, 5)] };
        var usage = AccountViewRules.Usage(Usage(windows:
        [
            new("weekly_cOpus", "每週・Opus", 41, null, false),
            new("codex_c300", "5 小時", 5, null, false),
            new("weekly_cclaude-opus-4_d5", "每週・Opus 4.5", 3, null, false),
            new("session", "5 小時", 12, null, false),
        ]), history)!;
        var windows = (JArray)usage["windows"]!;
        windows.Select(w => (string?)w["key"]).Should().Equal(["weekly:Opus", "codex:300", "weekly:claude-opus-4.5", "session"]);
        ((JArray)windows[0]["history"]!).Should().HaveCount(2, "走勢以跳脫後的鍵查");
        ((JArray)windows[1]["history"]!).Should().ContainSingle();
        ((JArray)windows[2]["history"]!).Should().BeEmpty();
    }

    [Fact]
    public void usage的key還原後撞名時送原本的鍵且沒有key的窗略過()
    {
        // 不是這種跳脫格式的鍵照原樣（a_x），合格式的 a__x 也還原成 a_x：網頁拿 key 當識別，後面那個改送原本的鍵
        var usage = AccountViewRules.Usage(Usage(windows:
        [
            new("a_x", "甲", 1, null, false),
            new(null!, "沒有鍵", 2, null, false),
            new("a__x", "乙", 3, null, false),
            new("end_", "丙", 4, null, false),
        ]), NoHistory)!;
        var windows = (JArray)usage["windows"]!;
        windows.Select(w => (string?)w["key"]).Should().Equal(["a_x", "a__x", "end_"]);
        windows.Select(w => (string?)w["label"]).Should().Equal(["甲", "乙", "丙"]);
    }

    [Theory]
    [InlineData(new[] { "a__x", "a_x" }, new[] { "a__x", "a_x" })]
    [InlineData(new[] { "five__hour", "five_hour" }, new[] { "five__hour", "five_hour" })]
    [InlineData(new[] { "a_x", "a__x", "a____x" }, new[] { "a_x", "a__x", "a____x" })]
    public void usage的key不論順序都不重複且走勢對到線上鍵(string[] wireKeys, string[] expected)
    {
        // 合併前審查 WK-1／F2：合格式的鍵排在前面時，逐一還原、撞名才退回原本的鍵會送出兩個相同的 key（網頁的圓環欄只留後一個）
        var history = new Dictionary<string, IReadOnlyList<UsagePoint>>(StringComparer.Ordinal);
        var shared = new List<SharedUsageWindow>();
        for (var i = 0; i < wireKeys.Length; i++)
        {
            history[wireKeys[i]] = [new UsagePoint(1_000_000, i)];
            shared.Add(new SharedUsageWindow(wireKeys[i], "窗" + i, i, null, false));
        }
        var usage = AccountViewRules.Usage(Usage(windows: shared), history)!;
        var windows = (JArray)usage["windows"]!;
        windows.Select(w => (string?)w["key"]).Should().Equal(expected).And.OnlyHaveUniqueItems();
        for (var i = 0; i < wireKeys.Length; i++)
            ((double)windows[i]["history"]![0]![1]!).Should().Be(i, $"第 {i} 個窗的走勢照它的線上鍵 {wireKeys[i]} 查");
    }

    [Fact]
    public void usage的百分比不是有限值時給0()
    {
        var usage = AccountViewRules.Usage(Usage(windows: [new("session", "5 小時", double.NaN, null, false)]), NoHistory)!;
        ((double)usage["windows"]![0]!["percent"]!).Should().Be(0);
        JsonConvert.SerializeObject(usage).Should().NotContain("NaN");
    }

    // ───── DeviceView ─────

    [Fact]
    public void DeviceView不送windowsUser且verified為null()
    {
        var view = AccountViewRules.Device(Dev("d_1", 1, windowsUser: "victim-user"));
        view.ContainsKey("windowsUser").Should().BeFalse();
        view.ToString().Should().NotContain("victim-user");
        view["verified"]!.Type.Should().Be(JTokenType.Null);
        ((bool)view["self"]!).Should().BeFalse();
    }

    [Fact]
    public void 已撤銷的裝置排最後並帶revoked()
    {
        var devices = AccountViewRules.Devices([Dev("d_R", 1, revokedAt: 5000, revokedReason: "compromised"), Dev("d_B", 3), Dev("d_A", 2), Dev("d_Q", 0, revokedAt: 6000, revokedReason: "retired")]);
        devices.Select(d => (string)d["id"]!).Should().Equal("d_A", "d_B", "d_Q", "d_R");
        devices[0]["revoked"]!.Type.Should().Be(JTokenType.Null);
        JToken.DeepEquals(devices[3]["revoked"], JObject.Parse("""{"at":5000,"reason":"compromised"}""")).Should().BeTrue();
        JToken.DeepEquals(devices[2]["revoked"], JObject.Parse("""{"at":6000,"reason":"retired"}""")).Should().BeTrue();
        ((string?)AccountViewRules.Device(Dev("d_X", 1, revokedAt: 1, revokedReason: "evil"))["revoked"]!["reason"]).Should().BeNull("撤銷原因走白名單");
        ((bool)AccountViewRules.Device(Dev("d_X", 1, online: true, revokedAt: 1, revokedReason: "retired"))["online"]!).Should().BeFalse("已撤銷的一律不在線");
    }

    [Theory]
    [InlineData("Off", "Off")]
    [InlineData("View", "View")]
    [InlineData("Control", "Control")]
    [InlineData("control", "Off")]
    [InlineData("FullAccess", "Off")]
    [InlineData(null, "Off")]
    public void remoteControlLevel走白名單(string? level, string expected)
    {
        var policy = level is null ? null : new DevicePolicy(level, true);
        var view = AccountViewRules.Device(Dev("d_1", 1) with { Policy = policy });
        ((string?)view["remoteControlLevel"]).Should().Be(expected);
        ((bool)view["allowRemoteFullAccess"]!).Should().Be(policy is not null);
    }

    [Theory]
    [InlineData("tpm", "tpm")]
    [InlineData("software", "software")]
    [InlineData("hsm", "software")]
    [InlineData("TPM", "software")]
    public void keyStorage走白名單(string storage, string expected) =>
        ((string?)AccountViewRules.Device(Dev("d_1", 1, keyStorage: storage))["keyStorage"]).Should().Be(expected);

    [Fact]
    public void name取顯示名稱signedName取配對名稱()
    {
        var renamed = AccountViewRules.Device(Dev("d_1", 1, name: "OFFICE-PC") with { StreamName = "辦公室", StreamAppVersion = "0.3.1" });
        ((string?)renamed["name"]).Should().Be("辦公室");
        ((string?)renamed["signedName"]).Should().Be("OFFICE-PC");
        ((string?)renamed["appVersion"]).Should().Be("0.3.1");
        var plain = AccountViewRules.Device(Dev("d_1", 1, name: "OFFICE-PC"));
        ((string?)plain["name"]).Should().Be("OFFICE-PC");
        ((string?)plain["appVersion"]).Should().Be("0.3.0", "device_streams 沒有版本時用配對時的");
    }

    [Fact]
    public void lastSeenAt取hello與心跳較晚者()
    {
        ((long?)AccountViewRules.Device(Dev("d_1", 1) with { LastHelloAt = 10, LastHeartbeatAt = 20 })["lastSeenAt"]).Should().Be(20);
        ((long?)AccountViewRules.Device(Dev("d_1", 1) with { LastHelloAt = 30, LastHeartbeatAt = 20 })["lastSeenAt"]).Should().Be(30);
        ((long?)AccountViewRules.Device(Dev("d_1", 1) with { LastHelloAt = 30, LastHeartbeatAt = null })["lastSeenAt"]).Should().Be(30);
        AccountViewRules.Device(Dev("d_1", 1) with { LastHelloAt = null, LastHeartbeatAt = null })["lastSeenAt"]!.Type.Should().Be(JTokenType.Null);
    }

    // ───── DeviceCliView、devicesCli、settings ─────

    [Fact]
    public void DeviceCliView逐欄白名單()
    {
        var view = AccountViewRules.DeviceCli(new DeviceCliView(true, "2.1.297", "evil", new string('9', 65), true, 200_000, -1, new string('x', 600), 1234));
        ((bool)view["installed"]!).Should().BeTrue();
        ((string?)view["version"]).Should().Be("2.1.297");
        view["installKind"]!.Type.Should().Be(JTokenType.Null, "installKind 只放行 npm、native、winget、managed、other");
        view["latest"]!.Type.Should().Be(JTokenType.Null, "超過 64 字");
        ((int)view["runningOnOldVersion"]!).Should().Be(100_000);
        ((int)view["restartPending"]!).Should().Be(0);
        view["installBroken"]!.Type.Should().Be(JTokenType.Null, "超過 500 字");
        ((long?)view["checkedAt"]).Should().Be(1234);
        view.Properties().Select(p => p.Name).Should().Equal(
            "installed", "version", "installKind", "latest", "updateAvailable", "runningOnOldVersion", "restartPending", "installBroken", "checkedAt");

        foreach (var kind in new[] { "npm", "native", "winget", "managed", "other" })
            ((string?)AccountViewRules.DeviceCli(new DeviceCliView(true, null, kind, null, false, 0, 0, null, null))["installKind"]).Should().Be(kind);
        AccountViewRules.DeviceCli(new DeviceCliView(true, "1.0" + (char)0x07, null, null, false, 0, 0, null, null))["version"]!.Type
            .Should().Be(JTokenType.Null, "含控制字元");
    }

    [Fact]
    public void devicesCli照AgentProviders_All的順序_不含已撤銷與不認得的裝置()
    {
        var cli = new DeviceCliView(true, "1.0.0", "npm", "1.0.0", false, 0, 0, null, 1);
        var devices = new[] { Dev("d_B", 2), Dev("d_A", 1), Dev("d_R", 0, revokedAt: 1) };
        var result = AccountViewRules.DevicesCli(devices,
        [
            ("d_B", AgentProvider.Codex, cli),
            ("d_B", AgentProvider.Claude, cli),
            ("d_B", AgentProvider.Antigravity, cli),
            ("d_A", AgentProvider.Antigravity, cli),
            ("d_R", AgentProvider.Claude, cli),
            ("d_X", AgentProvider.Claude, cli),
        ]);
        result.Properties().Select(p => p.Name).Should().Equal("d_A", "d_B");
        ((JObject)result["d_B"]!).Properties().Select(p => p.Name).Should().Equal("Claude", "Codex", "Antigravity");
        ((JObject)result["d_A"]!).Properties().Select(p => p.Name).Should().Equal("Antigravity");
        AccountViewRules.DevicesCli(devices, []).Should().BeEmpty();
    }

    [Fact]
    public void settings只有四個鍵與預設值()
    {
        var defaults = AccountViewRules.Settings(new Dictionary<string, JToken>());
        JToken.DeepEquals(defaults, JObject.Parse("""{"theme":"dark","fontSize":14,"enterToSend":true,"notifications":true}""")).Should().BeTrue(defaults.ToString());

        var prefs = new Dictionary<string, JToken>
        {
            ["theme"] = "light",
            ["fontSize"] = 16,
            ["enterToSend"] = "yes",
            ["notifications"] = false,
            ["instructions"] = "rm -rf",
            ["closeToTray"] = false,
        };
        var settings = AccountViewRules.Settings(prefs);
        JToken.DeepEquals(settings, JObject.Parse("""{"theme":"light","fontSize":16,"enterToSend":true,"notifications":false}""")).Should().BeTrue(settings.ToString());
        AccountViewRules.Settings(new Dictionary<string, JToken> { ["fontSize"] = 30 })["fontSize"]!.Value<int>().Should().Be(14, "不合格的給預設");
    }

    // ───── 安全與整體形狀 ─────

    [Fact]
    public void 輸出不含權杖與本機路徑()
    {
        var devices = new[] { Dev("d_1", 1, windowsUser: "victim-user"), Dev("d_2", 2, revokedAt: 9, revokedReason: "retired") };
        var account = AccountViewRules.Account(Acc(expectedEmail: "me@example.com"), [Link("d_1", "ready")], Usage(), NoHistory, "d_1", devices);
        var all = new JArray(account, AccountViewRules.Devices(devices),
            AccountViewRules.DevicesCli(devices, [("d_1", AgentProvider.Claude, new DeviceCliView(true, "1", "npm", "1", false, 0, 0, null, 1))]),
            AccountViewRules.Settings(new Dictionary<string, JToken>()), AccountViewRules.Providers());

        var names = all.Descendants().OfType<JProperty>().Select(p => p.Name).ToList();
        names.Should().NotBeEmpty();
        names.Should().NotContain(n => Forbidden().IsMatch(n));
        account["configDirectory"]!.Type.Should().Be(JTokenType.Null);
        var text = all.ToString(Formatting.None);
        text.Should().NotContain("victim-user").And.NotContain(devices[0].Info.DevicePubKey);
    }

    [Fact]
    public void AccountView完整形狀()
    {
        var devices = new[]
        {
            Dev("d_2", 200, name: "HOME-PC", online: false),
            Dev("d_1", 100, name: "OFFICE-PC", online: true) with
            {
                StreamName = "辦公室",
                StreamAppVersion = "0.3.1",
                LastHelloAt = 1000,
                LastHeartbeatAt = 2000,
                Policy = new DevicePolicy("View", true),
            },
            Dev("d_3", 50, name: "OLD-PC", revokedAt: 3000, revokedReason: "retired"),
        };
        var links = new[]
        {
            Link("d_1", "ready", email: "me@example.com", plan: "Claude Max", sessions: new LinkSessions(2, 1, 0), checkedAt: 5000) with
            {
                StatusText = "已登入",
                AccessExpiresAt = 9000,
                LoginExpiresAt = 9999,
            },
            Link("d_3", "ready"),
        };
        var usage = new SharedUsage(6000, "api", [new SharedUsageWindow("session", "5 小時", 42, 7000, false)], null, null, null, false, 1, 8000);
        var history = new Dictionary<string, IReadOnlyList<UsagePoint>> { ["session"] = [new(4000, 10), new(5000, 20)] };

        var view = AccountViewRules.Account(Acc(label: "工作", expectedEmail: "me@example.com"), links, usage, history, "d_1", devices);

        var expected = JObject.Parse("""
            {
              "id": "LA",
              "provider": "Claude",
              "label": "工作",
              "color": "#D97757",
              "isSystemDefault": false,
              "configDirectory": null,
              "email": "me@example.com",
              "plan": "Claude Max",
              "status": "ready",
              "statusText": "1 台已登入（1 台在線）",
              "safeMode": false,
              "loginBusy": false,
              "usage": {
                "fetchedAt": 6000,
                "stale": false,
                "source": "api",
                "error": null,
                "windows": [
                  { "key": "session", "label": "5 小時", "percent": 42.0, "resetsAt": 7000, "severity": "ok", "history": [[4000, 10.0], [5000, 20.0]] }
                ],
                "credits": null,
                "resetCredits": 1,
                "resetCreditExpiresAt": 8000,
                "note": null
              },
              "sessions": { "open": 2, "running": 1, "waiting": 0, "background": 0, "programs": 0, "subagents": 0 },
              "logicalId": "LA",
              "remoteOnly": false,
              "devices": [
                {
                  "deviceId": "d_1", "deviceName": "辦公室", "self": false, "status": "ready", "statusText": "已登入",
                  "email": "me@example.com", "plan": "Claude Max", "accessExpiresAt": 9000, "loginExpiresAt": 9999,
                  "identityOk": true, "usagePoller": true, "online": true, "checkedAt": 5000
                },
                {
                  "deviceId": "d_2", "deviceName": "HOME-PC", "self": false, "status": "absent", "statusText": "尚未加入",
                  "email": null, "plan": null, "accessExpiresAt": null, "loginExpiresAt": null,
                  "identityOk": true, "usagePoller": false, "online": false, "checkedAt": null
                }
              ]
            }
            """.Replace("LA", La, StringComparison.Ordinal));
        JToken.DeepEquals(view, expected).Should().BeTrue(view.ToString());

        var device = AccountViewRules.Device(devices[1]);
        var expectedDevice = JObject.Parse("""
            {
              "id": "d_1", "name": "辦公室", "signedName": "OFFICE-PC", "self": false, "machineName": "OFFICE-PC-M", "appVersion": "0.3.1",
              "fingerprint": "FP-d_1", "keyStorage": "tpm", "online": true, "lastSeenAt": 2000, "remoteControlLevel": "View",
              "allowRemoteFullAccess": true, "verified": null, "revoked": null
            }
            """);
        JToken.DeepEquals(device, expectedDevice).Should().BeTrue(device.ToString());
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<UsagePoint>> NoHistory = new Dictionary<string, IReadOnlyList<UsagePoint>>();

    private static AccountViewRow Acc(string label = "帳戶", string provider = "Claude", string? expectedEmail = null, string? planHint = null) =>
        new(La, provider, label, "#D97757", 0, expectedEmail, planHint, 1);

    private static LinkViewRow Link(string deviceId, string status, bool identityOk = true, string? email = null, string? plan = null,
        LinkSessions? sessions = null, long? checkedAt = 1) =>
        new(La, deviceId, status, null, email, plan, null, null, false, identityOk, sessions, checkedAt);

    private static DeviceViewRow Dev(string id, long createdAt, string? name = null, bool online = false, long? revokedAt = null,
        string? revokedReason = null, string? windowsUser = "user", string keyStorage = "tpm") =>
        new(new DeviceInfo(id, name ?? id.ToUpperInvariant() + "-PC", (name ?? id) + "-M", windowsUser, "0.3.0", keyStorage,
                "SPKI-" + id + "-pubkey", "FP-" + id, createdAt, revokedAt, revokedReason),
            null, null, null, null, null, online);

    private static SharedUsage Usage(string source = "api", IReadOnlyList<SharedUsageWindow>? windows = null) =>
        new(6000, source, windows ?? [new SharedUsageWindow("session", "5 小時", 12, 7000, false)], null, null, null, false);

    [GeneratedRegex("token|credential|secret|windowsUser|localAccountId|identityKey|orgId|configKind", RegexOptions.IgnoreCase)]
    private static partial Regex Forbidden();
}
