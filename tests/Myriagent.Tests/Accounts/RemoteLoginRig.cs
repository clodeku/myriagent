using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Tests.Hub;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Accounts;

/// <summary>
/// 帳戶管理員層級的遠端登入、登出測試台（WP4.8 步 3、4）：HubHarness（帳戶都是未登入；設定資料夾在暫存資料夾）＋假 app-server（Codex）
/// ＋假 claude（Claude）當 ResolveLauncher，其他種類找不到 CLI；開瀏覽器換成計數器。不跑真的 claude／codex、不開瀏覽器、不連網。
/// 假程式由測試建立、交給這裡一起釋放。中樞層級的遠端指令改用 T/Hub/RemoteAccountRig（步 7）
/// </summary>
internal sealed class RemoteLoginRig : IDisposable
{
    /// <summary>帳戶相關的等待一律 60 秒（D30）</summary>
    public const int Slow = 60_000;

    private sealed class NoUsagePolling : IUsagePollPolicy
    {
        public bool ShouldPoll(AgentAccount account) => false;
    }

    private readonly Recorder<(string Id, bool LoggedOut)> mismatches = new();
    private readonly Recorder<(string Id, string Message, bool Done, bool? Success)> progress = new();
    private int opened;

    public RemoteLoginRig(FakeCodexAppServer? codex = null, FakeClaudeLogin? claude = null)
    {
        H = new HubHarness(status: AccountStatus.NotLoggedIn);
        Fake = codex ?? new FakeCodexAppServer("ok");
        Claude = claude ?? new FakeClaudeLogin();
        H.Ui.Invoke(() =>
        {
            H.Accounts.ResolveLauncher = Resolve;
            H.Accounts.OpenBrowser = _ => Interlocked.Increment(ref opened);
            // 驗證碼一直等著（取消、期限、核准才結束）：抓「期限沒有取消登入」
            H.Accounts.DeviceCodeTimeout = TimeSpan.FromSeconds(90);
            // 不查官方用量端點（Claude 的 /api/oauth/usage 是 HTTP，Codex 的 rateLimits 不需要）：狀態只由假 CLI 決定
            H.Accounts.UsagePollPolicy = new NoUsagePolling();
            H.Accounts.RemoteIdentityMismatch += (id, loggedOut) => mismatches.Add((id, loggedOut));
            H.Accounts.LoginProgress += (id, message, done, success) => progress.Add((id, message, done, success));
        });
    }

    public HubHarness H { get; }

    public FakeCodexAppServer Fake { get; }

    public FakeClaudeLogin Claude { get; }

    public int Opened => Volatile.Read(ref opened);

    public string Codex => H.Codex.Id;

    /// <summary>預設的 ResolveLauncher：Codex＝假 app-server、Claude＝假 claude，其他種類沒有 CLI</summary>
    public AgentLauncher? Resolve(AgentProvider provider) => provider switch
    {
        AgentProvider.Codex => Fake.Launcher,
        AgentProvider.Claude => Claude.Launcher,
        AgentProvider.Antigravity or AgentProvider.Grok => null,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>RemoteIdentityMismatch 事件的紀錄（帳戶、登出是否成功）</summary>
    public IReadOnlyList<(string Id, bool LoggedOut)> Mismatches => mismatches.All;

    /// <summary>LoginProgress 事件的紀錄</summary>
    public IReadOnlyList<(string Id, string Message, bool Done, bool? Success)> Progress => progress.All;

    public AccountRuntimeState State(string id) => H.OnUi(() => H.Accounts.GetState(id));

    public bool IsHeld(string id) => H.OnUi(() => H.Accounts.IsIdentityHeld(id));

    public Task<RemoteLoginStart> StartAsync(string accountId, LoginRequest request, int timeoutMs = Slow)
        => H.OnUi(() => H.Accounts.StartRemoteLoginAsync(accountId, request)).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));

    public Task WaitLoginEndedAsync(string accountId)
        => H.WaitUntilAsync(() => !H.Accounts.GetState(accountId).LoginBusy, "登入結束（LoginBusy 變回 false）", Slow);

    /// <summary>等這個帳戶的登入回報結束（done 的進度：登入結束、重新查詢之後才發）</summary>
    public async Task<(string Id, string Message, bool Done, bool? Success)> WaitDoneAsync(string accountId)
    {
        await H.WaitUntilAsync(() => Progress.Any(p => p.Id == accountId && p.Done), "登入回報結束（done 的進度）", Slow);
        return Progress.Last(p => p.Id == accountId && p.Done);
    }

    public async Task CancelAndWaitAsync(string accountId)
    {
        H.Ui.Invoke(() => H.Accounts.CancelLogin(accountId));
        await WaitLoginEndedAsync(accountId);
    }

    /// <summary>記錄檔（AppPaths.LogDirectory 底下的每個檔）的全文</summary>
    public static string LogText()
    {
        Log.Flush();
        var dir = AppPaths.LogDirectory;
        return Directory.Exists(dir)
            ? string.Join("\n", Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => File.ReadAllText(f)))
            : string.Empty;
    }

    public void Dispose()
    {
        try
        {
            // 保險：還在進行的登入一律取消，等登入程序關掉
            H.Ui.Invoke(() =>
            {
                foreach (var account in H.Accounts.Accounts.ToList())
                    H.Accounts.CancelLogin(account.Id);
            });
            H.Ui.DrainAsync(5).Wait(TimeSpan.FromSeconds(15));
        }
        finally
        {
            H.Dispose();
            Fake.Dispose();
            Claude.Dispose();
        }
    }
}
