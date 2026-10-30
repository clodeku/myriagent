using Myriagent.Remote;
using MyriagentHub.Realtime;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 遠端指令給瀏覽器的訊息（WP4.5 計畫步 2；D21～D24、D26）：commandResult、control、sendRejected 的形狀，toast 的文字，
/// 還原文字的條件，所有電腦登出的彙整文字與各台的提示
/// </summary>
public sealed class CommandMessageRulesTests
{
    [Fact]
    public void commandResult的JSON逐字()
    {
        CommandMessageRules.CommandResultJson("01J9Q0000000000000000000AA", RemoteResultStates.Rejected, RemoteResultCodes.Offline,
                "那台電腦目前不在線，指令沒有送出", null)
            .Should().Be("""{"type":"commandResult","commandId":"01J9Q0000000000000000000AA","state":"rejected","code":"offline","message":"那台電腦目前不在線，指令沒有送出","tier":null}""");
        CommandMessageRules.CommandResultJson("01J9Q0000000000000000000AA", RemoteResultStates.Done, null, null, 1)
            .Should().Be("""{"type":"commandResult","commandId":"01J9Q0000000000000000000AA","state":"done","code":null,"message":null,"tier":1}""");
        CommandMessageRules.CommandResultJson("x", RemoteResultStates.Rejected, RemoteResultCodes.Stale, "<b>\"引號\"</b>", 2)
            .Should().Contain("\"message\":\"<b>\\\"引號\\\"</b>\"", "文字以 JSON 字串跳脫，網頁以 textContent 顯示");
    }

    [Fact]
    public void control逐字()
    {
        CommandMessageRules.ControlJson(null, null, defaultPassword: true)
            .Should().Be("""{"type":"control","tier1Until":null,"tier2Until":null,"defaultPassword":true}""");
        CommandMessageRules.ControlJson(1_791_587_200_000, 1_791_580_300_000, defaultPassword: false)
            .Should().Be("""{"type":"control","tier1Until":1791587200000,"tier2Until":1791580300000,"defaultPassword":false}""");
        CommandMessageRules.ControlKey.Should().Be("control");
    }

    [Fact]
    public void sendRejected逐字()
    {
        CommandMessageRules.SendRejectedJson("0123456789abcdef0123456789abcdef", "原本的訊息")
            .Should().Be("""{"type":"sendRejected","conversationId":"0123456789abcdef0123456789abcdef","text":"原本的訊息"}""");
    }

    [Fact]
    public void RemoteAuthRules的兩個時間窗是null()
    {
        // D24：control 的 tier1Until／tier2Until 一律送 null（擁有者 2026-10-18 的 Q2「都不用輸入」）。
        // 有人把時間窗改成非 null 時這裡會紅：要先實作每個工作階段的時間窗（control 送真的到期時間、router 回 step_up_required）
        RemoteAuthRules.Tier1WindowMs.Should().BeNull();
        RemoteAuthRules.Tier2WindowMs.Should().BeNull();
    }

