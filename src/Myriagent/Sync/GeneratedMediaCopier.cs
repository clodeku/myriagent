using Myriagent.Agents;
using Myriagent.Core;

namespace Myriagent.Sync;

/// <summary>
/// 把 Codex 產生的圖片複製到對話的 <c>attachments\generated\</c>（WP4.7 計畫 D45，擁有者 Q2 選 A；任何執行緒）：
/// <list type="number">
/// <item>複本已經在就直接用（不覆寫；原檔之後刪了照樣看得到，複本由呼叫端再經守門員開）。</item>
/// <item>原檔經 <see cref="RemoteFileGuard.OpenGeneratedSourceAsync"/> 開（窄的檢查，不碰範圍與禁止清單）、整份讀進記憶體（≤<see cref="GeneratedMediaRules.MaxCopyBytes"/>），
/// 內容格式（<see cref="AgentAttachmentRules.DetectImageMediaType"/>）要和副檔名相符。</item>
/// <item>目的資料夾建好之後確認路徑上沒有重新解析點（代理可以把 <c>generated</c> 換成 junction，寫到別的地方），寫暫存檔再
/// <c>File.Move(overwrite: false)</c>；一定是真的複製位元組，不用硬連結。</item>
/// </list>
/// 一次只複製一個（同時兩個請求要同一個複本時，第二個看到複本已經在）；失敗回 null（呼叫端回 404）
/// </summary>
internal sealed class GeneratedMediaCopier
{
    /// <summary>格式判斷要的開頭與結尾位元組數</summary>
    private const int HeadBytes = 12;

    private const int TailBytes = 16;

    private readonly RemoteFileGuard guard;
    private readonly SemaphoreSlim gate = new(1, 1);

    public GeneratedMediaCopier(RemoteFileGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        this.guard = guard;
    }

    /// <summary>複製過的檔數（記錄與測試用）</summary>
    public int Copied => Volatile.Read(ref copied);

    private int copied;

    /// <summary>
    /// 確保 <paramref name="destination"/>（<see cref="GeneratedMediaRules.CopyPath"/> 算的）有 <paramref name="source"/> 的複本；回複本路徑，做不到回 null。
    /// <paramref name="generatedRoots"/> 是已正規化的產生圖片根目錄
    /// </summary>
    public async Task<string?> EnsureCopyAsync(string source, string destination, IReadOnlyList<string> generatedRoots, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(generatedRoots);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(destination))
                return destination;

            byte[] content;
            var opened = await guard.OpenGeneratedSourceAsync(source, generatedRoots, GeneratedMediaRules.MaxCopyBytes, ct).ConfigureAwait(false);
            if (opened.File is not { } file)
            {
                Log.Info($"Codex 產生的圖片不複製：守門員拒絕（{opened.Reason}）");
                return null;
            }

            using (file)
            {
                content = new byte[checked((int)file.Length)];
                await file.Stream.ReadExactlyAsync(content, ct).ConfigureAwait(false);
            }

            var head = content.AsSpan(0, Math.Min(HeadBytes, content.Length));
            var tail = content.AsSpan(Math.Max(0, content.Length - TailBytes));
            var detected = AgentAttachmentRules.DetectImageMediaType(head, tail, content.LongLength);
            if (detected is null || detected != AgentAttachmentRules.GetClaudeImageMediaType(destination))
            {
                Log.Info("Codex 產生的圖片不複製：內容格式和副檔名不符或檔案不完整");
                return null;
            }

            return await Task.Run(() => Write(destination, content), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"複製 Codex 產生的圖片失敗：{ex.Message}");
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>寫進目的資料夾（暫存檔 → 不覆寫的搬移）；目的資料夾路徑上有重新解析點就不寫</summary>
    private string? Write(string destination, byte[] content)
    {
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        if (RemoteFileGuardRules.CheckSyntax(directory, out var normalized) != null || RemoteFileGuard.WalkReparsePoints(normalized!, directory: true) != null)
        {
            Log.Warn("複本的資料夾路徑上有重新解析點或不合格：不寫");
            return null;
        }

        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, content);
            try
            {
                File.Move(temporary, destination, overwrite: false);
                Interlocked.Increment(ref copied);
            }
            catch (IOException) when (File.Exists(destination))
            {
                // 別的程序剛好寫好了同一個複本：用它（守門員之後照樣驗）
            }

            return destination;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
