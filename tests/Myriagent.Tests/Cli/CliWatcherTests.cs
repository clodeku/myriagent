using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Tests.TestSupport;
using static Myriagent.Tests.Cli.CliWatcherTestKit;

namespace Myriagent.Tests.Cli;

/// <summary>
/// CliWatcher（Claude 原生安裝、輪詢、Reset、資料夾重建）：在暫存資料夾照研究實測的順序模擬換檔
/// （.new → 改名 .old.&lt;ms&gt;.&lt;pid&gt; → 改名成 claude.exe）、版本鎖、監看資料夾被刪後重建、去抖與連續更新只宣告一次。
/// 時間縮短（去抖 200 毫秒）；npm 的情境在 CliWatcherNpmTests，實際常數的 10 秒驗收（S2）在 CliWatcherRealTimingTests
/// </summary>
public sealed class CliWatcherTests
{
    [Fact]
    public async Task Start_有安裝就發一次Startup的Settled_監看Bin_Versions_Locks()
    {
        using var temp = new TempDirectory("cli-watch");
        var claude = new ClaudeLayout(temp);
        var recorder = new SignalRecorder();
        using var watcher = Watch(claude.Installation, Fast(new FakeProcesses(), AgentProvider.Claude), recorder);

        var settled = await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "啟動時的第一次檢查");
        settled.Trigger.Should().Be(CliWatchTrigger.Startup);
        settled.Installation.Should().Be(claude.Installation);
        settled.Snapshot!.FileVersion.Should().Be(ClaudeLayout.OldVersion);
        CliInstallStabilityRules.Evaluate(settled.Snapshot, null).Condition.Should().Be(CliInstallCondition.Stable);
        recorder.All.Should().ContainSingle("啟動時沒有在變動：不發 Changing");
        watcher.GetWatchedDirectories(AgentProvider.Claude).Should().BeEquivalentTo(claude.Bin, claude.Versions, claude.Locks);
    }

    [Fact]
    public async Task 沒有安裝_不發訊號也不監看()
    {
        var recorder = new SignalRecorder();
        using var watcher = Watch(null, Fast(new FakeProcesses(), AgentProvider.Codex), recorder);
        await Task.Delay(500);
        recorder.All.Should().BeEmpty();
        watcher.GetWatchedDirectories(AgentProvider.Codex).Should().BeEmpty();
    }

    [Fact]
    public async Task Claude換檔_一次Changing一次Settled_更新者PID取自檔名_宣告的是新檔()
    {
        using var temp = new TempDirectory("cli-watch");
        var claude = new ClaudeLayout(temp);
        var recorder = new SignalRecorder();
        using var watcher = Watch(claude.Installation, Fast(new FakeProcesses(), AgentProvider.Claude), recorder);
        var startup = await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "啟動時的第一次檢查");
        var mark = recorder.Count;

        claude.Swap(updaterPid: 48780, ClaudeLayout.NewSource);

        var settled = await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "換檔後安靜下來", skip: mark);
        await Task.Delay(Quiet * 3);
        var burst = recorder.All.Skip(mark).ToList();
        burst.Select(s => s.Kind).Should().Equal(new[] { CliWatchSignalKind.Changing, CliWatchSignalKind.Settled }, "一次更新只宣告一次");
        burst[0].Trigger.Should().Be(CliWatchTrigger.FileEvent);
        burst[0].UpdaterProcessId.Should().Be(48780, "第一個事件就是 .new.<pid> 的建立");
        settled.UpdaterProcessId.Should().Be(48780);
        settled.Trigger.Should().Be(CliWatchTrigger.FileEvent);

        settled.Snapshot!.Executable!.Value.FileId.Should().NotBe(startup.Snapshot!.Executable!.Value.FileId, "換上的是另一個檔案");
        settled.Snapshot.Executable.Should().Be(CliBinaryIdentityRules.TryReadStamp(claude.Exe));
        var verdict = CliInstallStabilityRules.Evaluate(settled.Snapshot, null);
        verdict.Condition.Should().Be(CliInstallCondition.Stable);
        verdict.Version.Should().Be(ClaudeLayout.NewVersion);
    }

    [Fact]
    public async Task 連續換檔只宣告一次_宣告的是最後一份()
    {
        using var temp = new TempDirectory("cli-watch");
        var claude = new ClaudeLayout(temp);
        var recorder = new SignalRecorder();
        // 去抖拉長到 1 秒：測試主機很忙時，兩次換檔之間的等待也不會超過去抖時間
        var quiet = TimeSpan.FromSeconds(1);
        using var watcher = Watch(claude.Installation, Fast(new FakeProcesses(), AgentProvider.Claude) with { ClaudeQuiet = quiet }, recorder);
        await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "啟動時的第一次檢查");
        var mark = recorder.Count;

        // 去抖時間內三次換檔（例如 claude update 之後互動式 claude 的背景更新又換了一次）
        claude.Swap(updaterPid: 100, ClaudeLayout.NewSource);
        await Task.Delay(30);
        claude.Swap(updaterPid: 200, ClaudeLayout.OldSource);
        await Task.Delay(30);
        claude.Swap(updaterPid: 300, ClaudeLayout.NewSource);

        var settled = await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "最後一次換檔後安靜下來", skip: mark);
        await Task.Delay(quiet * 2);
        recorder.All.Skip(mark).Select(s => s.Kind).Should().Equal(CliWatchSignalKind.Changing, CliWatchSignalKind.Settled);
        settled.UpdaterProcessId.Should().Be(100, "這一波第一個看到的更新者");
        settled.Snapshot!.Executable.Should().Be(CliBinaryIdentityRules.TryReadStamp(claude.Exe), "宣告的是安靜下來時的檔案");
        CliInstallStabilityRules.Evaluate(settled.Snapshot, null).Version.Should().Be(ClaudeLayout.NewVersion);

        // 宣告之後的下一次更新是新的一波
        var next = recorder.Count;
        claude.Swap(updaterPid: 400, ClaudeLayout.OldSource);
        await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "下一次換檔", skip: next);
        recorder.All.Skip(next).Select(s => s.Kind).Should().Equal(CliWatchSignalKind.Changing, CliWatchSignalKind.Settled);
    }

    [Fact]
    public async Task 宣告穩定後才到的事件_安裝沒變_不另開一波()
    {
        using var temp = new TempDirectory("cli-watch");
        var claude = new ClaudeLayout(temp);
        var recorder = new SignalRecorder();
        using var watcher = Watch(claude.Installation, Fast(new FakeProcesses(), AgentProvider.Claude), recorder);
        await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "啟動時的第一次檢查");
        var mark = recorder.Count;
        claude.Swap(updaterPid: 4100, ClaudeLayout.NewSource);
        await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "換檔後安靜下來", skip: mark);

        // 像慢機器上晚到的事件：版本鎖出現又消失（持有者早就結束），安裝跟剛宣告的一樣
        var lockFile = claude.WriteLock("2.1.297", pid: 4101);
        File.Delete(lockFile);
        await Task.Delay(Quiet * 4);
        recorder.All.Skip(mark).Select(s => s.Kind).Should().Equal(new[] { CliWatchSignalKind.Changing, CliWatchSignalKind.Settled },
            "宣告穩定後 EchoWindow 內的事件先查安裝：沒有改變就不另發 Changing／Settled");
    }

    [Fact]
    public async Task 宣告穩定後不久又真的換檔_查過安裝後照常宣告_帶這次的更新者()
    {
        using var temp = new TempDirectory("cli-watch");
        var claude = new ClaudeLayout(temp);
        var recorder = new SignalRecorder();
        using var watcher = Watch(claude.Installation, Fast(new FakeProcesses(), AgentProvider.Claude), recorder);
        await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "啟動時的第一次檢查");
        var mark = recorder.Count;
        claude.Swap(updaterPid: 4200, ClaudeLayout.NewSource);
        await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "第一次換檔安靜下來", skip: mark);

        var next = recorder.Count;
        claude.Swap(updaterPid: 4300, ClaudeLayout.OldSource);
        var changing = await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Changing, "緊接著又換了一次", skip: next);
        var settled = await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "第二次換檔安靜下來", skip: next);
        changing.UpdaterProcessId.Should().Be(4300, "查過安裝後開的一波仍帶觸發事件的更新者");
        recorder.All.Skip(next).Select(s => s.Kind).Should().Equal(CliWatchSignalKind.Changing, CliWatchSignalKind.Settled);
        CliInstallStabilityRules.Evaluate(settled.Snapshot!, null).Version.Should().Be(ClaudeLayout.OldVersion);
    }

    [Fact]
    public async Task 版本鎖持有中不宣告_鎖刪掉才宣告_PID取自鎖的JSON()
    {
        using var temp = new TempDirectory("cli-watch");
        var claude = new ClaudeLayout(temp);
        var processes = new FakeProcesses();
        var recorder = new SignalRecorder();
        using var watcher = Watch(claude.Installation, Fast(processes, AgentProvider.Claude), recorder);
        await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "啟動時的第一次檢查");
        var mark = recorder.Count;

        // 互動式 claude 的背景更新：取得版本鎖 → 下載（監看的資料夾沒有事件）→ 放 versions → 換檔 → 釋放鎖
        processes.Start(777, claude.Exe + ".old.1791575087343.777");
        var lockFile = claude.WriteLock("2.1.297", pid: 777);
        await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Changing, "取得版本鎖＝開始安裝", skip: mark);
        await Task.Delay(Quiet * 4);
        claude.Swap(updaterPid: 777, ClaudeLayout.NewSource);
        await Task.Delay(Quiet * 4);
        recorder.All.Skip(mark).Should().NotContain(s => s.Kind == CliWatchSignalKind.Settled, "鎖還在：安裝還沒結束");

        File.Delete(lockFile);
        var settled = await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "釋放鎖", skip: mark);
        settled.UpdaterProcessId.Should().Be(777);
        recorder.All.Skip(mark).Count(s => s.Kind == CliWatchSignalKind.Changing).Should().Be(1);
        CliInstallStabilityRules.Evaluate(settled.Snapshot!, null).Version.Should().Be(ClaudeLayout.NewVersion);
    }

    [Fact]
    public async Task 過期的版本鎖_持有者已結束_不擋宣告()
    {
        using var temp = new TempDirectory("cli-watch");
        var claude = new ClaudeLayout(temp);
        var recorder = new SignalRecorder();
        using var watcher = Watch(claude.Installation, Fast(new FakeProcesses(), AgentProvider.Claude), recorder);
        await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "啟動時的第一次檢查");
        var mark = recorder.Count;

        claude.WriteLock("2.1.297", pid: 999);   // 沒有這個程序
        claude.Swap(updaterPid: 999, ClaudeLayout.NewSource);
        var settled = await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "過期的鎖不算進行中", skip: mark);
        CliInstallStabilityRules.Evaluate(settled.Snapshot!, null).Condition.Should().Be(CliInstallCondition.Stable);
    }

    [Fact]
    public async Task Start時正在安裝_先發Changing_裝完才Settled()
    {
        using var temp = new TempDirectory("cli-watch");
        var claude = new ClaudeLayout(temp);
        var processes = new FakeProcesses();
        processes.Start(555, @"C:\Users\U\.local\bin\claude.exe");
        var lockFile = claude.WriteLock("2.1.297", pid: 555);
        var recorder = new SignalRecorder();
        using var watcher = Watch(claude.Installation, Fast(processes, AgentProvider.Claude), recorder);

        var changing = await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Changing, "啟動時看到版本鎖");
        changing.Trigger.Should().Be(CliWatchTrigger.Startup);
        changing.UpdaterProcessId.Should().Be(555);
        await Task.Delay(Quiet * 3);
        recorder.All.Should().ContainSingle();

        File.Delete(lockFile);
        var settled = await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "安裝結束");
        settled.Trigger.Should().Be(CliWatchTrigger.Startup);
    }

    [Fact]
    public async Task 重查才看到變穩定_再安靜一個去抖時間才宣告_晚到的檔案事件併進同一波()
    {
        using var temp = new TempDirectory("cli-watch");
        var claude = new ClaudeLayout(temp);
        var processes = new FakeProcesses();
        processes.Start(556, @"C:\Users\U\.local\bin\claude.exe");
        var lockFile = claude.WriteLock("2.1.297", pid: 556);
        var recorder = new SignalRecorder();
        var quiet = TimeSpan.FromMilliseconds(600);
        // 不收檔案事件：刪鎖只能靠每 50 ms 一次的重查看到，等於「刪鎖的事件還沒送到，重查就先看到鎖不見了」
        using var watcher = Watch(claude.Installation, Fast(processes, AgentProvider.Claude, fileEvents: false) with { ClaudeQuiet = quiet }, recorder);
        await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Changing, "啟動時看到版本鎖");
        // 先過完開頭的去抖：之後鎖還在時每 50 ms 重查一次
        await Task.Delay(quiet * 2);
        recorder.All.Should().ContainSingle("鎖還在：安裝還沒結束");

        var deletedAt = DateTime.UtcNow;
        File.Delete(lockFile);
        var settled = await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "安裝結束");
        (settled.AtUtc - deletedAt).Should().BeGreaterThanOrEqualTo(quiet - TimeSpan.FromMilliseconds(50),
            "重查看到穩定後還要再安靜一個去抖時間，晚到的事件才會落在這一波裡，而不是宣告後再開一波");
        recorder.All.Select(s => s.Kind).Should().Equal(CliWatchSignalKind.Changing, CliWatchSignalKind.Settled);
    }

    [Fact]
    public async Task 監看的資料夾被刪掉_改等上層_資料夾回來後重建監看()
    {
        using var temp = new TempDirectory("cli-watch");
        var claude = new ClaudeLayout(temp);
        var processes = new FakeProcesses();
        var recorder = new SignalRecorder();
        using var watcher = Watch(claude.Installation, Fast(processes, AgentProvider.Claude), recorder);
        await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "啟動時的第一次檢查");
        watcher.GetWatchedDirectories(AgentProvider.Claude).Should().Contain(claude.Locks);

        Directory.Delete(claude.Locks);
        await WaitUntilAsync(() => watcher.GetAnchorDirectories(AgentProvider.Claude).Contains(Path.GetDirectoryName(claude.Locks)!),
            "locks 被刪：watcher 丟 Error 後改監看上層（state\\claude）等它回來");
        watcher.GetWatchedDirectories(AgentProvider.Claude).Should().NotContain(claude.Locks);
        await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled && s.Trigger == CliWatchTrigger.WatcherRebuilt, "監看中斷要全量檢查");

        var mark = recorder.Count;
        Directory.CreateDirectory(claude.Locks);
        await WaitUntilAsync(() => watcher.GetWatchedDirectories(AgentProvider.Claude).Contains(claude.Locks), "locks 回來後重建監看");
        // 重建後的全量檢查：上一波剛宣告穩定（EchoWindow 內），先查安裝——沒變就不另開一波
        await Task.Delay(Quiet * 4);
        recorder.All.Skip(mark).Should().BeEmpty("資料夾回來時安裝沒變：查過就不另發 Changing／Settled");

        // 重建的 watcher 真的在工作：新的版本鎖會開始一波變動
        mark = recorder.Count;
        processes.Start(321, claude.Exe);
        claude.WriteLock("2.1.298", pid: 321);
        var changing = await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Changing, "重建後的監看收到事件", skip: mark);
        changing.Trigger.Should().Be(CliWatchTrigger.FileEvent);
    }

    [Fact]
    public async Task 監看的資料夾一開始不存在_出現後開始監看並全量檢查()
    {
        using var temp = new TempDirectory("cli-watch");
        var claude = new ClaudeLayout(temp, createDataDirectories: false);
        var processes = new FakeProcesses();
        var recorder = new SignalRecorder();
        using var watcher = Watch(claude.Installation, Fast(processes, AgentProvider.Claude), recorder);
        await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "啟動時的第一次檢查");
        watcher.GetWatchedDirectories(AgentProvider.Claude).Should().Equal(claude.Bin);
        watcher.GetAnchorDirectories(AgentProvider.Claude).Should().AllBe(Path.Combine(claude.Home, ".local"), "share、state 都還沒有");

        // 第一次更新時安裝程式才建立 locks（mkdir -p）並馬上寫入鎖：建立監看前的事件漏了也要看得到
        var mark = recorder.Count;
        processes.Start(654, claude.Exe);
        Directory.CreateDirectory(claude.Locks);
        claude.WriteLock("2.1.297", pid: 654);
        await WaitUntilAsync(() => watcher.GetWatchedDirectories(AgentProvider.Claude).Contains(claude.Locks), "locks 出現後開始監看");
        var changing = await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Changing, "全量檢查看到版本鎖", skip: mark);
        changing.Trigger.Should().BeOneOf(new[] { CliWatchTrigger.WatcherRebuilt, CliWatchTrigger.FileEvent }, "監看建立前寫的鎖靠重建後的全量檢查，之後寫的靠事件");
        await Task.Delay(Quiet * 3);
        recorder.All.Skip(mark).Should().NotContain(s => s.Kind == CliWatchSignalKind.Settled, "鎖還在：安裝還沒結束");
        watcher.GetAnchorDirectories(AgentProvider.Claude).Should().Equal(new[] { Path.Combine(claude.Home, ".local") }, "versions 還不存在，繼續等");
    }

    [Fact]
    public async Task 收不到檔案事件時_60秒輪詢發現換檔()
    {
        using var temp = new TempDirectory("cli-watch");
        var claude = new ClaudeLayout(temp);
        var recorder = new SignalRecorder();
        using var watcher = Watch(claude.Installation, Fast(new FakeProcesses(), AgentProvider.Claude, poll: TimeSpan.FromMilliseconds(300), fileEvents: false), recorder);
        await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "啟動時的第一次檢查");
        await Task.Delay(800);
        var mark = recorder.Count;
        mark.Should().Be(1, "沒有變化時輪詢不發訊號");

        claude.Swap(updaterPid: 4242, ClaudeLayout.NewSource);
        var settled = await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "輪詢發現指紋變了", skip: mark);
        settled.Trigger.Should().Be(CliWatchTrigger.Poll);
        recorder.All.Skip(mark).First().Kind.Should().Be(CliWatchSignalKind.Changing);
        CliInstallStabilityRules.Evaluate(settled.Snapshot!, null).Version.Should().Be(ClaudeLayout.NewVersion);
    }

    [Fact]
    public async Task Reset_安裝位置變了_重建監看並宣告新的安裝()
    {
        using var temp = new TempDirectory("cli-watch");
        var claude = new ClaudeLayout(temp);
        var other = temp.Combine("tools", "claude.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.Copy(ClaudeLayout.NewSource, other);
        var otherInstallation = new CliInstallation(AgentProvider.Claude, new AgentLauncher(other, Array.Empty<string>()), CliInstallKind.Other, other) { NativeExecutable = other };

        var current = claude.Installation;
        var recorder = new SignalRecorder();
        using var watcher = new CliWatcher(_ => current, Fast(new FakeProcesses(), AgentProvider.Claude));
        watcher.Signal += recorder.Add;
        watcher.Start();
        await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "啟動時的第一次檢查");
        var mark = recorder.Count;

        current = otherInstallation;
        watcher.Reset(AgentProvider.Claude);
        var settled = await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "Reset 後全量檢查", skip: mark);
        settled.Trigger.Should().Be(CliWatchTrigger.Reset);
        settled.Installation.Should().Be(otherInstallation);
        CliInstallStabilityRules.Evaluate(settled.Snapshot!, null).Version.Should().Be(ClaudeLayout.NewVersion);
        watcher.GetWatchedDirectories(AgentProvider.Claude).Should().Equal(Path.GetDirectoryName(other));
    }

    [Fact]
    public async Task 輪詢發現安裝位置變了_自己重建監看()
    {
        using var temp = new TempDirectory("cli-watch");
        var claude = new ClaudeLayout(temp);
        CliInstallation? current = null;
        var recorder = new SignalRecorder();
        using var watcher = new CliWatcher(_ => current, Fast(new FakeProcesses(), AgentProvider.Claude, poll: TimeSpan.FromMilliseconds(200)));
        watcher.Signal += recorder.Add;
        watcher.Start();
        await Task.Delay(400);
        recorder.All.Should().BeEmpty("還沒安裝");

        current = claude.Installation;   // app 開著時才裝好
        var settled = await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "輪詢找到新安裝");
        settled.Trigger.Should().Be(CliWatchTrigger.Poll);
        await WaitUntilAsync(() => watcher.GetWatchedDirectories(AgentProvider.Claude).Contains(claude.Bin), "開始監看新安裝");
    }

    [Fact]
    public void 真的程序查詢_不用WMI讀得到命令列建立時間與執行檔_接上版本鎖的判斷()
    {
        var self = CliProcessQueries.TryGetProcessInfo(Environment.ProcessId)!.Value;
        self.Id.Should().Be(Environment.ProcessId);
        self.ImagePath.Should().BeEquivalentTo(Environment.ProcessPath, "QueryFullProcessImageName");
        self.StartTimeUtc.Should().BeBefore(DateTime.UtcNow);
        CliProcessQueries.TryGetCommandLine(Environment.ProcessId).Should().NotBeNullOrWhiteSpace("NtQueryInformationProcess(ProcessCommandLineInformation)");
        CliProcessQueries.TryGetProcessInfo(0).Should().BeNull();
        CliProcessQueries.FindNpmProcesses().Where(p => !p.CommandLine.Contains("npm-cli.js", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty("只回命令列含 npm-cli.js 的 node（通常沒有在跑的 npm）");

        int exitedPid;
        using (var exited = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!)
        {
            exited.WaitForExit();
            exitedPid = exited.Id;
        }
        CliProcessQueries.TryGetProcessInfo(exitedPid).Should().BeNull("已結束的程序");

        // 真的程序查詢接上鎖的判斷：持有者活著、執行檔就是鎖裡的 execPath → 持有中；持有者已結束 → 過期
        using var temp = new TempDirectory("cli-watch");
        var claude = new ClaudeLayout(temp);
        claude.WriteLock("2.1.297", Environment.ProcessId, execPath: Environment.ProcessPath);
        claude.WriteLock("2.1.298", exitedPid);
        var snapshot = new CliInstallInspector(NoEnvironment).Inspect(AgentProvider.Claude, claude.Installation, DateTime.UtcNow);
        snapshot.Locks.Should().HaveCount(2);
        var live = snapshot.Locks.Single(o => o.Lock.Name == "2.1.297.lock");
        live.Holder!.Value.Id.Should().Be(Environment.ProcessId);
        CliInstallStabilityRules.IsLockHeld(live.Lock, live.Holder, snapshot.InspectedAtUtc).Should().BeTrue();
        var dead = snapshot.Locks.Single(o => o.Lock.Name == "2.1.298.lock");
        dead.Holder.Should().BeNull();
        CliInstallStabilityRules.IsLockHeld(dead.Lock, dead.Holder, snapshot.InspectedAtUtc).Should().BeFalse();
        var verdict = CliInstallStabilityRules.Evaluate(snapshot, null);
        verdict.Condition.Should().Be(CliInstallCondition.Changing);
        verdict.UpdaterProcessId.Should().Be(Environment.ProcessId);
    }

    [Fact]
    public async Task Dispose之後不再發訊號()
    {
        using var temp = new TempDirectory("cli-watch");
        var claude = new ClaudeLayout(temp);
        var recorder = new SignalRecorder();
        var watcher = Watch(claude.Installation, Fast(new FakeProcesses(), AgentProvider.Claude), recorder);
        await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "啟動時的第一次檢查");
        watcher.Dispose();
        var mark = recorder.Count;

        claude.Swap(updaterPid: 1, ClaudeLayout.NewSource);
        await Task.Delay(Quiet * 4);
        recorder.Count.Should().Be(mark);
        watcher.GetWatchedDirectories(AgentProvider.Claude).Should().BeEmpty();
        watcher.Dispose();
    }
}

