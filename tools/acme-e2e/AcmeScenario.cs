using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace MyriagentTools.AcmeE2e;

/// <summary>
/// 端到端流程（每一步寫進 result.json）：
/// 1. 準備：下載（固定版本＋SHA-256）並解開 Pebble 與 challtestsrv、產生 Pebble 監聽憑證、挑連接埠、拍主機快照。
/// 2. 啟動 challtestsrv（DNS 把網域解析到 127.0.0.1／::1）並先讓網域 SERVFAIL；啟動 Pebble（profiles classic／shortlived、支援 ARI）。
/// 3. 啟動 MyriagentHub（ACME 開、SkipStaging、DirectoryTrustPem＝監聽 CA）：第一次申請因 DNS 失敗 → 退避 1 小時、仍以自簽憑證服務。
/// 4. 熱換前開兩條連線（手寫 HTTP/1.1 keep-alive、HttpClient HTTP/2）。
/// 5. 恢復 DNS → admin cert-renew：退避中立即重試，TLS-ALPN-01 在同一個埠完成、熱換；新連線拿到 Pebble 簽的憑證（含中繼）。
/// 6. 熱換前的兩條連線照常回應（沒有重新交握、沒有新 TCP 連線）。
/// 7. ARI：以 Pebble 管理介面把這張憑證的 ARI 視窗改成過去 → admin cert-renew → 帶 replaces 續期（Pebble 記錄為 replacement）。
/// 8. cert-renew 一律重查 ARI：視窗未到不續期；CA 隨後把視窗改成過去，12 小時內馬上再 cert-renew 也重查並續期；
///    cert-renew --force 不看續期時點直接續期。
/// 9. admin stop → 重新啟動：以指紋載入已存的憑證、不重新申請。
/// 10. 收尾：正常停止 MyriagentHub、結束 Pebble 與 challtestsrv；檢查孤兒金鑰檔、移除這次寫進 CurrentUser\CA 的 Pebble 中繼憑證。
/// </summary>
internal sealed class AcmeScenario(AcmeE2eOptions options)
{
    private static readonly string[] H2AndHttp11 = ["h2", "http/1.1"];

    private ResultLog log = null!;
    private PortPlan ports = null!;
    private (string Pebble, string ChallTestSrv) executables;
    private ManagedProcess? challtestsrv;
    private ManagedProcess? pebble;
    private HubInstance? hub;
    private KeepAliveHttp1? http1;
    private Http2Session? http2;
    private PebbleControl? pebbleControl;
    private ChallTestSrvControl? challControl;
    private X509Certificate2? pebbleRoot;

