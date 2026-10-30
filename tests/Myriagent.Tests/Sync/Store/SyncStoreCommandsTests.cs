using System.Text;
using Myriagent.Remote;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Rules;
using Myriagent.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Store;

/// <summary>
/// sync.db v2 的遠端指令去重（commands_seen）與遠端操作紀錄（remote_audit）（WP4.4 計畫步 2；D8、D9、D11、D12、D33）：
/// 新指令的 executing 與紀錄同一個交易、重送只加 BeginCommand 那一列的 count、衝突多一列 seen、完成時兩表同一個交易、busy 刪掉 seen 列、
/// 殘留的 executing 改 unknown、8 天與 180 天的修剪邊界、紀錄的分頁與匯出
/// </summary>
public sealed class SyncStoreCommandsTests : IDisposable
{
    private const string Cid = "01J9QZ3K8M5R7T2V4W6X8Y0A1B";
    private const string OtherCid = "01J9QZ3K8M5R7T2V4W6X8Y0A1C";
    private const long Now = 1_791_580_000_000;
    private const long Day = 24L * 60 * 60 * 1000;

    private static readonly ServerInstance Server = new("srv-instance-1", 0);
    private static readonly string DigestA = new('a', 64);
    private static readonly string DigestB = new('b', 64);

    private readonly TempDirectory temp = new("syncstore-commands");
    private readonly TestDevice self = TestDevice.Create("OFFICE-PC");

    private string DbPath => temp.Combine("sync", "sync.db");

    public void Dispose() => temp.Dispose();

    private SyncStore Create() => SyncStore.Create(DbPath, self.DeviceId, 1791500000000, Server, new TrustBundle(3, [self.Entry()]));

    private static CommandAuditFacts Facts(string? commandId = Cid, string? label = "Chrome・Windows", string? detail = null) => new(
        commandId,
        "rename",
        "5f3a0c1d2e3f4a5b6c7d8e9f0a1b2c3d",
        DeclaredTier: 1,
        DefaultPassword: false,
        Iat: Now - 500,
        Created: Now - 1_000,
        detail,
        label is null ? null : new JObject { ["cid"] = commandId, ["issuer"] = new JObject { ["kind"] = "browser", ["label"] = label } }.ToString(Newtonsoft.Json.Formatting.None),
        new string('c', 64),
        new string('s', 300));

    // ── BeginCommand ──

    [Fact]
    public void BeginCommand_新指令_同一交易寫executing與稽核()
    {
        using var store = Create();

        var begin = store.BeginCommand(Cid, DigestA, Facts(), Now);

        begin.Kind.Should().Be(CommandBeginKind.New);
        begin.Row.Should().BeNull();
        begin.AuditSeq.Should().NotBeNull();
        var seen = SeenRows();
        seen.Should().ContainSingle();
        seen[0].Should().Be((Cid, DigestA, "executing", (string?)null, Now));
        var audit = AuditRows();
        audit.Should().ContainSingle();
        audit[0].Seq.Should().Be(begin.AuditSeq!.Value);
        audit[0].At.Should().Be(Now, "與 commands_seen 的 at 相同");
        audit[0].Result.Should().Be("executing");
        audit[0].Step.Should().BeNull();
        audit[0].Count.Should().Be(1);
        audit[0].Sig.Should().HaveLength(SyncCommandRules.MaxSigLength, "簽章截到 200 字");
        audit[0].CommandId.Should().Be(Cid);
    }

    [Fact]
    public void BeginCommand_稽核寫入失敗_commands_seen也沒有()
    {
        using var store = Create();
        store.FaultInjection = point =>
        {
            if (point == "BeginCommand:afterSeen")
                throw new SqliteException("模擬磁碟已滿", 13);
        };

        var act = () => store.BeginCommand(Cid, DigestA, Facts(), Now);

        act.Should().Throw<SyncStoreException>().Which.Failure.Should().Be(SyncStoreFailure.Unavailable);
        store.FaultInjection = null;
        SeenRows().Should().BeEmpty("executing 與紀錄在同一個交易：紀錄寫不進去，seen 也沒有");
        AuditRows().Should().BeEmpty();
        store.BeginCommand(Cid, DigestA, Facts(), Now).Kind.Should().Be(CommandBeginKind.New, "沒有留下半套，之後照樣是新指令");
    }

