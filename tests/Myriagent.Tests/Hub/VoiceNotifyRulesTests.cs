using Myriagent.Hub;
using Myriagent.Sync;

namespace Myriagent.Tests.Hub;

/// <summary>語音通知念的句子（沒有字數上限、帶簡要說明；送到伺服器時才截到同步協定的上限）與佇列規則（VoiceNotifyRules）</summary>
public sealed class VoiceNotifyRulesTests
{
    [Fact]
    public void 完成_接回覆的第一句_沒有回覆時只說已完成()
    {
        VoiceNotifyRules.Completed("整理報表", "月報已經匯出成 Excel。另外修正了日期格式。").Should().Be("「整理報表」已完成：月報已經匯出成 Excel");
        VoiceNotifyRules.Completed("整理報表", null).Should().Be("「整理報表」已完成");
        VoiceNotifyRules.Completed("   ", "  ").Should().Be("對話已完成");
    }

    [Fact]
    public void 完成_拿掉括號補充_程式碼_連結網址與Markdown符號()
    {
        VoiceNotifyRules.Completed("語音通知", "語音通知已經加進**桌面版**，並推送到 main（`12087fa`）。")
            .Should().Be("「語音通知」已完成：語音通知已經加進桌面版，並推送到 main");
        VoiceNotifyRules.Completed("文件", "```bash\ndotnet build\n```\n看 [部署手冊](https://example.test/ops) 照做就好。")
            .Should().Be("「文件」已完成：看 部署手冊 照做就好");
    }

    [Fact]
    public void 完成_跳過標題與如下帶出的清單句_拿掉開頭的客套()
    {
        const string reply = "## 摘要\n\n完成了，改動如下：\n\n- 修好登入頁的錯誤訊息\n- 加上測試";
        VoiceNotifyRules.Completed("登入", reply).Should().Be("「登入」已完成：修好登入頁的錯誤訊息");
        VoiceNotifyRules.Completed("登入", "好的。已把登入頁的錯誤訊息改成中文。").Should().Be("「登入」已完成：已把登入頁的錯誤訊息改成中文");
        VoiceNotifyRules.Completed("登入", "完成登入頁改版。").Should().Be("「登入」已完成：完成登入頁改版", "「完成」後面沒有標點，是內容不是客套");
    }

    [Fact]
    public void 完成_第一句太短就接下一句()
        => VoiceNotifyRules.Completed("報表", "改好了。欄位順序照你給的範本排。").Should().Be("「報表」已完成：改好了。欄位順序照你給的範本排");

    [Fact]
    public void 沒有字數上限_標題與第一句整個念()
    {
        var reply = "語音通知已經加進 Clode Myriagent 桌面版的設定頁，並且完成建置與全部單元測試，也推送到 main 讓伺服器自動部署。第二句不念。";
        VoiceNotifyRules.Completed("加入語音通知功能與設定開關", reply)
            .Should().Be("「加入語音通知功能與設定開關」已完成：語音通知已經加進 Clode Myriagent 桌面版的設定頁，並且完成建置與全部單元測試，也推送到 main 讓伺服器自動部署");
        VoiceNotifyRules.Completed("報表", new string('字', 300)).Should().Be("「報表」已完成：" + new string('字', 300));
    }

    [Fact]
    public void 提問_接第一個問題的第一行並保留問號()
    {
        VoiceNotifyRules.Question("整理報表", "要用 **哪個** 格式？\n第二行說明").Should().Be("「整理報表」有問題想問你：要用 哪個 格式？");
        VoiceNotifyRules.Question("整理報表", "  ").Should().Be("「整理報表」有問題想問你");
        VoiceNotifyRules.Question("整理報表", null).Should().Be("「整理報表」有問題想問你");
    }

