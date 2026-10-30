using System.Diagnostics;
using Myriagent.UI;

namespace Myriagent.Tests.UI;

/// <summary>在背景檢查路徑、最多等一小段時間（網路磁碟離線時 Exists 會卡到 SMB 逾時，不能卡住 UI 執行緒）</summary>
public class PathProbeTests
{
    [Fact]
    public void Check_很快完成就回結果()
    {
        using var temp = new TestSupport.TempDirectory("probe");
        PathProbe.Check(() => Directory.Exists(temp.Root), TimeSpan.FromSeconds(5)).Should().BeTrue();
        PathProbe.Check(() => File.Exists(temp.Combine("沒有這個檔案.png")), TimeSpan.FromSeconds(5)).Should().BeFalse();
    }

    [Fact]
    public void Check_卡住的檢查等到逾時就回Null_不會一直擋住呼叫端()
    {
        var gate = new ManualResetEventSlim();   // 不 Dispose：背景的檢查可能還在等它
        try
        {
            var sw = Stopwatch.StartNew();
            PathProbe.Check(() => gate.Wait(TimeSpan.FromSeconds(30)), TimeSpan.FromMilliseconds(200)).Should().BeNull("像離線的網路磁碟一樣卡住");
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public async Task CheckAsync_很快完成就回結果_卡住就在逾時後回Null()
    {
        (await PathProbe.CheckAsync(() => true, TimeSpan.FromSeconds(5))).Should().BeTrue();
        (await PathProbe.CheckAsync(() => false, TimeSpan.FromSeconds(5))).Should().BeFalse();

        var gate = new ManualResetEventSlim();   // 不 Dispose：背景的檢查可能還在等它
        try
        {
            var sw = Stopwatch.StartNew();
            (await PathProbe.CheckAsync(() => gate.Wait(TimeSpan.FromSeconds(30)), TimeSpan.FromMilliseconds(200))).Should().BeNull();
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        }
        finally
        {
            gate.Set();
        }
    }
}
