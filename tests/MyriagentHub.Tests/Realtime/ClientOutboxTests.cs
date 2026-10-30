using System.Collections.Concurrent;
using MyriagentHub.Realtime;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 瀏覽器連線的送出佇列（設計 4.6、14.6；WP3 計畫 D10）：整份取代的合併、init 屏障、上限與溢出、送出幫浦。
/// WP4.2（計畫 D23）：依對話與項目的鍵合併（item 與 removeItem 共用）、conversation 屏障、notify 與 clipboard 跨 init 保留；沒帶鍵的訊息與第 3 階段相同
/// </summary>
public sealed class ClientOutboxTests
{
    private const int MaxMessages = 500;
    private const long MaxBytes = 2 * 1024 * 1024;

    [Fact]
    public void 同型別整份取代的訊息只留最新且位置不變()
    {
        var outbox = new ClientOutbox(MaxMessages, MaxBytes);
        outbox.Enqueue(Msg("accounts", "a1")).Should().Be(ClientOutboxResult.Queued);
        outbox.Enqueue(Msg("devices", "d1")).Should().Be(ClientOutboxResult.Queued);
        outbox.Enqueue(Msg("toast", "t1")).Should().Be(ClientOutboxResult.Queued);
        outbox.Enqueue(Msg("accounts", "a2-longer")).Should().Be(ClientOutboxResult.Merged);
        outbox.Enqueue(Msg("settings", "s1")).Should().Be(ClientOutboxResult.Queued);
        outbox.Enqueue(Msg("cli", "c1")).Should().Be(ClientOutboxResult.Queued);
        outbox.Enqueue(Msg("cli", "c2")).Should().Be(ClientOutboxResult.Merged);

        outbox.Count.Should().Be(5);
        outbox.Bytes.Should().Be(new[] { "a2-longer", "d1", "t1", "s1", "c2" }.Sum(j => (long)j.Length));
        Drain(outbox).Should().Equal("a2-longer", "d1", "t1", "s1", "c2");
        outbox.Bytes.Should().Be(0);
    }

    [Fact]
    public void init清掉之前排著的快照類訊息()
    {
        var outbox = new ClientOutbox(MaxMessages, MaxBytes);
        outbox.Enqueue(Msg("accounts", "a1"));
        outbox.Enqueue(Msg("unknownType", "u1"));
        outbox.Enqueue(Msg("init", "i1")).Should().Be(ClientOutboxResult.Queued);
        outbox.Enqueue(Msg("accounts", "a2"));
        outbox.Enqueue(Msg("accounts", "a3")).Should().Be(ClientOutboxResult.Merged, "init 之後的同型別照樣合併");
        Drain(outbox).Should().Equal(["i1", "a3"], "不認得的種類當成快照的一部分，照舊清掉");

        outbox.Enqueue(Msg("init", "i2"));
        outbox.Enqueue(Msg("devices", "d1"));
        outbox.Enqueue(Msg("init", "i3"));
        Drain(outbox).Should().Equal("i3");
    }

    [Fact]
    public void init保留排著的toast並排在init之後()
    {
        // toast 是對使用者指令的回應、不是快照的一部分：被 init 清掉等於靜默遺失（WP3.7 步驟 1）
        var outbox = new ClientOutbox(MaxMessages, MaxBytes);
        outbox.Enqueue(Msg("accounts", "a1"));
        outbox.Enqueue(Msg("toast", "t1"));
        outbox.Enqueue(Msg("devices", "d1"));
        outbox.Enqueue(Msg("toast", "t2"));
        outbox.Enqueue(Msg("init", "i1")).Should().Be(ClientOutboxResult.Queued);
        outbox.Count.Should().Be(3);
        outbox.Bytes.Should().Be("i1t1t2".Length);
        outbox.Enqueue(Msg("accounts", "a2"));
        Drain(outbox).Should().Equal(["i1", "t1", "t2", "a2"], "init 先送，保留的 toast 依原順序接在後面");
        outbox.Bytes.Should().Be(0);

        // 連續兩則 init：toast 只留一份、跟在最後那則 init 後面
        outbox.Enqueue(Msg("toast", "t3"));
        outbox.Enqueue(Msg("init", "i2"));
        outbox.Enqueue(Msg("init", "i3"));
        Drain(outbox).Should().Equal("i3", "t3");

        ClientOutboxRules.KeptAcrossInit("toast").Should().BeTrue();
        foreach (var type in new[] { "init", "accounts", "devices", "settings", "cli", "unknownType", "" })
            ClientOutboxRules.KeptAcrossInit(type).Should().BeFalse(type);
    }

