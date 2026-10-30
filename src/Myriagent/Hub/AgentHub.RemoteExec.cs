using Myriagent.Remote;
using Myriagent.RemoteExec;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 遠端執行指令的被操作端（docs/sync-protocol.md 14.11；2026-10-25 擁有者要求）：remoteExec 照一般的遠端指令過閘門 5a～5i
// （本機政策要 Control），這裡的處理者另外要這台開了「允許其他電腦的代理在這台執行指令」（SyncSettings.AllowRemoteExec，只能在這台改），
// 再交給執行者的檢查（app 以系統管理員執行不收、工作資料夾、殼層）。done 只表示這台接下了；高風險的確認、輸出與結束碼走 exec1 的串流。
// 中樞預設沒有執行者（回 unsupported），只有 AppController 以 AttachRemoteExec 接上真的；中樞測試用假的
internal sealed partial class AgentHub
{
    private IRemoteExecRunner? remoteExecRunner;

    private RemoteExecIssuerInfo? remoteExecIssuer;

    /// <summary>接上執行遠端指令的執行者（AppController 在建立同步之前呼叫）</summary>
    internal void AttachRemoteExec(IRemoteExecRunner runner) => remoteExecRunner = runner ?? throw new ArgumentNullException(nameof(runner));

    /// <summary>接上發起的一方：對話的 CLI 拿到 myriagent-remote 的管道、權杖與資料夾（PATH 的最前面），已配對時給代理的說明多一段用法</summary>
    internal void AttachRemoteExecIssuer(RemoteExecIssuerInfo issuer) => remoteExecIssuer = issuer ?? throw new ArgumentNullException(nameof(issuer));

    /// <summary>對話的標題（UI 執行緒；myriagent-remote 送出時告訴那台的人是哪個對話下的）；找不到回 null</summary>
    internal string? ConversationTitle(string conversationId)
        => conversations.TryGetValue(conversationId, out var c) && !c.IsDraft && !string.IsNullOrWhiteSpace(c.Meta.Title) ? c.Meta.Title : null;

    /// <summary>對話 CLI 的環境變數（BuildSessionSettings 的回呼；背景執行緒）</summary>
    private void ApplyRemoteExecEnvironment(string conversationId, IDictionary<string, string?> env)
    {
        if (remoteExecIssuer is not { } issuer)
            return;
        env[RemotePipeVariable] = issuer.PipeName;
        env[RemoteTokenVariable] = issuer.TokenFor(conversationId);
        if (issuer.ToolDirectory is { } dir)
        {
            var key = env.Keys.FirstOrDefault(k => string.Equals(k, "PATH", StringComparison.OrdinalIgnoreCase)) ?? "PATH";
            env[key] = RemoteExecHubRules.PrependPath(dir, env.TryGetValue(key, out var path) ? path : null);
        }
    }

    /// <summary>已配對、接上了 myriagent-remote 時附在給代理的說明後面的一段（UI 執行緒）</summary>
    private string RemoteExecInstructions()
        => remoteExecIssuer is { ToolDirectory: not null } issuer && issuer.IsPaired() ? RemoteExecHubRules.InstructionsHint : string.Empty;

    private const string RemotePipeVariable = RemoteExecPipeRules.PipeVariable;

    private const string RemoteTokenVariable = RemoteExecPipeRules.TokenVariable;

    /// <summary>還有遠端執行指令沒結束（app 自我更新不在這時自動重啟；UI 執行緒讀）</summary>
    internal bool RemoteExecRunning => remoteExecRunner is { RunningCount: > 0 };

    partial void RegisterExecRemoteHandlers(List<IRemoteCommandHandler> into) => into.Add(new ExecRemoteHandler(this));

