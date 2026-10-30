using Myriagent.Remote;
using Myriagent.Sync;
using MyriagentHub.Sync;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using HubOptions = MyriagentHub.Hosting.HubOptions;

namespace MyriagentHub.Realtime;

/// <summary>
/// 裝置 hub 的 <c>PeerCmd</c>（桌面遠端操作，sync-protocol 14.7；擁有者 2026-10-22 決定「已配對就能操作」）：桌面對其他電腦下的指令，依序：
/// <list type="number">
/// <item>連線已證明、協商出 <c>peer1</c> 與 <c>cmd1</c>（否則 <c>not_available</c>）；每台電腦一個權杖桶（同瀏覽器每個工作階段的速率，超過回 <c>rate_limited</c>）。</item>
/// <item>以瀏覽器 <c>Cmd</c> 的同一套解析（<see cref="MessageGuard.ParseCmd"/>）檢查形狀與上限；只收型錄路由 Owner／Device 的指令（瀏覽器版判成 not_available、
/// 由指令路由轉送的那些），其他（伺服器自己處理的、桌面專屬的、沒列名的）回 <c>not_available</c>。</item>
/// <item>交給 <see cref="CommandRouter.RouteAsync(CommandOrigin, string?, RemoteCommandSpec, JObject, CancellationToken)"/>（發起者是 <see cref="DeviceCommandOrigin"/>）。</item>
/// </list>
/// 在 hub 的呼叫執行緒執行；路由丟出的例外記錄後回 <c>unavailable</c>
/// </summary>
internal sealed class PeerCommandHandler(CommandRouter router, IOptions<HubOptions> options, TimeProvider time, ILogger<PeerCommandHandler> log)
{
    private readonly object gate = new();
    private readonly Dictionary<string, TokenBucket> buckets = new(StringComparer.Ordinal);

    public async Task<PeerCmdResult> HandleAsync(DeviceConnection? connection, PeerCmd? request, string? ip, CancellationToken cancellationToken)
    {
        var commandId = request is not null && SyncIds.IsUlid(request.CommandId) ? request.CommandId : null;
        if (connection is null || !connection.IsProven || !connection.Allows(DeviceHubMethods.PeerCmd) || !connection.Allows(DeviceHubMethods.CommandDelivery))
            return new PeerCmdResult(false, commandId, BrowserErrorCodes.NotAvailable);
        if (!TryTake(connection.DeviceId))
            return new PeerCmdResult(false, commandId, BrowserErrorCodes.RateLimited);
        if (request is null || request.Cmd is null)
            return new PeerCmdResult(false, commandId, BrowserErrorCodes.Invalid);

        // 桌面在其他電腦開新對話（瀏覽器沒有這個網頁指令，由伺服器草稿組；sync-protocol 14.7）：形狀與上限由型錄檢查
        if (RemoteCommandShapeRules.IsString(request.Cmd["cmd"], out var cmdName) && cmdName == RemoteCommandCatalog.ConversationCreate)
        {
            if (commandId is null)
                return new PeerCmdResult(false, null, BrowserErrorCodes.Invalid);
            try
            {
                var created = await router.RouteConversationCreateAsync(new DeviceCommandOrigin(connection.DeviceId, connection.ConnectionId, ip), commandId, request.Cmd, cancellationToken);
                return new PeerCmdResult(created.Accepted, created.CommandId, created.Error);
            }
            catch (OperationCanceledException)
            {
                return new PeerCmdResult(false, commandId, BrowserErrorCodes.Unavailable);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "電腦 {DeviceId} 的 conversationCreate（桌面遠端操作）路由失敗", connection.DeviceId);
                return new PeerCmdResult(false, commandId, BrowserErrorCodes.Unavailable);
            }
        }

        // 遠端執行指令（sync-protocol 14.11）：不是網頁指令（MessageGuard 不認得），形狀與上限由型錄檢查，之後同一般的 Device 路由
        if (cmdName == RemoteCommandCatalog.RemoteExec)
        {
            if (commandId is null)
                return new PeerCmdResult(false, null, BrowserErrorCodes.Invalid);
            try
            {
                var result = await router.RouteAsync(new DeviceCommandOrigin(connection.DeviceId, connection.ConnectionId, ip), commandId,
                    RemoteCommandCatalog.Find(RemoteCommandCatalog.RemoteExec)!, request.Cmd, cancellationToken);
                return new PeerCmdResult(result.Accepted, result.CommandId, result.Error);
            }
            catch (OperationCanceledException)
            {
                return new PeerCmdResult(false, commandId, BrowserErrorCodes.Unavailable);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "電腦 {DeviceId} 的 remoteExec 路由失敗", connection.DeviceId);
                return new PeerCmdResult(false, commandId, BrowserErrorCodes.Unavailable);
            }
        }

        var parsed = MessageGuard.ParseCmd(new JObject { ["commandId"] = request.CommandId, ["cmd"] = request.Cmd });
        if (parsed.CommandId is null)
            return new PeerCmdResult(false, null, BrowserErrorCodes.Invalid);
        if (parsed.Error != BrowserErrorCodes.NotAvailable || parsed.CmdName is not { } name || parsed.Body is not { } body
            || RemoteCommandCatalog.Find(name) is not { Route: RemoteRoute.Owner or RemoteRoute.Device } spec)
            return new PeerCmdResult(false, parsed.CommandId, parsed.Error == BrowserErrorCodes.Invalid ? BrowserErrorCodes.Invalid : BrowserErrorCodes.NotAvailable);

        try
        {
            var result = await router.RouteAsync(new DeviceCommandOrigin(connection.DeviceId, connection.ConnectionId, ip), parsed.CommandId, spec, body, cancellationToken);
            return new PeerCmdResult(result.Accepted, result.CommandId, result.Error);
        }
        catch (OperationCanceledException)
        {
            return new PeerCmdResult(false, parsed.CommandId, BrowserErrorCodes.Unavailable);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "電腦 {DeviceId} 的 {Cmd}（桌面遠端操作）路由失敗", connection.DeviceId, name);
            return new PeerCmdResult(false, parsed.CommandId, BrowserErrorCodes.Unavailable);
        }
    }

    private bool TryTake(string deviceId)
    {
        var limits = options.Value.RateLimits;
        if (!limits.Enabled)
            return true;
        var nowMs = time.GetUtcNow().ToUnixTimeMilliseconds();
        lock (gate)
        {
            if (!buckets.TryGetValue(deviceId, out var bucket))
            {
                // 補滿的桶等同新建的：桶太多時先丟掉補滿的（不會讓人多拿到權杖）
                if (buckets.Count >= MaxBuckets)
                {
                    foreach (var full in buckets.Where(b => b.Value.IsFull(nowMs)).Select(b => b.Key).ToList())
                        buckets.Remove(full);
                }
                buckets[deviceId] = bucket = new TokenBucket(limits.BrowserCmdPerSecond, Math.Max(1, limits.BrowserCmdBurst), nowMs);
            }
            return bucket.TryTake(nowMs);
        }
    }

    private const int MaxBuckets = 256;
}
