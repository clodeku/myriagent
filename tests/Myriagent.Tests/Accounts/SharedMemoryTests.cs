using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Tests.Hub;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Accounts;

/// <summary>
/// 各家代理共用記憶（SharedMemoryRules、SharedCustomizations.WithSharedMemory、中樞的 BuildSessionSettings）：
/// Codex、Antigravity、Grok 的對話拿得到 Claude 那一份專案記憶的位置與索引；Claude、精簡模式、關掉設定、沒接上共用設定時不帶。
/// 一律用暫存資料夾當 ~/.claude，不碰真的使用者目錄
/// </summary>
public sealed class SharedMemoryTests
{
    // ───────────────────────────── 規則 ─────────────────────────────

    [Fact]
    public void 哪幾家由app帶記憶_Claude自己處理_其他三家要帶()
    {
        SharedMemoryRules.AppliesTo(AgentProvider.Claude).Should().BeFalse("Claude 的 CLI 自己載入索引、自己寫（autoMemoryDirectory）");
        SharedMemoryRules.AppliesTo(AgentProvider.Codex).Should().BeTrue();
        SharedMemoryRules.AppliesTo(AgentProvider.Antigravity).Should().BeTrue();
        SharedMemoryRules.AppliesTo(AgentProvider.Grok).Should().BeTrue();
        foreach (var provider in AgentProviders.All)
            FluentActions.Invoking(() => SharedMemoryRules.AppliesTo(provider)).Should().NotThrow("每一種代理都要明確決定");
    }

    [Fact]
    public void 索引_換行統一_沒有就是空的()
    {
        SharedMemoryRules.TrimIndex(null).Should().Be((string.Empty, false));
        SharedMemoryRules.TrimIndex("  \r\n ").Should().Be((string.Empty, false));
        SharedMemoryRules.TrimIndex("- [甲](a.md) — 一  \r\n- [乙](b.md) — 二\r\n\r\n").Should().Be(("- [甲](a.md) — 一\n- [乙](b.md) — 二", false));
    }

    [Fact]
    public void 索引_太多行或太長只取前面_裁在行的邊界()
    {
        var many = string.Join("\n", Enumerable.Range(1, SharedMemoryRules.MaxIndexLines + 5).Select(i => $"- [第{i}則](m{i}.md) — 提示"));
        var (text, truncated) = SharedMemoryRules.TrimIndex(many);
        truncated.Should().BeTrue();
        text.Split('\n').Should().HaveCount(SharedMemoryRules.MaxIndexLines);
        text.Should().EndWith($"- [第{SharedMemoryRules.MaxIndexLines}則](m{SharedMemoryRules.MaxIndexLines}.md) — 提示");

        var longLine = new string('字', 9_000);
        var (longText, longTruncated) = SharedMemoryRules.TrimIndex(string.Join("\n", longLine, longLine, longLine));
        longTruncated.Should().BeTrue("三行合計超過字數上限");
        longText.Should().Be(longLine + "\n" + longLine, "不把一行切一半");
    }

    [Fact]
    public void 說明_寫記憶在哪裡與格式_附目前的索引()
    {
        var text = SharedMemoryRules.Instructions(@"C:\Users\u\.claude\projects\C--repo-x\memory", "- [發佈流程](release.md) — 先部署伺服器\n- [偏好](prefs.md) — 回覆用中文");

        text.Should().StartWith("\n\n# 記憶\n\n", "接在既有說明後面，自己帶分隔");
        text.Should().Contain(@"`C:\Users\u\.claude\projects\C--repo-x\memory`");
        text.Should().Contain("Claude、Codex、Antigravity、Grok");
        text.Should().Contain("type: user | feedback | project | reference");
        text.Should().Contain("`MEMORY.md` 是索引");
        text.Should().Contain(@"重讀 `C:\Users\u\.claude\projects\C--repo-x\memory\MEMORY.md`", "索引是對話建立時的內容，之後要代理自己重讀");
        text.Should().EndWith("目前的索引（`MEMORY.md`）：\n\n- [發佈流程](release.md) — 先部署伺服器\n- [偏好](prefs.md) — 回覆用中文");
    }

