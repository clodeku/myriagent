using System.Collections.Concurrent;
using System.Text;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Agents;

/// <summary>
/// 以假的 grok agent（Windows PowerShell 腳本，照 grok 1.0.52 實測的 ACP 訊息格式）測 GrokAgentSession 的會話層行為，不連網、不耗額度：
/// 建立與續接對話、背景說明只在新對話的第一則、同一個程序即時切換模型／思考強度／權限模式、工具卡、核准卡與代為核准、提問卡、
/// 計畫確認、停止、沒登入、CLI 換版的熱重開。假 grok 把收到的請求寫進記錄檔（每行前面是第幾個程序 p1、p2…），測試據此比對訊息序列。
/// </summary>
public abstract class GrokSessionFakeCliTestsBase : IDisposable
{
    private protected static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
    private protected static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 假的 grok agent：腳本只用 ASCII（Windows PowerShell 讀沒有 BOM 的腳本用系統碼頁）。開頭由 Fake 補上 $scenario、$logPath、$counterPath、$work、$outside。
    /// 每一輪依 $scenario：basic＝思考、文字、一個指令工具（沒有 status 的更新帶說明、執行中、完成）；permission＝依訊息文字要求核准（exec＝指令、outside＝資料夾外的編輯、
    /// twice＝連續兩次編輯，其他＝資料夾內的編輯），拒絕時照實測以 stopReason cancelled＋PermissionRejected 結束；question＝提問；plan＝計畫確認，同意後送
    /// current_mode_update default 再要求一次編輯；hang＝指令執行中等 session/cancel；hangstuck＝不理 session/cancel；restart＝第一個程序同 hang、之後同 basic；
    /// notfound＝session/resume 回 FS_NOT_FOUND；auth＝session/new 回 Authentication required；slowconfig＝同 basic，session/set_config_option 記錄後過 1.5 秒才回應。
    /// images＝initialize 宣告 promptCapabilities.image；quota＝_x.ai/billing 回鎖定；compact＝記 compact、先送 auto_compact_completed 再以 end_turn 結束；
    /// autocompact＝一般的一輪，但結束前先送 auto_compact_completed（tokens_after 120）；background＝先送 task_backgrounded、等 work\bg-go.txt 再送 will_wake false 的 task_completed；
    /// background_idle＝開一個 ping 子程序（PID 寫進 work\bg-pid.txt）、把輸出寫進 work\bg.log 並在 task_backgrounded 帶 output_file，這一輪照常結束、背景工作一直不結束。
    /// rewind/points、rewind/execute、session/fork 都有回應（rewindfail 的 execute 失敗、forkfail 的 fork 回錯誤）。未鎖定的 billing 不寫記錄
    /// </summary>
    private protected const string FakeGrok = """"
        $ErrorActionPreference = 'Stop'
        $utf8 = New-Object System.Text.UTF8Encoding $false
        $out = New-Object System.IO.StreamWriter([Console]::OpenStandardOutput(), $utf8)
        $out.NewLine = [string][char]10
        $out.AutoFlush = $true
        $in = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $utf8)

        $n = 1
        if (Test-Path $counterPath) { $n = [int](Get-Content $counterPath) + 1 }
        Set-Content -Path $counterPath -Value $n
        $p = 'p' + $n
        $version = '1.0.52'
        if ($n -gt 1) { $version = '1.0.60' }
        $script:sid = $null
        $script:turn = 0
        $script:reqId = 0

        function Log([string]$text) { [IO.File]::AppendAllText($logPath, $p + ':' + $text + [char]10, $utf8) }
        function Send($obj) { $out.WriteLine((ConvertTo-Json -InputObject $obj -Depth 30 -Compress)) }
        function Result($id, $result) { Send ([ordered]@{ jsonrpc = '2.0'; id = $id; result = $result }) }
        function Fail($id, [int]$code, [string]$message, $data) {
            $e = [ordered]@{ code = $code; message = $message }
            if ($data) { $e.data = $data }
            Send ([ordered]@{ jsonrpc = '2.0'; id = $id; error = $e })
        }
        function Update($u) { Send ([ordered]@{ jsonrpc = '2.0'; method = 'session/update'; params = [ordered]@{ sessionId = $script:sid; update = $u } }) }
        function Chunk([string]$kind, [string]$text) { Update ([ordered]@{ sessionUpdate = $kind; content = [ordered]@{ type = 'text'; text = $text } }) }
        function Flat([string]$text) { if ($text -eq $null) { return '' }; return $text.Replace([string][char]10, '\n') }
        function Opt([string]$id, [string]$kind) { return [ordered]@{ optionId = $id; name = $id; kind = $kind } }
        function Tool([string]$name, [string]$kind) { return [ordered]@{ 'x.ai/tool' = [ordered]@{ version = 1; name = $name; kind = $kind } } }
        function TextContent([string]$text) { return ,@([ordered]@{ type = 'content'; content = [ordered]@{ type = 'text'; text = $text } }) }
        function BashUpdate([string]$tc, [string]$status, [string]$text, [int]$code) {
            return [ordered]@{ sessionUpdate = 'tool_call_update'; toolCallId = $tc; status = $status; content = (TextContent $text); rawOutput = [ordered]@{ type = 'Bash'; output_for_prompt = ('exit: ' + $code); exit_code = $code } }
        }
        function Models {
            return [ordered]@{ currentModelId = 'grok-fake'; availableModels = @(
                ([ordered]@{ modelId = 'grok-fake'; name = 'Grok Fake'; description = 'd1'; _meta = [ordered]@{ totalContextTokens = 256000; reasoningEfforts = @(([ordered]@{ value = 'high' }), ([ordered]@{ value = 'low' })) } }),
                ([ordered]@{ modelId = 'grok-fake-2'; name = 'Grok Fake 2'; description = 'd2'; _meta = [ordered]@{ totalContextTokens = 500000; reasoningEfforts = @(([ordered]@{ value = 'xhigh' }), ([ordered]@{ value = 'high' })) } })) }
        }
        function EndTurn($id, [string]$stop, $extra) {
            # measured on 1.0.67: the top-level token counts are the last model call (inputTokens includes cached reads), usage is the whole turn
            $meta = [ordered]@{ inputTokens = 38339; outputTokens = 82; usage = [ordered]@{ inputTokens = 100; outputTokens = 10; cachedReadTokens = 50; costUsdTicks = 12345678900 } }
            if ($extra) { foreach ($k in $extra.Keys) { $meta[$k] = $extra[$k] } }
            Result $id ([ordered]@{ stopReason = $stop; _meta = $meta })
        }

        # every message except blank lines; logs cancel and permission mode notifications; exits on EOF
        function ReadMsg {
            while ($true) {
                $line = $in.ReadLine()
                if ($line -eq $null) { Log 'eof'; exit 0 }
                if ($line.Trim().Length -eq 0) { continue }
                $m = $line | ConvertFrom-Json
                if ($m.method -eq 'session/cancel') { Log 'cancel' }
                if ($m.method -eq '_x.ai/yolo_mode_changed') { Log ('yolo ' + $m.params.permission_mode) }
                return $m
            }
        }
        # the response to our request $id; $null when session/cancel arrives first
        function WaitResponse($id) {
            while ($true) {
                $m = ReadMsg
                if ($m.method -eq $null -and $m.id -eq $id) { return $m }
                if ($m.method -eq 'session/cancel') { return $null }
                if ($m.method -ne $null -and $m.id -ne $null) { HandleRequest $m }
            }
        }
        function NextId { $r = $script:reqId; $script:reqId = $script:reqId + 1; return $r }

        function Basic($id) {
            Chunk 'agent_thought_chunk' 'thinking'
            Chunk 'agent_message_chunk' 'working'
            $tc = 'call-b' + $script:turn
            Update ([ordered]@{ sessionUpdate = 'tool_call'; toolCallId = $tc; title = 'run_terminal_command'; rawInput = [ordered]@{ command = 'echo hello'; description = 'Say hello' }; _meta = (Tool 'run_terminal_command' 'execute') })
            Update ([ordered]@{ sessionUpdate = 'tool_call_update'; toolCallId = $tc; kind = 'execute'; title = 'Execute `echo hello`'; content = (TextContent 'Say hello'); locations = @(); rawInput = [ordered]@{ variant = 'Bash'; command = 'echo hello'; description = 'Say hello' }; _meta = (Tool 'run_terminal_command' 'execute') })
            Update (BashUpdate $tc 'in_progress' '' 0)
            Update (BashUpdate $tc 'in_progress' 'hello' 0)
            Update (BashUpdate $tc 'completed' ('hello' + [char]13 + [char]10 + 'world' + [char]13 + [char]10) 0)
            Chunk 'agent_message_chunk' ('done ' + $p + ' ' + $script:turn)
            # one model call finished (measured on 1.0.67: input_tokens excludes cached reads)
            Send ([ordered]@{ jsonrpc = '2.0'; method = '_x.ai/session_notification'; params = [ordered]@{ sessionId = $script:sid; update = [ordered]@{ sessionUpdate = 'response_completed'; usage = [ordered]@{ input_tokens = (36675 + $script:turn); output_tokens = 82; cache_read_input_tokens = 1664; cache_creation_input_tokens = 0; reasoning_tokens = 81 } } } })
            EndTurn $id 'end_turn' $null
        }

        # returns the selected optionId, 'cancelled', or 'cancel' (session/cancel while waiting)
        function AskPermission([string]$kind, [string]$tc) {
            if ($kind -eq 'exec') {
                $call = [ordered]@{ toolCallId = $tc; kind = 'execute'; title = 'Execute `npm test`'; rawInput = [ordered]@{ variant = 'Bash'; command = 'npm test'; description = 'Run tests' }; _meta = (Tool 'run_terminal_command' 'execute') }
                $options = @((Opt 'always-allow' 'allow_always'), (Opt 'allow-once' 'allow_once'), (Opt 'reject-once' 'reject_once'), (Opt 'reject-always' 'reject_always'))
            } else {
                $path = Join-Path $work ($tc + '.txt')
                if ($kind -eq 'outside') { $path = $outside }
                $call = [ordered]@{ toolCallId = $tc; kind = 'edit'; title = ('Write `' + $path + '`'); content = @([ordered]@{ type = 'diff'; path = $path; oldText = ''; newText = 'x' }); locations = @([ordered]@{ path = $path }); rawInput = [ordered]@{ variant = 'Write'; file_path = $path; content = 'x' }; _meta = (Tool 'write' 'write') }
                $options = @((Opt 'allow-edits-session' 'allow_always'), (Opt 'allow-once' 'allow_once'), (Opt 'reject-once' 'reject_once'))
            }
            Update ([ordered]@{ sessionUpdate = 'tool_call'; toolCallId = $tc; title = $call._meta.'x.ai/tool'.name; rawInput = $call.rawInput; _meta = $call._meta })
            $u = [ordered]@{ sessionUpdate = 'tool_call_update' }
            foreach ($k in $call.Keys) { $u[$k] = $call[$k] }
            Update $u
            $rid = NextId
            Send ([ordered]@{ jsonrpc = '2.0'; id = $rid; method = 'session/request_permission'; params = [ordered]@{ sessionId = $script:sid; toolCall = $call; options = $options } })
            $resp = WaitResponse $rid
            if ($resp -eq $null) { Log ('permission ' + $tc + ' -> cancel'); return 'cancel' }
            $outcome = $resp.result.outcome
            $answer = $outcome.outcome
            if ($answer -eq 'selected') { $answer = $outcome.optionId }
            Log ('permission ' + $tc + ' ' + $kind + ' -> ' + $answer)
            if ($answer -eq 'reject-once') {
                Update ([ordered]@{ sessionUpdate = 'tool_call_update'; toolCallId = $tc; status = 'failed'; content = (TextContent ('User rejected the execution for tool `' + $call._meta.'x.ai/tool'.name + '`')); _meta = [ordered]@{ toolNotRun = 'rejected' } })
            } elseif ($answer -like 'allow*' -or $answer -eq 'always-allow') {
                if ($kind -eq 'exec') { Update (BashUpdate $tc 'completed' 'ok' 0) }
                else { Update ([ordered]@{ sessionUpdate = 'tool_call_update'; toolCallId = $tc; status = 'completed'; content = $call.content; rawOutput = [ordered]@{ type = 'SearchReplace' } }) }
            }
            return $answer
        }

        function Permission($id, [string]$text) {
            $kinds = @('edit')
            if ($text -like '*exec*') { $kinds = @('exec') }
            elseif ($text -like '*outside*') { $kinds = @('outside') }
            elseif ($text -like '*twice*') { $kinds = @('edit', 'edit') }
            $i = 0
            foreach ($kind in $kinds) {
                $i++
                $answer = AskPermission $kind ('call-' + $script:turn + '-' + $i)
                if ($answer -eq 'reject-once') { EndTurn $id 'cancelled' ([ordered]@{ cancellationCategory = 'PermissionRejected'; cancellationContext = [ordered]@{ tool_name = 'write'; reason = 'User rejected the execution' } }); return }
                if ($answer -eq 'cancel' -or $answer -eq 'cancelled') { EndTurn $id 'cancelled' $null; return }
            }
            Chunk 'agent_message_chunk' 'edited'
            EndTurn $id 'end_turn' $null
        }

        function Question($id) {
            $tc = 'call-q' + $script:turn
            Update ([ordered]@{ sessionUpdate = 'tool_call'; toolCallId = $tc; title = 'ask_user_question'; rawInput = @{}; _meta = (Tool 'ask_user_question' 'ask_user') })
            Update ([ordered]@{ sessionUpdate = 'tool_call_update'; toolCallId = $tc; kind = 'other'; title = 'Ask: Color?'; _meta = (Tool 'ask_user_question' 'ask_user') })
            $rid = NextId
            $q = [ordered]@{ question = 'Color?'; options = @(([ordered]@{ label = 'Red'; description = 'r' }), ([ordered]@{ label = 'Blue'; description = 'b' })); multiSelect = $null }
            Send ([ordered]@{ jsonrpc = '2.0'; id = $rid; method = '_x.ai/ask_user_question'; params = [ordered]@{ sessionId = $script:sid; toolCallId = $tc; questions = @($q); mode = 'default' } })
            $resp = WaitResponse $rid
            if ($resp -eq $null) { EndTurn $id 'cancelled' $null; return }
            Log ('question -> ' + (ConvertTo-Json -InputObject $resp.result -Depth 10 -Compress))
            Update ([ordered]@{ sessionUpdate = 'tool_call_update'; toolCallId = $tc; status = 'completed'; content = (TextContent 'User has answered your questions') })
            Chunk 'agent_message_chunk' 'answered'
            EndTurn $id 'end_turn' $null
        }

        function Plan($id) {
            $tc = 'call-p' + $script:turn
            Update ([ordered]@{ sessionUpdate = 'tool_call'; toolCallId = $tc; title = 'exit_plan_mode'; rawInput = @{}; _meta = (Tool 'exit_plan_mode' 'exit_plan') })
            Update ([ordered]@{ sessionUpdate = 'tool_call_update'; toolCallId = $tc; kind = 'other'; title = 'Plan: Exit'; _meta = (Tool 'exit_plan_mode' 'exit_plan') })
            $rid = NextId
            Send ([ordered]@{ jsonrpc = '2.0'; id = $rid; method = '_x.ai/exit_plan_mode'; params = [ordered]@{ sessionId = $script:sid; toolCallId = $tc; planContent = ('# Plan' + [char]10 + '1. write a file') } })
            $resp = WaitResponse $rid
            if ($resp -eq $null) { EndTurn $id 'cancelled' $null; return }
            Log ('plan -> ' + (ConvertTo-Json -InputObject $resp.result -Depth 10 -Compress))
            if ($resp.result.outcome -ne 'approved') { EndTurn $id 'end_turn' $null; return }
            Update ([ordered]@{ sessionUpdate = 'current_mode_update'; currentModeId = 'default' })
            Update ([ordered]@{ sessionUpdate = 'tool_call_update'; toolCallId = $tc; status = 'completed'; title = 'Plan mode exited'; content = (TextContent 'Plan file: plan.md') })
            $answer = AskPermission 'edit' ('call-' + $script:turn + '-after-plan')
            EndTurn $id 'end_turn' $null
        }

        function Hang($id) {
            $tc = 'call-h' + $script:turn
            Update ([ordered]@{ sessionUpdate = 'tool_call'; toolCallId = $tc; title = 'run_terminal_command'; rawInput = [ordered]@{ command = 'sleep 60' }; _meta = (Tool 'run_terminal_command' 'execute') })
            Update ([ordered]@{ sessionUpdate = 'tool_call_update'; toolCallId = $tc; kind = 'execute'; title = 'Execute `sleep 60`'; content = (TextContent 'Wait'); rawInput = [ordered]@{ variant = 'Bash'; command = 'sleep 60' } })
            Update (BashUpdate $tc 'in_progress' 'partial' 0)
            Log 'tool-running'
            while ($true) {
                $m = ReadMsg
                if ($m.method -eq 'session/cancel') { break }
            }
            if ($scenario -eq 'hangstuck') { while ($true) { $null = ReadMsg } }
            EndTurn $id 'cancelled' $null
        }

        # client requests other than session/prompt (also answered while waiting for our own requests)
        function HandleRequest($m) {
            if ($m.method -eq 'initialize') {
                Log 'initialize'
                $caps = [ordered]@{ loadSession = $true }
                if ($scenario -eq 'images') { $caps.promptCapabilities = [ordered]@{ image = $true } }
                Result $m.id ([ordered]@{ protocolVersion = 1; agentCapabilities = $caps; authMethods = @(); _meta = [ordered]@{ agentVersion = $version } })
            } elseif ($m.method -eq 'session/new') {
                $meta = $m.params._meta
                Log ('session/new sid=' + $meta.sessionId + ' model=' + $meta.modelId + ' yolo=' + $meta.yoloMode + ' auto=' + $meta.autoMode + ' ask=' + $meta.askUserQuestion + ' cwdOk=' + ($m.params.cwd -eq $work))
                if ($scenario -eq 'auth') { Fail $m.id -32000 'Authentication required' $null }
                else {
                    $script:sid = $meta.sessionId
                    Result $m.id ([ordered]@{ sessionId = $script:sid; models = (Models) })
                }
            } elseif ($m.method -eq 'session/resume') {
                Log ('session/resume sid=' + $m.params.sessionId + ' yolo=' + $m.params._meta.yoloMode + ' cwdOk=' + ($m.params.cwd -eq $work))
                if ($scenario -eq 'notfound') { Fail $m.id -32603 'Path not found' ([ordered]@{ code = 'FS_NOT_FOUND' }) }
                else { $script:sid = $m.params.sessionId; Result $m.id @{} }
            } elseif ($m.method -eq 'session/set_config_option') {
                Log ('set_config ' + $m.params.configId + '=' + $m.params.value)
                if ($scenario -eq 'slowconfig') { Start-Sleep -Milliseconds 1500 }
                Result $m.id @{}
            } elseif ($m.method -eq 'session/set_mode') {
                Log ('set_mode ' + $m.params.modeId)
                Result $m.id @{}
            } elseif ($m.method -eq '_x.ai/billing') {
                if ($scenario -eq 'quota') {
                    Log 'billing locked'
                    Result $m.id ([ordered]@{ config = [ordered]@{ creditUsagePercent = 100; currentPeriod = [ordered]@{ type = 'USAGE_PERIOD_TYPE_WEEKLY'; end = '2026-10-31T00:00:00Z' }; billingPeriodEnd = '2026-10-31T00:00:00Z'; onDemandCap = [ordered]@{ val = 0 }; onDemandUsed = [ordered]@{ val = 0 }; prepaidBalance = [ordered]@{ val = 0 } } })
                } else {
                    Result $m.id ([ordered]@{ config = [ordered]@{ creditUsagePercent = 1; currentPeriod = [ordered]@{ type = 'USAGE_PERIOD_TYPE_WEEKLY'; end = '2026-11-07T00:00:00Z' }; billingPeriodEnd = '2026-11-07T00:00:00Z'; onDemandCap = [ordered]@{ val = 0 }; onDemandUsed = [ordered]@{ val = 0 }; prepaidBalance = [ordered]@{ val = 0 } } })
                }
            } elseif ($m.method -eq '_x.ai/compact_conversation') {
                Log ('compact sid=' + $m.params.sessionId)
                Send ([ordered]@{ jsonrpc = '2.0'; method = '_x.ai/session_notification'; params = [ordered]@{ sessionId = $script:sid; update = [ordered]@{ sessionUpdate = 'auto_compact_completed'; tokens_before = 100; tokens_after = 120; summary_preview = $null } } })
                Result $m.id ([ordered]@{ stopReason = 'end_turn' })
            } elseif ($m.method -eq '_x.ai/rewind/points') {
                Log ('rewind/points sid=' + $m.params.sessionId)
                Result $m.id ([ordered]@{ rewind_points = @(([ordered]@{ prompt_index = 0; prompt_preview = 'keep me...' })) })
            } elseif ($m.method -eq '_x.ai/rewind/execute') {
                Log ('rewind/execute sid=' + $m.params.sessionId + ' index=' + $m.params.targetPromptIndex + ' mode=' + $m.params.mode + ' force=' + $m.params.force)
                if ($scenario -eq 'rewindfail' -or $m.params.targetPromptIndex -eq 99) {
                    Result $m.id ([ordered]@{ success = $false })
                } else {
                    Result $m.id ([ordered]@{ success = $true; prompt_text = 'kept' })
                }
            } elseif ($m.method -eq '_x.ai/session/fork') {
                Log ('fork new=' + $m.params.newSessionId + ' source=' + $m.params.sourceSessionId + ' sid=' + $m.params.sessionId)
                if ($scenario -eq 'forkfail') { Fail $m.id -32603 'fork failed' $null }
                else { Result $m.id ([ordered]@{ newSessionId = $m.params.newSessionId; chatMessagesCopied = 2; updatesCopied = 4 }) }
            } else {
                Log ('unknown ' + $m.method)
                Fail $m.id -32601 'Method not found' $null
            }
        }

        Log ('start version=' + $version)
        while ($true) {
            $m = ReadMsg
            if ($m.method -eq $null -or $m.id -eq $null) { continue }
            if ($m.method -eq 'session/prompt') {
                $script:turn = $script:turn + 1
                $text = $m.params.prompt[0].text
                $imageCount = @($m.params.prompt | Where-Object { $_.type -eq 'image' }).Count
                $logged = 'prompt text=' + (Flat $text)
                if ($imageCount -gt 0) { $logged = $logged + ' images=' + $imageCount }
                Log $logged
                if ($scenario -eq 'permission') { Permission $m.id $text }
                elseif ($scenario -eq 'question') { Question $m.id }
                elseif ($scenario -eq 'plan') { Plan $m.id }
                elseif ($scenario -eq 'hang' -or $scenario -eq 'hangstuck' -or ($scenario -eq 'restart' -and $n -eq 1)) { Hang $m.id }
                elseif ($scenario -eq 'background_idle') {
                    $child = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\PING.EXE') -ArgumentList '-n', '120', '127.0.0.1' -WindowStyle Hidden -PassThru
                    [IO.File]::WriteAllText((Join-Path $work 'bg-pid.txt'), [string]$child.Id)
                    $bgLog = Join-Path $work 'bg.log'
                    [IO.File]::WriteAllText($bgLog, ('hello' + [char]13 + [char]10 + 'bg'), $utf8)
                    Update ([ordered]@{ sessionUpdate = 'task_backgrounded'; task_id = 'bg1'; tool_call_id = 'call-bg'; monitor_description = 'ping -n 120'; output_file = $bgLog })
                    Log 'backgrounded'
                    Basic $m.id
                }
                elseif ($scenario -eq 'autocompact') {
                    Send ([ordered]@{ jsonrpc = '2.0'; method = '_x.ai/session_notification'; params = [ordered]@{ sessionId = $script:sid; update = [ordered]@{ sessionUpdate = 'auto_compact_completed'; tokens_before = 100; tokens_after = 120; summary_preview = $null } } })
                    Basic $m.id
                }
                elseif ($scenario -eq 'background') {
                    Update ([ordered]@{ sessionUpdate = 'task_backgrounded'; task_id = 'bg1'; tool_call_id = 'call-bg'; monitor_description = 'sleep 30' })
                    Log 'backgrounded'
                    while (-not (Test-Path (Join-Path $work 'bg-go.txt'))) { Start-Sleep -Milliseconds 50 }
                    Update ([ordered]@{ sessionUpdate = 'task_completed'; task_id = 'bg1'; tool_call_id = 'call-bg'; will_wake = $false })
                    Basic $m.id
                }
                else { Basic $m.id }
            } else {
                HandleRequest $m
            }
        }
        """";

