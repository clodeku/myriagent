using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Core;
using Myriagent.Hub;

namespace Myriagent.Tests.Hub;

/// <summary>
/// CLI 熱更新的純函式規則：政策表全組合（設計 7.4.1）、不自動接續的情況與高風險操作（7.7）、標頭與 Notice 的文字（7.8）
/// </summary>
public class CliRestartRulesTests
{
    // ───── 對話的狀態 ─────

    [Theory]
    [InlineData(false, true, true, false, false, false, CliRestartSituation.NoProcess)]
    [InlineData(false, false, true, false, false, true, CliRestartSituation.NoProcess)]
    [InlineData(true, false, true, false, false, false, CliRestartSituation.Idle)]
    [InlineData(true, false, false, false, false, true, CliRestartSituation.IdleWithBackground)]
    [InlineData(true, true, true, false, false, false, CliRestartSituation.AtSafePoint)]
    [InlineData(true, true, false, true, false, false, CliRestartSituation.ToolRunning)]
    [InlineData(true, true, false, true, true, false, CliRestartSituation.WaitingForUser)]
    [InlineData(true, true, false, false, true, true, CliRestartSituation.WaitingForUser)]
    [InlineData(true, true, false, false, false, true, CliRestartSituation.BackgroundRunning)]
    [InlineData(true, true, false, true, false, true, CliRestartSituation.ToolRunning)]
    [InlineData(true, true, false, false, false, false, CliRestartSituation.ToolRunning)]
    public void Classify_依程序與安全點旗標分到政策表的列(bool live, bool running, bool safePoint, bool tool, bool user, bool background, object expected)
        => CliRestartRules.Classify(live, running, safePoint, tool, user, background).Should().Be((CliRestartSituation)expected);

    // ───── 政策表（7.4.1）─────

    [Theory]
    // 預設：安全點立即切換
    [InlineData(CliRestartPolicy.SafePoint, CliRestartSituation.NoProcess, CliRestartAction.None)]
    [InlineData(CliRestartPolicy.SafePoint, CliRestartSituation.Idle, CliRestartAction.Close)]
    [InlineData(CliRestartPolicy.SafePoint, CliRestartSituation.IdleWithBackground, CliRestartAction.WaitBackground)]
    [InlineData(CliRestartPolicy.SafePoint, CliRestartSituation.AtSafePoint, CliRestartAction.Restart)]
    [InlineData(CliRestartPolicy.SafePoint, CliRestartSituation.ToolRunning, CliRestartAction.WaitTool)]
    [InlineData(CliRestartPolicy.SafePoint, CliRestartSituation.BackgroundRunning, CliRestartAction.WaitBackground)]
    [InlineData(CliRestartPolicy.SafePoint, CliRestartSituation.WaitingForUser, CliRestartAction.WaitUser)]
    // 等這一輪結束
    [InlineData(CliRestartPolicy.AfterTurn, CliRestartSituation.NoProcess, CliRestartAction.None)]
    [InlineData(CliRestartPolicy.AfterTurn, CliRestartSituation.Idle, CliRestartAction.Close)]
    [InlineData(CliRestartPolicy.AfterTurn, CliRestartSituation.IdleWithBackground, CliRestartAction.WaitBackground)]
    [InlineData(CliRestartPolicy.AfterTurn, CliRestartSituation.AtSafePoint, CliRestartAction.WaitTurn)]
    [InlineData(CliRestartPolicy.AfterTurn, CliRestartSituation.ToolRunning, CliRestartAction.WaitTurn)]
    [InlineData(CliRestartPolicy.AfterTurn, CliRestartSituation.BackgroundRunning, CliRestartAction.WaitTurn)]
    [InlineData(CliRestartPolicy.AfterTurn, CliRestartSituation.WaitingForUser, CliRestartAction.WaitTurn)]
    // 下次送出時才用新版
    [InlineData(CliRestartPolicy.NextSend, CliRestartSituation.NoProcess, CliRestartAction.None)]
    [InlineData(CliRestartPolicy.NextSend, CliRestartSituation.Idle, CliRestartAction.NextSend)]
    [InlineData(CliRestartPolicy.NextSend, CliRestartSituation.IdleWithBackground, CliRestartAction.NextSend)]
    [InlineData(CliRestartPolicy.NextSend, CliRestartSituation.AtSafePoint, CliRestartAction.NextSend)]
    [InlineData(CliRestartPolicy.NextSend, CliRestartSituation.ToolRunning, CliRestartAction.NextSend)]
    [InlineData(CliRestartPolicy.NextSend, CliRestartSituation.BackgroundRunning, CliRestartAction.NextSend)]
    [InlineData(CliRestartPolicy.NextSend, CliRestartSituation.WaitingForUser, CliRestartAction.NextSend)]
    public void 政策表_一般換版(object policy, object situation, object expected)
        => CliRestartRules.Decide((CliRestartPolicy)policy, (CliRestartSituation)situation, drain: false, applyNow: false).Should().Be((CliRestartAction)expected);

    [Theory]
    [InlineData(CliRestartSituation.NoProcess, CliRestartAction.None)]
    [InlineData(CliRestartSituation.Idle, CliRestartAction.Close)]
    [InlineData(CliRestartSituation.IdleWithBackground, CliRestartAction.WaitBackground)]
    [InlineData(CliRestartSituation.AtSafePoint, CliRestartAction.Stop)]
    [InlineData(CliRestartSituation.ToolRunning, CliRestartAction.WaitTool)]
    [InlineData(CliRestartSituation.BackgroundRunning, CliRestartAction.WaitBackground)]
    [InlineData(CliRestartSituation.WaitingForUser, CliRestartAction.WaitUser)]
    public void 政策表_必須排空_不看重開政策_執行中的到安全點停下(object situation, object expected)
    {
        foreach (var policy in Enum.GetValues<CliRestartPolicy>())
            CliRestartRules.Decide(policy, (CliRestartSituation)situation, drain: true, applyNow: false).Should().Be((CliRestartAction)expected, $"政策 {policy}：npm 不能在程序執行中換檔");
    }

