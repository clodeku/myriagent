using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Myriagent.Update;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Remote;

/// <summary>連到服務的用戶端（服務以管道的用戶端資訊查出來的，不是用戶端自己說的）</summary>
/// <param name="SessionId">用戶端程序所在的 Windows 工作階段</param>
/// <param name="UserSid">用戶端的使用者（查不到是 null）</param>
/// <param name="SessionUserSid">那個工作階段登入的使用者（沒有人登入、查不到是 null）</param>
internal sealed record DeskServiceClientInfo(int SessionId, string? UserSid, string? SessionUserSid);

/// <summary>服務對 <c>hello</c> 的回覆</summary>
/// <param name="System">服務以 LocalSystem 執行（開發用的前景模式是 false：碰不到安全桌面）</param>
internal sealed record DeskServiceHello(int Proto, string Version, string Build, bool System, int Session, IReadOnlyList<string> Features);

/// <summary>服務對 <c>probe</c> 的回覆：那個工作階段目前的輸入桌面</summary>
/// <param name="Desktop">輸入桌面的名稱（Default、Winlogon、Screen-saver）；開不了是 null</param>
/// <param name="Secure">是安全桌面（鎖定畫面、UAC 的確認畫面、登入畫面）</param>
/// <param name="Error">開不了時的 Win32 錯誤碼（開得了是 0）</param>
/// <param name="Desktops">那個工作階段的視窗站裡有哪些桌面（列不出來是空的；診斷用）</param>
internal sealed record DeskServiceProbe(int Session, string? Desktop, bool Secure, int Error, IReadOnlyList<string> Desktops);

/// <summary>服務回的錯誤</summary>
internal sealed record DeskServiceError(string Code, string Message);

/// <summary>要幫手擷取的範圍與大小：螢幕上的矩形（虛擬桌面的像素）縮成 <c>OutW</c>×<c>OutH</c></summary>
/// <param name="Force">就算和上一張一樣也要整張回（看的一方要求整個畫面重送、來源剛換過）</param>
internal sealed record DeskServiceCaptureRequest(int X, int Y, int W, int H, int OutW, int OutH, bool Force);

/// <summary>幫手對 <c>capture</c> 的回覆的第一行；<see cref="DeskServiceRules.CaptureOk"/> 時後面緊接 <c>Bytes</c> 個位元組的 BGRA（由上而下）</summary>
/// <param name="Result">ok（有新的畫面）、unchanged（和上一張一樣，沒有資料）、failed（擷取不到）</param>
/// <param name="Desktop">擷取的是哪個桌面（開不了是 null）</param>
/// <param name="Secure">那是安全桌面</param>
/// <param name="Error">擷取不到時的 Win32 錯誤碼</param>
internal sealed record DeskServiceCaptureHeader(string Result, int Bytes, string? Desktop, bool Secure, int Error);

/// <summary>裝在這台的服務（<c>install.json</c>，裝的時候寫、只有系統管理員能改）</summary>
/// <param name="Payload">服務檔的指紋（<see cref="DeskServiceRules.PayloadHash"/>）</param>
/// <param name="SignedBy">驗過的發佈簽章金鑰（開發版的服務檔沒有簽章是 null）</param>
internal sealed record DeskServiceInstall(string Service, string Version, string Build, string Payload, string Folder, string? SignedBy, string InstalledAt);

/// <summary>
/// 服務信任的伺服器（<c>trust.json</c>，安裝時由安裝器寫、只有系統管理員能改）：伺服器的身分公鑰、配對的來源、這台的裝置 id。
/// 幫手只收這台伺服器簽發、目標是這台的 <c>deskOpen</c> 帶來的金鑰加密的輸入（<see cref="Security.CommandEnvelope.VerifyDeskOpen"/>）。
/// app 當下說的不算：app 以使用者的權限執行，同一個使用者的程式改得了它
/// </summary>
internal sealed record DeskServiceTrust(string HubSpki, string Aud, string DeviceId);

/// <summary>安裝器（<c>MyriagentDesk.exe install／uninstall</c>）的結束碼</summary>
internal enum DeskServiceExit
{
    Ok = 0,
    Usage = 2,
    NotElevated = 10,
    PayloadMissing = 11,
    PayloadInvalid = 12,
    SignatureInvalid = 13,
    CopyFailed = 14,
    ServiceFailed = 15,
    StartFailed = 16,
    Busy = 17,
    Unexpected = 19,
}

/// <summary>
/// 遠端桌面的鎖定畫面支援（docs/web-protocol.md 的 deskService）：以 LocalSystem 執行的服務
/// <c>MyriagentDesk</c> 與 app 之間的本機協定、安裝的規則（純函式；桌面、服務與安裝器共用）。
/// 協定：具名管道上一行一個 JSON 物件（UTF-8、<c>\n</c>），要求 <c>{op, …}</c>、回覆 <c>{ok:true, …}</c> 或 <c>{ok:false, code, message}</c>。
/// 服務不連網、不碰同步與帳戶，只替「同一個工作階段登入的那個使用者」的程序做事
/// </summary>
internal static class DeskServiceRules
{
    public const string ServiceName = "MyriagentDesk";

    /// <summary>手動整合測試用的暫時服務名稱的開頭（後面接 4～16 個十六進位字）：絕不碰真的服務</summary>
    public const string TestServicePrefix = "MyriagentDeskTest-";

