using System.Security.Cryptography;
using System.Text;
using Myriagent.Agents;
using Myriagent.Sync;
using MyriagentHub.Rendering;
using MyriagentHub.Sync;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Rendering;

/// <summary>伺服器渲染語料的一筆：名稱與線上的 <c>conv.item</c> payload（<see cref="Ord"/> 是它的 Ord）</summary>
internal sealed record HubItemCase(string Name, long Ord, JObject Payload)
{
    public string Id => (string)Payload["id"]!;

    /// <summary>照 items 表存的樣子（PayloadRules 編碼）當成渲染的來源</summary>
    public HubItemSource Source()
    {
        var (enc, body) = PayloadRules.Encode(ConversationWire.Serialize(Payload));
        return new HubItemSource(HubItemCorpus.Conversation, HubItemCorpus.Owner, HubItemCorpus.WorkingDirectory, Id, Ord, body, enc, HubItemCorpus.ReceivedAt);
    }
}

/// <summary>
/// 伺服器渲染的語料（HubItemRendererTests 與黃金檔 hub-items.json，WP4.2 計畫步 7）：每種 kind、附件與媒體（有參照與沒有參照、可播放與不可播放）、
/// requestDigest、risk（Normal、High、不認得的值）、答案（含秘密題目與 id 是 path 的題目）、待辦、差異、noticeKind（認得與不認得的）、
/// blob 參照（換得回的文字與陣列、換不回的）、裝置多送的 html／url 欄位、沒有與轉換不了的 createdAt。
/// 項目由桌面模型經 <see cref="ConversationWire.TryItem"/> 產生（和 WP4.3 發布的一樣），線上才表示得出來的狀態再直接改 payload。
/// 每次呼叫都建立新的物件；時間一律 UTC
/// </summary>
internal static class HubItemCorpus
{
    public const string Conversation = "0123456789abcdef0123456789abcdef";

    /// <summary>擁有者裝置（附件與媒體的參照是它發的）</summary>
    public const string Owner = "dev-owner-0001";

    public const string WorkingDirectory = @"C:\repo\myriagent";

    /// <summary>伺服器收到事件的時間（2026-07-15 前後）：項目沒有 createdAt 時當成建立時間</summary>
    public const long ReceivedAt = 1_784_080_000_000;

    /// <summary>卡片的摘要（rd1，只原樣轉送）</summary>
    public const string Digest = "rd1:Q2xvZGVIdWItZ29sZGVuLWRpZ2VzdC0wMDAwMDAwMDA";

    /// <summary>裝置多送的欄位值的記號：渲染結果裡不能出現</summary>
    public const string DeviceMark = "DEVICE-SENT";

    private static readonly DateTime Start = new(2026, 7, 15, 1, 0, 0, DateTimeKind.Utc);

    /// <summary>第 <paramref name="n"/> 個媒體參照（26 個 Crockford Base32 字）</summary>
    public static string Ref(int n)
        => "0123456789ABCDEFGHJKMNPQ" + CrockfordBase32.Alphabet[n / 32 % 32] + CrockfordBase32.Alphabet[n % 32];

