using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Rules;

/// <summary>
/// outbox 瘦身的純規則（WP4.3 計畫 D31、步 9）：BatchAck 只處理尾段、結果與整份逐筆相同；尾段原地接回；
/// 已完成的對話列換成骨架（第 3 階段的列不換）、送出前要讀回的骨架
/// </summary>
public sealed class OutboxSliceRulesTests
{
    private const long T0 = 1_791_580_000_000;

    private const string ConvA = "0123456789abcdef0123456789abcdef";

    private const string ConvB = "fedcba9876543210fedcba9876543210";

    [Fact]
    public void 尾段ApplyAck與整份逐筆相同()
    {
        // 固定種子：1,000 組隨機的 outbox（最前面一段已完成、後面各種狀態混著，deviceSeq 偶爾不連續）與確認（逐筆結果、被拒、重複、
        // gap 帶與不帶 expected、seq_regression、invalid、too_large、不認得的碼）
        var random = new Random(20261019);
        var fromAfterStart = 0;
        var gaps = 0;
        for (var round = 0; round < 1000; round++)
        {
            var ordered = RandomOutbox(random);
            var ack = RandomAck(random, ordered);
            if (ack.Error?.Code == SyncErrorCodes.Gap)
                gaps++;
            var now = T0 + 1000 + round;
            var whole = OutboxRules.ApplyAck(ordered, ack, now);

            var first = OutboxSliceRules.FirstActiveIndex(ordered);
            var knownDone = random.Next(first + 1);
            var from = OutboxSliceRules.AckStart(ordered, ack, knownDone);
            from.Should().BeInRange(0, ordered.Count, $"第 {round} 組");
            if (from > 0)
                fromAfterStart++;
            var tail = ordered.Skip(from).ToList();
            var sliced = OutboxRules.ApplyAck(tail, ack, now);

            sliced.Verdict.Should().Be(whole.Verdict, $"第 {round} 組的判斷要和整份相同（{Describe(ordered, ack)}）");
            var spliced = ordered.ToList();
            OutboxSliceRules.Splice(spliced, from, sliced.Updated);
            spliced.Should().Equal(whole.Updated, $"第 {round} 組接回尾段之後要和整份處理逐筆相同（{Describe(ordered, ack)}）");
        }

        fromAfterStart.Should().BeGreaterThan(300, "尾段大多不是從頭開始（否則這個測試證明不了什麼）");
        gaps.Should().BeGreaterThan(100);
    }

    [Fact]
    public void Splice長度與順序不變()
    {
        var ordered = Enumerable.Range(1, 6).Select(i => Conv(i, i <= 3 ? OutboxState.Acked : OutboxState.Sent)).ToList();
        var tail = ordered.Skip(3).Select(e => e with { State = OutboxState.Acked, ServerSeq = e.DeviceSeq + 100, AckedAt = T0 }).ToList();
        var before = ordered.Take(3).ToList();

        OutboxSliceRules.Splice(ordered, 3, tail);

        ordered.Should().HaveCount(6);
        ordered.Select(e => e.DeviceSeq).Should().Equal(1, 2, 3, 4, 5, 6);
        ordered.Take(3).Should().Equal(before, "尾段之前的列不動");
        ordered.Skip(3).Should().Equal(tail);
    }

    [Fact]
    public void Splice_尾段沒接到最後或deviceSeq不同_丟例外而且不改()
    {
        var ordered = Enumerable.Range(1, 4).Select(i => Conv(i, OutboxState.Sent)).ToList();
        var snapshot = ordered.ToList();

        var shortTail = () => OutboxSliceRules.Splice(ordered, 1, ordered.Skip(2).ToList());
        shortTail.Should().Throw<ArgumentException>();
        var negative = () => OutboxSliceRules.Splice(ordered, -1, ordered.ToList());
        negative.Should().Throw<ArgumentException>();
        var wrongSeq = () => OutboxSliceRules.Splice(ordered, 2, [Conv(3, OutboxState.Acked), Conv(5, OutboxState.Acked)]);
        wrongSeq.Should().Throw<ArgumentException>();

        ordered.Should().Equal(snapshot, "不合格的尾段一筆都不接");
        OutboxSliceRules.Splice(ordered, 4, []);
        ordered.Should().Equal(snapshot, "空的尾段接在最後");
    }