    public const string DisplayName = "Myriagent 遠端桌面的鎖定畫面支援";

    public const string Description = "讓 Clode Myriagent 的遠端桌面看得到並操作這台電腦的鎖定畫面與系統管理員確認畫面（UAC）。停用或移除後遠端桌面照常可用，只是鎖定時不能操作。";

    public const string ExeName = "MyriagentDesk.exe";

    /// <summary>目前的協定版本（<c>hello</c> 兩邊各報自己的；功能以 <see cref="DeskServiceHello.Features"/> 判斷，不比版本）</summary>
    public const int ProtocolVersion = 1;

    /// <summary>一行的上限（要求與回覆都一樣；超過就斷線）</summary>
    public const int MaxLineChars = 64 * 1024;

    public const string OpHello = "hello";

    public const string OpProbe = "probe";

    /// <summary>服務：在用戶端的工作階段開（或沿用）擷取用的幫手，回它的管道名稱</summary>
    public const string OpOpen = "open";

    /// <summary>幫手：擷取目前的輸入桌面</summary>
    public const string OpCapture = "capture";

    public const string FeatureProbe = "probe";

    /// <summary>服務認得 <see cref="OpOpen"/>、幫手認得 <see cref="OpCapture"/></summary>
    public const string FeatureCapture = "capture";

    /// <summary>幫手：驗 <c>deskOpen</c> 的信封、建立安全桌面輸入的金鑰，回自己的公鑰</summary>
    public const string OpSecureOpen = "secureOpen";

    /// <summary>幫手：解開一包以那把金鑰加密的輸入，注入目前的安全桌面</summary>
    public const string OpSecureInput = "secureInput";

    /// <summary>幫手認得 <see cref="OpSecureOpen"/>、<see cref="OpSecureInput"/></summary>
    public const string FeatureSecureInput = "secureInput";

    /// <summary>服務沒有信任的伺服器（安裝時這台還沒配對，或裝的是舊版的服務）：安全桌面只能看</summary>
    public const string CodeNoTrust = "noTrust";

    public const string TrustFile = "trust.json";

    public const string CaptureOk = "ok";

    public const string CaptureUnchanged = "unchanged";

    public const string CaptureFailed = "failed";

    /// <summary>擷取輸出的上限（和遠端桌面最大的傳送寬度一致，高度留給直立的螢幕）：超過的要求不收，幫手不會被要求配置巨大的緩衝</summary>
    public const int MaxCaptureWidth = 3840;

    public const int MaxCaptureHeight = 3840;

    /// <summary>螢幕矩形的座標與邊長的上限（虛擬桌面是 16 位元的範圍）</summary>
    public const int MaxScreenExtent = 32768;

    /// <summary>幫手多久沒有連線就自己結束</summary>
    public static readonly TimeSpan HelperIdleExit = TimeSpan.FromSeconds(30);

    public const string CodeInvalid = "invalid";

    public const string CodeUnsupported = "unsupported";

    public const string CodeDenied = "denied";

    public const string CodeFailed = "failed";

    /// <summary>現在不能做（另一個更新進行中、遠端桌面正在用幫手）：稍後再試</summary>
    public const string CodeBusy = "busy";

    // ---- 服務自己更新（不經 UAC；擁有者 2026-11-13 決定） ----
    // app 把新版的服務檔、發佈清單與簽章經管道分段送進服務（服務不去讀使用者寫得到的資料夾），服務在記憶體裡驗：
    // 清單的簽章（服務內建的發佈金鑰）、每個服務檔的雜湊、版本比現在的新。通過才寫進安裝資料夾的 incoming，
    // 以那一份（驗過的新版）執行 self-update 換版。信任的伺服器（trust.json）不動

    /// <summary>服務：開始送一份更新（回這一次的 id；之前沒送完的作廢）</summary>
    public const string OpUpdateBegin = "updateBegin";

    /// <summary>服務：送一段（照順序、接著上一段）</summary>
    public const string OpUpdatePut = "updatePut";

    /// <summary>服務：都送完了，驗過就換版（回要換成的版本；服務接著會重新啟動）</summary>
    public const string OpUpdateApply = "updateApply";

    /// <summary>服務認得 <see cref="OpUpdateBegin"/>、<see cref="OpUpdatePut"/>、<see cref="OpUpdateApply"/>，而且這個組建有內建的發佈金鑰</summary>
    public const string FeatureSelfUpdate = "selfUpdate";

    /// <summary>發佈清單與它的簽章（受管理安裝的 app 資料夾裡有；和服務檔一起送）</summary>
    public const string ManifestFile = "release.manifest.json";

    public const string SignatureFile = "release.manifest.sig";

    /// <summary>安裝資料夾底下放驗過、等著換版的那一份的子資料夾</summary>
    public const string IncomingFolder = "incoming";

    /// <summary>一段最多幾個位元組（base64 之後一行不超過 <see cref="MaxLineChars"/>）</summary>
    public const int UpdateChunkBytes = 32 * 1024;

    /// <summary>整份更新（服務檔＋清單＋簽章）的上限：服務先收進記憶體才驗</summary>
    public const long MaxUpdateBytes = 32L * 1024 * 1024;

