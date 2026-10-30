using System.Diagnostics.CodeAnalysis;
using System.Text;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Security;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>代理種類在同步裡的分類（D8；Grok、Antigravity 見設計 2026-10-22-grok-antigravity-sync 第 3 節）</summary>
internal enum ProviderSyncKind
{
    /// <summary>進帳戶目錄、cli.state 與對話事件，任何伺服器都收（Claude、Codex）</summary>
    Catalog,

    /// <summary>
    /// 同 <see cref="Catalog"/>，但只在連線協商出 <c>prov1</c> 時送（Grok、Antigravity）：舊伺服器的目錄、對話事件與 cli.state 只收 Claude、Codex，
    /// 送了會被判 invalid、佔掉 deviceSeq，或讓對話被封鎖。沒有 prov1 時延後（不是 localOnly：不送伺服器刪除），prov1 回來再補送
    /// </summary>
    NeedsProv1,
}

/// <summary>代理種類的同步分類（一律 switch、未知值丟例外；新種類合併時 SyncProviderRulesTests 會變紅，提醒補上分類）</summary>
internal static class SyncProviderRules
{
    public static ProviderSyncKind Classify(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude or AgentProvider.Codex => ProviderSyncKind.Catalog,
        AgentProvider.Antigravity or AgentProvider.Grok => ProviderSyncKind.NeedsProv1,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "沒有同步分類的代理種類"),
    };

    /// <summary>這條連線（或離線時上一條連線）協商出 <c>prov1</c> 與否之下，這種代理現在送不送伺服器</summary>
    public static bool Publishable(AgentProvider provider, bool prov1) => Classify(provider) switch
    {
        ProviderSyncKind.Catalog => true,
        ProviderSyncKind.NeedsProv1 => prov1,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "沒有同步分類的代理種類"),
    };

    /// <summary>線上的供應商名稱（同列舉名稱）</summary>
    public static string WireName(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => "Claude",
        AgentProvider.Codex => "Codex",
        AgentProvider.Antigravity => "Antigravity",
        AgentProvider.Grok => "Grok",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "沒有同步分類的代理種類"),
    };

    /// <summary>
    /// 線上的供應商名稱。2026-10-22 起四種都有（Grok、Antigravity 送不送由引擎依 prov1 決定，<see cref="Publishable"/>）；
    /// 保留 Try 的形狀給之後可能不同步的種類
    /// </summary>
    public static bool TryWireName(AgentProvider provider, [NotNullWhen(true)] out string? name)
    {
        name = WireName(provider);
        return true;
    }

    /// <summary>
    /// 對話實際的同步政策（docs/sync-protocol.md 14.2）：存檔的值經 ConversationSyncPolicies 解讀。不看 prov1：沒有 prov1 時
    /// 引擎延後 Grok、Antigravity 的對話（記重整），不能當成 localOnly（那會送伺服器刪除）
    /// </summary>
    public static string ConversationPolicy(AgentConversation conversation)
        => ConversationSyncPolicies.Effective(conversation.SyncPolicy, TryWireName(conversation.Provider, out _));

    /// <summary>線上名稱 → 代理種類；不認得的（新版伺服器多出的種類）回 null</summary>
    public static AgentProvider? FromWireName(string? name) => name switch
    {
        "Claude" => AgentProvider.Claude,
        "Codex" => AgentProvider.Codex,
        "Antigravity" => AgentProvider.Antigravity,
        "Grok" => AgentProvider.Grok,
        _ => null,
    };
}

/// <summary>
/// UI 執行緒上取得的本機帳戶摘要（鏡像從 AccountView 與 AccountManager 組成）。LoginExpiresAt＝Claude 憑證檔的 refreshTokenExpiresAt（WP3.4，只是參考值）；
/// ClaimedLogicalAccountId＝「在這台登入」宣告的邏輯帳戶（AgentAccount.LogicalAccountId）；OrgIdHint＝保守模式 org 不明時 CLI 設定的
/// organizationUuid（未確認，只用來判斷和目錄鍵的 org 不符，SEC-1；不進任何 payload）
/// </summary>
internal sealed record LocalAccountSnapshot(
    string Id,
    AgentProvider Provider,
    bool IsSystemDefault,
    string Label,
    string Color,
    int SortOrder,
    string Status,
    string? StatusText,
    string? Email,
    string? OrgId,
    string? Plan,
    long? AccessExpiresAt,
    bool LoginBusy,
    LinkSessions Sessions,
    long CheckedAt,
    long? LoginExpiresAt = null,
    string? ClaimedLogicalAccountId = null,
    string? OrgIdHint = null);

