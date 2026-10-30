using System.Text;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Media;

namespace Myriagent.Voice;

/// <summary>一則錄音的逐字稿與長度（秒；讀不到是 null）</summary>
internal sealed record VoiceTranscript(string Text, double? DurationSeconds);

/// <summary>辨識失敗：訊息是給人看的說明（中樞放進提示）</summary>
internal sealed class VoiceTranscribeException(string message) : Exception(message);

/// <summary>語音訊息的轉文字（中樞經它呼叫；中樞測試用假的，不跑 Python）。任何執行緒都可以呼叫</summary>
internal interface IVoiceTranscriber
{
    /// <summary>把錄音檔轉成逐字稿；失敗丟 <see cref="VoiceTranscribeException"/></summary>
    Task<VoiceTranscript> TranscribeAsync(string audioPath, CancellationToken cancellationToken);
}

/// <summary>
/// 以本機的 faster-whisper 辨識（2026-10-23 使用者選的做法：app 先轉成逐字稿、連錄音檔交給代理）：第一則語音時啟動一個常駐的 Python 程序
/// （<c>python -X utf8 -u whisper_worker.py</c>，腳本由這裡寫到工作資料夾），載入模型（GPU 的 large-v3，不行就 CPU 的 small）之後每行收一個請求；
/// 錄音由 ffmpeg 解碼成 16 kHz 單聲道交給模型（faster-whisper 1.2 和新版 PyAV 不相容，不用它自己解碼）。閒置 10 分鐘就結束（釋放顯示卡記憶體）。
/// 程序在 app 的工作物件裡（app 結束時跟著結束），輸出以專用執行緒讀（<see cref="AgentProcess"/>）。不呼叫 Windows 的語音輸入
/// </summary>
internal sealed class WhisperVoiceTranscriber : IVoiceTranscriber, IDisposable
{
    /// <summary>腳本檔名（內容改了就換名字，舊的留著不影響）</summary>
    internal const string ScriptFileName = "whisper_worker_v1.py";

    private readonly object gate = new();
    private readonly string workDirectory;
    private readonly System.Threading.Timer idleTimer;
    private Worker? worker;
    private long nextId;
    private bool disposed;

    /// <summary>上一個程序沒等到 ready 就結束（顯示卡的程式庫讓程序當掉之類）：下一個只用 CPU</summary>
    private bool cpuOnly;

    private sealed class Worker(AgentProcess process)
    {
        public AgentProcess Process { get; } = process;

        public TaskCompletionSource<VoiceWorkerLine> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Dictionary<long, TaskCompletionSource<VoiceWorkerLine>> Pending { get; } = new();

        public bool Exited;

        public bool Answered;

        public DateTime LastUsedUtc = DateTime.UtcNow;
    }

    public WhisperVoiceTranscriber(string workDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectory);
        this.workDirectory = workDirectory;
        idleTimer = new System.Threading.Timer(_ => StopIfIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public async Task<VoiceTranscript> TranscribeAsync(string audioPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);
        Worker w;
        long id;
        bool first;
        var result = new TaskCompletionSource<VoiceWorkerLine>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            w = worker is { Exited: false } alive ? alive : worker = StartWorker();
            first = !w.Answered;
            id = ++nextId;
            w.Pending[id] = result;
            w.LastUsedUtc = DateTime.UtcNow;
        }

