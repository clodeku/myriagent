using System.Collections.Concurrent;
using Myriagent.Voice;

namespace Myriagent.Tests.TestSupport;

/// <summary>假的語音辨識器（中樞測試用，不跑 Python）：記下要辨識的檔，回 <see cref="Result"/>；<see cref="Failure"/> 不是 null 時丟它；<see cref="Gate"/> 讓辨識停住</summary>
internal sealed class FakeVoiceTranscriber : IVoiceTranscriber
{
    public ConcurrentQueue<string> Paths { get; } = new();

    public Func<string, VoiceTranscript> Result { get; set; } = _ => new VoiceTranscript("請幫我整理九月的報表", 12.4);

    public Exception? Failure { get; set; }

    public TaskCompletionSource? Gate { get; set; }

    public async Task<VoiceTranscript> TranscribeAsync(string audioPath, CancellationToken cancellationToken)
    {
        Paths.Enqueue(audioPath);
        if (Gate is { } gate)
            await gate.Task;
        if (Failure is { } failure)
            throw failure;
        return Result(audioPath);
    }
}
