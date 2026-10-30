using Myriagent.Agents;
using Myriagent.Core;

namespace Myriagent.Sync;

// WP4.7（媒體、檔案與上傳）步 10 的 partial（D14、D31）：協商出 media1、定稿（不是 Running）的項目在 WP4.3 的截斷之前先試 blob 分支——
// 每欄截到 4 MB（ItemBlobRules.CapFields）→ MaskItem → TryItem → TryFit（大欄位換成 {blob, length}，內容一律取自遮蔽後的 payload）→ 序列化 → Validate。
// blob 都已確認在伺服器上才送 blob 版；否則排背景上傳、這次照 WP4.3 送截斷版，上傳完成後引擎重新走這個對話、投影最新的快照。
// 不能走 blob 的（base64 片段超過 DLP 的解碼範圍、放不下、不合格）一律回 null，照 WP4.3 截斷
internal sealed partial class ConversationProjector
{
    /// <summary>送出帶 blob 參照的項目數（記錄與測試用）</summary>
    public int BlobItems { get; private set; }

    /// <summary>
    /// blob 分支：可以送 blob 版時回投影好的項目（<see cref="PublishRecord.Truncated"/> 只在每欄截到 4 MB 時為 true）；
    /// 不走 blob、blob 還沒確認在伺服器上、或放不下時回 null（呼叫端照 WP4.3 截斷）
    /// </summary>
    /// <param name="clean">秘密回答清除過的複本（已配好媒體參照）</param>
    /// <param name="snapshot">中樞的快照（配好參照的複本；狀態、等待中卡片的摘要以它為準）</param>
    /// <param name="source">投影前的來源摘要</param>
    private ProjectedItem? TryBlobs(string wireId, long epoch, AgentChatItem clean, AgentChatItem snapshot, string source, long ver)
    {
        if (MediaHook is not { } hook || !hook.UseBlobs(wireId, snapshot))
            return null;

        var budget = ItemBlobRules.MaxItemBlobBytes;
        for (var attempt = 0; attempt <= ItemBlobRules.MaxHalvings; attempt++, budget /= 2)
        {
            var capped = ItemBlobRules.CapFields(clean, budget, budget, out var cappedFields);
            var masked = CredentialLeakRules.MaskItem(capped);
            if (snapshot.Status == AgentItemStatus.Waiting)
                masked.RequestDigest = RequestDigestRules.Compute(snapshot);
            if (!ConversationWire.TryItem(masked, ver, out var wire))
                return null;
            if (ItemBlobRules.HasLongBase64Run(wire))
            {
                // 權杖 DLP 只解碼 base64 片段的開頭 64 KB：照 WP4.3 截斷（每欄 ≤30 KB，片段整段解得到）
                Log.Info($"對話 {wireId} 的項目 {snapshot.Id} 有很長的 base64 片段：不走 blob，照舊截斷");
                return null;
            }

            if (!ConversationEventRules.TryFit(wire, out var blobs))
                return null;
            if (!ItemBlobRules.BlobsFit(blobs, ItemBlobRules.MaxItemBlobBytes, ItemBlobRules.MaxItemBytes))
                continue;   // 遮蔽改寫或陣列的 JSON 讓內容變大：預算減半再試

            var json = ConversationWire.Serialize(wire);
            if (Checked(ConversationEventKinds.Item, json, PublishKeyRules.Item(wireId, snapshot.Id), wireId, epoch, out var problem) is not { } append)
            {
                Log.Info($"對話 {wireId} 的項目 {snapshot.Id} 的 blob 版不合格（{problem}）：照舊截斷");
                return null;
            }

            if (!hook.EnsureUploaded(wireId, snapshot.Id, blobs))
                return null;
            if (blobs.Count > 0)
                BlobItems++;
            return new ProjectedItem(append, new PublishRecord(wireId, snapshot.Id, snapshot.Ord, source, PublishDigestRules.Wire(wire), cappedFields.Count > 0));
        }

        Log.Info($"對話 {wireId} 的項目 {snapshot.Id} 遮蔽之後放不進 blob 的上限：照舊截斷");
        return null;
    }
}
