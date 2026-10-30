using Myriagent.Sync;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Pairing;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using MyriagentHub.Tests.Sync;
using MyriagentHub.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.Sync.EventLogTests;
using static MyriagentHub.Tests.TestSupport.BrowserFixtures;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 瀏覽器閘道（設計 4.9.2、14.6；web-protocol 第六節第 8 點）：ready 後送 init、只推有變的部分、節流 500 ms、保底重讀、
/// 沒有連線不讀、讀取失敗退避、溢出以重送 init 達成 resync、慢用戶端中斷、每個部分只序列化一次；
/// AccountViewBuilder 以 deferred 唯讀交易讀快照；主機整合：init 的內容、提交與上下線時推送
/// </summary>
public sealed class BrowserGatewayTests
{
    private const string AccountsA = """[{"id":"la_a","label":"A"}]""";

    private const string AccountsB = """[{"id":"la_b","label":"B"}]""";

    // ───── 單元測試（假的快照來源與時鐘，直接驅動 RunOnceAsync） ─────

    [Fact]
    public async Task ready之前不送任何訊息且第一則是init()
    {
        var h = new GatewayHarness();
        var a = h.Add("c1");
        h.Gateway.MarkDirty(BrowserParts.All);
        await h.RunAsync();
        h.Source.Calls.Should().Be(1, "有連線就讀");
        await a.AssertNothingAsync();

        h.Source.Current = h.Source.Current with { AccountsJson = AccountsA };
        h.Gateway.MarkDirty(BrowserParts.Accounts);
        h.Time.Advance(TimeSpan.FromMilliseconds(500));
        await h.RunAsync();
        await a.AssertNothingAsync("還沒送 ready");

        h.Gateway.RequestInit(a.Connection, "main");
        await h.RunAsync();
        var first = await a.NextAsync();
        first["type"]!.Value<string>().Should().Be("init");
        first["accounts"]!.ToString(Newtonsoft.Json.Formatting.None).Should().Be(AccountsA);
        await a.AssertNothingAsync("init 已帶最新的各部分");
        a.Connection.InitSent.Should().BeTrue();
    }

    [Fact]
    public async Task init之後只送有變的部分()
    {
        var h = new GatewayHarness();
        var a = await h.ReadyAsync("c1");
        h.Source.Current = h.Source.Current with { AccountsJson = AccountsA };
        h.Gateway.MarkDirty(BrowserParts.All);
        h.Time.Advance(TimeSpan.FromMilliseconds(500));
        await h.RunAsync();
        var message = await a.NextAsync();
        message["type"]!.Value<string>().Should().Be("accounts");
        message["accounts"]!.ToString(Newtonsoft.Json.Formatting.None).Should().Be(AccountsA);
        await a.AssertNothingAsync("devices、cli、settings 沒變");

        h.Source.Current = h.Source.Current with { DevicesCliJson = """{"d_1":{}}""" };
        h.Gateway.MarkDirty(BrowserParts.DevicesCli);
        h.Time.Advance(TimeSpan.FromMilliseconds(500));
        await h.RunAsync();
        var cli = await a.NextAsync();
        cli["type"]!.Value<string>().Should().Be("cli");
        cli["cli"]!.Should().BeOfType<JObject>().Which.Count.Should().Be(0, "瀏覽器模式的 cli 一律是 {}");
        cli["devicesCli"]!["d_1"].Should().NotBeNull();
    }

    [Fact]
    public async Task 內容沒變不重送()
    {
        var h = new GatewayHarness();
        var a = await h.ReadyAsync("c1");
        for (var i = 0; i < 3; i++)
        {
            h.Gateway.MarkDirty(BrowserParts.All);
            h.Time.Advance(TimeSpan.FromMilliseconds(500));
            await h.RunAsync();
        }
        h.Source.Calls.Should().Be(4, "照樣讀");
        await a.AssertNothingAsync("內容沒變");
    }

    [Fact]
    public async Task init不比之前送出的更新舊()
    {
        var h = new GatewayHarness();
        h.Source.Current = h.Source.Current with { AccountsJson = AccountsA };
        var a = await h.ReadyAsync("c1");

        h.Source.Current = h.Source.Current with { AccountsJson = AccountsB };
        h.Gateway.MarkDirty(BrowserParts.Accounts);
        h.Time.Advance(TimeSpan.FromMilliseconds(500));
        await h.RunAsync();
        (await a.NextAsync())["accounts"]!.ToString(Newtonsoft.Json.Formatting.None).Should().Be(AccountsB);

        // 同一條連線再送 ready、另一條新連線：init 都帶 B（目前最新）
        h.Gateway.RequestInit(a.Connection, "main");
        var b = h.Add("c2");
        h.Gateway.RequestInit(b.Connection, "main");
        await h.RunAsync();
        (await a.NextAsync())["accounts"]!.ToString(Newtonsoft.Json.Formatting.None).Should().Be(AccountsB);
        var init = await b.NextAsync();
        init["type"]!.Value<string>().Should().Be("init");
        init["accounts"]!.ToString(Newtonsoft.Json.Formatting.None).Should().Be(AccountsB);
    }

