using Myriagent.Core;
using Myriagent.Diagnostics;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 網頁的錯誤回報（web-protocol 第二節 clientError，2026-10-23）：沒被接住的例外、沒處理的 Promise 拒絕、圖片與影片載入失敗寫進這台的記錄檔，
// 同一件事 10 分鐘內只記一次（網頁每次重畫都可能再載入同一張破圖）。不合格的回報略過（不回 toast：使用者沒做任何事）。只在 UI 執行緒
internal sealed partial class AgentHub
{
    private readonly LogThrottle clientErrorThrottle = new(TimeSpan.FromMinutes(10), TimeProvider.System);

    private void ReportClientError(IAgentView view, JObject command)
    {
        if (ClientErrorRules.TryParse(command) is not { } report)
        {
            Log.Info("網頁送來的 clientError 格式不合格，略過");
            return;
        }

        if (!clientErrorThrottle.ShouldLog(ClientErrorRules.ThrottleKey(report), out var suppressed))
            return;
        Log.Warn($"網頁回報（{ViewLabel(view)}）{ClientErrorRules.Describe(report)}{LogThrottle.SuppressedNote(suppressed)}");
    }

    private static string ViewLabel(IAgentView view) => view.IsPopout ? "彈出視窗" : WindowRules.IsPrimary(view) ? "主視窗" : "其他視窗";
}
