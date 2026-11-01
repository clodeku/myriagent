using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Preview;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 文件預覽（2026-11-01 使用者要求，docs/web-protocol.md 的 docPreview）：網頁要預覽 Word、Excel、PowerPoint 時送 docPreview，
// 中樞確認是畫面上出現過的本機檔（同「另存新檔」的白名單）後，在背景以 IDocumentPdfConverter 轉成 PDF（快取在 cache\doc-preview，
// 依路徑＋大小＋修改時間命名），登記成可以經 /media 預覽的 PDF，回 docPreview { requestId, url }（失敗帶 error）。
// 同一時間只轉一個（Office 的 COM 自動化不適合同時開好幾個）。中樞預設沒有轉檔器（回「沒有 Office」的說明），只有 AppController 以 AttachDocPreview 接上真的
internal sealed partial class AgentHub
{
    private IDocumentPdfConverter? docConverter;
    private string? docPreviewDirectory;
    private readonly SemaphoreSlim docPreviewGate = new(1, 1);

    /// <summary>接上轉檔器與快取資料夾（AppController；測試給假的）</summary>
    internal void AttachDocPreview(IDocumentPdfConverter converter, string cacheDirectory)
    {
        docConverter = converter ?? throw new ArgumentNullException(nameof(converter));
        docPreviewDirectory = cacheDirectory ?? throw new ArgumentNullException(nameof(cacheDirectory));
    }

    private async Task DocPreviewAsync(IAgentView view, string? requestId, string? path)
    {
        if (string.IsNullOrEmpty(requestId) || requestId.Length > 100)
            return;
        void Reply(string? url, string? error)
        {
            if (!disposed)
                Post(view, new JObject { ["type"] = "docPreview", ["requestId"] = requestId, ["url"] = url, ["error"] = error });
        }

        if (string.IsNullOrWhiteSpace(path) || DocPreviewRules.KindOf(path) is not { } kind)
        {
            Reply(null, DocPreviewRules.NotSupportedText);
            return;
        }
        if (!AgentAttachmentRules.IsLocalDrivePath(path) || !mediaUrls.IsSaveAllowed(path))
        {
            Reply(null, DocPreviewRules.NotShownText);
            return;
        }
        if (docConverter is not { } converter || docPreviewDirectory is not { } directory)
        {
            Reply(null, DocPreviewRules.NoConverterText);
            return;
        }
        var probed = await ProbeLocalPathAsync(path);
        if (disposed)
            return;
        if (probed != LocalPathKind.File)
        {
            Reply(null, probed == null ? $"無法存取（逾時）：{path}" : $"找不到檔案：{path}");
            return;
        }

        string? error;
        string? output = null;
        await docPreviewGate.WaitAsync();
        try
        {
            (output, error) = await Task.Run(async () =>
            {
                try
                {
                    var info = new FileInfo(path);
                    Directory.CreateDirectory(directory);
                    var target = Path.Combine(directory, DocPreviewRules.CacheFileName(info.FullName, info.Length, info.LastWriteTimeUtc));
                    if (File.Exists(target))
                    {
                        File.SetLastWriteTimeUtc(target, DateTime.UtcNow); // 記下最後使用時間（快取 7 天沒用到才刪）
                        return (target, (string?)null);
                    }
                    var failed = await converter.ConvertAsync(info.FullName, kind, target, CancellationToken.None).ConfigureAwait(false);
                    PruneDocPreviewCache(directory, target);
                    return (failed == null ? target : null, failed);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    Log.Warn($"文件預覽失敗：{ex.Message}");
                    return ((string?)null, (string?)DocPreviewRules.FailedText);
                }
            });
        }
        finally
        {
            docPreviewGate.Release();
        }
        if (disposed)
            return;
        if (output == null)
        {
            Reply(null, error ?? DocPreviewRules.FailedText);
            return;
        }
        mediaUrls.AllowDocument(output);
        Reply(AgentMarkdownRenderer.MediaUrl(output), null);
    }

    /// <summary>刪掉 7 天沒用到的預覽（背景執行緒；刪不掉就留著）</summary>
    private static void PruneDocPreviewCache(string directory, string keep)
    {
        try
        {
            var now = DateTime.UtcNow;
            foreach (var file in Directory.EnumerateFiles(directory, "*.pdf"))
            {
                if (string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    if (DocPreviewRules.Expired(File.GetLastWriteTimeUtc(file), now))
                        File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
