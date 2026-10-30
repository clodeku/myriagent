using System.IO.Compression;
using System.Text;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Update;

namespace MyriagentHub.Sync;

/// <summary>上行事件 payload 的檢查結果：格式錯誤與太大要分開回（BatchAck 的 rejected 是 invalid 或 too_large）</summary>
internal enum PayloadCheck
{
    Ok,
    TooLarge,
    Invalid,
}

/// <summary>
/// 帳戶目錄與上行事件的純函式規則（伺服器端；sync-protocol 5.4、6.1～6.6）。裝置寫的內容一律當成不可信：
/// 長度有上限、識別碼與列舉值照格式、名稱去掉控制與格式字元（顯示一律 textContent，但名稱也會進名冊聲明與核准頁）。
/// </summary>
internal static class CatalogRules
{
    public const string Claude = "Claude";

    public const string Codex = "Codex";

    /// <summary>Grok、Antigravity：2026-10-22 起進目錄（設計 2026-10-22-grok-antigravity-sync；桌面只在協商出 prov1 時送）</summary>
    public const string Grok = "Grok";

    public const string Antigravity = "Antigravity";

    /// <summary>目錄收的代理種類數（cli.state 的 providers 最多這麼多個）</summary>
    public const int ProviderCount = 4;

    public const int MaxLabelLength = 60;

    public const int MaxEmailLength = 320;

    public const int MaxIdentityKeyLength = 400;

    public const int MaxTextLength = 500;

    public const int MaxShortTextLength = 128;

    public const int MaxSortOrder = 1_000_000;

    public const int MaxSessionCount = 100_000;

    public static bool IsProvider(string? provider) => provider is Claude or Codex or Grok or Antigravity;

    public static bool IsConfigKind(string? kind) => kind is "systemDefault" or "isolated";

    /// <summary>本機帳戶 id（桌面產生的 GUID 類字串）：1～64 個英數字、底線、連字號、句點</summary>
    public static bool IsLocalAccountId(string? id) =>
        id is { Length: > 0 and <= 64 } && id.AsSpan().IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.") < 0;