    [Fact]
    public void BeginCommand_同摘要重送_count加一_回Row()
    {
        using var store = Create();
        var first = store.BeginCommand(Cid, DigestA, Facts(), Now);
        var done = new CommandResult(Cid, RemoteResultStates.Done, Tier: 1, FullAccess: false);
        store.CompleteCommand(Cid, first.AuditSeq!.Value, done, RemoteGateSteps.Handler, "Control", false);

        var again = store.BeginCommand(Cid, DigestA, Facts(), Now + 5_000);

        again.Kind.Should().Be(CommandBeginKind.Duplicate);
        again.AuditSeq.Should().Be(first.AuditSeq);
        again.Row!.ContentSha256.Should().Be(DigestA);
        again.Row.State.Should().Be(RemoteResultStates.Done);
        again.Row.At.Should().Be(Now, "at 是第一次 BeginCommand 的時間");
        SyncCommandRules.ParseResult(Cid, again.Row.ResultJson).Should().Be(done);
        var audit = AuditRows();
        audit.Should().ContainSingle("重送不多寫一列");
        audit[0].Count.Should().Be(2);
        audit[0].Result.Should().Be("done");
    }

    [Fact]
    public void BeginCommand_重送加在Begin寫的那一列_不加在之前驗章失敗的列()
    {
        using var store = Create();
        store.AuditRejected(Facts(detail: "conn 不是收到它的這條連線"), CommandEnvelopeSteps.Connection, RemoteResultCodes.Invalid, Now - 2_000);
        var begin = store.BeginCommand(Cid, DigestA, Facts(), Now);
        // 執行中又收到一則 conn 不符的重簽（重連的競態），與一則內容不同的（衝突）
        store.AuditRejected(Facts(detail: "conn 不是收到它的這條連線"), CommandEnvelopeSteps.Connection, RemoteResultCodes.Invalid, Now + 1_000);
        store.BeginCommand(Cid, DigestB, Facts(), Now + 2_000).Kind.Should().Be(CommandBeginKind.Conflict);

        store.BeginCommand(Cid, DigestA, Facts(), Now + 3_000).Kind.Should().Be(CommandBeginKind.Duplicate);
        store.BeginCommand(Cid, DigestA, Facts(), Now + 4_000).Kind.Should().Be(CommandBeginKind.Duplicate);

        var audit = AuditRows();
        audit.Should().HaveCount(4);
        audit.Single(a => a.Seq == begin.AuditSeq).Count.Should().Be(3, "BeginCommand 寫的那一列");
        audit.Where(a => a.Seq != begin.AuditSeq).Should().OnlyContain(a => a.Count == 1, "驗信封失敗與衝突的列不加");
        audit.Count(a => a.Step == CommandEnvelopeSteps.Connection).Should().Be(2);
        SeenRows().Should().ContainSingle("驗信封失敗不寫 commands_seen");
    }

    [Fact]
    public void BeginCommand_不同摘要_Conflict_原本的列不變_多一列seen()
    {
        using var store = Create();
        var first = store.BeginCommand(Cid, DigestA, Facts(), Now);
        store.CompleteCommand(Cid, first.AuditSeq!.Value, new CommandResult(Cid, RemoteResultStates.Done), RemoteGateSteps.Handler, "Control", false);
        var before = SeenRows();

        var conflict = store.BeginCommand(Cid, DigestB, Facts(), Now + 1_000);

        conflict.Kind.Should().Be(CommandBeginKind.Conflict);
        conflict.Row!.ContentSha256.Should().Be(DigestA);
        SeenRows().Should().Equal(before, "原本的列不變");
        var audit = AuditRows();
        audit.Should().HaveCount(2);
        var row = audit.Single(a => a.Seq == conflict.AuditSeq);
        row.Result.Should().Be(RemoteResultStates.Rejected);
        row.Step.Should().Be(SyncCommandRules.SeenStep);
        row.Code.Should().Be(RemoteResultCodes.Conflict);
        row.At.Should().Be(Now + 1_000);
        audit.Single(a => a.Seq == first.AuditSeq).Count.Should().Be(1);
    }

    // ── CompleteCommand ──

