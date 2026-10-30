using System.Globalization;
using System.Text.Json;
using Myriagent.Update;
using MyriagentHub.Audit;
using MyriagentHub.Auth;
using MyriagentHub.Hosting;
using MyriagentHub.Realtime;
using MyriagentHub.Releases;
using MyriagentHub.Tls;
using MyriagentHub.Trust;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Ops;

/// <summary>
/// 收件匣裡的一個指令檔（admin-inbox\時間-id.json）。<see cref="Force"/>：<c>cert-renew --force</c>（不看續期時點直接續期）；
/// <see cref="RetryAfterSec"/>：drain 時給裝置的重連建議（部署的 drain 帶來的值，沒有就用預設）；
/// <see cref="Release"/>：<c>app-release-withdraw</c> 要撤回的版本（只有這個指令能帶，<see cref="AdminInboxRules.CheckRelease"/>）。
/// </summary>
internal sealed record AdminCommand(string Id, string Command, long RequestedAt, bool Force = false, int? RetryAfterSec = null, string? Release = null);

/// <summary>結果檔（admin-inbox\results\id.json）</summary>
internal sealed record AdminResult(string Id, string Command, bool Ok, string Message, object? Data, long ProcessedAt);

/// <summary>部署收件匣（admin-inbox\deploy\）的 drain 要求：安裝腳本以 runner 帳戶寫入（docs/hub-setup.md 第 11 節）</summary>
internal sealed record DeployRequest(string Id, string? Release, int RetryAfterSec, long RequestedAt);

/// <summary>status\drain.json：部署的 drain 處理完的回覆（安裝腳本比對 id）</summary>
internal sealed record DrainAck(string Id, bool Ok, string State, string? Release, int RetryAfterSec, long RequestedAt, long ProcessedAt, string Message);

/// <summary>
/// 這個服務程序的開始時間：牆上時鐘的 Unix 毫秒（要求檔的 requestedAt 是另一個程序以牆上時鐘寫的，不用 TimeProvider），
/// 在 HubApplication.Build 登記服務時記下——早於 SCM 回報 Running，而安裝腳本看到 Running 才寫部署的 drain。
/// </summary>
internal sealed record HubInstance(long StartedAtMs);

/// <summary>
/// 管理收件匣的純函式規則（8.3：管理指令沒有任何網路端點）。
/// <list type="bullet">
/// <item><c>admin-inbox\</c>：所有管理指令；資料根的 ACL 只給 Administrators、SYSTEM 與服務帳戶，所以只有本機管理員能寫入。</item>
/// <item><c>admin-inbox\deploy\</c>：runner 帳戶唯一能寫的子資料夾，只接受 drain（其他指令一律拒絕並記稽核）；
/// 處理完寫 <c>status\drain.json</c> 回 id。</item>
/// </list>
/// </summary>
internal static class AdminInboxRules
{
    public const int MaxFileBytes = 64 * 1024;

    /// <summary>drain 時給裝置的重連建議（秒）；部署的 drain 可以帶自己的值</summary>
    public const int DefaultRetryAfterSec = 30;

    /// <summary>收件匣接受的指令（owner-reset 是設計文件的寫法，等同 reset-owner）</summary>
    public static readonly IReadOnlyList<string> Commands =
    [
        "reset-owner", "drain", "undrain", "backup-now", "selftest", "cert-renew", "verify-audit", "stop", "rotate-hub-identity",
        "app-releases", "app-release-withdraw", "app-devices",
    ];

    /// <summary>
    /// 暫時性指令（排空、停止、備份…）寫進收件匣超過這個時間還沒處理就作廢（沒有人在等結果了）；這個時間之內的照樣執行，
    /// 所以「服務停著時下的指令不在下次啟動才生效」另外靠：admin CLI 等不到結果時收回自己的指令檔，安裝腳本等不到 drain 的回覆時
    /// 也收回，部署收件匣的 drain 只執行本程序啟動之後寫的（<see cref="IsFromEarlierInstance"/>）。
    /// reset-owner、rotate-hub-identity、app-release-withdraw 是刻意的管理動作（不是暫時性指令）：CLI 逾時時不收回，服務啟動後照樣處理。
    /// </summary>
    public static readonly TimeSpan TransientCommandTtl = TimeSpan.FromMinutes(10);

