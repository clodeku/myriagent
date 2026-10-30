using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using Myriagent.Hub;
using Myriagent.Speech;
using Myriagent.Tests.TestSupport;
using Microsoft.Win32;

namespace Myriagent.Tests.Speech;

/// <summary>這台有 Windows 語音（SAPI 的聲音）才執行：沒有時顯示為「略過」</summary>
public sealed class SapiFactAttribute : FactAttribute
{
    public SapiFactAttribute()
    {
        using var tokens = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Speech\Voices\Tokens");
        if (Type.GetTypeFromProgID("SAPI.SpVoice") == null || tokens == null || tokens.SubKeyCount == 0)
            Skip = "需要 Windows 語音（SAPI）與至少一個聲音";
    }
}

/// <summary>語音通知的佇列與工作執行緒（假的聲音引擎），以及實際用 SAPI 念到 WAV 檔</summary>
public sealed class VoiceAnnouncerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>假的聲音引擎：開始念時呼叫 onStart，每句念到 Release 或被要求停下才返回</summary>
    private sealed class FakeEngine : ISpeechEngine
    {
        public BlockingCollection<string> Started { get; } = new();

        public ConcurrentQueue<(string Text, VoiceTuning Tuning)> Tunings { get; } = new();

        public ConcurrentQueue<(string Text, bool Stopped)> Finished { get; } = new();

        public SemaphoreSlim Release { get; } = new(0);

        public int Disposed;

        public void Speak(string text, VoiceTuning tuning, Func<bool> shouldStop, Action onStart)
        {
            Tunings.Enqueue((text, tuning));
            onStart();
            Started.Add(text);
            while (!Release.Wait(10))
            {
                if (shouldStop())
                {
                    Finished.Enqueue((text, true));
                    return;
                }
            }
            Finished.Enqueue((text, false));
        }

        public void Dispose() => Interlocked.Increment(ref Disposed);
    }

    private static string NextStarted(FakeEngine engine)
    {
        engine.Started.TryTake(out var text, Timeout).Should().BeTrue("工作執行緒應該開始念下一句");
        return text!;
    }

    private static async Task WaitIdleAsync(VoiceAnnouncer announcer)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!announcer.IsIdle)
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "應該念完");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task 依序念_一句念完才念下一句_同一句剛念過不重念()
    {
        var engine = new FakeEngine();
        using var announcer = new VoiceAnnouncer(() => engine);

        announcer.Speak("第一句");
        NextStarted(engine).Should().Be("第一句");
        announcer.Speak("第二句");
        announcer.Speak("第二句");
        engine.Started.TryTake(out _, 100).Should().BeFalse("第一句還沒念完");

        engine.Release.Release();
        NextStarted(engine).Should().Be("第二句");
        engine.Release.Release();
        await WaitIdleAsync(announcer);

        engine.Finished.Select(f => f.Text).Should().Equal("第一句", "第二句");
        engine.Started.Count.Should().Be(0, "同一句十秒內只念一次");
    }

    [Fact]
    public async Task 開始念時呼叫那一句的回呼_略過的句子不呼叫_回呼出錯照樣念()
    {
        var engine = new FakeEngine();
        using var announcer = new VoiceAnnouncer(() => engine);
        var calls = new ConcurrentQueue<string>();

        announcer.Speak("第一句", () => calls.Enqueue("第一句"));
        NextStarted(engine);
        announcer.Speak("第一句", () => calls.Enqueue("重複的第一句"));
        announcer.Speak("第二句", () => throw new InvalidOperationException("排不回 UI 執行緒"));
        announcer.Speak("第三句", () => calls.Enqueue("第三句"));
        engine.Release.Release(3);
        await WaitIdleAsync(announcer);

        calls.Should().Equal("第一句", "第三句");
        engine.Finished.Select(f => f.Text).Should().Equal("第一句", "第二句", "第三句");
    }

    [Fact]
    public async Task 每句照排進來時的語速與音調念_沒給的用預設()
    {
        var engine = new FakeEngine();
        using var announcer = new VoiceAnnouncer(() => engine);
        var slow = new VoiceTuning(0.8, -3);
        var fast = new VoiceTuning(2.2, 5);

        announcer.Speak("第一句", null, slow);
        NextStarted(engine);
        announcer.Speak("第二句", null, fast);
        announcer.Speak("第三句");
        engine.Release.Release(3);
        await WaitIdleAsync(announcer);

        engine.Tunings.Should().Equal(("第一句", slow), ("第二句", fast), ("第三句", VoiceTuning.Default));
    }

    [Fact]
    public async Task 佇列最多等三句_多的不念()
    {
        var engine = new FakeEngine();
        using var announcer = new VoiceAnnouncer(() => engine);
        announcer.Speak("念著的");
        NextStarted(engine);

        for (var i = 1; i <= VoiceNotifyRules.MaxPending + 2; i++)
            announcer.Speak($"第 {i} 句");
        engine.Release.Release(VoiceNotifyRules.MaxPending + 3);
        await WaitIdleAsync(announcer);

        engine.Finished.Select(f => f.Text).Should().Equal("念著的", "第 1 句", "第 2 句", "第 3 句");
    }

    [Fact]
    public async Task Stop_停下正在念的並清掉佇列_之後照常念()
    {
        var engine = new FakeEngine();
        using var announcer = new VoiceAnnouncer(() => engine);
        announcer.Speak("很長的一句");
        NextStarted(engine);
        announcer.Speak("排著的");

        announcer.Stop();
        await WaitIdleAsync(announcer);
        engine.Finished.Should().Equal(("很長的一句", true));

        announcer.Speak("很長的一句");
        NextStarted(engine).Should().Be("很長的一句", "Stop 之後同一句可以馬上再念");
        engine.Release.Release();
        await WaitIdleAsync(announcer);
    }

    [Fact]
    public async Task Dispose_停下正在念的並釋放引擎_之後不再念()
    {
        var engine = new FakeEngine();
        var announcer = new VoiceAnnouncer(() => engine);
        announcer.Speak("念著的");
        NextStarted(engine);

        announcer.Dispose();
        var deadline = DateTime.UtcNow + Timeout;
        while (Volatile.Read(ref engine.Disposed) == 0)
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "工作執行緒結束時釋放引擎");
            await Task.Delay(10);
        }
        engine.Finished.Should().Equal(("念著的", true));

        announcer.Speak("之後的");
        engine.Started.TryTake(out _, 100).Should().BeFalse();
    }

    [Fact]
    public async Task 引擎建不起來_之後都不念也不再嘗試()
    {
        var attempts = 0;
        using var announcer = new VoiceAnnouncer(() =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("這台電腦沒有 Windows 語音（SAPI）");
        });

        announcer.Speak("第一句");
        await WaitIdleAsync(announcer);
        announcer.Speak("第二句");
        await WaitIdleAsync(announcer);

        attempts.Should().Be(1);
    }

    [SapiFact]
    public async Task SAPI_念成升調的WAV_取樣率高2個半音_有裝雅婷就選雅婷()
    {
        using var temp = new TempDirectory("voice");
        var wave = temp.Combine("voice.wav");
        SapiSpeechEngine? created = null;
        using var announcer = new VoiceAnnouncer(() =>
        {
            created = SapiSpeechEngine.Create(wave);
            return created;
        });
        var started = 0;

        announcer.Speak(VoiceNotifyRules.Completed("語音測試", "月報已經匯出成 Excel。"), () => Interlocked.Increment(ref started));
        await WaitIdleAsync(announcer);

        var bytes = File.ReadAllBytes(wave);
        Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("RIFF");
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24)).Should().Be(SpeechAudioRules.Plan(VoiceTuning.Default).PlaybackSampleRate);
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40)).Should().BeGreaterThan(20_000, "念了一句話（24 kHz 16 位元，一秒 48000 位元組）");
        started.Should().Be(1);
        using var yating = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Speech_OneCore\Voices\Tokens\MSTTS_V110_zhTW_YatingM");
        if (yating != null)
            created!.VoiceName.Should().Contain("Yating");
    }

    [SapiFact]
    public async Task SAPI_語速兩倍的長度約一半_音調只改取樣率()
    {
        using var temp = new TempDirectory("voice");
        var sentence = VoiceNotifyRules.TuningSample;

        async Task<byte[]> SpeakAsync(string name, VoiceTuning tuning)
        {
            var wave = temp.Combine(name);
            using var announcer = new VoiceAnnouncer(() => SapiSpeechEngine.Create(wave));
            announcer.Speak(sentence, null, tuning);
            await WaitIdleAsync(announcer);
            return File.ReadAllBytes(wave);
        }

        double Seconds(byte[] wave) => (wave.Length - 44) / 2.0 / BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(24));

        var normal = await SpeakAsync("normal.wav", new VoiceTuning(1, 0));
        var fast = await SpeakAsync("fast.wav", new VoiceTuning(2, 0));
        var high = await SpeakAsync("high.wav", new VoiceTuning(1, 4));

        BinaryPrimitives.ReadInt32LittleEndian(normal.AsSpan(24)).Should().Be(24000);
        BinaryPrimitives.ReadInt32LittleEndian(high.AsSpan(24)).Should().Be(SpeechAudioRules.PlaybackSampleRate(24000, 4));
        (Seconds(normal) / Seconds(fast)).Should().BeApproximately(2, 0.2, "含頭尾的靜音，容許一成");
        (Seconds(normal) / Seconds(high)).Should().BeApproximately(1, 0.1, "升調的加速由較慢的 SAPI 語速與時間伸縮抵掉");
    }

    /// <summary>
    /// 試聽：照 app 實際的做法（雅婷、語速與升調照 SpeechAudioRules）把幾句通知念成 WAV，放在 %TEMP%\myriagent-voice-samples\。
    /// 調整 SpeechAudioRules 之後用它聽聽看：MYRIAGENT_VOICE_SAMPLES=1 dotnet test --filter "FullyQualifiedName~試聽"
    /// </summary>
    [EnvFact("MYRIAGENT_VOICE_SAMPLES", "把通知念成試聽用的 WAV 檔")]
    public async Task 試聽_照app的做法念成WAV檔()
    {
        var dir = Path.Combine(Path.GetTempPath(), "myriagent-voice-samples");
        Directory.CreateDirectory(dir);
        string[] sentences =
        [
            VoiceNotifyRules.Completed("修正登入錯誤", "已推送到 main，GitHub Actions 部署成功。"),
            VoiceNotifyRules.Question("整理報表", "要輸出成 Excel 還是 PDF？"),
            VoiceNotifyRules.Approval("部署網站", "執行指令", "git push origin main", null),
            VoiceNotifyRules.Completed("升級套件", "Next.js 升到 15 版，npm test 全部通過。"),
            VoiceNotifyRules.Failed("同步模組", "SQLite database is locked"),
        ];
        for (var i = 0; i < sentences.Length; i++)
        {
            using var announcer = new VoiceAnnouncer(() => SapiSpeechEngine.Create(Path.Combine(dir, $"sample-{i}.wav")));
            announcer.Speak(sentences[i]);
            await WaitIdleAsync(announcer);
        }
        File.WriteAllLines(Path.Combine(dir, "sentences.txt"), sentences);
    }
}
