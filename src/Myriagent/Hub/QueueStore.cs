using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Remote;
using Newtonsoft.Json;

namespace Myriagent.Hub;

/// <summary>
/// 佇列裡的一則訊息（存檔用；非同步提問的答案關聯不存，重啟後的答案照一般訊息送出）。
/// <paramref name="RemoteOrigin"/>：從遠端送進來時的來源（設計 14.11 D13）；<paramref name="DisplayText"/>：畫面、紀錄與同步看的文字
/// （秘密回答遮蔽後的，D20；null＝同 <paramref name="Text"/>）。兩者本機一律 null，不寫進 queue.json
/// </summary>
internal sealed record QueuedMessageData(string Id, string Text, List<AgentAttachment> Attachments, RemoteOrigin? RemoteOrigin = null, string? DisplayText = null);

/// <summary>一個對話的排隊訊息、輸入區的待送附件、Codex 拒絕核准時附的說明（設計 9.4）</summary>
internal sealed record QueueSnapshot(List<QueuedMessageData> Queue, List<AgentAttachment> PendingAttachments, List<DeniedOperation> PendingFeedback)
{
    [JsonIgnore]
    public bool IsEmpty => Queue.Count == 0 && PendingAttachments.Count == 0 && PendingFeedback.Count == 0;

    /// <summary>
    /// 內容指紋：沒變就不重寫檔（PostComposer 很常被呼叫）。排隊訊息與附件以 Id 區分（建立後不會改）；
    /// 拒絕說明沒有 Id，放完整內容（同長度的另一則說明也要認得出來）
    /// </summary>
    public string Fingerprint()
        => string.Join("|", Queue.Select(q => q.Id + ":" + q.Text.Length + ":" + string.Join(",", q.Attachments.Select(a => a.Id))))
           + "#" + string.Join(",", PendingAttachments.Select(a => a.Id))
           + "#" + string.Join(",", PendingFeedback.Select(f => Field(f.ToolName) + Field(f.Title) + Field(f.Feedback)));

    /// <summary>長度前綴：任意文字（含分隔符號）串起來仍然不會和別的內容相同；null 與空字串也不同</summary>
    private static string Field(string? value) => value == null ? "~" : value.Length + ":" + value;
}

/// <summary>conversations\{id}\queue.json 的讀寫（原子寫入；空的就刪檔）</summary>
internal static class QueueStore
{
    public const string FileName = "queue.json";

    public static void Save(string conversationDirectory, QueueSnapshot snapshot)
    {
        var path = Path.Combine(conversationDirectory, FileName);
        if (snapshot.IsEmpty)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }
        JsonFile.Write(path, snapshot);
    }

    /// <summary>
    /// 讀 queue.json；沒有內容回 null。Status 是 Unreadable（被占用、沒有權限）時原檔還在，呼叫端讀到之前不可以覆寫它；
    /// 損毀的已由 JsonFile 改名備份（回 Corrupt、null）
    /// </summary>
    public static (JsonReadStatus Status, QueueSnapshot? Snapshot) TryLoad(string conversationDirectory)
    {
        var (status, value, _) = JsonFile.TryRead<QueueSnapshot>(Path.Combine(conversationDirectory, FileName));
        var snapshot = status == JsonReadStatus.Ok && value != null ? Normalize(value) : null;
        return (status, snapshot is { IsEmpty: false } ? snapshot : null);
    }

    public static QueueSnapshot? Load(string conversationDirectory) => TryLoad(conversationDirectory).Snapshot;

    /// <summary>
    /// 缺欄位的檔（手動修改過、或別的版本寫的）：Newtonsoft 對缺的建構式參數傳 null。清單補成空的、null 項目與沒有說明文字的拒絕略過、
    /// 缺的文字與 Id 補上——中樞在建構式載入佇列，不能因為一個 queue.json 丟例外讓 app 起不來。遠端來源與顯示文字照原樣保留
    /// </summary>
    private static QueueSnapshot Normalize(QueueSnapshot raw)
        => new(
            (raw.Queue ?? new()).Where(q => q != null)
                .Select(q => new QueuedMessageData(
                    string.IsNullOrEmpty(q.Id) ? Guid.NewGuid().ToString("N") : q.Id, q.Text ?? string.Empty, Attachments(q.Attachments), q.RemoteOrigin, q.DisplayText))
                .ToList(),
            Attachments(raw.PendingAttachments),
            (raw.PendingFeedback ?? new()).Where(f => f is { Feedback: not null }).ToList());

    private static List<AgentAttachment> Attachments(List<AgentAttachment>? attachments) => (attachments ?? new()).Where(a => a != null).ToList();
}
