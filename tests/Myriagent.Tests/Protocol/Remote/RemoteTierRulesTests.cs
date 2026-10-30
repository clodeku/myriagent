using Myriagent.Agents;
using Myriagent.Remote;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Protocol.Remote;

/// <summary>
/// 遠端指令的層級（WP4.1 步 6；設計 8.10、14.11 D5、D9、Q3-A）：型錄每一項 × 事實組合的整張表，加上明列的幾列。
/// 不變式：層級在型錄的最低層級與 2 之間；伺服器端事實不明取低、電腦端取高；事實都知道時兩邊相同；伺服器的結果永遠不高於電腦
/// </summary>
public sealed class RemoteTierRulesTests
{
    // 靜態欄位依宣告順序初始化：後面的欄位用到前面的，順序不能換
    private static readonly AgentPermissionMode?[] Modes = [null, .. Enum.GetValues<AgentPermissionMode>().Cast<AgentPermissionMode?>()];

    private static readonly bool?[] Flags = [null, false, true];

    private static readonly AgentRemoteRisk?[] Risks = [null, AgentRemoteRisk.Normal, AgentRemoteRisk.High];

    /// <summary>七個事實欄位各有幾個值（第 0 個值一律是 null），依 RemoteTierFacts 的參數順序</summary>
    private static readonly int[] Radix = [Modes.Length, Flags.Length, Risks.Length, Flags.Length, Flags.Length, Flags.Length, Modes.Length];

    /// <summary>所有事實組合；索引是以 <see cref="Radix"/> 為底的七位數（最後一位的權重是 1）</summary>
    private static readonly RemoteTierFacts[] Facts = BuildFacts();

    /// <summary>每個指令變化在每個事實組合下的兩邊結果（三個整表測試共用，只算一次）</summary>
    private static readonly Lazy<List<Row>> Table = new(BuildTable);

    private sealed record Row(string Cmd, JObject Command, RemoteTierResult[] Server, RemoteTierResult[] Device);

    private static RemoteTierFacts[] BuildFacts()
    {
        var facts = new List<RemoteTierFacts>();
        foreach (var mode in Modes)
        foreach (var preApproved in Flags)
        foreach (var risk in Risks)
        foreach (var known in Flags)
        foreach (var forbidden in Flags)
        foreach (var inScope in Flags)
        foreach (var target in Modes)
            facts.Add(new RemoteTierFacts(mode, preApproved, risk, known, forbidden, inScope, target));
        return facts.ToArray();
    }

    private static List<Row> BuildTable()
        => AllCommands()
            .Select(c => new Row(c.Cmd, c.Command,
                Facts.Select(f => Server(c.Cmd, c.Command, f)).ToArray(),
                Facts.Select(f => Device(c.Cmd, c.Command, f)).ToArray()))
            .ToList();

    private static int Stride(int field)
    {
        var stride = 1;
        for (var k = field + 1; k < Radix.Length; k++)
            stride *= Radix[k];
        return stride;
    }

    /// <summary>事實組合 <paramref name="index"/> 的第 <paramref name="field"/> 個欄位的值的位置（0＝null）</summary>
    private static int Digit(int index, int field) => index / Stride(field) % Radix[field];

