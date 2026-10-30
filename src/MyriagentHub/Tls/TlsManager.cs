using System.Security.Cryptography.X509Certificates;
using MyriagentHub.Audit;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Tls;

/// <summary>
/// ops/status 的 TLS 摘要。Name 是顯示名稱：主體的 CN，沒有 CN（Let's Encrypt 的短效憑證、Pebble）時用 SAN 的 DNS 名稱；
/// Subject 照原樣（可能是空字串）。
/// </summary>
internal sealed record TlsSnapshot(
    string? Thumbprint,
    string? Subject,
    string? Name,
    IReadOnlyList<string> DnsNames,
    string? Issuer,
    long? NotAfter,
    bool SelfSigned,
    string Expiry,
    string Storage,
    bool AcmeEnabled,
    long? StagingVerifiedAt,
    long? LastAttemptAt,
    bool? LastOk,
    string? LastMessage,
    int ConsecutiveFailures,
    long? NextCheckAt,
    long? RenewAt);

/// <summary>
/// TLS 憑證的生命週期（9.6）：啟動時載入正式憑證（沒有就用自簽）；ACME 啟用時 staging 成功才切正式目錄、
/// ARI／效期三分之二續期、失敗退避並保留最後一張有效憑證繼續服務；換下來的舊憑證 24 小時後刪除。
/// </summary>
internal sealed class TlsManager(
    CertificateHolder holder,
    ICertificateStorage storage,
    AcmeService acme,
    KvStore kv,
    AuditLog audit,
    TimeProvider time,
    IOptions<HubOptions> options,
    ILogger<TlsManager> log)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private TlsState state = new();
    private long? nextCheckAt;

    private HubOptions Options => options.Value;

    private long Now => time.GetUtcNow().ToUnixTimeMilliseconds();

    public TlsState State => state;

    /// <summary>啟動步驟：開始聽連接埠之前放好第一張憑證</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        state = await kv.GetAsync<TlsState>(KvKeys.AcmeState, cancellationToken) ?? new TlsState();
        var domain = Options.ServerDomain;
        if (state.CurrentThumbprint is { } thumbprint && string.Equals(state.Domain, domain, StringComparison.OrdinalIgnoreCase))
        {
            var certificate = storage.Load(thumbprint);
            if (certificate is not null && certificate.NotAfter.ToUniversalTime() > time.GetUtcNow().UtcDateTime)
            {
                holder.Swap(certificate, TlsCertificates.ParseChain(state.CurrentChainPem), selfSigned: false);
                log.LogInformation("載入正式 TLS 憑證 {Thumbprint}（{Name}，{Issuer}，到期 {NotAfter:O}）", certificate.Thumbprint, TlsCertificates.DisplayName(certificate), certificate.Issuer, certificate.NotAfter);
                return;
            }
            certificate?.Dispose();
            log.LogWarning("正式 TLS 憑證 {Thumbprint} 載入不到或已過期，先用自簽憑證並重新申請", thumbprint);
            state = state with
            {
                Retired = [.. state.Retired, new RetiredCertificate(thumbprint, Now)],
                CurrentThumbprint = null,
                CurrentChainPem = null,
                CurrentAriId = null,
                NotBefore = null,
                NotAfter = null,
            };
        }
        else if (state.CurrentThumbprint is not null)
        {
            log.LogWarning("網域從 {Old} 改成 {New}：舊憑證不適用，先用自簽憑證並重新申請", state.Domain, domain);
            state = state with
            {
                Retired = [.. state.Retired, new RetiredCertificate(state.CurrentThumbprint, Now)],
                CurrentThumbprint = null,
                CurrentChainPem = null,
                CurrentAriId = null,
                NotBefore = null,
                NotAfter = null,
                StagingVerifiedAt = null,
            };
        }
        UseSelfSigned(domain);
        await SaveAsync(cancellationToken);
    }

    /// <summary>
    /// 續期迴圈的一次檢查。手動（admin cert-renew）時清掉失敗退避並強制重查 ARI；ForceRenew（--force）時不看續期時點直接續期。
    /// 回傳這一輪做了什麼與下次該檢查的時間。
    /// </summary>
    public async Task<TlsTickReport> TickAsync(TlsTickRequest request, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var now = Now;
            if (CleanupRetired(now))
                await SaveAsync(cancellationToken);
            var checkInterval = Options.Acme.CheckIntervalHours * AcmeRules.HourMs;
            if (!Options.Acme.Enabled)
            {
                nextCheckAt = now + checkInterval;
                return Report(acmeEnabled: false, ariChecked: false, new AcmePlan(AcmeAction.Wait, nextCheckAt.Value, "acme-disabled"), changed: false);
            }

            var ariChecked = AcmeRules.ShouldCheckAri(state, now, checkInterval, request.Manual);
            if (ariChecked)
                await RefreshAriAsync(now, cancellationToken);

            var before = state.CurrentThumbprint;
            var plan = AcmeRules.Plan(request.Manual ? state with { NextAttemptAt = null } : state, now, Options.Acme.SkipStaging, checkInterval, request.ForceRenew);
            switch (plan.Action)
            {
                case AcmeAction.IssueStaging:
                    if (await TryIssueAsync(staging: true, cancellationToken))
                        await TryIssueAsync(staging: false, cancellationToken);
                    break;
                case AcmeAction.IssueProduction:
                case AcmeAction.Renew:
                    await TryIssueAsync(staging: false, cancellationToken);
                    break;
            }

            var after = AcmeRules.Plan(state, Now, Options.Acme.SkipStaging, checkInterval);
            nextCheckAt = after.Action == AcmeAction.Wait ? after.NextCheckAt : Now + AcmeRules.HourMs;
            return Report(acmeEnabled: true, ariChecked, plan, changed: !string.Equals(before, state.CurrentThumbprint, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            gate.Release();
        }
    }

    private TlsTickReport Report(bool acmeEnabled, bool ariChecked, AcmePlan plan, bool changed) =>
        new(acmeEnabled, ariChecked, plan.Action.ToString().ToLowerInvariant(), plan.Reason, changed, holder.Current?.Certificate.Thumbprint,
            state.CurrentThumbprint is null ? null : AcmeRules.RenewAt(state), state.LastOk, state.LastMessage, nextCheckAt ?? Now);

    public TlsSnapshot Snapshot()
    {
        var entry = holder.Current;
        var notAfter = entry is null ? (long?)null : new DateTimeOffset(entry.Certificate.NotAfter.ToUniversalTime()).ToUnixTimeMilliseconds();
        return new TlsSnapshot(
            entry?.Certificate.Thumbprint,
            entry?.Certificate.Subject,
            entry is null ? null : TlsCertificates.DisplayName(entry.Certificate),
            entry is null ? [] : TlsCertificates.DnsNames(entry.Certificate),
            entry?.Certificate.Issuer,
            notAfter,
            entry?.SelfSigned ?? true,
            notAfter is null ? "none" : AcmeRules.Expiry(notAfter.Value, Now).ToString().ToLowerInvariant(),
            storage.Kind,
            Options.Acme.Enabled,
            state.StagingVerifiedAt,
            state.LastAttemptAt,
            state.LastOk,
            state.LastMessage,
            state.ConsecutiveFailures,
            nextCheckAt,
            state.CurrentThumbprint is null ? null : AcmeRules.RenewAt(state));
    }

    private void UseSelfSigned(string domain)
    {
        var now = Now;
        if (state.SelfSignedThumbprint is { } existing && !AcmeRules.SelfSignedNeedsRenewal(state.SelfSignedNotAfter, now))
        {
            var reused = storage.Load(existing);
            if (reused is not null)
            {
                holder.Swap(reused, null, selfSigned: true);
                log.LogInformation("沿用自簽 TLS 憑證 {Thumbprint}（還沒有正式憑證）", reused.Thumbprint);
                return;
            }
        }
        var lifetime = TimeSpan.FromDays(Options.Tls.SelfSignedDays);
        // 自簽憑證也只匯入一次：記下指紋，之後的啟動沿用（不會每次啟動留下一個新金鑰）
        var certificate = storage.Import(TlsCertificates.CreateSelfSignedPfx(domain, lifetime, time.GetUtcNow()));
        var retired = state.SelfSignedThumbprint is { } old ? [.. state.Retired, new RetiredCertificate(old, now)] : state.Retired;
        state = state with
        {
            SelfSignedThumbprint = certificate.Thumbprint,
            SelfSignedNotAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime()).ToUnixTimeMilliseconds(),
            Retired = retired,
        };
        holder.Swap(certificate, null, selfSigned: true);
        log.LogWarning("使用自簽 TLS 憑證 {Thumbprint}（{Domain}）：還沒有正式憑證", certificate.Thumbprint, domain);
    }

    private async Task<bool> TryIssueAsync(bool staging, CancellationToken cancellationToken)
    {
        var directory = staging ? Options.Acme.StagingDirectory : Options.Acme.Directory;
        var domain = Options.ServerDomain;
        var now = Now;
        try
        {
            var issued = await acme.IssueAsync(directory, domain, staging ? null : state.CurrentAriId, cancellationToken);
            if (staging)
            {
                state = state with { StagingVerifiedAt = Now, LastAttemptAt = now, LastOk = true, LastMessage = "staging ok", ConsecutiveFailures = 0, NextAttemptAt = null };
                await SaveAsync(cancellationToken);
                await audit.WriteAsync(new AuditEntry(AuditActors.System, null, null, AuditActions.Certificate, domain, AuditOutcomes.Ok,
                    new { stage = "staging", issuer = issued.Issuer, ms = issued.ElapsedMs }), cancellationToken);
                return true;
            }

            var certificate = storage.Import(issued.Pfx);
            var previous = holder.Swap(certificate, TlsCertificates.ParseChain(issued.ChainPem), selfSigned: false);
            var retired = state.Retired.ToList();
            if (previous is not null)
                retired.Add(new RetiredCertificate(previous.Certificate.Thumbprint, Now));
            var retiredSelfSigned = previous is { SelfSigned: true } && previous.Certificate.Thumbprint == state.SelfSignedThumbprint;
            state = state with
            {
                SelfSignedThumbprint = retiredSelfSigned ? null : state.SelfSignedThumbprint,
                SelfSignedNotAfter = retiredSelfSigned ? null : state.SelfSignedNotAfter,
                CurrentThumbprint = issued.Thumbprint,
                CurrentChainPem = issued.ChainPem,
                CurrentAriId = issued.AriId,
                NotBefore = issued.NotBefore,
                NotAfter = issued.NotAfter,
                Issuer = issued.Issuer,
                Domain = domain,
                Directory = directory,
                LastAttemptAt = now,
                LastOk = true,
                LastMessage = "issued",
                ConsecutiveFailures = 0,
                NextAttemptAt = null,
                AriWindowStart = null,
                AriWindowEnd = null,
                AriCheckedAt = null,
                AriRetryAfterMs = null,
                Retired = retired,
            };
            await SaveAsync(cancellationToken);
            await audit.WriteAsync(new AuditEntry(AuditActors.System, null, null, AuditActions.Certificate, domain, AuditOutcomes.Ok,
                new { stage = "production", issuer = issued.Issuer, notAfter = issued.NotAfter, thumbprint = issued.Thumbprint, ms = issued.ElapsedMs }), cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "ACME {Stage} 失敗", staging ? "staging" : "正式");
            state = AcmeRules.Failed(state, now, $"{(staging ? "staging" : "production")}: {ex.GetType().Name}: {ex.Message}");
            await SaveAsync(cancellationToken);
            await audit.WriteAsync(new AuditEntry(AuditActors.System, null, null, AuditActions.Certificate, domain, AuditOutcomes.Error,
                new { stage = staging ? "staging" : "production", error = ex.GetType().Name, failures = state.ConsecutiveFailures }), cancellationToken);
            return false;
        }
    }

    private async Task RefreshAriAsync(long now, CancellationToken cancellationToken)
    {
        try
        {
            var window = await acme.GetRenewalWindowAsync(state.Directory ?? Options.Acme.Directory, state.CurrentAriId!, cancellationToken);
            state = window is { } w
                ? state with { AriWindowStart = w.Start, AriWindowEnd = w.End, AriRetryAfterMs = w.RetryAfterMs, AriCheckedAt = now }
                : state with { AriCheckedAt = now };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ARI 不可用時退回效期三分之二
            log.LogInformation("ARI 查詢失敗，改用效期三分之二續期：{Type} {Message}", ex.GetType().Name, ex.Message);
            state = state with { AriCheckedAt = now };
        }
        await SaveAsync(cancellationToken);
    }

    private bool CleanupRetired(long now)
    {
        var due = AcmeRules.DueForRemoval(state.Retired, now);
        if (due.Count == 0)
            return false;
        foreach (var item in due)
        {
            if (item.Thumbprint.Equals(holder.Current?.Certificate.Thumbprint, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                storage.Remove(item.Thumbprint);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                log.LogWarning(ex, "刪除舊憑證失敗：{Thumbprint}", item.Thumbprint);
            }
        }
        state = state with { Retired = state.Retired.Except(due).ToList() };
        return true;
    }

    private Task SaveAsync(CancellationToken cancellationToken) => kv.SetAsync(KvKeys.AcmeState, state, cancellationToken);
}

