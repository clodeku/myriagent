using System.Globalization;
using Myriagent.Remote;
using Myriagent.Security;
using Myriagent.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Sync;

/// <summary>
/// durable 指令的未簽 body（sync.db <c>commands.unsigned_body</c>；WP4.5 計畫 D15、D27）：信封 body 去掉每次簽發才定的 <c>conn</c>、<c>iat</c>、<c>exp</c>，
/// 其他欄位（就是 <see cref="CommandEnvelope.ContentDigest"/> 涵蓋的那些）原樣保存，電腦上線後以新的連線與時間重簽：<c>created</c> 與內容摘要不變。
/// 鍵依 <c>aud, cid, dev, conv, epoch, cmd, tier, fullAccess, durable, created, issuer {kind, label}, auth {uvAt, defaultPassword}</c> 的順序
/// （<c>conv</c>、<c>epoch</c>、<c>auth.uvAt</c> 是 null 時省略；<c>cmd</c> 照物件原本的鍵順序）。純函式
/// </summary>
internal static class DurableBodyRules
{
    private static readonly HashSet<string> Keys = ["aud", "cid", "dev", "conv", "epoch", "cmd", "tier", "fullAccess", "durable", "created", "issuer", "auth"];

    /// <summary>不縮排的 JSON（不含 <c>conn</c>、<c>iat</c>、<c>exp</c>）</summary>
    public static string Serialize(CommandEnvelopeBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        using (var w = new JsonTextWriter(text) { Formatting = Formatting.None })
        {
            w.WriteStartObject();
            w.WritePropertyName("aud");
            w.WriteValue(body.Aud);
            w.WritePropertyName("cid");
            w.WriteValue(body.Cid);
            w.WritePropertyName("dev");
            w.WriteValue(body.Dev);
            if (body.Conv is not null)
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
            w.WritePropertyName("issuer");
            w.WriteStartObject();
            w.WritePropertyName("kind");
            w.WriteValue(body.Issuer.Kind);
            w.WritePropertyName("label");
            w.WriteValue(body.Issuer.Label);
            w.WriteEndObject();
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
        }
        return text.ToString();
    }

    /// <summary>
    /// 還原成要重簽的 body（<paramref name="conn"/>、<paramref name="iat"/>、<paramref name="exp"/> 是這次簽發的）。只收 <see cref="Serialize"/> 寫出的形狀：
    /// 只有一種解讀、鍵與型別照上面的清單（多一個、少一個必要的、型別不對都不收）
    /// </summary>
    /// <exception cref="FormatException">格式不對（列被改壞了：呼叫端記錯誤、不送）</exception>
    public static CommandEnvelopeBody Deserialize(string json, string conn, long iat, long exp)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(conn);
        if (!ConversationEventRules.TryParseStrict(json, out var token) || token is not JObject root)
            throw new FormatException("unsigned_body 不是只有一種解讀的 JSON 物件");
        if (root.Properties().Any(p => !Keys.Contains(p.Name)))
            throw new FormatException("unsigned_body 有不認得的鍵");
        if (root["cmd"] is not JObject cmd || !RemoteCommandShapeRules.IsString(cmd["cmd"], out _))
            throw new FormatException("unsigned_body 的 cmd 不是物件");
        if (root["issuer"] is not JObject issuer || issuer.Properties().Any(p => p.Name is not ("kind" or "label")))
            throw new FormatException("unsigned_body 的 issuer 不是 {kind, label}");
        if (root["auth"] is not JObject auth || auth.Properties().Any(p => p.Name is not ("uvAt" or "defaultPassword")))
            throw new FormatException("unsigned_body 的 auth 不是 {uvAt?, defaultPassword}");
        var tier = Long(root, "tier");
        if (tier is < int.MinValue or > int.MaxValue)
            throw new FormatException("unsigned_body 的 tier 不是整數");
        return new CommandEnvelopeBody(
            String(root, "aud"), String(root, "cid"), String(root, "dev"), conn, OptionalString(root, "conv"), OptionalLong(root, "epoch"),
            (JObject)cmd.DeepClone(), (int)tier, Bool(root, "fullAccess"), Bool(root, "durable"), Long(root, "created"), iat, exp,
            new CommandIssuer(String(issuer, "kind"), String(issuer, "label")), new CommandAuth(OptionalLong(auth, "uvAt"), Bool(auth, "defaultPassword")));
    }

    private static string String(JObject o, string name) =>
        RemoteCommandShapeRules.IsString(o[name], out var value) ? value : throw new FormatException($"unsigned_body 的 {name} 不是字串");

    private static string? OptionalString(JObject o, string name) =>
        o[name] is null or { Type: JTokenType.Null } ? null : String(o, name);

    private static long Long(JObject o, string name) =>
        o[name] is JValue { Type: JTokenType.Integer, Value: long value } ? value : throw new FormatException($"unsigned_body 的 {name} 不是整數");

    private static long? OptionalLong(JObject o, string name) =>
        o[name] is null or { Type: JTokenType.Null } ? null : Long(o, name);

    private static bool Bool(JObject o, string name) =>
        o[name] is JValue { Type: JTokenType.Boolean, Value: bool value } ? value : throw new FormatException($"unsigned_body 的 {name} 不是布林");
}
