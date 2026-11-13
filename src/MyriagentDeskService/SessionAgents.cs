using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Myriagent.Remote;
using Microsoft.Win32.SafeHandles;

namespace MyriagentDesk;

/// <summary>
/// 各個工作階段的擷取幫手（<c>MyriagentDesk.exe session-agent --pipe 名稱</c>）。服務在工作階段 0 碰不到使用者的桌面：
/// app 要擷取時，服務在 app 那個工作階段開一個系統權限的幫手，把幫手的管道名稱回給 app，之後 app 直接和幫手講（畫面不經服務轉一手）。
/// 一個工作階段一個幫手，還活著就沿用；幫手沒人連了自己結束（<see cref="DeskServiceRules.HelperIdleExit"/>），服務停止時把還在的都結束。
/// 前景模式（沒有系統權限）也用同一段：目標是自己的工作階段時權杖不必換，開出來的幫手就是一般權限
/// </summary>
internal sealed class SessionAgents(string exePath, string serviceName) : ISessionAgents, IDisposable
{
    /// <summary>幫手開了之後等它的管道建好的上限（.NET 程序啟動約一秒內）</summary>
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(15);

    private readonly object gate = new();
    private readonly Dictionary<int, (SafeProcessHandle Process, string Pipe)> agents = new();
    private bool disposed;

    /// <summary>有幫手還活著（遠端桌面正在用，或剛用完還沒自己結束）：服務這時不自己更新（服務一停幫手就跟著結束）</summary>
    public bool AnyAlive
    {
        get
        {
            lock (gate)
                return !disposed && agents.Values.Any(agent => !Exited(agent.Process));
        }
    }

    public string Open(int sessionId)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (agents.TryGetValue(sessionId, out var agent))
            {
                if (!Exited(agent.Process))
                    return agent.Pipe;
                agent.Process.Dispose();
                agents.Remove(sessionId);
            }

            var pipe = DeskServiceRules.HelperPipeName(serviceName, sessionId, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());
            var (process, processId) = SessionProcess.Start(sessionId, exePath, "session-agent --pipe " + pipe);
            try
            {
                WaitReady(pipe, process);
            }
            catch
            {
                TerminateProcess(process, 1);
                process.Dispose();
                throw;
            }

            agents[sessionId] = (process, pipe);
            ServiceLog.Info($"工作階段 {sessionId} 的擷取幫手已啟動（程序 {processId}）");
            return pipe;
        }
    }

    /// <summary>等幫手的管道建好（只問有沒有，不連進去）；幫手先結束了、等太久都丟例外</summary>
    private static void WaitReady(string pipe, SafeProcessHandle process)
    {
        var path = @"\\.\pipe\" + pipe;
        var started = Environment.TickCount64;
        while (!WaitNamedPipeW(path, 50))
        {
            if (Exited(process))
                throw new InvalidOperationException("擷取幫手啟動後就結束了");
            if (Environment.TickCount64 - started > ReadyTimeout.TotalMilliseconds)
                throw new TimeoutException("擷取幫手沒有在時間內準備好");
            Thread.Sleep(50);
        }
    }

    private static bool Exited(SafeProcessHandle process) => WaitForSingleObject(process, 0) == 0;

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            foreach (var (process, _) in agents.Values)
            {
                if (!Exited(process))
                    TerminateProcess(process, 0);
                process.Dispose();
            }
            agents.Clear();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipeW(string name, uint timeoutMs);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
}
