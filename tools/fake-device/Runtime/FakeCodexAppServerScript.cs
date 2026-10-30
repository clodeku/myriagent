using System.Globalization;

namespace FakeDevice;

/// <summary>
/// 假的 codex app-server 的腳本文字（Windows PowerShell 腳本，照 G5 實測的 codex 0.163.0 裝置碼回應形狀；WP3.4 第 11 步、WP4.8 D31）。
/// WP4.9 步 13（D11）從測試的 FakeCodexAppServer 搬來：測試與 fake-device 的 <c>--fake-codex &lt;情境&gt;</c> 用同一份產生器，腳本逐位元組相同
/// （FakeCodexAppServerScriptTests 以搬移前記下的 SHA-256 釘住）。純函式：只產生文字，不寫檔、不啟動程序；啟動方式見 <see cref="FakeLauncherRules"/>。
/// <para>
/// 腳本的行為：每次啟動與收到的每個請求都記進腳本旁邊的 <see cref="CallsFile"/>（args=…、codexHome=…、pid=…、initialize、account/login/start type=…、
/// account/login/cancel loginId=…、account/read、account/rateLimits/read、model/list…，stdin 結束時記 exit）；<see cref="CompleteFlagFile"/> 出現時送
/// account/login/completed {success:true}，之後 account/read 回已登入。腳本只用 ASCII（Windows PowerShell 讀沒有 BOM 的腳本用系統碼頁）。不連網、不耗額度。
/// </para>
/// <para>情境（<see cref="Scenarios"/>）：ok、evil_host（網址主機不是 auth.openai.com）、http、missing_fields（只有 type 與 loginId）、unsupported（-32601）、
/// complete_fail（一開始就送失敗的完成通知，錯誤文字含驗證碼）、init_fail（initialize 回錯誤：CodexAppServerClient.StartAsync 丟例外）</para>
/// <para>
/// 登出狀態依 CODEX_HOME 分開（WP4.8 D31）：<c>logout</c> 子指令記 logout，在 <c>$env:CODEX_HOME\fake-logged-out</c> 留標記（CODEX_HOME 空白時 exit 3、
/// 不留標記；不在暫存資料夾底下時記 refused-not-temp、exit 4，絕不寫到使用者真正的設定資料夾）；account/read 在旗標存在而且這個 CODEX_HOME
/// 沒有標記時才回已登入；完成登入時刪掉這個 CODEX_HOME 的標記。旗標本身是每個實例一個（complete.flag 在腳本的資料夾）
/// </para>
/// </summary>
internal static class FakeCodexAppServerScript
{
    /// <summary>假的驗證碼（10 字，同 G5 實測的格式）：測試以它掃描記錄檔與 payload</summary>
    public const string UserCode = "QWER-TY12Z";

    public const string LoginId = "0d8f3c3e-7a1b-4c0e-9f6b-5b2d7a9c1e44";

    public const string VerificationUrl = "https://auth.openai.com/codex/device";

    /// <summary>登入完成後 account/read 回的 email（沒指定時）</summary>
    public const string Email = "device@example.test";

    /// <summary>呼叫紀錄（腳本旁邊；一行一筆，LF）</summary>
    public const string CallsFile = "calls.txt";

    /// <summary>「使用者在別的裝置核准了」的旗標（腳本旁邊；出現時送完成通知）</summary>
    public const string CompleteFlagFile = "complete.flag";

    /// <summary>登出標記（CODEX_HOME 底下）</summary>
    public const string LoggedOutMarker = "fake-logged-out";

    /// <summary>認得的情境（見類別說明）</summary>
    public static IReadOnlyList<string> Scenarios { get; } = ["ok", "evil_host", "http", "missing_fields", "unsupported", "complete_fail", "init_fail"];

    /// <summary>腳本的檔名（同搬移前：fake-codex-{情境}.ps1）</summary>
    public static string FileName(string scenario)
    {
        CheckScenario(scenario);
        return $"fake-codex-{scenario}.ps1";
    }

