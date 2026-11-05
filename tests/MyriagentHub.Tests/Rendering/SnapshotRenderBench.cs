using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using MyriagentHub.Data;
using MyriagentHub.Rendering;
using MyriagentHub.Sync;
using Xunit;
using Xunit.Abstractions;

namespace MyriagentHub.Tests.Rendering;

/// <summary>
/// 量瀏覽器開啟對話時伺服器渲染第一頁（最後 80 筆）的成本：以 sync.db 的複本（每日備份），只讀。
/// 只在設了 <c>MYRIAGENT_SNAPSHOT_BENCH=&lt;sync.db 的路徑&gt;</c> 時跑（2026-11-05 調查手機版載入對話變慢時寫的；查詢的部分見 tools/hub-probe/snapshot_cost.py）。
/// 備份不含 blob：參照 blob 的項目渲染成佔位，量到的是下限
/// </summary>
public sealed class SnapshotRenderBench(ITestOutputHelper output)
{
    [Fact]
    public void RenderFirstPages()
    {
        var path = Environment.GetEnvironmentVariable("MYRIAGENT_SNAPSHOT_BENCH");
        if (string.IsNullOrEmpty(path))
            return;
        var renderer = new HubItemRenderer(new HubMediaUrls(), new FakeBlobStore(new Dictionary<string, byte[]>()), NullLogger<HubItemRenderer>.Instance);
        using var connection = new SqliteConnection(SqliteSetup.ConnectionString(path, readOnly: true));
        connection.Open();
        var conversations = new List<(string Id, string? Owner, string Meta)>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT conversation_id, owner_device_id, meta_json FROM conversations WHERE deleted_at IS NULL ORDER BY conversation_id";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                conversations.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2)));
        }
        // 匯出最大的幾頁（瀏覽器的 items 陣列，舊到新）給 tools/ui-probe/open-cost.mjs：內容是真的對話，只寫到指定的暫存資料夾、不進 repo
        var dump = Environment.GetEnvironmentVariable("MYRIAGENT_SNAPSHOT_DUMP");
        var pages = new Dictionary<string, List<string>>();
        var results = new List<(string Id, double Ms, long Bytes, int Items, double SlowestMs, string SlowestKind)>();
        foreach (var (id, owner, meta) in conversations)
        {
            var rows = new List<(string ItemId, long Ord, string Kind, string Body, string Enc, long At)>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT i.item_id, i.ord, i.kind, i.body, i.enc, IFNULL(e.created_at, 0) FROM items i LEFT JOIN events e ON e.seq = i.last_seq "
                    + "WHERE i.conversation_id = $c AND i.removed = 0 ORDER BY i.ord DESC, i.item_id DESC LIMIT 80";
                cmd.Parameters.AddWithValue("$c", id);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    rows.Add((r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5)));
            }
            var total = Stopwatch.StartNew();
            long bytes = 0;
            var slowest = 0.0;
            var slowestKind = "";
            var rendered = new List<string>();
            foreach (var row in Enumerable.Reverse(rows))
            {
                var one = Stopwatch.StartNew();
                var json = renderer.Render(new HubItemSource(id, owner ?? string.Empty, null, row.ItemId, row.Ord, row.Body, row.Enc, row.At), 1024 * 1024);
                var ms = one.Elapsed.TotalMilliseconds;
                if (ms > slowest)
                {
                    slowest = ms;
                    slowestKind = row.Kind;
                }
                bytes += Encoding.UTF8.GetByteCount(json);
                rendered.Add(json);
            }
            results.Add((id, total.Elapsed.TotalMilliseconds, bytes, rows.Count, slowest, slowestKind));
            if (dump is not null)
                pages[id] = rendered;
        }
        var ordered = results.Select(r => r.Ms).OrderBy(x => x).ToList();
        var sizes = results.Select(r => r.Bytes).OrderBy(x => x).ToList();
        output.WriteLine($"conversations {results.Count}；渲染一頁 ms：中位數 {ordered[ordered.Count / 2]:F1}、p95 {ordered[(int)(ordered.Count * 0.95)]:F1}、最大 {ordered[^1]:F1}；"
            + $"合計 {ordered.Sum():F0}");
        output.WriteLine($"輸出 KB：中位數 {sizes[sizes.Count / 2] / 1024}、p95 {sizes[(int)(sizes.Count * 0.95)] / 1024}、最大 {sizes[^1] / 1024}");
        foreach (var r in results.OrderByDescending(r => r.Ms).Take(15))
            output.WriteLine($"  {r.Id} {r.Ms,8:F1} ms {r.Bytes / 1024,6} KB {r.Items,3} 筆  最慢一筆 {r.SlowestKind} {r.SlowestMs:F1} ms");
        foreach (var r in results.OrderByDescending(r => r.Bytes).Take(10))
            output.WriteLine($"  大：{r.Id} {r.Bytes / 1024,6} KB {r.Ms,8:F1} ms");
        if (dump is not null)
        {
            Directory.CreateDirectory(dump);
            foreach (var r in results.OrderByDescending(r => r.Bytes).Take(8))
                File.WriteAllText(Path.Combine(dump, r.Id + ".json"), "[" + string.Join(",", pages[r.Id]) + "]", new UTF8Encoding(false));
        }
    }
}
