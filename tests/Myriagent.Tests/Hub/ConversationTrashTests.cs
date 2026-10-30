using System.Security.Cryptography;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 對話回收區（WP4.4 計畫步 7、11，D24、D25、SEC-27）：搬移是改名、原本每個檔案內容不變，只多了伺服器刪除的旁檔（WP4.3 的格式，搬之前寫）；
/// 資訊檔在資料夾旁邊；剛好 30 天還留著、多 1 毫秒才清；搬不動的記在 pending、下次完成；只處理 app 建立的對話資料夾名稱；
/// 還原（TryRestore）搬回對話根目錄、帶著旁檔，拿掉資訊檔與 pending
/// </summary>
public sealed class ConversationTrashTests : IDisposable
{
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    private readonly TempDirectory temp = new("trash");
    private readonly ManualTimeProvider time = new();

    private string ConversationsRoot => temp.Combine("conversations");

    private string TrashRoot => ConversationTrash.RootFor(ConversationsRoot);

    private ConversationTrash NewTrash() => new(TrashRoot, ConversationsRoot, time);

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static TrashInfo Info(long at, string title = "要刪的對話") => new(at, "01J9QZ3K8M5R7T2V4W6X8Y0A1B", "Chrome・Windows", title);

    public void Dispose()
    {
        DirectoryRemover.TryDelete(temp.Root);   // 有唯讀的物件檔（TempDirectory 的 Directory.Delete 刪不掉它）
        temp.Dispose();
    }

    /// <summary>做一個看起來像真的對話資料夾：meta.json、items.json、二進位附件、workspace 裡 git 留下的唯讀物件檔</summary>
    private string SeedConversation(string id)
    {
        var dir = Path.Combine(ConversationsRoot, id);
        Directory.CreateDirectory(Path.Combine(dir, "attachments"));
        File.WriteAllText(Path.Combine(dir, "meta.json"), $$"""{"Id":"{{id}}","Title":"要刪的對話"}""");
        File.WriteAllText(Path.Combine(dir, "items.json"), "[]");
        var bytes = new byte[4096];
        new Random(7).NextBytes(bytes);
        File.WriteAllBytes(Path.Combine(dir, "attachments", "a.png"), bytes);
        var objectFile = Path.Combine(dir, "workspace", ".git", "objects", "ab", "cdef");
        Directory.CreateDirectory(Path.GetDirectoryName(objectFile)!);
        File.WriteAllText(objectFile, "blob");
        File.SetAttributes(objectFile, FileAttributes.ReadOnly);
        return dir;
    }