    /// <summary>型錄每一項的變化：只有必填、全部欄位、每個列舉值、附言有無、路徑有無、dirsOnly、新對話的資料夾與文字有無</summary>
    private static IEnumerable<(string Cmd, JObject Command)> AllCommands()
    {
        foreach (var spec in RemoteCommandCatalog.All)
        {
            var variants = new List<JObject> { RemoteSamples.Required(spec), RemoteSamples.Full(spec) };
            foreach (var choice in spec.Fields.Where(f => f.Kind == RemoteFieldKind.Choice))
            {
                foreach (var value in choice.Values!)
                {
                    foreach (var baseline in new[] { RemoteSamples.Required(spec), RemoteSamples.Full(spec) })
                    {
                        baseline[choice.Name] = value;
                        variants.Add(baseline);
                    }
                }
            }

            if (spec.Name == "setOption")
            {
                string[] values = ["", "default", .. Enum.GetNames<AgentPermissionMode>()];
                foreach (var value in values)
                    variants.Add(RemoteSamples.Command("setOption", ("key", "permission"), ("value", value)));
            }

            foreach (var variant in variants.ToList())
            {
                foreach (var field in spec.Fields.Where(f => !f.Required))
                {
                    var without = (JObject)variant.DeepClone();
                    without.Remove(field.Name);
                    variants.Add(without);
                    if (field.Kind == RemoteFieldKind.Text)
                    {
                        var blank = (JObject)variant.DeepClone();
                        blank[field.Name] = " \n";
                        variants.Add(blank);
                    }

                    if (field.Nullable)
                    {
                        var nulled = (JObject)variant.DeepClone();
                        nulled[field.Name] = JValue.CreateNull();
                        variants.Add(nulled);
                    }

                    if (field.Kind == RemoteFieldKind.Bool)
                    {
                        var no = (JObject)variant.DeepClone();
                        no[field.Name] = false;
                        variants.Add(no);
                    }
                }
            }

            foreach (var variant in variants.DistinctBy(v => v.ToString(Newtonsoft.Json.Formatting.None)))
            {
                RemoteCommandShapeRules.Check(variant).Should().BeNull($"{variant} 要是合格的指令");
                yield return (spec.Name, variant);
            }
        }
    }

    private static bool AtMost(RemoteTierResult a, RemoteTierResult b) => a.Tier <= b.Tier && (!a.FullAccess || b.FullAccess);

    private static RemoteTierResult Server(string cmd, JObject command, RemoteTierFacts facts) => RemoteTierRules.Evaluate(cmd, command, facts, RemoteSide.Server);

    private static RemoteTierResult Device(string cmd, JObject command, RemoteTierFacts facts) => RemoteTierRules.Evaluate(cmd, command, facts, RemoteSide.Device);

    [Fact]
    public void 整張表_層級在最低層級與2之間_伺服器不高於電腦_事實都知道時相同()
    {
        var table = Table.Value;
        table.Select(r => r.Cmd).Distinct().Should().HaveCount(RemoteCommandCatalog.All.Count, "型錄的每一項都在表裡");
        var failures = new List<string>();
        foreach (var row in table)
        {
            var baseTier = RemoteCommandCatalog.Find(row.Cmd)!.BaseTier;
            for (var index = 0; index < Facts.Length && failures.Count <= 20; index++)
            {
                var (server, device) = (row.Server[index], row.Device[index]);
                if (server.Tier < baseTier || server.Tier > 2 || device.Tier < baseTier || device.Tier > 2)
                    failures.Add($"範圍：{row.Command} {Facts[index]} → {server}／{device}");
                if (server.FullAccess && server.Tier != 2 || device.FullAccess && device.Tier != 2)
                    failures.Add($"FullAccess 只在層級 2：{row.Command} {Facts[index]}");
                if (!AtMost(server, device))
                    failures.Add($"伺服器高於電腦：{row.Command} {Facts[index]} → {server}／{device}");
                var allKnown = Enumerable.Range(0, Radix.Length).All(field => Digit(index, field) != 0);
                if (allKnown && server != device)
                    failures.Add($"事實都知道卻不同：{row.Command} {Facts[index]} → {server}／{device}");
            }
        }

        failures.Should().BeEmpty();
        (table.Count * Facts.Length).Should().BeGreaterThan(100_000);
    }

    [Fact]
    public void 事實不明時_伺服器取低_電腦取高()
    {
        var failures = new List<string>();
        foreach (var row in Table.Value)
        {
            for (var index = 0; index < Facts.Length && failures.Count <= 20; index++)
            {
                for (var field = 0; field < Radix.Length; field++)
                {
                    var digit = Digit(index, field);
                    if (digit == 0)
                        continue;
                    var unknown = index - digit * Stride(field);
                    if (!AtMost(row.Device[index], row.Device[unknown]))
                        failures.Add($"電腦：第 {field} 個事實不明反而變低：{row.Command} {Facts[index]}");
                    // 伺服器看得到指令寫的模式名稱，TargetMode（第 6 個）不明時用它（不是「最低」），另在下面的測試斷言
                    if (field != 6 && !AtMost(row.Server[unknown], row.Server[index]))
                        failures.Add($"伺服器：第 {field} 個事實不明反而變高：{row.Command} {Facts[index]}");
                }
            }
        }

        failures.Should().BeEmpty();
    }

