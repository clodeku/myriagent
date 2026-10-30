using System.Net;
using Myriagent.Remote;
using Myriagent.Security;
using Myriagent.Sync;
using MyriagentHub.Audit;
using MyriagentHub.Auth;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using MyriagentHub.Tests.Sync;
using MyriagentHub.Tests.TestSupport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Security;

/// <summary>
/// 多人協作（docs/plans/2026-10-30-collaborators.md、sync-protocol 14.14）：協作人的帳號、登入與工作階段、擁有者端點一律拒絕、
/// 瀏覽器 hub 只看得到分享的對話、轉給電腦的指令以協作人的身分簽、擁有者經瀏覽器 hub 管理協作人
/// </summary>
public sealed class CollaboratorTests
{
    private const string Password = "collab-pass-1";

    private static readonly CollaboratorActor Test = new(AuditActors.Owner, "test", null);

    /// <summary>協作人能用的端點（Browser 政策）：其他非匿名端點一律擋下</summary>
    private static readonly HashSet<string> BrowserEndpoints =
    [
        "GET /api/v1/auth/me",
        "POST /api/v1/auth/logout",
        "POST /api/v1/auth/password",
        "GET /m/{key}",
        "GET /t/{key}",
        "GET /f/{key}",
        "PUT /api/v1/uploads/{uploadId}",
    ];

    [Fact]
    public async Task 協作人登入_持久cookie_me是協作人_擁有者端點一律拒絕()
    {
        await using var host = await HubTestHost.StartAsync();
        var id = await CreateAsync(host, "ming", "小明");
        using var client = host.CreateClient();
        using (var login = await client.PostAsync("/api/v1/auth/login", new { userName = "  MING ", password = Password }))
        {
            login.StatusCode.Should().Be(HttpStatusCode.OK);
            JObject.Parse(await login.Content.ReadAsStringAsync())["role"]!.Value<string>().Should().Be("collaborator");
            var cookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__Host-myriagenthub=", StringComparison.Ordinal));
            cookie.Should().Contain("expires=", "瀏覽器關掉再開也不用重登（持久 cookie）");
        }

        var me = await client.GetJsonAsync("/api/v1/auth/me");
        me.GetProperty("role").GetString().Should().Be("collaborator");
        me.GetProperty("displayName").GetString().Should().Be("小明");
        me.GetProperty("name").GetString().Should().Be("ming");
        // 伺服器端工作階段：閒置 30 天、絕對 180 天，記下是哪一位
        var session = await host.Services.GetRequiredService<SessionService>().FindAsync(me.GetProperty("session").GetProperty("id").GetString());
        session!.UserId.Should().Be(id);
        (session.AbsoluteExpiresAt - session.CreatedAt).Should().Be(180L * 86_400_000);
        (session.IdleExpiresAt - session.CreatedAt).Should().Be(30L * 86_400_000);

        foreach (var endpoint in Endpoints(host).Where(e => !e.AllowAnonymous && !BrowserEndpoints.Contains(e.Key)))
        {
            var path = "/" + string.Join('/', endpoint.Pattern.Trim('/').Split('/').Select(s => s.StartsWith('{') ? "x" : s));
            foreach (var method in endpoint.Methods)
            {
                using var response = await client.SendAsync(new HttpMethod(method), path, method == "GET" ? null : new { });
                response.StatusCode.Should().BeOneOf([HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized], $"協作人不能用 {method} {path}");
            }
        }
        // 未對應的受保護路徑也一樣（catch-all 是擁有者或裝置）
        using (var unmapped = await client.GetAsync("/api/v1/nope"))
            unmapped.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
        // 媒體端點照收（查不到一律 404）
        using (var media = await client.GetAsync("/m/abc"))
            media.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task 錯的密碼與停用的協作人_一律login_failed_停用時撤銷工作階段()
    {
        await using var host = await HubTestHost.StartAsync();
        var id = await CreateAsync(host, "hua", "小華");
        using var client = host.CreateClient();
        using (var wrong = await client.PostAsync("/api/v1/auth/login", new { userName = "hua", password = "wrong-password" }))
            wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await LoginAsync(client, "hua");

        var directory = host.Services.GetRequiredService<CollaboratorDirectory>();
        (await directory.UpdateAsync(id, null, disabled: true, null, Test, CancellationToken.None)).Ok.Should().BeTrue();
        using (var me = await client.GetAsync("/api/v1/auth/me"))
            me.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "停用時撤銷了他的工作階段");
        using var other = host.CreateClient();
        using (var login = await other.PostAsync("/api/v1/auth/login", new { userName = "hua", password = Password }))
            login.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "停用的不能登入（回應和密碼錯一樣）");

