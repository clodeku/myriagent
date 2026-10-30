namespace Myriagent.Sync;

/// <summary>同步的延後啟動與結束（設計 S5 的暖啟動預算：同步不擠在主視窗第一次載入之前）</summary>
internal static class SyncStartupRules
{
    /// <summary>主視窗第一次載入完成（ViewLoaded）之後等多久才啟動同步</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(2.5);

    /// <summary>備援：一直沒有 ViewLoaded（WebView 初始化失敗）時，從 AppController 建好起算多久也啟動一次（Start 重複呼叫無效）</summary>
    public static readonly TimeSpan FallbackDelay = TimeSpan.FromSeconds(10);

    /// <summary>正常結束時等同步引擎停止的時間（逾時就取消）</summary>
    public static readonly TimeSpan ExitStopTimeout = TimeSpan.FromSeconds(3);

    /// <summary>引擎沒照逾時結束時，結束流程最多再等多久就不等了（不能讓 app 關不掉）</summary>
    public static readonly TimeSpan ExitStopGuard = TimeSpan.FromSeconds(5);
}
