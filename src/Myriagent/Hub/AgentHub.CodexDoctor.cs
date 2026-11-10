using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

internal sealed partial class AgentHub
{
    private readonly HashSet<string> doctorRunning = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource doctorCancel = new();

    internal Func<AgentLauncher, string[], Action<IDictionary<string, string?>>, string, CancellationToken, Task<CliRunResult>> RunCodexDoctor { get; set; }
        = (launcher, args, env, cwd, ct) => CliRunner.RunAsync(launcher, args, env, TimeSpan.FromSeconds(90), ct, cwd, maxOutputCharacters: 512_000);

    private async Task CodexDoctorAsync(IAgentView view, JObject command)
    {
        var id = command.Value<string>("accountId") ?? string.Empty;
        var reply = new JObject { ["type"] = "codexDoctor", ["accountId"] = id, ["requestId"] = command["requestId"]?.DeepClone() };
        var started = false;
        try
        {
            if (view is RemoteResponseView) throw new InvalidOperationException("診斷只能在這台電腦操作");
            var account = accounts.Find(id);
            if (account?.Provider != AgentProvider.Codex) throw new InvalidOperationException("請選擇這台電腦的 Codex 帳戶");
            if (!doctorRunning.Add(id)) throw new InvalidOperationException("這個帳戶正在診斷，請稍候");
            started = true;
            var launcher = accounts.ResolveLauncher(AgentProvider.Codex) ?? throw new InvalidOperationException("請先安裝 Codex CLI");
            var dir = Path.Combine(AppPaths.CacheDirectory, "codex-doctor");
            Directory.CreateDirectory(dir);
            var result = await RunCodexDoctor(launcher, CodexDoctorRules.Arguments(command.Value<bool?>("networkOnly") == true),
                env => AccountRules.ApplyEnvironment(account, env), dir, doctorCancel.Token);
            if (!ReferenceEquals(accounts.Find(id), account)) throw new InvalidOperationException("帳戶已變更，請重新診斷");
            reply["result"] = CodexDoctorRules.Report(result);
        }
        catch (OperationCanceledException) { reply["error"] = "診斷已取消"; }
        catch (Exception ex) { reply["error"] = ex.Message; }
        finally
        {
            if (started) doctorRunning.Remove(id);
            if (!disposed) Post(view, reply);
        }
    }
}
