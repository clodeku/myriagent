using System.Text.Json;
using MyriagentHub.Audit;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Ops;

/// <summary>status\running.json 的內容（部署腳本比對版本與 build）</summary>
internal sealed record RunningInfo(
    string State,
    string Version,
    string Build,
    int Pid,
    long StartedAt,
    long? StoppedAt,
    string Environment,
    bool IsWindowsService,
    string ServerDomain,
    int HttpsPort,
    string DataDir);

/// <summary>
/// 服務真的開始聽連接埠之後寫 running.json（state＝running）；停止時改成 stopping／stopped。
/// 啟動時與上一次執行的 build 不同就記一筆稽核（部署版本變更）。
/// </summary>
internal sealed class RunningStatus(
    HubPaths paths,
    KvStore kv,
    AuditLog audit,
    TimeProvider time,
    IOptions<HubOptions> options,
    IHostEnvironment environment,
    IHostApplicationLifetime lifetime,
    ILogger<RunningStatus> log) : IHostedService
{
    private long startedAt;

    public long StartedAt => startedAt;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        startedAt = time.GetUtcNow().ToUnixTimeMilliseconds();
        lifetime.ApplicationStarted.Register(() => _ = OnStartedAsync());
        lifetime.ApplicationStopping.Register(() => Write("stopping", null));
        lifetime.ApplicationStopped.Register(() => Write("stopped", time.GetUtcNow().ToUnixTimeMilliseconds()));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task OnStartedAsync()
    {
        try
        {
            Write("running", null);
            var build = BuildInfo.Current;
            var previous = await kv.GetAsync<string>(KvKeys.LastRunningBuild);
            var current = build.Version + "+" + build.Build;
            if (previous != current)
            {
                await kv.SetAsync(KvKeys.LastRunningBuild, current);
                await audit.WriteAsync(new AuditEntry(AuditActors.System, null, null, AuditActions.VersionChanged, current, AuditOutcomes.Ok, new { from = previous, to = current }));
            }
            log.LogInformation("Myriagent Hub {Version}（build {Build}）已啟動：https://{Domain}:{Port}，資料根 {Data}",
                build.Version, build.Build, options.Value.ServerDomain, options.Value.HttpsPort, paths.Root);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "寫入 running.json 失敗");
        }
    }

    private void Write(string state, long? stoppedAt)
    {
        try
        {
            var build = BuildInfo.Current;
            var info = new RunningInfo(state, build.Version, build.Build, Environment.ProcessId, startedAt, stoppedAt, environment.EnvironmentName,
                HostInfo.IsWindowsService, options.Value.ServerDomain, options.Value.HttpsPort, paths.Root);
            Directory.CreateDirectory(paths.StatusDir);
            var temp = paths.RunningFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(info, StatusJson.Options));
            File.Move(temp, paths.RunningFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogError(ex, "寫入 running.json 失敗（{State}）", state);
        }
    }
}
