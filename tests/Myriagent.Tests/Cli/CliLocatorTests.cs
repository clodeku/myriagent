using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Cli;

/// <summary>找本機 CLI 與判斷安裝方式（用假的 fileExists，不碰真的檔案系統）</summary>
public class CliLocatorTests
{
    private const string Native = @"C:\Users\U\.local\bin";
    private const string Npm = @"C:\Users\U\AppData\Roaming\npm";
    private const string NodeDir = @"C:\Program Files\nodejs";
    private const string Managed = @"C:\Users\U\AppData\Local\Myriagent\codex";
    private const string Tools = @"D:\tools";

    private static CliInstallation? Resolve(AgentProvider provider, IReadOnlyList<string> dirs, params string[] files)
    {
        var set = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        return CliLocator.Resolve(provider, dirs, set.Contains, Native, Npm, Managed);
    }

    /// <summary>有檔案大小的版本：sizes 沒列的檔案視為不存在</summary>
    private static CliInstallation? ResolveWithSizes(AgentProvider provider, IReadOnlyList<string> dirs, Dictionary<string, long> sizes)
    {
        var files = new Dictionary<string, long>(sizes, StringComparer.OrdinalIgnoreCase);
        return CliLocator.Resolve(provider, dirs, files.ContainsKey, Native, Npm, Managed, p => files.TryGetValue(p, out var size) ? size : 0);
    }

