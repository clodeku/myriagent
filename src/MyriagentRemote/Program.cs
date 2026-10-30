using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyriagentRemote;

/// <summary>
/// myriagent-remote：在其他已配對的電腦執行主控台指令（docs/sync-protocol.md 14.11；2026-10-25 擁有者要求）。只能在 Clode Myriagent 的對話裡執行：
/// 對話的 CLI 從環境變數拿到這台 app 的本機管道（MYRIAGENT_REMOTE_PIPE）、這個對話的權杖（MYRIAGENT_REMOTE_TOKEN）與對話 id（MYRIAGENT_AGENT_CONVERSATION）。
/// 那台的輸出照樣寫到標準輸出／錯誤，結束碼對齊那台的程式（另見 <see cref="ExitCodes"/>）
/// </summary>
internal static class Program
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public static int Main(string[] args)
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), Utf8) { AutoFlush = true };
        var stderr = new StreamWriter(Console.OpenStandardError(), Utf8) { AutoFlush = true };
        var parsed = CliArgs.Parse(args);
        if (parsed.Help)
        {
            stdout.Write(CliArgs.Usage);
            return 0;
        }

        if (parsed.Error is { } usage)
        {
            stderr.WriteLine("myriagent-remote：" + usage);
            stderr.WriteLine("用法見 myriagent-remote --help");
            return ExitCodes.Usage;
        }

        if (parsed.ReadStdin)
        {
            using var input = new StreamReader(Console.OpenStandardInput(), Utf8);
            parsed = parsed with { Command = input.ReadToEnd().TrimEnd('\r', '\n') };
            if (string.IsNullOrWhiteSpace(parsed.Command))
            {
                stderr.WriteLine("myriagent-remote：--stdin 讀到的指令是空的");
                return ExitCodes.Usage;
            }
        }

        var pipe = Environment.GetEnvironmentVariable("MYRIAGENT_REMOTE_PIPE");
        var token = Environment.GetEnvironmentVariable("MYRIAGENT_REMOTE_TOKEN");
        var conversation = Environment.GetEnvironmentVariable("MYRIAGENT_AGENT_CONVERSATION");
        if (string.IsNullOrEmpty(pipe) || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(conversation))
        {
            stderr.WriteLine("myriagent-remote：只能在 Clode Myriagent 的對話裡執行（找不到 MYRIAGENT_REMOTE_PIPE、MYRIAGENT_REMOTE_TOKEN 或 MYRIAGENT_AGENT_CONVERSATION；"
                             + "app 剛更新或剛配對時，讓對話重新開始一輪再試）");
            return ExitCodes.NotSent;
        }

        using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            client.Connect(5000);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"myriagent-remote：連不上這台的 Clode Myriagent（{ex.Message}）；app 要開著，而且是在它的對話裡執行");
            return ExitCodes.NotSent;
        }

        var reader = new StreamReader(client, Utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var writer = new StreamWriter(client, Utf8, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        writer.WriteLine(CliArgs.Request(parsed, conversation, token));

        var cancelSent = false;
        var cancelled = false;
        Console.CancelKeyPress += (_, e) =>
        {
            // 第一次 Ctrl+C：請那台停止、等結束碼；第二次直接結束
            if (cancelSent || parsed.Op != CliArgs.OpRun)
                return;
            e.Cancel = true;
            cancelSent = true;
            cancelled = true;
            stderr.WriteLine("[myriagent-remote] 已要求那台停止，等它結束…");
            try
            {
                writer.WriteLine("{\"op\":\"cancel\"}");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        };

        var printer = new ReplyPrinter(stdout, stderr, parsed.Json);
        try
        {
            while (reader.ReadLine() is { } line)
            {
                if (printer.Handle(line) is { } exit)
                    return cancelled && exit == ExitCodes.Cancelled ? ExitCodes.Cancelled : exit;
            }
        }
        catch (IOException)
        {
        }

        if (parsed.Op == CliArgs.OpList && printer.Listed)
            return 0;
        stderr.WriteLine("[myriagent-remote] 和這台的 Clode Myriagent 的連線中斷，不知道指令有沒有執行完");
        return ExitCodes.Unknown;
    }
}

/// <summary>myriagent-remote 的結束碼：正常結束時是那台程式的結束碼，其他照下面（同 GNU timeout、docker run 的慣例）</summary>
internal static class ExitCodes
{
    /// <summary>參數不對</summary>
    public const int Usage = 2;

    /// <summary>超過 --timeout，那台已停止</summary>
    public const int Timeout = 124;

    /// <summary>沒有送出或被拒絕（不在對話裡、沒配對、那台離線或沒開放、版本太舊…），指令沒有執行</summary>
    public const int NotSent = 125;

    /// <summary>那台的人拒絕了高風險的指令（或時間內沒人同意），沒有執行</summary>
    public const int Denied = 126;

    /// <summary>按了 Ctrl+C（或發起的一方要求停止）</summary>
    public const int Cancelled = 130;

    /// <summary>結果不明（連線中斷、那台的 app 結束、伺服器不再轉送）</summary>
    public const int Unknown = 255;
}

/// <summary>解析後的參數</summary>
internal sealed record CliArgs(string Op, string? Device, string? Shell, string? Cwd, int? TimeoutSeconds, string? Command, bool ReadStdin, bool Json, bool Help, string? Error)
{
    public const string OpList = "list";

    public const string OpRun = "run";

    public const string Usage =
        "myriagent-remote：在其他已配對的電腦執行主控台指令（只能在 Clode Myriagent 的對話裡用）\n" +
        "\n" +
        "用法：\n" +
        "  myriagent-remote list [--json]\n" +
        "      列出已配對的電腦：在不在線、能不能在那台執行指令\n" +
        "  myriagent-remote run <電腦> [--shell powershell|pwsh|cmd] [--cwd <資料夾>] [--timeout <秒>] [--] <指令…>\n" +
        "  myriagent-remote run <電腦> [選項] --stdin      （指令從標準輸入讀，適合多行的腳本）\n" +
        "      在那台以登入的使用者、一般權限執行，輸出照樣傳回。<電腦> 是名稱、名稱的開頭或 id\n" +
        "      --shell    預設 powershell（Windows PowerShell 5.1）；pwsh 要那台裝了 PowerShell 7\n" +
        "      --cwd      那台的工作資料夾（預設是那台使用者的家目錄）\n" +
        "      --timeout  上限秒數（預設 600、最多 86400），超過就停止\n" +
        "\n" +
        "高風險的指令（關機、格式化、改系統設定、要系統管理員權限…）要那台電腦的人按同意才會執行；\n" +
        "需要系統管理員的操作另外會在那台跳出 Windows 的 UAC。那台要在設定頁開啟「允許其他電腦的代理在這台執行指令」。\n" +
        "\n" +
        "結束碼：那台程式的結束碼；124 逾時、125 沒有送出或被拒、126 那台的人拒絕、130 已停止、255 結果不明、2 參數不對\n";

    public static CliArgs Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return Fail("少了 list 或 run");
        var first = args[0];
        if (first is "-h" or "--help" or "help" or "/?")
            return new CliArgs(string.Empty, null, null, null, null, null, false, false, true, null);
        if (first == OpList)
        {
            var json = false;
            foreach (var a in args.Skip(1))
            {
                if (a == "--json")
                    json = true;
                else if (a is "-h" or "--help")
                    return new CliArgs(OpList, null, null, null, null, null, false, false, true, null);
                else
                    return Fail($"list 不認得參數 {a}");
            }

            return new CliArgs(OpList, null, null, null, null, null, false, json, false, null);
        }

        if (first != OpRun)
            return Fail($"不認得的操作 {first}（只有 list、run）");

        string? device = null, shell = null, cwd = null;
        int? timeout = null;
        var stdin = false;
        var json2 = false;
        var command = new List<string>();
        var i = 1;
        for (; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--")
            {
                i++;
                break;
            }

            if (device is not null && !a.StartsWith("--", StringComparison.Ordinal))
                break;
            switch (a)
            {
                case "--shell":
                    if (++i >= args.Count)
                        return Fail("--shell 後面要接 powershell、pwsh 或 cmd");
                    shell = args[i].ToLowerInvariant();
                    if (shell is not ("powershell" or "pwsh" or "cmd"))
                        return Fail("--shell 只能是 powershell、pwsh 或 cmd");
                    break;
                case "--cwd":
                    if (++i >= args.Count)
                        return Fail("--cwd 後面要接資料夾");
                    cwd = args[i];
                    break;
                case "--timeout":
                    if (++i >= args.Count || !int.TryParse(args[i], out var seconds) || seconds is < 1 or > 86_400)
                        return Fail("--timeout 要是 1～86400 秒");
                    timeout = seconds;
                    break;
                case "--stdin":
                    stdin = true;
                    break;
                case "--json":
                    json2 = true;
                    break;
                case "-h":
                case "--help":
                    return new CliArgs(OpRun, null, null, null, null, null, false, false, true, null);
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal))
                        return Fail($"不認得的選項 {a}");
                    device = a;
                    break;
            }
        }

        for (; i < args.Count; i++)
            command.Add(args[i]);
        if (device is null)
            return Fail("少了要執行的電腦（myriagent-remote list 列出已配對的電腦）");
        var text = string.Join(' ', command);
        if (stdin && text.Length > 0)
            return Fail("--stdin 和指令只能擇一");
        if (!stdin && string.IsNullOrWhiteSpace(text))
            return Fail("少了要執行的指令");
        if (text.Length > 8000)
            return Fail("指令超過 8000 字（長的腳本改用 --stdin，或先寫成檔案）");
        return new CliArgs(OpRun, device, shell, cwd, timeout, stdin ? null : text, stdin, json2, false, null);
    }

    /// <summary>送給 app 的第一行</summary>
    public static string Request(CliArgs a, string conversation, string token)
    {
        var o = new JsonObject
        {
            ["v"] = 1,
            ["op"] = a.Op,
            ["conversation"] = conversation,
            ["token"] = token,
        };
        if (a.Op == OpRun)
        {
            o["device"] = a.Device;
            o["command"] = a.Command;
            if (a.Shell is not null)
                o["shell"] = a.Shell;
            if (a.Cwd is not null)
                o["cwd"] = a.Cwd;
            if (a.TimeoutSeconds is { } t)
                o["timeoutSec"] = t;
        }

        return o.ToJsonString();
    }

    private static CliArgs Fail(string error) => new(string.Empty, null, null, null, null, null, false, false, false, error);
}

