using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Myriagent.SystemStatus;

/// <summary>
/// 整台電腦的網路收送量（對話框的網路燈，web-protocol 的 activity）：只加總硬體網卡（Wi-Fi、乙太網路）、
/// 不是篩選器、而且已連線的那幾個——.NET 的 NetworkInterface 會把每張網卡的輕量篩選器（WFP、QoS…）各列一次（加總會重複算），
/// GetIPStatistics 每張約 1.5ms（2026-10-23 實測），所以不用。只讀計數器，不改任何設定、不需要管理員。
/// 對話框燈號每 250 ms、電腦狀態面板每秒各讀一次：GetIfTable2 每次要列出所有介面（2026-10-25 實測這台 51 個，一次約 0.9 ms CPU、實際約 6 ms），
/// 所以記住哪幾張是硬體網卡（類型與旗標是介面本身的性質，不會變），平常只以 GetIfEntry2 問那幾張的計數器與連線狀態（7 張約 0.3 ms CPU）；
/// 每 <see cref="RelistMs"/>（找新插上的網卡）、有一張問不到了（拔掉）、或一張已連線的都沒有時，照原本的方式以 GetIfTable2 重列。兩條路加總的條件相同
/// </summary>
internal static class NetworkTotals
{
    // MIB_IF_ROW2 的欄位位移（netioapi.h，x64 與 x86 相同；2026-10-23 以 GetIfTable2 對照 .NET 讀到的乙太網路收送量驗證）
    private const int RowSize = 1352;
    private const int TableHeader = 8;          // MIB_IF_TABLE2：ULONG NumEntries＋對齊到 8
    private const int LuidOffset = 0;           // NET_LUID InterfaceLuid（GetIfEntry2 以它指定要問哪一張）
    private const int TypeOffset = 1128;        // IFTYPE Type
    private const int FlagsOffset = 1152;       // InterfaceAndOperStatusFlags（位元 0 HardwareInterface、位元 1 FilterInterface）
    private const int OperStatusOffset = 1156;  // IF_OPER_STATUS（1＝IfOperStatusUp）
    private const int InOctetsOffset = 1208;
    private const int OutOctetsOffset = 1280;
    private const int SoftwareLoopback = 24;

    /// <summary>多久以 GetIfTable2 重列一次（找新插上的網卡）</summary>
    internal const int RelistMs = 60_000;

    private static readonly object Gate = new();
    private static long[]? hardware;
    private static long listedAtMs;

    /// <summary>硬體網卡收與送的位元組累計；讀不到回 null（對話框燈號與電腦狀態面板在不同的執行緒讀，以鎖保護記住的網卡）</summary>
    public static (ulong Rx, ulong Tx)? Read()
    {
        lock (Gate)
        {
            var now = Environment.TickCount64;
            if (hardware is not null && now - listedAtMs < RelistMs && ReadListed(hardware) is { } listed)
                return listed;
            var (totals, found) = ReadTable();
            hardware = found;
            listedAtMs = now;
            return totals;
        }
    }

    /// <summary>GetIfTable2 列出所有介面：加總已連線的硬體網卡，並回傳所有硬體網卡（不管有沒有連線）的 LUID；讀不到回 (null, null)</summary>
    internal static ((ulong Rx, ulong Tx)? Totals, long[]? Hardware) ReadTable()
    {
        IntPtr table;
        try
        {
            if (GetIfTable2(out table) != 0 || table == IntPtr.Zero)
                return (null, null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return (null, null);
        }
        try
        {
            var count = Marshal.ReadInt32(table);
            ulong rx = 0, tx = 0;
            var found = false;
            var luids = new List<long>();
            var row = new byte[RowSize];
            for (var i = 0; i < count; i++)
            {
                Marshal.Copy(table + TableHeader + i * RowSize, row, 0, RowSize);
                if (!IsHardware(row))
                    continue;
                luids.Add(BinaryPrimitives.ReadInt64LittleEndian(row.AsSpan(LuidOffset)));
                if (!IsUp(row))
                    continue;
                rx += Octets(row, InOctetsOffset);
                tx += Octets(row, OutOctetsOffset);
                found = true;
            }
            return (found ? (rx, tx) : null, luids.ToArray());
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    /// <summary>只問記住的那幾張（GetIfEntry2）；有一張問不到（拔掉了）或一張已連線的都沒有就回 null，呼叫端改以 <see cref="ReadTable"/> 重列</summary>
    internal static (ulong Rx, ulong Tx)? ReadListed(IReadOnlyList<long> luids)
    {
        var row = new byte[RowSize];
        ulong rx = 0, tx = 0;
        var found = false;
        try
        {
            foreach (var luid in luids)
            {
                Array.Clear(row);
                BinaryPrimitives.WriteInt64LittleEndian(row.AsSpan(LuidOffset), luid);
                if (GetIfEntry2(row) != 0 || !IsHardware(row))
                    return null;
                if (!IsUp(row))
                    continue;
                rx += Octets(row, InOctetsOffset);
                tx += Octets(row, OutOctetsOffset);
                found = true;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        return found ? (rx, tx) : null;
    }

    /// <summary>硬體網卡、不是篩選器、不是 loopback</summary>
    private static bool IsHardware(byte[] row)
        => (row[FlagsOffset] & 1) != 0 && (row[FlagsOffset] & 2) == 0 && BinaryPrimitives.ReadInt32LittleEndian(row.AsSpan(TypeOffset)) != SoftwareLoopback;

    private static bool IsUp(byte[] row) => BinaryPrimitives.ReadInt32LittleEndian(row.AsSpan(OperStatusOffset)) == 1;

    private static ulong Octets(byte[] row, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(row.AsSpan(offset));

    [DllImport("iphlpapi.dll")]
    private static extern int GetIfTable2(out IntPtr table);

    [DllImport("iphlpapi.dll")]
    private static extern int GetIfEntry2([In, Out] byte[] row);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(IntPtr memory);
}