    [Fact]
    public async Task 推送有節流()
    {
        var h = new GatewayHarness();
        var start = h.Time.NowMs;
        var a = await h.ReadyAsync("c1");
        for (var i = 1; i <= 10; i++)
        {
            h.Time.Advance(TimeSpan.FromMilliseconds(100));
            h.Source.Current = h.Source.Current with { AccountsJson = $$"""[{"id":"la_{{i}}"}]""" };
            h.Gateway.MarkDirty(BrowserParts.Accounts);
            await h.RunAsync();
            // 等幫浦取走：還在佇列裡的 accounts 會被下一則整份取代的合併掉（ClientOutbox 的設計），這裡要數的是讀取與推送的次數
            await WaitUntilAsync(() => a.Connection.Outbox.Count == 0);
        }
        h.Source.Reads.Select(r => r.AtMs - start).Should().Equal([0L, 500L, 1000L], "最多每 500 ms 讀一次");
        var pushed = await a.TakeAsync(2);
        pushed.Select(m => m["accounts"]![0]!["id"]!.Value<string>()).Should().Equal(["la_5", "la_10"]);
    }

    [Fact]
    public async Task 沒有髒旗標時十五秒保底重讀一次()
    {
        var h = new GatewayHarness();
        await h.ReadyAsync("c1");
        h.Source.Calls.Should().Be(1);
        h.Time.Advance(TimeSpan.FromMilliseconds(14_900));
        (await h.RunAsync()).Should().Be(TimeSpan.FromMilliseconds(100), "下一次保底重讀在 100 ms 後");
        h.Source.Calls.Should().Be(1);
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        (await h.RunAsync()).Should().Be(TimeSpan.FromSeconds(15));
        h.Source.Calls.Should().Be(2);
        h.Source.Reads[^1].Parts.Should().Be(BrowserParts.All, "保底重讀全部");
    }

    [Fact]
    public async Task 沒有連線時不讀()
    {
        var h = new GatewayHarness();
        h.Gateway.MarkDirty(BrowserParts.All);
        (await h.RunAsync()).Should().Be(Timeout.InfiniteTimeSpan);
        h.Time.Advance(TimeSpan.FromMinutes(1));
        await h.RunAsync();
        h.Source.Calls.Should().Be(0);

        // 連線來了才讀（髒旗標還在）
        var a = await h.ReadyAsync("c1");
        h.Source.Calls.Should().Be(1);
        (await a.TakeAsync(0)).Should().BeEmpty();
    }

    [Fact]
    public async Task 讀取失敗沿用上一份並退避重試()
    {
        var h = new GatewayHarness();
        h.Source.Current = h.Source.Current with { AccountsJson = AccountsA };
        var a = await h.ReadyAsync("c1");

        h.Source.Throw = true;
        h.Source.Current = h.Source.Current with { AccountsJson = AccountsB };
        h.Gateway.MarkDirty(BrowserParts.Accounts);
        h.Time.Advance(TimeSpan.FromMilliseconds(500));
        (await h.RunAsync()).Should().Be(TimeSpan.FromSeconds(1), "第一次失敗退避 1 秒");
        h.Source.Calls.Should().Be(2);

        // 退避期間不讀；新連線的 init 用上一份快照
        h.Time.Advance(TimeSpan.FromMilliseconds(500));
        var b = h.Add("c2");
        h.Gateway.RequestInit(b.Connection, "main");
        await h.RunAsync();
        h.Source.Calls.Should().Be(2);
        (await b.NextAsync())["accounts"]!.ToString(Newtonsoft.Json.Formatting.None).Should().Be(AccountsA);

        h.Time.Advance(TimeSpan.FromMilliseconds(500));
        (await h.RunAsync()).Should().Be(TimeSpan.FromSeconds(2), "第二次失敗退避 2 秒");
        h.Source.Calls.Should().Be(3);
        h.Source.Reads[^1].Parts.Should().Be(BrowserParts.Accounts, "髒旗標放回了");

        h.Source.Throw = false;
        h.Time.Advance(TimeSpan.FromSeconds(2));
        await h.RunAsync();
        h.Source.Calls.Should().Be(4);
        (await a.NextAsync())["accounts"]!.ToString(Newtonsoft.Json.Formatting.None).Should().Be(AccountsB);
        (await b.NextAsync())["accounts"]!.ToString(Newtonsoft.Json.Formatting.None).Should().Be(AccountsB);
    }

