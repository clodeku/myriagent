using System.Collections.Concurrent;
using Myriagent.Remote;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.TestSupport.BrowserFixtures;
using HubOptions = MyriagentHub.Hosting.HubOptions;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 瀏覽器那一側的第 4 階段接縫（WP4.1 步 12，D50、D51、D52；plan 6.2）：<c>CmdParse.Body</c>、<see cref="BrowserCommandHandler"/> 的
/// 對話處理者（WP4.2）與指令路由（WP4.5）、<see cref="BrowserConnections"/> 的連線事件、空的預設實作。沒有登記處理者（WP4.1）時與第 3 階段完全相同
/// </summary>
public sealed class BrowserSeamTests
{
    private const string Conversation = "0123456789abcdef0123456789abcdef";

    // ───── CmdParse.Body ─────

    [Theory]
    [InlineData("send", BrowserErrorCodes.NotAvailable)]
    [InlineData("newConversation", BrowserErrorCodes.NotAvailable)]
    [InlineData("syncPause", BrowserErrorCodes.DesktopOnly)]
    [InlineData("syncAnything", BrowserErrorCodes.DesktopOnly)]
    [InlineData("open", null)]
    [InlineData("ready", null)]
    public void 認得的名稱帶原始的cmd物件_Error與Command不變(string name, string? error)
    {
        var cmd = new JObject { ["cmd"] = name, ["conversationId"] = Conversation, ["extra"] = 1 };
        if (name == "ready")
            cmd = new JObject { ["cmd"] = "ready", ["mode"] = "main" };
        var parsed = MessageGuard.ParseCmd(new JObject { ["commandId"] = "01J9Q0000000000000000000AA", ["cmd"] = cmd });

        parsed.Body.Should().BeSameAs(cmd);
        parsed.Error.Should().Be(error);
        parsed.CommandId.Should().Be("01J9Q0000000000000000000AA");
        if (error is null)
            parsed.Command.Should().NotBeNull();
        else
            parsed.Command.Should().BeNull("Error 不是 null 時 Command 是 null（照舊）");
    }

    [Fact]
    public void 伺服器指令格式不對也帶Body_不認得的名稱與頂層不合格不帶()
    {
        var bad = new JObject { ["cmd"] = "setting", ["key"] = "instructions", ["value"] = "x" };
        var parsed = MessageGuard.ParseCmd(new JObject { ["cmd"] = bad });
        parsed.Error.Should().Be(BrowserErrorCodes.DesktopOnly);
        parsed.DeniedSettingKey.Should().Be("instructions");
        parsed.Body.Should().BeSameAs(bad);

        MessageGuard.ParseCmd(new JObject { ["cmd"] = new JObject { ["cmd"] = "Send" } }).Body.Should().BeNull("不認得的名稱（大小寫不同）");
        MessageGuard.ParseCmd(new JObject { ["cmd"] = new JObject { ["cmd"] = "send" }, ["what"] = 1 }).Body.Should().BeNull("頂層不合格");
        MessageGuard.ParseCmd(new JObject { ["cmd"] = new JObject { ["cmd"] = "send" }, ["envelope"] = "x" }).Body.Should().BeNull("帶 envelope 照舊 not_available、不解析 cmd");
    }

    // ───── BrowserCommandHandler 的接縫 ─────

