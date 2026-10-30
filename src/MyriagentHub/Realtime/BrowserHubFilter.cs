using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Realtime;

/// <summary>
/// 瀏覽器 hub 每一次呼叫的關卡（設計 14.4「伺服器授權是唯一的關卡」）：
/// <list type="bullet">
/// <item>方法允許清單：只有 <c>Cmd</c> 與 <c>ClientState</c>。第 4 階段加方法時要一起改這份清單（partial 類別新增的公開方法不會自動開放）。</item>
/// <item>再查一次呼叫者：擁有者工作階段的身分、連線已登記。</item>
/// <item>速率：每個工作階段一個權杖桶（Cmd 與 ClientState 共用）；Cmd 被限流時回 <c>rate_limited</c> 的結果、ClientState 丟 HubException。</item>
/// <item>通過時記下活動（工作階段監看以此延長閒置期限）。</item>
/// </list>
/// </summary>
internal sealed class BrowserHubFilter(BrowserConnections connections, TimeProvider time) : IHubFilter
{
    private static readonly HashSet<string> AllowedMethods = new(StringComparer.Ordinal) { BrowserHubMethods.Cmd, BrowserHubMethods.ClientState };

    public ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var method = invocationContext.HubMethodName;
        if (!AllowedMethods.Contains(method))
            throw new HubException(BrowserErrorCodes.Invalid);
        var context = invocationContext.Context;
        if (BrowserCaller.SessionId(context.User) is not { } sessionId
            || connections.Get(context.ConnectionId) is not { } connection
            || connection.SessionId != sessionId)
            throw new HubException(BrowserErrorCodes.Invalid);

        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        if (!connections.TryTakeToken(sessionId, now))
        {
            if (method != BrowserHubMethods.Cmd)
                throw new HubException(BrowserErrorCodes.RateLimited);
            var request = invocationContext.HubMethodArguments.Count > 0 ? invocationContext.HubMethodArguments[0] as JToken : null;
            return ValueTask.FromResult<object?>(new CmdResult(false, MessageGuard.CommandIdOf(request), BrowserErrorCodes.RateLimited));
        }
        connection.Touch(now);
        return next(invocationContext);
    }
}
