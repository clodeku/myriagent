using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Files;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

internal sealed partial class AgentHub
{
    private const int BrowserThumbSize = 240;

    // ───────────────────────────── 附件 ─────────────────────────────

    private void AddAttachments(ConversationState c, IReadOnlyList<string> paths)
    {
        if (paths == null || paths.Count == 0)
            return;

        // 先過濾掉非本機磁碟路徑再碰檔案系統：對 UNC 路徑做 Exists 就會對外發 SMB 連線、送出登入雜湊
        var candidates = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        var files = candidates.Where(AgentAttachmentRules.IsLocalDrivePath).ToList();
        if (files.Count < candidates.Count)
            Toast("網路共用資料夾（\\\\主機\\…）上的檔案不能直接附加，請先複製到這台電腦", isError: true);
        if (files.Any(Directory.Exists))
            Toast("資料夾不能直接附加，請把資料夾設為工作資料夾，或選資料夾裡的檔案");
        files = files.Where(File.Exists).ToList();

        int room = AgentAttachmentRules.MaxAttachmentsPerMessage - c.PendingAttachments.Count - c.ImportingCount;
        if (files.Count > room)
        {
            Toast($"每則訊息最多附 {AgentAttachmentRules.MaxAttachmentsPerMessage} 個檔案");
            files = files.Take(Math.Max(0, room)).ToList();
        }

        foreach (var file in files)
        {
            _ = ImportAsync(c, () => AgentAttachmentProcessor.ImportFileAsync(file, store.GetAttachmentDirectory(c.Meta.Id), store.GetModelImageDirectory(c.Meta.Id), CancellationToken.None),
                Path.GetFileName(file));
        }
    }

