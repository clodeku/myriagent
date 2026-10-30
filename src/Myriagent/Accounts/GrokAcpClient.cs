using Myriagent.Agents;
using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Accounts;

/// <summary>
/// 短命的 grok agent 連線：查登入狀態、帳戶資訊與額度、在 app 內登入時用，用完就關（權杖由 grok 自己維護，app 不讀 auth.json）。
/// 對話用的程序由 GrokAgentSession 另外管理
/// </summary>
internal sealed class GrokAcpClient : IAsyncDisposable
{
    private readonly GrokAcpConnection connection;

    private GrokAcpClient(GrokAcpConnection connection)
    {
        this.connection = connection;
    }

    /// <summary>initialize 的回應（_meta.modelState 是模型清單、_meta.agentVersion 是版本）</summary>
    public JToken? InitializeResult { get; private set; }

    /// <summary>grok 程序已結束（等登入完成的流程用它提早結束，不必等到逾時）</summary>
    public Task ExitTask => connection.Process.ExitTask;

    public static async Task<GrokAcpClient> StartAsync(AgentLauncher launcher, Action<IDictionary<string, string?>> configureEnvironment, CancellationToken ct)
    {
        Directory.CreateDirectory(AppPaths.CacheDirectory);
        var process = AgentProcess.Start(launcher, GrokAcpRules.AgentArguments, AppPaths.CacheDirectory, configureEnvironment);
        var connection = new GrokAcpConnection(process);
        var client = new GrokAcpClient(connection);
        process.LineReceived += (_, line) => connection.OnLine(line);
        process.Exited += (_, _) => connection.FailAll("Grok 程序已結束");
        process.BeginReading();

        var version = typeof(GrokAcpClient).Assembly.GetName().Version?.ToString() ?? "1.0";
        var (result, error) = await connection.RequestAsync("initialize", GrokAcpRules.BuildInitializeParams(version), TimeSpan.FromSeconds(30), ct);
        if (error != null)
        {
            await client.DisposeAsync();
            throw new InvalidOperationException($"Grok 初始化失敗：{error.Text}");
        }
        client.InitializeResult = result;
        return client;
    }

    public Task<(JToken? Result, GrokAcpError? Error)> RequestAsync(string method, JObject? parameters, TimeSpan timeout, CancellationToken ct = default)
        => connection.RequestAsync(method, parameters, timeout, ct);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await connection.Process.CloseAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            connection.FailAll("連線已關閉");
            connection.Process.Dispose();
        }
    }
}