    public static HubCorpus Build()
    {
        var b = new Builder();

        b.Add("user-attachments", AgentItemKind.User, AgentItemStatus.Done, "幫我看這些檔案：a < b & \"引號\"", i =>
        {
            i.Attachments =
            [
                Attachment("att-image", "截圖.png", AgentAttachmentKind.Image, 186_368),
                Attachment("att-video", "重現步驟.mp4", AgentAttachmentKind.Video, 1_258_291, frames: 6, duration: 3.4),
                Attachment("att-avi", "螢幕錄影.avi", AgentAttachmentKind.Video, 2L * 1024 * 1024 * 1024, frames: 6, duration: 3725.5),
                Attachment("att-video-noref", "沒有參照.mp4", AgentAttachmentKind.Video, 1536, frames: 2),
                Attachment("att-doc", "agent.log", AgentAttachmentKind.Document, 12_288),
                Attachment("att-image-noref", "沒有參照.png", AgentAttachmentKind.Image, 1023),
            ];
            i.MediaRefs =
            [
                new AgentMediaRef { Ref = Ref(1), AttachmentId = "att-image" },
                new AgentMediaRef { Ref = Ref(2), AttachmentId = "att-video" },
                new AgentMediaRef { Ref = Ref(3), AttachmentId = "att-avi" },
                new AgentMediaRef { Ref = Ref(4), AttachmentId = "att-doc" },
            ];
        });
        b.Add("assistant-markdown", AgentItemKind.Assistant, AgentItemStatus.Done, """
            ### 結果

            | 情境 | 結果 |
            |:---|---:|
            | 關閉視窗 | 立即回報 |

            ```C#
            var ok = a < b && c > d;
            ```

            本機圖片：![圖表](C:\repo\myriagent\assets\chart.png) 本機檔案：[a.cs](src/a.cs#L3)
            外站：[官網](https://example.com/?a=1&b=2) 危險：[點我](javascript:alert(1)) 原始 HTML：<img src=x onerror=alert(1)>

            - [x] 已完成
            - [ ] 還沒做
            """);
        b.Add("thinking", AgentItemKind.Thinking, AgentItemStatus.Running, "先看 `LoginAsync` 怎麼等登入結果");
        b.Add("tool-bash", AgentItemKind.Tool, AgentItemStatus.Done, string.Empty, i =>
        {
            Card(i, "bash", "執行指令", "dotnet test");
            i.Detail = "dotnet test -c Release";
            i.Output = "…（前 1,234 字省略）\n已通過! - 失敗: 0 <ok> & \"done\"";
        });
        b.Add("tool-edit-diff", AgentItemKind.Tool, AgentItemStatus.Done, string.Empty, i =>
        {
            Card(i, "edit", "修改檔案", @"src\a.cs");
            i.FilePath = WorkingDirectory + @"\src\a.cs";
            i.Diff = DiffRules.FromEdit("int a = 1;\nint b = 2;", "int a = 1;\nlong b = 2;");
        });
        b.Add("approval-high", AgentItemKind.Approval, AgentItemStatus.Waiting, string.Empty, i =>
        {
            Card(i, "bash", "執行指令", "git push origin main");
            i.Detail = "推送到遠端";
            i.AllowSessionLabel = "本對話同樣的指令都允許";
            i.RequestDigest = Digest;
            i.Risk = AgentRemoteRisk.High;
        });
        b.Add("approval-normal", AgentItemKind.Approval, AgentItemStatus.Done, string.Empty, i =>
        {
            Card(i, "edit", "修改檔案", @"src\b.cs");
            i.Decision = "allow";
            i.RequestDigest = Digest;
            i.Risk = AgentRemoteRisk.Normal;
        });
        b.Add("approval-unknown-risk", AgentItemKind.Approval, AgentItemStatus.Waiting, string.Empty, i =>
        {
            Card(i, "mcp", "MCP：github", "create_issue");
            i.CanAllowForSession = false;
            i.RequestDigest = Digest;
        }, p => p["risk"] = "Extreme");
        b.Add("approval-no-digest", AgentItemKind.Approval, AgentItemStatus.Waiting, string.Empty, i => Card(i, "bash", "執行指令", "npm test"));
        b.Add("question-answers", AgentItemKind.Question, AgentItemStatus.Done, string.Empty, i =>
        {
            i.Questions =
            [
                Question("q1", "分支", "放在哪個分支？", false, "直接改 main", "開新分支"),
                Question("token", "權杖", "請輸入部署權杖", false),
                Question("q3", null, "要補哪些測試？", true, "逾時", "關閉視窗"),
            ];
            i.Questions[1].IsSecret = true;
            i.Answers = new Dictionary<string, List<string>>
            {
                ["q1"] = ["開新分支"],
                ["token"] = ["不該出現的明文"],
                ["q3"] = ["逾時", "關閉視窗", "另外：斷線重試"],
            };
            i.Decision = "answered";
            i.RequestDigest = Digest;
        });
        b.Add("question-id-path", AgentItemKind.Question, AgentItemStatus.Waiting, string.Empty, i =>
        {
            i.Questions = [Question("path", "路徑", "要放在哪個資料夾？", false, "src", "docs")];
            i.Answers = new Dictionary<string, List<string>> { ["path"] = ["docs"] };
            i.AnswerAsMessage = true;
            i.RequestDigest = Digest;
            i.Risk = AgentRemoteRisk.Normal;
        });
        b.Add("plan-approval", AgentItemKind.PlanApproval, AgentItemStatus.Waiting, "PlanApproval 的 Text 不顯示", i =>
        {
            Card(i, "plan", "確認執行計畫", "代理已規劃好做法，請確認是否照計畫執行");
            i.CanAllowForSession = false;
            i.Detail = "#### 計畫\n\n1. 修正逾時\n2. 補測試\n\n![流程圖](docs/flow.png) 見[規格](docs/spec.md)";
            i.RequestDigest = Digest;
            i.Risk = AgentRemoteRisk.High;
        });
        b.Add("plan", AgentItemKind.Plan, AgentItemStatus.Running, "先修 **逾時**", i => i.PlanSteps =
        [
            new AgentPlanStep { Text = "找出原因", Status = "completed" },
            new AgentPlanStep { Text = "修正", Status = "inProgress" },
            new AgentPlanStep { Text = "補測試", Status = "pending" },
        ]);
        b.Add("notice-cli", AgentItemKind.Notice, AgentItemStatus.Done, "已用 Claude Code 2.1.297 自動接續", i => i.NoticeKind = AgentNoticeKind.CliUpdate);
        b.Add("notice-unknown-kind", AgentItemKind.Notice, AgentItemStatus.Done, "新版的提示種類", null, p => p["noticeKind"] = "FutureKind");
        b.Add("error", AgentItemKind.Error, AgentItemStatus.Done, "Claude 程序意外結束（結束碼 1）：`ECONNRESET`");
        b.Add("assistant-media", AgentItemKind.Assistant, AgentItemStatus.Done, "產出的圖與影片：", i =>
        {
            i.MediaPaths = [WorkingDirectory + @"\out\圖表.png", WorkingDirectory + @"\out\示範.mp4", WorkingDirectory + @"\out\沒有參照.png"];
            i.MediaRefs =
            [
                new AgentMediaRef { Ref = Ref(11), MediaPath = WorkingDirectory + @"\out\圖表.png" },
                new AgentMediaRef { Ref = Ref(12), MediaPath = WorkingDirectory + @"\out\示範.mp4" },
            ];
        });
        b.Add("tool-with-card-fields", AgentItemKind.Tool, AgentItemStatus.Done, string.Empty, i =>
        {
            Card(i, "bash", "執行指令", "ls");
            i.RequestDigest = Digest;
            i.Risk = AgentRemoteRisk.High;
        });
        b.Add("no-created-at", AgentItemKind.Notice, AgentItemStatus.Done, "沒有建立時間的項目", null, p => p.Remove("createdAt"));
        b.Add("created-at-overflow", AgentItemKind.Notice, AgentItemStatus.Done, "轉換不了的建立時間", null, p => p["createdAt"] = long.MaxValue);
        b.Add("device-extra-keys", AgentItemKind.User, AgentItemStatus.Done, "裝置多送了欄位", i =>
        {
            i.Attachments = [Attachment("att-extra", "多送.png", AgentAttachmentKind.Image, 4096)];
            i.MediaPaths = [WorkingDirectory + @"\out\多送.png"];
            i.MediaRefs =
            [
                new AgentMediaRef { Ref = Ref(21), AttachmentId = "att-extra" },
                new AgentMediaRef { Ref = Ref(22), MediaPath = WorkingDirectory + @"\out\多送.png" },
            ];
        }, p =>
        {
            p["html"] = DeviceMark + "-html<img src=x onerror=alert(1)>";
            p["detailHtml"] = DeviceMark + "-detailHtml";
            p["url"] = "https://evil.example/" + DeviceMark + "-url";
            p["thumbUrl"] = "https://evil.example/" + DeviceMark + "-thumbUrl";
            var attachment = (JObject)p["attachments"]![0]!;
            attachment["url"] = DeviceMark + "-att-url";
            attachment["thumbUrl"] = DeviceMark + "-att-thumbUrl";
            attachment["html"] = DeviceMark + "-att-html";
            attachment["videoInfo"] = DeviceMark + "-att-videoInfo";
            var media = (JObject)p["media"]![0]!;
            media["url"] = DeviceMark + "-media-url";
            media["thumbUrl"] = DeviceMark + "-media-thumbUrl";
            media["html"] = DeviceMark + "-media-html";
        });
        b.Add("user-html", AgentItemKind.User, AgentItemStatus.Done, "<script>alert(1)</script>\r\n第二行 **不是粗體** &amp; [不是連結](javascript:alert(1))");
        b.Add("blob-text", AgentItemKind.Assistant, AgentItemStatus.Done, string.Empty, null, p => b.ToBlob(p, "text", new JValue("blob 換回的 **Markdown** 內容：`ok`")));
        b.Add("blob-diff", AgentItemKind.Tool, AgentItemStatus.Done, string.Empty, i =>
        {
            Card(i, "edit", "修改檔案", @"src\c.cs");
            i.FilePath = WorkingDirectory + @"\src\c.cs";
        }, p => b.ToBlob(p, "diff", new JArray(new JObject { ["t"] = "del", ["s"] = "舊的一行" }, new JObject { ["t"] = "add", ["s"] = "新的一行" })));
        b.Add("blob-missing", AgentItemKind.Assistant, AgentItemStatus.Done, string.Empty, null, p => p["text"] = new JObject
        {
            ["blob"] = new string('e', 64),
            ["length"] = 123,
        });

        // Grok 與 Antigravity 的對話（設計 2026-10-22-grok-antigravity-sync 第 10 節第 4 項；形狀同桌面語料 grok、antigravity，ProviderCorpusShapeTests 以實錄核對過）
        b.Add("grok-search", AgentItemKind.Tool, AgentItemStatus.Done, string.Empty, i =>
        {
            Card(i, "search", "搜尋內容", $"TODO{(char)0x3000}src");
            i.Detail = "{\n  \"pattern\": \"TODO\",\n  \"path\": \"src\"\n}";
            i.Output = @"src\a.cs:3:// TODO 補測試" + "\n";
        });
        b.Add("grok-approval-exec", AgentItemKind.Approval, AgentItemStatus.Waiting, string.Empty, i =>
        {
            Card(i, "bash", "執行指令", "Write-Output hi");
            i.Detail = "Print hi\n\nWrite-Output hi";
            i.AllowSessionLabel = "本對話的指令都不再詢問";
            i.RequestDigest = Digest;
            i.Risk = AgentRemoteRisk.Normal;
        });
        b.Add("grok-plan-approval", AgentItemKind.PlanApproval, AgentItemStatus.Waiting, string.Empty, i =>
        {
            Card(i, "plan", "計畫", "照這個計畫執行？");
            i.CanAllowForSession = false;
            i.Detail = "# 計畫\n\n1. 改 `a.cs` 的逾時\n2. 補測試";
            i.RequestDigest = Digest;
            i.Risk = AgentRemoteRisk.High;
        });
        b.Add("grok-question", AgentItemKind.Question, AgentItemStatus.Waiting, string.Empty, i =>
        {
            i.Questions = [Question("你偏好哪個顏色？", null, "你偏好哪個顏色？", false, "藍色", "紅色")];
            i.Questions[0].AllowOther = true;
            i.RequestDigest = Digest;
        });
        b.Add("agy-write", AgentItemKind.Tool, AgentItemStatus.Done, string.Empty, i =>
        {
            Card(i, "write", "寫入檔案", @"C:\w\a.txt");
            i.FilePath = @"C:\w\a.txt";
            i.Detail = "{\n  \"CodeContent\": \"hello\"\n}";
        });
        b.Add("agy-command-aborted", AgentItemKind.Tool, AgentItemStatus.Cancelled, string.Empty, i =>
        {
            Card(i, "bash", "執行指令", "npm run dev");
            i.Detail = "npm run dev";
            i.Output = "agy 程序已結束，這個工具被中斷，結果未知";
        });
        b.Add("agy-notice-denied", AgentItemKind.Notice, AgentItemStatus.Done,
            "agy 自動拒絕了需要核准的操作（RunCommand）：Antigravity 在這裡無法於對話中詢問。要讓代理執行指令，請把權限模式改成「完全自動」。");
        return new HubCorpus(b.Cases, b.Blobs);
    }

