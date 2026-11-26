using Myriagent.Agents;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 代理主機的接線（設計 docs/plans/2026-11-26-agent-host.md）：中樞預設不經主機（測試、沒接上時直接開）；
/// 接上之後每種代理的會話一律經主機開程序——沒有開關（2026-11-26 擁有者決定）
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
    public void 預設不經主機_接上之後一律交給會話_沒有開關()
    {
        using var h = new HubHarness();
        var (_, bare) = h.StartConversation(h.Main, h.Claude);
        bare.Sent.All.Single().Settings.ProcessHost.Should().BeNull("中樞沒接主機");

        var host = new FakeHost();
        h.Ui.Invoke(() => h.Hub.AttachProcessHost(host));
        h.Command(h.Main, "ready");
        ((JObject)h.Main.Last("init")!["settings"]!).ContainsKey("cliViaHost").Should().BeFalse("不是設定：網頁沒有這個開關");
        foreach (var account in new[] { h.Claude, h.Codex })
        {
            var (_, on) = h.StartConversation(h.Main, account);
            on.Sent.All.Single().Settings.ProcessHost.Should().BeSameAs(host, $"{account.Provider} 的會話經主機開程序");
        }

        // 以前的版本送的 cliViaHost 不再是設定：不認得的鍵不改變行為
        h.Command(h.Main, "setting", new { key = "cliViaHost", value = false });
        var (_, again) = h.StartConversation(h.Main, h.Claude);
        again.Sent.All.Single().Settings.ProcessHost.Should().BeSameAs(host);
    }
}
