using Myriagent.Agents;
using Myriagent.Media;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>要上傳的一個本機檔（<see cref="Subfolder"/> 是在那台的資料夾底下要建的子資料夾：上傳整個資料夾時是資料夾名稱加相對路徑）</summary>
internal sealed record CliPushFile(string Path, string Name, string Subfolder, long Size);

/// <summary>一個檔的結果（myriagent-remote push／pull 的一列）</summary>
internal sealed record CliFileOutcome(string Source, string? Saved, string? Error);

/// <summary>
/// 代理經 myriagent-remote 在電腦之間傳檔案（push：這台 → 那台的資料夾；pull：那台的檔或資料夾 → 這台的資料夾；2026-11-15 使用者要求
/// 「也要讓對話框 agent 可以直接上傳或下載檔案到另一台電腦」）的規則。走電腦狀態面板的檔案傳輸同一條路（devicePut、deviceFetch、deviceTree、
/// deviceFetchMany，那台照舊要「可以操作」）。純函式，除了 <see cref="ExpandLocal"/> 讀這台的檔案系統
/// </summary>
internal static class CliFileRules
{
    /// <summary>一次最多幾個路徑</summary>
    public const int MaxPaths = 100;

    /// <summary>一次最多幾個檔（資料夾展開之後）</summary>
    public const int MaxFiles = 1000;

    /// <summary>那台存檔的進度多久沒有變化就放棄（等的是那台從伺服器下載、存檔）</summary>
    public static readonly TimeSpan SaveStallLimit = TimeSpan.FromMinutes(3);

    /// <summary>
    /// 展開要上傳的本機路徑：檔案照收；資料夾連子資料夾的檔一起（子資料夾以反斜線接在資料夾名稱後面），跳過連結點與符號連結。
    /// 只收這台的本機磁碟（不收 UNC）、每個檔不超過 <see cref="DeviceTransferRules.MaxPutFileBytes"/>、合計不超過 <see cref="MaxFiles"/> 個
    /// </summary>
    public static (List<CliPushFile>? Files, string? Problem) ExpandLocal(IReadOnlyList<string> paths)
    {
        var files = new List<CliPushFile>();
        foreach (var raw in paths)
        {
            var path = raw.Trim();
            if (!Path.IsPathFullyQualified(path) || !AgentAttachmentRules.IsLocalDrivePath(path))
                return (null, $"「{raw}」不是這台本機磁碟上的完整路徑");
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                if (info.Length > DeviceTransferRules.MaxPutFileBytes)
                    return (null, $"「{path}」超過 512 MB，不能經伺服器傳送（請先壓縮或分割）");
                files.Add(new CliPushFile(info.FullName, info.Name, string.Empty, info.Length));
            }
            else if (Directory.Exists(path))
            {
                var root = new DirectoryInfo(path);
                var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
                foreach (var info in root.EnumerateFiles("*", options))
                {
                    if (info.Length > DeviceTransferRules.MaxPutFileBytes)
                        return (null, $"「{info.FullName}」超過 512 MB，不能經伺服器傳送（請先壓縮或分割）");
                    var relative = Path.GetRelativePath(root.FullName, info.DirectoryName ?? root.FullName);
                    var sub = relative == "." ? root.Name : root.Name + "\\" + relative.Replace('/', '\\');
                    files.Add(new CliPushFile(info.FullName, info.Name, sub, info.Length));
                    if (files.Count > MaxFiles)
                        return (null, $"一次最多傳 {MaxFiles} 個檔");
                }
            }
            else
            {
                return (null, $"找不到「{path}」");
            }

            if (files.Count > MaxFiles)
                return (null, $"一次最多傳 {MaxFiles} 個檔");
        }

        return files.Count == 0 ? (null, "沒有要傳的檔（資料夾是空的）") : (files, null);
    }

    /// <summary>
    /// 上傳到伺服器（E4）的結果 → devicePut 的一筆（<c>{uploadId, name, size, sha256}</c>，同網頁的 <c>liverules.uploadResult</c>）；
    /// 不成功回 null 與原因
    /// </summary>
    public static (JObject? Entry, string? Problem) UploadEntry(int status, string? json, string uploadId, string name)
    {
        if (status is not (200 or 201))
        {
            return (null, status switch
            {
                0 => "上傳到伺服器失敗（沒有連上伺服器）",
                413 => "檔案太大，伺服器不收",
                507 => "伺服器的磁碟空間不夠",
                _ => $"上傳到伺服器失敗（{status}）",
            });
        }

        JObject? o = null;
        try
        {
            o = json is null ? null : JObject.Parse(json);
        }
        catch (JsonException)
        {
        }

        if (o?["uploadId"] is not JValue { Type: JTokenType.String, Value: string id } || id != uploadId
            || o["sha256"] is not JValue { Type: JTokenType.String, Value: string sha } || !BlobIdRules.IsBlobId(sha)
            || o["size"] is not JValue { Type: JTokenType.Integer } size)
            return (null, "伺服器的回應不對，上傳沒有完成");
        return (new JObject { ["uploadId"] = uploadId, ["name"] = name, ["size"] = size.Value<long>(), ["sha256"] = sha }, null);
    }

    /// <summary>整個資料夾下載時一個檔要放的子資料夾：資料夾名稱加上檔案所在的相對資料夾（反斜線）</summary>
    public static string PullSubfolder(string folderName, string relativePath)
    {
        var rel = relativePath.Replace('/', '\\');
        var cut = rel.LastIndexOf('\\');
        return cut <= 0 ? folderName : folderName + "\\" + rel[..cut];
    }

    /// <summary>給代理看的大小</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };
}