    private async Task PasteImageAsync(ConversationState? c, string? dataUrl, string? name)
    {
        if (c == null || string.IsNullOrWhiteSpace(dataUrl))
            return;
        int comma = dataUrl.IndexOf(',');
        if (comma < 0 || !dataUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            return;
        var header = dataUrl[..comma];
        var extension = header.Contains("image/jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg"
            : header.Contains("image/gif", StringComparison.OrdinalIgnoreCase) ? ".gif"
            : header.Contains("image/webp", StringComparison.OrdinalIgnoreCase) ? ".webp"
            : ".png";
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
        }
        catch (FormatException)
        {
            Toast("貼上的圖片格式無法讀取", isError: true);
            return;
        }

        var fileName = string.IsNullOrWhiteSpace(name) || name == "image.png"
            ? $"貼上的圖片_{DateTime.Now:yyyyMMdd_HHmmss}{extension}"
            : name;
        await ImportAsync(c, () => AgentAttachmentProcessor.ImportImageBytesAsync(bytes, fileName, store.GetAttachmentDirectory(c.Meta.Id), store.GetModelImageDirectory(c.Meta.Id), CancellationToken.None), fileName);
    }

    private async Task ImportAsync(ConversationState c, Func<Task<AgentAttachment>> import, string displayName)
    {
        c.ImportingCount++;
        PostComposer(c, $"正在處理「{displayName}」…");
        try
        {
            var attachment = await Task.Run(import);
            if (conversations.TryGetValue(c.Meta.Id, out var live) && ReferenceEquals(live, c))
                c.PendingAttachments.Add(attachment);
            else
                DeleteFolderInBackground(c.Meta.Id, quiet: true);   // 匯入期間對話被刪或草稿被丟棄：不留下孤兒資料夾
        }
        catch (Exception ex)
        {
            Toast($"附加「{displayName}」失敗：{ex.Message}", isError: true);
        }
        finally
        {
            c.ImportingCount--;
            if (!disposed)
                PostComposer(c);
        }
    }

    private void RemoveAttachment(ConversationState? c, string? attachmentId)
    {
        var attachment = c?.PendingAttachments.FirstOrDefault(a => a.Id == attachmentId);
        if (c == null || attachment == null)
            return;
        c.PendingAttachments.Remove(attachment);
        foreach (var path in attachment.ModelImagePaths.Where(p => !string.Equals(p, attachment.StoredPath, StringComparison.OrdinalIgnoreCase)).Append(attachment.StoredPath))
        {
            RevokeMedia(path);
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        PostComposer(c);
    }

    // ───────────────────────────── 檔案瀏覽與 @ 提示 ─────────────────────────────

    private async Task ListFilesAsync(IAgentView view, ConversationState? c, string? requestId, string? path)
    {
        if (c == null)
            return;
        var root = GetWorkingDirectory(c);
        var target = string.IsNullOrWhiteSpace(path) ? root : path;
        // 只建立對話自己的預設 workspace：使用者選的資料夾被刪掉時不偷偷重建（回「找不到資料夾」，送出時的存在檢查也才擋得下）
        bool createRoot = string.IsNullOrWhiteSpace(c.Meta.WorkingDirectory);

        List<FileEntry> entries;
        bool truncated;
        string? error = null;
        try
        {
            // 建立與列出都在背景執行緒（離線的網路磁碟會卡到 SMB 逾時）；失敗一律回帶 error 的 files，網頁才不會一直停在讀取中
            (entries, truncated) = await Task.Run(() =>
            {
                if (createRoot)
                    Directory.CreateDirectory(root);
                return FileBrowser.List(target);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            entries = new List<FileEntry>();
            truncated = false;
            error = ex.Message;
        }
        if (disposed)
            return;

        var list = new JArray();
        foreach (var e in entries)
        {
            bool media = e.Kind is "image" or "video";
            list.Add(new JObject
            {
                ["name"] = e.Name,
                ["path"] = e.Path,
                ["kind"] = e.Kind,
                ["size"] = e.Size,
                ["modified"] = ToUnixMs(e.Modified),
                // 工作資料夾的檔案可能被代理覆寫：縮圖網址帶修改時間與大小，檔案一變網址就變（/thumb 回應可快取 5 分鐘）
                ["thumbUrl"] = media ? AllowThumb(e.Path, BrowserThumbSize, $"{e.Modified.Ticks}-{e.Size}") : null,
                ["url"] = AgentAttachmentRules.IsDisplayableMedia(e.Path) ? AllowMedia(e.Path) : null,
                ["hidden"] = e.Hidden,
            });
        }

        Post(view, new JObject
        {
            ["type"] = "files",
            ["requestId"] = requestId,
            ["conversationId"] = c.Meta.Id,
            ["path"] = target,
            ["parent"] = FileBrowser.GetParent(target),
            ["root"] = root,
            ["entries"] = list,
            ["truncated"] = truncated,
            ["error"] = error,
        });
    }

    private async Task SearchFilesAsync(IAgentView view, ConversationState? c, string? requestId, string query)
    {
        if (c == null)
            return;
        var root = GetWorkingDirectory(c);
        List<FileSearchResult> results;
        try
        {
            // SearchAsync 在第一個 await 之前就檢查資料夾存在：整段放到背景執行緒，離線的網路磁碟不會每次查詢都卡住畫面
            results = await Task.Run(() => FileBrowser.SearchAsync(root, query, CancellationToken.None));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            results = new List<FileSearchResult>();
        }
        if (disposed)
            return;
        Post(view, new JObject
        {
            ["type"] = "fileSearch",
            ["requestId"] = requestId,
            ["conversationId"] = c.Meta.Id,
            ["query"] = query,
            ["results"] = new JArray(results.Select(r => new JObject
            {
                ["relativePath"] = r.RelativePath,
                ["fullPath"] = r.FullPath,
                ["kind"] = r.Kind,
            })),
        });
    }

    // ───────────────────────────── 開檔與連結 ─────────────────────────────

    private void OpenLink(string? href)
    {
        if (string.IsNullOrWhiteSpace(href) || href == "#")
            return;

        var openPrefix = AgentMarkdownRenderer.Origin + "/open?path=";
        var mediaPrefix = AgentMarkdownRenderer.Origin + "/media?path=";
        if (href.StartsWith(openPrefix, StringComparison.OrdinalIgnoreCase))
        {
            OpenLocalPath(Uri.UnescapeDataString(href[openPrefix.Length..]));
            return;
        }
        if (href.StartsWith(mediaPrefix, StringComparison.OrdinalIgnoreCase))
        {
            OpenLocalPath(Uri.UnescapeDataString(href[mediaPrefix.Length..]));
            return;
        }

        if (Uri.TryCreate(href, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeMailto))
            host.OpenWithShell(uri.AbsoluteUri);
    }

    private enum LocalPathKind
    {
        Missing,
        File,
        Directory,
    }

    /// <summary>背景確認路徑是檔案、資料夾或不存在；逾時（離線的網路磁碟會卡到 SMB 逾時）回 null</summary>
    private static async Task<LocalPathKind?> ProbeLocalPathAsync(string path)
    {
        try
        {
            return await Task.Run(() => Directory.Exists(path) ? LocalPathKind.Directory : File.Exists(path) ? LocalPathKind.File : LocalPathKind.Missing)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private void OpenLocalPath(string? path) => _ = OpenLocalPathAsync(path);

    /// <summary>
    /// 開本機檔案：只認磁碟路徑（UNC 會對外連線，不開）；資料夾開檔案總管；
    /// 白名單內的文件與媒體用預設程式開，其他（執行檔、腳本、捷徑、網頁…）只在檔案總管中選取，不直接執行。
    /// 存在與否在背景確認（回覆裡的路徑可能在離線的網路磁碟上），確認後回到 UI 執行緒交給宿主
    /// </summary>
    private async Task OpenLocalPathAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        if (!AgentAttachmentRules.IsLocalDrivePath(path))
        {
            Toast("只能開啟這台電腦磁碟上的檔案", isError: true);
            return;
        }
        var kind = await ProbeLocalPathAsync(path);
        if (disposed)
            return;
        switch (kind)
        {
            case null:
                Toast($"無法存取（逾時）：{path}", isError: true);
                return;
            case LocalPathKind.Directory:
                host.RevealInExplorer(path);
                return;
            case LocalPathKind.Missing:
                Toast($"找不到檔案：{path}", isError: true);
                return;
        }
        if (!AgentAttachmentRules.IsSafeToOpen(path))
        {
            host.RevealInExplorer(path);
            Toast("這類檔案不直接開啟，已在檔案總管中選取，請自行確認後再開");
            return;
        }
        host.OpenWithShell(path);
    }

    /// <summary>
    /// 「另存新檔」（回覆中的檔案連結、燈箱與影片播放器的下載）：只複製畫面上出現過的本機檔案（<see cref="LocalMediaUrls.IsSaveAllowed"/>），
    /// 原檔不動。存在與否在背景確認 → 宿主的另存對話框（取消就什麼都不做）→ 背景先寫到目的資料夾的暫存檔再取代，
    /// 寫到一半失敗不會毀掉已有的目的檔。提示只給發起的視窗
    /// </summary>
    private async Task SaveFileAsync(IAgentView view, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        if (!AgentAttachmentRules.IsLocalDrivePath(path) || !mediaUrls.IsSaveAllowed(path))
        {
            Toast("只能另存回覆或預覽中出現的本機檔案", isError: true, view: view);
            return;
        }
        var kind = await ProbeLocalPathAsync(path);
        if (disposed)
            return;
        switch (kind)
        {
            case null:
                Toast($"無法存取（逾時）：{path}", isError: true, view: view);
                return;
            case LocalPathKind.Missing:
                Toast($"找不到檔案：{path}", isError: true, view: view);
                return;
            case LocalPathKind.Directory:
                Toast($"這是資料夾，不能另存：{path}", isError: true, view: view);
                return;
        }

        var name = Path.GetFileName(path);
        var destination = host.PickSaveFile(view, name);
        if (string.IsNullOrWhiteSpace(destination) || disposed)
            return;
        if (string.Equals(NormalizePath(destination), NormalizePath(path), StringComparison.OrdinalIgnoreCase))
        {
            Toast("選的位置就是原始檔案，不需要另存", view: view);
            return;
        }

        try
        {
            await Task.Run(() => CopyReplacing(path, destination));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log.Warn($"另存檔案失敗：{ex.Message}");
            if (!disposed)
                Toast($"無法另存：{ex.Message}", isError: true, view: view);
            return;
        }
        if (!disposed)
            Toast($"已另存：{Path.GetFileName(destination)}", view: view);
    }

    /// <summary>複製到目的資料夾的暫存檔再取代目的檔；失敗時刪掉暫存檔，原本的目的檔保留</summary>
    private static void CopyReplacing(string source, string destination)
    {
        var temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(source, temp);
            File.Move(temp, destination, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 刪不掉就留著，名稱看得出是暫存檔
            }
            throw;
        }
    }

    private void RevealPath(string? path) => _ = RevealPathAsync(path);

    private async Task RevealPathAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !AgentAttachmentRules.IsLocalDrivePath(path))
            return;
        var kind = await ProbeLocalPathAsync(path);
        if (disposed)
            return;
        if (kind is LocalPathKind.File or LocalPathKind.Directory)
            host.RevealInExplorer(path);
        else
            Toast(kind == null ? $"無法存取（逾時）：{path}" : $"找不到：{path}", isError: true);
    }

    /// <summary>
    /// 用檔案總管開對話的資料夾。create＝附件資料夾或對話自己的預設 workspace，不存在就建立；
    /// 使用者選的資料夾被刪掉時不偷偷重建（重建後送出時的存在檢查就擋不下，代理會在空資料夾裡工作）。
    /// 建立與存在檢查都在背景執行緒（離線的網路磁碟會卡到 SMB 逾時）
    /// </summary>
    private async Task OpenFolderAsync(string folder, bool create)
    {
        bool? exists;
        try
        {
            exists = await Task.Run(() =>
            {
                if (create)
                    Directory.CreateDirectory(folder);
                return Directory.Exists(folder);
            }).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            exists = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Toast($"無法開啟資料夾：{ex.Message}", isError: true);
            return;
        }
        if (disposed)
            return;
        if (exists == true)
            host.RevealInExplorer(folder);
        else
            Toast(exists == null ? $"資料夾無法存取（逾時）：{folder}" : $"找不到資料夾：{folder}，請重新選擇工作資料夾", isError: true);
    }
}