    [Fact]
    public void FirstActiveIndex_第一個未完成的列_全部完成回Count_從已知的位置起找()
    {
        OutboxSliceRules.FirstActiveIndex([]).Should().Be(0);
        OutboxSliceRules.FirstActiveIndex([Conv(1, OutboxState.Acked), Conv(2, OutboxState.Rejected)]).Should().Be(2);
        OutboxSliceRules.FirstActiveIndex([Conv(1, OutboxState.Acked), Conv(2, OutboxState.Sent), Conv(3, OutboxState.Acked)]).Should().Be(1);
        OutboxSliceRules.FirstActiveIndex([Conv(1, OutboxState.Pending)]).Should().Be(0);
        OutboxSliceRules.FirstActiveIndex([Conv(1, OutboxState.Acked), Conv(2, OutboxState.Retry)]).Should().Be(1);

        var mixed = new[] { Conv(1, OutboxState.Acked), Conv(2, OutboxState.Acked), Conv(3, OutboxState.Pending) };
        OutboxSliceRules.FirstActiveIndex(mixed, 2).Should().Be(2);
        OutboxSliceRules.FirstActiveIndex(mixed, 1).Should().Be(2);
        OutboxSliceRules.FirstActiveIndex(mixed, 9).Should().Be(3, "超過範圍的已知位置夾到 Count");
        OutboxSliceRules.FirstActiveIndex(mixed, -3).Should().Be(2, "負的從頭找");
    }

    [Fact]
    public void AckStart_一般是第一個未完成的列_gap從期待值之前的最後一列起()
    {
        var ordered = new[]
        {
            Conv(5, OutboxState.Acked), Conv(6, OutboxState.Acked), Conv(7, OutboxState.Acked), Conv(8, OutboxState.Sent), Conv(9, OutboxState.Pending),
        };
        OutboxSliceRules.AckStart(ordered, Ack([new BatchEventResult(8, 100)])).Should().Be(3);
        OutboxSliceRules.AckStart(ordered, Ack([], new BatchError(SyncErrorCodes.SeqRegression))).Should().Be(3);
        OutboxSliceRules.AckStart(ordered, Ack([], new BatchError(SyncErrorCodes.Gap))).Should().Be(3, "gap 沒帶 expected 是重連，只改在途的");
        OutboxSliceRules.AckStart(ordered, Ack([], new BatchError(SyncErrorCodes.Gap, 6))).Should().Be(0, "期待 6：5 號是期待值之前的最後一列");
        OutboxSliceRules.AckStart(ordered, Ack([], new BatchError(SyncErrorCodes.Gap, 7))).Should().Be(1);
        OutboxSliceRules.AckStart(ordered, Ack([], new BatchError(SyncErrorCodes.Gap, 5))).Should().Be(0, "沒有比期待值小的列：從頭（第一列判斷伺服器缺不缺）");
        OutboxSliceRules.AckStart(ordered, Ack([], new BatchError(SyncErrorCodes.Gap, 3))).Should().Be(0);
        OutboxSliceRules.AckStart(ordered, Ack([], new BatchError(SyncErrorCodes.Gap, 9))).Should().Be(3, "期待值之前的最後一列比第一個未完成的晚：照第一個未完成的");
        OutboxSliceRules.AckStart(ordered, Ack([], new BatchError(SyncErrorCodes.Gap, 50))).Should().Be(3);
        OutboxSliceRules.AckStart([], Ack([], new BatchError(SyncErrorCodes.Gap, 4))).Should().Be(0);
    }

    [Fact]
    public void 骨架_已完成的對話列_payload與雜湊是空的_已確認的拿掉合併鍵_被拒的留著()
    {
        var acked = Conv(1, OutboxState.Acked) with { ServerSeq = 77, AckedAt = T0 };
        var skeleton = OutboxSliceRules.Skeleton(acked);
        skeleton.Payload.Should().BeEmpty();
        skeleton.PayloadSha256.Should().BeEmpty();
        skeleton.CoalesceKey.Should().BeNull("已確認的列不再合併、也不再找回項目");
        (skeleton with { Payload = acked.Payload, PayloadSha256 = acked.PayloadSha256, CoalesceKey = acked.CoalesceKey }).Should().Be(acked, "其他欄位不變");
        OutboxSliceRules.IsSkeleton(skeleton).Should().BeTrue();
        OutboxSliceRules.IsSkeleton(acked).Should().BeFalse();

        var rejected = Conv(2, OutboxState.Rejected) with { RejectReason = new string(ConversationRejectCodes.Fenced.ToCharArray()), AckedAt = T0 };
        var rejectedSkeleton = OutboxSliceRules.Skeleton(rejected);
        rejectedSkeleton.CoalesceKey.Should().Be(rejected.CoalesceKey, "被拒的留著合併鍵（D36 要找回項目）");
        rejectedSkeleton.RejectReason.Should().BeSameAs(ConversationRejectCodes.Fenced, "拒絕碼換成共用的常數");
        rejectedSkeleton.Payload.Should().BeEmpty();
    }

