using System.Diagnostics;
using Myriagent.Media;
using Myriagent.Speech;
using Myriagent.Tests.TestSupport;
using Myriagent.Voice;

namespace Myriagent.Tests.Voice;

/// <summary>
/// 語音訊息的真實辨識（本機的 Python＋faster-whisper＋ffmpeg，第一次要載入模型）：以 Windows 語音（SAPI）念一句中文成 WAV，
/// 另以 ffmpeg 轉成瀏覽器錄音的格式（WebM／Opus，.weba），兩個都要辨識出關鍵字；同一個程序接連辨識（常駐）。
/// 要 <c>MYRIAGENT_VOICE_IT=1</c> 才跑（需要這台裝好 faster-whisper 與模型；GPU 不能用時改用 CPU 的 small）
/// </summary>
public sealed class WhisperVoiceTranscriberIntegrationTests
{
    [EnvFact("MYRIAGENT_VOICE_IT", "本機的 faster-whisper 語音辨識")]
    public async Task 真的辨識_WAV與瀏覽器格式的錄音_同一個程序接連辨識()
    {
        using var temp = new TempDirectory("voice-it");
        var wave = temp.Combine("sample.wav");
        using (var engine = SapiSpeechEngine.Create(wave))
            engine.Speak("請幫我把側欄的搜尋框移到最上面，然後跑一次建置。", new VoiceTuning(1, 0), () => false, () => { });
        File.Exists(wave).Should().BeTrue();

        var weba = temp.Combine("語音訊息_20261023_120000.weba");
        var ffmpeg = FfmpegTool.FfmpegPath;
        ffmpeg.Should().NotBeNull("要有 ffmpeg");
        using (var convert = Process.Start(new ProcessStartInfo(ffmpeg!, ["-nostdin", "-loglevel", "error", "-y", "-i", wave, "-c:a", "libopus", "-b:a", "32k", "-f", "webm", weba])
               { CreateNoWindow = true, UseShellExecute = false })!)
            await convert.WaitForExitAsync();
        File.Exists(weba).Should().BeTrue();

        using var transcriber = new WhisperVoiceTranscriber(temp.Combine("voice"));
        var first = await transcriber.TranscribeAsync(wave, CancellationToken.None);
        first.Text.Should().Contain("搜尋");
        first.DurationSeconds.Should().BeGreaterThan(1);

        var watch = Stopwatch.StartNew();
        var second = await transcriber.TranscribeAsync(weba, CancellationToken.None);
        second.Text.Should().Contain("搜尋");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "模型已載入，第二則不必重新啟動");

        var missing = () => transcriber.TranscribeAsync(temp.Combine("不存在.weba"), CancellationToken.None);
        (await missing.Should().ThrowAsync<VoiceTranscribeException>()).Which.Message.Should().Contain("ffmpeg");
    }
}
