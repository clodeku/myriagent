using System.Reflection;
using Myriagent.Accounts;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.UI;

namespace Myriagent.Tests.Sync.Isolation;

/// <summary>
/// S6 第一道防線（D6、D7）：未配對的程式路徑（從這些根型別出發的可達閉包）不得參考 SignalR、SQLite、ASP.NET Core、Microsoft.Extensions。
/// 之後的步驟把新的根型別加進 <see cref="UnpairedRoots"/>（第 6 步的配對元件、第 11 步的鏡像與橋接、第 12 步的協調者、第 13 步的系統匣與探針報告）。
/// </summary>
public sealed class IsolationScanTests
{
    private static readonly string DesktopAssembly = typeof(AgentHub).Assembly.Location;

    private static readonly string TestAssembly = typeof(IsolationScanTests).Assembly.Location;

    /// <summary>未配對時會執行到的根型別</summary>
    private static readonly IReadOnlyList<string> UnpairedRoots =
    [
        typeof(AppController).FullName!,
        typeof(MainForm).FullName!,
        typeof(AgentWebView).FullName!,
        typeof(AgentHub).FullName!,
        typeof(AccountManager).FullName!,
        typeof(AppSettings).FullName!,
        typeof(ProbeRunner).FullName!,
        "Myriagent.Program",
        // 第 6 步：配對（未配對時執行；只用 HttpClient、CNG、DPAPI）
        typeof(PairingClient).FullName!,
        typeof(PairingSession).FullName!,
        typeof(DeviceKey).FullName!,
        typeof(DeviceFile).FullName!,
        typeof(PairingRecovery).FullName!,
        typeof(PairingCommitResult).FullName!,
        // 第 8 步：協調者在未配對的路徑上也看得到的型別（載入邊界的簽章、建立參數、引擎與宿主的介面）
        typeof(SyncEngineLoader).FullName!,
        typeof(SyncEngineCreateArgs).FullName!,
        typeof(ISyncEngine).FullName!,
        typeof(ISyncHostPort).FullName!,
        typeof(ISyncUiApplier).FullName!,
        // 第 11 步：中樞的鏡像、UI→引擎的橋、用量查詢閘門（協調者在 UI 執行緒持有；只碰引擎的介面）
        typeof(HubBridge).FullName!,
        typeof(SyncMirrorView).FullName!,
        typeof(UsagePollGate).FullName!,
        // 第 12 步：同步協調者（未配對時也建立：配對指令、用量閘門、中樞的掛鉤）
        typeof(SyncCoordinator).FullName!,
        typeof(SyncCoordinatorOptions).FullName!,
        typeof(SyncCoordinatorRules).FullName!,
        typeof(ISyncBridge).FullName!,
        // 第 13 步：延後啟動、系統匣的同步項目、探針報告的同步段
        typeof(SyncStartupRules).FullName!,
        typeof(TraySyncRules).FullName!,
        typeof(TraySyncItem).FullName!,
        typeof(ProbeSyncReport).FullName!,
        // WP4.1 步 9：中樞的遠端閘門與對話變化的掛鉤（未配對時也編進中樞；WP4.3／WP4.4 的實作掛上後同樣不得碰 SignalR／SQLite）
        typeof(IConversationChangeSink).FullName!,
        typeof(ConversationHookInfo).FullName!,
        typeof(RemoteViewContext).FullName!,
        typeof(RemoteOutcome).FullName!,
        typeof(IRemoteCommandAuthorizer).FullName!,
        typeof(DenyAllRemoteAuthorizer).FullName!,
        typeof(IRemoteCommandHandler).FullName!,
        // WP4.1 步 10：SyncStatus 的第 4 階段選用欄位（協調者組 sync 訊息時用；之後的工作包只經這裡擴充凍結的 SyncViewRules）
        typeof(SyncStatusExtras).FullName!,
        typeof(SyncBackfillProgress).FullName!,
        // WP4.3 步 7：UI 端的對話發布者與盒子、中樞的快照來源（協調者在未配對的路徑上也看得到；中樞實作快照來源）
        typeof(ConversationPublisher).FullName!,
        typeof(ConversationChangeBox).FullName!,
        typeof(ConversationChange).FullName!,
        typeof(ConversationWake).FullName!,
        typeof(IConversationEngine).FullName!,
        typeof(IConversationUiPort).FullName!,
        typeof(IConversationPublishSource).FullName!,
        typeof(PublishMetaSnapshot).FullName!,
        typeof(PublishItemsPage).FullName!,
        typeof(PublishQueueSnapshot).FullName!,
        typeof(PublishNoticeKind).FullName!,
        typeof(ChatItemCopyRules).FullName!,
        typeof(PublishPageRules).FullName!,
        typeof(SecretAnswerScrubRules).FullName!,
        typeof(ConversationPolicyCommandRules).FullName!,
        typeof(PublishNoticeRules).FullName!,
        typeof(PublishTimingRules).FullName!,
        // WP4.4 步 5：協調者的遠端指令入口與引擎的指令介面、指令的純規則、放行的授權者（協調者與中樞看得到，未配對時也編進來）
        typeof(IRemoteCommandPort).FullName!,
        typeof(RemoteCommandRun).FullName!,
        typeof(ISyncEngineCommands).FullName!,
        typeof(RemoteAuditEntry).FullName!,
        typeof(SyncCommandRules).FullName!,
        typeof(OwnerTrustedRemoteAuthorizer).FullName!,
        // WP4.7 步 11：中樞的遠端檔案指令（未配對時也編進中樞；傳輸只經介面，實作在同步引擎那一側）
        typeof(IRemoteFileTransfer).FullName!,
        typeof(IRemoteFileTransferSource).FullName!,
        typeof(RemoteUpload).FullName!,
        typeof(Myriagent.Files.RemoteFileIndex).FullName!,
        typeof(Myriagent.Files.RemoteListRules).FullName!,
        typeof(Myriagent.Files.UploadNameRules).FullName!,
        typeof(RemoteScopeBuilder).FullName!,
        // WP4.7 步 12：複本的媒體網址與 AgentWebView 的 /m/、/t/（來源只經介面，E9 的實作在同步引擎那一側）
        typeof(IReplicaMediaSource).FullName!,
        typeof(IReplicaMediaSourceProvider).FullName!,
        typeof(ReplicaMediaReply).FullName!,
        typeof(ReplicaMediaUrls).FullName!,
        typeof(ReplicaMediaRules).FullName!,
        typeof(Myriagent.UI.ReplicaMediaRouteRules).FullName!,
    ];

