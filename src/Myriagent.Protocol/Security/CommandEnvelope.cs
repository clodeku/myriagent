using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Myriagent.Remote;
using Myriagent.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Security;

/// <summary>
/// 簽信封的委派：輸入要簽的位元組（<see cref="CommandEnvelope.SignedData"/>），回傳 P1363 簽章的 base64url。
/// 參數是 <see cref="ReadOnlySpan{T}"/>，伺服器的 <c>HubIdentity.Sign</c> 可以直接綁上（<c>Func&lt;byte[], string&gt;</c> 綁不上）
/// </summary>
internal delegate string EnvelopeSigner(ReadOnlySpan<byte> data);

/// <summary>信封的簽發者種類（第 4 階段只有瀏覽器；桌面遠端操作加上裝置，docs/sync-protocol.md 14.7）</summary>
internal static class CommandIssuerKinds
{
    public const string Browser = "browser";

    /// <summary>
    /// 另一台已配對的桌面（擁有者 2026-10-22 決定「已配對就能操作」）：標籤是那台電腦的名稱。只有收到信封的連線協商出 <c>peer1</c>
    /// 時電腦才接受（<see cref="CommandEnvelopeTarget.AcceptsDeviceIssuer"/>）
    /// </summary>
    public const string Device = "device";

    /// <summary>
    /// 擁有者分享對話給他的協作人（docs/plans/2026-10-30-collaborators.md、sync-protocol 14.14）：標籤是協作人的顯示名稱，電腦把它記在使用者訊息與卡片上。
    /// 只有收到信封的連線協商出 <c>collab1</c> 時電腦才接受（<see cref="CommandEnvelopeTarget.AcceptsCollaboratorIssuer"/>），而且只收
    /// <see cref="Myriagent.Sync.CollaboratorRules.RemoteCommands"/> 的指令（閘門再查一次）
    /// </summary>
    public const string Collaborator = "collaborator";
}

/// <summary>
/// 簽發者：種類與給人看的標籤（瀏覽器是瀏覽器與作業系統，例如「Chrome・Windows」；裝置是電腦名稱；≤64 字、沒有 Cc／Cf 類字元）
/// </summary>
internal sealed record CommandIssuer(string Kind, string Label);

/// <summary>伺服器工作階段的驗證狀態：最後一次輸入密碼的伺服器時間（沒有為 null）、擁有者密碼是不是預設值（閘門 5f、5g）</summary>
internal sealed record CommandAuth(long? UvAt, bool DefaultPassword);

/// <summary>
/// 信封的內容（docs/sync-protocol.md 14.4 的 <c>body</c>；設計 14.11 D2）。時間一律是伺服器時間（Unix 毫秒）
/// </summary>
/// <param name="Aud">配對時記下的伺服器來源</param>
/// <param name="Cid">伺服器產生的指令 id（ULID，D17；電腦以它去重）</param>
/// <param name="Dev">目標裝置</param>
/// <param name="Conn">
/// 目標裝置這條連線 Hello 回應的 nonceS（重簽時換成新連線的）：每條連線在 Hello 新產生一次、在 hubSig 的範圍內，裝置在 HelloResponse 看得到；
/// 不是 SignalR 的連線 id（裝置略過 negotiate，拿不到它）
/// </param>
/// <param name="Conv">對話（型錄路由 Owner 的指令必帶；路由 Device 的不帶）</param>
/// <param name="Epoch">對話的 epoch（與 <paramref name="Conv"/> 一起帶）</param>
/// <param name="Cmd">網頁的指令物件（形狀照型錄，RemoteCommandShapeRules）</param>
/// <param name="Tier">伺服器算的層級（0～2；電腦另以自己的事實重算後取較高者，D5）</param>
/// <param name="Durable">電腦不在線時伺服器保存、上線後重簽（只限型錄標了 DurableAllowed 的，D16）</param>
/// <param name="Created">指令建立的時間（重簽時不變，7 天上限以它算）</param>
/// <param name="Iat">這次簽發的時間</param>
/// <param name="Exp">這次簽發的到期時間（≤ iat＋60 秒）</param>
internal sealed record CommandEnvelopeBody(
    string Aud,
    string Cid,
    string Dev,
    string Conn,
    string? Conv,
    long? Epoch,
    JObject Cmd,
    int Tier,
    bool FullAccess,
    bool Durable,
    long Created,
    long Iat,
    long Exp,
    CommandIssuer Issuer,
    CommandAuth Auth);

