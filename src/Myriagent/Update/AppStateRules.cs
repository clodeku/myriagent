using System.Text;
using Myriagent.Sync;

namespace Myriagent.Update;

/// <summary>
/// 這台的 app 更新狀態 → <c>app.state</c> 的 payload（app 自我更新 U3，設計 7.8；docs/sync-protocol.md 的 app.state）。
/// 階段的對應：協調者的排空 → draining、交棒與結束 → applying、檢查中與失敗 → idle（失敗的原因放 lastError）、
/// 已備妥但還有不能重啟的原因（使用者在用、Remote Control 在用…）→ waiting、缺 runtime／空間不足／未啟用 → blocked。
/// rolledBack 不送：退回之後的第一次啟動只有通知與設定頁的「最近結果」，狀態本身是 idle
/// </summary>
internal static class AppStateRules
{
    /// <summary>lastError 的上限（伺服器的 CatalogRules.MaxTextLength）</summary>
    public const int MaxLastError = 500;

    /// <param name="stagePhase">協調者自己的階段（AppUpdateView 的 draining、restarting、installing）；不在這些階段時是 null，照 <paramref name="status"/></param>
    /// <param name="restartBlocked">已備妥時還有不能自動重啟的原因（AppUpdateView 的 blocker 不是 null）</param>
    /// <param name="fallbackCheckedAt">還沒檢查過時的 checkedAt（伺服器要大於 0；呼叫端給這次啟動的時間，內容才不會每次都變）</param>
    public static AppStatePayload Build(string? stagePhase, AppUpdateStatus status, bool restartBlocked, ReleaseIdentity current, long fallbackCheckedAt)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(current);
        var phase = stagePhase switch
        {
            "draining" => AppStatePhases.Draining,
            "restarting" => AppStatePhases.Applying,
            "installing" => AppStatePhases.Idle,
            _ => status.Phase switch
            {
                AppUpdatePhase.Idle or AppUpdatePhase.Checking or AppUpdatePhase.Failed => AppStatePhases.Idle,
                AppUpdatePhase.Downloading => AppStatePhases.Downloading,
                AppUpdatePhase.Staged => restartBlocked ? AppStatePhases.Waiting : AppStatePhases.Staged,
                AppUpdatePhase.Blocked or AppUpdatePhase.Disabled => AppStatePhases.Blocked,
                _ => throw new ArgumentOutOfRangeException(nameof(status), status.Phase, "不認得的更新階段"),
            },
        };
        var lastError = status.Phase is AppUpdatePhase.Failed or AppUpdatePhase.Blocked or AppUpdatePhase.Disabled ? CleanLine(status.Detail) : null;
        var staged = ReleaseIdentity.TryParseRelease(status.StagedRelease, out _) ? status.StagedRelease : null;
        var checkedAt = status.LastCheckAt is { } at ? new DateTimeOffset(at).ToUnixTimeMilliseconds() : fallbackCheckedAt;
        return new AppStatePayload(current.Version.ToString(), current.IsDev ? null : current.Release, phase, staged, lastError, Math.Max(1, checkedAt));
    }

    /// <summary>整理成一行（控制字元與格式字元換成空白、連續空白併成一個）、最多 <see cref="MaxLastError"/> 字；空的回 null</summary>
    public static string? CleanLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var sb = new StringBuilder(Math.Min(text.Length, MaxLastError));
        var space = false;
        foreach (var c in text)
        {
            var blank = char.IsWhiteSpace(c) || char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format;
            if (blank)
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(c);
        }

        if (sb.Length == 0)
            return null;
        if (sb.Length > MaxLastError)
        {
            sb.Length = MaxLastError;
            // 不留下半個代理對
            if (char.IsHighSurrogate(sb[^1]))
                sb.Length--;
        }

        return sb.ToString();
    }
}
