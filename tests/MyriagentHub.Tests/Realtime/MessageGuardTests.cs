using System.Text;
using Myriagent.Remote;
using Myriagent.Sync;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Realtime;

/// <summary>瀏覽器 hub 的輸入檢查（web-protocol 第六節第 9～11 點；SEC-24）：進來的 JSON 一律不可信</summary>
public sealed class MessageGuardTests
{
    private static readonly string Account = SyncIds.NewLogicalAccountId();

    private static readonly string Device = SyncIds.NewDeviceId();

    [Fact]
    public void ready預設main()
    {
        Parse("""{"cmd":{"cmd":"ready"}}""").Command.Should().Be(new ReadyCommand("main"));
        Parse("""{"cmd":{"cmd":"ready","mode":null}}""").Command.Should().Be(new ReadyCommand("main"), "null 與省略相同");
        Parse("""{"cmd":{"cmd":"ready","mode":"popout","conversationId":"c1"}}""").Command.Should().Be(new ReadyCommand("popout"));
        Parse("""{"cmd":{"cmd":"ready","mode":"main","conversationId":null}}""").Error.Should().BeNull();
        Parse("""{"cmd":{"cmd":"ready","mode":"x"}}""").Error.Should().Be(BrowserErrorCodes.Invalid);
        Parse("""{"cmd":{"cmd":"ready","mode":1}}""").Error.Should().Be(BrowserErrorCodes.Invalid);
        Parse("""{"cmd":{"cmd":"ready","conversationId":5}}""").Error.Should().Be(BrowserErrorCodes.Invalid);
    }

    [Fact]
    public void 請求不是物件回invalid()
    {
        MessageGuard.ParseCmd(null).Error.Should().Be(BrowserErrorCodes.Invalid);
        MessageGuard.ParseCmd(JValue.CreateNull()).Error.Should().Be(BrowserErrorCodes.Invalid);
        MessageGuard.ParseCmd(new JValue("ready")).Error.Should().Be(BrowserErrorCodes.Invalid);
        MessageGuard.ParseCmd(new JArray(new JObject { ["cmd"] = "ready" })).Error.Should().Be(BrowserErrorCodes.Invalid);
        Parse("""{"cmd":"ready"}""").Error.Should().Be(BrowserErrorCodes.Invalid, "cmd 要是物件");
        Parse("""{"cmd":{"cmd":5}}""").Error.Should().Be(BrowserErrorCodes.Invalid, "cmd.cmd 要是字串");
        Parse("""{}""").Error.Should().Be(BrowserErrorCodes.Invalid);
    }

    [Fact]
    public void 頂層多出未知鍵回invalid()
    {
        Parse("""{"cmd":{"cmd":"ready"},"extra":1}""").Error.Should().Be(BrowserErrorCodes.Invalid);
        Parse("""{"cmd":{"cmd":"ready"},"__proto__":{}}""").Error.Should().Be(BrowserErrorCodes.Invalid);
        Parse("""{"cmd":{"cmd":"ready"},"CommandId":null}""").Error.Should().Be(BrowserErrorCodes.Invalid, "鍵名大小寫有別");
    }

    [Fact]
    public void commandId必須是ULID且原樣帶回()
    {
        var id = SyncIds.NewUlid(1_791_580_000_000);
        var ok = MessageGuard.ParseCmd(new JObject { ["commandId"] = id, ["cmd"] = new JObject { ["cmd"] = "ready" } });
        ok.Error.Should().BeNull();
        ok.CommandId.Should().Be(id);

        foreach (var bad in new[] { id.ToLowerInvariant(), id[..25], "8" + id[1..], id[..25] + "U" })
        {
            var parsed = MessageGuard.ParseCmd(new JObject { ["commandId"] = bad, ["cmd"] = new JObject { ["cmd"] = "ready" } });
            parsed.Error.Should().Be(BrowserErrorCodes.Invalid, bad);
            parsed.CommandId.Should().BeNull("不合格的 commandId 不帶回");
        }

        Parse("""{"commandId":5,"cmd":{"cmd":"ready"}}""").Error.Should().Be(BrowserErrorCodes.Invalid);
        Parse("""{"commandId":null,"cmd":{"cmd":"ready"}}""").Error.Should().BeNull("null 與省略相同");

        // 被拒的指令也帶回 commandId
        var rejected = MessageGuard.ParseCmd(new JObject { ["commandId"] = id, ["cmd"] = new JObject { ["cmd"] = "send" } });
        rejected.Error.Should().Be(BrowserErrorCodes.NotAvailable);
        rejected.CommandId.Should().Be(id);
    }

