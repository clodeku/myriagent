using System.Collections.Concurrent;
using Myriagent.Core;

namespace Myriagent.Sync;

/// <summary>
/// 同步引擎的專用執行緒（MyriagentSync，背景執行緒）：上面裝單執行緒的 SynchronizationContext，排進來的工作依序執行，
/// 引擎的 async 方法的接續也回到這條執行緒——引擎的狀態只在這裡改（不加鎖），SQLite 的同步呼叫（含 fsync）也只在這裡。
/// </summary>
internal sealed class SyncEngineThread
{
    private readonly BlockingCollection<Action> queue = new();
    private readonly Thread thread;
    private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int started;
    private int pending;

    public SyncEngineThread(string name)
    {
        Context = new EngineSynchronizationContext(this);
        thread = new Thread(Run) { IsBackground = true, Name = name };
    }

    public SynchronizationContext Context { get; }

    public bool IsCurrent => Thread.CurrentThread == thread;

    /// <summary>執行緒結束了（<see cref="Complete"/> 之後佇列做完）</summary>
    public Task Exited => exited.Task;

    public int ManagedThreadId => thread.ManagedThreadId;

    /// <summary>排著還沒做完的工作數（正在執行的那一個也算；測試等引擎閒置用）</summary>
    public int Pending => Volatile.Read(ref pending);

    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) == 0)
            thread.Start();
    }

    /// <summary>排一個工作（任何執行緒；已結束時丟掉）</summary>
    public void Post(Action action)
    {
        Interlocked.Increment(ref pending);
        try
        {
            queue.Add(action);
        }
        catch (InvalidOperationException)
        {
            // 已結束：晚到的回呼（舊連線、計時器）直接丟掉
            Interlocked.Decrement(ref pending);
        }
    }

    /// <summary>不再收新工作；已排的做完後執行緒結束</summary>
    public void Complete()
    {
        try
        {
            queue.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
        }

        if (Volatile.Read(ref started) == 0)
            exited.TrySetResult();
    }

    private void Run()
    {
        SynchronizationContext.SetSynchronizationContext(Context);
        try
        {
            foreach (var action in queue.GetConsumingEnumerable())
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    // 引擎的流程都自己接例外；到這裡的是程式錯誤，記下來、執行緒照常
                    Log.Error("同步引擎的工作丟出未處理的例外", ex);
                }
                finally
                {
                    Interlocked.Decrement(ref pending);
                }
            }
        }
        finally
        {
            exited.TrySetResult();
        }
    }

    private sealed class EngineSynchronizationContext(SyncEngineThread owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => owner.Post(() => d(state));

        /// <summary>同步的 Send 會讓呼叫端卡住等引擎執行緒（執行緒結束後就永遠等不到）：只允許在引擎執行緒上呼叫</summary>
        public override void Send(SendOrPostCallback d, object? state)
        {
            if (!owner.IsCurrent)
                throw new NotSupportedException("同步引擎的執行緒不支援從別的執行緒 Send");
            d(state);
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}