    public static bool IsTransient(string command) => command is not ("reset-owner" or "rotate-hub-identity" or "app-release-withdraw");

    /// <summary>以牆上時鐘比較（指令檔由另一個程序寫入）；requestedAt 在未來太多也當作過期</summary>
    public static bool IsExpired(AdminCommand command, DateTimeOffset now) =>
        IsTransient(command.Command)
        && (now.ToUnixTimeMilliseconds() - command.RequestedAt > (long)TransientCommandTtl.TotalMilliseconds
            || command.RequestedAt - now.ToUnixTimeMilliseconds() > (long)TransientCommandTtl.TotalMilliseconds);

    /// <summary>
    /// 部署的 drain 是本程序啟動之前寫的：那是給上一個程序的（安裝腳本等不到回覆就停了服務，要求留在收件匣），
    /// 剛部署好的新版啟動時執行它就會一啟動就排空，而部署的健康閘門不看排空。
    /// </summary>
    public static bool IsFromEarlierInstance(DeployRequest request, long instanceStartedAtMs) => request.RequestedAt < instanceStartedAtMs;

    public static string? Canonical(string? command)
    {
        var c = (command ?? string.Empty).Trim().ToLowerInvariant();
        if (c == "owner-reset")
            c = "reset-owner";
        return Commands.Contains(c) ? c : null;
    }

    public static bool IsCommandFile(string fileName) =>
        fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !fileName.StartsWith('.') && !fileName.Contains(".tmp", StringComparison.OrdinalIgnoreCase);

    public static string FileName(DateTimeOffset now, string id) =>
        now.UtcDateTime.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + "-" + id + ".json";

    /// <summary>id 只允許英數與「-」（結果檔名用它，不能有路徑字元）</summary>
    public static bool IsValidId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    /// <summary>
    /// 部署要求的 id：安裝腳本寫 drain-&lt;release&gt;-&lt;yyyyMMddHHmmss&gt;（release 是 0.2.0-1a2b3c4，含「.」）。
    /// 只寫進 status\drain.json、不當檔名，仍然限制字元與長度（記錄與稽核乾淨）。
    /// </summary>
    public static bool IsValidDeployId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 128 && !id.Contains("..", StringComparison.Ordinal)
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_');

    public static AdminCommand? Parse(string json, out string? error)
    {
        error = null;
        try
        {
            var command = JsonSerializer.Deserialize<AdminCommand>(json, StatusJson.Options);
            if (command is null || !IsValidId(command.Id))
            {
                error = "id 不合法";
                return null;
            }
            if (Canonical(command.Command) is not { } canonical)
            {
                error = "不認得的指令：" + command.Command;
                return command;
            }
            command = command with { Command = canonical };
            error = CheckRelease(command);
            return command;
        }
        catch (JsonException ex)
        {
            error = "JSON 格式錯誤：" + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// release 參數：<c>app-release-withdraw</c> 一定要帶標準寫法的 release（<see cref="ReleaseIdentity.TryParseRelease"/>），其他指令一律不能帶。
    /// 不合格回原因（指令不執行，結果檔寫失敗）
    /// </summary>
    public static string? CheckRelease(AdminCommand command)
    {
        if (command.Command == "app-release-withdraw")
            return ReleaseIdentity.TryParseRelease(command.Release, out _) ? null : "app-release-withdraw 要帶標準寫法的 release（例如 0.2.1523-ab12cd3）";
        return command.Release is null ? null : "release 只能用在 app-release-withdraw";
    }

    /// <summary>
    /// 解析部署收件匣的要求 <c>{"command":"drain","id":…,"release":…,"retryAfterSec":30,"requestedAt":…}</c>（屬性名稱不分大小寫）。
    /// 只接受 drain；requestedAt 可以是 Unix 毫秒或 ISO 8601（安裝腳本寫 <c>(Get-Date).ToUniversalTime().ToString('o')</c>）。
    /// 不接受時回 null 並說明原因。
    /// </summary>
    public static DeployRequest? ParseDeploy(string json, out string? error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "不是 JSON 物件";
                return null;
            }
            var command = (Property(root, "command") is { ValueKind: JsonValueKind.String } c ? c.GetString() : null)?.Trim().ToLowerInvariant();
            if (command != "drain")
            {
                error = "部署收件匣只接受 drain：" + (command ?? "（沒有 command）");
                return null;
            }
            var id = Property(root, "id") is { ValueKind: JsonValueKind.String } i ? i.GetString() : null;
            if (!IsValidDeployId(id))
            {
                error = "id 不合法";
                return null;
            }
            if (!TryReadTime(Property(root, "requestedAt"), out var requestedAt))
            {
                error = "requestedAt 缺少或格式不對（要 Unix 毫秒或 ISO 8601）";
                return null;
            }
            var retryAfter = Property(root, "retryAfterSec") is { ValueKind: JsonValueKind.Number } r && r.TryGetInt32(out var seconds)
                ? Math.Clamp(seconds, 1, 3600)
                : DefaultRetryAfterSec;
            var release = Property(root, "release") is { ValueKind: JsonValueKind.String } rel ? rel.GetString() : null;
            if (release is { Length: > 80 })
                release = release[..80];
            return new DeployRequest(id!, release, retryAfter, requestedAt);
        }
        catch (JsonException ex)
        {
            error = "JSON 格式錯誤：" + ex.Message;
            return null;
        }
    }

