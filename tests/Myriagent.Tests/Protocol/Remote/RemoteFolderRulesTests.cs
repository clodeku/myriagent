using Myriagent.Remote;

namespace Myriagent.Tests.Protocol.Remote;

/// <summary>
/// 遠端選資料夾、列檔案的路徑規則（WP4.1 步 6；設計 8.7、14.11 D14、D15、Q4-A）：純字串，不碰檔案系統
/// </summary>
public sealed class RemoteFolderRulesTests
{
    private static readonly RemoteFolderContext Context = new(
        UserProfile: @"C:\Users\Amy",
        LocalAppData: @"C:\Users\Amy\AppData\Local",
        RoamingAppData: @"C:\Users\Amy\AppData\Roaming",
        AppDataRoot: @"D:\MyriagentData",
        AccountConfigDirectories: [@"C:\Users\Amy\AppData\Local\Myriagent\accounts\a1", @"E:\iso\acc2"]);

    [Theory]
    [InlineData(@"C:\repo", @"C:\repo")]
    [InlineData(@"c:\repo", @"C:\repo")]
    [InlineData(@"C:\repo\", @"C:\repo")]
    [InlineData(@"C:\repo\\src\\", @"C:\repo\src")]
    [InlineData("C:/repo/src", @"C:\repo\src")]
    [InlineData(@"C:\repo\.\src", @"C:\repo\src")]
    [InlineData(@"C:\repo\src\..\docs", @"C:\repo\docs")]
    [InlineData(@"C:\repo\..", @"C:\")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData("C:/", @"C:\")]
    [InlineData(@"C:\資料\報告 2026", @"C:\資料\報告 2026")]
    [InlineData(@"C:\a.b\c.d.e", @"C:\a.b\c.d.e")]
    [InlineData(@"C:\.ssh2", @"C:\.ssh2")]
    [InlineData(@"C:\~foo\a~b", @"C:\~foo\a~b")]
    [InlineData(@"C:\LONGNAME~1X", @"C:\LONGNAME~1X")]
    [InlineData(@"C:\CONSOLE\COM10", @"C:\CONSOLE\COM10")]
    public void 正規化(string path, string expected)
    {
        RemoteFolderRules.TryNormalize(path, out var normalized).Should().BeTrue(path);
        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("C:")]
    [InlineData("C:repo")]
    [InlineData(@"repo\src")]
    [InlineData(@"\repo")]
    [InlineData(@".\repo")]
    [InlineData(@"\\server\share\repo")]
    [InlineData("//server/share/repo")]
    [InlineData(@"\\?\C:\repo")]
    [InlineData(@"\\.\C:\repo")]
    [InlineData(@"\??\C:\repo")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"1:\repo")]
    [InlineData(@"C:\..")]
    [InlineData(@"C:\repo\..\..")]
    [InlineData(@"C:\repo\file.txt:secret")]
    [InlineData(@"C:\repo\file.txt::$DATA")]
    [InlineData(@"C:\repo\a*b")]
    [InlineData(@"C:\repo\a?b")]
    [InlineData(@"C:\repo\a|b")]
    [InlineData(@"C:\repo\a<b")]
    [InlineData(@"C:\repo\a""b")]
    [InlineData("C:\\repo\\a\tb")]
    [InlineData("C:\\repo\\a\0b")]
    [InlineData(@"C:\repo.")]
    [InlineData(@"C:\repo ")]
    [InlineData(@"C:\repo\src.\x")]
    [InlineData(@"C:\repo\...")]
    [InlineData(@"C:\CON")]
    [InlineData(@"C:\repo\nul")]
    [InlineData(@"C:\repo\NUL.txt")]
    [InlineData(@"C:\repo\aux.tar.gz")]
    [InlineData(@"C:\repo\COM1")]
    [InlineData(@"C:\repo\com9.log")]
    [InlineData(@"C:\repo\LPT¹")]
    [InlineData(@"C:\repo\CONIN$")]
    [InlineData(@"C:\repo\conout$")]
    [InlineData(@"C:\repo\PRN \x")]
    [InlineData(@"C:\PROGRA~1")]
    [InlineData(@"C:\Users\AMY~1\.ssh")]
    [InlineData(@"C:\repo\MYRIAG~1.TXT")]
    [InlineData(@"C:\repo\a~12")]
    public void 不收的路徑(string? path)
    {
        RemoteFolderRules.TryNormalize(path, out var normalized).Should().BeFalse(path);
        normalized.Should().BeNull();
        RemoteFolderRules.IsForbidden(path, RemoteDriveKind.Fixed, Context).Should().BeTrue("正規化不了的一律禁止");
        RemoteFolderRules.IsWithin(path, @"C:\").Should().BeFalse();
    }

    [Fact]
    public void 路徑長度上限()
    {
        var max = @"C:\" + string.Join('\\', Enumerable.Repeat(new string('a', 200), 200))[..(RemoteFolderRules.MaxPathLength - 3)];
        max.Length.Should().Be(RemoteFolderRules.MaxPathLength);
        RemoteFolderRules.TryNormalize(max, out _).Should().BeTrue();
        RemoteFolderRules.TryNormalize(max + "a", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("PROGRA~1", true)]
    [InlineData("MYRIAG~1.TXT", true)]
    [InlineData("A~1", true)]
    [InlineData("AB~123", true)]
    [InlineData("LONGNA~9.C", true)]
    [InlineData("LONGNAME~1", false)]
    [InlineData("A~1.HTML", false)]
    [InlineData("~1", false)]
    [InlineData("A~", false)]
    [InlineData("A~B", false)]
    [InlineData("A~1.B.C", false)]
    [InlineData("README.md", false)]
    public void 八點三短檔名(string name, bool expected)
    {
        RemoteFolderRules.IsShortName(name).Should().Be(expected);
    }

    [Theory]
    [InlineData(@"C:\repo", @"C:\repo", true)]
    [InlineData(@"C:\repo\src", @"C:\repo", true)]
    [InlineData(@"c:\REPO\Src\", @"C:\repo\", true)]
    [InlineData("C:/repo/src", @"C:\repo", true)]
    [InlineData(@"C:\repo2", @"C:\repo", false)]
    [InlineData(@"C:\repo.old\x", @"C:\repo", false)]
    [InlineData(@"C:\rep", @"C:\repo", false)]
    [InlineData(@"C:\repo\..\secret", @"C:\repo", false)]
    [InlineData(@"C:\repo\src\..\..\Windows", @"C:\repo", false)]
    [InlineData(@"D:\repo", @"C:\repo", false)]
    [InlineData(@"C:\anything", @"C:\", true)]
    [InlineData(@"\\server\share\repo", @"C:\repo", false)]
    [InlineData(@"C:\repo", @"\\server\share", false)]
    public void 在範圍內_以資料夾加分隔字元為界(string path, string root, bool expected)
    {
        RemoteFolderRules.IsWithin(path, root).Should().Be(expected);
    }

    [Fact]
    public void 範圍與已知資料夾()
    {
        RemoteFolderRules.IsInScope(@"C:\work\proj\src", [null, @"D:\conv\1", @"C:\work\proj"]).Should().BeTrue();
        RemoteFolderRules.IsInScope(@"C:\work\project", [@"C:\work\proj"]).Should().BeFalse();
        RemoteFolderRules.IsInScope(@"C:\work", []).Should().BeFalse();

        string?[] recent = [@"C:\Work\Proj\", null, @"\\nas\share"];
        RemoteFolderRules.IsKnown(@"c:\work\proj", recent).Should().BeTrue("正規化後相同");
        RemoteFolderRules.IsKnown(@"C:\work\proj\src", recent).Should().BeFalse("子資料夾不算已知");
        RemoteFolderRules.IsKnown(@"\\nas\share", recent).Should().BeFalse("UNC 一律不收");
        RemoteFolderRules.IsKnown(@"C:\work\proj\..\proj", recent).Should().BeTrue();
    }

    [Fact]
    public void 禁止清單()
    {
        var roots = RemoteFolderRules.ForbiddenRoots(Context);
        roots.Should().Contain(new[]
        {
            @"D:\MyriagentData",
            @"C:\Users\Amy\AppData\Local\Myriagent",
            @"C:\Users\Amy\.claude",
            @"C:\Users\Amy\.claude.json",
            @"C:\Users\Amy\.codex",
            @"C:\Users\Amy\.ssh",
            @"C:\Users\Amy\AppData\Local\Microsoft\Credentials",
            @"C:\Users\Amy\AppData\Roaming\Microsoft\Credentials",
            @"C:\Users\Amy\AppData\Local\Google\Chrome\User Data",
            @"C:\Users\Amy\AppData\Local\Microsoft\Edge\User Data",
            @"C:\Users\Amy\AppData\Roaming\Mozilla\Firefox",
            @"E:\iso\acc2",
        });
        roots.Should().OnlyHaveUniqueItems();

        RemoteFolderRules.TryGetForbiddenRoots(Context, out _, out var unusable).Should().BeTrue("每個資料夾都正規化得了");
        unusable.Should().BeNull();
        RemoteFolderRules.TryGetForbiddenRoots(Context with { RoamingAppData = null, AccountConfigDirectories = [""] }, out var partial, out unusable).Should().BeTrue("null 與空字串略過");
        partial.Should().NotContain(r => r.Contains("Roaming"));
        unusable.Should().BeNull();
    }

    [Fact]
    public void 禁止清單有正規化不了的資料夾_任何路徑都禁止()
    {
        var unc = Context with { AccountConfigDirectories = [@"\\nas\x", ""] };
        RemoteFolderRules.TryGetForbiddenRoots(unc, out var roots, out var unusable).Should().BeFalse("清單不完整");
        unusable.Should().Be(@"\\nas\x");
        roots.Should().NotContain(r => r.StartsWith(@"\\", StringComparison.Ordinal), "列出的只有正規化得了的");
        RemoteFolderRules.IsForbidden(@"C:\Users\Amy\repo", RemoteDriveKind.Fixed, unc, out var reported).Should().BeTrue("fail-closed：不知道漏了哪個資料夾");
        reported.Should().Be(@"\\nas\x");
        RemoteFolderRules.IsForbidden(@"C:\Users\Amy\repo", RemoteDriveKind.Fixed, Context, out reported).Should().BeFalse();
        reported.Should().BeNull();
    }

    [Theory]
    [InlineData(@"C:\Users\RUNNER~1\AppData\Local\Temp\x")]
    [InlineData(@"data")]
    [InlineData(@"D:\MyriagentData.")]
    public void 資料根目錄是短檔名_相對路徑或結尾是點_一律禁止(string appDataRoot)
    {
        // --data 指到 8.3 形式的 %TEMP%：資料根目錄與它底下各帳戶的設定資料夾都正規化不了；長檔名形式的同一個資料夾不能因此放行
        var context = Context with { AppDataRoot = appDataRoot, AccountConfigDirectories = [appDataRoot + @"\accounts\a1"] };
        RemoteFolderRules.IsForbidden(@"C:\Users\runneradmin\AppData\Local\Temp\x\accounts\a1", RemoteDriveKind.Fixed, context).Should().BeTrue();
        RemoteFolderRules.IsForbidden(@"C:\Users\Amy\repo", RemoteDriveKind.Fixed, context, out var unusable).Should().BeTrue();
        unusable.Should().Be(appDataRoot);
    }

    /// <summary>目前的對話是 D:\MyriagentData\conversations\{id}（預設工作資料夾與 attachments 給遠端）</summary>
    private const string ConversationId = "0f3c9a52b7e94d0c8a61f2d4e5b6c7d8";

    private static readonly RemoteFolderContext WithConversation = Context with
    {
        ConversationDirectory = $@"D:\MyriagentData\conversations\{ConversationId}",
        AllowedDataSubtrees = [$@"D:\MyriagentData\conversations\{ConversationId}\workspace", $@"D:\MyriagentData\conversations\{ConversationId}\attachments"],
    };

    [Theory]
    [InlineData($@"D:\MyriagentData\conversations\{ConversationId}\workspace")]
    [InlineData($@"D:\MyriagentData\conversations\{ConversationId}\workspace\src\app.ts")]
    [InlineData($@"d:\myriagentdata\Conversations\{ConversationId}\Attachments\chart.png")]
    public void 預設工作資料夾與附件_在範圍內也不在禁止清單(string path)
    {
        var scope = new[] { WithConversation.AllowedDataSubtrees![0], WithConversation.AllowedDataSubtrees[1] };
        RemoteFolderRules.IsInScope(path, scope).Should().BeTrue(path);
        RemoteFolderRules.IsForbidden(path, RemoteDriveKind.Fixed, WithConversation).Should().BeFalse(path);
        RemoteFolderRules.IsForbidden(path, RemoteDriveKind.Fixed, Context).Should().BeTrue("沒有給允許的子資料夾時，資料根目錄整個禁止");
    }

    [Theory]
    [InlineData($@"D:\MyriagentData\conversations\{ConversationId}")]
    [InlineData($@"D:\MyriagentData\conversations\{ConversationId}\queue.json")]
    [InlineData($@"D:\MyriagentData\conversations\{ConversationId}\items.json")]
    [InlineData($@"D:\MyriagentData\conversations\{ConversationId}\meta.json.corrupt-20261018-010203")]
    [InlineData(@"D:\MyriagentData\conversations\1f3c9a52b7e94d0c8a61f2d4e5b6c7d8\workspace\src")]
    [InlineData(@"D:\MyriagentData\accounts\x")]
    [InlineData(@"D:\MyriagentData\sync")]
    [InlineData(@"D:\MyriagentData\sync\device.json")]
    [InlineData($@"D:\MyriagentData\conversations\{ConversationId}\workspace\..\queue.json")]
    public void 對話資料夾的其他檔案_別的對話_帳戶與同步資料仍然禁止(string path)
    {
        RemoteFolderRules.IsForbidden(path, RemoteDriveKind.Fixed, WithConversation).Should().BeTrue(path);
    }

    [Fact]
    public void 允許的子資料夾要在對話資料夾裡面_其他禁止的資料夾照判()
    {
        // 呼叫端給錯（整個資料根目錄、對話資料夾本身）也不放行
        var wide = Context with { ConversationDirectory = $@"D:\MyriagentData\conversations\{ConversationId}", AllowedDataSubtrees = [@"D:\MyriagentData", $@"D:\MyriagentData\conversations\{ConversationId}"] };
        RemoteFolderRules.IsForbidden(@"D:\MyriagentData\sync\device.json", RemoteDriveKind.Fixed, wide).Should().BeTrue();
        RemoteFolderRules.IsForbidden($@"D:\MyriagentData\conversations\{ConversationId}\queue.json", RemoteDriveKind.Fixed, wide).Should().BeTrue();
        RemoteFolderRules.IsForbidden($@"D:\MyriagentData\conversations\{ConversationId}\workspace", RemoteDriveKind.Fixed, WithConversation with { ConversationDirectory = null })
            .Should().BeTrue("沒有對話資料夾就不免除");

        // 帳戶的設定資料夾剛好在允許的子資料夾裡（使用者把 CLAUDE_CONFIG_DIR 設進 workspace）：仍然禁止
        var nested = WithConversation with { AccountConfigDirectories = [$@"D:\MyriagentData\conversations\{ConversationId}\workspace\.claude-iso"] };
        RemoteFolderRules.IsForbidden($@"D:\MyriagentData\conversations\{ConversationId}\workspace\.claude-iso\.credentials.json", RemoteDriveKind.Fixed, nested).Should().BeTrue();
        RemoteFolderRules.IsForbidden($@"D:\MyriagentData\conversations\{ConversationId}\workspace\src", RemoteDriveKind.Fixed, nested).Should().BeFalse();
    }

    [Theory]
    [InlineData(@"C:\Users\Amy\.ssh")]
    [InlineData(@"C:\Users\Amy\.ssh\id_ed25519")]
    [InlineData(@"c:\users\amy\.SSH\")]
    [InlineData("C:/Users/Amy/.ssh/config")]
    [InlineData(@"C:\Users\Amy\repo\..\.ssh")]
    [InlineData(@"C:\Users\Amy\.claude")]
    [InlineData(@"C:\Users\Amy\.claude.json")]
    [InlineData(@"C:\Users\Amy\.codex\auth.json")]
    [InlineData(@"C:\Users\Amy\AppData\Local\Myriagent\sync")]
    [InlineData(@"D:\MyriagentData\accounts\x")]
    [InlineData(@"d:\myriagentdata")]
    [InlineData(@"E:\ISO\ACC2\.credentials.json")]
    [InlineData(@"C:\Users\Amy\AppData\Local\Google\Chrome\User Data\Default")]
    [InlineData(@"C:\Users\Amy\AppData\Roaming\Mozilla\Firefox\Profiles")]
    [InlineData(@"C:\Users\Amy\AppData\Local\Microsoft\Credentials")]
    public void 禁止清單裡的路徑(string path)
    {
        RemoteFolderRules.IsForbidden(path, RemoteDriveKind.Fixed, Context).Should().BeTrue(path);
    }

    [Theory]
    [InlineData(@"C:\Users\Amy\.ssh2")]
    [InlineData(@"C:\Users\Amy\.sshx\keys")]
    [InlineData(@"C:\Users\Amy\.claude-backup")]
    [InlineData(@"C:\Users\Amy\.codex2")]
    [InlineData(@"C:\Users\Amy\repo")]
    [InlineData(@"C:\Users\Amy")]
    [InlineData(@"C:\Users\Amy\AppData\Local\MyriagentX")]
    [InlineData(@"D:\MyriagentData2")]
    [InlineData(@"C:\")]
    public void 禁止清單以資料夾為界_前綴相同的不算(string path)
    {
        RemoteFolderRules.IsForbidden(path, RemoteDriveKind.Fixed, Context).Should().BeFalse(path);
        RemoteFolderRules.IsForbidden(path, RemoteDriveKind.Removable, Context).Should().BeFalse(path);
    }

    [Theory]
    [InlineData("Network")]
    [InlineData("Unknown")]
    [InlineData("CdRom")]
    [InlineData("Ram")]
    [InlineData("NoRootDirectory")]
    public void 不是固定或卸除式磁碟的一律禁止(string kindName)
    {
        RemoteFolderRules.IsForbidden(@"Z:\share\repo", Enum.Parse<RemoteDriveKind>(kindName), Context).Should().BeTrue("網路磁碟機代號也是「X:\\」開頭");
    }
}
