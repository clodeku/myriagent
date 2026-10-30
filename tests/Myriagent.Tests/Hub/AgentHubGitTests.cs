using System.Collections.Concurrent;
using Myriagent.Git;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 對話工作資料夾的 git 狀態（web-protocol 的 gitStatus／gitRefresh）：沒掛讀取器不送；開分頁先送快取的再讀；換資料夾、這一輪結束、
/// 使用者要求時重讀；定時只讀畫面上看得到的；讀失敗留著上次的、一陣子內只有使用者要求才讀；副本不讀。讀取器是假的（不跑 git）
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubGitTests
{
    private const string LocalSha = "1111111111111111111111111111111111111111";
    private const string RemoteSha = "2222222222222222222222222222222222222222";

    private sealed class FakeGitReader : IGitStatusReader
    {
        public ConcurrentQueue<string> Reads { get; } = new();

        public ConcurrentQueue<GitRemoteTarget> RemoteChecks { get; } = new();

        public volatile bool Fail;

        public Func<string, GitRepoStatus?> Status { get; set; } = folder => Repo(folder);

        public Task<GitRepoStatus?> ReadAsync(string folder, CancellationToken ct)
        {
            Reads.Enqueue(folder);
            return Fail ? throw new GitReadException("假的失敗") : Task.FromResult(Status(folder));
        }

        public Task<GitRemoteSnapshot> CheckRemoteAsync(string folder, GitRemoteTarget target, CancellationToken ct)
        {
            RemoteChecks.Enqueue(target);
            return Task.FromResult(new GitRemoteSnapshot(target, RemoteSha, DateTime.UtcNow));
        }

        /// <summary>每次自動拉取的計畫</summary>
        public ConcurrentQueue<GitPullPlan> Pulls { get; } = new();

        /// <summary>自動拉取的結果（預設：快轉計畫裡的提交數）</summary>
        public Func<GitPullPlan, GitPullResult> Pull { get; set; } = plan => GitPullResult.Done(plan.Behind);

        /// <summary>設定後自動拉取等它完成（模擬 fetch、merge 的期間）</summary>
        public TaskCompletionSource? PullGate { get; set; }

        /// <summary>同時在拉的最大數目</summary>
        public int MaxConcurrentPulls => Volatile.Read(ref maxConcurrentPulls);

        private int concurrentPulls;
        private int maxConcurrentPulls;

        public async Task<GitPullResult> FastForwardAsync(string folder, GitPullPlan plan, CancellationToken ct)
        {
            Pulls.Enqueue(plan);
            var now = Interlocked.Increment(ref concurrentPulls);
            InterlockedMax(ref maxConcurrentPulls, now);
            try
            {
                if (PullGate is { } gate)
                    await gate.Task.WaitAsync(ct);
                return Pull(plan);
            }
            finally
            {
                Interlocked.Decrement(ref concurrentPulls);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }

    /// <param name="tracked">這台記著的遠端提交（預設 LocalSha：問遠端會得到 RemoteSha，算成遠端有新提交）</param>
    /// <param name="commonDir">git 的共用資料夾（同一個 repo 的子資料夾、worktree 一樣）；null＝不明（以最上層資料夾代替）</param>
    private static GitRepoStatus Repo(string root, int changed = 0, int ahead = 1, int behind = 0, string tracked = LocalSha, string? commonDir = null) => new()
    {
        Root = root,
        Porcelain = new GitPorcelainStatus(LocalSha, "main", "origin/main", ahead, behind, changed, 0, changed, 0, 0, 0),
        RemoteTarget = new GitRemoteTarget("origin", "refs/heads/main", tracked),
        CommonDir = commonDir,
        CheckedAt = DateTime.UtcNow,
    };

    private const string SharedCommonDir = @"C:\fake\repo\.git";

    [Fact]
    public async Task 同一個資料夾的多個對話_定時只讀一次()
    {
        var (h, reader) = Create();
        using var dispose = h;
        var now = DateTime.UtcNow;
        h.Hub.GitClock = () => now;
        var folder = h.Temp.CreateDirectory("shared-folder");
        var a = h.NewConversation(h.Main, h.Claude);
        var b = h.NewConversation(h.Main, h.Claude);
        await h.SetFolderAsync(h.Main, a, folder);
        await h.SetFolderAsync(h.Main, b, folder);
        await WaitGitAsync(h.Main, a, g => g?.Value<string>("root") == folder);
        await WaitGitAsync(h.Main, b, g => g?.Value<string>("root") == folder);
        await h.Ui.DrainAsync();
        h.Command(h.Main, "visible", new { conversationIds = new[] { a, b } });
        var reads = reader.Reads.Count(r => r == folder);

        now = now.AddSeconds(30);
        var mark = h.Main.Mark();
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        await WaitGitAsync(h.Main, b, g => g != null, mark);
        await h.Ui.DrainAsync();
        await Task.Delay(100);

        reader.Reads.Count(r => r == folder).Should().Be(reads + 1, "兩個看得到的對話是同一個資料夾：定時只讀一次，兩個都收到");
        h.Main.Messages("gitStatus", mark, a).Should().NotBeEmpty();
    }

    [Fact]
    public async Task 同一個repo的不同資料夾與worktree_遠端只問一次_間隔到了也只問一次()
    {
        var (h, reader) = Create();
        using var dispose = h;
        var now = DateTime.UtcNow;
        h.Hub.GitClock = () => now;
        reader.Status = folder => Repo(folder, commonDir: SharedCommonDir);
        var ids = new[] { h.NewConversation(h.Main, h.Claude), h.NewConversation(h.Main, h.Claude), h.NewConversation(h.Main, h.Claude) };
        foreach (var id in ids)
            await WaitGitAsync(h.Main, id, g => g?.Value<bool?>("remoteNew") == true);
        await h.Ui.DrainAsync();
        reader.Reads.Distinct().Should().HaveCount(3, "三個對話是三個不同的資料夾（各自的預設工作區）");
        reader.RemoteChecks.Should().HaveCount(1, "同一個 repository 的同一個遠端分支只問一次，結果三個資料夾共用");

        h.Command(h.Main, "visible", new { conversationIds = ids });
        now = now.AddMinutes(6);
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        await h.WaitUntilAsync(() => reader.RemoteChecks.Count == 2, "間隔到了再問");
        await h.Ui.DrainAsync();
        await Task.Delay(100);
        reader.RemoteChecks.Should().HaveCount(2, "三個資料夾都重讀了，遠端還是只問一次");

        // 不同的 repository 各問各的
        reader.Status = folder => Repo(folder, commonDir: folder + @"\.git");
        var other = h.NewConversation(h.Main, h.Claude);
        await h.WaitUntilAsync(() => reader.RemoteChecks.Count == 3, "另一個 repo 自己問");
        other.Should().NotBeNull();
    }

    [Fact]
    public async Task 自動拉取_同一個repo同時只拉一次_拉完重讀這個repo的其他資料夾再拉()
    {
        var (h, reader) = Create();
        using var dispose = h;
        reader.Status = folder => Repo(folder, ahead: 0, behind: 1, tracked: RemoteSha, commonDir: SharedCommonDir);
        reader.PullGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = h.NewConversation(h.Main, h.Claude);
        await h.WaitUntilAsync(() => reader.Pulls.Count == 1, "第一個資料夾開始拉");
        var b = h.NewConversation(h.Main, h.Claude);
        await WaitGitAsync(h.Main, b, g => g != null);
        await h.Ui.DrainAsync();
        await Task.Delay(100);
        reader.Pulls.Should().HaveCount(1, "同一個 repository 正在拉：另一個資料夾先不拉");

        reader.PullGate.SetResult();
        await h.WaitUntilAsync(() => reader.Pulls.Count == 2, "拉完重讀這個 repo 的其他資料夾，輪到它拉");
        reader.MaxConcurrentPulls.Should().Be(1);
        a.Should().NotBe(b);
    }

    private static string? AutoPullText(JObject? git) => (git?["autoPull"] as JObject)?.Value<string>("text");

    [Fact]
    public async Task 自動拉取_未拉取而且乾淨_拉完重讀_面板顯示已拉取()
    {
        var (h, reader) = Create();
        using var dispose = h;
        var pulled = false;
        reader.Status = folder => pulled ? Repo(folder, ahead: 0, tracked: RemoteSha) : Repo(folder, ahead: 0, behind: 2);
        reader.Pull = plan =>
        {
            pulled = true;
            return GitPullResult.Done(plan.Behind);
        };

        var id = h.NewConversation(h.Main, h.Claude);
        var shown = await WaitGitAsync(h.Main, id, g => AutoPullText(g) == "已從 origin/main 快轉拉取 2 個提交" && g!.Value<int>("behind") == 0);

        ((JObject)shown["git"]!["autoPull"]!).Value<string>("state").Should().Be("pulled");
        reader.Pulls.Should().ContainSingle().Which.Should().Be(new GitPullPlan("refs/remotes/origin/main", "origin/main", null, LocalSha, 2));
    }

    [Fact]
    public async Task 自動拉取_對話執行中不拉_這一輪結束後才拉()
    {
        var (h, reader) = Create();
        using var dispose = h;
        reader.Status = folder => Repo(folder, ahead: 0, behind: 1, tracked: RemoteSha);
        var (id, session) = h.StartConversation(h.Main, h.Claude);
        await WaitGitAsync(h.Main, id, g => AutoPullText(g) == "這次不拉：這個資料夾有對話正在執行，結束後再拉");
        reader.Pulls.Should().BeEmpty();

        h.CompleteTurn(session);

        await h.WaitUntilAsync(() => reader.Pulls.Count == 1, "這一輪結束、重讀之後拉取");
    }

    [Fact]
    public async Task 自動拉取_有未提交的變更_不拉_寫出原因()
    {
        var (h, reader) = Create();
        using var dispose = h;
        reader.Status = folder => Repo(folder, changed: 2, ahead: 0, behind: 1, tracked: RemoteSha);
        var id = h.NewConversation(h.Main, h.Claude);
        await WaitGitAsync(h.Main, id, g => AutoPullText(g) == "這次不拉：有 2 個已追蹤的檔案還沒提交");
        await h.Ui.DrainAsync();
        reader.Pulls.Should().BeEmpty();
    }

    [Fact]
    public async Task 自動拉取失敗_15分鐘內不再自動試_使用者重新檢查照樣試()
    {
        var (h, reader) = Create();
        using var dispose = h;
        var now = DateTime.UtcNow;
        h.Hub.GitClock = () => now;
        reader.Status = folder => Repo(folder, ahead: 0, behind: 1, tracked: RemoteSha);
        reader.Pull = _ => GitPullResult.Fail("fatal: unable to access 'https://example.test/x.git/': Could not resolve host");
        var id = h.NewConversation(h.Main, h.Claude);
        h.Command(h.Main, "visible", new { conversationIds = new[] { id } });
        await WaitGitAsync(h.Main, id, g => AutoPullText(g)?.StartsWith("失敗：fatal: unable to access") == true);
        await h.Ui.DrainAsync();
        reader.Pulls.Should().HaveCount(1);

        now = now.AddMinutes(5);
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        await h.Ui.DrainAsync();
        await Task.Delay(100);
        reader.Pulls.Should().HaveCount(1, "失敗之後 15 分鐘內定時的重讀不再拉");

        h.Command(h.Main, "gitRefresh", new { conversationId = id });
        await h.WaitUntilAsync(() => reader.Pulls.Count == 2, "使用者按重新檢查照樣拉");
        await h.WaitUntilAsync(() => reader.RemoteChecks.Count >= 2, "重新檢查也問遠端");
        await h.Ui.DrainAsync();
        await Task.Delay(200);
        reader.Pulls.Should().HaveCount(2, "按一次重新檢查只拉一次：重讀本機與問遠端完成時都會判斷，略過冷卻只用一次");

        now = now.AddMinutes(16);
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        await h.WaitUntilAsync(() => reader.Pulls.Count == 3, "冷卻過了，定時的重讀再試");
    }

    private static (HubHarness H, FakeGitReader Reader) Create()
    {
        var h = new HubHarness();
        var reader = new FakeGitReader();
        h.Ui.Invoke(() => h.Hub.AttachGitStatus(reader));
        return (h, reader);
    }

    private static Task<JObject> WaitGitAsync(FakeView view, string conversationId, Func<JObject?, bool> predicate, int since = 0)
        => view.WaitForAsync("gitStatus", m => predicate(m["git"] as JObject), since, timeoutMs: 4000, conversationId: conversationId);

    [Fact]
    public void 沒掛讀取器_不送gitStatus()
    {
        using var h = new HubHarness();
        var id = h.NewConversation(h.Main, h.Claude);
        h.Command(h.Main, "gitRefresh", new { conversationId = id });
        h.Main.Messages("gitStatus").Should().BeEmpty();
    }

    [Fact]
    public async Task 開分頁先送快取的_讀完送狀態_順便問遠端()
    {
        var (h, reader) = Create();
        using var dispose = h;
        var mark = h.Main.Mark();
        var id = h.NewConversation(h.Main, h.Claude);
        var all = h.Main.All().Skip(mark).Select(m => m.Value<string>("type")).ToList();
        all.IndexOf("gitStatus").Should().BeGreaterThan(all.IndexOf("conversation"), "conversation 之後才送（網頁才有這個分頁）");
        h.Main.Messages("gitStatus", mark, id)[0]["git"]!.Type.Should().Be(JTokenType.Null, "還沒讀過");

        var shown = await WaitGitAsync(h.Main, id, g => g != null, mark);
        var git = (JObject)shown["git"]!;
        git.Value<string>("branch").Should().Be("main");
        git.Value<int>("ahead").Should().Be(1);
        var folder = reader.Reads.Single();
        git.Value<string>("root").Should().Be(folder);
        folder.Should().StartWith(h.Temp.Root, "草稿的預設工作區");

        var remote = await WaitGitAsync(h.Main, id, g => g?.Value<bool?>("remoteNew") == true, mark);
        reader.RemoteChecks.Should().ContainSingle().Which.Should().Be(new GitRemoteTarget("origin", "refs/heads/main", LocalSha));
        remote["git"]!["remoteCheckedAt"]!.Type.Should().Be(JTokenType.Integer);

        // 再開一個視窗：快取的直接送，5 秒內不重讀
        var other = h.AddView("other");
        var otherMark = other.Mark();
        h.Command(other, "open", new { conversationId = id });
        other.Messages("gitStatus", otherMark, id).Single()["git"]!.Value<string>("branch").Should().Be("main");
        reader.Reads.Should().HaveCount(1);
    }

    [Fact]
    public async Task 換資料夾_先藏起來再讀新的()
    {
        var (h, reader) = Create();
        using var dispose = h;
        var id = h.NewConversation(h.Main, h.Claude);
        // 等遠端也問完（晚到的結果不能落在下面的比對範圍裡）
        await WaitGitAsync(h.Main, id, g => g?.Value<bool?>("remoteNew") == true);
        reader.Status = folder => folder.EndsWith("plain", StringComparison.OrdinalIgnoreCase) ? null : Repo(folder);

        var plain = h.Temp.CreateDirectory("plain");
        var mark = h.Main.Mark();
        await h.SetFolderAsync(h.Main, id, plain);
        await h.WaitUntilAsync(() => reader.Reads.Contains(plain), "讀新的資料夾");
        await h.Ui.DrainAsync();
        h.Main.Messages("gitStatus", mark, id).Should().NotBeEmpty().And.OnlyContain(m => m["git"]!.Type == JTokenType.Null, "新資料夾不是 repo");

        var repo = h.Temp.CreateDirectory("repo");
        mark = h.Main.Mark();
        await h.SetFolderAsync(h.Main, id, repo);
        h.Main.Messages("gitStatus", mark, id)[0]["git"]!.Type.Should().Be(JTokenType.Null, "還沒讀過的資料夾先藏起舊的");
        var shown = await WaitGitAsync(h.Main, id, g => g != null, mark);
        shown["git"]!.Value<string>("root").Should().Be(repo);
    }

    [Fact]
    public async Task 一輪結束重讀_使用者重新檢查連遠端一起問()
    {
        var (h, reader) = Create();
        using var dispose = h;
        var (id, session) = h.StartConversation(h.Main, h.Claude);
        await WaitGitAsync(h.Main, id, g => g?.Value<bool?>("remoteNew") == true);
        var reads = reader.Reads.Count;

        reader.Status = folder => Repo(folder, changed: 3);
        var mark = h.Main.Mark();
        h.CompleteTurn(session);
        var after = await WaitGitAsync(h.Main, id, g => g?.Value<int>("changed") == 3, mark);
        reader.Reads.Count.Should().Be(reads + 1);
        reader.RemoteChecks.Should().HaveCount(1, "遠端 5 分鐘內問過");

        h.Command(h.Main, "gitRefresh", new { conversationId = id });
        await h.WaitUntilAsync(() => reader.RemoteChecks.Count == 2, "使用者要求時一定問遠端");
        reader.Reads.Count.Should().Be(reads + 2);
        after.Should().NotBeNull();
    }

    [Fact]
    public async Task 定時只讀畫面上看得到的()
    {
        var (h, reader) = Create();
        using var dispose = h;
        var now = DateTime.UtcNow;
        h.Hub.GitClock = () => now;
        var a = h.NewConversation(h.Main, h.Claude);
        var b = h.NewConversation(h.Main, h.Claude);
        await WaitGitAsync(h.Main, a, g => g != null);
        await WaitGitAsync(h.Main, b, g => g != null);
        await h.Ui.DrainAsync();
        // 兩個對話的讀取在背景平行跑，先後不固定：以路徑（對話預設的工作資料夾含對話 id）認出 A 的資料夾
        var folderA = reader.Reads.Single(r => r.Contains(a, StringComparison.OrdinalIgnoreCase));
        reader.Reads.Should().HaveCount(2);

        h.Command(h.Main, "visible", new { conversationIds = new[] { a } });
        reader.Reads.Should().HaveCount(2, "剛讀過");
        now = now.AddSeconds(30);
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        await h.WaitUntilAsync(() => reader.Reads.Count == 3, "看得到的那個重讀");
        await Task.Delay(100);
        reader.Reads.Should().HaveCount(3, "看不到的不讀");
        reader.Reads.Last().Should().Be(folderA);
    }

    [Fact]
    public async Task 讀失敗_留著上次的_一陣子內只有使用者要求才讀()
    {
        var (h, reader) = Create();
        using var dispose = h;
        var now = DateTime.UtcNow;
        h.Hub.GitClock = () => now;
        var id = h.NewConversation(h.Main, h.Claude);
        await WaitGitAsync(h.Main, id, g => g != null);
        h.Command(h.Main, "visible", new { conversationIds = new[] { id } });

        reader.Fail = true;
        var mark = h.Main.Mark();
        h.Command(h.Main, "gitRefresh", new { conversationId = id });
        var kept = await WaitGitAsync(h.Main, id, _ => true, mark);
        kept["git"]!.Value<string>("branch").Should().Be("main", "讀失敗時留著上次的");
        var reads = reader.Reads.Count;

        now = now.AddSeconds(30);
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        await h.Ui.DrainAsync();
        reader.Reads.Should().HaveCount(reads, "2 分鐘內定時不再讀");

        reader.Fail = false;
        h.Command(h.Main, "gitRefresh", new { conversationId = id });
        await h.WaitUntilAsync(() => reader.Reads.Count == reads + 1, "使用者要求照樣讀");
    }

    [Fact]
    public async Task 副本不讀()
    {
        var (h, reader) = Create();
        using var dispose = h;
        var now = DateTime.UtcNow;
        h.Hub.GitClock = () => now;
        var (id, _) = h.StartConversation(h.Main, h.Claude);
        await WaitGitAsync(h.Main, id, g => g?.Value<bool?>("remoteNew") == true);
        var reads = reader.Reads.Count;
        h.Ui.Invoke(() => h.Hub.MarkReplicaForTest(id));
        var mark = h.Main.Mark();
        h.Command(h.Main, "gitRefresh", new { conversationId = id });
        h.Command(h.Main, "visible", new { conversationIds = new[] { id } });
        now = now.AddMinutes(10);
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        await h.Ui.DrainAsync();
        reader.Reads.Should().HaveCount(reads);
        h.Main.Messages("gitStatus", mark, id).Should().BeEmpty();
    }
}
