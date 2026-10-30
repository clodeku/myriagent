# MyriagentHub 同步資料庫（<資料根>\db\sync.db）的只讀檢查（WP4.9 步 7；給 FF9：換版前後某台裝置的 deviceSeq 沒有間隙、被擋下的是哪幾種 payload）。
# 只用標準庫 sqlite3，一律以 file:<路徑>?mode=ro 開（不寫入、不建檔、不做檢查點）；伺服器執行中也能讀（WAL）。
#
# 用法（一律 python -X utf8）：
#   python -X utf8 sqlite_ro.py <sync.db> device <author>     某台裝置的 events 與 rejected_events 的 deviceSeq、間隙、被擋下的種類與原因
#   python -X utf8 sqlite_ro.py <sync.db> rejected [<author>]  rejected_events 的 (kind, reason) 計數
#   python -X utf8 sqlite_ro.py <sync.db> conversation <id>    一個對話的列（政策、項目數、刪除）、items 列數、media_refs、項目欄位 blob（步 16 的 FF5、FF6）
#   python -X utf8 sqlite_ro.py <sync.db> rejected-rows <author> [<afterDeviceSeq>]
#                                                             某台裝置被擋下的每一列：deviceSeq、種類、原因、對話、payload 是不是 {}（步 18 的 FF9：
#                                                             舊伺服器只記下 {} 或 K1 凍結的列）；不回 payload 本身
#   python -X utf8 sqlite_ro.py <sync.db> command <browserCommandId>
#                                                             瀏覽器 commandId 對到的指令列：狀態、指令、durable、目標（步 18 的 FF9：強制結束後非 durable 的變 unknown）
#   python -X utf8 sqlite_ro.py <sync.db> leases               對話的 lease_state 計數與 recovering 的對話 id（步 18 的 FF9：還原之後全部 recovering、之後解除）
#   python -X utf8 sqlite_ro.py <sync.db> conversations        還沒刪除的對話：id、擁有者、政策、標題、項目數（步 18 的 FF9：升級回來之後網頁的清單要等於它）
#   python -X utf8 sqlite_ro.py <桌面的 sync.db> publish-blocks
#                                                             桌面 sync.db 的 publish_meta 裡被封鎖的對話：原因、政策（步 18 的 FF9：PRIV-R3-3 的 not_owner 封鎖換版不解除）
#   python -X utf8 sqlite_ro.py --self-test                    在暫存資料夾建一個 WAL 資料庫、關掉之後以只讀開，確認讀得到、檔案沒變
# 輸出一行 JSON（UTF-8）；錯誤時結束碼 2、stderr 一行說明。
import hashlib
import json
import os
import pathlib
import sqlite3
import sys
import tempfile
import urllib.parse


def connect_ro(db_path):
    full = pathlib.Path(db_path).resolve()
    if not full.is_file():
        raise FileNotFoundError(f"找不到資料庫：{full}")
    uri = "file:" + urllib.parse.quote(full.as_posix(), safe="/:") + "?mode=ro"
    return sqlite3.connect(uri, uri=True)


def gaps(seqs):
    """從 1 開始的連續序號裡缺的（序號已排序、不重複）"""
    out = []
    expected = 1
    for s in seqs:
        if s > expected:
            out.extend(range(expected, s))
        expected = s + 1
    return out


def device_report(conn, author):
    events = [r[0] for r in conn.execute(
        "SELECT device_seq FROM events WHERE author = ? AND device_seq IS NOT NULL ORDER BY device_seq", (author,))]
    rejected = [{"deviceSeq": r[0], "kind": r[1], "reason": r[2]} for r in conn.execute(
        "SELECT device_seq, kind, reason FROM rejected_events WHERE author = ? ORDER BY device_seq", (author,))]
    all_seqs = sorted(set(events) | {r["deviceSeq"] for r in rejected})
    return {
        "author": author,
        "events": len(events),
        "rejected": rejected,
        "maxDeviceSeq": all_seqs[-1] if all_seqs else 0,
        "gaps": gaps(all_seqs),
        "duplicates": len(events) + len(rejected) - len(all_seqs),
    }


