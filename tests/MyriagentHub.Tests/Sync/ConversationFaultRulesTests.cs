using MyriagentHub.Sync;
using Microsoft.Data.Sqlite;

namespace MyriagentHub.Tests.Sync;

/// <summary>物化時的例外分類（WP4.2 計畫步 2、D37）：毒事件只拒收那一筆，環境與程式狀態的問題照舊中斷連線</summary>
public sealed class ConversationFaultRulesTests
{
    public static TheoryData<string> PoisonNames =>
    [
        "Newtonsoft.JsonException", "Newtonsoft.JsonReaderException", "System.Text.Json.JsonException",
        "InvalidCast", "Argument", "ArgumentNull", "ArgumentOutOfRange", "DecoderFallback", "Format", "Overflow", "NullReference",
        "KeyNotFound", "IndexOutOfRange", "Sqlite.Constraint", "Sqlite.ConstraintUnique", "Sqlite.ConstraintPrimaryKey", "Sqlite.Mismatch",
        "Sqlite.Range",
    ];

    public static TheoryData<string> NotPoisonNames =>
    [
        "Sqlite.Busy", "Sqlite.BusyRecovery", "Sqlite.IoErr", "Sqlite.Full", "Sqlite.Corrupt", "Sqlite.Locked", "Sqlite.ReadOnly", "IO",
        "OperationCanceled", "TaskCanceled", "ObjectDisposed", "InvalidOperation", "Exception", "OutOfMemory", "Timeout",
        "Unauthorized",
    ];

    [Theory]
    [MemberData(nameof(PoisonNames))]
    public void 毒事件為真(string name) => ConversationFaultRules.IsPoison(Make(name)).Should().BeTrue(name);

    [Theory]
    [MemberData(nameof(NotPoisonNames))]
    public void 環境與程式狀態的問題為假(string name) => ConversationFaultRules.IsPoison(Make(name)).Should().BeFalse(name);

    private static Exception Make(string name) => name switch
    {
        "Newtonsoft.JsonException" => new Newtonsoft.Json.JsonException("x"),
        "Newtonsoft.JsonReaderException" => new Newtonsoft.Json.JsonReaderException("x"),
        "System.Text.Json.JsonException" => new System.Text.Json.JsonException("x"),
        "InvalidCast" => new InvalidCastException(),
        "Argument" => new ArgumentException("x"),
        "ArgumentNull" => new ArgumentNullException("x"),
        "ArgumentOutOfRange" => new ArgumentOutOfRangeException("x"),
        "DecoderFallback" => new System.Text.DecoderFallbackException(),
        "Format" => new FormatException(),
        "Overflow" => new OverflowException(),
        "NullReference" => new NullReferenceException(),
        "KeyNotFound" => new KeyNotFoundException(),
        "IndexOutOfRange" => new IndexOutOfRangeException(),
        "Sqlite.Constraint" => new SqliteException("constraint", 19),
        "Sqlite.ConstraintUnique" => new SqliteException("unique", 19, 2067),
        // 擴充結果碼直接當成 errorCode 傳也照低 8 位元判斷
        "Sqlite.ConstraintPrimaryKey" => new SqliteException("primary key", 1555),
        "Sqlite.Mismatch" => new SqliteException("mismatch", 20),
        "Sqlite.Range" => new SqliteException("range", 25),
        "Sqlite.Busy" => new SqliteException("busy", 5),
        "Sqlite.BusyRecovery" => new SqliteException("busy recovery", 261),
        "Sqlite.IoErr" => new SqliteException("io", 10),
        "Sqlite.Full" => new SqliteException("full", 13),
        "Sqlite.Corrupt" => new SqliteException("corrupt", 11),
        "Sqlite.Locked" => new SqliteException("locked", 6),
        "Sqlite.ReadOnly" => new SqliteException("readonly", 8),
        "IO" => new IOException(),
        "OperationCanceled" => new OperationCanceledException(),
        "TaskCanceled" => new TaskCanceledException(),
        "ObjectDisposed" => new ObjectDisposedException("x"),
        "InvalidOperation" => new InvalidOperationException(),
        "Exception" => new Exception(),
        "OutOfMemory" => new OutOfMemoryException(),
        "Timeout" => new TimeoutException(),
        "Unauthorized" => new UnauthorizedAccessException(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };
}
