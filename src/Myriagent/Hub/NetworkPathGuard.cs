using Myriagent.Core;
using Myriagent.Sync;

namespace Myriagent.Hub;

/// <summary>
/// 網路芳鄰路徑的守門（電腦狀態面板的檔案傳輸；本機磁碟照舊走 <see cref="RemoteFileGuard"/>）：<c>\\主機\分享\…</c> 與對應的網路磁碟機上的路徑。
/// 先照 <see cref="NetworkPathRules"/> 驗格式、主機（只收區域網路、不收這台自己）與分享，再在逾時之內碰檔案系統；憑證類檔名照樣不給。
/// 網路磁碟機以它對應的 UNC 判斷主機。任何執行緒
/// </summary>
internal sealed class NetworkPathGuard(INetworkShares shares, TimeSpan timeout)
{
    /// <summary>網路操作的逾時（遠端指令要在 30 秒內回）</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(12);

    public INetworkShares Shares { get; } = shares;

    public TimeSpan Timeout { get; } = timeout;

    /// <summary>網路路徑：「網路」這一層、UNC，或對應的網路磁碟機上的路徑</summary>
    public bool IsNetworkPath(string? path)
        => NetworkPathRules.IsNetworkRoot(path) || NetworkPathRules.LooksUnc(path) || MappedTargetOf(path) is not null;

    /// <summary>對應的網路磁碟機上的路徑 → 等效的 UNC（不是網路磁碟機回 null）</summary>
    public string? MappedTargetOf(string? path)
    {
        if (path is not { Length: >= 3 } || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
            return null;
        var root = path[..3].ToUpperInvariant();
        foreach (var (mappedRoot, target) in Shares.MappedDrives())
        {
            if (string.Equals(mappedRoot, root, StringComparison.OrdinalIgnoreCase))
                return target.TrimEnd('\\') + (path.Length > 3 ? @"\" + path[3..].TrimEnd('\\') : string.Empty);
        }

        return null;
    }

    /// <summary>主機能不能連（null＝可以）；解析逾時算 <see cref="NetworkPathReasons.Timeout"/></summary>
    public async Task<string?> CheckHostAsync(string host, CancellationToken ct)
    {
        try
        {
            var resolved = await Shares.ResolveAsync(host, ct).WaitAsync(Timeout, ct).ConfigureAwait(false);
            return NetworkPathRules.HostVerdict(host, resolved, Shares.LocalAddresses(), Shares.MachineName);
        }
        catch (TimeoutException)
        {
            return NetworkPathReasons.Timeout;
        }
    }

    /// <summary>格式、主機與分享（不碰檔案系統）；回拆好的 UNC 或拒絕原因。<paramref name="needShare"/>：一定要指到分享以下</summary>
    public async Task<(string? Host, string? Share, string? Reason)> CheckPathAsync(string path, bool needShare, CancellationToken ct)
    {
        var unc = NetworkPathRules.LooksUnc(path) ? path : MappedTargetOf(path);
        if (!NetworkPathRules.TryParse(unc, out var host, out var share, out _) || needShare && share is null)
            return (null, null, NetworkPathReasons.Invalid);
        if (share is not null && NetworkPathRules.IsBlockedShare(share))
            return (null, null, NetworkPathReasons.Share);
        return await CheckHostAsync(host, ct).ConfigureAwait(false) is { } reason ? (null, null, reason) : (host, share, null);
    }

    /// <summary>資料夾：路徑驗得過、而且在逾時之內確認存在</summary>
    public async Task<(string? Directory, string? Reason)> CheckDirectoryAsync(string path, CancellationToken ct)
    {
        var (_, _, reason) = await CheckPathAsync(path, needShare: true, ct).ConfigureAwait(false);
        if (reason is not null)
            return (null, reason);
        var directory = path.Length > 3 ? path.TrimEnd('\\') : path;
        var exists = await RunAsync(() => Directory.Exists(directory), ct).ConfigureAwait(false);
        return exists switch
        {
            null => (null, NetworkPathReasons.Timeout),
            false => (null, NetworkPathReasons.NotFound),
            _ => (directory, null),
        };
    }

    /// <summary>開檔（唯讀、允許別人同時讀寫刪）：路徑驗得過、不在檔名拒絕清單、逾時之內開得了</summary>
    public async Task<(GuardedFile? File, string? Reason)> OpenAsync(string path, CancellationToken ct)
    {
        var (_, _, reason) = await CheckPathAsync(path, needShare: true, ct).ConfigureAwait(false);
        if (reason is not null)
            return (null, reason);
        if (RemoteFileGuardRules.IsForbiddenFileName(path))
            return (null, NetworkPathReasons.Name);
        GuardedFile? opened = null;
        var done = await RunAsync(() =>
        {
            try
            {
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
                var info = new FileInfo(path);
                opened = new GuardedFile(stream, path, stream.Length, info.LastWriteTimeUtc);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Log.Info($"開不了網路上的檔：{ex.Message}");
                return false;
            }
        }, ct).ConfigureAwait(false);
        if (done is null)
        {
            // 逾時：之後才開好的檔由背景收掉
            _ = Task.Run(async () =>
            {
                await Task.Delay(Timeout * 2, CancellationToken.None).ConfigureAwait(false);
                opened?.Dispose();
            }, CancellationToken.None);
            return (null, NetworkPathReasons.Timeout);
        }

        return done == true && opened is not null ? (opened, null) : (null, NetworkPathReasons.NotFound);
    }

    /// <summary>在背景跑會碰網路的同步呼叫；逾時回 null（那條執行緒自己跑完）</summary>
    public async Task<T?> RunAsync<T>(Func<T> work, CancellationToken ct) where T : struct
    {
        try
        {
            return await Task.Run(work, CancellationToken.None).WaitAsync(Timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>同 <see cref="RunAsync{T}"/>，給參考型別的結果</summary>
    public async Task<T?> RunRefAsync<T>(Func<T> work, CancellationToken ct) where T : class?
    {
        try
        {
            return await Task.Run(work, CancellationToken.None).WaitAsync(Timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }
}