    [Fact]
    public void app更新排空_等你回應視為安全點_直接停下()
    {
        CliRestartRules.Decide(CliRestartPolicy.SafePoint, CliRestartSituation.WaitingForUser, drain: true, applyNow: false, appUpdate: true)
            .Should().Be(CliRestartAction.Stop);
        CliRestartRules.Decide(CliRestartPolicy.SafePoint, CliRestartSituation.WaitingForUser, drain: true, applyNow: false)
            .Should().Be(CliRestartAction.WaitUser, "CLI 更新的排空照舊等回應");
    }

    [Fact]
    public void app更新排空_開著RemoteControl的等你回應_照CLI更新等回應_立即套用才停下_其他處境不受影響()
    {
        CliRestartRules.Decide(CliRestartPolicy.SafePoint, CliRestartSituation.WaitingForUser, drain: true, applyNow: false, remoteControl: true, appUpdate: true)
            .Should().Be(CliRestartAction.WaitUser, "桌面沒人時使用者可能正在手機上要回應，不收回請求");
        CliRestartRules.Decide(CliRestartPolicy.SafePoint, CliRestartSituation.WaitingForUser, drain: true, applyNow: true, remoteControl: true, appUpdate: true)
            .Should().Be(CliRestartAction.Stop, "排空滿 10 分鐘由中樞以立即套用停下");
        foreach (var policy in Enum.GetValues<CliRestartPolicy>())
        foreach (var situation in Enum.GetValues<CliRestartSituation>().Where(x => x != CliRestartSituation.WaitingForUser))
        foreach (var applyNow in new[] { false, true })
        {
            CliRestartRules.Decide(policy, situation, drain: true, applyNow, remoteControl: true, appUpdate: true)
                .Should().Be(CliRestartRules.Decide(policy, situation, drain: true, applyNow, appUpdate: true),
                    $"{policy}／{situation}：閒置的照常關（app 要結束，程序留不住；中樞記進 resume.json 由新版接回），其他照 app 更新的排空");
        }
    }

    [Theory]
    [InlineData(CliRestartSituation.ToolRunning, CliRestartAction.WaitTool)]
    [InlineData(CliRestartSituation.BackgroundRunning, CliRestartAction.WaitBackground)]
    [InlineData(CliRestartSituation.IdleWithBackground, CliRestartAction.WaitBackground)]
    [InlineData(CliRestartSituation.AtSafePoint, CliRestartAction.Stop)]
    [InlineData(CliRestartSituation.Idle, CliRestartAction.Close)]
    public void app更新排空_其他處境同CLI更新的排空(object situation, object expected)
        => CliRestartRules.Decide(CliRestartPolicy.NextSend, (CliRestartSituation)situation, drain: true, applyNow: false, appUpdate: true).Should().Be((CliRestartAction)expected);

    [Theory]
    [InlineData(CliRestartSituation.NoProcess, CliRestartAction.None, CliRestartAction.None)]
    [InlineData(CliRestartSituation.Idle, CliRestartAction.Close, CliRestartAction.Close)]
    [InlineData(CliRestartSituation.IdleWithBackground, CliRestartAction.Restart, CliRestartAction.Stop)]
    [InlineData(CliRestartSituation.AtSafePoint, CliRestartAction.Restart, CliRestartAction.Stop)]
    [InlineData(CliRestartSituation.ToolRunning, CliRestartAction.Restart, CliRestartAction.Stop)]
    [InlineData(CliRestartSituation.BackgroundRunning, CliRestartAction.Restart, CliRestartAction.Stop)]
    [InlineData(CliRestartSituation.WaitingForUser, CliRestartAction.Restart, CliRestartAction.Stop)]
    public void 政策表_立即套用_不等安全點_三種政策都一樣(object situation, object switching, object draining)
    {
        foreach (var policy in Enum.GetValues<CliRestartPolicy>())
        {
            CliRestartRules.Decide(policy, (CliRestartSituation)situation, drain: false, applyNow: true).Should().Be((CliRestartAction)switching, $"政策 {policy}");
            CliRestartRules.Decide(policy, (CliRestartSituation)situation, drain: true, applyNow: true).Should().Be((CliRestartAction)draining, $"政策 {policy}：屏障的「現在中斷」");
        }
    }

    [Theory]
    [InlineData(CliRestartSituation.NoProcess, false, CliRestartAction.None)]
    [InlineData(CliRestartSituation.Idle, false, CliRestartAction.Close)]
    [InlineData(CliRestartSituation.IdleWithBackground, false, CliRestartAction.WaitBackground)]
    [InlineData(CliRestartSituation.IdleWithBackground, true, CliRestartAction.Close)]
    [InlineData(CliRestartSituation.AtSafePoint, false, CliRestartAction.WaitTurn)]
    [InlineData(CliRestartSituation.ToolRunning, true, CliRestartAction.WaitTurn)]
    [InlineData(CliRestartSituation.BackgroundRunning, false, CliRestartAction.WaitTurn)]
    [InlineData(CliRestartSituation.WaitingForUser, true, CliRestartAction.WaitTurn)]
    public void 政策表_會話不支援重開時_退回這一輪結束後關程序(object situationValue, bool applyNow, object expectedValue)
    {
        var situation = (CliRestartSituation)situationValue;
        var expected = (CliRestartAction)expectedValue;
        foreach (var policy in Enum.GetValues<CliRestartPolicy>())
        {
            foreach (var drain in new[] { false, true })
            {
                var action = CliRestartRules.Decide(policy, situation, drain, applyNow, canRestart: false);
                if (policy == CliRestartPolicy.NextSend && !drain && !applyNow && situation != CliRestartSituation.NoProcess)
                    action.Should().Be(CliRestartAction.NextSend, "下次送出時才用新版：本來就不主動處理");
                else
                    action.Should().Be(expected, $"政策 {policy}、排空 {drain}");
            }
        }
    }