/// <summary>published 表的一列：上次送出的摘要與 identityKey</summary>
internal sealed record PublishedLink(string Digest, string? IdentityKey);

/// <summary>
/// 帳戶目錄的裝置端規則（sync-protocol 6.1、6.2；伺服器的檢查在 MyriagentHub 的 CatalogRules，這裡照同樣的上限先清理，
/// 被判 invalid 的事件會佔掉 deviceSeq、永不重送）
/// </summary>
internal static class CatalogSyncRules
{
    public const string CliCoalesceKey = "cli";

    /// <summary>app.state 的合併鍵（sync-protocol 的 app.state；同 cli.state 的做法）</summary>
    public const string AppCoalesceKey = "app";

    public const int MaxLabelLength = AccountRules.MaxCatalogLabelLength;

    public const int MaxEmailLength = 320;

    public const int MaxIdentityKeyLength = 400;

    public const int MaxTextLength = 500;

    public const int MaxShortTextLength = 128;

    public const int MaxSortOrder = 1_000_000;

    public const int MaxSessionCount = 100_000;

    public const int MaxLocalAccountIdLength = 64;

    public const string ConfigKindSystemDefault = "systemDefault";

    public const string ConfigKindIsolated = "isolated";

    public const string StatusChecking = "checking";

    public const string StatusReady = "ready";

    /// <summary>link.upsert 與 link.removed 共用（sync-protocol 6 的合併鍵）：之後的取代之前還沒送出的</summary>
    public static string LinkCoalesceKey(string localAccountId) => "link:" + localAccountId;

    public static string DeviceCoalesceKey(string deviceId) => "device:" + deviceId;

    /// <summary>
    /// account.update（名稱、顏色、順序共用一個鍵：規劃時把還沒送出的那筆的欄位併進新的，每個邏輯帳戶最多一筆 Pending）。
    /// 分成兩個鍵的話，改名與重排的兩筆帶同一個 baseVersion，伺服器接受第一筆之後第二筆必定 conflict。
    /// 合併鍵只在本機 outbox 用，不上線
    /// </summary>
    public static string AccountCoalesceKey(string logicalAccountId) => "account:" + logicalAccountId;

    public static string PrefCoalesceKey(string key) => "prefs:" + key;

    /// <summary>
    /// published 表裡 cli.state 那一列的鍵：「#」不在本機帳戶 id 的字元集裡（<see cref="IsLocalAccountId"/>），不會和帳戶撞名；
    /// 放在 published 表裡，伺服器換實例（BeginServerChange）與全部重送時跟著清掉
    /// </summary>
    public const string CliPublishedKey = "#cli";

    /// <summary>published 表裡 app.state 那一列的鍵（不會和帳戶 id 撞）</summary>
    public const string AppPublishedKey = "#app";

    /// <summary>cli.state 的摘要（SyncJson 序列化後的 SHA-256；Build 照 AgentProviders.All 的順序，同內容同摘要）</summary>
    public static string CliDigest(CliStatePayload state) => PayloadRules.Sha256Hex(SyncJson.Serialize(state));

