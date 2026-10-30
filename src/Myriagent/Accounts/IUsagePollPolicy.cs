namespace Myriagent.Accounts;

/// <summary>
/// 用量查詢的閘門（設計 6.6、S12）：同一個訂閱身分在多台電腦登入時，只有被伺服器指派的那台查官方用量端點，
/// 其他台顯示共享的用量。定期輪詢與使用者手動「更新用量」都走同一個閘門。實作是同步協調者（未配對時一律回 true）
/// </summary>
internal interface IUsagePollPolicy
{
    /// <summary>UI 執行緒呼叫；false＝這次不查官方用量端點（不標過期、不改用量）</summary>
    bool ShouldPoll(AgentAccount account);
}
