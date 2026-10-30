using System.Security.Cryptography;
using System.Text.Json;
using Myriagent.Security;
using MyriagentHub.Admin;
using MyriagentHub.Data;
using MyriagentHub.Pairing;
using MyriagentHub.Tests.TestSupport;
using MyriagentHub.Trust;

namespace MyriagentHub.Tests.Trust;

/// <summary>
/// 伺服器身分金鑰與裝置名冊（sync-protocol 第 3、7 節）：admin status 印出的伺服器身分指紋就是裝置本機確認的依據；
/// 名冊由伺服器發布（設計 14.4），配對與撤銷讓版本前進，裝置的版本不同就給整份。
/// </summary>
public sealed class TrustTests
{
    [Fact]
    public async Task admin_status印出伺服器身分指紋_沒有擁有者金鑰的欄位()
    {
        await using var host = await HubTestHost.StartAsync();
        var identity = host.Services.GetRequiredService<HubIdentity>();
        File.Exists(host.Paths.HubIdentityKeyFile).Should().BeTrue();

        var status = await AdminStatusAsync(host.DataDir);
        status.GetProperty("hubIdentityFingerprint").GetString().Should().Be(identity.Fingerprint).And.MatchRegex("^[0-9A-Z]{4}(-[0-9A-Z]{4}){3}$");
        status.GetProperty("hubIdentityMismatch").ValueKind.Should().Be(JsonValueKind.Null);
        status.TryGetProperty("ownerKeyFingerprint", out _).Should().BeFalse();
        var trust = status.GetProperty("trust");
        trust.GetProperty("registryRev").GetInt64().Should().Be(0);
        trust.GetProperty("hubIdentitySpki").GetString().Should().Be(identity.Spki);
        trust.TryGetProperty("ownerKeysRev", out _).Should().BeFalse();
        identity.Fingerprint.Should().Be(P256KeyRules.AnchorFingerprint(identity.Spki));
    }

