using System.Text;
using Myriagent.Diagnostics;
using Myriagent.Remote;
using Myriagent.Sync;
using MyriagentHub.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Realtime;

/// <summary>
/// 瀏覽器 hub 的輸入檢查（設計 4.9.2、14.4「伺服器授權是唯一的關卡」；web-protocol 第六節第 9～11 點）。
/// 進來的 JSON 一律當成不可信：先限制深度、節點數與字串長度，再照 web-protocol 第二節的欄位逐一驗證型別與格式；
/// <c>cmd</c> 物件與 <c>ClientState</c> 裡不認得的欄位忽略（網頁多送欄位不能把連線弄壞），但仍受形狀上限約束；
/// <c>Cmd</c> 參數的頂層從嚴，只認 <c>commandId</c>、<c>envelope</c>、<c>cmd</c>（web-protocol 第六節第 4 點）。
/// <c>Cmd</c> 的上限分兩段（WP4.5 D31）：讀名稱之前值字串放寬到 <see cref="MaxRoutedStringLength"/>、屬性名照 <see cref="MaxNameLength"/>；
/// 名稱分類之後，轉送給電腦的指令只再限本文 <see cref="RemoteCommandShapeRules.MaxBodyBytes"/>（各欄位的上限交給路由時的型錄形狀檢查），
/// 其他指令照原本的 <see cref="MaxStringLength"/> 再檢查一次；<c>ClientState</c> 照舊是 <see cref="MaxStringLength"/>。
/// </summary>
internal static class MessageGuard
{
    public const int MaxDepth = 8;

    public const int MaxTokens = 4096;

    /// <summary>不轉送的指令與 <c>ClientState</c> 的字串值與屬性名上限</summary>
    public const int MaxStringLength = 4096;

    /// <summary>第一段（讀名稱之前）的值字串上限：轉送給電腦的指令的代理文字上限（<c>send.text</c> 等，WP4.5 D31）</summary>
    public const int MaxRoutedStringLength = RemoteCommandShapeRules.MaxAgentTextLength;

    /// <summary>屬性名的上限（兩段都一樣）</summary>
    public const int MaxNameLength = 4096;

    public const int MaxReorderIds = 500;

    public const int MaxOpenConversationIds = 100;

    public const int MaxTzOffsetMinutes = 840;

    /// <summary>ClientState 的 <c>features</c> 最多幾個、每個最多幾字</summary>
    public const int MaxClientFeatures = 16;

    public const int MaxClientFeatureLength = 32;

    /// <summary>網頁收 <c>itemPatch</c>（即時更新的渲染結果增量）</summary>
    public const string Patch1Feature = "patch1";

    private static readonly HashSet<string> TopLevelKeys = new(StringComparer.Ordinal) { "commandId", "envelope", "cmd" };

