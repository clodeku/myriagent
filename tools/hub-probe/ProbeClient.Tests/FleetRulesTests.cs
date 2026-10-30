using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MyriagentHub.ProbeClient.Tests;

/// <summary>
/// 假裝置群的分批、角色與劇本（WP4.9 計畫步 9、D6）：10 台分成 4、4、2 三批、任一批大於 4 會紅；串流最後配對；
/// 串流的劇本每步最多 1,000 段、250 ms、帶時間標記、蓋滿負載期間；持久那一條每秒一段、每段完成；閒置的只連著；
/// 子程序剝掉的環境變數（含 GC 設定）和 hub-e2e 的 child-env.mjs 相同。
/// 轉送階段（WP4.9 步 12）：View 政策、只有 media 步驟的短對話、串流多跑、對話跑完只看角色的 chat、固定種子的 8 MiB 檔
/// </summary>
public sealed class FleetRulesTests
{
    private static FleetTiming Timing(int minutes = 10, int idle = 2, int batches = 3, int seed = 0) => new(minutes, idle, 250, 20, batches, seed);

    [Fact]
    public void 十台分成4_4_2三批()
    {
        FleetRules.Batches(10).Should().Equal(4, 4, 2);
        FleetRules.Batches(4).Should().Equal(4);
        FleetRules.Batches(5).Should().Equal(4, 1);
        FleetRules.Batches(0).Should().BeEmpty();
    }

    [Fact]
    public void 任一批都不大於4台_合計等於台數()
    {
        for (var n = 1; n <= 40; n++)
        {
            var batches = FleetRules.Batches(n);
            batches.Should().OnlyContain(b => b >= 1 && b <= FleetRules.MaxBatch, $"{n} 台");
            batches.Sum().Should().Be(n);
            batches.SkipLast(1).All(b => b == FleetRules.MaxBatch).Should().BeTrue($"只有最後一批可以不滿（{n} 台）");
        }

        FleetRules.MaxBatch.Should().Be(4, "待核准上限是 5（Pairing.cs）：一批最多 4 台");
    }

    [Fact]
    public void 角色_閒置先配對_持久其次_串流最後()
    {
        var plan = FleetRules.Plan(10, 5, 1);
        plan.Select(d => d.Role).Should().Equal(
            FleetRole.Idle, FleetRole.Idle, FleetRole.Idle, FleetRole.Idle, FleetRole.Persistent,
            FleetRole.Stream, FleetRole.Stream, FleetRole.Stream, FleetRole.Stream, FleetRole.Stream);
        plan.Select(d => d.Batch).Should().Equal(0, 0, 0, 0, 1, 1, 1, 1, 2, 2);
        plan.GroupBy(d => d.Batch).Select(g => g.Count()).Should().Equal(FleetRules.Batches(10));
        plan.Select(d => d.Name).Should().OnlyHaveUniqueItems();
        plan.Where(d => d.Role == FleetRole.Idle).Should().OnlyContain(d => d.Title == null);
        plan.Where(d => d.Role != FleetRole.Idle).Select(d => FleetRules.RoleOfTitle(d.Title)).Should().Equal(plan.Where(d => d.Role != FleetRole.Idle).Select(d => (FleetRole?)d.Role));
        FleetRules.RoleOfTitle("修正登入流程").Should().BeNull();
        FleetRules.RoleOfTitle(null).Should().BeNull();
        FluentActions.Invoking(() => FleetRules.Plan(5, 5, 1)).Should().Throw<ArgumentException>("每台同時只跑一個對話");
    }

