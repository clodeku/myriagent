using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyriagentTools.AcmeE2e;

/// <summary>
/// 結果檔：每一步完成就整份重寫（程式中途被殺也留得下已完成的步驟）。
/// ok＝true／false 是檢查；ok＝null 是只記錄現況的觀察（不影響結束碼）。
/// </summary>
internal sealed class ResultLog
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object gate = new();
    private readonly string path;
    private readonly JsonObject root;
    private readonly JsonArray steps = [];

    public ResultLog(string path, object header)
    {
        this.path = path;
        root = JsonSerializer.SerializeToNode(header, Json)!.AsObject();
        root["steps"] = steps;
        Save();
    }

    public string Path => path;

    public bool AnyFailed { get; private set; }

    public void Set(string key, object? value)
    {
        lock (gate)
        {
            root[key] = value is null ? null : JsonSerializer.SerializeToNode(value, Json);
            Save();
        }
    }

    /// <summary>記一步：ok＝null 是觀察；summary 是印在主控台的一行說明</summary>
    public void Step(string name, bool? ok, string summary, object? data = null)
    {
        lock (gate)
        {
            var node = new JsonObject
            {
                ["name"] = name,
                ["ok"] = ok,
                ["at"] = DateTimeOffset.Now.ToString("O"),
                ["summary"] = summary,
            };
            if (data is not null)
                node["data"] = JsonSerializer.SerializeToNode(data, Json);
            steps.Add(node);
            if (ok == false)
                AnyFailed = true;
            Save();
        }
        var tag = ok switch
        {
            true => "通過",
            false => "失敗",
            null => "觀察",
        };
        Console.WriteLine($"[{tag}] {name}：{summary}");
    }

    private void Save()
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(Json), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
