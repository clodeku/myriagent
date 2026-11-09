using System.Text.RegularExpressions;
using Myriagent.Agents;
using Myriagent.Remote;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Protocol.Remote;

/// <summary>
/// 遠端指令的型錄與形狀（WP4.1 步 6；設計 14.11 D6、D15、D18、D53）。web-protocol 第二節每個指令都在型錄或明列只在本機的檢查在
/// MyriagentHub.Tests 的 RemoteCommandCatalogConsistencyTests（那邊有讀表的工具與 BrowserCommandRules）
/// </summary>
public sealed partial class RemoteCommandCatalogTests
{
    // ───────────────────────────── 型錄 ─────────────────────────────

    [Fact]
    public void 型錄的名稱不重複_與伺服器處理和本機專屬的不重疊()
    {
        var names = RemoteCommandCatalog.All.Select(s => s.Name).ToList();
        names.Should().OnlyHaveUniqueItems();
        names.Count.Should().Be(40, "第 4 階段由電腦執行的 28 個網頁指令＋語音訊息 voiceMessage（2026-10-23）＋conversationCreate＋遠端執行指令 remoteExec（2026-10-25）＋排程頁的 scheduleAction、scheduleAgent（2026-10-26）＋Claude 遠端登入貼回授權碼的 accountLoginCode（2026-10-26）＋瀏覽器新對話的資料夾挑選 listFolders（2026-10-27）＋電腦狀態面板的檔案傳輸 deviceBrowse、deviceFetch、devicePut（2026-11-09）＋整個資料夾的 deviceTree、deviceFetchMany（2026-11-09）");
        names.Should().NotIntersectWith(RemoteCommandCatalog.ServerOnlyCommands);
        names.Should().NotIntersectWith(RemoteCommandCatalog.LocalOnlyCommands);
        RemoteCommandCatalog.ServerOnlyCommands.Should().NotIntersectWith(RemoteCommandCatalog.LocalOnlyCommands);
        names.Should().NotContain(n => RemoteCommandCatalog.IsLocalOnly(n));
    }

    [Fact]
    public void 對話指令第一個欄位是conversationId_其他指令沒有()
    {
        foreach (var spec in RemoteCommandCatalog.All)
        {
            if (spec.Route == RemoteRoute.Owner)
                spec.Fields[0].Should().Be(new RemoteField("conversationId", RemoteFieldKind.ConversationId), spec.Name);
            else
                spec.Fields.Should().NotContain(f => f.Kind == RemoteFieldKind.ConversationId, $"{spec.Name}：信封不帶 conv，電腦改驗 dev");
            spec.Route.Should().NotBe(RemoteRoute.ServerOnly, "伺服器處理的不在型錄裡有形狀");
            spec.BaseTier.Should().BeInRange(0, 2);
            spec.Fields.Select(f => f.Name).Should().OnlyHaveUniqueItems().And.NotContain("cmd");
            spec.Fields.Where(f => f.Kind == RemoteFieldKind.Choice).All(f => f.Values is { Count: > 0 }).Should().BeTrue($"{spec.Name}：列舉要列出名稱");
        }
    }

    [Fact]
    public void 路由_伺服器處理的與本機專屬的與沒列名的()
    {
        RemoteCommandCatalog.RouteOf("send").Should().Be(RemoteRoute.Owner);
        RemoteCommandCatalog.RouteOf("accountLogin").Should().Be(RemoteRoute.Device);
        RemoteCommandCatalog.RouteOf("conversationCreate").Should().Be(RemoteRoute.Device);
        RemoteCommandCatalog.RouteOf("ready").Should().Be(RemoteRoute.ServerOnly);
        RemoteCommandCatalog.RouteOf("newConversation").Should().Be(RemoteRoute.ServerOnly, "瀏覽器的草稿在伺服器");
        RemoteCommandCatalog.RouteOf("copyItem").Should().Be(RemoteRoute.ServerOnly, "伺服器回 clipboard");
        RemoteCommandCatalog.RouteOf("syncSetPolicy").Should().BeNull();
        RemoteCommandCatalog.RouteOf("takeover").Should().BeNull("第 5 階段");
        RemoteCommandCatalog.RouteOf("Send").Should().BeNull("大小寫有別");
        RemoteCommandCatalog.RouteOf(null).Should().BeNull();
        RemoteCommandCatalog.Find("ready").Should().BeNull();

        RemoteCommandCatalog.IsLocalOnly("syncLoadRemoteAudit").Should().BeTrue("sync 後面接大寫字母的一律只在本機");
        RemoteCommandCatalog.IsLocalOnly("openPath").Should().BeTrue();
        RemoteCommandCatalog.IsLocalOnly("sync").Should().BeFalse();
        RemoteCommandCatalog.IsLocalOnly("syncing").Should().BeFalse();
        RemoteCommandCatalog.IsLocalOnly("send").Should().BeFalse();
    }

