using System.Diagnostics.CodeAnalysis;
using System.Xml;
using System.Xml.Linq;
using Myriagent.Update;

namespace MyriagentTools.ReleaseTool;

/// <summary>算出的發佈版本：Version＝主.次.N、Build＝提交前 7 碼、Release＝「version-build」</summary>
internal sealed record ComputedVersion(ReleaseVersion Version, string Build, string Release);

/// <summary>
/// 版本規則（規格 6.1、§16-2）：csproj 只維護「主.次」（&lt;Version&gt; 寫成「主.次.0」），發佈時第三段是 main 上的提交數 N，
/// build 是完整提交雜湊的前 7 碼。組件版本每段最多 65534，超過就在這裡拒絕，不等到 dotnet publish 才失敗
/// </summary>
internal static class Versioning
{
    public const int MaxComponent = 65534;

    /// <summary>40 個小寫十六進位字（GITHUB_SHA 的格式）</summary>
    public static bool IsCommit([NotNullWhen(true)] string? text)
        => text is { Length: 40 } && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>讀 csproj 裡唯一的 &lt;Version&gt;：必須是標準寫法的「主.次.0」。通過回 null，否則回原因</summary>
    public static string? ReadCsprojVersion(string csprojPath, out ReleaseVersion baseVersion)
    {
        baseVersion = default;
        XDocument document;
        try
        {
            document = XDocument.Load(csprojPath);
        }
        catch (XmlException ex)
        {
            return $"csproj 不是合法的 XML：{ex.Message}";
        }
        var elements = document.Descendants().Where(e => e.Name.LocalName == "Version").ToList();
        if (elements.Count != 1)
            return $"csproj 必須正好有一個 <Version>（找到 {elements.Count} 個）";
        var text = elements[0].Value.Trim();
        if (!ReleaseVersion.TryParse(text, out var version) || version.ToString() != text || version.Patch != 0)
            return $"csproj 的 <Version> 必須寫成「主.次.0」：{text}";
        if (version.Major > MaxComponent || version.Minor > MaxComponent)
            return $"主、次版本每段最多 {MaxComponent}：{text}";
        baseVersion = version;
        return null;
    }

    /// <summary>主.次＋提交數 N＋完整提交雜湊 → version、build、release（release 要通過 TryParseRelease）。通過回 null，否則回原因</summary>
    public static string? Compute(ReleaseVersion baseVersion, int count, string commit, out ComputedVersion? computed)
    {
        computed = null;
        if (count is < 1 or > MaxComponent)
            return $"提交數 N 必須在 1～{MaxComponent}（組件版本每段的上限）：{count}";
        if (!IsCommit(commit))
            return "commit 必須是 40 個小寫十六進位字";
        var version = new ReleaseVersion(baseVersion.Major, baseVersion.Minor, count);
        var release = $"{version}-{commit[..7]}";
        if (!ReleaseIdentity.TryParseRelease(release, out var identity))
            return $"release 格式不對：{release}";
        computed = new ComputedVersion(identity!.Version, identity.Build, identity.Release);
        return null;
    }
}
