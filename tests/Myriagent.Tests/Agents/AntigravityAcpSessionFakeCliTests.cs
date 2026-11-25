using System.Collections.Concurrent;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>
/// 以假的 <c>agy acp --stdio</c>（ASCII Windows PowerShell 腳本，依 agy 1.5.17 實測 JSON-RPC 2.0 封包）驗證 <see cref="AntigravityAcpSession"/>：
/// initialize／session/new／session/resume、內嵌圖片、即時切換模型與權限模式（免重開程序）、互動權限核准（session/request_permission）、
/// 結構化問卷（elicitation/create）、update_steps 待辦清單、usage_update 上下文視窗、session/cancel 停止、兩階段 RestartAsync 與標註檔改名同步。
/// </summary>
public abstract class AntigravityAcpSessionFakeCliTestsBase : IDisposable
{
    private protected static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private protected const string FakeAcpAgy = """"
        $ErrorActionPreference = 'Stop'
        $utf8 = New-Object System.Text.UTF8Encoding $false
        $out = New-Object System.IO.StreamWriter([Console]::OpenStandardOutput(), $utf8)
        $out.NewLine = [string][char]10
        $out.AutoFlush = $true
        $in = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $utf8)
        $argList = @($args)
        $scenario = $env:FAKE_ACP_SCENARIO
        $cliVer = if ($env:FAKE_ACP_VERSION) { $env:FAKE_ACP_VERSION } else { '1.5.17' }

        function Send([hashtable]$obj) {
            $json = ConvertTo-Json -InputObject $obj -Depth 15 -Compress
            $out.WriteLine($json)
        }

        $pIdx = [array]::IndexOf($argList, '-p')
        if ($pIdx -ge 0 -and $pIdx + 1 -lt $argList.Count) {
            $out.WriteLine('single-prompt:' + $argList[$pIdx + 1])
            exit 0
        }
        if ($argList.Count -ge 2 -and $argList[0] -eq 'mcp') {
            $out.WriteLine('mcp-subcommand:' + ($argList -join ' '))
            exit 0
        }

        $sid = ''
        $curModel = 'gemini-4-neon'
        $curEffort = 'high'
        $curMode = 'default'
        $turn = 0

        function SessionResult([string]$id) {
            return [ordered]@{
                sessionId = $id
                models = [ordered]@{
                    currentModelId = 'gemini-4-neon-high'
                    availableModels = @(
                        [ordered]@{ modelId = 'gemini-4-neon-high'; name = 'Gemini 4 Neon (High)' },
                        [ordered]@{ modelId = 'gemini-4-neon-low'; name = 'Gemini 4 Neon (Low)' },
                        [ordered]@{ modelId = 'gemini-pro-orchestrator'; name = 'Orchestrator (Carbon)' },
                        [ordered]@{ modelId = 'gemini-3.8-flash-low'; name = 'Gemini 3.8 Flash (Low)' }
                    )
                }
                modes = [ordered]@{
                    currentModeId = $curMode
                    availableModes = @(
                        [ordered]@{ id = 'default'; name = 'Default' },
                        [ordered]@{ id = 'accept-edits'; name = 'Accept edits' },
                        [ordered]@{ id = 'auto'; name = 'Auto' },
                        [ordered]@{ id = 'plan'; name = 'Plan' }
                    )
                }
                configOptions = @(
                    [ordered]@{
                        id = 'model'; name = 'Model'; category = 'model'; currentValue = $curModel
                        options = @(
                            [ordered]@{ value = 'gemini-4-neon'; name = 'Gemini 4 Neon' },
                            [ordered]@{ value = 'gemini-pro-orchestrator'; name = 'Orchestrator (Carbon)' },
                            [ordered]@{ value = 'gemini-3.8-flash'; name = 'Gemini 3.8 Flash' }
                        )
                    },
                    [ordered]@{
                        id = 'thought_level'; name = 'Thinking Level'; category = 'thought_level'; currentValue = $curEffort
                        options = @(
                            [ordered]@{ value = 'low'; name = 'Low' },
                            [ordered]@{ value = 'high'; name = 'High' }
                        )
                    },
                    [ordered]@{
                        id = 'mode'; name = 'Mode'; category = 'mode'; currentValue = $curMode
                        options = @(
                            [ordered]@{ value = 'default'; name = 'Default' },
                            [ordered]@{ value = 'accept-edits'; name = 'Accept edits' },
                            [ordered]@{ value = 'auto'; name = 'Auto' },
                            [ordered]@{ value = 'plan'; name = 'Plan' }
                        )
                    }
                )
            }
        }

        while ($true) {
            $line = $in.ReadLine()
            if ($null -eq $line) { exit 0 }
            if ($line.Trim().Length -eq 0) { continue }
            $msg = $line | ConvertFrom-Json
            $method = [string]$msg.method
            $id = $msg.id

            if ($method -eq 'initialize') {
                if ($scenario -eq 'unauth') {
                    Send ([ordered]@{ jsonrpc = '2.0'; id = $id; error = [ordered]@{ code = -32000; message = 'Authentication required' } })
                    continue
                }
                Send ([ordered]@{
                    jsonrpc = '2.0'; id = $id
                    result = [ordered]@{
                        protocolVersion = 1
                        agentCapabilities = [ordered]@{ loadSession = $true; promptCapabilities = [ordered]@{ image = $true } }
                        agentInfo = [ordered]@{ name = 'Antigravity ACP Server'; version = $cliVer }
                    }
                })
                continue
            }

            if ($method -eq 'session/resume') {
                $reqSid = [string]$msg.params.sessionId
                if ($scenario -eq 'missing32002') {
                    Send ([ordered]@{ jsonrpc = '2.0'; id = $id; error = [ordered]@{ code = -32002; message = ('Session not found: ' + $reqSid) } })
                    continue
                }
                if ($scenario -eq 'missing32603') {
                    Send ([ordered]@{ jsonrpc = '2.0'; id = $id; error = [ordered]@{ code = -32603; message = ('Failed to read session ' + $reqSid + '; retry, or load it with session/load') } })
                    continue
                }
                $sid = $reqSid
                Send ([ordered]@{ jsonrpc = '2.0'; id = $id; result = (SessionResult $sid) })
                continue
            }

            if ($method -eq 'session/new') {
                $sid = 'acp-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
                Send ([ordered]@{ jsonrpc = '2.0'; id = $id; result = (SessionResult $sid) })
                Send ([ordered]@{ jsonrpc = '2.0'; method = 'session/update'; params = [ordered]@{ sessionId = $sid; update = [ordered]@{ sessionUpdate = 'usage_update'; used = 0; size = 1048576 } } })
                continue
            }

            if ($method -eq 'session/set_model') {
                $curModel = [string]$msg.params.modelId
                Send ([ordered]@{ jsonrpc = '2.0'; id = $id; result = @{} })
                if ($curModel -like '*orchestrator*') {
                    Send ([ordered]@{
                        jsonrpc = '2.0'; method = 'session/update'
                        params = [ordered]@{
                            sessionId = $sid
                            update = [ordered]@{
                                sessionUpdate = 'config_option_update'
                                configOptions = @(
                                    [ordered]@{ id = 'model'; currentValue = $curModel; options = @() },
                                    [ordered]@{ id = 'mode'; currentValue = $curMode; options = @() }
                                )
                            }
                        }
                    })
                }
                continue
            }

            if ($method -eq 'session/set_config_option') {
                if ([string]$msg.params.configId -eq 'thought_level') { $curEffort = [string]$msg.params.value }
                Send ([ordered]@{ jsonrpc = '2.0'; id = $id; result = @{} })
                continue
            }

            if ($method -eq 'session/set_mode') {
                $curMode = [string]$msg.params.modeId
                Send ([ordered]@{ jsonrpc = '2.0'; id = $id; result = @{} })
                continue
            }

            if ($method -eq 'session/prompt') {
                $turn++
                $blocks = @($msg.params.prompt)
                $textBlock = ($blocks | Where-Object { $_.type -eq 'text' } | Select-Object -Last 1)
                $imgCount = @($blocks | Where-Object { $_.type -eq 'image' }).Count

                if ($scenario -eq 'prepaid_quota') {
                    Send ([ordered]@{
                        jsonrpc = '2.0'; id = $id
                        error = [ordered]@{ code = -32603; message = "Your organization's prepaid balance is insufficient. Contact your administrator to add funds." }
                    })
                    continue
                }

                if ($scenario -eq 'neterr' -and $turn -eq 2) {
                    Send ([ordered]@{
                        jsonrpc = '2.0'; id = $id
                        error = [ordered]@{ code = -32603; message = 'API error (attempt 2): request failed: stream idle timeout (5m0s): context deadline exceeded' }
                    })
                    continue
                }

                if ($scenario -eq 'ask_perm') {
                    Send ([ordered]@{
                        jsonrpc = '2.0'; id = 'perm-' + $turn; method = 'session/request_permission'
                        params = [ordered]@{
                            sessionId = $sid
                            toolCall = [ordered]@{
                                toolCallId = 'call-perm-' + $turn; name = 'run_command'; kind = 'execute'
                                title = 'git status'; args = [ordered]@{ CommandLine = 'git status' }
                            }
                            options = @(
                                [ordered]@{ optionId = 'allow'; name = 'Allow'; kind = 'allow_once' },
                                [ordered]@{ optionId = 'deny'; name = 'Deny'; kind = 'reject_once' }
                            )
                        }
                    })
                    $permLine = $in.ReadLine()
                    if ($null -eq $permLine) { exit 0 }
                    $permResp = $permLine | ConvertFrom-Json
                    $chosen = [string]$permResp.result.outcome.optionId
                    if ($chosen -eq 'allow') {
                        Send ([ordered]@{
                            jsonrpc = '2.0'; method = 'session/update'
                            params = [ordered]@{
                                sessionId = $sid
                                update = [ordered]@{
                                    sessionUpdate = 'tool_call_update'; toolCallId = 'call-perm-' + $turn; name = 'run_command'
                                    status = 'completed'; rawOutput = [ordered]@{ output = 'clean tree'; exit_code = 0 }
                                }
                            }
                        })
                    } else {
                        Send ([ordered]@{
                            jsonrpc = '2.0'; method = 'session/update'
                            params = [ordered]@{
                                sessionId = $sid
                                update = [ordered]@{
                                    sessionUpdate = 'tool_call_update'; toolCallId = 'call-perm-' + $turn; name = 'run_command'
                                    status = 'rejected'; rawOutput = [ordered]@{ output = 'user denied permission'; exit_code = 1 }
                                }
                            }
                        })
                    }
                    Send ([ordered]@{
                        jsonrpc = '2.0'; method = 'session/update'
                        params = [ordered]@{ sessionId = $sid; update = [ordered]@{ sessionUpdate = 'agent_message_chunk'; content = [ordered]@{ type = 'text'; text = ('perm:' + $chosen) } } }
                    })
                    Send ([ordered]@{ jsonrpc = '2.0'; id = $id; result = [ordered]@{ stopReason = 'end_turn'; usage = [ordered]@{ inputTokens = 50; outputTokens = 10; cachedReadTokens = 0; thoughtTokens = 0 } } })
                    continue
                }

                if ($scenario -eq 'ask_q') {
                    Send ([ordered]@{
                        jsonrpc = '2.0'; id = 'q-' + $turn; method = 'elicitation/create'
                        params = [ordered]@{
                            mode = 'form'; message = 'Pick target env'
                            requestedSchema = [ordered]@{
                                type = 'object'
                                properties = [ordered]@{
                                    env = [ordered]@{ type = 'string'; title = 'Env'; description = 'Select target'; enum = @('staging', 'prod') }
                                }
                            }
                        }
                    })
                    $qLine = $in.ReadLine()
                    if ($null -eq $qLine) { exit 0 }
                    $qResp = $qLine | ConvertFrom-Json
                    $act = [string]$qResp.result.action
                    $ans = if ($act -eq 'accept') { [string]$qResp.result.content.env } else { '' }
                    Send ([ordered]@{
                        jsonrpc = '2.0'; method = 'session/update'
                        params = [ordered]@{ sessionId = $sid; update = [ordered]@{ sessionUpdate = 'agent_message_chunk'; content = [ordered]@{ type = 'text'; text = ($act + ':' + $ans) } } }
                    })
                    Send ([ordered]@{ jsonrpc = '2.0'; id = $id; result = [ordered]@{ stopReason = 'end_turn' } })
                    continue
                }

                if ($scenario -eq 'cancel_turn' -and $turn -eq 1) {
                    Send ([ordered]@{
                        jsonrpc = '2.0'; method = 'session/update'
                        params = [ordered]@{ sessionId = $sid; update = [ordered]@{ sessionUpdate = 'agent_message_chunk'; content = [ordered]@{ type = 'text'; text = 'working' } } }
                    })
                    Send ([ordered]@{
                        jsonrpc = '2.0'; method = 'session/update'
                        params = [ordered]@{
                            sessionId = $sid
                            update = [ordered]@{
                                sessionUpdate = 'tool_call'; toolCallId = 'call-long-1'; name = 'run_command'
                                status = 'in_progress'; args = [ordered]@{ CommandLine = 'sleep 60' }
                            }
                        }
                    })
                    while ($true) {
                        $subLine = $in.ReadLine()
                        if ($null -eq $subLine) { exit 0 }
                        $sub = $subLine | ConvertFrom-Json
                        if ([string]$sub.method -eq 'session/set_model') {
                            $curModel = [string]$sub.params.modelId
                            Send ([ordered]@{ jsonrpc = '2.0'; id = $sub.id; result = @{} })
                        } elseif ([string]$sub.method -eq 'session/set_config_option') {
                            $curEffort = [string]$sub.params.value
                            Send ([ordered]@{ jsonrpc = '2.0'; id = $sub.id; result = @{} })
                        } elseif ([string]$sub.method -eq 'session/set_mode') {
                            $curMode = [string]$sub.params.modeId
                            Send ([ordered]@{ jsonrpc = '2.0'; id = $sub.id; result = @{} })
                        } elseif ([string]$sub.method -eq 'session/cancel') {
                            Send ([ordered]@{ jsonrpc = '2.0'; id = $id; result = [ordered]@{ stopReason = 'cancelled' } })
                            break
                        }
                    }
                    continue
                }

                # Default normal turn: emit update_steps + usage_update + run_command + JSON summary message
                Send ([ordered]@{
                    jsonrpc = '2.0'; method = 'session/update'
                    params = [ordered]@{
                        sessionId = $sid
                        update = [ordered]@{
                            sessionUpdate = 'tool_call'; toolCallId = 'steps-' + $turn; name = 'update_steps'; status = 'completed'
                            args = [ordered]@{ steps = @([ordered]@{ title = 'Step 1'; status = 'done' }, [ordered]@{ title = 'Step 2'; status = 'in_progress' }) }
                        }
                    }
                })
                Send ([ordered]@{
                    jsonrpc = '2.0'; method = 'session/update'
                    params = [ordered]@{
                        sessionId = $sid
                        update = [ordered]@{
                            sessionUpdate = 'tool_call'; toolCallId = 'cmd-' + $turn; name = 'run_command'; status = 'in_progress'
                            args = [ordered]@{ CommandLine = 'echo ok' }
                        }
                    }
                })
                Send ([ordered]@{
                    jsonrpc = '2.0'; method = 'session/update'
                    params = [ordered]@{
                        sessionId = $sid
                        update = [ordered]@{
                            sessionUpdate = 'tool_call_update'; toolCallId = 'cmd-' + $turn; name = 'run_command'; status = 'completed'
                            rawOutput = [ordered]@{ output = "ok`r`n"; exit_code = 0 }
                        }
                    }
                })
                Send ([ordered]@{
                    jsonrpc = '2.0'; method = 'session/update'
                    params = [ordered]@{ sessionId = $sid; update = [ordered]@{ sessionUpdate = 'usage_update'; used = 14200; size = 1048576 } }
                })

                $summary = ConvertTo-Json -Compress -InputObject ([ordered]@{
                    turn = $turn; sid = $sid; args = $argList; model = $curModel; effort = $curEffort; mode = $curMode
                    images = $imgCount; text = [string]$textBlock.text; autoCompact = [string]$env:AGY_CLI_AUTO_COMPACT_OVERRIDE
                })
                Send ([ordered]@{
                    jsonrpc = '2.0'; method = 'session/update'
                    params = [ordered]@{ sessionId = $sid; update = [ordered]@{ sessionUpdate = 'agent_message_chunk'; content = [ordered]@{ type = 'text'; text = $summary } } }
                })
                Send ([ordered]@{
                    jsonrpc = '2.0'; id = $id
                    result = [ordered]@{
                        stopReason = 'end_turn'
                        usage = [ordered]@{ inputTokens = 100; outputTokens = 20; cachedReadTokens = 50; cachedWriteTokens = 0; thoughtTokens = 5; totalTokens = 175 }
                    }
                })
            }
        }
        """";

