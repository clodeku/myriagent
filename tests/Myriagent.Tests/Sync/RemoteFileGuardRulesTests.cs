using Myriagent.Remote;
using Myriagent.Sync;
using R = Myriagent.Sync.RemoteFileGuardReasons;

namespace Myriagent.Tests.Sync;

/// <summary>
/// RemoteFileGuard 的純函式部分（WP4.7 步 7；設計 8.7 第 1、4、5 步、SEC-22 的字串語料）：拒絕原因的分類、範圍、禁止清單、
/// 檔名拒絕清單、GetFinalPathNameByHandle 結果的解讀、放行的重新解析標籤。一律不碰檔案系統
/// </summary>
public sealed class RemoteFileGuardRulesTests
{
    private const string Data = @"C:\Users\Amy\AppData\Local\Myriagent";
    private const string Conversation = Data + @"\conversations\c1";

    private static readonly RemoteFolderContext Folders = new(
        UserProfile: @"C:\Users\Amy",
        LocalAppData: @"C:\Users\Amy\AppData\Local",
        RoamingAppData: @"C:\Users\Amy\AppData\Roaming",
        AppDataRoot: Data,
        AccountConfigDirectories: [Data + @"\accounts\a1"],
        AllowedDataSubtrees: [Conversation + @"\workspace", Conversation + @"\attachments"],
        ConversationDirectory: Conversation);

    private static RemoteFileScope Scope(string[] listRoots, string[]? exact = null, RemoteFolderContext? folders = null)
        => new("c1", listRoots, new HashSet<string>(exact ?? [], StringComparer.OrdinalIgnoreCase), folders ?? Folders);

    private static readonly RemoteFileScope RepoScope = Scope([@"C:\repo"], [@"C:\repo\a.png", @"C:\repo\clip.mp4", @"C:\repo\notes.txt"]);

