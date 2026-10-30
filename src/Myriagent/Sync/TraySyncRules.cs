using System.Globalization;

namespace Myriagent.Sync;

/// <summary>系統匣同步項目按下去要做什麼</summary>
internal enum TraySyncAction
{
    /// <summary>只顯示狀態（停用的項目）</summary>
    None,

    PauseSync,

    ResumeSync,

    PauseRemote,

    ResumeRemote,
}

/// <summary>系統匣選單的一個同步項目</summary>
internal sealed record TraySyncItem(string Text, bool Enabled, TraySyncAction Action);

/// <summary>
/// 系統匣的同步狀態與暫停（計畫第 13 步）：已配對時在用量清單之後列「同步：狀態」、暫停／繼續同步、暫停／恢復遠端操作；
/// 未配對（含配對中）時沒有任何同步項目。遠端操作暫停時 Hello 一律回報 Off／false（<see cref="SyncCoordinatorRules.HelloPolicy"/>）。
/// WP4.6（計畫 D29）：舊對話回補到伺服器時，狀態之後多一列停用的回補進度（暫停時說明已暫停）；提示文字加「・回補 N 個對話」
/// </summary>
internal static class TraySyncRules
{
    /// <summary>回補進度的位元組換算單位（1 MB＝1024×1024 位元組，同網頁的顯示）</summary>
    private const long BytesPerMb = 1024 * 1024;

    /// <param name="status">協調者目前的狀態（已配對但引擎還沒發布時是初始的 connecting）</param>
    /// <param name="paired">已配對（引擎在跑）</param>
    /// <param name="paused">設定的「暫停同步」（選單照設定顯示，不等引擎回報）</param>
    /// <param name="remotePaused">設定的「暫停遠端操作」</param>
    /// <param name="backfill">回補的進度（WP4.3 的發布者；沒有在回補時 null，不加列）</param>
    /// <param name="backfillPaused">回補因磁碟保護暫停中（只在系統匣顯示，WP4.6 D29）</param>
    public static IReadOnlyList<TraySyncItem> Items(SyncStatusSnapshot? status, bool paired, bool paused, bool remotePaused,
        SyncBackfillProgress? backfill = null, bool backfillPaused = false)
    {
        if (!paired)
            return [];
        var items = new List<TraySyncItem>(4) { new("同步：" + StatusText(status), false, TraySyncAction.None) };
        if (BackfillText(backfill, backfillPaused) is { } backfillText)
            items.Add(new TraySyncItem(backfillText, false, TraySyncAction.None));
        items.Add(paused
            ? new TraySyncItem("繼續同步", true, TraySyncAction.ResumeSync)
            : new TraySyncItem("暫停同步", true, TraySyncAction.PauseSync));
        items.Add(remotePaused
            ? new TraySyncItem("恢復遠端操作", true, TraySyncAction.ResumeRemote)
            : new TraySyncItem("暫停遠端操作", true, TraySyncAction.PauseRemote));
        return items;
    }

    /// <summary>
    /// 回補那一列的文字（D29）：沒有在回補（<paramref name="backfill"/> 是 null）時 null（不顯示）；暫停中「回補已暫停（保護這台的磁碟）」；
    /// 其他「回補到伺服器：還有 12 個對話（約 50 MB）」（位元組無條件進位到 MB，不到 1 MB 寫「不到 1 MB」）
    /// </summary>
    public static string? BackfillText(SyncBackfillProgress? backfill, bool paused)
    {
        if (backfill is null)
            return null;
        if (paused)
            return "回補已暫停（保護這台的磁碟）";
        return $"回補到伺服器：還有 {backfill.ConversationsLeft.ToString(CultureInfo.InvariantCulture)} 個對話（{SizeText(backfill.BytesLeft)}）";
    }

    /// <summary>約略的大小：不到 1 MB 是「不到 1 MB」，其他「約 N MB」（無條件進位、千分位）</summary>
    private static string SizeText(long bytes)
    {
        if (bytes < BytesPerMb)
            return "不到 1 MB";
        var mb = bytes / BytesPerMb + (bytes % BytesPerMb == 0 ? 0 : 1);
        return "約 " + mb.ToString("N0", CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>給人看的同步狀態（不含「同步：」）；有待送的修改時加「（N 筆待送）」</summary>
    public static string StatusText(SyncStatusSnapshot? status)
    {
        if (status is null)
            return "連線中";
        var text = status.State switch
        {
            SyncEngineState.Live => "已連線",
            SyncEngineState.Idle or SyncEngineState.Connecting or SyncEngineState.Handshaking or SyncEngineState.Draining => "連線中",
            SyncEngineState.Backoff => "離線",
            SyncEngineState.UpdateOnly => "只提供更新（app 版本太舊）",
            SyncEngineState.Paused => "已暫停",
            SyncEngineState.NeedsConfirmation => "伺服器身分改變，請到設定確認",
            SyncEngineState.Stopped => "已停止",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status.State, "不認得的同步狀態"),
        };
        return status.Pending > 0 ? $"{text}（{status.Pending} 筆待送）" : text;
    }

    /// <summary>系統匣提示文字的同步段（AgentHub.UpdateTray 附加）；未配對時 null。回補中加「・回補 N 個對話」（D29）</summary>
    public static string? TrayText(SyncStatusSnapshot? status, bool paired, SyncBackfillProgress? backfill = null)
    {
        if (!paired)
            return null;
        var text = "同步 " + StatusText(status);
        return backfill is null ? text : text + "・回補 " + backfill.ConversationsLeft.ToString(CultureInfo.InvariantCulture) + " 個對話";
    }
}