    [Fact]
    public void CompleteCommand_更新兩表_busy刪掉seen列_其他保留()
    {
        using var store = Create();
        var done = store.BeginCommand(Cid, DigestA, Facts(), Now);
        var busy = store.BeginCommand(OtherCid, DigestA, Facts(OtherCid), Now);

        var result = new CommandResult(Cid, RemoteResultStates.Rejected, RemoteResultCodes.Policy, "那台的設定不允許", 2, true);
        store.CompleteCommand(Cid, done.AuditSeq!.Value, result, RemoteGateSteps.Policy, "View", true, detail: "remoteControlLevel 是 View");
        store.CompleteCommand(OtherCid, busy.AuditSeq!.Value, new CommandResult(OtherCid, RemoteResultStates.Rejected, RemoteResultCodes.Busy),
            null, "Control", false);

        var seen = SeenRows();
        seen.Should().ContainSingle("rejected＋busy 什麼都沒執行：刪掉，durable 重簽要能再試");
        seen[0].CommandId.Should().Be(Cid);
        seen[0].State.Should().Be(RemoteResultStates.Rejected);
        SyncCommandRules.ParseResult(Cid, seen[0].ResultJson).Should().Be(result);
        var audit = AuditRows().Single(a => a.Seq == done.AuditSeq);
        audit.Result.Should().Be(RemoteResultStates.Rejected);
        audit.Code.Should().Be(RemoteResultCodes.Policy);
        audit.Step.Should().Be(RemoteGateSteps.Policy);
        audit.Tier.Should().Be(2);
        audit.FullAccess.Should().Be(1);
        audit.Policy.Should().Be("View");
        audit.Paused.Should().Be(1);
        audit.Detail.Should().Be("remoteControlLevel 是 View");
        var busyAudit = AuditRows().Single(a => a.Seq == busy.AuditSeq);
        busyAudit.Result.Should().Be(RemoteResultStates.Rejected);
        busyAudit.Code.Should().Be(RemoteResultCodes.Busy);
        busyAudit.Detail.Should().BeNull("沒給 detail 就不改");

        store.BeginCommand(OtherCid, DigestA, Facts(OtherCid), Now + 5_000).Kind.Should().Be(CommandBeginKind.New, "busy 之後同一個 cid 重新執行");
        store.BeginCommand(Cid, DigestA, Facts(), Now + 5_000).Kind.Should().Be(CommandBeginKind.Duplicate);
    }

    [Fact]
    public void CompleteCommand_各種結果都保留_只有busy不留()
    {
        using var store = Create();
        var results = new List<CommandResult>
        {
            new(Cid, RemoteResultStates.Done),
            new(Cid, RemoteResultStates.Unknown),
        };
        results.AddRange(RemoteResultCodes.All.Select(code => new CommandResult(Cid, RemoteResultStates.Rejected, code)));
        foreach (var result in results)
        {
            store.Dispose();
            File.Delete(DbPath);
            using var fresh = Create();
            var begin = fresh.BeginCommand(Cid, DigestA, Facts(), Now);
            fresh.CompleteCommand(Cid, begin.AuditSeq!.Value, result, RemoteGateSteps.Handler, "Control", false);
            SeenRows().Should().HaveCount(result.Code == RemoteResultCodes.Busy ? 0 : 1, $"{result.State}/{result.Code}");
        }
    }

    [Fact]
    public void CompleteCommand_寫入失敗丟例外_seen仍是executing()
    {
        using var store = Create();
        var begin = store.BeginCommand(Cid, DigestA, Facts(), Now);
        store.FaultInjection = point =>
        {
            if (point == "CompleteCommand:afterSeen")
                throw new SqliteException("模擬磁碟已滿", 13);
        };

        var act = () => store.CompleteCommand(Cid, begin.AuditSeq!.Value, new CommandResult(Cid, RemoteResultStates.Done), RemoteGateSteps.Handler, "Control", false);

        act.Should().Throw<SyncStoreException>().Which.Failure.Should().Be(SyncStoreFailure.Unavailable);
        store.FaultInjection = null;
        SeenRows().Single().State.Should().Be("executing");
        SeenRows().Single().ResultJson.Should().BeNull();
        AuditRows().Single().Result.Should().Be("executing");
    }

    [Fact]
    public void 損毀的錯誤碼是Corrupt()
    {
        using var store = Create();
        store.FaultInjection = point =>
        {
            if (point == "BeginCommand:commit")
                throw new SqliteException("模擬損毀", 11);
        };

        var act = () => store.BeginCommand(Cid, DigestA, Facts(), Now);

        act.Should().Throw<SyncStoreException>().Which.Failure.Should().Be(SyncStoreFailure.Corrupt);
    }