/// <summary>CliWatcher（npm 版 Codex）：retire 改名、下載空檔、大量建立與緩衝溢位、刪殘留、重寫殼；rollback 混版只宣告損壞（去抖 300 毫秒）</summary>
public sealed class CliWatcherNpmTests
{
    [Fact]
    public async Task Npm換套件_Retire改名_下載空檔npm在跑時不宣告_大量建立刪殘留重寫殼後只宣告一次()
    {
        using var temp = new TempDirectory("cli-watch");
        var codex = new CodexNpmLayout(temp, "0.160.1");
        var processes = new FakeProcesses();
        var recorder = new SignalRecorder();
        using var watcher = Watch(codex.Installation, Fast(processes, AgentProvider.Codex), recorder);
        var startup = await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "啟動時的第一次檢查");
        CliInstallStabilityRules.Evaluate(startup.Snapshot!, "codex-cli 0.160.1").Version.Should().Be("0.160.1");
        var mark = recorder.Count;

        // npm install -g @openai/codex@latest（研究 E2 的順序）
        processes.StartNpm(9001, "\"node\"  \"" + temp.Combine("npm") + "\\\\node_modules\\npm\\bin\\npm-cli.js\" install -g @openai/codex@latest");
        codex.Retire();
        await Task.Delay(NpmQuiet * 4);   // 下載中：套件不在、只有退役資料夾
        codex.WritePackage("0.162.0", "0.162.0", extraFiles: 300);
        codex.RemoveTrash();
        codex.RewriteShims();
        await Task.Delay(NpmQuiet * 3);
        recorder.All.Skip(mark).Should().NotContain(s => s.Kind == CliWatchSignalKind.Settled, "npm 還在跑");