    [Fact]
    public void 政策表_開著RemoteControl的閒置程序_不關改下次送出_排空與立即套用照常_其他情境不受影響()
    {
        foreach (var policy in Enum.GetValues<CliRestartPolicy>())
        {
            CliRestartRules.Decide(policy, CliRestartSituation.Idle, drain: false, applyNow: false, remoteControl: true)
                .Should().Be(CliRestartAction.NextSend, $"政策 {policy}：關了遠端工作階段就斷線");
            CliRestartRules.Decide(policy, CliRestartSituation.Idle, drain: true, applyNow: false, remoteControl: true)
                .Should().Be(CliRestartAction.Close, "npm 不能在程序執行中換檔");
            CliRestartRules.Decide(policy, CliRestartSituation.Idle, drain: false, applyNow: true, remoteControl: true)
                .Should().Be(CliRestartAction.Close, "使用者按了立即套用");
            foreach (var situation in Enum.GetValues<CliRestartSituation>().Where(x => x != CliRestartSituation.Idle))
            foreach (var drain in new[] { false, true })
            foreach (var applyNow in new[] { false, true })
            {
                CliRestartRules.Decide(policy, situation, drain, applyNow, remoteControl: true)
                    .Should().Be(CliRestartRules.Decide(policy, situation, drain, applyNow), $"{policy}／{situation}：執行中的照政策重開（新程序會接回同一個遠端工作階段）");
            }
        }
    }

    [Fact]
    public void 政策表_全組合的不變式()
    {
        foreach (var policy in Enum.GetValues<CliRestartPolicy>())
        foreach (var situation in Enum.GetValues<CliRestartSituation>())
        foreach (var drain in new[] { false, true })
        foreach (var applyNow in new[] { false, true })
        foreach (var canRestart in new[] { false, true })
        {
            var action = CliRestartRules.Decide(policy, situation, drain, applyNow, canRestart);
            var because = $"{policy}／{situation}／排空 {drain}／立即 {applyNow}／可重開 {canRestart}";

            if (situation == CliRestartSituation.NoProcess)
                action.Should().Be(CliRestartAction.None, because + "：沒有程序就不做事");
            if (policy == CliRestartPolicy.NextSend && !drain && !applyNow && situation != CliRestartSituation.NoProcess)
                action.Should().Be(CliRestartAction.NextSend, because + "：下次送出時才用新版，不主動關程序");
            else if (situation == CliRestartSituation.Idle)
                action.Should().Be(CliRestartAction.Close, because + "：閒置的只關程序，不送接續");
            if (!canRestart)
                action.Should().NotBe(CliRestartAction.Restart).And.NotBe(CliRestartAction.Stop, because + "：不支援就不呼叫 RestartAsync");
            if (drain)
                action.Should().NotBe(CliRestartAction.Restart).And.NotBe(CliRestartAction.NextSend, because + "：排空只停下，等更新完才重開");
            else
                action.Should().NotBe(CliRestartAction.Stop, because + "：一般換版不停在半路");
            if (applyNow && canRestart && situation != CliRestartSituation.NoProcess)
                CliRestartRules.WaitingStateOf(action).Should().BeNull(because + "：立即套用不等任何東西");
            if ((situation is CliRestartSituation.ToolRunning or CliRestartSituation.WaitingForUser or CliRestartSituation.BackgroundRunning) && !applyNow)
                action.Should().NotBe(CliRestartAction.Restart).And.NotBe(CliRestartAction.Stop, because + "：工具執行中或等你回應不打斷");
        }
    }

    [Fact]
    public void 等待狀態與協定字串()
    {
        CliRestartRules.WaitingStateOf(CliRestartAction.WaitTool).Should().Be(CliRestartState.WaitingTool);
        CliRestartRules.WaitingStateOf(CliRestartAction.WaitUser).Should().Be(CliRestartState.WaitingUser);
        CliRestartRules.WaitingStateOf(CliRestartAction.WaitBackground).Should().Be(CliRestartState.WaitingBackground);
        CliRestartRules.WaitingStateOf(CliRestartAction.WaitTurn).Should().Be(CliRestartState.WaitingTurn);
        CliRestartRules.WaitingStateOf(CliRestartAction.Restart).Should().BeNull();
        CliRestartRules.WaitingStateOf(CliRestartAction.Close).Should().BeNull();

        Enum.GetValues<CliRestartState>().Select(CliRestartRules.ToProtocol).Should().Equal(
            "waitingTool", "waitingUser", "waitingBackground", "waitingTurn", "waitingUpdate", "restarting", "continued", "notContinued", "failed", "nextSend", "appUpdate");
    }

