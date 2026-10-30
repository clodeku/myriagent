using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Myriagent.Voice;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 輸入區的語音訊息（本機；web-protocol 的 voiceMessage）：錄音存進附件、交給辨識器（假的），代理收到逐字稿、錄音檔與「先複誦」的說明，
/// 畫面上是「語音訊息（0:12）」；新對話的標題用逐字稿；失敗時錄音留在輸入區、沒內容時刪掉；辨識期間送出被拒；沒有辨識器時不顯示也不處理
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubVoiceMessageTests
{
    private static readonly byte[] Audio = [0x1A, 0x45, 0xDF, 0xA3, 1, 2, 3, 4];

    private static string DataUrl(string mime = "audio/webm;codecs=opus") => $"data:{mime};base64,{Convert.ToBase64String(Audio)}";

    private static (HubHarness H, FakeVoiceTranscriber Voice) Create()
    {
        var h = new HubHarness();
        var voice = new FakeVoiceTranscriber();
        h.Ui.Invoke(() => h.Hub.AttachVoice(voice));
        return (h, voice);
    }

    private static List<string> Toasts(HubHarness h, int since) => h.Main.Messages("toast", since).Select(m => m.Value<string>("text")!).ToList();

    private static IReadOnlyList<string> PendingNames(HubHarness h, string id) => h.OnUi(() => h.Hub.ComposerStateForTest(id).Attachments);

    private static string[] VoiceFiles(HubHarness h, string id)
    {
        var dir = h.Store.GetAttachmentDirectory(id);
        return Directory.Exists(dir) ? Directory.GetFiles(dir, VoiceMessageRules.FilePrefix + "*") : [];
    }

    [Fact]
    public void 沒有辨識器_init不顯示麥克風_送了只提示()
    {
        using var h = new HubHarness();
        h.Command(h.Main, "ready", new { mode = "main" });
        h.Main.Last("init")!["voiceInput"]!.Value<bool>().Should().BeFalse();
        var id = h.NewConversation(h.Main, h.Claude);
        var mark = h.Main.Mark();

        h.Command(h.Main, "voiceMessage", new { conversationId = id, dataUrl = DataUrl() });

        Toasts(h, mark).Should().ContainSingle(t => t.Contains("沒有啟用語音訊息"));
        h.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task 新對話的語音訊息_代理收到逐字稿與錄音_畫面是語音訊息_標題用逐字稿()
    {
        var (h, voice) = Create();
        using var dispose = h;
        h.Command(h.Main, "ready", new { mode = "main" });
        h.Main.Last("init")!["voiceInput"]!.Value<bool>().Should().BeTrue();
        var id = h.NewConversation(h.Main, h.Claude);

        h.Command(h.Main, "voiceMessage", new { conversationId = id, dataUrl = DataUrl() });
        await h.WaitUntilAsync(() => h.Sessions.Count == 1 && h.Sessions[0].Sent.Count == 1, "語音訊息送進代理");

        var (input, _) = h.Sessions[0].Sent.All[0];
        input.Text.Should().Contain("逐字稿：「請幫我整理九月的報表」").And.Contain("請先用一段文字複誦使用者說了什麼");
        var attachment = input.Attachments.Should().ContainSingle().Subject;
        attachment.FileName.Should().StartWith(VoiceMessageRules.FilePrefix).And.EndWith(".weba");
        attachment.Kind.Should().Be(AgentAttachmentKind.Document);
        File.ReadAllBytes(attachment.StoredPath).Should().Equal(Audio);
        voice.Paths.Should().Equal(attachment.StoredPath);

        var user = h.Items(id).Single(i => i.Kind == AgentItemKind.User);
        user.Text.Should().Be("語音訊息（0:12）", "畫面上不是給代理的長說明");
        user.Attachments.Should().ContainSingle(a => a.StoredPath == attachment.StoredPath);
        h.Conversation(id)!.Title.Should().Be("請幫我整理九月的報表", "新對話的標題用逐字稿");
        PendingNames(h, id).Should().BeEmpty();
    }

    [Fact]
    public async Task 輸入區原本的附件一起送出()
    {
        var (h, _) = Create();
        using var dispose = h;
        var id = h.NewConversation(h.Main, h.Claude);
        var file = h.Temp.Combine("錯誤訊息.txt");
        File.WriteAllText(file, "NullReferenceException");
        h.Command(h.Main, "addFiles", new { conversationId = id }, [file]);
        await h.WaitUntilAsync(() => PendingNames(h, id).Count == 1, "檔案成為附件");

        h.Command(h.Main, "voiceMessage", new { conversationId = id, dataUrl = DataUrl("audio/mp4") });
        await h.WaitUntilAsync(() => h.Sessions.Count == 1 && h.Sessions[0].Sent.Count == 1, "語音訊息送進代理");

        h.Sessions[0].Sent.All[0].Input.Attachments.Select(a => Path.GetExtension(a.FileName)).Should().BeEquivalentTo([".txt", ".m4a"]);
    }

    [Fact]
    public async Task 辨識失敗_錄音留在輸入區的附件_提示原因_不送()
    {
        var (h, voice) = Create();
        using var dispose = h;
        voice.Failure = new VoiceTranscribeException("這台電腦找不到 Python");
        var id = h.NewConversation(h.Main, h.Claude);
        var mark = h.Main.Mark();

        h.Command(h.Main, "voiceMessage", new { conversationId = id, dataUrl = DataUrl() });
        await h.WaitUntilAsync(() => Toasts(h, mark).Count > 0, "提示");

        Toasts(h, mark).Should().ContainSingle(t => t.Contains("找不到 Python") && t.Contains("錄音已放進輸入區的附件"));
        PendingNames(h, id).Should().ContainSingle(n => n.StartsWith(VoiceMessageRules.FilePrefix));
        h.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task 送出被拒_錄音留在輸入區而且網頁看得到_不把長說明還給輸入框()
    {
        using var h = new HubHarness(status: AccountStatus.NotLoggedIn);
        h.Ui.Invoke(() => h.Hub.AttachVoice(new FakeVoiceTranscriber()));
        var id = h.NewConversation(h.Main, h.Claude);
        var mark = h.Main.Mark();

        h.Command(h.Main, "voiceMessage", new { conversationId = id, dataUrl = DataUrl() });
        await h.WaitUntilAsync(() => Toasts(h, mark).Count > 0, "送出被拒的提示");
        await h.Ui.DrainAsync();

        h.Sessions.Should().BeEmpty();
        var composer = h.Main.Messages("composer", mark, id).Last()["composer"]!;
        ((JArray)composer["attachments"]!).Select(a => a.Value<string>("name")).Should().ContainSingle(n => n!.StartsWith(VoiceMessageRules.FilePrefix),
            "拒絕之後重送輸入區，網頁看得到留下的錄音");
        h.Main.Messages("sendRejected", mark).Should().BeEmpty("給代理的長說明不還給輸入框");
    }

    [Fact]
    public async Task 沒有聽到內容_刪掉錄音_提示再錄一次()
    {
        var (h, voice) = Create();
        using var dispose = h;
        voice.Result = _ => new VoiceTranscript("  \n ", 3);
        var id = h.NewConversation(h.Main, h.Claude);
        var mark = h.Main.Mark();

        h.Command(h.Main, "voiceMessage", new { conversationId = id, dataUrl = DataUrl() });
        await h.WaitUntilAsync(() => Toasts(h, mark).Count > 0, "提示");

        Toasts(h, mark).Should().ContainSingle(t => t.Contains("沒有聽到說話的內容"));
        VoiceFiles(h, id).Should().BeEmpty("錄音刪了");
        PendingNames(h, id).Should().BeEmpty();
        h.Sessions.Should().BeEmpty();
    }

    [Fact]
    public void 不是音訊的資料_不存檔不辨識()
    {
        var (h, voice) = Create();
        using var dispose = h;
        var id = h.NewConversation(h.Main, h.Claude);
        var mark = h.Main.Mark();

        h.Command(h.Main, "voiceMessage", new { conversationId = id, dataUrl = "data:text/plain;base64,AAAA" });

        Toasts(h, mark).Should().ContainSingle(t => t.Contains("錄音的格式讀不了"));
        voice.Paths.Should().BeEmpty();
        VoiceFiles(h, id).Should().BeEmpty();
    }

    [Fact]
    public async Task 辨識期間_輸入區顯示辨識中_送出被拒_辨識完照常送出()
    {
        var (h, voice) = Create();
        using var dispose = h;
        voice.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = h.NewConversation(h.Main, h.Claude);
        var mark = h.Main.Mark();

        h.Command(h.Main, "voiceMessage", new { conversationId = id, dataUrl = DataUrl() });
        await h.WaitUntilAsync(() => !voice.Paths.IsEmpty, "開始辨識");
        h.Main.Messages("composer", mark, id).Select(m => m["composer"]!.Value<string>("busyText")).Should().Contain("正在辨識語音…");
        h.Send(h.Main, id, "打字的那則");
        Toasts(h, mark).Should().Contain("附件還在處理中，請稍候");

        voice.Gate.SetResult();
        await h.WaitUntilAsync(() => h.Sessions.Count == 1 && h.Sessions[0].Sent.Count == 1, "辨識完送出");
        h.Sessions[0].Sent.All[0].Input.Text.Should().Contain("請幫我整理九月的報表");
    }

    [Fact]
    public async Task 執行中_語音訊息排隊_畫面文字是語音訊息_這一輪結束後送出()
    {
        var (h, _) = Create();
        using var dispose = h;
        var (id, session) = h.StartConversation(h.Main, h.Claude, "先做這個");

        h.Command(h.Main, "voiceMessage", new { conversationId = id, dataUrl = DataUrl() });
        await h.WaitUntilAsync(() => h.Queue(id).Count == 1, "排進佇列");
        var queue = h.Main.Last("composer", id)!["composer"]!["queue"]!;
        queue[0]!.Value<string>("text")!.Should().StartWith("語音訊息（0:12）", "佇列顯示的是畫面文字");

        h.CompleteTurn(session);
        await h.WaitUntilAsync(() => session.Sent.Count == 2, "這一輪結束後送出");
        session.Sent.All[1].Input.Text.Should().Contain("逐字稿：「請幫我整理九月的報表」");
        h.Items(id).Last(i => i.Kind == AgentItemKind.User).Text.Should().Be("語音訊息（0:12）");
    }

    [Fact]
    public async Task 錄音時記下的波形存在錄音附件上_畫面的附件帶wave_格式不對的不存()
    {
        var (h, _) = Create();
        using var dispose = h;
        h.Command(h.Main, "ready", new { mode = "main" });
        var id = h.NewConversation(h.Main, h.Claude);
        h.Command(h.Main, "voiceMessage", new { conversationId = id, dataUrl = DataUrl(), wave = "0136985310" });
        await h.WaitUntilAsync(() => h.Sessions.Count == 1 && h.Sessions[0].Sent.Count == 1, "語音訊息送進代理");
        h.Items(id).Single(i => i.Kind == AgentItemKind.User).Attachments.Should().ContainSingle().Which.Waveform.Should().Be("0136985310");
        var rendered = h.Main.All().Where(m => m.Value<string>("type") == "item").Select(m => (JObject)m["item"]!).Last(i => i.Value<string>("kind") == "User");
        rendered["attachments"]![0]!.Value<string>("wave").Should().Be("0136985310");

        h.CompleteTurn(h.Sessions[0]);
        h.Command(h.Main, "voiceMessage", new { conversationId = id, dataUrl = DataUrl(), wave = "<b>" });
        await h.WaitUntilAsync(() => h.Sessions[0].Sent.Count == 2, "第二則送進代理");
        h.Items(id).Last(i => i.Kind == AgentItemKind.User).Attachments.Single().Waveform.Should().BeNull("不是 0～9 的數字不存");
    }
}
