using Myriagent.Tests.TestSupport;
using Myriagent.Update;

namespace Myriagent.Tests.Update;

public sealed class UpdaterStateRulesTests
{
    private static ReleaseIdentity Id(string release)
    {
        ReleaseIdentity.TryParseRelease(release, out var id).Should().BeTrue();
        return id!;
    }

    [Fact]
    public void 只裝比目前新而且不是壞版的()
    {
        var state = new UpdaterState { Bad = { "0.2.12-cccccc3" } };
        var current = Id("0.2.10-aaaaaa1");
        UpdaterStateRules.ShouldInstall(state, Id("0.2.11-bbbbbb2"), current).Should().BeTrue();
        UpdaterStateRules.ShouldInstall(state, Id("0.2.10-ffffff9"), current).Should().BeFalse("同版本不同 build 不算新");
        UpdaterStateRules.ShouldInstall(state, Id("0.2.9-bbbbbb2"), current).Should().BeFalse("不降版");
        UpdaterStateRules.ShouldInstall(state, Id("0.2.12-cccccc3"), current).Should().BeFalse("壞版不再裝");
    }

    [Fact]
    public void 連續三次沒到健康_第四次啟動切回前一版並標壞版()
    {
        var state = new UpdaterState { Current = "0.2.11-bbbbbb2", Previous = "0.2.10-aaaaaa1" };
        for (int i = 1; i <= 3; i++)
        {
            (state, var action) = UpdaterStateRules.OnLaunch(state, "0.2.11-bbbbbb2");
            action.Should().Be(LaunchAction.Proceed, $"第 {i} 次照常啟動");
        }
        (state, var fourth) = UpdaterStateRules.OnLaunch(state, "0.2.11-bbbbbb2");
        fourth.Should().Be(LaunchAction.RollBack);
        state.Current.Should().Be("0.2.10-aaaaaa1");
        state.Previous.Should().BeNull();
        state.Bad.Should().Contain("0.2.11-bbbbbb2");
        state.LaunchFailures.Should().NotContainKey("0.2.11-bbbbbb2");
        state.LastResult.Should().Contain("0.2.10");
    }

    [Fact]
    public void 達到健康就歸零()
    {
        var state = new UpdaterState { Current = "0.2.11-bbbbbb2", Previous = "0.2.10-aaaaaa1" };
        (state, _) = UpdaterStateRules.OnLaunch(state, "0.2.11-bbbbbb2");
        (state, _) = UpdaterStateRules.OnLaunch(state, "0.2.11-bbbbbb2");
        state = UpdaterStateRules.OnHealthy(state, "0.2.11-bbbbbb2");
        state.LaunchFailures.Should().NotContainKey("0.2.11-bbbbbb2");
        (_, var action) = UpdaterStateRules.OnLaunch(state, "0.2.11-bbbbbb2");
        action.Should().Be(LaunchAction.Proceed);
    }

    [Fact]
    public void 沒有前一版就不回滾_照常啟動()
    {
        var state = new UpdaterState { Current = "0.2.11-bbbbbb2" };
        for (int i = 0; i < 6; i++)
            (state, _) = UpdaterStateRules.OnLaunch(state, "0.2.11-bbbbbb2");
        UpdaterStateRules.OnLaunch(state, "0.2.11-bbbbbb2").Action.Should().Be(LaunchAction.Proceed);
    }

    [Fact]
    public void 健康閘門第一次失敗_允許再試一次_第二次才列壞版()
    {
        var state = new UpdaterState { Current = "0.2.10-aaaaaa1", Staged = "0.2.11-bbbbbb2" };
        state = UpdaterStateRules.OnGateFailed(state, "0.2.11-bbbbbb2", "0.2.10-aaaaaa1");
        state.Bad.Should().BeEmpty();
        state.Staged.Should().BeNull("回滾後這次的備妥作廢，下次檢查重新決定");
        UpdaterStateRules.ShouldInstall(state, Id("0.2.11-bbbbbb2"), Id("0.2.10-aaaaaa1")).Should().BeTrue();

        state = UpdaterStateRules.OnGateFailed(state, "0.2.11-bbbbbb2", "0.2.10-aaaaaa1");
        state.Bad.Should().Contain("0.2.11-bbbbbb2");
        state.Current.Should().Be("0.2.10-aaaaaa1");
        state.LastResult.Should().Contain("退回");
    }

