using System.Diagnostics.CodeAnalysis;
using Myriagent.Agents;
using Myriagent.Sync;
using MyriagentHub.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Realtime;

/// <summary>草稿可以用的帳戶（閘道最後一份帳戶快照的邏輯帳戶 id 與代理種類，依顯示順序）</summary>
internal sealed record DraftAccount(string Id, AgentProvider Provider);

/// <summary>
/// 一份伺服器草稿（WP4.2 計畫步 13；D29）：瀏覽器的 <c>newConversation</c> 開的、還沒送出第一則的新對話，只存在記憶體。
/// WP4.5 的草稿指令（setOption、setAccount、setFolder、resetFolder、rename）經 <see cref="IBrowserDrafts.TryUpdate"/> 以 <c>with</c> 改它，
/// 第一次 send 時轉成正式對話。模型、強度、權限模式是 null＝帳戶預設（同標頭的「預設」）
/// </summary>
internal sealed record BrowserDraft(string Id, string AccountId, AgentProvider Provider)
{
    public string? Model { get; init; }

    public string? Effort { get; init; }

    /// <summary>權限模式的名稱（<see cref="AgentPermissionMode"/>；null＝每步詢問，同 <see cref="ConversationViewRules.Permission"/>）</summary>
    public string? PermissionMode { get; init; }

    public string? WorkingDirectory { get; init; }

    public bool CustomFolder { get; init; }

    /// <summary>草稿的標題（WP4.5 的 <c>rename</c>；null＝<see cref="BrowserDraftRules.Title"/>，第一次送出時不帶 <c>title</c>）</summary>
    public string? Title { get; init; }

    /// <summary>新對話要在哪台電腦建立（<c>newConversation</c> 選填的 <c>deviceId</c>，WP4.5 計畫 K8；null＝送出時照預設挑一台）</summary>
    public string? DeviceId { get; init; }

    /// <summary>第一次 send 送出的 <c>conversationCreate</c> 的 cid（WP4.5 計畫 D33：列提交之前記下，rejected 時清掉；有值時再送 send 回 busy）</summary>
    public string? SentCommandId { get; init; }

    /// <summary>新對話的線上 id（<c>RemoteConversationIdRules.FromCommandId(SentCommandId)</c>；和 <see cref="SentCommandId"/> 一起設、一起清）</summary>
    public string? PendingConversationId { get; init; }

    /// <summary>
    /// 臨時對話（2026-11-11，瀏覽器的 <c>quickChat</c>）：第一次送出時 conversationCreate 帶 <c>ephemeral</c>，那台建成不列在清單、
    /// 關掉面板就刪掉的對話（要那台協商出 quick1）
    /// </summary>
    public bool Ephemeral { get; init; }

    /// <summary>最近使用的順序（數字越大越新；由 <see cref="BrowserDrafts"/> 維護，超過上限時丟最小的）</summary>
    public long Used { get; init; }
}

/// <summary>
/// 伺服器草稿的純函式規則（WP4.2 計畫步 13；D29；web-protocol 的 newConversation、conversation、conversationClosed）。
/// <list type="bullet">
/// <item>id 是 <c>d</c> 加 26 字的 ULID（Crockford Base32）：27 字、大寫，和 32 個小寫十六進位的對話 id（<see cref="ConversationIds.IsWireId"/>）看格式就分得開，
/// 所以 <c>visible</c> 與 <c>openConversationIds</c> 本來就把它濾掉（不算看得到、不進 WatchChanged、不標未讀）。</item>
/// <item>帳戶：要求的是快照裡的邏輯帳戶就用它，否則用第一個；一個帳戶都沒有就不開（同桌面的「請先新增帳戶」；網頁自動送的，不提示）。</item>
/// <item>標頭與正式對話的標頭同一組鍵（<see cref="ConversationViewRules.Header"/>），<c>isDraft</c> 是 true、標題是「新對話」、沒有狀態列與裝置。</item>
/// </list>
/// </summary>
internal static class BrowserDraftRules
{
    /// <summary>草稿 id 的前綴</summary>
    public const char Prefix = 'd';

    /// <summary>草稿 id 的長度（前綴＋26 字的 ULID）</summary>
    public const int IdLength = 1 + SyncIds.RandomChars;

    /// <summary>草稿的標題（同桌面 AgentHub.CreateDraft）</summary>
    public const string Title = "新對話";

    /// <summary><c>paneId</c> 最多幾個字（網頁自己產生的窗格 id；超過的當成沒帶）</summary>
    public const int MaxPaneIdLength = 128;

    /// <summary>新的草稿 id：<c>d</c> 加 26 字的 ULID（時間在前、80 位元亂數）</summary>
    public static string NewId(long unixMs) => Prefix + SyncIds.NewUlid(unixMs);

    /// <summary>是草稿 id 的形狀嗎（<c>d</c> 加合格的 ULID）</summary>
    public static bool IsDraftId([NotNullWhen(true)] string? value) =>
        value is { Length: IdLength } && value[0] == Prefix && SyncIds.IsUlid(value[1..]);

    /// <summary>要求的帳戶在 <paramref name="accounts"/> 裡就用它（完全相同才算），否則用第一個；一個都沒有回 null</summary>
    public static DraftAccount? PickAccount(string? requested, IReadOnlyList<DraftAccount> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        if (requested is not null)
        {
            foreach (var account in accounts)
            {
                if (string.Equals(account.Id, requested, StringComparison.Ordinal))
                    return account;
            }
        }
        return accounts.Count > 0 ? accounts[0] : null;
    }

