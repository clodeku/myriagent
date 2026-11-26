// 代理主機：替 app 開 CLI 程序、轉送 stdin／stdout／stderr（設計 docs/plans/2026-11-26-agent-host.md）。
// 不懂任何 CLI 的協定，只搬位元組。.NET Framework 4.8：Windows 內建，不受 .NET 更新刪掉修補版的影響。
// H1（協定第 2 版）：app 先說要交棒（Handoff）再斷線時，程序留著、輸出暫存在這裡，等新的 app 連上來接回（Attach）；
// 沒說就斷線（app 當掉）照舊結束全部程序並結束自己。
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace Myriagent.Host
{
    internal static class Program
    {
        /// <summary>啟動後多久沒有 app 連上就結束（app 啟動主機之後馬上連線；連不上多半是 app 已經不在了）</summary>
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(60);

        /// <summary>交棒最多保留多久（app 給的秒數超過就用這個）：新版 app 一直沒起來時不能讓 CLI 無人看管地一直跑</summary>
        private const int MaxKeepSeconds = 30 * 60;

        private static readonly object WriteGate = new object();
        private static readonly object ProcessGate = new object();
        private static readonly Dictionary<int, HostedProcess> Processes = new Dictionary<int, HostedProcess>();
        private static volatile NamedPipeServerStream? client;
        private static IntPtr job = IntPtr.Zero;
        private static string? logPath;
        private static int nextHandle;

        /// <summary>目前連著的 app 說的協定版本（第 2 版起才替它留著還沒確認的輸出）</summary>
        private static volatile int clientVersion = 1;

        /// <summary>app 說要交棒：斷線後保留程序到這個時間（UTC）；null＝斷線就結束全部</summary>
        private static DateTime? keepUntilUtc;

        internal static bool ClientKeeps
        {
            get { return clientVersion >= HostProtocol.KeepVersion; }
        }

        private static int Main(string[] args)
        {
            string? pipeName = null, jobName = null;
            for (int i = 0; i + 1 < args.Length; i += 2)
            {
                switch (args[i])
                {
                    case "--pipe": pipeName = args[i + 1]; break;
                    case "--job": jobName = args[i + 1]; break;
                    case "--log": logPath = args[i + 1]; break;
                }
            }
            if (string.IsNullOrEmpty(pipeName) || string.IsNullOrEmpty(jobName))
                return 2;

            int exitCode = 0;
            try
            {
                job = CreateJob(jobName!);
                var deadline = DateTime.UtcNow + ConnectTimeout;
                while (true)
                {
                    using (var server = CreatePipe(pipeName!))
                    {
                        var wait = deadline - DateTime.UtcNow;
                        var connecting = server.BeginWaitForConnection(null, null);
                        if (wait <= TimeSpan.Zero || !connecting.AsyncWaitHandle.WaitOne(wait))
                        {
                            Log(Processes.Count == 0 ? "沒有 app 連上，結束" : "交棒後沒有 app 接手，結束還在的 " + Processes.Count + " 個程序");
                            exitCode = 3;
                            break;
                        }
                        server.EndWaitForConnection(connecting);
                        keepUntilUtc = null;
                        clientVersion = 1;
                        client = server;
                        bool shutdown;
                        try
                        {
                            shutdown = Serve(server);
                        }
                        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
                        {
                            shutdown = false;   // 連線中斷
                        }
                        client = null;
                        DetachAll();
                        if (shutdown)
                            break;
                        // app 沒說要交棒就斷線（當掉、被結束）：照舊結束全部程序
                        if (keepUntilUtc == null || keepUntilUtc.Value <= DateTime.UtcNow)
                        {
                            if (keepUntilUtc == null && Processes.Count > 0)
                                Log("app 的連線中斷（沒有交棒），結束還在的 " + Processes.Count + " 個程序");
                            break;
                        }
                        deadline = keepUntilUtc.Value;
                        Log("app 交棒：保留 " + Processes.Count + " 個程序，等新的 app 連上（最多到 " + deadline.ToLocalTime().ToString("HH:mm:ss") + "）");
                    }
                }
            }
            catch (Exception ex)
            {
                Log("主機失敗：" + ex);
                exitCode = 1;
            }
            finally
            {
                // 結束所有還在的 CLI（同 app 自己的工作物件在 app 結束時做的事）
                if (job != IntPtr.Zero)
                    TerminateJobObject(job, 1);
            }
            return exitCode;
        }

        /// <summary>只有同一個 Windows 使用者連得上（預設的存取控制會讓 Everyone 可讀）</summary>
        private static NamedPipeServerStream CreatePipe(string name)
        {
            var security = new PipeSecurity();
            var user = WindowsIdentity.GetCurrent().User!;
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
            return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1 << 16, 1 << 16, security);
        }

        /// <summary>處理一條連線上的指令直到它結束；回 true＝app 要主機結束（Shutdown）</summary>
        private static bool Serve(Stream stream)
        {
            byte type;
            byte[] payload;
            while (HostProtocol.ReadFrame(stream, out type, out payload))
            {
                switch (type)
                {
                    case HostProtocol.Hello:
                        clientVersion = payload.Length >= 4 ? HostProtocol.DecodeHello(payload) : 1;
                        Send(HostProtocol.HelloOk, HostProtocol.EncodeHelloOk(HostProtocol.Version, Process.GetCurrentProcess().Id));
                        break;
                    case HostProtocol.Spawn:
                    {
                        // 回覆先送出去，才開始轉送輸出：app 要先知道代號才認得 Output
                        HostedProcess? hosted;
                        var result = Spawn(HostProtocol.DecodeSpawn(payload), out hosted);
                        Send(HostProtocol.SpawnResult, HostProtocol.EncodeSpawnResult(result));
                        if (hosted != null)
                            hosted.StartPumps();
                        break;
                    }
                    case HostProtocol.Write:
                    {
                        int handle, sequence;
                        byte[] data;
                        HostProtocol.DecodeWrite(payload, out handle, out sequence, out data);
                        var target = Find(handle);
                        if (target != null)
                            target.Write(sequence, data);
                        else
                            Send(HostProtocol.WriteResult, HostProtocol.EncodeWriteResult(handle, sequence, false));   // 程序已經結束
                        break;
                    }
                    case HostProtocol.CloseInput:
                    {
                        var target = Find(HostProtocol.DecodeHandle(payload));
                        if (target != null)
                            target.CloseInput();
                        break;
                    }
                    case HostProtocol.Ack:
                    {
                        int handle;
                        long outOffset, errOffset;
                        HostProtocol.DecodeOffsets(payload, out handle, out outOffset, out errOffset);
                        var target = Find(handle);
                        if (target != null)
                            target.Ack(outOffset, errOffset);
                        break;
                    }
                    case HostProtocol.Detach:
                    {
                        int handle = HostProtocol.DecodeHandle(payload);
                        var target = Find(handle);
                        long outEnd = -1, errEnd = -1;   // -1＝沒有這個程序（已經結束、回報過了）
                        if (target != null)
                            target.Detach(out outEnd, out errEnd);
                        Send(HostProtocol.Detached, HostProtocol.EncodeOffsets(handle, outEnd, errEnd));
                        break;
                    }
                    case HostProtocol.Handoff:
                    {
                        int seconds = Math.Max(0, Math.Min(HostProtocol.DecodeHandoff(payload), MaxKeepSeconds));
                        keepUntilUtc = seconds == 0 ? (DateTime?)null : DateTime.UtcNow.AddSeconds(seconds);
                        break;
                    }
                    case HostProtocol.List:
                    {
                        int requestId = HostProtocol.DecodeHandle(payload);
                        var list = new List<HostProcessInfo>();
                        lock (ProcessGate)
                        {
                            foreach (var hosted in Processes.Values)
                                list.Add(hosted.Describe());
                        }
                        Send(HostProtocol.ListResult, HostProtocol.EncodeListResult(requestId, list));
                        break;
                    }
                    case HostProtocol.Attach:
                    {
                        int requestId, handle;
                        long outOffset, errOffset;
                        HostProtocol.DecodeAttach(payload, out requestId, out handle, out outOffset, out errOffset);
                        var target = Find(handle);
                        if (target == null)
                            Send(HostProtocol.AttachResult, HostProtocol.EncodeAttachResult(requestId, false, "沒有這個程序"));
                        else
                            target.Attach(requestId, outOffset, errOffset);
                        break;
                    }
                    case HostProtocol.Shutdown:
                        return true;
                    // 不認得的型別略過：新版 app 對舊主機多送的指令不能讓連線斷掉
                }
            }
            return false;
        }

        private static HostedProcess? Find(int handle)
        {
            lock (ProcessGate)
            {
                HostedProcess found;
                return Processes.TryGetValue(handle, out found) ? found : null;
            }
        }

        /// <summary>app 的連線沒了：每個程序都改成只暫存、不轉送</summary>
        private static void DetachAll()
        {
            List<HostedProcess> all;
            lock (ProcessGate)
                all = new List<HostedProcess>(Processes.Values);
            foreach (var hosted in all)
            {
                long ignoredOut, ignoredErr;
                hosted.Detach(out ignoredOut, out ignoredErr);
            }
        }

        /// <summary>送一個框；沒有 app 連著（或管道斷了）回 false</summary>
        internal static bool Send(byte type, byte[] payload)
        {
            var pipe = client;
            if (pipe == null)
                return false;
            var frame = HostProtocol.Frame(type, payload);
            try
            {
                lock (WriteGate)
                {
                    pipe.Write(frame, 0, frame.Length);
                    pipe.Flush();
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                return false;
            }
        }

        private static HostSpawnResult Spawn(HostSpawnRequest request, out HostedProcess? started)
        {
            started = null;
            var result = new HostSpawnResult { RequestId = request.RequestId };
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = request.FileName,
                    Arguments = HostProtocol.BuildCommandLine(request.Arguments),
                    WorkingDirectory = request.WorkingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                // 照 app 給的那一份開，不帶主機自己的環境（主機可能是好幾版以前的 app 開的）
                psi.Environment.Clear();
                foreach (var pair in request.Environment)
                    psi.Environment[pair.Key] = pair.Value;

                var process = new Process { StartInfo = psi };
                if (!process.Start())
                    throw new InvalidOperationException("無法啟動 " + request.FileName);
                if (job != IntPtr.Zero)
                    AssignProcessToJobObject(job, process.Handle);

                var hosted = new HostedProcess(Interlocked.Increment(ref nextHandle), process);
                lock (ProcessGate)
                    Processes[hosted.Handle] = hosted;
                result.Ok = true;
                result.Handle = hosted.Handle;
                result.ProcessId = process.Id;
                started = hosted;
            }
            catch (Win32Exception ex)
            {
                result.Error = ex.Message;
                result.NativeError = ex.NativeErrorCode;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            return result;
        }

        internal static void Forget(HostedProcess hosted)
        {
            lock (ProcessGate)
                Processes.Remove(hosted.Handle);
        }

        internal static void Log(string message)
        {
            if (string.IsNullOrEmpty(logPath))
                return;
            try
            {
                lock (WriteGate)
                    File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }

        // ───────────────────────────── 工作物件 ─────────────────────────────

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
        private const uint JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK = 0x00001000;

        /// <summary>
        /// 主機的工作物件（有名稱：app 以名稱打開它查程序）：旗標同 app 的 ChildProcessJob——主機結束（含當掉）時系統一併結束 CLI；
        /// SILENT_BREAKAWAY_OK 讓 CLI 再開的程式（代理替使用者開的編輯器、背景程式）不被綁進來
        /// </summary>
        private static IntPtr CreateJob(string name)
        {
            var handle = CreateJobObject(IntPtr.Zero, name);
            if (handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "建立工作物件失敗");
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK;
            int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "設定工作物件失敗");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return handle;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    }

    /// <summary>
    /// 主機開的一個 CLI 程序：兩條執行緒各讀一條輸出。有 app 接著時轉成 Output 框送出；還沒被確認的輸出留著（app 是第 2 版起）、
    /// 沒有 app 接著時只暫存，之後 Attach 從給的位移補送。兩條都讀完、程序結束後回報 Exited（沒有 app 接著就等接回時再回報）
    /// </summary>
    internal sealed class HostedProcess
    {
        /// <summary>一個程序最多暫存多少位元組（兩條輸出合計）：超過就不留了，這個程序之後接不回去（由 app 結束它、照舊的方式接續）</summary>
        private const long MaxRetained = 64L * 1024 * 1024;

        private sealed class Chunk
        {
            public long Offset;
            public byte[] Data = new byte[0];
        }

        private sealed class OutputStream
        {
            public byte Id;
            public long End;
            public readonly List<Chunk> Retained = new List<Chunk>();
            public bool Closed;
        }

        private readonly Process process;
        private readonly object inputGate = new object();

        /// <summary>保護兩條輸出的暫存、attached、結束狀態：附加與送出在同一把鎖裡，接回時補送的和之後即時的才不會重疊或顛倒</summary>
        private readonly object gate = new object();
        private readonly OutputStream stdout = new OutputStream { Id = HostProtocol.StdOut };
        private readonly OutputStream stderr = new OutputStream { Id = HostProtocol.StdErr };
        private bool attached = true;
        private bool overflow;
        private long retainedBytes;
        private bool exited;
        private int? exitCode;
        private bool exitReported;

        /// <summary>
        /// 等著寫進 stdin 的資料（null＝關閉 stdin）：由這個程序自己的執行緒依序寫。不在讀指令的那條執行緒上寫——
        /// 這個程序不讀 stdin、管線滿了的時候，寫入會卡住，其他程序的指令不能跟著卡
        /// </summary>
        private readonly System.Collections.Concurrent.BlockingCollection<KeyValuePair<int, byte[]?>> input
            = new System.Collections.Concurrent.BlockingCollection<KeyValuePair<int, byte[]?>>();

        private bool inputClosed;

        public HostedProcess(int handle, Process process)
        {
            Handle = handle;
            this.process = process;
            ProcessId = process.Id;
        }

        public int Handle { get; private set; }

        public int ProcessId { get; private set; }

        /// <summary>開始轉送輸出（SpawnResult 送出之後才呼叫）</summary>
        public void StartPumps()
        {
            Start("stdout", stdout, process.StandardOutput.BaseStream);
            Start("stderr", stderr, process.StandardError.BaseStream);
            var writer = new Thread(WriteLoop) { IsBackground = true, Name = "host " + ProcessId + " stdin" };
            writer.Start();
        }

        private void Start(string name, OutputStream target, Stream source)
        {
            var thread = new Thread(() => Pump(target, source)) { IsBackground = true, Name = "host " + ProcessId + " " + name };
            thread.Start();
        }

        private void Pump(OutputStream target, Stream source)
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (true)
                {
                    int n = source.Read(buffer, 0, buffer.Length);
                    if (n <= 0)
                        break;
                    var data = new byte[n];
                    Buffer.BlockCopy(buffer, 0, data, 0, n);
                    Append(target, data);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                // 程序被結束時管線關閉
            }
            finally
            {
                bool both;
                lock (gate)
                {
                    target.Closed = true;
                    both = stdout.Closed && stderr.Closed;
                }
                if (both)
                    OnBothClosed();
            }
        }

        private void Append(OutputStream target, byte[] data)
        {
            lock (gate)
            {
                long offset = target.End;
                target.End += data.Length;
                bool sent = attached && Program.Send(HostProtocol.Output, HostProtocol.EncodeOutput(Handle, target.Id, offset, data, data.Length));
                // 送出去了、app 又不會確認（第 1 版）的不必留；其餘留到 app 確認（Ack）為止
                if (overflow || (sent && !Program.ClientKeeps))
                    return;
                target.Retained.Add(new Chunk { Offset = offset, Data = data });
                retainedBytes += data.Length;
                if (retainedBytes > MaxRetained)
                {
                    overflow = true;
                    stdout.Retained.Clear();
                    stderr.Retained.Clear();
                    retainedBytes = 0;
                    Program.Log("程序 " + ProcessId + " 的暫存超過上限，之後接不回去");
                }
            }
        }

        private void OnBothClosed()
        {
            int? code = null;
            try
            {
                process.WaitForExit();
                code = process.ExitCode;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception)
            {
            }
            bool forget;
            lock (gate)
            {
                exited = true;
                exitCode = code;
                forget = ReportExitLocked();
            }
            // 還排著的寫入都算失敗（app 那邊等著的寫入由 Exited 一併結束）；寫入的執行緒跟著結束
            input.CompleteAdding();
            if (forget)
                Release();
        }

        /// <summary>有 app 接著才回報結束（回 true＝回報了，可以忘掉這個程序）；沒有就留著，等接回時補報</summary>
        private bool ReportExitLocked()
        {
            if (exitReported || !exited || !attached)
                return false;
            if (!Program.Send(HostProtocol.Exited, HostProtocol.EncodeExited(Handle, exitCode)))
                return false;
            exitReported = true;
            return true;
        }

        private void Release()
        {
            Program.Forget(this);
            try
            {
                process.Dispose();
            }
            catch (Exception)
            {
            }
        }

        // ───────────────────────────── 第 2 版：確認、放開、接回 ─────────────────────────────

        public void Ack(long outOffset, long errOffset)
        {
            lock (gate)
            {
                Drop(stdout, outOffset);
                Drop(stderr, errOffset);
            }
        }

        private void Drop(OutputStream target, long offset)
        {
            int remove = 0;
            while (remove < target.Retained.Count && target.Retained[remove].Offset + target.Retained[remove].Data.Length <= offset)
            {
                retainedBytes -= target.Retained[remove].Data.Length;
                remove++;
            }
            if (remove > 0)
                target.Retained.RemoveRange(0, remove);
        }

        /// <summary>先不再送輸出（app 要放開它，或 app 的連線沒了）：回目前兩條輸出的結尾——在這之前的都送過了</summary>
        public void Detach(out long outEnd, out long errEnd)
        {
            lock (gate)
            {
                attached = false;
                outEnd = stdout.End;
                errEnd = stderr.End;
            }
        }

        public HostProcessInfo Describe()
        {
            lock (gate)
            {
                return new HostProcessInfo
                {
                    Handle = Handle,
                    ProcessId = ProcessId,
                    Exited = exited,
                    ExitCode = exitCode,
                    OutEnd = stdout.End,
                    ErrEnd = stderr.End,
                    Overflow = overflow,
                    Attached = attached,
                };
            }
        }

        /// <summary>
        /// 接回：從給的位移把暫存的輸出補送，之後照常即時轉送。位移以前的已經丟掉（確認過了或超過上限）就接不回去。
        /// 回覆（AttachResult）在補送之前送出；程序已經結束的，補送完接著回報 Exited
        /// </summary>
        public void Attach(int requestId, long outOffset, long errOffset)
        {
            bool forget = false;
            lock (gate)
            {
                string? error = overflow ? "暫存的輸出超過上限、已經丟掉" : Covers(stdout, outOffset) && Covers(stderr, errOffset) ? null : "要求的位移已經不在暫存裡";
                Program.Send(HostProtocol.AttachResult, HostProtocol.EncodeAttachResult(requestId, error == null, error));
                if (error != null)
                    return;
                Replay(stdout, outOffset);
                Replay(stderr, errOffset);
                attached = true;
                forget = ReportExitLocked();
            }
            if (forget)
                Release();
        }

        /// <summary>暫存的輸出從這個位移起是連續的（位移剛好在結尾＝沒有要補的）</summary>
        private static bool Covers(OutputStream target, long offset)
        {
            if (offset < 0 || offset > target.End)
                return false;
            if (offset == target.End)
                return true;
            return target.Retained.Count > 0 && target.Retained[0].Offset <= offset;
        }

        private void Replay(OutputStream target, long offset)
        {
            foreach (var chunk in target.Retained)
            {
                long end = chunk.Offset + chunk.Data.Length;
                if (end <= offset)
                    continue;
                if (chunk.Offset >= offset)
                {
                    Program.Send(HostProtocol.Output, HostProtocol.EncodeOutput(Handle, target.Id, chunk.Offset, chunk.Data, chunk.Data.Length));
                    continue;
                }
                // 位移落在這一段中間：只送後半
                int skip = (int)(offset - chunk.Offset);
                var tail = new byte[chunk.Data.Length - skip];
                Buffer.BlockCopy(chunk.Data, skip, tail, 0, tail.Length);
                Program.Send(HostProtocol.Output, HostProtocol.EncodeOutput(Handle, target.Id, offset, tail, tail.Length));
            }
        }

        // ───────────────────────────── stdin ─────────────────────────────

        private void WriteLoop()
        {
            try
            {
                foreach (var item in input.GetConsumingEnumerable())
                {
                    if (item.Value == null)
                    {
                        CloseInputNow();
                        continue;
                    }
                    Program.Send(HostProtocol.WriteResult, HostProtocol.EncodeWriteResult(Handle, item.Key, WriteNow(item.Value)));
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException || ex is InvalidOperationException)
            {
            }
        }

        /// <summary>排進這個程序的寫入佇列；結果由寫入的執行緒以 WriteResult 回報</summary>
        public void Write(int sequence, byte[] data)
        {
            try
            {
                input.Add(new KeyValuePair<int, byte[]?>(sequence, data));
            }
            catch (InvalidOperationException)
            {
                Program.Send(HostProtocol.WriteResult, HostProtocol.EncodeWriteResult(Handle, sequence, false));   // 程序已經結束
            }
        }

        /// <summary>排在已經排著的寫入後面關閉 stdin（順序同 app 送來的）</summary>
        public void CloseInput()
        {
            try
            {
                input.Add(new KeyValuePair<int, byte[]?>(0, null));
            }
            catch (InvalidOperationException)
            {
            }
        }

        private bool WriteNow(byte[] data)
        {
            lock (inputGate)
            {
                if (inputClosed)
                    return false;
                try
                {
                    var stdin = process.StandardInput.BaseStream;
                    stdin.Write(data, 0, data.Length);
                    stdin.Flush();
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
                {
                    return false;   // 程序結束了，或它自己關了 stdin
                }
            }
        }

        private void CloseInputNow()
        {
            lock (inputGate)
            {
                if (inputClosed)
                    return;
                inputClosed = true;
                try
                {
                    process.StandardInput.Close();
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
                {
                }
            }
        }
    }
}
