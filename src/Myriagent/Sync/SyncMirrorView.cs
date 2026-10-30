using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Rendering;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>
/// 中樞的鏡像視窗（看不到的 view，AgentHub.AttachSyncView 掛上）：把中樞本來就會送給網頁的 accounts、cli 訊息轉成同步的快照交給
/// <see cref="HubBridge"/>，中樞不必為同步多算一份。
/// <list type="bullet">
/// <item>只讀不改：中樞把同一個 JObject 發給所有 view（AgentHub.TryPost），改了會讓其他視窗看到。</item>
/// <item>永遠不送 ready：拿不到 init，也不會搶走只發給第一個就緒視窗的啟動警告；不實作 IPopoutView。</item>
/// <item>例外不往外丟：TryPost 對每個 view 依序呼叫，鏡像丟例外會讓排在後面的視窗收不到這則訊息。</item>
/// </list>
/// 只在 UI 執行緒呼叫（中樞的狀態只在 UI 執行緒）
/// </summary>
internal sealed class SyncMirrorView : IAgentView
{
    public const string ViewId = AgentHub.SyncViewId;

    private readonly AccountManager accounts;
    private readonly HubBridge bridge;
    private readonly Dictionary<string, long> usageFetchedAt = new(StringComparer.Ordinal);

    public SyncMirrorView(AccountManager accounts, HubBridge bridge)
    {
        this.accounts = accounts;
        this.bridge = bridge;
    }

    public string Id => ViewId;

    public bool IsPopout => false;

    /// <summary>
    /// accounts→LocalAccountSnapshot（略過 remoteOnly；CheckedAt、OrgId、AccessExpiresAt、LoginExpiresAt、宣告的邏輯帳戶取自 AccountManager，SortOrder＝清單索引）；
    /// cli→CliStateRules；帳戶的 usage.fetchedAt 改變→OfferUsage(AccountManager.GetUsage)。其他種類略過
    /// </summary>
    public void PostToWeb(JObject message)
    {
        try
        {
            switch (message?.Value<string>("type"))
            {
                case "accounts":
                    if (message["accounts"] is JArray list)
                        OnAccounts(list);
                    break;
                case "cli":
                    if (message["cli"] is JObject cli)
                        OnCli(cli);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("同步鏡像處理中樞訊息失敗", ex);
        }
    }

    private void OnAccounts(JArray list)
    {
        var snapshot = new List<LocalAccountSnapshot>(list.Count);
        var usage = new List<(string Id, long FetchedAt)>();
        foreach (var token in list)
        {
            if (token is not JObject item || IsTrue(item, "remoteOnly"))
                continue;
            if (Text(item, "id") is not { Length: > 0 } id || !TryProvider(Text(item, "provider"), out var provider))
                continue;
            var identity = accounts.GetSyncIdentity(id);
            var sessions = item["sessions"] as JObject;
            snapshot.Add(new LocalAccountSnapshot(
                id,
                provider,
                IsTrue(item, "isSystemDefault"),
                Text(item, "label") ?? string.Empty,
                Text(item, "color") ?? string.Empty,
                snapshot.Count,
                Text(item, "status") ?? "error",
                Text(item, "statusText"),
                Text(item, "email"),
                identity.OrgId,
                Text(item, "plan"),
                identity.AccessExpiresAt,
                IsTrue(item, "loginBusy"),
                new LinkSessions(Count(sessions, "open"), Count(sessions, "running"), Count(sessions, "waiting"), Count(sessions, "background"),
                    Count(sessions, "programs") is > 0 and var programs ? programs : null,
                    Count(sessions, "subagents") is > 0 and var subagents ? subagents : null),
                ProtocolValues.ToUnixMs(accounts.GetState(id).CheckedAt) ?? 0,
                identity.LoginExpiresAt,
                identity.ClaimedLogicalAccountId,
                identity.OrgIdHint));
            // 四種都交給引擎（Grok、Antigravity 沒有 prov1 時沒有連結，用量不上傳；Antigravity 沒有額度，沒有 fetchedAt）
            if (item["usage"] is JObject u && u["fetchedAt"] is JValue { Type: JTokenType.Integer, Value: long fetchedAt })
                usage.Add((id, fetchedAt));
        }

        bridge.OfferAccounts(snapshot, accounts.IsAccountListReadOnly);

        var present = usage.Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in usageFetchedAt.Keys.Where(id => !present.Contains(id)).ToList())
            usageFetchedAt.Remove(gone);
        foreach (var (id, fetchedAt) in usage)
        {
            if (usageFetchedAt.TryGetValue(id, out var last) && last == fetchedAt)
                continue;
            if (accounts.GetUsage(id) is not { } current)
                continue;
            usageFetchedAt[id] = fetchedAt;
            bridge.OfferUsage(id, current);
        }
    }

    private void OnCli(JObject cli)
    {
        var views = new Dictionary<AgentProvider, DeviceCliView>();
        // 四種都放，引擎送出前依 prov1 篩（CliStateRules.ForServer）
        foreach (var provider in AgentProviders.All)
        {
            if (CliStateRules.FromCliView(cli[provider.ToString()] as JObject) is { } view)
                views[provider] = view;
        }

        bridge.OfferCli(CliStateRules.Build(views));
    }

    private static bool TryProvider(string? name, out AgentProvider provider)
    {
        provider = default;
        return name is { Length: > 0 } && char.IsLetter(name[0])
            && Enum.TryParse(name, ignoreCase: false, out provider) && Enum.IsDefined(provider);
    }

    private static string? Text(JObject item, string key) =>
        item[key] is JValue { Type: JTokenType.String, Value: string text } ? text : null;

    private static bool IsTrue(JObject item, string key) =>
        item[key] is JValue { Type: JTokenType.Boolean, Value: true };

    private static int Count(JObject? item, string key) =>
        item?[key] is JValue { Type: JTokenType.Integer, Value: long count } ? (int)Math.Clamp(count, 0, int.MaxValue) : 0;
}
