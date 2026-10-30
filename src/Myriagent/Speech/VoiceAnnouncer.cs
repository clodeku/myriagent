using System.Diagnostics;
using System.Runtime.InteropServices;
using Myriagent.Core;
using Myriagent.Hub;

namespace Myriagent.Speech;

/// <summary>念一句話的聲音引擎（只在語音通知的工作執行緒上建立與使用）</summary>
internal interface ISpeechEngine : IDisposable
{
    /// <summary>
    /// 以 <paramref name="tuning"/> 的語速與音調念出 <paramref name="text"/>：念完，或 <paramref name="shouldStop"/> 回 true（中途停下）才返回。
    /// 聲音開始出來的那一刻呼叫 <paramref name="onStart"/>（畫面同時閃那個對話的標頭）；沒念出來就不呼叫
    /// </summary>
    void Speak(string text, VoiceTuning tuning, Func<bool> shouldStop, Action onStart);
}

/// <summary>
/// 語音通知（設定 voiceNotifications）：一條背景工作執行緒依序念佇列裡的句子，一句念完才念下一句（佇列上限與重複的規則見
/// <see cref="VoiceNotifyRules.ShouldQueue"/>）。聲音引擎在第一次要念時才建立；建立失敗（這台沒有 Windows 語音）記一次警告，之後都不念。
/// 呼叫端可在任何執行緒呼叫；每句帶自己的語速與音調（設定改了之後排進來的句子才用新的），也可以帶一個「開始念了」的回呼（在工作執行緒上呼叫，呼叫端自己排回 UI 執行緒；略過的句子不呼叫）。
/// Stop 清掉佇列並停下正在念的那句（關掉設定時）；Dispose 之後不再念
/// </summary>
internal sealed class VoiceAnnouncer : IDisposable
{
    private readonly Func<ISpeechEngine> createEngine;
    private readonly object gate = new();
    private readonly Queue<(string Text, VoiceTuning Tuning, Action? Started)> pending = new();
    private (string Text, DateTime At)? last;
    private Thread? worker;
    private bool speaking;

    /// <summary>Stop 與 Dispose 時加一：正在念的那句開始時記下的值不同了就停下</summary>
    private long stopGeneration;
    private bool unavailable;
    private bool disposed;

    public VoiceAnnouncer(Func<ISpeechEngine> createEngine)
    {
        this.createEngine = createEngine;
    }

    /// <summary>排進佇列（佇列滿了、或同一句剛念過就略過）；<paramref name="started"/>＝聲音開始出來時呼叫一次，<paramref name="tuning"/>＝語速與音調（null＝預設）</summary>
    public void Speak(string text, Action? started = null, VoiceTuning? tuning = null)
    {
        lock (gate)
        {
            if (disposed || unavailable)
                return;
            var now = DateTime.UtcNow;
            if (!VoiceNotifyRules.ShouldQueue(text, pending.Count, last, now))
                return;
            last = (text, now);
            pending.Enqueue((text, tuning ?? VoiceTuning.Default, started));
            if (worker == null)
            {
                // 工作執行緒到 Dispose（或引擎建不起來）才結束；SAPI 的 SpVoice 兩種 apartment 都能用，MTA 不需要訊息迴圈
                worker = new Thread(Run) { IsBackground = true, Name = "語音通知" };
                worker.SetApartmentState(ApartmentState.MTA);
                worker.Start();
            }
            else
            {
                Monitor.PulseAll(gate);
            }
        }
    }

    /// <summary>停下正在念的那句、清掉還沒念的</summary>
    public void Stop()
    {
        lock (gate)
        {
            pending.Clear();
            last = null;
            stopGeneration++;
            Monitor.PulseAll(gate);
        }
    }

    /// <summary>佇列空了、也沒有正在念的句子</summary>
    public bool IsIdle
    {
        get
        {
            lock (gate)
                return pending.Count == 0 && !speaking;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            pending.Clear();
            stopGeneration++;
            Monitor.PulseAll(gate);
        }
    }

    /// <summary>「開始念了」的回呼出錯（例如 app 正在結束、排不回 UI 執行緒）不影響念這一句</summary>
    private static void Notify(Action? started)
    {
        try
        {
            started?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn($"語音通知的開始回呼：{ex.Message}");
        }
    }