    private protected readonly TempDirectory temp = new("fake-agy-acp");
    private protected readonly string script;
    private protected readonly string work;
    private protected readonly string fakeHome;

    protected AntigravityAcpSessionFakeCliTestsBase()
    {
        script = temp.WriteFile("fake-agy-acp.ps1", FakeAcpAgy);
        work = temp.CreateDirectory("work");
        fakeHome = temp.CreateDirectory("home");
    }

    public void Dispose() => temp.Dispose();

    private protected AntigravityAcpSession NewSession() => new()
    {
        TranscriptPathResolver = null,
        UserProfileOverride = fakeHome,
        RestartCloseGrace = TimeSpan.FromMilliseconds(500),
        RestartTreeExitTimeout = TimeSpan.FromSeconds(2),
    };

    private protected AgentSessionSettings Settings(
        string scenario = "default",
        string? sessionId = null,
        bool started = false,
        string? model = null,
        string? effort = null,
        AgentPermissionMode mode = AgentPermissionMode.Ask,
        string? workingDirectory = null,
        CliBinaryIdentity? cli = null,
        bool useLocalCustomizations = true,
        string? agent = null,
        long? autoCompactTokens = null,
        string? title = null,
        string? version = null)
        => new(
            sessionId,
            started,
            new AgentLauncher(PowerShell, new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }),
            "fake-acp",
            e =>
            {
                e["FAKE_ACP_SCENARIO"] = scenario;
                if (version != null)
                    e["FAKE_ACP_VERSION"] = version;
            },
            null,
            model,
            effort,
            mode,
            workingDirectory ?? work,
            temp.Root,
            "ACP 測試指示",
            UseLocalCustomizations: useLocalCustomizations,
            HistoryRecap: "（歷史節錄）",
            Cli: cli,
            Title: title,
            Agent: agent,
            AutoCompactTokens: autoCompactTokens);

    private protected sealed class EventLog : IDisposable
    {
        private readonly IAgentSession session;
        private readonly BlockingCollection<AgentEvent> queue = new();
        private readonly Action<AgentEvent> handler;

        public EventLog(IAgentSession session)
        {
            this.session = session;
            handler = queue.Add;
            session.EventReceived += handler;
        }

        public async Task<List<AgentEvent>> UntilAsync(Func<AgentEvent, bool> until, double seconds = 30)
        {
            var collected = new List<AgentEvent>();
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                if (!queue.TryTake(out var ev, 100))
                {
                    await Task.Yield();
                    continue;
                }
                collected.Add(ev);
                if (until(ev))
                    return collected;
            }
            throw new TimeoutException("假 ACP agy 逾時未完成；已收到：" + string.Join("、", collected.Select(e => e.GetType().Name)));
        }

        public void Dispose() => session.EventReceived -= handler;
    }

    private protected static async Task<List<AgentEvent>> TurnAsync(IAgentSession session, EventLog log, AgentSessionSettings settings, string text, IReadOnlyList<AgentAttachment>? attachments = null)
    {
        await session.SendAsync(new AgentTurnInput(text, attachments ?? Array.Empty<AgentAttachment>()), settings);
        return await log.UntilAsync(e => e is AgentTurnCompletedEvent);
    }

    private protected static JObject Reply(IEnumerable<AgentEvent> events)
        => JObject.Parse(events.OfType<AgentTextCompletedEvent>().Last().Text);
}