    private string Domain => options.Domain;

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.WorkDir);
        log = new ResultLog(Path.Combine(options.WorkDir, "result.json"), new
        {
            tool = "acme-e2e",
            pebble = PinnedAssets.PebbleVersion,
            startedAt = DateTimeOffset.Now,
            workDir = options.WorkDir,
            cacheDir = options.CacheDir,
            hubExe = options.HubExe,
            domain = Domain,
            staging = options.Staging,
            machine = Environment.MachineName,
            os = Environment.OSVersion.VersionString,
            dotnet = Environment.Version.ToString(),
        });
        Console.WriteLine($"工作資料夾：{options.WorkDir}");

        var keysBefore = HostSideEffects.SnapshotKeys();
        var userCaBefore = HostSideEffects.SnapshotStore(StoreName.CertificateAuthority, StoreLocation.CurrentUser);
        var machineCaBefore = HostSideEffects.SnapshotStore(StoreName.CertificateAuthority, StoreLocation.LocalMachine);
        using var job = ChildProcessJob.Create();
        try
        {
            await PrepareAsync(keysBefore, userCaBefore, machineCaBefore, cancellationToken);
            await StartEnvironmentAsync(job, cancellationToken);
            var selfSigned = await FirstAttemptFailsAsync(job, cancellationToken);
            await OpenConnectionsAsync(selfSigned, cancellationToken);
            var first = await CertRenewAfterBackoffAsync(selfSigned, cancellationToken);
            await KeepAliveSurvivesAsync("熱換：熱換前開著的連線不斷", selfSigned, cancellationToken);
            var renewed = await AriRenewalAsync(first, selfSigned, cancellationToken);
            var current = await CertRenewRecheckAndForceAsync(renewed, cancellationToken);
            await RestartKeepsCertificateAsync(job, current, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            log.Step("中止", false, "使用者中止（Ctrl+C）");
        }
        catch (Exception ex)
        {
            log.Step("例外", false, TlsProbe.Describe(ex), new { exception = ex.ToString() });
        }
        finally
        {
            await ShutdownAsync(keysBefore, userCaBefore, machineCaBefore);
        }

        log.Set("finishedAt", DateTimeOffset.Now);
        log.Set("ok", !log.AnyFailed);
        Console.WriteLine(log.AnyFailed ? $"有檢查失敗，結果檔：{log.Path}" : $"全部通過，結果檔：{log.Path}");
        return log.AnyFailed ? 1 : 0;
    }

    // ───── 1. 準備 ─────

    private async Task PrepareAsync(
        IReadOnlyDictionary<string, KeyFileEntry> keysBefore,
        IReadOnlyList<StoreCertificate> userCaBefore,
        IReadOnlyList<StoreCertificate> machineCaBefore,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(options.HubExe) || !File.Exists(options.HubExe))
            throw new FileNotFoundException("找不到 MyriagentHub.exe（先 dotnet build src/MyriagentHub/MyriagentHub.csproj -c Release，或用 --hub 指定）：" + options.HubExe);

        void Note(string message) => Console.WriteLine("  " + message);
        var pebbleZip = await Downloader.EnsureAsync(PinnedAssets.Pebble, options.CacheDir, Note, cancellationToken);
        var challZip = await Downloader.EnsureAsync(PinnedAssets.ChallTestSrv, options.CacheDir, Note, cancellationToken);
        var pebbleExe = await Downloader.ExtractAsync(pebbleZip, Path.Combine(options.WorkDir, "bin", "pebble"), cancellationToken);
        var challExe = await Downloader.ExtractAsync(challZip, Path.Combine(options.WorkDir, "bin", "challtestsrv"), cancellationToken);
        ports = PortPlan.Pick(options.HubPort, options.AcmePort, options.AcmeManagementPort, options.DnsPort, options.DnsManagementPort);
        log.Set("ports", ports);
        log.Set("executables", new { pebble = pebbleExe, challtestsrv = challExe });

        var pebbleCerts = HostSideEffects.PebbleCertificates(userCaBefore.Concat(machineCaBefore));
        log.Step("準備", true,
            $"Pebble {PinnedAssets.PebbleVersion} 與 challtestsrv 的壓縮檔大小與 SHA-256 相符（{(pebbleZip.Downloaded || challZip.Downloaded ? "這次下載" : "快取")}），"
            + $"連接埠 hub={ports.Hub} acme={ports.Acme} dns={ports.Dns}；主機快照：金鑰檔 {keysBefore.Count} 個、CurrentUser\\CA {userCaBefore.Count} 張、執行前已有的 Pebble 憑證 {pebbleCerts.Count} 張",
            new
            {
                assets = new[] { pebbleZip, challZip }.Select(a => new { a.Asset.Name, url = a.Asset.Url, a.Asset.Size, a.Asset.Sha256, cached = a.ZipPath, a.Downloaded }),
                pebbleExe,
                challExe,
                keyFilesBefore = keysBefore.Count,
                currentUserCaBefore = userCaBefore.Count,
                localMachineCaBefore = machineCaBefore.Count,
                pebbleCertificatesBefore = pebbleCerts,
            });
        executables = (pebbleExe.Path, challExe.Path);
    }

    // ───── 2. Pebble 與 challtestsrv ─────

    private async Task StartEnvironmentAsync(ChildProcessJob job, CancellationToken cancellationToken)
    {
        var pebbleDir = Path.Combine(options.WorkDir, "pebble");
        var listener = PebbleSetup.CreateListenerCertificates(pebbleDir);
        var config = PebbleSetup.WriteConfig(pebbleDir, ports, listener);

        // challtestsrv：只開 DNS 與管理介面（其他驗證伺服器預設聽所有介面的 5001～5003、8443，會跳防火牆詢問，全部關掉）
        challtestsrv = ManagedProcess.Start("challtestsrv", executables.ChallTestSrv,
        [
            "-management", $"127.0.0.1:{ports.DnsManagement}",
            "-dnsserver", $"127.0.0.1:{ports.Dns}",
            "-http01=", "-https01=", "-tlsalpn01=", "-doh=",
            "-defaultIPv4", "127.0.0.1",
            "-defaultIPv6", "::1",
        ], options.WorkDir, Path.Combine(options.WorkDir, "challtestsrv.log"), null, job, captureLines: true);
        if (!await NetWait.TcpAsync(ports.DnsManagement, options.Timeout, () => challtestsrv.HasExited, cancellationToken))
            throw new InvalidOperationException("challtestsrv 沒有起來，看 challtestsrv.log");
        challControl = new ChallTestSrvControl(ports.DnsManagement);
        var servfail = await challControl.SetServFailAsync(Domain, cancellationToken);
        log.Step("啟動 challtestsrv", servfail == 200,
            $"DNS 127.0.0.1:{ports.Dns}（A→127.0.0.1、AAAA→::1）；先讓 {Domain} 回 SERVFAIL（第一次申請要失敗）",
            new { pid = challtestsrv.Id, setServFailStatus = servfail });

        // Pebble：VA 不隨機延遲、不故意拒 nonce、不重用授權（每次續期都真的走一次 TLS-ALPN-01）
        pebble = ManagedProcess.Start("pebble", executables.Pebble,
            ["-config", config, "-dnsserver", $"127.0.0.1:{ports.Dns}"],
            pebbleDir, Path.Combine(options.WorkDir, "pebble.log"),
            new Dictionary<string, string>
            {
                ["PEBBLE_VA_NOSLEEP"] = "1",
                ["PEBBLE_WFE_NONCEREJECT"] = "0",
                ["PEBBLE_AUTHZREUSE"] = "0",
            }, job, captureLines: true);
        pebbleControl = new PebbleControl(ports, listener.Ca);
        JsonElement directory = default;
        var deadline = DateTime.UtcNow + options.Timeout;
        while (true)
        {
            if (pebble.HasExited)
                throw new InvalidOperationException("Pebble 啟動後立即結束，看 pebble.log");
            try
            {
                directory = await pebbleControl.DirectoryAsync(cancellationToken);
                break;
            }
            catch (HttpRequestException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(200, cancellationToken);
            }
        }
        pebbleRoot = await pebbleControl.RootAsync(cancellationToken);
        var intermediate = await pebbleControl.IntermediateAsync(cancellationToken);
        var renewalInfo = Str(directory, "renewalInfo");
        var profiles = directory.TryGetProperty("meta", out var meta) && meta.TryGetProperty("profiles", out var p) && p.ValueKind == JsonValueKind.Object
            ? p.EnumerateObject().Select(x => x.Name).ToList()
            : [];
        log.Step("啟動 Pebble", renewalInfo is not null && profiles.Contains("classic"),
            $"目錄 {pebbleControl.DirectoryUrl}：renewalInfo（ARI）{(renewalInfo is null ? "沒有" : "有")}、設定檔 {string.Join("／", profiles)}；發證根 {pebbleRoot.Subject}",
            new
            {
                pid = pebble.Id,
                directory = JsonSerializer.Deserialize<object>(directory.GetRawText()),
                root = new { pebbleRoot.Subject, pebbleRoot.Thumbprint },
                intermediate = new { intermediate.Subject, intermediate.Thumbprint },
                listenerCa = listener.CaPemPath,
            });

        hub = new HubInstance(options.HubExe, Path.Combine(options.WorkDir, "hub-data"), Domain, ports.Hub,
        [
            "--environment", "Development",
            $"--Hub:ServerDomain={Domain}",
            $"--Hub:HttpsPort={ports.Hub}",
            "--Hub:BindAddress=localhost",
            "--Hub:BelowNormalPriority=false",
            "--Hub:Tls:CertStore=file",
            "--Hub:Acme:Enabled=true",
            $"--Hub:Acme:Directory={pebbleControl.DirectoryUrl}",
            $"--Hub:Acme:StagingDirectory={pebbleControl.DirectoryUrl}",
            $"--Hub:Acme:SkipStaging={(options.Staging ? "false" : "true")}",
            "--Hub:Acme:Profile=classic",
            $"--Hub:Acme:DirectoryTrustPem={listener.CaPemPath}",
            "--Hub:AdminInbox:PollSeconds=1",
            "--Hub:Backup:Enabled=false",
            "--Hub:SelfTest:Enabled=false",
        ], options.WorkDir);
    }

    // ───── 3. 第一次申請失敗 ─────

    private async Task<string> FirstAttemptFailsAsync(ChildProcessJob job, CancellationToken cancellationToken)
    {
        var startMs = await hub!.StartAsync(job, options.Timeout, cancellationToken);
        var health = await hub.HealthzAsync(cancellationToken);
        log.Step("啟動 MyriagentHub", true, $"https://{Domain}:{ports.Hub}（{startMs:0} ms 後 /healthz 200，build {Str(health, "build")}）",
            new { pid = hub.Process!.Id, dataDir = hub.DataDir, healthz = health });

        var ready = await WaitReadyzAsync(r => Long(Tls(r), "lastAttemptAt") is not null, TimeSpan.FromSeconds(1), cancellationToken)
            ?? throw new TimeoutException("MyriagentHub 沒有開始第一次 ACME 申請");
        var tls = Tls(ready);
        var lastAttempt = Long(tls, "lastAttemptAt") ?? 0;
        var nextCheck = Long(tls, "nextCheckAt") ?? 0;
        var backoffMinutes = (nextCheck - lastAttempt) / 60_000.0;
        var selfSignedThumbprint = Str(tls, "thumbprint") ?? string.Empty;
        var handshake = await TlsProbe.HandshakeAsync(ports.Hub, Domain, H2AndHttp11, cancellationToken);
        var alerts = ready.TryGetProperty("alerts", out var a) ? a.EnumerateArray().Select(x => x.GetString()).ToList() : [];
        // 失敗原因要是 TLS-ALPN-01 驗證（DNS SERVFAIL）：代表帳戶、下單、取驗證都已經走通（DirectoryTrustPem 有效）
        var ok = Bool(tls, "lastOk") == false
            && (Str(tls, "lastMessage") ?? string.Empty).Contains("TLS-ALPN-01", StringComparison.Ordinal)
            && Bool(tls, "selfSigned") == true
            && Long(tls, "consecutiveFailures") == 1
            && backoffMinutes is > 55 and < 65
            && alerts.Contains("certificate_self_signed")
            && handshake.Ok && handshake.Thumbprint == selfSignedThumbprint;
        log.Step("第一次申請失敗：退避並繼續以自簽憑證服務", ok,
            $"lastMessage「{Str(tls, "lastMessage")}」；退避 {backoffMinutes:0.0} 分鐘；告警 {string.Join(",", alerts)}；新連線仍拿到自簽 {Short(handshake.Thumbprint)}",
            new { tls = Raw(tls), alerts, handshake = Describe(handshake) });
        return selfSignedThumbprint;
    }

    // ───── 4. 熱換前先開的連線 ─────

    private async Task OpenConnectionsAsync(string selfSigned, CancellationToken cancellationToken)
    {
        http1 = await KeepAliveHttp1.OpenAsync(ports.Hub, Domain, cancellationToken);
        var r1 = await http1.GetAsync("/healthz", cancellationToken);
        http2 = new Http2Session(ports.Hub, Domain);
        var r2 = await http2.GetAsync("/healthz", cancellationToken);
        var ok = r1.Status == 200 && r2.Status == 200 && r2.Version == "2.0"
            && http1.Certificate.Thumbprint == selfSigned && http2.Handshakes.SequenceEqual([selfSigned]);
        log.Step("熱換前先開兩條連線", ok,
            $"HTTP/1.1 keep-alive {r1.Status}、HTTP/2 {r2.Status}（{r2.Version}），兩條都拿到自簽 {Short(selfSigned)}",
            new { http1 = new { r1.Status, certificate = http1.Certificate.Thumbprint }, http2 = new { r2.Status, r2.Version, http2.Connects, handshakes = http2.Handshakes } });
    }

    // ───── 5. admin cert-renew：退避中立即重試、TLS-ALPN-01 取得並熱換 ─────

    private async Task<HandshakeResult> CertRenewAfterBackoffAsync(string selfSigned, CancellationToken cancellationToken)
    {
        var clear = await challControl!.ClearServFailAsync(Domain, cancellationToken);
        var validBefore = pebble!.CountLines(l => l.Contains("set VALID by completed challenge", StringComparison.Ordinal));
        var alpnBefore = CountDiag("MyriagentHub.Tls.TlsAlpnChallengeStore");
        var sw = Stopwatch.StartNew();
        var admin = await hub!.AdminAsync("cert-renew", 30, cancellationToken);
        var issued = await WaitForNewCertificateAsync(selfSigned, cancellationToken);
        var swapMs = sw.Elapsed.TotalMilliseconds;
        var ready = await hub.ReadyzAsync(cancellationToken);
        var tls = Tls(ready);
        log.Step("admin cert-renew：退避期間立即重新申請並熱換", admin.ExitCode == 0 && admin.Ok && issued is not null && clear == 200,
            issued is null
                ? $"cert-renew 結束碼 {admin.ExitCode}「{admin.Message}」，{options.Timeout.TotalSeconds:0} 秒內沒有換上新憑證"
                : $"cert-renew 結束碼 {admin.ExitCode}「{admin.Message}」；{swapMs:0} ms 後新連線拿到 {Short(issued.Thumbprint)}（{issued.Leaf!.Issuer}）",
            new { clearServFail = clear, admin, swapMs, tls = Raw(tls) });
        if (issued is null)
            throw new InvalidOperationException("沒有取得憑證，後續步驟無法進行");

        // 憑證本身：Pebble 簽發、伺服器有送中繼、用 Pebble 的根驗得過、ALPN 照常；驗證走 TLS-ALPN-01（同一個埠）
        var (valid, detail) = TlsProbe.ValidateChain(issued.Leaf!, issued.SentChain, pebbleRoot!, Domain);
        var alpnHandshakes = CountDiag("MyriagentHub.Tls.TlsAlpnChallengeStore") - alpnBefore;
        var pebbleValid = pebble.CountLines(l => l.Contains("set VALID by completed challenge", StringComparison.Ordinal)) - validBefore;
        var acmeOnly = await TlsProbe.HandshakeAsync(ports.Hub, Domain, ["acme-tls/1"], cancellationToken);
        var renewAt = Long(tls, "renewAt");
        var notBefore = new DateTimeOffset(issued.Leaf!.NotBefore.ToUniversalTime()).ToUnixTimeMilliseconds();
        var notAfter = new DateTimeOffset(issued.Leaf.NotAfter.ToUniversalTime()).ToUnixTimeMilliseconds();
        var twoThirds = notBefore + (notAfter - notBefore) * 2 / 3;
        var ok = valid
            && issued.SentChain.Count >= 1
            && issued.Leaf.Issuer.Contains("Pebble Intermediate CA", StringComparison.Ordinal)
            && issued.Alpn == "h2"
            && alpnHandshakes >= 1 && pebbleValid >= 1
            && Bool(tls, "lastOk") == true && Str(tls, "lastMessage") == "issued" && Bool(tls, "selfSigned") == false
            && Str(tls, "thumbprint") == issued.Thumbprint
            // Pebble 簽的憑證主體是空的（promote CN=false），顯示名稱要從 SAN 來
            && Str(tls, "name") == Domain
            && renewAt is { } r && Math.Abs(r - twoThirds) < 120_000
            && !acmeOnly.Ok
            // --staging：先在 staging 簽一次（驗證兩次）、記下 stagingVerifiedAt 才打正式目錄
            && (!options.Staging || (Long(tls, "stagingVerifiedAt") is not null && pebbleValid >= 2));
        log.Step("TLS-ALPN-01 取得的憑證", ok,
            $"SAN {string.Join(",", DnsNames(issued.Leaf))}（主體「{issued.Leaf.Subject}」，/readyz 的顯示名稱 {Str(tls, "name")}），簽發者 {issued.Leaf.Issuer}，效期到 {issued.Leaf.NotAfter:yyyy-MM-dd HH:mm}；ALPN {issued.Alpn}；伺服器送 {issued.SentChain.Count} 張中繼；"
            + $"Pebble 根驗證 {(valid ? "通過" : "不通過")}；伺服器記錄 TLS-ALPN-01 驗證交握 {alpnHandshakes} 次、Pebble 記錄驗證成功 {pebbleValid} 次；"
            + $"續期時點＝效期三分之二（ARI 還沒查）；沒有進行中的驗證時只提 acme-tls/1 被拒"
            + (options.Staging ? $"；staging 先成功（stagingVerifiedAt {Long(tls, "stagingVerifiedAt")}）才打正式目錄" : string.Empty),
            new
            {
                leaf = Describe(issued),
                chainValidation = detail,
                alpnHandshakes,
                pebbleValidations = pebbleValid,
                renewAt,
                twoThirds,
                acmeTlsOnlyWithoutChallenge = new { acmeOnly.Ok, acmeOnly.Error },
            });
        return issued;
    }

    // ───── 6. 熱換前的連線照常 ─────

    private async Task KeepAliveSurvivesAsync(string name, string selfSigned, CancellationToken cancellationToken)
    {
        var r1 = await http1!.GetAsync("/healthz", cancellationToken);
        var r2 = await http2!.GetAsync("/healthz", cancellationToken);
        var ok = r1.Status == 200 && r2.Status == 200
            && http1.Certificate.Thumbprint == selfSigned
            && http2.Connects == 1 && http2.Handshakes.Count == 1;
        log.Step(name, ok,
            $"HTTP/1.1 keep-alive 第 {http1.Requests} 個請求 {r1.Status}（同一個 TLS 工作階段，憑證仍是熱換前的 {Short(http1.Certificate.Thumbprint)}）；"
            + $"HTTP/2 {r2.Status}，TCP 連線 {http2.Connects} 條、TLS 交握 {http2.Handshakes.Count} 次",
            new { http1 = new { r1.Status, requests = http1.Requests }, http2 = new { r2.Status, r2.Version, http2.Connects, handshakes = http2.Handshakes } });
    }

    // ───── 7. ARI 要求提前續期 ─────

    private async Task<HandshakeResult> AriRenewalAsync(HandshakeResult current, string selfSigned, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var set = await pebbleControl!.SetRenewalInfoAsync(current.Leaf!, now.AddHours(-2), now.AddHours(-1), cancellationToken);
        var ariBefore = CountPebble("/renewalInfo/");
        var replacementBefore = CountPebble("is a replacement of");
        var markedBefore = CountPebble("has been marked as replaced");
        var sw = Stopwatch.StartNew();
        var admin = await hub!.AdminAsync("cert-renew", 30, cancellationToken);
        var renewed = await WaitForNewCertificateAsync(current.Thumbprint!, cancellationToken);
        var swapMs = sw.Elapsed.TotalMilliseconds;
        var ariQueries = CountPebble("/renewalInfo/") - ariBefore;
        var replacementLines = PebbleLines(l => l.Contains("is a replacement of", StringComparison.Ordinal)).Skip(replacementBefore).ToList();
        var marked = CountPebble("has been marked as replaced") - markedBefore;
        var tls = Tls(await hub.ReadyzAsync(cancellationToken));
        var valid = renewed is not null && TlsProbe.ValidateChain(renewed.Leaf!, renewed.SentChain, pebbleRoot!, Domain).Valid;
        var ok = set == 200 && admin.ExitCode == 0 && renewed is not null && valid
            && ariQueries >= 1 && replacementLines.Count >= 1 && marked >= 1
            && Str(tls, "thumbprint") == renewed.Thumbprint && Bool(tls, "lastOk") == true;
        log.Step("ARI：CA 要求提前續期 → cert-renew 帶 replaces 續期並熱換", ok,
            renewed is null
                ? $"Pebble ARI 覆寫 {set}、cert-renew 結束碼 {admin.ExitCode}；{options.Timeout.TotalSeconds:0} 秒內沒有換上新憑證（ARI 查詢 {ariQueries} 次）"
                : $"Pebble 把 {Short(current.Thumbprint)} 的 ARI 視窗改成一小時前；cert-renew 後 {swapMs:0} ms 換上 {Short(renewed.Thumbprint)}；"
                    + $"MyriagentHub 查了 ARI {ariQueries} 次，Pebble 記錄 replacement {replacementLines.Count} 筆、舊訂單標記已取代 {marked} 筆",
            new { setRenewalInfo = set, admin, swapMs, ariQueries, replacementLines, markedReplaced = marked, renewed = renewed is null ? null : Describe(renewed), tls = Raw(tls) });
        if (renewed is null)
            throw new InvalidOperationException("ARI 續期沒有發生，後續步驟無法進行");
        await KeepAliveSurvivesAsync("第二次熱換：最早開的連線仍然不斷", selfSigned, cancellationToken);
        return renewed;
    }

    // ───── 8. cert-renew 一律重查 ARI；--force 直接續期 ─────

    private async Task<HandshakeResult> CertRenewRecheckAndForceAsync(HandshakeResult current, CancellationToken cancellationToken)
    {
        // (a) 剛續期的憑證：Pebble 給正常的 ARI 視窗 → cert-renew 重查 ARI、視窗未到不續期
        var first = await CertRenewAsync(cancellationToken);
        var afterFirst = await TlsProbe.HandshakeAsync(ports.Hub, Domain, H2AndHttp11, cancellationToken);
        var firstOk = first.Admin.ExitCode == 0 && first.Completed && first.AriChecked && first.AriQueries >= 1
            && first.Action == "wait" && !first.Changed && afterFirst.Ok && afterFirst.Thumbprint == current.Thumbprint;
        log.Step("cert-renew 一律重查 ARI：視窗未到不續期", firstOk,
            $"cert-renew 結束碼 {first.Admin.ExitCode}「{first.Admin.Message}」；Pebble 收到 ARI 查詢 {first.AriQueries} 次、動作 {first.Action}；憑證仍是 {Short(afterFirst.Thumbprint)}",
            new { first, thumbprint = afterFirst.Thumbprint });

        // (b) CA 隨後把視窗改成過去（例如撤銷事件）：距上次查詢不到 12 小時，管理員馬上再下一次 cert-renew → 重查並帶 replaces 續期
        var now = DateTimeOffset.UtcNow;
        var set = await pebbleControl!.SetRenewalInfoAsync(current.Leaf!, now.AddHours(-2), now.AddHours(-1), cancellationToken);
        var replacementBefore = CountPebble("is a replacement of");
        var second = await CertRenewAsync(cancellationToken);
        var renewed = await WaitForNewCertificateAsync(current.Thumbprint!, cancellationToken);
        var replacements = CountPebble("is a replacement of") - replacementBefore;
        var secondOk = set == 200 && second.Admin.ExitCode == 0 && second.Completed && second.AriChecked && second.AriQueries >= 1
            && second.Action == "renew" && second.Changed && renewed is not null && renewed.Thumbprint == second.Thumbprint && replacements >= 1;
        log.Step("CA 改了 ARI 視窗後立刻 cert-renew：重查 ARI 並續期", secondOk,
            renewed is null
                ? $"Pebble ARI 覆寫 {set}、cert-renew 結束碼 {second.Admin.ExitCode}「{second.Admin.Message}」；{options.Timeout.TotalSeconds:0} 秒內沒有換上新憑證（ARI 查詢 {second.AriQueries} 次）"
                : $"Pebble 把 {Short(current.Thumbprint)} 的 ARI 視窗改成一小時前；馬上 cert-renew：ARI 查詢 {second.AriQueries} 次、動作 {second.Action}（{second.Reason}），換上 {Short(renewed.Thumbprint)}；Pebble 記錄 replacement {replacements} 筆",
            new { setRenewalInfo = set, second, renewed = renewed is null ? null : Describe(renewed), replacements });
        if (renewed is null)
            throw new InvalidOperationException("cert-renew 重查 ARI 後沒有續期，後續步驟無法進行");

        // (c) --force：新憑證的視窗還沒到，也直接續期
        var forced = await CertRenewAsync(cancellationToken, force: true);
        var afterForce = await WaitForNewCertificateAsync(renewed.Thumbprint!, cancellationToken);
        var forceOk = forced.Admin.ExitCode == 0 && forced.Completed && forced.Action == "renew" && forced.Reason == "forced" && forced.Changed
            && afterForce is not null && afterForce.Thumbprint == forced.Thumbprint
            && TlsProbe.ValidateChain(afterForce.Leaf!, afterForce.SentChain, pebbleRoot!, Domain).Valid;
        log.Step("cert-renew --force：不看續期時點直接續期", forceOk,
            afterForce is null
                ? $"cert-renew --force 結束碼 {forced.Admin.ExitCode}「{forced.Admin.Message}」；{options.Timeout.TotalSeconds:0} 秒內沒有換上新憑證"
                : $"cert-renew --force 結束碼 {forced.Admin.ExitCode}、動作 {forced.Action}（{forced.Reason}）；換上 {Short(afterForce.Thumbprint)}（{afterForce.Leaf!.Issuer}）",
            new { forced, renewed = afterForce is null ? null : Describe(afterForce) });
        return afterForce ?? renewed;
    }

    private sealed record CertRenewCheck(AdminCallResult Admin, int AriQueries, bool Completed, bool AriChecked, string? Action, string? Reason, bool Changed, string? Thumbprint);

    /// <summary>admin cert-renew（等那一輪檢查做完才回來）；從結果的 data 讀這一輪做了什麼，另外數 Pebble 收到的 ARI 查詢</summary>
    private async Task<CertRenewCheck> CertRenewAsync(CancellationToken cancellationToken, bool force = false)
    {
        var ariBefore = CountPebble("/renewalInfo/");
        var admin = force
            ? await hub!.AdminAsync("cert-renew", 30, cancellationToken, "--force")
            : await hub!.AdminAsync("cert-renew", 30, cancellationToken);
        var data = default(JsonElement);
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(admin.Output);
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
                data = d;
        }
        catch (JsonException)
        {
        }
        return new CertRenewCheck(admin, CountPebble("/renewalInfo/") - ariBefore, Bool(data, "completed") == true, Bool(data, "ariChecked") == true,
            Str(data, "action"), Str(data, "reason"), Bool(data, "changed") == true, Str(data, "thumbprint"));
    }

    // ───── 9. 重啟沿用已存的憑證 ─────

    private async Task RestartKeepsCertificateAsync(ChildProcessJob job, HandshakeResult current, CancellationToken cancellationToken)
    {
        await DisposeConnectionsAsync();
        var issuedBefore = CountPebble("Issued certificate serial");
        var stop = await hub!.StopAsync(TimeSpan.FromSeconds(30), cancellationToken);
        var startMs = await hub.StartAsync(job, options.Timeout, cancellationToken);
        var handshake = await TlsProbe.HandshakeAsync(ports.Hub, Domain, H2AndHttp11, cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        var issuedAfter = CountPebble("Issued certificate serial");
        var tls = Tls(await hub.ReadyzAsync(cancellationToken));
        var ok = stop.Graceful && handshake.Ok && handshake.Thumbprint == current.Thumbprint && issuedAfter == issuedBefore
            && Str(tls, "thumbprint") == current.Thumbprint && Bool(tls, "selfSigned") == false;
        log.Step("重啟後以指紋載入已存的憑證、不重新申請", ok,
            $"admin stop {(stop.Graceful ? "正常結束" : "逾時被強制結束")}（{stop.ElapsedMs:0} ms）；重啟 {startMs:0} ms 後新連線拿到 {Short(handshake.Thumbprint)}；"
            + $"Pebble 簽發數 {issuedBefore} → {issuedAfter}",
            new { stop = new { stop.Graceful, stop.ElapsedMs, stop.Admin }, startMs, handshake = Describe(handshake), issuedBefore, issuedAfter, tls = Raw(tls) });
    }

    // ───── 10. 收尾 ─────

    private async Task ShutdownAsync(
        IReadOnlyDictionary<string, KeyFileEntry> keysBefore,
        IReadOnlyList<StoreCertificate> userCaBefore,
        IReadOnlyList<StoreCertificate> machineCaBefore)
    {
        await DisposeConnectionsAsync();
        if (hub is { IsRunning: true })
        {
            try
            {
                var stop = await hub.StopAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                log.Step("停止 MyriagentHub", stop.Graceful,
                    stop.Graceful ? $"admin stop 正常結束（{stop.ElapsedMs:0} ms）" : "admin stop 逾時，已強制結束（file 模式會留下孤兒金鑰檔）",
                    new { stop.Graceful, stop.ElapsedMs, stop.Admin });
            }
            catch (Exception ex)
            {
                log.Step("停止 MyriagentHub", false, TlsProbe.Describe(ex));
            }
        }
        foreach (var process in new[] { pebble, challtestsrv })
        {
            if (process is null)
                continue;
            process.Kill();
            await process.WaitForExitAsync(TimeSpan.FromSeconds(10));
            process.Dispose();
        }
        hub?.Process?.Dispose();
        pebbleControl?.Dispose();
        challControl?.Dispose();

        // 主機副作用：剛停的程序可能還在釋放金鑰檔，稍等一下再比
        await Task.Delay(TimeSpan.FromSeconds(1));
        var newKeys = HostSideEffects.NewKeyFiles(keysBefore);
        var removed = HostSideEffects.RemoveNewPebbleCertificates(userCaBefore);
        var userCaAfter = HostSideEffects.SnapshotStore(StoreName.CertificateAuthority, StoreLocation.CurrentUser);
        var machineCaAfter = HostSideEffects.SnapshotStore(StoreName.CertificateAuthority, StoreLocation.LocalMachine);
        var machineNew = machineCaAfter.Where(c => machineCaBefore.All(b => b.Thumbprint != c.Thumbprint)).ToList();
        var userLeft = HostSideEffects.PebbleCertificates(userCaAfter).Where(c => userCaBefore.All(b => b.Thumbprint != c.Thumbprint)).ToList();
        log.Step("主機副作用（孤兒金鑰檔、CA 存放區）", newKeys.Count == 0 && machineNew.Count == 0 && userLeft.Count == 0,
            $"新增且仍在的金鑰檔 {newKeys.Count} 個；MyriagentHub 寫進 CurrentUser\\CA 的 Pebble 中繼憑證 {removed.Count} 張已移除；"
            + $"LocalMachine\\CA 新增 {machineNew.Count} 張；CurrentUser\\CA {userCaBefore.Count} → {userCaAfter.Count} 張",
            new { newKeyFiles = newKeys, removedFromCurrentUserCa = removed, newInLocalMachineCa = machineNew, pebbleLeftInCurrentUserCa = userLeft });
    }

    private async Task DisposeConnectionsAsync()
    {
        if (http1 is not null)
        {
            await http1.DisposeAsync();
            http1 = null;
        }
        http2?.Dispose();
        http2 = null;
    }

    // ───── 工具 ─────

    /// <summary>每 250 ms 開一條新連線看憑證，直到指紋和 previous 不同（逾時回 null）</summary>
    private async Task<HandshakeResult?> WaitForNewCertificateAsync(string previous, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + options.Timeout;
        while (DateTime.UtcNow < deadline)
        {
            var h = await TlsProbe.HandshakeAsync(ports.Hub, Domain, H2AndHttp11, cancellationToken);
            if (h.Ok && h.Thumbprint is not null && !h.Thumbprint.Equals(previous, StringComparison.OrdinalIgnoreCase))
                return h;
            await Task.Delay(250, cancellationToken);
        }
        return null;
    }

    private async Task<JsonElement?> WaitReadyzAsync(Func<JsonElement, bool> predicate, TimeSpan interval, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? options.Timeout);
        while (DateTime.UtcNow < deadline)
        {
            var ready = await hub!.ReadyzAsync(cancellationToken);
            if (predicate(ready))
                return ready;
            await Task.Delay(interval, cancellationToken);
        }
        return null;
    }

    private int CountDiag(string category) =>
        hub!.ReadDiagLog().Count(e => e.TryGetProperty("c", out var c) && c.GetString() == category);

    private int CountPebble(string text) => pebble!.CountLines(l => l.Contains(text, StringComparison.Ordinal));

    private IEnumerable<string> PebbleLines(Func<string, bool> predicate) => pebble!.Lines.Where(predicate);

    private static JsonElement Tls(JsonElement ready) => ready.GetProperty("tls");

    private static object? Raw(JsonElement element) => element.ValueKind == JsonValueKind.Undefined ? null : JsonSerializer.Deserialize<object>(element.GetRawText());

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static string Short(string? thumbprint) => thumbprint is null ? "（無）" : thumbprint[..Math.Min(12, thumbprint.Length)];

    private static object Describe(HandshakeResult h) => new
    {
        h.Ok,
        h.Alpn,
        h.Error,
        h.ElapsedMs,
        thumbprint = h.Thumbprint,
        subject = h.Leaf?.Subject,
        issuer = h.Leaf?.Issuer,
        notBefore = h.Leaf?.NotBefore.ToUniversalTime(),
        notAfter = h.Leaf?.NotAfter.ToUniversalTime(),
        dnsNames = h.Leaf is null ? null : DnsNames(h.Leaf),
        sentChain = h.SentChain.Select(c => new { c.Subject, c.Issuer, c.Thumbprint }),
    };

    private static List<string> DnsNames(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().SelectMany(e => e.EnumerateDnsNames()).ToList();
}
