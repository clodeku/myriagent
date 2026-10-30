using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Schedules;

/// <summary>各電腦的排程（S3，sched1）的純規則：上傳的摘要只留列入管理、疑似與定時訊息，遮蔽秘密、不帶 XML 與完整文字；格式檢查；給每台的是它以外的</summary>
public sealed class ScheduleSyncRulesTests
{
    private static JObject Local() => JObject.Parse("""
        {
          "machine": "DEMO-PC-3", "scannedAt": 1, "error": null, "failing": 1, "autoDiagnose": true, "accountId": "acc-claude",
          "entries": [
            { "path": "\\TkReportSync", "name": "TkReportSync", "origin": "agent", "managed": true, "state": "ready", "enabled": true,
              "action": "C:\\Python314\\pythonw.exe run.py --token abc123", "xml": "<Task/>", "author": "DEMO-PC-3\\User",
              "runs": [1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16], "upcoming": [],
              "provenance": { "source": "myriagent", "verb": "created", "at": 1, "conversationId": "c1", "title": "建立", "provider": "Claude", "exists": true, "cwd": "C:\\repo" },
              "management": { "conversationId": "c2", "title": "管理", "provider": "Claude", "running": false, "isDraft": false } },
            { "path": "\\Suspect", "name": "Suspect", "origin": "suspected", "action": "x.exe" },
            { "path": "\\Google", "name": "Google", "origin": "other", "action": "updater.exe" },
            { "path": "\\Noise", "name": "Noise", "origin": "ignored", "action": "n.exe" }
          ],
          "timed": [ { "id": "tm_1", "label": "日報", "text": "API_KEY=secret123 整理報表", "when": "每天 09:00", "enabled": true, "runs": [], "upcoming": [] } ],
          "devices": [ { "deviceId": "d_other", "updatedAt": 1, "snapshot": {} } ]
        }
        """);

    [Fact]
    public void ForSync_KeepsManagedRedactsAndDropsLocalOnly()
    {
        var s = ScheduleSyncRules.ForSync(Local());
        var entries = (JArray)s["entries"]!;
        entries.Select(e => e.Value<string>("path")).Should().Equal("\\TkReportSync", "\\Suspect");
        s.Value<int>("otherCount").Should().Be(2, "系統、廠商與不再列出的只送數量");
        var tk = (JObject)entries[0];
        tk.Value<string>("action").Should().Be("C:\\Python314\\pythonw.exe run.py --token ••••");
        tk["xml"].Should().BeNull();
        tk["author"].Should().BeNull();
        ((JArray)tk["runs"]!).Should().HaveCount(14);
        tk["provenance"]!["cwd"].Should().BeNull();
        tk["management"]!["isDraft"].Should().BeNull();
        s["devices"].Should().BeNull("不轉送別台的");
        s["accountId"].Should().BeNull();
        ((JArray)s["timed"]!).Single()["text"]!.Value<string>().Should().Be("API_KEY=•••• 整理報表");
        ScheduleSyncRules.CheckSnapshot(s).Should().BeNull();
    }

    [Fact]
    public void Check_AndOthers()
    {
        ScheduleSyncRules.Check(null).Should().NotBeNull();
        ScheduleSyncRules.Check(new SchedulesPublish(null)).Should().NotBeNull();
        ScheduleSyncRules.Check(new SchedulesPublish(new JObject { ["entries"] = new JArray(), ["timed"] = new JArray() })).Should().BeNull();
        ScheduleSyncRules.Check(new SchedulesPublish(new JObject { ["entries"] = new JArray(1), ["timed"] = new JArray() })).Should().NotBeNull("項目要是物件");
        ScheduleSyncRules.Check(new SchedulesPublish(new JObject { ["entries"] = new JArray(), ["timed"] = new JArray(), ["pad"] = new string('x', ScheduleSyncRules.MaxSnapshotChars) }))
            .Should().Be("摘要太大");

        var ok = new JObject { ["entries"] = new JArray(), ["timed"] = new JArray(), ["failing"] = 2 };
        var all = new[] { new DeviceSchedulesView("d_b", 1, ok), new DeviceSchedulesView("d_a", 1, ok), new DeviceSchedulesView("d_c", 1, ok) };
        ScheduleSyncRules.Others(all, "d_b").Devices.Select(d => d.DeviceId).Should().Equal("d_a", "d_c");
        ScheduleSyncRules.CheckUpdate(ScheduleSyncRules.Others(all, "d_b")).Should().BeNull();
        ScheduleSyncRules.CheckUpdate(new SchedulesUpdate([new DeviceSchedulesView("", 1, ok)])).Should().NotBeNull();
        var view = ScheduleSyncRules.BrowserView(all);
        ((JArray)view["devices"]!).Should().HaveCount(3);
        view.Value<int>("failing").Should().Be(0);
        ScheduleSyncRules.FailingOf(all).Should().Be(6);
    }
}