    private protected readonly TempDirectory temp = new("fake-grok-session");

    public void Dispose() => temp.Dispose();

    /// <summary>一個情境的假 grok：自己的工作資料夾、記錄檔與程序計數</summary>
    private protected sealed class Fake
    {
        public Fake(TempDirectory temp, string scenario)
        {
            var root = temp.CreateDirectory("grok-" + scenario + "-" + Guid.NewGuid().ToString("N")[..6]);
            Work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
            Conversation = Directory.CreateDirectory(Path.Combine(root, "conv")).FullName;
            Outside = Path.Combine(root, "outside", "b.txt");
            LogPath = Path.Combine(root, "log.txt");
            static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
            var header = new StringBuilder()
                .Append("$scenario = ").Append(Quote(scenario)).Append('\n')
                .Append("$logPath = ").Append(Quote(LogPath)).Append('\n')
                .Append("$counterPath = ").Append(Quote(Path.Combine(root, "count.txt"))).Append('\n')
                .Append("$work = ").Append(Quote(Work)).Append('\n')
                .Append("$outside = ").Append(Quote(Outside)).Append('\n');
            Script = Path.Combine(root, "fake-grok.ps1");
            File.WriteAllText(Script, header + FakeGrok);
        }

        public string Script { get; }

        public string Work { get; }