    [Theory]
    [InlineData(@"\\server\share\a.png", R.Unc)]
    [InlineData("//server/share/a.png", R.Unc)]
    [InlineData(@"\\server", R.Unc)]
    [InlineData(@"\\?\UNC\srv\s\a.png", R.Unc)]
    [InlineData(@"\\?\unc\srv\s\a.png", R.Unc)]
    [InlineData("//?/UNC/srv/s/a.png", R.Unc)]
    [InlineData(@"\\?\C:\a", R.Device)]
    [InlineData(@"\\.\C:\a", R.Device)]
    [InlineData("//./C:/a", R.Device)]
    [InlineData(@"\\.\PhysicalDrive0", R.Device)]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\a", R.Device)]
    [InlineData(@"\??\C:\a", R.Device)]
    [InlineData("/??/C:/a", R.Device)]
    [InlineData(@"C:\CON", R.Device)]
    [InlineData(@"C:\a\NUL.txt", R.Device)]
    [InlineData(@"C:\a\com1.log", R.Device)]
    [InlineData(@"C:\a\CONOUT$", R.Device)]
    [InlineData(@"C:\a\LPT¹", R.Device)]
    [InlineData(@"C:\a\PRN .txt", R.Device)]
    [InlineData(@"C:\a.png:ads", R.Ads)]
    [InlineData(@"C:\a\b.txt::$DATA", R.Ads)]
    [InlineData(@"C:\a\b:c\d.png", R.Ads)]
    [InlineData("", R.Invalid)]
    [InlineData("a.png", R.Invalid)]
    [InlineData(@"\a.png", R.Invalid)]
    [InlineData("C:", R.Invalid)]
    [InlineData("C:a.png", R.Invalid)]
    [InlineData(@"1:\a.png", R.Invalid)]
    [InlineData(@"C:\..\a.png", R.Invalid)]
    [InlineData(@"C:\a\..\..\x", R.Invalid)]
    [InlineData(@"C:\a\b.", R.Invalid)]
    [InlineData(@"C:\a\b ", R.Invalid)]
    [InlineData(@"C:\a\...", R.Invalid)]
    [InlineData(@"C:\a\b*c", R.Invalid)]
    [InlineData(@"C:\a\b?c", R.Invalid)]
    [InlineData(@"C:\a\b|c", R.Invalid)]
    [InlineData("C:\\a\\b\tc", R.Invalid)]
    [InlineData(@"C:\PROGRA~1\a.png", R.ShortName)]
    [InlineData(@"C:\repo\LONGFI~1.PNG", R.ShortName)]
    public void CheckSyntax_拒絕的原因(string path, string reason)
    {
        RemoteFileGuardRules.CheckSyntax(path, out _).Should().Be(reason, path);
        RemoteFileGuardRules.CheckString(path, RemoteFilePurpose.List, Scope([@"C:\"]), RemoteDriveKind.Fixed).Should().Be(reason, "檔案檢查先跑字串層");
        RemoteFileGuardRules.CheckDirectoryString(path, Scope([@"C:\"]), RemoteDriveKind.Fixed).Should().Be(reason, "資料夾檢查先跑字串層");
    }

    [Fact]
    public void CheckSyntax_null與太長的路徑()
    {
        RemoteFileGuardRules.CheckSyntax(null, out var normalized).Should().Be(R.Invalid);
        normalized.Should().BeNull();
        RemoteFileGuardRules.CheckSyntax(@"C:\" + new string('a', RemoteFolderRules.MaxPathLength), out _).Should().Be(R.Invalid);
    }

    [Theory]
    [InlineData("c:/repo//a.png", @"C:\repo\a.png")]
    [InlineData(@"C:\repo\.\src\..\a.png", @"C:\repo\a.png")]
    [InlineData(@"C:\repo\", @"C:\repo")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"D:\資料\報告 2026.png", @"D:\資料\報告 2026.png")]
    [InlineData(@"C:\repo\LONGNAME~1X.png", @"C:\repo\LONGNAME~1X.png")]
    public void CheckSyntax_通過時是正規化的路徑(string path, string expected)
    {
        RemoteFileGuardRules.CheckSyntax(path, out var normalized).Should().BeNull(path);
        normalized.Should().Be(expected);
    }

    [Fact]
    public void CheckSyntax_短名的正規化也解析點點()
    {
        RemoteFileGuardRules.CheckSyntax(@"c:\x\..\PROGRA~1\.\a.png", out var normalized).Should().Be(R.ShortName);
        normalized.Should().Be(@"C:\PROGRA~1\a.png", "守門員拿這個去 GetLongPathName，不能帶著 ..");
        RemoteFileGuardRules.CheckSyntax(@"C:\CON\PROGRA~1", out var device).Should().Be(R.Device, "保留名稱優先，不會因為有短名而進到展開");
        device.Should().BeNull();
    }

    [Theory]
    [InlineData(@"C:\repo\a.png")]
    [InlineData("C:/repo/a b/c.d.png")]
    [InlineData(@"C:\repo\..\x")]
    [InlineData(@"C:\..")]
    [InlineData(@"C:\a\b.")]
    [InlineData(@"C:\CON")]
    [InlineData(@"\\?\C:\a")]
    [InlineData(@"C:\a:b")]
    [InlineData(@"C:\PROGRA~1")]
    [InlineData(@"C:\~foo\a~b")]
    public void CheckSyntax_通過與否和RemoteFolderRules一致(string path)
    {
        var reason = RemoteFileGuardRules.CheckSyntax(path, out var normalized);
        var folderRules = RemoteFolderRules.TryNormalize(path, out var expected);
        (reason == null).Should().Be(folderRules, path);
        if (reason == null)
            normalized.Should().Be(expected);
    }

    [Fact]
    public void 前綴陷阱()
    {
        RemoteFileGuardRules.CheckString(@"C:\repo2\a.png", RemoteFilePurpose.List, RepoScope, RemoteDriveKind.Fixed).Should().Be(R.Scope);
        RemoteFileGuardRules.CheckString(@"C:\repo2\a.png", RemoteFilePurpose.File, RepoScope, RemoteDriveKind.Fixed).Should().Be(R.Scope);
        RemoteFileGuardRules.CheckDirectoryString(@"C:\repo2", RepoScope, RemoteDriveKind.Fixed).Should().Be(R.Scope);
        RemoteFileGuardRules.CheckString(@"C:\repo\a.png", RemoteFilePurpose.List, RepoScope, RemoteDriveKind.Fixed).Should().BeNull();
        RemoteFileGuardRules.CheckString(@"c:\REPO\sub\x.txt", RemoteFilePurpose.File, RepoScope, RemoteDriveKind.Fixed).Should().BeNull("不分大小寫");
        RemoteFileGuardRules.CheckDirectoryString(@"C:\repo", RepoScope, RemoteDriveKind.Fixed).Should().BeNull("範圍的根本身");
    }

    [Theory]
    [InlineData(".env.local")]
    [InlineData(".env")]
    [InlineData(".envrc")]
    [InlineData("id_rsa.pub")]
    [InlineData("id_rsa")]
    [InlineData("id_ed25519")]
    [InlineData("id_ed25519_sk.pub")]
    [InlineData("id_ecdsa")]
    [InlineData("id_dsa")]
    [InlineData("appsettings.Development.json")]
    [InlineData("appsettings.json")]
    [InlineData("web.config")]
    [InlineData("NuGet.Config")]
    [InlineData("a.PFX")]
    [InlineData("b.p12")]
    [InlineData("server.pem")]
    [InlineData("tls.key")]
    [InlineData("putty.ppk")]
    [InlineData("vault.kdbx")]
    [InlineData("release.jks")]
    [InlineData("debug.keystore")]
    [InlineData(".credentials.json")]
    [InlineData("AUTH.JSON")]
    [InlineData(".claude.json")]
    [InlineData("site.PublishSettings")]
    [InlineData(".npmrc")]
    [InlineData(".git-credentials")]
    [InlineData(".netrc")]
    [InlineData("_netrc")]
    [InlineData(".pgpass")]
    [InlineData(".pypirc")]
    [InlineData(".htpasswd")]
    [InlineData(".dockercfg")]
    [InlineData("credentials")]
    [InlineData("secrets.json")]
    public void 拒絕的檔名(string name)
    {
        RemoteFileGuardRules.IsForbiddenFileName(name).Should().BeTrue(name);
        RemoteFileGuardRules.IsForbiddenFileName(@"C:\repo\sub\" + name).Should().BeTrue("完整路徑只看最後一段");
        RemoteFileGuardRules.CheckString(@"C:\repo\" + name, RemoteFilePurpose.File, RepoScope, RemoteDriveKind.Fixed).Should().Be(R.Name);
        RemoteFileGuardRules.CheckString(@"C:\repo\" + name, RemoteFilePurpose.List, RepoScope, RemoteDriveKind.Fixed).Should().Be(R.Name);
    }

    [Theory]
    [InlineData("a.png")]
    [InlineData("environment.txt")]
    [InlineData("my.env.txt")]
    [InlineData("id_card.png")]
    [InlineData("keys.txt")]
    [InlineData("monkey.pem.png")]
    [InlineData("config.json")]
    [InlineData("appsettings.txt")]
    [InlineData("auth.json.png")]
    [InlineData("credentials.md")]
    [InlineData("README.md")]
    public void 一般的檔名照樣通過(string name)
    {
        RemoteFileGuardRules.IsForbiddenFileName(name).Should().BeFalse(name);
        RemoteFileGuardRules.CheckString(@"C:\repo\" + name, RemoteFilePurpose.File, RepoScope, RemoteDriveKind.Fixed).Should().BeNull();
    }

    [Fact]
    public void git的config只在git資料夾裡擋()
    {
        RemoteFileGuardRules.IsForbiddenFileName(@"C:\repo\.git\config").Should().BeTrue("remote 的網址可能帶帳密");
        RemoteFileGuardRules.IsForbiddenFileName(@"C:\repo\.GIT\CONFIG").Should().BeTrue();
        RemoteFileGuardRules.IsForbiddenFileName(".git/config").Should().BeTrue();
        RemoteFileGuardRules.IsForbiddenFileName(@"C:\repo\.git\modules\lib\config").Should().BeTrue("子模組的 config 一樣");
        RemoteFileGuardRules.IsForbiddenFileName(@"C:\repo\config").Should().BeFalse();
        RemoteFileGuardRules.IsForbiddenFileName("config").Should().BeFalse();
        RemoteFileGuardRules.IsForbiddenFileName(@"C:\repo\x.git\config").Should().BeFalse();
        RemoteFileGuardRules.IsForbiddenFileName(@"C:\repo\.git\HEAD").Should().BeFalse();
        RemoteFileGuardRules.CheckString(@"C:\repo\.git\config", RemoteFilePurpose.File, RepoScope, RemoteDriveKind.Fixed).Should().Be(R.Name);
    }

    [Fact]
    public void 空的檔名一律拒絕_資料夾不看檔名清單()
    {
        RemoteFileGuardRules.IsForbiddenFileName(null).Should().BeTrue();
        RemoteFileGuardRules.IsForbiddenFileName("").Should().BeTrue();
        RemoteFileGuardRules.IsForbiddenFileName(@"C:\repo\").Should().BeTrue();
        RemoteFileGuardRules.CheckString(@"C:\", RemoteFilePurpose.List, Scope([@"C:\"]), RemoteDriveKind.Fixed).Should().Be(R.Name, "根目錄不是檔案");
        RemoteFileGuardRules.CheckDirectoryString(@"C:\repo\.env", RepoScope, RemoteDriveKind.Fixed).Should().BeNull("檔名清單是檔案的名稱");
    }

    [Fact]
    public void 媒體與縮圖只收登記過的精確檔案()
    {
        foreach (var purpose in new[] { RemoteFilePurpose.Media, RemoteFilePurpose.Thumb })
        {
            RemoteFileGuardRules.CheckString(@"C:\repo\a.png", purpose, RepoScope, RemoteDriveKind.Fixed).Should().BeNull();
            RemoteFileGuardRules.CheckString(@"c:/REPO/A.PNG", purpose, RepoScope, RemoteDriveKind.Fixed).Should().BeNull("正規化後不分大小寫相同");
            RemoteFileGuardRules.CheckString(@"C:\repo\clip.mp4", purpose, RepoScope, RemoteDriveKind.Fixed).Should().BeNull();
            RemoteFileGuardRules.CheckString(@"C:\repo\b.png", purpose, RepoScope, RemoteDriveKind.Fixed).Should().Be(R.Scope, "同資料夾、沒登記的不給");
            RemoteFileGuardRules.CheckString(@"C:\repo\notes.txt", purpose, RepoScope, RemoteDriveKind.Fixed).Should().Be(R.Type, "登記過但不是媒體格式");
        }

        RemoteFileGuardRules.CheckString(@"C:\repo\b.png", RemoteFilePurpose.List, RepoScope, RemoteDriveKind.Fixed).Should().BeNull("列檔看範圍資料夾，不看精確檔案");
        RemoteFileGuardRules.CheckString(@"C:\repo\notes.txt", RemoteFilePurpose.File, RepoScope, RemoteDriveKind.Fixed).Should().BeNull("fileFetch 不限媒體格式");
    }

    [Fact]
    public void 精確檔案裡正規化不了的項目對不到任何路徑()
    {
        var scope = Scope([], [@"\\srv\s\a.png", @"C:\PROGRA~1\a.png", @"C:\x\a.png:ads", "a.png"]);
        foreach (var path in new[] { @"C:\PROGRA~1\a.png", @"C:\x\a.png", @"C:\a.png" })
            RemoteFileGuardRules.CheckString(path, RemoteFilePurpose.Media, scope, RemoteDriveKind.Fixed).Should().NotBeNull(path);
    }

    [Theory]
    [InlineData(nameof(RemoteDriveKind.Network))]
    [InlineData(nameof(RemoteDriveKind.CdRom))]
    [InlineData(nameof(RemoteDriveKind.Ram))]
    [InlineData(nameof(RemoteDriveKind.Unknown))]
    [InlineData(nameof(RemoteDriveKind.NoRootDirectory))]
    public void 不是固定或卸除式的磁碟被拒(string kindName)
    {
        var kind = Enum.Parse<RemoteDriveKind>(kindName);
        RemoteFileGuardRules.CheckString(@"C:\repo\a.png", RemoteFilePurpose.Media, RepoScope, kind).Should().Be(R.Drive);
        RemoteFileGuardRules.CheckDirectoryString(@"C:\repo", RepoScope, kind).Should().Be(R.Drive);
    }

    [Fact]
    public void 卸除式磁碟可以()
        => RemoteFileGuardRules.CheckString(@"C:\repo\a.png", RemoteFilePurpose.List, RepoScope, RemoteDriveKind.Removable).Should().BeNull();

    [Fact]
    public void 禁止清單fail_closed()
    {
        var shortRoot = Folders with { AppDataRoot = @"C:\Users\AMY~1\AppData\Local\Myriagent" };
        var scope = Scope([@"C:\repo"], [@"C:\repo\a.png"], shortRoot);

        RemoteFileGuardRules.CheckString(@"C:\repo\a.png", RemoteFilePurpose.List, scope, RemoteDriveKind.Fixed).Should().Be(R.Forbidden, "禁止清單不完整時任何路徑都禁止");
        RemoteFileGuardRules.CheckString(@"C:\repo\a.png", RemoteFilePurpose.Media, scope, RemoteDriveKind.Fixed).Should().Be(R.Forbidden);
        RemoteFileGuardRules.CheckDirectoryString(@"C:\repo", scope, RemoteDriveKind.Fixed).Should().Be(R.Forbidden);
        var badConversation = Scope([@"C:\repo"], folders: Folders with { ConversationDirectory = @"\\srv\c1" });
        RemoteFileGuardRules.CheckString(@"C:\repo\a.png", RemoteFilePurpose.List, badConversation, RemoteDriveKind.Fixed).Should().Be(R.Forbidden, "對話資料夾正規化不了也禁止");
    }

    [Theory]
    [InlineData(@"C:\Users\Amy\.claude\settings.png")]
    [InlineData(@"C:\Users\Amy\.claude.json")]
    [InlineData(@"C:\Users\Amy\.codex\generated_images\t\a.png")]
    [InlineData(@"C:\Users\Amy\.ssh\known.png")]
    [InlineData(@"C:\Users\Amy\AppData\Local\Microsoft\Credentials\x.png")]
    [InlineData(@"C:\Users\Amy\AppData\Local\Myriagent\sync\device.png")]
    [InlineData(@"C:\Users\Amy\AppData\Local\Myriagent\accounts\a1\x.png")]
    [InlineData(@"C:\Users\Amy\AppData\Local\Myriagent\conversations\c2\attachments\x.png")]
    public void 禁止的資料夾就算在範圍與精確檔案裡也拒絕(string path)
    {
        var scope = Scope([@"C:\Users\Amy"], [path]);
        RemoteFileGuardRules.CheckString(path, RemoteFilePurpose.List, scope, RemoteDriveKind.Fixed).Should().Be(R.Forbidden, path);
        RemoteFileGuardRules.CheckString(path, RemoteFilePurpose.Media, scope, RemoteDriveKind.Fixed).Should().Be(R.Forbidden, path);
    }

    [Theory]
    [InlineData("items.json")]
    [InlineData("queue.json")]
    [InlineData("meta.json")]
    [InlineData("items.json.bak")]
    public void items_json與queue_json被拒(string name)
    {
        var path = Conversation + @"\" + name;
        var normal = Scope([Conversation + @"\workspace", Conversation + @"\attachments"]);
        RemoteFileGuardRules.CheckString(path, RemoteFilePurpose.File, normal, RemoteDriveKind.Fixed).Should().Be(R.Scope, "對話資料夾本身不在範圍裡");
        var wide = Scope([Conversation]);
        RemoteFileGuardRules.CheckString(path, RemoteFilePurpose.File, wide, RemoteDriveKind.Fixed).Should().Be(R.Forbidden, "就算範圍放到整個對話資料夾也禁止");
        RemoteFileGuardRules.CheckDirectoryString(Conversation, wide, RemoteDriveKind.Fixed).Should().Be(R.Forbidden);
    }

    [Fact]
    public void 對話資料夾不在資料根目錄裡也擋得住它本身的檔()
    {
        var custom = Folders with
        {
            ConversationDirectory = @"E:\conv\c1",
            AllowedDataSubtrees = [@"E:\conv\c1\attachments"],
        };
        var scope = Scope([@"E:\conv\c1"], [@"E:\conv\c1\cover.png", @"E:\conv\c1\queue.json"], custom);
        RemoteFileGuardRules.CheckString(@"E:\conv\c1\queue.json", RemoteFilePurpose.File, scope, RemoteDriveKind.Fixed).Should().Be(R.Forbidden);
        RemoteFileGuardRules.CheckString(@"E:\conv\c1\queue.json", RemoteFilePurpose.Media, scope, RemoteDriveKind.Fixed).Should().Be(R.Forbidden, "不是媒體格式");
        RemoteFileGuardRules.CheckString(@"E:\conv\c1\cover.png", RemoteFilePurpose.File, scope, RemoteDriveKind.Fixed).Should().Be(R.Forbidden, "預覽與下載不給對話資料夾本身的檔");
        RemoteFileGuardRules.CheckString(@"E:\conv\c1\cover.png", RemoteFilePurpose.Media, scope, RemoteDriveKind.Fixed).Should().BeNull("登記過的媒體照樣給");
        RemoteFileGuardRules.CheckString(@"E:\conv\c1\attachments\a.png", RemoteFilePurpose.File, scope, RemoteDriveKind.Fixed).Should().BeNull();
        RemoteFileGuardRules.CheckString(@"E:\conv\c1x\queue.json", RemoteFilePurpose.File, Scope([@"E:\"], folders: custom), RemoteDriveKind.Fixed)
            .Should().BeNull("對話資料夾以「資料夾＋分隔字元」為界");
    }

    [Theory]
    [InlineData(@"\screens\dashboard-1440-dark.png")]
    [InlineData(@"\release-0.2.512-fresh.png")]
    [InlineData(@"\out\demo\clip.mp4")]
    [InlineData(@"\work\preview\look1_試看_A.mp4")]
    public void 對話資料夾裡登記過的媒體給原檔與縮圖(string relative)
    {
        // 2026-10-23：代理把截圖存在 conversations\{id}\screens\、預覽影片存在 work\preview\，網頁版與其他電腦原本一律被擋成破圖、影片放不出來
        var path = Conversation + relative;
        var scope = Scope([Conversation + @"\workspace", Conversation + @"\attachments"], [path]);
        RemoteFileGuardRules.CheckString(path, RemoteFilePurpose.Media, scope, RemoteDriveKind.Fixed).Should().BeNull(path);
        RemoteFileGuardRules.CheckString(path, RemoteFilePurpose.Thumb, scope, RemoteDriveKind.Fixed).Should().BeNull(path);
        RemoteFileGuardRules.CheckString(path, RemoteFilePurpose.List, scope, RemoteDriveKind.Fixed).Should().Be(R.Scope, "列檔的範圍不變");
        RemoteFileGuardRules.CheckString(path, RemoteFilePurpose.File, Scope([Conversation]), RemoteDriveKind.Fixed)
            .Should().Be(R.Forbidden, "預覽與下載不給對話資料夾本身的檔");
        RemoteFileGuardRules.CheckString(path, RemoteFilePurpose.Media, Scope([Conversation]), RemoteDriveKind.Fixed).Should().Be(R.Scope, "媒體一樣只收精確檔案");
    }

    [Fact]
    public void 對話資料夾裡的媒體例外不放寬其他禁止的地方()
    {
        var other = Data + @"\conversations\c2\screens\a.png";
        RemoteFileGuardRules.CheckString(other, RemoteFilePurpose.Media, Scope([], [other]), RemoteDriveKind.Fixed).Should().Be(R.Forbidden, "別的對話的資料夾");

        var items = Conversation + @"\items.json";
        RemoteFileGuardRules.CheckString(items, RemoteFilePurpose.Media, Scope([], [items]), RemoteDriveKind.Fixed).Should().Be(R.Forbidden, "不是媒體格式");

        var underClaude = Folders with { ConversationDirectory = @"C:\Users\Amy\.claude\conv\c1", AllowedDataSubtrees = [] };
        var claudeShot = @"C:\Users\Amy\.claude\conv\c1\shot.png";
        RemoteFileGuardRules.CheckString(claudeShot, RemoteFilePurpose.Media, Scope([], [claudeShot], underClaude), RemoteDriveKind.Fixed)
            .Should().Be(R.Forbidden, "其他禁止的資料夾照判");

        var shot = Conversation + @"\screens\a.png";
        var incomplete = Folders with { RoamingAppData = @"C:\PROGRA~1\x" };
        RemoteFileGuardRules.CheckString(shot, RemoteFilePurpose.Media, Scope([], [shot], incomplete), RemoteDriveKind.Fixed)
            .Should().Be(R.Forbidden, "禁止清單不完整一律禁止");
        RemoteFileGuardRules.CheckString(shot, RemoteFilePurpose.Media, Scope([], [shot]), RemoteDriveKind.Network).Should().Be(R.Drive);
        RemoteFileGuardRules.CheckString(Conversation + @"\screens\.env.png", RemoteFilePurpose.Media, Scope([], [Conversation + @"\screens\.env.png"]),
            RemoteDriveKind.Fixed).Should().Be(R.Name, "檔名拒絕清單照判");
    }

    [Fact]
    public void 附件資料夾在資料根目錄裡照樣通過()
    {
        var attachment = Conversation + @"\attachments\photo.png";
        var model = Conversation + @"\attachments\.model\photo.jpg";
        var workspace = Conversation + @"\workspace\out\chart.png";
        var scope = Scope([Conversation + @"\workspace", Conversation + @"\attachments"], [attachment, model, workspace]);

        foreach (var path in new[] { attachment, model, workspace })
        {
            RemoteFileGuardRules.CheckString(path, RemoteFilePurpose.List, scope, RemoteDriveKind.Fixed).Should().BeNull(path);
            RemoteFileGuardRules.CheckString(path, RemoteFilePurpose.Media, scope, RemoteDriveKind.Fixed).Should().BeNull(path);
        }

        RemoteFileGuardRules.CheckDirectoryString(Conversation + @"\attachments", scope, RemoteDriveKind.Fixed).Should().BeNull();
    }

    [Theory]
    [InlineData(@"\\?\C:\a\b.png", @"C:\a\b.png")]
    [InlineData(@"\\?\D:\資料\報告.txt", @"D:\資料\報告.txt")]
    [InlineData(@"\\?\C:\", @"C:\")]
    [InlineData(@"\\?\UNC\srv\s\a", null)]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\a", null)]
    [InlineData(@"\\.\C:\a", null)]
    [InlineData(@"\??\C:\a", null)]
    [InlineData(@"C:\a\b.png", null)]
    [InlineData(@"\\?\C:", null)]
    [InlineData(@"\\?\C:a", null)]
    [InlineData(@"\\?\", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void FromFinalPath(string? finalPath, string? expected)
        => RemoteFileGuardRules.FromFinalPath(finalPath).Should().Be(expected);

    [Theory]
    [InlineData(0x9000001Au, true)]   // IO_REPARSE_TAG_CLOUD
    [InlineData(0x9000101Au, true)]   // CLOUD_1
    [InlineData(0x9000601Au, true)]   // CLOUD_6（OneDrive Files On-Demand）
    [InlineData(0x9000F01Au, true)]   // CLOUD_F
    [InlineData(0x80000021u, true)]   // ONEDRIVE
    [InlineData(0x80000015u, true)]   // FILE_PLACEHOLDER
    [InlineData(0xA000000Cu, false)]  // SYMLINK
    [InlineData(0xA0000003u, false)]  // MOUNT_POINT（junction、掛載點）
    [InlineData(0x8000001Bu, false)]  // APPEXECLINK
    [InlineData(0x80000013u, false)]  // DEDUP
    [InlineData(0x90001018u, false)]  // WCI_1
    [InlineData(0xA000001Du, false)]  // LX_SYMLINK
    [InlineData(0xB000001Au, false)]  // 雲端的值加上名稱代理位元
    [InlineData(0x9001001Au, false)]  // 雲端以外的位元
    [InlineData(0u, false)]
    public void 放行的重新解析標籤只有雲端佔位檔(uint tag, bool allowed)
        => RemoteFileGuardRules.IsAllowedReparseTag(tag).Should().Be(allowed, $"0x{tag:X8}");

    [Theory]
    [InlineData(0xA000000Cu, true)]   // SYMLINK
    [InlineData(0xA0000003u, true)]   // MOUNT_POINT（junction、掛載點）
    [InlineData(0xA000001Du, true)]   // LX_SYMLINK
    [InlineData(0xB000001Au, true)]   // 雲端的值加上名稱代理位元
    [InlineData(0x9000601Au, false)]  // CLOUD_6（OneDrive 佔位檔：不導到別處）
    [InlineData(0x80000021u, false)]  // ONEDRIVE
    [InlineData(0x80000013u, false)]  // DEDUP
    [InlineData(0x8000001Bu, false)]  // APPEXECLINK
    [InlineData(0u, false)]
    public void 名稱代理是指到別處的標籤(uint tag, bool surrogate)
        => RemoteFileGuardRules.IsNameSurrogate(tag).Should().Be(surrogate, $"0x{tag:X8}");

    [Fact]
    public void 不認得的用途丟例外()
    {
        var act = () => RemoteFileGuardRules.CheckString(@"C:\repo\a.png", (RemoteFilePurpose)99, RepoScope, RemoteDriveKind.Fixed);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
