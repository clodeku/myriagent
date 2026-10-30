namespace Myriagent.Agents;

/// <summary>截尾的結果種類</summary>
internal enum TranscriptTailOutcome
{
    /// <summary>檔案不存在（還沒寫過逐字稿／rollout）：沒有動</summary>
    Missing,

    /// <summary>結尾已是 LF（或是空檔）：沒有動</summary>
    Intact,

    /// <summary>截掉了寫到一半的尾行；切下的位元組在 BackupPath</summary>
    Trimmed,

    /// <summary>讀寫失敗（檔案被占用、沒有權限、備份寫不進去）：原檔沒有動，Error 是原因</summary>
    Failed,
}

/// <param name="OriginalLength">截尾前的長度（位元組）</param>
/// <param name="KeptLength">截尾後保留的長度（沒有動時等於 OriginalLength）</param>
internal sealed record TranscriptTailResult(TranscriptTailOutcome Outcome, long OriginalLength, long KeptLength, string? BackupPath = null, string? Error = null);

/// <summary>
/// 重開 CLI 前的截尾（熱更新、app 或電腦當機後的第一次續接）：Claude 逐字稿（jsonl）與 Codex rollout 都是只附加的 JSON 行，
/// 程序在一行寫到一半時被結束，新程序會直接接在半行後面寫，把下一筆黏成永久壞行。所以 --resume／thread/resume 之前檢查最後一個位元組：
/// 不是 LF 就截到最後一個 LF，切下的位元組先另存成 .tail 備份（保留 7 天）再截。
/// 只能在確認寫這個檔的程序都已結束之後呼叫（ProcessTreeSnapshot.WaitForExitAsync 回 true 之後）
/// </summary>
internal static class TranscriptTailRules
{
    /// <summary>從檔尾往前找 LF 時每次讀的大小</summary>
    private const int ChunkSize = 64 * 1024;

    /// <summary>.tail 備份保留多久</summary>
    public static readonly TimeSpan BackupRetention = TimeSpan.FromDays(7);

    /// <summary>
    /// 純函式：tail 是檔案 [fileLength - tail.Length, fileLength) 這一段的位元組，回傳最後一個 LF 之後的位置（＝要保留的長度）；
    /// 這一段裡沒有 LF 回 null（要再往前讀；整個檔案都沒有 LF 時保留 0）
    /// </summary>
    public static long? FindKeepLength(ReadOnlySpan<byte> tail, long fileLength)
    {
        int index = tail.LastIndexOf((byte)'\n');
        return index < 0 ? null : fileLength - tail.Length + index + 1;
    }

    /// <summary>備份檔名：{name}-{yyyyMMdd-HHmmss-fff}.tail（name 是 session id／thread id；不能當檔名的字元換成 _）</summary>
    public static string BuildBackupFileName(string name, DateTime now)
    {
        var safe = string.Concat((string.IsNullOrWhiteSpace(name) ? "transcript" : name.Trim()).Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        return $"{safe}-{now:yyyyMMdd-HHmmss-fff}.tail";
    }

    /// <summary>
    /// 檔案結尾不是 LF：截到最後一個 LF，切下的位元組先另存到 backupDirectory（BuildBackupFileName）再截；成功後順便清掉過期的備份。
    /// 讀檔帶 FileShare.ReadWrite | FileShare.Delete，不擋其他程式讀寫或改名。任何失敗都不動原檔
    /// </summary>
    /// <param name="backupName">備份檔名的前綴（Claude 的 session id、Codex 的 thread id）</param>
    public static TranscriptTailResult TrimPartialLine(string path, string backupDirectory, string backupName, DateTime now)
    {
        long length = 0;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            length = stream.Length;
            if (length == 0)
                return new TranscriptTailResult(TranscriptTailOutcome.Intact, 0, 0);
            stream.Position = length - 1;
            if (stream.ReadByte() == '\n')
                return new TranscriptTailResult(TranscriptTailOutcome.Intact, length, length);

            long keep = FindKeepLengthInFile(stream, length);
            Directory.CreateDirectory(backupDirectory);
            var backupPath = CreateBackup(stream, keep, length, backupDirectory, BuildBackupFileName(backupName, now));
            stream.SetLength(keep);
            stream.Flush(flushToDisk: true);
            CleanupBackups(backupDirectory, now);
            return new TranscriptTailResult(TranscriptTailOutcome.Trimmed, length, keep, backupPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new TranscriptTailResult(TranscriptTailOutcome.Missing, 0, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new TranscriptTailResult(TranscriptTailOutcome.Failed, length, length, Error: ex.Message);
        }
    }

    /// <summary>
    /// 檔尾是不是完整的一行（最後一個位元組是 LF，或是空檔）；檔案不存在或讀不到回 null。
    /// 讀檔帶 FileShare.ReadWrite | FileShare.Delete，不擋其他程式讀寫或改名
    /// </summary>
    public static bool? EndsWithLineFeed(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length == 0)
                return true;
            stream.Position = stream.Length - 1;
            return stream.ReadByte() == '\n';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>刪掉超過保留期限的 .tail 備份，回傳刪了幾個（刪不掉的略過）</summary>
    public static int CleanupBackups(string backupDirectory, DateTime now)
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(backupDirectory))
                return 0;
            foreach (var file in Directory.EnumerateFiles(backupDirectory, "*.tail"))
            {
                try
                {
                    if (now - File.GetLastWriteTime(file) > BackupRetention)
                    {
                        File.Delete(file);
                        removed++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return removed;
    }

    /// <summary>從檔尾一段一段往前找最後一個 LF；整個檔案都沒有 LF 回 0</summary>
    private static long FindKeepLengthInFile(Stream stream, long length)
    {
        var buffer = new byte[(int)Math.Min(ChunkSize, length)];
        long end = length;
        while (end > 0)
        {
            int count = (int)Math.Min(buffer.Length, end);
            long start = end - count;
            stream.Position = start;
            stream.ReadExactly(buffer, 0, count);
            if (FindKeepLength(buffer.AsSpan(0, count), end) is { } keep)
                return keep;
            end = start;
        }
        return 0;
    }

    /// <summary>把 [keep, length) 寫成備份檔並落盤（同名已存在就加 -2、-3…，不覆寫別的備份）；回傳備份的完整路徑</summary>
    private static string CreateBackup(FileStream source, long keep, long length, string directory, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        for (int n = 1; ; n++)
        {
            var path = Path.Combine(directory, n == 1 ? fileName : $"{stem}-{n}.tail");
            FileStream target;
            try
            {
                target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException) when (File.Exists(path) && n < 100)
            {
                continue;
            }

            try
            {
                using (target)
                {
                    source.Position = keep;
                    var buffer = new byte[(int)Math.Min(ChunkSize, Math.Max(1, length - keep))];
                    long remaining = length - keep;
                    while (remaining > 0)
                    {
                        int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (read <= 0)
                            throw new IOException("讀取逐字稿尾段時檔案變短了");
                        target.Write(buffer, 0, read);
                        remaining -= read;
                    }
                    target.Flush(flushToDisk: true);
                }
            }
            catch
            {
                // 備份沒寫完就不截原檔：刪掉不完整的備份，原樣丟出（呼叫端回 Failed）
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
                throw;
            }
            return path;
        }
    }
}
