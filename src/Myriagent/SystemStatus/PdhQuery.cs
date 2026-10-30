using System.Runtime.InteropServices;

namespace Myriagent.SystemStatus;

/// <summary>
/// 效能計數器（PDH）的一個查詢（只讀，不需要管理員）：以英文路徑加計數器（中文版 Windows 的計數器名稱是中文，英文路徑不受語系影響），
/// 每次 <see cref="Collect"/> 收一次，再以 <see cref="Read"/> 取各實例的值。速率型的計數器（GPU 使用率）要收過兩次才有值，值是兩次之間的平均；
/// 路徑的實例是 <c>*</c> 時每次收集都重新列舉（程序開開關關，實例跟著變）。不是執行緒安全：呼叫端自己序列化
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    private const uint PdhMoreData = 0x800007D2;
    private const uint FormatDouble = 0x00000200;
    private const uint FormatNoCap100 = 0x00008000;

    private IntPtr query;

    private PdhQuery(IntPtr query) => this.query = query;

    /// <summary>開一個查詢；PDH 不能用時 null</summary>
    public static PdhQuery? Open()
    {
        try
        {
            return PdhOpenQueryW(null, IntPtr.Zero, out var handle) == 0 ? new PdhQuery(handle) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>加一個計數器（英文路徑，例如 <c>\GPU Engine(*)\Utilization Percentage</c>）；這台沒有這個計數器時 null</summary>
    public IntPtr? AddCounter(string englishPath)
    {
        ObjectDisposedException.ThrowIf(query == IntPtr.Zero, this);
        return PdhAddEnglishCounterW(query, englishPath, IntPtr.Zero, out var counter) == 0 ? counter : null;
    }

    /// <summary>收一次；沒有任何實例（例如沒有程序在用 GPU）或失敗回 false</summary>
    public bool Collect()
    {
        ObjectDisposedException.ThrowIf(query == IntPtr.Zero, this);
        return PdhCollectQueryData(query) == 0;
    }

    /// <summary>上一次收集時各實例的值（不夾在 100 以內）；值還不能用的實例（剛出現、速率型只收過一次）略過，整個讀不到回空的</summary>
    public List<(string Instance, double Value)> Read(IntPtr counter)
    {
        ObjectDisposedException.ThrowIf(query == IntPtr.Zero, this);
        var result = new List<(string, double)>();
        // 第一次問要多大；兩次之間實例變多會再回 PDH_MORE_DATA，重問
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint size = 0;
            if (PdhGetFormattedCounterArrayW(counter, FormatDouble | FormatNoCap100, ref size, out _, IntPtr.Zero) != PdhMoreData || size == 0)
                return result;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                var status = PdhGetFormattedCounterArrayW(counter, FormatDouble | FormatNoCap100, ref size, out var count, buffer);
                if (status == PdhMoreData)
                    continue;
                if (status != 0)
                    return result;
                var itemSize = Marshal.SizeOf<PdhFmtCounterValueItem>();
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(buffer + i * itemSize);
                    // PDH_CSTATUS_VALID_DATA（0）、PDH_CSTATUS_NEW_DATA（1）才能用
                    if (item.Value.CStatus > 1 || Marshal.PtrToStringUni(item.Name) is not { } name)
                        continue;
                    result.Add((name, item.Value.DoubleValue));
                }
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return result;
    }

    public void Dispose()
    {
        if (query == IntPtr.Zero)
            return;
        PdhCloseQuery(query);
        query = IntPtr.Zero;
    }

    /// <summary>PDH_FMT_COUNTERVALUE：CStatus 之後是 8 位元組對齊的聯集（這裡只用 double）</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValue
    {
        public uint CStatus;
        public double DoubleValue;
    }

    /// <summary>PDH_FMT_COUNTERVALUE_ITEM_W</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItem
    {
        public IntPtr Name;
        public PdhFmtCounterValue Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