public sealed class AntigravityAcpSessionFakeCliTests : AntigravityAcpSessionFakeCliTestsBase
{
    [Fact]
    public async Task 新對話_內嵌圖片_待辦清單_上下文視窗_換模型與權限免重開程序_標註檔同步標題()
    {
        using var session = NewSession();
        using var log = new EventLog(session);

        var imgPath = temp.WriteFile("shot.png", "png-bytes");
        var att = new AgentAttachment
        {
            Kind = AgentAttachmentKind.Image,
            FileName = "shot.png",
            StoredPath = imgPath,
            ModelImagePaths = { imgPath },
        };

        var first = await TurnAsync(
            session,
            log,
            Settings(model: "gemini-3.8-flash", effort: "low", mode: AgentPermissionMode.Ask, autoCompactTokens: 750_000, title: "首輪標題"),
            "檢查圖片",
            new[] { att });

        var pid = session.CurrentProcessId;
        pid.Should().NotBeNull();
        var started = first.OfType<AgentSessionStartedEvent>().Single();
        var r1 = Reply(first);
        ((string)r1["sid"]!).Should().Be(started.SessionId);
        ((int)r1["images"]!).Should().Be(1, "ACP 支援直接內嵌 Base64 圖片");
        ((string)r1["model"]!).Should().Be("gemini-3.8-flash");
        ((string)r1["effort"]!).Should().Be("low");
        ((string)r1["mode"]!).Should().Be("default");
        ((string)r1["autoCompact"]!).Should().Be("750000");
        ((string)r1["text"]!).Should().StartWith("<myriagent_instructions>\nACP 測試指示\n</myriagent_instructions>");

        first.OfType<AgentPlanEvent>().Single().Steps.Select(s => (s.Text, s.Status)).Should().Equal(
            ("Step 1", "completed"), ("Step 2", "inProgress"));
        first.OfType<AgentContextUsageEvent>().Should().Contain(c => c.UsedTokens == 14200 && c.WindowTokens == 1048576);
        first.OfType<AgentTurnCompletedEvent>().Single().Usage.Should().Be(new AgentUsage(150, 25, 50, null));

        var pbtxtPath = Path.Combine(fakeHome, ".gemini", "antigravity-cli", "annotations", started.SessionId + ".pbtxt");
        File.ReadAllText(pbtxtPath).Should().Be("title:\"首輪標題\"\n");

        await session.RenameAsync("改名後的標題");
        File.ReadAllText(pbtxtPath).Should().Be("title:\"改名後的標題\"\n");

        // 第二輪切換成 Orchestrator 模型與自動模式：同一個 ACP 程序內即時切換，不殺程序、不帶無效 effort
        var second = await TurnAsync(
            session,
            log,
            Settings(model: "gemini-pro-orchestrator", effort: "high", mode: AgentPermissionMode.Auto, autoCompactTokens: 750_000),
            "第二輪");
        session.CurrentProcessId.Should().Be(pid, "換模型與權限模式不必重開 190 MB 的 agy 程序");
        var r2 = Reply(second);
        ((int)r2["turn"]!).Should().Be(2);
        ((string)r2["model"]!).Should().Be("gemini-pro-orchestrator");
        ((string)r2["mode"]!).Should().Be("auto");
        ((string)r2["text"]!).Should().Be("第二輪", "第二輪不再重複附背景說明");
    }

