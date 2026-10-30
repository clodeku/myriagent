using System.Diagnostics;
using System.Reflection;
using System.Text;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json;
using Xunit.Abstractions;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 增量存檔（ItemsSaveCache、ItemsJsonFile、AgentChatItem.SameForSave）：沒變的項目沿用上次的快照、從上一份檔案複製位元組，
/// 輸出和整份序列化逐位元組相同；上一份檔案被改過就整份重寫；每個存檔欄位的改動都看得出來（反射擋下沒比到的）；中樞的兩條存檔路徑都用它、停下一陣子就丟掉
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class ItemsSaveCacheTests(ITestOutputHelper output) : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly TempDirectory temp = new("items-save");

    public void Dispose() => temp.Dispose();

    private static byte[] Full(IReadOnlyList<AgentChatItem?> items) => Utf8.GetBytes(JsonConvert.SerializeObject(items, JsonFile.Settings));

    /// <summary>各種形狀的項目：中文、跳脫字元、孤立的代理字元、時間的 Kind、列舉、略過的 null、巢狀清單、陣列裡的 null</summary>
    private static List<AgentChatItem?> Sample()
    {
        var odd = "跳脫 \"引號\" " + (char)92 + " " + (char)9 + (char)10 + (char)13 + (char)0x1F + " 😀 孤立" + (char)0xD800 + "尾";
        return
        [
            new AgentChatItem { Id = "u1", Kind = AgentItemKind.User, Text = odd, CreatedAt = new DateTime(2026, 10, 25, 1, 2, 3, DateTimeKind.Local), UpdatedAt = new DateTime(2026, 10, 25, 1, 2, 3, DateTimeKind.Utc) },
            new AgentChatItem { Id = "t1", Kind = AgentItemKind.Tool, Status = AgentItemStatus.Running, ToolName = "Bash", Detail = "dotnet test", Output = new string('字', 3000), Diff = [new AgentDiffLine("+", "新"), new AgentDiffLine("-", "舊")], MediaPaths = [@"C:\x\a.png"] },
            null,
            new AgentChatItem
            {
                Id = "q1", Kind = AgentItemKind.Question, Status = AgentItemStatus.Waiting,
                Questions = [new AgentQuestion { Id = "a", Text = "哪個？", Options = [new AgentQuestionOption { Label = "甲" }] }],
                Answers = new() { ["a"] = ["甲"] }, Attachments = [new AgentAttachment { FileName = "a.png", ModelImagePaths = [@"C:\x\f.png"] }],
                PlanSteps = [new AgentPlanStep { Text = "一", Status = "completed" }], MediaRefs = [new AgentMediaRef { Ref = "r" }],
                KeptMedia = new() { [@"c:\x\a.png"] = @"attachments\.kept\1\a.png" }, NoticeKind = AgentNoticeKind.Clear, Risk = AgentRemoteRisk.High, Ord = 4,
            },
            new AgentChatItem { Id = "a1", Kind = AgentItemKind.Assistant, Text = "完成。" + (char)10 + "第二行" },
        ];
    }

    private string Write(IReadOnlyList<AgentChatItem?> items, ItemsJsonLayout layout, string name = "items.json")
    {
        var path = temp.Combine(name);
        ItemsJsonFile.Write(path, items, layout);
        File.ReadAllBytes(path).Should().Equal(Full(items), "和整份序列化逐位元組相同");
        File.Exists(path + ".tmp").Should().BeFalse();
        return path;
    }

    [Fact]
    public void 寫檔_第一次和整份序列化逐位元組相同_空的與只有一項的也是()
    {
        Write(Sample(), new ItemsJsonLayout());
        Write([], new ItemsJsonLayout(), "empty.json");
        File.ReadAllText(temp.Combine("empty.json")).Should().Be("[]");
        Write([new AgentChatItem { Id = "x" }], new ItemsJsonLayout(), "one.json");
        Write([null], new ItemsJsonLayout(), "null.json");
    }

    [Fact]
    public void 寫檔_第二次沿用沒換的快照_改了加了刪了換了順序的照樣逐位元組相同()
    {
        var layout = new ItemsJsonLayout();
        var cache = new ItemsSaveCache();
        var live = Sample().Select(i => i!).ToList();
        Write(cache.Take(live), layout);
        layout.LastWrite.Should().Be((0, 5, false));

        // 沒變：沿用（null 與有巢狀內容的問卷照樣序列化）
        Write(cache.Take(live), layout);
        layout.LastWrite.Should().Be((3, 2, false));

        // 改一項、加兩項、刪一項、把一項搬到最後
        live[1]!.Output += "多一行";
        live[1]!.Status = AgentItemStatus.Done;
        live.Add(new AgentChatItem { Id = "n1", Kind = AgentItemKind.Tool, Output = "新的" });
        live.Add(new AgentChatItem { Id = "n2", Kind = AgentItemKind.Assistant, Text = "又一則" });
        live.RemoveAt(0);
        var moved = live[^3];
        live.Remove(moved);
        live.Add(moved);
        var snapshot = cache.Take(live);
        Write(snapshot, layout);
        layout.LastWrite.Reused.Should().Be(1, "只有沒改的那一項（搬到最後了）沿用：改了的、新加的、有巢狀內容的問卷都序列化");
        layout.LastWrite.Fallback.Should().BeFalse();

        // 再寫一次：這次除了 null 與有巢狀內容的問卷，全部沿用
        Write(cache.Take(live), layout);
        layout.LastWrite.Reused.Should().Be(4);
    }

    [Fact]
    public void 寫檔_上一份被改過_大小不同或內容不同都整份重寫()
    {
        var layout = new ItemsJsonLayout();
        var cache = new ItemsSaveCache();
        var live = Sample().Select(i => i!).ToList();
        var path = Write(cache.Take(live), layout);

        // 別的程式改了內容、大小與修改時間都一樣：複製時雜湊對不上
        var bytes = File.ReadAllBytes(path);
        var stamp = File.GetLastWriteTimeUtc(path);
        var tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("完成。", "改掉。"));
        tampered.Length.Should().Be(bytes.Length);
        File.WriteAllBytes(path, tampered);
        File.SetLastWriteTimeUtc(path, stamp);
        Write(cache.Take(live), layout);
        layout.LastWrite.Fallback.Should().BeTrue();

        // 大小不同：不沿用（不必讀到一半才發現）
        File.AppendAllText(path, " ");
        Write(cache.Take(live), layout);
        layout.LastWrite.Should().Be((0, 5, false));

        // 檔案不見了：整份寫
        File.Delete(path);
        Write(cache.Take(live), layout);
        layout.LastWrite.Reused.Should().Be(0);
    }

    [Fact]
    public void 寫檔_資料夾不在時丟例外不建立_記下的樣子清掉()
    {
        var layout = new ItemsJsonLayout();
        var cache = new ItemsSaveCache();
        var live = Sample().Select(i => i!).ToList();
        var dir = temp.CreateDirectory("c1");
        ItemsJsonFile.Write(Path.Combine(dir, "items.json"), cache.Take(live), layout, createDirectory: false);
        layout.Segments.Should().NotBeNull();
        Directory.Delete(dir, recursive: true);

        var act = () => ItemsJsonFile.Write(Path.Combine(dir, "items.json"), cache.Take(live), layout, createDirectory: false);
        act.Should().Throw<DirectoryNotFoundException>();
        Directory.Exists(dir).Should().BeFalse();
        layout.Segments.Should().BeNull();
    }

    [Fact]
    public void 快照_沒變的沿用同一份_變了的另外複製_插入刪除後以id找_同一個id出現兩次照樣寫對()
    {
        var cache = new ItemsSaveCache();
        var a = new AgentChatItem { Id = "a", Text = "一" };
        var b = new AgentChatItem { Id = "b", Text = "二" };
        var c = new AgentChatItem { Id = "c", Text = "三" };
        var first = cache.Take([a, b]);
        first[0].Should().NotBeSameAs(a);
        var second = cache.Take([a, b]);
        second[0].Should().BeSameAs(first[0]);
        second[1].Should().BeSameAs(first[1]);

        b.Text = "改";
        var third = cache.Take([a, b]);
        third[0].Should().BeSameAs(first[0]);
        third[1].Should().NotBeSameAs(first[1]);
        third[1].Text.Should().Be("改");
        first[1].Text.Should().Be("二", "舊的快照不跟著變");

        // 前面插入一項：位置對不上，以 id 找到上次的快照
        var fourth = cache.Take([c, a, b]);
        fourth[1].Should().BeSameAs(first[0]);
        fourth[2].Should().BeSameAs(third[1]);

        // 同一個 id 出現兩次（不該發生）：可以沿用同一份，寫出來照樣和整份序列化相同
        var layout = new ItemsJsonLayout();
        Write(cache.Take([c, a, b, a]), layout);
        Write(cache.Take([c, a, b, a]), layout);
        layout.LastWrite.Reused.Should().Be(4);
    }

    /// <summary>每個欄位都有值、巢狀物件的清單是空的（SameForSave 才可能是 true）</summary>
    private static AgentChatItem Comparable() => new()
    {
        Id = "c", Kind = AgentItemKind.Tool, Status = AgentItemStatus.Done, Text = "t", ToolName = "Bash", ToolKind = "execute", Title = "標題", Detail = "細節", Output = "輸出",
        Diff = [new AgentDiffLine("+", "a")], FilePath = @"C:\a.cs", Decision = "allow", CanAllowForSession = true, AllowSessionLabel = "允許", AnswerAsMessage = false,
        MediaPaths = [@"C:\x.png"], CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local), UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Local),
        ProcessInstance = "p", NoticeKind = AgentNoticeKind.Clear, Source = AgentItemSource.Remote, CliUuid = "u", Ord = 3, RequestDigest = "rd", Risk = AgentRemoteRisk.Normal,
        MediaRefs = [], KeptMedia = new() { ["k"] = "v" },
    };

    [Fact]
    public void 比對_每個存檔的欄位改了都看得出來_新加的欄位要加進SameForSave()
    {
        var problems = new List<string>();
        var props = typeof(AgentChatItem).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanWrite && p.GetCustomAttribute<JsonIgnoreAttribute>() is null).ToList();
        props.Should().HaveCountGreaterThan(25);
        var baseline = Comparable();
        baseline.SameForSave(baseline.CloneForSave()).Should().BeTrue();
        foreach (var p in props)
        {
            var mutations = Mutations(p.PropertyType, p.GetValue(Comparable()));
            if (mutations.Count == 0)
            {
                problems.Add($"{p.Name}（{p.PropertyType.Name}）：測試不會改這種型別，在 Mutations 補上");
                continue;
            }
            foreach (var (label, mutate) in mutations)
            {
                var item = Comparable();
                var snapshot = item.CloneForSave();
                mutate(item, p);
                if (JsonConvert.SerializeObject(item, JsonFile.Settings) == JsonConvert.SerializeObject(snapshot, JsonFile.Settings))
                    problems.Add($"{p.Name} {label}：改了之後存檔內容沒變（測試的改法不對）");
                else if (item.SameForSave(snapshot))
                    problems.Add($"{p.Name} {label}：存檔內容變了，SameForSave 卻說相同");
            }
        }
        problems.Should().BeEmpty("SameForSave 漏比的欄位，增量存檔會寫出舊的內容");
    }

    /// <summary>某個型別的欄位怎麼改（改的是項目本身，不是快照）</summary>
    private static List<(string, Action<AgentChatItem, PropertyInfo>)> Mutations(Type type, object? current)
    {
        var list = new List<(string, Action<AgentChatItem, PropertyInfo>)>();
        var inner = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string))
            list.Add(("加字", (i, p) => p.SetValue(i, (string?)p.GetValue(i) + "x")));
        else if (inner.IsEnum)
        {
            if (Enum.GetValues(inner).Length > 1)
                list.Add(("換值", (i, p) => p.SetValue(i, Enum.GetValues(inner).Cast<object>().First(v => !v.Equals(p.GetValue(i))))));
            if (type != inner)
                list.Add(("設成 null", (i, p) => p.SetValue(i, null)));
        }
        else if (inner == typeof(bool))
            list.Add(("反過來", (i, p) => p.SetValue(i, !(bool)p.GetValue(i)!)));
        else if (inner == typeof(long))
        {
            list.Add(("加一", (i, p) => p.SetValue(i, ((long?)p.GetValue(i) ?? 0) + 1)));
            if (type != inner)
                list.Add(("設成 null", (i, p) => p.SetValue(i, null)));
        }
        else if (inner == typeof(DateTime))
        {
            list.Add(("加一個 tick", (i, p) => p.SetValue(i, ((DateTime)p.GetValue(i)!).AddTicks(1))));
            list.Add(("換 Kind", (i, p) => p.SetValue(i, DateTime.SpecifyKind((DateTime)p.GetValue(i)!, DateTimeKind.Utc))));
        }
        else if (type == typeof(List<AgentDiffLine>))
        {
            list.Add(("加一行", (i, p) => ((List<AgentDiffLine>)p.GetValue(i)!).Add(new AgentDiffLine("-", "b"))));
            list.Add(("換一行", (i, p) => ((List<AgentDiffLine>)p.GetValue(i)!)[0] = new AgentDiffLine("+", "改")));
            list.Add(("設成 null", (i, p) => p.SetValue(i, null)));
        }
        else if (type == typeof(List<string>))
        {
            list.Add(("加一筆", (i, p) => ((List<string>)p.GetValue(i)!).Add("y")));
            list.Add(("換一筆", (i, p) => ((List<string>)p.GetValue(i)!)[0] = "改"));
            list.Add(("設成 null", (i, p) => p.SetValue(i, null)));
        }
        else if (type == typeof(Dictionary<string, string>))
        {
            list.Add(("加一筆", (i, p) => ((Dictionary<string, string>)p.GetValue(i)!)["k2"] = "v2"));
            list.Add(("換值", (i, p) => ((Dictionary<string, string>)p.GetValue(i)!)["k"] = "改"));
            list.Add(("設成 null", (i, p) => p.SetValue(i, null)));
        }
        else if (type == typeof(Dictionary<string, List<string>>))
        {
            list.Add(("加一筆", (i, p) => ((Dictionary<string, List<string>>)p.GetValue(i)!)["q"] = ["a"]));
            list.Add(("設成 null", (i, p) => p.SetValue(i, null)));
        }
        else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) && current is System.Collections.IList)
        {
            var element = type.GetGenericArguments()[0];
            list.Add(("加一個", (i, p) => ((System.Collections.IList)p.GetValue(i)!).Add(Activator.CreateInstance(element))));
            list.Add(("設成 null", (i, p) => p.SetValue(i, null)));
        }
        return list;
    }

    [Fact]
    public async Task 中樞_維護與一輪結束都增量存_內容和整份序列化相同_停下一陣子丟掉快取()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "跑測試");
        for (var i = 0; i < 30; i++)
        {
            h.Raise(s, new AgentTextDeltaEvent("b" + i, "第 " + i + " 段"));
            h.Raise(s, new AgentTextCompletedEvent("b" + i, "第 " + i + " 段"));
        }
        h.Raise(s, new AgentTextDeltaEvent("last", "正在改"));
        var path = h.Store.GetItemsPath(id);

        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        var writer = h.OnUi(() => h.Hub.ItemsWriterForTests)!;
        writer.Drain(TimeSpan.FromSeconds(10)).Should().BeTrue();
        File.ReadAllText(path).Should().Be(JsonConvert.SerializeObject(h.Items(id), JsonFile.Settings));
        var layout = h.OnUi(() => h.Hub.SaveCacheForTests(id))!.Layout;
        lock (layout.Gate)
            layout.LastWrite.Serialized.Should().BeGreaterThanOrEqualTo(31, "新的項目都要序列化");

        // 再改最後一段：只序列化變了的
        h.Raise(s, new AgentTextDeltaEvent("last", "，改好了"));
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        writer.Drain(TimeSpan.FromSeconds(10)).Should().BeTrue();
        File.ReadAllText(path).Should().Be(JsonConvert.SerializeObject(h.Items(id), JsonFile.Settings));
        lock (layout.Gate)
            layout.LastWrite.Reused.Should().BeGreaterThan(20);

        // 一輪結束的同步存檔也沿用
        h.CompleteTurn(s);
        await Task.Delay(100);
        writer.Drain(TimeSpan.FromSeconds(10)).Should().BeTrue();
        File.ReadAllText(path).Should().Be(JsonConvert.SerializeObject(h.Items(id), JsonFile.Settings));
        h.OnUi(() => h.Hub.SaveCacheForTests(id)).Should().NotBeNull();
        lock (layout.Gate)
            layout.LastWrite.Reused.Should().BeGreaterThan(20);

        // 停下 10 分鐘沒存：維護丟掉快取
        h.Ui.Invoke(() => h.Hub.RunMaintenance(DateTime.Now + TimeSpan.FromMinutes(5)));
        h.OnUi(() => h.Hub.SaveCacheForTests(id)).Should().NotBeNull();
        h.Ui.Invoke(() => h.Hub.RunMaintenance(DateTime.Now + ItemsSaveCache.IdleLimit + TimeSpan.FromSeconds(1)));
        h.OnUi(() => h.Hub.SaveCacheForTests(id)).Should().BeNull();
    }

    /// <summary>
    /// 實際的長對話量時間（不是正規測試）：<c>MYRIAGENT_ITEMS_BENCH=items.json 的完整路徑</c>。複製到暫存資料夾，量整份序列化（改前）與增量存檔
    /// （第一次整份、之後改最後一項並加兩項）各要多久，並確認輸出和整份序列化逐位元組相同。2026-10-25 實測結果寫在提交說明
    /// </summary>
    [EnvFileFact("MYRIAGENT_ITEMS_BENCH", "量實際長對話的存檔時間")]
    public void 量測_實際長對話的存檔時間()
    {
        var source = Environment.GetEnvironmentVariable("MYRIAGENT_ITEMS_BENCH")!;
        var live = JsonConvert.DeserializeObject<List<AgentChatItem>>(File.ReadAllText(source, Utf8), JsonFile.Settings)!;
        output.WriteLine($"{source}：{new FileInfo(source).Length / 1048576.0:0.0} MB、{live.Count} 項");
        var fullPath = temp.Combine("full.json");
        var incPath = temp.Combine("inc.json");
        var cache = new ItemsSaveCache();
        var sw = new Stopwatch();
        for (var round = 0; round < 6; round++)
        {
            if (round > 0)
            {
                // 每一回合：最後一項多一段輸出、加兩項（像執行中的對話 30 秒內的變化）
                live[^1].Output += "多一行輸出";
                live[^1].UpdatedAt = DateTime.Now;
                live.Add(new AgentChatItem { Kind = AgentItemKind.Tool, ToolName = "Bash", Detail = "git status", Output = new string('x', 2000) });
                live.Add(new AgentChatItem { Kind = AgentItemKind.Assistant, Text = "下一步" });
            }
            sw.Restart();
            var clones = live.Select(i => i.CloneForSave()).ToList();
            var tClone = sw.Elapsed.TotalMilliseconds;
            var cpu = Cpu();
            sw.Restart();
            JsonFile.Write(fullPath, clones);
            var tFull = sw.Elapsed.TotalMilliseconds;
            var cFull = Cpu() - cpu;

            sw.Restart();
            var snapshot = cache.Take(live);
            var tTake = sw.Elapsed.TotalMilliseconds;
            cpu = Cpu();
            sw.Restart();
            ItemsJsonFile.Write(incPath, snapshot, cache.Layout);
            var tInc = sw.Elapsed.TotalMilliseconds;
            var cInc = Cpu() - cpu;
            File.ReadAllBytes(incPath).AsSpan().SequenceEqual(File.ReadAllBytes(fullPath)).Should().BeTrue("增量存檔和整份序列化逐位元組相同");
            output.WriteLine($"第 {round + 1} 次：改前 快照 {tClone:0.0} ms＋整份寫 {tFull:0} ms（CPU {cFull:0} ms）；改後 快照 {tTake:0.0} ms＋增量寫 {tInc:0} ms（CPU {cInc:0} ms；沿用 {cache.Layout.LastWrite.Reused} 項、序列化 {cache.Layout.LastWrite.Serialized} 項）");
        }

        // 這條執行緒用掉的 CPU（user＋kernel）：寫檔的實際時間含等磁碟落盤，那段不耗 CPU
        static double Cpu()
        {
            GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user);
            return (kernel + user) / 10_000.0;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
}
