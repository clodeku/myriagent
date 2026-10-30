using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>背景活動日誌的假寫入端：記在記憶體（中樞在 UI 執行緒寫、測試在別的執行緒讀，加鎖）</summary>
internal sealed class FakeJournal : IBackgroundJournal
{
    private readonly List<JObject> entries = [];

    public void Write(JObject entry)
    {
        lock (entries)
            entries.Add(entry);
    }

    public List<JObject> All
    {
        get
        {
            lock (entries)
                return entries.ToList();
        }
    }

    /// <summary>某一種事件，依寫入的順序</summary>
    public List<JObject> Of(string ev) => All.Where(e => e.Value<string>("ev") == ev).ToList();

    /// <summary>事件名稱的順序（只列 <paramref name="prefixes"/> 開頭的）</summary>
    public List<string> Events(params string[] prefixes)
        => All.Select(e => e.Value<string>("ev")!).Where(ev => prefixes.Length == 0 || prefixes.Any(p => ev.StartsWith(p, StringComparison.Ordinal))).ToList();
}
