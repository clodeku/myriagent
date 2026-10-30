using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Myriagent.Sync;
using Xunit.Abstractions;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>
/// 權杖 DLP 的 F10 補洞（WP4.3 步 4；WP4.1 合併前審查 wire/F10、SEC-21）：Authorization 的 Basic／Token／Bearer、網址裡的密碼、
/// git 的 http.extraheader、base64 包起來的憑證檔。照 <see cref="CredentialLeakRulesTests"/> 的作法，正例一律在執行時以固定種子的亂數組出來
/// （前綴拆開拼、主機都是 example.test），repo 裡沒有任何像真憑證的字串
/// </summary>
public sealed class CredentialLeakRulesF10Tests(ITestOutputHelper output)
{
    private const string Alnum = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private static readonly string AntOat = "sk" + "-ant-" + "oat01-";
    private static readonly string AntOrt = "sk" + "-ant-" + "ort01-";

    private static string Body(Random random, int length, string chars = Alnum)
        => new(Enumerable.Range(0, length).Select(_ => chars[random.Next(chars.Length)]).ToArray());

    /// <summary>字母與數字都有的假值（佔位判斷要求有數字；開頭固定是字母、結尾固定是數字）</summary>
    private static string Mixed(Random random, int length) => "f" + Body(random, length - 2) + "7";

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    /// <summary>以 <paramref name="width"/> 字折行（base64 -w、PEM、kubeconfig 的寫法），每行前面加 <paramref name="indent"/></summary>
    private static string Fold(string base64, int width, string indent = "")
        => string.Join("\n", base64.Chunk(width).Select(c => indent + new string(c)));

    /// <summary>一筆正例：文字、遮蔽之後一定要消失的部分、一定要留下的周圍文字</summary>
    internal sealed record Positive(string Kind, string Text, string Secret, string Context);

