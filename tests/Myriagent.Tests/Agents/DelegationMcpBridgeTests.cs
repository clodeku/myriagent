using System.Diagnostics;
using System.Reflection;
using System.Text;
using Myriagent.Agents;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

public sealed class DelegationMcpBridgeTests
{
    [Fact]
    public async Task 真正exe以stdio握手並能取消進行中的工具呼叫()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new DelegationMcpServer(async (_, _, ct) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { cancelled.TrySetResult(); }
            return new JObject();
        });
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "Myriagent.slnx")))
            repository = repository.Parent;
        repository.Should().NotBeNull();
        var configuration = typeof(DelegationMcpBridgeTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "BuildConfiguration").Value!;
        var executable = Path.Combine(repository!.FullName, "src", "Myriagent", "bin", configuration, "net10.0-windows", "win-x64", "Myriagent.exe");
        File.Exists(executable).Should().BeTrue("先建置 slnx 才有真正的 MCP 橋接 exe");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
        };
        info.ArgumentList.Add("--delegation-mcp");
        info.ArgumentList.Add(server.PipeName);
        using var process = Process.Start(info)!;
        try
        {
            await SendAsync(new JObject { ["id"] = 1, ["method"] = "initialize" });
            var init = await ReadAsync();
            init.Value<int>("id").Should().Be(1);
            init["result"]!["serverInfo"]!.Value<string>("name").Should().Be(DelegationMcpRules.ServerName);
            await SendAsync(new JObject { ["id"] = 2, ["method"] = "tools/list" });
            var tools = await ReadAsync();
            tools["result"]!["tools"]!.OfType<JObject>().Select(t => t.Value<string>("name")).Should().Equal("list_accounts", "delegate_task");
            await SendAsync(new JObject { ["id"] = 3, ["method"] = "tools/call", ["params"] = new JObject { ["name"] = "delegate_task" } });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await SendAsync(new JObject { ["method"] = "notifications/cancelled", ["params"] = new JObject { ["requestId"] = 3 } });
            (await ReadAsync())["error"].Should().NotBeNull();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await SendAsync(new JObject { ["id"] = 4, ["method"] = "ping" });
            (await ReadAsync()).Value<int>("id").Should().Be(4, "工具取消後 MCP 仍能繼續處理請求");
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            process.ExitCode.Should().Be(0);
            (await process.StandardError.ReadToEndAsync()).Should().BeEmpty();
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }

        async Task SendAsync(JObject request)
        {
            request["jsonrpc"] = "2.0";
            await process.StandardInput.WriteLineAsync(request.ToString(Formatting.None));
            await process.StandardInput.FlushAsync();
        }

        async Task<JObject> ReadAsync()
            => JObject.Parse((await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)))!);
    }
}
