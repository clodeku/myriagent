using System.Globalization;
using Myriagent.Agents;

namespace FakeDevice;

/// <summary>
/// 交給會話的一輪（<see cref="DeviceRuntime.ChatAsync"/> 放、<see cref="ScriptedSession.SendAsync"/> 取）：劇本（null＝固定回覆）、開始與結束的通知
/// </summary>
internal sealed class PendingTurn(ScriptedTurn? turn)
{
    public ScriptedTurn? Turn => turn;

    /// <summary>會話收下了這一輪（送出沒有被中樞擋下）</summary>
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>這一輪結束（完成事件已交給中樞）：結果</summary>
    public TaskCompletionSource<AgentTurnOutcome> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// 會話收到的呼叫次數（WP4.9 步 15：FF3 以它證明每個遠端動作只到假會話一次——第二個分頁按同一張卡、同一個 commandId 重送都停在中樞的檢查）。
/// 同一台的所有 <see cref="ScriptedSession"/> 共用一份；任何執行緒（<see cref="Interlocked"/>）。<c>status</c> 的 <c>session</c> 就是 <see cref="ToJson"/>
/// </summary>
internal sealed class SessionCalls
{
    private long sends;
    private long interrupts;
    private long answers;
    private long declines;
    private long approvals;
    private long plans;
    private long steers;
    private long modelSwitches;
    private long modelSwitchesWhileRunning;
    private readonly object modelGate = new();
    private (string? Model, string? Effort, bool Running)? lastModelSwitch;

    public void Send() => Interlocked.Increment(ref sends);

    public void Interrupt() => Interlocked.Increment(ref interrupts);

    public void Answer() => Interlocked.Increment(ref answers);

    public void Decline() => Interlocked.Increment(ref declines);

    public void Approval() => Interlocked.Increment(ref approvals);

    public void Plan() => Interlocked.Increment(ref plans);

    public void Steer() => Interlocked.Increment(ref steers);

    /// <summary>即時換模型與推理強度（SetModelAsync）；running＝當時這一輪還在進行</summary>
    public void ModelSwitch(string? model, string? effort, bool running)
    {
        Interlocked.Increment(ref modelSwitches);
        if (running)
            Interlocked.Increment(ref modelSwitchesWhileRunning);
        lock (modelGate)
            lastModelSwitch = (model, effort, running);
    }

    /// <summary>最後一次 SetModelAsync 收到的模型、推理強度（null＝預設）與當時有沒有在執行；沒有呼叫過是 null</summary>
    public (string? Model, string? Effort, bool Running)? LastModelSwitch
    {
        get
        {
            lock (modelGate)
                return lastModelSwitch;
        }
    }