        public string Conversation { get; }

        public string Outside { get; }

        public string LogPath { get; }

        public AgentSessionSettings Settings(string? sessionId = "s-pre", bool started = false, string? model = null, string? effort = null,
            AgentPermissionMode mode = AgentPermissionMode.Ask, CliBinaryIdentity? cli = null)
            => new(
                sessionId,
                started,
                new AgentLauncher(PowerShell, new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script }),
                "fake",
                _ => { },
                null,
                model,
                effort,
                mode,
                Work,
                Conversation,
                "測試說明",
                UseLocalCustomizations: true,
                HistoryRecap: "（前文節錄）",
                Cli: cli);

        /// <summary>假 grok 執行中也會讀（WaitForLogAsync）：以共享讀寫開檔，不和正在附加的寫入互斥</summary>
        public string[] Log()
        {
            if (!File.Exists(LogPath))
                return Array.Empty<string>();
            using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        }

        public async Task WaitForLogAsync(string prefix)
        {
            var deadline = DateTime.UtcNow + Deadline;
            while (DateTime.UtcNow < deadline)
            {
                if (Log().Any(l => l.StartsWith(prefix, StringComparison.Ordinal)))
                    return;
                await Task.Delay(50);
            }
            throw new TimeoutException($"假 grok 的記錄沒有出現「{prefix}」；目前：{string.Join(" | ", Log())}");
        }
    }

    private protected static AgentTurnInput Input(string text) => new(text, Array.Empty<AgentAttachment>());

    private protected static BlockingCollection<AgentEvent> Collect(IAgentSession session)
    {
        var events = new BlockingCollection<AgentEvent>();
        session.EventReceived += events.Add;
        return events;
    }

    /// <summary>收事件直到 until 成立（含那一個事件）；onEvent 可以回應卡片</summary>
    private protected static async Task<List<AgentEvent>> TakeUntilAsync(BlockingCollection<AgentEvent> events, Func<AgentEvent, bool> until, Func<AgentEvent, Task>? onEvent = null)
    {
        var collected = new List<AgentEvent>();
        var deadline = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < deadline)
        {
            if (!events.TryTake(out var ev, TimeSpan.FromMilliseconds(100)))
                continue;
            collected.Add(ev);
            if (onEvent != null)
                await onEvent(ev);
            if (until(ev))
                return collected;
        }
        throw new TimeoutException("假的 grok 沒有在時限內送出預期的事件；收到：" + string.Join("、", collected.Select(e => e.GetType().Name)));
    }

    private protected static async Task<List<AgentEvent>> TurnAsync(IAgentSession session, BlockingCollection<AgentEvent> events, AgentSessionSettings settings, string text,
        Func<AgentEvent, Task>? onEvent = null)
    {
        await session.SendAsync(Input(text), settings);
        return await TakeUntilAsync(events, e => e is AgentTurnCompletedEvent, onEvent);
    }

    private protected static int IndexOf<T>(List<AgentEvent> events, Func<T, bool>? match = null) where T : AgentEvent
        => events.FindIndex(e => e is T t && (match == null || match(t)));

    private protected static AgentRestartRequest Restart(AgentRestartMode mode, AgentSessionSettings? settings, string? continuation = null)
        => new(AgentRestartReason.CliUpdated, mode, "1.0.52", "1.0.60", AutoContinue: true, continuation, Array.Empty<AgentInterruptedTool>(), settings);
}