    [Fact]
    public void 換版成功_目前與前一版前移_清掉備妥()
    {
        var state = UpdaterStateRules.OnSwitched(new UpdaterState { Current = "0.2.10-aaaaaa1", Staged = "0.2.11-bbbbbb2" }, "0.2.10-aaaaaa1", "0.2.11-bbbbbb2");
        state.Current.Should().Be("0.2.11-bbbbbb2");
        state.Previous.Should().Be("0.2.10-aaaaaa1");
        state.Staged.Should().BeNull();
        state.LastResult.Should().Contain("0.2.11");
    }

    [Fact]
    public void 換版時from與to相同_前一版不變_閃退保護仍能退回()
    {
        // current 早就指向新版（換版的空窗中別的實例的幫手已經換過）時又換一次：幫手的 from 是 current 的目標，等於 to
        var state = new UpdaterState { Current = "0.2.11-bbbbbb2", Previous = "0.2.10-aaaaaa1", Staged = "0.2.11-bbbbbb2" };

        state = UpdaterStateRules.OnSwitched(state, "0.2.11-bbbbbb2", "0.2.11-bbbbbb2");

        state.Previous.Should().Be("0.2.10-aaaaaa1", "Previous==Current 會讓閃退保護沒有版本可以退回");
        state.Current.Should().Be("0.2.11-bbbbbb2");
        state.Staged.Should().BeNull();
        state.LaunchFailures["0.2.11-bbbbbb2"] = UpdaterStateRules.LaunchFailureThreshold;
        UpdaterStateRules.OnLaunch(state, "0.2.11-bbbbbb2").Action.Should().Be(LaunchAction.RollBack);
    }

    [Fact]
    public void 清理清單不含目前版與前一版_保留三版()
    {
        var state = new UpdaterState { Current = "0.2.14-eeeeee5", Previous = "0.2.13-dddddd4" };
        var installed = new[] { "0.2.10-aaaaaa1", "0.2.11-bbbbbb2", "0.2.12-cccccc3", "0.2.13-dddddd4", "0.2.14-eeeeee5" };
        var delete = UpdaterStateRules.VersionsToDelete(state, installed, running: "0.2.14-eeeeee5");
        delete.Should().BeEquivalentTo(new[] { "0.2.10-aaaaaa1", "0.2.11-bbbbbb2" });
    }

    [Fact]
    public void 清理清單不含正在執行與已備妥的版本()
    {
        var state = new UpdaterState { Current = "0.2.14-eeeeee5", Previous = "0.2.13-dddddd4", Staged = "0.2.15-ffffff6" };
        var installed = new[] { "0.2.10-aaaaaa1", "0.2.13-dddddd4", "0.2.14-eeeeee5", "0.2.15-ffffff6" };
        UpdaterStateRules.VersionsToDelete(state, installed, running: "0.2.10-aaaaaa1").Should().BeEmpty();
    }

    [Fact]
    public void 壞版不占保留名額_退回後先刪壞版_留下較舊的好版本()
    {
        // 閃退保護退回 0.2.10 之後：沒有前一版，0.2.11 是壞版（之後不會再裝、也不會切回它）
        var state = new UpdaterState { Current = "0.2.10-aaaaaa1", Bad = { "0.2.11-bbbbbb2" } };
        var installed = new[] { "0.2.8-8888888", "0.2.9-9999999", "0.2.10-aaaaaa1", "0.2.11-bbbbbb2" };
        UpdaterStateRules.VersionsToDelete(state, installed, running: "0.2.10-aaaaaa1")
            .Should().BeEquivalentTo(new[] { "0.2.11-bbbbbb2" });
    }

