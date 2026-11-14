using Myriagent.Core;
using Myriagent.Remote;
using Myriagent.RemoteExec;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 對話之間互送訊息（myriagent-remote conversations／send；2026-11-15 使用者要求：A 電腦的對話 A1 的代理通知 B 電腦的對話 B1 的代理，單向通知）：
// - conversations：列出這台與其他電腦（複本）找得到的對話、所在的電腦與對話 ID（線上 id，每台都一樣）。
// - send：照 <電腦> 與 <對話 ID 或標題> 找到對話，前面加上來源與回覆方式（ConversationMessageRules.Compose），
//   在這台的直接照一般的送出（執行中插進這一輪或排隊），在其他電腦的以型錄的 send（路由 Owner）經伺服器送過去——
//   和桌面在副本上打字送出是同一條路，那台照舊驗信封、看「可以對話」的政策與閘門。只在 UI 執行緒
internal sealed partial class AgentHub
{
    /// <summary>找得到的對話（清單上的：不含草稿、臨時對話；複本的電腦名稱取名冊）</summary>
    internal List<ConversationTarget> ConversationTargets(RemoteExecDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var names = directory.Devices.GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
        var selfName = SelfDeviceName(directory);
        var list = new List<ConversationTarget>();
        foreach (var c in ListedConversations())
        {
            if (ConversationIds.ToWire(c.Meta.Id) is not { } wire)
                continue;
            if (c.IsReplica)
            {
                var device = c.ReplicaOwnerDeviceId;
                var name = device != null && names.TryGetValue(device, out var n) ? n : "其他電腦";
                list.Add(new ConversationTarget(wire, c.Meta.Title ?? string.Empty, device, name, false, c.Running, AwaitsUser(c), c.Meta.UpdatedAt));
            }
            else
            {
                list.Add(new ConversationTarget(wire, c.Meta.Title ?? string.Empty, directory.SelfDeviceId, selfName, true, c.Running, AwaitsUser(c), c.Meta.UpdatedAt));
            }
        }

        return list;
    }