    [Fact]
    public void 溢出照樣清空toast()
    {
        var outbox = new ClientOutbox(2, MaxBytes);
        outbox.Enqueue(Msg("toast", "t1"));
        outbox.Enqueue(Msg("accounts", "a1"));
        outbox.Enqueue(Msg("devices", "d1")).Should().Be(ClientOutboxResult.Overflow);
        outbox.Count.Should().Be(0, "溢出是慢用戶端的降級：整個佇列清空、重送 init");
    }

    [Fact]
    public void toast不合併()
    {
        var outbox = new ClientOutbox(MaxMessages, MaxBytes);
        outbox.Enqueue(Msg("toast", "t1"));
        outbox.Enqueue(Msg("toast", "t2")).Should().Be(ClientOutboxResult.Queued);
        outbox.Enqueue(Msg("unknownType", "u1"));
        outbox.Enqueue(Msg("unknownType", "u2")).Should().Be(ClientOutboxResult.Queued, "不認得的種類不合併");
        Drain(outbox).Should().Equal("t1", "t2", "u1", "u2");
        ClientOutboxRules.MergeKey("init").Should().BeNull();
        ClientOutboxRules.MergeKey("toast").Should().BeNull();
        ClientOutboxRules.MergeKey("accounts").Should().Be("accounts");
    }

    [Fact]
    public void 超過500則回Overflow並清空()
    {
        var outbox = new ClientOutbox(MaxMessages, MaxBytes);
        for (var i = 0; i < MaxMessages; i++)
            outbox.Enqueue(Msg("toast", "t" + i)).Should().Be(ClientOutboxResult.Queued);
        outbox.Count.Should().Be(MaxMessages);
        outbox.Enqueue(Msg("toast", "overflow")).Should().Be(ClientOutboxResult.Overflow);
        outbox.Count.Should().Be(0);
        outbox.Bytes.Should().Be(0);
        outbox.Enqueue(Msg("init", "i")).Should().Be(ClientOutboxResult.Queued, "清空之後重送 init");
    }

    [Fact]
    public void 超過2MB回Overflow並清空()
    {
        var outbox = new ClientOutbox(MaxMessages, MaxBytes);
        outbox.Enqueue(Msg("accounts", new string('a', 1_500_000))).Should().Be(ClientOutboxResult.Queued);
        outbox.Enqueue(Msg("devices", new string('d', 600_000))).Should().Be(ClientOutboxResult.Overflow);
        outbox.Count.Should().Be(0);

        // 合併後變大也算
        outbox.Enqueue(Msg("accounts", new string('a', 1_000_000)));
        outbox.Enqueue(Msg("devices", new string('d', 500_000)));
        outbox.Enqueue(Msg("accounts", new string('a', 1_700_000))).Should().Be(ClientOutboxResult.Overflow);
        outbox.Count.Should().Be(0);
    }

    [Fact]
    public void 佇列是空的時超過上限的單則照收()
    {
        var outbox = new ClientOutbox(MaxMessages, MaxBytes);
        outbox.Enqueue(Msg("accounts", new string('a', 3_000_000))).Should().Be(ClientOutboxResult.Queued);
        outbox.Count.Should().Be(1);
        Drain(outbox).Should().ContainSingle();

        outbox.Enqueue(Msg("accounts", "a"));
        outbox.Enqueue(Msg("init", new string('i', 3_000_000))).Should().Be(ClientOutboxResult.Queued, "init 先清空，等於從空的佇列開始");
        outbox.Count.Should().Be(1);
    }

