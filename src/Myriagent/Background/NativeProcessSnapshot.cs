using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Myriagent.Background;

/// <summary>列出目前的程序（測試換成假的）</summary>
internal interface IProcessSnapshotSource
{
    /// <summary>這一刻的程序；帶標記的程序附上命令列與子系統。任何執行緒呼叫，但同一時間只會有一個呼叫</summary>
    IReadOnlyList<ProcessRow> Snapshot();
}

/// <summary>
/// 以 Win32 列程序（背景程式的偵測，<see cref="BackgroundProgramRules"/>）：<c>NtQuerySystemInformation(SystemProcessInformation)</c> 一次拿到所有程序的
/// PID、父 PID、建立時間與名稱（不必開任何程序），只看這個工作階段（同一個登入）的程序。每個程序（PID＋建立時間）第一次出現時讀一次環境變數裡的標記
/// （PEB 的 ProcessParameters.Environment；同一個使用者、非提升權限的程序才開得了，開不了的當成沒有標記），帶標記的再讀命令列與執行檔的子系統。
/// 一律只讀：不改任何程序、不注入、不需要管理員。只支援 64 位元的 app 讀 64 位元的程序（WOW64 的 32 位元程序當成沒有標記）
/// </summary>
internal sealed class NativeProcessSnapshot : IProcessSnapshotSource
{
    private const int SystemProcessInformation = 5;
    private const int ProcessBasicInformation = 0;
    private const int ProcessCommandLineInformation = 60;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;

    /// <summary>環境區塊最多讀多少位元組（一般幾 KB；超過的只看前面）</summary>
    private const int MaxEnvironmentBytes = 256 * 1024;

    /// <summary>剛建立的程序 PEB 可能還沒準備好：這段時間內讀不到就下一次再讀</summary>
    private static readonly long RetryWindowTicks = TimeSpan.FromSeconds(10).Ticks;

    private sealed class Entry
    {
        public string? Marker;
        public bool? IsConsole;
        public string? CommandLine;
        public bool Done;
    }

    private readonly Dictionary<(int Pid, long CreatedAt), Entry> cache = new();
    private readonly Dictionary<string, bool?> subsystemCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly int sessionId = Process.GetCurrentProcess().SessionId;
    private IntPtr buffer = IntPtr.Zero;
    private int bufferSize;

    public IReadOnlyList<ProcessRow> Snapshot()
    {
        if (IntPtr.Size != 8 || !OperatingSystem.IsWindows())
            return [];
        var raw = ReadSystemProcesses();
        var rows = new List<ProcessRow>(raw.Count);
        var seen = new HashSet<(int, long)>();
        var now = DateTime.UtcNow.ToFileTimeUtc();
        foreach (var (pid, parentPid, createdAt, name) in raw)
        {
            var key = (pid, createdAt);
            seen.Add(key);
            if (!cache.TryGetValue(key, out var entry))
                cache[key] = entry = new Entry();
            if (!entry.Done)
                Inspect(pid, entry, now - createdAt > RetryWindowTicks);
            rows.Add(new ProcessRow(pid, parentPid, createdAt, name, entry.Marker, entry.IsConsole, entry.CommandLine));
        }
        foreach (var gone in cache.Keys.Where(k => !seen.Contains(k)).ToList())
            cache.Remove(gone);
        return rows;
    }

