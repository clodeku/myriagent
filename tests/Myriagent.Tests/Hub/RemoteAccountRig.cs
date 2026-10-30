using System.Diagnostics;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.Agents;
using Myriagent.Tests.Cli;
using Myriagent.Tests.Protocol.Remote;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit.Sdk;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 中樞層級的遠端帳戶與 CLI 指令測試台（WP4.8 步 7 起、D30）：經閘門送帳戶與 CLI 指令的測試一律用它。
/// <list type="bullet">
/// <item>HubHarness（帳戶預設未登入；設定資料夾在暫存資料夾：HubHarness 的 acc-claude 是系統預設〔沿用使用者真的 ~/.claude〕，
/// rig 把它改成暫存資料夾裡的隔離帳戶，登出、查詢與憑證檔監看都碰不到真的設定資料夾）的 CliManager 換成假的：找安裝（<see cref="Installations"/>，預設都沒裝）、
/// 子指令（<see cref="Commands"/>，不起程序）、三個官方安裝視窗（<see cref="InstallersStarted"/>：暫存資料夾裡的假腳本，不開視窗）與查最新版
/// （假的 npm registry，最新版 9.9.9）都是假的</item>
/// <item>ResolveLauncher 換成假 app-server（Codex）與假 claude（Claude）；<see cref="Launchers"/> 可以換。開瀏覽器換成計數器</item>
/// <item>守門：ResolveLauncher 與找安裝回傳的每一個，執行檔要是 Windows PowerShell 或 cmd、腳本與安裝都要在暫存資料夾底下，
/// 否則不交出去（回 null）並記下，<see cref="Dispose"/> 時讓測試失敗</item>
/// <item>假的同步橋（<see cref="Bridge"/>）回測試指定的遠端登入目標與預期信箱；這台允許遠端操作（Control）</item>
/// </list>
/// 不跑真的 claude／codex、不開安裝視窗或 winget、不開瀏覽器、不連網、不碰使用者真的設定資料夾。帳戶相關的等待一律 60 秒
/// </summary>
internal sealed class RemoteAccountRig : IDisposable
{
    /// <summary>帳戶相關的等待一律 60 秒（D30）</summary>
    public const int Slow = 60_000;

    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private readonly object gate = new();
    private readonly List<string> violations = new();
    private readonly TempDirectory installers = new("fake-installer");
    private readonly HttpClient registry = new(new FakeRegistry());
    private int opened;
    private int installersStarted;
    private bool disposed;

    private sealed class NoUsagePolling : IUsagePollPolicy
    {
        public bool ShouldPoll(AgentAccount account) => false;
    }

    /// <param name="codex">假 app-server（預設情境 ok）；交給測試台一起釋放</param>
    /// <param name="claude">假 claude；交給測試台一起釋放</param>
    /// <param name="status">三個帳戶一開始的狀態（預設未登入）</param>
    /// <param name="attachBridge">false＝這台沒有配對（中樞沒有同步橋）</param>
    public RemoteAccountRig(FakeCodexAppServer? codex = null, FakeClaudeLogin? claude = null, AccountStatus status = AccountStatus.NotLoggedIn, bool attachBridge = true)
    {
        Fake = codex ?? new FakeCodexAppServer("ok");
        Claude = claude ?? new FakeClaudeLogin();
        Commands = new FakeCliCommands(_ => "9.9.9");
        Launchers = DefaultLauncher;
        // CliManager 查最新版只問假的 npm registry（最新版 9.9.9；其他主機 404，不連網）
        H = new HubHarness(status: status, createCli: _ => CliManagerTestKit.Create(registry, GuardedLocate, Commands, new FakeProcesses(), AgentProvider.Claude,
            background: false, configure: o => o with
            {
                StartClaudeInstaller = () => StartFakeInstaller("claude"),
                StartAntigravityInstaller = () => StartFakeInstaller("antigravity"),
                StartGrokInstaller = () => StartFakeInstaller("grok"),
            }));
        H.Ui.Invoke(() =>
        {
            // 經閘門的帳戶與 CLI 指令會登出、重新查詢所有帳戶（cliInstall／cliUpdate 之後）：系統預設的 acc-claude 一律改到暫存資料夾（D30）
            H.Accounts.Find(H.Claude.Id)!.ConfigDirectory = H.Temp.Combine("accounts", H.Claude.Id, "claude");
            H.Accounts.ResolveLauncher = provider => Guard("ResolveLauncher", provider, Launchers(provider));
            H.Accounts.OpenBrowser = _ => Interlocked.Increment(ref opened);
            H.Accounts.DeviceCodeTimeout = TimeSpan.FromSeconds(90);
            H.Accounts.UsagePollPolicy = new NoUsagePolling();
            if (attachBridge)
                H.Hub.AttachSync(Bridge);
        });
        RemoteEntrySamples.AllowRemote(H);
    }

