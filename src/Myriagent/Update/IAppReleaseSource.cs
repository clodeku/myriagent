using Newtonsoft.Json.Linq;

namespace Myriagent.Update;

/// <summary>來源宣告的最新版：manifest 原始位元組（簽章就是對它簽的）與簽章 JSON；<see cref="ZipBytes"/> 是套件的大小（伺服器來源才有，U3）</summary>
internal sealed record ReleaseOffer(string Release, byte[] ManifestBytes, string SignatureJson)
{
    public long? ZipBytes { get; init; }
}

/// <summary>暫時失敗的例外帶的重試時間（伺服器的 Retry-After；U3 的 HubReleaseSource）：更新管理員照它排下一次重試</summary>
internal interface IRetryAfterHint
{
    TimeSpan? RetryAfter { get; }

    /// <summary>沒有連上來源（不是來源出錯）：畫面顯示「等待連上伺服器」，不顯示成下載失敗</summary>
    bool Offline { get; }
}

/// <summary>新版的來源：U1 只有端到端測試的本機資料夾（FileReleaseSource）；U3 加上 MyriagentHub（裝置權杖，Sync/HubReleaseSource.cs）</summary>
internal interface IAppReleaseSource
{
    string Name { get; }

    /// <summary>
    /// 最新版；來源沒有任何發佈時回 null。
    /// manifest 與簽章讀取時不得超過 <see cref="ReleaseManifestRules.MaxManifestBytes"/>／<see cref="ReleaseManifestRules.MaxSignatureBytes"/>
    /// （超過丟 InvalidDataException，不讀進記憶體）
    /// </summary>
    Task<ReleaseOffer?> GetLatestAsync(CancellationToken ct);

    /// <summary>
    /// 下載 zip 到 destination：已經有部分內容就從它的長度接著下載（HTTP 來源用 Range）。
    /// 已下載的部分接不上來源（例如比來源還大）丟 InvalidDataException：呼叫端當成內容錯誤，刪掉從頭下載。
    /// progress 回報目的檔目前的總長度，在下載迴圈裡依序呼叫、可以很頻繁（呼叫端在回報的執行緒上同步處理並節流）
    /// </summary>
    Task DownloadZipAsync(ReleaseOffer offer, string zipName, string destination, IProgress<long>? progress, CancellationToken ct);
}

/// <summary>本機資料夾當來源（tools/update-e2e）：latest.json＝{"release":"…"}，旁邊是 myriagent-&lt;release&gt;.manifest.json／.sig／.zip</summary>
internal sealed class FileReleaseSource : IAppReleaseSource
{
    private const int MaxLatestBytes = 4 * 1024;

    private readonly string directory;

    public FileReleaseSource(string directory) => this.directory = directory;

    public string Name => "本機資料夾 " + directory;

    public async Task<ReleaseOffer?> GetLatestAsync(CancellationToken ct)
    {
        var latest = Path.Combine(directory, "latest.json");
        if (!File.Exists(latest))
            return null;
        EnsureSize(latest, MaxLatestBytes);
        // release 不是字串（物件、陣列、數字）就當成沒有發佈
        var release = (JObject.Parse(await File.ReadAllTextAsync(latest, ct))["release"] as JValue)?.Value as string;
        if (string.IsNullOrWhiteSpace(release) || !ReleaseIdentity.TryParseRelease(release, out _))
            return null;
        var manifestPath = Path.Combine(directory, $"myriagent-{release}.manifest.json");
        EnsureSize(manifestPath, ReleaseManifestRules.MaxManifestBytes);
        var manifest = await File.ReadAllBytesAsync(manifestPath, ct);
        var signaturePath = Path.Combine(directory, $"myriagent-{release}.manifest.sig");
        EnsureSize(signaturePath, ReleaseManifestRules.MaxSignatureBytes);
        var signature = await File.ReadAllTextAsync(signaturePath, ct);
        return new ReleaseOffer(release, manifest, signature);
    }

    public async Task DownloadZipAsync(ReleaseOffer offer, string zipName, string destination, IProgress<long>? progress, CancellationToken ct)
    {
        // zipName 已經過 manifest 規則檢查（myriagent-<release>.zip），不會跳出資料夾
        await using var input = new FileStream(Path.Combine(directory, zipName), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        await using var output = new FileStream(destination, FileMode.Append, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        long offset = output.Length;
        if (offset > input.Length)
            throw new InvalidDataException($"部分下載的檔案比來源還大（{offset} 位元組，來源 {input.Length} 位元組）");
        input.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[1 << 16];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            offset += read;
            progress?.Report(offset);
        }
        await output.FlushAsync(ct);
    }

    /// <summary>讀進記憶體之前先比大小，超過丟 InvalidDataException；檔案不存在時 FileInfo.Length 丟 FileNotFoundException（呼叫端照 IOException 處理）</summary>
    private static void EnsureSize(string path, long max)
    {
        var length = new FileInfo(path).Length;
        if (length > max)
            throw new InvalidDataException($"{Path.GetFileName(path)} 太大：{length} 位元組（上限 {max}）");
    }
}
