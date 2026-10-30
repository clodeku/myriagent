using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Pairing;
using Myriagent.Tests.TestSupport;
using FakeDevice;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.FakeDevice;

/// <summary>
/// 正式站守門（WP4.9 計畫 D18、步 8）：tools/hub-e2e/test/guard-cases.json 是和 Node 的 guard-rules.mjs 共用的測試表；
/// 每一列寫著只有哪一條規則擋得住它，拿掉任一條規則對應的列會紅
/// </summary>
public sealed class LocalHubTargetRulesTests
{
    private static readonly Lazy<JObject> Cases = new(() =>
        JObject.Parse(File.ReadAllText(Path.Combine(MyriagentHubExe.RepoRoot(), "tools", "hub-e2e", "test", "guard-cases.json"))));

    [Fact]
    public void 網址_共用測試表()
    {
        var rows = (JArray)Cases.Value["allowedServerUrl"]!;
        rows.Should().HaveCountGreaterThanOrEqualTo(15);
        foreach (var row in rows.Cast<JObject>())
        {
            var url = row.Value<string>("url");
            LocalHubTargetRules.IsAllowedServerUrl(url).Should().Be(row.Value<bool>("allowed"), $"{url}（{row.Value<string>("why")}）");
        }
    }

    [Fact]
    public void 網址_拒絕時有原因_null也拒絕()
    {
        LocalHubTargetRules.CheckUrl(null).Should().NotBeNull();
        LocalHubTargetRules.CheckUrl("https://hub.example.com").Should().Contain("example.com");
        LocalHubTargetRules.CheckUrl("http://127.0.0.1:1").Should().Contain("https");
        LocalHubTargetRules.CheckUrl("https://127.0.0.1:1").Should().BeNull();
    }

    [Fact]
    public void 連上之後_共用測試表()
    {
        var rows = (JArray)Cases.Value["productionTarget"]!;
        rows.Select(r => r.Value<bool>("production")).Distinct().Should().HaveCount(2, "正例與反例都要有");
        foreach (var row in rows.Cast<JObject>())
        {
            var facts = LocalHubTargetRules.FactsFromJson(Json(row["readyz"]), Json(row["tls"]));
            LocalHubTargetRules.IsProductionTarget(facts).Should().Be(row.Value<bool>("production"), row.Value<string>("why"));
            (LocalHubTargetRules.CheckTarget(facts) is null).Should().Be(!row.Value<bool>("production"), row.Value<string>("why"));
        }
    }

    [Fact]
    public void 連上之後_由連線的憑證取名稱與簽發者()
    {
        const string readyz = """{"environment":"console","tls":{"name":"localhost","subject":"CN=localhost","issuer":"CN=localhost"}}""";
        using var local = SelfSigned("CN=localhost", "localhost");
        LocalHubTargetRules.IsProductionTarget(LocalHubTargetRules.FactsFromCertificate(readyz, local)).Should().BeFalse();

        // ssh 轉送：/readyz 是本機看得到的，連線的憑證是正式站的（主體不是、SAN 才是）
        using var forwarded = SelfSigned("CN=HUB-PC", "hub.example.com");
        LocalHubTargetRules.DnsNames(forwarded).Should().Contain("hub.example.com");
        LocalHubTargetRules.IsProductionTarget(LocalHubTargetRules.FactsFromCertificate(readyz, forwarded)).Should().BeTrue();

        LocalHubTargetRules.IsProductionTarget(LocalHubTargetRules.FactsFromCertificate("""{"environment":"service"}""", local)).Should().BeTrue();
        LocalHubTargetRules.FactsFromCertificate("不是 JSON", null).Environment.Should().BeNull("讀不懂的 /readyz 沒有 environment（呼叫端當成不確定而拒絕）");
    }

    // ── 已配對的資料根（合併前審查 F4）：啟動執行環境就照 device.json 連線，所以每個指令啟動之前都先看它 ──

    [Fact]
    public async Task 已配對的資料根_device_json指到正式站_連線之前就拒絕()
    {
        using var dir = new TempDirectory("fd-guard");
        var paths = new SyncPaths(Path.Combine(dir.Root, "sync"));
        DeviceFile.Save(paths.DeviceFile, PairingTestData.Device() with { ServerUrl = "https://hub.example.com" });

        foreach (var connect in new[] { true, false })
        {
            // 真實資料根的複本：status、run、backlog（connect）與 unpair（只看網址）都在啟動之前以 DeviceRuntimeException（結束碼 1）結束
            var act = () => PairedServerGuard.VerifyAsync(paths, connect, TimeSpan.FromSeconds(5));
            (await act.Should().ThrowAsync<DeviceRuntimeException>()).Which.Message.Should().Contain("device.json").And.Contain("example.com");
        }
    }

    [Fact]
    public async Task 已配對的資料根_本機網址_unpair只看網址_要連線的指令讀不到readyz就拒絕()
    {
        using var dir = new TempDirectory("fd-guard");
        var paths = new SyncPaths(Path.Combine(dir.Root, "sync"));
        DeviceFile.Save(paths.DeviceFile, PairingTestData.Device() with { ServerUrl = "https://127.0.0.1:9" });

        (await PairedServerGuard.VerifyAsync(paths, connect: false, TimeSpan.FromSeconds(5))).Should().BeNull("unpair 只刪本機檔：伺服器停了也解除得了");
        var act = () => PairedServerGuard.VerifyAsync(paths, connect: true, TimeSpan.FromSeconds(5));
        (await act.Should().ThrowAsync<DeviceRuntimeException>()).Which.Message.Should().Contain("/readyz", "不確定是不是正式站就不連（fail closed）");
    }

    [Fact]
    public async Task 沒有配對或device_json損毀_協調者不連線_守門不擋()
    {
        using var dir = new TempDirectory("fd-guard");
        var paths = new SyncPaths(Path.Combine(dir.Root, "sync"));
        (await PairedServerGuard.VerifyAsync(paths, connect: true, TimeSpan.FromSeconds(5))).Should().BeNull();

        Directory.CreateDirectory(paths.Directory);
        File.WriteAllText(paths.DeviceFile, """{"serverUrl":"https://hub.example.com"}""");
        (await PairedServerGuard.VerifyAsync(paths, connect: true, TimeSpan.FromSeconds(5))).Should().BeNull("損毀的 device.json 不建引擎（SyncCoordinator 只在 Ok 時連線）");
    }

    private static string? Json(JToken? token) => token is null || token.Type == JTokenType.Null ? null : token.ToString(Newtonsoft.Json.Formatting.None);

    private static X509Certificate2 SelfSigned(string subject, string dnsName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
