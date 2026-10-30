using System.Collections.Concurrent;
using System.Diagnostics;
using Myriagent.Agents;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>
/// 以假的 claude -p（Windows PowerShell 腳本，照實測的 stream-json 格式）測 ClaudeAgentSession 的會話層行為，不連網、不耗額度：
/// 權限模式的同步、「本對話都允許」的範圍與重開程序後帶回、換工作資料夾續接、續接時的雜訊 result 與寫入競態、中斷看門狗、CLI 撤回請求。
/// 情境由環境變數 FAKE_SCENARIO 決定；假 CLI 把收到的回應與模式切換記下來，以回覆文字回報給測試。
/// </summary>
public sealed class ClaudeSessionFakeCliTests : IDisposable
{
    private static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>假的 claude -p：腳本只用 ASCII（Windows PowerShell 讀沒有 BOM 的腳本用系統碼頁）</summary>
    private const string FakeClaude = """"
        $ErrorActionPreference = 'Stop'
        $utf8 = New-Object System.Text.UTF8Encoding $false
        $out = New-Object System.IO.StreamWriter([Console]::OpenStandardOutput(), $utf8)
        $out.NewLine = [string][char]10
        $out.AutoFlush = $true
        $argList = @($args)
        $scenario = $env:FAKE_SCENARIO
        $script:log = New-Object System.Collections.Generic.List[string]
        $script:turn = 0
        $script:interrupted = $false

        function Emit([string]$json) { $out.WriteLine($json) }
        function Js([string]$text) { return (ConvertTo-Json -InputObject $text -Compress) }
        function ArgValue([string]$name) {
            $i = [array]::IndexOf($argList, $name)
            if ($i -ge 0 -and $i + 1 -lt $argList.Count) { return $argList[$i + 1] }
            return $null
        }
        function Note([string]$entry) { $script:log.Add($entry) }

        $resume = ArgValue '--resume'
        if ($resume -and $resume.EndsWith('.jsonl')) { $sessionId = [IO.Path]::GetFileNameWithoutExtension($resume) }
        elseif ($resume) { $sessionId = $resume }
        else { $sessionId = ArgValue '--session-id' }
        $script:mode = ArgValue '--permission-mode'

        if ($scenario -eq 'resumerace' -and $resume) {
            # 續接失敗的 CLI：不讀 stdin 就關掉它（宿主的大訊息寫入先失敗），稍後才印 result 並結束
            Add-Type -Namespace FakeCli -Name Native -MemberDefinition '[System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern System.IntPtr GetStdHandle(int n); [System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern bool CloseHandle(System.IntPtr h);'
            [void][FakeCli.Native]::CloseHandle([FakeCli.Native]::GetStdHandle(-10))
            Start-Sleep -Milliseconds 300
            [Console]::Error.WriteLine('No conversation found with session ID: ' + $sessionId)
            Emit ('{"type":"result","subtype":"error_during_execution","is_error":true,"num_turns":0,"errors":["No conversation found with session ID: ' + $sessionId + '"]}')
            exit 1
        }
        if ($scenario -eq 'modereject') { [Environment]::CurrentDirectory = [IO.Path]::GetTempPath() }

        $in = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $utf8)

        function ReadLineMsg {
            while ($true) {
                $line = $in.ReadLine()
                if ($line -eq $null) { exit 0 }
                if ($line.Trim().Length -eq 0) { continue }
                return ($line | ConvertFrom-Json)
            }
        }

        function HandleControl($msg) {
            $subtype = $msg.request.subtype
            if ($subtype -eq 'set_permission_mode') {
                if ($scenario -eq 'modereject') {
                    Emit ('{"type":"control_response","response":{"subtype":"error","request_id":"' + $msg.request_id + '","error":"Cannot set permission mode"}}')
                    return
                }
                $script:mode = $msg.request.mode
                Note ('mode=' + $script:mode)
                Emit ('{"type":"control_response","response":{"subtype":"success","request_id":"' + $msg.request_id + '","response":{"mode":"' + $script:mode + '"}}}')
                Emit ('{"type":"system","subtype":"status","permissionMode":"' + $script:mode + '","session_id":"' + $sessionId + '"}')
                return
            }
            if ($subtype -eq 'remote_control') {
                # 照實測 2.1.300：開啟成功回 bridge_session_id 等欄位，接著 bridge_state ready、connected；接回的 id 原樣沿用。
                # FAKE_RC=fail 一律失敗、failreattach 帶接回的 id 才失敗、drop 連上後馬上 failed（terminal）、flap 連上後暫時斷線（transient）
                if ($msg.request.enabled) {
                    Note ('rc=on:' + $msg.request.name + ':' + $msg.request.reattach_session_id)
                    if ($env:FAKE_RC -eq 'fail' -or ($env:FAKE_RC -eq 'failreattach' -and $msg.request.reattach_session_id)) {
                        Emit ('{"type":"control_response","response":{"subtype":"error","request_id":"' + $msg.request_id + '","error":"Remote Control is not enabled for this account"}}')
                        return
                    }
                    $bridge = $msg.request.reattach_session_id
                    if (-not $bridge) { $bridge = 'cse_' + [guid]::NewGuid().ToString('N') }
                    Emit ('{"type":"control_response","response":{"subtype":"success","request_id":"' + $msg.request_id + '","response":{"session_url":"https://claude.ai/code/session_' + $bridge + '","connect_url":"https://claude.ai/code?environment=","environment_id":"","bridge_epoch":1,"bridge_session_id":"' + $bridge + '"}}}')
                    Emit ('{"type":"system","subtype":"bridge_state","state":"ready","session_id":"' + $sessionId + '"}')
                    if ($env:FAKE_RC -eq 'drop') {
                        Emit ('{"type":"system","subtype":"bridge_state","state":"failed","detail":"network gone","failure_kind":"terminal","bridge_epoch":1,"session_id":"' + $sessionId + '"}')
                    } elseif ($env:FAKE_RC -eq 'flap') {
                        Emit ('{"type":"system","subtype":"bridge_state","state":"connected","bridge_epoch":1,"session_id":"' + $sessionId + '"}')
                        Emit ('{"type":"system","subtype":"bridge_state","state":"failed","detail":"reconnecting","failure_kind":"transient","bridge_epoch":1,"session_id":"' + $sessionId + '"}')
                    } else {
                        Emit ('{"type":"system","subtype":"bridge_state","state":"connected","bridge_epoch":1,"session_id":"' + $sessionId + '"}')
                    }
                } else {
                    Note 'rc=off'
                    Emit ('{"type":"control_response","response":{"subtype":"success","request_id":"' + $msg.request_id + '"}}')
                }
                return
            }
            if ($subtype -eq 'rename_session') {
                # 照實測 2.1.308：回 success，接著回送 system/session_title_changed（宿主自己改的也回送）
                Note ('rename=' + $msg.request.title)
                Emit ('{"type":"control_response","response":{"subtype":"success","request_id":"' + $msg.request_id + '"}}')
                Emit ('{"type":"system","subtype":"session_title_changed","title":' + (Js $msg.request.title) + ',"session_id":"' + $sessionId + '"}')
                return
            }
            if ($subtype -eq 'get_context_usage') {
                # Measured on 2.1.309: computed locally (5-10 ms), the data of /context; FAKE_CTX=error answers like a CLI that does not know it
                if ($env:FAKE_CTX -eq 'error') {
                    Emit ('{"type":"control_response","response":{"subtype":"error","request_id":"' + $msg.request_id + '","error":"Unknown request subtype"}}')
                    return
                }
                Emit ('{"type":"control_response","response":{"subtype":"success","request_id":"' + $msg.request_id + '","response":{"totalTokens":24219,"maxTokens":200000,"rawMaxTokens":200000,"percentage":12,"autocompactSource":"model-default","model":"fake","autoCompactThreshold":167000,"isAutoCompactEnabled":true,"apiUsage":null}}}')
                return
            }
            if ($subtype -eq 'mcp_status') {
                # Measured on 2.1.310 (tools/probes/claude_cli3_probe.py --mode mcp): local, answers right after initialize
                $st = 'needs-auth'
                if ($script:mcpAuthed) { $st = 'connected' }
                Emit ('{"type":"control_response","response":{"subtype":"success","request_id":"' + $msg.request_id + '","response":{"mcpServers":[{"name":"probe","status":"' + $st + '","transport":"http","scope":"user"},{"name":"broken","status":"failed","error":"Connection closed","transport":"stdio","scope":"project"}]}}}')
                return
            }
            if ($subtype -eq 'mcp_authenticate') {
                # Returns the URL to open; the next mcp_status is connected (the browser came back to the local callback)
                Note ('mcpauth=' + $msg.request.serverName)
                $script:mcpAuthed = $true
                Emit ('{"type":"control_response","response":{"subtype":"success","request_id":"' + $msg.request_id + '","response":{"authUrl":"http://127.0.0.1:9/authorize?state=x","requiresUserAction":true,"callbackExpected":true,"redirectScheme":"localhost","callbackPort":9}}}')
                return
            }
            if ($subtype -eq 'mcp_reconnect' -and $msg.request.serverName -ne 'probe') {
                Emit ('{"type":"control_response","response":{"subtype":"error","request_id":"' + $msg.request_id + '","error":"Server not found: ' + $msg.request.serverName + '"}}')
                return
            }
            if ($subtype -eq 'side_question') {
                # Measured on 2.1.310 (--mode btw): a progress line, then {response, synthetic}
                Note ('btw=' + $msg.request.question)
                Emit ('{"type":"system","subtype":"control_request_progress","request_id":"' + $msg.request_id + '","status":"started","session_id":"' + $sessionId + '"}')
                Emit ('{"type":"control_response","response":{"subtype":"success","request_id":"' + $msg.request_id + '","response":{"response":' + (Js ('side:' + $msg.request.question)) + ',"synthetic":false}}}')
                return
            }
            if ($subtype -eq 'cancel_async_message') {
                # Measured on 2.1.310 (tools/probes/claude_cli6_probe.py --mode cancel): still queued -> cancelled true (and a
                # command_lifecycle cancelled); already read into the turn or unknown -> false
                $ok = $script:queuedSteer -and $msg.request.message_uuid -eq $script:queuedSteer
                if ($ok) { $script:cancelledSteer = $script:queuedSteer; $script:queuedSteer = $null; Lifecycle $msg.request.message_uuid 'cancelled' }
                Note ('cancel=' + $msg.request.message_uuid + ':' + $ok)
                Emit ('{"type":"control_response","response":{"subtype":"success","request_id":"' + $msg.request_id + '","response":{"cancelled":' + ($ok.ToString().ToLower()) + '}}}')
                return
            }
            if ($subtype -eq 'interrupt') { $script:interrupted = $true }
            Emit ('{"type":"control_response","response":{"subtype":"success","request_id":"' + $msg.request_id + '","response":{}}}')
        }

        function ReadMessage {
            while ($true) {
                $msg = ReadLineMsg
                if ($msg.type -eq 'control_request') { HandleControl $msg; continue }
                return $msg
            }
        }

        function Init {
            $source = 'none'
            if ($env:FAKE_API_KEY_SOURCE) { $source = $env:FAKE_API_KEY_SOURCE }
            Emit ('{"type":"system","subtype":"init","session_id":"' + $sessionId + '","model":"fake","permissionMode":"' + $script:mode + '","apiKeySource":"' + $source + '","claude_code_version":"fake"}')
        }

        function Say([string]$text) {
            Emit ('{"type":"assistant","message":{"id":"m' + [guid]::NewGuid().ToString('N') + '","content":[{"type":"text","text":' + (Js $text) + '}]},"parent_tool_use_id":null}')
        }

        function Done { Emit ('{"type":"result","subtype":"success","is_error":false,"num_turns":1,"result":"done","session_id":"' + $sessionId + '"}') }

        function Ask([string]$id, [string]$tool, [string]$inputJson, [string]$suggestions) {
            Emit ('{"type":"control_request","request_id":"' + $id + '","request":{"subtype":"can_use_tool","tool_name":"' + $tool + '","input":' + $inputJson + ',"permission_suggestions":' + $suggestions + ',"tool_use_id":"tu_' + $id + '"}}')
            while ($true) {
                $msg = ReadMessage
                if ($msg.type -ne 'control_response') { continue }
                $r = $msg.response.response
                $entry = [string]$msg.response.request_id + '=' + $r.behavior
                if ($r.updatedPermissions) { $entry += '/' + ((@($r.updatedPermissions) | ForEach-Object { $_.type }) -join '+') }
                Note $entry
                if ($msg.response.request_id -eq $id) { return }
            }
        }

        function UserText($msg) { return ((@($msg.message.content) | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text }) -join '') }

        # Measured on 2.1.301 with --replay-user-messages: a queued command (host message with uuid, or a message sent
        # from claude.ai / Claude App) reports command_lifecycle with command_uuid; the message is replayed (isReplay)
        # right before the model output; remote ones carry origin human, host ones have no origin.
        function Lifecycle([string]$id, [string]$state) { Emit ('{"type":"command_lifecycle","command_uuid":"' + $id + '","state":"' + $state + '","uuid":"' + [guid]::NewGuid().ToString() + '","session_id":"' + $sessionId + '"}') }
        function Replay([string]$id, [string]$text, [bool]$remote) {
            $origin = ''
            if ($remote) { $origin = ',"origin":{"kind":"human"}' }
            Emit ('{"type":"user","message":{"role":"user","content":' + (Js $text) + '},"session_id":"' + $sessionId + '","parent_tool_use_id":null,"uuid":"' + $id + '","isReplay":true' + $origin + '}')
        }
        function StartOwn($msg) { Lifecycle $msg.uuid 'started'; Init; Replay $msg.uuid (UserText $msg) $false }
        function StartRemote([string]$id, [string]$text) { Lifecycle $id 'queued'; Lifecycle $id 'started'; Init; Replay $id $text $true }
        function DoneFor([string]$id) {
            Emit ('{"type":"result","subtype":"success","is_error":false,"num_turns":1,"result":"done","user_message_uuid":"' + $id + '","session_id":"' + $sessionId + '"}')
            Lifecycle $id 'completed'
        }
        function WaitInterrupt {
            while (-not $script:interrupted) {
                $x = ReadLineMsg
                if ($x.type -eq 'control_request') { HandleControl $x }
            }
            $script:interrupted = $false
        }
        # Measured on 2.1.301: a stopped turn (from the host or from the phone) gets an inserted user message
        # "[Request interrupted by user...]" (not a replay), then result error_during_execution, then lifecycle cancelled
        function StoppedFor([string]$id) {
            Emit ('{"type":"user","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user for tool use]"}]},"parent_tool_use_id":null,"uuid":"' + [guid]::NewGuid().ToString() + '","session_id":"' + $sessionId + '"}')
            Emit ('{"type":"result","subtype":"error_during_execution","is_error":true,"num_turns":1,"errors":[],"user_message_uuid":"' + $id + '","session_id":"' + $sessionId + '"}')
            Lifecycle $id 'cancelled'
        }

        # MCP elicitation (measured on 2.1.310): send the request, note the host's {action, content} as compact JSON
        function Elicit([string]$id, [string]$requestJson) {
            Emit ('{"type":"control_request","request_id":"' + $id + '","request":' + $requestJson + '}')
            while ($true) {
                $msg = ReadMessage
                if ($msg.type -ne 'control_response') { continue }
                Note ([string]$msg.response.request_id + '=' + (ConvertTo-Json -InputObject $msg.response.response -Compress -Depth 5))
                if ($msg.response.request_id -eq $id) { return }
            }
        }

        while ($true) {
            $m = ReadMessage
            if ($m.type -ne 'user') { continue }
            $script:turn++
            switch ($scenario) {
                'editmode' {
                    Init
                    if ($script:turn -eq 1) {
                        Ask 'r1' 'Edit' '{"file_path":"C:/w/a.txt","old_string":"x","new_string":"y"}' '[{"type":"setMode","mode":"acceptEdits","destination":"session"}]'
                        Ask 'r2' 'Write' '{"file_path":"C:/Users/someone/.claude/settings.json","content":"{}"}' '[]'
                    }
                    Say ('turn' + $script:turn + ':' + ($script:log -join ','))
                    Done
                }
                'statusmode' {
                    Init
                    if ($script:turn -eq 1) {
                        $script:mode = 'plan'
                        Emit ('{"type":"system","subtype":"status","permissionMode":"plan","session_id":"' + $sessionId + '"}')
                    }
                    Say ('turn' + $script:turn + ':' + ($script:log -join ','))
                    Done
                }
                'rules' {
                    Init
                    if (-not ($argList -contains '--allowedTools')) {
                        Ask 'r1' 'Bash' '{"command":"npm test"}' '[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"npm test:*"}],"behavior":"allow","destination":"localSettings"},{"type":"addDirectories","directories":["C:\\data"],"destination":"localSettings"}]'
                    }
                    Say (ConvertTo-Json -InputObject $argList -Compress)
                    Done
                }
                'resume' {
                    Init
                    Say (ConvertTo-Json -InputObject ([ordered]@{ resume = (ArgValue '--resume'); text = (UserText $m) }) -Compress)
                    Done
                }
                'rc' {
                    # 回報這個程序收到的 remote_control（開啟的名稱與接回的 id、關閉）
                    Init
                    Say ('turn' + $script:turn + ':' + ($script:log -join ','))
                    Done
                }
                'notification' {
                    # 實測 2.1.295：--resume 時佇列裡有背景工作通知，先送一個 origin 為 task-notification、num_turns 0 的 result
                    Emit ('{"type":"system","subtype":"task_notification","task_id":"b1","status":"stopped","summary":"Background shell command did not finish","session_id":"' + $sessionId + '"}')
                    Init
                    Emit ('{"type":"result","subtype":"success","is_error":false,"num_turns":0,"result":"","origin":{"kind":"task-notification"},"session_id":"' + $sessionId + '"}')
                    Start-Sleep -Milliseconds 300
                    Init
                    Say 'real-reply'
                    Done
                }
                'slowstop' {
                    # 一直跑到收到 interrupt；第 2 輪起要 FAKE_STOP_DELAY_MS 才收尾（模擬工具還在結束中）
                    Init
                    while (-not $script:interrupted) {
                        $x = ReadLineMsg
                        if ($x.type -eq 'control_request') { HandleControl $x }
                    }
                    $script:interrupted = $false
                    if ($script:turn -gt 1) { Start-Sleep -Milliseconds ([int]$env:FAKE_STOP_DELAY_MS) }
                    Emit ('{"type":"result","subtype":"error_during_execution","is_error":true,"num_turns":1,"errors":[],"session_id":"' + $sessionId + '"}')
                }
                'cancel' {
                    # 送出 r1 後又撤回（工具被中止、有逾時的詢問被收回），接著問 r2
                    Init
                    Emit '{"type":"control_request","request_id":"r1","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"echo r1"},"tool_use_id":"tu_r1"}}'
                    Emit '{"type":"control_cancel_request","request_id":"r1"}'
                    Ask 'r2' 'Bash' '{"command":"echo r2"}' '[]'
                    Say ($script:log -join ',')
                    Done
                }
                'remoteidle' {
                    # host turn, then (idle) a message from the phone starts its own turn
                    StartOwn $m
                    Say ('uuid:' + $m.uuid)
                    DoneFor $m.uuid
                    if ($script:turn -eq 1) {
                        Start-Sleep -Milliseconds 200
                        StartRemote 'r-1' 'phone: status?'
                        Say 'remote reply'
                        DoneFor 'r-1'
                    }
                }
                'remoterace' {
                    # the phone message started first; the host message waits behind it in the queue
                    Lifecycle $m.uuid 'queued'
                    StartRemote 'r-2' 'phone first'
                    Say 'R'
                    Start-Sleep -Milliseconds 700
                    DoneFor 'r-2'
                    StartOwn $m
                    Say 'U'
                    DoneFor $m.uuid
                }
                'remotemid' {
                    # the phone message arrives while a tool runs: delivered into the running host turn after the tool result
                    StartOwn $m
                    Say 'a'
                    Lifecycle 'r-3' 'queued'
                    Lifecycle 'r-3' 'started'
                    Replay 'r-3' 'phone mid-turn' $true
                    Say 'b'
                    Lifecycle 'r-3' 'completed'
                    Emit ('{"type":"result","subtype":"success","is_error":false,"num_turns":2,"result":"b","user_message_uuid":"' + $m.uuid + '","session_id":"' + $sessionId + '"}')
                    Lifecycle $m.uuid 'completed'
                }
                'remotestop' {
                    # host turn, then a phone turn that runs until interrupted
                    StartOwn $m
                    Say 'ok'
                    DoneFor $m.uuid
                    StartRemote 'r-4' 'phone long task'
                    while (-not $script:interrupted) {
                        $x = ReadLineMsg
                        if ($x.type -eq 'control_request') { HandleControl $x }
                    }
                    $script:interrupted = $false
                    Emit ('{"type":"result","subtype":"error_during_execution","is_error":true,"num_turns":1,"errors":[],"user_message_uuid":"r-4","session_id":"' + $sessionId + '"}')
                    Lifecycle 'r-4' 'completed'
                }
                'remotetakeover' {
                    # host turn; a phone turn starts; the host sends before it ends (queued behind it)
                    StartOwn $m
                    Say 'ok'
                    DoneFor $m.uuid
                    StartRemote 'r-5' 'phone question'
                    $next = ReadMessage
                    Lifecycle $next.uuid 'queued'
                    Say 'R'
                    DoneFor 'r-5'
                    StartOwn $next
                    Say ('U:' + (UserText $next))
                    DoneFor $next.uuid
                }
                'remotestopqueued' {
                    # host turn; a phone turn starts; the host sends (queued behind it) and stops: the interrupt stops
                    # the phone turn (2.1.301 answers still_queued) and the queued host message starts next
                    StartOwn $m
                    Say 'ok'
                    DoneFor $m.uuid
                    StartRemote 'r-7' 'phone long task'
                    $next = ReadMessage
                    Lifecycle $next.uuid 'queued'
                    WaitInterrupt
                    StoppedFor 'r-7'
                    StartOwn $next
                    WaitInterrupt
                    StoppedFor $next.uuid
                }
                'remotenotify' {
                    # host turn; then (idle) a background task completes and the CLI opens its own turn; a phone message
                    # is delivered into that turn, so the only result carries origin task-notification
                    StartOwn $m
                    Say 'ok'
                    DoneFor $m.uuid
                    Start-Sleep -Milliseconds 200
                    Emit ('{"type":"system","subtype":"task_notification","task_id":"b2","status":"completed","summary":"done","session_id":"' + $sessionId + '"}')
                    Lifecycle 'n-1' 'started'
                    Init
                    Say 'notified'
                    Lifecycle 'r-6' 'queued'
                    Lifecycle 'r-6' 'started'
                    Replay 'r-6' 'phone during notification' $true
                    Say 'merged'
                    Emit ('{"type":"result","subtype":"success","is_error":false,"num_turns":2,"result":"merged","origin":{"kind":"task-notification"},"user_message_uuid":"n-1","session_id":"' + $sessionId + '"}')
                    Lifecycle 'n-1' 'completed'
                    Lifecycle 'r-6' 'completed'
                }
                'bgtask' {
                    # Measured E-11 (2.1.296): a run_in_background command lists itself in background_tasks_changed and
                    # task_started (tool_use_id), the tool result says it runs in the background and the turn ends; later it
                    # completes (task_notification, empty list) and the CLI opens its own turn
                    StartOwn $m
                    Emit '{"type":"assistant","message":{"id":"m_bg","content":[{"type":"tool_use","id":"tu_bg","name":"Bash","input":{"command":"npm test","run_in_background":true}}]},"parent_tool_use_id":null}'
                    Emit ('{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"b9","tool_use_id":"tu_bg","task_type":"local_bash","description":"npm test"}],"session_id":"' + $sessionId + '"}')
                    Emit ('{"type":"system","subtype":"task_started","task_id":"b9","tool_use_id":"tu_bg","description":"npm test","is_backgrounded":true,"task_type":"local_bash","session_id":"' + $sessionId + '"}')
                    Emit '{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tu_bg","content":"Command running in background with ID: b9"}]},"parent_tool_use_id":null}'
                    Say 'started'
                    DoneFor $m.uuid
                    Start-Sleep -Milliseconds 500
                    Emit ('{"type":"system","subtype":"task_notification","task_id":"b9","status":"completed","summary":"done","session_id":"' + $sessionId + '"}')
                    Emit ('{"type":"system","subtype":"background_tasks_changed","tasks":[],"session_id":"' + $sessionId + '"}')
                    Init
                    Say 'tests passed'
                    Emit ('{"type":"result","subtype":"success","is_error":false,"num_turns":1,"result":"tests passed","origin":{"kind":"task-notification","producer":"session-task"},"session_id":"' + $sessionId + '"}')
                }
                'notifyidle' {
                    # Measured E-11 (2.1.296): after the host turn a background task completes while idle and the CLI opens
                    # its own turn: task_notification completed, system/init, output, result with origin task-notification
                    StartOwn $m
                    Say 'ok'
                    DoneFor $m.uuid
                    Start-Sleep -Milliseconds 200
                    Emit ('{"type":"system","subtype":"task_notification","task_id":"b3","status":"completed","summary":"done","session_id":"' + $sessionId + '"}')
                    Init
                    Say 'checking'
                    Start-Sleep -Milliseconds 400
                    Say 'checked'
                    Emit ('{"type":"result","subtype":"success","is_error":false,"num_turns":1,"result":"checked","origin":{"kind":"task-notification","producer":"session-task"},"session_id":"' + $sessionId + '"}')
                }
                'notifystop' {
                    # the CLI's own turn after a background task runs until the host stops it
                    StartOwn $m
                    Say 'ok'
                    DoneFor $m.uuid
                    Start-Sleep -Milliseconds 200
                    Emit ('{"type":"system","subtype":"task_notification","task_id":"b4","status":"completed","summary":"done","session_id":"' + $sessionId + '"}')
                    Init
                    Say 'checking'
                    WaitInterrupt
                    Emit ('{"type":"user","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user]"}]},"parent_tool_use_id":null,"uuid":"' + [guid]::NewGuid().ToString() + '","session_id":"' + $sessionId + '"}')
                    Emit ('{"type":"result","subtype":"error_during_execution","is_error":true,"num_turns":1,"errors":[],"origin":{"kind":"task-notification","producer":"session-task"},"session_id":"' + $sessionId + '"}')
                }
                'notifytakeover' {
                    # the host writes a message (send or steer) while the CLI's own turn runs: queued behind it, then handled
                    StartOwn $m
                    Say 'ok'
                    DoneFor $m.uuid
                    Start-Sleep -Milliseconds 200
                    Emit ('{"type":"system","subtype":"task_notification","task_id":"b5","status":"completed","summary":"done","session_id":"' + $sessionId + '"}')
                    Init
                    Say 'N'
                    $next = ReadMessage
                    Lifecycle $next.uuid 'queued'
                    Emit ('{"type":"result","subtype":"success","is_error":false,"num_turns":1,"result":"N","origin":{"kind":"task-notification","producer":"session-task"},"session_id":"' + $sessionId + '"}')
                    StartOwn $next
                    Say ('U:' + (UserText $next))
                    DoneFor $next.uuid
                }
                'replayforeign' {
                    # hypothetical CLI version: replays the host message under its own uuid (not the one the host sent)
                    Init
                    Replay 'x-1' (UserText $m) $false
                    Say 'ok'
                    Emit ('{"type":"result","subtype":"success","is_error":false,"num_turns":1,"result":"ok","user_message_uuid":"x-1","session_id":"' + $sessionId + '"}')
                }
                'remotephonestop' {
                    # host turn; a phone turn starts and is stopped from the phone
                    StartOwn $m
                    Say 'ok'
                    DoneFor $m.uuid
                    StartRemote 'r-8' 'phone long task'
                    Start-Sleep -Milliseconds 200
                    StoppedFor 'r-8'
                }
                'steermerge' {
                    # Measured on 2.1.307: a host message written while a tool runs is queued, then delivered into the same
                    # turn after the tool result (started + replay); the only result carries the first message's uuid
                    StartOwn $m
                    Say 'working'
                    $next = ReadMessage
                    Lifecycle $next.uuid 'queued'
                    Start-Sleep -Milliseconds 300
                    Lifecycle $next.uuid 'started'
                    Replay $next.uuid (UserText $next) $false
                    Say ('merged:' + (UserText $next))
                    Lifecycle $next.uuid 'completed'
                    Emit ('{"type":"result","subtype":"success","is_error":false,"num_turns":2,"result":"merged","user_message_uuid":"' + $m.uuid + '","session_id":"' + $sessionId + '"}')
                    Lifecycle $m.uuid 'completed'
                }
                'steercancel' {
                    # a host message written while a tool runs is queued until the tool result; cancelled before that,
                    # the turn goes on without it and the only result carries the first message's uuid
                    StartOwn $m
                    Emit ('{"type":"assistant","message":{"id":"mt1","content":[{"type":"tool_use","id":"tu_sc","name":"Bash","input":{"command":"ping -n 3 127.0.0.1"}}]},"parent_tool_use_id":null}')
                    $next = ReadMessage
                    $script:queuedSteer = $next.uuid
                    Lifecycle $next.uuid 'queued'
                    while (-not $script:cancelledSteer) {
                        $c = ReadLineMsg
                        if ($c.type -eq 'control_request') { HandleControl $c }
                    }
                    Emit ('{"type":"user","message":{"role":"user","content":[{"tool_use_id":"tu_sc","type":"tool_result","content":"pong"}]},"parent_tool_use_id":null,"session_id":"' + $sessionId + '"}')
                    Say 'after'
                    DoneFor $m.uuid
                }
                'steernext' {
                    # Measured on 2.1.307: with no tool boundary left, the turn ends first (result for the first message)
                    # and the queued message then runs as the next turn
                    StartOwn $m
                    Say 'counting'
                    $next = ReadMessage
                    Lifecycle $next.uuid 'queued'
                    DoneFor $m.uuid
                    Start-Sleep -Milliseconds 400
                    StartOwn $next
                    Say ('next:' + (UserText $next))
                    DoneFor $next.uuid
                }
                'steerstop' {
                    # Measured on 2.1.307: stop while the inserted message is still queued: the interrupt stops the first
                    # part (still_queued), the queued message starts next and has to be stopped again
                    StartOwn $m
                    Say 'working'
                    $next = ReadMessage
                    Lifecycle $next.uuid 'queued'
                    WaitInterrupt
                    StoppedFor $m.uuid
                    StartOwn $next
                    WaitInterrupt
                    StoppedFor $next.uuid
                }
                'elicit' {
                    # a form, then a url
                    Init
                    Elicit 'e1' '{"subtype":"elicitation","mcp_server_name":"probe","message":"fill in","mode":"form","requested_schema":{"type":"object","properties":{"name":{"type":"string","title":"Name"},"agree":{"type":"boolean"},"count":{"type":"integer"}},"required":["name"]},"property_order":["name","agree","count"]}'
                    Elicit 'e2' '{"subtype":"elicitation","mcp_server_name":"probe","message":"open it","mode":"url","url":"https://example.test/a","elicitation_id":"el-1"}'
                    Say ($script:log -join ' | ')
                    Done
                }
                'elicitstop' {
                    # the user stops while the form is waiting: the host answers cancel, then sends interrupt
                    Init
                    Elicit 'e3' '{"subtype":"elicitation","mcp_server_name":"probe","message":"fill in","mode":"form","requested_schema":{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}}'
                    while (-not $script:interrupted) {
                        $x = ReadLineMsg
                        if ($x.type -eq 'control_request') { HandleControl $x }
                    }
                    $script:interrupted = $false
                    Say ($script:log -join ' | ')
                    Emit ('{"type":"result","subtype":"error_during_execution","is_error":true,"num_turns":1,"errors":[],"session_id":"' + $sessionId + '"}')
                }
                default {
                    Init
                    Say 'ok'
                    Done
                }
            }
        }
        """";

