namespace MyriagentHub.Tests.TestSupport;

/// <summary>
/// 需要環境變數（值為 1）才執行的測試：未設定時顯示為「略過」（照 Myriagent.Tests 的同名屬性，不參考那個專案）。
/// 取代 <c>if (!ShouldRun) return;</c> 寫法——那種寫法沒執行也顯示「通過」，容易誤以為真的跑過了。
/// 例：<c>[EnvFact("MYRIAGENT_HUB_PERF", "量測")]</c>，執行時在 Git Bash 下 <c>MYRIAGENT_HUB_PERF=1 dotnet test ...</c>
/// </summary>
public sealed class EnvFactAttribute : FactAttribute
{
    /// <param name="variable">要設為 1 的環境變數名稱</param>
    /// <param name="reason">略過訊息括號內的說明</param>
    public EnvFactAttribute(string variable, string reason)
    {
        if (Environment.GetEnvironmentVariable(variable) != "1")
            Skip = $"需設定環境變數 {variable}=1（{reason}）";
    }
}

/// <summary>
/// 需要環境變數指向一個檔案才執行的測試（例如 <c>MYRIAGENT_SYNC_OLD_HUB</c>＝舊組建的 MyriagentHub.exe 完整路徑）：沒設定（或是空的）時略過。
/// 設了但檔案不存在時照樣執行，由測試自己斷言檔案存在而失敗——設錯路徑不會被誤當成「略過」
/// </summary>
public sealed class EnvPathFactAttribute : FactAttribute
{
    /// <param name="variable">值是檔案完整路徑的環境變數名稱</param>
    /// <param name="reason">略過訊息括號內的說明</param>
    public EnvPathFactAttribute(string variable, string reason)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
            Skip = $"需設定環境變數 {variable}（{reason}）";
    }
}