    /// <summary>資料夾裡每個檔案（相對路徑）的 SHA-256</summary>
    private static Dictionary<string, string> Hashes(string dir)
        => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    /// <summary>伺服器刪除的旁檔以外的檔案的 SHA-256（搬移時只多了旁檔，其他內容不變）</summary>
    private static Dictionary<string, string> HashesWithoutSidecar(string dir)
        => Hashes(dir).Where(kv => kv.Key != ConversationStore.ServerDeletedFileName).ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>伺服器刪除的旁檔的時間（沒有是 null）</summary>
    private static long? SidecarAt(string dir)
    {
        var path = Path.Combine(dir, ConversationStore.ServerDeletedFileName);
        return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)).Value<long>("at") : null;
    }

    private string PendingPath => Path.Combine(TrashRoot, ConversationTrash.PendingFileName);

    private string[] PendingIds()
        => File.Exists(PendingPath)
            ? JObject.Parse(File.ReadAllText(PendingPath))["moves"]!.Select(m => m.Value<string>("conversationId")!).ToArray()
            : [];

    [Theory]
    [InlineData(@"C:\Users\u\AppData\Local\Myriagent\conversations", @"C:\Users\u\AppData\Local\Myriagent\conversations-trash")]
    [InlineData(@"C:\Users\u\AppData\Local\Myriagent\conversations\", @"C:\Users\u\AppData\Local\Myriagent\conversations-trash")]
    [InlineData(@"D:\data\conversations", @"D:\data\conversations-trash")]
    public void RootFor_是對話根目錄的上層加conversations_trash(string conversationsRoot, string expected)
        => ConversationTrash.RootFor(conversationsRoot).Should().Be(expected);

    [Fact]
    public void TryMove_每個檔案的SHA256相同_原資料夾不見_資訊檔在旁邊()
    {
        var id = NewId();
        var source = SeedConversation(id);
        var before = Hashes(source);
        var trash = NewTrash();

        trash.TryMove(id, Info(time.GetUtcNow().ToUnixTimeMilliseconds())).Should().BeTrue();

        Directory.Exists(source).Should().BeFalse();
        before.Should().NotContainKey(ConversationStore.ServerDeletedFileName, "前提：原本沒有旁檔");
        HashesWithoutSidecar(Path.Combine(TrashRoot, id)).Should().BeEquivalentTo(before, "搬移是改名，原本的內容不變（SEC-27）");
        SidecarAt(Path.Combine(TrashRoot, id)).Should().Be(time.GetUtcNow().ToUnixTimeMilliseconds(), "搬之前寫伺服器刪除的旁檔，時間是移進回收區的時間（D25）");
        Hashes(Path.Combine(TrashRoot, id)).Should().HaveCount(before.Count + 1, "只多了旁檔");
        File.GetAttributes(Path.Combine(TrashRoot, id, "workspace", ".git", "objects", "ab", "cdef")).Should().HaveFlag(FileAttributes.ReadOnly);
        trash.Contains(id).Should().BeTrue();
        var info = JObject.Parse(File.ReadAllText(Path.Combine(TrashRoot, id + ConversationTrash.InfoSuffix)));
        info.Value<long>("trashedAt").Should().Be(time.GetUtcNow().ToUnixTimeMilliseconds());
        info.Value<string>("commandId").Should().Be("01J9QZ3K8M5R7T2V4W6X8Y0A1B");
        info.Value<string>("issuer").Should().Be("Chrome・Windows");
        info.Value<string>("title").Should().Be("要刪的對話");
        File.Exists(Path.Combine(TrashRoot, id, "trash.json")).Should().BeFalse("資訊檔不放進對話資料夾");
    }

    [Fact]
    public void TryMove_檔案被占用_回false_原資料夾不動()
    {
        var id = NewId();
        var source = SeedConversation(id);
        var before = Hashes(source);
        var trash = NewTrash();

        using (new FileStream(Path.Combine(source, "items.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            trash.TryMove(id, Info(1)).Should().BeFalse();

        HashesWithoutSidecar(source).Should().BeEquivalentTo(before);
        SidecarAt(source).Should().Be(1, "旁檔先寫（伺服器端已經刪了）：留在原處的資料夾也不會重新發布");
        trash.Contains(id).Should().BeFalse();
        trash.TryMove(id, Info(2)).Should().BeTrue("放開之後再搬就成功");
        HashesWithoutSidecar(Path.Combine(TrashRoot, id)).Should().BeEquivalentTo(before);
        SidecarAt(Path.Combine(TrashRoot, id)).Should().Be(1, "已有旁檔就不重寫，保留第一次的時間");
    }

    [Fact]
    public void TryMove_回收區已有同一個id的舊資料夾_換成這一份()
    {
        var id = NewId();
        Directory.CreateDirectory(Path.Combine(TrashRoot, id));
        File.WriteAllText(Path.Combine(TrashRoot, id, "old.txt"), "舊的");
        var source = SeedConversation(id);
        var before = Hashes(source);

        NewTrash().TryMove(id, Info(5)).Should().BeTrue();

        HashesWithoutSidecar(Path.Combine(TrashRoot, id)).Should().BeEquivalentTo(before);
        SidecarAt(Path.Combine(TrashRoot, id)).Should().Be(5);
    }

    [Fact]
    public void TryMove_原資料夾不在_回true_前一次留下的資訊檔拿掉()
    {
        var id = NewId();
        var trash = NewTrash();
        Directory.CreateDirectory(TrashRoot);
        File.WriteAllText(Path.Combine(TrashRoot, id + ConversationTrash.InfoSuffix), """{"trashedAt":1}""");

        trash.TryMove(id, Info(1)).Should().BeTrue();

        File.Exists(Path.Combine(TrashRoot, id + ConversationTrash.InfoSuffix)).Should().BeFalse();
        trash.Contains(id).Should().BeFalse();
    }

    [Fact]
    public void 回收區_29天留著_剛好30天留著_30天又1毫秒清掉()
    {
        var id = NewId();
        SeedConversation(id);
        var trash = NewTrash();
        trash.TryMove(id, Info(trash.NowMs())).Should().BeTrue();

        time.Advance(29 * Day);
        trash.Purge().Should().Be(0);
        trash.Contains(id).Should().BeTrue("29 天");

        time.Advance(Day);
        trash.Purge().Should().Be(0);
        trash.Contains(id).Should().BeTrue("剛好 30 天還留著");

        time.Advance(TimeSpan.FromMilliseconds(1));
        trash.Purge().Should().Be(1);
        trash.Contains(id).Should().BeFalse("30 天又 1 毫秒清掉");
        File.Exists(Path.Combine(TrashRoot, id + ConversationTrash.InfoSuffix)).Should().BeFalse("資訊檔一併刪掉");
        Directory.Exists(Path.Combine(ConversationsRoot, id)).Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, ConversationTrashRules.RetentionMs - 1, false)]
    [InlineData(0, ConversationTrashRules.RetentionMs, false)]
    [InlineData(0, ConversationTrashRules.RetentionMs + 1, true)]
    [InlineData(1_000, 0, false)]
    public void IsExpired_剛好30天不算_多1毫秒才算_時鐘倒退不算(long trashedAt, long now, bool expired)
        => ConversationTrashRules.IsExpired(trashedAt, now).Should().Be(expired);

    [Fact]
    public void RetentionMs_是30天()
        => ConversationTrashRules.RetentionMs.Should().Be((long)TimeSpan.FromDays(30).TotalMilliseconds);

    [Theory]
    [InlineData("0123456789abcdef0123456789ABCDEF", true)]
    [InlineData("0123456789abcdef0123456789abcde", false)]
    [InlineData("0123456789abcdef0123456789abcdeg", false)]
    [InlineData("pending-moves.json", false)]
    [InlineData("..", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTrashableId_只收app建立的對話資料夾名稱(string? id, bool expected)
        => ConversationTrashRules.IsTrashableId(id).Should().Be(expected);

    [Fact]
    public void id不合格的資料夾不搬也不清()
    {
        var trash = NewTrash();
        var legacy = Path.Combine(ConversationsRoot, "legacy-1");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "meta.json"), "{}");
        var notes = Path.Combine(TrashRoot, "notes");
        Directory.CreateDirectory(notes);
        File.WriteAllText(Path.Combine(TrashRoot, "notes" + ConversationTrash.InfoSuffix), """{"trashedAt":0}""");

        trash.TryMove("legacy-1", Info(0)).Should().BeFalse();
        trash.TryMove(@"..\conversations\legacy-1", Info(0)).Should().BeFalse();
        trash.RecordPending("legacy-1", Info(0));
        time.Advance(400 * Day);
        trash.Purge().Should().Be(0);

        Directory.Exists(legacy).Should().BeTrue("不是 app 建立的名稱不搬");
        Directory.Exists(notes).Should().BeTrue("回收區裡不是對話名稱的資料夾不清");
        File.Exists(Path.Combine(TrashRoot, "notes" + ConversationTrash.InfoSuffix)).Should().BeTrue();
        PendingIds().Should().BeEmpty("不合格的 id 不記進 pending");
        trash.Contains("notes").Should().BeFalse();
        trash.List().Should().BeEmpty();
    }

    [Fact]
    public void 沒有資訊檔的資料夾_從現在起算_不會提早清掉()
    {
        var id = NewId();
        Directory.CreateDirectory(Path.Combine(TrashRoot, id));
        var trash = NewTrash();

        trash.Purge().Should().Be(0);
        trash.Contains(id).Should().BeTrue();
        trash.List().Single().Info.TrashedAt.Should().Be(trash.NowMs(), "補寫資訊檔、從現在起算");

        time.Advance(30 * Day);
        trash.Purge().Should().Be(0);
        time.Advance(TimeSpan.FromMilliseconds(1));
        trash.Purge().Should().Be(1);
    }

    [Fact]
    public void 資料夾已不在的過期資訊檔一併刪掉_沒過期的留著()
    {
        var expired = NewId();
        var fresh = NewId();
        Directory.CreateDirectory(TrashRoot);
        var trash = NewTrash();
        File.WriteAllText(Path.Combine(TrashRoot, expired + ConversationTrash.InfoSuffix), $$"""{"trashedAt":{{trash.NowMs() - ConversationTrashRules.RetentionMs - 1}}}""");
        File.WriteAllText(Path.Combine(TrashRoot, fresh + ConversationTrash.InfoSuffix), $$"""{"trashedAt":{{trash.NowMs()}}}""");

        trash.Purge();

        File.Exists(Path.Combine(TrashRoot, expired + ConversationTrash.InfoSuffix)).Should().BeFalse();
        File.Exists(Path.Combine(TrashRoot, fresh + ConversationTrash.InfoSuffix)).Should().BeTrue();
    }

    [Fact]
    public void CompletePendingMoves_搬得動的完成_鎖住的留著_原資料夾不在的拿掉()
    {
        var (movable, locked, gone) = (NewId(), NewId(), NewId());
        SeedConversation(movable);
        var lockedDir = SeedConversation(locked);
        var lockedHashes = Hashes(lockedDir);
        var trash = NewTrash();
        foreach (var id in new[] { movable, locked, gone })
            trash.RecordPending(id, Info(trash.NowMs(), id));
        PendingIds().Should().Equal(movable, locked, gone);

        IReadOnlySet<string> remaining;
        using (new FileStream(Path.Combine(lockedDir, "attachments", "a.png"), FileMode.Open, FileAccess.Read, FileShare.None))
            remaining = NewTrash().CompletePendingMoves();

        remaining.Should().BeEquivalentTo([locked]);
        remaining.Contains(locked.ToUpperInvariant()).Should().BeTrue("不分大小寫");
        trash.Contains(movable).Should().BeTrue();
        Directory.Exists(lockedDir).Should().BeTrue();
        PendingIds().Should().Equal(locked);
        trash.List().Select(e => e.ConversationId).Should().Equal(movable);

        NewTrash().CompletePendingMoves().Should().BeEmpty();
        HashesWithoutSidecar(Path.Combine(TrashRoot, locked)).Should().BeEquivalentTo(lockedHashes);
        SidecarAt(Path.Combine(TrashRoot, locked)).Should().NotBeNull("補搬也先寫旁檔");
        SidecarAt(Path.Combine(TrashRoot, movable)).Should().NotBeNull();
        trash.List().Single(e => e.ConversationId == locked).Info.Title.Should().Be(locked, "pending 帶著原本的資訊");
        File.Exists(PendingPath).Should().BeFalse("都搬好就刪掉 pending 檔");
    }

    [Fact]
    public void ClearPending_只拿掉那一個()
    {
        var (a, b) = (NewId(), NewId());
        var trash = NewTrash();
        trash.RecordPending(a, Info(1));
        trash.RecordPending(b, Info(1));
        trash.RecordPending(a, Info(2));
        PendingIds().Should().Equal(b, a);

        trash.ClearPending(a.ToUpperInvariant());

        PendingIds().Should().Equal(b);
        trash.ClearPending(b);
        File.Exists(PendingPath).Should().BeFalse();
    }

    [Fact]
    public void 損毀的pending檔_當成沒有_不丟例外()
    {
        Directory.CreateDirectory(TrashRoot);
        File.WriteAllText(PendingPath, "{壞掉");

        NewTrash().CompletePendingMoves().Should().BeEmpty();
    }

    [Fact]
    public void List_新到舊_只列資料夾還在的()
    {
        var (older, newer, missing) = (NewId(), NewId(), NewId());
        SeedConversation(older);
        SeedConversation(newer);
        var trash = NewTrash();
        trash.TryMove(older, Info(trash.NowMs(), "舊")).Should().BeTrue();
        time.Advance(TimeSpan.FromMinutes(1));
        trash.TryMove(newer, Info(trash.NowMs(), "新")).Should().BeTrue();
        File.WriteAllText(Path.Combine(TrashRoot, missing + ConversationTrash.InfoSuffix), """{"trashedAt":9999999999999}""");

        trash.List().Select(e => (e.ConversationId, e.Info.Title)).Should().Equal((newer, "新"), (older, "舊"));
        trash.Contains(missing).Should().BeFalse();
        NewTrash().List().Should().HaveCount(2, "資訊在檔案裡，換一個實例照樣讀得到");
    }

    [Fact]
    public void 回收區不存在時_Purge與List與CompletePendingMoves都不建資料夾()
    {
        var trash = NewTrash();

        trash.Purge().Should().Be(0);
        trash.List().Should().BeEmpty();
        trash.CompletePendingMoves().Should().BeEmpty();
        trash.Contains(NewId()).Should().BeFalse();

        Directory.Exists(TrashRoot).Should().BeFalse("app 啟動時沒有遠端刪除過就不留下空的回收區");
    }

    // ───────────────────────────── 伺服器刪除的旁檔與還原（步 11、D25） ─────────────────────────────

    [Fact]
    public void TryMove_沒有meta的資料夾_不寫旁檔_照樣搬()
    {
        var id = NewId();
        var source = SeedConversation(id);
        File.Delete(Path.Combine(source, "meta.json"));
        var before = Hashes(source);

        NewTrash().TryMove(id, Info(1)).Should().BeTrue();

        Hashes(Path.Combine(TrashRoot, id)).Should().BeEquivalentTo(before, "載入不了的資料夾不用寫旁檔");
    }

    [Fact]
    public void TryMove_旁檔寫不進去_回false_不搬()
    {
        var id = NewId();
        var source = SeedConversation(id);
        // 旁檔的位置被一個資料夾占住：寫不進去（也不算已有旁檔）
        Directory.CreateDirectory(Path.Combine(source, ConversationStore.ServerDeletedFileName));
        var trash = NewTrash();

        trash.TryMove(id, Info(1)).Should().BeFalse();

        Directory.Exists(source).Should().BeTrue("沒有旁檔的資料夾不搬進回收區：之後還原或手動搬回會重新發布");
        trash.Contains(id).Should().BeFalse();
    }

    [Fact]
    public void InfoOf_讀資訊檔_沒有或id不合格是null()
    {
        var id = NewId();
        SeedConversation(id);
        var trash = NewTrash();
        trash.InfoOf(id).Should().BeNull();

        trash.TryMove(id, Info(42, "原本的標題")).Should().BeTrue();

        trash.InfoOf(id).Should().Be(Info(trash.NowMs(), "原本的標題"), "保留期從真正搬進來的時候算，不是傳進來的刪除時間（合併前審查 AMO-01）");
        SidecarAt(Path.Combine(TrashRoot, id)).Should().Be(42, "伺服器刪除旁檔照舊是刪除的時間");
        trash.InfoOf("legacy-1").Should().BeNull();
        trash.InfoOf(@"..\x").Should().BeNull();
    }

    [Fact]
    public void TryRestore_搬回對話根目錄_帶著旁檔_拿掉資訊檔與pending()
    {
        var id = NewId();
        var source = SeedConversation(id);
        var before = Hashes(source);
        var trash = NewTrash();
        trash.RecordPending(id, Info(7));
        trash.TryMove(id, Info(7)).Should().BeTrue();
        PendingIds().Should().Equal(id);

        trash.TryRestore(id).Should().BeTrue();

        Directory.Exists(Path.Combine(TrashRoot, id)).Should().BeFalse();
        HashesWithoutSidecar(source).Should().BeEquivalentTo(before, "還原也是改名");
        SidecarAt(source).Should().Be(7, "旁檔跟著回來：還原的對話是 localOnly、不重新發布");
        File.Exists(Path.Combine(TrashRoot, id + ConversationTrash.InfoSuffix)).Should().BeFalse();
        PendingIds().Should().BeEmpty("不然下次建構中樞又會被搬回回收區");
        trash.Contains(id).Should().BeFalse();
        NewTrash().CompletePendingMoves().Should().BeEmpty();
        Directory.Exists(source).Should().BeTrue();
    }

    [Fact]
    public void TryRestore_回收區的資料夾沒有旁檔_補寫再搬回()
    {
        var id = NewId();
        // 手動放進回收區的資料夾（不是 TryMove 搬的）
        var dir = Path.Combine(TrashRoot, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "meta.json"), $$"""{"Id":"{{id}}","Title":"手動放的"}""");
        var trash = NewTrash();

        trash.TryRestore(id).Should().BeTrue();

        SidecarAt(Path.Combine(ConversationsRoot, id)).Should().Be(trash.NowMs());
    }

    [Fact]
    public void TryRestore_回收區沒有_或對話根目錄已有同一個id_或沒有meta_回false什麼都不動()
    {
        var trash = NewTrash();
        trash.TryRestore(NewId()).Should().BeFalse("回收區沒有");
        trash.TryRestore("legacy-1").Should().BeFalse("id 不合格");

        // 對話根目錄已有同一個 id 的資料夾
        var taken = NewId();
        SeedConversation(taken);
        trash.TryMove(taken, Info(1)).Should().BeTrue();
        var trashed = Hashes(Path.Combine(TrashRoot, taken));
        Directory.CreateDirectory(Path.Combine(ConversationsRoot, taken));
        File.WriteAllText(Path.Combine(ConversationsRoot, taken, "meta.json"), "{}");
        trash.TryRestore(taken).Should().BeFalse();
        Hashes(Path.Combine(TrashRoot, taken)).Should().BeEquivalentTo(trashed);
        Hashes(Path.Combine(ConversationsRoot, taken)).Should().ContainSingle();
        trash.InfoOf(taken).Should().NotBeNull("資訊檔留著");

        // 資料夾裡沒有 meta.json（搬回去也載入不了）
        var noMeta = NewId();
        Directory.CreateDirectory(Path.Combine(TrashRoot, noMeta));
        File.WriteAllText(Path.Combine(TrashRoot, noMeta, "items.json"), "[]");
        trash.TryRestore(noMeta).Should().BeFalse();
        Directory.Exists(Path.Combine(ConversationsRoot, noMeta)).Should().BeFalse();
        Hashes(Path.Combine(TrashRoot, noMeta)).Keys.Should().Equal("items.json");
    }

    [Fact]
    public void TryRestore_檔案被占用_回false_留在回收區()
    {
        var id = NewId();
        SeedConversation(id);
        var trash = NewTrash();
        trash.TryMove(id, Info(3)).Should().BeTrue();

        using (new FileStream(Path.Combine(TrashRoot, id, "items.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            trash.TryRestore(id).Should().BeFalse();

        trash.Contains(id).Should().BeTrue();
        trash.InfoOf(id).Should().Be(Info(trash.NowMs()));
        Directory.Exists(Path.Combine(ConversationsRoot, id)).Should().BeFalse();
    }

    // ───────────────────────────── 補搬的保留期與刪除之後又用過的（合併前審查 AMO-01） ─────────────────────────────

    [Fact]
    public void CompletePendingMoves_搬了31天才搬得動_保留期從搬進來的時候算_不會立刻被清掉()
    {
        var id = NewId();
        var dir = SeedConversation(id);
        var trash = NewTrash();
        var deletedAt = trash.NowMs();
        trash.RecordPending(id, Info(deletedAt));
        using (new FileStream(Path.Combine(dir, "items.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            trash.TryMove(id, Info(deletedAt)).Should().BeFalse("前提：刪除當下搬不動");

        time.Advance(31 * Day);
        var later = NewTrash();
        later.CompletePendingMoves().Should().BeEmpty();

        later.Purge().Should().Be(0, "剛搬進來：從現在起算 30 天");
        later.Contains(id).Should().BeTrue();
        later.InfoOf(id)!.TrashedAt.Should().Be(later.NowMs());
        SidecarAt(Path.Combine(TrashRoot, id)).Should().Be(deletedAt, "伺服器刪除旁檔是刪除的時間");
        time.Advance(30 * Day + TimeSpan.FromMilliseconds(1));
        later.Purge().Should().Be(1, "對照：從搬進來起算滿 30 天又 1 毫秒才清");
    }

    [Fact]
    public void CompletePendingMoves_刪除之後資料夾又被用過_不搬_從pending拿掉_帶著旁檔留在對話根目錄()
    {
        var id = NewId();
        var dir = SeedConversation(id);
        var meta = Path.Combine(dir, "meta.json");
        var trash = NewTrash();
        trash.RecordPending(id, Info(trash.NowMs()));
        using (new FileStream(Path.Combine(dir, "items.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            trash.TryMove(id, Info(trash.NowMs())).Should().BeFalse("前提：刪除當下搬不動（留下了資訊檔）");
        File.Exists(Path.Combine(TrashRoot, id + ConversationTrash.InfoSuffix)).Should().BeTrue("前提：搬不動時資訊檔已經寫了");

        // 回滾到舊版、在舊版繼續用了一陣子：meta.json 比記進 pending 時新
        File.WriteAllText(meta, $$"""{"Id":"{{id}}","Title":"在舊版繼續用"}""");
        File.SetLastWriteTimeUtc(meta, File.GetLastWriteTimeUtc(meta).AddMinutes(5));
        var before = Hashes(dir);

        NewTrash().CompletePendingMoves().Should().BeEmpty("不略過：照常載入");

        Directory.Exists(dir).Should().BeTrue("刪除之後又用過的不搬進回收區");
        Hashes(dir).Should().BeEquivalentTo(before);
        SidecarAt(dir).Should().NotBeNull("帶著伺服器刪除的旁檔：只在這台、不重新發布");
        trash.Contains(id).Should().BeFalse();
        PendingIds().Should().BeEmpty();
        File.Exists(Path.Combine(TrashRoot, id + ConversationTrash.InfoSuffix)).Should().BeFalse("搬不動時留下的資訊檔一併拿掉");
    }

    [Fact]
    public void CompletePendingMoves_刪除當下的最後一次存檔不算又用過_照常搬()
    {
        var id = NewId();
        var dir = SeedConversation(id);
        var meta = Path.Combine(dir, "meta.json");
        var trash = NewTrash();
        trash.RecordPending(id, Info(trash.NowMs()));
        // 程序停下時的最後一次存檔：比記進 pending 時新，但在餘裕之內
        File.SetLastWriteTimeUtc(meta, File.GetLastWriteTimeUtc(meta).AddMilliseconds(ConversationTrash.UsedAfterDeleteSlackMs - 1_000));

        NewTrash().CompletePendingMoves().Should().BeEmpty();

        trash.Contains(id).Should().BeTrue();
        Directory.Exists(dir).Should().BeFalse();
    }
}
