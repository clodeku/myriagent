using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Sync;
using Myriagent.Tests.Protocol.Remote;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 型錄涵蓋的不變式（WP4.4 計畫步 9；契約:647）：真的處理者都登記、授權者依擁有者指示放行，型錄裡由電腦執行的每個指令以合格的形狀經閘門，
/// 都走到交給處理者這一步；退路 10 個＋這一包的處理者 9 個＋WP4.7 的檔案指令 4 個＋帳戶與 CLI 的處理者 5 個（WP4.8 步 7、8）＋回 unsupported 的 1 個（accountAdd）＝全部。
/// 這個中樞沒有和伺服器連線（沒有傳輸），所以回 unsupported、step 是 handler 的是 accountAdd 加上要傳輸的 fileFetch、addUploaded。
/// 退路的指令由最後登記的記錄處理者接下（不跑 HandleCommand：不碰真的帳戶與 CLI）。步 11 起 answer 由這一包處理；WP4.7 合併後（WP4.7 計畫步 13）檔案指令由它的處理者接下。
/// 帳戶與 CLI 指令經 RemoteAccountRig（WP4.8 D30：假 app-server、假 claude、假 CLI 管理員與安裝視窗、守門；acc-claude 改在暫存資料夾）；
/// 帳戶都已登入：accountLogin 在前置條件就 conflict，accountLogout 在 acc-claude 的暫存資料夾跑假 claude 的登出，cliInstall 開假的安裝視窗，
/// 什麼登入都不起
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class RemoteCommandCoverageTests
{
    private const string QuestionId = "要怎麼彙總？";

    /// <summary>這一包的處理者（步 6～9、11）</summary>
    private static readonly string[] Handled =
        ["send", "voiceMessage", "answer", "approve", "approvePlan", "delete", "setOption", "setAccount", "setFolder", RemoteCommandCatalog.ConversationCreate];

    /// <summary>對話處理者裡要有語音辨識器的（這裡沒掛，回 unsupported）</summary>
    private static readonly string[] NeedingVoice = ["voiceMessage"];

    /// <summary>WP4.7 的檔案指令（AgentHub.RemoteFiles.cs）</summary>
    private static readonly string[] Files = ["addUploaded", "listFiles", "fileSearch", "fileFetch", RemoteCommandCatalog.ListFolders];

    /// <summary>檔案指令裡要和伺服器傳輸的（沒有連線時回 unsupported）</summary>
    private static readonly string[] FilesNeedingTransfer = ["addUploaded", "fileFetch"];

    /// <summary>WP4.8 的帳戶與 CLI 處理者（步 7：遠端登入；步 8：登出、CLI 的安裝與更新）</summary>
    private static readonly string[] AccountHandled = ["accountLogin", "accountLoginDevice", RemoteCommandCatalog.AccountLoginCode, "accountLogout", "cliInstall", "cliUpdate"];

    /// <summary>還沒有處理者的：新增帳戶（WP4.8 的範圍外：網頁走 accountLoginDevice）</summary>
    private static readonly string[] Unsupported = ["accountAdd"];

    /// <summary>遠端執行指令（sync-protocol 14.11，AgentHub.RemoteExec.cs）：這個中樞沒接執行者，處理者回 unsupported</summary>
    private static readonly string[] ExecHandled = [RemoteCommandCatalog.RemoteExec];

    /// <summary>排程頁（S3）：HubHarness 沒掛排程讀取器，處理者回 unsupported</summary>
    private static readonly string[] ScheduleHandled = [RemoteCommandCatalog.ScheduleAction, RemoteCommandCatalog.ScheduleAgent];

    /// <summary>電腦狀態面板的檔案傳輸（AgentHub.DeviceFiles.cs）：範圍沒有磁碟，瀏覽被守門員拒絕；下載與存檔要和伺服器傳輸，回 unsupported</summary>
    private static readonly string[] DeviceTransferHandled = [RemoteCommandCatalog.DeviceBrowse, RemoteCommandCatalog.DeviceFetch, RemoteCommandCatalog.DevicePut,
        RemoteCommandCatalog.DeviceTree, RemoteCommandCatalog.DeviceFetchMany];

    private static readonly string[] DeviceTransferNeedingTransfer = [RemoteCommandCatalog.DeviceFetch, RemoteCommandCatalog.DevicePut, RemoteCommandCatalog.DeviceFetchMany];

    /// <summary>登記在最後：記下交到它手上的指令；退路的指令由它回 done（代替 HandleCommand），其他不接</summary>
    private sealed class FallbackRecorder : IRemoteCommandHandler
    {
        public Recorder<string> Seen { get; } = new();

        public bool TryHandle(RemoteViewContext context, IAgentView view, string cmd, JObject command)
        {
            Seen.Add(cmd);
            if (!AgentHub.RemoteFallbackCommands.Contains(cmd))
                return false;
            context.Complete();
            return true;
        }
    }

    /// <summary>型錄裡這個指令的合格內容（對話、卡片、帳戶都對得上這台）</summary>
    private static JObject ValidCommand(RemoteCommandSpec spec, string conversationId, IReadOnlyDictionary<string, AgentChatItem> cards)
    {
        var command = RemoteSamples.Required(spec);
        if (spec.Route == RemoteRoute.Owner)
            command["conversationId"] = ConversationIds.ToWire(conversationId);
        if (command.ContainsKey("accountId"))
            command["accountId"] = "acc-claude";
        if (cards.TryGetValue(spec.Name, out var card))
        {
            command["itemId"] = card.Id;
            command["requestDigest"] = RequestDigestRules.Compute(card);
        }

        if (spec.Name == "answer")
            command["answers"] = new JObject { [QuestionId] = new JArray("依日期") };
        return command;
    }

    [Fact]
    public async Task 型錄裡電腦執行的指令_退路10加處理者10加檔案5加帳戶6加unsupported1_unsupported的恰好是accountAdd與要傳輸的2個與語音1個()
    {
        using var rig = new RemoteAccountRig(status: AccountStatus.Ready);
        var h = rig.H;
        RemoteEntrySamples.AllowRemote(h, allowFullAccess: true);
        var recorder = new FallbackRecorder();
        h.Ui.Invoke(() =>
        {
            h.Hub.AddRemoteHandlerForTest(recorder);
            // 檔案傳輸：不看這台真的磁碟與權限
            h.Hub.DeviceFilesElevatedForTest = () => false;
            h.Hub.DeviceDriveRootsForTest = () => [];
            h.Hub.DevicePlacesForTest = () => [];
        });
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Raise(s, new AgentApprovalEvent("p-1", "執行指令", "bash", "npm test", "npm test", "t1", CanAllowForSession: true));
        h.Raise(s, new AgentApprovalEvent("plan-1", "確認執行計畫", "plan", "請確認", "1. 改 A", null, CanAllowForSession: false, IsPlan: true));
        h.Raise(s, new AgentQuestionEvent("q-1", new[] { new AgentQuestion { Id = QuestionId, Text = QuestionId } }, AnswerAsMessage: false));
        var items = h.Items(id);
        var cards = new Dictionary<string, AgentChatItem>
        {
            ["approve"] = items.Single(i => i.Kind == AgentItemKind.Approval),
            ["approvePlan"] = items.Single(i => i.Kind == AgentItemKind.PlanApproval),
            ["answer"] = items.Single(i => i.Kind == AgentItemKind.Question),
        };

        var outcomes = new Dictionary<string, RemoteOutcome>(StringComparer.Ordinal);
        // delete 放最後：之後的對話指令就找不到對話了。帳戶指令的等待照 rig 的 60 秒（登出要等假 claude 結束）
        foreach (var spec in RemoteCommandCatalog.All.OrderBy(spec => spec.Name == "delete"))
        {
            var outcome = await rig.RunAsync(ValidCommand(spec, id, cards), tier: spec.BaseTier);
            outcomes[spec.Name] = outcome;
            (outcome.Step == null || outcome.Step == RemoteGateSteps.Handler).Should().BeTrue($"{spec.Name} 要走到交給處理者這一步（{outcome.Code}／{outcome.Step}：{outcome.Message}）");
        }

        var fallback = AgentHub.RemoteFallbackCommands.ToArray();
        outcomes.Keys.Should().BeEquivalentTo(fallback.Concat(Handled).Concat(Files).Concat(AccountHandled).Concat(Unsupported).Concat(ExecHandled).Concat(ScheduleHandled).Concat(DeviceTransferHandled),
            "退路＋處理者＋檔案＋帳戶＋unsupported＋遠端執行指令＋排程頁＝型錄裡電腦執行的全部（契約:647）");
        (fallback.Length, Handled.Length, Files.Length, AccountHandled.Length, Unsupported.Length, ExecHandled.Length, ScheduleHandled.Length, DeviceTransferHandled.Length, outcomes.Count)
            .Should().Be((10, 10, 5, 6, 1, 1, 2, 5, 40));
        outcomes.Where(p => p.Value is { State: RemoteResultStates.Rejected, Code: RemoteResultCodes.Unsupported, Step: RemoteGateSteps.Handler })
            .Select(p => p.Key).Should().BeEquivalentTo(Unsupported.Concat(FilesNeedingTransfer).Concat(NeedingVoice).Concat(ExecHandled).Concat(ScheduleHandled).Concat(DeviceTransferNeedingTransfer),
                "回 unsupported 而且 step 是 handler 的恰好是 accountAdd、要傳輸的 2 個、沒掛辨識器的語音訊息、沒接執行者的遠端執行指令、沒掛排程讀取器的排程頁");
        recorder.Seen.All.Should().BeEquivalentTo(fallback.Concat(Unsupported), "這一包的處理者接下自己的 10 個、WP4.7 接下檔案的 5 個（含新對話的 listFolders）、帳戶與 CLI 的處理者接下 6 個，其他都交給下一個處理者");
        foreach (var name in fallback)
            outcomes[name].State.Should().Be(RemoteResultStates.Done, name);
        foreach (var name in Handled.Except(NeedingVoice).Concat(Files.Except(FilesNeedingTransfer)).Concat(AccountHandled))
            outcomes[name].Code.Should().NotBe(RemoteResultCodes.Unsupported, name);
        outcomes["accountLogout"].State.Should().Be(RemoteResultStates.Done, "帳戶處理者回報真的結果：假 claude 的登出結束碼 0");
        outcomes["cliInstall"].State.Should().Be(RemoteResultStates.Done, "Claude 沒安裝：開始安裝（假的安裝視窗）");

        rig.Fake.Calls.Should().NotContain(c => c.StartsWith("account/login/start", StringComparison.Ordinal), "帳戶都已登入：前置條件就擋下，沒有起任何登入");
        rig.Claude.Commands.Should().NotContain(c => c.StartsWith("auth login", StringComparison.Ordinal));
        rig.Claude.Calls.Should().NotContain("refused-not-temp", "登出在暫存資料夾裡的 acc-claude 跑，不碰真的 ~/.claude");
        await h.WaitUntilAsync(() => rig.InstallersStarted == 1 && !h.Cli.Get(AgentProvider.Claude).Busy, "假的安裝視窗跑完", RemoteAccountRig.Slow);
    }

    [Fact]
    public void sync_protocol的5j列_退路清單與帳戶處理者和程式一致()
    {
        // 合併前審查 P2：5j 列曾經還寫著 WP4.8 之前的退路（帳戶的登出、CLI 的安裝與更新），照文件審查的人會以為它們回假的 done
        var sp = File.ReadAllText(Path.Combine(MyriagentHubExe.RepoRoot(), "docs", "sync-protocol.md"));
        var row = sp.Split('\n').Single(line => line.StartsWith("| 5j |", StringComparison.Ordinal));
        static IReadOnlyList<string> Names(string text) =>
            System.Text.RegularExpressions.Regex.Matches(text, "`([A-Za-z]+)`").Select(m => m.Groups[1].Value).ToList();

        const string fallbackStart = "`AgentHub.RemoteFallbackCommands`：";
        var from = row.IndexOf(fallbackStart, StringComparison.Ordinal);
        from.Should().BeGreaterThan(0, "5j 列列出退路清單");
        var to = row.IndexOf("）以回應檢視", from, StringComparison.Ordinal);
        to.Should().BeGreaterThan(from);
        Names(row[(from + fallbackStart.Length)..to]).Should().BeEquivalentTo(AgentHub.RemoteFallbackCommands, "文件的退路清單＝AgentHub.RemoteFallbackCommands");

        const string accountStart = "WP4.8 登記 ";
        var accountsFrom = row.IndexOf(accountStart, StringComparison.Ordinal);
        accountsFrom.Should().BeGreaterThan(0, "5j 列寫明 WP4.8 登記的處理者");
        var accountsTo = row.IndexOf(" 的處理者", accountsFrom, StringComparison.Ordinal);
        Names(row[(accountsFrom + accountStart.Length)..accountsTo]).Should().BeEquivalentTo(AccountHandled);
        row.Should().Contain("`accountAdd` 維持 `unsupported`");
    }
}
