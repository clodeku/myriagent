using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyriagentSchedule;

/// <summary>
/// myriagent-schedule：建立、列出、刪除 Myriagent 的定時訊息（時間到了 Myriagent 把文字送進這個對話，或每次開新對話）。只能在 Clode Myriagent 的對話裡執行：
/// 和 myriagent-remote 一樣從環境變數拿到本機管道、這個對話的權杖與對話 id。時間的寫法與檢查都在 app 那邊（TimedMessageRules），這裡只轉送
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
            stderr.WriteLine("myriagent-schedule：" + usage);
            stderr.WriteLine("用法見 myriagent-schedule --help");
            return 2;
        }

        var pipe = Environment.GetEnvironmentVariable("MYRIAGENT_REMOTE_PIPE");
        var token = Environment.GetEnvironmentVariable("MYRIAGENT_REMOTE_TOKEN");
        var conversation = Environment.GetEnvironmentVariable("MYRIAGENT_AGENT_CONVERSATION");
        if (string.IsNullOrEmpty(pipe) || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(conversation))
        {
            stderr.WriteLine("myriagent-schedule：只能在 Clode Myriagent 的對話裡執行（找不到 MYRIAGENT_REMOTE_PIPE、MYRIAGENT_REMOTE_TOKEN 或 MYRIAGENT_AGENT_CONVERSATION；app 剛更新時，讓對話重新開始一輪再試）");
            return 3;
        }

        using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        try
        {
            client.Connect(5000);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"myriagent-schedule：連不上這台的 Clode Myriagent（{ex.Message}）；app 要開著，而且是在它的對話裡執行");
            return 3;
        }

        var reader = new StreamReader(client, Utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var writer = new StreamWriter(client, Utf8, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        writer.WriteLine(CliArgs.Request(parsed, conversation, token));
        string? line;
        try
        {
            line = reader.ReadLine();
        }
        catch (IOException)
        {
            line = null;
        }
        if (line == null)
        {
            stderr.WriteLine("myriagent-schedule：和這台的 Clode Myriagent 的連線中斷");
            return 3;
        }
        return CliArgs.Print(line, stdout, stderr, parsed.Json);
    }
}

/// <summary>參數</summary>
internal sealed record ScheduleArgs(string Op, string? WhenKind, string? WhenValue, string? Text, string? Label, bool NewConversation, string? Id, bool Json, bool Help, string? Error);

internal static class CliArgs
{
    public const string Usage = """
        myriagent-schedule：Myriagent 的定時訊息（時間到了把文字送進這個對話，app 開著時才送；錯過的重複訊息只補一次）

          myriagent-schedule add <時間> [--new] [--label 名稱] -- <要送的文字>
              時間（擇一）：
                --at "2026-10-27 14:00"   一次（也可以只寫 14:00＝今天或明天的 14:00）
                --daily 09:00             每天
                --weekdays 09:00          平日（週一到週五）
                --every 30m | 2h          每隔一段時間（分鐘要能整除 60、至少 5 分鐘；小時要能整除 24）
                --cron "0 9 * * 1-5"      五欄的 cron（分 時 日 月 星期，本機時間）
              --new                       每次開新對話（帳戶、模型、權限模式、資料夾照這個對話）；不加就送進這個對話
              文字也可以用 --text "…"
          myriagent-schedule list               列出這台的定時訊息（* 是這個對話建立或送進這個對話的）
          myriagent-schedule remove <id>        刪除
          myriagent-schedule pause <id>         暫停
          myriagent-schedule resume <id>        繼續
          加 --json 以 JSON 輸出

        結束碼：0 成功、1 被 app 拒絕（訊息在標準錯誤）、2 參數不對、3 不在對話裡或連不上 app。

        """;

    private static readonly string[] WhenFlags = { "--at", "--daily", "--weekdays", "--every", "--cron" };