    [Fact]
    public async Task 溢出時清空並重送init()
    {
        var h = new GatewayHarness(o => o.Browser.OutboxMaxMessages = 2);
        var a = h.Add("c1");
        a.Block();
        h.Gateway.RequestInit(a.Connection, "popout");
        await h.RunAsync();
        await WaitUntilAsync(() => a.Connection.Outbox.Count == 0, because: "第一則 init 已出列、送出卡住");

        ChangeEverything(h, 1);
        (await h.RunAsync()).Should().Be(TimeSpan.Zero, "溢出後要立刻再跑一輪重送 init");
        a.Connection.Outbox.Count.Should().Be(0, "溢出時清空，之後的部分也不再入列");
        a.Connection.InitSent.Should().BeFalse();

        await h.RunAsync();
        a.Connection.Outbox.Count.Should().Be(1);
        a.Release();
        var sent = await a.TakeAsync(2);
        sent.Select(m => m["type"]!.Value<string>()).Should().Equal(["init", "init"], "溢出後到重送 init 之前不送其他訊息");
        sent[1]["mode"]!.Value<string>().Should().Be("popout", "沿用最後一次 ready 的 mode");
        sent[1]["accounts"]![0]!["id"]!.Value<string>().Should().Be("la_1", "重送的 init 帶目前最新的快照");
        await a.AssertNothingAsync();
        a.Connection.AbortReason.Should().BeNull();
    }

    [Fact]
    public async Task 第二則init不清掉之前排著的toast()
    {
        var h = new GatewayHarness();
        var a = await h.ReadyAsync("c1");
        a.Block();
        h.Gateway.SendToast(a.Connection, "send", Toast("t0"));
        await WaitUntilAsync(() => a.Connection.Outbox.Count == 0, because: "t0 已出列、送出卡住，之後入列的都留在佇列裡");
        h.Gateway.SendToast(a.Connection, "openFolder", Toast("t1"));
        a.Connection.Outbox.Count.Should().Be(1);

        // 同一條連線再送 ready（例如 ready 被限流後重送，web-protocol 第六節第 11 點）：init 是屏障，但 toast 不是快照的一部分
        h.Gateway.RequestInit(a.Connection, "main");
        await h.RunAsync();
        a.Release();
        var sent = await a.TakeAsync(3);
        sent.Select(m => $"{m["type"]}:{m["text"]}").Should().Equal(["toast:t0", "init:", "toast:t1"], "toast 跟在 init 後面送出");
        await a.AssertNothingAsync();
    }

    [Fact]
    public async Task 溢出後的resync不清掉之後入列的toast()
    {
        var h = new GatewayHarness(o => o.Browser.OutboxMaxMessages = 2);
        var a = await h.ReadyAsync("c1");
        a.Block();
        h.Gateway.SendToast(a.Connection, "send", Toast("t0"));
        await WaitUntilAsync(() => a.Connection.Outbox.Count == 0);
        ChangeEverything(h, 1);
        (await h.RunAsync()).Should().Be(TimeSpan.Zero, "溢出：清空、下一輪重送 init");
        a.Connection.Outbox.Count.Should().Be(0);
        a.Connection.InitSent.Should().BeFalse();
        h.Gateway.SendToast(a.Connection, "openFolder", Toast("dropped"));
        a.Connection.Outbox.Count.Should().Be(0, "標回還沒送過 init 之後被拒的指令不送 toast（D17）");

        // 指令處理在溢出清空佇列之後、標回「還沒送過 init」之前把 toast 入列（那時 InitSent 還是 true，兩邊在不同的執行緒）：
        // 這個窗口在閘道的一輪裡面，這裡直接放進送出佇列模擬
        a.Connection.Outbox.Enqueue(Toast("t1"));
        await h.RunAsync();
        a.Release();
        var sent = await a.TakeAsync(3);
        sent.Select(m => $"{m["type"]}:{m["text"]}").Should().Equal(["toast:t0", "init:", "toast:t1"]);
        a.Connection.AbortReason.Should().BeNull();
    }

    [Fact]
    public async Task init出現在送出佇列時連線已標成送過init()
    {
        // WP3.7 步驟 1 的根因：init 入列之後才標成已送過 init。網頁一收到 init 就可能送出指令，中間閘道的執行緒被搶走 CPU 時，
        // 被拒的指令看到「還沒送過 init」而不送 toast（D17），之後同一 cmd 的 toast 又被節流——雲端主機上整批 200 個指令都沒有 toast。
        // 觀察者在另一條執行緒上等 init 出現在佇列裡、立刻讀 InitSent：修正前很快就會讀到 false；修正後（先標再入列）絕不會
        for (var i = 0; i < 300; i++)
        {
            var h = new GatewayHarness();
            var a = h.Add("c1");
            a.Block();
            // 只為了讓送出端卡住（init 才會留在佇列裡讓觀察者看到）：直接放進送出佇列，不經閘道（閘道在 init 之前不送 toast）
            a.Connection.Outbox.Enqueue(Toast("t0"));
            SpinWait.SpinUntil(() => a.Connection.Outbox.Count == 0, 5000).Should().BeTrue("t0 已出列、送出卡住");
            h.Gateway.RequestInit(a.Connection, "main");

            bool? seen = null;
            using var spinning = new ManualResetEventSlim();
            var observer = new Thread(() =>
            {
                // 緊迴圈（SpinWait 會退讓成 Sleep(1)，就追不上閘道的執行緒了）
                var outbox = a.Connection.Outbox;
                var deadline = Environment.TickCount64 + 5000;
                spinning.Set();
                while (outbox.Count == 0)
                {
                    if (Environment.TickCount64 > deadline)
                        return;
                }
                seen = a.Connection.InitSent;
            })
            { IsBackground = true };
            observer.Start();
            spinning.Wait();
            await h.RunAsync();
            observer.Join();
            seen.Should().BeTrue($"第 {i} 次：init 一出現在佇列裡，這條連線就要算已送過 init");

            a.Release();
            h.Connections.Remove("c1");
        }
    }