    /// <summary><c>remoteExec</c> 的處理者（其他指令回 false）</summary>
    private sealed class ExecRemoteHandler(AgentHub hub) : IRemoteCommandHandler
    {
        public bool TryHandle(RemoteViewContext context, IAgentView view, string cmd, JObject command)
        {
            if (cmd != RemoteCommandCatalog.RemoteExec)
                return false;
            if (!context.Authorized)
            {
                context.Reject(RemoteResultCodes.Invalid, "這個指令沒有經過這台電腦的遠端操作檢查", RemoteGateSteps.Handler);
                return true;
            }

            if (hub.remoteExecRunner is not { } runner)
            {
                context.Reject(RemoteResultCodes.Unsupported, "這台的 Clode Myriagent 不能執行遠端指令", RemoteGateSteps.Handler);
                return true;
            }

            if (SyncCoordinatorRules.HelloPolicy(hub.settings.Sync).AllowRemoteExec != true)
            {
                context.Reject(RemoteResultCodes.Policy, RemoteExecHubRules.NotAllowedMessage, RemoteGateSteps.Handler);
                return true;
            }

            if (RemoteExecRunRules.Parse(command) is not { } request)
            {
                context.Reject(RemoteResultCodes.Invalid, "遠端執行指令的內容不合格", RemoteGateSteps.Handler);
                return true;
            }

            if (runner.Check(request) is { } problem)
            {
                context.Reject(problem.Code, problem.Message, RemoteGateSteps.Handler);
                return true;
            }

            runner.Start(context.CommandId, request, context.IssuerLabel);
            context.Complete();
            return true;
        }
    }
}

/// <summary>發起的一方給對話 CLI 的東西（AppController 建）</summary>
/// <param name="PipeName">myriagent-remote 連的本機管道</param>
/// <param name="TokenFor">對話的權杖</param>
/// <param name="ToolDirectory">myriagent-remote.exe 所在的資料夾（放到 PATH 的最前面；找不到 myriagent-remote.exe 時是 null，也不給代理說明）</param>
/// <param name="IsPaired">這台已配對（任何執行緒）</param>
internal sealed record RemoteExecIssuerInfo(string PipeName, Func<string, string> TokenFor, string? ToolDirectory, Func<bool> IsPaired);

/// <summary>遠端執行指令在中樞的規則與說明文字</summary>
internal static class RemoteExecHubRules
{
    /// <summary>這台沒有開「允許遠端執行指令」</summary>
    public const string NotAllowedMessage = "這台電腦沒有開啟「允許其他電腦的代理在這台執行指令」：請到那台的設定頁「多電腦同步」開啟（只能在那台改）";

    /// <summary>myriagent-remote 的執行檔名稱（和 app 放在同一個資料夾）</summary>
    public const string ToolFileName = "myriagent-remote.exe";

    /// <summary>已配對時附在給代理的說明後面（Claude 的 --append-system-prompt、Codex 的 developerInstructions…）</summary>
    public const string InstructionsHint =
        "\n\n這台電腦已和其他電腦配對。要在其他配對的電腦執行主控台指令，用 myriagent-remote（已在 PATH）：" +
        "`myriagent-remote list` 列出電腦與能不能執行；`myriagent-remote run <電腦> [--shell powershell|pwsh|cmd] [--cwd 資料夾] [--timeout 秒] -- <指令>` " +
        "在那台以一般權限執行，輸出與結束碼照樣傳回（預設 powershell、上限 600 秒；結束碼 125 是沒有送出或被拒、126 是那台的人拒絕、124 逾時、255 結果不明）。" +
        "關機、格式化、改系統設定、要系統管理員權限這類高風險指令，要那台電腦的人在現場同意，沒人同意就不會執行；不要嘗試繞過這個確認。";

    /// <summary>把 <paramref name="directory"/> 放到 PATH 的最前面（已經在第一個就不重複）</summary>
    public static string PrependPath(string directory, string? path)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        if (string.IsNullOrEmpty(path))
            return directory;
        var first = path.Split(Path.PathSeparator, 2)[0].Trim().TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(first, directory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ? path : directory + Path.PathSeparator + path;
    }
}
