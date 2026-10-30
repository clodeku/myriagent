using System.Reflection;
using Myriagent.Update;

namespace MyriagentHub.Hosting;

/// <summary>
/// 版本與建置指紋。<c>/healthz</c> 只回 build（私有 repo 的提交雜湊前 7 碼，不對應任何公開產品或相依套件版本）；
/// 詳細版本只在已認證的 /readyz、ops/status 與本機的 status\running.json。部署的 manifest、安裝腳本的健康閘門
/// 與 workflow 的外部驗證都比對同一個 7 碼（docs/hub-setup.md 第 11 節）。
/// </summary>
internal sealed record BuildInfo(string Version, string Build, string InformationalVersion)
{
    /// <summary>workflow 發佈時以 <c>-p:MyriagentHubBuild=&lt;7 碼&gt;</c> 帶入的組件中繼資料鍵（MyriagentHub.csproj）</summary>
    public const string MetadataKey = "MyriagentHubBuild";

    public static BuildInfo Current { get; } = FromAssembly(typeof(BuildInfo).Assembly);

    /// <summary>這個 build 的提交時間（瀏覽器版在版號前面顯示的日期時間，init.app.commitAt）；建置時沒有 git 是 null</summary>
    public DateTimeOffset? CommitTime { get; init; }

    /// <summary>給網頁的 commitAt：Unix 毫秒或 null</summary>
    public long? CommitAtMs => CommitTimeRules.ToUnixMs(CommitTime);

    public static BuildInfo FromAssembly(Assembly assembly) =>
        BuildInfoRules.Parse(
            assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == MetadataKey)?.Value,
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            assembly.GetName().Version) with { CommitTime = CommitTimeRules.FromAssembly(assembly) };
}

internal static class BuildInfoRules
{
    public const int BuildLength = 7;

    /// <summary>
    /// build：優先用 MyriagentHubBuild 組件中繼資料（workflow 傳的提交短雜湊）；沒有（或不是提交雜湊）時，
    /// 取 SDK 附在 InformationalVersion「+」後面的 SourceRevisionId（0.2.0+完整雜湊）；兩者都取前 7 碼、轉小寫。
    /// 都沒有時 build＝dev。版本取 InformationalVersion「+」前面的部分（沒有就用組件版本）。
    /// </summary>
    public static BuildInfo Parse(string? myriagentHubBuild, string? informationalVersion, Version? assemblyVersion)
    {
        var info = informationalVersion?.Trim() ?? string.Empty;
        var plus = info.IndexOf('+');
        var version = plus >= 0 ? info[..plus] : info;
        if (version.Length == 0)
            version = assemblyVersion is null ? "0.0.0" : assemblyVersion.ToString(3);
        var build = ShortHash(myriagentHubBuild?.Trim())
            ?? (plus >= 0 ? ShortHash(new string(info[(plus + 1)..].TakeWhile(Uri.IsHexDigit).ToArray())) : null)
            ?? "dev";
        return new BuildInfo(version, build, info.Length == 0 ? version : info);
    }

    /// <summary>7～40 個十六進位字元才算提交雜湊：取前 7 碼、轉小寫；其他回 null</summary>
    private static string? ShortHash(string? value) =>
        value is { Length: >= BuildLength and <= 40 } && value.All(Uri.IsHexDigit)
            ? value[..BuildLength].ToLowerInvariant()
            : null;
}
