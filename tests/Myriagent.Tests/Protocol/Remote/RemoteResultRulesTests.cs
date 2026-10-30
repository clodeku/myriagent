using Myriagent.Remote;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Protocol.Remote;

/// <summary>
/// 裝置回報的結果與輸出（docs/sync-protocol.md 14.6、設計 14.11 D18）：被入侵的裝置回報的內容一律當成不可信——
/// 結果的 state 與 code 要對得上、層級在 0～2；輸出只收型錄給原本那個指令列的訊息種類。
/// </summary>
public sealed class RemoteResultRulesTests
{
    private const string Cid = "01J9QZ3K8M5R7T2V4W6X8Y0A1B";

    [Fact]
    public void 結果_合格的三種狀態()
    {
        RemoteResultRules.Check(new CommandResult(Cid, RemoteResultStates.Done, Tier: 1, FullAccess: false)).Should().BeNull();
        RemoteResultRules.Check(new CommandResult(Cid, RemoteResultStates.Unknown)).Should().BeNull();
        foreach (var code in RemoteResultCodes.All)
            RemoteResultRules.Check(new CommandResult(Cid, RemoteResultStates.Rejected, code, "說明", 2, true)).Should().BeNull(code);
    }

    [Fact]
    public void 結果_state與code要對得上()
    {
        RemoteResultRules.Check(new CommandResult(Cid, RemoteResultStates.Rejected)).Should().NotBeNull("rejected 一定帶 code");
        RemoteResultRules.Check(new CommandResult(Cid, RemoteResultStates.Rejected, "nope")).Should().NotBeNull("不認得的 code");
        RemoteResultRules.Check(new CommandResult(Cid, RemoteResultStates.Rejected, "STALE")).Should().NotBeNull("大小寫不同");
        RemoteResultRules.Check(new CommandResult(Cid, RemoteResultStates.Done, RemoteResultCodes.Stale)).Should().NotBeNull("done 不帶 code");
        RemoteResultRules.Check(new CommandResult(Cid, RemoteResultStates.Unknown, RemoteResultCodes.Busy)).Should().NotBeNull();
        RemoteResultRules.Check(new CommandResult(Cid, "Done")).Should().NotBeNull();
        RemoteResultRules.Check(new CommandResult(Cid, null!)).Should().NotBeNull();
    }

    [Fact]
    public void 結果_commandId_說明的長度_層級()
    {
        RemoteResultRules.Check(null).Should().NotBeNull();
        RemoteResultRules.Check(new CommandResult(Cid.ToLowerInvariant(), RemoteResultStates.Done)).Should().NotBeNull();
        RemoteResultRules.Check(new CommandResult(null!, RemoteResultStates.Done)).Should().NotBeNull();
        RemoteResultRules.Check(new CommandResult(Cid, RemoteResultStates.Done, Message: new string('字', RemoteResultRules.MaxMessageLength))).Should().BeNull();
        RemoteResultRules.Check(new CommandResult(Cid, RemoteResultStates.Done, Message: new string('字', RemoteResultRules.MaxMessageLength + 1))).Should().NotBeNull();
        RemoteResultRules.Check(new CommandResult(Cid, RemoteResultStates.Done, Tier: 0)).Should().BeNull();
        RemoteResultRules.Check(new CommandResult(Cid, RemoteResultStates.Done, Tier: 3)).Should().NotBeNull();
        RemoteResultRules.Check(new CommandResult(Cid, RemoteResultStates.Done, Tier: -1)).Should().NotBeNull();
    }

    [Fact]
    public void 輸出_只收型錄給原本那個指令列的訊息種類()
    {
        RemoteResultRules.CheckOutput(Output("files"), "listFiles").Should().BeNull();
        RemoteResultRules.CheckOutput(Output("toast"), "listFiles").Should().BeNull();
        RemoteResultRules.CheckOutput(Output("accountLogin"), "accountLogin").Should().BeNull();
        RemoteResultRules.CheckOutput(Output("accountLogin"), "listFiles").Should().NotBeNull("listFiles 只能回 files 與 toast");
        RemoteResultRules.CheckOutput(Output("files"), "ready").Should().NotBeNull("伺服器自己處理的指令沒有輸出");
        RemoteResultRules.CheckOutput(Output("files"), null).Should().NotBeNull();
        RemoteResultRules.CheckOutput(Output("Files"), "listFiles").Should().NotBeNull("大小寫不同");
    }

    [Fact]
    public void 輸出_格式不對()
    {
        RemoteResultRules.CheckOutput(null, "listFiles").Should().NotBeNull();
        RemoteResultRules.CheckOutput(new CommandOutput("x", new JObject { ["type"] = "files" }), "listFiles").Should().NotBeNull();
        RemoteResultRules.CheckOutput(new CommandOutput(Cid, null!), "listFiles").Should().NotBeNull();
        RemoteResultRules.CheckOutput(new CommandOutput(Cid, new JObject()), "listFiles").Should().NotBeNull();
        RemoteResultRules.CheckOutput(new CommandOutput(Cid, new JObject { ["type"] = 1 }), "listFiles").Should().NotBeNull();
    }

    private static CommandOutput Output(string type) => new(Cid, new JObject { ["type"] = type });
}
