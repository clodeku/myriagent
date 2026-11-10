using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Myriagent.Agents;
using Myriagent.Rendering;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>
/// 對話事件的線上投影（設計 14.11 D31、D32；docs/sync-protocol.md 14.2）：同步專用，與網頁用的 ItemRenderer 分開（黃金檔不變）。
/// 擁有者裝置發布前以這裡把桌面模型轉成 payload，再經 <see cref="ConversationEventRules.TryFit"/> 把太大的欄位換成 blob、
/// <see cref="ConversationEventRules.Validate(string?, string?)"/> 檢查（伺服器收到時呼叫同一份）。純函式、不碰檔案系統。
/// 呼叫端（WP4.3）負責先遮蔽項目的權杖（<see cref="CredentialLeakRules.MaskItem"/>，D43）、只發布 <see cref="ConversationSyncPolicies.Publishes"/> 允許的種類；
/// 中繼資料（標題、模型、強度、工作資料夾）與佇列的顯示文字在這裡遮蔽（<see cref="Meta"/>、<see cref="Queue"/>：metadata、paused 的對話也發 meta 類，
/// DLP 命中而暫停的對話不能反而把權杖寫進全域的對話清單）。
/// </summary>
internal static class ConversationWire
{
    /// <summary>秘密題目（Codex isSecret）的答案在線上一律是這個字（中樞本來就只存這個，這裡再擋一次）</summary>
    public const string HiddenAnswer = "（已隱藏）";

    private static readonly JsonSerializer Serializer = JsonSerializer.Create(SyncJson.Settings);

    /// <summary>對話事件的 epoch：對話的 LeaseEpoch，沒有時 1（第 4 階段沒有租約；第 5 階段每次換手＋1，接手端寫進 meta）</summary>
    public static long EpochOf(AgentConversation meta) => meta.LeaseEpoch ?? ConversationIds.DefaultEpoch;

    /// <summary>payload 的線上原文（不縮排；物件照 SyncJson、JObject 照原本的鍵順序）</summary>
    public static string Serialize(object payload)
        => payload is JToken token ? token.ToString(Formatting.None) : SyncJson.Serialize(payload);

    /// <summary>
    /// <c>conv.created</c>／<c>conv.meta</c> 的 payload：完整的中繼資料。標題、模型、強度、工作資料夾先經權杖 DLP 遮蔽（標題取自第一則訊息、
    /// 可能是截斷過的：<see cref="CredentialLeakRules.MaskCut"/>）；標題、模型、強度過長時截斷，太長的工作資料夾路徑不送
    /// （<c>customFolder</c> 照樣是 true），所以任何對話都產生得出合格的 payload
    /// </summary>
    /// <param name="providerWireName">線上的供應商名稱（桌面 <c>SyncProviderRules.TryWireName</c>）</param>
    /// <param name="running">執行中</param>
    /// <param name="waiting">有等待使用者回應的卡片</param>
    /// <param name="models">這個對話的帳戶在這台可選的模型（<see cref="ModelOptions"/> 整理；null 或整理完沒有任何一項時不送 <c>models</c>）</param>
    /// <param name="transcript">
    /// 第 5 階段：逐字稿的狀態（<see cref="TranscriptStates.MetaValues"/>；WP5.3 才傳）。null 或不是這三種的不送
    /// </param>
    /// <param name="workingDirectoryRef">
    /// 第 5 階段：自訂工作資料夾的跨電腦參照（WP5.4 才傳，取 <see cref="AgentConversation.WorkingDirectoryRef"/>）。只在自訂資料夾時送；
    /// sub、display 先經權杖 DLP 遮蔽並截斷，repoKey 遮蔽（遮蔽過的鍵在別台對應不到，只會要使用者選資料夾）、太長或種類不合格就整個不送
    /// </param>
    public static ConversationMetaPayload Meta(AgentConversation meta, string providerWireName, bool running, bool waiting,
        IReadOnlyList<AgentModelOption>? models = null, string? transcript = null, PathRef? workingDirectoryRef = null)
    {
        var payload = MetaWithoutModels(meta, providerWireName, running, waiting, transcript, workingDirectoryRef) with { Models = ModelOptions(models) };
        // 模型清單是附帶的：整份超過上限時從後面少帶（其他欄位都有長度上限，沒有清單時一定放得下）
        while (payload.Models is { Count: > 0 } list && Encoding.UTF8.GetByteCount(Serialize(payload)) > ConversationEventRules.MaxPayloadBytes)
            payload = payload with { Models = list.Count > 1 ? list.Take(list.Count - 1).ToList() : null };
        return payload;
    }

