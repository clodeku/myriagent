using System.Reflection;
using System.Security.Cryptography;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Tests.TestSupport;
using FakeDevice;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.FakeDevice;

/// <summary>
/// 假裝置的 <c>--fake-codex</c>（WP4.9 計畫步 13，D11；用連結編譯的 Runtime，未配對的一台、不連伺服器）：沒帶時每種代理都找不到 CLI、拒絕的 CLI 照舊計數；
/// 帶了時資料根底下的 fake-codex 資料夾有和測試同一份的腳本，帳戶管理員只有 Codex 拿到假啟動器，裝置碼登入與登出走真的帳戶管理員流程、
/// 由假 app-server 回答，控制通道的 fakeCodexComplete 完成登入、fakeCodexCalls 讀得到呼叫紀錄。不跑真的 codex、不開瀏覽器、不連網
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class FakeCodexDeviceTests
{
    private static readonly TimeSpan Slow = TimeSpan.FromSeconds(60);

    private static readonly FakeAccountSpec Codex = new("c", AgentProvider.Codex, "Codex c", null, null, null, "plus");

    private static Task<DeviceRuntime> StartAsync(string root, string? fakeCodex) =>
        DeviceRuntime.StartAsync(new DeviceRuntimeOptions { DataRoot = root, Persist = false, Accounts = [Codex], FakeCodex = fakeCodex });

    private static Task<JObject> ExecAsync(FakeControl control, string json) =>
        control.ExecuteAsync(FakeControlRules.Parse(json).Command ?? throw new InvalidOperationException(FakeControlRules.Parse(json).Error));

    /// <summary>CLI 管理員的「執行 CLI 指令」（fake-device 換成拒絕並計數的那一個）</summary>
    private static CliCommandRunner CliRunner(DeviceRuntime device)
    {
        var field = typeof(CliManager).GetField("options", BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new InvalidOperationException("CliManager 沒有 options 欄位");
        return ((CliManagerOptions)field.GetValue(device.Cli)!).Run;
    }

    private static void RefusedRunCounted(DeviceRuntime device)
    {
        var before = device.RefusedCli;
        var runner = CliRunner(device);
        // 拒絕的那一個不是 async：呼叫當下就丟例外
        Action run = () => _ = runner(new AgentLauncher(@"C:\fake\codex.exe", []), ["--version"], null, TimeSpan.FromSeconds(1), false, null, CancellationToken.None);
        run.Should().Throw<InvalidOperationException>().WithMessage("*不執行任何 CLI 指令*");
        device.RefusedCli.Should().Be(before + 1, "拒絕的 CLI 指令照舊計數");
    }

    [Fact]
    public async Task 沒帶fakeCodex_每種代理都找不到CLI_拒絕的CLI照舊計數_op要求fakeCodex()
    {
        using var temp = new TempDirectory("fd-fake-codex-none");
        await using var device = await StartAsync(temp.Combine("device"), fakeCodex: null);
        await using var control = new FakeControl(device);

        foreach (var provider in AgentProviders.All)
            (await device.OnUi(() => device.Accounts.ResolveLauncher(provider))).Should().BeNull($"沒帶 --fake-codex 時 {provider} 找不到 CLI");
        device.FakeCodexLauncher.Should().BeNull();
        device.FakeCodexFolder.Should().BeNull();
        Directory.Exists(Path.Combine(device.DataRoot, FakeLauncherRules.FolderName)).Should().BeFalse();
        var status = await device.DescribeAsync();
        status["fakeCodex"]!.Type.Should().Be(JTokenType.Null);
        status.Value<int>("openedBrowser").Should().Be(0);
        status.Value<int>("refusedCli").Should().Be(0);

        var calls = () => ExecAsync(control, """{"op":"fakeCodexCalls"}""");
        await calls.Should().ThrowAsync<DeviceRuntimeException>().WithMessage("*--fake-codex*");
        var complete = () => ExecAsync(control, """{"op":"fakeCodexComplete"}""");
        await complete.Should().ThrowAsync<DeviceRuntimeException>().WithMessage("*--fake-codex*");

        RefusedRunCounted(device);
        (await device.DescribeAsync()).Value<int>("refusedCli").Should().Be(1);
        device.Host.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task 帶fakeCodex_只有Codex拿到假app_server_裝置碼登入與登出走同一份腳本()
    {
        using var temp = new TempDirectory("fd-fake-codex");
        await using var device = await StartAsync(temp.Combine("device"), fakeCodex: "ok");
        await using var control = new FakeControl(device);
        var id = DeviceRuntime.AccountId("c");

        // 腳本：資料根底下的 fake-codex，和測試的 FakeCodexAppServer 同一份（搬移前的 SHA-256）
        var folder = Path.Combine(device.DataRoot, FakeLauncherRules.FolderName);
        device.FakeCodexFolder.Should().Be(folder);
        var script = Path.Combine(folder, "fake-codex-ok.ps1");
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(script))).Should().Be("1847e3945b49c3b1edf98132f54d485f5c0a5efb50d9873fd2d7c5ce41c315f3");

        var launchers = await device.OnUi(() => AgentProviders.All.ToDictionary(p => p, p => device.Accounts.ResolveLauncher(p)));
        launchers[AgentProvider.Codex].Should().NotBeNull().And.BeSameAs(device.FakeCodexLauncher);
        launchers[AgentProvider.Codex]!.FileName.Should().Be(FakeLauncherRules.SystemPowerShell);
        launchers[AgentProvider.Codex]!.PrefixArguments.Should().Equal(FakeLauncherRules.PrefixArguments(script));
        launchers.Where(kv => kv.Key != AgentProvider.Codex).Should().OnlyContain(kv => kv.Value == null, "只有 Codex 有假的 CLI");

        var status = (JObject)(await device.DescribeAsync())["fakeCodex"]!;
        status.Value<string>("scenario").Should().Be("ok");
        status.Value<string>("folder").Should().Be(folder);
        status.Value<bool>("started").Should().BeFalse();
        var before = await ExecAsync(control, """{"op":"fakeCodexCalls"}""");
        before.Value<bool>("started").Should().BeFalse();
        before.Value<int>("count").Should().Be(0);

        // 裝置碼登入（這台的帳戶卡發起；遠端的 accountLoginDevice 走同一個登入流程）：假 app-server 回驗證碼，fakeCodexComplete 之後完成
        await device.CommandAsync("accountLogin", new JObject { ["accountId"] = id, ["method"] = "deviceCode" });
        (await device.WaitUntilAsync(() => device.Accounts.GetState(id).DeviceCode is not null, Slow)).Should().BeTrue("假 app-server 回了驗證碼");
        (await device.OnUi(() => device.Accounts.GetState(id).DeviceCode!.UserCode)).Should().Be(FakeCodexAppServerScript.UserCode);
        var started = await ExecAsync(control, """{"op":"fakeCodexCalls"}""");
        started.Value<bool>("started").Should().BeTrue();
        started.Value<int>("loginStarts").Should().Be(1);
        var lines = ((JArray)started["calls"]!).Select(c => c.ToString()).ToList();
        lines.Should().Contain("account/login/start type=chatgptDeviceCode").And.Contain(c => c.StartsWith("args=app-server", StringComparison.Ordinal));
        var codexHome = await device.OnUi(() => device.Accounts.Find(id)!.ConfigDirectory!);
        ((JArray)started["codexHomes"]!).Select(c => c.ToString()).Should().Contain(codexHome, "子程序的 CODEX_HOME 是這個隔離帳戶的資料夾");

        var complete = await ExecAsync(control, """{"op":"fakeCodexComplete"}""");
        complete.Value<bool>("alreadyComplete").Should().BeFalse();
        File.Exists(complete.Value<string>("flag")!).Should().BeTrue();
        (await device.WaitUntilAsync(() => !device.Accounts.GetState(id).LoginBusy && device.Accounts.Find(id)!.Email == FakeCodexAppServerScript.Email, Slow))
            .Should().BeTrue("完成通知之後 account/read 回已登入的信箱");
        (await device.OnUi(() => device.Accounts.GetState(id).Status)).Should().Be(AccountStatus.Ready);
        (await ExecAsync(control, """{"op":"fakeCodexComplete"}""")).Value<bool>("alreadyComplete").Should().BeTrue();

        // 登出：帳戶管理員跑 launcher＋logout（腳本依 argv 分派），在這個 CODEX_HOME 留標記，之後 account/read 回未登入
        await device.CommandAsync("accountLogout", new JObject { ["accountId"] = id });
        (await device.WaitUntilAsync(() => device.Accounts.GetState(id).Status == AccountStatus.NotLoggedIn, Slow)).Should().BeTrue("登出之後假 app-server 回未登入");
        var after = await ExecAsync(control, """{"op":"fakeCodexCalls"}""");
        after.Value<int>("logouts").Should().Be(1);
        File.Exists(Path.Combine(codexHome, FakeCodexAppServerScript.LoggedOutMarker)).Should().BeTrue();

        device.OpenedBrowser.Should().Be(0, "裝置碼登入不開瀏覽器");
        device.RefusedCli.Should().Be(0, "假 app-server 走帳戶管理員的啟動器，不經 CLI 管理員");
        RefusedRunCounted(device);
        device.Host.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task 帶fakeCodex_資料根不在TEMP底下或情境不認得_什麼都不建就拒絕()
    {
        var outside = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "myriagent-fd-not-temp-" + Guid.NewGuid().ToString("N")[..12]);
        var start = () => StartAsync(outside, fakeCodex: "ok");
        await start.Should().ThrowAsync<DeviceRuntimeException>().WithMessage("*TEMP*");
        Directory.Exists(outside).Should().BeFalse("檢查在建資料根之前");

        using var temp = new TempDirectory("fd-fake-codex-bad");
        var root = temp.Combine("device");
        var bogus = () => StartAsync(root, fakeCodex: "bogus");
        await bogus.Should().ThrowAsync<DeviceRuntimeException>().WithMessage("*情境要是*");
        Directory.Exists(root).Should().BeFalse();
    }
}