/// <summary>驗過的 <c>deskOpen</c> 信封裡服務要的東西（<see cref="CommandEnvelope.VerifyDeskOpen"/>）</summary>
internal sealed record DeskOpenProof(string SessionId, string ViewerKey, string IssuerKind, string IssuerLabel);

/// <summary>
/// 裝置驗信封時的「自己」：釘選的伺服器身分公鑰（SPKI）、配對的伺服器來源、自己的 deviceId、收到信封的這條連線的 conn
/// （<paramref name="ConnectionId"/>：這條連線 Hello 回應的 nonceS，桌面的 <c>SyncConnection.EnvelopeConn</c>）。
/// <paramref name="AcceptsDeviceIssuer"/>：這條連線協商出 <c>peer1</c>，接受 <see cref="CommandIssuerKinds.Device"/> 發起的信封（第 9 步）
/// </summary>
/// <paramref name="AcceptsCollaboratorIssuer"/>：這條連線協商出 <c>collab1</c>，接受 <see cref="CommandIssuerKinds.Collaborator"/> 發起的信封（第 9 步）
internal sealed record CommandEnvelopeTarget(string HubSpki, string Aud, string DeviceId, string ConnectionId, bool AcceptsDeviceIssuer = false,
    bool AcceptsCollaboratorIssuer = false);

/// <summary>
/// <see cref="CommandEnvelope.Verify"/> 的各步（docs/sync-protocol.md 14.4 的十步；第 5 步分成 aud、dev、conn 三個名稱，
/// 遠端操作紀錄看得出是哪一個）。名稱只進遠端操作紀錄；瀏覽器看到的是 <see cref="CommandEnvelopeVerdict.Code"/>
/// </summary>
internal static class CommandEnvelopeSteps
{
    /// <summary>1 版本字串</summary>
    public const string Version = "version";

    /// <summary>2 body 的 UTF-8 ≤128 KB（不成對的代理字元也算不合格）</summary>
    public const string Size = "size";

    /// <summary>3 簽章（釘選的伺服器身分公鑰）</summary>
    public const string Signature = "signature";

    /// <summary>4 解析：只收一種解讀、鍵與型別照 SP 14.4</summary>
    public const string Parse = "parse";

    /// <summary>5 aud 是這台配對的伺服器</summary>
    public const string Audience = "aud";

    /// <summary>5 dev 是自己</summary>
    public const string Device = "dev";

    /// <summary>5 conn 是收到它的這條連線</summary>
    public const string Connection = "conn";

    /// <summary>6 cid 是 ULID 而且等於外層的 commandId</summary>
    public const string CommandId = "cid";

    /// <summary>7 tier 在 0～2（不叫 tier：閘門 5d 的步驟已經叫 tier）</summary>
    public const string TierRange = "tierRange";

    /// <summary>8 時間（伺服器時間的界線）</summary>
    public const string Time = "time";

    /// <summary>9 簽發者</summary>
    public const string Issuer = "issuer";

    /// <summary>10 cmd 與 conv、dev 一致</summary>
    public const string Target = "target";

    /// <summary>驗證的順序（先失敗的先回報）</summary>
    public static IReadOnlyList<string> Order { get; } =
        [Version, Size, Signature, Parse, Audience, Device, Connection, CommandId, TierRange, Time, Issuer, Target];
}

/// <summary>
/// 驗證的結果。<see cref="Step"/> 是 null＝合格；否則 <see cref="Code"/> 是給瀏覽器的代碼（<c>invalid</c>，時間的界線是 <c>expired</c>），
/// <see cref="Detail"/> 只進遠端操作紀錄（不含指令的內容）。解析過了（第 4 步之後）才有 <see cref="Body"/> 與 <see cref="ContentSha256"/>
/// </summary>
internal sealed record CommandEnvelopeVerdict(string? Step, string? Code, string? Detail, CommandEnvelopeBody? Body, string? ContentSha256)
{
    public bool Ok => Step == null;

    /// <summary>算進竄改計數（D24：簽章不符、aud／dev 不符，10 分鐘內 3 次以上自動暫停遠端操作）；conn 不符不算（重連的競態）</summary>
    public bool CountsAsTampering => Step is CommandEnvelopeSteps.Signature or CommandEnvelopeSteps.Audience or CommandEnvelopeSteps.Device;
}

