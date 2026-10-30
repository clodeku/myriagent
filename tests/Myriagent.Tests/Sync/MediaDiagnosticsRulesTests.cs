using Myriagent.Sync;
using R = Myriagent.Sync.RemoteFileGuardReasons;

namespace Myriagent.Tests.Sync;

/// <summary>媒體轉送與縮圖上傳不給時的記錄行（2026-10-23：原本只有轉送 id 與原因，看不出是哪個對話的哪個檔）</summary>
public class MediaDiagnosticsRulesTests
{
    private const string Ref = "0123456789ABCDEFGHJKMNPQRS";

    [Fact]
    public void Describe_寫出對話_項目_用途_參照與檔()
    {
        var row = new LocalMediaRefRow(Ref, "c1", "i1", null, @"C:\data\conversations\c1\screens\a.png", 0);
        MediaDiagnosticsRules.Describe("媒體轉送", Ref, row, "thumb", "守門員拒絕（forbidden）", null)
            .Should().Be(@"媒體轉送不給：守門員拒絕（forbidden）（對話 c1、項目 i1、thumb、參照 0123456789ABCDEFGHJKMNPQRS、檔 C:\data\conversations\c1\screens\a.png）");
        MediaDiagnosticsRules.Describe("媒體轉送", Ref, row, "media", "r", @"C:\copy.png").Should().EndWith(@"、檔 C:\copy.png）", "給了路徑就用它");
    }

    [Fact]
    public void Describe_附件寫附件id_不認得的參照寫問號()
    {
        var attachment = new LocalMediaRefRow(Ref, "c1", "i1", "att-1", null, 0);
        MediaDiagnosticsRules.Describe("縮圖上傳", Ref, attachment, "thumb", "r", null).Should().EndWith("、檔 附件 att-1）");
        MediaDiagnosticsRules.Describe("媒體轉送", null, null, "media", "這台不認得這個參照", null)
            .Should().Be("媒體轉送不給：這台不認得這個參照（對話 ?、項目 ?、media、參照 ?）");
    }

    [Theory]
    [InlineData(R.NotFound, false)]
    [InlineData(R.NotFile, false)]
    [InlineData(R.Forbidden, true)]
    [InlineData(R.Scope, true)]
    [InlineData(R.Reparse, true)]
    [InlineData(null, true)]
    public void IsUnexpectedGuardReason_檔不在是預期中的(string? reason, bool unexpected)
        => MediaDiagnosticsRules.IsUnexpectedGuardReason(reason).Should().Be(unexpected);
}
