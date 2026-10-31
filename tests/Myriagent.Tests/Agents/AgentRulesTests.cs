using Myriagent.Agents;

namespace Myriagent.Tests.Agents;

/// <summary>附件、權限模式、Codex 設定檔、會話重開判斷等純函式規則</summary>
public class AgentRulesTests
{
    // ───── 附件 ─────

    [Theory]
    [InlineData("a.PNG", nameof(AgentAttachmentKind.Image))]
    [InlineData("photo.jpeg", nameof(AgentAttachmentKind.Image))]
    [InlineData("scan.tiff", nameof(AgentAttachmentKind.Image))]
    [InlineData("clip.MOV", nameof(AgentAttachmentKind.Video))]
    [InlineData("x.webm", nameof(AgentAttachmentKind.Video))]
    [InlineData("報表.xlsx", nameof(AgentAttachmentKind.Document))]
    [InlineData("photo.heic", nameof(AgentAttachmentKind.Document))]
    [InlineData("noext", nameof(AgentAttachmentKind.Document))]
    public void Classify_依副檔名分類(string name, string kind)
        => AgentAttachmentRules.Classify(name).ToString().Should().Be(kind);

    [Theory]
    [InlineData("a.png", "image")]
    [InlineData("logo.svg", "image")]
    [InlineData("p.HEIC", "image")]
    [InlineData("v.mkv", "video")]
    [InlineData("s.flac", "audio")]
    [InlineData("Program.cs", "code")]
    [InlineData("readme.md", "doc")]
    [InlineData("pack.7z", "archive")]
    [InlineData("data.bin", "file")]
    public void ClassifyForBrowser_檔案瀏覽器的種類(string name, string kind)
        => AgentAttachmentRules.ClassifyForBrowser(name).Should().Be(kind);

    [Theory]
    [InlineData(@"C:\c\a.png", "image/png", 500_000L, 1200, 800, false)]
    [InlineData(@"C:\c\a.png", "image/png", 1_500_000L, 1200, 800, true)]
    [InlineData(@"C:\c\a.jpg", "image/jpeg", 300_000L, 4032, 3024, true)]
    [InlineData(@"C:\c\a.bmp", null, 100_000L, 100, 100, true)]
    [InlineData(@"C:\c\a.webp", "image/webp", 200_000L, 1568, 1000, false)]
    [InlineData(@"C:\c\a.gif", "image/gif", 1_000_000L, 1568, 1568, false)]
    [InlineData(@"C:\c\jpeg-named.png", "image/jpeg", 8_000L, 800, 600, true)]
    [InlineData(@"C:\c\png-named.jpg", "image/png", 8_000L, 800, 600, true)]
    [InlineData(@"C:\c\truncated.png", null, 8_000L, 800, 600, true)]
    public void NeedsModelCopy_內容與副檔名不符_太大太寬或格式不收才另存縮圖(string path, string? contentType, long bytes, int w, int h, bool expected)
        => AgentAttachmentRules.NeedsModelCopy(path, contentType, bytes, w, h).Should().Be(expected);