/// <summary>
/// 遠端指令的信封 <c>myriagent-hub-cmd-v1</c>（設計 8.11、14.11 D2、D16、D17；docs/sync-protocol.md 14.4）：伺服器以身分金鑰
/// （Hello 的 hubSig 用的同一把）簽 <c>UTF-8("myriagent-hub-cmd-v1\n" + body)</c>，裝置以配對時釘選的公鑰驗——簽的是 body 字串的原始位元組、
/// 驗完才解析，不需要 JSON 正規化。一個指令一個信封，只對一台電腦、一條連線有效（D3）。
/// WP4.1 只立格式與驗證：伺服器在 WP4.5 才簽（<see cref="TrySign"/>），電腦在 WP4.4 才驗（<see cref="Verify"/>）
/// </summary>
/// <param name="V">版本（<see cref="Version"/>）</param>
/// <param name="Body">內容的 JSON 字串（<see cref="SerializeBody"/>）</param>
/// <param name="Sig">P1363 簽章的 base64url</param>
internal sealed record CommandEnvelope(string V, string Body, string Sig)
{
    public const string Version = "myriagent-hub-cmd-v1";

    /// <summary>body 的 UTF-8 上限</summary>
    public const int MaxBodyBytes = 128 * 1024;

    /// <summary>一次簽發的有效時間上限：<c>exp − iat</c></summary>
    public const long MaxLifetimeMs = 60_000;

    /// <summary>非 durable 的指令：<c>exp − created</c> 的上限（不排隊，D16）</summary>
    public const long MaxNonDurableAgeMs = 60_000;

    /// <summary>durable 的指令：伺服器時間的上界減 <c>created</c> 的上限</summary>
    public const long MaxDurableAgeMs = 7L * 24 * 60 * 60 * 1000;

    /// <summary><c>iat</c> 最多可以比伺服器時間的下界晚多少（之後的算還沒生效）</summary>
    public const long NotBeforeLeewayMs = 15_000;

    public const int MaxIssuerLabelLength = 64;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly int PrefixBytes = Encoding.UTF8.GetByteCount(Version + "\n");

    private static readonly HashSet<string> BodyKeys =
        ["aud", "cid", "dev", "conn", "conv", "epoch", "cmd", "tier", "fullAccess", "durable", "created", "iat", "exp", "issuer", "auth"];

    /// <summary>要簽的位元組：<c>UTF-8("myriagent-hub-cmd-v1\n" + body)</c>。body 有不成對的代理字元時丟 <see cref="ArgumentException"/></summary>
    public static byte[] SignedData(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return TryEncode(body, out var data) ? data : throw new ArgumentException("body 有不成對的代理字元", nameof(body));
    }

    /// <summary>
    /// body 的 JSON（不縮排）：鍵依 <c>aud, cid, dev, conn, conv, epoch, cmd, tier, fullAccess, durable, created, iat, exp, issuer {kind, label},
    /// auth {uvAt, defaultPassword}</c> 的順序；<c>conv</c>、<c>epoch</c>、<c>auth.uvAt</c> 是 null 時省略；<c>cmd</c> 照物件原本的鍵順序
    /// </summary>
    public static string SerializeBody(CommandEnvelopeBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return Write(w =>
        {
            w.WriteStartObject();
            w.WritePropertyName("aud");
            w.WriteValue(body.Aud);
            w.WritePropertyName("cid");
            w.WriteValue(body.Cid);
            w.WritePropertyName("dev");
            w.WriteValue(body.Dev);
            w.WritePropertyName("conn");
            w.WriteValue(body.Conn);
            if (body.Conv != null)
            {
                w.WritePropertyName("conv");
                w.WriteValue(body.Conv);
            }

            if (body.Epoch is { } epoch)
            {
                w.WritePropertyName("epoch");
                w.WriteValue(epoch);
            }

            w.WritePropertyName("cmd");
            body.Cmd.WriteTo(w);
            w.WritePropertyName("tier");
            w.WriteValue(body.Tier);
            w.WritePropertyName("fullAccess");
            w.WriteValue(body.FullAccess);
            w.WritePropertyName("durable");
            w.WriteValue(body.Durable);
            w.WritePropertyName("created");
            w.WriteValue(body.Created);
            w.WritePropertyName("iat");
            w.WriteValue(body.Iat);
            w.WritePropertyName("exp");
            w.WriteValue(body.Exp);
            w.WritePropertyName("issuer");
            WriteIssuer(w, body.Issuer);
            w.WritePropertyName("auth");
            w.WriteStartObject();
            if (body.Auth.UvAt is { } uvAt)
            {
                w.WritePropertyName("uvAt");
                w.WriteValue(uvAt);
            }

            w.WritePropertyName("defaultPassword");
            w.WriteValue(body.Auth.DefaultPassword);
            w.WriteEndObject();
            w.WriteEndObject();
        });
    }