    // ── 啟動回收、修剪 ──

    [Fact]
    public void MarkInterruptedUnknown_只改executing_紀錄的step是handler_result_json是unknown()
    {
        using (var store = Create())
        {
            var done = store.BeginCommand(OtherCid, DigestA, Facts(OtherCid), Now);
            store.CompleteCommand(OtherCid, done.AuditSeq!.Value, new CommandResult(OtherCid, RemoteResultStates.Done), RemoteGateSteps.Handler, "Control", false);
            store.BeginCommand(Cid, DigestA, Facts(), Now); // 執行中當機：沒有完成
        }

        using var reopened = SyncStore.Open(DbPath, self.DeviceId);
        reopened.MarkInterruptedUnknown().Should().Be(1);

        var seen = SeenRows().ToDictionary(r => r.CommandId);
        seen[Cid].State.Should().Be(RemoteResultStates.Unknown);
        SyncCommandRules.ParseResult(Cid, seen[Cid].ResultJson).Should().Be(SyncCommandRules.UnknownResult(Cid));
        seen[OtherCid].State.Should().Be(RemoteResultStates.Done, "已完成的不動");
        var audit = AuditRows().ToDictionary(a => a.CommandId!);
        audit[Cid].Result.Should().Be(RemoteResultStates.Unknown);
        audit[Cid].Step.Should().Be(RemoteGateSteps.Handler);
        audit[OtherCid].Result.Should().Be(RemoteResultStates.Done);
        reopened.MarkInterruptedUnknown().Should().Be(0, "第二次沒有可改的");

        var again = reopened.BeginCommand(Cid, DigestA, Facts(), Now + 1_000);
        again.Kind.Should().Be(CommandBeginKind.Duplicate);
        SyncCommandRules.Decide(DigestA, again.Row, inFlight: false, cached: null)
            .Should().Be(new CommandSeenDecision(CommandSeenKind.Duplicate, SyncCommandRules.UnknownResult(Cid)), "重送回 unknown、不重做");
    }

    [Fact]
    public void Prune_8天與180天的邊界()
    {
        using var store = Create();
        var seenKeep = "01J9QZ3K8M5R7T2V4W6X8Y0A2A";
        var seenDrop = "01J9QZ3K8M5R7T2V4W6X8Y0A2B";
        store.BeginCommand(seenKeep, DigestA, Facts(seenKeep), Now - 8 * Day);
        store.BeginCommand(seenDrop, DigestA, Facts(seenDrop), Now - 8 * Day - 1);
        store.AuditRejected(Facts("audit-keep"), CommandEnvelopeSteps.Signature, RemoteResultCodes.Invalid, Now - 180 * Day);
        store.AuditRejected(Facts("audit-drop"), CommandEnvelopeSteps.Signature, RemoteResultCodes.Invalid, Now - 180 * Day - 1);

        var pruned = store.Prune(Now);

        pruned.Should().Be((1, 1));
        SeenRows().Select(r => r.CommandId).Should().Equal(seenKeep);
        AuditRows().Select(a => a.CommandId).Should().BeEquivalentTo([seenKeep, seenDrop, "audit-keep"], "紀錄保留 180 天，比 seen 長");
        store.Prune(Now).Should().Be((0, 0));
    }

    // ── 讀取與匯出 ──