    private void Run()
    {
        ISpeechEngine? engine = null;
        try
        {
            while (true)
            {
                string text;
                VoiceTuning tuning;
                Action? started;
                long generation;
                lock (gate)
                {
                    while (pending.Count == 0 && !disposed)
                        Monitor.Wait(gate);
                    if (disposed)
                        return;
                    (text, tuning, started) = pending.Dequeue();
                    generation = stopGeneration;
                    speaking = true;
                }
                try
                {
                    engine ??= createEngine();
                    engine.Speak(text, tuning, () =>
                    {
                        lock (gate)
                            return disposed || stopGeneration != generation;
                    }, () => Notify(started));
                }
                catch (Exception ex) when (engine == null)
                {
                    Log.Warn($"語音通知無法使用：{ex.Message}");
                    lock (gate)
                    {
                        unavailable = true;
                        pending.Clear();
                    }
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warn($"語音通知念不出來：{ex.Message}");
                }
                finally
                {
                    lock (gate)
                        speaking = false;
                }
            }
        }
        finally
        {
            try
            {
                engine?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn($"關閉語音通知：{ex.Message}");
            }
        }
    }
}

/// <summary>
/// Windows 內建的 SAPI 5（SAPI.SpVoice 的自動化介面，以 dynamic 呼叫）。先選偏好的聲音（<see cref="VoiceNotifyRules.PreferredVoiceNames"/>：雅婷），
/// 沒有就照語言順序（<see cref="VoiceNotifyRules.VoiceLanguages"/>：每個語言先找桌面聲音、再找 OneCore 的聲音），都沒有就用系統預設的聲音。
/// 每句照語速與音調算出做法（<see cref="SpeechAudioRules.Plan"/>）：以 SAPI 的整數語速念成記憶體裡的 PCM、零頭做時間伸縮，再以升調的取樣率
/// 交給 PlaySound 播放（見 SpeechAudioRules）。文字一律當純文字念（不解析 SAPI XML，代理的文字可能有角括號）
/// </summary>
internal sealed class SapiSpeechEngine : ISpeechEngine
{
    private const int IsNotXml = 16;          // SVSFIsNotXML
    private const string OneCoreVoices = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech_OneCore\Voices";

    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndMemory = 0x0004;

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", SetLastError = true)]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);

    private readonly dynamic voice;
    private readonly string? waveFile;

    private SapiSpeechEngine(dynamic voice, string? waveFile, string? voiceName)
    {
        this.voice = voice;
        this.waveFile = waveFile;
        VoiceName = voiceName;
    }

    /// <summary>選到的聲音（null＝系統預設）</summary>
    public string? VoiceName { get; }

    /// <param name="waveFile">不播放，把調好語速與音調、要播的 WAV 寫到這個檔（測試與試聽用；每句覆寫）</param>
    public static SapiSpeechEngine Create(string? waveFile = null)
    {
        var type = Type.GetTypeFromProgID("SAPI.SpVoice") ?? throw new InvalidOperationException("這台電腦沒有 Windows 語音（SAPI）");
        dynamic voice = Activator.CreateInstance(type) ?? throw new InvalidOperationException("無法建立 SAPI.SpVoice");
        try
        {
            var name = SelectVoice((object)voice);
            Log.Info($"語音通知的聲音：{name ?? "系統預設"}");
            return new SapiSpeechEngine((object)voice, waveFile, name);
        }
        catch
        {
            Marshal.FinalReleaseComObject((object)voice);
            throw;
        }
    }

    public void Speak(string text, VoiceTuning tuning, Func<bool> shouldStop, Action onStart)
    {
        var plan = SpeechAudioRules.Plan(tuning);
        var pcm = Render(text, plan.SapiRate);
        if (pcm.Length == 0 || shouldStop())
            return;
        if (plan.Stretch != 1)
            pcm = SpeechAudioRules.TimeStretch(pcm, plan.Stretch, SpeechAudioRules.RenderSampleRate);
        var rate = plan.PlaybackSampleRate;
        var wave = SpeechAudioRules.Wave(pcm, rate);
        onStart();
        if (waveFile != null)
        {
            File.WriteAllBytes(waveFile, wave);
            return;
        }
        Play(wave, SpeechAudioRules.Duration(wave.Length - 44, rate), shouldStop);
    }