    /// <summary>
    /// {sends, interrupts, answers, declines, approvals, plans, steers, modelSwitches, modelSwitchesWhileRunning}：送出（含排隊訊息接著送）、停止、回答問卷、略過問卷、
    /// 回應核准卡、回應計畫、執行中插入的嘗試（中樞只對支援插入的代理呼叫 SteerAsync：AgentHubRules.SupportsSteering；假會話一律不插，訊息照舊排隊）、
    /// 即時換模型與推理強度（SetModelAsync，其中有幾次是這一輪還在進行時；假會話一律回已套用）
    /// </summary>
    public Newtonsoft.Json.Linq.JObject ToJson() => new()
    {
        ["sends"] = Interlocked.Read(ref sends),
        ["interrupts"] = Interlocked.Read(ref interrupts),
        ["answers"] = Interlocked.Read(ref answers),
        ["declines"] = Interlocked.Read(ref declines),
        ["approvals"] = Interlocked.Read(ref approvals),
        ["plans"] = Interlocked.Read(ref plans),
        ["steers"] = Interlocked.Read(ref steers),
        ["modelSwitches"] = Interlocked.Read(ref modelSwitches),
        ["modelSwitchesWhileRunning"] = Interlocked.Read(ref modelSwitchesWhileRunning),
    };
}

/// <summary>
/// 可腳本化的代理會話（<see cref="IAgentSession"/>）：不啟動任何程序、不呼叫任何 CLI。每次送出時向 <paramref name="take"/> 要這一輪：
/// <list type="bullet">
/// <item>沒有劇本：在宿主的 UI 執行緒上依序發出「會話已開始」（第一次）、一段固定的回覆（<see cref="Reply"/>）與「這一輪完成」（第 3 階段的行為）。</item>
/// <item>有劇本（<see cref="ScriptedTurn"/>）：在背景依序發出文字片段、工具從執行中到完成、問卷卡與核准卡（等中樞回應才接著做）、代理產出的媒體，
/// 最後是這一輪的結果；每一段之前照步驟的延遲等。停止（<see cref="InterruptAsync"/>）時以「被中斷」結束這一輪。</item>
/// </list>
/// 事件一律經 <paramref name="post"/> 交給宿主的 UI 執行緒（依序）。中樞的對話功能因此照常運作；第 4 階段的發布看到的是真的串流與卡片。
/// <paramref name="calls"/>（可省略）：每個送出、停止、回答與回應各記一次（<see cref="SessionCalls"/>）
/// </summary>
internal sealed class ScriptedSession(AgentProvider provider, Action<Action> post, Func<PendingTurn?>? take = null, SessionCalls? calls = null) : IAgentSession
{
    private readonly object gate = new();
    private readonly Dictionary<string, TaskCompletionSource> waiting = new(StringComparer.Ordinal);
    private CancellationTokenSource? running;
    private int turns;
    private bool started;

    public AgentProvider Provider { get; } = provider;

    /// <summary>沒有劇本時每一輪的回覆文字</summary>
    public string Reply { get; set; } = "（fake-device 的腳本回覆：不執行任何 CLI）";

    public bool IsRunning { get; private set; }

    public bool HasLiveProcess => false;

    public string? RunningVersion => null;

    public string? ProcessInstance => null;

    public bool IsAtSafePoint => !IsRunning;

    public bool HasLiveBackgroundWork => false;

    public IReadOnlyList<AgentBackgroundTool> LiveBackgroundTools => [];

    public event Action<AgentEvent>? EventReceived;

    public Task SendAsync(AgentTurnInput input, AgentSessionSettings settings)
    {
        calls?.Send();
        IsRunning = true;
        var turn = Interlocked.Increment(ref turns);
        var pending = take?.Invoke();
        pending?.Started.TrySetResult();
        if (pending?.Turn is not { } script)
        {
            post(() =>
            {
                EnsureStarted();
                EventReceived?.Invoke(new AgentTextCompletedEvent($"fake-text-{turn}", Reply));
                IsRunning = false;
                EventReceived?.Invoke(new AgentTurnCompletedEvent(AgentTurnOutcome.Completed, null, null));
                pending?.Completed.TrySetResult(AgentTurnOutcome.Completed);
            });
            return Task.CompletedTask;
        }

        var cancel = new CancellationTokenSource();
        lock (gate)
        {
            running?.Cancel();
            running = cancel;
        }

        _ = Task.Run(() => RunAsync(turn, script, pending, cancel));
        return Task.CompletedTask;
    }

