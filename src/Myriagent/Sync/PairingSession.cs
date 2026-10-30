using Myriagent.Core;
using Myriagent.Security;

namespace Myriagent.Sync;

/// <summary>一次配對的輸入（協調者在 UI 執行緒組好）</summary>
/// <param name="Server">要連的伺服器（MYRIAGENT_SYNC_SERVER 覆寫時是它）</param>
/// <param name="PairedServer">配對的伺服器（syncPair.server 或預設；寫進 device.json）</param>
/// <param name="TryTpm">先試 TPM（MYRIAGENT_SYNC_NO_TPM=1 時 false）</param>
internal sealed record PairingRequest(
    Uri Server,
    Uri PairedServer,
    string DeviceName,
    string? MachineName,
    string? WindowsUser,
    bool DevTrustEnv,
    bool TryTpm,
    SyncPaths Paths,
    string AppVersion);

/// <summary>配對的進度回報（在呼叫 <see cref="PairingSession.RunAsync"/> 的同步內容上呼叫）</summary>
internal interface IPairingProgress
{
    /// <summary>拿到配對碼：顯示 userCode、網址與裝置指紋；<paramref name="expiresAt"/> 是本機算的到期時間（Unix 毫秒）</summary>
    void Started(PairStartResponse response, string deviceFingerprint, long expiresAt);

    /// <summary>失敗（取消不呼叫）；<paramref name="text"/> 是給人看的說明</summary>
    void Failed(string code, string text);
}

/// <summary>配對提交的結果（只用 BCL、Protocol 與桌面型別：未配對路徑的 S6 根型別）</summary>
internal sealed record PairingCommitResult(DeviceFileData Device);

/// <summary>
/// 配對流程（sync-protocol 2.1、2.3；計畫第 6 步）：
/// <list type="number">
/// <item>裝置名稱以 <see cref="PairingRules.CleanDeviceName"/> 清理，不合格立刻失敗。</item>
/// <item>寫 pending-key.json，再在背景執行緒建金鑰（TPM 可能要好幾秒）。</item>
/// <item><c>/pair/start</c>，回報配對碼與裝置指紋。</item>
/// <item>收到回應後才等 interval 再送 <c>/pair/token</c>（伺服器以到達時間差判斷太快），依 <see cref="PairingFlowRules.Next"/> 決定下一步。</item>
/// <item>核准後 <see cref="PairingFlowRules.ValidateApproval"/>，在背景執行緒 commit（正式是 SyncEngineLoader.CompletePairing：建 sync.db、最後寫 device.json＝提交點）。</item>
/// <item>刪 pending-key.json。</item>
/// </list>
/// 失敗、取消、驗證不過時，以 <see cref="PairingRecovery"/> 刪金鑰與 pending 檔（device.json 已經寫好、是同一把時不刪）。
/// 檔案與金鑰的工作都在背景執行緒，呼叫端（UI 執行緒）只等 await。配對輪詢期間只用 HttpClient 與 CNG（S6）。
/// </summary>
internal static class PairingSession
{
    /// <param name="commit">在背景執行緒呼叫；回傳後才算配對完成</param>
    /// <param name="keyFactory">(金鑰名稱, 先試 TPM) → 簽章者；null 用 <see cref="DeviceKey.Create(string, bool, Action{string}?)"/></param>
    /// <exception cref="PairingException">失敗（已呼叫 <see cref="IPairingProgress.Failed"/>）</exception>
    /// <exception cref="OperationCanceledException">取消</exception>
    public static async Task<PairingCommitResult> RunAsync(
        PairingRequest request,
        IPairingProgress progress,
        TimeProvider time,
        Func<SyncPaths, PairTokenResponse, DeviceFileData, PairingCommitResult> commit,
        CancellationToken ct,
        Func<string, bool, IDeviceSigner>? keyFactory = null,
        HttpMessageHandler? handler = null)
    {
        var name = PairingRules.CleanDeviceName(request.DeviceName);
        if (name is null)
            throw Fail(progress, PairingCodes.InvalidDeviceName);

        var paths = request.Paths;
        var keyName = DeviceKey.NewKeyName();
        IDeviceSigner? signer = null;
        var committed = false;
        try
        {
            signer = await CreateKeyAsync(paths, keyName, request.TryTpm, keyFactory ?? DefaultKeyFactory, time, progress, ct);
            using var client = new PairingClient(request.Server, request.PairedServer, request.DevTrustEnv, handler);

            PairStartResponse start;
            try
            {
                start = await client.StartAsync(
                    new PairStartRequest(name, request.MachineName, request.WindowsUser, signer.PublicSpki, signer.KeyStorage, request.AppVersion, SyncProtocol.Version),
                    ct);
            }
            catch (PairingException ex)
            {
                throw Fail(progress, ex.Code, ex.RetryAfter);
            }

            var startedAt = time.GetUtcNow();
            var localExpiry = startedAt.AddSeconds(start.ExpiresIn);
            progress.Started(start, P256KeyRules.Fingerprint(signer.PublicSpki), localExpiry.ToUnixTimeMilliseconds());

            var interval = start.Interval;
            var delay = TimeSpan.FromSeconds(Math.Max(1, interval));
            while (true)
            {
                await Task.Delay(delay, time, ct);
                var result = await client.PollAsync(start.DeviceCode, ct);
                if (result is PairPollResult.NetworkError network)
                    Log.Info($"配對輪詢失敗，稍後重試：{network.Detail}");
                var (step, next) = PairingFlowRules.Next(result, interval, time.GetUtcNow(), localExpiry);
                interval = next;
                switch (step)
                {
                    case PairStep.Wait wait:
                        delay = wait.Delay;
                        continue;
                    case PairStep.Approved approved:
                        var device = await CommitAsync(request, name, keyName, signer, approved.Response, time.GetUtcNow().ToUnixTimeMilliseconds(), commit, progress, ct);
                        committed = true;
                        await Task.Run(() => DeletePending(paths), CancellationToken.None);
                        return device;
                    case PairStep.Denied:
                        throw Fail(progress, PairingErrors.AccessDenied);
                    case PairStep.Expired:
                        throw Fail(progress, PairingErrors.ExpiredToken);
                    case PairStep.Fatal fatal:
                        throw Fail(progress, fatal.Code);
                    default:
                        throw new InvalidOperationException($"不認得的配對步驟：{step.GetType().Name}");
                }
            }
        }
        finally
        {
            var keyStorage = signer?.KeyStorage;
            signer?.Dispose();
            if (!committed)
            {
                // 失敗或取消：刪這次建的金鑰、pending 檔與殘留的 sync.db（device.json 損毀或讀不到時也刪這把：commit 沒完成就不可能是它的）。
                // 取消時也要做完
                await Task.Run(() => PairingRecovery.Abandon(paths, keyName, keyStorage, Log.Info), CancellationToken.None);
            }
        }
    }