    private static void Card(AgentChatItem item, string toolKind, string toolName, string title)
    {
        item.ToolKind = toolKind;
        item.ToolName = toolName;
        item.Title = title;
    }

    private static AgentAttachment Attachment(string id, string name, AgentAttachmentKind kind, long size, int frames = 0, double? duration = null) => new()
    {
        Id = id,
        FileName = name,
        StoredPath = @"C:\Users\Lin\AppData\Local\Myriagent\conversations\c1\attachments\" + name,
        Kind = kind,
        SizeBytes = size,
        ModelImagePaths = Enumerable.Range(1, frames).Select(n => $@"C:\Users\Lin\AppData\Local\Myriagent\conversations\c1\attachments\.model\{n}.jpg").ToList(),
        DurationSeconds = duration,
    };

    private static AgentQuestion Question(string id, string? header, string text, bool multi, params string[] options) => new()
    {
        Id = id,
        Header = header,
        Text = text,
        MultiSelect = multi,
        Options = options.Select(o => new AgentQuestionOption { Label = o }).ToList(),
    };

    private sealed class Builder
    {
        public List<HubItemCase> Cases { get; } = [];

        public Dictionary<string, byte[]> Blobs { get; } = new(StringComparer.Ordinal);

        public void Add(string name, AgentItemKind kind, AgentItemStatus status, string text, Action<AgentChatItem>? setup = null, Action<JObject>? wire = null)
        {
            var n = Cases.Count + 1;
            var time = Start.AddMinutes(n);
            var item = new AgentChatItem
            {
                Id = $"hub-{n:00}-{name}",
                Kind = kind,
                Status = status,
                Text = text,
                CreatedAt = time,
                UpdatedAt = time,
                Ord = 100 + n,
            };
            setup?.Invoke(item);
            if (!ConversationWire.TryItem(item, 1, out var payload))
                throw new InvalidOperationException($"語料 {name} 產生不出 conv.item");
            wire?.Invoke(payload);
            Cases.Add(new HubItemCase(name, item.Ord!.Value, payload));
        }

