using System.Collections.Concurrent;
using Myriagent.Sync;
using MyriagentHub.Audit;
using MyriagentHub.Auth;
using MyriagentHub.Data;
using MyriagentHub.Sync;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace MyriagentHub.Realtime;

/// <summary>Prove 時 device_streams 的狀態：伺服器期待的下一個 deviceSeq、是否要對帳、上一次的啟動 id 與版本</summary>
internal sealed record StreamState(long NextDeviceSeq, bool NeedsResync, string? PreviousBootId, string? PreviousAppVersion);

/// <summary>
/// 裝置 hub 在 hub 實例之外的部分（hub 實例每次呼叫就建一個）：連線的批次佇列、上下線的發布、心跳寫入的節流、裝置權杖輪換
/// </summary>
internal sealed class DeviceHubRuntime(
    IHubContext<DeviceHub> hub,
    EventLog eventLog,
    Catalog catalog,
    SyncWriter writer,
    ConnectionRegistry connections,
    DeviceDirectory devices,
    IServiceScopeFactory scopes,
    HubWriteLock hubWriteLock,
    AuditLog audit,
    DeviceHubFeatures features,
    ILogger<DeviceHubRuntime> log)
{
    /// <summary>完整同步的已證明連線（Notify、TrustChanged、ServerNotice）；只更新的連線在 <see cref="UpdateGroup"/></summary>
    public const string Group = "devices";

    /// <summary>只更新的已證明連線（sync-protocol 1.4）：只收 ServerNotice（shutdown、appRelease），不收 Notify、TrustChanged</summary>
    public const string UpdateGroup = "devices-update";

    /// <summary>
    /// 伺服器在 HelloResponse 宣告的功能（sync-protocol 14.1；各工作包登記的 conv1、live1、cmd1、media1，U2 起再加 AddHubReleases 的 update1）；
    /// 沒有任何登記時是 null（第 3 階段的行為，只有測試會拿掉正式的登記）
    /// </summary>
    public IReadOnlyList<string>? ServerFeatures => features.Declared;

    private readonly ConcurrentDictionary<string, long> heartbeatPersisted = new(StringComparer.Ordinal);

    /// <summary>開始處理這條連線的批次佇列</summary>
    public void Start(DeviceConnection connection) => connection.Pump = Task.Run(() => PumpAsync(connection));

    /// <summary>Prove 成功：記下這次的啟動 id、版本、政策與時間，發布上線的 device.upsert（連線已登記，online＝true）</summary>
    public Task<StreamState> RecordHelloAsync(HelloState hello, long now) =>
        writer.WriteAsync(context =>
        {
            Catalog.EnsureStream(context, hello.DeviceId);
            StreamState state;
            using (var cmd = context.Command("SELECT next_device_seq, needs_resync, boot_id, app_version FROM device_streams WHERE device_id = $d", ("$d", hello.DeviceId)))
            using (var reader = cmd.ExecuteReader())
            {
                reader.Read();
                state = new StreamState(reader.GetInt64(0), reader.GetInt64(1) != 0, reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3));
            }
            context.Execute(
                "UPDATE device_streams SET boot_id = $boot, app_version = $app, last_hello_at = $now, last_heartbeat_at = $now, policy_json = $policy WHERE device_id = $d",
                ("$boot", hello.BootId), ("$app", hello.AppVersion), ("$now", now), ("$policy", SyncJson.Serialize(hello.Policy)), ("$d", hello.DeviceId));
            if (devices.Get(hello.DeviceId) is { Revoked: false })
                catalog.EmitDevice(context, hello.DeviceId);
            return state;
        });

    /// <summary>
    /// 連線結束：停止收批次（已排進來的照樣做完）、這台沒有別的連線時發布離線的 device.upsert。還有別的連線（重連時舊的還沒斷）時上線狀態不變，
    /// 只在 device.upsert 的 <c>features</c>（取最新的那條連線的，<see cref="ConnectionRegistry.FeaturesOf"/>）因此改變時重發（sync-protocol 14.1）
    /// </summary>
    public async Task OnDisconnectedAsync(string connectionId)
    {
        var before = connections.Get(connectionId) is { } current ? connections.FeaturesOf(current.DeviceId) : null;
        if (connections.Remove(connectionId) is not { } connection)
            return;
        connection.Batches.Writer.TryComplete();
        if (connections.IsOnline(connection.DeviceId))
        {
            if (before is null || connections.FeaturesOf(connection.DeviceId).SequenceEqual(before, StringComparer.Ordinal))
                return;
        }
        else
        {
            heartbeatPersisted.TryRemove(connection.DeviceId, out _);
        }
        try
        {
            await writer.WriteAsync(context =>
            {
                if (devices.Get(connection.DeviceId) is { Revoked: false })
                    catalog.EmitDevice(context, connection.DeviceId);
            });
        }
        catch (Exception ex)
        {
            // 服務停止中（寫入者已關）
            log.LogDebug(ex, "發布離線狀態失敗：{DeviceId}", connection.DeviceId);
        }
    }

    /// <summary>心跳：記憶體裡每次更新；device_streams 每 60 秒最多寫一次</summary>
    public async Task HeartbeatAsync(string deviceId, long uiAliveAt, long now)
    {
        connections.Heartbeat(deviceId, now);
        if (heartbeatPersisted.TryGetValue(deviceId, out var last) && now - last < DeviceHubRules.HeartbeatPersistIntervalMs)
            return;
        heartbeatPersisted[deviceId] = now;
        await writer.WriteAsync(context => context.Execute(
            "UPDATE device_streams SET last_heartbeat_at = $now, last_ui_alive_at = $ui WHERE device_id = $d", ("$now", now), ("$ui", uiAliveAt), ("$d", deviceId)));
    }

    /// <summary>
    /// Prove 時依 <see cref="DeviceHubRules.RotationFor"/> 換發裝置權杖（hub.db，寫入鎖內先讀再寫）；回傳新的權杖（不換為 null）。
    /// 新權杖只在已釘選驗證過的通道內送出（ProveResponse.tokenRotation）。
    /// </summary>
    public async Task<string?> RotateTokenIfDueAsync(string deviceId, bool usedPreviousToken, long now)
    {
        var token = await hubWriteLock.RunAsync(async () =>
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId);
            if (device is null || device.RevokedAt is not null)
                return null;
            var rotation = DeviceHubRules.RotationFor(usedPreviousToken, device.TokenIssuedAt ?? device.CreatedAt, now);
            if (rotation == TokenRotation.None)
                return null;
            var fresh = DeviceTokenRules.NewToken();
            if (rotation == TokenRotation.Rotate)
            {
                device.PrevTokenHash = device.TokenHash;
                device.PrevTokenExpires = now + DeviceHubRules.PreviousTokenGraceMs;
            }
            device.TokenHash = DeviceTokenRules.Hash(fresh);
            device.TokenIssuedAt = now;
            await db.SaveChangesAsync();
            return (string?)fresh;
        });
        if (token is not null)
        {
            await audit.WriteAsync(new AuditEntry(AuditActors.Device, deviceId, null, AuditActions.DeviceTokenRotated, deviceId, AuditOutcomes.Ok,
                new { reissue = usedPreviousToken }));
        }
        return token;
    }

    private async Task PumpAsync(DeviceConnection connection)
    {
        await foreach (var batch in connection.Batches.Reader.ReadAllAsync())
        {
            BatchOutcome outcome;
            try
            {
                outcome = await eventLog.AppendBatchAsync(connection.Upstream, batch);
            }
            catch (DeviceRevokedException)
            {
                log.LogInformation("裝置已撤銷，丟掉佇列裡的批次並中斷連線：{DeviceId}", connection.DeviceId);
                connection.Batches.Writer.TryComplete();
                connection.Abort();
                return;
            }
            catch (Exception ex)
            {
                // 寫入失敗（磁碟、資料庫、服務停止中）：這一批沒有生效；中斷連線，裝置重連時以 Prove 重新對齊
                log.LogError(ex, "批次寫入失敗，中斷裝置連線：{DeviceId}", connection.DeviceId);
                connection.Batches.Writer.TryComplete();
                connection.Abort();
                return;
            }
            if (outcome.ResyncDone)
                connection.Upstream = connection.Upstream with { ResyncFrom = null };
            try
            {
                await hub.Clients.Client(connection.ConnectionId).SendAsync(DeviceHubMethods.BatchAck, outcome.Ack);
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "BatchAck 送不出去（連線已斷）：{DeviceId}", connection.DeviceId);
            }
        }
    }
}