    private static JsonElement? Property(JsonElement obj, string name)
    {
        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value;
        }
        return null;
    }

    private static bool TryReadTime(JsonElement? value, out long unixMs)
    {
        unixMs = 0;
        switch (value)
        {
            case { ValueKind: JsonValueKind.Number } n when n.TryGetInt64(out var ms):
                unixMs = ms;
                return true;
            case { ValueKind: JsonValueKind.String } s
                when DateTimeOffset.TryParse(s.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at):
                unixMs = at.ToUnixTimeMilliseconds();
                return true;
            default:
                return false;
        }
    }
}

/// <summary>執行收件匣指令</summary>
internal sealed class AdminCommandExecutor(
    OwnerAccountService ownerAccount,
    BackupService backup,
    IsolationSelfTest selfTest,
    AcmeRenewalService renewal,
    AuditLog audit,
    DrainState drain,
    HubIdentity identity,
    TrustService trust,
    ConnectionRegistry connections,
    AppReleaseStore releases,
    AppReleaseDeviceStates devices,
    IHostApplicationLifetime lifetime,
    TimeProvider time,
    IOptions<HubOptions> options)
{
    /// <summary>cert-renew 等這一輪憑證檢查做完的上限（admin 指令預設等 30 秒）；超過就回「已觸發」，結果看 /readyz</summary>
    public static readonly TimeSpan CertRenewWait = TimeSpan.FromSeconds(20);

    public async Task<AdminResult> ExecuteAsync(AdminCommand command, CancellationToken cancellationToken, string actor = AuditActors.Admin)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        AdminResult result;
        try
        {
            result = command.Command switch
            {
                "reset-owner" => Ok(command, "擁有者密碼已改回預設，所有瀏覽器工作階段已撤銷", await ownerAccount.ResetAsync(cancellationToken)),
                "drain" => Drain(command, now, actor),
                "undrain" => Undrain(command),
                "backup-now" => await BackupAsync(command, cancellationToken),
                "selftest" => await SelfTestAsync(command, cancellationToken),
                "cert-renew" => await CertRenewAsync(command, cancellationToken),
                "verify-audit" => await VerifyAuditAsync(command, cancellationToken),
                "stop" => Stop(command),
                "rotate-hub-identity" => await RotateHubIdentityAsync(command, cancellationToken),
                "app-releases" => await AppReleasesAsync(command, cancellationToken),
                "app-devices" => AppDevices(command),
                "app-release-withdraw" => await AppReleaseWithdrawAsync(command, actor),
                _ => Fail(command, "不認得的指令"),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = Fail(command, ex.GetType().Name + ": " + ex.Message);
        }
        await audit.WriteAsync(new AuditEntry(actor, null, null, AuditActions.AdminCommand, command.Command, result.Ok ? AuditOutcomes.Ok : AuditOutcomes.Error,
            command.Force ? new { id = command.Id, force = true } : new { id = command.Id }), cancellationToken);
        return result;
    }

    /// <summary>
    /// drain：部署收件匣來的（actor deploy）有期限（Hub:Deploy:DrainMaxSeconds，到期由 DrainExpiryService 解除），
    /// 管理收件匣根目錄來的（擁有者）沒有；擁有者的排空中收到部署的 drain 維持原狀（規則見 <see cref="DrainRules.Drain"/>）。
    /// </summary>
    private AdminResult Drain(AdminCommand command, long now, string actor)
    {
        var source = actor == AuditActors.Deploy ? DrainSource.Deploy : DrainSource.Admin;
        var maxSeconds = options.Value.Deploy.DrainMaxSeconds;
        var state = drain.Drain(source, now, command.RetryAfterSec ?? AdminInboxRules.DefaultRetryAfterSec, maxSeconds, command.Id);
        var message = state.Source == source
            ? source == DrainSource.Deploy
                ? $"已進入排空：/readyz 回 503，可以停止服務（部署的排空 {maxSeconds} 秒後服務若仍在執行就自動解除）"
                : "已進入排空：/readyz 回 503，可以停止服務（要 undrain 才解除）"
            : "擁有者的排空進行中：維持（不加期限），可以停止服務";
        return Ok(command, message, new
        {
            draining = state.Draining,
            source = DrainRules.SourceName(state.Source),
            retryAfterSec = state.RetryAfterSec,
            expiresAt = state.ExpiresAt,
        });
    }

    /// <summary>
    /// 輪換伺服器身分金鑰（sync-protocol 第 3 節）：舊金鑰簽輪換聲明，之後每個 HelloResponse 都附上，釘舊金鑰的裝置自動改釘。
    /// 離線期間輪換兩次以上的裝置只拿得到最近一次的聲明，要在本機確認新指紋。
    /// 輪換之後中斷所有裝置連線（sync-protocol 14.4）：連線上的裝置還釘著舊公鑰，之後以新金鑰簽的信封必然驗章失敗、被算成竄改；
    /// 裝置自動重連、依輪換聲明改釘
    /// </summary>
    private async Task<AdminResult> RotateHubIdentityAsync(AdminCommand command, CancellationToken cancellationToken)
    {
        var oldFingerprint = identity.Fingerprint;
        var rotation = await identity.RotateAsync(cancellationToken);
        await audit.WriteAsync(new AuditEntry(AuditActors.Admin, null, null, AuditActions.HubIdentityRotated, identity.Fingerprint, AuditOutcomes.Ok,
            new { from = oldFingerprint, to = identity.Fingerprint }), cancellationToken);
        await trust.PublishAsync(cancellationToken);
        connections.AbortAll();
        return Ok(command, "伺服器身分金鑰已輪換：已中斷所有裝置連線，裝置重連時自動改釘新的公鑰", new
        {
            oldFingerprint,
            newFingerprint = identity.Fingerprint,
            newSpki = rotation.NewSpki,
        });
    }

    /// <summary>收進來的桌面版（收件時間由新到舊）：Data 是版本清單，欄位同 <c>GET /api/v1/app/releases</c> 的 releases</summary>
    /// <summary>
    /// 各電腦的 app 版本（版本頁的電腦表，<see cref="AppReleaseDeviceStates"/>：已撤銷的不列，依配對順序）。開發機的桌面發佈腳本
    /// （tools/release/Publish-DesktopReleaseFromDev.ps1）以它確認每台都已經裝了信任這次簽章金鑰的版本；只讀，不寫任何東西
    /// </summary>
    private AdminResult AppDevices(AdminCommand command)
    {
        var list = devices.Read()
            .Select(d => new { d.DeviceId, d.Name, d.AppVersion, d.LastHelloAt, d.Online, d.UpdateOnly })
            .ToList();
        return Ok(command, list.Count == 0 ? "還沒有配對任何電腦" : $"共 {list.Count} 台電腦", list);
    }

    private async Task<AdminResult> AppReleasesAsync(AdminCommand command, CancellationToken cancellationToken)
    {
        var list = (await releases.ListAsync(cancellationToken)).Select(AppReleaseView.From).ToList();
        if (list.Count == 0)
            return Ok(command, "還沒有收到任何版本", list);
        return Ok(command, $"共 {list.Count} 個版本；目前提供 {releases.Latest?.Release ?? "（沒有可用的版本）"}", list);
    }

    /// <summary>
    /// 撤回一個桌面版（同版本頁的撤回）：資料列改成 withdrawn，latest 與套件端點立刻不再提供它，資料夾由下一次保存刪除（下載中的照樣傳完）；
    /// 稽核 app.release.withdrawn（actor 是 <paramref name="actor"/>：管理收件匣根目錄來的是 admin）。先前就撤回的回成功、不再寫稽核；找不到回失敗。
    /// 撤回與稽核不跟著服務停止取消：資料列改了就一定留下稽核
    /// </summary>
    private async Task<AdminResult> AppReleaseWithdrawAsync(AdminCommand command, string actor)
    {
        if (AdminInboxRules.CheckRelease(command) is { } error)
            return Fail(command, error);
        var release = command.Release!;
        switch (await releases.WithdrawAsync(release, CancellationToken.None))
        {
            case AppReleaseWithdrawOutcome.NotFound:
                return Fail(command, $"找不到版本 {release}");
            case AppReleaseWithdrawOutcome.AlreadyWithdrawn:
                return Ok(command, $"{release} 先前已經撤回，沒有變更",
                    new { release, state = AppReleaseStates.Withdrawn, alreadyWithdrawn = true, latest = releases.Latest?.Release });
            case AppReleaseWithdrawOutcome.Withdrawn:
                await audit.WriteAsync(new AuditEntry(actor, null, null, AuditActions.AppReleaseWithdrawn, release, AuditOutcomes.Ok), CancellationToken.None);
                var latest = releases.Latest?.Release;
                return Ok(command, $"已撤回 {release}：裝置不再下載它，目前提供 {latest ?? "（沒有可用的版本）"}",
                    new { release, state = AppReleaseStates.Withdrawn, alreadyWithdrawn = false, latest });
            default:
                throw new InvalidOperationException("不認得的撤回結果");
        }
    }

    private AdminResult Undrain(AdminCommand command)
    {
        drain.Undrain();
        return Ok(command, "已解除排空", new { draining = false, retryAfterSec = (int?)null });
    }

    private async Task<AdminResult> BackupAsync(AdminCommand command, CancellationToken cancellationToken)
    {
        var r = await backup.RunAsync("admin", scheduled: false, cancellationToken);
        return r.Ok ? Ok(command, "備份完成", new { folder = r.Folder, files = r.Manifest?.Files }) : Fail(command, "備份失敗：" + r.Error);
    }

    private async Task<AdminResult> SelfTestAsync(AdminCommand command, CancellationToken cancellationToken)
    {
        var report = await selfTest.RunAsync(cancellationToken);
        return new AdminResult(command.Id, command.Command, report.Ok, report.Ok ? "隔離自檢通過" : $"隔離自檢不符 {report.Failures} 項", report, time.GetUtcNow().ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// 正常結束（結束碼 0；以服務執行時 SCM 視為正常停止、不重啟）。給主控台執行（開發、探針）用；正式環境照樣用 Stop-Service。
    /// 稍等一下才停，結果檔才寫得出去。
    /// </summary>
    private AdminResult Stop(AdminCommand command)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            lifetime.StopApplication();
        });
        return Ok(command, "伺服器即將停止", null);
    }

    /// <summary>
    /// 立即做一輪憑證檢查：清掉失敗退避、強制重查 ARI（忽略上次查詢時間；CA 要求提前續期時不必等 12 小時），
    /// <c>--force</c> 時不看續期時點直接續期。等這一輪做完（最多 <see cref="CertRenewWait"/>）再回報結果。
    /// </summary>
    private async Task<AdminResult> CertRenewAsync(AdminCommand command, CancellationToken cancellationToken)
    {
        TlsTickReport report;
        try
        {
            report = await renewal.TriggerNow(command.Force).WaitAsync(CertRenewWait, cancellationToken);
        }
        catch (TimeoutException)
        {
            return Ok(command, $"已觸發憑證檢查（{(command.Force ? "直接續期" : "重查 ARI")}），{CertRenewWait.TotalSeconds:0} 秒內還沒做完：結果看 /readyz 的 tls 或稽核",
                new { forceRenew = command.Force, completed = false });
        }
        var data = new
        {
            forceRenew = command.Force,
            completed = true,
            report.AriChecked,
            report.Action,
            report.Reason,
            report.Changed,
            report.Thumbprint,
            report.RenewAt,
            report.LastOk,
            report.LastMessage,
            report.NextCheckAt,
        };
        if (!report.AcmeEnabled)
            return new AdminResult(command.Id, command.Command, false, "ACME 沒有啟用（Hub:Acme:Enabled=false），沒有做任何事", data, time.GetUtcNow().ToUnixTimeMilliseconds());
        var ari = report.AriChecked ? "已重查 ARI" : "沒有 ARI 可查（還沒有正式憑證或憑證沒有 ARI 識別碼）";
        if (report.Action == "wait")
        {
            var renewAt = report.RenewAt is { } at ? DateTimeOffset.FromUnixTimeMilliseconds(at).ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) : "（無）";
            return Ok(command, report.Reason == "backoff" ? $"{ari}；仍在失敗退避中" : $"{ari}；續期時點 {renewAt} 還沒到，不必續期（要立刻續期加 --force）", data);
        }
        return report.LastOk == true && report.Changed
            ? Ok(command, $"{ari}；已取得新憑證並熱換：{report.Thumbprint}（{report.Reason}）", data)
            : new AdminResult(command.Id, command.Command, false, $"{ari}；申請失敗：{report.LastMessage}", data, time.GetUtcNow().ToUnixTimeMilliseconds());
    }

    private async Task<AdminResult> VerifyAuditAsync(AdminCommand command, CancellationToken cancellationToken)
    {
        var v = await audit.VerifyAsync(cancellationToken);
        return new AdminResult(command.Id, command.Command, v.Ok, v.Ok ? $"稽核雜湊鏈完整（{v.Checked} 列）" : $"稽核雜湊鏈在 seq {v.BrokenAtSeq} 斷掉：{v.Reason}", v, time.GetUtcNow().ToUnixTimeMilliseconds());
    }

    private AdminResult Ok(AdminCommand command, string message, object? data) =>
        new(command.Id, command.Command, true, message, data, time.GetUtcNow().ToUnixTimeMilliseconds());

    private AdminResult Fail(AdminCommand command, string message) =>
        new(command.Id, command.Command, false, message, null, time.GetUtcNow().ToUnixTimeMilliseconds());
}

