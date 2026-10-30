using Myriagent.Agents;
using Myriagent.Core;

namespace Myriagent.Hub;

/// <summary>
/// 一個對話的存檔快取（增量存檔）：上次取的存檔快照，以及上次寫進 items.json 的樣子（<see cref="ItemsJsonLayout"/>）。
/// 取快照時沒變的項目（<see cref="AgentChatItem.SameForSave"/>）沿用上次那份快照物件、不再複製；寫檔時（<see cref="ItemsJsonFile"/>）
/// 沿用的快照物件直接從上一份檔案複製位元組、不再序列化——長對話裡只有最後幾項在變，每次存檔只做變了的那幾項。
/// <see cref="Take"/> 只在 UI 執行緒呼叫；<see cref="Layout"/> 由寫檔的那一方（背景存檔的執行緒或 UI 執行緒）鎖著用。
/// 快照建立之後就不再改（只會被序列化），所以背景執行緒寫的時候 UI 執行緒可以照常取下一份
/// </summary>
internal sealed class ItemsSaveCache
{
    /// <summary>對話停下、這麼久沒存檔就丟掉快取（中樞的維護）：只有執行中與剛停下的對話留著，記憶體不會隨著開過的對話一直變多</summary>
    public static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(10);

    private List<AgentChatItem> previous = new();

    public ItemsJsonLayout Layout { get; } = new();

    /// <summary>上次取快照的時間</summary>
    public DateTime LastUsed { get; private set; } = DateTime.Now;

    /// <summary>
    /// 取這一次的存檔快照：和上次的快照存檔內容相同的項目沿用那份物件，其他的 <see cref="AgentChatItem.CloneForSave"/>。
    /// 先照位置找上次的快照（長對話幾乎都是在尾端加項目，位置對得上就不必查表），id 對不上（中間插入、刪除）才以 id 查；
    /// 沿用一律要 <see cref="AgentChatItem.SameForSave"/>，所以同一份快照出現兩次（同一個 id 出現兩次，不該發生）寫出來照樣正確
    /// </summary>
    public List<AgentChatItem> Take(IReadOnlyList<AgentChatItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var result = new List<AgentChatItem>(items.Count);
        Dictionary<string, AgentChatItem>? byId = null;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item is null)
            {
                result.Add(null!);
                continue;
            }
            var candidate = i < previous.Count && previous[i] is { } atSame && atSame.Id == item.Id ? atSame : null;
            if (candidate is null && item.Id is not null)
            {
                byId ??= IndexById(previous);
                byId.TryGetValue(item.Id, out candidate);
            }
            result.Add(candidate is not null && item.SameForSave(candidate) ? candidate : item.CloneForSave());
        }
        previous = result;
        LastUsed = DateTime.Now;
        return result;
    }

    /// <summary>上次的快照以 id 查（同一個 id 有兩個時取前面那個）</summary>
    private static Dictionary<string, AgentChatItem> IndexById(List<AgentChatItem> snapshots)
    {
        var map = new Dictionary<string, AgentChatItem>(snapshots.Count, StringComparer.Ordinal);
        foreach (var s in snapshots)
        {
            if (s?.Id is { } id)
                map.TryAdd(id, s);
        }
        return map;
    }

    /// <summary>維護時要不要丟掉（停下、而且 <see cref="IdleLimit"/> 沒存檔）</summary>
    public static bool ShouldDrop(bool running, DateTime lastUsed, DateTime now) => !running && now - lastUsed >= IdleLimit;
}