    [Fact]
    public void 立即套用的按鈕_只在等待類狀態_等背景工作的也有()
    {
        CliRestartRules.CanApplyNow(CliRestartState.WaitingTool).Should().BeTrue();
        CliRestartRules.CanApplyNow(CliRestartState.WaitingUser).Should().BeTrue();
        CliRestartRules.CanApplyNow(CliRestartState.WaitingTurn).Should().BeTrue();
        CliRestartRules.CanApplyNow(CliRestartState.WaitingBackground).Should().BeTrue("不想等到上限（30 分鐘）就自己按");
        foreach (var state in new[] { CliRestartState.WaitingUpdate, CliRestartState.Restarting, CliRestartState.Continued, CliRestartState.NotContinued, CliRestartState.Failed, CliRestartState.NextSend, CliRestartState.AppUpdate })
            CliRestartRules.CanApplyNow(state).Should().BeFalse(state.ToString());

        Enum.GetValues<CliRestartState>().Where(CliRestartRules.IsPending).Should().Equal(
            CliRestartState.WaitingTool, CliRestartState.WaitingUser, CliRestartState.WaitingBackground, CliRestartState.WaitingTurn,
            CliRestartState.WaitingUpdate, CliRestartState.Restarting, CliRestartState.AppUpdate);
        CliRestartRules.MaxConcurrentStarts(AgentProvider.Claude).Should().Be(2);
        CliRestartRules.MaxConcurrentStarts(AgentProvider.Codex).Should().Be(4);
    }

    [Fact]
    public void app更新狀態_協定字串與說明()
    {
        CliRestartRules.ToProtocol(CliRestartState.AppUpdate).Should().Be("appUpdate");
        CliRestartRules.DescribeState(CliRestartState.AppUpdate, AgentProvider.Claude, null).Should().Contain("Clode Myriagent");
        CliRestartRules.IsPending(CliRestartState.AppUpdate).Should().BeTrue();
        CliRestartRules.CanApplyNow(CliRestartState.AppUpdate).Should().BeFalse();
        CliRestartRules.DescribeAppDrainWaiting(CliRestartState.WaitingTool, "npm test").Should().Be("等目前的指令（npm test）完成後停下，讓 Clode Myriagent 更新");
        CliRestartRules.DescribeAppDrainWaiting(CliRestartState.WaitingBackground, null).Should().Be("等背景工作結束後停下，讓 Clode Myriagent 更新（最多等 30 分鐘）");
    }

    [Fact]
    public void 標頭的已等待時間_只有還在等與停下等更新的狀態帶()
    {
        Enum.GetValues<CliRestartState>().Where(CliRestartRules.ShowsWaitTime).Should().Equal(
            CliRestartState.WaitingTool, CliRestartState.WaitingUser, CliRestartState.WaitingBackground, CliRestartState.WaitingTurn,
            CliRestartState.WaitingUpdate, CliRestartState.AppUpdate);
    }

    // ───── 不自動接續（7.7）─────

    private static readonly DateTime Now = new(2026, 10, 10, 12, 0, 0);

    [Fact]
    public void 不自動接續_依序是按過停止_閒置_超過1小時_額度或API錯誤_續接失敗_高風險工具()
    {
        CliRestartRules.WhyNotContinue(false, true, null, Now, false, false, false).Should().BeNull("在執行中、剛被打斷：自動接續");
        CliRestartRules.WhyNotContinue(false, true, Now.AddMinutes(-59), Now, false, false, false).Should().BeNull();

        CliRestartRules.WhyNotContinue(true, true, null, Now, true, true, true).Should().Be(CliNoContinueReason.UserStopped);
        CliRestartRules.WhyNotContinue(false, false, null, Now, true, true, true).Should().Be(CliNoContinueReason.Idle);
        CliRestartRules.WhyNotContinue(false, true, Now.AddMinutes(-61), Now, true, true, true).Should().Be(CliNoContinueReason.TooOld);
        CliRestartRules.WhyNotContinue(false, true, Now.AddMinutes(-60), Now, false, false, false).Should().BeNull("剛好 1 小時還接續（超過才不接）");
        CliRestartRules.WhyNotContinue(false, true, null, Now, true, true, true).Should().Be(CliNoContinueReason.PreviousTurnFailed);
        CliRestartRules.WhyNotContinue(false, true, null, Now, false, true, true).Should().Be(CliNoContinueReason.ContinuationFailed);
        CliRestartRules.WhyNotContinue(false, true, null, Now, false, false, true).Should().Be(CliNoContinueReason.RiskyTool);
    }

    [Fact]
    public void 不自動接續_原因文字與能不能按繼續()
    {
        CliRestartRules.DescribeNoContinue(CliNoContinueReason.RiskyTool, "git push").Should().Be("上一個操作〈git push〉結果未知，請確認後按「繼續」或送出新訊息");
        CliRestartRules.DescribeNoContinue(CliNoContinueReason.RiskyTool).Should().Contain("〈工具〉");
        CliRestartRules.DescribeNoContinue(CliNoContinueReason.UserStopped).Should().Be("你按了停止");
        CliRestartRules.DescribeNoContinue(CliNoContinueReason.TooOld).Should().Contain("超過 1 小時");
        CliRestartRules.DescribeNoContinue(CliNoContinueReason.PreviousTurnFailed).Should().Contain("額度用完或 API 錯誤");
        CliRestartRules.DescribeNoContinue(CliNoContinueReason.ContinuationFailed).Should().Contain("接續沒有成功");

        CliRestartRules.CanContinueManually(CliNoContinueReason.UserStopped).Should().BeFalse("使用者自己停的：送出新訊息即可");
        CliRestartRules.CanContinueManually(CliNoContinueReason.Idle).Should().BeFalse();
        foreach (var reason in new[] { CliNoContinueReason.TooOld, CliNoContinueReason.PreviousTurnFailed, CliNoContinueReason.ContinuationFailed, CliNoContinueReason.RiskyTool })
            CliRestartRules.CanContinueManually(reason).Should().BeTrue(reason.ToString());
    }

