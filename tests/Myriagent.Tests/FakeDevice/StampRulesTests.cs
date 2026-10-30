using Myriagent.Agents;
using Myriagent.Sync;
using FakeDevice;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.FakeDevice;

/// <summary>
/// 串流文字的時間標記（WP4.9 計畫 D8、步 8）：全形括號＋剛好 13 位數字；DLP 不改它、串流的「可以先送的長度」不扣住它
/// （改成 ASCII 括號或拿掉括號的突變會紅），劇本的 stamp、completeEachSegment
/// </summary>
public sealed class StampRulesTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000);

    [Fact]
    public void 格式_全形括號夾住剛好13位數字()
    {
        var stamp = StampRules.Format(At);
        stamp.Should().Be("〔1760000000000〕");
        stamp.Should().HaveLength(StampRules.Length);
        stamp[0].Should().Be('\u3014');
        stamp[^1].Should().Be('\u3015');
        StampRules.Format(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000_000_000)).Should().Be("〔1000000000000〕");
        FluentActions.Invoking(() => StampRules.Format(DateTimeOffset.FromUnixTimeMilliseconds(999_999_999_999))).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => StampRules.Format(DateTimeOffset.FromUnixTimeMilliseconds(10_000_000_000_000))).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void 解析_只認全形括號加剛好13位數字_同Node的parseStamps()
    {
        StampRules.Parse("a〔1760000000000〕b〔123〕c〔17600000000000〕").Should().Equal(1760000000000);
        StampRules.Parse("[1760000000000] (1760000000000) 1760000000000").Should().BeEmpty();
        StampRules.Parse("〔1760000000001〕〔1760000000002〕").Should().Equal(1760000000001, 1760000000002);
        StampRules.Parse("〔１７６００００００００００〕").Should().BeEmpty("全形數字不算");
        StampRules.Parse(null).Should().BeEmpty();
        StampRules.Parse(StampRules.Format(At) + "片段").Should().Equal(At.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void DLP不改標記_串流不扣住結尾的標記()
    {
        var stamp = StampRules.Format(At);
        CredentialLeakRules.Mask("x" + stamp + "y").Should().Be("x" + stamp + "y");
        CredentialLeakRules.HasLeak(stamp).Should().BeFalse();
        // 結尾是標記時整段都可以先送：全形括號不是權杖字元（拿掉括號的話 13 位數字會被當成還沒傳完的權杖扣住）
        CredentialLeakRules.SafeLength("x" + stamp).Should().Be(1 + StampRules.Length);
        CredentialLeakRules.SafeLength("片段1760000000000").Should().Be(2, "對照：沒有括號的數字會被扣住");
        CredentialLeakRules.SafeLength("片段[1760000000000]").Should().Be(17, "ASCII 括號也不是權杖字元：ASCII 括號的突變由格式與解析的測試抓");
        CredentialLeakRules.MaskLive("片段" + stamp).Should().Be("片段" + stamp);
    }

    [Fact]
    public void 劇本_stamp與completeEachSegment()
    {
        var turn = ScriptedTurn.Parse(new JArray(new JObject { ["text"] = "abcd", ["segments"] = 2, ["stamp"] = true, ["completeEachSegment"] = true }));
        var text = turn.Steps.Should().ContainSingle().Which.Should().BeOfType<ScriptedText>().Subject;
        text.Stamp.Should().BeTrue();
        text.CompleteEachSegment.Should().BeTrue();
        var plain = (ScriptedText)ScriptedTurn.Parse(new JArray(new JObject { ["text"] = "abcd" })).Steps[0];
        plain.Stamp.Should().BeFalse();
        plain.CompleteEachSegment.Should().BeFalse();
        FluentActions.Invoking(() => ScriptedTurn.Parse(new JArray(new JObject { ["text"] = "abcd", ["stamp"] = "yes" })))
            .Should().Throw<FormatException>().WithMessage("*stamp*");
    }

    [Fact]
    public async Task 會話_每段開頭有發出當下的時間標記_completeEachSegment每段各成完成的項目()
    {
        var events = new List<AgentEvent>();
        var gate = new object();
        PendingTurn? next = null;
        var session = new ScriptedSession(AgentProvider.Claude, action =>
        {
            lock (gate)
                action();
        }, () => Interlocked.Exchange(ref next, null));
        session.EventReceived += events.Add;
        var settings = new AgentSessionSettings(null, false, null, "fake", _ => { }, null, null, null,
            AgentPermissionMode.Ask, @"C:\", @"C:\", "", false);

        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var stream = new PendingTurn(ScriptedTurn.Parse(new JArray(new JObject { ["text"] = "一二三", ["segments"] = 3, ["stamp"] = true, ["delayMs"] = 5 })));
        next = stream;
        await session.SendAsync(new AgentTurnInput("開始", []), settings);
        await stream.Completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        List<AgentEvent> snapshot;
        lock (gate)
            snapshot = events.ToList();
        var deltas = snapshot.OfType<AgentTextDeltaEvent>().ToList();
        deltas.Should().HaveCount(3);
        deltas.Select(d => d.Delta[StampRules.Length..]).Should().Equal("一", "二", "三");
        var stamps = deltas.SelectMany(d => StampRules.Parse(d.Delta)).ToList();
        stamps.Should().HaveCount(3).And.BeInAscendingOrder().And.OnlyContain(ms => ms >= before && ms <= after);
        var completed = snapshot.OfType<AgentTextCompletedEvent>().Should().ContainSingle().Subject;
        completed.Text.Should().Be(string.Concat(deltas.Select(d => d.Delta)), "完成事件帶加了標記的全文");

        lock (gate)
            events.Clear();
        var each = new PendingTurn(ScriptedTurn.Parse(new JArray(new JObject { ["text"] = "甲乙", ["segments"] = 2, ["completeEachSegment"] = true })));
        next = each;
        await session.SendAsync(new AgentTurnInput("再來", []), settings);
        await each.Completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        lock (gate)
            snapshot = events.ToList();
        var done = snapshot.OfType<AgentTextCompletedEvent>().ToList();
        done.Select(d => d.Text).Should().Equal("甲", "乙");
        done.Select(d => d.BlockId).Distinct().Should().HaveCount(2, "每段各自一個區塊（各成一個項目）");
    }
}
