using System.Collections.Concurrent;
using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Fakes;

// 第 5 階段的交接方法（sync-protocol 第 16 節，hand1；WP5.1 只立形狀）：收下每一次呼叫（依序），照各自的 Responder 回；沒設時回 not_available
// （同沒有交接服務的伺服器）。HandoffRequest、HandoffUpdate、LogRepair 由測試以 Session.Events 推
internal sealed partial class FakeSyncServer
{
    /// <summary>收到的交接呼叫（方法名稱與請求，依序）</summary>
    public ConcurrentQueue<(string Method, object Request)> HandoffCalls { get; } = new();

    public Func<HandoffPreviewRequest, HandoffPreviewResult>? HandoffPreviewResponder { get; set; }

    public Func<HandoffStartRequest, HandoffStartResult>? HandoffStartResponder { get; set; }

    public Func<HandoffCancelRequest, HandoffCancelResult>? HandoffCancelResponder { get; set; }

    public Func<HandoffProgress, HandoffAck>? HandoffProgressResponder { get; set; }

    public Func<HandoffReady, HandoffAck>? HandoffReadyResponder { get; set; }

    internal sealed partial class FakeSession
    {
        public Task<HandoffPreviewResult> HandoffPreviewAsync(HandoffPreviewRequest r, CancellationToken ct) => Call(nameof(DeviceHubMethods.HandoffPreview), ct, () =>
        {
            RequireProven();
            server.HandoffCalls.Enqueue((DeviceHubMethods.HandoffPreview, r));
            return server.HandoffPreviewResponder?.Invoke(r)
                   ?? new HandoffPreviewResult(null, null, false, null, false, null, null, null, null, null, HandoffCodes.NotAvailable);
        });

        public Task<HandoffStartResult> HandoffStartAsync(HandoffStartRequest r, CancellationToken ct) => Call(nameof(DeviceHubMethods.HandoffStart), ct, () =>
        {
            RequireProven();
            server.HandoffCalls.Enqueue((DeviceHubMethods.HandoffStart, r));
            return server.HandoffStartResponder?.Invoke(r) ?? new HandoffStartResult(null, HandoffCodes.NotAvailable);
        });

        public Task<HandoffCancelResult> HandoffCancelAsync(HandoffCancelRequest r, CancellationToken ct) => Call(nameof(DeviceHubMethods.HandoffCancel), ct, () =>
        {
            RequireProven();
            server.HandoffCalls.Enqueue((DeviceHubMethods.HandoffCancel, r));
            return server.HandoffCancelResponder?.Invoke(r) ?? new HandoffCancelResult(HandoffCodes.NotAvailable);
        });

        public Task<HandoffAck> HandoffProgressAsync(HandoffProgress p, CancellationToken ct) => Call(nameof(DeviceHubMethods.HandoffProgress), ct, () =>
        {
            RequireProven();
            server.HandoffCalls.Enqueue((DeviceHubMethods.HandoffProgress, p));
            return server.HandoffProgressResponder?.Invoke(p) ?? new HandoffAck(false, HandoffCodes.NotAvailable);
        });

        public Task<HandoffAck> HandoffReadyAsync(HandoffReady r, CancellationToken ct) => Call(nameof(DeviceHubMethods.HandoffReady), ct, () =>
        {
            RequireProven();
            server.HandoffCalls.Enqueue((DeviceHubMethods.HandoffReady, r));
            return server.HandoffReadyResponder?.Invoke(r) ?? new HandoffAck(false, HandoffCodes.NotAvailable);
        });
    }
}
