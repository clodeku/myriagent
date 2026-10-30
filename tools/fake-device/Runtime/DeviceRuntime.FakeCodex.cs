using System.Text;
using Myriagent.Agents;
using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace FakeDevice;

/// <summary>
/// 假 Codex app-server（WP4.9 步 13，D11）：<see cref="DeviceRuntimeOptions.FakeCodex"/> 時在 <c>{資料根}\fake-codex\</c> 寫
/// <see cref="FakeCodexAppServerScript"/>（和 WP4.8 測試同一份腳本），帳戶管理員找 Codex 的 CLI 時交出它的假啟動器（<see cref="FakeLauncherRules"/>）。
/// 控制通道的 <c>fakeCodexComplete</c>（寫 complete.flag，同測試的 Complete()）與 <c>fakeCodexCalls</c>（讀 calls.txt）在這裡。
/// 資料夾跟著資料根：同一個資料根重新啟動時沿用 calls.txt 與 complete.flag（登入狀態以 account/read 的回答為準，跨重啟一致）
/// </summary>
internal sealed partial class DeviceRuntime
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>假 app-server 的情境（沒有 <c>--fake-codex</c> 是 null）</summary>
    public string? FakeCodexScenario => options.FakeCodex;

    /// <summary>假 app-server 的資料夾（腳本、calls.txt、complete.flag；沒有 <c>--fake-codex</c> 是 null）</summary>
    public string? FakeCodexFolder => options.FakeCodex is null ? null : FakeLauncherRules.FolderOf(options.DataRoot);

    /// <summary>交給帳戶管理員的 Codex 假啟動器（沒有 <c>--fake-codex</c> 是 null）</summary>
    public AgentLauncher? FakeCodexLauncher => fakeCodexLauncher;

    /// <summary>帳戶管理員想開瀏覽器的次數（假裝置一律不開）</summary>
    public int OpenedBrowser => Volatile.Read(ref openedBrowser);

    /// <summary>有 <c>--fake-codex</c> 時情境要認得、資料根要在 TEMP 底下（StartAsync 建任何資料夾之前檢查）；合格或沒有 <c>--fake-codex</c> 回 null</summary>
    private static string? CheckFakeCodexOptions(DeviceRuntimeOptions options) => options.FakeCodex is null
        ? null
        : FakeCodexAppServerScript.CheckScenarioName(options.FakeCodex) ?? FakeLauncherRules.CheckDataRoot(options.DataRoot, Path.GetTempPath());

    /// <summary>
    /// 有 <c>--fake-codex</c> 時（情境與資料根已由 <see cref="CheckFakeCodexOptions"/> 檢查過）：寫腳本（UTF-8 無 BOM、只有 ASCII）、建好並檢查假啟動器；
    /// 在 Build 之前、UI 執行緒以外做。不合格丟 <see cref="DeviceRuntimeException"/>
    /// </summary>
    private void PrepareFakeCodex()
    {
        if (options.FakeCodex is not { } scenario)
            return;
        var environment = FakeLauncherEnvironment.Current();
        var folder = FakeLauncherRules.FolderOf(options.DataRoot);
        Directory.CreateDirectory(folder);
        if (new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new DeviceRuntimeException($"假 app-server 的資料夾是重新解析點（連結），不寫：{folder}");
        var script = Path.Combine(folder, FakeCodexAppServerScript.FileName(scenario));
        File.WriteAllBytes(script, Utf8NoBom.GetBytes(FakeCodexAppServerScript.Build(scenario)));
        fakeCodexLauncher = FakeLauncherRules.Launcher(AgentProvider.Codex, script, FakeLauncherRules.SystemPowerShell, environment)
                            ?? throw new DeviceRuntimeException("假 app-server 的啟動器沒有通過檢查："
                                                                + FakeLauncherRules.Check(AgentProvider.Codex, script, FakeLauncherRules.SystemPowerShell, environment));
        Log.Info($"fake-device：假 Codex app-server（情境 {scenario}）在 {folder}");
    }

    /// <summary>status 的 <c>fakeCodex</c>（沒有 <c>--fake-codex</c> 是 null）</summary>
    internal JObject? DescribeFakeCodex() => options.FakeCodex is null
        ? null
        : new JObject
        {
            ["scenario"] = options.FakeCodex,
            ["folder"] = FakeCodexFolder,
            ["launcher"] = fakeCodexLauncher?.FileName,
            ["started"] = File.Exists(Path.Combine(FakeCodexFolder!, FakeCodexAppServerScript.CallsFile)),
        };

    /// <summary>
    /// 控制通道的 <c>fakeCodexCalls</c>：calls.txt 的每一行（腳本可能正在寫：共用讀寫開檔，被占用就重試）；另回幾個常用的計數
    /// （<c>logouts</c>＝logout 子指令的次數、<c>loginStarts</c>＝account/login/start 的次數、<c>codexHomes</c>＝每次啟動時的 CODEX_HOME）
    /// </summary>
    public JObject FakeCodexCalls()
    {
        var folder = RequireFakeCodex("fakeCodexCalls");
        var calls = FakeCodexFiles.ReadCalls(folder);
        return new JObject
        {
            ["scenario"] = options.FakeCodex,
            ["folder"] = folder,
            ["started"] = File.Exists(Path.Combine(folder, FakeCodexAppServerScript.CallsFile)),
            ["count"] = calls.Count,
            ["logouts"] = calls.Count(c => c == "logout"),
            ["loginStarts"] = calls.Count(c => c.StartsWith("account/login/start", StringComparison.Ordinal)),
            ["codexHomes"] = new JArray(FakeCodexFiles.CodexHomes(calls)),
            ["calls"] = new JArray(calls),
        };
    }

    /// <summary>控制通道的 <c>fakeCodexComplete</c>：「使用者在別的裝置核准了」——寫 complete.flag（同測試的 Complete()），進行中的登入接著完成</summary>
    public JObject FakeCodexComplete()
    {
        var folder = RequireFakeCodex("fakeCodexComplete");
        var flag = Path.Combine(folder, FakeCodexAppServerScript.CompleteFlagFile);
        var already = File.Exists(flag);
        FakeCodexFiles.WriteCompleteFlag(folder);
        return new JObject { ["scenario"] = options.FakeCodex, ["flag"] = flag, ["alreadyComplete"] = already };
    }

    private string RequireFakeCodex(string op) =>
        FakeCodexFolder ?? throw new DeviceRuntimeException($"{op} 要以 --fake-codex <情境> 啟動（{string.Join("、", FakeCodexAppServerScript.Scenarios)}）");
}

