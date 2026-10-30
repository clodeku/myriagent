using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Cli;

/// <summary>
/// 熱更新契約：執行檔身分的比較語意（相等＝同一個安裝的同一份檔案）、換版事件的四種情況、換版套用到 CLI 狀態
/// </summary>
public sealed class CliBinaryIdentityTests
{
    private static readonly DateTime Written = new(2026, 10, 9, 14, 49, 39, DateTimeKind.Utc);

    private static CliBinaryIdentity Make(
        string path = CliIdentities.ClaudeNativeExe,
        string version = "2.1.295",
        ulong volume = 7,
        ulong fileId = 0x220000003BF109,
        long size = 259_509_920,
        DateTime? written = null,
        AgentProvider provider = AgentProvider.Claude,
        CliInstallKind kind = CliInstallKind.Native)
        => new(provider, kind, path, version, volume, fileId, size, written ?? Written);

    [Fact]
    public void 同一個安裝的同一份檔案才相等_路徑不分大小寫_版本字串不參與比較()
    {
        var a = Make();
        a.Should().Be(Make(), "每個欄位都相同");
        a.Should().Be(Make(path: CliIdentities.ClaudeNativeExe.ToUpperInvariant()), "Windows 路徑不分大小寫");
        a.GetHashCode().Should().Be(Make(path: CliIdentities.ClaudeNativeExe.ToUpperInvariant()).GetHashCode(), "相等的身分雜湊值也要相同");
        a.Should().Be(Make(version: "2.1.295.0"), "版本是從檔案推得的顯示資訊：來源不同（FileVersionInfo、--version）不能讓同一份檔案被當成換版");

        a.Should().NotBe(Make(fileId: 0x80000003BF11C), "換檔後 FileId 一定會變（原地換版時路徑不變）");
        a.Should().NotBe(Make(size: 259_509_921));
        a.Should().NotBe(Make(written: Written.AddMilliseconds(1)));
        a.Should().NotBe(Make(volume: 8));
        a.Should().NotBe(Make(path: @"C:\Users\U\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe"));
        a.Should().NotBe(Make(kind: CliInstallKind.Npm), "不同的安裝方式是不同的安裝");
        a.Should().NotBe(Make(provider: AgentProvider.Codex, kind: CliInstallKind.Native));
        a.Equals(null).Should().BeFalse();
    }

    [Fact]
    public void 最後寫入時間一律存成UTC_本機時間會換算_未指定種類的視為UTC()
    {
        var local = Written.ToLocalTime();
        Make(written: local).LastWriteUtc.Should().Be(Written);
        Make(written: local).LastWriteUtc.Kind.Should().Be(DateTimeKind.Utc);
        Make(written: local).Should().Be(Make(), "同一個時間點用本機時間傳入也是同一份檔案");

        var unspecified = DateTime.SpecifyKind(Written, DateTimeKind.Unspecified);
        Make(written: unspecified).LastWriteUtc.Kind.Should().Be(DateTimeKind.Utc);
        Make(written: unspecified).Should().Be(Make());

        var changed = Make() with { LastWriteUtc = local };
        changed.LastWriteUtc.Kind.Should().Be(DateTimeKind.Utc, "以 with 改值也換算");
        changed.Should().Be(Make());
    }

    [Fact]
    public void SameOrUnknown_任一方不明就不構成重開的理由()
    {
        CliBinaryIdentity.SameOrUnknown(null, null).Should().BeTrue();
        CliBinaryIdentity.SameOrUnknown(Make(), null).Should().BeTrue("還沒偵測到身分（或讀取失敗）不能讓所有對話重開");
        CliBinaryIdentity.SameOrUnknown(null, Make()).Should().BeTrue();
        CliBinaryIdentity.SameOrUnknown(Make(), Make(version: "x")).Should().BeTrue();
        CliBinaryIdentity.SameOrUnknown(Make(), Make(fileId: 1)).Should().BeFalse();
    }

    [Fact]
    public void 顯示文字含版本路徑與十六進位FileId()
    {
        var text = Make().ToString();
        text.Should().Contain("Claude 2.1.295").And.Contain(CliIdentities.ClaudeNativeExe).And.Contain("220000003bf109");
    }

    [Fact]
    public void 換版事件的四種情況()
    {
        var old = CliIdentities.Claude("2.1.295", fileId: 1);
        var fresh = CliIdentities.Claude("2.1.297", fileId: 2);

        var changing = new CliVersionChange(AgentProvider.Claude, old, null, CliChangeOrigin.External, Stable: false, InstallBroken: false, null);
        changing.IsSettledBinary.Should().BeFalse("安裝還在變動");
        changing.IsVersionSwitch.Should().BeFalse();

        var broken = new CliVersionChange(AgentProvider.Codex, CliIdentities.Codex("0.160.1"), null, CliChangeOrigin.App, Stable: true, InstallBroken: true,
            "Codex 安裝不一致（package.json 0.162.0、執行檔 0.160.1），請修復");
        broken.IsSettledBinary.Should().BeFalse("損壞時不重開任何對話");

        var settled = new CliVersionChange(AgentProvider.Claude, old, fresh, CliChangeOrigin.External, Stable: true, InstallBroken: false, "你在終端機更新了 Claude Code");
        settled.IsSettledBinary.Should().BeTrue();
        settled.IsVersionSwitch.Should().BeTrue();
        settled.FromVersion.Should().Be("2.1.295");
        settled.ToVersion.Should().Be("2.1.297");
        settled.DetectedAtUtc.Kind.Should().Be(DateTimeKind.Utc);

        var firstSighting = settled with { Old = null };
        firstSighting.IsSettledBinary.Should().BeTrue();
        firstSighting.IsVersionSwitch.Should().BeFalse("之前的身分不明（啟動後第一次偵測）不算換版");

        var removed = new CliVersionChange(AgentProvider.Claude, old, null, CliChangeOrigin.External, Stable: true, InstallBroken: false, null);
        removed.IsSettledBinary.Should().BeFalse("CLI 已不在這台電腦");
        removed.ToVersion.Should().BeNull();
    }

