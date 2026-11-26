// 代理主機：替 app 開 CLI 程序、轉送 stdin／stdout／stderr（設計 docs/plans/2026-11-26-agent-host.md）。
// 不懂任何 CLI 的協定，只搬位元組。.NET Framework 4.8：Windows 內建，不受 .NET 更新刪掉修補版的影響。
// H0：app 的連線斷了（app 結束、當掉）就結束全部程序並結束自己——行為同 CLI 直接由 app 開。之後的階段才在 app 換版時保留程序。
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

        private static readonly object WriteGate = new object();
        private static readonly object ProcessGate = new object();
        private static readonly Dictionary<int, HostedProcess> Processes = new Dictionary<int, HostedProcess>();
        private static NamedPipeServerStream? pipe;
        private static IntPtr job = IntPtr.Zero;
        private static string? logPath;
        private static int nextHandle;

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

            try
            {
                job = CreateJob(jobName!);
                using (var server = CreatePipe(pipeName!))
                {
                    pipe = server;
                    var connecting = server.BeginWaitForConnection(null, null);
                    if (!connecting.AsyncWaitHandle.WaitOne(ConnectTimeout))
                    {
                        Log("沒有 app 連上，結束");
                        return 3;
                    }
                    server.EndWaitForConnection(connecting);
                    Serve(server);
                }
            }
            catch (Exception ex)
            {
                Log("主機失敗：" + ex);
                return 1;
            }
            finally
            {
                // app 不在了：結束所有還在的 CLI（H0；同 app 自己的工作物件在 app 結束時做的事）
                if (job != IntPtr.Zero)
                    TerminateJobObject(job, 1);
            }
            return 0;
        }

        /// <summary>只有同一個 Windows 使用者連得上（預設的存取控制會讓 Everyone 可讀）</summary>
        private static NamedPipeServerStream CreatePipe(string name)
        {
            var security = new PipeSecurity();
            var user = WindowsIdentity.GetCurrent().User!;
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
            return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1 << 16, 1 << 16, security);
        }

        private static void Serve(Stream stream)
        {
            byte type;
            byte[] payload;
            while (HostProtocol.ReadFrame(stream, out type, out payload))
            {
                switch (type)
                {
                    case HostProtocol.Hello:
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
                        Find(HostProtocol.DecodeHandle(payload))?.CloseInput();
                        break;
                    case HostProtocol.Shutdown:
                        return;
                    // 不認得的型別略過：新版 app 對舊主機多送的指令不能讓連線斷掉
                }
            }
        }

        private static HostedProcess? Find(int handle)
        {
            lock (ProcessGate)
            {
                HostedProcess found;
                return Processes.TryGetValue(handle, out found) ? found : null;
            }
        }

        /// <summary>送一個框；app 已經不在（管道斷了）回 false</summary>
        internal static bool Send(byte type, byte[] payload)
        {
            var frame = HostProtocol.Frame(type, payload);
            try
            {
                lock (WriteGate)
                {
                    pipe!.Write(frame, 0, frame.Length);
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

    /// <summary>主機開的一個 CLI 程序：兩條執行緒各讀一條輸出、轉成 Output 框；兩條都讀完、程序結束後送 Exited</summary>
    internal sealed class HostedProcess
    {
        private readonly Process process;
        private readonly object inputGate = new object();

        /// <summary>
        /// 等著寫進 stdin 的資料（null＝關閉 stdin）：由這個程序自己的執行緒依序寫。不在讀指令的那條執行緒上寫——
        /// 這個程序不讀 stdin、管線滿了的時候，寫入會卡住，其他程序的指令不能跟著卡
        /// </summary>
        private readonly System.Collections.Concurrent.BlockingCollection<KeyValuePair<int, byte[]?>> input
            = new System.Collections.Concurrent.BlockingCollection<KeyValuePair<int, byte[]?>>();

        private int streamsOpen = 2;
        private bool inputClosed;

        public HostedProcess(int handle, Process process)
        {
            Handle = handle;
            this.process = process;
        }

        public int Handle { get; private set; }

        /// <summary>開始轉送輸出（SpawnResult 送出之後才呼叫）</summary>
        public void StartPumps()
        {
            Start("stdout", HostProtocol.StdOut, process.StandardOutput.BaseStream);
            Start("stderr", HostProtocol.StdErr, process.StandardError.BaseStream);
            var writer = new Thread(WriteLoop) { IsBackground = true, Name = "host " + process.Id + " stdin" };
            writer.Start();
        }

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

        private void Start(string name, byte stream, Stream source)
        {
            var thread = new Thread(() => Pump(stream, source)) { IsBackground = true, Name = "host " + process.Id + " " + name };
            thread.Start();
        }

        private void Pump(byte stream, Stream source)
        {
            var buffer = new byte[64 * 1024];
            long offset = 0;
            try
            {
                while (true)
                {
                    int n = source.Read(buffer, 0, buffer.Length);
                    if (n <= 0)
                        break;
                    if (!Program.Send(HostProtocol.Output, HostProtocol.EncodeOutput(Handle, stream, offset, buffer, n)))
                        break;
                    offset += n;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                // 程序被結束時管線關閉
            }
            finally
            {
                if (Interlocked.Decrement(ref streamsOpen) == 0)
                    ReportExit();
            }
        }

        private void ReportExit()
        {
            int? exitCode = null;
            try
            {
                process.WaitForExit();
                exitCode = process.ExitCode;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception)
            {
            }
            Program.Send(HostProtocol.Exited, HostProtocol.EncodeExited(Handle, exitCode));
            Program.Forget(this);
            // 還排著的寫入都算失敗（app 那邊等著的寫入由 Exited 一併結束）；寫入的執行緒跟著結束
            input.CompleteAdding();
            try
            {
                process.Dispose();
            }
            catch (Exception)
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
