using Myriagent.Core;
using Myriagent.Tests.TestSupport;
using Myriagent.Update;

namespace Myriagent.Tests.Update;

/// <summary>新版自動接手舊版（2026-10-22）：要不要請已在執行的實例交棒、交棒請求的檢查、新版的啟動參數</summary>
public class InstanceHandoverRulesTests
{
    private static ReleaseVersion V(string text)
    {
        ReleaseVersion.TryParse(text, out var v).Should().BeTrue(text);
        return v;
    }

    private static RunningInstanceFile Running(string release) => new() { Pid = 1234, Release = release, Executable = @"C:\apps\old\Myriagent.exe" };

    [Theory]
    [InlineData("0.2.512", "0.2.470-975df59", nameof(LaunchDecision.RequestHandover))]
    [InlineData("0.2.512", "0.2.0-dev", nameof(LaunchDecision.RequestHandover))]   // 系統匣裡的開發版：發佈版比它新
    [InlineData("0.3.0", "0.2.999-abcdef1", nameof(LaunchDecision.RequestHandover))]
    [InlineData("0.2.512", "0.2.512-b247e78", nameof(LaunchDecision.Activate))]    // 同版號（含不同提交的開發版）照舊叫醒
    [InlineData("0.2.0", "0.2.512-b247e78", nameof(LaunchDecision.Activate))]      // 開了比較舊的：叫醒較新的那一份
    [InlineData("0.2.512", "壞掉的版本", nameof(LaunchDecision.Activate))]          // 看不懂對方的版本：不冒險交棒
    public void Decide_對方較舊才請它交棒(string self, string running, string expected)
        => InstanceHandoverRules.Decide(V(self), Running(running)).ToString().Should().Be(expected);

    [Fact]
    public void Decide_對方沒留instance_json_是不懂交棒的舊版_要說明()
        => InstanceHandoverRules.Decide(V("0.2.600"), null).Should().Be(LaunchDecision.ExplainOldInstance);

    private static readonly DateTime Now = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

    private static HandoverRequestFile Request(string release = "0.2.600-cccccc3", string exe = @"C:\Downloads\myriagent\Myriagent.exe", int ageSeconds = 1) => new()
    {
        Executable = exe,
        Release = release,
        Pid = 99,
        CreatedAt = new DateTimeOffset(Now.AddSeconds(-ageSeconds)).ToUnixTimeMilliseconds(),
    };

    [Fact]
    public void CheckRequest_合格的請求()
        => InstanceHandoverRules.CheckRequest(Request(), V("0.2.512"), Now, _ => true).Should().BeNull();

    [Theory]
    [InlineData("0.2.512-b247e78", null, 1, "沒有比較新")]
    [InlineData("0.2.400-aaaaaa1", null, 1, "沒有比較新")]
    [InlineData("不合法", null, 1, "版本不合法")]
    [InlineData("0.2.600-cccccc3", null, 31, "過期")]
    [InlineData("0.2.600-cccccc3", null, -31, "過期")]                              // 時鐘不對的未來請求也不收
    [InlineData("0.2.600-cccccc3", @"\\evil\share\Myriagent.exe", 1, "執行檔不合法")]   // UNC 一律不碰
    [InlineData("0.2.600-cccccc3", @"C:\Downloads\other.exe", 1, "執行檔不合法")]
    [InlineData("0.2.600-cccccc3", "Myriagent.exe", 1, "執行檔不合法")]              // 相對路徑
    public void CheckRequest_不合格的不接(string release, string? exe, int ageSeconds, string reason)
    {
        var request = Request(release, exe ?? @"C:\Downloads\myriagent\Myriagent.exe", ageSeconds);
        InstanceHandoverRules.CheckRequest(request, V("0.2.512"), Now, _ => true).Should().Contain(reason);
    }

    [Fact]
    public void CheckRequest_沒有請求或執行檔不在()
    {
        InstanceHandoverRules.CheckRequest(null, V("0.2.512"), Now, _ => true).Should().NotBeNull();
        InstanceHandoverRules.CheckRequest(Request(), V("0.2.512"), Now, _ => false).Should().Contain("不存在");
    }

    [Fact]
    public void 新版的啟動參數_預設資料夾不帶data_其他資料夾帶上同一個()
    {
        var token = StartupOptions.NewToken();
        InstanceHandoverRules.RelaunchArguments(token, null).Should().Equal("--handed-over", token);
        InstanceHandoverRules.RelaunchArguments(token, @"D:\probe data").Should().Equal("--handed-over", token, "--data", @"D:\probe data");
        StartupOptions.Parse(InstanceHandoverRules.RelaunchArguments(token, @"D:\probe data").ToList()).Should().Match<StartupOptions>(
            o => o.HandedOverToken == token && o.DataRoot == @"D:\probe data" && o.AfterUpdateToken == null);
    }

