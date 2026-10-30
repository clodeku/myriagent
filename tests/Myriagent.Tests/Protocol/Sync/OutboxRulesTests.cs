using Myriagent.Sync;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>
/// outbox（設計 3.6、4.3、10.2）：無間隙 deviceSeq、只合併還沒送過的、批次連續且不超過上限、最多 4 批在途、gap 從期待的重送、
/// 送過的內容凍結、Prove 之後補標已提交、還原後的對帳全部重送、確認後保留 24 小時；伺服器端對一筆上行事件的判斷。
/// </summary>
public sealed class OutboxRulesTests
{
    private const long T0 = 1_791_580_000_000;

    private static readonly SyncLimits Limits = SyncLimits.Default;

    [Fact]
    public void 只合併還沒送過的列()
    {
        OutboxRules.CanCoalesceInto(Entry(1, OutboxState.Pending, key: "link:a")).Should().BeTrue();
        OutboxRules.CanCoalesceInto(Entry(1, OutboxState.Pending, key: null)).Should().BeFalse("沒有合併鍵");
        OutboxRules.CanCoalesceInto(Entry(1, OutboxState.Retry, key: "link:a")).Should().BeFalse("送過一次就凍結：舊連線的批次可能晚一點才被提交");
        OutboxRules.CanCoalesceInto(Entry(1, OutboxState.Sent, key: "link:a")).Should().BeFalse();
        OutboxRules.CanCoalesceInto(Entry(1, OutboxState.Acked, key: "link:a")).Should().BeFalse();
    }

    [Fact]
    public void 批次_從第一個待送的列起連續_接在在途的後面_不超過筆數與位元組()
    {
        var entries = new[] { Entry(1, OutboxState.Acked), Entry(2, OutboxState.Sent), Entry(3, OutboxState.Pending), Entry(4, OutboxState.Pending), Entry(5, OutboxState.Pending) };
        OutboxRules.NextBatch(entries, 1, Limits).Select(e => e.DeviceSeq).Should().Equal(3, 4, 5);

        var many = Enumerable.Range(1, 1200).Select(i => Entry(i, OutboxState.Pending)).ToList();
        OutboxRules.NextBatch(many, 0, Limits).Should().HaveCount(500, "≤500 筆");

        var big = Enumerable.Range(1, 10).Select(i => Entry(i, OutboxState.Pending, payload: new string('x', 100_000))).ToList();
        OutboxRules.NextBatch(big, 0, Limits).Select(e => e.DeviceSeq).Should().Equal(1, 2);

        var generous = Limits with { MaxBatchBytes = 10 * 1024 * 1024 };
        var many100k = Enumerable.Range(1, 20).Select(i => Entry(i, OutboxState.Pending, payload: new string('x', 100_000))).ToList();
        OutboxRules.NextBatch(many100k, 0, generous).Sum(e => e.Payload.Length).Should().BeLessThan(SyncProtocol.DeviceHubMaxMessageBytes - OutboxRules.BatchEnvelopeReserveBytes,
            "伺服器給的上限比單則訊息上限大時，以訊息上限為準（超過伺服器會關掉整條連線）");

        var huge = new[] { Entry(1, OutboxState.Pending, payload: new string('x', 300_000)), Entry(2, OutboxState.Pending) };
        OutboxRules.NextBatch(huge, 0, Limits).Select(e => e.DeviceSeq).Should().Equal([1L], "單筆超過批次上限仍然自己成一批（內嵌上限由寫入端擋）");
    }

    [Fact]
    public void 批次_在途已達4批不送_沒有待送的回空()
    {
        var entries = new[] { Entry(1, OutboxState.Pending) };
        OutboxRules.NextBatch(entries, 4, Limits).Should().BeEmpty();
        OutboxRules.NextBatch(entries, 3, Limits).Should().ContainSingle();
        OutboxRules.NextBatch([Entry(1, OutboxState.Acked), Entry(2, OutboxState.Sent)], 1, Limits).Should().BeEmpty();
        OutboxRules.NextBatch([], 0, Limits).Should().BeEmpty();
    }

