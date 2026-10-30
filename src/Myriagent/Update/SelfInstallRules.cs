using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Update;

/// <summary>
/// 「安裝到這台電腦」要複製哪些檔（設計 8.3）：只複製這個發佈自己的檔。發佈的 zip 把檔案直接放在根目錄，
/// 使用者「解壓縮到這裡」解到「下載」或桌面時，exe 旁邊是使用者自己的檔案，絕不能整個資料夾複製進程式資料夾
/// </summary>
internal static class SelfInstallRules
{
    public const string ExeName = "Myriagent.exe";
    public const string MainAssemblyName = "Myriagent.dll";
    public const string DepsName = "Myriagent.deps.json";
    public const string RuntimeConfigName = "Myriagent.runtimeconfig.json";

    /// <summary>沒有這幾個檔就不是完整的發佈資料夾（framework-dependent 的 apphost 少了任何一個都啟動不了）</summary>
    public static IReadOnlyList<string> RequiredFiles { get; } = new[] { ExeName, MainAssemblyName, DepsName, RuntimeConfigName };

    /// <summary>
    /// 從 deps.json 推出發佈資料夾裡可能屬於這個 app 的檔（相對路徑、斜線分隔、不分大小寫去重；呼叫端只複製實際存在的）：
    /// 固定的 exe、主組件、deps、runtimeconfig；每個 target 的 runtime 組件（發佈時放在根目錄）、native 組件（根目錄，也保留原本的相對路徑：
    /// WebView2 的套件會另外放一份在 runtimes\win-x64\native\）、runtimeTargets（保留相對路徑）、resources（語系資料夾\檔名）；
    /// 每個 dll、exe 旁同名的 .pdb 與 .xml。不合法的路徑（ReleaseManifestRules.IsValidPath）整筆略過。deps.json 讀不懂丟 InvalidDataException
    /// </summary>
    public static IReadOnlyList<string> CandidatesFromDeps(string depsJson)
    {
        JObject root;
        try
        {
            root = JToken.Parse(depsJson) as JObject ?? throw new InvalidDataException($"{DepsName} 不是 JSON 物件");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{DepsName} 讀不懂：{ex.Message}", ex);
        }

        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path)
        {
            if (ReleaseManifestRules.IsValidPath(path) && seen.Add(path))
                files.Add(path);
        }

        foreach (var required in RequiredFiles)
            Add(required);
        foreach (var library in Libraries(root))
        {
            foreach (var asset in Assets(library, "runtime"))
            {
                if (ReleaseManifestRules.IsValidPath(asset))
                    Add(FileName(asset));
            }
            foreach (var asset in Assets(library, "native"))
            {
                if (!ReleaseManifestRules.IsValidPath(asset))
                    continue;
                Add(FileName(asset));
                Add(asset);
            }
            foreach (var asset in Assets(library, "runtimeTargets"))
                Add(asset);
            if (library["resources"] is JObject resources)
            {
                foreach (var resource in resources.Properties())
                {
                    if (ReleaseManifestRules.IsValidPath(resource.Name) && resource.Value is JObject info && info["locale"] is JValue { Type: JTokenType.String } locale)
                        Add($"{locale.Value<string>()}/{FileName(resource.Name)}");
                }
            }
        }

        foreach (var binary in files.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var stem = binary[..^4];
            Add(stem + ".pdb");
            Add(stem + ".xml");
        }
        return files;
    }

    /// <summary>targets 底下每個 target 的每個程式庫（物件）</summary>
    private static IEnumerable<JObject> Libraries(JObject root)
        => root["targets"] is JObject targets
            ? targets.Properties().Select(t => t.Value).OfType<JObject>().SelectMany(t => t.Properties().Select(l => l.Value).OfType<JObject>())
            : Enumerable.Empty<JObject>();

    private static IEnumerable<string> Assets(JObject library, string section)
        => library[section] is JObject assets ? assets.Properties().Select(p => p.Name) : Enumerable.Empty<string>();

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];
}