    /// <summary>
    /// meta 的 <c>models</c>：value 是空的、超過短欄位上限（截斷了就不是能送回 setOption 的值）或遮蔽後會變的不帶，同一個 value 只帶第一個；
    /// label、說明遮蔽後截斷（label 空的用 value），推理強度只帶合格、不重複的前 <see cref="ConversationEventRules.MaxModelEfforts"/> 個（沒有時省略）；
    /// 最多 <see cref="ConversationEventRules.MaxModelOptions"/> 項，一項都沒有時 null。結果一定通過 <see cref="ConversationEventRules.Validate(string?, string?)"/>
    /// </summary>
    public static List<ConversationModelOption>? ModelOptions(IReadOnlyList<AgentModelOption>? models)
    {
        if (models is null)
            return null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ConversationModelOption>();
        foreach (var m in models)
        {
            if (result.Count >= ConversationEventRules.MaxModelOptions)
                break;
            if (m?.Value is not { Length: > 0 and <= ConversationEventRules.MaxShortTextLength } value || CredentialLeakRules.Mask(value) != value || !seen.Add(value))
                continue;
            var description = string.IsNullOrEmpty(m.Description) ? null : ClampMasked(m.Description, ConversationEventRules.MaxModelDescriptionLength);
            var efforts = (m.Efforts ?? Array.Empty<string>())
                .Where(e => e is { Length: > 0 and <= ConversationEventRules.MaxShortTextLength } && CredentialLeakRules.Mask(e) == e)
                .Distinct(StringComparer.Ordinal)
                .Take(ConversationEventRules.MaxModelEfforts)
                .ToList();
            result.Add(new ConversationModelOption(value, Short(CredentialLeakRules.Mask(m.Label)) ?? value, string.IsNullOrEmpty(description) ? null : description,
                efforts.Count > 0 ? efforts : null));
        }

        return result.Count > 0 ? result : null;
    }

    private static ConversationMetaPayload MetaWithoutModels(AgentConversation meta, string providerWireName, bool running, bool waiting,
        string? transcript, PathRef? workingDirectoryRef)
    {
        var custom = !string.IsNullOrWhiteSpace(meta.WorkingDirectory);
        var folder = custom ? CredentialLeakRules.Mask(meta.WorkingDirectory!) : null;
        return new ConversationMetaPayload(
            Provider: providerWireName,
            LocalAccountId: ConversationEventRules.IsLocalAccountId(meta.AccountId) ? meta.AccountId : null,
            Title: ClampMasked(meta.Title ?? string.Empty, ConversationEventRules.MaxTitleLength),
            PermissionMode: Enum.IsDefined(meta.PermissionMode) ? meta.PermissionMode.ToString() : null,
            Model: Short(CredentialLeakRules.Mask(meta.Model)),
            Effort: Short(CredentialLeakRules.Mask(meta.Effort)),
            CustomFolder: custom,
            WorkingDirectory: folder is { Length: <= ConversationEventRules.MaxPathLength } ? folder : null,
            SyncPolicy: ConversationSyncPolicies.Interpret(meta.SyncPolicy),
            CreatedAt: ProtocolValues.ToUnixMs(meta.CreatedAt),
            UpdatedAt: ProtocolValues.ToUnixMs(meta.UpdatedAt),
            Running: running,
            Waiting: waiting,
            Transcript: TranscriptStates.MetaValues.Contains(transcript ?? string.Empty, StringComparer.Ordinal) ? transcript : null,
            WorkingDirectoryRef: custom ? WirePathRef(workingDirectoryRef) : null,
            Ephemeral: meta.Ephemeral == true ? true : null);
    }

    /// <summary>資料夾參照的線上投影（遮蔽、截斷；不合格回 null＝不送）</summary>
    private static PathRef? WirePathRef(PathRef? reference)
    {
        if (reference is null || !PathRefKinds.IsKnown(reference.Kind))
            return null;
        var repoKey = string.IsNullOrEmpty(reference.RepoKey) ? null : CredentialLeakRules.Mask(reference.RepoKey);
        if (repoKey is { Length: > ConversationEventRules.MaxRepoKeyLength } || (reference.Kind == PathRefKinds.Git && repoKey is null))
            return null;
        var sub = string.IsNullOrEmpty(reference.Sub) ? null : CredentialLeakRules.Mask(reference.Sub);
        var display = string.IsNullOrEmpty(reference.Display) ? null : ClampMasked(reference.Display, ConversationEventRules.MaxTitleLength);
        return new PathRef(reference.Kind, repoKey, sub is { Length: <= ConversationEventRules.MaxPathLength } ? sub : null, Empty(display));
    }