    /// <summary>照劇本發事件（背景）；任何結束方式都以一個「這一輪完成」事件收尾</summary>
    private async Task RunAsync(int turn, ScriptedTurn script, PendingTurn pending, CancellationTokenSource cancel)
    {
        var outcome = script.Outcome;
        var ct = cancel.Token;
        try
        {
            post(EnsureStarted);
            for (var i = 0; i < script.Steps.Count; i++)
            {
                var step = script.Steps[i];
                switch (step)
                {
                    case ScriptedText text:
                    {
                        var block = $"fake-{turn}-{i}";
                        var parts = Split(text.Text, text.Segments);
                        var full = new System.Text.StringBuilder();
                        for (var k = 0; k < parts.Count; k++)
                        {
                            await PauseAsync(text.DelayMs, ct);
                            // 時間標記在發出的當下產生（D8：瀏覽器端以它算這一段從產生到看到的延遲）
                            var part = text.Stamp ? StampRules.Format(DateTimeOffset.UtcNow) + parts[k] : parts[k];
                            if (text.CompleteEachSegment)
                            {
                                var segmentBlock = $"{block}-{k}";
                                Emit(new AgentTextDeltaEvent(segmentBlock, part));
                                Emit(new AgentTextCompletedEvent(segmentBlock, part));
                                continue;
                            }

                            full.Append(part);
                            Emit(new AgentTextDeltaEvent(block, part));
                        }

                        if (!text.CompleteEachSegment)
                            Emit(new AgentTextCompletedEvent(block, text.Stamp ? full.ToString() : text.Text));
                        break;
                    }
                    case ScriptedTool tool:
                    {
                        var toolId = $"fake-tool-{turn}-{i}";
                        await PauseAsync(tool.DelayMs, ct);
                        Emit(new AgentToolStartedEvent(toolId, tool.Name, tool.Kind, tool.Title, tool.Detail, tool.FilePath));
                        foreach (var part in Split(tool.Output, tool.Segments))
                        {
                            await PauseAsync(tool.DelayMs, ct);
                            Emit(new AgentToolOutputEvent(toolId, part));
                        }

                        Emit(new AgentToolCompletedEvent(toolId, tool.Failed ? AgentItemStatus.Failed : AgentItemStatus.Done, tool.Output, []));
                        break;
                    }
                    case ScriptedQuestion question:
                    {
                        await PauseAsync(question.DelayMs, ct);
                        var key = $"fake-req-{turn}-{i}";
                        var answered = Expect(key);
                        Emit(new AgentQuestionEvent(key, question.Questions, AnswerAsMessage: false));
                        await answered.WaitAsync(ct);
                        break;
                    }
                    case ScriptedApproval approval:
                    {
                        await PauseAsync(approval.DelayMs, ct);
                        var key = $"fake-req-{turn}-{i}";
                        var responded = Expect(key);
                        Emit(new AgentApprovalEvent(key, approval.ToolName, approval.Kind, approval.Title, approval.Detail, ToolId: null, CanAllowForSession: true));
                        await responded.WaitAsync(ct);
                        break;
                    }
                    case ScriptedMedia media:
                        await PauseAsync(media.DelayMs, ct);
                        Emit(new AgentMediaEvent(media.Path));
                        break;
                    case ScriptedThinking thinking:
                    {
                        var block = $"fake-think-{turn}-{i}";
                        foreach (var part in Split(thinking.Text, thinking.Segments))
                        {
                            await PauseAsync(thinking.DelayMs, ct);
                            Emit(new AgentThinkingDeltaEvent(block, part));
                        }

                        break;
                    }
                    case ScriptedTodo todo:
                        await PauseAsync(todo.DelayMs, ct);
                        Emit(new AgentPlanEvent(todo.Explanation, todo.Steps.Select(s => new AgentPlanStep { Text = s.Text, Status = s.Status }).ToList()));
                        break;
                    case ScriptedPlanApproval plan:
                    {
                        await PauseAsync(plan.DelayMs, ct);
                        var key = $"fake-req-{turn}-{i}";
                        var responded = Expect(key);
                        Emit(new AgentApprovalEvent(key, plan.ToolName, "plan", plan.Title, plan.Detail, ToolId: null, CanAllowForSession: false, IsPlan: true));
                        await responded.WaitAsync(ct);
                        break;
                    }
                    default:
                        throw new InvalidOperationException($"不認得的劇本步驟：{step.GetType().Name}");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = AgentTurnOutcome.Interrupted;
        }
        finally
        {
            lock (gate)
            {
                waiting.Clear();
                if (ReferenceEquals(running, cancel))
                    running = null;
            }

            cancel.Dispose();
        }

        post(() =>
        {
            IsRunning = false;
            EventReceived?.Invoke(new AgentTurnCompletedEvent(outcome, outcome == AgentTurnOutcome.Failed ? "劇本指定這一輪失敗" : null, script.Usage));
            pending.Completed.TrySetResult(outcome);
        });
    }

    private void EnsureStarted()
    {
        if (started)
            return;
        started = true;
        EventReceived?.Invoke(new AgentSessionStartedEvent("fake-" + Guid.NewGuid().ToString("N")[..12], null, null));
    }

    private void Emit(AgentEvent ev) => post(() => EventReceived?.Invoke(ev));

    private static Task PauseAsync(int milliseconds, CancellationToken ct) => milliseconds > 0 ? Task.Delay(milliseconds, ct) : Task.CompletedTask;

    /// <summary>等中樞回應的卡片（以請求鍵認）</summary>
    private Task Expect(string requestKey)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
            waiting[requestKey] = done;
        return done.Task;
    }

    private Task Resolve(string requestKey)
    {
        TaskCompletionSource? done;
        lock (gate)
        {
            if (waiting.Remove(requestKey, out done))
                done.TrySetResult();
        }

        return Task.CompletedTask;
    }

    /// <summary>把文字切成最多 <paramref name="segments"/> 段（不切開代理字對、每段非空；空字串是零段）</summary>
    internal static IReadOnlyList<string> Split(string text, int segments)
    {
        var elements = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
            elements.Add(enumerator.GetTextElement());
        var count = Math.Min(Math.Max(segments, 1), elements.Count);
        var parts = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var from = elements.Count * i / count;
            var to = elements.Count * (i + 1) / count;
            parts.Add(string.Concat(elements.Skip(from).Take(to - from)));
        }

        return parts;
    }