    [Fact]
    public void 換版套用到狀態_變動中_損壞_換上新身分_移除()
    {
        var old = CliIdentities.Claude("2.1.295", fileId: 1);
        var fresh = CliIdentities.Claude("2.1.297", fileId: 2);
        var state = new CliState(AgentProvider.Claude, null, "2.1.295", "2.1.297", false, null, null, null) { Identity = old };

        var changing = CliManager.ApplyVersionChange(state, new CliVersionChange(AgentProvider.Claude, old, null, CliChangeOrigin.External, false, false, null));
        changing.InstallChanging.Should().BeTrue();
        changing.Identity.Should().Be(old, "變動中還沒有新的身分");

        var broken = CliManager.ApplyVersionChange(changing, new CliVersionChange(AgentProvider.Claude, old, null, CliChangeOrigin.External, true, true, "安裝不一致"));
        broken.InstallChanging.Should().BeFalse();
        broken.InstallBroken.Should().BeTrue();
        broken.InstallBrokenReason.Should().Be("安裝不一致");
        broken.Identity.Should().Be(old, "損壞時保留最後可用的身分：對話不會因此重開");
        CliManager.ApplyVersionChange(changing, new CliVersionChange(AgentProvider.Claude, old, null, CliChangeOrigin.External, true, true, null))
            .InstallBrokenReason.Should().NotBeNullOrWhiteSpace("沒有說明時給預設說明");

        var change = new CliVersionChange(AgentProvider.Claude, old, fresh, CliChangeOrigin.App, true, false, null);
        var switched = CliManager.ApplyVersionChange(broken, change);
        switched.Identity.Should().Be(fresh);
        switched.InstallChanging.Should().BeFalse();
        switched.InstallBroken.Should().BeFalse("穩定的新身分清掉損壞");
        switched.LastChange.Should().BeSameAs(change);
        switched.Version.Should().Be("2.1.297", "CLI 卡立即顯示新版");

        var again = CliManager.ApplyVersionChange(switched, new CliVersionChange(AgentProvider.Claude, fresh, fresh, CliChangeOrigin.External, true, false, null));
        again.LastChange.Should().BeSameAs(change, "身分沒變（例如輪詢再確認一次）不算新的換版");

        var removed = CliManager.ApplyVersionChange(switched, new CliVersionChange(AgentProvider.Claude, fresh, null, CliChangeOrigin.External, true, false, null));
        removed.Identity.Should().BeNull();
        removed.LastChange.Should().BeSameAs(change);
        removed.Version.Should().Be("2.1.297", "版本由下一次檢查（找不到執行檔）清掉");
    }

    [Fact]
    public void NpmInstalling_只在npm安裝正在換檔時成立()
    {
        var npm = new CliInstallation(AgentProvider.Codex, new AgentLauncher(@"C:\npm\node.exe", new[] { @"C:\npm\node_modules\@openai\codex\bin\codex.js" }), CliInstallKind.Npm, @"C:\npm\codex.cmd");
        var native = new CliInstallation(AgentProvider.Claude, new AgentLauncher(CliIdentities.ClaudeNativeExe, Array.Empty<string>()), CliInstallKind.Native, CliIdentities.ClaudeNativeExe);
        CliState State(CliInstallation installation, CliUpdatePhase phase)
            => new(installation.Provider, installation, "1.0.0", null, true, null, null, null) { UpdatePhase = phase };

        State(npm, CliUpdatePhase.Installing).NpmInstalling.Should().BeTrue("這時結束 app 會讓 CLI 安裝不完整");
        State(npm, CliUpdatePhase.Draining).NpmInstalling.Should().BeFalse("還在等對話停下，還沒動到檔案");
        State(npm, CliUpdatePhase.Verifying).NpmInstalling.Should().BeFalse();
        State(native, CliUpdatePhase.Installing).NpmInstalling.Should().BeFalse("claude update 以改名換檔，中途結束 app 不會弄壞安裝");
        new CliState(AgentProvider.Codex, npm, "1.0.0", null, false, null, null, null).UpdatePhase.Should().Be(CliUpdatePhase.None);
    }

    [Fact]
    public void CliInstallation_原生執行檔預設未解析_with保留()
    {
        var installation = new CliInstallation(AgentProvider.Codex, new AgentLauncher(@"C:\npm\node.exe", Array.Empty<string>()), CliInstallKind.Npm, @"C:\npm\codex.cmd");
        installation.NativeExecutable.Should().BeNull("由 CliLocator.Resolve 填入");
        var resolved = installation with { NativeExecutable = CliIdentities.CodexNpmExe };
        resolved.NativeExecutable.Should().Be(CliIdentities.CodexNpmExe);
        resolved.Should().NotBe(installation);
    }
}