    /// <summary>讀標記；有標記再讀命令列與子系統。讀不到而且程序還很新時不記成完成（下一次再讀）</summary>
    private void Inspect(int pid, Entry entry, bool settled)
    {
        var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, pid);
        if (handle == IntPtr.Zero)
        {
            // 別的使用者、提升權限、受保護的程序：不是這個 app 的代理開的
            entry.Done = true;
            return;
        }
        try
        {
            if (IsWow64Process(handle, out var wow64) && wow64)
            {
                entry.Done = true;
                return;
            }
            var environment = ReadEnvironment(handle, out var ready);
            if (environment is null)
            {
                entry.Done = settled || ready;
                return;
            }
            entry.Marker = BackgroundProgramRules.MarkerOf(environment);
            if (entry.Marker is not null)
            {
                entry.CommandLine = ReadCommandLine(handle);
                entry.IsConsole = ReadIsConsole(handle);
            }
            entry.Done = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NativeProcessSnapshot] 讀程序 {pid} 失敗：{ex.Message}");
            entry.Done = true;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>(PID, 父 PID, 建立時間, 名稱)：這個工作階段的程序（不含 Idle 與 System；app 自己也列，CLI 程序的父程序要在）</summary>
    private List<(int Pid, int ParentPid, long CreatedAt, string Name)> ReadSystemProcesses()
    {
        var result = new List<(int, int, long, string)>();
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (buffer == IntPtr.Zero)
            {
                bufferSize = Math.Max(bufferSize, 512 * 1024);
                buffer = Marshal.AllocHGlobal(bufferSize);
            }
            var status = NtQuerySystemInformation(SystemProcessInformation, buffer, bufferSize, out var needed);
            if (status == StatusInfoLengthMismatch)
            {
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
                bufferSize = Math.Max(bufferSize * 2, needed + 64 * 1024);
                continue;
            }
            if (status != 0)
                return result;

            var offset = 0L;
            while (true)
            {
                var entry = buffer + (nint)offset;
                var next = Marshal.ReadInt32(entry, 0x00);
                var pid = (int)Marshal.ReadInt64(entry, 0x50);
                var session = Marshal.ReadInt32(entry, 0x64);
                if (pid > 4 && session == sessionId)
                {
                    var createdAt = Marshal.ReadInt64(entry, 0x20);
                    var parentPid = (int)Marshal.ReadInt64(entry, 0x58);
                    var nameLength = Marshal.ReadInt16(entry, 0x38);
                    var namePtr = Marshal.ReadIntPtr(entry, 0x40);
                    var name = namePtr == IntPtr.Zero || nameLength <= 0 ? string.Empty : Marshal.PtrToStringUni(namePtr, nameLength / 2);
                    result.Add((pid, parentPid, createdAt, name));
                }
                if (next == 0)
                    break;
                offset += next;
            }
            return result;
        }
        return result;
    }

    /// <summary>
    /// 環境區塊（UTF-16 解碼，到兩個 \0 為止）：PEB → ProcessParameters → Environment，長度以那塊記憶體剩下的大小為上限。
    /// <paramref name="ready"/>＝PEB 已經準備好（讀不到不是因為程序還在啟動）
    /// </summary>
    private static string? ReadEnvironment(IntPtr handle, out bool ready)
    {
        ready = false;
        var pbi = new byte[48];
        var pinned = GCHandle.Alloc(pbi, GCHandleType.Pinned);
        try
        {
            if (NtQueryInformationProcess(handle, ProcessBasicInformation, pinned.AddrOfPinnedObject(), pbi.Length, out _) != 0)
                return null;
        }
        finally
        {
            pinned.Free();
        }
        var peb = (IntPtr)BitConverter.ToInt64(pbi, 8);
        if (peb == IntPtr.Zero || ReadPointer(handle, peb + 0x20) is not { } parameters || parameters == IntPtr.Zero)
            return null;
        if (ReadPointer(handle, parameters + 0x80) is not { } environment || environment == IntPtr.Zero)
            return null;
        ready = true;
        if (VirtualQueryEx(handle, environment, out var mbi, (IntPtr)Marshal.SizeOf<MemoryBasicInformation>()) == IntPtr.Zero)
            return null;
        var available = (long)mbi.BaseAddress + (long)mbi.RegionSize - (long)environment;
        var length = (int)Math.Clamp(available, 0, MaxEnvironmentBytes) & ~1;
        if (length == 0)
            return null;
        var bytes = new byte[length];
        if (!ReadProcessMemory(handle, environment, bytes, (IntPtr)length, out var read) || (long)read < 2)
            return null;
        var text = Encoding.Unicode.GetString(bytes, 0, (int)(long)read & ~1);
        var end = text.IndexOf("\0\0", StringComparison.Ordinal);
        return end < 0 ? text : text[..(end + 1)];
    }

    private static IntPtr? ReadPointer(IntPtr handle, IntPtr address)
    {
        var bytes = new byte[8];
        return ReadProcessMemory(handle, address, bytes, (IntPtr)8, out var read) && (long)read == 8 ? (IntPtr)BitConverter.ToInt64(bytes, 0) : null;
    }

    /// <summary>命令列（ProcessCommandLineInformation，Windows 8.1 起）；讀不到回 null</summary>
    private static string? ReadCommandLine(IntPtr handle)
    {
        var size = 4096;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var info = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, info, size, out var needed);
                if (status == StatusInfoLengthMismatch || (status != 0 && needed > size))
                {
                    size = Math.Max(size * 2, needed + 64);
                    continue;
                }
                if (status != 0)
                    return null;
                var length = Marshal.ReadInt16(info, 0) & 0xFFFF;
                var text = Marshal.ReadIntPtr(info, 8);
                return text == IntPtr.Zero || length == 0 ? null : Marshal.PtrToStringUni(text, length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(info);
            }
        }
        return null;
    }

    /// <summary>執行檔的子系統是不是主控台（PE 選用表頭的 Subsystem：3＝主控台、2＝視窗）；同一個路徑只讀一次，讀不到回 null</summary>
    private bool? ReadIsConsole(IntPtr handle)
    {
        var path = new StringBuilder(1024);
        var length = path.Capacity;
        if (!QueryFullProcessImageNameW(handle, 0, path, ref length))
            return null;
        var file = path.ToString(0, length);
        if (subsystemCache.TryGetValue(file, out var cached))
            return cached;
        bool? result = null;
        try
        {
            // 一定要允許刪除與改名：CLI 自己更新時會換掉執行檔（讀 claude.exe 沒帶 FILE_SHARE_DELETE 會讓它的更新失敗）
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var header = new byte[4096];
            var read = stream.Read(header, 0, header.Length);
            if (read >= 0x40)
            {
                var pe = BitConverter.ToInt32(header, 0x3C);
                if (pe > 0 && pe + 24 + 70 <= read && header[pe] == 'P' && header[pe + 1] == 'E' && header[pe + 2] == 0 && header[pe + 3] == 0)
                    result = BitConverter.ToUInt16(header, pe + 24 + 68) == 3;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        subsystemCache[file] = result;
        return result;
    }

    ~NativeProcessSnapshot()
    {
        if (buffer != IntPtr.Zero)
            Marshal.FreeHGlobal(buffer);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr process, out bool wow64);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr baseAddress, byte[] buffer, IntPtr size, out IntPtr read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualQueryEx(IntPtr process, IntPtr address, out MemoryBasicInformation info, IntPtr length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder name, ref int size);
}