    [Fact]
    public void 核准_中文說明照念_否則念檔名_英文工具名稱改說執行操作()
    {
        VoiceNotifyRules.Approval("部署", "執行指令", "推送到 main\n\ngit push origin HEAD:main", null).Should().Be("「部署」要執行指令，等你核准：推送到 main");
        VoiceNotifyRules.Approval("部署", "執行指令", "Push to main", null).Should().Be("「部署」要執行指令，等你核准");
        VoiceNotifyRules.Approval("部署", "修改檔案", null, @"C:\repo\src\AgentHub.cs").Should().Be("「部署」要修改檔案，等你核准：AgentHub.cs");
        VoiceNotifyRules.Approval("部署", "mcp__playwright__browser_click", null, null).Should().Be("「部署」要執行操作，等你核准");
    }

    [Fact]
    public void 計畫與失敗()
    {
        VoiceNotifyRules.Plan("同步", "# 重構同步模組\n\n1. 拆出規則").Should().Be("「同步」的計畫等你確認：重構同步模組");
        VoiceNotifyRules.Failed("備份", "資料庫連不上").Should().Be("「備份」執行失敗：資料庫連不上");
        VoiceNotifyRules.Failed("備份", "API Error: 529 overloaded_error").Should().Be("「備份」執行失敗", "英文的技術錯誤不念");
    }

    [Fact]
    public void 每一種句子都不截短()
    {
        var longTitle = new string('題', 40);
        var longText = string.Concat(Enumerable.Repeat("很長的說明，", 30)) + "最後一段";
        var name = "「" + longTitle + "」";
        new[]
        {
            VoiceNotifyRules.Completed(longTitle, longText),
            VoiceNotifyRules.Failed(longTitle, longText),
            VoiceNotifyRules.Question(longTitle, longText),
            VoiceNotifyRules.Approval(longTitle, "執行指令", longText, null),
            VoiceNotifyRules.Plan(longTitle, longText),
        }.Should().OnlyContain(t => t.StartsWith(name, StringComparison.Ordinal) && t.EndsWith("：" + longText, StringComparison.Ordinal));
    }

    [Fact]
    public void ForWire_沒超過同步上限照原樣_超過的在子句邊界截短()
    {
        VoiceNotifyRules.ForWire(null).Should().BeNull();
        VoiceNotifyRules.ForWire(string.Empty).Should().BeEmpty("空字串是不念，照樣帶");
        var exact = "「報表」已完成：" + new string('字', ConversationEventRules.MaxSpeechLength - 8);
        VoiceNotifyRules.ForWire(exact).Should().BeSameAs(exact);

        var line = VoiceNotifyRules.Completed("報表", string.Concat(Enumerable.Repeat("很長的說明，", ConversationEventRules.MaxSpeechLength / 3)));
        line.Length.Should().BeGreaterThan(ConversationEventRules.MaxSpeechLength);
        var wire = VoiceNotifyRules.ForWire(line)!;
        wire.Length.Should().BeLessThanOrEqualTo(ConversationEventRules.MaxSpeechLength);
        wire.Should().StartWith("「報表」已完成：很長的說明").And.EndWith("很長的說明", "在逗號斷、尾端不留標點");
        ConversationEventRules.Validate(ConversationEventKinds.Attention, SyncJson.Serialize(new ConversationAttentionPayload("completed", null, wire))).Should().BeNull();
    }

    [Fact]
    public void Line_完整版帶說明_標題版只有標題與事件()
    {
        var line = VoiceNotifyRules.Line(full => VoiceNotifyRules.Completed("報表", full ? "月報已經匯出。" : null));
        line.Should().Be(new VoiceLine("「報表」已完成：月報已經匯出", "「報表」已完成"));
        line.IsSilent.Should().BeFalse();
        VoiceLine.Silent.IsSilent.Should().BeTrue();
    }