    internal static List<Positive> Positives()
    {
        var random = new Random(20261019);
        var list = new List<Positive>();

        // 一、Authorization（Basic 的值是「帳號:密碼」的 base64；Token／Bearer 的值是不透明字串）
        var headerTemplates = new (string Template, string Context)[]
        {
            ("Authorization: Basic {0}", "Authorization: Basic "),
            ("authorization: basic {0}", "authorization: basic "),
            ("AUTHORIZATION: BASIC {0}", "AUTHORIZATION: BASIC "),
            ("Proxy-Authorization: Basic {0}", "Proxy-Authorization: Basic "),
            ("curl -H \"Authorization: Basic {0}\" https://api.example.test/v1", "https://api.example.test/v1"),
            ("{{\"Authorization\": \"Basic {0}\", \"Accept\": \"*/*\"}}", "\"Accept\": \"*/*\""),
            ("headers = {{'Authorization': 'Token {0}'}}", "headers = {'Authorization': 'Token "),
            ("$h = @{{ Authorization = \"Bearer {0}\" }}", "$h = @{ Authorization = \"Bearer "),
            ("Authorization=Bearer {0}", "Authorization=Bearer "),
            ("> Authorization: Token {0}\n> Host: api.example.test", "> Host: api.example.test"),
            ("請求標頭\nAuthorization:  Basic   {0}\n回應 200", "回應 200"),
        };
        foreach (var (template, context) in headerTemplates)
        {
            for (var i = 0; i < 2; i++)
            {
                var value = template.Contains("Basic", StringComparison.OrdinalIgnoreCase)
                    ? B64("deploy" + i + ":" + Mixed(random, 14 + i * 9))
                    : Mixed(random, 20 + i * 12);
                list.Add(new("authorization", string.Format(template, value), value, context));
            }
        }

        // 二、網址的 userinfo（密碼換掉、使用者名稱留下；使用者名稱本身像權杖時整段換掉）
        var urlTemplates = new (string Template, string Context)[]
        {
            ("https://deploy:{0}@git.example.test/org/repo.git", "@git.example.test/org/repo.git"),
            ("git clone https://ci-bot:{0}@git.example.test/org/repo", "git clone https://ci-bot:"),
            ("postgres://app:{0}@db.example.test:5432/main", "@db.example.test:5432/main"),
            ("mongodb+srv://admin:{0}@cluster0.example.test/?retryWrites=true", "mongodb+srv://admin:"),
            ("redis://:{0}@cache.example.test:6379/0", "@cache.example.test:6379/0"),
            ("amqp://guest:{0}@mq.example.test:5672", "amqp://guest:"),
            ("連線字串是 ftp://user:{0}@ftp.example.test/home 請保管好", "請保管好"),
            ("\"url\": \"https://bot:{0}@registry.example.test/v2/\"", "@registry.example.test/v2/\""),
            ("origin\thttps://me:{0}@git.example.test/a/b.git (fetch)", "(fetch)"),
            ("smtp://mailer:{0}@smtp.example.test:587", "smtp://mailer:"),
        };
        foreach (var (template, context) in urlTemplates)
        {
            for (var i = 0; i < 2; i++)
            {
                var password = i == 0 ? Mixed(random, 12) : Body(random, 6) + "%2F" + Body(random, 6) + "9";
                list.Add(new("url", string.Format(template, password), password, context));
            }
        }

        for (var i = 0; i < 3; i++)
        {
            var tokenUser = Mixed(random, 40);
            list.Add(new("url", $"https://{tokenUser}@git.example.test/org/repo.git", tokenUser, "@git.example.test/org/repo.git"));
            list.Add(new("url", $"https://{tokenUser}:x-oauth-basic@git.example.test/org/repo", tokenUser, "@git.example.test/org/repo"));
        }

        // 三、git 的 http.extraheader（整個值換掉）
        var extraTemplates = new (string Template, string Context)[]
        {
            ("git -c http.extraheader=\"AUTHORIZATION: basic {0}\" clone https://git.example.test/org/repo", " clone https://git.example.test/org/repo"),
            ("git -c http.extraheader='Authorization: Bearer {0}' fetch", "' fetch"),
            ("git config --global http.https://git.example.test/.extraheader \"AUTHORIZATION: basic {0}\"", "git config --global http.https://git.example.test/.extraheader \""),
            ("[http \"https://git.example.test/\"]\n\textraheader = AUTHORIZATION: basic {0}\n[core]", "[core]"),
            ("[http]\n    extraheader = X-Api-Key: {0}\n", "[http]"),
            ("http.extraheader=Authorization: token {0}", "http.extraheader="),
            ("http.https://git.example.test/.extraheader AUTHORIZATION: bearer {0}", "http.https://git.example.test/.extraheader "),
        };
        foreach (var (template, context) in extraTemplates)
        {
            for (var i = 0; i < 3; i++)
            {
                var value = template.Contains("basic", StringComparison.Ordinal)
                    ? B64("x-access-token:" + Mixed(random, 18 + i * 5))
                    : Mixed(random, 22 + i * 7);
                list.Add(new("extraheader", string.Format(template, value), value, context));
            }
        }

        // 四、base64 包起來的憑證檔（欄位名稱或解碼後的權杖命中才換；折行與縮排的也要認得）
        for (var i = 0; i < 4; i++)
        {
            var claude = B64("{\"claudeAiOauth\":{\"accessToken\":\"" + AntOat + Body(random, 80) + "\",\"refreshToken\":\""
                             + AntOrt + Body(random, 80) + "\",\"expiresAt\":1790000000000}}");
            list.Add(new("base64", "$ base64 -w0 .credentials.json\n" + claude + "\n$ ", claude, "$ base64 -w0 .credentials.json"));

            var codex = B64("{\"OPENAI_API_KEY\":null,\"tokens\":{\"id_token\":\"" + Mixed(random, 60) + "\",\"refresh_token\":\"" + Mixed(random, 50)
                            + "\",\"account_id\":\"" + Body(random, 12) + "\"}}");
            list.Add(new("base64", "auth.json 的內容：" + codex + "（完）", codex, "（完）"));

            var docker = B64("{\"auths\":{\"registry.example.test\":{\"auth\":\"" + B64("ci:" + Mixed(random, 20)) + "\"}}}");
            list.Add(new("base64", "kubectl get secret regcred -o yaml\ndata:\n  .dockerconfigjson: " + docker + "\nkind: Secret", docker, "kind: Secret"));

            // 欄位名稱在很後面：只看第一行解不出來，要接上折行才認得
            var late = B64("{\"version\":1,\"description\":\"" + string.Concat(Enumerable.Repeat("filler text for wrapping ", 6))
                           + "\",\"client_secret\":\"" + Mixed(random, 32) + "\"}");
            var folded = Fold(late, 76, "    ");
            list.Add(new("base64", "secret.yaml:\n" + folded + "\n# end", folded.Split('\n')[1].Trim(), "# end"));

            var pem = "-----BEGIN " + "EC PRIVATE KEY-----\n" + Fold(B64(Body(random, 90)), 64) + "\n-----END " + "EC PRIVATE KEY-----\n";
            var kubeKey = B64(pem);
            list.Add(new("base64", "users:\n- name: admin\n  user:\n    client-key-data: " + kubeKey + "\n", kubeKey, "    client-key-data: "));

            var command = Convert.ToBase64String(Encoding.Unicode.GetBytes("$env:GH_TOKEN='" + "gh" + "p_" + Body(random, 36) + "'; gh auth status"));
            list.Add(new("base64", "powershell -EncodedCommand " + command, command, "powershell -EncodedCommand "));
        }

        return list;
    }