    /// <summary>
    /// 讀回 <c>conv.lease</c>（第 5 階段；WP5.7 的接手端、WP5.5 寫之前的自檢）：先照 <see cref="ConversationEventRules.Validate(string?, string?)"/> 的外形
    /// 檢查（呼叫端先做），再把 <see cref="ConversationEventRules.LeaseBlobFields"/> 的 blob 參照換回內容（雜湊、長度、UTF-8、只收一種解讀的陣列，
    /// 同 <see cref="TryReadItem"/>）、整份以不收 blob 的形狀再驗一次，最後轉成 <see cref="ConversationLeasePayload"/>。任何一步不對回 false；不改 <paramref name="payload"/>
    /// </summary>
    public static bool TryReadLease(JObject payload, Func<string, byte[]?> blobContent, [NotNullWhen(true)] out ConversationLeasePayload? lease)
    {
        lease = null;
        var resolved = (JObject)payload.DeepClone();
        foreach (var name in ConversationEventRules.LeaseBlobFields)
        {
            if (!ConversationEventRules.TryReadBlobRef(resolved[name], out var blobRef))
                continue;
            if (!TryResolveBlob(blobRef, blobContent, out var text) || !ConversationEventRules.TryParseStrict(text, out var token) || token is not JArray array)
                return false;
            resolved[name] = array;
        }

        if (ConversationEventRules.CheckLeaseShape(resolved, allowBlobs: false) != null)
            return false;
        try
        {
            lease = resolved.ToObject<ConversationLeasePayload>(Serializer);
        }
        catch (JsonException)
        {
            return false;
        }

        return lease != null;
    }

