using System.Collections.Concurrent;
using System.Security.Cryptography;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Sync.Fakes;

/// <summary>
/// 媒體轉送測試的資料夾（WP4.7 步 8）：暫存資料夾裡照正式的樣子放一個資料根目錄（<c>data\conversations\{id}\attachments</c>、<c>workspace</c>）
/// 與一個工作資料夾（<c>repo</c>）。禁止清單的使用者資料夾放在另一個磁碟代號（Q:），暫存資料夾本身才不會被當成 %LOCALAPPDATA% 擋掉；
/// 資料根目錄是真的，所以附件要靠「允許的子樹」才放行（照正式的範圍）
/// </summary>
internal sealed class MediaTestFiles : IDisposable
{
    /// <summary>對話 id（線上格式）</summary>
    public const string ConversationId = "0f3c9a52b7e94d0c8a61f2d4e5b6c7d8";

    public MediaTestFiles()
    {
        Dir = new TempDirectory("media-server");
        DataRoot = Dir.CreateDirectory("data");
        ConversationDirectory = Dir.CreateDirectory(Path.Combine("data", "conversations", ConversationId));
        AttachmentDirectory = Dir.CreateDirectory(Path.Combine("data", "conversations", ConversationId, "attachments"));
        DefaultWorkingDirectory = Dir.CreateDirectory(Path.Combine("data", "conversations", ConversationId, "workspace"));
        WorkingDirectory = Dir.CreateDirectory("repo");
        Folders = new MediaFolderRoots(@"Q:\Users\Amy", @"Q:\Users\Amy\AppData\Local", @"Q:\Users\Amy\AppData\Roaming", DataRoot, [@"Q:\accounts\acc-2\claude"]);
    }

    public TempDirectory Dir { get; }

    public string DataRoot { get; }

    public string ConversationDirectory { get; }

    public string AttachmentDirectory { get; }

    public string DefaultWorkingDirectory { get; }

    public string WorkingDirectory { get; }

    public MediaFolderRoots Folders { get; }

    /// <summary>假的 PNG（魔數正確）</summary>
    public static byte[] Png { get; } = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02, 0x03];

    /// <summary>伺服器的轉送 id 與參照的格式（128 位元亂數的 Crockford Base32）</summary>
    public static string NewId() => CrockfordBase32.Encode(RandomNumberGenerator.GetBytes(16));

    /// <summary>守門員：磁碟一律當成固定磁碟（Q: 的禁止清單根目錄才正規化得了；不查真的 GetDriveType）</summary>
    public static RemoteFileGuard Guard() => new(_ => RemoteDriveKind.Fixed, TimeSpan.FromSeconds(10), TimeProvider.System);

    /// <summary>寫一個檔（相對暫存資料夾），回傳完整路徑</summary>
    public string Write(string relativePath, byte[] content)
    {
        var path = Dir.Combine(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>看得到的對話的快照（<paramref name="resolved"/>：項目已載入時中樞解析出的檔）</summary>
    public MediaScopeSnapshot Snapshot(bool itemsLoaded = true, (string Path, string FileName)? resolved = null) =>
        new(ConversationId, true, ConversationDirectory, AttachmentDirectory, DefaultWorkingDirectory, WorkingDirectory, itemsLoaded, resolved, Folders);

    public void Dispose() => Dir.Dispose();
}

/// <summary>代替中樞的媒體快照來源：記錄每次呼叫與執行緒，回 <see cref="Answer"/></summary>
internal sealed class FakeMediaScope : IMediaScopeSource
{
    public Func<MediaScopeQuery, MediaScopeSnapshot?> Answer { get; set; } = _ => null;

    public ConcurrentQueue<(MediaScopeQuery Query, int Thread)> Queries { get; } = new();

    public MediaScopeSnapshot? GetMediaScope(MediaScopeQuery query)
    {
        Queries.Enqueue((query, Environment.CurrentManagedThreadId));
        return Answer(query);
    }
}
