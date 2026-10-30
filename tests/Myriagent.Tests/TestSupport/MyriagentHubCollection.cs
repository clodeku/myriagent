namespace Myriagent.Tests.TestSupport;

/// <summary>用 MyriagentHub 子程序的測試循序執行（同一時間只有一個測試類別的 MyriagentHub 在跑）</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MyriagentHubCollection
{
    public const string Name = "MyriagentHub 子程序（循序執行）";
}
