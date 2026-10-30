using System.Reflection;
using System.Runtime.CompilerServices;
using Myriagent.Accounts;
using Myriagent.Remote;

namespace Myriagent.Tests.Accounts;

/// <summary>
/// WP4.8 步 5（D19）：Codex 裝置碼的驗證網址，電腦端（<see cref="CodexDeviceCodeRules.IsAllowedVerificationUrl"/>）與伺服器
/// （<see cref="RemoteLoginUrlRules.IsAllowedVerificationUrl"/>）用同一份主機清單；電腦收的網址伺服器一定收（電腦只會更嚴：多了字元與長度的檢查）
/// </summary>
public class VerificationUrlConsistencyTests
{
    private const string Url = "https://auth.openai.com/codex/device";

    /// <summary>
    /// 名稱 → (網址, 電腦端的期望, 伺服器的期望)。伺服器是 null 的列只有電腦端的字元與長度檢查會擋（伺服器不檢查這些，不釘死它的結果），
    /// 只檢查「電腦收 ⇒ 伺服器收」。前段是 <c>CodexDeviceCodeRulesTests.IsAllowedVerificationUrl_只收auth_openai_com的https</c> 的全部向量
    /// </summary>
    private static readonly Dictionary<string, (string? Url, bool Desktop, bool? Server)> Rows = new()
    {
        // ---- CodexDeviceCodeRulesTests 的向量 ----
        ["實測的網址"] = (Url, true, true),
        ["協定與主機都大寫"] = ("HTTPS://AUTH.OPENAI.COM/codex/device", true, true),
        ["寫出預設埠"] = ("https://auth.openai.com:443/codex/device", true, true),
        ["verification_uri_complete"] = ("https://auth.openai.com/codex/device?user_code=ABCD-EFGH", true, true),
        ["http"] = ("http://auth.openai.com/codex/device", false, false),
        ["http_沒有路徑"] = ("http://auth.openai.com", false, false),
        ["別的主機_後面接網域"] = ("https://auth.openai.com.evil.x/codex/device", false, false),
        ["主機名稱只在查詢字串"] = ("https://evil.x/?auth.openai.com", false, false),
        ["前面多一個字"] = ("https://xauth.openai.com/codex/device", false, false),
        ["上層網域"] = ("https://openai.com/codex/device", false, false),
        ["結尾的點"] = ("https://auth.openai.com./codex/device", false, false),
        ["帶帳號"] = ("https://user@auth.openai.com/codex/device", false, false),
        ["帶帳號密碼"] = ("https://user:pw@auth.openai.com/codex/device", false, false),
        ["非預設埠"] = ("https://auth.openai.com:8443/codex/device", false, false),
        ["相對網址"] = ("/codex/device", false, false),
        ["沒有協定"] = ("auth.openai.com/codex/device", false, false),
        ["javascript"] = ("javascript:alert(1)", false, false),
        ["含空白"] = ("https://auth.openai.com/codex/device x", false, null),
        ["含換行"] = (Url + (char)10, false, null),
        ["太長"] = ("https://auth.openai.com/" + new string('a', 2100), false, null),
        ["路徑非ASCII"] = ("https://auth.openai.com/" + (char)0x00E9, false, null),
        ["空字串"] = ("", false, false),
        ["null"] = (null, false, false),
        // ---- 另外加的 ----
        ["只有主機大寫"] = ("https://AUTH.openai.com/codex/device", true, true),
        ["沒有路徑"] = ("https://auth.openai.com", true, true),
        ["大寫加結尾的點"] = ("https://AUTH.OPENAI.COM./codex/device", false, false),
        ["punycode主機"] = ("https://xn--uth-1na.openai.com/codex/device", false, false),
        ["同形字主機_西里爾字母a"] = ("https://" + (char)0x0430 + "uth.openai.com/codex/device", false, false),
        ["空的帳密"] = ("https://:@auth.openai.com/codex/device", false, false),
        ["白名單主機當帳號_實際主機是別的"] = ("https://auth.openai.com@evil.x/codex/device", false, false),
        ["https走80埠"] = ("https://auth.openai.com:80/codex/device", false, false),
    };

    public static TheoryData<string> RowNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Rows.Keys)
            data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(RowNames))]
    public void 同一組向量_電腦收的伺服器一定收(string row)
    {
        var (url, desktop, server) = Rows[row];
        var gotDesktop = CodexDeviceCodeRules.IsAllowedVerificationUrl(url);
        var gotServer = RemoteLoginUrlRules.IsAllowedVerificationUrl(url);

        gotDesktop.Should().Be(desktop, row);
        if (server is { } expected)
            gotServer.Should().Be(expected, row);
        if (gotDesktop)
            gotServer.Should().BeTrue($"{row}：電腦送出的驗證網址，伺服器不能丟掉（主機清單只有一份）");
    }

    [Fact]
    public void 主機清單是Codex與Grok的()
    {
        // Grok 的遠端登入（設計 2026-10-22-grok-antigravity-sync 的 P3）加了 accounts.x.ai
        RemoteLoginUrlRules.VerificationHosts.Should().BeEquivalentTo(["auth.openai.com", "accounts.x.ai"]);
    }

    [Fact]
    public void 電腦端沒有自己的主機清單()
    {
        // D19：主機只在 RemoteLoginUrlRules（原本的 VerificationHost 常數已刪掉）；電腦端再放一份就會和伺服器分歧
        HostMembers(typeof(RemoteLoginUrlRules)).Should().Equal(["VerificationHosts"], "陽性對照：掃描找得到唯一的那一份");
        HostMembers(typeof(CodexDeviceCodeRules)).Should().BeEmpty();
    }

    /// <summary>型別裡值含 openai.com 的靜態欄位（含常數）與屬性（字串或字串集合）的名稱</summary>
    private static List<string> HostMembers(Type type)
    {
        const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        static bool NamesHost(object? value) => value switch
        {
            string s => s.Contains("openai.com", StringComparison.OrdinalIgnoreCase),
            IEnumerable<string> hosts => hosts.Any((h) => h.Contains("openai.com", StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
        // 自動屬性的支援欄位不另算（屬性本身會列出）
        return type.GetFields(Static)
            .Where((f) => !f.IsDefined(typeof(CompilerGeneratedAttribute), false) && NamesHost(f.GetValue(null)))
            .Select((f) => f.Name)
            .Concat(type.GetProperties(Static)
                .Where((p) => p.GetIndexParameters().Length == 0 && NamesHost(p.GetValue(null)))
                .Select((p) => p.Name))
            .ToList();
    }
}
