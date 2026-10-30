using Myriagent.Update;

namespace MyriagentHub.Releases;

/// <summary>
/// 收件驗發佈簽章的信任來源（U2 設計 D1）：只有編譯進程式的正式金鑰，沒有任何設定檔、指令或端點能加信任金鑰。
/// 這個介面只是讓測試換成夾具的暫時金鑰（HubTestHost.StartAsync 的 services 回呼登記 TestBuiltInReleaseKeys）
/// </summary>
internal interface IBuiltInReleaseKeys
{
    IReadOnlyList<ReleaseKey> Keys { get; }
}

/// <summary>正式的內建金鑰。MyriagentHub 只有這個檔引用 ReleaseKeys.Production（AppReleaseInboxTests 掃原始碼把關）</summary>
internal sealed class ProductionBuiltInReleaseKeys : IBuiltInReleaseKeys
{
    public IReadOnlyList<ReleaseKey> Keys => ReleaseKeys.Production;
}