def rejected_report(conn, author=None):
    if author is None:
        rows = conn.execute("SELECT kind, reason, COUNT(*) FROM rejected_events GROUP BY kind, reason ORDER BY kind, reason")
    else:
        rows = conn.execute(
            "SELECT kind, reason, COUNT(*) FROM rejected_events WHERE author = ? GROUP BY kind, reason ORDER BY kind, reason", (author,))
    return {"author": author, "kinds": [{"kind": k, "reason": r, "count": c} for k, r, c in rows]}


def conversation_report(conn, conversation_id):
    """一個對話在伺服器上的樣子（WP4.9 步 16 的 FF5、FF6）：列（政策、項目數、刪除時間…）、items 的列數、
    conversation_runtime 有沒有列、media_refs（依用途、有沒有縮圖 blob）、項目欄位 blob（blob_refs 的 item、ref_id＝{對話}:{項目}）"""
    row = conn.execute(
        "SELECT sync_policy, item_count, max_ord, title, deleted_at, owner_device_id FROM conversations WHERE conversation_id = ?",
        (conversation_id,)).fetchone()
    items, removed = conn.execute(
        "SELECT COUNT(*), IFNULL(SUM(removed), 0) FROM items WHERE conversation_id = ?", (conversation_id,)).fetchone()
    runtime = conn.execute("SELECT COUNT(*) FROM conversation_runtime WHERE conversation_id = ?", (conversation_id,)).fetchone()[0]
    media = [{"purpose": p, "count": c, "thumbs": t} for p, c, t in conn.execute(
        "SELECT purpose, COUNT(*), IFNULL(SUM(thumb_blob IS NOT NULL), 0) FROM media_refs WHERE conversation_id = ? GROUP BY purpose ORDER BY purpose",
        (conversation_id,))]
    prefix = conversation_id + ":"
    blob_count, blob_bytes = conn.execute(
        "SELECT COUNT(*), IFNULL(SUM(b.size), 0) FROM blob_refs r JOIN blobs b ON b.sha256 = r.sha256 "
        "WHERE r.ref_kind = 'item' AND substr(r.ref_id, 1, ?) = ?", (len(prefix), prefix)).fetchone()
    return {
        "conversationId": conversation_id,
        "exists": row is not None,
        "policy": row[0] if row else None,
        "itemCount": row[1] if row else None,
        "maxOrd": row[2] if row else None,
        "title": row[3] if row else None,
        "deleted": row is not None and row[4] is not None,
        "owner": row[5] if row else None,
        "items": items,
        "removed": removed,
        "runtime": runtime,
        "media": media,
        "itemBlobs": {"count": blob_count, "bytes": blob_bytes},
    }


def is_empty_payload(enc, payload):
    """payload 是 {}（桌面在沒有 conv1 的連線、或中樞沒有那個對話時換上的空物件；只有 json 編碼會是它）"""
    return (enc or "json") == "json" and payload is not None and payload.strip() == "{}"


def rejected_rows_report(conn, author, after=0):
    """某台裝置被擋下的每一列（deviceSeq 大於 after 的；WP4.9 步 18 的 FF9）：不回 payload 本身，只回是不是 {} 與長度"""
    rows = [{"deviceSeq": s, "kind": k, "reason": r, "conversationId": c, "enc": e, "empty": is_empty_payload(e, p), "bytes": len(p or "")}
            for s, k, r, c, e, p in conn.execute(
                "SELECT device_seq, kind, reason, conversation_id, enc, payload FROM rejected_events WHERE author = ? AND device_seq > ? ORDER BY device_seq",
                (author, after))]
    return {"author": author, "after": after, "count": len(rows), "rows": rows}


def command_report(conn, browser_command_id):
    """瀏覽器的 commandId 對到的指令列（同一個 bcid 可能有好幾列：所有電腦登出一台一列）"""
    rows = [{"commandId": i, "cmd": c, "durable": bool(d), "state": s, "target": t} for i, c, d, s, t in conn.execute(
        "SELECT command_id, cmd, durable, state, target_device_id FROM commands WHERE browser_command_id = ? ORDER BY command_id",
        (browser_command_id,))]
    return {"browserCommandId": browser_command_id, "rows": rows}