    [Fact]
    public async Task 一看到送過init就送的toast也排在init後面()
    {
        // 合併前審查 F1：原本先標成送過 init、放開連線鎖之後才組 init 並入列。兩步之間被拒的指令看到「已送過 init」就把 toast 入列，
        // 佇列是空的、送出幫浦馬上取走，網頁先收到 toast（第一則 init 之前、溢出後的 resync 都會；修正前 200 次約 80 次）。
        // 觀察者在另一條執行緒上以緊迴圈等 InitSent 變成 true、立刻送 toast：修正後（標與入列在同一把連線鎖裡）第一則一定是 init
        for (var i = 0; i < 200; i++)
        {
            var h = new GatewayHarness();
            var a = h.Add("c1");
            h.Gateway.RequestInit(a.Connection, "main");

            var toasted = false;
            using var spinning = new ManualResetEventSlim();
            var observer = new Thread(() =>
            {
                var connection = a.Connection;
                var deadline = Environment.TickCount64 + 5000;
                spinning.Set();
                while (!connection.InitSent)
                {
                    if (Environment.TickCount64 > deadline)
                        return;
                }
                h.Gateway.SendToast(connection, "send", Toast("t0"));
                toasted = true;
            })
            { IsBackground = true };
            observer.Start();
            spinning.Wait();
            await h.RunAsync();
            observer.Join();
            toasted.Should().BeTrue($"第 {i} 次：觀察者看到了送過 init");

            var sent = await a.TakeAsync(2);
            sent.Select(m => $"{m["type"]}:{m["text"]}").Should().Equal(["init:", "toast:t0"], $"第 {i} 次：toast 不比 init 先送出");
            h.Connections.Remove("c1");
        }
    }

    [Fact]
    public async Task 六十秒內第三次溢出中斷連線()
    {
        var h = new GatewayHarness(o => o.Browser.OutboxMaxMessages = 2);
        var a = h.Add("c1");
        a.Block();
        h.Gateway.RequestInit(a.Connection, "main");
        await h.RunAsync();
        await WaitUntilAsync(() => a.Connection.Outbox.Count == 0);

        for (var round = 1; round <= 3; round++)
        {
            ChangeEverything(h, round);
            await h.RunAsync();
            if (round < 3)
            {
                a.Connection.AbortReason.Should().BeNull($"第 {round} 次溢出重送 init");
                await h.RunAsync();
                a.Connection.InitSent.Should().BeTrue();
            }
        }
        a.Connection.AbortReason.Should().Be(BrowserGateway.SlowClientReason, "60 秒內第 3 次");
        a.Aborts.Should().Be(1);
    }

    [Fact]
    public async Task 每個部分只序列化一次()
    {
        var h = new GatewayHarness();
        var a = h.Add("c1");
        var b = h.Add("c2");
        h.Gateway.RequestInit(a.Connection, "main");
        h.Gateway.RequestInit(b.Connection, "main");
        await h.RunAsync();
        var initA = await a.NextRawAsync();
        var initB = await b.NextRawAsync();
        ReferenceEquals(initA, initB).Should().BeTrue("同一個 mode、同一組版本的 init 只組一次");

        h.Source.Current = h.Source.Current with { AccountsJson = AccountsA };
        h.Gateway.MarkDirty(BrowserParts.Accounts);
        h.Time.Advance(TimeSpan.FromMilliseconds(500));
        await h.RunAsync();
        var pushA = await a.NextRawAsync();
        var pushB = await b.NextRawAsync();
        ReferenceEquals(pushA, pushB).Should().BeTrue("各連線共用同一份字串");
    }

    [Fact]
    public void 目錄類事件推全部用量只推accounts()
    {
        BrowserGatewayWiring.PartsFor(SyncClasses.Catalog).Should().Be(BrowserParts.All);
        BrowserGatewayWiring.PartsFor(SyncClasses.Usage).Should().Be(BrowserParts.Accounts);
        // WP4.2 D13：對話的 meta、item 類由變更通知處理，不再讓帳戶與裝置快照每次重讀
        BrowserGatewayWiring.PartsFor(SyncClasses.Meta).Should().Be(BrowserParts.None);
        BrowserGatewayWiring.PartsFor(SyncClasses.Item).Should().Be(BrowserParts.None);
        BrowserGatewayWiring.PartsFor("future").Should().Be(BrowserParts.All, "其他類別保守地當全部");
    }

