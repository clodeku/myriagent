using Myriagent.Media;
using Myriagent.Sync;
using MyriagentHub.Media;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Media;

/// <summary>
/// 指令輸出改寫的實作（WP4.7 計畫 D37）：查對話目錄、只對新的參照寫 <c>media_refs</c>、寫完才交回，改寫出來的網址媒體端點查得到。
/// WP4.5 的呼叫點待 rebase 後整合，這裡直接呼叫 <see cref="ICommandOutputRewriter"/>
/// </summary>
public sealed class CommandOutputRewriterTests
{
    [Fact]
    public async Task image帶ref_media_refs有一列list_媒體端點查得到()
    {
        await using var host = await MediaHost.StartAsync();
        var (device, _) = await host.DeviceAsync();
        using var _d = device;
        var conversation = await MediaSeed.ConversationAsync(host, device.Id);
        await MediaSeed.IndexedAsync(host);
        var mediaRef = MediaSeed.NewRef();
        var rewriter = host.Services.GetRequiredService<ICommandOutputRewriter>();

        var rewritten = await rewriter.RewriteAsync(device.Id, "listFiles", conversation, Files(conversation, mediaRef), CancellationToken.None);

        var key = MediaKeyRules.Format(device.Id, mediaRef);
        rewritten!["entries"]![0]!.Value<string>("url").Should().Be("/m/" + key);
        rewritten["entries"]![0]!.Value<string>("thumbUrl").Should().Be("/t/" + key + "?s=160&v=1712345678");
        MediaSeed.Rows(host, conversation).Should().Equal((device.Id, mediaRef, conversation, MediaRefPurposes.List, (string?)null));
        var lookup = host.Services.GetRequiredService<MediaLookup>();
        (await lookup.FindAsync(MediaEndpointKind.Media, key, CancellationToken.None)).Should().NotBeNull("瀏覽器一拿到網址就查得到");
        (await lookup.FindAsync(MediaEndpointKind.Thumb, key, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task 同一個ref第二次不再寫()
    {
        await using var host = await MediaHost.StartAsync();
        var (device, _) = await host.DeviceAsync();
        using var _d = device;
        var conversation = await MediaSeed.ConversationAsync(host, device.Id);
        var mediaRef = MediaSeed.NewRef();
        var rewriter = host.Services.GetRequiredService<CommandOutputRewriter>();
        host.Services.GetRequiredService<ICommandOutputRewriter>().Should().BeSameAs(rewriter);

        (await rewriter.RewriteAsync(device.Id, "listFiles", conversation, Files(conversation, mediaRef), CancellationToken.None)).Should().NotBeNull();
        host.Host.Clock.Advance(TimeSpan.FromDays(1));
        (await rewriter.RewriteAsync(device.Id, "listFiles", conversation, Files(conversation, mediaRef), CancellationToken.None)).Should().NotBeNull();

        rewriter.Writes.Should().Be(1, "第二次全部已經在：不呼叫寫入者");
        rewriter.Inserted.Should().Be(1);
        MediaSeed.Rows(host, conversation).Should().HaveCount(1);

        // 一則裡有新有舊：只寫新的那一列
        var another = MediaSeed.NewRef();
        (await rewriter.RewriteAsync(device.Id, "listFiles", conversation, Files(conversation, mediaRef, another), CancellationToken.None)).Should().NotBeNull();
        rewriter.Writes.Should().Be(2);
        rewriter.Inserted.Should().Be(2);
        MediaSeed.Rows(host, conversation).Select(r => r.Ref).Should().BeEquivalentTo(mediaRef, another);
    }

    [Fact]
    public async Task fileRef登記file參照_有error時不寫列()
    {
        await using var host = await MediaHost.StartAsync();
        var (device, _) = await host.DeviceAsync();
        using var _d = device;
        var conversation = await MediaSeed.ConversationAsync(host, device.Id);
        var rewriter = host.Services.GetRequiredService<CommandOutputRewriter>();
        var mediaRef = MediaSeed.NewRef();

        var ok = await rewriter.RewriteAsync(device.Id, "fileFetch", conversation, FileRef(conversation, mediaRef, error: null), CancellationToken.None);
        var failed = await rewriter.RewriteAsync(device.Id, "fileFetch", conversation, FileRef(conversation, MediaSeed.NewRef(), "這個檔案不能從網頁讀取"), CancellationToken.None);

        var key = MediaKeyRules.Format(device.Id, mediaRef);
        ok!.Value<string>("url").Should().Be("/f/" + key);
        failed!["url"]!.Type.Should().Be(JTokenType.Null);
        MediaSeed.Rows(host, conversation).Should().Equal((device.Id, mediaRef, conversation, MediaRefPurposes.File, (string?)null));
        rewriter.Writes.Should().Be(1);
        (await host.Services.GetRequiredService<MediaLookup>().FindAsync(MediaEndpointKind.File, key, CancellationToken.None)).Should().NotBeNull();

        host.Host.Clock.Advance(MediaLookupRules.FileLifetime);
        (await host.Services.GetRequiredService<MediaLookup>().FindAsync(MediaEndpointKind.File, key, CancellationToken.None)).Should().BeNull("15 分鐘就到期");
    }

    [Fact]
    public async Task 別台裝置的對話與不存在的對話回null_什麼都不寫()
    {
        await using var host = await MediaHost.StartAsync();
        var (owner, _) = await host.DeviceAsync();
        using var _o = owner;
        var (other, _) = await host.DeviceAsync();
        using var _x = other;
        var conversation = await MediaSeed.ConversationAsync(host, owner.Id);
        var rewriter = host.Services.GetRequiredService<CommandOutputRewriter>();

        (await rewriter.RewriteAsync(other.Id, "listFiles", conversation, Files(conversation, MediaSeed.NewRef()), CancellationToken.None)).Should().BeNull();
        var missing = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        (await rewriter.RewriteAsync(owner.Id, "listFiles", missing, Files(missing, MediaSeed.NewRef()), CancellationToken.None)).Should().BeNull();
        await MediaSeed.DeleteAsync(host, owner.Id, conversation);
        (await rewriter.RewriteAsync(owner.Id, "listFiles", conversation, Files(conversation, MediaSeed.NewRef()), CancellationToken.None)).Should().BeNull("已刪除的對話");

        rewriter.Writes.Should().Be(0);
        MediaSeed.Rows(host).Should().BeEmpty();
    }

    [Fact]
    public async Task 不認得的type原樣回同一個物件()
    {
        await using var host = await MediaHost.StartAsync();
        var rewriter = host.Services.GetRequiredService<ICommandOutputRewriter>();
        var toast = new JObject { ["type"] = "toast", ["level"] = "info", ["text"] = "完成" };

        (await rewriter.RewriteAsync(SyncIds.NewDeviceId(), "send", null, toast, CancellationToken.None)).Should().BeSameAs(toast);
    }

    private static JObject Files(string conversationId, params string[] refs) => new()
    {
        ["type"] = "files",
        ["requestId"] = "r1",
        ["conversationId"] = conversationId,
        ["path"] = "C:\\repo",
        ["parent"] = null,
        ["root"] = "C:\\repo",
        ["entries"] = new JArray(refs.Select((r, i) => new JObject
        {
            ["name"] = "shot" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".png",
            ["path"] = "C:\\repo\\shot.png",
            ["kind"] = "image",
            ["size"] = 100,
            ["modified"] = 1L,
            ["hidden"] = false,
            ["ref"] = r,
            ["ver"] = 1712345678L,
            ["url"] = "https://myriagent.local/media?path=x",
        })),
        ["truncated"] = false,
        ["error"] = null,
    };

    private static JObject FileRef(string conversationId, string mediaRef, string? error) => new()
    {
        ["type"] = "fileRef",
        ["requestId"] = "r2",
        ["conversationId"] = conversationId,
        ["ref"] = mediaRef,
        ["name"] = "report.txt",
        ["size"] = 10,
        ["error"] = error,
    };
}
