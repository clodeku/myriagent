using Myriagent.SystemStatus;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.SystemStatus;

/// <summary>
/// 真的 Core Audio：只讀這台預設播放裝置的音量與靜音（不改音量、不關螢幕）。設 MYRIAGENT_DEVCTL_IT=1 才跑（CI 機器常沒有播放裝置）
/// </summary>
public sealed class WindowsDeviceControlsTests
{
    [EnvFact("MYRIAGENT_DEVCTL_IT", "讀這台預設播放裝置的音量")]
    public void 讀得到音量與靜音()
    {
        var audio = new WindowsDeviceControls().GetAudio();
        audio.Should().NotBeNull("這台要有播放裝置");
        audio!.Value.Volume.Should().BeInRange(0, 100);
    }
}