    [Fact]
    public void ForPolicy_完整同步送完整版_其他政策只送標題版_沒有句子不帶_不念是空字串()
    {
        var line = new VoiceLine("「報表」已完成：說明", "「報表」已完成");
        VoiceNotifyRules.ForPolicy(line, ConversationSyncPolicies.Full).Should().Be("「報表」已完成：說明");
        VoiceNotifyRules.ForPolicy(line, ConversationSyncPolicies.Metadata).Should().Be("「報表」已完成");
        VoiceNotifyRules.ForPolicy(line, ConversationSyncPolicies.Paused).Should().Be("「報表」已完成", "權杖 DLP 命中：內容不上線");
        VoiceNotifyRules.ForPolicy(line, null).Should().Be("「報表」已完成");
        VoiceNotifyRules.ForPolicy(VoiceLine.Silent, ConversationSyncPolicies.Full).Should().BeEmpty();
        VoiceNotifyRules.ForPolicy(null, ConversationSyncPolicies.Full).Should().BeNull();
    }

    [Fact]
    public void HeadFor_伺服器替沒帶句子的事件組只有標題的句子()
    {
        VoiceNotifyRules.HeadFor(ConversationAttentionKinds.Completed, "報表").Should().Be("「報表」已完成");
        VoiceNotifyRules.HeadFor(ConversationAttentionKinds.Failed, "報表").Should().Be("「報表」執行失敗");
        VoiceNotifyRules.HeadFor(ConversationAttentionKinds.Question, "報表").Should().Be("「報表」有問題想問你");
        VoiceNotifyRules.HeadFor(ConversationAttentionKinds.Approval, "報表").Should().Be("「報表」要執行操作，等你核准");
        VoiceNotifyRules.HeadFor(ConversationAttentionKinds.Plan, "報表").Should().Be("「報表」的計畫等你確認");
        VoiceNotifyRules.HeadFor("alert", "報表").Should().BeNull();
    }

    [Fact]
    public void Clean_網址換成連結_空白合併_超過上限截斷且不加省略號()
    {
        VoiceNotifyRules.Clean("請看 https://example.test/a?b=1 的\t說明", 60).Should().Be("請看 連結 的 說明");
        VoiceNotifyRules.Clean("`npm test` 要跑嗎", 60).Should().Be("npm test 要跑嗎");
        VoiceNotifyRules.Clean(new string('長', 80), 20).Should().Be(new string('長', 20));
        VoiceNotifyRules.Name(new string('長', 50)).Should().Be("「" + new string('長', 50) + "」", "標題不截短");
        VoiceNotifyRules.Clean(null, 10).Should().BeEmpty();
    }

    [Fact]
    public void 截斷不切開代理對()
    {
        var emoji = char.ConvertFromUtf32(0x1F600);
        VoiceNotifyRules.Clean(new string('字', 9) + emoji, 10).Should().Be(new string('字', 9), "第 10 個字是代理對的前半，連同它一起拿掉");
        VoiceNotifyRules.Fit(new string('字', 9) + emoji + "尾", 10).Should().Be(new string('字', 9));
    }

    [Fact]
    public void ShouldQueue_佇列滿了不排_同一句十秒內不重念()
    {
        var now = new DateTime(2026, 10, 23, 8, 0, 0, DateTimeKind.Utc);
        VoiceNotifyRules.ShouldQueue("「甲」已完成", 0, null, now).Should().BeTrue();
        VoiceNotifyRules.ShouldQueue("「甲」已完成", VoiceNotifyRules.MaxPending, null, now).Should().BeFalse("佇列已滿");
        VoiceNotifyRules.ShouldQueue("「甲」已完成", 1, ("「甲」已完成", now.AddSeconds(-3)), now).Should().BeFalse("剛念過同一句");
        VoiceNotifyRules.ShouldQueue("「甲」已完成", 0, ("「甲」已完成", now - VoiceNotifyRules.RepeatWindow), now).Should().BeTrue("過了重念的間隔");
        VoiceNotifyRules.ShouldQueue("「乙」已完成", 1, ("「甲」已完成", now.AddSeconds(-1)), now).Should().BeTrue("不同句子");
        VoiceNotifyRules.ShouldQueue("「甲」已完成", 0, ("「甲」已完成", now.AddMinutes(5)), now).Should().BeTrue("時鐘倒退時不卡住");
        VoiceNotifyRules.ShouldQueue("  ", 0, null, now).Should().BeFalse();
    }
}