/// <summary>一輪續期檢查的要求：Manual＝admin cert-renew（清掉失敗退避、強制重查 ARI）；ForceRenew＝cert-renew --force（直接續期）</summary>
internal sealed record TlsTickRequest(bool Manual, bool ForceRenew)
{
    public static readonly TlsTickRequest Scheduled = new(false, false);
}

/// <summary>一輪續期檢查的結果（admin cert-renew 回給管理員；Action 是 wait／issuestaging／issueproduction／renew）</summary>
internal sealed record TlsTickReport(
    bool AcmeEnabled,
    bool AriChecked,
    string Action,
    string Reason,
    bool Changed,
    string? Thumbprint,
    long? RenewAt,
    bool? LastOk,
    string? LastMessage,
    long NextCheckAt);

/// <summary>續期迴圈：每 12 小時（或下次續期時點、退避到期）檢查一次；管理收件匣的 cert-renew 立即觸發並等那一輪的結果</summary>
internal sealed class AcmeRenewalService(TlsManager manager, TimeProvider time, ILogger<AcmeRenewalService> log) : BackgroundService
{
    private readonly SemaphoreSlim trigger = new(0, 1);
    private readonly object gate = new();
    private TaskCompletionSource<TlsTickReport>? pending;
    private bool pendingForceRenew;