    private static bool TryResolveBlob(ConversationBlobRef blobRef, Func<string, byte[]?> blobContent, [NotNullWhen(true)] out string? text)
    {
        text = null;
        var content = blobContent(blobRef.Blob);
        if (content == null || content.LongLength != blobRef.Length || Convert.ToHexStringLower(SHA256.HashData(content)) != blobRef.Blob)
            return false;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// <c>conv.queue</c> 的 payload：最多 <see cref="ConversationEventRules.MaxQueueEntries"/> 則、每則的文字截到
    /// <see cref="ConversationEventRules.MaxQueueTextLength"/> 字；整份仍超過上限時從後面少帶幾則（<c>more</c> 是沒帶的則數）。
    /// id 不合格的那則不帶（也算進 more）。文字先經權杖 DLP 遮蔽（使用者貼上的權杖；同一段文字成為項目之後由 MaskItem 遮蔽，佇列裡也要），
    /// 截斷處尾端的權杖字元去掉（<see cref="CredentialLeakRules.MaskCut"/>）
    /// </summary>
    /// <param name="entries">佇列的順序；文字是給人看的（秘密回答已遮蔽的 DisplayText，沒有時是原文）</param>
    public static ConversationQueuePayload Queue(IEnumerable<(string Id, string DisplayText)> entries)
    {
        var all = entries.ToList();
        var kept = all
            .Where(e => ConversationIds.IsItemId(e.Id))
            .Take(ConversationEventRules.MaxQueueEntries)
            .Select(e => new ConversationQueueEntry(e.Id, ClampMasked(e.DisplayText ?? string.Empty, ConversationEventRules.MaxQueueTextLength)))
            .ToList();
        while (true)
        {
            var payload = new ConversationQueuePayload(kept, all.Count - kept.Count);
            if (kept.Count == 0 || Encoding.UTF8.GetByteCount(Serialize(payload)) <= ConversationEventRules.MaxPayloadBytes)
                return payload;
            kept.RemoveAt(kept.Count - 1);
        }
    }

    /// <summary>
    /// 一筆項目 → <c>conv.item</c> 的 payload（還沒換 blob；接著呼叫 <see cref="ConversationEventRules.TryFit"/>）。
    /// 不能上線的項目回 false：還沒配 Ord（OrdRules，WP4.1 步 4）、id 不合格、種類或狀態是沒定義的數值（舊版讀到新版的存檔）。
    /// 附件與媒體只帶這台發的參照（<see cref="AgentChatItem.MediaRefs"/>，還沒發的沒有 ref），不帶任何本機路徑。
    /// 存檔裡的病態內容照樣產生得出合格的 payload：清單是 null 或裡面有 null 的略過、字串是 null 的當成空字串、
    /// 差異行的種類不認得的當成 ctx、附件 id 不合格的那個附件不帶、附件種類是沒定義的數值當成 Document
    /// </summary>
    /// <param name="ver">擁有者對這筆的更新次數（≥1）</param>
    public static bool TryItem(AgentChatItem item, long ver, [NotNullWhen(true)] out JObject? wire)
    {
        wire = null;
        if (item.Ord is not >= 1 || ver < 1 || !ConversationIds.IsItemId(item.Id) || !Enum.IsDefined(item.Kind) || !Enum.IsDefined(item.Status))
            return false;

        var refs = (item.MediaRefs ?? new List<AgentMediaRef>()).Where(r => r != null && ConversationEventRules.IsMediaRef(r.Ref)).ToList();
        var questions = (item.Questions ?? new List<AgentQuestion>()).Where(q => q != null).ToList();
        var secret = questions.Where(q => q.IsSecret).Select(q => q.Id ?? string.Empty).ToHashSet(StringComparer.Ordinal);
        var projected = new WireItem(
            Id: item.Id,
            Ord: item.Ord.Value,
            Ver: ver,
            Kind: item.Kind.ToString(),
            Status: item.Status.ToString(),
            CreatedAt: ProtocolValues.ToUnixMs(item.CreatedAt),
            UpdatedAt: ProtocolValues.ToUnixMs(item.UpdatedAt),
            Text: Empty(item.Text),
            ToolName: Empty(item.ToolName),
            ToolKind: Short(item.ToolKind),
            Title: Empty(item.Title),
            Detail: Empty(item.Detail),
            Output: Empty(item.Output),
            Diff: NullIfEmpty(item.Diff?.Where(d => d != null).Select(DiffLine)),
            FilePath: Empty(item.FilePath),
            Attachments: NullIfEmpty(item.Attachments?.Where(a => a != null && ConversationIds.IsItemId(a.Id)).Select(a => Attachment(a, refs))),
            Questions: NullIfEmpty(questions.Select(Question)),
            Answers: NullIfEmpty(item.Answers?.Where(a => a.Value != null).Select(a => new WireAnswer(
                a.Key, secret.Contains(a.Key) ? [HiddenAnswer] : a.Value.Where(v => v != null).ToList()))),
            Decision: Short(item.Decision),
            CanAllowForSession: item.CanAllowForSession,
            AllowSessionLabel: Empty(item.AllowSessionLabel),
            AnswerAsMessage: item.AnswerAsMessage,
            Media: NullIfEmpty(item.MediaPaths?.Where(path => path != null).Select(path => Media(path, refs))),
            Plan: NullIfEmpty(item.PlanSteps?.Where(s => s != null).Select(s => new WirePlanStep(s.Text ?? string.Empty, Short(s.Status) is { Length: <= 32 } status ? status : "pending"))),
            NoticeKind: item.NoticeKind is { } notice && Enum.IsDefined(notice) ? notice.ToString() : null,
            RequestDigest: Short(item.RequestDigest),
            Risk: item.Risk is { } risk && Enum.IsDefined(risk) ? risk.ToString() : null,
            Author: CollaboratorRules.CleanAuthor(item.Author),
            ResolvedBy: CollaboratorRules.CleanAuthor(item.ResolvedBy));
        wire = JObject.FromObject(projected, Serializer);
        return true;
    }

    /// <summary>
    /// 讀回 <c>conv.item</c>：先把 blob 參照換回內容（<paramref name="blobContent"/> 以 SHA-256 取 UTF-8 內容，拿不到回 null），
    /// 內容的雜湊或長度不符、拿不到、或格式不對都回 false。換回之後整筆再照共用驗證的形狀檢查一次
    /// （<see cref="ConversationEventRules.CheckItemShape"/>：不准上線的欄位、識別碼與列舉；不含大小上限——換回之後本來就超過內嵌上限）：
    /// blob 的內容不在 payload 的驗證範圍內，不能讓它繞過。只讀、不改 <paramref name="payload"/>
    /// </summary>
    public static bool TryReadItem(JObject payload, Func<string, byte[]?> blobContent, [NotNullWhen(true)] out WireItem? item)
    {
        item = null;
        var resolved = (JObject)payload.DeepClone();
        foreach (var name in ConversationEventRules.BlobFields)
        {
            if (!ConversationEventRules.TryReadBlobRef(resolved[name], out var blobRef))
                continue;
            var content = blobContent(blobRef.Blob);
            if (content == null || content.LongLength != blobRef.Length || Convert.ToHexStringLower(SHA256.HashData(content)) != blobRef.Blob)
                return false;
            string text;
            try
            {
                text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content);
            }
            catch (DecoderFallbackException)
            {
                return false;
            }

            if (ConversationEventRules.ArrayFields.Contains(name))
            {
                if (!ConversationEventRules.TryParseStrict(text, out var token) || token is not JArray array)
                    return false;
                resolved[name] = array;
            }
            else
            {
                resolved[name] = text;
            }
        }

        if (ConversationEventRules.CheckItemShape(resolved) != null)
            return false;

        try
        {
            item = resolved.ToObject<WireItem>(Serializer);
        }
        catch (JsonException)
        {
            return false;
        }

        return item != null;
    }