    // ───── AccountViewBuilder（主機） ─────

    [Fact]
    public async Task 讀快照不受進行中的寫入交易阻擋且讀到提交前的狀態()
    {
        await using var host = await HubTestHost.StartAsync();
        var builder = host.Services.GetRequiredService<AccountViewBuilder>();
        var writer = host.Services.GetRequiredService<SyncWriter>();
        var catalog = host.Services.GetRequiredService<Catalog>();
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var write = writer.WriteAsync(ctx =>
        {
            if (catalog.SetPreference(ctx, "owner", "theme", new JValue("light"), null).Rejected is { } rejected)
                throw new InvalidOperationException(rejected);
            inside.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        });
        try
        {
            inside.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("寫入交易開著");
            var read = Task.Run(() => builder.Read(BrowserParts.All, null, host.Clock.NowMs));
            (await Task.WhenAny(read, Task.Delay(5000))).Should().Be(read, "唯讀的 deferred 交易不等寫入者");
            JObject.Parse((await read).SettingsJson)["theme"]!.Value<string>().Should().Be("dark", "讀到提交前的狀態");
        }
        finally
        {
            release.Set();
        }
        await write;
        var after = builder.Read(BrowserParts.Settings, builder.Read(BrowserParts.All, null, host.Clock.NowMs), host.Clock.NowMs);
        JObject.Parse(after.SettingsJson)["theme"]!.Value<string>().Should().Be("light");
    }

    [Fact]
    public async Task JSON欄位壞掉時那一格是null整份照常()
    {
        await using var host = await HubTestHost.StartAsync();
        var deviceId = await AddDeviceAsync(host, "壞資料的電腦");
        var la = SyncIds.NewLogicalAccountId();
        await host.Services.GetRequiredService<SyncWriter>().WriteAsync(ctx =>
        {
            ctx.Execute("INSERT INTO logical_accounts(logical_account_id, provider, identity_key, label, color, sort_order, version, created_at) "
                + "VALUES($id, 'Claude', 'claude:org:a@example.com', '工作', '#D97757', 0, 1, 1)", ("$id", la));
            // 不認得的 provider 略過（2026-10-22 起四種都同步）
            ctx.Execute("INSERT INTO logical_accounts(logical_account_id, provider, identity_key, label, color, sort_order, version, created_at) "
                + "VALUES($id, '0', 'x:numeric', '數字', '#000000', 1, 1, 1)", ("$id", SyncIds.NewLogicalAccountId()));
            ctx.Execute("INSERT INTO logical_accounts(logical_account_id, provider, identity_key, label, color, sort_order, version, created_at) "
                + "VALUES($id, 'Gemini', 'gemini:x', '不認得', '#000000', 2, 1, 1)", ("$id", SyncIds.NewLogicalAccountId()));
            ctx.Execute("INSERT INTO account_links(logical_account_id, device_id, local_account_id, config_kind, status, identity_ok, sessions_json, version) "
                + "VALUES($id, $d, 'acct-1', 'isolated', 'ready', 1, '{bad', 1)", ("$id", la), ("$d", deviceId));
            ctx.Execute("INSERT INTO usage_latest(logical_account_id, snapshot_json, source_device_id, source, fetched_at) VALUES($id, 'not json', $d, 'api', 1)",
                ("$id", la), ("$d", deviceId));
            ctx.Execute("INSERT INTO device_streams(device_id, policy_json) VALUES($d, '[')", ("$d", deviceId));
            ctx.Execute("INSERT INTO device_cli(device_id, provider, state_json, updated_at) VALUES($d, 'Claude', '{', 1)", ("$d", deviceId));
            ctx.Execute("INSERT INTO prefs(key, value_json, version, updated_at, updated_by) VALUES('theme', '{oops', 1, 1, 'owner')");
            ctx.Execute("INSERT INTO prefs(key, value_json, version, updated_at, updated_by) VALUES('fontSize', '16', 1, 1, 'owner')");
        });

        var data = host.Services.GetRequiredService<AccountViewBuilder>().Read(BrowserParts.All, null, host.Clock.NowMs);
        var accounts = JArray.Parse(data.AccountsJson);
        accounts.Should().ContainSingle("不認得的 provider 不列");
        var account = accounts[0];
        account["id"]!.Value<string>().Should().Be(la);
        account["usage"]!.Type.Should().Be(JTokenType.Null, "壞的 snapshot_json");
        account["sessions"]!.ToString(Newtonsoft.Json.Formatting.None).Should().Be("""{"open":0,"running":0,"waiting":0,"background":0,"programs":0,"subagents":0}""", "壞的 sessions_json");
        account["status"]!.Value<string>().Should().Be("ready");
        account["devices"]![0]!["status"]!.Value<string>().Should().Be("ready");
        JArray.Parse(data.DevicesJson)[0]!["remoteControlLevel"]!.Value<string>().Should().Be("Off", "壞的 policy_json");
        JObject.Parse(data.DevicesCliJson).Count.Should().Be(0, "壞的 state_json 略過");
        var settings = JObject.Parse(data.SettingsJson);
        settings["theme"]!.Value<string>().Should().Be("dark", "壞的值給預設");
        settings["fontSize"]!.Value<int>().Should().Be(16);
    }