    // ───── 高風險操作（IsNonIdempotent）─────

    [Theory]
    [InlineData("git-push", "git push origin main")]
    [InlineData("git-push", "git -C C:\\repo push --force-with-lease")]
    [InlineData("npm-publish", "npm publish --access public")]
    [InlineData("npm-publish", "pnpm publish")]
    [InlineData("dotnet-nuget-push", "dotnet nuget push bin\\Release\\a.nupkg -k $KEY")]
    [InlineData("gh-release", "gh release create v1.2.0")]
    [InlineData("gh-pr-merge", "gh pr merge 42 --squash")]
    [InlineData("deploy-script", "pwsh ./tools/deploy.ps1 -Env prod")]
    [InlineData("deploy-script", "npm run deploy")]
    [InlineData("deploy-script", "firebase deploy --only hosting")]
    [InlineData("deploy-script", "./publish-site.sh")]
    [InlineData("ef-database-update", "dotnet ef database update --project src/Api")]
    [InlineData("sqlcmd", "sqlcmd -S . -d app -i migrate.sql")]
    [InlineData("sqlcmd", "Invoke-Sqlcmd -Query 'select 1'")]
    [InlineData("sql-drop", "psql -c \"DROP TABLE users\"")]
    [InlineData("sql-drop", "echo 'truncate table logs' | mysql")]
    [InlineData("remove-item-recurse", "Remove-Item -Recurse -Force .\\out")]
    [InlineData("remove-item-recurse", "Remove-Item C:\\tmp\\x -Recurse")]
    [InlineData("remove-item-recurse", "rmdir /s /q build")]
    [InlineData("rm-rf", "rm -rf node_modules")]
    [InlineData("rm-rf", "rm -fr ./dist")]
    [InlineData("rm-rf", "rm -Rf /tmp/x")]
    [InlineData("terraform-apply", "terraform apply -auto-approve")]
    [InlineData("kubectl", "kubectl delete pod api-0")]
    [InlineData("http-mutating", "curl -X POST https://api.example.com/items -d '{}'")]
    [InlineData("http-mutating", "curl -s --request DELETE https://api.example.com/items/1")]
    [InlineData("http-mutating", "curl -XPUT https://api.example.com/items/1")]
    [InlineData("http-mutating", "Invoke-RestMethod -Uri https://x -Method Post -Body $b")]
    [InlineData("payment", "node scripts/charge.js --provider stripe")]
    [InlineData("payment", "python 付款.py")]
    public void 高風險_預設規則_指令(string ruleId, string command)
    {
        var match = CliRestartRules.FindRisk("執行指令", "bash", command, command);
        match.Should().NotBeNull(command);
        match!.RuleId.Should().Be(ruleId);
        match.Operation.Should().Be(command, "提示文字用指令的第一行");
        CliRestartRules.IsNonIdempotent("執行指令", "bash", command, command).Should().BeTrue();
    }

    [Theory]
    [InlineData("git status")]
    [InlineData("git diff HEAD~1")]
    [InlineData("npm test")]
    [InlineData("dotnet build -c Release")]
    [InlineData("rm build.log")]
    [InlineData("curl https://example.com/a.json")]
    [InlineData("curl -X GET https://example.com/a.json")]
    [InlineData("Get-ChildItem -Recurse")]
    [InlineData("Start-Sleep 20; Set-Content done.txt done")]
    [InlineData("github-cli --help")]
    public void 高風險_一般指令不符合(string command)
        => CliRestartRules.FindRisk("執行指令", "bash", command, command).Should().BeNull(command);

    [Fact]
    public void 高風險_改檔讀檔搜尋這類工具不比對指令規則()
    {
        CliRestartRules.FindRisk("修改檔案", "edit", @"C:\repo\.github\workflows\deploy.yml", null).Should().BeNull("重做一次修改不會有不可逆的副作用");
        CliRestartRules.FindRisk("讀取檔案", "read", @"C:\repo\scripts\deploy.ps1", null).Should().BeNull();
        CliRestartRules.FindRisk("搜尋內容", "search", "git push", null).Should().BeNull();
        CliRestartRules.FindRisk("讀取網頁", "web", "https://docs.example.com/deploy", null).Should().BeNull();
        CliRestartRules.FindRisk("子代理", "agent", "發佈", "請執行 git push 把修正推上去").Should().NotBeNull("子代理裡可能跑任何指令");
    }

    [Theory]
    [InlineData("create_issue", true)]
    [InlineData("browser_click", true)]
    [InlineData("send_message", true)]
    [InlineData("checkout_branch", true)]
    [InlineData("getaway", true)]
    [InlineData("get_issue", false)]
    [InlineData("getIssue", false)]
    [InlineData("list", false)]
    [InlineData("search_code", false)]
    [InlineData("ReadFile", false)]
    [InlineData("query", false)]
    public void 高風險_MCP工具_名稱不是查詢類動詞開頭就無法判斷副作用(string tool, bool risky)
    {
        var match = CliRestartRules.FindRisk("MCP：github", "mcp", tool, "{ \"owner\": \"me\" }");
        if (risky)
        {
            match.Should().NotBeNull(tool);
            match!.RuleId.Should().Be(CliRestartRules.McpRuleId);
        }
        else
        {
            match.Should().BeNull(tool);
        }
    }

    [Fact]
    public void 高風險_MCP查詢類工具的參數裡有危險指令仍然擋下()
        => CliRestartRules.FindRisk("MCP：db", "mcp", "query", "{ \"sql\": \"DROP TABLE orders\" }")!.RuleId.Should().Be("sql-drop");

