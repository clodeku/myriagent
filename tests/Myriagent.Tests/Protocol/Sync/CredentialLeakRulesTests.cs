using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Json;
using Myriagent.Sync;
using Myriagent.Tests.Rendering;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>
/// 權杖 DLP（WP4.1 步 5；設計 8.8、14.11 D43、D44、SEC-21）。正例在執行時以字元組合產生（前綴拆開拼、本體用固定種子的亂數），
/// repo 裡不放任何像真權杖的字串（避免推送保護與誤報）；反例有一般程式碼、雜湊、UUID、base64 圖片，誤遮率與漏遮率都斷言是 0
/// </summary>
public sealed class CredentialLeakRulesTests(ITestOutputHelper output)
{
    private const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    private const string Alnum = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private const string Url = Alnum + "-_";

    // 前綴一律拆開拼（原始碼裡沒有完整的前綴＋本體）
    private static readonly string AntOat = "sk" + "-ant-" + "oat01-";
    private static readonly string AntApi = "sk" + "-ant-" + "api03-";
    private static readonly string Proj = "sk" + "-proj-";
    private static readonly string SvcAcct = "sk" + "-svcacct-";
    private static readonly string Admin = "sk" + "-admin-";
    private static readonly string Legacy = "sk" + "-";
    private static readonly string GithubPat = "github" + "_pat_";
    private static readonly string Ghp = "gh" + "p_";
    private static readonly string Gho = "gh" + "o_";
    private static readonly string Ghs = "gh" + "s_";
    private static readonly string Slack = "xo" + "xb-";
    private static readonly string Ya29 = "ya" + "29.";
    private static readonly string GoogleRefresh = "1/" + "/0";
    private static readonly string Xai = "xa" + "i-";
    private static readonly string Chd = "ch" + "d_";
    private static readonly string Cha = "ch" + "a_";
    private static readonly string Akia = "AK" + "IA";
    private static readonly string Asia = "AS" + "IA";
    private static readonly string Aiza = "AI" + "za";
    private static readonly string Eyj = "ey" + "J";
    private static readonly string KeyBegin = "-----BEGIN " + "RSA PRIVATE KEY-----";
    private static readonly string KeyEnd = "-----END " + "RSA PRIVATE KEY-----";

    private static string Body(Random random, int length, string chars = Alnum)
        => new(Enumerable.Range(0, length).Select(_ => chars[random.Next(chars.Length)]).ToArray());

    /// <summary>一筆正例：文字、文字裡必須消失的部分（值類樣式只有值）、與周圍必須留下的文字</summary>
    internal sealed record Positive(string Name, string Text, string Secret, string Context);

