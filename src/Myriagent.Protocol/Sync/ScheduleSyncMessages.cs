using Myriagent.Schedules;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>裝置 → 伺服器（Invoke，sched1）：這台排程頁的摘要（整份取代；<see cref="ScheduleSyncRules.ForSync"/> 產生）</summary>
internal sealed record SchedulesPublish(JObject? Snapshot);

/// <summary>一台電腦最後回報的排程摘要（伺服器存的；UpdatedAt 是伺服器收到的時間）</summary>
internal sealed record DeviceSchedulesView(string DeviceId, long UpdatedAt, JObject Snapshot);

/// <summary>伺服器 → 裝置（Send，sched1）：這台以外、沒撤銷的電腦最後回報的排程摘要</summary>
internal sealed record SchedulesUpdate(IReadOnlyList<DeviceSchedulesView> Devices);

/// <summary>
/// 各電腦的排程（排程頁的 S3；docs/sync-protocol.md 14.12，sched1）的純規則：上傳前把這台排程頁的資料變成摘要（只留列入管理、疑似與定時訊息，
/// 動作參數遮蔽秘密〔<see cref="ScheduleRedactRules"/>〕，不帶完整 XML 與定時訊息的完整文字）、伺服器與桌面檢查格式與上限、
/// 給瀏覽器版與其他桌面的 <c>devices</c>。只供顯示：伺服器不解讀內容、不影響任何判斷
/// </summary>
internal static class ScheduleSyncRules
{
    /// <summary>一份摘要序列化之後的上限（字元）</summary>
    public const int MaxSnapshotChars = 256 * 1024;

    public const int MaxEntries = 300;

    public const int MaxTimed = 200;

    /// <summary>給一台的 SchedulesUpdate 最多幾台</summary>
    public const int MaxDevices = 64;

    /// <summary>桌面兩次上傳至少隔多久（期間的變動併成一次）</summary>
    public static readonly TimeSpan PublishInterval = TimeSpan.FromSeconds(2);

    /// <summary>伺服器在連線證明完成之後等多久才送第一份 SchedulesUpdate（同 tok2：ProveResponse 還沒到裝置）</summary>
    public static readonly TimeSpan FirstSendDelay = TimeSpan.FromSeconds(5);

    /// <summary>伺服器的轉送兩次至少隔多久</summary>
    public static readonly TimeSpan FanoutInterval = TimeSpan.FromSeconds(2);

    /// <summary>伺服器沒有變動時多久重讀一次（撤銷裝置不經 Changed）</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(60);

    private static readonly string[] EntryKeys =
    {
        "path", "name", "folder", "state", "enabled", "origin", "managed", "trigger", "workFolder", "runAs", "conditions", "lastRun", "last", "nextRun",
        "missed", "streak", "autoDiagnose", "diagnoseBlock",
    };

    private static readonly string[] SyncedOrigins = { "agent", "adopted", "suspected" };

    /// <summary>這台排程頁的資料（中樞的 RenderSchedules）→ 上傳的摘要</summary>
    public static JObject ForSync(JObject local)
    {
        var entries = new JArray();
        int other = 0;
        foreach (var e in (local["entries"] as JArray ?? new JArray()).OfType<JObject>())
        {
            if (!SyncedOrigins.Contains(e.Value<string>("origin")))
            {
                other++;
                continue;
            }
            if (entries.Count >= MaxEntries)
                break;
            var o = new JObject();
            foreach (var k in EntryKeys)
            {
                if (e[k] is { } v)
                    o[k] = v.DeepClone();
            }
            o["action"] = ScheduleRedactRules.Redact(e.Value<string>("action"));
            o["runs"] = new JArray((e["runs"] as JArray ?? new JArray()).Take(14).Select(r => r.DeepClone()));
            o["upcoming"] = new JArray((e["upcoming"] as JArray ?? new JArray()).Take(48).Select(r => r.DeepClone()));
            if (e["provenance"] is JObject p)
                o["provenance"] = Pick(p, "source", "verb", "at", "conversationId", "title", "provider");
            if (e["management"] is JObject m)
                o["management"] = Pick(m, "conversationId", "title", "provider", "running");
            if (e["lastDiagnosis"] is JObject d)
                o["lastDiagnosis"] = Pick(d, "at", "conversationId");
            entries.Add(o);
        }
        var timed = new JArray();
        foreach (var t in (local["timed"] as JArray ?? new JArray()).OfType<JObject>().Take(MaxTimed))
        {
            var o = Pick(t, "id", "label", "when", "enabled", "done", "newConversation", "conversationId", "conversationTitle", "provider", "nextAt", "createdAt");
            var text = t.Value<string>("text") ?? "";
            o["text"] = ScheduleRedactRules.Redact(text.Length > 300 ? text[..300] + "…" : text);
            o["upcoming"] = new JArray((t["upcoming"] as JArray ?? new JArray()).Take(48).Select(r => r.DeepClone()));
            if (t["createdBy"] is JObject c)
                o["createdBy"] = Pick(c, "conversationId", "title", "provider");
            if (t["last"] is JObject last)
                o["last"] = last.DeepClone();
            o["runs"] = new JArray((t["runs"] as JArray ?? new JArray()).Take(14).OfType<JObject>().Select(r => Pick(r, "at", "level", "text", "late")));
            timed.Add(o);
        }
        return new JObject
        {
            ["machine"] = local["machine"]?.DeepClone(),
            ["scannedAt"] = local["scannedAt"]?.DeepClone(),
            ["error"] = local["error"]?.DeepClone(),
            ["failing"] = local["failing"]?.DeepClone(),
            ["entries"] = entries,
            ["timed"] = timed,
            ["otherCount"] = other,
        };
    }

