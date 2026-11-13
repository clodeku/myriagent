using Myriagent.Core;

namespace Myriagent.Sync;

/// <summary>
/// 引擎 → 協調者的輸入框草稿（draft1；協調者的 EnginePort 實作；引擎以 <c>args.Host as IComposerDraftPort</c> 取得，沒有就不宣告 draft1）。任何執行緒呼叫
/// </summary>
internal interface IComposerDraftPort
{
    /// <summary>
    /// 伺服器送來的草稿（格式已檢查過）。<paramref name="pending"/> 是這台還沒送成功的對話（線上 id）：中樞不拿伺服器的蓋掉它們，
    /// 整份的（<paramref name="full"/>）也不因為沒列出就清掉
    /// </summary>
    void DeliverComposerDrafts(IReadOnlyList<ComposerDraftView> drafts, bool full, IReadOnlyCollection<string> pending);
}

/// <summary>協調者給引擎的通知（<c>engine as ISyncEngineComposerDrafts</c>）：這台某個對話的輸入框草稿變了（任何執行緒）</summary>
internal interface ISyncEngineComposerDrafts
{
    void ComposerDraftChanged(string conversationId, string text);
}

// 輸入框的草稿（sync-protocol 14.17，draft1）：中樞送來的變動記在 draftPending（每個對話只留最新的一份），協商出 draft1 的連線一則一則送
// （ComposerDraftPublish；同時只有一則在途，送成功而且期間沒再變才拿掉），沒有連線時留著等下一條連線。收到的 ComposerDraftUpdate 檢查格式後
// 連同還沒送成功的對話一起交給協調者。狀態只在引擎執行緒讀寫
internal sealed partial class SyncEngine : ISyncEngineComposerDrafts
{
    /// <summary>還沒送出的草稿最多記幾個對話（超過丟最早記的）</summary>
    internal const int MaxPendingDrafts = 200;

    private IComposerDraftPort? DraftsPort => args.Host as IComposerDraftPort;
    private readonly Dictionary<string, string> draftPending = new(StringComparer.Ordinal);
    private readonly List<string> draftOrder = [];
    private SyncConnection? draftConn;
    private bool draftSending;
    private bool draftInvalidLogged;

    public void ComposerDraftChanged(string conversationId, string text) => thread.Post(() =>
    {
        if (DraftsPort is null || ComposerDraftRules.Check(conversationId, text) is not null)
            return;
        draftPending[conversationId] = text;
        draftOrder.Remove(conversationId);
        draftOrder.Add(conversationId);
        while (draftOrder.Count > MaxPendingDrafts)
        {
            draftPending.Remove(draftOrder[0]);
            draftOrder.RemoveAt(0);
        }
        SendNextDraft();
    });

    /// <summary>
    /// Hello 的功能：有 port 才宣告 draft1；臨時對話的 quick1（這版的中樞收得了帶 ephemeral 的 conversationCreate，2026-11-11）跟著一起宣告——
    /// 兩個都由協調者的 EnginePort 接上，正式環境一定同時有；att1（網頁上傳的附件以上傳 id 移除，2026-11-13）也一起
    /// </summary>
    private void ContributeDraftFeatures(List<string> into)
    {
        if (DraftsPort is null)
            return;
        into.Add(DeviceFeatureRules.Draft1);
        into.Add(DeviceFeatureRules.Quick1);
        // 網頁上傳的附件以上傳 id 移除（2026-11-13，AgentHub.Files.cs 的 RemoveAttachment）
        into.Add(DeviceFeatureRules.Att1);
    }

    /// <summary><c>ComposerDraftUpdate</c>：格式不合格整則略過，否則連同還沒送成功的對話交給協調者</summary>
    private void OnComposerDraftUpdate(SyncConnection conn, ComposerDraftUpdate update)
    {
        if (DraftsPort is not { } port || conn.UpdateOnly)
            return;
        if (ComposerDraftRules.CheckUpdate(update) is { } problem)
        {
            Log.Warn($"伺服器送來格式不合格的 ComposerDraftUpdate：略過（{problem}）");
            return;
        }
        try
        {
            port.DeliverComposerDrafts(update.Drafts, update.Full, draftPending.Keys.ToList());
        }
        catch (Exception ex)
        {
            Log.Error("交出其他電腦的輸入框草稿失敗", ex);
        }
    }

    /// <summary>交握完成：這條連線協商出 draft1 就開始送還沒送出的</summary>
    private void StartDrafts(SyncConnection conn)
    {
        if (DraftsPort is null || conn.UpdateOnly || !DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Draft1))
            return;
        draftConn = conn;
        draftSending = false;
        draftInvalidLogged = false;
        SendNextDraft();
    }

    private void StopDrafts(SyncConnection conn)
    {
        if (!ReferenceEquals(draftConn, conn))
            return;
        draftConn = null;
        draftSending = false;
    }

    private void SendNextDraft()
    {
        if (draftConn is not { } conn || !IsCurrent(conn) || !conn.Proven || draftSending || draftOrder.Count == 0)
            return;
        var conversationId = draftOrder[0];
        var text = draftPending[conversationId];
        draftSending = true;
        Spawn(async () =>
        {
            var done = false;
            try
            {
                await conn.Session!.ComposerDraftPublishAsync(new ComposerDraftPublish(conversationId, text), conn.Token);
                done = true;
            }
            catch (SyncTransportException ex) when (ex.Failure.Kind == SyncFailureKind.ClientBug)
            {
                if (!draftInvalidLogged)
                    Log.Warn($"伺服器拒收這台的輸入框草稿（{ex.Failure.Code}）：丟掉這一則（這條連線之後不再記錄）");
                draftInvalidLogged = true;
                done = true;
            }
            catch (SyncTransportException ex)
            {
                draftSending = false;
                Fail(conn, ex.Failure);
                return;
            }
            draftSending = false;
            if (done && draftPending.TryGetValue(conversationId, out var now) && string.Equals(now, text, StringComparison.Ordinal))
            {
                draftPending.Remove(conversationId);
                draftOrder.Remove(conversationId);
            }
            if (IsCurrent(conn) && ReferenceEquals(draftConn, conn))
                SendNextDraft();
        }, conn);
    }
}
