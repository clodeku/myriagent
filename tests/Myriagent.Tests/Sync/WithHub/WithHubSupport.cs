using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Myriagent.Core;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;
using Xunit.Sdk;

namespace Myriagent.Tests.Sync.WithHub;

/// <summary>SEC-09：公鑰報的是名冊裡那一把（過得了引擎的公鑰比對），簽章卻用另一把私鑰——只偷到權杖、沒有私鑰的人</summary>
internal sealed class ForgedSigner(string claimedSpki) : IDeviceSigner
{
    private readonly ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public string PublicSpki => claimedSpki;

    public string KeyStorage => PairingRules.KeyStorageSoftware;

    public string Sign(ReadOnlySpan<byte> data) => P256KeyRules.Sign(other, data);

    public void Dispose() => other.Dispose();
}

/// <summary>直接用傳輸層時記下伺服器推來的訊息</summary>
internal sealed partial class RecordingSessionEvents : ISyncSessionEvents
{
    private readonly TaskCompletionSource<Exception?> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConcurrentQueue<object> Received { get; } = new();

    public Task<Exception?> Closed => closed.Task;

    public void OnBatchAck(BatchAck ack) => Received.Enqueue(ack);

    public void OnNotify(Notify notify) => Received.Enqueue(notify);

    public void OnUsageAssign(UsageAssign assign) => Received.Enqueue(assign);

    public void OnTrustChanged(TrustChanged changed) => Received.Enqueue(changed);

    public void OnServerNotice(ServerNotice notice) => Received.Enqueue(notice);

    public void OnWatchChanged(WatchChanged changed) => Received.Enqueue(changed);

    public void OnSnapshotRequest(SnapshotRequest request) => Received.Enqueue(request);

    public void OnMediaRequest(MediaRequest request) => Received.Enqueue(request);

    public void OnCommandDelivery(CommandDelivery delivery) => Received.Enqueue(delivery);

    public void OnPeerReply(PeerReply reply) => Received.Enqueue(reply);

    public void OnStatsUpdate(StatsUpdate update) => Received.Enqueue(update);

    public void OnTokensUpdate(TokensUpdate update) => Received.Enqueue(update);

    public void OnLiveResync(LiveResync resync) => Received.Enqueue(resync);

    public void OnHandoffRequest(HandoffRequest request) => Received.Enqueue(request);

    public void OnHandoffUpdate(HandoffUpdate update) => Received.Enqueue(update);

    public void OnLogRepair(LogRepair repair) => Received.Enqueue(repair);

    public void OnExecReply(ExecChunk chunk) => Received.Enqueue(chunk);

    public void OnExecAbort(ExecAbort abort) => Received.Enqueue(abort);

    public void OnClosed(Exception? error) => closed.TrySetResult(error);
}

/// <summary>
/// 使用者的 CNG 金鑰存放區（%APPDATA%\Microsoft\Crypto\Keys）。這台電腦上別的測試與 MyriagentHub 也會在這裡建刪檔案，
/// 所以不比總數，只認內容（UTF-16）含特定字串的檔案：金鑰名稱，或 CNG 記下的 CreatorProcessName（本程序複製出來的 MyriagentHub.exe 的路徑）
/// </summary>
internal static class UserKeyStore
{
    public static string Directory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Crypto", "Keys");

    public static IReadOnlyList<string> Snapshot() =>
        System.IO.Directory.Exists(Directory) ? System.IO.Directory.GetFiles(Directory).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToList() : [];

    /// <summary>本程序複製出來的 MyriagentHub.exe 建的金鑰檔（強制結束留下的孤兒；正常停止後應該沒有）</summary>
    public static IReadOnlyList<string> FilesCreatedByTestHub() => FilesNaming(MyriagentHubExe.CopyDirectoryName + Path.DirectorySeparatorChar + "MyriagentHub.exe");

    /// <summary>內容含 <paramref name="text"/>（UTF-16LE）的金鑰檔</summary>
    public static IReadOnlyList<string> FilesNaming(string text)
    {
        var needle = Encoding.Unicode.GetBytes(text);
        var found = new List<string>();
        foreach (var file in System.IO.Directory.Exists(Directory) ? System.IO.Directory.GetFiles(Directory) : [])
        {
            byte[] content;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                content = new byte[stream.Length];
                stream.ReadExactly(content);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (content.AsSpan().IndexOf(needle) >= 0)
                found.Add(Path.GetFileName(file));
        }

        return found;
    }
}

/// <summary>
/// 測試看過的權杖明文與 device.json（給「記錄與 device.json 不含權杖明文」掃描）：<see cref="DeviceDir"/> 刪除前先記下 device.json 的內容與解開的權杖
/// </summary>
internal static class SyncSecrets
{
    private static readonly ConcurrentDictionary<string, byte> Tokens = new(StringComparer.Ordinal);

    private static readonly ConcurrentQueue<(string Path, string Text)> DeviceFiles = new();

    public static IReadOnlyCollection<string> Known => Tokens.Keys.ToList();

    public static IReadOnlyList<(string Path, string Text)> RetiredDeviceFiles => DeviceFiles.ToList();

    public static void Remember(string? token)
    {
        if (!string.IsNullOrEmpty(token))
            Tokens[token] = 0;
    }

