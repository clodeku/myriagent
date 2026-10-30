namespace Myriagent.UI;

/// <summary>
/// UI 執行緒要知道路徑存不存在時用：檢查放到背景執行緒，最多等一小段時間。
/// 磁碟代號也可能是對應的網路磁碟（Z:\…，本機路徑規則擋不掉），離線時 File.Exists／Directory.Exists 會卡到 SMB 逾時（約 40 秒），
/// 不能讓整個 app（所有視窗、串流更新、系統匣選單）跟著凍結
/// </summary>
internal static class PathProbe
{
    /// <summary>開對話框、開檔案總管前最多等多久（本機磁碟只要幾毫秒；等不到就當作不知道）</summary>
    public static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(2);

    /// <summary>在背景執行 check，timeout 內完成就回結果；逾時或失敗回 null（呼叫端當作「不知道」）。呼叫端最多被擋 timeout 這麼久</summary>
    public static bool? Check(Func<bool> check, TimeSpan timeout)
    {
        var task = Task.Run(check);
        try
        {
            return task.Wait(timeout) ? task.Result : null;
        }
        catch (AggregateException)
        {
            return null;
        }
    }

    /// <summary>同 Check，但不擋住呼叫端（在 UI 執行緒 await 完會回到 UI 執行緒）</summary>
    public static async Task<bool?> CheckAsync(Func<bool> check, TimeSpan timeout)
    {
        try
        {
            return await Task.Run(check).WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }
}
