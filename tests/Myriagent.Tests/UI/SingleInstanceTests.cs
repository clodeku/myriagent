using System.Diagnostics;
using Myriagent.UI;

namespace Myriagent.Tests.UI;

/// <summary>
/// 單一實例的交接：第二個實例叫醒第一個實例後結束；第一個實例正在結束（不回應）時，第二個實例等它放開 mutex 後接手。
/// mutex 屬於取得它的執行緒，所以每個「實例」在自己的執行緒上取得與放開；名稱用亂數後綴，不會碰到使用者正在執行的 app。
/// </summary>
public class SingleInstanceTests
{
    private static string NewSuffix() => ".test-" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>在專用執行緒上取得實例並保持到 Release</summary>
    private sealed class InstanceThread : IDisposable
    {
        private readonly Thread thread;
        private readonly ManualResetEventSlim release = new();
        private readonly TaskCompletionSource<SingleInstance?> acquired = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <param name="abandon">結束執行緒時不放開 mutex（模擬當掉）</param>
        public InstanceThread(Func<SingleInstance?> acquire, bool abandon = false)
        {
            thread = new Thread(() =>
            {
                SingleInstance? instance;
                try
                {
                    instance = acquire();
                }
                catch (Exception ex)
                {
                    acquired.SetException(ex);
                    return;
                }
                acquired.SetResult(instance);
                release.Wait();
                if (!abandon)
                    instance?.Dispose();
            })
            { IsBackground = true, Name = "測試實例" };
            thread.Start();
        }

        public Task<SingleInstance?> Acquired => acquired.Task;

        public SingleInstance? Instance
            => acquired.Task.Wait(TimeSpan.FromSeconds(30)) ? acquired.Task.Result : throw new TimeoutException("30 秒內沒有取得結果");

        public void Release()
        {
            release.Set();
            thread.Join(TimeSpan.FromSeconds(10)).Should().BeTrue("實例執行緒要能結束");
        }

        public void Dispose()
        {
            if (!release.IsSet)
                Release();
        }
    }

    [Fact]
    public void 探測有沒有實例_只看不搶_實例結束後就沒有()
    {
        var suffix = NewSuffix();
        SingleInstance.IsRunning(suffix).Should().BeFalse("還沒有實例");

        using (var first = new InstanceThread(() => SingleInstance.Acquire(suffix)))
        {
            first.Instance.Should().NotBeNull();
            SingleInstance.IsRunning(suffix).Should().BeTrue("換版幫手切換 current 前看得到使用者自己開的實例");
            SingleInstance.IsRunning(NewSuffix()).Should().BeFalse("別的資料夾的實例不算");

            using var second = new InstanceThread(() => SingleInstance.Acquire(suffix, acknowledgeTimeout: TimeSpan.FromMilliseconds(200),
                takeoverTimeout: TimeSpan.FromMilliseconds(200)));
            second.Instance.Should().BeNull("探測不持有 mutex：第一個實例仍是唯一的實例（它沒有在聽，第二個實例等不到它結束就放棄）");
            first.Release();
        }

        SingleInstance.IsRunning(suffix).Should().BeFalse("實例結束後 mutex 就不在了：探測本身沒有留下 handle");
    }

    [Fact]
    public void 已經有實例在執行_第二個實例交出前景權限並叫醒它後結束()
    {
        var suffix = NewSuffix();
        using var first = new InstanceThread(() => SingleInstance.Acquire(suffix));
        first.Instance.Should().NotBeNull("第一個實例");
        int shown = 0;
        first.Instance!.Listen(() => true, () => Interlocked.Increment(ref shown));

        bool allowed = false;
        var sw = Stopwatch.StartNew();
        using var second = new InstanceThread(() => SingleInstance.Acquire(suffix, () => allowed = true, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)));

