using System.Media;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.RemoteExec;

namespace Myriagent.UI;

/// <summary>
/// 高風險的遠端執行指令在這台問人（docs/sync-protocol.md 14.11；2026-10-25 擁有者要求「高風險例如 UAC 的操作都還是要到該電腦操作同意」）：
/// 置頂的對話框列出發起的電腦、對話、高風險的原因與完整的指令，預設焦點在「拒絕」（按 Enter 不會誤允許），倒數到 0、按 X 或 Esc 都當成拒絕。
/// app 藏在系統匣時照樣跳出來（不依附主視窗）。UI 執行緒
/// </summary>
internal sealed class RemoteExecConfirmer(IAgentHost host) : IRemoteExecConfirmer
{
    public Task<RemoteExecConfirmResult> ConfirmAsync(RemoteExecConfirmation confirmation, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        var result = new TaskCompletionSource<RemoteExecConfirmResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BeginInvoke(() =>
        {
            try
            {
                var form = new RemoteExecConfirmForm(confirmation, timeout);
                form.FormClosed += (_, _) =>
                {
                    result.TrySetResult(form.Result);
                    form.Dispose();
                };
                var registration = cancellationToken.Register(() => host.BeginInvoke(() =>
                {
                    if (!form.IsDisposed)
                        form.CloseWith(RemoteExecConfirmResult.Denied);
                }));
                form.FormClosed += (_, _) => registration.Dispose();
                form.Show();
                form.Activate();
                SystemSounds.Exclamation.Play();
            }
            catch (Exception ex)
            {
                Log.Error("遠端執行指令的確認視窗開不起來", ex);
                result.TrySetResult(RemoteExecConfirmResult.Denied);
            }
        });
        return result.Task;
    }
}

/// <summary>確認視窗本身</summary>
internal sealed class RemoteExecConfirmForm : Form
{
    private readonly System.Windows.Forms.Timer countdown = new() { Interval = 1000 };
    private readonly Label remaining;
    private readonly DateTime deadline;

    public RemoteExecConfirmForm(RemoteExecConfirmation confirmation, TimeSpan timeout)
    {
        var request = confirmation.Request;
        deadline = DateTime.UtcNow + timeout;
        Text = "遠端指令需要你同意 — Clode Myriagent";
        Icon = AppIcons.Window;
        TopMost = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? Font;
        ClientSize = new Size(620, 430);
        Padding = new Padding(16);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var headline = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(580, 0),
            Font = new Font(Font.FontFamily, Font.Size + 2, FontStyle.Bold),
            Text = $"{confirmation.IssuerLabel} 的代理要在這台執行一個高風險的指令",
            Margin = new Padding(0, 0, 0, 8),
        };
        var risk = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(580, 0),
            ForeColor = Color.Firebrick,
            Text = $"高風險：{confirmation.Risk.Category}（符合「{confirmation.Risk.Match}」）",
            Margin = new Padding(0, 0, 0, 6),
        };
        var facts = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(580, 0),
            Text = (request.Label is { } label ? $"對話：{label}\n" : string.Empty)
                   + $"殼層：{request.Shell}　工作資料夾：{request.Cwd ?? "（使用者的家目錄）"}　上限：{request.TimeoutSeconds} 秒\n"
                   + "以這台目前登入的使用者、一般權限執行；需要系統管理員的操作另外會跳出 Windows 的 UAC。",
            Margin = new Padding(0, 0, 0, 8),
        };
        var command = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", Font.Size + 1),
            Text = request.Command.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal),
            TabStop = false,
        };
        remaining = new Label { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };

        var deny = new Button { Text = "拒絕", AutoSize = true, MinimumSize = new Size(110, 34), DialogResult = DialogResult.None };
        var allow = new Button { Text = "允許執行", AutoSize = true, MinimumSize = new Size(110, 34), DialogResult = DialogResult.None };
        deny.Click += (_, _) => CloseWith(RemoteExecConfirmResult.Denied);
        allow.Click += (_, _) => CloseWith(RemoteExecConfirmResult.Allowed);
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0, 10, 0, 0),
        };
        buttons.Controls.Add(deny);
        buttons.Controls.Add(allow);

        layout.Controls.Add(headline, 0, 0);
        layout.Controls.Add(risk, 0, 1);
        layout.Controls.Add(facts, 0, 2);
        layout.Controls.Add(command, 0, 3);
        layout.Controls.Add(remaining, 0, 4);
        layout.Controls.Add(buttons, 0, 5);
        Controls.Add(layout);

        CancelButton = deny;
        ActiveControl = deny;
        countdown.Tick += (_, _) => Tick();
        Shown += (_, _) =>
        {
            deny.Focus();
            countdown.Start();
        };
        remaining.Text = Remaining(timeout);
    }

    /// <summary>使用者的回應（關掉之前沒選的是拒絕）</summary>
    public RemoteExecConfirmResult Result { get; private set; } = RemoteExecConfirmResult.Denied;

    public void CloseWith(RemoteExecConfirmResult result)
    {
        Result = result;
        countdown.Stop();
        Close();
    }

    private void Tick()
    {
        var left = deadline - DateTime.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            CloseWith(RemoteExecConfirmResult.TimedOut);
            return;
        }

        remaining.Text = Remaining(left);
    }

    private static string Remaining(TimeSpan left) => $"{(int)left.TotalMinutes}:{left.Seconds:00} 後沒有回應就自動拒絕";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            countdown.Dispose();
        base.Dispose(disposing);
    }
}
