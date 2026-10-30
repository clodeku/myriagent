using Myriagent.Agents;
using Myriagent.Hub;

namespace Myriagent.Tests.Hub;

/// <summary>背景工作的「詳細」：key、拆出代理的說明與指令、白話說明、看不到輸出的原因</summary>
public sealed class BackgroundTaskDetailRulesTests
{
    [Fact]
    public void Key_工具名稱加一行摘要_同一個說明同一把_不同就不同()
    {
        var a = new AgentBackgroundTool("執行指令", "bash", "npm test\nnpm run lint", "x", TaskId: "b1");
        var key = BackgroundTaskDetailRules.Key(a);
        key.Should().MatchRegex("^t[0-9a-f]{12}$");
        BackgroundTaskDetailRules.Key(a with { Detail = "y", TaskId = "b2" }).Should().Be(key, "標頭同一個說明只列一次：只看工具名稱與一行摘要");
        BackgroundTaskDetailRules.Key(a with { Title = "npm run dev" }).Should().NotBe(key);
        BackgroundTaskDetailRules.Key(a with { ToolName = "執行 PowerShell" }).Should().NotBe(key);
    }

    [Fact]
    public void Describe_Claude的指令拆出代理的說明_Codex整段是指令_子代理寫它在處理什麼()
    {
        var claude = BackgroundTaskDetailRules.Describe(new AgentBackgroundTool("執行指令", "bash", "dotnet test Myriagent.slnx -c Release",
            "Run full solution tests\n\ndotnet test Myriagent.slnx -c Release"));
        claude.AgentNote.Should().Be("Run full solution tests");
        claude.Content.Should().Be("dotnet test Myriagent.slnx -c Release");
        claude.ContentLabel.Should().Be("指令");
        claude.Explain.Should().StartWith("執行測試");

        var multi = BackgroundTaskDetailRules.Describe(new AgentBackgroundTool("執行指令", "bash", "cd web", "Start dev server\n\ncd web\n\nnpm run dev"));
        multi.AgentNote.Should().Be("Start dev server");
        multi.Content.Should().Be("cd web\n\nnpm run dev", "指令本身有空行：只拆第一個");
        multi.Explain.Should().StartWith("啟動開發伺服器");

        var codex = BackgroundTaskDetailRules.Describe(new AgentBackgroundTool("執行指令", "bash", "npm run dev", "npm run dev -- --port 5173\n\n# note"));
        codex.AgentNote.Should().BeNull("空行之後不是一行摘要：整段都是指令");
        codex.Content.Should().Be("npm run dev -- --port 5173\n\n# note");

        var noDetail = BackgroundTaskDetailRules.Describe(new AgentBackgroundTool("背景工作", "bash", "sleep 30", null));
        noDetail.Content.Should().Be("sleep 30");
        noDetail.Explain.Should().StartWith("等待");

        var agent = BackgroundTaskDetailRules.Describe(new AgentBackgroundTool("子代理", "agent", "檢查文件裡的連結", "逐一打開 docs 裡的連結"));
        agent.Explain.Should().Be("子代理（另一個代理）在背景處理「檢查文件裡的連結」，做完會把結果交給這個對話的代理。");
        agent.Content.Should().Be("逐一打開 docs 裡的連結");
        agent.ContentLabel.Should().Be("交代給子代理的工作");

        BackgroundTaskDetailRules.Describe(new AgentBackgroundTool("Monitor", "other", "dev server log", null)).Explain.Should().StartWith("持續監看「dev server log」");
        BackgroundTaskDetailRules.Describe(new AgentBackgroundTool("排程計時", "other", "提醒檢查部署", "{}")).Explain.Should().StartWith("排定的計時或排程");
        BackgroundTaskDetailRules.Describe(new AgentBackgroundTool("讀取網頁", "web", null, null)).Explain.Should().Be("代理以「讀取網頁」在背景執行一件工作，完成後代理會接著處理。");
    }

    [Theory]
    [InlineData("npm run dev -- --port 5173", "啟動開發伺服器")]
    [InlineData("cd web && pnpm dev", "啟動開發伺服器")]
    [InlineData("dotnet watch run --project src/App", "啟動開發伺服器")]
    [InlineData("python -m http.server 8000", "啟動開發伺服器")]
    [InlineData("tail -f logs/app.log", "持續監看")]
    [InlineData("Get-Content app.log -Wait", "持續監看")]
    [InlineData("dotnet test Myriagent.slnx -c Release 2>&1 | tail -5", "執行測試")]
    [InlineData("until curl -s localhost:5173; do sleep 1; done; npx playwright test", "執行測試")]
    [InlineData("cd tools/ui-probe && node --test live-rules.test.mjs", "執行測試")]
    [InlineData("pytest -x tests/", "執行測試")]
    [InlineData("dotnet build Myriagent.slnx -c Release -nodeReuse:false", "建置專案")]
    [InlineData("npm run build", "建置專案")]
    [InlineData("npm ci", "安裝或還原套件")]
    [InlineData("pip install -r requirements.txt", "安裝或還原套件")]
    [InlineData("powershell -File tools/deploy/Deploy-MyriagentHubFromDev.ps1 -Commit abc", "部署或上傳")]
    [InlineData("git push origin HEAD:main", "部署或上傳")]
    [InlineData("ping -n 30 127.0.0.1", "等待一段時間")]
    [InlineData("Start-Sleep 25; Set-Content bg.txt done", "等待一段時間")]
    [InlineData("until grep -q Ready dev.log; do sleep 0.5; done", "等待一段時間")]
    [InlineData("curl -L -o data.zip https://example.com/data.zip", "下載或連到網路服務")]
    [InlineData("python -X utf8 tools/fake-device/measure_sync.py --runs 5", "執行腳本 measure_sync.py")]
    [InlineData("cd x; node scripts/gen.mjs", "執行腳本 gen.mjs")]
    [InlineData("pwsh -NoProfile -File C:\\tools\\run.ps1", "執行腳本 run.ps1")]
    [InlineData("ffmpeg -i a.mp4 b.webm", "在背景執行指令")]
    [InlineData("", "在背景執行指令")]
    public void ExplainCommand_依指令的樣子說明(string command, string expected)
        => BackgroundTaskDetailRules.ExplainCommand(command).Should().StartWith(expected);

    [Fact]
    public void NoOutputNote_每種代理都有說法()
    {
        foreach (var provider in AgentProviders.All)
            BackgroundTaskDetailRules.NoOutputNote(provider, "bash").Should().NotBeNullOrWhiteSpace(provider.ToString());
        BackgroundTaskDetailRules.NoOutputNote(AgentProvider.Claude, "agent").Should().Contain("子代理");
    }
}