        processes.ExitNpm();
        var settled = await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "npm 結束後安靜下來", skip: mark);
        await Task.Delay(NpmQuiet * 3);
        recorder.All.Skip(mark).Select(s => s.Kind).Should().Equal(new[] { CliWatchSignalKind.Changing, CliWatchSignalKind.Settled }, "retire、下載、解壓、收尾是同一次更新");
        settled.UpdaterProcessId.Should().Be(9001, "npm 的檔名沒有 PID：取自檢查時看到的 npm 程序");
        var verdict = CliInstallStabilityRules.Evaluate(settled.Snapshot!, "codex-cli 0.162.0");
        verdict.Condition.Should().Be(CliInstallCondition.Stable);
        verdict.Version.Should().Be("0.162.0");
        settled.Snapshot!.RetiredPackages.Should().BeEmpty();
    }

    [Fact]
    public async Task Npm一次解壓幾千個檔_緩衝溢位也只宣告一次_宣告的是新版()
    {
        using var temp = new TempDirectory("cli-watch");
        var codex = new CodexNpmLayout(temp, "0.160.1");
        var recorder = new SignalRecorder();
        // 去抖拉長到 1 秒：寫幾千個檔的期間事件不會斷
        using var watcher = Watch(codex.Installation, Fast(new FakeProcesses(), AgentProvider.Codex) with { NpmQuiet = TimeSpan.FromSeconds(1) }, recorder);
        await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "啟動時的第一次檢查");
        var mark = recorder.Count;

        codex.Retire();
        codex.WritePackage("0.162.0", "0.162.0", extraFiles: 3000);
        codex.RemoveTrash();
        codex.RewriteShims();

        var settled = await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "大量建立後安靜下來", skip: mark, seconds: 20);
        await Task.Delay(TimeSpan.FromSeconds(2));
        recorder.All.Skip(mark).Select(s => s.Kind).Should().Equal(new[] { CliWatchSignalKind.Changing, CliWatchSignalKind.Settled }, "溢位只是漏了事件，仍是同一次更新");
        CliInstallStabilityRules.Evaluate(settled.Snapshot!, "codex-cli 0.162.0").Version.Should().Be("0.162.0");
        watcher.GetWatchedDirectories(AgentProvider.Codex).Should().Contain(codex.Scope, "溢位後照常監看");
    }

    [Fact]
    public async Task Npm混版_不能用的結論要安靜一段時間才宣告_宣告的是損壞不是換版()
    {
        using var temp = new TempDirectory("cli-watch");
        var codex = new CodexNpmLayout(temp, "0.160.1");
        var recorder = new SignalRecorder();
        using var watcher = Watch(codex.Installation, Fast(new FakeProcesses(), AgentProvider.Codex), recorder);
        var startup = await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "啟動時的第一次檢查");
        var old = CliBinaryIdentityRules.TryRead(codex.Installation, "0.160.1")!;
        var mark = recorder.Count;

        // 研究 E2b：rollback 後 package.json 是新版、套件位置的 codex.exe 與 codex-package.json 是舊的
        codex.WritePackageJson("0.162.0");
        var written = Stopwatch.StartNew();
        var settled = await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "混版", skip: mark);
        written.Elapsed.Should().BeGreaterThan(Unusable * 0.8, "看起來不能用時要等 30 秒（測試縮成 1 秒）沒有新事件才宣告");

        var verdict = CliInstallStabilityRules.Evaluate(settled.Snapshot!, null);
        verdict.Condition.Should().Be(CliInstallCondition.Broken);
        verdict.Detail.Should().Be("Codex 安裝不一致（package.json 0.162.0、codex-package.json 0.160.1），請修復");
        var change = CliInstallStabilityRules.BuildVersionChange(AgentProvider.Codex, old, verdict, CliBinaryIdentityRules.TryRead(codex.Installation, "0.162.0"), CliChangeOrigin.External)!;
        change.IsVersionSwitch.Should().BeFalse("S2：混版時 0 次重開");
        change.InstallBroken.Should().BeTrue();
        startup.Snapshot!.Fingerprint.Should().NotBe(settled.Snapshot!.Fingerprint);
    }
}