    // ───── 整合（主機＋裝置） ─────

    [Fact]
    public async Task ready回init帶瀏覽器的remote與各部分()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        var init = await owner.Hub.ReadyAsync();
        init["type"]!.Value<string>().Should().Be("init");
        init["mode"]!.Value<string>().Should().Be("main");
        init["popoutConversationId"]!.Type.Should().Be(JTokenType.Null);
        init["app"]!["version"]!.Value<string>().Should().Be(BuildInfo.Current.Version);
        var remote = init["remote"]!;
        remote["mode"]!.Value<string>().Should().Be("browser");
        remote["deviceId"]!.Type.Should().Be(JTokenType.Null);
        remote["sync"]!.Type.Should().Be(JTokenType.Null);
        remote["devices"]!.Should().BeOfType<JArray>().Which.Should().BeEmpty();
        init["cli"]!.Should().BeOfType<JObject>().Which.Count.Should().Be(0);
        init["devicesCli"]!.Should().BeOfType<JObject>().Which.Count.Should().Be(0);
        init["conversations"]!.Should().BeOfType<JArray>().Which.Should().BeEmpty();
        init["accounts"]!.Should().BeOfType<JArray>().Which.Should().BeEmpty();
        init["layout"]!.Type.Should().Be(JTokenType.Null);
        init["providers"]!.Select(p => p["key"]!.Value<string>()).Should().Equal(["Claude", "Codex", "Antigravity", "Grok"]);
        init["providers"]!.Should().OnlyContain(p => p["safeMode"]!.Value<bool>() == false);
        init["settings"]!.Should().BeOfType<JObject>().Which.Properties().Select(p => p.Name)
            .Should().BeEquivalentTo(["theme", "fontSize", "enterToSend", "notifications"]);
    }

    [Fact]
    public async Task ready回init_有對話時conversations是清單()
    {
        // WP4.2 步 10：init 的 conversations 是伺服器的對話清單（沒有對話時仍是空的，見上一則）
        await using var host = await HubTestHost.StartAsync();
        var deviceId = await AddDeviceAsync(host, "有對話的電腦");
        (await MyriagentHub.Tests.Sync.ConversationEventLogTests.Append(host, deviceId,
            MyriagentHub.Tests.Sync.ConversationEventLogTests.Ev(1, ConversationEventKinds.Created, MyriagentHub.Tests.Sync.ConversationSamples.Meta("伺服器上的對話"))))
            .Ack.Results.Should().OnlyContain(r => r.Rejected == null);
        await using var owner = await LoginAndConnectAsync(host);
        var init = await owner.Hub.ReadyAsync();
        var summary = init["conversations"]!.Should().BeOfType<JArray>().Which.Should().ContainSingle().Subject;
        summary["id"]!.Value<string>().Should().Be(MyriagentHub.Tests.Sync.ConversationSamples.Conv);
        summary["title"]!.Value<string>().Should().Be("伺服器上的對話");
        summary["provider"]!.Value<string>().Should().Be("Claude");
        summary["deviceName"]!.Value<string>().Should().Be("有對話的電腦");
        summary["deviceOnline"]!.Value<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task popout照ready回mode且popoutConversationId為null()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        var init = await owner.Hub.ReadyAsync("popout");
        init["mode"]!.Value<string>().Should().Be("popout");
        init["popoutConversationId"]!.Type.Should().Be(JTokenType.Null, "第 3 階段沒有對話");
    }

    [Fact]
    public async Task 兩條連線各自收到init()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        await using var tab = await BrowserHubClient.ConnectAsync(host, owner.Http);
        (await owner.Hub.ReadyAsync())["mode"]!.Value<string>().Should().Be("main");
        await tab.AssertNoMessageAsync("init", 300);
        (await tab.ReadyAsync("popout"))["mode"]!.Value<string>().Should().Be("popout");
        await owner.Hub.AssertNoMessageAsync("init", 300);
    }

    [Fact]
    public async Task 裝置上傳帳戶連結後瀏覽器收到accounts()
    {
        await using var host = await HubTestHost.StartAsync();
        var deviceId = await AddDeviceAsync(host, "書房電腦");
        await using var owner = await LoginAndConnectAsync(host);
        (await owner.Hub.ReadyAsync())["accounts"]!.Should().BeOfType<JArray>().Which.Should().BeEmpty();

        var outcome = await AppendAsync(host, deviceId, Batch(Ev(1, SyncEventKinds.LinkUpsert, Link("acct-1", "claude:org1:a@example.com"))));
        outcome.Ack.Results.Single().Rejected.Should().BeNull();
        var message = await owner.Hub.WaitForAsync("accounts", m => m["accounts"]!.Any());
        var account = message["accounts"]!.Single();
        account["id"]!.Value<string>().Should().Be(LatestAccount(host).LogicalAccountId);
        account["label"]!.Value<string>().Should().Be("工作帳號");
        account["status"]!.Value<string>().Should().Be("ready");
        var cell = account["devices"]!.Single();
        cell["deviceId"]!.Value<string>().Should().Be(deviceId);
        cell["deviceName"]!.Value<string>().Should().Be("書房電腦");
        cell["status"]!.Value<string>().Should().Be("ready");
        account.ToString().Should().NotContain("acct-1", "本機帳戶 id 不離開電腦").And.NotContain("claude:org1", "身分鍵不送");
    }

    [Fact]
    public async Task 裝置上線下線時收到devices()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host, "客廳電腦");
        await using var owner = await LoginAndConnectAsync(host);
        var init = await owner.Hub.ReadyAsync();
        init["remote"]!["devices"]!.Single()["online"]!.Value<bool>().Should().BeFalse();

        await using (var client = await DeviceHubClient.ConnectAsync(host, device.Token))
        {
            await client.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host));
            await owner.Hub.WaitForAsync("devices", m => m["devices"]!.Single()["online"]!.Value<bool>());
            (await AppendAsync(host, device.Id, Batch(Ev(1, SyncEventKinds.LinkUpsert, Link("acct-1", "claude:org1:a@example.com"))))).Ack.Results.Single().Rejected.Should().BeNull();
            await owner.Hub.WaitForAsync("accounts", m => m["accounts"]!.FirstOrDefault()?["statusText"]?.Value<string>() == "1 台已登入（1 台在線）");
        }

        // 上下線同時標 accounts 與 devices，同一輪依序送 accounts、devices（WaitForAsync 會略過前面的其他訊息，所以先等 accounts）
        var offline = await owner.Hub.WaitForAsync("accounts", m => m["accounts"]!.FirstOrDefault()?["statusText"]?.Value<string>() == "1 台已登入（0 台在線）");
        offline["accounts"]![0]!["status"]!.Value<string>().Should().Be("ready", "離線的那台仍是登入的");
        await owner.Hub.WaitForAsync("devices", m => !m["devices"]!.Single()["online"]!.Value<bool>());
    }

    [Fact]
    public async Task 用量上傳後accounts帶用量與history()
    {
        await using var host = await HubTestHost.StartAsync();
        var deviceId = await AddDeviceAsync(host);
        (await AppendAsync(host, deviceId, Batch(Ev(1, SyncEventKinds.LinkUpsert, Link("acct-1", "claude:org1:a@example.com"))))).Ack.Results.Single().Rejected.Should().BeNull();
        var la = LatestAccount(host).LogicalAccountId;
        host.Services.GetRequiredService<ConnectionRegistry>().Add(new DeviceConnection("dc-1", deviceId, () => { }, new UpstreamContext(deviceId, null, null)), host.Clock.NowMs);
        var coordinator = host.Services.GetRequiredService<UsageCoordinator>();
        await coordinator.ReassessAsync();

        await using var owner = await LoginAndConnectAsync(host);
        await owner.Hub.ReadyAsync();
        var publishedAt = host.Clock.NowMs;
        // 第二個窗是裝置跳脫過的鍵（UsageWindowKeyRules.Encode("weekly:Opus")）：伺服器照存、閘道送還原後的鍵（WP3.7 步驟 2）
        var shared = UsageRulesTests.Usage(UsageRules.SourceApi, 30);
        shared = shared with { Windows = [.. shared.Windows!, new SharedUsageWindow(UsageWindowKeyRules.Encode("weekly:Opus")!, "每週・Opus", 12, null, false)] };
        (await coordinator.PublishAsync(deviceId, new UsagePublish(la, 1, shared))).Should().Be(UsagePublishResult.Accepted);

        var message = await owner.Hub.WaitForAsync("accounts", m => m["accounts"]!.FirstOrDefault()?["usage"] is JObject);
        var usage = message["accounts"]![0]!["usage"]!;
        usage["source"]!.Value<string>().Should().Be("api");
        usage["windows"]!.Select(w => w["key"]!.Value<string>()).Should().Equal(["five_hour", "weekly:Opus"]);
        var window = usage["windows"]![0]!;
        window["percent"]!.Value<double>().Should().Be(30);
        window["severity"]!.Value<string>().Should().Be("ok");
        window["history"]!.Should().ContainSingle();
        window["history"]![0]![0]!.Value<long>().Should().Be(publishedAt, "歷史點以伺服器收到的時間記");
        window["history"]![0]![1]!.Value<double>().Should().Be(30);
        usage["windows"]![1]!["history"]!.Should().ContainSingle("走勢照跳脫後的鍵累積，送出時才還原");
        usage["windows"]![1]!["history"]![0]![1]!.Value<double>().Should().Be(12);
        message["accounts"]![0]!["devices"]![0]!["usagePoller"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task 撤銷的裝置排最後()
    {
        await using var host = await HubTestHost.StartAsync();
        var devices = await AddDevicesInOrderAsync(host, "甲", "乙", "丙");
        try
        {
            await host.Services.GetRequiredService<PairingService>().RevokeAsync(devices[1].Id, "retired", null, null);
            await using var owner = await LoginAndConnectAsync(host);
            var list = (await owner.Hub.ReadyAsync())["remote"]!["devices"]!;
            list.Select(d => d["name"]!.Value<string>()).Should().Equal(["甲", "丙", "乙"]);
            list[2]!["revoked"]!["reason"]!.Value<string>().Should().Be("retired");
            list[0]!["revoked"]!.Type.Should().Be(JTokenType.Null);
            list.Should().OnlyContain(d => d["verified"]!.Type == JTokenType.Null && d["windowsUser"] == null);
        }
        finally
        {
            foreach (var device in devices)
                device.Dispose();
        }
    }

    [Fact]
    public async Task devicesCli帶各電腦的CLI()
    {
        await using var host = await HubTestHost.StartAsync();
        var devices = await AddDevicesInOrderAsync(host, "甲", "乙");
        try
        {
            var view = new DeviceCliView(true, "2.1.0", "npm", "2.1.1", true, 1, 0, null, 1000);
            (await AppendAsync(host, devices[0].Id, Batch(Ev(1, SyncEventKinds.CliState,
                new CliStatePayload(new Dictionary<string, DeviceCliView> { ["Claude"] = view, ["Codex"] = view with { Version = "0.9.0" } })))))
                .Ack.Results.Single().Rejected.Should().BeNull();
            await using var owner = await LoginAndConnectAsync(host);
            var init = await owner.Hub.ReadyAsync();
            var cli = init["devicesCli"]!.Should().BeOfType<JObject>().Subject;
            cli.Properties().Select(p => p.Name).Should().Equal([devices[0].Id], "只有回報過 CLI 的電腦");
            cli[devices[0].Id]!["Claude"]!["version"]!.Value<string>().Should().Be("2.1.0");
            cli[devices[0].Id]!["Claude"]!["updateAvailable"]!.Value<bool>().Should().BeTrue();
            cli[devices[0].Id]!["Codex"]!["version"]!.Value<string>().Should().Be("0.9.0");

            // 之後的變更以 cli 推送
            (await AppendAsync(host, devices[1].Id, Batch(Ev(1, SyncEventKinds.CliState,
                new CliStatePayload(new Dictionary<string, DeviceCliView> { ["Claude"] = view })))))
                .Ack.Results.Single().Rejected.Should().BeNull();
            var pushed = await owner.Hub.WaitForAsync("cli", m => m["devicesCli"]![devices[1].Id] is not null);
            pushed["cli"]!.Should().BeOfType<JObject>().Which.Count.Should().Be(0);
        }
        finally
        {
            foreach (var device in devices)
                device.Dispose();
        }
    }

    [Fact]
    public async Task init的app帶build()
    {
        // WP4.6 步 8（D13）：網頁比對第一則與之後的 init 的 app.build，換版（含回滾到沒有這個鍵的舊伺服器）時重新載入或顯示橫幅
        await using var host = await HubTestHost.StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        var app = (JObject)(await owner.Hub.ReadyAsync())["app"]!;
        app.Properties().Select(p => p.Name).Should().Equal("version", "build", "commitAt");
        app["build"]!.Value<string>().Should().Be(BuildInfo.Current.Build, "同 /healthz 的 build（提交前 7 碼，本機建置可能是 dev）");
        app["version"]!.Value<string>().Should().Be(BuildInfo.Current.Version);
        BuildInfo.Current.Build.Should().NotBeNullOrEmpty();
        // 版號前面的日期時間：這個 build 的提交時間（Unix 毫秒；Directory.Build.targets 在 repo 裡建置時一定寫進組件）
        BuildInfo.Current.CommitTime.Should().NotBeNull();
        app["commitAt"]!.Value<long>().Should().Be(BuildInfo.Current.CommitTime!.Value.ToUnixTimeMilliseconds());
    }

    // ───── 工具 ─────

    private static ClientOutboxMessage Toast(string text) => new("toast", BrowserCommandRules.ToastJson(text, BrowserCommandRules.ToastLevel));

    private static void ChangeEverything(GatewayHarness h, int round)
    {
        h.Source.Current = new BrowserSnapshotData(
            $$"""[{"id":"la_{{round}}"}]""",
            $$"""[{"id":"d_{{round}}"}]""",
            $$$"""{"d_{{{round}}}":{}}""",
            $$"""{"theme":"dark","fontSize":{{12 + round}},"enterToSend":true,"notifications":true}""");
        h.Gateway.MarkDirty(BrowserParts.All);
        h.Time.Advance(TimeSpan.FromMilliseconds(500));
    }
}