    /// <summary>
    /// 身分鍵（sync-protocol 6.1）：Claude <c>claude:{orgId}:{email}</c>（拿不到 orgId 時 <c>claude:{email}</c>）、Codex <c>codex:{email}</c>、
    /// Grok <c>grok:{email}</c>、Antigravity <c>antigravity:{email}</c>；前綴要與供應商相符、沒有空白與控制字元
    /// </summary>
    public static bool IsIdentityKey(string? key, string provider)
    {
        if (key is null || key.Length > MaxIdentityKeyLength)
            return false;
        var prefix = provider switch
        {
            Claude => "claude:",
            Codex => "codex:",
            Grok => "grok:",
            Antigravity => "antigravity:",
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

    /// <summary>狀態（web-protocol AccountView.status）：1～32 個英文字母</summary>
    public static bool IsStatus(string? status) =>
        status is { Length: > 0 and <= 32 } && status.AsSpan().IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz") < 0;

    /// <summary>帳戶代表色：#RGB 或 #RRGGBB</summary>
    public static bool IsColor(string? color) =>
        color is { Length: 4 or 7 } && color[0] == '#' && color.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    public static bool IsSortOrder(int order) => order is >= -MaxSortOrder and <= MaxSortOrder;

    /// <summary>
    /// 可省略的顯示文字：null，或不超過長度、沒有控制字元與單獨的代理字元（以 Unicode 純量值逐字檢查）。
    /// 格式字元（雙向覆寫、零寬字元、BOM）不在這裡拒絕：整筆被擋的裝置事件不會重送，Windows 使用者名稱帶組合 emoji 的電腦
    /// 也會永遠配對不了——改成存進目錄時以 <see cref="CleanText"/> 去掉
    /// </summary>
    public static bool IsOptionalText(string? text, int maxLength)
    {
        if (text is null)
            return true;
        if (text.Length > maxLength)
            return false;
        var rest = text.AsSpan();
        while (!rest.IsEmpty)
        {
            if (System.Text.Rune.DecodeFromUtf16(rest, out var rune, out var consumed) != System.Buffers.OperationStatus.Done || System.Text.Rune.IsControl(rune))
                return false;
            rest = rest[consumed..];
        }
        return true;
    }

    /// <summary>
    /// 存進目錄、會出現在核准頁與儀表板的文字：去掉格式字元（雙向覆寫能讓畫面上的內容與實際的不同）、行段分隔字元，去頭尾空白；
    /// 清理後是空的回 null（<see cref="LabelRules.Clean"/>，長度已由 <see cref="IsOptionalText"/> 檢查過）
    /// </summary>
    public static string? CleanText(string? text, int maxLength) => text is null ? null : LabelRules.Clean(text, maxLength);

    public static string? CleanLabel(string? label) => LabelRules.Clean(label, MaxLabelLength);

    public static string? ValidateLink(LinkUpsertPayload? p)
    {
        if (p is null)
            return "payload 不是物件";
        if (!IsLocalAccountId(p.LocalAccountId))
            return "localAccountId 不正確";
        if (!IsProvider(p.Provider))
            return "provider 不正確";
        if (!IsConfigKind(p.ConfigKind))
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
        if (p.Sessions is null || !IsCount(p.Sessions.Open) || !IsCount(p.Sessions.Running) || !IsCount(p.Sessions.Waiting) || !IsCount(p.Sessions.Background)
            || p.Sessions.Programs is { } programs && !IsCount(programs) || p.Sessions.Subagents is { } subagents && !IsCount(subagents))
            return "sessions 不正確";
        if (CleanLabel(p.Label) is null || !IsColor(p.Color) || !IsSortOrder(p.SortOrder))
            return "名稱、顏色或順序不正確";
        return null;
    }

    public static string? ValidateCli(CliStatePayload? p)
    {
        if (p?.Providers is null || p.Providers.Count > ProviderCount)
            return "providers 不正確";
        foreach (var (provider, view) in p.Providers)
        {
            if (!IsProvider(provider) || view is null)
                return "providers 不正確";
            if (!IsOptionalText(view.Version, 64) || !IsOptionalText(view.InstallKind, 32) || !IsOptionalText(view.Latest, 64)
                || !IsOptionalText(view.InstallBroken, MaxTextLength) || !IsCount(view.RunningOnOldVersion) || !IsCount(view.RestartPending))
                return "CLI 狀態的欄位不正確";
        }
        return null;
    }

    /// <summary>
    /// <c>app.state</c>（app 自我更新規格 7.8，sync-protocol 6.6）：version 是標準寫法的三段版本（<see cref="ReleaseVersion"/> 解析後重組與原字串相同，
    /// 同 <see cref="ReleaseIdentity.TryParseRelease"/> 只收標準寫法）；release 與 staged 是 null 或合法的 release 名稱（開發版沒有 release 名稱，送 null）；
    /// phase 是 <see cref="AppStatePhases"/> 的值；lastError 最多 <see cref="MaxTextLength"/> 字、不含控制字元（換行也算）；checkedAt 大於 0。
    /// 通過回 null，否則回原因。格式字元不在這裡拒絕（被擋的裝置事件不會重送），存進目錄時以 <see cref="CleanText"/> 去掉
    /// </summary>
    public static string? ValidateApp(AppStatePayload? payload)
    {
        if (payload is null)
            return "payload 不是物件";
        if (!ReleaseVersion.TryParse(payload.Version, out var version) || version.ToString() != payload.Version)
            return "version 不正確";
        if ((payload.Release is not null && !ReleaseIdentity.TryParseRelease(payload.Release, out _))
            || (payload.Staged is not null && !ReleaseIdentity.TryParseRelease(payload.Staged, out _)))
            return "release 或 staged 不正確";
        if (!AppStatePhases.IsKnown(payload.Phase))
            return "phase 不正確";
        if (!IsOptionalText(payload.LastError, MaxTextLength))
            return "lastError 太長或含控制字元";
        if (payload.CheckedAt <= 0)
            return "checkedAt 不正確";
        return null;
    }

    /// <summary>
    /// 還原 payload 原文並檢查大小（sync-protocol 5.4）：<c>json</c> 是原文、<c>gzip</c> 是 gzip 的標準 base64；解壓後超過 <paramref name="maxJsonBytes"/>
    /// 是 <see cref="PayloadCheck.TooLarge"/>（讀到超過就停，不會被 gzip 炸彈拖垮），其他格式錯誤（含不合法的 UTF-8）是 <see cref="PayloadCheck.Invalid"/>
    /// </summary>
    public static PayloadCheck CheckPayload(string? enc, string? payload, int maxJsonBytes, out string json)
    {
        json = string.Empty;
        if (payload is null)
            return PayloadCheck.Invalid;
        if (enc == PayloadRules.EncJson)
        {
            if (Encoding.UTF8.GetByteCount(payload) > maxJsonBytes)
                return PayloadCheck.TooLarge;
            json = payload;
            return PayloadCheck.Ok;
        }
        if (enc != PayloadRules.EncGzip)
            return PayloadCheck.Invalid;
        try
        {
            using var input = new MemoryStream(Convert.FromBase64String(payload));
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = gzip.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (output.Length + read > maxJsonBytes)
                    return PayloadCheck.TooLarge;
                output.Write(chunk, 0, read);
            }
            json = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(output.GetBuffer(), 0, (int)output.Length);
            return PayloadCheck.Ok;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or ArgumentException)
        {
            return PayloadCheck.Invalid;
        }
    }

    private static bool IsCount(int value) => value is >= 0 and <= MaxSessionCount;
}