    [Fact]
    public async Task 互動權限核准_本對話都允許記憶同類操作_以及Elicitation結構化問卷作答與略過()
    {
        using var session = NewSession();
        using var log = new EventLog(session);

        // 1) 第一次指令核准選 AllowForSession（本對話都允許）
        await session.SendAsync(new AgentTurnInput("跑指令", Array.Empty<AgentAttachment>()), Settings("ask_perm", mode: AgentPermissionMode.Ask));
        var untilApproval = await log.UntilAsync(e => e is AgentApprovalEvent);
        var approval = untilApproval.OfType<AgentApprovalEvent>().Single();
        approval.ToolKind.Should().Be("bash");
        approval.Title.Should().Be("git status");
        session.IsAtSafePoint.Should().BeFalse("等待使用者核准期間不在安全點");

        await session.RespondApprovalAsync(approval.RequestKey, AgentApprovalChoice.AllowForSession, null);
        var done1 = await log.UntilAsync(e => e is AgentTurnCompletedEvent);
        done1.OfType<AgentTextCompletedEvent>().Last().Text.Should().Be("perm:allow");
        done1.OfType<AgentToolCompletedEvent>().Single().Status.Should().Be(AgentItemStatus.Done);

        // 2) 同一對話第二次同類 bash 核准：由會話自動放行，不再跳核准卡
        var done2 = await TurnAsync(session, log, Settings("ask_perm", mode: AgentPermissionMode.Ask), "再跑一次");
        done2.OfType<AgentApprovalEvent>().Should().BeEmpty("已選過本對話都允許");
        done2.OfType<AgentTextCompletedEvent>().Last().Text.Should().Be("perm:allow");

        // 3) Elicitation 結構化問卷作答與略過
        using var qSession = NewSession();
        using var qLog = new EventLog(qSession);
        await qSession.SendAsync(new AgentTurnInput("問我環境", Array.Empty<AgentAttachment>()), Settings("ask_q"));
        var untilQ = await qLog.UntilAsync(e => e is AgentQuestionEvent);
        var qEv = untilQ.OfType<AgentQuestionEvent>().Single();
        qEv.AnswerAsMessage.Should().BeFalse();
        qEv.Questions.Single().Options.Select(o => o.Label).Should().Equal("staging", "prod");

        await qSession.AnswerQuestionAsync(qEv.RequestKey, new Dictionary<string, IReadOnlyList<string>> { ["env"] = new[] { "prod" } });
        var qDone1 = await qLog.UntilAsync(e => e is AgentTurnCompletedEvent);
        qDone1.OfType<AgentTextCompletedEvent>().Last().Text.Should().Be("accept:prod");

        await qSession.SendAsync(new AgentTurnInput("再問一次", Array.Empty<AgentAttachment>()), Settings("ask_q"));
        var qEv2 = (await qLog.UntilAsync(e => e is AgentQuestionEvent)).OfType<AgentQuestionEvent>().Single();
        await qSession.DeclineQuestionAsync(qEv2.RequestKey);
        var qDone2 = await qLog.UntilAsync(e => e is AgentTurnCompletedEvent);
        qDone2.OfType<AgentTextCompletedEvent>().Last().Text.Should().Be("decline:");
    }
}