def leases_report(conn):
    """還沒刪除的對話依 lease_state 計數，與 recovering 的對話 id（還原之後全部 recovering，擁有者的新 conv.created 解除）"""
    counts = {s: c for s, c in conn.execute(
        "SELECT lease_state, COUNT(*) FROM conversations WHERE deleted_at IS NULL GROUP BY lease_state ORDER BY lease_state")}
    recovering = [r[0] for r in conn.execute(
        "SELECT conversation_id FROM conversations WHERE deleted_at IS NULL AND lease_state = 'recovering' ORDER BY conversation_id")]
    return {"counts": counts, "recovering": recovering, "total": sum(counts.values())}


def conversations_report(conn):
    """還沒刪除的對話（id、擁有者、政策、標題、項目數；依 id 排）：網頁的清單要等於它（步 18 的 FF9：升級回來之後）"""
    rows = [{"conversationId": i, "owner": o, "policy": p, "title": t, "itemCount": n} for i, o, p, t, n in conn.execute(
        "SELECT conversation_id, owner_device_id, sync_policy, title, item_count FROM conversations WHERE deleted_at IS NULL ORDER BY conversation_id")]
    return {"count": len(rows), "conversations": rows}


def publish_blocks_report(conn):
    """桌面 sync.db 的 publish_meta 裡被封鎖的對話（原因、政策、created 寫了沒、確認了沒）"""
    rows = [{"conversationId": i, "blocked": b, "policy": p, "createdWritten": bool(w), "createdAcked": bool(a)} for i, b, p, w, a in conn.execute(
        "SELECT conversation_id, blocked, policy, created_written, created_acked FROM publish_meta WHERE blocked IS NOT NULL ORDER BY conversation_id")]
    return {"count": len(rows), "blocks": rows}


def sha256_of(path):
    return hashlib.sha256(pathlib.Path(path).read_bytes()).hexdigest()


