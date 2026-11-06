using System.Text;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 桌面遠端操作的檔案下載（sync-protocol 14.7、web-protocol 的 peerFileSave）：瀏覽器版的 fileRef（/f/{鍵}）要擁有者的 cookie，
// 桌面改送 peerFileSave {conversationId, url, name}：這裡確認是可以操作的副本、網址是伺服器轉來登記過的 /f/ 鍵，
// 在背景以裝置的存取權杖取檔（IPeerFileSource，E11），寫進使用者的「下載」資料夾（不覆寫、不執行），在檔案總管顯示並提示。只在 UI 執行緒改狀態
internal sealed partial class AgentHub
{
    /// <summary>測試接縫：取代目前的檔案來源</summary>
    internal IPeerFileSource? PeerFileSourceForTest { get; set; }

    /// <summary>測試接縫：取代「下載」資料夾</summary>
    internal string? PeerDownloadsForTest { get; set; }

    /// <summary>HandleCommand 開頭（擋副本之前）：peerFileSave 在這裡處理，回 true</summary>
    private bool TryHandlePeerFileSave(IAgentView view, JObject command)
    {
        if (command.Value<string>("cmd") != "peerFileSave")
            return false;
        var conversationId = command.Value<string>("conversationId");
        var parsed = ReplicaMediaRules.KeyOfRelativeUrl(command.Value<string>("url"));
        if (conversationId == null || !conversations.TryGetValue(conversationId, out var c) || !c.IsReplica || !peerLive
            || parsed is not { Purpose: MediaPurposes.File } file || replicaMediaUrls is not { } urls || !urls.IsAllowed(file.Key, file.Purpose)
            || (PeerFileSourceForTest ?? CurrentReplicaMediaSource() as IPeerFileSource) is not { } source)
        {
            Post(view, PeerFileRules.Toast("現在不能下載這個檔案（連結已過期或沒有連上伺服器），請重新讀取一次", "error"));
            return true;
        }

        var name = command.Value<string>("name");
        // Word、Excel、PowerPoint、PDF（網頁帶 open:true，2026-11-05）：下載完用這台的預設程式開；白名單以外的照舊只在檔案總管顯示（同 openPath）
        bool open = command["open"] is JValue { Type: JTokenType.Boolean } flag && (bool)flag.Value!;
        var downloads = PeerDownloadsForTest ?? PeerFileRules.DownloadsFolder();
        var viewId = view.Id;
        _ = Task.Run(async () =>
        {
            string? saved = null;
            try
            {
                if (await source.FetchFileAsync(file.Key, CancellationToken.None).ConfigureAwait(false) is { } got)
                    saved = await PeerFileRules.SaveAsync(downloads, got.FileName ?? name, got.Body).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("下載其他電腦的檔案失敗", ex);
            }

            host.BeginInvoke(() =>
            {
                if (disposed)
                    return;
                bool opened = saved != null && open && AgentAttachmentRules.IsSafeToOpen(saved);
                if (opened)
                    host.OpenWithShell(saved!);
                else if (saved != null)
                    host.RevealInExplorer(saved);
                if (views.TryGetValue(viewId, out var v))
                    Post(v, saved != null
                        ? PeerFileRules.Toast("已下載到「" + Path.GetFileName(saved) + "」" + (opened ? "，用預設程式開啟" : ""), "success")
                        : PeerFileRules.Toast("下載失敗：讀不到這個檔案，請重新讀取一次", "error"));
            });
        });
        return true;
    }
}

/// <summary>桌面遠端操作的檔案下載規則（sync-protocol 14.7、web-protocol 的 peerFileSave）</summary>
internal static class PeerFileRules
{
    /// <summary>同 fileFetch 的單檔上限（20 MB）</summary>
    public const long MaxBytes = 20L * 1024 * 1024;

    /// <summary>沒有檔名時的名稱</summary>
    public const string DefaultName = "下載的檔案";

    /// <summary>使用者的「下載」資料夾（%USERPROFILE%\Downloads）</summary>
    public static string DownloadsFolder() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>
    /// 存檔用的名稱：只留檔名（去掉路徑）、換掉 Windows 不收的字元與控制字元、去掉結尾的點與空白、保留名稱（CON、NUL…）前面加底線、最多 120 字；
    /// 空的是 <see cref="DefaultName"/>
    /// </summary>
    public static string SafeFileName(string? name)
    {
        var raw = string.IsNullOrWhiteSpace(name) ? DefaultName : name;
        raw = raw.Replace('\\', '/');
        raw = raw[(raw.LastIndexOf('/') + 1)..];
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(raw.Length);
        foreach (var ch in raw)
            builder.Append(char.IsControl(ch) || Array.IndexOf(invalid, ch) >= 0 || char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.Format ? '_' : ch);
        var cleaned = builder.ToString().Trim().TrimEnd('.', ' ');
        if (cleaned.Length > 120)
            cleaned = cleaned[..120].TrimEnd('.', ' ');
        if (cleaned.Length == 0)
            cleaned = DefaultName;
        var stem = Path.GetFileNameWithoutExtension(cleaned).ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsAsciiDigit(stem[3]))
            cleaned = "_" + cleaned;
        return cleaned;
    }

    /// <summary>寫進 <paramref name="folder"/>（不存在就建立）：同名時加「 (2)」、「 (3)」…，不覆寫既有的檔；回寫好的完整路徑</summary>
    public static async Task<string> SaveAsync(string folder, string? name, byte[] body)
    {
        Directory.CreateDirectory(folder);
        var safe = SafeFileName(name);
        var stem = Path.GetFileNameWithoutExtension(safe);
        var ext = Path.GetExtension(safe);
        for (var n = 1; n < 1000; n++)
        {
            var path = Path.Combine(folder, n == 1 ? safe : $"{stem} ({n}){ext}");
            try
            {
                await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
                    await file.WriteAsync(body).ConfigureAwait(false);
                // 其他電腦來的檔：標成從網際網路下載（Office 以保護的檢視開啟，2026-11-07 資安檢查）
                MarkOfTheWeb.Mark(path);
                return path;
            }
            catch (IOException) when (File.Exists(path))
            {
                // 同名的檔已經在：換下一個名字
            }
        }

        throw new IOException("「下載」資料夾裡同名的檔太多");
    }

    public static JObject Toast(string text, string level) => new() { ["type"] = "toast", ["text"] = text, ["level"] = level };
}
