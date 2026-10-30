using Myriagent.Accounts;
using Myriagent.Remote;

namespace Myriagent.Sync;

// 協調者的遠端帳戶部分（WP4.8 計畫 D21；擁有者：WP4.8）：中樞的遠端登入處理者經同步橋問「要登入哪個本機帳戶、目錄預期的身分」。
// 只讀目錄的快照（catalogView）與帳戶清單，不碰引擎、SQLite 與 SignalR（S6）
internal sealed partial class SyncCoordinator
{
    /// <summary>
    /// 遠端登入的目標（UI 執行緒；<see cref="ISyncBridge.FindRemoteLoginTarget"/>）。未配對、已釋放、還沒有目錄回 NotPaired。
    /// <list type="bullet">
    /// <item>給邏輯帳戶（accountLoginDevice）：格式不對 invalid；目錄沒有、種類不認得 conflict；本機帳戶先找目錄的連結、其次是宣告
    /// （帳戶清單裡第一個，設計:2870）；種類和目錄不同 conflict；都沒有就給建立的範本（同「在這台登入」，檢查不過依原因對成代碼）</item>
    /// <item>給本機帳戶（accountLogin）：帳戶不存在 invalid；邏輯帳戶取目錄的連結，沒有就取宣告；目錄沒有這一筆時預期身分是 None；
    /// 種類和目錄不同 conflict</item>
    /// </list>
    /// </summary>
    /// <exception cref="ArgumentException">兩個 id 不是恰好給一個（呼叫端寫錯）</exception>
    public RemoteLoginLookup FindRemoteLoginTarget(string? localAccountId, string? logicalAccountId)
    {
        if ((localAccountId is null) == (logicalAccountId is null))
            throw new ArgumentException("本機帳戶與邏輯帳戶要恰好給一個");
        if (disposed || engine is null || catalogView is not { } view)
            return RemoteLoginLookup.NotPaired;
        return logicalAccountId is not null ? FindByLogical(view, logicalAccountId) : FindByLocal(view, localAccountId!);
    }

    private RemoteLoginLookup FindByLogical(SyncCatalogView view, string logicalAccountId)
    {
        if (!SyncIds.IsLogicalAccountId(logicalAccountId))
            return RemoteLoginLookup.Rejected(RemoteResultCodes.Invalid, AccountIdentityRules.DescribeClaimFailure(CatalogClaimCheck.InvalidId));
        // 這版不認得的種類（伺服器多出來的）當成目錄沒有，同 ClaimCatalogAccount
        if (!view.Accounts.TryGetValue(logicalAccountId, out var entry) || SyncProviderRules.FromWireName(entry.Provider) is not { } provider)
            return RemoteLoginLookup.Rejected(RemoteResultCodes.Conflict, AccountIdentityRules.DescribeClaimFailure(CatalogClaimCheck.NotInCatalog));

        var expected = ExpectationOf(entry);
        // 目錄的連結優先（帳戶清單的順序，不依字典的列舉順序），其次是宣告
        var local = accounts.Accounts.FirstOrDefault(a => view.LocalToLogical.TryGetValue(a.Id, out var linked)
                                                          && string.Equals(linked, logicalAccountId, StringComparison.Ordinal))
                    ?? accounts.Accounts.FirstOrDefault(a => string.Equals(a.LogicalAccountId, logicalAccountId, StringComparison.Ordinal));
        if (local is not null)
        {
            return local.Provider == provider
                ? RemoteLoginLookup.Found(new RemoteLoginTarget(logicalAccountId, provider, entry.Label, local.Id, null, expected))
                : RemoteLoginLookup.Rejected(RemoteResultCodes.Conflict, RemoteAccountRules.TargetKindMismatchMessage);
        }

        // 都沒有：照「在這台登入」的檢查給建立的範本（連結還指著已移除的本機帳戶時是 AlreadyLinkedHere）
        var linkedHere = view.LocalToLogical.Values.Contains(logicalAccountId, StringComparer.Ordinal);
        var check = AccountIdentityRules.CheckCatalogClaim(logicalAccountId, IsPaired, provider, linkedHere, claimedHere: false);
        if (check != CatalogClaimCheck.Ok)
            return RemoteLoginLookup.Rejected(ClaimFailureCode(check), AccountIdentityRules.DescribeClaimFailure(check));
        return ClaimCatalogAccount(logicalAccountId, out var failure) is { } template
            ? RemoteLoginLookup.Found(new RemoteLoginTarget(logicalAccountId, provider, entry.Label, null, template, expected))
            : RemoteLoginLookup.Rejected(RemoteResultCodes.Conflict, failure);
    }

    private RemoteLoginLookup FindByLocal(SyncCatalogView view, string localAccountId)
    {
        if (accounts.Find(localAccountId) is not { } account)
            return RemoteLoginLookup.Rejected(RemoteResultCodes.Invalid, "這台電腦沒有這個帳戶");
        var logical = view.LocalToLogical.TryGetValue(localAccountId, out var linked) ? linked : account.LogicalAccountId;
        if (logical is null || !view.Accounts.TryGetValue(logical, out var entry))
            return RemoteLoginLookup.Found(new RemoteLoginTarget(logical, account.Provider, account.Label, account.Id, null, RemoteLoginExpectation.None));
        if (SyncProviderRules.FromWireName(entry.Provider) != account.Provider)
            return RemoteLoginLookup.Rejected(RemoteResultCodes.Conflict, RemoteAccountRules.TargetKindMismatchMessage);
        return RemoteLoginLookup.Found(new RemoteLoginTarget(logical, account.Provider, entry.Label, account.Id, null, ExpectationOf(entry)));
    }

    private static RemoteLoginExpectation ExpectationOf(AccountUpsertPayload entry) => new(entry.IdentityKey, entry.ExpectedEmail, entry.ExpectedOrgId);

    /// <summary>「在這台登入」的檢查不過 → 遠端的拒絕代碼：不能在這台建立的種類 policy、格式不對 invalid、其他（已連結、已宣告、目錄沒有、未配對）conflict</summary>
    private static string ClaimFailureCode(CatalogClaimCheck check) => check switch
    {
        CatalogClaimCheck.UnsupportedProvider => RemoteResultCodes.Policy,
        CatalogClaimCheck.InvalidId => RemoteResultCodes.Invalid,
        CatalogClaimCheck.NotPaired or CatalogClaimCheck.NotInCatalog or CatalogClaimCheck.AlreadyLinkedHere or CatalogClaimCheck.AlreadyClaimedHere
            => RemoteResultCodes.Conflict,
        CatalogClaimCheck.Ok => throw new ArgumentException("Ok 不是失敗", nameof(check)),
        _ => throw new ArgumentOutOfRangeException(nameof(check), check, null),
    };
}
