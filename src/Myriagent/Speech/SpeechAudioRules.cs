using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Myriagent.Speech;

/// <summary>語音通知的語速（總倍速，1＝原速）與音調（升幾個半音，負的是降）；設定頁「語音通知」底下的兩個滑桿，值的範圍見 <see cref="SpeechAudioRules"/></summary>
internal readonly record struct VoiceTuning(double Speed, double PitchSemitones)
{
    /// <summary>使用者 2026-10-23 試聽後選定的：約 1.5 倍速、升 2 個半音</summary>
    public static VoiceTuning Default => new(SpeechAudioRules.DefaultSpeed, SpeechAudioRules.DefaultPitch);
}

/// <summary>念一句的做法：SAPI 的整數語速、播放的取樣率、念完之後再做的時間伸縮倍速（1＝不做）</summary>
internal readonly record struct SpeechPlan(int SapiRate, int PlaybackSampleRate, double Stretch);

/// <summary>
/// 語音通知的聲音調整（雅婷；語速與音調由使用者在設定頁調，預設約 1.5 倍速、升 2 個半音）。
/// 升調的做法是「念慢一點、播快一點」：SAPI 念成 PCM，再以高 N 個半音的取樣率播放——音高與聲音特質一起升 N 個半音、速度同時快 2^(N/12) 倍，
/// 和 rubberband 後製「升調、共振峰跟著升」的版本相同，不需要變調元件。SAPI 的語速只有整數級（每級約 3^(1/10)≈1.116 倍；SSML 的小數語速被忽略，
/// 2026-10-23 實測），所以總倍速＝SAPI 的整數級 × 升調播放 × 剩下的零頭；零頭（多半在 ±6% 內，範圍兩端的組合最多約 18%）以不改音高的時間伸縮（WSOLA）補上，
/// 滑桿每一格都聽得出差別
/// </summary>
internal static class SpeechAudioRules
{
    /// <summary>SAPI 念出來的格式：24 kHz、16 位元、單聲道（SpeechAudioFormatType.SAFT24kHz16BitMono）</summary>
    public const int RenderFormatType = 26;

    public const int RenderSampleRate = 24000;

    /// <summary>語速（總倍速）的範圍、間距與預設（網頁 voicerules.js 的 SPEED_* 同值）</summary>
    public const double MinSpeed = 0.5;

    public const double MaxSpeed = 2.5;

    public const double SpeedStep = 0.1;

    public const double DefaultSpeed = 1.5;

    /// <summary>音調（半音）的範圍與預設，以整數半音調整（網頁 voicerules.js 的 PITCH_* 同值）</summary>
    public const double MinPitch = -6;

    public const double MaxPitch = 6;

    public const double DefaultPitch = 2;

    /// <summary>SAPI 的語速範圍；每一級是 3^(1/10) 倍（10 約 3 倍、-10 約 1/3）</summary>
    public const int MinSapiRate = -10;

    public const int MaxSapiRate = 10;

    /// <summary>零頭小於這個比例就不做時間伸縮（聽不出差別，省一次處理）</summary>
    public const double StretchTolerance = 0.005;

    /// <summary>時間伸縮的窗長與搜尋範圍（秒）：25 毫秒的 Hann 窗、50% 重疊；前後各找 10 毫秒（涵蓋男聲一個基音週期）對齊波形</summary>
    private const double FrameSeconds = 0.025;

    private const double ToleranceSeconds = 0.010;

    /// <summary>語速：限制在範圍內、取到 0.1；不是有限的數字回預設</summary>
    public static double NormalizeSpeed(double speed)
        => double.IsFinite(speed) ? Math.Round(Math.Clamp(speed, MinSpeed, MaxSpeed), 1, MidpointRounding.AwayFromZero) : DefaultSpeed;

    /// <summary>音調：限制在範圍內、取到整數半音；不是有限的數字回預設</summary>
    public static double NormalizePitch(double semitones)
        => double.IsFinite(semitones) ? Math.Round(Math.Clamp(semitones, MinPitch, MaxPitch), MidpointRounding.AwayFromZero) : DefaultPitch;

    /// <summary>SAPI 語速 <paramref name="rate"/> 的倍速（雅婷實測 0～4 級與 3^(級/10) 相差不到 0.3%）</summary>
    public static double SapiSpeed(int rate) => Math.Pow(3, rate / 10.0);

    /// <summary>
    /// 怎麼念出 <paramref name="tuning"/>（先正規化）：音調全由播放的取樣率做（升 N 個半音也快 2^(N/12) 倍），
    /// 還要的倍速取最接近的 SAPI 整數級，差的零頭交給時間伸縮
    /// </summary>
    public static SpeechPlan Plan(VoiceTuning tuning)
    {
        var speed = NormalizeSpeed(tuning.Speed);
        var semitones = NormalizePitch(tuning.PitchSemitones);
        var pitchSpeed = Math.Pow(2, semitones / 12);
        var rate = (int)Math.Clamp(Math.Round(10 * Math.Log(speed / pitchSpeed, 3), MidpointRounding.AwayFromZero), MinSapiRate, MaxSapiRate);
        var stretch = speed / (SapiSpeed(rate) * pitchSpeed);
        if (Math.Abs(stretch - 1) < StretchTolerance)
            stretch = 1;
        return new SpeechPlan(rate, PlaybackSampleRate(RenderSampleRate, semitones), stretch);
    }

