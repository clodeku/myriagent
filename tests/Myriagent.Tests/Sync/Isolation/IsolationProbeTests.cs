namespace Myriagent.Tests.Sync.Isolation;

/// <summary>
/// S6 第二道防線（D6）：以測試組件的自訂 Main 在子程序裡跑情境，比對實際載入的組件。
/// 同一個探針的 <c>baseline</c>（不建同步元件）與 <c>unpaired</c> 的差集必須是空的；陽性對照 <c>control</c> 證明探針看得到 SignalR、SQLite 與 e_sqlite3.dll，
/// <c>paired-offline</c> 證明已配對的正式路徑（載入邊界之後）真的會載入它們。
/// </summary>
public sealed class IsolationProbeTests
{
    [Fact]
    public void 子程序_未配對與基準的組件集合相同()
    {
        var baseline = IsolationProbeRunner.Run("baseline");
        var unpaired = IsolationProbeRunner.Run("unpaired");

        baseline.Assemblies.Should().Contain(["Myriagent", "Newtonsoft.Json"], "探針真的建了中樞");
        unpaired.Assemblies.Except(baseline.Assemblies, StringComparer.Ordinal).Should().BeEmpty("未配對時不得多載入任何組件");
    }

    [Fact]
    public void 子程序_未配對_沒有禁止的組件與e_sqlite3()
    {
        var unpaired = IsolationProbeRunner.Run("unpaired");

        unpaired.ForbiddenAssemblies.Should().BeEmpty();
        unpaired.LoadedNativeSqlite.Should().BeFalse();
        unpaired.LogLines.Should().BePositive("探針收得到中樞的記錄檔");
        unpaired.LogProblems.Should().BeEmpty("未配對時啟動同步不該有警告或錯誤");
    }

    [Fact]
    public void 子程序_未配對跑過所有同步指令_沒有禁止的組件與e_sqlite3()
    {
        var baseline = IsolationProbeRunner.Run("baseline");
        var commands = IsolationProbeRunner.Run("unpaired-commands");

        // System.Net.Http 在基準就載入了（中樞本身用 HttpClient），沒有鑑別力；真的對伺服器開了連線才會有 Sockets
        baseline.Assemblies.Should().NotContain("System.Net.Sockets", "對照組：基準沒有開過任何連線");
        commands.Assemblies.Should().Contain("System.Net.Sockets", "真的對伺服器開了連線（探針另外要求失敗原因是「連不上伺服器」）");
        commands.ForbiddenAssemblies.Should().BeEmpty("配對、取消、暫停、重試、改政策、改名、解除配對都不碰 SignalR／SQLite");
        commands.LoadedNativeSqlite.Should().BeFalse();
    }

    [Fact]
    public void 子程序_陽性對照_兩種組件與e_sqlite3都載入()
    {
        var control = IsolationProbeRunner.Run("control");

        control.ForbiddenAssemblies.Should().Contain(a => a.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal), "碰了 HubConnectionBuilder");
        control.ForbiddenAssemblies.Should().Contain(a => a.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal), "開了 SqliteConnection");
        control.ForbiddenAssemblies.Should().Contain(a => a.StartsWith("SQLitePCLRaw", StringComparison.Ordinal));
        control.LoadedNativeSqlite.Should().BeTrue("記憶體資料庫也要載入原生的 e_sqlite3.dll");
    }

    /// <summary>已配對的路徑經載入邊界建正式的引擎：連不上伺服器也已經載入 SignalR 與 SQLite（未配對的情境沒有它們，才有意義）</summary>
    [Fact]
    public void 子程序_已配對但連不上_SignalR與Sqlite會載入()
    {
        var paired = IsolationProbeRunner.Run("paired-offline", PairedOfflineProbe.Prepare);

        paired.ForbiddenAssemblies.Should().Contain("Microsoft.AspNetCore.SignalR.Client", "引擎建了 HubConnection");
        paired.ForbiddenAssemblies.Should().Contain("Microsoft.Data.Sqlite", "引擎開了 sync.db");
        paired.LoadedNativeSqlite.Should().BeTrue();
    }
}
