using Myriagent.Core;
using Myriagent.Sync;

namespace Myriagent.Hub;

// 媒體轉送的對話快照（WP4.7 計畫步 8，D26）：引擎經 RunOnUiAsync 在 UI 執行緒問，之後的開檔與讀取都在背景。
// 這裡只判斷「看不看得到」與取路徑，不碰檔案系統（伺服器刪除的旁檔除外：EffectiveSyncPolicy 第一次會讀它，之後用記下的值）
internal sealed partial class AgentHub : IMediaScopeSource
{
    MediaScopeSnapshot? IMediaScopeSource.GetMediaScope(MediaScopeQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        // 電腦狀態面板的檔案傳輸（deviceFetch 的 @xfer）：沒有對話，只給禁止清單用的資料夾；以系統管理員執行時看不到
        if (query.ItemId == MediaServerRules.TransferItemId)
            return DeviceFilesElevated
                ? MediaScopeSnapshot.Hidden(string.Empty)
                : new MediaScopeSnapshot(string.Empty, true, string.Empty, string.Empty, string.Empty, string.Empty, false, null, CurrentMediaFolderRoots());
        if (FindByWireId(query.ConversationId) is not { } c)
            return null;
        // 本機擁有（還沒記擁有者的是這台的舊對話）、不是草稿與複本、目前的政策是 full（本機剛改成 metadata、伺服器還不知道的空窗也擋，D26）
        var owned = c.Meta.OwnerDeviceId is not { } owner || string.Equals(owner, query.DeviceId, StringComparison.Ordinal);
        if (c.IsDraft || c.IsReplica || !owned || EffectiveSyncPolicy(c) != ConversationSyncPolicies.Full)
            return MediaScopeSnapshot.Hidden(c.Meta.Id);

        (string Path, string FileName)? resolved = null;
        int? sequence = null;
        string? keptPath = null;
        var itemReference = query.ItemId is not (MediaServerRules.ListItemId or MediaServerRules.FileItemId or MediaServerRules.TransferItemId);
        if (itemReference && c.ItemsLoaded)
        {
            var item = c.Items.FirstOrDefault(i => string.Equals(i.Id, query.ItemId, StringComparison.Ordinal));
            resolved = MediaServerRules.Resolve(item, query.AttachmentId, query.MediaPath);
            if (resolved is not null && query.AttachmentId is null)
            {
                sequence = GeneratedMediaRules.Sequence(item?.MediaPaths, query.MediaPath);
                // 這台留了副本（AgentHub.MediaKeep.cs）：原檔被刪或被覆寫，遠端看到的也是當時那一張
                keptPath = MediaKeepRules.KeptPath(item?.KeptMedia, store.GetConversationDirectory(c.Meta.Id), query.MediaPath);
            }
        }

        return new MediaScopeSnapshot(
            c.Meta.Id,
            true,
            store.GetConversationDirectory(c.Meta.Id),
            store.GetAttachmentDirectory(c.Meta.Id),
            store.GetDefaultWorkingDirectory(c.Meta.Id),
            GetWorkingDirectory(c),
            itemReference && c.ItemsLoaded,
            resolved,
            CurrentMediaFolderRoots(),
            sequence,
            keptPath);
    }

    /// <summary>算禁止清單用的本機資料夾（原樣；媒體轉送與遠端檔案指令共用，UI 執行緒）</summary>
    private MediaFolderRoots CurrentMediaFolderRoots()
    {
        var accountFolders = accounts.Accounts
            .Select(a => a.ConfigDirectory)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d!)
            .ToList();
        return new MediaFolderRoots(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            NullIfEmpty(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
            AppPaths.Root,
            accountFolders,
            NullIfBlank(Environment.GetEnvironmentVariable("CODEX_HOME")));

        static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

        static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