    /// <summary>
    /// Claude 的身分鍵（D7／Q1）：org 已知→<c>claude:{org}:{email 小寫}</c>；org 未知但已發布過→email 相同沿用、不同送 <c>claude:{email 小寫}</c>
    /// （讓伺服器把連結標成身分不符）、email 未知沿用；都沒有→null（不發布）。先送 <c>claude:{email}</c> 再換成含 org 的鍵，
    /// 伺服器會把既有的連結標成身分不符，而且沒有升級規則，所以沒發布過、又不知道 org 時寧可不發布。
    /// <para>
    /// WP3.4（D11）：已對應或宣告了邏輯帳戶（<paramref name="catalogIdentityKey"/>＝目錄裡那個帳戶的身分鍵）、沒發布過、org 未知、email 已知時，
    /// 拿目錄鍵最後一個 <c>:</c> 之後的 email 比對（不分大小寫）：相同→null（伺服器在身分鍵是 null 時一律當成相符）；不同→<c>claude:{email 小寫}</c>，
    /// 讓伺服器把連結標成身分不符。否則保守模式（org 一直不明）登錯帳號也不會被發現
    /// </para>
    /// <para>
    /// SEC-1：email 相同時，再拿 <paramref name="orgIdHint"/>（保守模式從 CLI 設定讀到、還沒確認的 organizationUuid）比對目錄鍵的 org 段
    /// （<c>claude:</c> 之後第一個 <c>:</c> 與最後一個 <c>:</c> 之間；兩個是同一個位置就沒有 org 段）：目錄鍵有 org 段、提示合格而且不同
    /// （區分大小寫）→<c>claude:{email 小寫}</c>，讓伺服器標成身分不符（同一個信箱、不同 org 的登入不能連成相符）。提示只用來判斷不符，
    /// 絕不寫進身分鍵；其他情況照舊回 null
    /// </para>
    /// </summary>
    public static string? ClaudeIdentityKey(string? email, string? orgId, string? publishedIdentityKey, string? catalogIdentityKey = null,
        string? orgIdHint = null)
    {
        var mail = NormalizeEmail(email);
        var org = orgId?.Trim();
        if (mail is not null && !string.IsNullOrEmpty(org))
            return ValidKey($"claude:{org}:{mail}") ?? publishedIdentityKey;
        if (publishedIdentityKey is null)
        {
            if (mail is null || catalogIdentityKey is null)
                return null;
            var catalogEmail = catalogIdentityKey[(catalogIdentityKey.LastIndexOf(':') + 1)..];
            if (!string.Equals(catalogEmail, mail, StringComparison.OrdinalIgnoreCase))
                return ValidKey($"claude:{mail}");
            return CatalogOrg(catalogIdentityKey) is { } catalogOrg && AccountRules.IsValidOrgId(orgIdHint)
                   && !string.Equals(catalogOrg, orgIdHint, StringComparison.Ordinal)
                ? ValidKey($"claude:{mail}")
                : null;
        }
        if (mail is null)
            return publishedIdentityKey;
        var publishedEmail = publishedIdentityKey[(publishedIdentityKey.LastIndexOf(':') + 1)..];
        return string.Equals(publishedEmail, mail, StringComparison.Ordinal) ? publishedIdentityKey : ValidKey($"claude:{mail}") ?? publishedIdentityKey;
    }