    [Fact]
    public void 未配對路徑_不參考禁止的組件()
    {
        var result = AssemblyIsolationScanner.Scan(DesktopAssembly, UnpairedRoots);

        result.Findings.Should().BeEmpty("未配對的路徑只能經 SyncEngineLoader 的載入邊界碰到 SignalR／SQLite（S6）");
        // 確認掃描真的走過整個閉包，不是空轉
        result.ScannedTypes.Should().Contain(typeof(AgentHub).FullName!).And.Contain(typeof(AccountManager).FullName!);
        result.ScannedTypes.Should().Contain(typeof(Myriagent.Cli.CliManager).FullName!, "AppController 建立 CliManager");
        result.ScannedMethods.Should().BeGreaterThan(500);
        result.ScannedTypes.Should().Contain(typeof(PairingSession).FullName!).And.Contain(typeof(PairingFlowRules).FullName!, "配對的閉包也走過");
    }

    [Fact]
    public void 掃描器_抓得到欄位與方法參考()
    {
        var findings = AssemblyIsolationScanner.FindForbidden(TestAssembly, [typeof(IsolationControlField).FullName!]);

        findings.Should().Contain(f => f.Contains(".connection [field]", StringComparison.Ordinal) && f.Contains("Microsoft.AspNetCore.SignalR.Client", StringComparison.Ordinal),
            "HubConnection 型別的欄位");
        findings.Should().Contain(f => f.Contains(".Describe [il]", StringComparison.Ordinal) && f.Contains("HubConnection", StringComparison.Ordinal),
            "IL 裡對 HubConnection 成員的參考");
    }

    [Fact]
    public void 掃描器_抓得到catch型別與區域變數()
    {
        var findings = AssemblyIsolationScanner.FindForbidden(TestAssembly, [typeof(IsolationControlCatchLocal).FullName!]);

        findings.Should().Contain(f => f.Contains(".Open [catch]", StringComparison.Ordinal) && f.Contains("Microsoft.Data.Sqlite.SqliteException", StringComparison.Ordinal));
        findings.Should().Contain(f => f.Contains(".Open [local]", StringComparison.Ordinal) && f.Contains("Microsoft.Data.Sqlite.SqliteConnection", StringComparison.Ordinal));
    }

    [Fact]
    public void 掃描器_邊界方法本體不算_沒標示的同樣本體算()
    {
        AssemblyIsolationScanner.FindForbidden(TestAssembly, [typeof(IsolationControlMarked).FullName!])
            .Should().BeEmpty("[SyncLoadBoundary] 的方法只看簽章");
        AssemblyIsolationScanner.FindForbidden(TestAssembly, [typeof(IsolationControlUnmarked).FullName!])
            .Should().Contain(f => f.Contains(".Describe [il]", StringComparison.Ordinal) && f.Contains("Microsoft.AspNetCore.SignalR.Client.HubConnectionBuilder", StringComparison.Ordinal));
    }

    [Fact]
    public void 載入邊界_都是NoInlining而且不是async()
    {
        var boundaries = AssemblyIsolationScanner.FindBoundaries(DesktopAssembly)
            .Concat(AssemblyIsolationScanner.FindBoundaries(TestAssembly))
            .ToList();

        boundaries.Should().NotBeEmpty("至少掃得到陽性對照的那一個");
        boundaries.Should().Contain(b => b.Method == typeof(IsolationControlMarked).FullName + ".Describe");
        boundaries.Should().AllSatisfy(b =>
        {
            b.Impl.HasFlag(MethodImplAttributes.NoInlining).Should().BeTrue($"{b.Method} 要 NoInlining（TieredPGO 會把它內聯進呼叫端）");
            b.IsAsync.Should().BeFalse($"{b.Method} 不可以是 async");
        });
    }

    [Fact]
    public void 載入邊界_桌面組件裡恰好是SyncEngineLoader的兩個_沒有lambda與巢狀型別()
    {
        var loader = typeof(SyncEngineLoader).FullName!;

        AssemblyIsolationScanner.FindBoundaries(DesktopAssembly).Select(b => b.Method)
            .Should().BeEquivalentTo([loader + ".Create", loader + ".CompletePairing"], "未配對的路徑只能經這兩個入口碰 SignalR／SQLite（D7）");
        AssemblyIsolationScanner.CompilerGeneratedNestedTypes(DesktopAssembly, loader)
            .Should().BeEmpty("lambda、閉包與靜態方法群組的委派快取都會產生巢狀型別，邊界方法本體不掃，裡面的參考會漏掉");
        AssemblyIsolationScanner.CompilerGeneratedMethods(DesktopAssembly, loader)
            .Should().BeEmpty("只捕捉 this 的 lambda、區域函式是型別自己的方法");
    }
}
