using System.Globalization;
using System.Text;

namespace Myriagent.Schedules;

/// <summary>交給代理的排程資料（中樞在 UI 執行緒組好）</summary>
internal sealed record ScheduleBrief(
    ScheduledTaskInfo Task,
    string Machine,
    ScheduleOrigin Origin,
    ScheduleOriginEntry? OriginEntry,
    IReadOnlyList<ScheduleRunRecord> Runs,
    IReadOnlyList<ScheduleChangeEntry> Changes,
    string? WorkFolder);

/// <summary>
/// 排程頁「交給代理」的說明（放進草稿或自動送出）：排程的資料、最近的執行、完整定義、要做的事、固定的規則。
/// task：diagnose（診斷最近一次失敗）、explain（說明它在做什麼）、retime（改執行時間）、retire（停用並清理）、custom（自訂）、auto（自動診斷）
/// </summary>
internal static class ScheduleAgentPromptRules
{
    public static readonly IReadOnlyList<string> Tasks = new[] { "diagnose", "explain", "retime", "retire", "custom", "auto" };

    public static bool IsTask(string? task) => task != null && Tasks.Contains(task);

    /// <summary>畫面上顯示的那一則（自動診斷以 displayText 送出：代理看完整說明，對話裡顯示這句）</summary>
    public static string AutoDisplay(ScheduleBrief b, ScheduleRunRecord run)
        => $"（自動診斷）排程 {b.Task.Path} 在 {run.At:M/d HH:mm} 執行失敗（{ScheduleResultRules.Classify(run.Code).Text}），請找出原因，先不要修改。";

    public static string Build(string task, ScheduleBrief b)
    {
        var sb = new StringBuilder();
        sb.Append("這是 Clode Myriagent 排程頁交給你的工作，對象是這台電腦（").Append(b.Machine).Append("）的 Windows 排程。\n\n");
        AppendTask(sb, task, b);
        sb.Append("\n## 排程\n\n");
        AppendFacts(sb, b);
        AppendRuns(sb, b);
        AppendChanges(sb, b);
        if (!string.IsNullOrWhiteSpace(b.Task.Xml))
        {
            sb.Append("\n## 完整定義（工作排程器的 XML）\n\n```xml\n").Append(b.Task.Xml!.Trim()).Append("\n```\n");
        }
        sb.Append("\n## 規則\n\n");
        sb.Append("- 改排程一律用 `schtasks` 或 PowerShell 的 ScheduledTasks 模組（Register-／Set-／Unregister-／Enable-／Disable-ScheduledTask）。\n");
        sb.Append("- 刪除排程、改執行身分或提高權限之前，先問我。要系統管理員權限的操作會跳 UAC，請先告訴我原因。\n");
        sb.Append("- 改完不必再列出排程清單：Myriagent 會在這一輪結束後自動重新掃描，排程頁會記下這個對話做的變更。\n");
        return sb.ToString();
    }

    private static void AppendTask(StringBuilder sb, string task, ScheduleBrief b)
    {
        var last = b.Runs.Count > 0 ? b.Runs[^1] : null;
        var lastText = last != null ? $"{last.At:M/d HH:mm} 的{ScheduleResultRules.Classify(last.Code).Text}" : ScheduleResultRules.Classify(b.Task.LastResult).Text;
        sb.Append("## 要做的事\n\n");
        switch (task)
        {
            case "diagnose":
                sb.Append($"這個排程最近一次執行失敗（{lastText}）。請找出原因：先看定義與它執行的程式、腳本與記錄檔（如果有寫記錄），必要時用同樣的指令與工作資料夾手動試跑一次。說明原因並提出修正方式，修正前先跟我確認。\n");
                break;
            case "auto":
                sb.Append($"這個排程剛剛執行失敗（{lastText}），Myriagent 自動請你診斷。**只診斷、不要修改任何檔案或排程**：看定義、它執行的程式與腳本、記錄檔，必要時可以用同樣的指令手動試跑（試跑本身不能改資料或對外發送）。最後列出原因與建議的修法，等我決定。\n");
                break;
            case "explain":
                sb.Append("請說明這個排程在做什麼：它執行的程式或腳本做了哪些事、讀寫哪些資料、多久跑一次、失敗會有什麼影響、有沒有更好的做法。不要修改任何東西。\n");
                break;
            case "retime":
                sb.Append("我想改這個排程的執行時間。新的時間：（請在這裡寫，例如「每天 03:00」「平日每小時」）\n\n改之前確認新的時間不會和這台電腦的其他排程衝突，改完說明下一次會在什麼時候執行。\n");
                break;
            case "retire":
                sb.Append("這個排程不再需要了。請先確認沒有其他東西依賴它（別的排程、服務、網站、其他程式），然後停用它；跟我確認之後再刪除排程，並清理只為它存在的檔案（腳本、記錄、設定）。repo 裡有版控的檔案要刪之前先問我。\n");
                break;
            default:
                sb.Append("我想要：（請在這裡寫）\n");
                break;
        }
    }

