using System.Runtime.InteropServices;

namespace Myriagent.SystemStatus;

/// <summary>
/// 這台的喇叭音量與螢幕（電腦狀態面板的電腦選單，2026-11-17；中樞的 AgentHub.DeviceControl.cs 在背景執行緒呼叫）。
/// 測試換成假的，正式的是 <see cref="WindowsDeviceControls"/>
/// </summary>
internal interface IDeviceControls
{
    /// <summary>預設播放裝置的音量（0～100）與靜音；沒有播放裝置回 null</summary>
    (int Volume, bool Muted)? GetAudio();

    /// <summary>設定音量（null＝不改）與靜音（null＝不改），回設定之後的狀態；沒有播放裝置回 null</summary>
    (int Volume, bool Muted)? SetAudio(int? volume, bool? mute);

    /// <summary>立即關閉螢幕（同讓螢幕休眠：動滑鼠或鍵盤就亮；不鎖定、程式照跑）。送不出去回 false</summary>
    bool ScreenOff();
}

/// <summary>
/// Windows 的實作：音量是 Core Audio 的 IAudioEndpointVolume（預設的播放裝置、多媒體角色；一般權限就能改，和工作列的音量一樣），
/// 關螢幕是對所有最上層視窗廣播 WM_SYSCOMMAND／SC_MONITORPOWER（2＝關閉）。都是這個使用者工作階段的設定，不需要系統管理員
/// </summary>
internal sealed class WindowsDeviceControls : IDeviceControls
{
    public (int Volume, bool Muted)? GetAudio() => WithEndpoint(volume =>
    {
        Marshal.ThrowExceptionForHR(volume.GetMasterVolumeLevelScalar(out var level));
        Marshal.ThrowExceptionForHR(volume.GetMute(out var muted));
        return (ToPercent(level), muted);
    });

    public (int Volume, bool Muted)? SetAudio(int? volume, bool? mute) => WithEndpoint(endpoint =>
    {
        var context = Guid.Empty;
        if (volume is { } v)
            Marshal.ThrowExceptionForHR(endpoint.SetMasterVolumeLevelScalar(Math.Clamp(v, 0, 100) / 100f, ref context));
        if (mute is { } m)
            Marshal.ThrowExceptionForHR(endpoint.SetMute(m, ref context));
        Marshal.ThrowExceptionForHR(endpoint.GetMasterVolumeLevelScalar(out var level));
        Marshal.ThrowExceptionForHR(endpoint.GetMute(out var muted));
        return (ToPercent(level), muted);
    });

    public bool ScreenOff() => PostMessage(HwndBroadcast, WmSysCommand, ScMonitorPower, MonitorOff);

    private static int ToPercent(float level) => (int)Math.Round(Math.Clamp(level, 0f, 1f) * 100, MidpointRounding.AwayFromZero);

    /// <summary>取預設播放裝置的音量介面做事；沒有播放裝置（E_NOTFOUND）回 null，其他錯誤照丟</summary>
    private static (int Volume, bool Muted)? WithEndpoint(Func<IAudioEndpointVolume, (int Volume, bool Muted)> action)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        object? activated = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            var hr = enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out device);
            if (hr == ENotFound || device is null)
                return null;
            Marshal.ThrowExceptionForHR(hr);
            var iid = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out activated));
            return action((IAudioEndpointVolume)activated);
        }
        finally
        {
            if (activated is not null)
                Marshal.ReleaseComObject(activated);
            if (device is not null)
                Marshal.ReleaseComObject(device);
            if (enumerator is not null)
                Marshal.ReleaseComObject(enumerator);
        }
    }

    private const int ERender = 0;
    private const int EMultimedia = 1;
    private const int ClsctxAll = 0x17;
    private const int ENotFound = unchecked((int)0x80070490);
    private static readonly IntPtr HwndBroadcast = new(0xFFFF);
    private const uint WmSysCommand = 0x0112;
    private static readonly IntPtr ScMonitorPower = new(0xF170);
    private static readonly IntPtr MonitorOff = new(2);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        int UnregisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        int GetChannelCount(out uint count);

        [PreserveSig]
        int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);

        [PreserveSig]
        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

        [PreserveSig]
        int GetMasterVolumeLevel(out float levelDb);

        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float level);

        [PreserveSig]
        int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);

        [PreserveSig]
        int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);

        [PreserveSig]
        int GetChannelVolumeLevel(uint channel, out float levelDb);

        [PreserveSig]
        int GetChannelVolumeLevelScalar(uint channel, out float level);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
