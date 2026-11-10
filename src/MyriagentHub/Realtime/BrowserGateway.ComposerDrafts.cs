using Myriagent.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Realtime;

/// <summary>
/// 輸入框的草稿給瀏覽器（draft1，sync-protocol 14.17；web-protocol 的 <c>composerDrafts</c>）：擁有者的連線送過 init 之後收一份整份的
/// （<c>full:true</c>），之後每次有草稿變了收那一則（送來的那條連線不送回去）。協作人一律不送（草稿是擁有者打的字）。
/// 不是快照的一部分：溢出重送 init 時跟著 init 再送一份整份的
/// </summary>
internal sealed partial class BrowserGateway
{
    public const string ComposerDraftsType = "composerDrafts";

    /// <summary>送來的那條瀏覽器連線的 origin（<see cref="MyriagentHub.Sync.ComposerDraftStore.Changed"/>）</summary>
    public static string BrowserOrigin(string connectionId) => "b:" + connectionId;

    /// <summary><c>{type:"composerDrafts", full, drafts:[{conversationId, text, rev, at, from}]}</c></summary>
    internal static ClientOutboxMessage ComposerDraftsMessage(IEnumerable<ComposerDraftView> drafts, bool full)
    {
        var list = new JArray();
        foreach (var d in drafts)
        {
            list.Add(new JObject
            {
                ["conversationId"] = d.ConversationId,
                ["text"] = d.Text,
                ["rev"] = d.Rev,
                ["at"] = d.UpdatedAt,
                ["from"] = d.From,
            });
        }
        var message = new JObject { ["type"] = ComposerDraftsType, ["full"] = full, ["drafts"] = list };
        return new ClientOutboxMessage(ComposerDraftsType, message.ToString(Formatting.None));
    }

    /// <summary>草稿變了（呼叫 Publish 的執行緒）：擁有者的每一條連線（送來的那條除外）入列那一則</summary>
    internal void OnComposerDraftChanged(ComposerDraftView view, string? origin)
    {
        var message = ComposerDraftsMessage([view], full: false);
        foreach (var connection in connections.Snapshot())
        {
            if (connection.IsCollaborator || origin == BrowserOrigin(connection.ConnectionId))
                continue;
            SendAfterInit(connection, message);
        }
    }

    /// <summary>init 剛入列（迴圈裡）：擁有者的連線接著收一份整份的（伺服器一則都沒有時不送）。溢出要重送 init 時回 true</summary>
    private bool EnqueueComposerDrafts(BrowserConnection connection, long nowMs)
    {
        if (composerDrafts is null || connection.IsCollaborator)
            return false;
        var all = composerDrafts.Snapshot();
        if (all.Count == 0)
            return false;
        return Enqueue(connection, ComposerDraftsMessage(all, full: true), nowMs).Resync;
    }
}