    [Fact]
    public void CommandIdOf只取頂層合格的ULID()
    {
        var id = SyncIds.NewUlid(1_791_580_000_000);
        MessageGuard.CommandIdOf(new JObject { ["commandId"] = id, ["cmd"] = "不解析" }).Should().Be(id, "限流時不解析整個請求也帶回");
        MessageGuard.CommandIdOf(new JObject { ["commandId"] = id.ToLowerInvariant() }).Should().BeNull();
        MessageGuard.CommandIdOf(new JObject { ["commandId"] = 5 }).Should().BeNull();
        MessageGuard.CommandIdOf(new JObject { ["cmd"] = new JObject { ["commandId"] = id } }).Should().BeNull("只看頂層");
        MessageGuard.CommandIdOf(new JArray(id)).Should().BeNull();
        MessageGuard.CommandIdOf(null).Should().BeNull();
    }

    [Fact]
    public void 帶envelope一律not_available()
    {
        var parsed = Parse("""{"envelope":{"to":"d_x"},"cmd":{"cmd":"ready"}}""");
        parsed.Error.Should().Be(BrowserErrorCodes.NotAvailable);
        parsed.Command.Should().BeNull();
        parsed.CmdName.Should().BeNull("envelope 的請求不送 toast");
    }

    [Fact]
    public void envelope為null視同沒帶() =>
        Parse("""{"envelope":null,"cmd":{"cmd":"ready"}}""").Command.Should().Be(new ReadyCommand("main"));

    [Fact]
    public void 深度超過8回invalid()
    {
        // 根物件算第 1 層：{"cmd":{"cmd":"layout","layout":{…}}} 已用掉 2 層
        Parse(Nested(8)).Error.Should().BeNull("剛好 8 層容器");
        Parse(Nested(9)).Error.Should().Be(BrowserErrorCodes.Invalid, "9 層容器");

        MessageGuard.WithinShape(NestedArrays(8)).Should().BeTrue();
        MessageGuard.WithinShape(NestedArrays(9)).Should().BeFalse();
    }

    [Fact]
    public void 字串超過4096回invalid()
    {
        var ok = new JObject { ["cmd"] = new JObject { ["cmd"] = "layout", ["note"] = new string('x', MessageGuard.MaxStringLength) } };
        MessageGuard.ParseCmd(ok).Error.Should().BeNull();
        var tooLong = new JObject { ["cmd"] = new JObject { ["cmd"] = "layout", ["note"] = new string('x', MessageGuard.MaxStringLength + 1) } };
        MessageGuard.ParseCmd(tooLong).Error.Should().Be(BrowserErrorCodes.Invalid);
        var longName = new JObject { ["cmd"] = new JObject { ["cmd"] = "layout", [new string('k', MessageGuard.MaxStringLength + 1)] = 1 } };
        MessageGuard.ParseCmd(longName).Error.Should().Be(BrowserErrorCodes.Invalid, "屬性名稱也算");
    }

    // ───── 上限分兩段（WP4.5 D31）：讀名稱之前值字串 16,000、屬性名 4096；轉送的指令之後只限本文，其他照 4096 ─────

    [Fact]
    public void send的text16000字_不是invalid_分類是NotAvailable()
    {
        MessageGuard.MaxRoutedStringLength.Should().Be(RemoteCommandShapeRules.MaxAgentTextLength).And.Be(16_000);
        var parsed = MessageGuard.ParseCmd(Request(new JObject { ["cmd"] = "send", ["conversationId"] = "c1", ["text"] = new string('x', 16_000) }));
        parsed.Error.Should().Be(BrowserErrorCodes.NotAvailable, "轉送給電腦的指令放寬到代理文字的上限，照分類回");
        parsed.CmdName.Should().Be("send");
        parsed.Body!["text"]!.Value<string>().Should().HaveLength(16_000, "原始的 cmd 物件原樣交給路由");
    }

    [Fact]
    public void send的text16001字_invalid()
    {
        var parsed = MessageGuard.ParseCmd(Request(new JObject { ["cmd"] = "send", ["conversationId"] = "c1", ["text"] = new string('x', 16_001) }));
        parsed.Error.Should().Be(BrowserErrorCodes.Invalid);
        parsed.CmdName.Should().BeNull("形狀不合格時和第 3 階段一樣不帶名稱");
        parsed.Body.Should().BeNull();
    }

