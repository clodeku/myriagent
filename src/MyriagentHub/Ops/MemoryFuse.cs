using System.ServiceProcess;
using MyriagentHub.Hosting;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Ops;

internal sealed record FuseState(long? OverKillSince, bool Warned)
{
    public static readonly FuseState Initial = new(null, false);
}

internal enum FuseAction
{
    None,
    Warn,
    Recovered,
    Kill,
}

/// <summary>記憶體保險絲的純函式規則（9.10）：私有記憶體 &gt;400 MB 告警；&gt;700 MB 持續 5 分鐘就以非 0 結束碼退出，交給 SCM 失敗動作重啟</summary>
internal static class MemoryFuseRules
{
    public static (FuseState State, FuseAction Action) Evaluate(FuseState state, long privateMb, long now, MemoryFuseSettings settings)
    {
        var since = privateMb > settings.KillMb ? state.OverKillSince ?? now : (long?)null;
        if (since is { } s && now - s >= settings.KillAfterMinutes * 60_000L)
            return (state with { OverKillSince = since }, FuseAction.Kill);
        if (privateMb > settings.WarnMb && !state.Warned)
            return (new FuseState(since, true), FuseAction.Warn);
        if (privateMb <= settings.WarnMb && state.Warned)
            return (new FuseState(since, false), FuseAction.Recovered);
        return (state with { OverKillSince = since }, FuseAction.None);
    }
}

/// <summary>
/// 每 30 秒（Hub:MemoryFuse:SampleSeconds）取樣程序記憶體與 CPU；超過門檻就告警或退出。/readyz、ops status 查詢時另外即時取樣。
/// 退出用非 0 結束碼：以服務執行時設定 ServiceBase.ExitCode（WindowsServiceLifetime 不會自己傳 Environment.ExitCode），
/// 配合部署時設定的 <c>sc.exe failureflag MyriagentHub 1</c> 讓 SCM 的失敗動作重啟服務；60 秒內沒停乾淨就直接結束程序。
/// </summary>
internal sealed class MemoryFuse(
    OpsMetrics metrics,
    TimeProvider time,
    IOptions<HubOptions> options,
    IHostApplicationLifetime lifetime,
    IServiceProvider services,
    ILogger<MemoryFuse> log) : BackgroundService
{
    public const int ExitCode = 3;
    private FuseState state = FuseState.Initial;

    public bool Warning => state.Warned;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value.MemoryFuse;
        var interval = TimeSpan.FromSeconds(Math.Max(1, settings.SampleSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            var sample = metrics.Sample();
            if (settings.Enabled)
            {
                var mb = sample.PrivateBytes / (1024 * 1024);
                var (next, action) = MemoryFuseRules.Evaluate(state, mb, time.GetUtcNow().ToUnixTimeMilliseconds(), settings);
                state = next;
                switch (action)
                {
                    case FuseAction.Warn:
                        log.LogWarning("私有記憶體 {Mb} MB 超過告警門檻 {Warn} MB", mb, settings.WarnMb);
                        break;
                    case FuseAction.Recovered:
                        log.LogInformation("私有記憶體回到 {Mb} MB", mb);
                        break;
                    case FuseAction.Kill:
                        log.LogCritical("私有記憶體 {Mb} MB 超過 {Kill} MB 已持續 {Minutes} 分鐘：以結束碼 {Code} 退出，由 SCM 重啟", mb, settings.KillMb, settings.KillAfterMinutes, ExitCode);
                        RequestFailureExit(lifetime, services, log, ExitCode);
                        return;
                }
            }
            try
            {
                await Task.Delay(interval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>以非 0 結束碼結束服務（SCM 看得到的失敗），並在 60 秒後保險地直接結束程序</summary>
    public static void RequestFailureExit(IHostApplicationLifetime lifetime, IServiceProvider services, ILogger log, int code) =>
        _ = RequestFailureExit(lifetime, services, log, code, TimeSpan.FromSeconds(60), Environment.Exit);

    /// <summary>
    /// 結束碼同時設在 <see cref="Environment.ExitCode"/>（主控台執行時 Main 回傳它）與 <see cref="ServiceBase.ExitCode"/>
    /// （以服務執行時 SCM 只看服務回報的結束碼；非 0＋<c>sc.exe failureflag MyriagentHub 1</c> 才會觸發失敗動作重啟）。
    /// <paramref name="grace"/> 內沒停乾淨就呼叫 <paramref name="hardExit"/>（正式是 Environment.Exit；測試換掉）。
    /// </summary>
    internal static Task RequestFailureExit(IHostApplicationLifetime lifetime, IServiceProvider services, ILogger log, int code, TimeSpan grace, Action<int> hardExit)
    {
        Environment.ExitCode = code;
        if (services.GetService<IHostLifetime>() is ServiceBase service)
            service.ExitCode = code;
        lifetime.StopApplication();
        return Task.Run(async () =>
        {
            await Task.Delay(grace);
            log.LogCritical("{Seconds} 秒內沒有停止，直接結束程序", grace.TotalSeconds);
            hardExit(code);
        });
    }
}