    internal static List<(string Kind, string Text)> Negatives()
    {
        var random = new Random(20261020);
        var list = new List<(string Kind, string Text)>();

        foreach (var text in new[]
        {
            "Authorization: Basic 後面接 base64 編碼的帳號與密碼",
            "Authorization: Bearer YOUR_TOKEN",
            "Authorization: Bearer <token>",
            "Authorization: Bearer $TOKEN",
            "Authorization: Bearer ${GITHUB_TOKEN}",
            "Authorization: Bearer {{access_token}}",
            "Authorization: Token PLACEHOLDER",
            "Authorization: Basic CREDENTIALS",
            "Authorization: Basic REDACTED",
            "headers['Authorization'] = 'Bearer ' + token",
            "設定 Authorization 標頭",
            "the Authorization header is missing",
            "Authorization: Basic\n",
            "WWW-Authenticate: Basic realm=\"example\"",
            "Authorization: Bearer ***",
            "req.Headers.Authorization = new AuthenticationHeaderValue(\"Bearer\", token);",
            "Authorization: Basic abc",
            "Authorization: Negotiate",
            "proxy authorization required",
            "Authorization: Bearer MY_SECRET_TOKEN",
        })
            list.Add(("authorization", text));

        foreach (var text in new[]
        {
            "git@github.com:org/repo.git",
            "https://github.com/org/repo",
            "http://user@host/",
            "ssh://git@git.example.test/org/repo.git",
            "https://example.test/@user/profile",
            "mailto:someone@example.test",
            "https://user:$PASSWORD@git.example.test/x",
            "https://user:***@git.example.test/x",
            "https://user:%s@%s/repo",
            "ftp://anonymous@ftp.example.test/pub",
            "http://localhost:8080/api/v1",
            "https://[::1]:443/healthz",
            "npm install @scope/pkg@1.2.3",
            "https://registry.example.test/@scope%2fpkg",
            "s3://bucket/key/path",
            "https://deploy@example.test:8443/x",
            "file:///C:/repo/x.txt",
            "https://a.example.test/c?email=x@y.example.test",
            "postgres://localhost/main",
            "https://user:@git.example.test/repo",
        })
            list.Add(("url", text));

        foreach (var text in new[]
        {
            "git config --get http.extraheader",
            "設定 http.extraheader 之後再 clone",
            "extraheader",
            "git config --unset-all http.https://git.example.test/.extraheader",
            "// extraheader 留空",
            "http.extraheader 是 git 的設定",
            "persist-credentials: false 會拿掉 extraheader",
            "Use extraheader to pass a header",
            "extraheader=",
            "[http]\n\tsslVerify = true",
            "git -c core.autocrlf=false status",
            "extraheaders are not supported",
            "the extraheader value is empty",
            "http.extraHeader 與 http.proxy",
            "extraheader = \n",
            "git config http.postBuffer 524288000",
            "actions/checkout 會寫入 extraheader",
            "移除 extraheader：git config --unset http.extraheader",
            "extraheader（git 2.9 以後）",
            "#extraheader = ",
        })
            list.Add(("extraheader", text));

        var png = new byte[600];
        random.NextBytes(png);
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("x"))).ToLowerInvariant();
        foreach (var text in new[]
        {
            "data:image/png;base64," + Convert.ToBase64String(png),
            Fold(Convert.ToBase64String(png), 76),
            B64("{\"name\":\"myriagent\",\"version\":\"1.0.0\",\"private\":true,\"description\":\"desktop app and server for agents\"}"),
            B64(string.Concat(Enumerable.Repeat("Lorem ipsum dolor sit amet, consectetur adipiscing elit. ", 4))),
            Fold(B64(string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 6))), 64, "  "),
            "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("app.js"))),
            sha,
            "commit " + sha[..40],
            "integrity=\"sha512-" + Convert.ToBase64String(SHA512.HashData(Encoding.UTF8.GetBytes("pkg"))) + "\"",
            Convert.ToBase64String(Encoding.Unicode.GetBytes("Get-ChildItem -Recurse | Select-Object -First 10 | Format-Table")),
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/ 是 base64 的字母表",
            "/usr/local/lib/node_modules/typescript/lib/typescript.js",
            "src/Myriagent.Protocol/Sync/CredentialLeakRules.cs",
            string.Concat(Enumerable.Repeat("aGVsbG8gd29ybGQ", 5)),
            B64("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"16\" height=\"16\"><path d=\"M0 0h16v16H0z\" fill=\"none\"/></svg>"),
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            B64("[{\"id\":1,\"title\":\"first\"},{\"id\":2,\"title\":\"second\"},{\"id\":3,\"title\":\"third\"},{\"id\":4}]"),
            "-----BEGIN CERTIFICATE-----\n" + Fold(Convert.ToBase64String(png[..300]), 64) + "\n-----END CERTIFICATE-----",
            Guid.Empty.ToString("N") + Guid.Empty.ToString("N"),
            B64("export PATH=\"$HOME/.local/bin:$PATH\"\nalias ll='ls -la'\nset -o vi\n# end of profile\n"),
        })
            list.Add(("base64", text));

        return list;
    }

    [Fact]
    public void F10_四類正例_執行時產生_全部遮蔽()
    {
        var positives = Positives();
        foreach (var kind in new[] { "authorization", "url", "extraheader", "base64" })
            positives.Count(p => p.Kind == kind).Should().BeGreaterThanOrEqualTo(20, $"{kind}：每類 ≥20 筆");

        foreach (var p in positives)
        {
            var masked = CredentialLeakRules.Mask(p.Text);
            masked.Should().NotContain(p.Secret, $"{p.Kind}：{p.Text}");
            masked.Should().Contain(CredentialLeakRules.Replacement, p.Kind);
            masked.Should().Contain(p.Context, $"{p.Kind}：只換命中的部分，周圍的文字留下（{p.Text}）");
            CredentialLeakRules.HasLeak(p.Text).Should().BeTrue($"{p.Kind}：HasLeak 與 Mask 用同一組判斷（{p.Text}）");
        }
    }

    [Fact]
    public void F10_反例_全部不動()
    {
        var negatives = Negatives();
        foreach (var kind in new[] { "authorization", "url", "extraheader", "base64" })
            negatives.Count(n => n.Kind == kind).Should().BeGreaterThanOrEqualTo(20, $"{kind}：每類 ≥20 筆");

        foreach (var (kind, text) in negatives)
        {
            CredentialLeakRules.Mask(text).Should().Be(text, $"{kind}：{text}");
            CredentialLeakRules.HasLeak(text).Should().BeFalse($"{kind}：{text}");
        }
    }

    [Fact]
    public void F10_漏遮率與誤遮率都是0()
    {
        var positives = Positives();
        var negatives = Negatives();
        var missed = positives.Count(p => CredentialLeakRules.Mask(p.Text).Contains(p.Secret, StringComparison.Ordinal));
        var misfired = negatives.Count(n => CredentialLeakRules.Mask(n.Text) != n.Text);
        output.WriteLine($"正例 {positives.Count} 筆、漏遮 {missed}；反例 {negatives.Count} 筆、誤遮 {misfired}");
        missed.Should().Be(0, "SEC-21：漏遮率 0");
        misfired.Should().Be(0, "SEC-21：誤遮率 0");
    }

    [Fact]
    public void F10_遮蔽之後HasLeak為假_而且冪等()
    {
        foreach (var p in Positives())
        {
            var once = CredentialLeakRules.Mask(p.Text);
            CredentialLeakRules.HasLeak(once).Should().BeFalse($"{p.Kind}：伺服器的第二道（答 5 的再遮蔽）不會再動桌面送的內容（{p.Text}）");
            CredentialLeakRules.Mask(once).Should().Be(once, p.Kind);
            CredentialLeakRules.Mask(p.Text).Should().Be(once, "同一個輸入一定得到同一個輸出");
        }
    }

    [Fact]
    public void F10_1MB的base64雜訊與重複的前綴_線性時間()
    {
        const int size = 1 << 20;
        var random = new Random(7);
        string Repeat(string unit) => new StringBuilder(size + unit.Length).Insert(0, unit, size / unit.Length).ToString();
        var noise = Convert.ToBase64String(RandomNumberGenerator.GetBytes(size / 4 * 3));
        var inputs = new (string Name, string Text)[]
        {
            ("1 MB 的 base64 雜訊", noise),
            ("折成 76 字一行的 base64 雜訊", Fold(noise, 76)),
            ("每行越來越短的 base64", string.Join("\n", Enumerable.Range(0, 2000).Select(i => Body(random, 76 - i % 60, Alnum + "+/")))),
            ("scheme:// 重複", Repeat("https://")),
            ("沒有 @ 的 authority 重複", Repeat("https://a:b")),
            ("Authorization: Basic 重複", Repeat("Authorization: Basic ")),
            ("extraheader= 重複", Repeat("extraheader=")),
            ("16 個 base64 字元接空白重複", Repeat("ABCDEFGHIJKLMNOP ")),
        };

        foreach (var (name, text) in inputs)
        {
            var watch = Stopwatch.StartNew();
            _ = CredentialLeakRules.Mask(text);
            _ = CredentialLeakRules.HasLeak(text);
            watch.Stop();
            output.WriteLine($"{name}：{watch.ElapsedMilliseconds} ms");
            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), $"{name}：線性時間");
        }
    }

    /// <summary>樣式內容的雜湊：改了樣式（名稱、正規式、延伸的字元、邊界、判斷、base64 欄位名稱）要把 Version 加 1，並更新這裡</summary>
    private const int PinnedVersion = 2;

    private const string PinnedFingerprintSha256 = "f0658625313f43f75bccf3c0e8237ed361656fb00616f09007d9e9288b95fbdb";

    [Fact]
    public void 樣式內容的雜湊釘住_改樣式要升Version()
    {
        var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CredentialLeakRules.PatternFingerprint))).ToLowerInvariant();
        output.WriteLine($"Version={CredentialLeakRules.Version} SHA-256={actual}");
        (CredentialLeakRules.Version, actual).Should().Be((PinnedVersion, PinnedFingerprintSha256),
            "改了樣式要把 CredentialLeakRules.Version 加 1，並把這裡的 PinnedVersion 與 PinnedFingerprintSha256 換成新值");
    }

    [Fact]
    public void Version是2_也是宣告conv1的必要版本()
    {
        CredentialLeakRules.Version.Should().Be(2, "F10 是 WP4.3 第一個宣告 conv1 的組建必須帶著的版本（D38）");
        CredentialLeakRules.TokenFieldNames.Should().OnlyHaveUniqueItems().And.Contain(["refresh_token", "client_secret", "auths", "client-key-data"]);
    }
}