    [Fact]
    public void LoadRemoteAudit_由新到舊分頁_issuer取自body_masked_limit夾住()
    {
        using var store = Create();
        store.AuditRejected(Facts("a1", "Safari・iOS"), CommandEnvelopeSteps.Time, RemoteResultCodes.Expired, Now + 1);
        store.AuditRejected(Facts("a2", label: null), CommandEnvelopeSteps.Signature, RemoteResultCodes.Invalid, Now + 2);
        store.AuditRejected(Facts("a3", "Edge・Android"), CommandEnvelopeSteps.Time, RemoteResultCodes.Expired, Now + 2);
        var begin = store.BeginCommand(Cid, DigestA, Facts(), Now + 3);

        var page = store.LoadRemoteAudit(null, 3);

        page.Select(e => e.CommandId).Should().Equal(Cid, "a3", "a2");
        page[0].Should().Be(new RemoteAuditEntry(begin.AuditSeq!.Value, Now + 3, Cid, "rename", "5f3a0c1d2e3f4a5b6c7d8e9f0a1b2c3d", "Chrome・Windows",
            1, null, null, null, null, "executing", null, null, false, 1));
        page[1].IssuerLabel.Should().Be("Edge・Android");
        page[1].State.Should().Be(RemoteResultStates.Rejected);
        page[1].Code.Should().Be(RemoteResultCodes.Expired);
        page[1].Step.Should().Be(CommandEnvelopeSteps.Time);
        page[2].IssuerLabel.Should().BeNull("沒解析過的信封沒有 body_masked");

        var next = store.LoadRemoteAudit(page[^1].At, 3);
        next.Select(e => e.CommandId).Should().Equal("a1");
        next[0].IssuerLabel.Should().Be("Safari・iOS");

        store.LoadRemoteAudit(null, 0).Should().HaveCount(1, "limit 夾到至少 1");
        store.LoadRemoteAudit(null, -5).Should().HaveCount(1);
        for (var i = 0; i < SyncStore.MaxRemoteAuditPage + 10; i++)
            store.AuditRejected(Facts("bulk"), CommandEnvelopeSteps.Signature, RemoteResultCodes.Invalid, Now - 10 - i);
        store.LoadRemoteAudit(null, 10_000).Should().HaveCount(SyncStore.MaxRemoteAuditPage, "limit 夾到最多 200");
    }

    [Fact]
    public void LoadRemoteAudit_帶beforeSeq以at與seq翻頁_同一個at的列不跳過也不重複()
    {
        using var store = Create();
        // at：Now+1 一列、Now+2 三列、Now+3 一列（同一個 at 的多列是同一毫秒裡的重試或驗信封失敗）
        foreach (var (cid, at) in new[] { ("t1", Now + 1), ("t2a", Now + 2), ("t2b", Now + 2), ("t2c", Now + 2), ("t3", Now + 3) })
            store.AuditRejected(Facts(cid), CommandEnvelopeSteps.Signature, RemoteResultCodes.Invalid, at);
        var all = store.LoadRemoteAudit(null, 10);
        all.Select(e => e.CommandId).Should().Equal("t3", "t2c", "t2b", "t2a", "t1");

        var walked = new List<RemoteAuditEntry>();
        var page = store.LoadRemoteAudit(null, 2);
        // 最多 10 頁：條件寫錯（例如 seq <=）時最後一頁會一直重複，以斷言失敗、不讓測試卡住
        for (var pages = 0; page.Count > 0 && pages < 10; pages++)
        {
            walked.AddRange(page);
            page = store.LoadRemoteAudit(page[^1].At, 2, page[^1].Seq);
        }

        walked.Select(e => e.Seq).Should().Equal(all.Select(e => e.Seq), "以 (at, seq) 翻頁走完全部，順序與一次讀出相同（ORDER BY at DESC, seq DESC）");
        store.LoadRemoteAudit(Now + 2, 10).Select(e => e.CommandId).Should().Equal(["t1"], "對照：只帶 at 時同一個 at 的其他列被跳過");
        store.LoadRemoteAudit(null, 10, beforeSeq: all[1].Seq).Select(e => e.Seq).Should().Equal(all.Select(e => e.Seq), "沒有 at 時是第一頁，beforeSeq 不看");
    }

