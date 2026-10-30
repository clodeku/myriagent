using System.Collections.Concurrent;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;
using Myriagent.Tests.Sync.Pairing;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Engine;

/// <summary>
/// 引擎收遠端指令（WP4.4 計畫步 3；D2～D8、D36）：只在有 port、aud 合格時宣告 cmd1；以 Hello 記下的伺服器時鐘驗信封、驗不過的只寫紀錄；
/// commands_seen 去重並在交給中樞之前記 executing；經 port 在 UI 執行緒進閘門、結果回同一條連線。假伺服器宣告 cmd1，閘門是 <see cref="FakeCommandPort"/> 的腳本
/// </summary>
public sealed class CommandEngineTests
{
    internal sealed record AuditRow(string? CommandId, string? Cmd, string Result, string? Code, string? Step, int Count, string? Policy, bool? Paused,
        int? DeclaredTier, int? Tier, string? Detail, string? BodyMasked, string? SignedSha256, string? Sig);

    internal sealed record SeenRow(string CommandId, string State, string? ResultJson);

    /// <summary>引擎改用同時實作 IRemoteCommandPort 的宿主；假伺服器宣告 cmd1</summary>
    internal static FakeCommandPort UsePort(EngineHarness h)
    {
        var port = new FakeCommandPort(h.Host);
        h.HostPort = port;
        h.Server.Features = [DeviceFeatureRules.Cmd1];
        return port;
    }

    internal static SqliteConnection OpenRaw(EngineHarness h)
    {
        var connection = new SqliteConnection(SyncStore.ConnectionString(h.Paths.DatabaseFile, SyncStoreOpenMode.Open));
        connection.Open();
        return connection;
    }

