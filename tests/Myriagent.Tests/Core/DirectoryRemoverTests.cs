using System.Diagnostics;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Core;

/// <summary>刪除整個資料夾：唯讀檔與唯讀資料夾（Git 物件檔）也刪得掉；連結本身刪掉，但不進去改目標的屬性</summary>
public sealed class DirectoryRemoverTests : IDisposable
{
    private readonly TempDirectory temp = new("remover");

    public void Dispose()
    {
        DirectoryRemover.ClearReadOnly(temp.Root);
        temp.Dispose();
    }

    /// <summary>像 git init／commit 之後的工作區：.git\objects 底下的物件檔是唯讀，另有一個唯讀資料夾</summary>
    private string CreateGitLikeTree(string relative)
    {
        var root = temp.CreateDirectory(relative);
        var objectFile = temp.WriteFile(Path.Combine(relative, "workspace", ".git", "objects", "ab", "cdef1234"), "blob");
        File.SetAttributes(objectFile, FileAttributes.ReadOnly);
        new DirectoryInfo(Path.Combine(root, "workspace", ".git")).Attributes |= FileAttributes.Hidden;
        var pack = temp.WriteFile(Path.Combine(relative, "workspace", ".git", "objects", "pack", "pack-1234.pack"), "pack");
        File.SetAttributes(pack, FileAttributes.ReadOnly | FileAttributes.Hidden);
        var readOnlyDir = temp.CreateDirectory(Path.Combine(relative, "唯讀資料夾"));
        temp.WriteFile(Path.Combine(relative, "唯讀資料夾", "a.txt"));
        new DirectoryInfo(readOnlyDir).Attributes |= FileAttributes.ReadOnly;
        return root;
    }

    [Fact]
    public void 唯讀檔與唯讀資料夾_清掉屬性後刪乾淨()
    {
        var root = CreateGitLikeTree("conv");
        FluentActions.Invoking(() => Directory.Delete(root, recursive: true)).Should().Throw<Exception>("前提：Directory.Delete 不會清唯讀屬性");

        DirectoryRemover.TryDelete(root, retryDelayMs: 10).Should().BeTrue();

        Directory.Exists(root).Should().BeFalse();
    }

    [Fact]
    public void 不存在的資料夾算刪乾淨()
        => DirectoryRemover.TryDelete(temp.Combine("沒有這個資料夾")).Should().BeTrue();

    [Fact]
    public void 檔案被其他程式獨占時回False_其他檔案照樣刪掉()
    {
        var root = temp.CreateDirectory("locked");
        var locked = temp.WriteFile(@"locked\被占用.txt");
        var other = temp.WriteFile(@"locked\sub\其他.txt");
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            DirectoryRemover.TryDelete(root, attempts: 2, retryDelayMs: 10).Should().BeFalse();
            File.Exists(other).Should().BeFalse("刪得掉的照樣刪");
        }

        DirectoryRemover.TryDelete(root, retryDelayMs: 10).Should().BeTrue("放開之後就刪得掉");
    }

    [Fact]
    public void 資料夾裡的Junction_連結本身刪掉_目標的檔案與唯讀屬性不動()
    {
        var target = temp.CreateDirectory("外部資料夾");
        var keep = temp.WriteFile(@"外部資料夾\保留.txt");
        File.SetAttributes(keep, FileAttributes.ReadOnly);
        var root = CreateGitLikeTree("conv-with-link");
        var link = Path.Combine(root, "workspace", "node_modules");
        CreateJunction(link, target);
        File.Exists(Path.Combine(link, "保留.txt")).Should().BeTrue("前提：連結建立成功");

        DirectoryRemover.TryDelete(root, retryDelayMs: 10).Should().BeTrue();

        Directory.Exists(root).Should().BeFalse();
        File.Exists(keep).Should().BeTrue("連結的目標不是這個資料夾的東西，不能刪");
        File.GetAttributes(keep).Should().HaveFlag(FileAttributes.ReadOnly, "也不能改目標的屬性");
    }

    [Fact]
    public void ConversationStore刪除對話_工作區有Git唯讀物件檔也刪乾淨()
    {
        var store = new ConversationStore(temp.Combine("conversations"));
        var meta = new Myriagent.Agents.AgentConversation { Title = "有 git repo 的對話" };
        store.SaveConversation(meta);
        var objectFile = temp.WriteFile(Path.Combine("conversations", meta.Id, "workspace", ".git", "objects", "ab", "cdef"), "blob");
        File.SetAttributes(objectFile, FileAttributes.ReadOnly);

        store.DeleteConversation(meta.Id).Should().BeTrue();

        Directory.Exists(store.GetConversationDirectory(meta.Id)).Should().BeFalse("整個工作區一起刪，不留在 %LOCALAPPDATA%");
    }

    private static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit(10_000);
        process.ExitCode.Should().Be(0, "mklink /J 建立 junction 不需要系統管理員權限");
    }
}
