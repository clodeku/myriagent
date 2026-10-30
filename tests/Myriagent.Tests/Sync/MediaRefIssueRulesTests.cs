using Myriagent.Agents;
using Myriagent.Sync;

namespace Myriagent.Tests.Sync;

/// <summary>媒體參照的配發規則（WP4.7 計畫步 9，D3～D5）：只發給本機磁碟上可顯示的媒體、同一個附件或路徑沿用既有的列、缺列的判斷</summary>
public sealed class MediaRefIssueRulesTests
{
    private const string Conversation = "0f3c9a52b7e94d0c8a61f2d4e5b6c7d8";

    private static int counter;

    /// <summary>可預測的參照（每次呼叫不同）</summary>
    private static string NextRef()
    {
        var n = Interlocked.Increment(ref counter);
        var bytes = new byte[16];
        BitConverter.TryWriteBytes(bytes, n);
        return MediaRefIssueRules.NewRef(_ => bytes);
    }

    private static AgentAttachment Attachment(string id, AgentAttachmentKind kind, string path) => new() { Id = id, Kind = kind, StoredPath = path, FileName = Path.GetFileName(path) };

    private static AgentChatItem Item(params AgentAttachment[] attachments) => new() { Id = "item-1", Attachments = [.. attachments] };

    [Fact]
    public void NewRef_16位元組_26字_Crockford()
    {
        var value = MediaRefIssueRules.NewRef(n => new byte[n]);

        value.Should().HaveLength(26);
        Myriagent.Sync.ConversationEventRules.IsMediaRef(value).Should().BeTrue();
        MediaRefIssueRules.NewRef(System.Security.Cryptography.RandomNumberGenerator.GetBytes).Should().NotBe(MediaRefIssueRules.NewRef(System.Security.Cryptography.RandomNumberGenerator.GetBytes));
        FluentActions.Invoking(() => MediaRefIssueRules.NewRef(_ => new byte[15])).Should().Throw<InvalidOperationException>("亂數來源給錯長度");
    }

    [Theory]
    [InlineData("Image", @"C:\data\conversations\c\attachments\a.png", true)]
    [InlineData("Video", @"D:\clips\b.mp4", true)]
    [InlineData("Document", @"C:\data\a.pdf", false)]
    [InlineData("Document", @"C:\data\a.png", false)]
    [InlineData("Image", @"\\nas\share\a.png", false)]
    [InlineData("Image", @"C:\data\a.heic", false)]
    [InlineData("Image", "a.png", false)]
    [InlineData("Image", "", false)]
    public void NeedsRef_附件(string kind, string path, bool expected)
        => MediaRefIssueRules.NeedsRef(Attachment("att-1", Enum.Parse<AgentAttachmentKind>(kind), path)).Should().Be(expected);

    [Fact]
    public void NeedsRef_附件id上不了線的不發()
        => MediaRefIssueRules.NeedsRef(Attachment("att/1", AgentAttachmentKind.Image, @"C:\a.png")).Should().BeFalse();

    [Theory]
    [InlineData(@"C:\repo\out\chart.png", true)]
    [InlineData(@"C:/repo/out/clip.webm", true)]
    [InlineData(@"\\evil\share\x.png", false)]
    [InlineData(@"//evil/share/x.png", false)]
    [InlineData(@"C:\repo\out\report.pdf", false)]
    [InlineData("chart.png", false)]
    [InlineData(null, false)]
    public void NeedsRef_媒體路徑(string? path, bool expected) => MediaRefIssueRules.NeedsRef(path).Should().Be(expected);

    [Fact]
    public void Merge_沒有列時配新的_附件只填AttachmentId_媒體只填MediaPath()
    {
        var item = Item(Attachment("att-1", AgentAttachmentKind.Image, @"C:\a\x.png"), Attachment("att-2", AgentAttachmentKind.Document, @"C:\a\y.pdf"));
        item.MediaPaths = [@"C:\out\chart.png", @"\\nas\z.png", @"C:\out\chart.png"];

        var refs = MediaRefIssueRules.Merge(item, Conversation, [], NextRef, 1791600000000, out var added);

        refs.Should().HaveCount(2, "文件、UNC 不發；同一個路徑只發一個");
        refs[0].AttachmentId.Should().Be("att-1");
        refs[0].MediaPath.Should().BeNull();
        refs[1].AttachmentId.Should().BeNull();
        refs[1].MediaPath.Should().Be(@"C:\out\chart.png");
        added.Should().HaveCount(2);
        added.Should().OnlyContain(r => r.IsWellFormed && r.ConversationId == Conversation && r.ItemId == "item-1" && r.CreatedAt == 1791600000000);
        added.Select(r => r.Ref).Should().Equal(refs.Select(r => r.Ref));
    }

