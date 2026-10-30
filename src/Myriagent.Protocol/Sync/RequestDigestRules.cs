using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Myriagent.Agents;

namespace Myriagent.Sync;

/// <summary>
/// 卡片摘要 rd1（設計 14.11 D36；docs/sync-protocol.md 14.5）：遠端回應核准卡、問卷與計畫確認卡時必須帶卡片的摘要，
/// 電腦以目前的卡片重算，不同就回 <c>stale</c>——「使用者核准的就是他看到的內容」，而且是這張卡（含只在這台記憶體裡的 RequestKey）。
/// 摘要只由擁有者電腦算，存在項目的 RequestDigest、隨 conv.item 發布。
/// 輸入：前綴 <c>rd1\n</c>，依序編碼 kind、toolName、toolKind、title、text、detail、filePath、canAllowForSession、allowSessionLabel、requestKey、
/// 差異（行數，接每行的 t、s）、題目（題數，接每題的 id、header、text、multiSelect、allowOther、isSecret、選項數，接每個選項的 label、description、preview）。
/// 每個值編成「UTF-8 位元組數」<c>:</c>「值」，null 是 <c>-:</c>；布林 true／false、列舉用名稱、數目用十進位；值之間不加分隔、不做 Unicode 正規化。
/// 輸出 <c>rd1:</c> 加 SHA-256 的 64 個小寫十六進位
/// </summary>
internal static class RequestDigestRules
{
    public const string Prefix = "rd1:";

    private const string InputPrefix = "rd1\n";

    /// <summary>卡片的摘要；RequestKey 是 null（卡片不在等待）時沒有摘要（null），遠端不能回應</summary>
    public static string? Compute(AgentChatItem item)
        => item.RequestKey == null ? null : Prefix + Convert.ToHexStringLower(SHA256.HashData(Input(item)));

    /// <summary>遠端帶來的摘要與目前的卡片相符（null 一律不符：本機的 RejectStaleCard 在值是 null 時放行，遠端不放行）</summary>
    public static bool Matches(AgentChatItem item, string? digest)
    {
        if (digest == null || Compute(item) is not { } current)
            return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(current), Encoding.UTF8.GetBytes(digest));
    }

    /// <summary>雜湊的輸入（UTF-8；固定向量測試用）</summary>
    public static byte[] Input(AgentChatItem item)
    {
        var s = new StringBuilder(InputPrefix);
        Value(s, item.Kind.ToString());
        Value(s, item.ToolName);
        Value(s, item.ToolKind);
        Value(s, item.Title);
        Value(s, item.Text);
        Value(s, item.Detail);
        Value(s, item.FilePath);
        Value(s, item.CanAllowForSession);
        Value(s, item.AllowSessionLabel);
        Value(s, item.RequestKey);

        Count(s, item.Diff?.Count);
        foreach (var line in item.Diff ?? [])
        {
            Value(s, line?.T);
            Value(s, line?.S);
        }

        Count(s, item.Questions?.Count);
        foreach (var q in item.Questions ?? [])
        {
            if (q == null)
            {
                // 存檔裡的 null 題目：佔一個位置、內容全是 null（數目照樣算進去，與「沒有這題」不同）
                for (int i = 0; i < 6; i++)
                    Value(s, (string?)null);
                Count(s, null);
                continue;
            }

            Value(s, q.Id);
            Value(s, q.Header);
            Value(s, q.Text);
            Value(s, q.MultiSelect);
            Value(s, q.AllowOther);
            Value(s, q.IsSecret);
            Count(s, q.Options?.Count);
            foreach (var option in q.Options ?? [])
            {
                Value(s, option?.Label);
                Value(s, option?.Description);
                Value(s, option?.Preview);
            }
        }

        return Encoding.UTF8.GetBytes(s.ToString());
    }

    private static void Value(StringBuilder s, string? value)
    {
        if (value == null)
        {
            s.Append("-:");
            return;
        }

        s.Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
    }

    private static void Value(StringBuilder s, bool value) => Value(s, value ? "true" : "false");

    private static void Count(StringBuilder s, int? count) => Value(s, count?.ToString(CultureInfo.InvariantCulture));
}