public sealed class AntigravityAcpSessionFakeCliTests2 : AntigravityAcpSessionFakeCliTestsBase
{
    [Theory]
    [InlineData("missing32002")]
    [InlineData("missing32603")]
    public async Task 續接對話不存在_支援負32002與負32603兩種錯誤_自動改開新對話並附歷史節錄(string scenario)
    {
        using var session = NewSession();
        using var log = new EventLog(session);

        var events = await TurnAsync(session, log, Settings(scenario, sessionId: "gone-id", started: true), "接著做");

        events.OfType<AgentNoticeEvent>().Should().ContainSingle(n => n.Message.Contains("改開新對話"));
        var r = Reply(events);
        ((string)r["sid"]!).Should().NotBe("gone-id");
        ((string)r["text"]!).Should().Contain("ACP 測試指示").And.Contain("（歷史節錄）").And.EndWith("接著做");
    }

    [Fact]
    public async Task 執行中即時切換模型與權限_以session_cancel平滑停止免殺程序_以及RestartAsync兩階段熱更新()
    {
        using var session = NewSession();
        using var log = new EventLog(session);

        var oldCli = CliIdentities.Antigravity("1.5.16", fileId: 1);
        await session.SendAsync(new AgentTurnInput("跑長任務", Array.Empty<AgentAttachment>()), Settings("cancel_turn", cli: oldCli));
        await log.UntilAsync(e => e is AgentToolStartedEvent);
        var pid1 = session.CurrentProcessId;
        session.IsAtSafePoint.Should().BeFalse();

        // 執行中即時切換模型與權限模式回 true
        (await session.SetModelAsync("gemini-3.8-flash", "low")).Should().BeTrue();
        (await session.SetPermissionModeAsync(AgentPermissionMode.AcceptEdits)).Should().BeTrue();

        // SafePoint 模式重開回 Deferred
        var deferred = await session.RestartAsync(new AgentRestartRequest(
            AgentRestartReason.CliUpdated, AgentRestartMode.SafePoint, "1.5.16", "1.5.17",
            AutoContinue: true, "接續", Array.Empty<AgentInterruptedTool>(), Settings("default")));
        deferred.Outcome.Should().Be(AgentRestartOutcome.Deferred);

        // InterruptAsync 送出 session/cancel 平滑結束回合，程序仍活著供第二輪沿用
        await session.InterruptAsync();
        var interrupted = await log.UntilAsync(e => e is AgentTurnCompletedEvent);
        interrupted.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Interrupted);
        interrupted.OfType<AgentToolCompletedEvent>().Single().Status.Should().Be(AgentItemStatus.Cancelled);
        session.HasLiveProcess.Should().BeTrue("session/cancel 不強殺 ACP 常駐程序");
        session.CurrentProcessId.Should().Be(pid1);