    /// <summary>另開一條連線讀遠端操作紀錄（依 seq）</summary>
    internal static List<AuditRow> Audit(EngineHarness h)
    {
        using var connection = OpenRaw(h);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT command_id, cmd, result, code, step, count, policy, paused, declared_tier, tier, detail, body_masked, signed_sha256, sig
            FROM remote_audit ORDER BY seq
            """;
        using var reader = cmd.ExecuteReader();
        var rows = new List<AuditRow>();
        while (reader.Read())
        {
            rows.Add(new AuditRow(Text(reader, 0), Text(reader, 1), reader.GetString(2), Text(reader, 3), Text(reader, 4), reader.GetInt32(5), Text(reader, 6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7) != 0, reader.IsDBNull(8) ? null : reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetInt32(9),
                Text(reader, 10), Text(reader, 11), Text(reader, 12), Text(reader, 13)));
        }

        return rows;
    }

    /// <summary>另開一條連線讀 commands_seen（依 command_id）</summary>
    internal static List<SeenRow> Seen(EngineHarness h)
    {
        using var connection = OpenRaw(h);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT command_id, state, result_json FROM commands_seen ORDER BY command_id";
        using var reader = cmd.ExecuteReader();
        var rows = new List<SeenRow>();
        while (reader.Read())
            rows.Add(new SeenRow(reader.GetString(0), reader.GetString(1), Text(reader, 2)));
        return rows;
    }

    internal static async Task WaitUntilAsync(Func<bool> condition, string because, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException(because);
            await Task.Delay(5);
        }
    }

    private static string? Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static (string State, string? Code) Pair(CommandResult r) => (r.State, r.Code);

    // ── 宣告 cmd1 ──

    [Fact]
    public async Task 沒有port不宣告cmd1_有port就宣告_Hello的其他鍵不變()
    {
        await using var h = new EngineHarness();
        h.Server.Features = [DeviceFeatureRules.Cmd1];
        await h.StartAsync();
        var plain = h.Server.Hellos.Last();
        plain.Features.Should().BeNull("沒有 port（FakeHostPort）就不宣告");
        var plainKeys = JObject.Parse(SyncJson.Serialize(plain)).Properties().Select(p => p.Name).ToList();

        var port = new FakeCommandPort(h.Host);
        h.HostPort = port;
        await h.RestartAsync();

        var hello = h.Server.Hellos.Last();
        hello.Features.Should().Equal(DeviceFeatureRules.Cmd1, DeviceFeatureRules.Peer1, DeviceFeatureRules.Collab1);
        JObject.Parse(SyncJson.Serialize(hello)).Properties().Select(p => p.Name).Should().Equal(plainKeys.Append("features"), "只多 features，其他鍵不變");
        (await h.Inspect(v => v.Features)).Should().Equal(DeviceFeatureRules.Cmd1);
        port.Reports.Should().Equal(true);
    }

    [Fact]
    public async Task aud不合格不宣告cmd1()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        h.Device = h.Device with { ServerUrl = PairingTestData.Server + "/x" };

        await h.StartAsync();

        h.Server.Hellos.Single().Features.Should().BeNull("配對網址帶路徑，算不出 aud");
        port.Reports.Should().Equal(false);
        (await h.StateAsync()).Should().Be(SyncEngineState.Live, "同步照常");
    }

    // ── 合格的指令 ──

    [Fact]
    public async Task 合格指令_交給中樞時commands_seen已是executing()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        await h.StartAsync();
        List<SeenRow>? seenAtGate = null;
        List<AuditRow>? auditAtGate = null;
        port.Script = (b, _) =>
        {
            seenAtGate = Seen(h);
            auditAtGate = Audit(h);
            return Task.FromResult(FakeCommandPort.Done(b));
        };
        var body = f.Body();

        f.Send(f.Sign(body));
        await h.IdleAsync();

        port.Executed.Should().Equal(body.Cid);
        seenAtGate.Should().ContainSingle().Which.Should().Be(new SeenRow(body.Cid, SyncCommandRules.ExecutingState, null), "提交之後才交給中樞");
        auditAtGate.Should().ContainSingle().Which.Result.Should().Be(SyncCommandRules.ExecutingState, "紀錄與 executing 同一個交易");
    }

    [Fact]
    public async Task 合格指令_結果回到同一連線_紀錄是done()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        await h.StartAsync();
        port.Policy = "Control";
        var body = f.Body();
        var delivery = f.Sign(body);

        f.Send(delivery);
        await h.IdleAsync();

        var result = h.Server.CommandResults.Should().ContainSingle().Subject;
        result.Should().Be(new CommandResult(body.Cid, RemoteResultStates.Done, Tier: 1, FullAccess: false));
        RemoteResultRules.Check(result).Should().BeNull();
        h.Server.Calls.Should().Contain("1:CommandResult", "回在收到它的那條連線");
        var audit = Audit(h).Should().ContainSingle().Subject;
        audit.Should().BeEquivalentTo(new
        {
            CommandId = body.Cid,
            Cmd = "rename",
            Result = RemoteResultStates.Done,
            Code = (string?)null,
            Step = (string?)null,
            Count = 1,
            Policy = "Control",
            Paused = (bool?)false,
            DeclaredTier = (int?)1,
            Tier = (int?)1,
            SignedSha256 = SyncCommandRules.SignedSha256(delivery.Envelope),
            Sig = delivery.Envelope.Sig,
        });
        SyncCommandRules.IssuerLabelFromMasked(audit.BodyMasked).Should().Be("Chrome・Windows");
        var seen = Seen(h).Should().ContainSingle().Subject;
        seen.State.Should().Be(RemoteResultStates.Done);
        SyncCommandRules.ParseResult(body.Cid, seen.ResultJson).Should().Be(result);
    }

    // ── 驗信封失敗（D7）──

    public static TheoryData<string, string, string, int?> EnvelopeFailures => new()
    {
        { "錯誤金鑰", CommandEnvelopeSteps.Signature, RemoteResultCodes.Invalid, null },
        { "竄改body", CommandEnvelopeSteps.Signature, RemoteResultCodes.Invalid, null },
        { "aud", CommandEnvelopeSteps.Audience, RemoteResultCodes.Invalid, 1 },
        { "dev", CommandEnvelopeSteps.Device, RemoteResultCodes.Invalid, 1 },
        { "舊conn", CommandEnvelopeSteps.Connection, RemoteResultCodes.Invalid, 1 },
        { "cid與外層不同", CommandEnvelopeSteps.CommandId, RemoteResultCodes.Invalid, 1 },
        { "tier是3", CommandEnvelopeSteps.TierRange, RemoteResultCodes.Invalid, null },
        { "過期", CommandEnvelopeSteps.Time, RemoteResultCodes.Expired, 1 },
        { "還沒生效", CommandEnvelopeSteps.Time, RemoteResultCodes.Expired, 1 },
        { "非durable卻標durable", CommandEnvelopeSteps.Time, RemoteResultCodes.Invalid, 1 },
        { "issuer不是browser", CommandEnvelopeSteps.Issuer, RemoteResultCodes.Invalid, 1 },
        { "conv與cmd不同", CommandEnvelopeSteps.Target, RemoteResultCodes.Invalid, 1 },
    };

    private static CommandDelivery Failing(string name, CommandEnvelopeFactory f)
    {
        var body = f.Body();
        var now = f.ServerNow;
        return name switch
        {
            "錯誤金鑰" => f.SignWithOtherKey(body),
            "竄改body" => f.Tampered(body, "新標題", "別的標題"),
            "aud" => f.Sign(body with { Aud = "https://other.test" }),
            "dev" => f.Sign(body with { Dev = SyncIds.NewDeviceId() }),
            "舊conn" => f.Sign(body with { Conn = HandshakeRules.NewNonce() }),
            "cid與外層不同" => f.Sign(body, SyncIds.NewUlid(now)),
            "tier是3" => f.Sign(body with { Tier = 3 }),
            "過期" => f.Sign(body with { Created = now - 90_000, Iat = now - 90_000, Exp = now - 60_000 }),
            "還沒生效" => f.Sign(body with { Created = now + 20_000, Iat = now + 20_000, Exp = now + 50_000 }),
            "非durable卻標durable" => f.Sign(body with { Durable = true }),
            "issuer不是browser" => f.Sign(body with { Issuer = new CommandIssuer("device", "Chrome・Windows") }),
            "conv與cmd不同" => f.Sign(body with { Conv = new string('d', 32) }),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
        };
    }

    [Theory]
    [MemberData(nameof(EnvelopeFailures))]
    public async Task 驗信封失敗_不執行_紀錄一列_回步驟的代碼_不寫seen(string name, string step, string code, int? tier)
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        await h.StartAsync();
        var delivery = Failing(name, f);

        f.Send(delivery);
        await h.IdleAsync();

        port.Calls.Should().BeEmpty("驗不過的不交給中樞");
        var result = h.Server.CommandResults.Should().ContainSingle().Subject;
        result.Should().Be(new CommandResult(delivery.CommandId, RemoteResultStates.Rejected, code, Message: null, Tier: tier));
        RemoteResultRules.Check(result).Should().BeNull("伺服器的格式檢查收得下");
        var audit = Audit(h).Should().ContainSingle().Subject;
        audit.CommandId.Should().Be(delivery.CommandId);
        audit.Result.Should().Be(RemoteResultStates.Rejected);
        audit.Step.Should().Be(step);
        audit.Code.Should().Be(code);
        audit.Detail.Should().NotBeNullOrEmpty();
        audit.SignedSha256.Should().Be(SyncCommandRules.SignedSha256(delivery.Envelope));
        audit.Sig.Should().Be(delivery.Envelope.Sig);
        if (step == CommandEnvelopeSteps.Signature)
            audit.BodyMasked.Should().BeNull("沒解析過的 body 不進紀錄");
        else
            audit.BodyMasked.Should().NotBeNull();
        if (name == "aud")
            audit.Detail.Should().Contain("https://other.test").And.Contain(f.Aud, "aud 不符時寫出兩邊的值");
        Seen(h).Should().BeEmpty("驗不過的不寫 commands_seen");
    }

    [Fact]
    public async Task conn不符_之後同cid合格的照樣執行一次()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        await h.StartAsync();
        var body = f.Body();

        f.Send(f.Sign(body with { Conn = HandshakeRules.NewNonce() }));
        await h.IdleAsync();
        f.Send(f.Sign(body));
        await h.IdleAsync();

        port.Executed.Should().Equal(body.Cid);
        h.Server.CommandResults.Select(Pair).Should().Equal((RemoteResultStates.Rejected, RemoteResultCodes.Invalid), (RemoteResultStates.Done, null));
        Audit(h).Select(a => (a.Result, a.Step)).Should().Equal((RemoteResultStates.Rejected, CommandEnvelopeSteps.Connection), (RemoteResultStates.Done, null));
    }

    [Fact]
    public async Task 外層commandId不是ULID_只寫紀錄_不回結果()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        await h.StartAsync();

        f.Send(f.Sign(f.Body(), "not-a-ulid"));
        await h.IdleAsync();

        h.Server.CommandResults.Should().BeEmpty("伺服器的 RemoteResultRules.Check 也會丟掉");
        port.Calls.Should().BeEmpty();
        var audit = Audit(h).Should().ContainSingle().Subject;
        audit.CommandId.Should().Be("not-a-ulid");
        audit.Step.Should().Be(CommandEnvelopeSteps.CommandId);
    }

    // ── 去重（D8）──

    [Fact]
    public async Task 同cid同內容重送5次_只執行一次_5個結果相同_count是5()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        await h.StartAsync();
        var delivery = f.Sign(f.Body());

        for (var i = 0; i < 5; i++)
        {
            f.Send(delivery);
            await h.IdleAsync();
        }

        port.Executed.Should().Equal(delivery.CommandId);
        var results = h.Server.CommandResults.ToList();
        results.Should().HaveCount(5);
        results.Should().OnlyContain(r => r == results[0]);
        results[0].State.Should().Be(RemoteResultStates.Done);
        Audit(h).Should().ContainSingle().Which.Count.Should().Be(5);
    }

    [Fact]
    public async Task 以新的conn_iat_exp重簽_仍命中去重()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        await h.StartAsync();
        var body = f.Body();
        f.Send(f.Sign(body));
        await h.IdleAsync();

        h.Server.Current!.Drop();
        await h.IdleAsync();
        await h.AdvanceAsync(1);
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        var resigned = body with { Conn = f.Conn, Iat = f.ServerNow, Exp = f.ServerNow + 30_000 };
        resigned.Conn.Should().NotBe(body.Conn);
        f.Send(f.Sign(resigned));
        await h.IdleAsync();

        port.Executed.Should().Equal(body.Cid);
        h.Server.CommandResults.Select(Pair).Should().Equal((RemoteResultStates.Done, null), (RemoteResultStates.Done, null));
        Audit(h).Should().ContainSingle().Which.Count.Should().Be(2);
    }

    [Fact]
    public async Task 執行中重送_等原本那次_只執行一次()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        await h.StartAsync();
        var gate = new TaskCompletionSource<RemoteOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        port.Script = (_, _) => gate.Task;
        var body = f.Body();
        var delivery = f.Sign(body);

        f.Send(delivery);
        await WaitUntilAsync(() => port.Runs.Count == 1, "第一次進閘門");
        f.Send(delivery);
        await WaitUntilAsync(() => Audit(h).Single().Count == 2, "第二次已記成重送");
        h.Server.CommandResults.Should().BeEmpty("原本那次還沒有結果");
        gate.SetResult(FakeCommandPort.Done(body));
        await h.IdleAsync();

        port.Calls.Should().ContainSingle();
        h.Server.CommandResults.Should().HaveCount(2).And.OnlyContain(r => r == new CommandResult(body.Cid, RemoteResultStates.Done, null, null, 1, false));
    }

    [Fact]
    public async Task 同cid不同內容_conflict_不執行()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        await h.StartAsync();
        var body = f.Body();
        f.Send(f.Sign(body));
        await h.IdleAsync();

        f.Send(f.Sign(f.Body(CommandEnvelopeFactory.Rename("別的標題"), body.Cid)));
        await h.IdleAsync();

        port.Executed.Should().Equal(body.Cid);
        var results = h.Server.CommandResults.ToList();
        results.Select(Pair).Should().Equal((RemoteResultStates.Done, null), (RemoteResultStates.Rejected, RemoteResultCodes.Conflict));
        results[1].Message.Should().NotBeNullOrEmpty();
        Audit(h).Select(a => (a.Result, a.Step, a.Code)).Should().Equal(
            (RemoteResultStates.Done, null, null), (RemoteResultStates.Rejected, SyncCommandRules.SeenStep, RemoteResultCodes.Conflict));
        Seen(h).Should().ContainSingle().Which.State.Should().Be(RemoteResultStates.Done, "原本的列不變");
    }

    [Fact]
    public async Task commands_seen寫不進去_回busy_不執行()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        var fail = true;
        h.OpenStore = (path, deviceId) =>
        {
            var store = SyncStore.Open(path, deviceId);
            store.FaultInjection = point =>
            {
                if (Volatile.Read(ref fail) && point == "BeginCommand:afterSeen")
                    throw new SqliteException("模擬磁碟已滿", 13);
            };
            return store;
        };
        await h.StartAsync();
        var delivery = f.Sign(f.Body());

        f.Send(delivery);
        await h.IdleAsync();

        port.Calls.Should().BeEmpty();
        var busy = h.Server.CommandResults.Should().ContainSingle().Subject;
        busy.Should().BeEquivalentTo(new { State = RemoteResultStates.Rejected, Code = RemoteResultCodes.Busy });
        busy.Message.Should().NotBeNullOrEmpty();
        Seen(h).Should().BeEmpty();
        Audit(h).Should().BeEmpty("同一個交易，整個回滾");

        Volatile.Write(ref fail, false);
        f.Send(delivery);
        await h.IdleAsync();
        port.Executed.Should().Equal(new[] { delivery.CommandId }, "busy 什麼都沒執行，之後同一個 cid 照常執行");
        h.Server.CommandResults.Last().State.Should().Be(RemoteResultStates.Done);
    }

    [Fact]
    public async Task 完成時寫入失敗_重送回快取的結果_快取沒有時回unknown()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        h.OpenStore = (path, deviceId) =>
        {
            var store = SyncStore.Open(path, deviceId);
            store.FaultInjection = point =>
            {
                if (point == "CompleteCommand:afterSeen")
                    throw new SqliteException("模擬磁碟已滿", 13);
            };
            return store;
        };
        await h.StartAsync();
        var done = f.Sign(f.Body());
        var notStarted = f.Sign(f.Body(CommandEnvelopeFactory.Rename("第二個")));
        port.NotStarted = b => b.Cid == notStarted.CommandId;

        f.Send(done);
        await h.IdleAsync();
        Seen(h).Should().ContainSingle().Which.State.Should().Be(SyncCommandRules.ExecutingState, "完成時寫不進去，列還是 executing");
        f.Send(done);
        await h.IdleAsync();

        f.Send(notStarted);
        await h.IdleAsync();
        f.Send(notStarted);
        await h.IdleAsync();

        port.Executed.Should().Equal(done.CommandId);
        port.Calls.Select(c => c.Body.Cid).Should().Equal(done.CommandId, notStarted.CommandId);
        h.Server.CommandResults.Select(r => (r.CommandId, r.State, r.Code)).Should().Equal(
            (done.CommandId, RemoteResultStates.Done, null),
            (done.CommandId, RemoteResultStates.Done, null),
            (notStarted.CommandId, RemoteResultStates.Rejected, RemoteResultCodes.Busy),
            (notStarted.CommandId, RemoteResultStates.Unknown, null));
    }

    [Fact]
    public async Task 中樞回unknown_記unknown_重送回unknown()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        await h.StartAsync();
        port.Script = (b, _) => Task.FromResult(FakeCommandPort.Unknown(b));
        var delivery = f.Sign(f.Body());

        f.Send(delivery);
        await h.IdleAsync();
        f.Send(delivery);
        await h.IdleAsync();

        port.Calls.Should().ContainSingle();
        h.Server.CommandResults.Select(Pair).Should().Equal((RemoteResultStates.Unknown, null), (RemoteResultStates.Unknown, null));
        var audit = Audit(h).Should().ContainSingle().Subject;
        audit.Result.Should().Be(RemoteResultStates.Unknown);
        audit.Step.Should().Be(RemoteGateSteps.Handler);
        audit.Count.Should().Be(2);
        Seen(h).Should().ContainSingle().Which.State.Should().Be(RemoteResultStates.Unknown);
    }

    [Fact]
    public async Task port還沒開始就取消_回busy_seen列被刪_同cid可以再執行()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        await h.StartAsync();
        var notStarted = true;
        port.NotStarted = _ => Volatile.Read(ref notStarted);
        var delivery = f.Sign(f.Body());

        f.Send(delivery);
        await h.IdleAsync();
        h.Server.CommandResults.Select(Pair).Should().Equal((RemoteResultStates.Rejected, RemoteResultCodes.Busy));
        Seen(h).Should().BeEmpty("busy 什麼都沒執行：刪掉 seen 列");

        Volatile.Write(ref notStarted, false);
        f.Send(delivery);
        await h.IdleAsync();

        port.Executed.Should().Equal(delivery.CommandId);
        h.Server.CommandResults.Last().State.Should().Be(RemoteResultStates.Done);
        Seen(h).Should().ContainSingle().Which.State.Should().Be(RemoteResultStates.Done);
    }

    // ── 伺服器時鐘（D4）──

    [Fact]
    public async Task 時鐘不可用_非stop回expired並安排重新Hello_stop照常執行()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        // 0＝照實際的時鐘
        long fakeNow = 0;
        await h.StartAsync(e => e.CommandTimestampForTest = () => Interlocked.Read(ref fakeNow) is var t && t != 0 ? t : h.Time.GetTimestamp());
        // Hello 收到回應時的時戳（時鐘沒撥過）＋12 小時＋1 毫秒：時鐘太舊，界線不可用
        Interlocked.Exchange(ref fakeNow, h.Time.GetTimestamp() + TimeSpan.FromHours(12).Ticks + TimeSpan.TicksPerMillisecond);

        var stop = f.Body(CommandEnvelopeFactory.Stop());
        f.Send(f.Sign(stop));
        await h.IdleAsync();
        port.Executed.Should().Equal(new[] { stop.Cid }, "stop 不看時間界線");
        h.Server.Hellos.Should().HaveCount(1);

        var rename = f.Body();
        f.Send(f.Sign(rename));
        await h.IdleAsync();

        h.Server.CommandResults.Last().Should().Be(new CommandResult(rename.Cid, RemoteResultStates.Rejected, RemoteResultCodes.Expired, Message: null, Tier: 1));
        port.Executed.Should().Equal(stop.Cid);
        Seen(h).Select(s => s.CommandId).Should().NotContain(rename.Cid, "什麼都沒執行，不寫 seen");
        var audit = Audit(h).Last();
        (audit.Step, audit.Code).Should().Be((CommandEnvelopeSteps.Time, RemoteResultCodes.Expired));
        h.Server.Hellos.Should().HaveCount(2, "時間判不過：重新 Hello 換新的時鐘");
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);

        // 新的時鐘、新的 conn：伺服器以同一個 cid 重簽，照常執行一次
        Interlocked.Exchange(ref fakeNow, 0);
        f.Send(f.Sign(rename with { Conn = f.Conn }));
        await h.IdleAsync();
        port.Executed.Should().Equal(stop.Cid, rename.Cid);
        h.Server.CommandResults.Last().Should().Be(new CommandResult(rename.Cid, RemoteResultStates.Done, Tier: 1, FullAccess: false));
    }

    [Fact]
    public async Task 伺服器時間比本機快一小時_照樣用伺服器時間判斷()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        h.Server.ClockOffsetMs = 3_600_000;
        await h.StartAsync();
        var body = f.Body();
        body.Iat.Should().BeGreaterThan(h.Time.NowMs + 3_500_000, "信封依伺服器時間簽");

        f.Send(f.Sign(body));
        await h.IdleAsync();

        port.Executed.Should().Equal(body.Cid);
        h.Server.CommandResults.Should().ContainSingle().Which.State.Should().Be(RemoteResultStates.Done);
    }

    // ── 執行緒（M-執行緒）──

    [Fact]
    public async Task 回呼的執行緒()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        var hooks = new ConcurrentQueue<(string Hook, int Thread)>();
        await h.StartAsync(e => e.ContractProbeForTest = (hook, _) => hooks.Enqueue((hook, Environment.CurrentManagedThreadId)));

        f.Send(f.Sign(f.Body()));
        await h.IdleAsync();

        var run = port.Runs.Should().ContainSingle().Subject;
        run.OnUi.Should().BeTrue("中樞閘門在 UI 執行緒");
        run.Thread.Should().NotBe(h.Engine.ThreadId);
        port.Calls.Should().ContainSingle().Which.Thread.Should().Be(h.Engine.ThreadId, "引擎在自己的執行緒呼叫 port，port 再排到 UI 執行緒");
        hooks.Where(c => c.Hook == "CompleteCommand").Should().ContainSingle().Which.Thread.Should().Be(h.Engine.ThreadId, "sync.db 只在引擎執行緒寫");
        hooks.Where(c => c.Hook == "OnCommand").Should().ContainSingle().Which.Thread.Should().Be(h.Engine.ThreadId);
    }

    // ── 啟動回收（D11）──

    [Fact]
    public async Task 啟動前塞一列executing_第一次Hello後是unknown_重送不執行()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        await h.StartAsync();
        var body = f.Body();
        await h.Engine.StopAsync(TimeSpan.FromSeconds(5));
        // 上次執行中當機：commands_seen 還是 executing（conn 不在摘要裡，重連後的信封摘要相同）
        using (var store = SyncStore.Open(h.Paths.DatabaseFile, h.DeviceId))
        {
            var facts = new CommandAuditFacts(body.Cid, "rename", body.Conv, body.Tier, false, body.Iat, body.Created, null, null, null, null);
            store.BeginCommand(body.Cid, CommandEnvelope.ContentDigest(body), facts, h.Time.NowMs).Kind.Should().Be(CommandBeginKind.New);
        }

        await h.StartAsync();

        var seen = Seen(h).Should().ContainSingle().Subject;
        seen.State.Should().Be(RemoteResultStates.Unknown);
        SyncCommandRules.ParseResult(body.Cid, seen.ResultJson).Should().Be(SyncCommandRules.UnknownResult(body.Cid));
        Audit(h).Should().ContainSingle().Which.Should().BeEquivalentTo(new { Result = RemoteResultStates.Unknown, Step = RemoteGateSteps.Handler });

        f.Send(f.Sign(body with { Conn = f.Conn }));
        await h.IdleAsync();

        port.Calls.Should().BeEmpty("不知道上次有沒有執行完：不重做");
        h.Server.CommandResults.Should().ContainSingle().Which.Should().Be(SyncCommandRules.UnknownResult(body.Cid));
        Audit(h).Should().ContainSingle().Which.Count.Should().Be(2);
    }

    // ── 擁有者登入網頁的通知不做（計畫 1.3、D30）──

    [Fact]
    public async Task ServerNotice的ownerLogin_照舊只記錄_不通知()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        var notices = new ConcurrentQueue<(ServerNotice Notice, bool Handled)>();
        await h.StartAsync(e => e.ContractProbeForTest = (hook, arg) =>
        {
            if (hook == "HandleServerNoticeExtension" && arg is ValueTuple<ServerNotice, System.Runtime.CompilerServices.StrongBox<bool>> pair)
                notices.Enqueue((pair.Item1, pair.Item2.Value));
        });
        var detail = h.Host.LastStatus?.Detail;

        h.Server.Current!.Notice("ownerLogin", text: "Chrome・Windows");
        await h.IdleAsync();

        notices.Should().ContainSingle().Which.Should().Be((new ServerNotice("ownerLogin", null, "Chrome・Windows"), false),
            "沒有實作 HandleServerNoticeExtension：不認得的通知照舊只記錄（依擁有者指示不做 ownerLogin）");
        port.Calls.Should().BeEmpty("不經 port 通知這台");
        port.Reports.Should().Equal(true);
        h.Server.Hellos.Should().ContainSingle("連線照常，不重新 Hello");
        h.Host.LastStatus?.Detail.Should().Be(detail, "不改同步狀態的說明");
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
    }
}
