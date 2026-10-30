using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using MyriagentHub.Auth;
using MyriagentHub.Media;
using MyriagentHub.Tests.TestSupport;

namespace MyriagentHub.Tests.Media;

/// <summary>
/// 媒體端點的測試主機：真的 Kestrel（本文上限、Expect: 100-continue 只有它才有）＋固定的磁碟可用空間（不看測試機的 C:）。
/// 用戶端不驗自簽憑證、cookie 經 <see cref="CookieHandler"/>（假時鐘的到期交給伺服器）、Expect: 100-continue 等 30 秒
/// （伺服器不讀本文就回應時，用戶端一個位元組都不送）
/// </summary>
internal sealed class MediaHost : IAsyncDisposable
{
    private MediaHost(HubTestHost host, FakeDiskSpace disk)
    {
        Host = host;
        Disk = disk;
    }

    public HubTestHost Host { get; }

    public FakeDiskSpace Disk { get; }

    public IServiceProvider Services => Host.Services;

    /// <param name="services">在磁碟空間之後再登記（測試換掉單一元件，例如轉送的計時器）</param>
    /// <param name="dataDir">沿用的資料根（重新啟動的測試；呼叫端負責刪）；null＝新的暫存資料根</param>
    /// <param name="clock">主機的手動時鐘（null＝預設的固定起點；回滾測試的子程序用真的時鐘，要以真實時間起點寫資料）</param>
    public static async Task<MediaHost> StartAsync(IDictionary<string, string?>? settings = null, long freeBytes = 500L << 30, Action<IServiceCollection>? services = null,
        string? dataDir = null, ManualTimeProvider? clock = null)
    {
        var disk = new FakeDiskSpace(freeBytes);
        var host = await HubTestHost.StartAsync(dataDir: dataDir, settings: settings, kestrel: true, clock: clock, services: s =>
        {
            s.AddSingleton<IDiskSpaceProbe>(disk);
            services?.Invoke(s);
        });
        return new MediaHost(host, disk);
    }

    /// <summary>新的用戶端（自己的 cookie 容器）</summary>
    public HttpClient Client()
    {
        var sockets = new SocketsHttpHandler
        {
            UseCookies = false,
            Expect100ContinueTimeout = TimeSpan.FromSeconds(30),
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        };
        return new HttpClient(new CookieHandler(new CookieContainer()) { InnerHandler = sockets })
        {
            BaseAddress = Host.KestrelAddress,
            Timeout = TimeSpan.FromSeconds(60),
        };
    }

    /// <summary>以預設密碼登入的擁有者用戶端</summary>
    public async Task<HttpClient> OwnerAsync()
    {
        var client = Client();
        using var login = Owner(HttpMethod.Post, "/api/v1/auth/login",
            new StringContent("{\"userName\":\"" + OwnerAccount.UserName + "\",\"password\":\"" + OwnerAccount.DefaultPassword + "\"}", Encoding.UTF8, "application/json"));
        using var response = await client.SendAsync(login);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "擁有者登入");
        return client;
    }

    /// <summary>配對過的裝置與它的存取權杖 cha_</summary>
    public async Task<(TestDevice Device, string AccessToken)> DeviceAsync()
    {
        var device = await TestDevice.AddAsync(Host);
        return (device, Services.GetRequiredService<AccessTokenStore>().Issue(device.Id).AccessToken);
    }

    /// <summary>擁有者的請求：帶本站 Origin 與 CSRF 標頭（<paramref name="csrf"/> 是 false 時不帶 CSRF）</summary>
    public static HttpRequestMessage Owner(HttpMethod method, string path, HttpContent? content = null, bool csrf = true)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("Origin", HubTestHost.Origin);
        if (csrf)
            request.Headers.Add("X-Myriagent-Csrf", "1");
        return request;
    }

    /// <summary>裝置的請求：<c>Authorization: Bearer</c>，不帶 Origin（裝置的 .NET 用戶端不送）</summary>
    public static HttpRequestMessage Device(HttpMethod method, string path, string bearer, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return request;
    }

    /// <summary>位元組本文（Content-Length 照實際長度）</summary>
    public static ByteArrayContent Bytes(byte[] data) => new(data);

    /// <summary>
    /// 宣告 <paramref name="declaredLength"/>、內容一讀就丟例外的本文，請求帶 <c>Expect: 100-continue</c>：
    /// 伺服器沒讀本文就回應（≥300）時拿得到回應；伺服器一讀（Kestrel 送 100 Continue），用戶端送本文時就丟例外。
    /// 只用在拒絕的回應：最終回應 &lt;300 時用戶端照樣送本文。宣告的長度至少 16 KB：Kestrel 對很小的本文（實測宣告 ≤1000 位元組）
    /// 會在回應之後送 100 Continue 把本文讀掉、以便重用連線（2000 起就不會），證明不了伺服器沒讀
    /// </summary>
    public static HttpRequestMessage Unread(HttpRequestMessage request, long declaredLength, string? contentType = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(declaredLength, 16 * 1024);
        request.Content = new StreamContent(new ThrowingStream());
        request.Content.Headers.ContentLength = declaredLength;
        if (contentType is not null)
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        request.Headers.ExpectContinue = true;
        return request;
    }

    /// <summary>JSON 錯誤回應的 <c>error</c></summary>
    public static async Task<string> ErrorAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return System.Text.Json.JsonDocument.Parse(text).RootElement.GetProperty("error").GetString()!;
    }

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}

