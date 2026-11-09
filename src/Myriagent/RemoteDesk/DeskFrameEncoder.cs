using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Myriagent.Remote;

namespace Myriagent.RemoteDesk;

/// <summary>一個變動的區塊（像素座標與 JPEG）</summary>
internal sealed record DeskTile(DeskRect Rect, byte[] Jpeg);

/// <summary>
/// 遠端桌面的影格編碼（docs/sync-protocol.md 14.16）：目前的畫面和上一張比，切成 <see cref="DeskRules.Tile"/> 的格子找變動的，
/// 合併成矩形（<see cref="DeskRules.MergeTiles"/>、太高的切成幾段）各自壓成 JPEG。只在擷取執行緒用（GDI+ 的物件不跨執行緒）
/// </summary>
internal sealed class DeskFrameEncoder
{
    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    /// <summary>
    /// 變動的格子（以列為主）：<paramref name="previous"/> 是 null 或 <paramref name="key"/> 時全部算變動。兩個緩衝都是 BGRA、由上而下、寬 <paramref name="width"/>
    /// </summary>
    public static bool[] ChangedTiles(byte[]? previous, byte[] current, int width, int height, bool key, out int cols, out int rows)
    {
        cols = (width + DeskRules.Tile - 1) / DeskRules.Tile;
        rows = (height + DeskRules.Tile - 1) / DeskRules.Tile;
        var changed = new bool[cols * rows];
        if (key || previous is null || previous.Length != current.Length)
        {
            Array.Fill(changed, true);
            return changed;
        }

        var stride = width * 4;
        for (var r = 0; r < rows; r++)
        {
            var y0 = r * DeskRules.Tile;
            var y1 = Math.Min(height, y0 + DeskRules.Tile);
            for (var c = 0; c < cols; c++)
            {
                var x0 = c * DeskRules.Tile;
                var len = (Math.Min(width, x0 + DeskRules.Tile) - x0) * 4;
                for (var y = y0; y < y1; y++)
                {
                    var offset = y * stride + x0 * 4;
                    if (!current.AsSpan(offset, len).SequenceEqual(previous.AsSpan(offset, len)))
                    {
                        changed[r * cols + c] = true;
                        break;
                    }
                }
            }
        }

        return changed;
    }

    /// <summary>這一張要送的區塊（沒有變動回空的）</summary>
    public IReadOnlyList<DeskTile> Encode(byte[]? previous, byte[] current, int width, int height, int quality, bool key)
    {
        var changed = ChangedTiles(previous, current, width, height, key, out var cols, out var rows);
        var tiles = new List<DeskTile>();
        foreach (var merged in DeskRules.MergeTiles(changed, cols, rows))
        {
            foreach (var band in DeskRules.SplitTall(merged))
            {
                var rect = DeskRules.ToPixels(band, width, height);
                if (rect.W > 0 && rect.H > 0)
                    tiles.Add(new DeskTile(rect, Jpeg(current, width, rect, quality)));
            }
        }

        return tiles;
    }

    /// <summary>緩衝裡的一塊壓成 JPEG</summary>
    public static byte[] Jpeg(byte[] bgra, int width, DeskRect rect, int quality)
    {
        using var bitmap = new Bitmap(rect.W, rect.H, PixelFormat.Format32bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, rect.W, rect.H), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            var stride = width * 4;
            for (var y = 0; y < rect.H; y++)
                Marshal.Copy(bgra, (rect.Y + y) * stride + rect.X * 4, data.Scan0 + y * data.Stride, rect.W * 4);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 10, 95));
        using var stream = new MemoryStream();
        bitmap.Save(stream, JpegCodec, parameters);
        return stream.ToArray();
    }
}