// 測試分成幾個 class：xUnit 同一個 class 的測試依序跑，分開才能同時跑
public sealed class GrokSessionFakeCliTests : GrokSessionFakeCliTestsBase
{
    [Fact]
    public async Task 新對話_預先產生的id與權限在建立時帶上_背景說明只在第一則_同一個程序跑第二輪()
    {
        var fake = new Fake(temp, "basic");
        using var session = new GrokAgentSession();
        var events = Collect(session);

        var first = await TurnAsync(session, events, fake.Settings(effort: "low", cli: CliIdentities.Grok("1.0.52")), "你好");

        first[0].Should().BeOfType<AgentProcessStartedEvent>("程序的任何事件之前先發 AgentProcessStartedEvent");
        var started = first.OfType<AgentSessionStartedEvent>().Single();
        started.Should().Be(new AgentSessionStartedEvent("s-pre", "grok-fake", "new"));
        first.OfType<AgentRunningVersionEvent>().Single().Version.Should().Be("1.0.52");
        first.OfType<AgentModelsEvent>().Single().Models.Select(m => m.Value).Should().Equal("default", "grok-fake", "grok-fake-2");
        first.OfType<AgentThinkingDeltaEvent>().Single().Delta.Should().Be("thinking");
        first.OfType<AgentTextCompletedEvent>().Select(t => t.Text).Should().Equal("working", "done p1 1");

        var tool = first.OfType<AgentToolStartedEvent>().Single();
        tool.ToolKind.Should().Be("bash");
        tool.Title.Should().Be("echo hello");
        first.OfType<AgentToolOutputEvent>().Select(o => o.Delta).Should().Equal(new[] { "hello" }, "沒有 status 的第一個更新放的是工具說明（Say hello），不是輸出");
        first.OfType<AgentToolCompletedEvent>().Single().Should().Match<AgentToolCompletedEvent>(e => e.ToolId == tool.ToolId && e.Status == AgentItemStatus.Done && e.Output == "hello\nworld\n");
        IndexOf<AgentTextCompletedEvent>(first, t => t.Text == "working").Should().BeLessThan(IndexOf<AgentToolStartedEvent>(first), "工具開始前先結束前面的文字");
        first.OfType<AgentTurnCompletedEvent>().Single().Should().Be(new AgentTurnCompletedEvent(AgentTurnOutcome.Completed, null, new AgentUsage(100, 10, 50, 1.234568)));

        var processId = session.CurrentProcessId;
        var second = await TurnAsync(session, events, fake.Settings(effort: "low"), "再一次");
        session.CurrentProcessId.Should().Be(processId, "同一個程序");
        second.OfType<AgentSessionStartedEvent>().Should().BeEmpty();
        second.OfType<AgentTextCompletedEvent>().Last().Text.Should().Be("done p1 2");

        fake.Log().Should().Equal(
            "p1:start version=1.0.52",
            "p1:initialize",
            "p1:session/new sid=s-pre model= yolo=False auto=False ask=True cwdOk=True",
            "p1:set_config reasoning_effort=low",
            @"p1:prompt text=<myriagent_instructions>\n測試說明\n</myriagent_instructions>\n\n你好",
            "p1:prompt text=再一次");
    }

