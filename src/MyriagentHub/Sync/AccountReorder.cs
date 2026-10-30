using MyriagentHub.Data;

namespace MyriagentHub.Sync;

/// <summary>
/// 目錄寫入工作被拒：在 <see cref="SyncWriter.WriteAsync{T}"/> 的工作裡丟出，讓這個工作的 SAVEPOINT 整個回滾（已寫的列與發布的事件一起撤掉）。
/// <see cref="Code"/> 是瀏覽器的錯誤碼（BrowserErrorCodes 的值）
/// </summary>
internal sealed class CatalogRejectedException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>瀏覽器的 <c>accountReorder</c>（web-protocol 第六節第 10 點）：整批套用，任一筆被拒就整批回滾</summary>
internal static class AccountReorder
{
    /// <summary>
    /// 在寫入者交易裡讀未刪除帳戶的 (id, sort_order, created_at) → <see cref="AccountOrderRules.Plan"/> → 逐筆
    /// <see cref="Catalog.UpdateAccount"/>（只改順序、不帶 baseVersion）。回傳更新的筆數（順序沒變的帳戶不寫、不發事件）
    /// </summary>
    public static int Apply(SyncWriteContext context, Catalog catalog, IReadOnlyList<string> requested)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requested);
        var current = new List<AccountOrder>();
        using (var cmd = context.Command("SELECT logical_account_id, sort_order, created_at FROM logical_accounts WHERE deleted_at IS NULL"))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                current.Add(new AccountOrder(reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2)));
        }
        return ApplyPlan(context, catalog, AccountOrderRules.Plan(current, requested));
    }

    /// <summary>逐筆套用計畫；任一筆不是 Accepted 就丟 <see cref="CatalogRejectedException"/>（invalid），讓整個工作回滾</summary>
    internal static int ApplyPlan(SyncWriteContext context, Catalog catalog, IReadOnlyList<(string Id, int SortOrder)> plan)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(plan);
        foreach (var (id, order) in plan)
        {
            if (catalog.UpdateAccount(context, id, null, null, null, order).Rejected is not null)
                throw new CatalogRejectedException(MyriagentHub.Realtime.BrowserErrorCodes.Invalid);
        }
        return plan.Count;
    }
}
