using System.Globalization;
using Myriagent.Agents;
using Myriagent.Sync;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 瀏覽器的對話清單、標頭與輸入區的組成（WP4.2 計畫步 8；D10、D11、D20、D21、D22、P5、P11、答 8）：時區分組的正負號與跨日、
/// 鍵集合寫死（web-protocol 第三節的欄位加 P5 的裝置欄位）、離線／recovering／重建中的投影、分頁的筆數與位元組上限
/// </summary>
public sealed class ConversationViewRulesTests
{
    private const string Conv = "0123456789abcdef0123456789abcdef";

    private const string Owner = "d_owner";

    private static readonly long UpdatedAt = At("2026-10-11T08:00:00Z");

    private static readonly ConversationDeviceInfo Online = new(Owner, "工作電腦", true);

    private static readonly ConversationDeviceInfo Offline = new(Owner, "工作電腦", false);

    // ───── 時區與分組（D22） ─────

    [Fact]
    public void ToLocalMs_臺灣負480是加8小時()
    {
        ConversationViewRules.ToLocalMs(0, -480).Should().Be(28_800_000);
        ConversationViewRules.ToLocalMs(At("2026-10-11T16:00:00Z"), -480).Should().Be(At("2026-10-12T00:00:00Z"), "UTC 16:00 是臺灣的午夜");
    }

    [Fact]
    public void ToLocalMs_正300是減5小時()
    {
        ConversationViewRules.ToLocalMs(0, 300).Should().Be(-18_000_000);
        ConversationViewRules.ToLocalMs(At("2026-10-11T05:00:00Z"), 300).Should().Be(At("2026-10-11T00:00:00Z"));
        ConversationViewRules.ToLocalMs(1234, 0).Should().Be(1234);
    }

    [Fact]
    public void 臺灣時區當地午夜前後_今天與昨天()
    {
        var midnight = At("2026-10-11T16:00:00Z");   // 臺灣 10-12 00:00
        ConversationViewRules.GroupLabel(midnight - 1, midnight, -480).Should().Be("昨天", "當地 10-11 23:59:59.999");
        ConversationViewRules.GroupLabel(midnight, midnight, -480).Should().Be("今天");
        ConversationViewRules.GroupLabel(midnight - 1, midnight - 1, -480).Should().Be("今天");
        ConversationViewRules.GroupLabel(At("2026-10-11T15:59:00Z"), At("2026-10-12T15:00:00Z"), -480).Should().Be("昨天");
    }

    [Fact]
    public void UTC與負480的分組不同()
    {
        var updated = At("2026-10-11T20:00:00Z");
        var now = At("2026-10-12T01:00:00Z");
        ConversationViewRules.GroupLabel(updated, now, 0).Should().Be("昨天", "UTC：10-11 與 10-12");
        ConversationViewRules.GroupLabel(updated, now, -480).Should().Be("今天", "臺灣：都是 10-12");
        ConversationViewRules.GroupLabel(At("2026-10-12T03:00:00Z"), At("2026-10-12T06:00:00Z"), 300).Should().Be("昨天", "UTC-5：10-11 22:00 與 10-12 01:00");
    }

    [Fact]
    public void 分組照桌面的規則_月份與年份()
    {
        var now = At("2026-10-12T04:00:00Z");
        ConversationViewRules.GroupLabel(At("2026-10-06T04:00:00Z"), now, -480).Should().Be("過去 7 天");
        ConversationViewRules.GroupLabel(At("2026-09-20T04:00:00Z"), now, -480).Should().Be("過去 30 天");
        ConversationViewRules.GroupLabel(At("2026-03-01T04:00:00Z"), now, -480).Should().Be("3 月");
        ConversationViewRules.GroupLabel(At("2025-03-01T04:00:00Z"), now, -480).Should().Be("2025 年 3 月");
        ConversationViewRules.GroupLabel(long.MinValue, now, 840).Should().NotBeNullOrEmpty("超出範圍的時間夾在邊界，不丟例外");
        ConversationViewRules.GroupLabel(long.MaxValue, now, -840).Should().Be("今天", "未來的時間與今天同組（同桌面）");
    }

