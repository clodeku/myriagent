using System.Text;
using Myriagent.Json;
using Newtonsoft.Json;

namespace Myriagent.Core;

/// <summary>讀檔結果：不存在、成功、失敗（內容損毀時已把原檔改名備份）</summary>
internal enum JsonReadStatus
{
    Missing,
    Ok,

    /// <summary>內容損毀：原檔已改名成 *.corrupt-時間（之後寫入新檔不會蓋掉它）</summary>
    Corrupt,

    /// <summary>讀不到（被占用、沒有權限）：原檔還在，呼叫端不可以覆寫它</summary>
    Unreadable,
}

/// <summary>
/// JSON 檔讀寫。寫檔先寫暫存檔、落盤後再取代（寫到一半當掉不會留下壞檔）。
/// 讀檔要區分「不存在」與「失敗」：失敗時絕不能當成空資料再存回去（會把使用者的資料蓋掉），
/// 內容損毀就把原檔改名備份、讀不到就讓呼叫端不要覆寫。
/// </summary>
internal static class JsonFile
{
    /// <summary>
    /// 存檔的 Newtonsoft 設定：就是 Protocol 的 ProtocolJson.Settings（縮排、略過 null、時間轉本機時區），
    /// 送給伺服器的原始項目與 items.json 才會是同一種格式
    /// </summary>
    public static readonly JsonSerializerSettings Settings = ProtocolJson.Settings;

    private static readonly UTF8Encoding Utf8 = new(false);

    public static (JsonReadStatus Status, T? Value, string? BackupPath) TryRead<T>(string path) where T : class
    {
        if (!File.Exists(path))
            return (JsonReadStatus.Missing, null, null);

        string? text = null;
        for (int attempt = 0; attempt < 3 && text == null; attempt++)
        {
            try
            {
                text = File.ReadAllText(path, Utf8);
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(80);   // 其他程式短暫占用（防毒掃描、備份軟體）
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"讀不到 {path}：{ex.Message}");
                return (JsonReadStatus.Unreadable, null, null);
            }
        }
        if (text == null)
            return (JsonReadStatus.Unreadable, null, null);

        try
        {
            var value = JsonConvert.DeserializeObject<T>(text, Settings);
            if (value != null)
                return (JsonReadStatus.Ok, value, null);
        }
        catch (JsonException ex)
        {
            Log.Warn($"{path} 內容損毀：{ex.Message}");
        }

        var backup = BackupCorrupt(path);
        return backup == null ? (JsonReadStatus.Unreadable, null, null) : (JsonReadStatus.Corrupt, null, backup);
    }

    /// <summary>讀檔；不存在或失敗回 null（只給「失敗時不會被存回去」的資料用，例如唯讀快取）</summary>
    public static T? Read<T>(string path) where T : class => TryRead<T>(path).Value;

    /// <summary>把損毀的檔案改名保留（*.corrupt-yyyyMMdd-HHmmss）；改名失敗回 null</summary>
    private static string? BackupCorrupt(string path)
    {
        var backup = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            File.Move(path, backup, overwrite: true);
            Log.Warn($"已把損毀的 {path} 備份成 {backup}");
            return backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"備份損毀檔 {path} 失敗：{ex.Message}");
            return null;
        }
    }

    /// <param name="createDirectory">
    /// 資料夾不存在時建立（預設）；false＝不建立，資料夾不在就丟 DirectoryNotFoundException（背景寫入的對話紀錄：對話這時已經刪掉或搬進回收區，
    /// 重建只有 items.json 的資料夾會讓刪掉的對話在下次啟動時被「還原」回來）
    /// </param>
    public static void Write<T>(string path, T value, bool createDirectory = true)
    {
        var dir = Path.GetDirectoryName(path);
        if (createDirectory && !string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.None))
        {
            Serialize(stream, value);
            stream.Flush(flushToDisk: true);   // 先落盤再取代：斷電時不會出現「改名成功但內容是空的」
        }
        ReplaceWithRetry(temp, path);
    }

    /// <summary>
    /// 直接序列化進資料流（UTF-8、沒有 BOM）：位元組與 Utf8.GetBytes(JsonConvert.SerializeObject(value, Settings)) 相同（JsonFileTests 逐位元組比對），
    /// 但不產生整份的字串與位元組陣列——items.json 一兩 MB 時那是兩份大物件堆積的配置加一次複製，而且都在 UI 執行緒上
    /// </summary>
    internal static void Serialize<T>(Stream stream, T value)
    {
        var serializer = JsonSerializer.CreateDefault(Settings);
        using var text = new StreamWriter(stream, Utf8, 1 << 16, leaveOpen: true);
        using var json = new JsonTextWriter(text) { Formatting = serializer.Formatting };
        serializer.Serialize(json, value);
    }

    /// <summary>以暫存檔取代目標檔時的重試次數（只在目標檔被占用時重試）</summary>
    private const int ReplaceRetries = 5;

    /// <summary>
    /// 以暫存檔取代目標檔。目標檔正被別的程式開著（同步的重整在執行緒集區唯讀讀它、防毒掃描），即使對方允許共用刪除，Windows 也拒絕取代
    /// （UnauthorizedAccessException 或共用違規）：目標檔存在時短暫重試，最後一次照樣丟出。平常不會等（合併前審查 T3）
    /// </summary>
    internal static void ReplaceWithRetry(string temp, string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < ReplaceRetries && IsInUse(ex) && IsTransient(path))
            {
                Thread.Sleep(20 + (attempt * 10));
            }
        }
    }

    /// <summary>
    /// 目標檔存在而且沒有唯讀屬性才可能只是暫時被占用；唯讀（一定被拒絕）與查不到屬性（含不存在）立刻丟出，不在 UI 執行緒白等
    /// （第二輪合併前審查 T3）
    /// </summary>
    private static bool IsTransient(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReadOnly) == 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>檔案被占用：拒絕存取，或共用違規、鎖定違規（ERROR_SHARING_VIOLATION 32、ERROR_LOCK_VIOLATION 33）</summary>
    private static bool IsInUse(Exception ex)
        => ex is UnauthorizedAccessException || (ex is IOException io && (io.HResult & 0xFFFF) is 32 or 33);
}