    private readonly TempDirectory temp = new("fake-claude-session");
    private readonly string script;
    private readonly string work;
    private readonly string config;
    private readonly string sessionId = Guid.NewGuid().ToString();

    public ClaudeSessionFakeCliTests()
    {
        script = temp.WriteFile("fake-claude.ps1", FakeClaude);
        work = temp.CreateDirectory("work");
        config = temp.CreateDirectory("cfg");
    }

    public void Dispose() => temp.Dispose();

    /// <summary>對話設定：假 CLI 的情境與環境變數；CLAUDE_CONFIG_DIR 指到暫存資料夾（找逐字稿時不碰使用者的 ~/.claude）</summary>
    private AgentSessionSettings Settings(string scenario, bool started = false, AgentPermissionMode mode = AgentPermissionMode.Ask,
        string? workingDirectory = null, IReadOnlyDictionary<string, string>? env = null)
        => new(
            sessionId,
            started,
            new AgentLauncher(PowerShell, new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }),
            "fake",
            e =>
            {
                e["FAKE_SCENARIO"] = scenario;
                e["CLAUDE_CONFIG_DIR"] = config;
                foreach (var (key, value) in env ?? new Dictionary<string, string>())
                    e[key] = value;
            },
            null,
            null,
            null,
            mode,
            workingDirectory ?? work,
            temp.Root,
            "測試說明",
            UseLocalCustomizations: true,
            HistoryRecap: "（節錄）\n");

    private static AgentTurnInput Input(string text) => new(text, Array.Empty<AgentAttachment>());

    /// <summary>會話事件的收集器（事件在程序讀取執行緒發出）</summary>
    private sealed class EventLog : IDisposable
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

        public List<AgentEvent> All { get; } = new();

        /// <summary>讀事件直到 until 成立（含該事件）；handle 可以回應核准卡</summary>
        public async Task<List<AgentEvent>> UntilAsync(Func<AgentEvent, bool> until, Func<AgentEvent, Task>? handle = null, double seconds = 30)
        {
            var collected = new List<AgentEvent>();
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                if (!queue.TryTake(out var ev, 100))
                    continue;
                collected.Add(ev);
                All.Add(ev);
                if (handle != null)
                    await handle(ev);
                if (until(ev))
                    return collected;
            }
            throw new TimeoutException("假的 CLI 沒有在時限內完成；收到：" + string.Join("、", collected.Select(e => e.GetType().Name)));
        }

        /// <summary>等一小段時間，取出之後到達的事件</summary>
        public List<AgentEvent> Drain(int waitMilliseconds)
        {
            var result = new List<AgentEvent>();
            var deadline = DateTime.UtcNow.AddMilliseconds(waitMilliseconds);
            while (DateTime.UtcNow < deadline)
            {
                if (queue.TryTake(out var ev, 50))
                {
                    result.Add(ev);
                    All.Add(ev);
                }
            }
            return result;
        }

        public void Dispose() => session.EventReceived -= handler;
    }

    /// <summary>送一則訊息，核准卡依 decide 回應，跑到這一輪結束</summary>
    private static async Task<List<AgentEvent>> TurnAsync(IAgentSession session, EventLog log, AgentSessionSettings settings,
        Func<AgentApprovalEvent, AgentApprovalChoice> decide, string text = "開始")
    {
        await session.SendAsync(Input(text), settings);
        return await log.UntilAsync(e => e is AgentTurnCompletedEvent, async ev =>
        {
            if (ev is AgentApprovalEvent approval)
                await session.RespondApprovalAsync(approval.RequestKey, decide(approval), null);
        });
    }

    private static string Text(IEnumerable<AgentEvent> events) => events.OfType<AgentTextCompletedEvent>().Single().Text;

    [Fact]
    public async Task 檔案編輯按本對話都允許_改為自動接受編輯且畫面跟著改_CLI還會詢問的寫入照樣跳卡_切回每步詢問會送出切換()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);

        var first = await TurnAsync(session, log, Settings("editmode"),
            a => a.RequestKey == "r1" ? AgentApprovalChoice.AllowForSession : AgentApprovalChoice.Allow);

        var approvals = first.OfType<AgentApprovalEvent>().ToList();
        approvals.Select(a => a.RequestKey).Should().Equal(new[] { "r1", "r2" },
            "r2 寫的是使用者的 .claude 設定檔：CLI 的 safety check 仍送來詢問，不能因為按過 r1 就自動放行");
        approvals[0].AllowSessionLabel.Should().Be("本對話改為自動接受編輯（檔案修改與 mkdir、rm、mv 等檔案指令不再詢問）");
        first.OfType<AgentPermissionModeChangedEvent>().Last().Mode.Should().Be(AgentPermissionMode.AcceptEdits, "CLI 已切到自動接受編輯，畫面上的模式要跟著改");
        Text(first).Should().Be("turn1:r1=allow,mode=acceptEdits,r2=allow", "setMode 不放進 updatedPermissions，改以 set_permission_mode 明確切換");

        // 使用者把模式切回每步詢問：會話記得 CLI 現在是自動接受編輯，送出前要切回去
        var second = await TurnAsync(session, log, Settings("editmode", started: true), _ => AgentApprovalChoice.Allow);
        Text(second).Should().Be("turn2:r1=allow,mode=acceptEdits,r2=allow,mode=default");
    }

    [Fact]
    public async Task 上下文用量_每輪結束後問get_context_usage_發自動壓縮門檻與CLI採用的視窗()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);

        await TurnAsync(session, log, Settings("plain"), _ => AgentApprovalChoice.Allow);
        var after = await log.UntilAsync(e => e is AgentContextUsageEvent);
        after.OfType<AgentContextUsageEvent>().Single().Should().Be(new AgentContextUsageEvent(null, 200_000, 167_000),
            "視窗取 rawMaxTokens、門檻取 autoCompactThreshold；用量（totalTokens 是 CLI 自己估的）不用");

        await TurnAsync(session, log, Settings("plain", started: true), _ => AgentApprovalChoice.Allow, "第二則");
        (await log.UntilAsync(e => e is AgentContextUsageEvent)).OfType<AgentContextUsageEvent>().Should().ContainSingle("每一輪結束都再問一次");
    }

    [Fact]
    public async Task 上下文用量_舊版不認得get_context_usage_不發事件也不影響下一輪()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        var env = new Dictionary<string, string> { ["FAKE_CTX"] = "error" };

        await TurnAsync(session, log, Settings("plain", env: env), _ => AgentApprovalChoice.Allow);
        log.Drain(800).OfType<AgentContextUsageEvent>().Should().BeEmpty();
        var second = await TurnAsync(session, log, Settings("plain", started: true, env: env), _ => AgentApprovalChoice.Allow, "第二則");
        second.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
    }

    [Fact]
    public async Task CLI自己改了權限模式_以回報的模式為準_之後切回每步詢問會送出切換()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);

        var first = await TurnAsync(session, log, Settings("statusmode"), _ => AgentApprovalChoice.Allow);
        first.OfType<AgentPermissionModeChangedEvent>().Last().Mode.Should().Be(AgentPermissionMode.Plan,
            "代理呼叫 EnterPlanMode 並被核准：CLI 以 system/status 回報規劃模式");

        var second = await TurnAsync(session, log, Settings("statusmode", started: true), _ => AgentApprovalChoice.Allow);
        Text(second).Should().Be("turn2:mode=default", "CLI 在規劃模式、使用者選的是每步詢問：不能拿過期的模式比對而略過切換");
    }

    [Fact]
    public async Task 本對話都允許的規則與資料夾_程序重開後帶回_API計費提醒一個對話只提示一次()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        var env = new Dictionary<string, string> { ["FAKE_API_KEY_SOURCE"] = "ANTHROPIC_API_KEY" };

        var first = await TurnAsync(session, log, Settings("rules", env: env), _ => AgentApprovalChoice.AllowForSession);
        first.OfType<AgentApprovalEvent>().Single().AllowSessionLabel.Should().Be(@"本對話都允許 Bash(npm test:*)、存取 C:\data");
        JArray.Parse(Text(first)).Select(t => t.ToString()).Should().NotContain("--allowedTools");

        // 閒置回收：程序關掉，CLI 記憶體裡的 session 規則跟著消失；下次送出以 --resume 開新程序
        await session.CloseAsync();
        var second = await TurnAsync(session, log, Settings("rules", started: true, env: env), _ => AgentApprovalChoice.Allow);

        var args = JArray.Parse(Text(second)).Select(t => t.ToString()).ToList();
        args.Should().Contain("--resume");
        args[args.IndexOf("--allowedTools") + 1].Should().Be("Bash(npm test:*)", "同意過的規則以 --allowedTools 帶回");
        args[args.IndexOf(@"C:\data") - 1].Should().Be("--add-dir", "同意過的資料夾以 --add-dir 帶回");
        log.All.OfType<AgentNoticeEvent>().Count(n => n.Message.Contains("API Key")).Should().Be(1, "init 每輪都送、程序也重開過，同一個對話只提示一次");
    }

    [Fact]
    public async Task 換了工作資料夾_以逐字稿路徑續接並告訴代理新的資料夾_同一個資料夾照常以Id續接()
    {
        var a = temp.CreateDirectory("workA");
        var b = temp.CreateDirectory("workB");
        var transcript = temp.WriteFile(Path.Combine("cfg", "projects", ClaudeStreamRules.ProjectFolderName(a), sessionId + ".jsonl"), "{}\n");

        using (var session = new ClaudeAgentSession())
        using (var log = new EventLog(session))
        {
            var moved = JObject.Parse(Text(await TurnAsync(session, log, Settings("resume", started: true, workingDirectory: b), _ => AgentApprovalChoice.Allow, "繼續")));
            moved.Value<string>("resume").Should().Be(transcript, "CLI 依建立 session 時的資料夾存逐字稿，換了資料夾要帶逐字稿路徑才續接得到");
            moved.Value<string>("text").Should().Be(ClaudeStreamRules.BuildFolderChangedNote(b) + "繼續", "CLI 沿用舊的系統提示，要告訴代理新的資料夾");

            await session.CloseAsync();
            var again = JObject.Parse(Text(await TurnAsync(session, log, Settings("resume", started: true, workingDirectory: b), _ => AgentApprovalChoice.Allow, "再一次")));
            again.Value<string>("resume").Should().Be(transcript, "CLI 之後仍寫回原本的逐字稿，重開程序也要以路徑續接");
            again.Value<string>("text").Should().Be("再一次", "已經在新資料夾跑過，不重複說明");
        }

        using (var session = new ClaudeAgentSession())
        using (var log = new EventLog(session))
        {
            var same = JObject.Parse(Text(await TurnAsync(session, log, Settings("resume", started: true, workingDirectory: a), _ => AgentApprovalChoice.Allow, "原資料夾")));
            same.Value<string>("resume").Should().Be(sessionId);
            same.Value<string>("text").Should().Be("原資料夾");
        }
    }

    [Fact]
    public async Task 續接時先收到背景工作通知的Result_不當成這一輪結束()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);

        var events = await TurnAsync(session, log, Settings("notification", started: true), _ => AgentApprovalChoice.Allow);

        Text(events).Should().Be("real-reply", "要等到使用者這則訊息真正的回覆才算這一輪結束");
        events.FindIndex(e => e is AgentTextCompletedEvent).Should().BeLessThan(events.FindIndex(e => e is AgentTurnCompletedEvent));
        log.Drain(300).OfType<AgentTurnCompletedEvent>().Should().BeEmpty("這一輪只結束一次");
        session.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task 切換權限模式被拒改重開程序_重開失敗時回報錯誤且不會卡在執行中()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        await TurnAsync(session, log, Settings("modereject"), _ => AgentApprovalChoice.Allow);

        TempDirectory.DeleteWithRetry(work);
        Directory.Exists(work).Should().BeFalse("假 CLI 啟動後已把自己的目前資料夾移開，工作資料夾可以刪除");

        await FluentActions.Awaiting(() => session.SendAsync(Input("切到完全自動"), Settings("modereject", started: true, mode: AgentPermissionMode.FullAccess)))
            .Should().ThrowAsync<DirectoryNotFoundException>("CLI 拒絕切換模式、重開程序又失敗，要讓中樞回報送出失敗");
        session.IsRunning.Should().BeFalse("送出失敗後可以再送，停止鍵也不會卡住");
    }

    [Fact]
    public async Task 續接失敗時大訊息寫入先失敗_由自動改開新對話接手_不回報送出失敗也只結束一次()
    {
        // 超過管線緩衝的訊息：寫入會卡到假 CLI 關掉 stdin 才失敗，比 result 早到
        var image = temp.WriteFile("big.png", new string('x', 256 * 1024));
        var attachment = new AgentAttachment { FileName = "big.png", StoredPath = image, Kind = AgentAttachmentKind.Image, ModelImagePaths = { image } };
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);

        await session.SendAsync(new AgentTurnInput("看圖", new[] { attachment }), Settings("resumerace", started: true));
        var events = await log.UntilAsync(e => e is AgentTurnCompletedEvent, seconds: 60);

        events.OfType<AgentNoticeEvent>().Should().Contain(n => n.Message.Contains("找不到原本的 Claude 對話紀錄"));
        events.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        Text(events).Should().Be("ok", "改開的新對話把這則訊息跑完");
        log.Drain(300).OfType<AgentTurnCompletedEvent>().Should().BeEmpty("這一輪只結束一次");
        session.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task 上一輪停止排的看門狗_不會提早砍掉下一輪的程序()
    {
        using var session = new ClaudeAgentSession { InterruptWatchdogDelay = TimeSpan.FromSeconds(4) };
        using var log = new EventLog(session);
        var settings = Settings("slowstop", env: new Dictionary<string, string> { ["FAKE_STOP_DELAY_MS"] = "3000" });

        await session.SendAsync(Input("第 1 輪"), settings);
        var clock = Stopwatch.StartNew();
        await session.InterruptAsync();   // 第 1 輪的看門狗：4 秒後到期
        (await log.UntilAsync(e => e is AgentTurnCompletedEvent)).OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Interrupted);

        var wait = TimeSpan.FromSeconds(2) - clock.Elapsed;
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait);
        await session.SendAsync(Input("第 2 輪"), settings with { SessionStarted = true });
        await session.InterruptAsync();   // CLI 要 3 秒才收尾：第 1 輪的看門狗會在這之前到期
        var second = await log.UntilAsync(e => e is AgentTurnCompletedEvent, seconds: 20);

        second.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Interrupted);
        session.HasLiveProcess.Should().BeTrue("程序不能被上一輪的看門狗強制結束");
        second.Concat(log.Drain(200)).OfType<AgentProcessExitedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task CLI撤回尚未回應的請求_卡片改為已失效_之後按下去也不回應()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        await session.SendAsync(Input("開始"), Settings("cancel"));

        var events = await log.UntilAsync(e => e is AgentTurnCompletedEvent, async ev =>
        {
            switch (ev)
            {
                case AgentRequestResolvedEvent resolved:
                    // 使用者在卡片改為失效之前按了允許：不能再送出回應
                    await session.RespondApprovalAsync(resolved.RequestKey, AgentApprovalChoice.Allow, null);
                    break;
                case AgentApprovalEvent { RequestKey: "r2" } approval:
                    await session.RespondApprovalAsync(approval.RequestKey, AgentApprovalChoice.Allow, null);
                    break;
            }
        });

        events.OfType<AgentApprovalEvent>().Select(a => a.RequestKey).Should().Equal("r1", "r2");
        events.OfType<AgentRequestResolvedEvent>().Single().RequestKey.Should().Be("r1");
        Text(events).Should().Be("r2=allow", "CLI 撤回的 r1 不再送出回應");
    }

    // ───── MCP 的 elicitation、MCP 狀態與操作、旁支提問（實測 2.1.310，tools/probes/claude_cli3_probe.py） ─────

    [Fact]
    public async Task MCP的elicitation_表單畫成問卷_答案轉成JSON值_網址題略過回decline()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        await session.SendAsync(Input("開始"), Settings("elicit"));

        var events = await log.UntilAsync(e => e is AgentTurnCompletedEvent, async ev =>
        {
            if (ev is AgentQuestionEvent { RequestKey: "e1" })
                await session.AnswerQuestionAsync("e1", new Dictionary<string, IReadOnlyList<string>> { ["name"] = ["neo"], ["agree"] = ["不填"], ["count"] = [" 3 "] });
            else if (ev is AgentQuestionEvent { RequestKey: "e2" })
                await session.DeclineQuestionAsync("e2");
        });

        var questions = events.OfType<AgentQuestionEvent>().ToList();
        questions.Select(q => q.Source).Should().Equal("MCP：probe", "MCP：probe");
        questions[0].Questions.Select(q => q.Id).Should().Equal("name", "agree", "count");
        questions[1].Detail.Should().EndWith("<https://example.test/a>");
        Text(events).Should().Be("""e1={"action":"accept","content":{"name":"neo","count":3}} | e2={"action":"decline"}""");
    }

    [Fact]
    public async Task MCP的elicitation_按停止回cancel_不是拒絕()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        await session.SendAsync(Input("開始"), Settings("elicitstop"));

        var events = await log.UntilAsync(e => e is AgentTurnCompletedEvent, async ev =>
        {
            if (ev is AgentQuestionEvent)
                await session.InterruptAsync();
        });

        Text(events).Should().Be("""e3={"action":"cancel"}""");
        events.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Interrupted);
    }

    [Fact]
    public async Task 旁支提問與MCP操作_沒有程序時以續接開一個_授權後問得到連上_之後送出沿用同一個程序()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        var settings = Settings("default");

        (await session.AskSideQuestionAsync("what now?", settings)).Should().Be(new AgentSideAnswer("side:what now?", null));
        session.HasLiveProcess.Should().BeTrue("沒有程序時開一個（不送訊息）");

        var status = await session.GetMcpStatusAsync(settings);
        status.Error.Should().BeNull();
        status.Servers.Should().Equal(
            new AgentMcpServer("probe", "needs-auth", null, "http", "user", null),
            new AgentMcpServer("broken", "failed", "Connection closed", "stdio", "project", null));

        (await session.McpActionAsync("probe", AgentMcpAction.Authenticate, settings))
            .Should().Be(new AgentMcpActionResult(true, null, "http://127.0.0.1:9/authorize?state=x"));
        // 瀏覽器回到 CLI 的本機回呼之後（假 CLI 授權後就算連上）再問：連上了
        (await session.GetMcpStatusAsync(settings)).Servers![0].Status.Should().Be("connected");
        (await session.McpActionAsync("nope", AgentMcpAction.Reconnect, settings)).Should().Be(new AgentMcpActionResult(false, "Server not found: nope"));

        var turn = await TurnAsync(session, log, settings, _ => AgentApprovalChoice.Allow);
        Text(turn).Should().Be("ok");
        log.All.OfType<AgentProcessStartedEvent>().Should().ContainSingle("送出沿用旁支提問開的程序");
    }

    // ───── Remote Control ─────

    private static readonly AgentRemoteControlOptions Remote = new("報表整理", null);

    private static async Task WaitForAsync(Func<bool> condition, string because, double seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("沒有等到：" + because);
            await Task.Delay(50);
        }
    }

    private static Task<List<AgentEvent>> Turn(IAgentSession session, EventLog log, AgentSessionSettings settings)
        => TurnAsync(session, log, settings, _ => AgentApprovalChoice.Allow);

    [Fact]
    public async Task RemoteControl_啟動時開啟並回報遠端工作階段_程序重開後接回同一個()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        var settings = Settings("rc") with { RemoteControl = Remote };

        var first = await Turn(session, log, settings);

        var opened = first.OfType<AgentRemoteControlEvent>().Single();
        opened.BridgeSessionId.Should().StartWith("cse_");
        opened.SessionUrl.Should().Be("https://claude.ai/code/session_" + opened.BridgeSessionId);
        Text(first).Should().Be("turn1:rc=on:報表整理:", "initialize 之後、使用者的訊息之前開啟；第一次沒有可接回的工作階段");
        await WaitForAsync(() => session.HasRemoteControl, "連上後算開著（閒置不回收）");

        // 換模型重開程序：接回剛才的遠端工作階段（手機上一直是同一個）
        var second = await Turn(session, log, settings with { SessionStarted = true, Model = "sonnet" });

        Text(second).Should().Be($"turn1:rc=on:報表整理:{opened.BridgeSessionId}");
        second.OfType<AgentRemoteControlEvent>().Single().BridgeSessionId.Should().Be(opened.BridgeSessionId);
    }

    [Fact]
    public async Task 工作階段名稱_啟動時照對話標題命名_改名立即送給活著的程序_CLI回送的名稱轉成事件()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);

        var first = await Turn(session, log, Settings("rc") with { Title = "報表整理" });

        Text(first).Should().Be("turn1:rename=報表整理", "initialize 之後、使用者的訊息之前以對話標題命名");
        first.OfType<AgentSessionTitleEvent>().Select(e => e.Title).Should().Equal("報表整理");

        await session.RenameAsync("月報");
        await log.UntilAsync(e => e is AgentSessionTitleEvent { Title: "月報" }, seconds: 10);

        var second = await Turn(session, log, Settings("rc", started: true) with { Title = "月報" });
        Text(second).Should().Be("turn2:rename=報表整理,rename=月報", "同一個程序：改名只送一次，送出不重送");
    }

    [Fact]
    public async Task 工作階段名稱_開著RemoteControl時連上後再命名一次_沒有標題就不送()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        var settings = Settings("rc") with { RemoteControl = Remote, Title = "月報" };

        var first = await Turn(session, log, settings);
        // 等兩次改名的回送都到（開啟成功後那次可能晚於第一輪的訊息），第二輪的紀錄才完整
        for (int titles = first.OfType<AgentSessionTitleEvent>().Count(); titles < 2; titles++)
            await log.UntilAsync(e => e is AgentSessionTitleEvent, seconds: 10);
        session.HasRemoteControl.Should().BeTrue();
        var second = await Turn(session, log, settings with { SessionStarted = true });

        var notes = Text(second)["turn2:".Length..].Split(',');
        notes.Should().Contain("rc=on:報表整理:");
        notes.Count(n => n == "rename=月報").Should().Be(2, "啟動時一次、Remote Control 開啟成功後一次（接回的遠端工作階段也照對話標題命名）");

        using var untitled = new ClaudeAgentSession();
        using var untitledLog = new EventLog(untitled);
        Text(await Turn(untitled, untitledLog, Settings("rc") with { RemoteControl = Remote })).Should().Be("turn1:rc=on:報表整理:", "沒有標題：不送 rename_session");
    }

    [Fact]
    public async Task RemoteControl_設定沒開就不送_沒有程序時切換什麼都不做()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        await session.SetRemoteControlAsync(Remote);

        var events = await Turn(session, log, Settings("rc"));

        Text(events).Should().Be("turn1:", "設定沒開：不送 remote_control（沒有程序時的切換也不留到下次）");
        events.OfType<AgentRemoteControlEvent>().Should().BeEmpty();
        session.HasRemoteControl.Should().BeFalse();
    }

    [Fact]
    public async Task RemoteControl_沒開過時接回中樞記的工作階段_換了帳戶不接回自己記的()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        var settings = Settings("rc") with { RemoteControl = new AgentRemoteControlOptions("報表整理", "cse_saved") };

        Text(await Turn(session, log, settings)).Should().Be("turn1:rc=on:報表整理:cse_saved", "app 重開後的新會話用對話裡記的 id");

        // 換帳戶：中樞清掉記的 id；會話自己記的屬於原帳戶，也不能拿去接回
        var other = settings with { SessionStarted = true, AccountKey = "other", RemoteControl = Remote };
        Text(await Turn(session, log, other)).Should().Be("turn1:rc=on:報表整理:");
    }

    [Fact]
    public async Task RemoteControl_設定切換立即套用到活著的程序_關掉後不算開著_再開接回同一個_狀態沒變不重送()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        var settings = Settings("rc") with { RemoteControl = Remote };
        var bridge = (await Turn(session, log, settings)).OfType<AgentRemoteControlEvent>().Single().BridgeSessionId;
        await WaitForAsync(() => session.HasRemoteControl, "開著");

        await session.SetRemoteControlAsync(null);
        session.HasRemoteControl.Should().BeFalse("送出關閉就不再算開著，閒置回收照常");
        await session.SetRemoteControlAsync(null);
        await session.SetRemoteControlAsync(Remote);
        await WaitForAsync(() => session.HasRemoteControl, "再開啟");
        await session.SetRemoteControlAsync(Remote);

        Text(await Turn(session, log, settings with { SessionStarted = true }))
            .Should().Be($"turn2:rc=on:報表整理:,rc=off,rc=on:報表整理:{bridge}", "已經關了、已經開著都不重送；再開接回同一個工作階段");
    }

    [Fact]
    public async Task RemoteControl_開不起來時提示一次_程序重開再失敗不重複提示_也不算開著()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        var settings = Settings("rc", env: new Dictionary<string, string> { ["FAKE_RC"] = "fail" }) with { RemoteControl = Remote };

        var first = await Turn(session, log, settings);

        first.OfType<AgentRemoteControlFailedEvent>().Single().Message.Should().Contain("Remote Control 沒有開啟").And.Contain("not enabled for this account");
        first.OfType<AgentRemoteControlEvent>().Should().BeEmpty();
        session.HasRemoteControl.Should().BeFalse("開不起來的程序閒置時照常回收");

        var second = await Turn(session, log, settings with { SessionStarted = true, Model = "sonnet" });
        Text(second).Should().Be("turn1:rc=on:報表整理:", "每次啟動程序都再試；失敗時沒有工作階段可接回");
        second.Concat(log.Drain(300)).OfType<AgentRemoteControlFailedEvent>().Should().BeEmpty("一個對話只提示一次");
    }

    [Fact]
    public async Task RemoteControl_接回失敗時不接回重送一次_不提示()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        var settings = Settings("rc", env: new Dictionary<string, string> { ["FAKE_RC"] = "failreattach" })
            with { RemoteControl = new AgentRemoteControlOptions("報表整理", "cse_gone") };

        var first = await Turn(session, log, settings);
        var opened = first.OfType<AgentRemoteControlEvent>().SingleOrDefault()
                     ?? (await log.UntilAsync(e => e is AgentRemoteControlEvent)).OfType<AgentRemoteControlEvent>().Single();
        opened.BridgeSessionId.Should().NotBe("cse_gone");
        await WaitForAsync(() => session.HasRemoteControl, "重送後開著");

        var second = await Turn(session, log, settings with { SessionStarted = true });
        Text(second).Should().Be("turn2:rc=on:報表整理:cse_gone,rc=on:報表整理:");
        log.All.OfType<AgentRemoteControlFailedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task RemoteControl_連線斷掉且不會恢復時不算開著並提示一次()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        var settings = Settings("rc", env: new Dictionary<string, string> { ["FAKE_RC"] = "drop" }) with { RemoteControl = Remote };

        var events = await Turn(session, log, settings);

        events.OfType<AgentRemoteControlEvent>().Should().ContainSingle("開啟本身成功");
        events.OfType<AgentRemoteControlFailedEvent>().Single().Message.Should().Contain("Remote Control 連線中斷").And.Contain("network gone");
        session.HasRemoteControl.Should().BeFalse("斷線的程序閒置時照常回收，下次送出重開時再開");
    }

    [Fact]
    public async Task RemoteControl_開啟的回應還沒到就關掉_舊回應與之後的連線狀態都不算開著()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        var settings = Settings("rc");
        await Turn(session, log, settings);

        // 連續切換：開啟的回應（成功）與 ready、connected 都在關閉送出之後才到
        await session.SetRemoteControlAsync(Remote);
        await session.SetRemoteControlAsync(null);

        Text(await Turn(session, log, settings with { SessionStarted = true })).Should().Be("turn2:rc=on:報表整理:,rc=off");
        log.Drain(300);
        session.HasRemoteControl.Should().BeFalse("關閉之前送出的開啟，它的回應與連線狀態都不能讓程序變成開著（否則永遠不回收）");
    }

    [Fact]
    public async Task RemoteControl_暫時斷線時關掉設定_照樣送出關閉()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        var settings = Settings("rc", env: new Dictionary<string, string> { ["FAKE_RC"] = "flap" }) with { RemoteControl = Remote };
        await Turn(session, log, settings);
        log.Drain(300);
        session.HasRemoteControl.Should().BeFalse("暫時斷線不算開著");
        log.All.OfType<AgentRemoteControlFailedEvent>().Should().BeEmpty("暫時斷線不提示");

        await session.SetRemoteControlAsync(null);

        Text(await Turn(session, log, settings with { SessionStarted = true }))
            .Should().Be("turn2:rc=on:報表整理:,rc=off", "CLI 之後可能自己恢復連線：送過開啟就要送關閉");
    }

    [Fact]
    public async Task RemoteControl_程序啟動期間切換_新程序照切換後的值()
    {
        ClaudeAgentSession? off = null;
        off = new ClaudeAgentSession { BeforeProcessStartForTest = () => off!.SetRemoteControlAsync(null) };
        using (off)
        using (var log = new EventLog(off))
        {
            Text(await Turn(off, log, Settings("rc") with { RemoteControl = Remote })).Should().Be("turn1:", "送出時開著、啟動期間關掉：不開");
            off.HasRemoteControl.Should().BeFalse();
        }

        ClaudeAgentSession? on = null;
        on = new ClaudeAgentSession { BeforeProcessStartForTest = () => on!.SetRemoteControlAsync(Remote) };
        using (on)
        using (var log = new EventLog(on))
            Text(await Turn(on, log, Settings("rc"))).Should().Be("turn1:rc=on:報表整理:", "送出時關著、啟動期間打開：開");
    }

    // ───── app 更新後重開程序接回 Remote Control（設計 9.2、9.5）─────

    private AgentSessionSettings Reattach() => Settings("rc", started: true) with { RemoteControl = new AgentRemoteControlOptions("報表整理", "cse_saved") };

    [Fact]
    public async Task RemoteControl_重開程序接回_不送訊息不開始一輪_之後的送出沿用這個程序()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        var settings = Reattach();

        (await session.ReattachRemoteControlAsync(settings with { RemoteControl = null })).Should().BeFalse("設定沒開 Remote Control");
        (await session.ReattachRemoteControlAsync(settings with { SessionStarted = false })).Should().BeFalse("CLI 端還沒建立 session：沒有對話可以 --resume");
        session.HasLiveProcess.Should().BeFalse();

        (await session.ReattachRemoteControlAsync(settings)).Should().BeTrue();

        var opened = (await log.UntilAsync(e => e is AgentRemoteControlEvent)).OfType<AgentRemoteControlEvent>().Single();
        opened.BridgeSessionId.Should().Be("cse_saved", "接回原本的遠端工作階段");
        await WaitForAsync(() => session.HasRemoteControl, "連上後算開著（閒置不回收）");
        session.IsRunning.Should().BeFalse("只接回、不開始一輪");
        log.Drain(300);
        log.All.Should().NotContain(e => e is AgentTurnCompletedEvent || e is AgentTextCompletedEvent || e is AgentSessionStartedEvent, "不送訊息、不耗額度");
        (await session.ReattachRemoteControlAsync(settings)).Should().BeFalse("已有活著的程序");

        var events = await Turn(session, log, settings);

        Text(events).Should().Be("turn1:rc=on:報表整理:cse_saved", "之後的送出沿用接回的程序，不重送 remote_control");
        events.OfType<AgentProcessStartedEvent>().Should().BeEmpty("沒有另開程序");
    }

    [Fact]
    public async Task RemoteControl_重開程序接回途中關閉對話_程序起來後立刻關掉()
    {
        ClaudeAgentSession? session = null;
        session = new ClaudeAgentSession { BeforeProcessStartForTest = () => session!.CloseAsync() };
        using (session)
        {
            (await session.ReattachRemoteControlAsync(Reattach())).Should().BeFalse("app 結束或關掉對話：不留下沒人管的程序");
            session.HasLiveProcess.Should().BeFalse();
            session.HasRemoteControl.Should().BeFalse();
        }
    }

    [Fact]
    public async Task RemoteControl_重開程序接回途中送出_等它起來沿用同一個程序()
    {
        var settings = Reattach();
        Task? send = null;
        ClaudeAgentSession? session = null;
        session = new ClaudeAgentSession
        {
            BeforeProcessStartForTest = () =>
            {
                send ??= session!.SendAsync(Input("桌面送出"), settings);
                return Task.CompletedTask;
            },
        };
        using (session)
        using (var log = new EventLog(session))
        {
            (await session.ReattachRemoteControlAsync(settings)).Should().BeTrue();
            await send!.WaitAsync(TimeSpan.FromSeconds(30));
            var events = await log.UntilAsync(e => e is AgentTurnCompletedEvent);

            events.OfType<AgentProcessStartedEvent>().Should().ContainSingle("送出等接回的程序起來再沿用它，不會同時開兩個");
            Text(events).Should().Be("turn1:rc=on:報表整理:cse_saved");
        }
    }

    // ───── 從 claude.ai 或 Claude App 送來的訊息（--replay-user-messages，格式照 2.1.301 實測） ─────

    [Fact]
    public async Task 遠端訊息_閒置時送來開始新的一輪_訊息先於回覆_app的訊息每次帶新的uuid()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        var settings = Settings("remoteidle");

        var first = await Turn(session, log, settings);
        var uuid = Text(first)["uuid:".Length..];
        Guid.TryParse(uuid, out _).Should().BeTrue("app 送的訊息帶 uuid，CLI 以它回報 command_lifecycle 與 result 的 user_message_uuid");
        first.OfType<AgentRemoteUserMessageEvent>().Should().BeEmpty("app 自己的訊息回送時沒有 origin，不是遠端送來的");

        var remote = await log.UntilAsync(e => e is AgentTurnCompletedEvent);
        var message = remote.OfType<AgentRemoteUserMessageEvent>().Single();
        message.Should().Be(new AgentRemoteUserMessageEvent("phone: status?", 0, 0, StartsTurn: true));
        remote.IndexOf(message).Should().BeLessThan(remote.FindIndex(e => e is AgentTextCompletedEvent), "訊息先於回覆");
        Text(remote).Should().Be("remote reply");
        remote.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);

        var second = await Turn(session, log, settings with { SessionStarted = true });
        Text(second)["uuid:".Length..].Should().NotBe(uuid, "CLI 會略過 uuid 重複的訊息");
    }

    [Fact]
    public async Task 遠端訊息_先開始的遠端那一輪結束時app這則還在排隊_不算這一輪結束()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);

        await session.SendAsync(Input("開始"), Settings("remoterace"));
        var head = await log.UntilAsync(e => e is AgentTextCompletedEvent { Text: "R" });
        await Task.Delay(250);
        session.IsAtSafePoint.Should().BeFalse(
            "app 這則還排在遠端那一輪後面（command_lifecycle queued）：遠端那一輪的輸出不算 CLI 接手了它，這時重開，它只在 CLI 記憶體的佇列裡會不見");
        var events = head.Concat(await log.UntilAsync(e => e is AgentTurnCompletedEvent)).ToList();

        events.OfType<AgentRemoteUserMessageEvent>().Single().Should().Be(new AgentRemoteUserMessageEvent("phone first", 0, 0, StartsTurn: false),
            "app 這一輪已在執行中：遠端的訊息只顯示");
        events.OfType<AgentTextCompletedEvent>().Select(e => e.Text).Should().Equal(new[] { "R", "U" },
            "遠端那一輪的 result（user_message_uuid 是遠端那則）不能結束 app 這一輪，否則 app 這則的回覆會落在閒置的對話上");
        log.Drain(300).OfType<AgentTurnCompletedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task 遠端訊息_在工具結果之後併進進行中的一輪_只顯示_這一輪照常結束()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);

        var events = await Turn(session, log, Settings("remotemid"));

        events.OfType<AgentRemoteUserMessageEvent>().Single().Should().Be(new AgentRemoteUserMessageEvent("phone mid-turn", 0, 0, StartsTurn: false));
        events.OfType<AgentTextCompletedEvent>().Select(e => e.Text).Should().Equal("a", "b");
        events.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
    }

    [Fact]
    public async Task 遠端訊息_開始的一輪可以從app停止()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        await Turn(session, log, Settings("remotestop"));

        (await log.UntilAsync(e => e is AgentRemoteUserMessageEvent)).OfType<AgentRemoteUserMessageEvent>().Single().StartsTurn.Should().BeTrue();
        await Task.Delay(150);
        session.IsRunning.Should().BeTrue();
        session.IsAtSafePoint.Should().BeFalse("換版等遠端這一輪做完，不在中途重開");
        session.HasLiveBackgroundWork.Should().BeFalse("遠端開始的一輪不是背景工作（換版的顯示與接續說明會說錯）");
        await session.InterruptAsync();

        var end = await log.UntilAsync(e => e is AgentTurnCompletedEvent);
        end.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Interrupted,
            "遠端開始的一輪也當成執行中：停止送得出去，CLI 回的失敗改成已停止");
    }

    [Fact]
    public async Task 遠端訊息_那一輪還沒結束就從app送出_接手成為這一輪_遠端的result不算()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        await Turn(session, log, Settings("remotetakeover"));
        await log.UntilAsync(e => e is AgentRemoteUserMessageEvent);

        // 中樞還沒收到遠端訊息的事件就送出（還以為閒置）：不能丟「還在處理上一則」
        var events = await TurnAsync(session, log, Settings("remotetakeover", started: true), _ => AgentApprovalChoice.Allow, "接著問");

        events.OfType<AgentTextCompletedEvent>().Select(e => e.Text).Should().Equal(new[] { "R", "U:接著問" },
            "遠端那一輪的 result 不結束 app 這則；app 這則排在後面照常處理");
        log.Drain(300).OfType<AgentTurnCompletedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task 遠端訊息_app這則排在遠端那一輪後面時按停止_接著開始的這則再停一次()
    {
        // 看門狗拉長：沒有再停一次時假 CLI 會一直等中斷，測試以逾時失敗，而不是被看門狗殺掉後看起來也像已停止
        using var session = new ClaudeAgentSession { InterruptWatchdogDelay = TimeSpan.FromMinutes(5) };
        using var log = new EventLog(session);
        await Turn(session, log, Settings("remotestopqueued"));
        await log.UntilAsync(e => e is AgentRemoteUserMessageEvent);

        await session.SendAsync(Input("排在後面"), Settings("remotestopqueued", started: true));
        await session.InterruptAsync();

        var end = await log.UntilAsync(e => e is AgentTurnCompletedEvent, seconds: 10);
        end.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Interrupted,
            "中斷停的是遠端那一輪（CLI 回 still_queued）；接著開始的 app 這則要再停一次，不能照常跑完");
        session.IsRunning.Should().BeFalse();
        log.Drain(300).OfType<AgentTurnCompletedEvent>().Should().BeEmpty("遠端那一輪的 result 不算這一輪的");
    }

    [Fact]
    public async Task 遠端訊息_併進CLI自己開的通知那一輪_origin是通知的result也結束遠端這一輪()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        await Turn(session, log, Settings("remotenotify"));

        var events = await log.UntilAsync(e => e is AgentTurnCompletedEvent, seconds: 10);
        events.OfType<AgentIdleTurnStartedEvent>().Single().AfterBackgroundTask.Should().BeTrue("通知那一輪的 system/init：閒置時 CLI 自己開的一輪也標成執行中");
        events.OfType<AgentRemoteUserMessageEvent>().Single().StartsTurn.Should().BeTrue("通知那一輪改記成遠端的一輪：中樞照樣當成遠端開始的一輪");
        events.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed,
            "遠端那則已併進通知那一輪（看過它開始）：origin 是 task-notification 的 result 也是它的，不然會一直卡在執行中");
        session.IsRunning.Should().BeFalse();
        session.HasLiveBackgroundWork.Should().BeFalse();
    }

    // ───── 閒置時 CLI 自己開的一輪（背景工作完成的通知，E-11） ─────

    [Fact]
    public async Task 背景工作完成後CLI自己開的一輪_會話標成執行中並發事件_它的result結束這一輪()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        await Turn(session, log, Settings("notifyidle"));

        var head = await log.UntilAsync(e => e is AgentTextCompletedEvent { Text: "checking" }, seconds: 10);
        head.OfType<AgentIdleTurnStartedEvent>().Single().Should().Be(new AgentIdleTurnStartedEvent(AfterBackgroundTask: true));
        head.FindIndex(e => e is AgentIdleTurnStartedEvent).Should().BeLessThan(head.FindIndex(e => e is AgentTextCompletedEvent),
            "system/init 時就標成執行中，畫面在第一個輸出之前就轉圈");
        session.IsRunning.Should().BeTrue("畫面上回覆一直出來，對話要看得出在執行（以前一直是閒置，沒有執行中的動畫、停不下來）");
        session.IsAtSafePoint.Should().BeFalse("換版等這一輪做完");
        session.HasLiveBackgroundWork.Should().BeTrue("背景工作通知那一輪到 result 為止照舊算背景工作");

        var end = await log.UntilAsync(e => e is AgentTurnCompletedEvent, seconds: 10);
        Text(end).Should().Be("checked");
        end.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed,
            "這一輪就是 CLI 自己開的：origin 是 task-notification 的 result 也是它的結尾，不然會一直卡在執行中");
        session.IsRunning.Should().BeFalse();
        session.HasLiveBackgroundWork.Should().BeFalse();
        session.IsAtSafePoint.Should().BeTrue();
        log.Drain(300).OfType<AgentTurnCompletedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task 背景指令_開始時回報數量與工具_這一輪結束後還在_完成時回報0_接著CLI自己那一輪()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);

        var first = await Turn(session, log, Settings("bgtask"));

        var started = first.OfType<AgentBackgroundWorkEvent>().Should().ContainSingle("背景工作清單只變了一次（task_started 對到同一個工作，不重發）").Subject;
        started.Count.Should().Be(1);
        started.Tools.Should().ContainSingle().Which.Title.Should().Be("npm test", "以 tool_use_id 對到那張工具卡的說明");
        first.FindIndex(e => e is AgentBackgroundWorkEvent).Should().BeLessThan(first.FindIndex(e => e is AgentTurnCompletedEvent),
            "這一輪結束時中樞已經知道背景工作還在跑（完成的通知與畫面據此改成等背景工作回報）");
        session.IsRunning.Should().BeFalse();
        session.HasLiveBackgroundWork.Should().BeTrue();

        var own = await log.UntilAsync(e => e is AgentTurnCompletedEvent, seconds: 10);
        var done = own.OfType<AgentBackgroundWorkEvent>().Should().ContainSingle().Subject;
        done.Count.Should().Be(0);
        done.Tools.Should().BeEmpty();
        own.FindIndex(e => e is AgentBackgroundWorkEvent).Should().BeLessThan(own.FindIndex(e => e is AgentIdleTurnStartedEvent),
            "先拿掉等背景工作的樣貌，CLI 自己那一輪再照執行中顯示");
        Text(own).Should().Be("tests passed");
        log.Drain(300).OfType<AgentBackgroundWorkEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task 背景工作還在跑時關掉程序_回報0()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        await Turn(session, log, Settings("bgtask"));
        session.HasLiveBackgroundWork.Should().BeTrue();

        await session.CloseAsync();

        log.Drain(300).OfType<AgentBackgroundWorkEvent>().Should().ContainSingle().Which.Count.Should().Be(0, "程序關掉，背景工作跟著沒了");
        session.HasLiveBackgroundWork.Should().BeFalse();
    }

    [Fact]
    public async Task 背景工作完成後CLI自己開的一輪_可以從app停止()
    {
        using var session = new ClaudeAgentSession { InterruptWatchdogDelay = TimeSpan.FromMinutes(5) };
        using var log = new EventLog(session);
        await Turn(session, log, Settings("notifystop"));
        await log.UntilAsync(e => e is AgentTextCompletedEvent { Text: "checking" }, seconds: 10);

        await session.InterruptAsync();

        var end = await log.UntilAsync(e => e is AgentTurnCompletedEvent, seconds: 10);
        end.OfType<AgentTurnCompletedEvent>().Single().Should().Be(new AgentTurnCompletedEvent(AgentTurnOutcome.Interrupted, null, null),
            "停止送得出去（看門狗拉長，不是被它殺掉的），CLI 回的失敗改成已停止");
        session.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task 背景工作完成後CLI自己開的一輪_中樞還沒收到事件就送出_接手成為這一輪_通知那一輪的result不算()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        await Turn(session, log, Settings("notifytakeover"));
        await log.UntilAsync(e => e is AgentTextCompletedEvent { Text: "N" }, seconds: 10);

        // 不能丟「還在處理上一則」：中樞看到的還是閒置
        var events = await TurnAsync(session, log, Settings("notifytakeover", started: true), _ => AgentApprovalChoice.Allow, "接著問");

        Text(events).Should().Be("U:接著問", "通知那一輪的 result 不結束 app 這則；app 這則排在後面照常處理");
        events.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        session.IsRunning.Should().BeFalse();
        log.Drain(300).OfType<AgentTurnCompletedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task 背景工作完成後CLI自己開的一輪_執行中送出的訊息插得進去_插入的那則跑完才結束()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        await Turn(session, log, Settings("notifytakeover"));
        await log.UntilAsync(e => e is AgentTextCompletedEvent { Text: "N" }, seconds: 10);

        (await session.SteerAsync(Input("補一句"))).Should().BeTrue("CLI 自己開的一輪也是執行中：中樞照常插入");
        var events = await log.UntilAsync(e => e is AgentTurnCompletedEvent, seconds: 10);

        Text(events).Should().Be("U:補一句", "通知那一輪的 result 到的時候插入的那則還在排隊：不能結束這一輪");
        events.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        session.IsRunning.Should().BeFalse();
        log.Drain(300).OfType<AgentTurnCompletedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task 回送app自己的訊息時用了CLI另給的uuid_不記成別的指令_這一輪照常結束()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);

        await session.SendAsync(Input("開始"), Settings("replayforeign"));
        var events = await log.UntilAsync(e => e is AgentTurnCompletedEvent, seconds: 10);

        events.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed,
            "只記遠端送來的回送：記下 x-1 的話，user_message_uuid 是 x-1 的 result 會被當成別一輪，每一輪都卡住");
    }

    // ───── 執行中插入訊息（docs/protocol-notes.md「執行中插入訊息」） ─────

    [Fact]
    public async Task 插入訊息_工具結果之後併進同一輪_開始前不是安全點_只結束一次()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        (await session.SteerAsync(Input("閒置"))).Should().BeFalse("沒在執行：中樞照常送出");
        await session.SendAsync(Input("開始"), Settings("steermerge"));
        await log.UntilAsync(e => e is AgentTextCompletedEvent { Text: "working" });

        (await session.SteerAsync(Input("補充"))).Should().BeTrue();
        session.IsAtSafePoint.Should().BeFalse("插入的訊息開始之前只在 CLI 記憶體的佇列裡，這時重開它會不見");
        var events = await log.UntilAsync(e => e is AgentTurnCompletedEvent);

        events.OfType<AgentTextCompletedEvent>().Select(e => e.Text).Should().Equal(new[] { "merged:補充" }, "插入的訊息帶自己的 uuid、CLI 照常處理");
        events.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed,
            "併進同一輪：result 的 user_message_uuid 是開頭那則，但插入的那則已開始，算這一輪的結尾");
        session.IsRunning.Should().BeFalse();
        log.Drain(300).OfType<AgentTurnCompletedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task 插入訊息_工具執行中取消_CLI還沒讀走就還原_這一輪照常以開頭那則的result結束()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        await session.SendAsync(Input("開始"), Settings("steercancel"));
        await log.UntilAsync(e => e is AgentToolStartedEvent);

        var uuid = Guid.NewGuid().ToString();
        (await session.SteerAsync(Input("插入") with { MessageUuid = uuid })).Should().BeTrue();
        (await session.CancelSteerAsync(Guid.NewGuid().ToString())).Should().BeFalse("不是最近插入的那則");
        (await session.CancelSteerAsync(uuid)).Should().BeTrue();
        var events = await log.UntilAsync(e => e is AgentTurnCompletedEvent);

        events.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed,
            "取消了：這一輪的訊息回到開頭那則，它的 result 就是這一輪的結尾（不然會一直等插入那則的 result）");
        events.OfType<AgentTextCompletedEvent>().Select(e => e.Text).Should().Equal("after");
        session.IsRunning.Should().BeFalse();
        (await session.CancelSteerAsync(uuid)).Should().BeFalse("取消過了");
    }

    [Fact]
    public async Task 插入訊息_沒有工具邊界時前一則的result不結束這一輪_插入的那則跑完才結束()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        await session.SendAsync(Input("開始"), Settings("steernext"));
        await log.UntilAsync(e => e is AgentTextCompletedEvent { Text: "counting" });

        (await session.SteerAsync(Input("再一則"))).Should().BeTrue();
        var events = await log.UntilAsync(e => e is AgentTurnCompletedEvent);

        events.OfType<AgentTextCompletedEvent>().Select(e => e.Text).Should().Equal(new[] { "next:再一則" },
            "前一則的 result 到的時候插入的那則還在排隊：不能結束這一輪，否則它的回覆會落在閒置的對話上");
        events.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        log.Drain(300).OfType<AgentTurnCompletedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task 插入訊息_這一輪還在準備時先等訊息寫出_排在它後面()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);

        // 程序還在啟動、這一輪的訊息還沒寫出就插入：要等它寫出才寫，CLI 才會照順序處理
        var send = session.SendAsync(Input("開始"), Settings("steermerge"));
        var steered = await session.SteerAsync(Input("搶先"));
        await send;
        var events = await log.UntilAsync(e => e is AgentTurnCompletedEvent);

        steered.Should().BeTrue();
        events.OfType<AgentTextCompletedEvent>().Select(e => e.Text).Should().Equal("working", "merged:搶先");
        events.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
    }

    [Fact]
    public async Task 插入訊息_還在排隊時按停止_開始時再停一次_只回報一次已停止_停止後不再插入()
    {
        // 看門狗拉長：沒有再停一次時假 CLI 會一直等中斷，測試以逾時失敗，而不是被看門狗殺掉後看起來也像已停止
        using var session = new ClaudeAgentSession { InterruptWatchdogDelay = TimeSpan.FromMinutes(5) };
        using var log = new EventLog(session);
        await session.SendAsync(Input("開始"), Settings("steerstop"));
        await log.UntilAsync(e => e is AgentTextCompletedEvent { Text: "working" });

        (await session.SteerAsync(Input("插入"))).Should().BeTrue();
        await session.InterruptAsync();
        (await session.SteerAsync(Input("停止後"))).Should().BeFalse("按了停止：留在佇列跟著暫停");

        var end = await log.UntilAsync(e => e is AgentTurnCompletedEvent, seconds: 10);
        end.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Interrupted,
            "中斷停的是前一則（CLI 回 still_queued）；接著開始的插入那則要再停一次，不能照常跑完");
        session.IsRunning.Should().BeFalse();
        log.Drain(300).OfType<AgentTurnCompletedEvent>().Should().BeEmpty("前一則的 result 不算這一輪的結尾");
    }

    [Fact]
    public async Task 遠端訊息_在手機上按停止_這一輪是已停止不是失敗()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        await Turn(session, log, Settings("remotephonestop"));

        var end = (await log.UntilAsync(e => e is AgentTurnCompletedEvent, seconds: 10)).OfType<AgentTurnCompletedEvent>().Single();
        end.Outcome.Should().Be(AgentTurnOutcome.Interrupted, "CLI 插進「[Request interrupted by user…]」後的 error_during_execution 是被停止");
        end.ErrorMessage.Should().BeNull();
        session.IsRunning.Should().BeFalse();
    }
}
