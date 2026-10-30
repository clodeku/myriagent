using MyriagentHub.Media;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Media;

/// <summary>
/// 指令的 uploads → 上傳檢查的宣告（WP4.7 計畫 D21、步 13）：沒有 uploads 是空清單（不查），
/// 形狀不對或空陣列回 null（路由當成不通過），合格的照順序轉成 <see cref="UploadClaim"/>
/// </summary>
public sealed class CommandUploadRulesTests
{
    private static readonly string Sha = new('a', 64);

    private static JObject Upload(string id = "up_1", long size = 3, string? sha = null)
        => new() { ["uploadId"] = id, ["name"] = "a.txt", ["size"] = size, ["sha256"] = sha ?? Sha };

    private static JObject Command(JToken? uploads)
    {
        var command = new JObject { ["cmd"] = "addUploaded", ["conversationId"] = new string('0', 32) };
        if (uploads is not null)
            command["uploads"] = uploads;
        return command;
    }

    [Fact]
    public void 沒有uploads或是null_空清單()
    {
        CommandUploadRules.Claims(Command(null)).Should().BeEmpty();
        CommandUploadRules.Claims(Command(JValue.CreateNull())).Should().BeEmpty();
        CommandUploadRules.Claims(new JObject { ["cmd"] = "send", ["text"] = "hi" }).Should().BeEmpty();
    }

    [Fact]
    public void 合格的照順序轉成宣告()
    {
        var other = new string('b', 64);
        CommandUploadRules.Claims(Command(new JArray(Upload("up_1"), Upload("up-2_X", 0, other))))
            .Should().Equal(new UploadClaim("up_1", 3, Sha), new UploadClaim("up-2_X", 0, other));
    }

    [Fact]
    public void 形狀不對或空陣列_null()
    {
        CommandUploadRules.Claims(Command(new JArray())).Should().BeNull("空陣列");
        CommandUploadRules.Claims(Command(new JObject())).Should().BeNull("不是陣列");
        CommandUploadRules.Claims(Command("up_1")).Should().BeNull();
        CommandUploadRules.Claims(Command(new JArray("up_1"))).Should().BeNull("項目不是物件");
        CommandUploadRules.Claims(Command(new JArray(Upload("../x")))).Should().BeNull("uploadId 不合格");
        CommandUploadRules.Claims(Command(new JArray(Upload("")))).Should().BeNull();
        CommandUploadRules.Claims(Command(new JArray(Upload(size: -1)))).Should().BeNull("size 負數");
        CommandUploadRules.Claims(Command(new JArray(Upload(sha: Sha.ToUpperInvariant())))).Should().BeNull("sha256 要小寫");
        CommandUploadRules.Claims(Command(new JArray(Upload(sha: "abc")))).Should().BeNull();

        var stringSize = Upload();
        stringSize["size"] = "3";
        CommandUploadRules.Claims(Command(new JArray(stringSize))).Should().BeNull("size 是字串");
        var floatSize = Upload();
        floatSize["size"] = 3.0;
        CommandUploadRules.Claims(Command(new JArray(floatSize))).Should().BeNull("size 是浮點數");
        var numericId = Upload();
        numericId["uploadId"] = 1;
        CommandUploadRules.Claims(Command(new JArray(numericId))).Should().BeNull("uploadId 不是字串");
        CommandUploadRules.Claims(Command(new JArray(Upload(), Upload("bad id")))).Should().BeNull("任何一筆不合格就整批不通過");
    }
}
