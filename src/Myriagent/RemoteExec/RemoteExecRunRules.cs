using System.Text;
using Myriagent.Remote;
using Newtonsoft.Json.Linq;

namespace Myriagent.RemoteExec;

/// <summary>一個遠端執行指令（<c>remoteExec</c> 的內容，形狀已由閘門 5b 檢查）</summary>
/// <param name="Shell"><see cref="RemoteExecRules.Shells"/> 之一</param>
/// <param name="Cwd">工作資料夾（null＝這台使用者的家目錄）</param>
/// <param name="TimeoutSeconds">上限時間（已夾進範圍）</param>
/// <param name="Label">發起的對話標題（給這台的人看；可能是 null）</param>
internal sealed record RemoteExecRequest(string Shell, string Command, string? Cwd, int TimeoutSeconds, string? Label);

/// <summary>怎麼開殼層：執行檔、參數（PowerShell 是 ArgumentList，cmd 是原樣的命令列）與要先寫好的腳本</summary>
/// <param name="RawArguments">cmd 的命令列（<c>/s /c "…"</c> 要原樣傳，不能經 ArgumentList 跳脫）；PowerShell 是 null</param>
/// <param name="Script">PowerShell 要先寫進 <see cref="ScriptPath"/> 的腳本（UTF-8 含 BOM）；cmd 是 null</param>
internal sealed record RemoteExecLaunch(string FileName, IReadOnlyList<string> Arguments, string? RawArguments, string? ScriptPath, string? Script);