/// <summary>CliWatcher 測試共用：縮短的時間、建立並啟動監看、等條件成立</summary>
internal static class CliWatcherTestKit
{
    public static readonly Func<string, string?> NoEnvironment = _ => null;

    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan NpmQuiet = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan Unusable = TimeSpan.FromSeconds(1);

    public static CliWatcherOptions Fast(FakeProcesses processes, AgentProvider provider, TimeSpan? poll = null, bool fileEvents = true) => new()
    {
        ClaudeQuiet = Quiet,
        NpmQuiet = NpmQuiet,
        UnusableQuiet = Unusable,
        Recheck = TimeSpan.FromMilliseconds(50),
        PollInterval = poll ?? Timeout.InfiniteTimeSpan,
        MaxSettleWait = TimeSpan.FromMinutes(1),
        Environment = NoEnvironment,
        Inspector = processes.Inspector,
        FileEvents = fileEvents,
        Providers = new[] { provider },
    };

    public static CliWatcher Watch(CliInstallation? installation, CliWatcherOptions options, SignalRecorder recorder)
    {
        var watcher = new CliWatcher(_ => installation, options);
        watcher.Signal += recorder.Add;
        watcher.Start();
        return watcher;
    }

    public static async Task WaitUntilAsync(Func<bool> condition, string because, double seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"{seconds} 秒內沒有等到：{because}");
            await Task.Delay(25);
        }
    }
}

