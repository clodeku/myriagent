using System.Security.Cryptography;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Sync.Pairing;

/// <summary>
/// CNG 的裝置金鑰（使用者金鑰存放區）。測試金鑰的名稱前綴是 <c>Myriagent.Test.</c>，一律在 finally 刪除並確認
/// <see cref="CngKey.Exists(string, CngProvider)"/> 為 false（金鑰存放區是這台電腦共用的，不留孤兒金鑰）。
/// </summary>
public sealed class DeviceKeyTests
{
    private static readonly byte[] Data = "myriagent-device-hello-v1\nd_TEST"u8.ToArray();

    [Fact]
    public void 軟體KSP_簽章用P256KeyRules_Verify驗得過()
    {
        var name = DeviceKey.NewKeyName(PairingTestData.TestKeyPrefix);
        try
        {
            using var key = DeviceKey.Create(name, tryTpm: false);

            key.KeyStorage.Should().Be(PairingRules.KeyStorageSoftware);
            key.Name.Should().Be(name);
            P256KeyRules.IsValidSpki(key.PublicSpki).Should().BeTrue();
            P256KeyRules.Verify(key.PublicSpki, Data, key.Sign(Data)).Should().BeTrue();
            P256KeyRules.Verify(key.PublicSpki, "別的內容"u8, key.Sign(Data)).Should().BeFalse();
        }
        finally
        {
            DeleteAndAssertGone(name, PairingRules.KeyStorageSoftware);
        }
    }

