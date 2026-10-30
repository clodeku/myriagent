using System.Diagnostics;
using System.Runtime.InteropServices;
using Myriagent.Core;
using Myriagent.Sync;

namespace Myriagent.SystemStatus;

/// <summary>
/// 這台的顯示卡（stat1 的 <c>gpus</c>）：使用率與專用記憶體的已用讀效能計數器「GPU Engine」「GPU Adapter Memory」（工作管理員用的同一組，
/// 不需要管理員），彙總規則在 <see cref="DeviceSensorRules"/>；名稱與專用記憶體總量以 DXGI 列舉（依 LUID 對上計數器；軟體轉譯器與
/// Microsoft 的基本顯示卡不列）。第一次讀的時候才開查詢（app 啟動時不多做事），那一次只收基準、回 null；之後每次是上一次到這一次的平均。
/// 不是執行緒安全：呼叫端（<see cref="SystemMonitor"/> 的取樣鎖）序列化
/// </summary>
internal sealed class GpuReader : IDisposable
{
    /// <summary>多久重新列舉一次顯示卡（外接顯示卡、驅動程式重裝）</summary>
    private static readonly TimeSpan AdapterRefresh = TimeSpan.FromMinutes(5);

    /// <summary>計數器出現沒列舉過的 LUID 時，最快多久重新列舉一次</summary>
    private static readonly TimeSpan UnknownRefresh = TimeSpan.FromMinutes(1);

    private const uint DxgiAdapterFlagSoftware = 2;

    /// <summary>Microsoft 的基本顯示卡、基本轉譯器、遠端桌面的顯示卡</summary>
    private const uint MicrosoftVendorId = 0x1414;

    private PdhQuery? query;
    private IntPtr? engineCounter;
    private IntPtr? memoryCounter;
    private bool opened;
    private List<Adapter> adapters = [];
    private HashSet<long> knownLuids = [];
    private long adaptersAt;
    private bool warnedDxgi;

    private sealed record Adapter(long Luid, string Name, long DedicatedTotal);

    /// <summary>各張顯示卡；這台沒有 GPU 計數器（太舊的 Windows、某些虛擬機）、第一次讀（只收基準）回 null</summary>
    public IReadOnlyList<GpuStats>? Read()
    {
        if (!opened)
        {
            opened = true;
            query = PdhQuery.Open();
            engineCounter = query?.AddCounter(@"\GPU Engine(*)\Utilization Percentage");
            memoryCounter = query?.AddCounter(@"\GPU Adapter Memory(*)\Dedicated Usage");
            if (engineCounter is null)
            {
                Log.Info("這台沒有 GPU 的效能計數器，電腦狀態不列顯示卡");
                return null;
            }
            query!.Collect();
            RefreshAdapters();
            return null;
        }
        if (query is null || engineCounter is not { } engine)
            return null;

        // 沒有任何程序在用 GPU 時收集回失敗（沒有實例）：照樣當成全部 0
        var collected = query.Collect();
        var util = collected ? DeviceSensorRules.GpuUtilization(query.Read(engine)) : [];
        var used = collected && memoryCounter is { } memory ? DeviceSensorRules.GpuDedicatedUsage(query.Read(memory)) : [];

        var age = Stopwatch.GetElapsedTime(adaptersAt);
        if (age >= AdapterRefresh || (age >= UnknownRefresh && util.Keys.Concat(used.Keys).Any(luid => !knownLuids.Contains(luid))))
            RefreshAdapters();

        if (adapters.Count == 0)
        {
            // DXGI 列舉不到（不該發生）：照計數器的 LUID 排序，名稱是「GPU n」
            return util.Keys.Order().Select((luid, i) => DeviceSensorRules.Gpu(null, i, util[luid], null, 0)).ToList() is { Count: > 0 } fallback ? fallback : null;
        }
        return adapters.Select((a, i) => DeviceSensorRules.Gpu(a.Name, i, util.GetValueOrDefault(a.Luid),
            used.TryGetValue(a.Luid, out var bytes) ? bytes : null, a.DedicatedTotal)).ToList();
    }

    public void Dispose()
    {
        query?.Dispose();
        query = null;
    }

    private void RefreshAdapters()
    {
        adaptersAt = Stopwatch.GetTimestamp();
        var listed = new List<Adapter>();
        var known = new HashSet<long>();
        object? factoryObject = null;
        try
        {
            if (CreateDXGIFactory1(typeof(IDXGIFactory1).GUID, out factoryObject) < 0 || factoryObject is not IDXGIFactory1 factory)
                return;
            for (uint i = 0; i < 64; i++)
            {
                // 列舉完回 DXGI_ERROR_NOT_FOUND（負的）
                if (factory.EnumAdapters1(i, out var adapter) < 0 || adapter is null)
                    break;
                try
                {
                    if (adapter.GetDesc1(out var desc) < 0)
                        continue;
                    var luid = DeviceSensorRules.LuidKey(desc.LuidHigh, desc.LuidLow);
                    known.Add(luid);
                    if ((desc.Flags & DxgiAdapterFlagSoftware) != 0 || desc.VendorId == MicrosoftVendorId || listed.Any(a => a.Luid == luid))
                        continue;
                    listed.Add(new Adapter(luid, desc.Description ?? "", (long)Math.Min((ulong)desc.DedicatedVideoMemory, (ulong)DeviceStatsRules.MaxBytes)));
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException or InvalidCastException)
        {
            if (!warnedDxgi)
                Log.Warn($"列舉顯示卡失敗：{ex.Message}");
            warnedDxgi = true;
            return;
        }
        finally
        {
            if (factoryObject is not null && Marshal.IsComObject(factoryObject))
                Marshal.ReleaseComObject(factoryObject);
        }
        adapters = listed;
        knownLuids = known;
    }

    // ───── DXGI（只用列舉；vtable 的順序照 dxgi.h，用不到的方法只佔位、不呼叫） ─────

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? factory);

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        // IDXGIObject
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        // IDXGIFactory
        void EnumAdapters();
        void MakeWindowAssociation();
        void GetWindowAssociation();
        void CreateSwapChain();
        void CreateSoftwareAdapter();
        // IDXGIFactory1
        [PreserveSig]
        int EnumAdapters1(uint index, out IDXGIAdapter1? adapter);
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        // IDXGIObject
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        // IDXGIAdapter
        void EnumOutputs();
        void GetDesc();
        void CheckInterfaceSupport();
        // IDXGIAdapter1
        [PreserveSig]
        int GetDesc1(out DxgiAdapterDesc1 desc);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }
}