    [Fact]
    public async Task 上下文用量_模型清單的視窗_每次呼叫與一輪結束發用量_換模型改用新模型的視窗()
    {
        var fake = new Fake(temp, "basic");
        using var session = new GrokAgentSession();
        var events = Collect(session);

        var first = await TurnAsync(session, events, fake.Settings(), "第一則");
        first.OfType<AgentContextUsageEvent>().Should().Equal(
            new AgentContextUsageEvent(null, 256_000),
            new AgentContextUsageEvent(36_675 + 1 + 1_664 + 82, null),
            new AgentContextUsageEvent(38_339 + 82, null));
        IndexOf<AgentContextUsageEvent>(first, e => e.WindowTokens != null).Should().BeLessThan(IndexOf<AgentSessionStartedEvent>(first), "建立對話的回應就有模型清單");
        IndexOf<AgentContextUsageEvent>(first, e => e.UsedTokens == 38_421).Should().BeLessThan(IndexOf<AgentTurnCompletedEvent>(first), "最後一次呼叫的用量在這一輪結束之前");

        var second = await TurnAsync(session, events, fake.Settings(model: "grok-fake-2"), "第二則");
        second.OfType<AgentContextUsageEvent>().First().Should().Be(new AgentContextUsageEvent(null, 500_000), "切換成功後用新模型的視窗");
    }

    [Fact]
    public async Task 換模型思考強度與權限模式_同一個程序即時切換_規劃模式另外切()
    {
        var fake = new Fake(temp, "basic");
        using var session = new GrokAgentSession();
        var events = Collect(session);
        await TurnAsync(session, events, fake.Settings(), "第一則");
        var processId = session.CurrentProcessId;

        var second = await TurnAsync(session, events, fake.Settings(model: "grok-fake-2", effort: "xhigh", mode: AgentPermissionMode.FullAccess), "第二則");
        await TurnAsync(session, events, fake.Settings(model: "grok-fake-2", effort: "xhigh", mode: AgentPermissionMode.Plan), "第三則");
        await TurnAsync(session, events, fake.Settings(model: "default", effort: "low", mode: AgentPermissionMode.Auto), "第四則");

        session.CurrentProcessId.Should().Be(processId, "模型、思考強度、權限模式都在同一個程序裡切換");
        second.OfType<AgentNoticeEvent>().Should().BeEmpty();
        fake.Log().Should().ContainInOrder(
            @"p1:prompt text=<myriagent_instructions>\n測試說明\n</myriagent_instructions>\n\n第一則",
            "p1:set_config model=grok-fake-2", "p1:set_config reasoning_effort=xhigh", "p1:yolo always-approve", "p1:prompt text=第二則",
            "p1:set_mode plan", "p1:yolo ask", "p1:prompt text=第三則",
            "p1:set_config model=grok-fake", "p1:set_config reasoning_effort=low", "p1:set_mode default", "p1:yolo auto", "p1:prompt text=第四則");
        fake.Log().Count(l => l.Contains("session/new")).Should().Be(1);
    }

    [Fact]
    public async Task 一輪進行中換模型與思考強度_立即送set_config_option_下一輪照同樣的設定不重送()
    {
        var fake = new Fake(temp, "permission");
        using var session = new GrokAgentSession();
        var events = Collect(session);

        (await session.SetModelAsync("grok-fake-2", "xhigh")).Should().BeFalse("程序還沒啟動：下一則訊息照設定套用");
        var switched = false;
        await TurnAsync(session, events, fake.Settings(), "寫檔", async ev =>
        {
            if (ev is not AgentApprovalEvent approval)
                return;
            // 這一輪停在等核准（prompt 已送出）：實測 grok 立即回應，從下一次模型呼叫起改用
            switched = await session.SetModelAsync("grok-fake-2", "xhigh");
            await session.RespondApprovalAsync(approval.RequestKey, AgentApprovalChoice.Allow, null);
        });
        switched.Should().BeTrue();
        var processId = session.CurrentProcessId;

        await TurnAsync(session, events, fake.Settings(model: "grok-fake-2", effort: "xhigh"), "再寫一次", async ev =>
        {
            if (ev is AgentApprovalEvent approval)
                await session.RespondApprovalAsync(approval.RequestKey, AgentApprovalChoice.Allow, null);
        });

        session.CurrentProcessId.Should().Be(processId);
        var log = fake.Log().ToList();
        log.IndexOf("p1:set_config model=grok-fake-2").Should().BeGreaterThan(log.FindIndex(l => l.StartsWith("p1:prompt", StringComparison.Ordinal)), "在這一輪的 prompt 之後送");
        log.IndexOf("p1:set_config reasoning_effort=xhigh").Should().BeGreaterThan(log.IndexOf("p1:set_config model=grok-fake-2"));
        log.IndexOf("p1:permission call-1-1 edit -> allow-once").Should().BeGreaterThan(log.IndexOf("p1:set_config reasoning_effort=xhigh"), "核准之前就已切換");
        log.Count(l => l.StartsWith("p1:set_config", StringComparison.Ordinal)).Should().Be(2, "下一輪的設定和已套用的相同，不重送");
        (await session.SetModelAsync("grok-fake", null)).Should().BeFalse("沒有在執行的一輪：下一則訊息照設定套用");
    }

    [Fact]
    public async Task 準備中切換權限模式_回false由中樞提示下一則才生效_不會送出又被蓋回()
    {
        var fake = new Fake(temp, "slowconfig");
        using var session = new GrokAgentSession();
        var events = Collect(session);

        // 第一輪要設思考強度（模型在 session/new 就帶上，強度要另送）：set_config_option 的往返期間（已有 sessionId、還沒送出 prompt）切換權限模式
        var send = session.SendAsync(Input("第一則"), fake.Settings(model: "grok-fake-2", effort: "xhigh"));
        await fake.WaitForLogAsync("p1:set_config reasoning_effort=xhigh");
        (await session.SetPermissionModeAsync(AgentPermissionMode.FullAccess)).Should().BeFalse("這一輪會照開始時的設定套用，現在送出會被蓋回");
        await send;
        await TakeUntilAsync(events, e => e is AgentTurnCompletedEvent);

        fake.Log().Should().NotContain(l => l.Contains("yolo always-approve"), "沒有送出之後又改回的完全自動");
        (await session.SetPermissionModeAsync(AgentPermissionMode.FullAccess)).Should().BeFalse("沒有在執行的一輪：下一則訊息照設定套用");
    }

    [Fact]
    public async Task 程式重開後續接_用中樞記的id_session_resume_不附背景說明()
    {
        var fake = new Fake(temp, "basic");
        using var session = new GrokAgentSession();
        var events = Collect(session);

        var turn = await TurnAsync(session, events, fake.Settings(sessionId: "s-saved", started: true, mode: AgentPermissionMode.FullAccess), "接著做");

        turn.OfType<AgentSessionStartedEvent>().Single().Should().Be(new AgentSessionStartedEvent("s-saved", null, "resumed"));
        fake.Log().Should().ContainInOrder("p1:session/resume sid=s-saved yolo=True cwdOk=True", "p1:yolo always-approve", "p1:prompt text=接著做");
        fake.Log().Should().NotContain(l => l.Contains("session/new"));
    }