    [Fact]
    public void 什麼都不知道時_電腦取最高_伺服器取最低()
    {
        Facts[0].Should().Be(RemoteTierFacts.Unknown);
        foreach (var row in Table.Value)
        {
            row.Device[0].Should().Be(row.Device.Aggregate((a, b) => a.Max(b)), $"{row.Command}");
            // 伺服器：TargetMode 不明的組合裡最低的（TargetMode 有值時用它，可能比指令寫的名稱低，例如代理種類的正規化）
            var serverTargetUnknown = Enumerable.Range(0, Facts.Length).Where(i => Digit(i, 6) == 0).Select(i => row.Server[i]).ToList();
            serverTargetUnknown.Should().HaveCount(Facts.Length / Radix[6]);
            serverTargetUnknown.Should().OnlyContain(r => AtMost(row.Server[0], r), $"{row.Command}");
        }
    }

    [Theory]
    // 層級 0 與 1 的固定項
    [InlineData("""{"cmd":"stop"}""", 0, false)]
    [InlineData("""{"cmd":"cancelQueued","id":"q1"}""", 1, false)]
    [InlineData("""{"cmd":"skipQuestion","itemId":"i1"}""", 1, false)]
    [InlineData("""{"cmd":"rename","title":"x"}""", 1, false)]
    [InlineData("""{"cmd":"resetFolder"}""", 1, false)]
    [InlineData("""{"cmd":"removeAttachment","id":"a1"}""", 1, false)]
    [InlineData("""{"cmd":"fileSearch","requestId":"r","query":"x"}""", 1, false)]
    [InlineData("""{"cmd":"accountRefresh"}""", 1, false)]
    [InlineData("""{"cmd":"cliCheck"}""", 1, false)]
    [InlineData("""{"cmd":"accountLoginCancel","accountId":"a"}""", 1, false)]
    [InlineData("""{"cmd":"approve","choice":"deny"}""", 1, false)]
    [InlineData("""{"cmd":"approvePlan","choice":"manual"}""", 1, false)]
    [InlineData("""{"cmd":"approvePlan","choice":"keepPlanning","message":"  "}""", 1, false)]
    [InlineData("""{"cmd":"setOption","key":"model","value":"x"}""", 1, false)]
    [InlineData("""{"cmd":"setOption","key":"effort","value":"x"}""", 1, false)]
    [InlineData("""{"cmd":"listFiles","requestId":"r"}""", 1, false)]
    [InlineData("""{"cmd":"listFiles","requestId":"r","path":null,"dirsOnly":false}""", 1, false)]
    // 層級 2 的固定項
    [InlineData("""{"cmd":"approve","choice":"allowSession"}""", 2, false)]
    [InlineData("""{"cmd":"approvePlan","choice":"acceptEdits"}""", 2, false)]
    [InlineData("""{"cmd":"listFiles","requestId":"r","dirsOnly":true}""", 2, false)]
    [InlineData("""{"cmd":"delete"}""", 2, false)]
    [InlineData("""{"cmd":"setAccount","accountId":"a"}""", 2, false)]
    [InlineData("""{"cmd":"fileFetch","requestId":"r","path":"C:\\a"}""", 2, false)]
    [InlineData("""{"cmd":"accountAdd","provider":"Claude"}""", 2, false)]
    [InlineData("""{"cmd":"accountLogin","accountId":"a"}""", 2, false)]
    [InlineData("""{"cmd":"accountLoginDevice","logicalAccountId":"la_01J9QZ3K8M5R7T2V4W6X8Y0A1B","deviceId":"d_01J9QZ3K8M5R7T2V4W6X8Y0A1B"}""", 2, false)]
    [InlineData("""{"cmd":"accountLogout","accountId":"a"}""", 2, false)]
    [InlineData("""{"cmd":"accountRemove","accountId":"a"}""", 2, false)]
    [InlineData("""{"cmd":"cliInstall","provider":"Codex"}""", 2, false)]
    [InlineData("""{"cmd":"cliUpdate","provider":"Codex"}""", 2, false)]
    public void 不看事實的指令_兩邊都一樣(string json, int tier, bool fullAccess)
    {
        var command = JObject.Parse(json);
        var cmd = command.Value<string>("cmd")!;
        Server(cmd, command, RemoteTierFacts.Unknown).Should().Be(new RemoteTierResult(tier, fullAccess));
        Device(cmd, command, RemoteTierFacts.Unknown).Should().Be(new RemoteTierResult(tier, fullAccess));
    }

