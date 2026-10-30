using Myriagent.UI;

namespace Myriagent.Tests.UI;

/// <summary>
/// 主視窗工作列按鈕的狀態（TaskbarRules）：等你回應優先於執行中、疊加的數字與說明、關掉動態效果時執行中不流動；
/// 以及真的呼叫 ITaskbarList3（藏起來的表單，沒有工作列按鈕時不能丟例外）
/// </summary>
public sealed class TaskbarRulesTests
{
    [Fact]
    public void 等你回應優先_執行中是流動的綠色_都沒有就清掉()
    {
        TaskbarRules.Decide(0, 0, true).Should().Be(new TaskbarView(TaskbarState.None, false, 0));
        TaskbarRules.Decide(2, 0, true).Should().Be(new TaskbarView(TaskbarState.Running, true, 0));
        TaskbarRules.Decide(2, 1, true).Should().Be(new TaskbarView(TaskbarState.Waiting, false, 1), "要你動手的先講");
        TaskbarRules.Decide(0, 3, true).Should().Be(new TaskbarView(TaskbarState.Waiting, false, 3));
    }

    [Fact]
    public void 關掉動態效果_執行中改成靜止的滿格()
    {
        TaskbarRules.Decide(1, 0, false).Should().Be(new TaskbarView(TaskbarState.Running, false, 0));
    }

    [Fact]
    public void 疊加的數字最多寫到9_說明念出對話數()
    {
        TaskbarRules.OverlayText(0).Should().BeEmpty();
        TaskbarRules.OverlayText(1).Should().Be("1");
        TaskbarRules.OverlayText(9).Should().Be("9");
        TaskbarRules.OverlayText(10).Should().Be("9+");
        TaskbarRules.OverlayDescription(0).Should().BeEmpty();
        TaskbarRules.OverlayDescription(3).Should().Be("3 個對話等你回應");
    }

    [Fact]
    public void 真的呼叫工作列_沒有按鈕的藏起來的表單也不丟例外()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new Form { ShowInTaskbar = false };
                _ = form.Handle; // 建立視窗但不顯示：沒有工作列按鈕
                using var taskbar = new TaskbarProgress();
                foreach (var view in new[] { TaskbarRules.Decide(1, 0, true), TaskbarRules.Decide(1, 2, true), TaskbarRules.Decide(0, 12, false), TaskbarRules.Decide(0, 0, true) })
                    taskbar.Apply(form, view);
                taskbar.Reapply(form);
            }
            catch (Exception e)
            {
                error = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(20)).Should().BeTrue();
        error.Should().BeNull();
    }

    /// <summary>
    /// 有工作列按鈕的視窗（畫面外）：Windows 收下進度與疊加圖示（MYRIAGENT_TASKBAR_IT=1 才跑：測試期間工作列會多一個按鈕約 3 秒）
    /// </summary>
    [Fact]
    public void 真的工作列按鈕_進度與疊加圖示都被收下()
    {
        if (Environment.GetEnvironmentVariable("MYRIAGENT_TASKBAR_IT") != "1")
            return;
        Exception? error = null;
        var results = new List<bool>();
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new Form { ShowInTaskbar = true, StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000), Size = new Size(200, 100), Text = "TaskbarProbe" };
                using var taskbar = new TaskbarProgress();
                form.Shown += async (_, _) =>
                {
                    await Task.Delay(800);
                    foreach (var view in new[] { TaskbarRules.Decide(1, 0, true), TaskbarRules.Decide(1, 3, true), TaskbarRules.Decide(0, 12, true), TaskbarRules.Decide(0, 0, true) })
                    {
                        taskbar.Apply(form, view);
                        results.Add(taskbar.LastPushOk);
                        await Task.Delay(500);
                    }
                    form.Close();
                };
                Application.Run(form);
            }
            catch (Exception e)
            {
                error = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue();
        error.Should().BeNull();
        results.Should().Equal(true, true, true, true);
    }
}
