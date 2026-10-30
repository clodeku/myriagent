using Myriagent.Sync;

namespace Myriagent.Tests.Sync;

/// <summary>
/// 抽屜（@list）與 fileFetch（@file）的參照（WP4.7 計畫步 11，D33、D36）：抽屜同一個檔 29 天內重用同一個參照、
/// fileFetch 每次都新配；每日修剪抽屜 30 天、fileFetch 15 分鐘
/// </summary>
public sealed class LooseMediaRefRulesTests
{
    private const string Conv = "0123456789abcdef0123456789abcdef";
    private const string OtherConv = "fedcba9876543210fedcba9876543210";
    private const long Now = 1_800_000_000_000;
    private static readonly long Day = (long)TimeSpan.FromDays(1).TotalMilliseconds;

    private static Func<string> Refs(int seed = 7)
    {
        var random = new Random(seed);
        return () => MediaRefIssueRules.NewRef(n =>
        {
            var bytes = new byte[n];
            random.NextBytes(bytes);
            return bytes;
        });
    }

    private static LocalMediaRefRow ListRow(string reference, string path, long createdAt, string conversation = Conv, string item = MediaServerRules.ListItemId)
        => new(reference, conversation, item, null, path, createdAt);

    [Fact]
    public void Assign_抽屜29天內同一個檔重用最新的那一列_不分大小寫_不新增列()
    {
        var next = Refs();
        var older = next();
        var newer = next();
        var existing = new[]
        {
            ListRow(older, @"C:\proj\a.png", Now - 20 * Day),
            ListRow(newer, @"C:\proj\A.PNG", Now - 2 * Day),
        };

        var refs = LooseMediaRefRules.Assign(MediaServerRules.ListItemId, Conv, [@"c:\proj\a.png"], existing, Now, next, out var added);

        refs.Should().Equal(newer);
        added.Should().BeEmpty();
    }

    [Fact]
    public void Assign_抽屜過了29天_別的對話_fileFetch的列_未來的列都不重用()
    {
        var next = Refs();
        var existing = new[]
        {
            ListRow(next(), @"C:\proj\old.png", Now - 29 * Day),
            ListRow(next(), @"C:\proj\other.png", Now - Day, conversation: OtherConv),
            ListRow(next(), @"C:\proj\fetched.png", Now - Day, item: MediaServerRules.FileItemId),
            ListRow(next(), @"C:\proj\future.png", Now + Day),
            ListRow(next(), @"C:\proj\item.png", Now - Day, item: "0123456789abcdef0123456789abcdef"),
        };
        string[] paths = [@"C:\proj\old.png", @"C:\proj\other.png", @"C:\proj\fetched.png", @"C:\proj\future.png", @"C:\proj\item.png"];

        var refs = LooseMediaRefRules.Assign(MediaServerRules.ListItemId, Conv, paths, existing, Now, next, out var added);

        refs.Should().OnlyHaveUniqueItems().And.NotIntersectWith(existing.Select(r => r.Ref));
        added.Should().HaveCount(5);
        added.Select(r => r.MediaPath).Should().Equal(paths);
        added.Should().AllSatisfy(r =>
        {
            r.ConversationId.Should().Be(Conv);
            r.ItemId.Should().Be(MediaServerRules.ListItemId);
            r.AttachmentId.Should().BeNull();
            r.CreatedAt.Should().Be(Now);
        });
        added.Select(r => r.Ref).Should().Equal(refs);
    }

    [Fact]
    public void Assign_剛好不到29天還重用()
    {
        var next = Refs();
        var row = ListRow(next(), @"C:\proj\a.png", Now - (29 * Day - 1));

        LooseMediaRefRules.Assign(MediaServerRules.ListItemId, Conv, [@"C:\proj\a.png"], [row], Now, next, out var added).Should().Equal(row.Ref);
        added.Should().BeEmpty();
    }