    /// <summary>服務在 <c>%ProgramFiles%</c> 底下的資料夾（只有系統管理員能寫：LocalSystem 執行的檔絕不能放使用者寫得到的地方）</summary>
    public const string InstallFolder = @"Myriagent\DeskService";

    public const string InstallFile = "install.json";

    /// <summary>單一服務檔的上限（實際都在幾百 KB 上下；擋掉被換成巨大檔案的來源）</summary>
    public const long MaxPayloadFileBytes = 64L * 1024 * 1024;

    /// <summary>
    /// 服務要帶走的檔（和 app 同一個資料夾，都在發佈 manifest 的清單裡）。服務參考 Myriagent.Protocol，所以連它的相依一起：
    /// 少一個，裝好的服務就起不來（.NET 的宿主照 deps.json 找檔）。<c>DeskServicePayloadTests</c> 拿建置輸出的 deps.json 比對這份清單
    /// </summary>
    public static readonly IReadOnlyList<string> PayloadFiles =
    [
        "MyriagentDesk.exe",
        "MyriagentDesk.dll",
        "MyriagentDesk.runtimeconfig.json",
        "MyriagentDesk.deps.json",
        "Myriagent.Protocol.dll",
        "Newtonsoft.Json.dll",
        "Markdig.dll",
    ];

    /// <summary>管道的名稱（<c>\\.\pipe\</c> 之後的部分）；測試的暫時服務各用各的</summary>
    public static string PipeNameOf(string serviceName) => serviceName;

