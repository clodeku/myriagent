using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 電腦狀態面板的下載在這台（peerXferSave）的暫停、繼續與中止（2026-11-10 使用者要求：可以手動暫停、手動中止，在背景執行）：
/// 暫停保留寫到一半的暫存檔，繼續時從那裡接著要（PeerDownloadCursor），中止或 app 結束刪掉暫存檔
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubPeerXferTests
{
    private static readonly string OwnDevice = "d_" + new string('A', 26);

    private static readonly string OtherDevice = "d_" + new string('B', 26);

    private const string MediaRef = "0123456789ABCDEFGHJKMNPQRS";

    private static string NewId() => SyncIds.NewUlid(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private sealed class FakeSink : IPeerCommandSink
    {
        public string? DeviceId => OwnDevice;

        public void SubmitPeerCommand(string commandId, JObject cmd)
        {
        }
    }

    /// <summary>假的下載來源：寫完前半就停住等 <see cref="Release"/>（暫停時在這裡被取消），之後寫後半；照游標的位置接著寫</summary>
    private sealed class GatedFiles(byte[] data) : IPeerFileSource
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<long> StartOffsets { get; } = [];

        public Task<PeerFile?> FetchFileAsync(string key, CancellationToken ct) => Task.FromResult<PeerFile?>(null);

        public async Task<PeerDownload?> DownloadAsync(string key, Stream target, Action<long, long>? progress, CancellationToken ct, PeerDownloadCursor? cursor = null)
        {
            var offset = cursor?.Offset ?? 0;
            lock (StartOffsets)
                StartOffsets.Add(offset);
            var half = data.Length / 2;
            try
            {
                if (offset < half)
                {
                    await target.WriteAsync(data.AsMemory((int)offset, half - (int)offset), ct);
                    offset = half;
                    progress?.Invoke(offset, data.Length);
                    if (cursor is not null)
                    {
                        cursor.Offset = offset;
                        cursor.Total = data.Length;
                    }
                    await Release.Task.WaitAsync(ct);
                }

                await target.WriteAsync(data.AsMemory((int)offset), ct);
                offset = data.Length;
                progress?.Invoke(offset, data.Length);
                return new PeerDownload("大檔.bin", data.Length);
            }
            finally
            {
                if (cursor is not null)
                {
                    cursor.Offset = offset;
                    cursor.Total = data.Length;
                }
            }
        }
    }

    private static byte[] Data()
    {
        var bytes = new byte[64 * 1024];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(i * 13 + 5);
        return bytes;
    }

    /// <summary>連上同步、這個視窗收過 /f/ 的網址（deviceFetch 的 fileRef），回網址</summary>
    private static string Prepare(HubHarness h, IPeerFileSource files, string downloads)
    {
        h.Hub.PeerDownloadsForTest = downloads;
        h.Hub.PeerFileSourceForTest = files;
        h.Ui.Invoke(() =>
        {
            h.Hub.AttachPeerSink(new FakeSink());
            h.Hub.PeerLive = true;
        });
        var url = "/f/" + OtherDevice + "-" + MediaRef;
        var cid = NewId();
        h.Command(h.Main, "deviceFetch", new { deviceId = OtherDevice, requestId = "dx1", path = @"D:\大檔.bin", commandId = cid });
        h.Ui.Invoke(() => h.Hub.OnPeerReply(cid, new JObject { ["type"] = "fileRef", ["requestId"] = "dx1", ["conversationId"] = null, ["url"] = url, ["name"] = "大檔.bin", ["size"] = 65536 }));
        return url;
    }

    private static Task<JObject> StateAsync(HubHarness h, string transferId, string state, int mark)
        => h.Main.WaitForAsync("xferProgress", m => m.Value<string>("transferId") == transferId && m.Value<string>("state") == state, mark, timeoutMs: 10000);

    [Fact]
    public async Task 暫停保留暫存檔_繼續從斷點接著下載_內容完整()
    {
        using var h = new HubHarness();
        var data = Data();
        var files = new GatedFiles(data);
        var downloads = h.Temp.Combine("Downloads");
        var url = Prepare(h, files, downloads);
        var id = NewId();
        var mark = h.Main.Mark();

        h.Command(h.Main, "peerXferSave", new { transferId = id, url, name = "大檔.bin", folder = (string?)null });
        await h.WaitUntilAsync(() => files.StartOffsets.Count == 1 && Directory.Exists(downloads) && Directory.GetFiles(downloads).Length == 1, "開始下載");

        h.Command(h.Main, "peerXferPause", new { transferId = id });
        var paused = await StateAsync(h, id, "paused", mark);
        paused.Value<long>("done").Should().Be(data.Length / 2, "暫停時回報寫到哪裡");
        var part = Directory.GetFiles(downloads).Single();
        Path.GetFileName(part).Should().EndWith(DeviceTransferRules.PartSuffix, "暫存檔留著");
        h.Ui.Invoke(() => h.Hub.FileTransferActive).Should().BeFalse("暫停中不算在傳（不擋 app 換版）");

        files.Release.SetResult();
        var resumeMark = h.Main.Mark();
        h.Command(h.Main, "peerXferResume", new { transferId = id });
        var done = await StateAsync(h, id, "done", resumeMark);

        files.StartOffsets.Should().Equal(0, data.Length / 2);
        File.ReadAllBytes(done.Value<string>("path")!).Should().Equal(data);
        Directory.GetFiles(downloads).Should().ContainSingle("暫存檔改名成正式的檔");
    }

    [Fact]
    public async Task 暫停中中止_刪掉暫存檔_回cancelled_之後不能繼續()
    {
        using var h = new HubHarness();
        var files = new GatedFiles(Data());
        var downloads = h.Temp.Combine("Downloads");
        var url = Prepare(h, files, downloads);
        var id = NewId();
        var mark = h.Main.Mark();
        h.Command(h.Main, "peerXferSave", new { transferId = id, url, name = "大檔.bin", folder = (string?)null });
        await h.WaitUntilAsync(() => files.StartOffsets.Count == 1, "開始下載");
        h.Command(h.Main, "peerXferPause", new { transferId = id });
        await StateAsync(h, id, "paused", mark);

        h.Command(h.Main, "peerXferCancel", new { transferId = id });
        await StateAsync(h, id, "cancelled", mark);
        Directory.GetFiles(downloads).Should().BeEmpty("暫存檔刪掉");

        var resumeMark = h.Main.Mark();
        h.Command(h.Main, "peerXferResume", new { transferId = id });
        (await StateAsync(h, id, "failed", resumeMark)).Value<string>("error").Should().Contain("找不到");
    }

    [Fact]
    public async Task 下載中中止_刪掉暫存檔_回cancelled()
    {
        using var h = new HubHarness();
        var files = new GatedFiles(Data());
        var downloads = h.Temp.Combine("Downloads");
        var url = Prepare(h, files, downloads);
        var id = NewId();
        var mark = h.Main.Mark();
        h.Command(h.Main, "peerXferSave", new { transferId = id, url, name = "大檔.bin", folder = (string?)null });
        await h.WaitUntilAsync(() => files.StartOffsets.Count == 1, "開始下載");
        h.Ui.Invoke(() => h.Hub.FileTransferActive).Should().BeTrue();

        h.Command(h.Main, "peerXferCancel", new { transferId = id });
        await StateAsync(h, id, "cancelled", mark);
        await h.WaitUntilAsync(() => Directory.GetFiles(downloads).Length == 0, "暫存檔刪掉");
    }

    [Fact]
    public async Task app結束_暫停的暫存檔刪掉()
    {
        var h = new HubHarness();
        string downloads;
        try
        {
            var files = new GatedFiles(Data());
            downloads = h.Temp.Combine("Downloads");
            var url = Prepare(h, files, downloads);
            var id = NewId();
            var mark = h.Main.Mark();
            h.Command(h.Main, "peerXferSave", new { transferId = id, url, name = "大檔.bin", folder = (string?)null });
            await h.WaitUntilAsync(() => files.StartOffsets.Count == 1, "開始下載");
            h.Command(h.Main, "peerXferPause", new { transferId = id });
            await StateAsync(h, id, "paused", mark);
            Directory.GetFiles(downloads).Should().ContainSingle();

            h.Ui.Invoke(() => h.Hub.Dispose());
            Directory.GetFiles(downloads).Should().BeEmpty();
        }
        finally
        {
            h.Dispose();
        }
    }
}
