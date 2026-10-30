using Myriagent.Tests.TestSupport;
using Myriagent.Update;

namespace Myriagent.Tests.Update;

public sealed class JunctionTests
{
    [Fact]
    public void 建立後_透過junction讀得到目標的檔案_讀得回目標()
    {
        using var temp = new TempDirectory("junction");
        var v1 = temp.CreateDirectory(Path.Combine("versions", "v1"));
        File.WriteAllText(Path.Combine(v1, "a.txt"), "one");
        var link = temp.Combine("current");

        Junction.Create(link, v1);

        Junction.IsJunction(link).Should().BeTrue();
        Junction.GetTarget(link).Should().BeEquivalentTo(v1);
        File.ReadAllText(Path.Combine(link, "a.txt")).Should().Be("one");
    }

    [Fact]
    public void 原地改指向_不必先刪除_開著舊目標的檔案也不受影響()
    {
        using var temp = new TempDirectory("junction");
        var v1 = temp.CreateDirectory(Path.Combine("versions", "v1"));
        var v2 = temp.CreateDirectory(Path.Combine("versions", "v2"));
        File.WriteAllText(Path.Combine(v1, "a.txt"), "one");
        File.WriteAllText(Path.Combine(v2, "a.txt"), "two");
        var link = temp.Combine("current");
        Junction.Create(link, v1);

        using (var held = File.OpenRead(Path.Combine(link, "a.txt")))
        {
            Junction.Retarget(link, v2);
            new StreamReader(held).ReadToEnd().Should().Be("one", "已開啟的檔案在開啟時就解析到 v1");
        }

        Junction.GetTarget(link).Should().BeEquivalentTo(v2);
        File.ReadAllText(Path.Combine(link, "a.txt")).Should().Be("two");
    }

    [Fact]
    public void 一般資料夾不是junction_改指向丟例外()
    {
        using var temp = new TempDirectory("junction");
        var plain = temp.CreateDirectory("plain");
        Junction.IsJunction(plain).Should().BeFalse();
        Junction.GetTarget(plain).Should().BeNull();
        var act = () => Junction.Retarget(plain, temp.CreateDirectory("v2"));
        act.Should().Throw<IOException>();
    }

    [Fact]
    public void 刪除junction_只刪連結不刪目標()
    {
        using var temp = new TempDirectory("junction");
        var v1 = temp.CreateDirectory(Path.Combine("versions", "v1"));
        File.WriteAllText(Path.Combine(v1, "a.txt"), "one");
        var link = temp.Combine("current");
        Junction.Create(link, v1);

        Directory.Delete(link);

        Directory.Exists(link).Should().BeFalse();
        File.Exists(Path.Combine(v1, "a.txt")).Should().BeTrue();
    }
}
