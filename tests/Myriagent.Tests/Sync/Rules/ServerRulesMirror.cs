using System.Buffers;
using System.Text;
using Myriagent.Security;
using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Rules;

/// <summary>
/// 伺服器端檢查的鏡像：桌面測試參考不了 MyriagentHub（net10.0 的 Web SDK、internal 只開給 MyriagentHub.Tests），
/// 所以把 src/MyriagentHub/Sync/CatalogRules.cs 的 ValidateLink 與 src/MyriagentHub/Sync/UsageRules.cs 的 Validate 逐條抄過來，
/// 常數一字不改。伺服器改了規則時這裡要跟著改（被判 invalid 的事件會佔掉 deviceSeq、永不重送）。
/// <c>prov1</c> 為 false 是宣告 prov1 之前（2026-10-22 以前、或部署回滾）的伺服器：目錄與 cli.state 只收 Claude、Codex。
/// </summary>
internal static class ServerRulesMirror
{
    public const int MaxLabelLength = 60;
    public const int MaxEmailLength = 320;
    public const int MaxIdentityKeyLength = 400;
    public const int MaxTextLength = 500;
    public const int MaxShortTextLength = 128;
    public const int MaxSortOrder = 1_000_000;
    public const int MaxSessionCount = 100_000;
    public const int MaxWindows = 8;
    public const int MaxWindowKeyLength = 32;
    public const double MaxPercent = 1000;

    public static string? ValidateLink(LinkUpsertPayload? p, bool prov1 = true)
    {
        if (p is null)
            return "payload 不是物件";
        if (!IsLocalAccountId(p.LocalAccountId))
            return "localAccountId 不正確";
        if (!IsProvider(p.Provider, prov1))
            return "provider 不正確";
        if (p.ConfigKind is not ("systemDefault" or "isolated"))
            return "configKind 不正確";
        if (p.IdentityKey is not null && !IsIdentityKey(p.IdentityKey, p.Provider))
            return "identityKey 不正確";
        if (p.LogicalAccountId is not null && !SyncIds.IsLogicalAccountId(p.LogicalAccountId))
            return "logicalAccountId 不正確";
        if (!IsStatus(p.Status))
            return "status 不正確";
        if (!IsOptionalText(p.StatusText, MaxTextLength) || !IsOptionalText(p.Email, MaxEmailLength)
            || !IsOptionalText(p.OrgId, MaxShortTextLength) || !IsOptionalText(p.Plan, MaxShortTextLength))
            return "文字欄位太長或含控制字元";
        if (p.Sessions is null || !IsCount(p.Sessions.Open) || !IsCount(p.Sessions.Running) || !IsCount(p.Sessions.Waiting))
            return "sessions 不正確";
        if (LabelRules.Clean(p.Label, MaxLabelLength) is null || !IsColor(p.Color) || p.SortOrder is < -MaxSortOrder or > MaxSortOrder)
            return "名稱、顏色或順序不正確";
        return null;
    }

    public static string? ValidateCli(CliStatePayload? p, bool prov1 = true)
    {
        if (p?.Providers is null || p.Providers.Count > (prov1 ? 4 : 2))
            return "providers 不正確";
        foreach (var (provider, view) in p.Providers)
        {
            if (!IsProvider(provider, prov1) || view is null)
                return "providers 不正確";
            if (!IsOptionalText(view.Version, 64) || !IsOptionalText(view.InstallKind, 32) || !IsOptionalText(view.Latest, 64)
                || !IsOptionalText(view.InstallBroken, MaxTextLength) || !IsCount(view.RunningOnOldVersion) || !IsCount(view.RestartPending))
                return "CLI 狀態的欄位不正確";
        }

        return null;
    }

    public static string? ValidateUsage(UsagePublish? publish)
    {
        if (publish is null || !SyncIds.IsLogicalAccountId(publish.LogicalAccountId) || publish.Usage is not { } usage)
            return "格式不正確";
        if (publish.Term is { } term && term < 1)
            return "term 不正確";
        if (usage.Source != (publish.Term is null ? "session" : "api"))
            return "source 與 term 不相符";
        if (usage.FetchedAt <= 0 || usage.Windows is null || usage.Windows.Count > MaxWindows)
            return "時間窗不正確";
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in usage.Windows)
        {
            if (w is null || w.Key is not { Length: > 0 and <= MaxWindowKeyLength } key
                || key.AsSpan().IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_") >= 0 || !keys.Add(key))
                return "時間窗的鍵不正確";
            if (LabelRules.Clean(w.Label, MaxLabelLength) != w.Label)
                return "時間窗的名稱不正確";
            if (!double.IsFinite(w.Percent) || w.Percent < 0 || w.Percent > MaxPercent || w.ResetsAt is <= 0)
                return "時間窗的數值不正確";
        }

        if (!IsOptionalText(usage.Credits, MaxShortTextLength) || !IsOptionalText(usage.Note, MaxTextLength) || !IsOptionalText(usage.Error, MaxTextLength))
            return "文字欄位太長或含控制字元";
        return null;
    }

    private static bool IsLocalAccountId(string? id) =>
        id is { Length: > 0 and <= 64 } && id.AsSpan().IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.") < 0;

    private static bool IsProvider(string? provider, bool prov1) =>
        provider is "Claude" or "Codex" || (prov1 && provider is "Grok" or "Antigravity");

    private static bool IsIdentityKey(string? key, string provider)
    {
        if (key is null || key.Length > MaxIdentityKeyLength)
            return false;
        var prefix = provider switch
        {
            "Claude" => "claude:",
            "Codex" => "codex:",
            "Grok" => "grok:",
            "Antigravity" => "antigravity:",
            _ => null,
        };
        if (prefix is null || !key.StartsWith(prefix, StringComparison.Ordinal) || key.Length == prefix.Length)
            return false;
        foreach (var c in key)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
                return false;
        }

        return true;
    }

    private static bool IsStatus(string? status) =>
        status is { Length: > 0 and <= 32 } && status.AsSpan().IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz") < 0;

    private static bool IsColor(string? color) =>
        color is { Length: 4 or 7 } && color[0] == '#' && color.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    private static bool IsOptionalText(string? text, int maxLength)
    {
        if (text is null)
            return true;
        if (text.Length > maxLength)
            return false;
        var rest = text.AsSpan();
        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(rest, out var rune, out var consumed) != OperationStatus.Done || Rune.IsControl(rune))
                return false;
            rest = rest[consumed..];
        }

        return true;
    }

    private static bool IsCount(int value) => value is >= 0 and <= MaxSessionCount;
}
