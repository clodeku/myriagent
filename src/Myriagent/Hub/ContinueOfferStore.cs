using Myriagent.Agents;
using Myriagent.Core;

namespace Myriagent.Hub;

/// <summary>
/// conversations\{id}\continue.json 的內容：這個對話有一輪被中斷、沒有自動接續，標頭給了「繼續」（AgentHub.ContinueOffers.cs）。
/// 標頭的「繼續」以前只在記憶體：app 再重新啟動一次就不見了，對話裡的提示卻還寫著「請按繼續」（2026-11-12 實機：斷電後重新開機，
/// 兩個對話給了「繼續」，74 秒後 app 閒置中套用等著的新版，按鈕就沒了）。給「繼續」時寫、送出任何訊息（含按了「繼續」）或換帳戶時刪；
/// 下次啟動據此還原標頭的「繼續」，不再加一次提示。只加欄位（設計 8.8）：舊版不認得這個檔、不讀也不刪
/// </summary>
internal sealed class ContinueOfferRecord
{
    public int Schema { get; set; } = 1;

    /// <summary>給「繼續」的時間（Unix 毫秒；記錄用）</summary>
    public long OfferedAt { get; set; }

    /// <summary>標頭的「舊版 → 新版」（CLI 換版沒有接續的才有；app 更新、app 重新啟動的是 null）</summary>
    public string? From { get; set; }

    public string? To { get; set; }

    /// <summary>標頭的說明（純文字）</summary>
    public string? Detail { get; set; }

    /// <summary>按「繼續」時接續訊息的內容；沒有（換版當時沒記下）時按了照最少的內容送</summary>
    public ContinueOfferContext? Context { get; set; }
}

/// <summary>continue.json 裡接續訊息的內容（ContinuationContext 的欄位；代理種類照對話現在的，不存）</summary>
internal sealed class ContinueOfferContext
{
    public string? FromVersion { get; set; }

    public string? ToVersion { get; set; }

    public List<AgentInterruptedTool> InterruptedTools { get; set; } = new();

    public List<string> PendingRequests { get; set; } = new();

    public bool BackgroundWorkStopped { get; set; }

    public string? AppFrom { get; set; }

    public string? AppTo { get; set; }

    /// <summary>AppRestartKind 的名稱（Closed、Unexpected）；沒有或不認得的當成不是 app 重新啟動</summary>
    public string? AppRestart { get; set; }

    public bool WaitingBackground { get; set; }

    public List<string> BackgroundTasks { get; set; } = new();
}

/// <summary>continue.json 與 ContinuationContext 的轉換。純函式</summary>
internal static class ContinueOfferRules
{
    public static ContinueOfferRecord ToRecord(string? from, string? to, string detail, ContinuationContext? context, DateTimeOffset now) => new()
    {
        OfferedAt = now.ToUnixTimeMilliseconds(),
        From = from,
        To = to,
        Detail = detail,
        Context = context == null ? null : new ContinueOfferContext
        {
            FromVersion = context.FromVersion,
            ToVersion = context.ToVersion,
            InterruptedTools = context.InterruptedTools.ToList(),
            PendingRequests = context.PendingRequests.ToList(),
            BackgroundWorkStopped = context.BackgroundWorkStopped,
            AppFrom = context.AppFrom,
            AppTo = context.AppTo,
            AppRestart = context.AppRestart?.ToString(),
            WaitingBackground = context.WaitingBackground,
            BackgroundTasks = context.BackgroundTasks?.ToList() ?? new(),
        },
    };

    /// <summary>還原接續訊息的內容（代理種類照對話現在的；UserRequested 在按下時才設）；沒存就 null</summary>
    public static ContinuationContext? Context(AgentProvider provider, ContinueOfferRecord record)
    {
        if (record.Context is not { } c)
            return null;
        return new ContinuationContext(provider, c.FromVersion, c.ToVersion, c.InterruptedTools, c.PendingRequests, c.BackgroundWorkStopped,
            AppFrom: c.AppFrom, AppTo: c.AppTo,
            AppRestart: Enum.TryParse<AppRestartKind>(c.AppRestart, out var kind) && Enum.IsDefined(kind) ? kind : null,
            WaitingBackground: c.WaitingBackground, BackgroundTasks: c.BackgroundTasks);
    }

    /// <summary>快照內容的指紋（不含時間）：一樣的不重寫</summary>
    public static string Fingerprint(ContinueOfferRecord r)
    {
        var c = r.Context;
        return string.Join("|", r.From, r.To, r.Detail, c == null ? "-" : string.Join("\u001d", c.FromVersion, c.ToVersion, c.BackgroundWorkStopped, c.AppFrom, c.AppTo,
            c.AppRestart, c.WaitingBackground, string.Join("\u001f", c.InterruptedTools.Select(t => t.ToolName + "\u001e" + t.Title)),
            string.Join("\u001f", c.PendingRequests), string.Join("\u001f", c.BackgroundTasks)));
    }
}

/// <summary>conversations\{id}\continue.json 的讀寫（原子寫入）</summary>
internal static class ContinueOfferStore
{
    public const string FileName = "continue.json";

    public static string PathFor(string conversationDirectory) => Path.Combine(conversationDirectory, FileName);

    /// <summary>寫 continue.json；不建立資料夾（對話的資料夾不在＝對話已刪除，重建會讓它在下次啟動被還原）：不在時丟 DirectoryNotFoundException</summary>
    public static void Save(string conversationDirectory, ContinueOfferRecord record)
        => JsonFile.Write(PathFor(conversationDirectory), record, createDirectory: false);

    /// <summary>刪掉 continue.json（本來就沒有也算成功）；刪不掉的例外照樣丟出</summary>
    public static void Delete(string conversationDirectory)
    {
        try
        {
            File.Delete(PathFor(conversationDirectory));
        }
        catch (DirectoryNotFoundException)
        {
            // 資料夾已不在（對話被刪除）：檔案也不在
        }
    }

    /// <summary>
    /// 讀 continue.json：Ok 時是正規化過的內容（清單的 null 換成空的、null 項目略過；沒有說明的當成沒有這個檔）。
    /// 損毀的已由 JsonFile 改名成 .corrupt-*（回 Corrupt、null）；Unreadable 時檔案還在
    /// </summary>
    public static (JsonReadStatus Status, ContinueOfferRecord? Record) TryLoad(string conversationDirectory)
    {
        var (status, value, _) = JsonFile.TryRead<ContinueOfferRecord>(PathFor(conversationDirectory));
        if (status != JsonReadStatus.Ok || value == null)
            return (status == JsonReadStatus.Ok ? JsonReadStatus.Missing : status, null);
        if (string.IsNullOrWhiteSpace(value.Detail))
            return (JsonReadStatus.Ok, null);
        if (value.Context is { } c)
        {
            c.InterruptedTools = (c.InterruptedTools ?? new()).Where(t => t != null).ToList();
            c.PendingRequests = (c.PendingRequests ?? new()).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            c.BackgroundTasks = (c.BackgroundTasks ?? new()).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        }
        return (status, value);
    }
}