/// <summary>S2 驗收：用實際的去抖常數（Claude 3 秒、npm 5 秒），換版完成後 10 秒內宣告已穩定的新身分</summary>
public sealed class CliWatcherRealTimingTests
{
    private static CliWatcherOptions Real(FakeProcesses processes, AgentProvider provider) => new()
    {
        PollInterval = Timeout.InfiniteTimeSpan,
        Environment = NoEnvironment,
        Inspector = processes.Inspector,
        Providers = new[] { provider },
    };

    [Fact]
    public async Task S2_Claude原生換檔完成後10秒內宣告已穩定的新身分()
    {
        using var temp = new TempDirectory("cli-watch");
        var claude = new ClaudeLayout(temp);
        var recorder = new SignalRecorder();
        using var watcher = new CliWatcher(_ => claude.Installation, Real(new FakeProcesses(), AgentProvider.Claude));
        watcher.Signal += recorder.Add;
        watcher.Start();
        var startup = await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "啟動時的第一次檢查");
        var old = CliBinaryIdentityRules.TryRead(claude.Installation, CliInstallStabilityRules.Evaluate(startup.Snapshot!, null).Version!)!;
        var mark = recorder.Count;

        claude.Swap(updaterPid: 48780, ClaudeLayout.NewSource);
        var elapsed = Stopwatch.StartNew();
        var settled = await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "換檔後宣告", skip: mark, seconds: 15);
        elapsed.Stop();

        elapsed.Elapsed.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(10), "S2：換版完成後 10 秒內");
        elapsed.Elapsed.Should().BeGreaterThanOrEqualTo(CliInstallStabilityRules.ClaudeQuietPeriod - TimeSpan.FromMilliseconds(100), "去抖 3 秒");
        var verdict = CliInstallStabilityRules.Evaluate(settled.Snapshot!, null);
        var identity = CliBinaryIdentityRules.TryRead(claude.Installation, verdict.Version!)!;
        var change = CliInstallStabilityRules.BuildVersionChange(AgentProvider.Claude, old, verdict, identity, CliChangeOrigin.External)!;
        change.IsVersionSwitch.Should().BeTrue("已穩定的新身分");
        change.ToVersion.Should().Be(ClaudeLayout.NewVersion);
    }

    [Fact]
    public async Task S2_npm換版完成後10秒內宣告已穩定的新身分()
    {
        using var temp = new TempDirectory("cli-watch");
        var codex = new CodexNpmLayout(temp, "0.160.1");
        var recorder = new SignalRecorder();
        using var watcher = new CliWatcher(_ => codex.Installation, Real(new FakeProcesses(), AgentProvider.Codex));
        watcher.Signal += recorder.Add;
        watcher.Start();
        await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "啟動時的第一次檢查");
        var mark = recorder.Count;

        codex.Retire();
        codex.WritePackage("0.162.0", "0.162.0", extraFiles: 50);
        codex.RemoveTrash();
        codex.RewriteShims();
        var elapsed = Stopwatch.StartNew();
        var settled = await recorder.WaitAsync(s => s.Kind == CliWatchSignalKind.Settled, "npm 換版後宣告", skip: mark, seconds: 15);
        elapsed.Stop();

        elapsed.Elapsed.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(10), "S2：換版完成後 10 秒內");
        CliInstallStabilityRules.Evaluate(settled.Snapshot!, "codex-cli 0.162.0").Version.Should().Be("0.162.0");
    }
}