    /// <summary>以 SAPI 語速 <paramref name="rate"/> 念成 24 kHz 16 位元單聲道的 PCM（念到記憶體很快，不到 0.1 秒）</summary>
    private byte[] Render(string text, int rate)
    {
        var streamType = Type.GetTypeFromProgID("SAPI.SpMemoryStream") ?? throw new InvalidOperationException("無法建立 SAPI.SpMemoryStream");
        dynamic stream = Activator.CreateInstance(streamType)!;
        try
        {
            stream.Format.Type = SpeechAudioRules.RenderFormatType;
            voice.AudioOutputStream = stream;
            voice.Rate = rate;
            voice.Speak(text, IsNotXml);
            return (byte[])stream.GetData();
        }
        finally
        {
            Marshal.FinalReleaseComObject((object)stream);
        }
    }

    /// <summary>
    /// 以 PlaySound 非同步播放記憶體裡的 WAV：播放期間緩衝區固定住，播完（或被要求停下）先停止播放再放開，
    /// 播放器不會讀到已經移動或回收的記憶體
    /// </summary>
    private static void Play(byte[] wave, TimeSpan length, Func<bool> shouldStop)
    {
        var handle = GCHandle.Alloc(wave, GCHandleType.Pinned);
        try
        {
            if (!PlaySound(handle.AddrOfPinnedObject(), IntPtr.Zero, SndMemory | SndAsync | SndNoDefault))
                throw new InvalidOperationException($"PlaySound 失敗（{Marshal.GetLastPInvokeError()}）");
            var clock = Stopwatch.StartNew();
            var end = length + TimeSpan.FromMilliseconds(150);
            while (clock.Elapsed < end && !shouldStop())
                Thread.Sleep(50);
        }
        finally
        {
            PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
            handle.Free();
        }
    }

    public void Dispose() => Marshal.FinalReleaseComObject((object)voice);

    /// <summary>先找偏好的聲音（雅婷），再照語言順序找第一個聲音，設定給 voice 並回它的名稱；都找不到回 null（沿用系統預設）</summary>
    private static string? SelectVoice(object voice)
    {
        foreach (var name in VoiceNotifyRules.PreferredVoiceNames)
        {
            if (Use(voice, FindToken(OneCoreTokens("Language=404"), name) ?? FindToken(DesktopTokens(voice, "Language=404"), name)) is { } preferred)
                return preferred;
        }
        foreach (var language in VoiceNotifyRules.VoiceLanguages)
        {
            var query = "Language=" + language;
            if (Use(voice, FindToken(DesktopTokens(voice, query), null) ?? FindToken(OneCoreTokens(query), null)) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>把 token 設定給 voice，回聲音的名稱；token 是 null 或設定失敗回 null</summary>
    private static string? Use(object voice, object? token)
    {
        if (token == null)
            return null;
        try
        {
            dynamic v = voice;
            dynamic t = token;
            v.Voice = t;
            return (string)t.GetDescription();
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            Log.Warn($"語音通知選聲音：{ex.Message}");
            return null;
        }
    }

    private static object? DesktopTokens(object voice, string query)
    {
        try
        {
            dynamic v = voice;
            return (object?)v.GetVoices(query, string.Empty);
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }
    }

    /// <summary>OneCore 的聲音（雅婷、志偉在這裡）；沒有 OneCore 的機碼回 null</summary>
    private static object? OneCoreTokens(string query)
    {
        try
        {
            var categoryType = Type.GetTypeFromProgID("SAPI.SpObjectTokenCategory");
            if (categoryType == null)
                return null;
            dynamic category = Activator.CreateInstance(categoryType)!;
            category.SetId(OneCoreVoices, false);
            return (object?)category.EnumerateTokens(query, string.Empty);
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }
    }

    /// <summary>清單（ISpeechObjectTokens）裡名稱含 <paramref name="name"/> 的第一個（null＝第一個）；沒有回 null</summary>
    private static object? FindToken(object? tokens, string? name)
    {
        if (tokens == null)
            return null;
        try
        {
            dynamic list = tokens;
            for (var i = 0; i < (int)list.Count; i++)
            {
                dynamic token = list.Item(i);
                if (name == null || ((string)token.GetDescription()).Contains(name, StringComparison.OrdinalIgnoreCase))
                    return (object)token;
            }
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
        }
        return null;
    }
}
