namespace Myriagent.Core;

/// <summary>
/// 讀子程序輸出管線的專用背景執行緒（不用執行緒集區）。
/// .NET 在 Windows 上把子程序重新導向的 stdout／stderr 接成同步的匿名管線：<c>ReadLineAsync</c>、<c>BeginOutputReadLine</c>
/// 與 <c>Task.Run</c> 裡的同步讀，其實都是在執行緒集區佔一條執行緒卡在 ReadFile 上，程序活多久就佔多久（stdout、stderr 各一條）。
/// 開著十幾個閒置的 CLI 程序就把集區的最小執行緒數用完，之後每個背景工作（計時器回到 UI 執行緒、檢查資料夾、同步的連線、
/// 媒體讀檔）都排隊等集區每秒補一兩條執行緒，送出的訊息要好幾秒才上畫面（2026-10-23 實測：40 個閒置程序時突發的短工作要等 1.2 秒）。
/// 長時間存活的程序一律用這裡讀；跑完就結束的短命程序（版本查詢、ffmpeg）不必
/// </summary>
internal static class PipeReadThread
{
    /// <summary>
    /// 在新的背景執行緒執行 <paramref name="body"/>（同步讀到管線結束為止），回傳它結束時完成的工作。
    /// 執行緒上沒接住的例外會讓整個程序當掉，所以這裡一律接住、記錄，工作照樣完成
    /// </summary>
    public static Task Start(string name, Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                Log.Warn($"讀取子程序輸出的執行緒（{name}）失敗：{ex.Message}");
            }
            finally
            {
                done.TrySetResult();
            }
        })
        {
            IsBackground = true,
            Name = name,
        };
        thread.Start();
        return done.Task;
    }
}
