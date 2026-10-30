using Myriagent.Security;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>一頁拉取準備好的結果：要提交的快取、要在 UI 執行緒套用的內容、要記下與解除的 detached</summary>
internal sealed record CatalogPage(
    CatalogCache Next,
    CatalogApply Apply,
    IReadOnlyList<(string LocalAccountId, string LogicalAccountId)> Detach,
    IReadOnlyList<string> Undetach,
    bool SelfRevoked);

/// <summary>發布的計畫：要寫進 outbox 的事件，與寫入成功之後才記的 published 摘要（null＝刪除）</summary>
internal sealed record PublishPlan(IReadOnlyList<OutboxAppend> Appends, IReadOnlyList<(string Key, PublishedLink? Link)> Published)
{
    public static PublishPlan Empty { get; } = new([], []);

    /// <summary>
    /// 只要忘掉 published 摘要、不寫事件的鍵（沒有 prov1 時延後的 Grok、Antigravity 帳戶，<see cref="CatalogSync.PlanAccounts"/>）：
    /// 不必等 outbox 寫入成功，下一次規劃就照沒發布過的處理
    /// </summary>
    public IReadOnlyList<string> Forget { get; init; } = [];

    public PublishPlan Concat(PublishPlan other) =>
        new([.. Appends, .. other.Appends], [.. Published, .. other.Published]) { Forget = [.. Forget, .. other.Forget] };
}

/// <summary>
/// 規劃修改類事件（account.update、prefs.update、device.update）時看的 outbox（引擎提供，含這一輪收件匣準備要寫的）
/// </summary>
/// <param name="PendingJson">合併鍵 → 還沒送出（Pending）的那一列的 JSON（同鍵多筆取最後一筆）；沒有回 null</param>
/// <param name="Unpulled">
/// 合併鍵 → 已送出、伺服器可能已經套用、但它造成的伺服器事件（account.upsert／prefs.set）還沒拉回的筆數：在途或要重送的列，
/// 與已提交而且伺服器的 seq 還沒被目錄游標越過的列（伺服器發的事件緊接在它之後）。伺服器每接受一筆版本加一
/// </param>
internal sealed record OutboxPlanView(Func<string, string?> PendingJson, Func<string, int> Unpulled)
{
    /// <summary>沒有任何待送、在途列的 outbox（測試與第一次規劃用）</summary>
    public static OutboxPlanView Empty { get; } = new(_ => null, _ => 0);

    public bool HasPending(string coalesceKey) => PendingJson(coalesceKey) is not null;
}

/// <summary>
/// 引擎的目錄（只在引擎執行緒使用）：sync.db 目錄快取的記憶體副本、從快取組 <see cref="SyncCatalogView"/>、算一頁拉取的 <see cref="CatalogApply"/>。
/// <list type="bullet">
/// <item>拉取側：只套用名稱、顏色、順序與自己的對應（sync-protocol 6.1）；account.deleted 時把這台連結到它的本機帳戶記成 detached，
/// 之後同一個邏輯帳戶有 seq 更大的 account.upsert（伺服器讓它復活）就解除（Q8）。</item>
/// <item>發布側：本機帳戶的 link.upsert／link.removed（摘要和 published 比較，有變才送）、cli.state、
/// 使用者在這台改的名稱、顏色、順序（account.update）、漫遊偏好（prefs.update）與裝置改名（device.update）。
/// 修改類帶目錄快取裡的版本（樂觀並行，sync-protocol 6.2）。</item>
/// </list>
/// 檢視交給 UI 執行緒之後不再改：每次都換新的字典（copy-on-write）。
/// </summary>
internal sealed class CatalogSync
{
    private static readonly IReadOnlyDictionary<string, AccountUpsertPayload> NoAccounts = new Dictionary<string, AccountUpsertPayload>();

    private readonly string selfDeviceId;
    private CatalogCache cache;
    private IReadOnlyDictionary<string, string> detached;

    public CatalogSync(string selfDeviceId, CatalogCache cache, IReadOnlyDictionary<string, string> detached)
    {
        this.selfDeviceId = selfDeviceId;
        this.cache = cache;
        this.detached = detached;
    }

    public CatalogCache Cache => cache;

    /// <summary>本機帳戶 id → 被解除連結時的邏輯帳戶 id</summary>
    public IReadOnlyDictionary<string, string> Detached => detached;

