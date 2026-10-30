using System.Globalization;

namespace Myriagent.Sync;

/// <summary>
/// app 版本的比較（Hello 的 appVersion 與伺服器的 minAppVersion，sync-protocol 1.4）：<c>主.次.修訂</c>（2～4 段數字），
/// 可帶 <c>-預覽版</c> 或 <c>+建置資訊</c>（比較時忽略）；少的段補 0（<c>0.3</c>＝<c>0.3.0</c>）
/// </summary>
internal static class AppVersionRules
{
    public const int MaxLength = 64;

    public static bool TryParse(string? value, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (value is null || value.Length is 0 or > MaxLength)
            return false;
        var cut = value.IndexOfAny(['-', '+']);
        var core = cut >= 0 ? value[..cut] : value;
        var parts = core.Split('.');
        if (parts.Length is < 2 or > 4)
            return false;
        var numbers = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length is 0 or > 9 || parts[i].AsSpan().IndexOfAnyExcept("0123456789") >= 0)
                return false;
            numbers[i] = int.Parse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture);
        }
        version = new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }

    /// <summary>app 版本可以連線（格式不對一律不行）</summary>
    public static bool IsAtLeast(string? appVersion, string minVersion) =>
        TryParse(appVersion, out var app) && TryParse(minVersion, out var min) && app >= min;
}
