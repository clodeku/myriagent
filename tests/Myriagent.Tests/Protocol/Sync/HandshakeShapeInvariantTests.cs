using Myriagent.Sync;
using Myriagent.Update;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>
/// docs/sync-protocol.md 1.4 的不變式（app 自我更新規格 7.5）：Hello、Prove、RefreshAccessToken 的請求與回應，以及 app 下載端點
/// （第 15 節）的形狀永遠只加選用欄位，任何舊版桌面都連得上、拿得到只更新的連線、下載得到新版。線上格式由 record 的主建構子決定
/// （Newtonsoft 以它反序列化），所以凍結建構子：凍結的參數不能改名、換型別、換位置或刪掉，選用的不能改成必填，之後加的一律要有預設值。
/// 協定支援的範圍、新的錯誤代碼與通知種類的值也在這裡固定
/// </summary>
public sealed class HandshakeShapeInvariantTests
{
    /// <summary>Required＝凍結的必填參數；Optional＝凍結的選用參數（名稱、型別、位置同樣凍結，而且要有預設值）</summary>
    private sealed record Shape(Type Type, (string Name, Type Type)[] Required, (string Name, Type Type)[] Optional);

    private static readonly Shape[] Shapes =
    [
        new(typeof(HelloRequest),
            [
                ("ProtocolVersion", typeof(int)), ("DeviceId", typeof(string)), ("BootId", typeof(string)), ("AppVersion", typeof(string)),
                ("MachineName", typeof(string)), ("WindowsUser", typeof(string)), ("NonceD", typeof(string)), ("ObservedSpkiSha256", typeof(string)),
                ("SeenServer", typeof(ServerInstance)), ("Outbox", typeof(OutboxPosition)), ("Cursors", typeof(IReadOnlyDictionary<string, long>)),
                ("Policy", typeof(DevicePolicy)),
            ],
            [("Features", typeof(IReadOnlyList<string>))]),
        new(typeof(HelloResponse),
            [
                ("ServerTime", typeof(long)), ("NonceS", typeof(string)), ("Server", typeof(ServerInstance)), ("HubSig", typeof(string)),
                ("HubIdentitySpki", typeof(string)), ("TrustRevs", typeof(TrustRevs)), ("Limits", typeof(SyncLimits)), ("MinAppVersion", typeof(string)),
            ],
            [("Rotation", typeof(HubIdentityRotation)), ("Features", typeof(IReadOnlyList<string>)), ("LatestApp", typeof(AppReleaseInfo))]),
        new(typeof(ProveRequest), [("DeviceSig", typeof(string))], []),
        new(typeof(ProveResponse),
            [
                ("AccessToken", typeof(string)), ("AccessTokenExpiresAt", typeof(long)), ("NextDeviceSeqExpected", typeof(long)), ("Resync", typeof(bool)),
                ("UsageAssignments", typeof(IReadOnlyList<UsageAssign>)), ("TokenRotation", typeof(string)),
            ],
            [("UpdateOnly", typeof(bool?))]),
        new(typeof(AccessTokenResponse), [("AccessToken", typeof(string)), ("ExpiresAt", typeof(long))], []),
        new(typeof(ServerNotice),
            [("Kind", typeof(string))],
            [("RetryAfterSec", typeof(int?)), ("Text", typeof(string)), ("Release", typeof(string))]),
        new(typeof(ReleaseSignature), [("Kid", typeof(string)), ("Sig", typeof(string))], []),
        new(typeof(AppReleaseInfo), [("Release", typeof(string)), ("Version", typeof(string))], []),
        new(typeof(LatestAppRelease),
            [
                ("Release", typeof(string)), ("Version", typeof(string)), ("Build", typeof(string)), ("Manifest", typeof(string)),
                ("Sig", typeof(ReleaseSignature)), ("ZipBytes", typeof(long)),
            ],
            []),
    ];

    [Fact]
    public void 交握與下載端點的形狀只能加選用欄位()
    {
        Shapes.Should().HaveCount(9);
        foreach (var shape in Shapes)
        {
            var name = shape.Type.Name;
            var constructors = shape.Type.GetConstructors();
            constructors.Should().ContainSingle($"{name} 只有主建構子（線上格式由它決定）");
            var parameters = constructors[0].GetParameters();
            var frozen = shape.Required.Concat(shape.Optional).ToArray();
            parameters.Length.Should().BeGreaterThanOrEqualTo(frozen.Length, $"{name} 的既有欄位不能刪");
            for (var i = 0; i < frozen.Length; i++)
            {
                parameters[i].Name.Should().Be(frozen[i].Name, $"{name} 第 {i + 1} 個參數不能改名或換位置");
                parameters[i].ParameterType.Should().Be(frozen[i].Type, $"{name}.{frozen[i].Name} 不能換型別");
            }

            foreach (var parameter in parameters.Skip(shape.Required.Length))
            {
                parameter.HasDefaultValue.Should().BeTrue($"{name}.{parameter.Name} 是後加的欄位，必須是選用的（有預設值，沒有值時線上省略）");
                // PROTO-3：只有預設值不夠。Newtonsoft 讀到缺這個欄位的 JSON（舊的一方送的）時給的是 default(T)，不是 C# 寫的預設值；
                // 預設值是 null（型別也要可為 null）才會兩邊一致：沒有值時線上省略、讀進來也是 null
                parameter.DefaultValue.Should().BeNull($"{name}.{parameter.Name} 的預設值必須是 null");
                (!parameter.ParameterType.IsValueType || Nullable.GetUnderlyingType(parameter.ParameterType) is not null)
                    .Should().BeTrue($"{name}.{parameter.Name} 的型別必須可為 null（值型別用 T?）");
            }
        }
    }

