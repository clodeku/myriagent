using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Myriagent.Remote;
using Myriagent.RemoteDesk;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.Hub;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.RemoteDesk;

/// <summary>
/// 遠端桌面輸入的端對端加密（<see cref="DeskSealRules"/>、被看的一方的 <see cref="DeskInputCipher"/>、接上 <see cref="DeskSession"/>；
/// docs/sync-protocol.md 14.16）。固定的向量和網頁的 <c>tools/ui-probe/deskseal.test.mjs</c> 是同一組：兩邊的演算法要算出一樣的金鑰與密文
/// </summary>
public sealed class DeskSealRulesTests
{
    // ---- 固定的向量（看的一方的私鑰是 1～32、被看的一方的是 33～64；網頁的測試用同一組） ----
    internal const string Session = "01JDESKSEALVECTOR00000000A";
    internal const string ViewerPublic = "BFFcPW6545a5BNP-yn9U_c0MwemXvzddylFa0KbDtANfRTa-OlDzGPv5pUdZAqIhUCvvDVfgjFOyzApW8X2fk1Q";
    internal const string TargetPublic = "BB8UAUa_sbJR-E9N2-DUzc_Xev2YSpUg41eUAh-DErue7JlaCLH6dwTfPcwLUKlmUmP7dxH5X5-KRJxQluR8iSs";
    internal const string KeyHex = "978352a7eba74e2e67f55b725c4e192fe2442ef0befbfd97a3ed20ffcff80cd3"; // gitleaks:allow（測試用的固定向量：兩把寫死的測試私鑰算出來的，不是秘密）
    internal const string Plain = "{\"events\":[{\"t\":\"kd\",\"k\":65},{\"t\":\"tx\",\"s\":\"密碼 P@ss\"}]}";
    internal const string Sealed1 = "XjA1Tyyr5zCqEcCdBnSxWbNOWUNLQ3ZYAsmdOnH2U1tFVN0QEd9HBM6fVFyUoyEVPSsnKKMC5gv9Z2fliJfhVKcunI6Bk1KUYrat";
    internal const string Sealed7 = "0X9krb4Yq4F95cL_wDzsLutZjBm6s2lqVoaitRFAJ8dOspHDNqFy-w43GDaf1LYPtcIOLJk_FCnV2kZCLUhIHEB2-6FuhSs9ZbH2";

