using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Myriagent.Core;

namespace Myriagent.Files;

/// <summary>
/// 縮圖：用 Windows 外殼的縮圖（IShellItemImageFactory，圖片與影片都有，Windows 自己有快取所以很快），輸出 PNG（保留透明）。
/// 外殼 COM 物件要在 STA 執行緒呼叫：專用一條 STA 背景執行緒排隊處理，不佔 UI 執行緒；結果放記憶體快取（以路徑＋修改時間＋大小為鍵）。
/// </summary>
internal sealed class ThumbnailService : IDisposable
{
    private const int MaxCacheEntries = 400;

    private sealed record WorkItem(string Path, int Size, TaskCompletionSource<byte[]?> Completion);

    private readonly BlockingCollection<WorkItem> queue = new(new ConcurrentQueue<WorkItem>());
    private readonly ConcurrentDictionary<string, byte[]> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> cacheOrder = new();
    private readonly Thread worker;

    public ThumbnailService()
    {
        worker = new Thread(Run) { IsBackground = true, Name = "Thumbnails" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    /// <summary>取得縮圖（PNG）；不是本機檔案、取不到回 null</summary>
    public Task<byte[]?> GetAsync(string path, int size)
    {
        size = Math.Clamp(size, 32, 1024);
        FileInfo info;
        try
        {
            path = Path.GetFullPath(path);   // Windows 外殼只認反斜線的完整路徑
            info = new FileInfo(path);
            if (!info.Exists)
                return Task.FromResult<byte[]?>(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Task.FromResult<byte[]?>(null);
        }

        var key = $"{path}|{info.LastWriteTimeUtc.Ticks}|{info.Length}|{size}";
        if (cache.TryGetValue(key, out var cached))
            return Task.FromResult<byte[]?>(cached);

        var completion = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (queue.IsAddingCompleted)
            return Task.FromResult<byte[]?>(null);
        queue.Add(new WorkItem(path, size, completion));
        return completion.Task.ContinueWith(t =>
        {
            var bytes = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
            if (bytes != null && cache.TryAdd(key, bytes))
            {
                cacheOrder.Enqueue(key);
                while (cache.Count > MaxCacheEntries && cacheOrder.TryDequeue(out var old))
                    cache.TryRemove(old, out _);
            }
            return bytes;
        }, TaskScheduler.Default);
    }

    private void Run()
    {
        foreach (var item in queue.GetConsumingEnumerable())
        {
            try
            {
                item.Completion.TrySetResult(Render(item.Path, item.Size));
            }
            catch (Exception ex)
            {
                Log.Warn($"縮圖失敗 {item.Path}：{ex.Message}");
                item.Completion.TrySetResult(null);
            }
        }
    }

    private static byte[]? Render(string path, int size)
    {
        IShellItemImageFactory? factory = null;
        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out factory);
            if (hr != 0 || factory == null)
                return null;
            hr = factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF.BIGGERSIZEOK | SIIGBF.THUMBNAILONLY, out hbitmap);
            if (hr != 0 || hbitmap == IntPtr.Zero)
                return null;
            using var bitmap = ToBitmap(hbitmap);
            if (bitmap == null)
                return null;
            using var scaled = FitWithin(bitmap, size);
            using var stream = new MemoryStream();
            scaled.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        finally
        {
            if (hbitmap != IntPtr.Zero)
                DeleteObject(hbitmap);
            if (factory != null)
                Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>HBITMAP → 32 位元 ARGB（以 GetDIBits 取上到下的像素；alpha 全為 0 的提供者當作不透明）</summary>
    private static Bitmap? ToBitmap(IntPtr hbitmap)
    {
        if (GetObject(hbitmap, Marshal.SizeOf<BITMAP>(), out var bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0)
            return null;
        int width = bm.bmWidth, height = bm.bmHeight;
        var info = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,   // 負數＝由上而下
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
        };
        var pixels = new byte[width * height * 4];
        var dc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, hbitmap, 0, (uint)height, pixels, ref info, 0) == 0)
                return Image.FromHbitmap(hbitmap);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }

        bool anyAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                anyAlpha = true;
                break;
            }
        }
        if (!anyAlpha)
        {
            for (int i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;
        }

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            for (int y = 0; y < height; y++)
                Marshal.Copy(pixels, y * width * 4, data.Scan0 + y * data.Stride, width * 4);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    private static Bitmap FitWithin(Bitmap source, int size)
    {
        double scale = Math.Min(1.0, (double)size / Math.Max(source.Width, source.Height));
        int w = Math.Max(1, (int)Math.Round(source.Width * scale));
        int h = Math.Max(1, (int)Math.Round(source.Height * scale));
        var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.DrawImage(source, 0, 0, w, h);
        return result;
    }

    public void Dispose()
    {
        queue.CompleteAdding();
    }

    [Flags]
    private enum SIIGBF
    {
        RESIZETOFIT = 0x00,
        BIGGERSIZEOK = 0x01,
        MEMORYONLY = 0x02,
        ICONONLY = 0x04,
        THUMBNAILONLY = 0x08,
        INCACHEONLY = 0x10,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, out BITMAP lpvObject);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, [Out] byte[] lpvBits, ref BITMAPINFOHEADER lpbi, uint uUsage);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
}