    [Fact]
    public void 骨架_種類與編碼換成常數_串流與對話id經共用函式()
    {
        var entry = Conv(1, OutboxState.Acked) with
        {
            Kind = new string(ConversationEventKinds.Item.ToCharArray()),
            Enc = new string(PayloadRules.EncGzip.ToCharArray()),
            Stream = new string(SyncStreams.Conversation(ConvA).ToCharArray()),
            ConversationId = new string(ConvA.ToCharArray()),
        };
        var pool = new Dictionary<string, string>(StringComparer.Ordinal);
        string Share(string s) => pool.TryGetValue(s, out var shared) ? shared : (pool[s] = s);
        var first = OutboxSliceRules.Skeleton(entry, Share);
        var second = OutboxSliceRules.Skeleton(entry with
        {
            DeviceSeq = 2,
            Stream = new string(entry.Stream.ToCharArray()),
            ConversationId = new string(ConvA.ToCharArray()),
        }, Share);

        first.Kind.Should().BeSameAs(ConversationEventKinds.Item);
        first.Enc.Should().BeSameAs(PayloadRules.EncGzip);
        second.Stream.Should().BeSameAs(first.Stream, "同一個對話的串流共用一份字串");
        second.ConversationId.Should().BeSameAs(first.ConversationId);
        OutboxSliceRules.Skeleton(entry).Stream.Should().BeSameAs(entry.Stream, "沒給共用函式就照原本的");
    }

    [Fact]
    public void 骨架_第3階段的列與未完成的列不能換()
    {
        var catalog = new OutboxEntry(1, SyncStreams.Catalog, SyncEventKinds.CliState, PayloadRules.EncJson, "{}", PayloadRules.Sha256Hex("{}"), null, OutboxState.Acked, T0);
        OutboxSliceRules.CanSkeleton(catalog).Should().BeFalse("第 3 階段的列一律不換");
        var act = () => OutboxSliceRules.Skeleton(catalog);
        act.Should().Throw<ArgumentException>();
        foreach (var state in new[] { OutboxState.Pending, OutboxState.Retry, OutboxState.Sent })
        {
            OutboxSliceRules.CanSkeleton(Conv(1, state)).Should().BeFalse(state.ToString());
            var notDone = () => OutboxSliceRules.Skeleton(Conv(1, state));
            notDone.Should().Throw<ArgumentException>();
        }

        OutboxSliceRules.IsSkeleton(catalog with { Payload = string.Empty, PayloadSha256 = string.Empty }).Should().BeFalse("沒有對話的列不是骨架");
    }

    [Fact]
    public void Restore_補回內容_狀態照記憶體的_deviceSeq不同丟例外()
    {
        var stored = Conv(3, OutboxState.Acked) with { Enc = PayloadRules.EncGzip };
        var skeleton = OutboxSliceRules.Skeleton(stored with { ServerSeq = 9, AckedAt = T0 }) with { State = OutboxState.Retry, ServerSeq = null, AckedAt = null };

        var restored = OutboxSliceRules.Restore(skeleton, stored);

        restored.Should().Be(stored with { State = OutboxState.Retry });
        OutboxSliceRules.IsSkeleton(restored).Should().BeFalse();
        var wrong = () => OutboxSliceRules.Restore(skeleton, stored with { DeviceSeq = 4 });
        wrong.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ReloadNeeded_第一個待送的列起_最多一批_只列骨架()
    {
        var full = Conv(1, OutboxState.Acked);
        var s = (long seq, OutboxState state) => OutboxSliceRules.Skeleton(Conv(seq, OutboxState.Acked)) with { State = state };
        var ordered = new List<OutboxEntry>
        {
            OutboxSliceRules.Skeleton(full), s(2, OutboxState.Sent), s(3, OutboxState.Retry), Conv(4, OutboxState.Retry), s(5, OutboxState.Retry),
            s(6, OutboxState.Retry), Conv(7, OutboxState.Sent), s(8, OutboxState.Retry),
        };

        OutboxSliceRules.ReloadNeeded(ordered, 500).Should().Equal([3L, 5L, 6L], "遇到在途就停（NextBatch 也停在那裡）");
        OutboxSliceRules.ReloadNeeded(ordered, 3).Should().Equal([3L, 5L], "最多一批的筆數");
        OutboxSliceRules.ReloadNeeded(ordered, 500, 1).Should().Equal([3L, 5L, 6L]);
        OutboxSliceRules.ReloadNeeded([OutboxSliceRules.Skeleton(full)], 500).Should().BeEmpty("已完成的骨架不讀回");
        OutboxSliceRules.ReloadNeeded([], 500).Should().BeEmpty();
    }

    [Fact]
    public void ReloadNeeded_涵蓋NextBatch會選的每一列()
    {
        var random = new Random(91019);
        for (var round = 0; round < 300; round++)
        {
            var ordered = RandomOutbox(random)
                .Select(e => OutboxSliceRules.CanSkeleton(e) ? OutboxSliceRules.Skeleton(e) : e)
                .Select(e => random.Next(3) == 0 && OutboxSliceRules.IsSkeleton(e) ? e with { State = OutboxState.Retry } : e)
                .ToList();
            var limits = SyncLimits.Default with { MaxBatchEvents = 1 + random.Next(8) };
            var needed = OutboxSliceRules.ReloadNeeded(ordered, limits.MaxBatchEvents).ToHashSet();
            var batch = OutboxRules.NextBatch(ordered, 0, limits);
            batch.Where(OutboxSliceRules.IsSkeleton).Select(e => e.DeviceSeq).Should().BeSubsetOf(needed, $"第 {round} 組：NextBatch 選的骨架都要先讀回");
        }
    }

    private static List<OutboxEntry> RandomOutbox(Random random)
    {
        var count = random.Next(41);
        var donePrefix = random.Next(count + 1);
        var seq = 1L + random.Next(50);
        var list = new List<OutboxEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var state = i < donePrefix
                ? (random.Next(4) == 0 ? OutboxState.Rejected : OutboxState.Acked)
                : (OutboxState)random.Next(5);
            var conv = random.Next(3) switch { 0 => null, 1 => ConvA, _ => ConvB };
            var entry = conv is null
                ? new OutboxEntry(seq, SyncStreams.Catalog, SyncEventKinds.CliState, PayloadRules.EncJson, $"{{\"n\":{seq}}}", PayloadRules.Sha256Hex($"{{\"n\":{seq}}}"), null, state, T0)
                : Conv(seq, state, conv);
            entry = state switch
            {
                OutboxState.Acked => entry with { ServerSeq = random.Next(3) == 0 ? null : 1000 + seq, AckedAt = T0 + random.Next(100) },
                OutboxState.Rejected => entry with { RejectReason = ConversationRejectCodes.All[random.Next(ConversationRejectCodes.All.Count)], AckedAt = T0 },
                _ => entry,
            };
            list.Add(entry);
            seq += random.Next(12) == 0 ? 2 : 1;
        }

        return list;
    }