        (await directory.UpdateAsync(id, null, disabled: false, null, Test, CancellationToken.None)).Ok.Should().BeTrue();
        await LoginAsync(other, "hua");
        (await directory.DeleteAsync(id, Test, CancellationToken.None)).Ok.Should().BeTrue();
        using (var me = await other.GetAsync("/api/v1/auth/me"))
            me.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "刪除時撤銷了他的工作階段");
        using var third = host.CreateClient();
        using (var login = await third.PostAsync("/api/v1/auth/login", new { userName = "hua", password = Password }))
            login.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 重設協作人的密碼_舊密碼不能用_他被登出()
    {
        await using var host = await HubTestHost.StartAsync();
        var id = await CreateAsync(host, "ming", "小明");
        using var client = host.CreateClient();
        await LoginAsync(client, "ming");
        var directory = host.Services.GetRequiredService<CollaboratorDirectory>();
        (await directory.UpdateAsync(id, "小明明", null, "new-password-2", Test, CancellationToken.None)).Ok.Should().BeTrue();
        using (var me = await client.GetAsync("/api/v1/auth/me"))
            me.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var other = host.CreateClient();
        using (var old = await other.PostAsync("/api/v1/auth/login", new { userName = "ming", password = Password }))
            old.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using (var fresh = await other.PostAsync("/api/v1/auth/login", new { userName = "ming", password = "new-password-2" }))
            fresh.StatusCode.Should().Be(HttpStatusCode.OK);
        directory.Find(id)!.DisplayName.Should().Be("小明明");
    }

    [Fact]
    public async Task 擁有者改密碼_協作人的工作階段照舊_協作人改自己的密碼只撤銷他自己的()
    {
        await using var host = await HubTestHost.StartAsync();
        await CreateAsync(host, "ming", "小明");
        using var collaborator = host.CreateClient();
        await LoginAsync(collaborator, "ming");
        using var owner = host.CreateClient();
        await OwnerFlows.LoginOwnerAsync(owner);
        using (var changed = await OwnerFlows.ChangePasswordAsync(owner, OwnerAccount.DefaultPassword, "owner-new-pass"))
            changed.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var me = await collaborator.GetAsync("/api/v1/auth/me"))
            me.StatusCode.Should().Be(HttpStatusCode.OK, "擁有者改密碼不撤銷協作人的工作階段");

        using var second = host.CreateClient();
        await LoginAsync(second, "ming");
        using (var changed = await collaborator.PostAsync("/api/v1/auth/password", new { currentPassword = Password, newPassword = "collab-pass-2" }))
            changed.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var me = await collaborator.GetAsync("/api/v1/auth/me"))
            me.StatusCode.Should().Be(HttpStatusCode.OK, "改密碼的這個工作階段留著");
        using (var me = await second.GetAsync("/api/v1/auth/me"))
            me.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "他自己的其他工作階段撤銷");
        using (var ownerMe = await owner.GetAsync("/api/v1/auth/me"))
            ownerMe.StatusCode.Should().Be(HttpStatusCode.OK, "擁有者的不動");
        // 擁有者的工作階段清單不列協作人的
        var sessions = await owner.GetJsonAsync("/api/v1/sessions");
        sessions.GetProperty("sessions").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task 名冊的格式規則_帳號重複_保留名稱_密碼太短()
    {
        await using var host = await HubTestHost.StartAsync();
        var directory = host.Services.GetRequiredService<CollaboratorDirectory>();
        (await directory.CreateAsync("ming", "小明", Password, Test, CancellationToken.None)).Ok.Should().BeTrue();
        (await directory.CreateAsync("MING", "另一個", Password, Test, CancellationToken.None)).Code.Should().Be(CollaboratorCodes.UserNameTaken);
        (await directory.CreateAsync("admin", "冒充", Password, Test, CancellationToken.None)).Code.Should().Be(CollaboratorCodes.Invalid);
        (await directory.CreateAsync("hua", "小華", "short", Test, CancellationToken.None)).Code.Should().Be(CollaboratorCodes.PasswordTooShort);
        (await directory.CreateAsync("hua", "小" + (char)0x202E + "華", Password, Test, CancellationToken.None)).Code.Should().Be(CollaboratorCodes.Invalid);
        (await directory.ShareAsync("not-a-wire-id", [], Test, CancellationToken.None)).Code.Should().Be(CollaboratorCodes.Invalid);
        (await directory.ShareAsync(CommandRouterTests.Conv, ["nobody"], Test, CancellationToken.None)).Code.Should().Be(CollaboratorCodes.Invalid);
        directory.List().Select(c => c.UserName).Should().Equal("ming");
    }

    [Fact]
    public async Task 協作人的瀏覽器hub_init只有分享的對話_擁有者端的指令一律invalid()
    {
        await using var s = await CommandRouterTests.Setup.StartAsync(features: [DeviceFeatureRules.Conv1, DeviceFeatureRules.Cmd1, DeviceFeatureRules.Collab1]);
        var directory = s.Host.Services.GetRequiredService<CollaboratorDirectory>();
        var id = await CreateAsync(s.Host, "ming", "小明");
        await using var collaborator = await ConnectCollaboratorAsync(s.Host, "ming");
        var init = await collaborator.Hub.ReadyAsync();
        init["viewer"]!["role"]!.Value<string>().Should().Be("collaborator");
        init["viewer"]!["name"]!.Value<string>().Should().Be("小明");
        ((JArray)init["accounts"]!).Should().BeEmpty();
        ((JArray)init["conversations"]!).Should().BeEmpty("還沒有分享");
        init["collaborators"].Should().BeNull("協作人看不到協作人的清單");
        init["schedules"]!.Type.Should().Be(JTokenType.Null);

        // 沒分享的對話：open 回 invalid 並送 conversationClosed、send 回 invalid（不轉給電腦）
        (await collaborator.Hub.CmdAsync(new { cmd = "open", conversationId = CommandRouterTests.Conv }, commandId: Ulid()))["error"]!.Value<string>().Should().Be("invalid");
        await collaborator.Hub.WaitForAsync("conversationClosed");
        (await collaborator.Hub.CmdAsync(new { cmd = "send", conversationId = CommandRouterTests.Conv, text = "hi" }, commandId: Ulid()))["error"]!.Value<string>().Should().Be("invalid");
        // 擁有者的指令：一律 invalid
        foreach (var cmd in new object[]
                 {
                     new { cmd = "newConversation", accountId = (string?)null, paneId = "p1" },
                     new { cmd = "deviceRename", deviceId = s.Owner.Id, name = "x" },
                     new { cmd = "collabCreate", userName = "evil", displayName = "壞人", password = Password },
                     new { cmd = "monitor", on = true },
                 })
        {
            (await collaborator.Hub.CmdAsync(cmd, commandId: Ulid()))["error"]!.Value<string>().Should().Be("invalid");
        }
        directory.List().Should().HaveCount(1);

        // 分享之後：清單出現、可以 open、send 以協作人的身分簽、記在電腦收到的信封
        (await directory.ShareAsync(CommandRouterTests.Conv, [id], Test, CancellationToken.None)).Ok.Should().BeTrue();
        var list = await collaborator.Hub.WaitForAsync("conversations", m => ((JArray)m["items"]!).Count == 1);
        list["items"]![0]!["id"]!.Value<string>().Should().Be(CommandRouterTests.Conv);
        (await collaborator.Hub.CmdAsync(new { cmd = "open", conversationId = CommandRouterTests.Conv }, commandId: Ulid()))["accepted"]!.Value<bool>().Should().BeTrue();
        await collaborator.Hub.WaitForAsync("conversation");
        (await collaborator.Hub.CmdAsync(new { cmd = "send", conversationId = CommandRouterTests.Conv, text = "幫我看一下" }, commandId: Ulid()))["accepted"]!.Value<bool>().Should().BeTrue();
        var delivery = await s.Device!.WaitForAsync<CommandDelivery>();
        var verdict = CommandEnvelope.Verify(delivery.Envelope, delivery.CommandId, Target(s, collaborator: true), Now(s));
        verdict.Step.Should().BeNull(verdict.Detail);
        verdict.Body!.Issuer.Should().Be(new CommandIssuer(CommandIssuerKinds.Collaborator, "小明"));
        CommandEnvelope.Verify(delivery.Envelope, delivery.CommandId, Target(s, collaborator: false), Now(s)).Step.Should().Be(CommandEnvelopeSteps.Issuer,
            "沒協商出 collab1 的電腦拒絕協作人發起的信封");
        // 改對話設定的指令在分享的對話裡也一樣不收
        (await collaborator.Hub.CmdAsync(new { cmd = "setOption", conversationId = CommandRouterTests.Conv, key = "model", value = "opus" }, commandId: Ulid()))["error"]!
            .Value<string>().Should().Be("invalid");

        // 取消分享：開著的分頁關掉、清單拿掉
        (await directory.ShareAsync(CommandRouterTests.Conv, [], Test, CancellationToken.None)).Ok.Should().BeTrue();
        await collaborator.Hub.WaitForAsync("conversationClosed", m => m["conversationId"]?.Value<string>() == CommandRouterTests.Conv);
        await collaborator.Hub.WaitForAsync("conversations", m => ((JArray)m["items"]!).Count == 0);
    }

    [Fact]
    public async Task 電腦沒有collab1_協作人的send立即拒絕unsupported()
    {
        await using var s = await CommandRouterTests.Setup.StartAsync();
        var directory = s.Host.Services.GetRequiredService<CollaboratorDirectory>();
        var id = await CreateAsync(s.Host, "ming", "小明");
        (await directory.ShareAsync(CommandRouterTests.Conv, [id], Test, CancellationToken.None)).Ok.Should().BeTrue();
        await using var collaborator = await ConnectCollaboratorAsync(s.Host, "ming");
        await collaborator.Hub.ReadyAsync();
        var bcid = Ulid();
        (await collaborator.Hub.CmdAsync(new { cmd = "send", conversationId = CommandRouterTests.Conv, text = "hi" }, commandId: bcid))["accepted"]!.Value<bool>().Should().BeTrue();
        var result = await collaborator.Hub.WaitForAsync("commandResult", m => m["commandId"]?.Value<string>() == bcid);
        result["state"]!.Value<string>().Should().Be("rejected");
        result["code"]!.Value<string>().Should().Be(RemoteResultCodes.Unsupported);
        var nothing = () => s.Device!.WaitForAsync<CommandDelivery>(timeoutMs: 300);
        await nothing.Should().ThrowAsync<OperationCanceledException>("沒有 collab1 的電腦不送信封");
    }

    [Fact]
    public async Task 擁有者經瀏覽器hub管理協作人_collabResult_推送collaborators()
    {
        await using var s = await CommandRouterTests.Setup.StartAsync(features: [DeviceFeatureRules.Conv1, DeviceFeatureRules.Cmd1, DeviceFeatureRules.Collab1]);
        var hub = s.Browser.Hub;
        (await hub.CmdAsync(new { cmd = "collabCreate", requestId = "r1", userName = "ming", displayName = "小明", password = Password }, commandId: Ulid()))["accepted"]!
            .Value<bool>().Should().BeTrue();
        var created = await hub.WaitForAsync("collabResult", m => m["requestId"]?.Value<string>() == "r1");
        created["ok"]!.Value<bool>().Should().BeTrue();
        var id = created["userId"]!.Value<string>()!;
        var pushed = await hub.WaitForAsync("collaborators", m => m["collaborators"]?["list"] is JArray { Count: 1 });
        pushed["collaborators"]!["list"]![0]!["displayName"]!.Value<string>().Should().Be("小明");

        (await hub.CmdAsync(new { cmd = "collabCreate", requestId = "r2", userName = "ming", displayName = "重複", password = Password }, commandId: Ulid()))["accepted"]!
            .Value<bool>().Should().BeTrue();
        (await hub.WaitForAsync("collabResult", m => m["requestId"]?.Value<string>() == "r2"))["code"]!.Value<string>().Should().Be(CollaboratorCodes.UserNameTaken);

        (await hub.CmdAsync(new { cmd = "collabShare", requestId = "r3", conversationId = CommandRouterTests.Conv, userIds = new[] { id } }, commandId: Ulid()))["accepted"]!
            .Value<bool>().Should().BeTrue();
        (await hub.WaitForAsync("collabResult", m => m["requestId"]?.Value<string>() == "r3"))["ok"]!.Value<bool>().Should().BeTrue();
        await hub.WaitForAsync("collaborators", m => m["collaborators"]?["shares"]?[CommandRouterTests.Conv] is JArray { Count: 1 });

        // 不在伺服器的對話：not_found
        (await hub.CmdAsync(new { cmd = "collabShare", requestId = "r4", conversationId = "ffffffffffffffffffffffffffffffff", userIds = new[] { id } }, commandId: Ulid()))["accepted"]!
            .Value<bool>().Should().BeTrue();
        (await hub.WaitForAsync("collabResult", m => m["requestId"]?.Value<string>() == "r4"))["code"]!.Value<string>().Should().Be(CollaboratorCodes.NotFound);

        // 桌面（collab1）收到 CollabUpdate；經裝置 hub 的 CollabCmd 也能管理
        var update = await s.Device!.WaitForAsync<CollaboratorsState>(m => m.List.Count == 1 && m.Shares.Count == 1);
        update.Shares[CommandRouterTests.Conv].Should().Equal(id);
        var viaDevice = await s.Device.Connection.InvokeAsync<CollabCmdResult>(DeviceHubMethods.CollabCmd, new CollabCmdRequest(CollaboratorCommands.Update, UserId: id, DisplayName: "阿明"));
        viaDevice.Ok.Should().BeTrue();
        await hub.WaitForAsync("collaborators", m => m["collaborators"]?["list"]?[0]?["displayName"]?.Value<string>() == "阿明");
    }

    // ───── 共用 ─────

    private static async Task<string> CreateAsync(HubTestHost host, string userName, string displayName)
    {
        var result = await host.Services.GetRequiredService<CollaboratorDirectory>().CreateAsync(userName, displayName, Password, Test, CancellationToken.None);
        result.Ok.Should().BeTrue(result.Code);
        return result.UserId!;
    }

    private static async Task LoginAsync(HubClient client, string userName)
    {
        using var login = await client.PostAsync("/api/v1/auth/login", new { userName, password = Password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<BrowserOwner> ConnectCollaboratorAsync(HubTestHost host, string userName)
    {
        var http = host.CreateClient();
        http.Http.DefaultRequestHeaders.UserAgent.ParseAdd(CommandRouterTests.ChromeWindows);
        await LoginAsync(http, userName);
        return new BrowserOwner(http, await BrowserHubClient.ConnectAsync(host, http));
    }

    private static CommandEnvelopeTarget Target(CommandRouterTests.Setup s, bool collaborator)
    {
        var options = s.Host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<MyriagentHub.Hosting.HubOptions>>().Value;
        return new CommandEnvelopeTarget(s.Host.Services.GetRequiredService<MyriagentHub.Trust.HubIdentity>().Spki,
            MyriagentHub.Hosting.HostingRules.NormalizeOrigin(MyriagentHub.Hosting.HostingRules.PublicOrigin(options))!, s.Owner.Id, s.Hello!.NonceS,
            AcceptsCollaboratorIssuer: collaborator);
    }

    private static ServerTimeBounds Now(CommandRouterTests.Setup s) => new(s.Host.Clock.NowMs, s.Host.Clock.NowMs);

    private static string Ulid() => SyncIds.NewUlid(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private sealed record EndpointInfo(string Key, string Pattern, IReadOnlyList<string> Methods, bool AllowAnonymous);

    private static List<EndpointInfo> Endpoints(HubTestHost host)
    {
        var list = new List<EndpointInfo>();
        foreach (var endpoint in host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var pattern = endpoint.RoutePattern.RawText ?? string.Empty;
            if (!pattern.StartsWith('/'))
                pattern = "/" + pattern;
            var methods = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods.ToList() ?? ["GET"];
            var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            foreach (var method in methods)
                list.Add(new EndpointInfo(method + " " + pattern, pattern, [method], anonymous));
        }
        // 瀏覽器 hub、裝置 hub 的 negotiate 不在這裡測（hub 有自己的測試），catch-all 另外測
        return list.Where(e => !e.Pattern.Contains("/hubs/", StringComparison.Ordinal) && !e.Pattern.Contains("{**", StringComparison.Ordinal)).ToList();
    }
}