    /// <summary>記下 device.json 的原文與裡面的權杖（檔案不在就略過）</summary>
    public static void RememberDeviceFile(string path)
    {
        if (!File.Exists(path))
            return;
        DeviceFiles.Enqueue((path, File.ReadAllText(path)));
        Remember(DeviceSecrets.Unprotect(DeviceFile.Load(path).Data?.ProtectedDeviceToken));
    }
}

/// <summary>積壓補送的情境（S5、S8）：不合併的 cli.state（和 MyriagentHub.Tests 的 SoakTests 同一種事件），以伺服器的 sync.db 驗收</summary>
internal static class Backlog
{
    public static string CliStateJson(long n) => SyncJson.Serialize(new CliStatePayload(new Dictionary<string, DeviceCliView>
    {
        ["Claude"] = new(true, "2.1.0", "native", "2.2.0", false, 0, 0, null, n),
    }));

    public static void Append(SyncEngine engine, long from, int count, string? coalesceKey = null)
    {
        for (var i = 0; i < count; i++)
            engine.Post(new SyncSignal.Append(new OutboxAppend(SyncStreams.Catalog, SyncEventKinds.CliState, CliStateJson(from + i), coalesceKey)));
    }

    /// <summary>筆數不固定時（有合併的事件）：等本機 outbox 全部 Acked、伺服器的筆數等於 outbox；回傳花的時間與筆數</summary>
    public static async Task<(TimeSpan Elapsed, int Count)> WaitDrainedAsync(HubDb db, HubEngine engine, string deviceId, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var outbox = await engine.Inspect(v => v.Outbox);
            var stored = db.DeviceEvents(deviceId).Count;
            if (outbox.Count > 0 && outbox.All(e => e.State == OutboxState.Acked) && stored == outbox.Count)
                return (watch.Elapsed, outbox.Count);
            if (watch.Elapsed > timeout)
            {
                var states = string.Join("、", outbox.GroupBy(e => e.State).Select(g => $"{g.Key} {g.Count()}"));
                throw new XunitException($"{timeout.TotalSeconds:0} 秒內沒有補完：伺服器 {stored} 筆，outbox {outbox.Count} 列（{states}），狀態 {await engine.StateAsync()}");
            }

            await Task.Delay(200);
        }
    }

    /// <summary>等伺服器收齊 <paramref name="count"/> 筆、本機 outbox 全部 Acked；回傳花的時間</summary>
    public static async Task<TimeSpan> WaitDrainedAsync(HubDb db, HubEngine engine, string deviceId, int count, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var stored = db.DeviceEvents(deviceId).Count;
            var outbox = await engine.Inspect(v => v.Outbox);
            if (stored >= count && outbox.Count >= count && outbox.All(e => e.State == OutboxState.Acked))
                return watch.Elapsed;
            if (watch.Elapsed > timeout)
            {
                var states = string.Join("、", outbox.GroupBy(e => e.State).Select(g => $"{g.Key} {g.Count()}"));
                throw new XunitException($"{timeout.TotalSeconds:0} 秒內沒有補完：伺服器 {stored}／{count} 筆，outbox {states}，狀態 {await engine.StateAsync()}");
            }

            await Task.Delay(200);
        }
    }

    /// <summary>伺服器上這台的事件剛好是 deviceSeq 1..count 各一筆、payload 雜湊等於本機 outbox、沒有被擋下的</summary>
    public static async Task AssertExactlyOnceAsync(HubDb db, HubEngine engine, string deviceId, int count)
    {
        var events = db.DeviceEvents(deviceId);
        events.Select(e => e.DeviceSeq).Should().Equal(Enumerable.Range(1, count).Select(i => (long?)i), "deviceSeq 1..N 各一筆（0 遺失、0 重複）");
        db.Rejected(deviceId).Should().Be(0);
        var local = (await engine.Inspect(v => v.Outbox)).ToDictionary(e => e.DeviceSeq, e => e.PayloadSha256);
        foreach (var e in events)
        {
            Convert.ToHexStringLower(e.PayloadSha256 ?? []).Should().Be(local[e.DeviceSeq!.Value].ToLowerInvariant(), $"deviceSeq {e.DeviceSeq} 的內容和本機 outbox 相同");
        }
    }
}

/// <summary>測試程序的記錄檔（AppPaths.LogDirectory，即 TestEnvironment.DataRoot 的 logs）：記下各檔目前的長度，之後只讀新寫的部分</summary>
internal static class LogScan
{
    public static string[] Files() => Directory.Exists(AppPaths.LogDirectory) ? Directory.GetFiles(AppPaths.LogDirectory, "*.log") : [];

    public static IReadOnlyDictionary<string, long> Mark()
    {
        Log.Flush();
        return Files().ToDictionary(f => f, f => new FileInfo(f).Length, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>標記之後寫進記錄的文字（新檔從頭讀）</summary>
    public static string Since(IReadOnlyDictionary<string, long> mark)
    {
        Log.Flush();
        return string.Concat(Files().Select(f => ReadShared(f, mark.GetValueOrDefault(f))));
    }

    public static string ReadShared(string path, long offset)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (offset > 0 && offset <= stream.Length)
            stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
