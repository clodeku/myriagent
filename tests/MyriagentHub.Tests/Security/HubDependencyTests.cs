using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyriagentHub.Tests.Security;

/// <summary>
/// SEC-15：服務組件不含 SQL Server 用戶端、不含啟動程序的程式碼（伺服器從不啟動 CLI 或任何程序）；
/// 部署腳本不含 sqlcmd／Invoke-Sqlcmd／icacls … /deny。以中繼資料掃描組件，不載入、不執行。
/// </summary>
public sealed partial class HubDependencyTests
{
    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "Microsoft.Data.SqlClient",
        "System.Data.SqlClient",
        "Microsoft.EntityFrameworkCore.SqlServer",
        "Microsoft.SqlServer",
        "Microsoft.EntityFrameworkCore.Design",
        "Microsoft.CodeAnalysis",
        "Microsoft.Build",
        "Myriagent",
    ];

    /// <summary>桌面與伺服器共用的協定組件（第 3 階段起）：Myriagent 開頭的組件只放行這一個，它本身也一併掃描</summary>
    private const string ProtocolAssembly = "Myriagent.Protocol";

    [Fact]
    public void 伺服器的相依套件沒有SQL_Server用戶端_設計工具_桌面程式()
    {
        var deps = HubDepsJson();
        using var doc = JsonDocument.Parse(File.ReadAllText(deps));
        var libraries = doc.RootElement.GetProperty("libraries").EnumerateObject().Select(p => p.Name.Split('/')[0]).ToList();
        libraries.Should().Contain("Microsoft.EntityFrameworkCore.Sqlite", "確認讀到的是伺服器的相依清單");
        libraries.Should().Contain(ProtocolAssembly);
        // 伺服器自己（MyriagentHub）也在清單裡，和桌面組件 Myriagent 同一個前綴
        foreach (var library in libraries.Where(l => l != ProtocolAssembly && l != "MyriagentHub"))
            ForbiddenAssemblyPrefixes.Should().NotContain(prefix => library.StartsWith(prefix, StringComparison.OrdinalIgnoreCase), $"相依清單出現 {library}");
    }

    [Fact]
    public void 伺服器組件參考的組件沒有SQL_Server用戶端()
    {
        // 協定組件也是起點：伺服器還沒用到它的型別時，編譯器不會在 MyriagentHub.dll 留下參考
        var closure = ReferenceClosure(HubOutputAssembly("MyriagentHub"), HubOutputAssembly(ProtocolAssembly));
        closure.Should().Contain("Certes");
        closure.Should().Contain("Markdig", "確認協定組件的參考也掃到了");
        foreach (var name in closure.Where(n => n != ProtocolAssembly))
            ForbiddenAssemblyPrefixes.Should().NotContain(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase), $"參考到 {name}");
    }

    [Theory]
    [InlineData("MyriagentHub")]
    [InlineData(ProtocolAssembly)]
    public void 伺服器組件沒有任何啟動程序的程式碼(string assembly)
    {
        using var stream = File.OpenRead(HubOutputAssembly(assembly));
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();

        var typeRefs = md.TypeReferences.Select(h => md.GetTypeReference(h))
            .Select(t => md.GetString(t.Namespace) + "." + md.GetString(t.Name))
            .ToHashSet();
        typeRefs.Should().NotContain("System.Diagnostics.ProcessStartInfo");

        var processStart = md.MemberReferences.Select(h => md.GetMemberReference(h))
            .Where(m => m.Parent.Kind == HandleKind.TypeReference)
            .Select(m => (Type: TypeName(md, (TypeReferenceHandle)m.Parent), Name: md.GetString(m.Name)))
            .Where(m => m.Type is "System.Diagnostics.Process" && m.Name is "Start" or "set_StartInfo" or "get_StartInfo")
            .ToList();
        processStart.Should().BeEmpty("伺服器不得啟動任何程序（Process.Start／StartInfo）");

        var forbiddenImports = new[] { "CreateProcess", "ShellExecute", "WinExec", "CreateProcessAsUser", "CreateProcessWithLogon", "CreateProcessWithToken" };
        foreach (var handle in md.MethodDefinitions)
        {
            var method = md.GetMethodDefinition(handle);
            var import = method.GetImport();
            if (import.Module.IsNil)
                continue;
            var entry = import.Name.IsNil ? md.GetString(method.Name) : md.GetString(import.Name);
            forbiddenImports.Should().NotContain(f => entry.StartsWith(f, StringComparison.OrdinalIgnoreCase), $"P/Invoke {entry}");
        }

        var types = md.TypeDefinitions.Select(h => md.GetString(md.GetTypeDefinition(h).Name)).ToHashSet();
        types.Should().NotContain(new[] { "AgentProcess", "CliRunner", "CliManager", "ChildProcessJob" }, "桌面的 CLI 執行程式碼不能進伺服器");
    }

    [Fact]
    public void 部署與Hub腳本不碰SQL_Server也不加Deny_ACE()
    {
        var root = RepoRoot();
        // tools/release（送件腳本、產生金鑰的包裝）與 tools/release-tool（myriagent-release）一定要在：改名或搬家時掃描範圍不能靜默變小
        var required = new[]
        {
            Path.Combine(root, "tools", "deploy"), Path.Combine(root, "tools", "hub"),
            Path.Combine(root, "tools", "release"), Path.Combine(root, "tools", "release-tool"),
        };
        foreach (var dir in required)
            Directory.Exists(dir).Should().BeTrue($"{Path.GetRelativePath(root, dir)} 要在掃描範圍裡");
        var scanned = new List<string>();
        foreach (var file in required.Append(Path.Combine(root, ".github")).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)))
        {
            var relative = Path.GetRelativePath(root, file);
            // 建置輸出（tools/release-tool 的 bin、obj）不是原始碼
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
                continue;
            if (!Regex.IsMatch(Path.GetExtension(file), @"^\.(ps1|psm1|psd1|cmd|bat|sh|yml|yaml|json|cs|csproj)$", RegexOptions.IgnoreCase))
                continue;
            scanned.Add(relative.Replace('\\', '/'));
            var text = File.ReadAllText(file);
            ForbiddenScript().Matches(text).Select(m => m.Value).Should().BeEmpty($"{relative} 不可連 SQL Server 或加 Deny ACE");
        }
        scanned.Should().Contain(new[]
        {
            "tools/release/Publish-DesktopRelease.ps1", "tools/release/New-DesktopRelease.ps1", "tools/hub/Setup-MyriagentHub.ps1",
            "tools/release-tool/Signer.cs", "tools/release-tool/ReleaseTool.csproj",
        }, "新加的兩個目錄與 .cs、.csproj 真的有掃到");
    }

    [GeneratedRegex(@"\bsqlcmd\b|Invoke-Sqlcmd|icacls[^\r\n]*/deny|SqlConnection|Invoke-DbaQuery", RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenScript();

    private static string TypeName(MetadataReader md, TypeReferenceHandle handle)
    {
        var t = md.GetTypeReference(handle);
        return md.GetString(t.Namespace) + "." + md.GetString(t.Name);
    }

    /// <summary>伺服器組件旁（測試輸出資料夾）的組件路徑</summary>
    private static string HubOutputAssembly(string name) =>
        Path.Combine(Path.GetDirectoryName(typeof(MyriagentHub.Hosting.HubApplication).Assembly.Location)!, name + ".dll");

    /// <summary>從起點組件出發、只沿著輸出資料夾裡的組件（套件與專案）往下找，回傳所有被參考的組件名稱</summary>
    private static HashSet<string> ReferenceClosure(params string[] starts)
    {
        var dir = Path.GetDirectoryName(starts[0])!;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(starts);
        while (queue.Count > 0)
        {
            using var stream = File.OpenRead(queue.Dequeue());
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                continue;
            var md = pe.GetMetadataReader();
            foreach (var handle in md.AssemblyReferences)
            {
                var name = md.GetString(md.GetAssemblyReference(handle).Name);
                if (!seen.Add(name))
                    continue;
                var local = Path.Combine(dir, name + ".dll");
                if (File.Exists(local))
                    queue.Enqueue(local);
            }
        }
        return seen;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Myriagent.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("找不到 repo 根目錄（Myriagent.slnx）");
    }

    /// <summary>伺服器自己的 deps.json（測試輸出資料夾的是測試專案的，含 EF 設計工具，不能拿來判斷）</summary>
    private static string HubDepsJson()
    {
        // tests/MyriagentHub.Tests/bin/<組態>/net10.0/win-x64/ → src/MyriagentHub/bin/<組態>/net10.0/win-x64/MyriagentHub.deps.json
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var configuration = testOutput.Parent!.Parent!.Name;
        var path = Path.Combine(RepoRoot(), "src", "MyriagentHub", "bin", configuration, "net10.0", "win-x64", "MyriagentHub.deps.json");
        File.Exists(path).Should().BeTrue($"要先建置伺服器專案：{path}");
        return path;
    }
}