/// <summary>收集 CliWatcher 的訊號（背景執行緒寫、測試執行緒讀）</summary>
internal sealed class SignalRecorder
{
    private readonly object gate = new();
    private readonly List<CliWatchSignal> signals = new();

    public void Add(CliWatchSignal signal)
    {
        lock (gate)
            signals.Add(signal);
    }

    public List<CliWatchSignal> All
    {
        get
        {
            lock (gate)
                return signals.ToList();
        }
    }

    public int Count
    {
        get
        {
            lock (gate)
                return signals.Count;
        }
    }

    /// <summary>等第 skip 個之後第一個符合的訊號</summary>
    public async Task<CliWatchSignal> WaitAsync(Func<CliWatchSignal, bool> match, string because, int skip = 0, double seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var found = All.Skip(skip).FirstOrDefault(match);
            if (found != null)
                return found;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"{seconds} 秒內沒有等到：{because}；收到的訊號：" + string.Join("、", All.Select(s => $"{s.Kind}/{s.Trigger}/{s.Detail}")));
            await Task.Delay(25);
        }
    }
}

/// <summary>假的程序查詢：哪些 PID 還活著（版本鎖的持有者）、哪些 npm 在跑</summary>
internal sealed class FakeProcesses
{
    private readonly ConcurrentDictionary<int, CliProcessInfo> alive = new();
    private volatile CliNpmProcess[] npm = Array.Empty<CliNpmProcess>();

