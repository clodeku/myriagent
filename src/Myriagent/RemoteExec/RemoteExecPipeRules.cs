using System.Security.Cryptography;
using System.Text;
using Myriagent.Remote;
using Myriagent.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.RemoteExec;

/// <summary>其他電腦（<c>myriagent-remote list</c>、解析 <c>run</c> 的目標）</summary>
/// <param name="SupportsExec">那台目前在線的連線協商出 exec1（版本夠新）</param>
/// <param name="AllowRemoteExec">那台開了「允許其他電腦的代理在這台執行指令」（Hello 回報的政策，層級是 Control 才會是 true）</param>
internal sealed record RemoteExecDevice(string Id, string Name, string? MachineName, bool Self, bool Online, bool Revoked, bool SupportsExec,
    string RemoteControlLevel, bool AllowRemoteExec);

/// <summary>同步的現況（UI 執行緒取的快照）</summary>
/// <param name="Live">這台目前的連線交握完成、協商出 peer1 與 exec1（能送遠端執行指令）</param>
internal sealed record RemoteExecDirectory(bool Paired, bool Live, string? SelfDeviceId, IReadOnlyList<RemoteExecDevice> Devices);

/// <summary>myriagent-remote 送來的一個要求（管道的第一行）</summary>
internal sealed record RemoteExecPipeRequest(string Op, string Conversation, string Token, string? Device, string? Shell, string? Cwd, int? TimeoutSeconds, string? Command)
{
    /// <summary>op 是 timed（myriagent-schedule）時的內容：{op:"add"|"list"|"remove"|"pause"|"resume", …}</summary>
    public Newtonsoft.Json.Linq.JObject? Timed { get; init; }
}

/// <summary>
/// 對話裡的代理經 <c>myriagent-remote</c> 下遠端執行指令的本機管道（docs/sync-protocol.md 14.11 的「發起的電腦」）：每個 app 實例一條具名管道
/// （名稱取資料根目錄的雜湊，只給同一個 Windows 使用者：PipeOptions.CurrentUserOnly），對話的 CLI 經環境變數拿到管道名稱與這個對話的權杖
/// （HMAC(app 這次執行的亂數, 對話 id)：只有這個 app 開的對話拿得到，換版重開後舊的權杖就不能用）。協定是一行一個 JSON（UTF-8、<c>\n</c>）：
/// <list type="bullet">
/// <item>要求：<c>{v:1, op:"list"|"run", conversation, token, device?, shell?, cwd?, timeoutSec?, command?}</c>；<c>run</c> 之後還可以送 <c>{op:"cancel"}</c>。</item>
/// <item>回應：<c>{t:"devices", self, devices:[…]}</c>、<c>{t:"accepted", device, id}</c>、<c>{t:"status", code, text}</c>、<c>{t:"out"|"err", text}</c>、
/// <c>{t:"gap"}</c>（中間有段落沒收到）、<c>{t:"exit", exitCode, code, text}</c>、<c>{t:"error", code, message}</c>。</item>
/// </list>
/// 純函式
/// </summary>
internal static class RemoteExecPipeRules
{
    /// <summary>給 CLI 的環境變數：管道名稱</summary>
    public const string PipeVariable = "MYRIAGENT_REMOTE_PIPE";

    /// <summary>給 CLI 的環境變數：這個對話的權杖</summary>
    public const string TokenVariable = "MYRIAGENT_REMOTE_TOKEN";

    /// <summary>協定版本</summary>
    public const int Version = 1;

    /// <summary>要求一行的上限（UTF-16 字元）：指令 8000 字，跳脫之後最多約六倍</summary>
    public const int MaxRequestChars = 64 * 1024;

    public const string OpList = "list";

    public const string OpRun = "run";

    public const string OpCancel = "cancel";

    /// <summary>myriagent-schedule 的定時訊息（不需要配對；結果一行 {t:"timed", ok, message?, item?, items?}）</summary>
    public const string OpTimed = "timed";

    // 錯誤代碼（{t:"error"}）
    public const string ErrorInvalid = "invalid";

    public const string ErrorToken = "token";

    public const string ErrorNotPaired = "not_paired";

    public const string ErrorOffline = "offline";

    public const string ErrorUnknownDevice = "unknown_device";

    public const string ErrorRejected = "rejected";

    public const string ErrorUnknown = "unknown";

