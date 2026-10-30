namespace MyriagentHub.ProbeClient.Tests;

/// <summary>
/// SqliteBench 的資料根守門（WP4.9 合併前審查）：資料夾或它的上層是 MyriagentHub 的資料根（有 <c>db\hub.db</c> 或 <c>status\service.lock</c>）時，
/// B1～B6 一律不跑、結束碼 2、不建任何檔——營運 16.1 的 P2a 在 Hub 主機上跑 B3，資料夾打錯成正式資料根時不能寫進去（之前只有 B6 查）
/// </summary>
public sealed class SqliteBenchGuardTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sqlitebench-guard-" + Guid.NewGuid().ToString("N"));

    public SqliteBenchGuardTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("db", "hub.db", "")]
    [InlineData("db", "hub.db", @"bench\b3")]
    [InlineData("status", "service.lock", "")]
    [InlineData("status", "service.lock", "sub")]
    public void 資料根或它底下的資料夾_B3不跑_結束碼2_不建任何檔(string markerDir, string markerFile, string under)
    {
        Directory.CreateDirectory(Path.Combine(root, markerDir));
        File.WriteAllText(Path.Combine(root, markerDir, markerFile), "");
        var dir = under.Length == 0 ? root : Path.Combine(root, under);
        var before = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order().ToList();

        var code = SqliteBench.Program.Main([dir, "1", "--only", "B3"]);

        code.Should().Be(2);
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order().Should().Equal(before, "沒有建 b2-*.db、也沒有建資料夾");
    }

    [Fact]
    public void 資料根以外的資料夾_照樣跑()
    {
        // 正向對照：同樣的參數在資料根以外照跑（B1 最快：3×3000 筆）
        var dir = Path.Combine(root, "plain");

        var code = SqliteBench.Program.Main([dir, "1", "--only", "B1"]);

        code.Should().Be(0);
        Directory.EnumerateFiles(dir, "b1-*.db").Should().NotBeEmpty();
    }
}