    public CliInstallInspector Inspector => new(NoEnvironment, pid => alive.TryGetValue(pid, out var p) ? p : null, () => npm);

    public void Start(int pid, string image) => alive[pid] = new CliProcessInfo(pid, DateTime.UtcNow.AddMinutes(-1), image);

    public void StartNpm(int pid, string commandLine) => npm = new[] { new CliNpmProcess(pid, commandLine) };

    public void ExitNpm() => npm = Array.Empty<CliNpmProcess>();
}

/// <summary>暫存資料夾裡的 Claude 原生安裝：home\.local\bin\claude.exe、home\.local\share\claude\versions、home\.local\state\claude\locks</summary>
internal sealed class ClaudeLayout
{
    /// <summary>兩份有版本資源的真檔案（測試主機的組件），當成換版前後的 claude.exe</summary>
    public static string OldSource => typeof(CliWatcher).Assembly.Location;

    public static string NewSource => typeof(Newtonsoft.Json.JsonConvert).Assembly.Location;

    public static string OldVersion => CliBinaryIdentityRules.ReadClaudeFileVersion(OldSource)!;

    public static string NewVersion => CliBinaryIdentityRules.ReadClaudeFileVersion(NewSource)!;

    public ClaudeLayout(TempDirectory temp, bool createDataDirectories = true)
    {
        Home = temp.Combine("home");
        Bin = Path.Combine(Home, ".local", "bin");
        Versions = Path.Combine(Home, ".local", "share", "claude", "versions");
        Locks = Path.Combine(Home, ".local", "state", "claude", "locks");
        Exe = Path.Combine(Bin, "claude.exe");
        Directory.CreateDirectory(Bin);
        if (createDataDirectories)
        {
            Directory.CreateDirectory(Versions);
            Directory.CreateDirectory(Locks);
        }
        File.Copy(OldSource, Exe);
        Installation = new CliInstallation(AgentProvider.Claude, new AgentLauncher(Exe, Array.Empty<string>()), CliInstallKind.Native, Exe) { NativeExecutable = Exe };
        OldVersion.Should().NotBe(NewVersion, "兩份來源要是不同版本");
    }

