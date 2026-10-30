using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Speech;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 語音通知（設定 voiceNotifications，預設開）：對話完成、失敗、代理提問、要求核准、計畫等確認時念一句（帶簡要說明）；視窗在前景也念，
/// 使用者按停止的那一輪不念；事情一發生就要每個視窗閃那個對話（flash，網頁閃到使用者點它為止；語音關著也閃），開始念時再送帶 speaking 的 flash；
/// 設定頁打開時念試聽、關掉時停下正在念的；
/// 語速與音調（voiceSpeed、voicePitch）由使用者調，調了念一句試聽，之後每句都照新的念
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubVoiceNotifyTests
{
    private static string TitleOf(HubHarness h, string id) => h.Conversation(id)!.Title;

    [Fact]
    public async Task 預設開_設定送給網頁()
    {
        using var h = new HubHarness();
        h.OnUi(() => h.Settings.VoiceNotifications).Should().BeTrue("預設開");
        var mark = h.Main.Mark();

        h.Command(h.Main, "setting", new { key = "voiceNotifications", value = false });

        h.OnUi(() => h.Settings.VoiceNotifications).Should().BeFalse();
        var settings = await h.Main.WaitForAsync("settings", since: mark);
        settings["settings"]!.Value<bool>("voiceNotifications").Should().BeFalse();
    }

    [Fact]
    public void 完成_對話開在前景視窗也念_系統通知照舊不打擾()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Command(h.Main, "visible", new { conversationIds = new[] { id } });

        h.CompleteTurn(s);

        h.Host.Spoken.All.Should().Equal(VoiceNotifyRules.Completed(TitleOf(h, id), null));
        h.Host.Notifications.Count.Should().Be(0, "看得到的對話不跳系統通知");
    }

    [Fact]
    public void 完成_念這一輪最後一則回覆的重點()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);

        h.Raise(s, new AgentTextCompletedEvent("b1", "先查一下報表的格式。"));
        h.Raise(s, new AgentTextCompletedEvent("b2", "月報已經匯出成 Excel，放在桌面。\n\n## 細節\n- 日期改成民國年"));
        h.CompleteTurn(s);

        h.Host.Spoken.All.Should().Equal($"「{TitleOf(h, id)}」已完成：月報已經匯出成 Excel，放在桌面");
    }

    [Fact]
    public void 結束時背景工作還在跑_只是在等不念完成_背景工作結束後CLI接著的那一輪結束才念()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);

        // 代理開了背景指令，回一句「等待介面探針結果」就結束這一輪
        s.HasLiveBackgroundWork = true;
        h.Raise(s, new AgentTextCompletedEvent("b1", "等待介面探針結果。"));
        h.CompleteTurn(s);
        h.Host.Spoken.Count.Should().Be(0, "背景工作還在跑，還沒真的完成");

        // 背景工作完成：CLI 自己開一輪處理結果，這一輪結束才是真的完成
        s.HasLiveBackgroundWork = false;
        h.Raise(s, new AgentIdleTurnStartedEvent(AfterBackgroundTask: true));
        h.Raise(s, new AgentTextCompletedEvent("b2", "介面探針全部通過，已推送到 main。"));
        h.CompleteTurn(s);
        h.Host.Spoken.All.Should().Equal($"「{TitleOf(h, id)}」已完成：介面探針全部通過，已推送到 main");
    }

    [Fact]
    public void 背景工作還在跑時_提問與核准照念()
    {
        using var h = new HubHarness();
        var (_, s) = h.StartConversation(h.Main, h.Claude);
        s.HasLiveBackgroundWork = true;

        h.Raise(s, new AgentApprovalEvent("p-1", "執行指令", "bash", "ls", null, null, CanAllowForSession: true));

        h.Host.Spoken.Count.Should().Be(1, "等你核准是真的要你處理");
    }

    [Fact]
    public async Task 完成時每個視窗馬上收到flash_開始念時再收到帶speaking的flash()
    {
        using var h = new HubHarness();
        var popout = h.AddView("popout");
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        var mark = h.Main.Mark();

        h.CompleteTurn(s);

        var start = await h.Main.WaitForAsync("flash", since: mark);
        start.Value<string>("conversationId").Should().Be(id);
        start["speaking"].Should().BeNull("聲音還沒出來就開始閃（閃到使用者點它）");
        popout.Messages("flash").Should().ContainSingle().Which.Value<string>("conversationId").Should().Be(id);

        var spoken = h.Main.Mark();
        h.Ui.Invoke(() => h.Host.SpeechStarted.All.Single()!());

        var speaking = await h.Main.WaitForAsync("flash", since: spoken);
        speaking.Value<string>("conversationId").Should().Be(id);
        speaking.Value<bool>("speaking").Should().BeTrue();
        popout.Messages("flash").Should().HaveCount(2);
    }

    [Fact]
    public async Task 語音通知關著_完成失敗提問核准照樣閃_不念()
    {
        using var h = new HubHarness();
        h.Command(h.Main, "setting", new { key = "voiceNotifications", value = false });
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        var mark = h.Main.Mark();

        h.Raise(s, new AgentQuestionEvent("q-1", new[] { new AgentQuestion { Id = "q", Text = "要用哪個格式？" } }, AnswerAsMessage: false));
        h.Raise(s, new AgentApprovalEvent("p-1", "執行指令", "bash", "ls", null, null, CanAllowForSession: true));
        h.CompleteTurn(s, AgentTurnOutcome.Failed, "連線中斷");
        h.Send(h.Main, id, "再試一次");
        h.CompleteTurn(s);

        await h.Main.WaitForAsync("flash", since: mark, predicate: _ => h.Main.Messages("flash", mark).Count == 4);
        h.Main.Messages("flash", mark).Should().OnlyContain(m => m.Value<string>("conversationId") == id && m["speaking"] == null);
        h.Host.Spoken.Count.Should().Be(0);
    }

    [Fact]
    public async Task 背景工作還在跑的結束與使用者按停止的那一輪_不閃()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        var mark = h.Main.Mark();

        s.HasLiveBackgroundWork = true;
        h.CompleteTurn(s);
        h.Send(h.Main, id, "停下");
        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);
        await Task.Delay(100);

        h.Main.Messages("flash", mark).Should().BeEmpty("還沒真的完成、使用者自己停下的，都不用回來看");
    }

    [Fact]
    public void 提問與核准_念對話名稱與要做的事()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        var title = TitleOf(h, id);

        h.Raise(s, new AgentQuestionEvent("q-1", new[] { new AgentQuestion { Id = "q", Text = "要用哪個格式？\n第二行" } }, AnswerAsMessage: false));
        h.Raise(s, new AgentApprovalEvent("p-1", "執行指令", "bash", "ls", null, null, CanAllowForSession: true));
        h.Raise(s, new AgentApprovalEvent("plan-1", "確認執行計畫", "plan", "代理已規劃好做法", "# 計畫", null, CanAllowForSession: false, IsPlan: true));

        h.Host.Spoken.All.Should().Equal(
            $"「{title}」有問題想問你：要用哪個格式？",
            $"「{title}」要執行指令，等你核准",
            $"「{title}」的計畫等你確認：計畫");
    }

    [Fact]
    public void 失敗念_使用者按停止的那一輪不念()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);

        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);
        h.Host.Spoken.Count.Should().Be(0, "使用者自己停下的");

        h.Send(h.Main, id, "再試一次");
        h.CompleteTurn(s, AgentTurnOutcome.Failed, "連線中斷");
        h.Host.Spoken.All.Should().Equal(VoiceNotifyRules.Failed(TitleOf(h, id), "連線中斷"));
        h.Host.Spoken.All.Single().Should().EndWith("執行失敗：連線中斷");
    }

    [Fact]
    public void 排隊的訊息接著送時不念_整串做完才念完成()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Send(h.Main, id, "排隊的第二則");

        h.CompleteTurn(s);
        h.Host.Spoken.Count.Should().Be(0, "接著送排隊的訊息，還沒結束");

        h.CompleteTurn(s);
        h.Host.Spoken.All.Should().Equal(VoiceNotifyRules.Completed(TitleOf(h, id), null));
    }

    [Fact]
    public void 關掉時停下正在念的且之後不念_打開時念一句試聽()
    {
        using var h = new HubHarness();
        var (_, s) = h.StartConversation(h.Main, h.Claude);

        h.Command(h.Main, "setting", new { key = "voiceNotifications", value = false });
        h.Host.SpeechStops.Should().Be(1);
        h.Raise(s, new AgentApprovalEvent("p-1", "執行指令", "bash", "ls", null, null, CanAllowForSession: true));
        h.CompleteTurn(s, AgentTurnOutcome.Failed, "連線中斷");
        h.Host.Spoken.Count.Should().Be(0);

        h.Command(h.Main, "setting", new { key = "voiceNotifications", value = true });
        h.Command(h.Main, "setting", new { key = "voiceNotifications", value = true });
        h.Host.Spoken.All.Should().Equal(new[] { VoiceNotifyRules.EnabledSample }, "已經開著時再送一次不重念");
        h.Host.SpeechStarted.All.Should().Equal(new Action?[] { null }, "試聽不屬於任何對話，不閃");
    }

    [Fact]
    public async Task 語速與音調_預設1點5倍升2個半音_送給網頁()
    {
        using var h = new HubHarness();
        var mark = h.Main.Mark();

        h.Command(h.Main, "setting", new { key = "notifications", value = true });

        var settings = (await h.Main.WaitForAsync("settings", since: mark))["settings"]!;
        settings.Value<double>("voiceSpeed").Should().Be(1.5);
        settings.Value<double>("voicePitch").Should().Be(2);
    }

    [Fact]
    public async Task 調語速_取到0點1並存檔_停下正在念的再以新語速念一句試聽()
    {
        using var h = new HubHarness();
        var mark = h.Main.Mark();

        h.Command(h.Main, "setting", new { key = "voiceSpeed", value = 1.84 });

        h.OnUi(() => h.Settings.VoiceSpeed).Should().Be(1.8);
        h.Host.SpeechStops.Should().Be(1, "先停下正在念的，連續調好幾次只聽到最後一次");
        h.Host.Spoken.All.Should().Equal(VoiceNotifyRules.TuningSample);
        h.Host.SpokenTuning.All.Should().Equal(new VoiceTuning(1.8, 2));
        h.Host.SpeechStarted.All.Should().Equal(new Action?[] { null }, "試聽不屬於任何對話，不閃");
        var settings = (await h.Main.WaitForAsync("settings", since: mark))["settings"]!;
        settings.Value<double>("voiceSpeed").Should().Be(1.8);

        h.Command(h.Main, "setting", new { key = "voicePitch", value = -2 });
        h.Command(h.Main, "setting", new { key = "voicePitch", value = -2 });
        h.OnUi(() => h.Settings.VoicePitch).Should().Be(-2);
        h.Host.SpokenTuning.All.Should().Equal(new VoiceTuning[] { new(1.8, 2), new(1.8, -2) }, "值沒變不重念");

        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        h.Host.Spoken.All.Last().Should().Be(VoiceNotifyRules.Completed(TitleOf(h, id), null));
        h.Host.SpokenTuning.All.Last().Should().Be(new VoiceTuning(1.8, -2), "之後的通知照新的語速與音調念");
    }

    [Fact]
    public void 調音調_超出範圍限制在範圍內_不是數字不改()
    {
        using var h = new HubHarness();

        h.Command(h.Main, "setting", new { key = "voicePitch", value = 30 });
        h.OnUi(() => h.Settings.VoicePitch).Should().Be(SpeechAudioRules.MaxPitch);
        h.Command(h.Main, "setting", new { key = "voiceSpeed", value = "fast" });
        h.Command(h.Main, "setting", new { key = "voiceSpeed", value = true });

        h.OnUi(() => h.Settings.VoiceSpeed).Should().Be(1.5);
        h.Host.Spoken.All.Should().Equal(VoiceNotifyRules.TuningSample);
    }

    [Fact]
    public void 語音通知關著時調語速_存起來但不念試聽()
    {
        using var h = new HubHarness();
        h.Command(h.Main, "setting", new { key = "voiceNotifications", value = false });
        var stops = h.Host.SpeechStops;

        h.Command(h.Main, "setting", new { key = "voiceSpeed", value = 2.1 });

        h.OnUi(() => h.Settings.VoiceSpeed).Should().Be(2.1);
        h.Host.Spoken.Count.Should().Be(0);
        h.Host.SpeechStops.Should().Be(stops);

        h.Command(h.Main, "setting", new { key = "voiceNotifications", value = true });
        h.Host.SpokenTuning.All.Should().Equal(new[] { new VoiceTuning(2.1, 2) }, "打開時的試聽就用調好的語速");
    }
}
