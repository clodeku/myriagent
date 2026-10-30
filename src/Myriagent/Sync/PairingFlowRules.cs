using System.Text;

namespace Myriagent.Sync;

/// <summary>配對輪詢的下一步（<see cref="PairingFlowRules.Next"/>）</summary>
internal abstract record PairStep
{
    /// <summary>等這麼久再輪詢（從收到這次回應起算）</summary>
    public sealed record Wait(TimeSpan Delay) : PairStep;

    public sealed record Approved(PairTokenResponse Response) : PairStep
    {
        /// <summary>ToString 不印回應（D16：裡面有 chd_）</summary>
        protected override bool PrintMembers(StringBuilder builder)
        {
            builder.Append("Response = …");
            return true;
        }
    }

    public sealed record Denied : PairStep;

    public sealed record Expired : PairStep;

    /// <summary>不會再成功的錯誤（invalid_grant、invalid_request、protocol、too_many_pending、不認得的代碼）</summary>
    public sealed record Fatal(string Code) : PairStep;
}

/// <summary>配對流程的純函式規則（sync-protocol 2.1、2.3；計畫第 6 步）</summary>
internal static class PairingFlowRules
{
    /// <summary>
    /// 本機算的到期（expiresIn）之後再繼續輪詢多久：伺服器對「已核准、還沒換權杖」的請求在到期後寬限 5 分鐘（PairingServerRules.ConsumeGraceMs），
    /// 核准落在到期前幾秒時，本機不能先放棄
    /// </summary>
    public static readonly TimeSpan ApprovedGrace = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 一次輪詢結果之後怎麼做：authorization_pending 照目前間隔；slow_down 用 <see cref="PairingRules.NextInterval"/>（＋5 秒或伺服器給的較大值）；
    /// 429 照 Retry-After（不短於間隔）；網路錯誤照間隔重試；終止的錯誤直接結束。
    /// 本機到期後不停（伺服器說了才算），但超過到期＋5 分鐘的寬限就以 Expired 結束
    /// </summary>
    /// <param name="interval">目前的間隔（秒）</param>
    public static (PairStep Step, int Interval) Next(PairPollResult r, int interval, DateTimeOffset now, DateTimeOffset localExpiry)
    {
        var current = Math.Max(1, interval);
        switch (r)
        {
            case PairPollResult.Approved approved:
                return (new PairStep.Approved(approved.Response), current);
            case PairPollResult.Error { Body: var error }:
                switch (error.Error)
                {
                    case PairingErrors.AuthorizationPending:
                        return Keep(current, TimeSpan.FromSeconds(current), now, localExpiry);
                    case PairingErrors.SlowDown:
                        var next = Math.Max(1, PairingRules.NextInterval(current, error));
                        return Keep(next, TimeSpan.FromSeconds(next), now, localExpiry);
                    case PairingErrors.ExpiredToken:
                        return (new PairStep.Expired(), current);
                    case PairingErrors.AccessDenied:
                        return (new PairStep.Denied(), current);
                    default:
                        return (new PairStep.Fatal(error.Error ?? PairingCodes.BadResponse), current);
                }
            case PairPollResult.RateLimited { RetryAfter: var retryAfter }:
                var wait = retryAfter is { } after && after > TimeSpan.FromSeconds(current) ? after : TimeSpan.FromSeconds(current);
                return Keep(current, wait, now, localExpiry);
            case PairPollResult.NetworkError:
                return Keep(current, TimeSpan.FromSeconds(current), now, localExpiry);
            default:
                throw new ArgumentOutOfRangeException(nameof(r), r?.GetType().Name, "不認得的輪詢結果");
        }
    }

    /// <summary>給人看的錯誤說明（顯示在 sync.pairing.error；一律 textContent）</summary>
    public static string ErrorText(string code) => code switch
    {
        PairingErrors.ExpiredToken => "配對碼已過期，請重新開始配對",
        PairingErrors.AccessDenied => "擁有者拒絕了這次配對",
        PairingErrors.InvalidGrant => "配對碼已使用過或不存在，請重新開始配對",
        PairingErrors.TooManyPending => "待核准的裝置太多，請先在網頁核准或拒絕，或等它們過期",
        PairingErrors.InvalidRequest => "伺服器拒絕這次配對要求（格式不正確，或這把裝置金鑰已配對過）",
        SyncErrorCodes.Protocol => "伺服器不支援這個版本，請更新 app",
        PairingCodes.RateLimited => "配對要求太頻繁，請稍後再試",
        PairingCodes.BadResponse => "伺服器回應不正確",
        PairingCodes.NetworkError => "連不上伺服器",
        PairingCodes.InvalidDeviceName => "裝置名稱不合格（1～60 字，不可以只有空白或控制字元）",
        PairingCodes.KeyFailed => "無法建立裝置金鑰",
        PairingCodes.InvalidApproval => "伺服器的配對回應驗證失敗，沒有保存；請到網頁撤銷這台後重新配對",
        PairingCodes.CommitFailed => "配對已核准但存檔失敗，請到網頁撤銷這台後重新配對",
        _ => $"配對失敗（{code}）",
    };

    /// <summary>
    /// 核准回應的檢查（sync-protocol 2.3，全部通過才保存）：deviceId 格式、chd_ 前綴、伺服器身分公鑰是 P-256 SPKI、伺服器身分、
    /// 名冊合格而且有自己（<see cref="Myriagent.Security.DeviceRegistryRules.IsOwnRecord"/>：公鑰是送出的同一串、未撤銷）。
    /// 合格回 null，否則回原因（不含權杖）
    /// </summary>
    public static string? ValidateApproval(PairTokenResponse r, string devicePubKey)
    {
        if (!SyncIds.IsDeviceId(r.DeviceId))
            return "deviceId 格式不正確";
        if (r.DeviceToken is not { Length: > 4 and <= 512 } token || !token.StartsWith(SyncProtocol.DeviceTokenPrefix, StringComparison.Ordinal))
            return "裝置權杖格式不正確";
        if (!Myriagent.Security.P256KeyRules.IsValidSpki(r.HubIdentitySpki))
            return "伺服器身分公鑰不是 P-256 SPKI";
        if (r.Server is not { InstanceId: { Length: > 0 } } || r.Server.RestoreGeneration < 0)
            return "伺服器身分（instanceId）不正確";
        return TrustRules.CheckBundle(r.Trust, r.DeviceId, devicePubKey) switch
        {
            TrustCheck.Accept => null,
            TrustCheck.NotInRegistry => "名冊裡沒有這台",
            TrustCheck.SelfRevoked => "名冊裡這台已被撤銷",
            TrustCheck.Unchanged => "沒有附名冊",
            TrustCheck.Invalid => "名冊格式不正確，或這台的公鑰不符",
            var other => throw new ArgumentOutOfRangeException(nameof(r), other, null),
        };
    }

    private static (PairStep Step, int Interval) Keep(int interval, TimeSpan delay, DateTimeOffset now, DateTimeOffset localExpiry) =>
        now >= localExpiry + ApprovedGrace ? (new PairStep.Expired(), interval) : (new PairStep.Wait(delay), interval);
}
