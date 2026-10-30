namespace Myriagent.Hub;

/// <summary>旁支提問（/btw）那則提示的文字（Notice，noticeKind sideQuestion；Markdown）</summary>
internal static class SideQuestionRules
{
    /// <summary>還在等答案時的那一行</summary>
    public const string PendingLine = "*思考中…*";

    /// <summary>答案回來之前 app 就結束了（載入時 ItemLoadRules.FinalizeOnLoad 換掉 PendingLine）</summary>
    public const string UnansweredLine = "沒有回答：答案回來之前 app 就結束了";

    /// <summary>
    /// 問題以引用區塊列在前面（每一行都加「&gt; 」），下面是答案；還沒有答案是 PendingLine，失敗是「沒有回答：原因」。
    /// 問題與答案都是 Markdown（Markdig 停用原始 HTML）
    /// </summary>
    public static string Text(string question, string? answer, string? error)
    {
        var quoted = string.Join("\n", question.Replace("\r\n", "\n").Split('\n').Select(line => "> " + line));
        var body = answer is { Length: > 0 } ? answer.Trim() : error != null ? $"沒有回答：{error}" : PendingLine;
        return $"{quoted}\n\n{body}";
    }
}
