using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Myriagent.Update;

namespace MyriagentTools.ReleaseTool;

/// <summary>以 System.Reflection.Metadata 讀出的組件資訊（不載入組件，也就不會執行它的任何程式碼）</summary>
internal sealed record AssemblyFacts(string Name, Version Version, string? InformationalVersion, IReadOnlyList<KeyValuePair<string, string?>> Metadata);

/// <summary>
/// 發佈前的組件檢查（規格 6.4；U2 設計 2.1 的兩道守門）：桌面組件不得帶測試金鑰的中繼資料（ReleaseKeys.TestKeyMetadata）；
/// InformationalVersion 必須正好是「version+完整提交雜湊」，而且 ReleaseIdentity.FromInformationalVersion 解出同一個非 dev 的 release；
/// 發佈資料夾裡要有 Myriagent.exe
/// </summary>
internal static class AssemblyChecks
{
    public const string DesktopAssemblyName = "Myriagent";
    public const string DesktopDll = "Myriagent.dll";

    private const string InformationalVersionAttribute = "System.Reflection.AssemblyInformationalVersionAttribute";
    private const string MetadataAttribute = "System.Reflection.AssemblyMetadataAttribute";

    /// <summary>通過回 null，否則回第一個不符的原因</summary>
    public static string? Check(string publishDir, string release, string commit)
    {
        if (!ReleaseIdentity.TryParseRelease(release, out var identity))
            return $"release 格式不對：{release}";
        if (!Versioning.IsCommit(commit) || !commit.StartsWith(identity!.Build, StringComparison.Ordinal))
            return "commit 必須是 40 個小寫十六進位字，而且以 release 的 build 開頭";
        var root = Path.GetFullPath(publishDir);
        if (!File.Exists(Path.Combine(root, ReleasePackageRules.EntryExecutable)))
            return $"發佈資料夾沒有 {ReleasePackageRules.EntryExecutable}：{root}";
        var dll = Path.Combine(root, DesktopDll);
        if (!File.Exists(dll))
            return $"發佈資料夾沒有 {DesktopDll}：{root}";
        AssemblyFacts facts;
        try
        {
            facts = Read(dll);
        }
        catch (BadImageFormatException ex)
        {
            return $"{DesktopDll} 讀不出組件中繼資料：{ex.Message}";
        }
        if (facts.Name != DesktopAssemblyName)
            return $"{DesktopDll} 的組件名稱是 {facts.Name}，不是 {DesktopAssemblyName}";
        // 比對不分大小寫：比 app 本身（ReleaseKeys.TestKeySpki 以序數比對）嚴格
        if (facts.Metadata.Any(m => string.Equals(m.Key, ReleaseKeys.TestKeyMetadata, StringComparison.OrdinalIgnoreCase)))
            return $"{DesktopDll} 帶著測試金鑰的中繼資料 {ReleaseKeys.TestKeyMetadata}：正式發佈不可用 -p:MyriagentUpdateTestKeySpki 建置";
        var expected = $"{identity.Version}+{commit}";
        if (facts.InformationalVersion != expected)
            return $"{DesktopDll} 的 InformationalVersion 是「{facts.InformationalVersion ?? "（沒有）"}」，應該是「{expected}」";
        var fromAssembly = ReleaseIdentity.FromInformationalVersion(facts.InformationalVersion, facts.Version);
        if (fromAssembly.IsDev || fromAssembly.Release != release)
            return $"{DesktopDll} 的版本解讀成 {fromAssembly.Release}，不是 {release}";
        return null;
    }

    /// <summary>讀組件名稱、版本、InformationalVersion 與所有 AssemblyMetadata；不是 .NET 組件丟 BadImageFormatException</summary>
    public static AssemblyFacts Read(string dllPath)
    {
        using var stream = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata)
            throw new BadImageFormatException("沒有 .NET 中繼資料", dllPath);
        var md = pe.GetMetadataReader();
        if (!md.IsAssembly)
            throw new BadImageFormatException("是模組，不是組件", dllPath);
        var assembly = md.GetAssemblyDefinition();
        string? informational = null;
        var metadata = new List<KeyValuePair<string, string?>>();
        foreach (var handle in assembly.GetCustomAttributes())
        {
            var attribute = md.GetCustomAttribute(handle);
            switch (AttributeTypeName(md, attribute.Constructor))
            {
                case InformationalVersionAttribute:
                    informational = StringArguments(attribute, 1)[0];
                    break;
                case MetadataAttribute:
                    var arguments = StringArguments(attribute, 2);
                    metadata.Add(new KeyValuePair<string, string?>(arguments[0] ?? string.Empty, arguments[1]));
                    break;
            }
        }
        return new AssemblyFacts(md.GetString(assembly.Name), assembly.Version, informational, metadata);
    }

    private static IReadOnlyList<string?> StringArguments(CustomAttribute attribute, int count)
    {
        var value = attribute.DecodeValue(StringTypeProvider.Instance);
        if (value.FixedArguments.Length != count || value.FixedArguments.Any(a => a.Type != nameof(PrimitiveTypeCode.String)))
            throw new BadImageFormatException($"屬性參數不是 {count} 個字串");
        return value.FixedArguments.Select(a => (string?)a.Value).ToList();
    }

    private static string AttributeTypeName(MetadataReader md, EntityHandle constructor)
    {
        switch (constructor.Kind)
        {
            case HandleKind.MemberReference:
                var parent = md.GetMemberReference((MemberReferenceHandle)constructor).Parent;
                return parent.Kind switch
                {
                    HandleKind.TypeReference => TypeName(md, md.GetTypeReference((TypeReferenceHandle)parent)),
                    HandleKind.TypeDefinition => TypeName(md, md.GetTypeDefinition((TypeDefinitionHandle)parent)),
                    _ => string.Empty,
                };
            case HandleKind.MethodDefinition:
                var declaring = md.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType();
                return TypeName(md, md.GetTypeDefinition(declaring));
            default:
                return string.Empty;
        }
    }

    private static string TypeName(MetadataReader md, TypeReference type) => md.GetString(type.Namespace) + "." + md.GetString(type.Name);

    private static string TypeName(MetadataReader md, TypeDefinition type) => md.GetString(type.Namespace) + "." + md.GetString(type.Name);

    /// <summary>只為了解出字串參數：型別一律以名稱表示；列舉參數用不到，遇到就當成格式錯</summary>
    private sealed class StringTypeProvider : ICustomAttributeTypeProvider<string>
    {
        public static readonly StringTypeProvider Instance = new();

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetSystemType() => "System.Type";

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => TypeName(reader, reader.GetTypeDefinition(handle));

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => TypeName(reader, reader.GetTypeReference(handle));

        public string GetTypeFromSerializedName(string name) => name;

        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => throw new BadImageFormatException($"不支援列舉參數：{type}");

        public bool IsSystemType(string type) => type == "System.Type";
    }
}
