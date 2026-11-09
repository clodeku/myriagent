using System.Buffers;
using System.Globalization;
using System.Text;
using Myriagent.Security;
using Myriagent.Sync;
using MyriagentHub.Realtime;

namespace MyriagentHub.Sync;

/// <summary>選連線用的快照：連線 id、先後、已證明、協商出 <c>peer1</c></summary>
internal sealed record PeerConnectionFact(string ConnectionId, long Order, bool IsProven, bool AllowsPeer);

/// <summary>
/// 桌面遠端操作的伺服器規則（sync-protocol 14.7；擁有者 2026-10-22 決定「已配對就能操作」）
/// </summary>
internal static class PeerRules
{
    /// <summary>電腦名稱是空的（或整個去掉之後是空的）時信封的標籤</summary>
    public const string UnnamedDevice = "另一台電腦";

    /// <summary>目標是發起的電腦自己時（自己的對話與帳戶在本機操作）</summary>
    public const string SelfTargetMessage = "這是這台電腦自己的對話或帳戶，請直接在這台操作";

    /// <summary>
    /// 裝置發起的信封標籤：電腦名稱去掉 Cc、Cf、Zl、Zp 類的字元與不成對的代理字元、去掉前後空白、截成
    /// <see cref="CommandEnvelope.MaxIssuerLabelLength"/> 個 Unicode 字元；一定通過 <see cref="CommandEnvelope.IsValidIssuerLabel"/>，空的是 <see cref="UnnamedDevice"/>
    /// </summary>
    public static string DeviceLabel(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return UnnamedDevice;
        var builder = new StringBuilder(name.Length);
        var rest = name.AsSpan();
        while (!rest.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(rest, out var rune, out var used);
            rest = rest[Math.Max(used, 1)..];
            if (status != OperationStatus.Done)
                continue;
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                continue;
            builder.Append(rune.ToString());
        }

        var trimmed = builder.ToString().Trim();
        var label = new StringBuilder();
        var count = 0;
        foreach (var rune in trimmed.EnumerateRunes())
        {
            if (++count > CommandEnvelope.MaxIssuerLabelLength)
                break;
            label.Append(rune.ToString());
        }

        var result = label.ToString().TrimEnd();
        return result.Length == 0 || !CommandEnvelope.IsValidIssuerLabel(result) ? UnnamedDevice : result;
    }

    /// <summary>
    /// <c>PeerReply</c> 送哪條連線：已證明、協商出 <c>peer1</c> 的連線裡 <c>Order</c> 最大的（最後完成 Prove 的）；沒有回 null
    /// </summary>
    public static string? ReplyConnection(IEnumerable<PeerConnectionFact> connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        return connections.Where(c => c.IsProven && c.AllowsPeer).OrderByDescending(c => c.Order).FirstOrDefault()?.ConnectionId;
    }

    /// <summary><see cref="ReplyConnection(IEnumerable{PeerConnectionFact})"/> 的裝置連線版</summary>
    public static string? ReplyConnection(IReadOnlyList<DeviceConnection> connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        return ReplyConnection(connections.Select(c => new PeerConnectionFact(c.ConnectionId, c.Order, c.IsProven, c.Allows(DeviceHubMethods.PeerReply))));
    }

    /// <summary>目標電腦選到的連線沒有協商出 <c>peer1</c>（舊版桌面不認得 <c>issuer.kind = device</c>）時的說明</summary>
    public static string TargetTooOldMessage(string? deviceName)
        => "「" + (string.IsNullOrWhiteSpace(deviceName) ? UnnamedDevice : deviceName.Trim()) + "」的 Clode Myriagent 版本太舊，還不能接受其他電腦的操作；請先更新那台，或改用網頁版";

    /// <summary>序列化後放得進一則呼叫（≤1 MB − 4 KB，sync-protocol 1.1）</summary>
    public static bool Fits(PeerReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        return SyncProtocol.FitsInvocation(Encoding.UTF8.GetByteCount(SyncJson.Serialize(reply)));
    }
}

/// <summary>遠端執行指令的伺服器規則（docs/sync-protocol.md 14.11）</summary>
internal static class RemoteExecServerRules
{
    /// <summary>目標電腦選到的連線沒有協商出 <c>xfer1</c>（舊版桌面）時的說明（電腦狀態面板的檔案傳輸）</summary>
    public static string TransferTooOldMessage(string? deviceName)
        => "「" + (string.IsNullOrWhiteSpace(deviceName) ? PeerRules.UnnamedDevice : deviceName.Trim()) + "」的 Clode Myriagent 版本太舊，還不能傳檔案；請先更新那台";

    /// <summary>目標電腦選到的連線沒有協商出 <c>exec1</c>（舊版桌面）時的說明</summary>
    public static string TargetTooOldMessage(string? deviceName)
        => "「" + (string.IsNullOrWhiteSpace(deviceName) ? PeerRules.UnnamedDevice : deviceName.Trim()) + "」的 Clode Myriagent 版本太舊，還不能執行遠端指令；請先更新那台";
}
