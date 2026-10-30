using Myriagent.Security;
using Myriagent.Sync;
using MyriagentHub.Data;
using Newtonsoft.Json;

namespace MyriagentHub.Sync;

/// <summary>
/// 撤銷的 sync.db 那一半（hub.db 已標撤銷的裝置）：還沒發布過 device.revoked 的，照 <see cref="Catalog.RevokeDevice"/> 解除它的帳戶連結、
/// 發布 device.revoked 與受影響帳戶的 account.upsert；已經發布過的什麼都不做（冪等）。撤銷本身（PairingService.RevokeAsync）、
/// 重送撤銷（回 409 之前）與啟動時的補做都走這裡：撤銷在 hub.db 提交之後沒做完（sync.db 寫入失敗、寫入者已停止、程序結束）時由後兩者補上，
/// 而且不會發第二筆。在 <see cref="SyncWriter"/> 的交易裡執行（單一寫入者：查與做之間不會有別的寫入）。
/// 獨立成一個檔、不改 <see cref="Catalog"/>（計畫 5.2：Catalog.cs 刻意不動，減少與 WP3.3 的共用面）
/// </summary>
internal static class RevocationCompletion
{
    private const int MaxPayloadBytes = 64 * 1024;

    /// <summary>補做 <paramref name="devices"/> 裡已撤銷、sync.db 還沒有 device.revoked 的；回傳這次做了的裝置</summary>
    public static IReadOnlyList<string> Complete(SyncWriteContext context, Catalog catalog, IEnumerable<DeviceInfo> devices)
    {
        var pending = devices.Where(d => d.Revoked).ToList();
        if (pending.Count == 0)
            return [];
        var published = Published(context);
        var completed = new List<string>();
        foreach (var device in pending)
        {
            if (!published.Add(device.Id))
                continue;
            // 原因缺了（舊資料）當成疑似遭入侵：之後送出的內容不可信是比較保守的一邊
            catalog.RevokeDevice(context, device.Id, device.RevokedAt!.Value, device.RevokedReason ?? DeviceRegistryRules.RevokedCompromised);
            completed.Add(device.Id);
        }
        return completed;
    }

    /// <summary>已經發布過 device.revoked 的裝置（伺服器寫的、在 catalog 流；走 ix_events_stream，不掃整個 events）</summary>
    internal static HashSet<string> Published(SyncWriteContext context)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        using var cmd = context.Command("SELECT enc, payload FROM events WHERE stream = $stream AND kind = $kind AND author = $author",
            ("$stream", SyncStreams.Catalog), ("$kind", SyncEventKinds.DeviceRevoked), ("$author", SyncEventStore.ServerAuthor));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (PayloadRules.TryDecode(reader.GetString(0), reader.GetString(1), MaxPayloadBytes, out var json) && DeviceIdOf(json) is { } id)
                ids.Add(id);
        }
        return ids;
    }

    private static string? DeviceIdOf(string json)
    {
        try
        {
            return SyncJson.Deserialize<DeviceRevokedPayload>(json)?.DeviceId;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