    [Fact]
    public void 只有所有電腦登出可以durable()
    {
        RemoteCommandCatalog.All.Where(s => s.DurableAllowed).Select(s => s.Name).Should().Equal("accountLogout");
    }

    [Fact]
    public void 卡片指令是approve_approvePlan_answer()
    {
        RemoteCommandCatalog.All.Where(s => s.IsCard).Select(s => s.Name).Should().BeEquivalentTo("answer", "approve", "approvePlan");
    }

    [Fact]
    public void 輸出白名單_每個指令都能回toast_其他照指令()
    {
        RemoteCommandCatalog.All.Should().OnlyContain(s => s.Outputs.Contains("toast"));
        RemoteCommandCatalog.AllowsOutput("listFiles", "files").Should().BeTrue();
        RemoteCommandCatalog.AllowsOutput("listFiles", "accountLogin").Should().BeFalse("被入侵的電腦不能借 listFiles 塞登入網址（D18）");
        RemoteCommandCatalog.AllowsOutput("fileSearch", "fileSearch").Should().BeTrue();
        RemoteCommandCatalog.AllowsOutput("fileFetch", "fileRef").Should().BeTrue();
        RemoteCommandCatalog.AllowsOutput("accountLogin", "accountLogin").Should().BeTrue();
        RemoteCommandCatalog.AllowsOutput("send", "sendRejected").Should().BeTrue();
        RemoteCommandCatalog.AllowsOutput("stop", "files").Should().BeFalse();
        RemoteCommandCatalog.AllowsOutput("ready", "toast").Should().BeFalse("不在型錄");
        RemoteCommandCatalog.AllowsOutput("send", null).Should().BeFalse();
        RemoteCommandCatalog.All.SelectMany(s => s.Outputs).Distinct().Should().BeSubsetOf(["toast", "sendRejected", "files", "fileSearch", "fileRef", "fileTree", "fileRefs", "accountLogin"],
            "web-protocol 第二節附註：中樞回給這個視窗的訊息只有這幾種");
    }

    [Fact]
    public void 遠端不寫持久設定的限制_D15()
    {
        RemoteCommandCatalog.Find("setOption")!.Limits.Should().Be(RemoteLimits.ConversationOnly);
        RemoteCommandCatalog.Find("setFolder")!.Limits.Should().Be(RemoteLimits.NoRememberFolder);
        RemoteCommandCatalog.Find("accountRemove")!.Limits.Should().Be(RemoteLimits.KeepAccountData);
        RemoteCommandCatalog.Find("conversationCreate")!.Limits.Should().HaveFlag(RemoteLimits.ExplicitFolder).And.HaveFlag(RemoteLimits.NoRememberFolder);

        var remove = JObject.Parse("""{"cmd":"accountRemove","accountId":"acc-1","deleteData":true}""");
        var limited = RemoteCommandCatalog.ApplyLimits(remove);
        limited["deleteData"]!.Value<bool>().Should().BeFalse("遠端移除帳戶一律保留資料夾");
        remove["deleteData"]!.Value<bool>().Should().BeTrue("回傳複本，不改原物件");
        RemoteCommandCatalog.ApplyLimits(JObject.Parse("""{"cmd":"accountRemove","accountId":"acc-1"}""")).Property("deleteData").Should().BeNull("沒帶的本來就是 false");
        var send = JObject.Parse("""{"cmd":"send","conversationId":"5f3a0c1d2e3f4a5b6c7d8e9f0a1b2c3d","text":"hi"}""");
        RemoteCommandCatalog.ApplyLimits(send).ToString().Should().Be(send.ToString());
    }

    // ───────────────────────────── 形狀 ─────────────────────────────

    [Fact]
    public void 每個指令的合格樣本都通過()
    {
        foreach (var spec in RemoteCommandCatalog.All)
        {
            RemoteCommandShapeRules.Check(RemoteSamples.Required(spec)).Should().BeNull($"{spec.Name}：只有必填欄位");
            RemoteCommandShapeRules.Check(RemoteSamples.Full(spec)).Should().BeNull($"{spec.Name}：每個欄位都有");
        }
    }

