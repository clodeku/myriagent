using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Core;

/// <summary>JSON 檔讀寫：區分不存在、成功、損毀（原檔改名備份）、讀不到（原檔不動），寫檔先寫暫存檔再取代</summary>
public sealed class JsonFileTests : IDisposable
{
    private sealed class Sample
    {
        public string Name { get; set; } = string.Empty;

        public List<int> Values { get; set; } = new();
    }

    private readonly TempDirectory temp = new("jsonfile");

    public void Dispose() => temp.Dispose();

    [Fact]
    public void Write_直接序列化進檔案_位元組與先轉字串的寫法相同()
    {
        // 對話項目（中文、跳脫字元、時間、列舉、null 略過、巢狀清單）與設定這兩種實際會寫的形狀
        var items = new List<AgentChatItem>
        {
            new() { Kind = AgentItemKind.User, Text = "請修正「登入」" + (char)10 + "第二行 \"引號\" " + (char)92 + " 反斜線 " + (char)9 + " tab 😀" + (char)0x1F, UpdatedAt = new DateTime(2026, 10, 23, 3, 4, 5, DateTimeKind.Local) },
            new() { Kind = AgentItemKind.Tool, Status = AgentItemStatus.Running, ToolName = "Bash", Detail = "dotnet test", Output = new string('字', 5000) },
            new() { Kind = AgentItemKind.Assistant, Status = AgentItemStatus.Done, Text = "完成。" },
        };
        var path = temp.Combine("items.json");
        JsonFile.Write(path, items);
        File.ReadAllBytes(path).Should().Equal(new System.Text.UTF8Encoding(false).GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(items, JsonFile.Settings)));

