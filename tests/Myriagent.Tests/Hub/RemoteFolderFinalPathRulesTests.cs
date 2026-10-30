using Myriagent.Hub;
using Myriagent.Remote;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 遠端選資料夾的真實路徑規則（設計 8.7 第 3 步在資料夾探針的做法）：要求的路徑與真實路徑的比較、真實路徑的磁碟種類、
/// 真實路徑對禁止清單（展開後的與每個禁止的資料夾自己的真實路徑）、連結目標的解讀。純函式，以假路徑驗
/// </summary>
public sealed class RemoteFolderFinalPathRulesTests
{
    private const string DataRoot = @"D:\MyriagentData";

    /// <summary>資料根目錄在 D:、這個對話是 c1 的禁止清單（同探針展開之後的形式）</summary>
    private static readonly RemoteFolderContext Context = new(
        UserProfile: @"C:\Users\Amy",
        LocalAppData: @"C:\Users\Amy\AppData\Local",
        RoamingAppData: @"C:\Users\Amy\AppData\Roaming",
        AppDataRoot: DataRoot,
        AccountConfigDirectories: [@"D:\MyriagentData\accounts\a1\claude"],
        AllowedDataSubtrees: [@"D:\MyriagentData\conversations\c1\workspace", @"D:\MyriagentData\conversations\c1\attachments"],
        ConversationDirectory: @"D:\MyriagentData\conversations\c1");

    private static string Same(string root) => root;

    /// <summary>把 <paramref name="from"/> 底下的路徑換成 <paramref name="to"/> 底下（模擬那個資料夾是 junction）</summary>
    private static Func<string, string> Junction(string from, string to)
        => root => RemoteFolderRules.IsWithin(root, from) ? to + root[from.Length..] : root;

    // ───────────────────────────── Interpret ─────────────────────────────

