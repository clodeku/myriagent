using System.Collections.Concurrent;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Tests.Hub;
using Xunit.Abstractions;

namespace Myriagent.Tests.Integration;

/// <summary>真的會話＋事件記錄（時間戳）：依建立順序（第一則送出時建立）；中樞重啟後新建的會話接在後面</summary>
internal sealed class Sessions
{
    private readonly List<Recorded> created = new();

    public IAgentSession Create(AgentProvider provider)
    {
        IAgentSession session = provider switch
        {
            AgentProvider.Claude => new ClaudeAgentSession(),
            AgentProvider.Codex => new CodexAgentSession(),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "整合測試只跑 Claude 與 Codex"),
        };
        var recorded = new Recorded(session);
        session.EventReceived += recorded.Add;
        lock (created)
            created.Add(recorded);
        return session;
    }

    public Recorded this[int index]
    {
        get
        {
            lock (created)
                return created[index];
        }
    }

    public int Count
    {
        get
        {
            lock (created)
                return created.Count;
        }
    }
}

internal sealed class Recorded
{
    private readonly ConcurrentQueue<(DateTime At, AgentEvent Event)> events = new();

    public Recorded(IAgentSession session) => Session = session;

    public IAgentSession Session { get; }

    public void Add(AgentEvent ev) => events.Enqueue((DateTime.UtcNow, ev));

    public List<(DateTime At, T Event)> Of<T>() where T : AgentEvent
        => events.Where(e => e.Event is T).Select(e => (e.At, (T)e.Event)).ToList();
}

internal static class HubIntegration
{
    /// <summary>先檢查再寫（寫之前等 15 秒，排空與換版發生在工具執行中）：重跑只會回報 exists，寫檔次數看輸出裡的 written</summary>
    public const string GuardedWrite = "if (Test-Path done.txt) { 'exists' } else { Start-Sleep -Seconds 15; Set-Content done.txt done; 'written' }";

    /// <summary>模型、推理強度與完全存取（不跳核准）</summary>
    public static void SetOptions(HubHarness h, string conversationId, string model, string effort)
    {
        h.Command(h.Main, "setOption", new { conversationId, key = "model", value = model });
        h.Command(h.Main, "setOption", new { conversationId, key = "effort", value = effort });
        h.Command(h.Main, "setOption", new { conversationId, key = "permission", value = nameof(AgentPermissionMode.FullAccess) });
    }

    public static async Task WaitUntilAsync(Func<bool> condition, string because, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"{timeout.TotalSeconds:0} 秒內沒有等到：{because}");
            await Task.Delay(100);
        }
    }

    /// <summary>失敗時的診斷：app 今天記錄檔的最後幾行（只在測試的暫存資料夾）</summary>
    public static void WriteLogTail(ITestOutputHelper output, int lines = 120)
    {
        Log.Flush();
        var logFile = Path.Combine(AppPaths.LogDirectory, $"{DateTime.Now:yyyy-MM-dd}.log");
        if (!File.Exists(logFile))
            return;
        foreach (var line in File.ReadAllLines(logFile).TakeLast(lines))
            output.WriteLine("[log] " + line);
    }
}
