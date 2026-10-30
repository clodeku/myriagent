namespace Myriagent.Tests.TestSupport;

/// <summary>
/// 需要環境變數（值為 1）才執行的整合測試：未設定時顯示為「略過」。
/// 取代 <c>if (!ShouldRun) return;</c> 寫法——那種寫法沒執行也顯示「通過」，容易誤以為真的跑過了。
/// 例：<c>[EnvFact("MYRIAGENT_IT")]</c>，執行時在 Git Bash 下 <c>MYRIAGENT_IT=1 dotnet test ...</c>。
/// </summary>
public sealed class EnvFactAttribute : FactAttribute
{
    /// <param name="variable">要設為 1 的環境變數名稱</param>
    /// <param name="reason">略過訊息括號內的說明</param>
    public EnvFactAttribute(string variable, string reason = "實際呼叫本機 Claude／Codex CLI 的整合測試")
    {
        if (Environment.GetEnvironmentVariable(variable) != "1")
            Skip = $"需設定環境變數 {variable}=1（{reason}）";
    }
}

/// <summary>
/// 需要環境變數指到一個存在的檔案才執行的測試（值是路徑，<see cref="EnvFactAttribute"/> 要求值是 1、裝不下）：沒設定或檔案不在時顯示為「略過」。
/// 例：<c>[EnvFileFact("MYRIAGENT_SYNC_OLD_HUB", "…")]</c>，值是以 <c>git archive 1724718</c> 建出的 MyriagentHub.exe 完整路徑（寫成 <c>C:/…</c>）
/// </summary>
public sealed class EnvFileFactAttribute : FactAttribute
{
    /// <param name="variable">值是檔案完整路徑的環境變數名稱</param>
    /// <param name="reason">略過訊息括號內的說明</param>
    public EnvFileFactAttribute(string variable, string reason)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value))
            Skip = $"需設定環境變數 {variable}＝檔案的完整路徑（{reason}）";
        else if (!File.Exists(value))
            Skip = $"環境變數 {variable} 指的檔案不存在：{value}（{reason}）";
    }
}

/// <summary>同 <see cref="EnvFactAttribute"/>，給 Theory 用</summary>
public sealed class EnvTheoryAttribute : TheoryAttribute
{
    public EnvTheoryAttribute(string variable, string reason = "實際呼叫本機 Claude／Codex CLI 的整合測試")
    {
        if (Environment.GetEnvironmentVariable(variable) != "1")
            Skip = $"需設定環境變數 {variable}=1（{reason}）";
    }
}