    /// <summary>合法的服務名稱：真的那一個，或測試用的暫時名稱</summary>
    public static bool IsServiceName([NotNullWhen(true)] string? name)
    {
        if (name == ServiceName)
            return true;
        if (name is null || !name.StartsWith(TestServicePrefix, StringComparison.Ordinal))
            return false;
        var tail = name[TestServicePrefix.Length..];
        return tail.Length is >= 4 and <= 16 && tail.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    /// <summary>這個服務裝在哪裡：真的在 <c>Myriagent\DeskService</c>，測試的暫時服務在它旁邊以服務名稱命名的資料夾</summary>
    public static string InstallRoot(string programFiles, string serviceName)
        => serviceName == ServiceName
            ? Path.Combine(programFiles, InstallFolder)
            : Path.Combine(programFiles, "Myriagent", serviceName);

    /// <summary>服務檔的指紋：每個檔「名稱:SHA-256」照 <see cref="PayloadFiles"/> 的順序、各接一個換行，整段 UTF-8 的 SHA-256（小寫十六進位）</summary>
    public static string PayloadHash(IReadOnlyDictionary<string, string> sha256ByName)
    {
        var text = new StringBuilder();
        foreach (var name in PayloadFiles)
        {
            if (!sha256ByName.TryGetValue(name, out var sha) || !IsSha256(sha))
                throw new ArgumentException($"少了服務檔的雜湊：{name}", nameof(sha256ByName));
            text.Append(name).Append(':').Append(sha).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    /// <summary>
    /// 每一份服務檔裝在自己的子資料夾（名稱取指紋的前 16 個字）：更新是「放進新的資料夾、把服務指過去、重新啟動」，
    /// 不必覆寫執行中的檔，舊的留到新的啟動成功才刪
    /// </summary>
    public static string PayloadFolder(string payloadHash) => "p-" + payloadHash[..16];

    public static bool IsPayloadFolder(string name)
        => name.Length == 18 && name.StartsWith("p-", StringComparison.Ordinal) && name.AsSpan(2).ToString().All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>服務的啟動指令：路徑一律加引號（沒有引號、路徑帶空白的服務會被 Windows 當成好幾個候選路徑）</summary>
    public static string BinPath(string exePath) => "\"" + exePath + "\" service";

    /// <summary>
    /// 服務檔和簽章過的發佈清單對不對得上：每個檔都要在清單裡、雜湊相同。對得上回 null，否則回原因。
    /// 這是完整性檢查（服務檔是那一版發佈的、沒有損毀或被換過）；發佈清單本身的簽章由呼叫端先驗
    /// </summary>
    public static string? CheckPayload(ReleaseManifest manifest, IReadOnlyDictionary<string, string> sha256ByName)
    {
        foreach (var name in PayloadFiles)
        {
            if (manifest.Files.FirstOrDefault(f => f.Path == name) is not { } listed)
                return $"發佈清單裡沒有 {name}";
            if (!sha256ByName.TryGetValue(name, out var sha) || !string.Equals(sha, listed.Sha256, StringComparison.Ordinal))
                return $"{name} 和發佈清單不符";
        }
        return null;
    }

    /// <summary>
    /// 服務收不收這個用戶端。以 LocalSystem 執行時：只收使用者工作階段（不是 0）裡、而且就是那個工作階段登入的使用者的程序——
    /// 服務只替「坐在那個工作階段的人」看與操作他自己的安全桌面，別的使用者（runas、另一個登入的人）不行。
    /// 前景模式（開發用，沒有系統權限）：只收執行它的那個使用者。收回 null，不收回原因
    /// </summary>
    public static string? Admit(DeskServiceClientInfo client, bool serviceIsSystem)
    {
        if (string.IsNullOrEmpty(client.UserSid))
            return "查不到用戶端的使用者";
        if (serviceIsSystem && client.SessionId <= 0)
            return "服務的工作階段裡的程序不能使用";
        if (string.IsNullOrEmpty(client.SessionUserSid))
            return "那個工作階段沒有使用者登入";
        return string.Equals(client.UserSid, client.SessionUserSid, StringComparison.OrdinalIgnoreCase)
            ? null
            : "不是那個工作階段登入的使用者";
    }

    /// <summary>輸入桌面是不是安全桌面：使用者的桌面叫 Default，其他（Winlogon、Screen-saver）都算；名稱拿不到的不算（另外回錯誤碼）</summary>
    public static bool IsSecureDesktop(string? desktopName)
        => !string.IsNullOrEmpty(desktopName) && !string.Equals(desktopName, "Default", StringComparison.OrdinalIgnoreCase);

    // ---- 協定 ----

    public static string HelloRequest() => Line(new JObject { ["op"] = OpHello, ["proto"] = ProtocolVersion });

    public static string ProbeRequest() => Line(new JObject { ["op"] = OpProbe });

    /// <summary>解析一行要求：不是 JSON 物件、沒有 op 回 false</summary>
    public static bool TryParseRequest(string? line, [NotNullWhen(true)] out string? op, [NotNullWhen(true)] out JObject? request)
    {
        op = null;
        request = TryObject(line);
        if (request is null || request["op"] is not JValue { Type: JTokenType.String, Value: string name } || name.Length is 0 or > 32)
        {
            request = null;
            return false;
        }
        op = name;
        return true;
    }

    public static string HelloReply(DeskServiceHello hello) => Line(new JObject
    {
        ["ok"] = true,
        ["proto"] = hello.Proto,
        ["version"] = hello.Version,
        ["build"] = hello.Build,
        ["system"] = hello.System,
        ["session"] = hello.Session,
        ["features"] = new JArray(hello.Features),
    });

    public static string ProbeReply(DeskServiceProbe probe) => Line(new JObject
    {
        ["ok"] = true,
        ["session"] = probe.Session,
        ["desktop"] = probe.Desktop,
        ["secure"] = probe.Secure,
        ["error"] = probe.Error,
        ["desktops"] = new JArray(probe.Desktops),
    });

    public static string ErrorReply(string code, string message) => Line(new JObject { ["ok"] = false, ["code"] = code, ["message"] = message });

    /// <summary>回覆是錯誤（<c>ok</c> 不是 true）時取出來；格式不對也算錯誤</summary>
    public static DeskServiceError? ErrorOf(string? line)
    {
        if (TryObject(line) is not { } o)
            return new DeskServiceError(CodeInvalid, "服務的回覆不是 JSON 物件");
        if (o["ok"] is JValue { Type: JTokenType.Boolean, Value: true })
            return null;
        return new DeskServiceError(Text(o, "code") ?? CodeFailed, Text(o, "message") ?? "服務沒有說明原因");
    }

    public static DeskServiceHello? ParseHello(string? line)
    {
        if (ErrorOf(line) is not null || TryObject(line) is not { } o || Int(o, "proto") is not { } proto || Text(o, "version") is not { } version)
            return null;
        var features = o["features"] is JArray list
            ? list.OfType<JValue>().Where(v => v.Type == JTokenType.String).Select(v => (string)v.Value!).Take(64).ToList()
            : [];
        return new DeskServiceHello(proto, version, Text(o, "build") ?? ReleaseIdentity.DevBuild,
            o["system"] is JValue { Type: JTokenType.Boolean, Value: true }, Int(o, "session") ?? -1, features);
    }

    public static DeskServiceProbe? ParseProbe(string? line)
    {
        if (ErrorOf(line) is not null || TryObject(line) is not { } o || Int(o, "session") is not { } session)
            return null;
        return new DeskServiceProbe(session, Text(o, "desktop"), o["secure"] is JValue { Type: JTokenType.Boolean, Value: true }, Int(o, "error") ?? 0, Names(o, "desktops"));
    }

    /// <summary>工作階段裡的幫手（<c>MyriagentDesk.exe session-probe</c>）印出的一行 → 探測結果；格式不對回 null</summary>
    public static DeskServiceProbe? ParseHelperProbe(string? line, int session)
    {
        if (TryObject(line) is not { } o)
            return null;
        var desktop = Text(o, "desktop");
        var error = Int(o, "error") ?? 0;
        if (desktop is null && error == 0)
            return null;
        return new DeskServiceProbe(session, desktop, IsSecureDesktop(desktop), error, Names(o, "desktops"));
    }

    public static string HelperProbeLine(string? desktop, int error, IReadOnlyList<string> desktops)
        => Line(new JObject { ["desktop"] = desktop, ["error"] = error, ["desktops"] = new JArray(desktops) });

    // ---- 擷取：app 先向服務 open 拿到幫手的管道，再直接向幫手 capture ----

    /// <summary>
    /// 幫手的管道名稱：服務名稱＋工作階段＋每次開幫手新產生的亂數（32 個十六進位字）。亂數不是秘密（管道本來就只收那個工作階段登入的使用者），
    /// 只是讓同一個工作階段前後兩個幫手不撞名
    /// </summary>
    public static string HelperPipeName(string serviceName, int sessionId, string nonce) => $"{serviceName}-s{sessionId}-{nonce}";

    public static bool IsHelperPipeName([NotNullWhen(true)] string? name)
    {
        if (name is null || name.Length > 96)
            return false;
        var s = name.LastIndexOf("-s", StringComparison.Ordinal);
        if (s <= 0 || !IsServiceName(name[..s]))
            return false;
        var parts = name[(s + 2)..].Split('-');
        return parts.Length == 2 && parts[0].Length is >= 1 and <= 6 && parts[0].All(char.IsAsciiDigit)
               && parts[1].Length == 32 && parts[1].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    /// <summary>
    /// 幫手收不收這個用戶端：除了 <see cref="Admit"/>，用戶端還要和幫手在同一個工作階段——幫手看的是自己那個工作階段的畫面，
    /// 另一個工作階段登入的人（切換使用者）不能經它看到這邊
    /// </summary>
    public static string? AdmitHelper(DeskServiceClientInfo client, int helperSession, bool helperIsSystem)
    {
        if (Admit(client, helperIsSystem) is { } denied)
            return denied;
        return client.SessionId == helperSession ? null : "不是這個工作階段的程序";
    }

    public static string OpenRequest() => Line(new JObject { ["op"] = OpOpen });

    public static string OpenReply(string helperPipe) => Line(new JObject { ["ok"] = true, ["pipe"] = helperPipe });

    /// <summary>服務對 <c>open</c> 的回覆 → 幫手的管道名稱；錯誤、格式不對、名稱不合格回 null</summary>
    public static string? ParseOpen(string? line)
        => ErrorOf(line) is null && TryObject(line) is { } o && Text(o, "pipe") is { } pipe && IsHelperPipeName(pipe) ? pipe : null;

    public static string CaptureRequest(DeskServiceCaptureRequest r) => Line(new JObject
    {
        ["op"] = OpCapture,
        ["x"] = r.X,
        ["y"] = r.Y,
        ["w"] = r.W,
        ["h"] = r.H,
        ["outW"] = r.OutW,
        ["outH"] = r.OutH,
        ["force"] = r.Force,
    });

    /// <summary>解析擷取的要求：座標與大小都要在範圍內（幫手照 <c>OutW×OutH×4</c> 配置緩衝，所以輸出大小一定要有上限）；不合格回 null</summary>
    public static DeskServiceCaptureRequest? ParseCaptureRequest(JObject request)
    {
        if (Int(request, "x") is not { } x || Int(request, "y") is not { } y || Int(request, "w") is not { } w || Int(request, "h") is not { } h
            || Int(request, "outW") is not { } outW || Int(request, "outH") is not { } outH)
            return null;
        if (Math.Abs(x) > MaxScreenExtent || Math.Abs(y) > MaxScreenExtent || w is < 1 or > MaxScreenExtent || h is < 1 or > MaxScreenExtent)
            return null;
        if (outW is < 1 or > MaxCaptureWidth || outH is < 1 or > MaxCaptureHeight)
            return null;
        return new DeskServiceCaptureRequest(x, y, w, h, outW, outH, request["force"] is JValue { Type: JTokenType.Boolean, Value: true });
    }

    /// <summary>一張畫面的位元組數（BGRA）</summary>
    public static int CaptureBytes(int outW, int outH) => checked(outW * outH * 4);

    public static string CaptureReply(DeskServiceCaptureHeader header) => Line(new JObject
    {
        ["ok"] = true,
        ["result"] = header.Result,
        ["bytes"] = header.Bytes,
        ["desktop"] = header.Desktop,
        ["secure"] = header.Secure,
        ["error"] = header.Error,
    });

    /// <summary>
    /// 幫手對 <c>capture</c> 的回覆的第一行。<paramref name="expectedBytes"/> 是用戶端要的那張的大小：ok 的位元組數一定要正好是它
    /// （用戶端照它讀、寫進自己的緩衝），其他結果一定是 0；對不上、格式不對回 null（用戶端當成連線壞了）
    /// </summary>
    public static DeskServiceCaptureHeader? ParseCaptureReply(string? line, int expectedBytes)
    {
        if (ErrorOf(line) is not null || TryObject(line) is not { } o || Text(o, "result") is not { } result || Int(o, "bytes") is not { } bytes)
            return null;
        if (result is not (CaptureOk or CaptureUnchanged or CaptureFailed))
            return null;
        if (bytes != (result == CaptureOk ? expectedBytes : 0))
            return null;
        return new DeskServiceCaptureHeader(result, bytes, Text(o, "desktop"), o["secure"] is JValue { Type: JTokenType.Boolean, Value: true }, Int(o, "error") ?? 0);
    }

    // ---- 安全桌面的輸入 ----

    /// <summary>信任的伺服器合不合格：P-256 的公鑰、https 的來源（不帶路徑）、這台的裝置 id</summary>
    public static bool IsTrust([NotNullWhen(true)] DeskServiceTrust? trust)
        => trust is not null && Security.P256KeyRules.IsValidSpki(trust.HubSpki) && IsOrigin(trust.Aud) && Sync.SyncIds.IsDeviceId(trust.DeviceId);

    private static bool IsOrigin(string? aud)
        => aud is { Length: > 8 and <= 256 } && Uri.TryCreate(aud, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
           && string.Equals(uri.GetLeftPart(UriPartial.Authority), aud, StringComparison.Ordinal);

    public static string SerializeTrust(DeskServiceTrust trust) => new JObject
    {
        ["schema"] = 1,
        ["hubSpki"] = trust.HubSpki,
        ["aud"] = trust.Aud,
        ["deviceId"] = trust.DeviceId,
    }.ToString(Formatting.Indented);

    public static DeskServiceTrust? ParseTrust(string? json)
    {
        if (TryObject(json) is not { } o || Text(o, "hubSpki") is not { } spki || Text(o, "aud") is not { } aud || Text(o, "deviceId") is not { } device)
            return null;
        var trust = new DeskServiceTrust(spki, aud, device);
        return IsTrust(trust) ? trust : null;
    }

    /// <summary>
    /// 安裝器的信任參數（<c>--hub-key</c>、<c>--aud</c>、<c>--device</c>）：三個都給而且合格才算；都沒給＝這台還沒配對，裝了只能看。
    /// 只給一部分或不合格回 false（安裝器當成用法錯誤，不裝）
    /// </summary>
    public static bool TryTrustArgs(string? hubKey, string? aud, string? device, out DeskServiceTrust? trust)
    {
        trust = null;
        if (hubKey is null && aud is null && device is null)
            return true;
        if (hubKey is null || aud is null || device is null)
            return false;
        var candidate = new DeskServiceTrust(hubKey, aud, device);
        if (!IsTrust(candidate))
            return false;
        trust = candidate;
        return true;
    }

    public static string SecureOpenRequest(Security.CommandEnvelope envelope) => Line(new JObject
    {
        ["op"] = OpSecureOpen,
        ["envelope"] = new JObject { ["v"] = envelope.V, ["body"] = envelope.Body, ["sig"] = envelope.Sig },
    });

    /// <summary>要求裡的信封；沒有、格式不對回 null</summary>
    public static Security.CommandEnvelope? EnvelopeOf(JObject request)
        => request["envelope"] is JObject e && Text(e, "v") is { } v && Text(e, "body") is { } body && Text(e, "sig") is { } sig
            ? new Security.CommandEnvelope(v, body, sig)
            : null;

    public static string SecureOpenReply(string session, string servicePublic) => Line(new JObject { ["ok"] = true, ["session"] = session, ["pub"] = servicePublic });

    /// <summary>幫手對 <c>secureOpen</c> 的回覆 → 服務這個工作階段的公鑰；錯誤、格式不對回 null</summary>
    public static string? ParseSecureOpen(string? line, string session)
        => ErrorOf(line) is null && TryObject(line) is { } o && Text(o, "session") == session && Text(o, "pub") is { } pub && DeskSealRules.IsPublicKey(pub) ? pub : null;

    public static string SecureInputRequest(string session, long n, string ct) => Line(new JObject { ["op"] = OpSecureInput, ["session"] = session, ["n"] = n, ["ct"] = ct });

    /// <summary>解析 <c>secureInput</c> 的要求；不合格回 null</summary>
    public static (string Session, long N, string Ct)? ParseSecureInput(JObject request)
        => Text(request, "session") is { } session && DeskRules.IsSessionId(session)
           && request["n"] is JValue { Type: JTokenType.Integer, Value: long n } && n >= 1
           && Text(request, "ct") is { Length: >= 22 } ct && ct.Length <= DeskSealRules.MaxSealedBytes * 2
            ? (session, n, ct)
            : null;

    /// <summary>幫手對 <c>secureInput</c> 的回覆：<paramref name="injected"/> 是注入的事件數（0＝解開了但現在不是安全桌面，沒有注入）</summary>
    public static string SecureInputReply(int injected) => Line(new JObject { ["ok"] = true, ["injected"] = injected });

    /// <summary>回覆裡注入的事件數；錯誤、格式不對回 null</summary>
    public static int? ParseSecureInput(string? line)
        => ErrorOf(line) is null && TryObject(line) is { } o ? Int(o, "injected") : null;

    // ---- 服務自己更新 ----

    /// <summary>一份更新要送的檔：服務檔、發佈清單、簽章</summary>
    public static IReadOnlyList<string> UpdateParts { get; } = [.. PayloadFiles, ManifestFile, SignatureFile];

    private static long MaxPartBytes(string name)
        => name == ManifestFile ? ReleaseManifestRules.MaxManifestBytes : name == SignatureFile ? ReleaseManifestRules.MaxSignatureBytes : MaxPayloadFileBytes;

    public static string UpdateBeginRequest(IReadOnlyDictionary<string, int> sizes) => Line(new JObject
    {
        ["op"] = OpUpdateBegin,
        ["parts"] = new JArray(UpdateParts.Select(name => new JObject { ["name"] = name, ["bytes"] = sizes[name] })),
    });

    /// <summary>
    /// <c>updateBegin</c> 的內容：<see cref="UpdateParts"/> 的每個檔各一次、不多不少，大小在各自的上限內、合計不超過 <see cref="MaxUpdateBytes"/>。
    /// 不合格回 null
    /// </summary>
    public static IReadOnlyDictionary<string, int>? ParseUpdateBegin(JObject request)
    {
        if (request["parts"] is not JArray parts || parts.Count != UpdateParts.Count)
            return null;
        var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
        long total = 0;
        foreach (var token in parts)
        {
            if (token is not JObject part || Text(part, "name") is not { } name || !UpdateParts.Contains(name) || sizes.ContainsKey(name)
                || Int(part, "bytes") is not { } bytes || bytes <= 0 || bytes > MaxPartBytes(name))
                return null;
            sizes[name] = bytes;
            total += bytes;
        }

        return total <= MaxUpdateBytes ? sizes : null;
    }

    public static string UpdateBeginReply(string id) => Line(new JObject { ["ok"] = true, ["id"] = id });

    /// <summary><c>updateBegin</c> 的回覆裡這一次的 id；錯誤、格式不對回 null</summary>
    public static string? ParseUpdateBegin(string? line)
        => ErrorOf(line) is null && TryObject(line) is { } o && Text(o, "id") is { } id && IsUpdateId(id) ? id : null;

    /// <summary>這一次更新的 id：32 個小寫十六進位字（服務產生的亂數）</summary>
    public static bool IsUpdateId([NotNullWhen(true)] string? id) => id is { Length: 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string UpdatePutRequest(string id, string name, long offset, ReadOnlySpan<byte> data) => Line(new JObject
    {
        ["op"] = OpUpdatePut, ["id"] = id, ["name"] = name, ["offset"] = offset, ["data"] = Convert.ToBase64String(data),
    });

    /// <summary><c>updatePut</c> 的內容；不合格（id、檔名、位移、不是 base64、空的或超過一段的上限）回 null</summary>
    public static (string Id, string Name, int Offset, byte[] Data)? ParseUpdatePut(JObject request)
    {
        if (Text(request, "id") is not { } id || !IsUpdateId(id) || Text(request, "name") is not { } name || !UpdateParts.Contains(name)
            || Int(request, "offset") is not { } offset || offset < 0 || Text(request, "data") is not { Length: > 0 } text)
            return null;
        var buffer = new byte[UpdateChunkBytes];
        return text.Length <= (UpdateChunkBytes + 2) / 3 * 4 && Convert.TryFromBase64String(text, buffer, out var written) && written > 0
            ? (id, name, offset, buffer[..written])
            : null;
    }

    public static string UpdatePutReply(int received) => Line(new JObject { ["ok"] = true, ["received"] = received });

    /// <summary><c>updatePut</c> 的回覆：這個檔到目前收了幾個位元組；錯誤、格式不對回 null</summary>
    public static int? ParseUpdatePut(string? line)
        => ErrorOf(line) is null && TryObject(line) is { } o ? Int(o, "received") : null;

    public static string UpdateApplyRequest(string id) => Line(new JObject { ["op"] = OpUpdateApply, ["id"] = id });

    public static string? ParseUpdateApply(JObject request) => Text(request, "id") is { } id && IsUpdateId(id) ? id : null;

    public static string UpdateApplyReply(string release) => Line(new JObject { ["ok"] = true, ["release"] = release });

    /// <summary><c>updateApply</c> 的回覆：要換成的版本（服務接著會重新啟動）；錯誤、格式不對回 null</summary>
    public static string? ParseUpdateApply(string? line)
        => ErrorOf(line) is null && TryObject(line) is { } o && Text(o, "release") is { } release && ReleaseIdentity.TryParseRelease(release, out _) ? release : null;

    /// <summary>
    /// 一份更新能不能不經 UAC 就裝（純函式；服務收完之後、換版的程式動手之前各驗一次）。全部都要成立：
    /// 這個組建有內建的發佈金鑰（開發版、沒有金鑰的組建一律不自動更新）；有發佈清單與簽章、簽章是內建的金鑰簽的；
    /// 每個服務檔都在清單裡、雜湊相同（發佈清單一定是發佈版：開發版的 release 連解析都不過）；**版本比現在的新**——比執行中的服務新、也比安裝紀錄上的新
    /// （同一個使用者的程式不能拿舊的、簽章照樣有效的版本把系統權限的服務降回去），而且不是已經裝著的那一份。
    /// 通過回 null 並給要裝的版本與簽章的金鑰，否則回原因
    /// </summary>
    /// <param name="parts"><see cref="UpdateParts"/> 的內容（少了的算不通過）</param>
    /// <param name="running">執行中的服務的版本（換版的程式自己就是新版，給 null）</param>
    public static string? CheckSelfUpdate(IReadOnlyDictionary<string, byte[]> parts, IReadOnlyList<ReleaseKey> trustedKeys, ReleaseIdentity? running,
        DeskServiceInstall? installed, out ReleaseIdentity? release, out string? signedBy)
    {
        release = null;
        signedBy = null;
        if (trustedKeys.Count == 0)
            return "這個組建沒有內建的發佈簽章金鑰，不自動更新";
        if (!parts.TryGetValue(ManifestFile, out var manifestBytes) || !parts.TryGetValue(SignatureFile, out var signatureBytes))
            return "沒有發佈清單或簽章";
        if (!ReleaseManifestRules.TryParseSignature(Encoding.UTF8.GetString(signatureBytes), out var signature))
            return "簽章檔的格式不對";
        if (ReleaseManifestRules.VerifySignature(manifestBytes, signature, trustedKeys, []) is { } bad)
            return bad;
        if (!ReleaseManifestRules.TryParse(manifestBytes, out var manifest, out var error))
            return error ?? "發佈清單解析不了";
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in PayloadFiles)
        {
            if (!parts.TryGetValue(name, out var bytes))
                return $"少了服務檔 {name}";
            hashes[name] = ReleaseManifestRules.Sha256Hex(bytes);
        }

        if (CheckPayload(manifest, hashes) is { } mismatch)
            return mismatch;
        if (running is { IsDev: false } && manifest.Identity.Version <= running.Version)
            return $"{manifest.Identity.Release} 不比執行中的服務（{running.Release}）新";
        if (installed is not null)
        {
            if (string.Equals(installed.Payload, PayloadHash(hashes), StringComparison.Ordinal))
                return "和已經安裝的是同一份";
            if (installed.Build != ReleaseIdentity.DevBuild && ReleaseVersion.TryParse(installed.Version, out var have) && manifest.Identity.Version <= have)
                return $"{manifest.Identity.Release} 不比已經安裝的（{installed.Version}）新";
        }

        release = manifest.Identity;
        signedBy = signature.Kid;
        return null;
    }

    // ---- 安裝紀錄 ----

    public static string SerializeInstall(DeskServiceInstall install) => new JObject
    {
        ["schema"] = 1,
        ["service"] = install.Service,
        ["version"] = install.Version,
        ["build"] = install.Build,
        ["payload"] = install.Payload,
        ["folder"] = install.Folder,
        ["signedBy"] = install.SignedBy,
        ["installedAt"] = install.InstalledAt,
    }.ToString(Formatting.Indented);

    public static DeskServiceInstall? ParseInstall(string? json)
    {
        if (TryObject(json) is not { } o || Text(o, "service") is not { } service || !IsServiceName(service))
            return null;
        if (Text(o, "version") is not { } version || Text(o, "payload") is not { } payload || !IsSha256(payload)
            || Text(o, "folder") is not { } folder || !IsPayloadFolder(folder))
            return null;
        return new DeskServiceInstall(service, version, Text(o, "build") ?? ReleaseIdentity.DevBuild, payload, folder, Text(o, "signedBy"), Text(o, "installedAt") ?? string.Empty);
    }

    /// <summary>
    /// app 帶的服務檔要不要拿去更新裝好的：指紋一樣就不用；兩邊都是發佈版時只往新的更新（app 回滾到舊版時不把服務降回去），
    /// 有一邊是開發版就只看指紋不同
    /// </summary>
    public static bool NeedsUpdate(DeskServiceInstall installed, string bundledVersion, string bundledBuild, string bundledPayload)
    {
        if (string.Equals(installed.Payload, bundledPayload, StringComparison.Ordinal))
            return false;
        if (installed.Build == ReleaseIdentity.DevBuild || bundledBuild == ReleaseIdentity.DevBuild)
            return true;
        if (!ReleaseVersion.TryParse(installed.Version, out var have) || !ReleaseVersion.TryParse(bundledVersion, out var bundled))
            return true;
        return bundled > have;
    }

    /// <summary>安裝器的結束碼給人看的說明（app 顯示在設定頁）；成功回 null</summary>
    public static string? Describe(DeskServiceExit exit) => exit switch
    {
        DeskServiceExit.Ok => null,
        DeskServiceExit.Usage => "安裝程式的參數不對",
        DeskServiceExit.NotElevated => "沒有取得系統管理員權限",
        DeskServiceExit.PayloadMissing => "找不到服務的檔案（app 的資料夾不完整）",
        DeskServiceExit.PayloadInvalid => "服務的檔案不正常（是捷徑、連結或大小不合理）",
        DeskServiceExit.SignatureInvalid => "服務的檔案和這一版的發佈簽章不符，沒有安裝",
        DeskServiceExit.CopyFailed => "複製服務的檔案失敗",
        DeskServiceExit.ServiceFailed => "向 Windows 登記服務失敗",
        DeskServiceExit.StartFailed => "服務裝好了但啟動不了",
        DeskServiceExit.Busy => "另一個安裝正在進行",
        _ => "安裝程式發生未預期的錯誤",
    };

    /// <summary>程序的結束碼 → <see cref="DeskServiceExit"/>（不認得的一律 <see cref="DeskServiceExit.Unexpected"/>）</summary>
    public static DeskServiceExit ExitOf(int code)
        => Enum.IsDefined(typeof(DeskServiceExit), code) ? (DeskServiceExit)code : DeskServiceExit.Unexpected;

    private static bool IsSha256([NotNullWhen(true)] string? text)
        => text is { Length: 64 } && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Line(JObject o) => o.ToString(Formatting.None);

    private static JObject? TryObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLineChars)
            return null;
        try
        {
            using var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, MaxDepth = 8 };
            return JToken.ReadFrom(reader) as JObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>名稱的清單（桌面名稱都很短）：只留字串、最多 32 個、每個最多 64 字</summary>
    private static IReadOnlyList<string> Names(JObject o, string name)
        => o[name] is JArray list
            ? list.OfType<JValue>().Where(v => v.Type == JTokenType.String).Select(v => (string)v.Value!).Where(s => s.Length is > 0 and <= 64).Take(32).ToList()
            : [];

    private static string? Text(JObject o, string name) => o[name] is JValue { Type: JTokenType.String, Value: string s } ? s : null;

    private static int? Int(JObject o, string name) => o[name] is JValue { Type: JTokenType.Integer, Value: long n } && n is >= int.MinValue and <= int.MaxValue ? (int)n : null;
}
