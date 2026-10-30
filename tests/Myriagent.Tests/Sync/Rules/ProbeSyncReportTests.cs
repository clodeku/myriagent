using Myriagent.Sync;
using Myriagent.Tests.Sync.Isolation;
using Myriagent.Tests.Sync.Pairing;

namespace Myriagent.Tests.Sync.Rules;

public sealed class ProbeSyncReportTests
{
    [Fact]
    public void 只列禁止前綴的組件()
    {
        string[] assemblies =
        [
            "Myriagent", "System.Net.Http", "Newtonsoft.Json", "Microsoft.Web.WebView2.Core",
            "Microsoft.AspNetCore.SignalR.Client", "Microsoft.Extensions.Logging", "Microsoft.Data.Sqlite", "SQLitePCLRaw.core",
            "Microsoft.Data.Sqlite", "Microsoft.Win32.SystemEvents", "",
        ];
        string[] modules = ["ntdll.dll", "KERNEL32.DLL", "E_SQLITE3.DLL"];
        var status = SyncCoordinatorRules.Initial(PairingTestData.Device(), null) with { State = SyncEngineState.Live, Pending = 4 };

        var report = ProbeSyncReport.Build(assemblies, modules, status, paired: true, appPrivateMb: 82.5, notOnline: false);

        report["loadedForbidden"]!.Values<string>().Should().Equal(
            "Microsoft.AspNetCore.SignalR.Client", "Microsoft.Data.Sqlite", "Microsoft.Extensions.Logging", "SQLitePCLRaw.core", "e_sqlite3.dll");
        report.Value<bool>("paired").Should().BeTrue();
        report.Value<string>("state").Should().Be("online");
        report.Value<int>("pending").Should().Be(4);
        report.Value<double>("appPrivateMb").Should().Be(82.5);
        report.Property("notOnline").Should().BeNull("連上了");
    }

    [Fact]
    public void 已配對連上_記取樣期間的心跳數_其他情況不寫()
    {
        var status = SyncCoordinatorRules.Initial(PairingTestData.Device(), null) with { State = SyncEngineState.Live };

        ProbeSyncReport.Build([], [], status, paired: true, appPrivateMb: null, notOnline: false, heartbeatsDuringSample: 2)
            .Value<long>("heartbeatsDuringSample").Should().Be(2);
        ProbeSyncReport.Build([], [], status, paired: true, appPrivateMb: null, notOnline: false, heartbeatsDuringSample: 0)
            .Value<long>("heartbeatsDuringSample").Should().Be(0, "0 也要寫：量測工具據此判定取樣窗裡沒有心跳、量測無效");
        ProbeSyncReport.Build([], [], null, paired: false, appPrivateMb: null, notOnline: false)
            .Property("heartbeatsDuringSample").Should().BeNull();
    }

    [Fact]
    public void 未配對_沒有狀態_清單是空的()
    {
        var report = ProbeSyncReport.Build(["Myriagent", "System.Net.Http"], ["ntdll.dll"], null, paired: false, appPrivateMb: 60, notOnline: false);

        report["loadedForbidden"]!.Should().BeEmpty();
        report.Value<bool>("paired").Should().BeFalse();
        report.Value<string>("state").Should().BeNull();
        report.Value<int?>("pending").Should().BeNull();
    }

    [Fact]
    public void 已配對逾時沒連上_記notOnline()
    {
        var status = SyncCoordinatorRules.Initial(PairingTestData.Device(), null) with { State = SyncEngineState.Backoff, Pending = 1 };

        var report = ProbeSyncReport.Build([], [], status, paired: true, appPrivateMb: null, notOnline: true);

        report.Value<bool>("notOnline").Should().BeTrue();
        report.Value<string>("state").Should().Be("offline");
    }

    [Fact]
    public void 禁止的前綴與原生模組_和S6掃描器相同()
    {
        ProbeSyncReport.ForbiddenPrefixes.Should().Equal(AssemblyIsolationScanner.ForbiddenPrefixes);
        ProbeSyncReport.ForbiddenNativeModule.Should().Be(AssemblyIsolationScanner.ForbiddenNativeModule);
    }
}