    [Fact]
    public void 後加的欄位_線上缺少時讀成null()
    {
        // 實際經 SyncJson（Newtonsoft、主建構子）讀一份只有必填欄位的 JSON（這裡用空物件：必填的讀成 default 不影響）：後加的欄位一律是 null
        var read = typeof(SyncJson).GetMethod(nameof(SyncJson.Deserialize))!;
        foreach (var shape in Shapes)
        {
            var value = read.MakeGenericMethod(shape.Type).Invoke(null, ["{}"]);
            value.Should().NotBeNull(shape.Type.Name);
            var parameters = shape.Type.GetConstructors().Single().GetParameters();
            foreach (var parameter in parameters.Skip(shape.Required.Length))
            {
                var property = shape.Type.GetProperty(parameter.Name!)!;
                property.GetValue(value).Should().BeNull($"{shape.Type.Name}.{parameter.Name} 缺少時要讀成 null");
            }
        }
    }

    [Fact]
    public void 下載端點路徑固定()
    {
        AppReleaseRoutes.DevicePrefix.Should().Be("/api/v1/device/app/");
        AppReleaseRoutes.Latest.Should().Be("/api/v1/device/app/releases/latest");
        AppReleaseRoutes.PackagePattern.Should().Be("/api/v1/device/app/releases/{release}/package");
        AppReleaseRoutes.Package("0.2.300-ab12cd3").Should().Be("/api/v1/device/app/releases/0.2.300-ab12cd3/package");
        AppReleaseRoutes.Package("0.2.300-ab12cd3").Should().Be(AppReleaseRoutes.PackagePattern.Replace("{release}", "0.2.300-ab12cd3", StringComparison.Ordinal));
        // 不驗證就組路徑：「../」、「?」、「#」會改變請求的對象（同 ReleasePackageRules.FileNames，不合法的一律丟例外，呼叫端不必跳脫）
        foreach (var bad in new[] { "../latest", "0.2.300-ab12cd3?x=1", "0.2.300-ab12cd3#", "0.2.300-AB12CD3", " 0.2.300-ab12cd3", "", "0.2.300-dev" })
            FluentActions.Invoking(() => AppReleaseRoutes.Package(bad)).Should().Throw<ArgumentException>(bad);
        AppReleaseRoutes.Latest.Should().StartWith(AppReleaseRoutes.DevicePrefix);
        AppReleaseRoutes.PackagePattern.Should().StartWith(AppReleaseRoutes.DevicePrefix);
        AppReleaseRoutes.DevicePrefix.Should().StartWith("/api/v1/device/", "存取權杖 cha_ 只在 /api/v1/device/ 底下認證得過（伺服器的 AccessTokenStore.PathPrefix）");
    }

    [Fact]
    public void 支援範圍包含目前版本與前一版()
    {
        SyncProtocol.IsSupported(SyncProtocol.Version).Should().BeTrue();
        SyncProtocol.MinSupportedVersion.Should().BeGreaterThanOrEqualTo(1);
        SyncProtocol.MinSupportedVersion.Should().BeLessThanOrEqualTo(Math.Max(1, SyncProtocol.Version - 1),
            "伺服器至少支援目前與前一個 protocolVersion（app 自我更新規格 7.6）：升 Version 時 MinSupportedVersion 不能跟著升");
        SyncProtocol.IsSupported(SyncProtocol.Version + 1).Should().BeFalse("比伺服器新的協定（伺服器回滾）回 protocol");
        SyncProtocol.IsSupported(SyncProtocol.MinSupportedVersion - 1).Should().BeFalse();
        SyncProtocol.IsSupported(0).Should().BeFalse();
    }

    [Fact]
    public void 新的錯誤代碼與通知種類的值固定()
    {
        SyncErrorCodes.Draining.Should().Be("draining");
        SyncErrorCodes.UpdateRequired.Should().Be("update_required");
        new[] { ServerNoticeKinds.Shutdown, ServerNoticeKinds.CertExpiring, ServerNoticeKinds.MinAppVersion, ServerNoticeKinds.Alert, ServerNoticeKinds.AppRelease }
            .Should().Equal(new[] { "shutdown", "certExpiring", "minAppVersion", "alert", "appRelease" }, "桌面 SyncEngine.Session.cs 與伺服器共用這份（規格第 16 節第 59 項）");
    }
}