def self_test():
    with tempfile.TemporaryDirectory(prefix="myriagent-sqlite-ro-") as tmp:
        db = os.path.join(tmp, "sync.db")
        w = sqlite3.connect(db)
        w.execute("PRAGMA journal_mode=WAL")
        w.execute("CREATE TABLE events(seq INTEGER PRIMARY KEY AUTOINCREMENT, author TEXT NOT NULL, device_seq INTEGER, kind TEXT NOT NULL)")
        w.execute("CREATE TABLE rejected_events(author TEXT NOT NULL, device_seq INTEGER NOT NULL, kind TEXT NOT NULL, reason TEXT NOT NULL, PRIMARY KEY(author, device_seq))")
        w.executemany("INSERT INTO events(author, device_seq, kind) VALUES (?, ?, ?)", [("d1", 1, "a"), ("d1", 2, "a"), ("d1", 5, "a"), ("d2", 1, "a")])
        w.execute("INSERT INTO rejected_events VALUES ('d1', 3, 'conversation.item', 'dlp')")
        # 步 16：對話、項目、媒體參照、項目欄位 blob（另一個對話的 blob 不算進來）
        w.execute("CREATE TABLE conversations(conversation_id TEXT PRIMARY KEY, sync_policy TEXT NOT NULL, item_count INTEGER NOT NULL, max_ord INTEGER NOT NULL, "
                  "title TEXT NOT NULL, deleted_at INTEGER, owner_device_id TEXT)")
        w.execute("CREATE TABLE items(conversation_id TEXT NOT NULL, item_id TEXT NOT NULL, removed INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(conversation_id, item_id))")
        w.execute("CREATE TABLE conversation_runtime(conversation_id TEXT PRIMARY KEY)")
        w.execute("CREATE TABLE media_refs(device_id TEXT NOT NULL, ref TEXT NOT NULL, conversation_id TEXT NOT NULL, purpose TEXT NOT NULL, thumb_blob TEXT, PRIMARY KEY(device_id, ref))")
        w.execute("CREATE TABLE blobs(sha256 TEXT PRIMARY KEY, size INTEGER NOT NULL)")
        w.execute("CREATE TABLE blob_refs(sha256 TEXT NOT NULL, ref_kind TEXT NOT NULL, ref_id TEXT NOT NULL, PRIMARY KEY(sha256, ref_kind, ref_id))")
        c1, c2 = "a" * 32, "b" * 32
        w.executemany("INSERT INTO conversations VALUES (?, ?, ?, ?, ?, ?, ?)",
                      [(c1, "metadata", 0, 7, "標題", None, "d1"), (c2, "full", 1, 1, "另一個", 99, "d2")])
        w.executemany("INSERT INTO items VALUES (?, ?, ?)", [(c2, "i1", 0)])
        w.executemany("INSERT INTO media_refs VALUES (?, ?, ?, ?, ?)", [("d1", "r1", c1, "media", "t1"), ("d1", "r2", c1, "media", None)])
        w.executemany("INSERT INTO blobs VALUES (?, ?)", [("s1", 300), ("s2", 50)])
        w.executemany("INSERT INTO blob_refs VALUES (?, ?, ?)", [("s1", "item", c1 + ":i9"), ("s2", "item", c2 + ":i1"), ("s2", "thumb", c1)])
        # 步 18：被擋下的每一列（另一台 d3：不動上面 d1 的計數）、指令、lease_state、桌面的 publish_meta
        for col in ("conversation_id TEXT", "enc TEXT", "payload TEXT"):
            w.execute(f"ALTER TABLE rejected_events ADD COLUMN {col}")
        w.executemany("INSERT INTO rejected_events VALUES (?, ?, ?, ?, ?, ?, ?)",
                      [("d3", 1, "conversation.item", "invalid", c1, "json", " {} "), ("d3", 2, "conversation.created", "invalid", c1, "json", '{"meta":{}}'),
                       ("d3", 3, "conversation.item", "invalid", c1, "gzip", "{}")])
        w.execute("CREATE TABLE commands(command_id TEXT PRIMARY KEY, browser_command_id TEXT, cmd TEXT NOT NULL, durable INTEGER NOT NULL, state TEXT NOT NULL, target_device_id TEXT NOT NULL)")
        w.executemany("INSERT INTO commands VALUES (?, ?, ?, ?, ?, ?)", [("k1", "b1", "rename", 0, "unknown", "d1"), ("k2", "b2", "logoutAll", 1, "pending", "d2")])
        w.execute("ALTER TABLE conversations ADD COLUMN lease_state TEXT NOT NULL DEFAULT 'owned'")
        w.execute("INSERT INTO conversations VALUES (?, 'full', 0, 0, 't', NULL, 'd1', 'recovering')", ("c" * 32,))
        w.execute("CREATE TABLE publish_meta(conversation_id TEXT PRIMARY KEY, created_written INTEGER NOT NULL DEFAULT 0, created_acked INTEGER NOT NULL DEFAULT 0, "
                  "policy TEXT, blocked TEXT)")
        w.executemany("INSERT INTO publish_meta VALUES (?, ?, ?, ?, ?)", [(c1, 1, 0, "localOnly", "not_owner"), (c2, 1, 1, "full", None)])
        w.commit()
        w.close()  # 最後一條連線關閉：檢查點、刪掉 -wal 與 -shm（伺服器停止後的樣子）
        before = sha256_of(db)
        with connect_ro(db) as conn:
            report = device_report(conn, "d1")
            kinds = rejected_report(conn, "d1")
            conv = conversation_report(conn, c1)
            gone = conversation_report(conn, "d" * 32)
            rows = rejected_rows_report(conn, "d3", 1)
            cmd = command_report(conn, "b1")
            leases = leases_report(conn)
            convs = conversations_report(conn)
            blocks = publish_blocks_report(conn)
            try:
                conn.execute("INSERT INTO events(author, device_seq, kind) VALUES ('x', 1, 'a')")
                writable = True
            except sqlite3.OperationalError:
                writable = False
        conn.close()
        after = sha256_of(db)
        ok = (report["events"] == 3 and report["gaps"] == [4] and report["maxDeviceSeq"] == 5 and report["duplicates"] == 0
              and report["rejected"] == [{"deviceSeq": 3, "kind": "conversation.item", "reason": "dlp"}]
              and kinds["kinds"] == [{"kind": "conversation.item", "reason": "dlp", "count": 1}]
              and conv["exists"] and conv["policy"] == "metadata" and conv["itemCount"] == 0 and conv["maxOrd"] == 7 and not conv["deleted"]
              and conv["items"] == 0 and conv["runtime"] == 0 and conv["media"] == [{"purpose": "media", "count": 2, "thumbs": 1}]
              and conv["itemBlobs"] == {"count": 1, "bytes": 300}
              and not gone["exists"] and gone["items"] == 0 and gone["itemBlobs"] == {"count": 0, "bytes": 0}
              and [(r["deviceSeq"], r["empty"]) for r in rows["rows"]] == [(2, False), (3, False)] and rows["rows"][0]["kind"] == "conversation.created"
              and cmd["rows"] == [{"commandId": "k1", "cmd": "rename", "durable": False, "state": "unknown", "target": "d1"}]
              and leases["counts"] == {"owned": 1, "recovering": 1} and leases["recovering"] == ["c" * 32] and leases["total"] == 2
              and [(c["conversationId"], c["policy"]) for c in convs["conversations"]] == [(c1, "metadata"), ("c" * 32, "full")]
              and blocks["blocks"] == [{"conversationId": c1, "blocked": "not_owner", "policy": "localOnly", "createdWritten": True, "createdAcked": False}]
              and is_empty_payload("json", " {} ") and not is_empty_payload(None, '{"a":1}')
              and not writable and before == after)
        return {"ok": ok, "device": report, "rejected": kinds, "conversation": conv, "rejectedRows": rows, "command": cmd, "leases": leases, "blocks": blocks,
                "writable": writable, "unchanged": before == after}


