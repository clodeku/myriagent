using System.Diagnostics;
using Myriagent.Schedules;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Schedules;

/// <summary>
/// 讀真的工作排程器（TaskSchedulerReader：COM 的 Schedule.Service）。會在這台建立一個暫時的工作（根資料夾的 MyriagentTest-xxxx、停用、只會手動執行），
/// 結束時一定刪掉，所以預設不跑：<c>MYRIAGENT_SCHED_IT=1 dotnet test … --filter "FullyQualifiedName~TaskSchedulerReaderTests"</c>（約 10 秒）。
/// 找 git repo 與紀錄檔的部分不碰工作排程器，一律跑
/// </summary>
public sealed class TaskSchedulerReaderTests
{
    private const string Reason = "在這台的工作排程器建立、執行、刪除一個暫時的工作（約 10 秒）";

    [EnvFact("MYRIAGENT_SCHED_IT", Reason)]
    public async Task 讀到暫時建立的工作_立即執行讀得到結束碼_停用啟用()
    {
        using var temp = new TempDirectory("sched-it");
        var name = "MyriagentTest-" + Guid.NewGuid().ToString("N")[..8];
        var path = "\\" + name;
        var script = Path.Combine(temp.Root, "job.cmd");
        File.WriteAllText(script, "@echo off\r\nexit /b 3\r\n");
        // 一次性、2099 年才觸發：只會被測試手動執行
        Schtasks($"/create /tn \"{name}\" /tr \"\\\"{script}\\\"\" /sc once /st 23:59 /sd 2099/12/31 /f").Should().Be(0);
        try
        {
            var reader = new TaskSchedulerReader();
            var scan = reader.Scan(CancellationToken.None);
            scan.Error.Should().BeNull();
            scan.Tasks.Should().NotContain(t => t.Path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase), "系統資料夾不進去");
            var task = scan.Tasks.Single(t => t.Path == path);
            task.Definition.Should().NotBeNull();
            task.Definition!.Actions.Single().Command.Should().Contain("job.cmd");
            task.Definition.Triggers.Single().Kind.Should().Be(ScheduleTriggerKind.Once);
            task.LastResult.Should().Be(ScheduleResultRules.HasNotRun);
            task.Enabled.Should().BeTrue();
            ScheduleClassifyRules.Classify(new ScheduleClassifyInput(task.Path, task.Definition.Author, task.Definition.Actions, task.RepoRoot != null, scan.Environment))
                .Should().Be(ScheduleOrigin.Other, "腳本在 %TEMP%（AppData 底下）：照規則算廠商位置");
            var withWork = scan.Environment with { WorkRoots = new[] { temp.Root } };
            ScheduleClassifyRules.Classify(new ScheduleClassifyInput(task.Path, task.Definition.Author, task.Definition.Actions, false, withWork))
                .Should().Be(ScheduleOrigin.Suspected, "工作區（例如 Myriagent 對話的工作區）裡的腳本不算廠商的");

            reader.Run(path);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            ScheduledTaskInfo? done = null;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(300);
                done = reader.Scan(CancellationToken.None).Tasks.Single(t => t.Path == path);
                if (done.LastRun != null && ScheduleResultRules.IsFinal(done.LastResult) && done.State != ScheduledTaskState.Running)
                    break;
            }
            done!.LastResult.Should().Be(3);
            ScheduleResultRules.Classify(done.LastResult).Text.Should().Be("結束碼 3");

            reader.SetEnabled(path, false);
            reader.Scan(CancellationToken.None).Tasks.Single(t => t.Path == path).Enabled.Should().BeFalse();
            reader.SetEnabled(path, true);
            reader.Scan(CancellationToken.None).Tasks.Single(t => t.Path == path).Enabled.Should().BeTrue();

            var missing = () => reader.Run(@"\MyriagentTest-不存在的工作");
            missing.Should().Throw<InvalidOperationException>().WithMessage("*找不到這個排程*");
        }
        finally
        {
            Schtasks($"/delete /tn \"{name}\" /f");
        }
    }

    [Fact]
    public void 找git_repo_往上找到點git_結果記在快取()
    {
        using var temp = new TempDirectory("sched-repo");
        var repo = Path.Combine(temp.Root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var tool = Path.Combine(repo, "tools", "x");
        Directory.CreateDirectory(tool);
        var cache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        TaskSchedulerReader.RepoRootOf(Path.Combine(tool, "run.py"), cache).Should().Be(repo);
        cache.Should().ContainKey(tool);
        TaskSchedulerReader.RepoRootOf(Path.Combine(temp.Root, "other", "a.ps1"), cache).Should().BeNull();
        TaskSchedulerReader.RepoRootOf(@"\\server\share\x.cmd", cache).Should().BeNull("UNC 一律不碰");

        // worktree：.git 是檔案
        var wt = Path.Combine(temp.Root, "wt");
        Directory.CreateDirectory(wt);
        File.WriteAllText(Path.Combine(wt, ".git"), "gitdir: x");
        TaskSchedulerReader.RepoRootOf(Path.Combine(wt, "a.cmd"), new Dictionary<string, string?>()).Should().Be(wt);
    }

    [Fact]
    public void 紀錄檔_執行紀錄附加_重新載入_壞掉的那一行略過()
    {
        using var temp = new TempDirectory("sched-store");
        var dir = Path.Combine(temp.Root, "schedules");
        var store = ScheduleStore.Load(dir);
        var t0 = new DateTime(2026, 10, 26, 9, 0, 0);
        for (int i = 0; i < ScheduleRunRules.KeepPerTask + 5; i++)
            store.AppendRun(new ScheduleRunRecord(@"\A", t0.AddMinutes(i), i % 3 == 0 ? 1 : 0, t0.AddMinutes(i), false));
        store.AppendRun(new ScheduleRunRecord(@"\B", t0, 0, t0, true));
        store.State.Ignored.Add(@"\C");
        store.State.Links[@"\A"] = "conv1";
        store.Save();
        File.AppendAllText(Path.Combine(dir, "runs.jsonl"), "{\"Path\":\"\\\\A\",\"At\":");

        var again = ScheduleStore.Load(dir);
        again.RunsOf(@"\A").Should().HaveCount(ScheduleRunRules.KeepPerTask);
        again.LastRunOf(@"\A")!.At.Should().Be(t0.AddMinutes(ScheduleRunRules.KeepPerTask + 4));
        again.RunsOf(@"\b").Single().Gap.Should().BeTrue("路徑不分大小寫");
        again.State.Ignored.Should().Equal(@"\C");
        again.State.Links[@"\a"].Should().Be("conv1");
    }

    private static int Schtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return p.ExitCode;
    }
}
