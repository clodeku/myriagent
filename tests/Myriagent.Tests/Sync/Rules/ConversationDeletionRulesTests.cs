using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Rules;

/// <summary>「已知、但中樞清單裡沒有」的對話要資料夾裡什麼都沒有、也沒有讀取錯誤才算刪除（WP4.3 計畫 D12）</summary>
public sealed class ConversationDeletionRulesTests
{
    [Fact]
    public void 四個都不存在才刪()
    {
        for (var mask = 0; mask < 16; mask++)
        {
            var probe = new FolderProbe((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0, (mask & 8) != 0);
            ConversationDeletionRules.ConfirmedDeleted(probe).Should().Be(mask == 0, probe.ToString());
        }
    }

    [Fact]
    public void 只有corrupt備份_不刪()
    {
        ConversationDeletionRules.ConfirmedDeleted(new FolderProbe(false, false, true, false)).Should().BeFalse("損毀而改名的對話：使用者還救得回來");
    }

    [Fact]
    public void 讀取錯誤_不刪()
    {
        ConversationDeletionRules.ConfirmedDeleted(new FolderProbe(false, false, false, true)).Should().BeFalse("被占用或沒有權限：不知道裡面有什麼");
    }
}
