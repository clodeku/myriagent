using System.Runtime.CompilerServices;
using Myriagent.Sync;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;

namespace Myriagent.Tests.Sync.Isolation;

// S6 的陽性對照（D6）：靜態掃描與子程序探針都要抓得到這些型別碰到的 SignalR／SQLite，證明防線不是空轉。
// 只有 IsolationScanTests 與探針的 control 情境會用到；任何根型別都不參考它們。

/// <summary>欄位與方法參考：<see cref="HubConnection"/> 型別的欄位、呼叫它的成員</summary>
internal sealed class IsolationControlField
{
    private HubConnection? connection;

    public void Attach(object value) => connection = value as HubConnection;

    public string? Describe() => connection?.State.ToString();
}

/// <summary>catch 型別與區域變數：<see cref="SqliteException"/> 的 catch 子句、<see cref="SqliteConnection"/> 的區域變數（using 讓它活到 finally）</summary>
internal static class IsolationControlCatchLocal
{
    /// <summary>開一條連線並回傳 SQLite 的版本（探針的 control 情境以記憶體資料庫呼叫：會載入 Microsoft.Data.Sqlite、SQLitePCLRaw 與 e_sqlite3.dll）</summary>
    public static string? Open(string connectionString)
    {
        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            return connection.ServerVersion;
        }
        catch (SqliteException)
        {
            return null;
        }
    }
}

/// <summary>載入邊界：本體碰 SignalR，但標了 [SyncLoadBoundary]，掃描只看簽章（不算）</summary>
internal static class IsolationControlMarked
{
    [SyncLoadBoundary]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string Describe() => typeof(HubConnectionBuilder).FullName ?? string.Empty;
}

/// <summary>同樣的本體、沒有標示：算（探針的 control 情境呼叫它載入 SignalR 用戶端組件）</summary>
internal static class IsolationControlUnmarked
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string Describe() => typeof(HubConnectionBuilder).FullName ?? string.Empty;
}