    public string Home { get; }

    public string Bin { get; }

    public string Versions { get; }

    public string Locks { get; }

    public string Exe { get; }

    public CliInstallation Installation { get; }

    /// <summary>照 2.1.296 的換檔順序：複製成 .new.&lt;pid&gt;.&lt;ms&gt;.&lt;hex&gt; → 執行中的改名成 .old.&lt;ms&gt;.&lt;pid&gt; → .new 改名成 claude.exe</summary>
    public void Swap(int updaterPid, string source)
    {
        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var staged = $"{Exe}.new.{updaterPid}.{ms}.abcd1234";
        var backup = $"{Exe}.old.{ms}.{updaterPid}";
        File.Copy(source, staged);
        File.Move(Exe, backup);
        File.Move(staged, Exe);
    }

    /// <summary>寫版本鎖（2.1.296 的格式）；execPath 預設是這個安裝的 claude.exe</summary>
    public string WriteLock(string version, int pid, string? execPath = null)
    {
        Directory.CreateDirectory(Locks);
        var path = Path.Combine(Locks, version + ".lock");
        var json = "{\n  \"pid\": " + pid + ",\n  \"version\": \"" + version + "\",\n  \"execPath\": \"" + (execPath ?? Exe).Replace("\\", "\\\\") + "\",\n  \"acquiredAt\": "
                   + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "\n}";
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }
}

/// <summary>暫存資料夾裡的 npm 版 Codex：npm\codex.cmd、npm\node_modules\@openai\codex\（package.json、bin\codex.js、vendor 的 codex.exe 與 codex-package.json）</summary>
internal sealed class CodexNpmLayout
{
    private readonly string retired;

    public CodexNpmLayout(TempDirectory temp, string version)
    {
        Prefix = temp.Combine("npm");
        Scope = Path.Combine(Prefix, "node_modules", "@openai");
        PackageRoot = Path.Combine(Scope, "codex");
        retired = Path.Combine(Scope, ".codex-nsHu5pVi");
        Directory.CreateDirectory(Prefix);
        RewriteShims();
        WritePackage(version, version, extraFiles: 3);
        var js = Path.Combine(PackageRoot, "bin", "codex.js");
        Installation = new CliInstallation(AgentProvider.Codex, new AgentLauncher(@"C:\Program Files\nodejs\node.exe", new[] { js }), CliInstallKind.Npm, Path.Combine(Prefix, "codex.cmd"))
        {
            NativeExecutable = CliLocator.CodexNativeExecutable(PackageRoot, File.Exists),
        };
    }

    public string Prefix { get; }

    public string Scope { get; }

    public string PackageRoot { get; }

    public CliInstallation Installation { get; }

    private string VendorRoot => CliLocator.CodexVendorRootCandidates(PackageRoot)[0];

    /// <summary>npm retire：整個套件資料夾改名成 .codex-&lt;雜湊&gt;</summary>
    public void Retire() => Directory.Move(PackageRoot, retired);

    /// <summary>removeTrash：刪掉退役資料夾</summary>
    public void RemoveTrash() => Directory.Delete(retired, recursive: true);

    /// <summary>npm 收尾：重寫 codex、codex.cmd、codex.ps1</summary>
    public void RewriteShims()
    {
        foreach (var name in new[] { "codex", "codex.cmd", "codex.ps1" })
            File.WriteAllText(Path.Combine(Prefix, name), "\"%_prog%\"  \"%dp0%\\node_modules\\@openai\\codex\\bin\\codex.js\" %*");
    }

    /// <summary>解壓新版：套件的 package.json、bin\codex.js、vendor 的 codex.exe、codex-package.json，外加 extraFiles 個資源檔（大量建立）</summary>
    public void WritePackage(string packageVersion, string vendorVersion, int extraFiles)
    {
        var resources = Path.Combine(VendorRoot, "codex-resources", "voice", "bin");
        Directory.CreateDirectory(resources);
        for (int i = 0; i < extraFiles; i++)
            File.WriteAllText(Path.Combine(resources, $"lib{i}.dll"), new string('x', 256));
        Directory.CreateDirectory(Path.Combine(VendorRoot, "bin"));
        File.WriteAllText(Path.Combine(VendorRoot, "bin", "codex.exe"), "codex " + vendorVersion + Guid.NewGuid());
        File.WriteAllText(Path.Combine(VendorRoot, "codex-package.json"), "{\"layoutVersion\": 1, \"version\": \"" + vendorVersion + "\", \"entrypoint\": \"bin/codex.exe\"}");
        Directory.CreateDirectory(Path.Combine(PackageRoot, "bin"));
        File.WriteAllText(Path.Combine(PackageRoot, "bin", "codex.js"), "// codex.js");
        WritePackageJson(packageVersion);
    }

    public void WritePackageJson(string version)
        => File.WriteAllText(Path.Combine(PackageRoot, "package.json"), "{\"name\": \"@openai/codex\", \"version\": \"" + version + "\", \"bin\": {\"codex\": \"bin/codex.js\"}}");
}
