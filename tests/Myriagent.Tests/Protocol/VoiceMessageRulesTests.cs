using Myriagent.Agents;

namespace Myriagent.Tests.Protocol;

/// <summary>語音訊息的規則：錄音格式與檔名、網頁送來的資料網址、逐字稿整理、給代理的文字（要求先複誦）、畫面上的文字</summary>
public sealed class VoiceMessageRulesTests
{
    [Theory]
    [InlineData("audio/webm;codecs=opus", ".weba")]
    [InlineData("audio/webm", ".weba")]
    [InlineData("AUDIO/MP4", ".m4a")]
    [InlineData("audio/ogg; codecs=opus", ".ogg")]
    [InlineData("audio/wav", ".wav")]
    [InlineData("video/x-matroska", null)]
    [InlineData("text/plain", null)]
    [InlineData(null, null)]
    public void 錄音格式的副檔名_webm存成weba不被當成影片(string? mime, string? extension)
    {
        VoiceMessageRules.ExtensionFor(mime).Should().Be(extension);
        if (extension != null)
            AgentAttachmentRules.Classify("x" + extension).Should().Be(AgentAttachmentKind.Document, "錄音不是影片（不擷取畫面）");
    }

    [Fact]
    public void 檔名_前綴加時間_認得自己的檔名()
    {
        var name = VoiceMessageRules.FileName(new DateTime(2026, 10, 23, 9, 5, 7), ".weba");
        name.Should().Be("語音訊息_20261023_090507.weba");
        VoiceMessageRules.IsVoiceFileName(name).Should().BeTrue();
        VoiceMessageRules.IsVoiceFileName("語音訊息_20261023_090507 (2).m4a").Should().BeTrue("存檔重名加的編號不影響");
        VoiceMessageRules.IsVoiceFileName("語音訊息_x.exe").Should().BeFalse();
        VoiceMessageRules.IsVoiceFileName("報表.weba").Should().BeFalse();
        VoiceMessageRules.IsVoiceFileName(null).Should().BeFalse();
    }

    [Fact]
    public void 資料網址_只收音訊的base64_有上限()
    {
        VoiceMessageRules.TryParseDataUrl("data:audio/webm;codecs=opus;base64,AQIDBA==", out var bytes, out var ext).Should().BeTrue();
        bytes.Should().Equal(1, 2, 3, 4);
        ext.Should().Be(".weba");

        VoiceMessageRules.TryParseDataUrl("data:image/png;base64,AQIDBA==", out _, out _).Should().BeFalse("不是音訊");
        VoiceMessageRules.TryParseDataUrl("data:audio/webm,AQIDBA==", out _, out _).Should().BeFalse("不是 base64");
        VoiceMessageRules.TryParseDataUrl("data:audio/webm;base64,***", out _, out _).Should().BeFalse("base64 壞了");
        VoiceMessageRules.TryParseDataUrl("data:audio/webm;base64,", out _, out _).Should().BeFalse("空的");
        VoiceMessageRules.TryParseDataUrl(null, out _, out _).Should().BeFalse();
        var tooLarge = "data:audio/webm;base64," + new string('A', (VoiceMessageRules.MaxBytes / 3 + 2) * 4);
        VoiceMessageRules.TryParseDataUrl(tooLarge, out _, out _).Should().BeFalse("超過上限，不必解碼就擋");
    }

    [Fact]
    public void 逐字稿整理_空白與換行併成一個空格_截斷()
    {
        VoiceMessageRules.CleanTranscript("  請幫我\n\n整理   報表 ").Should().Be("請幫我 整理 報表");
        VoiceMessageRules.CleanTranscript(null).Should().BeEmpty();
        VoiceMessageRules.CleanTranscript(" \t ").Should().BeEmpty();
        VoiceMessageRules.CleanTranscript(new string('字', VoiceMessageRules.MaxTranscriptLength + 50)).Length.Should().Be(VoiceMessageRules.MaxTranscriptLength);
    }

    [Fact]
    public void 給代理的文字_有逐字稿_要求先複誦再照做()
    {
        var text = VoiceMessageRules.AgentText(" 請幫我\n整理報表 ", 12.4);
        text.Should().Contain("語音訊息，長 0:12");
        text.Should().Contain("逐字稿：「請幫我 整理報表」");
        text.Should().Contain("請先用一段文字複誦使用者說了什麼");
        text.Should().Contain("再依這段內容回應或執行");
        VoiceMessageRules.AgentText("好", null).Should().StartWith("（這則是使用者的語音訊息。");
    }

    [Fact]
    public void 畫面上的文字_語音訊息加長度()
    {
        VoiceMessageRules.DisplayText(12.4).Should().Be("語音訊息（0:12）");
        VoiceMessageRules.DisplayText(75).Should().Be("語音訊息（1:15）");
        VoiceMessageRules.DisplayText(null).Should().Be("語音訊息");
        VoiceMessageRules.DisplayText(0).Should().Be("語音訊息");

        // 歷史指令略過語音訊息：認得 DisplayText 產生的每一種
        foreach (double? seconds in new double?[] { null, 0, 12.4, 75, 3725 })
            VoiceMessageRules.IsDisplayText(VoiceMessageRules.DisplayText(seconds)).Should().BeTrue(VoiceMessageRules.DisplayText(seconds));
        VoiceMessageRules.IsDisplayText("語音訊息（0:12）請看").Should().BeFalse();
        VoiceMessageRules.IsDisplayText("請看語音訊息").Should().BeFalse();
        VoiceMessageRules.IsDisplayText(null).Should().BeFalse();
    }

    [Fact]
    public void 錄音波形_只認0到9的數字_最多64格_全是0或其他格式不存()
    {
        VoiceMessageRules.NormalizeWaveform("0137950").Should().Be("0137950");
        VoiceMessageRules.NormalizeWaveform(new string('5', VoiceMessageRules.MaxWaveformBars)).Should().HaveLength(64);
        foreach (var bad in new[] { null, "", "0000", "12a3", "1 2", "-1", new string('5', VoiceMessageRules.MaxWaveformBars + 1), "１２" })
            VoiceMessageRules.NormalizeWaveform(bad).Should().BeNull(bad ?? "null");
    }
}
