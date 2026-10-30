using System.Globalization;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Tests.Agents;

namespace Myriagent.Tests.TestSupport;

/// <summary>
/// 假的 claude 登入（WP4.8；Windows PowerShell 腳本，啟動方式同 FakeCodexAppServer）：每次呼叫記進 calls.txt（<c>CLAUDE_CONFIG_DIR#參數</c>），
/// 登入完成記 login-done、拒絕寫檔記 refused-not-temp。腳本只用 ASCII（Windows PowerShell 讀沒有 BOM 的腳本用系統碼頁）。不連網、不開瀏覽器。
/// <list type="bullet">
/// <item><c>auth login [--claudeai] [--email x]</c>：等 go.flag（最多 120 秒，<see cref="Go"/>）；之後在 CLAUDE_CONFIG_DIR 寫 .credentials.json
/// （claudeAiOauth 只放到期欄位與 <see cref="Sentinel"/>）與 .claude.json（oauthAccount.emailAddress 是建構式給的信箱），exit 0</item>
/// <item><c>auth logout</c>：刪掉那兩個檔，以建構式給的結束碼結束</item>
/// <item><c>auth status</c>：依 .credentials.json 在不在印 <c>{"loggedIn":…,"email":…}</c></item>
/// <item><c>-p</c>（app 內登入的控制程序，ClaudeControlClient）：預設只記下、exit 0（app 退回主控台登入）；建構式給 inApp＝true 時照 stream-json 回
/// initialize、claude_authenticate（兩個 claude.com 網址，state 每次不同，記成 <c>state:…</c>）、claude_oauth_wait_for_completion（go.flag 出現＝本機的瀏覽器完成，
/// 或收到正確的授權碼才回成功）、claude_oauth_callback（授權碼是 <see cref="GoodCode"/> 而且 state 對得上才寫憑證、回成功，否則兩個請求都回 400）；
/// 每個請求記成 <c>control:子類型</c></item>
/// <item>其他參數：只記下，exit 0</item>
/// </list>
/// 只寫、只刪暫存資料夾底下的 CLAUDE_CONFIG_DIR（系統預設帳戶沒有 CLAUDE_CONFIG_DIR，登入一律 exit 4），絕不碰使用者真正的 ~/.claude
/// </summary>
internal sealed class FakeClaudeLogin : IDisposable
{
    /// <summary>登入完成後 .claude.json 的預設信箱</summary>
    public const string DefaultEmail = "claude@example.test";

    /// <summary>app 內登入（inApp）時 claude_oauth_callback 接受的授權碼</summary>
    public const string GoodCode = "good-code";

