using System.Diagnostics;
using System.Text;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Tests.TestSupport;
using static Myriagent.Tests.Cli.CliManagerTestKit;

namespace Myriagent.Tests.Cli;

/// <summary>
/// npm 全體屏障（設計 7.3.1）以真的程序驗證（研究 E2 的作法）：暫存資料夾的假 npm 前綴（路徑含空白）、把系統的 PING.EXE 複製成
/// vendor 的 codex.exe 執行（佔住檔案：執行中的 exe 不能刪、所在資料夾可以改名）。app 自己的程序（在工作物件裡）要等它結束才換檔；
/// 殘留被 app 以外的程序佔住時列出程序並中止、不跑 npm；npm 以 cmd /d /c call "npm.cmd" 執行（使用者實際會用的 npm）
/// </summary>
public sealed class CliNpmBarrierTests
{
    private static string PingExe => Path.Combine(Environment.SystemDirectory, "PING.EXE");

    /// <summary>以假的 codex.exe（PING.EXE 的複本）佔住檔案：ping 60 次本機（約 60 秒），測試結束前一定結束它</summary>
    private static Process StartHolder(string exe)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add("60");
        psi.ArgumentList.Add("127.0.0.1");
        return Process.Start(psi) ?? throw new InvalidOperationException("無法啟動假的 codex.exe");
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>照 npm install -g 的順序換掉套件（研究 E2）</summary>
    private static void InstallVersion(CodexNpmLayout codex, string version)
    {
        codex.Retire();
        codex.WritePackage(version, version, extraFiles: 3);
        codex.RemoveTrash();
        codex.RewriteShims();
    }

    [Fact]
    public async Task App自己的假CodexExe佔住檔案_屏障等它結束才跑npm()
    {
        using var temp = new TempDirectory("cli npm barrier");
        var codex = new CodexNpmLayout(temp, "0.160.1");
        var vendorExe = codex.Installation.NativeExecutable!;
        File.Copy(PingExe, vendorExe, overwrite: true);
        var commands = FakeCliCommands.For(codex);
        commands.Npm = (_, _) =>
        {
            InstallVersion(codex, "0.162.0");
            return new CliRunResult(0, "changed 2 packages", string.Empty, false);
        };
        using var http = new HttpClient(new FakeRegistry { Latest = "0.162.0" });
        using var cli = Create(http, _ => codex.Installation, commands, new FakeProcesses(), AgentProvider.Codex, background: false,
            o => o with { AppProcessExitWait = TimeSpan.FromSeconds(30) });
        var participant = new RecordingParticipant();
        cli.UpdateParticipant = participant;
        await cli.CheckAsync(AgentProvider.Codex);

        using var holder = StartHolder(vendorExe);
        try
        {
            // 像 node 開的 codex.exe 一樣在 app 的工作物件裡（帳戶查詢的 app-server、剛關還沒結束的對話程序）
            ChildProcessJob.TryAssign(holder);
            ChildProcessJob.Contains(holder.Id).Should().BeTrue();

            var update = Task.Run(() => cli.UpdateAsync(AgentProvider.Codex));
            await WaitAsync(() => cli.Get(AgentProvider.Codex).UpdatePhase == CliUpdatePhase.WaitingForProcesses, "排空後等 app 自己的程序結束");
            await Task.Delay(800);
            cli.Get(AgentProvider.Codex).UpdatePhase.Should().Be(CliUpdatePhase.WaitingForProcesses, "app 自己的 codex.exe 還在跑（ChildProcessJob 的 PID 清單＋QueryFullProcessImageName）");
            commands.Count("install").Should().Be(0);

            Stop(holder);
            await update.WaitAsync(TimeSpan.FromSeconds(30));

            commands.Count("install").Should().Be(1, "程序結束後才換檔");
            participant.Finished.All.Single().Outcome.Should().Be(CliUpdateOutcome.Updated);
            cli.GetIdentity(AgentProvider.Codex)!.Version.Should().Be("0.162.0");
        }
        finally
        {
            Stop(holder);
        }
    }