    [Fact]
    public void NextDayBoundary正好是當地午夜()
    {
        var now = At("2026-10-11T10:00:00Z");   // 臺灣 18:00
        var boundary = ConversationViewRules.NextDayBoundaryMs(now, -480);
        boundary.Should().Be(At("2026-10-11T16:00:00Z"), "臺灣 10-12 00:00");
        ConversationViewRules.GroupLabel(now, boundary - 1, -480).Should().Be("今天");
        ConversationViewRules.GroupLabel(now, boundary, -480).Should().Be("昨天");
        ConversationViewRules.NextDayBoundaryMs(boundary - 1, -480).Should().Be(boundary);
        ConversationViewRules.NextDayBoundaryMs(boundary, -480).Should().Be(boundary + 86_400_000, "正好在午夜時，下一個是明天的午夜");

        ConversationViewRules.NextDayBoundaryMs(At("2026-10-11T03:00:00Z"), 300).Should().Be(At("2026-10-11T05:00:00Z"), "UTC-5 的 10-11 00:00");
        ConversationViewRules.NextDayBoundaryMs(0, 300).Should().Be(18_000_000, "當地時間是負的（1969-12-31 19:00）也取到下一個午夜");
        ConversationViewRules.NextDayBoundaryMs(0, 0).Should().Be(86_400_000);
    }

    [Fact]
    public void FallbackOffset_用TimeProvider的時區()
    {
        var taipei = TimeZoneInfo.CreateCustomTimeZone("test+8", TimeSpan.FromHours(8), "test+8", "test+8");
        var eastern = TimeZoneInfo.CreateCustomTimeZone("test-5", TimeSpan.FromHours(-5), "test-5", "test-5");
        ConversationViewRules.FallbackOffset(taipei, At("2026-10-11T00:00:00Z")).Should().Be(-480, "同 getTimezoneOffset() 的正負號");
        ConversationViewRules.FallbackOffset(eastern, At("2026-10-11T00:00:00Z")).Should().Be(300);
        ConversationViewRules.FallbackOffset(TimeZoneInfo.Utc, long.MaxValue).Should().Be(0, "超出範圍的時間不丟例外");
    }

    // ───── 清單（ConversationSummary） ─────

