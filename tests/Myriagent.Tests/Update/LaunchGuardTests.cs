using Myriagent.Tests.TestSupport;
using Myriagent.Update;

namespace Myriagent.Tests.Update;

public sealed class LaunchGuardTests
{
    [Fact]
    public void 連續三次沒到健康_第四次切回前一版並啟動它()
    {
        using var temp = new TempDirectory("guard");
        var layout = new InstallLayout(temp.Combine("Programs"));
        temp.WriteFile(Path.Combine("Programs", "versions", "0.2.1-aaaaaa1", "Myriagent.exe"));
        temp.WriteFile(Path.Combine("Programs", "versions", "0.2.2-bbbbbb2", "Myriagent.exe"));
        Junction.Create(layout.Current, layout.VersionDirectory("0.2.2-bbbbbb2"));
        UpdaterStateStore.Update(layout, s =>
        {
            s.Current = "0.2.2-bbbbbb2";
            s.Previous = "0.2.1-aaaaaa1";
            return s;
        });
        var launches = new List<(string Exe, IReadOnlyList<string> Args)>();
        int? Launch(string exe, IReadOnlyList<string> args)
        {
            launches.Add((exe, args));
            return 1;
        }

        for (int i = 0; i < 3; i++)
            LaunchGuard.RollBackIfCrashing(layout, "0.2.2-bbbbbb2", Launch).Should().BeFalse();
        LaunchGuard.RollBackIfCrashing(layout, "0.2.2-bbbbbb2", Launch).Should().BeTrue();

        Junction.GetTarget(layout.Current).Should().BeEquivalentTo(layout.VersionDirectory("0.2.1-aaaaaa1"));
        launches.Should().ContainSingle().Which.Exe.Should().Be(layout.CurrentExecutable);
        UpdaterStateStore.Load(layout).Bad.Should().Contain("0.2.2-bbbbbb2");
    }

    [Fact]
    public void 到健康就歸零_並寫健康訊號()
    {
        using var temp = new TempDirectory("guard");
        var layout = new InstallLayout(temp.Root);
        UpdaterStateStore.Update(layout, s =>
        {
            s.Current = "0.2.2-bbbbbb2";
            s.Previous = "0.2.1-aaaaaa1";
            return s;
        });
        LaunchGuard.RollBackIfCrashing(layout, "0.2.2-bbbbbb2", (_, _) => 1);
        var token = StartupOptions.NewToken();

        LaunchGuard.MarkHealthy(layout, "0.2.2-bbbbbb2", token);

        UpdaterStateStore.Load(layout).LaunchFailures.Should().NotContainKey("0.2.2-bbbbbb2");
        File.Exists(layout.HealthFile(token)).Should().BeTrue();
    }

    [Theory]
    [InlineData("current 不是 junction")]
    [InlineData("前一版的程式不在")]
    public void 切不回前一版_照常啟動不丟例外_狀態不寫_下次再試(string failure)
    {
        using var temp = new TempDirectory("guard");
        var layout = new InstallLayout(temp.Combine("Programs"));
        temp.WriteFile(Path.Combine("Programs", "versions", "0.2.2-bbbbbb2", "Myriagent.exe"));
        if (failure == "current 不是 junction")
        {
            temp.WriteFile(Path.Combine("Programs", "versions", "0.2.1-aaaaaa1", "Myriagent.exe"));
            temp.CreateDirectory(Path.Combine("Programs", "current"));
        }
        else
        {
            temp.CreateDirectory(Path.Combine("Programs", "versions", "0.2.1-aaaaaa1"));
            Junction.Create(layout.Current, layout.VersionDirectory("0.2.2-bbbbbb2"));
        }
        UpdaterStateStore.Update(layout, s =>
        {
            s.Current = "0.2.2-bbbbbb2";
            s.Previous = "0.2.1-aaaaaa1";
            s.LaunchFailures["0.2.2-bbbbbb2"] = UpdaterStateRules.LaunchFailureThreshold;
            return s;
        });
        bool launched = false;

        var act = () => LaunchGuard.RollBackIfCrashing(layout, "0.2.2-bbbbbb2", (_, _) =>
        {
            launched = true;
            return 1;
        });

        act.Should().NotThrow().Subject.Should().BeFalse("切不回去就照常啟動這一版");
        launched.Should().BeFalse();
        var state = UpdaterStateStore.Load(layout);
        state.Current.Should().Be("0.2.2-bbbbbb2", "切不回去就不寫退回的狀態，下次啟動再試");
        state.Previous.Should().Be("0.2.1-aaaaaa1");
        state.Bad.Should().BeEmpty();
        state.LaunchFailures["0.2.2-bbbbbb2"].Should().Be(UpdaterStateRules.LaunchFailureThreshold);
    }

    [Fact]
    public void 狀態檔被占用_照常啟動不丟例外_健康訊號照寫()
    {
        using var temp = new TempDirectory("guard");
        var layout = new InstallLayout(temp.Root);
        UpdaterStateStore.Update(layout, s =>
        {
            s.Current = "0.2.2-bbbbbb2";
            s.Previous = "0.2.1-aaaaaa1";
            s.LaunchFailures["0.2.2-bbbbbb2"] = UpdaterStateRules.LaunchFailureThreshold;
            return s;
        });
        var token = StartupOptions.NewToken();
        bool launched = false;

        using (new FileStream(layout.StateFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var rollBack = () => LaunchGuard.RollBackIfCrashing(layout, "0.2.2-bbbbbb2", (_, _) =>
            {
                launched = true;
                return 1;
            });
            rollBack.Should().NotThrow().Subject.Should().BeFalse();
            var markHealthy = () => LaunchGuard.MarkHealthy(layout, "0.2.2-bbbbbb2", token);
            markHealthy.Should().NotThrow();
        }

        launched.Should().BeFalse();
        File.Exists(layout.HealthFile(token)).Should().BeTrue("新版確實健康：狀態檔寫不進去也要給幫手健康訊號");
        UpdaterStateStore.Load(layout).LaunchFailures["0.2.2-bbbbbb2"].Should().Be(UpdaterStateRules.LaunchFailureThreshold);
    }
}