        var timeout = first ? VoiceToolRules.FirstRequestTimeout : VoiceToolRules.RequestTimeout;
        try
        {
            var ready = await w.Ready.Task.WaitAsync(timeout, cancellationToken);
            if (ready.Event != "ready")
                throw new VoiceTranscribeException(ready.Error ?? "語音辨識程序沒有啟動");
            await w.Process.WriteLineAsync(VoiceToolRules.RequestLine(id, audioPath));
            var line = await result.Task.WaitAsync(timeout, cancellationToken);
            if (!line.Ok)
                throw new VoiceTranscribeException("語音辨識失敗：" + (line.Error ?? "原因不明"));
            lock (gate)
                w.Answered = true;
            return new VoiceTranscript(line.Text ?? string.Empty, line.Duration);
        }
        catch (TimeoutException)
        {
            // 卡住的程序不再用（下一則重新啟動）
            Log.Warn($"語音辨識逾時（{timeout.TotalSeconds:0} 秒），結束辨識程序");
            Stop(w);
            throw new VoiceTranscribeException(first ? "語音辨識逾時（第一次使用要載入或下載模型，請再試一次）" : "語音辨識逾時");
        }
        catch (IOException ex)
        {
            throw new VoiceTranscribeException("語音辨識程序沒有回應：" + ex.Message);
        }
        finally
        {
            lock (gate)
            {
                w.Pending.Remove(id);
                w.LastUsedUtc = DateTime.UtcNow;
            }
        }
    }

    /// <summary>在鎖裡：找 Python 與 ffmpeg、寫腳本、啟動程序（找不到或啟動失敗丟 <see cref="VoiceTranscribeException"/>）</summary>
    private Worker StartWorker()
    {
        var python = VoiceToolRules.FindPython(
            [
                Environment.GetEnvironmentVariable("PATH"),
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
            ], File.Exists)
            ?? throw new VoiceTranscribeException("這台電腦找不到 Python（語音訊息以本機的 faster-whisper 辨識；安裝 Python 與 pip install faster-whisper 後再試）");
        var ffmpeg = FfmpegTool.FfmpegPath
            ?? throw new VoiceTranscribeException("這台電腦找不到 ffmpeg（語音訊息要用它讀錄音檔；安裝後再試）");

        Directory.CreateDirectory(workDirectory);
        var script = Path.Combine(workDirectory, ScriptFileName);
        if (!File.Exists(script) || File.ReadAllText(script, Encoding.UTF8) != Script)
            File.WriteAllText(script, Script, new UTF8Encoding(false));

        var arguments = new List<string> { script, "--ffmpeg", ffmpeg, "--gpu-model", VoiceToolRules.GpuModel, "--cpu-model", VoiceToolRules.CpuModel };
        if (cpuOnly)
            arguments.Add("--cpu-only");
        AgentProcess process;
        try
        {
            process = AgentProcess.Start(new AgentLauncher(python, ["-X", "utf8", "-u"]), arguments, workDirectory, env =>
            {
                env["PYTHONIOENCODING"] = "utf-8";
                env["HF_HUB_DISABLE_TELEMETRY"] = "1";
                env["HF_HUB_DISABLE_PROGRESS_BARS"] = "1";
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new VoiceTranscribeException("語音辨識程序啟動失敗：" + ex.Message);
        }

        var w = new Worker(process);
        process.LineReceived += (_, line) => OnLine(w, line);
        process.Exited += (_, code) => OnExited(w, code);
        process.BeginReading();
        Log.Info($"語音辨識程序已啟動（PID {process.ProcessId}{(cpuOnly ? "，只用 CPU" : string.Empty)}）");
        return w;
    }

    private void OnLine(Worker w, string text)
    {
        if (VoiceToolRules.ParseWorkerLine(text) is not { } line)
            return;
        if (line.Event == "ready")
        {
            Log.Info($"語音辨識模型已載入（{line.Device}、{line.Model}）");
            w.Ready.TrySetResult(line);
            return;
        }
        if (line.Event == "fatal")
        {
            w.Ready.TrySetResult(line with { Error = VoiceToolRules.StartFailure(line.Error, null) });
            return;
        }
        if (line.Id is not { } id)
            return;
        TaskCompletionSource<VoiceWorkerLine>? pending;
        lock (gate)
            w.Pending.TryGetValue(id, out pending);
        pending?.TrySetResult(line);
    }

    private void OnExited(Worker w, int? exitCode)
    {
        List<TaskCompletionSource<VoiceWorkerLine>> pending;
        var tail = w.Process.GetStderrTail(600);
        lock (gate)
        {
            w.Exited = true;
            pending = w.Pending.Values.ToList();
            // 沒等到 ready 也沒有說明就結束（顯示卡的程式庫讓程序當掉之類）：下一次只用 CPU
            if (!w.Ready.Task.IsCompleted && !disposed)
                cpuOnly = true;
        }
        var message = VoiceToolRules.StartFailure(null, tail);
        if (!w.Ready.Task.IsCompleted)
            Log.Warn($"語音辨識程序在載入模型前結束（{exitCode}）：{tail}");
        w.Ready.TrySetResult(new VoiceWorkerLine("fatal", null, false, null, null, message, null, null));
        foreach (var p in pending)
            p.TrySetResult(new VoiceWorkerLine(null, null, false, null, null, "語音辨識程序結束了" + (string.IsNullOrWhiteSpace(tail) ? string.Empty : "：" + tail.Trim()), null, null));
    }

    /// <summary>計時器：沒有在辨識、閒置超過 <see cref="VoiceToolRules.IdleTimeout"/> 就結束程序</summary>
    private void StopIfIdle()
    {
        Worker? idle = null;
        lock (gate)
        {
            if (worker is { Exited: false } w && w.Pending.Count == 0 && DateTime.UtcNow - w.LastUsedUtc > VoiceToolRules.IdleTimeout)
                idle = w;
        }
        if (idle != null)
        {
            Log.Info("語音辨識程序閒置，結束以釋放記憶體");
            Stop(idle);
        }
    }

    private void Stop(Worker w)
    {
        lock (gate)
        {
            if (ReferenceEquals(worker, w))
                worker = null;
        }
        try
        {
            // 關掉 stdin 讓腳本的迴圈結束；沒在時限內結束就整棵結束
            _ = w.Process.CloseAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            Log.Warn($"結束語音辨識程序失敗：{ex.Message}");
        }
    }

    public void Dispose()
    {
        Worker? w;
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            w = worker;
            worker = null;
        }
        idleTimer.Dispose();
        if (w != null)
        {
            try
            {
                w.Process.Kill();
            }
            catch (Exception ex)
            {
                Log.Warn($"結束語音辨識程序失敗：{ex.Message}");
            }
        }
    }

    /// <summary>辨識程序的腳本（寫到工作資料夾執行）。stdout 只寫一行一個 JSON；記錄寫 stderr</summary>
    internal const string Script = """
        # Clode Myriagent 語音訊息的本機辨識程序（faster-whisper）。由 app 啟動，不要直接執行：
        # stdin 每行一個請求 {"id": n, "path": "錄音檔"}，stdout 每行一個結果 {"id", "ok", "text", "duration"} 或 {"id", "ok": false, "error"}；
        # 一開始先回 {"event": "ready", "device", "model"}（或 {"event": "fatal", "error"} 後結束）。stdin 關閉就結束。
        import argparse
        import json
        import subprocess
        import sys

        PROMPT = '以下是臺灣繁體中文的語音指令。'
        NO_WINDOW = 0x08000000


        def emit(obj):
            sys.stdout.write(json.dumps(obj, ensure_ascii=False) + '\n')
            sys.stdout.flush()


        def main():
            ap = argparse.ArgumentParser()
            ap.add_argument('--ffmpeg', required=True)
            ap.add_argument('--gpu-model', default='large-v3')
            ap.add_argument('--cpu-model', default='small')
            ap.add_argument('--cpu-only', action='store_true')
            args = ap.parse_args()
            try:
                import numpy as np
                from faster_whisper import WhisperModel
            except Exception as e:
                emit({'event': 'fatal', 'error': 'faster-whisper 沒有安裝：' + repr(e)})
                return 2

            choices = [('cpu', 'int8', args.cpu_model)] if args.cpu_only else [('cuda', 'float16', args.gpu_model), ('cpu', 'int8', args.cpu_model)]
            model = None
            used = None
            errors = []
            for device, compute, name in choices:
                try:
                    candidate = WhisperModel(name, device=device, compute_type=compute)
                    # 先辨識一秒的靜音：顯示卡的程式庫缺了的話在這裡就會失敗，改用下一個
                    segments, _ = candidate.transcribe(np.zeros(16000, dtype=np.float32), language='zh', beam_size=1)
                    list(segments)
                    model = candidate
                    used = (device, name)
                    break
                except Exception as e:
                    errors.append(device + ': ' + repr(e)[:300])
                    print('load failed: ' + device + ' ' + repr(e), file=sys.stderr, flush=True)
            if model is None:
                emit({'event': 'fatal', 'error': '載入語音辨識模型失敗：' + ' / '.join(errors)})
                return 2
            emit({'event': 'ready', 'device': used[0], 'model': used[1]})

            for line in sys.stdin:
                line = line.strip()
                if not line:
                    continue
                try:
                    request = json.loads(line)
                    rid = request['id']
                    path = request['path']
                except Exception:
                    continue
                try:
                    decoded = subprocess.run(
                        [args.ffmpeg, '-nostdin', '-loglevel', 'error', '-i', path, '-f', 'f32le', '-ac', '1', '-ar', '16000', '-'],
                        stdin=subprocess.DEVNULL, capture_output=True, timeout=300, creationflags=NO_WINDOW)
                    if decoded.returncode != 0:
                        raise RuntimeError('ffmpeg 讀不了錄音檔：' + decoded.stderr.decode('utf-8', 'replace').strip()[-300:])
                    audio = np.frombuffer(decoded.stdout, dtype=np.float32)
                    text = ''
                    if len(audio) > 0:
                        segments, _ = model.transcribe(audio, language='zh', initial_prompt=PROMPT, vad_filter=True, beam_size=1,
                                                       condition_on_previous_text=False)
                        text = ''.join(s.text for s in segments)
                    emit({'id': rid, 'ok': True, 'text': text, 'duration': len(audio) / 16000.0})
                except Exception as e:
                    emit({'id': rid, 'ok': False, 'error': repr(e)[:500]})
            return 0


        if __name__ == '__main__':
            sys.exit(main())
        """;
}