/// <summary>
/// 監看 admin-inbox\ 與 admin-inbox\deploy\（檔案監看＋保險輪詢），依檔名順序逐一處理後刪除要求檔。
/// 根目錄：所有管理指令，結果寫 admin-inbox\results\id.json；不認得的檔案（不是 .json、暫存檔）不動，格式錯誤的指令寫失敗結果後刪除。
/// deploy\：只接受 drain，處理完寫 status\drain.json；其他內容一律拒絕、記稽核後刪除（runner 帳戶不能藉此下其他管理指令）。
/// </summary>
internal sealed class AdminInboxProcessor(
    HubPaths paths,
    AdminCommandExecutor executor,
    AuditLog audit,
    HubInstance instance,
    IOptions<HubOptions> options,
    ILogger<AdminInboxProcessor> log) : BackgroundService
{
    private readonly SemaphoreSlim signal = new(0, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.AdminInbox.Enabled)
            return;
        Directory.CreateDirectory(paths.AdminInboxDir);
        Directory.CreateDirectory(paths.AdminResultsDir);
        Directory.CreateDirectory(paths.AdminInboxDeployDir);
        using var watcher = Watch(paths.AdminInboxDir);
        using var deployWatcher = Watch(paths.AdminInboxDeployDir);
        var poll = TimeSpan.FromSeconds(Math.Max(1, options.Value.AdminInbox.PollSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "管理收件匣處理失敗");
            }
            try
            {
                await signal.WaitAsync(poll, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task ProcessPendingAsync(CancellationToken cancellationToken)
    {
        await ProcessAdminAsync(cancellationToken);
        await ProcessDeployAsync(cancellationToken);
    }

    /// <summary>admin-inbox\ 根目錄：所有管理指令（只有本機管理員寫得進來）</summary>
    private async Task ProcessAdminAsync(CancellationToken cancellationToken)
    {
        foreach (var file in CommandFiles(paths.AdminInboxDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = await TryReadAsync(file, cancellationToken);
            if (json is null)
                continue;

            var command = AdminInboxRules.Parse(json, out var error);
            AdminResult result;
            if (command is null)
            {
                log.LogWarning("管理收件匣的指令看不懂：{File}：{Error}", Path.GetFileName(file), error);
                File.Delete(file);
                continue;
            }
            if (error is not null)
                result = new AdminResult(command.Id, command.Command, false, error, null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            else if (AdminInboxRules.IsExpired(command, DateTimeOffset.UtcNow))
            {
                log.LogWarning("管理指令放太久沒處理，作廢：{Command}（{Id}）", command.Command, command.Id);
                result = new AdminResult(command.Id, command.Command, false, "指令已過期（服務當時沒有在執行），沒有執行", null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
            else
            {
                log.LogInformation("執行管理指令：{Command}（{Id}）", command.Command, command.Id);
                result = await executor.ExecuteAsync(command, cancellationToken);
            }
            WriteJsonAtomically(Path.Combine(paths.AdminResultsDir, command.Id + ".json"), result);
            File.Delete(file);
        }
    }

    /// <summary>admin-inbox\deploy\：runner 帳戶寫入的部署要求，只接受 drain，回覆寫 status\drain.json</summary>
    private async Task ProcessDeployAsync(CancellationToken cancellationToken)
    {
        foreach (var file in CommandFiles(paths.AdminInboxDeployDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = await TryReadAsync(file, cancellationToken);
            if (json is null)
                continue;

            var name = Path.GetFileName(file);
            var request = AdminInboxRules.ParseDeploy(json, out var error);
            if (request is null)
            {
                log.LogWarning("部署收件匣的要求不接受，刪除：{File}：{Error}", name, error);
                await audit.WriteAsync(new AuditEntry(AuditActors.Deploy, null, null, AuditActions.AdminCommand, "deploy-inbox", AuditOutcomes.Denied,
                    new { file = name, error }), cancellationToken);
                File.Delete(file);
                continue;
            }
            if (AdminInboxRules.IsExpired(new AdminCommand(request.Id, "drain", request.RequestedAt), DateTimeOffset.UtcNow))
            {
                log.LogWarning("部署的 drain 放太久沒處理，作廢：{Id}", request.Id);
                File.Delete(file);
                continue;
            }
            if (AdminInboxRules.IsFromEarlierInstance(request, instance.StartedAtMs))
            {
                log.LogWarning("部署的 drain 是本程序啟動之前寫的（給上一個程序），不執行：{Id}（{Release}）", request.Id, request.Release);
                File.Delete(file);
                continue;
            }

            log.LogInformation("執行部署的 drain：{Id}（{Release}）", request.Id, request.Release);
            var result = await executor.ExecuteAsync(new AdminCommand(request.Id, "drain", request.RequestedAt, RetryAfterSec: request.RetryAfterSec), cancellationToken, AuditActors.Deploy);
            WriteJsonAtomically(paths.DrainAckFile, new DrainAck(request.Id, result.Ok, result.Ok ? "draining" : "failed", request.Release, request.RetryAfterSec,
                request.RequestedAt, result.ProcessedAt, result.Message));
            File.Delete(file);
        }
    }

    private static List<string> CommandFiles(string dir) =>
        Directory.Exists(dir)
            ? Directory.GetFiles(dir)
                .Where(f => AdminInboxRules.IsCommandFile(Path.GetFileName(f)))
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                .ToList()
            : [];

    /// <summary>讀要求檔：太大的直接刪；還在寫（被鎖住）回 null，下一輪再處理</summary>
    private async Task<string?> TryReadAsync(string file, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(file);
            if (info.Length > AdminInboxRules.MaxFileBytes)
            {
                log.LogWarning("管理收件匣的檔案太大，刪除：{File}", info.FullName);
                File.Delete(file);
                return null;
            }
            return await File.ReadAllTextAsync(file, cancellationToken);
        }
        catch (IOException)
        {
            return null; // 還在寫（以暫存檔＋改名寫入，正常不會遇到）：下一輪再處理
        }
    }

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, StatusJson.Options));
        File.Move(temp, path, overwrite: true);
    }

    private FileSystemWatcher Watch(string dir)
    {
        var watcher = new FileSystemWatcher(dir, "*.json") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
        watcher.Created += (_, _) => Signal();
        watcher.Renamed += (_, _) => Signal();
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void Signal()
    {
        if (signal.CurrentCount == 0)
        {
            try
            {
                signal.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }
}