    [Fact]
    public void setting的字串4097字仍是invalid()
    {
        // 合格的 setting 多帶一個不認得的欄位（內容忽略、仍受形狀上限約束）：放寬只給轉送給電腦的指令
        var fits = Request(new JObject { ["cmd"] = "setting", ["key"] = "theme", ["value"] = "light", ["note"] = new string('x', MessageGuard.MaxStringLength) });
        MessageGuard.ParseCmd(fits).Command.Should().Be(new SettingCommand("theme", "light"));
        var tooLong = MessageGuard.ParseCmd(Request(new JObject { ["cmd"] = "setting", ["key"] = "theme", ["value"] = "light", ["note"] = new string('x', MessageGuard.MaxStringLength + 1) }));
        tooLong.Error.Should().Be(BrowserErrorCodes.Invalid, "不轉送的指令照原本的 4096");
        tooLong.CmdName.Should().BeNull("和第一段不過一樣不帶名稱");
        tooLong.Command.Should().BeNull();

        // 其他不轉送的分類也一樣：被動指令、桌面專屬、伺服器處理的 not_available（newConversation）
        foreach (var name in new[] { "layout", "openFolder", "newConversation", "copyItem" })
        {
            MessageGuard.ParseCmd(Request(new JObject { ["cmd"] = name, ["note"] = new string('x', MessageGuard.MaxStringLength + 1) }))
                .Error.Should().Be(BrowserErrorCodes.Invalid, name);
        }
        // 只靠 sync 前綴認得的超長名稱（名稱本身就是值字串）
        MessageGuard.ParseCmd(Request(new JObject { ["cmd"] = "sync" + new string('x', MessageGuard.MaxStringLength) }))
            .Error.Should().Be(BrowserErrorCodes.Invalid);
    }

    [Fact]
    public void 屬性名4097仍是invalid()
    {
        MessageGuard.MaxNameLength.Should().Be(4096);
        var fits = Request(new JObject { ["cmd"] = "send", ["conversationId"] = "c1", ["text"] = "hi", [new string('k', MessageGuard.MaxNameLength)] = 1 });
        MessageGuard.ParseCmd(fits).Error.Should().Be(BrowserErrorCodes.NotAvailable);
        var tooLong = Request(new JObject { ["cmd"] = "send", ["conversationId"] = "c1", ["text"] = "hi", [new string('k', MessageGuard.MaxNameLength + 1)] = 1 });
        MessageGuard.ParseCmd(tooLong).Error.Should().Be(BrowserErrorCodes.Invalid, "屬性名不跟著值放寬，轉送的指令也一樣");
        var nested = Request(new JObject { ["cmd"] = "answer", ["answers"] = new JObject { [new string('q', MessageGuard.MaxNameLength + 1)] = new JArray("a") } });
        MessageGuard.ParseCmd(nested).Error.Should().Be(BrowserErrorCodes.Invalid, "巢狀物件的屬性名也算");
    }

    [Fact]
    public void 轉送的本文超過MaxBodyBytes_invalid()
    {
        // 多筆 answers（每個字串 4000 字、都在上限內）組出剛好 MaxBodyBytes 的本文，再多一個位元組
        var exact = AnswerOfBytes(RemoteCommandShapeRules.MaxBodyBytes);
        BodyBytes(exact).Should().Be(RemoteCommandShapeRules.MaxBodyBytes, "夾具");
        MessageGuard.ParseCmd(Request(exact)).Error.Should().Be(BrowserErrorCodes.NotAvailable, "剛好 MaxBodyBytes 照收");

        var over = AnswerOfBytes(RemoteCommandShapeRules.MaxBodyBytes + 1);
        BodyBytes(over).Should().Be(RemoteCommandShapeRules.MaxBodyBytes + 1, "夾具");
        var parsed = MessageGuard.ParseCmd(Request(over));
        parsed.Error.Should().Be(BrowserErrorCodes.Invalid, "超過 98,304 位元組");
        parsed.CmdName.Should().BeNull();
        parsed.Body.Should().BeNull();
    }