        second.Instance.Should().BeNull("第一個實例收到喚醒並回應，第二個實例直接結束");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "有回應就不必等逾時");
        allowed.Should().BeTrue("叫醒前先把前景權限交出去");
        SpinWait.SpinUntil(() => Volatile.Read(ref shown) == 1, TimeSpan.FromSeconds(5)).Should().BeTrue("第一個實例把視窗帶到前面");
    }

    [Fact]
    public void 再開一次app_第一個實例懂開新視窗_就開新視窗而不是叫醒()
    {
        var suffix = NewSuffix();
        using var first = new InstanceThread(() => SingleInstance.Acquire(suffix));
        int shown = 0, opened = 0;
        first.Instance!.Listen(() => true, () => Interlocked.Increment(ref shown), openWindow: () => Interlocked.Increment(ref opened));

        using var second = new InstanceThread(() => SingleInstance.Acquire(suffix, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), requestNewWindow: true));

        second.Instance.Should().BeNull("第一個實例收到並回應");
        SpinWait.SpinUntil(() => Volatile.Read(ref opened) == 1, TimeSpan.FromSeconds(5)).Should().BeTrue("像 Chrome 一樣開新視窗");
        Volatile.Read(ref shown).Should().Be(0);

        using var third = new InstanceThread(() => SingleInstance.Acquire(suffix, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)));
        third.Instance.Should().BeNull();
        SpinWait.SpinUntil(() => Volatile.Read(ref shown) == 1, TimeSpan.FromSeconds(5)).Should().BeTrue("不要求開新視窗的（開機自動啟動、換版）照舊叫醒");
        Volatile.Read(ref opened).Should().Be(1);
    }

    [Fact]
    public void 再開一次app_第一個實例是不懂開新視窗的舊版_照原本叫醒()
    {
        var suffix = NewSuffix();
        // 舊版的實例：只有 mutex 與喚醒、回應事件（沒有建立「開新視窗」事件）
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Myriagent.Activate" + suffix);
        using var acknowledge = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Myriagent.Activated" + suffix);
        Mutex? held = null;   // 留著參考：handle 被回收時 mutex 就不在了
        using var oldInstance = new InstanceThread(() =>
        {
            held = new Mutex(true, @"Local\Myriagent.Instance" + suffix, out _);
            return null;
        });
        oldInstance.Instance.Should().BeNull("只是佔住 mutex 的舊版");
        var woken = new ManualResetEventSlim();
        var listener = new Thread(() =>
        {
            if (activate.WaitOne(TimeSpan.FromSeconds(10)))
            {
                acknowledge.Set();
                woken.Set();
            }
        }) { IsBackground = true };
        listener.Start();

        using var second = new InstanceThread(() => SingleInstance.Acquire(suffix, null, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(500), requestNewWindow: true));

        second.Instance.Should().BeNull("舊版收到喚醒並回應");
        woken.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("對方不懂開新視窗時送的是原本的喚醒");
        GC.KeepAlive(held);
    }

    [Fact]
    public void 第一個實例正在結束_不回應_新實例等它放開mutex後接手()
    {
        var suffix = NewSuffix();
        using var first = new InstanceThread(() => SingleInstance.Acquire(suffix));
        var asked = new ManualResetEventSlim();
        first.Instance!.Listen(() =>
        {
            asked.Set();
            return false;   // 正在結束
        }, () => throw new InvalidOperationException("正在結束時不能再顯示視窗"));

        using var second = new InstanceThread(() => SingleInstance.Acquire(suffix, null, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(15)));
        asked.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("第一個實例收到喚醒");
        second.Acquired.IsCompleted.Should().BeFalse("第一個實例還沒結束，新實例要等（修正前直接結束，這次啟動被吞掉）");

        first.Release();   // 收尾完成、放開 mutex

        var takenOver = second.Instance;
        takenOver.Should().NotBeNull("由新實例接手啟動");
        int shown = 0;
        takenOver!.Listen(() => true, () => Interlocked.Increment(ref shown));
        using var third = new InstanceThread(() => SingleInstance.Acquire(suffix, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)));
        third.Instance.Should().BeNull("接手後的實例照樣能被叫醒");
        SpinWait.SpinUntil(() => Volatile.Read(ref shown) == 1, TimeSpan.FromSeconds(5)).Should().BeTrue();
    }

    [Fact]
    public void 第一個實例沒放開mutex就結束_當掉_新實例照樣接手_接手前自己送的喚醒不算()
    {
        var suffix = NewSuffix();
        var first = new InstanceThread(() => SingleInstance.Acquire(suffix), abandon: true);
        var crashed = first.Instance;
        crashed.Should().NotBeNull();
        first.Release();   // 執行緒結束但沒放開 mutex（沒有監聽，也不會回應）

        using var second = new InstanceThread(() => SingleInstance.Acquire(suffix, null, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(10)));
        var takenOver = second.Instance;
        takenOver.Should().NotBeNull("被放棄的 mutex 改歸新實例");

        int shown = 0;
        takenOver!.Listen(() => true, () => Interlocked.Increment(ref shown));
        Thread.Sleep(300);
        Volatile.Read(ref shown).Should().Be(0, "接手前自己送出的喚醒已清掉，不會一啟動就把視窗叫到前面");
        crashed!.Dispose();   // 只關掉 handle（不在擁有的執行緒上，也不再擁有）
    }

    [Fact]
    public void 第一個實例沒回應也沒結束_等到逾時就放棄()
    {
        var suffix = NewSuffix();
        using var first = new InstanceThread(() => SingleInstance.Acquire(suffix));   // 沒有監聽（卡住的實例）
        first.Instance.Should().NotBeNull();

        var sw = Stopwatch.StartNew();
        using var second = new InstanceThread(() => SingleInstance.Acquire(suffix, null, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(300)));
        second.Instance.Should().BeNull();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void 較新的版本請求交棒_第一個實例接下_新版直接結束_不叫醒()
    {
        var suffix = NewSuffix();
        using var first = new InstanceThread(() => SingleInstance.Acquire(suffix));
        int shown = 0, accepted = 0;
        first.Instance!.Listen(() => true, () => Interlocked.Increment(ref shown), () =>
        {
            Interlocked.Increment(ref accepted);
            return true;
        });

        var outcome = AcquireOutcome.Acquired;
        bool prepared = false;
        var sw = Stopwatch.StartNew();
        using var second = new InstanceThread(() => SingleInstance.Acquire(suffix, out outcome, prepareHandover: () => prepared = true,
            acknowledgeTimeout: TimeSpan.FromSeconds(10), takeoverTimeout: TimeSpan.FromSeconds(10), handoverTimeout: TimeSpan.FromSeconds(10)));

        second.Instance.Should().BeNull();
        outcome.Should().Be(AcquireOutcome.HandedOver);
        prepared.Should().BeTrue("先寫好請求才送交棒訊號");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "接下就不必等逾時");
        Volatile.Read(ref accepted).Should().Be(1);
        Thread.Sleep(200);
        Volatile.Read(ref shown).Should().Be(0, "交棒不是喚醒：舊版自己決定要不要把視窗叫出來");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 交棒沒人接下_請求不合格或對方不懂交棒_改用一般的喚醒(bool listensForHandover)
    {
        var suffix = NewSuffix();
        using var first = new InstanceThread(() => SingleInstance.Acquire(suffix));
        int shown = 0;
        // 不懂交棒的舊版：沒有 acceptHandover（訊號照樣收到，但不回應）；懂的版本拒絕不合格的請求
        first.Instance!.Listen(() => true, () => Interlocked.Increment(ref shown), listensForHandover ? () => false : null);

        var outcome = AcquireOutcome.Acquired;
        using var second = new InstanceThread(() => SingleInstance.Acquire(suffix, out outcome, prepareHandover: () => true,
            acknowledgeTimeout: TimeSpan.FromSeconds(10), takeoverTimeout: TimeSpan.FromSeconds(10), handoverTimeout: TimeSpan.FromMilliseconds(300)));

        second.Instance.Should().BeNull();
        outcome.Should().Be(AcquireOutcome.ActivatedExisting);
        SpinWait.SpinUntil(() => Volatile.Read(ref shown) == 1, TimeSpan.FromSeconds(5)).Should().BeTrue("沒接下交棒就照原本叫醒它");
    }

    [Fact]
    public void 沒寫請求就不送交棒訊號_直接叫醒()
    {
        var suffix = NewSuffix();
        using var first = new InstanceThread(() => SingleInstance.Acquire(suffix));
        int shown = 0, asked = 0;
        first.Instance!.Listen(() => true, () => Interlocked.Increment(ref shown), () =>
        {
            Interlocked.Increment(ref asked);
            return true;
        });

        var outcome = AcquireOutcome.Acquired;
        using var second = new InstanceThread(() => SingleInstance.Acquire(suffix, out outcome, prepareHandover: () => false,
            acknowledgeTimeout: TimeSpan.FromSeconds(10), takeoverTimeout: TimeSpan.FromSeconds(10)));

        second.Instance.Should().BeNull();
        outcome.Should().Be(AcquireOutcome.ActivatedExisting);
        SpinWait.SpinUntil(() => Volatile.Read(ref shown) == 1, TimeSpan.FromSeconds(5)).Should().BeTrue();
        Volatile.Read(ref asked).Should().Be(0, "同版或對方較新：不請它交棒");
    }

    [Fact]
    public void 正在結束的實例不接交棒_新版等它放開mutex後接手()
    {
        var suffix = NewSuffix();
        using var first = new InstanceThread(() => SingleInstance.Acquire(suffix));
        first.Instance!.Listen(() => false, () => throw new InvalidOperationException("正在結束"), () => throw new InvalidOperationException("正在結束時不能接交棒"));

        var outcome = AcquireOutcome.ActivatedExisting;
        using var second = new InstanceThread(() => SingleInstance.Acquire(suffix, out outcome, prepareHandover: () => true,
            acknowledgeTimeout: TimeSpan.FromMilliseconds(300), takeoverTimeout: TimeSpan.FromSeconds(15), handoverTimeout: TimeSpan.FromMilliseconds(300)));
        Thread.Sleep(800);
        second.Acquired.IsCompleted.Should().BeFalse("還在等第一個實例結束");

        first.Release();

        second.Instance.Should().NotBeNull();
        outcome.Should().Be(AcquireOutcome.Acquired);
    }

    [Fact]
    public void 不同資料夾的實例互不影響()
    {
        using var a = new InstanceThread(() => SingleInstance.Acquire(NewSuffix()));
        using var b = new InstanceThread(() => SingleInstance.Acquire(NewSuffix(), null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)));
        a.Instance.Should().NotBeNull();
        b.Instance.Should().NotBeNull();
    }
}