    [Fact]
    public void 軟體KSP_ExportPolicy為None_匯出私鑰丟例外()
    {
        var name = DeviceKey.NewKeyName(PairingTestData.TestKeyPrefix);
        try
        {
            using (DeviceKey.Create(name, tryTpm: false))
            {
            }

            using var cng = CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider);
            cng.ExportPolicy.Should().Be(CngExportPolicies.None);
            cng.Invoking(k => k.Export(CngKeyBlobFormat.EccPrivateBlob)).Should().Throw<CryptographicException>();
            using var ecdsa = new ECDsaCng(cng);
            ecdsa.Invoking(e => e.ExportParameters(includePrivateParameters: true)).Should().Throw<CryptographicException>();
        }
        finally
        {
            DeleteAndAssertGone(name, PairingRules.KeyStorageSoftware);
        }
    }

    [Fact]
    public void Open_同一把_公鑰相同_簽章驗得過()
    {
        var name = DeviceKey.NewKeyName(PairingTestData.TestKeyPrefix);
        try
        {
            string spki;
            using (var created = DeviceKey.Create(name, tryTpm: false))
                spki = created.PublicSpki;

            using var opened = DeviceKey.Open(name, PairingRules.KeyStorageSoftware);
            opened.Should().NotBeNull();
            opened!.PublicSpki.Should().Be(spki);
            P256KeyRules.Verify(spki, Data, opened.Sign(Data)).Should().BeTrue();
            DeviceKey.Open(name, "unknown").Should().BeNull();
            // 存放位置不同就找不到；沒有 TPM 的電腦連提供者都開不了，照樣丟出（由引擎分類，不當成金鑰不見了）
            try
            {
                DeviceKey.Open(name, PairingRules.KeyStorageTpm).Should().BeNull("存放位置不同就找不到");
            }
            catch (CryptographicException ex)
            {
                DeviceKeyErrorRules.Classify(ex).Should().NotBe(DeviceKeyErrorKind.Missing, $"提供者開不了（{DeviceKeyErrorRules.Hex(ex.HResult)}）不是金鑰不存在");
            }
        }
        finally
        {
            DeleteAndAssertGone(name, PairingRules.KeyStorageSoftware);
        }
    }

    [Fact]
    public void Open_提供者開不了_丟CryptographicException不回null()
    {
        var name = DeviceKey.NewKeyName(PairingTestData.TestKeyPrefix);

        var act = () => DeviceKey.Open(name, new CngProvider("Myriagent Nonexistent Key Storage Provider"), PairingRules.KeyStorageSoftware);

        var ex = act.Should().Throw<CryptographicException>().Which;
        DeviceKeyErrorRules.Classify(ex).Should().Be(DeviceKeyErrorKind.Unknown, $"HResult {DeviceKeyErrorRules.Hex(ex.HResult)}");
        DeviceKey.Open(" ", PairingRules.KeyStorageSoftware).Should().BeNull("名稱是空的");
    }

    [Fact]
    public void Delete後_Open回null()
    {
        var name = DeviceKey.NewKeyName(PairingTestData.TestKeyPrefix);
        try
        {
            using (DeviceKey.Create(name, tryTpm: false))
            {
            }

            DeviceKey.Exists(name, PairingRules.KeyStorageSoftware).Should().BeTrue();
            DeviceKey.Delete(name, PairingRules.KeyStorageSoftware).Should().BeTrue();
            DeviceKey.Open(name, PairingRules.KeyStorageSoftware).Should().BeNull();
            DeviceKey.Delete(name, PairingRules.KeyStorageSoftware).Should().BeFalse("已經不存在");
        }
        finally
        {
            DeleteAndAssertGone(name, PairingRules.KeyStorageSoftware);
        }
    }

    [Fact]
    public void 第一個提供者失敗_退回軟體KSP_KeyStorage為software_log有原因()
    {
        var name = DeviceKey.NewKeyName(PairingTestData.TestKeyPrefix);
        var logs = new List<string>();
        try
        {
            using var key = DeviceKey.Create(name, [new CngProvider("Myriagent Nonexistent Key Storage Provider"), CngProvider.MicrosoftSoftwareKeyStorageProvider], logs.Add);

            key.KeyStorage.Should().Be(PairingRules.KeyStorageSoftware);
            logs.Should().ContainSingle().Which.Should().Contain("Myriagent Nonexistent Key Storage Provider");
        }
        finally
        {
            DeleteAndAssertGone(name, PairingRules.KeyStorageSoftware);
        }
    }

    [Fact]
    public void 金鑰名稱_前綴加26字()
    {
        var name = DeviceKey.NewKeyName();

        name.Should().StartWith(DeviceKey.NamePrefix);
        name.Length.Should().Be(DeviceKey.NamePrefix.Length + 26);
        CrockfordBase32.IsValid(name.AsSpan(DeviceKey.NamePrefix.Length)).Should().BeTrue();
        DeviceKey.NewKeyName().Should().NotBe(name);
    }

    /// <summary>本機實測 TPM（Platform Crypto Provider）：建立耗時、KeyStorage、簽章；結果記進 commit 訊息</summary>
    [EnvFact("MYRIAGENT_TPM_IT", "在這台電腦的 TPM 建一把測試金鑰（測完刪除）")]
    public void TPM可用時_KeyStorage為tpm()
    {
        var name = DeviceKey.NewKeyName(PairingTestData.TestKeyPrefix);
        var logs = new List<string>();
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            using var key = DeviceKey.Create(name, tryTpm: true, logs.Add);
            watch.Stop();
            Console.WriteLine($"TPM 金鑰：KeyStorage={key.KeyStorage}，建立 {watch.ElapsedMilliseconds} ms，log={string.Join(" | ", logs)}");

            key.KeyStorage.Should().Be(PairingRules.KeyStorageTpm, string.Join(" | ", logs));
            P256KeyRules.Verify(key.PublicSpki, Data, key.Sign(Data)).Should().BeTrue();
        }
        finally
        {
            DeleteAndAssertGone(name, PairingRules.KeyStorageTpm);
            DeleteAndAssertGone(name, PairingRules.KeyStorageSoftware);
        }
    }

    private static void DeleteAndAssertGone(string name, string storage)
    {
        DeviceKey.Delete(name, storage);
        var provider = storage == PairingRules.KeyStorageTpm ? CngProvider.MicrosoftPlatformCryptoProvider : CngProvider.MicrosoftSoftwareKeyStorageProvider;
        bool exists;
        try
        {
            exists = CngKey.Exists(name, provider);
        }
        catch (CryptographicException)
        {
            exists = false;   // 這台沒有 TPM 提供者
        }

        exists.Should().BeFalse($"測試金鑰 {name} 必須刪掉");
    }
}
