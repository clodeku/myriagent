using System.Buffers.Binary;
using System.Text;
using Myriagent.Speech;

namespace Myriagent.Tests.Speech;

/// <summary>語音通知的語速與音調（SAPI 整數級＋升調播放＋時間伸縮）、時間伸縮本身與 WAV 檔頭（SpeechAudioRules）</summary>
public sealed class SpeechAudioRulesTests
{
    [Fact]
    public void 正規化_限制在範圍內_語速取到0點1_音調取到整數半音_不是數字回預設()
    {
        SpeechAudioRules.NormalizeSpeed(1.5).Should().Be(1.5);
        SpeechAudioRules.NormalizeSpeed(1.26).Should().Be(1.3);
        SpeechAudioRules.NormalizeSpeed(1.25).Should().Be(1.3, "中間值進位");
        SpeechAudioRules.NormalizeSpeed(0.1).Should().Be(SpeechAudioRules.MinSpeed);
        SpeechAudioRules.NormalizeSpeed(9).Should().Be(SpeechAudioRules.MaxSpeed);
        SpeechAudioRules.NormalizeSpeed(double.NaN).Should().Be(SpeechAudioRules.DefaultSpeed);
        SpeechAudioRules.NormalizeSpeed(double.PositiveInfinity).Should().Be(SpeechAudioRules.DefaultSpeed);

        SpeechAudioRules.NormalizePitch(2).Should().Be(2);
        SpeechAudioRules.NormalizePitch(-2.6).Should().Be(-3);
        SpeechAudioRules.NormalizePitch(-20).Should().Be(SpeechAudioRules.MinPitch);
        SpeechAudioRules.NormalizePitch(20).Should().Be(SpeechAudioRules.MaxPitch);
        SpeechAudioRules.NormalizePitch(double.NaN).Should().Be(SpeechAudioRules.DefaultPitch);
    }

    [Fact]
    public void 預設_SAPI語速3級_升2個半音的取樣率_零頭放慢約4趴()
    {
        var plan = SpeechAudioRules.Plan(VoiceTuning.Default);

        plan.SapiRate.Should().Be(3);
        plan.PlaybackSampleRate.Should().Be(26939);
        plan.Stretch.Should().BeApproximately(0.961, 0.001, "1.39 × 1.1225 ≈ 1.561 倍，要 1.5 倍");
    }

    [Fact]
    public void 原速原調_什麼都不做()
        => SpeechAudioRules.Plan(new VoiceTuning(1, 0)).Should().Be(new SpeechPlan(0, 24000, 1));

    [Fact]
    public void 每一種組合_SAPI級數在範圍內_總倍速等於設定的語速_音調全由取樣率決定()
    {
        for (var speed = SpeechAudioRules.MinSpeed; speed <= SpeechAudioRules.MaxSpeed + 1e-9; speed += SpeechAudioRules.SpeedStep)
        {
            for (var pitch = SpeechAudioRules.MinPitch; pitch <= SpeechAudioRules.MaxPitch; pitch++)
            {
                var plan = SpeechAudioRules.Plan(new VoiceTuning(speed, pitch));
                plan.SapiRate.Should().BeInRange(SpeechAudioRules.MinSapiRate, SpeechAudioRules.MaxSapiRate);
                plan.PlaybackSampleRate.Should().Be(SpeechAudioRules.PlaybackSampleRate(SpeechAudioRules.RenderSampleRate, pitch));
                var total = SpeechAudioRules.SapiSpeed(plan.SapiRate) * plan.Stretch * plan.PlaybackSampleRate / SpeechAudioRules.RenderSampleRate;
                total.Should().BeApproximately(Math.Round(speed, 1), Math.Round(speed, 1) * 0.006, $"語速 {speed:0.0}、音調 {pitch}");
                plan.Stretch.Should().BeInRange(0.8, 1.2, "零頭不大，時間伸縮聽不出加工");
            }
        }
    }

    [Fact]
    public void 相鄰的語速_做法不同_每一格都聽得出差別()
    {
        for (var pitch = SpeechAudioRules.MinPitch; pitch <= SpeechAudioRules.MaxPitch; pitch++)
        {
            SpeechPlan? previous = null;
            for (var speed = SpeechAudioRules.MinSpeed; speed <= SpeechAudioRules.MaxSpeed + 1e-9; speed += SpeechAudioRules.SpeedStep)
            {
                var plan = SpeechAudioRules.Plan(new VoiceTuning(speed, pitch));
                plan.Should().NotBe(previous, $"語速 {speed:0.0}、音調 {pitch}");
                previous = plan;
            }
        }
    }