        var settings = new AppSettings { Theme = "light", RecentFolders = ["C:" + (char)92 + "repo" + (char)92 + "專案"], FontSize = 15 };
        var settingsPath = temp.Combine("settings.json");
        JsonFile.Write(settingsPath, settings);
        File.ReadAllBytes(settingsPath).Should().Equal(new System.Text.UTF8Encoding(false).GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(settings, JsonFile.Settings)));
        JsonFile.TryRead<List<AgentChatItem>>(path).Value!.Select(i => i.Text).Should().Equal(items.Select(i => i.Text));
    }

    [Fact]
    public void TryRead_不存在()
    {
        var (status, value, backup) = JsonFile.TryRead<Sample>(temp.Combine("沒有.json"));
        status.Should().Be(JsonReadStatus.Missing);
        value.Should().BeNull();
        backup.Should().BeNull();
    }

    [Fact]
    public void Write後TryRead_內容一致_不留暫存檔_自動建立資料夾()
    {
        var path = temp.Combine("子資料夾", "sample.json");

        JsonFile.Write(path, new Sample { Name = "測試", Values = { 1, 2, 3 } });

        var (status, value, _) = JsonFile.TryRead<Sample>(path);
        status.Should().Be(JsonReadStatus.Ok);
        value!.Name.Should().Be("測試");
        value.Values.Should().Equal(1, 2, 3);
        File.Exists(path + ".tmp").Should().BeFalse();
        File.ReadAllBytes(path).Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, "UTF-8 不帶 BOM");
    }

    [Fact]
    public void Write_目標檔正被別人以共用模式開著_短暫重試後照樣取代()
    {
        var path = temp.Combine("busy.json");
        JsonFile.Write(path, new Sample { Name = "舊的" });

        // 同步的重整在執行緒集區唯讀讀它（允許共用讀寫與刪除）：開著的期間 Windows 拒絕以暫存檔取代。
        // 另一條專用的執行緒 30 ms 後關掉（不用執行緒集區：測試平行跑時排不到，會超過重試的約 0.2 秒）
        var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var release = new Thread(() =>
        {
            Thread.Sleep(30);
            reader.Dispose();
        }) { IsBackground = true };
        release.Start();
        try
        {
            JsonFile.Write(path, new Sample { Name = "新的" });
        }
        finally
        {
            release.Join();
        }

        JsonFile.TryRead<Sample>(path).Value!.Name.Should().Be("新的", "合併前審查 T3");
        File.Exists(path + ".tmp").Should().BeFalse();
    }

    [Fact]
    public void Write_目標檔一直被占用_重試完照樣丟出_原檔不變()
    {
        var path = temp.Combine("locked.json");
        JsonFile.Write(path, new Sample { Name = "舊的" });

        Exception? thrown = null;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            try
            {
                JsonFile.Write(path, new Sample { Name = "新的" });
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
        }

        (thrown is IOException or UnauthorizedAccessException).Should().BeTrue($"重試完仍被占用要丟出（實際：{thrown?.GetType().Name ?? "沒有丟出"}）");

        JsonFile.TryRead<Sample>(path).Value!.Name.Should().Be("舊的");
    }

    [Fact]
    public void Write_目標檔是唯讀_立刻丟出不重試_原檔不變()
    {
        var path = temp.Combine("readonly.json");
        JsonFile.Write(path, new Sample { Name = "舊的" });
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        try
        {
            // 唯讀的目標檔一定被拒絕（不是暫時被占用）：不在 UI 執行緒先睡完約 0.2 秒的重試才丟（第二輪合併前審查 T3）
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var act = () => JsonFile.Write(path, new Sample { Name = "新的" });
            act.Should().Throw<UnauthorizedAccessException>();
            watch.Stop();
            watch.ElapsedMilliseconds.Should().BeLessThan(150, "重試的等待合計 200 ms");
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        JsonFile.TryRead<Sample>(path).Value!.Name.Should().Be("舊的");
    }

    [Fact]
    public void TryRead_內容損毀時把原檔改名備份_之後寫入不會蓋掉備份()
    {
        var path = temp.WriteFile("broken.json", "{不是 JSON");

        var (status, value, backup) = JsonFile.TryRead<Sample>(path);

        status.Should().Be(JsonReadStatus.Corrupt);
        value.Should().BeNull();
        backup.Should().StartWith(path + ".corrupt-");
        File.Exists(path).Should().BeFalse("原檔已改名");
        File.ReadAllText(backup!).Should().Be("{不是 JSON", "備份保留原本的內容");

        JsonFile.Write(path, new Sample { Name = "新的" });
        File.ReadAllText(backup!).Should().Be("{不是 JSON");
    }

    [Fact]
    public void TryRead_內容是Null也當作損毀()
    {
        var path = temp.WriteFile("null.json", "null");
        JsonFile.TryRead<Sample>(path).Status.Should().Be(JsonReadStatus.Corrupt);
    }

    [Fact]
    public void TryRead_被其他程式獨占時是讀不到_原檔不動()
    {
        var path = temp.WriteFile("locked.json", """{"Name":"鎖住"}""");
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var (status, value, backup) = JsonFile.TryRead<Sample>(path);
            status.Should().Be(JsonReadStatus.Unreadable);
            value.Should().BeNull();
            backup.Should().BeNull();
        }

        File.Exists(path).Should().BeTrue("讀不到時絕不能改名或覆寫原檔");
        JsonFile.Read<Sample>(path)!.Name.Should().Be("鎖住");
    }

    [Fact]
    public void Read_不存在或損毀回Null()
    {
        JsonFile.Read<Sample>(temp.Combine("沒有.json")).Should().BeNull();
        JsonFile.Read<Sample>(temp.WriteFile("bad.json", "[1,")).Should().BeNull();
    }

    [Fact]
    public void ConversationStore_TryLoadItems_回報讀檔狀態讓呼叫端不把空清單存回去()
    {
        var store = new ConversationStore(temp.Root);
        store.TryLoadItems("c1").Status.Should().Be(JsonReadStatus.Missing);

        store.SaveItems("c1", new[] { new Myriagent.Agents.AgentChatItem { Text = "你好" } });
        var ok = store.TryLoadItems("c1");
        ok.Status.Should().Be(JsonReadStatus.Ok);
        ok.Items.Single().Text.Should().Be("你好");

        File.WriteAllText(Path.Combine(store.GetConversationDirectory("c1"), "items.json"), "[{壞掉");
        var corrupt = store.TryLoadItems("c1");
        corrupt.Status.Should().Be(JsonReadStatus.Corrupt);
        corrupt.Items.Should().BeEmpty();
        File.Exists(corrupt.BackupPath).Should().BeTrue();
    }
}