    private static IDeviceSigner DefaultKeyFactory(string keyName, bool tryTpm) => DeviceKey.Create(keyName, tryTpm, Log.Info);

    /// <summary>先寫 pending-key.json 再建金鑰（背景執行緒）；建好後依實際的存放位置改寫 pending 檔</summary>
    private static async Task<IDeviceSigner> CreateKeyAsync(
        SyncPaths paths, string keyName, bool tryTpm, Func<string, bool, IDeviceSigner> factory, TimeProvider time, IPairingProgress progress, CancellationToken ct)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        try
        {
            return await Task.Run(() =>
            {
                PendingKeyRecord.Write(paths.PendingKeyFile, new PendingKeyRecord(keyName, tryTpm ? PairingRules.KeyStorageTpm : PairingRules.KeyStorageSoftware, now));
                var signer = factory(keyName, tryTpm);
                try
                {
                    if (!PairingRules.IsKeyStorage(signer.KeyStorage) || !P256KeyRules.IsValidSpki(signer.PublicSpki))
                        throw new System.Security.Cryptography.CryptographicException("裝置金鑰不是 P-256");
                    PendingKeyRecord.Write(paths.PendingKeyFile, new PendingKeyRecord(keyName, signer.KeyStorage, now));
                    return signer;
                }
                catch
                {
                    signer.Dispose();
                    throw;
                }
            }, ct);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"建立裝置金鑰失敗：{ex.Message}");
            throw Fail(progress, PairingCodes.KeyFailed);
        }
    }

    private static async Task<PairingCommitResult> CommitAsync(
        PairingRequest request,
        string name,
        string keyName,
        IDeviceSigner signer,
        PairTokenResponse response,
        long pairedAt,
        Func<SyncPaths, PairTokenResponse, DeviceFileData, PairingCommitResult> commit,
        IPairingProgress progress,
        CancellationToken ct)
    {
        if (PairingFlowRules.ValidateApproval(response, signer.PublicSpki) is { } problem)
        {
            // 原因不含權杖（D16）
            Log.Warn($"配對回應驗證失敗：{problem}");
            throw Fail(progress, PairingCodes.InvalidApproval);
        }

        try
        {
            return await Task.Run(() =>
            {
                var device = new DeviceFileData(
                    DeviceFileData.CurrentVersion,
                    response.DeviceId,
                    name,
                    request.PairedServer.GetLeftPart(UriPartial.Authority),
                    pairedAt,
                    DeviceSecrets.Protect(response.DeviceToken),
                    keyName,
                    signer.KeyStorage,
                    signer.PublicSpki,
                    response.HubIdentitySpki);
                return commit(request.Paths, response, device);
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not PairingException)
        {
            // 記錄只放例外的型別與訊息（commit 不會把權杖放進例外訊息）
            Log.Warn($"配對提交失敗：{ex.GetType().Name}：{ex.Message}");
            throw Fail(progress, PairingCodes.CommitFailed);
        }
    }

    private static void DeletePending(SyncPaths paths)
    {
        try
        {
            File.Delete(paths.PendingKeyFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 下次啟動 PairingRecovery 會看到 device.json 是同一把，只刪 pending 檔
            Log.Warn($"刪不掉 pending-key.json：{ex.Message}");
        }
    }

    private static PairingException Fail(IPairingProgress progress, string code, TimeSpan? retryAfter = null)
    {
        var text = PairingFlowRules.ErrorText(code);
        if (retryAfter is { } wait && wait > TimeSpan.Zero)
            text += $"（約 {Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes))} 分鐘後）";
        progress.Failed(code, text);
        return new PairingException(code, retryAfter);
    }
}
