using System.Reflection;
using Myriagent.Update;

namespace Myriagent.Tests.Update;

public sealed class ReleaseKeysBuildTests
{
    /// <summary>端到端工具建置時會把測試金鑰寫進組件；一般建置（測試、發佈）絕不能帶著它</summary>
    [Fact]
    public void 一般建置的桌面組件沒有測試金鑰()
        => typeof(AppVersionInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Should().NotContain(a => a.Key == ReleaseKeys.TestKeyMetadata);
}
