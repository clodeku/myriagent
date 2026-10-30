namespace Myriagent.Update;

/// <summary>
/// 一個發佈：版本＋build（提交前 7 碼小寫，與 MyriagentHub 同規則）。release 名稱「0.2.1523-ab12cd3」是安裝資料夾、套件檔名與 manifest 的識別。
/// 本機建置沒有提交雜湊時 build 是 "dev"（開發版，不會出現在 release 名稱的合法格式裡）
/// </summary>
internal sealed record ReleaseIdentity(ReleaseVersion Version, string Build)
{
    public const string DevBuild = "dev";

    public string Release => $"{Version}-{Build}";

    public bool IsDev => Build == DevBuild;

    public static bool IsValidBuild(string? build)
        => build is { Length: 7 } && build.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>
    /// 「0.2.1523-ab12cd3」；build 必須正好 7 個小寫十六進位字。
    /// 只收標準寫法（解析後重組的 release 名稱與原字串完全相同）：前後或中間夾空白的名稱一律拒絕，
    /// 呼叫端驗證過的原字串可以直接拿來組路徑與檔名
    /// </summary>
    public static bool TryParseRelease(string? text, out ReleaseIdentity? identity)
    {
        identity = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var dash = text.LastIndexOf('-');
        if (dash <= 0 || !ReleaseVersion.TryParse(text[..dash], out var version))
            return false;
        var build = text[(dash + 1)..];
        if (!IsValidBuild(build))
            return false;
        var parsed = new ReleaseIdentity(version, build);
        if (!string.Equals(parsed.Release, text, StringComparison.Ordinal))
            return false;
        identity = parsed;
        return true;
    }

    /// <summary>
    /// 組件的 InformationalVersion（SDK 寫成「0.2.1523+完整雜湊」）→ 版本＋build（雜湊前 7 碼轉小寫）。
    /// 沒有「+」、雜湊不是十六進位或不到 7 碼時 build＝dev；版本解析不了時改用組件版本的前三段
    /// </summary>
    public static ReleaseIdentity FromInformationalVersion(string? informational, Version? assemblyVersion)
    {
        var text = informational ?? string.Empty;
        var plus = text.IndexOf('+');
        var versionText = plus >= 0 ? text[..plus] : text;
        var hash = plus >= 0 ? text[(plus + 1)..].Trim().ToLowerInvariant() : string.Empty;
        if (!ReleaseVersion.TryParse(versionText, out var version))
        {
            var v = assemblyVersion ?? new Version(0, 0, 0, 0);
            version = new ReleaseVersion(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));
        }
        var build = hash.Length >= 7 && IsValidBuild(hash[..7]) ? hash[..7] : DevBuild;
        return new ReleaseIdentity(version, build);
    }
}