    public HubHarness H { get; }

    public FakeCodexAppServer Fake { get; }

    public FakeClaudeLogin Claude { get; }

    /// <summary>假 CLI 子指令（--version、update、npm；不起程序）</summary>
    public FakeCliCommands Commands { get; }

    /// <summary>假的同步橋（沒有配對時沒掛上）</summary>
    public FakeAccountBridge Bridge { get; } = new();

    /// <summary>ResolveLauncher 交給守門之前的來源（預設：Codex＝假 app-server、Claude＝假 claude，其他沒有 CLI）</summary>
    public Func<AgentProvider, AgentLauncher?> Launchers { get; set; }

    /// <summary>CliManager 找安裝的來源（交給守門之前；預設都沒裝）</summary>
    public Func<AgentProvider, CliInstallation?> Installations { get; set; } = _ => null;

    /// <summary>假安裝視窗的結束碼</summary>
    public int InstallerExitCode { get; set; }

    public int Opened => Volatile.Read(ref opened);

    /// <summary>假安裝視窗啟動了幾次</summary>
    public int InstallersStarted => Volatile.Read(ref installersStarted);

    /// <summary>守門擋下的（執行檔或腳本不在暫存資料夾）</summary>
    public IReadOnlyList<string> Violations
    {
        get
        {
            lock (gate)
                return violations.ToList();
        }
    }

