using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Myriagent.Security;
using Myriagent.Tests.TestSupport;
using Myriagent.Update;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Protocol.Update;

public sealed class ReleaseManifestRulesTests
{
    private static readonly ReleaseFile[] Files =
    {
        ReleaseFixtures.File("Myriagent.exe", "exe"),
        ReleaseFixtures.File("Myriagent.dll", "dll"),
        ReleaseFixtures.File("runtimes/win-x64/native/WebView2Loader.dll", "loader"),
    };

    [Fact]
    public void 正確的manifest_解析出所有欄位()
    {
        var bytes = ReleaseFixtures.ManifestBytes("0.2.1523-ab12cd3", Files);
        ReleaseManifestRules.TryParse(bytes, out var m, out var error).Should().BeTrue(error);
        m!.Identity.Release.Should().Be("0.2.1523-ab12cd3");
        m.Entry.Should().Be("Myriagent.exe");
        m.MinRuntime.Name.Should().Be("Microsoft.WindowsDesktop.App");
        m.MinRuntime.Version.ToString().Should().Be("10.0.0");
        m.ProtocolVersion.Should().Be(1);
        m.Files.Should().HaveCount(3);
        m.TotalBytes.Should().Be(Files.Sum(f => f.Size));
    }

    [Theory]
    [InlineData("name", "OtherApp")]
    [InlineData("entry", "missing.exe")]
    [InlineData("zipSha256", "xyz")]
    public void 欄位不合法_拒絕(string field, string value)
    {
        var json = JObject.Parse(Encoding.UTF8.GetString(ReleaseFixtures.ManifestBytes("0.2.1523-ab12cd3", Files)));
        json[field] = value;
        ReleaseManifestRules.TryParse(Encoding.UTF8.GetBytes(json.ToString()), out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void release與version或build不一致_拒絕()
    {
        var json = JObject.Parse(Encoding.UTF8.GetString(ReleaseFixtures.ManifestBytes("0.2.1523-ab12cd3", Files)));
        json["version"] = "0.2.1524";
        ReleaseManifestRules.TryParse(Encoding.UTF8.GetBytes(json.ToString()), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void 不是JSON或太大_拒絕()
    {
        ReleaseManifestRules.TryParse(Encoding.UTF8.GetBytes("not json"), out _, out _).Should().BeFalse();
        ReleaseManifestRules.TryParse(new byte[ReleaseManifestRules.MaxManifestBytes + 1], out _, out _).Should().BeFalse();
    }

    [Fact]
    public void 重複的欄位名稱_拒絕()
    {
        // 預設的 Newtonsoft 會讓後面的同名欄位蓋掉前面的；不同的解析器可能取不同的那一個，同一份原文就有兩種解讀
        var text = Encoding.UTF8.GetString(ReleaseFixtures.ManifestBytes("0.2.1523-ab12cd3", Files));
        var duplicated = "{\n  \"name\": \"OtherApp\"," + text[1..];
        ReleaseManifestRules.TryParse(Encoding.UTF8.GetBytes(duplicated), out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(" {}")]
    [InlineData("x")]
    [InlineData("\n{\"name\": \"OtherApp\"}")]
    [InlineData("\n[]")]
    public void manifest結尾有多餘的內容_拒絕(string suffix)
    {
        // JToken.ReadFrom 讀完第一個值就停；後面接的東西不檢查的話，合法的 manifest 後面接什麼都會通過
        var text = Encoding.UTF8.GetString(ReleaseFixtures.ManifestBytes("0.2.1523-ab12cd3", Files)) + suffix;
        ReleaseManifestRules.TryParse(Encoding.UTF8.GetBytes(text), out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\n")]
    [InlineData("\r\n\t \n")]
    public void manifest結尾只有空白或換行_仍通過(string suffix)
    {
        var text = Encoding.UTF8.GetString(ReleaseFixtures.ManifestBytes("0.2.1523-ab12cd3", Files)) + suffix;
        ReleaseManifestRules.TryParse(Encoding.UTF8.GetBytes(text), out _, out var error).Should().BeTrue(error);
    }

    [Theory]
    [InlineData("files[0].size")]
    [InlineData("totalBytes")]
    [InlineData("fileCount")]
    public void 整數超出long範圍_拒絕而不丟例外(string field)
    {
        var json = JObject.Parse(Encoding.UTF8.GetString(ReleaseFixtures.ManifestBytes("0.2.1523-ab12cd3", Files)));
        json.SelectToken(field)!.Replace(JToken.Parse("99999999999999999999"));
        ReleaseManifestRules.TryParse(Encoding.UTF8.GetBytes(json.ToString()), out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void 單檔超過大小上限_拒絕()
    {
        var huge = new[] { new ReleaseFile("Myriagent.exe", ReleaseManifestRules.MaxFileBytes + 1, new string('c', 64)) };
        ReleaseManifestRules.TryParse(ReleaseFixtures.ManifestBytes("0.2.1523-ab12cd3", huge), out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("Myriagent.exe", true)]
    [InlineData("runtimes/win-x64/native/WebView2Loader.dll", true)]
    [InlineData("Web/app.js", true)]
    [InlineData("../evil.exe", false)]
    [InlineData("a/../b.dll", false)]
    [InlineData("a//b.dll", false)]
    [InlineData("/abs.dll", false)]
    [InlineData("C:/abs.dll", false)]
    [InlineData("a\\b.dll", false)]
    [InlineData("a.dll:Zone.Identifier", false)]
    [InlineData("con.txt", false)]
    [InlineData("sub/NUL", false)]
    [InlineData("sub/COM0.dll", false)]
    [InlineData("lpt0", false)]
    [InlineData("CONIN$", false)]
    [InlineData("logs/conout$.txt", false)]
    [InlineData("nul .txt", false)]
    [InlineData("a./b.dll", false)]
    [InlineData("a /b.dll", false)]
    [InlineData("", false)]
    [InlineData(".", false)]
    public void 路徑規則(string path, bool ok) => ReleaseManifestRules.IsValidPath(path).Should().Be(ok);

    [Fact]
    public void 路徑規則_上標數字的COM與LPT也是保留名稱()
    {
        // 上標 1、2、3（U+00B9、U+00B2、U+00B3）以碼位組字，原始碼裡不放看起來像一般數字的字元
        foreach (var superscript in new[] { (char)0xB9, (char)0xB2, (char)0xB3 })
        {
            ReleaseManifestRules.IsValidPath($"COM{superscript}.dll").Should().BeFalse();
            ReleaseManifestRules.IsValidPath($"sub/lpt{superscript}").Should().BeFalse();
        }
    }

    [Fact]
    public void 清單有不合法路徑或不分大小寫重複_拒絕()
    {
        var dup = Files.Append(ReleaseFixtures.File("myriagent.EXE", "x")).ToArray();
        ReleaseManifestRules.TryParse(ReleaseFixtures.ManifestBytes("0.2.1523-ab12cd3", dup), out _, out _).Should().BeFalse();
        var bad = Files.Append(new ReleaseFile("../x.dll", 1, new string('b', 64))).ToArray();
        ReleaseManifestRules.TryParse(ReleaseFixtures.ManifestBytes("0.2.1523-ab12cd3", bad), out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("runtimes", "runtimes/win-x64/native/WebView2Loader.dll")]
    [InlineData("RUNTIMES/win-x64", "runtimes/win-x64/native/WebView2Loader.dll")]
    [InlineData("Myriagent.dll/x.txt", "Myriagent.dll/x.txt")]
    [InlineData("myriagent.EXE/a/b", "myriagent.EXE/a/b")]
    public void 清單裡檔案與資料夾同名_不分大小寫_拒絕(string extra, string clash)
    {
        // PROTO-2：「x」與「x/y」在 NTFS 上不能並存，桌面解壓時建不了資料夾（或檔案）——伺服器收件、發佈工具與桌面都經過 TryParse，一起擋
        var files = Files.Append(ReleaseFixtures.File(extra, "x")).ToArray();
        ReleaseManifestRules.FindFileFolderClash(files.Select(f => f.Path).ToList()).Should().Be(clash);

        ReleaseManifestRules.TryParse(ReleaseFixtures.ManifestBytes("0.2.1523-ab12cd3", files), out _, out var error).Should().BeFalse();
        error.Should().Contain("檔案與資料夾同名").And.Contain(clash);
        ReleaseIdentity.TryParseRelease("0.2.1523-ab12cd3", out var identity).Should().BeTrue();
        FluentActions.Invoking(() => ReleaseManifestRules.Build(identity!, "ab12cd3" + new string('0', 33), DateTimeOffset.UnixEpoch, "Myriagent.exe",
                new ReleaseRuntime("Microsoft.WindowsDesktop.App", new ReleaseVersion(10, 0, 0)), 1, new string('a', 64), files))
            .Should().Throw<ArgumentException>().WithMessage("*檔案與資料夾同名*");
    }

    [Fact]
    public void 名稱只是前綴相同_不算檔案與資料夾同名()
    {
        string[] paths = ["Web", "Web.config", "Webx/app.js", "a/b", "a/bc/d", "runtimes/win-x64/native/WebView2Loader.dll"];
        ReleaseManifestRules.FindFileFolderClash(paths).Should().BeNull("只比完整的一段：Web 與 Webx/、b 與 bc/ 不衝突");
        var files = Files.Append(ReleaseFixtures.File("runtimes/win-x64/native.txt", "x")).ToArray();
        ReleaseManifestRules.TryParse(ReleaseFixtures.ManifestBytes("0.2.1523-ab12cd3", files), out _, out var error).Should().BeTrue(error);
    }

    [Fact]
    public void 檔案清單完全相符才通過()
    {
        var expected = Files.ToList();
        var actual = Files.Select(f => (f.Path, f.Size)).ToList();
        ReleaseManifestRules.CheckFileSet(expected, actual).Should().BeNull();
        ReleaseManifestRules.CheckFileSet(expected, actual.Append(("extra.dll", 1L)).ToList()).Should().Contain("extra.dll");
        ReleaseManifestRules.CheckFileSet(expected, actual.Skip(1).ToList()).Should().Contain("Myriagent.exe");
        ReleaseManifestRules.CheckFileSet(expected, actual.Select(a => a.Path == "Myriagent.dll" ? (a.Path, a.Size + 1) : a).ToList()).Should().Contain("Myriagent.dll");
    }

    [Fact]
    public void 簽章_正確金鑰通過_竄改一個位元組失敗()
    {
        using var key = ReleaseFixtures.NewKey();
        var trusted = new[] { ReleaseFixtures.KeyOf(key) };
        var bytes = ReleaseFixtures.ManifestBytes("0.2.1523-ab12cd3", Files);
        var sig = ReleaseManifestRules.Sign(bytes, key);
        sig.Kid.Should().HaveLength(16);
        ReleaseManifestRules.VerifySignature(bytes, sig, trusted, Array.Empty<string>()).Should().BeNull();

        var tampered = bytes.ToArray();
        tampered[^3] ^= 0x01;
        ReleaseManifestRules.VerifySignature(tampered, sig, trusted, Array.Empty<string>()).Should().NotBeNull();
    }

    [Fact]
    public void 簽章_未知kid或已撤銷或沒有簽章_失敗()
    {
        using var key = ReleaseFixtures.NewKey();
        using var other = ReleaseFixtures.NewKey();
        var bytes = ReleaseFixtures.ManifestBytes("0.2.1523-ab12cd3", Files);
        var sig = ReleaseManifestRules.Sign(bytes, key);
        ReleaseManifestRules.VerifySignature(bytes, sig, new[] { ReleaseFixtures.KeyOf(other) }, Array.Empty<string>()).Should().Contain("不認得");
        ReleaseManifestRules.VerifySignature(bytes, sig, new[] { ReleaseFixtures.KeyOf(key) }, new[] { sig.Kid }).Should().Contain("撤銷");
        ReleaseManifestRules.VerifySignature(bytes, null, new[] { ReleaseFixtures.KeyOf(key) }, Array.Empty<string>()).Should().NotBeNull();
    }

    [Fact]
    public void 簽章_撤銷清單的kid大小寫不同_仍算撤銷()
    {
        // 撤銷清單來自 updater-state.json（U3 依擁有者的撤銷聲明寫入）；寫成大寫或混大小寫時撤銷不能無聲失效
        var bytes = ReleaseFixtures.ManifestBytes("0.2.1523-ab12cd3", Files);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var key = ReleaseFixtures.NewKey();
            var sig = ReleaseManifestRules.Sign(bytes, key);
            // kid 全是數字時大小寫沒有差別（16 個十六進位字全是數字的機率約萬分之五），換一把再試
            var firstLetter = sig.Kid.IndexOfAny("abcdef".ToCharArray());
            if (firstLetter < 0)
                continue;
            var trusted = new[] { ReleaseFixtures.KeyOf(key) };
            var mixed = sig.Kid[..firstLetter] + char.ToUpperInvariant(sig.Kid[firstLetter]) + sig.Kid[(firstLetter + 1)..];
            ReleaseManifestRules.VerifySignature(bytes, sig, trusted, new[] { sig.Kid.ToUpperInvariant() }).Should().Contain("撤銷");
            ReleaseManifestRules.VerifySignature(bytes, sig, trusted, new[] { mixed }).Should().Contain("撤銷");
            return;
        }
        throw new InvalidOperationException("100 把金鑰的 kid 都沒有字母，不可能發生");
    }

    [Fact]
    public void kid相同但公鑰不同的冒牌項目_仍驗不過()
    {
        using var key = ReleaseFixtures.NewKey();
        using var other = ReleaseFixtures.NewKey();
        var bytes = ReleaseFixtures.ManifestBytes("0.2.1523-ab12cd3", Files);
        var sig = ReleaseManifestRules.Sign(bytes, key);
        var fake = new ReleaseKey(sig.Kid, ReleaseFixtures.KeyOf(other).Spki);
        ReleaseManifestRules.VerifySignature(bytes, sig, new[] { fake }, Array.Empty<string>()).Should().NotBeNull();
    }

    [Fact]
    public void 簽章檔_序列化與解析往返()
    {
        using var key = ReleaseFixtures.NewKey();
        var sig = ReleaseManifestRules.Sign(ReleaseFixtures.ManifestBytes("0.2.1523-ab12cd3", Files), key);
        ReleaseManifestRules.TryParseSignature(ReleaseManifestRules.SerializeSignature(sig), out var back).Should().BeTrue();
        back.Should().Be(sig);
        ReleaseManifestRules.TryParseSignature("{\"kid\":\"x\"}", out _).Should().BeFalse();
        ReleaseManifestRules.TryParseSignature("nope", out _).Should().BeFalse();
    }

    [Fact]
    public void 簽章檔_超過大小上限_拒絕()
    {
        using var key = ReleaseFixtures.NewKey();
        var sig = ReleaseManifestRules.Sign(ReleaseFixtures.ManifestBytes("0.2.1523-ab12cd3", Files), key);
        ReleaseManifestRules.SerializeSignature(sig).Length.Should().BeLessThan(ReleaseManifestRules.MaxSignatureBytes / 4, "真正的 .manifest.sig 約 120 字元");

        // 其他部分都合法、只用多餘的 pad 欄位撐大：剛好到上限還收（多餘的欄位照舊忽略），多一個字元就拒絕
        var prefix = $"{{\"kid\":\"{sig.Kid}\",\"sig\":\"{sig.Sig}\",\"pad\":\"";
        string Padded(int length) => prefix + new string('x', length - prefix.Length - 2) + "\"}";
        ReleaseManifestRules.TryParseSignature(Padded(ReleaseManifestRules.MaxSignatureBytes), out var atLimit).Should().BeTrue();
        atLimit.Should().Be(sig);
        ReleaseManifestRules.TryParseSignature(Padded(ReleaseManifestRules.MaxSignatureBytes + 1), out var overLimit).Should().BeFalse();
        overLimit.Should().BeNull();
        ReleaseManifestRules.TryParseSignature(prefix + new string('x', 2048) + "\"}", out _).Should().BeFalse();
    }

    [Fact]
    public void 正式金鑰清單的每一把都合法()
    {
        // 擁有者產生正式金鑰（U2 設計 O3）之前清單是空的，照樣通過；之後每一把都要合格，換金鑰時可以同時有好幾把
        KeyListProblems(ReleaseKeys.Production).Should().BeEmpty();
        ReleaseKeys.Trusted(allowTestKey: false).Should().Equal(ReleaseKeys.Production, "不允許測試金鑰時信任清單就是正式清單");

        // 對照組：證明檢查本身擋得住（正式清單是空的時候，上面一把都沒檢查到）
        using var first = ReleaseFixtures.NewKey();
        using var second = ReleaseFixtures.NewKey();
        var a = ReleaseFixtures.KeyOf(first);
        var b = ReleaseFixtures.KeyOf(second);
        KeyListProblems([a, b]).Should().BeEmpty("兩把合格的金鑰（換金鑰期間）");
        KeyListProblems([a with { Kid = b.Kid }]).Should().NotBeEmpty("kid 不是由 SPKI 算出的");
        KeyListProblems([a with { Spki = "AAAA" }]).Should().NotBeEmpty("SPKI 不是 P-256 公鑰");
        KeyListProblems([a, a]).Should().NotBeEmpty("kid 重複");
    }

    /// <summary>發佈金鑰清單的結構問題（空的表示合格）：SPKI 是 P-256 公鑰、kid 是由 SPKI 算出的 16 個小寫十六進位字、kid 不重複</summary>
    private static List<string> KeyListProblems(IReadOnlyList<ReleaseKey> keys)
    {
        var problems = new List<string>();
        foreach (var k in keys)
        {
            if (!P256KeyRules.IsValidSpki(k.Spki))
                problems.Add($"{k.Kid}：SPKI 不是 P-256 公鑰");
            else if (k.Kid != ReleaseManifestRules.ComputeKid(k.Spki))
                problems.Add($"{k.Kid}：kid 不是由 SPKI 算出的");
            if (k.Kid.Length != 16 || !k.Kid.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                problems.Add($"{k.Kid}：kid 不是 16 個小寫十六進位字（信任清單以序數比對）");
        }
        problems.AddRange(keys.GroupBy(k => k.Kid, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => $"{g.Key}：kid 重複"));
        return problems;
    }

    [Fact]
    public void 測試金鑰的中繼資料只認桌面組件_別的組件帶著也不認()
    {
        using var key = ReleaseFixtures.NewKey();
        var metadata = new CustomAttributeBuilder(
            typeof(AssemblyMetadataAttribute).GetConstructor(new[] { typeof(string), typeof(string) })!,
            new object[] { ReleaseKeys.TestKeyMetadata, ReleaseFixtures.KeyOf(key).Spki });
        const string otherName = "Myriagent.Tests.OtherAssemblyWithTestKey";
        AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(otherName), AssemblyBuilderAccess.RunAndCollect, new[] { metadata });
        AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == otherName)
            .GetCustomAttributes<AssemblyMetadataAttribute>().Should().Contain(a => a.Key == ReleaseKeys.TestKeyMetadata, "前提：已載入的組件裡確實有一個帶著測試金鑰");

        ReleaseKeys.TestKeySpki().Should().BeNull("中繼資料只寫在桌面組件 Myriagent，守門測試也只檢查它");
        ReleaseKeys.Trusted(allowTestKey: true).Should().Equal(ReleaseKeys.Production, "別的組件帶著測試金鑰也不加進信任清單");
    }
}
