using System.Collections.Concurrent;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Speech;

namespace FakeDevice;

/// <summary>
/// 無頭的宿主（<see cref="IAgentHost"/>）：自己的一條「UI 執行緒」與單執行緒的 SynchronizationContext，BeginInvoke 一律 Post
/// （照正式環境：AppController 的 BeginInvoke 一律排進訊息迴圈，呼叫端不會同步執行）。中樞、帳戶管理員與同步協調者只在這條執行緒上動
/// （CLAUDE.md「狀態只在 UI 執行緒」）；async 的接續也回到這條執行緒。對話框、外殼、通知一律不做（記錄而已）
/// </summary>
internal sealed class HeadlessHost : IAgentHost, IDisposable
{
    private readonly BlockingCollection<Action> queue = new();
    private readonly Thread thread;
    private readonly ConcurrentQueue<Exception> errors = new();

    public HeadlessHost(string name = "fake-device UI")
    {
        Context = new HostSynchronizationContext(this);
        thread = new Thread(Run) { IsBackground = true, Name = name };
        thread.Start();
    }

    public SynchronizationContext Context { get; }

    /// <summary>UI 執行緒上沒被接住的例外（結束時回報）</summary>
    public IReadOnlyCollection<Exception> Errors => errors.ToList();

    public bool IsCurrent => Thread.CurrentThread == thread;

    /// <summary>最後的系統匣提示文字（中樞組的，含同步段）</summary>
    public string? TrayText { get; private set; }

    public void BeginInvoke(Action action) => Post(action);

    public void Post(Action action)
    {
        try
        {
            queue.Add(action);
        }
        catch (InvalidOperationException)
        {
            // 已關閉（結束後才到的計時器）：照正式環境，訊息迴圈結束後排的工作不執行
        }
    }

    /// <summary>在 UI 執行緒執行並等它完成（例外原樣丟回呼叫端）</summary>
    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                done.TrySetResult(func());
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        return done.Task;
    }

    public Task InvokeAsync(Action action) => InvokeAsync(() =>
    {
        action();
        return true;
    });

    /// <summary>在 UI 執行緒啟動 async 工作並等它完成（它的接續也在 UI 執行緒）</summary>
    public Task InvokeAsync(Func<Task> func)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async () =>
        {
            try
            {
                await func();
                done.TrySetResult();
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        return done.Task;
    }

    /// <summary>等 UI 執行緒把目前排著的工作（含其後排進來的接續）做完</summary>
    public async Task DrainAsync(int rounds = 3)
    {
        for (var i = 0; i < rounds; i++)
            await InvokeAsync(() => { });
    }

    public IReadOnlyList<string> PickFiles(IAgentView view) => [];

    public string? PickFolder(IAgentView view, string? initialDirectory) => null;

    public string? PickSaveFile(IAgentView view, string fileName) => null;

    public void OpenWithShell(string pathOrUrl) => Log.Info($"fake-device：略過開啟 {pathOrUrl}");

    public void RevealInExplorer(string path)
    {
    }

    public void CopyText(string text)
    {
    }

    public bool CopyFile(string path) => false;

    public void OpenPopout(string conversationId)
    {
    }

    public void ConversationDeleted(string conversationId)
    {
    }

    /// <summary>沒有視窗：一律當成在背景（中樞照常送通知，這裡只記錄）</summary>
    public bool IsViewForeground(string viewId) => false;

    public void Notify(string title, string message, string? conversationId) => Log.Info($"fake-device 通知：{title}｜{message}");

    public void RequestAttention(IReadOnlyCollection<string> viewIds)
    {
    }

    public void Speak(string text, VoiceTuning tuning, Action? started)
    {
    }

    public void StopSpeaking()
    {
    }

    public void ApplyTheme(string theme)
    {
    }

    public void UpdateTray(string tooltip, int running, int waiting) => TrayText = tooltip;

    public void Dispose()
    {
        queue.CompleteAdding();
        if (!IsCurrent)
            thread.Join(TimeSpan.FromSeconds(5));
    }

    private void Run()
    {
        SynchronizationContext.SetSynchronizationContext(Context);
        foreach (var action in queue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
                Log.Error("fake-device 的 UI 執行緒有未處理的例外", ex);
            }
        }
    }

    private sealed class HostSynchronizationContext(HeadlessHost host) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => host.Post(() => d(state));

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (host.IsCurrent)
            {
                d(state);
                return;
            }

            host.InvokeAsync(() => d(state)).GetAwaiter().GetResult();
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}
