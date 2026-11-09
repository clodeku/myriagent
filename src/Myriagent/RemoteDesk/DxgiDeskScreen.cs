using System.Runtime.InteropServices;
using Myriagent.Core;
using Myriagent.Remote;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Myriagent.RemoteDesk;

/// <summary>
/// 遠端桌面的高效能擷取（docs/sync-protocol.md 14.16；2026-11-09 擁有者要求「必需高效能、手機也能用、反應快速」）：DXGI 桌面複製——
/// 畫面在 GPU 上取得，沒有變化時 AcquireNextFrame 直接告訴我們（不必讀回、比對、壓縮），縮小在 GPU 上以 mipmap 做（每層減半，
/// <see cref="DeskRules.MipLevel"/>），只讀回要傳的那一層。實測 3072×1728 的螢幕 GDI 讀回一張約 50 ms，這裡只要幾毫秒。
/// 拿不到（遠端桌面工作階段、旋轉的螢幕、顯示卡重設、沒有顯示卡）時改用 GDI（<see cref="GdiDeskScreen"/>），30 秒後再試。
/// 螢幕清單、游標、輸入都交給 GDI 那一份。只在擷取執行緒呼叫擷取
/// </summary>
internal sealed class DxgiDeskScreen : IDeskScreen
{
    private readonly GdiDeskScreen gdi = new();
    private Duplicator? duplicator;
    private long fallbackUntilMs;

    public IReadOnlyList<DeskScreenInfo> Screens() => gdi.Screens();

    public DeskRect VirtualBounds() => gdi.VirtualBounds();

    public DeskCursorState? Cursor() => gdi.Cursor();

    public DeskCursorImage? CursorImage() => gdi.CursorImage();

    public void Inject(IReadOnlyList<DeskInputEvent> events, DeskRect screen, DeskRect virtualScreen) => gdi.Inject(events, screen, virtualScreen);

    public void ReleaseAll() => gdi.ReleaseAll();

    public (int W, int H) OutputSize(DeskRect bounds, int? maxWidth)
        => UseDxgi ? DeskRules.MipSize(bounds.W, bounds.H, DeskRules.MipLevel(bounds.W, maxWidth)) : gdi.OutputSize(bounds, maxWidth);

    private bool UseDxgi => Environment.TickCount64 >= fallbackUntilMs;

    public DeskCapture Capture(DeskRect bounds, int width, int height, byte[] bgra, bool force)
    {
        if (!UseDxgi)
            return gdi.Capture(bounds, width, height, bgra, force);
        if (!GdiDeskScreen.InputDesktopIsDefault())
        {
            // 鎖定畫面、UAC：桌面複製會失效，回來時重建
            DropDuplicator();
            return DeskCapture.Failed;
        }

        try
        {
            if (duplicator is null || duplicator.Bounds != bounds || duplicator.OutWidth != width || duplicator.OutHeight != height)
            {
                DropDuplicator();
                duplicator = Duplicator.Create(bounds, width, height);
                if (duplicator is null)
                    return FallBack("找不到這個螢幕對應的顯示卡輸出（可能是旋轉的螢幕或遠端桌面工作階段）");
            }

            return duplicator.Capture(bgra, force);
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or COMException or InvalidOperationException)
        {
            DropDuplicator();
            // 存取遺失（換解析度、全螢幕程式、顯示卡重設）：下一次重建；其他錯誤改用 GDI 一陣子
            if (ex is SharpGen.Runtime.SharpGenException sg && sg.ResultCode == Vortice.DXGI.ResultCode.AccessLost)
                return DeskCapture.Retry;
            return FallBack(ex.Message);
        }
    }

    private DeskCapture FallBack(string why)
    {
        Log.Info($"遠端桌面改用 GDI 擷取 30 秒：{why}");
        fallbackUntilMs = Environment.TickCount64 + 30_000;
        return DeskCapture.Retry;
    }

    private void DropDuplicator()
    {
        duplicator?.Dispose();
        duplicator = null;
    }

    public void Dispose()
    {
        DropDuplicator();
        gdi.Dispose();
    }

    /// <summary>一個螢幕的桌面複製與縮小用的材質</summary>
    private sealed class Duplicator : IDisposable
    {
        private readonly ID3D11Device device;
        private readonly ID3D11DeviceContext context;
        private readonly IDXGIOutputDuplication duplication;
        private readonly ID3D11Texture2D? mip;
        private readonly ID3D11ShaderResourceView? mipView;
        private readonly ID3D11Texture2D staging;
        private readonly int level;
        private bool hasFrame;

