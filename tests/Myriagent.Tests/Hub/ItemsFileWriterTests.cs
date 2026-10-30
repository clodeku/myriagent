using System.Diagnostics;
using System.Reflection;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 定期維護的對話紀錄改在背景寫（ItemsFileWriter、AgentChatItem.CloneForSave）：快照和原本的序列化相同、之後改原本的不影響快照、
/// 可變欄位都有處理（反射擋下新加的）；背景執行緒寫、同一個對話只寫最新的、Forget 讓較舊的不會晚到、不重建資料夾；中樞的維護照樣存得到
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class ItemsFileWriterTests
{
    /// <summary>每個欄位都有值（清單都有元素）的項目</summary>
    private static AgentChatItem Full() => new()
    {
        Id = "full",
        Kind = AgentItemKind.Question,
        Status = AgentItemStatus.Waiting,
        Text = "要用哪一種？",
        Title = "標題",
        Detail = "細節",
        Output = "輸出",
        ToolName = "Bash",
        ToolKind = "execute",
        Decision = "allow",
        FilePath = @"C:\repo\a.cs",
        Diff = [new AgentDiffLine("+", "新的一行"), new AgentDiffLine("-", "舊的")],
        Attachments = [new AgentAttachment { FileName = "a.png", StoredPath = @"C:\x\a.png", Kind = AgentAttachmentKind.Image, SizeBytes = 12, ModelImagePaths = [@"C:\x\f1.png"], FrameSeconds = [0.5], DurationSeconds = 3, Waveform = "abc" }],
        Questions = [new AgentQuestion { Id = "q1", Header = "選項", Text = "哪個？", Options = [new AgentQuestionOption { Label = "甲", Description = "說明", Preview = "預覽" }], MultiSelect = true }],
        Answers = new Dictionary<string, List<string>> { ["q1"] = ["甲"], ["q2"] = ["乙", "丙"] },
        MediaPaths = [@"C:\x\out.png"],
        PlanSteps = [new AgentPlanStep { Text = "第一步", Status = "in_progress" }],
        MediaRefs = [new AgentMediaRef { Ref = "r1", AttachmentId = "a1", MediaPath = @"C:\x\m.png" }],
        KeptMedia = new Dictionary<string, string> { [@"C:\x\out.png"] = @"attachments\.kept\0123456789abcdef\out.png" },
        Risk = AgentRemoteRisk.High,
        Seq = 7,
        Ord = 9,
    };

    private static string Json(object value) => JsonConvert.SerializeObject(value, JsonFile.Settings);

    [Fact]
    public void 快照_序列化和原本相同_之後改原本的清單與巢狀物件不影響快照()
    {
        var item = Full();
        var before = Json(item);
        var copy = item.CloneForSave();
        Json(copy).Should().Be(before);

        // UI 執行緒繼續改原本的：每一種可變欄位都動
        item.Text = "改了";
        item.Diff!.Add(new AgentDiffLine("+", "再一行"));
        item.Attachments[0].ModelImagePaths.Add(@"C:\x\f2.png");
        item.Attachments[0].FrameSeconds.Add(1.5);
        item.Attachments[0].FileName = "b.png";
        item.Attachments.Add(new AgentAttachment());
        item.Questions[0].Options[0].Label = "改";
        item.Questions[0].Options.Add(new AgentQuestionOption { Label = "多一個" });
        item.Questions[0].Text = "改";
        item.Answers["q1"].Add("丁");
        item.Answers["q3"] = ["新"];
        item.MediaPaths.Add(@"C:\x\more.png");
        item.PlanSteps[0].Status = "completed";
        item.MediaRefs![0].Ref = "r2";
        item.MediaRefs.Add(new AgentMediaRef());
        item.KeptMedia![@"C:\x\more.png"] = "x";

        Json(copy).Should().Be(before, "快照不跟著變");
    }

    [Fact]
    public void 快照_容忍讀檔讀進來的null()
    {
        var item = new AgentChatItem { Id = "n", Attachments = { null! }, Questions = null!, Answers = new() { ["q"] = null! }, MediaPaths = null!, PlanSteps = null!, MediaRefs = null };
        var copy = item.CloneForSave();
        Json(copy).Should().Be(Json(item));
    }

    /// <summary>不可變（快照可以共用）的欄位型別</summary>
    private static bool Immutable(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(Guid) || t == typeof(TimeSpan);
    }

    [Fact]
    public void 快照_可變欄位都有處理_新加的清單或物件欄位要加進CloneForSave()
    {
        // CloneForSave 另外複製的欄位（新增可變欄位時：在 CloneForSave 複製它，再加進這張表）
        var handled = new Dictionary<Type, string[]>
        {
            [typeof(AgentChatItem)] = ["Diff", "Attachments", "Questions", "Answers", "MediaPaths", "PlanSteps", "MediaRefs", "KeptMedia"],
            [typeof(AgentAttachment)] = ["ModelImagePaths", "FrameSeconds"],
            [typeof(AgentQuestion)] = ["Options"],
            [typeof(AgentQuestionOption)] = [],
            [typeof(AgentPlanStep)] = [],
            [typeof(AgentMediaRef)] = [],
        };
        var problems = new List<string>();
        foreach (var (type, names) in handled)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var name = field.Name.StartsWith('<') ? field.Name[1..field.Name.IndexOf('>')] : field.Name;
                if (!Immutable(field.FieldType) && !names.Contains(name))
                    problems.Add($"{type.Name}.{name}（{field.FieldType.Name}）");
            }
        }
        // AgentDiffLine 是不可變的 record（快照只複製清單、元素共用）
        foreach (var field in typeof(AgentDiffLine).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!field.IsInitOnly || !Immutable(field.FieldType))
                problems.Add($"AgentDiffLine.{field.Name} 不是不可變的");
        }
        problems.Should().BeEmpty("可變欄位沒有在 CloneForSave 另外複製：UI 執行緒之後改原本的，背景執行緒序列化快照時會一起變（甚至丟例外）");
    }

    [Fact]
    public void 背景寫的檔和同步寫的逐位元組相同_讀得回來()
    {
        using var temp = new TempDirectory("items-writer");
        var items = new List<AgentChatItem> { Full(), new() { Id = "a2", Kind = AgentItemKind.Assistant, Text = "回覆\n第二行" } };
        var syncPath = Path.Combine(temp.Root, "sync", "items.json");
        JsonFile.Write(syncPath, items);
        var dir = Path.Combine(temp.Root, "bg");
        Directory.CreateDirectory(dir);
        var bgPath = Path.Combine(dir, "items.json");

        using var writer = new ItemsFileWriter((_, ex) => throw ex);
        writer.Enqueue("c1", bgPath, items.Select(i => i.CloneForSave()).ToList());
        writer.Drain(TimeSpan.FromSeconds(10)).Should().BeTrue();

        File.ReadAllBytes(bgPath).Should().Equal(File.ReadAllBytes(syncPath));
        writer.WorkerThreadId.Should().NotBe(Environment.CurrentManagedThreadId);
    }

    [Fact]
    public void 資料夾不在了不寫也不重建_也不算失敗()
    {
        using var temp = new TempDirectory("items-writer");
        var gone = Path.Combine(temp.Root, "deleted-conversation");
        var failures = new List<Exception>();
        using var writer = new ItemsFileWriter((_, ex) => { lock (failures) failures.Add(ex); });
        writer.Enqueue("c1", Path.Combine(gone, "items.json"), [new AgentChatItem()]);
        writer.Drain(TimeSpan.FromSeconds(10)).Should().BeTrue();

        Directory.Exists(gone).Should().BeFalse("刪掉的對話不能被重建（下次啟動會被當成 meta 損毀而還原）");
        lock (failures) failures.Should().BeEmpty();
    }

    [Fact]
    public async Task 同一個對話只寫最新的_Forget讓排著的作廢並等正在寫的寫完_失敗回報()
    {
        var gate = new ManualResetEventSlim(false);
        var started = new ManualResetEventSlim(false);
        var written = new List<string>();
        var failures = new List<string>();
        using var writer = new ItemsFileWriter((id, _) => { lock (failures) failures.Add(id); }, (path, items) =>
        {
            started.Set();
            gate.Wait(TimeSpan.FromSeconds(10));
            if (items[0].Text == "壞")
                throw new IOException("磁碟滿了");
            lock (written) written.Add(path + ":" + items[0].Text);
        });

        writer.Enqueue("c1", "p1", [new AgentChatItem { Text = "第一份" }]);
        started.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        // 第一份寫到一半：同一個對話又排了兩份，只留最後那份
        writer.Enqueue("c1", "p1", [new AgentChatItem { Text = "第二份" }]);
        writer.Enqueue("c1", "p1", [new AgentChatItem { Text = "第三份" }]);
        writer.IsPending("c1").Should().BeTrue();

        // Forget：排著的第三份作廢，正在寫的第一份等它寫完才回來
        var forget = Task.Run(() => writer.Forget("c1"));
        await Task.Delay(100);
        forget.IsCompleted.Should().BeFalse("正在寫的還沒寫完");
        gate.Set();
        (await forget.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        writer.IsPending("c1").Should().BeFalse();
        writer.Drain(TimeSpan.FromSeconds(10)).Should().BeTrue();
        lock (written) written.Should().Equal("p1:第一份");

        writer.Enqueue("c2", "p2", [new AgentChatItem { Text = "壞" }]);
        writer.Drain(TimeSpan.FromSeconds(10)).Should().BeTrue();
        lock (failures) failures.Should().Equal("c2");
    }

    [Fact]
    public async Task 中樞_維護在背景寫執行中的對話_內容和同步存的相同_之後同步存的不被較舊的蓋掉()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "跑測試");
        h.Raise(s, new AgentTextDeltaEvent("b1", "正在改"));
        var path = h.Store.GetItemsPath(id);

        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        var writer = h.OnUi(() => h.Hub.ItemsWriterForTests);
        writer.Should().NotBeNull("執行中的對話在背景寫");
        writer!.Drain(TimeSpan.FromSeconds(10)).Should().BeTrue();
        writer.WorkerThreadId.Should().NotBe(h.OnUi(() => Environment.CurrentManagedThreadId), "不在 UI 執行緒寫");
        var items = h.Items(id);
        File.ReadAllText(path).Should().Be(JsonConvert.SerializeObject(items, JsonFile.Settings), "和整份同步序列化相同");

        // 維護排了一份之後，一輪結束照常同步存：最後在磁碟上的是比較新的那份
        h.Raise(s, new AgentTextDeltaEvent("b1", "，改好了"));
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        h.CompleteTurn(s);
        await Task.Delay(200);
        writer.Drain(TimeSpan.FromSeconds(10)).Should().BeTrue();
        var saved = JsonConvert.DeserializeObject<List<AgentChatItem>>(File.ReadAllText(path), JsonFile.Settings)!;
        saved.Single(i => i.Kind == AgentItemKind.Assistant).Text.Should().Be("正在改，改好了");
        saved.Single(i => i.Kind == AgentItemKind.Assistant).Status.Should().NotBe(AgentItemStatus.Running, "結束的狀態也存到了");
    }
}
