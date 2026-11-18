using System.Reflection;

namespace Myriagent.Core;

/// <summary>
/// 這份組建是哪一種版本。建置時以 <c>-p:AppEdition=standalone</c> 寫進桌面組件的 AssemblyMetadata（csproj）；一般的建置沒有這筆。
/// <para>
/// 單機版（standalone）：給不架伺服器的人用的安裝檔。只有這台電腦的多帳戶與對話——不讀配對資料、不建同步元件、不接受配對
/// （<see cref="Sync.SyncCoordinator"/>），網頁照 init 的 <c>edition</c> 不顯示多電腦同步、協作人與自動更新（web-protocol 的 init）。
/// 組建時決定、執行時改不了：同一個資料夾以前配對過（device.json 還在）也一律當成未配對。
/// </para>
/// </summary>
internal static class AppEdition
{
    public const string MetadataKey = "AppEdition";

    public const string Standalone = "standalone";

    /// <summary>這份組建的版本名稱；一般的建置是 null</summary>
    public static string? Name { get; } = Parse(typeof(AppEdition).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == MetadataKey)?.Value);

    public static bool IsStandalone => Name == Standalone;

    /// <summary>中繼資料的值 → 版本名稱：只認得 standalone（不分大小寫、前後空白不算）；沒有、空的、不認得的一律 null（當成一般的建置）</summary>
    internal static string? Parse(string? value) =>
        string.Equals(value?.Trim(), Standalone, StringComparison.OrdinalIgnoreCase) ? Standalone : null;
}
