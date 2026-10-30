using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Json;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 第 5 階段的模型欄位（計畫 4.3 第 6 點、WP5.1 步 5）：AgentConversation 的 WorkingDirectoryRef、ForkedFrom、ForkedFromTitle、ForkPointSeq、
/// ForkSourceTranscript、PendingHandoffNote 全部可為 null、null 時不寫進 meta.json（舊檔存回逐位元組不變由 Phase4ModelFieldsTests 一起把關）；
/// 有值時存讀往返；前一版忽略不認得的欄位（這版讀到更新版多寫的欄位照樣讀得回來）
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class Phase5ModelFieldsTests : IDisposable
{
    private static readonly string[] NewFieldNames = ["WorkingDirectoryRef", "ForkedFrom", "ForkedFromTitle", "ForkPointSeq", "ForkSourceTranscript", "PendingHandoffNote"];

    private readonly TempDirectory temp = new("phase5-model");

    public void Dispose() => temp.Dispose();

    [Fact]
    public void 新欄位是null時不寫進meta_json()
    {
        var json = JsonConvert.SerializeObject(new AgentConversation { Title = "x", LeaseEpoch = 2 }, ProtocolJson.Settings);

        foreach (var name in NewFieldNames)
            json.Should().NotContain($"\"{name}\"", $"{name} 是 null 時不寫（舊檔逐位元組不變、回滾的前一版讀得懂）");
        json.Should().Contain("\"LeaseEpoch\": 2", "陽性對照：序列化的確實是這個物件");
    }

    [Fact]
    public void 新欄位存讀往返()
    {
        var store = new ConversationStore(temp.Root);
        var meta = new AgentConversation
        {
            Title = "分支（OFFICE-PC 離線分支）",
            AccountId = "acc-1",
            WorkingDirectory = @"C:\repo\proj\src",
            WorkingDirectoryRef = new PathRef(PathRefKinds.Git, "git:github.com/example-user/proj", "src", "proj"),
            ForkedFrom = "5f3a0c1d2e3f4a5b6c7d8e9f0a1b2c3d",
            ForkedFromTitle = "修正登入流程",
            ForkPointSeq = 81234,
            ForkSourceTranscript = temp.Combine("fork-source", "ed703f02-0092-409f-907d-3e6ab753ef6c.jsonl"),
            PendingHandoffNote = "這個對話剛從 OFFICE-PC 換到 HOME-PC。",
            LeaseEpoch = 3,
        };
        store.SaveConversation(meta);

        var loaded = new ConversationStore(temp.Root).LoadConversations().Single();
        loaded.WorkingDirectoryRef.Should().Be(meta.WorkingDirectoryRef);
        loaded.ForkedFrom.Should().Be(meta.ForkedFrom);
        loaded.ForkedFromTitle.Should().Be(meta.ForkedFromTitle);
        loaded.ForkPointSeq.Should().Be(81234);
        loaded.ForkSourceTranscript.Should().Be(meta.ForkSourceTranscript);
        loaded.PendingHandoffNote.Should().Be(meta.PendingHandoffNote);
        loaded.LeaseEpoch.Should().Be(3);

        var file = JObject.Parse(File.ReadAllText(Path.Combine(temp.Root, meta.Id, "meta.json")));
        file["WorkingDirectoryRef"]!.ToString(Formatting.None).Should().Be("""{"Kind":"git","RepoKey":"git:github.com/example-user/proj","Sub":"src","Display":"proj"}""");
    }

    [Fact]
    public void 參照的選用欄位null時不寫()
    {
        JsonConvert.SerializeObject(new AgentConversation { WorkingDirectoryRef = new PathRef(PathRefKinds.Path) }, ProtocolJson.Settings)
            .Should().Contain("\"WorkingDirectoryRef\": {").And.Contain("\"Kind\": \"path\"").And.NotContain("RepoKey");
    }

    [Fact]
    public void 更新版多寫的欄位_這版照樣讀得回來()
    {
        var store = new ConversationStore(temp.Root);
        var meta = new AgentConversation { Title = "x", AccountId = "acc-1" };
        store.SaveConversation(meta);
        var path = Path.Combine(temp.Root, meta.Id, "meta.json");
        var o = JObject.Parse(File.ReadAllText(path));
        o["WorkingDirectoryRef"] = new JObject { ["Kind"] = "svn", ["RepoKey"] = "svn:x", ["Future"] = 1 };
        o["SomeFutureField"] = "value";
        File.WriteAllText(path, o.ToString());

        var loaded = new ConversationStore(temp.Root).LoadConversations().Single();
        loaded.Title.Should().Be("x");
        loaded.WorkingDirectoryRef.Should().Be(new PathRef("svn", "svn:x"), "不認得的種類照存字串（由規則判斷不能用）");
    }
}
