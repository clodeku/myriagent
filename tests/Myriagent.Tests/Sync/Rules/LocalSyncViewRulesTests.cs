using Myriagent.Hub;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Rules;

public sealed class LocalSyncViewRulesTests
{
    private static (long? Before, long? BeforeSeq, int Limit)? Parse(string json) => LocalSyncViewRules.ParseAuditRequest(JObject.Parse(json));

    [Fact]
    public void ParseAuditRequest_沒帶是第一頁50筆()
    {
        Parse("""{"cmd":"syncLoadRemoteAudit"}""").Should().Be(((long?)null, (long?)null, 50));
        Parse("""{"before":null,"beforeSeq":null,"limit":null}""").Should().Be(((long?)null, (long?)null, 50), "null 當成省略");
        LocalSyncViewRules.AuditDefaultLimit.Should().Be(50);
        LocalSyncViewRules.AuditMaxLimit.Should().Be(200);
    }

    [Fact]
    public void ParseAuditRequest_limit1與200照收_before與beforeSeq一起()
    {
        Parse("""{"limit":1}""").Should().Be(((long?)null, (long?)null, 1));
        Parse("""{"limit":200}""").Should().Be(((long?)null, (long?)null, 200));
        Parse("""{"before":1791580000000,"beforeSeq":7,"limit":2}""").Should().Be(((long?)1791580000000, (long?)7, 2));
        Parse("""{"before":0}""").Should().Be(((long?)0, (long?)null, 50), "before 可以是 0");
        Parse("""{"before":1791580000000,"beforeSeq":0}""").Should().Be(((long?)1791580000000, (long?)0, 50));
        LocalSyncViewRules.ParseAuditRequest(new JObject { ["limit"] = 30, ["before"] = 5 }).Should().Be(((long?)5, (long?)null, 30), "程式組的 JObject 也收");
    }

    [Theory]
    [InlineData("""{"limit":0}""")]
    [InlineData("""{"limit":201}""")]
    [InlineData("""{"limit":-1}""")]
    [InlineData("""{"limit":"50"}""")]
    [InlineData("""{"limit":50.5}""")]
    [InlineData("""{"limit":50.0}""")]
    [InlineData("""{"limit":true}""")]
    [InlineData("""{"limit":99999999999999999999}""")]
    [InlineData("""{"before":-1}""")]
    [InlineData("""{"before":"1791580000000"}""")]
    [InlineData("""{"before":1.5}""")]
    [InlineData("""{"before":[1]}""")]
    [InlineData("""{"beforeSeq":3}""")]
    [InlineData("""{"before":null,"beforeSeq":3}""")]
    [InlineData("""{"before":5,"beforeSeq":-1}""")]
    [InlineData("""{"before":5,"beforeSeq":"3"}""")]
    public void ParseAuditRequest_不合格整個指令忽略(string json)
    {
        Parse(json).Should().BeNull();
    }

    [Fact]
    public void ConversationSyncView_可同步而且沒刪除時三個選項_刪除或不可同步只有localOnly()
    {
        var view = LocalSyncViewRules.ConversationSyncView("metadata", syncable: true, serverDeleted: false);
        JToken.DeepEquals(view, JObject.Parse("""{"policy":"metadata","choices":["full","metadata","localOnly"],"serverDeleted":false}""")).Should().BeTrue(view.ToString());

        var deleted = LocalSyncViewRules.ConversationSyncView("localOnly", syncable: true, serverDeleted: true);
        JToken.DeepEquals(deleted, JObject.Parse("""{"policy":"localOnly","choices":["localOnly"],"serverDeleted":true}""")).Should().BeTrue(deleted.ToString());

        var unsyncable = LocalSyncViewRules.ConversationSyncView("localOnly", syncable: false, serverDeleted: false);
        unsyncable["choices"]!.Values<string>().Should().Equal("localOnly");

        LocalSyncViewRules.ConversationSyncView("paused", syncable: true, serverDeleted: false)["policy"]!.Value<string>()
            .Should().Be("paused", "政策原樣（含權杖 DLP 設的 paused）");
    }

    [Fact]
    public void ConversationSyncView_的選項都是認得的政策()
    {
        foreach (var syncable in new[] { true, false })
        {
            foreach (var deleted in new[] { true, false })
            {
                var choices = LocalSyncViewRules.ConversationSyncView("full", syncable, deleted)["choices"]!.Values<string>().ToList();
                choices.Should().NotBeEmpty().And.OnlyContain(c => ConversationSyncPolicies.IsKnown(c) && c != ConversationSyncPolicies.Paused,
                    "paused 只由權杖 DLP 設定，不能選");
            }
        }
    }