    [Fact]
    public void 代理輸入_一般是1_完全自動或有預先放行是2加完全自動_Q3A()
    {
        var send = RemoteSamples.Command("send");
        Device("send", send, new RemoteTierFacts(AgentPermissionMode.Ask, PreApproved: false)).Should().Be(new RemoteTierResult(1, false));
        Device("send", send, new RemoteTierFacts(AgentPermissionMode.Auto, PreApproved: false)).Should().Be(new RemoteTierResult(1, false), "自動模式本身不升級");
        Device("send", send, new RemoteTierFacts(AgentPermissionMode.FullAccess, PreApproved: false)).Should().Be(new RemoteTierResult(2, true));
        Device("send", send, new RemoteTierFacts(AgentPermissionMode.Ask, PreApproved: true)).Should().Be(new RemoteTierResult(2, true));
        Device("send", send, new RemoteTierFacts(AgentPermissionMode.Ask)).Should().Be(new RemoteTierResult(2, true), "預先放行偵測不到算有（Q3-A）");
        Server("send", send, new RemoteTierFacts(AgentPermissionMode.Ask)).Should().Be(new RemoteTierResult(1, false));
        Server("send", send, RemoteTierFacts.Unknown).Should().Be(new RemoteTierResult(1, false));
        Device("send", send, RemoteTierFacts.Unknown).Should().Be(new RemoteTierResult(2, true));

        var answer = RemoteSamples.Command("answer");
        Device("answer", answer, new RemoteTierFacts(AgentPermissionMode.Plan, PreApproved: false)).Should().Be(new RemoteTierResult(1, false));
        Device("answer", answer, new RemoteTierFacts(AgentPermissionMode.FullAccess, PreApproved: false)).Should().Be(new RemoteTierResult(2, true));
    }

    [Fact]
    public void 核准_風險一般的allow是1_其他是2_附言照代理輸入()
    {
        var allow = RemoteSamples.Command("approve", ("choice", "allow"));
        var safe = new RemoteTierFacts(AgentPermissionMode.Ask, PreApproved: false);
        Device("approve", allow, safe with { Risk = AgentRemoteRisk.Normal }).Should().Be(new RemoteTierResult(1, false));
        Device("approve", allow, safe with { Risk = AgentRemoteRisk.High }).Should().Be(new RemoteTierResult(2, false));
        Device("approve", allow, safe).Should().Be(new RemoteTierResult(2, false), "沒分類的當成非一般（D9）");
        Server("approve", allow, safe).Should().Be(new RemoteTierResult(1, false), "伺服器不知道風險時取低");

        var deny = RemoteSamples.Command("approve", ("choice", "deny"));
        Device("approve", deny, safe).Should().Be(new RemoteTierResult(1, false));
        var denyWithMessage = RemoteSamples.Command("approve", ("choice", "deny"), ("message", "改用 pnpm"));
        Device("approve", denyWithMessage, safe).Should().Be(new RemoteTierResult(1, false), "附言送進代理，照代理輸入：一般是 1");
        Device("approve", denyWithMessage, safe with { Mode = AgentPermissionMode.FullAccess }).Should().Be(new RemoteTierResult(2, true));
        Device("approve", denyWithMessage, safe with { PreApproved = true }).Should().Be(new RemoteTierResult(2, true));
        Device("approve", deny, safe with { PreApproved = true }).Should().Be(new RemoteTierResult(1, false), "不附言時不看預先放行");

        var plan = RemoteSamples.Command("approvePlan", ("choice", "manual"), ("message", "先改測試"));
        Device("approvePlan", plan, safe with { Mode = AgentPermissionMode.FullAccess }).Should().Be(new RemoteTierResult(2, true));
        Device("approvePlan", plan, safe).Should().Be(new RemoteTierResult(1, false));
    }

