using Myriagent.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Rules;

/// <summary>
/// 桌面帳戶卡的 token 計數加上其他電腦的（tok2，sync-protocol 14.10）：連結到帳戶目錄的帳戶改成所有電腦的合計並加 here（這台今天的），
/// 沒連結的照舊只有這台，remoteOnly 的帳戶在其他電腦有格時加一筆
/// </summary>
public sealed class TokenViewRulesTests
{
    private const string La1 = "la_0123456789ABCDEFGHJKMNPQRS";
    private const string La2 = "la_1123456789ABCDEFGHJKMNPQRS";
    private const string La3 = "la_2123456789ABCDEFGHJKMNPQRS";

    private static readonly DateOnly Today = new(2026, 10, 23);

    private static JObject Count(long input, long cached, long output) => new() { ["input"] = input, ["cached"] = cached, ["output"] = output };

    private static JObject Local() => new()
    {
        ["day"] = "2026-10-23",
        ["accounts"] = new JObject
        {
            ["acc-1"] = new JObject { ["today"] = Count(100, 40, 10), ["week"] = Count(150, 40, 15), ["month"] = Count(300, 40, 30) },
            ["acc-unlinked"] = new JObject { ["today"] = Count(1, 0, 1), ["week"] = Count(1, 0, 1), ["month"] = Count(1, 0, 1) },
        },
    };

    [Fact]
    public void 連結的帳戶加上其他電腦的_here是這台今天的_沒連結的不動()
    {
        var tokens = Local();
        TokenTallyRow[] others =
        [
            new(La1, "2026-10-23", 20, 5, 2),
            new(La1, "2026-10-20", 7, 0, 1),
            new(La1, "2026-10-01", 3, 0, 3),
            new(La1, "2026-10-24", 999, 0, 9),
            new(La2, "2026-10-23", 50, 0, 5),
        ];

        TokenViewRules.Decorate(tokens, others, new Dictionary<string, string> { ["acc-1"] = La1 }, [], Today);

        var a = (JObject)tokens["accounts"]!["acc-1"]!;
        a["today"]!.ToString(Formatting.None).Should().Be("""{"input":120,"cached":45,"output":12}""");
        a["week"]!.ToString(Formatting.None).Should().Be("""{"input":177,"cached":45,"output":18}""", "10-20 在近 7 天內");
        a["month"]!.ToString(Formatting.None).Should().Be("""{"input":330,"cached":45,"output":36}""", "比今天晚的格不算");
        a["here"]!.ToString(Formatting.None).Should().Be("""{"input":100,"cached":40,"output":10}""", "這台今天的（加之前的 today）");
        var unlinked = (JObject)tokens["accounts"]!["acc-unlinked"]!;
        unlinked["today"]!.ToString(Formatting.None).Should().Be("""{"input":1,"cached":0,"output":1}""");
        unlinked.Should().NotContainKey("here", "沒連結：只有這台");
        ((JObject)tokens["accounts"]!).Should().NotContainKey(La2, "不是 remoteOnly 的邏輯帳戶不列");
        tokens.Should().NotContainKey("scope", "桌面不設 scope（網頁照 here 判斷）");
    }

    [Fact]
    public void 其他電腦都沒有這個帳戶的格_照樣加here()
    {
        var tokens = Local();

        TokenViewRules.Decorate(tokens, [], new Dictionary<string, string> { ["acc-1"] = La1 }, [], Today);

        var a = (JObject)tokens["accounts"]!["acc-1"]!;
        a["today"]!.ToString(Formatting.None).Should().Be("""{"input":100,"cached":40,"output":10}""");
        a["here"]!.ToString(Formatting.None).Should().Be("""{"input":100,"cached":40,"output":10}""", "合計就是這台的，說明照樣寫所有電腦的合計");
    }

    [Fact]
    public void remoteOnly的帳戶_其他電腦有格才列_here是0()
    {
        var tokens = Local();
        TokenTallyRow[] others = [new(La2, "2026-10-23", 50, 10, 5), new(La2, "2026-10-19", 5, 0, 1)];

        TokenViewRules.Decorate(tokens, others, new Dictionary<string, string>(), [La2, La3], Today);

        var a = (JObject)tokens["accounts"]![La2]!;
        a["today"]!.ToString(Formatting.None).Should().Be("""{"input":50,"cached":10,"output":5}""");
        a["week"]!.ToString(Formatting.None).Should().Be("""{"input":55,"cached":10,"output":6}""");
        a["here"]!.ToString(Formatting.None).Should().Be("""{"input":0,"cached":0,"output":0}""");
        ((JObject)tokens["accounts"]!).Should().NotContainKey(La3, "其他電腦沒有格");
    }

    [Fact]
    public void 中樞的計數形狀不對時當0_不丟例外()
    {
        var tokens = new JObject
        {
            ["accounts"] = new JObject
            {
                ["acc-1"] = new JObject { ["today"] = "壞的", ["week"] = new JObject { ["input"] = "x" } },
                ["acc-2"] = "不是物件",
            },
        };

        TokenViewRules.Decorate(tokens, [new TokenTallyRow(La1, "2026-10-23", 1, 0, 1)], new Dictionary<string, string> { ["acc-1"] = La1, ["acc-2"] = La1 }, [], Today);

        tokens["accounts"]!["acc-1"]!["today"]!.ToString(Formatting.None).Should().Be("""{"input":1,"cached":0,"output":1}""");
        tokens["accounts"]!["acc-1"]!["here"]!.ToString(Formatting.None).Should().Be("""{"input":0,"cached":0,"output":0}""");
        tokens["accounts"]!["acc-2"]!.Value<string>().Should().Be("不是物件");
        TokenViewRules.Decorate(new JObject(), [], new Dictionary<string, string>(), [], Today);
    }
}