    [Fact]
    public void 說明_還沒有記憶_索引很長時各有說法()
    {
        SharedMemoryRules.Instructions(@"C:\m", null).Should().EndWith("目前還沒有任何記憶（`MEMORY.md` 還不存在；存第一則時建立它）。");

        var many = string.Join("\n", Enumerable.Range(1, SharedMemoryRules.MaxIndexLines + 1).Select(i => $"- [第{i}則](m{i}.md) — 提示"));
        var text = SharedMemoryRules.Instructions(@"C:\m", many);
        text.Should().EndWith(@"（索引很長，只附了前面一部分；完整的在 `C:\m\MEMORY.md`。）");
        text.Should().NotContain($"第{SharedMemoryRules.MaxIndexLines + 1}則");
    }

    // ───────────────────────────── 找資料夾、讀索引 ─────────────────────────────

    private static SharedCustomizations Shared(TempDirectory temp, out string source)
    {
        var claude = temp.CreateDirectory(".claude");
        source = claude;
        return new SharedCustomizations(_ => claude, () => temp.Combine(".claude.json"));
    }

    [Fact]
    public void 準備_用Claude那一份專案記憶_建立資料夾並讀索引()
    {
        using var temp = new TempDirectory("shared-memory");
        var shared = Shared(temp, out var source);
        var work = temp.CreateDirectory("work");
        var expected = SharedCustomizationRules.SharedMemoryDirectory(source, work);
        Directory.CreateDirectory(expected);
        // 帶 BOM 的索引（別的編輯器存的）照樣讀得懂
        File.WriteAllText(Path.Combine(expected, "MEMORY.md"), "- [發佈流程](release.md) — 先部署伺服器\n", new System.Text.UTF8Encoding(true));

        var launch = shared.WithSharedMemory(AgentSharedLaunch.None, work);

        launch.SharedMemoryDirectory.Should().Be(expected, "和 Claude 各帳戶共用的是同一個資料夾");
        launch.SharedMemoryInstructions.Should().Contain($"`{expected}`").And.EndWith("- [發佈流程](release.md) — 先部署伺服器");
        launch.SharedMemoryInstructions.Should().NotContain("\uFEFF");
        launch.ClaudeMemoryDirectory.Should().BeNull("原本的啟動參數不動");
    }

    [Fact]
    public void 準備_還沒有記憶的專案_先把資料夾建好()
    {
        using var temp = new TempDirectory("shared-memory");
        var shared = Shared(temp, out var source);
        var work = temp.CreateDirectory("work");
        var expected = SharedCustomizationRules.SharedMemoryDirectory(source, work);
        Directory.Exists(expected).Should().BeFalse();

        var launch = shared.WithSharedMemory(AgentSharedLaunch.None, work);

        Directory.Exists(expected).Should().BeTrue("Codex 沙箱的可寫資料夾、Antigravity 的 --add-dir 都要資料夾已經存在");
        launch.SharedMemoryDirectory.Should().Be(expected);
        launch.SharedMemoryInstructions.Should().Contain("目前還沒有任何記憶");
    }

    /// <summary>對話自己的工作區（每個對話一個）：不替每個對話在 ~/.claude 留一個空資料夾；已經有記憶了（同一個對話先前的 Claude 存過）就照常</summary>
    [Fact]
    public void 準備_不預先建立時_還沒有資料夾只給說明_已經有的照常放行()
    {
        using var temp = new TempDirectory("shared-memory");
        var shared = Shared(temp, out var source);
        var work = temp.CreateDirectory("work");
        var expected = SharedCustomizationRules.SharedMemoryDirectory(source, work);

        var none = shared.WithSharedMemory(AgentSharedLaunch.None, work, create: false);

        Directory.Exists(expected).Should().BeFalse("不建");
        none.SharedMemoryDirectory.Should().BeNull("資料夾不存在：不放進沙箱的可寫資料夾、不帶 --add-dir");
        none.SharedMemoryInstructions.Should().Contain($"`{expected}`", "代理還是知道記憶在哪裡").And.Contain("目前還沒有任何記憶");

        Directory.CreateDirectory(expected);
        File.WriteAllText(Path.Combine(expected, "MEMORY.md"), "- [偏好](prefs.md) — 回覆用中文\n");
        var existing = shared.WithSharedMemory(AgentSharedLaunch.None, work, create: false);

        existing.SharedMemoryDirectory.Should().Be(expected);
        existing.SharedMemoryInstructions.Should().EndWith("- [偏好](prefs.md) — 回覆用中文");
    }

