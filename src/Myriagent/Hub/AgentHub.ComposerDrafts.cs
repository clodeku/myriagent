using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 輸入框的草稿（2026-11-10 使用者要求：在某台電腦或手機打到一半沒送出的字，其他電腦與桌面版也要看得到；sync-protocol 14.17、web-protocol 的
// composerDraft／composerDrafts）。網頁打字停下來一會兒送 composerDraft，中樞記下、轉給這台的其他視窗，可以同步的對話再交給同步引擎；
// 伺服器送來的（其他電腦、瀏覽器版）轉給這台的每個視窗。誰蓋過誰由網頁決定（Web/draftsyncrules.js：沒改過就換、正在打的不蓋掉，改成提示）。
// 中樞只記每個對話最後一份（線上 id → 草稿），不存檔：app 重開後由伺服器的整份補回來。只在 UI 執行緒
internal sealed partial class AgentHub
{
    /// <summary>每個對話最後一份草稿（線上 id）。Rev 是伺服器給的；這台打的是 0（整份的沒列出時只清掉伺服器來的）</summary>
    private readonly Dictionary<string, ComposerDraftView> syncedComposerDrafts = new(StringComparer.Ordinal);

    /// <summary>網頁的 composerDraft：對話不在這台（或是還沒建立的草稿對話）、文字太長一律略過</summary>
    private void ComposerDraftCommand(IAgentView view, string? conversationId, string? text)
    {
        if (conversationId is null || text is null || text.Length > ComposerDraftRules.MaxTextChars)
            return;
        if (!conversations.TryGetValue(conversationId, out var c) || c.IsDraft || ConversationIds.ToWire(c.Meta.Id) is not { } wire)
            return;
        var empty = ComposerDraftRules.IsEmpty(text);
        syncedComposerDrafts.TryGetValue(wire, out var known);
        if (empty ? known is null : known is not null && string.Equals(known.Text, text, StringComparison.Ordinal))
            return;
        var draft = new ComposerDraftView(wire, empty ? "" : text, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null);
        if (empty)
            syncedComposerDrafts.Remove(wire);
        else
            syncedComposerDrafts[wire] = draft;
        PostComposerDrafts([draft], full: false, except: view);
        if (CanSyncComposerDraft(c) && ComposerDraftRules.Syncable(draft.Text))
            sync?.OnComposerDraft(wire, draft.Text);
    }

    /// <summary>
    /// 這個對話的草稿能不能送上伺服器：這台的對話要同步政策是 full（內容本來就上線）；其他電腦的對話要擁有者同步了內容
    /// （metadata、paused 的對話內容不上線，打的字也不送）
    /// </summary>
    private bool CanSyncComposerDraft(ConversationState c)
        => c.IsReplica ? c.ReplicaItemsAvailable : EffectiveSyncPolicy(c) == ConversationSyncPolicies.Full;

    /// <summary>
    /// 伺服器送來的草稿（協調者在 UI 執行緒呼叫）：比這台記得的舊的不要；這台還沒送成功的（<paramref name="pending"/>）不蓋掉；
    /// 整份的（<paramref name="full"/>）沒列出的、伺服器來的那幾則當成已經清掉。變了的轉給每個視窗
    /// </summary>
    internal void ApplyComposerDrafts(IReadOnlyList<ComposerDraftView> drafts, bool full, IReadOnlyCollection<string> pending)
    {
        var skip = pending as IReadOnlySet<string> ?? new HashSet<string>(pending, StringComparer.Ordinal);
        var changed = new List<ComposerDraftView>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in drafts)
        {
            seen.Add(d.ConversationId);
            if (skip.Contains(d.ConversationId))
                continue;
            syncedComposerDrafts.TryGetValue(d.ConversationId, out var known);
            if (known is not null && known.Rev > d.Rev)
                continue;
            if (ComposerDraftRules.IsEmpty(d.Text))
            {
                if (known is null)
                    continue;
                syncedComposerDrafts.Remove(d.ConversationId);
                changed.Add(d with { Text = "" });
                continue;
            }
            if (known is not null && known.Rev == d.Rev && string.Equals(known.Text, d.Text, StringComparison.Ordinal))
                continue;
            syncedComposerDrafts[d.ConversationId] = d;
            changed.Add(d);
        }
        if (full)
        {
            foreach (var gone in syncedComposerDrafts.Values.Where(k => k.Rev > 0 && !seen.Contains(k.ConversationId) && !skip.Contains(k.ConversationId)).ToList())
            {
                syncedComposerDrafts.Remove(gone.ConversationId);
                changed.Add(gone with { Text = "" });
            }
        }
        if (changed.Count > 0)
            PostComposerDrafts(changed, full: false, except: null);
    }

    /// <summary>網頁 init 之後：這台記得的草稿整份送給那個視窗</summary>
    private void PostComposerDraftsTo(IAgentView view)
    {
        if (view.Id == SyncViewId || syncedComposerDrafts.Count == 0)
            return;
        Post(view, ComposerDraftsMessage(syncedComposerDrafts.Values.OrderBy(d => d.Rev), full: true));
    }

    /// <summary>送給這台的每個視窗（同步鏡像與 <paramref name="except"/> 除外）</summary>
    private void PostComposerDrafts(IReadOnlyList<ComposerDraftView> drafts, bool full, IAgentView? except)
    {
        var targets = views.Values.Where(v => v.Id != SyncViewId && !ReferenceEquals(v, except)).ToList();
        if (targets.Count > 0)
            TryPost(targets, () => ComposerDraftsMessage(drafts, full));
    }

    /// <summary>
    /// web-protocol 的 <c>syncedComposerDrafts</c>（同伺服器給瀏覽器的形狀）：對話 id 換成這台網頁用的（這台的對話是資料夾名，其他電腦的是線上 id）；
    /// from 是 null 時是這台的另一個視窗
    /// </summary>
    private JObject ComposerDraftsMessage(IEnumerable<ComposerDraftView> drafts, bool full)
    {
        var list = new JArray();
        foreach (var d in drafts)
        {
            list.Add(new JObject
            {
                ["conversationId"] = FindByWireId(d.ConversationId)?.Meta.Id ?? d.ConversationId,
                ["text"] = d.Text,
                ["rev"] = d.Rev,
                ["at"] = d.UpdatedAt,
                ["from"] = d.From,
            });
        }
        return new JObject { ["type"] = "composerDrafts", ["full"] = full, ["drafts"] = list };
    }
}