    [Fact]
    public void 檔案指令轉送之後字串4097照分類回_欄位上限交給路由的形狀檢查()
    {
        // WP4.7 合併前延後的四個不轉送、不放寬（4097 字 invalid）；合併後（WP4.7 計畫步 13）照轉送的指令放寬，路徑的 4096 由路由的型錄形狀檢查
        CommandRouteRules.Deferred.Should().BeEmpty();
        MessageGuard.ParseCmd(Request(new JObject { ["cmd"] = "listFiles", ["conversationId"] = "c1", ["path"] = new string('p', MessageGuard.MaxStringLength + 1) }))
            .Error.Should().Be(BrowserErrorCodes.NotAvailable);
        MessageGuard.ParseCmd(Request(new JObject { ["cmd"] = "fileSearch", ["conversationId"] = "c1", ["query"] = new string('p', MessageGuard.MaxStringLength + 1) }))
            .Error.Should().Be(BrowserErrorCodes.NotAvailable);
        MessageGuard.ParseCmd(Request(new JObject { ["cmd"] = "fileFetch", ["conversationId"] = "c1", ["path"] = new string('p', MessageGuard.MaxRoutedStringLength + 1) }))
            .Error.Should().Be(BrowserErrorCodes.Invalid, "第一段的放寬上限照舊");
        RemoteCommandShapeRules.Check(RemoteSamplesForGuard.ListFiles(new string('p', RemoteCommandShapeRules.MaxTextLength))).Should().BeNull();
        RemoteCommandShapeRules.Check(RemoteSamplesForGuard.ListFiles(new string('p', RemoteCommandShapeRules.MaxTextLength + 1))).Should().NotBeNull("路由的形狀檢查擋超過的路徑");
    }

    /// <summary>listFiles 的型錄形狀樣本（路由時的第二道檢查）</summary>
    private static class RemoteSamplesForGuard
    {
        public static JObject ListFiles(string path) => new() { ["cmd"] = "listFiles", ["conversationId"] = "c" + new string('0', 31), ["requestId"] = "r1", ["path"] = path };
    }

    [Fact]
    public void 轉送的指令深度與節點照舊()
    {
        var deep = (JToken)new JArray("a");
        for (var i = 0; i < 6; i++)
            deep = new JArray(deep);
        // 根、cmd 物件之外再 7 層陣列＝9 層容器
        MessageGuard.ParseCmd(Request(new JObject { ["cmd"] = "send", ["conversationId"] = "c1", ["text"] = "hi", ["deep"] = deep })).Error.Should().Be(BrowserErrorCodes.Invalid);
        MessageGuard.ParseCmd(Request(new JObject { ["cmd"] = "send", ["conversationId"] = "c1", ["text"] = "hi", ["deep"] = deep[0] })).Error.Should().Be(BrowserErrorCodes.NotAvailable, "8 層");
        // 根、cmd 物件、三個字串、陣列本身共 6 個節點
        var many = new JArray(Enumerable.Range(0, MessageGuard.MaxTokens - 5).Select(i => (object)i));
        MessageGuard.ParseCmd(Request(new JObject { ["cmd"] = "send", ["conversationId"] = "c1", ["text"] = "hi", ["n"] = many })).Error.Should().Be(BrowserErrorCodes.Invalid);
        many.RemoveAt(0);
        MessageGuard.ParseCmd(Request(new JObject { ["cmd"] = "send", ["conversationId"] = "c1", ["text"] = "hi", ["n"] = many })).Error.Should().Be(BrowserErrorCodes.NotAvailable);
    }

    [Fact]
    public void 帶envelope的請求照原本的上限()
    {
        var id = SyncIds.NewUlid(1_791_580_000_000);
        var fits = MessageGuard.ParseCmd(new JObject { ["commandId"] = id, ["envelope"] = new string('e', MessageGuard.MaxStringLength), ["cmd"] = new JObject { ["cmd"] = "send" } });
        fits.Error.Should().Be(BrowserErrorCodes.NotAvailable);
        fits.CommandId.Should().Be(id);
        var tooLong = MessageGuard.ParseCmd(new JObject { ["commandId"] = id, ["envelope"] = new string('e', MessageGuard.MaxStringLength + 1), ["cmd"] = new JObject { ["cmd"] = "send" } });
        tooLong.Error.Should().Be(BrowserErrorCodes.Invalid, "envelope 不轉送：照不轉送的指令再檢查一次 4096");
        tooLong.CommandId.Should().BeNull("和形狀不合格一樣不帶回");
    }

    [Fact]
    public void ClientState的字串仍是4096()
    {
        MessageGuard.ParseClientState(State(ids: 1, idLength: MessageGuard.MaxStringLength)).Should().NotBeNull();
        MessageGuard.ParseClientState(State(ids: 1, idLength: MessageGuard.MaxStringLength + 1)).Should().BeNull("ClientState 不放寬");
        var longName = State();
        longName[new string('k', MessageGuard.MaxStringLength + 1)] = 1;
        MessageGuard.ParseClientState(longName).Should().BeNull("屬性名也是 4096");
        MessageGuard.WithinShape(new JValue(new string('x', MessageGuard.MaxStringLength + 1))).Should().BeFalse("WithinShape 的預設是 4096");
        MessageGuard.WithinShape(new JValue(new string('x', MessageGuard.MaxStringLength + 1)), MessageGuard.MaxRoutedStringLength).Should().BeTrue();
    }

