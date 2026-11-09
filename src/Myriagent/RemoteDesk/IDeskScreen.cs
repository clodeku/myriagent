using Myriagent.Remote;

namespace Myriagent.RemoteDesk;

/// <summary>一個螢幕（實際像素）</summary>
internal sealed record DeskScreenInfo(string Name, DeskRect Bounds, bool Primary);

/// <summary>擷取的結果</summary>
internal enum DeskCapture
{
    /// <summary>寫進了緩衝</summary>
    Ok,

    /// <summary>畫面和上一次一樣（DXGI 告訴我們沒有變化；緩衝沒動）</summary>
    Unchanged,

    /// <summary>擷取不到（鎖定畫面、UAC 的安全桌面、顯示卡重設中）</summary>
    Failed,

    /// <summary>擷取的方式換了（DXGI 改成 GDI、重建桌面複製）：重新問 <see cref="IDeskScreen.OutputSize"/> 再擷取一次</summary>
    Retry,
}

/// <summary>滑鼠游標：位置是虛擬桌面的像素；<see cref="ShapeId"/> 變了才需要新的圖</summary>
internal sealed record DeskCursorState(int X, int Y, bool Visible, long ShapeId);

/// <summary>游標的圖（PNG）與熱點（像素）</summary>
internal sealed record DeskCursorImage(byte[] Png, int Width, int Height, int HotX, int HotY);

/// <summary>
/// 遠端桌面被看的電腦的畫面與輸入（<see cref="DxgiDeskScreen"/> 是真的；測試以假的換掉，不碰真的螢幕與滑鼠鍵盤）。
/// <see cref="OutputSize"/>、<see cref="Capture"/>、<see cref="Cursor"/>、<see cref="CursorImage"/> 只在擷取執行緒呼叫，
/// <see cref="Inject"/> 與 <see cref="ReleaseAll"/> 可能在別的執行緒
/// </summary>
internal interface IDeskScreen : IDisposable
{
    /// <summary>目前的螢幕（主螢幕不一定在第一個）</summary>
    IReadOnlyList<DeskScreenInfo> Screens();

    /// <summary>整個虛擬桌面的範圍</summary>
    DeskRect VirtualBounds();

    /// <summary>這個螢幕要傳送的大小（擷取的方式決定：GPU 縮小只能減半，<paramref name="maxWidth"/> 是看的一方要求的寬度上限）</summary>
    (int W, int H) OutputSize(DeskRect bounds, int? maxWidth);

    /// <summary>
    /// 把 <paramref name="bounds"/> 的畫面（不含游標）縮成 <see cref="OutputSize"/> 的大小，以 BGRA、由上而下寫進 <paramref name="bgra"/>
    /// （長度 width×height×4）。<paramref name="force"/>：就算沒有變化也要寫（整個畫面重送時）
    /// </summary>
    DeskCapture Capture(DeskRect bounds, int width, int height, byte[] bgra, bool force);

    /// <summary>目前的游標</summary>
    DeskCursorState? Cursor();

    /// <summary>目前游標的圖（形狀變了才呼叫）；拿不到回 null</summary>
    DeskCursorImage? CursorImage();

    /// <summary>注入輸入（座標是相對於 <paramref name="screen"/> 的比例）</summary>
    void Inject(IReadOnlyList<DeskInputEvent> events, DeskRect screen, DeskRect virtualScreen);

    /// <summary>放開還按著的按鍵與滑鼠鍵（工作階段結束時）</summary>
    void ReleaseAll();
}

/// <summary>被看的電腦上的提示列（「〈發起者〉正在遠端操控這台電腦」與「中斷」）；UI 執行緒建立與關閉</summary>
internal interface IDeskIndicator : IDisposable
{
}