    private static readonly string ClaudeJs = Path.Combine(Npm, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
    private static readonly string ClaudeNpmExe = Path.Combine(Npm, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
    private static readonly string CodexJs = Path.Combine(Npm, "node_modules", "@openai", "codex", "bin", "codex.js");

    [Fact]
    public void Claude原生安裝_直接執行Exe()
    {
        var found = Resolve(AgentProvider.Claude, new[] { Native, Npm, NodeDir }, Native + @"\claude.exe", Npm + @"\claude.cmd", ClaudeJs, NodeDir + @"\node.exe")!;

        found.Kind.Should().Be(CliInstallKind.Native);
        found.Provider.Should().Be(AgentProvider.Claude);
        found.Launcher.FileName.Should().Be(Native + @"\claude.exe");
        found.Launcher.PrefixArguments.Should().BeEmpty();
        found.DisplayPath.Should().Be(Native + @"\claude.exe");
    }

    [Fact]
    public void Npm安裝_Cmd殼改以Node執行Js入口()
    {
        var found = Resolve(AgentProvider.Claude, new[] { Npm, NodeDir }, Npm + @"\claude.cmd", ClaudeJs, NodeDir + @"\node.exe")!;

        found.Kind.Should().Be(CliInstallKind.Npm);
        found.Launcher.FileName.Should().Be(NodeDir + @"\node.exe");
        found.Launcher.PrefixArguments.Should().Equal(ClaudeJs);
        found.DisplayPath.Should().Be(Npm + @"\claude.cmd", "畫面顯示使用者熟悉的 .cmd 路徑");
    }

    [Fact]
    public void Npm安裝的Claude_2點1點113起沒有CliJs_Cmd殼直接執行BinClaudeExe()
    {
        // 2.1.113 起 package.json 的 bin 是 bin/claude.exe：postinstall 把平台套件的原生檔（數百 MB）放到這裡，套件裡沒有 cli.js
        var found = ResolveWithSizes(AgentProvider.Claude, new[] { Npm, NodeDir }, new()
        {
            [Npm + @"\claude.cmd"] = 300,
            [ClaudeNpmExe] = 250_000_000,
            [NodeDir + @"\node.exe"] = 80_000_000,
        })!;

        found.Kind.Should().Be(CliInstallKind.Npm, "更新要走 npm install -g");
        found.Launcher.FileName.Should().Be(ClaudeNpmExe, "與 claude.cmd 一樣直接執行原生檔，不經 node");
        found.Launcher.PrefixArguments.Should().BeEmpty();
        found.DisplayPath.Should().Be(Npm + @"\claude.cmd");
    }

    [Fact]
    public void Npm安裝的Claude_BinClaudeExe只是佔位腳本_不算安裝_有CliJs時退回Node()
    {
        // postinstall 沒跑（--ignore-scripts）時 bin\claude.exe 是 500 位元組的佔位腳本，執行只會印錯誤
        ResolveWithSizes(AgentProvider.Claude, new[] { Npm, NodeDir }, new()
        {
            [Npm + @"\claude.cmd"] = 300,
            [ClaudeNpmExe] = 500,
            [NodeDir + @"\node.exe"] = 80_000_000,
        }).Should().BeNull();

        var old = ResolveWithSizes(AgentProvider.Claude, new[] { Npm, NodeDir }, new()
        {
            [Npm + @"\claude.cmd"] = 300,
            [ClaudeNpmExe] = 500,
            [ClaudeJs] = 9_000_000,
            [NodeDir + @"\node.exe"] = 80_000_000,
        })!;
        old.Launcher.FileName.Should().Be(NodeDir + @"\node.exe");
        old.Launcher.PrefixArguments.Should().Equal(ClaudeJs);

        ResolveWithSizes(AgentProvider.Claude, new[] { Npm, Tools }, new()
        {
            [Npm + @"\claude.cmd"] = 300,
            [ClaudeNpmExe] = 500,
            [Tools + @"\claude.exe"] = 250_000_000,
        })!.DisplayPath.Should().Be(Tools + @"\claude.exe", "佔位腳本不能用就找下一個目錄");
    }

    [Fact]
    public void Npm安裝的Codex_入口是BinCodexJs_同目錄有Node就優先用它()
    {
        var found = Resolve(AgentProvider.Codex, new[] { Npm, NodeDir }, Npm + @"\codex.cmd", CodexJs, Npm + @"\node.exe", NodeDir + @"\node.exe")!;

        found.Kind.Should().Be(CliInstallKind.Npm);
        found.Launcher.FileName.Should().Be(Npm + @"\node.exe");
        found.Launcher.PrefixArguments.Should().Equal(CodexJs);
    }

    [Fact]
    public void App自行安裝的Codex_是Managed()
    {
        var found = Resolve(AgentProvider.Codex, new[] { Tools, Npm, Managed }, Managed + @"\codex.exe")!;

        found.Kind.Should().Be(CliInstallKind.Managed);
        found.Launcher.FileName.Should().Be(Managed + @"\codex.exe");
    }

    [Fact]
    public void PATH上其他位置的Exe_是Other()
    {
        Resolve(AgentProvider.Codex, new[] { Tools, Npm }, Tools + @"\codex.exe")!.Kind.Should().Be(CliInstallKind.Other);
        Resolve(AgentProvider.Claude, new[] { Tools }, Tools + @"\claude.exe")!.Kind.Should().Be(CliInstallKind.Other);
        Resolve(AgentProvider.Claude, new[] { Managed }, Managed + @"\claude.exe")!.Kind.Should().Be(CliInstallKind.Other, "Managed 只給 Codex");
        Resolve(AgentProvider.Codex, new[] { Native }, Native + @"\codex.exe")!.Kind.Should().Be(CliInstallKind.Other, "Native 只給 Claude");
    }

    [Fact]
    public void 依目錄順序_先找到的優先()
    {
        var found = Resolve(AgentProvider.Claude, new[] { Npm, NodeDir, Native }, Npm + @"\claude.cmd", ClaudeJs, NodeDir + @"\node.exe", Native + @"\claude.exe")!;
        found.Kind.Should().Be(CliInstallKind.Npm, "PATH 上 npm 在前面時用 npm 版");
    }

    [Fact]
    public void Cmd殼缺Js入口或找不到Node_就繼續找下一個目錄()
    {
        Resolve(AgentProvider.Claude, new[] { Npm, Tools }, Npm + @"\claude.cmd", Tools + @"\claude.exe")!.DisplayPath.Should().Be(Tools + @"\claude.exe");
        Resolve(AgentProvider.Claude, new[] { Npm }, Npm + @"\claude.cmd", ClaudeJs).Should().BeNull("沒有 node.exe 無法執行 js 入口");
    }

    [Fact]
    public void 都沒有_回Null()
    {
        Resolve(AgentProvider.Claude, new[] { Native, Npm }).Should().BeNull();
        Resolve(AgentProvider.Codex, Array.Empty<string>()).Should().BeNull();
    }

    [Fact]
    public void 目錄比對不分大小寫與結尾斜線()
    {
        var found = CliLocator.Resolve(AgentProvider.Claude, new[] { @"c:\users\u\.local\bin\" }, p => p.Equals(@"c:\users\u\.local\bin\claude.exe", StringComparison.OrdinalIgnoreCase), Native, Npm, Managed)!;
        found.Kind.Should().Be(CliInstallKind.Native);
    }

    [Fact]
    public void MergeSearchDirectories_切開PATH_去引號與結尾斜線_不分大小寫去重_保留先後()
    {
        var merged = CliLocator.MergeSearchDirectories(
            new[] { @"C:\A;""C:\Program Files\nodejs\"";;  C:\b  ", @"c:\a\;D:\x", null },
            new[] { @"D:\X", @"E:\extra", null, " " });

        merged.Should().Equal(@"C:\A", @"C:\Program Files\nodejs", @"C:\b", @"D:\x", @"E:\extra");
    }

    [Fact]
    public void MergeSearchDirectories_全部空白回空清單()
        => CliLocator.MergeSearchDirectories(new string?[] { null, ";;" }, Array.Empty<string?>()).Should().BeEmpty();

    [Fact]
    public void MergeSearchDirectories_略過UNC目錄_不對外發SMB連線()
    {
        var merged = CliLocator.MergeSearchDirectories(new[] { @"\\server\share\bin;C:\tools;//nas/tools" }, new[] { @"\\server\npm" });
        merged.Should().Equal(@"C:\tools");
    }

    [Fact]
    public void MergeSearchDirectories_略過網路磁碟機上的目錄()
    {
        // 對應成磁碟代號的網路磁碟機（H:\bin）字串上不是 UNC，離線時檢查檔案一樣會卡在 SMB 逾時
        var asked = new List<string>();
        var merged = CliLocator.MergeSearchDirectories(
            new[] { @"C:\tools;H:\bin;Z:\company\tools;c:\TOOLS" },
            new[] { @"H:\npm" },
            d =>
            {
                asked.Add(d);
                return d.StartsWith("H:", StringComparison.OrdinalIgnoreCase) || d.StartsWith("Z:", StringComparison.OrdinalIgnoreCase);
            });

        merged.Should().Equal(@"C:\tools");
        // 重複的目錄（c:\TOOLS）只判斷一次
        asked.Should().Equal(@"C:\tools", @"H:\bin", @"Z:\company\tools", @"H:\npm");
    }

    [Fact]
    public void IsNetworkDrive_本機磁碟與相對路徑不算()
    {
        CliLocator.IsNetworkDrive(Environment.SystemDirectory).Should().BeFalse();
        CliLocator.IsNetworkDrive("bin").Should().BeFalse();
        CliLocator.IsNetworkDrive(string.Empty).Should().BeFalse();
    }

    [Fact]
    public void MergeSearchDirectories_展開登錄檔PATH裡的環境變數()
    {
        var merged = CliLocator.MergeSearchDirectories(new[] { @"%SystemRoot%\System32;%USERPROFILE%\.local\bin\" }, Array.Empty<string?>());

        merged.Should().Equal(
            Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32"),
            Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\.local\bin"));
        merged.Should().NotContain(d => d.Contains('%'));
    }

    [Fact]
    public void Npm全域前綴不在預設位置_Cmd加Js仍算Npm安裝()
    {
        const string customPrefix = @"D:\nodeglobal";
        var js = Path.Combine(customPrefix, "node_modules", "@openai", "codex", "bin", "codex.js");

        var found = Resolve(AgentProvider.Codex, new[] { customPrefix, NodeDir }, customPrefix + @"\codex.cmd", js, NodeDir + @"\node.exe")!;

        found.Kind.Should().Be(CliInstallKind.Npm);
        found.Launcher.PrefixArguments.Should().Equal(js);
    }

    // ───────────── 實際執行的原生執行檔（CliInstallation.NativeExecutable，熱更新的身分比對與監看用） ─────────────

    private static readonly string CodexPackageRoot = Path.Combine(Npm, "node_modules", "@openai", "codex");

    [Fact]
    public void 原生執行檔_直接找到的Exe就是它()
    {
        Resolve(AgentProvider.Claude, new[] { Native }, Native + @"\claude.exe")!.NativeExecutable.Should().Be(Native + @"\claude.exe");
        Resolve(AgentProvider.Codex, new[] { Managed }, Managed + @"\codex.exe")!.NativeExecutable.Should().Be(Managed + @"\codex.exe");
        Resolve(AgentProvider.Codex, new[] { Tools }, Tools + @"\codex.exe")!.NativeExecutable.Should().Be(Tools + @"\codex.exe");
    }

    [Fact]
    public void 原生執行檔_Npm版Claude是套件的BinClaudeExe_舊配置以CliJs代表()
    {
        ResolveWithSizes(AgentProvider.Claude, new[] { Npm, NodeDir }, new()
        {
            [Npm + @"\claude.cmd"] = 300,
            [ClaudeNpmExe] = 250_000_000,
        })!.NativeExecutable.Should().Be(ClaudeNpmExe);

        Resolve(AgentProvider.Claude, new[] { Npm, NodeDir }, Npm + @"\claude.cmd", ClaudeJs, NodeDir + @"\node.exe")!
            .NativeExecutable.Should().Be(ClaudeJs, "2.1.112 以前沒有原生檔：換版時 cli.js 一樣會被換掉");
    }

    [Fact]
    public void 原生執行檔_Npm版Codex是Vendor的CodexExe_不是Node()
    {
        // 這台電腦的實際配置：平台套件巢狀在 @openai\codex\node_modules 底下
        var nested = Path.Combine(CodexPackageRoot, "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe");
        var found = Resolve(AgentProvider.Codex, new[] { Npm, NodeDir }, Npm + @"\codex.cmd", CodexJs, NodeDir + @"\node.exe", nested)!;
        found.NativeExecutable.Should().Be(nested);
        found.NativeExecutable.Should().Be(CliIdentities.CodexNpmExe, "與測試替身的路徑相同（%APPDATA%\\npm 的真實配置）");
        found.Launcher.FileName.Should().Be(NodeDir + @"\node.exe", "啟動方式不變：node 執行 codex.js");

        // 平台套件被提升到前綴的 node_modules\@openai（codex.js 的 require.resolve 往上找得到）
        var hoisted = Path.Combine(Npm, "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe");
        Resolve(AgentProvider.Codex, new[] { Npm, NodeDir }, Npm + @"\codex.cmd", CodexJs, NodeDir + @"\node.exe", hoisted)!
            .NativeExecutable.Should().Be(hoisted);

        // 正在替換或安裝不完整、vendor 的 exe 都不在：用第一個候選（監看與身分讀取知道該看哪裡，讀不到身分＝不明）
        Resolve(AgentProvider.Codex, new[] { Npm, NodeDir }, Npm + @"\codex.cmd", CodexJs, NodeDir + @"\node.exe")!
            .NativeExecutable.Should().Be(Path.Combine(CliLocator.CodexVendorRootCandidates(CodexPackageRoot)[0], "bin", "codex.exe"));
    }

    [Fact]
    public void CodexVendorRootCandidates_照CodexJs的順序_這台電腦的架構優先()
    {
        CliLocator.CodexVendorRootCandidates(CodexPackageRoot, System.Runtime.InteropServices.Architecture.X64).Should().Equal(
            Path.Combine(CodexPackageRoot, "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc"),
            Path.Combine(Npm, "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc"),
            Path.Combine(CodexPackageRoot, "vendor", "x86_64-pc-windows-msvc"),
            Path.Combine(CodexPackageRoot, "node_modules", "@openai", "codex-win32-arm64", "vendor", "aarch64-pc-windows-msvc"),
            Path.Combine(Npm, "node_modules", "@openai", "codex-win32-arm64", "vendor", "aarch64-pc-windows-msvc"),
            Path.Combine(CodexPackageRoot, "vendor", "aarch64-pc-windows-msvc"));
        CliLocator.CodexVendorRootCandidates(CodexPackageRoot, System.Runtime.InteropServices.Architecture.Arm64)[0]
            .Should().Be(Path.Combine(CodexPackageRoot, "node_modules", "@openai", "codex-win32-arm64", "vendor", "aarch64-pc-windows-msvc"));
    }

    [Fact]
    public void Npm前綴與套件根目錄()
    {
        var codex = Resolve(AgentProvider.Codex, new[] { Npm, NodeDir }, Npm + @"\codex.cmd", CodexJs, NodeDir + @"\node.exe")!;
        CliLocator.GetNpmPrefix(codex).Should().Be(Npm);
        CliLocator.NpmPackageRoot(AgentProvider.Codex, Npm).Should().Be(CodexPackageRoot);
        CliLocator.NpmPackageRoot(AgentProvider.Claude, Npm).Should().Be(Path.Combine(Npm, "node_modules", "@anthropic-ai", "claude-code"));
        CliLocator.GetNpmPrefix(Resolve(AgentProvider.Claude, new[] { Native }, Native + @"\claude.exe")!).Should().BeNull("不是 npm 安裝");
    }

    [Fact]
    public void CliState_已安裝與有新版的判斷()
    {
        var installation = new CliInstallation(AgentProvider.Codex, new AgentLauncher(@"C:\npm\node.exe", new[] { CodexJs }), CliInstallKind.Npm, @"C:\npm\codex.cmd");

        var state = new CliState(AgentProvider.Codex, installation, "0.160.1", "0.162.0", false, null, null, null);
        state.Installed.Should().BeTrue();
        state.UpdateAvailable.Should().BeTrue();
        (state with { Latest = "0.160.1" }).UpdateAvailable.Should().BeFalse();
        (state with { Version = null }).Installed.Should().BeFalse("找到執行檔但查不到版本不算已安裝");
        (state with { Version = null }).UpdateAvailable.Should().BeFalse();
        new CliState(AgentProvider.Codex, null, null, "0.162.0", false, null, null, null).UpdateAvailable.Should().BeFalse("沒安裝不提示更新");

        var failed = state with { ActionError = "npm 更新失敗：EBUSY" };
        (failed with { LastCheckedAt = DateTime.Now, Error = null }).ActionError.Should().Be("npm 更新失敗：EBUSY", "查版本更新狀態時保留安裝／更新失敗的原因");
    }

    [Fact]
    public void CliState_找到執行檔但還沒查過版本_是檢查中而不是未安裝()
    {
        var installation = new CliInstallation(AgentProvider.Claude, new AgentLauncher(Native + @"\claude.exe", Array.Empty<string>()), CliInstallKind.Native, Native + @"\claude.exe");

        // 啟動時只找了執行檔、還沒跑 --version
        var startup = new CliState(AgentProvider.Claude, installation, null, null, false, null, null, null);
        startup.Checking.Should().BeTrue();
        startup.Installed.Should().BeFalse();

        var checkedOk = startup with { Version = "2.1.295", VersionCheckedFor = installation.DisplayPath };
        checkedOk.Checking.Should().BeFalse();
        checkedOk.Installed.Should().BeTrue();

        (startup with { VersionCheckedFor = installation.DisplayPath.ToUpperInvariant() }).Checking.Should().BeFalse("查過了只是 --version 失敗：顯示未安裝與原因");
        (checkedOk with { Installation = installation with { DisplayPath = Npm + @"\claude.cmd" } }).Checking.Should().BeTrue("app 開著時換了一個執行檔，還沒查它的版本");
        new CliState(AgentProvider.Claude, null, null, null, false, null, null, null).Checking.Should().BeFalse("沒找到執行檔就是未安裝");
    }

    [Fact]
    public void CliState_只有npm更新中才擋住啟動CLI()
    {
        CliInstallation Of(CliInstallKind kind) => new(AgentProvider.Codex, new AgentLauncher(@"C:\x\codex.exe", Array.Empty<string>()), kind, @"C:\x\codex.cmd");

        new CliState(AgentProvider.Codex, Of(CliInstallKind.Npm), "0.160.1", "0.162.0", true, "更新中…", null, null).ReplacingFiles.Should().BeTrue("npm 先把舊版改名退役，新版解壓完才放好");
        new CliState(AgentProvider.Codex, Of(CliInstallKind.Npm), "0.160.1", "0.162.0", false, null, null, null).ReplacingFiles.Should().BeFalse();
        new CliState(AgentProvider.Codex, Of(CliInstallKind.Managed), "0.160.1", "0.162.0", true, "下載中 40%", null, null).ReplacingFiles.Should().BeFalse("受管版下載期間舊版完整可用，換版是改名");
        new CliState(AgentProvider.Claude, Of(CliInstallKind.Native), "2.1.286", "2.1.295", true, "更新中…", null, null).ReplacingFiles.Should().BeFalse("claude update 下載完才換檔");
    }
}