    private static ECDiffieHellman Fixed(int first)
        => ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = Enumerable.Range(first, 32).Select(i => (byte)i).ToArray() });

    private static byte[] Key => Convert.FromHexString(KeyHex);

    [Fact]
    public void 固定的向量_公鑰_兩邊算出同一把金鑰_密文()
    {
        using var viewer = Fixed(1);
        using var target = Fixed(33);
        DeskSealRules.ExportPublicKey(viewer).Should().Be(ViewerPublic);
        DeskSealRules.ExportPublicKey(target).Should().Be(TargetPublic);

        var atTarget = DeskSealRules.DeriveKey(target, ViewerPublic, Session, ViewerPublic, TargetPublic)!;
        var atViewer = DeskSealRules.DeriveKey(viewer, TargetPublic, Session, ViewerPublic, TargetPublic)!;
        Convert.ToHexString(atTarget).ToLowerInvariant().Should().Be(KeyHex);
        atViewer.Should().Equal(atTarget);

        DeskSealRules.Seal(Key, Session, 1, Encoding.UTF8.GetBytes(Plain)).Should().Be(Sealed1);
        DeskSealRules.Seal(Key, Session, 7, Encoding.UTF8.GetBytes(Plain)).Should().Be(Sealed7);
        Encoding.UTF8.GetString(DeskSealRules.Open(Key, Session, 1, Sealed1)!).Should().Be(Plain);
        Encoding.UTF8.GetString(DeskSealRules.Open(Key, Session, 7, Sealed7)!).Should().Be(Plain);
    }

    [Fact]
    public void 解開_金鑰_工作階段_編號_內容任何一個不對都解不開()
    {
        DeskSealRules.Open(Key, Session, 2, Sealed1).Should().BeNull("編號是驗證的一部分：第 1 包不能當第 2 包");
        DeskSealRules.Open(Key, "01JDESKSEALVECTOR00000000B", 1, Sealed1).Should().BeNull("別的工作階段");
        var otherKey = Key;
        otherKey[0] ^= 1;
        DeskSealRules.Open(otherKey, Session, 1, Sealed1).Should().BeNull();

        var bytes = Convert.FromBase64String(Sealed1.Replace('-', '+').Replace('_', '/'));
        bytes[3] ^= 0x40;
        DeskSealRules.Open(Key, Session, 1, Base64UrlRules.Encode(bytes)).Should().BeNull("內容被動過");
        DeskSealRules.Open(Key, Session, 1, "not base64url!").Should().BeNull();
        DeskSealRules.Open(Key, Session, 1, "AAAA").Should().BeNull("比驗證碼還短");
        DeskSealRules.Open(Key, Session, 0, Sealed1).Should().BeNull();
        DeskSealRules.Open(Key, Session, 1, null).Should().BeNull();
    }

    [Fact]
    public void 公鑰_只收65位元組未壓縮的點_不在曲線上的算不出金鑰()
    {
        DeskSealRules.IsPublicKey(ViewerPublic).Should().BeTrue();
        DeskSealRules.IsPublicKey(null).Should().BeFalse();
        DeskSealRules.IsPublicKey("").Should().BeFalse();
        DeskSealRules.IsPublicKey(ViewerPublic[..^2]).Should().BeFalse();
        DeskSealRules.IsPublicKey("A" + ViewerPublic[1..]).Should().BeFalse("開頭不是 0x04");

        // 65 位元組、開頭 0x04，但不是曲線上的點
        var bogus = new byte[65];
        bogus[0] = 4;
        bogus[1] = 1;
        bogus[64] = 2;
        var offCurve = Base64UrlRules.Encode(bogus);
        DeskSealRules.IsPublicKey(offCurve).Should().BeTrue("形狀對");
        using var own = DeskSealRules.CreateKey();
        var mine = DeskSealRules.ExportPublicKey(own);
        DeskSealRules.DeriveKey(own, offCurve, Session, offCurve, mine).Should().BeNull();
        DeskInputCipher.TryCreate(Session, offCurve).Should().BeNull();
        DeskInputCipher.TryCreate(Session, null).Should().BeNull();
        DeskInputCipher.TryCreate(Session, "garbage").Should().BeNull();
    }

    [Fact]
    public void 每次產生的金鑰都不同_公鑰的寫法固定87個字()
    {
        using var a = DeskSealRules.CreateKey();
        using var b = DeskSealRules.CreateKey();
        var pa = DeskSealRules.ExportPublicKey(a);
        pa.Should().HaveLength(87).And.NotBe(DeskSealRules.ExportPublicKey(b));
        DeskSealRules.IsPublicKey(pa).Should().BeTrue();
    }

    [Fact]
    public void 線上的形狀_kx與加密的那一包()
    {
        var body = DeskRules.KeyExchangeBody(TargetPublic);
        body.Value<string>("text").Should().BeNull();
        DeskSealRules.TargetPublic(body).Should().Be(TargetPublic);
        DeskSealRules.TargetPublic(DeskRules.StatusBody("鎖定中", null)).Should().BeNull();
        DeskSealRules.TargetPublic(new JObject { ["kx"] = new JObject { ["v"] = 2, ["pub"] = TargetPublic } }).Should().BeNull("不認得的版本");
        DeskSealRules.TargetPublic(new JObject { ["kx"] = new JObject { ["v"] = 1, ["pub"] = "x" } }).Should().BeNull();

        DeskSealRules.SealedOf(new JObject { ["e2e"] = new JObject { ["n"] = 3, ["ct"] = Sealed1 } }, out var bad).Should().Be((3L, Sealed1));
        bad.Should().BeFalse();
        DeskSealRules.SealedOf(new JObject { ["events"] = new JArray() }, out bad).Should().BeNull();
        bad.Should().BeFalse("沒有 e2e 不算格式錯");
        foreach (var e2e in new JToken[]
                 {
                     new JObject { ["n"] = 0, ["ct"] = Sealed1 }, new JObject { ["n"] = "1", ["ct"] = Sealed1 }, new JObject { ["n"] = 1 },
                     new JObject { ["n"] = 1, ["ct"] = "短" }, new JObject { ["n"] = 1.5, ["ct"] = Sealed1 }, new JValue("x"), new JArray(),
                 })
        {
            DeskSealRules.SealedOf(new JObject { ["e2e"] = e2e }, out bad).Should().BeNull(e2e.ToString());
            bad.Should().BeTrue(e2e.ToString());
        }
    }

    [Fact]
    public void 封包檢查_加密的輸入只看形狀_不能和明文的事件放同一包()
    {
        JObject Sealed(long n = 1) => new() { ["e2e"] = new JObject { ["n"] = n, ["ct"] = Sealed1 } };
        DeskRules.ParseInput(Sealed()).Should().BeEmpty("內容只有被看的那台解得開");
        var withAck = Sealed();
        withAck["ack"] = 5;
        DeskRules.ParseInput(withAck).Should().BeEmpty();
        DeskRules.AckOf(withAck).Should().Be(5);

        var mixed = Sealed();
        mixed["events"] = new JArray(new JObject { ["t"] = "kd", ["k"] = 65 });
        DeskRules.ParseInput(mixed).Should().BeNull();
        DeskRules.ParseInput(new JObject { ["e2e"] = new JObject { ["n"] = 0, ["ct"] = Sealed1 } }).Should().BeNull();

        const string id = "01JDESKSEALVECTOR00000000A";
        DeskRules.Check(new DeskPacket(id, DeskRules.Input, 1, Sealed()), fromTarget: false).Should().BeNull("伺服器照樣轉送");
        DeskRules.Check(new DeskPacket(id, DeskRules.Input, 1, mixed), fromTarget: false).Should().NotBeNull();
        DeskRules.Check(new DeskPacket(id, DeskRules.Status, 0, DeskRules.KeyExchangeBody(TargetPublic)), fromTarget: true).Should().BeNull();
    }

    // ---- 被看的一方的解密 ----

    /// <summary>以看的一方的固定私鑰和 <paramref name="cipher"/> 的公鑰算出金鑰（測試扮演看的一方）</summary>
    private static byte[] ViewerKeyFor(DeskInputCipher cipher)
    {
        using var viewer = Fixed(1);
        return DeskSealRules.DeriveKey(viewer, cipher.PublicKey, Session, ViewerPublic, cipher.PublicKey)!;
    }

    private static string SealEvents(byte[] key, long n, string json = Plain) => DeskSealRules.Seal(key, Session, n, Encoding.UTF8.GetBytes(json));

    [Fact]
    public void 解密器_解得開看的一方加密的事件_編號只收比上一包大的()
    {
        using var cipher = DeskInputCipher.TryCreate(Session, ViewerPublic)!;
        cipher.Established.Should().BeFalse();
        var key = ViewerKeyFor(cipher);

        var events = cipher.Open(1, SealEvents(key, 1))!;
        events.Should().HaveCount(2);
        events[0].Should().Be(new DeskInputEvent("kd", Key: 65));
        events[1].Text.Should().Be("密碼 P@ss");
        cipher.Established.Should().BeTrue();

        cipher.Open(1, SealEvents(key, 1)).Should().BeNull("同一包重送");
        cipher.Open(5, SealEvents(key, 5)).Should().NotBeNull("跳號可以（中間的掉了）");
        cipher.Open(3, SealEvents(key, 3)).Should().BeNull("比上一包小：亂序或重放");
        cipher.Open(6, SealEvents(key, 7)).Should().BeNull("編號和加密時的不一樣");
        cipher.Open(8, SealEvents(key, 8)).Should().NotBeNull("解不開的那一包不佔編號");
    }

    [Fact]
    public void 解密器_別人的金鑰_不是事件的內容_釋放之後都解不開()
    {
        using var cipher = DeskInputCipher.TryCreate(Session, ViewerPublic)!;
        var key = ViewerKeyFor(cipher);
        cipher.Open(1, SealEvents(Key, 1)).Should().BeNull("不是這個工作階段的金鑰");
        cipher.Established.Should().BeFalse();
        cipher.Open(1, SealEvents(key, 1, "not json")).Should().BeNull();
        cipher.Open(2, SealEvents(key, 2, "{\"events\":[{\"t\":\"zz\"}]}")).Should().BeNull("不認得的事件");
        cipher.Open(3, SealEvents(key, 3, "{\"e2e\":{\"n\":1,\"ct\":\"" + Sealed1 + "\"}}")).Should().BeNull("不收又包一層的");
        cipher.Open(4, SealEvents(key, 4)).Should().NotBeNull();

        cipher.Dispose();
        cipher.Open(9, SealEvents(key, 9)).Should().BeNull("工作階段結束、金鑰清掉了");
    }

    // ---- 接上工作階段 ----

    private const string SessionId = Session;

    private static async Task Until(Func<bool> condition, string what, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > ms)
                throw new TimeoutException("等不到：" + what);
            await Task.Delay(20);
        }
    }

    private static DeskPacket SealedInput(byte[] key, long n, string json = "{\"events\":[{\"t\":\"md\",\"x\":0.5,\"y\":0.5,\"b\":0}]}")
        => new(SessionId, DeskRules.Input, 0, new JObject { ["e2e"] = new JObject { ["n"] = n, ["ct"] = SealEvents(key, n, json) } });

    private static DeskPacket PlainInput()
        => new(SessionId, DeskRules.Input, 0, new JObject { ["events"] = new JArray(new JObject { ["t"] = "md", ["x"] = 0.5, ["y"] = 0.5, ["b"] = 0 }) });

    [Fact]
    public async Task 工作階段_第一個封包是自己的公鑰_加密的輸入解開後注入_之後明文的不收()
    {
        var screen = new FakeDeskScreen();
        var sent = new Recorder<DeskPacket>();
        using var cipher = DeskInputCipher.TryCreate(SessionId, ViewerPublic)!;
        var session = new DeskSession(SessionId, "擁有者的瀏覽器", screen, sent.Add, (_, _) => { }, cipher: cipher);
        try
        {
            session.InputSealed.Should().BeTrue();
            session.Start();
            await Until(() => sent.All.Any(p => p.Kind == DeskRules.Frame), "第一個影格");
            var first = sent.All[0];
            first.Kind.Should().Be(DeskRules.Status, "公鑰一定在第一個封包：看的一方看第一個封包決定要不要加密");
            DeskSealRules.TargetPublic(first.Body).Should().Be(cipher.PublicKey);
            var key = ViewerKeyFor(cipher);

            // 還沒收過加密的：明文的照收（看的一方可能是不會加密的舊網頁——但那種不會給公鑰；這裡是保險）
            session.OnPacket(PlainInput());
            await Until(() => screen.Injected.All.Count == 1, "明文的輸入");

            session.OnPacket(SealedInput(key, 1));
            await Until(() => screen.Injected.All.Count == 2, "加密的輸入");
            screen.Injected.All[1].Events.Single().Should().Be(new DeskInputEvent("md", 0.5, 0.5, Button: 0));

            // 看的一方已經在加密了：之後明文的一律不收
            session.OnPacket(PlainInput());
            session.OnPacket(SealedInput(key, 2));
            await Until(() => screen.Injected.All.Count == 3, "第二包加密的輸入");
            screen.Injected.All.Should().HaveCount(3, "中間那包明文的沒有注入");

            // 解不開的（重送、被動過）丟掉，確認照樣算
            session.OnPacket(SealedInput(key, 2));
            var tampered = SealedInput(key, 3);
            ((JObject)tampered.Body!["e2e"]!)["ct"] = Sealed1;
            tampered.Body["ack"] = sent.All.Last(p => p.Kind == DeskRules.Frame).Seq;
            session.OnPacket(tampered);
            session.OnPacket(SealedInput(key, 4));
            await Until(() => screen.Injected.All.Count == 4, "之後合格的照收");
        }
        finally
        {
            session.Stop("test", "測試結束", notifyViewer: false);
        }
    }

    [Fact]
    public async Task 工作階段_看的一方沒給公鑰_不送kx_加密的輸入解不開就丟掉()
    {
        var screen = new FakeDeskScreen();
        var sent = new Recorder<DeskPacket>();
        var session = new DeskSession(SessionId, "舊的網頁", screen, sent.Add, (_, _) => { });
        try
        {
            session.InputSealed.Should().BeFalse();
            session.Start();
            await Until(() => sent.All.Any(p => p.Kind == DeskRules.Frame), "第一個影格");
            sent.All.Should().NotContain(p => p.Kind == DeskRules.Status && p.Body!["kx"] != null);

            session.OnPacket(SealedInput(Key, 1));
            session.OnPacket(PlainInput());
            await Until(() => screen.Injected.All.Count == 1, "明文的輸入");
            screen.Injected.All.Should().ContainSingle("沒有金鑰：加密的那包丟掉");
        }
        finally
        {
            session.Stop("test", "測試結束", notifyViewer: false);
        }
    }
}
