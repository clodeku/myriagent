using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Myriagent.Core;

namespace Myriagent.UI;

/// <summary>
/// 系統匣圖示加上右下角的狀態點（<see cref="TrayBadgeRules"/>）：app 圖示畫成點陣圖、挖空一圈再畫點，同一個樣子只畫一次。
/// 狀態和上次一樣就不換圖示；畫不出來（GDI+ 失敗）就留著原本的圖示。只在 UI 執行緒用
/// </summary>
internal sealed class TrayIconBadge : IDisposable
{
    private readonly Dictionary<string, Icon> cache = new(StringComparer.Ordinal);
    private TrayBadge? applied;

    /// <summary>沒有狀態時的圖示（也是系統匣一開始的圖示）</summary>
    public Icon Base { get; } = AppIcons.Tray;

    public void Apply(NotifyIcon tray, TrayBadge badge)
    {
        if (applied == badge)
            return;
        applied = badge;
        tray.Icon = badge.State == TaskbarState.None ? Base : Compose(badge, SystemInformation.SmallIconSize.Width) ?? Base;
    }

    /// <summary>畫出加了狀態點的圖示（測試也用：看右下角的顏色）</summary>
    internal Icon? Compose(TrayBadge badge, int size)
    {
        var key = badge.State + "|" + badge.Text + "@" + size;
        if (cache.TryGetValue(key, out var cached))
            return cached;
        try
        {
            // 以 ToBitmap 取底（保留 alpha）；Graphics.DrawIcon 畫到透明底時半透明的邊會變色
            using var sized = new Icon(AppIcons.Window, size, size);
            using var src = sized.ToBitmap();
            using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(src, new Rectangle(0, 0, size, size));
                var b = TrayBadgeRules.Bounds(size, badge.State);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // 挖空一圈：以透明色直接覆寫（SourceCopy），點和圖示之間留一道縫
                g.CompositingMode = CompositingMode.SourceCopy;
                using (var clear = new SolidBrush(Color.Transparent))
                    g.FillEllipse(clear, b.X - b.Gap, b.Y - b.Gap, b.Diameter + b.Gap * 2, b.Diameter + b.Gap * 2);
                g.CompositingMode = CompositingMode.SourceOver;
                var tone = badge.State == TaskbarState.Waiting ? Color.FromArgb(0xF5, 0xA6, 0x23) : Color.FromArgb(0x3F, 0xB9, 0x50);
                using (var fill = new SolidBrush(tone))
                    g.FillEllipse(fill, b.X, b.Y, b.Diameter - 0.5f, b.Diameter - 0.5f);
                if (badge.Text.Length > 0)
                {
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    using var font = new Font("Segoe UI", TrayBadgeRules.FontPixels(b.Diameter, badge.Text), FontStyle.Bold, GraphicsUnit.Pixel);
                    using var ink = new SolidBrush(Color.FromArgb(0x1A, 0x14, 0x05));
                    using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString(badge.Text, font, ink, new RectangleF(b.X, b.Y, b.Diameter, b.Diameter + 1), format);
                }
            }
            var icon = ToIcon(bmp);
            cache[key] = icon;
            return icon;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.ExternalException or ArgumentException)
        {
            Log.Warn("系統匣的狀態圖示畫不出來：" + e.Message);
            return null;
        }
    }

    /// <summary>
    /// 點陣圖包成只有一張 PNG 的 .ico 再載入：Bitmap.GetHicon 會把半透明的像素存成預乘 alpha，圖示的圓角邊緣變暗
    /// </summary>
    private static Icon ToIcon(Bitmap bmp)
    {
        using var png = new MemoryStream();
        bmp.Save(png, ImageFormat.Png);
        using var ico = new MemoryStream();
        using (var w = new BinaryWriter(ico, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            w.Write((short)0);   // ICONDIR：保留
            w.Write((short)1);   // 類型：圖示
            w.Write((short)1);   // 張數
            w.Write((byte)(bmp.Width >= 256 ? 0 : bmp.Width));
            w.Write((byte)(bmp.Height >= 256 ? 0 : bmp.Height));
            w.Write((byte)0);    // 色盤數
            w.Write((byte)0);    // 保留
            w.Write((short)1);   // 色彩平面
            w.Write((short)32);  // 每像素位元
            w.Write((int)png.Length);
            w.Write(6 + 16);     // 影像的位移：ICONDIR 6 位元組＋一筆 ICONDIRENTRY 16 位元組
            png.Position = 0;
            png.CopyTo(ico);
        }
        ico.Position = 0;
        return new Icon(ico, bmp.Width, bmp.Height);
    }

    public void Dispose()
    {
        foreach (var icon in cache.Values)
            icon.Dispose();
        cache.Clear();
        Base.Dispose();
    }
}