    /// <summary>超過 <paramref name="max"/> 份時要丟的草稿：最久沒用的（<see cref="BrowserDraft.Used"/> 最小的）先丟，同值依 id</summary>
    public static IReadOnlyList<BrowserDraft> Evict(IReadOnlyCollection<BrowserDraft> drafts, int max)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        if (drafts.Count <= max)
            return [];
        return drafts.OrderBy(d => d.Used).ThenBy(d => d.Id, StringComparer.Ordinal).Take(drafts.Count - max).ToList();
    }

    /// <summary>工作階段沒有連線之後過了寬限（<paramref name="graceSeconds"/> 秒，含剛好等於）：草稿整批丟掉</summary>
    public static bool GraceOver(TimeSpan sinceEmpty, int graceSeconds) => sinceEmpty >= TimeSpan.FromSeconds(graceSeconds);

    /// <summary>換草稿的墓碑至少留多久（秒；WP4.6 合併前審查 LIVE-1）</summary>
    public const int ReplacedKeepMinSeconds = 600;

    /// <summary>換草稿的墓碑留多久（秒）：至少 <see cref="ReplacedKeepMinSeconds"/>，也不短於草稿的斷線寬限（寬限內重連的網頁一定拿得到）</summary>
    public static int ReplacedKeepSeconds(int graceSeconds) => Math.Max(graceSeconds, ReplacedKeepMinSeconds);

    /// <summary>每個工作階段最多留幾筆換草稿的墓碑：草稿上限的兩倍（至少 2）</summary>
    public static int MaxReplacedPerSession(int maxDraftsPerSession) => Math.Max(1, maxDraftsPerSession) * 2;

    /// <summary>
    /// 帳戶快照（<see cref="AccountViewRules"/> 組的 AccountView 陣列）裡的 <c>id</c> 與 <c>provider</c>，依原本的順序；
    /// JSON 不合格回空的，id 不是非空字串或代理種類不認得的那一筆略過
    /// </summary>
    public static IReadOnlyList<DraftAccount> AccountsOf(string accountsJson)
    {
        ArgumentNullException.ThrowIfNull(accountsJson);
        JArray array;
        try
        {
            array = JArray.Parse(accountsJson);
        }
        catch (JsonException)
        {
            return [];
        }
        var accounts = new List<DraftAccount>(array.Count);
        foreach (var token in array)
        {
            if (token is not JObject account
                || account["id"] is not JValue { Type: JTokenType.String } id || (string?)id is not { Length: > 0 } idText
                || account["provider"] is not JValue { Type: JTokenType.String } provider || ConversationViewRules.ProviderOf((string?)provider) is not { } kind)
                continue;
            accounts.Add(new DraftAccount(idText, kind));
        }
        return accounts;
    }

    /// <summary>newConversation 帶來的 <c>paneId</c>：1～<see cref="MaxPaneIdLength"/> 字的字串原樣送回，其他當成沒帶（null）</summary>
    public static string? PaneId(JToken? token) =>
        token is JValue { Type: JTokenType.String } value && (string?)value is { Length: > 0 and <= MaxPaneIdLength } paneId ? paneId : null;

    /// <summary>
    /// 草稿的標頭：同正式對話的鍵（D21），<c>isDraft</c> 是 true、標題「新對話」（WP4.5 的 rename 之後是改的標題）、不在執行、沒有狀態列。
    /// <c>deviceId</c> 是 newConversation 選的電腦（WP4.6 D16；沒選是 null），<c>deviceName</c>／<c>deviceOnline</c> 不填（網頁從 <c>remote.devices</c> 查）
    /// </summary>
    public static JObject Header(BrowserDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var row = new ConversationHeaderRow(draft.Id, OwnerDeviceId: null, draft.AccountId, draft.Provider, draft.Title ?? Title, draft.Model, draft.Effort, draft.PermissionMode,
            draft.CustomFolder, draft.WorkingDirectory, Running: false, StatusText: null, TurnStartedAt: null, LastTurn: null, LeaseState: null,
            ConversationSyncPolicies.Full, Epoch: 0, LastSeq: 0, Ephemeral: draft.Ephemeral);
        var header = ConversationViewRules.Header(row, ConversationDeviceInfo.None, rebuilding: false);
        header["isDraft"] = true;
        header["deviceId"] = draft.DeviceId is { } deviceId ? new JValue(deviceId) : JValue.CreateNull();
        // 沒有擁有者裝置時 Header 的狀態列是「電腦離線」：草稿還沒有電腦，不顯示
        header["status"] = JValue.CreateNull();
        return header;
    }

    /// <summary>草稿的 <c>conversation</c>（newConversation 的回應與 open 草稿）：沒有項目、空的輸入區；<paramref name="paneId"/> 原樣送回</summary>
    public static ClientOutboxMessage Message(BrowserDraft draft, string? paneId) =>
        ConversationGatewayRules.ConversationMessage(draft.Id, Header(draft).ToString(Formatting.None), [], hasMore: false,
            ConversationViewRules.Composer(null).ToString(Formatting.None), paneId);
}