    [Fact]
    public void 高風險_設定可停用預設規則與加自訂樣式_寫錯的樣式略過()
    {
        CliRestartRules.FindRisk("執行指令", "bash", "kubectl get pods", null, disabledRules: new[] { "KUBECTL" }).Should().BeNull("停用的 id 不分大小寫");
        CliRestartRules.FindRisk("執行指令", "bash", "git push", null, disabledRules: new[] { "kubectl" })!.RuleId.Should().Be("git-push", "只停用指定的規則");
        CliRestartRules.FindRisk("MCP：github", "mcp", "create_issue", null, disabledRules: new[] { CliRestartRules.McpRuleId }).Should().BeNull();

        var custom = CliRestartRules.FindRisk("執行指令", "bash", "./bin/send-newsletter --all", null, extraPatterns: new[] { "(", "send-newsletter" });
        custom!.RuleId.Should().Be(CliRestartRules.CustomRuleId, "寫錯的「(」略過，下一個樣式照常比對");
        custom.Description.Should().Be("send-newsletter");
        CliRestartRules.FindRisk("修改檔案", "edit", @"C:\repo\prod.env", null, extraPatterns: new[] { @"prod\.env" })
            .Should().NotBeNull("自訂樣式比對所有種類的工具");
        CliRestartRules.FindRisk("執行指令", "bash", "npm test", null, extraPatterns: new[] { "", "  " }).Should().BeNull();

        CliRestartRules.DefaultRiskRuleIds.Should().Contain(new[]
        {
            "git-push", "npm-publish", "dotnet-nuget-push", "gh-release", "gh-pr-merge", "deploy-script", "ef-database-update", "sqlcmd",
            "sql-drop", "remove-item-recurse", "rm-rf", "terraform-apply", "kubectl", "http-mutating", "payment", CliRestartRules.McpRuleId,
        });
        CliRestartRules.DefaultRiskRuleIds.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void 高風險_比對工具名稱與完整輸入_摘要只取第一行()
    {
        var match = CliRestartRules.FindRisk("執行指令", "bash", "cd C:\\repo", "cd C:\\repo\ngit push");
        match!.RuleId.Should().Be("git-push", "多行指令看完整輸入");
        match.Operation.Should().Be("cd C:\\repo");
        CliRestartRules.DescribeOperation("執行指令", null).Should().Be("執行指令");
        CliRestartRules.DescribeOperation(null, "  ").Should().Be("工具");
    }

    // ───── 文字 ─────

    [Fact]
    public void 標頭說明_每種狀態()
    {
        CliRestartRules.DescribeState(CliRestartState.WaitingTool, AgentProvider.Claude, "2.1.297", "npm test").Should().Be("CLI 已更新到 2.1.297，等目前的指令（npm test）完成後切換");
        CliRestartRules.DescribeState(CliRestartState.WaitingTool, AgentProvider.Claude, null).Should().Be("CLI 已更新，等目前的工具完成後切換", "版本不明");
        CliRestartRules.DescribeState(CliRestartState.WaitingUser, AgentProvider.Claude, "2.1.297").Should().Be("CLI 已更新到 2.1.297，等你回應後切換");
        CliRestartRules.DescribeState(CliRestartState.WaitingBackground, AgentProvider.Claude, "2.1.297").Should().Be("CLI 已更新到 2.1.297，等背景工作結束後切換（最多等 30 分鐘）");
        CliRestartRules.DescribeState(CliRestartState.WaitingTurn, AgentProvider.Codex, "0.162.0").Should().Be("CLI 已更新到 0.162.0，這一輪結束後切換");
        CliRestartRules.DescribeState(CliRestartState.WaitingUpdate, AgentProvider.Codex, "0.162.0").Should().Be("等待 Codex 更新完成…");
        CliRestartRules.DescribeState(CliRestartState.Restarting, AgentProvider.Claude, "2.1.297").Should().Be("正在以 Claude Code 2.1.297 重新啟動…");
        CliRestartRules.DescribeState(CliRestartState.Restarting, AgentProvider.Claude, null).Should().Be(CliRestartRules.RestartingStatus);
        CliRestartRules.DescribeState(CliRestartState.Continued, AgentProvider.Codex, "0.162.0").Should().Be("已用 Codex 0.162.0 自動接續");
        CliRestartRules.DescribeState(CliRestartState.Continued, AgentProvider.Codex, null).Should().Be("已用新版 Codex 自動接續");
        CliRestartRules.DescribeState(CliRestartState.NotContinued, AgentProvider.Claude, "2.1.297", "上一個操作〈git push〉結果未知")
            .Should().Be("已換成 Claude Code 2.1.297，這一輪沒有自動接續：上一個操作〈git push〉結果未知");
        CliRestartRules.DescribeState(CliRestartState.Failed, AgentProvider.Claude, "2.1.297", "找不到執行檔\n第二行").Should().Be("以新版 CLI 重新啟動失敗：找不到執行檔");
        CliRestartRules.DescribeState(CliRestartState.NextSend, AgentProvider.Claude, "2.1.297").Should().Be("CLI 已更新到 2.1.297，下次送出時改用新版");
    }

    [Fact]
    public void Notice_自動接續_有沒有工具在執行()
    {
        CliRestartRules.ContinuedNotice(AgentProvider.Claude, "2.1.297", new[] { new AgentInterruptedTool("執行指令", "npm test", TimeSpan.FromSeconds(42.6)) })
            .Should().Be("已用 Claude Code 2.1.297 自動接續（中斷時正在執行：npm test，已執行 42 秒）");
        CliRestartRules.ContinuedNotice(AgentProvider.Codex, "0.162.0", Array.Empty<AgentInterruptedTool>())
            .Should().Be("已用 Codex 0.162.0 自動接續（重啟時沒有工具在執行）");
        CliRestartRules.ContinuedNotice(AgentProvider.Claude, "2.1.297", new[] { new AgentInterruptedTool("執行指令", "rm *.tmp", TimeSpan.FromSeconds(1)) })
            .Should().Contain(@"rm \*\.tmp", "指令放進 Markdown 要跳脫，星號不能變成斜體");
        CliRestartRules.ContinuedNotice(AgentProvider.Claude, "2.1.297", Array.Empty<AgentInterruptedTool>(), fromVersion: "2.1.295")
            .Should().Be("已用 Claude Code 2.1.297 自動接續（重啟時沒有工具在執行）", "真的換了版照舊");
    }

    [Fact]
    public void 版本沒變或放棄app更新_不說已用新版()
    {
        CliRestartRules.SameVersion("2.1.310", "2.1.310").Should().BeTrue();
        CliRestartRules.SameVersion(" 2.1.310", "2.1.310 ").Should().BeTrue();
        CliRestartRules.SameVersion("2.1.309", "2.1.310").Should().BeFalse();
        CliRestartRules.SameVersion(null, "2.1.310").Should().BeFalse("不明的一邊當成不同，照換版的說法");
        CliRestartRules.SameVersion(null, null).Should().BeFalse();

        // CLI 更新取消、失敗或沒換版後以同一版接續
        CliRestartRules.ContinuedNotice(AgentProvider.Claude, "2.1.310", new[] { new AgentInterruptedTool("執行指令", "npm test", TimeSpan.FromSeconds(5)) }, fromVersion: "2.1.310")
            .Should().Be("Claude Code 沒有換版（仍是 2.1.310），已自動接續（中斷時正在執行：npm test，已執行 5 秒）");
        CliRestartRules.DescribeState(CliRestartState.Continued, AgentProvider.Codex, "0.164.0", fromVersion: "0.164.0")
            .Should().Be("Codex 沒有換版（仍是 0.164.0），已自動接續");
        CliRestartRules.DescribeState(CliRestartState.NotContinued, AgentProvider.Claude, "2.1.310", "上一個操作〈git push〉結果未知", fromVersion: "2.1.310")
            .Should().Be("Claude Code 沒有換版（仍是 2.1.310），這一輪沒有自動接續：上一個操作〈git push〉結果未知");
        CliRestartRules.NotContinuedNotice("2.1.310", CliNoContinueReason.RiskyTool, "git push", queuePaused: false, fromVersion: "2.1.310")
            .Should().Be("CLI 沒有換版（仍是 2.1.310）；這一輪沒有自動接續，原因：上一個操作〈git push〉結果未知，請確認後按「繼續」或送出新訊息");

        // 放棄 app 更新後就地接續：講 app 更新；排空期間 CLI 剛好換了版才帶 CLI 的版本
        CliRestartRules.ContinuedNotice(AgentProvider.Claude, "2.1.310", Array.Empty<AgentInterruptedTool>(), fromVersion: "2.1.310", appUpdateAbandoned: true)
            .Should().Be("已放棄這次 Clode Myriagent 更新，已自動接續（重啟時沒有工具在執行）");
        CliRestartRules.DescribeState(CliRestartState.Continued, AgentProvider.Claude, "2.1.310", fromVersion: null, appUpdateAbandoned: true)
            .Should().Be("已放棄這次 Clode Myriagent 更新，已自動接續", "原本的版本不明時不猜 CLI 換了版");
        CliRestartRules.DescribeState(CliRestartState.Continued, AgentProvider.Claude, "2.1.311", fromVersion: "2.1.310", appUpdateAbandoned: true)
            .Should().Be("已放棄這次 Clode Myriagent 更新，改用 Claude Code 2.1.311 自動接續");
        CliRestartRules.DescribeState(CliRestartState.NotContinued, AgentProvider.Claude, "2.1.310", "你按了停止", fromVersion: "2.1.310", appUpdateAbandoned: true)
            .Should().Be("已放棄這次 Clode Myriagent 更新，這一輪沒有自動接續：你按了停止");
        CliRestartRules.NotContinuedNotice("2.1.310", CliNoContinueReason.RiskyTool, "git push", queuePaused: true, fromVersion: "2.1.310", appUpdateAbandoned: true)
            .Should().Be("已放棄這次 Clode Myriagent 更新；這一輪沒有自動接續，原因：上一個操作〈git push〉結果未知，請確認後按「繼續」或送出新訊息（排隊的訊息暫停，按送出繼續）");
    }

    [Fact]
    public void Notice_沒有接續_背景工作_失敗_更新後沒有可用版本()
    {
        CliRestartRules.NotContinuedNotice("2.1.297", CliNoContinueReason.RiskyTool, "git push", queuePaused: false)
            .Should().Be("CLI 已更新到 2.1.297；這一輪沒有自動接續，原因：上一個操作〈git push〉結果未知，請確認後按「繼續」或送出新訊息");
        CliRestartRules.NotContinuedNotice("2.1.297", CliNoContinueReason.UserStopped, null, queuePaused: true)
            .Should().Be("CLI 已更新到 2.1.297；這一輪沒有自動接續，原因：你按了停止（排隊的訊息暫停，按送出繼續）");
        CliRestartRules.BackgroundCheckNotice("2.1.297").Should().Be("CLI 已更新到 2.1.297；切換時停止了還在跑的背景工作，已請代理確認背景工作做到哪裡、需要的話重新執行");
        CliRestartRules.BackgroundNotCheckedNotice("2.1.297", "git push origin main")
            .Should().Be("CLI 已更新到 2.1.297；切換時停止了還在跑的背景工作，沒有自動請代理確認，原因：上一個操作〈git push origin main〉結果未知，請確認後按「繼續」或送出新訊息");
        CliRestartRules.FailedNotice("找不到 session", queuePaused: false).Should().Be("以新版 CLI 重新啟動失敗：找不到 session；按送出繼續");
        CliRestartRules.FailedNotice(null, queuePaused: true).Should().Be("以新版 CLI 重新啟動失敗：原因不明；按送出繼續（排隊的訊息暫停，按送出繼續）");
        CliRestartRules.UpdateUnavailableNotice(AgentProvider.Codex, "安裝不一致", queuePaused: false)
            .Should().Be("Codex 更新後沒有可用的版本（安裝不一致），這一輪沒有接續；修好 CLI 後按送出繼續");
        CliRestartRules.StaleCardOutput.Should().Be("CLI 已重新啟動，這張卡已失效；代理會視需要重新提出", "協定固定的文字");
        CliRestartRules.ManualContinueNotice.Should().Be("已請代理從中斷處繼續（CLI 更新後的接續訊息，不是你送的）");
    }

    [Fact]
    public void 系統匣彙總_一波換版一則()
    {
        CliRestartRules.WaveSummaryTitle(AgentProvider.Claude, "2.1.297").Should().Be("Claude Code 已更新到 2.1.297");
        CliRestartRules.WaveSummaryTitle(AgentProvider.Codex, null).Should().Be("Codex 已更新");
        CliRestartRules.WaveSummaryMessage(2, 1, 0, 0).Should().Be("3 個對話已處理：2 個已自動接續、1 個閒置的已關閉（下次送出用新版）");
        CliRestartRules.WaveSummaryMessage(0, 0, 1, 1).Should().Be("2 個對話已處理：1 個沒有自動接續（請查看對話）、1 個重開失敗");
        CliRestartRules.WaveSummaryMessage(0, 0, 0, 0).Should().Be("沒有需要換版的對話");
    }

    [Fact]
    public void 經過時間與Markdown跳脫()
    {
        CliRestartRules.FormatElapsed(TimeSpan.FromSeconds(42.9)).Should().Be("42 秒");
        CliRestartRules.FormatElapsed(TimeSpan.FromSeconds(-3)).Should().Be("0 秒");
        CliRestartRules.FormatElapsed(TimeSpan.FromSeconds(185)).Should().Be("3 分 5 秒");
        CliRestartRules.FormatElapsed(TimeSpan.FromMinutes(5)).Should().Be("5 分");
        CliRestartRules.FormatElapsed(TimeSpan.FromMinutes(62)).Should().Be("1 小時 2 分");
        CliRestartRules.FormatElapsed(TimeSpan.FromHours(2)).Should().Be("2 小時");

        CliRestartRules.EscapeMarkdown("a*b_c`d[e](f)<g>#|~").Should().Be(@"a\*b\_c\`d\[e\]\(f\)\<g\>\#\|\~");
        CliRestartRules.EscapeMarkdown(@"C:\repo").Should().Be(@"C\:\\repo", "路徑的反斜線照原樣顯示");
        CliRestartRules.EscapeMarkdown("中文　全形〈〉").Should().Be("中文　全形〈〉", "只跳脫 ASCII 標點");
        CliRestartRules.EscapeMarkdown(null).Should().BeEmpty();
    }

    [Fact]
    public void 協定字串_列舉值一律照協定的小駝峰_政策是列舉名稱_setting只接受三個值()
    {
        // HeaderRestart.state、CliView.restartWaiting[].state
        Enum.GetValues<CliRestartState>().Select(s => CliRestartRules.ToProtocol(s)).Should().Equal(
            "waitingTool", "waitingUser", "waitingBackground", "waitingTurn", "waitingUpdate", "restarting", "continued", "notContinued", "failed", "nextSend", "appUpdate");
        // CliView.update.phase（None＝沒有在更新）
        Enum.GetValues<CliUpdatePhase>().Select(p => CliRestartRules.ToProtocol(p)).Should().Equal(
            null, "draining", "waitingProcesses", "installing", "verifying");
        // CliView.lastChange.origin
        Enum.GetValues<CliChangeOrigin>().Select(o => CliRestartRules.ToProtocol(o)).Should().Equal("app", "external");
        // Item.noticeKind
        Enum.GetValues<AgentNoticeKind>().Select(k => CliRestartRules.ToProtocol(k)).Should().Equal("cliUpdate", "appUpdate", "clear", "accountSwitch", "handoff", "fork", "backgroundTask", "backgroundProgram", "overloadRetry", "sideQuestion");
        // Settings.cliRestartPolicy、CliView.restartPolicy 與 setting cliRestartPolicy
        Enum.GetValues<CliRestartPolicy>().Select(p => CliRestartRules.ToProtocol(p)).Should().Equal("SafePoint", "AfterTurn", "NextSend");
        foreach (var policy in Enum.GetValues<CliRestartPolicy>())
            CliRestartRules.ParsePolicy(CliRestartRules.ToProtocol(policy)).Should().Be(policy);
        CliRestartRules.ParsePolicy("safePoint").Should().BeNull("只接受協定的三個值（大小寫要對）");
        CliRestartRules.ParsePolicy("Immediately").Should().BeNull();
        CliRestartRules.ParsePolicy(null).Should().BeNull();
    }
}
