using System.Diagnostics;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using MyriagentHub.Hosting;
using MyriagentHub.Security;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Tls;

/// <summary>ACME 簽發結果（PFX 含私鑰；中繼憑證另外帶）</summary>
internal sealed record IssuedCertificate(byte[] Pfx, string ChainPem, string Thumbprint, string Issuer, long NotBefore, long NotAfter, string? AriId, long ElapsedMs);

/// <summary>
/// 程式內 ACME（Certes 4.1.0，移植原型）：TLS-ALPN-01 在正式服務的同一個 443 上完成（驗證連線由 <see cref="TlsAlpnChallengeStore"/> 回應），
/// 不必停服務、不必開 80。目錄公告了設定的憑證設定檔時以它下單並帶 ARI 的 replaces（ARI 協調的續期不受速率限制）。
/// 正式組建沒有「接受任何目錄憑證」的選項：本機 Pebble 端到端以 Hub:Acme:DirectoryTrustPem 指定要信任的根憑證。
/// </summary>
internal sealed class AcmeService(TlsAlpnChallengeStore challenges, HubPaths paths, IOptions<HubOptions> options, ILogger<AcmeService> log)
{
    private const string AccountKeyPurpose = "acme-account";

    private AcmeSettings Settings => options.Value.Acme;

    public async Task<IssuedCertificate> IssueAsync(string directory, string domain, string? replacesAriId, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        using var http = CreateHttpClient();
        var directoryUri = new Uri(directory);
        var accountKey = LoadOrCreateAccountKey();
        var acme = new AcmeContext(directoryUri, accountKey, new AcmeHttpClient(directoryUri, http));
        var contacts = string.IsNullOrWhiteSpace(Settings.Email) ? new List<string>() : new List<string> { "mailto:" + Settings.Email.Trim() };
        await acme.NewAccount(contacts, true);

        var order = await NewOrderAsync(acme, domain, replacesAriId);
        var authorization = (await order.Authorizations()).Single();
        var challenge = await authorization.TlsAlpn() ?? throw new InvalidOperationException("CA 沒有提供 tls-alpn-01 驗證");
        using (var challengeCertificate = TlsAlpnChallengeStore.CreateChallengeCertificate(domain, challenge.KeyAuthz))
        {
            challenges.Add(domain, challengeCertificate);
            try
            {
                await challenge.Validate();
                for (var i = 0; ; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var resource = await authorization.Resource();
                    if (resource.Status == AuthorizationStatus.Valid)
                        break;
                    if (resource.Status == AuthorizationStatus.Invalid || i >= 120)
                    {
                        var detail = await challenge.Resource();
                        throw new InvalidOperationException($"TLS-ALPN-01 驗證失敗：{resource.Status} {detail.Error?.Type} {detail.Error?.Detail}");
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                }
            }
            finally
            {
                challenges.Remove(domain);
            }
        }

        var certificateKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
        var chain = await order.Generate(new CsrInfo { CommonName = domain }, certificateKey);
        var leafPem = chain.Certificate.ToPem();
        using var leaf = X509Certificate2.CreateFromPem(leafPem, certificateKey.ToPem());
        var pfx = leaf.Export(X509ContentType.Pkcs12);
        var chainPem = string.Concat(chain.Issuers.Select(c => c.ToPem()));
        string? ariId = null;
        try
        {
            ariId = chain.GetRenewalInfoCertificateId();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            log.LogWarning("新憑證沒有 ARI 識別碼（沒有 authority key identifier）：{Message}", ex.Message);
        }
        log.LogInformation("ACME 簽發成功：{Thumbprint}（{Issuer}，到期 {NotAfter:O}，{Ms} ms）", leaf.Thumbprint, leaf.Issuer, leaf.NotAfter, sw.ElapsedMilliseconds);
        return new IssuedCertificate(pfx, chainPem, leaf.Thumbprint, leaf.Issuer,
            new DateTimeOffset(leaf.NotBefore.ToUniversalTime()).ToUnixTimeMilliseconds(),
            new DateTimeOffset(leaf.NotAfter.ToUniversalTime()).ToUnixTimeMilliseconds(),
            ariId, sw.ElapsedMilliseconds);
    }

    /// <summary>查 ARI 建議的續期視窗（RFC 9773）；伺服器不支援或失敗回 null</summary>
    public async Task<(long Start, long End, long? RetryAfterMs)?> GetRenewalWindowAsync(string directory, string ariId, CancellationToken cancellationToken)
    {
        using var http = CreateHttpClient();
        var directoryUri = new Uri(directory);
        var acme = new AcmeContext(directoryUri, LoadOrCreateAccountKey(), new AcmeHttpClient(directoryUri, http));
        var info = await acme.GetRenewalInfo(ariId);
        cancellationToken.ThrowIfCancellationRequested();
        return (info.SuggestedWindow.Start.ToUnixTimeMilliseconds(), info.SuggestedWindow.End.ToUnixTimeMilliseconds(),
            info.RetryAfter is { } retry ? (long)retry.TotalMilliseconds : null);
    }

    private async Task<IOrderContext> NewOrderAsync(AcmeContext acme, string domain, string? replacesAriId)
    {
        var profile = Settings.Profile;
        if (!string.IsNullOrWhiteSpace(profile))
        {
            var meta = (await acme.GetDirectory()).Meta;
            if (meta?.Profiles is { } profiles && profiles.ContainsKey(profile))
                return await acme.NewOrderWithProfile([domain], profile, null, null, replacesAriId, false);
            log.LogInformation("ACME 目錄沒有公告設定檔 {Profile}，改用預設下單（不帶 replaces）", profile);
        }
        return await acme.NewOrder([domain]);
    }

    private IKey LoadOrCreateAccountKey()
    {
        var protectedPem = SecretFiles.ReadProtected(paths.AcmeAccountKeyFile, AccountKeyPurpose);
        if (protectedPem is not null)
            return KeyFactory.FromPem(System.Text.Encoding.UTF8.GetString(protectedPem));
        var key = KeyFactory.NewKey(KeyAlgorithm.ES256);
        SecretFiles.WriteProtected(paths.AcmeAccountKeyFile, System.Text.Encoding.UTF8.GetBytes(key.ToPem()), AccountKeyPurpose);
        log.LogInformation("已建立 ACME 帳戶金鑰（DPAPI LocalMachine）：{Path}", paths.AcmeAccountKeyFile);
        return key;
    }

    /// <summary>連 ACME 目錄的 HTTP 用戶端：預設系統信任清單；設定了 DirectoryTrustPem 就只信任那個根憑證（不是「接受任何憑證」）</summary>
    private HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) };
        if (!string.IsNullOrWhiteSpace(Settings.DirectoryTrustPem))
        {
            var roots = new X509Certificate2Collection();
            roots.ImportFromPemFile(Settings.DirectoryTrustPem);
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is null)
                    return false;
                if (errors == SslPolicyErrors.None)
                    return true;
                if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
                    return false;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.AddRange(roots);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()));
            };
        }
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }
}