    [Fact]
    public void 鍵集合等於WPR的欄位加P5的裝置欄位()
    {
        var summary = ConversationViewRules.Summary(ListRow(), UpdatedAt, -480, Online, unread: false);
        summary.Properties().Select(p => p.Name).Should().Equal(
            "id", "accountId", "provider", "title", "group", "updatedAt", "workingDirectory", "customFolder", "running", "waiting", "unread",
            "deviceId", "deviceName", "deviceOnline");
        summary["id"]!.Value<string>().Should().Be(Conv);
        summary["accountId"]!.Value<string>().Should().Be("la_1");
        summary["provider"]!.Value<string>().Should().Be("Codex");
        summary["title"]!.Value<string>().Should().Be("標題");
        summary["group"]!.Value<string>().Should().Be("今天");
        summary["updatedAt"]!.Value<long>().Should().Be(UpdatedAt);
        summary["workingDirectory"]!.Value<string>().Should().Be("C:/repo/proj");
        summary["customFolder"]!.Value<bool>().Should().BeTrue();
        summary["running"]!.Value<bool>().Should().BeTrue();
        summary["waiting"]!.Value<bool>().Should().BeTrue();
        summary["deviceId"]!.Value<string>().Should().Be(Owner);
        summary["deviceName"]!.Value<string>().Should().Be("工作電腦");
        summary["deviceOnline"]!.Value<bool>().Should().BeTrue();

        var bare = ConversationViewRules.Summary(ListRow(account: null, workingDirectory: null), UpdatedAt, -480, ConversationDeviceInfo.None, unread: false);
        bare["accountId"]!.Type.Should().Be(JTokenType.Null);
        bare["workingDirectory"]!.Type.Should().Be(JTokenType.Null);
        bare["deviceId"]!.Type.Should().Be(JTokenType.Null);
        bare["deviceName"]!.Type.Should().Be(JTokenType.Null);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void unread照未讀集合傳入的值(bool unread)
    {
        ConversationViewRules.Summary(ListRow(), UpdatedAt, 0, Online, unread)["unread"]!.Value<bool>().Should().Be(unread);
    }

    [Fact]
    public void 清單的分組依連線的時區()
    {
        var row = ListRow(updatedAt: At("2026-10-11T20:00:00Z"));
        var now = At("2026-10-12T01:00:00Z");
        ConversationViewRules.Summary(row, now, 0, Online, false)["group"]!.Value<string>().Should().Be("昨天");
        ConversationViewRules.Summary(row, now, -480, Online, false)["group"]!.Value<string>().Should().Be("今天");
    }

    [Fact]
    public void 擁有者離線_清單的running是false_waiting照實()
    {
        var summary = ConversationViewRules.Summary(ListRow(), UpdatedAt, 0, Offline, false);
        summary["running"]!.Value<bool>().Should().BeFalse("離線的電腦不在執行：同標頭的離線投影");
        summary["waiting"]!.Value<bool>().Should().BeTrue();
        summary["deviceOnline"]!.Value<bool>().Should().BeFalse();
    }

    // ───── 標頭（Header） ─────

    [Fact]
    public void 標頭的鍵集合等於WPR的欄位加P5的裝置欄位()
    {
        var header = ConversationViewRules.Header(HeaderRow(), Online, rebuilding: false);
        header.Properties().Select(p => p.Name).Should().Equal(
            "id", "accountId", "provider", "title", "model", "effort", "permission", "permissionModes", "models", "defaultEffort", "commands", "workingDirectory",
            "customFolder", "recentFolders", "isDraft", "running", "steer", "status", "startedAt", "lastTurn", "accountMissing", "restart",
            "deviceId", "deviceName", "deviceOnline");
        header["steer"]!.Value<bool>().Should().BeTrue("Claude 執行中送出直接插進這一輪");
        header["commands"]!.Should().BeOfType<JArray>().Which.Should().BeEmpty();
        header["recentFolders"]!.Should().BeOfType<JArray>().Which.Should().BeEmpty();
        header["isDraft"]!.Value<bool>().Should().BeFalse();
        header["restart"]!.Type.Should().Be(JTokenType.Null);
        header["running"]!.Value<bool>().Should().BeTrue();
        header["startedAt"]!.Value<long>().Should().Be(1_000);
        header["lastTurn"]!.Value<string>().Should().Be("輸入 12,345・輸出 2,100 tokens");
        header["status"]!.Type.Should().Be(JTokenType.Null, "第 4 階段沒有執行中的狀態文字");
        header["accountMissing"]!.Value<bool>().Should().BeFalse();
        header["deviceOnline"]!.Value<bool>().Should().BeTrue();
        header["workingDirectory"]!.Value<string>().Should().Be("C:/repo/proj");
    }

    [Fact]
    public void 標頭的steer照代理種類()
    {
        foreach (var provider in AgentProviders.All)
            ConversationViewRules.Header(HeaderRow(provider: provider), Online, rebuilding: false)["steer"]!.Value<bool>()
                .Should().Be(Myriagent.Hub.AgentHubRules.SupportsSteering(provider), provider.ToString());
    }

    [Fact]
    public void model與effort省略時是default()
    {
        var header = ConversationViewRules.Header(HeaderRow(model: null, effort: null), Online, false);
        header["model"]!.Value<string>().Should().Be("default");
        header["effort"]!.Value<string>().Should().Be("default");
        var models = header["models"]!.Should().BeOfType<JArray>().Subject;
        models.Should().ContainSingle();
        models[0]["value"]!.Value<string>().Should().Be("default");
        models[0]["label"]!.Value<string>().Should().Be("預設");
        models[0]["efforts"]!.Should().BeOfType<JArray>().Which.Should().BeEmpty();

        var explicitDefault = ConversationViewRules.Header(HeaderRow(model: "default", effort: "default"), Online, false);
        explicitDefault["models"]!.Should().BeOfType<JArray>().Which.Should().ContainSingle("「default」就是預設，不另列一個");
        explicitDefault["effort"]!.Value<string>().Should().Be("default");
    }

    [Fact]
    public void models含預設與目前的模型()
    {
        var models = ConversationViewRules.Header(HeaderRow(model: "opus", effort: "high"), Online, false)["models"]!.Should().BeOfType<JArray>().Subject;
        models.Select(m => m["value"]!.Value<string>()).Should().Equal("default", "opus");
        models[1]["label"]!.Value<string>().Should().Be("opus");
        models[1]["description"]!.Value<string>().Should().Be("目前使用中");
        foreach (var model in models)
            model["efforts"]!.Values<string>().Should().Equal(["high"], "目前的強度一定在清單裡，網頁不會標成不支援");

        var onlyEffort = ConversationViewRules.Models(null, "max");
        onlyEffort.Should().ContainSingle();
        onlyEffort[0]["efforts"]!.Values<string>().Should().Equal("max");
        ConversationViewRules.Models("sonnet", null)[1]["efforts"]!.Should().BeOfType<JArray>().Which.Should().BeEmpty();
    }

    [Fact]
    public void models_擁有者發布了清單_照它列_補預設與目前的模型()
    {
        // 從瀏覽器改電腦上對話的模型：選單列那台那個帳戶實際可選的（同桌面的標頭），不是只有「預設」與目前的模型
        ConversationModelOption[] published = [new("opus", "Opus", "複雜工作", ["low", "high"]), new("haiku", "Haiku", null, null)];

        var models = ConversationViewRules.Header(HeaderRow(model: "haiku", effort: "low") with { Models = published }, Online, false)["models"]!.Should().BeOfType<JArray>().Subject;

        models.Select(m => m["value"]!.Value<string>()).Should().Equal("default", "opus", "haiku");
        models[0]["label"]!.Value<string>().Should().Be("預設");
        models[0]["efforts"]!.Values<string>().Should().Equal(["low", "high"], "補的「預設」用第一項的強度（同桌面的 EnsureDefaultModel）");
        models[1]["description"]!.Value<string>().Should().Be("複雜工作");
        models[1]["efforts"]!.Values<string>().Should().Equal("low", "high");
        models[2]["description"]!.Type.Should().Be(JTokenType.Null);
        models[2]["efforts"]!.Should().BeOfType<JArray>().Which.Should().BeEmpty();

        // 清單已有 default 就不再補；目前的模型不在清單裡時加在最後
        var withDefault = ConversationViewRules.Models("gpt-legacy", "high", [new("default", "預設（建議）", "帳戶預設模型", ["high"]), new("gpt-5", "GPT-5", null, ["high"])]);
        withDefault.Select(m => m["value"]!.Value<string>()).Should().Equal("default", "gpt-5", "gpt-legacy");
        withDefault[0]["label"]!.Value<string>().Should().Be("預設（建議）");
        withDefault[2]["description"]!.Value<string>().Should().Be("目前使用中");
        withDefault[2]["efforts"]!.Values<string>().Should().Equal("high");
        ConversationViewRules.Models("default", null, published).Select(m => m["value"]!.Value<string>()).Should().Equal("default", "opus", "haiku");
        ConversationViewRules.Models("opus", "high", []).Select(m => m["value"]!.Value<string>()).Should().Equal(["default", "opus"], "空的清單當成沒發布");
    }

    [Fact]
    public void PublishedModels_只收合格的項目()
    {
        ConversationViewRules.PublishedModels(null).Should().BeNull();
        ConversationViewRules.PublishedModels(new JObject()).Should().BeNull();
        ConversationViewRules.PublishedModels(new JArray()).Should().BeNull();
        ConversationViewRules.PublishedModels(JArray.Parse("""[1, "opus", {"label":"沒有 value"}, {"value":""}]""")).Should().BeNull();

        var list = ConversationViewRules.PublishedModels(JArray.Parse(
            """[{"value":"opus","label":"Opus","description":"說明","efforts":["low",1,"","high"]},{"value":"opus","label":"重複"},{"value":"haiku","label":"","description":2,"efforts":"high"}]"""))!;

        list.Select(m => (m.Value, m.Label, m.Description)).Should().Equal(("opus", "Opus", "說明"), ("haiku", "haiku", null));
        list[0].Efforts.Should().Equal("low", "high");
        list[1].Efforts.Should().BeNull();
        ConversationViewRules.PublishedModels(new JArray(Enumerable.Range(0, 40).Select(i => new JObject { ["value"] = "m" + i, ["label"] = "M" })))!
            .Should().HaveCount(ConversationEventRules.MaxModelOptions);
    }

    [Fact]
    public void permission經Normalize()
    {
        Permission(AgentProvider.Claude, "AcceptEdits").Should().Be("AcceptEdits");
        Permission(AgentProvider.Codex, "AcceptEdits").Should().Be("Auto", "Codex 沒有自動接受編輯");
        Permission(AgentProvider.Codex, "Plan").Should().Be("Ask");
        Permission(AgentProvider.Claude, null).Should().Be("Ask", "不明＝每步詢問（同桌面的預設）");
        Permission(AgentProvider.Claude, "acceptEdits").Should().Be("Ask", "名稱要完全相同");
        Permission(AgentProvider.Claude, "3").Should().Be("Ask", "數字字串不算");
        Permission(AgentProvider.Claude, "Weird").Should().Be("Ask");

        var modes = ConversationViewRules.Header(HeaderRow(provider: AgentProvider.Codex), Online, false)["permissionModes"]!.Should().BeOfType<JArray>().Subject;
        modes.Select(m => m["value"]!.Value<string>()).Should().Equal("Ask", "Auto", "FullAccess");
        modes[0].Should().BeOfType<JObject>().Which.Properties().Select(p => p.Name).Should().Equal("value", "label", "description");
        ConversationViewRules.PermissionModes(AgentProvider.Claude).Should().HaveCount(5);
    }

    [Fact]
    public void 沒有帳戶連結_accountMissing為真()
    {
        var header = ConversationViewRules.Header(HeaderRow(account: null), Online, false);
        header["accountMissing"]!.Value<bool>().Should().BeTrue();
        header["accountId"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public void 擁有者離線_running為假且status是電腦離線_models照樣有預設()
    {
        var header = ConversationViewRules.Header(HeaderRow(model: "opus", effort: "high"), Offline, false);
        header["running"]!.Value<bool>().Should().BeFalse();
        header["startedAt"]!.Type.Should().Be(JTokenType.Null);
        header["status"]!.Value<string>().Should().Be(ConversationViewRules.OfflineStatus).And.Be("電腦離線");
        header["models"]!.Should().BeOfType<JArray>().Which.Select(m => m["value"]!.Value<string>()).Should().Equal("default", "opus");
        header["permissionModes"]!.Should().BeOfType<JArray>().Which.Should().NotBeEmpty();
        header["lastTurn"]!.Value<string>().Should().NotBeNull("上一輪的用量照樣顯示");
    }

    [Fact]
    public void recovering_status是等待電腦重新上傳()
    {
        var header = ConversationViewRules.Header(HeaderRow(leaseState: ConversationLeaseStates.Recovering), Online, false);
        header["status"]!.Value<string>().Should().Be("等待電腦重新上傳");
        ConversationViewRules.Header(HeaderRow(leaseState: ConversationLeaseStates.Recovering), Offline, false)["status"]!.Value<string>()
            .Should().Be("電腦離線", "離線比 recovering 優先：電腦連上之後才會重新上傳");
    }

    [Fact]
    public void 重建中_status是伺服器正在整理()
    {
        ConversationViewRules.Header(HeaderRow(), Online, rebuilding: true)["status"]!.Value<string>().Should().Be("伺服器正在整理這個對話");
        ConversationViewRules.Header(HeaderRow(leaseState: ConversationLeaseStates.Recovering), Offline, rebuilding: true)["status"]!.Value<string>()
            .Should().Be(ConversationViewRules.RebuildingStatus, "重建中最優先：內容可能還不完整");
        ConversationViewRules.Header(HeaderRow(), Online, rebuilding: true)["running"]!.Value<bool>().Should().BeTrue("重建不影響執行中");
    }

    [Theory]
    [InlineData("leased")]
    [InlineData("owned")]
    [InlineData(null)]
    [InlineData("Recovering")]
    public void lease_state不認得_當成owned(string? leaseState)
    {
        ConversationViewRules.IsRecovering(leaseState).Should().BeFalse();
        ConversationViewRules.Header(HeaderRow(leaseState: leaseState), Online, false)["status"]!.Type.Should().Be(JTokenType.Null);
        ConversationViewRules.IsRecovering("recovering").Should().BeTrue();
    }

    [Fact]
    public void 存下的狀態文字_沒有投影時照送()
    {
        ConversationViewRules.Header(HeaderRow(statusText: "執行指令：npm test"), Online, false)["status"]!.Value<string>().Should().Be("執行指令：npm test");
        ConversationViewRules.Header(HeaderRow(statusText: ""), Online, false)["status"]!.Type.Should().Be(JTokenType.Null);
        ConversationViewRules.Header(HeaderRow(statusText: "執行指令：npm test"), Offline, false)["status"]!.Value<string>().Should().Be("電腦離線");
    }

    [Fact]
    public void 沒在執行_startedAt是null()
    {
        ConversationViewRules.Header(HeaderRow(running: false), Online, false)["startedAt"]!.Type.Should().Be(JTokenType.Null);
        ConversationViewRules.Header(HeaderRow(turnStartedAt: null), Online, false)["startedAt"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public void lastTurn同桌面的格式_沒有用量是null_不隨文化特性()
    {
        var usage = new ConversationTurnPayload(ConversationTurnPhases.Completed, ConversationTurnOutcomes.Completed, new ConversationTurnUsage(1_234_567, 89, 0));
        var current = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            ConversationViewRules.LastTurnText(usage).Should().Be("輸入 1,234,567・輸出 89 tokens");
        }
        finally
        {
            CultureInfo.CurrentCulture = current;
        }
        ConversationViewRules.LastTurnText(new ConversationTurnPayload(ConversationTurnPhases.Completed, ConversationTurnOutcomes.Interrupted)).Should().BeNull();
        ConversationViewRules.LastTurnText(null).Should().BeNull();
    }

    // ───── 裝置欄位（P5、答 8） ─────

    [Fact]
    public void 已撤銷或不再上線的擁有者_deviceOnline是false()
    {
        var active = Info(revoked: false);
        var revoked = Info(revoked: true);
        ConversationViewRules.Device(Owner, active, "顯示名稱", online: true).Should().Be(new ConversationDeviceInfo(Owner, "顯示名稱", true));
        ConversationViewRules.Device(Owner, revoked, null, online: true).Should().Be(new ConversationDeviceInfo(Owner, "配對名稱", false), "已撤銷的舊身分永遠離線");
        ConversationViewRules.Device(Owner, active, " ", online: false).Should().Be(new ConversationDeviceInfo(Owner, "配對名稱", false), "不再連線的舊身分");
        ConversationViewRules.Device(Owner, null, "顯示名稱", online: true).Should().Be(new ConversationDeviceInfo(Owner, "顯示名稱", false), "不在裝置目錄裡的");
        ConversationViewRules.Device(null, active, "顯示名稱", online: true).Should().Be(ConversationDeviceInfo.None);

        // 清單與標頭都照它：標頭顯示「電腦離線」
        var device = ConversationViewRules.Device(Owner, revoked, null, online: true);
        ConversationViewRules.Summary(ListRow(), UpdatedAt, 0, device, false)["deviceOnline"]!.Value<bool>().Should().BeFalse();
        var header = ConversationViewRules.Header(HeaderRow(), device, false);
        header["deviceOnline"]!.Value<bool>().Should().BeFalse();
        header["status"]!.Value<string>().Should().Be("電腦離線");
        header["deviceName"]!.Value<string>().Should().Be("配對名稱");
    }

    // ───── 輸入區（Composer） ─────

    [Fact]
    public void queue照displayText_attachments空()
    {
        var composer = ConversationViewRules.Composer(new ConversationRuntimeRow([new("q1", "接著跑測試"), new("q2", "（已隱藏）")], 0, false, null));
        composer.Properties().Select(p => p.Name).Should().Equal("attachments", "busy", "busyText", "queue");
        composer["attachments"]!.Should().BeOfType<JArray>().Which.Should().BeEmpty();
        composer["busy"]!.Value<bool>().Should().BeFalse();
        composer["busyText"]!.Type.Should().Be(JTokenType.Null);
        composer["queue"]!.ToString(Newtonsoft.Json.Formatting.None).Should().Be("[{\"id\":\"q1\",\"text\":\"接著跑測試\"},{\"id\":\"q2\",\"text\":\"（已隱藏）\"}]");

        var empty = ConversationViewRules.Composer(null);
        empty["queue"]!.Should().BeOfType<JArray>().Which.Should().BeEmpty();
        empty["busy"]!.Value<bool>().Should().BeFalse();
        empty.ContainsKey("queueMore").Should().BeFalse();

        ConversationViewRules.Composer(new ConversationRuntimeRow([], 0, true, null))["busyText"]!.Value<string>().Should().Be("正在處理附件…");
        ConversationViewRules.Composer(new ConversationRuntimeRow([], 0, true, "轉檔中"))["busyText"]!.Value<string>().Should().Be("轉檔中");
        ConversationViewRules.Composer(new ConversationRuntimeRow([], 0, false, "轉檔中"))["busyText"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public void More大於0_queueMore照實_等於0時省略()
    {
        ConversationViewRules.Composer(new ConversationRuntimeRow([new("q1", "一")], 7, false, null))["queueMore"]!.Value<int>().Should().Be(7);
        ConversationViewRules.Composer(new ConversationRuntimeRow([new("q1", "一")], 0, false, null)).ContainsKey("queueMore").Should().BeFalse("省略＝0（P11）");
    }

    // ───── 分頁（D20） ─────

    [Fact]
    public void SelectPage_80筆上限()
    {
        ConversationViewRules.SelectPage(Enumerable.Repeat(10, 100).ToList(), ConversationViewRules.PageItems, 1 << 20)
            .Should().Be(new PageSelection(80, true));
        ConversationViewRules.SelectPage(Enumerable.Repeat(10, 80).ToList(), 80, 1 << 20).Should().Be(new PageSelection(80, false));
        ConversationViewRules.SelectPage(Enumerable.Repeat(10, 81).ToList(), 80, 1 << 20).Should().Be(new PageSelection(80, true));
        ConversationViewRules.SelectPage([], 80, 1 << 20).Should().Be(new PageSelection(0, false));
    }

    [Fact]
    public void SelectPage_位元組上限少放且hasMore()
    {
        ConversationViewRules.SelectPage([400, 400, 400], 80, 1000).Should().Be(new PageSelection(2, true));
        ConversationViewRules.SelectPage([500, 500], 80, 1000).Should().Be(new PageSelection(2, false), "正好等於上限照放");
        ConversationViewRules.SelectPage([500, 501, 1], 80, 1000).Should().Be(new PageSelection(1, true), "放不下的那筆之後就停（較早的不跳著放）");
    }

    [Fact]
    public void SelectPage_單筆超過上限仍放一筆()
    {
        ConversationViewRules.SelectPage([5000, 1], 80, 1000).Should().Be(new PageSelection(1, true));
        ConversationViewRules.SelectPage([5000], 80, 1000).Should().Be(new PageSelection(1, false));
    }

    // ───── 代理種類 ─────

    [Fact]
    public void ProviderOf_不認得回null()
    {
        ConversationViewRules.ProviderOf("Claude").Should().Be(AgentProvider.Claude);
        ConversationViewRules.ProviderOf("Codex").Should().Be(AgentProvider.Codex);
        foreach (var wire in new[] { "claude", "CODEX", "0", "1", "", " Claude", "Unknown" })
            ConversationViewRules.ProviderOf(wire).Should().BeNull(wire);
        ConversationViewRules.ProviderOf(null).Should().BeNull();
    }

    [Fact]
    public void ProviderOf_每一種代理都認得()
    {
        // 新的代理種類要在 switch 加一行：漏了這裡會紅
        foreach (var provider in AgentProviders.All)
            ConversationViewRules.ProviderOf(provider.ToString()).Should().Be(provider);
    }

    // ───── 工具 ─────

    private static string Permission(AgentProvider provider, string? mode) => ConversationViewRules.Permission(provider, mode).ToString();

    private static long At(string iso) => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

    private static ConversationListRow ListRow(string? account = "la_1", string? workingDirectory = "C:/repo/proj", long? updatedAt = null) =>
        new(Conv, Owner, account, AgentProvider.Codex, "標題", updatedAt ?? UpdatedAt, true, workingDirectory, Running: true, Waiting: true, 42);

    private static ConversationHeaderRow HeaderRow(string? account = "la_1", AgentProvider provider = AgentProvider.Claude, string? model = "opus",
        string? effort = "high", bool running = true, long? turnStartedAt = 1_000, string? leaseState = ConversationLeaseStates.Owned, string? statusText = null) =>
        new(Conv, Owner, account, provider, "標題", model, effort, "Ask", true, "C:/repo/proj", running, statusText, turnStartedAt,
            new ConversationTurnPayload(ConversationTurnPhases.Completed, ConversationTurnOutcomes.Completed, new ConversationTurnUsage(12_345, 2_100, 0)),
            leaseState, ConversationSyncPolicies.Full, 1, 42);

    private static DeviceInfo Info(bool revoked) =>
        new(Owner, "配對名稱", "MACHINE", null, "1.0.0", "software", "spki", "fp", 1, revoked ? 2 : null, revoked ? "retired" : null);

    [Fact]
    public void 預設強度_照擁有者發布的模型清單()
    {
        IReadOnlyList<Myriagent.Sync.ConversationModelOption> published =
        [
            new("default", "預設", null, ["low", "medium"], "medium"),
            new("claude-opus-4-7", "Opus 4.7", null, ["low", "xhigh"], "xhigh"),
            new("haiku", "Haiku", null, null),
        ];
        ConversationViewRules.DefaultEffort(null, published).Should().Be("medium");
        ConversationViewRules.DefaultEffort("default", published).Should().Be("medium");
        ConversationViewRules.DefaultEffort("claude-opus-4-7", published).Should().Be("xhigh");
        ConversationViewRules.DefaultEffort("haiku", published).Should().BeNull();
        ConversationViewRules.DefaultEffort(null, null).Should().BeNull("舊版電腦沒發布清單");
        var models = ConversationViewRules.Models(null, null, published);
        models.Cast<Newtonsoft.Json.Linq.JObject>().Select(m => m.Value<string>("defaultEffort")).Should().Equal("medium", "xhigh", null);
        var parsed = ConversationViewRules.PublishedModels(Newtonsoft.Json.Linq.JArray.Parse("""[{"value":"a","label":"A","defaultEffort":"high"},{"value":"b","defaultEffort":5}]"""));
        parsed!.Select(m => m.DefaultEffort).Should().Equal("high", null);
    }
}
