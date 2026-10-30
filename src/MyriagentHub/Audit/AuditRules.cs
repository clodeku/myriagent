using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MyriagentHub.Audit;

/// <summary>稽核列中參與雜湊的內容（不含 seq：順序由 prev_hash 的鏈結保證，刪列、改列、調換順序都會斷鏈）</summary>
internal readonly record struct AuditContent(
    long At,
    string ActorType,
    string? ActorId,
    string? Ip,
    string Action,
    string? Target,
    string Outcome,
    string? Detail);

/// <summary>驗證時讀到的一列</summary>
internal readonly record struct AuditChainRow(long Seq, AuditContent Content, byte[] PrevHash, byte[] Hash);

/// <summary>雜湊鏈驗證結果</summary>
internal sealed record AuditVerification(bool Ok, long Checked, long LastSeq, long? BrokenAtSeq, string? Reason, long VerifiedAt);

/// <summary>
/// 稽核雜湊鏈（8.14、SEC-17）：hash = SHA-256(prev_hash ‖ 正規化列內容)。
/// 正規化：版本位元組 0x01、prev_hash（32 位元組）、at（8 位元組 big-endian）、
/// 其餘欄位依序以「4 位元組 big-endian 長度＋UTF-8」寫入，null 寫成長度 0xFFFFFFFF——欄位邊界明確，沒有拼接歧義。
/// </summary>
internal static class AuditRules
{
    public const int HashLength = 32;
    private const byte FormatVersion = 1;

    /// <summary>第一列的 prev_hash（32 個 0）；每次回傳新陣列，呼叫端改了也不影響別人</summary>
    public static byte[] GenesisHash() => new byte[HashLength];

    public static byte[] ComputeHash(byte[] prevHash, AuditContent content)
    {
        if (prevHash.Length != HashLength)
            throw new ArgumentException("prev_hash 必須是 32 位元組", nameof(prevHash));
        using var buffer = new MemoryStream(256);
        buffer.WriteByte(FormatVersion);
        buffer.Write(prevHash);
        Span<byte> at = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(at, content.At);
        buffer.Write(at);
        WriteField(buffer, content.ActorType);
        WriteField(buffer, content.ActorId);
        WriteField(buffer, content.Ip);
        WriteField(buffer, content.Action);
        WriteField(buffer, content.Target);
        WriteField(buffer, content.Outcome);
        WriteField(buffer, content.Detail);
        return SHA256.HashData(buffer.ToArray());
    }

    /// <summary>
    /// 依 seq 遞增驗證整條鏈。<paramref name="anchorPrevHash"/>：保留期清除舊列後，第一列應有的 prev_hash（沒有清除過就是 genesis）。
    /// 回傳第一個斷掉的 seq 與原因。
    /// </summary>
    public static AuditVerification Verify(IEnumerable<AuditChainRow> rowsInSeqOrder, byte[] anchorPrevHash, long verifiedAt)
    {
        var expectedPrev = anchorPrevHash;
        long checkedCount = 0;
        long lastSeq = 0;
        foreach (var row in rowsInSeqOrder)
        {
            if (row.Seq <= lastSeq)
                return new AuditVerification(false, checkedCount, lastSeq, row.Seq, "seq 沒有遞增", verifiedAt);
            if (!CryptographicOperations.FixedTimeEquals(row.PrevHash, expectedPrev))
                return new AuditVerification(false, checkedCount, lastSeq, row.Seq, checkedCount == 0 ? "第一列的 prev_hash 與錨點不符（開頭的列被刪除或錨點被改）" : "prev_hash 與上一列的 hash 不符（中間的列被刪除、插入或調換）", verifiedAt);
            var actual = ComputeHash(row.PrevHash, row.Content);
            if (!CryptographicOperations.FixedTimeEquals(row.Hash, actual))
                return new AuditVerification(false, checkedCount, lastSeq, row.Seq, "內容與 hash 不符（列被修改）", verifiedAt);
            expectedPrev = row.Hash;
            lastSeq = row.Seq;
            checkedCount++;
        }
        return new AuditVerification(true, checkedCount, lastSeq, null, null, verifiedAt);
    }

    private static void WriteField(Stream buffer, string? value)
    {
        Span<byte> length = stackalloc byte[4];
        if (value is null)
        {
            BinaryPrimitives.WriteUInt32BigEndian(length, uint.MaxValue);
            buffer.Write(length);
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(value);
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)bytes.Length);
        buffer.Write(length);
        buffer.Write(bytes);
    }
}