    public static ScheduleArgs Parse(string[] args)
    {
        ScheduleArgs Fail(string message) => new("", null, null, null, null, false, null, false, false, message);
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            return new ScheduleArgs("", null, null, null, null, false, null, false, true, null);
        var op = args[0];
        string? whenKind = null, whenValue = null, text = null, label = null, id = null;
        bool isNew = false, json = false;
        for (int i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--")
            {
                text = string.Join(' ', args.Skip(i + 1));
                break;
            }
            if (WhenFlags.Contains(a))
            {
                if (whenKind != null)
                    return Fail("時間只能給一個（--at、--daily、--weekdays、--every、--cron 擇一）");
                if (i + 1 >= args.Length)
                    return Fail(a + " 後面要接時間");
                whenKind = a[2..];
                whenValue = args[++i];
                continue;
            }
            switch (a)
            {
                case "--new":
                    isNew = true;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--label":
                    if (i + 1 >= args.Length)
                        return Fail("--label 後面要接名稱");
                    label = args[++i];
                    break;
                case "--text":
                    if (i + 1 >= args.Length)
                        return Fail("--text 後面要接文字");
                    text = args[++i];
                    break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal))
                        return Fail("不認得的參數 " + a);
                    if (id != null)
                        return Fail("多了一個參數 " + a + "（文字請放在 -- 後面）");
                    id = a;
                    break;
            }
        }
        switch (op)
        {
            case "add":
                if (whenKind == null)
                    return Fail("add 要給時間：--at、--daily、--weekdays、--every 或 --cron");
                if (string.IsNullOrWhiteSpace(text))
                    return Fail("add 要給文字：放在 -- 後面，或用 --text");
                if (id != null)
                    return Fail("多了一個參數 " + id + "（文字請放在 -- 後面）");
                break;
            case "list":
                break;
            case "remove":
            case "pause":
            case "resume":
                if (id == null)
                    return Fail(op + " 要給 id（myriagent-schedule list 看得到）");
                break;
            default:
                return Fail("不認得的操作 " + op + "（add、list、remove、pause、resume）");
        }
        return new ScheduleArgs(op, whenKind, whenValue, text, label, isNew, id, json, false, null);
    }

    /// <summary>送給 app 的一行：{v:1, op:"timed", conversation, token, timed:{op, when?, text?, label?, new?, id?}}</summary>
    public static string Request(ScheduleArgs a, string conversation, string token)
    {
        var timed = new JsonObject { ["op"] = a.Op };
        if (a.WhenKind != null)
            timed["when"] = new JsonObject { ["kind"] = a.WhenKind, ["value"] = a.WhenValue };
        if (a.Text != null)
            timed["text"] = a.Text;
        if (a.Label != null)
            timed["label"] = a.Label;
        if (a.NewConversation)
            timed["new"] = true;
        if (a.Id != null)
            timed["id"] = a.Id;
        var o = new JsonObject { ["v"] = 1, ["op"] = "timed", ["conversation"] = conversation, ["token"] = token, ["timed"] = timed };
        return o.ToJsonString();
    }

    /// <summary>印出 app 的回覆，回結束碼</summary>
    public static int Print(string line, TextWriter stdout, TextWriter stderr, bool json)
    {
        JsonObject? o;
        try
        {
            o = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            o = null;
        }
        if (o == null)
        {
            stderr.WriteLine("myriagent-schedule：app 的回覆看不懂");
            return 3;
        }
        if (o["t"]?.GetValue<string>() == "error")
        {
            stderr.WriteLine("myriagent-schedule：" + (o["message"]?.GetValue<string>() ?? "被拒絕"));
            return o["code"]?.GetValue<string>() == "token" ? 3 : 1;
        }
        bool ok = o["ok"]?.GetValue<bool>() == true;
        if (json)
        {
            (ok ? stdout : stderr).WriteLine(o.ToJsonString());
            return ok ? 0 : 1;
        }
        if (!ok)
        {
            stderr.WriteLine("myriagent-schedule：" + (o["message"]?.GetValue<string>() ?? "被拒絕"));
            return 1;
        }
        if (o["items"] is JsonArray items)
        {
            if (items.Count == 0)
                stdout.WriteLine("沒有定時訊息");
            foreach (var it in items.OfType<JsonObject>())
            {
                var mine = it["mine"]?.GetValue<bool>() == true ? "*" : " ";
                var state = it["enabled"]?.GetValue<bool>() == true ? "" : "（停用）";
                var next = it["next"]?.GetValue<string>() is { } n ? "，下一次 " + n : "";
                var where = it["newConversation"]?.GetValue<bool>() == true ? "每次開新對話" : "送進「" + (it["conversation"]?.GetValue<string>() ?? "已刪除的對話") + "」";
                stdout.WriteLine($"{mine} {it["id"]}  {it["label"]}｜{it["when"]}{next}｜{where}{state}");
            }
            return 0;
        }
        stdout.WriteLine(o["message"]?.GetValue<string>() ?? "完成");
        return 0;
    }
}
