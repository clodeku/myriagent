namespace Myriagent.Sync;

// 協調者的輸入框草稿（sync-protocol 14.17，draft1）：中樞的草稿變了（ISyncBridge.OnComposerDraft）就交給引擎排著送；
// 伺服器送來的回 UI 執行緒交給中樞（舊一代的丟掉）
internal sealed partial class SyncCoordinator
{
    void ISyncBridge.OnComposerDraft(string conversationId, string text) => (engine as ISyncEngineComposerDrafts)?.ComposerDraftChanged(conversationId, text);

    private void OnComposerDrafts(int gen, IReadOnlyList<ComposerDraftView> drafts, bool full, IReadOnlyCollection<string> pending) => host.BeginInvoke(() =>
    {
        if (disposed || gen != Volatile.Read(ref generation))
            return;
        hub.ApplyComposerDrafts(drafts, full, pending);
    });

    private sealed partial class EnginePort : IComposerDraftPort
    {
        public void DeliverComposerDrafts(IReadOnlyList<ComposerDraftView> drafts, bool full, IReadOnlyCollection<string> pending)
            => owner.OnComposerDrafts(gen, drafts, full, pending);
    }
}
