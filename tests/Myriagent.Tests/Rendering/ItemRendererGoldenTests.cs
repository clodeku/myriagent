using System.Globalization;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Json;
using Myriagent.Rendering;
using Myriagent.Tests.Hub;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Rendering;

/// <summary>
/// 項目渲染的黃金檔（設計 docs/plans/2026-10-10-multi-device-sync.md 11.1、13.2 WP2.2）：渲染器從中樞抽到 Protocol 之前，
/// 以當時的 AgentHub 把語料（ItemRendererCorpus）渲染成 Item JSON 錄下來；之後中樞、共用渲染器（ItemRenderer＋桌面的 LocalMediaUrls）、
/// 以及「原始項目經 ProtocolJson 送出再收回」後的渲染（伺服器的情況），輸出都必須與它逐位元組相同。
/// 每個黃金檔還記下渲染完之後 /media、/thumb 白名單的狀態（語料裡出現的每個本機路徑放不放行），白名單的登記行為也一起固定。
/// 渲染在固定的文化特性（zh-TW）下進行：附件大小的小數點依文化特性格式化
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class ItemRendererGoldenTests
{
    public static TheoryData<string> Corpora => new(ItemRendererCorpus.Names);

    [Theory]
    [MemberData(nameof(Corpora))]
    public void 中樞渲染_與黃金檔逐位元組相同(string corpus)
    {
        var input = ItemRendererCorpus.Get(corpus);
        using var h = new HubHarness();

        var golden = h.OnUi(() => InCulture(() => BuildGolden(input, item => h.Hub.RenderItem(item, input.WorkingDirectory), h.Hub.IsMediaAllowed, h.Hub.IsThumbAllowed)));

        GoldenFile.Verify(corpus, golden);
    }

    [Theory]
    [MemberData(nameof(Corpora))]
    public void 共用渲染器加桌面網址_與黃金檔逐位元組相同(string corpus)
    {
        var input = ItemRendererCorpus.Get(corpus);

        GoldenFile.Verify(corpus, RenderShared(input));
    }

    [Theory]
    [MemberData(nameof(Corpora))]
    public void 原始項目經ProtocolJson送出再收回後渲染_與黃金檔逐位元組相同(string corpus)
    {
        var input = ItemRendererCorpus.Get(corpus);
        var json = JsonConvert.SerializeObject(input.Items, ProtocolJson.Settings);
        var received = JsonConvert.DeserializeObject<List<AgentChatItem>>(json, ProtocolJson.Settings)!;
        received.Should().HaveCount(input.Items.Count);
        // 建立順序（Seq）不存檔也不送出：接收端自己依順序配（伺服器是 Ord），這裡照原本的順序補回
        for (int i = 0; i < received.Count; i++)
            received[i].Seq = input.Items[i].Seq;

        GoldenFile.Verify(corpus, RenderShared(input with { Items = received }));
    }

    /// <summary>不經中樞：桌面的網址與白名單（LocalMediaUrls）＋共用渲染器</summary>
    private static JObject RenderShared(GoldenCorpus input)
    {
        var urls = new LocalMediaUrls();
        var renderer = new ItemRenderer(urls);
        return InCulture(() => BuildGolden(input, item => renderer.Render(item, input.WorkingDirectory), urls.IsMediaAllowed, urls.IsThumbAllowed));
    }

    [Fact]
    public void 語料涵蓋所有項目種類狀態工具種類附件種類與卡片的決定()
    {
        var items = ItemRendererCorpus.All().SelectMany(c => c.Items).ToList();

        items.Select(i => i.Kind).Distinct().Should().Contain(Enum.GetValues<AgentItemKind>());
        items.Select(i => i.Status).Distinct().Should().Contain(Enum.GetValues<AgentItemStatus>());
        items.Where(i => i.NoticeKind != null).Select(i => i.NoticeKind!.Value).Distinct().Should().Contain(Enum.GetValues<AgentNoticeKind>());
        items.Where(i => i.Kind == AgentItemKind.Tool).Select(i => i.ToolKind ?? "（未指定）").Distinct().Should().Contain(new[]
        {
            "bash", "read", "write", "edit", "search", "web", "agent", "mcp", "todo", "image", "plan", "other", "（未指定）",
        }, "docs/web-protocol.md 的 Item.toolKind 每一種都要有");
        items.SelectMany(i => i.Attachments).Where(a => a != null).Select(a => a.Kind).Distinct().Should().Contain(Enum.GetValues<AgentAttachmentKind>());
        items.Where(i => i.Kind == AgentItemKind.Approval).Select(i => i.Decision).Should().Contain(new[] { null, "allow", "allowSession", "deny" });
        items.Where(i => i.Kind == AgentItemKind.Question).Select(i => i.Decision).Should().Contain(new[] { null, "answered", "skip" });
        items.Where(i => i.Kind == AgentItemKind.PlanApproval).Select(i => i.Decision).Should().Contain(new[] { null, "acceptEdits", "manual", "keepPlanning" });
        items.Select(i => i.Id).Should().OnlyHaveUniqueItems();
    }

    /// <summary>一份黃金檔：語料說明、渲染出的每一筆 Item JSON、渲染完之後語料裡每個本機路徑的白名單狀態</summary>
    internal static JObject BuildGolden(GoldenCorpus corpus, Func<AgentChatItem, JObject> render, Func<string, bool> isMediaAllowed, Func<string, bool> isThumbAllowed)
    {
        var items = new JArray();
        foreach (var item in corpus.Items)
            items.Add(render(item));
        var whitelist = new JArray(CandidatePaths(corpus).Select(path => new JObject
        {
            ["path"] = path,
            ["media"] = isMediaAllowed(path),
            ["thumb"] = isThumbAllowed(path),
        }));
        return new JObject
        {
            ["corpus"] = corpus.Name,
            ["description"] = corpus.Description,
            ["workingDirectory"] = corpus.WorkingDirectory,
            ["items"] = items,
            ["whitelist"] = whitelist,
        };
    }

    /// <summary>
    /// 要查白名單的路徑（依出現順序、不重複）：附件與給模型看的圖、代理產出的媒體、Markdown 轉換找到的本機圖片（文字與計畫內容），
    /// 以及語料另外指定的（轉換失敗、不會被找到的圖片）
    /// </summary>
    private static IEnumerable<string> CandidatePaths(GoldenCorpus corpus)
    {
        var result = new List<string>();
        void AddPath(string? path)
        {
            if (!string.IsNullOrEmpty(path) && !result.Contains(path, StringComparer.Ordinal))
                result.Add(path);
        }

        foreach (var item in corpus.Items)
        {
            foreach (var attachment in item.Attachments.Where(a => a != null))
            {
                AddPath(attachment.StoredPath);
                foreach (var image in attachment.ModelImagePaths)
                    AddPath(image);
            }
            foreach (var media in item.MediaPaths)
                AddPath(media);
            foreach (var markdown in new[] { item.Text, item.Detail })
            {
                var found = new List<string>();
                try
                {
                    AgentMarkdownRenderer.ToHtml(markdown, found, corpus.WorkingDirectory);
                }
                catch (ArgumentException)
                {
                    // 巢狀過深：轉換失敗，裡面的圖片由語料的 ExtraPaths 指定
                }
                foreach (var image in found)
                    AddPath(image);
            }
        }
        foreach (var path in corpus.ExtraPaths)
            AddPath(path);
        return result;
    }

    /// <summary>在 zh-TW 文化特性下執行（附件大小「1.5 MB」的小數點依文化特性）；結束後還原</summary>
    internal static T InCulture<T>(Func<T> action)
    {
        var (culture, uiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-TW");
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }
}