    /// <summary>
    /// 腳本全文：前面幾行是這個實例的參數（情境、驗證碼、loginId、email、三個延遲與 logout 的結束碼），後面是固定的本體。
    /// 參數只收安全的值（情境在 <see cref="Scenarios"/> 裡、email 只有英數字與 <c>@._+-</c>、延遲不是負數）：它們放在 PowerShell 的單引號字串與數字裡
    /// </summary>
    /// <param name="scenario">情境（見類別說明）</param>
    /// <param name="email">登入完成後 account/read 回的 email（身分比對的測試給不同的信箱）</param>
    /// <param name="startDelayMs">account/login/start 等多久才回（模擬冷啟動很慢）</param>
    /// <param name="logoutExitCode">logout 子指令的結束碼</param>
    /// <param name="logoutDelayMs">logout 子指令留下標記之後等多久才結束（模擬登出逾時）</param>
    /// <param name="readDelayMs">account/read 等多久才回（模擬進行中的查詢；登入狀態在回應時才判斷）</param>
    public static string Build(string scenario, string email = Email, int startDelayMs = 0, int logoutExitCode = 0, int logoutDelayMs = 0, int readDelayMs = 0)
    {
        CheckScenario(scenario);
        if (!IsSafeEmail(email))
            throw new ArgumentException($"假 app-server 的 email 只收英數字與 @._+-（放在 PowerShell 的單引號字串裡）：{email}", nameof(email));
        ArgumentOutOfRangeException.ThrowIfNegative(startDelayMs);
        ArgumentOutOfRangeException.ThrowIfNegative(logoutDelayMs);
        ArgumentOutOfRangeException.ThrowIfNegative(readDelayMs);
        return string.Create(CultureInfo.InvariantCulture,
                   $"$scenario = '{scenario}'\n$userCode = '{UserCode}'\n$loginId = '{LoginId}'\n$email = '{email}'\n")
               + string.Create(CultureInfo.InvariantCulture,
                   $"$startDelay = {startDelayMs}\n$logoutExit = {logoutExitCode}\n$logoutDelay = {logoutDelayMs}\n$readDelay = {readDelayMs}\n")
               + Body;
    }

    /// <summary>情境在 <see cref="Scenarios"/> 裡（fake-device 的命令列先以它檢查）；不合格回原因</summary>
    public static string? CheckScenarioName(string? scenario) =>
        scenario is not null && Scenarios.Contains(scenario, StringComparer.Ordinal)
            ? null
            : $"假 app-server 的情境要是 {string.Join("、", Scenarios)} 其中一個：{scenario}";

    private static void CheckScenario(string scenario)
    {
        if (CheckScenarioName(scenario) is { } problem)
            throw new ArgumentException(problem, nameof(scenario));
    }

    private static bool IsSafeEmail(string? email) =>
        !string.IsNullOrEmpty(email) && email.Length <= 254 && email.All(c => char.IsAsciiLetterOrDigit(c) || c is '@' or '.' or '_' or '+' or '-');

