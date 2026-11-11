using Myriagent.Agents;
using Myriagent.Core;

namespace Myriagent.Hub;

/// <summary>
/// 設定頁「模型設定」的純函式規則：每家一個預設模型與預設權限模式、每個模型一個預設推理強度。
/// 新對話（與換到另一家的帳戶）一律套用；對話內修改只影響那個對話；對話內換模型時推理強度換成新模型的預設。
/// 預設模型可以是「沿用上次選擇」（每個帳戶上次在對話裡選的，舊版的行為）
/// </summary>
internal static class ConversationDefaultsRules
{
    /// <summary>對照表的鍵：沒指定模型＝"default"（帳戶預設模型）</summary>
    public static string ModelKey(string? model) => string.IsNullOrWhiteSpace(model) ? "default" : model.Trim();

    /// <summary>
    /// 新對話的模型：設定頁是「沿用上次選擇」就用這個帳戶上次選的（remembered）；指定了模型而且這個帳戶有它就用它，
    /// "default" 或這個帳戶沒有這個模型＝帳戶預設模型（null）。還不知道模型清單時照設定值
    /// </summary>
    public static string? ModelFor(ProviderConversationDefaults? defaults, string? remembered, IReadOnlyList<AgentModelOption> models)
    {
        if (string.IsNullOrWhiteSpace(defaults?.Model))
            return remembered;
        var key = ModelKey(defaults.Model);
        return key != "default" && (models.Count == 0 || models.Any(m => m.Value == key)) ? key : null;
    }

    /// <summary>這個模型的預設推理強度：有設定值而且模型支援才用，否則 null（CLI 預設）</summary>
    public static string? EffortFor(ProviderConversationDefaults? defaults, string? model, IReadOnlyList<AgentModelOption> models)
        => defaults != null && defaults.Efforts.TryGetValue(ModelKey(model), out var effort)
            ? AgentHubRules.EffortForModel(effort, model, models)
            : null;

    /// <summary>對話內換模型：新模型有設定值就換成它；沒有就沿用目前的（新模型不支援就清掉）</summary>
    public static string? EffortAfterModelChange(ProviderConversationDefaults? defaults, string? currentEffort, string? newModel, IReadOnlyList<AgentModelOption> models)
        => EffortFor(defaults, newModel, models) ?? AgentHubRules.EffortForModel(currentEffort, newModel, models);

    /// <summary>新對話的權限模式：設定頁的預設（沒有＝每步詢問），這一家不支援的換成最接近的</summary>
    public static AgentPermissionMode PermissionFor(AgentProvider provider, ProviderConversationDefaults? defaults)
        => AgentPermissionModes.Normalize(provider, defaults?.PermissionMode ?? AgentPermissionMode.Ask);

    /// <summary>
    /// 升級遷移（舊版只有「新對話沿用上次選擇」）：每家的權限模式取上次用的帳戶（是這一家的話）記住的，否則依帳戶順序第一個有記錄的；
    /// 推理強度不搬（舊值是整個帳戶共用、不分模型）。每一家（AgentProviders.All）一定都有
    /// </summary>
    public static Dictionary<string, ProviderConversationDefaults> Migrate(
        IReadOnlyList<(string Id, AgentProvider Provider)> accounts, IReadOnlyDictionary<string, AccountDefaults> previous, string? lastAccountId)
    {
        var result = new Dictionary<string, ProviderConversationDefaults>();
        foreach (var provider in AgentProviders.All)
        {
            var mine = accounts.Where(a => a.Provider == provider).Select(a => a.Id).ToList();
            var ordered = mine.Where(id => id == lastAccountId).Concat(mine.Where(id => id != lastAccountId));
            var remembered = ordered.Select(id => previous.TryGetValue(id, out var d) ? d.PermissionMode : null).FirstOrDefault(m => m != null);
            result[provider.ToString()] = new ProviderConversationDefaults
            {
                PermissionMode = AgentPermissionModes.Normalize(provider, remembered ?? AgentPermissionMode.Ask),
            };
        }
        return result;
    }

