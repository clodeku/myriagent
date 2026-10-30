using Myriagent.UI;

namespace Myriagent.Tests.UI;

/// <summary>
/// 系統匣圖示的狀態點（TrayBadgeRules、TrayIconBadge）：和工作列同一個優先順序、點的位置與大小、
/// 真的畫出來的圖示右下角是那個顏色、其他地方照原本的圖示
/// </summary>
public sealed class TrayBadgeRulesTests
{
    [Fact]
    public void 等你回應優先_寫數字_執行中只有點()
    {
        TrayBadgeRules.Decide(0, 0).Should().Be(new TrayBadge(TaskbarState.None, ""));
        TrayBadgeRules.Decide(3, 0).Should().Be(new TrayBadge(TaskbarState.Running, ""));
        TrayBadgeRules.Decide(3, 2).Should().Be(new TrayBadge(TaskbarState.Waiting, "2"), "要你動手的先講");
        TrayBadgeRules.Decide(0, 14).Should().Be(new TrayBadge(TaskbarState.Waiting, "9+"));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void 點貼著右下角_等你回應比執行中大_不超出圖示(int size)
    {
        var run = TrayBadgeRules.Bounds(size, TaskbarState.Running);
        var wait = TrayBadgeRules.Bounds(size, TaskbarState.Waiting);
        foreach (var b in new[] { run, wait })
        {
            (b.X + b.Diameter).Should().Be(size);
            (b.Y + b.Diameter).Should().Be(size);
            (b.X - b.Gap).Should().BeGreaterThan(0);
        }
        wait.Diameter.Should().BeGreaterThan(run.Diameter);
        TrayBadgeRules.Bounds(size, TaskbarState.None).Diameter.Should().Be(0);
    }

    [Fact]
    public void 兩個字的數字縮小字級()
    {
        TrayBadgeRules.FontPixels(11, "9+").Should().BeLessThan(TrayBadgeRules.FontPixels(11, "3"));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    public void 畫出來的圖示_右下角是狀態色_其他照原樣(int size)
    {
        using var badge = new TrayIconBadge();
        Check(badge, new TrayBadge(TaskbarState.Running, ""), size, Color.FromArgb(0x3F, 0xB9, 0x50));
        Check(badge, new TrayBadge(TaskbarState.Waiting, "3"), size, Color.FromArgb(0xF5, 0xA6, 0x23));
        DumpPreview(badge, size);
        badge.Compose(new TrayBadge(TaskbarState.Waiting, "3"), size).Should().BeSameAs(badge.Compose(new TrayBadge(TaskbarState.Waiting, "3"), size), "同一個樣子只畫一次");
    }

    /// <summary>MYRIAGENT_TRAY_DUMP＝資料夾時把四種狀態放大 8 倍、深淺兩種底各畫一列存成 tray-{size}.png（看長相用）</summary>
    private static void DumpPreview(TrayIconBadge badge, int size)
    {
        var dir = Environment.GetEnvironmentVariable("MYRIAGENT_TRAY_DUMP");
        if (string.IsNullOrEmpty(dir)) return;
        var states = new[] { new TrayBadge(TaskbarState.None, ""), new TrayBadge(TaskbarState.Running, ""), new TrayBadge(TaskbarState.Waiting, "3"), new TrayBadge(TaskbarState.Waiting, "9+") };
        int z = 8, pad = 4;
        using var sheet = new Bitmap((size * z + pad) * states.Length, (size * z + pad) * 2);
        using var g = Graphics.FromImage(sheet);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        for (int row = 0; row < 2; row++)
        {
            g.FillRectangle(new SolidBrush(row == 0 ? Color.FromArgb(32, 32, 32) : Color.FromArgb(240, 240, 240)), 0, row * (size * z + pad), sheet.Width, size * z + pad);
            for (int i = 0; i < states.Length; i++)
            {
                using var ic = states[i].State == TaskbarState.None ? new Icon(AppIcons.Window, size, size) : (Icon)badge.Compose(states[i], size)!.Clone();
                using var b = ic.ToBitmap();
                g.DrawImage(b, new Rectangle(i * (size * z + pad), row * (size * z + pad), size * z, size * z));
            }
        }
        sheet.Save(Path.Combine(dir, $"tray-{size}.png"));
    }

    private static void Check(TrayIconBadge badge, TrayBadge state, int size, Color tone)
    {
        var icon = badge.Compose(state, size);
        icon.Should().NotBeNull();
        using var bmp = icon!.ToBitmap();
        bmp.Width.Should().Be(size);
        var b = TrayBadgeRules.Bounds(size, state.State);
        // 點的左側中間（圓內、避開置中的數字）是狀態色
        var inside = bmp.GetPixel(b.X + Math.Max(2, b.Diameter / 6), b.Y + b.Diameter / 2);
        Math.Abs(inside.R - tone.R).Should().BeLessThan(40, $"{state.State} 的點是狀態色（{inside}）");
        Math.Abs(inside.G - tone.G).Should().BeLessThan(40);
        inside.A.Should().BeGreaterThan(200);
        // 點以外照原本的圖示：左上四分之一和沒有狀態點的圖示一樣
        using var plain = new Icon(AppIcons.Window, size, size);
        using var orig = plain.ToBitmap();
        // 經過 HICON 來回一次，半透明的像素會有一點捨入差：透明度與（不太透明的）顏色各容許 8
        for (int y = 0; y < size / 4; y++)
            for (int x = 0; x < size / 4; x++)
            {
                Color got = bmp.GetPixel(x, y), want = orig.GetPixel(x, y);
                Math.Abs(got.A - want.A).Should().BeLessThanOrEqualTo(8, $"({x},{y}) 照原本的圖示");
                if (want.A > 64)
                    (Math.Abs(got.R - want.R) + Math.Abs(got.G - want.G) + Math.Abs(got.B - want.B)).Should().BeLessThanOrEqualTo(24, $"({x},{y}) 照原本的圖示 got={got} want={want}");
            }
    }
}