    private const string Body = """"
        $ErrorActionPreference = 'Stop'
        $utf8 = New-Object System.Text.UTF8Encoding $false
        $in = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $utf8)
        $out = New-Object System.IO.StreamWriter([Console]::OpenStandardOutput(), $utf8)
        $out.NewLine = [string][char]10
        $out.AutoFlush = $true
        $callsPath = Join-Path $PSScriptRoot 'calls.txt'
        $flag = Join-Path $PSScriptRoot 'complete.flag'
        $codexHome = [string]$env:CODEX_HOME
        $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        $script:pending = $false
        function Emit([string]$json) { $out.WriteLine($json) }
        function Record([string]$text) { [System.IO.File]::AppendAllText($callsPath, $text + [string][char]10) }
        function IsTemp([string]$path) {
            if ($path.Trim().Length -eq 0) { return $false }
            return [System.IO.Path]::GetFullPath($path).StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)
        }
        function MarkerPath() {
            if ($codexHome.Trim().Length -eq 0) { return $null }
            return (Join-Path $codexHome 'fake-logged-out')
        }
        function LoggedOutHere() {
            $marker = MarkerPath
            return ($marker -ne $null) -and (Test-Path -LiteralPath $marker)
        }

        Record ('args=' + ($args -join ' '))
        Record ('codexHome=' + $codexHome)
        Record ('pid=' + $PID)

        if ($args.Count -ge 1 -and $args[0] -eq 'logout') {
            Record 'logout'
            if ($codexHome.Trim().Length -eq 0) { exit 3 }
            if (-not (IsTemp $codexHome)) { Record 'refused-not-temp'; exit 4 }
            [void][System.IO.Directory]::CreateDirectory($codexHome)
            [System.IO.File]::WriteAllText((MarkerPath), '')
            if ($logoutDelay -gt 0) { Start-Sleep -Milliseconds $logoutDelay }
            exit $logoutExit
        }

        function Respond($msg) {
            $id = $msg.id
            switch ($msg.method) {
                'initialize' {
                    Record 'initialize'
                    if ($scenario -eq 'init_fail') {
                        Emit ('{"error":{"code":-32603,"message":"fake initialize failure"},"id":' + $id + '}')
                        return
                    }
                    Emit ('{"id":' + $id + ',"result":{"userAgent":"fake/0.163.0","codexHome":"fake"}}')
                    Emit '{"method":"remoteControl/status/changed","params":{"environmentId":null,"installationId":"fake","serverName":"FAKE","status":"disabled"}}'
                }
                'account/login/start' {
                    Record ('account/login/start type=' + [string]$msg.params.type)
                    if ($startDelay -gt 0) { Start-Sleep -Milliseconds $startDelay }
                    if ($scenario -eq 'unsupported') {
                        Emit ('{"error":{"code":-32601,"message":"unknown variant chatgptDeviceCode"},"id":' + $id + '}')
                        return
                    }
                    if ($scenario -eq 'missing_fields') {
                        Emit ('{"id":' + $id + ',"result":{"type":"chatgptDeviceCode","loginId":"' + $loginId + '"}}')
                        return
                    }
                    $url = 'https://auth.openai.com/codex/device'
                    if ($scenario -eq 'evil_host') { $url = 'https://auth.openai.com.evil.x/codex/device' }
                    if ($scenario -eq 'http') { $url = 'http://auth.openai.com/codex/device' }
                    Emit ('{"id":' + $id + ',"result":{"type":"chatgptDeviceCode","loginId":"' + $loginId + '","userCode":"' + $userCode + '","verificationUrl":"' + $url + '"}}')
                    if ($scenario -eq 'complete_fail') {
                        Emit ('{"method":"account/login/completed","params":{"loginId":"' + $loginId + '","success":false,"error":"device code ' + $userCode + ' rejected: enable device code login","onboardingEntrypoint":null}}')
                    } else {
                        $script:pending = $true
                    }
                }
                'account/login/cancel' {
                    Record ('account/login/cancel loginId=' + [string]$msg.params.loginId)
                    $script:pending = $false
                    Emit ('{"id":' + $id + ',"result":{"status":"canceled"}}')
                    Emit ('{"method":"account/login/completed","params":{"loginId":"' + [string]$msg.params.loginId + '","success":false,"error":"Login was canceled","onboardingEntrypoint":null}}')
                }
                'account/read' {
                    Record 'account/read'
                    if ($readDelay -gt 0) { Start-Sleep -Milliseconds $readDelay }
                    if ((Test-Path $flag) -and -not (LoggedOutHere)) {
                        Emit ('{"id":' + $id + ',"result":{"account":{"type":"chatgpt","email":"' + $email + '","planType":"plus"},"requiresOpenaiAuth":true}}')
                    } else {
                        Emit ('{"id":' + $id + ',"result":{"account":null,"requiresOpenaiAuth":true}}')
                    }
                }
                'account/rateLimits/read' {
                    Record 'account/rateLimits/read'
                    Emit ('{"id":' + $id + ',"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":12,"windowDurationMins":300,"resetsAt":1792003600},"secondary":null}}}')
                }
                'model/list' {
                    Record 'model/list'
                    Emit ('{"id":' + $id + ',"result":{"data":[]}}')
                }
                default {
                    Record ([string]$msg.method)
                    Emit ('{"id":' + $id + ',"result":{}}')
                }
            }
        }

        $task = $in.ReadLineAsync()
        while ($true) {
            if ($task.Wait(50)) {
                $line = $task.Result
                if ($line -eq $null) { Record 'exit'; exit 0 }
                if ($line.Trim().Length -gt 0) {
                    $msg = $line | ConvertFrom-Json
                    if ($msg.method -and $msg.id -ne $null) { Respond $msg }
                }
                $task = $in.ReadLineAsync()
            }
            if ($script:pending -and (Test-Path $flag)) {
                $script:pending = $false
                if ((LoggedOutHere) -and (IsTemp $codexHome)) { Remove-Item -LiteralPath (MarkerPath) -Force }
                Emit ('{"method":"account/login/completed","params":{"loginId":"' + $loginId + '","success":true,"error":null,"onboardingEntrypoint":null}}')
            }
        }
        """";
}