    /// <summary>Claude 目錄鍵的 org 段（<c>claude:{org}:{email}</c> 的 {org}）；不是 <c>claude:</c> 開頭、沒有 org 段（<c>claude:{email}</c>）或 org 段是空的回 null</summary>
    private static string? CatalogOrg(string catalogIdentityKey)
    {
        const string prefix = "claude:";
        if (!catalogIdentityKey.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var first = prefix.Length - 1;
        var last = catalogIdentityKey.LastIndexOf(':');
        return last > first + 1 ? catalogIdentityKey[(first + 1)..last] : null;
    }

    /// <summary>Codex 的身分鍵：<c>codex:{email 小寫}</c>；email 未知回 null</summary>
    public static string? CodexIdentityKey(string? email) => EmailIdentityKey("codex:", email);

    /// <summary>
    /// 以信箱認身分的種類：Codex <c>codex:</c>、Grok <c>grok:</c>（<c>_x.ai/auth/info</c> 的信箱；X 帳號登入沒有信箱）、
    /// Antigravity <c>antigravity:</c>（agy 記錄裡登入成功的那一行，設計 2026-10-22-grok-antigravity-sync D1）＋email 小寫；email 未知回 null
    /// </summary>
    public static string? EmailIdentityKey(string prefix, string? email) => NormalizeEmail(email) is { } mail ? ValidKey(prefix + mail) : null;

    /// <summary>
    /// 一個本機帳戶的 link.upsert；不發布時回 null：還在檢查中、身分與連結都沒有、本機帳戶 id 不合伺服器的格式
    /// （Grok、Antigravity 要不要送由呼叫端依 prov1 決定，<see cref="CatalogSync.PlanAccounts"/>）。
    /// 文字欄位依伺服器的上限截斷，去掉控制字元與單獨的代理字元（伺服器的 IsOptionalText）。
    /// <paramref name="logicalAccountId"/>＝目錄的對應或「在這台登入」的宣告（<see cref="ResolveLinkLogical"/>）；
    /// <paramref name="catalogIdentityKey"/>＝目錄裡那個邏輯帳戶的身分鍵（只有 Claude 用，D11）
    /// </summary>
    public static LinkUpsertPayload? BuildLinkUpsert(LocalAccountSnapshot a, string? logicalAccountId, PublishedLink? published, string? catalogIdentityKey = null)
    {
        if (a.Status == StatusChecking || !IsLocalAccountId(a.Id))
            return null;
        var identityKey = a.Provider switch
        {
            AgentProvider.Claude => ClaudeIdentityKey(a.Email, a.OrgId, published?.IdentityKey, catalogIdentityKey, a.OrgIdHint),
            AgentProvider.Codex => CodexIdentityKey(a.Email),
            AgentProvider.Grok => EmailIdentityKey("grok:", a.Email),
            AgentProvider.Antigravity => EmailIdentityKey("antigravity:", a.Email),
            _ => throw new ArgumentOutOfRangeException(nameof(a), a.Provider, "沒有同步分類的代理種類"),
        };
        var logical = SyncIds.IsLogicalAccountId(logicalAccountId) ? logicalAccountId : null;
        if (identityKey is null && logical is null)
            return null;
        return new LinkUpsertPayload(
            a.Id,
            SyncProviderRules.WireName(a.Provider),
            a.IsSystemDefault ? ConfigKindSystemDefault : ConfigKindIsolated,
            identityKey,
            logical,
            IsStatus(a.Status) ? a.Status : "error",
            CleanText(a.StatusText, MaxTextLength),
            CleanText(a.Email, MaxEmailLength),
            CleanText(a.OrgId, MaxShortTextLength),
            CleanText(a.Plan, MaxShortTextLength),
            a.AccessExpiresAt,
            a.Provider switch
            {
                // 登入的到期只是參考值（D5）；超出範圍（網頁換不成日期）就不送。只有已登入（ready）才送：
                // 登入失效時 CLI 清掉權杖、檔案裡的 refreshTokenExpiresAt 還在，送了網頁會對失效的登入顯示「N 天後到期」
                AgentProvider.Claude => a.Status == StatusReady && a.LoginExpiresAt is > 0 and <= AccountIdentityRules.MaxUnixMs ? a.LoginExpiresAt : null,
                AgentProvider.Codex or AgentProvider.Grok or AgentProvider.Antigravity => null,
                _ => throw new ArgumentOutOfRangeException(nameof(a), a.Provider, "沒有同步分類的代理種類"),
            },
            a.LoginBusy,
            new LinkSessions(ClampCount(a.Sessions.Open), ClampCount(a.Sessions.Running), ClampCount(a.Sessions.Waiting), ClampCount(a.Sessions.Background),
                a.Sessions.Programs is > 0 and var programs ? ClampCount(programs) : null,
                a.Sessions.Subagents is > 0 and var subagents ? ClampCount(subagents) : null),
            CleanLabel(a.Label, a.Provider),
            NormalizeColor(a.Color) ?? AccountRules.SystemDefaultAppearance(a.Provider).Color,
            Math.Clamp(a.SortOrder, -MaxSortOrder, MaxSortOrder),
            a.CheckedAt);
    }

    /// <summary>
    /// 摘要（SyncJson 序列化後的 SHA-256）：不含 CheckedAt（變了不必重送）；已連結（目錄裡有這台的這一格）的帳戶另外不含 LogicalAccountId、
    /// Label、Color、SortOrder（伺服器只在新建或第一次連結時用，之後改名稱走 account.update）。
    /// 未連結的帳戶含 LogicalAccountId（WP3.4：「在這台登入」的宣告改變要重送；沒有宣告的是 null、序列化時省略，既有帳戶的摘要不變）
    /// </summary>
    public static string Digest(LinkUpsertPayload p, bool linked)
    {
        var json = JObject.FromObject(p, JsonSerializer.Create(SyncJson.Settings));
        json.Remove("checkedAt");
        if (linked)
        {
            json.Remove("logicalAccountId");
            json.Remove("label");
            json.Remove("color");
            json.Remove("sortOrder");
        }

        return PayloadRules.Sha256Hex(json.ToString(Formatting.None));
    }

    /// <summary>
    /// link.upsert 要帶的邏輯帳戶（WP3.4，D10）：目錄的對應（<paramref name="mapped"/>）優先，和伺服器的順序相同（先找這個本機帳戶已有的連結）。
    /// 沒有對應時才看「在這台登入」的宣告（<paramref name="claimed"/>），要全部成立：
    /// <list type="bullet">
    /// <item>格式合格、在目錄裡而且種類相同（<paramref name="catalogIdsSameProvider"/>）；</item>
    /// <item>這台還沒有連結到它（<paramref name="linkedHere"/>）；</item>
    /// <item>清單裡前面的帳戶還沒宣告它（<paramref name="claimedEarlier"/>，通過就加進去）。</item>
    /// </list>
    /// 伺服器以 (邏輯帳戶, 裝置) 為主鍵，後到的宣告會把前一個本機帳戶蓋掉，所以同一台同一個邏輯帳戶只認一個。不成立回 null（照身分鍵連結）
    /// </summary>
    public static string? ResolveLinkLogical(string? mapped, string? claimed, IReadOnlySet<string> catalogIdsSameProvider,
        IReadOnlySet<string> linkedHere, ISet<string> claimedEarlier)
    {
        if (mapped is not null)
            return mapped;
        if (!SyncIds.IsLogicalAccountId(claimed) || !catalogIdsSameProvider.Contains(claimed!) || linkedHere.Contains(claimed!))
            return null;
        return claimedEarlier.Add(claimed!) ? claimed : null;
    }

    /// <summary>發布過、現在不在本機清單的帳戶（送 link.removed）；帳戶清單唯讀時（accounts.json 讀不到）一律不算移除</summary>
    public static IReadOnlyList<string> RemovedLinks(IReadOnlyCollection<string> published, IReadOnlyCollection<string> current, bool listReadOnly)
    {
        if (listReadOnly)
            return [];
        var keep = current.ToHashSet(StringComparer.Ordinal);
        return published.Where(id => !keep.Contains(id)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// 帳戶名稱：去掉控制、格式字元與單獨的代理字元（LabelRules），超過 60 字以文字元素為界截斷（不切斷代理字元對與組合字）；
    /// 清理後是空的給供應商的預設名稱（「Claude 帳戶」）
    /// </summary>
    public static string CleanLabel(string? label, AgentProvider provider)
        => AccountRules.CleanCatalogLabel(label) ?? $"{AccountRules.ProviderName(provider)} 帳戶";

    /// <summary>#RGB→#RRGGBB、一律大寫；其他格式回 null（和 AccountManager 套用伺服器的顏色同一條規則）</summary>
    public static string? NormalizeColor(string? color) => AccountRules.NormalizeCatalogColor(color);

    /// <summary>
    /// 本機重排後要送的 account.update：把這些已連結帳戶原有的 sortOrder（由小到大，同分的往後推成嚴格遞增，<see cref="DistinctSlots"/>）
    /// 依新的本機順序重新分配，只回傳改變的。
    /// 不在 <paramref name="linked"/> 的本機帳戶（未連結）略過。<paramref name="resend"/> 裡的邏輯帳戶（outbox 還有沒送出的順序修改）
    /// 沒變也回傳：排回原位時要蓋掉那筆還沒送的（合併鍵相同），否則伺服器會收到中間的順序
    /// </summary>
    public static IReadOnlyList<(string LogicalAccountId, int SortOrder)> ReorderUpdates(
        IReadOnlyList<string> newLocalOrderOfLinked, IReadOnlyDictionary<string, (string LogicalAccountId, int SortOrder)> linked,
        Func<string, bool>? resend = null)
    {
        var order = newLocalOrderOfLinked.Where(linked.ContainsKey).Distinct(StringComparer.Ordinal).ToList();
        var slots = DistinctSlots(order.Select(id => linked[id].SortOrder).Order().ToList());
        var updates = new List<(string, int)>();
        for (var i = 0; i < order.Count; i++)
        {
            var (logical, current) = linked[order[i]];
            if (current != slots[i] || resend?.Invoke(logical) == true)
                updates.Add((logical, slots[i]));
        }

        return updates;
    }

    /// <summary>
    /// 由小到大的 sortOrder → 嚴格遞增的位置（同值往後推一格，夾在 ±<see cref="MaxSortOrder"/> 之內）。伺服器只在建立邏輯帳戶時寫
    /// sortOrder（建立那台的本機索引），兩台各自以索引 0 建立的帳戶就同分；同分的兩個帳戶對調時照原值分配的話一筆都不送，
    /// 下一次目錄套用又把順序改回去
    /// </summary>
    private static List<int> DistinctSlots(IReadOnlyList<int> sorted)
    {
        var slots = new List<int>(sorted.Count);
        foreach (var value in sorted)
            slots.Add(slots.Count == 0 ? value : Math.Max(value, slots[^1] + 1));
        // 推到上限之外時整段往下移（帳戶數遠小於範圍，不會撞到下限）
        var overflow = slots.Count == 0 ? 0 : slots[^1] - MaxSortOrder;
        if (overflow > 0)
        {
            for (var i = 0; i < slots.Count; i++)
                slots[i] = Math.Max(slots[i] - overflow, -MaxSortOrder);
        }

        return slots;
    }

    /// <summary>伺服器的本機帳戶 id 格式：1～64 個英數字、底線、連字號、句點</summary>
    public static bool IsLocalAccountId(string? id) =>
        id is { Length: > 0 and <= MaxLocalAccountIdLength }
        && id.AsSpan().IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.") < 0;

    /// <summary>伺服器的狀態格式：1～32 個英文字母</summary>
    public static bool IsStatus(string? status) =>
        status is { Length: > 0 and <= 32 } && status.AsSpan().IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz") < 0;

    /// <summary>
    /// 可省略的顯示文字：去掉控制字元與單獨的代理字元、去頭尾空白，超過上限截斷（不切斷代理字元對）；清理後是空的回 null
    /// </summary>
    public static string? CleanText(string? text, int maxLength)
    {
        if (text is null)
            return null;
        var builder = new StringBuilder(Math.Min(text.Length, maxLength + 1));
        var rest = text.AsSpan();
        while (!rest.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(rest, out var rune, out var consumed);
            if (status == System.Buffers.OperationStatus.Done && !Rune.IsControl(rune))
            {
                if (builder.Length + rune.Utf16SequenceLength > maxLength)
                    break;
                builder.Append(rest[..consumed]);
            }

            rest = rest[Math.Max(consumed, 1)..];
        }

        var cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string? NormalizeEmail(string? email)
    {
        var mail = email?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(mail) ? null : mail;
    }

    /// <summary>伺服器的身分鍵格式：不超過 400 字、沒有空白與控制字元</summary>
    private static string? ValidKey(string key)
    {
        if (key.Length > MaxIdentityKeyLength)
            return null;
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (char.IsHighSurrogate(c) && i + 1 < key.Length && char.IsLowSurrogate(key[i + 1]))
            {
                i++;
                continue;
            }

            // 單獨的代理字元轉 UTF-8 時會變成 U+FFFD，雜湊與顯示的就不同
            if (char.IsWhiteSpace(c) || char.IsControl(c) || char.IsSurrogate(c))
                return null;
        }

        return key;
    }

    private static int ClampCount(int value) => Math.Clamp(value, 0, MaxSessionCount);
}

/// <summary>cli.state 的規則（sync-protocol 6.5）：只送 Claude、Codex，欄位照伺服器的上限截斷</summary>
internal static class CliStateRules
{
    public const int MaxVersionLength = 64;

    public const int MaxInstallKindLength = 32;

    /// <summary>
    /// 中樞 CliView 的一個供應商（AgentHub.RenderCli＋AddCliRestartFields 的輸出）→ DeviceCliView；沒有 installed 欄位（不是 CliView）回 null
    /// </summary>
    public static DeviceCliView? FromCliView(JObject? providerView)
    {
        if (providerView?["installed"] is not JValue { Type: JTokenType.Boolean } installed)
            return null;
        return new DeviceCliView(
            (bool)installed,
            Text(providerView, "version", MaxVersionLength),
            Text(providerView, "installKind", MaxInstallKindLength),
            Text(providerView, "latest", MaxVersionLength),
            providerView["updateAvailable"] is JValue { Type: JTokenType.Boolean } update && (bool)update,
            Count(providerView, "runningOnOldVersion"),
            Count(providerView, "restartPending"),
            Text(providerView, "installBroken", CatalogSyncRules.MaxTextLength),
            providerView["checkedAt"] is JValue { Type: JTokenType.Integer, Value: long checkedAt } ? checkedAt : null);
    }

    /// <summary>四種都放（送出前再以 <see cref="ForServer"/> 依 prov1 篩），鍵是線上名稱、照 AgentProviders.All 的順序</summary>
    public static CliStatePayload Build(IReadOnlyDictionary<AgentProvider, DeviceCliView> views)
    {
        var providers = new Dictionary<string, DeviceCliView>(StringComparer.Ordinal);
        foreach (var provider in AgentProviders.All)
        {
            if (views.TryGetValue(provider, out var view))
                providers[SyncProviderRules.WireName(provider)] = view;
        }

        return new CliStatePayload(providers);
    }

    /// <summary>
    /// 要送給伺服器的 cli.state：沒有 prov1 時只留 Claude、Codex（舊伺服器的 cli.state 超過 2 種或有不認得的種類就整筆拒收，
    /// 連 Claude、Codex 的狀態也停住）。prov1 有無改變時內容跟著變、摘要不同，所以會重送
    /// </summary>
    public static CliStatePayload ForServer(CliStatePayload state, bool prov1)
    {
        var providers = new Dictionary<string, DeviceCliView>(StringComparer.Ordinal);
        foreach (var (name, view) in state.Providers)
        {
            if (SyncProviderRules.FromWireName(name) is { } provider && SyncProviderRules.Publishable(provider, prov1))
                providers[name] = view;
        }

        return providers.Count == state.Providers.Count ? state : new CliStatePayload(providers);
    }

    /// <summary>內容相同（鍵與每個 DeviceCliView 的欄位都相同；字典的順序不算）</summary>
    public static bool Same(CliStatePayload? a, CliStatePayload b)
    {
        if (a?.Providers is null || b.Providers is null || a.Providers.Count != b.Providers.Count)
            return false;
        foreach (var (key, view) in b.Providers)
        {
            if (!a.Providers.TryGetValue(key, out var other) || other != view)
                return false;
        }

        return true;
    }

    private static string? Text(JObject view, string key, int maxLength) =>
        view[key] is JValue { Type: JTokenType.String, Value: string text } ? CatalogSyncRules.CleanText(text, maxLength) : null;

    private static int Count(JObject view, string key) =>
        view[key] is JValue { Type: JTokenType.Integer, Value: long count } ? (int)Math.Clamp(count, 0, CatalogSyncRules.MaxSessionCount) : 0;
}

/// <summary>漫遊偏好（sync-protocol 6.3、SEC-24）：只有 PrefsRules 的四個鍵；套用前再檢查一次</summary>
internal static class PrefsSyncRules
{
    /// <summary>四個漫遊鍵的目前值（fontSize 是 JSON 整數：PrefsRules.IsValid 要求 Integer）</summary>
    public static IReadOnlyDictionary<string, JToken> Snapshot(AppSettings settings) => new Dictionary<string, JToken>(StringComparer.Ordinal)
    {
        ["theme"] = new JValue(settings.Theme),
        ["fontSize"] = new JValue((long)settings.FontSize),
        ["enterToSend"] = new JValue(settings.EnterToSend),
        ["notifications"] = new JValue(settings.Notifications),
    };

    /// <summary>改變的漫遊鍵（照 PrefsRules.Keys 的順序；before 沒有的鍵算改變）</summary>
    public static IReadOnlyList<(string Key, JToken Value)> Changed(IReadOnlyDictionary<string, JToken> before, IReadOnlyDictionary<string, JToken> after)
    {
        var changed = new List<(string, JToken)>();
        foreach (var key in PrefsRules.Keys)
        {
            if (!after.TryGetValue(key, out var value))
                continue;
            if (!before.TryGetValue(key, out var old) || !JToken.DeepEquals(old, value))
                changed.Add((key, value));
        }

        return changed;
    }

    /// <summary>伺服器發布的 prefs.set → 要套用的鍵與值；不在白名單或值的型別不對回 false（伺服器檢查過一次，這裡再檢查一次）</summary>
    public static bool TryFromWire(PrefsSetPayload? p, out string key, out JToken value)
    {
        key = string.Empty;
        value = JValue.CreateNull();
        if (p is null || !PrefsRules.IsValid(p.Key, p.Value))
            return false;
        key = p.Key;
        value = p.Value.DeepClone();
        return true;
    }
}
