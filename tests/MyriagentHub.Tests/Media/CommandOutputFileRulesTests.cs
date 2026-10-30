using Myriagent.Remote;
using MyriagentHub.Media;
using MyriagentHub.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Media;

/// <summary>
/// 遠端檔案指令的輸出（files、fileSearch、fileRef）轉給瀏覽器之前的投影（WP4.7 計畫步 13，接在 WP4.5 的 CommandOutputRules 裡）：
/// 只留已知的鍵、requestId 與 conversationId 要等於指令的、電腦送來的 url／thumbUrl 不留、媒體參照留給改寫器；任何一處不合格整則丟掉
/// </summary>
public sealed class CommandOutputFileRulesTests
{
    private const string Conv = "0123456789abcdef0123456789abcdef";

    private const string Ref = "01J9Q0000000000000000000AA";

    private static OutputContext Context(string cmd, string? requestId = "r1", string? expected = Conv, string? browser = Conv)
        => new(cmd, expected, browser, requestId, null);

    private static JObject Entry(string name = "a.png", string kind = "image") => new()
    {
        ["name"] = name, ["path"] = @"C:\work\" + name, ["kind"] = kind, ["size"] = 12, ["modified"] = 1_700_000_000_000, ["hidden"] = false,
        ["ref"] = Ref, ["ver"] = 3, ["url"] = "/media?p=x", ["thumbUrl"] = "/thumb?p=x", ["extra"] = "多的",
    };

    private static JObject Files(params JObject[] entries) => new()
    {
        ["type"] = "files", ["requestId"] = "r1", ["conversationId"] = Conv, ["path"] = @"C:\work", ["parent"] = null, ["root"] = @"C:\work",
        ["entries"] = new JArray(entries), ["truncated"] = true, ["error"] = null, ["extra"] = 1,
    };

    // ───── files ─────

    [Fact]
    public void files_只留已知的鍵_url與thumbUrl不留_參照與版本留給改寫器()
    {
        var projected = CommandOutputFileRules.ProjectFiles(Files(Entry()), Context("listFiles"))!;

        projected.ToString(Formatting.None).Should().Be(
            $$"""{"type":"files","requestId":"r1","conversationId":"{{Conv}}","path":"C:\\work","parent":null,"root":"C:\\work","entries":[{"name":"a.png","path":"C:\\work\\a.png","kind":"image","size":12,"modified":1700000000000,"hidden":false,"ref":"{{Ref}}","ver":3}],"truncated":true,"error":null}""");
    }

    [Fact]
    public void files_資料夾的size與modified是null_沒給的hidden與truncated是false_字串版本照留()
    {
        var dir = new JObject { ["name"] = "src", ["path"] = @"C:\work\src", ["kind"] = "dir", ["size"] = null };
        var file = new JObject { ["name"] = "b.mp4", ["path"] = @"C:\work\b.mp4", ["kind"] = "video", ["ver"] = "1700000000000", ["ref"] = 5 };
        var message = new JObject { ["type"] = "files", ["requestId"] = "r1", ["conversationId"] = Conv, ["entries"] = new JArray(dir, file) };

        var projected = CommandOutputFileRules.ProjectFiles(message, Context("listFiles"))!;

        projected["truncated"]!.Value<bool>().Should().BeFalse();
        projected["path"]!.Type.Should().Be(JTokenType.Null);
        var entries = (JArray)projected["entries"]!;
        entries[0].ToString(Formatting.None).Should().Be("""{"name":"src","path":"C:\\work\\src","kind":"dir","size":null,"modified":null,"hidden":false}""");
        entries[1]["ver"]!.Value<string>().Should().Be("1700000000000");
        ((JObject)entries[1]).ContainsKey("ref").Should().BeFalse("參照不是字串：不留（改寫器也不會發網址）");
    }

