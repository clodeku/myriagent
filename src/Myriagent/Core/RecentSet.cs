namespace Myriagent.Core;

/// <summary>
/// 有上限的字串集合：超過上限時移除最久沒用到的一筆（加入與查詢都算用到，正在使用的不會被擠掉）。
/// 給程序存活期間一直登記的白名單用（例如媒體端點允許的路徑），常駐很久也不會無限長大。只在單一執行緒使用，不加鎖。
/// </summary>
internal sealed class RecentSet
{
    private readonly int capacity;
    private readonly Dictionary<string, LinkedListNode<string>> nodes;
    private readonly LinkedList<string> order = new();

    public RecentSet(int capacity, IEqualityComparer<string>? comparer = null)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "上限必須大於 0");
        this.capacity = capacity;
        nodes = new Dictionary<string, LinkedListNode<string>>(comparer ?? StringComparer.Ordinal);
    }

    public int Count => nodes.Count;

    public void Add(string value)
    {
        if (Touch(value))
            return;
        nodes[value] = order.AddLast(value);
        while (nodes.Count > capacity)
        {
            var oldest = order.First!;
            order.RemoveFirst();
            nodes.Remove(oldest.Value);
        }
    }

    /// <summary>在集合裡嗎（查詢也算用到，排到最後才會被擠掉）</summary>
    public bool Contains(string value) => Touch(value);

    public bool Remove(string value)
    {
        if (!nodes.Remove(value, out var node))
            return false;
        order.Remove(node);
        return true;
    }

    private bool Touch(string value)
    {
        if (!nodes.TryGetValue(value, out var node))
            return false;
        order.Remove(node);
        order.AddLast(node);
        return true;
    }
}