    /// <summary>每種樣式各 3 種上下文（純文字、JSON 字串、緊接中文字），共 ≥50 筆</summary>
    internal static List<Positive> Positives()
    {
        var random = new Random(20261018);
        var tokens = new List<(string Name, Func<string> Make, Func<string, string>? Secret)>
        {
            ("anthropic oat", () => AntOat + Body(random, 80, Url) + "AA", null),
            ("anthropic api", () => AntApi + Body(random, 93, Url), null),
            ("openai project", () => Proj + Body(random, 120, Url), null),
            ("openai service account", () => SvcAcct + Body(random, 60, Url), null),
            ("openai admin", () => Admin + Body(random, 60, Url), null),
            ("openai legacy", () => Legacy + Body(random, 48), null),
            ("github fine-grained", () => GithubPat + Body(random, 82, Alnum + "_"), null),
            ("github classic", () => Ghp + Body(random, 36), null),
            ("github oauth", () => Gho + Body(random, 36), null),
            ("github server", () => Ghs + Body(random, 36), null),
            ("slack", () => Slack + Body(random, 12, "0123456789") + "-" + Body(random, 24), null),
            ("google access", () => Ya29 + Body(random, 120, Url), null),
            ("google refresh", () => GoogleRefresh + Body(random, 100, Url), null),
            ("xai", () => Xai + Body(random, 80), null),
            ("myriagenthub device", () => Chd + Body(random, 43, Url), null),
            ("myriagenthub access", () => Cha + Body(random, 43, Url), null),
            ("aws", () => Akia + Body(random, 16, Upper), null),
            ("aws 暫時金鑰", () => Asia + Body(random, 16, Upper), null),
            ("google api key", () => Aiza + Body(random, 35, Url), null),
            ("jwt", () => Eyj + Body(random, 30, Url) + "." + Eyj + Body(random, 60, Url) + "." + Body(random, 43, Url), null),
        };

        var list = new List<Positive>();
        foreach (var (name, make, _) in tokens)
        {
            var a = make();
            list.Add(new Positive(name + "／純文字", $"權杖是 {a} 請保密", a, "請保密"));
            var b = make();
            list.Add(new Positive(name + "／JSON 字串", $$"""{"note":"{{b}}","n":1}""", b, "\"note\":\""));
            var c = make();
            list.Add(new Positive(name + "／緊接中文字", $"輸出{c}結束", c, "結束"));
        }

        // 欄位名稱＋值、私鑰區塊、cookie：只換值（欄位名稱留下）
        var access = Body(random, 40, Url);
        list.Add(new Positive("json accessToken", $$"""{"accessToken": "{{access}}", "expiresAt": 1}""", access, "\"accessToken\": \""));
        var refresh = Body(random, 40, Url);
        list.Add(new Positive("json refresh_token", $$"""{"refresh_token":"{{refresh}}"}""", refresh, "\"refresh_token\":\""));
        var idToken = Body(random, 40, Url);
        list.Add(new Positive("json id_token", $$"""{ "id_token" : "{{idToken}}" }""", idToken, "\"id_token\""));
        var apiKey = Body(random, 40, Url);
        list.Add(new Positive("json OPENAI_API_KEY", $$"""{"OPENAI_API_KEY":"{{apiKey}}"}""", apiKey, "OPENAI_API_KEY"));
        var secret = Body(random, 40, Url);
        list.Add(new Positive("json client_secret", $$"""{"client_secret":"{{secret}}"}""", secret, "client_secret"));
        var param = Body(random, 40, Url);
        list.Add(new Positive("查詢字串 refresh_token", $"grant_type=refresh_token&refresh_token={param}&x=1", param, "&x=1"));
        var bearer = Body(random, 60, Url + ".");
        list.Add(new Positive("Bearer", $"Authorization: Bearer {bearer}\r\nAccept: */*", bearer, "Accept: */*"));
        var cookie = Body(random, 200, Url);
        list.Add(new Positive("cookie", $"Cookie: __Host-myriagenthub={cookie}; other=1", cookie, "; other=1"));
        var chunk = Body(random, 200, Url);
        list.Add(new Positive("cookie 分塊", $"__Host-myriagenthubC1={chunk}", chunk, "__Host-myriagenthubC1="));
        var key = string.Join("\n", Enumerable.Range(0, 5).Select(_ => Body(random, 64, Alnum + "+/")));
        list.Add(new Positive("私鑰區塊", $"cat id_rsa\n{KeyBegin}\n{key}\n{KeyEnd}\n完成", key, "完成"));
        var cut = Body(random, 64, Alnum + "+/");
        list.Add(new Positive("沒有結尾的私鑰（被截斷的輸出）", $"cat id_rsa\n{KeyBegin}\n{cut}", cut, "cat id_rsa"));

        // 沒有 JSON 引號的權杖欄位（Codex 的 refresh_token 不是 JWT、沒有前綴，只能靠欄位名稱）
        var tokenChars = Alnum + "-.";
        var psRefresh = Body(random, 60, tokenChars);
        list.Add(new Positive("PowerShell fl 的 refresh_token",
            $"PS> Get-Content ~\\.codex\\auth.json | ConvertFrom-Json | Select -Expand tokens | fl\n\nrefresh_token : {psRefresh}\naccount_id    : 0f3c9a52", psRefresh, "account_id    : 0f3c9a52"));
        var yamlRefresh = Body(random, 60, tokenChars);
        list.Add(new Positive("YAML 的 refresh_token", $"tokens:\n  refresh_token: {yamlRefresh}\n  last_refresh: 2026-10-18", yamlRefresh, "last_refresh: 2026-10-18"));
        var pyRefresh = Body(random, 60, tokenChars);
        list.Add(new Positive("Python dict 的 refresh_token", $"{{'refresh_token': '{pyRefresh}', 'scope': 'openid'}}", pyRefresh, "'scope': 'openid'"));
        var rgRefresh = Body(random, 60, tokenChars);
        list.Add(new Positive("rg 印出的跳脫 jsonl 行",
            $"rollout.jsonl:12:{{\"type\":\"function_call_output\",\"output\":\"{{\\\"refresh_token\\\":\\\"{rgRefresh}\\\",\\\"n\\\":1}}\"}}", rgRefresh, "\\\"n\\\":1"));
        var dashed = Body(random, 60, tokenChars);
        list.Add(new Positive("access-token（連字號）", $$"""{"access-token": "{{dashed}}", "x": 1}""", dashed, "\"x\": 1"));
        var envRefresh = Body(random, 60, tokenChars);
        list.Add(new Positive("環境變數 CODEX_REFRESH_TOKEN", $"set CODEX_REFRESH_TOKEN={envRefresh}\n下一行", envRefresh, "下一行"));

        // AWS 的秘密金鑰（存取金鑰 ID 本身不是秘密）
        var awsSecretChars = Alnum + "/+";
        var credentialsSecret = Body(random, 40, awsSecretChars);
        list.Add(new Positive("~/.aws/credentials",
            $"[default]\naws_access_key_id = {Akia + Body(random, 16, Upper)}\naws_secret_access_key = {credentialsSecret}\nregion = ap-northeast-1", credentialsSecret, "region = ap-northeast-1"));
        var stsSecret = Body(random, 40, awsSecretChars);
        list.Add(new Positive("aws sts get-session-token",
            $$$"""{"Credentials": {"AccessKeyId": "{{{Asia + Body(random, 16, Upper)}}}", "SecretAccessKey": "{{{stsSecret}}}", "SessionToken": "{{{Body(random, 120, awsSecretChars)}}}", "Expiration": "2026-10-19T00:00:00Z"}}""",
            stsSecret, "\"Expiration\": \"2026-10-19T00:00:00Z\""));
        var exportSession = Body(random, 120, awsSecretChars);
        list.Add(new Positive("aws configure export-credentials",
            $$"""{"Version": 1, "AccessKeyId": "{{Asia + Body(random, 16, Upper)}}", "SecretAccessKey": "{{Body(random, 40, awsSecretChars)}}", "SessionToken": "{{exportSession}}"}""",
            exportSession, "\"Version\": 1"));
        var envSecret = Body(random, 40, awsSecretChars);
        list.Add(new Positive("環境變數 AWS_SECRET_ACCESS_KEY", $"export AWS_SECRET_ACCESS_KEY={envSecret}\nexport AWS_REGION=us-east-1", envSecret, "AWS_REGION=us-east-1"));
        var envSession = Body(random, 200, awsSecretChars);
        list.Add(new Positive("環境變數 AWS_SESSION_TOKEN", $"$env:AWS_SESSION_TOKEN=\"{envSession}\"; aws s3 ls", envSession, "; aws s3 ls"));
        return list;
    }

