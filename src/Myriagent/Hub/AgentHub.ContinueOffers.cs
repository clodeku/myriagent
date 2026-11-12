using Myriagent.Agents;
using Myriagent.Core;

namespace Myriagent.Hub;

// 標頭的「繼續」（換版、app 更新、app 重新啟動後沒有自動接續的對話）存成 conversations\{id}\continue.json，app 再重新啟動時還原
// （ContinueOfferStore.cs 說明起因）。標頭的結果（c.Restart.Result）每次被改就記成「動過」，MarkHeaderDirty 與 app 結束時對到檔案：
// 是可以按「繼續」的沒有接續就寫，其他的（已接續、失敗、清掉）就刪。只在 UI 執行緒
internal sealed partial class AgentHub
{
    private sealed partial class ConversationState
    {
        /// <summary>continue.json 在磁碟上（或可能在：這次啟動讀不到的檔也算，之後照樣覆寫或刪）</summary>
        public bool ContinueOfferOnDisk;

        /// <summary>上次寫出的內容（不含時間）：一樣的不重寫</summary>
        public string? ContinueOfferFingerprint;
    }

    /// <summary>
    /// 建構中樞時讀 continue.json：還原標頭的「繼續」（不加提示：給「繼續」的那次已經加過）。之後主視窗就緒時 app 更新的接續、
    /// 上次中斷的接續照常處理，它們有新的決定就蓋過這個
    /// </summary>
    private void LoadContinueOffer(ConversationState c)
    {
        var (status, record) = ContinueOfferStore.TryLoad(store.GetConversationDirectory(c.Meta.Id));
        c.ContinueOfferOnDisk = status is JsonReadStatus.Ok or JsonReadStatus.Unreadable;
        if (record == null)
            return;
        c.Restart.Result = new RestartResultView(CliRestartState.NotContinued, record.From, record.To, record.Detail!, CanContinue: true,
            ContinueOfferRules.Context(c.Meta.Provider, record));
        c.Restart.ResultTouched = false;
        c.ContinueOfferFingerprint = ContinueOfferRules.Fingerprint(record);
    }

    /// <summary>
    /// 標頭的結果動過之後（MarkHeaderDirty、app 結束時）：可以按「繼續」的沒有接續寫 continue.json，其他的刪掉。
    /// 草稿、其他電腦的對話、已從中樞移除的（刪除中：寫了會重建資料夾）不寫。寫不了、刪不了只記警告（這次照常，只是 app 重開後沒有「繼續」）
    /// </summary>
    private void SyncContinueOffer(ConversationState c)
    {
        c.Restart.ResultTouched = false;
        try
        {
            if (c.Restart.Result is { State: CliRestartState.NotContinued, CanContinue: true } result && !c.IsDraft && !c.IsReplica && IsLive(c))
            {
                var record = ContinueOfferRules.ToRecord(result.From, result.To, result.Detail, result.Continuation, DateTimeOffset.Now);
                var fingerprint = ContinueOfferRules.Fingerprint(record);
                if (c.ContinueOfferOnDisk && fingerprint == c.ContinueOfferFingerprint)
                    return;
                ContinueOfferStore.Save(store.GetConversationDirectory(c.Meta.Id), record);
                c.ContinueOfferOnDisk = true;
                c.ContinueOfferFingerprint = fingerprint;
            }
            else if (c.ContinueOfferOnDisk && !c.IsReplica)
            {
                ContinueOfferStore.Delete(store.GetConversationDirectory(c.Meta.Id));
                c.ContinueOfferOnDisk = false;
                c.ContinueOfferFingerprint = null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"更新「繼續」的記錄失敗（{c.Meta.Id}）：{ex.Message}");
        }
    }

    /// <summary>app 結束（或 Windows 詢問能不能關機）時：標頭的結果動過、還沒對到檔案的補上</summary>
    private void SyncContinueOffersOnExit()
    {
        foreach (var c in conversations.Values.Where(c => c.Restart.ResultTouched).ToList())
            SyncContinueOffer(c);
    }
}