    /// <summary>
    /// 解析 <c>Cmd</c> 的參數 <c>{commandId?, envelope?, cmd}</c>。<see cref="CmdParse.Error"/> 不是 null 時 <see cref="CmdParse.Command"/> 是 null；
    /// <see cref="CmdParse.CmdName"/> 只在名稱認得時填（給 toast 用，不讓任意字串進每條連線的 toast 紀錄）；只靠 <c>sync</c> 前綴認得的桌面專屬名稱
    /// 一律填 <see cref="BrowserCommandRules.SyncWildcardToast"/>
    /// </summary>
    public static CmdParse ParseCmd(JToken? request)
    {
        // 第一段：還不知道是哪個指令，值字串先放寬到轉送指令的上限；第二段在讀到名稱之後
        if (request is not JObject root || !WithinShape(root, MaxRoutedStringLength, MaxNameLength))
            return Fail(null, null, BrowserErrorCodes.Invalid);
        foreach (var property in root.Properties())
        {
            if (!TopLevelKeys.Contains(property.Name))
                return Fail(null, null, BrowserErrorCodes.Invalid);
        }

        string? commandId = null;
        if (Present(root, "commandId") is { } idToken)
        {
            if (idToken.Type != JTokenType.String || (string?)idToken is not { } id || !SyncIds.IsUlid(id))
                return Fail(null, null, BrowserErrorCodes.Invalid);
            commandId = id;
        }

        if (Present(root, "envelope") is not null)
        {
            // 帶 envelope 的請求不轉送（一律 not_available），照不轉送的指令再檢查一次原本的上限
            return WithinShape(root) ? Fail(commandId, null, BrowserErrorCodes.NotAvailable) : Fail(null, null, BrowserErrorCodes.Invalid);
        }

        if (root["cmd"] is not JObject cmd || cmd["cmd"] is not { Type: JTokenType.String } nameToken)
            return Fail(commandId, null, BrowserErrorCodes.Invalid);
        var name = (string)nameToken!;
        // 第二段不過時和第一段一樣：不帶 commandId 與名稱（任意字串不進每條連線的 toast 紀錄）
        if (!WithinSecondStage(name, root, cmd))
            return Fail(null, null, BrowserErrorCodes.Invalid);
        // 認得的名稱一律帶原始的 cmd 物件（CmdParse.Body；第 4 階段的處理者自己驗它的欄位），Error 與 Command 不受影響
        switch (BrowserCommandRules.Classify(name))
        {
            case null:
                return Fail(commandId, null, BrowserErrorCodes.Invalid);
            case BrowserCommandClass.DesktopOnly:
                // 只靠 sync 前綴認得的名稱（syncA…、任意長度）一律記成同一個鍵：每條連線的 toast 紀錄最多多一筆
                return Fail(commandId, BrowserCommandRules.DesktopOnlyCommands.Contains(name) ? name : BrowserCommandRules.SyncWildcardToast, BrowserErrorCodes.DesktopOnly)
                    with { Body = cmd };
            case BrowserCommandClass.NotAvailable:
                return Fail(commandId, name, BrowserErrorCodes.NotAvailable) with { Body = cmd };
            case BrowserCommandClass.Ignored:
                return new CmdParse(commandId, name, new PassiveCommand(name), null) { Body = cmd };
            case BrowserCommandClass.Server:
                var (command, error) = ParseServerCommand(name, cmd);
                if (error is null)
                    return new CmdParse(commandId, name, command, null) { Body = cmd };
                return Fail(commandId, name, error) with { DeniedSettingKey = DeniedSettingKeyOf(name, cmd, error), Body = cmd };
            default:
                throw new ArgumentOutOfRangeException(nameof(request), "未知的指令分類");
        }
    }

    /// <summary>
    /// 請求頂層合格的 commandId（ULID）；沒有或不合格回 null。給不解析整個請求的回應（限流）照樣原樣帶回 commandId（web-protocol 第六節第 4 點）
    /// </summary>
    public static string? CommandIdOf(JToken? request) =>
        request is JObject root && root.TryGetValue("commandId", StringComparison.Ordinal, out var token)
            && token.Type == JTokenType.String && (string?)token is { } id && SyncIds.IsUlid(id)
            ? id
            : null;

    /// <summary>解析 <c>ClientState {visible, focused, openConversationIds, tzOffsetMinutes}</c>；四個欄位都要有、型別要對；null＝不合格。
    /// <c>tzOffsetMinutes</c> 照 JS <c>getTimezoneOffset()</c>（UTC 減本地，臺灣 -480）</summary>
    public static BrowserClientState? ParseClientState(JToken? state)
    {
        if (state is not JObject root || !WithinShape(root))
            return null;
        if (root["visible"] is not { Type: JTokenType.Boolean } visible || root["focused"] is not { Type: JTokenType.Boolean } focused)
            return null;
        if (root["openConversationIds"] is not JArray ids || ids.Count > MaxOpenConversationIds)
            return null;
        var list = new List<string>(ids.Count);
        foreach (var id in ids)
        {
            if (id.Type != JTokenType.String || (string?)id is not { Length: > 0 } text)
                return null;
            list.Add(text);
        }
        if (root["tzOffsetMinutes"] is not JValue { Type: JTokenType.Integer, Value: long offset } || offset is < -MaxTzOffsetMinutes or > MaxTzOffsetMinutes)
            return null;
        // 選用的 features（舊網頁沒有）：字串陣列、最多 MaxClientFeatures 個；不認得的名稱照收（只看認得的）
        var features = new List<string>();
        if (root["features"] is { } declared)
        {
            if (declared is not JArray array || array.Count > MaxClientFeatures)
                return null;
            foreach (var feature in array)
            {
                if (feature.Type != JTokenType.String || (string?)feature is not { Length: > 0 and <= MaxClientFeatureLength } name)
                    return null;
                features.Add(name);
            }
        }
        return new BrowserClientState((bool)visible, (bool)focused, list, (int)offset) { Features = features };
    }