    private static readonly byte[] PngHead = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 };
    private static readonly byte[] PngTail = { 0, 0, 0, 0, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 };
    private static readonly byte[] JpegHead = { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1 };
    private static readonly byte[] GifHead = { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 1, 0, 1, 0, 0, 0 };
    private static readonly byte[] WebpHead = { 0x52, 0x49, 0x46, 0x46, 0x1C, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 };

    [Fact]
    public void DetectImageMediaType_依檔頭判斷格式()
    {
        AgentAttachmentRules.DetectImageMediaType(PngHead, PngTail, 5_000).Should().Be("image/png");
        AgentAttachmentRules.DetectImageMediaType(JpegHead, new byte[] { 1, 2, 0xFF, 0xD9 }, 5_000).Should().Be("image/jpeg");
        AgentAttachmentRules.DetectImageMediaType(GifHead, new byte[] { 0, 0, 0x3B }, 5_000).Should().Be("image/gif");
        AgentAttachmentRules.DetectImageMediaType(WebpHead, new byte[] { 1, 2, 3 }, 36).Should().Be("image/webp", "RIFF 長度 0x1C＋8 = 36");
        AgentAttachmentRules.DetectImageMediaType(new byte[] { 0x42, 0x4D, 0, 0 }, PngTail, 5_000).Should().BeNull("BMP 不是 Claude 收的格式");
        AgentAttachmentRules.DetectImageMediaType(ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty, 0).Should().BeNull();
    }

    [Fact]
    public void DetectImageMediaType_結尾沒有結束標記_視為截斷回Null()
    {
        AgentAttachmentRules.DetectImageMediaType(PngHead, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 5_000).Should().BeNull("沒有 IEND");
        AgentAttachmentRules.DetectImageMediaType(JpegHead, new byte[] { 1, 2, 3, 4 }, 5_000).Should().BeNull("沒有 EOI");
        AgentAttachmentRules.DetectImageMediaType(GifHead, new byte[] { 1, 2, 3 }, 5_000).Should().BeNull("沒有 0x3B 結尾");
        AgentAttachmentRules.DetectImageMediaType(WebpHead, new byte[] { 1, 2, 3 }, 30).Should().BeNull("比 RIFF 標示的長度短");
    }

    [Theory]
    [InlineData("image/webp", 500_000L, true)]
    [InlineData("image/webp", 1_500_000L, false)]
    [InlineData("image/png", 500_000L, false)]
    [InlineData("image/jpeg", 500_000L, false)]
    [InlineData(null, 500_000L, false)]
    public void CanSendUndecoded_GDI讀不了時只有完整的WebP照原樣給(string? contentType, long bytes, bool expected)
        => AgentAttachmentRules.CanSendUndecoded(contentType, bytes).Should().Be(expected);

    [Fact]
    public void FitWithin_只依最長邊等比縮小()
    {
        AgentAttachmentRules.FitWithin(4032, 3024, 1568).Should().Be((1568, 1176));
        AgentAttachmentRules.FitWithin(1000, 3000, 1568).Should().Be((523, 1568));
        AgentAttachmentRules.FitWithin(800, 600, 1568).Should().Be((800, 600));
        AgentAttachmentRules.FitWithin(0, -5, 1568).Should().Be((1, 1));
    }

    [Fact]
    public void FrameSeconds_均勻取每段中點()
    {
        AgentAttachmentRules.GetFrameSeconds(60).Should().Equal(5, 15, 25, 35, 45, 55);
        AgentAttachmentRules.GetFrameSeconds(null).Should().Equal(0);
        AgentAttachmentRules.GetFrameSeconds(0.5).Should().Equal(0);
        AgentAttachmentRules.GetFrameSeconds(10, 1).Should().Equal(0);
        AgentAttachmentRules.GetFrameSeconds(10, 4).Should().Equal(1.25, 3.75, 6.25, 8.75);
    }

    [Theory]
    [InlineData(0d, "0:00")]
    [InlineData(65d, "1:05")]
    [InlineData(3725d, "1:02:05")]
    [InlineData(-3d, "0:00")]
    public void FormatSeconds_分秒或時分秒(double seconds, string expected)
        => AgentAttachmentRules.FormatSeconds(seconds).Should().Be(expected);

    [Theory]
    [InlineData(512L, "512 B")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(5L * 1024 * 1024, "5 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3 GB")]
    public void FormatSize_自動選單位(long bytes, string expected)
        => AgentAttachmentRules.FormatSize(bytes).Should().Be(expected);

    [Fact]
    public void AttachmentNote_列出路徑與影片畫面()
    {
        var note = AgentAttachmentRules.BuildAttachmentNote(new[]
        {
            new AgentAttachment { FileName = "a.png", StoredPath = @"C:\c\attachments\a.png", Kind = AgentAttachmentKind.Image, ModelImagePaths = { @"C:\c\attachments\a.png" } },
            new AgentAttachment { FileName = "big.tif", StoredPath = @"C:\c\attachments\big.tif", Kind = AgentAttachmentKind.Image },
            new AgentAttachment
            {
                FileName = "v.mp4", StoredPath = @"C:\c\attachments\v.mp4", Kind = AgentAttachmentKind.Video, DurationSeconds = 65,
                ModelImagePaths = new List<string> { "f1", "f2" }, FrameSeconds = new List<double> { 5.4, 65 },
            },
            new AgentAttachment { FileName = "壞.mp4", StoredPath = @"C:\c\attachments\壞.mp4", Kind = AgentAttachmentKind.Video },
            new AgentAttachment { FileName = "b.pdf", StoredPath = @"C:\c\attachments\b.pdf", Kind = AgentAttachmentKind.Document },
        });

        note.Should().Contain("使用者附加了以下檔案");
        note.Should().Contain(@"1. 圖片「a.png」：C:\c\attachments\a.png（圖片已附在這則訊息中）");
        note.Should().Contain(@"2. 圖片「big.tif」：C:\c\attachments\big.tif（無法直接附上，請用工具讀取）");
        note.Should().Contain(@"3. 影片「v.mp4」：C:\c\attachments\v.mp4（長 1:05，已附 2 張畫面（0:05、1:05））");
        note.Should().Contain(@"4. 影片「壞.mp4」：C:\c\attachments\壞.mp4（無法擷取畫面，請自行處理原檔）");
        note.Should().Contain(@"5. 檔案「b.pdf」：C:\c\attachments\b.pdf");
        AgentAttachmentRules.BuildAttachmentNote(Array.Empty<AgentAttachment>()).Should().BeEmpty();
    }

    [Fact]
    public void StoredFileName_清掉不合法字元並為同名加序號()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "報表.xlsx", "報表 (2).xlsx" };
        AgentAttachmentRules.MakeStoredFileName("報表.xlsx", existing.Contains).Should().Be("報表 (3).xlsx");
        AgentAttachmentRules.MakeStoredFileName(@"C:\x\a|b?.txt", _ => false).Should().Be("a_b_.txt");
        AgentAttachmentRules.MakeStoredFileName(".png", _ => false).Should().Be("附件.png");
        AgentAttachmentRules.MakeStoredFileName("結尾有點.", _ => false).Should().Be("結尾有點");
        var longName = new string('長', 200) + ".txt";
        var stored = AgentAttachmentRules.MakeStoredFileName(longName, _ => false);
        stored.Length.Should().Be(120);
        stored.Should().EndWith(".txt");
    }

    [Theory]
    [InlineData("v1.0 ", 'x', 130)]
    [InlineData("報告 2026.10.10 ", '長', 125)]
    public void StoredFileName_沒有副檔名但中段有點號的長檔名_整串截斷不丟例外(string prefix, char filler, int count)
    {
        var name = prefix + new string(filler, count);

        var stored = AgentAttachmentRules.MakeStoredFileName(name, _ => false);

        stored.Length.Should().Be(120);
        stored.Should().Be(name[..120]);
    }

    [Fact]
    public void StoredFileName_整串截斷後結尾的點號與空白去掉()
    {
        var name = new string('x', 119) + ". " + new string('y', 30);

        AgentAttachmentRules.MakeStoredFileName(name, _ => false).Should().Be(new string('x', 119), "Windows 會吃掉檔名結尾的點號，路徑就對不上");
    }

    [Theory]
    [InlineData(@"C:\a\b.png", true)]
    [InlineData("D:/x.txt", true)]
    [InlineData(@"\\server\share\x.png", false)]
    [InlineData("//server/share/x.png", false)]
    [InlineData("relative\\x.png", false)]
    [InlineData("https://x.com/a.png", false)]
    [InlineData("C:", false)]
    [InlineData("1:\\x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsLocalDrivePath_不認UNC與相對路徑(string? path, bool expected)
        => AgentAttachmentRules.IsLocalDrivePath(path).Should().Be(expected);

    [Theory]
    [InlineData(@"C:\a\報表.xlsx", true)]
    [InlineData(@"C:\a\x.pdf", true)]
    [InlineData(@"C:\a\clip.MP4", true)]
    [InlineData(@"C:\a\run.exe", false)]
    [InlineData(@"C:\a\s.ps1", false)]
    [InlineData(@"C:\a\link.lnk", false)]
    [InlineData(@"C:\a\page.html", false)]
    [InlineData(@"C:\a\x.svg", false)]
    [InlineData(@"C:\a\報告.docx", true)]
    [InlineData(@"C:\a\簡報.pptx", true)]
    [InlineData(@"C:\a\巨集.xlsm", false)]
    [InlineData(@"C:\a\舊版.doc", false)]
    [InlineData(@"C:\a\舊版.xls", false)]
    [InlineData(@"C:\a\舊版.ppt", false)]
    [InlineData(@"C:\a\文件.rtf", false)]
    public void IsSafeToOpen_白名單才直接開_可帶巨集的Office格式不直接開(string path, bool expected)
        => AgentAttachmentRules.IsSafeToOpen(path).Should().Be(expected);

    [Theory]
    [InlineData("a.png", "image/png")]
    [InlineData("a.JPG", "image/jpeg")]
    [InlineData("a.MP4", "video/mp4")]
    [InlineData("a.mov", "video/quicktime")]
    [InlineData("a.svg", null)]
    [InlineData("a.html", null)]
    public void MediaContentType_白名單(string path, string? expected)
        => AgentAttachmentRules.GetMediaContentType(path).Should().Be(expected);

    [Theory]
    [InlineData("a.png", "image/png")]
    [InlineData("a.jpeg", "image/jpeg")]
    [InlineData("a.webp", "image/webp")]
    [InlineData("a.bmp", null)]
    [InlineData("a.tif", null)]
    public void ClaudeImageMediaType_只收四種格式(string path, string? expected)
        => AgentAttachmentRules.GetClaudeImageMediaType(path).Should().Be(expected);

    [Theory]
    [InlineData("a.png", true, true, false)]
    [InlineData("a.avif", true, true, false)]
    [InlineData("a.svg", false, false, false)]
    [InlineData("a.mp4", true, false, true)]
    [InlineData("a.mkv", false, false, false)]
    public void 可在畫面顯示的媒體_圖片與可播放影片(string path, bool displayable, bool image, bool video)
    {
        AgentAttachmentRules.IsDisplayableMedia(path).Should().Be(displayable);
        AgentAttachmentRules.IsDisplayableImage(path).Should().Be(image);
        AgentAttachmentRules.IsPlayableVideo(path).Should().Be(video);
    }

    // ───── 權限模式 ─────

    [Theory]
    [InlineData(nameof(AgentPermissionMode.Ask), nameof(AgentPermissionMode.Ask))]
    [InlineData(nameof(AgentPermissionMode.AcceptEdits), nameof(AgentPermissionMode.Auto))]
    [InlineData(nameof(AgentPermissionMode.Plan), nameof(AgentPermissionMode.Ask))]
    [InlineData(nameof(AgentPermissionMode.Auto), nameof(AgentPermissionMode.Auto))]
    [InlineData(nameof(AgentPermissionMode.FullAccess), nameof(AgentPermissionMode.FullAccess))]
    public void Normalize_Codex不支援的模式換成最接近的(string mode, string codexExpected)
    {
        var parsed = Enum.Parse<AgentPermissionMode>(mode);
        AgentPermissionModes.Normalize(AgentProvider.Codex, parsed).ToString().Should().Be(codexExpected);
        AgentPermissionModes.Normalize(AgentProvider.Claude, parsed).Should().Be(parsed, "Claude 五種都支援");
    }

    [Fact]
    public void For_Claude五種Codex三種_都有中文名稱與說明()
    {
        AgentPermissionModes.For(AgentProvider.Claude).Select(o => o.Mode).Should().Equal(
            AgentPermissionMode.Ask, AgentPermissionMode.AcceptEdits, AgentPermissionMode.Plan, AgentPermissionMode.Auto, AgentPermissionMode.FullAccess);
        AgentPermissionModes.For(AgentProvider.Codex).Select(o => o.Mode).Should().Equal(
            AgentPermissionMode.Ask, AgentPermissionMode.Auto, AgentPermissionMode.FullAccess);
        AgentPermissionModes.For(AgentProvider.Claude).Should().OnlyContain(o => o.Label.Length > 0 && o.Description.Length > 0);
        AgentPermissionModes.For(AgentProvider.Claude)[1].Label.Should().Be("自動接受編輯");
        AgentPermissionModes.For(AgentProvider.Claude)[2].Label.Should().Be("規劃模式");
    }

    // ───── Codex 設定檔 ─────

    [Fact]
    public void ParseMcpServerNames_自ConfigToml抓伺服器名稱_子表不重複算()
    {
        var toml = """
            model = "gpt-6-astra"
            [mcp_servers.node_repl]
            command = 'C:\x\node_repl.exe'
            [mcp_servers.node_repl.env]
            A = "1"
            [mcp_servers."my server"]
            [mcp_servers.'single-quoted']
              [ mcp_servers.slack-tools ]
            [plugins."slack@openai-curated"]
            enabled = true
            # [mcp_servers.commented]
            """;
        CodexConfigRules.ParseMcpServerNames(toml).Should().Equal("node_repl", "my server", "single-quoted", "slack-tools");
        CodexConfigRules.ParseMcpServerNames(null).Should().BeEmpty();
        CodexConfigRules.ParseMcpServerNames(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void ResolveHome_有指定就用指定的資料夾()
        => CodexConfigRules.ResolveHome(@"D:\accounts\a1\codex").Should().Be(@"D:\accounts\a1\codex");

    [Fact]
    public void ReadConfig_讀不到回Null()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Myriagent.Tests", "codex-home-" + Guid.NewGuid().ToString("N")[..8]);
        CodexConfigRules.ReadConfig(dir).Should().BeNull();
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "config.toml"), "[mcp_servers.a]\n");
            CodexConfigRules.ParseMcpServerNames(CodexConfigRules.ReadConfig(dir)).Should().Equal("a");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ───── 會話：何時重開程序 ─────

    private static AgentSessionSettings Settings(string? model = null, string? effort = null, string cwd = @"C:\w", bool local = true,
        string accountKey = "a1|C:\\cfg", string exe = @"C:\bin\claude.exe", string sessionId = "s-1", AgentPermissionMode mode = AgentPermissionMode.Ask)
        => new(sessionId, false, new AgentLauncher(exe, Array.Empty<string>()), accountKey, _ => { }, null, model, effort, mode, cwd, @"C:\conv", "說明", local);

    [Fact]
    public void Claude_NeedsRestart_權限模式不必重開_其他設定改了要重開()
    {
        var current = Settings();
        ClaudeAgentSession.NeedsRestart(current, Settings(mode: AgentPermissionMode.FullAccess)).Should().BeFalse("權限模式以 set_permission_mode 即時切換");
        ClaudeAgentSession.NeedsRestart(current, Settings(model: "default")).Should().BeFalse("default 與空白都代表帳戶預設");
        ClaudeAgentSession.NeedsRestart(current, Settings(cwd: @"C:\W")).Should().BeFalse("同一個資料夾（只有大小寫不同）");
        ClaudeAgentSession.NeedsRestart(current, Settings(model: "opus")).Should().BeTrue();
        ClaudeAgentSession.NeedsRestart(current, Settings(effort: "high")).Should().BeTrue();
        ClaudeAgentSession.NeedsRestart(current, Settings(cwd: @"C:\other")).Should().BeTrue();
        ClaudeAgentSession.NeedsRestart(current, Settings(local: false)).Should().BeTrue();
        ClaudeAgentSession.NeedsRestart(current, Settings(accountKey: "a2|D:\\cfg")).Should().BeTrue("換帳戶");
        ClaudeAgentSession.NeedsRestart(current, Settings(exe: @"C:\npm\node.exe")).Should().BeTrue("CLI 更新或換安裝方式");
        ClaudeAgentSession.NeedsRestart(current, Settings(sessionId: "s-2")).Should().BeTrue();
        ClaudeAgentSession.NeedsRestart(current, current with { AutoCompactTokens = 400_000 }).Should().BeTrue("自動壓縮上限在啟動時帶");
        CodexAgentSession.Signature(current).Should().NotBe(CodexAgentSession.Signature(current with { AutoCompactTokens = 400_000 }), "Codex 也要重開 app-server");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("Default", null)]
    [InlineData("haiku", "haiku")]
    public void NormalizeModel_預設不帶Model參數(string? model, string? expected)
    {
        ClaudeAgentSession.NormalizeModel(model).Should().Be(expected);
        CodexAgentSession.NormalizeModel(model).Should().Be(expected);
    }

    [Fact]
    public void Codex_Signature_只看帳戶執行檔與客製設定()
    {
        CodexAgentSession.Signature(Settings(model: "a", mode: AgentPermissionMode.FullAccess, cwd: @"C:\x"))
            .Should().Be(CodexAgentSession.Signature(Settings()), "模型、權限、資料夾每輪都隨 turn/start 帶上，不必重開");
        CodexAgentSession.Signature(Settings(accountKey: "a2|x")).Should().NotBe(CodexAgentSession.Signature(Settings()));
        CodexAgentSession.Signature(Settings(local: false)).Should().NotBe(CodexAgentSession.Signature(Settings()));
    }
}