    /// <summary>管道名稱：資料根目錄（正規化、轉小寫）的 SHA-256 前 16 碼——同一台的兩個 app 實例（不同 --data）各有各的</summary>
    public static string PipeName(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataRoot);
        var normalized = Path.GetFullPath(dataRoot).TrimEnd('\\', '/').ToLowerInvariant();
        return "myriagent-agent-remote-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
    }

    /// <summary>對話的權杖：HMAC-SHA256(這次執行的亂數, 對話 id) 的十六進位</summary>
    public static string Token(byte[] secret, string conversationId)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        return Convert.ToHexStringLower(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(conversationId)));
    }

    /// <summary>權杖對得上這個對話（固定時間比較）</summary>
    public static bool VerifyToken(byte[] secret, string conversationId, string token)
    {
        if (string.IsNullOrEmpty(conversationId) || string.IsNullOrEmpty(token))
            return false;
        var expected = Encoding.ASCII.GetBytes(Token(secret, conversationId));
        var given = Encoding.ASCII.GetBytes(token);
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    /// <summary>解析要求的第一行；不合格回 null 與原因</summary>
    public static (RemoteExecPipeRequest? Request, string? Problem) ParseRequest(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > MaxRequestChars)
            return (null, "要求是空的或太長");
        JObject o;
        try
        {
            o = JObject.Parse(line);
        }
        catch (JsonException)
        {
            return (null, "要求不是 JSON 物件");
        }

        if (o["v"] is not JValue { Type: JTokenType.Integer, Value: long v } || v != Version)
            return (null, "myriagent-remote 與這台的 Clode Myriagent 版本不一致，請在對話裡重新開始（讓 CLI 以新版重開）");
        var op = Str(o, "op");
        if (op is not (OpList or OpRun or OpTimed))
            return (null, "不認得的操作");
        if (Str(o, "conversation") is not { Length: > 0 } conversation || Str(o, "token") is not { Length: > 0 } token)
            return (null, "少了對話或權杖：myriagent-remote 只能在 Clode Myriagent 的對話裡執行");
        int? timeout = null;
        if (o["timeoutSec"] is { Type: not JTokenType.Null } t)
        {
            if (t is not JValue { Type: JTokenType.Integer, Value: long seconds } || seconds is < 1 or > RemoteExecRules.MaxTimeoutSeconds)
                return (null, $"--timeout 要是 1～{RemoteExecRules.MaxTimeoutSeconds} 秒");
            timeout = (int)seconds;
        }

        var request = new RemoteExecPipeRequest(op, conversation, token, Str(o, "device"), Str(o, "shell"), Str(o, "cwd"), timeout, Str(o, "command"))
        {
            Timed = o["timed"] as JObject,
        };
        if (op == OpTimed && request.Timed == null)
            return (null, "少了定時訊息的內容");
        if (op == OpRun)
        {
            if (string.IsNullOrWhiteSpace(request.Device))
                return (null, "少了要執行的電腦");
            if (string.IsNullOrWhiteSpace(request.Command))
                return (null, "少了要執行的指令");
            if (request.Command.Length > RemoteExecRules.MaxCommandLength)
                return (null, $"指令超過 {RemoteExecRules.MaxCommandLength} 字");
            if (request.Shell is { } shell && !RemoteExecRules.Shells.Contains(shell, StringComparer.Ordinal))
                return (null, "--shell 只能是 powershell、pwsh 或 cmd");
        }

        return (request, null);
    }

    /// <summary>
    /// 找目標電腦：id 完全相同，或名稱、電腦名稱不分大小寫相同，或名稱不分大小寫的唯一開頭；撤銷的不算。
    /// 找不到或不只一台回 null 與說明
    /// </summary>
    public static (RemoteExecDevice? Device, string? Problem) Resolve(IReadOnlyList<RemoteExecDevice> devices, string query)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var q = query.Trim();
        var live = devices.Where(d => !d.Revoked).ToList();
        if (live.FirstOrDefault(d => string.Equals(d.Id, q, StringComparison.Ordinal)) is { } byId)
            return (byId, null);
        var exact = live.Where(d => string.Equals(d.Name, q, StringComparison.OrdinalIgnoreCase)
                                    || d.MachineName is { } machine && string.Equals(machine, q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1)
            return (exact[0], null);
        if (exact.Count > 1)
            return (null, $"有 {exact.Count} 台電腦叫「{q}」，請改用 id（myriagent-remote list）");
        var prefix = live.Where(d => d.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (prefix.Count == 1)
            return (prefix[0], null);
        return prefix.Count > 1
            ? (null, $"「{q}」符合 {prefix.Count} 台電腦（{string.Join("、", prefix.Select(d => d.Name))}），請打完整的名稱或 id")
            : (null, $"找不到電腦「{q}」（myriagent-remote list 列出已配對的電腦）");
    }

    /// <summary>送出之前就知道不行的（不送伺服器）：這台、離線、版本太舊、那台沒有開放。null＝可以送</summary>
    public static string? Precheck(RemoteExecDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Self)
            return "這就是這台電腦：直接在這台執行就好，不必用 myriagent-remote";
        if (!device.Online)
            return $"「{device.Name}」目前不在線上（那台的 Clode Myriagent 要開著並連上伺服器）";
        if (!device.SupportsExec)
            return $"「{device.Name}」的 Clode Myriagent 版本太舊，還不能執行遠端指令；請先更新那台";
        if (!device.AllowRemoteExec)
            return $"「{device.Name}」沒有開啟「允許其他電腦的代理在這台執行指令」（要在那台的設定頁「多電腦同步」把遠端操作設成「可以操作」並打開它）";
        return null;
    }

    /// <summary>送給伺服器的 <c>remoteExec</c>（型錄的形狀：deviceId、shell、command、cwd、timeoutSec、label）</summary>
    public static JObject Command(RemoteExecDevice device, RemoteExecPipeRequest request, string? label)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(request);
        var cmd = new JObject
        {
            ["cmd"] = RemoteCommandCatalog.RemoteExec,
            ["deviceId"] = device.Id,
            ["shell"] = request.Shell ?? RemoteExecRules.PowerShell,
            ["command"] = request.Command,
        };
        if (!string.IsNullOrWhiteSpace(request.Cwd))
            cmd["cwd"] = request.Cwd.Trim();
        if (request.TimeoutSeconds is { } timeout)
            cmd["timeoutSec"] = timeout;
        if (!string.IsNullOrWhiteSpace(label))
        {
            var clean = new string(label.Trim().Where(ch => !char.IsControl(ch)).ToArray());
            if (clean.Length > RemoteExecRules.MaxLabelLength)
                clean = clean[..RemoteExecRules.MaxLabelLength];
            if (clean.Length > 0 && !char.IsHighSurrogate(clean[^1]))
                cmd["label"] = clean;
        }

        return cmd;
    }

    // ── 回應的一行 ──

    public static string Error(string code, string message) => Line(new JObject { ["t"] = "error", ["code"] = code, ["message"] = message });

    public static string Devices(RemoteExecDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var list = new JArray();
        foreach (var d in directory.Devices.Where(d => !d.Revoked))
        {
            list.Add(new JObject
            {
                ["id"] = d.Id,
                ["name"] = d.Name,
                ["machineName"] = d.MachineName,
                ["self"] = d.Self,
                ["online"] = d.Online,
                ["exec"] = d.SupportsExec,
                ["level"] = d.RemoteControlLevel,
                ["allowExec"] = d.AllowRemoteExec,
            });
        }

        return Line(new JObject { ["t"] = "devices", ["self"] = directory.SelfDeviceId, ["live"] = directory.Live, ["devices"] = list });
    }

    public static string Accepted(RemoteExecDevice device) => Line(new JObject { ["t"] = "accepted", ["device"] = device.Name, ["id"] = device.Id });

    public static string Gap() => Line(new JObject { ["t"] = "gap" });

    /// <summary>一段串流（status、out、err、exit）</summary>
    public static string Chunk(ExecChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var o = new JObject { ["t"] = chunk.Kind };
        switch (chunk.Kind)
        {
            case RemoteExecRules.KindStatus:
                o["code"] = chunk.Code;
                o["text"] = chunk.Text;
                break;
            case RemoteExecRules.KindOut:
            case RemoteExecRules.KindErr:
                o["text"] = chunk.Text;
                break;
            case RemoteExecRules.KindExit:
                o["exitCode"] = chunk.ExitCode;
                o["code"] = chunk.Code;
                o["text"] = chunk.Text;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(chunk), chunk.Kind, "不認得的段落種類");
        }

        return Line(o);
    }

    private static string Line(JObject o) => o.ToString(Formatting.None);

    private static string? Str(JObject o, string key) => o[key] is JValue { Type: JTokenType.String, Value: string s } ? s : null;
}