    [Fact]
    public void Assign_fileFetch每次都新配_同一次呼叫重複的路徑給同一個()
    {
        var next = Refs();
        var existing = new[] { ListRow(next(), @"C:\proj\a.txt", Now - 1000, item: MediaServerRules.FileItemId) };

        var refs = LooseMediaRefRules.Assign(MediaServerRules.FileItemId, Conv, [@"C:\proj\a.txt", @"C:\PROJ\A.TXT"], existing, Now, next, out var added);

        refs.Should().HaveCount(2);
        refs[0].Should().Be(refs[1]).And.NotBe(existing[0].Ref);
        added.Should().ContainSingle().Which.Should().Be(new LocalMediaRefRow(refs[0], Conv, MediaServerRules.FileItemId, null, @"C:\proj\a.txt", Now));
    }

    [Fact]
    public void Assign_亂數撞到已有的或不合格就重抽_一直撞丟例外()
    {
        var next = Refs();
        var taken = next();
        var fresh = next();
        var queue = new Queue<string>([taken, "not-a-ref", fresh]);
        var existing = new[] { ListRow(taken, @"C:\proj\x.png", Now - 40 * Day) };

        LooseMediaRefRules.Assign(MediaServerRules.ListItemId, Conv, [@"C:\proj\a.png"], existing, Now, queue.Dequeue, out _).Should().Equal(fresh);

        FluentActions.Invoking(() => LooseMediaRefRules.Assign(MediaServerRules.ListItemId, Conv, [@"C:\proj\a.png"], existing, Now, () => taken, out _))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Assign_不合格的輸入丟ArgumentException()
    {
        var next = Refs();
        FluentActions.Invoking(() => LooseMediaRefRules.Assign("0123456789abcdef0123456789abcdef", Conv, [@"C:\a.png"], [], Now, next, out _))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => LooseMediaRefRules.Assign(MediaServerRules.ListItemId, Conv.ToUpperInvariant(), [@"C:\a.png"], [], Now, next, out _))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => LooseMediaRefRules.Assign(MediaServerRules.ListItemId, Conv, [" "], [], Now, next, out _))
            .Should().Throw<ArgumentException>();
        var tooMany = Enumerable.Range(0, LooseMediaRefRules.MaxPathsPerCall + 1).Select(i => $@"C:\p\{i}.png").ToList();
        FluentActions.Invoking(() => LooseMediaRefRules.Assign(MediaServerRules.ListItemId, Conv, tooMany, [], Now, next, out _))
            .Should().Throw<ArgumentException>();

        LooseMediaRefRules.Assign(MediaServerRules.ListItemId, Conv, [], [], Now, next, out var added).Should().BeEmpty();
        added.Should().BeEmpty();
    }

    [Fact]
    public void 期限與修剪的界線()
    {
        LooseMediaRefRules.IsReusable(Now - 29 * Day + 1, Now).Should().BeTrue();
        LooseMediaRefRules.IsReusable(Now - 29 * Day, Now).Should().BeFalse();
        LooseMediaRefRules.IsReusable(Now + 1, Now).Should().BeFalse("本機時鐘倒退過的不信");
        LooseMediaRefRules.ListReuseSince(Now).Should().Be(Now - 29 * Day + 1);
        LooseMediaRefRules.ListPruneBefore(Now).Should().Be(Now - 30 * Day);
        LooseMediaRefRules.FilePruneBefore(Now).Should().Be(Now - 15 * 60_000);
        LooseMediaRefRules.PruneInterval.Should().Be(TimeSpan.FromDays(1));
        LooseMediaRefRules.IsKind(MediaServerRules.ListItemId).Should().BeTrue();
        LooseMediaRefRules.IsKind(MediaServerRules.FileItemId).Should().BeTrue();
        LooseMediaRefRules.IsKind("@other").Should().BeFalse();
        LooseMediaRefRules.IsKind(null).Should().BeFalse();
    }
}