/// <summary>把 app 回的每一行轉成輸出；遇到最後一行時回結束碼</summary>
internal sealed class ReplyPrinter(TextWriter stdout, TextWriter stderr, bool json)
{
    /// <summary>list 已經印出來了</summary>
    public bool Listed { get; private set; }

    /// <summary>處理一行；這是最後一行時回 myriagent-remote 的結束碼，否則 null</summary>
    public int? Handle(string line)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }

        if (node is not JsonObject o)
            return null;
        var t = Str(o, "t");
        switch (t)
        {
            case "devices":
                PrintDevices(o);
                Listed = true;
                return 0;
            case "accepted":
                stderr.WriteLine($"[myriagent-remote] 已送到「{Str(o, "device")}」");
                return null;
            case "status":
                if (Str(o, "text") is { Length: > 0 } status)
                    stderr.WriteLine("[myriagent-remote] " + status);
                return null;
            case "out":
                stdout.Write(Str(o, "text"));
                return null;
            case "err":
                stderr.Write(Str(o, "text"));
                return null;
            case "gap":
                stderr.WriteLine("\n[myriagent-remote] （中間有一段輸出沒收到：連線曾經中斷）");
                return null;
            case "exit":
                return Exit(o);
            case "error":
                stderr.WriteLine("myriagent-remote：" + (Str(o, "message") ?? "失敗"));
                return Str(o, "code") == "unknown" ? ExitCodes.Unknown : ExitCodes.NotSent;
            default:
                return null;
        }
    }

    private int Exit(JsonObject o)
    {
        var code = Str(o, "code");
        var exitCode = o["exitCode"] is JsonValue v && v.TryGetValue<int>(out var c) ? c : (int?)null;
        if (Str(o, "text") is { Length: > 0 } text)
            stderr.WriteLine("[myriagent-remote] " + text);
        return code switch
        {
            null => exitCode ?? ExitCodes.Unknown,
            "timeout" => ExitCodes.Timeout,
            "cancelled" => ExitCodes.Cancelled,
            "denied" => ExitCodes.Denied,
            "failed" => ExitCodes.NotSent,
            _ => ExitCodes.Unknown,
        };
    }

    private void PrintDevices(JsonObject o)
    {
        var devices = o["devices"] as JsonArray ?? [];
        if (json)
        {
            stdout.WriteLine(devices.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return;
        }

        if (o["live"] is JsonValue live && live.TryGetValue<bool>(out var isLive) && !isLive)
            stderr.WriteLine("[myriagent-remote] 注意：這台目前沒有連上伺服器，暫時不能送出");
        if (devices.Count == 0)
        {
            stdout.WriteLine("（還沒有其他已配對的電腦）");
            return;
        }

        foreach (var d in devices.OfType<JsonObject>())
        {
            var name = Str(d, "name") ?? Str(d, "id") ?? "?";
            var machine = Str(d, "machineName") is { Length: > 0 } m && !string.Equals(m, name, StringComparison.OrdinalIgnoreCase) ? $"（{m}）" : string.Empty;
            string state;
            if (Bool(d, "self"))
                state = "這台";
            else if (!Bool(d, "online"))
                state = "離線";
            else if (!Bool(d, "exec"))
                state = "在線，版本太舊（要先更新那台）";
            else if (!Bool(d, "allowExec"))
                state = "在線，沒有開放（要在那台開「允許其他電腦的代理在這台執行指令」）";
            else
                state = "在線，可以執行";
            stdout.WriteLine($"{name}{machine}\t{state}\t{Str(d, "id")}");
        }
    }

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool Bool(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}
