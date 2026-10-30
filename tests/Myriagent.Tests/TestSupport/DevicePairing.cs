using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;

namespace Myriagent.Tests.TestSupport;

/// <summary>配對好的一台裝置：device.json 與 sync.db 已在 <see cref="Paths"/>；<see cref="SignerFactory"/> 給引擎開同一把金鑰</summary>
internal sealed record PairedDevice(
    SyncPaths Paths,
    DeviceFileData Device,
    Func<DeviceFileData, IDeviceSigner?> SignerFactory,
    InMemoryDeviceSigner? MemoryKey,
    PairStartResponse Start)
{
    public string DeviceId => Device.DeviceId;
}

/// <summary>進行中的一次配對（拿到配對碼、還在輪詢）</summary>
internal sealed class PairingAttempt
{
    public required PairStartResponse Start { get; init; }

    public required string DeviceFingerprint { get; init; }

    public required Task<PairingCommitResult> Run { get; init; }

    public required RecordingPairingProgress Progress { get; init; }

    /// <summary>同一把記憶體金鑰的複本（給之後的引擎；預設 keyFactory 時才有）</summary>
    public InMemoryDeviceSigner? MemoryKey { get; init; }

    /// <summary>交給配對流程的那個實例（流程結束時要被 Dispose）</summary>
    public InMemoryDeviceSigner? IssuedKey { get; init; }
}

/// <summary>記下配對進度（拿到配對碼時完成 <see cref="StartedTask"/>）</summary>
internal sealed class RecordingPairingProgress : IPairingProgress
{
    private readonly TaskCompletionSource<(PairStartResponse Response, string Fingerprint)> started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<(PairStartResponse Response, string Fingerprint)> StartedTask => started.Task;

    public string? FailedCode { get; private set; }

    public void Started(PairStartResponse response, string deviceFingerprint, long expiresAt) => started.TrySetResult((response, deviceFingerprint));

    public void Failed(string code, string text)
    {
        FailedCode = code;
        started.TrySetException(new InvalidOperationException($"配對在拿到配對碼之前失敗：{code} {text}"));
    }
}

/// <summary>
/// 對 MyriagentHub 子程序跑完整的配對（<see cref="PairingSession"/>，commit 用正式的 <see cref="SyncEngineLoader.CompletePairing"/>）。
/// 預設的金鑰是記憶體裡的 P-256（<see cref="InMemoryDeviceSigner"/>，不碰 CNG 的金鑰存放區）；開發信任開著（自簽憑證、迴路位址）、不試 TPM
/// </summary>
internal static class DevicePairing
{
    public const string AppVersion = "0.5.0";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>送出配對、拿到配對碼就回傳（還沒核准）</summary>
    public static async Task<PairingAttempt> BeginAsync(
        MyriagentHubProcess hub,
        SyncPaths paths,
        Func<string, bool, IDeviceSigner>? keyFactory = null,
        string deviceName = "測試電腦",
        CancellationToken ct = default)
    {
        var progress = new RecordingPairingProgress();
        InMemoryDeviceSigner? memoryKey = null;
        InMemoryDeviceSigner? issuedKey = null;
        keyFactory ??= (_, _) =>
        {
            issuedKey = new InMemoryDeviceSigner();
            memoryKey = issuedKey.Clone();
            return issuedKey;
        };
        var request = new PairingRequest(hub.BaseAddress, hub.BaseAddress, deviceName, "TEST-PC", "tester", DevTrustEnv: true, TryTpm: false, paths, AppVersion);
        var run = PairingSession.RunAsync(request, progress, TimeProvider.System, SyncEngineLoader.CompletePairing, ct, keyFactory);
        var (start, fingerprint) = await progress.StartedTask.WaitAsync(Timeout, CancellationToken.None);
        return new PairingAttempt { Start = start, DeviceFingerprint = fingerprint, Run = run, Progress = progress, MemoryKey = memoryKey, IssuedKey = issuedKey };
    }

    /// <summary>配對並由擁有者核准；回傳配對好的裝置</summary>
    public static async Task<PairedDevice> PairAsync(
        MyriagentHubProcess hub,
        OwnerClient owner,
        SyncPaths paths,
        Func<string, bool, IDeviceSigner>? keyFactory = null,
        string deviceName = "測試電腦")
    {
        var attempt = await BeginAsync(hub, paths, keyFactory, deviceName);
        await owner.ApproveAsync(attempt.Start.UserCode);
        var result = await attempt.Run.WaitAsync(Timeout, CancellationToken.None);
        var memoryKey = attempt.MemoryKey;
        Func<DeviceFileData, IDeviceSigner?> signerFactory = memoryKey is null
            ? d => DeviceKey.Open(d.KeyName, d.KeyStorage)
            : _ => memoryKey.Clone();
        return new PairedDevice(paths, result.Device, signerFactory, memoryKey, attempt.Start);
    }
}
