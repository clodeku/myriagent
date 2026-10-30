using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 沒送出的草稿留下的資料夾（草稿只在記憶體裡；登出、關機、當掉時附件複本留在 conversations\{id}\attachments）：
/// 啟動時只挑出「確定是草稿」的資料夾刪掉，可能還有使用者資料的一律不動
/// </summary>
public sealed class ConversationStoreDraftLeftoverTests : IDisposable
{
    private readonly TempDirectory temp = new("store-leftover");

    public void Dispose() => temp.Dispose();

    private static string NewId() => Guid.NewGuid().ToString("N");

    [Fact]
    public void 只有附件與空工作資料夾的草稿資料夾_找得到並刪掉_對話與可能有資料的資料夾不動()
    {
        var store = new ConversationStore(temp.Root);

        // 草稿留下的：附件（含給模型看的縮圖）、開過檔案抽屜而建立的空 workspace、什麼都沒有的空資料夾
        var draft = NewId();
        temp.WriteFile($@"{draft}\attachments\影片.mp4");
        temp.WriteFile($@"{draft}\attachments\.model\影片_1.jpg");
        var draftWithWorkspace = NewId();
        temp.WriteFile($@"{draftWithWorkspace}\attachments\截圖.png");
        temp.CreateDirectory($@"{draftWithWorkspace}\workspace");
        var empty = NewId();
        temp.CreateDirectory(empty);

        // 不能動的：正常的對話、meta 遺失但還有內容、meta 損毀已改名備份、meta 寫到一半、工作資料夾有代理產出、不是對話 Id 的資料夾
        var saved = new AgentConversation { Title = "已送出" };
        store.SaveConversation(saved);
        temp.WriteFile($@"{saved.Id}\attachments\a.png");
        var itemsOnly = NewId();
        temp.WriteFile($@"{itemsOnly}\items.json", "[]");
        var corrupt = NewId();
        temp.WriteFile($@"{corrupt}\meta.json.corrupt-20261010-010203", "{");
        temp.WriteFile($@"{corrupt}\attachments\b.png");
        var halfWritten = NewId();
        temp.WriteFile($@"{halfWritten}\meta.json.tmp", "{}");
        var agentOutput = NewId();
        temp.WriteFile($@"{agentOutput}\attachments\c.png");
        temp.WriteFile($@"{agentOutput}\workspace\輸出.txt");
        temp.WriteFile(@"我的備份\attachments\d.png");

        var leftovers = store.FindDraftLeftovers();

        leftovers.Should().BeEquivalentTo(new[] { draft, draftWithWorkspace, empty });
        foreach (var id in leftovers)
            store.DeleteConversation(id).Should().BeTrue();
        new[] { draft, draftWithWorkspace, empty }.Should().OnlyContain(id => !Directory.Exists(temp.Combine(id)));
        new[] { saved.Id, itemsOnly, corrupt, halfWritten, agentOutput, "我的備份" }.Should().OnlyContain(id => Directory.Exists(temp.Combine(id)));
        File.Exists(temp.Combine(saved.Id, "attachments", "a.png")).Should().BeTrue();
        store.LoadConversations().Select(c => c.Id).Should().Equal(saved.Id);
    }

    [Fact]
    public void 對話資料夾還不存在_沒有草稿殘留()
        => new ConversationStore(temp.Combine("conversations")).FindDraftLeftovers().Should().BeEmpty();
}