        private Duplicator(DeskRect bounds, int outWidth, int outHeight, int level, ID3D11Device device, ID3D11DeviceContext context, IDXGIOutputDuplication duplication)
        {
            Bounds = bounds;
            OutWidth = outWidth;
            OutHeight = outHeight;
            this.level = level;
            this.device = device;
            this.context = context;
            this.duplication = duplication;
            if (level > 0)
            {
                mip = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)bounds.W, (uint)bounds.H, 1, (uint)(level + 1),
                    BindFlags.ShaderResource | BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.GenerateMips));
                mipView = device.CreateShaderResourceView(mip);
            }

            staging = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)outWidth, (uint)outHeight, 1, 1,
                BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None));
        }

        public DeskRect Bounds { get; }

        public int OutWidth { get; }

        public int OutHeight { get; }

        /// <summary>找這個螢幕所在的顯示卡輸出、建立桌面複製；找不到（或螢幕旋轉了）回 null</summary>
        public static Duplicator? Create(DeskRect bounds, int outWidth, int outHeight)
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
            {
                using (adapter)
                {
                    for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                    {
                        using (output)
                        {
                            var desc = output.Description;
                            var r = desc.DesktopCoordinates;
                            if (r.Left != bounds.X || r.Top != bounds.Y || r.Right - r.Left != bounds.W || r.Bottom - r.Top != bounds.H)
                                continue;
                            if ((int)desc.Rotation is not (0 or 1))
                                return null;
                            var level = 0;
                            while (DeskRules.MipSize(bounds.W, bounds.H, level).W > outWidth)
                                level++;
                            if (DeskRules.MipSize(bounds.W, bounds.H, level) != (outWidth, outHeight))
                                return null;
                            FeatureLevel[] levels = [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
                            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels, out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
                            try
                            {
                                using var output1 = output.QueryInterface<IDXGIOutput1>();
                                var duplication = output1.DuplicateOutput(device);
                                return new Duplicator(bounds, outWidth, outHeight, level, device!, context!, duplication);
                            }
                            catch
                            {
                                context?.Dispose();
                                device?.Dispose();
                                throw;
                            }
                        }
                    }
                }
            }

            return null;
        }

        public DeskCapture Capture(byte[] bgra, bool force)
        {
            // 第一次等久一點（建立後的第一張一定會來），之後不等：節奏由工作階段控制
            var result = duplication.AcquireNextFrame(hasFrame ? 0u : 500u, out var info, out var resource);
            if (result == Vortice.DXGI.ResultCode.WaitTimeout)
            {
                if (!hasFrame)
                    return DeskCapture.Retry;
                if (!force)
                    return DeskCapture.Unchanged;
                ReadStaging(bgra);
                return DeskCapture.Ok;
            }

            result.CheckError();
            try
            {
                // 只有滑鼠動了（沒有新的畫面）：游標另外送，畫面當成沒變
                if (info.LastPresentTime == 0 && hasFrame)
                {
                    if (!force)
                        return DeskCapture.Unchanged;
                }
                else
                {
                    using var texture = resource!.QueryInterface<ID3D11Texture2D>();
                    if (level == 0)
                    {
                        context.CopyResource(staging, texture);
                    }
                    else
                    {
                        context.CopySubresourceRegion(mip!, 0, 0, 0, 0, texture, 0, null);
                        context.GenerateMips(mipView!);
                        context.CopySubresourceRegion(staging, 0, 0, 0, 0, mip!, (uint)level, null);
                    }

                    hasFrame = true;
                }
            }
            finally
            {
                resource?.Dispose();
                duplication.ReleaseFrame();
            }

            ReadStaging(bgra);
            return DeskCapture.Ok;
        }

        private void ReadStaging(byte[] bgra)
        {
            var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var row = OutWidth * 4;
                for (var y = 0; y < OutHeight; y++)
                    Marshal.Copy(mapped.DataPointer + (nint)(y * (long)mapped.RowPitch), bgra, y * row, row);
            }
            finally
            {
                context.Unmap(staging, 0);
            }
        }

        public void Dispose()
        {
            staging.Dispose();
            mipView?.Dispose();
            mip?.Dispose();
            duplication.Dispose();
            context.Dispose();
            device.Dispose();
        }
    }
}