/// <summary>看起來像權杖的字串一律在執行時組（repo 裡不出現像真權杖的字面值）：前綴分開寫、本體由固定種子亂數產生</summary>
internal static class FakeSecrets
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>GitHub 個人權杖的樣式（前綴之後 36 個英數字，DLP 的長度下限是 20）</summary>
    public static string GitHubStyle(int seed = 11)
    {
        var random = new Random(seed);
        var body = new char[36];
        for (var i = 0; i < body.Length; i++)
            body[i] = Alphabet[random.Next(Alphabet.Length)];
        return "gh" + "p_" + new string(body);
    }
}

/// <summary>固定的磁碟可用空間（配額與磁碟下限的測試）</summary>
internal sealed class FakeDiskSpace(long free) : IDiskSpaceProbe
{
    public long Free { get; set; } = free;

    public long FreeBytes(string path) => Free;
}

/// <summary>媒體測試的小工具</summary>
internal static class MediaBytes
{
    public static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>乾淨的 ASCII 文字（嚴格 UTF-8、沒有任何權杖樣式），正好 <paramref name="length"/> 個位元組</summary>
    public static byte[] Text(int length)
    {
        const string sentence = "lorem ipsum dolor sit amet. ";
        var data = new byte[length];
        for (var i = 0; i < length; i++)
            data[i] = (byte)sentence[i % sentence.Length];
        return data;
    }

    /// <summary>固定種子的亂數內容（不是真實資料）</summary>
    public static byte[] Random(int length, int seed = 7)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>開頭是 PNG 魔數、其餘是固定種子亂數的 <paramref name="length"/> 個位元組（只驗魔數的縮圖檢查看得過；不是真的圖）</summary>
    public static byte[] Png(int length, int seed = 5)
    {
        var data = Random(length, seed);
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(data, 0);
        return data;
    }

    /// <summary>開頭是 JPEG 魔數的 <paramref name="length"/> 個位元組</summary>
    public static byte[] Jpeg(int length, int seed = 6)
    {
        var data = Random(length, seed);
        new byte[] { 0xFF, 0xD8, 0xFF }.CopyTo(data, 0);
        return data;
    }
}

/// <summary>一讀就丟例外：證明呼叫端沒有讀內容</summary>
internal sealed class ThrowingStream : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("不該讀內容");

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("不該讀內容");

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// 讀取前先等 <paramref name="gate"/>（第一次讀的時候呼叫 <paramref name="started"/>），之後照 <paramref name="data"/> 給內容；
/// <paramref name="endless"/> 時讀完一輪再從頭給（沒有結尾），以 <see cref="BytesRead"/> 算總共被讀了多少
/// </summary>
internal sealed class ScriptedStream(byte[] data, Task? gate = null, Action? started = null, bool endless = false) : Stream
{
    private int position;
    private bool first = true;

    public long BytesRead { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (first)
        {
            first = false;
            started?.Invoke();
            if (gate is not null)
                await gate.WaitAsync(cancellationToken);
        }

        if (position >= data.Length)
        {
            if (!endless || data.Length == 0)
                return 0;
            position = 0;
        }

        var n = Math.Min(buffer.Length, data.Length - position);
        data.AsSpan(position, n).CopyTo(buffer.Span);
        position += n;
        BytesRead += n;
        return n;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