    [Fact]
    public void files_dirsOnly的虛擬根_名稱是完整路徑()
    {
        var drive = new JObject { ["name"] = @"D:\", ["path"] = @"D:\", ["kind"] = "dir" };
        var message = new JObject { ["type"] = "files", ["requestId"] = "r1", ["conversationId"] = Conv, ["path"] = null, ["entries"] = new JArray(drive) };
        CommandOutputFileRules.ProjectFiles(message, Context("listFiles"))!["entries"]![0]!["name"]!.Value<string>().Should().Be(@"D:\");
    }

    [Fact]
    public void files_錯誤訊息照留()
    {
        var message = new JObject { ["type"] = "files", ["requestId"] = "r1", ["conversationId"] = Conv, ["entries"] = new JArray(), ["error"] = "找不到這個資料夾" };
        CommandOutputFileRules.ProjectFiles(message, Context("listFiles"))!["error"]!.Value<string>().Should().Be("找不到這個資料夾");
    }

    public static TheoryData<string, Action<JObject>> BadFiles => new()
    {
        { "沒有 entries", m => m.Remove("entries") },
        { "entries 不是陣列", m => m["entries"] = new JObject() },
        { "path 不是字串", m => m["path"] = 1 },
        { "truncated 不是布林", m => m["truncated"] = "true" },
        { "error 太長", m => m["error"] = new string('e', CommandOutputFileRules.MaxErrorLength + 1) },
        { "path 太長", m => m["path"] = new string('p', CommandOutputFileRules.MaxPathLength + 1) },
        { "entry 不是物件", m => ((JArray)m["entries"]!).Add("x") },
        { "entry 沒有名稱", m => ((JObject)m["entries"]![0]!)["name"] = "" },
        { "entry 沒有路徑", m => ((JObject)m["entries"]![0]!).Remove("path") },
        { "entry 的 kind 不認得", m => ((JObject)m["entries"]![0]!)["kind"] = "exe" },
        { "entry 的 kind 大小寫不同", m => ((JObject)m["entries"]![0]!)["kind"] = "Image" },
        { "entry 的 size 負數", m => ((JObject)m["entries"]![0]!)["size"] = -1 },
        { "entry 的 size 是字串", m => ((JObject)m["entries"]![0]!)["size"] = "12" },
        { "entry 的 modified 是浮點數", m => ((JObject)m["entries"]![0]!)["modified"] = 1.5 },
        { "entry 的 hidden 是字串", m => ((JObject)m["entries"]![0]!)["hidden"] = "false" },
        { "requestId 不同", m => m["requestId"] = "r2" },
        { "沒有 requestId", m => m.Remove("requestId") },
        { "conversationId 不同", m => m["conversationId"] = "fedcba9876543210fedcba9876543210" },
        { "conversationId 大寫", m => m["conversationId"] = Conv.ToUpperInvariant() },
        { "沒有 conversationId", m => m.Remove("conversationId") },
    };

    [Theory]
    [MemberData(nameof(BadFiles))]
    public void files_任何一處不合格_整則丟掉(string because, Action<JObject> mutate)
    {
        var message = Files(Entry());
        mutate(message);
        CommandOutputFileRules.ProjectFiles(message, Context("listFiles")).Should().BeNull(because);
    }

    [Fact]
    public void files_筆數上限3000()
    {
        var entries = Enumerable.Range(0, CommandOutputFileRules.MaxEntries).Select(i => Entry($"f{i}.txt", "doc")).ToArray();
        CommandOutputFileRules.ProjectFiles(Files(entries), Context("listFiles")).Should().NotBeNull();
        CommandOutputFileRules.ProjectFiles(Files([.. entries, Entry("one-more.txt", "doc")]), Context("listFiles")).Should().BeNull("3001 筆");
    }

    [Fact]
    public void 指令沒有requestId或預期的對話不是線上id_一律丟()
    {
        CommandOutputFileRules.ProjectFiles(Files(Entry()), Context("listFiles", requestId: null)).Should().BeNull();
        CommandOutputFileRules.ProjectFiles(Files(Entry()), Context("listFiles", expected: null, browser: null)).Should().BeNull();
        var draft = "d01J9Q0000000000000000000BB";
        var message = Files(Entry());
        message["conversationId"] = draft;
        CommandOutputFileRules.ProjectFiles(message, Context("listFiles", expected: draft, browser: draft)).Should().BeNull("草稿沒有檔案指令（WP4.6 再看）");
    }

    [Fact]
    public void 瀏覽器看到的id優先於預期的id()
    {
        // 型錄路由 Owner 的指令兩者相同；不同時以瀏覽器送來的為準（電腦回的要等於它）
        var other = "fedcba9876543210fedcba9876543210";
        CommandOutputFileRules.ProjectFiles(Files(Entry()), Context("listFiles", expected: other, browser: Conv))!["conversationId"]!.Value<string>().Should().Be(Conv);
        CommandOutputFileRules.ProjectFiles(Files(Entry()), Context("listFiles", expected: Conv, browser: other)).Should().BeNull();
    }

    // ───── fileSearch ─────

    [Fact]
    public void fileSearch_只留三個欄位_上限30筆()
    {
        JObject Result(int i) => new() { ["relativePath"] = $"src/a{i}.cs", ["fullPath"] = $@"C:\work\src\a{i}.cs", ["kind"] = "code", ["url"] = "/media?p=x" };
        JObject Search(int count) => new()
        {
            ["type"] = "fileSearch", ["requestId"] = "r1", ["conversationId"] = Conv, ["query"] = "a", ["results"] = new JArray(Enumerable.Range(0, count).Select(Result)),
        };

        CommandOutputFileRules.ProjectFileSearch(Search(1), Context("fileSearch"))!.ToString(Formatting.None).Should().Be(
            $$"""{"type":"fileSearch","requestId":"r1","conversationId":"{{Conv}}","query":"a","results":[{"relativePath":"src/a0.cs","fullPath":"C:\\work\\src\\a0.cs","kind":"code"}]}""");
        CommandOutputFileRules.ProjectFileSearch(Search(CommandOutputFileRules.MaxResults), Context("fileSearch")).Should().NotBeNull();
        CommandOutputFileRules.ProjectFileSearch(Search(CommandOutputFileRules.MaxResults + 1), Context("fileSearch")).Should().BeNull("31 筆");

        var noQuery = Search(1);
        noQuery.Remove("query");
        CommandOutputFileRules.ProjectFileSearch(noQuery, Context("fileSearch")).Should().BeNull();
        var longQuery = Search(1);
        longQuery["query"] = new string('q', RemoteCommandShapeRules.MaxTextLength + 1);
        CommandOutputFileRules.ProjectFileSearch(longQuery, Context("fileSearch")).Should().BeNull();
        var emptyFull = Search(1);
        emptyFull["results"]![0]!["fullPath"] = "";
        CommandOutputFileRules.ProjectFileSearch(emptyFull, Context("fileSearch")).Should().BeNull();
        var badKind = Search(1);
        badKind["results"]![0]!["kind"] = "binary";
        CommandOutputFileRules.ProjectFileSearch(badKind, Context("fileSearch")).Should().BeNull();
    }

    // ───── fileRef ─────

    [Fact]
    public void fileRef_成功時留name_size_expiresAt與ref_error是null()
    {
        var message = new JObject
        {
            ["type"] = "fileRef", ["requestId"] = "r1", ["conversationId"] = Conv, ["ref"] = Ref, ["name"] = "報告.pdf", ["size"] = 2048,
            ["expiresAt"] = 1_700_000_900_000, ["url"] = "/media?p=x", ["error"] = null,
        };
        CommandOutputFileRules.ProjectFileRef(message, Context("fileFetch"))!.ToString(Formatting.None).Should().Be(
            $$"""{"type":"fileRef","requestId":"r1","conversationId":"{{Conv}}","ref":"{{Ref}}","name":"報告.pdf","size":2048,"expiresAt":1700000900000,"error":null}""");

        foreach (var (key, value) in new (string, JToken?)[] { ("name", ""), ("name", null), ("size", -1), ("size", "1"), ("expiresAt", null), ("expiresAt", 1.5) })
        {
            var bad = (JObject)message.DeepClone();
            if (value is null)
                bad.Remove(key);
            else
                bad[key] = value;
            CommandOutputFileRules.ProjectFileRef(bad, Context("fileFetch")).Should().BeNull($"{key}={value}");
        }
    }

    [Fact]
    public void fileRef_有錯誤時只留開頭與error_空字串的錯誤丟掉()
    {
        var message = new JObject
        {
            ["type"] = "fileRef", ["requestId"] = "r1", ["conversationId"] = Conv, ["error"] = "這個檔案不能給遠端讀取", ["name"] = "a.txt", ["ref"] = Ref,
        };
        CommandOutputFileRules.ProjectFileRef(message, Context("fileFetch"))!.ToString(Formatting.None).Should().Be(
            $$"""{"type":"fileRef","requestId":"r1","conversationId":"{{Conv}}","error":"這個檔案不能給遠端讀取"}""");
        message["error"] = "";
        CommandOutputFileRules.ProjectFileRef(message, Context("fileFetch")).Should().BeNull();
        message["error"] = 1;
        CommandOutputFileRules.ProjectFileRef(message, Context("fileFetch")).Should().BeNull();
    }

    [Fact]
    public void 不改原物件()
    {
        var message = Files(Entry());
        var before = message.ToString(Formatting.None);
        var projected = CommandOutputFileRules.ProjectFiles(message, Context("listFiles"))!;
        projected["entries"]![0]!["name"] = "changed";
        message.ToString(Formatting.None).Should().Be(before);
    }
}
