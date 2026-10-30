using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Myriagent.Sync;

/// <summary>
/// 逐字稿與檔案日誌的鏈雜湊（設計 4.12；第 5 階段計畫 5.6；伺服器與桌面共用）：
/// <code>
/// H_0 = SHA256( UTF8("myriagent-log/1|" + logId + "|" + gen) )
/// H_n = SHA256( H_{n-1} ‖ SHA256(chunk_n) )        // chunk_n＝第 n 次附加的原始位元組（解壓後）
/// </code>
/// 只偵測傳輸與存放時的損毀、截斷（擁有者決定 2：不簽章）：同一段位元組切成不同的段，鏈雜湊不同，所以伺服器以 <c>log_chunks</c> 的邊界重算
/// （<c>LogScrub</c>），整檔另以 SHA-256 比對。線上一律是標準 base64（<see cref="ToWire"/>，44 字）
/// </summary>
internal static class LogChainRules
{
    public const string Prefix = "myriagent-log/1|";

    public const int HashBytes = 32;

    /// <summary>base64 的長度（32 位元組）</summary>
    public const int WireLength = 44;

    /// <summary>H_0：還沒有任何附加時的鏈雜湊（每個世代各自從頭算）</summary>
    public static byte[] Initial(string logId, long gen)
    {
        ArgumentNullException.ThrowIfNull(logId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gen);
        return SHA256.HashData(Encoding.UTF8.GetBytes(Prefix + logId + "|" + gen.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>H_n：上一個鏈雜湊接上這一段（一次給整段）</summary>
    public static byte[] Next(ReadOnlySpan<byte> previous, ReadOnlySpan<byte> chunk) => Combine(previous, SHA256.HashData(chunk));

    /// <summary>H_n：上一個鏈雜湊接上這一段的 SHA-256（串流時先算好段的雜湊）</summary>
    public static byte[] Combine(ReadOnlySpan<byte> previous, ReadOnlySpan<byte> chunkSha256)
    {
        if (previous.Length != HashBytes)
            throw new ArgumentException("鏈雜湊要 32 位元組", nameof(previous));
        if (chunkSha256.Length != HashBytes)
            throw new ArgumentException("段的 SHA-256 要 32 位元組", nameof(chunkSha256));
        Span<byte> joined = stackalloc byte[HashBytes * 2];
        previous.CopyTo(joined);
        chunkSha256.CopyTo(joined[HashBytes..]);
        return SHA256.HashData(joined);
    }

    /// <summary>從 H_0 依序接上每一段（測試、掃描用）</summary>
    public static byte[] Over(string logId, long gen, IEnumerable<ReadOnlyMemory<byte>> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        var chain = Initial(logId, gen);
        foreach (var chunk in chunks)
            chain = Next(chain, chunk.Span);
        return chain;
    }

    /// <summary>線上的寫法：標準 base64</summary>
    public static string ToWire(ReadOnlySpan<byte> chain)
    {
        if (chain.Length != HashBytes)
            throw new ArgumentException("鏈雜湊要 32 位元組", nameof(chain));
        return Convert.ToBase64String(chain);
    }

    /// <summary>讀線上的鏈雜湊：恰好 44 字的標準 base64、解出 32 位元組、而且是唯一的寫法（再編一次要完全相同）</summary>
    public static bool TryFromWire(string? text, [NotNullWhen(true)] out byte[]? chain)
    {
        chain = null;
        if (text is not { Length: WireLength })
            return false;
        var buffer = new byte[HashBytes + 3];
        if (!Convert.TryFromBase64String(text, buffer, out var written) || written != HashBytes)
            return false;
        var value = buffer[..HashBytes];
        if (!string.Equals(Convert.ToBase64String(value), text, StringComparison.Ordinal))
            return false;
        chain = value;
        return true;
    }

    /// <summary>
    /// 串流版的一段：邊讀邊算這一段的 SHA-256（不整段放進記憶體），<see cref="Finish"/> 回接上這一段之後的鏈雜湊。
    /// 結果與 <see cref="Next"/> 一次給整段相同。不是執行緒安全的；<see cref="Finish"/> 之後不能再 <see cref="Append"/>
    /// </summary>
    public sealed class Hasher : IDisposable
    {
        private readonly byte[] previous;
        private readonly IncrementalHash chunk = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private bool finished;

        public Hasher(ReadOnlySpan<byte> previous)
        {
            if (previous.Length != HashBytes)
                throw new ArgumentException("鏈雜湊要 32 位元組", nameof(previous));
            this.previous = previous.ToArray();
        }

        /// <summary>這一段到目前為止的位元組數</summary>
        public long Length { get; private set; }

        public void Append(ReadOnlySpan<byte> data)
        {
            ObjectDisposedException.ThrowIf(finished, this);
            chunk.AppendData(data);
            Length += data.Length;
        }

        /// <summary>接上這一段之後的鏈雜湊（只能呼叫一次）</summary>
        public byte[] Finish()
        {
            ObjectDisposedException.ThrowIf(finished, this);
            finished = true;
            return Combine(previous, chunk.GetHashAndReset());
        }

        public void Dispose()
        {
            finished = true;
            chunk.Dispose();
        }
    }
}