    [Fact]
    public async Task PumpAsync依序送出()
    {
        var outbox = new ClientOutbox(MaxMessages, MaxBytes);
        var sent = new List<string>();
        var aborted = new List<string>();
        var pump = ClientOutbox.PumpAsync(outbox, (json, _) =>
        {
            lock (sent)
                sent.Add(json);
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(5), aborted.Add, CancellationToken.None);

        for (var i = 0; i < 20; i++)
            outbox.Enqueue(Msg("toast", "t" + i));
        await WaitUntilAsync(() => { lock (sent) return sent.Count == 20; });
        outbox.Enqueue(Msg("init", "i"));
        await WaitUntilAsync(() => { lock (sent) return sent.Count == 21; });
        outbox.Complete();
        await pump.WaitAsync(TimeSpan.FromSeconds(5));

        sent.Should().Equal(Enumerable.Range(0, 20).Select(i => "t" + i).Append("i"));
        aborted.Should().BeEmpty();
    }

    [Fact]
    public async Task 送出逾時就中斷連線()
    {
        var outbox = new ClientOutbox(MaxMessages, MaxBytes);
        var aborted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource();
        var pump = ClientOutbox.PumpAsync(outbox, (_, _) => never.Task, TimeSpan.FromMilliseconds(100), r => aborted.TrySetResult(r), CancellationToken.None);

        outbox.Enqueue(Msg("accounts", "a"));
        outbox.Enqueue(Msg("devices", "d"));
        (await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("send_timeout");
        await pump.WaitAsync(TimeSpan.FromSeconds(5));
        outbox.Count.Should().Be(0, "中斷後丟掉剩下的");
        outbox.Enqueue(Msg("toast", "late"));
        outbox.Count.Should().Be(0, "中斷後不再收");
    }

    [Fact]
    public async Task 送出時尊重取消權杖的也算逾時()
    {
        var outbox = new ClientOutbox(MaxMessages, MaxBytes);
        var aborted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = ClientOutbox.PumpAsync(outbox, (_, ct) => Task.Delay(Timeout.Infinite, ct), TimeSpan.FromMilliseconds(100),
            r => aborted.TrySetResult(r), CancellationToken.None);
        outbox.Enqueue(Msg("accounts", "a"));
        (await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("send_timeout");
        await pump.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task 送出例外就中斷連線()
    {
        var outbox = new ClientOutbox(MaxMessages, MaxBytes);
        var aborted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var pump = ClientOutbox.PumpAsync(outbox, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromException(new IOException("連線已關閉"));
        }, TimeSpan.FromSeconds(5), r => aborted.TrySetResult(r), CancellationToken.None);

        outbox.Enqueue(Msg("toast", "t1"));
        outbox.Enqueue(Msg("toast", "t2"));
        (await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("send_failed");
        await pump.WaitAsync(TimeSpan.FromSeconds(5));
        calls.Should().Be(1, "第一則失敗就停");
    }

    [Fact]
    public async Task abort丟例外也不讓幫浦丟例外()
    {
        var outbox = new ClientOutbox(MaxMessages, MaxBytes);
        var pump = ClientOutbox.PumpAsync(outbox, (_, _) => throw new InvalidOperationException("同步丟出"),
            TimeSpan.FromSeconds(5), _ => throw new InvalidOperationException("abort 失敗"), CancellationToken.None);
        outbox.Enqueue(Msg("toast", "t1"));
        await pump.WaitAsync(TimeSpan.FromSeconds(5));
        pump.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task Complete後PumpAsync結束()
    {
        var outbox = new ClientOutbox(MaxMessages, MaxBytes);
        var sent = new ConcurrentQueue<string>();
        var aborted = new List<string>();
        var pump = ClientOutbox.PumpAsync(outbox, (json, _) =>
        {
            sent.Enqueue(json);
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(5), aborted.Add, CancellationToken.None);

        outbox.Enqueue(Msg("toast", "t1"));
        outbox.Complete();
        await pump.WaitAsync(TimeSpan.FromSeconds(5));
        sent.Should().Equal("t1");
        aborted.Should().BeEmpty("正常結束不中斷連線");
        (await outbox.WaitAsync(CancellationToken.None)).Should().BeFalse();

        var cancelled = new ClientOutbox(MaxMessages, MaxBytes);
        using var cts = new CancellationTokenSource();
        var other = ClientOutbox.PumpAsync(cancelled, (_, _) => Task.CompletedTask, TimeSpan.FromSeconds(5), aborted.Add, cts.Token);
        cts.Cancel();
        await other.WaitAsync(TimeSpan.FromSeconds(5));
        other.IsCompletedSuccessfully.Should().BeTrue("取消時直接結束、不丟例外");
        aborted.Should().BeEmpty();
    }

    [Fact]
    public async Task 同時入列與出列不遺失也不重複()
    {
        const int perThread = 10_000;
        var outbox = new ClientOutbox(int.MaxValue, long.MaxValue);
        var received = new ConcurrentBag<string>();
        var aborted = new List<string>();
        var pump = ClientOutbox.PumpAsync(outbox, (json, _) =>
        {
            received.Add(json);
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(30), aborted.Add, CancellationToken.None);

        var producers = Enumerable.Range(0, 2).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < perThread; i++)
                outbox.Enqueue(Msg("toast", $"{t}:{i}"));
        })).ToArray();
        await Task.WhenAll(producers);
        outbox.Complete();
        await pump.WaitAsync(TimeSpan.FromSeconds(30));

        aborted.Should().BeEmpty();
        received.Should().HaveCount(2 * perThread);
        received.Should().OnlyHaveUniqueItems();
        outbox.Count.Should().Be(0);
    }

    // ───── WP4.2：依對話與項目合併、對話層的屏障、通知類（計畫 D23） ─────

    private const string C1 = "0123456789abcdef0123456789abcdef";

    private const string C2 = "fedcba9876543210fedcba9876543210";

    [Fact]
    public void item同鍵合併_位置不變_位元組重算()
    {
        var outbox = new ClientOutbox(MaxMessages, MaxBytes);
        outbox.Enqueue(Item(C1, "i1", "v1")).Should().Be(ClientOutboxResult.Queued);
        outbox.Enqueue(Msg("toast", "t1"));
        outbox.Enqueue(Header(C1, "h1"));
        outbox.Enqueue(Item(C1, "i1", "v2-longer")).Should().Be(ClientOutboxResult.Merged);
        outbox.Count.Should().Be(3);
        outbox.Bytes.Should().Be(new[] { "v2-longer", "t1", "h1" }.Sum(j => (long)j.Length));
        outbox.Enqueue(Header(C1, "h2")).Should().Be(ClientOutboxResult.Merged);
        outbox.Enqueue(Composer(C1, "c1")).Should().Be(ClientOutboxResult.Queued);
        outbox.Enqueue(Composer(C1, "c2-更長")).Should().Be(ClientOutboxResult.Merged);
        outbox.Bytes.Should().Be(new[] { "v2-longer", "t1", "h2", "c2-更長" }.Sum(j => (long)System.Text.Encoding.UTF8.GetByteCount(j)), "以 UTF-8 計");
        Drain(outbox).Should().Equal("v2-longer", "t1", "h2", "c2-更長");
        outbox.Bytes.Should().Be(0);
    }

    [Fact]
    public void 不同對話的item不合併()
    {
        var outbox = new ClientOutbox(MaxMessages, MaxBytes);
        outbox.Enqueue(Item(C1, "i1", "a1"));
        outbox.Enqueue(Item(C2, "i1", "b1")).Should().Be(ClientOutboxResult.Queued, "同一個項目 id、不同的對話");
        outbox.Enqueue(Header(C2, "h2"));
        outbox.Enqueue(Header(C1, "h1")).Should().Be(ClientOutboxResult.Queued);
        outbox.Enqueue(Composer(C2, "c2"));
        outbox.Enqueue(Composer(C1, "c1")).Should().Be(ClientOutboxResult.Queued);
        outbox.Enqueue(Item(C2, "i1", "b2")).Should().Be(ClientOutboxResult.Merged);
        Drain(outbox).Should().Equal("a1", "b2", "h2", "h1", "c2", "c1");
    }

    [Fact]
    public void 不同項目不合併()
    {
        var outbox = new ClientOutbox(MaxMessages, MaxBytes);
        outbox.Enqueue(Item(C1, "i1", "a"));
        outbox.Enqueue(Item(C1, "i2", "b")).Should().Be(ClientOutboxResult.Queued);
        outbox.Enqueue(Removal(C1, "i3", "r3")).Should().Be(ClientOutboxResult.Queued);
        outbox.Enqueue(Item(C1, "i1", "a2")).Should().Be(ClientOutboxResult.Merged);
        Drain(outbox).Should().Equal("a2", "b", "r3");
        ClientOutboxRules.ItemKey(C1, "i1").Should().NotBe(ClientOutboxRules.ItemKey(C1, "i2"));
        ClientOutboxRules.HeaderKey(C1).Should().NotBe(ClientOutboxRules.ComposerKey(C1));
    }

    [Fact]
    public void item與removeItem共用鍵()
    {
        // 後到的整筆取代：送了又移除、移除之後又送回來，佇列裡只留最後一則
        var outbox = new ClientOutbox(MaxMessages, MaxBytes);
        outbox.Enqueue(Item(C1, "i1", "v1")).Should().Be(ClientOutboxResult.Queued);
        outbox.Enqueue(Removal(C1, "i1", "r1")).Should().Be(ClientOutboxResult.Merged);
        outbox.Enqueue(Item(C1, "i1", "v2")).Should().Be(ClientOutboxResult.Merged);
        outbox.Count.Should().Be(1);
        Drain(outbox).Should().Equal("v2");

        outbox.Enqueue(Item(C1, "i1", "v1"));
        outbox.Enqueue(Removal(C1, "i1", "r1")).Should().Be(ClientOutboxResult.Merged);
        Drain(outbox).Should().Equal("r1");
        ClientOutboxRules.MergeKey(Item(C1, "i1", "x")).Should().Be(ClientOutboxRules.MergeKey(Removal(C1, "i1", "y"))).And.Be("item:" + C1 + ":i1");
    }

    [Fact]
    public void conversation屏障清掉同對話的增量_不清別的對話_不清toast()
    {
        var outbox = new ClientOutbox(MaxMessages, MaxBytes);
        outbox.Enqueue(Item(C1, "i1", "a1"));
        outbox.Enqueue(Header(C1, "h1"));
        outbox.Enqueue(Item(C2, "i1", "b1"));
        outbox.Enqueue(Msg("toast", "t1"));
        outbox.Enqueue(Removal(C1, "i2", "r1"));
        outbox.Enqueue(Composer(C1, "c1"));
        outbox.Enqueue(Header(C2, "h2"));
        outbox.Enqueue(new ClientOutboxMessage("olderItems", "o1", ConversationId: C1));
        outbox.Enqueue(new ClientOutboxMessage(ClientOutboxRules.NotifyType, "n1", ConversationId: C1));
        outbox.Enqueue(new ClientOutboxMessage("resync", "y1", ConversationId: C1));
        outbox.Enqueue(Msg("accounts", "acc"));
        outbox.Enqueue(Snapshot(C1, "s1")).Should().Be(ClientOutboxResult.Queued);
        outbox.Bytes.Should().Be(new[] { "b1", "t1", "h2", "o1", "n1", "y1", "acc", "s1" }.Sum(j => (long)j.Length), "清掉的不再算位元組");

        // 屏障之後的增量排在快照後面（不併進被清掉的那則）
        outbox.Enqueue(Item(C1, "i1", "a2")).Should().Be(ClientOutboxResult.Queued);
        outbox.Enqueue(Header(C1, "h3")).Should().Be(ClientOutboxResult.Queued);
        Drain(outbox).Should().Equal(["b1", "t1", "h2", "o1", "n1", "y1", "acc", "s1", "a2", "h3"], "只清同一個對話的 item、removeItem、header、composer");

        ClientOutboxRules.IsConversationBarrier("conversation").Should().BeTrue();
        foreach (var type in new[] { "init", "conversations", "item", "olderItems", "conversationClosed", "resync", "" })
            ClientOutboxRules.IsConversationBarrier(type).Should().BeFalse(type);
        foreach (var type in new[] { "item", "removeItem", "header", "composer" })
            ClientOutboxRules.IsConversationIncrement(type).Should().BeTrue(type);
        foreach (var type in new[] { "conversation", "conversations", "olderItems", "conversationClosed", "resync", "notify", "clipboard", "toast", "accounts" })
            ClientOutboxRules.IsConversationIncrement(type).Should().BeFalse(type);
    }

    [Fact]
    public void 同一個對話的兩份快照都保留()
    {
        // 屏障只清增量：前一份快照可能是 newConversation 帶 paneId 的回應，網頁要靠它放分頁
        var outbox = new ClientOutbox(MaxMessages, MaxBytes);
        outbox.Enqueue(Snapshot(C1, "s1"));
        outbox.Enqueue(Item(C1, "i1", "a1"));
        outbox.Enqueue(Snapshot(C1, "s2")).Should().Be(ClientOutboxResult.Queued);
        Drain(outbox).Should().Equal("s1", "s2");
    }

    [Fact]
    public void init屏障保留notify與clipboard_依原順序排到init後面()
    {
        var outbox = new ClientOutbox(MaxMessages, MaxBytes);
        outbox.Enqueue(new ClientOutboxMessage(ClientOutboxRules.NotifyType, "n1", ConversationId: C1));
        outbox.Enqueue(Item(C1, "i1", "a1"));
        outbox.Enqueue(new ClientOutboxMessage(ClientOutboxRules.ClipboardType, "k1", ConversationId: C1));
        outbox.Enqueue(Msg("toast", "t1"));
        outbox.Enqueue(Msg(ClientOutboxRules.ConversationsType, "l1"));
        outbox.Enqueue(Snapshot(C2, "s2"));
        outbox.Enqueue(new ClientOutboxMessage(ClientOutboxRules.NotifyType, "n2", ConversationId: C2));
        outbox.Enqueue(Msg("init", "i1")).Should().Be(ClientOutboxResult.Queued);
        outbox.Enqueue(Header(C1, "h1"));
        Drain(outbox).Should().Equal(["i1", "n1", "k1", "t1", "n2", "h1"], "init 先送，通知類依原順序接在後面；快照與增量清掉");

        foreach (var type in new[] { "toast", "notify", "speak", "clipboard" })
            ClientOutboxRules.KeptAcrossInit(type).Should().BeTrue(type);
        foreach (var type in new[] { "conversation", "conversations", "item", "removeItem", "header", "composer", "olderItems", "conversationClosed", "resync" })
            ClientOutboxRules.KeptAcrossInit(type).Should().BeFalse(type);
    }

    [Fact]
    public void fileRef跨init保留()
    {
        // WP4.7 D44：fileFetch 的回應只送一次（對使用者指令的回應），被 init 清掉等於靜默遺失
        var outbox = new ClientOutbox(MaxMessages, MaxBytes);
        outbox.Enqueue(Msg("files", "f1"));
        outbox.Enqueue(new ClientOutboxMessage("fileRef", "r1", ConversationId: C1));
        outbox.Enqueue(Item(C1, "i1", "a1"));
        outbox.Enqueue(new ClientOutboxMessage("fileRef", "r2", ConversationId: C2));
        outbox.Enqueue(Msg("init", "i1")).Should().Be(ClientOutboxResult.Queued);
        Drain(outbox).Should().Equal(["i1", "r1", "r2"], "init 先送，fileRef 依原順序接在後面；files 是快照類照舊清掉");

        ClientOutboxRules.KeptAcrossInit("fileRef").Should().BeTrue();
        ClientOutboxRules.KeptAcrossInit("files").Should().BeFalse();
        ClientOutboxRules.KeptAcrossInit("fileSearch").Should().BeFalse();
        ClientOutboxRules.KeptAcrossInit("FileRef").Should().BeFalse("種類分大小寫");
    }

    [Fact]
    public void resync被init清掉()
    {
        // web-protocol：init 之後網頁本來就重新 open 畫面上的分頁
        var outbox = new ClientOutbox(MaxMessages, MaxBytes);
        outbox.Enqueue(new ClientOutboxMessage("resync", "y1", ConversationId: C1));
        outbox.Enqueue(new ClientOutboxMessage("resync", "y0"));
        outbox.Enqueue(Msg("init", "i1"));
        Drain(outbox).Should().Equal("i1");
        ClientOutboxRules.KeptAcrossInit("resync").Should().BeFalse();
    }

    [Theory]
    [InlineData("commandResult")]
    [InlineData("sendRejected")]
    [InlineData("accountLogin")]
    public void commandResult_sendRejected_accountLogin跨init保留_排在init後面(string type)
    {
        // WP4.5 計畫 M14、K4：遠端指令的結果與輸出每個指令至多一則，被 init 清掉就再也不會來
        var outbox = new ClientOutbox(MaxMessages, MaxBytes);
        outbox.Enqueue(Msg("accounts", "a1"));
        outbox.Enqueue(Msg(type, "x1"));
        outbox.Enqueue(Item(C1, "i1", "it1"));
        outbox.Enqueue(Msg("toast", "t1"));
        outbox.Enqueue(Msg(type, "x2"));
        outbox.Enqueue(Msg("init", "i1")).Should().Be(ClientOutboxResult.Queued);
        Drain(outbox).Should().Equal(["i1", "x1", "t1", "x2"], "init 先送，通知類依原順序接在後面；同一種不合併");
        ClientOutboxRules.KeptAcrossInit(type).Should().BeTrue(type);
    }

    [Fact]
    public void control不跨init保留()
    {
        // control 是狀態（init 之後閘道會再送一則）：以合併鍵只留最新的一則，被 init 清掉
        var outbox = new ClientOutbox(MaxMessages, MaxBytes);
        outbox.Enqueue(new ClientOutboxMessage(CommandMessageRules.ControlType, "c1", CommandMessageRules.ControlKey));
        outbox.Enqueue(Msg("toast", "t1"));
        outbox.Enqueue(new ClientOutboxMessage(CommandMessageRules.ControlType, "c2", CommandMessageRules.ControlKey)).Should().Be(ClientOutboxResult.Merged);
        Drain(outbox).Should().Equal(["c2", "t1"], "同鍵的取代舊的、位置不變");

        outbox.Enqueue(new ClientOutboxMessage(CommandMessageRules.ControlType, "c3", CommandMessageRules.ControlKey));
        outbox.Enqueue(Msg("toast", "t2"));
        outbox.Enqueue(Msg("init", "i1"));
        Drain(outbox).Should().Equal(["i1", "t2"]);
        ClientOutboxRules.KeptAcrossInit(CommandMessageRules.ControlType).Should().BeFalse();
    }

    [Fact]
    public void 溢出_notify與clipboard一併清掉()
    {
        // 同第 3 階段的 toast：溢出是慢用戶端的降級，整個佇列清空、重送 init（寫進 web-protocol）
        var outbox = new ClientOutbox(3, MaxBytes);
        outbox.Enqueue(new ClientOutboxMessage(ClientOutboxRules.NotifyType, "n1", ConversationId: C1));
        outbox.Enqueue(new ClientOutboxMessage(ClientOutboxRules.ClipboardType, "k1", ConversationId: C1));
        outbox.Enqueue(Msg("toast", "t1"));
        outbox.Enqueue(Item(C1, "i1", "a1")).Should().Be(ClientOutboxResult.Overflow);
        outbox.Count.Should().Be(0);
        outbox.Bytes.Should().Be(0);
    }

    [Fact]
    public void 溢出規則不變()
    {
        // 則數：合併不加則數、不溢出；新的一則超過才溢出
        var outbox = new ClientOutbox(3, MaxBytes);
        outbox.Enqueue(Item(C1, "i1", "a"));
        outbox.Enqueue(Item(C1, "i2", "b"));
        outbox.Enqueue(Item(C1, "i3", "c"));
        outbox.Enqueue(Item(C1, "i1", "a2")).Should().Be(ClientOutboxResult.Merged);
        outbox.Count.Should().Be(3);
        outbox.Enqueue(Item(C1, "i4", "d")).Should().Be(ClientOutboxResult.Overflow);
        outbox.Count.Should().Be(0);

        // 位元組：合併後變大也算
        var bytes = new ClientOutbox(MaxMessages, 10);
        bytes.Enqueue(Item(C1, "i1", "aaaa"));
        bytes.Enqueue(Item(C1, "i2", "bbbb"));
        bytes.Enqueue(Item(C1, "i1", "aaaaaaa")).Should().Be(ClientOutboxResult.Overflow);
        bytes.Count.Should().Be(0);

        // 佇列是空的時單則照收；屏障清掉的不算「原本」
        bytes.Enqueue(Item(C1, "i1", new string('a', 30))).Should().Be(ClientOutboxResult.Queued);
        bytes.Enqueue(Snapshot(C1, new string('s', 30))).Should().Be(ClientOutboxResult.Queued, "排著的只有被這份快照取代的增量");
        bytes.Count.Should().Be(1);
        Drain(bytes);
        bytes.Enqueue(Item(C2, "i1", "bb"));
        bytes.Enqueue(Snapshot(C1, new string('s', 30))).Should().Be(ClientOutboxResult.Overflow, "還有別的對話的訊息排著");
    }

    [Fact]
    public void Key是null時與第3階段相同()
    {
        // 沒帶鍵的訊息照 type 的規則：item 這種不認得的 type 不合併、不帶 ConversationId 的不受屏障影響、沒帶 ConversationId 的 conversation 不清任何東西
        var outbox = new ClientOutbox(MaxMessages, MaxBytes);
        outbox.Enqueue(Msg("item", "a1"));
        outbox.Enqueue(Msg("item", "a2")).Should().Be(ClientOutboxResult.Queued);
        outbox.Enqueue(new ClientOutboxMessage("header", "h1", ConversationId: C1));
        outbox.Enqueue(Msg("conversation", "s0")).Should().Be(ClientOutboxResult.Queued);
        outbox.Enqueue(Snapshot(C1, "s1"));
        Drain(outbox).Should().Equal(["a1", "a2", "s0", "s1"], "沒帶 ConversationId 的 item 不被屏障清掉、h1 被 C1 的快照清掉");

        foreach (var type in new[] { "accounts", "devices", "settings", "cli", "init", "toast", "unknownType", "item", "conversation" })
            ClientOutboxRules.MergeKey(Msg(type, "{}")).Should().Be(ClientOutboxRules.MergeKey(type), type);
        ClientOutboxRules.MergeKey(new ClientOutboxMessage("accounts", "{}", Key: "k")).Should().Be("k", "帶了鍵就用鍵");
    }

    [Fact]
    public void conversations整份取代()
    {
        var outbox = new ClientOutbox(MaxMessages, MaxBytes);
        outbox.Enqueue(Msg("conversations", "l1"));
        outbox.Enqueue(Msg("toast", "t1"));
        outbox.Enqueue(Msg("conversations", "l2")).Should().Be(ClientOutboxResult.Merged);
        Drain(outbox).Should().Equal("l2", "t1");
        ClientOutboxRules.MergeKey("conversations").Should().Be("conversations");
    }

    [Fact]
    public void WouldOverflow_與Enqueue的結果一致_不改佇列()
    {
        // 合併前審查 F2：閘道以它決定 open 的快照要不要先留著。固定種子的隨機操作：同樣操作的兩個佇列，一個先問、一個直接入列，答案要和結果一樣
        var rng = new Random(20261020);
        string[] conversations = ["c1", "c2"];
        for (var run = 0; run < 200; run++)
        {
            var asked = new ClientOutbox(6, 200);
            var direct = new ClientOutbox(6, 200);
            for (var step = 0; step < 60; step++)
            {
                if (rng.Next(4) == 0)
                {
                    asked.TryDequeue(out _);
                    direct.TryDequeue(out _);
                    continue;
                }
                var conversation = conversations[rng.Next(conversations.Length)];
                var json = new string('x', rng.Next(1, 90));
                var message = rng.Next(9) switch
                {
                    0 => Msg("init", json),
                    1 => Msg("toast", json),
                    2 => Msg("accounts", json),
                    3 => Snapshot(conversation, json),
                    4 => Header(conversation, json),
                    5 => Composer(conversation, json),
                    6 => Removal(conversation, "i" + rng.Next(3), json),
                    _ => Item(conversation, "i" + rng.Next(3), json),
                };
                var (count, bytes) = (asked.Count, asked.Bytes);
                var predicted = asked.WouldOverflow(message);
                (asked.Count, asked.Bytes).Should().Be((count, bytes), "不改佇列");
                var result = direct.Enqueue(message);
                predicted.Should().Be(result == ClientOutboxResult.Overflow, $"第 {run} 次第 {step} 步（{message.Type}）");
                asked.Enqueue(message);
            }
        }
    }

    [Fact]
    public void WouldOverflow_屏障清掉同一個對話的增量後才算_空佇列單則照收()
    {
        var outbox = new ClientOutbox(MaxMessages, 100);
        outbox.Enqueue(Item("c1", "i1", new string('x', 60)));
        outbox.WouldOverflow(Snapshot("c1", new string('y', 90))).Should().BeFalse("屏障清掉 c1 的增量之後佇列是空的：單則照收");
        outbox.WouldOverflow(Snapshot("c2", new string('y', 41))).Should().BeTrue("c2 的屏障清不掉 c1 的增量");
        outbox.WouldOverflow(Snapshot("c2", new string('y', 40))).Should().BeFalse("剛好等於上限");
        outbox.WouldOverflow(Item("c1", "i1", new string('z', 100))).Should().BeFalse("同鍵原地取代：只剩它自己");
        outbox.WouldOverflow(Msg("init", new string('z', 500))).Should().BeFalse("init 一律排得進去");
        outbox.Complete();
        outbox.WouldOverflow(Msg("toast", new string('z', 500))).Should().BeFalse("Complete 之後的直接丟掉");
    }

    private static ClientOutboxMessage Item(string conversationId, string itemId, string json) =>
        new(ClientOutboxRules.ItemType, json, ClientOutboxRules.ItemKey(conversationId, itemId), conversationId);

    private static ClientOutboxMessage Removal(string conversationId, string itemId, string json) =>
        new(ClientOutboxRules.RemoveItemType, json, ClientOutboxRules.ItemKey(conversationId, itemId), conversationId);

    private static ClientOutboxMessage Header(string conversationId, string json) =>
        new(ClientOutboxRules.HeaderType, json, ClientOutboxRules.HeaderKey(conversationId), conversationId);

    private static ClientOutboxMessage Composer(string conversationId, string json) =>
        new(ClientOutboxRules.ComposerType, json, ClientOutboxRules.ComposerKey(conversationId), conversationId);

    private static ClientOutboxMessage Snapshot(string conversationId, string json) =>
        new(ClientOutboxRules.ConversationType, json, ConversationId: conversationId);

    private static ClientOutboxMessage Msg(string type, string json) => new(type, json);

    private static List<string> Drain(ClientOutbox outbox)
    {
        var result = new List<string>();
        while (outbox.TryDequeue(out var message))
            result.Add(message.Json);
        return result;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("等不到條件成立");
            await Task.Delay(10);
        }
    }
}