    [Fact]
    public async Task 殘留被App以外的程序佔住_刪不掉就列出程序並中止_不跑npm()
    {
        using var temp = new TempDirectory("cli npm barrier");
        var codex = new CodexNpmLayout(temp, "0.160.1");
        var vendorExe = codex.Installation.NativeExecutable!;
        File.Copy(PingExe, vendorExe, overwrite: true);
        using var foreign = StartHolder(vendorExe);   // 例如終端機或 VS Code 開著的 codex：不在 app 的工作物件裡
        try
        {
            // 上一次 npm 更新：舊套件退役成 .codex-<雜湊>（執行中的 exe 所在資料夾可以改名），新版放好，退役資料夾刪不掉留成殘留
            codex.Retire();
            codex.WritePackage("0.161.0", "0.161.0", extraFiles: 3);
            var residue = Path.Combine(codex.Scope, ".codex-nsHu5pVi");
            Directory.Exists(residue).Should().BeTrue();

            var commands = FakeCliCommands.For(codex);
            using var http = new HttpClient(new FakeRegistry { Latest = "0.162.0" });
            using var cli = Create(http, _ => codex.Installation, commands, new FakeProcesses(), AgentProvider.Codex, background: false);
            var participant = new RecordingParticipant();
            cli.UpdateParticipant = participant;
            await cli.CheckAsync(AgentProvider.Codex);
            var current = cli.GetIdentity(AgentProvider.Codex)!;
            current.Version.Should().Be("0.161.0");

            await cli.UpdateAsync(AgentProvider.Codex);

            commands.Count("install").Should().Be(0, "殘留還在被執行：npm 改名會撞到它，逐檔搬 EBUSY 後還可能回滾成混版（研究 e2b）");
            var error = cli.Get(AgentProvider.Codex).ActionError;
            error.Should().Contain($"PID {foreign.Id}").And.Contain("codex.exe").And.Contain(".codex-nsHu5pVi").And.Contain("這次沒有更新");
            participant.Finished.All.Should().ContainSingle().Which.Should().Be(new CliUpdateCompletion(AgentProvider.Codex, CliUpdateOutcome.Cancelled, current, error),
                "沒有換檔：停下的對話以目前的版本接續");
            Directory.Exists(residue).Should().BeTrue();
        }
        finally
        {
            Stop(foreign);
        }
    }

    [Fact]
    public async Task 以cmd_call執行使用者的npmCmd_路徑有空白也照常_清掉沒人用的殘留()
    {
        using var temp = new TempDirectory("cli npm barrier");
        var codex = new CodexNpmLayout(temp, "0.160.1");
        codex.Prefix.Should().Contain(" ", "這個測試要驗證 npm.cmd 的路徑含空白時 cmd 的引號照常有效");
        // 上一次更新留下的殘留（舊程序都已結束）：換檔前清掉，免得 npm 的退役改名撞名
        var residue = Path.Combine(codex.Scope, ".codex-AbCd1234");
        Directory.CreateDirectory(Path.Combine(residue, "bin"));
        File.WriteAllText(Path.Combine(residue, "bin", "codex.js"), "// old");

        var vendor = Path.GetRelativePath(codex.Prefix, CliLocator.CodexVendorRootCandidates(codex.PackageRoot)[0]);
        var package = Path.GetRelativePath(codex.Prefix, codex.PackageRoot);
        var npmCmd = Path.Combine(codex.Prefix, "npm.cmd");
        var script = string.Join("\r\n",
            "@echo off",
            ">\"%~dp0npm-args.txt\" echo %*",
            $">\"%~dp0{package}\\package.json\" echo {{\"name\": \"@openai/codex\", \"version\": \"0.162.0\", \"bin\": {{\"codex\": \"bin/codex.js\"}}}}",
            $">\"%~dp0{vendor}\\codex-package.json\" echo {{\"layoutVersion\": 1, \"version\": \"0.162.0\", \"entrypoint\": \"bin/codex.exe\"}}",
            $"del /f /q \"%~dp0{vendor}\\bin\\codex.exe\"",
            $">\"%~dp0{vendor}\\bin\\codex.exe\" echo codex 0.162.0",
            "exit /b 0",
            string.Empty);
        script.All(c => c < 128).Should().BeTrue("批次檔只寫 ASCII（cmd 以系統碼頁讀）");
        File.WriteAllText(npmCmd, script, Encoding.ASCII);

        var fake = FakeCliCommands.For(codex);
        CliCommandRunner runner = (launcher, arguments, environment, timeout, killWithApp, started, cancellationToken)
            => launcher.FileName.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase)
                ? CliRunner.RunAsync(launcher, arguments, environment, timeout, cancellationToken, killWithApp: killWithApp, started: started)
                : fake.Runner(launcher, arguments, environment, timeout, killWithApp, started, cancellationToken);
        using var http = new HttpClient(new FakeRegistry { Latest = "0.162.0" });
        using var cli = Create(http, _ => codex.Installation, fake, new FakeProcesses(), AgentProvider.Codex, background: false,
            o => o with { Run = runner, LocateNpm = () => npmCmd });
        var participant = new RecordingParticipant();
        cli.UpdateParticipant = participant;
        await cli.CheckAsync(AgentProvider.Codex);

        await cli.UpdateAsync(AgentProvider.Codex);

        File.ReadAllText(Path.Combine(codex.Prefix, "npm-args.txt")).Trim().Should().Be("install -g @openai/codex@latest");
        Directory.Exists(residue).Should().BeFalse("換檔前清掉 .codex-* 殘留");
        var finished = participant.Finished.All.Single();
        finished.Outcome.Should().Be(CliUpdateOutcome.Updated, cli.Get(AgentProvider.Codex).ActionError);
        finished.Identity!.Version.Should().Be("0.162.0");
    }
}