    [Fact]
    public void 多一個鍵_少一個必填_都不合格()
    {
        foreach (var spec in RemoteCommandCatalog.All)
        {
            var extra = RemoteSamples.Full(spec);
            extra["unknownKey"] = "x";
            RemoteCommandShapeRules.Check(extra).Should().NotBeNull($"{spec.Name}：遠端指令不收不認得的鍵（和網頁指令不同）");

            foreach (var field in spec.Fields.Where(f => f.Required))
            {
                var missing = RemoteSamples.Full(spec);
                missing.Remove(field.Name);
                RemoteCommandShapeRules.Check(missing).Should().NotBeNull($"{spec.Name}.{field.Name} 是必填");
            }
        }
    }

    [Fact]
    public void 型別不對_數值列舉_null都不合格()
    {
        foreach (var spec in RemoteCommandCatalog.All)
        {
            foreach (var field in spec.Fields)
            {
                var number = RemoteSamples.Full(spec);
                number[field.Name] = 0;
                RemoteCommandShapeRules.Check(number).Should().NotBeNull($"{spec.Name}.{field.Name}：數字（數值列舉）一律拒絕");

                var nested = RemoteSamples.Full(spec);
                nested[field.Name] = new JObject { ["x"] = new JArray(1) };
                RemoteCommandShapeRules.Check(nested).Should().NotBeNull($"{spec.Name}.{field.Name}：物件");

                var nulled = RemoteSamples.Full(spec);
                nulled[field.Name] = JValue.CreateNull();
                (RemoteCommandShapeRules.Check(nulled) == null).Should().Be(field.Nullable, $"{spec.Name}.{field.Name}：null 只在 Nullable 時收");

                if (field.Kind == RemoteFieldKind.Bool)
                {
                    var text = RemoteSamples.Full(spec);
                    text[field.Name] = "true";
                    RemoteCommandShapeRules.Check(text).Should().NotBeNull($"{spec.Name}.{field.Name}：字串的 true 不是布林");
                }
            }
        }
    }

    [Fact]
    public void 列舉只收型錄列的名稱_大小寫有別()
    {
        RemoteCommandShapeRules.Check(RemoteSamples.Command("approve", ("choice", "allow"))).Should().BeNull();
        RemoteCommandShapeRules.Check(RemoteSamples.Command("approve", ("choice", "Allow"))).Should().NotBeNull();
        RemoteCommandShapeRules.Check(RemoteSamples.Command("approve", ("choice", "AllowForSession"))).Should().NotBeNull("中樞的列舉名稱不算，只收網頁的");
        RemoteCommandShapeRules.Check(RemoteSamples.Command("conversationCreate", ("permissionMode", "FullAccess"))).Should().BeNull();
        RemoteCommandShapeRules.Check(RemoteSamples.Command("conversationCreate", ("permissionMode", "4"))).Should().NotBeNull("數字字串也不是名稱");
        RemoteCommandShapeRules.Check(RemoteSamples.Command("cliInstall", ("provider", "Grok"))).Should().BeNull();
        RemoteCommandShapeRules.Check(RemoteSamples.Command("cliInstall", ("provider", "claude"))).Should().NotBeNull();
    }

    [Theory]
    [InlineData("permission", "FullAccess", true)]
    [InlineData("permission", "Ask", true)]
    [InlineData("permission", "default", true)]
    [InlineData("permission", "", true)]
    [InlineData("permission", "4", false)]
    [InlineData("permission", "fullAccess", false)]
    [InlineData("permission", " ", false)]
    [InlineData("model", "claude-opus-4-1", true)]
    [InlineData("effort", "high", true)]
    [InlineData("model", "4", true)]
    public void setOption的value_permission只收模式名稱(string key, string value, bool ok)
    {
        var command = RemoteSamples.Command("setOption", ("key", key), ("value", value));
        (RemoteCommandShapeRules.Check(command) == null).Should().Be(ok);
    }