    [Fact]
    public void Merge_既有的列沿用_只配缺的()
    {
        var item = Item(Attachment("att-1", AgentAttachmentKind.Image, @"C:\a\x.png"));
        item.MediaPaths = [@"C:\out\chart.png"];
        var existing = new LocalMediaRefRow(NextRef(), Conversation, "item-1", "att-1", null, 1);

        var refs = MediaRefIssueRules.Merge(item, Conversation, [existing], NextRef, 2, out var added);

        refs[0].Ref.Should().Be(existing.Ref);
        added.Should().ContainSingle().Which.MediaPath.Should().Be(@"C:\out\chart.png");
    }

    [Fact]
    public void Merge_媒體路徑以序數比對_大小寫不同是另一個()
    {
        var item = Item();
        item.MediaPaths = [@"C:\Out\Chart.png"];
        var existing = new LocalMediaRefRow(NextRef(), Conversation, "item-1", null, @"C:\out\chart.png", 1);

        MediaRefIssueRules.Merge(item, Conversation, [existing], NextRef, 2, out var added);

        added.Should().ContainSingle("同 ConversationWire 的比對（序數）");
    }

    [Fact]
    public void Merge_附件id的列不會被當成媒體路徑的列()
    {
        var item = Item();
        item.MediaPaths = [@"C:\out\chart.png"];
        var wrong = new LocalMediaRefRow(NextRef(), Conversation, "item-1", "att-9", null, 1);

        var refs = MediaRefIssueRules.Merge(item, Conversation, [wrong], NextRef, 2, out var added);

        refs.Should().ContainSingle().Which.Ref.Should().NotBe(wrong.Ref);
        added.Should().ContainSingle();
    }

    [Fact]
    public void Merge_沒有可顯示的媒體回空()
    {
        var item = Item(Attachment("att-1", AgentAttachmentKind.Document, @"C:\a\x.docx"));

        MediaRefIssueRules.Merge(item, Conversation, [], NextRef, 1, out var added).Should().BeEmpty();
        added.Should().BeEmpty();
        MediaRefIssueRules.HasDisplayableMedia(item).Should().BeFalse();
    }

    [Fact]
    public void Merge_亂數一直重複_丟例外()
    {
        var item = Item(Attachment("att-1", AgentAttachmentKind.Image, @"C:\a\x.png"), Attachment("att-2", AgentAttachmentKind.Image, @"C:\a\y.png"));
        var same = NextRef();

        FluentActions.Invoking(() => MediaRefIssueRules.Merge(item, Conversation, [], () => same, 1, out _)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Missing_有要的媒體而缺列才是true()
    {
        var item = Item(Attachment("att-1", AgentAttachmentKind.Image, @"C:\a\x.png"));
        item.MediaPaths = [@"C:\out\chart.png"];
        var attachmentRow = new LocalMediaRefRow(NextRef(), Conversation, "item-1", "att-1", null, 1);
        var mediaRow = new LocalMediaRefRow(NextRef(), Conversation, "item-1", null, @"C:\out\chart.png", 1);

        MediaRefIssueRules.Missing(item, Conversation, []).Should().BeTrue();
        MediaRefIssueRules.Missing(item, Conversation, [attachmentRow]).Should().BeTrue("媒體路徑還缺");
        MediaRefIssueRules.Missing(item, Conversation, [attachmentRow, mediaRow]).Should().BeFalse();
        MediaRefIssueRules.Missing(item, "1f3c9a52b7e94d0c8a61f2d4e5b6c7d8", [attachmentRow, mediaRow]).Should().BeTrue("別的對話的列不算");
        MediaRefIssueRules.Missing(Item(Attachment("att-1", AgentAttachmentKind.Document, @"C:\a\x.pdf")), Conversation, []).Should().BeFalse("文件不需要參照");
    }
}