/// <summary>假 app-server 資料夾裡的檔（calls.txt、complete.flag）：測試的 FakeCodexAppServer 與 fake-device 共用</summary>
internal static class FakeCodexFiles
{
    /// <summary>calls.txt 的每一行（腳本可能正在寫：共用讀寫開檔，被占用就每 50 毫秒重試，最多 20 次）；還沒有這個檔回空的</summary>
    public static IReadOnlyList<string> ReadCalls(string folder)
    {
        var path = Path.Combine(folder, FakeCodexAppServerScript.CallsFile);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                    return Array.Empty<string>();
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>每次啟動時的 CODEX_HOME（codexHome= 的值，依啟動順序；空字串＝沒有設定）</summary>
    public static IReadOnlyList<string> CodexHomes(IReadOnlyList<string> calls) =>
        calls.Where(c => c.StartsWith("codexHome=", StringComparison.Ordinal)).Select(c => c["codexHome=".Length..]).ToList();

    /// <summary>使用者在別的裝置核准了：寫 complete.flag（假 app-server 送 account/login/completed {success:true}，之後 account/read 回已登入）</summary>
    public static void WriteCompleteFlag(string folder) => File.WriteAllText(Path.Combine(folder, FakeCodexAppServerScript.CompleteFlagFile), "");
}