    [Fact]
    public void 長度上限_代理文字16000字_其他4096字_整份96KB()
    {
        RemoteCommandShapeRules.Check(RemoteSamples.Command("send", ("text", new string('字', RemoteCommandShapeRules.MaxAgentTextLength)))).Should().BeNull();
        RemoteCommandShapeRules.Check(RemoteSamples.Command("send", ("text", new string('字', RemoteCommandShapeRules.MaxAgentTextLength + 1)))).Should().NotBeNull();
        RemoteCommandShapeRules.Check(RemoteSamples.Command("rename", ("title", new string('a', RemoteCommandShapeRules.MaxTextLength)))).Should().BeNull();
        RemoteCommandShapeRules.Check(RemoteSamples.Command("rename", ("title", new string('a', RemoteCommandShapeRules.MaxTextLength + 1)))).Should().NotBeNull();
        RemoteCommandShapeRules.Check(RemoteSamples.Command("setOption", ("key", "model"), ("value", new string('m', 129)))).Should().NotBeNull();

        // 16,000 個三位元組的字加上附言，超過 96 KB 的本文上限
        var big = RemoteSamples.Command("approve", ("choice", "deny"), ("message", new string('字', RemoteCommandShapeRules.MaxAgentTextLength)));
        RemoteCommandShapeRules.Check(big).Should().BeNull("48 KB");
        var answers = new JObject();
        for (var i = 0; i < 30; i++)
            answers["題目" + i] = new JArray(new string('字', 1200));
        RemoteCommandShapeRules.Check(RemoteSamples.Command("answer", ("answers", answers))).Should().NotBeNull("UTF-8 超過 96 KB");
    }

    [Fact]
    public void answers與uploads的形狀()
    {
        RemoteCommandShapeRules.Check(RemoteSamples.Command("answer", ("answers", new JObject { ["q"] = new JArray("a", "b") }))).Should().BeNull();
        RemoteCommandShapeRules.Check(RemoteSamples.Command("answer", ("answers", new JObject { ["q"] = "a" }))).Should().NotBeNull("值要是陣列");
        RemoteCommandShapeRules.Check(RemoteSamples.Command("answer", ("answers", new JObject { ["q"] = new JArray(1) }))).Should().NotBeNull();
        RemoteCommandShapeRules.Check(RemoteSamples.Command("answer", ("answers", new JObject { ["q"] = JArray.Parse("""[["a"]]""") }))).Should().NotBeNull();
        RemoteCommandShapeRules.Check(RemoteSamples.Command("answer", ("answers", new JArray()))).Should().NotBeNull();
        var many = new JObject();
        for (var i = 0; i <= RemoteCommandShapeRules.MaxAnswerQuestions; i++)
            many["q" + i] = new JArray("a");
        RemoteCommandShapeRules.Check(RemoteSamples.Command("answer", ("answers", many))).Should().NotBeNull("題數上限");

        RemoteCommandShapeRules.Check(RemoteSamples.Command("addUploaded", ("uploads", new JArray()))).Should().NotBeNull("至少一個");
        RemoteCommandShapeRules.Check(RemoteSamples.Command("addUploaded", ("uploads", new JArray(Enumerable.Range(0, 21).Select(_ => RemoteSamples.Upload()))))).Should().NotBeNull("最多 20 個");
        foreach (var (key, value) in new (string, JToken)[]
                 {
                     ("sha256", new string('B', 64)), ("size", -1), ("size", 1.5), ("size", "1"), ("size", JToken.Parse("100000000000000000000000000")),
                     ("size", AgentAttachmentRules.MaxAttachmentBytes + 1), ("uploadId", "../x"), ("name", ""), ("extra", 1),
                 })
        {
            var upload = RemoteSamples.Upload();
            upload[key] = value;
            RemoteCommandShapeRules.Check(RemoteSamples.Command("addUploaded", ("uploads", new JArray(upload)))).Should().NotBeNull($"{key}={value}");
        }
    }

    [Fact]
    public void 惡意的值只會不合格_不會丟例外()
    {
        JToken[] hostile =
        [
            JToken.Parse("100000000000000000000000000"), double.NaN, double.NegativeInfinity, JToken.Parse("1e308"), 1.5, long.MinValue, "", new string('x', 200_000),
            JToken.Parse("[[[[[[[[[[1]]]]]]]]]]"), JToken.Parse("""{"a":{"b":{"c":{"d":{"e":{"f":{"g":{"h":1}}}}}}}}"""),
            new JArray(JValue.CreateNull()), new JObject { ["uploadId"] = JValue.CreateNull() }, new JArray(new JObject { ["size"] = double.PositiveInfinity }),
            new JValue(new DateTime(2026, 10, 18)), new JValue(Guid.Empty), new JValue(new byte[] { 1 }), new JValue(new Uri("https://x")), JValue.CreateUndefined(),
        ];
        foreach (var spec in RemoteCommandCatalog.All)
        {
            foreach (var field in spec.Fields)
            {
                foreach (var value in hostile)
                {
                    var command = RemoteSamples.Full(spec);
                    command[field.Name] = value.DeepClone();
                    var act = () => RemoteCommandShapeRules.Check(command);
                    act.Should().NotThrow($"{spec.Name}.{field.Name}={value.Type}");
                    RemoteCommandCatalog.ApplyLimits(command);
                }

                if (field.Kind == RemoteFieldKind.Uploads)
                {
                    foreach (var value in hostile)
                    {
                        var upload = RemoteSamples.Upload();
                        upload["size"] = value.DeepClone();
                        var command = RemoteSamples.Full(spec);
                        command[field.Name] = new JArray(upload);
                        RemoteCommandShapeRules.Check(command).Should().NotBeNull($"size={value}");
                    }
                }
            }
        }

        RemoteCommandCatalog.ApplyLimits(new JObject { ["cmd"] = new JObject(), ["deleteData"] = true })["deleteData"]!.Value<bool>().Should().BeTrue("不是遠端指令的不改");
    }