    [Fact]
    public void 每個rejected代碼都有不同的toast文字_unknown也有_done沒有()
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var code in RemoteResultCodes.All)
        {
            var text = CommandMessageRules.ToastText(RemoteResultStates.Rejected, code, null);
            text.Should().NotBeNullOrWhiteSpace(code);
            texts[code] = text!;
        }

        texts.Values.Should().OnlyHaveUniqueItems("每個代碼的文字都不同");
        var unknown = CommandMessageRules.ToastText(RemoteResultStates.Unknown, null, null);
        unknown.Should().Be(CommandMessageRules.UnknownText);
        texts.Values.Should().NotContain(unknown);
        CommandMessageRules.ToastText(RemoteResultStates.Done, null, null).Should().BeNull();
        CommandMessageRules.ToastText(RemoteResultStates.Done, null, "電腦的說明").Should().BeNull("done 不提示");
        texts[RemoteResultCodes.Offline].Should().Be("那台電腦目前不在線，指令沒有送出");
        texts[RemoteResultCodes.Unsupported].Should().Be("那台電腦的 Clode Myriagent 還不支援網頁版遠端操作，請先更新那台電腦");
    }

    [Fact]
    public void 不認得的狀態或代碼丟例外()
    {
        var state = () => CommandMessageRules.ToastText("accepted", null, null);
        state.Should().Throw<ArgumentOutOfRangeException>();
        var code = () => CommandMessageRules.ToastText(RemoteResultStates.Rejected, "someday", "電腦的說明");
        code.Should().Throw<ArgumentOutOfRangeException>("新的代碼要先決定瀏覽器看到什麼");
        var missing = () => CommandMessageRules.ToastText(RemoteResultStates.Rejected, null, null);
        missing.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void 電腦的訊息1到1000字才用()
    {
        var offline = CommandMessageRules.ToastText(RemoteResultStates.Rejected, RemoteResultCodes.Offline, null);
        CommandMessageRules.ToastText(RemoteResultStates.Rejected, RemoteResultCodes.Stale, "  這張核准卡已在其他地方處理  ").Should().Be("這張核准卡已在其他地方處理", "去掉前後空白");
        CommandMessageRules.ToastText(RemoteResultStates.Rejected, RemoteResultCodes.Offline, "   ").Should().Be(offline, "只有空白用代碼的文字");
        CommandMessageRules.ToastText(RemoteResultStates.Rejected, RemoteResultCodes.Offline, "").Should().Be(offline);
        var max = new string('字', RemoteResultRules.MaxMessageLength);
        CommandMessageRules.ToastText(RemoteResultStates.Rejected, RemoteResultCodes.Policy, max).Should().Be(max, "1,000 字照用");
        CommandMessageRules.ToastText(RemoteResultStates.Rejected, RemoteResultCodes.Policy, max + "字")
            .Should().Be(CommandMessageRules.ToastText(RemoteResultStates.Rejected, RemoteResultCodes.Policy, null), "超過的本來就過不了 Check，防禦");
        CommandMessageRules.ToastText(RemoteResultStates.Unknown, null, "引擎停止了").Should().Be("引擎停止了");
    }

    [Fact]
    public void RestoresText()
    {
        foreach (var outcome in new[] { RemoteResultStates.Rejected, BrowserErrorCodes.Invalid, BrowserErrorCodes.Unavailable })
        {
            CommandMessageRules.RestoresText("send", outcome).Should().BeTrue(outcome);
            CommandMessageRules.RestoresText(RemoteCommandCatalog.ConversationCreate, outcome).Should().BeTrue($"草稿的第一次 send：{outcome}");
            CommandMessageRules.RestoresText("stop", outcome).Should().BeFalse($"stop 沒有文字：{outcome}");
            CommandMessageRules.RestoresText("answer", outcome).Should().BeFalse("問卷的回答由電腦送 sendRejected");
        }

        foreach (var outcome in new[] { RemoteResultStates.Unknown, RemoteResultStates.Done, BrowserErrorCodes.Conflict, BrowserErrorCodes.NotAvailable })
            CommandMessageRules.RestoresText("send", outcome).Should().BeFalse(outcome);
    }

    [Fact]
    public void FanOutText_數字是0的那一段省略()
    {
        CommandMessageRules.FanOutText(1, 2, 1).Should().Be("已送出：在線的 1 台已送出；不在線的 2 台在 7 天內上線時執行；1 台的程式還不支援，要先更新");
        CommandMessageRules.FanOutText(1, 1, 1).Should().Be("已送出：在線的 1 台已送出；不在線的 1 台在 7 天內上線時執行；1 台的程式還不支援，要先更新");
        CommandMessageRules.FanOutText(2, 0, 0).Should().Be("已送出：在線的 2 台已送出");
        CommandMessageRules.FanOutText(0, 3, 0).Should().Be("已送出：不在線的 3 台在 7 天內上線時執行");
        CommandMessageRules.FanOutText(0, 1, 2).Should().Be("已送出：不在線的 1 台在 7 天內上線時執行；2 台的程式還不支援，要先更新");
        var none = () => CommandMessageRules.FanOutText(0, 0, 3);
        none.Should().Throw<ArgumentOutOfRangeException>("一列都沒寫時回 rejected，不用這段文字");
        var negative = () => CommandMessageRules.FanOutText(-1, 2, 0);
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void DeviceToast_done是success_其他是error_含電腦名稱()
    {
        CommandMessageRules.DeviceToast("工作機", RemoteResultStates.Done, null, null).Should().Be(("『工作機』已登出這個帳戶", "success"));

        var policy = CommandMessageRules.DeviceToast("工作機", RemoteResultStates.Rejected, RemoteResultCodes.Policy, null);
        policy.Level.Should().Be("error");
        policy.Text.Should().Be("『工作機』沒有登出：那台電腦的設定不允許這個遠端操作");
        CommandMessageRules.DeviceToast("筆電", RemoteResultStates.Rejected, RemoteResultCodes.Invalid, "這台沒有這個帳戶").Text
            .Should().Be("『筆電』沒有登出：這台沒有這個帳戶", "電腦的說明優先");

        CommandMessageRules.DeviceToast("筆電", RemoteResultStates.Unknown, null, null)
            .Should().Be(("『筆電』沒有回報登出的結果：可能已經登出，請到那台電腦確認", "error"));
        CommandMessageRules.DeviceToast("筆電", RemoteResultStates.Unknown, null, " 引擎停止了 ").Text.Should().Be("『筆電』沒有回報登出的結果：引擎停止了");

        var act = () => CommandMessageRules.DeviceToast("筆電", "accepted", null, null);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void 所有輸出都是合格的JSON()
    {
        foreach (var json in new[]
                 {
                     CommandMessageRules.CommandResultJson("01J9Q0000000000000000000AA", RemoteResultStates.Unknown, null, CommandMessageRules.UnknownText, null),
                     CommandMessageRules.ControlJson(null, null, false),
                     CommandMessageRules.SendRejectedJson("c", "line1\nline2"),
                 })
        {
            var parsed = JObject.Parse(json);
            parsed["type"]!.Type.Should().Be(JTokenType.String);
        }
    }
}