    public SyncCatalogView View(IReadOnlyList<DeviceRegistryEntry> registry) => ViewOf(cache, registry);

    /// <summary>算一頁拉取的結果（不改目前的快取；UI 套用、sync.db 提交成功之後才 <see cref="Accept"/>）</summary>
    /// <param name="currentLocalOrder">這台目前的本機帳戶順序（最後一次的帳戶快照）：sortOrder 同分的帳戶照它排，不打亂使用者的清單</param>
    public CatalogPage Prepare(PullDelta delta, IReadOnlyList<DeviceRegistryEntry> registry, IReadOnlyList<string>? currentLocalOrder = null)
    {
        var accounts = new Dictionary<string, AccountUpsertPayload>(cache.Accounts, StringComparer.Ordinal);
        var appearance = new List<(string, string, string)>();
        var detach = new List<(string, string)>();
        var undetach = new List<string>();
        var detachedLogical = detached.Values.ToHashSet(StringComparer.Ordinal);
        foreach (var (id, (_, upsert, deleted)) in delta.Accounts.OrderBy(a => a.Value.Seq))
        {
            if (upsert is not null)
            {
                accounts[id] = upsert;
                if (detachedLogical.Contains(id))
                    undetach.Add(id);
                foreach (var link in SelfLinks(upsert))
                    appearance.Add((link.LocalAccountId, upsert.Label, upsert.Color));
            }
            else if (deleted is not null)
            {
                if (cache.Accounts.TryGetValue(id, out var old))
                {
                    foreach (var link in SelfLinks(old))
                        detach.Add((link.LocalAccountId, id));
                }

                accounts.Remove(id);
            }
        }

        var devices = Merge(cache.Devices, delta.Devices);
        var revoked = Merge(cache.Revoked, delta.Revoked);
        var prefs = Merge(cache.Prefs, delta.Prefs);
        var next = new CatalogCache(accounts, devices, revoked, prefs);

        var prefChanges = new List<(string, JToken)>();
        foreach (var key in PrefsRules.Keys)
        {
            if (delta.Prefs.TryGetValue(key, out var pref) && PrefsSyncRules.TryFromWire(pref, out var k, out var value))
                prefChanges.Add((k, value));
        }

        var view = ViewOf(next, registry);
        var order = delta.Accounts.Count > 0 ? LocalOrder(accounts, currentLocalOrder) : null;
        return new CatalogPage(
            next,
            new CatalogApply(view, appearance, order, prefChanges),
            detach,
            undetach,
            delta.Revoked.ContainsKey(selfDeviceId));
    }

    /// <summary>UI 套用、sync.db 提交成功之後換成新的快取與 detached</summary>
    public void Accept(CatalogPage page)
    {
        cache = page.Next;
        if (page.Detach.Count == 0 && page.Undetach.Count == 0)
            return;
        var next = new Dictionary<string, string>(detached, StringComparer.Ordinal);
        foreach (var logical in page.Undetach)
        {
            foreach (var local in next.Where(d => d.Value == logical).Select(d => d.Key).ToList())
                next.Remove(local);
        }

        foreach (var (local, logical) in page.Detach)
            next[local] = logical;
        detached = next;
    }

    /// <summary>伺服器換實例（D15 第 1 步）：目錄快取已在 sync.db 清空，記憶體跟著清（detached 保留）</summary>
    public void Reset() =>
        cache = new CatalogCache(NoAccounts, new Dictionary<string, DeviceUpsertPayload>(), new Dictionary<string, DeviceRevokedPayload>(), new Dictionary<string, PrefsSetPayload>());

    // ── 發布側 ──

