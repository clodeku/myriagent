using Myriagent.Update;
using MyriagentHub.Releases;

namespace MyriagentHub.Tests.TestSupport;

/// <summary>
/// 測試換掉內建發佈金鑰的接縫（U2 T4）：把 <see cref="ReleasePackageFixture.Keys"/> 這類暫時金鑰包成 <see cref="IBuiltInReleaseKeys"/>，
/// 以 HubTestHost.StartAsync 的 services 回呼登記（正式的登記之後再登記，以後登記的為準）
/// </summary>
internal sealed class TestBuiltInReleaseKeys(IReadOnlyList<ReleaseKey> keys) : IBuiltInReleaseKeys
{
    public IReadOnlyList<ReleaseKey> Keys { get; } = keys;
}
