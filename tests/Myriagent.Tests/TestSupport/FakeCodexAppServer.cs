using Myriagent.Agents;
using Myriagent.Tests.Agents;
using FakeDevice;

namespace Myriagent.Tests.TestSupport;

/// <summary>
/// 假的 codex app-server（WP3.4 第 11 步、WP4.8 D31）：腳本文字由 <see cref="FakeCodexAppServerScript"/> 產生（WP4.9 步 13 搬到 tools/fake-device，
/// fake-device 的 --fake-codex 用同一份），啟動器經 <see cref="FakeLauncherRules"/>（System32 的 Windows PowerShell、腳本在 TEMP 底下）。
/// 情境、calls.txt、complete.flag 與登出標記的行為見 <see cref="FakeCodexAppServerScript"/>。每個實例一個暫存資料夾（腳本、calls.txt、complete.flag）。
/// 不連網、不耗額度
/// </summary>
internal sealed class FakeCodexAppServer : IDisposable
{
    /// <summary>假的驗證碼（10 字，同 G5 實測的格式）：測試以它掃描記錄檔與 payload</summary>
    public const string UserCode = FakeCodexAppServerScript.UserCode;

    public const string LoginId = FakeCodexAppServerScript.LoginId;

    public const string VerificationUrl = FakeCodexAppServerScript.VerificationUrl;

    /// <summary>登入完成後 account/read 回的 email</summary>
    public const string Email = FakeCodexAppServerScript.Email;

    private readonly TempDirectory temp;

    /// <param name="scenario">情境（見類別說明）</param>
    /// <param name="email">登入完成後 account/read 回的 email（身分比對的測試給不同的信箱）</param>
    /// <param name="startDelayMs">account/login/start 等多久才回（模擬冷啟動很慢）</param>
    /// <param name="logoutExitCode">logout 子指令的結束碼</param>
    /// <param name="logoutDelayMs">logout 子指令留下標記之後等多久才結束（模擬登出逾時）</param>
    /// <param name="readDelayMs">account/read 等多久才回（模擬進行中的查詢；登入狀態在回應時才判斷）</param>
    public FakeCodexAppServer(string scenario, string email = Email, int startDelayMs = 0, int logoutExitCode = 0, int logoutDelayMs = 0, int readDelayMs = 0)
    {
        temp = new TempDirectory("fake-codex-app-server");
        var script = temp.WriteFile(FakeCodexAppServerScript.FileName(scenario),
            FakeCodexAppServerScript.Build(scenario, email, startDelayMs, logoutExitCode, logoutDelayMs, readDelayMs));
        Launcher = FakeLauncherRules.Launcher(AgentProvider.Codex, script, CodexAgentSessionTests.PowerShell)
                   ?? throw new InvalidOperationException("假 app-server 的啟動器沒有通過檢查：" + FakeLauncherRules.Check(AgentProvider.Codex, script,
                       CodexAgentSessionTests.PowerShell, FakeLauncherEnvironment.Current()));
    }

    public AgentLauncher Launcher { get; }

    /// <summary>腳本與 calls.txt 所在的暫存資料夾</summary>
    public string Root => temp.Root;

    private string CallsPath => Path.Combine(temp.Root, FakeCodexAppServerScript.CallsFile);

    /// <summary>每次啟動時的 CODEX_HOME（codexHome= 的值，依啟動順序；空字串＝沒有設定）</summary>
    public IReadOnlyList<string> CodexHomes => FakeCodexFiles.CodexHomes(Calls);

    /// <summary>
    /// 啟動過的程序是不是都已結束（pid= 的每一個；找不到那個程序也算結束）。PID 可能被重複使用：只在測試的短時間內用
    /// </summary>
    public bool AllExited()
    {
        foreach (var line in Calls.Where(c => c.StartsWith("pid=", StringComparison.Ordinal)))
        {
            if (!int.TryParse(line["pid=".Length..], out var pid))
                continue;
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                if (!process.HasExited)
                    return false;
            }
            catch (ArgumentException)
            {
                // 已經結束
            }
            catch (InvalidOperationException)
            {
                // 已經結束
            }
        }
        return true;
    }

    /// <summary>有沒有啟動過（calls.txt 存在）</summary>
    public bool Started => File.Exists(CallsPath);

    /// <summary>calls.txt 的每一行（腳本可能正在寫：共用讀寫開檔，被占用就重試）</summary>
    public IReadOnlyList<string> Calls => FakeCodexFiles.ReadCalls(temp.Root);

    public int Count(string call) => Calls.Count(c => c == call);

    /// <summary>使用者在別的裝置核准了：假 app-server 送 account/login/completed {success:true}，之後 account/read 回已登入</summary>
    public void Complete() => FakeCodexFiles.WriteCompleteFlag(temp.Root);

    /// <summary>等 calls.txt 符合條件（PowerShell 啟動要好幾百毫秒）</summary>
    public async Task WaitForAsync(Func<IReadOnlyList<string>, bool> condition, int timeoutMs = 30000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition(Calls))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("假 app-server 的呼叫紀錄在時限內沒有符合條件：" + string.Join(" | ", Calls));
            await Task.Delay(50);
        }
    }

    public void Dispose() => temp.Dispose();
}
