using System.Collections.Concurrent;
using Myriagent.Sync;
using MyriagentHub.Realtime;

namespace MyriagentHub.Sync;

/// <summary>
/// 輸入框草稿給桌面的轉送（draft1；docs/sync-protocol.md 14.17）：協商出 draft1 的已證明連線（只更新的除外）在證明完成
/// <see cref="ComposerDraftRules.FirstSendDelay"/> 之後收一份整份的 <c>ComposerDraftUpdate</c>（<c>full</c>），之後每次有草稿變了就收那一則
/// （送來的那條連線不送回去）。每條連線的送出依序（一把鎖）：整份送出之前變了的不另外送（整份是在鎖裡取的，已經包含它）
/// </summary>
internal sealed class ComposerDraftFanout(
    ComposerDraftStore store,
    ConnectionRegistry registry,
    IDeviceMessageSender sender,
    TimeProvider time,
    ILogger<ComposerDraftFanout> log) : IHostedService
{
    private readonly ConcurrentDictionary<string, Target> targets = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stopping = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        store.Changed += OnChanged;
        registry.ConnectionProven += OnConnectionProven;
        registry.ConnectionRemoved += OnConnectionRemoved;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        store.Changed -= OnChanged;
        registry.ConnectionProven -= OnConnectionProven;
        registry.ConnectionRemoved -= OnConnectionRemoved;
        stopping.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>送來的那條裝置連線的 origin（<see cref="ComposerDraftStore.Changed"/>）</summary>
    public static string DeviceOrigin(string connectionId) => "d:" + connectionId;

    private void OnConnectionProven(DeviceConnection connection)
    {
        if (connection.UpdateOnly || !connection.Allows(DeviceHubMethods.ComposerDraftUpdate))
            return;
        var target = new Target(connection);
        targets[connection.ConnectionId] = target;
        _ = SendFullLaterAsync(target);
    }

    private void OnConnectionRemoved(DeviceConnection connection)
    {
        if (targets.TryGetValue(connection.ConnectionId, out var target) && ReferenceEquals(target.Connection, connection))
            targets.TryRemove(new KeyValuePair<string, Target>(connection.ConnectionId, target));
    }

    private void OnChanged(ComposerDraftView view, string? origin)
    {
        foreach (var target in targets.Values)
        {
            if (origin == DeviceOrigin(target.Connection.ConnectionId))
                continue;
            _ = SendAsync(target, () => target.Primed ? new ComposerDraftUpdate([view], Full: false) : null);
        }
    }

    private async Task SendFullLaterAsync(Target target)
    {
        try
        {
            await Task.Delay(ComposerDraftRules.FirstSendDelay, time, stopping.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await SendAsync(target, () =>
        {
            target.Primed = true;
            return new ComposerDraftUpdate(store.Snapshot(), Full: true);
        });
    }

    /// <summary>在這條連線的鎖裡組訊息（null＝不送）再送</summary>
    private async Task SendAsync(Target target, Func<ComposerDraftUpdate?> build)
    {
        try
        {
            await target.Gate.WaitAsync(stopping.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            if (!ReferenceEquals(registry.Get(target.Connection.ConnectionId), target.Connection) || build() is not { } update)
                return;
            await sender.SendAsync(target.Connection.ConnectionId, DeviceHubMethods.ComposerDraftUpdate, update, stopping.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogDebug(ex, "ComposerDraftUpdate 送不到裝置連線 {Connection}", target.Connection.ConnectionId);
        }
        catch (OperationCanceledException)
        {
            // 停機
        }
        finally
        {
            target.Gate.Release();
        }
    }

    private sealed class Target(DeviceConnection connection)
    {
        public DeviceConnection Connection { get; } = connection;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public volatile bool Primed;
    }
}

/// <summary>輸入框草稿（draft1）的登記：存放（<see cref="ComposerDraftStore"/>，停機時寫檔）、給桌面的轉送，宣告 draft1。瀏覽器那一側在瀏覽器閘道</summary>
internal static class ComposerDraftRegistration
{
    public static IServiceCollection AddHubComposerDrafts(this IServiceCollection services)
    {
        services.AddSingleton<ComposerDraftStore>();
        services.AddHostedService(sp => sp.GetRequiredService<ComposerDraftStore>());
        services.AddSingleton<ComposerDraftFanout>();
        services.AddHostedService(sp => sp.GetRequiredService<ComposerDraftFanout>());
        services.AddDeviceHubFeature(DeviceFeatureRules.Draft1);
        return services;
    }
}
