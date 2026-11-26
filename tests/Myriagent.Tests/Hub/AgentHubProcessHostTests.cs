using Myriagent.Agents;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 代理主機的接線（設計 docs/plans/2026-11-26-agent-host.md 的 H0）：中樞預設不經主機；接上了也要設定 cliViaHost 開著才把主機交給會話，
/// 設定只能在本機改、改了之後新啟動的程序才照新的
/// </summary>
public sealed class AgentHubProcessHostTests
{
    private sealed class FakeHost : IAgentProcessHost
    {
        public HostedProcess Spawn(string fileName, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyList<KeyValuePair<string, string>> environment)
            => throw new AgentHostUnavailableException("測試用的主機不開程序");

        public HostedProcess Attach(AgentProcessDetachState state) => throw new AgentHostUnavailableException("測試用的主機不接回程序");

        public void Discard(AgentProcessDetachState state)
        {
        }

        public void DiscardUnclaimed()
        {
        }
    }

    [Fact]
    public void 預設不經主機_接上而且設定開著才交給會話_關掉後新的程序改回直接開()
    {
        using var h = new HubHarness();
        var (_, bare) = h.StartConversation(h.Main, h.Claude);
        bare.Sent.All.Single().Settings.ProcessHost.Should().BeNull("中樞沒接主機");

        var host = new FakeHost();
        h.Ui.Invoke(() => h.Hub.AttachProcessHost(host));
        h.Command(h.Main, "ready");
        h.Main.Last("init")!["settings"]!["cliViaHost"]!.Value<bool>().Should().BeFalse("預設關");
        var (_, off) = h.StartConversation(h.Main, h.Codex);
        off.Sent.All.Single().Settings.ProcessHost.Should().BeNull("設定關著：照舊直接開");

        h.Command(h.Main, "setting", new { key = "cliViaHost", value = true });
        h.OnUi(() => h.Settings.CliViaHost).Should().BeTrue();
        foreach (var account in new[] { h.Claude, h.Codex })
        {
            var (_, on) = h.StartConversation(h.Main, account);
            on.Sent.All.Single().Settings.ProcessHost.Should().BeSameAs(host, $"{account.Provider} 的會話經主機開程序");
        }

        h.Command(h.Main, "setting", new { key = "cliViaHost", value = false });
        var (_, again) = h.StartConversation(h.Main, h.Claude);
        again.Sent.All.Single().Settings.ProcessHost.Should().BeNull();
    }
}