    [Theory]
    [InlineData("Ask", 1, false)]
    [InlineData("Plan", 1, false)]
    [InlineData("AcceptEdits", 2, false)]
    [InlineData("Auto", 2, false)]
    [InlineData("FullAccess", 2, true)]
    public void 改權限模式與新對話的模式(string modeName, int tier, bool fullAccess)
    {
        var mode = Enum.Parse<AgentPermissionMode>(modeName);
        var expected = new RemoteTierResult(tier, fullAccess);
        RemoteTierRules.ModeTier(mode).Should().Be(expected);
        var set = RemoteSamples.Command("setOption", ("key", "permission"), ("value", mode.ToString()));
        Server("setOption", set, RemoteTierFacts.Unknown).Should().Be(expected, "伺服器照指令寫的名稱");
        Device("setOption", set, new RemoteTierFacts(TargetMode: mode)).Should().Be(expected);
        Device("setOption", set, RemoteTierFacts.Unknown).Should().Be(new RemoteTierResult(2, true), "電腦一定要填實際會設成的模式，沒填取最高");

        var create = RemoteSamples.Command("conversationCreate", ("permissionMode", mode.ToString()));
        Server(RemoteCommandCatalog.ConversationCreate, create, RemoteTierFacts.Unknown).Should().Be(expected);
        Device(RemoteCommandCatalog.ConversationCreate, create, new RemoteTierFacts(TargetMode: mode)).Should().Be(expected, "沒給資料夾、沒有文字");
    }

    [Fact]
    public void 改權限的default與空字串_伺服器當成每步詢問_電腦看實際的模式()
    {
        foreach (var value in new[] { "default", "" })
        {
            var set = RemoteSamples.Command("setOption", ("key", "permission"), ("value", value));
            Server("setOption", set, RemoteTierFacts.Unknown).Should().Be(new RemoteTierResult(1, false));
            Device("setOption", set, new RemoteTierFacts(TargetMode: AgentPermissionMode.FullAccess)).Should().Be(new RemoteTierResult(2, true), "預設是完全自動時");
        }

        var named = RemoteSamples.Command("setOption", ("key", "permission"), ("value", "FullAccess"));
        Server("setOption", named, new RemoteTierFacts(TargetMode: AgentPermissionMode.Ask)).Should().Be(new RemoteTierResult(1, false), "事實優先（代理種類正規化之後）");
    }

    [Fact]
    public void 選資料夾_已知而且不在禁止清單是1()
    {
        var set = RemoteSamples.Command("setFolder", ("path", @"C:\repo"));
        Device("setFolder", set, new RemoteTierFacts(FolderKnown: true, FolderForbidden: false)).Should().Be(new RemoteTierResult(1, false));
        Device("setFolder", set, new RemoteTierFacts(FolderKnown: false, FolderForbidden: false)).Should().Be(new RemoteTierResult(2, false));
        Device("setFolder", set, new RemoteTierFacts(FolderKnown: true, FolderForbidden: true)).Should().Be(new RemoteTierResult(2, false));
        Device("setFolder", set, new RemoteTierFacts(FolderKnown: true)).Should().Be(new RemoteTierResult(2, false));
        Server("setFolder", set, RemoteTierFacts.Unknown).Should().Be(new RemoteTierResult(1, false));

        var create = RemoteSamples.Command("conversationCreate", ("permissionMode", "Ask"), ("folder", @"C:\repo"));
        Device(RemoteCommandCatalog.ConversationCreate, create, new RemoteTierFacts(TargetMode: AgentPermissionMode.Ask, FolderKnown: false, FolderForbidden: false))
            .Should().Be(new RemoteTierResult(2, false), "新對話的資料夾照 setFolder");
        Device(RemoteCommandCatalog.ConversationCreate, create, new RemoteTierFacts(TargetMode: AgentPermissionMode.Ask, FolderKnown: true, FolderForbidden: false))
            .Should().Be(new RemoteTierResult(1, false));
    }