    /// <summary>
    /// 本機帳戶清單 → link.upsert（每個帳戶以 BuildLinkUpsert 算 payload、摘要和 published 不同才送）與 link.removed
    /// （發布過、現在不在清單；清單唯讀時不算）。detached 的帳戶不送。合併鍵一律 link:{id}：還沒送出的 upsert 會被 removed 取代。
    /// 同一台電腦兩個本機帳戶身分相同（同一個帳號登入兩次）時，沒連結的那個不送（<see cref="DuplicateIdentity"/>）。
    /// 帶的邏輯帳戶照清單順序以 <see cref="CatalogSyncRules.ResolveLinkLogical"/> 決定（目錄的對應優先，其次是「在這台登入」的宣告，
    /// 同一個邏輯帳戶只認清單裡第一個宣告者）；決定了邏輯帳戶時帶目錄裡它的身分鍵（Claude 的 org 不明時拿來比對 email，D11）。
    /// <paramref name="prov1"/> 是 false（這條連線、或離線時上一條連線沒協商出 prov1）時 Grok、Antigravity 的帳戶不送；發布過的忘掉摘要
    /// （不送 link.removed：帳戶還在），prov1 回來時整筆重送（舊伺服器以 invalid 擋下的那筆也補回來）
    /// </summary>
    public PublishPlan PlanAccounts(IReadOnlyList<LocalAccountSnapshot> snapshot, bool listReadOnly, IReadOnlyDictionary<string, PublishedLink> published,
        bool prov1 = true)
    {
        var localToLogical = LocalToLogical(cache);
        var linkedHere = localToLogical.Values.ToHashSet(StringComparer.Ordinal);
        var claimedEarlier = new HashSet<string>(StringComparer.Ordinal);
        var catalogIds = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var entry in cache.Accounts.Values)
        {
            if (!catalogIds.TryGetValue(entry.Provider, out var ids))
                catalogIds[entry.Provider] = ids = new HashSet<string>(StringComparer.Ordinal);
            ids.Add(entry.LogicalAccountId);
        }

        var candidates = new List<(LocalAccountSnapshot Account, bool Linked, LinkUpsertPayload Payload)>();
        var deferred = new List<string>();
        foreach (var account in snapshot)
        {
            if (!SyncProviderRules.Publishable(account.Provider, prov1))
            {
                if (published.ContainsKey(account.Id))
                    deferred.Add(account.Id);
                continue;
            }

            if (detached.ContainsKey(account.Id))
                continue;
            var mapped = localToLogical.TryGetValue(account.Id, out var m) ? m : null;
            var sameProvider = catalogIds.TryGetValue(SyncProviderRules.WireName(account.Provider), out var ids) ? ids : [];
            var logical = CatalogSyncRules.ResolveLinkLogical(mapped, account.ClaimedLogicalAccountId, sameProvider, linkedHere, claimedEarlier);
            var catalogIdentityKey = logical is not null && cache.Accounts.TryGetValue(logical, out var target) ? target.IdentityKey : null;
            var last = published.TryGetValue(account.Id, out var p) ? p : null;
            if (CatalogSyncRules.BuildLinkUpsert(account, logical, last, catalogIdentityKey) is { } payload)
                candidates.Add((account, mapped is not null, payload));
        }

        var duplicates = DuplicateIdentity(candidates.Select(c => (c.Account.Id, c.Payload)).ToList());
        var appends = new List<OutboxAppend>();
        var changes = new List<(string, PublishedLink?)>();
        foreach (var (account, linked, payload) in candidates)
        {
            if (duplicates.Contains(account.Id))
                continue;
            var last = published.TryGetValue(account.Id, out var p) ? p : null;
            var digest = CatalogSyncRules.Digest(payload, linked);
            if (last?.Digest == digest)
                continue;
            appends.Add(new OutboxAppend(SyncStreams.Catalog, SyncEventKinds.LinkUpsert, SyncJson.Serialize(payload), CatalogSyncRules.LinkCoalesceKey(account.Id)));
            changes.Add((account.Id, new PublishedLink(digest, payload.IdentityKey)));
        }

        var publishedAccounts = published.Keys.Where(CatalogSyncRules.IsLocalAccountId).ToList();
        foreach (var id in CatalogSyncRules.RemovedLinks(publishedAccounts, snapshot.Select(a => a.Id).ToList(), listReadOnly))
        {
            appends.Add(new OutboxAppend(SyncStreams.Catalog, SyncEventKinds.LinkRemoved, SyncJson.Serialize(new LinkRemovedPayload(id)), CatalogSyncRules.LinkCoalesceKey(id)));
            changes.Add((id, null));
        }

