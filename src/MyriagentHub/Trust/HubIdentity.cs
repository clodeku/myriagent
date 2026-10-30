using System.Security.Cryptography;
using Myriagent.Security;
using Myriagent.Sync;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Security;

namespace MyriagentHub.Trust;

/// <summary>
/// 伺服器身分金鑰（設計 8.2.4、sync-protocol 第 3 節）：ECDSA P-256，PKCS#8 以 DPAPI LocalMachine 存在 <c>secrets\hub-identity.dpapi</c>
/// （ACL 只有 Administrators、SYSTEM 與服務帳戶）。裝置配對時釘選它的公鑰，之後每次 Hello 以它簽 <see cref="HandshakeRules.HubSignedData"/>；
/// TLS 憑證可隨 ACME 自由輪替，不影響釘選。檔案不在備份裡：伺服器重建時金鑰是新的，各裝置要在本機確認新指紋（與 <c>admin status</c> 比對）。
/// <para>
/// 輪換（<c>admin rotate-hub-identity</c>）：產生新金鑰、以舊金鑰簽 <see cref="HandshakeRules.RotationSignedData"/>，聲明存 kv，
/// 之後每個 HelloResponse 都附上（裝置釘的是舊金鑰就自動改釘）。先寫聲明、再換金鑰檔：兩步之間中斷時金鑰還是舊的，
/// 聲明的新公鑰對不上目前的金鑰就不會附上（<see cref="Rotation"/>），重跑一次即可。
/// </para>
/// </summary>
internal sealed class HubIdentity(HubPaths paths, KvStore kv, ILogger<HubIdentity> log) : IDisposable
{
    public const string Purpose = "hub-identity";

    private readonly Lock gate = new();
    private ECDsa? key;
    private string? spki;
    private HubIdentityRotation? rotation;

    /// <summary>身分公鑰（SPKI base64url）</summary>
    public string Spki => spki ?? throw new InvalidOperationException("伺服器身分金鑰尚未載入");

    /// <summary>16 字指紋（裝置要本機確認時與它比對）</summary>
    public string Fingerprint => P256KeyRules.AnchorFingerprint(Spki);

    /// <summary>最近一次輪換的聲明（新公鑰就是目前的身分公鑰時才有）</summary>
    public HubIdentityRotation? Rotation
    {
        get
        {
            var current = Volatile.Read(ref rotation);
            return current is not null && current.NewSpki == spki ? current : null;
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var loaded = Load(paths) ?? Create();
        lock (gate)
        {
            key?.Dispose();
            key = loaded;
            spki = P256KeyRules.ExportSpki(loaded);
        }
        Volatile.Write(ref rotation, await kv.GetAsync<HubIdentityRotation>(KvKeys.HubIdentityRotation, cancellationToken));
        log.LogInformation("伺服器身分指紋 {Fingerprint}", Fingerprint);
    }

    /// <summary>以身分私鑰簽（P1363 base64url）</summary>
    public string Sign(ReadOnlySpan<byte> data)
    {
        lock (gate)
        {
            if (key is null)
                throw new InvalidOperationException("伺服器身分金鑰尚未載入");
            return P256KeyRules.Sign(key, data);
        }
    }

    /// <summary>輪換身分金鑰：回傳輪換聲明（舊公鑰、新公鑰、舊金鑰的簽章）</summary>
    public async Task<HubIdentityRotation> RotateAsync(CancellationToken cancellationToken)
    {
        var next = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var newSpki = P256KeyRules.ExportSpki(next);
            var oldSpki = Spki;
            var statement = new HubIdentityRotation(oldSpki, newSpki, Sign(HandshakeRules.RotationSignedData(oldSpki, newSpki)));
            await kv.SetAsync(KvKeys.HubIdentityRotation, statement, cancellationToken);
            Save(next);
            lock (gate)
            {
                key?.Dispose();
                key = next;
                spki = newSpki;
            }
            Volatile.Write(ref rotation, statement);
            log.LogWarning("伺服器身分金鑰已輪換：{Old} → {New}", P256KeyRules.AnchorFingerprint(oldSpki), P256KeyRules.AnchorFingerprint(newSpki));
            return statement;
        }
        catch
        {
            next.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            key?.Dispose();
            key = null;
        }
    }

    /// <summary>不經服務直接讀金鑰檔算指紋（admin status：本機確認時的依據不靠服務自報）；沒有檔案或讀不出來回 null</summary>
    public static string? ReadFingerprint(HubPaths paths)
    {
        using var loaded = Load(paths);
        return loaded is null ? null : P256KeyRules.AnchorFingerprint(P256KeyRules.ExportSpki(loaded));
    }

    private static ECDsa? Load(HubPaths paths)
    {
        var pkcs8 = SecretFiles.ReadProtected(paths.HubIdentityKeyFile, Purpose);
        if (pkcs8 is null)
            return null;
        try
        {
            var loaded = ECDsa.Create();
            loaded.ImportPkcs8PrivateKey(pkcs8, out _);
            if (loaded.KeySize != 256)
            {
                loaded.Dispose();
                throw new InvalidOperationException("伺服器身分金鑰不是 P-256");
            }
            return loaded;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    private ECDsa Create()
    {
        var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Save(created);
        log.LogInformation("已建立伺服器身分金鑰（DPAPI LocalMachine）：{Path}", paths.HubIdentityKeyFile);
        return created;
    }

    private void Save(ECDsa value)
    {
        var pkcs8 = value.ExportPkcs8PrivateKey();
        try
        {
            SecretFiles.WriteProtected(paths.HubIdentityKeyFile, pkcs8, Purpose);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }
}
