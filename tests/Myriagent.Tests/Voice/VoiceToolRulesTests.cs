using Myriagent.UI;
using Myriagent.Voice;

namespace Myriagent.Tests.Voice;

/// <summary>語音訊息本機辨識的規則：找 Python（略過商店的捷徑）、辨識程序的輸出、啟動失敗的說明；WebView2 只對 app 自己的頁面允許麥克風</summary>
public sealed class VoiceToolRulesTests
{
    [Fact]
    public void 找Python_照PATH順序_略過WindowsApps的商店捷徑()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Users\u\AppData\Local\Microsoft\WindowsApps\python.exe",
            @"C:\Python313\python.exe",
            @"D:\tools\python.exe",
        };
        VoiceToolRules.FindPython([@"C:\Users\u\AppData\Local\Microsoft\WindowsApps;C:\Python313;D:\tools"], existing.Contains)
            .Should().Be(@"C:\Python313\python.exe");
        VoiceToolRules.FindPython([@"C:\Users\u\AppData\Local\Microsoft\WindowsApps"], existing.Contains).Should().BeNull("只有商店的捷徑");
        VoiceToolRules.FindPython([null, ""], existing.Contains).Should().BeNull();
    }

    [Fact]
    public void 辨識程序的輸出_事件與結果_不是JSON的略過()
    {
        var ready = VoiceToolRules.ParseWorkerLine("""{"event":"ready","device":"cuda","model":"large-v3"}""")!;
        (ready.Event, ready.Device, ready.Model).Should().Be(("ready", "cuda", "large-v3"));
        var ok = VoiceToolRules.ParseWorkerLine("""{"id":3,"ok":true,"text":"你好","duration":1.5}""")!;
        (ok.Id, ok.Ok, ok.Text, ok.Duration).Should().Be((3L, true, "你好", 1.5));
        var failed = VoiceToolRules.ParseWorkerLine("""{"id":4,"ok":false,"error":"壞了"}""")!;
        (failed.Id, failed.Ok, failed.Error).Should().Be((4L, false, "壞了"));
        VoiceToolRules.ParseWorkerLine("""{"id":5,"ok":true,"text":"整數長度","duration":2}""")!.Duration.Should().Be(2);
        VoiceToolRules.ParseWorkerLine("Warning: something").Should().BeNull();
        VoiceToolRules.ParseWorkerLine("{壞掉").Should().BeNull();
        VoiceToolRules.ParseWorkerLine("   ").Should().BeNull();
        VoiceToolRules.RequestLine(7, @"C:\a\語音.weba").Should().Be("""{"id":7,"path":"C:\\a\\語音.weba"}""");
    }

    [Fact]
    public void 啟動失敗的說明_沒裝faster_whisper教怎麼裝()
    {
        VoiceToolRules.StartFailure("faster-whisper 沒有安裝：ModuleNotFoundError(\"No module named 'faster_whisper'\")", null).Should().Contain("pip install faster-whisper");
        VoiceToolRules.StartFailure(null, "Traceback…\nModuleNotFoundError: No module named 'faster_whisper'").Should().Contain("pip install faster-whisper");
        VoiceToolRules.StartFailure(null, "No module named 'numpy'").Should().Contain("pip install numpy");
        VoiceToolRules.StartFailure("載入語音辨識模型失敗：cuda: X", null).Should().Be("語音辨識程序沒有啟動：載入語音辨識模型失敗：cuda: X");
        VoiceToolRules.StartFailure(null, null).Should().Be("語音辨識程序沒有啟動");
        VoiceToolRules.StartFailure(null, new string('x', 1000)).Length.Should().BeLessThan(330);
    }

    [Theory]
    [InlineData("https://myriagent.local/index.html", true)]
    [InlineData("https://MYRIAGENT.LOCAL/index.html?mode=popout", true)]
    [InlineData("https://myriagent.local.evil.example/", false)]
    [InlineData("https://example.com/", false)]
    [InlineData("http://myriagent.local/", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 麥克風只給app自己的頁面(string? uri, bool allowed) => AgentWebView.IsAppOrigin(uri).Should().Be(allowed);
}