        /// <summary>把 payload 的一欄換成 blob 參照，內容放進假的 blob 儲存</summary>
        public void ToBlob(JObject payload, string field, JToken value)
        {
            var content = Encoding.UTF8.GetBytes(value is JValue { Type: JTokenType.String } text ? (string)text! : ConversationWire.Serialize(value));
            var sha = Convert.ToHexStringLower(SHA256.HashData(content));
            Blobs[sha] = content;
            payload[field] = new JObject { ["blob"] = sha, ["length"] = content.Length };
        }
    }
}

/// <summary>語料與它用到的 blob 內容</summary>
internal sealed record HubCorpus(IReadOnlyList<HubItemCase> Cases, IReadOnlyDictionary<string, byte[]> Blobs)
{
    public HubItemCase Get(string name) => Cases.Single(c => c.Name == name);
}

/// <summary>測試用的 blob 儲存：只讀，內容由測試給</summary>
internal sealed class FakeBlobStore(IReadOnlyDictionary<string, byte[]> contents) : IBlobStore
{
    private int reads;

    public int Reads => Volatile.Read(ref reads);

    public bool Exists(string sha256) => contents.ContainsKey(sha256);

    public Stream? OpenRead(string sha256)
    {
        Interlocked.Increment(ref reads);
        return contents.TryGetValue(sha256, out var content) ? new MemoryStream(content, writable: false) : null;
    }

    public Task<bool> PutAsync(string sha256, long length, string kind, string? contentType, Stream content, CancellationToken cancellationToken)
        => throw new NotSupportedException();
}

/// <summary>WP4.7 形狀的網址（測試用）：有參照就給 <c>/m/{ref}</c>、<c>/t/{ref}?s=320</c>、<c>/f/{ref}</c>；參照一定要是擁有者裝置發的</summary>
internal sealed class WithMediaUrls : HubMediaUrls
{
    public override string? ForRef(string deviceId, string? mediaRef, string purpose, int? thumbSize = null)
    {
        if (deviceId != HubItemCorpus.Owner)
            throw new InvalidOperationException("參照要以擁有者裝置解析：" + deviceId);
        if (mediaRef is null)
            return null;
        return purpose switch
        {
            PurposeMedia => "/m/" + mediaRef,
            PurposeThumb => "/t/" + mediaRef + "?s=" + thumbSize,
            PurposeFile => "/f/" + mediaRef,
            _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, null),
        };
    }
}