    private static BatchAck RandomAck(Random random, IReadOnlyList<OutboxEntry> ordered)
    {
        var lo = ordered.Count > 0 ? ordered[0].DeviceSeq : 1;
        var hi = ordered.Count > 0 ? ordered[^1].DeviceSeq : 1;
        switch (random.Next(10))
        {
            case 0:
            case 1:
            case 2:
                return Ack([], new BatchError(SyncErrorCodes.Gap, lo - 3 + random.Next((int)(hi - lo) + 7)));
            case 3:
                return Ack([], new BatchError(SyncErrorCodes.Gap));
            case 4:
                return Ack([], new BatchError(random.Next(4) switch
                {
                    0 => SyncErrorCodes.SeqRegression,
                    1 => SyncErrorCodes.Invalid,
                    2 => SyncErrorCodes.TooLarge,
                    _ => "weird_new_code",
                }));
            default:
            {
                var results = new List<BatchEventResult>();
                var n = random.Next(12);
                for (var i = 0; i < n; i++)
                {
                    var deviceSeq = lo - 2 + random.Next((int)(hi - lo) + 5);
                    results.Add(random.Next(4) == 0
                        ? new BatchEventResult(deviceSeq, Rejected: ConversationRejectCodes.All[random.Next(ConversationRejectCodes.All.Count)])
                        : new BatchEventResult(deviceSeq, 5000 + i));
                }

                return Ack(results);
            }
        }
    }

    private static BatchAck Ack(IReadOnlyList<BatchEventResult> results, BatchError? error = null) => new("b-1", 0, results, error);

    private static OutboxEntry Conv(long deviceSeq, OutboxState state, string conversationId = ConvA)
    {
        var payload = $$"""{"id":"i{{deviceSeq}}","ord":{{deviceSeq}}}""";
        return new OutboxEntry(deviceSeq, SyncStreams.Conversation(conversationId), ConversationEventKinds.Item, PayloadRules.EncJson, payload, PayloadRules.Sha256Hex(payload),
            $"item:{conversationId}:i{deviceSeq}", state, T0, ConversationId: conversationId, Epoch: 1);
    }

    private static string Describe(IReadOnlyList<OutboxEntry> ordered, BatchAck ack) =>
        $"outbox {string.Join(",", ordered.Select(e => $"{e.DeviceSeq}{e.State.ToString()[0]}"))}；確認 {ack.Error?.Code}/{ack.Error?.Expected} 結果 {string.Join(",", ack.Results.Select(r => $"{r.DeviceSeq}{(r.Rejected is null ? "" : "x")}"))}";
}