    [Fact]
    public void 批次_重送與新的列連成一批_遇到在途就停()
    {
        var entries = new[] { Entry(1, OutboxState.Retry), Entry(2, OutboxState.Retry), Entry(3, OutboxState.Pending), Entry(4, OutboxState.Sent), Entry(5, OutboxState.Pending) };
        OutboxRules.NextBatch(entries, 0, Limits).Select(e => e.DeviceSeq).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void 確認_逐筆標成已提交或被拒_沒結果的不動()
    {
        var entries = new[] { Entry(1, OutboxState.Sent), Entry(2, OutboxState.Sent), Entry(3, OutboxState.Sent), Entry(4, OutboxState.Pending) };
        var ack = new BatchAck("b1", 2, [new BatchEventResult(1, Seq: 88123), new BatchEventResult(2, Rejected: "conflict")]);

        var (updated, verdict) = OutboxRules.ApplyAck(entries, ack, T0);

        verdict.Should().Be(OutboxVerdict.Continue);
        updated[0].Should().Be(entries[0] with { State = OutboxState.Acked, ServerSeq = 88123, AckedAt = T0 });
        updated[1].Should().Be(entries[1] with { State = OutboxState.Rejected, RejectReason = "conflict", AckedAt = T0 });
        updated[2].State.Should().Be(OutboxState.Sent, "還在途");
        updated[3].State.Should().Be(OutboxState.Pending);
    }

    [Fact]
    public void 確認_結果有重複只取第一筆_缺結果清單不崩潰()
    {
        var entries = new[] { Entry(1, OutboxState.Sent) };
        var duplicated = new BatchAck("b", 1, [new BatchEventResult(1, Seq: 10), new BatchEventResult(1, Rejected: "invalid")]);
        OutboxRules.ApplyAck(entries, duplicated, T0).Updated[0].Should().Be(entries[0] with { State = OutboxState.Acked, ServerSeq = 10, AckedAt = T0 });

        var missing = Newtonsoft.Json.JsonConvert.DeserializeObject<BatchAck>("{\"batchId\":\"b\",\"acceptedThrough\":1}", SyncJson.Settings)!;
        var (updated, verdict) = OutboxRules.ApplyAck(entries, missing, T0);
        verdict.Should().Be(OutboxVerdict.Continue);
        updated.Should().Equal(entries);
    }

    [Theory]
    [InlineData("server_busy")]
    [InlineData(SyncErrorCodes.Gap)]
    public void 整批錯誤_不認得的代碼或gap沒帶期待值_在途的改回重送並重連(string code)
    {
        var entries = new[] { Entry(1, OutboxState.Acked, ackedAt: T0), Entry(2, OutboxState.Sent), Entry(3, OutboxState.Pending) };

        var (updated, verdict) = OutboxRules.ApplyAck(entries, new BatchAck("b", 1, [], new BatchError(code)), T0);

        verdict.Should().Be(OutboxVerdict.Reconnect, "新版伺服器多出的代碼不該讓舊版停止同步");
        updated.Select(e => e.State).Should().Equal(OutboxState.Acked, OutboxState.Retry, OutboxState.Pending);
    }

    [Fact]
    public void 確認_重連後才到的舊確認照樣生效()
    {
        var entries = new[] { Entry(1, OutboxState.Retry) };
        var (updated, _) = OutboxRules.ApplyAck(entries, new BatchAck("old", 1, [new BatchEventResult(1, Seq: 7)]), T0);
        updated[0].State.Should().Be(OutboxState.Acked, "伺服器已經提交了，不必再送");
    }

    [Fact]
    public void gap_期待的那筆以前標成已提交_從它起改成重送()
    {
        var entries = new[] { Entry(10, OutboxState.Sent), Entry(11, OutboxState.Sent), Entry(12, OutboxState.Sent), Entry(13, OutboxState.Pending) };

        var (updated, verdict) = OutboxRules.ApplyAck(entries, new BatchAck("b2", 0, [], new BatchError(SyncErrorCodes.Gap, Expected: 11)), T0);

        verdict.Should().Be(OutboxVerdict.Continue);
        updated.Select(e => e.State).Should().Equal(OutboxState.Acked, OutboxState.Retry, OutboxState.Retry, OutboxState.Pending);
        updated[0].ServerSeq.Should().BeNull("伺服器 seq 未知");
        OutboxRules.NextBatch(updated, 0, Limits).Select(e => e.DeviceSeq).Should().Equal(11, 12, 13);
    }

    [Theory]
    [InlineData(SyncErrorCodes.SeqRegression, nameof(OutboxVerdict.StopSeqRegression))]
    [InlineData(SyncErrorCodes.Invalid, nameof(OutboxVerdict.StopInvalidBatch))]
    [InlineData(SyncErrorCodes.TooLarge, nameof(OutboxVerdict.StopInvalidBatch))]
    public void 整批錯誤_停止同步(string code, string verdictName)
    {
        var expected = Enum.Parse<OutboxVerdict>(verdictName);
        var entries = new[] { Entry(1, OutboxState.Sent) };
        var (updated, verdict) = OutboxRules.ApplyAck(entries, new BatchAck("b", 0, [], new BatchError(code)), T0);
        verdict.Should().Be(expected);
        updated.Should().Equal(entries, "不動 outbox");
    }

    [Fact]
    public void Prove_期待的那筆以前補標已提交_在途的改成重送()
    {
        var entries = new[] { Entry(5, OutboxState.Acked, ackedAt: T0 - 1000), Entry(6, OutboxState.Sent), Entry(7, OutboxState.Sent), Entry(8, OutboxState.Pending) };

        var (updated, verdict) = OutboxRules.ApplyProve(entries, nextDeviceSeqExpected: 7, nextDeviceSeq: 9, resync: false, T0);

        verdict.Should().Be(OutboxVerdict.Continue);
        updated.Select(e => e.State).Should().Equal(OutboxState.Acked, OutboxState.Acked, OutboxState.Retry, OutboxState.Pending);
        updated[0].AckedAt.Should().Be(T0 - 1000, "原本已確認的不動");
        updated[1].AckedAt.Should().Be(T0);
    }

    [Fact]
    public void Prove_伺服器期待的比這台發過的大_這台的資料庫倒退了()
    {
        var (_, verdict) = OutboxRules.ApplyProve([Entry(5, OutboxState.Pending)], nextDeviceSeqExpected: 20, nextDeviceSeq: 6, resync: false, T0);
        verdict.Should().Be(OutboxVerdict.StopSeqRegression);
    }

    [Fact]
    public void Prove_伺服器缺了這台已刪掉的事件_不在對帳模式就停止()
    {
        var (_, verdict) = OutboxRules.ApplyProve([Entry(100, OutboxState.Acked, ackedAt: T0)], nextDeviceSeqExpected: 50, nextDeviceSeq: 101, resync: false, T0);
        verdict.Should().Be(OutboxVerdict.StopServerBehind);

        OutboxRules.ApplyProve([], nextDeviceSeqExpected: 101, nextDeviceSeq: 101, resync: false, T0).Verdict.Should().Be(OutboxVerdict.Continue, "outbox 空、伺服器一致");
        OutboxRules.ApplyProve([], nextDeviceSeqExpected: 90, nextDeviceSeq: 101, resync: false, T0).Verdict.Should().Be(OutboxVerdict.StopServerBehind);
    }

    [Fact]
    public void 對帳模式_保留的列全部重送_沒送過的仍可合併()
    {
        var entries = new[] { Entry(100, OutboxState.Acked, ackedAt: T0), Entry(101, OutboxState.Rejected, ackedAt: T0), Entry(102, OutboxState.Sent), Entry(103, OutboxState.Pending) };

        var (updated, verdict) = OutboxRules.ApplyProve(entries, nextDeviceSeqExpected: 50, nextDeviceSeq: 104, resync: true, T0);

        verdict.Should().Be(OutboxVerdict.Continue, "還原後伺服器接受缺口，由快照重送補齊");
        updated.Select(e => e.State).Should().Equal(OutboxState.Retry, OutboxState.Retry, OutboxState.Retry, OutboxState.Pending);
        var batch = OutboxRules.NextBatch(updated, 0, Limits);
        batch.Select(e => e.DeviceSeq).Should().Equal(100, 101, 102, 103);

        // 伺服器（備份的期待值 50，比保留窗舊）：第一筆正好是 Hello 帶的 oldestRetained → 接受缺口，之後照常
        var next = 50L;
        var kinds = new List<IncomingEventKind>();
        foreach (var e in batch)
        {
            var kind = OutboxRules.ClassifyIncoming(next, e.DeviceSeq, null, OutboxRules.EventIdentity(e.ToEvent()), resyncFrom: 100);
            kinds.Add(kind);
            if (kind is IncomingEventKind.Next or IncomingEventKind.GapAccepted)
                next = e.DeviceSeq + 1;
        }

        kinds.Should().Equal(IncomingEventKind.GapAccepted, IncomingEventKind.Next, IncomingEventKind.Next, IncomingEventKind.Next);
        OutboxRules.ClassifyIncoming(50, 101, null, "x", resyncFrom: 100).Should().Be(IncomingEventKind.GapAccepted, "Hello 之後才刪掉 100：一樣是補不回來的缺口");
        OutboxRules.ClassifyIncoming(101, 103, null, "x", resyncFrom: 100).Should().Be(IncomingEventKind.Gap, "期待值越過起點之後照樣檢查跳號");
        OutboxRules.ClassifyIncoming(50, 100, null, "x").Should().Be(IncomingEventKind.Gap, "不在對帳模式不接受缺口");
    }

    [Fact]
    public void 對帳模式_outbox是空的_從Hello的下一個序號接受缺口_追上之前不解除()
    {
        // 閒置超過保留期，outbox 全刪光：Hello 的 oldestRetained 是 null。伺服器還原到期待 50 的備份
        var hello = OutboxRules.Position([], nextDeviceSeq: 200);
        hello.OldestRetainedDeviceSeq.Should().BeNull();
        var resyncFrom = OutboxRules.ResyncFrom(hello);
        resyncFrom.Should().Be(200);

        var (_, verdict) = OutboxRules.ApplyProve([], nextDeviceSeqExpected: 50, nextDeviceSeq: 200, resync: true, T0);
        verdict.Should().Be(OutboxVerdict.Continue);
        OutboxRules.IsResyncDone(50, hello).Should().BeFalse("outbox 是空的不代表對帳完成：還沒收到重送的本機狀態");

        // 第 9 節第 3 步：以新的 deviceSeq 重送本機狀態
        OutboxRules.ClassifyIncoming(50, 200, null, "x", resyncFrom).Should().Be(IncomingEventKind.GapAccepted);
        OutboxRules.IsResyncDone(201, hello).Should().BeTrue();

        // 保留窗不是空的時候也一樣：Hello 當下有的列都提交了才解除
        var retained = OutboxRules.Position([Entry(100, OutboxState.Acked, ackedAt: T0), Entry(101, OutboxState.Pending)], nextDeviceSeq: 102);
        OutboxRules.ResyncFrom(retained).Should().Be(100);
        OutboxRules.IsResyncDone(101, retained).Should().BeFalse();
        OutboxRules.IsResyncDone(102, retained).Should().BeTrue();
    }

    [Fact]
    public void Prove_伺服器缺了保留窗內已確認的列_改成重送_不會無限循環()
    {
        // 伺服器說期待 6，但 6 在這台已標成提交（伺服器沒有它）：從 6 起重送，內容凍結、不會被判倒退
        var entries = new[] { Entry(5, OutboxState.Acked, ackedAt: T0), Entry(6, OutboxState.Acked, ackedAt: T0), Entry(7, OutboxState.Pending) };

        var (updated, verdict) = OutboxRules.ApplyProve(entries, nextDeviceSeqExpected: 6, nextDeviceSeq: 8, resync: false, T0);

        verdict.Should().Be(OutboxVerdict.Continue);
        updated.Select(e => e.State).Should().Equal(OutboxState.Acked, OutboxState.Retry, OutboxState.Pending);
        updated[1].ServerSeq.Should().BeNull();
        OutboxRules.NextBatch(updated, 0, Limits).Select(e => e.DeviceSeq).Should().Equal(6, 7);

        var (afterGap, gapVerdict) = OutboxRules.ApplyAck(entries, new BatchAck("b", 5, [], new BatchError(SyncErrorCodes.Gap, Expected: 6)), T0);
        gapVerdict.Should().Be(OutboxVerdict.Continue);
        OutboxRules.NextBatch(afterGap, 0, Limits).Select(e => e.DeviceSeq).Should().Equal(6, 7);
    }

    [Fact]
    public void 期待值以前還有沒送過的列_sync_db倒退了_停止而不是當成已提交()
    {
        // sync.db 還原到下一個序號 80 的時候，伺服器已有舊內容的 80～82；離線產生新的 80～90 後連線：期待 83、這台下一個 91，
        // 「期待的比發過的大」攔不到。MarkSent 在送出前寫回，Pending 不可能送過——伺服器那幾筆是倒退前的舊內容，
        // 當成已提交的話新的 80～82 永遠不會送出、也永遠不會觸發 seq_regression
        var entries = Enumerable.Range(80, 11).Select(seq => Entry(seq, OutboxState.Pending)).ToArray();

        var (updated, verdict) = OutboxRules.ApplyProve(entries, nextDeviceSeqExpected: 83, nextDeviceSeq: 91, resync: false, T0);

        verdict.Should().Be(OutboxVerdict.StopSeqRegression);
        updated.Should().BeSameAs(entries, "停止時不改任何一列");
        OutboxRules.ApplyAck(entries, new BatchAck("b", 82, [], new BatchError(SyncErrorCodes.Gap, Expected: 83)), T0).Verdict
            .Should().Be(OutboxVerdict.StopSeqRegression, "gap 走同一套判斷");

        // 送過的列（在途、重送）在期待值以前照樣是已提交
        var sent = new[] { Entry(5, OutboxState.Sent), Entry(6, OutboxState.Retry), Entry(7, OutboxState.Pending) };
        var (rewound, ok) = OutboxRules.ApplyProve(sent, nextDeviceSeqExpected: 7, nextDeviceSeq: 8, resync: false, T0);
        ok.Should().Be(OutboxVerdict.Continue);
        rewound.Select(e => e.State).Should().Equal(OutboxState.Acked, OutboxState.Acked, OutboxState.Pending);
    }

    [Fact]
    public void 已提交與被拒的保留24小時才刪_還沒確認的永遠不刪()
    {
        var entries = new[]
        {
            Entry(1, OutboxState.Acked, ackedAt: T0 - OutboxRules.RetentionMs),
            Entry(2, OutboxState.Rejected, ackedAt: T0 - OutboxRules.RetentionMs - 1),
            Entry(3, OutboxState.Acked, ackedAt: T0 - OutboxRules.RetentionMs + 1),
            Entry(4, OutboxState.Sent),
            Entry(5, OutboxState.Pending),
        };

        OutboxRules.Prunable(entries, T0).Should().Equal(1, 2);

        var holed = new[] { Entry(1, OutboxState.Acked, ackedAt: T0 - OutboxRules.RetentionMs), Entry(2, OutboxState.Sent), Entry(3, OutboxState.Acked, ackedAt: T0 - OutboxRules.RetentionMs) };
        OutboxRules.Prunable(holed, T0).Should().Equal([1L], "只刪最前面連續的一段，保留窗不能有洞（Hello 的 oldestRetained 與 Prove 的判斷都靠它）");
    }

    [Fact]
    public void Hello的outbox位置()
    {
        var entries = new[] { Entry(7, OutboxState.Acked, ackedAt: T0), Entry(8, OutboxState.Sent), Entry(9, OutboxState.Pending) };
        OutboxRules.Position(entries, 10).Should().Be(new OutboxPosition(10, 8, 7));
        OutboxRules.Position([], 10).Should().Be(new OutboxPosition(10, null, null));
    }

    [Fact]
    public void 伺服器_批次結構檢查()
    {
        var good = new Batch("b", [Entry(5, OutboxState.Pending).ToEvent(), Entry(6, OutboxState.Pending).ToEvent()]);
        OutboxRules.CheckBatchShape(good, Limits).Should().BeNull();
        OutboxRules.CheckBatchShape(good with { Events = [] }, Limits).Should().Be(SyncErrorCodes.Invalid);
        OutboxRules.CheckBatchShape(good with { BatchId = "" }, Limits).Should().Be(SyncErrorCodes.Invalid);
        OutboxRules.CheckBatchShape(good with { Events = [good.Events[1], good.Events[0]] }, Limits).Should().Be(SyncErrorCodes.Invalid, "不連續");
        OutboxRules.CheckBatchShape(good with { Events = [good.Events[0], good.Events[0]] }, Limits).Should().Be(SyncErrorCodes.Invalid);
        OutboxRules.CheckBatchShape(good with { Events = [good.Events[0] with { PayloadSha256 = "ABC" }] }, Limits).Should().Be(SyncErrorCodes.Invalid);
        OutboxRules.CheckBatchShape(good with { Events = [good.Events[0] with { DeviceSeq = 0 }] }, Limits).Should().Be(SyncErrorCodes.Invalid);
        var tooMany = new Batch("b", Enumerable.Range(1, 501).Select(i => Entry(i, OutboxState.Pending).ToEvent()).ToList());
        OutboxRules.CheckBatchShape(tooMany, Limits).Should().Be(SyncErrorCodes.TooLarge);
        OutboxRules.CheckBatchShape(null, Limits).Should().Be(SyncErrorCodes.Invalid);
    }

    [Fact]
    public void 伺服器_上行事件的判斷_冪等_倒退_缺口_還原後缺紀錄()
    {
        var a = OutboxRules.EventIdentity(Entry(9, OutboxState.Pending).ToEvent());
        var b = OutboxRules.EventIdentity(Entry(9, OutboxState.Pending, payload: "{\"a\":2}").ToEvent());

        OutboxRules.ClassifyIncoming(10, 10, null, a).Should().Be(IncomingEventKind.Next);
        OutboxRules.ClassifyIncoming(10, 9, a, a).Should().Be(IncomingEventKind.Duplicate);
        OutboxRules.ClassifyIncoming(10, 9, a, b).Should().Be(IncomingEventKind.Regression);
        OutboxRules.ClassifyIncoming(10, 11, null, a).Should().Be(IncomingEventKind.Gap);
        OutboxRules.ClassifyIncoming(10, 5, null, a, resyncFrom: 3).Should().Be(IncomingEventKind.Missing, "對帳模式：備份裡沒有這筆，套用並稽核");
        OutboxRules.ClassifyIncoming(10, 5, null, a).Should().Be(IncomingEventKind.Stale, "不在對帳模式：不重複套用舊事件");
    }

    [Fact]
    public void 事件身分_payload相同但種類串流對話epoch不同_不是同一筆()
    {
        var e = Entry(9, OutboxState.Pending).ToEvent();
        var same = OutboxRules.EventIdentity(e);
        OutboxRules.EventIdentity(e with { Kind = SyncEventKinds.CliState }).Should().NotBe(same);
        OutboxRules.EventIdentity(e with { Stream = SyncStreams.Usage }).Should().NotBe(same);
        OutboxRules.EventIdentity(e with { ConversationId = "c1" }).Should().NotBe(same);
        OutboxRules.EventIdentity(e with { Epoch = 2 }).Should().NotBe(same);
        OutboxRules.EventIdentity(e with { Enc = PayloadRules.EncGzip, Payload = "壓縮後" }).Should().Be(same, "編碼不同、原文相同仍是同一筆");
        OutboxRules.ClassifyIncoming(10, 9, same, OutboxRules.EventIdentity(e with { Kind = SyncEventKinds.CliState })).Should().Be(IncomingEventKind.Regression);
    }

    private static OutboxEntry Entry(long deviceSeq, OutboxState state, string? key = "k", string payload = "{\"a\":1}", long? ackedAt = null)
    {
        var sha = PayloadRules.Sha256Hex(payload);
        return new OutboxEntry(deviceSeq, SyncStreams.Catalog, SyncEventKinds.LinkUpsert, PayloadRules.EncJson, payload, sha, key, state, T0, AckedAt: ackedAt);
    }
}