    /// <summary>設定頁列出的模型：這一家各帳戶 CLI 回報的聯集（依帳戶順序、同一個模型取第一個帳戶的）；都還沒回報就用內建清單</summary>
    public static List<AgentModelOption> ProviderModels(IEnumerable<IReadOnlyList<AgentModelOption>> perAccount, IReadOnlyList<AgentModelOption> fallback)
    {
        var result = new List<AgentModelOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var models in perAccount)
        {
            foreach (var model in models)
            {
                if (seen.Add(model.Value))
                    result.Add(model);
            }
        }
        return result.Count > 0 ? result : fallback.ToList();
    }

    /// <summary>兩份模型清單的內容相同（決定要不要重送設定頁的模型清單）</summary>
    public static bool SameModels(IReadOnlyList<AgentModelOption>? a, IReadOnlyList<AgentModelOption>? b)
        => (a ?? Array.Empty<AgentModelOption>()).SequenceEqual(b ?? Array.Empty<AgentModelOption>(), ModelComparer.Instance);

    /// <summary>
    /// 回報的模型清單沒有預設強度（Claude 的會話每次啟動回報的清單不量強度）時，沿用記下的那份裡同一個模型的值；回報的有就用回報的
    /// </summary>
    public static IReadOnlyList<AgentModelOption> CarryDefaultEfforts(IReadOnlyList<AgentModelOption>? stored, IReadOnlyList<AgentModelOption> reported)
    {
        if (stored is not { Count: > 0 } || reported.All(m => m.DefaultEffort != null))
            return reported;
        var known = stored.Where(m => m.DefaultEffort != null).GroupBy(m => m.Value, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().DefaultEffort, StringComparer.Ordinal);
        return reported.Select(m => m.DefaultEffort == null && known.TryGetValue(m.Value, out var e) ? m with { DefaultEffort = e } : m).ToList();
    }

    /// <summary>有推理強度可選、卻還不知道預設強度的模型（Claude 的帳戶要不要再探查一次）</summary>
    public static bool MissingDefaultEfforts(IReadOnlyList<AgentModelOption>? models)
        => models is { Count: > 0 } && models.Any(m => m.Efforts.Count > 0 && m.DefaultEffort == null);

    /// <summary>
    /// 這個對話選「預設」強度時實際用的強度：設定檔指定的（Codex 的 config.toml model_reasoning_effort）優先，否則目前模型的預設強度；
    /// 模型是「預設」時用「預設」那一項的、沒有就用實際是哪個模型（<paramref name="resolvedModel"/>）的、再沒有就用清單第一個的。不知道回 null
    /// </summary>
    public static string? EffectiveDefaultEffort(string? model, IReadOnlyList<AgentModelOption> models, string? resolvedModel, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();
        static bool IsDefault(string? v) => string.IsNullOrEmpty(v) || v.Equals("default", StringComparison.OrdinalIgnoreCase);
        if (!IsDefault(model))
            return models.FirstOrDefault(m => string.Equals(m.Value, model, StringComparison.Ordinal))?.DefaultEffort;
        return models.FirstOrDefault(m => IsDefault(m.Value))?.DefaultEffort
               ?? (resolvedModel != null ? models.FirstOrDefault(m => string.Equals(m.Value, resolvedModel, StringComparison.Ordinal))?.DefaultEffort : null)
               ?? models.FirstOrDefault(m => !IsDefault(m.Value))?.DefaultEffort;
    }

    /// <summary>兩份「/」指令清單的內容與順序相同（CLI 每次啟動都回報一次，相同時不必存檔、不重送標頭）；沒記過的（null）一律算不同</summary>
    public static bool SameCommands(IReadOnlyList<AgentSlashCommand>? stored, IReadOnlyList<AgentSlashCommand> reported)
        => stored != null && stored.SequenceEqual(reported);

    /// <summary>設定預設權限模式：只收這一家支援的模式</summary>
    public static bool TrySetPermission(ProviderConversationDefaults defaults, AgentProvider provider, string? value)
    {
        if (!Enum.TryParse<AgentPermissionMode>(value, out var mode) || !Enum.IsDefined(mode)
            || !AgentPermissionModes.For(provider).Any(o => o.Mode == mode))
            return false;
        defaults.PermissionMode = mode;
        return true;
    }

    /// <summary>設定預設模型：空白＝沿用上次選擇（null）；其他值要在這一家的模型清單裡（"default"＝帳戶預設模型）</summary>
    public static bool TrySetModel(ProviderConversationDefaults defaults, string? value, IReadOnlyList<AgentModelOption> models)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            defaults.Model = null;
            return true;
        }
        var key = value.Trim();
        if (!models.Any(m => m.Value == key))
            return false;
        defaults.Model = key;
        return true;
    }

    /// <summary>設定某個模型的預設推理強度："default"／空白＝清除（CLI 預設）；模型不在這一家的清單、或不支援這個強度就拒絕</summary>
    public static bool TrySetEffort(ProviderConversationDefaults defaults, string? model, string? effort, IReadOnlyList<AgentModelOption> models)
    {
        var key = ModelKey(model);
        if (string.IsNullOrWhiteSpace(effort) || effort == "default")
        {
            defaults.Efforts.Remove(key);
            return true;
        }
        var option = models.FirstOrDefault(m => m.Value == key);
        if (option == null || !option.Efforts.Contains(effort, StringComparer.Ordinal))
            return false;
        defaults.Efforts[key] = effort;
        return true;
    }

    private sealed class ModelComparer : IEqualityComparer<AgentModelOption>
    {
        public static readonly ModelComparer Instance = new();

        public bool Equals(AgentModelOption? x, AgentModelOption? y)
            => x != null && y != null && x.Value == y.Value && x.Label == y.Label && x.Description == y.Description && x.Efforts.SequenceEqual(y.Efforts)
               && x.DefaultEffort == y.DefaultEffort;

        public int GetHashCode(AgentModelOption obj) => obj.Value.GetHashCode(StringComparison.Ordinal);
    }
}