    [Fact]
    public void 串流的劇本_每步最多1000段_250毫秒_帶標記_蓋滿負載期間()
    {
        var timing = Timing();
        var device = FleetRules.Plan(10, 5, 1)[^1];
        var steps = (JsonArray)FleetRules.Script(device, timing)["steps"]!;
        steps[0]!["op"]!.GetValue<string>().Should().Be("addAccount", "第一步的進度行代表已配對、連上");
        var chat = steps.Single(s => s!["op"]!.GetValue<string>() == "chat")!;
        chat["message"]!.GetValue<string>().Should().Be(device.Title);
        var texts = ((JsonArray)chat["script"]!["steps"]!).Select(t => t!).ToList();
        texts.Should().HaveCount(3, "10 分鐘是三步");
        texts.Should().OnlyContain(t => t["segments"]!.GetValue<int>() <= FleetRules.MaxSegmentsPerStep && t["delayMs"]!.GetValue<int>() == 250
                                        && t["stamp"]!.GetValue<bool>() && t["completeEachSegment"] == null);
        texts.Should().OnlyContain(t => new StringInfo(t["text"]!.GetValue<string>()).LengthInTextElements == 4 * t["segments"]!.GetValue<int>(), "每段 4 個字");
        var streamedMs = texts.Sum(t => t["segments"]!.GetValue<int>() * 250L);
        streamedMs.Should().BeGreaterThanOrEqualTo(timing.Minutes * 60_000L, "蓋滿從全部配對好開始的負載期間");
        chat["seconds"]!.GetValue<int>().Should().BeGreaterThan((int)(streamedMs / 1000));
        Waits(steps).Should().Be(FleetRules.TailSeconds(timing) * 1000L, "跑完再連著閒置");
    }

    [Fact]
    public void 較早配對的串流多跑一點()
    {
        var timing = Timing();
        var plan = FleetRules.Plan(10, 7, 0);
        var early = plan.First(d => d.Role == FleetRole.Stream && d.Batch == 0);
        var late = plan.Last();
        FleetRules.LeadSeconds(early, timing).Should().Be(40);
        FleetRules.LeadSeconds(late, timing).Should().Be(0);
        Segments(FleetRules.Script(early, timing)).Should().Be(Segments(FleetRules.Script(late, timing)) + 40 * 4);
    }

    [Fact]
    public void 持久的劇本_每秒一段_每段完成_帶標記()
    {
        var timing = Timing(minutes: 2);
        var device = FleetRules.Plan(10, 5, 1).Single(d => d.Role == FleetRole.Persistent);
        var steps = (JsonArray)FleetRules.Script(device, timing)["steps"]!;
        var texts = ((JsonArray)steps.Single(s => s!["op"]!.GetValue<string>() == "chat")!["script"]!["steps"]!).Select(t => t!).ToList();
        texts.Should().OnlyContain(t => t["delayMs"]!.GetValue<int>() == FleetRules.PersistentDelayMs && t["completeEachSegment"]!.GetValue<bool>() && t["stamp"]!.GetValue<bool>());
        texts.Sum(t => t["segments"]!.GetValue<int>()).Should().Be(FleetRules.ActiveSeconds(device, timing), "每秒一段");
    }

    [Fact]
    public void 閒置的只連著_wait每步不超過10分鐘()
    {
        var timing = Timing(minutes: 30, idle: 5);
        var device = FleetRules.Plan(10, 5, 1)[0];
        var steps = (JsonArray)FleetRules.Script(device, timing)["steps"]!;
        steps.Select(s => s!["op"]!.GetValue<string>()).Distinct().Should().Equal("addAccount", "wait");
        steps.Where(s => s!["op"]!.GetValue<string>() == "wait").Should().OnlyContain(s => s!["ms"]!.GetValue<int>() <= FleetRules.MaxWaitMs);
        Waits(steps).Should().Be(FleetRules.TotalSeconds(device, timing) * 1000L);
        FleetRules.WaitSteps(1_250_000).Should().Equal(600_000, 600_000, 50_000);
        FleetRules.SegmentSteps(2_400).Should().Equal(1_000, 1_000, 400);
    }

    [Fact]
    public void 回補的種子對話在最前面_每台不同的種子()
    {
        var timing = Timing(seed: 300);
        var plan = FleetRules.Plan(10, 0, 0);
        var first = (JsonArray)FleetRules.Script(plan[0], timing)["steps"]!;
        var second = (JsonArray)FleetRules.Script(plan[1], timing)["steps"]!;
        first[0]!["op"]!.GetValue<string>().Should().Be("seedConversations");
        first[0]!["count"]!.GetValue<int>().Should().Be(300);
        first[0]!["seed"]!.GetValue<int>().Should().Be(FleetRules.SeedBase);
        second[0]!["seed"]!.GetValue<int>().Should().Be(FleetRules.SeedBase + 1);
        first[0]!["baseTimeUtc"]!.GetValue<string>().Should().Be(FleetRules.SeedBaseTimeUtc);
        first[1]!["op"]!.GetValue<string>().Should().Be("addAccount");
        ((JsonArray)FleetRules.Script(plan[0], Timing())["steps"]!)[0]!["op"]!.GetValue<string>().Should().Be("addAccount", "沒有種子就不寫");
    }

