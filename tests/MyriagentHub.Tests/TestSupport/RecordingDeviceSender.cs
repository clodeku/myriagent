using System.Collections.Concurrent;
using Myriagent.Sync;
using MyriagentHub.Realtime;

namespace MyriagentHub.Tests.TestSupport;

/// <summary>記下伺服器 → 裝置的訊息（觀看與快照請求的測試；取代經 IHubContext 送的正式實作）</summary>
internal sealed class RecordingDeviceSender : IDeviceMessageSender
{
    private readonly ConcurrentQueue<(string ConnectionId, string Method, object Message)> sent = new();

    /// <summary>不是 null 時 SendAsync 丟這個例外（照樣記一次）</summary>
    public Exception? FailWith { get; set; }

    /// <summary>每送一則就呼叫（測試模擬裝置的反應）</summary>
    public Action<string, object>? OnSent { get; set; }

    public Task SendAsync(string connectionId, string method, object message, CancellationToken cancellationToken)
    {
        sent.Enqueue((connectionId, method, message));
        if (FailWith is { } failure)
            return Task.FromException(failure);
        OnSent?.Invoke(connectionId, message);
        return Task.CompletedTask;
    }

    public IReadOnlyList<(string ConnectionId, string Method, object Message)> All => sent.ToList();

    /// <summary>送給這條連線的 WatchChanged（依序）</summary>
    public List<IReadOnlyList<string>> Watches(string connectionId) =>
        sent.Where(s => s.ConnectionId == connectionId && s.Method == DeviceHubMethods.WatchChanged).Select(s => ((WatchChanged)s.Message).ConversationIds).ToList();

    /// <summary>送給這條連線的 SnapshotRequest（依序）</summary>
    public List<SnapshotRequest> Snapshots(string connectionId) =>
        sent.Where(s => s.ConnectionId == connectionId && s.Method == DeviceHubMethods.SnapshotRequest).Select(s => (SnapshotRequest)s.Message).ToList();

    /// <summary>全部的 SnapshotRequest（依序）</summary>
    public List<SnapshotRequest> Snapshots() =>
        sent.Where(s => s.Method == DeviceHubMethods.SnapshotRequest).Select(s => (SnapshotRequest)s.Message).ToList();

    public void Clear() => sent.Clear();
}