    [Fact]
    public async Task 身分金鑰重啟後不變_輪換後舊金鑰簽的聲明驗得過_重啟後保留新金鑰與聲明()
    {
        var dir = TempDir.Create();
        try
        {
            string first;
            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
                first = host.Services.GetRequiredService<HubIdentity>().Spki;

            string rotated;
            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
            {
                var identity = host.Services.GetRequiredService<HubIdentity>();
                identity.Spki.Should().Be(first, "身分金鑰存在 DPAPI 檔，重啟不換");
                identity.Rotation.Should().BeNull();

                var output = new StringWriter();
                (await AdminCommandLine.RunAsync(["rotate-hub-identity", "--data", dir, "--wait", "20"], output)).Should().Be(AdminExitCodes.Ok, output.ToString());
                identity.Spki.Should().NotBe(first);
                var rotation = identity.Rotation!;
                HandshakeRules.AcceptRotation(first, rotation.OldSpki, rotation.NewSpki, rotation.Sig).Should().Be(identity.Spki, "釘舊金鑰的裝置自動改釘");
                rotated = identity.Spki;
                (await AdminStatusAsync(dir)).GetProperty("hubIdentityFingerprint").GetString().Should().Be(P256KeyRules.AnchorFingerprint(rotated));
            }

            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
            {
                var identity = host.Services.GetRequiredService<HubIdentity>();
                identity.Spki.Should().Be(rotated);
                identity.Rotation!.NewSpki.Should().Be(rotated);
            }
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public async Task 名冊_撤銷讓版本前進_含撤銷的裝置_版本相同時不給整份_伺服器比裝置舊時也給整份()
    {
        await using var host = await HubTestHost.StartAsync();
        // 名冊依建立時間再依 id 排序：兩台隔 1 毫秒建立，順序才固定（同一毫秒時 id 是隨機的）
        var keptAt = host.Clock.NowMs;
        using var kept = await TestDevice.AddAsync(host, "辦公室電腦");
        host.Clock.Advance(TimeSpan.FromMilliseconds(1));
        using var retired = await TestDevice.AddAsync(host, "舊筆電");
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        var trust = host.Services.GetRequiredService<TrustService>();
        var before = (await trust.GetRevsAsync()).Registry;

        await host.Services.GetRequiredService<PairingService>().RevokeAsync(retired.Id, DeviceRegistryRules.RevokedRetired, null, null);

        var rev = (await trust.GetRevsAsync()).Registry;
        rev.Should().Be(before + 1);
        var bundle = await trust.GetBundleAsync(0);
        bundle.RegistryRev.Should().Be(rev);
        var registry = bundle.Registry!;
        DeviceRegistryRules.IsValid(registry).Should().BeTrue();
        registry.Select(r => r.DeviceId).Should().Equal(kept.Id, retired.Id);
        registry.Single(r => r.DeviceId == kept.Id).Should().Be(new DeviceRegistryEntry(kept.Id, "辦公室電腦", P256KeyRules.ExportSpki(kept.Key), keptAt));
        var revoked = registry.Single(r => r.DeviceId == retired.Id);
        revoked.RevokedAt.Should().Be(host.Clock.NowMs);
        revoked.RevokedReason.Should().Be(DeviceRegistryRules.RevokedRetired);

        (await trust.GetBundleAsync(rev)).Registry.Should().BeNull("裝置已經有這個版本");
        (await trust.GetBundleAsync(rev + 5)).Registry.Should().HaveCount(2, "伺服器還原到舊的備份：版本比裝置的舊也給整份，讓裝置自己判斷");
        (await AdminStatusAsync(host.DataDir)).GetProperty("trust").GetProperty("registryRev").GetInt64().Should().Be(rev);
    }

    [Fact]
    public async Task 名冊_舊資料格式不合格的裝置不列進去()
    {
        await using var host = await HubTestHost.StartAsync();
        using var good = await TestDevice.AddAsync(host, "辦公室電腦");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            db.Devices.Add(new DeviceRecord
            {
                Id = Myriagent.Sync.SyncIds.NewDeviceId(),
                Name = "OFFICE" + (char)0x202E + "PC",
                TokenHash = RandomNumberGenerator.GetBytes(32),
                DevicePubkey = key.ExportSubjectPublicKeyInfo(),
                KeyStorage = "software",
                CreatedAt = host.Clock.NowMs,
            });
            await db.SaveChangesAsync();
        }

        var bundle = await host.Services.GetRequiredService<TrustService>().GetBundleAsync(-1);
        bundle.Registry!.Select(r => r.DeviceId).Should().Equal(good.Id);
        DeviceRegistryRules.IsValid(bundle.Registry).Should().BeTrue();
    }

    [Fact]
    public async Task 遠端指令的信封_HubIdentity_Sign直接綁簽章委派_裝置以Hello釘選的同一把公鑰驗得過()
    {
        await using var host = await HubTestHost.StartAsync();
        var identity = host.Services.GetRequiredService<HubIdentity>();
        EnvelopeSigner signer = identity.Sign;
        var cid = Myriagent.Sync.SyncIds.NewUlid(host.Clock.NowMs);
        var body = new CommandEnvelopeBody("https://hub.example.com", cid, "d_01J9QZ3K8M5R7T2V4W6X8Y0A1B", "conn-1", "5f3a0c1d2e3f4a5b6c7d8e9f0a1b2c3d", 1,
            new Newtonsoft.Json.Linq.JObject { ["cmd"] = "stop", ["conversationId"] = "5f3a0c1d2e3f4a5b6c7d8e9f0a1b2c3d" }, 0, false, false,
            host.Clock.NowMs, host.Clock.NowMs, host.Clock.NowMs + 30_000, new CommandIssuer(CommandIssuerKinds.Browser, "Chrome・Windows"), new CommandAuth(host.Clock.NowMs, false));

        CommandEnvelope.TrySign(body, signer, out var envelope).Should().BeTrue();

        var target = new CommandEnvelopeTarget(identity.Spki, body.Aud, body.Dev, body.Conn);
        var verdict = CommandEnvelope.Verify(envelope, cid, target, new ServerTimeBounds(host.Clock.NowMs, host.Clock.NowMs + 1_000));
        verdict.Ok.Should().BeTrue(verdict.Detail);
        verdict.ContentSha256.Should().Be(CommandEnvelope.ContentDigest(body));
    }

    internal static async Task<JsonElement> AdminStatusAsync(string dataDir)
    {
        var output = new StringWriter();
        (await AdminCommandLine.RunAsync(["status", "--data", dataDir], output)).Should().Be(AdminExitCodes.Ok);
        return JsonDocument.Parse(output.ToString()).RootElement.Clone();
    }
}
