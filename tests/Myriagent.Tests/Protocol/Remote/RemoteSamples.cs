using Myriagent.Remote;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Protocol.Remote;

/// <summary>遠端規則測試共用的樣本：依型錄的欄位組出合格的指令</summary>
internal static class RemoteSamples
{
    public const string ConversationId = "5f3a0c1d2e3f4a5b6c7d8e9f0a1b2c3d";

    public const string DeviceId = "d_01J9QZ3K8M5R7T2V4W6X8Y0A1B";

    public const string LogicalAccountId = "la_01J9QZ3K8M5R7T2V4W6X8Y0A1B";

    public static readonly string Digest = "rd1:" + new string('a', 64);

    /// <summary>一個欄位的合格值</summary>
    public static JToken ValueFor(RemoteField field) => field.Kind switch
    {
        RemoteFieldKind.ConversationId => ConversationId,
        RemoteFieldKind.ItemId => "a1b2c3",
        RemoteFieldKind.Text => "x",
        RemoteFieldKind.Choice => field.Values![0],
        RemoteFieldKind.Bool => true,
        RemoteFieldKind.Digest => Digest,
        RemoteFieldKind.AccountId => "acc-1",
        RemoteFieldKind.LogicalAccountId => LogicalAccountId,
        RemoteFieldKind.DeviceId => DeviceId,
        RemoteFieldKind.RequestId => "k21.r12",
        RemoteFieldKind.Answers => new JObject { ["要怎麼彙總？"] = new JArray("依日期") },
        RemoteFieldKind.Uploads => new JArray(Upload()),
        RemoteFieldKind.OptionValue => "default",
        RemoteFieldKind.Seconds => 60,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field.Kind, null),
    };

    /// <summary>只有必填欄位的合格指令</summary>
    public static JObject Required(RemoteCommandSpec spec)
    {
        var command = new JObject { ["cmd"] = spec.Name };
        foreach (var field in spec.Fields.Where(f => f.Required))
            command[field.Name] = ValueFor(field);
        return command;
    }

    /// <summary>每個欄位都有值的合格指令</summary>
    public static JObject Full(RemoteCommandSpec spec)
    {
        var command = new JObject { ["cmd"] = spec.Name };
        foreach (var field in spec.Fields)
            command[field.Name] = ValueFor(field);
        return command;
    }

    public static JObject Upload() => new()
    {
        ["uploadId"] = "u_1",
        ["name"] = "報告.pdf",
        ["size"] = 18233,
        ["sha256"] = new string('b', 64),
    };

    public static JObject Command(string cmd, params (string Key, JToken? Value)[] fields)
    {
        var spec = RemoteCommandCatalog.Find(cmd) ?? throw new ArgumentException(cmd);
        var command = Required(spec);
        foreach (var (key, value) in fields)
        {
            if (value == null)
                command.Remove(key);
            else
                command[key] = value;
        }

        return command;
    }
}
