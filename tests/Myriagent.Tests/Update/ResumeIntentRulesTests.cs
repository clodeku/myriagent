using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Myriagent.Update;

namespace Myriagent.Tests.Update;

public sealed class ResumeIntentRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    private static AppResumeFile File(string token = "tok", DateTime? createdUtc = null)
        => new() { Token = token, From = "0.2.1-aaaaaa1", To = "0.2.2-bbbbbb2", CreatedAt = new DateTimeOffset(createdUtc ?? Now.AddMinutes(-1)).ToUnixTimeMilliseconds() };

    [Fact]
    public void token相符而且一小時內_有效()
        => ResumeIntentRules.Validate(File(), "tok", Now).Should().Be(ResumeValidity.Ok);

    [Fact]
    public void 一般啟動沒有token_或token不同_都當不符()
    {
        ResumeIntentRules.Validate(File(), null, Now).Should().Be(ResumeValidity.TokenMismatch);
        ResumeIntentRules.Validate(File(), "other", Now).Should().Be(ResumeValidity.TokenMismatch);
    }

    [Fact]
    public void 超過一小時_太舊()
        => ResumeIntentRules.Validate(File(createdUtc: Now.AddMinutes(-61)), "tok", Now).Should().Be(ResumeValidity.TooOld);

    [Fact]
    public void 建立時間超出範圍_當太舊不丟例外()
    {
        var file = File();
        file.CreatedAt = long.MaxValue;
        ResumeIntentRules.Validate(file, "tok", Now).Should().Be(ResumeValidity.TooOld, "手改或損毀的 resume.json 不能讓啟動當掉");
        file.CreatedAt = long.MinValue;
        ResumeIntentRules.Validate(file, "tok", Now).Should().Be(ResumeValidity.TooOld);
    }

    [Fact]
    public void 沒有檔案()
        => ResumeIntentRules.Validate(null, "tok", Now).Should().Be(ResumeValidity.Missing);

    [Fact]
    public void 換版前主視窗藏著_帶相符的token啟動才藏著主視窗()
    {
        var hidden = File();
        hidden.Ui.MainHidden = true;

        ResumeIntentRules.StartMainHidden(hidden, "tok").Should().BeTrue("無人值守換版不能把藏在系統匣的主視窗叫出來");
        ResumeIntentRules.StartMainHidden(hidden, null).Should().BeFalse("一般啟動（使用者自己開的）一律顯示");
        ResumeIntentRules.StartMainHidden(hidden, "other").Should().BeFalse("不是這次換版寫的");
        ResumeIntentRules.StartMainHidden(File(), "tok").Should().BeFalse("換版前主視窗開著");
        ResumeIntentRules.StartMainHidden(null, "tok").Should().BeFalse("沒有 resume.json（或讀不到）照常顯示");
    }

    [Fact]
    public void 舊版寫的resume沒有MainHidden欄位_照常顯示主視窗()
    {
        using var temp = new TempDirectory("resume");
        var path = ResumeFileStore.PathFor(temp.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, """{ "Token": "tok", "Ui": { "Popouts": [] } }""");

        var file = ResumeFileStore.Load(temp.Root);

        file!.Ui.MainHidden.Should().BeFalse();
        ResumeIntentRules.StartMainHidden(file, "tok").Should().BeFalse();
    }

    [Fact]
    public void 只有有效而且mode是continue才接續()
    {
        var entry = new ResumeConversation { Id = "c1", Mode = ResumeMode.Continue };
        ResumeIntentRules.ShouldContinue(entry, ResumeValidity.Ok).Should().BeTrue();
        ResumeIntentRules.ShouldContinue(entry, ResumeValidity.TokenMismatch).Should().BeFalse();
        ResumeIntentRules.ShouldContinue(entry, ResumeValidity.TooOld).Should().BeFalse();
        ResumeIntentRules.ShouldContinue(new ResumeConversation { Id = "c2", Mode = ResumeMode.None, WhyNot = CliNoContinueReason.RiskyTool }, ResumeValidity.Ok).Should().BeFalse();
    }

    [Fact]
    public void 不接續的原因與說明()
    {
        var risky = new ResumeConversation { Id = "c", Mode = ResumeMode.None, WhyNot = CliNoContinueReason.RiskyTool, RiskOperation = "git push" };
        ResumeIntentRules.ReasonOf(risky, ResumeValidity.Ok).Should().Be(CliNoContinueReason.RiskyTool);
        ResumeIntentRules.DescribeNotContinued(risky, ResumeValidity.Ok, "0.2.2").Should().Contain("git push");

        var cont = new ResumeConversation { Id = "c", Mode = ResumeMode.Continue };
        ResumeIntentRules.ReasonOf(cont, ResumeValidity.TooOld).Should().Be(CliNoContinueReason.TooOld);
        ResumeIntentRules.DescribeNotContinued(cont, ResumeValidity.TokenMismatch, null).Should().Be("上次 app 更新沒有完成（程式在換版前結束），這個對話被中斷後沒有自動接續。按「繼續」請代理從中斷處接著做。");
        ResumeIntentRules.ReasonOf(cont, ResumeValidity.TokenMismatch).Should().Be(CliNoContinueReason.Unknown);
        CliRestartRules.CanContinueManually(ResumeIntentRules.ReasonOf(cont, ResumeValidity.TokenMismatch)).Should().BeTrue();

        // 標頭的 detail 是純文字（不跳脫）；Notice 是 Markdown（操作摘要跳脫，同 CliRestartRules.NotContinuedNotice）
        var publish = new ResumeConversation { Id = "c", Mode = ResumeMode.None, WhyNot = CliNoContinueReason.RiskyTool, RiskOperation = "npm publish --access public" };
        var plain = ResumeIntentRules.DescribeNotContinued(publish, ResumeValidity.Ok, "0.2.2", markdown: false);
        plain.Should().Contain("〈npm publish --access public〉").And.NotContain("\\");
        ResumeIntentRules.DescribeNotContinued(publish, ResumeValidity.Ok, "0.2.2", markdown: true).Should().Contain(@"npm publish \-\-access public");
    }

    [Fact]
    public void 上次排空沒完成_高風險操作的警告不能丟()
    {
        var risky = new ResumeConversation { Id = "c", Mode = ResumeMode.None, WhyNot = CliNoContinueReason.RiskyTool, RiskOperation = "git push" };

        ResumeIntentRules.ReasonOf(risky, ResumeValidity.TokenMismatch).Should().Be(CliNoContinueReason.RiskyTool);
        var text = ResumeIntentRules.DescribeNotContinued(risky, ResumeValidity.TokenMismatch, null);
        text.Should().StartWith("上次 app 更新沒有完成（程式在換版前結束），這個對話被中斷後沒有自動接續");
        text.Should().Contain("上一個操作〈git push〉結果未知");
    }

    [Fact]
    public void 使用者按了停止的_太舊或token不符也不給繼續()
    {
        var stopped = new ResumeConversation { Id = "c", Mode = ResumeMode.None, WhyNot = CliNoContinueReason.UserStopped };

        foreach (var validity in new[] { ResumeValidity.Ok, ResumeValidity.TooOld, ResumeValidity.TokenMismatch })
        {
            ResumeIntentRules.ReasonOf(stopped, validity).Should().Be(CliNoContinueReason.UserStopped, "同 CliRestartRules.WhyNotContinue：按了停止優先於太舊");
            CliRestartRules.CanContinueManually(ResumeIntentRules.ReasonOf(stopped, validity)).Should().BeFalse();
        }
        ResumeIntentRules.DescribeNotContinued(stopped, ResumeValidity.TooOld, "0.2.2").Should().Be("Clode Myriagent 已更新為 0.2.2，這個對話沒有自動接續：你按了停止");
    }

    [Fact]
    public void 接續提示_更新與退回()
    {
        ResumeIntentRules.ContinuedNotice("0.2.2").Should().Be("Clode Myriagent 已更新為 0.2.2，已請代理從中斷處繼續");
        ResumeIntentRules.ContinuedNotice("0.2.1", rolledBack: true).Should().Be("新版啟動失敗，Clode Myriagent 已退回 0.2.1，已請代理從中斷處繼續");
        ResumeIntentRules.VersionOfRelease("0.2.1-aaaaaa1").Should().Be("0.2.1");
        ResumeIntentRules.VersionOfRelease("不是 release").Should().Be("不是 release");
        ResumeIntentRules.VersionOfRelease(null).Should().BeNull();
    }

    [Theory]
    [InlineData(AgentProvider.Claude, 0, 0)]
    [InlineData(AgentProvider.Claude, 1, 0)]
    [InlineData(AgentProvider.Claude, 2, 3)]
    [InlineData(AgentProvider.Claude, 5, 6)]
    [InlineData(AgentProvider.Codex, 3, 0)]
    [InlineData(AgentProvider.Codex, 4, 3)]
    public void 啟動間隔照同時重開上限分批(object provider, int index, int seconds)
        => ResumeIntentRules.StartDelay(index, (AgentProvider)provider).Should().Be(TimeSpan.FromSeconds(seconds));

    [Fact]
    public void 正規化_清單本身是null_Ui是null_都換成空的()
    {
        var file = ResumeIntentRules.Normalize(new AppResumeFile
        {
            Token = null!, From = null!, Conversations = null!, DeferredSends = null!, RemoteControl = null!, Ui = null!,
        });

        file.Token.Should().BeEmpty();
        file.From.Should().BeEmpty();
        file.Conversations.Should().BeEmpty();
        file.DeferredSends.Should().BeEmpty();
        file.RemoteControl.Should().BeEmpty();
        file.Ui.Should().NotBeNull();
        file.Ui.Popouts.Should().BeEmpty();
        file.Ui.Drafts.Should().BeEmpty();
        file.Ui.NewConversations.Should().BeEmpty();

        var ui = ResumeIntentRules.Normalize(new AppResumeFile { Ui = new ResumeUi { Popouts = null!, Drafts = null!, NewConversations = null! } }).Ui;
        ui.Popouts.Should().BeEmpty();
        ui.Drafts.Should().BeEmpty();
        ui.NewConversations.Should().BeEmpty();
    }

    [Fact]
    public void 正規化_null項目與缺必要欄位的項目丟掉_其他欄位照舊()
    {
        var attachment = new AgentAttachment { FileName = "a.txt", StoredPath = @"C:\data\a.txt", Kind = AgentAttachmentKind.Document };
        var file = File();
        file.Conversations = new List<ResumeConversation>
        {
            null!,
            new() { Id = null!, Mode = ResumeMode.Continue },
            new() { Id = " ", Mode = ResumeMode.Continue },
            new()
            {
                Id = "c1", Provider = AgentProvider.Codex, AccountId = null!, Mode = ResumeMode.None, WhyNot = CliNoContinueReason.RiskyTool, RiskOperation = "git push",
                InterruptedTools = null!, PendingRequests = new List<string> { null!, "執行指令：git status" },
            },
            new() { Id = "c2", InterruptedTools = new List<AgentInterruptedTool> { null!, new("執行指令", "dotnet test", TimeSpan.FromSeconds(3)) } },
        };
        file.DeferredSends = new List<string> { null!, "", " ", "c2" };
        file.RemoteControl = new List<string> { null!, " ", "c3" };
        file.Ui = new ResumeUi
        {
            Popouts = new List<ResumePopout> { null!, new(null!, null), new("c1", new[] { 10, 20, 800, 600 }) },
            Drafts = new List<ResumeDraft> { null!, new(null!, "沒有對話"), new("c1", null!), new("c2", "還沒送的字") },
            NewConversations = new List<ResumeNewConversation>
            {
                null!,
                new(null!, "acc", null, "沒有草稿 id", new List<AgentAttachment>()),
                new("d1", null!, null, null!, null!),
                new("d2", "acc", @"C:\work", "第一則", new List<AgentAttachment> { null!, attachment }),
            },
        };

        var result = ResumeIntentRules.Normalize(file);

        result.Token.Should().Be("tok");
        result.From.Should().Be("0.2.1-aaaaaa1");
        result.To.Should().Be("0.2.2-bbbbbb2");
        result.Conversations.Select(c => c.Id).Should().Equal("c1", "c2");
        var c1 = result.Conversations[0];
        c1.Provider.Should().Be(AgentProvider.Codex);
        c1.AccountId.Should().BeEmpty();
        c1.WhyNot.Should().Be(CliNoContinueReason.RiskyTool);
        c1.RiskOperation.Should().Be("git push");
        c1.InterruptedTools.Should().BeEmpty();
        c1.PendingRequests.Should().Equal("執行指令：git status");
        result.Conversations[1].InterruptedTools.Should().ContainSingle().Which.Title.Should().Be("dotnet test");
        result.DeferredSends.Should().Equal("c2");
        result.RemoteControl.Should().Equal("c3");
        result.Ui.Popouts.Should().ContainSingle().Which.ConversationId.Should().Be("c1");
        result.Ui.Drafts.Should().Equal(new ResumeDraft("c1", string.Empty), new ResumeDraft("c2", "還沒送的字"));
        result.Ui.NewConversations.Select(n => n.DraftId).Should().Equal("d1", "d2");
        var d1 = result.Ui.NewConversations[0];
        d1.AccountId.Should().BeEmpty();
        d1.Text.Should().BeEmpty();
        d1.Attachments.Should().BeEmpty();
        result.Ui.NewConversations[1].Folder.Should().Be(@"C:\work");
        result.Ui.NewConversations[1].Attachments.Should().Equal(attachment);
    }

    [Fact]
    public void 讀檔時正規化_合法JSON但含null_不丟例外()
    {
        using var temp = new TempDirectory("resume");
        var path = ResumeFileStore.PathFor(temp.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, """
            {
              "Token": "tok",
              "Conversations": [ null, { "Id": "c1", "Provider": "Claude", "Mode": "Continue", "InterruptedTools": [ null ] } ],
              "DeferredSends": [ null, "c1" ],
              "Ui": { "Drafts": [ null ], "NewConversations": [ { "DraftId": "d1", "AccountId": "acc", "Text": "第一則", "Attachments": null } ] }
            }
            """);

        var file = ResumeFileStore.Load(temp.Root);

        file.Should().NotBeNull();
        file!.Conversations.Should().ContainSingle().Which.InterruptedTools.Should().BeEmpty();
        file.DeferredSends.Should().Equal("c1");
        file.RemoteControl.Should().BeEmpty("合併 main 的 Remote Control 之前的版本寫的檔沒有這個欄位");
        file.Ui.Drafts.Should().BeEmpty();
        file.Ui.NewConversations.Should().ContainSingle().Which.Attachments.Should().BeEmpty();
    }

    [Fact]
    public void 存檔讀檔往返_列舉存成名稱()
    {
        using var temp = new TempDirectory("resume");
        var file = File();
        file.Conversations.Add(new ResumeConversation
        {
            Id = "c1", Provider = AgentProvider.Codex, AccountId = "acc", SessionId = "thread-1", Mode = ResumeMode.None,
            WhyNot = CliNoContinueReason.RiskyTool, StoppedAt = 1792000000000,
            InterruptedTools = { new AgentInterruptedTool("執行指令", "dotnet test", TimeSpan.FromSeconds(4.2)) },
            PendingRequests = { "執行指令：git status" }, BackgroundWorkStopped = true, RiskOperation = "git push", CliVersion = "0.160.1",
        });
        file.DeferredSends.Add("c2");
        file.RemoteControl.Add("c3");
        file.Ui.Drafts.Add(new ResumeDraft("c1", "還沒送的字"));

        ResumeFileStore.Save(temp.Root, file);
        var text = System.IO.File.ReadAllText(ResumeFileStore.PathFor(temp.Root));
        var back = ResumeFileStore.Load(temp.Root);

        text.Should().Contain("\"RiskyTool\"").And.Contain("\"Codex\"").And.Contain("\"None\"");
        back.Should().NotBeNull();
        var entry = back!.Conversations.Single();
        entry.InterruptedTools.Single().Elapsed.Should().Be(TimeSpan.FromSeconds(4.2));
        entry.WhyNot.Should().Be(CliNoContinueReason.RiskyTool);
        back.DeferredSends.Should().Equal("c2");
        back.RemoteControl.Should().Equal("c3");
        back.Ui.Drafts.Single().Text.Should().Be("還沒送的字");

        ResumeFileStore.Delete(temp.Root);
        ResumeFileStore.Load(temp.Root).Should().BeNull();
    }

    [Fact]
    public void 刪檔時被別的程式開著_回false檔案還在_放開後刪得掉()
    {
        using var temp = new TempDirectory("resume");
        ResumeFileStore.Save(temp.Root, File());
        var path = ResumeFileStore.PathFor(temp.Root);

        // 防毒、備份軟體開著檔而且沒給 FILE_SHARE_DELETE：重試幾次仍刪不掉，呼叫端要知道、稍後再刪
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            ResumeFileStore.Delete(temp.Root).Should().BeFalse("留下的檔案會讓下次一般啟動把已接續的對話當成上次中斷");
            System.IO.File.Exists(path).Should().BeTrue();
        }

        ResumeFileStore.Delete(temp.Root).Should().BeTrue();
        ResumeFileStore.Load(temp.Root).Should().BeNull();
        ResumeFileStore.Delete(temp.Root).Should().BeTrue("本來就沒有檔案也算刪掉了");
    }

    [Fact]
    public void 連update資料夾都沒有_刪檔也算成功()
    {
        using var temp = new TempDirectory("resume");
        ResumeFileStore.Delete(temp.Root).Should().BeTrue("不能因為資料夾不存在就讓呼叫端一直重試");
    }

    [Fact]
    public void 草稿殘留_resume讀不到時一個都不刪_讀到時跳過列出的草稿()
    {
        var file = File();
        file.Ui.NewConversations.Add(new ResumeNewConversation("keep", "acc", null, "還沒送出", new List<AgentAttachment>()));
        var leftovers = new[] { "keep", "old" };

        ResumeIntentRules.DraftLeftoversToDelete(leftovers, new StartupResume(file, Unreadable: false)).Should().Equal("old");
        ResumeIntentRules.DraftLeftoversToDelete(leftovers, StartupResume.None).Should().Equal("keep", "old");
        ResumeIntentRules.DraftLeftoversToDelete(leftovers, new StartupResume(null, Unreadable: true))
            .Should().BeEmpty("不知道哪些草稿要留給新版還原：這次一個都不刪，下次啟動再清");
    }

    [Fact]
    public async Task 啟動時resume暫時被鎖住_重試到放開就讀得到()
    {
        using var temp = new TempDirectory("resume");
        ResumeFileStore.Save(temp.Root, File());
        var path = ResumeFileStore.PathFor(temp.Root);

        // 備份、防毒軟體不共享讀取地開著，比 JsonFile 自己的三次重試（約 160 毫秒）久
        var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(600);
            holder.Dispose();
        });
        var startup = ResumeFileStore.LoadAtStartup(temp.Root, TimeSpan.FromSeconds(10));
        await release;

        startup.Unreadable.Should().BeFalse();
        startup.File.Should().NotBeNull("放開之後讀得到：照常接續");
        startup.File!.Token.Should().Be("tok");
    }

    [Fact]
    public void 啟動時resume一直被鎖住_回讀不到_不是沒有()
    {
        using var temp = new TempDirectory("resume");
        ResumeFileStore.Save(temp.Root, File());
        var path = ResumeFileStore.PathFor(temp.Root);

        StartupResume startup;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            startup = ResumeFileStore.LoadAtStartup(temp.Root, TimeSpan.FromMilliseconds(300));

        startup.Unreadable.Should().BeTrue("檔案還在：呼叫端不能把它當成沒有而刪掉或覆寫");
        startup.File.Should().BeNull();
        System.IO.File.Exists(path).Should().BeTrue();
        ResumeFileStore.LoadAtStartup(temp.Root, TimeSpan.Zero).File.Should().NotBeNull("放開後讀得到");
    }

    [Fact]
    public void 啟動時沒有resume_或內容損毀_當成沒有_不等()
    {
        using var temp = new TempDirectory("resume");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        ResumeFileStore.LoadAtStartup(temp.Root, TimeSpan.FromSeconds(10)).Should().Be(StartupResume.None);

        var path = ResumeFileStore.PathFor(temp.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, "{ 壞掉");
        ResumeFileStore.LoadAtStartup(temp.Root, TimeSpan.FromSeconds(10)).Should().Be(StartupResume.None, "損毀的已改名成 .corrupt-*");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "沒有檔案或損毀都不是被占用：不重試");
    }
}
