using Myriagent.Sync;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Pairing;

public sealed class DeviceFileTests : IDisposable
{
    private readonly TempDirectory temp = new("device-file");

    private string FilePath => temp.Combine("sync", "device.json");

    public void Dispose() => temp.Dispose();

    [Fact]
    public void 權杖以DPAPI保護_檔案內容不含明文()
    {
        var token = PairingTestData.NewToken();
        var data = PairingTestData.Device(token: token);

        DeviceFile.Save(FilePath, data);

        var text = File.ReadAllText(FilePath);
        text.Should().NotContain(token).And.NotContain(token[4..]);
        text.Should().NotContain("chd_");
        DeviceSecrets.Unprotect(data.ProtectedDeviceToken).Should().Be(token);
        var (status, loaded) = DeviceFile.Load(FilePath);
        status.Should().Be(DeviceFileStatus.Ok);
        loaded.Should().Be(data);
        DeviceSecrets.Unprotect(loaded!.ProtectedDeviceToken).Should().Be(token);
        File.Exists(FilePath + ".tmp").Should().BeFalse("先寫 .tmp 再改名取代");
    }

    [Fact]
    public void Unprotect竄改過的內容_回null()
    {
        var protectedToken = DeviceSecrets.Protect(PairingTestData.NewToken());
        var bytes = Convert.FromBase64String(protectedToken);
        bytes[^5] ^= 0x5A;

        DeviceSecrets.Unprotect(Convert.ToBase64String(bytes)).Should().BeNull();
        DeviceSecrets.Unprotect("不是 base64").Should().BeNull();
        DeviceSecrets.Unprotect(string.Empty).Should().BeNull();
        DeviceSecrets.Unprotect(null).Should().BeNull();
    }

    [Fact]
    public void 損毀的JSON_Corrupt()
    {
        temp.WriteFile(Path.Combine("sync", "device.json"), "{ 這不是 JSON");
        DeviceFile.Load(FilePath).Status.Should().Be(DeviceFileStatus.Corrupt);

        // JSON 合法但缺欄位或格式不對
        var good = JObject.Parse(Save(PairingTestData.Device()));
        foreach (var (field, value) in new (string, JToken?)[]
        {
            ("deviceId", "d_short"),
            ("deviceId", null),
            ("serverUrl", "http://insecure.test"),
            ("keyStorage", "cloud"),
            ("devicePubKey", "AAAA"),
            ("hubIdentitySpki", 12),
            ("version", 0),
            ("protectedDeviceToken", ""),
        })
        {
            var bad = (JObject)good.DeepClone();
            if (value is null)
                bad.Remove(field);
            else
                bad[field] = value;
            File.WriteAllText(FilePath, bad.ToString());
            DeviceFile.Load(FilePath).Status.Should().Be(DeviceFileStatus.Corrupt, $"{field}={value}");
        }

        File.WriteAllText(FilePath, "[]");
        DeviceFile.Load(FilePath).Status.Should().Be(DeviceFileStatus.Corrupt, "不是物件");
    }

    [Fact]
    public void 不存在_Missing()
    {
        DeviceFile.Load(FilePath).Should().Be((DeviceFileStatus.Missing, (DeviceFileData?)null));
    }

    [Fact]
    public void Save_保留不認得的欄位()
    {
        var data = PairingTestData.Device();
        var json = JObject.Parse(Save(data));
        json["futureField"] = new JObject { ["nested"] = 3 };
        json["version"] = 2;
        File.WriteAllText(FilePath, json.ToString());

        var (status, loaded) = DeviceFile.Load(FilePath);
        status.Should().Be(DeviceFileStatus.Ok, "較新版寫的也讀（欄位只做加法）");
        DeviceFile.Save(FilePath, loaded! with { HubIdentitySpki = PairingTestData.NewSpki() });

        var after = JObject.Parse(File.ReadAllText(FilePath));
        after["futureField"]!["nested"]!.Value<int>().Should().Be(3);
        after["hubIdentitySpki"]!.Value<string>().Should().NotBe(data.HubIdentitySpki);
    }

    [Fact]
    public void ToString不含ProtectedDeviceToken()
    {
        var data = PairingTestData.Device();

        var text = data.ToString();

        text.Should().NotContain(data.ProtectedDeviceToken).And.NotContain("ProtectedDeviceToken");
        text.Should().Contain(data.DeviceId).And.Contain(data.KeyName);
    }

    private string Save(DeviceFileData data)
    {
        DeviceFile.Save(FilePath, data);
        return File.ReadAllText(FilePath);
    }
}

public sealed class PairingRecoveryTests : IDisposable
{
    private readonly TempDirectory temp = new("pairing-recovery");

    private SyncPaths Paths => new(temp.Combine("sync"));

    public void Dispose() => temp.Dispose();