    [Fact]
    public async Task 續接找不到對話_改開新對話_附上背景說明與前文節錄()
    {
        var fake = new Fake(temp, "notfound");
        using var session = new GrokAgentSession();
        var events = Collect(session);

        var turn = await TurnAsync(session, events, fake.Settings(sessionId: "s-gone", started: true), "還記得嗎");

        turn.OfType<AgentNoticeEvent>().Should().ContainSingle();
        var started = turn.OfType<AgentSessionStartedEvent>().Single();
        started.SessionId.Should().NotBe("s-gone", "不能再用找不到的 id");
        Guid.TryParse(started.SessionId, out _).Should().BeTrue();
        fake.Log().Should().ContainInOrder(
            "p1:session/resume sid=s-gone yolo=False cwdOk=True",
            $"p1:session/new sid={started.SessionId} model= yolo=False auto=False ask=True cwdOk=True",
            @"p1:prompt text=<myriagent_instructions>\n測試說明\n</myriagent_instructions>\n\n（前文節錄）\n\n還記得嗎");
        turn.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
    }

    [Fact]
    public async Task 核准卡_允許一次照選項回覆_拒絕一次結束這一輪_工具卡標已拒絕()
    {
        var fake = new Fake(temp, "permission");
        using var session = new GrokAgentSession();
        var events = Collect(session);

        var allowed = await TurnAsync(session, events, fake.Settings(), "寫檔", async ev =>
        {
            if (ev is AgentApprovalEvent approval)
                await session.RespondApprovalAsync(approval.RequestKey, AgentApprovalChoice.Allow, null);
        });
        var card = allowed.OfType<AgentApprovalEvent>().Single();
        card.ToolKind.Should().Be("write");
        card.CanAllowForSession.Should().BeTrue();
        card.AllowSessionLabel.Should().Be("本對話的編輯都允許");
        card.FilePath.Should().Be(Path.Combine(fake.Work, "call-1-1.txt"));
        card.Diff.Should().NotBeNullOrEmpty();
        IndexOf<AgentToolStartedEvent>(allowed).Should().BeLessThan(IndexOf<AgentApprovalEvent>(allowed), "工具卡要在核准卡之前");
        allowed.OfType<AgentToolCompletedEvent>().Single().Should().Match<AgentToolCompletedEvent>(e => e.Status == AgentItemStatus.Done && e.Output == null, "編輯成功有差異就夠了");

        var denied = await TurnAsync(session, events, fake.Settings(), "再寫一次", async ev =>
        {
            if (ev is AgentApprovalEvent approval)
                await session.RespondApprovalAsync(approval.RequestKey, AgentApprovalChoice.Deny, "換個檔名");
        });
        denied.OfType<AgentToolCompletedEvent>().Single().Should().Match<AgentToolCompletedEvent>(e => e.Status == AgentItemStatus.Declined && e.Output == null);
        denied.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Interrupted, "grok 的拒絕會結束這一輪（中樞依「這一輪拒絕過」當成正常結束並送說明）");
        fake.Log().Should().ContainInOrder("p1:permission call-1-1 edit -> allow-once", "p1:permission call-2-1 edit -> reject-once");
    }

    [Fact]
    public async Task 本對話都允許_同一輪的第二次編輯由會話代為核准_不再出卡()
    {
        var fake = new Fake(temp, "permission");
        using var session = new GrokAgentSession();
        var events = Collect(session);

        var turn = await TurnAsync(session, events, fake.Settings(), "twice", async ev =>
        {
            if (ev is AgentApprovalEvent approval)
                await session.RespondApprovalAsync(approval.RequestKey, AgentApprovalChoice.AllowForSession, null);
        });

        turn.OfType<AgentApprovalEvent>().Should().ContainSingle();
        turn.OfType<AgentToolStartedEvent>().Should().HaveCount(2);
        fake.Log().Should().ContainInOrder("p1:permission call-1-1 edit -> allow-edits-session", "p1:permission call-1-2 edit -> allow-once");
    }
}

public sealed class GrokSessionFakeCliTests2 : GrokSessionFakeCliTestsBase
{
    [Fact]
    public async Task 自動接受編輯_工作資料夾內的編輯代為核准_資料夾外與指令仍要問()
    {
        var fake = new Fake(temp, "permission");
        using var session = new GrokAgentSession();
        var events = Collect(session);
        Func<AgentEvent, Task> deny = async ev =>
        {
            if (ev is AgentApprovalEvent approval)
                await session.RespondApprovalAsync(approval.RequestKey, AgentApprovalChoice.Deny, null);
        };

        var inside = await TurnAsync(session, events, fake.Settings(mode: AgentPermissionMode.AcceptEdits), "寫檔", deny);
        var outside = await TurnAsync(session, events, fake.Settings(mode: AgentPermissionMode.AcceptEdits), "outside", deny);
        var exec = await TurnAsync(session, events, fake.Settings(mode: AgentPermissionMode.AcceptEdits), "exec", deny);

        inside.OfType<AgentApprovalEvent>().Should().BeEmpty();
        outside.OfType<AgentApprovalEvent>().Should().ContainSingle("工作資料夾外的檔案");
        exec.OfType<AgentApprovalEvent>().Single().Should().Match<AgentApprovalEvent>(a => a.ToolKind == "bash" && a.Title == "npm test" && a.AllowSessionLabel == "本對話的指令都不再詢問");
        fake.Log().Should().ContainInOrder("p1:permission call-1-1 edit -> allow-once", "p1:permission call-2-1 outside -> reject-once", "p1:permission call-3-1 exec -> reject-once");
    }

    [Fact]
    public async Task 提問卡_選項照原文_自填的答案以Other加notes回覆()
    {
        var fake = new Fake(temp, "question");
        using var session = new GrokAgentSession();
        var events = Collect(session);

        var turn = await TurnAsync(session, events, fake.Settings(), "問我", async ev =>
        {
            if (ev is AgentQuestionEvent question)
                await session.AnswerQuestionAsync(question.RequestKey, new Dictionary<string, IReadOnlyList<string>> { ["Color?"] = new[] { "綠色" } });
        });

        var card = turn.OfType<AgentQuestionEvent>().Single();
        card.AnswerAsMessage.Should().BeFalse();
        card.Questions.Single().Options.Select(o => o.Label).Should().Equal("Red", "Blue");
        turn.OfType<AgentToolStartedEvent>().Should().BeEmpty("提問不畫成工具卡");
        fake.Log().Should().Contain("""p1:question -> {"outcome":"accepted","answers":{"Color?":["Other"]},"annotations":{"Color?":{"notes":"綠色"}}}""");
    }

    [Fact]
    public async Task 計畫確認_同意並自動接受編輯_模式跟著改_接下來資料夾內的編輯代為核准()
    {
        var fake = new Fake(temp, "plan");
        using var session = new GrokAgentSession();
        var events = Collect(session);

        var turn = await TurnAsync(session, events, fake.Settings(mode: AgentPermissionMode.Plan), "規劃", async ev =>
        {
            if (ev is AgentApprovalEvent { IsPlan: true } plan)
                await session.RespondPlanAsync(plan.RequestKey, AgentPlanChoice.AcceptEdits, null);
        });

        var card = turn.OfType<AgentApprovalEvent>().Single();
        card.IsPlan.Should().BeTrue();
        card.Detail.Should().Be("# Plan\n1. write a file");
        turn.OfType<AgentPermissionModeChangedEvent>().Should().Equal(new AgentPermissionModeChangedEvent(AgentPermissionMode.AcceptEdits));
        turn.OfType<AgentApprovalEvent>().Should().ContainSingle("同意後的編輯照新模式代為核准");
        turn.OfType<AgentToolStartedEvent>().Should().ContainSingle("計畫確認不畫成工具卡，只有之後的編輯");
        fake.Log().Should().ContainInOrder("p1:set_mode plan", """p1:plan -> {"outcome":"approved"}""", "p1:permission call-1-after-plan edit -> allow-once");
    }