    [Fact]
    public void 準備_git子資料夾與repo根目錄是同一份記憶_保留原本的啟動參數()
    {
        using var temp = new TempDirectory("shared-memory");
        var shared = Shared(temp, out var source);
        var repo = temp.CreateDirectory("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var sub = Directory.CreateDirectory(Path.Combine(repo, "src", "app")).FullName;
        var before = new AgentSharedLaunch(null, null, new[] { "mcp_servers.x.command=\"y\"" });

        var launch = shared.WithSharedMemory(before, sub);

        launch.SharedMemoryDirectory.Should().Be(SharedCustomizationRules.SharedMemoryDirectory(source, repo), "同 Claude：在 git 裡用 repo 的根目錄");
        launch.CodexConfigOverrides.Should().Equal(before.CodexConfigOverrides);
    }

    [Fact]
    public void 準備_資料夾建不起來_照原本的啟動參數不擋對話()
    {
        using var temp = new TempDirectory("shared-memory");
        // 來源設定資料夾的位置是一個檔：底下建不了 projects\…\memory
        var blocked = temp.WriteFile("not-a-folder", "x");
        var shared = new SharedCustomizations(_ => blocked, () => temp.Combine(".claude.json"));

        var launch = shared.WithSharedMemory(AgentSharedLaunch.None, temp.CreateDirectory("work"));

        launch.Should().Be(AgentSharedLaunch.None);
    }

    [Fact]
    public void 會話的說明_有共用記憶就接在後面_沒有就是原本的()
    {
        var settings = new AgentSessionSettings(null, false, null, "k", _ => { }, null, null, null, AgentPermissionMode.Ask, @"C:\w", @"C:\c", "原本的說明", UseLocalCustomizations: true);

        settings.InstructionsWith(null).Should().Be("原本的說明");
        settings.InstructionsWith(AgentSharedLaunch.None).Should().Be("原本的說明");
        settings.InstructionsWith(AgentSharedLaunch.None with { SharedMemoryInstructions = "\n\n# 記憶\n\n…" }).Should().Be("原本的說明\n\n# 記憶\n\n…");
    }

    // ───────────────────────────── 中樞 ─────────────────────────────

    [Fact]
    public void 中樞_Codex的對話帶共用記憶_AccountKey標記_Claude不由app帶()
    {
        using var h = new HubHarness();
        using var temp = new TempDirectory("hub-shared-memory");
        var source = temp.CreateDirectory(".claude");
        h.OnUi(() => h.Accounts.Shared = new SharedCustomizations(_ => source, () => temp.Combine(".claude.json")));
        h.Command(h.Main, "ready");
        h.Main.Last("init")!["settings"]!["shareAgentMemory"]!.Value<bool>().Should().BeTrue("預設開");

        var (_, codex) = h.StartConversation(h.Main, h.Codex);
        var settings = codex.Sent.All.Single().Settings;
        settings.AccountKey.Should().EndWith("|memory", "開關改變時程序要重開");
        settings.PrepareShared.Should().NotBeNull();
        var memory = SharedCustomizationRules.SharedMemoryDirectory(source, settings.WorkingDirectory);
        // 這個對話用自己的工作區：還沒有記憶時不預先建資料夾，只給說明
        var before = settings.PrepareShared!(settings.WorkingDirectory);
        before.SharedMemoryDirectory.Should().BeNull();
        before.SharedMemoryInstructions.Should().Contain($"`{memory}`");
        Directory.Exists(memory).Should().BeFalse("對話自己的工作區每個對話一個：不留空資料夾");
        // 同一個對話先前的 Claude 存過記憶之後（資料夾有了）就整套帶上
        Directory.CreateDirectory(memory);
        File.WriteAllText(Path.Combine(memory, "MEMORY.md"), "- [偏好](prefs.md) — 回覆用中文\n");
        var launch = settings.PrepareShared!(settings.WorkingDirectory);
        launch.SharedMemoryDirectory.Should().Be(memory);
        settings.InstructionsWith(launch).Should().StartWith(settings.Instructions).And.EndWith("- [偏好](prefs.md) — 回覆用中文");

        // Claude：系統預設帳戶本身就是來源、CLI 自己載入記憶；隔離帳戶照舊只有 autoMemoryDirectory
        var (_, claude) = h.StartConversation(h.Main, h.Claude);
        claude.Sent.All.Single().Settings.PrepareShared.Should().BeNull();
        var (_, claude2) = h.StartConversation(h.Main, h.Claude2);
        var isolated = claude2.Sent.All.Single().Settings;
        isolated.AccountKey.Should().NotContain("|memory");
        var isolatedLaunch = isolated.PrepareShared!(isolated.WorkingDirectory);
        isolatedLaunch.SharedMemoryInstructions.Should().BeNull();
        isolatedLaunch.ClaudeMemoryDirectory.Should().Be(SharedCustomizationRules.SharedMemoryDirectory(source, isolated.WorkingDirectory));
    }

    [Fact]
    public async Task 中樞_使用者選的專案資料夾_預先建好記憶資料夾並放行寫入()
    {
        using var h = new HubHarness();
        using var temp = new TempDirectory("hub-shared-memory");
        var source = temp.CreateDirectory(".claude");
        var project = temp.CreateDirectory("project");
        h.OnUi(() => h.Accounts.Shared = new SharedCustomizations(_ => source, () => temp.Combine(".claude.json")));
        var id = h.NewConversation(h.Main, h.Codex);
        await h.SetFolderAsync(h.Main, id, project);

        h.Send(h.Main, id, "第一則");
        // 指定了資料夾的對話，送出前先在背景確認資料夾還在：等會話真的收到
        await h.WaitUntilAsync(() => h.Sessions.Count > 0 && h.Sessions[^1].Sent.All.Count > 0, "送出第一則");
        var settings = h.Sessions[^1].Sent.All.Single().Settings;
        var launch = settings.PrepareShared!(settings.WorkingDirectory);

        var memory = SharedCustomizationRules.SharedMemoryDirectory(source, project);
        launch.SharedMemoryDirectory.Should().Be(memory);
        Directory.Exists(memory).Should().BeTrue("專案資料夾的記憶各個對話共用：先建好，Codex 的沙箱與 Antigravity 的 --add-dir 才放行得了");
    }

    [Fact]
    public void 中樞_關掉設定_精簡模式_沒接上共用設定_都不帶()
    {
        using var h = new HubHarness();
        using var temp = new TempDirectory("hub-shared-memory");
        var source = temp.CreateDirectory(".claude");

        // 沒接上（測試與實機探針的預設）：不碰任何人的 ~/.claude
        var (_, bare) = h.StartConversation(h.Main, h.Codex);
        bare.Sent.All.Single().Settings.PrepareShared.Should().BeNull();
        bare.Sent.All.Single().Settings.AccountKey.Should().NotContain("|memory");

        h.OnUi(() => h.Accounts.Shared = new SharedCustomizations(_ => source, () => temp.Combine(".claude.json")));
        h.Command(h.Main, "setting", new { key = "shareAgentMemory", value = false });
        h.OnUi(() => h.Settings.ShareAgentMemory).Should().BeFalse();
        h.Command(h.Main, "ready");
        h.Main.Last("init")!["settings"]!["shareAgentMemory"]!.Value<bool>().Should().BeFalse();
        // 隔離的 Codex 帳戶照舊有它自己的共用設定（全域指示、MCP）：只看有沒有帶記憶
        static AgentSharedLaunch Launch(AgentSessionSettings s) => s.PrepareShared == null ? AgentSharedLaunch.None : s.PrepareShared(s.WorkingDirectory);
        var (_, off) = h.StartConversation(h.Main, h.Codex);
        var offSettings = off.Sent.All.Single().Settings;
        offSettings.AccountKey.Should().NotContain("|memory", "設定關著");
        Launch(offSettings).SharedMemoryInstructions.Should().BeNull();
        Launch(offSettings).SharedMemoryDirectory.Should().BeNull();
        Directory.Exists(SharedCustomizationRules.SharedMemoryDirectory(source, offSettings.WorkingDirectory)).Should().BeFalse("沒有帶記憶就不建資料夾");

        h.Command(h.Main, "setting", new { key = "shareAgentMemory", value = true });
        h.Command(h.Main, "accountUpdate", new { accountId = h.Codex.Id, safeMode = true });
        var (_, safe) = h.StartConversation(h.Main, h.Codex);
        var safeSettings = safe.Sent.All.Single().Settings;
        safeSettings.AccountKey.Should().NotContain("|memory", "精簡模式不帶使用者的自訂內容");
        Launch(safeSettings).SharedMemoryInstructions.Should().BeNull();
        Directory.Exists(SharedCustomizationRules.SharedMemoryDirectory(source, safeSettings.WorkingDirectory)).Should().BeFalse();
    }
}