    /// <summary>
    /// 伺服器簽信封（WP4.5；<paramref name="signer"/> 綁 <c>HubIdentity.Sign</c>）。body 的 UTF-8 超過 <see cref="MaxBodyBytes"/>、
    /// 或有不成對的代理字元（簽不出唯一的位元組）時回 false、不呼叫 <paramref name="signer"/>。
    /// 只檢查簽得出來；內容合不合格由簽之前的型錄、形狀檢查決定（要確認時以自己的公鑰呼叫 <see cref="Verify"/>）
    /// </summary>
    public static bool TrySign(CommandEnvelopeBody body, EnvelopeSigner signer, out CommandEnvelope? envelope)
    {
        ArgumentNullException.ThrowIfNull(signer);
        envelope = null;
        var json = SerializeBody(body);
        if (!TrySizedData(json, out var data))
            return false;
        envelope = new CommandEnvelope(Version, json, signer(data));
        return true;
    }

    /// <summary>
    /// 內容摘要（D2、D17；兩邊都存成 <c>content_sha256</c>）：不隨重簽改變的欄位依
    /// <c>[aud, cid, dev, conv, epoch, cmd, tier, fullAccess, durable, created, issuer {kind, label}, auth {uvAt, defaultPassword}]</c>
    /// 組成 JSON 陣列（不縮排；<c>conv</c>、<c>epoch</c>、<c>uvAt</c> 沒有時是 null；<c>cmd</c> 照物件的鍵順序）後取 SHA-256 的 64 個小寫十六進位。
    /// <c>conn</c>、<c>iat</c>、<c>exp</c> 不在裡面，所以 durable 重簽後同一個指令的摘要不變
    /// </summary>
    public static string ContentDigest(CommandEnvelopeBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var json = Write(w =>
        {
            w.WriteStartArray();
            w.WriteValue(body.Aud);
            w.WriteValue(body.Cid);
            w.WriteValue(body.Dev);
            w.WriteValue(body.Conv);
            w.WriteValue(body.Epoch);
            body.Cmd.WriteTo(w);
            w.WriteValue(body.Tier);
            w.WriteValue(body.FullAccess);
            w.WriteValue(body.Durable);
            w.WriteValue(body.Created);
            WriteIssuer(w, body.Issuer);
            w.WriteStartObject();
            w.WritePropertyName("uvAt");
            w.WriteValue(body.Auth.UvAt);
            w.WritePropertyName("defaultPassword");
            w.WriteValue(body.Auth.DefaultPassword);
            w.WriteEndObject();
            w.WriteEndArray();
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>
    /// 裝置驗信封（docs/sync-protocol.md 14.4 的十步，順序固定、先失敗的先回報；WP4.4 在驗過之後才查 <c>commands_seen</c> 去重）。
    /// <paramref name="commandId"/> 是外層 <c>CommandDelivery.commandId</c>；<paramref name="bounds"/> 是收到時的伺服器時間界線
    /// （<see cref="ServerClockRules.Bounds"/>；null＝不可用，除了 <c>stop</c> 一律 <c>expired</c>）。
    /// 型錄的路由與形狀不在這裡判斷（閘門 5a、5b）：這裡只看信封本身與 <c>cmd</c> 和 <c>conv</c>、<c>dev</c> 的一致
    /// </summary>
    public static CommandEnvelopeVerdict Verify(CommandEnvelope? envelope, string? commandId, CommandEnvelopeTarget target, ServerTimeBounds? bounds)
        => VerifyCore(envelope, commandId, target, bounds);

    /// <summary>
    /// 鎖定畫面支援的服務（系統權限）驗 <c>deskOpen</c> 的信封（sync-protocol 14.16「安全桌面的輸入」）：服務要確定「這把看的一方的公鑰是伺服器簽發的
    /// 遠端桌面指令帶來的」，才收那把金鑰加密的輸入、注入安全桌面——不然同一個使用者的任何程式連上服務的管道就能按 UAC。
    /// 驗：版本、大小、簽章（<paramref name="hubSpki"/> 是安裝時記下的伺服器身分金鑰，不是 app 當下說的）、只有一種解讀、
    /// <c>aud</c> 與 <c>dev</c> 是安裝時記下的那一組、指令是 <c>deskOpen</c>、帶合格的工作階段 id 與看的一方的公鑰。
    /// 不驗連線與時間：服務不知道 app 的連線、也沒有伺服器時鐘；信封被重放也沒有用——它帶的是看的一方的公鑰，
    /// 沒有那把私鑰造不出服務解得開的輸入。伺服器不簽「看自己」的 deskOpen，所以這台自己的身分發不出目標是這台的信封。
    /// 通過回工作階段與公鑰，否則回 null 與原因
    /// </summary>
    public static DeskOpenProof? VerifyDeskOpen(CommandEnvelope? envelope, string hubSpki, string aud, string deviceId, out string problem)
    {
        problem = string.Empty;
        if (envelope is null || !string.Equals(envelope.V, Version, StringComparison.Ordinal))
            return Problem("版本不是 " + Version, out problem);
        if (!TrySizedData(envelope.Body, out var data))
            return Problem("內容太大", out problem);
        if (!P256KeyRules.Verify(hubSpki, data, envelope.Sig))
            return Problem("不是安裝時記下的那台伺服器簽的", out problem);
        if (!TryParseBody(envelope.Body, out var body, out var parseProblem))
            return Problem(parseProblem, out problem);
        if (!string.Equals(body.Aud, aud, StringComparison.Ordinal) || !string.Equals(body.Dev, deviceId, StringComparison.Ordinal))
            return Problem("不是給這台電腦的", out problem);
        if (!string.Equals(body.Cmd.Value<string>("cmd"), RemoteCommandCatalog.DeskOpen, StringComparison.Ordinal))
            return Problem("不是遠端桌面的指令", out problem);
        // 只收擁有者的瀏覽器或另一台已配對的電腦發起的：協作人不能用遠端桌面（伺服器與 app 的閘門本來就擋，這裡再擋一次），不認得的種類也不收
        if (body.Issuer.Kind is not (CommandIssuerKinds.Browser or CommandIssuerKinds.Device))
            return Problem("發起的不是擁有者的瀏覽器或已配對的電腦", out problem);
        if (body.Cmd["requestId"] is not JValue { Type: JTokenType.String, Value: string session } || !DeskRules.IsSessionId(session))
            return Problem("沒有合格的工作階段 id", out problem);
        if (body.Cmd[DeskSealRules.ViewerKeyField] is not JValue { Type: JTokenType.String, Value: string viewerKey } || !DeskSealRules.IsPublicKey(viewerKey))
            return Problem("沒有看的一方的公鑰", out problem);
        return new DeskOpenProof(session, viewerKey, body.Issuer.Kind, body.Issuer.Label);

        static DeskOpenProof? Problem(string text, out string problem)
        {
            problem = text;
            return null;
        }
    }

    private static CommandEnvelopeVerdict VerifyCore(CommandEnvelope? envelope, string? commandId, CommandEnvelopeTarget target, ServerTimeBounds? bounds)
    {
        ArgumentNullException.ThrowIfNull(target);

        // 1～4：還沒有可信的內容
        if (envelope is null || !string.Equals(envelope.V, Version, StringComparison.Ordinal))
            return Fail(CommandEnvelopeSteps.Version, "版本不是 " + Version);
        if (!TrySizedData(envelope.Body, out var data))
            return Fail(CommandEnvelopeSteps.Size, "body 超過 128 KB 或有不成對的代理字元");
        if (!P256KeyRules.Verify(target.HubSpki, data, envelope.Sig))
            return Fail(CommandEnvelopeSteps.Signature, "簽章驗不過");
        if (!TryParseBody(envelope.Body, out var body, out var problem))
            return Fail(CommandEnvelopeSteps.Parse, problem);

        var digest = ContentDigest(body);
        CommandEnvelopeVerdict Reject(string step, string code, string detail) => new(step, code, detail, body, digest);

        // 5～10
        if (!string.Equals(body.Aud, target.Aud, StringComparison.Ordinal))
            return Reject(CommandEnvelopeSteps.Audience, RemoteResultCodes.Invalid, "aud 不是這台配對的伺服器");
        if (!string.Equals(body.Dev, target.DeviceId, StringComparison.Ordinal))
            return Reject(CommandEnvelopeSteps.Device, RemoteResultCodes.Invalid, "dev 不是這台");
        if (!string.Equals(body.Conn, target.ConnectionId, StringComparison.Ordinal))
            return Reject(CommandEnvelopeSteps.Connection, RemoteResultCodes.Invalid, "conn 不是收到它的這條連線");
        if (!SyncIds.IsUlid(body.Cid) || !string.Equals(body.Cid, commandId, StringComparison.Ordinal))
            return Reject(CommandEnvelopeSteps.CommandId, RemoteResultCodes.Invalid, "cid 不是 ULID 或與外層的 commandId 不同");
        if (body.Tier is < 0 or > 2)
            return Reject(CommandEnvelopeSteps.TierRange, RemoteResultCodes.Invalid, "tier 不在 0～2");
        if (CheckTime(body, bounds) is { } time)
            return Reject(CommandEnvelopeSteps.Time, time.Code, time.Detail);
        if (!IsAcceptedIssuerKind(body.Issuer.Kind, target.AcceptsDeviceIssuer, target.AcceptsCollaboratorIssuer))
            return Reject(CommandEnvelopeSteps.Issuer, RemoteResultCodes.Invalid, "issuer.kind 不是這條連線接受的種類");
        if (!IsValidIssuerLabel(body.Issuer.Label))
            return Reject(CommandEnvelopeSteps.Issuer, RemoteResultCodes.Invalid, "issuer.label 太長或有控制、格式字元");
        if (CheckTarget(body) is { } mismatch)
            return Reject(CommandEnvelopeSteps.Target, RemoteResultCodes.Invalid, mismatch);
        return new CommandEnvelopeVerdict(null, null, null, body, digest);
    }

    /// <summary>
    /// 第 9 步的種類：<see cref="CommandIssuerKinds.Browser"/>，或連線協商出 <c>peer1</c> 時的 <see cref="CommandIssuerKinds.Device"/>、
    /// 協商出 <c>collab1</c> 時的 <see cref="CommandIssuerKinds.Collaborator"/>（大小寫要相同）
    /// </summary>
    public static bool IsAcceptedIssuerKind(string? kind, bool acceptsDeviceIssuer, bool acceptsCollaboratorIssuer = false)
        => string.Equals(kind, CommandIssuerKinds.Browser, StringComparison.Ordinal)
           || acceptsDeviceIssuer && string.Equals(kind, CommandIssuerKinds.Device, StringComparison.Ordinal)
           || acceptsCollaboratorIssuer && string.Equals(kind, CommandIssuerKinds.Collaborator, StringComparison.Ordinal);

    /// <summary>
    /// 簽發者標籤：合格的 UTF-16（沒有不成對的代理字元）、≤<see cref="MaxIssuerLabelLength"/> 個 Unicode 字元（空字串可以），
    /// 沒有 Cc（控制）、Cf（格式，例如 U+202E 由右至左覆寫、U+200B 零寬空格）、Zl、Zp（U+2028、U+2029）類的字元——
    /// 本機通知與遠端操作紀錄會顯示它，不能藉它改寫顯示的方向或換行
    /// </summary>
    public static bool IsValidIssuerLabel(string? label)
    {
        if (label is null)
            return false;
        var rest = label.AsSpan();
        var count = 0;
        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(rest, out var rune, out var used) != OperationStatus.Done)
                return false;
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                return false;
            if (++count > MaxIssuerLabelLength)
                return false;
            rest = rest[used..];
        }

        return true;
    }

    private static CommandEnvelopeVerdict Fail(string step, string detail) => new(step, RemoteResultCodes.Invalid, detail, null, null);

    /// <summary>body 不是 null、UTF-8 ≤<see cref="MaxBodyBytes"/>、沒有不成對的代理字元時給出要簽（驗）的位元組</summary>
    private static bool TrySizedData(string? body, out byte[] data)
    {
        data = [];
        // 每個 UTF-16 字元至少一個 UTF-8 位元組：字元數超過上限就不必編碼
        if (body is null || body.Length > MaxBodyBytes || !TryEncode(body, out var encoded) || encoded.Length - PrefixBytes > MaxBodyBytes)
            return false;
        data = encoded;
        return true;
    }

    private static bool TryEncode(string body, out byte[] data)
    {
        try
        {
            data = StrictUtf8.GetBytes(Version + "\n" + body);
            return true;
        }
        catch (EncoderFallbackException)
        {
            data = [];
            return false;
        }
    }

    /// <summary>
    /// 第 4 步：只收一種解讀（重複的鍵含巢狀、註解、結尾多餘的內容、深度超過 32 一律拒絕，<see cref="ConversationEventRules.TryParseStrict"/>）；
    /// 鍵只收 SP 14.4 列的（伺服器多送一個就不合格：信封要改格式就換版本字串）；型別照列：字串、整數（long，不收小數與超出 long 的）、布林，
    /// <c>conv</c>、<c>epoch</c>、<c>auth.uvAt</c> 可以省略或是 null，<c>cmd</c> 是物件而且 <c>cmd.cmd</c> 是字串，
    /// <c>issuer</c> 恰好是 {kind, label}、<c>auth</c> 只有 {uvAt?, defaultPassword}。值合不合格在之後各步判斷
    /// </summary>
    private static bool TryParseBody(string json, out CommandEnvelopeBody body, out string problem)
    {
        body = null!;
        problem = string.Empty;
        if (!ConversationEventRules.TryParseStrict(json, out var token) || token is not JObject root)
            return Bad("不是只有一種解讀的 JSON 物件（重複的鍵、註解、結尾多餘的內容或深度超過 32）", out problem);
        foreach (var property in root.Properties())
        {
            if (!BodyKeys.Contains(property.Name))
                return Bad("不認得的鍵", out problem);
        }

        if (!TryString(root, "aud", out var aud) || !TryString(root, "cid", out var cid) || !TryString(root, "dev", out var dev) || !TryString(root, "conn", out var conn))
            return Bad("aud／cid／dev／conn 不是字串", out problem);
        if (!TryOptionalString(root, "conv", out var conv) || !TryOptionalLong(root, "epoch", out var epoch))
            return Bad("conv／epoch 的型別不對", out problem);
        if (root["cmd"] is not JObject cmd || !RemoteCommandShapeRules.IsString(cmd["cmd"], out _))
            return Bad("cmd 不是物件或 cmd.cmd 不是字串", out problem);
        if (!TryLong(root, "tier", out var tier) || tier is < int.MinValue or > int.MaxValue)
            return Bad("tier 不是整數", out problem);
        if (!TryBool(root, "fullAccess", out var fullAccess) || !TryBool(root, "durable", out var durable))
            return Bad("fullAccess／durable 不是布林", out problem);
        if (!TryLong(root, "created", out var created) || !TryLong(root, "iat", out var iat) || !TryLong(root, "exp", out var exp))
            return Bad("created／iat／exp 不是整數", out problem);
        if (root["issuer"] is not JObject issuer || !HasOnly(issuer, "kind", "label") || !TryString(issuer, "kind", out var kind) || !TryString(issuer, "label", out var label))
            return Bad("issuer 不是 {kind, label}", out problem);
        if (root["auth"] is not JObject auth || !HasOnly(auth, "uvAt", "defaultPassword") || !TryOptionalLong(auth, "uvAt", out var uvAt)
            || !TryBool(auth, "defaultPassword", out var defaultPassword))
            return Bad("auth 不是 {uvAt?, defaultPassword}", out problem);

        body = new CommandEnvelopeBody(aud, cid, dev, conn, conv, epoch, cmd, (int)tier, fullAccess, durable, created, iat, exp,
            new CommandIssuer(kind, label), new CommandAuth(uvAt, defaultPassword));
        return true;
    }

    /// <summary>
    /// 第 8 步（全部用伺服器時間，D4）：先看信封自己的時間（不合理是 invalid），再看收到時的伺服器時間界線（過期、還沒生效、推算不可用是 expired）
    /// </summary>
    private static (string Code, string Detail)? CheckTime(CommandEnvelopeBody body, ServerTimeBounds? bounds)
    {
        var lifetime = (Int128)body.Exp - body.Iat;
        if (lifetime < 0 || lifetime > MaxLifetimeMs)
            return (RemoteResultCodes.Invalid, "exp − iat 不在 0～60 秒");
        if (body.Iat < body.Created)
            return (RemoteResultCodes.Invalid, "iat 早於 created");
        var name = CommandName(body);
        if (body.Durable)
        {
            if (RemoteCommandCatalog.Find(name) is not { DurableAllowed: true })
                return (RemoteResultCodes.Invalid, "這個指令不能 durable");
        }
        else if ((Int128)body.Exp - body.Created > MaxNonDurableAgeMs)
        {
            return (RemoteResultCodes.Invalid, "非 durable 的 exp 晚於 created＋60 秒");
        }

        if (bounds is not { } now)
            return name == RemoteCommandCatalog.Stop ? null : (RemoteResultCodes.Expired, "伺服器時間推算不可用（還沒有 Hello，或 Hello 的往返超過 30 秒）");
        if (body.Durable && (Int128)now.Upper - body.Created > MaxDurableAgeMs)
            return (RemoteResultCodes.Expired, "durable 的指令建立超過 7 天");
        if (now.Upper > body.Exp)
            return (RemoteResultCodes.Expired, "已過期（伺服器時間的上界晚於 exp）");
        if ((Int128)body.Iat > (Int128)now.Lower + NotBeforeLeewayMs)
            return (RemoteResultCodes.Expired, "還沒生效（iat 晚於伺服器時間的下界＋15 秒）");
        return null;
    }

    /// <summary>第 10 步：conv 與 epoch 一起帶、格式合格；路由 Owner 必帶 conv、Device 不帶；<c>cmd.conversationId</c> 等於 conv；<c>cmd.deviceId</c> 有帶時等於 dev</summary>
    private static string? CheckTarget(CommandEnvelopeBody body)
    {
        if ((body.Conv is null) != (body.Epoch is null))
            return "conv 與 epoch 要一起帶或一起省略";
        if (body.Conv is { } wire && (!ConversationIds.IsWireId(wire) || !ConversationIds.IsEpoch(body.Epoch)))
            return "conv 不是線上的對話 id 或 epoch 不是正數";

        var route = RemoteCommandCatalog.Find(CommandName(body))?.Route;
        if (route == RemoteRoute.Owner && body.Conv is null)
            return "對話指令要帶 conv";
        if (route == RemoteRoute.Device && body.Conv is not null)
            return "路由 Device 的指令不帶 conv";

        var conversationId = body.Cmd.Property("conversationId", StringComparison.Ordinal);
        if (body.Conv is null
                ? conversationId != null
                : conversationId == null || !RemoteCommandShapeRules.IsString(conversationId.Value, out var id) || !string.Equals(id, body.Conv, StringComparison.Ordinal))
            return "cmd.conversationId 與 conv 不一致";
        if (body.Cmd.Property("deviceId", StringComparison.Ordinal) is { } deviceId
            && (!RemoteCommandShapeRules.IsString(deviceId.Value, out var device) || !string.Equals(device, body.Dev, StringComparison.Ordinal)))
            return "cmd.deviceId 與 dev 不一致";
        return null;
    }

    private static string CommandName(CommandEnvelopeBody body) => RemoteCommandShapeRules.IsString(body.Cmd["cmd"], out var name) ? name : string.Empty;

    private static void WriteIssuer(JsonWriter w, CommandIssuer issuer)
    {
        w.WriteStartObject();
        w.WritePropertyName("kind");
        w.WriteValue(issuer.Kind);
        w.WritePropertyName("label");
        w.WriteValue(issuer.Label);
        w.WriteEndObject();
    }

    private static string Write(Action<JsonTextWriter> write)
    {
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        using (var writer = new JsonTextWriter(text) { Formatting = Formatting.None })
            write(writer);
        return text.ToString();
    }

    private static bool Bad(string why, out string problem)
    {
        problem = why;
        return false;
    }

    private static bool HasOnly(JObject o, params string[] keys) => o.Properties().All(p => keys.Contains(p.Name, StringComparer.Ordinal));

    private static bool TryString(JObject o, string name, out string value) => RemoteCommandShapeRules.IsString(o[name], out value);

    private static bool TryOptionalString(JObject o, string name, out string? value)
    {
        value = null;
        if (o[name] is null or { Type: JTokenType.Null })
            return true;
        if (!RemoteCommandShapeRules.IsString(o[name], out var text))
            return false;
        value = text;
        return true;
    }

    private static bool TryLong(JObject o, string name, out long value)
    {
        if (o[name] is JValue { Type: JTokenType.Integer, Value: long number })
        {
            value = number;
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryOptionalLong(JObject o, string name, out long? value)
    {
        value = null;
        if (o[name] is null or { Type: JTokenType.Null })
            return true;
        if (!TryLong(o, name, out var number))
            return false;
        value = number;
        return true;
    }

    private static bool TryBool(JObject o, string name, out bool value)
    {
        if (o[name] is JValue { Type: JTokenType.Boolean, Value: bool flag })
        {
            value = flag;
            return true;
        }

        value = false;
        return false;
    }
}