        // 第二輪直接沿用同一個程序，且剛才執行中切換的模型與權限已生效
        var second = await TurnAsync(session, log, Settings("cancel_turn", model: "gemini-3.8-flash", effort: "low", mode: AgentPermissionMode.AcceptEdits, cli: oldCli), "第二輪完成");
        var r2 = Reply(second);
        var sid = (string)r2["sid"]!;
        ((string)r2["model"]!).Should().Be("gemini-3.8-flash");
        ((string)r2["effort"]!).Should().Be("low");
        ((string)r2["mode"]!).Should().Be("accept-edits");

        // 驗證兩階段 RestartAsync（先 Stopped 排空，再以新版 1.5.17 啟動續接）
        using var session2 = NewSession();
        using var log2 = new EventLog(session2);
        await session2.SendAsync(new AgentTurnInput("被打斷的工作", Array.Empty<AgentAttachment>()), Settings("cancel_turn", sessionId: sid, started: true, cli: oldCli));
        await log2.UntilAsync(e => e is AgentToolStartedEvent);

        var stopped = await session2.RestartAsync(new AgentRestartRequest(
            AgentRestartReason.CliUpdated, AgentRestartMode.Immediate, "1.5.16", "1.5.17",
            AutoContinue: true, null, Array.Empty<AgentInterruptedTool>(), Settings: null));
        stopped.Outcome.Should().Be(AgentRestartOutcome.Stopped);
        session2.HasLiveProcess.Should().BeFalse();

