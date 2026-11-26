using System.Runtime.CompilerServices;
using Myriagent.Agents;

namespace Myriagent.Tests.TestSupport;

/// <summary>
/// MYRIAGENT_TEST_VIA_HOST=1：這次測試執行裡，沒有指定主機的 AgentProcess（各個會話測試、假 CLI、真的 CLI 的整合測試）一律經代理主機開，
/// 用來驗證經主機開的程序和直接開的行為一樣（設計 docs/plans/2026-11-26-agent-host.md 的 H0）。平常不設：測試照舊直接開程序
/// </summary>
internal static class HostedProcessMode
{
    public static bool Enabled { get; private set; }

    [ModuleInitializer]
    internal static void Initialize()
    {
        if (Environment.GetEnvironmentVariable("MYRIAGENT_TEST_VIA_HOST") != "1")
            return;
        var root = Path.Combine(Path.GetTempPath(), "Myriagent.Tests", "via-host", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        AgentProcess.AmbientHostForTests = new AgentHostClient(Path.Combine(AppContext.BaseDirectory, AgentHostClient.ExecutableName), root, Path.Combine(root, "host.log"));
        Enabled = true;
    }
}
