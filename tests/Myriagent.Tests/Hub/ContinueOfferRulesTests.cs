using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Hub;

/// <summary>
/// continue.json（沒有自動接續、標頭給了「繼續」的記錄，ContinueOfferStore.cs）：接續訊息的內容存進去再讀回來一樣；
/// 沒有內容、不認得的 app 重新啟動種類、清單是 null、沒有說明的檔都讀得了
/// </summary>
public sealed class ContinueOfferRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 3, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void 存進去再讀回來_接續訊息的內容一樣_代理種類照對話現在的()
    {
        using var temp = new TempDirectory("continue-offer");
        var context = new ContinuationContext(AgentProvider.Claude, "2.1.320", "2.1.322",
            [new AgentInterruptedTool("執行指令", "npm test", TimeSpan.FromSeconds(42))], ["核准：寫入檔案"], BackgroundWorkStopped: true,
            AppFrom: "0.2.932", AppTo: "0.2.936", AppRestart: AppRestartKind.Unexpected, WaitingBackground: true, BackgroundTasks: ["執行指令：npm run dev"]);

        ContinueOfferStore.Save(temp.Root, ContinueOfferRules.ToRecord("2.1.320", "2.1.322", "沒有自動接續：中斷已超過 1 小時", context, Now));
        var (status, record) = ContinueOfferStore.TryLoad(temp.Root);

        status.Should().Be(JsonReadStatus.Ok);
        record!.OfferedAt.Should().Be(Now.ToUnixTimeMilliseconds());
        (record.From, record.To, record.Detail).Should().Be(("2.1.320", "2.1.322", "沒有自動接續：中斷已超過 1 小時"));
        var back = ContinueOfferRules.Context(AgentProvider.Codex, record)!;
        back.Provider.Should().Be(AgentProvider.Codex, "代理種類不存：照對話現在的");
        back.Should().BeEquivalentTo(context with { Provider = AgentProvider.Codex });
        back.UserRequested.Should().BeFalse("按下時才設");
        ContinueOfferRules.Fingerprint(record).Should().Be(ContinueOfferRules.Fingerprint(ContinueOfferRules.ToRecord("2.1.320", "2.1.322",
            "沒有自動接續：中斷已超過 1 小時", context, Now.AddHours(1))), "指紋不含時間");
    }

    [Fact]
    public void 沒有接續內容的_讀回來也沒有()
    {
        using var temp = new TempDirectory("continue-offer");
        ContinueOfferStore.Save(temp.Root, ContinueOfferRules.ToRecord(null, null, "說明", context: null, Now));

        var record = ContinueOfferStore.TryLoad(temp.Root).Record!;

        ContinueOfferRules.Context(AgentProvider.Claude, record).Should().BeNull();
        ContinueOfferRules.Fingerprint(record).Should().NotBe(ContinueOfferRules.Fingerprint(ContinueOfferRules.ToRecord(null, null, "別的說明", null, Now)));
    }

    [Fact]
    public void 別的版本寫的檔_清單是null_種類不認得_照樣讀得了()
    {
        using var temp = new TempDirectory("continue-offer");
        temp.WriteFile(ContinueOfferStore.FileName,
            """{"Schema":9,"Detail":"說明","Extra":1,"Context":{"InterruptedTools":null,"PendingRequests":[null,"核准"],"BackgroundTasks":null,"AppRestart":"Hibernated"}}""");

        var (status, record) = ContinueOfferStore.TryLoad(temp.Root);

        status.Should().Be(JsonReadStatus.Ok);
        var context = ContinueOfferRules.Context(AgentProvider.Grok, record!)!;
        context.InterruptedTools.Should().BeEmpty();
        context.PendingRequests.Should().Equal("核准");
        context.BackgroundTasks.Should().BeEmpty();
        context.AppRestart.Should().BeNull("不認得的種類當成不是 app 重新啟動");
    }

    [Fact]
    public void 沒有檔_沒有說明_當成沒有_刪除本來就沒有的也算成功()
    {
        using var temp = new TempDirectory("continue-offer");
        ContinueOfferStore.TryLoad(temp.Root).Should().Be((JsonReadStatus.Missing, (ContinueOfferRecord?)null));
        ContinueOfferStore.Delete(temp.Root);
        ContinueOfferStore.Delete(Path.Combine(temp.Root, "不存在的資料夾"));

        temp.WriteFile(ContinueOfferStore.FileName, """{"Schema":1,"Detail":" "}""");
        ContinueOfferStore.TryLoad(temp.Root).Record.Should().BeNull("沒有說明的不還原");
    }

    [Fact]
    public void 對話的資料夾不在_不重建()
    {
        using var temp = new TempDirectory("continue-offer");
        var gone = Path.Combine(temp.Root, "deleted");

        var save = () => ContinueOfferStore.Save(gone, ContinueOfferRules.ToRecord(null, null, "說明", null, Now));

        save.Should().Throw<DirectoryNotFoundException>();
        Directory.Exists(gone).Should().BeFalse("重建資料夾會讓刪掉的對話在下次啟動被還原");
    }
}
