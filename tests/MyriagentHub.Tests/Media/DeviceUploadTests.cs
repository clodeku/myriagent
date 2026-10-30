using System.Net;
using System.Text;
using MyriagentHub.Media;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Media;

/// <summary>
/// 桌面遠端操作的裝置上傳（sync-protocol 14.7：<c>PUT /api/v1/device/uploads/{uploadId}</c>）：存取權杖、免 CSRF，和瀏覽器的 E4 同一個登記，
/// 之後經 PeerCmd 的 addUploaded 照樣驗得過；E11 的 <c>purpose=file</c> 只收 fileFetch 的參照
/// </summary>
public sealed class DeviceUploadTests
{
    [Fact]
    public async Task 裝置上傳_新存201_同一份重傳200_登記在同一個UploadRegistry()
    {
        await using var host = await MediaHost.StartAsync();
        var (device, cha) = await host.DeviceAsync();
        using var _ = device;
        using var client = host.Client();
        var data = Encoding.UTF8.GetBytes("從另一台桌面附加的檔案");

        using (var first = await client.SendAsync(MediaHost.Device(HttpMethod.Put, "/api/v1/device/uploads/up-1", cha, MediaHost.Bytes(data))))
        {
            first.StatusCode.Should().Be(HttpStatusCode.Created);
            var json = JObject.Parse(await first.Content.ReadAsStringAsync());
            (json.Value<string>("uploadId"), json.Value<long>("size")).Should().Be(("up-1", data.LongLength));
            json.Value<string>("sha256").Should().Be(MediaBytes.Sha(data));
        }
        using (var again = await client.SendAsync(MediaHost.Device(HttpMethod.Put, "/api/v1/device/uploads/up-1", cha, MediaHost.Bytes(data))))
            again.StatusCode.Should().Be(HttpStatusCode.OK);

        host.Services.GetRequiredService<IUploadRegistry>().TryVerify([new UploadClaim("up-1", data.LongLength, MediaBytes.Sha(data))]).Should().BeTrue();
    }

    [Fact]
    public async Task 裝置上傳_沒有存取權杖401_id不合格400()
    {
        await using var host = await MediaHost.StartAsync();
        var (device, cha) = await host.DeviceAsync();
        using var _ = device;
        using var client = host.Client();

        using (var anonymous = await client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/v1/device/uploads/up-2") { Content = MediaHost.Bytes([1]) }))
            anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using (var bad = await client.SendAsync(MediaHost.Device(HttpMethod.Put, "/api/v1/device/uploads/bad%20id", cha, MediaHost.Bytes([1]))))
            bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public void 媒體查詢規則_E11只收file的參照_不快取()
    {
        MediaLookupRules.Allows(MediaEndpointKind.ReplicaFile, MediaRefPurposes.File).Should().BeTrue();
        MediaLookupRules.Allows(MediaEndpointKind.ReplicaFile, MediaRefPurposes.List).Should().BeFalse();
        MediaLookupRules.Allows(MediaEndpointKind.ReplicaFile, "item:x").Should().BeFalse();
        MediaLookupRules.RequestPurpose(MediaEndpointKind.ReplicaFile).Should().Be("file");
        MediaServeRules.CacheControl(MediaEndpointKind.ReplicaFile).Should().Contain("no-store");
    }
}