    /// <summary>
    /// 立即做一輪手動檢查（清掉失敗退避、強制重查 ARI；<paramref name="forceRenew"/> 時直接續期）。
    /// 還沒輪到的手動要求會合併成同一輪（--force 只要有一個要求就算）；回傳那一輪的結果，等多久由呼叫端決定。
    /// </summary>
    public Task<TlsTickReport> TriggerNow(bool forceRenew = false)
    {
        Task<TlsTickReport> task;
        lock (gate)
        {
            pending ??= new TaskCompletionSource<TlsTickReport>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingForceRenew |= forceRenew;
            task = pending.Task;
        }
        if (trigger.CurrentCount == 0)
        {
            try
            {
                trigger.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
        return task;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TaskCompletionSource<TlsTickReport>? manual;
            bool forceRenew;
            lock (gate)
            {
                manual = pending;
                forceRenew = pendingForceRenew;
                pending = null;
                pendingForceRenew = false;
            }
            long next;
            try
            {
                var report = await manager.TickAsync(manual is null ? TlsTickRequest.Scheduled : new TlsTickRequest(true, forceRenew), stoppingToken);
                manual?.TrySetResult(report);
                next = report.NextCheckAt;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                manual?.TrySetCanceled(stoppingToken);
                CancelPending();
                return;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "TLS 續期檢查失敗");
                manual?.TrySetException(ex);
                next = time.GetUtcNow().ToUnixTimeMilliseconds() + AcmeRules.HourMs;
            }
            var delay = TimeSpan.FromMilliseconds(Math.Clamp(next - time.GetUtcNow().ToUnixTimeMilliseconds(), 1_000, 12 * AcmeRules.HourMs));
            try
            {
                await trigger.WaitAsync(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                CancelPending();
                return;
            }
        }
        CancelPending();
    }

    /// <summary>服務停止時還沒輪到的手動要求一律取消（不讓 admin 指令空等）</summary>
    private void CancelPending()
    {
        lock (gate)
        {
            pending?.TrySetCanceled();
            pending = null;
            pendingForceRenew = false;
        }
    }
}

internal static class TlsServiceCollectionExtensions
{
    public static IServiceCollection AddHubTls(this IServiceCollection services, bool useCertificateStore)
    {
        services.AddSingleton<CertificateHolder>();
        services.AddSingleton<TlsAlpnChallengeStore>();
        if (useCertificateStore)
            services.AddSingleton<ICertificateStorage, WindowsStoreCertificateStorage>();
        else
            services.AddSingleton<ICertificateStorage, FileCertificateStorage>();
        services.AddSingleton<AcmeService>();
        services.AddSingleton<TlsManager>();
        services.AddSingleton<AcmeRenewalService>();
        services.AddHostedService(sp => sp.GetRequiredService<AcmeRenewalService>());
        services.AddSingleton<IStartupTask>(sp => new StartupTask(StartupOrder.Tls, "TLS 憑證", ct => sp.GetRequiredService<TlsManager>().InitializeAsync(ct)));
        return services;
    }
}