    /// <summary>myriagent-remote 的 conversations 與 send（RemoteExecService 經 AppController 交到 UI 執行緒）：回一行的內容（{t:"conversations"|"sent"|"error"}）</summary>
    internal async Task<JObject> HandleConversationCli(string callerId, RemoteExecPipeRequest request, RemoteExecDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(directory);
        var all = ConversationTargets(directory);
        if (request.Op == RemoteExecPipeRules.OpConversations)
        {
            Func<ConversationTarget, bool> keep = _ => true;
            if (!string.IsNullOrWhiteSpace(request.Device))
            {
                var (device, self, _, problem) = ResolveMessageDevice(directory, request.Device);
                if (problem != null)
                    return CliError(RemoteExecPipeRules.ErrorUnknownDevice, problem);
                keep = self ? t => t.Self : t => !t.Self && t.DeviceId == device!.Id;
            }

            var items = new JArray();
            foreach (var t in all.Where(keep).OrderByDescending(t => t.UpdatedAt))
            {
                items.Add(new JObject
                {
                    ["id"] = t.Id,
                    ["title"] = t.Title,
                    ["device"] = t.DeviceName,
                    ["deviceId"] = t.DeviceId,
                    ["self"] = t.Self,
                    ["running"] = t.Running,
                    ["waiting"] = t.Waiting,
                    ["updatedAt"] = ToUnixMs(t.UpdatedAt),
                    ["current"] = t.Id == ConversationIds.ToWire(callerId),
                });
            }

            return new JObject { ["t"] = "conversations", ["self"] = SelfDeviceName(directory), ["paired"] = directory.Paired, ["items"] = items };
        }

        // send
        var (target, isSelf, deviceName, deviceProblem) = ResolveMessageDevice(directory, request.Device!);
        if (deviceProblem != null)
            return CliError(RemoteExecPipeRules.ErrorUnknownDevice, deviceProblem);
        var (found, problem2) = ConversationMessageRules.Resolve(all, deviceName, isSelf ? t => t.Self : t => !t.Self && t.DeviceId == target!.Id, request.Target!);
        if (found is null)
            return CliError(RemoteExecPipeRules.ErrorInvalid, problem2!);
        var callerWire = ConversationIds.ToWire(callerId) ?? callerId;
        if (found.Id == callerWire)
            return CliError(RemoteExecPipeRules.ErrorInvalid, "不能送給自己這個對話");
        var c = conversations.Values.FirstOrDefault(x => x.IsReplica != isSelf && ConversationIds.ToWire(x.Meta.Id) == found.Id);
        if (c is null)
            return CliError(RemoteExecPipeRules.ErrorInvalid, $"對話 {found.Id} 剛剛不見了（可能被刪除），訊息沒有送出");
        conversations.TryGetValue(callerId, out var caller);
        var text = ConversationMessageRules.Compose(SelfDeviceName(directory), directory.Paired ? directory.SelfDeviceId : null,
            caller?.Meta.Title ?? string.Empty, callerWire, request.Command!);
        var sent = new JObject { ["t"] = "sent", ["device"] = found.DeviceName, ["conversation"] = found.Id, ["title"] = found.Title };

        if (isSelf)
        {
            sent["queued"] = c.Running;
            Log.Info($"對話 {callerId} 經 myriagent-remote send 送訊息到這台的對話 {c.Meta.Id}（{request.Command!.Length} 字）");
            _ = SendAsync(new ScheduleEchoView(this), c, text);
            return sent;
        }

        if (!target!.Online)
            return CliError(RemoteExecPipeRules.ErrorOffline, $"「{deviceName}」目前不在線上，訊息沒有送出");
        if (peerSink is not { } sink || !PeerLive)
            return CliError(RemoteExecPipeRules.ErrorOffline, "這台目前沒有連上伺服器（或伺服器還不支援跨電腦操作），訊息沒有送出");

        var commandId = SyncIds.NewUlid(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var completion = new TaskCompletionSource<JObject?>(TaskCreationOptions.RunContinuationsAsynchronously);
        PrunePeerPending();
        peerPending[commandId] = new PeerPending(string.Empty, "send", Environment.TickCount64) { Completion = completion };
        Log.Info($"對話 {callerId} 經 myriagent-remote send 送訊息到「{deviceName}」的對話 {found.Id}（{commandId}，{request.Command!.Length} 字）");
        try
        {
            sink.SubmitPeerCommand(commandId, new JObject { ["cmd"] = "send", ["conversationId"] = c.Meta.Id, ["text"] = text });
        }
        catch (Exception ex)
        {
            Log.Error("myriagent-remote send 交給同步失敗", ex);
            peerPending.Remove(commandId);
            return CliError(RemoteExecPipeRules.ErrorOffline, "訊息沒有送出：交給同步失敗");
        }

        var first = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromMilliseconds(PeerHubRules.PendingMs)));
        // 等完之後可能不在 UI 執行緒（呼叫端沒有同步內容時）：狀態一律交回 UI 執行緒改
        host.BeginInvoke(() => peerPending.Remove(commandId));
        if (first != completion.Task || completion.Task.Result is not { } reply)
            return CliError(RemoteExecPipeRules.ErrorUnknown, $"等不到「{deviceName}」的結果，不知道訊息有沒有送到");
        if (reply.Value<string>("type") == "sendRejected")
            return CliError(RemoteExecPipeRules.ErrorRejected, $"「{deviceName}」沒有送出這則訊息" + (reply.Value<string>("reason") is { Length: > 0 } reason ? "：" + reason : string.Empty));
        var state = reply.Value<string>("state");
        var message = reply.Value<string>("message");
        return state switch
        {
            RemoteResultStates.Done => sent,
            RemoteResultStates.Rejected => CliError(RemoteExecPipeRules.ErrorRejected, message ?? $"「{deviceName}」拒絕了這則訊息（{reply.Value<string>("code") ?? "沒有原因"}；那台要開「可以對話」或「可以操作」）"),
            _ => CliError(RemoteExecPipeRules.ErrorUnknown, message ?? $"不知道「{deviceName}」有沒有收到這則訊息"),
        };
    }

    /// <summary>這台的名稱：名冊裡標成這台的那一筆，未配對時是電腦名稱</summary>
    private static string SelfDeviceName(RemoteExecDirectory directory)
        => directory.Devices.FirstOrDefault(d => d.Self)?.Name is { Length: > 0 } name ? name : Environment.MachineName;

    /// <summary>送訊息的電腦：「這台」、self 等一律是這台；未配對時只有這台（名稱是電腦名稱）；配對了照 myriagent-remote run 的規則找</summary>
    private static (RemoteExecDevice? Device, bool Self, string Name, string? Problem) ResolveMessageDevice(RemoteExecDirectory directory, string query)
    {
        if (ConversationMessageRules.IsSelfAlias(query))
            return (directory.Devices.FirstOrDefault(d => d.Self), true, SelfDeviceName(directory), null);
        if (!directory.Paired || directory.Devices.Count == 0)
        {
            return string.Equals(query.Trim(), Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                ? (null, true, SelfDeviceName(directory), null)
                : (null, false, query, "這台電腦還沒有和 MyriagentHub 配對，只能送給這台的對話（電腦寫「這台」）");
        }

        var (device, problem) = RemoteExecPipeRules.Resolve(directory.Devices, query);
        return device is null ? (null, false, query, problem) : (device, device.Self, device.Name, null);
    }

    private static JObject CliError(string code, string message) => new() { ["t"] = "error", ["code"] = code, ["message"] = message };
}