    [Fact]
    public async Task 停止_送session_cancel_執行中的工具改成已停止_回報已停止()
    {
        var fake = new Fake(temp, "hang");
        using var session = new GrokAgentSession();
        var events = Collect(session);

        await session.SendAsync(Input("跑很久"), fake.Settings());
        await TakeUntilAsync(events, e => e is AgentToolOutputEvent);
        session.IsAtSafePoint.Should().BeFalse("工具執行中");
        await session.InterruptAsync();
        var rest = await TakeUntilAsync(events, e => e is AgentTurnCompletedEvent);

        rest.OfType<AgentToolCompletedEvent>().Single().Should().Match<AgentToolCompletedEvent>(e => e.Status == AgentItemStatus.Cancelled && e.Output == "partial\n\n已停止");
        rest.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Interrupted);
        session.IsRunning.Should().BeFalse();
        fake.Log().Should().ContainInOrder("p1:tool-running", "p1:cancel");
        session.HasLiveProcess.Should().BeTrue("停止只取消這一輪，程序留著下一則用");
    }

    [Fact]
    public async Task 停止後沒有回應_逾時結束程序樹_回報已停止()
    {
        var fake = new Fake(temp, "hangstuck");
        using var session = new GrokAgentSession { CancelTimeout = TimeSpan.FromSeconds(1) };
        var events = Collect(session);

        await session.SendAsync(Input("跑很久"), fake.Settings());
        await TakeUntilAsync(events, e => e is AgentToolOutputEvent);
        await session.InterruptAsync();
        var rest = await TakeUntilAsync(events, e => e is AgentTurnCompletedEvent);

        rest.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Interrupted);
        rest.OfType<AgentToolCompletedEvent>().Single().Status.Should().Be(AgentItemStatus.Cancelled);
        session.HasLiveProcess.Should().BeFalse();
    }

    [Fact]
    public async Task 沒登入_建立對話失敗_丟出尚未登入()
    {
        var fake = new Fake(temp, "auth");
        using var session = new GrokAgentSession();

        var act = () => session.SendAsync(Input("你好"), fake.Settings());

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage(GrokAcpRules.NotSignedInMessage);
        session.IsRunning.Should().BeFalse();
        fake.Log().Count(l => l.Contains("session/new")).Should().Be(1, "沒登入不換 id 重試");
    }

    [Fact]
    public async Task 重開_工具執行中立即套用_cancel後關程序_resume並送接續訊息_回報新版本()
    {
        var fake = new Fake(temp, "restart");
        using var session = new GrokAgentSession { RestartCloseGrace = TimeSpan.FromSeconds(1), RestartTreeExitTimeout = TimeSpan.FromSeconds(2) };
        var events = Collect(session);
        await session.SendAsync(Input("開始"), fake.Settings(cli: CliIdentities.Grok("1.0.52", fileId: 1)));
        var before = await TakeUntilAsync(events, e => e is AgentToolOutputEvent);
        var p1 = before.OfType<AgentProcessStartedEvent>().Single();

        (await session.RestartAsync(Restart(AgentRestartMode.SafePoint, fake.Settings(sessionId: "s-pre", started: true))))
            .Outcome.Should().Be(AgentRestartOutcome.Deferred, "工具還在跑，只在安全點重開時什麼都不做");

        const string Continuation = "（系統通知：測試的接續訊息，不是使用者拒絕）";
        var newCli = CliIdentities.Grok("1.0.60", fileId: 2);
        var result = await session.RestartAsync(Restart(AgentRestartMode.Immediate, fake.Settings(sessionId: "s-pre", started: true, cli: newCli), Continuation));

        result.Outcome.Should().Be(AgentRestartOutcome.RestartedAndContinued);
        result.PreviousProcessInstance.Should().Be(p1.ProcessInstance);
        var after = await TakeUntilAsync(events, e => e is AgentTurnCompletedEvent);
        after.OfType<AgentProcessExitedEvent>().Single().Should().Match<AgentProcessExitedEvent>(e => e.ProcessInstance == p1.ProcessInstance && e.Expected);
        var p2 = after.OfType<AgentProcessStartedEvent>().Single();
        p2.Cli.Should().Be(newCli);
        after.OfType<AgentRunningVersionEvent>().Should().Equal(new AgentRunningVersionEvent(p2.ProcessInstance, "1.0.60"));
        after.OfType<AgentSessionStartedEvent>().Single().Detail.Should().Be("resumed");
        after.OfType<AgentTurnStartedByCliEvent>().Should().Equal(new AgentTurnStartedByCliEvent(AgentAutoTurnKind.Continuation));
        after.OfType<AgentTurnCompletedEvent>().Should().ContainSingle("被重開打斷的那一輪不回報結束，只有接續的一輪").Which.Outcome.Should().Be(AgentTurnOutcome.Completed);
        IndexOf<AgentTurnStartedByCliEvent>(after).Should().BeLessThan(IndexOf<AgentTextCompletedEvent>(after), "接續一輪的事件都排在它後面");
        after.OfType<AgentToolCompletedEvent>().Should().OnlyContain(e => e.ToolId != before.OfType<AgentToolStartedEvent>().Single().ToolId,
            "被打斷的工具卡由中樞收尾，會話不替它回報「已停止」");

        fake.Log().Should().ContainInOrder(
            "p1:start version=1.0.52", "p1:session/new sid=s-pre model= yolo=False auto=False ask=True cwdOk=True",
            @"p1:prompt text=<myriagent_instructions>\n測試說明\n</myriagent_instructions>\n\n開始",
            "p1:tool-running", "p1:cancel", "p1:eof",
            "p2:start version=1.0.60", "p2:initialize", "p2:session/resume sid=s-pre yolo=False cwdOk=True", "p2:set_config model=grok-fake", "p2:yolo ask", $"p2:prompt text={Continuation}");
        session.RunningVersion.Should().Be("1.0.60");
        session.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task 圖片_initialize宣告image時以區塊附上()
    {
        var fake = new Fake(temp, "images");
        var image = Path.Combine(fake.Work, "shot.png");
        File.WriteAllBytes(image, [1, 2, 3]);
        var attachment = new AgentAttachment { FileName = "shot.png", StoredPath = image, Kind = AgentAttachmentKind.Image, ModelImagePaths = { image } };
        using var session = new GrokAgentSession();
        var events = Collect(session);

        await session.SendAsync(new AgentTurnInput("看圖", [attachment]), fake.Settings());
        await TakeUntilAsync(events, e => e is AgentTurnCompletedEvent);

        fake.Log().Should().Contain(l => l.Contains("prompt text=", StringComparison.Ordinal) && l.Contains(" images=1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 額度用完_這一輪結束前先發鎖定_重置時間來自billing()
    {
        var fake = new Fake(temp, "quota");
        using var session = new GrokAgentSession();
        var events = Collect(session);

        var turn = await TurnAsync(session, events, fake.Settings(), "用完");

        var quotaAt = turn.FindIndex(e => e is AgentQuotaExhaustedEvent);
        var doneAt = turn.FindIndex(e => e is AgentTurnCompletedEvent);
        quotaAt.Should().BeGreaterThanOrEqualTo(0).And.BeLessThan(doneAt);
        var quota = (AgentQuotaExhaustedEvent)turn[quotaAt];
        quota.Provider.Should().Be(AgentProvider.Grok);
        quota.ResetsAt.Should().Be(new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero));
        fake.Log().Should().Contain("p1:billing locked");
    }

    [Fact]
    public async Task 手動壓縮_送compact_成功才發已壓縮_不當成一般提示()
    {
        var fake = new Fake(temp, "compact");
        using var session = new GrokAgentSession();
        var events = Collect(session);

        var turn = await TurnAsync(session, events, fake.Settings(), "/compact");

        turn.OfType<AgentNoticeEvent>().Count(e => e.Message == GrokAcpRules.CompactedNotice && !e.IsError).Should().Be(1);
        turn.OfType<AgentContextUsageEvent>().Count(e => e.Compacted).Should().Be(1);
        turn.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        session.IsRunning.Should().BeFalse();
        fake.Log().Should().Contain(l => l.StartsWith("p1:compact sid=", StringComparison.Ordinal));
        fake.Log().Should().NotContain(l => l.Contains("prompt text=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 上下文視窗_只發說明_不送提示也不查額度()
    {
        var fake = new Fake(temp, "basic");
        using var session = new GrokAgentSession();
        var events = Collect(session);

        var turn = await TurnAsync(session, events, fake.Settings(), "/context-window");

        turn.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        turn.OfType<AgentNoticeEvent>().Should().Contain(e => e.Message.Contains("不能切換", StringComparison.Ordinal) && e.Message.Contains("256,000", StringComparison.Ordinal));
        session.IsRunning.Should().BeFalse();
        fake.Log().Should().NotContain(l => l.Contains("prompt text=", StringComparison.Ordinal));
        fake.Log().Should().NotContain(l => l.Contains("billing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 背景工作_開始時算存活_will_wake為false才叫醒_結束後數量歸零()
    {
        var fake = new Fake(temp, "background");
        using var session = new GrokAgentSession();
        var events = Collect(session);

        var sending = session.SendAsync(Input("放背景"), fake.Settings());
        await fake.WaitForLogAsync("p1:backgrounded");
        var seen = false;
        for (var i = 0; i < 40 && !seen; i++)
        {
            seen = session.HasLiveBackgroundWork;
            if (!seen)
                await Task.Delay(50);
        }
        seen.Should().BeTrue("backgrounded 之後、完成通知之前還有背景工作");

        File.WriteAllText(Path.Combine(fake.Work, "bg-go.txt"), "go");
        var untilFinished = await TakeUntilAsync(events, e => e is AgentBackgroundCommandFinishedEvent);
        var finished = untilFinished.OfType<AgentBackgroundCommandFinishedEvent>().Single();
        finished.ToolId.Should().Be("call-bg");
        finished.Title.Should().Be("sleep 30");
        finished.Status.Should().Be(AgentItemStatus.Done);

        untilFinished.OfType<AgentBackgroundWorkEvent>().Should().Contain(e => e.Count == 0);
        await sending;
        await TakeUntilAsync(events, e => e is AgentTurnCompletedEvent);
        session.HasLiveBackgroundWork.Should().BeFalse();
    }

    [Fact]
    public async Task 停止背景工作_沒有逐一停止的協定_閒置時結束程序樹_背景工作跟著結束_只能全部停止()
    {
        var fake = new Fake(temp, "background_idle");
        using var session = new GrokAgentSession();
        var events = Collect(session);

        await session.SendAsync(Input("放背景"), fake.Settings());
        await TakeUntilAsync(events, e => e is AgentTurnCompletedEvent);
        session.HasLiveBackgroundWork.Should().BeTrue();
        session.LiveBackgroundTools.Should().OnlyContain(t => t.StopId == null, "Grok 沒有單獨停止的 id");
        using var child = System.Diagnostics.Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(fake.Work, "bg-pid.txt"))));
        child.HasExited.Should().BeFalse();

        (await session.StopBackgroundWorkAsync("bg1")).Stopped.Should().Be(0, "只能全部停止");
        session.HasLiveProcess.Should().BeTrue();

        (await session.StopBackgroundWorkAsync(null)).Should().Be(new AgentBackgroundStopResult(1, null));
        child.WaitForExit(10_000).Should().BeTrue("grok 的子孫（背景工作）跟著程序樹結束");
        session.HasLiveBackgroundWork.Should().BeFalse();
        session.HasLiveProcess.Should().BeFalse();
        (await TakeUntilAsync(events, e => e is AgentBackgroundWorkEvent { Count: 0 })).Should().NotBeEmpty();
        session.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task 自動壓縮_通知發已壓縮_不把tokens_after當上下文()
    {
        var fake = new Fake(temp, "autocompact");
        using var session = new GrokAgentSession();
        var events = Collect(session);

        var turn = await TurnAsync(session, events, fake.Settings(), "繼續");

        turn.OfType<AgentNoticeEvent>().Count(e => e.Message == GrokAcpRules.CompactedNotice).Should().Be(1);
        turn.OfType<AgentContextUsageEvent>().Count(e => e.Compacted).Should().Be(1);
        turn.OfType<AgentContextUsageEvent>().Should().NotContain(e => e.UsedTokens == 120 || e.UsedTokens == 100);
        turn.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
    }

    [Fact]
    public async Task 背景工作輸出_讀本機檔尾_結束的id沒有輸出()
    {
        var fake = new Fake(temp, "background_idle");
        using var session = new GrokAgentSession();
        var events = Collect(session);

        await session.SendAsync(Input("放背景"), fake.Settings());
        await TakeUntilAsync(events, e => e is AgentTurnCompletedEvent);
        session.HasLiveBackgroundWork.Should().BeTrue();

        var output = await session.GetTaskOutputAsync("bg1");
        output.Error.Should().BeNull();
        output.Output.Should().Be("hello\nbg");
        output.Truncated.Should().BeFalse();
        output.TotalBytes.Should().Be(new FileInfo(Path.Combine(fake.Work, "bg.log")).Length);
        (await session.GetTaskOutputAsync("gone")).Error.Should().Be("這個背景工作已經結束了");

        (await session.StopBackgroundWorkAsync(null)).Stopped.Should().Be(1);
    }

    [Fact]
    public async Task 分叉_程序開著_在同一個程序分叉_回新id且來源不變()
    {
        var fake = new Fake(temp, "basic");
        using var session = new GrokAgentSession();
        var events = Collect(session);
        await TurnAsync(session, events, fake.Settings(), "第一則");
        session.IsRunning.Should().BeFalse();

        var proposed = Guid.NewGuid().ToString();
        var child = await session.ForkSessionAsync(proposed, fake.Settings(started: true));
        child.Should().Be(proposed);
        fake.Log().Should().Contain($"p1:fork new={proposed} source=s-pre sid=s-pre");
        fake.Log().Count(l => l.Contains("session/new", StringComparison.Ordinal)).Should().Be(1);
        session.HasLiveProcess.Should().BeTrue();
        session.IsRunning.Should().BeFalse("分叉不是一輪");
    }

    [Fact]
    public async Task 分叉_沒有程序_先接上續接再分叉_之後送出沿用同一個程序與對話()
    {
        var fake = new Fake(temp, "basic");
        using var session = new GrokAgentSession();
        var events = Collect(session);

        var proposed = Guid.NewGuid().ToString();
        var child = await session.ForkSessionAsync(proposed, fake.Settings(sessionId: "s-saved", started: true));
        child.Should().Be(proposed);
        session.IsRunning.Should().BeFalse();

        await TurnAsync(session, events, fake.Settings(sessionId: "s-saved", started: true), "接著做");
        fake.Log().Should().ContainInOrder(
            "p1:session/resume sid=s-saved yolo=False cwdOk=True",
            $"p1:fork new={proposed} source=s-saved sid=s-saved",
            "p1:prompt text=接著做");
        fake.Log().Count(l => l.Contains("session/resume", StringComparison.Ordinal)).Should().Be(1, "接上的對話直接用");
        fake.Log().Should().NotContain(l => l.Contains("session/new", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 分叉_原本的對話續接不了_不分叉_下一次送出帶背景說明與前文節錄()
    {
        // 子代理審查（2026-11-01）：接上時續接失敗改開了新的對話，分叉出來的會是空的；那次新對話的旗標也不能丟，不然原對話下一則不帶說明與前文
        var fake = new Fake(temp, "notfound");
        using var session = new GrokAgentSession();
        var events = Collect(session);

        var child = await session.ForkSessionAsync(Guid.NewGuid().ToString(), fake.Settings(sessionId: "s-gone", started: true));
        child.Should().BeNull("新開的對話沒有前文，改附前文節錄");
        fake.Log().Should().NotContain(l => l.Contains("fork new=", StringComparison.Ordinal));

        await TurnAsync(session, events, fake.Settings(sessionId: "s-gone", started: true), "還記得嗎");
        fake.Log().Count(l => l.Contains("session/new", StringComparison.Ordinal)).Should().Be(1, "沿用接上時開的那個新對話");
        fake.Log().Should().Contain(@"p1:prompt text=<myriagent_instructions>\n測試說明\n</myriagent_instructions>\n\n（前文節錄）\n\n還記得嗎");
    }

    [Fact]
    public async Task 分叉失敗_回null不丟例外()
    {
        var fake = new Fake(temp, "forkfail");
        using var session = new GrokAgentSession();
        var events = Collect(session);
        await TurnAsync(session, events, fake.Settings(), "第一則");

        var child = await session.ForkSessionAsync(Guid.NewGuid().ToString(), fake.Settings(started: true));
        child.Should().BeNull();
        session.IsRunning.Should().BeFalse();
        session.HasLiveProcess.Should().BeTrue();
    }
}