    [Fact]
    public void 對話超過chat的時限上限就拒絕()
    {
        var device = FleetRules.Plan(1, 1, 0)[0];
        FluentActions.Invoking(() => FleetRules.Script(device, Timing(minutes: 59, batches: 1))).Should().Throw<ArgumentOutOfRangeException>();
        FleetRules.Script(device, Timing(minutes: 50, batches: 1)).Should().NotBeNull();
    }

    private const string Media = @"C:\work\media\load-01.mp4";

    [Fact]
    public void 轉送階段_每台先設View_再跑只有media步驟的短對話_之後照角色跑()
    {
        var timing = Timing() with { RelaySeconds = 120 };
        foreach (var device in FleetRules.Plan(10, 5, 1))
        {
            var script = FleetRules.Script(device, timing, Media);
            var steps = (JsonArray)script["steps"]!;
            steps[0]!["op"]!.GetValue<string>().Should().Be("addAccount");
            steps[1]!["op"]!.GetValue<string>().Should().Be("policy");
            steps[1]!["level"]!.GetValue<string>().Should().Be("View", "原檔要 View 以上（MediaServerRules.AllowsOriginals）");
            var relay = steps[2]!;
            relay["op"]!.GetValue<string>().Should().Be("chat");
            relay["message"]!.GetValue<string>().Should().Be(FleetRules.RelayTitle(device));
            FleetRules.DeviceOfRelayTitle(FleetRules.RelayTitle(device)).Should().Be(device.Name);
            var media = ((JsonArray)relay["script"]!["steps"]!).Single()!;
            media["media"]!.GetValue<string>().Should().Be(Media);
            relay["conversation"]!.GetValue<string>().Should().NotBe(device.Role switch
            {
                FleetRole.Stream => "stream",
                FleetRole.Persistent => "persist",
                _ => "(閒置沒有角色的對話)",
            }, "媒體放在自己的對話");
            var chats = steps.Select((s, i) => (Step: s!, Number: i + 1)).Where(x => x.Step["op"]!.GetValue<string>() == "chat").ToList();
            FleetRules.RoleChatStep(script).Should().Be(chats[^1].Number, "對話跑完只看最後一個 chat（角色的那一個）");
            chats.Should().HaveCount(device.Role == FleetRole.Idle ? 1 : 2);
        }

        FleetRules.RoleOfTitle("load-relay-03").Should().Be(FleetRole.Idle, "瀏覽器要開（拿網址），但不算延遲");
        FleetRules.DeviceOfRelayTitle("load-stream-03").Should().BeNull();
        FleetRules.DeviceOfRelayTitle("load-relay-").Should().BeNull();
        FleetRules.DeviceOfRelayTitle(null).Should().BeNull();
    }

    [Fact]
    public void 轉送階段_串流與持久多跑階段的秒數加餘裕_閒置的連著到結束()
    {
        var plain = Timing();
        var relay = plain with { RelaySeconds = 120 };
        var plan = FleetRules.Plan(10, 5, 1);
        foreach (var device in plan)
            FleetRules.ActiveSeconds(device, relay).Should().Be(FleetRules.ActiveSeconds(device, plain) + 120 + FleetRules.RelayMarginSeconds);
        var stream = plan[^1];
        Segments(FleetRules.Script(stream, relay, Media)).Should().Be(Segments(FleetRules.Script(stream, plain)) + (120 + FleetRules.RelayMarginSeconds) * 4,
            "串流照跑（媒體短對話的 media 步驟沒有段數）");
        var idle = plan[0];
        Waits((JsonArray)FleetRules.Script(idle, relay, Media)["steps"]!).Should().Be(FleetRules.TotalSeconds(idle, relay) * 1000L);
    }