    [Fact]
    public async Task 沒有登記處理者_預設的對話目錄與blob儲存是空的_指令照舊()
    {
        // WP4.2 起正式登記 ConversationDirectoryCache 與對話處理者、WP4.5 起正式登記指令路由（沒有開關，答 1）：以 services 換回 AddHubSync 的空目錄、
        // 拿掉對話處理者與指令路由，保留接縫預設值的證明（F51）；WP4.7 起正式登記 BlobStore，同樣換回空的 blob 儲存
        await using var host = await HubTestHost.StartAsync(services: s => s
            .AddSingleton<IConversationDirectory, EmptyConversationDirectory>()
            .AddSingleton<IBlobStore, EmptyBlobStore>()
            .RemoveAll<IBrowserConversationCommands>()
            .RemoveAll<IBrowserCommandRouter>());
        host.Services.GetService<IBrowserConversationCommands>().Should().BeNull("WP4.1 不登記");
        host.Services.GetService<IBrowserCommandRouter>().Should().BeNull("WP4.1 不登記");
        host.Services.GetRequiredService<IConversationDirectory>().Should().BeOfType<EmptyConversationDirectory>();
        host.Services.GetRequiredService<IConversationDirectory>().Find(Conversation).Should().BeNull();
        var blobs = host.Services.GetRequiredService<IBlobStore>();
        blobs.Should().BeOfType<EmptyBlobStore>();
        blobs.Exists(new string('0', 64)).Should().BeFalse();
        blobs.OpenRead(new string('0', 64)).Should().BeNull();
        await FluentActions.Awaiting(() => blobs.PutAsync(new string('0', 64), 0, "upload", null, Stream.Null, CancellationToken.None)).Should().ThrowAsync<NotSupportedException>();

        await using var owner = await LoginAndConnectAsync(host);
        foreach (var name in new[] { "send", "newConversation", "accountRefresh", "takeover" })
            (await owner.Hub.CmdAsync(new { cmd = name, conversationId = Conversation }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.NotAvailable, name);
        (await owner.Hub.CmdAsync(new { cmd = "open", conversationId = Conversation }))["accepted"]!.Value<bool>().Should().BeTrue("接受不處理（照舊）");
    }

    [Fact]
    public async Task 正式登記的對話目錄是ConversationDirectoryCache()
    {
        await using var host = await HubTestHost.StartAsync();
        var directory = host.Services.GetRequiredService<IConversationDirectory>();
        directory.Should().BeOfType<ConversationDirectoryCache>().And.BeSameAs(host.Services.GetRequiredService<ConversationDirectoryCache>(), "同一個實例");
        directory.Find(Conversation).Should().BeNull("沒有任何對話");
        host.Services.GetServices<IConversationDirectory>().Should().HaveCount(2, "AddHubSync 的空目錄仍在，後登記的為準");
    }

    [Fact]
    public async Task 正式登記時_open不存在的對話回invalid()
    {
        await using var host = await HubTestHost.StartAsync();
        host.Services.GetRequiredService<IBrowserConversationCommands>().Should().BeOfType<BrowserConversationCommands>();
        await using var owner = await LoginAndConnectAsync(host);
        await owner.Hub.ReadyAsync();

        (await owner.Hub.CmdAsync(new { cmd = "open", conversationId = Conversation }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.Invalid);
        (await owner.Hub.WaitForAsync("conversationClosed"))["conversationId"]!.Value<string>().Should().Be(Conversation);
        // 不是對話 id 形狀的（第 3 階段網頁的本機 id、伺服器草稿）照舊接受不處理；網頁自動送的不提示
        (await owner.Hub.CmdAsync(new { cmd = "open", conversationId = "c1" }))["accepted"]!.Value<bool>().Should().BeTrue();
        await owner.Hub.AssertNoMessageAsync("toast", 200);
    }

    [Fact]
    public async Task 正式登記時_newConversation回草稿()
    {
        // 沒有開關（答 1）：合併部署後 newConversation 就由伺服器回草稿（WP4.2 步 13）；沒有帳戶時照舊 not_available
        await using var host = await HubTestHost.StartAsync();
        host.Services.GetRequiredService<IBrowserDrafts>().Should().BeSameAs(host.Services.GetRequiredService<BrowserDrafts>());
        await using (var none = await LoginAndConnectAsync(host))
        {
            await none.Hub.ReadyAsync();
            (await none.Hub.CmdAsync(new { cmd = "newConversation" }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.NotAvailable, "一個帳戶都沒有");
        }

        var deviceId = await MyriagentHub.Tests.Sync.EventLogTests.AddDeviceAsync(host);
        (await MyriagentHub.Tests.Sync.EventLogTests.AppendAsync(host, deviceId, MyriagentHub.Tests.Sync.EventLogTests.Batch(MyriagentHub.Tests.Sync.EventLogTests.Ev(1,
            Myriagent.Sync.SyncEventKinds.LinkUpsert, MyriagentHub.Tests.Sync.EventLogTests.Link("acct-1", "claude:org1:a@example.com"))))).Ack.Results.Single().Rejected.Should().BeNull();
        var account = MyriagentHub.Tests.Sync.EventLogTests.LatestAccount(host).LogicalAccountId;
        await using var owner = await LoginAndConnectAsync(host);
        await owner.Hub.ReadyAsync();
        // 帳戶取自閘道的帳戶快照：帳戶事件之後閘道依節奏重讀（init 可能還是舊的那一份）
        await WaitUntilAsync(() => host.Services.GetRequiredService<BrowserGateway>().DraftAccounts.Any(a => a.Id == account), because: "閘道讀到新的帳戶");

        (await owner.Hub.CmdAsync(new { cmd = "newConversation", accountId = (string?)null, paneId = "p1" }))["accepted"]!.Value<bool>().Should().BeTrue();
        var draft = await owner.Hub.WaitForAsync("conversation");
        var id = draft["conversationId"]!.Value<string>();
        BrowserDraftRules.IsDraftId(id).Should().BeTrue(id);
        draft["paneId"]!.Value<string>().Should().Be("p1");
        draft["header"]!["isDraft"]!.Value<bool>().Should().BeTrue();
        draft["header"]!["accountId"]!.Value<string>().Should().Be(account);
        draft["header"]!["provider"]!.Value<string>().Should().Be("Claude");

        (await owner.Hub.CmdAsync(new { cmd = "open", conversationId = id }))["accepted"]!.Value<bool>().Should().BeTrue();
        (await owner.Hub.WaitForAsync("conversation"))["conversationId"]!.Value<string>().Should().Be(id);
        (await owner.Hub.CmdAsync(new { cmd = "close", conversationId = id }))["accepted"]!.Value<bool>().Should().BeTrue();
        (await owner.Hub.WaitForAsync("conversationClosed"))["conversationId"]!.Value<string>().Should().Be(id);
        (await owner.Hub.CmdAsync(new { cmd = "open", conversationId = id }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.Invalid);
        await owner.Hub.AssertNoMessageAsync("toast", 200);
    }

    [Fact]
    public async Task 正式登記時_router存在_send與檔案指令轉送_takeover仍是not_available()
    {
        // 沒有開關（答 1）：合併部署後 send 就交給指令路由（WP4.5 步 6）；WP4.7 合併後（WP4.7 計畫步 13）檔案指令也轉送，只剩第 5 階段的照舊 not_available
        await using var host = await HubTestHost.StartAsync();
        host.Services.GetRequiredService<IBrowserCommandRouter>().Should().BeOfType<CommandRouter>()
            .And.BeSameAs(host.Services.GetRequiredService<CommandRouter>());
        host.Services.GetServices<IBrowserCommandRouter>().Should().ContainSingle("TryAdd：只登記一份");
        await using var owner = await LoginAndConnectAsync(host);
        await owner.Hub.ReadyAsync();

        // 不存在的線上對話：路由判斷的 conflict（第 3 階段是 not_available），另送 conversationClosed
        var sent = await owner.Hub.CmdAsync(new { cmd = "send", conversationId = Conversation, text = "hi" }, commandId: "01J9Q0000000000000000000AE");
        sent["error"]!.Value<string>().Should().Be(BrowserErrorCodes.Conflict);
        sent["commandId"]!.Value<string>().Should().Be("01J9Q0000000000000000000AE");
        (await owner.Hub.WaitForAsync("conversationClosed"))["conversationId"]!.Value<string>().Should().Be(Conversation);
        (await owner.Hub.WaitForAsync("toast"))["text"]!.Value<string>().Should().Be(BrowserCommandRules.ConversationGoneToast);

        // 檔案指令：不存在的線上對話一樣是路由判斷的 conflict；listFiles 是自動送出的，不提示
        var files = await owner.Hub.CmdAsync(new { cmd = "listFiles", conversationId = Conversation, requestId = "r1" }, commandId: "01J9Q0000000000000000000AG");
        files["error"]!.Value<string>().Should().Be(BrowserErrorCodes.Conflict);
        (await owner.Hub.WaitForAsync("conversationClosed"))["conversationId"]!.Value<string>().Should().Be(Conversation);

        var deferred = await owner.Hub.CmdAsync(new { cmd = "takeover", conversationId = Conversation }, commandId: "01J9Q0000000000000000000AF");
        deferred["error"]!.Value<string>().Should().Be(BrowserErrorCodes.NotAvailable);
        (await owner.Hub.WaitForAsync("toast"))["text"]!.Value<string>().Should().Be("網頁版還不能做這件事", "takeover 的提示；前面的 listFiles 沒有提示");
        await owner.Hub.AssertNoMessageAsync("toast", 200);
    }

    [Fact]
    public async Task 對話處理者接手它認得的not_available與接受不處理的指令_回的錯誤照樣送toast_例外回unavailable()
    {
        var conversations = new FakeConversationCommands("newConversation", "open", "copyItem", "setting", "syncPause");
        await using var host = await HubTestHost.StartAsync(services: s => s.AddSingleton<IBrowserConversationCommands>(conversations));
        await using var owner = await LoginAndConnectAsync(host);
        await owner.Hub.ReadyAsync();

        var accepted = await owner.Hub.CmdAsync(new { cmd = "newConversation", accountId = "x" }, commandId: "01J9Q0000000000000000000AB");
        accepted["accepted"]!.Value<bool>().Should().BeTrue();
        accepted["commandId"]!.Value<string>().Should().Be("01J9Q0000000000000000000AB");
        (await owner.Hub.CmdAsync(new { cmd = "open", conversationId = Conversation }))["accepted"]!.Value<bool>().Should().BeTrue();
        conversations.Calls.Select(c => c.Cmd).Should().Equal("newConversation", "open");
        conversations.Calls[0].Body["accountId"]!.Value<string>().Should().Be("x", "原始的 cmd 物件");

        conversations.Error = BrowserErrorCodes.Invalid;
        var rejected = await owner.Hub.CmdAsync(new { cmd = "copyItem", conversationId = Conversation, itemId = "i1" });
        rejected["error"]!.Value<string>().Should().Be(BrowserErrorCodes.Invalid);
        conversations.Error = null;
        conversations.Throw = true;
        (await owner.Hub.CmdAsync(new { cmd = "newConversation" }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.Unavailable);
        conversations.Throw = false;

        // 伺服器處理的、桌面專屬的不交給它（就算它說認得）；它不認得的照舊 not_available
        (await owner.Hub.CmdAsync(new { cmd = "setting", key = "theme", value = "light" }))["accepted"]!.Value<bool>().Should().BeTrue();
        (await owner.Hub.CmdAsync(new { cmd = "syncPause" }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.DesktopOnly);
        // WP4.5 起 send 交給指令路由、WP4.7 起檔案指令也是；第 5 階段的 takeover 仍是 not_available
        (await owner.Hub.CmdAsync(new { cmd = "takeover", conversationId = Conversation }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.NotAvailable);
        conversations.Calls.Select(c => c.Cmd).Should().Equal("newConversation", "open", "copyItem", "newConversation");
    }

    [Fact]
    public async Task 指令路由只接手型錄裡由電腦執行的not_available指令_結果原樣回給瀏覽器()
    {
        var router = new FakeRouter();
        // 只看路由：拿掉 WP4.2 正式登記的對話處理者（它會接手 open、copyItem，F51 的作法）
        await using var host = await HubTestHost.StartAsync(services: s => s.AddSingleton<IBrowserCommandRouter>(router).RemoveAll<IBrowserConversationCommands>());
        await using var owner = await LoginAndConnectAsync(host);
        await owner.Hub.ReadyAsync();

        var sent = await owner.Hub.CmdAsync(new { cmd = "send", conversationId = Conversation, text = "hi" }, commandId: "01J9Q0000000000000000000AC");
        sent["accepted"]!.Value<bool>().Should().BeTrue();
        sent["commandId"]!.Value<string>().Should().Be("01J9Q0000000000000000000AC");
        (await owner.Hub.CmdAsync(new { cmd = "accountRefresh", accountId = "a1" }))["accepted"]!.Value<bool>().Should().BeTrue();
        router.Calls.Select(c => (c.Spec.Name, c.Spec.Route)).Should().Equal(("send", RemoteRoute.Owner), ("accountRefresh", RemoteRoute.Device));
        router.Calls[0].Body["text"]!.Value<string>().Should().Be("hi");
        router.Calls[0].Parsed.Error.Should().Be(BrowserErrorCodes.NotAvailable);

        router.Result = id => new CmdResult(false, id, "offline");
        (await owner.Hub.CmdAsync(new { cmd = "stop", conversationId = Conversation }))["error"]!.Value<string>().Should().Be("offline");
        router.Result = _ => throw new InvalidOperationException("壞的路由");
        (await owner.Hub.CmdAsync(new { cmd = "stop", conversationId = Conversation }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.Unavailable);

        // 伺服器自己處理的（newConversation、copyItem）、第 5 階段的（takeover）、桌面專屬、接受不處理的都不交給路由
        foreach (var name in new[] { "newConversation", "copyItem", "takeover", "handoffCancel" })
            (await owner.Hub.CmdAsync(new { cmd = name, conversationId = Conversation }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.NotAvailable, name);
        (await owner.Hub.CmdAsync(new { cmd = "syncPause" }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.DesktopOnly);
        (await owner.Hub.CmdAsync(new { cmd = "open", conversationId = Conversation }))["accepted"]!.Value<bool>().Should().BeTrue();
        router.Calls.Should().HaveCount(4);
    }

    [Fact]
    public async Task 對話處理者先於路由()
    {
        var conversations = new FakeConversationCommands("send");
        var router = new FakeRouter();
        await using var host = await HubTestHost.StartAsync(services: s => s
            .AddSingleton<IBrowserConversationCommands>(conversations)
            .AddSingleton<IBrowserCommandRouter>(router));
        await using var owner = await LoginAndConnectAsync(host);

        (await owner.Hub.CmdAsync(new { cmd = "send", conversationId = Conversation, text = "hi" }))["accepted"]!.Value<bool>().Should().BeTrue();
        (await owner.Hub.CmdAsync(new { cmd = "stop", conversationId = Conversation }))["accepted"]!.Value<bool>().Should().BeTrue();
        conversations.Calls.Select(c => c.Cmd).Should().Equal("send");
        router.Calls.Select(c => c.Spec.Name).Should().Equal("stop");
    }

    // ───── BrowserConnections 的連線事件 ─────

    [Fact]
    public async Task ClientState換了觸發StateChanged_移除觸發Removed_處理者丟例外不影響其他與呼叫端()
    {
        var connections = new BrowserConnections(Microsoft.Extensions.Options.Options.Create(new HubOptions()), NullLogger<BrowserConnections>.Instance);
        var changed = new List<(string Connection, BrowserClientState? State)>();
        var removed = new List<string>();
        connections.StateChanged += _ => throw new InvalidOperationException("壞的處理者");
        connections.StateChanged += c => changed.Add((c.ConnectionId, c.State));
        connections.Removed += _ => throw new InvalidOperationException("壞的處理者");
        connections.Removed += c => removed.Add(c.ConnectionId);

        var connection = connections.TryAdd("b1", "s1", null, () => { }, (_, _) => Task.CompletedTask)!;
        var state = new BrowserClientState(true, true, [Conversation], 480);
        connection.SetState(state);
        changed.Should().Equal(("b1", state));
        connection.State.Should().Be(state);

        connections.Remove("b1").Should().BeSameAs(connection);
        connections.Remove("b1").Should().BeNull();
        removed.Should().Equal("b1");

        // 登記之後工作階段已失效：移除並中斷，也觸發 Removed
        var (outcome, _) = await connections.AdmitAsync("b2", "s1", null, () => { }, (_, _) => Task.CompletedTask, _ => Task.FromResult(false), CancellationToken.None);
        outcome.Should().Be(AdmitOutcome.SessionInvalid);
        removed.Should().Equal("b1", "b2");
    }

    [Fact]
    public async Task 登記成功觸發Added_被拒的不觸發_處理者丟例外不影響呼叫端()
    {
        // WP4.2 步 13：伺服器草稿以 Added 知道工作階段在寬限內重新連上
        var connections = new BrowserConnections(Microsoft.Extensions.Options.Options.Create(new HubOptions()), NullLogger<BrowserConnections>.Instance);
        var added = new List<string>();
        var removed = new List<string>();
        connections.Added += _ => throw new InvalidOperationException("壞的處理者");
        connections.Added += c => added.Add(c.ConnectionId + "@" + c.SessionId + (connections.Get(c.ConnectionId) is null ? "（還沒登記）" : ""));
        connections.Removed += c => removed.Add(c.ConnectionId);

        connections.TryAdd("b1", "s1", null, () => { }, (_, _) => Task.CompletedTask).Should().NotBeNull();
        connections.TryAdd("b1", "s1", null, () => { }, (_, _) => Task.CompletedTask).Should().BeNull("同一個連線 id");
        added.Should().Equal(["b1@s1"], "登記之後才觸發；被拒的不觸發");

        var (outcome, _) = await connections.AdmitAsync("b2", "s2", null, () => { }, (_, _) => Task.CompletedTask, _ => Task.FromResult(false), CancellationToken.None);
        outcome.Should().Be(AdmitOutcome.SessionInvalid);
        added.Should().Equal("b1@s1", "b2@s2");
        removed.Should().Equal(["b2"], "登記後才查工作階段：已失效的接著觸發 Removed");

        connections.Shutdown();
        connections.TryAdd("b3", "s1", null, () => { }, (_, _) => Task.CompletedTask).Should().BeNull();
        added.Should().HaveCount(2, "停機之後不登記、不觸發");
    }

    [Fact]
    public async Task 經瀏覽器hub的ClientState觸發StateChanged_斷線觸發Removed()
    {
        await using var host = await HubTestHost.StartAsync();
        var connections = host.Services.GetRequiredService<BrowserConnections>();
        var changed = new TaskCompletionSource<BrowserConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var removed = new TaskCompletionSource<BrowserConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        connections.StateChanged += c => changed.TrySetResult(c);
        connections.Removed += c => removed.TrySetResult(c);

        var owner = await LoginAndConnectAsync(host);
        await owner.Hub.Connection.InvokeAsync("ClientState", JObject.FromObject(new { visible = true, focused = false, openConversationIds = new[] { Conversation }, tzOffsetMinutes = 480 }));
        var connection = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        connection.State!.OpenConversationIds.Should().Equal(Conversation);
        await owner.DisposeAsync();
        (await removed.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeSameAs(connection);
    }

    private sealed class FakeConversationCommands(params string[] handles) : IBrowserConversationCommands
    {
        private readonly ConcurrentQueue<(string Cmd, JObject Body)> calls = new();

        public string? Error { get; set; }

        public bool Throw { get; set; }

        public List<(string Cmd, JObject Body)> Calls => calls.ToList();

        public bool Handles(string cmd) => handles.Contains(cmd);

        public Task<string?> HandleAsync(BrowserConnection connection, string cmd, JObject body, CancellationToken cancellationToken)
        {
            calls.Enqueue((cmd, body));
            if (Throw)
                throw new InvalidOperationException("壞的對話處理者");
            return Task.FromResult(Error);
        }
    }

    private sealed class FakeRouter : IBrowserCommandRouter
    {
        private readonly ConcurrentQueue<(CmdParse Parsed, RemoteCommandSpec Spec, JObject Body)> calls = new();

        public Func<string?, CmdResult> Result { get; set; } = id => new CmdResult(true, id);

        public List<(CmdParse Parsed, RemoteCommandSpec Spec, JObject Body)> Calls => calls.ToList();

        public Task<CmdResult> RouteAsync(BrowserConnection connection, CmdParse parsed, RemoteCommandSpec spec, JObject body, CancellationToken cancellationToken)
        {
            calls.Enqueue((parsed, spec, body));
            return Task.FromResult(Result(parsed.CommandId));
        }
    }
}