        // 延後的帳戶只忘掉摘要、不寫事件（Write 在沒有 append 時不記 published，所以另外帶在 Forget）
        return new PublishPlan(appends, changes) { Forget = deferred };
    }

    /// <summary>
    /// 不發布的本機帳戶：沒連結（payload 沒有邏輯帳戶）、而且身分鍵和這台另一個已連結的帳戶或清單順序更前面的未連結帳戶相同的。
    /// 伺服器以 (邏輯帳戶, 裝置) 為主鍵：沒連結的帳戶依身分找到同一個邏輯帳戶後會把那一格的本機帳戶蓋成自己，兩個都送的話連結來回翻轉，
    /// 目錄的名稱與顏色被套到兩個本機帳戶上。已連結的一律照送（伺服器依 (裝置, 本機帳戶) 找到自己的連結，不會蓋到別人）。
    /// 不發布的帳戶在用量閘門上就是「沒有連結」：照本機頻率查、不上傳
    /// </summary>
    internal static HashSet<string> DuplicateIdentity(IReadOnlyList<(string LocalAccountId, LinkUpsertPayload Payload)> candidates)
    {
        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, payload) in candidates)
        {
            if (payload.LogicalAccountId is not null && payload.IdentityKey is { } identity)
                owners.Add(identity);
        }

        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, payload) in candidates)
        {
            if (payload.LogicalAccountId is null && payload.IdentityKey is { } identity && !owners.Add(identity))
                duplicates.Add(id);
        }

        return duplicates;
    }

    /// <summary>cli.state：摘要和上次送出的不同才送（合併鍵 cli）</summary>
    public static PublishPlan PlanCli(CliStatePayload state, IReadOnlyDictionary<string, PublishedLink> published)
    {
        var digest = CatalogSyncRules.CliDigest(state);
        if (published.TryGetValue(CatalogSyncRules.CliPublishedKey, out var last) && last.Digest == digest)
            return PublishPlan.Empty;
        return new PublishPlan(
            [new OutboxAppend(SyncStreams.Catalog, SyncEventKinds.CliState, SyncJson.Serialize(state), CatalogSyncRules.CliCoalesceKey)],
            [(CatalogSyncRules.CliPublishedKey, new PublishedLink(digest, null))]);
    }

    /// <summary>app.state（app 自我更新 U3）：摘要和上次送出的不同才送（合併鍵 app；伺服器換實例或還原後 published 清掉，重送一次）</summary>
    public static PublishPlan PlanApp(AppStatePayload state, IReadOnlyDictionary<string, PublishedLink> published)
    {
        var json = SyncJson.Serialize(state);
        var digest = PayloadRules.Sha256Hex(json);
        if (published.TryGetValue(CatalogSyncRules.AppPublishedKey, out var last) && last.Digest == digest)
            return PublishPlan.Empty;
        return new PublishPlan(
            [new OutboxAppend(SyncStreams.Catalog, SyncEventKinds.AppState, json, CatalogSyncRules.AppCoalesceKey)],
            [(CatalogSyncRules.AppPublishedKey, new PublishedLink(digest, null))]);
    }

    /// <summary>
    /// 使用者在這台改了帳戶名稱或顏色（AccountManager 套用後的值）：已連結、目錄裡有版本的帳戶送 account.update（兩者都帶）；
    /// 沒連結不送。和目錄的值相同也不送，除非 outbox 還有這個帳戶沒送出的修改（改回原值時要蓋掉它）。
    /// 還沒送出的那筆的順序併進來（<see cref="AccountUpdate"/>）
    /// </summary>
    public OutboxAppend? PlanAccountEdited(string localAccountId, string label, string color, OutboxPlanView outbox)
    {
        if (!LocalToLogical(cache).TryGetValue(localAccountId, out var logical) || !cache.Accounts.TryGetValue(logical, out var account))
            return null;
        var newLabel = Myriagent.Accounts.AccountRules.CleanCatalogLabel(label) ?? account.Label;
        var currentColor = CatalogSyncRules.NormalizeColor(account.Color) ?? account.Color;
        var newColor = CatalogSyncRules.NormalizeColor(color) ?? currentColor;
        var pending = PendingAccountUpdate(outbox, logical);
        if (newLabel == account.Label && newColor == currentColor && pending is null)
            return null;
        return AccountUpdate(account, outbox, newLabel, newColor, pending?.SortOrder);
    }

    /// <summary>
    /// 本機重排：把已連結帳戶原有的 sortOrder 依新順序重新分配（ReorderUpdates），每個改變的（或 outbox 還有沒送出的順序修改的）
    /// 送 account.update{sortOrder}；還沒送出的那筆的名稱與顏色併進來（<see cref="AccountUpdate"/>）
    /// </summary>
    public IReadOnlyList<OutboxAppend> PlanReorder(IReadOnlyList<string> localOrder, OutboxPlanView outbox)
    {
        var linked = new Dictionary<string, (string LogicalAccountId, int SortOrder)>(StringComparer.Ordinal);
        foreach (var (local, logical) in LocalToLogical(cache))
        {
            if (cache.Accounts.TryGetValue(logical, out var account))
                linked[local] = (logical, account.SortOrder);
        }

        return CatalogSyncRules.ReorderUpdates(localOrder, linked, logical => PendingAccountUpdate(outbox, logical) is { SortOrder: not null })
            .Select(u =>
            {
                var pending = PendingAccountUpdate(outbox, u.LogicalAccountId);
                return AccountUpdate(cache.Accounts[u.LogicalAccountId], outbox, pending?.Label, pending?.Color, u.SortOrder);
            })
            .ToList();
    }

    /// <summary>
    /// 一個邏輯帳戶的 account.update：名稱、顏色、順序共用一個合併鍵（account:{L}），每個帳戶最多一筆 Pending——分成兩個鍵時
    /// 兩筆帶同一個 baseVersion，伺服器接受第一筆、版本加一之後第二筆必定 conflict。還沒送出的那筆的欄位由呼叫端併進來（新的值優先）。
    /// baseVersion＝目錄的版本＋已送出、結果還沒拉回的筆數（前一筆已經送出、不能合併時，伺服器套用它之後的版本）
    /// </summary>
    private static OutboxAppend AccountUpdate(AccountUpsertPayload account, OutboxPlanView outbox, string? label, string? color, int? sortOrder)
    {
        var key = CatalogSyncRules.AccountCoalesceKey(account.LogicalAccountId);
        var baseVersion = account.Version + outbox.Unpulled(key);
        return new OutboxAppend(SyncStreams.Catalog, SyncEventKinds.AccountUpdate,
            SyncJson.Serialize(new AccountUpdatePayload(account.LogicalAccountId, baseVersion, label, color, sortOrder)), key);
    }

    /// <summary>這個邏輯帳戶還沒送出的 account.update（沒有回 null；內容解不開時當成沒有欄位的一筆）</summary>
    private static AccountUpdatePayload? PendingAccountUpdate(OutboxPlanView outbox, string logicalAccountId)
    {
        if (outbox.PendingJson(CatalogSyncRules.AccountCoalesceKey(logicalAccountId)) is not { } json)
            return null;
        var empty = new AccountUpdatePayload(logicalAccountId, 0, null, null, null);
        try
        {
            return SyncJson.Deserialize<AccountUpdatePayload>(json) ?? empty;
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return empty;
        }
    }

    /// <summary>
    /// 漫遊偏好：白名單內才送 prefs.update（baseVersion＝目錄的版本〔沒設過是 0〕＋已送出、結果還沒拉回的筆數）；
    /// 和目錄的值相同不送，除非 outbox 還有這個鍵沒送出的修改
    /// </summary>
    public OutboxAppend? PlanPrefChanged(string key, JToken value, OutboxPlanView outbox)
    {
        if (!PrefsRules.IsValid(key, value))
            return null;
        var known = cache.Prefs.TryGetValue(key, out var pref) ? pref : null;
        var coalesceKey = CatalogSyncRules.PrefCoalesceKey(key);
        if (known is not null && JToken.DeepEquals(known.Value, value) && !outbox.HasPending(coalesceKey))
            return null;
        return new OutboxAppend(SyncStreams.Catalog, SyncEventKinds.PrefsUpdate,
            SyncJson.Serialize(new PrefsUpdatePayload(key, value.DeepClone(), (known?.Version ?? 0) + outbox.Unpulled(coalesceKey))), coalesceKey);
    }

    /// <summary>
    /// 裝置改名：名稱清理後合格、目標在目錄或名冊裡、沒被撤銷才送 device.update（合併鍵 device:{id}）；
    /// 和目前的名稱相同不送，除非 outbox 還有這台裝置沒送出的改名
    /// </summary>
    public OutboxAppend? PlanDeviceRenamed(string deviceId, string name, IReadOnlyList<DeviceRegistryEntry> registry, OutboxPlanView outbox)
    {
        if (PairingRules.CleanDeviceName(name) is not { } clean)
            return null;
        var registered = registry.FirstOrDefault(e => e is not null && e.DeviceId == deviceId);
        var known = cache.Devices.TryGetValue(deviceId, out var device) ? device : null;
        if (known is null && registered is null)
            return null;
        if (cache.Revoked.ContainsKey(deviceId) || registered?.RevokedAt is not null)
            return null;
        var key = CatalogSyncRules.DeviceCoalesceKey(deviceId);
        if ((known?.Name ?? registered?.Name) == clean && !outbox.HasPending(key))
            return null;
        return new OutboxAppend(SyncStreams.Catalog, SyncEventKinds.DeviceUpdate, SyncJson.Serialize(new DeviceUpdatePayload(deviceId, clean)), key);
    }

    /// <summary>用量閘門用的連結狀態：本機帳戶 id → 它在目錄裡那一格（ready、身分相符）</summary>
    public IReadOnlyDictionary<string, UsageLinkState> LinkStates()
    {
        var result = new Dictionary<string, UsageLinkState>(StringComparer.Ordinal);
        foreach (var account in cache.Accounts.Values.OrderBy(a => a.LogicalAccountId, StringComparer.Ordinal))
        {
            foreach (var link in SelfLinks(account))
                result[link.LocalAccountId] = new UsageLinkState(account.LogicalAccountId, link.Status == "ready", link.IdentityOk);
        }

        return result;
    }

    private SyncCatalogView ViewOf(CatalogCache c, IReadOnlyList<DeviceRegistryEntry> registry) =>
        new(selfDeviceId, c.Accounts, c.Devices, registry, LocalToLogical(c), c.Revoked);

    /// <summary>本機帳戶 id → 邏輯帳戶 id（links 裡 deviceId 是自己的那幾格）</summary>
    private Dictionary<string, string> LocalToLogical(CatalogCache c)
    {
        var localToLogical = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var account in c.Accounts.Values.OrderBy(a => a.LogicalAccountId, StringComparer.Ordinal))
        {
            foreach (var link in SelfLinks(account))
                localToLogical[link.LocalAccountId] = account.LogicalAccountId;
        }

        return localToLogical;
    }

    /// <summary>
    /// 目錄的順序套到本機：這台連結到的本機帳戶依邏輯帳戶的 sortOrder 排；同分時照這台目前的位置（不在目前清單裡的排後面、再依 id）。
    /// 同分不能依邏輯帳戶 id 排：id 是亂數，會把使用者的清單打亂（伺服器只在建立時寫 sortOrder，兩台各自建立的帳戶常常同分）
    /// </summary>
    private List<string> LocalOrder(IReadOnlyDictionary<string, AccountUpsertPayload> accounts, IReadOnlyList<string>? currentLocalOrder)
    {
        var position = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < (currentLocalOrder?.Count ?? 0); i++)
            position.TryAdd(currentLocalOrder![i], i);
        int PositionOf(string localAccountId) => position.TryGetValue(localAccountId, out var p) ? p : int.MaxValue;
        return accounts.Values
            .Select(a => (Account: a, Locals: SelfLinks(a).Select(l => l.LocalAccountId).OrderBy(PositionOf).ThenBy(id => id, StringComparer.Ordinal).ToList()))
            .OrderBy(x => x.Account.SortOrder)
            .ThenBy(x => x.Locals.Count == 0 ? int.MaxValue : PositionOf(x.Locals[0]))
            .ThenBy(x => x.Account.LogicalAccountId, StringComparer.Ordinal)
            .SelectMany(x => x.Locals)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private IEnumerable<AccountLinkView> SelfLinks(AccountUpsertPayload account) =>
        (account.Links ?? []).Where(l => l is not null && l.DeviceId == selfDeviceId && !string.IsNullOrEmpty(l.LocalAccountId));

    private static Dictionary<string, T> Merge<T>(IReadOnlyDictionary<string, T> current, IReadOnlyDictionary<string, T> changes)
    {
        var result = new Dictionary<string, T>(current, StringComparer.Ordinal);
        foreach (var (key, value) in changes)
            result[key] = value;
        return result;
    }
}