    public AgentLauncher? DefaultLauncher(AgentProvider provider) => provider switch
    {
        AgentProvider.Codex => Fake.Launcher,
        AgentProvider.Claude => Claude.Launcher,
        AgentProvider.Antigravity or AgentProvider.Grok => null,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    public AccountRuntimeState State(string id) => H.OnUi(() => H.Accounts.GetState(id));

    /// <summary>經中樞的遠端指令入口執行（UI 執行緒），最多等 60 秒</summary>
    public async Task<RemoteOutcome> RunAsync(JObject command, int tier = 2, Recorder<JObject>? output = null, string? cid = null)
        => await H.Ui.Invoke(() => H.Hub.ExecuteRemoteCommandAsync(RemoteEntrySamples.Body(command, tier, cid: cid), m => output?.Add(m)))
            .WaitAsync(TimeSpan.FromMilliseconds(Slow));

    /// <summary>accountLogin {accountId, method?, deviceId}（deviceId 是這台）</summary>
    public static JObject Login(string accountId, string? method = null)
    {
        var command = new JObject { ["cmd"] = "accountLogin", ["accountId"] = accountId, ["deviceId"] = RemoteSamples.DeviceId };
        if (method != null)
            command["method"] = method;
        return command;
    }

    /// <summary>accountLoginDevice {logicalAccountId, deviceId, method?}（網頁一律送這個，D2）</summary>
    public static JObject LoginDevice(string logicalAccountId, string? method = null)
    {
        var command = new JObject { ["cmd"] = "accountLoginDevice", ["logicalAccountId"] = logicalAccountId, ["deviceId"] = RemoteSamples.DeviceId };
        if (method != null)
            command["method"] = method;
        return command;
    }

    /// <summary>accountLogout {accountId, deviceId}（單台登出；不帶 deviceId 的「所有電腦登出」由伺服器分給各台，到這台時一樣是這個形狀）</summary>
    public static JObject Logout(string accountId)
        => new() { ["cmd"] = "accountLogout", ["accountId"] = accountId, ["deviceId"] = RemoteSamples.DeviceId };

    /// <summary>cliInstall／cliUpdate {provider, deviceId}</summary>
    public static JObject CliCommand(string cmd, AgentProvider provider)
        => new() { ["cmd"] = cmd, ["provider"] = provider.ToString(), ["deviceId"] = RemoteSamples.DeviceId };

    public Task WaitLoginEndedAsync(string accountId)
        => H.WaitUntilAsync(() => H.Accounts.Find(accountId) is null || !H.Accounts.GetState(accountId).LoginBusy, "登入結束（LoginBusy 變回 false）", Slow);

    public async Task CancelAndWaitAsync(string accountId)
    {
        H.Ui.Invoke(() => H.Accounts.CancelLogin(accountId));
        await WaitLoginEndedAsync(accountId);
    }

    /// <summary>守門：執行檔是 Windows PowerShell 或 cmd，而且帶的每個腳本路徑都在暫存資料夾底下才交出去</summary>
    private AgentLauncher? Guard(string source, AgentProvider provider, AgentLauncher? launcher)
    {
        if (launcher is null)
            return null;
        var exe = SafeFullPath(launcher.FileName);
        var shell = string.Equals(exe, CodexAgentSessionTests.PowerShell, StringComparison.OrdinalIgnoreCase) || string.Equals(exe, Cmd, StringComparison.OrdinalIgnoreCase);
        var scripts = launcher.PrefixArguments.Where(a => Path.IsPathRooted(a) && Path.HasExtension(a)).ToList();
        if (shell && scripts.Count > 0 && scripts.All(IsUnderTemp))
            return launcher;
        Violate($"{source}({provider}) 解析到 {launcher.FileName} {string.Join(" ", launcher.PrefixArguments)}：不是暫存資料夾裡的假程式");
        return null;
    }

    /// <summary>守門：找到的安裝（顯示路徑、原生執行檔）都要在暫存資料夾底下</summary>
    private CliInstallation? GuardedLocate(AgentProvider provider)
    {
        var installation = Installations(provider);
        if (installation is null)
            return null;
        if (IsUnderTemp(installation.DisplayPath) && (installation.NativeExecutable is null || IsUnderTemp(installation.NativeExecutable)))
            return installation;
        Violate($"Locate({provider}) 找到 {installation.DisplayPath}：不是暫存資料夾裡的假安裝");
        return null;
    }

    /// <summary>假的官方安裝視窗：暫存資料夾裡的 PowerShell 腳本，照 <see cref="InstallerExitCode"/> 結束，不開視窗</summary>
    private Process StartFakeInstaller(string name)
    {
        Interlocked.Increment(ref installersStarted);
        var script = installers.WriteFile($"install-{name}-{Guid.NewGuid():N}.ps1", $"exit {InstallerExitCode}\n");
        var info = new ProcessStartInfo(CodexAgentSessionTests.PowerShell) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
            info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new InvalidOperationException("假安裝視窗沒有啟動");
    }

    private void Violate(string text)
    {
        lock (gate)
            violations.Add(text);
    }

    private static string SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static bool IsUnderTemp(string path)
    {
        var temp = Path.GetFullPath(Path.GetTempPath());
        return SafeFullPath(path).StartsWith(temp, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>還在進行的登入一律取消、等登入程序關掉；守門擋過任何東西就讓測試失敗（只丟一次）</summary>
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        try
        {
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
            installers.Dispose();
            registry.Dispose();
        }

        var found = Violations;
        if (found.Count > 0)
            throw new XunitException("RemoteAccountRig 的守門擋下了暫存資料夾以外的程式（D30）：" + string.Join("；", found));
    }
}

/// <summary>
/// 遠端帳戶測試的同步橋替身：FindRemoteLoginTarget 回 <see cref="ByLocal"/>／<see cref="ByLogical"/> 指定的結果（沒指定的是 NotPaired），
/// ExpectedEmail 回 <see cref="Emails"/>；其他成員不做事。每次呼叫都記下
/// </summary>
internal sealed class FakeAccountBridge : ISyncBridge
{
    public Dictionary<string, RemoteLoginLookup> ByLocal { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, RemoteLoginLookup> ByLogical { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string?> Emails { get; } = new(StringComparer.Ordinal);

    public Recorder<(string? Local, string? Logical)> Lookups { get; } = new();

    public RemoteLoginLookup FindRemoteLoginTarget(string? localAccountId, string? logicalAccountId)
    {
        Lookups.Add((localAccountId, logicalAccountId));
        if (localAccountId is not null)
            return ByLocal.TryGetValue(localAccountId, out var local) ? local : RemoteLoginLookup.NotPaired;
        return logicalAccountId is not null && ByLogical.TryGetValue(logicalAccountId, out var logical) ? logical : RemoteLoginLookup.NotPaired;
    }

    public string? ExpectedEmail(string localAccountId) => Emails.TryGetValue(localAccountId, out var email) ? email : null;

    public bool HandleCommand(IAgentView view, string type, JObject msg) => false;

    public void OnAccountUpdated(string accountId)
    {
    }

    public void OnAccountsReordered()
    {
    }

    public void OnRoamingSettingChanged(string key, JToken value)
    {
    }

    public JObject? InitRemote() => null;

    public bool IsStarting => false;

    public JObject? DevicesCli() => null;

    public void DecorateAccounts(JArray accounts)
    {
    }

    public CatalogAccountTemplate? ClaimCatalogAccount(string? logicalAccountId, out string failure)
    {
        failure = "測試替身不處理「在這台登入」";
        return null;
    }
}