    private static void AppendFacts(StringBuilder sb, ScheduleBrief b)
    {
        var t = b.Task;
        var d = t.Definition;
        sb.Append("- 名稱：`").Append(t.Path).Append("`\n");
        sb.Append("- 狀態：").Append(StateText(t)).Append('\n');
        if (d != null)
        {
            sb.Append("- 何時：").Append(ScheduleTriggerRules.Describe(d.Triggers)).Append('\n');
            sb.Append("- 動作：`").Append(ScheduleClassifyRules.ActionText(d.Actions)).Append("`\n");
        }
        if (t.NextRun is { } next)
            sb.Append("- 下次執行：").Append(next.ToString("yyyy/M/d HH:mm", CultureInfo.InvariantCulture)).Append('\n');
        if (b.WorkFolder != null)
            sb.Append("- 工作資料夾：`").Append(b.WorkFolder).Append("`\n");
        if (t.RepoRoot != null)
            sb.Append("- 所在的 git repo：`").Append(t.RepoRoot).Append("`\n");
        if (d != null)
            sb.Append("- 執行身分：").Append(RunAsText(d)).Append('\n');
        if (t.MissedRuns > 0)
            sb.Append("- 錯過的執行：").Append(t.MissedRuns).Append(" 次\n");
        sb.Append("- 來源：").Append(OriginText(b)).Append('\n');
        if (d?.Description is { } description)
            sb.Append("- 描述：").Append(description).Append('\n');
    }

    private static void AppendRuns(StringBuilder sb, ScheduleBrief b)
    {
        if (b.Runs.Count == 0)
        {
            var r = ScheduleResultRules.Classify(b.Task.LastResult);
            if (b.Task.LastRun is { } at)
                sb.Append("\n## 上次執行\n\n- ").Append(at.ToString("M/d HH:mm", CultureInfo.InvariantCulture)).Append("：").Append(r.Text).Append('\n');
            return;
        }
        sb.Append("\n## 最近的執行（Myriagent 觀察到的，新的在前）\n\n");
        foreach (var run in b.Runs.Reverse().Take(10))
        {
            var r = ScheduleResultRules.Classify(run.Code);
            sb.Append("- ").Append(run.At.ToString("M/d HH:mm", CultureInfo.InvariantCulture)).Append("：").Append(r.Text);
            if (r.Level == ScheduleResultLevel.Fail && !r.Text.StartsWith("結束碼", StringComparison.Ordinal))
                sb.Append("（").Append(ScheduleResultRules.CodeText(run.Code)).Append('）');
            if (run.Gap)
                sb.Append("（和前一次之間還有沒觀察到的執行）");
            sb.Append('\n');
        }
    }

    private static void AppendChanges(StringBuilder sb, ScheduleBrief b)
    {
        if (b.Changes.Count == 0)
            return;
        sb.Append("\n## 變更紀錄\n\n");
        foreach (var c in b.Changes.Reverse().Take(10))
        {
            sb.Append("- ").Append(c.At.ToString("yyyy/M/d HH:mm", CultureInfo.InvariantCulture)).Append(' ').Append(ChangeText(c.Kind));
            if (c.Title != null)
                sb.Append("（對話「").Append(c.Title).Append("」）");
            sb.Append('\n');
        }
    }

    /// <summary>要代理做的事的名稱（遠端交給代理時對話裡顯示）</summary>
    public static string TaskLabel(string task) => task switch
    {
        "diagnose" => "診斷最近一次失敗",
        "explain" => "說明它在做什麼",
        "retime" => "改執行時間",
        "retire" => "停用並清理",
        "auto" => "自動診斷",
        _ => "自訂",
    };

    public static string ChangeText(string kind) => kind switch
    {
        "created" => "建立",
        "changed" => "修改定義",
        "deleted" => "刪除",
        "enabled" => "啟用",
        "disabled" => "停用",
        _ => kind,
    };

    public static string StateText(ScheduledTaskInfo t)
    {
        if (t.Error != null && t.Definition == null)
            return "讀不到";
        return t.State switch
        {
            ScheduledTaskState.Running => "執行中",
            ScheduledTaskState.Disabled => "已停用",
            ScheduledTaskState.Queued => "排隊中",
            ScheduledTaskState.Ready => "已啟用",
            _ => t.Enabled ? "已啟用" : "已停用",
        };
    }

    /// <summary>執行身分：「只在使用者登入時」「不論使用者是否登入」，加上以最高權限</summary>
    public static string RunAsText(ScheduleDefinition d)
    {
        var who = d.RunAs ?? "（未指定）";
        var when = d.LogonType switch
        {
            "InteractiveToken" => "只在使用者登入時執行",
            "Password" => "不論使用者是否登入都執行（存了密碼）",
            "S4U" => "不論使用者是否登入都執行（不存密碼）",
            "ServiceAccount" => "以服務帳戶執行",
            "Group" => "群組成員登入時執行",
            null => "",
            _ => d.LogonType,
        };
        var text = when.Length > 0 ? $"{who}，{when}" : who;
        return d.Elevated ? text + "，以最高權限執行" : text;
    }

    public static string OriginText(ScheduleBrief b)
    {
        var o = b.OriginEntry;
        if (o != null)
        {
            var verb = o.Verb == "created" ? "建立" : "修改";
            var where = o.Source == "transcript" ? "外部 CLI 的工作階段" : "Myriagent 的對話";
            var title = o.Title != null ? $"「{o.Title}」" : "";
            var provider = o.Provider != null ? $"（{o.Provider}）" : "";
            return $"{o.At:yyyy/M/d} 由{where}{title}{provider}{verb}";
        }
        return b.Origin switch
        {
            ScheduleOrigin.Adopted => "使用者納入管理（不知道是誰建立的）",
            ScheduleOrigin.Suspected => "不知道是誰建立的，看起來是自己開發的（執行的是使用者資料夾裡的程式或腳本）",
            _ => "不知道是誰建立的",
        };
    }
}