    [Fact]
    public void 清理時資料夾被占用_跳過不丟例外()
    {
        using var temp = new TempDirectory("cleanup");
        var layout = new InstallLayout(temp.Root);
        var busy = layout.VersionDirectory("0.2.10-aaaaaa1");
        var free = layout.VersionDirectory("0.2.11-bbbbbb2");
        Directory.CreateDirectory(busy);
        Directory.CreateDirectory(free);
        File.WriteAllText(Path.Combine(busy, "Myriagent.exe"), "x");
        File.WriteAllText(Path.Combine(free, "Myriagent.exe"), "x");

        IReadOnlyList<string> deleted;
        using (new FileStream(Path.Combine(busy, "Myriagent.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
            deleted = UpdaterStateRules.DeleteVersions(layout, new[] { "0.2.10-aaaaaa1", "0.2.11-bbbbbb2" });

        deleted.Should().BeEquivalentTo(new[] { "0.2.11-bbbbbb2" });
        Directory.Exists(free).Should().BeFalse();
        Directory.Exists(busy).Should().BeTrue();
    }

    [Fact]
    public void 狀態檔讀寫往返_損毀時回預設值()
    {
        using var temp = new TempDirectory("state");
        var layout = new InstallLayout(temp.Root);
        UpdaterStateStore.Update(layout, s => UpdaterStateRules.OnStaged(s, "0.2.11-bbbbbb2"));
        UpdaterStateStore.Load(layout).Staged.Should().Be("0.2.11-bbbbbb2");
        File.WriteAllText(layout.StateFile, "{ broken");
        UpdaterStateStore.Load(layout).Staged.Should().BeNull();
    }

    [Fact]
    public void 狀態檔讀不到_更新丟例外_不呼叫修改也不覆寫()
    {
        using var temp = new TempDirectory("state");
        var layout = new InstallLayout(temp.Root);
        UpdaterStateStore.Update(layout, s => UpdaterStateRules.OnStaged(s, "0.2.11-bbbbbb2"));
        bool called = false;
        using (new FileStream(layout.StateFile, FileMode.Open, FileAccess.Read, FileShare.None))
            FluentActions.Invoking(() => UpdaterStateStore.Update(layout, s => { called = true; return new UpdaterState(); }))
                .Should().Throw<IOException>();
        called.Should().BeFalse();
        UpdaterStateStore.Load(layout).Staged.Should().Be("0.2.11-bbbbbb2");
    }

    [Fact]
    public async Task 讀取也等狀態檔的鎖_別的程序讀改寫時不會開著狀態檔()
    {
        using var temp = new TempDirectory("state");
        var layout = new InstallLayout(temp.Root);
        UpdaterStateStore.Update(layout, s => UpdaterStateRules.OnStaged(s, "0.2.11-bbbbbb2"));
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        // mutex 綁執行緒：拿鎖、放鎖都在同一條專用執行緒上（模擬換版幫手正在讀－改－寫）
        var holder = new Thread(() =>
        {
            using var mutex = new Mutex(false, layout.StateMutexName);
            mutex.WaitOne();
            held.Set();
            release.Wait();
            mutex.ReleaseMutex();
        }) { IsBackground = true };
        holder.Start();
        try
        {
            held.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

            var load = Task.Run(() => UpdaterStateStore.Load(layout));
            await Task.WhenAny(load, Task.Delay(TimeSpan.FromMilliseconds(300)));
            load.IsCompleted.Should().BeFalse("寫入端以取代換檔，讀的人開著檔案會讓它失敗，所以讀取要等鎖");

            release.Set();
            (await load.WaitAsync(TimeSpan.FromSeconds(5))).Staged.Should().Be("0.2.11-bbbbbb2");
        }
        finally
        {
            // 斷言失敗時也要放掉持鎖的執行緒，再處置它在等的事件
            release.Set();
            holder.Join(TimeSpan.FromSeconds(5));
        }
    }
}