        var newCli = CliIdentities.Antigravity("1.5.17", fileId: 2);
        var continued = await session2.RestartAsync(new AgentRestartRequest(
            AgentRestartReason.CliUpdated, AgentRestartMode.Immediate, "1.5.16", "1.5.17",
            AutoContinue: true, "請接著完成", Array.Empty<AgentInterruptedTool>(),
            Settings("default", sessionId: sid, started: true, cli: newCli, version: "1.5.17")));
        continued.Outcome.Should().Be(AgentRestartOutcome.RestartedAndContinued);

        var contEvents = await log2.UntilAsync(e => e is AgentTurnCompletedEvent);
        contEvents.OfType<AgentTurnStartedByCliEvent>().Should().Equal(new AgentTurnStartedByCliEvent(AgentAutoTurnKind.Continuation));
        var rCont = Reply(contEvents);
        ((string)rCont["sid"]!).Should().Be(sid);
        ((string)rCont["text"]!).Should().Be("請接著完成");
        session2.RunningVersion.Should().Be("1.5.17");
    }

    [Fact]
    public async Task 預付錢包耗盡發送AgentQuotaExhaustedEvent_本地斜線指令獨立執行不殺ACP程序()
    {
        using var session = NewSession();
        using var log = new EventLog(session);

        var normal = await TurnAsync(session, log, Settings("default"), "起頭");
        var pid = session.CurrentProcessId;
        pid.Should().NotBeNull();

        var slash = await TurnAsync(session, log, Settings("default"), "/thinking off");
        slash.OfType<AgentTextCompletedEvent>().Single().Text.Should().Be("single-prompt:/thinking off");
        session.CurrentProcessId.Should().Be(pid);

        var mcpSlash = await TurnAsync(session, log, Settings("default"), "/mcp");
        mcpSlash.OfType<AgentTextCompletedEvent>().Single().Text.Should().Be("mcp-subcommand:mcp list");
        session.CurrentProcessId.Should().Be(pid);

        using var quotaSession = NewSession();
        using var quotaLog = new EventLog(quotaSession);
        var quotaEvents = await TurnAsync(quotaSession, quotaLog, Settings("prepaid_quota"), "測試額度");
        quotaEvents.OfType<AgentQuotaExhaustedEvent>().Should().ContainSingle();
        quotaEvents.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Failed);
    }
}
