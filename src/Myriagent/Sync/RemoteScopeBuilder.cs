using Myriagent.Hub;
using Myriagent.Remote;

namespace Myriagent.Sync;

/// <summary>
/// 組守門員的範圍（<see cref="RemoteFileScope"/>）的共用部分（WP4.7；媒體轉送的 <see cref="LocalMediaResolver"/> 與中樞的遠端檔案指令共用）：
/// 根目錄一律先經 <c>normalize</c>（正式是 <see cref="RemoteFileGuard.NormalizeRootAsync"/>：只收固定或卸除式磁碟、8.3 短名展開）。
/// 禁止清單的資料夾正規化不了的原樣保留（<see cref="RemoteFolderRules.TryGetForbiddenRoots"/> 會 fail-closed）；範圍的根正規化不了的不算。
/// 會碰檔案系統（GetDriveType、GetLongPathName）：只在背景呼叫
/// </summary>
internal static class RemoteScopeBuilder
{
    /// <summary>
    /// 禁止清單的資料夾與這個對話的允許子樹（預設的工作資料夾與 attachments\）；繼承的 CODEX_HOME 和帳戶設定資料夾一樣進禁止清單
    /// </summary>
    public static async Task<RemoteFolderContext> FoldersAsync(MediaFolderRoots folders, string conversationDirectory, string defaultWorkingDirectory,
        string attachmentDirectory, Func<string, CancellationToken, Task<string?>> normalize, CancellationToken ct)
    {
        var device = await DeviceFoldersAsync(folders, normalize, ct).ConfigureAwait(false);
        async Task<string> KeepAsync(string path) => await normalize(path, ct).ConfigureAwait(false) ?? path;
        var workspace = await KeepAsync(defaultWorkingDirectory).ConfigureAwait(false);
        var attachments = await KeepAsync(attachmentDirectory).ConfigureAwait(false);
        return device with { AllowedDataSubtrees = [workspace, attachments], ConversationDirectory = await KeepAsync(conversationDirectory).ConfigureAwait(false) };
    }

    /// <summary>
    /// 沒有對話時（新對話的資料夾挑選，<c>listFolders</c>）的禁止清單：同 <see cref="FoldersAsync"/>，但沒有對話的允許子樹（app 的資料夾整個不給）
    /// </summary>
    public static async Task<RemoteFolderContext> DeviceFoldersAsync(MediaFolderRoots folders, Func<string, CancellationToken, Task<string?>> normalize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(normalize);
        async Task<string> KeepAsync(string path) => await normalize(path, ct).ConfigureAwait(false) ?? path;

        var accounts = new List<string>(folders.AccountConfigDirectories.Count + 1);
        foreach (var directory in folders.AccountConfigDirectories)
            accounts.Add(await KeepAsync(directory).ConfigureAwait(false));
        if (!string.IsNullOrWhiteSpace(folders.CodexHome))
            accounts.Add(await KeepAsync(folders.CodexHome).ConfigureAwait(false));
        return new RemoteFolderContext(
            await KeepAsync(folders.UserProfile).ConfigureAwait(false),
            await KeepAsync(folders.LocalAppData).ConfigureAwait(false),
            folders.RoamingAppData is null ? null : await KeepAsync(folders.RoamingAppData).ConfigureAwait(false),
            await KeepAsync(folders.AppDataRoot).ConfigureAwait(false),
            accounts);
    }

    /// <summary>範圍的根（工作資料夾、attachments\、dirsOnly 的最近資料夾）：正規化得了的才留，去掉重複（不分大小寫），順序不變</summary>
    public static async Task<List<string>> RootsAsync(IEnumerable<string?> roots, Func<string, CancellationToken, Task<string?>> normalize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(normalize);
        var result = new List<string>();
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            if (await normalize(root, ct).ConfigureAwait(false) is { } normalized && !result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                result.Add(normalized);
        }

        return result;
    }
}