    /// <summary>反例：一般程式碼、雜湊、UUID、base64 圖片、路徑、網址、提到前綴但沒有本體的說明文字（≥50 筆）</summary>
    internal static List<string> Negatives()
    {
        var random = new Random(7);
        var png = new byte[96 * 1024];
        random.NextBytes(png);
        var list = new List<string>
        {
            "using System.Text.RegularExpressions;",
            "public static bool IsLocalDrivePath(string? path) => path is { Length: >= 3 };",
            "var tokens = new List<string>(); // 權杖清單",
            "const accessToken = getToken(); fetch(url, { headers: { Authorization: `Bearer ${accessToken}` } });",
            "Authorization: Bearer <token>",
            "Bearer authentication scheme",
            "The Bearer tokens are short-lived.",
            "{\"token_type\":\"Bearer\",\"expires_in\":3600}",
            "{\"accessToken\": \"short\"}",
            "{\"refreshToken\": null}",
            "{\"apiKey\": \"\"}",
            "refresh_token=&grant_type=refresh_token",
            "task-proj-alpha 與 risk-admin-panel 是專案代號",
            "desk-ant-colony-sim",
            "ghp_ 開頭的是 GitHub 的個人權杖，gho_ 是 OAuth",
            "xoxo-hugs 是暱稱",
            "ya29. 是 Google 存取權杖的前綴",
            "https://example.com/api/v1//0/items",
            "xai-grok 模型",
            "chd_count_total 與 cha_cha 是變數",
            "AKIA 是 AWS 存取金鑰的前綴",
            Akia + Body(random, 15, Upper) + " 少一碼",
            Akia + Body(random, 17, Upper) + " 多一碼",
            "x" + Akia + Body(random, 16, Upper) + " 前面接英數字",
            Aiza + Body(random, 34, Url) + " 少一碼",
            Aiza + Body(random, 36, Url) + " 多一碼",
            Legacy + Body(random, 39) + " 少一碼",
            "x" + Ghp + Body(random, 36) + " 前面接英數字",
            Ghp + Body(random, 19) + " 太短",
            Slack + Body(random, 9) + " 太短",
            Chd + Body(random, 15, Url) + " 太短",
            Xai + Body(random, 19) + " 太短",
            Ya29 + Body(random, 19, Url) + " 太短",
            GoogleRefresh + Body(random, 19, Url) + " 太短",
            Eyj + Body(random, 30, Url) + " 只有一段（不是 JWT）",
            Eyj + Body(random, 9, Url) + "." + Eyj + Body(random, 9, Url) + ".x 太短",
            "-----BEGIN CERTIFICATE-----\nMIIBszCCAVmgAwIBAgIUQ\n-----END CERTIFICATE-----",
            "-----BEGIN PUBLIC KEY-----\nMFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE\n-----END PUBLIC KEY-----",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("myriagent"))),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("agent"))),
            Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes("commit"))) + " 修正（測試）：…",
            Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("b64"))),
            new Guid(Enumerable.Range(1, 16).Select(i => (byte)(i * 13)).ToArray()).ToString(),
            new Guid(Enumerable.Range(1, 16).Select(i => (byte)(i * 7)).ToArray()).ToString("N"),
            new Guid(Enumerable.Range(1, 16).Select(i => (byte)(i * 5)).ToArray()).ToString("B").ToUpperInvariant(),
            "rd1:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("card"))),
            "01J9QZ3K8M5R7T2V4W6X8Y0A1B",
            "data:image/png;base64," + Convert.ToBase64String(png),
            Convert.ToBase64String(png[..3000]),
            @"C:\Users\Lin\AppData\Local\Myriagent\conversations\0f3c9a52b7e94d0c8a61f2d4e5b6c7d8\attachments\chart.png",
            @"C:\repo\myriagent\src\Myriagent.Protocol\Sync\CredentialLeakRules.cs",
            "https://github.com/anthropics/claude-code/issues/12345#issuecomment-987654321",
            "https://hub.example.com/app/?mode=popout&conversation=5f3a0c1d2e3f4a5b6c7d8e9f0a1b2c3d",
            "npm install @anthropic-ai/claude-code@2.1.0 --global",
            "dotnet build Myriagent.slnx -c Release -nodeReuse:false",
            "git log --oneline origin/main..HEAD",
            "我已經修好登入流程卡住的問題，接下來要跑整合測試。",
            "| 欄位 | 說明 |\n|---|---|\n| token | 權杖（不會出現在這裡） |",
            "SELECT TOP 10 * FROM Orders WHERE CustomerId = @id ORDER BY CreatedAt DESC;",
            "export const SESSION_TOKEN_HEADER = 'X-Myriagent-Csrf';",
            "lorem ipsum dolor sit amet, consectetur adipiscing elit 1234567890",
            "版本 1.2.3-beta.4+build.5678",
            "lin@example.com 寄了 3 封信",
            // 欄位名稱像權杖、值不是權杖的程式碼與說明
            "refresh_token: Optional[str] = None",
            "refresh_token: string;",
            "  access_token: str",
            "id-token: write",
            "def refresh_token(self) -> str: return self._token",
            "aws_secret_access_key = <你的秘密金鑰>",
            "{\"SecretAccessKey\": \"string\", \"SessionToken\": \"string\"}",
            "AWS_SESSION_TOKEN=$(aws sts get-session-token --query Credentials.SessionToken)",
            "ASIA 開頭的是 STS 的暫時金鑰",
        };
        return list;
    }

    // ───────────────────────────── 語料 ─────────────────────────────

    [Fact]
    public void 正例語料_漏遮率是0_周圍的文字留下()
    {
        var positives = Positives();
        positives.Count.Should().BeGreaterThanOrEqualTo(50, "SEC-21：正例 ≥50 筆");

        var missed = positives.Where(p => CredentialLeakRules.Mask(p.Text).Contains(p.Secret, StringComparison.Ordinal) || !CredentialLeakRules.HasLeak(p.Text)).Select(p => p.Name).ToList();
        missed.Should().BeEmpty("漏遮率要是 0");

        foreach (var p in positives)
        {
            var masked = CredentialLeakRules.Mask(p.Text);
            masked.Should().Contain(CredentialLeakRules.Replacement, p.Name);
            masked.Should().Contain(p.Context, $"{p.Name}：只換命中的片段");
        }
    }

    [Fact]
    public void 反例語料_誤遮率是0()
    {
        var negatives = Negatives();
        negatives.Count.Should().BeGreaterThanOrEqualTo(50, "SEC-21：反例 ≥50 筆");

        var misfired = negatives.Where(n => CredentialLeakRules.Mask(n) != n || CredentialLeakRules.HasLeak(n)).ToList();
        misfired.Should().BeEmpty("誤遮率要是 0");
    }

    [Fact]
    public void 項目渲染語料_遮蔽之後完全不變()
    {
        var count = 0;
        foreach (var corpus in ItemRendererCorpus.All())
        {
            foreach (var item in corpus.Items)
            {
                item.RequestKey = "req-" + count;
                var masked = CredentialLeakRules.MaskItem(item);
                // 開頭被截掉的（「…（前 N 字省略）」）換掉前綴之後的第一行（切點可能在權杖中間）；執行中的項目（串流到一半）
                // 再去掉尾端還沒完的權杖字元（同 MaskLive）；其餘完全不變
                var expected = JsonConvert.DeserializeObject<AgentChatItem>(JsonConvert.SerializeObject(item, ProtocolJson.Settings), ProtocolJson.Settings)!;
                expected.Source = null;   // 不上線的欄位不複製（WP4.3 計畫 D34）
                expected.Text = HeadReplaced(item.Text)!;
                expected.Detail = HeadReplaced(item.Detail);
                expected.Output = HeadReplaced(item.Output);
                if (item.Status == AgentItemStatus.Running)
                {
                    expected.Text = expected.Text?[..CredentialLeakRules.SafeLength(expected.Text)]!;
                    expected.Detail = expected.Detail?[..CredentialLeakRules.SafeLength(expected.Detail)];
                    expected.Output = expected.Output?[..CredentialLeakRules.SafeLength(expected.Output)];
                }

                JsonConvert.SerializeObject(masked, ProtocolJson.Settings).Should().Be(JsonConvert.SerializeObject(expected, ProtocolJson.Settings), item.Id);
                masked.RequestKey.Should().Be(item.RequestKey);
                masked.Seq.Should().Be(item.Seq);
                count++;
            }
        }

        count.Should().BeGreaterThan(80, "實際的項目內容（Markdown、工具輸出、差異、問卷、附件）不能被誤遮");
    }

    /// <summary>語料（沒有私鑰）裡開頭被截掉的文字預期的樣子：前綴之後到第一個換行換成 Replacement，第一行是空的不換</summary>
    private static string? HeadReplaced(string? text)
    {
        var prefix = AgentHubRules.OmittedPrefixLength(text);
        if (prefix == 0)
            return text;
        var newline = text!.IndexOf('\n', prefix);
        return newline == prefix ? text : text[..prefix] + CredentialLeakRules.Replacement + (newline < 0 ? string.Empty : text[newline..]);
    }

    // ───────────────────────────── 各樣式的邊界 ─────────────────────────────

    [Theory]
    [InlineData("github", 20)]
    [InlineData("slack", 10)]
    [InlineData("googleAccess", 20)]
    [InlineData("googleRefresh", 20)]
    [InlineData("xai", 20)]
    [InlineData("myriagentHubDevice", 16)]
    [InlineData("myriagentHubAccess", 16)]
    [InlineData("openAiLegacy", 40)]
    [InlineData("bearer", 16)]
    [InlineData("jsonTokenField", 8)]
    [InlineData("jsonTokenFieldEscaped", 8)]
    [InlineData("tokenFieldLoose", 16)]
    [InlineData("awsSecretKey", 40)]
    [InlineData("awsSessionToken", 16)]
    public void 長度下限_剛好到下限才遮(string kind, int min)
    {
        var random = new Random(min);
        string Make(int length) => kind switch
        {
            "github" => Ghp + Body(random, length),
            "slack" => Slack + Body(random, length),
            "googleAccess" => Ya29 + Body(random, length, Url),
            "googleRefresh" => GoogleRefresh + Body(random, length, Url),
            "xai" => Xai + Body(random, length),
            "myriagentHubDevice" => Chd + Body(random, length, Url),
            "myriagentHubAccess" => Cha + Body(random, length, Url),
            "openAiLegacy" => Legacy + Body(random, length),
            "bearer" => "Bearer " + Body(random, length, Url),
            "jsonTokenField" => "{\"access_token\":\"" + Body(random, length, Url) + "\"}",
            "jsonTokenFieldEscaped" => "\"{\\\"refresh_token\\\":\\\"" + Body(random, length, Url) + "\\\"}\"",
            "tokenFieldLoose" => "refresh_token : " + Body(random, length, Alnum + "-."),
            "awsSecretKey" => "aws_secret_access_key = " + Body(random, length, Alnum + "/+"),
            "awsSessionToken" => "aws_session_token = " + Body(random, length, Alnum + "/+"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        CredentialLeakRules.HasLeak("前 " + Make(min) + " 後").Should().BeTrue($"{kind}：{min} 字要遮");
        CredentialLeakRules.HasLeak("前 " + Make(min - 1) + " 後").Should().BeFalse($"{kind}：{min - 1} 字不遮");
    }

    [Fact]
    public void 固定長度_多一碼少一碼都不算_前後不能接英數字()
    {
        var random = new Random(3);
        CredentialLeakRules.HasLeak(Akia + Body(random, 16, Upper)).Should().BeTrue();
        CredentialLeakRules.HasLeak(Akia + Body(random, 15, Upper)).Should().BeFalse();
        CredentialLeakRules.HasLeak(Akia + Body(random, 17, Upper)).Should().BeFalse();
        CredentialLeakRules.HasLeak(Akia + Body(random, 16, Upper) + "x").Should().BeFalse("後面接英數字（更長的識別碼）");
        CredentialLeakRules.HasLeak("x" + Akia + Body(random, 16, Upper)).Should().BeFalse("前面接英數字");
        CredentialLeakRules.HasLeak("金鑰" + Akia + Body(random, 16, Upper) + "，").Should().BeTrue("中文字與標點不算英數字");

        CredentialLeakRules.HasLeak(Aiza + Body(random, 35, Url)).Should().BeTrue();
        CredentialLeakRules.HasLeak(Aiza + Body(random, 34, Url)).Should().BeFalse();
        CredentialLeakRules.HasLeak(Aiza + Body(random, 36, Url)).Should().BeFalse();
        CredentialLeakRules.HasLeak("\"" + Aiza + Body(random, 35, Url) + "\"").Should().BeTrue();
    }

    [Fact]
    public void 前綴夠獨特的_沒有本體也遮()
    {
        foreach (var prefix in new[] { AntOat, AntApi, Proj, SvcAcct, Admin, GithubPat })
        {
            CredentialLeakRules.HasLeak("看 " + prefix).Should().BeTrue(prefix);
            CredentialLeakRules.Mask("看 " + prefix).Should().Be("看 " + CredentialLeakRules.Replacement);
        }
    }

    [Fact]
    public void 前綴類_前一個字元是英數字不算_中文字與底線算()
    {
        var random = new Random(11);
        var body = Body(random, 36);
        CredentialLeakRules.HasLeak("x" + Ghp + body).Should().BeFalse();
        CredentialLeakRules.HasLeak("9" + Ghp + body).Should().BeFalse();
        CredentialLeakRules.HasLeak("權杖" + Ghp + body).Should().BeTrue("\\b 在中文字後面不成立，所以不用 \\b");
        CredentialLeakRules.HasLeak("_" + Ghp + body).Should().BeTrue();
        CredentialLeakRules.HasLeak("/" + Ghp + body).Should().BeTrue();
        CredentialLeakRules.HasLeak("ta" + Proj + "alpha").Should().BeFalse("task-proj-… 不是金鑰");
    }

    [Fact]
    public void 值類樣式只換值_欄位名稱留下()
    {
        var random = new Random(5);
        var value = Body(random, 40, Url);

        CredentialLeakRules.Mask($$"""{"refreshToken":"{{value}}"}""").Should().Be($$"""{"refreshToken":"{{CredentialLeakRules.Replacement}}"}""");
        CredentialLeakRules.Mask("Authorization: Bearer " + value).Should().Be("Authorization: Bearer " + CredentialLeakRules.Replacement);
        CredentialLeakRules.Mask("bearer " + value).Should().Be("bearer " + CredentialLeakRules.Replacement, "不分大小寫");
        CredentialLeakRules.Mask("__Host-myriagenthub=" + value + "; path=/").Should().Be("__Host-myriagenthub=" + CredentialLeakRules.Replacement + "; path=/");
    }

    [Fact]
    public void 兩把私鑰之間的文字不被吞掉_沒有結尾的換到最後()
    {
        var random = new Random(9);
        var a = Body(random, 64, Alnum + "+/");
        var b = Body(random, 64, Alnum + "+/");
        var text = $"{KeyBegin}\n{a}\n{KeyEnd}\n中間的說明\n{KeyBegin}\n{b}\n{KeyEnd}";
        CredentialLeakRules.Mask(text).Should().Be($"{CredentialLeakRules.Replacement}\n中間的說明\n{CredentialLeakRules.Replacement}");

        CredentialLeakRules.Mask($"開頭\n{KeyBegin}\n{a}\n後面都被截斷").Should().Be("開頭\n" + CredentialLeakRules.Replacement);
        var openSsh = "-----BEGIN " + "OPENSSH PRIVATE KEY-----";
        CredentialLeakRules.HasLeak($"{openSsh}\n{a}\n").Should().BeTrue();
        var pgp = "-----BEGIN " + "PGP PRIVATE KEY BLOCK-----";
        CredentialLeakRules.HasLeak($"{pgp}\n{a}\n").Should().BeTrue();
    }

    [Fact]
    public void 很長的權杖整段換掉_樣式只認最短的本體_之後延伸()
    {
        var random = new Random(31);
        var r = CredentialLeakRules.Replacement;
        CredentialLeakRules.Mask("a " + Ghp + Body(random, 10_000) + " b").Should().Be($"a {r} b");
        var bigJwt = Eyj + Body(random, 40, Url) + "." + Eyj + Body(random, 20_000, Url) + "." + Body(random, 400, Url);
        CredentialLeakRules.Mask("x " + bigJwt + " y").Should().Be($"x {r} y", "第二段再長也整段換掉（樣式只認到第二段的開頭）");
        CredentialLeakRules.Mask("Bearer " + Body(random, 9_000, Url)).Should().Be("Bearer " + r);
        CredentialLeakRules.Mask("__Host-myriagenthub=" + Body(random, 9_000, Url) + "; x").Should().Be($"__Host-myriagenthub={r}; x");

        // 長度不對而被拒絕的一段本體裡、以 - 隔開的合格金鑰照樣找得到
        var inner = Aiza + Body(random, 10) + "-" + Aiza + Body(random, 35, Url);
        CredentialLeakRules.Mask(inner).Should().Be(inner[..15] + r);
    }

    [Fact]
    public void 遮蔽是確定的而且冪等()
    {
        foreach (var p in Positives())
        {
            var once = CredentialLeakRules.Mask(p.Text);
            CredentialLeakRules.Mask(once).Should().Be(once, $"{p.Name}：遮過的不會再被比對到");
            CredentialLeakRules.Mask(p.Text).Should().Be(once, "同一個輸入一定得到同一個輸出（題目 id 靠它找回原題）");
            CredentialLeakRules.HasLeak(once).Should().BeFalse(p.Name);
        }

        CredentialLeakRules.Mask(null).Should().BeNull();
        CredentialLeakRules.Mask(string.Empty).Should().BeEmpty();
        CredentialLeakRules.HasLeak(null).Should().BeFalse();
        CredentialLeakRules.Replacement.All(c => c >= 128).Should().BeTrue("遮蔽的文字不含 ASCII，任何樣式都比對不到");
    }

    // ───────────────────────────── NonBacktracking ─────────────────────────────

    [Fact]
    public void 所有樣式都以NonBacktracking編譯()
    {
        CredentialLeakRules.PatternNames.Should().HaveCountGreaterThanOrEqualTo(19).And.OnlyHaveUniqueItems();
        CredentialLeakRules.PatternOptions.Should().OnlyContain(o => o.HasFlag(RegexOptions.NonBacktracking), "R6：建構時就編譯所有樣式，不支援的語法在這裡就會丟例外");
    }

    [Fact]
    public void 一MB的病態輸入_線性時間內完成()
    {
        const int size = 1 << 20;
        string Repeat(string unit) => new StringBuilder(size + unit.Length).Insert(0, unit, size / unit.Length).ToString();
        var inputs = new (string Name, string Text)[]
        {
            ("同一個字母", new string('a', size)),
            ("eyJ 重複", Repeat(Eyj)),
            ("eyJ…. 重複", Repeat(Eyj + "aaaaaaaaaaa.")),
            ("私鑰開頭重複、沒有結尾", Repeat(KeyBegin + "\n")),
            ("私鑰開頭＋不完整的結尾重複", Repeat(KeyBegin + "\n-----END RSA PRIVATE KE\n")),
            ("前綴重複", Repeat(AntOat)),
            ("JSON 欄位名稱重複", Repeat("\"access_token\": \"")),
            ("Bearer 重複", Repeat("Bearer ")),
            ("cookie 名稱重複", Repeat("__Host-myriagenthub=")),
            ("超長的 GitHub 本體", Ghp + new string('a', size)),
            ("超長的 AWS 本體", Akia + new string('A', size)),
            ("AIza 重複", Repeat(Aiza)),
            ("1//0 重複", Repeat(GoogleRefresh)),
        };

        foreach (var (name, text) in inputs)
        {
            var watch = Stopwatch.StartNew();
            _ = CredentialLeakRules.Mask(text);
            _ = CredentialLeakRules.HasLeak(text);
            _ = CredentialLeakRules.MaskJsonStrings(text);
            watch.Stop();
            output.WriteLine($"{name}：{watch.ElapsedMilliseconds} ms");
            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), $"{name}：NonBacktracking 是線性時間（回溯式的引擎在這些輸入會跑好幾分鐘）");
        }
    }

    // ───────────────────────────── 即時更新 ─────────────────────────────

    [Fact]
    public void SafeLength_任何切點都不洩漏權杖的片段()
    {
        foreach (var p in Positives().Where(p => p.Name.EndsWith("／純文字", StringComparison.Ordinal)).Concat(Positives().Where(p => !p.Name.Contains('／'))))
        {
            var text = p.Text + " 之後還有字";
            var windows = Windows(p.Secret, 6);
            for (var k = 0; k <= text.Length; k++)
            {
                var live = CredentialLeakRules.MaskLive(text[..k]);
                var leaked = windows.FirstOrDefault(w => live.Contains(w, StringComparison.Ordinal));
                leaked.Should().BeNull($"{p.Name}：切在第 {k} 字時不能送出權杖的片段");
            }
        }
    }

    [Fact]
    public void SafeLength_只留下尾端的權杖字元()
    {
        CredentialLeakRules.SafeLength(null).Should().Be(0);
        CredentialLeakRules.SafeLength("我已經修好了").Should().Be(6, "中文字不是權杖字元，不會整段被留下");
        CredentialLeakRules.SafeLength("執行 npm test").Should().Be("執行 npm ".Length);
        CredentialLeakRules.SafeLength("完成。\n").Should().Be(4);
        CredentialLeakRules.SafeLength("x=" + Ghp + "abc").Should().Be(0);
        CredentialLeakRules.SafeLength("token " + GoogleRefresh + "a\\/b").Should().Be("token ".Length);
        CredentialLeakRules.MaskLive("回覆中 " + Ghp + "abc").Should().Be("回覆中 ");
    }

    // ───────────────────────────── JSON ─────────────────────────────

    [Fact]
    public void MaskJsonStrings_跳脫過的權杖與屬性名稱也遮()
    {
        var random = new Random(13);
        var body = Body(random, 40, Url);
        var escaped = "{\"note\":\"1\\/\\/0" + body + "\",\"n\":1.50,\"at\":\"2026-10-18T01:02:03Z\"}";
        CredentialLeakRules.Mask(escaped).Should().Be(escaped, "原文的 \\/ 讓一般樣式比對不到");

        var masked = CredentialLeakRules.MaskJsonStrings(escaped);
        masked.Should().NotContain(body);
        var parsed = JObject.Parse(masked);
        parsed["note"]!.ToString().Should().Be(CredentialLeakRules.Replacement);
        masked.Should().Contain("\"at\":\"2026-10-18T01:02:03Z\"", "日期字串不被改格式").And.Contain("1.50", "數字照原樣");

        var keyed = "{\"" + Ghp + Body(random, 36) + "\":[\"值\"],\"" + Ghs + Body(random, 36) + "\":1}";
        var maskedKeys = JObject.Parse(CredentialLeakRules.MaskJsonStrings(keyed));
        maskedKeys.Properties().Select(p => p.Name).Should().Equal(CredentialLeakRules.Replacement, CredentialLeakRules.Replacement + "#2");
    }

    [Fact]
    public void MaskJsonStrings_不是一個完整的JSON值_照一般文字遮()
    {
        var random = new Random(17);
        var token = Ghp + Body(random, 36);
        CredentialLeakRules.MaskJsonStrings("[WIP] " + token).Should().Be("[WIP] " + CredentialLeakRules.Replacement);
        CredentialLeakRules.MaskJsonStrings("{\"a\":1} 之後 " + token).Should().Be("{\"a\":1} 之後 " + CredentialLeakRules.Replacement, "後面還有別的內容");
        CredentialLeakRules.MaskJsonStrings("{\"a\":").Should().Be("{\"a\":", "解析失敗");
        var untouched = "{\n  \"a\": \"b\"\n}";
        CredentialLeakRules.MaskJsonStrings(untouched).Should().BeSameAs(untouched, "沒有命中就不重新序列化");
        var deep = new string('[', 100) + new string(']', 100);
        CredentialLeakRules.MaskJsonStrings(deep).Should().Be(deep, "太深的照一般文字處理");
    }

    // ───────────────────────────── MaskItem ─────────────────────────────

    /// <summary>AgentChatItem 與它的子物件（附件、題目、選項、待辦、差異、媒體參照）每個字串欄位都填上權杖，遮蔽後一個都不能剩、也不能變空（沒複製到）</summary>
    [Fact]
    public void MaskItem_以反射列舉每個字串欄位_都被遮蔽_原本的項目不動()
    {
        var random = new Random(23);
        var tokens = new List<string>();
        string Token()
        {
            var token = Ghp + Body(random, 36);
            tokens.Add(token);
            return "值 " + token;
        }

        var item = (AgentChatItem)Fill(typeof(AgentChatItem), Token);
        var masked = CredentialLeakRules.MaskItem(item);

        masked.Should().NotBeSameAs(item);
        var maskedStrings = Strings(masked, "item").ToList();
        maskedStrings.Count.Should().BeGreaterThanOrEqualTo(25, "反射要走到所有子物件（避免空轉通過）");
        foreach (var (path, value) in maskedStrings)
        {
            value.Should().NotBeNullOrEmpty($"{path}：MaskItem 沒有複製這個欄位");
            value.Should().Contain(CredentialLeakRules.Replacement, path);
            tokens.Should().NotContain(t => value.Contains(t, StringComparison.Ordinal), $"{path}：權杖要被遮蔽");
        }

        Strings(item, "item").Select(s => s.Value).Should().OnlyContain(v => tokens.Any(t => v.Contains(t, StringComparison.Ordinal)), "本機的項目不動");
        maskedStrings.Select(s => s.Path).Should().BeEquivalentTo(Strings(item, "item").Select(s => s.Path), "每個字串欄位都在副本裡");
    }

    [Fact]
    public void MaskItem_識別碼不變_題目與答案的鍵_附件與媒體參照仍對得起來()
    {
        var random = new Random(29);
        var questionId = "要用哪把金鑰？" + Ghp + Body(random, 36);
        var media = @"C:\out\" + Xai + Body(random, 30) + ".png";
        var item = new AgentChatItem
        {
            Id = "0f3c9a52b7e94d0c8a61f2d4e5b6c7d8",
            Kind = AgentItemKind.Question,
            RequestDigest = "rd1:" + new string('c', 64),
            Questions = { new AgentQuestion { Id = questionId, Text = questionId, IsSecret = true } },
            Answers = { [questionId] = ["祕密"] },
            Attachments = { new AgentAttachment { Id = "a1b2c3", FileName = "x.png" } },
            MediaPaths = { media },
            MediaRefs = [new AgentMediaRef { Ref = "01J9QZ3K8M5R7T2V4W6X8Y0A1B", AttachmentId = "a1b2c3" }, new AgentMediaRef { Ref = "01J9QZ3K8M5R7T2V4W6X8Y0A1C", MediaPath = media }],
        };

        var masked = CredentialLeakRules.MaskItem(item);

        masked.Id.Should().Be(item.Id);
        masked.RequestDigest.Should().Be(item.RequestDigest);
        masked.MediaRefs!.Select(r => r.Ref).Should().Equal(item.MediaRefs.Select(r => r.Ref));
        masked.Questions[0].Id.Should().NotContain(Ghp).And.Be(CredentialLeakRules.Mask(questionId), "遠端回答時以同一個 Mask 找回原題");
        masked.Answers.Keys.Should().Equal(masked.Questions[0].Id);
        masked.MediaRefs![1].MediaPath.Should().Be(masked.MediaPaths[0], "MediaPaths 與 MediaRefs 的 MediaPath 經同一個函式遮蔽");

        ConversationWire.TryItem(Ordered(masked), 1, out var wire).Should().BeTrue();
        wire!["answers"]![0]!["values"]![0]!.ToString().Should().Be(ConversationWire.HiddenAnswer, "遮蔽後秘密題目仍認得出來");
        wire["media"]![0]!["ref"]!.ToString().Should().Be("01J9QZ3K8M5R7T2V4W6X8Y0A1C");
    }

    [Fact]
    public void MaskItem_存檔裡的null清單與null元素照樣是null()
    {
        // ProtocolJson 讀檔時忽略 null 值（清單留預設的空清單），手改或別的程式寫的檔仍可能讓欄位是 null：直接設
        var item = new AgentChatItem
        {
            Id = "a1",
            Kind = AgentItemKind.Tool,
            Text = null!,
            Diff = [null!, new AgentDiffLine("add", null!)],
            Attachments = null!,
            Questions = [null!],
            Answers = new Dictionary<string, List<string>> { ["q"] = null! },
            MediaPaths = [null!],
            PlanSteps = null!,
            MediaRefs = [null!],
        };

        var masked = CredentialLeakRules.MaskItem(item);

        masked.Text.Should().BeNull();
        masked.Diff.Should().HaveCount(2);
        masked.Diff![0].Should().BeNull();
        masked.Diff[1].S.Should().BeNull();
        masked.Attachments.Should().BeNull();
        masked.Questions.Should().ContainSingle().Which.Should().BeNull();
        masked.Answers["q"].Should().BeNull();
        masked.MediaPaths.Should().ContainSingle().Which.Should().BeNull();
        masked.PlanSteps.Should().BeNull();
        masked.MediaRefs.Should().ContainSingle().Which.Should().BeNull();
    }

    // ───────────────────────────── 差異、截斷與串流（合併前審查） ─────────────────────────────

    [Fact]
    public void MaskItem_差異裡的多行私鑰_從開頭到結尾每一行都換掉_投影之後也不外洩()
    {
        var random = new Random(41);
        var body = Enumerable.Range(0, 5).Select(_ => Body(random, 64, Alnum + "+/")).ToList();
        var file = string.Join("\n", new[] { KeyBegin }.Concat(body).Append(KeyEnd));
        // Claude 的 Write（新檔，全部是 add 行）、Codex 的 apply_patch 刪掉一把私鑰（unified diff 的 del 行）
        var write = DiffRules.FromNewFile(file + "\n# 後面的設定\nHost github.com");
        var unified = DiffRules.FromUnified("--- a/id_rsa\n+++ /dev/null\n@@ -1,7 +0,0 @@\n" + string.Join("\n", file.Split('\n').Select(l => "-" + l)) + "\n");

        foreach (var (name, diff) in new[] { ("Write 的新檔", write), ("apply_patch 刪除", unified) })
        {
            var item = new AgentChatItem { Id = "0f3c9a52b7e94d0c8a61f2d4e5b6c7d8", Ord = 1, Kind = AgentItemKind.Tool, Status = AgentItemStatus.Done, ToolName = "Write", Diff = diff };

            var masked = CredentialLeakRules.MaskItem(item);

            masked.Diff!.Should().HaveCount(diff.Count, name);
            masked.Diff.Select(d => d.T).Should().Equal(diff.Select(d => d.T), $"{name}：行的種類不變");
            foreach (var line in masked.Diff)
            {
                body.Should().NotContain(b => line.S.Contains(b, StringComparison.Ordinal), $"{name}：私鑰本體的每一行都要換掉");
                line.S.Should().NotContain("-----END", $"{name}：結尾標記那一行也換掉");
            }

            ConversationWire.TryItem(masked, 1, out var wire).Should().BeTrue(name);
            var json = ConversationWire.Serialize(wire!);
            body.Should().NotContain(b => json.Contains(b, StringComparison.Ordinal), $"{name}：線上的 JSON 不含私鑰");
            CredentialLeakRules.HasLeak(json).Should().BeFalse(name);
            ConversationEventRules.Validate(ConversationEventKinds.Item, json).Should().BeNull(name);
        }

        // 私鑰之後的一般內容照常；同一行開了又關的不延續到下一行
        var written = CredentialLeakRules.MaskItem(new AgentChatItem { Id = "a1", Kind = AgentItemKind.Tool, Diff = write }).Diff!;
        written[^2].S.Should().Be("# 後面的設定");
        written[^1].S.Should().Be("Host github.com");
        var inline = new List<AgentDiffLine> { new("ctx", "x = \"" + KeyBegin + "\\n" + body[0] + "\\n" + KeyEnd + "\""), new("add", "y = 1") };
        var maskedInline = CredentialLeakRules.MaskItem(new AgentChatItem { Id = "a2", Kind = AgentItemKind.Tool, Diff = inline }).Diff!;
        maskedInline[0].S.Should().NotContain(body[0]);
        maskedInline[1].S.Should().Be("y = 1");
    }

    /// <summary>中樞只留尾段時的開頭切點（AgentHubRules.TrimToolOutput 的「…（前 N 字省略）」）</summary>
    private static string HeadCut(string text, int k)
        => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"…（前 {k:N0} 字省略）\n") + text[k..];

    [Fact]
    public void 開頭被截斷的工具輸出_任何切點都不洩漏權杖的片段()
    {
        foreach (var p in Positives().Where(p => p.Name.EndsWith("／純文字", StringComparison.Ordinal) || p.Name.EndsWith("／JSON 字串", StringComparison.Ordinal))
                     .Concat(Positives().Where(p => !p.Name.Contains('／'))))
        {
            var text = p.Text + " 之後還有字\n下一行";
            var windows = Windows(p.Secret, 6);
            for (var k = 1; k < text.Length; k++)
            {
                var cut = HeadCut(text, k);
                foreach (var status in new[] { AgentItemStatus.Done, AgentItemStatus.Running })
                {
                    // Text、Detail、Output 經同一個函式（MaskText），這裡只看 Output
                    var masked = CredentialLeakRules.MaskItem(new AgentChatItem { Id = "a1", Kind = AgentItemKind.Tool, Status = status, Output = cut });
                    windows.FirstOrDefault(w => masked.Output!.Contains(w, StringComparison.Ordinal)).Should().BeNull($"{p.Name}：{status} 開頭切在第 {k} 字時不能留下權杖的片段");
                }

                var live = CredentialLeakRules.MaskLive(cut);
                windows.FirstOrDefault(w => live.Contains(w, StringComparison.Ordinal)).Should().BeNull($"{p.Name}：即時更新開頭切在第 {k} 字");
            }
        }
    }

    [Fact]
    public void 開頭被截斷_只換前綴之後的第一行_沒有截斷的照原樣()
    {
        var r = CredentialLeakRules.Replacement;
        CredentialLeakRules.MaskTruncated("…（前 5,093 字省略）\nPWhK3x\n第二行").Should().Be($"…（前 5,093 字省略）\n{r}\n第二行");
        CredentialLeakRules.MaskTruncated("…（前 12 字省略）\n沒有換行").Should().Be($"…（前 12 字省略）\n{r}");
        CredentialLeakRules.MaskTruncated("…（前 12 字省略）\n\n第一行是空的").Should().Be("…（前 12 字省略）\n\n第一行是空的");
        CredentialLeakRules.MaskTruncated("…（前 12 字省略）\n\n/usr/local/share/applications/someprogram/config\n").Should()
            .Be("…（前 12 字省略）\n\n/usr/local/share/applications/someprogram/config\n", "空的第一行之後是路徑：不是私鑰本體");

        // 剛好切在沒有結尾的私鑰開頭標記之後（第一行是空的）、切在標記裡：本體換到最後
        var keyBody = "MIIEpAIBAAKCAQEAu1SU1LfVLPHCozMxH2Mo4lgOEePzNm0tRgeLezV6ffAt0gun";
        CredentialLeakRules.MaskTruncated($"…（前 42 字省略）\n\n{keyBody}\n{keyBody}").Should().Be($"…（前 42 字省略）\n{r}");
        CredentialLeakRules.MaskTruncated($"…（前 30 字省略）\nKEY-----\n{keyBody}\n{keyBody}").Should().Be($"…（前 30 字省略）\n{r}");
        CredentialLeakRules.MaskTruncated("一般的輸出\n第二行").Should().Be("一般的輸出\n第二行");
        CredentialLeakRules.MaskTruncated("…不是省略前綴\nx").Should().Be("…不是省略前綴\nx");
        CredentialLeakRules.MaskTruncated(null).Should().BeNull();
    }

    [Fact]
    public void 執行中的項目_MaskItem也去掉尾端還沒完的權杖片段_任何尾端切點都不洩漏()
    {
        foreach (var p in Positives().Where(p => p.Name.EndsWith("／純文字", StringComparison.Ordinal)).Concat(Positives().Where(p => !p.Name.Contains('／'))))
        {
            var text = p.Text + " 之後還有字";
            var windows = Windows(p.Secret, 6);
            for (var k = 0; k <= text.Length; k++)
            {
                var masked = CredentialLeakRules.MaskItem(new AgentChatItem { Id = "a1", Kind = AgentItemKind.Assistant, Status = AgentItemStatus.Running, Text = text[..k], Output = text[..k] });
                windows.FirstOrDefault(w => masked.Text.Contains(w, StringComparison.Ordinal) || masked.Output!.Contains(w, StringComparison.Ordinal))
                    .Should().BeNull($"{p.Name}：執行中的持久快照切在第 {k} 字");
            }
        }

        // 串流到一半、還不夠長的權杖：執行中的去掉，結束的照原樣（完整的版本由樣式判斷）
        var random = new Random(43);
        var partial = "金鑰 " + Aiza + Body(random, 34, Url);
        CredentialLeakRules.MaskItem(new AgentChatItem { Id = "a1", Kind = AgentItemKind.Assistant, Status = AgentItemStatus.Running, Text = partial }).Text.Should().Be("金鑰 ");
        CredentialLeakRules.MaskItem(new AgentChatItem { Id = "a1", Kind = AgentItemKind.Assistant, Status = AgentItemStatus.Done, Text = partial }).Text.Should().Be(partial);
    }

    [Fact]
    public void MaskCut_截斷的短文字_省略號前的權杖字元先去掉()
    {
        var random = new Random(47);
        var title = AgentHubRules.MakeTitle("用 " + Proj + Body(random, 120, Url) + " 呼叫 API");
        title.Should().EndWith("…");
        CredentialLeakRules.MaskCut(title).Should().NotContain(Proj).And.EndWith("…");
        var shortCut = "設定 " + Ghp + Body(random, 10) + "…";
        CredentialLeakRules.HasLeak(shortCut).Should().BeFalse("還不夠長，樣式比對不到");
        CredentialLeakRules.MaskCut(shortCut).Should().Be("設定 …");
        CredentialLeakRules.MaskCut("沒有截斷的 " + Ghp + "abc").Should().Be("沒有截斷的 " + Ghp + "abc");
        CredentialLeakRules.MaskCut(null).Should().BeNull();
    }

    private static AgentChatItem Ordered(AgentChatItem item)
    {
        item.Ord = 1;
        return item;
    }

    private static IReadOnlyList<string> Windows(string secret, int width)
        => secret.Length <= width ? [secret] : Enumerable.Range(0, secret.Length - width + 1).Select(i => secret.Substring(i, width)).ToList();

    private static bool IsModelType(Type type) => type.IsClass && type != typeof(string) && type.Namespace == typeof(AgentChatItem).Namespace;

    /// <summary>建一個每個字串欄位都有值（含清單與字典的鍵）、子物件與清單都有一個元素的物件</summary>
    private static object Fill(Type type, Func<string> token)
    {
        var instance = type.GetConstructor(Type.EmptyTypes) is { } ctor
            ? ctor.Invoke(null)
            : type.GetConstructors().OrderBy(c => c.GetParameters().Length).First().Invoke(type.GetConstructors().OrderBy(c => c.GetParameters().Length).First().GetParameters().Select(_ => (object?)null).ToArray());
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
        {
            var t = property.PropertyType;
            if (t == typeof(string))
                property.SetValue(instance, token());
            else if (t == typeof(List<string>))
                property.SetValue(instance, new List<string> { token() });
            else if (t == typeof(Dictionary<string, List<string>>))
                property.SetValue(instance, new Dictionary<string, List<string>> { [token()] = [token()] });
            else if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>) && IsModelType(t.GetGenericArguments()[0]))
            {
                var list = (IList)Activator.CreateInstance(t)!;
                list.Add(Fill(t.GetGenericArguments()[0], token));
                property.SetValue(instance, list);
            }
            else if (IsModelType(t))
                property.SetValue(instance, Fill(t, token));
        }

        return instance;
    }

    /// <summary>物件裡每個字串（路徑、值）：字串屬性、字串清單的元素、字典的鍵與值、子物件與清單元素遞迴</summary>
    private static IEnumerable<(string Path, string Value)> Strings(object? instance, string path)
    {
        if (instance == null)
            yield break;
        foreach (var property in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var value = property.GetValue(instance);
            var at = path + "." + property.Name;
            switch (value)
            {
                case string s:
                    yield return (at, s);
                    break;
                case null when property.PropertyType == typeof(string):
                    yield return (at, null!);
                    break;
                case List<string> strings:
                    foreach (var (s, i) in strings.Select((s, i) => (s, i)))
                        yield return ($"{at}[{i}]", s);
                    break;
                case Dictionary<string, List<string>> map:
                    foreach (var (key, values) in map)
                    {
                        yield return ($"{at}.key", key);
                        foreach (var v in values)
                            yield return ($"{at}.value", v);
                    }
                    break;
                case IList list when property.PropertyType.IsGenericType && IsModelType(property.PropertyType.GetGenericArguments()[0]):
                    for (var i = 0; i < list.Count; i++)
                    {
                        foreach (var s in Strings(list[i], $"{at}[{i}]"))
                            yield return s;
                    }
                    break;
                default:
                    if (value != null && IsModelType(value.GetType()))
                    {
                        foreach (var s in Strings(value, at))
                            yield return s;
                    }
                    break;
            }
        }
    }
}
