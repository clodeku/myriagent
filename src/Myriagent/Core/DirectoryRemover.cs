namespace Myriagent.Core;

/// <summary>
/// 刪除整個資料夾。Directory.Delete 不會清唯讀屬性，遇到唯讀檔（Git 把 .git\objects 底下的物件檔都設成唯讀）
/// 或唯讀資料夾一定失敗：失敗時清掉剩下項目的唯讀屬性再重試。連結（junction、symlink）本身會被刪掉，
/// 但不進去、也不改它的屬性（目標不是這個資料夾的東西）。檔案被其他程式開著時重試幾次，仍刪不掉回 false。
/// </summary>
internal static class DirectoryRemover
{
    public static bool TryDelete(string path, int attempts = 3, int retryDelayMs = 200)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (!Directory.Exists(path))
                return true;
            try
            {
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ClearReadOnly(path);
                if (attempt < attempts - 1)
                    Thread.Sleep(retryDelayMs);
            }
        }
        return !Directory.Exists(path);
    }

    /// <summary>清掉資料夾（含自己）底下所有檔案與資料夾的唯讀屬性；遇到連結不進去、不改屬性，個別項目失敗就略過</summary>
    internal static void ClearReadOnly(string path)
    {
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true, RecurseSubdirectories = false };
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(path));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            try
            {
                if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                ClearReadOnlyAttribute(directory);
                foreach (var entry in directory.EnumerateFileSystemInfos("*", options))
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;
                    if (entry is DirectoryInfo sub)
                        pending.Push(sub);
                    else
                        ClearReadOnlyAttribute(entry);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 這一層列不出來（剛被刪掉、沒有權限）：其他資料夾照常處理
            }
        }
    }

    private static void ClearReadOnlyAttribute(FileSystemInfo entry)
    {
        try
        {
            var attributes = entry.Attributes;
            if ((attributes & FileAttributes.ReadOnly) != 0)
                entry.Attributes = attributes & ~FileAttributes.ReadOnly;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