    [Theory]
    [InlineData(@"C:\repo\proj", @"\\?\C:\repo\proj")]
    [InlineData(@"C:\repo\proj", @"\\?\c:\REPO\Proj")]
    [InlineData(@"C:\repo\proj", @"\\?\C:\repo\proj\")]
    [InlineData(@"C:\", @"\\?\C:\")]
    public void Interpret_一樣_回要求的路徑原樣(string requested, string final)
        => RemoteFolderFinalPathRules.Interpret(requested, final).Should().Be(new RemoteFinalPath(RemoteFinalPathKind.Same, requested));

    [Theory]
    [InlineData(@"C:\work\link", @"\\?\C:\other\proj", @"C:\other\proj")]
    [InlineData(@"C:\work\link", @"\\?\d:\Proj\", @"D:\Proj")]
    [InlineData(@"C:\work\link-data", @"\\?\D:\MyriagentData", @"D:\MyriagentData")]
    public void Interpret_經過連結_回正規化的真實路徑(string requested, string final, string expected)
        => RemoteFolderFinalPathRules.Interpret(requested, final).Should().Be(new RemoteFinalPath(RemoteFinalPathKind.Moved, expected));

    [Theory]
    [InlineData(@"\\?\UNC\srv\share\proj")]
    [InlineData(@"\\?\Volume{0b1f0c1e-0000-0000-0000-100000000000}\proj")]
    [InlineData(@"\\.\C:\proj")]
    [InlineData(@"C:\work\proj")]
    [InlineData(@"\\srv\share")]
    [InlineData("")]
    [InlineData(null)]
    public void Interpret_不是本機磁碟代號的真實路徑_NotLocal(string? final)
    {
        RemoteFolderFinalPathRules.Interpret(@"C:\work\proj", final).Should().Be(new RemoteFinalPath(RemoteFinalPathKind.NotLocal, null));
        RemoteFolderFinalPathRules.NotFollowed.Should().BeEmpty("前提：探針的「不跟進」是空字串，不會是真的路徑");
    }

    [Theory]
    [InlineData(@"\\?\C:\work\CON")]
    [InlineData(@"\\?\C:\work\a.")]
    [InlineData(@"\\?\C:\work\PROGRA~1")]
    [InlineData(@"\\?\C:\work\a:stream")]
    public void Interpret_真實路徑的名稱正規化不了_Invalid(string final)
        => RemoteFolderFinalPathRules.Interpret(@"C:\work\link", final).Should().Be(new RemoteFinalPath(RemoteFinalPathKind.Invalid, null));

    [Fact]
    public void ChangesDrive_只有經過連結而且換了磁碟代號()
    {
        RemoteFolderFinalPathRules.ChangesDrive(@"C:\a", new RemoteFinalPath(RemoteFinalPathKind.Moved, @"D:\b")).Should().BeTrue();
        RemoteFolderFinalPathRules.ChangesDrive(@"c:\a", new RemoteFinalPath(RemoteFinalPathKind.Moved, @"C:\b")).Should().BeFalse("不分大小寫");
        RemoteFolderFinalPathRules.ChangesDrive(@"C:\a", new RemoteFinalPath(RemoteFinalPathKind.Same, @"C:\a")).Should().BeFalse();
        RemoteFolderFinalPathRules.ChangesDrive(@"C:\a", new RemoteFinalPath(RemoteFinalPathKind.NotLocal, null)).Should().BeFalse();
        RemoteFolderFinalPathRules.ChangesDrive(@"C:\a", new RemoteFinalPath(RemoteFinalPathKind.Invalid, null)).Should().BeFalse();
    }

    // ───────────────────────────── Refusal ─────────────────────────────

    [Theory]
    [InlineData(nameof(RemoteFinalPathKind.NotLocal), nameof(RemoteDriveKind.Fixed), nameof(RemoteFolderVerdict.Policy))]
    [InlineData(nameof(RemoteFinalPathKind.Invalid), nameof(RemoteDriveKind.Fixed), nameof(RemoteFolderVerdict.Invalid))]
    [InlineData(nameof(RemoteFinalPathKind.Moved), nameof(RemoteDriveKind.Network), nameof(RemoteFolderVerdict.Policy))]
    [InlineData(nameof(RemoteFinalPathKind.Moved), nameof(RemoteDriveKind.CdRom), nameof(RemoteFolderVerdict.Policy))]
    [InlineData(nameof(RemoteFinalPathKind.Moved), nameof(RemoteDriveKind.Unknown), nameof(RemoteFolderVerdict.Policy))]
    [InlineData(nameof(RemoteFinalPathKind.Same), nameof(RemoteDriveKind.NoRootDirectory), nameof(RemoteFolderVerdict.Policy))]
    public void Refusal_拒絕的代碼同直接選那個地方(string kindName, string driveName, string expectedName)
    {
        var kind = Enum.Parse<RemoteFinalPathKind>(kindName);
        var path = kind is RemoteFinalPathKind.Same or RemoteFinalPathKind.Moved ? @"D:\proj" : null;

        RemoteFolderFinalPathRules.Refusal(new RemoteFinalPath(kind, path), Enum.Parse<RemoteDriveKind>(driveName))
            .Should().Be(Enum.Parse<RemoteFolderVerdict>(expectedName));
    }

    [Theory]
    [InlineData(nameof(RemoteFinalPathKind.Same), nameof(RemoteDriveKind.Fixed))]
    [InlineData(nameof(RemoteFinalPathKind.Moved), nameof(RemoteDriveKind.Fixed))]
    [InlineData(nameof(RemoteFinalPathKind.Moved), nameof(RemoteDriveKind.Removable))]
    public void Refusal_本機磁碟的真實路徑接著比對禁止清單(string kindName, string driveName)
        => RemoteFolderFinalPathRules.Refusal(new RemoteFinalPath(Enum.Parse<RemoteFinalPathKind>(kindName), @"D:\proj"), Enum.Parse<RemoteDriveKind>(driveName))
            .Should().BeNull();

    [Fact]
    public void Refusal_不認得的情形丟例外()
    {
        var act = () => RemoteFolderFinalPathRules.Refusal(new RemoteFinalPath((RemoteFinalPathKind)99, null), RemoteDriveKind.Fixed);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ───────────────────────────── IsForbidden ─────────────────────────────

    [Theory]
    [InlineData(@"D:\MyriagentData", true)]
    [InlineData(@"D:\MyriagentData\accounts", true)]
    [InlineData(@"D:\MyriagentData\sync", true)]
    [InlineData(@"D:\MyriagentData\conversations\c1", true)]
    [InlineData(@"D:\MyriagentData\conversations\c2\workspace", true)]
    [InlineData(@"C:\Users\Amy\.ssh", true)]
    [InlineData(@"C:\Users\Amy\.claude\projects", true)]
    [InlineData(@"C:\Users\Amy\AppData\Local\Myriagent\sync", true)]
    [InlineData(@"D:\MyriagentData\conversations\c1\workspace\src", false)]
    [InlineData(@"D:\MyriagentData\conversations\c1\attachments", false)]
    [InlineData(@"C:\Users\Amy\repo", false)]
    [InlineData(@"D:\MyriagentData2", false)]
    [InlineData(@"E:\proj", false)]
    public void IsForbidden_沒有連結時同展開後的禁止清單(string final, bool forbidden)
    {
        RemoteFolderFinalPathRules.IsForbidden(final, RemoteDriveKind.Fixed, Context, Same, out var unusable).Should().Be(forbidden, final);
        unusable.Should().BeNull();
        RemoteFolderRules.IsForbidden(final, RemoteDriveKind.Fixed, Context).Should().Be(forbidden, "前提：和字串比對一致");
    }

    [Theory]
    [InlineData(@"E:\RealData", true)]
    [InlineData(@"E:\RealData\accounts\a1\claude", true)]
    [InlineData(@"E:\RealData\sync\device.json", true)]
    [InlineData(@"E:\RealData\conversations\c1", true)]
    [InlineData(@"E:\RealData\conversations\c1\queue.json", true)]
    [InlineData(@"E:\RealData\conversations\c2\workspace", true)]
    [InlineData(@"E:\RealData\conversations\c1\workspace\src", false)]
    [InlineData(@"E:\RealData\conversations\c1\attachments\a.png", false)]
    [InlineData(@"E:\RealData2", false)]
    [InlineData(@"E:\proj", false)]
    public void IsForbidden_資料根目錄是junction_照它的真實路徑比對_允許的子樹也換成真實路徑(string final, bool forbidden)
        => RemoteFolderFinalPathRules.IsForbidden(final, RemoteDriveKind.Fixed, Context, Junction(DataRoot, @"E:\RealData"), out _)
            .Should().Be(forbidden, final);

    [Theory]
    [InlineData(@"F:\dotfiles\claude", true)]
    [InlineData(@"F:\dotfiles\claude\projects", true)]
    [InlineData(@"F:\dotfiles\other", false)]
    [InlineData(@"F:\dotfiles", false)]
    public void IsForbidden_使用者資料夾的claude是junction_目標也擋(string final, bool forbidden)
        => RemoteFolderFinalPathRules.IsForbidden(final, RemoteDriveKind.Fixed, Context, Junction(@"C:\Users\Amy\.claude", @"F:\dotfiles\claude"), out _)
            .Should().Be(forbidden, final);

    [Fact]
    public void IsForbidden_允許的子樹換成真實路徑之後不在對話資料夾裡_不免除()
    {
        // workspace 本身是指到別處的 junction：它的真實位置不再是「資料根目錄裡的允許子樹」，資料根目錄裡別的地方照樣禁止
        var resolve = Junction(@"D:\MyriagentData\conversations\c1\workspace", @"G:\elsewhere");

        RemoteFolderFinalPathRules.IsForbidden(@"G:\elsewhere\src", RemoteDriveKind.Fixed, Context, resolve, out _)
            .Should().BeFalse("G: 不在任何禁止的資料夾裡（同直接選 G:\\elsewhere）");
        RemoteFolderFinalPathRules.IsForbidden(@"D:\MyriagentData\conversations\c1\attachments", RemoteDriveKind.Fixed, Context, resolve, out _)
            .Should().BeFalse("attachments 沒有變");
        RemoteFolderFinalPathRules.IsForbidden(@"D:\MyriagentData\accounts", RemoteDriveKind.Fixed, Context, resolve, out _).Should().BeTrue();
    }

    [Fact]
    public void IsForbidden_禁止清單不完整_一律禁止並報出是哪一項()
    {
        var unc = Context with { AccountConfigDirectories = [@"\\srv\share\.claude"] };

        RemoteFolderFinalPathRules.IsForbidden(@"E:\proj", RemoteDriveKind.Fixed, unc, Same, out var unusable).Should().BeTrue();
        unusable.Should().Be(@"\\srv\share\.claude");
    }

    [Fact]
    public void IsForbidden_解析真實路徑只拿到正規化的本機資料夾()
    {
        var asked = new List<string>();

        RemoteFolderFinalPathRules.IsForbidden(@"E:\proj", RemoteDriveKind.Fixed, Context, root =>
        {
            asked.Add(root);
            return root;
        }, out _).Should().BeFalse();

        asked.Should().NotBeEmpty();
        asked.Where(root => !RemoteFolderRules.TryNormalize(root, out var n) || n != root).Should().BeEmpty("UNC 與正規化不了的不交給呼叫端解析");
        asked.Should().Contain([DataRoot, @"C:\Users\Amy\.ssh", @"D:\MyriagentData\conversations\c1\workspace", @"D:\MyriagentData\conversations\c1"]);
    }

    // ───────────────────────────── LinkTargetPath ─────────────────────────────

    [Theory]
    [InlineData(@"C:\a\link", @"C:\target", @"C:\target")]
    [InlineData(@"C:\a\link", @"d:\x\y\", @"D:\x\y")]
    [InlineData(@"C:\a\link", @"C:/fwd/slash", @"C:\fwd\slash")]
    [InlineData(@"C:\a\b\link", @"..\x", @"C:\a\x")]
    [InlineData(@"C:\a\link", @"sub\x", @"C:\a\sub\x")]
    [InlineData(@"C:\a\link", @"..\..\..\x", @"C:\x")]
    [InlineData(@"C:\a\link", @"\root\x", @"C:\root\x")]
    [InlineData(@"C:\a\link", @"PROGRA~1\x", @"C:\a\PROGRA~1\x")]
    public void LinkTargetPath_接著要走的完整路徑(string link, string target, string expected)
        => RemoteFolderFinalPathRules.LinkTargetPath(link, target).Should().Be(expected);

    [Theory]
    [InlineData(@"\\srv\share")]
    [InlineData(@"//srv/share")]
    [InlineData(@"\\?\C:\x")]
    [InlineData(@"\\?\UNC\srv\share")]
    [InlineData(@"\\.\C:\x")]
    [InlineData(@"\??\C:\x")]
    [InlineData(@"\\?\Volume{0b1f0c1e-0000-0000-0000-100000000000}\")]
    // .NET 的 LinkTarget 對掛載點回的是拿掉 \??\ 的這種（合併前審查 PROD-2）：不能當成相對路徑接在連結的資料夾後面
    [InlineData(@"Volume{0b1f0c1e-0000-0000-0000-100000000000}\")]
    [InlineData(@"Volume{0b1f0c1e-0000-0000-0000-100000000000}")]
    [InlineData(@"Volume{0B1F0C1E-0000-0000-0000-100000000000}\sub\x")]
    [InlineData(@"\??\Volume{0b1f0c1e-0000-0000-0000-100000000000}\")]
    [InlineData(@"D:rel")]
    [InlineData(@"C:")]
    [InlineData(@"C:\x:ads")]
    [InlineData(@"C:\a\CON")]
    [InlineData(@"C:\a\b.")]
    [InlineData("")]
    [InlineData(null)]
    public void LinkTargetPath_網路裝置與不合格的目標_不跟進(string? target)
        => RemoteFolderFinalPathRules.LinkTargetPath(@"C:\a\link", target).Should().BeNull(target);

    // ───────────────────────────── 磁碟區掛載點（合併前審查 PROD-2） ─────────────────────────────

    [Theory]
    [InlineData(@"Volume{0b1f0c1e-0000-0000-0000-100000000000}\", "")]
    [InlineData(@"Volume{0b1f0c1e-0000-0000-0000-100000000000}", "")]
    [InlineData(@"Volume{0b1f0c1e-0000-0000-0000-100000000000}\Users\x", @"Users\x")]
    [InlineData(@"\\?\Volume{0b1f0c1e-0000-0000-0000-100000000000}\a", "a")]
    [InlineData(@"\??\Volume{0b1f0c1e-0000-0000-0000-100000000000}\", "")]
    public void TryParseVolumeTarget_掛載點的目標_磁碟區名稱與剩下的路徑(string target, string rest)
    {
        RemoteFolderFinalPathRules.TryParseVolumeTarget(target, out var volume, out var after).Should().BeTrue(target);
        volume.Should().Be(@"\\?\Volume{0b1f0c1e-0000-0000-0000-100000000000}\", "GetVolumePathNamesForVolumeNameW 收的寫法");
        after.Should().Be(rest);
    }

    [Theory]
    [InlineData(@"C:\target")]
    [InlineData(@"sub\Volume{0b1f0c1e-0000-0000-0000-100000000000}")]
    [InlineData(@"Volume{0b1f0c1e-0000-0000-0000-10000000000}\")]
    [InlineData(@"Volume{x}\")]
    [InlineData(@"Volumes\x")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseVolumeTarget_不是磁碟區GUID路徑(string? target)
        => RemoteFolderFinalPathRules.TryParseVolumeTarget(target, out _, out _).Should().BeFalse(target);

    [Theory]
    [InlineData(@"C:\", "", @"C:\")]
    [InlineData(@"e:\", @"Users\x", @"E:\Users\x")]
    [InlineData(@"D:\", @"a\..\b", @"D:\b")]
    public void VolumeTargetPath_換成磁碟代號之後照指到別的磁碟的junction走(string driveRoot, string rest, string expected)
        => RemoteFolderFinalPathRules.VolumeTargetPath(@"C:\tmp\mnt", driveRoot, rest).Should().Be(expected);

    [Theory]
    [InlineData(null, "")]
    [InlineData(@"C:", "")]
    [InlineData(@"C:\mnt\", "")]
    [InlineData(@"\\?\Volume{0b1f0c1e-0000-0000-0000-100000000000}\", "")]
    [InlineData(@"C:\", @"x\CON")]
    [InlineData(@"C:\", @"x:ads")]
    [InlineData(@"C:\", @"Volume{0b1f0c1e-0000-0000-0000-100000000000}")]
    public void VolumeTargetPath_沒有磁碟代號或剩下的路徑不合格_不跟進(string? driveRoot, string rest)
        => RemoteFolderFinalPathRules.VolumeTargetPath(@"C:\tmp\mnt", driveRoot, rest).Should().BeNull();
}