    [Fact]
    public void 列檔案_範圍內是1_範圍外或只列資料夾是2()
    {
        var list = RemoteSamples.Command("listFiles", ("path", @"C:\repo\src"));
        Device("listFiles", list, new RemoteTierFacts(PathInScope: true)).Should().Be(new RemoteTierResult(1, false));
        Device("listFiles", list, new RemoteTierFacts(PathInScope: false)).Should().Be(new RemoteTierResult(2, false));
        Device("listFiles", list, RemoteTierFacts.Unknown).Should().Be(new RemoteTierResult(2, false));
        Server("listFiles", list, RemoteTierFacts.Unknown).Should().Be(new RemoteTierResult(1, false));
        var dirs = RemoteSamples.Command("listFiles", ("path", @"C:\repo\src"), ("dirsOnly", true));
        Device("listFiles", dirs, new RemoteTierFacts(PathInScope: true)).Should().Be(new RemoteTierResult(2, false), "資料夾選擇器能瀏覽整台");
    }

    [Fact]
    public void 新對話帶文字_照新對話的模式的代理輸入()
    {
        var create = RemoteSamples.Command("conversationCreate", ("permissionMode", "Ask"), ("text", "幫我看 README"));
        Device(RemoteCommandCatalog.ConversationCreate, create, new RemoteTierFacts(PreApproved: false, TargetMode: AgentPermissionMode.Ask))
            .Should().Be(new RemoteTierResult(1, false));
        Device(RemoteCommandCatalog.ConversationCreate, create, new RemoteTierFacts(PreApproved: true, TargetMode: AgentPermissionMode.Ask))
            .Should().Be(new RemoteTierResult(2, true), "那個帳戶有預先放行規則");
        Device(RemoteCommandCatalog.ConversationCreate, create, new RemoteTierFacts(Mode: AgentPermissionMode.FullAccess, PreApproved: false, TargetMode: AgentPermissionMode.Ask))
            .Should().Be(new RemoteTierResult(1, false), "Mode 是既有對話的模式，新對話不看");
    }

    [Fact]
    public void 最終層級取較高_FullAccess取OR()
    {
        RemoteTierRules.Final(1, false, new RemoteTierResult(2, false)).Should().Be(new RemoteTierResult(2, false));
        RemoteTierRules.Final(2, false, new RemoteTierResult(1, false)).Should().Be(new RemoteTierResult(2, false), "伺服器宣告較高時不降");
        RemoteTierRules.Final(2, true, new RemoteTierResult(1, false)).Should().Be(new RemoteTierResult(2, true));
        RemoteTierRules.Final(0, false, new RemoteTierResult(2, true)).Should().Be(new RemoteTierResult(2, true));
        var act = () => RemoteTierRules.Final(3, false, new RemoteTierResult(1, false));
        act.Should().Throw<ArgumentOutOfRangeException>();
        act = () => RemoteTierRules.Final(-1, false, new RemoteTierResult(1, false));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void 不在型錄的指令丟例外()
    {
        foreach (var cmd in new[] { "ready", "newConversation", "syncSetPolicy", "takeover", "Send", "" })
        {
            var act = () => RemoteTierRules.Evaluate(cmd, new JObject { ["cmd"] = cmd }, RemoteTierFacts.Unknown, RemoteSide.Device);
            act.Should().Throw<ArgumentOutOfRangeException>(cmd);
        }
    }
}
