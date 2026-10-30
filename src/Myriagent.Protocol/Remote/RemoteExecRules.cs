using System.Buffers;
using System.Text;
using Myriagent.Sync;

namespace Myriagent.Remote;

/// <summary>
/// 遠端執行指令的共用規則（docs/sync-protocol.md 14.11、docs/plans/2026-10-25-remote-exec.md；2026-10-25 擁有者要求：已配對的電腦之間
/// 由對話裡的代理互相執行主控台指令，高風險的要在那台電腦同意）。<c>remoteExec</c> 照一般的遠端指令簽發、去重、過閘門，
/// 輸出、狀態與結束碼走 <c>exec1</c> 的串流（<see cref="ExecChunk"/>）。伺服器、發起與被操作的電腦共用
/// </summary>
internal static class RemoteExecRules
{
    /// <summary>Windows PowerShell 5.1（每台都有；預設）</summary>
    public const string PowerShell = "powershell";

    /// <summary>PowerShell 7（那台要裝了）</summary>
    public const string Pwsh = "pwsh";

    /// <summary>cmd.exe</summary>
    public const string Cmd = "cmd";

    public static IReadOnlyList<string> Shells { get; } = [PowerShell, Pwsh, Cmd];

    /// <summary>指令的長度上限（UTF-16 字元）：cmd 的命令列上限是 8191 字</summary>
    public const int MaxCommandLength = 8000;

    /// <summary>沒給 timeoutSec 時的上限（秒）</summary>
    public const int DefaultTimeoutSeconds = 600;

    /// <summary>timeoutSec 的上限（秒）：一天</summary>
    public const int MaxTimeoutSeconds = 86_400;

    /// <summary>label（發起的對話標題，給那台的人看）的長度上限</summary>
    public const int MaxLabelLength = 120;

    /// <summary>高風險指令等那台的人同意的時間：過了就當成拒絕</summary>
    public static readonly TimeSpan ConfirmTimeout = TimeSpan.FromMinutes(3);

    /// <summary>伺服器在指令的上限之外多留的轉送時間（等待同意、最後一段在路上）</summary>
    public static readonly TimeSpan RelayGrace = ConfirmTimeout + TimeSpan.FromMinutes(2);

    /// <summary>發起的電腦連不上這麼久，伺服器就不再轉送、請被操作的電腦停止（<see cref="ExecOutputResult.Keep"/> 是 false）</summary>
    public static readonly TimeSpan OriginLostLimit = TimeSpan.FromMinutes(2);

    // ── 串流的段落 ──

    public const string KindStatus = "status";

    public const string KindOut = "out";

    public const string KindErr = "err";

    public const string KindExit = "exit";

    /// <summary>status：等待那台的人同意（<c>text</c> 是高風險的原因）</summary>
    public const string StatusConfirm = "confirm";

    /// <summary>status：已開始執行</summary>
    public const string StatusStarted = "started";

    /// <summary>status：輸出超過 <see cref="MaxOutputChars"/>，之後的不再轉送（程式照樣跑完）</summary>
    public const string StatusTruncated = "truncated";

    /// <summary>exit：超過上限時間，已停止</summary>
    public const string ExitTimeout = "timeout";

    /// <summary>exit：發起的一方要求停止</summary>
    public const string ExitCancelled = "cancelled";

    /// <summary>exit：那台的人拒絕（或時間內沒有回應），沒有執行</summary>
    public const string ExitDenied = "denied";

    /// <summary>exit：啟動失敗（找不到殼層、工作資料夾不能用…），沒有執行</summary>
    public const string ExitFailed = "failed";

    /// <summary>exit：伺服器不再轉送（發起的電腦離線太久），已停止</summary>
    public const string ExitOrphaned = "orphaned";

    /// <summary>exit：被操作的電腦的 app 結束，已停止</summary>
    public const string ExitAppExit = "app_exit";

    public static IReadOnlyList<string> StatusCodes { get; } = [StatusConfirm, StatusStarted, StatusTruncated];

    public static IReadOnlyList<string> ExitCodes { get; } = [ExitTimeout, ExitCancelled, ExitDenied, ExitFailed, ExitOrphaned, ExitAppExit];

    /// <summary>一段的 text 上限（UTF-16 字元）</summary>
    public const int MaxChunkChars = 32_768;

    /// <summary>一個指令轉送的輸出上限（out＋err 的 UTF-16 字元）：超過的丟掉、送一段 truncated</summary>
    public const long MaxOutputChars = 8L * 1024 * 1024;

    /// <summary>輸出合併成一段的等待時間（每段最多 <see cref="MaxChunkChars"/> 字）</summary>
    public static readonly TimeSpan OutputBatchDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>指令的上限時間（秒）：沒給用 <see cref="DefaultTimeoutSeconds"/>，超出範圍的夾進 1～<see cref="MaxTimeoutSeconds"/></summary>
    public static int TimeoutSeconds(int? requested) => requested is { } s ? Math.Clamp(s, 1, MaxTimeoutSeconds) : DefaultTimeoutSeconds;

    /// <summary>
    /// 一段串流合不合格（伺服器收 <c>ExecOutput</c>、發起的電腦收 <c>ExecReply</c> 都照它）：commandId 是 ULID、seq ≥ 0、kind 認得；
    /// text ≤ <see cref="MaxChunkChars"/> 而且沒有不成對的代理字元；exitCode 只在 exit；code 只在 status（必帶）與 exit（選用），而且是認得的名稱
    /// </summary>
    public static bool IsValidChunk(ExecChunk? chunk)
    {
        if (chunk is null || !SyncIds.IsUlid(chunk.CommandId) || chunk.Seq < 0)
            return false;
        if (chunk.Text is { } text && (text.Length > MaxChunkChars || !IsWellFormed(text)))
            return false;
        return chunk.Kind switch
        {
            KindStatus => chunk.ExitCode is null && chunk.Code is { } code && StatusCodes.Contains(code, StringComparer.Ordinal),
            KindOut or KindErr => chunk.ExitCode is null && chunk.Code is null && chunk.Text is { Length: > 0 },
            KindExit => chunk.Code is null || ExitCodes.Contains(chunk.Code, StringComparer.Ordinal),
            _ => false,
        };
    }

    /// <summary>
    /// 把輸出切成每段最多 <see cref="MaxChunkChars"/> 字（不切開代理字元對）
    /// </summary>
    public static IEnumerable<string> Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var start = 0;
        while (start < text.Length)
        {
            var length = Math.Min(MaxChunkChars, text.Length - start);
            if (start + length < text.Length && char.IsHighSurrogate(text[start + length - 1]))
                length--;
            yield return text.Substring(start, length);
            start += length;
        }
    }

    /// <summary>沒有不成對的代理字元</summary>
    private static bool IsWellFormed(string text)
    {
        var rest = text.AsSpan();
        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(rest, out _, out var used) != OperationStatus.Done)
                return false;
            rest = rest[used..];
        }

        return true;
    }
}