    [Fact]
    public void 有pending沒有device_刪孤兒金鑰_pending檔與殘留sync_db()
    {
        var name = DeviceKey.NewKeyName(PairingTestData.TestKeyPrefix);
        try
        {
            using (DeviceKey.Create(name, tryTpm: false))
            {
            }

            // 記錄寫的是 tpm（建立前寫的），實際退回了軟體 KSP：也要刪得到
            PendingKeyRecord.Write(Paths.PendingKeyFile, new PendingKeyRecord(name, PairingRules.KeyStorageTpm, 1));
            temp.WriteFile(Path.Combine("sync", "sync.db"));
            temp.WriteFile(Path.Combine("sync", "sync.db-wal"));
            temp.WriteFile(Path.Combine("sync", "sync.db-shm"));
            var logs = new List<string>();

            PairingRecovery.Cleanup(Paths, logs.Add);

            DeviceKey.Exists(name, PairingRules.KeyStorageSoftware).Should().BeFalse();
            File.Exists(Paths.PendingKeyFile).Should().BeFalse();
            File.Exists(Paths.DatabaseFile).Should().BeFalse();
            File.Exists(Paths.DatabaseFile + "-wal").Should().BeFalse();
            File.Exists(Paths.DatabaseFile + "-shm").Should().BeFalse();
            logs.Should().Contain(l => l.Contains(name, StringComparison.Ordinal));
        }
        finally
        {
            DeviceKey.Delete(name, PairingRules.KeyStorageSoftware);
        }
    }

    [Fact]
    public void pending與device的金鑰相同_只刪pending檔()
    {
        var name = DeviceKey.NewKeyName(PairingTestData.TestKeyPrefix);
        try
        {
            string spki;
            using (var key = DeviceKey.Create(name, tryTpm: false))
                spki = key.PublicSpki;
            DeviceFile.Save(Paths.DeviceFile, PairingTestData.Device(keyName: name, devicePubKey: spki));
            PendingKeyRecord.Write(Paths.PendingKeyFile, new PendingKeyRecord(name, PairingRules.KeyStorageSoftware, 1));
            temp.WriteFile(Path.Combine("sync", "sync.db"));

            PairingRecovery.Cleanup(Paths, null);

            DeviceKey.Exists(name, PairingRules.KeyStorageSoftware).Should().BeTrue("device.json 用的就是這把（提交後、刪 pending 前當掉）");
            File.Exists(Paths.PendingKeyFile).Should().BeFalse();
            File.Exists(Paths.DatabaseFile).Should().BeTrue("已配對的 sync.db 不動");
            File.Exists(Paths.DeviceFile).Should().BeTrue();
        }
        finally
        {
            DeviceKey.Delete(name, PairingRules.KeyStorageSoftware);
            DeviceKey.Exists(name, PairingRules.KeyStorageSoftware).Should().BeFalse();
        }
    }

    [Fact]
    public void pending與device不同把_刪pending那把_device的不動()
    {
        var pendingName = DeviceKey.NewKeyName(PairingTestData.TestKeyPrefix);
        var deviceName = DeviceKey.NewKeyName(PairingTestData.TestKeyPrefix);
        try
        {
            using (DeviceKey.Create(pendingName, tryTpm: false))
            {
            }

            using (DeviceKey.Create(deviceName, tryTpm: false))
            {
            }

            DeviceFile.Save(Paths.DeviceFile, PairingTestData.Device(keyName: deviceName));
            PendingKeyRecord.Write(Paths.PendingKeyFile, new PendingKeyRecord(pendingName, PairingRules.KeyStorageSoftware, 1));

            PairingRecovery.Cleanup(Paths, null);

            DeviceKey.Exists(pendingName, PairingRules.KeyStorageSoftware).Should().BeFalse();
            DeviceKey.Exists(deviceName, PairingRules.KeyStorageSoftware).Should().BeTrue();
            File.Exists(Paths.PendingKeyFile).Should().BeFalse();
        }
        finally
        {
            DeviceKey.Delete(pendingName, PairingRules.KeyStorageSoftware);
            DeviceKey.Delete(deviceName, PairingRules.KeyStorageSoftware);
        }
    }

    [Fact]
    public void 沒有pending_什麼都不做_device損毀時也不刪()
    {
        temp.WriteFile(Path.Combine("sync", "sync.db"));
        PairingRecovery.Cleanup(Paths, null);
        File.Exists(Paths.DatabaseFile).Should().BeTrue();

        PendingKeyRecord.Write(Paths.PendingKeyFile, new PendingKeyRecord(DeviceKey.NewKeyName(PairingTestData.TestKeyPrefix), PairingRules.KeyStorageSoftware, 1));
        temp.WriteFile(Path.Combine("sync", "device.json"), "{ 損毀");
        PairingRecovery.Cleanup(Paths, null);

        File.Exists(Paths.PendingKeyFile).Should().BeTrue("分不出 pending 那把是不是 device.json 的，先不刪");
        File.Exists(Paths.DatabaseFile).Should().BeTrue();
    }
}
