using Myriagent.Remote;

namespace MyriagentDesk;

/// <summary>命令列的模式（<see cref="Program"/>）</summary>
internal enum DeskServiceMode
{
    Usage,
    Service,
    Run,
    SessionProbe,
    SessionAgent,
    Install,
    Uninstall,
    Query,
    Version,
}

/// <summary>解析過的命令列</summary>
/// <param name="ServiceName">服務名稱（預設真的那一個；手動整合測試給暫時的名稱）</param>
/// <param name="Pipe">run：前景模式的管道名稱；session-agent：幫手的管道名稱</param>
/// <param name="Trust">install：信任的伺服器（<c>--hub-key</c>、<c>--aud</c>、<c>--device</c> 三個一起；沒給是 null）</param>
internal sealed record DeskServiceCommand(DeskServiceMode Mode, string ServiceName, string? Pipe, string? Error, DeskServiceTrust? Trust = null)
{
    public static DeskServiceCommand Bad(string error) => new(DeskServiceMode.Usage, DeskServiceRules.ServiceName, null, error);
}

/// <summary>
/// 命令列的解析（純函式）：<c>模式 [--name 服務名稱] [--pipe 管道名稱]</c>。安裝器沒有「服務檔在哪裡」「裝到哪裡」的參數：
/// 一律裝自己旁邊的那幾個檔、裝到 Program Files 底下固定的位置——以系統管理員執行的程式不讓呼叫的人指定路徑
/// </summary>
internal static class DeskServiceArgs
{
    public const string Usage =
        "MyriagentDesk service | install [--name <service>] [--hub-key <spki> --aud <origin> --device <id>] | uninstall [--name <service>] | query [--name <service>] | run --pipe <name> | session-probe | session-agent --pipe <name> | version";

    public static DeskServiceCommand Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return DeskServiceCommand.Bad("沒有指定模式");
        DeskServiceMode mode = args[0] switch
        {
            "service" => DeskServiceMode.Service,
            "run" => DeskServiceMode.Run,
            "session-probe" => DeskServiceMode.SessionProbe,
            "session-agent" => DeskServiceMode.SessionAgent,
            "install" => DeskServiceMode.Install,
            "uninstall" => DeskServiceMode.Uninstall,
            "query" => DeskServiceMode.Query,
            "version" => DeskServiceMode.Version,
            _ => DeskServiceMode.Usage,
        };
        if (mode == DeskServiceMode.Usage)
            return DeskServiceCommand.Bad($"不認得的模式：{args[0]}");

        var name = DeskServiceRules.ServiceName;
        string? pipe = null;
        string? hubKey = null;
        string? aud = null;
        string? device = null;
        for (var i = 1; i < args.Count; i++)
        {
            var key = args[i];
            if (key is not ("--name" or "--pipe" or "--hub-key" or "--aud" or "--device"))
                return DeskServiceCommand.Bad($"不認得的參數：{key}");
            if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]))
                return DeskServiceCommand.Bad($"{key} 後面要接值");
            var value = args[++i];
            switch (key)
            {
                case "--name":
                    name = value;
                    break;
                case "--pipe":
                    pipe = value;
                    break;
                case "--hub-key":
                    hubKey = value;
                    break;
                case "--aud":
                    aud = value;
                    break;
                default:
                    device = value;
                    break;
            }
        }

        // 信任的伺服器只在安裝時給，三個要一起、而且合格（值是公鑰、來源與裝置 id，不是路徑）
        if ((hubKey ?? aud ?? device) is not null && mode != DeskServiceMode.Install)
            return DeskServiceCommand.Bad("--hub-key、--aud、--device 只用在 install");
        if (!DeskServiceRules.TryTrustArgs(hubKey, aud, device, out var trust))
            return DeskServiceCommand.Bad("--hub-key、--aud、--device 要三個一起給，而且是合格的公鑰、來源與裝置 id");

        // 服務名稱只收真的那一個與測試用的暫時名稱：安裝器以系統管理員執行，不讓呼叫的人隨便指定要建立、刪除哪個服務
        if (!DeskServiceRules.IsServiceName(name))
            return DeskServiceCommand.Bad($"不合法的服務名稱：{name}");
        if (mode == DeskServiceMode.Run && !IsPipeName(pipe))
            return DeskServiceCommand.Bad("run 要以 --pipe 指定管道名稱（英數字、-、_、.，最多 80 字）");
        // 幫手的管道名稱是服務產生的固定格式（服務名稱-s工作階段-亂數）：別的名稱不收
        if (mode == DeskServiceMode.SessionAgent && !DeskServiceRules.IsHelperPipeName(pipe))
            return DeskServiceCommand.Bad("session-agent 要以 --pipe 指定幫手的管道名稱");
        if (pipe is not null && mode is not (DeskServiceMode.Run or DeskServiceMode.SessionAgent))
            return DeskServiceCommand.Bad("--pipe 只用在 run 與 session-agent");
        return new DeskServiceCommand(mode, name, pipe, null, trust);
    }

    private static bool IsPipeName(string? pipe)
        => pipe is { Length: > 0 and <= 80 } && pipe.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
