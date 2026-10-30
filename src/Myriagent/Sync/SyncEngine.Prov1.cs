using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;

namespace Myriagent.Sync;

// Grok、Antigravity 的同步閘門（設計 docs/plans/2026-10-22-grok-antigravity-sync.md 第 3 節；DeviceFeatureRules.Prov1）：
// 這兩種的帳戶、cli.state 與對話事件只在協商出 prov1 的連線上送（離線時看上一條連線，kv prov1_armed）。沒有 prov1 時延後——
// 不是 localOnly（那會送伺服器刪除）：對話照「沒有 conv1」的做法記重整、不寫事件，待送的列在送出前換成 {}（伺服器以 invalid 擋下後重整），
// 帳戶忘掉 published 摘要；prov1 回來時重算目錄、重整對話。第一次協商出 prov1 時把對話中繼資料的游標歸零一次，
// 補拉舊版當時略過的這兩種的對話（舊版的 ConversationEventRules 只收 Claude、Codex，略過之後游標照樣前進）。
internal sealed partial class SyncEngine
{
    /// <summary>kv prov1_armed：上一條交握完成的連線有沒有 prov1（離線時看它，預設 false）</summary>
    private bool? prov1Armed;

    /// <summary>這次啟動延後過 Grok、Antigravity 的對話：下一條 prov1 連線進 Live 時重整一次（找出延後期間新建、沒寫過事件的）</summary>
    private bool prov1Deferred;

    /// <summary>有 prov1 的連線，或離線時上一條連線有 prov1</summary>
    private bool CanPublishProv1() =>
        current is { Proven: true } conn ? DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Prov1) : Prov1Armed();

    private bool Prov1Armed()
    {
        if (prov1Armed is null)
        {
            try
            {
                prov1Armed = store?.GetKv(SyncStoreSchema.Prov1Armed) == "1";
            }
            catch (Exception ex)
            {
                Log.Warn($"讀取 prov1_armed 失敗（當成 false）：{ex.Message}");
                return false;
            }
        }

        return prov1Armed.Value;
    }

    private void SetProv1Armed(bool value)
    {
        if (Prov1Armed() == value && prov1Armed is not null)
            return;
        try
        {
            store!.SetKv(SyncStoreSchema.Prov1Armed, value ? "1" : "0");
            prov1Armed = value;
        }
        catch (Exception ex)
        {
            Log.Warn($"記錄 prov1_armed 失敗：{ex.Message}");
        }
    }

    /// <summary>
    /// 交握完成、第一次拉取之前（CompleteHandshake）：prov1 有無和上一條連線不同時記下來、重算目錄（帳戶與 cli.state 依 prov1 篩）；
    /// 有 prov1 而這次啟動延後過對話時，進 Live 要重整。第一次有 prov1 時補拉對話中繼資料（游標歸零一次，kv prov1_replicas）
    /// </summary>
    private void OnProvenForProv1(SyncConnection conn)
    {
        if (store is null)
            return;
        var prov1 = DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Prov1);
        var changed = Prov1Armed() != prov1;
        SetProv1Armed(prov1);
        if (changed)
        {
            Log.Info(prov1
                ? "這條連線協商出 prov1：Grok、Antigravity 的帳戶、CLI 狀態與對話開始同步"
                : "這條連線沒有協商出 prov1（伺服器較舊或回滾）：Grok、Antigravity 先不同步，伺服器更新後補送");
            Republish();
        }

        if (prov1 && prov1Deferred)
        {
            prov1Deferred = false;
            refreshNeeded = true;
        }

        if (prov1)
            ResetReplicaMetaCursorOnce();
    }

    /// <summary>
    /// 第一次協商出 prov1：對話中繼資料（類別 meta）的游標歸零，接下來的拉取把舊版略過的 Grok、Antigravity 對話補回來
    /// （其他對話的事件照 seq 重新套用一次，結果相同）。拉取途中（上一條連線的頁還沒寫完）不動，下一條連線再試
    /// </summary>
    private void ResetReplicaMetaCursorOnce()
    {
        try
        {
            if (store!.GetKv(SyncStoreSchema.Prov1Replicas) == "1" || !store.ConversationTablesReady || replicaPage is not null)
                return;
            store.SetConversationCursors(new Dictionary<string, long>(StringComparer.Ordinal) { [SyncClasses.Meta] = 0 });
            store.SetKv(SyncStoreSchema.Prov1Replicas, "1");
            conversationCursors = null;
            Log.Info("第一次協商出 prov1：對話中繼資料從頭重拉一次，補回舊版略過的 Grok、Antigravity 對話");
        }
        catch (Exception ex)
        {
            Log.Warn($"補拉 Grok、Antigravity 的對話失敗（下一條連線再試）：{ex.Message}");
        }
    }

    /// <summary>對話的代理種類：這一輪的中繼資料，沒有時用最後看過的；都沒有時 null（還不知道，不延後）</summary>
    private AgentProvider? ProviderOf(string wire, PublishMetaSnapshot? snapshot = null) =>
        (snapshot ?? lastMeta.GetValueOrDefault(wire))?.Meta.Provider;

    /// <summary>這個對話現在要延後（Grok、Antigravity，沒有 prov1）：記下來，prov1 回來時重整</summary>
    private bool DeferredForProv1(string wire, PublishMetaSnapshot? snapshot = null)
    {
        if (ProviderOf(wire, snapshot) is not { } provider || !Enum.IsDefined(provider) || SyncProviderRules.Publishable(provider, CanPublishProv1()))
            return false;
        prov1Deferred = true;
        return true;
    }

    /// <summary>
    /// 有 conv1、沒有 prov1 的連線送出之前：Grok、Antigravity 的對話還沒送的列整個換成 {}（記在 <see cref="scrubOwed"/>，接著由
    /// <see cref="PayScrubOwed"/> 換掉、清摘要、記重整）。種類以這次啟動看過的中繼資料判斷；沒看過的對話由 ConversationSendLimit 先截斷、要中繼資料
    /// </summary>
    private void OweProv1Scrub(SyncConnection conn)
    {
        if (Conversations is null || !DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Conv1)
            || DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Prov1))
            return;
        var marked = 0;
        for (var i = FirstActive(); i < outbox.Count; i++)
        {
            var entry = outbox[i];
            if (entry.State != OutboxState.Pending || entry.ConversationId is not { } wire || entry.PayloadSha256 == EmptyConversationSha
                || scrubOwed.ContainsKey(wire) || !DeferredForProv1(wire))
                continue;
            scrubOwed[wire] = true;
            marked++;
        }

        if (marked > 0)
            Log.Info($"這條連線沒有協商出 prov1：{marked} 個 Grok、Antigravity 對話的待送列換成空物件，伺服器更新後重整補送");
    }
}
