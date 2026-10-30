using System.Reflection;
using Myriagent.Agents;

namespace Myriagent.Tests.Protocol;

/// <summary>
/// 共用專案 Myriagent.Protocol 的邊界（設計 2.5：伺服器只參考它）：只能參考 BCL、Markdig、Newtonsoft.Json；
/// 不可以有 Win32 互通（P/Invoke）與啟動 CLI 程序的程式，也不可以反過來參考桌面 exe、WinForms、WebView2
/// </summary>
public class ProtocolAssemblyTests
{
    private static readonly Assembly Protocol = typeof(AgentChatItem).Assembly;

    [Fact]
    public void 模型規則與共用渲染器確實在Protocol組件()
    {
        Protocol.GetName().Name.Should().Be("Myriagent.Protocol");
        typeof(Myriagent.Hub.AgentHubRules).Assembly.Should().BeSameAs(Protocol, "伺服器的對話清單要用 GetGroupLabel");
        typeof(TextRules).Assembly.Should().BeSameAs(Protocol);
        typeof(Myriagent.UI.WebAssetRules).Assembly.Should().BeSameAs(Protocol);
        typeof(Myriagent.Rendering.ItemRenderer).Assembly.Should().BeSameAs(Protocol, "伺服器從原始項目自己渲染");
        typeof(Myriagent.Rendering.IMediaUrlBuilder).Assembly.Should().BeSameAs(Protocol);
        typeof(Myriagent.Rendering.ProtocolValues).Assembly.Should().BeSameAs(Protocol);
        typeof(Myriagent.Json.ProtocolJson).Assembly.Should().BeSameAs(Protocol);
        typeof(Myriagent.Sync.UsageWindowKeyRules).Assembly.Should().BeSameAs(Protocol, "瀏覽器閘道送給網頁時要還原時間窗的鍵");
        typeof(Myriagent.Hub.LocalMediaUrls).Assembly.Should().NotBeSameAs(Protocol, "桌面的網址與白名單留在 exe");
    }

    [Fact]
    public void 只參考BCL_Markdig與NewtonsoftJson()
    {
        var references = Protocol.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        references.Should().Contain(new[] { "Markdig", "Newtonsoft.Json" });
        references.Where(name => !IsAllowed(name)).Should().BeEmpty("Protocol 只能參考 BCL、Markdig、Newtonsoft.Json（實際參考：{0}）", string.Join("、", references));
    }

    [Fact]
    public void 沒有Win32互通也沒有啟動程序的型別()
    {
        var types = Protocol.GetTypes();
        var pinvokes = types
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.Attributes.HasFlag(MethodAttributes.PinvokeImpl))
            .Select(m => $"{m.DeclaringType!.FullName}.{m.Name}")
            .ToList();

        pinvokes.Should().BeEmpty("伺服器組件不可有 Win32 互通（kernel32 工作物件、程序快照）");
        types.Select(t => t.Name).Should().NotContain(new[] { "AgentProcess", "ChildProcessJob", "CliRunner", "ClaudeStreamRules", "AgentHub" });
    }

    private static bool IsAllowed(string name)
        => name is "Markdig" or "Newtonsoft.Json" or "netstandard" or "mscorlib" or "System"
           || (name.StartsWith("System.", StringComparison.Ordinal)
               && !name.StartsWith("System.Windows", StringComparison.Ordinal)
               && !name.StartsWith("System.Drawing", StringComparison.Ordinal)
               && !name.StartsWith("System.Management", StringComparison.Ordinal));
}
