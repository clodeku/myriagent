using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Myriagent.RemoteDesk;

/// <summary>
/// 遠端桌面的滑鼠游標（docs/sync-protocol.md 14.16）：位置與形狀另外送、由看的一方疊在畫面上——游標移動不必重送畫面區塊，
/// 看的一方也能讓它立即跟手。形狀以游標的 handle 當 id，變了才轉成 PNG
/// </summary>
internal static class DeskCursorReader
{
    public static DeskCursorState? Read()
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info))
            return null;
        return new DeskCursorState(info.X, info.Y, (info.Flags & 1) != 0 && info.Cursor != IntPtr.Zero, info.Cursor.ToInt64());
    }

    public static DeskCursorImage? Image()
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info) || info.Cursor == IntPtr.Zero || !GetIconInfo(info.Cursor, out var icon))
            return null;
        try
        {
            using var handle = Icon.FromHandle(info.Cursor);
            using var bitmap = handle.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return new DeskCursorImage(stream.ToArray(), bitmap.Width, bitmap.Height, icon.HotspotX, icon.HotspotY);
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException)
        {
            return null;
        }
        finally
        {
            if (icon.Mask != IntPtr.Zero)
                DeleteObject(icon.Mask);
            if (icon.Color != IntPtr.Zero)
                DeleteObject(icon.Color);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Cursor;
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public bool IsIcon;
        public int HotspotX;
        public int HotspotY;
        public IntPtr Mask;
        public IntPtr Color;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CursorInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
}
