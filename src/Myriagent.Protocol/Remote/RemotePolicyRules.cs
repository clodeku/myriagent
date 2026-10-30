using Myriagent.Sync;

namespace Myriagent.Remote;

/// <summary>
/// 電腦本機的遠端操作政策（設計 8.10、14.11 D7；閘門 5e）：<c>Off</c>（預設，D29）不收任何遠端指令（連 stop 也不收）；
/// <c>View</c> 只看對話與媒體參照、不收任何遠端指令；<c>Control</c> 收層級 0～2，要完全自動（FullAccess）的另要 <c>AllowRemoteFullAccess</c>。
/// 暫停遠端操作等於 <c>Off</c>：傳入的是 Hello 回報給伺服器的同一個政策（桌面 <c>SyncCoordinatorRules.HelloPolicy</c>，暫停時是 Off／false）。
/// 不認得的層級名稱當成 <c>Off</c>（最保守）
/// </summary>
internal static class RemotePolicyRules
{
    public const string Off = "Off";

    public const string View = "View";

    public const string Control = "Control";

    public static IReadOnlyList<string> Levels { get; } = [Off, View, Control];

    /// <summary>null＝允許；否則是 <see cref="RemoteResultCodes.Policy"/>。層級超出 0～2 丟例外</summary>
    public static string? Check(DevicePolicy? policy, int tier, bool fullAccess)
    {
        if (tier is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(tier), tier, "層級是 0～2");
        if (policy is not { RemoteControlLevel: Control })
            return RemoteResultCodes.Policy;
        if (fullAccess && !policy.AllowRemoteFullAccess)
            return RemoteResultCodes.Policy;
        return null;
    }
}