    private const string Script = """"
        $ErrorActionPreference = 'Stop'
        $utf8 = New-Object System.Text.UTF8Encoding $false
        $callsPath = Join-Path $PSScriptRoot 'calls.txt'
        $go = Join-Path $PSScriptRoot 'go.flag'
        $configDir = [string]$env:CLAUDE_CONFIG_DIR
        $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        function Record([string]$text) { [System.IO.File]::AppendAllText($callsPath, $text + [string][char]10) }
        function InTemp() {
            if ($configDir.Trim().Length -eq 0) { return $false }
            return [System.IO.Path]::GetFullPath($configDir).StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)
        }
        function WriteCreds() {
            [void][System.IO.Directory]::CreateDirectory($configDir)
            $now = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
            $expires = $now + 28800000
            $loginExpires = $now + 2592000000
            [System.IO.File]::WriteAllText($cred, '{"claudeAiOauth":{"accessToken":"' + $sentinel + '","expiresAt":' + $expires + ',"refreshTokenExpiresAt":' + $loginExpires + ',"subscriptionType":"max"}}', $utf8)
            $org = ''
            if ($orgUuid.Length -gt 0) { $org = ',"organizationUuid":"' + $orgUuid + '"' }
            [System.IO.File]::WriteAllText($config, '{"numStartups":1,"oauthAccount":{"accountUuid":"fake-account","emailAddress":"' + $email + '"' + $org + '}}', $utf8)
            Record 'login-done'
        }

        Record ($configDir + '#' + ($args -join ' '))
        $first = ''
        $second = ''
        if ($args.Count -ge 1) { $first = [string]$args[0] }
        if ($args.Count -ge 2) { $second = [string]$args[1] }
        $cred = ''
        $config = ''
        if ($configDir.Trim().Length -gt 0) {
            $cred = Join-Path $configDir '.credentials.json'
            $config = Join-Path $configDir '.claude.json'
        }

        if ($first -eq 'auth' -and $second -eq 'login') {
            if (-not (InTemp)) { Record 'refused-not-temp'; exit 4 }
            $deadline = (Get-Date).AddSeconds(120)
            while (-not (Test-Path -LiteralPath $go)) {
                if ((Get-Date) -gt $deadline) { exit 2 }
                Start-Sleep -Milliseconds 100
            }
            WriteCreds
            exit 0
        }

        if (($first -eq '-p') -and $inApp) {
            if (-not (InTemp)) { Record 'refused-not-temp'; exit 4 }
            $stdin = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $utf8)
            $out = [Console]::Out
            function Reply([string]$id, [string]$body) { $out.WriteLine('{"type":"control_response","response":{"subtype":"success","request_id":"' + $id + '","response":' + $body + '}}'); $out.Flush() }
            function Fail([string]$id, [string]$err) { $out.WriteLine('{"type":"control_response","response":{"subtype":"error","request_id":"' + $id + '","error":"' + $err + '"}}'); $out.Flush() }
            $state = 'st' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
            $waitId = ''
            $pending = $stdin.ReadLineAsync()
            while ($true) {
                if (-not $pending.IsCompleted) {
                    if (($waitId.Length -gt 0) -and (Test-Path -LiteralPath $go)) { WriteCreds; Reply $waitId '{}'; $waitId = '' }
                    Start-Sleep -Milliseconds 50
                    continue
                }
                $line = $pending.Result
                if ($line -eq $null) { exit 0 }
                $pending = $stdin.ReadLineAsync()
                $msg = ConvertFrom-Json $line
                $id = [string]$msg.request_id
                $sub = [string]$msg.request.subtype
                Record ('control:' + $sub)
                if ($sub -eq 'initialize') { Reply $id '{"commands":[]}' }
                elseif ($sub -eq 'claude_authenticate') {
                    Record ('state:' + $state)
                    Reply $id ('{"manualUrl":"https://claude.com/cai/oauth/authorize?code=true&state=' + $state + '","automaticUrl":"https://claude.com/cai/oauth/authorize?code=true&redirect_uri=http%3A%2F%2Flocalhost%3A1%2Fcallback&state=' + $state + '"}')
                }
                elseif ($sub -eq 'claude_oauth_wait_for_completion') { $waitId = $id }
                elseif ($sub -eq 'claude_oauth_callback') {
                    if (([string]$msg.request.authorizationCode -eq 'good-code') -and ([string]$msg.request.state -eq $state)) {
                        WriteCreds
                        Reply $id '{}'
                        if ($waitId.Length -gt 0) { Reply $waitId '{}'; $waitId = '' }
                    } else {
                        Fail $id 'Request failed with status code 400'
                        if ($waitId.Length -gt 0) { Fail $waitId 'Request failed with status code 400'; $waitId = '' }
                    }
                }
                else { Fail $id 'unsupported' }
            }
        }

        if ($first -eq 'auth' -and $second -eq 'logout') {
            if (InTemp) {
                if (Test-Path -LiteralPath $cred) { Remove-Item -LiteralPath $cred -Force }
                if (Test-Path -LiteralPath $config) { Remove-Item -LiteralPath $config -Force }
            } else {
                Record 'refused-not-temp'
            }
            exit $logoutExit
        }

        if ($first -eq 'auth' -and $second -eq 'status') {
            if (($cred.Length -gt 0) -and (Test-Path -LiteralPath $cred)) {
                [Console]::Out.WriteLine('{"loggedIn":true,"authMethod":"claude.ai","email":"' + $email + '","subscriptionType":"max"}')
            } else {
                [Console]::Out.WriteLine('{"loggedIn":false,"authMethod":"none"}')
            }
            exit 0
        }

        exit 0
        """";

    private readonly TempDirectory temp;