    /// <summary>
    /// 遮蔽後截到 <paramref name="max"/> 字：原本就截斷過的（標題的「…」）與這裡截斷的，截斷處尾端的權杖字元都先去掉再遮蔽
    /// （<see cref="CredentialLeakRules.MaskCut"/>）。遮蔽的文字本身不會再被比對到，所以第二次只處理這裡的截斷
    /// </summary>
    public static string ClampMasked(string text, int max)
    {
        var masked = CredentialLeakRules.MaskCut(text);
        if (masked.Length <= max)
            return masked;
        var cut = CredentialLeakRules.MaskCut(Clamp(masked, max));
        return cut.Length <= max ? cut : Clamp(cut, max);
    }

    /// <summary>截到 <paramref name="max"/> 字（含結尾的「…」）；不切開代理字元對</summary>
    public static string Clamp(string text, int max)
    {
        if (text.Length <= max)
            return text;
        var cut = max - 1;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
            cut--;
        return text[..cut] + "…";
    }

    private static AgentDiffLine DiffLine(AgentDiffLine d)
        => new(d.T is "add" or "del" or "ctx" or "hunk" ? d.T : "ctx", d.S ?? string.Empty);

    private static WireAttachment Attachment(AgentAttachment a, List<AgentMediaRef> refs) => new(
        Id: a.Id,
        Name: Clamp(a.FileName ?? string.Empty, ConversationEventRules.MaxPathLength),
        Kind: Enum.IsDefined(a.Kind) ? a.Kind.ToString() : AgentAttachmentKind.Document.ToString(),
        Size: Math.Max(0, a.SizeBytes),
        Frames: a.Kind == AgentAttachmentKind.Video ? a.ModelImagePaths?.Count ?? 0 : null,
        Duration: a.DurationSeconds is >= 0 and <= double.MaxValue ? a.DurationSeconds : null,
        Ref: refs.FirstOrDefault(r => r.AttachmentId == a.Id)?.Ref);

    private static WireMedia Media(string path, List<AgentMediaRef> refs) => new(
        Ref: refs.FirstOrDefault(r => r.MediaPath == path)?.Ref,
        Kind: AgentAttachmentRules.IsPlayableVideo(path) ? "video" : "image",
        Name: Clamp(FileName(path), ConversationEventRules.MaxPathLength));

    private static WireQuestion Question(AgentQuestion q) => new(
        q.Id ?? string.Empty,
        Empty(q.Header),
        q.Text ?? string.Empty,
        q.MultiSelect,
        q.AllowOther,
        q.IsSecret,
        (q.Options ?? new List<AgentQuestionOption>()).Where(o => o != null).Select(o => new WireQuestionOption(o.Label ?? string.Empty, Empty(o.Description), Empty(o.Preview))).ToList());

    private static List<T>? NullIfEmpty<T>(IEnumerable<T>? values) => values?.ToList() is { Count: > 0 } list ? list : null;

    /// <summary>路徑的檔名（不經 Path：伺服器在 Linux 也要切 Windows 的反斜線）</summary>
    private static string FileName(string path)
    {
        var index = path.LastIndexOfAny(['\\', '/']);
        return index < 0 ? path : path[(index + 1)..];
    }

    private static string? Empty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>短欄位（工具種類、決定、模型、強度、摘要）超過上限就截斷（不切開代理字元對）：任何項目都產生得出合格的 payload</summary>
    private static string? Short(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        var max = ConversationEventRules.MaxShortTextLength;
        if (value.Length <= max)
            return value;
        return value[..(char.IsHighSurrogate(value[max - 1]) ? max - 1 : max)];
    }
}