    [Fact]
    public void ExportRemoteAudit_JSONL每列一筆_沒有暫存檔留下()
    {
        using var store = Create();
        var begin = store.BeginCommand(Cid, DigestA, Facts(), Now);
        store.CompleteCommand(Cid, begin.AuditSeq!.Value, new CommandResult(Cid, RemoteResultStates.Done, Tier: 1, FullAccess: false), RemoteGateSteps.Handler,
            "Control", false);
        store.AuditRejected(Facts("not-a-ulid", detail: "cid 不是 ULID"), CommandEnvelopeSteps.CommandId, RemoteResultCodes.Invalid, Now + 1);
        var path = temp.Combine("remote-audit", self.DeviceId + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "舊的內容\n");

        store.ExportRemoteAudit(path).Should().Be(2);

        var bytes = File.ReadAllBytes(path);
        bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, "UTF-8 無 BOM");
        bytes.Should().NotContain((byte)'\r', "LF");
        var lines = Encoding.UTF8.GetString(bytes).Split('\n');
        lines.Should().HaveCount(3).And.EndWith(string.Empty, "最後一列也以換行結束");
        var first = JObject.Parse(lines[0]);
        first["commandId"]!.Value<string>().Should().Be(Cid);
        first["result"]!.Value<string>().Should().Be("done");
        first["policy"]!.Value<string>().Should().Be("Control");
        first["count"]!.Value<int>().Should().Be(1);
        first["fullAccess"]!.Value<bool>().Should().BeFalse();
        first["bodyMasked"]!.Value<string>().Should().Contain("Chrome・Windows");
        first["sig"]!.Value<string>().Should().HaveLength(SyncCommandRules.MaxSigLength);
        var second = JObject.Parse(lines[1]);
        second["commandId"]!.Value<string>().Should().Be("not-a-ulid");
        second["detail"]!.Value<string>().Should().Be("cid 不是 ULID");
        second["step"]!.Value<string>().Should().Be(CommandEnvelopeSteps.CommandId);
        File.Exists(path + ".tmp").Should().BeFalse();
        Directory.GetFiles(Path.GetDirectoryName(path)!).Should().ContainSingle();
    }

    [Fact]
    public void ExportRemoteAudit_還在執行中的列寫成unknown_sync_db不改()
    {
        using var store = Create();
        store.BeginCommand(Cid, DigestA, Facts(), Now);
        var path = temp.Combine("export", "running.jsonl");

        store.ExportRemoteAudit(path).Should().Be(1);

        var line = JObject.Parse(File.ReadAllLines(path).Single());
        line["commandId"]!.Value<string>().Should().Be(Cid);
        line["result"]!.Value<string>().Should().Be(RemoteResultStates.Unknown, "匯出是解除配對前最後的版本：之後完成的結果只寫進要被刪的 sync.db（合併前審查 AMO-07）");
        line["step"]!.Value<string>().Should().Be(RemoteGateSteps.Handler, "同 MarkInterruptedUnknown");
        store.LoadRemoteAudit(null, 10).Single().State.Should().Be(SyncCommandRules.ExecutingState, "只改匯出檔，sync.db 照舊（之後的完成照常寫）");
    }

    [Fact]
    public void ExportRemoteAudit_沒有紀錄_寫出空檔()
    {
        using var store = Create();
        var path = temp.Combine("export", "empty.jsonl");

        store.ExportRemoteAudit(path).Should().Be(0);

        File.ReadAllBytes(path).Should().BeEmpty();
    }

    [Fact]
    public void CommandTablesReady_v2是true_表被刪是false()
    {
        using (var store = Create())
        {
            store.CommandTablesReady.Should().BeTrue();
        }

        using var reopened = SyncStore.Open(DbPath, self.DeviceId);
        Raw("DROP TABLE remote_audit;");

        reopened.CommandTablesReady.Should().BeFalse("還沒讀過就被刪：查得到缺表");
        Raw(SyncStoreSchema.CreateV2);
        reopened.CommandTablesReady.Should().BeTrue("false 不記下：補上之後再查就是 true");
    }

    // ── 輔助（另開一條連線讀：WAL 下與 store 的長連線同時存在；連線字串一樣只從 SyncStore.ConnectionString 來）──

    private List<(string CommandId, string ContentSha256, string State, string? ResultJson, long At)> SeenRows()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT command_id, content_sha256, state, result_json, at FROM commands_seen ORDER BY command_id";
        using var reader = cmd.ExecuteReader();
        var rows = new List<(string, string, string, string?, long)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt64(4)));
        return rows;
    }

    private sealed record AuditRow(long Seq, long At, string? CommandId, string Result, string? Step, string? Code, int Count, long? Tier, long? FullAccess,
        string? Policy, long? Paused, string? Detail, string? Sig);

    private List<AuditRow> AuditRows()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT seq, at, command_id, result, step, code, count, tier, full_access, policy, paused, detail, sig FROM remote_audit ORDER BY seq";
        using var reader = cmd.ExecuteReader();
        var rows = new List<AuditRow>();
        while (reader.Read())
        {
            rows.Add(new AuditRow(reader.GetInt64(0), reader.GetInt64(1), Text(reader, 2), reader.GetString(3), Text(reader, 4), Text(reader, 5),
                reader.GetInt32(6), Long(reader, 7), Long(reader, 8), Text(reader, 9), Long(reader, 10), Text(reader, 11), Text(reader, 12)));
        }

        return rows;
    }

    private void Raw(string sql)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(SyncStore.ConnectionString(DbPath, SyncStoreOpenMode.Open));
        connection.Open();
        return connection;
    }

    private static string? Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? Long(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
}