    /// <summary>播放的取樣率：念的取樣率乘上 2 的（半音數／12）次方（24 kHz 升 2 個半音是 26939 Hz）</summary>
    public static int PlaybackSampleRate(int renderSampleRate, double semitones)
        => (int)Math.Round(renderSampleRate * Math.Pow(2, semitones / 12));

    /// <summary>16 位元單聲道 PCM 以 <paramref name="sampleRate"/> 播放要多久</summary>
    public static TimeSpan Duration(int pcmBytes, int sampleRate) => TimeSpan.FromSeconds(pcmBytes / 2.0 / sampleRate);

    /// <summary>
    /// 不改音高的時間伸縮（WSOLA）：16 位元單聲道 PCM 變成 <paramref name="speed"/> 倍速（大於 1 變短、小於 1 變長），長度是原本的 1/speed。
    /// 每 12.5 毫秒放一個 25 毫秒的 Hann 窗（50% 重疊相加剛好是 1，音量不變）；每一格在理想位置前後 10 毫秒內，挑和上一格的自然接續最像
    /// （正規化互相關最大）的位置，接縫的波形對得上，不會出現顫音或破音。speed 和 1 差不到 <see cref="StretchTolerance"/> 時原樣複製；奇數長度捨掉最後一個位元組
    /// </summary>
    public static byte[] TimeStretch(ReadOnlySpan<byte> pcm16Mono, double speed, int sampleRate)
    {
        var count = pcm16Mono.Length / 2;
        if (count == 0 || !double.IsFinite(speed) || speed <= 0 || Math.Abs(speed - 1) < StretchTolerance)
            return pcm16Mono[..(count * 2)].ToArray();
        var frame = Math.Max(4, (int)(sampleRate * FrameSeconds) & ~1);
        var hop = frame / 2;
        var tolerance = Math.Max(1, (int)(sampleRate * ToleranceSeconds));
        // 尾端補零：最後幾格的搜尋與自然接續會超出原本的長度
        var input = new float[count + 2 * frame + 2 * tolerance];
        for (var i = 0; i < count; i++)
            input[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm16Mono[(i * 2)..]);
        // 能量的前綴和（正規化互相關的分母，每個候選位置 O(1)）
        var energy = new double[input.Length + 1];
        for (var i = 0; i < input.Length; i++)
            energy[i + 1] = energy[i] + (double)input[i] * input[i];
        var window = new float[frame];
        for (var n = 0; n < frame; n++)
            window[n] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * n / frame));

        var outLength = (int)Math.Round(count / speed);
        var output = new float[outLength + frame];
        var previous = 0;
        for (var k = 0; k * hop < outLength; k++)
        {
            var position = 0;
            if (k > 0)
            {
                var nominal = Math.Clamp((int)Math.Round(k * hop * speed), 0, input.Length - frame);
                var reference = input.AsSpan(Math.Min(previous + hop, input.Length - frame), frame);
                var low = Math.Max(0, nominal - tolerance);
                var high = Math.Min(input.Length - frame, nominal + tolerance);
                position = nominal;
                var best = double.NegativeInfinity;
                for (var c = low; c <= high; c++)
                {
                    var e = energy[c + frame] - energy[c];
                    var score = e > 1e-6 ? Dot(reference, input.AsSpan(c, frame)) / Math.Sqrt(e) : 0;
                    if (score > best)
                    {
                        best = score;
                        position = c;
                    }
                }
            }
            var target = output.AsSpan(k * hop, frame);
            var source = input.AsSpan(position, frame);
            for (var n = 0; n < frame; n++)
                target[n] += window[n] * source[n];
            previous = position;
        }

        var result = new byte[outLength * 2];
        for (var i = 0; i < outLength; i++)
            BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(i * 2), (short)Math.Clamp(MathF.Round(output[i]), short.MinValue, short.MaxValue));
        return result;
    }

    private static double Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var sum = Vector<float>.Zero;
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
        for (var i = 0; i < va.Length; i++)
            sum += va[i] * vb[i];
        double total = Vector.Dot(sum, Vector<float>.One);
        for (var i = va.Length * Vector<float>.Count; i < a.Length; i++)
            total += a[i] * b[i];
        return total;
    }

    /// <summary>16 位元單聲道 PCM 加上 44 位元組的 RIFF／WAVE 檔頭（PlaySound 的 SND_MEMORY 要完整的 WAV）；奇數長度捨掉最後一個位元組</summary>
    public static byte[] Wave(ReadOnlySpan<byte> pcm16Mono, int sampleRate)
    {
        var data = pcm16Mono[..(pcm16Mono.Length & ~1)];
        var wave = new byte[44 + data.Length];
        var span = wave.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + data.Length);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);           // fmt 區塊長度
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);            // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);            // 單聲道
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * 2); // 每秒位元組數
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);            // 每個樣本的位元組數
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);           // 位元深度
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], data.Length);
        data.CopyTo(span[44..]);
        return wave;
    }
}