    /// <summary>
    /// 形狀上限：容器巢狀深度 ≤ <see cref="MaxDepth"/>、節點（容器與值）≤ <see cref="MaxTokens"/>、每個字串值 ≤ <paramref name="maxString"/> 字、
    /// 每個屬性名稱 ≤ <paramref name="maxName"/> 字（兩者預設都是 <see cref="MaxStringLength"/>；字數是 UTF-16 單位）
    /// </summary>
    internal static bool WithinShape(JToken token, int maxString = MaxStringLength, int maxName = MaxStringLength)
    {
        var count = 0;
        var stack = new Stack<(JToken Token, int Depth)>();
        stack.Push((token, token is JContainer ? 1 : 0));
        while (stack.Count > 0)
        {
            var (current, depth) = stack.Pop();
            if (++count > MaxTokens || depth > MaxDepth)
                return false;
            switch (current)
            {
                case JObject obj:
                    foreach (var property in obj.Properties())
                    {
                        if (property.Name.Length > maxName)
                            return false;
                        stack.Push((property.Value, property.Value is JContainer ? depth + 1 : depth));
                    }
                    break;
                case JArray array:
                    foreach (var item in array)
                        stack.Push((item, item is JContainer ? depth + 1 : depth));
                    break;
                case JValue { Type: JTokenType.String, Value: string text } when text.Length > maxString:
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 第二段上限（WP4.5 D31）：轉送給電腦的指令（<see cref="CommandRouteRules.Routes"/>）只再限 cmd 物件的 UTF-8 本文（不縮排的 JSON）≤
    /// <see cref="RemoteCommandShapeRules.MaxBodyBytes"/>，各欄位的上限交給路由時的 <see cref="RemoteCommandShapeRules"/>（D8）；
    /// 其他指令（含 <see cref="CommandRouteRules.Deferred"/> 延後的）照原本的 <see cref="MaxStringLength"/> 再檢查一次整個請求；WP4.7 合併後檔案指令也轉送
    /// </summary>
    private static bool WithinSecondStage(string name, JObject root, JObject cmd) =>
        CommandRouteRules.Routes(name)
            ? Encoding.UTF8.GetByteCount(cmd.ToString(Formatting.None)) <= RemoteCommandShapeRules.MaxBodyBytes
            : WithinShape(root);

    private static (BrowserCommand? Command, string? Error) ParseServerCommand(string name, JObject cmd) => name switch
    {
        "ready" => ParseReady(cmd),
        "setting" => ParseSetting(cmd),
        "accountUpdate" => ParseAccountUpdate(cmd),
        "accountReorder" => ParseAccountReorder(cmd),
        "deviceRename" => ParseDeviceRename(cmd),
        "monitor" => ParseMonitor(cmd),
        "clientError" => ClientErrorRules.TryParse(cmd) is { } report ? (new ClientErrorCommand(report), null) : (null, BrowserErrorCodes.Invalid),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "伺服器處理的指令沒有對應的解析"),
    };

    /// <summary><c>ready {mode?, conversationId?}</c>：mode 只收 main、popout，沒帶是 main（web-protocol 第二節）</summary>
    private static (BrowserCommand?, string?) ParseReady(JObject cmd)
    {
        var mode = "main";
        if (Present(cmd, "mode") is { } modeToken)
        {
            if (modeToken.Type != JTokenType.String || (string?)modeToken is not ("main" or "popout"))
                return (null, BrowserErrorCodes.Invalid);
            mode = (string)modeToken!;
        }
        if (Present(cmd, "conversationId") is { Type: not JTokenType.String })
            return (null, BrowserErrorCodes.Invalid);
        return (new ReadyCommand(mode), null);
    }

    /// <summary><c>setting {key, value}</c>：四個漫遊鍵照 <see cref="PrefsRules"/>；桌面專屬的鍵 desktop_only；其他鍵一律 invalid（SEC-24）</summary>
    private static (BrowserCommand?, string?) ParseSetting(JObject cmd)
    {
        if (cmd["key"] is not { Type: JTokenType.String } keyToken)
            return (null, BrowserErrorCodes.Invalid);
        var key = (string)keyToken!;
        if (BrowserCommandRules.DesktopOnlySettingKeys.Contains(key))
            return (null, BrowserErrorCodes.DesktopOnly);
        var value = cmd["value"];
        if (!PrefsRules.Keys.Contains(key, StringComparer.Ordinal) || !PrefsRules.IsValid(key, value))
            return (null, BrowserErrorCodes.Invalid);
        return (new SettingCommand(key, value!.DeepClone()), null);
    }

    /// <summary>
    /// <c>accountUpdate {accountId, label?, color?, safeMode?, shareCustomizations?}</c>：帶了 safeMode 或 shareCustomizations 就整筆 desktop_only
    /// （精簡模式與共用預設帳戶的設定只能在各台本機改，什麼都不套用）；label、color 至少一個、都要是字串（清理與格式交給 Catalog.UpdateAccount）
    /// </summary>
    private static (BrowserCommand?, string?) ParseAccountUpdate(JObject cmd)
    {
        if (cmd["accountId"] is not { Type: JTokenType.String } idToken || (string?)idToken is not { } accountId || !SyncIds.IsLogicalAccountId(accountId))
            return (null, BrowserErrorCodes.Invalid);
        if (Present(cmd, "safeMode") is not null || Present(cmd, "shareCustomizations") is not null)
            return (null, BrowserErrorCodes.DesktopOnly);
        var label = Present(cmd, "label");
        var color = Present(cmd, "color");
        if (label is null && color is null)
            return (null, BrowserErrorCodes.Invalid);
        if (label is { Type: not JTokenType.String } || color is { Type: not JTokenType.String })
            return (null, BrowserErrorCodes.Invalid);
        return (new AccountUpdateCommand(accountId, (string?)label, (string?)color), null);
    }

    /// <summary><c>accountReorder {ids}</c>：1～500 個不重複的邏輯帳戶 id</summary>
    private static (BrowserCommand?, string?) ParseAccountReorder(JObject cmd)
    {
        if (cmd["ids"] is not JArray array || array.Count is 0 or > MaxReorderIds)
            return (null, BrowserErrorCodes.Invalid);
        var ids = new List<string>(array.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array)
        {
            if (item.Type != JTokenType.String || (string?)item is not { } id || !SyncIds.IsLogicalAccountId(id) || !seen.Add(id))
                return (null, BrowserErrorCodes.Invalid);
            ids.Add(id);
        }
        return (new AccountReorderCommand(ids), null);
    }

    /// <summary><c>deviceRename {deviceId, name}</c>：名稱要是字串（清理與長度交給 Catalog.RenameDevice）</summary>
    private static (BrowserCommand?, string?) ParseDeviceRename(JObject cmd)
    {
        if (cmd["deviceId"] is not { Type: JTokenType.String } idToken || (string?)idToken is not { } deviceId || !SyncIds.IsDeviceId(deviceId))
            return (null, BrowserErrorCodes.Invalid);
        if (cmd["name"] is not { Type: JTokenType.String } nameToken)
            return (null, BrowserErrorCodes.Invalid);
        return (new DeviceRenameCommand(deviceId, (string)nameToken!), null);
    }

    /// <summary><c>monitor {on}</c>：on 要是布林（stat1；web-protocol 第二節）</summary>
    private static (BrowserCommand?, string?) ParseMonitor(JObject cmd) =>
        cmd["on"] is { Type: JTokenType.Boolean } on ? (new MonitorCommand((bool)on), null) : (null, BrowserErrorCodes.Invalid);

    /// <summary>欄位存在而且不是 JSON null（web-protocol：省略與 null 視為相同）</summary>
    private static JToken? Present(JObject obj, string name) =>
        obj.TryGetValue(name, StringComparison.Ordinal, out var token) && token.Type != JTokenType.Null ? token : null;

    private static CmdParse Fail(string? commandId, string? name, string error) => new(commandId, name, null, error);

    /// <summary>被拒成 desktop_only 的 <c>setting</c> 的鍵（ParseSetting 只在鍵屬於桌面專屬集合時回 desktop_only，這裡再查一次集合）</summary>
    private static string? DeniedSettingKeyOf(string name, JObject cmd, string error) =>
        name == "setting" && error == BrowserErrorCodes.DesktopOnly && cmd["key"] is { Type: JTokenType.String } key
            && BrowserCommandRules.DesktopOnlySettingKeys.Contains((string)key!)
            ? (string)key!
            : null;
}

/// <summary>Cmd 的解析結果：<see cref="Error"/> 不是 null 時 <see cref="Command"/> 是 null</summary>
internal sealed record CmdParse(string? CommandId, string? CmdName, BrowserCommand? Command, string? Error)
{
    /// <summary>
    /// <c>setting</c> 被拒成 desktop_only 時的鍵（一定是 <see cref="BrowserCommandRules.DesktopOnlySettingKeys"/> 之一，集合有上限）：
    /// 處理端對每條連線每個鍵寫一次稽核；其他情況是 null
    /// </summary>
    public string? DeniedSettingKey { get; init; }

    /// <summary>
    /// 原始的 <c>cmd</c> 物件（第 4 階段的接縫，WP4.1）：名稱認得（任何分類）時才有，已過 <see cref="MessageGuard"/> 的深度、節點數與字串長度上限
    /// （轉送給電腦的指令是放寬的上限：值字串 <see cref="MessageGuard.MaxRoutedStringLength"/>、本文 <see cref="RemoteCommandShapeRules.MaxBodyBytes"/>），
    /// 但只有伺服器處理的指令驗過欄位；<c>not_available</c> 的指令交給對話處理者（WP4.2）或指令路由（WP4.5）時由它們自己照型錄驗（不可信）
    /// </summary>
    public JObject? Body { get; init; }
}

/// <summary>驗證過的網頁指令；<see cref="CommandName"/> 是 cmd 的名稱（不叫 Name：deviceRename 的參數就叫 name）</summary>
internal abstract record BrowserCommand(string CommandName);

internal sealed record ReadyCommand(string Mode) : BrowserCommand("ready");

internal sealed record SettingCommand(string Key, JToken Value) : BrowserCommand("setting");

internal sealed record AccountUpdateCommand(string AccountId, string? Label, string? Color) : BrowserCommand("accountUpdate");

internal sealed record AccountReorderCommand(IReadOnlyList<string> Ids) : BrowserCommand("accountReorder");

internal sealed record DeviceRenameCommand(string DeviceId, string Name) : BrowserCommand("deviceRename");

/// <summary>這條連線要不要收各電腦的即時狀態（stat1）</summary>
internal sealed record MonitorCommand(bool On) : BrowserCommand("monitor");

/// <summary>網頁的錯誤回報（已驗過、截短；<see cref="ClientErrorRules"/>）</summary>
internal sealed record ClientErrorCommand(ClientErrorReport Report) : BrowserCommand("clientError");

/// <summary>接受、不處理的指令（layout、open、close、visible、loadOlder）</summary>
internal sealed record PassiveCommand(string Cmd) : BrowserCommand(Cmd);

/// <summary>ClientState 的內容（第 3 階段只驗證並保存）</summary>
internal sealed record BrowserClientState(bool Visible, bool Focused, IReadOnlyList<string> OpenConversationIds, int TzOffsetMinutes)
{
    /// <summary>網頁宣告的功能（選用的 <c>features</c>；舊網頁沒有）：<c>patch1</c>＝收 <c>itemPatch</c>（web-protocol 第六節第 13 點）</summary>
    public IReadOnlyList<string> Features { get; init; } = [];

    public bool Patch1 => Features.Contains(MessageGuard.Patch1Feature, StringComparer.Ordinal);
}