/// <summary>
/// 被操作的電腦執行遠端指令的純函式規則（docs/sync-protocol.md 14.11）：解析指令、組殼層的命令列與腳本、檢查工作資料夾。
/// PowerShell 把指令寫成暫存的 .ps1 以 <c>-File</c> 執行（命令列放不下 8000 字的 -EncodedCommand），開頭把輸出編碼改成 UTF-8、
/// 結尾把結束碼對齊最後一個指令（失敗時是原生程式的結束碼或 1）；cmd 以 <c>chcp 65001</c> 再接指令。輸出一律以 UTF-8 解（<see cref="RemoteExecOutputDecoder"/>）
/// </summary>
internal static class RemoteExecRunRules
{
    /// <summary>從閘門交來的指令取出內容；形狀不對（照理閘門已擋）回 null</summary>
    public static RemoteExecRequest? Parse(JObject command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command["shell"] is not JValue { Type: JTokenType.String, Value: string shell } || !RemoteExecRules.Shells.Contains(shell, StringComparer.Ordinal))
            return null;
        if (command["command"] is not JValue { Type: JTokenType.String, Value: string text } || string.IsNullOrWhiteSpace(text)
            || text.Length > RemoteExecRules.MaxCommandLength)
            return null;
        var cwd = command["cwd"] is JValue { Type: JTokenType.String, Value: string folder } && !string.IsNullOrWhiteSpace(folder) ? folder.Trim() : null;
        int? timeout = command["timeoutSec"] is JValue { Type: JTokenType.Integer, Value: long seconds } && seconds is >= 1 and <= RemoteExecRules.MaxTimeoutSeconds
            ? (int)seconds
            : null;
        var label = command["label"] is JValue { Type: JTokenType.String, Value: string title } && !string.IsNullOrWhiteSpace(title) ? title.Trim() : null;
        return new RemoteExecRequest(shell, text, cwd, RemoteExecRules.TimeoutSeconds(timeout), label);
    }

    /// <summary>
    /// 工作資料夾的問題（null＝可以用）：要是這台的絕對路徑、存在；UNC 一律不收（檢查存在就會對外送 SMB 雜湊，同媒體端點）
    /// </summary>
    public static string? CheckFolder(string? folder, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        if (folder is null)
            return null;
        if (folder.StartsWith(@"\\", StringComparison.Ordinal) || folder.StartsWith("//", StringComparison.Ordinal))
            return "工作資料夾不能是網路路徑";
        if (!Path.IsPathFullyQualified(folder))
            return "工作資料夾要是完整的路徑（例如 C:\\Users\\me\\project）";
        return exists(folder) ? null : $"這台電腦上沒有資料夾「{folder}」";
    }

    /// <summary>PowerShell 腳本：UTF-8 輸出、不顯示進度列、最後一個指令失敗時回它的結束碼（原生程式）或 1</summary>
    public static string PowerShellScript(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var builder = new StringBuilder();
        builder.Append("$ProgressPreference = 'SilentlyContinue'\r\n");
        builder.Append("[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)\r\n");
        builder.Append("$OutputEncoding = [System.Text.UTF8Encoding]::new($false)\r\n");
        builder.Append(command.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal));
        builder.Append("\r\n$__myriagentOk = $?\r\n");
        builder.Append("if (-not $__myriagentOk) { if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; exit 1 }\r\n");
        builder.Append("exit 0\r\n");
        return builder.ToString();
    }

    /// <summary>cmd 的命令列：<c>/d /s /c "chcp 65001>nul &amp; 指令"</c>（/s 讓 cmd 只拿掉最外層的引號，指令裡的引號原樣保留）</summary>
    public static string CmdArguments(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        // cmd 的 /c 只讀一行：換行改成 &（多行的指令照順序執行）
        var oneLine = string.Join(" & ", command.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return "/d /s /c \"chcp 65001>nul & " + oneLine + "\"";
    }

    /// <summary>
    /// 殼層的開法。<paramref name="powerShellPath"/>、<paramref name="pwshPath"/>、<paramref name="cmdPath"/> 是這台找到的執行檔（pwsh 沒裝是 null）；
    /// <paramref name="scriptDirectory"/> 放暫存腳本。找不到殼層回 null
    /// </summary>
    public static RemoteExecLaunch? Launch(RemoteExecRequest request, string commandId, string powerShellPath, string? pwshPath, string cmdPath, string scriptDirectory)
    {
        ArgumentNullException.ThrowIfNull(request);
        switch (request.Shell)
        {
            case RemoteExecRules.PowerShell:
            case RemoteExecRules.Pwsh:
                var exe = request.Shell == RemoteExecRules.Pwsh ? pwshPath : powerShellPath;
                if (exe is null)
                    return null;
                var script = Path.Combine(scriptDirectory, commandId + ".ps1");
                return new RemoteExecLaunch(exe, ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script], null, script,
                    PowerShellScript(request.Command));
            case RemoteExecRules.Cmd:
                return new RemoteExecLaunch(cmdPath, [], CmdArguments(request.Command), null, null);
            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Shell, "不認得的殼層");
        }
    }

    /// <summary>不交給遠端指令的環境變數：對話的標記與遠端執行的管道（指令不是代理開的，不能被當成對話另開的程式、也不能再拿這台的權杖）</summary>
    public static bool IsStrippedVariable(string name)
        => name.StartsWith("MYRIAGENT_REMOTE", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, Background.BackgroundProgramRules.MarkerVariable, StringComparison.OrdinalIgnoreCase);

    /// <summary>給這台的人看的一行（確認視窗、記錄）：指令太長時截斷</summary>
    public static string Preview(string command, int max = 200)
    {
        ArgumentNullException.ThrowIfNull(command);
        var flat = command.Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', '⏎');
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}

/// <summary>
/// 子程序輸出的解碼（一條管線一個）：先以 UTF-8 解（腳本已把輸出編碼改成 UTF-8），遇到不合格的位元組就改用主控台的字碼頁解之後的內容
/// （有些程式不理會 chcp、照樣輸出 cp950）。切在多位元組字元中間的位元組留到下一次
/// </summary>
internal sealed class RemoteExecOutputDecoder(Encoding fallback)
{
    private readonly Decoder utf8 = new UTF8Encoding(false, throwOnInvalidBytes: true).GetDecoder();

    private Decoder? legacy;

    /// <summary>已經改用主控台的字碼頁</summary>
    public bool UsingFallback => legacy is not null;

    public string Decode(ReadOnlySpan<byte> data, bool flush = false)
    {
        if (legacy is null)
        {
            try
            {
                var chars = new char[utf8.GetCharCount(data, flush)];
                var count = utf8.GetChars(data, chars, flush);
                return new string(chars, 0, count);
            }
            catch (DecoderFallbackException)
            {
                utf8.Reset();
                legacy = fallback.GetDecoder();
            }
        }

        var buffer = new char[legacy.GetCharCount(data, flush)];
        var written = legacy.GetChars(data, buffer, flush);
        return new string(buffer, 0, written);
    }
}