    [Fact]
    public void 時間伸縮_1倍原樣複製_空的或奇數長度不出錯()
    {
        var pcm = new byte[] { 1, 2, 3, 4, 5 };

        SpeechAudioRules.TimeStretch(pcm, 1, 24000).Should().Equal(1, 2, 3, 4);
        SpeechAudioRules.TimeStretch(pcm, 1.003, 24000).Should().Equal(1, 2, 3, 4);
        SpeechAudioRules.TimeStretch([], 1.3, 24000).Should().BeEmpty();
        SpeechAudioRules.TimeStretch([7], 1.3, 24000).Should().BeEmpty();
    }

    [Theory]
    [InlineData(1.2)]
    [InlineData(0.85)]
    [InlineData(0.96)]
    [InlineData(1.06)]
    public void 時間伸縮_長度變成1除以倍速_音高與音量不變(double speed)
    {
        const int rate = 24000;
        const double frequency = 220;
        var pcm = Sine(frequency, 2.0, rate, 12000);

        var stretched = SpeechAudioRules.TimeStretch(pcm, speed, rate);

        (stretched.Length / 2).Should().BeCloseTo((int)Math.Round(2.0 * rate / speed), 1);
        var samples = Samples(stretched);
        // 頭尾各去掉 0.1 秒（開頭是半個窗的淡入）
        var middle = samples[(rate / 10)..(samples.Length - rate / 10)];
        var crossings = 0;
        for (var i = 1; i < middle.Length; i++)
        {
            if ((middle[i - 1] < 0) != (middle[i] < 0))
                crossings++;
        }
        var measured = crossings / 2.0 / ((double)middle.Length / rate);
        measured.Should().BeApproximately(frequency, frequency * 0.02, "WSOLA 只改長度、不改音高");
        var rms = Math.Sqrt(middle.Average(x => (double)x * x));
        rms.Should().BeApproximately(12000 / Math.Sqrt(2), 12000 / Math.Sqrt(2) * 0.05, "Hann 窗 50% 重疊相加是 1，音量不變");
    }

    [Fact]
    public void 時間伸縮_最大音量不會溢位()
    {
        var pcm = Sine(330, 1.0, 24000, short.MaxValue);

        var samples = Samples(SpeechAudioRules.TimeStretch(pcm, 1.15, 24000));

        samples.Max().Should().BeLessThanOrEqualTo(short.MaxValue);
        samples.Min().Should().BeGreaterThanOrEqualTo(short.MinValue);
        samples.Select(x => Math.Abs((int)x)).Max().Should().BeGreaterThan(30000, "接縫對齊了，相加沒有互相抵消");
    }

    private static byte[] Sine(double frequency, double seconds, int rate, double amplitude)
    {
        var count = (int)(seconds * rate);
        var pcm = new byte[count * 2];
        for (var i = 0; i < count; i++)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)Math.Round(amplitude * Math.Sin(2 * Math.PI * frequency * i / rate)));
        return pcm;
    }

    private static short[] Samples(byte[] pcm)
    {
        var samples = new short[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2));
        return samples;
    }

    [Fact]
    public void 播放取樣率_升2個半音是26939Hz_0個半音不變()
    {
        SpeechAudioRules.PlaybackSampleRate(24000, 2).Should().Be(26939);
        SpeechAudioRules.PlaybackSampleRate(24000, 0).Should().Be(24000);
        SpeechAudioRules.PlaybackSampleRate(24000, 12).Should().Be(48000, "升一個八度是兩倍");
    }

    [Fact]
    public void 長度_16位元單聲道每秒兩倍取樣率的位元組()
        => SpeechAudioRules.Duration(53878, 26939).Should().Be(TimeSpan.FromSeconds(1));

    [Fact]
    public void Wave_44位元組的PCM檔頭_奇數長度捨掉最後一個位元組()
    {
        var wave = SpeechAudioRules.Wave(new byte[] { 1, 2, 3, 4, 5 }, 26939);

        wave.Length.Should().Be(48);
        Encoding.ASCII.GetString(wave, 0, 4).Should().Be("RIFF");
        BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(4)).Should().Be(40);
        Encoding.ASCII.GetString(wave, 8, 8).Should().Be("WAVEfmt ");
        BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(20)).Should().Be(1, "PCM");
        BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(22)).Should().Be(1, "單聲道");
        BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(24)).Should().Be(26939);
        BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(28)).Should().Be(53878);
        BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(32)).Should().Be(2);
        BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(34)).Should().Be(16);
        Encoding.ASCII.GetString(wave, 36, 4).Should().Be("data");
        BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(40)).Should().Be(4);
        wave.AsSpan(44).ToArray().Should().Equal(1, 2, 3, 4);
    }
}