    public Task InterruptAsync()
    {
        calls?.Interrupt();
        CancellationTokenSource? current;
        lock (gate)
            current = running;
        if (current is null)
            IsRunning = false;
        else
            current.Cancel();
        return Task.CompletedTask;
    }

    public Task AnswerQuestionAsync(string requestKey, IReadOnlyDictionary<string, IReadOnlyList<string>> answers)
    {
        calls?.Answer();
        return Resolve(requestKey);
    }

    public Task DeclineQuestionAsync(string requestKey)
    {
        calls?.Decline();
        return Resolve(requestKey);
    }

    public Task RespondApprovalAsync(string requestKey, AgentApprovalChoice choice, string? feedback)
    {
        calls?.Approval();
        return Resolve(requestKey);
    }

    public Task RespondPlanAsync(string requestKey, AgentPlanChoice choice, string? feedback)
    {
        calls?.Plan();
        return Resolve(requestKey);
    }

    public Task<bool> SetPermissionModeAsync(AgentPermissionMode mode) => Task.FromResult(true);

    /// <summary>即時換模型與推理強度：一律回已套用（同 Claude、Grok 的會話），記一次（含當時有沒有在執行）</summary>
    public Task<bool> SetModelAsync(string? model, string? effort)
    {
        calls?.ModelSwitch(model, effort, IsRunning);
        return Task.FromResult(true);
    }

    /// <summary>
    /// 假裝置的劇本沒有執行中插入：一律不插（中樞照舊排進佇列等這一輪結束），但記一次嘗試（<see cref="SessionCalls.Steer"/>）——
    /// 中樞只對支援插入的代理（Claude、Codex）呼叫，Grok、Antigravity 的對話直接排隊（AgentHubRules.SupportsSteering）
    /// </summary>
    public Task<bool> SteerAsync(AgentTurnInput input, CancellationToken cancellationToken = default)
    {
        calls?.Steer();
        return Task.FromResult(false);
    }

    public bool HasRemoteControl => false;

    public Task SetRemoteControlAsync(AgentRemoteControlOptions? options) => Task.CompletedTask;

    public Task RenameAsync(string title) => Task.CompletedTask;

    public Task<bool> ReattachRemoteControlAsync(AgentSessionSettings settings) => Task.FromResult(false);

    public Task<AgentRestartResult> RestartAsync(AgentRestartRequest request) => Task.FromResult(AgentRestartResult.Unsupported);

    public Task CloseAsync()
    {
        CancelRunning();
        IsRunning = false;
        return Task.CompletedTask;
    }

    public void Dispose() => CancelRunning();

    private void CancelRunning()
    {
        lock (gate)
            running?.Cancel();
    }
}
