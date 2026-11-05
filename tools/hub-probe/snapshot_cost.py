"""量瀏覽器開啟對話（open）時伺服器讀快照的成本：用 sync.db 的複本（每日備份），只讀。

對每個沒刪除的對話照 ConversationReader.ReadSnapshot 的查詢讀最後一頁（80＋1 筆），記：
查詢耗時、這一頁 body 的位元組（enc 解開前）、最大的一筆。輸出最慢、最大的對話與分布。

用法：python -X utf8 tools/hub-probe/snapshot_cost.py <sync.db 的複本> [--top 15]
（2026-11-05 調查手機版載入對話變慢時寫的）
"""
import argparse
import sqlite3
import statistics
import time

PAGE = 80

ITEM_SQL = (
    "SELECT i.item_id, i.ord, i.last_seq, i.kind, i.status, i.final, length(i.body), i.enc, IFNULL(e.created_at, 0) "
    "FROM items i LEFT JOIN events e ON e.seq = i.last_seq WHERE i.conversation_id = ? AND i.removed = 0 "
    "ORDER BY i.ord DESC, i.item_id DESC LIMIT ?"
)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("db")
    ap.add_argument("--top", type=int, default=15)
    a = ap.parse_args()
    con = sqlite3.connect(f"file:{a.db}?mode=ro", uri=True)
    print("indexes on items:")
    for row in con.execute("SELECT name, sql FROM sqlite_master WHERE type='index' AND tbl_name='items'"):
        print("  ", row)
    print("plan:", con.execute("EXPLAIN QUERY PLAN " + ITEM_SQL, ("x", PAGE + 1)).fetchall())
    convs = [r[0] for r in con.execute("SELECT conversation_id FROM conversations WHERE deleted_at IS NULL ORDER BY conversation_id")]
    counts = dict(con.execute("SELECT conversation_id, count(*) FROM items WHERE removed = 0 GROUP BY conversation_id"))
    rows = []
    for c in convs:
        t0 = time.perf_counter()
        page = con.execute(ITEM_SQL, (c, PAGE + 1)).fetchall()
        ms = (time.perf_counter() - t0) * 1000
        page = page[:PAGE]
        size = sum(p[6] or 0 for p in page)
        biggest = max(page, key=lambda p: p[6] or 0) if page else None
        rows.append((c, ms, size, len(page), counts.get(c, 0), biggest))
    print(f"conversations: {len(rows)}")
    ms = [r[1] for r in rows]
    sizes = [r[2] for r in rows]
    q = statistics.quantiles(ms, n=100)
    print(f"query ms: median {statistics.median(ms):.2f}  p95 {q[94]:.2f}  max {max(ms):.2f}")
    qs = statistics.quantiles(sizes, n=100)
    print(f"page bytes: median {statistics.median(sizes)/1024:.0f} KB  p95 {qs[94]/1024:.0f} KB  max {max(sizes)/1024:.0f} KB")
    encs = dict(con.execute("SELECT enc, count(*) FROM items GROUP BY enc"))
    print("enc:", encs)
    print(f"\nlargest pages (top {a.top}):")
    for c, t, size, n, total, big in sorted(rows, key=lambda r: -r[2])[: a.top]:
        print(f"  {c}  {size/1024:8.0f} KB  {n:3d}/{total:6d} items  {t:6.1f} ms  biggest {big[3]}/{(big[6] or 0)/1024:.0f} KB enc={big[7]}")
    print(f"\nslowest queries (top {a.top}):")
    for c, t, size, n, total, big in sorted(rows, key=lambda r: -r[1])[: a.top]:
        print(f"  {c}  {t:6.1f} ms  {size/1024:8.0f} KB  {total:6d} items")


if __name__ == "__main__":
    main()