    private static JObject Pick(JObject from, params string[] keys)
    {
        var o = new JObject();
        foreach (var k in keys)
        {
            if (from[k] is { } v)
                o[k] = v.DeepClone();
        }
        return o;
    }

    /// <summary>伺服器收到的 SchedulesPublish：null＝合格</summary>
    public static string? Check(SchedulesPublish? publish)
        => publish?.Snapshot is not { } snapshot ? "沒有摘要" : CheckSnapshot(snapshot);

    public static string? CheckSnapshot(JObject snapshot)
    {
        if (snapshot.ToString(Formatting.None).Length > MaxSnapshotChars)
            return "摘要太大";
        if (snapshot["entries"] is not JArray entries || entries.Count > MaxEntries || entries.Any(e => e is not JObject))
            return "entries 不合格";
        if (snapshot["timed"] is not JArray timed || timed.Count > MaxTimed || timed.Any(e => e is not JObject))
            return "timed 不合格";
        return null;
    }

    /// <summary>桌面收到的 SchedulesUpdate：null＝合格，否則整則略過</summary>
    public static string? CheckUpdate(SchedulesUpdate? update)
    {
        if (update?.Devices is not { } devices || devices.Count > MaxDevices)
            return "devices 不合格";
        foreach (var d in devices)
        {
            if (string.IsNullOrEmpty(d?.DeviceId) || d.Snapshot is null)
                return "裝置不合格";
            if (CheckSnapshot(d.Snapshot) is { } problem)
                return problem;
        }
        return null;
    }

    /// <summary>伺服器：給 <paramref name="selfDeviceId"/> 的——它以外的電腦（照 deviceId 排序，內容一樣才不重送）</summary>
    public static SchedulesUpdate Others(IEnumerable<DeviceSchedulesView> all, string selfDeviceId)
        => new(all.Where(d => d.DeviceId != selfDeviceId).OrderBy(d => d.DeviceId, StringComparer.Ordinal).Take(MaxDevices).ToList());

    /// <summary>
    /// 排程頁的 <c>devices</c>（瀏覽器版的整份、桌面的其他電腦）：每台 {deviceId, updatedAt, snapshot}，名稱與在不在線由網頁照 devices 對；
    /// <c>failing</c> 是各台列入管理、最近一次失敗的合計
    /// </summary>
    public static JArray DevicesView(IEnumerable<DeviceSchedulesView> devices)
        => new(devices.Select(d => new JObject { ["deviceId"] = d.DeviceId, ["updatedAt"] = d.UpdatedAt, ["snapshot"] = d.Snapshot.DeepClone() }));

    public static int FailingOf(IEnumerable<DeviceSchedulesView> devices)
        => devices.Sum(d => d.Snapshot.Value<int?>("failing") ?? 0);

    /// <summary>瀏覽器版的 schedules（ScheduleView）：沒有這台（entries、timed 空的），全部在 devices</summary>
    public static JObject BrowserView(IReadOnlyList<DeviceSchedulesView> devices) => new()
    {
        ["machine"] = null,
        ["loaded"] = true,
        ["scanning"] = false,
        ["scannedAt"] = null,
        ["error"] = null,
        ["entries"] = new JArray(),
        ["timed"] = new JArray(),
        // 瀏覽器版沒有這台：頂端的數字由網頁把各台的 failing 加起來
        ["failing"] = 0,
        ["autoDiagnose"] = null,
        ["accountId"] = null,
        ["devices"] = DevicesView(devices),
    };
}