    [Fact]
    public void 說明寫出這一版與怎麼結束舊版()
    {
        var text = InstanceHandoverRules.OldInstanceMessage(V("0.2.600"));
        text.Should().Contain("0.2.600").And.Contain("系統匣").And.Contain("「結束」");
    }
}

/// <summary>交棒用的兩個暫時檔（InstanceHandover）：請求的寫與取、對方的 instance.json 的存活判斷</summary>
public sealed class InstanceHandoverFilesTests : IDisposable
{
    private readonly TempDirectory temp = new("handover");

    public void Dispose() => temp.Dispose();

    private static ReleaseIdentity Self(string release)
    {
        ReleaseIdentity.TryParseRelease(release, out var id).Should().BeTrue();
        return id!;
    }

    [Fact]
    public void 請求寫了取一次就沒了()
    {
        InstanceHandover.WriteRequest(temp.Root, Self("0.2.600-cccccc3")).Should().BeTrue();

        var request = InstanceHandover.TakeRequest(temp.Root);

        request.Should().NotBeNull();
        request!.Release.Should().Be("0.2.600-cccccc3");
        request.Executable.Should().Be(Environment.ProcessPath);
        request.Pid.Should().Be(Environment.ProcessId);
        InstanceHandover.TakeRequest(temp.Root).Should().BeNull("每個請求只處理一次");
    }

    [Fact]
    public void 壞掉的檔當成沒有_不備份()
    {
        Directory.CreateDirectory(Path.Combine(temp.Root, "update"));
        File.WriteAllText(InstanceHandover.RequestPath(temp.Root), "{ 不是 JSON");

        InstanceHandover.TakeRequest(temp.Root).Should().BeNull();
        Directory.GetFiles(Path.Combine(temp.Root, "update")).Should().BeEmpty("暫時檔壞了就丟掉，不留 .corrupt");
    }

    [Fact]
    public void 對方的instance_json_程序不在了不算_自己寫的也不算()
    {
        Directory.CreateDirectory(Path.Combine(temp.Root, "update"));
        // 當掉留下的舊檔：pid 不存在
        JsonFile.Write(InstanceHandover.RunningPath(temp.Root), new RunningInstanceFile { Pid = int.MaxValue - 7, Release = "0.2.400-aaaaaa1", Executable = @"C:\x\Myriagent.exe" });
        InstanceHandover.ReadRunning(temp.Root, TimeSpan.Zero).Should().BeNull();

        InstanceHandover.MarkRunning(temp.Root, Self("0.2.512-b247e78"));
        InstanceHandover.ReadRunning(temp.Root, TimeSpan.Zero).Should().BeNull("自己寫的不是「已在執行的另一份」");

        InstanceHandover.ClearRunning(temp.Root);
        File.Exists(InstanceHandover.RunningPath(temp.Root)).Should().BeFalse();
    }

    [Fact]
    public void 對方還在執行_執行檔相同才算()
    {
        // 一個確定在執行、而且不是自己的程序：自己啟動一個
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        using var sleeper = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(cmd, "/c ping -n 30 127.0.0.1 >nul")
        { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            Directory.CreateDirectory(Path.Combine(temp.Root, "update"));
            SpinWait.SpinUntil(() =>
            {
                try
                {
                    sleeper.Refresh();
                    return sleeper.MainModule != null;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    return false;
                }
            }, TimeSpan.FromSeconds(5)).Should().BeTrue("程序起來了");
            JsonFile.Write(InstanceHandover.RunningPath(temp.Root), new RunningInstanceFile { Pid = sleeper.Id, Release = "0.2.400-aaaaaa1", Executable = cmd });
            InstanceHandover.ReadRunning(temp.Root, TimeSpan.Zero).Should().NotBeNull().And.Match<RunningInstanceFile>(f => f.Pid == sleeper.Id);

            JsonFile.Write(InstanceHandover.RunningPath(temp.Root), new RunningInstanceFile { Pid = sleeper.Id, Release = "0.2.400-aaaaaa1", Executable = @"C:\別的\Myriagent.exe" });
            InstanceHandover.ReadRunning(temp.Root, TimeSpan.Zero).Should().BeNull("pid 被別的程式重用：執行檔不同就不算");
        }
        finally
        {
            try
            {
                sleeper.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    [Fact]
    public void Prepare_沒有對方的instance_json_記下要說明_不寫請求()
    {
        InstanceHandover.Prepare(temp.Root, Self("0.2.600-cccccc3"), out var decision).Should().BeFalse();

        decision.Should().Be(LaunchDecision.ExplainOldInstance);
        File.Exists(InstanceHandover.RequestPath(temp.Root)).Should().BeFalse();
    }
}