    /// <param name="email">登入完成後 .claude.json 的 oauthAccount.emailAddress（也是 auth status 的 email）</param>
    /// <param name="logoutExitCode">auth logout 的結束碼</param>
    /// <param name="orgUuid">.claude.json 的 oauthAccount.organizationUuid（null＝不寫這個欄位）</param>
    /// <param name="inApp">支援 app 內登入的控制請求（-p 的 stream-json）；預設不支援，app 退回主控台的 auth login</param>
    public FakeClaudeLogin(string email = DefaultEmail, int logoutExitCode = 0, string? orgUuid = null, bool inApp = false)
    {
        temp = new TempDirectory("fake-claude-login");
        Sentinel = MakeSentinel();
        Email = email;
        var script = temp.WriteFile("fake-claude.ps1",
            $"$email = '{email}'\n$logoutExit = {logoutExitCode}\n$sentinel = '{Sentinel}'\n$orgUuid = '{orgUuid}'\n$inApp = ${(inApp ? "true" : "false")}\n" + Script);
        Launcher = new AgentLauncher(CodexAgentSessionTests.PowerShell, new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script });
    }

    public AgentLauncher Launcher { get; }

    /// <summary>登入完成後的信箱</summary>
    public string Email { get; }

    /// <summary>
    /// 寫進假憑證的哨兵字串（D30：執行時才組出來——固定前綴＋固定種子的亂數＋example.test，不像任何真的權杖）：
    /// 測試以它掃描記錄檔、事件與 payload，確認憑證的內容沒有離開設定資料夾
    /// </summary>
    public string Sentinel { get; }

    private string CallsPath => Path.Combine(temp.Root, "calls.txt");

    /// <summary>有沒有啟動過（calls.txt 存在）</summary>
    public bool Started => File.Exists(CallsPath);

    /// <summary>calls.txt 的每一行（腳本可能正在寫：共用讀寫開檔，被占用就重試）</summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (!File.Exists(CallsPath))
                        return Array.Empty<string>();
                    using var stream = new FileStream(CallsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
                }
                catch (IOException) when (attempt < 20)
                {
                    Thread.Sleep(50);
                }
            }
        }
    }

    /// <summary>每次呼叫的參數（<c>#</c> 之後；refused-not-temp、login-done 這類標記行不算）</summary>
    public IReadOnlyList<string> Commands => Calls.Where(c => c.Contains('#', StringComparison.Ordinal)).Select(c => c[(c.IndexOf('#', StringComparison.Ordinal) + 1)..]).ToList();

    /// <summary>app 內登入這一次的 state（claude_authenticate 之後才有）</summary>
    public string? LastState => Calls.LastOrDefault(c => c.StartsWith("state:", StringComparison.Ordinal))?["state:".Length..];

    /// <summary>使用者在那台的瀏覽器完成了登入：假 claude 寫入憑證與 CLI 設定後結束</summary>
    public void Go() => File.WriteAllText(Path.Combine(temp.Root, "go.flag"), "");

    /// <summary>
    /// 那台的人在 app 外（終端機）以 claude auth login 登入這個設定資料夾（WP4.8 合併前審查 F5）：先放 go.flag，假 claude 直接寫入憑證與 CLI 設定後結束。
    /// 資料夾不在暫存資料夾底下時假 claude 拒絕（exit 4），這裡丟例外
    /// </summary>
    public async Task LoginOutsideAppAsync(string configDirectory)
    {
        Go();
        var result = await CliRunner.RunAsync(Launcher, ["auth", "login"], env => env["CLAUDE_CONFIG_DIR"] = configDirectory, TimeSpan.FromSeconds(30));
        if (result.TimedOut || result.ExitCode != 0)
            throw new InvalidOperationException($"假 claude 的 auth login 沒有成功（結束碼 {result.ExitCode}、逾時 {result.TimedOut}）：{result.Combined}");
    }

    /// <summary>等 calls.txt 符合條件（PowerShell 啟動要好幾百毫秒）</summary>
    public async Task WaitForAsync(Func<IReadOnlyList<string>, bool> condition, int timeoutMs = 30000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition(Calls))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("假 claude 的呼叫紀錄在時限內沒有符合條件：" + string.Join(" | ", Calls));
            await Task.Delay(50);
        }
    }

    public void Dispose() => temp.Dispose();

    /// <summary>固定前綴＋固定種子的 12 位元組亂數（十六進位）＋example.test</summary>
    private static string MakeSentinel()
    {
        var random = new Random(4808);
        var bytes = new byte[12];
        random.NextBytes(bytes);
        return "fakecred-" + string.Concat(bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture))) + ".example.test";
    }
}