    [Fact]
    public void 不是遠端指令的一律不合格()
    {
        RemoteCommandShapeRules.Check(null).Should().NotBeNull();
        RemoteCommandShapeRules.Check(new JObject()).Should().NotBeNull("沒有 cmd");
        RemoteCommandShapeRules.Check(new JObject { ["cmd"] = 1 }).Should().NotBeNull();
        RemoteCommandShapeRules.Check(new JObject { ["cmd"] = "ready" }).Should().NotBeNull("伺服器處理的");
        RemoteCommandShapeRules.Check(new JObject { ["cmd"] = "syncSetPolicy", ["remoteControlLevel"] = "Control" }).Should().NotBeNull("本機專屬的（遠端不能改政策）");
        RemoteCommandShapeRules.Check(new JObject { ["cmd"] = "setting", ["key"] = "instructions", ["value"] = "x" }).Should().NotBeNull();
        RemoteCommandShapeRules.Check(new JObject { ["cmd"] = "takeover" }).Should().NotBeNull("第 5 階段");
        RemoteCommandShapeRules.Check(new JObject { ["cmd"] = "Stop", ["conversationId"] = RemoteSamples.ConversationId }).Should().NotBeNull();
    }

    [Fact]
    public void 識別碼的格式()
    {
        RemoteCommandShapeRules.Check(RemoteSamples.Command("stop", ("conversationId", RemoteSamples.ConversationId.ToUpperInvariant()))).Should().NotBeNull("線上的對話 id 一律小寫（D38）");
        RemoteCommandShapeRules.Check(RemoteSamples.Command("stop", ("conversationId", "../" + RemoteSamples.ConversationId[3..]))).Should().NotBeNull();
        RemoteCommandShapeRules.Check(RemoteSamples.Command("cancelQueued", ("id", "a/b"))).Should().NotBeNull();
        RemoteCommandShapeRules.Check(RemoteSamples.Command("approve", ("requestDigest", "rd1:" + new string('A', 64)))).Should().NotBeNull("摘要是小寫十六進位");
        RemoteCommandShapeRules.Check(RemoteSamples.Command("approve", ("requestDigest", "rd2:" + new string('a', 64)))).Should().NotBeNull();
        RemoteCommandShapeRules.Check(RemoteSamples.Command("listFiles", ("requestId", "r 1"))).Should().NotBeNull();
        RemoteCommandShapeRules.Check(RemoteSamples.Command("accountLoginDevice", ("deviceId", "d_x"))).Should().NotBeNull();
    }

    // ───────────────────────────── 文件 ─────────────────────────────

    [Fact]
    public void 閘門各步與結果代碼和文件一致()
    {
        var sp = File.ReadAllText(Path.Combine(MyriagentHubExe.RepoRoot(), "docs", "sync-protocol.md"));
        var gate = sp[sp.IndexOf("### 14.6", StringComparison.Ordinal)..];
        GateRow().Matches(gate).Select(m => m.Groups[1].Value).Should().Equal(RemoteGateSteps.Order, "sync-protocol 14.6 的表與閘門的順序相同");

        var wpr = File.ReadAllText(Path.Combine(MyriagentHubExe.RepoRoot(), "docs", "web-protocol.md"));
        var start = wpr.IndexOf("### `commandResult`", StringComparison.Ordinal);
        var section = wpr[start..wpr.IndexOf("### `resync`", start, StringComparison.Ordinal)];
        CodeRow().Matches(section).Select(m => m.Groups[1].Value).Should().Equal(RemoteResultCodes.All, "web-protocol 的 commandResult 代碼表");
    }

    [GeneratedRegex(@"^\|\s*5[a-j]\s*\|\s*`([A-Za-z]+)`\s*\|", RegexOptions.Multiline)]
    private static partial Regex GateRow();

    [GeneratedRegex(@"^\|\s*`([a-z_]+)`\s*\|", RegexOptions.Multiline)]
    private static partial Regex CodeRow();
}
