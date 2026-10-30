using System.Security.Cryptography;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.Protocol.Remote;
using Myriagent.Tests.Sync.Engine;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Fakes;

/// <summary>
/// 遠端指令的信封（WP4.4 計畫 D35）：依假伺服器目前這條連線（<c>HelloState.NonceS</c> 是 conn）與伺服器時間（測試時鐘加 <c>ClockOffsetMs</c>）
/// 組出合格的 body，以假伺服器的身分金鑰簽（<see cref="FakeSyncServer.TrySignEnvelope"/>）；可以改任一欄（record 的 with）、換別把金鑰、竄改 body
/// </summary>
internal sealed class CommandEnvelopeFactory(EngineHarness h) : IDisposable
{
    public const string ConversationId = RemoteSamples.ConversationId;

    private readonly ECDsa otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>這台配對網址的 aud</summary>
    public string Aud => SyncCommandRules.Audience(h.Device.ServerUrl)!;

    /// <summary>伺服器現在的時間（Unix 毫秒）</summary>
    public long ServerNow => h.Time.NowMs + h.Server.ClockOffsetMs;

    /// <summary>目前這條連線的 conn（Hello 回應的 nonceS）</summary>
    public string Conn => h.Server.Current!.HelloState!.Value.NonceS;

    public static JObject Rename(string title = "新標題") => new() { ["cmd"] = "rename", ["conversationId"] = ConversationId, ["title"] = title };

    public static JObject Stop() => new() { ["cmd"] = "stop", ["conversationId"] = ConversationId };

    /// <summary>合格的 body：預設是對 <see cref="ConversationId"/> 的 rename、層級 1、非 durable、iat＝created＝伺服器現在、30 秒後到期</summary>
    public CommandEnvelopeBody Body(JObject? cmd = null, string? cid = null)
    {
        cmd ??= Rename();
        var now = ServerNow;
        var conv = cmd["conversationId"]?.Value<string>();
        return new CommandEnvelopeBody(Aud, cid ?? SyncIds.NewUlid(now), h.DeviceId, Conn, conv, conv is null ? null : 1, cmd, 1, false, false, now, now, now + 30_000,
            new CommandIssuer(CommandIssuerKinds.Browser, "Chrome・Windows"), new CommandAuth(null, false));
    }

    /// <summary>以伺服器目前的身分金鑰簽；外層 commandId 預設是 cid</summary>
    public CommandDelivery Sign(CommandEnvelopeBody body, string? outerCommandId = null)
    {
        h.Server.TrySignEnvelope(body, out var envelope).Should().BeTrue();
        return new CommandDelivery(outerCommandId ?? body.Cid, envelope!);
    }

    /// <summary>以別把金鑰簽（冒牌的伺服器）</summary>
    public CommandDelivery SignWithOtherKey(CommandEnvelopeBody body)
    {
        CommandEnvelope.TrySign(body, data => P256KeyRules.Sign(otherKey, data), out var envelope).Should().BeTrue();
        return new CommandDelivery(body.Cid, envelope!);
    }

    /// <summary>簽好之後把 body 字串裡的 <paramref name="from"/> 換成 <paramref name="to"/>（簽章不再相符）</summary>
    public CommandDelivery Tampered(CommandEnvelopeBody body, string from, string to)
    {
        var delivery = Sign(body);
        delivery.Envelope.Body.Should().Contain(from);
        return delivery with { Envelope = delivery.Envelope with { Body = delivery.Envelope.Body.Replace(from, to, StringComparison.Ordinal) } };
    }

    /// <summary>在目前這條連線送出（假伺服器不看協商）</summary>
    public void Send(CommandDelivery delivery) => h.Server.Current!.Command(delivery);

    public void Dispose() => otherKey.Dispose();
}