    private static JObject Summary() => JObject.Parse("""
        {"id":"0123456789abcdef0123456789abcdef","accountId":"","provider":"Claude","title":"修正登入流程","group":"今天",
         "updatedAt":1791580000000,"workingDirectory":null,"customFolder":true,"running":false,"waiting":false,"unread":false}
        """);

    [Fact]
    public void ApplyReplicaSummary_覆寫running與waiting與工作資料夾_加deviceId()
    {
        var before = Summary();
        var summary = Summary();

        LocalSyncViewRules.ApplyReplicaSummary(summary, "d_0123456789ABCDEFGHJKMNPQRS", running: true, waiting: true, customFolder: true, folder: @"D:\owner\proj");

        summary["deviceId"]!.Value<string>().Should().Be("d_0123456789ABCDEFGHJKMNPQRS");
        summary["running"]!.Value<bool>().Should().BeTrue();
        summary["waiting"]!.Value<bool>().Should().BeTrue();
        summary["workingDirectory"]!.Value<string>().Should().Be(@"D:\owner\proj", "擁有者電腦上的自訂資料夾（只供顯示）");
        summary["customFolder"]!.Value<bool>().Should().BeTrue();
        foreach (var property in before.Properties().Where(p => p.Name is not "running" and not "waiting" and not "workingDirectory" and not "customFolder"))
            JToken.DeepEquals(summary[property.Name], property.Value).Should().BeTrue($"{property.Name} 不變");
        summary.Properties().Select(p => p.Name).Should().Equal(before.Properties().Select(p => p.Name).Append("deviceId"), "原本的鍵順序不變、deviceId 接在最後");

        var unknown = Summary();
        LocalSyncViewRules.ApplyReplicaSummary(unknown, null, running: false, waiting: false, customFolder: false, folder: null);
        unknown["deviceId"]!.Type.Should().Be(JTokenType.Null, "擁有者不明時是 null");
        unknown["customFolder"]!.Value<bool>().Should().BeFalse("擁有者那邊是對話自己的 workspace");
        unknown["workingDirectory"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public void 副本的工作資料夾_只有自訂資料夾才帶路徑()
    {
        var header = Header();
        LocalSyncViewRules.ApplyReplicaHeader(header, null, running: false, waiting: false, itemsAvailable: true, customFolder: false, folder: @"D:\owner\proj");
        header["customFolder"]!.Value<bool>().Should().BeFalse();
        header["workingDirectory"]!.Type.Should().Be(JTokenType.Null, "預設 workspace 不帶路徑（同瀏覽器版）");

        var noPath = Header();
        LocalSyncViewRules.ApplyReplicaHeader(noPath, null, running: false, waiting: false, itemsAvailable: true, customFolder: true, folder: "  ");
        noPath["customFolder"]!.Value<bool>().Should().BeTrue("擁有者的路徑太長沒送時仍是自訂資料夾");
        noPath["workingDirectory"]!.Type.Should().Be(JTokenType.Null);
    }

    private static JObject Header() => JObject.Parse("""
        {"id":"0123456789abcdef0123456789abcdef","accountId":"","provider":"Claude","title":"修正登入流程","model":"default","effort":"default",
         "permission":"Ask","permissionModes":[{"value":"Ask","label":"每步詢問","description":"d"}],
         "models":[{"value":"default","label":"預設（建議）","description":"","efforts":[]}],
         "commands":[{"name":"compact","description":"壓縮對話","argumentHint":""}],
         "workingDirectory":null,"customFolder":false,"recentFolders":["C:/repo/proj"],"isDraft":false,"running":false,
         "status":null,"startedAt":null,"lastTurn":null,"accountMissing":true,"restart":null}
        """);

    [Fact]
    public void ApplyReplicaHeader_accountMissing為false_recentFolders與commands是空陣列_工作資料夾是擁有者的_加replica()
    {
        var before = Header();
        var header = Header();

        LocalSyncViewRules.ApplyReplicaHeader(header, "d_0123456789ABCDEFGHJKMNPQRS", running: true, waiting: false, itemsAvailable: true,
            customFolder: true, folder: @"D:\owner\proj", accountId: "la_0123456789ABCDEFGHJKMNPQRS");

        header["accountMissing"]!.Value<bool>().Should().BeFalse("副本的 AccountId 是空字串，不是帳戶被移除");
        ((JArray)header["recentFolders"]!).Should().BeEmpty();
        ((JArray)header["commands"]!).Should().BeEmpty();
        header["deviceId"]!.Value<string>().Should().Be("d_0123456789ABCDEFGHJKMNPQRS");
        JToken.DeepEquals(header["replica"], JObject.Parse("""{"running":true,"waiting":false,"itemsAvailable":true,"accountId":"la_0123456789ABCDEFGHJKMNPQRS"}"""))
            .Should().BeTrue("擁有者那台的帳戶（邏輯帳戶）在 replica.accountId");
        header["accountId"]!.Value<string>().Should().BeEmpty("標頭自己的 accountId 照舊是空字串（網頁拿它判斷這台的帳戶）");
        header["running"]!.Value<bool>().Should().BeFalse("這台沒有副本的程序；擁有者的狀態在 replica");
        header["workingDirectory"]!.Value<string>().Should().Be(@"D:\owner\proj", "從這台換那台的資料夾之後要看得到結果（桌面遠端操作）");
        header["customFolder"]!.Value<bool>().Should().BeTrue();
        foreach (var property in before.Properties().Where(p => p.Name is not "accountMissing" and not "recentFolders" and not "commands"
                     and not "workingDirectory" and not "customFolder"))
            JToken.DeepEquals(header[property.Name], property.Value).Should().BeTrue($"{property.Name} 不變");
        header.Properties().Select(p => p.Name).Should().Equal(before.Properties().Select(p => p.Name).Append("deviceId").Append("replica"));
    }

    [Fact]
    public void ReplicaHeaderView_四個欄位_帳戶不明是null()
    {
        JToken.DeepEquals(LocalSyncViewRules.ReplicaHeaderView(false, true, false),
                JObject.Parse("""{"running":false,"waiting":true,"itemsAvailable":false,"accountId":null}"""))
            .Should().BeTrue();
        LocalSyncViewRules.ReplicaHeaderView(true, false, true, "la_X")["accountId"]!.Value<string>().Should().Be("la_X");
    }

    [Fact]
    public void ManualAuditExportPath_同資料夾_不等於解除配對的檔()
    {
        var folder = Path.Combine(Path.GetTempPath(), "myriagent-data", "remote-audit");
        var unpair = Path.Combine(folder, "d_0123456789ABCDEFGHJKMNPQRS.jsonl");
        var now = new DateTimeOffset(2026, 10, 20, 15, 30, 5, TimeSpan.FromHours(8));

        var path = LocalSyncViewRules.ManualAuditExportPath(unpair, now);

        path.Should().Be(Path.Combine(folder, "d_0123456789ABCDEFGHJKMNPQRS-20261020-153005.jsonl"));
        path.Should().NotBe(unpair);
        Path.GetDirectoryName(path).Should().Be(folder);
        LocalSyncViewRules.ManualAuditExportPath(unpair, now.AddSeconds(1)).Should().NotBe(path, "每次匯出的檔名不同");
        LocalSyncViewRules.ManualAuditExportPath("a.jsonl", now).Should().Be("a-20261020-153005.jsonl", "沒有資料夾時只有檔名");
    }

    // ───── 步 9：紀錄的一頁、回收區與匯出（D27、D28、D44） ─────

    private static RemoteAuditEntry Entry(long seq, long at, int? declared = 1, int? tier = 2, string? issuer = "Chrome・Windows", string state = "done") =>
        new(seq, at, "01J9QZ3K8M5R7T2V4W6X8Y0A1B", "send", "5f3a0c1d2e3f4a5b6c7d8e9f0a1b2c3d", issuer, declared, tier, false, "Control", false,
            state, null, null, false, 1);

    [Fact]
    public void AuditEntryView_鍵與WPR相同含seq()
    {
        var view = LocalSyncViewRules.AuditEntryView(Entry(7, 1791580000000));

        // web-protocol 的 remoteAudit entries 的鍵（第一節）再加 seq（D44，(at, seq) 翻頁）
        view.Properties().Select(p => p.Name).Should().BeEquivalentTo(
            ["seq", "at", "commandId", "cmd", "conversationId", "issuer", "tier", "fullAccess", "policy", "state", "code", "defaultPassword", "count"]);
        JToken.DeepEquals(view, JObject.Parse("""
            {"seq":7,"at":1791580000000,"commandId":"01J9QZ3K8M5R7T2V4W6X8Y0A1B","cmd":"send","conversationId":"5f3a0c1d2e3f4a5b6c7d8e9f0a1b2c3d",
             "issuer":"Chrome・Windows","tier":2,"fullAccess":false,"policy":"Control","state":"done","code":null,"defaultPassword":false,"count":1}
            """)).Should().BeTrue(view.ToString());

        LocalSyncViewRules.AuditEntryView(Entry(1, 1, declared: 1, tier: null))["tier"]!.Value<int>().Should().Be(1, "還沒有最終層級時用信封宣告的");
        LocalSyncViewRules.AuditEntryView(Entry(1, 1, declared: null, tier: null))["tier"]!.Type.Should().Be(JTokenType.Null);
        LocalSyncViewRules.AuditEntryView(Entry(1, 1, issuer: null))["issuer"]!.Type.Should().Be(JTokenType.Null, "標籤不合格時網頁顯示替代文字");
        LocalSyncViewRules.AuditEntryView(Entry(1, 1, state: "executing"))["state"]!.Value<string>().Should().Be("executing", "原樣，網頁顯示「執行中或不明」");
    }

    [Fact]
    public void AuditPage_hasMore()
    {
        var three = new[] { Entry(3, 30), Entry(2, 20), Entry(1, 10) };

        var more = LocalSyncViewRules.AuditPage(three, 2);
        more.Value<string>("type").Should().Be("remoteAudit");
        ((JArray)more["entries"]!).Select(e => e.Value<long>("seq")).Should().Equal(3, 2);
        more.Value<bool>("hasMore").Should().BeTrue("多讀的那一筆只用來判斷 hasMore");

        var exact = LocalSyncViewRules.AuditPage(three, 3);
        ((JArray)exact["entries"]!).Should().HaveCount(3);
        exact.Value<bool>("hasMore").Should().BeFalse("剛好 limit 筆＝沒有更早的");

        var empty = LocalSyncViewRules.AuditPage([], 50);
        ((JArray)empty["entries"]!).Should().BeEmpty();
        empty.Value<bool>("hasMore").Should().BeFalse();
        empty.Properties().Select(p => p.Name).Should().Equal("type", "entries", "hasMore");
    }

    [Fact]
    public void TrashView_expiresAt是30天後()
    {
        var at = 1791580000000L;
        var view = LocalSyncViewRules.TrashView([
            new TrashEntry("0123456789abcdef0123456789abcdef", new TrashInfo(at, "01J9QZ3K8M5R7T2V4W6X8Y0A1B", "Chrome・Windows", "修正登入流程")),
            new TrashEntry("1123456789abcdef0123456789abcdef", new TrashInfo(at - 1, null, null, null)),
        ]);

        view.Value<string>("type").Should().Be("trash");
        var entries = (JArray)view["entries"]!;
        JToken.DeepEquals(entries[0], JObject.Parse($$"""
            {"conversationId":"0123456789abcdef0123456789abcdef","title":"修正登入流程","trashedAt":{{at}},"issuer":"Chrome・Windows",
             "commandId":"01J9QZ3K8M5R7T2V4W6X8Y0A1B","expiresAt":{{at + 30L * 24 * 60 * 60 * 1000}}}
            """)).Should().BeTrue(entries[0].ToString());
        var expires = entries[0].Value<long>("expiresAt");
        ConversationTrashRules.IsExpired(at, expires).Should().BeFalse("到期那一刻還留著");
        ConversationTrashRules.IsExpired(at, expires + 1).Should().BeTrue("之後才清");
        entries[1]["title"]!.Type.Should().Be(JTokenType.Null, "沒有標題時網頁顯示替代文字");
        entries[1]["issuer"]!.Type.Should().Be(JTokenType.Null);
        entries.Select(e => e.Value<string>("conversationId")).Should().Equal(
            ["0123456789abcdef0123456789abcdef", "1123456789abcdef0123456789abcdef"], "順序照 ConversationTrash.List（新到舊）");
    }

    [Fact]
    public void AuditExportView_成功帶路徑_失敗只有錯誤()
    {
        JToken.DeepEquals(LocalSyncViewRules.AuditExportView(@"C:\data\remote-audit\a.jsonl", 3, null),
            JObject.Parse("""{"type":"remoteAuditExport","path":"C:\\data\\remote-audit\\a.jsonl","count":3,"error":null}""")).Should().BeTrue();
        JToken.DeepEquals(LocalSyncViewRules.AuditExportView(@"C:\x.jsonl", 3, "匯出失敗"),
            JObject.Parse("""{"type":"remoteAuditExport","path":null,"count":0,"error":"匯出失敗"}""")).Should().BeTrue("失敗時不給路徑");
    }

    [Fact]
    public void ConversationSyncView的每個choice都過ConversationPolicyCommandRules()
    {
        // 和 WP4.3 的指令規則交叉驗證：網頁照 choices 畫的選項一定收得下，沒列的一定被拒（不會出現按了沒反應的選項，也不會漏掉可以選的）
        string[] all = [ConversationSyncPolicies.Full, ConversationSyncPolicies.Metadata, ConversationSyncPolicies.LocalOnly];
        foreach (var syncable in new[] { true, false })
        {
            foreach (var deleted in new[] { true, false })
            {
                var choices = LocalSyncViewRules.ConversationSyncView("full", syncable, deleted)["choices"]!.Values<string>().ToList();
                var accepted = all.Where(p => ConversationPolicyCommandRules.Check(p, syncable, isDraft: false, isReplica: false, deleted, out _) is not null);
                choices.Should().Equal(accepted, $"syncable={syncable}、serverDeleted={deleted}");
            }
        }
    }
}