    [Fact]
    public void 節點數超過上限回invalid()
    {
        // 根、cmd 物件、"layout" 字串、陣列本身共 4 個節點
        var fits = new JArray(Enumerable.Range(0, MessageGuard.MaxTokens - 4).Select(i => (object)i));
        MessageGuard.ParseCmd(new JObject { ["cmd"] = new JObject { ["cmd"] = "layout", ["layout"] = fits } }).Error.Should().BeNull();
        var tooMany = new JArray(Enumerable.Range(0, MessageGuard.MaxTokens - 3).Select(i => (object)i));
        MessageGuard.ParseCmd(new JObject { ["cmd"] = new JObject { ["cmd"] = "layout", ["layout"] = tooMany } }).Error.Should().Be(BrowserErrorCodes.Invalid);
    }

    [Theory]
    [InlineData("""{"key":"theme","value":"light"}""")]
    [InlineData("""{"key":"theme","value":"system"}""")]
    [InlineData("""{"key":"fontSize","value":12}""")]
    [InlineData("""{"key":"fontSize","value":18}""")]
    [InlineData("""{"key":"enterToSend","value":false}""")]
    [InlineData("""{"key":"notifications","value":true}""")]
    public void setting漫遊鍵照PrefsRules驗證_合格(string body)
    {
        var parsed = Parse(Setting(body));
        parsed.Error.Should().BeNull();
        var command = parsed.Command.Should().BeOfType<SettingCommand>().Subject;
        var expected = JObject.Parse(body);
        command.Key.Should().Be((string)expected["key"]!);
        JToken.DeepEquals(command.Value, expected["value"]).Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"key":"theme","value":"blue"}""")]
    [InlineData("""{"key":"theme"}""")]
    [InlineData("""{"key":"fontSize","value":19}""")]
    [InlineData("""{"key":"fontSize","value":11}""")]
    [InlineData("""{"key":"fontSize","value":14.5}""")]
    [InlineData("""{"key":"fontSize","value":"14"}""")]
    [InlineData("""{"key":"fontSize","value":99999999999999999999999}""")]
    [InlineData("""{"key":"enterToSend","value":"true"}""")]
    [InlineData("""{"key":5,"value":true}""")]
    [InlineData("""{"value":true}""")]
    public void setting漫遊鍵照PrefsRules驗證_不合格(string body) =>
        Parse(Setting(body)).Error.Should().Be(BrowserErrorCodes.Invalid);

    [Theory]
    [InlineData("instructions")]
    [InlineData("closeToTray")]
    [InlineData("autoUpdateCli")]
    [InlineData("autoUpdateApp")]
    [InlineData("cliRestartPolicy")]
    [InlineData("claudeRemoteControl")]
    public void setting的instructions回desktop_only(string key)
    {
        var parsed = MessageGuard.ParseCmd(new JObject { ["cmd"] = new JObject { ["cmd"] = "setting", ["key"] = key, ["value"] = "x" } });
        parsed.Error.Should().Be(BrowserErrorCodes.DesktopOnly);
        parsed.CmdName.Should().Be("setting");
        parsed.Command.Should().BeNull();
        parsed.DeniedSettingKey.Should().Be(key, "處理端要對這個鍵寫一次稽核");
    }

    [Fact]
    public void 只有桌面專屬的設定鍵帶DeniedSettingKey()
    {
        MessageGuard.ParseCmd(new JObject { ["cmd"] = new JObject { ["cmd"] = "setting", ["key"] = "theme", ["value"] = "dark" } })
            .DeniedSettingKey.Should().BeNull("接受的設定");
        MessageGuard.ParseCmd(new JObject { ["cmd"] = new JObject { ["cmd"] = "setting", ["key"] = "__proto__", ["value"] = "x" } })
            .DeniedSettingKey.Should().BeNull("不在白名單的鍵是 invalid，不進每條連線的紀錄");
        MessageGuard.ParseCmd(new JObject { ["cmd"] = new JObject { ["cmd"] = "accountUpdate", ["accountId"] = Account, ["safeMode"] = true } })
            .DeniedSettingKey.Should().BeNull();
        MessageGuard.ParseCmd(new JObject { ["cmd"] = new JObject { ["cmd"] = "syncSetPolicy" } }).DeniedSettingKey.Should().BeNull();
    }

    [Theory]
    [InlineData("__proto__")]
    [InlineData("autoUpdate")]
    [InlineData("conversationDefaults")]
    [InlineData("Theme")]
    [InlineData("")]
    public void setting的其他鍵回invalid(string key) =>
        MessageGuard.ParseCmd(new JObject { ["cmd"] = new JObject { ["cmd"] = "setting", ["key"] = key, ["value"] = true } })
            .Error.Should().Be(BrowserErrorCodes.Invalid);

    [Fact]
    public void accountUpdate只收label與color()
    {
        Update(new JObject { ["label"] = "工作" }).Command.Should().Be(new AccountUpdateCommand(Account, "工作", null));
        Update(new JObject { ["color"] = "#D97757" }).Command.Should().Be(new AccountUpdateCommand(Account, null, "#D97757"));
        Update(new JObject { ["label"] = "工作", ["color"] = "#abc" }).Command.Should().Be(new AccountUpdateCommand(Account, "工作", "#abc"));
        Update(new JObject { ["label"] = "工作", ["color"] = null }).Command.Should().Be(new AccountUpdateCommand(Account, "工作", null), "null 與省略相同");

        Update(new JObject()).Error.Should().Be(BrowserErrorCodes.Invalid, "兩個都沒帶");
        Update(new JObject { ["label"] = null, ["color"] = null }).Error.Should().Be(BrowserErrorCodes.Invalid);
        Update(new JObject { ["label"] = 5 }).Error.Should().Be(BrowserErrorCodes.Invalid);
        Update(new JObject { ["color"] = true }).Error.Should().Be(BrowserErrorCodes.Invalid);

        var safe = Update(new JObject { ["safeMode"] = true });
        safe.Error.Should().Be(BrowserErrorCodes.DesktopOnly);
        safe.CmdName.Should().Be("accountUpdate");
        Update(new JObject { ["label"] = "工作", ["safeMode"] = false }).Error.Should().Be(BrowserErrorCodes.DesktopOnly, "帶 safeMode 就整筆不套用");
        Update(new JObject { ["shareCustomizations"] = false }).Error.Should().Be(BrowserErrorCodes.DesktopOnly);
        Update(new JObject { ["color"] = "#19C3A6", ["shareCustomizations"] = true }).Error.Should().Be(BrowserErrorCodes.DesktopOnly, "帶 shareCustomizations 就整筆不套用");

        foreach (var bad in new JToken[] { "a1", Device, Account.ToLowerInvariant(), 5, JValue.CreateNull() })
        {
            MessageGuard.ParseCmd(new JObject { ["cmd"] = new JObject { ["cmd"] = "accountUpdate", ["accountId"] = bad, ["label"] = "x" } })
                .Error.Should().Be(BrowserErrorCodes.Invalid, bad.ToString());
        }
    }

    [Fact]
    public void accountReorder的ids驗證()
    {
        var ids = Enumerable.Range(0, MessageGuard.MaxReorderIds).Select(_ => SyncIds.NewLogicalAccountId()).ToList();
        var ok = Reorder(new JArray(ids));
        ok.Error.Should().BeNull();
        ok.Command.Should().BeOfType<AccountReorderCommand>().Which.Ids.Should().Equal(ids);

        Reorder(new JArray(ids.Append(SyncIds.NewLogicalAccountId()))).Error.Should().Be(BrowserErrorCodes.Invalid, "501 筆");
        Reorder(new JArray(Account, Account)).Error.Should().Be(BrowserErrorCodes.Invalid, "重複");
        Reorder(new JArray(Account, 5)).Error.Should().Be(BrowserErrorCodes.Invalid, "非字串");
        Reorder(new JArray(Account, Device)).Error.Should().Be(BrowserErrorCodes.Invalid, "格式錯");
        Reorder(new JArray()).Error.Should().Be(BrowserErrorCodes.Invalid, "空陣列");
        Reorder(new JValue(Account)).Error.Should().Be(BrowserErrorCodes.Invalid, "不是陣列");
        Reorder(null).Error.Should().Be(BrowserErrorCodes.Invalid, "沒帶");
    }

    [Fact]
    public void deviceRename驗證id與name型別()
    {
        Rename(Device, "書房電腦").Command.Should().Be(new DeviceRenameCommand(Device, "書房電腦"));
        Rename(Device, "").Error.Should().BeNull("名稱的清理與長度交給 Catalog.RenameDevice");
        Rename(Account, "x").Error.Should().Be(BrowserErrorCodes.Invalid);
        Rename("d_x", "x").Error.Should().Be(BrowserErrorCodes.Invalid);
        Rename(Device, 5).Error.Should().Be(BrowserErrorCodes.Invalid);
        Rename(Device, null).Error.Should().Be(BrowserErrorCodes.Invalid);
        Rename(5, "x").Error.Should().Be(BrowserErrorCodes.Invalid);
    }

    [Fact]
    public void cmd物件的未知欄位忽略()
    {
        Parse("""{"cmd":{"cmd":"ready","mode":"main","extra":{"a":[1,2]},"__proto__":1}}""").Command.Should().Be(new ReadyCommand("main"));
        MessageGuard.ParseCmd(new JObject { ["cmd"] = new JObject { ["cmd"] = "deviceRename", ["deviceId"] = Device, ["name"] = "x", ["what"] = 1 } })
            .Command.Should().Be(new DeviceRenameCommand(Device, "x"));
    }

    [Theory]
    [InlineData("layout")]
    [InlineData("open")]
    [InlineData("close")]
    [InlineData("visible")]
    [InlineData("loadOlder")]
    public void 被動指令是PassiveCommand(string cmd)
    {
        var parsed = MessageGuard.ParseCmd(new JObject { ["cmd"] = new JObject { ["cmd"] = cmd } });
        parsed.Error.Should().BeNull();
        parsed.CmdName.Should().Be(cmd);
        parsed.Command.Should().Be(new PassiveCommand(cmd));
    }

    [Theory]
    [InlineData("openFolder", "desktop_only")]
    [InlineData("syncPair", "desktop_only")]
    [InlineData("syncSetPolicy", "desktop_only")]
    [InlineData("send", "not_available")]
    [InlineData("newConversation", "not_available")]
    public void 桌面專屬與尚未提供的指令帶名稱回錯誤(string cmd, string error)
    {
        var parsed = MessageGuard.ParseCmd(new JObject { ["cmd"] = new JObject { ["cmd"] = cmd } });
        parsed.Error.Should().Be(error);
        parsed.CmdName.Should().Be(cmd);
        parsed.Command.Should().BeNull();
    }

    [Theory]
    [InlineData("syncFoo")]
    [InlineData("syncA")]
    public void 只靠sync前綴認得的指令回desktop_only且名稱是共用的鍵(string cmd)
    {
        foreach (var name in new[] { cmd, cmd + new string('x', 4000) })
        {
            var parsed = MessageGuard.ParseCmd(new JObject { ["cmd"] = new JObject { ["cmd"] = name } });
            parsed.Error.Should().Be(BrowserErrorCodes.DesktopOnly);
            parsed.CmdName.Should().Be(BrowserCommandRules.SyncWildcardToast, "任意長度的名稱不原樣進每條連線的 toast 紀錄");
            parsed.Command.Should().BeNull();
        }
    }

    [Theory]
    [InlineData("Ready")]
    [InlineData("__proto__")]
    [InlineData("setSyncPolicy")]
    [InlineData("")]
    public void 不認得的指令回invalid且不帶名稱(string cmd)
    {
        var parsed = MessageGuard.ParseCmd(new JObject { ["cmd"] = new JObject { ["cmd"] = cmd } });
        parsed.Error.Should().Be(BrowserErrorCodes.Invalid);
        parsed.CmdName.Should().BeNull("任意字串不進每條連線的 toast 紀錄");
    }

    [Fact]
    public void ClientState驗證()
    {
        var ok = MessageGuard.ParseClientState(JObject.Parse("""{"visible":true,"focused":false,"openConversationIds":["c1","c2"],"tzOffsetMinutes":-480,"extra":1}"""));
        ok.Should().NotBeNull("未知欄位忽略");
        ok!.Visible.Should().BeTrue();
        ok.Focused.Should().BeFalse();
        ok.OpenConversationIds.Should().Equal("c1", "c2");
        ok.TzOffsetMinutes.Should().Be(-480);

        MessageGuard.ParseClientState(State(tz: 840)).Should().NotBeNull();
        MessageGuard.ParseClientState(State(tz: -840)).Should().NotBeNull();
        MessageGuard.ParseClientState(State(ids: MessageGuard.MaxOpenConversationIds)).Should().NotBeNull();

        MessageGuard.ParseClientState(State(tz: 841)).Should().BeNull();
        MessageGuard.ParseClientState(State(tz: -841)).Should().BeNull();
        MessageGuard.ParseClientState(State(ids: MessageGuard.MaxOpenConversationIds + 1)).Should().BeNull();
        MessageGuard.ParseClientState(JObject.Parse("""{"visible":"true","focused":false,"openConversationIds":[],"tzOffsetMinutes":0}""")).Should().BeNull();
        MessageGuard.ParseClientState(JObject.Parse("""{"visible":true,"focused":false,"openConversationIds":[5],"tzOffsetMinutes":0}""")).Should().BeNull();
        MessageGuard.ParseClientState(JObject.Parse("""{"visible":true,"focused":false,"openConversationIds":[],"tzOffsetMinutes":1.5}""")).Should().BeNull();
        MessageGuard.ParseClientState(JObject.Parse("""{"visible":true,"focused":false,"openConversationIds":[]}""")).Should().BeNull("四個欄位都要有");
        MessageGuard.ParseClientState(JObject.Parse("""{"visible":true,"focused":false,"openConversationIds":[],"tzOffsetMinutes":99999999999999999999}""")).Should().BeNull();
        MessageGuard.ParseClientState(new JArray()).Should().BeNull();
        MessageGuard.ParseClientState(null).Should().BeNull();
    }

    private static CmdParse Parse(string json) => MessageGuard.ParseCmd(JToken.Parse(json));

    private static string Setting(string body)
    {
        var cmd = JObject.Parse(body);
        cmd.AddFirst(new JProperty("cmd", "setting"));
        return new JObject { ["cmd"] = cmd }.ToString();
    }

    private static CmdParse Update(JObject fields)
    {
        var cmd = new JObject { ["cmd"] = "accountUpdate", ["accountId"] = Account };
        cmd.Merge(fields, new JsonMergeSettings { MergeNullValueHandling = MergeNullValueHandling.Merge });
        return MessageGuard.ParseCmd(new JObject { ["cmd"] = cmd });
    }

    private static CmdParse Reorder(JToken? ids)
    {
        var cmd = new JObject { ["cmd"] = "accountReorder" };
        if (ids is not null)
            cmd["ids"] = ids;
        return MessageGuard.ParseCmd(new JObject { ["cmd"] = cmd });
    }

    private static CmdParse Rename(JToken deviceId, JToken? name)
    {
        var cmd = new JObject { ["cmd"] = "deviceRename", ["deviceId"] = deviceId };
        if (name is not null)
            cmd["name"] = name;
        return MessageGuard.ParseCmd(new JObject { ["cmd"] = cmd });
    }

    /// <param name="idLength">大於 0 時每個對話 id 都是這麼長的字串</param>
    private static JObject State(int tz = 0, int ids = 0, int idLength = 0) => new()
    {
        ["visible"] = true,
        ["focused"] = true,
        ["openConversationIds"] = new JArray(Enumerable.Range(0, ids).Select(i => (object)(idLength > 0 ? new string('c', idLength) : "c" + i))),
        ["tzOffsetMinutes"] = tz,
    };

    private static JObject Request(JObject cmd) => new() { ["cmd"] = cmd };

    /// <summary>第二段量的本文：cmd 物件不縮排的 JSON 的 UTF-8 位元組</summary>
    private static int BodyBytes(JObject cmd) => Encoding.UTF8.GetByteCount(cmd.ToString(Formatting.None));

    /// <summary>本文剛好 <paramref name="bytes"/> 位元組的 answer：24 題各一個 4000 字的答案，再以最後一題的答案補足（ASCII，每字 1 位元組）</summary>
    private static JObject AnswerOfBytes(int bytes)
    {
        var answers = new JObject();
        for (var i = 0; i < 24; i++)
            answers["q" + i] = new JArray(new string('a', 4000));
        answers["pad"] = new JArray("");
        var cmd = new JObject { ["cmd"] = "answer", ["conversationId"] = "c1", ["itemId"] = "i1", ["answers"] = answers };
        var left = bytes - BodyBytes(cmd);
        if (left is < 0 or > 4096)
            throw new InvalidOperationException("夾具算錯：補足的長度 " + left);
        answers["pad"] = new JArray(new string('b', left));
        return cmd;
    }

    /// <summary>總共 <paramref name="containers"/> 層容器的 layout 指令（根物件與 cmd 物件各算一層）</summary>
    private static string Nested(int containers)
    {
        JToken inner = new JObject { ["leaf"] = 1 };
        for (var i = 0; i < containers - 3; i++)
            inner = new JObject { ["n"] = inner };
        return new JObject { ["cmd"] = new JObject { ["cmd"] = "layout", ["layout"] = inner } }.ToString();
    }

    private static JToken NestedArrays(int containers)
    {
        JToken token = new JArray(1);
        for (var i = 1; i < containers; i++)
            token = new JArray(token);
        return token;
    }
}
