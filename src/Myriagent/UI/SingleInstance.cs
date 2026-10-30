using Myriagent.Update;

namespace Myriagent.UI;

/// <summary>Acquire 的結果（回 null 時呼叫端據此決定要不要說明）</summary>
internal enum AcquireOutcome
{
    /// <summary>這一份成為實例（第一個，或等前一個結束後接手）</summary>
    Acquired,

    /// <summary>叫醒了已在執行的實例</summary>
    ActivatedExisting,

    /// <summary>已在執行的（較舊的）實例接下交棒請求：它會排空對話、啟動這一版的執行檔</summary>
    HandedOver,
}

/// <summary>
/// 單一實例（每個資料夾一個）：命名 mutex 決定誰是第一個實例；之後啟動的實例設定「喚醒」事件，第一個實例收到後設定「已處理」事件回應，再把視窗帶到前面。
/// 第一個實例正在結束（收尾代理程序最多約 10 秒）時不回應：新實例等它放開 mutex 後自己接手啟動，這段期間重新啟動不會被吞掉。
/// 較新的版本另有「交棒」事件（InstanceHandoverRules）：先寫好請求檔再設定，第一個實例接下就設定「已接下」，新版直接結束、由舊版排空後啟動它；
/// 沒接下（對方不懂交棒、正在結束、請求不合格）就照原本的喚醒處理。
/// 多視窗（2026-10-25）起另有「開新視窗」事件：使用者再開一次 app 時，懂這個事件的第一個實例（它在 Acquire 時建立這個事件）像 Chrome 一樣開一個新視窗；
/// 新版先試著開啟既有的事件來判斷對方懂不懂，不懂（舊版沒建立）就照原本的喚醒
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Myriagent.Instance";
    private const string ActivateEventName = @"Local\Myriagent.Activate";
    private const string AcknowledgeEventName = @"Local\Myriagent.Activated";
    private const string HandoverEventName = @"Local\Myriagent.Handover";
    private const string HandoverAcceptedEventName = @"Local\Myriagent.HandoverAccepted";
    private const string NewWindowEventName = @"Local\Myriagent.NewWindow";

    /// <summary>等第一個實例回應多久（它的監聽執行緒收到喚醒會立刻回應）</summary>
    public static readonly TimeSpan DefaultAcknowledgeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>第一個實例沒有回應時，等它結束（放開 mutex）多久；等不到就放棄</summary>
    public static readonly TimeSpan DefaultTakeoverTimeout = TimeSpan.FromSeconds(20);

    private readonly Mutex mutex;
    private readonly EventWaitHandle activate;
    private readonly EventWaitHandle acknowledge;
    private readonly EventWaitHandle handover;
    private readonly EventWaitHandle handoverAccepted;
    private readonly EventWaitHandle newWindow;
    private bool disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle activate, EventWaitHandle acknowledge, EventWaitHandle handover, EventWaitHandle handoverAccepted,
        EventWaitHandle newWindow)
    {
        this.mutex = mutex;
        this.activate = activate;
        this.acknowledge = acknowledge;
        this.handover = handover;
        this.handoverAccepted = handoverAccepted;
        this.newWindow = newWindow;
    }

    /// <summary>
    /// 成為這個資料夾的實例（mutex 屬於呼叫的執行緒：Dispose 也要在同一條執行緒）。
    /// 已經有實例在執行就叫醒它並回 null（呼叫端直接結束）；它正在結束、沒有回應時，等它結束後由這個實例接手。
    /// </summary>
    /// <param name="suffix">名稱後綴（ShellRules.InstanceSuffix）</param>
    /// <param name="beforeSignal">叫醒前呼叫（把前景權限交給第一個實例）</param>
    public static SingleInstance? Acquire(string suffix, Action? beforeSignal = null, TimeSpan? acknowledgeTimeout = null, TimeSpan? takeoverTimeout = null,
        bool requestNewWindow = false)
        => Acquire(suffix, out _, beforeSignal, prepareHandover: null, acknowledgeTimeout, takeoverTimeout, requestNewWindow: requestNewWindow);

    /// <param name="outcome">回 null 時是 ActivatedExisting 或 HandedOver</param>
    /// <param name="prepareHandover">已有實例時、叫醒之前呼叫：寫好交棒請求回 true（再送交棒訊號、等對方接下）；null 或回 false＝照原本叫醒</param>
    /// <param name="handoverTimeout">等對方接下交棒多久（預設 InstanceHandoverRules.AcceptTimeout）</param>
    /// <param name="requestNewWindow">已有實例時請它開新視窗（使用者再開一次 app）；對方不懂就照原本叫醒</param>
    public static SingleInstance? Acquire(string suffix, out AcquireOutcome outcome, Action? beforeSignal = null, Func<bool>? prepareHandover = null,
        TimeSpan? acknowledgeTimeout = null, TimeSpan? takeoverTimeout = null, TimeSpan? handoverTimeout = null, bool requestNewWindow = false)
    {
        // 「開新視窗」只有懂的實例才建立：先試著開啟既有的（建立自己的之前），開得到就是對方懂
        var newWindowSupported = false;
        if (requestNewWindow && EventWaitHandle.TryOpenExisting(NewWindowEventName + suffix, out var existingNewWindow))
        {
            newWindowSupported = true;
            existingNewWindow.Dispose();
        }
        // 先建立（或開啟）事件再搶 mutex：第二個實例在第一個實例剛起來時啟動，也一定叫得醒它（AutoReset 事件會保持訊號直到被等待）
        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName + suffix);
        var acknowledge = new EventWaitHandle(false, EventResetMode.AutoReset, AcknowledgeEventName + suffix);
        var handover = new EventWaitHandle(false, EventResetMode.AutoReset, HandoverEventName + suffix);
        var handoverAccepted = new EventWaitHandle(false, EventResetMode.AutoReset, HandoverAcceptedEventName + suffix);
        var newWindow = new EventWaitHandle(false, EventResetMode.AutoReset, NewWindowEventName + suffix);
        var mutex = new Mutex(true, MutexName + suffix, out bool created);
        outcome = AcquireOutcome.Acquired;
        if (!created)
        {
            if (prepareHandover != null && prepareHandover())
            {
                beforeSignal?.Invoke();
                handoverAccepted.Reset();
                handover.Set();
                if (handoverAccepted.WaitOne(handoverTimeout ?? InstanceHandoverRules.AcceptTimeout))
                    outcome = AcquireOutcome.HandedOver;
                else
                    handover.Reset();   // 沒人接（對方不懂交棒或正在結束）：收回訊號，免得之後的實例誤收
            }

            if (outcome != AcquireOutcome.HandedOver)
            {
                outcome = SignalOrTakeOver(mutex, newWindowSupported ? newWindow : activate, acknowledge, beforeSignal,
                    acknowledgeTimeout ?? DefaultAcknowledgeTimeout, takeoverTimeout ?? DefaultTakeoverTimeout)
                    ? AcquireOutcome.Acquired
                    : AcquireOutcome.ActivatedExisting;
            }

            if (outcome != AcquireOutcome.Acquired)
            {
                mutex.Dispose();
                activate.Dispose();
                acknowledge.Dispose();
                handover.Dispose();
                handoverAccepted.Dispose();
                newWindow.Dispose();
                return null;
            }
        }

        activate.Reset();   // 這個實例開始前殘留的訊號（含接手前自己送出的）不算
        handover.Reset();
        newWindow.Reset();
        return new SingleInstance(mutex, activate, acknowledge, handover, handoverAccepted, newWindow);
    }

    /// <summary>
    /// 這個資料夾現在有沒有實例（正在執行，或正在啟動、等著接手）。只探測、不持有 mutex：換版幫手切換 current 之前，
    /// 確認使用者沒有在換版的空窗中自己重新開 app（設計 8.5「幫手不搶單一實例的 mutex」）
    /// </summary>
    public static bool IsRunning(string suffix)
    {
        try
        {
            if (!Mutex.TryOpenExisting(MutexName + suffix, out var existing))
                return false;
            existing.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;   // 有這個名稱但開不了：當成有實例（不換版比在執行中的實例底下切換安全）
        }
    }

    /// <summary>叫醒第一個實例（signal＝喚醒或開新視窗）；回 true＝它沒有回應而且已經結束，mutex 改由這個實例擁有</summary>
    private static bool SignalOrTakeOver(Mutex mutex, EventWaitHandle signal, EventWaitHandle acknowledge, Action? beforeSignal,
        TimeSpan acknowledgeTimeout, TimeSpan takeoverTimeout)
    {
        beforeSignal?.Invoke();
        acknowledge.Reset();
        signal.Set();
        if (acknowledge.WaitOne(acknowledgeTimeout))
            return false;   // 第一個實例收到了，由它把視窗帶到前面

        // 沒有回應：第一個實例正在結束（或剛啟動、還沒開始聽）。等它放開 mutex 後接手
        try
        {
            return mutex.WaitOne(takeoverTimeout);
        }
        catch (AbandonedMutexException)
        {
            return true;   // 第一個實例沒放開 mutex 就結束了（當掉）：mutex 已經歸這個實例
        }
    }

    /// <summary>
    /// 在背景執行緒等喚醒與交棒並回應；canShow() 為 false（正在結束）時不回應、停止等待，讓新實例等這個實例結束後接手。
    /// 交棒：acceptHandover() 回 true（請求合格、已排給 UI 執行緒處理）才設定「已接下」；回 false 時不回應，新版改送喚醒。
    /// 開新視窗：回應後呼叫 openWindow()（沒給就當成喚醒）
    /// </summary>
    public void Listen(Func<bool> canShow, Action show, Func<bool>? acceptHandover = null, Action? openWindow = null)
    {
        var listener = new Thread(() =>
        {
            try
            {
                WaitHandle[] handles = [activate, handover, newWindow];
                while (true)
                {
                    var which = WaitHandle.WaitAny(handles);
                    if (!canShow())
                        break;
                    if (which == 1)
                    {
                        if (acceptHandover?.Invoke() == true)
                            handoverAccepted.Set();
                        continue;
                    }
                    acknowledge.Set();
                    if (which == 2 && openWindow != null)
                        openWindow();
                    else
                        show();
                }
            }
            catch (ObjectDisposedException)
            {
                // 結束時事件已關閉
            }
        })
        { IsBackground = true, Name = "Activate" };
        listener.Start();
    }

    /// <summary>放開 mutex（結束期間被叫醒的新實例接著啟動）並關閉事件；要在呼叫 Acquire 的執行緒呼叫</summary>
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 不在擁有 mutex 的執行緒：只關閉 handle，執行緒結束時 mutex 視為放棄，新實例一樣接得手
        }
        mutex.Dispose();
        activate.Dispose();
        acknowledge.Dispose();
        handover.Dispose();
        handoverAccepted.Dispose();
        newWindow.Dispose();
    }
}