def main(argv):
    if argv[:1] == ["--self-test"]:
        result = self_test()
        print(json.dumps(result, ensure_ascii=False))
        return 0 if result["ok"] else 1
    if len(argv) < 2:
        print("用法：sqlite_ro.py <sync.db> device <author> | rejected [<author>] | conversation <對話 id> | rejected-rows <author> [<after>] | command <bcid> | leases"
              " | publish-blocks（桌面的 sync.db） | --self-test", file=sys.stderr)
        return 2
    db, command = argv[0], argv[1]
    try:
        conn = connect_ro(db)
        try:
            if command == "device" and len(argv) == 3:
                out = device_report(conn, argv[2])
            elif command == "rejected" and len(argv) in (2, 3):
                out = rejected_report(conn, argv[2] if len(argv) == 3 else None)
            elif command == "conversation" and len(argv) == 3:
                out = conversation_report(conn, argv[2])
            elif command == "rejected-rows" and len(argv) in (3, 4):
                out = rejected_rows_report(conn, argv[2], int(argv[3]) if len(argv) == 4 else 0)
            elif command == "command" and len(argv) == 3:
                out = command_report(conn, argv[2])
            elif command == "leases" and len(argv) == 2:
                out = leases_report(conn)
            elif command == "conversations" and len(argv) == 2:
                out = conversations_report(conn)
            elif command == "publish-blocks" and len(argv) == 2:
                out = publish_blocks_report(conn)
            else:
                print(f"不認得的指令：{' '.join(argv[1:])}", file=sys.stderr)
                return 2
        finally:
            conn.close()
    except ValueError as e:
        print(f"參數不合格：{e}", file=sys.stderr)
        return 2
    except (sqlite3.Error, OSError) as e:
        print(f"讀不了 {db}：{e}", file=sys.stderr)
        return 2
    print(json.dumps(out, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
