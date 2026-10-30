using Myriagent.Agents;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FakeDevice;

/// <summary>使用者看得懂的失敗（訊息直接印到標準錯誤，結束碼 1）</summary>
internal sealed class FakeDeviceException(string message) : Exception(message);

/// <summary>
/// 劇本（<c>run --script 檔案.json</c>）：<c>{"steps":[{"op":…}, …]}</c>，依序執行，每一步的結果收進輸出。格式見 README。
/// 帳戶一律以別名（alias）指定：32 個十六進位字元直接當帳戶 id，其他的由它算出固定的 id（DeviceRuntime.AccountId），同一份劇本重跑時指到同一個帳戶
/// </summary>
internal static class FakeScript
{
    public static JObject Load(string path)
    {
        try
        {
            var json = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(path), new JsonSerializerSettings { DateParseHandling = DateParseHandling.None })
                ?? throw new FakeDeviceException("劇本是空的");
            if (json["steps"] is not JArray)
                throw new FakeDeviceException("劇本要有 steps 陣列");
            return json;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new FakeDeviceException($"讀不到劇本 {path}：{ex.Message}");
        }
    }

    /// <summary>
    /// 劇本最前面連續的 seedConversations（要在中樞啟動之前執行：中樞啟動時才讀對話資料夾）：寫進資料根，回傳每一步的結果
    /// （<see cref="RunAsync"/> 跳過這幾步、照樣列出結果）
    /// </summary>
    public static JArray SeedBeforeStart(string dataRoot, JObject script)
    {
        var results = new JArray();
        foreach (var token in (JArray)script["steps"]!)
        {
            if (token is not JObject step || step.Value<string>("op") != SeedOp)
                break;
            var index = results.Count + 1;
            var result = new JObject { ["step"] = index, ["op"] = SeedOp };
            try
            {
                var seeded = ConversationSeeder.Write(dataRoot, ParseSeed(step));
                result["conversations"] = seeded.Conversations;
                result["items"] = seeded.Items;
                result["sha256"] = seeded.Sha256;
                result["firstId"] = seeded.Ids[0];
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or FakeDeviceException)
            {
                throw new FakeDeviceException($"第 {index} 步（{SeedOp}）：{ex.Message}");
            }

            results.Add(result);
            Console.Error.WriteLine($"第 {index} 步 {SeedOp}：{result.ToString(Formatting.None)}");
        }

        return results;
    }

    private const string SeedOp = "seedConversations";

    /// <summary>
    /// 執行每一步，回傳每一步的結果。<paramref name="seeded"/>：已在中樞啟動之前執行的最前面幾步的結果（<see cref="SeedBeforeStart"/>）。
    /// 控制通道的 op（WP4.9 步 11：stream、nextRemoteTurn、setTitle、setPolicy、ords、remoteAudit、xssFill、restoreFromTrash、status；步 13：fakeCodexComplete、
    /// fakeCodexCalls）交給 <paramref name="control"/>；
    /// 沒給時用這次自己的一份，劇本跑完（或失敗）時等它的背景串流自己跑完。給了的話（<c>run --control-stdin</c>）背景串流接著留在控制通道裡
    /// </summary>
    public static async Task<JArray> RunAsync(DeviceRuntime device, JObject script, JArray? seeded = null, FakeControl? control = null)
    {
        var own = control is null ? new FakeControl(device) : null;
        var ops = control ?? own!;
        var results = new JArray();
        var index = 0;
        try
        {
            foreach (var token in (JArray)script["steps"]!)
            {
                index++;
                if (seeded is not null && index <= seeded.Count)
                {
                    results.Add(seeded[index - 1].DeepClone());
                    continue;
                }

                if (token is not JObject step || step.Value<string>("op") is not { } op)
                    throw new FakeDeviceException($"第 {index} 步沒有 op");
                var result = new JObject { ["step"] = index, ["op"] = op };
                try
                {
                    await RunStepAsync(device, ops, op, step, result);
                }
                catch (FakeDeviceException ex)
                {
                    throw new FakeDeviceException($"第 {index} 步（{op}）：{ex.Message}");
                }

                results.Add(result);
                Console.Error.WriteLine($"第 {index} 步 {op}：{result.ToString(Formatting.None)}");
            }
        }
        finally
        {
            if (own is not null)
            {
                var streams = await own.FinishStreamsAsync(stop: false, FakeControl.ScriptStreamsTimeout);
                if (streams.Count > 0)
                    Console.Error.WriteLine($"劇本的背景串流：{streams.ToString(Formatting.None)}");
            }
        }

        return results;
    }

    private static async Task RunStepAsync(DeviceRuntime device, FakeControl control, string op, JObject step, JObject result)
    {
        switch (op)
        {
            case "addAccount":
            {
                var account = await device.AddAccountAsync(new FakeAccountSpec(
                    Required(step, "alias"),
                    ParseProvider(Required(step, "provider")),
                    step.Value<string>("label"),
                    step.Value<string>("color"),
                    step.Value<string>("email"),
                    step.Value<string>("orgId"),
                    step.Value<string>("plan")));
                result["accountId"] = account.Id;
                break;
            }
            case "removeAccount":
                await device.CommandAsync("accountRemove", new JObject { ["accountId"] = await AccountIdAsync(device, step), ["deleteData"] = true });
                break;
            case "rename":
                await device.CommandAsync("accountUpdate", new JObject
                {
                    ["accountId"] = await AccountIdAsync(device, step),
                    ["label"] = step["label"]?.DeepClone(),
                    ["color"] = step["color"]?.DeepClone(),
                });
                break;
            case "reorder":
            {
                if (step["accounts"] is not JArray aliases)
                    throw new FakeDeviceException("reorder 要有 accounts 陣列（別名）");
                var ids = new JArray();
                foreach (var alias in aliases.Select(a => a.ToString()))
                    ids.Add(await AccountIdAsync(device, alias));
                await device.CommandAsync("accountReorder", new JObject { ["ids"] = ids });
                break;
            }
            case "cli":
                await device.SetCliAsync(ParseProvider(Required(step, "provider")), step.Value<string>("version"));
                break;
            case "poll":
                result["polled"] = await device.PollUsageAsync(Required(step, "account"), Windows(step));
                break;
            case "sessionUsage":
                await device.ReportSessionUsageAsync(Required(step, "account"), Windows(step));
                break;
            case "pref":
                await device.CommandAsync("setting", new JObject { ["key"] = Required(step, "key"), ["value"] = step["value"]?.DeepClone() });
                break;
            case "deviceRename":
            {
                var deviceId = step.Value<string>("deviceId") ?? (await device.StatusAsync())?.DeviceId ?? throw new FakeDeviceException("還沒配對：deviceRename 要給 deviceId");
                await device.CommandAsync("deviceRename", new JObject { ["deviceId"] = deviceId, ["name"] = Required(step, "name") });
                break;
            }
            case "wait":
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(step.Value<int?>("ms") ?? 1000, 0, 600_000)));
                break;
            case SeedOp:
                throw new FakeDeviceException("seedConversations 只能放在劇本最前面（中樞啟動之前寫進資料根）");
            case "chat":
            {
                ScriptedTurn? turn;
                try
                {
                    turn = step["script"] is { Type: not JTokenType.Null } script ? ScriptedTurn.Parse(script) : null;
                }
                catch (FormatException ex)
                {
                    throw new FakeDeviceException(ex.Message);
                }

                List<string> attachments = step["attachments"] switch
                {
                    null => [],
                    JArray paths when paths.All(p => p.Type == JTokenType.String) => paths.Select(p => p.ToString()).ToList(),
                    _ => throw new FakeDeviceException("attachments 要是檔案路徑的陣列"),
                };
                ChatResult chat;
                try
                {
                    chat = await device.ChatAsync(new ChatSpec(Required(step, "conversation"), step.Value<string>("account"), Required(step, "message"), attachments, turn,
                        TimeSpan.FromSeconds(Math.Clamp(step.Value<int?>("seconds") ?? 60, 1, 3600))));
                }
                catch (InvalidOperationException ex)
                {
                    throw new FakeDeviceException(ex.Message);
                }

                result["conversationId"] = chat.ConversationId;
                result["created"] = chat.Created;
                result["waiting"] = chat.Waiting;
                result["outcome"] = chat.Outcome?.ToString();
                result["items"] = chat.Items;
                result["seconds"] = chat.Seconds;
                break;
            }
            case "waitDrained":
            {
                var (drained, rows, rejected) = await device.WaitDrainedAsync(TimeSpan.FromSeconds(Math.Clamp(step.Value<int?>("seconds") ?? 60, 1, 600)));
                result["drained"] = drained;
                result["outboxRows"] = rows;
                result["rejected"] = rejected;
                if (!drained)
                    throw new FakeDeviceException("outbox 沒有在時限內清空");
                break;
            }
            case "policy":
            {
                // 這台的同步政策（WP4.6 步 13）：網頁送給中樞的 syncSetPolicy（同 deviceRename，經協調者的 SetPolicy 存設定、更新引擎的 hello 政策）。
                // 參數不合格時 SetPolicy 只記一行、整個忽略，所以回讀設定、和要求的不同就失敗（WP4.9 步 11 起和控制通道的 setPolicy 共用 DeviceRuntime.SetPolicyAsync）
                var level = Required(step, "level");
                try
                {
                    var (applied, full, notifyApplied) = await device.SetPolicyAsync(level, OptionalBool(step, "fullAccess"), OptionalBool(step, "notify"));
                    result["level"] = applied;
                    result["fullAccess"] = full;
                    result["notify"] = notifyApplied;
                }
                catch (DeviceRuntimeException ex)
                {
                    throw new FakeDeviceException(ex.Message);
                }

                break;
            }
            case "quit":
                throw new FakeDeviceException("quit 只能在控制通道（run --control-stdin）裡用");
            default:
            {
                // 控制通道的 op（WP4.9 步 11）：參數照同一份規則檢查，交給同一個執行端
                if (!FakeControlRules.Ops.Contains(op))
                    throw new FakeDeviceException($"不認得的 op：{op}");
                var args = (JObject)step.DeepClone();
                args.Remove("op");
                if (FakeControlRules.Validate(op, args) is { } problem)
                    throw new FakeDeviceException(problem);
                JObject output;
                try
                {
                    output = await control.ExecuteAsync(new FakeControlCommand(null, op, args));
                }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException or IOException or UnauthorizedAccessException or JsonException)
                {
                    throw new FakeDeviceException(ex.Message);
                }

                foreach (var property in output.Properties())
                    result[property.Name] = property.Value.DeepClone();
                break;
            }
        }
    }

    /// <summary>可省略的布林參數：沒有或 null 是 null，不是布林就失敗</summary>
    private static bool? OptionalBool(JObject step, string key) => step[key] switch
    {
        null or { Type: JTokenType.Null } => null,
        JValue { Type: JTokenType.Boolean, Value: bool value } => value,
        _ => throw new FakeDeviceException($"{key} 要是 true 或 false"),
    };

    private static async Task<string> AccountIdAsync(DeviceRuntime device, JObject step) => await AccountIdAsync(device, Required(step, "account"));

    private static async Task<string> AccountIdAsync(DeviceRuntime device, string alias) =>
        (await device.FindAccountAsync(alias))?.Id ?? throw new FakeDeviceException($"沒有帳戶 {alias}（先 addAccount）");

    /// <summary>seedConversations 的參數：count、seed、baseTimeUtc（帶時區的 ISO 8601，例如 2026-10-20T00:00:00Z）必填，其他有預設</summary>
    private static SeedSpec ParseSeed(JObject step)
    {
        var baseText = Required(step, "baseTimeUtc");
        // 一定要帶時區（Z 或 ±hh:mm）：不帶的話各電腦解讀成各自的本機時間，產生的就不是同一份
        var zoned = baseText.EndsWith('Z') || (baseText.Length > 6 && baseText[^6] is '+' or '-' && baseText[^3] == ':');
        if (!zoned || !DateTimeOffset.TryParseExact(baseText, ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"], System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var baseTime))
            throw new FakeDeviceException($"baseTimeUtc 要是帶時區的 ISO 8601（例如 2026-10-20T00:00:00Z）：{baseText}");
        return new SeedSpec(
            Int(step, "count", null),
            Int(step, "olderThanDays", 0),
            Int(step, "itemsPerConversation", 20),
            Int(step, "seed", null),
            baseTime.UtcDateTime,
            step.Value<string>("account") ?? "seed",
            ParseProvider(step.Value<string>("provider") ?? "Claude"));
    }

    private static int Int(JObject step, string key, int? fallback)
    {
        var token = step[key];
        if (token is null || token.Type == JTokenType.Null)
            return fallback ?? throw new FakeDeviceException($"缺少 {key}");
        if (token.Type != JTokenType.Integer || token.Value<long>() is < int.MinValue or > int.MaxValue)
            throw new FakeDeviceException($"{key} 要是整數");
        return token.Value<int>();
    }

    private static string Required(JObject step, string key) =>
        step[key] is JValue { Type: JTokenType.String, Value: string text } && text.Length > 0 ? text : throw new FakeDeviceException($"缺少 {key}");

    /// <summary>
    /// 四種代理（2026-10-22 起 Grok、Antigravity 也同步，設計 2026-10-22-grok-antigravity-sync）。Antigravity 沒有隔離帳戶：addAccount 建的是本機預設帳戶的形狀
    /// （DeviceRuntime.NewAccount），seedConversations 不收它（ConversationSeeder）
    /// </summary>
    private static AgentProvider ParseProvider(string name) => name switch
    {
        "Claude" => AgentProvider.Claude,
        "Codex" => AgentProvider.Codex,
        "Grok" => AgentProvider.Grok,
        "Antigravity" => AgentProvider.Antigravity,
        _ => throw new FakeDeviceException($"provider 只能是 Claude、Codex、Grok 或 Antigravity：{name}"),
    };

    private static IReadOnlyDictionary<string, double> Windows(JObject step)
    {
        if (step["windows"] is not JObject windows || windows.Count == 0)
            throw new FakeDeviceException("要有 windows（時間窗 → 百分比，例如 {\"session\":12,\"weekly\":32}）");
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in windows.Properties())
        {
            if (property.Value.Type is not (JTokenType.Integer or JTokenType.Float))
                throw new FakeDeviceException($"windows.{property.Name} 要是數字");
            result[property.Name] = property.Value.Value<double>();
        }

        return result;
    }
}
