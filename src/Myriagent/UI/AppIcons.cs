namespace Myriagent.UI;

/// <summary>app 圖示（嵌入資源 app.ico，含 16～256 px 多種尺寸；由 tools/make_icon.py 產生）</summary>
internal static class AppIcons
{
    private static readonly Lazy<Icon?> Source = new(() =>
    {
        try
        {
            using var stream = typeof(AppIcons).Assembly.GetManifestResourceStream("app.ico");
            return stream == null ? null : new Icon(stream);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return null;
        }
    });

    /// <summary>視窗用（系統自己挑大小）</summary>
    public static Icon Window => Source.Value ?? SystemIcons.Application;

    /// <summary>系統匣用（小圖示尺寸）</summary>
    public static Icon Tray => Source.Value != null ? new Icon(Source.Value, SystemInformation.SmallIconSize) : SystemIcons.Application;
}
