using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Sync;
using Myriagent.Tests.Protocol.Remote;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>遠端指令處理者的純規則（WP4.4 計畫步 6 起；D18、D19、D26）</summary>
public sealed class RemoteHandlerRulesTests
{
    [Theory]
    [InlineData(@"C:\a", true)]
    [InlineData(@"c:/a", true)]
    [InlineData(@"D:\", true)]
    [InlineData(@"\\srv\s", false)]
    [InlineData(@"\\?\C:\a", false)]
    [InlineData(@"\\.\COM1", false)]
    [InlineData(@"//srv/s", false)]
    [InlineData(@"a\b", false)]
    [InlineData(@"C:a", false)]
    [InlineData(@"C:", false)]
    [InlineData(@"\a", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LooksLikeLocalDrivePath_只收磁碟代號開頭的完整路徑(string? path, bool expected)
    {
        RemoteHandlerRules.LooksLikeLocalDrivePath(path).Should().Be(expected);
    }

    [Fact]
    public void QueueId_是r加cid_27字_是合格的項目id()
    {
        var cid = SyncIds.NewUlid(1_791_600_000_000);

        var id = RemoteHandlerRules.QueueId(cid);

        id.Should().Be("r" + cid);
        id.Should().HaveLength(27);
        ConversationIds.IsItemId(id).Should().BeTrue();
        FluentActions.Invoking(() => RemoteHandlerRules.QueueId("")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void QueueIdOf_有來源是r加cid_沒來源或cid不合格是null()
    {
        var cid = SyncIds.NewUlid(1_791_600_000_000);

        RemoteHandlerRules.QueueIdOf(new RemoteOrigin(cid, AgentPermissionMode.Ask, 1, false)).Should().Be("r" + cid);
        RemoteHandlerRules.QueueIdOf(null).Should().BeNull("本機的訊息用新的 Guid");
        RemoteHandlerRules.QueueIdOf(new RemoteOrigin(null!, AgentPermissionMode.Ask, 1, false)).Should().BeNull("queue.json 缺 cid");
        RemoteHandlerRules.QueueIdOf(new RemoteOrigin("", AgentPermissionMode.Ask, 1, false)).Should().BeNull();
        RemoteHandlerRules.QueueIdOf(new RemoteOrigin("..\\x", AgentPermissionMode.Ask, 1, false)).Should().BeNull("不是合格的項目 id");
        RemoteHandlerRules.QueueIdOf(new RemoteOrigin(new string('A', 64), AgentPermissionMode.Ask, 1, false)).Should().BeNull("加上 r 超過 64 字");
    }

    [Theory]
    [InlineData("approve", "allow", "allow")]
    [InlineData("approve", "allowSession", "allowSession")]
    [InlineData("approve", "deny", "deny")]
    [InlineData("approvePlan", "acceptEdits", "acceptEdits")]
    [InlineData("approvePlan", "manual", "manual")]
    [InlineData("approvePlan", "keepPlanning", "keepPlanning")]
    [InlineData("answer", null, "answered")]
    [InlineData("answer", "allow", "answered")]
    [InlineData("send", null, null)]
    public void CardDecision_照ApproveAsync與ApprovePlanAsync與AnswerAsync的對應(string cmd, string? choice, string? expected)
    {
        RemoteHandlerRules.CardDecision(cmd, choice).Should().Be(expected);
    }

    [Fact]
    public void AnswersJson_題目id對字串陣列_順序照原本的()
    {
        var mapped = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["要怎麼彙總？"] = ["依日期", "依月份"],
            ["q2"] = ["hunter2"],
        };

        var json = RemoteHandlerRules.AnswersJson(mapped);

        json.ToString(Newtonsoft.Json.Formatting.None).Should().Be("""{"要怎麼彙總？":["依日期","依月份"],"q2":["hunter2"]}""");
        RemoteHandlerRules.AnswersJson(new Dictionary<string, IReadOnlyList<string>>()).Should().BeEmpty();
    }

    [Fact]
    public void Origin_帶context的最終層級與給的模式()
    {
        var context = new RemoteViewContext(SyncIds.NewUlid(1_791_600_000_001), RemoteSamples.DeviceId, "c1", 1, 1, false, null, 2_000, false, "Chrome・Windows");
        context.SetTier(new RemoteTierResult(2, true));

        RemoteHandlerRules.Origin(context, AgentPermissionMode.Ask).Should().Be(new RemoteOrigin(context.CommandId, AgentPermissionMode.Ask, 2, true));
        RemoteHandlerRules.Origin(context, null).EffectiveMode.Should().BeNull();
    }

    [Fact]
    public void ParseCreate_全部欄位()
    {
        var uploads = new JArray(new JObject { ["blob"] = "b1" });
        var command = new JObject
        {
            ["cmd"] = "conversationCreate", ["accountId"] = "acc-claude", ["permissionMode"] = "AcceptEdits", ["model"] = "opus", ["effort"] = "high",
            ["folder"] = @"D:\proj", ["title"] = "標題", ["text"] = "第一則", ["uploads"] = uploads,
        };

        var request = RemoteHandlerRules.ParseCreate(command)!;

        request.Should().BeEquivalentTo(new RemoteCreateRequest("acc-claude", AgentPermissionMode.AcceptEdits, "opus", "high", @"D:\proj", "標題", "第一則", uploads));
        request.Uploads.Should().BeSameAs(uploads);
    }

    [Fact]
    public void ParseCreate_只有必填_其他是null()
    {
        var request = RemoteHandlerRules.ParseCreate(new JObject { ["accountId"] = "acc-codex", ["permissionMode"] = "Ask" });

        request.Should().Be(new RemoteCreateRequest("acc-codex", AgentPermissionMode.Ask, null, null, null, null, null, null));
    }

    [Fact]
    public void ParseCreate_空白的文字標題模型強度與空字串的資料夾和空的uploads都算沒給_只有空白的資料夾照原樣()
    {
        var blank = RemoteHandlerRules.ParseCreate(new JObject
        {
            ["accountId"] = "acc-claude", ["permissionMode"] = "Plan", ["model"] = " ", ["effort"] = "", ["folder"] = "", ["title"] = " \t", ["text"] = "\n ",
            ["uploads"] = new JArray(),
        })!;
        (blank.Model, blank.Effort, blank.Folder, blank.Title, blank.Text, blank.Uploads).Should().Be((null, null, null, null, null, (JArray?)null));

        var nulls = RemoteHandlerRules.ParseCreate(new JObject
        {
            ["accountId"] = "acc-claude", ["permissionMode"] = "Plan", ["model"] = null, ["effort"] = null, ["folder"] = null,
        })!;
        (nulls.Model, nulls.Effort, nulls.Folder).Should().Be((null, null, null));

        RemoteHandlerRules.ParseCreate(new JObject { ["accountId"] = "acc-claude", ["permissionMode"] = "Plan", ["folder"] = " " })!.Folder
            .Should().Be(" ", "資料夾只有 null 或空字串算沒給，其他交給探針（只有空白是 invalid）");
    }

    [Theory]
    [InlineData(null, "Ask")]
    [InlineData("", "Ask")]
    [InlineData("acc-claude", null)]
    [InlineData("acc-claude", "ask")]
    [InlineData("acc-claude", "0")]
    [InlineData("acc-claude", "Unknown")]
    public void ParseCreate_帳戶或權限模式不合格是null(string? accountId, string? mode)
    {
        var command = new JObject();
        if (accountId != null)
            command["accountId"] = accountId;
        if (mode != null)
            command["permissionMode"] = mode;

        RemoteHandlerRules.ParseCreate(command).Should().BeNull();
    }

    [Fact]
    public void ParseCreate_帳戶id不是字串或權限模式是數值是null()
    {
        RemoteHandlerRules.ParseCreate(new JObject { ["accountId"] = 1, ["permissionMode"] = "Ask" }).Should().BeNull();
        RemoteHandlerRules.ParseCreate(new JObject { ["accountId"] = "acc-claude", ["permissionMode"] = 2 }).Should().BeNull("數值的列舉不收");
    }

    [Theory]
    [InlineData("遠端的標題", "第一則", "遠端的標題")]
    [InlineData("  前後有空白  ", null, "前後有空白")]
    [InlineData(null, "\n  第一行的文字\n第二行", "第一行的文字")]
    [InlineData(" ", "從文字取", "從文字取")]
    [InlineData(null, null, "新對話")]
    [InlineData("", " ", "新對話")]
    public void CreateTitle_有標題用它_沒有取自文字_再沒有是新對話(string? title, string? text, string expected)
    {
        RemoteHandlerRules.CreateTitle(title, text).Should().Be(expected);
    }

    [Fact]
    public void CreateTitle_標題最多80字_文字取的標題照MakeTitle()
    {
        RemoteHandlerRules.CreateTitle(new string('長', 100), null).Should().Be(new string('長', 80));
        RemoteHandlerRules.CreateTitle(null, new string('字', 100)).Should().Be(AgentHubRules.MakeTitle(new string('字', 100)));
    }

    [Theory]
    [InlineData(DriveType.Fixed, "Fixed")]
    [InlineData(DriveType.Removable, "Removable")]
    [InlineData(DriveType.Network, "Network")]
    [InlineData(DriveType.CDRom, "CdRom")]
    [InlineData(DriveType.Ram, "Ram")]
    [InlineData(DriveType.NoRootDirectory, "NoRootDirectory")]
    [InlineData(DriveType.Unknown, "Unknown")]
    [InlineData((DriveType)99, "Unknown")]
    public void DriveKindOf_對應_不認得的是Unknown(DriveType type, string expected)
    {
        RemoteHandlerRules.DriveKindOf(type).Should().Be(Enum.Parse<RemoteDriveKind>(expected));
    }
}
