using Myriagent.Sync;
using MyriagentHub.Hosting;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Sync;

/// <summary>
/// 輸入框的草稿（draft1，sync-protocol 14.17）：伺服器每個對話只留最後一份、rev 遞增、一樣的與清掉沒有的不算變動、寫檔與讀回、
/// 太舊與太多的丟掉；瀏覽器的 composerDraft 解析與 composerDrafts 訊息的形狀
/// </summary>
public sealed class ComposerDraftStoreTests : IDisposable
{
    private static readonly string A = new('a', 32);
    private static readonly string B = new('b', 32);

    private readonly string dir = TempDir.Create();
    private readonly HubPaths paths;
    private readonly ManualTimeProvider time = new();

    public ComposerDraftStoreTests()
    {
        paths = new HubPaths(dir);
        paths.EnsureCreated();
    }

    public void Dispose() => TempDir.TryDelete(dir);

    private ComposerDraftStore NewStore() => new(paths, time, NullLogger<ComposerDraftStore>.Instance);

    [Fact]
    public void 存下並觸發_一樣的與清掉沒有的不觸發_清掉觸發空字串()
    {
        var store = NewStore();
        var changed = new List<(ComposerDraftView View, string? Origin)>();
        store.Changed += (v, o) => changed.Add((v, o));

        var first = store.Publish(A, "幫我看", "辦公室桌機", "d:1");
        first.Should().NotBeNull();
        first!.Rev.Should().Be(time.NowMs, "rev 至少是收到的毫秒");
        first.From.Should().Be("辦公室桌機");
        store.Publish(A, "幫我看", "筆電", "d:2").Should().BeNull("一樣的");
        store.Publish(B, "  ", "筆電", "d:2").Should().BeNull("清掉本來就沒有的");

        var second = store.Publish(A, "幫我看一下", ComposerDraftRules.BrowserFrom, "b:x")!;
        second.Rev.Should().Be(first.Rev + 1, "同一毫秒也遞增");
        var cleared = store.Publish(A, "", "筆電", "d:2")!;
        cleared.Text.Should().BeEmpty();
        cleared.Rev.Should().BeGreaterThan(second.Rev);

        changed.Select(c => (c.View.Text, c.Origin)).Should().Equal(("幫我看", "d:1"), ("幫我看一下", "b:x"), ("", "d:2"));
        store.Snapshot().Should().BeEmpty("清掉的不在整份裡");
    }

    [Fact]
    public void 寫檔之後新的存放讀得回來_rev接著往上()
    {
        var store = NewStore();
        store.Publish(A, "第一則", "桌機", null);
        var last = store.Publish(B, "第二則", "筆電", null)!;
        store.SaveNow();
        File.Exists(store.FilePath).Should().BeTrue();

        var again = NewStore();
        again.Snapshot().Select(d => (d.ConversationId, d.Text, d.From)).Should().Equal((A, "第一則", "桌機"), (B, "第二則", "筆電"));
        again.Publish(A, "改了", "桌機", null)!.Rev.Should().BeGreaterThan(last.Rev);
    }

    [Fact]
    public void 壞掉的檔當成沒有()
    {
        File.WriteAllText(Path.Combine(paths.DbDir, ComposerDraftStore.FileName), "{壞掉");
        var store = NewStore();
        store.Snapshot().Should().BeEmpty();
        store.Publish(A, "x", null, null).Should().NotBeNull();
    }

    [Fact]
    public void 太舊的丟掉_超過上限丟最舊的()
    {
        var store = NewStore();
        store.Publish(A, "很久以前", null, null);
        time.Advance(ComposerDraftRules.MaxAge + TimeSpan.FromMinutes(1));
        store.Publish(B, "剛剛", null, null);
        store.Snapshot().Select(d => d.ConversationId).Should().Equal(B);

        for (var i = 0; i < ComposerDraftRules.MaxDrafts + 3; i++)
            store.Publish(i.ToString("x32"), "第 " + i + " 則", null, null);
        var all = store.Snapshot();
        all.Should().HaveCount(ComposerDraftRules.MaxDrafts);
        all[0].ConversationId.Should().Be(3.ToString("x32"), "最舊的幾則丟掉");
    }

    [Fact]
    public void 疑似權杖的不存不轉()
    {
        var store = NewStore();
        var changed = 0;
        store.Changed += (_, _) => changed++;
        store.Publish(A, "金鑰是 " + "AK" + "IA" + "QWERTYUIOPASDFGH", ComposerDraftRules.BrowserFrom, "b:x").Should().BeNull();
        store.Snapshot().Should().BeEmpty();
        changed.Should().Be(0);
    }

    [Fact]
    public void 來源名稱去掉控制字元並截短()
    {
        var store = NewStore();
        store.Publish(A, "x", "桌機\n" + new string('名', 80), null)!.From.Should().HaveLength(ComposerDraftRules.MaxFromChars).And.NotContain("\n");
        store.Publish(B, "x", "   ", null)!.From.Should().BeNull();
    }

    [Fact]
    public void 瀏覽器的composerDraft_解析與上限()
    {
        static CmdParse Parse(object cmd) => MessageGuard.ParseCmd(new JObject { ["commandId"] = "01J9Q0000000000000000000AA", ["cmd"] = JObject.FromObject(cmd) });

        var ok = Parse(new { cmd = "composerDraft", conversationId = A, text = new string('字', ComposerDraftRules.MaxTextChars) });
        ok.Error.Should().BeNull();
        ok.Command.Should().BeOfType<ComposerDraftCommand>().Which.ConversationId.Should().Be(A);
        Parse(new { cmd = "composerDraft", conversationId = A, text = "" }).Error.Should().BeNull("空字串＝清掉");
        Parse(new { cmd = "composerDraft", conversationId = A, text = new string('字', ComposerDraftRules.MaxTextChars + 1) }).Error.Should().Be(BrowserErrorCodes.Invalid);
        Parse(new { cmd = "composerDraft", conversationId = "dXYZ", text = "x" }).Error.Should().Be(BrowserErrorCodes.Invalid, "伺服器草稿、不是線上 id");
        Parse(new { cmd = "composerDraft", conversationId = A }).Error.Should().Be(BrowserErrorCodes.Invalid);
        BrowserCommandRules.Classify("composerDraft").Should().Be(BrowserCommandClass.Server);
        BrowserCommandRules.SilentCommands.Should().Contain("composerDraft", "網頁自己送的，被拒不跳 toast");
    }

    [Fact]
    public void composerDrafts訊息的形狀()
    {
        var message = BrowserGateway.ComposerDraftsMessage([new ComposerDraftView(A, "幫我", 7, 8, "桌機"), new ComposerDraftView(B, "", 9, 10, null)], full: false);
        message.Type.Should().Be("composerDrafts");
        var json = JObject.Parse(message.Json);
        json.Value<bool>("full").Should().BeFalse();
        var drafts = (JArray)json["drafts"]!;
        drafts[0]!.ToString(Newtonsoft.Json.Formatting.None).Should().Be("{\"conversationId\":\"" + A + "\",\"text\":\"幫我\",\"rev\":7,\"at\":8,\"from\":\"桌機\"}");
        drafts[1]!["from"]!.Type.Should().Be(JTokenType.Null);
        ClientOutboxRules.KeptAcrossInit("composerDrafts").Should().BeFalse("溢出重送 init 時跟著再送一份整份的");
    }
}