    [Fact]
    public void 轉送階段_媒體檔要有而且要是完整路徑_沒有轉送階段就不能給()
    {
        var device = FleetRules.Plan(1, 1, 0)[0];
        var relay = Timing(batches: 1) with { RelaySeconds = 120 };
        FluentActions.Invoking(() => FleetRules.Script(device, relay)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => FleetRules.Script(device, Timing(batches: 1), Media)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => FleetRules.Script(device, relay, @"media\load-01.mp4")).Should().Throw<ArgumentException>("桌面只收磁碟代號開頭的路徑");
        FleetRules.RoleChatStep((JsonObject)JsonNode.Parse("""{"steps":[{"op":"wait","ms":1}]}""")!).Should().BeNull();
        // 沒有轉送階段時劇本不變（S9 照舊）
        var plain = (JsonArray)FleetRules.Script(device, Timing(batches: 1))["steps"]!;
        plain.Select(s => s!["op"]!.GetValue<string>()).Should().NotContain("policy");
        FleetRules.RoleChatStep(FleetRules.Script(device, Timing(batches: 1))).Should().Be(2);
    }

    [Fact]
    public void 轉送階段的檔案_固定種子_正好8MiB_開頭是ftyp_每台不同()
    {
        var a = FleetRules.RelayMediaContent(FleetRules.RelaySeedBase);
        var again = FleetRules.RelayMediaContent(FleetRules.RelaySeedBase);
        var b = FleetRules.RelayMediaContent(FleetRules.RelaySeedBase + 1);
        a.Length.Should().Be(8 * 1024 * 1024, "正好一段（MediaRelayRules.MaxSegmentBytes）：沒帶 Range 的 GET 是一次轉送");
        System.Text.Encoding.ASCII.GetString(a, 4, 8).Should().Be("ftypisom");
        var fa = RelayMediaFile.Describe(Media, a);
        fa.Should().Be(RelayMediaFile.Describe(Media, again));
        fa.Sha256.Should().NotBe(RelayMediaFile.Describe(Media, b).Sha256);
        fa.Length.Should().Be(a.Length);
        fa.PrefixSha256.Should().Be(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(a.AsSpan(0, LoadRules.RelayAfterReleaseBytes))));
        fa.Sha256.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void 子程序剝掉的環境變數和child_env_mjs相同()
    {
        var js = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "child-env.mjs"));
        var array = Regex.Match(js, @"STRIP_PREFIXES = \[(?<items>[^\]]*)\]").Groups["items"].Value;
        var prefixes = Regex.Matches(array, "'([^']+)'").Select(m => m.Groups[1].Value).ToList();
        prefixes.Should().NotBeEmpty();
        ChildEnvironment.StripPrefixes.Should().Equal(prefixes);

        var info = new ProcessStartInfo("x.exe");
        info.Environment["CLAUDE_CONFIG_DIR"] = "a";
        info.Environment["anthropic_api_key"] = "b";
        info.Environment["MYRIAGENT_LOAD_PW"] = "c";
        info.Environment["PATH_KEEP_ME"] = "d";
        info.Environment["DOTNET_gcServer"] = "1";
        info.Environment["COMPlus_gcServer"] = "1";
        ChildEnvironment.Apply(info, new Dictionary<string, string> { ["MYRIAGENT_LOAD_PW"] = "放回" });
        info.Environment.ContainsKey("CLAUDE_CONFIG_DIR").Should().BeFalse();
        info.Environment.ContainsKey("anthropic_api_key").Should().BeFalse("不分大小寫");
        info.Environment.ContainsKey("DOTNET_gcServer").Should().BeFalse("GC 設定會蓋掉 MyriagentHub 的 Workstation GC（合併前審查 F3）");
        info.Environment.ContainsKey("COMPlus_gcServer").Should().BeFalse("舊的 COMPlus_ 前綴在 .NET 10 照樣生效");
        info.Environment["MYRIAGENT_LOAD_PW"].Should().Be("放回", "要給子程序的值剝掉之後明確放回");
        info.Environment["PATH_KEEP_ME"].Should().Be("d");
    }

    private static long Waits(JsonArray steps) => steps.Where(s => s!["op"]!.GetValue<string>() == "wait").Sum(s => (long)s!["ms"]!.GetValue<int>());

    private static int Segments(JsonObject script) =>
        ((JsonArray)script["steps"]!).Where(s => s!["op"]!.GetValue<string>() == "chat")
        .SelectMany(s => ((JsonArray)s!["script"]!["steps"]!).Select(t => t!["segments"]?.GetValue<int>() ?? 0)).Sum();
}
