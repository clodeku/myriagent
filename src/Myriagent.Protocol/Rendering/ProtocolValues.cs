using Myriagent.Agents;

namespace Myriagent.Rendering;

/// <summary>
/// 畫面協定（docs/web-protocol.md）裡的值對照：列舉的協定字串、時間的 Unix 毫秒。
/// 桌面中樞與伺服器共用，兩邊送給網頁的值才會一致；桌面原本的函式（CliRestartRules.ToProtocol、AgentHub.ToUnixMs）改成轉呼叫這裡
/// </summary>
internal static class ProtocolValues
{
    /// <summary>協定的 Item.noticeKind（小駝峰）</summary>
    public static string ToProtocol(AgentNoticeKind kind) => Camel(kind.ToString());

    /// <summary>協定的 Item.source（小駝峰）</summary>
    public static string ToProtocol(AgentItemSource source) => Camel(source.ToString());

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    /// <summary>本機時間 → Unix 毫秒；DateTime.MinValue 這類轉換不了的值（例如壞檔裡的 0001-01-01）回 null，不丟例外</summary>
    public static long? ToUnixMs(DateTime? value)
    {
        if (value is not { } time || time.Year < 1971 || time.Year > 9000)
            return null;
        try
        {
            return new DateTimeOffset(time).ToUnixTimeMilliseconds();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
